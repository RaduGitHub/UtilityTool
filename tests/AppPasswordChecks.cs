using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using UtilityTool.Backend;
using UtilityTool.Frontend.Wpf;

internal static class AppPasswordChecks
{
    internal static void Run(Action<bool, string> check, string output, Window owner, Action pump, Action<Window, string> capture)
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try
        {
            string root = Path.Combine(output, "app-passwords-" + Guid.NewGuid().ToString("N")); string masterPath = Path.Combine(root, "global", "password.dat");
            var master = new PasswordStore(masterPath); master.SetPassword("owned-master"); byte[] originalMaster = File.ReadAllBytes(masterPath);
            string folder = Path.Combine(root, "overrides"); var service = new AppPasswordStore(folder, master); var first = new InstalledApp("stable-app-one", "Test browser", @"D:\Apps\Browser.exe"); var second = new InstalledApp("stable-app-two", "Test editor", @"D:\Apps\Editor.exe");
            check(!service.HasOverride(first) && ReferenceEquals(master, service.Resolve(first)) && !Directory.Exists(folder), "Existing apps fall back to the unchanged UtilityTool password without creating per-app files");
            service.UseUtilityPassword(first); check(!Directory.Exists(folder) && ReferenceEquals(master, service.Resolve(first)), "Saving the default password choice is a read-only no-op when no app override exists");
            service.Set(first, "per-app-secret-983412"); var overrideStore = service.Resolve(first); string path = Directory.GetFiles(folder).Single(); byte[] saved = File.ReadAllBytes(path);
            check(service.HasOverride(first) && !ReferenceEquals(master, overrideStore) && overrideStore.Verify("per-app-secret-983412") && ReferenceEquals(master, service.Resolve(second)), "A custom password belongs only to its app while other apps retain the UtilityTool password");
            check(Path.GetFileNameWithoutExtension(path).Length == 64 && Path.GetFileNameWithoutExtension(path).All(Uri.IsHexDigit) && Path.GetExtension(path) == ".dat" && !path.Contains(first.Id) && !System.Text.Encoding.UTF8.GetString(saved).Contains("per-app-secret-983412"), "App password filenames hash their durable identity and stored DPAPI data does not contain the entered password");
            check(new AppPasswordStore(folder, master).Resolve(first).Verify("per-app-secret-983412"), "Custom app passwords survive service restart using the existing salted hash and DPAPI storage");
            check(ReferenceEquals(overrideStore, service.Resolve(new InstalledApp(first.Id.ToUpperInvariant(), first.Name, first.ExecutablePath))), "App identity casing does not create duplicate passwords or cache entries");
            using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false; try { service.Set(first, "unsaved-new-password"); } catch (IOException) { failed = true; }
                check(failed && File.ReadAllBytes(path).SequenceEqual(saved) && ReferenceEquals(overrideStore, service.Resolve(first)), "A blocked password replacement preserves the previous encrypted file and cached password");
                failed = false; try { service.UseUtilityPassword(first); } catch (IOException) { failed = true; }
                check(failed && service.HasOverride(first) && ReferenceEquals(overrideStore, service.Resolve(first)), "A blocked reset preserves its custom password instead of silently falling back");
            }
            service.Set(first, "x"); var updated = service.Resolve(first);
            check(!ReferenceEquals(updated, overrideStore) && updated.Verify("x") && Directory.GetFiles(folder, "*.tmp").Length == 0, "Successful updates invalidate the old cache and preserve the current one-character testing policy");
            var odd = new InstalledApp(@"..\..\outside:app!?", "Odd identity", @"D:\Apps\Odd.exe"); service.Set(odd, "z");
            check(Directory.GetFiles(folder).Length == 2 && Directory.GetFiles(folder).All(file => Path.GetDirectoryName(file) == Path.GetFullPath(folder) && Path.GetFileNameWithoutExtension(file).Length == 64), "Path-like app identities cannot escape the app-password folder or introduce plaintext filenames");
            service.UseUtilityPassword(odd); service.UseUtilityPassword(first);
            check(Directory.GetFiles(folder).Length == 0 && ReferenceEquals(master, service.Resolve(first)) && File.ReadAllBytes(masterPath).SequenceEqual(originalMaster), "Reset deletes only the chosen app override and preserves the global password byte for byte");

