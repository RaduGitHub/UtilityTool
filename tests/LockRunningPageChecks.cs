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
using System.Windows.Input;
using System.Windows.Media;
using UtilityTool.Backend;
using UtilityTool.Frontend.Wpf;

internal static class LockRunningPageChecks
{
    internal static void Run(Action<bool, string> check, string output, Window owner, Action pump, Action<Window, string> capture)
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try { RunCore(check, output, owner, pump, capture); } finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunCore(Action<bool, string> check, string output, Window owner, Action pump, Action<Window, string> capture)
    {
        string root = Path.Combine(output, "owned-running-locks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var context = new Context(root); context.States.Save(new LockState());
        using (var page = new LockPage(owner, false, context.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); pump();
            check(page.Programs.Items.Count == 3 && context.Loads == 1, "Running programs use injected live windows, including an inaccessible window with a reason");
            check(!Command(page.Programs.Items[2], "MembershipCommand").CanExecute(null), "Inaccessible program metadata cannot be protected");
            var first = page.Programs.Items[0]; page.Programs.SelectedItem = first; pump(); check(context.States.Load().Apps.Count == 0, "Selecting a program row does not change saved membership");
            Command(first, "MembershipCommand").Execute(null); pump();
            check(context.States.Load().Apps.Count == 1 && !context.States.Load().Locked && !context.Guard.IsArmed, "Lock adds durable membership without silently enabling monitoring");
            check((string)Property(first, "MembershipLabel") == "Remove lock", "A running row updates its action after protection is saved");
            check(context.Dialogs == 0, "An existing global password is reused without creating another password");
            Click(page, "Turn on monitoring"); pump(); check(context.Guard.IsArmed && context.States.Load().Locked, "The monitoring button persists and arms the saved program list");
            Command(page.Programs.Items[1], "MembershipCommand").Execute(null); pump(); check(context.States.Load().Apps.Count == 2 && context.Updates == 1, "Adding another app while monitoring uses the atomic target-update API");
            context.Verify = false; Command(first, "MembershipCommand").Execute(null); pump(); check(context.States.Load().Apps.Count == 2, "Removing an app lock requires the global password");
            context.Verify = true; context.FailUpdate = true; byte[] before = File.ReadAllBytes(context.StatePath); Command(first, "MembershipCommand").Execute(null); pump();
            check(context.States.Load().Apps.Count == 2 && before.SequenceEqual(File.ReadAllBytes(context.StatePath)) && context.Guard.IsArmed, "A target-update failure rolls persisted membership back without disabling monitoring");
            context.FailUpdate = false; Command(first, "MembershipCommand").Execute(null); pump(); check(context.States.Load().Apps.Count == 1 && context.Guard.IsArmed && (string)Property(first, "MembershipLabel") == "Lock", "Verified removal updates the row and leaves unrelated monitoring active");
            var second = page.Programs.Items[1]; Command(second, "RelockCommand").Execute(null); Click(page, "Lock all now"); check(context.Relocks == 2, "Per-program Lock now and Lock all now use the injected relock action");
            Command(second, "PasswordCommand").Execute(null); check(context.PasswordChanges == 1 && context.Relocks == 3, "Changing an app password verifies the master password and relocks its current session");
            Click(page, "Locked programs (1)"); pump(); check(page.Programs.Items.Count == 1 && (string)Property(page.Programs.Items[0], "Title") == "Beta", "Locked programs contains saved durable apps rather than every running window");
            context.Programs.Clear(); Wait(page.LoadApps(), pump); check(page.Programs.Items.Count == 1 && ((string)Property(page.Programs.Items[0], "Detail")).Contains("Not running"), "A closed protected app remains visible in Locked programs");
            capture(window, "running-lock-protected-minimum");
            Click(page, "Running programs"); Wait(page.LoadApps(), pump); check(page.Programs.Items.Count == 0, "Running programs removes windows that are no longer open");
            context.Programs.Add(context.Create("Alpha", 1)); Wait(page.LoadApps(), pump); first = page.Programs.Items[0]; context.Programs[0] = context.Create("Alpha", 1, "New Alpha document"); Wait(page.LoadApps(), pump);
            check(ReferenceEquals(first, page.Programs.Items[0]) && (string)Property(first, "Title") == "New Alpha document", "Live refresh reuses unchanged row objects and updates their window title");
            context.FailLoad = true; Wait(page.LoadApps(), pump); check(page.Programs.Items.Count == 1 && Texts(page).Any(text => text.Contains("Could not refresh running programs")), "A failed refresh preserves the last successful live list and reports an actionable error"); context.FailLoad = false;
            var search = Visuals<global::Wpf.Ui.Controls.TextBox>(page).Single(); search.Text = "alpha.exe"; PumpUntil(() => page.Programs.Items.Count == 1, pump); check(page.Programs.Items.Count == 1, "Program search includes executable names"); search.Text = "no match"; PumpUntil(() => page.Programs.Items.Count == 0, pump); check(Texts(page).Any(text => text.Contains("No matching programs")), "An unmatched search displays its empty state"); search.Text = ""; PumpUntil(() => page.Programs.Items.Count == 1, pump);
            context.Verify = false; Click(page, "Turn off monitoring"); check(context.Guard.IsArmed && context.States.Load().Locked, "Stopping monitoring rejects an incorrect master password"); context.Verify = true; Click(page, "Turn off monitoring"); check(!context.Guard.IsArmed && !context.States.Load().Locked && context.States.Load().Apps.Count == 1, "Stopping monitoring saves membership and disarms only after password verification");
            string manual = Path.Combine(root, "Manual.exe"); File.WriteAllBytes(manual, new byte[] { 1, 2, 3 }); check(Wait(page.AddManualApp(manual), pump) && context.States.Load().Apps.Count == 2, "The executable fallback adds a nonrunning protected app without running its file");
            context.Picked = context.Programs[0]; Click(page, "Pick a window…"); pump(); check(context.States.Load().Apps.Count == 3, "Injected Pick a window adds the chosen identity without native desktop actions");
            context.PendingPick = new TaskCompletionSource<RunningApp>(); Click(page, "Pick a window…"); window.Hide(); pump(); context.PendingPick.SetResult(context.Create("Cancelled", 99)); PumpUntil(() => Button(page, "Pick a window…").IsEnabled, pump); window.Show(); pump(); check(context.States.Load().Apps.Count == 3 && Button(page, "Pick a window…").IsEnabled, "Hiding the page cancels a pending picker and restores its controls when shown again");
            window.Width = 1600; window.Height = 900; pump(); capture(window, "running-lock-wide"); check(page.Programs.ActualWidth > 1300, "The virtualized running list stretches with a wider application window");
            check(page.CanClose(), "Monitoring off allows the application to close without another password prompt"); window.Close();
        }
        var recovery = new Context(Path.Combine(root, "missing-password")); recovery.HasPassword = false; var saved = new LockState { Locked = true }; saved.Apps.Add(recovery.Programs[0].App); recovery.States.Save(saved);
        using (var page = new LockPage(owner, false, recovery.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); check(!recovery.Guard.IsArmed && Button(page, "Set password").IsEnabled, "Missing-password startup retains saved protection intent and offers password recovery"); Click(page, "Set password"); pump(); check(recovery.Guard.IsArmed && recovery.States.Load().Locked && recovery.States.Load().Apps.Count == 1, "Creating a missing password restores the existing saved locks"); window.Close();
        }
        var preview = new Context(Path.Combine(root, "preview")); using (var page = new LockPage(owner, true, preview.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); int callbacks = preview.Loads + preview.Dialogs; Click(page, "Turn on monitoring"); Click(page, "Add app…"); Click(page, "Pick a window…"); foreach (var row in page.Programs.Items.Cast<object>()) Command(row, "MembershipCommand").Execute(null); pump();
            check(page.Programs.Items.Count > 0 && callbacks == 0 && preview.Loads + preview.Dialogs == 0 && !File.Exists(preview.StatePath) && !preview.Guard.IsArmed, "Preview shows samples while guarding direct-raised actions against discovery, settings, passwords and monitoring"); capture(window, "running-lock-preview"); window.Close();
        }
        RunRecoveryAndFailureChecks(check, root, owner, pump, capture);
    }
    private static void RunRecoveryAndFailureChecks(Action<bool, string> check, string root, Window owner, Action pump, Action<Window, string> capture)
    {
        var loading = new Context(Path.Combine(root, "loading")); var pending = new TaskCompletionSource<List<RunningApp>>(); loading.Dependencies.RunningProgramsLoader = () => pending.Task;
        using (var page = new LockPage(owner, false, loading.Dependencies))
        {
            var window = Host(owner, page); var task = page.LoadApps(); pump();
            check(Visuals<StackPanel>(page).Single(value => AccessibleName(value) == "Lock loading").Visibility == Visibility.Visible && Visuals<ProgressBar>(page).Single().IsIndeterminate, "Initial running-program discovery shows an immediate loading indicator");
            pending.SetResult(new List<RunningApp>(loading.Programs)); Wait(task, pump); var row = page.Programs.Items[0]; var icon = Property(row, "Icon"); Wait(page.LoadApps(), pump);
            check(icon != null && ReferenceEquals(icon, Property(page.Programs.Items[0], "Icon")), "Refreshing unchanged program metadata reuses frozen icon objects");
            var search = Visuals<global::Wpf.Ui.Controls.TextBox>(page).Single(); search.Text = "B"; search.Text = "Be"; search.Text = "Beta"; check(page.Programs.Items.Count == 3, "Search input is debounced rather than rebuilding the list for every key"); PumpUntil(() => page.Programs.Items.Count == 1, pump); check((string)Property(page.Programs.Items[0], "Title") == "Beta window", "Debounced search resolves the final program query"); window.Close();
        }

        var restore = new Context(Path.Combine(root, "restore-metadata")); var original = new InstalledApp(restore.Programs[0].App.Id, "Existing friendly title", restore.Programs[0].ExecutablePath, "--preserve-saved-arguments"); var missing = new InstalledApp("old-missing", "Missing saved launcher", Path.Combine(root, "temporarily-missing.exe"), "--saved-launcher"); var state = new LockState { Locked = true }; state.Apps.Add(original); state.Apps.Add(missing); restore.States.Save(state); byte[] originalXml = File.ReadAllBytes(restore.StatePath);
        using (var page = new LockPage(owner, false, restore.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Wait(page.LoadApps(), pump); check(restore.Guard.IsArmed && originalXml.SequenceEqual(File.ReadAllBytes(restore.StatePath)), "Startup and repeated discovery preserve existing IDs, friendly names, executable paths and launch arguments byte for byte"); Click(page, "Locked programs (2)"); pump(); check(page.Programs.Items.Count == 2 && page.Programs.Items.Cast<object>().Any(row => (string)Property(row, "Title") == missing.Name && ((string)Property(row, "Detail")).Contains("Not running")), "Saved apps missing from current discovery remain manageable in Locked programs"); capture(window, "running-lock-restored-metadata"); window.Close();
        }
        restore = new Context(Path.Combine(root, "damaged-manual")); state = new LockState { Locked = true }; state.Apps.Add(restore.Programs[0].App); restore.States.Save(state); File.WriteAllText(Path.Combine(Path.GetDirectoryName(restore.StatePath), "customapps.xml"), "Owned corrupt manual-app fixture");
        using (var page = new LockPage(owner, false, restore.Dependencies))
        {
            page.InitializeSavedLocks(); check(restore.Guard.IsArmed && restore.States.Load().Locked && restore.States.Load().Apps.Count == 1, "A damaged manual-app library cannot prevent the independent saved lock list from restoring");
        }

        var corrupt = new Context(Path.Combine(root, "corrupt-state")); File.WriteAllText(corrupt.StatePath, "Owned corrupt lock state; retain these bytes."); byte[] corruptXml = File.ReadAllBytes(corrupt.StatePath);
        using (var page = new LockPage(owner, false, corrupt.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); check(!Button(page, "Turn on monitoring").IsEnabled && !Button(page, "Add app…").IsEnabled && page.Programs.Items.Cast<object>().All(row => !Command(row, "MembershipCommand").CanExecute(null)), "Unreadable saved lock state disables all state-changing program actions");
            Click(page, "Turn on monitoring"); foreach (var row in page.Programs.Items.Cast<object>()) Command(row, "MembershipCommand").Execute(null); string executable = Path.Combine(root, "corrupt-state-manual.exe"); File.WriteAllBytes(executable, new byte[] { 1 }); bool added = Wait(page.AddManualApp(executable), pump);
            check(!added && corruptXml.SequenceEqual(File.ReadAllBytes(corrupt.StatePath)) && !corrupt.Guard.IsArmed, "Direct-raised monitoring, row and manual-import attempts cannot overwrite a corrupt saved state"); window.Close();
        }

        var recovery = new Context(Path.Combine(root, "cancel-password-recovery")); recovery.HasPassword = false; state = new LockState { Locked = true }; state.Apps.Add(recovery.Programs[0].App); recovery.States.Save(state); bool allowCreate = false; recovery.Dependencies.PasswordDialog = (create, title) => { recovery.Dialogs++; if (create && allowCreate) { recovery.HasPassword = true; return true; } return !create && recovery.Verify; };
        using (var page = new LockPage(owner, false, recovery.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Click(page, "Set password"); check(!recovery.HasPassword && !recovery.Guard.IsArmed && recovery.States.Load().Locked && recovery.States.Load().Apps.Count == 1, "Cancelling missing-password recovery retains the saved monitoring intent and app choices"); allowCreate = true; Click(page, "Set password"); check(recovery.Guard.IsArmed && recovery.States.Load().Locked && recovery.States.Load().Apps.Count == 1, "Completing password recovery restores monitoring without replacing existing saved apps"); window.Close();
        }
        recovery = new Context(Path.Combine(root, "failed-password-recovery")); recovery.HasPassword = false; state = new LockState { Locked = true }; state.Apps.Add(recovery.Programs[0].App); recovery.States.Save(state); recovery.Dependencies.ArmGuard = (apps, start) => { throw new IOException("Owned unavailable guard target"); };
        using (var page = new LockPage(owner, false, recovery.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Click(page, "Set password"); check(!recovery.Guard.IsArmed && recovery.States.Load().Locked && Button(page, "Turn off monitoring").IsEnabled && !Button(page, "Change password").IsEnabled, "A failed recovery activation retains protection intent and allows the new master password to turn monitoring off"); recovery.Verify = false; Click(page, "Turn off monitoring"); check(recovery.States.Load().Locked, "Failed recovery still requires password verification before clearing saved monitoring intent"); recovery.Verify = true; Click(page, "Turn off monitoring"); check(!recovery.States.Load().Locked && recovery.States.Load().Apps.Count == 1, "Verified recovery can stop pending monitoring while retaining the saved programs"); window.Close();
        }
        recovery = new Context(Path.Combine(root, "unreadable-master-password")); recovery.HasPassword = false; recovery.Dependencies.Passwords = null; state = new LockState { Locked = true }; state.Apps.Add(recovery.Programs[0].App); recovery.States.Save(state);
        using (var page = new LockPage(owner, false, recovery.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Click(page, "Set password"); check(!Button(page, "Set password").IsEnabled && recovery.Dialogs == 0 && recovery.States.Load().Locked && !recovery.Guard.IsArmed, "An unreadable master password store is never replaced by missing-password recovery"); window.Close();
        }

        var failed = new Context(Path.Combine(root, "arm-failure")); state = new LockState(); state.Apps.Add(failed.Programs[0].App); failed.States.Save(state); failed.Dependencies.ArmGuard = (apps, start) => { throw new IOException("Owned activation failure"); };
        using (var page = new LockPage(owner, false, failed.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); Click(page, "Turn on monitoring"); check(!failed.Guard.IsArmed && !failed.States.Load().Locked && Texts(page).Any(text => text == "Monitoring off"), "A guard activation failure rolls the persisted monitoring state back to off"); window.Close();
        }
        failed = new Context(Path.Combine(root, "save-failures")); state = new LockState { Locked = true }; state.Apps.Add(failed.Programs[0].App); failed.States.Save(state);
        using (var page = new LockPage(owner, false, failed.Dependencies))
        {
            var window = Host(owner, page); Wait(page.LoadApps(), pump); string savedXml = File.ReadAllText(failed.StatePath); File.Delete(failed.StatePath); Directory.CreateDirectory(failed.StatePath);
            Click(page, "Turn off monitoring"); check(failed.Guard.IsArmed && Texts(page).Any(text => text.Contains("Could not turn off monitoring")), "An unwritable lock-state file cannot turn off active monitoring");
            string manual = Path.Combine(root, "Rollback manual.exe"); File.WriteAllBytes(manual, new byte[] { 2, 3 }); bool added = Wait(page.AddManualApp(manual), pump); var manualStore = new LockStateStore(Path.Combine(Path.GetDirectoryName(failed.StatePath), "customapps.xml"));
            check(!added && manualStore.Load().Apps.Count == 0 && !page.Programs.Items.Cast<object>().Any(row => (string)Property(row, "Executable") == manual), "A failed membership save rolls the separate manual-app import back without a new protected row"); Directory.Delete(failed.StatePath); File.WriteAllText(failed.StatePath, savedXml);
            check(!Wait(page.AddManualApp(Path.Combine(root, "not-an-executable.txt")), pump) && !Wait(page.AddManualApp(Path.Combine(Environment.SystemDirectory, "cmd.exe")), pump), "Manual fallback rejects non-executables and Windows system tools");
            var first = page.Programs.Items[0]; Command(first, "MembershipCommand").Execute(null); check(failed.Guard.IsArmed && failed.States.Load().Locked && failed.States.Load().Apps.Count == 0 && Button(page, "Turn off monitoring").IsEnabled, "Removing the final protected app leaves monitoring on and permits authenticated shutdown of monitoring");
            Command(first, "MembershipCommand").Execute(null); check(failed.Guard.IsArmed && failed.States.Load().Apps.Count == 1, "A program can be added again while the empty monitoring session remains active"); window.Close();
        }
        var emptyMonitoring = new Context(Path.Combine(root, "empty-monitoring")); emptyMonitoring.States.Save(new LockState { Locked = true });
        byte[] emptyState = File.ReadAllBytes(emptyMonitoring.StatePath);
        using (var page = new LockPage(owner, false, emptyMonitoring.Dependencies))
        {
            page.InitializeSavedLocks();
            check(emptyMonitoring.Guard.IsArmed && emptyState.SequenceEqual(File.ReadAllBytes(emptyMonitoring.StatePath)), "Restarting after the last protected app is removed restores monitoring intent without rewriting settings or reporting a failure");
        }
        var disposed = new Context(Path.Combine(root, "disposed-load")); var late = new TaskCompletionSource<List<RunningApp>>(); disposed.Dependencies.RunningProgramsLoader = () => late.Task;
        using (var page = new LockPage(owner, false, disposed.Dependencies))
        {
            var window = Host(owner, page); var task = page.LoadApps(); page.Dispose(); late.SetResult(disposed.Programs); Wait(task, pump); check(page.Programs.Items.Count == 0 && !disposed.Guard.IsArmed && !File.Exists(disposed.StatePath), "A late metadata result after disposal cannot repopulate the page, arm monitoring or create settings"); window.Close();
        }
    }
    private static Window Host(Window owner, LockPage page) { var window = new global::Wpf.Ui.Controls.FluentWindow { Owner = owner, Width = 1040, Height = 740, Content = page, Background = owner.Background, Foreground = System.Windows.Media.Brushes.WhiteSmoke }; window.Show(); return window; }
    private static object Property(object value, string name) { return value.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance).GetValue(value, null); }
    private static string AccessibleName(DependencyObject value) { return System.Windows.Automation.AutomationProperties.GetName(value); }
    private static ICommand Command(object value, string name) { return (ICommand)Property(value, name); }
    private static global::Wpf.Ui.Controls.Button Button(DependencyObject root, string label) { return Visuals<global::Wpf.Ui.Controls.Button>(root).Single(value => value.Content as string == label); }
    private static void Click(DependencyObject root, string label) { Button(root, label).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static IEnumerable<string> Texts(DependencyObject root) { return Visuals<TextBlock>(root).Select(value => value.Text); }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject { if (root is T) yield return (T)root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var item in Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return item; }
    private static void Wait(Task task, Action pump) { PumpUntil(() => task.IsCompleted, pump); task.GetAwaiter().GetResult(); }
    private static T Wait<T>(Task<T> task, Action pump) { Wait((Task)task, pump); return task.GetAwaiter().GetResult(); }
    private static void PumpUntil(Func<bool> condition, Action pump) { var watch = System.Diagnostics.Stopwatch.StartNew(); do { pump(); Thread.Sleep(5); } while (!condition() && watch.ElapsedMilliseconds < 10000); if (!condition()) throw new TimeoutException("Running lock UI check timed out."); }
    private sealed class Context
    {
        internal readonly string StatePath;
        internal readonly LockStateStore States;
        internal readonly AppGuardService Guard = new AppGuardService(() => new List<GuardProcess>(), process => { throw new Exception("A test must never stop a real process."); });
        internal readonly List<RunningApp> Programs = new List<RunningApp>();
        internal readonly LockPageDependencies Dependencies;
        internal bool HasPassword = true, Verify = true, FailUpdate, FailLoad;
        internal int Loads, Dialogs, Updates, Relocks, PasswordChanges;
        internal RunningApp Picked;
        internal TaskCompletionSource<RunningApp> PendingPick;
        private readonly string root;
        internal Context(string root)
        {
            this.root = root; Directory.CreateDirectory(root); StatePath = Path.Combine(root, "locks.xml"); States = new LockStateStore(StatePath); Programs.Add(Create("Alpha", 1)); Programs.Add(Create("Beta", 2)); var inaccessibleProcess = new RunningAppProcessSnapshot(3, 3, "Private.exe", ""); Programs.Add(new RunningApp(new RunningAppWindowSnapshot(new IntPtr(3), "Unavailable program", 3, new[] { inaccessibleProcess }), inaccessibleProcess, null, "Windows denied access to this process."));
            Dependencies = new LockPageDependencies { Passwords = new PasswordStore(Path.Combine(root, "unused-password.dat")), StateStore = States, CustomAppsStore = new LockStateStore(Path.Combine(root, "customapps.xml")), Guard = Guard, HasPassword = () => HasPassword, PasswordDialog = (create, title) => { Dialogs++; if (create) { HasPassword = true; return true; } return Verify; }, StartGuardTimer = false, RunningProgramsLoader = () => { Loads++; if (FailLoad) throw new IOException("Owned snapshot error"); return Task.FromResult(new List<RunningApp>(Programs)); }, IconLoader = Icons, UpdateGuardTargets = apps => { Updates++; if (FailUpdate) throw new IOException("Owned target-update error"); Guard.UpdateTargets(apps); }, RelockGuard = app => { Relocks++; Guard.Relock(app); }, ConfigureAppPassword = app => { PasswordChanges++; return true; }, PickWindow = () => PendingPick == null ? Task.FromResult(Picked) : PendingPick.Task, ChooseExecutable = () => null };
        }
        internal RunningApp Create(string name, int id, string title = null)
        {
            string executable = Path.Combine(root, name + ".exe"); var process = new RunningAppProcessSnapshot(id, id, name + ".exe", executable); var app = new InstalledApp("fake:" + name, name, executable); return new RunningApp(new RunningAppWindowSnapshot(new IntPtr(id), title ?? name + " window", id, new[] { process }), process, app, "");
        }
        private static Task<Dictionary<string, Bitmap>> Icons(IEnumerable<InstalledApp> apps, int size, CancellationToken token, IProgress<Dictionary<string, Bitmap>> progress)
        {
            var images = new Dictionary<string, Bitmap>(); foreach (var app in apps.GroupBy(value => value.Id).Select(value => value.First())) { var bitmap = new Bitmap(32, 32); using (var graphics = Graphics.FromImage(bitmap)) { graphics.Clear(System.Drawing.Color.Transparent); using (var brush = new SolidBrush(System.Drawing.Color.SteelBlue)) graphics.FillEllipse(brush, 2, 2, 28, 28); } images[app.Id] = bitmap; } return Task.FromResult(images);
        }
    }
}
