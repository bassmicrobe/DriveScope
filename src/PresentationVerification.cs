using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DriveScope
{
    public sealed partial class MainController
    {
        void LoadDocumentationSample()
        {
            // Only this opt-in screenshot mode uses example values. It never reads
            // the drive inventory, stored scans, or the user's selection settings.
            long gb = 1L << 30;
            string[] roots = { "C:\\", "E:\\", "F:\\" };
            string[] labels = { "System", "Projects", "Archive" };
            long[] capacities = { 512 * gb, 1024 * gb, 2048 * gb };
            int[][] sizes = {
                new[] { 26, 48, 64, 35, 8, 4, 12, 10, 8, 12, 58, 5, 24 },
                new[] { 230, 12, 24, 96, 18, 8, 44, 72, 38, 20, 0, 6, 96 },
                new[] { 80, 4, 240, 32, 340, 96, 128, 160, 72, 16, 0, 24, 64 }
            };
            var sample = roots.Select((root, index) => new DriveDescriptor {
                Root = root, Label = root + "  " + labels[index], Identity = "documentation-" + index,
                Ready = true, DefaultIncluded = true, Kind = "サンプル", Capacity = capacities[index], Free = capacities[index] - sizes[index].Sum() * gb
            }).ToArray();
            ApplyInventory(sample);
            for (int i = 0; i < roots.Length; i++)
            {
                var state = states.Single(s => s.Root == roots[i]);
                state.Bytes = sizes[i].Select(value => value * gb).ToArray();
                state.Counts = sizes[i].Select(value => value * 127L).ToArray(); state.Files = state.Counts.Sum();
                state.Complete = true; state.Status = "サンプル"; state.Finished = new DateTime(2026, 1, 1, 12, 0, 0);
            }
            dirty = true;
        }

        async Task VerifyIdleRefresh(string directory)
        {
            Directory.CreateDirectory(directory);
            await Task.Delay(250);
            var grids = new[] { "Folders", "Files", "Issues", "Comparison" }.Select(name => UI<DataGrid>(name)).ToArray();
            var originalSources = grids.Select(grid => grid.ItemsSource).ToArray();
            var originalCards = driveViews.Values.Select(view => view.Button).ToArray();
            var originalBrushes = originalCards.Select(card => card.Background).ToArray();
            int sourceChanges = 0, enabledChanges = 0;
            EventHandler sourceChanged = delegate { sourceChanges++; };
            var sourceDescriptor = DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid));
            foreach (var grid in grids) sourceDescriptor.AddValueChanged(grid, sourceChanged);
            EventHandler enabledChanged = delegate { enabledChanges++; };
            var enabledDescriptor = DependencyPropertyDescriptor.FromProperty(UIElement.IsEnabledProperty, typeof(Button));
            var refreshButton = UI<Button>("RefreshDrives"); enabledDescriptor.AddValueChanged(refreshButton, enabledChanged);
            var comparison = UI<DataGrid>("Comparison");
            comparison.Items.SortDescriptions.Add(new SortDescription("Bytes", ListSortDirection.Descending));
            object selectedRow = comparison.Items.Cast<object>().FirstOrDefault(); comparison.SelectedItem = selectedRow;
            int startTick = tick;
            try { await Task.Delay(10500); }
            finally
            {
                foreach (var grid in grids) sourceDescriptor.RemoveValueChanged(grid, sourceChanged);
                enabledDescriptor.RemoveValueChanged(refreshButton, enabledChanged);
            }
            bool sourcesPreserved = grids.Select((grid, i) => Object.ReferenceEquals(grid.ItemsSource, originalSources[i])).All(value => value);
            bool selectionPreserved = Object.ReferenceEquals(comparison.SelectedItem, selectedRow);
            bool brushesPreserved = originalCards.Select((card, i) => Object.ReferenceEquals(card.Background, originalBrushes[i])).All(value => value);
            var report = new List<string> {
                "Observed timer ticks: " + (tick - startTick),
                "ItemsSource changes while idle: " + sourceChanges,
                "Automatic refresh button enabled changes: " + enabledChanges,
                "ItemsSource references preserved: " + sourcesPreserved,
                "Selected comparison row preserved: " + selectionPreserved,
                "Card brush references preserved: " + brushesPreserved
            };
            File.WriteAllLines(Path.Combine(directory, "refresh-test.txt"), report);
            if (tick - startTick < 8 || sourceChanges != 0 || enabledChanges != 0 || !sourcesPreserved || !selectionPreserved || !brushesPreserved)
                throw new Exception("Idle polling rebuilt the presentation or reset selection.");
            File.AppendAllText(Path.Combine(directory, "refresh-test.txt"), "PASS: idle polling preserves rows, selection and visuals.\n");
            // A forced refresh must also keep row objects, even if it is requested
            // by an active scan. Property notifications still have to reach cells.
            var drive = drives.Targets.OrderBy(s => s.Root).FirstOrDefault();
            if (drive != null)
            {
                var row = comparisonItems.Single(item => item.Name == Catalog.Names[0]);
                var cell = row.Cells[0]; long originalBytes; int notifications = 0;
                PropertyChangedEventHandler changed = delegate(object sender, PropertyChangedEventArgs e) { if (e.PropertyName == "Text") notifications++; };
                cell.PropertyChanged += changed;
                lock (drive.Gate) { originalBytes = drive.Bytes[0]; drive.Bytes[0] += 1024; }
                try
                {
                    dirty = true; Refresh();
                    if (!Object.ReferenceEquals(row, comparisonItems.Single(item => item.Name == Catalog.Names[0])) || !Object.ReferenceEquals(cell, row.Cells[0]) || cell.Bytes != originalBytes + 1024 || notifications != 1)
                        throw new Exception("Incremental size update failed or replaced the row/cell.");
                    var rowReferences = grids.Select(grid => grid.ItemsSource).ToArray(); RefreshDetails();
                    if (!grids.Select((grid, i) => Object.ReferenceEquals(grid.ItemsSource, rowReferences[i])).All(value => value))
                        throw new Exception("Forced refresh replaced an ItemsSource.");
                    File.AppendAllText(Path.Combine(directory, "refresh-test.txt"), "PASS: size updates notify the existing cell without replacing rows or sources.\n");
                }
                finally
                {
                    lock (drive.Gate) drive.Bytes[0] = originalBytes;
                    cell.PropertyChanged -= changed; dirty = true; Refresh();
                }
            }
        }
    }
}
