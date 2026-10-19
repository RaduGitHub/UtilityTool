using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UtilityTool.Backend;
using UtilityTool.Frontend.Wpf;

internal static class LockMainChecks
{
    internal static void Run(Action<bool, string> check, string output, Action pump)
    {
        string root = Path.Combine(output, "session-exit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "owned-app.exe"); File.WriteAllText(executable, "Owned metadata fixture; never executed.");
        var app = new InstalledApp(executable, "Owned app", executable);
        var store = new LockStateStore(Path.Combine(root, "locks.xml")); var state = new LockState { Locked = true }; state.Apps.Add(app); store.Save(state);
        int passwordPrompts = 0, stopped = 0; bool verify = false;
        var guard = new AppGuardService(() => new List<GuardProcess>(), process => stopped++);
        var dependencies = new LockPageDependencies
        {
            Guard = guard, Passwords = new PasswordStore(Path.Combine(root, "unused-test-password.dat")), StateStore = store, CustomAppsStore = new LockStateStore(Path.Combine(root, "customapps.xml")),
            HasPassword = () => true, PasswordDialog = (create, title) => { passwordPrompts++; return !create && verify; },
            RunningProgramsLoader = () => Task.FromResult(new List<RunningApp>()),
            IconLoader = (apps, size, cancellation, progress) => Task.FromResult(new Dictionary<string, System.Drawing.Bitmap>()),
            StartGuardTimer = false
        };
        var window = new FluentMainWindow(true, lockFactory: (owner, preview) => new LockPage(owner, false, dependencies));
        try
        {
            window.Show(); pump();
            check(guard.IsArmed, "The visible Lock tab restores saved monitoring on startup using isolated dependencies");
            window.SelectPage("Lock"); pump(); check(window.CurrentPage == "Lock", "A restored monitoring session has an accessible Lock page for stopping monitoring");
            window.Close(); pump(); check(window.IsVisible && guard.IsArmed && passwordPrompts == 1, "Normal X-close rejects an incorrect custom password while monitoring is active");
            window.PrepareForSessionEnd(); window.Close(); pump();
            check(!window.IsVisible && !guard.IsArmed && passwordPrompts == 1 && stopped == 0 && store.Load().Locked,
                "Windows session end releases monitoring without another password or app termination and preserves startup intent");
        }
        finally { if (window.IsVisible) { window.PrepareForSessionEnd(); window.Close(); } guard.Dispose(); }
        guard = new AppGuardService(() => new List<GuardProcess>(), process => stopped++); dependencies.Guard = guard;
        window = new FluentMainWindow(true, lockFactory: (owner, preview) => new LockPage(owner, false, dependencies));
        try
        {
            window.Show(); pump(); check(guard.IsArmed && store.Load().Apps.Count == 1, "A subsequent launch restores the same saved app after the prior session ended"); verify = true; window.Close(); pump();
            check(!window.IsVisible && !guard.IsArmed && passwordPrompts == 2 && stopped == 0 && store.Load().Locked && store.Load().Apps.Count == 1,
                "Authenticated normal close releases monitoring without terminating apps or discarding the saved startup locks");
        }
        finally { if (window.IsVisible) { window.PrepareForSessionEnd(); window.Close(); } guard.Dispose(); }
    }
}
