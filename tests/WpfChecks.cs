using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UtilityTool.Backend;
using UtilityTool.Frontend.Wpf;

internal static class WpfChecks
{
    private static string output;
    private static readonly List<string> results = new List<string>();
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--guard-target")
        { System.Windows.Forms.Application.EnableVisualStyles(); System.Windows.Forms.Application.Run(new System.Windows.Forms.Form { Text = "Owned guard test window", Width = 500, Height = 350 }); return 0; }
        output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "UtilityTool-WpfChecks");
        Directory.CreateDirectory(output);
        try
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/UtilityTool;component/Frontend/Wpf/Styles.xaml", UriKind.Relative) });
            var window = new FluentMainWindow(true);
            window.Show(); Pump();
            File.WriteAllLines(Path.Combine(output, "theme.txt"), new[] { "HighContrast=" + SystemParameters.HighContrast, "Theme=" + Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme(), "Input=" + app.TryFindResource("TextControlBackground"), "ButtonDisabled=" + app.TryFindResource("ButtonBackgroundDisabled"), "WindowBackground=" + window.Background });
            Check(window.MinWidth == window.Width && window.MinHeight == window.Height, "Startup size is the minimum size");
            foreach (string name in new[] { "Home", "Shutdown", "Lock", "Sound", "Settings" })
            { window.SelectPage(name); Pump(); Check(window.CurrentPage == name, "Navigate to " + name); Capture(window, name); }
            DurationChecks(window);
            TileChecks(window);
            WindowControls(window);
            PreviewActions(window);
            PasswordChecks(window);
            OverlayChecks();
            BackendChecks();
            window.Close(); Pump(); Check(!window.IsVisible, "X/Close releases the preview window");
            app.Shutdown();
            File.WriteAllLines(Path.Combine(output, "results.txt"), results);
            Console.WriteLine(string.Join(Environment.NewLine, results));
            return 0;
        }
        catch (Exception error)
        { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); Console.WriteLine(error); return 1; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); results.Add("PASS: " + message); }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Type(DurationField field, string text)
    { field.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, new TextComposition(InputManager.Current, field, text)) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent }); }
    private static void DurationChecks(FluentMainWindow window)
    {
        window.SelectPage("Shutdown"); Pump(); var duration = window.Shutdown.Duration;
        foreach (var field in new[] { duration.Hours, duration.Minutes, duration.Seconds })
        {
            duration.Hours.Focus(); duration.Minutes.Focus(); field.Focus(); Pump();
            string[] expected = { "05", "54", "43", "32", "21" }; int index = 0;
            foreach (char digit in "54321") { Type(field, digit.ToString()); Check(field.Text == expected[index++], "Rolling input " + expected[index - 1]); }
        }
        duration.Seconds.Focus(); Type(duration.Seconds, "5"); duration.Minutes.Focus(); Type(duration.Minutes, "2"); duration.Seconds.Focus(); Type(duration.Seconds, "4"); Check(duration.Seconds.Text == "04", "Changing fields resets digit entry");
        duration.Minutes.Focus(); Type(duration.Minutes, "5"); Type(duration.Minutes, "9"); Type(duration.Minutes, "9"); Check(duration.Minutes.Text == "59", "Minutes reject values above 59");
        duration.Seconds.Focus(); Type(duration.Seconds, "5"); Type(duration.Seconds, "9"); Type(duration.Seconds, "9"); Check(duration.Seconds.Text == "59", "Seconds reject values above 59");
        duration.Hours.Focus(); Type(duration.Hours, "9"); Type(duration.Hours, "9"); Check(duration.Hours.Text == "99", "Hours accept 99");
        duration.Seconds.Step(1); Check(duration.Seconds.Text == "00", "Seconds up wraps at 59"); duration.Seconds.Step(-1); Check(duration.Seconds.Text == "59", "Seconds down wraps at zero");
        duration.Minutes.Text = "60"; Check(duration.Minutes.Text == "59", "Invalid programmatic/paste edits restore valid text");
        Check(duration.TotalSeconds == 359999, "Duration converts 99:59:59 exactly");
    }
    private static void TileChecks(FluentMainWindow window)
    {
        window.SelectPage("Lock"); Pump(); var picker = window.Lock.Tiles;
        var tiles = new List<TileItem>(); for (int index = 0; index < 1000; index++) tiles.Add(new TileItem(index.ToString(), "Test app " + index, null));
        picker.SetItems(tiles); Pump();
        Check(picker.Items.Count > 20 && Children<ListBoxItem>(picker).Count() < 12, "1,000 apps virtualize rows rather than materializing the whole list");
        int toggles = 0; tiles[0].SelectionChanged += item => toggles++;
        for (int i = 0; i < 20; i++) tiles[0].Selected = !tiles[0].Selected;
        Check(toggles == 20, "Rapid tile toggling processes every change");
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 60; i++) { window.Width = 1040 + i * 20; window.UpdateLayout(); }
        watch.Stop(); Check(picker.Columns >= 12, "Grid reaches 12 or more columns without horizontal scrolling");
        results.Add("INFO: 60 resize/layout steps for 1,000 apps: " + watch.ElapsedMilliseconds + " ms");
        Check(ScrollViewer.GetHorizontalScrollBarVisibility(picker) == ScrollBarVisibility.Disabled, "Horizontal scrollbar stays disabled");
        var row = (TileRow)picker.Items[0]; double size = row.Width; window.Width += 10; window.UpdateLayout();
        Check(ReferenceEquals(row, picker.Items[0]) && row.Width > size, "Resize within a column count stretches boxes without rebuilding rows");
        var image = Children<Image>(picker).First(); var text = Children<TextBlock>(picker).First(label => label.Text.StartsWith("Test app"));
        Check(image.Width == 60 && image.Height == 60 && text.FontSize == 12, "Icons and names retain their sizes");
        Check(Children<Border>(picker).Any(border => border.CornerRadius.TopLeft == 12), "Tile corners remain rounded without hovering");
        Capture(window, "Lock-large-grid");
        window.SelectPage("Sound"); Pump(); var soundRow = (TileRow)window.Sound.Tiles.Items[0];
        Check(soundRow.Width == 320, "Short Sound lists keep fixed 160px boxes");
        window.Width = 1040; Pump();
    }
    private static void WindowControls(FluentMainWindow window)
    {
        var title = Children<Wpf.Ui.Controls.TitleBar>(window).Single();
        Check(title.ShowClose && title.ShowMinimize && title.ShowMaximize && !title.ForceShutdown, "Fluent title bar exposes window controls with graceful close");
        SystemCommands.MaximizeWindow(window); Pump(); Check(window.WindowState == WindowState.Maximized, "Maximize works");
        SystemCommands.RestoreWindow(window); Pump(); Check(window.WindowState == WindowState.Normal, "Restore works");
        SystemCommands.MinimizeWindow(window); Pump(); Check(window.WindowState == WindowState.Minimized, "Minimize works"); SystemCommands.RestoreWindow(window); Pump();
    }
    private static void PreviewActions(FluentMainWindow window)
    {
        // Handlers must exit without touching shutdown.exe, audio, the password or startup settings.
        foreach (string name in new[] { "Shutdown", "Lock", "Sound", "Settings" })
        {
            window.SelectPage(name); Pump();
            foreach (var tile in Children<ToggleButton>(window).Where(button => button.DataContext is TileItem).Take(1)) tile.IsChecked = true;
            foreach (var button in Children<Wpf.Ui.Controls.Button>(window).Where(button => button.IsEnabled && button.Content is string && ((string)button.Content == "Schedule shutdown" || (string)button.Content == "Cancel shutdown" || (string)button.Content == "Apply locks" || (string)button.Content == "Set password" || (string)button.Content == "Start multiple outputs")).ToList()) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            foreach (var toggle in Children<Wpf.Ui.Controls.ToggleSwitch>(window)) toggle.IsChecked = !toggle.IsChecked;
            Pump();
        }
        Check(window.Lock.CanClose(), "Preview guard never arms and can close without a password");
    }
    private static void PasswordChecks(FluentMainWindow owner)
    {
        string path = Path.Combine(output, "test-password.dat"); var store = new PasswordStore(path); store.SetPassword("a");
        Check(store.Verify("a") && !store.Verify("wrong"), "Existing encrypted password store accepts simple test passwords and rejects wrong ones");
        var dialog = new PasswordWindow(owner, store, false, "UI test password"); dialog.Show(); Pump();
        File.WriteAllText(Path.Combine(output, "focus.txt"), "Active=" + dialog.IsActive + "; focused=" + Keyboard.FocusedElement + "; logical=" + FocusManager.GetFocusedElement(dialog));
        Check(Keyboard.FocusedElement is PasswordBox, "Password field receives immediate keyboard focus"); dialog.Close();
        File.Delete(path);
    }
    private static void BackendChecks()
    {
        Check(ShutdownService.ScheduleArguments(90) == "/s /t 90" && ShutdownService.CancelArguments == "/a", "Shutdown argument generation is unchanged (no command executed)");
        string path = Path.Combine(output, "test-locks.xml"); var store = new LockStateStore(path); var state = new LockState { Locked = true }; state.Apps.Add(new InstalledApp("fake", "Fake app", "fake.exe")); store.Save(state); var restored = store.Load();
        Check(restored.Locked && restored.Apps.Count == 1 && restored.Apps[0].Id == "fake", "Existing lock selections and armed state persist"); File.Delete(path);
        var process = new GuardProcess(123, "Fake app", "fake.exe", "", 456); int closed = 0, requested = 0;
        using (var guard = new AppGuardService(() => new List<GuardProcess> { process }, value => closed++))
        {
            guard.RequestAccess = value => { requested++; return true; }; guard.Arm(state.Apps, false); guard.ScanOnce(); guard.ScanOnce(); Check(requested == 1 && closed == 0, "Approved process remains unlocked until exit");
            var pending = new TaskCompletionSource<bool>(); guard.Disarm(); guard.RequestAccessAsync = value => pending.Task; guard.Arm(state.Apps, false); guard.ScanOnce(); guard.Disarm(); pending.SetResult(false); Pump(); Check(closed == 0, "Disarming invalidates pending access without stopping the app");
        }
        using (var mirror = new AudioMirrorService()) { mirror.StopAsync().GetAwaiter().GetResult(); Check(!mirror.IsRunning, "Idle audio service stops/disposes without changing Windows output"); }
    }
    private static void OverlayChecks()
    {
        string path = Path.Combine(output, "overlay-password.dat"); var store = new PasswordStore(path); store.SetPassword("a");
        using (var target = Process.Start(new ProcessStartInfo { FileName = Process.GetCurrentProcess().MainModule.FileName, Arguments = "--guard-target", UseShellExecute = false }))
        {
            try
            {
                var wait = Stopwatch.StartNew(); while (target.MainWindowHandle == IntPtr.Zero && wait.ElapsedMilliseconds < 5000) { target.Refresh(); Pump(); System.Threading.Thread.Sleep(10); }
                Check(target.MainWindowHandle != IntPtr.Zero, "Owned guard fixture opens");
                using (var overlay = new UtilityTool.Frontend.AppLockOverlay(new GuardProcess(target.Id, "Fixture", target.MainModule.FileName, "", target.StartTime.ToUniversalTime().Ticks)))
                {
                    var result = overlay.PromptAsync(store, () => true);
                    typeof(UtilityTool.Frontend.AppLockOverlay).GetMethod("ShowPrompt", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, new object[] { target.MainWindowHandle, true });
                    wait.Restart(); while (wait.ElapsedMilliseconds < 200) { Pump(); System.Threading.Thread.Sleep(5); }
                    var dialog = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.PasswordDialog>().FirstOrDefault();
                    Check(dialog != null, "Existing WinForms password overlay runs in the WPF message loop");
                    overlay.Cancel(); Pump(); Check(result.IsCompleted && !result.Result, "Cancelling the overlay resolves pending access");
                    Check(!System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Any(form => form is UtilityTool.Frontend.LockCoverWindow || form is UtilityTool.Frontend.PasswordDialog), "Disarming/disposal removes every blur cover and prompt");
                }
            }
            finally { target.CloseMainWindow(); if (!target.WaitForExit(1500)) { target.Kill(); target.WaitForExit(); } }
        }
        File.Delete(path);
    }
    private static IEnumerable<T> Children<T>(DependencyObject value) where T : DependencyObject
    { if (value is T) yield return (T)value; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) foreach (var child in Children<T>(VisualTreeHelper.GetChild(value, i))) yield return child; }
    private static void Capture(Window window, string name)
    { window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file); }
}
