using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
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
        if (args.Length > 0 && (args[0] == "--guard-target" || args[0] == "--guard-target-borderless"))
        { System.Windows.Forms.Application.EnableVisualStyles(); System.Windows.Forms.Application.Run(new System.Windows.Forms.Form { Text = "Owned guard test window", Width = 700, Height = 600, BackColor = System.Drawing.Color.Magenta, FormBorderStyle = args[0] == "--guard-target-borderless" ? System.Windows.Forms.FormBorderStyle.None : System.Windows.Forms.FormBorderStyle.Sizable }); return 0; }
        if (args.Length > 0 && args[0] == "--common-app-report")
        {
            foreach (var installed in InstalledAppCatalog.LoadAsync().GetAwaiter().GetResult())
                if (new[] { "Firefox", "Google Chrome", "Microsoft Edge", "Discord", "Steam", "Visual Studio Code", "VLC media player", "7-Zip File Manager", "Camera", "Microsoft Teams", "WhatsApp", "League of Legends" }.Contains(installed.Name))
                    Console.WriteLine(installed.Name + " | " + installed.Id + " | " + string.Join("; ", AppGuardTargets.Executables(installed)));
            return 0;
        }
        if (args.Length > 0 && args[0] == "--soundboard-devices")
        {
            var devices = SoundboardDevices.Read();
            foreach (var device in devices.Microphones) Console.WriteLine("Microphone: " + device.Name);
            foreach (var device in devices.VirtualOutputs) Console.WriteLine("Virtual output: " + device.Name);
            if (devices.VirtualOutputs.Count == 0) Console.WriteLine("No supported virtual output installed.");
            return 0;
        }
        if (args.Length > 1 && args[0] == "--sound-interop-check")
        {
            SoundDeviceChecks.RunInterop((passed, message) => { if (!passed) throw new Exception(message); Console.WriteLine("PASS: " + message); }, args[1] == "naudio-first");
            Task.Run(() => SoundDeviceChecks.RunInterop((passed, message) => { if (!passed) throw new Exception(message); Console.WriteLine("PASS (audio worker): " + message); }, args[1] == "naudio-first")).GetAwaiter().GetResult();
            Exception threadFailure = null;
            var mirrorThread = new System.Threading.Thread(() =>
            {
                try { SoundDeviceChecks.RunInterop((passed, message) => { if (!passed) throw new Exception(message); Console.WriteLine("PASS (mirror thread): " + message); }, args[1] == "naudio-first"); }
                catch (Exception error) { threadFailure = error; }
            }) { IsBackground = true };
            mirrorThread.SetApartmentState(System.Threading.ApartmentState.MTA); mirrorThread.Start();
            if (!mirrorThread.Join(15000)) throw new TimeoutException("Mirror-thread device enumeration timed out.");
            if (threadFailure != null) throw threadFailure;
            return 0;
        }
        if (args.Length > 0 && args[0] == "--soundboard-audio-report")
        {
            using (var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            {
                foreach (NAudio.CoreAudioApi.Role role in Enum.GetValues(typeof(NAudio.CoreAudioApi.Role)))
                    using (var device = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, role)) Console.WriteLine(role + " default: " + device.FriendlyName);
                foreach (var device in devices.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                    using (device) Console.WriteLine("Output: " + device.FriendlyName + "; muted=" + device.AudioEndpointVolume.Mute + "; volume=" + device.AudioEndpointVolume.MasterVolumeLevelScalar.ToString("0.00"));
            }
            if (args.Length > 1)
            {
                foreach (var sound in new SoundboardStore(args[1]).Load())
                    using (var clip = new SoundboardClip(sound.AudioPath))
                    {
                        var buffer = new float[9600]; float peak = 0; int read, total = 0;
                        while (total < 48000 * 2 * 5 && (read = clip.Read(buffer, 0, buffer.Length)) > 0) { total += read; for (int i = 0; i < read; i++) peak = Math.Max(peak, Math.Abs(buffer[i])); }
                        Console.WriteLine("Sound: " + sound.Name + "; decoded samples=" + total + "; peak=" + peak.ToString("0.000"));
                    }
            }
            return 0;
        }
        if (args.Length > 1 && args[0] == "--soundboard-waveform-check")
        { SoundboardChecks.VerifyWaveform(args[1]); return 0; }
        if (args.Length > 1 && args[0] == "--soundboard-local-check")
        {
            string folder = Path.GetFullPath(args[1]); Directory.CreateDirectory(folder);
            string silent = Path.Combine(folder, "silent-playback-check.mp3");
            NAudio.MediaFoundation.MediaFoundationApi.Startup();
            var silence = new NAudio.Wave.SampleProviders.OffsetSampleProvider(NAudio.Wave.WaveExtensionMethods.ToSampleProvider(new NAudio.Wave.SilenceProvider(new NAudio.Wave.WaveFormat(44100, 16, 2)))) { Take = TimeSpan.FromMilliseconds(200) };
            NAudio.Wave.MediaFoundationEncoder.EncodeToMp3(new NAudio.Wave.SampleProviders.SampleToWaveProvider16(silence), silent, 128000);
            using (var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            {
                string before;
                using (var endpoint = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia)) before = endpoint.ID;
                WasapiSoundboardSession native = null;
                using (var service = new SoundboardAudioService((mic, outputDevice, listening) => native = new WasapiSoundboardSession(mic, outputDevice, listening)))
                {
                    for (int replay = 0; replay < 3; replay++)
                    {
                        service.PlayAsync(silent).GetAwaiter().GetResult();
                        var end = Stopwatch.StartNew();
                        while (service.IsPlaying && end.ElapsedMilliseconds < 2500) System.Threading.Thread.Sleep(5);
                        if (service.IsPlaying || native.LocalOutputRunning) throw new Exception("The local output did not stop at MP3 EOF.");
                    }
                    // Repeated rapid starts and a replay near the old clip's EOF
                    // must not overlap or disappear into the last-buffer drain.
                    service.PlayAsync(silent).GetAwaiter().GetResult(); service.PlayAsync(silent).GetAwaiter().GetResult();
                    System.Threading.Thread.Sleep(190); service.PlayAsync(silent).GetAwaiter().GetResult();
                    var finish = Stopwatch.StartNew(); while (service.IsPlaying && finish.ElapsedMilliseconds < 2500) System.Threading.Thread.Sleep(5);
                    if (service.IsPlaying || native.LocalOutputRunning) throw new Exception("Rapid replay failed to stop at EOF.");
                    service.PlayAsync(silent).GetAwaiter().GetResult(); service.StopClipsAsync().GetAwaiter().GetResult();
                    if (service.IsPlaying || native.LocalOutputRunning) throw new Exception("Stop sounds left local playback active.");
                }
                using (var endpoint = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
                    if (endpoint.ID != before) throw new Exception("Default output changed during local playback.");
            }
            File.Delete(silent); Console.WriteLine("PASS: Real shared output stops at MP3 EOF, handles rapid/near-EOF replay, flushes on Stop and disposes without microphone capture or default-output changes (silent MP3)."); return 0;
        }
        if (args.Length > 0 && args[0] == "--soundboard-tone-check")
        {
            using (var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator())
            using (var device = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
            {
                string originalId = device.ID; float peak = 0;
                using (var audio = new SoundboardAudioService())
                {
                    audio.TestLocalOutputAsync().GetAwaiter().GetResult();
                    var timer = Stopwatch.StartNew();
                    while (timer.ElapsedMilliseconds < 650)
                    {
                        // Observe our own mixer as the real WASAPI output reads it.
                        // No system loopback recording or other app's audio is read.
                        peak = Math.Max(peak, audio.LocalPeak);
                        System.Threading.Thread.Sleep(15);
                    }
                    audio.StopClipsAsync().GetAwaiter().GetResult();
                    if (peak <= 0.001f) throw new Exception("The output session reported no audible signal.");
                    Console.WriteLine("PASS: Real shared WASAPI output reads the nonzero local test tone: " + audio.LocalOutputName + "; peak=" + peak.ToString("0.000") + ". No microphone opened.");
                }
                using (var after = devices.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia))
                    if (after.ID != originalId) throw new Exception("The Windows default output was changed.");
            }
            return 0;
        }
        if (args.Length > 1 && args[0] == "--guard-target-hosted")
        {
            using (var child = new System.Windows.Forms.Form { TopLevel = false, Width = 650, Height = 520, FormBorderStyle = System.Windows.Forms.FormBorderStyle.None })
            {
                SetNativeParent(child.Handle, new IntPtr(long.Parse(args[1])));
                System.Windows.Forms.Application.Run(child);
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "--guard-target-delayed")
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            using (var window = new System.Windows.Forms.Form { Text = "Owned early-lock fixture", Width = 700, Height = 600 })
            using (var show = new System.Windows.Forms.Timer { Interval = 500 })
            {
                var handle = window.Handle; // create before Show, like many real apps
                show.Tick += delegate { show.Stop(); window.Show(); };
                window.FormClosed += delegate { System.Windows.Forms.Application.ExitThread(); };
                show.Start(); System.Windows.Forms.Application.Run();
            }
            return 0;
        }
        if (args.Length > 0 && (args[0] == "--guard-target-delayed-background" || args[0] == "--guard-target-multi-background"))
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            using (var first = new NoActivateFixture { Text = "Owned background app window", Width = 700, Height = 600 })
            using (var second = new NoActivateFixture { Text = "Owned second app window", Width = 620, Height = 500, Left = 350, Top = 200 })
            using (var show = new System.Windows.Forms.Timer { Interval = 500 })
            {
                show.Tick += delegate { show.Stop(); first.Show(); if (args[0] == "--guard-target-multi-background") second.Show(); };
                first.FormClosed += delegate { System.Windows.Forms.Application.ExitThread(); };
                show.Start(); System.Windows.Forms.Application.Run();
            }
            return 0;
        }
        if (args.Length > 0 && (args[0] == "--guard-target-slow-close" || args[0] == "--guard-target-refuse-close"))
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            using (var window = new System.Windows.Forms.Form { Text = "Owned close fixture", Width = 700, Height = 600 })
            using (var finish = new System.Windows.Forms.Timer { Interval = 900 })
            {
                bool finished = false;
                window.FormClosing += delegate(object sender, System.Windows.Forms.FormClosingEventArgs closing)
                {
                    if (finished) return;
                    closing.Cancel = true;
                    if (args[0] == "--guard-target-refuse-close") { window.Text = "Close refused"; return; }
                    // Simulate shutdown work after hiding, before destroying the window.
                    window.Hide(); finish.Start();
                };
                finish.Tick += delegate { finish.Stop(); finished = true; window.Close(); };
                System.Windows.Forms.Application.Run(window);
            }
            // Apps can keep a background process after their last window closes.
            System.Threading.Thread.Sleep(900);
            return 0;
        }
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
            // Lock is visible again. Routine checks cover its isolated pages,
            // password prompts and owned guard fixtures unless explicitly skipped.
            bool includeLock = !args.Contains("--skip-lock");
            if (args.Contains("--soundboard-only")) SoundboardChecks.Run(Check, output, window, Pump, Capture);
            else if (args.Contains("--lock-page-only")) LockRunningPageChecks.Run(Check, output, window, Pump, Capture);
            else if (args.Contains("--applock-only")) { RunningAppCatalogChecks.Run(Check); GuardLiveTargetsChecks.Run(Check, output); LockRunningPageChecks.Run(Check, output, window, Pump, Capture); AppPasswordChecks.Run(Check, output, window, Pump, Capture); LockMainChecks.Run(Check, output, Pump); GuardReliabilityChecks.Run(Check, output, Pump); GuardFocusChecks.Run(Check, output, Pump); PasswordDialogChecks(); BackgroundPromptChecks(); }
            else if (args.Contains("--focus-only")) { GuardFocusChecks.Run(Check, output, Pump); PasswordDialogChecks(); BackgroundPromptChecks(); }
            else if (args.Contains("--shutdown-only")) { ShutdownPresetChecks.Run(Check, output, window, Pump, Capture); DurationChecks(window); }
            else if (args.Contains("--background-only"))
            { if (!includeLock) throw new ArgumentException("Background Lock checks cannot run with --skip-lock."); BackgroundPromptChecks(); }
            else
            {
            File.WriteAllLines(Path.Combine(output, "theme.txt"), new[] { "HighContrast=" + SystemParameters.HighContrast, "Theme=" + Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme(), "Input=" + app.TryFindResource("TextControlBackground"), "ButtonDisabled=" + app.TryFindResource("ButtonBackgroundDisabled"), "WindowBackground=" + window.Background });
            Check(window.MinWidth == window.Width && window.MinHeight == window.Height, "Startup size is the minimum size");
            foreach (string name in new[] { "Home", "Shutdown", "Lock", "Sound", "Soundboard", "Settings" })
            { window.SelectPage(name); Pump(); Check(window.CurrentPage == name, "Navigate to " + name); Capture(window, name); }
            Check(Children<Wpf.Ui.Controls.Button>(window).Any(button => button.Content is StackPanel && Children<TextBlock>((StackPanel)button.Content).Any(text => text.Text == "Lock")), "Lock navigation is visible again");
            ShutdownPresetChecks.Run(Check, output, window, Pump, Capture);
            SoundboardChecks.Run(Check, output, window, Pump, Capture);
            DurationChecks(window);
            SoundChecks(window);
            TileChecks(window);
            ResizeChecks(window);
            WindowControls(window);
            PreviewActions(window, includeLock);
            if (includeLock)
            {
                RunningAppCatalogChecks.Run(Check);
                GuardLiveTargetsChecks.Run(Check, output);
                LockRunningPageChecks.Run(Check, output, window, Pump, Capture);
                AppPasswordChecks.Run(Check, output, window, Pump, Capture);
                LockMainChecks.Run(Check, output, Pump);
                GuardReliabilityChecks.Run(Check, output, Pump);
                GuardFocusChecks.Run(Check, output, Pump);
                PasswordChecks(window);
                PasswordDialogChecks();
                DragMaskChecks();
                OverlayChecks(window);
                BackgroundPromptChecks();
                OverlayCloseChecks();
                EventGuardChecks();
                HostedAppChecks();
                MultiAppMatchingChecks();
                CommonAppChecks();
                BorderlessFitChecks();
                LockBackendChecks();
            }
            BackendChecks();
            }
            window.Close(); Pump(); Check(!window.IsVisible, "X/Close releases the preview window");
            app.Shutdown();
            File.WriteAllLines(Path.Combine(output, "results.txt"), results);
            Console.WriteLine(string.Join(Environment.NewLine, results));
            return 0;
        }
        catch (Exception error)
        { File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString()); Console.WriteLine(error); return 1; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL: " + message); results.Add("PASS: " + message); File.AppendAllText(Path.Combine(output, "progress.txt"), "PASS: " + message + Environment.NewLine); }
    private static void Pump()
    { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); }
    private static void Type(DurationField field, string text)
    { field.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, new TextComposition(InputManager.Current, field, text)) { RoutedEvent = TextCompositionManager.PreviewTextInputEvent }); }
    private static void DurationChecks(FluentMainWindow window)
    {
        window.SelectPage("Shutdown"); Pump(); var duration = window.Shutdown.Duration;
        foreach (var field in new[] { duration.Hours, duration.Minutes, duration.Seconds })
        {
            var panel = (StackPanel)field.Parent;
            var arrows = panel.Children.OfType<Wpf.Ui.Controls.Button>().ToList();
            var origin = field.TranslatePoint(new Point(0, 0), duration);
            Check(arrows.Count == 2 && arrows.All(button => Math.Abs(button.ActualWidth - field.ActualWidth) < 0.1 &&
                Math.Abs(button.TranslatePoint(new Point(0, 0), duration).X - origin.X) < 0.1 && button.HorizontalContentAlignment == HorizontalAlignment.Center),
                "Both arrow buttons span and align with their " + System.Windows.Automation.AutomationProperties.GetName(field) + " field");
        }
        foreach (var field in new[] { duration.Hours, duration.Minutes, duration.Seconds })
        {
            duration.Hours.Focus(); duration.Minutes.Focus(); field.Focus(); Pump();
            string[] expected = { "05", "54", "43", "32", "21" }; int index = 0;
            foreach (char digit in "54321") { Type(field, digit.ToString()); Check(field.Text == expected[index++], "Rolling input " + expected[index - 1]); }
            Pump();
            Check(!field.ClearButtonEnabled && !field.ShowClearButton && Children<ButtonBase>(field).All(button => !button.IsVisible), "Focused timer field never reveals a clear button");
        }
        duration.Seconds.Focus(); Type(duration.Seconds, "5"); duration.Minutes.Focus(); Type(duration.Minutes, "2"); duration.Seconds.Focus(); Type(duration.Seconds, "4"); Check(duration.Seconds.Text == "04", "Changing fields resets digit entry");
        foreach (var field in new[] { duration.Minutes, duration.Seconds })
        {
            duration.Hours.Focus(); field.Focus(); Type(field, "59"); Type(field, "7");
            Check(field.Text == "07", "59 followed by 7 restarts at 07");
            Type(field, "8"); Check(field.Text == "08", "Invalid rolling value restarts at 08");
            Type(field, "9"); Check(field.Text == "09", "Invalid rolling value restarts at 09");
            duration.Hours.Focus(); field.Focus(); Type(field, "54"); Type(field, "7");
            Check(field.Text == "47", "Valid rolling values keep both digits");
            bool valid = true;
            for (int value = 0; value <= 59; value++) for (int digit = 0; digit <= 9; digit++)
            {
                duration.Hours.Focus(); field.Focus(); Type(field, value.ToString("00")); Type(field, digit.ToString());
                int next = (value % 10) * 10 + digit;
                valid &= field.Value == (next > 59 ? digit : next);
            }
            Check(valid, "All 600 minute/second rolling combinations remain valid and keep the newest digit");
            duration.Hours.Focus(); field.Focus(); Type(field, "59");
        }
        duration.Hours.Focus(); Type(duration.Hours, "9"); Type(duration.Hours, "9"); Check(duration.Hours.Text == "99", "Hours accept 99");
        duration.Seconds.Step(1); Check(duration.Seconds.Text == "00", "Seconds up wraps at 59"); duration.Seconds.Step(-1); Check(duration.Seconds.Text == "59", "Seconds down wraps at zero");
        duration.Minutes.Text = "60"; Check(duration.Minutes.Text == "59", "Invalid programmatic/paste edits restore valid text");
        Check(duration.TotalSeconds == 359999, "Duration converts 99:59:59 exactly");
    }
    private static void TileChecks(FluentMainWindow window)
    {
        window.SelectPage("Soundboard"); Pump(); var picker = window.Soundboard.Tiles;
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
        Capture(window, "Soundboard-large-grid");
        window.SelectPage("Sound"); Pump(); var soundRow = (TileRow)window.Sound.Tiles.Items[0];
        Check(soundRow.Width == 480, "Short Sound lists keep fixed 160px boxes including the primary output");
        window.Width = 1040; Pump();
    }
    private static void SoundChecks(FluentMainWindow window)
    {
        window.SelectPage("Sound"); Pump();
        Check(!Children<ComboBox>(window.Sound).Any(), "Sound has no primary-output dropdown that suggests changing Windows settings");
        Func<List<TileItem>> all = () => window.Sound.Tiles.Items.Cast<TileRow>().SelectMany(row => row.Items).ToList();
        var items = all(); var primary = items.Single(item => item.IsPrimary);
        Check(items.Count == 3 && primary.Id == "primary" && primary.Selected && !primary.Editable && primary.RoleLabel == "PRIMARY OUTPUT", "Sound lists the default primary output selected and read-only");
        primary.Selected = false;
        Check(primary.Selected && window.Sound.SecondaryIds().Count == 0, "Primary cannot be deselected or included in audio mirroring destinations");
        var start = Children<Wpf.Ui.Controls.Button>(window.Sound).Single(button => (string)button.Content == "Start multiple outputs");
        Check(!start.IsEnabled, "Primary selection alone does not enable multiple outputs");
        var secondary = items.Single(item => item.Id == "headphones"); secondary.Selected = true; Pump();
        Check(start.IsEnabled && secondary.RoleLabel == "SECONDARY OUTPUT" && window.Sound.SecondaryIds().SequenceEqual(new[] { "headphones" }), "Secondary selection enables mirroring and labels its role");
        Capture(window, "Sound-selected");
        window.Sound.ApplyDevices(new List<AudioOutputDevice> { new AudioOutputDevice("primary", "Speakers", false), new AudioOutputDevice("headphones", "Wireless headphones", true, 3), new AudioOutputDevice("monitor", "Display audio", false, 9) }); Pump();
        items = all();
        Check(items.Single(item => item.IsPrimary).Id == "headphones" && items.Single(item => item.Id == "primary").Editable && !start.IsEnabled && window.Sound.SecondaryIds().Count == 0, "A changed Windows default automatically updates the primary tile and removes it from secondary outputs");
        all().Single(item => item.Id == "monitor").Selected = true; Pump();
        window.Sound.LoadDevices().GetAwaiter().GetResult(); Pump();
        Check(window.Sound.SecondaryIds().SequenceEqual(new[] { "monitor" }) && all().Single(item => item.IsPrimary).Id == "primary", "Refreshing outputs preserves secondary selection and uses the current Windows default");
        window.Sound.ApplyDevices(new List<AudioOutputDevice> { new AudioOutputDevice("headphones", "Wireless headphones", false, 3) }); Pump();
        Check(!all().Any(item => item.IsPrimary) && !start.IsEnabled, "No Windows default disables mirroring instead of inventing a primary output");
        window.Sound.LoadDevices().GetAwaiter().GetResult(); Pump();
        all().Single(item => item.Id == "headphones").Selected = true; Pump();
    }
    private static void WindowControls(FluentMainWindow window)
    {
        var title = Children<Wpf.Ui.Controls.TitleBar>(window).Single();
        Check(title.ShowClose && title.ShowMinimize && title.ShowMaximize && !title.ForceShutdown, "Fluent title bar exposes window controls with graceful close");
        SystemCommands.MaximizeWindow(window); Pump(); Check(window.WindowState == WindowState.Maximized, "Maximize works");
        SystemCommands.RestoreWindow(window); Pump(); Check(window.WindowState == WindowState.Normal, "Restore works");
        SystemCommands.MinimizeWindow(window); Pump(); Check(window.WindowState == WindowState.Minimized, "Minimize works"); SystemCommands.RestoreWindow(window); Pump();
    }
    private static void ResizeChecks(FluentMainWindow window)
    {
        window.SelectPage("Home"); Pump();
        var dpi = VisualTreeHelper.GetDpi(window);
        var handle = new WindowInteropHelper(window).Handle;
        NativeRect bounds; Check(GetWindowRect(handle, out bounds), "Native resize fixture exposes its window bounds");
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
        Check(WindowChrome.GetWindowChrome(window).ResizeBorderThickness.Left == 6, "Resize edges use a wider invisible border");
        foreach (int offset in new[] { 2, 5 })
        {
            int x = (int)(offset * dpi.DpiScaleX), y = (int)(offset * dpi.DpiScaleY);
            bool corners = NativeHit(handle, bounds.Left + x, bounds.Top + y) == 13 &&
                NativeHit(handle, bounds.Right - 1 - x, bounds.Top + y) == 14 &&
                NativeHit(handle, bounds.Left + x, bounds.Bottom - 1 - y) == 16 &&
                NativeHit(handle, bounds.Right - 1 - x, bounds.Bottom - 1 - y) == 17;
            Check(corners, "All four native corners allow diagonal resizing at " + offset + " DIP inward");
        }
        int edgeX = (int)(4 * dpi.DpiScaleX), edgeY = (int)(4 * dpi.DpiScaleY);
        Check(NativeHit(handle, bounds.Left + edgeX, bounds.Top + height / 2) == 10 &&
            NativeHit(handle, bounds.Right - 1 - edgeX, bounds.Top + height / 2) == 11 &&
            NativeHit(handle, bounds.Left + width / 2, bounds.Top + edgeY) == 12 &&
            NativeHit(handle, bounds.Left + width / 2, bounds.Bottom - 1 - edgeY) == 15, "Native edge resizing keeps horizontal and vertical directions");
        foreach (var button in Children<Wpf.Ui.Controls.TitleBarButton>(window).Where(button => button.IsVisible))
        {
            var center = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            int expected = button.ButtonType == Wpf.Ui.Controls.TitleBarButtonType.Close ? 20 : button.ButtonType == Wpf.Ui.Controls.TitleBarButtonType.Minimize ? 8 : 9;
            Check(NativeHit(handle, (int)center.X, (int)center.Y) == expected, "Resize corners preserve title-button hit testing: " + button.ButtonType);
            foreach (double horizontal in new[] { 0.25, 0.5, 0.75 }) foreach (double vertical in new[] { 0.25, 0.5, 0.75 })
            {
                var interior = button.PointToScreen(new Point(button.ActualWidth * horizontal, button.ActualHeight * vertical));
                Check(NativeHit(handle, (int)interior.X, (int)interior.Y) == expected, "Resize rim leaves title-button interior clickable: " + button.ButtonType);
            }
        }
        var caption = window.PointToScreen(new Point(400, 20));
        Check(NativeHit(handle, (int)caption.X, (int)caption.Y) == 2, "The middle of the title bar still drags the window");
        int resizeLoops = 0;
        HwndSourceHook resizeHook = delegate(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        { if (message == 0x0231) resizeLoops++; return IntPtr.Zero; }; // WM_ENTERSIZEMOVE
        var source = HwndSource.FromHwnd(handle); source.AddHook(resizeHook);
        try
        {
            foreach (int direction in new[] { 13, 14, 16, 17 })
            {
                int x = direction == 13 || direction == 16 ? bounds.Left + (int)(5 * dpi.DpiScaleX) : bounds.Right - 1 - (int)(5 * dpi.DpiScaleX);
                int y = direction == 13 || direction == 14 ? bounds.Top + (int)(5 * dpi.DpiScaleY) : bounds.Bottom - 1 - (int)(5 * dpi.DpiScaleY);
                int before = resizeLoops;
                // Cancel only this owned fixture's native resize loop from a worker.
                var cancel = Task.Run(delegate { System.Threading.Thread.Sleep(100); PostMessage(handle, 0x0100, new IntPtr(0x1B), IntPtr.Zero); });
                SendMessage(handle, 0x00A1, new IntPtr(direction), Coordinates(x, y));
                cancel.GetAwaiter().GetResult(); Pump();
                Check(resizeLoops == before + 1 && window.IsVisible, "Corner clicks start native resizing instead of activating a title button: " + direction);
            }
        }
        finally { source.RemoveHook(resizeHook); }
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            Check(WindowResizeBehavior.HitTest(5 * scale, 5 * scale, 1040 * scale, 740 * scale, scale, scale) == 13 &&
                WindowResizeBehavior.HitTest(500 * scale, 4 * scale, 1040 * scale, 740 * scale, scale, scale) == 12 &&
                WindowResizeBehavior.HitTest(10 * scale, 3 * scale, 1040 * scale, 740 * scale, scale, scale) == 13 &&
                WindowResizeBehavior.HitTest(10 * scale, 10 * scale, 1040 * scale, 740 * scale, scale, scale) == 0 &&
                WindowResizeBehavior.HitTest(500 * scale, 40 * scale, 1040 * scale, 740 * scale, scale, scale) == 0,
                "Corner and edge regions scale correctly at " + (scale * 100) + "% DPI without stealing content");
        double previousLeft = window.Left; window.Left = -300; Pump(); GetWindowRect(handle, out bounds);
        Check(NativeHit(handle, bounds.Left + (int)(5 * dpi.DpiScaleX), bounds.Top + (int)(5 * dpi.DpiScaleY)) == 13, "Resize coordinates work to the left of the primary monitor");
        window.Left = previousLeft; SystemCommands.MaximizeWindow(window); Pump(); GetWindowRect(handle, out bounds);
        // Maximized bounds include an invisible system frame outside the screen.
        int maximized = NativeHit(handle, bounds.Left + (int)(12 * dpi.DpiScaleX), bounds.Top + (int)(12 * dpi.DpiScaleY));
        Check(maximized < 10 || maximized > 17, "Maximized windows do not expose corner resizing");
        SystemCommands.RestoreWindow(window); Pump();
    }
    private static int NativeHit(IntPtr handle, int x, int y)
    { return SendMessage(handle, 0x0084, IntPtr.Zero, Coordinates(x, y)).ToInt32(); }
    private static IntPtr Coordinates(int x, int y) { return new IntPtr(unchecked((int)(((uint)(ushort)y << 16) | (ushort)x))); }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr handle, out NativeRect bounds);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr region);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EqualRgn(IntPtr first, IntPtr second);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    private static int RegionType(IntPtr window)
    {
        IntPtr region = CreateRectRgn(0, 0, 0, 0);
        try { return GetWindowRgn(window, region); }
        finally { DeleteObject(region); }
    }
    private static void PreviewActions(FluentMainWindow window, bool includeLock)
    {
        // Handlers must exit without touching shutdown.exe, audio, the password or startup settings.
        foreach (string name in new[] { "Shutdown", "Lock", "Sound", "Soundboard", "Settings" })
        {
            window.SelectPage(name); Pump();
            foreach (var tile in Children<ToggleButton>(window).Where(button => button.DataContext is TileItem).Take(1)) tile.IsChecked = true;
            foreach (var button in Children<Wpf.Ui.Controls.Button>(window).Where(button => button.IsEnabled && button.Content is string && ((string)button.Content == "Schedule shutdown" || (string)button.Content == "Cancel shutdown" || (string)button.Content == "Lock selected apps" || (string)button.Content == "Set password" || (string)button.Content == "Start multiple outputs")).ToList()) button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            foreach (var toggle in Children<Wpf.Ui.Controls.ToggleSwitch>(window)) toggle.IsChecked = !toggle.IsChecked;
            Pump();
        }
        if (includeLock) Check(window.Lock.CanClose(), "Preview guard never arms and can close without a password");
    }
    private static void PasswordChecks(FluentMainWindow owner)
    {
        string path = Path.Combine(output, "test-password.dat"); var store = new PasswordStore(path); store.SetPassword("a");
        Check(store.Verify("a") && !store.Verify("wrong"), "Existing encrypted password store accepts simple test passwords and rejects wrong ones");
        var dialog = new PasswordWindow(owner, store, false, "UI test password"); dialog.Show(); Pump();
        File.WriteAllText(Path.Combine(output, "focus.txt"), "Active=" + dialog.IsActive + "; focused=" + Keyboard.FocusedElement + "; logical=" + FocusManager.GetFocusedElement(dialog));
        Check(Keyboard.FocusedElement is PasswordBox, "Password field receives immediate keyboard focus"); dialog.Close();
        var create = new PasswordWindow(owner, store, true, "Set UtilityTool password"); create.Show(); Pump();
        Check(Children<PasswordBox>(create).All(field => Children<Border>(field).Any(border => border.CornerRadius.TopLeft == 8)), "Main password fields use the rounded Fluent style");
        foreach (var field in Children<PasswordBox>(create))
        {
            field.Password = "abcdef"; field.UpdateLayout(); Pump();
            var image = new RenderTargetBitmap((int)field.ActualWidth, (int)field.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual(); using (var drawing = visual.RenderOpen()) drawing.DrawRectangle(new VisualBrush(field), null, new Rect(0, 0, field.ActualWidth, field.ActualHeight)); image.Render(visual);
            byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            int bright = 0;
            for (int y = 8; y < image.PixelHeight - 8; y++) for (int x = 8; x < image.PixelWidth - 8; x++)
            { int offset = (y * image.PixelWidth + x) * 4; if (pixels[offset] > 180 && pixels[offset + 1] > 180 && pixels[offset + 2] > 180) bright++; }
            Check(bright > 100, "Typed password renders visible mask dots in each main dialog field");
        }
        Capture(create, "Password-main"); create.Close(); Pump();
        File.Delete(path);
    }
    private static void PasswordDialogChecks()
    {
        string path = Path.Combine(output, "prompt-password.dat"); var store = new PasswordStore(path); store.SetPassword("a");
        using (var dialog = new UtilityTool.Frontend.PasswordDialog(store, false, "Unlock test app", false))
        {
            dialog.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None; dialog.Show(); dialog.FocusPassword(); Pump();
            var input = (System.Windows.Forms.TextBox)typeof(UtilityTool.Frontend.PasswordDialog).GetField("password", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
            var submit = (System.Windows.Forms.Button)dialog.AcceptButton;
            Check(input.Focused && input.UseSystemPasswordChar && input.Parent is UtilityTool.Frontend.PasswordInputFrame, "Modern app password field receives immediate native focus and remains masked");
            input.Text = "abcdef";
            using (var image = new System.Drawing.Bitmap(dialog.Width, dialog.Height))
            {
                dialog.DrawToBitmap(image, dialog.ClientRectangle); image.Save(Path.Combine(output, "Password-app.png"));
                var origin = dialog.PointToClient(input.PointToScreen(System.Drawing.Point.Empty)); int bright = 0;
                for (int y = 2; y < input.Height - 2; y++) for (int x = 2; x < input.Width - 2; x++)
                { var pixel = image.GetPixel(origin.X + x, origin.Y + y); if (pixel.R > 180 && pixel.G > 180 && pixel.B > 180) bright++; }
                Check(bright > 100, "Typed app password renders visible mask dots");
            }
            input.Clear();
            // Direct WM_MOUSE messages skip the OS mouse-activation step. Bring
            // this owned fixture forward so native Button hit testing is valid.
            dialog.TopMost = true; dialog.BringToFront(); dialog.Activate(); input.Focus(); Pump();
            SendMessage(input.Handle, 0x0102, new IntPtr('a'), IntPtr.Zero); // WM_CHAR
            var center = Coordinates(submit.Width / 2, submit.Height / 2);
            var screenPoint = submit.PointToScreen(new System.Drawing.Point(submit.Width / 2, submit.Height / 2));
            Check(WindowFromPoint(new NativePoint { X = screenPoint.X, Y = screenPoint.Y }) == submit.Handle, "Owned mouse-click fixture is exposed for native button hit testing");
            SendMessage(submit.Handle, 0x0201, new IntPtr(1), center); // WM_LBUTTONDOWN
            SendMessage(submit.Handle, 0x0101, new IntPtr('A'), IntPtr.Zero); // WM_KEYUP overlaps the click
            SendMessage(submit.Handle, 0x0202, IntPtr.Zero, center); // WM_LBUTTONUP
            var wait = Stopwatch.StartNew(); while (!dialog.IsDisposed && dialog.Visible && wait.ElapsedMilliseconds < 8000) { Pump(); System.Threading.Thread.Sleep(5); }
            Check(dialog.DialogResult == System.Windows.Forms.DialogResult.OK && !dialog.Visible, "Typing and immediately clicking Unlock survives overlapping key-up");
        }
        using (var dialog = new UtilityTool.Frontend.PasswordDialog(store, false, "Unlock test app", false))
        {
            dialog.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None; dialog.Show(); dialog.FocusPassword(); Pump();
            var input = (System.Windows.Forms.TextBox)typeof(UtilityTool.Frontend.PasswordDialog).GetField("password", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
            SendMessage(input.Handle, 0x0102, new IntPtr('a'), IntPtr.Zero);
            SendMessage(input.Handle, 0x0100, new IntPtr(13), IntPtr.Zero);
            SendMessage(input.Handle, 0x0100, new IntPtr(13), IntPtr.Zero); // held-key repeat must not submit twice
            SendMessage(input.Handle, 0x0101, new IntPtr(13), IntPtr.Zero);
            var wait = Stopwatch.StartNew(); while (!dialog.IsDisposed && dialog.Visible && wait.ElapsedMilliseconds < 8000) { Pump(); System.Threading.Thread.Sleep(5); }
            Check(dialog.DialogResult == System.Windows.Forms.DialogResult.OK && !dialog.Visible, "Enter immediately after typing unlocks the modeless app prompt, including held-key repeats");
        }
        using (var dialog = new UtilityTool.Frontend.PasswordDialog(store, false, "Unlock test app", true))
        {
            dialog.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None; dialog.Show(); Pump();
            var input = (System.Windows.Forms.TextBox)typeof(UtilityTool.Frontend.PasswordDialog).GetField("password", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
            var message = (System.Windows.Forms.Label)typeof(UtilityTool.Frontend.PasswordDialog).GetField("message", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
            input.Text = "wrong"; SendMessage(input.Handle, 0x0100, new IntPtr(13), IntPtr.Zero); SendMessage(input.Handle, 0x0101, new IntPtr(13), IntPtr.Zero);
            bool explained = false; var wait = Stopwatch.StartNew();
            while (!dialog.IsDisposed && dialog.Visible && wait.ElapsedMilliseconds < 8000) { Pump(); explained |= message.Text.Contains("incorrect") && message.Text.Contains("close"); System.Threading.Thread.Sleep(5); }
            Check(explained && dialog.DialogResult == System.Windows.Forms.DialogResult.Cancel && !dialog.Visible, "Enter submits a wrong app password, explains closure inline and closes without an old message box");
        }
        File.Delete(path);
    }
    private static void BackendChecks()
    {
        Check(ShutdownService.ScheduleArguments(90) == "/s /t 90" && ShutdownService.CancelArguments == "/a", "Shutdown argument generation is unchanged (no command executed)");
        using (var mirror = new AudioMirrorService()) { mirror.StopAsync().GetAwaiter().GetResult(); Check(!mirror.IsRunning, "Idle audio service stops/disposes without changing Windows output"); }
    }
    private static void LockBackendChecks()
    {
        string path = Path.Combine(output, "test-locks.xml"); var store = new LockStateStore(path); var state = new LockState { Locked = true }; state.Apps.Add(new InstalledApp("fake", "Fake app", "fake.exe")); store.Save(state); var restored = store.Load();
        Check(restored.Locked && restored.Apps.Count == 1 && restored.Apps[0].Id == "fake", "Existing lock selections and armed state persist"); File.Delete(path);
        var process = new GuardProcess(123, "Fake app", "fake.exe", "", 456); int closed = 0, requested = 0;
        using (var guard = new AppGuardService(() => new List<GuardProcess> { process }, value => closed++))
        {
            guard.RequestAccess = value => { requested++; return true; }; guard.Arm(state.Apps, false); guard.ScanOnce(); guard.ScanOnce(); Check(requested == 1 && closed == 0, "Approved process remains unlocked until exit");
            var pending = new TaskCompletionSource<bool>(); guard.Disarm(); guard.RequestAccessAsync = value => pending.Task; guard.Arm(state.Apps, false); guard.ScanOnce(); guard.Disarm(); pending.SetResult(false); Pump(); Check(closed == 0, "Disarming invalidates pending access without stopping the app");
        }
    }
    private static void MultiAppMatchingChecks()
    {
        var firefox = new InstalledApp("firefox", "Firefox", "firefox.exe");
        var camera = new InstalledApp("Microsoft.WindowsCamera_8wekyb3d8bbwe!App", "Camera", "old-camera-path.exe");
        var league = new InstalledApp("league", "League client UI", "LeagueClientUx.exe");
        var processes = new List<GuardProcess> {
            new GuardProcess(101, "Firefox", "firefox.exe", "", 1),
            new GuardProcess(102, "Camera", "new-camera-path.exe", camera.Id, 2),
            new GuardProcess(103, "League client", "LeagueClientUx.exe", "", 3),
            new GuardProcess(104, "Unselected app", "other.exe", "", 4) };
        var waiting = new Dictionary<int, TaskCompletionSource<bool>>();
        var stopped = new System.Collections.Concurrent.ConcurrentBag<int>();
        using (var guard = new AppGuardService(() => processes, process => stopped.Add(process.Id)))
        {
            guard.RequestAccessAsync = process => { var source = new TaskCompletionSource<bool>(); waiting.Add(process.Id, source); return source.Task; };
            guard.Arm(new[] { firefox, camera, league }, false); guard.ScanOnce(); guard.ScanOnce();
            Check(waiting.Keys.OrderBy(id => id).SequenceEqual(new[] { 101, 102, 103 }), "Several selected apps request access independently while another app remains unaffected");
            Check(waiting.ContainsKey(102), "Store-app ID still matches when its executable path has changed");
            waiting[101].SetResult(true); waiting[102].SetResult(false);
            var completion = Stopwatch.StartNew();
            while ((!guard.IsApproved(firefox) || guard.IsPending(camera)) && completion.ElapsedMilliseconds < 2000) { Pump(); System.Threading.Thread.Sleep(1); }
            Check(stopped.IsEmpty && guard.IsApproved(firefox) && !guard.IsApproved(camera) && guard.IsPending(league) && waiting.Count == 3,
                "Modern asynchronous denial leaves the app unapproved without closing it or affecting another app's pending lock");
            guard.Disarm(); waiting[103].SetResult(true);
        }
        string root = Path.Combine(output, "riot-matching-fixture-" + Guid.NewGuid().ToString("N"));
        string client = Path.Combine(root, "Riot Client", "RiotClientServices.exe"), game = Path.Combine(root, "League of Legends");
        Directory.CreateDirectory(Path.GetDirectoryName(client)); Directory.CreateDirectory(game);
        File.WriteAllText(client, "owned fixture");
        File.WriteAllText(Path.Combine(game, "LeagueClient.exe"), "owned fixture");
        File.WriteAllText(Path.Combine(game, "LeagueClientUx.exe"), "owned fixture");
        var shortcut = new InstalledApp("league-shortcut", "League of Legends", client, "--launch-product=league_of_legends --launch-patchline=live");
        var targets = AppGuardTargets.Executables(shortcut, Path.Combine(root, "metadata"));
        Check(targets.Count == 2 && targets.Contains(Path.Combine(game, "LeagueClientUx.exe")) && !targets.Contains(client), "League shortcut resolves its real client executables without locking the shared Riot launcher");
        Check(AppGuardTargets.Executables(new InstalledApp("riot", "Riot Client", client), root).SequenceEqual(new[] { client }), "Selecting Riot Client itself still targets Riot Client");
        Check(AppGuardTargets.Executables(new InstalledApp("old-league", "League of Legends", client), root).Contains(Path.Combine(game, "LeagueClientUx.exe")), "Previously saved League selections resolve without needing shortcut arguments");
        string metadata = Path.Combine(root, "Riot Games", "Metadata", "league_of_legends.live"); Directory.CreateDirectory(metadata);
        string customGame = Path.Combine(root, "Custom Game Location"); Directory.CreateDirectory(customGame); File.WriteAllText(Path.Combine(customGame, "LeagueClientUx.exe"), "owned fixture");
        File.WriteAllText(Path.Combine(metadata, "league_of_legends.live.product_settings.yaml"), "product_install_full_path: \"" + customGame.Replace('\\', '/') + "\"\n");
        Check(AppGuardTargets.Executables(shortcut, root).SequenceEqual(new[] { Path.Combine(customGame, "LeagueClientUx.exe") }), "Riot metadata resolves League installed in a custom folder");
        string statePath = Path.Combine(output, "matching-locks.xml");
        var state = new LockState { Locked = true }; state.Apps.Add(shortcut); var states = new LockStateStore(statePath); states.Save(state);
        Check(states.Load().Apps[0].LaunchArguments == shortcut.LaunchArguments, "Saved lock selections preserve launcher arguments"); File.Delete(statePath);
    }

    private static void HostedAppChecks()
    {
        string path = Path.Combine(output, "hosted-password.dat"); var passwords = new PasswordStore(path); passwords.SetPassword("a");
        using (var frame = new OwnedApplicationFrame())
        using (var target = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--guard-target-hosted " + frame.Handle.ToInt64()) { UseShellExecute = false }))
        {
            try
            {
                var wait = Stopwatch.StartNew();
                while (!AppWindows.WindowProcessIds(frame.Handle).Contains(target.Id) && wait.ElapsedMilliseconds < 5000) { Pump(); System.Threading.Thread.Sleep(5); }
                Check(AppWindows.ProcessId(frame.Handle) != target.Id && AppWindows.WindowProcessIds(frame.Handle).Contains(target.Id), "Hosted-app fixture has separate frame and app processes");
                Check(AppWindows.VisibleProcessIds().Contains(target.Id) && AppWindows.Find(target.Id).ContainsKey(frame.Handle), "Store-app detection maps its child process to the visible host frame");
                using (var overlay = new UtilityTool.Frontend.AppLockOverlay(new GuardProcess(target.Id, "Hosted app", target.MainModule.FileName, "", target.StartTime.ToUniversalTime().Ticks)))
                {
                    var result = overlay.PromptAsync(passwords);
                    typeof(UtilityTool.Frontend.AppLockOverlay).GetMethod("ShowPrompt", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, new object[] { frame.Handle, true }); Pump();
                    var curtain = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().Single();
                    Check(curtain.Visible && NativeWindowOwner(curtain.Handle, 4) == frame.Handle, "Hosted app gets its curtain on the frame rather than an invisible child window");
                    overlay.Cancel(); Pump(); Check(result.IsCompleted && curtain.IsDisposed, "Disarming a hosted app releases its curtain and pending password request");
                }
            }
            finally { if (!target.HasExited) { target.Kill(); target.WaitForExit(); } }
        }
        File.Delete(path);
    }

    private sealed class OwnedApplicationFrame : IDisposable
    {
        private static readonly NativeFrameProc procedure = FrameProcedure;
        internal readonly IntPtr Handle;
        internal OwnedApplicationFrame()
        {
            var instance = NativeModule(IntPtr.Zero);
            var registration = new NativeWindowClass { Instance = instance, Procedure = procedure, ClassName = "ApplicationFrameWindow" };
            NativeRegisterClass(ref registration);
            Handle = NativeCreateWindow(0, registration.ClassName, "Owned Store-app frame fixture", 0x10CF0000, 180, 160, 700, 600, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        private static IntPtr FrameProcedure(IntPtr window, uint message, IntPtr first, IntPtr second) { return NativeDefWindowProc(window, message, first, second); }
        public void Dispose() { NativeDestroyWindow(Handle); }
    }
    private delegate IntPtr NativeFrameProc(IntPtr window, uint message, IntPtr first, IntPtr second);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeWindowClass
    {
        internal uint Style; internal NativeFrameProc Procedure; internal int ClassBytes, WindowBytes;
        internal IntPtr Instance, Icon, Cursor, Background; internal string MenuName, ClassName;
    }
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")] private static extern IntPtr NativeModule(IntPtr name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort NativeRegisterClass(ref NativeWindowClass registration);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr NativeCreateWindow(uint extendedStyle, string name, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern IntPtr NativeDefWindowProc(IntPtr window, uint message, IntPtr first, IntPtr second);
    [DllImport("user32.dll", EntryPoint = "DestroyWindow")] private static extern bool NativeDestroyWindow(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "SetParent")] private static extern IntPtr SetNativeParent(IntPtr window, IntPtr parent);

    private static void EventGuardChecks()
    {
        string path = Path.Combine(output, "event-password.dat"); var store = new PasswordStore(path); store.SetPassword("a");
        var dispatcher = Dispatcher.CurrentDispatcher;
        var overlays = new List<UtilityTool.Frontend.AppLockOverlay>();
        string executable = Process.GetCurrentProcess().MainModule.FileName;
        int requests = 0;
        using (var guard = new AppGuardService())
        {
            guard.RequestAccessAsync = process => dispatcher.Invoke(new Func<Task<bool>>(() =>
            {
                requests++; var overlay = new UtilityTool.Frontend.AppLockOverlay(process); overlays.Add(overlay);
                return overlay.PromptAsync(store, () => guard.IsArmed);
            }));
            guard.Arm(new[] { new InstalledApp("owned-event-fixture", "Owned event fixture", executable) }, true);
            Check(guard.EventDriven, "Guard registers native window event hooks while armed");
            guard.ScanOnce();
            try
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                using (var target = Process.Start(new ProcessStartInfo { FileName = executable, Arguments = "--guard-target-delayed", UseShellExecute = false }))
                {
                    try
                    {
                        var watch = Stopwatch.StartNew(); long shown = -1;
                        using (var events = new WindowEventWatcher((window, type) =>
                        { if (type == WindowEventWatcher.Show && AppWindows.ProcessId(window) == target.Id && shown < 0) shown = watch.ElapsedMilliseconds; }, target.Id))
                        {
                            while ((shown < 0 || !System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().Any()) && watch.ElapsedMilliseconds < 5000)
                            { Pump(); System.Threading.Thread.Sleep(1); }
                            long latency = shown < 0 ? long.MaxValue : watch.ElapsedMilliseconds - shown;
                            Check(requests == attempt && latency < 600, "Window events cover newly shown apps without waiting for the 750ms scan (launch " + attempt + ")");
                            results.Add("INFO: Show-event to curtain latency, launch " + attempt + ": " + latency + " ms");
                            var cover = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().Single();
                            var timer = (System.Windows.Forms.Timer)typeof(UtilityTool.Frontend.AppLockOverlay).GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(overlays.Last());
                            Check(timer.Interval == 1000, "Window events replace 100ms overlay polling with a 1s backup");
                            Task.Run(() => guard.ScanOnce()).Wait(); Pump();
                            Check(requests == attempt, "A fallback scan and event burst do not create duplicate prompts");
                            target.Refresh(); target.CloseMainWindow();
                            var close = Stopwatch.StartNew();
                            while (System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().Any() && close.ElapsedMilliseconds < 700)
                            { Pump(); System.Threading.Thread.Sleep(1); }
                            Check(!System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().Any() && close.ElapsedMilliseconds < 700, "Closing the app promptly removes its curtain without waiting for the backup timer");
                            results.Add("INFO: Close to curtain removal, launch " + attempt + ": " + close.ElapsedMilliseconds + " ms");
                        }
                    }
                    finally { target.CloseMainWindow(); if (!target.WaitForExit(1500)) { target.Kill(); target.WaitForExit(); } }
                }
            }
            finally { guard.Disarm(); foreach (var overlay in overlays) overlay.Dispose(); }
            Check(!guard.EventDriven, "Disarming removes native event hooks and queued work");
        }
        File.Delete(path);
    }
    private static void DragMaskChecks()
    {
        Check(WindowDragMask.TryCreate(IntPtr.Zero) == null, "Invalid drag targets leave window drawing untouched");
        using (var window = new System.Windows.Forms.Form { Width = 400, Height = 300 })
        {
            window.Show(); Pump();
            int previous = RegionType(window.Handle);
            using (var mask = WindowDragMask.TryCreate(window.Handle))
                Check(mask != null && window.Visible && RegionType(window.Handle) == 1, "Drag mask hides drawing without hiding the native window or its ownership");
            Check(RegionType(window.Handle) == previous, "Drag mask restores an ordinary window's original drawing region");
            using (var shape = new System.Drawing.Region(new System.Drawing.Rectangle(0, 0, 380, 280)))
            {
                shape.Exclude(new System.Drawing.Rectangle(30, 30, 50, 50)); window.Region = shape.Clone();
            }
            IntPtr before = CreateRectRgn(0, 0, 0, 0), after = CreateRectRgn(0, 0, 0, 0);
            try
            {
                GetWindowRgn(window.Handle, before);
                var mask = WindowDragMask.TryCreate(window.Handle); Check(mask != null && RegionType(window.Handle) == 1, "Custom-shaped windows are temporarily masked during dragging");
                mask.Dispose(); mask.Dispose(); GetWindowRgn(window.Handle, after);
                Check(EqualRgn(before, after), "Custom window shape is restored exactly and repeated cleanup is safe");
            }
            finally { DeleteObject(before); DeleteObject(after); }
        }
    }
    private static void OverlayChecks(FluentMainWindow main)
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
                    var cover = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().Single();
                    var theme = ((LinearGradientBrush)((Grid)main.Content).Background).GradientStops[0].Color;
                    Check(cover.Opacity == 1 && cover.TransparencyKey.IsEmpty && cover.BackColor == dialog.BackColor && cover.BackColor.R == theme.R && cover.BackColor.G == theme.G && cover.BackColor.B == theme.B, "Opaque privacy curtain and prompt match the main app background");
                    Check(!cover.TopMost && !dialog.TopMost && dialog.Owner == cover && cover.MinimizeBox && cover.MaximizeBox && cover.ControlBox, "Privacy curtain retains app-level ownership and native window controls");
                    using (var image = new System.Drawing.Bitmap(cover.Width, cover.Height))
                    {
                        cover.DrawToBitmap(image, new System.Drawing.Rectangle(0, 0, image.Width, image.Height));
                        bool hidden = true;
                        for (int y = 60; y < image.Height - 80; y += 17) for (int x = 20; x < image.Width - 20; x += 17)
                            hidden &= image.GetPixel(x, y).ToArgb() == cover.BackColor.ToArgb();
                        Check(hidden, "Privacy curtain paints a solid background across the locked app content");
                        using (var prompt = new System.Drawing.Bitmap(dialog.Width, dialog.Height))
                        using (var graphics = System.Drawing.Graphics.FromImage(image))
                        { dialog.DrawToBitmap(prompt, dialog.ClientRectangle); graphics.DrawImageUnscaled(prompt, dialog.Left - cover.Left, dialog.Top - cover.Top); }
                        image.Save(Path.Combine(output, "Lock-privacy-curtain.png"));
                    }
                    int originalRegion = RegionType(target.MainWindowHandle);
                    var start = cover.Bounds; bool aligned = true;
                    IntPtr moving = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeRect)));
                    try
                    {
                        SendMessage(cover.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
                        for (int step = 1; step <= 24; step++)
                        {
                            var next = new System.Drawing.Rectangle(start.Left + step * 3, start.Top + step * 2, start.Width, start.Height);
                            Marshal.StructureToPtr(new NativeRect { Left = next.Left, Top = next.Top, Right = next.Right, Bottom = next.Bottom }, moving, false);
                            SendMessage(cover.Handle, 0x0216, IntPtr.Zero, moving);
                            cover.Location = next.Location; Pump();
                            NativeRect targetBounds; GetWindowRect(target.MainWindowHandle, out targetBounds);
                            var expectedTarget = cover.TargetLocation(next.Location);
                            aligned &= cover.IsDragging && RegionType(target.MainWindowHandle) == 1 && targetBounds.Left == expectedTarget.X && targetBounds.Top == expectedTarget.Y;
                        }
                        Check(aligned && dialog.Visible, "Protected app stays visually masked during 24 native drag steps while curtain and prompt move");
                        SendMessage(cover.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
                        NativeRect finalBounds; GetWindowRect(target.MainWindowHandle, out finalBounds);
                        var finalTarget = cover.TargetLocation(cover.Location);
                        Check(!cover.IsDragging && RegionType(target.MainWindowHandle) == originalRegion && finalBounds.Left == finalTarget.X && finalBounds.Top == finalTarget.Y, "Releasing the drag aligns the app before restoring its drawing surface");
                        SendMessage(cover.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
                        Check(RegionType(target.MainWindowHandle) == 1, "Another drag safely masks the target again");
                    }
                    finally { Marshal.FreeHGlobal(moving); }
                    overlay.Cancel(); Pump(); Check(result.IsCompleted && !result.Result, "Cancelling the overlay resolves pending access");
                    Check(RegionType(target.MainWindowHandle) == originalRegion, "Disarming mid-drag restores the app's drawing surface");
                    Check(!System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Any(form => form is UtilityTool.Frontend.LockCoverWindow || form is UtilityTool.Frontend.PasswordDialog), "Disarming/disposal removes every privacy curtain and prompt");
                }
                using (var overlay = new UtilityTool.Frontend.AppLockOverlay(new GuardProcess(target.Id, "Fixture", target.MainModule.FileName, "", target.StartTime.ToUniversalTime().Ticks)))
                {
                    var result = overlay.PromptAsync(store, () => true);
                    typeof(UtilityTool.Frontend.AppLockOverlay).GetMethod("ShowPrompt", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, new object[] { target.MainWindowHandle, true }); Pump();
                    var dialog = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.PasswordDialog>().Single();
                    var input = (System.Windows.Forms.TextBox)typeof(UtilityTool.Frontend.PasswordDialog).GetField("password", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
                    SendMessage(input.Handle, 0x0102, new IntPtr('a'), IntPtr.Zero);
                    SendMessage(input.Handle, 0x0100, new IntPtr(13), IntPtr.Zero);
                    SendMessage(input.Handle, 0x0101, new IntPtr(13), IntPtr.Zero);
                    wait.Restart(); while (!result.IsCompleted && wait.ElapsedMilliseconds < 8000) { Pump(); System.Threading.Thread.Sleep(5); }
                    Check(result.IsCompleted && result.Result, "The privacy curtain's owned password prompt unlocks with Enter");
                }
                Pump(); Check(!System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().Any(form => form is UtilityTool.Frontend.LockCoverWindow || form is UtilityTool.Frontend.PasswordDialog), "Successful access releases the privacy curtain and prompt");
            }
            finally { target.CloseMainWindow(); if (!target.WaitForExit(1500)) { target.Kill(); target.WaitForExit(); } }
        }
        File.Delete(path);
    }
    private static void CommonAppChecks()
    {
        string root = Path.Combine(output, "common-app-fixtures"), discord = Path.Combine(root, "Discord");
        Directory.CreateDirectory(discord); File.WriteAllText(Path.Combine(discord, "Update.exe"), "owned fixture");
        foreach (string version in new[] { "app-1.0.9261", "app-1.0.9262" })
        { string folder = Path.Combine(discord, version); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "Discord.exe"), "owned fixture"); }
        var app = new InstalledApp("discord", "Discord", Path.Combine(discord, "Update.exe"), "--processStart \"Discord.exe\"");
        var rule = VersionedAppExecutable.From(app);
        Check(rule != null && AppGuardTargets.Executables(app).Count == 2 && AppGuardTargets.Executables(app).All(path => Path.GetFileName(path) == "Discord.exe"), "Discord updater shortcut resolves the actual installed app versions, not Update.exe");
        var versionPath = Path.Combine(discord, "app-1.0.9261", "Discord.exe");
        Check(VersionedAppExecutable.From(new InstalledApp("discord-old", "Discord", versionPath)).Matches(Path.Combine(discord, "app-1.0.9263", "Discord.exe")), "An old version-specific shortcut matches a future version of the same installed app");
        Check(!rule.Matches(Path.Combine(root, "OtherDiscord", "app-1.0.9263", "Discord.exe")) && !rule.Matches(Path.Combine(discord, "app-1.0.9263", "Other.exe")) && !rule.Matches(Path.Combine(discord, "app-backup", "Discord.exe")), "Version matching excludes other installations, unrelated executables and non-version folders");
        Check(VersionedAppExecutable.From(new InstalledApp("invalid", "Invalid", Path.Combine(discord, "Update.exe"), "--processStart ../other.exe")) == null, "Updater arguments cannot broaden a lock outside its app installation");
        var snapshots = new List<GuardProcess>(); int requests = 0;
        using (var guard = new AppGuardService(() => snapshots, process => { }))
        {
            guard.RequestAccess = process => { requests++; return true; }; guard.Arm(new[] { app }, false);
            snapshots.Add(new GuardProcess(11, "Discord", Path.Combine(discord, "app-1.0.9999", "Discord.exe"), "", 11));
            snapshots.Add(new GuardProcess(12, "Unrelated", Path.Combine(root, "OtherDiscord", "app-1.0.9999", "Discord.exe"), "", 12));
            guard.ScanOnce(); Check(requests == 1, "An app update while locks are armed is still matched, without locking another installation");
            guard.Disarm(); guard.Arm(new[] { new InstalledApp("firefox", "Firefox", "firefox.exe") }, false); guard.ScanOnce();
            Check(requests == 1, "Rearming for different apps removes previous versioned-app rules");
        }
        string steam = Path.Combine(root, "Steam"); Directory.CreateDirectory(steam);
        string helper = Path.Combine(steam, "bin", "cef", "cef.win64", "steamwebhelper.exe"); Directory.CreateDirectory(Path.GetDirectoryName(helper)); File.WriteAllText(helper, "owned fixture");
        var steamApp = new InstalledApp("steam", "Steam", Path.Combine(steam, "steam.exe"));
        Check(AppGuardTargets.Executables(steamApp).Contains(helper), "Steam selection includes its separate steamwebhelper UI process");
        foreach (var standard in new[] { new InstalledApp("chrome", "Chrome", "chrome.exe"), new InstalledApp("firefox", "Firefox", "firefox.exe"), new InstalledApp("edge", "Edge", "msedge.exe"), new InstalledApp("vscode", "VS Code", "Code.exe"), new InstalledApp("vlc", "VLC", "vlc.exe"), new InstalledApp("7zip", "7-Zip", "7zFM.exe") })
        using (var guard = new AppGuardService(() => new List<GuardProcess> { new GuardProcess(21, standard.Name, standard.ExecutablePath, "", 21), new GuardProcess(22, "Unrelated", "unrelated.exe", "", 22) }, process => { }))
        { int calls = 0; guard.RequestAccess = process => { calls++; return true; }; guard.Arm(new[] { standard }, false); guard.ScanOnce(); Check(calls == 1, standard.Name + " executable matches without locking another app"); }
        foreach (string name in new[] { "Camera", "Teams", "WhatsApp", "Spotify" })
        {
            string id = "Test." + name + "_family!App";
            using (var guard = new AppGuardService(() => new List<GuardProcess> { new GuardProcess(30, name, "version-specific-path.exe", id, 30), new GuardProcess(31, "Other Store app", "other.exe", "Test.Other_family!App", 31) }, process => { }))
            { int calls = 0; guard.RequestAccess = process => { calls++; return true; }; guard.Arm(new[] { new InstalledApp(id, name) }, false); guard.ScanOnce(); Check(calls == 1, name + " Store app matches by identity without locking other Store apps"); }
        }
    }

    private static void BorderlessFitChecks()
    {
        foreach (int border in new[] { 0, 4, 7, 12, 16 })
        {
            var visible = new System.Drawing.Rectangle(-900, 50, 800, 600);
            var outer = System.Drawing.Rectangle.FromLTRB(visible.Left - border, visible.Top, visible.Right + border, visible.Bottom + border);
            var target = new System.Drawing.Rectangle(-950, 30, 900, 650);
            var fitted = AppWindows.FitVisibleFrame(target, outer, visible);
            Check(fitted.Left + border == target.Left && fitted.Right - border == target.Right && fitted.Top == target.Top && fitted.Bottom - border == target.Bottom, "Curtain fitting compensates " + border + "px invisible borders, including negative monitor coordinates");
        }
        string passwordPath = Path.Combine(output, "borderless-password.dat"); var passwords = new PasswordStore(passwordPath); passwords.SetPassword("a");
        using (var target = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--guard-target-borderless") { UseShellExecute = false }))
        {
            try
            {
                var wait = Stopwatch.StartNew(); while (target.MainWindowHandle == IntPtr.Zero && wait.ElapsedMilliseconds < 5000) { target.Refresh(); Pump(); System.Threading.Thread.Sleep(5); }
                IntPtr window = target.MainWindowHandle; Check(window != IntPtr.Zero, "Borderless League-style fixture opens");
                using (var overlay = new UtilityTool.Frontend.AppLockOverlay(new GuardProcess(target.Id, "Borderless fixture", target.MainModule.FileName, "", target.StartTime.ToUniversalTime().Ticks)))
                {
                    overlay.PromptAsync(passwords); Pump();
                    var curtain = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().Single();
                    System.Drawing.Rectangle targetFrame, curtainFrame;
                    AppWindows.DisplayBounds(window, curtain.Handle, out targetFrame); AppWindows.DisplayBounds(curtain.Handle, curtain.Handle, out curtainFrame);
                    Check(curtainFrame.Contains(targetFrame), "Curtain's visible opaque frame covers all edges of a borderless app");
                    int corners; Check(NativeDwmInteger(curtain.Handle, 33, out corners, 4) != 0 || corners == 1, "Curtain disables rounded outer corners that could reveal a square app underneath");
                    IntPtr moving = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NativeRect)));
                    try
                    {
                        var next = curtain.Bounds; next.Offset(80, 40); Marshal.StructureToPtr(new NativeRect { Left = next.Left, Top = next.Top, Right = next.Right, Bottom = next.Bottom }, moving, false);
                        SendMessage(curtain.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero); SendMessage(curtain.Handle, 0x0216, IntPtr.Zero, moving); curtain.Location = next.Location;
                        SendMessage(curtain.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero); Pump();
                        NativeRect raw; GetWindowRect(window, out raw); var expected = curtain.TargetLocation(curtain.Location);
                        Check(raw.Left == expected.X && raw.Top == expected.Y, "Dragging a fitted borderless curtain preserves its app offset without exposing edges");
                    }
                    finally { Marshal.FreeHGlobal(moving); }
                    foreach (int command in new[] { 0xF030, 0xF120 })
                    {
                        AppWindows.WindowCommand(window, command, IntPtr.Zero); wait.Restart(); while (wait.ElapsedMilliseconds < 200) { Pump(); System.Threading.Thread.Sleep(5); }
                        AppWindows.DisplayBounds(window, curtain.Handle, out targetFrame); AppWindows.DisplayBounds(curtain.Handle, curtain.Handle, out curtainFrame);
                        Check(curtainFrame.Contains(targetFrame), "Curtain covers the borderless app after " + (command == 0xF030 ? "maximize" : "restore"));
                    }
                    overlay.Cancel(); Pump(); Check(RegionType(window) != 1 && curtain.IsDisposed, "Unlocking releases a borderless curtain and restores the app's drawing surface");
                }
            }
            finally { if (!target.HasExited) { target.CloseMainWindow(); if (!target.WaitForExit(1500)) { target.Kill(); target.WaitForExit(); } } }
        }
        File.Delete(passwordPath);
    }
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int NativeDwmInteger(IntPtr window, int attribute, out int value, int size);
    private sealed class NoActivateFixture : System.Windows.Forms.Form
    { protected override bool ShowWithoutActivation { get { return true; } } }

    private static void BackgroundPromptChecks()
    {
        string path = Path.Combine(output, "background-password.dat"); var passwords = new PasswordStore(path); passwords.SetPassword("a");
        try
        {
            foreach (string mode in new[] { "--guard-target-delayed-background", "--guard-target-multi-background" })
            using (var other = new System.Windows.Forms.Form { Text = "Owned Chrome-style foreground fixture", Width = 900, Height = 720 })
            using (var target = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, mode) { UseShellExecute = false }))
            {
                try
                {
                    // Press Play, then switch to another app before the client opens.
                    other.Show(); other.Activate();
                    IntPtr foreground = other.Handle;
                    var wait = Stopwatch.StartNew();
                    int expected = mode == "--guard-target-multi-background" ? 2 : 1;
                    while (AppWindows.Find(target.Id).Count < expected && wait.ElapsedMilliseconds < 5000) { Pump(); System.Threading.Thread.Sleep(5); }
                    other.BringToFront(); NativeFixtureFocus(other.Handle); Pump();
                    foreach (IntPtr window in AppWindows.Find(target.Id).Keys) AppWindows.PlaceBelowWindow(window, other.Handle);
                    Check(AppWindows.Find(target.Id).Count == expected,
                        "Delayed protected app opens behind the Chrome-style fixture: " + mode);
                    // The sandbox cannot become the OS foreground application.
                    // Supply its foreground snapshots; keep real child-process
                    // windows, native z-order, controls and password verification.
                    using (var overlay = new UtilityTool.Frontend.AppLockOverlay(new GuardProcess(target.Id, "Background fixture", target.MainModule.FileName, "", target.StartTime.ToUniversalTime().Ticks), () => foreground))
                    {
                        var result = overlay.PromptAsync(passwords);
                        wait.Restart(); while (wait.ElapsedMilliseconds < 100) { Pump(); System.Threading.Thread.Sleep(5); }
                        var dialog = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.PasswordDialog>().Single();
                        var curtains = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().ToList();
                        var input = (System.Windows.Forms.TextBox)typeof(UtilityTool.Frontend.PasswordDialog).GetField("password", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(dialog);
                        Check(dialog.Visible && NativeWindowVisible(dialog.Handle) && curtains.Count == expected && curtains.Contains((UtilityTool.Frontend.LockCoverWindow)dialog.Owner),
                            "Password prompt is already attached and visible on a client that opened in the background");
                        Check(!input.Focused && !dialog.TopMost && curtains.All(cover => !cover.TopMost) && NativeAbove(other.Handle, dialog.Handle),
                            "Background prompt stays below Chrome and leaves Chrome's input focus unchanged");
                        // Stop the backup timer to prove own-window activation events
                        // recover the prompt immediately, rather than after a scan.
                        ((System.Windows.Forms.Timer)typeof(UtilityTool.Frontend.AppLockOverlay).GetField("timer", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(overlay)).Stop();
                        var watcher = (WindowEventWatcher)typeof(UtilityTool.Frontend.AppLockOverlay).GetField("windowEvents", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(overlay);
                        Check(watcher.Active && watcher.WatchesOwnForeground, "Overlay registers foreground notifications for its own curtain and password windows");
                        foreach (var curtain in curtains.OrderBy(cover => cover == dialog.Owner ? 1 : 0))
                        {
                            foreground = other.Handle; other.BringToFront(); NativeFixtureFocus(other.Handle); NotifyFixtureForeground(watcher, foreground); Pump();
                            curtain.BringToFront(); foreground = curtain.Handle; NativeFixtureFocus(curtain.Handle); NotifyFixtureForeground(watcher, foreground);
                            wait.Restart(); while ((dialog.Owner != curtain || !input.Focused) && wait.ElapsedMilliseconds < 800) { Pump(); System.Threading.Thread.Sleep(5); }
                            Check(dialog.Visible && dialog.Owner == curtain && input.Focused && NativeAbove(dialog.Handle, curtain.Handle) && NativeAbove(dialog.Handle, other.Handle),
                                "Returning to each protected curtain brings its attached password field into focus without backup polling");
                        }
                        SendMessage(input.Handle, 0x0102, new IntPtr('a'), IntPtr.Zero);
                        foreground = other.Handle; other.BringToFront(); NativeFixtureFocus(other.Handle); NotifyFixtureForeground(watcher, foreground); Pump();
                        var activeCover = (UtilityTool.Frontend.LockCoverWindow)dialog.Owner;
                        activeCover.BringToFront(); foreground = activeCover.Handle; NativeFixtureFocus(activeCover.Handle); NotifyFixtureForeground(watcher, foreground);
                        wait.Restart(); while (!input.Focused && wait.ElapsedMilliseconds < 800) { Pump(); System.Threading.Thread.Sleep(5); }
                        Check(input.Text == "a" && input.Focused, "Switching away and back preserves the typed password and restores typing focus");
                        // Hide the prompt to reproduce the previous missing-dialog
                        // symptom, then click the curtain's client area to recover it.
                        dialog.Hide(); activeCover.BringToFront(); NativeFixtureFocus(activeCover.Handle); Pump();
                        SendMessage(activeCover.Handle, 0x0201, new IntPtr(1), Coordinates(activeCover.ClientSize.Width / 2, activeCover.ClientSize.Height / 2));
                        SendMessage(activeCover.Handle, 0x0202, IntPtr.Zero, Coordinates(activeCover.ClientSize.Width / 2, activeCover.ClientSize.Height / 2)); Pump();
                        Check(dialog.Visible && input.Focused, "A curtain click recovers a missing password prompt and focuses the field");
                        SendMessage(input.Handle, 0x0100, new IntPtr(13), IntPtr.Zero); SendMessage(input.Handle, 0x0101, new IntPtr(13), IntPtr.Zero);
                        wait.Restart(); while (!result.IsCompleted && wait.ElapsedMilliseconds < 8000) { Pump(); System.Threading.Thread.Sleep(5); }
                        Check(result.IsCompleted && result.Result, "Enter unlocks after background launch and repeated app switching");
                    }
                }
                finally { if (!target.HasExited) { target.CloseMainWindow(); if (!target.WaitForExit(1500)) { target.Kill(); target.WaitForExit(); } } }
            }
        }
        finally { File.Delete(path); }
    }
    private static bool NativeAbove(IntPtr first, IntPtr second)
    { for (IntPtr current = NativeWindowOwner(second, 3); current != IntPtr.Zero; current = NativeWindowOwner(current, 3)) if (current == first) return true; return false; }
    private static void NotifyFixtureForeground(WindowEventWatcher watcher, IntPtr window)
    { typeof(WindowEventWatcher).GetMethod("OnEvent", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(watcher, new object[] { IntPtr.Zero, WindowEventWatcher.Foreground, window, 0, 0, 0u, 0u }); }
    [DllImport("user32.dll", EntryPoint = "SetFocus")] private static extern IntPtr NativeFixtureFocus(IntPtr window);

    private static void OverlayCloseChecks()
    {
        string path = Path.Combine(output, "close-test-password.dat");
        var store = new PasswordStore(path); store.SetPassword("a");
        try
        {
            foreach (string mode in new[] { "--guard-target-slow-close", "--guard-target-refuse-close" })
            using (var target = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, mode) { UseShellExecute = false }))
            {
                try
                {
                    var wait = Stopwatch.StartNew();
                    while (target.MainWindowHandle == IntPtr.Zero && wait.ElapsedMilliseconds < 5000)
                    { target.Refresh(); Pump(); System.Threading.Thread.Sleep(10); }
                    IntPtr window = target.MainWindowHandle;
                    Check(window != IntPtr.Zero, "Owned X-close fixture opens: " + mode);
                    using (var overlay = new UtilityTool.Frontend.AppLockOverlay(new GuardProcess(target.Id, "Close fixture", target.MainModule.FileName, "", target.StartTime.ToUniversalTime().Ticks)))
                    {
                        var result = overlay.PromptAsync(store, () => true);
                        typeof(UtilityTool.Frontend.AppLockOverlay).GetMethod("ShowPrompt", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(overlay, new object[] { window, true }); Pump();
                        var cover = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.LockCoverWindow>().Single();
                        var dialog = System.Windows.Forms.Application.OpenForms.Cast<System.Windows.Forms.Form>().OfType<UtilityTool.Frontend.PasswordDialog>().Single();
                        SendMessage(cover.Handle, 0x0112, new IntPtr(0xF060), IntPtr.Zero);
                        Check(cover.ClosePending && NativeWindowOwner(cover.Handle, 4) == IntPtr.Zero && !cover.TopMost,
                            "X detaches the curtain before asking the protected app to close, without making it topmost");
                        if (mode == "--guard-target-slow-close")
                        {
                            wait.Restart();
                            while (NativeWindowVisible(window) && wait.ElapsedMilliseconds < 700) { Pump(); System.Threading.Thread.Sleep(5); }
                            Check(AppWindows.Exists(window) && !NativeWindowVisible(window), "Close fixture hides its app window while shutdown work is still pending");
                            bool retained = true; int samples = 0;
                            wait.Restart();
                            while (wait.ElapsedMilliseconds < 450)
                            {
                                Pump(); samples++;
                                retained &= AppWindows.Exists(window) && cover.Visible && NativeWindowVisible(cover.Handle) && dialog.Visible && NativeWindowVisible(dialog.Handle) && !result.IsCompleted;
                                System.Threading.Thread.Sleep(5);
                            }
                            Check(retained && samples > 20, "Curtain and password interface stay visible throughout delayed X-button shutdown");
                            wait.Restart();
                            while (AppWindows.Exists(window) && wait.ElapsedMilliseconds < 2000) { Pump(); System.Threading.Thread.Sleep(5); }
                            Pump();
                            Check(!AppWindows.Exists(window) && !target.HasExited && cover.IsDisposed && !dialog.Visible,
                                "Curtain is released only after its protected window is destroyed, even if the app retains a background process");
                            wait.Restart();
                            while (!result.IsCompleted && wait.ElapsedMilliseconds < 2500) { Pump(); System.Threading.Thread.Sleep(5); }
                            Check(result.IsCompleted && target.HasExited && dialog.IsDisposed, "Normal process exit completes access and cleans up the remaining prompt");
                        }
                        else
                        {
                            wait.Restart(); while (wait.ElapsedMilliseconds < 150) { Pump(); System.Threading.Thread.Sleep(5); }
                            Check(AppWindows.Exists(window) && cover.Visible && dialog.Visible && !result.IsCompleted,
                                "An app that refuses X-close remains locked with its password interface usable");
                            SendMessage(cover.Handle, 0x0112, new IntPtr(0xF020), IntPtr.Zero);
                            wait.Restart(); while (NativeWindowVisible(cover.Handle) && wait.ElapsedMilliseconds < 700) { Pump(); System.Threading.Thread.Sleep(5); }
                            Check(!cover.ClosePending && NativeWindowOwner(cover.Handle, 4) == window && !NativeWindowVisible(cover.Handle),
                                "Minimize after a refused close restores normal app ownership and hides the curtain");
                            AppWindows.WindowCommand(window, 0xF120, IntPtr.Zero);
                            wait.Restart(); while (!cover.Visible && wait.ElapsedMilliseconds < 1500) { Pump(); System.Threading.Thread.Sleep(5); }
                            Check(cover.Visible, "Restoring the protected app restores its curtain after a refused close");
                            overlay.Cancel(); Pump();
                            Check(cover.IsDisposed && dialog.IsDisposed && AppWindows.Exists(window), "Disarming after a refused close removes the interface without closing the app");
                        }
                    }
                }
                finally { if (!target.HasExited) { target.Kill(); target.WaitForExit(); } } // owned fixture only
            }
        }
        finally { File.Delete(path); }
    }
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool NativeWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "GetWindow")] private static extern IntPtr NativeWindowOwner(IntPtr window, uint command);
    private static IEnumerable<T> Children<T>(DependencyObject value) where T : DependencyObject
    { if (value is T) yield return (T)value; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) foreach (var child in Children<T>(VisualTreeHelper.GetChild(value, i))) yield return child; }
    private static void Capture(Window window, string name)
    { window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(output, name + ".png"))) encoder.Save(file); }
}
