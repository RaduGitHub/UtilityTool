using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using UtilityTool.Backend;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using TitleBar = Wpf.Ui.Controls.TitleBar;

namespace UtilityTool.Frontend.Wpf
{
    internal sealed class RecordSoundWindow : FluentWindow
    {
        private readonly SoundboardStore store;
        private readonly SoundboardRecorder recorder;
        private readonly ISoundboardRecordingPreview preview;
        private readonly Func<SoundboardRecordingSource, List<SoundboardDevice>> readDevices;
        private readonly string listeningOutputId, preferredMicrophoneId;
        private readonly float previewVolume;
        private readonly ComboBox source = new ComboBox { Height = 40 }, device = new ComboBox { Height = 40 };
        private readonly TextBlock clock = Ui.Text("00:00.0", 28), sourceHint = Ui.Text("", 12, true), selection = Ui.Text("Record some audio to preview and trim it.", 13, true), status = Ui.Text("", 13, true);
        private readonly ProgressBar level = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8 };
        private readonly Slider trimStart = new Slider { Minimum = 0, Maximum = 1, SmallChange = 0.01, LargeChange = 0.1 }, trimEnd = new Slider { Minimum = 0, Maximum = 1, SmallChange = 0.01, LargeChange = 0.1 };
        private readonly TextBlock startLabel = Ui.Text("Start · 00:00.00", 13), endLabel = Ui.Text("End · 00:00.00", 13);
        private readonly System.Windows.Controls.TextBox name = new System.Windows.Controls.TextBox { Text = "New recording", Height = 40, MaxLength = 64 }, image = new System.Windows.Controls.TextBox { IsReadOnly = true, Height = 40 };
        private readonly global::Wpf.Ui.Controls.Button record = Ui.Button("Record", true), stop = Ui.Button("Stop recording"), refresh = Ui.Button("Refresh devices"), play = Ui.Button("Preview"), stopPreview = Ui.Button("Stop preview"), save = Ui.Button("Save sound", true), cancel = Ui.Button("Cancel"), browse = Ui.Button("Browse…");
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly HashSet<string> previewFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly CheckBox reduceImage = new CheckBox { Content = "Reduce image size", Margin = new Thickness(0, 12, 0, 0) };
        private string cachedPreview, cachedRecording;
        private TimeSpan cachedStart, cachedEnd;
        private Task operation = Task.FromResult(0);
        private bool busy, saving, closing, closeAllowed, cleaned, settingTrim, wasRecording, wasPreviewing;
        internal SoundboardSound CreatedSound { get; private set; }

