using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using UtilityTool.Backend;
using UtilityTool.Frontend;

internal static class GuardFocusChecks
{
    internal static void Run(Action<bool, string> check, string output, Action pump)
    {
        string path = Path.Combine(output, "focus-password-" + Guid.NewGuid().ToString("N") + ".dat");
        var store = new PasswordStore(path); store.SetPassword("a");
        using (var unrelated = new Form { Text = "Owned unrelated focus fixture", Width = 420, Height = 260 })
        using (var target = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--guard-target") { UseShellExecute = false }))
        {
            try
            {
                Wait(() => { target.Refresh(); return target.MainWindowHandle != IntPtr.Zero; }, pump);
                IntPtr targetWindow = target.MainWindowHandle;
                var process = new GuardProcess(target.Id, "Owned focus fixture", target.MainModule.FileName, "", target.StartTime.ToUniversalTime().Ticks);
                IntPtr foreground = unrelated.Handle;
                int activations = 0; bool allowActivation = false, isLocked = true;
                // The sandbox cannot claim OS foreground. Keep native edit/button
                // focus, while supplying controlled external foreground snapshots.
                Action<IntPtr> activate = window => { activations++; if (allowActivation) foreground = window; };
                using (var overlay = new AppLockOverlay(process, () => foreground, activate))
                {
                    var result = overlay.PromptAsync(store, () => isLocked);
                    StopWatchers(overlay);
                    var dialog = Application.OpenForms.Cast<Form>().OfType<PasswordDialog>().Single();
                    var cover = Application.OpenForms.Cast<Form>().OfType<LockCoverWindow>().Single();
                    var input = (TextBox)Field(dialog, "password");
                    var submit = (Button)dialog.AcceptButton;
                    var retry = (Timer)Field(overlay, "focusTimer");
                    check(dialog.Visible && activations == 0 && !retry.Enabled,
                        "A locked app opening behind another app shows its prompt without requesting foreground");

                    input.Focus();
                    check(dialog.ContainsFocus && input.Focused && foreground != dialog.Handle,
                        "Focus fixture retains native password child focus while another HWND is foreground");
                    foreground = targetWindow;
                    Invoke(overlay, "ShowPrompt", targetWindow, true);
                    check(activations == 1 && input.Focused && retry.Enabled,
                        "Target activation requests prompt foreground even when its edit retained per-thread focus");
                    allowActivation = true;
                    Invoke(overlay, "RetryPromptFocus", null, EventArgs.Empty);
                    check(activations == 2 && foreground == dialog.Handle && input.Focused && !retry.Enabled,
                        "A bounded retry completes the delayed foreground handoff and stops immediately on success");

                    input.Text = "typed"; input.SelectionStart = 2; input.SelectionLength = 0;
                    submit.Select(); int before = activations;
                    dialog.FocusPassword();
                    check(submit.Focused && activations == before && input.Text == "typed",
                        "A foreground prompt preserves button focus and password text during fast Unlock clicks");
                    foreground = targetWindow;
                    Invoke(overlay, "OnWindowEvent", targetWindow, WindowEventWatcher.Foreground);
                    check(input.Focused && foreground == dialog.Handle && input.Text == "typed",
                        "A late self-activation by the locked app returns typing to its existing password prompt");

                    allowActivation = false; foreground = targetWindow; before = activations;
                    Invoke(overlay, "ShowPrompt", targetWindow, true);
                    for (int i = 0; i < 24; i++) Invoke(overlay, "RetryPromptFocus", null, EventArgs.Empty);
                    check(activations - before == 12 && !retry.Enabled,
                        "Foreground acquisition stops after twelve attempts instead of polling or fighting focus indefinitely");

                    Invoke(overlay, "ShowPrompt", targetWindow, true); before = activations;
                    foreground = unrelated.Handle;
                    Invoke(overlay, "RetryPromptFocus", null, EventArgs.Empty);
                    check(activations == before && !retry.Enabled,
                        "Switching to an unrelated app cancels pending focus retries without stealing focus");
                    foreground = targetWindow; Invoke(overlay, "ShowPrompt", targetWindow, true); before = activations;
                    foreground = unrelated.Handle;
                    Invoke(overlay, "OnWindowEvent", unrelated.Handle, WindowEventWatcher.Foreground);
                    check(activations == before && !retry.Enabled,
                        "An unrelated foreground event cancels handoff immediately without waiting for its timer");

                    foreground = targetWindow; Invoke(overlay, "ShowPrompt", targetWindow, true); before = activations;
                    isLocked = false;
                    Invoke(overlay, "RetryPromptFocus", null, EventArgs.Empty);
                    check(activations == before && !retry.Enabled && !result.IsCompleted,
                        "An externally disarmed or replaced guard session cannot retry focus before its backup timer notices");
                    isLocked = true;

                    foreground = targetWindow; Invoke(overlay, "ShowPrompt", targetWindow, true);
                    ((Button)dialog.CancelButton).PerformClick(); before = activations;
                    for (int i = 0; i < 4; i++)
                    {
                        Invoke(overlay, "RetryPromptFocus", null, EventArgs.Empty);
                        Invoke(overlay, "OnWindowEvent", targetWindow, WindowEventWatcher.Foreground);
                    }
                    check(!dialog.Visible && cover.Visible && !retry.Enabled && activations == before && !result.IsCompleted,
                        "Keep locked cancels handoff and remains dismissed through later target activations");
                    foreground = cover.Handle; allowActivation = true;
                    typeof(Control).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(cover, new object[] { EventArgs.Empty });
                    check(dialog.Visible && foreground == dialog.Handle && input.Focused,
                        "An explicit curtain click restores the dismissed prompt and immediately focuses its password");
                    input.Text = "a";
                    typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(input, new object[] { new KeyEventArgs(Keys.Enter) });
                    Wait(() => result.IsCompleted, pump);
                    check(result.Result && !target.HasExited && !retry.Enabled,
                        "Enter still unlocks after focus recovery without closing the protected fixture");
                }

                foreground = targetWindow; allowActivation = false;
                var disposedOverlay = new AppLockOverlay(process, () => foreground, activate);
                disposedOverlay.PromptAsync(store, () => true); StopWatchers(disposedOverlay);
                var disposedRetry = (Timer)Field(disposedOverlay, "focusTimer");
                check(disposedRetry.Enabled, "Disposal fixture has a pending bounded focus handoff");
                disposedOverlay.Dispose(); int count = activations;
                Invoke(disposedOverlay, "RetryPromptFocus", null, EventArgs.Empty);
                check(!disposedRetry.Enabled && activations == count && !Application.OpenForms.Cast<Form>().Any(form => form is PasswordDialog || form is LockCoverWindow),
                    "Disposing the overlay cancels handoff and releases its prompt and curtain");
            }
            finally
            {
                // Only the child started by this check is closed.
                if (!target.HasExited) { target.CloseMainWindow(); if (!target.WaitForExit(3000)) { target.Kill(); target.WaitForExit(); } }
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    private static object Field(object value, string name)
    { return value.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(value); }
    private static void Invoke(object value, string name, params object[] arguments)
    { value.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(value, arguments); }
    private static void StopWatchers(AppLockOverlay overlay)
    {
        ((Timer)Field(overlay, "timer")).Stop();
        var watcher = (WindowEventWatcher)Field(overlay, "windowEvents");
        if (watcher != null) watcher.Dispose();
        typeof(AppLockOverlay).GetField("windowEvents", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(overlay, null);
    }
    private static void Wait(Func<bool> ready, Action pump)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.ElapsedMilliseconds < 10000) { pump(); System.Threading.Thread.Sleep(5); }
        if (!ready()) throw new TimeoutException("Owned guard focus check timed out.");
    }
}
