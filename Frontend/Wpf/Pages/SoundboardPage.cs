using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using UtilityTool.Backend;

namespace UtilityTool.Frontend.Wpf
{
    internal sealed class SoundboardPage : Grid, IDisposable
    {
        internal readonly TileGrid Tiles = new TileGrid();
        private readonly Window owner;
        private readonly bool preview;
        private readonly SoundboardStore store;
        private readonly SoundboardAudioService audio;
        private readonly SoundboardSettingsStore settingsStore;
        private readonly SoundboardSettings settings;
        private readonly Func<List<SoundboardDevice>> listeningDevices;
        private readonly Func<SoundboardDevices> callDevices;
        private readonly Func<List<SoundboardSound>> libraryLoader;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> artworkCache = new System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage>(StringComparer.OrdinalIgnoreCase);
        private readonly StackPanel loading = new StackPanel { MaxWidth = 280, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        private int artworkVersion;
        private readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        private readonly Grid soundArea = new Grid();
        private readonly RowDefinition callRow = new RowDefinition { Height = new GridLength(246), MinHeight = 236 };
        private readonly GridSplitter splitter = new GridSplitter { Height = 12, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows, ResizeBehavior = GridResizeBehavior.PreviousAndNext, ShowsPreview = true, Cursor = Cursors.SizeNS };
        private readonly List<TileItem> items = new List<TileItem>();
        private readonly global::Wpf.Ui.Controls.Button create = Ui.Button("Create new sound", true), delete = Ui.Button("Delete sound"), stop = Ui.Button("Stop sounds");
        private readonly ContextMenu createMenu = new ContextMenu();
        private readonly global::Wpf.Ui.Controls.Button refresh = Ui.Button("Refresh devices"), startCalls = Ui.Button("Start call audio", true), stopCalls = Ui.Button("Stop call audio");
        private readonly global::Wpf.Ui.Controls.Button testOutput = Ui.Button("Test output"), refreshOutputs = Ui.Button("Refresh outputs");
        private readonly TextBlock status = Ui.Text("", 13, true), callStatus = Ui.Text("Choose your microphone and a virtual audio output.", 13, true), empty = Ui.Text("Your sounds will appear here. Create a sound to get started.", 15, true);
        private readonly ComboBox microphone = new ComboBox { MinWidth = 200, Height = 40 }, virtualOutput = new ComboBox { MinWidth = 200, Height = 40 };
        private readonly ComboBox listeningOutput = new ComboBox { Height = 40, MinWidth = 200 };
        private readonly Slider volume = new Slider { Minimum = 0, Maximum = 100, Value = SoundboardAudioService.DefaultVolume * 100, SmallChange = 1, LargeChange = 10, TickFrequency = 1, Height = 30, VerticalAlignment = VerticalAlignment.Center };
        private readonly TextBlock volumePercent = Ui.Text("25%", 13);
        private bool loaded, libraryBusy, audioBusy, lastRouting, disposed, libraryAvailable, bindingOutput, bindingDevices, settingsDirty;
        private bool busy { get { return libraryBusy || audioBusy; } }
        private string selectedId, listeningId;
        private Task initialization;
        internal bool NeedsLoading { get { return !loaded && !libraryBusy; } }
        internal SoundboardPage(Window owner, bool preview, SoundboardStore store = null, SoundboardAudioService audio = null, Func<List<SoundboardDevice>> listeningDevices = null, SoundboardSettingsStore settingsStore = null, Func<SoundboardDevices> callDevices = null, Func<List<SoundboardSound>> libraryLoader = null)
        {
            this.owner = owner; this.preview = preview; this.store = store ?? new SoundboardStore(); this.audio = audio ?? new SoundboardAudioService();
            this.listeningDevices = listeningDevices ?? SoundboardDevices.ReadListeningOutputs;
            this.callDevices = callDevices ?? SoundboardDevices.Read;
            this.libraryLoader = libraryLoader ?? this.store.Load;
            this.settingsStore = settingsStore ?? new SoundboardSettingsStore(Path.Combine(this.store.Root, "settings.xml"));
            settings = preview ? new SoundboardSettings() : this.settingsStore.Load(); listeningId = settings.ListeningOutputId;
            volume.Value = settings.Volume * 100;
            Margin = new Thickness(32, 28, 32, 24);
            RowDefinitions.Add(new RowDefinition { MinHeight = 336 }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(callRow);
            soundArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); soundArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); soundArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); soundArea.RowDefinitions.Add(new RowDefinition()); soundArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); soundArea.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Children.Add(soundArea);
            var listen = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            var listenLabel = Ui.Text("Listen on", 13); listenLabel.VerticalAlignment = VerticalAlignment.Center; listenLabel.Margin = new Thickness(0, 0, 12, 0); listen.Children.Add(listenLabel);
            refreshOutputs.Margin = new Thickness(0); DockPanel.SetDock(refreshOutputs, Dock.Right); listen.Children.Add(refreshOutputs); testOutput.Margin = new Thickness(12, 0, 10, 0); DockPanel.SetDock(testOutput, Dock.Right); listen.Children.Add(testOutput); listen.Children.Add(listeningOutput);
            System.Windows.Automation.AutomationProperties.SetName(listeningOutput, "Listen on"); Grid.SetRow(listen, 1); soundArea.Children.Add(listen);
            var volumeControls = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            volumeControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); volumeControls.ColumnDefinitions.Add(new ColumnDefinition()); volumeControls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var volumeLabel = Ui.Text("Volume", 13); volumeLabel.VerticalAlignment = VerticalAlignment.Center; volumeControls.Children.Add(volumeLabel);
            volume.Margin = new Thickness(16, 0, 16, 0); Grid.SetColumn(volume, 1); volumeControls.Children.Add(volume);
            volumePercent.MinWidth = 40; volumePercent.TextAlignment = TextAlignment.Right; volumePercent.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(volumePercent, 2); volumeControls.Children.Add(volumePercent);
            System.Windows.Automation.AutomationProperties.SetName(volume, "Soundboard volume"); Grid.SetRow(volumeControls, 2); soundArea.Children.Add(volumeControls);
            volume.ValueChanged += delegate { UpdateVolume(); }; UpdateVolume();
            loading.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 4, Margin = new Thickness(0, 0, 0, 12) }); loading.Children.Add(Ui.Text("Loading sounds…", 14, true));
            System.Windows.Automation.AutomationProperties.SetName(loading, "Soundboard loading");
            var grid = new Grid { MinHeight = 156 }; grid.Children.Add(Tiles); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(empty); grid.Children.Add(loading); Grid.SetRow(grid, 3); soundArea.Children.Add(grid);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 8) }; actions.Children.Add(create); actions.Children.Add(delete); actions.Children.Add(stop); Grid.SetRow(actions, 4); soundArea.Children.Add(actions);
            status.MinHeight = 32; Grid.SetRow(status, 5); soundArea.Children.Add(status);
            splitter.Background = (Brush)Application.Current.FindResource("ToolBorder"); splitter.ToolTip = "Drag to resize the sounds and call-audio sections";
            System.Windows.Automation.AutomationProperties.SetName(splitter, "Resize sound grid and call audio"); Grid.SetRow(splitter, 1); Children.Add(splitter);
            var callPanel = new Grid(); callPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); callPanel.RowDefinitions.Add(new RowDefinition());
            var callHeading = Ui.Text("Play in calls", 16); callHeading.FontWeight = FontWeights.SemiBold; callHeading.Margin = new Thickness(0, 0, 0, 4); callPanel.Children.Add(callHeading);
            var callBody = CallControls(); Grid.SetRow(callBody, 1); callPanel.Children.Add(callBody);
            var callCard = Ui.Card(callPanel, new Thickness(12)); callCard.Margin = new Thickness(0, 8, 0, 0);
            System.Windows.Automation.AutomationProperties.SetName(callCard, "Play in calls"); Grid.SetRow(callCard, 2); Children.Add(callCard);
            var importSound = new MenuItem { Header = "Import MP3", Icon = new TextBlock { Text = "\uE8D6", FontFamily = new FontFamily("Segoe MDL2 Assets") } };
            var recordSound = new MenuItem { Header = "Record audio", Icon = Ui.Text("\u25CF") };
            importSound.Click += delegate { Create(); }; recordSound.Click += async delegate { await CreateRecording(); };
            createMenu.Items.Add(importSound); createMenu.Items.Add(recordSound); createMenu.PlacementTarget = create; createMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom; create.ContextMenu = createMenu;
            create.Click += delegate { if (!preview && !busy && !disposed && libraryAvailable) createMenu.IsOpen = true; }; delete.Click += async delegate { await Delete(); }; stop.Click += async delegate { await StopSounds(); };
            refresh.Click += async delegate { await LoadDevices(); }; startCalls.Click += async delegate { await StartCalls(); }; stopCalls.Click += async delegate { await StopCalls(); };
            microphone.SelectionChanged += delegate { ChangeCallDevices(); }; virtualOutput.SelectionChanged += delegate { ChangeCallDevices(); };
            listeningOutput.SelectionChanged += async delegate { if (!bindingOutput) await ChangeListeningOutput(); };
            testOutput.Click += async delegate { await TestOutput(); }; refreshOutputs.Click += async delegate { await LoadListeningOutputs(); };
            if (preview) { listeningOutput.Items.Add(new SoundboardDevice(null, "Windows default — Speakers")); listeningOutput.SelectedIndex = 0; }
            saveTimer.Tick += delegate { SaveSettings(); };
            SizeChanged += delegate
            {
                // A divider moved in a taller window must still leave both
                // sections visible when the window returns to its minimum size.
                double maximum = Math.Max(callRow.MinHeight, ActualHeight - splitter.Height - RowDefinitions[0].MinHeight);
                if (callRow.Height.Value > maximum) callRow.Height = new GridLength(maximum);
            };
            callStatus.TextWrapping = TextWrapping.NoWrap; callStatus.TextTrimming = TextTrimming.CharacterEllipsis;
            callStatus.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Text") { Source = callStatus });
            this.audio.Stopped += AudioStopped; this.audio.PlaybackFinished += PlaybackFinished; UpdateControls();
        }
        private UIElement CallControls()
        {
            var body = new StackPanel();
            body.Children.Add(Ui.Text("Use CABLE Input here, and CABLE Output as your microphone in Discord or Meet.", 13, true));
            var choices = new Grid { Margin = new Thickness(0, 8, 0, 8) }; choices.ColumnDefinitions.Add(new ColumnDefinition()); choices.ColumnDefinitions.Add(new ColumnDefinition());
            var micPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; micPanel.Children.Add(Ui.Text("Your microphone", 13)); microphone.Margin = new Thickness(0, 6, 0, 0); micPanel.Children.Add(microphone); choices.Children.Add(micPanel);
            var outPanel = new StackPanel(); outPanel.Children.Add(Ui.Text("Virtual audio output", 13)); virtualOutput.Margin = new Thickness(0, 6, 0, 0); outPanel.Children.Add(virtualOutput); Grid.SetColumn(outPanel, 1); choices.Children.Add(outPanel); body.Children.Add(choices);
            System.Windows.Automation.AutomationProperties.SetName(microphone, "Your microphone"); System.Windows.Automation.AutomationProperties.SetName(virtualOutput, "Virtual audio output");
            var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(startCalls); actions.Children.Add(stopCalls); actions.Children.Add(refresh); body.Children.Add(actions);
            callStatus.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(callStatus);
            var hint = Ui.Text("Keep UtilityTool open for calls. After stopping, switch your call apps back to your microphone.", 12, true); hint.Margin = new Thickness(0, 4, 0, 0); body.Children.Add(hint);
            return body;
        }
        internal Task InitializeAsync()
        {
            if (preview || disposed) return Task.FromResult(0);
            return initialization ?? (initialization = InitializeCore());
        }
        private async Task InitializeCore()
        {
            Ui.Status(callStatus, settings.CallAudioEnabled ? "Restoring call audio…" : "Loading audio devices…");
            await LoadListeningOutputs(); if (disposed) return;
            await LoadDevices(); if (disposed || !settings.CallAudioEnabled) return;
            if (microphone.SelectedItem == null || virtualOutput.SelectedItem == null || string.IsNullOrEmpty(settings.MicrophoneId) || string.IsNullOrEmpty(settings.VirtualOutputId))
            { Ui.Status(callStatus, "Saved call audio could not start: reconnect your saved microphone and virtual output, then refresh devices.", true); return; }
            await StartCalls();
        }
        internal async Task LoadSounds()
        {
            if (libraryBusy || disposed) return; libraryBusy = true; UpdateControls();
            Ui.Status(status, "Loading sounds…");
            try
            {
                if (preview)
                {
                    ApplyItems(BuildItems(new List<SoundboardSound> { new SoundboardSound("sample1", "Applause", null, null), new SoundboardSound("sample2", "Air horn", null, null), new SoundboardSound("sample3", "Drum roll", null, null) }));
                    Ui.Status(status, "Sample preview • Import and playback are disabled.");
                }
                else
                {
                    var sounds = await Task.Run(libraryLoader); if (disposed) return; ApplyItems(BuildItems(sounds));
                    Ui.Status(status, "Choose speakers or headphones under Listen on, then press Test output to hear a short beep. Call sharing is optional.");
                }
                libraryAvailable = loaded = true;
                empty.Text = "Your sounds will appear here. Create a sound to get started.";
            }
            catch (Exception error) { if (!disposed) { libraryAvailable = loaded = false; ApplyItems(new List<TileItem>()); empty.Text = "Unable to load sounds. Reopen this tab to retry."; Ui.Status(status, error.Message, true); } }
            finally { libraryBusy = false; if (!disposed) UpdateControls(); }
            if (!preview && !disposed && initialization == null) BeginAudioInitialization();
        }
        private async void BeginAudioInitialization()
        { try { await InitializeAsync(); } catch (Exception error) { if (!disposed) Ui.Status(callStatus, "Unable to prepare call audio: " + error.Message, true); } }
        private List<TileItem> BuildItems(List<SoundboardSound> sounds)
        {
            var result = new List<TileItem>();
            foreach (var sound in sounds)
            {
                var item = new TileItem(sound.Id, sound.Name, sound, sound.Id == selectedId, "\uE8D6", playCommand: new PlaySoundCommand(Play), optionsCommand: new PlaySoundCommand(Edit));
                BitmapImage cached; if (sound.ImagePath != null && artworkCache.TryGetValue(sound.ImagePath, out cached)) item.Icon = cached;
                result.Add(item);
            }
            return result;
        }
        private void ApplyItems(List<TileItem> value)
        {
            items.Clear(); items.AddRange(value);
            if (!items.Any(item => item.Id == selectedId)) selectedId = null;
            Tiles.SetItems(items); UpdateControls(); LoadArtwork(value, ++artworkVersion);
        }
        private async void LoadArtwork(List<TileItem> value, int version)
        {
            foreach (var item in value)
            {
                var sound = (SoundboardSound)item.Tag; if (sound.ImagePath == null || item.Icon != null) continue;
                if (disposed || version != artworkVersion) return;
                BitmapImage image = await Task.Run(() => ReadArtwork(sound.ImagePath));
                if (disposed || version != artworkVersion) return;
                if (image != null) item.Icon = image;
            }
            var used = new HashSet<string>(value.Select(item => ((SoundboardSound)item.Tag).ImagePath).Where(path => path != null), StringComparer.OrdinalIgnoreCase);
            foreach (string path in artworkCache.Keys) if (!used.Contains(path)) { BitmapImage removed; artworkCache.TryRemove(path, out removed); }
        }
        private BitmapImage ReadArtwork(string path)
        {
            try
            {
                BitmapImage cached; if (artworkCache.TryGetValue(path, out cached)) return cached;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                { var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 120; image.StreamSource = stream; image.EndInit(); image.Freeze(); artworkCache[path] = image; return image; }
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is NotSupportedException || error is ArgumentException || error is FormatException) { return null; }
        }
        private async void Edit(TileItem item)
        {
            if (preview || busy || disposed || item == null || !libraryAvailable) return;
            libraryBusy = true; UpdateControls();
            try
            {
                await audio.StopClipsAsync(); if (disposed) return;
                var dialog = new CreateSoundWindow(owner, store, (SoundboardSound)item.Tag);
                if (dialog.ShowDialog() == true)
                {
                    selectedId = dialog.CreatedSound.Id;
                    var updated = items.Select(tile => tile.Id == selectedId ? dialog.CreatedSound : (SoundboardSound)tile.Tag).ToList();
                    ApplyItems(BuildItems(updated)); Ui.Status(status, "Sound updated.");
                }
            }
            catch (Exception error) { if (!disposed) Ui.Status(status, "Unable to edit this sound: " + error.Message, true); }
            finally { libraryBusy = false; if (!disposed) UpdateControls(); }
        }
        private async void Play(TileItem item)
        {
            if (disposed || busy || item == null) return;
            selectedId = item.Id; foreach (var tile in items) tile.Selected = tile.Id == item.Id; UpdateControls();
            if (preview) { Ui.Status(status, "Sample preview • Playback is disabled."); return; }
            var sound = (SoundboardSound)item.Tag;
            try
            {
                await audio.PlayAsync(sound.AudioPath);
                if (!disposed) Ui.Status(status, !audio.IsPlaying ? "Playback finished." : "Playing “" + sound.Name + "”" + (audio.LocalOutputName == null ? " in your virtual microphone. Choose speakers or headphones under Listen on to hear it yourself." : " through “" + audio.LocalOutputName + "”" + (audio.IsRouting ? " and your virtual microphone." : ".")));
            }
            catch (Exception error) { if (!disposed) Ui.Status(status, "Unable to play this sound: " + error.Message, true); }
        }
        private void Create()
        {
            if (preview || busy || disposed || !libraryAvailable) return;
            var dialog = new CreateSoundWindow(owner, store);
            if (dialog.ShowDialog() == true) { selectedId = dialog.CreatedSound.Id; loaded = false; Reload(); }
        }
        private async Task CreateRecording()
        {
            if (preview || busy || disposed || !libraryAvailable) return;
            libraryBusy = true; UpdateControls(); SoundboardSound created = null;
            try
            {
                await audio.StopClipsAsync(); if (disposed) return;
                var dialog = new RecordSoundWindow(owner, store, listeningId, (float)(volume.Value / 100), settings.MicrophoneId);
                if (dialog.ShowDialog() == true) created = dialog.CreatedSound;
            }
            catch (Exception error) { if (!disposed) Ui.Status(status, "Unable to open the recorder: " + error.Message, true); }
            finally { libraryBusy = false; if (!disposed) UpdateControls(); }
            if (created != null && !disposed) { selectedId = created.Id; loaded = false; await LoadSounds(); }
        }
        private async void Reload() { await LoadSounds(); }
        private async Task Delete()
        {
            if (preview || busy || disposed || selectedId == null) return;
            string id = selectedId; libraryBusy = true; UpdateControls();
            try
            {
                await audio.StopClipsAsync(); await Task.Run(() => store.Delete(id)); selectedId = null;
                if (disposed) return; ApplyItems(BuildItems(items.Where(tile => tile.Id != id).Select(tile => (SoundboardSound)tile.Tag).ToList())); Ui.Status(status, "Sound deleted. Your original files are unchanged.");
            }
            catch (Exception error) { if (!disposed) Ui.Status(status, error.Message, true); }
            finally { libraryBusy = false; if (!disposed) UpdateControls(); }
        }
        private async Task StopSounds()
        {
            if (preview || disposed) return;
            try { await audio.StopClipsAsync(); if (!disposed) Ui.Status(status, audio.IsRouting ? "Sounds stopped. Your microphone is still shared with calls." : "Sounds stopped."); }
            catch (Exception error) { if (!disposed) Ui.Status(status, error.Message, true); }
        }
        private async Task LoadListeningOutputs()
        {
            if (preview || audioBusy || disposed || audio.IsRouting) return; audioBusy = true; UpdateControls();
            try
            {
                var outputs = await Task.Run(listeningDevices); if (disposed) return;
                var choice = outputs.Find(device => device.Id == listeningId) ?? outputs.FirstOrDefault();
                bindingOutput = true; listeningOutput.ItemsSource = outputs; listeningOutput.SelectedItem = choice; bindingOutput = false;
                listeningId = choice == null ? null : choice.Id;
                await audio.SetListeningOutputAsync(listeningId);
            }
            catch (Exception error) { if (!disposed) Ui.Status(status, error.Message, true); }
            finally { bindingOutput = false; audioBusy = false; if (!disposed) UpdateControls(); }
        }
        private async Task ChangeListeningOutput()
        {
            if (preview || busy || disposed || audio.IsRouting) return;
            var choice = listeningOutput.SelectedItem as SoundboardDevice; if (choice == null) return;
            settings.ListeningOutputId = choice.Id; QueueSave();
            audioBusy = true; UpdateControls();
            try { await audio.SetListeningOutputAsync(choice.Id); listeningId = choice.Id; if (!disposed) Ui.Status(status, "Sounds will play through “" + choice.Name + "”. Press Test output to check."); }
            catch (Exception error) { if (!disposed) Ui.Status(status, error.Message, true); }
            finally { audioBusy = false; if (!disposed) UpdateControls(); }
        }
        private async Task TestOutput()
        {
            if (preview || busy || disposed) return; audioBusy = true; UpdateControls();
            try { await audio.TestLocalOutputAsync(); if (!disposed) Ui.Status(status, "Test tone sent to “" + audio.LocalOutputName + "”. You should hear a short beep."); }
            catch (Exception error) { if (!disposed) Ui.Status(status, "Unable to test this output: " + error.Message, true); }
            finally { audioBusy = false; if (!disposed) UpdateControls(); }
        }
        private async Task LoadDevices()
        {
            if (preview || audioBusy || disposed || audio.IsRouting) return; audioBusy = true; UpdateControls();
            try
            {
                var devices = await Task.Run(callDevices); if (disposed) return;
                bindingDevices = true;
                microphone.ItemsSource = devices.Microphones; virtualOutput.ItemsSource = devices.VirtualOutputs;
                microphone.SelectedItem = string.IsNullOrEmpty(settings.MicrophoneId) ? devices.Microphones.FirstOrDefault() : devices.Microphones.Find(device => device.Id == settings.MicrophoneId);
                virtualOutput.SelectedItem = string.IsNullOrEmpty(settings.VirtualOutputId) ? devices.VirtualOutputs.FirstOrDefault() : devices.VirtualOutputs.Find(device => device.Id == settings.VirtualOutputId);
                bindingDevices = false;
                Ui.Status(callStatus, devices.VirtualOutputs.Count == 0 ? "No supported virtual output found. Install VB-CABLE or Voicemeeter, then refresh devices." : devices.Microphones.Count == 0 ? "Connect a microphone and refresh devices." : microphone.SelectedItem == null || virtualOutput.SelectedItem == null ? "A saved device is unavailable. Reconnect it or choose another device." : "Call audio is off. Press Start call audio to share your microphone and sounds.");
            }
            catch (Exception error) { if (!disposed) Ui.Status(callStatus, error.Message, true); }
            finally { bindingDevices = false; audioBusy = false; if (!disposed) UpdateControls(); }
        }
        private void ChangeCallDevices()
        {
            UpdateControls(); if (preview || disposed || bindingDevices || busy) return;
            var mic = microphone.SelectedItem as SoundboardDevice; var output = virtualOutput.SelectedItem as SoundboardDevice;
            settings.MicrophoneId = mic == null ? null : mic.Id; settings.VirtualOutputId = output == null ? null : output.Id; QueueSave();
        }
        private async Task StartCalls()
        {
            if (preview || audioBusy || disposed || audio.IsRouting) return;
            var mic = microphone.SelectedItem as SoundboardDevice; var output = virtualOutput.SelectedItem as SoundboardDevice; if (mic == null || output == null) return;
            bool previouslyEnabled = settings.CallAudioEnabled;
            settings.MicrophoneId = mic.Id; settings.VirtualOutputId = output.Id; settings.CallAudioEnabled = true; settingsDirty = true; SaveSettings();
            audioBusy = true; UpdateControls(); Ui.Status(callStatus, "Starting call audio…");
            try
            {
                await audio.StartRoutingAsync(mic.Id, output.Id);
                if (!disposed)
                {
                    Ui.Status(callStatus, "Call audio is on · “" + mic.Name + "” + sounds → “" + output.Name + "”. Restores on startup.");
                }
            }
            catch (Exception error) { if (!disposed) { settings.CallAudioEnabled = previouslyEnabled; settingsDirty = true; SaveSettings(); Ui.Status(callStatus, error.Message, true); } }
            finally { audioBusy = false; if (!disposed) UpdateControls(); }
        }
        private async Task StopCalls()
        {
            if (preview || busy || disposed) return; audioBusy = true; UpdateControls();
            settings.CallAudioEnabled = false; settingsDirty = true; SaveSettings();
            try { await audio.StopRoutingAsync(); if (!disposed) Ui.Status(callStatus, "Call audio stopped. Switch your call apps back to your usual microphone."); }
            catch (Exception error) { if (!disposed) Ui.Status(callStatus, error.Message, true); }
            finally { audioBusy = false; if (!disposed) UpdateControls(); }
        }
        private void AudioStopped(string error)
        { if (!disposed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (disposed) return; Ui.Status(callStatus, error, true); Ui.Status(status, error, true); UpdateControls(); })); }
        private void PlaybackFinished()
        { if (!disposed && !Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(new Action(() => { if (disposed || audio.IsPlaying) return; Ui.Status(status, audio.IsRouting ? "Playback finished. Your microphone is still shared with calls." : "Playback finished."); UpdateControls(); })); }
        private void UpdateVolume()
        {
            int percent = (int)Math.Round(volume.Value); volumePercent.Text = percent + "%";
            System.Windows.Automation.AutomationProperties.SetHelpText(volume, percent + " percent. Adjusts soundboard sounds only.");
            if (!preview && !disposed)
            {
                float value = (float)(volume.Value / 100); audio.SetVolume(value);
                if (settings.Volume != value) { settings.Volume = value; QueueSave(); }
            }
        }
        private void QueueSave()
        { if (preview || disposed) return; settingsDirty = true; saveTimer.Stop(); saveTimer.Start(); }
        private void SaveSettings()
        {
            saveTimer.Stop(); if (preview || !settingsDirty) return;
            try { settingsStore.Save(settings); settingsDirty = false; }
            catch (Exception error) { Ui.Status(status, "Unable to save soundboard settings: " + error.Message, true); }
        }
        private void UpdateControls()
        {
            bool enabled = !preview && !busy && !disposed;
            loading.Visibility = libraryBusy ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = !libraryBusy && items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            create.IsEnabled = enabled && libraryAvailable; delete.IsEnabled = enabled && selectedId != null; stop.IsEnabled = enabled && items.Count > 0;
            // Session creation holds the backend lock while Windows opens audio
            // devices. Avoid waiting for that lock on the UI during startup.
            bool routing = audioBusy ? lastRouting : (lastRouting = audio.IsRouting); startCalls.IsEnabled = enabled && !routing && microphone.SelectedItem != null && virtualOutput.SelectedItem != null; stopCalls.IsEnabled = enabled && (routing || settings.CallAudioEnabled);
            refresh.IsEnabled = microphone.IsEnabled = virtualOutput.IsEnabled = enabled && !routing;
            listeningOutput.IsEnabled = refreshOutputs.IsEnabled = enabled && !routing; testOutput.IsEnabled = enabled && listeningOutput.SelectedItem != null;
            volume.IsEnabled = !preview && !disposed && !audioBusy;
            foreach (var item in items) item.Editable = !busy && !disposed;
        }
        public void Dispose() { if (disposed) return; SaveSettings(); disposed = true; ++artworkVersion; artworkCache.Clear(); audio.Stopped -= AudioStopped; audio.PlaybackFinished -= PlaybackFinished; audio.Dispose(); }
        private sealed class PlaySoundCommand : ICommand
        {
            private readonly Action<TileItem> play;
            internal PlaySoundCommand(Action<TileItem> play) { this.play = play; }
            // Command is bound before its parameter during tile creation. Returning
            // false for that temporary null parameter leaves WPF's enabled state stale.
            // Editable controls availability; Play also guards loading/disposal.
            public bool CanExecute(object parameter) { return true; }
            public void Execute(object parameter) { play(parameter as TileItem); }
            public event EventHandler CanExecuteChanged { add { } remove { } }
        }
    }
}
