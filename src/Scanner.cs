using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace DriveScope
{
    public static class Catalog
    {
        public static readonly string[] Names = { "Unity / VRChat", "アプリ", "ゲーム", "キャッシュ・開発依存", "動画", "音楽・音声", "画像", "3D・制作データ", "圧縮・インストーラー", "ドキュメント・コード", "Windows・システム", "その他", "AIモデル" };
        public static readonly string[] Colors = { "#B49BFF", "#789BFF", "#FBAD72", "#EDC969", "#70B9FF", "#F18CB8", "#75D2B6", "#B0C4E9", "#C9A986", "#8BCED6", "#8996AF", "#A4ABBA", "#D9A0F1" };
        static readonly HashSet<string> Video = Set(".mp4 .mkv .mov .avi .webm .wmv .m4v .mts .m2ts .vob .flv .ts");
        static readonly HashSet<string> Audio = Set(".wav .mp3 .flac .aiff .aif .ogg .opus .m4a .aac .wma .mid .midi .dsf .dff");
        static readonly HashSet<string> Images = Set(".png .jpg .jpeg .webp .gif .bmp .tif .tiff .heic .avif .exr .hdr .tga .dds .ico .svg .raw .cr2 .nef");
        static readonly HashSet<string> Art = Set(".blend .fbx .obj .gltf .glb .usd .usdz .stl .3ds .max .c4d .psd .psb .ai .afdesign .afphoto .spp .sbsar .als .flp .rpp .prproj .aep .veg");
        static readonly HashSet<string> Archives = Set(".zip .7z .rar .tar .gz .xz .bz2 .iso .img .msi .msix .appx .cab .dmg .vhd .vhdx");
        static readonly HashSet<string> Documents = Set(".pdf .doc .docx .xls .xlsx .ppt .pptx .txt .md .csv .json .jsonl .ndjson .xml .yaml .yml .html .css .js .ts .tsx .jsx .cs .py .cpp .c .h .hpp .sql .log .ini .toml .config .ipynb .epub .po .mo .lock .sh .bat .ps1");
        static readonly HashSet<string> Unity = Set(".unity .unitypackage .prefab .asset .vrca .vrcw .vpm .shader .shadergraph .shadersubgraph .mat .controller .anim .meta");
        static readonly HashSet<string> Applications = Set(".exe .dll .sys .pdb .pak .dat .bin .bundle .resources .so .jar");
        static readonly HashSet<string> Models = Set(".gguf .ggml .safetensors .ckpt .pt .pth .onnx .tflite");
        static HashSet<string> Set(string s) { return new HashSet<string>(s.Split(' '), StringComparer.OrdinalIgnoreCase); }
        public static int Classify(string path, int context)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            if (name == "pagefile.sys" || name == "hiberfil.sys" || name == "swapfile.sys") return 10;
            if (context >= 0) return context;
            string ext = Path.GetExtension(path);
            if (Unity.Contains(ext)) return 0;
            if (Models.Contains(ext)) return 12;
            if (Video.Contains(ext)) return 4;
            if (Audio.Contains(ext)) return 5;
            if (Images.Contains(ext)) return 6;
            if (Art.Contains(ext)) return 7;
            if (Archives.Contains(ext)) return 8;
            if (Documents.Contains(ext)) return 9;
            if (Applications.Contains(ext)) return 1;
            return 11;
        }
        public static int Context(string path, int inherited, bool unityProject)
        {
            string n = Path.GetFileName(path.TrimEnd('\\')).ToLowerInvariant();
            string p = path.ToLowerInvariant().Replace('/', '\\');
            if (inherited == 10) return 10;
            if (p.Length >= 3 && p.Substring(3).Split('\\').Length == 1 && (n == "windows" || n == "$recycle.bin" || n == "system volume information" || n == "recovery" || n.StartsWith("$windows"))) return 10;
            if (inherited == 3) return 3;
            if (n == "cache" || n == "caches" || n == "temp" || n == "tmp" || n == "node_modules" || n == ".git" || n == ".cache" || n == "npm-cache" || n == "cache-windowsplayer" || n == "shadercache" || n == "gpucache" || n == "dxcache" || n == "glcache" || n == "code cache" || n == "deriveddatacache" || n == "pip" || n == ".npm" || n == "packagecache" || n == "gicache") return 3;
            if ((n == "library" && inherited == 0) || (n.StartsWith("library", StringComparison.Ordinal) && (File.Exists(Path.Combine(path, "ArtifactDB")) || File.Exists(Path.Combine(path, "SourceAssetDB"))))) return 3;
            if (inherited == 12 || (n == "models" && (p.Contains("\\.ollama\\") || p.Contains("\\ai\\")))) return 12;
            if (unityProject || inherited == 0) return 0;
            if (inherited == 2 || n == "steamapps" || n == "epic games" || n == "xboxgames" || n == "riot games" || n == "gog games") return 2;
            if (inherited == 1 || n == "program files" || n == "program files (x86)" || n == "windowsapps") return 1;
            return -1;
        }
        public static string Size(long value)
        {
            double n = Math.Max(0, value); string[] units = { "B", "KB", "MB", "GB", "TB", "PB" }; int u = 0;
            while (n >= 1024 && u < units.Length - 1) { n /= 1024; u++; }
            return n.ToString(u == 0 ? "N0" : "N1") + " " + units[u];
        }
    }
    public sealed class FileRecord
    {
        public string Path; public long Bytes; public int Category;
    }
    public sealed class FolderRecord
    {
        public string Path; public string Parent; public long[] Bytes = new long[Catalog.Names.Length];
        public long Total { get { return Bytes.Sum(); } }
    }
    public sealed class ScanState
    {
        public readonly object Gate = new object();
        public string Root, Label, CurrentPath = "", Status = "未スキャン", VolumeIdentity = "", Kind = "ドライブ";
        public bool Present = true, Ready = true, Included = true, LoadingCache;
        public long Capacity, Free, Files, Skipped, Links;
        public long[] Bytes = new long[Catalog.Names.Length];
        public long[] Counts = new long[Catalog.Names.Length];
        public List<FileRecord>[] Largest = Enumerable.Range(0, Catalog.Names.Length).Select(x => new List<FileRecord>()).ToArray();
        public Dictionary<string, FolderRecord> Folders = new Dictionary<string, FolderRecord>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<FolderRecord>> childFolders = new Dictionary<string, List<FolderRecord>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, FolderRecord> indexedFolders;
        int indexedFolderCount = -1;
        public List<string> Issues = new List<string>();
        public bool Running, Saving, Complete, Cached;
        public DateTime Started, Finished;
        public CancellationTokenSource Cancel;
        public long Total { get { return Bytes.Sum(); } }
        public ScanState(string root, bool refresh = true)
        {
            Root = Path.GetFullPath(root);
            if (Root.Length > Path.GetPathRoot(Root).Length) Root = Root.TrimEnd('\\');
            Label = Root; if (refresh) RefreshDrive();
        }
        public void RefreshDrive()
        {
            try { var d = new DriveInfo(Root); if (d.IsReady) { Capacity = d.TotalSize; Free = d.TotalFreeSpace; Label = Root + (String.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : "  " + d.VolumeLabel); } }
            catch (Exception) { }
        }
        public void AddIssue(string path, string reason)
        {
            Skipped++;
            if (Issues.Count < 1000) Issues.Add(path + " — " + reason);
        }
        // Callers hold Gate. Scans update this index incrementally; a cache load
        // rebuilds it once before publishing its result to the UI.
        public void RebuildFolderIndex()
        {
            childFolders.Clear();
            foreach (var record in Folders.Values) IndexFolder(record);
            indexedFolders = Folders; indexedFolderCount = Folders.Count;
        }
        void IndexFolder(FolderRecord record)
        {
            List<FolderRecord> children;
            if (!childFolders.TryGetValue(record.Parent, out children)) { children = new List<FolderRecord>(); childFolders.Add(record.Parent, children); }
            children.Add(record);
        }
        void EnsureFolderIndex()
        {
            if (!Object.ReferenceEquals(indexedFolders, Folders) || indexedFolderCount != Folders.Count) RebuildFolderIndex();
        }
        public IEnumerable<FolderRecord> GetChildFolders(string parent)
        {
            EnsureFolderIndex(); List<FolderRecord> children;
            return childFolders.TryGetValue(parent, out children) ? children : Enumerable.Empty<FolderRecord>();
        }
        public void AddFolder(FolderRecord record)
        {
            EnsureFolderIndex(); Folders.Add(record.Path, record); IndexFolder(record); indexedFolderCount = Folders.Count;
        }
        public void ClearFolders()
        {
            Folders.Clear(); childFolders.Clear(); indexedFolders = Folders; indexedFolderCount = 0;
        }
        public void ReplaceFolders(ScanState source)
        {
            source.EnsureFolderIndex();
            Folders = source.Folders; childFolders = source.childFolders;
            indexedFolders = Folders; indexedFolderCount = Folders.Count;
        }
    }
    sealed class WorkItem { public string Path; public int Context; }
    sealed class LargestFiles
    {
        internal const int Limit = 100;
        internal readonly List<FileRecord> Items = new List<FileRecord>();
        static int Compare(long bytes, string path, FileRecord other)
        {
            int order = other.Bytes.CompareTo(bytes);
            return order == 0 ? StringComparer.OrdinalIgnoreCase.Compare(path, other.Path) : order;
        }
        internal void Consider(string path, long bytes, int category)
        {
            if (Items.Count == Limit && Compare(bytes, path, Items[Limit - 1]) >= 0) return;
            int low = 0, high = Items.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (Compare(bytes, path, Items[middle]) < 0) high = middle; else low = middle + 1;
            }
            Items.Insert(low, new FileRecord { Path = path, Bytes = bytes, Category = category });
            if (Items.Count > Limit) Items.RemoveAt(Limit);
        }
    }
    public static class Scanner
    {
        public static string ParentOf(string path)
        {
            // Pure string operation: Directory.GetParent can throw for a long path
            // even after an enumeration error was already handled.
            string p = path.TrimEnd('\\'); if (p.Length == 2 && p[1] == ':') return "";
            int separator = p.LastIndexOf('\\'); if (separator < 0) return "";
            return p.Substring(0, separator == 2 && p[1] == ':' ? 3 : separator);
        }
        public static void Run(ScanState state, CancellationToken token)
        {
            lock (state.Gate)
            {
                state.Running = true; state.Complete = false; state.Cached = false; state.Status = "スキャン中"; state.Started = DateTime.Now;
                state.Bytes = new long[Catalog.Names.Length]; state.Counts = new long[Catalog.Names.Length];
                state.Files = state.Skipped = state.Links = 0; state.ClearFolders(); state.Issues.Clear();
                foreach (var list in state.Largest) list.Clear();
            }
            try
            {
                var stack = new Stack<WorkItem>(); stack.Push(new WorkItem { Path = state.Root, Context = -1 });
                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var item = stack.Pop();
                    lock (state.Gate) state.CurrentPath = item.Path;
                    var children = new List<string>(); bool assets = false, settings = false;
                    var directBytes = new long[Catalog.Names.Length]; var directCounts = new long[Catalog.Names.Length];
                    var candidates = new LargestFiles[Catalog.Names.Length];
                    try
                    {
                        var directory = new DirectoryInfo(item.Path);
                        // Recheck queued paths, including the scan root: a directory
                        // can become a junction after its parent was enumerated.
                        // Missing paths expose -1 attributes; let enumeration report
                        // their read error instead of counting them as reparse points.
                        var directoryAttributes = directory.Attributes;
                        if (directoryAttributes != (FileAttributes)(-1) && (directoryAttributes & FileAttributes.ReparsePoint) != 0)
                        {
                            lock (state.Gate) { state.Links++; if (state.Issues.Count < 1000) state.Issues.Add(item.Path + " — 再解析ポイント／クラウド項目を除外"); }
                            continue;
                        }
                        foreach (var entry in directory.EnumerateFileSystemInfos())
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                var attr = entry.Attributes;
                                // Junctions, symlinks and cloud placeholders are never followed.
                                if ((attr & FileAttributes.ReparsePoint) != 0) { lock (state.Gate) { state.Links++; if (state.Issues.Count < 1000) state.Issues.Add(entry.FullName + " — 再解析ポイント／クラウド項目を除外"); } continue; }
                                if ((attr & FileAttributes.Directory) != 0)
                                {
                                    children.Add(entry.FullName);
                                    if (entry.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)) assets = true;
                                    if (entry.Name.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase)) settings = true;
                                }
                                else
                                {
                                    // Keep only bounded rankings while streaming metadata.
                                    // Directory context is applied after Unity markers are known.
                                    long length = ((FileInfo)entry).Length; int category = Catalog.Classify(entry.FullName, -1);
                                    directBytes[category] += length; directCounts[category]++;
                                    if (candidates[category] == null) candidates[category] = new LargestFiles();
                                    candidates[category].Consider(entry.FullName, length, category);
                                }
                            }
                            catch (Exception ex)
                            { if (!Expected(ex)) throw; lock (state.Gate) state.AddIssue(entry.FullName, ex.Message); }
                        }
                    }
                    catch (Exception ex)
                    { if (!Expected(ex)) throw; lock (state.Gate) state.AddIssue(item.Path, ex.Message); }
                    int context = Catalog.Context(item.Path, item.Context, assets && settings);
                    if (context >= 0)
                    {
                        var contextualBytes = new long[Catalog.Names.Length]; var contextualCounts = new long[Catalog.Names.Length];
                        var contextualCandidates = new LargestFiles[Catalog.Names.Length];
                        for (int i = 0; i < directBytes.Length; i++)
                        {
                            // Paging files retain their system classification in every context.
                            int category = i == 10 ? 10 : context;
                            contextualBytes[category] += directBytes[i]; contextualCounts[category] += directCounts[i];
                            if (candidates[i] == null) continue;
                            if (contextualCandidates[category] == null) contextualCandidates[category] = new LargestFiles();
                            foreach (var file in candidates[i].Items) contextualCandidates[category].Consider(file.Path, file.Bytes, category);
                        }
                        directBytes = contextualBytes; directCounts = contextualCounts; candidates = contextualCandidates;
                    }
                    List<FileRecord>[] previous;
                    lock (state.Gate) previous = state.Largest.ToArray();
                    // Rank outside the UI-shared lock. The scanner publishes new
                    // lists, so previously published lists remain safe to read.
                    for (int i = 0; i < candidates.Length; i++)
                        if (candidates[i] != null)
                            foreach (var file in previous[i]) candidates[i].Consider(file.Path, file.Bytes, file.Category);
                    lock (state.Gate)
                    {
                        for (int i = 0; i < directBytes.Length; i++)
                        {
                            state.Bytes[i] += directBytes[i]; state.Counts[i] += directCounts[i];
                            if (candidates[i] != null) state.Largest[i] = candidates[i].Items;
                        }
                        state.Files += directCounts.Sum();
                        string path = item.Path;
                        while (!String.IsNullOrEmpty(path))
                        {
                            FolderRecord folder;
                            if (!state.Folders.TryGetValue(path, out folder))
                            {
                                folder = new FolderRecord { Path = path, Parent = ParentOf(path) };
                                state.AddFolder(folder);
                            }
                            for (int i = 0; i < directBytes.Length; i++) folder.Bytes[i] += directBytes[i];
                            if (path.Equals(state.Root, StringComparison.OrdinalIgnoreCase)) break;
                            path = folder.Parent;
                        }
                    }
                    // Reverse so traversal is stable and alphabetical.
                    children.Sort(StringComparer.OrdinalIgnoreCase);
                    for (int i = children.Count - 1; i >= 0; i--) stack.Push(new WorkItem { Path = children[i], Context = context });
                }
                lock (state.Gate) { state.Complete = true; state.Status = "完了"; }
            }
            catch (OperationCanceledException) { lock (state.Gate) state.Status = "中断・途中結果"; }
            catch (Exception ex) { lock (state.Gate) { state.Status = "エラー・途中結果"; state.AddIssue(state.CurrentPath, ex.Message); } }
            finally
            {
                lock (state.Gate) { state.Finished = DateTime.Now; state.Running = false; }
            }
        }
        static bool Expected(Exception ex) { return ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException; }
    }
    public static class Store
    {
        const int MaxPathCharacters = 32767, MaxIssueCharacters = 65536;
        // A generous expanded-size ceiling prevents an indefinitely expanding gzip
        // input without restricting the existing ten-million-folder cache format.
        const long MaxExpandedCacheBytes = 4L * 1024 * 1024 * 1024;
        static readonly Encoding CacheEncoding = new UTF8Encoding(false, true);
        public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveScope");
        public static string FileName(ScanState s) { string name = DriveInventory.CacheName(s.Root, s.VolumeIdentity); return name == null ? null : Path.Combine(DirectoryPath, name); }
        sealed class CacheSnapshot
        {
            public string Root; public DateTime Finished; public bool Complete;
            public long Files, Skipped, Links; public long[] Bytes, Counts;
            public FolderRecord[] Folders; public FileRecord[][] Largest; public string[] Issues;
        }
        static CacheSnapshot Snapshot(ScanState s)
        {
            lock (s.Gate)
                return new CacheSnapshot {
                    Root = s.Root, Finished = s.Finished, Complete = s.Complete,
                    Files = s.Files, Skipped = s.Skipped, Links = s.Links,
                    Bytes = (long[])s.Bytes.Clone(), Counts = (long[])s.Counts.Clone(),
                    Folders = s.Folders.Values.Select(f => new FolderRecord { Path = f.Path, Parent = f.Parent, Bytes = (long[])f.Bytes.Clone() }).ToArray(),
                    Largest = s.Largest.Select(list => list.Select(f => new FileRecord { Path = f.Path, Bytes = f.Bytes, Category = f.Category }).ToArray()).ToArray(),
                    Issues = s.Issues.ToArray()
                };
        }
        public static void Save(ScanState s, string overridePath = null)
        {
            string dest = overridePath ?? FileName(s); if (dest == null) return;
            dest = Path.GetFullPath(dest); Directory.CreateDirectory(Path.GetDirectoryName(dest));
            string temp = dest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var saved = Snapshot(s);
            try
            {
                // Compression and disk I/O must not block UI readers of the scan.
                // CreateNew plus a random name avoids following a pre-existing .tmp.
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var zip = new GZipStream(stream, CompressionMode.Compress))
                using (var w = new BinaryWriter(zip, Encoding.UTF8))
                {
                    w.Write(3); w.Write(saved.Root); w.Write(saved.Finished.ToBinary()); w.Write(saved.Complete); w.Write(saved.Files); w.Write(saved.Skipped); w.Write(saved.Links);
                    for (int i = 0; i < saved.Bytes.Length; i++) { w.Write(saved.Bytes[i]); w.Write(saved.Counts[i]); }
                    w.Write(saved.Folders.Length);
                    foreach (var f in saved.Folders) { w.Write(f.Path); w.Write(f.Parent); foreach (long b in f.Bytes) w.Write(b); }
                    foreach (var list in saved.Largest) { w.Write(list.Length); foreach (var f in list) { w.Write(f.Path); w.Write(f.Bytes); w.Write(f.Category); } }
                    w.Write(saved.Issues.Length); foreach (var issue in saved.Issues) w.Write(issue);
                }
                if (File.Exists(dest)) File.Replace(temp, dest, null); else File.Move(temp, dest);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        sealed class CacheReadStream : Stream
        {
            readonly Stream source; long remaining = MaxExpandedCacheBytes;
            public CacheReadStream(Stream source) { this.source = source; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (remaining == 0) throw new InvalidDataException("Expanded cache is too large.");
                int read = source.Read(buffer, offset, (int)Math.Min(count, remaining)); remaining -= read; return read;
            }
            public override int ReadByte()
            {
                if (remaining == 0) throw new InvalidDataException("Expanded cache is too large.");
                int value = source.ReadByte(); if (value >= 0) remaining--; return value;
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
        static string ReadText(BinaryReader reader, int maxCharacters)
        {
            // BinaryReader.ReadString trusts its byte-length prefix. Check that
            // prefix before allocating or decoding text from an untrusted cache.
            uint length = 0; int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                byte next = reader.ReadByte();
                if (i == 4 && (next & 0xF8) != 0) throw new InvalidDataException("Invalid string length.");
                length |= (uint)(next & 0x7F) << shift;
                if ((next & 0x80) == 0)
                {
                    if (length > (uint)maxCharacters * 4) throw new InvalidDataException("Cache string is too long.");
                    byte[] bytes = reader.ReadBytes((int)length);
                    if (bytes.Length != length) throw new EndOfStreamException();
                    string value = CacheEncoding.GetString(bytes);
                    if (value.Length > maxCharacters) throw new InvalidDataException("Cache string is too long.");
                    return value;
                }
                shift += 7;
            }
            throw new InvalidDataException("Invalid string length.");
        }
        static long ReadNonnegative(BinaryReader reader)
        {
            long value = reader.ReadInt64(); if (value < 0) throw new InvalidDataException("Negative cache total."); return value;
        }
        static int ReadCount(BinaryReader reader, int maximum)
        {
            int value = reader.ReadInt32(); if (value < 0 || value > maximum) throw new InvalidDataException("Invalid cache count."); return value;
        }
        static bool ContainedPath(string path, string root, string canonicalRoot, bool allowRoot)
        {
            if (String.IsNullOrEmpty(path)) return false;
            string full = Path.GetFullPath(path);
            if (full.TrimEnd('\\').Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)) return allowRoot && path.Equals(root, StringComparison.OrdinalIgnoreCase);
            return path.Equals(full, StringComparison.OrdinalIgnoreCase) && !path.EndsWith("\\", StringComparison.Ordinal) && full.StartsWith(canonicalRoot + "\\", StringComparison.OrdinalIgnoreCase);
        }
        public static bool Load(ScanState s, string overridePath = null)
        {
            string dest = overridePath ?? FileName(s); if (dest == null || !File.Exists(dest)) return false;
            try
            {
                // Parse into a temporary state: a truncated cache must not leave partial totals.
                var loaded = new ScanState(s.Root, false);
                string canonicalRoot = Path.GetFullPath(s.Root).TrimEnd('\\');
                using (var stream = File.OpenRead(dest))
                using (var zip = new GZipStream(stream, CompressionMode.Decompress))
                using (var input = new CacheReadStream(zip))
                using (var r = new BinaryReader(input, Encoding.UTF8))
                {
                    int version = r.ReadInt32(); if ((version != 2 && version != 3) || ReadText(r, MaxPathCharacters) != s.Root) return false;
                    int categories = version == 2 ? 12 : Catalog.Names.Length;
                    loaded.Finished = DateTime.FromBinary(r.ReadInt64()); loaded.Complete = r.ReadBoolean(); loaded.Files = ReadNonnegative(r); loaded.Skipped = ReadNonnegative(r); loaded.Links = ReadNonnegative(r);
                    long totalBytes = 0, totalFiles = 0;
                    for (int i = 0; i < categories; i++)
                    {
                        loaded.Bytes[i] = ReadNonnegative(r); loaded.Counts[i] = ReadNonnegative(r);
                        checked { totalBytes += loaded.Bytes[i]; totalFiles += loaded.Counts[i]; }
                    }
                    if (totalFiles != loaded.Files) return false;
                    int n = ReadCount(r, 10000000);
                    for (int k = 0; k < n; k++)
                    {
                        var f = new FolderRecord { Path = ReadText(r, MaxPathCharacters), Parent = ReadText(r, MaxPathCharacters) };
                        if (!ContainedPath(f.Path, s.Root, canonicalRoot, true) || !f.Parent.Equals(Scanner.ParentOf(f.Path), StringComparison.OrdinalIgnoreCase) || loaded.Folders.ContainsKey(f.Path)) return false;
                        for (int i = 0; i < categories; i++) { f.Bytes[i] = ReadNonnegative(r); if (f.Bytes[i] > loaded.Bytes[i]) return false; }
                        loaded.Folders.Add(f.Path, f);
                    }
                    FolderRecord root;
                    if (loaded.Folders.Count > 0)
                    {
                        if (!loaded.Folders.TryGetValue(s.Root, out root) || !root.Bytes.SequenceEqual(loaded.Bytes)) return false;
                        foreach (var f in loaded.Folders.Values) if (!f.Path.Equals(s.Root, StringComparison.OrdinalIgnoreCase) && !loaded.Folders.ContainsKey(f.Parent)) return false;
                    }
                    else if (loaded.Files != 0 || totalBytes != 0) return false;
                    var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < categories; i++)
                    {
                        n = ReadCount(r, 100); if (n > loaded.Counts[i]) return false;
                        for (int k = 0; k < n; k++)
                        {
                            var f = new FileRecord { Path = ReadText(r, MaxPathCharacters), Bytes = ReadNonnegative(r), Category = r.ReadInt32() };
                            if (f.Category != i || f.Bytes > loaded.Bytes[i] || !ContainedPath(f.Path, s.Root, canonicalRoot, false) || !loaded.Folders.ContainsKey(Scanner.ParentOf(f.Path)) || !filePaths.Add(f.Path)) return false;
                            loaded.Largest[i].Add(f);
                        }
                    }
                    n = ReadCount(r, 1000); for (int k = 0; k < n; k++) loaded.Issues.Add(ReadText(r, MaxIssueCharacters));
                    if (input.ReadByte() != -1) return false;
                }
                loaded.RebuildFolderIndex();
                lock (s.Gate)
                {
                s.Bytes = loaded.Bytes; s.Counts = loaded.Counts; s.ReplaceFolders(loaded); s.Largest = loaded.Largest; s.Issues = loaded.Issues;
                s.Files = loaded.Files; s.Skipped = loaded.Skipped; s.Links = loaded.Links; s.Finished = loaded.Finished; s.Complete = loaded.Complete;
                s.Cached = true; s.Status = s.Complete ? "保存済み" : "保存済み・途中結果";
                }
                return true;
            }
            catch (Exception) { return false; }
        }
        public static void WriteCsv(string path, IEnumerable<string[]> rows)
        {
            using (var w = new StreamWriter(path, false, new UTF8Encoding(true)))
                foreach (var row in rows) w.WriteLine(String.Join(",", row.Select(x => "\"" + (x ?? "").Replace("\"", "\"\"") + "\"")));
        }
    }
}