            service.Set(first, "before-damage"); path = Directory.GetFiles(folder).Single(); byte[] damaged = System.Text.Encoding.UTF8.GetBytes("Owned damaged override fixture"); File.WriteAllBytes(path, damaged);
            var recovery = new AppPasswordStore(folder, master); bool unreadable = false;
            try { recovery.Resolve(first); } catch (System.Security.Cryptography.CryptographicException) { unreadable = true; } catch (InvalidDataException) { unreadable = true; }
            check(unreadable && recovery.HasOverride(first) && File.ReadAllBytes(path).SequenceEqual(damaged) && File.ReadAllBytes(masterPath).SequenceEqual(originalMaster), "An unreadable override reports an error instead of silently accepting the master password or overwriting data");
            using (var blocked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false; try { recovery.Set(first, "blocked-repair"); } catch (IOException) { failed = true; }
                check(failed && File.ReadAllBytes(path).SequenceEqual(damaged), "A blocked authorized repair preserves the damaged override for later recovery");
            }
            recovery.Set(first, "repaired-password");
            check(recovery.Resolve(first).Verify("repaired-password") && !File.ReadAllBytes(path).SequenceEqual(damaged) && Directory.GetFiles(folder, "*.tmp").Length == 0, "An authorized new password repairs an unreadable override atomically without requiring its old password");
            recovery.Set(second, "second-before-damage"); string secondPath = Directory.GetFiles(folder).Single(file => file != path); File.WriteAllBytes(secondPath, damaged);
            var resetRecovery = new AppPasswordStore(folder, master); resetRecovery.UseUtilityPassword(second);
            check(!File.Exists(secondPath) && File.Exists(path) && ReferenceEquals(master, resetRecovery.Resolve(second)) && File.ReadAllBytes(masterPath).SequenceEqual(originalMaster), "Reset can remove another unreadable app override without reading it or changing the repaired app or global password");
            recovery.UseUtilityPassword(first);

