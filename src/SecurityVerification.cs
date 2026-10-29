using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DriveScope
{
    public static class SecurityVerification
    {
        static void Check(bool value, string message, List<string> log)
        {
            if (!value) throw new Exception("FAIL: " + message);
            log.Add("PASS: " + message);
        }
        static ScanState Fixture(string root)
        {
            var state = new ScanState(root, false) { Files = 1, Complete = true, Finished = DateTime.Now };
            state.Bytes[9] = 7; state.Counts[9] = 1;
            var folder = new FolderRecord { Path = root, Parent = Scanner.ParentOf(root) }; folder.Bytes[9] = 7;
            state.Folders.Add(root, folder);
            state.Largest[9].Add(new FileRecord { Path = Path.Combine(root, "file.txt"), Bytes = 7, Category = 9 });
            state.Issues.Add("before snapshot");
            return state;
        }
        static void Reject(string output, string root, string name, Action<ScanState> mutate, List<string> log)
        {
            var corrupt = Fixture(root); mutate(corrupt);
            string path = Path.Combine(output, name + ".scan.gz"); Store.Save(corrupt, path);
            var target = Fixture(root); target.Status = "unchanged";
            var bytes = target.Bytes; var folders = target.Folders; var files = target.Largest;
            bool loaded = Store.Load(target, path);
            Check(!loaded && target.Bytes == bytes && target.Folders == folders && target.Largest == files && target.Status == "unchanged" && !target.Cached,
                "malformed cache rejected without changing current results: " + name, log);
        }
        static void VerifyReadBounds(string output, string root, List<string> log)
        {
            string path = Path.Combine(output, "oversized-string.scan.gz");
            using (var file = File.Create(path))
            using (var zip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new BinaryWriter(zip, Encoding.UTF8))
            {
                writer.Write(3);
                // Claim a 2 GiB string without allocating or emitting that payload.
                writer.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07 });
            }
            Check(!Store.Load(new ScanState(root, false), path), "oversized string prefix rejected before allocation", log);
            var valid = Fixture(root); Store.Save(valid, path);
            byte[] data;
            using (var file = File.OpenRead(path))
            using (var zip = new GZipStream(file, CompressionMode.Decompress))
            using (var memory = new MemoryStream()) { zip.CopyTo(memory); data = memory.ToArray(); }
            using (var file = File.Create(path))
            using (var zip = new GZipStream(file, CompressionMode.Compress)) { zip.Write(data, 0, data.Length); zip.WriteByte(1); }
            Check(!Store.Load(new ScanState(root, false), path), "trailing expanded cache payload rejected", log);
        }
        static void VerifyLegacyCache(string output, string root, List<string> log)
        {
            var state = Fixture(root); string path = Path.Combine(output, "legacy-v2.scan.gz");
            using (var file = File.Create(path))
            using (var zip = new GZipStream(file, CompressionMode.Compress))
            using (var writer = new BinaryWriter(zip, Encoding.UTF8))
            {
                writer.Write(2); writer.Write(root); writer.Write(state.Finished.ToBinary()); writer.Write(true);
                writer.Write(state.Files); writer.Write(0L); writer.Write(0L);
                for (int i = 0; i < 12; i++) { writer.Write(state.Bytes[i]); writer.Write(state.Counts[i]); }
                writer.Write(1); writer.Write(root); writer.Write(Scanner.ParentOf(root));
                for (int i = 0; i < 12; i++) writer.Write(state.Bytes[i]);
                for (int i = 0; i < 12; i++)
                {
                    writer.Write(state.Largest[i].Count);
                    foreach (var fileRecord in state.Largest[i]) { writer.Write(fileRecord.Path); writer.Write(fileRecord.Bytes); writer.Write(fileRecord.Category); }
                }
                writer.Write(0);
            }
            var loaded = new ScanState(root, false);
            Check(Store.Load(loaded, path) && loaded.Files == 1 && loaded.Bytes[9] == 7 && loaded.Bytes[12] == 0 && loaded.Largest[9].Count == 1,
                "valid version-2 cache remains compatible with category validation", log);
        }
        static void VerifySaveSnapshot(string output, string root, List<string> log)
        {
            var state = Fixture(root);
            // Unique names produce enough real compression work to observe a write.
            // No directories or test files are created for these synthetic records.
            for (int i = 0; i < 50000; i++)
            {
                string child = Path.Combine(root, "folder_" + i.ToString("D6") + "_" + Guid.NewGuid().ToString("N"));
                state.Folders.Add(child, new FolderRecord { Path = child, Parent = root });
            }
            string path = Path.Combine(output, "save-snapshot.scan.gz"), oldTemp = path + ".tmp";
            File.WriteAllText(oldTemp, "do not overwrite");
            var task = Task.Run(() => Store.Save(state, path));
            bool observed = false, entered = false, writing = false;
            var deadline = Stopwatch.StartNew();
            while (!task.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (Directory.GetFiles(output, Path.GetFileName(path) + ".*.tmp").Length > 0)
                {
                    observed = true;
                    entered = Monitor.TryEnter(state.Gate);
                    if (entered)
                    {
                        try
                        {
                            writing = !task.IsCompleted;
                            state.Bytes[9] = 99; state.Counts[9] = 2; state.Files = 2;
                            state.Folders[root].Bytes[9] = 99; state.Largest[9][0].Bytes = 99;
                            state.Issues[0] = "after snapshot";
                        }
                        finally { Monitor.Exit(state.Gate); }
                    }
                    break;
                }
                Thread.Sleep(1);
            }
            task.GetAwaiter().GetResult();
            Check(observed && entered && writing, "scan Gate is available while cache gzip is being written", log);
            var loaded = new ScanState(root, false);
            Check(Store.Load(loaded, path) && loaded.Bytes[9] == 7 && loaded.Files == 1 && loaded.Folders[root].Bytes[9] == 7 && loaded.Largest[9][0].Bytes == 7 && loaded.Issues[0] == "before snapshot",
                "cache save uses a consistent detached snapshot", log);
            Check(File.ReadAllText(oldTemp) == "do not overwrite" && Directory.GetFiles(output, Path.GetFileName(path) + ".*.tmp").Length == 0,
                "cache save leaves pre-existing temp paths untouched and cleans its own temp", log);
        }
        static void VerifyPreferences(string output, List<string> log)
        {
            string path = Path.Combine(output, "bounded-selection.txt"), oldTemp = path + ".tmp";
            File.WriteAllText(oldTemp, "do not overwrite");
            var choices = new DriveSelection(); choices.Choices["D:\\"] = false; choices.Save(path);
            var loaded = new DriveSelection(); loaded.Load(path);
            Check(loaded.Choices.ContainsKey("D:\\") && !loaded.Choices["D:\\"] && File.ReadAllText(oldTemp) == "do not overwrite" && Directory.GetFiles(output, Path.GetFileName(path) + ".*.tmp").Length == 0,
                "selection save round-trips without touching predictable temp paths", log);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(4097);
            loaded = new DriveSelection(); loaded.Load(path);
            var drive = new DriveDescriptor { Root = "D:\\", DefaultIncluded = true };
            Check(loaded.Choices.Count == 0 && loaded.Get(drive), "oversized selection ignored before parsing and drive defaults retained", log);
        }
        public static void Run(string output, List<string> log)
        {
            string root = Path.Combine(output, "cache-fixture");
            Reject(output, root, "out-of-range-category", s => s.Largest[9][0].Category = Catalog.Names.Length, log);
            Reject(output, root, "mismatched-category", s => s.Largest[9][0].Category = 0, log);
            Reject(output, root, "negative-byte-total", s => s.Bytes[9] = -1, log);
            Reject(output, root, "negative-file-count", s => s.Files = -1, log);
            Reject(output, root, "inconsistent-file-count", s => s.Counts[9] = 2, log);
            Reject(output, root, "overflowing-byte-total", s => { s.Bytes[0] = Int64.MaxValue; s.Bytes[1] = Int64.MaxValue; }, log);
            Reject(output, root, "outside-root-file", s => s.Largest[9][0].Path = Path.Combine(output, "outside.txt"), log);
            Reject(output, root, "relative-file", s => s.Largest[9][0].Path = "file.txt", log);
            Reject(output, root, "traversal-file", s => s.Largest[9][0].Path = Path.Combine(root, "..", "cache-fixture", "file.txt"), log);
            Reject(output, root, "outside-root-folder", s => { string p = Path.Combine(output, "outside"); s.Folders.Add(p, new FolderRecord { Path = p, Parent = Scanner.ParentOf(p) }); }, log);
            Reject(output, root, "wrong-folder-parent", s => s.Folders[root].Parent = root, log);
            Reject(output, root, "oversized-path", s => s.Largest[9][0].Path = root + "\\" + new string('x', 32768), log);
            VerifyReadBounds(output, root, log);
            VerifyLegacyCache(output, root, log);
            VerifySaveSnapshot(output, root, log);
            VerifyPreferences(output, log);
        }
    }
}
