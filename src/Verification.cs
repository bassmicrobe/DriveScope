using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading;

namespace DriveScope
{
    public static class Verification
    {
        static void Assert(bool value, string label, List<string> log)
        {
            if (!value) throw new Exception("FAIL: " + label);
            log.Add("PASS: " + label);
        }
        static void FixtureFile(string root, string relative, int size)
        {
            string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var f = File.Create(path)) f.SetLength(size);
        }
        public static int Run(string output)
        {
            output = Path.GetFullPath(output); Directory.CreateDirectory(output);
            string root = Path.Combine(output, "fixture_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var log = new List<string>();
            try
            {
                FixtureFile(root, "video\\movie.mp4", 1000); FixtureFile(root, "video\\nested\\another.mkv", 2000);
                FixtureFile(root, "audio\\track.wav", 300); FixtureFile(root, "photos\\photo.jpg", 400);
                FixtureFile(root, "art\\scene.blend", 500); FixtureFile(root, "downloads\\archive.zip", 600);
                FixtureFile(root, "docs\\notes.md", 700); FixtureFile(root, "other.weird", 800);
                FixtureFile(root, "Project\\Assets\\texture.png", 900); FixtureFile(root, "Project\\ProjectSettings\\project.asset", 100);
                FixtureFile(root, "Project\\Library\\artifacts\\large.bin", 1100);
                FixtureFile(root, "Project\\node_modules\\dependency.js", 1200);
                FixtureFile(root, "Program Files\\App\\icon.png", 1300);
                FixtureFile(root, "Program Files\\Steam\\steamapps\\common\\Game\\level.pak", 1400);
                FixtureFile(root, "TEMP\\cache.bin", 1500);
                FixtureFile(root, "AI\\model.gguf", 100);
                string longRelative = String.Join("\\", Enumerable.Repeat("long_folder_12345", 18)) + "\\long_file.unknown";
                FixtureFile(root, longRelative, 50);
                for (int i = 0; i < 130; i++) FixtureFile(root, "ranking\\movie" + i.ToString("D3") + ".mp4", i + 1);
                var s = new ScanState(root); Scanner.Run(s, CancellationToken.None);
                Assert(s.Complete && !s.Running, "scan completes", log);
                Assert(s.Files == 147, "all 147 files counted once including path over 260 characters", log);
                Assert(s.Bytes[0] == 1000, "Unity project textures and assets", log);
                Assert(s.Bytes[1] == 1300 && s.Bytes[2] == 1400, "app and game context", log);
                Assert(s.Bytes[3] == 3800, "Unity Library / dependencies / case-insensitive TEMP", log);
                Assert(s.Bytes[4] == 11515 && s.Bytes[5] == 300 && s.Bytes[6] == 400 && s.Bytes[7] == 500 && s.Bytes[8] == 600 && s.Bytes[9] == 700 && s.Bytes[11] == 850, "extension categories and exact byte totals", log);
                Assert(s.Bytes[12] == 100, "AI model extension classification", log);
                Assert(s.Total == 22465, "category totals match fixture bytes", log);
                Assert(Scanner.ParentOf(Path.Combine(root, longRelative)).Length > 260 && Scanner.ParentOf("C:\\") == "", "long-path parent extraction and drive root", log);
                Assert(s.Folders[root].Total == s.Total, "root folder aggregation equals categories", log);
                Assert(s.Folders[Path.Combine(root, "video")].Bytes[4] == 3000, "nested folder roll-up", log);
                Assert(s.Folders[Path.Combine(root, "Project")].Total == 3300, "project inclusive folder totals", log);
                Assert(s.Largest[4].Count == 100 && s.Largest[4][0].Bytes == 2000 && s.Largest[4][99].Bytes == 33, "bounded top-100 selection and ordering", log);
                Assert(Catalog.Classify("C:\\pagefile.sys", -1) == 10 && Catalog.Context("C:\\Windows", -1, false) == 10, "Windows and paging classification", log);
                Assert(Catalog.Size(1073741824) == "1.0 GB", "binary size formatting", log);
                var cancelled = new ScanState(root); var cts = new CancellationTokenSource(); cts.Cancel(); Scanner.Run(cancelled, cts.Token);
                Assert(!cancelled.Running && !cancelled.Complete && cancelled.Status.Contains("中断"), "cancellation produces explicit partial state", log);
                string cache = Path.Combine(output, "roundtrip.scan.gz"); Store.Save(s, cache); var loaded = new ScanState(root);
                Assert(Store.Load(loaded, cache) && loaded.Cached && loaded.Complete && loaded.Total == s.Total && loaded.Files == s.Files && loaded.Folders.Count == s.Folders.Count && loaded.Largest[4].Count == 100, "compressed cache round trip", log);
                Store.Save(cancelled, cache); loaded = new ScanState(root); Assert(Store.Load(loaded, cache) && !loaded.Complete && loaded.Status.Contains("途中"), "atomic replacement preserves partial marker", log);
                File.WriteAllText(cache, "corrupt"); var invalid = new ScanState(root); Assert(!Store.Load(invalid, cache) && invalid.Total == 0, "corrupt cache safely ignored", log);
                string csv = Path.Combine(output, "encoding.csv"); Store.WriteCsv(csv, new[] { new[] { "カテゴリ", "a,\"b\"", "改行\nあり" } });
                byte[] csvBytes = File.ReadAllBytes(csv); string csvText = File.ReadAllText(csv);
                Assert(csvBytes[0] == 239 && csvBytes[1] == 187 && csvBytes[2] == 191 && csvText.Contains("\"a,\"\"b\"\"\"") && csvText.Contains("改行\nあり"), "Excel UTF-8 BOM and CSV escaping", log);
                VerifyDriveManagement(output, log);
                VerifyIncrementalRows(log);
                PerformanceVerification.Run(output, log);
                SecurityVerification.Run(output, log);
                log.Add("ALL CHECKS PASSED"); File.WriteAllLines(Path.Combine(output, "self-test.txt"), log); return 0;
            }
            catch (Exception ex) { log.Add(ex.ToString()); File.WriteAllLines(Path.Combine(output, "self-test.txt"), log); return 1; }
            finally
            {
                // This GUID directory is created by this test and is always inside output.
                if (Path.GetFullPath(root).StartsWith(output.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
            }
        }
        static void VerifyIncrementalRows(List<string> log)
        {
            var rows = new ObservableCollection<DisplayRow>(); var actions = new List<NotifyCollectionChangedAction>();
            rows.CollectionChanged += delegate(object sender, NotifyCollectionChangedEventArgs e) { actions.Add(e.Action); };
            Func<DisplayRow, string> key = row => row.Path; Func<DisplayRow, DisplayRow, bool> update = (current, next) => current.UpdateFrom(next);
            RowReconciler.Update(rows, new[] { new DisplayRow { Name = "A", Path = "A", Bytes = 10 }, new DisplayRow { Name = "B", Path = "B", Bytes = 20 } }, key, update);
            var a = rows[0]; var b = rows[1]; actions.Clear();
            bool changed = RowReconciler.Update(rows, new[] { new DisplayRow { Name = "A", Path = "A", Bytes = 10 }, new DisplayRow { Name = "B", Path = "B", Bytes = 20 } }, key, update);
            Assert(!changed && actions.Count == 0 && rows[0] == a && rows[1] == b, "unchanged presentation keeps row identity without collection notifications", log);
            int sizeNotifications = 0; a.PropertyChanged += delegate(object sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == "Size") sizeNotifications++; };
            RowReconciler.Update(rows, new[] { new DisplayRow { Name = "A", Path = "A", Bytes = 30 }, new DisplayRow { Name = "B", Path = "B", Bytes = 20 } }, key, update);
            Assert(rows[0] == a && a.Bytes == 30 && sizeNotifications == 1 && actions.Count == 0, "size update notifies existing row without replacing the collection", log);
            RowReconciler.Update(rows, new[] { new DisplayRow { Name = "B", Path = "B", Bytes = 20 }, new DisplayRow { Name = "A", Path = "A", Bytes = 30 } }, key, update);
            Assert(rows[0] == b && rows[1] == a && actions.SequenceEqual(new[] { NotifyCollectionChangedAction.Move }), "rank changes move existing rows without resetting", log);
            actions.Clear(); RowReconciler.Update(rows, new[] { new DisplayRow { Name = "C", Path = "C", Bytes = 40 }, new DisplayRow { Name = "A", Path = "A", Bytes = 30 } }, key, update);
            Assert(rows.Count == 2 && rows[1] == a && !actions.Contains(NotifyCollectionChangedAction.Reset), "file additions and removals preserve unaffected rows", log);
            var comparison = new ComparisonRow { Name = "test", Cells = new List<SizeCell> { new SizeCell { Bytes = 10 }, new SizeCell { Bytes = 20 } } };
            var cells = comparison.Cells; var cell = cells[1]; int textNotifications = 0;
            cell.PropertyChanged += delegate(object sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == "Text") textNotifications++; };
            comparison.UpdateFrom(new ComparisonRow { Name = "test", Cells = new List<SizeCell> { new SizeCell { Bytes = 10 }, new SizeCell { Bytes = 40 } } });
            Assert(comparison.Cells == cells && comparison.Cells[1] == cell && comparison.Bytes == 50 && textNotifications == 1, "drive comparison updates existing cells and total", log);
            changed = comparison.UpdateFrom(new ComparisonRow { Name = "test", Cells = new List<SizeCell> { new SizeCell { Bytes = 10 }, new SizeCell { Bytes = 40 } } });
            Assert(!changed && textNotifications == 1, "unchanged drive cells do not raise redundant changes", log);
        }

        public static int VerifyCaches(string output)
        {
            var log = new List<string>(); bool ok = true;
            foreach (var descriptor in DriveInventory.Read().Where(d => d.Ready))
            {
                var s = new ScanState(descriptor.Root, false) { VolumeIdentity = descriptor.Identity }; bool loaded = Store.Load(s);
                log.Add(s.Root + " loaded=" + loaded + " files=" + s.Files + " complete=" + s.Complete + " total=" + s.Total);
                string file = Store.FileName(s); if (file != null && File.Exists(file) && !loaded) ok = false;
            }
            File.WriteAllLines(output, log); return ok ? 0 : 1;
        }
        public static int MigrateLegacy(string output)
        {
            // Explicit upgrade command used only when the old drive-letter caches
            // have been confirmed to belong to the currently attached disks.
            var log = new List<string>();
            foreach (var d in DriveInventory.Read().Where(d => d.Ready && !String.IsNullOrEmpty(d.Identity)))
            {
                var state = new ScanState(d.Root, false) { VolumeIdentity = d.Identity };
                string legacy = Path.Combine(Store.DirectoryPath, d.Root.Substring(0, 1) + ".scan.gz");
                if (!File.Exists(Store.FileName(state)) && Store.Load(state, legacy)) { Store.Save(state); log.Add(d.Root + " migrated " + state.Files + " files"); }
            }
            File.WriteAllLines(output, log); return 0;
        }
        static DriveDescriptor Drive(string root, string identity, bool ready = true)
        {
            return new DriveDescriptor { Root = root, Identity = identity, Ready = ready, DefaultIncluded = true, Label = root + " test", Kind = "テスト", Capacity = 10000, Free = 5000 };
        }
        static void VerifyDriveManagement(string output, List<string> log)
        {
            var choices = new DriveSelection(); var set = new DriveSet(choices);
            var c = Drive("C:\\", "volume-c"); var d = Drive("D:\\", "volume-d"); var g = Drive("G:\\", "volume-g"); var z = Drive("Z:\\", "volume-z");
            Assert(set.Apply(new[] { d }).Count == 1 && set.Targets.Single().Root == "D:\\", "arbitrary single drive, no C E F assumption", log);
            set.Apply(new[] { c, d, g, z }); Assert(set.Targets.Count() == 4, "four drives detected dynamically", log);
            var old = set.Targets.Single(s => s.Root == "G:\\"); old.Files = 42; old.Cancel = new CancellationTokenSource();
            set.Apply(new[] { c, d, z }); Assert(!old.Present && old.Cancel.IsCancellationRequested && set.Targets.Count() == 3, "removal excludes drive and cancels its scan", log);
            set.Apply(new[] { c, d, g, z }); Assert(set.Targets.Single(s => s.Root == "G:\\") == old && old.Files == 42, "same disk reconnection reuses its result", log);
            var replacement = Drive("G:\\", "different-usb");
            set.Apply(new[] { c, d, replacement, z }); var fresh = set.Targets.Single(s => s.Root == "G:\\");
            Assert(fresh != old && fresh.Files == 0 && DriveInventory.CacheName("G:\\", g.Identity) != DriveInventory.CacheName("G:\\", replacement.Identity), "same letter different disk gets a separate result and cache", log);
            Assert(DriveInventory.CacheName("D:\\", "volume-d") == DriveInventory.CacheName("D:\\", "volume-d") && DriveInventory.CacheName("D:\\", "") == null, "cache identities are stable; unknown identity cannot reuse a cache", log);
            choices.Choices["D:\\"] = false; choices.Choices["Z:\\"] = true;
            set.Apply(new[] { c, d, replacement, z }); Assert(!set.Targets.Any(s => s.Root == "D:\\"), "reconnected disks honor the latest saved letter selection", log);
            string pref = Path.Combine(output, "selection-test.txt"); choices.Save(pref); var reloaded = new DriveSelection(); reloaded.Load(pref);
            Assert(!reloaded.Get(d) && reloaded.Get(z) && reloaded.Get(c), "selected drives survive restart and new local drives default selected", log);
            choices.Choices["D:\\"] = true; choices.Save(pref); reloaded = new DriveSelection(); reloaded.Load(pref); Assert(reloaded.Get(d), "selection file atomic replacement", log);
            set.Apply(new DriveDescriptor[0]); Assert(!set.Targets.Any(), "zero connected drives supported", log);
            set.Apply(new[] { Drive("R:\\", "empty-reader", false) }); Assert(!set.Targets.Any(), "unavailable media excluded from scan targets", log);
        }
        public static int ScanRoot(string[] args)
        {
            string root = Path.GetFullPath(args[1]); string output = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(Environment.CurrentDirectory, "scan-report.txt");
            var s = new ScanState(root); Scanner.Run(s, CancellationToken.None);
            var lines = new List<string> { root, s.Status, "Files: " + s.Files, "Skipped: " + s.Skipped, "Links: " + s.Links, "Bytes: " + s.Total };
            for (int i = 0; i < s.Bytes.Length; i++) lines.Add(Catalog.Names[i] + ": " + s.Bytes[i]);
            lines.AddRange(s.Issues); File.WriteAllLines(output, lines);
            return s.Complete ? 0 : 1;
        }
    }
}
