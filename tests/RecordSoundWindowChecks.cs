using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.Wave;
using UtilityTool.Backend;
using UtilityTool.Frontend.Wpf;

internal static class RecordSoundWindowChecks
{
    internal static void Run(Action<bool, string> check, string output, FluentMainWindow owner, Action pump, Action<Window, string> capture)
    {
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        try { RunCore(check, output, owner, pump, capture); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunCore(Action<bool, string> check, string output, Window owner, Action pump, Action<Window, string> capture)
    {
        string root = Path.Combine(Path.GetFullPath(output), "recording-window-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        OpeningCheck(check, root, owner, pump, capture);
        SaveCheck(check, root, owner, pump, capture);
        CancelCaptureCheck(check, root, owner, pump);
        ClosePreviewCheck(check, root, owner, pump);
        CancelDeviceLoadingCheck(check, root, owner, pump);
        CancelStartupCheck(check, root, owner, pump);
        AutomaticStopCheck(check, root, owner, pump);
        ImmediateStopCheck(check, root, owner, pump);
        LeadingSilenceReplayCheck(check, root, owner, pump);
    }
    private static void OpeningCheck(Action<bool, string> check, string root, Window owner, Action pump, Action<Window, string> capture)
    {
        int starts = 0, reads = 0; string temporary = Path.Combine(root, "opening"); var store = new SoundboardStore(Path.Combine(root, "opening-library")); var preview = new SilentPreview();
        var recorder = new SoundboardRecorder((source, id) => { Interlocked.Increment(ref starts); return new GeneratedCapture(); }, temporary);
        var window = new RecordSoundWindow(owner, store, preferredMicrophoneId: "mic-preferred", recorder: recorder, preview: preview, readDevices: source => { Interlocked.Increment(ref reads); return Devices(source); });
        bool? result = Show(window, pump, () =>
        {
            WaitFor(() => RecordButton(window).IsEnabled, pump);
            check(starts == 0 && !recorder.IsRecording && !Directory.Exists(temporary) && reads == 1, "Opening the recording dialog enumerates metadata without creating capture or raw recording files");
            check(!Button(window, "Preview").IsEnabled && !Button(window, "Save sound").IsEnabled && !Button(window, "Stop recording").IsEnabled, "Preview, save and stop remain disabled before a recording exists");
            check(Named<CheckBox>(window, "Reduce recording image size").IsChecked != true, "The recording dialog exposes optional artwork reduction without recompressing its recorded MP3");
            Named<ComboBox>(window, "Recording source").SelectedIndex = 1;
            WaitFor(() => RecordButton(window).IsEnabled && ((SoundboardDevice)Named<ComboBox>(window, "Recording device").SelectedItem).Id == "mic-preferred", pump);
            check(starts == 0 && reads == 2 && !Directory.Exists(temporary), "Switching to the preferred microphone only refreshes device metadata and does not begin capture");
            capture(window, "Soundboard-record-empty"); Click(Button(window, "Cancel")); WaitFor(() => !window.IsVisible, pump);
        });
        check(result != true && starts == 0 && preview.Disposed && recorder.DisposalCompleted.IsCompleted && !Directory.Exists(store.Root), "Cancelling an unopened recording creates no library and disposes the idle recorder and local preview");
    }
    private static void SaveCheck(Action<bool, string> check, string root, Window owner, Action pump, Action<Window, string> capture)
    {
        string temporary = Path.Combine(root, "save-recording"), artwork = Path.Combine(root, "recorded-artwork.png");
        using (var bitmap = new System.Drawing.Bitmap(32, 32)) { using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.Clear(System.Drawing.Color.Coral); bitmap.Save(artwork, System.Drawing.Imaging.ImageFormat.Png); }
        byte[] artworkBytes = File.ReadAllBytes(artwork); var store = new SoundboardStore(Path.Combine(root, "saved-library")); var preview = new SilentPreview(); var captureSource = new GeneratedCapture();
        SoundboardRecordingSource usedSource = SoundboardRecordingSource.Microphone; string usedDevice = null, wave = null, previewPath = null;
        var recorder = new SoundboardRecorder((source, id) => { usedSource = source; usedDevice = id; return captureSource; }, temporary);
        var window = new RecordSoundWindow(owner, store, "listen-headphones", 0.31f, recorder: recorder, preview: preview, readDevices: Devices);
        bool? result = Show(window, pump, () =>
        {
            WaitFor(() => RecordButton(window).IsEnabled, pump); Named<ComboBox>(window, "Recording device").SelectedIndex = 1; Click(RecordButton(window));
            WaitFor(() => recorder.IsRecording && Button(window, "Stop recording").IsEnabled, pump); wave = recorder.WavePath;
            check(captureSource.Starts == 1 && usedSource == SoundboardRecordingSource.ComputerAudio && usedDevice == "pc-secondary" && File.Exists(wave), "Record starts only the explicitly selected PC audio source and streams a temporary WAV");
            check(!Named<ComboBox>(window, "Recording source").IsEnabled && !Named<ComboBox>(window, "Recording device").IsEnabled && !Button(window, "Preview").IsEnabled, "Source changes and playback are disabled while capture is active");
            capture(window, "Soundboard-record-capturing"); Click(Button(window, "Stop recording")); WaitFor(() => !recorder.IsRecording && Button(window, "Preview").IsEnabled, pump);
            check(captureSource.Disposed && captureSource.Stops > 0 && SoundboardRecordingExport.GetDuration(wave).TotalSeconds >= 1.2 - 1.0 / 48000, "Stop recording finalizes the raw WAV and releases capture before enabling trim and preview");
            var start = Named<Slider>(window, "Trim start seconds"); var end = Named<Slider>(window, "Trim end seconds");
            start.Value = 0.9; end.Value = 0.8;
            check(Math.Abs(start.Value - end.Value) < 0.000001 && !Button(window, "Preview").IsEnabled && !Button(window, "Save sound").IsEnabled, "Crossing trim handles keeps their order and disables zero-length preview and save");
            start.Value = 0.2; end.Value = 0.8; Named<TextBox>(window, "Recording sound name").Text = "  UI recorded sound  "; Named<TextBox>(window, "Recording image").Text = artwork;
            check(Button(window, "Save sound").IsEnabled && Visuals<TextBlock>(window).Any(text => text.Text.Contains("Selected duration") && text.Text.Contains("00:00.60")), "A valid selected interval shows its duration and enables saving with a custom name and artwork");
            Click(Button(window, "Preview")); WaitFor(() => preview.Plays == 1 && Button(window, "Stop preview").IsEnabled, pump); previewPath = preview.Path;
            check(preview.Output == "listen-headphones" && Math.Abs(preview.Volume - 0.31f) < 0.000001f && Math.Abs(preview.Duration - 0.6) < 0.001 && preview.Peak > 0.01f && File.Exists(previewPath), "Preview reads only the selected part into the chosen local output and volume through the injected silent player");
            using (var cachedWave = new WaveFileReader(previewPath)) check(cachedWave.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat && cachedWave.WaveFormat.SampleRate == 48000 && cachedWave.WaveFormat.Channels == 2 && System.IO.Path.GetExtension(previewPath) == ".wav", "Recording preview uses a standard 48 kHz stereo float WAV and requires no MP3 encoding or decoding");
            var replay = Button(window, "Preview"); replay.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); replay.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); PumpFor(100, pump);
            check(!replay.IsEnabled && preview.Plays == 1 && preview.Path == previewPath && !Named<Slider>(window, "Trim start seconds").IsEnabled && !Named<Slider>(window, "Trim end seconds").IsEnabled, "Rapid preview events cannot restart active playback, and trim controls wait until preview stops");
            check(!Directory.Exists(store.Root), "Previewing a recording does not create a saved library entry");
            Click(Button(window, "Stop preview")); WaitFor(() => !preview.IsPlaying && Button(window, "Preview").IsEnabled, pump);
            check(preview.Plays == 1 && !Button(window, "Stop preview").IsEnabled, "Stop preview ends local playback without starting another capture or sound");
            DateTime cachedWrite = File.GetLastWriteTimeUtc(previewPath); Click(Button(window, "Preview")); WaitFor(() => preview.Plays == 2 && Button(window, "Stop preview").IsEnabled, pump);
            check(preview.Path == previewPath && File.GetLastWriteTimeUtc(previewPath) == cachedWrite && Math.Abs(preview.Duration - 0.6) < 0.001, "Stopping and replaying an unchanged range reuses its cached WAV without regenerating or encoding it");
            Click(Button(window, "Stop preview")); WaitFor(() => Button(window, "Preview").IsEnabled, pump);
            start.Value = 0.3; end.Value = 0.7; Click(Button(window, "Preview")); WaitFor(() => preview.Plays == 3 && Button(window, "Stop preview").IsEnabled, pump);
            check(!File.Exists(previewPath) && File.Exists(preview.Path) && Math.Abs(preview.Duration - 0.4) < 0.001, "Previewing an adjusted trim removes the previous temporary WAV and plays the updated selected duration");
            previewPath = preview.Path; capture(window, "Soundboard-record-trimmed");
            preview.Complete(); WaitFor(() => Button(window, "Preview").IsEnabled && StatusContains(window, "Preview finished"), pump);
            check(!Button(window, "Stop preview").IsEnabled && StatusContains(window, "Preview finished"), "Natural preview EOF restores the preview button and shows an accurate finished status");
            // Save stops the player once before import; Cleanup calls it again.
            // A disconnect at cleanup must not turn a committed save into a retry.
            preview.ThrowOnStop = preview.Stops + 2; Click(Button(window, "Save sound"));
            WaitFor(() => !window.IsVisible || StatusContains(window, "Unable to save"), pump);
            if (window.IsVisible) throw new Exception("Record dialog failed to finish saving: " + StatusText(window));
        });
        var saved = store.Load().Single();
        check(result == true && window.CreatedSound != null && saved.Id == window.CreatedSound.Id && saved.Name == "UI recorded sound" && saved.ImagePath != null && File.ReadAllBytes(saved.ImagePath).SequenceEqual(artworkBytes), "Saving the recording creates one normal soundboard sound with its trimmed MP3, custom name and copied artwork");
        using (var decoded = new MediaFoundationReader(saved.AudioPath)) check(Math.Abs(decoded.TotalTime.TotalSeconds - 0.4) < 0.075, "The saved MP3 contains the selected trim rather than the whole recording");
        check(preview.Disposed && !preview.IsPlaying && captureSource.Disposed && !File.Exists(wave) && !File.Exists(previewPath) && recorder.DisposalCompleted.IsCompleted && File.ReadAllBytes(artwork).SequenceEqual(artworkBytes), "Successful save removes raw WAV and preview files, disposes resources despite a cleanup stop error, and retains original artwork");
    }
    private static void CancelCaptureCheck(Action<bool, string> check, string root, Window owner, Action pump)
    {
        var store = new SoundboardStore(Path.Combine(root, "cancel-library")); var preview = new SilentPreview(); var source = new GeneratedCapture(); string wave = null;
        var recorder = new SoundboardRecorder((kind, id) => source, Path.Combine(root, "cancel-capture")); var window = new RecordSoundWindow(owner, store, recorder: recorder, preview: preview, readDevices: Devices);
        Show(window, pump, () =>
        {
            WaitFor(() => RecordButton(window).IsEnabled, pump); Click(RecordButton(window)); WaitFor(() => recorder.IsRecording && Button(window, "Stop recording").IsEnabled, pump); wave = recorder.WavePath;
            preview.ThrowOnStop = preview.Stops + 1; Click(Button(window, "Cancel")); WaitFor(() => !window.IsVisible, pump);
        });
        check(source.Disposed && source.Stops > 0 && preview.Disposed && !recorder.IsRecording && !File.Exists(wave) && !Directory.Exists(store.Root), "Cancel during capture still releases the source and removes its WAV when local-preview stopping reports an error");
    }
    private static void ClosePreviewCheck(Action<bool, string> check, string root, Window owner, Action pump)
    {
        var store = new SoundboardStore(Path.Combine(root, "close-library")); var preview = new SilentPreview(); var source = new GeneratedCapture(); string wave = null, mp3 = null, device = null;
        var recorder = new SoundboardRecorder((kind, id) => { if (kind != SoundboardRecordingSource.Microphone) throw new Exception("Expected the selected microphone."); device = id; return source; }, Path.Combine(root, "close-preview"));
        var window = new RecordSoundWindow(owner, store, preferredMicrophoneId: "mic-preferred", recorder: recorder, preview: preview, readDevices: Devices);
        Show(window, pump, () =>
        {
            WaitFor(() => RecordButton(window).IsEnabled, pump); Named<ComboBox>(window, "Recording source").SelectedIndex = 1;
            WaitFor(() => RecordButton(window).IsEnabled && ((SoundboardDevice)Named<ComboBox>(window, "Recording device").SelectedItem).Id == "mic-preferred", pump);
            Click(RecordButton(window)); WaitFor(() => recorder.IsRecording && Button(window, "Stop recording").IsEnabled, pump); wave = recorder.WavePath;
            Click(Button(window, "Stop recording")); WaitFor(() => Button(window, "Preview").IsEnabled, pump); Click(Button(window, "Preview"));
            WaitFor(() => preview.IsPlaying && Button(window, "Stop preview").IsEnabled, pump); mp3 = preview.Path; window.Close(); WaitFor(() => !window.IsVisible, pump);
        });
        check(device == "mic-preferred" && source.Starts == 1 && preview.Plays == 1, "A microphone take captures the preferred selected device only after Record and previews without reopening capture");
        check(preview.Disposed && !preview.IsPlaying && source.Disposed && !File.Exists(wave) && !File.Exists(mp3) && !Directory.Exists(store.Root), "Closing with X while a preview is active stops it and removes all unsaved recording files");
    }
    private static void CancelDeviceLoadingCheck(Action<bool, string> check, string root, Window owner, Action pump)
    {
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
        {
            int starts = 0; var store = new SoundboardStore(Path.Combine(root, "loading-library")); var preview = new SilentPreview(); string temporary = Path.Combine(root, "loading-capture");
            var recorder = new SoundboardRecorder((kind, id) => { Interlocked.Increment(ref starts); return new GeneratedCapture(); }, temporary);
            var window = new RecordSoundWindow(owner, store, recorder: recorder, preview: preview, readDevices: kind => { entered.Set(); if (!release.Wait(10000)) throw new TimeoutException("Device fixture was not released."); return Devices(kind); });
            Show(window, pump, () =>
            {
                try { WaitFor(() => entered.IsSet, pump); Click(Button(window, "Cancel")); check(!RecordButton(window).IsEnabled && starts == 0, "Cancelling during delayed device enumeration disables recording without opening capture"); }
                finally { release.Set(); }
                WaitFor(() => !window.IsVisible, pump);
            });
            check(starts == 0 && preview.Disposed && !Directory.Exists(temporary) && !Directory.Exists(store.Root), "Cancelling device enumeration waits for its task and disposes the dialog without creating recording or library files");
        }
    }
    private static void CancelStartupCheck(Action<bool, string> check, string root, Window owner, Action pump)
    {
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
        {
            var source = new GeneratedCapture { StartEntered = entered, AllowStart = release }; var preview = new SilentPreview(); var store = new SoundboardStore(Path.Combine(root, "startup-library"));
            string temporary = Path.Combine(root, "startup-capture"); var recorder = new SoundboardRecorder((kind, id) => source, temporary); var window = new RecordSoundWindow(owner, store, recorder: recorder, preview: preview, readDevices: Devices);
            Show(window, pump, () =>
            {
                try { WaitFor(() => RecordButton(window).IsEnabled, pump); Click(RecordButton(window)); WaitFor(() => entered.IsSet, pump); Click(Button(window, "Cancel")); check(!RecordButton(window).IsEnabled, "Cancel remains available during delayed capture startup and blocks further recording actions"); }
                finally { release.Set(); }
                WaitFor(() => !window.IsVisible, pump);
            });
            check(source.Disposed && source.Stops > 0 && !recorder.IsRecording && !recorder.HasRecording && preview.Disposed && (!Directory.Exists(temporary) || !Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories).Any()) && !Directory.Exists(store.Root), "Cancelling queued capture startup waits for completion, stops the source and leaves no raw or saved audio files");
        }
    }
    private static void AutomaticStopCheck(Action<bool, string> check, string root, Window owner, Action pump)
    {
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
        {
            var source = new GeneratedCapture { DisposeEntered = entered, AllowDispose = release }; var preview = new SilentPreview(); var store = new SoundboardStore(Path.Combine(root, "auto-stop-library"));
            var recorder = new SoundboardRecorder((kind, id) => source, Path.Combine(root, "auto-stop-capture")); var window = new RecordSoundWindow(owner, store, recorder: recorder, preview: preview, readDevices: Devices);
            Show(window, pump, () =>
            {
                try
                {
                    WaitFor(() => RecordButton(window).IsEnabled, pump); Click(RecordButton(window)); WaitFor(() => recorder.IsRecording && Button(window, "Stop recording").IsEnabled, pump);
                    source.End(); WaitFor(() => entered.IsSet, pump); PumpFor(220, pump);
                    check(!Button(window, "Preview").IsEnabled && !Button(window, "Save sound").IsEnabled, "An automatic device stop keeps preview and saving disabled until the capture worker closes its WAV");
                }
                finally { release.Set(); }
                WaitFor(() => Button(window, "Preview").IsEnabled, pump);
                check(source.Disposed && SoundboardRecordingExport.GetDuration(recorder.WavePath) > TimeSpan.Zero && Named<Slider>(window, "Trim end seconds").Value > 0, "Automatic capture completion finalizes the recording and initializes a usable full trim");
                Click(Button(window, "Cancel")); WaitFor(() => !window.IsVisible, pump);
            });
        }
    }
    private static void ImmediateStopCheck(Action<bool, string> check, string root, Window owner, Action pump)
    {
        var source = new GeneratedCapture { EndInStart = true }; var preview = new SilentPreview(); var store = new SoundboardStore(Path.Combine(root, "immediate-library"));
        var recorder = new SoundboardRecorder((kind, id) => source, Path.Combine(root, "immediate-capture")); var window = new RecordSoundWindow(owner, store, recorder: recorder, preview: preview, readDevices: Devices);
        Show(window, pump, () =>
        {
            WaitFor(() => RecordButton(window).IsEnabled, pump); Click(RecordButton(window)); WaitFor(() => Button(window, "Preview").IsEnabled, pump);
            check(source.Disposed && !recorder.IsRecording && recorder.HasRecording && Named<Slider>(window, "Trim end seconds").Value > 0 && Button(window, "Save sound").IsEnabled, "A device that finishes before startup returns still exposes its completed take for trim, preview and save");
            Click(Button(window, "Cancel")); WaitFor(() => !window.IsVisible, pump);
        });
    }
    private static void LeadingSilenceReplayCheck(Action<bool, string> check, string root, Window owner, Action pump)
    {
        var source = new GeneratedCapture { DeferData = true, Seconds = 0.1 }; var preview = new SilentPreview(); var store = new SoundboardStore(Path.Combine(root, "leading-silence-library"));
        var recorder = new SoundboardRecorder((kind, id) => source, Path.Combine(root, "leading-silence-capture")); var window = new RecordSoundWindow(owner, store, recorder: recorder, preview: preview, readDevices: Devices);
        string wave = null, cachedPreview = null;
        Show(window, pump, () =>
        {
            WaitFor(() => RecordButton(window).IsEnabled, pump); Click(RecordButton(window)); WaitFor(() => recorder.IsRecording && Button(window, "Stop recording").IsEnabled, pump);
            PumpFor(800, pump); source.Push(); Click(Button(window, "Stop recording")); WaitFor(() => Button(window, "Preview").IsEnabled, pump); wave = recorder.WavePath;
            Click(Button(window, "Preview")); WaitFor(() => preview.Plays == 1 && Button(window, "Stop preview").IsEnabled, pump); cachedPreview = preview.Path;
            check(preview.FirstAudibleSeconds > 0.6 && preview.Duration >= 0.8 && preview.Peak > 0.01, "A generated PC take preserves its leading silence before the recorded sound instead of treating that silence as a preview failure");
            var button = Button(window, "Preview"); for (int attempt = 0; attempt < 4; attempt++) { button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); PumpFor(35, pump); }
            check(preview.Plays == 1 && preview.Path == cachedPreview && !button.IsEnabled && preview.IsPlaying, "Four rapid retries during a silent preview introduction do not reset its playback position or rebuild its WAV");
            preview.Complete(); WaitFor(() => button.IsEnabled && StatusContains(window, "Preview finished"), pump);
            Click(button); WaitFor(() => preview.Plays == 2 && Button(window, "Stop preview").IsEnabled, pump);
            check(preview.Path == cachedPreview, "An intentional replay after EOF uses the same cached selected waveform");
            preview.Fail("Fixture preview output disconnected."); WaitFor(() => button.IsEnabled && StatusContains(window, "Fixture preview output disconnected."), pump);
            check(StatusContains(window, "Unable to preview") && !Button(window, "Stop preview").IsEnabled, "An asynchronous local-output failure appears in the recording status and restores controls for retry");
            Click(Button(window, "Cancel")); WaitFor(() => !window.IsVisible, pump);
        });
        check(preview.Disposed && source.Disposed && !File.Exists(wave) && !File.Exists(cachedPreview) && !Directory.Exists(store.Root), "Cancelling after preview EOF or an output failure removes its cached WAV and raw take without saving a sound");
    }
    private static bool? Show(RecordSoundWindow window, Action pump, Action drive)
    {
        Exception failure = null; bool driven = false;
        window.Loaded += delegate { window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => { try { drive(); } catch (Exception error) { failure = error; window.Close(); } finally { driven = true; } })); };
        bool? result = window.ShowDialog();
        if (!driven) throw new Exception("Record dialog driver did not execute."); if (failure != null) throw failure;
        return result;
    }
    private static List<SoundboardDevice> Devices(SoundboardRecordingSource source)
    { return source == SoundboardRecordingSource.Microphone ? new List<SoundboardDevice> { new SoundboardDevice("mic-other", "Other fixture microphone"), new SoundboardDevice("mic-preferred", "Preferred fixture microphone") } : new List<SoundboardDevice> { new SoundboardDevice("pc-default", "Fixture speakers"), new SoundboardDevice("pc-secondary", "Fixture headphones") }; }
    private static Button Button(Window root, string title) { return Visuals<Button>(root).Single(button => string.Equals(button.Content as string, title, StringComparison.Ordinal)); }
    private static Button RecordButton(Window root) { return Visuals<Button>(root).Single(button => (button.Content as string) == "Record" || (button.Content as string) == "Record again"); }
    private static T Named<T>(DependencyObject root, string name) where T : DependencyObject { return Visuals<T>(root).Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == name); }
    private static void Click(Button button) { if (!button.IsEnabled) throw new Exception("Fixture tried to click disabled button: " + button.Content); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
    private static bool StatusContains(Window window, string value) { return Visuals<TextBlock>(window).Any(text => text.Text.Contains(value)); }
    private static string StatusText(Window window) { return string.Join(" | ", Visuals<TextBlock>(window).Select(text => text.Text).Where(text => !string.IsNullOrEmpty(text))); }
    private static void WaitFor(Func<bool> done, Action pump) { var timer = System.Diagnostics.Stopwatch.StartNew(); while (!done() && timer.ElapsedMilliseconds < 12000) { pump(); Thread.Sleep(5); } if (!done()) throw new TimeoutException("Recording window check timed out."); }
    private static void PumpFor(int milliseconds, Action pump) { var timer = System.Diagnostics.Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < milliseconds) { pump(); Thread.Sleep(5); } }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject { if (root is T) yield return (T)root; for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++) foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, index))) yield return child; }
    private sealed class GeneratedCapture : ISoundboardCapture
    {
        public WaveFormat WaveFormat { get { return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); } }
        public event Action<byte[], int> DataAvailable;
        public event Action<Exception> Stopped;
        private int starts, stops;
        internal int Starts { get { return Volatile.Read(ref starts); } }
        internal int Stops { get { return Volatile.Read(ref stops); } }
        internal volatile bool Disposed;
        internal bool EndInStart;
        internal bool DeferData;
        internal double Seconds = 1.2;
        internal ManualResetEventSlim StartEntered, AllowStart, DisposeEntered, AllowDispose;
        public void Start()
        {
            Interlocked.Increment(ref starts); if (StartEntered != null) { StartEntered.Set(); if (!AllowStart.Wait(10000)) throw new TimeoutException("Capture startup fixture was not released."); }
            if (!DeferData) Push(); if (EndInStart) End();
        }
        internal void Push()
        {
            int frames = (int)Math.Round(Seconds * 48000); var bytes = new byte[frames * WaveFormat.BlockAlign];
            for (int frame = 0; frame < frames; frame++) for (int channel = 0; channel < 2; channel++) Buffer.BlockCopy(BitConverter.GetBytes((float)(0.18 * Math.Sin(2 * Math.PI * (channel == 0 ? 660 : 880) * frame / 48000))), 0, bytes, (frame * 2 + channel) * 4, 4);
            var handler = DataAvailable; if (handler != null) handler(bytes, bytes.Length);
        }
        internal void End() { var handler = Stopped; if (handler != null) handler(null); }
        public void Stop() { Interlocked.Increment(ref stops); End(); }
        public void Dispose() { if (DisposeEntered != null) { DisposeEntered.Set(); if (!AllowDispose.Wait(10000)) throw new TimeoutException("Capture disposal fixture was not released."); } Disposed = true; }
    }
    private sealed class SilentPreview : ISoundboardRecordingPreview
    {
        internal int Plays, Stops, ThrowOnStop;
        internal bool Disposed;
        internal string Path, Output;
        internal float Volume, Peak;
        internal double Duration, FirstAudibleSeconds;
        public string Error { get; private set; }
        public bool IsPlaying { get; private set; }
        public Task PlayAsync(string path, string listeningOutputId, float volume)
        {
            Path = path; Output = listeningOutputId; Volume = volume;
            using (WaveStream reader = System.IO.Path.GetExtension(path) == ".wav" ? (WaveStream)new WaveFileReader(path) : new MediaFoundationReader(path))
            {
                var samples = reader.ToSampleProvider(); var buffer = new float[4096]; int read, count = 0, firstAudible = -1; float peak = 0;
                while ((read = samples.Read(buffer, 0, buffer.Length)) > 0) { for (int i = 0; i < read; i++) { peak = Math.Max(peak, Math.Abs(buffer[i])); if (firstAudible < 0 && Math.Abs(buffer[i]) > 0.0001f) firstAudible = count + i; } count += read; }
                Duration = count / (double)(samples.WaveFormat.SampleRate * samples.WaveFormat.Channels); FirstAudibleSeconds = firstAudible / (double)(samples.WaveFormat.SampleRate * samples.WaveFormat.Channels); Peak = peak;
            }
            Error = null; Plays++; IsPlaying = true; return Task.FromResult(0);
        }
        internal void Complete() { Error = null; IsPlaying = false; }
        internal void Fail(string error) { Error = error; IsPlaying = false; }
        public Task StopAsync() { Stops++; if (Stops == ThrowOnStop) return Task.FromException(new InvalidOperationException("Fixture preview disconnected during stop.")); IsPlaying = false; return Task.FromResult(0); }
        public void Dispose() { Disposed = true; IsPlaying = false; }
    }
}