        internal RecordSoundWindow(Window owner, SoundboardStore store, string listeningOutputId = null, float volume = SoundboardAudioService.DefaultVolume, string preferredMicrophoneId = null,
            SoundboardRecorder recorder = null, ISoundboardRecordingPreview preview = null, Func<SoundboardRecordingSource, List<SoundboardDevice>> readDevices = null)
        {
            Owner = owner; this.store = store; this.listeningOutputId = listeningOutputId; previewVolume = volume; this.preferredMicrophoneId = preferredMicrophoneId;
            this.recorder = recorder ?? new SoundboardRecorder(); this.preview = preview ?? new SoundboardRecordingPreview(); this.readDevices = readDevices ?? SoundboardRecorder.ReadSources;
            Title = "Record audio"; Width = 760; Height = Math.Min(880, SystemParameters.WorkArea.Height - 40); MinHeight = Math.Min(600, Height); MinWidth = 680;
            WindowStartupLocation = WindowStartupLocation.CenterOwner; ExtendsContentIntoTitleBar = true;
            Background = new SolidColorBrush(Color.FromRgb(28, 32, 39)); Foreground = Brushes.WhiteSmoke;
            var layout = new Grid { Background = Background };
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TitleBar { Title = "UtilityTool", ShowMinimize = false, ShowMaximize = false, ForceShutdown = false });
            var heading = Ui.Heading("Record audio", "Capture a sound, choose the part you want, and add it to your soundboard."); heading.Margin = new Thickness(24, 16, 24, 16); Grid.SetRow(heading, 1); layout.Children.Add(heading);
            var capture = Ui.Card(CaptureControls(), new Thickness(16)); capture.Margin = new Thickness(24, 0, 24, 16); Grid.SetRow(capture, 2); layout.Children.Add(capture);
            var details = new StackPanel();
            details.Children.Add(TrimControls());
            var labels = new Grid { Margin = new Thickness(0, 16, 0, 0) }; labels.ColumnDefinitions.Add(new ColumnDefinition()); labels.ColumnDefinitions.Add(new ColumnDefinition());
            var nameColumn = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; nameColumn.Children.Add(Label("Sound name")); nameColumn.Children.Add(name); labels.Children.Add(nameColumn);
            var imageColumn = new StackPanel(); imageColumn.Children.Add(Label("Image (optional)")); Grid.SetColumn(imageColumn, 1); labels.Children.Add(imageColumn);
            var artwork = new DockPanel(); browse.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right); artwork.Children.Add(browse); artwork.Children.Add(image); imageColumn.Children.Add(artwork); details.Children.Add(labels);
            details.Children.Add(reduceImage); var sizeHint = Ui.Text("Fits large images within 512 × 512 pixels. Recorded sounds are saved as 128 kbps MP3.", 12, true); sizeHint.Margin = new Thickness(0, 4, 0, 0); details.Children.Add(sizeHint);
            var scroll = new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(24, 0, 24, 8) }; Grid.SetRow(scroll, 3); layout.Children.Add(scroll);
            var footer = new StackPanel { Margin = new Thickness(24, 0, 24, 20) }; status.MinHeight = 34; status.Margin = new Thickness(0, 0, 0, 8); footer.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(save); buttons.Children.Add(cancel); footer.Children.Add(buttons); Grid.SetRow(footer, 4); layout.Children.Add(footer); Content = layout;
            source.Items.Add("PC audio"); source.Items.Add("Microphone"); source.SelectedIndex = 0;
            source.SelectionChanged += delegate { if (IsLoaded && !busy && !closing) operation = LoadDevices(); };
            device.SelectionChanged += delegate { UpdateControls(); };
            record.Click += delegate { operation = StartRecording(); }; stop.Click += delegate { operation = StopRecording(); }; refresh.Click += delegate { operation = LoadDevices(); };
            play.Click += delegate { operation = PreviewRecording(); }; stopPreview.Click += delegate { operation = StopPreview(); };
            save.Click += delegate { operation = SaveRecording(); }; cancel.Click += delegate { RequestClose(); };
            browse.Click += delegate { if (busy || closing) return; var picker = new OpenFileDialog { Title = "Choose a sound image", Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico", CheckFileExists = true }; if (picker.ShowDialog(this) == true) image.Text = picker.FileName; };
            trimStart.ValueChanged += delegate { ChangeTrim(true); }; trimEnd.ValueChanged += delegate { ChangeTrim(false); };
            Loaded += delegate { timer.Start(); operation = LoadDevices(); };
            timer.Tick += delegate { Tick(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (closeAllowed) return; e.Cancel = true; if (!saving) RequestClose(); };
            Closed += delegate { timer.Stop(); };
            PreviewKeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == System.Windows.Input.Key.Escape && !saving) { e.Handled = true; RequestClose(); } };
            System.Windows.Automation.AutomationProperties.SetName(source, "Recording source"); System.Windows.Automation.AutomationProperties.SetName(device, "Recording device"); System.Windows.Automation.AutomationProperties.SetName(level, "Recording input level");
            System.Windows.Automation.AutomationProperties.SetName(trimStart, "Trim start seconds"); System.Windows.Automation.AutomationProperties.SetName(trimEnd, "Trim end seconds"); System.Windows.Automation.AutomationProperties.SetName(name, "Recording sound name"); System.Windows.Automation.AutomationProperties.SetName(image, "Recording image");
            System.Windows.Automation.AutomationProperties.SetName(reduceImage, "Reduce recording image size");
            UpdateControls();
        }
        private static TextBlock Label(string text, double top = 0) { var label = Ui.Text(text, 13); label.Margin = new Thickness(0, top, 0, 6); return label; }
        private UIElement CaptureControls()
        {
            var body = new StackPanel(); var choices = new Grid(); choices.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) }); choices.ColumnDefinitions.Add(new ColumnDefinition());
            var sourceColumn = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; sourceColumn.Children.Add(Label("Source")); sourceColumn.Children.Add(source); choices.Children.Add(sourceColumn);
            var deviceColumn = new StackPanel(); deviceColumn.Children.Add(Label("Device")); deviceColumn.Children.Add(device); Grid.SetColumn(deviceColumn, 1); choices.Children.Add(deviceColumn); body.Children.Add(choices);
            sourceHint.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(sourceHint);
            var meter = new Grid { Margin = new Thickness(0, 14, 0, 14) }; meter.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); meter.ColumnDefinitions.Add(new ColumnDefinition());
            clock.FontFamily = new FontFamily("Consolas"); clock.MinWidth = 130; meter.Children.Add(clock);
            var input = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) }; input.Children.Add(Ui.Text("Input level", 12, true)); level.Margin = new Thickness(0, 6, 0, 0); input.Children.Add(level); Grid.SetColumn(input, 1); meter.Children.Add(input); body.Children.Add(meter);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(record); actions.Children.Add(stop); actions.Children.Add(refresh); body.Children.Add(actions); return body;
        }
        private UIElement TrimControls()
        {
            var body = new StackPanel(); var title = Ui.Text("Preview and trim", 16); title.FontWeight = FontWeights.SemiBold; body.Children.Add(title);
            startLabel.Margin = new Thickness(0, 12, 0, 4); body.Children.Add(startLabel); body.Children.Add(trimStart); endLabel.Margin = new Thickness(0, 8, 0, 4); body.Children.Add(endLabel); body.Children.Add(trimEnd);
            selection.Margin = new Thickness(0, 8, 0, 10); body.Children.Add(selection);
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(play); actions.Children.Add(stopPreview); body.Children.Add(actions); return Ui.Card(body, new Thickness(16));
        }
        private SoundboardRecordingSource Source { get { return source.SelectedIndex == 1 ? SoundboardRecordingSource.Microphone : SoundboardRecordingSource.ComputerAudio; } }
        private async Task LoadDevices()
        {
            if (busy || closing || recorder.IsRecording) return; busy = true; device.ItemsSource = null; device.SelectedItem = null; UpdateControls(); Ui.Status(status, "Loading recording devices…");
            try
            {
                var kind = Source; var devices = await Task.Run(() => readDevices(kind)); if (closing) return;
                device.ItemsSource = devices; device.SelectedItem = kind == SoundboardRecordingSource.Microphone ? devices.Find(item => item.Id == preferredMicrophoneId) ?? devices.FirstOrDefault() : devices.FirstOrDefault();
                sourceHint.Text = kind == SoundboardRecordingSource.ComputerAudio ? "Records everything playing through the selected speakers or headphones." : "Records the selected microphone.";
                Ui.Status(status, devices.Count == 0 ? "No recording device found. Connect a device, then refresh." : "Ready. Press Record when you want to start.", devices.Count == 0);
            }
            catch (Exception error) { if (!closing) Ui.Status(status, "Unable to load recording devices: " + error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private async Task StartRecording()
        {
            if (busy || closing || recorder.IsRecording) return; var selected = device.SelectedItem as SoundboardDevice; if (selected == null) return;
            busy = true; UpdateControls(); Ui.Status(status, "Starting recording…");
            try
            {
                await preview.StopAsync(); wasPreviewing = false; RemovePreviewFiles(); await recorder.StartAsync(Source, selected.Id);
                wasRecording = recorder.IsRecording;
                if (!closing)
                {
                    if (wasRecording) Ui.Status(status, "Recording… Press Stop recording when finished.");
                    else { await recorder.StopAsync(); SetFullTrim(); FinishedStatus(); }
                }
            }
            catch (Exception error) { if (!closing) Ui.Status(status, "Unable to record: " + error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private async Task StopRecording()
        {
            if (busy || closing) return; busy = true; UpdateControls(); Ui.Status(status, "Finishing recording…");
            try { await recorder.StopAsync(); wasRecording = false; SetFullTrim(); if (!closing) FinishedStatus(); }
            catch (Exception error) { if (!closing) Ui.Status(status, error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private void SetFullTrim()
        {
            settingTrim = true; double seconds = Math.Max(0, recorder.Duration.TotalSeconds); trimStart.Maximum = trimEnd.Maximum = Math.Max(0.001, seconds); trimStart.Value = 0; trimEnd.Value = seconds; settingTrim = false; UpdateTrimLabels();
        }
        private void ChangeTrim(bool startChanged)
        {
            if (settingTrim) return; settingTrim = true;
            if (trimStart.Value > trimEnd.Value) { if (startChanged) trimEnd.Value = trimStart.Value; else trimStart.Value = trimEnd.Value; }
            settingTrim = false; UpdateTrimLabels(); UpdateControls();
        }
        private void UpdateTrimLabels()
        {
            startLabel.Text = "Start · " + FormatTime(trimStart.Value); endLabel.Text = "End · " + FormatTime(trimEnd.Value);
            selection.Text = recorder.HasRecording ? "Selected duration · " + FormatTime(Math.Max(0, trimEnd.Value - trimStart.Value)) : "Record some audio to preview and trim it.";
        }
        private static string FormatTime(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return ((int)time.TotalMinutes).ToString("00") + ":" + time.Seconds.ToString("00") + "." + (time.Milliseconds / 10).ToString("00"); }
        private static TimeSpan TrimTime(double seconds) { return TimeSpan.FromTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)); }
        private void FinishedStatus()
        {
            bool failed = !string.IsNullOrEmpty(recorder.Error);
            Ui.Status(status, failed ? "Recording stopped: " + recorder.Error : recorder.HasRecording ? "Recording ready. Preview it or adjust the start and end." : "No audio was recorded. Check the selected device and try again.", failed || !recorder.HasRecording);
        }
        private async Task PreviewRecording()
        {
            if (busy || closing || preview.IsPlaying || !ValidTrim()) return; busy = true; UpdateControls(); Ui.Status(status, "Preparing preview…");
            try
            {
                await preview.StopAsync(); wasPreviewing = false;
                var start = TrimTime(trimStart.Value); var end = TrimTime(trimEnd.Value);
                if (cachedPreview == null || !File.Exists(cachedPreview) || cachedRecording != recorder.WavePath || cachedStart != start || cachedEnd != end)
                {
                    RemovePreviewFiles();
                    cachedPreview = await SoundboardRecordingExport.CreateWavePreviewAsync(recorder.WavePath, start, end, Path.GetDirectoryName(recorder.WavePath));
                    previewFiles.Add(cachedPreview); cachedRecording = recorder.WavePath; cachedStart = start; cachedEnd = end;
                }
                if (closing) return; await preview.PlayAsync(cachedPreview, listeningOutputId, previewVolume); wasPreviewing = true;
                Ui.Status(status, preview.IsPlaying ? "Playing the selected part through your listening output. Stop preview before replaying." : string.IsNullOrEmpty(preview.Error) ? "Preview finished." : "Unable to preview: " + preview.Error, !string.IsNullOrEmpty(preview.Error));
            }
            catch (Exception error) { wasPreviewing = false; if (!closing) Ui.Status(status, "Unable to preview: " + error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private async Task StopPreview()
        {
            if (busy || closing) return; busy = true; UpdateControls();
            try { await preview.StopAsync(); wasPreviewing = false; if (!closing) Ui.Status(status, "Preview stopped."); }
            catch (Exception error) { if (!closing) Ui.Status(status, error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private async Task SaveRecording()
        {
            if (busy || closing || !ValidTrim()) return; if (string.IsNullOrWhiteSpace(name.Text)) { Ui.Status(status, "Enter a name for this sound.", true); name.Focus(); return; }
            busy = saving = true; UpdateControls(); Ui.Status(status, "Saving your sound…");
            try
            {
                await preview.StopAsync();
                CreatedSound = await SoundboardRecordingExport.SaveAsync(store, recorder.WavePath, name.Text, image.Text, TrimTime(trimStart.Value), TrimTime(trimEnd.Value), Path.GetDirectoryName(recorder.WavePath), reduceImage.IsChecked == true);
                await Cleanup(); closeAllowed = true; DialogResult = true;
            }
            catch (Exception error) { if (!closing) Ui.Status(status, "Unable to save this recording: " + error.Message, true); }
            finally { busy = saving = false; UpdateControls(); }
        }
        private bool ValidTrim() { return recorder.HasRecording && !recorder.IsRecording && trimEnd.Value > trimStart.Value && trimStart.Value >= 0; }
        private void Tick()
        {
            if (closing) return; clock.Text = FormatTime(recorder.Duration.TotalSeconds); level.Value = recorder.IsRecording ? Math.Min(100, recorder.Peak * 100) : 0;
            if (wasRecording && !recorder.IsRecording && !busy) { wasRecording = false; operation = StopRecording(); }
            if (wasPreviewing && !preview.IsPlaying && !busy)
            {
                wasPreviewing = false; Ui.Status(status, string.IsNullOrEmpty(preview.Error) ? "Preview finished." : "Unable to preview: " + preview.Error, !string.IsNullOrEmpty(preview.Error));
            }
            UpdateControls();
        }
        private void UpdateControls()
        {
            bool active = recorder.IsRecording; bool enabled = !busy && !closing;
            source.IsEnabled = device.IsEnabled = refresh.IsEnabled = enabled && !active; record.IsEnabled = enabled && !active && device.SelectedItem != null; stop.IsEnabled = enabled && active;
            record.Content = recorder.HasRecording ? "Record again" : "Record";
            trimStart.IsEnabled = trimEnd.IsEnabled = enabled && recorder.HasRecording && !active && !preview.IsPlaying; play.IsEnabled = enabled && ValidTrim() && !preview.IsPlaying; stopPreview.IsEnabled = enabled && preview.IsPlaying;
            name.IsEnabled = image.IsEnabled = browse.IsEnabled = reduceImage.IsEnabled = enabled; save.IsEnabled = enabled && ValidTrim(); cancel.IsEnabled = !saving && !closing;
        }
        private void RequestClose()
        { if (closing || closeAllowed || saving) return; closing = true; timer.Stop(); UpdateControls(); Ui.Status(status, "Closing recorder…"); FinishClose(); }
        private async void FinishClose()
        {
            try { try { await operation; } catch { } await Cleanup(); }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            finally { closeAllowed = true; Close(); }
        }
        private async Task Cleanup()
        {
            if (cleaned) return; timer.Stop();
            // Release each resource even if a disconnected device reports a stop
            // failure. A saved sound is already committed before this cleanup.
            try { await preview.StopAsync(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            try { await recorder.StopAsync(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            try { preview.Dispose(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            try { RemovePreviewFiles(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            recorder.Dispose();
            try { await recorder.DisposalCompleted; } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            cleaned = true;
        }
        private void RemovePreviewFiles()
        { foreach (string file in previewFiles) if (File.Exists(file)) File.Delete(file); previewFiles.Clear(); cachedPreview = cachedRecording = null; }
    }
}
