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
        public List<string> Issues = new List<string>();
        public bool Running, Saving, Complete, Cached;
        public DateTime Started, Finished;
        public CancellationTokenSource Cancel;
        public long Total { get { return Bytes.Sum(); } }
        public ScanState(string root, bool refresh = true)
        {
            Root = root; Label = root; if (refresh) RefreshDrive();
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
    }
    sealed class WorkItem { public string Path; public int Context; }
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
                state.Files = state.Skipped = state.Links = 0; state.Folders.Clear(); state.Issues.Clear();
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
                    var children = new List<string>(); var files = new List<FileInfo>(); bool assets = false, settings = false;
                    try
                    {
                        foreach (var entry in new DirectoryInfo(item.Path).EnumerateFileSystemInfos())
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
                                else files.Add((FileInfo)entry);
                            }
                            catch (Exception ex)
                            { if (!Expected(ex)) throw; lock (state.Gate) state.AddIssue(entry.FullName, ex.Message); }
                        }
                    }
                    catch (Exception ex)
                    { if (!Expected(ex)) throw; lock (state.Gate) state.AddIssue(item.Path, ex.Message); }
                    int context = Catalog.Context(item.Path, item.Context, assets && settings);
                    var directBytes = new long[Catalog.Names.Length]; var directCounts = new long[Catalog.Names.Length];
                    var candidates = Enumerable.Range(0, Catalog.Names.Length).Select(x => new List<FileRecord>()).ToArray();
                    foreach (var f in files)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            long length = f.Length; int category = Catalog.Classify(f.FullName, context);
                            directBytes[category] += length; directCounts[category]++;
                            candidates[category].Add(new FileRecord { Path = f.FullName, Bytes = length, Category = category });
                        }
                        catch (Exception ex)
                        { if (!Expected(ex)) throw; lock (state.Gate) state.AddIssue(f.FullName, ex.Message); }
                    }
                    lock (state.Gate)
                    {
                        for (int i = 0; i < directBytes.Length; i++)
                        {
                            state.Bytes[i] += directBytes[i]; state.Counts[i] += directCounts[i];
                            if (candidates[i].Count > 0)
                                state.Largest[i] = state.Largest[i].Concat(candidates[i]).OrderByDescending(x => x.Bytes).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).Take(100).ToList();
                        }
                        state.Files += directCounts.Sum();
                        string path = item.Path;
                        while (!String.IsNullOrEmpty(path))
                        {
                            FolderRecord folder;
                            if (!state.Folders.TryGetValue(path, out folder))
                            {
                                folder = new FolderRecord { Path = path, Parent = ParentOf(path) };
                                state.Folders.Add(path, folder);
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
        public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveScope");
        public static string FileName(ScanState s) { string name = DriveInventory.CacheName(s.Root, s.VolumeIdentity); return name == null ? null : Path.Combine(DirectoryPath, name); }
        public static void Save(ScanState s, string overridePath = null)
        {
            Directory.CreateDirectory(DirectoryPath); string dest = overridePath ?? FileName(s); if (dest == null) return; string temp = dest + ".tmp";
            lock (s.Gate)
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new GZipStream(stream, CompressionMode.Compress))
            using (var w = new BinaryWriter(zip, Encoding.UTF8))
            {
                w.Write(3); w.Write(s.Root); w.Write(s.Finished.ToBinary()); w.Write(s.Complete); w.Write(s.Files); w.Write(s.Skipped); w.Write(s.Links);
                for (int i = 0; i < s.Bytes.Length; i++) { w.Write(s.Bytes[i]); w.Write(s.Counts[i]); }
                w.Write(s.Folders.Count);
                foreach (var f in s.Folders.Values) { w.Write(f.Path); w.Write(f.Parent); foreach (long b in f.Bytes) w.Write(b); }
                foreach (var list in s.Largest) { w.Write(list.Count); foreach (var f in list) { w.Write(f.Path); w.Write(f.Bytes); w.Write(f.Category); } }
                w.Write(s.Issues.Count); foreach (var issue in s.Issues) w.Write(issue);
            }
            if (File.Exists(dest)) File.Replace(temp, dest, null); else File.Move(temp, dest);
        }
        public static bool Load(ScanState s, string overridePath = null)
        {
            string dest = overridePath ?? FileName(s); if (dest == null || !File.Exists(dest)) return false;
            try
            {
                // Parse into a temporary state: a truncated cache must not leave partial totals.
                var loaded = new ScanState(s.Root, false);
                using (var stream = File.OpenRead(dest))
                using (var zip = new GZipStream(stream, CompressionMode.Decompress))
                using (var r = new BinaryReader(zip, Encoding.UTF8))
                {
                    int version = r.ReadInt32(); if ((version != 2 && version != 3) || r.ReadString() != s.Root) return false;
                    int categories = version == 2 ? 12 : Catalog.Names.Length;
                    loaded.Finished = DateTime.FromBinary(r.ReadInt64()); loaded.Complete = r.ReadBoolean(); loaded.Files = r.ReadInt64(); loaded.Skipped = r.ReadInt64(); loaded.Links = r.ReadInt64();
                    for (int i = 0; i < categories; i++) { loaded.Bytes[i] = r.ReadInt64(); loaded.Counts[i] = r.ReadInt64(); }
                    int n = r.ReadInt32(); if (n < 0 || n > 10000000) return false;
                    for (int k = 0; k < n; k++) { var f = new FolderRecord { Path = r.ReadString(), Parent = r.ReadString() }; for (int i = 0; i < categories; i++) f.Bytes[i] = r.ReadInt64(); loaded.Folders[f.Path] = f; }
                    for (int i = 0; i < categories; i++) { n = r.ReadInt32(); if (n < 0 || n > 100) return false; for (int k = 0; k < n; k++) loaded.Largest[i].Add(new FileRecord { Path = r.ReadString(), Bytes = r.ReadInt64(), Category = r.ReadInt32() }); }
                    n = r.ReadInt32(); if (n < 0 || n > 1000) return false; for (int k = 0; k < n; k++) loaded.Issues.Add(r.ReadString());
                }
                lock (s.Gate)
                {
                s.Bytes = loaded.Bytes; s.Counts = loaded.Counts; s.Folders = loaded.Folders; s.Largest = loaded.Largest; s.Issues = loaded.Issues;
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
