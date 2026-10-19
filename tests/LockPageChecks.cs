using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using UtilityTool.Backend;
using UtilityTool.Frontend.Wpf;

internal static class LockPageChecks
{
    internal static void Run(Action<bool, string> check, string output, Window owner, Action pump, Action<Window, string> capture)
    {
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { RunCore(check, output, owner, pump, capture); }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
    }
    private static void RunCore(Action<bool, string> check, string output, Window owner, Action pump, Action<Window, string> capture)
    {
        string root = Path.Combine(output, "lock-page-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var context = new Context(root); var pending = new TaskCompletionSource<List<InstalledApp>>(); context.Dependencies.CatalogLoader = () => pending.Task; context.HasPassword = false;
        using (var page = new LockPage(owner, false, context.Dependencies))
        {
            var window = Host(owner, page); var loading = page.LoadApps(); pump();
            check(Visuals<StackPanel>(page).Single(value => AccessibleName(value) == "Lock loading").Visibility == Visibility.Visible && Visuals<ProgressBar>(page).Single().IsIndeterminate, "Lock discovery immediately shows a visible loading indicator");
            capture(window, "Lock-loading"); pending.SetResult(context.Apps); Wait(loading, pump);
            check(Items(page).Count == 3 && Text(page, "Protection status") == "Protection off", "Installed apps appear with a clear Protection off state");
            check(Visuals<TextBlock>(page).Any(value => value.Text.Contains("1. Select app tiles")) && !Button(page, "Lock selected apps").IsEnabled, "First use explains the three steps and requires an app selection");
            TileItem alpha = Items(page).Single(item => item.Id == "alpha"); var icon = alpha.Icon; Wait(page.LoadApps(), pump);
            check(icon != null && ReferenceEquals(icon, Items(page).Single(item => item.Id == "alpha").Icon), "Refreshing unchanged apps reuses frozen icons");
            var search = Visuals<System.Windows.Controls.TextBox>(page).Single(value => AccessibleName(value) == "Search locked apps"); search.Text = "A"; search.Text = "Al"; search.Text = "Alpha";
            check(VisibleTileCount(page) == 3, "Typing a search does not rebuild the app grid on every keystroke");
            WaitFor(() => VisibleTileCount(page) == 1, pump);
            check(VisibleItems(page).Single().Id == "alpha", "Debounced search shows matching apps");
            search.Text = ""; WaitFor(() => VisibleTileCount(page) == 3, pump);
            Items(page).Single(item => item.Id == "alpha").Selected = true;
            var filter = Visuals<ComboBox>(page).Single(value => AccessibleName(value) == "Filter locked apps"); filter.SelectedIndex = 1; pump();
            check(VisibleItems(page).Single().Id == "alpha", "Selected apps filters the grid to the user's saved choices");
            WaitFor(() => context.States.Load().Apps.Count == 1, pump);
            check(!context.States.Load().Locked && context.States.Load().Apps[0].Id == "alpha", "Selecting apps saves the selection without enabling protection");
            filter.SelectedIndex = 0; search.Text = "nothing matches"; WaitFor(() => Visuals<TextBlock>(page).Single(value => AccessibleName(value) == "Lock empty state").Visibility == Visibility.Visible, pump);
            check(Text(page, "Lock empty state").Contains("No matching apps"), "An empty search result explains how to recover");
            search.Text = ""; WaitFor(() => VisibleTileCount(page) == 3, pump); capture(window, "Lock-first-use"); window.Close();
        }
        context.HasPassword = true; context.Dependencies.CatalogLoader = () => Task.FromResult(context.Apps);
        var missing = new InstalledApp("saved-missing", "Saved launcher", Path.Combine(root, "missing-launcher.exe")); var state = context.States.Load(); state.Apps.Add(missing); context.States.Save(state);
        using (var page = new LockPage(owner, false, context.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump);
            check(Items(page).Single(item => item.Id == missing.Id).Selected, "Saved selections remain visible when Windows discovery omits an app");
            Items(page).Single(item => item.Id == missing.Id).Selected = false;
            string executable = Path.Combine(root, "Manual tool.exe"); File.WriteAllText(executable, "Owned test fixture; never launched.");
            check(Wait(page.AddManualApp(executable), pump), "Add app accepts an installed executable without launching it");
            var manual = Items(page).Single(item => ((InstalledApp)item.Tag).ExecutablePath == executable);
            check(manual.Selected && context.Manual.Load().Apps.Any(app => app.Id == manual.Id), "A manually added app is selected and saved in its separate library");
            manual.Selected = false; WaitFor(() => !context.States.Load().Apps.Any(app => app.Id == manual.Id), pump);
            string settings = File.ReadAllText(context.StatePath); File.Delete(context.StatePath); Directory.CreateDirectory(context.StatePath);
            string rejected = Path.Combine(root, "Unsaved tool.exe"); File.WriteAllText(rejected, "Owned fixture; never launched.");
            check(!Wait(page.AddManualApp(rejected), pump) && !Items(page).Any(item => ((InstalledApp)item.Tag).ExecutablePath == rejected) && !context.Manual.Load().Apps.Any(app => app.ExecutablePath == rejected), "A failed selection save rolls manual import back without a success message or new tile");
            check(Visuals<TextBlock>(page).Any(value => value.Text.Contains("not added")), "Manual import reports a failed settings save clearly");
            Directory.Delete(context.StatePath); File.WriteAllText(context.StatePath, settings);
            check(!Wait(page.AddManualApp(Path.Combine(root, "wrong.txt")), pump) && !Wait(page.AddManualApp(Path.Combine(Environment.SystemDirectory, "cmd.exe")), pump), "Add app rejects non-executables and Windows system tools");
            window.Close();
        }
        using (var page = new LockPage(owner, false, context.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump);
            var manual = Items(page).Single(item => item.Id.StartsWith("manual:", StringComparison.OrdinalIgnoreCase));
            check(!manual.Selected, "Unselected manually added apps remain available after reopening");
            manual.Selected = true; Items(page).Single(item => item.Id == "alpha").Selected = true;
            Click(page, "Lock selected apps");
            check(context.Guard.IsArmed && context.States.Load().Locked && Text(page, "Protection status") == "Protection on", "Lock selected apps arms the injected guard and persists Protection on");
            check(!Button(page, "Add app…").IsEnabled && !Button(page, "Remove added apps").IsEnabled && !Button(page, "Change password").IsEnabled && Items(page).All(item => !item.Editable), "Protection disables selection, manual-app changes and password changes");
            manual.Selected = false;
            check(manual.Selected && !Wait(page.AddManualApp(((InstalledApp)manual.Tag).ExecutablePath), pump), "Programmatic selection and manual-add attempts cannot bypass active protection");
            context.Verify = false; Click(page, "Turn off protection");
            check(context.Guard.IsArmed && context.States.Load().Locked && !page.CanClose(), "Cancelling password verification keeps protection on and prevents closing");
            capture(window, "Lock-protected");
            context.Verify = true; string settings = File.ReadAllText(context.StatePath); File.Delete(context.StatePath); Directory.CreateDirectory(context.StatePath);
            Click(page, "Turn off protection");
            check(context.Guard.IsArmed && Text(page, "Protection status") == "Protection on", "A failed settings save cannot turn off active protection");
            Directory.Delete(context.StatePath); File.WriteAllText(context.StatePath, settings); Click(page, "Turn off protection");
            check(!context.Guard.IsArmed && !context.States.Load().Locked && context.States.Load().Apps.Count == 2, "Verified Turn off protection keeps the selected apps saved");
            settings = File.ReadAllText(context.StatePath); File.Delete(context.StatePath); Directory.CreateDirectory(context.StatePath); Click(page, "Remove added apps");
            check(Items(page).Any(item => item.Id == manual.Id && item.Selected) && context.Manual.Load().Apps.Any(app => app.Id == manual.Id) && Visuals<TextBlock>(page).Any(value => value.Text.Contains("not removed")), "A failed removal save retains the manual app and its selection with an explicit error");
            Directory.Delete(context.StatePath); File.WriteAllText(context.StatePath, settings);
            Click(page, "Remove added apps");
            check(!Items(page).Any(item => item.Id.StartsWith("manual:", StringComparison.OrdinalIgnoreCase)) && context.Manual.Load().Apps.Count == 0, "Removing selected added apps updates both the library and the grid");
            window.Close();
        }
        context.Guard = FakeGuard(); context.Dependencies.Guard = context.Guard; context.Dependencies.ArmGuard = (apps, timer) => { throw new InvalidOperationException("Owned simulated target-resolution failure"); };
        using (var page = new LockPage(owner, false, context.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Click(page, "Lock selected apps");
            check(!context.Guard.IsArmed && !context.States.Load().Locked && Text(page, "Protection status") == "Protection off", "A guard activation failure rolls persisted protection back and reports Protection off"); window.Close();
        }
        context.Guard = FakeGuard(); context.Dependencies.Guard = context.Guard; context.Dependencies.ArmGuard = null;
        state = context.States.Load(); state.Locked = true; context.States.Save(state);
        using (var page = new LockPage(owner, false, context.Dependencies))
        {
            page.InitializeSavedLocks(); check(context.Guard.IsArmed, "Saved Protection on restores through the injected empty-snapshot guard");
        }
        File.WriteAllText(Path.Combine(root, "customapps.xml"), "Invalid owned fixture"); context.Guard = FakeGuard(); context.Dependencies.Guard = context.Guard;
        using (var page = new LockPage(owner, false, context.Dependencies))
        {
            page.InitializeSavedLocks(); check(context.Guard.IsArmed && context.States.Load().Locked, "A damaged manual-app library cannot prevent saved protection from restoring");
        }
        var recovery = new Context(Path.Combine(root, "missing-password")); recovery.HasPassword = false;
        state = new LockState { Locked = true }; state.Apps.Add(recovery.Apps[0]); recovery.States.Save(state); bool createPassword = false;
        recovery.Dependencies.PasswordDialog = (create, title) => { if (create && createPassword) { recovery.HasPassword = true; return true; } return !create && recovery.Verify; };
        using (var page = new LockPage(owner, false, recovery.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump);
            check(!recovery.Guard.IsArmed && Button(page, "Set password").IsEnabled && !Button(page, "Add app…").IsEnabled && Items(page).All(item => !item.Editable), "A genuinely missing password enables password recovery while retaining saved choices and protection intent");
            Click(page, "Set password");
            check(!recovery.Guard.IsArmed && recovery.States.Load().Locked && !recovery.HasPassword, "Cancelling password recovery preserves the pending protection state");
            createPassword = true; Click(page, "Set password");
            check(recovery.Guard.IsArmed && recovery.States.Load().Locked && Text(page, "Protection status") == "Protection on", "Creating the missing password restores saved protection without changing app choices");
            window.Close();
        }
        recovery = new Context(Path.Combine(root, "missing-password-target-failure")); recovery.HasPassword = false;
        state = new LockState { Locked = true }; state.Apps.Add(recovery.Apps[0]); recovery.States.Save(state);
        recovery.Dependencies.PasswordDialog = (create, title) => { if (create) { recovery.HasPassword = true; return true; } return recovery.Verify; };
        recovery.Dependencies.ArmGuard = (apps, timer) => { throw new InvalidOperationException("Owned unavailable target"); };
        using (var page = new LockPage(owner, false, recovery.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Click(page, "Set password");
            check(!recovery.Guard.IsArmed && recovery.States.Load().Locked && Button(page, "Turn off protection").IsEnabled && !Button(page, "Change password").IsEnabled && Items(page).All(item => !item.Editable), "Failed recovery activation retains protection intent and allows verified Turn off protection");
            recovery.Verify = false; Click(page, "Turn off protection"); check(recovery.States.Load().Locked && Items(page).All(item => !item.Editable), "The new password must be verified before pending protection can be cleared");
            recovery.Verify = true; Click(page, "Turn off protection"); check(!recovery.States.Load().Locked && Items(page).All(item => item.Editable), "Verified recovery unlock permits reviewing unavailable saved apps"); window.Close();
        }
        recovery = new Context(Path.Combine(root, "unreadable-password")); recovery.HasPassword = false; recovery.Dependencies.Passwords = null; int recoveryDialogs = 0;
        state = new LockState { Locked = true }; state.Apps.Add(recovery.Apps[0]); recovery.States.Save(state); recovery.Dependencies.PasswordDialog = (create, title) => { recoveryDialogs++; return true; };
        using (var page = new LockPage(owner, false, recovery.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Click(page, "Set password");
            check(!Button(page, "Set password").IsEnabled && recoveryDialogs == 0 && recovery.States.Load().Locked && !recovery.Guard.IsArmed, "An unreadable password store cannot be overwritten by the missing-password recovery action"); window.Close();
        }
        int attempts = 0; var failedContext = new Context(Path.Combine(root, "discovery-failure")); failedContext.Dependencies.CatalogLoader = () => { if (attempts++ == 0) throw new IOException("Owned discovery failure"); return Task.FromResult(failedContext.Apps); };
        using (var page = new LockPage(owner, false, failedContext.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump);
            check(Text(page, "Lock empty state").Contains("Add app") && Visuals<TextBlock>(page).Any(value => value.Text.Contains("Try Refresh")), "Failed discovery shows actionable error and empty states instead of a blank page");
            Wait(page.LoadApps(), pump); check(Items(page).Count == 3 && Visuals<TextBlock>(page).Any(value => value.Text == "Apps refreshed."), "Refresh retries failed Windows discovery and clears the stale error"); window.Close();
        }
        var previewContext = new Context(Path.Combine(root, "preview")); previewContext.HasPassword = false; int callbacks = 0; previewContext.Dependencies.CatalogLoader = () => { callbacks++; return Task.FromResult(previewContext.Apps); }; previewContext.Dependencies.PasswordDialog = (create, title) => { callbacks++; return true; };
        using (var page = new LockPage(owner, true, previewContext.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Items(page)[0].Selected = true;
            check(!Button(page, "Add app…").IsEnabled && !Button(page, "Set password").IsEnabled && !Button(page, "Lock selected apps").IsEnabled && !Button(page, "Turn off protection").IsEnabled && callbacks == 0 && !File.Exists(previewContext.StatePath), "Preview permits sample selection while avoiding real discovery, dialogs and saved settings"); window.Close();
        }
    }
    private static Window Host(Window owner, LockPage page) { var window = new Wpf.Ui.Controls.FluentWindow { Owner = owner, Width = 1040, Height = 740, Content = page, Background = owner.Background, Foreground = System.Windows.Media.Brushes.WhiteSmoke }; window.Show(); return window; }
    private static string AccessibleName(DependencyObject value) { return System.Windows.Automation.AutomationProperties.GetName(value); }
    private static string Text(LockPage page, string name) { return Visuals<TextBlock>(page).Single(value => AccessibleName(value) == name).Text; }
    private static global::Wpf.Ui.Controls.Button Button(LockPage page, string text) { return Visuals<global::Wpf.Ui.Controls.Button>(page).Single(value => string.Equals(value.Content as string, text, StringComparison.Ordinal)); }
    private static void Click(LockPage page, string text) { Button(page, text).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static List<TileItem> Items(LockPage page) { return (List<TileItem>)typeof(LockPage).GetField("catalog", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(page); }
    private static List<TileItem> VisibleItems(LockPage page) { return page.Tiles.Items.Cast<TileRow>().SelectMany(row => row.Items).ToList(); }
    private static int VisibleTileCount(LockPage page) { return VisibleItems(page).Count; }
    private static void Wait(Task task, Action pump) { WaitFor(() => task.IsCompleted, pump); task.GetAwaiter().GetResult(); }
    private static T Wait<T>(Task<T> task, Action pump) { Wait((Task)task, pump); return task.GetAwaiter().GetResult(); }
    private static void WaitFor(Func<bool> condition, Action pump) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!condition() && clock.ElapsedMilliseconds < 10000) { pump(); Thread.Sleep(5); } if (!condition()) throw new TimeoutException("Lock page check timed out."); }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject { if (root is T) yield return (T)root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var item in Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return item; }
    private static AppGuardService FakeGuard() { return new AppGuardService(() => new List<GuardProcess>(), process => { throw new InvalidOperationException("No test process may be stopped."); }); }
    private sealed class Context
    {
        internal bool HasPassword = true, Verify = true;
        internal readonly string StatePath;
        internal readonly LockStateStore States, Manual;
        internal AppGuardService Guard;
        internal readonly List<InstalledApp> Apps;
        internal readonly LockPageDependencies Dependencies;
        internal Context(string folder)
        {
            Directory.CreateDirectory(folder); StatePath = Path.Combine(folder, "locks.xml"); States = new LockStateStore(StatePath); Manual = new LockStateStore(Path.Combine(folder, "customapps.xml")); Guard = FakeGuard();
            Apps = new[] { "Alpha", "Beta", "Gamma" }.Select(name => new InstalledApp(name.ToLowerInvariant(), name, Path.Combine(folder, name + ".exe"))).ToList();
            Dependencies = new LockPageDependencies { Passwords = new PasswordStore(Path.Combine(folder, "unused-test-password.dat")), StateStore = States, CustomAppsStore = Manual, Guard = Guard, HasPassword = () => HasPassword, PasswordDialog = (create, title) => !create && Verify, Confirm = (message, title) => true, ChooseExecutable = () => null, StartGuardTimer = false, CatalogLoader = () => Task.FromResult(Apps), IconLoader = Icons };
        }
        private static Task<Dictionary<string, Bitmap>> Icons(IEnumerable<InstalledApp> apps, int size, CancellationToken token, IProgress<Dictionary<string, Bitmap>> progress)
        {
            var images = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps) { token.ThrowIfCancellationRequested(); var image = new Bitmap(8, 8); using (var graphics = Graphics.FromImage(image)) graphics.Clear(System.Drawing.Color.CornflowerBlue); images[app.Id] = image; }
            return Task.FromResult(images);
        }
    }
}
