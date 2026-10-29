using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;

namespace DriveScope
{
    public sealed partial class MainController
    {
        async Task VerifyPresentation(string directory)
        {
            Directory.CreateDirectory(directory); var report = new List<string>();
            Action<bool, string> check = delegate(bool condition, string message) {
                if (!condition) throw new Exception(message);
                report.Add("PASS: " + message); File.WriteAllLines(Path.Combine(directory, "presentation-test.txt"), report);
            };
            check(verificationMode && states.All(s => !s.Cached), "presentation fixtures do not load real drive caches or save preferences");
            check(window.Width <= SystemParameters.WorkArea.Width && window.Height <= SystemParameters.WorkArea.Height, "startup window fits the usable desktop area");
            var target = states.First();
            string parentPath = Path.Combine(target.Root, "PresentationFixture"), childPath = Path.Combine(parentPath, "Nested");
            lock (target.Gate)
            {
                target.Bytes = new long[Catalog.Names.Length]; target.Bytes[0] = 4096;
                target.Counts = new long[Catalog.Names.Length]; target.Counts[0] = 1; target.Files = 1;
                foreach (string path in new[] { target.Root, parentPath, childPath })
                {
                    var record = new FolderRecord { Path = path, Parent = Scanner.ParentOf(path) }; record.Bytes[0] = 4096; target.Folders[path] = record;
                }
                target.Largest[0].Add(new FileRecord { Path = Path.Combine(childPath, "sample.bin"), Bytes = 4096, Category = 0 });
                target.Issues.Add(parentPath + " — テスト用の読取エラー");
            }
            driveViews[target].Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var folders = UI<DataGrid>("Folders");
            var first = folderRows.Single(); folders.SelectedItem = first;
            folders.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            check(folder == "", "double clicking empty grid space does not activate a previous selection");
            folders.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            check(folder == parentPath && folderRows.Single().Path == childPath, "Enter opens the selected folder and lists its direct children");
            UI<Button>("Up").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(folder == target.Root && folderRows.Single().Path == parentPath, "up navigation returns to the parent folder");
            NavigateUp(); check(folder == "", "up navigation returns from the drive root to its overview");
            folders.SelectedItem = folderRows.Single(); folders.ScrollIntoView(folders.SelectedItem); window.UpdateLayout();
            var row = folders.ItemContainerGenerator.ContainerFromItem(folders.SelectedItem) as DataGridRow;
            check(row != null && (string)row.ToolTip == parentPath, "folder rows expose the complete path in a tooltip");
            folders.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = row });
            check(folder == parentPath, "double clicking a folder row opens that row"); NavigateUp(); NavigateUp();
            int beforeSearch = folderRows.Count;
            UI<TextBox>("Search").Text = "Presentation"; UI<TextBox>("Search").Text = "__no_fixture_match__";
            check(folderRows.Count == beforeSearch, "typing does not rebuild the folder list on every keystroke");
            await Task.Delay(350);
            check(folderRows.Count == 0 && fileRows.Count == 0, "debounced filtering applies the latest query");
            SetSearch(""); check(folderRows.Count == beforeSearch && fileRows.Count == 1, "clearing the filter restores fixture rows");
            check(!String.IsNullOrEmpty(FrameworkElementAutomationPeer.CreatePeerForElement(UI<TextBox>("Search")).GetName()), "search has an accessible name");
            check(driveViews.Select(pair => FrameworkElementAutomationPeer.CreatePeerForElement(pair.Value.Choice).GetName()).Distinct().Count() == driveViews.Count, "drive checkboxes have distinct accessible names");
            check(categoryViews.All(pair => !String.IsNullOrEmpty(FrameworkElementAutomationPeer.CreatePeerForElement(pair.Value.Item).GetName())), "category choices have accessible names");
            check(new[] { "Folders", "Files", "Issues", "Comparison" }.All(name => !String.IsNullOrEmpty(AutomationProperties.GetName(UI<DataGrid>(name)))), "all result grids have accessible names");
            check(UI<Button>("Scan").Template.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty), "buttons provide a visible keyboard focus trigger");
            check(folders.CellStyle.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusWithinProperty), "grid cells provide a visible keyboard focus trigger");
            UI<TabControl>("Tabs").SelectedIndex = 2;
            check(!UI<Button>("Reveal").IsEnabled, "the issue tab cannot reveal an unrelated folder selection");
            UI<TabControl>("Tabs").SelectedIndex = 0;
            folders.SelectedItem = folderRows.Single();
            check(UI<Button>("Reveal").IsEnabled, "selecting a folder enables revealing its location");
            target.Cancel = new System.Threading.CancellationTokenSource(); target.Running = true; target.Saving = true; Refresh();
            UI<Button>("Stop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(target.Cancel.IsCancellationRequested && !UI<Button>("Stop").IsEnabled && UI<TextBlock>("ScanStatus").Text.Contains("中断中"), "stop immediately acknowledges cancellation and disables repeat requests");
            target.Running = false; Refresh();
            check(UI<TextBlock>("ScanStatus").Text.Contains("保存中"), "saving is displayed separately from scanning");
            target.Saving = false; target.Cancel.Dispose(); target.Cancel = null;
            feedbackMessage = "テスト用CSV出力完了"; feedbackUntil = DateTime.UtcNow.AddSeconds(10); Refresh(); Refresh();
            check(UI<TextBlock>("ScanStatus").Text == feedbackMessage, "export feedback survives periodic refreshes"); feedbackUntil = DateTime.MinValue;
            preferencesError = "テスト用設定保存エラー"; inventoryError = ""; Refresh();
            check(UI<TextBlock>("ScanStatus").Text == preferencesError && UI<TextBlock>("ScanStatus").ToolTip.ToString().Contains(preferencesError), "preference errors remain visible with full tooltip details"); preferencesError = "";
            target.Status = "保存失敗"; Refresh();
            check(UI<TextBlock>("ScanStatus").Text.Contains("保存できませんでした"), "save failures do not claim that results were saved"); target.Status = "サンプル";
            string invalidCache = Path.Combine(directory, "invalid-fixture.scan.gz"); File.WriteAllText(invalidCache, "invalid fixture");
            await LoadCache(target, invalidCache); Refresh();
            check(target.Files == 1 && UI<TextBlock>("ScanStatus").Text.Contains("再スキャン"), "invalid caches preserve current data and show a recoverable error"); target.Status = "サンプル";
            window.Width = 960; window.Height = 720; window.UpdateLayout(); await Task.Delay(100); dirty = true; Refresh(); window.UpdateLayout();
            foreach (var name in new[] { "RefreshDrives", "Scan", "ScanSelected", "Search", "Tabs", "Reveal" })
            {
                var element = UI<FrameworkElement>(name); element.BringIntoView(); window.UpdateLayout();
                var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                check(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= window.ActualWidth + 1 && bounds.Bottom <= window.ActualHeight + 1, name + " can be brought into view in a 960 by 720 window");
            }
            Capture(Path.Combine(directory, "compact-presentation.png"));
            check(folders.ActualHeight >= 80, "compact layout retains usable result rows (actual height " + folders.ActualHeight + ")");
            check(!Double.IsNaN(UI<Grid>("ContentGrid").Height) && !Double.IsInfinity(UI<Grid>("ContentGrid").Height) && VirtualizingStackPanel.GetIsVirtualizing(folders), "scrolling the compact layout preserves a finite grid and row virtualization");
            window.Width = 920; window.Height = 600; window.UpdateLayout(); await Task.Delay(100);
            UI<Button>("Reveal").BringIntoView(); window.UpdateLayout();
            check(folders.ActualHeight >= 80 && UI<ScrollViewer>("MainScroll").ScrollableHeight > 0, "minimum window size retains usable rows through vertical scrolling");
            Capture(Path.Combine(directory, "minimum-presentation.png"));
            UI<Button>("Overview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            timer.Start();
            try { await VerifyIdleRefresh(Path.Combine(directory, "idle")); }
            finally { timer.Stop(); }
            check(true, "synthetic idle refresh preserves rows, sorting, selection and brushes");
            File.AppendAllText(Path.Combine(directory, "presentation-test.txt"), "ALL PRESENTATION CHECKS PASSED\n");
        }

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
