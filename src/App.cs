using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DriveScope
{
    sealed class CategoryView
    {
        public ListBoxItem Item; public TextBlock Size, Count; public Border Bar, Track;
    }
    sealed class DriveView
    {
        public Button Button; public CheckBox Choice; public TextBlock Label, Used, Free, Status, Delta; public Border Bar, Track;
    }
    public sealed partial class MainController
    {
        readonly Window window;
        readonly List<ScanState> states;
        readonly DriveSelection preferences = new DriveSelection();
        readonly DriveSet drives;
        readonly string preferencesPath = Path.Combine(Store.DirectoryPath, "selection.txt");
        bool discovering, verificationMode, inventoryInitialized;
        readonly ObservableCollection<DisplayRow> folderItems = new ObservableCollection<DisplayRow>();
        readonly ObservableCollection<DisplayRow> fileItems = new ObservableCollection<DisplayRow>();
        readonly ObservableCollection<DisplayRow> issueItems = new ObservableCollection<DisplayRow>();
        readonly ObservableCollection<ComparisonRow> comparisonItems = new ObservableCollection<ComparisonRow>();
        string comparisonSignature = "", inventoryError = "", preferencesError = "", feedbackMessage = "";
        DateTime feedbackUntil;
        readonly List<Task> workers = new List<Task>();
        readonly Dictionary<ScanState, DriveView> driveViews = new Dictionary<ScanState, DriveView>();
        readonly Dictionary<int, CategoryView> categoryViews = new Dictionary<int, CategoryView>();
        readonly DispatcherTimer timer = new DispatcherTimer();
        readonly DispatcherTimer searchTimer = new DispatcherTimer();
        ScanState selected; int category = -1; string folder = ""; bool loading = true, closing;
        int tick; bool dirty = true;
        List<DisplayRow> folderRows = new List<DisplayRow>(), fileRows = new List<DisplayRow>(), issueRows = new List<DisplayRow>();
        public Window Window { get { return window; } }
        public bool VerificationFailed { get; private set; }
        T UI<T>(string name) where T : FrameworkElement { return (T)window.FindName(name); }
        static readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>();
        static SolidColorBrush Brush(string value)
        {
            SolidColorBrush brush;
            if (!brushes.TryGetValue(value, out brush)) { brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value); brush.Freeze(); brushes[value] = brush; }
            return brush;
        }
        static TextBlock Text(string text, double size, string color) { return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color) }; }

        public MainController(bool autoScan, string screenshot, string liveVerification = null, string uiVerification = null, string refreshVerification = null, bool documentation = false, string presentationVerification = null)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
            FitWindowToWorkArea();
            UI<ScrollViewer>("MainScroll").SizeChanged += delegate {
                // Keep the grid's measure finite so nested DataGrids retain row
                // virtualization, while short desktops can scroll the full layout.
                UI<Grid>("ContentGrid").Height = Math.Max(780, UI<ScrollViewer>("MainScroll").ActualHeight - 44);
            };
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Branding.Icon"))
            {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                window.Icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
            }
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Branding.Logo"))
            {
                var logo = new BitmapImage(); logo.BeginInit(); logo.CacheOption = BitmapCacheOption.OnLoad; logo.StreamSource = stream; logo.EndInit(); logo.Freeze();
                UI<Image>("BrandLogo").Source = logo;
            }
            UI<DataGrid>("Folders").ItemsSource = folderItems;
            UI<DataGrid>("Files").ItemsSource = fileItems;
            UI<DataGrid>("Issues").ItemsSource = issueItems;
            UI<DataGrid>("Comparison").ItemsSource = comparisonItems;
            AutomationProperties.SetName(UI<ListBox>("Categories"), "使用量のカテゴリ");
            foreach (var name in new[] { "Folders", "Files", "Issues", "Comparison" })
            {
                var grid = UI<DataGrid>(name);
                AutomationProperties.SetName(grid, name == "Folders" ? "フォルダの使用量" : name == "Files" ? "大きいファイル" : name == "Issues" ? "除外・読取エラー" : "カテゴリ別のドライブ比較");
                grid.SelectionChanged += delegate { UpdateRevealAvailability(); };
            }
            foreach (var source in new object[] { folderItems, fileItems, issueItems, comparisonItems })
            {
                var live = CollectionViewSource.GetDefaultView(source) as ICollectionViewLiveShaping;
                if (live != null && live.CanChangeLiveSorting) live.IsLiveSorting = true;
            }
            verificationMode = uiVerification != null || documentation || presentationVerification != null;
            if (!documentation && presentationVerification == null) preferences.Load(preferencesPath); drives = new DriveSet(preferences); states = drives.States;
            UI<Button>("RefreshDrives").Click += async delegate { await DiscoverDrives(true); };
            AddCategory(-1); for (int i = 0; i < Catalog.Names.Length; i++) AddCategory(i);
            UI<ListBox>("Categories").SelectedIndex = 0;
            UI<ListBox>("Categories").SelectionChanged += delegate {
                var item = UI<ListBox>("Categories").SelectedItem as ListBoxItem; if (item == null) return;
                category = (int)item.Tag; if (category >= 0 && UI<TabControl>("Tabs").SelectedIndex == 3) UI<TabControl>("Tabs").SelectedIndex = 0; dirty = true; Refresh();
            };
            UI<Button>("Overview").Click += delegate { selected = null; folder = ""; UI<TabControl>("Tabs").SelectedIndex = 3; dirty = true; Refresh(); };
            UI<TabControl>("Tabs").SelectionChanged += delegate(object sender, SelectionChangedEventArgs e) {
                if (e.Source != UI<TabControl>("Tabs")) return;
                if (UI<TabControl>("Tabs").SelectedIndex == 3) { selected = null; folder = ""; UI<ListBox>("Categories").SelectedIndex = 0; }
                dirty = true; Refresh();
            };
            UI<Button>("Scan").Click += delegate { Start(drives.Targets); };
            UI<Button>("ScanSelected").Click += delegate { Start(selected == null ? drives.Targets : new[] { selected }); };
            UI<Button>("Stop").Click += delegate { foreach (var s in states) if (s.Cancel != null) s.Cancel.Cancel(); Refresh(); };
            UI<Button>("Up").Click += delegate { NavigateUp(); };
            searchTimer.Interval = TimeSpan.FromMilliseconds(200);
            searchTimer.Tick += delegate { searchTimer.Stop(); dirty = true; RefreshDetails(); };
            UI<TextBox>("Search").TextChanged += delegate { searchTimer.Stop(); searchTimer.Start(); };
            UI<DataGrid>("Folders").MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e) {
                var row = ItemsControl.ContainerFromElement(UI<DataGrid>("Folders"), e.OriginalSource as DependencyObject) as DataGridRow;
                if (row != null) { OpenFolder(row.Item as DisplayRow); e.Handled = true; }
            };
            UI<DataGrid>("Folders").PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { OpenFolder(UI<DataGrid>("Folders").SelectedItem as DisplayRow); e.Handled = true; }
            };
            UI<DataGrid>("Files").MouseDoubleClick += delegate(object sender, MouseButtonEventArgs e) {
                if (ItemsControl.ContainerFromElement(UI<DataGrid>("Files"), e.OriginalSource as DependencyObject) is DataGridRow) { Reveal(); e.Handled = true; }
            };
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
                if ((e.Key == Key.Up || e.SystemKey == Key.Up) && (Keyboard.Modifiers & ModifierKeys.Alt) != 0 && folder.Length > 0) { NavigateUp(); e.Handled = true; }
            };
            UI<Button>("Reveal").Click += delegate { Reveal(); };
            UI<Button>("Export").Click += delegate { Export(); };
            window.Closing += OnClosing;
            timer.Interval = TimeSpan.FromSeconds(1); timer.Tick += delegate {
                tick++; Refresh(); if (!loading && tick % 3 == 0) PollDrives();
                if (!String.IsNullOrEmpty(liveVerification) && !loading)
                {
                    if (tick % 5 == 0) WriteDiagnostics(liveVerification);
                    if (states.All(s => !s.Running && !s.Saving) && states.Any(s => s.Files > 0))
                    {
                        WriteDiagnostics(liveVerification); Capture(Path.Combine(liveVerification, "live-ui.png"));
                        liveVerification = null; window.Close();
                    }
                }
            }; timer.Start();
            Refresh();
            window.ContentRendered += async delegate {
                if (documentation || presentationVerification != null) { LoadDocumentationSample(); timer.Stop(); }
                else await DiscoverDrives();
                loading = false; dirty = true; Refresh();
                if (presentationVerification != null)
                {
                    try { await VerifyPresentation(presentationVerification); }
                    catch (Exception ex) { VerificationFailed = true; File.AppendAllText(Path.Combine(presentationVerification, "presentation-test.txt"), "\nFAIL: " + ex); }
                    window.Close(); return;
                }
                if (autoScan) Start(drives.Targets);
                if (!String.IsNullOrEmpty(refreshVerification))
                {
                    try { await VerifyIdleRefresh(refreshVerification); }
                    catch (Exception ex) { VerificationFailed = true; File.AppendAllText(Path.Combine(refreshVerification, "refresh-test.txt"), "\nFAIL: " + ex); }
                    window.Close(); return;
                }
                if (!String.IsNullOrEmpty(uiVerification))
                {
                    try { VerifyUi(uiVerification); }
                    catch (Exception ex) { VerificationFailed = true; File.WriteAllText(Path.Combine(uiVerification, "ui-test.txt"), "FAIL: " + ex); }
                    window.Close(); return;
                }
                if (!String.IsNullOrEmpty(screenshot))
                {
                    await Task.Delay(500); window.UpdateLayout();
                    if (documentation) UI<TextBlock>("ScanStatus").Text = "表示例：サンプルデータ（実際のPCの情報ではありません）";
                    Capture(screenshot);
                    window.Close();
                }
            };
        }
        void FitWindowToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            window.MinWidth = Math.Min(window.MinWidth, area.Width); window.MinHeight = Math.Min(window.MinHeight, area.Height);
            window.Width = Math.Min(window.Width, area.Width); window.Height = Math.Min(window.Height, area.Height);
        }
        void OpenFolder(DisplayRow row)
        {
            if (row == null || String.IsNullOrEmpty(row.Path)) return;
            folder = row.Path; dirty = true; Refresh();
        }
        void NavigateUp()
        {
            if (!String.IsNullOrEmpty(folder)) folder = selected != null && folder.Equals(selected.Root, StringComparison.OrdinalIgnoreCase) ? "" : Scanner.ParentOf(folder);
            dirty = true; Refresh();
        }
        void SetSearch(string query)
        {
            UI<TextBox>("Search").Text = query; searchTimer.Stop(); dirty = true; RefreshDetails();
        }
        void UpdateRevealAvailability()
        {
            int tab = UI<TabControl>("Tabs").SelectedIndex;
            var row = tab == 0 ? UI<DataGrid>("Folders").SelectedItem as DisplayRow : tab == 1 ? UI<DataGrid>("Files").SelectedItem as DisplayRow : null;
            UI<Button>("Reveal").IsEnabled = (tab == 0 || tab == 1) && (row != null && !String.IsNullOrEmpty(row.Path) || folder.Length > 0);
        }
        void VerifyUi(string directory)
        {
            Directory.CreateDirectory(directory); var log = new List<string>();
            Action<bool, string> check = delegate(bool ok, string name) { if (!ok) throw new Exception(name); log.Add("PASS: " + name); };
            check(states.Any(s => s.Cached && s.Files > 0), "real drive cache loaded on startup");
            var target = states.Where(s => s.Files > 0).OrderByDescending(s => s.Files).First();
            driveViews[target].Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(selected == target && folderRows.Count > 0, "drive card selects drive and populates folders");
            int cat = Array.FindIndex(target.Bytes, b => b > 0);
            UI<ListBox>("Categories").SelectedIndex = cat + 1;
            check(category == cat && UI<TextBlock>("DetailSize").Text == Catalog.Size(target.Bytes[cat]), "category selection updates correct drive total");
            UI<ListBox>("Categories").SelectedIndex = 0;
            var grid = UI<DataGrid>("Folders"); var chosen = folderRows.First(); grid.SelectedItem = chosen;
            grid.ScrollIntoView(chosen); window.UpdateLayout();
            var chosenContainer = grid.ItemContainerGenerator.ContainerFromItem(chosen) as DataGridRow;
            check(chosenContainer != null, "selected folder has a visible row");
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = chosenContainer });
            check(folder == chosen.Path, "folder row double click navigates to selected folder");
            UI<Button>("Up").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(String.Equals(folder, target.Root, StringComparison.OrdinalIgnoreCase), "up button returns to parent");
            UI<Button>("Up").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); check(folder == "", "up button returns to drive root");
            string query = folderRows.First().Name; SetSearch(query);
            check(folderRows.Count > 0 && folderRows.All(r => r.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || r.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0), "search filters folder names and paths");
            SetSearch("__drivescope_no_matching_path_84e395__");
            check(folderRows.Count == 0 && fileRows.Count == 0, "empty search result works");
            SetSearch(""); check(folderRows.Count > 0 && fileRows.Count > 0, "clearing search restores data");
            UI<TabControl>("Tabs").SelectedIndex = 1;
            check(UI<DataGrid>("Files").Items.Count == fileRows.Count && fileRows.Count <= 300, "large-file tab contains bounded ranked files");
            grid.Items.SortDescriptions.Add(new SortDescription("Bytes", ListSortDirection.Ascending));
            dirty = true; RefreshDetails();
            check(grid.Items.SortDescriptions.Any(s => s.PropertyName == "Bytes"), "folder sorting survives refresh");
            UI<TabControl>("Tabs").SelectedIndex = 0; grid.Items.SortDescriptions.Clear();
            UI<Button>("Overview").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(selected == null && folder == "" && UI<TextBlock>("DetailSize").Text == Catalog.Size(drives.Targets.Sum(s => s.Total)), "overview combines checked connected drives");
            var comparisons = UI<DataGrid>("Comparison").Items.Cast<ComparisonRow>().ToList();
            check(comparisons.Count == Catalog.Names.Length && comparisons.Sum(r => r.Bytes) == drives.Targets.Sum(s => s.Total), "comparison table shows every category with dynamic drive totals");
            driveViews[target].Choice.IsChecked = false;
            check(!drives.Targets.Contains(target) && UI<DataGrid>("Comparison").Columns.Count == drives.Targets.Count() + 2, "unchecking drive removes its comparison column and total");
            foreach (var state in states.Where(s => s.Present)) driveViews[state].Choice.IsChecked = false;
            check(!drives.Targets.Any() && UI<TextBlock>("DetailSize").Text == Catalog.Size(0) && !UI<Button>("Scan").IsEnabled, "zero checked drives yields empty total and disables scan");
            foreach (var state in states.Where(s => s.Present && s.Ready)) driveViews[state].Choice.IsChecked = true;
            check(UI<DataGrid>("Comparison").Columns.Count == drives.Targets.Count() + 2, "checking drives rebuilds comparison columns");
            var comparisonGrid = UI<DataGrid>("Comparison");
            comparisonGrid.Items.SortDescriptions.Add(new SortDescription("Cells[0].Bytes", ListSortDirection.Ascending));
            var sorted = comparisonGrid.Items.Cast<ComparisonRow>().Select(r => r.Cells[0].Bytes).ToArray();
            check(sorted.SequenceEqual(sorted.OrderBy(b => b)), "dynamic drive column sorts by numeric bytes"); comparisonGrid.Items.SortDescriptions.Clear();
            var realInventory = states.Where(s => s.Present).Select(s => new DriveDescriptor { Root = s.Root, Label = s.Label, Identity = s.VolumeIdentity, Ready = s.Ready, DefaultIncluded = s.Included, Kind = s.Kind, Capacity = s.Capacity, Free = s.Free }).ToArray();
            var simulated = new[] { "B:\\", "D:\\", "G:\\", "M:\\", "Z:\\" }.Select(r => new DriveDescriptor { Root = r, Label = r + " テスト用", Identity = "ui-test-" + r, Ready = true, DefaultIncluded = true, Kind = "テスト", Capacity = 1000, Free = 500 }).ToArray();
            ApplyInventory(simulated.Take(1)); Refresh();
            check(driveViews.Count == 1 && comparisonGrid.Columns.Count == 3, "one arbitrary attached drive renders a card and column");
            ApplyInventory(simulated); Refresh();
            check(driveViews.Count == 5 && comparisonGrid.Columns.Count == 7, "five attached drives grow cards and comparison columns");
            ApplyInventory(simulated.Take(2)); Refresh();
            check(driveViews.Count == 2 && comparisonGrid.Columns.Count == 4, "removing drives shrinks visible cards and columns");
            ApplyInventory(new DriveDescriptor[0]); Refresh();
            check(driveViews.Count == 0 && comparisonGrid.Columns.Count == 2 && UI<TextBlock>("NoDrives").Visibility == Visibility.Visible && !UI<Button>("Scan").IsEnabled, "zero attached drives renders the empty state");
            ApplyInventory(realInventory); Refresh();
            check(drives.Targets.Contains(target) && target.Cached && target.Files > 0, "restoring real disks preserves their cached results");
            Capture(Path.Combine(directory, "overview-ui.png"));
            driveViews[target].Button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            UI<ListBox>("Categories").SelectedIndex = 1; Capture(Path.Combine(directory, "unity-ui.png"));
            File.WriteAllLines(Path.Combine(directory, "ui-test.txt"), log.Concat(new[] { "ALL UI CHECKS PASSED" }));
        }
        void Capture(string path)
        {
            window.UpdateLayout();
            var client = VisualTreeHelper.GetChildrenCount(window) > 0 ? VisualTreeHelper.GetChild(window, 0) as FrameworkElement : null;
            if (client == null) client = window;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(client.ActualWidth), (int)Math.Ceiling(client.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(client); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
        }
        void WriteDiagnostics(string directory)
        {
            Directory.CreateDirectory(directory); var lines = new List<string>();
            foreach (var s in states) lock (s.Gate)
            {
                lines.Add(s.Root + " " + s.Status + " running=" + s.Running + " saving=" + s.Saving + " files=" + s.Files + " bytes=" + s.Total + " folders=" + s.Folders.Count + " skipped=" + s.Skipped + " links=" + s.Links);
                lines.Add("Folder byte sum matches root: " + (s.Folders.ContainsKey(s.Root) && s.Folders[s.Root].Total == s.Total));
                for (int i = 0; i < s.Bytes.Length; i++) lines.Add("  " + Catalog.Names[i] + ": " + Catalog.Size(s.Bytes[i]));
            }
            File.WriteAllLines(Path.Combine(directory, "live-scan.txt"), lines);
        }
        async void PollDrives() { await DiscoverDrives(); }
        async Task DiscoverDrives(bool manual = false)
        {
            if (discovering || closing || verificationMode && !loading) return;
            discovering = true; if (manual) UI<Button>("RefreshDrives").IsEnabled = false;
            try
            {
                var inventory = await Task.Run(() => DriveInventory.Read());
                if (closing) return;
                var added = ApplyInventory(inventory);
                foreach (var s in added.Where(s => s.Ready)) s.LoadingCache = true;
                inventoryError = ""; Refresh();
                if (added.Any(s => s.Ready))
                {
                    await Task.WhenAll(added.Where(s => s.Ready).Select(s => LoadCache(s)).ToArray());
                    dirty = true; Refresh();
                }
            }
            catch (Exception ex) { inventoryError = "ドライブ一覧を更新できません: " + ex.Message; }
            finally { discovering = false; if (manual && !closing) UI<Button>("RefreshDrives").IsEnabled = true; }
        }
        async Task LoadCache(ScanState state, string overridePath = null)
        {
            try
            {
                await Task.Run(delegate {
                    string path = overridePath ?? Store.FileName(state);
                    bool existed = path != null && File.Exists(path);
                    if (!Store.Load(state, path) && existed)
                    {
                        lock (state.Gate)
                        {
                            state.Status = "保存結果が無効・再スキャンしてください";
                            if (state.Issues.Count < 1000) state.Issues.Add("保存結果を読み込めませんでした。対象ドライブを再スキャンしてください。");
                        }
                    }
                });
            }
            finally { state.LoadingCache = false; }
        }
        List<ScanState> ApplyInventory(IEnumerable<DriveDescriptor> inventory)
        {
            var before = states.Where(s => s.Present).ToArray();
            string beforeTargets = InventoryPresentationKey();
            var added = drives.Apply(inventory);
            bool structureChanged = !inventoryInitialized || !before.SequenceEqual(states.Where(s => s.Present));
            if (structureChanged) RebuildDriveCards();
            inventoryInitialized = true;
            if (selected != null && (!selected.Present || !selected.Ready)) { selected = null; folder = ""; dirty = true; }
            if (structureChanged || beforeTargets != InventoryPresentationKey()) dirty = true;
            return added;
        }
        string InventoryPresentationKey()
        {
            return String.Join("|", states.Where(s => s.Present).Select(s => s.Root + s.VolumeIdentity + s.Ready + s.Included));
        }
        void SetIncluded(ScanState state, bool included)
        {
            if (state.Included == included) return;
            state.Included = included; preferences.Choices[state.Root] = included;
            if (!included)
            {
                if (state.Cancel != null) state.Cancel.Cancel();
                if (selected == state) { selected = null; folder = ""; }
            }
            if (!verificationMode)
                try { preferences.Save(preferencesPath); preferencesError = ""; }
                catch (Exception ex) { preferencesError = "選択を保存できません: " + ex.Message; }
            dirty = true; Refresh();
        }
        void RebuildDriveCards()
        {
            UI<StackPanel>("DriveCards").Children.Clear(); driveViews.Clear();
            foreach (var state in states.Where(s => s.Present).OrderBy(s => s.Root)) AddDriveCard(state);
            UI<TextBlock>("NoDrives").Visibility = driveViews.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        void AddDriveCard(ScanState state)
        {
            var view = new DriveView();
            var grid = new Grid { Margin = new Thickness(2), Width = 348, MinHeight = 118 };
            for (int i = 0; i < 5; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var title = new Grid(); title.ColumnDefinitions.Add(new ColumnDefinition()); title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            view.Label = Text(state.Label, 16, "#E9EDF5"); view.Label.FontWeight = FontWeights.SemiBold; view.Label.TextTrimming = TextTrimming.CharacterEllipsis; title.Children.Add(view.Label);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            view.Choice = new CheckBox { Content = "対象", IsChecked = state.Included, Foreground = Brush("#8DE1C9"), Margin = new Thickness(8, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "スキャンと比較の対象に含める" };
            AutomationProperties.SetName(view.Choice, state.Label + " をスキャンと比較の対象に含める");
            view.Choice.Checked += delegate { SetIncluded(state, true); }; view.Choice.Unchecked += delegate { SetIncluded(state, false); };
            view.Choice.Click += delegate(object sender, RoutedEventArgs e) { e.Handled = true; };
            right.Children.Add(view.Choice);
            view.Status = Text("", 10, "#A3AEC2"); view.Status.MaxWidth = 110; view.Status.TextTrimming = TextTrimming.CharacterEllipsis; right.Children.Add(view.Status); Grid.SetColumn(right, 1); title.Children.Add(right); grid.Children.Add(title);
            view.Used = Text("—", 25, "#E9EDF5"); view.Used.Margin = new Thickness(0, 12, 0, 7); Grid.SetRow(view.Used, 1); grid.Children.Add(view.Used);
            view.Track = new Border { Height = 6, Background = Brush("#31394B"), CornerRadius = new CornerRadius(3) };
            view.Bar = new Border { Height = 6, Background = Brush("#8DE1C9"), CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 }; view.Track.Child = view.Bar;
            view.Track.SizeChanged += delegate { view.Bar.Width = state.Ready && state.Capacity > 0 ? Math.Max(0, view.Track.ActualWidth * Math.Min(1, (double)(state.Capacity - state.Free) / state.Capacity)) : 0; };
            Grid.SetRow(view.Track, 2); grid.Children.Add(view.Track);
            view.Free = Text("", 11, "#A3AEC2"); view.Free.Margin = new Thickness(0, 8, 0, 0); Grid.SetRow(view.Free, 3); grid.Children.Add(view.Free);
            view.Delta = Text("", 10, "#8795AD"); view.Delta.Margin = new Thickness(0, 4, 0, 0); Grid.SetRow(view.Delta, 4); grid.Children.Add(view.Delta);
            view.Button = new Button { Content = grid, Width = 392, Margin = new Thickness(5, 0, 5, 0), Padding = new Thickness(18, 15, 18, 13), Background = Brush("#191F2C") };
            AutomationProperties.SetName(view.Button, state.Label + " の内訳を表示");
            view.Button.Click += delegate { if (!state.Ready) return; selected = state; folder = ""; UI<TabControl>("Tabs").SelectedIndex = 0; dirty = true; Refresh(); };
            driveViews[state] = view; UI<StackPanel>("DriveCards").Children.Add(view.Button);
        }
        void AddCategory(int id)
        {
            var v = new CategoryView(); var panel = new Grid(); panel.ColumnDefinitions.Add(new ColumnDefinition()); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = Text(id < 0 ? "すべて" : Catalog.Names[id], 12, "#E9EDF5"); name.TextTrimming = TextTrimming.CharacterEllipsis; panel.Children.Add(name);
            v.Size = Text("—", 12, id < 0 ? "#8DE1C9" : Catalog.Colors[id]); v.Size.FontWeight = FontWeights.SemiBold; Grid.SetColumn(v.Size, 1); panel.Children.Add(v.Size);
            var track = new Border { Height = 2, Background = Brush("#2B3343"), Margin = new Thickness(0, 2, 0, 0), CornerRadius = new CornerRadius(2) }; v.Track = track;
            v.Bar = new Border { Width = 0, HorizontalAlignment = HorizontalAlignment.Left, Background = Brush(id < 0 ? "#8DE1C9" : Catalog.Colors[id]), CornerRadius = new CornerRadius(2) }; track.Child = v.Bar;
            Grid.SetRow(track, 1); Grid.SetColumnSpan(track, 2); panel.Children.Add(track);
            v.Count = Text("", 10, "#A3AEC2");
            v.Item = new ListBoxItem { Content = panel, Tag = id }; v.Item.ToolTip = v.Count;
            AutomationProperties.SetName(v.Item, id < 0 ? "すべてのカテゴリ" : Catalog.Names[id]);
            categoryViews[id] = v; UI<ListBox>("Categories").Items.Add(v.Item);
        }
        void Start(IEnumerable<ScanState> targets)
        {
            if (loading || closing) return;
            foreach (var s in targets)
            {
                if (s.Running || s.Saving || s.LoadingCache || !s.Present || !s.Ready || !s.Included) continue;
                if (!Directory.Exists(s.Root)) { s.Status = "未接続"; continue; }
                s.Cancel = new CancellationTokenSource(); s.Running = true; s.Saving = true; var state = s; var token = s.Cancel.Token;
                var task = Task.Run(delegate {
                    Scanner.Run(state, token);
                    if (!state.Present || !state.Ready) { state.Complete = false; state.Status = "取り外し・途中結果"; }
                    try { Store.Save(state); }
                    catch (Exception ex) { lock (state.Gate) { state.Status = "保存失敗"; state.Issues.Add("結果の保存: " + ex.Message); } }
                    finally { state.Saving = false; }
                    window.Dispatcher.BeginInvoke(new Action(delegate { dirty = true; Refresh(); }));
                }); workers.Add(task);
            }
            dirty = true; Refresh();
        }
        IEnumerable<ScanState> ActiveStates() { return selected == null ? drives.Targets : selected.Present && selected.Ready ? new[] { selected }.AsEnumerable() : Enumerable.Empty<ScanState>(); }
        long[] Totals()
        {
            var bytes = new long[Catalog.Names.Length];
            foreach (var s in ActiveStates()) lock (s.Gate) for (int i = 0; i < bytes.Length; i++) bytes[i] += s.Bytes[i];
            return bytes;
        }
        void Refresh()
        {
            if (closing) return;
            int running = 0; long count = 0; long excluded = 0;
            foreach (var s in states.Where(s => s.Present))
            {
                lock (s.Gate)
                {
                    DriveView v; if (!driveViews.TryGetValue(s, out v)) continue; long used = s.Capacity - s.Free;
                    v.Used.Text = Catalog.Size(used) + " 使用中"; v.Free.Text = "空き " + Catalog.Size(s.Free) + "  /  合計 " + Catalog.Size(s.Capacity);
                    v.Label.Text = s.Label; v.Label.ToolTip = s.Label + " · " + s.Kind;
                    AutomationProperties.SetName(v.Choice, s.Label + " をスキャンと比較の対象に含める");
                    AutomationProperties.SetName(v.Button, s.Label + " の内訳を表示");
                    v.Choice.IsEnabled = s.Ready; v.Choice.IsChecked = s.Included;
                    v.Status.Text = loading || s.LoadingCache ? "読込中" : !s.Ready ? "利用不可" : s.Saving && !s.Running ? "保存中" : s.Running && s.Cancel != null && s.Cancel.IsCancellationRequested ? "中断中" : s.Status;
                    v.Status.ToolTip = v.Status.Text;
                    v.Bar.Width = s.Ready && s.Capacity > 0 ? Math.Max(0, v.Track.ActualWidth * Math.Min(1, (double)used / s.Capacity)) : 0;
                    if (!s.Ready || s.Capacity == 0) { v.Used.Text = "未接続"; v.Free.Text = "ドライブを確認してください"; }
                    v.Button.Background = Brush(s == selected ? "#273649" : "#191F2C");
                    if (s.Files > 0)
                    {
                        long delta = used - s.Total;
                        string deltaText = delta >= 0 ? "差分 " + Catalog.Size(delta) : "集計が実使用量より " + Catalog.Size(-delta) + " 大きい";
                        v.Delta.Text = "集計 " + Catalog.Size(s.Total) + " · " + (s.Running ? "走査中" : deltaText);
                        v.Button.ToolTip = (s.Running ? "スキャン開始: " + s.Started.ToString("yyyy/MM/dd HH:mm") : "最終集計: " + s.Finished.ToString("yyyy/MM/dd HH:mm")) + "\n" + s.CurrentPath;
                    }
                    else v.Delta.Text = "カテゴリを調べるにはスキャンしてください";
                    if (s.Included && s.Ready) { count += s.Files; excluded += s.Skipped + s.Links; }
                }
            }
            running = states.Count(s => s.Running || s.Saving);
            bool canScan = drives.Targets.Any(s => !s.LoadingCache && !s.Running && !s.Saving);
            UI<Button>("Scan").IsEnabled = !loading && canScan;
            UI<Button>("ScanSelected").IsEnabled = !loading && (selected == null ? canScan : selected.Included && selected.Present && selected.Ready && !selected.LoadingCache && !selected.Running && !selected.Saving);
            UI<Button>("Stop").IsEnabled = states.Any(s => s.Running && s.Cancel != null && !s.Cancel.IsCancellationRequested); UI<Button>("Export").IsEnabled = !loading && count > 0;
            if (loading) UI<TextBlock>("ScanStatus").Text = "前回の結果を読み込み中…";
            else if (running > 0)
            {
                string phase = states.Any(s => s.Running && s.Cancel != null && s.Cancel.IsCancellationRequested) ? "中断中" : states.Any(s => s.Running) ? "スキャン中" : "保存中";
                UI<TextBlock>("ScanStatus").Text = String.Format("{0}ドライブを{1}  ·  {2:N0}ファイル  ·  除外／エラー {3:N0}件", running, phase, count, excluded);
            }
            else UI<TextBlock>("ScanStatus").Text = count == 0 ? "対象にチェックを付けてスキャンしてください。" : String.Format("{0:N0}ファイルを集計  ·  除外／エラー {1:N0}件  ·  結果はPC内に保存", count, excluded);
            if (!loading && !drives.Targets.Any()) UI<TextBlock>("ScanStatus").Text = "対象ドライブがありません。接続済みのドライブにチェックを付けてください。";
            if (DateTime.UtcNow < feedbackUntil) UI<TextBlock>("ScanStatus").Text = feedbackMessage;
            if (preferencesError.Length > 0) UI<TextBlock>("ScanStatus").Text = preferencesError;
            if (inventoryError.Length > 0) UI<TextBlock>("ScanStatus").Text = inventoryError;
            var failedSaves = drives.Targets.Where(s => s.Status == "保存失敗").Select(s => s.Root).ToArray();
            if (failedSaves.Length > 0) UI<TextBlock>("ScanStatus").Text = "結果を保存できませんでした: " + String.Join("、", failedSaves) + " · 除外・読取エラーで詳細を確認してください。";
            var invalidCaches = drives.Targets.Where(s => s.Status == "保存結果が無効・再スキャンしてください").Select(s => s.Root).ToArray();
            if (invalidCaches.Length > 0) UI<TextBlock>("ScanStatus").Text = "保存結果を読み込めませんでした: " + String.Join("、", invalidCaches) + " · 対象ドライブを再スキャンしてください。";
            UI<TextBlock>("ScanStatus").ToolTip = UI<TextBlock>("ScanStatus").Text + "\n" + String.Join("\n", states.Where(s => s.Running).Select(s => s.Root + " " + s.CurrentPath));
            var totals = Totals(); long total = totals.Sum();
            foreach (var pair in categoryViews)
            {
                int id = pair.Key; var v = pair.Value; long b = id < 0 ? total : totals[id];
                v.Size.Text = Catalog.Size(b); v.Bar.Width = total == 0 ? 0 : Math.Max(0, v.Track.ActualWidth * b / total);
                long n = 0; foreach (var s in ActiveStates()) lock (s.Gate) n += id < 0 ? s.Files : s.Counts[id];
                v.Count.Text = n.ToString("N0") + "ファイル · " + (total == 0 ? "0%" : ((double)b / total).ToString("P1"));
            }
            UI<TextBlock>("CategorySubtitle").Text = (selected == null ? "チェックしたドライブ" : selected.Root) + " · ファイルサイズ合計";
            bool comparing = UI<TabControl>("Tabs").SelectedIndex == 3;
            UI<FrameworkElement>("SearchControls").Visibility = comparing ? Visibility.Collapsed : Visibility.Visible;
            UI<FrameworkElement>("SearchHint").Visibility = comparing ? Visibility.Collapsed : Visibility.Visible;
            UI<FrameworkElement>("DetailFooter").Visibility = comparing ? Visibility.Collapsed : Visibility.Visible;
            UI<TextBlock>("DetailTitle").Text = comparing ? "カテゴリ × ドライブ" : category < 0 ? "すべてのカテゴリ" : Catalog.Names[category];
            UI<TextBlock>("DetailSize").Text = Catalog.Size(comparing ? drives.Targets.Sum(s => s.Total) : category < 0 ? total : totals[category]);
            UI<TextBlock>("DetailSummary").Text = selected == null ? "各ドライブを選ぶと、その中身を確認できます。" : selected.Running ? "走査中の値です。フォルダを掘り下げて確認できます。" : (selected.Files == 0 ? "スキャンするとフォルダ・ファイルを表示します。" : "集計日時 " + selected.Finished.ToString("yyyy/MM/dd HH:mm") + (selected.Complete ? " · 完了" : " · 途中結果"));
            if (comparing) UI<TextBlock>("DetailSummary").Text = drives.Targets.Count() + "ドライブを比較 · チェックを外すと集計から除外";
            if (dirty || (running > 0 && tick % 4 == 0)) RefreshDetails();
            UpdateRevealAvailability();
        }
        List<DisplayRow> SetRows(DataGrid grid, List<DisplayRow> rows)
        {
            var items = (ObservableCollection<DisplayRow>)grid.ItemsSource;
            RowReconciler.Update(items, rows, row => String.IsNullOrEmpty(row.Path) ? row.Name : row.Path, (current, next) => current.UpdateFrom(next));
            return items.ToList();
        }
        void EnsureComparisonColumns(ScanState[] targets)
        {
            string signature = "columns:" + String.Join("|", targets.Select(s => s.Root + s.VolumeIdentity));
            if (comparisonSignature == signature) return;
            comparisonSignature = signature; var grid = UI<DataGrid>("Comparison"); grid.Items.SortDescriptions.Clear(); grid.Columns.Clear();
            grid.Columns.Add(new DataGridTextColumn { Header = "カテゴリ", Binding = new System.Windows.Data.Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 180 });
            for (int i = 0; i < targets.Length; i++) grid.Columns.Add(new DataGridTextColumn { Header = targets[i].Root.TrimEnd('\\'), Binding = new System.Windows.Data.Binding("Cells[" + i + "].Text"), SortMemberPath = "Cells[" + i + "].Bytes", Width = new DataGridLength(105) });
            grid.Columns.Add(new DataGridTextColumn { Header = "合計", Binding = new System.Windows.Data.Binding("Total"), SortMemberPath = "Bytes", Width = new DataGridLength(110) });
        }
        void RefreshDetails()
        {
            dirty = false; string query = UI<TextBox>("Search").Text.Trim();
            var targets = drives.Targets.OrderBy(s => s.Root).ToArray();
            EnsureComparisonColumns(targets);
            var comparisons = new List<ComparisonRow>();
            for (int i = 0; i < Catalog.Names.Length; i++)
            {
                var row = new ComparisonRow { Name = Catalog.Names[i], Cells = new List<SizeCell>() };
                foreach (var state in targets) lock (state.Gate) row.Cells.Add(new SizeCell { Bytes = state.Bytes[i] });
                comparisons.Add(row);
            }
            RowReconciler.Update(comparisonItems, comparisons.OrderByDescending(row => row.Bytes), row => row.Name, (current, next) => current.UpdateFrom(next));
            folderRows.Clear(); fileRows.Clear(); issueRows.Clear(); long parentBytes = 0;
            foreach (var s in ActiveStates())
            {
                lock (s.Gate)
                {
                    if (folder.Length > 0 && !folder.StartsWith(s.Root, StringComparison.OrdinalIgnoreCase)) continue;
                    string parent = folder.Length > 0 ? folder : s.Root;
                    FolderRecord parentRecord; if (s.Folders.TryGetValue(parent, out parentRecord)) parentBytes += category < 0 ? parentRecord.Total : parentRecord.Bytes[category];
                    foreach (var f in s.GetChildFolders(parent))
                    {
                        long bytes = category < 0 ? f.Total : f.Bytes[category]; if (bytes == 0) continue;
                        string name = Path.GetFileName(f.Path.TrimEnd('\\')); if (selected == null) name = s.Root.Substring(0, 2) + "  " + name;
                        folderRows.Add(new DisplayRow { Name = name, Path = f.Path, Bytes = bytes });
                    }
                    IEnumerable<FileRecord> source = category < 0 ? s.Largest.SelectMany(x => x) : s.Largest[category];
                    foreach (var f in source)
                    {
                        if (folder.Length > 0 && !f.Path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                        fileRows.Add(new DisplayRow { Name = Path.GetFileName(f.Path), Path = f.Path, Bytes = f.Bytes, Category = Catalog.Names[f.Category] });
                    }
                    foreach (var issue in s.Issues) issueRows.Add(new DisplayRow { Name = issue, Path = "" });
                }
            }
            foreach (var r in folderRows) r.Ratio = parentBytes == 0 ? 0 : (double)r.Bytes / parentBytes;
            Func<DisplayRow, bool> filter = r => query.Length == 0 || r.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || r.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            folderRows = folderRows.Where(filter).OrderByDescending(r => r.Bytes).ThenBy(r => r.Path).ToList();
            fileRows = fileRows.Where(filter).OrderByDescending(r => r.Bytes).ThenBy(r => r.Path).Take(300).ToList();
            issueRows = issueRows.Where(filter).ToList();
            folderRows = SetRows(UI<DataGrid>("Folders"), folderRows); fileRows = SetRows(UI<DataGrid>("Files"), fileRows); issueRows = SetRows(UI<DataGrid>("Issues"), issueRows);
            UI<DataGrid>("Folders").ToolTip = "Enter／ダブルクリックで中を表示。Alt+↑で親へ。「エクスプローラーで表示」で場所を表示。";
            UI<DataGrid>("Files").ToolTip = "ドライブごと・カテゴリごとの上位100ファイルから表示。ダブルクリックで場所を表示。";
            UI<TextBlock>("FolderPath").Text = folder.Length > 0 ? folder : selected == null ? "全ドライブのルート" : selected.Root;
            UI<TextBlock>("FolderPath").ToolTip = UI<TextBlock>("FolderPath").Text;
            UI<Button>("Up").IsEnabled = folder.Length > 0;
            UI<TextBlock>("ListNote").Text = "フォルダ " + folderRows.Count.ToString("N0") + "件 · ファイルは各ドライブ・カテゴリの上位100件から抽出";
            UpdateRevealAvailability();
        }
        void Reveal()
        {
            int tab = UI<TabControl>("Tabs").SelectedIndex; if (tab != 0 && tab != 1) return;
            var grid = tab == 1 ? UI<DataGrid>("Files") : UI<DataGrid>("Folders");
            var row = grid.SelectedItem as DisplayRow; string path = row == null ? folder : row.Path;
            if (String.IsNullOrEmpty(path)) return;
            try
            {
                if (!Directory.Exists(path) && !File.Exists(path)) { MessageBox.Show(window, "このパスは現在存在しません。再スキャンしてください。", "DriveScope"); return; }
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + path.Replace("\"", "") + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "場所を開けませんでした"); }
        }
        void Export()
        {
            var dialog = new SaveFileDialog { Filter = "CSVファイル (*.csv)|*.csv", FileName = "DriveScope_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv" };
            if (dialog.ShowDialog(window) != true) return;
            try
            {
                var rows = new List<string[]> { new[] { "種別", "ドライブ", "カテゴリ", "パス", "サイズ（バイト）", "ファイル数", "集計日時", "状態" } };
                foreach (var s in ActiveStates()) lock (s.Gate)
                {
                    string stamp = s.Running ? DateTime.Now.ToString("s") : s.Finished.ToString("s");
                    for (int i = 0; i < s.Bytes.Length; i++) rows.Add(new[] { "カテゴリ", s.Root, Catalog.Names[i], "", s.Bytes[i].ToString(), s.Counts[i].ToString(), stamp, s.Status });
                    rows.Add(new[] { "ドライブ実使用量", s.Root, "", s.Root, (s.Capacity - s.Free).ToString(), "", stamp, s.Status });
                    rows.Add(new[] { "実使用量と集計の差分", s.Root, "", s.Root, ((s.Capacity - s.Free) - s.Total).ToString(), "", stamp, s.Status });
                }
                foreach (var r in folderRows) rows.Add(new[] { "表示中のフォルダ", Path.GetPathRoot(r.Path), category < 0 ? "すべて" : Catalog.Names[category], r.Path, r.Bytes.ToString(), "", "", "" });
                foreach (var r in fileRows) rows.Add(new[] { "大きいファイル", Path.GetPathRoot(r.Path), r.Category, r.Path, r.Bytes.ToString(), "", "", "" });
                Store.WriteCsv(dialog.FileName, rows); feedbackMessage = "CSVを書き出しました: " + dialog.FileName; feedbackUntil = DateTime.UtcNow.AddSeconds(10); Refresh();
            }
            catch (Exception ex) { MessageBox.Show(window, ex.Message, "CSVを書き出せませんでした"); }
        }
        async void OnClosing(object sender, CancelEventArgs e)
        {
            if (closing) return;
            searchTimer.Stop();
            if (workers.Any(t => !t.IsCompleted))
            {
                e.Cancel = true; closing = true; timer.Stop(); window.IsEnabled = false;
                UI<TextBlock>("ScanStatus").Text = "スキャンを中断して、結果を保存しています…";
                foreach (var s in states) if (s.Cancel != null) s.Cancel.Cancel();
                try { await Task.WhenAll(workers); } catch (Exception) { }
                window.Close();
            }
        }
    }
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test") return Verification.Run(args.Length > 1 ? args[1] : "verification");
            if (args.Length > 1 && args[0] == "--scan-root") return Verification.ScanRoot(args);
            if (args.Length > 1 && args[0] == "--verify-caches") return Verification.VerifyCaches(args[1]);
            if (args.Length > 1 && args[0] == "--migrate-legacy") return Verification.MigrateLegacy(args[1]);
            bool documentation = args.Length > 1 && args[0] == "--docs-screenshot";
            string presentation = args.Length > 1 && args[0] == "--verify-presentation" ? args[1] : null;
            string screenshot = args.Length > 1 && (args[0] == "--screenshot" || documentation) ? args[1] : null;
            using (var mutex = new Mutex(false, documentation || presentation != null ? "Local\\DriveScope.PresentationFixtures" : "Local\\DriveScope.CapacityViewer"))
            {
                bool acquired = false; try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { MessageBox.Show("DriveScopeはすでに起動しています。タスクバーから開いてください。", "DriveScope"); return 0; }
                try
                {
                    var app = new Application();
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) { MessageBox.Show(e.Exception.Message, "DriveScope"); e.Handled = true; };
                    string live = args.Length > 1 && args[0] == "--verify-live" ? args[1] : null;
                    string uiVerify = args.Length > 1 && args[0] == "--verify-ui" ? args[1] : null;
                    string refreshVerify = args.Length > 1 && args[0] == "--verify-refresh" ? args[1] : null;
                    var controller = new MainController(args.Contains("--scan") || live != null, screenshot, live, uiVerify, refreshVerify, documentation, presentation); app.Run(controller.Window);
                    if (controller.VerificationFailed) return 1;
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(Store.DirectoryPath); File.WriteAllText(Path.Combine(Store.DirectoryPath, "error.log"), ex.ToString());
                    MessageBox.Show(ex.Message, "DriveScopeを起動できませんでした"); return 1;
                }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
    }
}