            int count = Directory.GetFiles(folder).Length;
            Open(owner, service, first, pump, dialog =>
            {
                var useDefault = DefaultChoice(dialog); check(useDefault.IsChecked == true && Fields(dialog).All(field => !field.IsVisible) && !Visuals<System.Windows.Controls.TextBox>(dialog).Any(), "The app-password editor starts with the shared password selected, masks custom input and keeps the app name read-only");
                useDefault.IsChecked = false; var fields = Fields(dialog); fields[0].Password = "a"; fields[1].Password = "different"; Click(dialog, "Save password"); pump();
                check(dialog.IsVisible && !service.HasOverride(first) && Status(dialog, "do not match"), "Mismatched app passwords leave the editor open without creating a file");
                fields[0].Password = fields[1].Password = "q"; capture(dialog, "Lock-app-password-custom"); Click(dialog, "Cancel");
            });
            check(Directory.GetFiles(folder).Length == count && File.ReadAllBytes(masterPath).SequenceEqual(originalMaster), "Cancelling edited custom fields never writes app-password or global-password files");
            Open(owner, service, first, pump, dialog =>
            {
                DefaultChoice(dialog).IsChecked = false; var fields = Fields(dialog); fields[0].Password = fields[1].Password = "r"; Click(dialog, "Save password");
                check(!Button(dialog, "Save password").IsEnabled && !Button(dialog, "Cancel").IsEnabled && !DefaultChoice(dialog).IsEnabled, "Saving disables repeated submissions and changes while the password hash is being written");
                dialog.Close(); check(dialog.IsVisible, "The editor's close button cannot interrupt an in-progress password save");
            });
            check(service.HasOverride(first) && service.Resolve(first).Verify("r"), "A completed editor save commits the custom app password");
            Open(owner, service, first, pump, dialog =>
            {
                check(DefaultChoice(dialog).IsChecked == false && Fields(dialog).All(field => field.Password.Length == 0), "Existing overrides open in custom mode without disclosing the saved password");
                DefaultChoice(dialog).IsChecked = true; capture(dialog, "Lock-app-password-default"); Click(dialog, "Save password");
            });
            check(!service.HasOverride(first) && ReferenceEquals(master, service.Resolve(first)) && File.ReadAllBytes(masterPath).SequenceEqual(originalMaster), "Saving Use UtilityTool password removes the override without changing the master password");
            NativeUnlockChecks(check, root, master, pump);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void Open(Window owner, AppPasswordStore store, InstalledApp app, Action pump, Action<AppPasswordWindow> action)
    {
        var dialog = new AppPasswordWindow(owner, store, app); Exception failure = null; var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += delegate { if (!dialog.IsVisible) return; timer.Stop(); try { action(dialog); } catch (Exception error) { failure = error; dialog.Close(); } }; timer.Start();
        try { dialog.ShowDialog(); if (failure != null) throw failure; }
        finally { timer.Stop(); }
    }
    private static CheckBox DefaultChoice(DependencyObject root) { return Visuals<CheckBox>(root).Single(value => System.Windows.Automation.AutomationProperties.GetName(value) == "Use UtilityTool password"); }
    private static PasswordBox[] Fields(DependencyObject root) { return Visuals<PasswordBox>(root).ToArray(); }
    private static System.Windows.Controls.Button Button(DependencyObject root, string text) { return Visuals<System.Windows.Controls.Button>(root).Single(button => button.Content as string == text); }
    private static void Click(DependencyObject root, string text) { Button(root, text).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static bool Status(DependencyObject root, string text) { return Visuals<TextBlock>(root).Any(value => value.Text.Contains(text)); }
    private static void NativeUnlockChecks(Action<bool, string> check, string output, PasswordStore master, Action pump)
    {
        using (var dialog = NativeDialog(master, true))
        {
            dialog.Show(); pump(); var choices = NativeChildren<System.Windows.Forms.ComboBox>(dialog).Single(); var input = NativeChildren<System.Windows.Forms.TextBox>(dialog).Single(field => field.AccessibleName == "UtilityTool password"); var submit = (System.Windows.Forms.Button)dialog.AcceptButton;
            check(choices.DropDownStyle == System.Windows.Forms.ComboBoxStyle.DropDownList && choices.SelectedIndex == 0 && choices.Items.Count == 6 && choices.Items[0].ToString() == "Until the app exits" && dialog.AllowedFor == null, "Native app unlock starts with a bounded-choice duration list and until-exit default");
            choices.SelectedIndex = 4; input.Text = "wrong"; submit.PerformClick();
            check(!choices.Enabled && input.ReadOnly, "The native duration and password fields cannot change during verification");
            WaitFor(() => submit.Enabled && NativeChildren<System.Windows.Forms.Label>(dialog).Any(label => label.Text.Contains("Incorrect password")), pump);
            check(dialog.Visible && dialog.AllowedFor == null, "Wrong credentials never create a timed access grant");
            choices.SelectedIndex = 3; input.Text = "owned-master"; submit.PerformClick(); choices.SelectedIndex = 5;
            WaitFor(() => !dialog.Visible || dialog.IsDisposed, pump);
            check(dialog.DialogResult == System.Windows.Forms.DialogResult.OK && dialog.AllowedFor == TimeSpan.FromMinutes(15), "Successful native unlock returns the duration captured at submission rather than later control changes");
        }
        foreach (int index in new[] { 0, 5 })
        {
            using (var dialog = NativeDialog(master, true))
            {
                dialog.Show(); pump(); NativeChildren<System.Windows.Forms.ComboBox>(dialog).Single().SelectedIndex = index;
                NativeChildren<System.Windows.Forms.TextBox>(dialog).Single(field => field.AccessibleName == "UtilityTool password").Text = "owned-master"; ((System.Windows.Forms.Button)dialog.AcceptButton).PerformClick();
                WaitFor(() => !dialog.Visible || dialog.IsDisposed, pump);
                check(dialog.DialogResult == System.Windows.Forms.DialogResult.OK && dialog.AllowedFor == (index == 0 ? (TimeSpan?)null : TimeSpan.FromHours(1)), "Native unlock preserves the until-exit default and the one-hour timed option");
            }
        }
        using (var dialog = NativeDialog(master, false))
        {
            dialog.Show(); pump(); check(!NativeChildren<System.Windows.Forms.ComboBox>(dialog).Any() && dialog.AllowedFor == null, "Ordinary UtilityTool authentication does not expose an app-unlock duration"); dialog.CancelForExitedApp();
        }
        using (var dialog = NativeDialog(master, true))
        {
            const string error = "Owned credential data is unavailable. Configure this app's password in UtilityTool.";
            dialog.ShowAccessError(error); dialog.Show(); pump(); var submit = (System.Windows.Forms.Button)dialog.AcceptButton;
            typeof(System.Windows.Forms.Control).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(submit, new object[] { EventArgs.Empty });
            check(!submit.Enabled && NativeChildren<System.Windows.Forms.TextBox>(dialog).Single(field => field.AccessibleName == "UtilityTool password").ReadOnly && !NativeChildren<System.Windows.Forms.ComboBox>(dialog).Single().Enabled && dialog.AllowedFor == null, "Unreadable app credentials disable native unlock and timed grants even if a click event is invoked directly");
            ((System.Windows.Forms.Button)dialog.CancelButton).PerformClick(); dialog.Show(); pump();
            check(NativeChildren<System.Windows.Forms.Label>(dialog).Any(label => label.Text == error) && !submit.Enabled, "A credential-error prompt retains its recovery explanation after Keep locked and reopening");
            using (var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height)) { dialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(Path.Combine(output, "Lock-unlock-credential-error.png")); }
            dialog.CancelForExitedApp();
        }
    }
    private static UtilityTool.Frontend.PasswordDialog NativeDialog(PasswordStore master, bool curtain)
    { return new UtilityTool.Frontend.PasswordDialog(master, false, "Owned app unlock", false, false, curtain, curtain, () => IntPtr.Zero, window => { }); }
    private static System.Collections.Generic.IEnumerable<T> NativeChildren<T>(System.Windows.Forms.Control root) where T : System.Windows.Forms.Control
    { if (root is T) yield return (T)root; foreach (System.Windows.Forms.Control child in root.Controls) foreach (var value in NativeChildren<T>(child)) yield return value; }
    private static void WaitFor(Func<bool> condition, Action pump) { var clock = System.Diagnostics.Stopwatch.StartNew(); while (!condition() && clock.ElapsedMilliseconds < 15000) { pump(); Thread.Sleep(5); } if (!condition()) throw new TimeoutException("App-password check timed out."); }
    private static System.Collections.Generic.IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject { if (root is T) yield return (T)root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var value in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return value; }
}
