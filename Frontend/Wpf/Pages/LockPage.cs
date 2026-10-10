using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class LockPage : Grid, IDisposable
    {
        private readonly Window owner;
        private readonly bool preview;
        internal readonly TileGrid Tiles = new TileGrid();
        private readonly global::Wpf.Ui.Controls.TextBox search = new global::Wpf.Ui.Controls.TextBox { PlaceholderText = "Search installed apps…", Margin = new Thickness(0, 0, 12, 0) };
        private readonly global::Wpf.Ui.Controls.Button refresh = Ui.Button("Refresh"), apply = Ui.Button("Apply locks", true), unlock = Ui.Button("Unlock apps"), password = Ui.Button("Set password");
        private readonly TextBlock count = Ui.Text("Open this tab to load installed apps.", 13, true), status = Ui.Text("Guard is off. Select apps to protect.", 13);
        private readonly List<TileItem> catalog = new List<TileItem>();
        private readonly HashSet<string> selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, InstalledApp> saved = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<AppLockOverlay> overlays = new HashSet<AppLockOverlay>();
        private readonly CancellationTokenSource iconCancellation = new CancellationTokenSource();
        private readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly AppGuardService guard = new AppGuardService();
        private PasswordStore passwords;
        private LockStateStore stateStore;
        private bool loading, loaded, disposed, savedLocked, dirty, initialized;
        internal bool NeedsLoading { get { return !loaded && !loading; } }
        internal LockPage(Window owner, bool preview)
        {
            this.owner = owner; this.preview = preview; Margin = new Thickness(32, 28, 32, 24);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Children.Add(Ui.Heading("App locks", "Choose apps that should ask for your custom password."));
            var searchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(refresh, Dock.Right); searchRow.Children.Add(refresh); searchRow.Children.Add(search); Grid.SetRow(searchRow, 1); Children.Add(searchRow);
            Grid.SetRow(Tiles, 2); Children.Add(Tiles); count.Margin = new Thickness(0, 12, 0, 16); Grid.SetRow(count, 3); Children.Add(count);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) }; actions.Children.Add(password); actions.Children.Add(apply); actions.Children.Add(unlock); Grid.SetRow(actions, 4); Children.Add(actions);
            status.MinHeight = 40; Grid.SetRow(status, 5); Children.Add(status);
            if (!preview)
            {
                try { var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UtilityTool"); passwords = new PasswordStore(Path.Combine(folder, "password.dat")); stateStore = new LockStateStore(Path.Combine(folder, "locks.xml")); }
                catch (Exception error) { Ui.Status(status, "Could not load password settings: " + error.Message, true); }
            }
            search.TextChanged += delegate { Filter(); };
            refresh.Click += async delegate { await LoadApps(); }; apply.Click += delegate { Apply(); }; unlock.Click += delegate { Unlock(); }; password.Click += delegate { ConfigurePassword(); };
            saveTimer.Tick += delegate { saveTimer.Stop(); if (Save(savedLocked)) dirty = false; };
            guard.StatusChanged += message => { if (!disposed) Dispatcher.BeginInvoke(new Action(() => { if (!disposed && guard.IsArmed) Ui.Status(status, message); })); };
            guard.RequestAccessAsync = process =>
            {
                if (disposed || Dispatcher.HasShutdownStarted) return Task.FromResult(false);
                int version = guard.SessionVersion;
                try { return Dispatcher.Invoke(new Func<Task<bool>>(async () =>
                {
                    if (disposed || !guard.IsArmed || guard.SessionVersion != version) return false;
                    using (var overlay = new AppLockOverlay(process))
                    { overlays.Add(overlay); try { return await overlay.PromptAsync(passwords, () => !disposed && guard.IsArmed && guard.SessionVersion == version); } finally { overlays.Remove(overlay); } }
                })); }
                catch (InvalidOperationException) { return Task.FromResult(false); }
                catch (TaskCanceledException) { return Task.FromResult(false); }
            };
            UpdateControls();
        }
        internal void InitializeSavedLocks()
        {
            if (initialized || preview) return; initialized = true;
            try
            {
                if (stateStore == null) return;
                var state = stateStore.Load(); savedLocked = state.Locked;
                foreach (var app in state.Apps) if (InstalledAppCatalog.IsSelectable(app)) { selected.Add(app.Id); saved[app.Id] = app; }
                if (savedLocked)
                {
                    if (passwords == null || !passwords.HasPassword || saved.Count == 0) { Ui.Status(status, "Saved locks could not start. Check the password and selected apps.", true); return; }
                    guard.Arm(saved.Values, true); Ui.Status(status, "Saved locks restored. Guard is active.");
                }
            }
            catch (Exception error) { Ui.Status(status, "Could not restore locks: " + error.Message, true); }
            finally { UpdateControls(); }
        }
        internal async Task LoadApps()
        {
            if (loading || disposed) return; loading = true; UpdateControls(); count.Text = "Loading installed apps…";
            try
            {
                List<InstalledApp> apps = preview ? new List<InstalledApp> { new InstalledApp("firefox", "Firefox", "preview.exe"), new InstalledApp("chrome", "Google Chrome", "preview.exe"), new InstalledApp("vscode", "Visual Studio Code", "preview.exe"), new InstalledApp("steam", "Steam", "preview.exe"), new InstalledApp("discord", "Discord", "preview.exe"), new InstalledApp("7zip", "7-Zip", "preview.exe"), new InstalledApp("spotify", "Spotify", "preview.exe"), new InstalledApp("vlc", "VLC media player", "preview.exe") } : await InstalledAppCatalog.LoadAsync();
                if (disposed) return; var previousIcons = catalog.Where(item => item.Icon != null).ToDictionary(item => item.Id, item => item.Icon, StringComparer.OrdinalIgnoreCase); catalog.Clear();
                foreach (var app in apps)
                { if (selected.Contains(app.Id)) saved[app.Id] = app; var item = new TileItem(app.Id, app.Name, app, selected.Contains(app.Id)); System.Windows.Media.ImageSource icon; if (previousIcons.TryGetValue(app.Id, out icon)) item.Icon = icon; item.Editable = !guard.IsArmed; item.SelectionChanged += OnSelection; catalog.Add(item); }
                loaded = true; Filter();
                if (preview) return;
                var byId = catalog.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
                var progress = new Progress<Dictionary<string, Bitmap>>(batch =>
                { try { if (!disposed) foreach (var pair in batch) { TileItem item; if (byId.TryGetValue(pair.Key, out item)) item.Icon = TileGrid.ConvertIcon(pair.Value); } } finally { foreach (var image in batch.Values) image.Dispose(); } });
                var images = await AppIconService.LoadAsync(apps, 120, iconCancellation.Token, progress);
                try { if (!disposed) foreach (var pair in images) { TileItem item; if (byId.TryGetValue(pair.Key, out item) && item.Icon == null) item.Icon = TileGrid.ConvertIcon(pair.Value); } }
                finally { foreach (var image in images.Values) image.Dispose(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!disposed) Ui.Status(status, "Could not load apps: " + error.Message, true); }
            finally { loading = false; if (!disposed) UpdateControls(); }
        }
        private void OnSelection(TileItem item)
        {
            if (guard.IsArmed) return;
            if (item.Selected) selected.Add(item.Id); else selected.Remove(item.Id);
            saved[item.Id] = (InstalledApp)item.Tag;
            if (!preview) { dirty = true; saveTimer.Stop(); saveTimer.Start(); } UpdateCount(); UpdateControls();
        }
        private void Filter() { var filtered = catalog.FindAll(item => item.Name.IndexOf(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) >= 0); Tiles.SetItems(filtered); if (loaded) UpdateCount(); }
        private void UpdateCount() { count.Text = catalog.Count + " apps · " + selected.Count + " selected. System tools and unsupported apps are hidden."; }
        private bool Save(bool locked)
        {
            if (preview || stateStore == null) return false;
            try { var state = new LockState { Locked = locked }; foreach (string id in selected) { InstalledApp app; if (saved.TryGetValue(id, out app)) state.Apps.Add(app); } stateStore.Save(state); return true; }
            catch (Exception error) { Ui.Status(status, "Could not save locks: " + error.Message, true); return false; }
        }
        private bool CheckPassword(string title) { return passwords != null && passwords.HasPassword && new PasswordWindow(owner, passwords, false, title).ShowDialog() == true; }
        private bool ConfigurePassword()
        {
            if (preview) { Ui.Status(status, "Preview only: passwords and saved settings are unchanged."); return false; }
            if (passwords == null || guard.IsArmed) return false;
            if (passwords.HasPassword && !CheckPassword("Verify current password")) return false;
            if (new PasswordWindow(owner, passwords, true, "Set UtilityTool password").ShowDialog() != true) return false;
            Ui.Status(status, "Custom password saved."); UpdateControls(); return true;
        }
        private void Apply()
        {
            if (preview) { Ui.Status(status, "Preview only: no apps were locked."); return; }
            if (guard.IsArmed || passwords == null || selected.Count == 0) return;
            if (!passwords.HasPassword && !ConfigurePassword()) return;
            if (MessageBox.Show(owner, "Selected apps will ask for a password, including apps already open. A wrong password or cancelling closes that instance and may lose unsaved work. Continue?", "Apply app locks", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            try { if (!Save(true)) return; savedLocked = true; dirty = false; saveTimer.Stop(); guard.Arm(selected.Where(saved.ContainsKey).Select(id => saved[id]), true); Ui.Status(status, "Guard is active. A password is required to unlock or close UtilityTool."); }
            catch (Exception error) { Ui.Status(status, error.Message, true); }
            UpdateControls();
        }
        private void Unlock()
        {
            if (!guard.IsArmed || !CheckPassword("Unlock apps") || !Save(false)) return;
            savedLocked = false; dirty = false; saveTimer.Stop(); guard.Disarm(); ClearOverlays(); Ui.Status(status, "Apps unlocked."); UpdateControls();
        }
        private void UpdateControls()
        {
            refresh.IsEnabled = !loading && !guard.IsArmed; apply.IsEnabled = (preview || passwords != null) && !guard.IsArmed && selected.Count > 0; password.IsEnabled = (preview || passwords != null) && !guard.IsArmed; unlock.IsEnabled = guard.IsArmed;
            password.Content = passwords != null && passwords.HasPassword ? "Change password" : "Set password";
            foreach (var item in catalog) if (item.Editable == guard.IsArmed) item.Editable = !guard.IsArmed;
        }
        internal bool CanClose() { if (!guard.IsArmed) return true; if (!CheckPassword("Close UtilityTool")) return false; guard.Disarm(); ClearOverlays(); return true; }
        private void ClearOverlays() { foreach (var overlay in new List<AppLockOverlay>(overlays)) overlay.Cancel(); overlays.Clear(); }
        public void Dispose()
        { if (disposed) return; disposed = true; iconCancellation.Cancel(); saveTimer.Stop(); if (dirty) Save(savedLocked); guard.Dispose(); ClearOverlays(); iconCancellation.Dispose(); }
    }
}
