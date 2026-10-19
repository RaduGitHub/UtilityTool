using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using UtilityTool.Backend;
using UtilityTool.Frontend;

internal static class GuardReliabilityChecks
{
    internal static void Run(Action<bool, string> check, string output, Action pump)
    {
        FakeGuardChecks(check);
        string passwordPath = Path.Combine(output, "retry-policy-password-" + Guid.NewGuid().ToString("N") + ".dat");
        var passwords = new PasswordStore(passwordPath); passwords.SetPassword("test-a");
        using (var target = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--guard-target") { UseShellExecute = false }))
        {
            try
            {
                Wait(() => { target.Refresh(); return target.MainWindowHandle != IntPtr.Zero; }, pump);
                var process = new GuardProcess(target.Id, "Owned retry fixture", target.MainModule.FileName, "", target.StartTime.ToUniversalTime().Ticks);
                var read = (GuardProcess)typeof(AppGuardService).GetMethod("ReadProcess", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { target.Id });
                check(read != null && read.StartTimeTicks == process.StartTimeTicks, "Guard reads executable identity and creation time from the same process handle");
                typeof(AppGuardService).GetMethod("StopProcess", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null,
                    new object[] { new GuardProcess(target.Id, "Owned retry fixture", process.Path, "", process.StartTimeTicks + 1) });
                check(!target.HasExited, "Legacy close fallback rejects a mismatched process instance without closing the owned app");

                using (var overlay = new AppLockOverlay(process, () => IntPtr.Zero))
                {
                    var result = overlay.PromptAsync(passwords, () => true); pump();
                    var dialog = Application.OpenForms.Cast<Form>().OfType<PasswordDialog>().Single();
                    var cover = Application.OpenForms.Cast<Form>().OfType<LockCoverWindow>().Single();
                    var input = (TextBox)typeof(PasswordDialog).GetField("password", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
                    var message = (Label)typeof(PasswordDialog).GetField("message", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
                    var submit = (Button)dialog.AcceptButton; var keep = (Button)dialog.CancelButton;
                    check(keep.Text == "Keep locked" && keep.DialogResult == DialogResult.None, "App prompt presents Keep locked without a cancel access result");
                    input.Text = "wrong"; submit.PerformClick();
                    Wait(() => message.Text.Contains("Incorrect") && !input.ReadOnly, pump);
                    check(dialog.Visible && !dialog.IsDisposed && cover.Visible && !result.IsCompleted && !target.HasExited && message.Text.Contains("Try again"),
                        "A wrong password keeps the app covered and allows retry without closing the app");

                    keep.PerformClick(); pump();
                    check(!dialog.Visible && !dialog.IsDisposed && cover.Visible && !result.IsCompleted && !target.HasExited,
                        "Keep locked hides only the password prompt while the app curtain and pending access remain");
                    for (int i = 0; i < 8; i++) { Tick(overlay); pump(); }
                    check(!dialog.Visible && cover.Visible && !result.IsCompleted, "Timer updates do not immediately reopen a dismissed password prompt");
                    Click(cover); pump();
                    check(dialog.Visible && input.Text == "" && !result.IsCompleted, "Clicking the curtain reopens a clean password prompt");

                    typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(input, new object[] { new KeyEventArgs(Keys.Escape) }); pump();
                    check(!dialog.Visible && cover.Visible && !result.IsCompleted && !target.HasExited, "Escape keeps the app locked without closing its process");
                    Click(cover); pump(); dialog.Close(); pump(); Tick(overlay); pump();
                    check(!dialog.Visible && !dialog.IsDisposed && cover.Visible && !result.IsCompleted, "Closing the app password prompt, including Alt-F4, keeps its curtain and suppresses repeat popups");
                    Click(cover); pump(); input.Text = "test-a"; submit.PerformClick();
                    Wait(() => result.IsCompleted, pump);
                    check(result.Result && dialog.DialogResult == DialogResult.OK && !target.HasExited, "The correct password succeeds after wrong input and repeated Keep locked dismissals");
                }
                pump();
                check(!Application.OpenForms.Cast<Form>().Any(form => form is PasswordDialog || form is LockCoverWindow) && !target.HasExited,
                    "Successful access releases the curtain and prompt without closing the protected app");

                using (var overlay = new AppLockOverlay(process, () => IntPtr.Zero))
                {
                    var result = overlay.PromptAsync(passwords, () => true); pump(); overlay.Cancel(); pump();
                    check(result.IsCompleted && !result.Result && !Application.OpenForms.Cast<Form>().Any(form => form is PasswordDialog || form is LockCoverWindow) && !target.HasExited,
                        "Programmatic disarm still resolves pending access and removes all app-lock surfaces");
                }
                using (var dialog = new PasswordDialog(passwords, false, "Owned exited-app prompt", false, false, true, true))
                {
                    dialog.Show(); pump(); dialog.CancelForExitedApp(); pump();
                    check(dialog.IsDisposed || !dialog.Visible, "Programmatic process-exit cancellation can close a Keep locked prompt");
                }
            }
            finally
            {
                // The helper belongs to this check; never close a user's app.
                if (!target.HasExited) { target.CloseMainWindow(); if (!target.WaitForExit(3000)) { target.Kill(); target.WaitForExit(); } }
                if (File.Exists(passwordPath)) File.Delete(passwordPath);
            }
        }
    }

    private static void FakeGuardChecks(Action<bool, string> check)
    {
        var selected = new[] { new InstalledApp("owned-fake", "Owned fake app", "owned-fake.exe") };
        var processes = new List<GuardProcess> { new GuardProcess(1024, "Owned fake app", "owned-fake.exe", "", 10) };
        int requests = 0, stopped = 0, notified = 0;
        using (var guard = new AppGuardService(() => processes, process => stopped++))
        {
            guard.StatusChanged += message => { throw new InvalidOperationException("Owned disposed subscriber"); };
            guard.StatusChanged += message => notified++;
            guard.RequestAccessAsync = process => { requests++; return Task.FromResult(true); };
            guard.Arm(selected, false); guard.ScanOnce(); guard.ScanOnce();
            check(requests == 1 && stopped == 0 && notified == 1, "A failed status subscriber cannot crash app-lock access or block other status subscribers");
        }
        requests = stopped = 0;
        using (var guard = new AppGuardService(() => processes, process => stopped++))
        {
            guard.RequestAccessAsync = process => { requests++; return requests == 1 ? Task.FromException<bool>(new InvalidOperationException("Owned prompt failure")) : Task.FromResult(true); };
            guard.Arm(selected, false); guard.ScanOnce(); guard.ScanOnce(); guard.ScanOnce();
            check(requests == 2 && stopped == 0, "A failed app-lock prompt releases its pending reservation and can recover on the next scan");
        }
        notified = 0;
        using (var guard = new AppGuardService(() => processes, process => stopped++))
        {
            guard.StatusChanged += message => notified++;
            guard.RequestAccessAsync = process =>
            {
                guard.Disarm(); guard.Arm(selected, false);
                return Task.FromException<bool>(new InvalidOperationException("Owned stale request"));
            };
            guard.Arm(selected, false); guard.ScanOnce();
            check(guard.IsArmed && notified == 0, "A stale failed prompt cannot overwrite the status of a rearmed guard session");
        }
    }

    private static void Click(Control control)
    { typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(control, new object[] { EventArgs.Empty }); }
    private static void Tick(AppLockOverlay overlay)
    { typeof(AppLockOverlay).GetMethod("Tick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, new object[] { null, EventArgs.Empty }); }
    private static void Wait(Func<bool> ready, Action pump)
    { var watch = Stopwatch.StartNew(); while (!ready() && watch.ElapsedMilliseconds < 10000) { pump(); System.Threading.Thread.Sleep(5); } if (!ready()) throw new TimeoutException("Owned guard reliability check timed out."); }
}
