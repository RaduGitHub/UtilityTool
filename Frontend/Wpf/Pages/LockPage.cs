using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using UtilityTool.Backend;
using Bitmap = System.Drawing.Bitmap;
using ImageSource = System.Windows.Media.ImageSource;

namespace UtilityTool.Frontend.Wpf
{
    // All desktop discovery and protection actions can be replaced by owned test fixtures.
    internal sealed class LockPageDependencies
    {
        internal PasswordStore Passwords { get; set; }
        internal LockStateStore StateStore { get; set; }
        internal LockStateStore CustomAppsStore { get; set; }
        internal AppGuardService Guard { get; set; }
        internal Func<Task<List<InstalledApp>>> CatalogLoader { get; set; }
        internal Func<Task<List<RunningApp>>> RunningProgramsLoader { get; set; }
        internal Func<Task<RunningApp>> PickWindow { get; set; }
        internal Func<IEnumerable<InstalledApp>, int, CancellationToken, IProgress<Dictionary<string, Bitmap>>, Task<Dictionary<string, Bitmap>>> IconLoader { get; set; }
        internal Func<bool> HasPassword { get; set; }
        internal Func<bool, string, bool> PasswordDialog { get; set; }
        internal Func<string, string, bool> Confirm { get; set; }
        internal Func<string> ChooseExecutable { get; set; }
        internal Action<IEnumerable<InstalledApp>, bool> ArmGuard { get; set; }
        internal Action<IEnumerable<InstalledApp>> UpdateGuardTargets { get; set; }
        internal Action<InstalledApp> RelockGuard { get; set; }
        internal Func<InstalledApp, PasswordStore> AppPasswordProvider { get; set; }
        internal Func<InstalledApp, bool> ConfigureAppPassword { get; set; }
        internal bool StartGuardTimer = true;
    }

