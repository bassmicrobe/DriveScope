using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace DriveScope
{
    public abstract class ObservableRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Notify(string name) { var handler = PropertyChanged; if (handler != null) handler(this, new PropertyChangedEventArgs(name)); }
    }
    public sealed class DisplayRow : ObservableRow
    {
        string name, path, category; long bytes; double ratio;
        public string Name { get { return name; } set { if (name != value) { name = value; Notify("Name"); } } }
        public string Path { get { return path; } set { if (path != value) { path = value; Notify("Path"); } } }
        public string Category { get { return category; } set { if (category != value) { category = value; Notify("Category"); } } }
        public long Bytes { get { return bytes; } set { if (bytes != value) { bytes = value; Notify("Bytes"); Notify("Size"); } } }
        public double Ratio { get { return ratio; } set { if (ratio != value) { ratio = value; Notify("Ratio"); Notify("Share"); } } }
        public string Size { get { return Catalog.Size(Bytes); } }
        public string Share { get { return Ratio.ToString("P1"); } }
        public bool UpdateFrom(DisplayRow row)
        {
            bool changed = Name != row.Name || Path != row.Path || Category != row.Category || Bytes != row.Bytes || Ratio != row.Ratio;
            Name = row.Name; Path = row.Path; Category = row.Category; Bytes = row.Bytes; Ratio = row.Ratio; return changed;
        }
    }
    public sealed class SizeCell : ObservableRow
    {
        long bytes;
        public long Bytes { get { return bytes; } set { if (bytes != value) { bytes = value; Notify("Bytes"); Notify("Text"); } } }
        public string Text { get { return Catalog.Size(Bytes); } }
    }
    public sealed class ComparisonRow : ObservableRow
    {
        public string Name { get; set; }
        public List<SizeCell> Cells { get; set; }
        public ComparisonRow() { Cells = new List<SizeCell>(); }
        public long Bytes { get { return Cells.Sum(c => c.Bytes); } }
        public string Total { get { return Catalog.Size(Bytes); } }
        public bool UpdateFrom(ComparisonRow row)
        {
            bool changed = false;
            if (Name != row.Name) { Name = row.Name; Notify("Name"); changed = true; }
            if (Cells.Count != row.Cells.Count) { Cells = row.Cells; Notify("Cells"); changed = true; }
            else for (int i = 0; i < Cells.Count; i++) if (Cells[i].Bytes != row.Cells[i].Bytes) { Cells[i].Bytes = row.Cells[i].Bytes; changed = true; }
            if (changed) { Notify("Bytes"); Notify("Total"); } return changed;
        }
    }
    public static class RowReconciler
    {
        public static bool Update<T>(ObservableCollection<T> items, IEnumerable<T> desired, Func<T, string> key, Func<T, T, bool> update) where T : class
        {
            var rows = desired.GroupBy(key, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            var wanted = new HashSet<string>(rows.Select(key), StringComparer.OrdinalIgnoreCase);
            var existing = items.ToDictionary(key, StringComparer.OrdinalIgnoreCase); bool changed = false;
            for (int i = items.Count - 1; i >= 0; i--) if (!wanted.Contains(key(items[i]))) { items.RemoveAt(i); changed = true; }
            for (int i = 0; i < rows.Count; i++)
            {
                T row;
                if (!existing.TryGetValue(key(rows[i]), out row)) { row = rows[i]; items.Insert(i, row); changed = true; }
                else
                {
                    changed = update(row, rows[i]) || changed;
                    if (!Object.ReferenceEquals(items[i], row)) { items.Move(items.IndexOf(row), i); changed = true; }
                }
            }
            return changed;
        }
    }
}
