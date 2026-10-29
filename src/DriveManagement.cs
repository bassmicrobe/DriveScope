using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DriveScope
{
    public sealed class DriveDescriptor
    {
        public string Root, Label, Identity, Kind;
        public bool Ready, DefaultIncluded;
        public long Capacity, Free;
    }
    public static class DriveInventory
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeNameForVolumeMountPoint(string root, StringBuilder name, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool GetVolumeInformation(string root, StringBuilder label, uint labelSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder format, uint formatSize);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint QueryDosDevice(string name, StringBuilder target, int size);
        public static List<DriveDescriptor> Read()
        {
            var list = new List<DriveDescriptor>();
            foreach (var d in DriveInfo.GetDrives().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var entry = new DriveDescriptor { Root = d.Name, Label = d.Name, Identity = "", Kind = "ドライブ" };
                try
                {
                    var type = d.DriveType;
                    entry.Kind = type == DriveType.Fixed ? "ローカル" : type == DriveType.Removable ? "リムーバブル" : type == DriveType.Network ? "ネットワーク" : type == DriveType.CDRom ? "光学メディア" : "ドライブ";
                    entry.DefaultIncluded = type == DriveType.Fixed || type == DriveType.Removable;
                    entry.Ready = d.IsReady;
                    if (entry.Ready)
                    {
                        entry.Label = d.Name + (String.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : "  " + d.VolumeLabel);
                        entry.Capacity = d.TotalSize; entry.Free = d.TotalFreeSpace;
                        var name = new StringBuilder(1024);
                        if (GetVolumeNameForVolumeMountPoint(d.Name, name, (uint)name.Capacity)) entry.Identity = name.ToString();
                        else
                        {
                            var label = new StringBuilder(1024); var format = new StringBuilder(256); uint serial, max, flags;
                            if (GetVolumeInformation(d.Name, label, (uint)label.Capacity, out serial, out max, out flags, format, (uint)format.Capacity))
                            {
                                var target = new StringBuilder(4096); QueryDosDevice(d.Name.TrimEnd('\\'), target, target.Capacity);
                                entry.Identity = type + ":" + serial.ToString("X8") + ":" + label + ":" + format + ":" + target;
                            }
                        }
                    }
                }
                catch (Exception ex) { if (!(ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)) throw; entry.Ready = false; }
                list.Add(entry);
            }
            return list;
        }
        public static string CacheName(string root, string identity)
        {
            if (String.IsNullOrEmpty(identity)) return null;
            using (var hash = SHA256.Create())
                return root.Substring(0, 1).ToUpperInvariant() + "_" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-", "").Substring(0, 24) + ".scan.gz";
        }
    }
    public sealed class DriveSelection
    {
        const int MaxPreferenceBytes = 4096;
        public readonly Dictionary<string, bool> Choices = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        public bool Get(DriveDescriptor drive) { bool chosen; return Choices.TryGetValue(drive.Root, out chosen) ? chosen : drive.DefaultIncluded; }
        public void Save(string path)
        {
            path = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                    foreach (var choice in Choices.OrderBy(p => p.Key)) writer.WriteLine(choice.Key + "\t" + (choice.Value ? "1" : "0"));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void Load(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    // Only 26 drive-letter choices are possible. Inspect the opened
                    // handle before decoding so a corrupt long line cannot allocate
                    // an arbitrarily large string; ignored files use drive defaults.
                    if (file.Length > MaxPreferenceBytes) return;
                    using (var reader = new StreamReader(file, Encoding.UTF8, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string[] parts = line.Split('\t');
                            if (parts.Length == 2 && parts[0].Length == 3 && ((parts[0][0] >= 'A' && parts[0][0] <= 'Z') || (parts[0][0] >= 'a' && parts[0][0] <= 'z')) && parts[0].Substring(1) == ":\\" && (parts[1] == "0" || parts[1] == "1")) Choices[parts[0]] = parts[1] == "1";
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public sealed class DriveSet
    {
        public readonly List<ScanState> States = new List<ScanState>();
        public readonly DriveSelection Selection;
        public DriveSet(DriveSelection selection) { Selection = selection; }
        public List<ScanState> Apply(IEnumerable<DriveDescriptor> inventory)
        {
            var added = new List<ScanState>(); var seen = new HashSet<ScanState>();
            foreach (var d in inventory)
            {
                var state = States.LastOrDefault(s => s.Root.Equals(d.Root, StringComparison.OrdinalIgnoreCase) && ((!d.Ready && s.Present) || (!String.IsNullOrEmpty(d.Identity) && s.VolumeIdentity == d.Identity) || (d.Ready && String.IsNullOrEmpty(d.Identity) && String.IsNullOrEmpty(s.VolumeIdentity) && s.Present && s.Ready)));
                if (state == null)
                {
                    state = new ScanState(d.Root, false) { VolumeIdentity = d.Identity, Included = Selection.Get(d) };
                    States.Add(state); added.Add(state);
                }
                lock (state.Gate)
                {
                    if (state.Present && state.Ready && !d.Ready && state.Cancel != null) state.Cancel.Cancel();
                    state.Present = true; state.Ready = d.Ready; state.Label = d.Label; state.Kind = d.Kind;
                    state.Included = Selection.Get(d);
                    state.Capacity = d.Capacity; state.Free = d.Free;
                }
                seen.Add(state);
            }
            foreach (var s in States.Where(s => !seen.Contains(s)))
            {
                s.Present = false; s.Ready = false;
                if (s.Cancel != null) s.Cancel.Cancel();
            }
            return added;
        }
        public IEnumerable<ScanState> Targets { get { return States.Where(s => s.Present && s.Ready && s.Included); } }
    }
}
