using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class SoundPage : Grid, IDisposable
    {
        private readonly bool preview;
        private readonly ComboBox source = new ComboBox { MinWidth = 320, Margin = new Thickness(16, 0, 0, 0) };
        internal readonly TileGrid Tiles = new TileGrid();
        private readonly List<AudioOutputDevice> devices = new List<AudioOutputDevice>();
        private readonly List<TileItem> items = new List<TileItem>();
        private readonly AudioMirrorService mirror = new AudioMirrorService();
        private readonly global::Wpf.Ui.Controls.Button start = Ui.Button("Start multiple outputs", true), stop = Ui.Button("Stop"), refresh = Ui.Button("Refresh devices");
        private readonly TextBlock status = Ui.Text("Open this tab to load your audio outputs.", 13, true);
        private bool busy, loaded, disposed, rebuilding;
        internal bool NeedsLoading { get { return !loaded && !busy; } }
        internal SoundPage(bool preview)
        {
            this.preview = preview; Margin = new Thickness(32, 28, 32, 24);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Children.Add(Ui.Heading("Multiple sound outputs", "Listen through your speakers and headphones at the same time."));
            var primary = new DockPanel(); var label = Ui.Text("Primary output"); label.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(label, Dock.Left); primary.Children.Add(label); primary.Children.Add(source);
            var card = Ui.Card(primary, new Thickness(20)); card.Margin = new Thickness(0, 0, 0, 18); Grid.SetRow(card, 1); Children.Add(card);
            var hint = Ui.Text("Select additional outputs. The primary device is already playing.", 13, true); hint.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(hint, 2); Children.Add(hint);
            Grid.SetRow(Tiles, 3); Children.Add(Tiles);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 16) }; buttons.Children.Add(start); buttons.Children.Add(stop); buttons.Children.Add(refresh); Grid.SetRow(buttons, 4); Children.Add(buttons);
            status.MinHeight = 40; Grid.SetRow(status, 5); Children.Add(status);
            source.SelectionChanged += delegate { if (!rebuilding) Rebuild(); };
            refresh.Click += async delegate { await LoadDevices(); }; start.Click += async delegate { await Start(); }; stop.Click += async delegate { await Stop(); }; mirror.Stopped += OnStopped; UpdateControls();
        }
        internal async Task LoadDevices()
        {
            if (busy || disposed || mirror.IsRunning) return; busy = true; UpdateControls(); Ui.Status(status, "Loading audio outputs…");
            string previous = (source.SelectedItem as AudioOutputDevice)?.Id;
            try
            {
                var available = preview ? new List<AudioOutputDevice> { new AudioOutputDevice("primary", "Speakers", true), new AudioOutputDevice("headphones", "Wireless headphones", false, 3), new AudioOutputDevice("monitor", "Display audio", false, 7) } : await Task.Run(() => WindowsAudio.GetOutputs());
                if (disposed) return; devices.Clear(); devices.AddRange(available); rebuilding = true; source.ItemsSource = null; source.ItemsSource = devices;
                source.SelectedItem = devices.Find(device => device.Id == previous) ?? devices.Find(device => device.IsDefault) ?? devices.FirstOrDefault(); rebuilding = false; Rebuild(); loaded = true;
                Ui.Status(status, devices.Count < 2 ? "Connect another audio output, then refresh." : "Select additional devices, then start multiple outputs.");
            }
            catch (Exception error) { if (!disposed) Ui.Status(status, error.Message, true); }
            finally { rebuilding = false; busy = false; if (!disposed) UpdateControls(); }
        }
        private void Rebuild()
        {
            var chosen = new HashSet<string>(items.Where(item => item.Selected).Select(item => item.Id)); var primary = source.SelectedItem as AudioOutputDevice; items.Clear();
            foreach (var device in devices)
            { if (primary != null && device.Id == primary.Id) continue; var item = new TileItem(device.Id, device.Name, device, chosen.Contains(device.Id), device.FormFactor == 3 || device.FormFactor == 5 ? "\uE7F6" : device.FormFactor == 7 ? "\uE7F4" : "\uE767"); item.SelectionChanged += value => UpdateControls(); items.Add(item); }
            Tiles.SetItems(items); UpdateControls();
        }
        private async Task Start()
        {
            if (busy || mirror.IsRunning) return; var primary = source.SelectedItem as AudioOutputDevice; var ids = items.Where(item => item.Selected).Select(item => item.Id).ToList(); if (primary == null || ids.Count == 0) return;
            if (preview) { Ui.Status(status, "Preview only: audio routing was not changed."); return; }
            busy = true; UpdateControls(); Ui.Status(status, "Starting audio mirroring…");
            try { await mirror.StartAsync(primary.Id, ids); if (!disposed) Ui.Status(status, "Playing on " + ids.Count + " additional output" + (ids.Count == 1 ? "." : "s.")); }
            catch (Exception error) { if (!disposed) Ui.Status(status, error.Message, true); }
            finally { busy = false; if (!disposed) UpdateControls(); }
        }
        private async Task Stop()
        {
            if (busy || !mirror.IsRunning) return; busy = true; UpdateControls();
            try { await mirror.StopAsync(); if (!disposed) Ui.Status(status, "Stopped. Windows keeps playing on its original output."); }
            catch (Exception error) { if (!disposed) Ui.Status(status, error.Message, true); }
            finally { busy = false; if (!disposed) UpdateControls(); }
        }
        private void OnStopped(string error)
        { if (disposed || Dispatcher.HasShutdownStarted) return; Dispatcher.BeginInvoke(new Action(() => { if (disposed || mirror.IsRunning) return; if (error != null) Ui.Status(status, error, true); UpdateControls(); })); }
        private void UpdateControls()
        { bool editable = !busy && !mirror.IsRunning; source.IsEnabled = refresh.IsEnabled = editable; foreach (var item in items) if (item.Editable != editable) item.Editable = editable; start.IsEnabled = editable && source.SelectedItem != null && items.Any(item => item.Selected); stop.IsEnabled = !busy && mirror.IsRunning; }
        public void Dispose() { if (disposed) return; disposed = true; mirror.Stopped -= OnStopped; mirror.Dispose(); }
    }
}
