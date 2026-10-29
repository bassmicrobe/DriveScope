using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace DriveScope
{
    public static class PerformanceVerification
    {
        static void Check(bool value, string label, List<string> log)
        {
            if (!value) throw new Exception("FAIL: " + label);
            log.Add("PASS: " + label);
        }
        public static void Run(string output, List<string> log)
        {
            int start = log.Count;
            VerifyRanking(log);
            VerifyFolderIndex(log);
            VerifyStreamingScan(output, log);
            File.WriteAllLines(Path.Combine(output, "performance-test.txt"), log.Skip(start));
        }
        static void VerifyRanking(List<string> log)
        {
            const int count = 100000;
            var ranking = new LargestFiles(); int peak = 0; var timer = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                ranking.Consider("C:\\fixture\\file" + (count - i).ToString("D6") + ".mp4", i % 1000, 4);
                peak = Math.Max(peak, ranking.Items.Count);
            }
            timer.Stop();
            var expected = Enumerable.Range(0, count).Select(i => new FileRecord { Path = "C:\\fixture\\file" + (count - i).ToString("D6") + ".mp4", Bytes = i % 1000 })
                .OrderByDescending(file => file.Bytes).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            Check(peak == 100 && ranking.Items.Select(file => file.Path).SequenceEqual(expected.Select(file => file.Path)), "100,000 streamed file candidates retain at most 100 and preserve exact size/path ranking", log);
            log.Add("MEASURE: ranking candidates=" + count + ", peak retained=" + peak + ", elapsed=" + timer.ElapsedMilliseconds + " ms");
        }
        static void VerifyFolderIndex(List<string> log)
        {
            var state = new ScanState("C:\\fixture\\", false);
            for (int i = 0; i < 50000; i++)
            {
                string parent = "C:\\fixture\\branch" + (i / 100).ToString("D3");
                state.AddFolder(new FolderRecord { Path = parent + "\\leaf" + i.ToString("D5"), Parent = parent });
            }
            string lookup = "C:\\fixture\\branch123"; var children = state.GetChildFolders(lookup);
            var timer = Stopwatch.StartNew(); int visited = 0;
            for (int i = 0; i < 10000; i++) foreach (var child in state.GetChildFolders(lookup)) visited++;
            timer.Stop();
            Check(children.Count() == 100 && visited == 1000000 && Object.ReferenceEquals(children, state.GetChildFolders(lookup)), "50,000-folder index reuses direct child lists across 10,000 lookups", log);
            log.Add("MEASURE: indexed folders=50000, lookups=10000, visited children=" + visited + ", elapsed=" + timer.ElapsedMilliseconds + " ms");
            state.ClearFolders();
            Check(!state.GetChildFolders(lookup).Any(), "clearing a scan drops stale indexed children", log);
            state.Folders.Add("C:\\fixture\\new", new FolderRecord { Path = "C:\\fixture\\new", Parent = state.Root });
            Check(state.GetChildFolders(state.Root).Single().Path.EndsWith("\\new"), "index detects direct fixture dictionary changes", log);
            var loaded = new ScanState(state.Root, false);
            loaded.AddFolder(new FolderRecord { Path = "C:\\fixture\\loaded", Parent = state.Root });
            var loadedChildren = loaded.GetChildFolders(state.Root); state.ReplaceFolders(loaded);
            Check(Object.ReferenceEquals(loadedChildren, state.GetChildFolders(state.Root)), "cache folder replacement transfers its prepared index without rebuilding", log);
        }
        static void VerifyStreamingScan(string output, List<string> log)
        {
            string fixture = Path.Combine(output, "performance_fixture_" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(fixture, "scan"), junction = Path.Combine(fixture, "junction");
            Directory.CreateDirectory(Path.Combine(root, "Assets")); Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            var expected = new List<FileRecord>(); string[] extensions = { ".mp4", ".png", ".bin", ".txt", ".gguf" };
            try
            {
                for (int i = 0; i < 1200; i++)
                {
                    string path = Path.Combine(root, "file" + i.ToString("D4") + extensions[i % extensions.Length]); long bytes = i % 191 + 1;
                    using (var file = File.Create(path)) file.SetLength(bytes);
                    expected.Add(new FileRecord { Path = path, Bytes = bytes });
                }
                using (var file = File.Create(Path.Combine(root, "pagefile.sys"))) file.SetLength(7);
                var state = new ScanState(root, false); Scanner.Run(state, CancellationToken.None);
                Check(state.Complete && state.Files == 1201 && state.Bytes[0] == expected.Sum(file => file.Bytes) && state.Bytes[10] == 7, "wide Unity folder applies detected context after streaming while preserving paging files", log);
                var expectedTop = expected.OrderByDescending(file => file.Bytes).ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Take(100).Select(file => file.Path);
                Check(state.Largest[0].Count == 100 && state.Largest[0].All(file => file.Category == 0) && state.Largest[0].Select(file => file.Path).SequenceEqual(expectedTop), "context remapping merges bounded extension rankings into the exact global top 100", log);
                string cache = Path.Combine(fixture, "wide.scan.gz"); Store.Save(state, cache); var loaded = new ScanState(root, false);
                Check(Store.Load(loaded, cache) && loaded.GetChildFolders(root).Count() == 2, "streamed scan cache restores direct child folder index", log);
                var trailing = new ScanState(root + "\\", false); Scanner.Run(trailing, CancellationToken.None);
                Check(trailing.Root == root && trailing.Total == state.Total && trailing.Folders.Keys.All(path => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)), "trailing directory separator is normalized without aggregating ancestors outside the scan root", log);
                Store.Save(trailing, cache); var trailingLoaded = new ScanState(root + "\\", false);
                Check(Store.Load(trailingLoaded, cache) && trailingLoaded.Total == state.Total && trailingLoaded.GetChildFolders(root).Count() == 2 && new ScanState("C:\\", false).Root == "C:\\", "normalized directory-root cache round trip preserves drive-root separators", log);
                Directory.Delete(Path.Combine(root, "ProjectSettings")); Scanner.Run(state, CancellationToken.None);
                Check(state.Complete && state.Bytes[0] == 0 && state.Bytes[4] > 0 && state.Bytes[12] > 0 && state.GetChildFolders(root).Count() == 1, "rescan resets folder index and restores extension categories when Unity markers disappear", log);
                string target = Path.Combine(fixture, "target"); Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "outside.txt"), "must not be scanned");
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), "/d /c mklink /J \"" + junction + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var process = Process.Start(info))
                {
                    process.WaitForExit();
                    Check(process.ExitCode == 0, "test junction is created for scan-root exclusion", log);
                }
                var linked = new ScanState(junction, false); Scanner.Run(linked, CancellationToken.None);
                Check(linked.Complete && linked.Links == 1 && linked.Files == 0 && linked.Total == 0, "a reparse-point scan root is excluded before enumeration", log);
                var missing = new ScanState(Path.Combine(fixture, "missing"), false); Scanner.Run(missing, CancellationToken.None);
                Check(missing.Skipped == 1 && missing.Links == 0 && missing.Files == 0, "a missing scan root remains a read error instead of a reparse-point exclusion", log);
            }
            finally
            {
                // Remove the junction itself before recursively removing this test's
                // GUID directory, which is checked to stay inside the output folder.
                if (Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(output).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(junction)) Directory.Delete(junction);
                    Directory.Delete(fixture, true);
                }
            }
        }
    }
}
