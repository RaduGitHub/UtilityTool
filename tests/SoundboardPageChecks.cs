using System;
using System.Collections.Generic;
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

internal static class SoundboardPageChecks
{
    internal static void Run(Action<bool, string> check, string output, Window owner, SoundboardSound source, Action pump, Action<Window, string> capture)
    {
        string root = Path.Combine(output, "soundboard-page-" + Guid.NewGuid().ToString("N"));
        var store = new SoundboardStore(Path.Combine(root, "library")); var sound = store.Import("Menu sound", source.AudioPath, source.ImagePath);
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var page = new SoundboardPage(owner, false, store, FakeAudio(), () => { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException(); return Outputs(); }, callDevices: () => new SoundboardDevices()))
        {
            var window = Host(owner, page); var initialization = page.InitializeAsync(); WaitFor(() => entered.IsSet, pump);
            Wait(page.LoadSounds(), pump);
            check(!initialization.IsCompleted && Visuals<ToggleButton>(page).Any(button => button.DataContext is TileItem), "Sound tiles appear while slow audio-device startup is still pending");
            capture(window, "Soundboard-audio-initializing"); release.Set(); Wait(initialization, pump); window.Close();
        }
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var preferences = new SoundboardSettingsStore(Path.Combine(root, "opening-audio-settings.xml"));
            preferences.Save(new SoundboardSettings { MicrophoneId = "mic", VirtualOutputId = "cable", CallAudioEnabled = true });
            var devices = new SoundboardDevices(); devices.Microphones.Add(new SoundboardDevice("mic", "Test mic")); devices.VirtualOutputs.Add(new SoundboardDevice("cable", "CABLE Input"));
            using (var page = new SoundboardPage(owner, false, store, new SoundboardAudioService((mic, cable, listening) => { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException(); return new Session(); }), Outputs, preferences, () => devices))
            {
                var window = Host(owner, page); var initialization = page.InitializeAsync(); WaitFor(() => entered.IsSet, pump);
                Wait(page.LoadSounds(), pump);
                check(!initialization.IsCompleted && Visuals<ToggleButton>(page).Any(button => button.DataContext is TileItem), "Opening a slow audio session cannot block sound-grid rendering on the UI thread");
                release.Set(); Wait(initialization, pump); window.Close();
            }
        }
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var page = new SoundboardPage(owner, false, store, FakeAudio(), Outputs, callDevices: () => new SoundboardDevices(), libraryLoader: () => { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException(); return store.Load(); }))
        {
            var window = Host(owner, page); var libraryLoad = page.LoadSounds(); WaitFor(() => entered.IsSet, pump);
            var loading = Visuals<StackPanel>(page).Single(panel => System.Windows.Automation.AutomationProperties.GetName(panel) == "Soundboard loading");
            check(loading.Visibility == Visibility.Visible && Visuals<ProgressBar>(loading).Single().IsIndeterminate, "Slow library reads show a visible Loading sounds indicator immediately");
            capture(window, "Soundboard-loading"); release.Set(); Wait(libraryLoad, pump); Wait(page.InitializeAsync(), pump);
            check(loading.Visibility == Visibility.Collapsed && Visuals<ToggleButton>(page).Any(button => button.DataContext is TileItem), "Completing library loading replaces its indicator with sound tiles"); window.Close();
        }
        var session = new Session();
        using (var page = new SoundboardPage(owner, false, store, new SoundboardAudioService((mic, cable, listening) => session), Outputs, callDevices: () => new SoundboardDevices()))
        {
            var window = Host(owner, page); Wait(page.LoadSounds(), pump); Wait(page.InitializeAsync(), pump);
            var tile = Visuals<ToggleButton>(page).Single(button => button.DataContext is TileItem); var item = (TileItem)tile.DataContext;
            WaitFor(() => source.ImagePath == null || item.Icon != null, pump); var firstIcon = item.Icon;
            Wait(page.LoadSounds(), pump);
            tile = Visuals<ToggleButton>(page).Single(button => button.DataContext is TileItem); item = (TileItem)tile.DataContext;
            check(source.ImagePath == null || ReferenceEquals(firstIcon, item.Icon), "Reloading unchanged sounds reuses cached frozen artwork without decoding again");
            tile.Focus(); pump();
            var options = Visuals<Button>(page).Single(button => ReferenceEquals(button.Command, item.OptionsCommand));
            check(options.Visibility == Visibility.Visible && !(VisualTreeHelper.GetParent(options) is ToggleButton), "Sound options appear on tile focus as a sibling button outside the playback toggle");
            capture(window, "Soundboard-options");
            string catalog = File.ReadAllText(Path.Combine(store.Root, "sounds.xml")); int played = session.Played;
            OpenEditor(options, pump, editor =>
            {
                check(editor.Title == "Edit sound" && Fields(editor).Single(field => FieldName(field) == "Sound name").Text == sound.Name, "The options button opens the existing sound in a prefilled editor");
                check(session.Played == played && !tile.IsChecked.GetValueOrDefault(), "Opening sound options neither plays the MP3 nor toggles the tile");
                capture(editor, "Soundboard-edit"); Fields(editor).Single(field => FieldName(field) == "Sound name").Text = "Cancelled name";
                editor.Close();
            });
            check(File.ReadAllText(Path.Combine(store.Root, "sounds.xml")) == catalog && File.Exists(sound.AudioPath), "Cancelling the sound editor preserves the library and its files");
            string replacement = Path.Combine(root, "replacement.mp3"); File.Copy(sound.AudioPath, replacement);
            string artwork = Path.Combine(root, "replacement.png");
            using (var image = new System.Drawing.Bitmap(20, 20)) { using (var graphics = System.Drawing.Graphics.FromImage(image)) graphics.Clear(System.Drawing.Color.Orange); image.Save(artwork, System.Drawing.Imaging.ImageFormat.Png); }
            OpenEditor(options, pump, editor =>
            {
                Fields(editor).Single(field => FieldName(field) == "Sound name").Text = "Updated sound";
                Fields(editor).Single(field => FieldName(field) == "MP3 file").Text = replacement;
                Fields(editor).Single(field => FieldName(field) == "Sound image").Text = artwork;
                Visuals<Wpf.Ui.Controls.Button>(editor).Single(button => (string)button.Content == "Save changes").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            });
            WaitFor(() => Visuals<ToggleButton>(page).Any(button => button.DataContext is TileItem && ((TileItem)button.DataContext).Name == "Updated sound"), pump);
            var updated = store.Load().Single();
            check(updated.Id == sound.Id && updated.Name == "Updated sound" && updated.AudioPath != sound.AudioPath && updated.ImagePath != sound.ImagePath, "Saving from the editor refreshes the tile while retaining its sound identity");
            check(!File.Exists(sound.AudioPath) && (sound.ImagePath == null || !File.Exists(sound.ImagePath)) && File.Exists(replacement) && File.Exists(artwork), "Editor replacements remove old library copies while retaining the user-selected source files");
            check(session.Played == played, "Editing and saving a sound never invokes its playback command"); window.Close();
        }
    }
    private static List<SoundboardDevice> Outputs() { return new List<SoundboardDevice> { new SoundboardDevice(null, "Test speakers") }; }
    private static SoundboardAudioService FakeAudio() { return new SoundboardAudioService((mic, cable, listening) => new Session()); }
    private static Window Host(Window owner, SoundboardPage page) { var window = new Window { Owner = owner, Width = 1040, Height = 740, Content = page, Background = owner.Background, Foreground = Brushes.WhiteSmoke }; window.Show(); return window; }
    private static IEnumerable<TextBox> Fields(Window editor) { return Visuals<TextBox>(editor); }
    private static string FieldName(TextBox field) { return System.Windows.Automation.AutomationProperties.GetName(field); }
    private static void OpenEditor(Button options, Action pump, Action<CreateSoundWindow> action)
    {
        CreateSoundWindow editor = null; Exception failure = null; bool handled = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += delegate
        {
            var current = Application.Current.Windows.OfType<CreateSoundWindow>().FirstOrDefault(); if (current == null) return;
            timer.Stop(); editor = current; handled = true;
            try { action(current); } catch (Exception error) { failure = error; current.Close(); }
        };
        timer.Start(); typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(options, null);
        try { WaitFor(() => handled && editor != null && !editor.IsVisible, pump); if (failure != null) throw failure; }
        finally { timer.Stop(); }
    }
    private static void Wait(Task task, Action pump) { WaitFor(() => task.IsCompleted, pump); task.GetAwaiter().GetResult(); }
    private static void WaitFor(Func<bool> done, Action pump) { var timer = System.Diagnostics.Stopwatch.StartNew(); while (!done() && timer.ElapsedMilliseconds < 10000) { pump(); Thread.Sleep(5); } if (!done()) throw new TimeoutException("Soundboard page check timed out."); }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject { if (root is T) yield return (T)root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var item in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return item; }
    private sealed class Session : ISoundboardSession
    {
        internal int Played;
        public string LocalOutputName { get { return "Test speakers"; } }
        public float LocalPeak { get { return 0; } }
        public event Action<string> Failed { add { } remove { } }
        public event Action<int> PlaybackFinished { add { } remove { } }
        public void Play(string path, int id) { Played++; }
        public void TestLocalOutput(int id) { }
        public void StopClips() { }
        public void SetVolume(float value) { }
        public void Dispose() { }
    }
}