    internal sealed class LockPage : Grid, IDisposable
    {
        private readonly Window owner;
        private readonly bool preview, startGuardTimer;
        // Retained for source compatibility with older diagnostics; the live view is Programs.
        internal readonly TileGrid Tiles = new TileGrid();
        internal readonly ListBox Programs = new ListBox { Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        private readonly ObservableCollection<LockProgramRow> visibleRows = new ObservableCollection<LockProgramRow>();
        private readonly List<LockProgramRow> rows = new List<LockProgramRow>();
        private readonly global::Wpf.Ui.Controls.TextBox search = new global::Wpf.Ui.Controls.TextBox { PlaceholderText = "Search by program or window title…", MinWidth = 140, Margin = new Thickness(0, 0, 12, 0) };
        private readonly global::Wpf.Ui.Controls.Button runningView = Ui.Button("Running programs", true), lockedView = Ui.Button("Locked programs"), refresh = Ui.Button("Refresh"), pick = Ui.Button("Pick a window…"), add = Ui.Button("Add app…");
        private readonly global::Wpf.Ui.Controls.Button monitor = Ui.Button("Turn on monitoring", true), password = Ui.Button("Set password"), lockAll = Ui.Button("Lock all now");
        private readonly TextBlock protection = Ui.Text("Monitoring off", 17), monitorDetail = Ui.Text("Saved programs will ask for your password when monitoring is on.", 13, true), count = Ui.Text("", 13, true), status = Ui.Text("", 13), empty = Ui.Text("", 15, true);
        private readonly StackPanel loadingIndicator = new StackPanel { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 300 };
        private readonly Dictionary<string, InstalledApp> protectedApps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase), manualApps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ImageSource> iconCache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> unavailableIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AppLockOverlay, PendingOverlay> overlays = new Dictionary<AppLockOverlay, PendingOverlay>();
        private readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) }, liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly AppGuardService guard;
        private readonly RunningAppCatalog nativeCatalog = new RunningAppCatalog();
        private readonly Func<Task<List<RunningApp>>> runningLoader;
        private readonly Func<Task<RunningApp>> windowPicker;
        private readonly Func<IEnumerable<InstalledApp>, int, CancellationToken, IProgress<Dictionary<string, Bitmap>>, Task<Dictionary<string, Bitmap>>> iconLoader;
        private readonly Func<bool> hasPassword;
        private readonly Func<bool, string, bool> passwordDialog;
        private readonly Func<string> chooseExecutable;
        private readonly Action<IEnumerable<InstalledApp>, bool> armGuard;
        private readonly Action<IEnumerable<InstalledApp>> updateTargets;
        private readonly Action<InstalledApp> relockGuard;
        private readonly Func<InstalledApp, PasswordStore> appPasswordProvider;
        private readonly Func<InstalledApp, bool> configureAppPassword;
        private PasswordStore passwords;
        private AppPasswordStore appPasswords;
        private LockStateStore stateStore, customAppsStore;
        private List<RunningApp> running = new List<RunningApp>();
        private CancellationTokenSource iconCancellation, pickerCancellation;
        private bool loading, loaded, disposed, savedLocked, initialized, stateReady, showingLocked, busy, picking;
        private int loadVersion;
        internal bool NeedsLoading { get { return !loaded && !loading; } }

        internal LockPage(Window owner, bool preview, LockPageDependencies dependencies = null)
        {
            this.owner = owner; this.preview = preview; Margin = new Thickness(32, 28, 32, 24);
            guard = dependencies != null && dependencies.Guard != null ? dependencies.Guard : new AppGuardService();
            startGuardTimer = dependencies == null || dependencies.StartGuardTimer;
            if (dependencies != null && dependencies.RunningProgramsLoader != null) runningLoader = dependencies.RunningProgramsLoader;
            else if (dependencies != null && dependencies.CatalogLoader != null) runningLoader = async () => FromLegacyApps(await dependencies.CatalogLoader());
            else runningLoader = () => Task.Run(() => nativeCatalog.Read());
            windowPicker = dependencies != null ? dependencies.PickWindow : null;
            iconLoader = dependencies != null && dependencies.IconLoader != null ? dependencies.IconLoader : (apps, size, token, progress) => AppIconService.LoadAsync(apps, size, token, progress);
            if (!preview)
            {
                try
                {
                    if (dependencies != null) { passwords = dependencies.Passwords; stateStore = dependencies.StateStore; customAppsStore = dependencies.CustomAppsStore; }
                    else
                    {
                        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UtilityTool");
                        passwords = new PasswordStore(Path.Combine(folder, "password.dat")); stateStore = new LockStateStore(Path.Combine(folder, "locks.xml")); customAppsStore = new LockStateStore(Path.Combine(folder, "customapps.xml"));
                        appPasswords = new AppPasswordStore(Path.Combine(folder, "AppPasswords"), passwords);
                    }
                }
                catch (Exception error) { Ui.Status(status, "Could not load password settings: " + error.Message, true); }
            }
            hasPassword = dependencies != null && dependencies.HasPassword != null ? dependencies.HasPassword : () => passwords != null && passwords.HasPassword;
            passwordDialog = dependencies != null && dependencies.PasswordDialog != null ? dependencies.PasswordDialog : (create, title) => passwords != null && new PasswordWindow(owner, passwords, create, title).ShowDialog() == true;
            chooseExecutable = dependencies != null && dependencies.ChooseExecutable != null ? dependencies.ChooseExecutable : PickExecutable;
            armGuard = dependencies != null && dependencies.ArmGuard != null ? dependencies.ArmGuard : guard.StartMonitoring;
            updateTargets = dependencies != null && dependencies.UpdateGuardTargets != null ? dependencies.UpdateGuardTargets : guard.UpdateTargets;
            relockGuard = dependencies != null && dependencies.RelockGuard != null ? dependencies.RelockGuard : guard.Relock;
            appPasswordProvider = dependencies != null && dependencies.AppPasswordProvider != null ? dependencies.AppPasswordProvider : app => appPasswords != null && app != null ? appPasswords.Resolve(app) : passwords;
            configureAppPassword = dependencies != null ? dependencies.ConfigureAppPassword : appPasswords == null ? (Func<InstalledApp, bool>)null : app => new AppPasswordWindow(owner, appPasswords, app).ShowDialog() == true;

            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Children.Add(Ui.Heading("App locks", "Protect running programs and keep your saved locks in one place."));
            Place(BuildMonitorBanner(), 1); Place(BuildToolbar(), 2);
            count.Margin = new Thickness(0, 0, 0, 10); Place(count, 3);
            BuildProgramList();
            var listArea = new Grid { MinHeight = 140 }; listArea.Children.Add(Programs); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.TextAlignment = TextAlignment.Center; empty.MaxWidth = 450; listArea.Children.Add(empty);
            loadingIndicator.Children.Add(Ui.Text("Finding running programs…", 15)); loadingIndicator.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 14, 0, 0) }); listArea.Children.Add(loadingIndicator); Place(listArea, 4);
            status.Margin = new Thickness(0, 12, 0, 0); status.MinHeight = 34; status.MaxHeight = 64; status.SetBinding(ToolTipProperty, new Binding("Text") { Source = status }); Place(status, 5);
            SetAccessibleName(Programs, "App lock programs"); SetAccessibleName(search, "Search locked apps"); SetAccessibleName(protection, "Protection status"); SetAccessibleName(loadingIndicator, "Lock loading"); SetAccessibleName(empty, "Lock empty state"); SetAccessibleName(monitor, "Toggle app monitoring");

            search.TextChanged += delegate { searchTimer.Stop(); searchTimer.Start(); }; searchTimer.Tick += delegate { searchTimer.Stop(); FilterRows(); };
            runningView.Click += delegate { showingLocked = false; RebuildRows(); }; lockedView.Click += delegate { showingLocked = true; RebuildRows(); };
            refresh.Click += async delegate { await RefreshPrograms(true); }; pick.Click += async delegate { await PickRunningWindow(); };
            add.Click += async delegate { if (preview || busy || disposed) return; string path = chooseExecutable(); if (!string.IsNullOrWhiteSpace(path)) await AddManualApp(path); };
            monitor.Click += delegate { if (guard.IsArmed || savedLocked) TurnOff(); else TurnOn(); };
            password.Click += delegate { if (!preview && !busy && !disposed) { busy = true; UpdateControls(); try { ConfigurePassword(); } finally { busy = false; UpdateControls(); } } };
            lockAll.Click += delegate { Relock(null); };
            liveTimer.Tick += async delegate { await RefreshPrograms(false); };
            IsVisibleChanged += delegate { if (!IsVisible) { liveTimer.Stop(); CancelPicker(); } else if (loaded && !preview && !disposed) liveTimer.Start(); };
            guard.StatusChanged += GuardStatusChanged; guard.RequestAccessWithPolicyAsync = RequestAccess;
            UpdateControls();
        }

        private FrameworkElement BuildMonitorBanner()
        {
            var banner = new DockPanel(); var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; monitor.Margin = new Thickness(12, 0, 8, 0); password.Margin = new Thickness(0); actions.Children.Add(monitor); actions.Children.Add(password); DockPanel.SetDock(actions, Dock.Right); banner.Children.Add(actions);
            var detail = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; protection.FontWeight = FontWeights.SemiBold; detail.Children.Add(protection); monitorDetail.Margin = new Thickness(0, 5, 0, 0); detail.Children.Add(monitorDetail); banner.Children.Add(detail);
            var card = Ui.Card(banner, new Thickness(18, 14, 18, 14)); card.Margin = new Thickness(0, 0, 0, 18); return card;
        }
        private FrameworkElement BuildToolbar()
        {
            var panel = new Grid { Margin = new Thickness(0, 0, 0, 12) }; panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var views = new DockPanel { Margin = new Thickness(0, 0, 0, 12) }; lockAll.Margin = new Thickness(0); DockPanel.SetDock(lockAll, Dock.Right); views.Children.Add(lockAll); var viewButtons = new StackPanel { Orientation = Orientation.Horizontal }; viewButtons.Children.Add(runningView); viewButtons.Children.Add(lockedView); views.Children.Add(viewButtons); panel.Children.Add(views);
            var tools = new DockPanel(); add.Margin = new Thickness(0); DockPanel.SetDock(add, Dock.Right); tools.Children.Add(add); DockPanel.SetDock(pick, Dock.Right); tools.Children.Add(pick); DockPanel.SetDock(refresh, Dock.Right); tools.Children.Add(refresh); tools.Children.Add(search); Grid.SetRow(tools, 1); panel.Children.Add(tools);
            pick.ToolTip = "Choose Pick a window, then switch to the app within 3 seconds."; return panel;
        }
        private void BuildProgramList()
        {
            Programs.ItemsSource = visibleRows; ScrollViewer.SetHorizontalScrollBarVisibility(Programs, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollBarVisibility(Programs, ScrollBarVisibility.Auto); ScrollViewer.SetCanContentScroll(Programs, true); VirtualizingPanel.SetIsVirtualizing(Programs, true); VirtualizingPanel.SetVirtualizationMode(Programs, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(Programs, ScrollUnit.Pixel);
            var container = new Style(typeof(ListBoxItem)); container.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); container.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); container.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 8))); container.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent)); container.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0))); Programs.ItemContainerStyle = container;
            var card = new FrameworkElementFactory(typeof(Border)); card.SetValue(Border.CornerRadiusProperty, new CornerRadius(10)); card.SetValue(Border.PaddingProperty, new Thickness(14, 12, 14, 12)); card.SetValue(Border.BackgroundProperty, Application.Current.FindResource("ToolSurface")); card.SetValue(Border.BorderBrushProperty, Application.Current.FindResource("ToolBorder")); card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var dock = new FrameworkElementFactory(typeof(DockPanel)); card.AppendChild(dock);
            var actions = new FrameworkElementFactory(typeof(StackPanel)); actions.SetValue(DockPanel.DockProperty, Dock.Right); actions.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal); actions.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); actions.SetValue(FrameworkElement.MarginProperty, new Thickness(18, 0, 0, 0)); dock.AppendChild(actions);
            AddRowButton(actions, "Password…", "PasswordCommand", "PasswordVisible", "PasswordAccessibleName"); AddRowButton(actions, "Lock now", "RelockCommand", "ProtectedVisibility", "RelockAccessibleName"); AddRowButton(actions, null, "MembershipCommand", null, "MembershipAccessibleName");
            var icon = new FrameworkElementFactory(typeof(Grid)); icon.SetValue(DockPanel.DockProperty, Dock.Left); icon.SetValue(FrameworkElement.WidthProperty, 40.0); icon.SetValue(FrameworkElement.HeightProperty, 40.0); icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 14, 0)); dock.AppendChild(icon);
            var glyph = new FrameworkElementFactory(typeof(TextBlock)); glyph.SetBinding(TextBlock.TextProperty, new Binding("Glyph")); glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets")); glyph.SetValue(TextBlock.FontSizeProperty, 28.0); glyph.SetValue(TextBlock.ForegroundProperty, Ui.Accent); glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center); glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); icon.AppendChild(glyph);
            var image = new FrameworkElementFactory(typeof(System.Windows.Controls.Image)); image.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding("Icon")); image.SetValue(System.Windows.Controls.Image.StretchProperty, Stretch.Uniform); icon.AppendChild(image);
            var text = new FrameworkElementFactory(typeof(StackPanel)); text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); dock.AppendChild(text);
            var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding("Title")); title.SetBinding(ToolTipProperty, new Binding("Title")); title.SetValue(TextBlock.FontSizeProperty, 15.0); title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold); title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); title.SetValue(TextBlock.ForegroundProperty, Brushes.WhiteSmoke); text.AppendChild(title);
            var process = new FrameworkElementFactory(typeof(TextBlock)); process.SetBinding(TextBlock.TextProperty, new Binding("Detail")); process.SetBinding(ToolTipProperty, new Binding("Executable")); process.SetValue(TextBlock.FontSizeProperty, 12.0); process.SetValue(TextBlock.ForegroundProperty, Ui.Muted); process.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); process.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0)); text.AppendChild(process);
            var state = new FrameworkElementFactory(typeof(TextBlock)); state.SetBinding(TextBlock.TextProperty, new Binding("StateText")); state.SetBinding(ToolTipProperty, new Binding("StateText")); state.SetValue(TextBlock.FontSizeProperty, 12.0); state.SetValue(TextBlock.ForegroundProperty, Ui.Accent); state.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); state.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 4, 0, 0)); text.AppendChild(state);
            Programs.ItemTemplate = new DataTemplate { VisualTree = card };
        }
        private static void AddRowButton(FrameworkElementFactory parent, string label, string command, string visibility, string accessibleName)
        {
            var button = new FrameworkElementFactory(typeof(global::Wpf.Ui.Controls.Button)); if (label == null) button.SetBinding(ContentControl.ContentProperty, new Binding("MembershipLabel")); else button.SetValue(ContentControl.ContentProperty, label);
            button.SetValue(Control.PaddingProperty, new Thickness(12, 8, 12, 8)); button.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 0, 0)); button.SetValue(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center); button.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty, new Binding(command)); button.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new Binding(accessibleName)); if (visibility != null) button.SetBinding(VisibilityProperty, new Binding(visibility)); parent.AppendChild(button);
        }
        private static void SetAccessibleName(DependencyObject value, string name) { System.Windows.Automation.AutomationProperties.SetName(value, name); }
        private void Place(UIElement value, int row) { Grid.SetRow(value, row); Children.Add(value); }
        private void GuardStatusChanged(string message)
        {
            if (disposed || Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => { if (disposed) return; if (guard.IsArmed) Ui.Status(status, message); UpdateControls(); }));
        }
        private Task<GuardAccessResult> RequestAccess(GuardProcess process)
        {
            if (disposed || Dispatcher.HasShutdownStarted) return Task.FromResult(new GuardAccessResult(false)); int version = guard.SessionVersion, targetVersion = guard.GetTargetVersion(process);
            try { return Dispatcher.Invoke(new Func<Task<GuardAccessResult>>(async () =>
            {
                if (disposed || !guard.IsArmed || guard.SessionVersion != version || targetVersion == 0 || guard.GetTargetVersion(process) != targetVersion) return new GuardAccessResult(false);
                var app = guard.GetProtectedApp(process); if (app == null) return new GuardAccessResult(false);
                PasswordStore credentials; string accessError = null;
                try { credentials = appPasswordProvider(app); if (credentials == null) throw new InvalidOperationException("The password store is unavailable."); }
                catch (Exception)
                {
                    // Keep the privacy curtain in place when an app override cannot be read.
                    // Only the authenticated settings action may repair that saved override.
                    credentials = new PasswordStore(null); accessError = "App password unavailable. Reset it using Password… in UtilityTool.";
                    Ui.Status(status, accessError, true);
                }
                using (var overlay = new AppLockOverlay(process))
                {
                    overlays.Add(overlay, new PendingOverlay(process, targetVersion));
                    try { bool allowed = await overlay.PromptAsync(credentials, () => !disposed && guard.IsArmed && guard.SessionVersion == version && guard.GetTargetVersion(process) == targetVersion, accessError); return new GuardAccessResult(allowed, allowed ? overlay.AllowedFor : null); }
                    finally { overlays.Remove(overlay); }
                }
            })); }
            catch (InvalidOperationException) { return Task.FromResult(new GuardAccessResult(false)); }
            catch (TaskCanceledException) { return Task.FromResult(new GuardAccessResult(false)); }
        }
        internal void InitializeSavedLocks()
        {
            if (initialized || preview || disposed) return; initialized = true;
            try { if (customAppsStore != null) foreach (var app in customAppsStore.Load().Apps) if (!string.IsNullOrWhiteSpace(app.Id)) manualApps[app.Id] = app; }
            catch (Exception error) { Ui.Status(status, "Could not read added apps: " + error.Message + " Saved protection will still be checked.", true); }
            try
            {
                if (stateStore == null) return; var state = stateStore.Load(); stateReady = true; savedLocked = state.Locked;
                foreach (var app in state.Apps) { protectedApps[app.Id] = app; if (app.Id.StartsWith("manual:", StringComparison.OrdinalIgnoreCase)) manualApps[app.Id] = app; }
                if (savedLocked) RestoreMonitoring("Saved monitoring restored.");
            }
            catch (Exception error) { Ui.Status(status, "Could not restore app settings: " + error.Message, true); }
            finally { RebuildRows(); }
        }
        private bool RestoreMonitoring(string success)
        {
            var eligible = protectedApps.Values.Where(IsSafe).ToList();
            if (!hasPassword() || eligible.Count == 0 && protectedApps.Count != 0) { Ui.Status(status, "Saved monitoring needs attention. Check your password and protected programs, or turn monitoring off.", true); return false; }
            try { armGuard(eligible, startGuardTimer); Ui.Status(status, eligible.Count == protectedApps.Count ? success : success + " Some unsupported saved programs could not be protected.", eligible.Count != protectedApps.Count); return true; }
            catch (Exception error) { guard.Disarm(); ClearOverlays(); Ui.Status(status, "Monitoring could not start: " + error.Message + " Turn monitoring off to review the saved programs.", true); return false; }
        }
        private static bool IsSafe(InstalledApp app) { try { return app != null && InstalledAppCatalog.IsSelectable(app); } catch (ArgumentException) { return false; } catch (NotSupportedException) { return false; } }
        internal Task LoadApps() { return RefreshPrograms(false); }
        private async Task RefreshPrograms(bool announce)
        {
            if (loading || disposed || picking) return; InitializeSavedLocks(); int version = ++loadVersion; loading = true; ShowLoading(); UpdateControls();
            try
            {
                var discovered = preview ? PreviewPrograms() : await runningLoader(); if (disposed || version != loadVersion) return;
                running = discovered ?? new List<RunningApp>(); loaded = true; RebuildRows(); if (announce) Ui.Status(status, "Running programs refreshed.");
            }
            catch (Exception error) { if (!disposed && version == loadVersion) { loaded = true; RebuildRows(); Ui.Status(status, "Could not refresh running programs: " + error.Message + " Try Refresh, or use Add app…", true); } }
            finally { if (!disposed && version == loadVersion) { loading = false; ShowLoading(); FilterRows(); UpdateControls(); if (IsVisible && !preview) liveTimer.Start(); } }
            if (!disposed && version == loadVersion && !preview) await LoadMissingIcons(version);
        }
        private static List<RunningApp> FromLegacyApps(List<InstalledApp> apps)
        {
            var result = new List<RunningApp>(); int index = 0; foreach (var app in apps ?? new List<InstalledApp>()) { index++; var process = new RunningAppProcessSnapshot(index, index, Path.GetFileName(app.ExecutablePath), app.ExecutablePath, app.Id.IndexOf('!') >= 0 ? app.Id : ""); result.Add(new RunningApp(new RunningAppWindowSnapshot(new IntPtr(index), app.Name, index, new[] { process }), process, app, IsSafe(app) ? "" : "This program cannot be protected.")); } return result;
        }
        private static List<RunningApp> PreviewPrograms() { return FromLegacyApps(new[] { "Firefox", "Google Chrome", "Visual Studio Code", "Steam", "Discord", "Spotify" }.Select(name => new InstalledApp("preview:" + name, name, name + ".exe")).ToList()); }

        private void RebuildRows()
        {
            if (disposed) return; var existing = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase); var next = new List<LockProgramRow>();
            if (showingLocked)
            {
                foreach (var app in protectedApps.Values.OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var windows = running.Where(value => IsMatch(app, value)).ToList(); AddRow(next, existing, "protected:" + app.Id, app, windows.FirstOrDefault(), app, windows.Count);
                }
            }
            else
            {
                foreach (var program in running)
                {
                    var member = FindProtected(program); string key = "window:" + program.WindowHandle.ToInt64() + ":" + program.ProcessId + ":" + program.StartTimeTicks; AddRow(next, existing, key, member ?? program.App, program, member, 1);
                }
            }
            rows.Clear(); rows.AddRange(next); FilterRows(); UpdateControls();
        }
        private void AddRow(List<LockProgramRow> next, Dictionary<string, LockProgramRow> existing, string key, InstalledApp app, RunningApp runningApp, InstalledApp member, int windows)
        {
            LockProgramRow row; if (!existing.TryGetValue(key, out row)) row = new LockProgramRow(this, key); row.Update(app, runningApp, member, windows, showingLocked); ImageSource icon; if (app != null && iconCache.TryGetValue(IconKey(app), out icon)) row.Icon = icon; next.Add(row);
        }
        private bool IsMatch(InstalledApp app, RunningApp program)
        {
            if (app == null || program == null) return false; if (program.App != null && string.Equals(app.Id, program.App.Id, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrWhiteSpace(app.ExecutablePath) && string.Equals(app.ExecutablePath, program.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return true;
            return guard.Matches(app, new GuardProcess(program.ProcessId, program.ProcessName, program.ExecutablePath, program.AppUserModelId, program.StartTimeTicks));
        }
        private InstalledApp FindProtected(RunningApp program) { foreach (var app in protectedApps.Values) if (IsMatch(app, program)) return app; return null; }
        private void FilterRows()
        {
            if (disposed) return; string query = search.Text.Trim(); var wanted = rows.Where(row => row.Matches(query)).ToList(); var keys = new HashSet<string>(wanted.Select(row => row.Key), StringComparer.OrdinalIgnoreCase);
            for (int i = visibleRows.Count - 1; i >= 0; i--) if (!keys.Contains(visibleRows[i].Key)) visibleRows.RemoveAt(i);
            for (int i = 0; i < wanted.Count; i++) { int current = visibleRows.IndexOf(wanted[i]); if (current < 0) visibleRows.Insert(i, wanted[i]); else if (current != i) visibleRows.Move(current, i); }
            count.Text = preview ? visibleRows.Count + " sample programs · actions disabled" : showingLocked ? protectedApps.Count + " protected programs · " + visibleRows.Count + " shown" : running.Count + " running windows · " + protectedApps.Count + " protected programs";
            empty.Text = !string.IsNullOrEmpty(query) ? "No matching programs. Try another name or window title." : showingLocked ? "No protected programs yet. Open Running programs, then choose Lock beside a program." : "No running programs found. Open an app and use Refresh, or choose Add app… to protect its .exe file.";
            empty.Visibility = !loading && visibleRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        private static string IconKey(InstalledApp app) { return app.Id + "\n" + app.ExecutablePath; }
        private async Task LoadMissingIcons(int version)
        {
            var apps = rows.Where(row => row.App != null).Select(row => row.App).GroupBy(IconKey, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).Where(app => !iconCache.ContainsKey(IconKey(app)) && !unavailableIcons.Contains(IconKey(app))).ToList(); if (apps.Count == 0) return;
            if (iconCancellation != null) iconCancellation.Cancel(); var cancellation = new CancellationTokenSource(); iconCancellation = cancellation;
            var byId = apps.GroupBy(app => app.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var progress = new Progress<Dictionary<string, Bitmap>>(batch => { try { AcceptIcons(batch, byId); } finally { DisposeIcons(batch); } });
            try { var images = await iconLoader(apps, 48, cancellation.Token, progress); try { AcceptIcons(images, byId); } finally { DisposeIcons(images); } foreach (var app in apps) if (!iconCache.ContainsKey(IconKey(app))) unavailableIcons.Add(IconKey(app)); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* App names and actions remain usable without icons. */ }
            finally { if (ReferenceEquals(iconCancellation, cancellation)) iconCancellation = null; cancellation.Dispose(); }
        }
        private void AcceptIcons(Dictionary<string, Bitmap> images, Dictionary<string, InstalledApp> byId)
        {
            if (disposed || images == null) return; foreach (var pair in images) { InstalledApp app; if (pair.Value == null || !byId.TryGetValue(pair.Key, out app)) continue; string key = IconKey(app); if (!iconCache.ContainsKey(key)) iconCache[key] = TileGrid.ConvertIcon(pair.Value); foreach (var row in rows) if (row.App != null && string.Equals(IconKey(row.App), key, StringComparison.OrdinalIgnoreCase)) row.Icon = iconCache[key]; }
        }
        private static void DisposeIcons(Dictionary<string, Bitmap> images) { if (images != null) foreach (var image in images.Values) if (image != null) image.Dispose(); }
        private void ShowLoading() { loadingIndicator.Visibility = loading && !loaded ? Visibility.Visible : Visibility.Collapsed; if (loading && !loaded) empty.Visibility = Visibility.Collapsed; }

        private bool CheckPassword(string title) { return !preview && hasPassword() && passwordDialog(false, title); }
        private bool ConfigurePassword()
        {
            if (preview || passwords == null || guard.IsArmed || savedLocked && hasPassword()) return false;
            if (hasPassword() && !CheckPassword("Verify current password")) return false;
            if (!passwordDialog(true, "Set UtilityTool password")) return false;
            if (!hasPassword()) { Ui.Status(status, "The password was not saved. Try setting it again.", true); return false; }
            if (savedLocked) RestoreMonitoring("Custom password saved. Saved monitoring restored."); else Ui.Status(status, "Custom password saved. Add programs, then turn on monitoring.");
            return true;
        }
        private LockState State(IEnumerable<InstalledApp> apps, bool locked) { var state = new LockState { Locked = locked }; state.Apps.AddRange(apps); return state; }
        private bool CommitMembership(List<InstalledApp> next)
        {
            var previous = protectedApps.Values.ToList(); bool armed = guard.IsArmed;
            try
            {
                stateStore.Save(State(next, savedLocked));
                try { if (armed) updateTargets(next.Where(IsSafe).ToList()); }
                catch (Exception error)
                {
                    try { stateStore.Save(State(previous, savedLocked)); }
                    catch (Exception rollback) { Ui.Status(status, "The active locks are unchanged, but saved settings could not be restored: " + rollback.Message + ". Check the settings folder before restarting.", true); return false; }
                    throw new InvalidOperationException("The active locks were kept unchanged. " + error.Message, error);
                }
                protectedApps.Clear(); foreach (var app in next) protectedApps[app.Id] = app; RebuildRows(); return true;
            }
            catch (Exception error) { Ui.Status(status, "Could not save app locks: " + error.Message, true); return false; }
        }
        private void ChangeMembership(LockProgramRow row)
        {
            if (!CanManage || row == null) return; busy = true; UpdateControls();
            try
            {
                if (row.Member != null)
                {
                    var member = row.Member; if (!CheckPassword("Remove app lock")) return; var next = protectedApps.Values.Where(app => !string.Equals(app.Id, member.Id, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (CommitMembership(next)) { ClearStaleOverlays(); Ui.Status(status, "Lock removed from " + member.Name + "."); }
                }
                else if (row.App != null && row.CanProtect) AddProtectedApp(row.App);
            }
            finally { busy = false; UpdateControls(); }
        }
        private bool AddProtectedApp(InstalledApp app)
        {
            if (preview || !stateReady || !IsSafe(app)) { Ui.Status(status, "This program cannot be protected safely.", true); return false; }
            if (protectedApps.ContainsKey(app.Id) || protectedApps.Values.Any(value => SameTarget(value, app))) { Ui.Status(status, "This program is already protected."); return true; }
            if (!hasPassword() && !ConfigurePassword()) return false;
            var next = protectedApps.Values.ToList(); next.Add(app); if (!CommitMembership(next)) return false;
            Ui.Status(status, app.Name + (guard.IsArmed ? " is protected. It will ask for your password." : " added to Locked programs. Turn on monitoring to apply its lock.")); return true;
        }
        private static bool SameTarget(InstalledApp left, InstalledApp right) { return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(left.ExecutablePath) && string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase); }
        private void TurnOn()
        {
            if (!CanManage || guard.IsArmed || savedLocked) return;
            if (protectedApps.Count == 0) { Ui.Status(status, "Add a program to Locked programs before turning on monitoring."); return; }
            busy = true; UpdateControls();
            try
            {
                if (!hasPassword() && !ConfigurePassword()) return; if (protectedApps.Values.Any(app => !IsSafe(app))) { Ui.Status(status, "Remove unsupported saved programs before turning on monitoring.", true); return; }
                stateStore.Save(State(protectedApps.Values, true));
                try { armGuard(protectedApps.Values.ToList(), startGuardTimer); }
                catch { guard.Disarm(); ClearOverlays(); try { stateStore.Save(State(protectedApps.Values, false)); } catch { savedLocked = true; } throw; }
                savedLocked = true; Ui.Status(status, "Monitoring is on. Your password is required to turn it off or close UtilityTool.");
            }
            catch (Exception error) { Ui.Status(status, "Could not turn on monitoring: " + error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private void TurnOff()
        {
            if (!CanManage || !guard.IsArmed && !savedLocked) return; busy = true; UpdateControls();
            try { if (!CheckPassword("Turn off monitoring")) return; stateStore.Save(State(protectedApps.Values, false)); savedLocked = false; guard.Disarm(); ClearOverlays(); Ui.Status(status, "Monitoring is off. Your protected programs are saved."); }
            catch (Exception error) { Ui.Status(status, "Could not turn off monitoring: " + error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private void Relock(InstalledApp app)
        {
            if (preview || disposed || busy || !guard.IsArmed) return;
            try { relockGuard(app); ClearStaleOverlays(); Ui.Status(status, app == null ? "Protected programs are locked again." : app.Name + " is locked again."); }
            catch (Exception error) { Ui.Status(status, "Could not lock programs again: " + error.Message, true); }
            UpdateControls();
        }
        private void ConfigureProgramPassword(LockProgramRow row)
        {
            if (!CanManage || row == null || row.Member == null || configureAppPassword == null) return; busy = true; UpdateControls();
            try { if (CheckPassword("Change app password") && configureAppPassword(row.Member)) { if (guard.IsArmed) { relockGuard(row.Member); ClearStaleOverlays(); } Ui.Status(status, "Password settings updated for " + row.Member.Name + "."); } }
            catch (Exception error) { Ui.Status(status, "Could not change the app password: " + error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private string PickExecutable() { var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose the app's executable", Filter = "Applications (*.exe)|*.exe", CheckFileExists = true, Multiselect = false }; return dialog.ShowDialog(owner) == true ? dialog.FileName : null; }
        internal Task<bool> AddManualApp(string path)
        {
            if (preview || disposed) return Task.FromResult(false); InitializeSavedLocks(); if (!CanManage || customAppsStore == null) return Task.FromResult(false); bool previousBusy = busy; busy = true; UpdateControls();
            try
            {
                string full = Path.GetFullPath(path); if (!File.Exists(full) || !string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an installed app's .exe file.");
                var existing = protectedApps.Values.FirstOrDefault(candidate => string.Equals(candidate.ExecutablePath, full, StringComparison.OrdinalIgnoreCase)); if (existing != null) { Ui.Status(status, "This program is already protected."); return Task.FromResult(true); }
                var app = new InstalledApp("manual:" + full.ToUpperInvariant(), Path.GetFileNameWithoutExtension(full), full); if (!IsSafe(app)) throw new ArgumentException("Windows system tools and UtilityTool cannot be protected.");
                if (!hasPassword() && !ConfigurePassword()) return Task.FromResult(false);
                var oldManual = manualApps.Values.ToList(); var nextManual = oldManual.Where(value => !string.Equals(value.Id, app.Id, StringComparison.OrdinalIgnoreCase)).ToList(); nextManual.Add(app); customAppsStore.Save(State(nextManual, false));
                if (!AddProtectedApp(app)) { try { customAppsStore.Save(State(oldManual, false)); } catch (Exception error) { Ui.Status(status, "The lock was not added. The added-app list could not be restored: " + error.Message, true); } return Task.FromResult(false); }
                manualApps[app.Id] = app; return Task.FromResult(true);
            }
            catch (Exception error) { Ui.Status(status, "Could not add app: " + error.Message, true); return Task.FromResult(false); }
            finally { busy = previousBusy; UpdateControls(); }
        }
        private async Task PickRunningWindow()
        {
            if (!CanManage || picking) return; CancelPicker(); var cancellation = new CancellationTokenSource(); pickerCancellation = cancellation; picking = true; UpdateControls(); Ui.Status(status, "Switch to the program you want to protect within 3 seconds. Selection cancels if you stay here.");
            try
            {
                RunningApp program;
                if (windowPicker != null) program = await windowPicker();
                else { await Task.Delay(3000, cancellation.Token); if (disposed || !IsVisible) return; IntPtr target = AppWindows.ForegroundWindow; program = await Task.Run(() => nativeCatalog.ReadWindow(target)); }
                if (cancellation.IsCancellationRequested || disposed || !IsVisible) return;
                if (program == null) { Ui.Status(status, "No supported program window was chosen. Try again, then switch to another app."); return; }
                if (!program.CanGuard || !IsSafe(program.App)) { Ui.Status(status, string.IsNullOrEmpty(program.UnavailableReason) ? "This window cannot be protected." : program.UnavailableReason, true); return; }
                busy = true; UpdateControls(); AddProtectedApp(program.App);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!disposed) Ui.Status(status, "Could not choose this window: " + error.Message, true); }
            finally { if (ReferenceEquals(pickerCancellation, cancellation)) { pickerCancellation = null; picking = false; busy = false; if (!disposed) UpdateControls(); } cancellation.Dispose(); }
        }
        private void CancelPicker() { if (pickerCancellation != null) { pickerCancellation.Cancel(); pickerCancellation = null; picking = false; if (!disposed) { Ui.Status(status, "Window selection cancelled."); UpdateControls(); } } }
        private bool CanManage { get { return !preview && !disposed && !busy && !picking && passwords != null && stateStore != null && stateReady; } }
        private void UpdateControls()
        {
            if (disposed) return; bool active = guard.IsArmed, locked = active || savedLocked, ready = CanManage, passwordExists = !preview && hasPassword();
            protection.Text = active ? "Monitoring on" : savedLocked ? "Monitoring needs attention" : "Monitoring off"; protection.Foreground = active ? Ui.Accent : Ui.Muted;
            monitorDetail.Text = preview ? "Sample preview. Protection and settings are unchanged." : savedLocked && !active ? "Your saved locks are retained. Check your password or turn monitoring off." : active ? "Protected programs ask for a password. UtilityTool must stay running." : "Choose Lock beside a program, then turn on monitoring.";
            monitor.Content = locked ? "Turn off monitoring" : "Turn on monitoring"; monitor.IsEnabled = ready && (!locked || passwordExists); password.Content = passwordExists ? "Change password" : "Set password"; password.IsEnabled = !preview && !busy && !picking && passwords != null && !active && (!savedLocked || !passwordExists);
            refresh.IsEnabled = !loading && !picking && !busy; add.IsEnabled = ready && customAppsStore != null; pick.IsEnabled = ready; search.IsEnabled = !picking; runningView.IsEnabled = lockedView.IsEnabled = !picking; lockAll.IsEnabled = !preview && !busy && !picking && active && protectedApps.Count > 0;
            runningView.Appearance = showingLocked ? global::Wpf.Ui.Controls.ControlAppearance.Secondary : global::Wpf.Ui.Controls.ControlAppearance.Primary; lockedView.Appearance = showingLocked ? global::Wpf.Ui.Controls.ControlAppearance.Primary : global::Wpf.Ui.Controls.ControlAppearance.Secondary; lockedView.Content = "Locked programs (" + protectedApps.Count + ")";
            foreach (var row in rows) row.RefreshState();
        }
        internal bool CanClose() { if (!guard.IsArmed) return true; if (!CheckPassword("Close UtilityTool")) return false; guard.Disarm(); ClearOverlays(); return true; }
        private void ClearOverlays() { foreach (var overlay in overlays.Keys.ToList()) overlay.Cancel(); overlays.Clear(); }
        private void ClearStaleOverlays() { foreach (var pair in overlays.ToList()) if (guard.GetTargetVersion(pair.Value.Process) != pair.Value.TargetVersion) pair.Key.Cancel(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true; ++loadVersion; CancelPicker(); if (iconCancellation != null) iconCancellation.Cancel(); liveTimer.Stop(); searchTimer.Stop(); guard.StatusChanged -= GuardStatusChanged; guard.Dispose(); ClearOverlays();
        }

        private sealed class LockProgramRow : INotifyPropertyChanged
        {
            private readonly LockPage page;
            private RunningApp running;
            private int windows;
            private bool savedView;
            private ImageSource icon;
            internal string Key { get; private set; }
            internal InstalledApp App { get; private set; }
            internal InstalledApp Member { get; private set; }
            internal bool CanProtect { get { return App != null && (running == null ? IsSafe(App) : running.CanGuard && IsSafe(App)); } }
            public string Title { get { return savedView ? App.Name : running == null || string.IsNullOrWhiteSpace(running.WindowTitle) ? App == null ? "Unknown program" : App.Name : running.WindowTitle; } }
            public string Executable { get { return App == null ? running == null ? "" : running.ExecutablePath : App.ExecutablePath; } }
            public string Detail { get { string process = running == null ? Path.GetFileName(Executable) : running.ProcessName; if (string.IsNullOrWhiteSpace(process)) process = App == null ? "Program information unavailable" : App.Name; return process + (running == null ? " · Not running" : running.IsMinimized ? " · Minimized" : " · Running") + (windows > 1 ? " · " + windows + " windows" : ""); } }
            public string StateText { get { if (running != null && !running.CanGuard) return running.UnavailableReason; if (!CanProtect) return "Unsupported saved program"; if (Member == null) return "No lock configured"; if (!page.guard.IsArmed) return page.savedLocked ? "Saved lock · monitoring needs attention" : "Saved lock · monitoring off"; if (page.guard.IsPending(Member)) return "Password requested"; if (page.guard.IsApproved(Member)) return "Unlocked"; return "Protected"; } }
            public string MembershipLabel { get { return Member == null ? "Lock" : "Remove lock"; } }
            public string MembershipAccessibleName { get { return MembershipLabel + " for " + Title; } }
            public string RelockAccessibleName { get { return "Lock now for " + Title; } }
            public string PasswordAccessibleName { get { return "Password settings for " + Title; } }
            public Visibility ProtectedVisibility { get { return Member == null ? Visibility.Collapsed : Visibility.Visible; } }
            public Visibility PasswordVisible { get { return Member == null || page.configureAppPassword == null ? Visibility.Collapsed : Visibility.Visible; } }
            public string Glyph { get { return icon == null ? "\uE8A5" : ""; } }
            public ImageSource Icon { get { return icon; } set { if (ReferenceEquals(icon, value)) return; icon = value; Changed("Icon"); Changed("Glyph"); } }
            public ICommand MembershipCommand { get; private set; }
            public ICommand RelockCommand { get; private set; }
            public ICommand PasswordCommand { get; private set; }
            public event PropertyChangedEventHandler PropertyChanged;
            internal LockProgramRow(LockPage page, string key)
            {
                this.page = page; Key = key; MembershipCommand = new RowCommand(() => page.ChangeMembership(this), () => page.CanManage && (Member != null || CanProtect)); RelockCommand = new RowCommand(() => page.Relock(Member), () => !page.preview && !page.disposed && !page.busy && !page.picking && Member != null && page.guard.IsArmed); PasswordCommand = new RowCommand(() => page.ConfigureProgramPassword(this), () => page.CanManage && Member != null && page.configureAppPassword != null);
            }
            internal void Update(InstalledApp app, RunningApp runningApp, InstalledApp member, int windows, bool savedView) { App = app; running = runningApp; Member = member; this.windows = windows; this.savedView = savedView; foreach (string property in new[] { "Title", "Executable", "Detail", "StateText", "MembershipLabel", "MembershipAccessibleName", "RelockAccessibleName", "PasswordAccessibleName", "ProtectedVisibility", "PasswordVisible" }) Changed(property); }
            internal void RefreshState() { Changed("StateText"); ((RowCommand)MembershipCommand).Refresh(); ((RowCommand)RelockCommand).Refresh(); ((RowCommand)PasswordCommand).Refresh(); }
            internal bool Matches(string query) { return string.IsNullOrEmpty(query) || new[] { Title, Detail, Executable, App == null ? "" : App.Name, running == null ? "" : running.WindowTitle }.Any(value => value.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0); }
            private void Changed(string property) { var changed = PropertyChanged; if (changed != null) changed(this, new PropertyChangedEventArgs(property)); }
        }
        private sealed class RowCommand : ICommand
        {
            private readonly Action execute; private readonly Func<bool> canExecute;
            internal RowCommand(Action execute, Func<bool> canExecute) { this.execute = execute; this.canExecute = canExecute; }
            public bool CanExecute(object parameter) { return canExecute(); }
            public void Execute(object parameter) { if (CanExecute(parameter)) execute(); }
            public event EventHandler CanExecuteChanged;
            internal void Refresh() { var changed = CanExecuteChanged; if (changed != null) changed(this, EventArgs.Empty); }
        }
        private sealed class PendingOverlay
        {
            internal readonly GuardProcess Process;
            internal readonly int TargetVersion;
            internal PendingOverlay(GuardProcess process, int targetVersion) { Process = process; TargetVersion = targetVersion; }
        }
    }
}
