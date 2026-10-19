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
        private readonly Func<List<AudioOutputDevice>> readDevices;
        private AudioOutputDevice primary;
        internal readonly TileGrid Tiles = new TileGrid();
        private readonly List<AudioOutputDevice> devices = new List<AudioOutputDevice>();
        private readonly List<TileItem> items = new List<TileItem>();
        private readonly AudioMirrorService mirror = new AudioMirrorService();
        private readonly global::Wpf.Ui.Controls.Button start = Ui.Button("Start multiple outputs", true), stop = Ui.Button("Stop"), refresh = Ui.Button("Refresh devices");
        private readonly TextBlock status = Ui.Text("Open this tab to load your audio outputs.", 13, true);
        private readonly TextBlock empty = Ui.Text("Open this tab to load your speakers and headphones.", 15, true);
        private bool busy, loaded, disposed;
        internal bool NeedsLoading { get { return !loaded && !busy; } }
        internal SoundPage(bool preview, Func<List<AudioOutputDevice>> readDevices = null)
        {
            this.preview = preview; this.readDevices = readDevices ?? WindowsAudio.GetOutputs; Margin = new Thickness(32, 28, 32, 24);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition()); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Children.Add(Ui.Heading("Multiple sound outputs", "Listen through your speakers and headphones at the same time."));
            var hint = Ui.Text("Windows' default output is marked as primary. Select additional outputs to play alongside it.", 13, true); hint.Margin = new Thickness(0, 0, 0, 8); Grid.SetRow(hint, 1); Children.Add(hint);
            var outputs = new Grid(); outputs.Children.Add(Tiles);
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; empty.TextAlignment = TextAlignment.Center; empty.MaxWidth = 600; empty.Margin = new Thickness(24); outputs.Children.Add(empty);
            Grid.SetRow(outputs, 2); Children.Add(outputs);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 16) }; buttons.Children.Add(start); buttons.Children.Add(stop); buttons.Children.Add(refresh); Grid.SetRow(buttons, 3); Children.Add(buttons);
            status.MinHeight = 40; Grid.SetRow(status, 4); Children.Add(status);
            refresh.Click += async delegate { await LoadDevices(); }; start.Click += async delegate { await Start(); }; stop.Click += async delegate { await Stop(); }; mirror.Stopped += OnStopped; UpdateControls();
        }
        internal async Task<bool> LoadDevices()
        {
            if (busy || disposed || mirror.IsRunning) return false; busy = true; UpdateControls(); ShowStatus("Loading speakers and headphones…");
            try
            {
                var available = preview ? new List<AudioOutputDevice> { new AudioOutputDevice("primary", "Speakers", true), new AudioOutputDevice("headphones", "Wireless headphones", false, 3), new AudioOutputDevice("monitor", "Display audio", false, 9) } : await Task.Run(readDevices);
                if (disposed) return false; ApplyDevices(available);
                return true;
            }
            catch (Exception error) { if (!disposed) ShowStatus("Unable to load audio outputs: " + error.Message + " Press Refresh devices to try again.", true); return false; }
            finally { busy = false; if (!disposed) UpdateControls(); }
        }
        internal void ApplyDevices(List<AudioOutputDevice> available)
        {
            devices.Clear(); devices.AddRange(available.Where(device => !WindowsAudio.IsVirtualOutput(device.Name))); primary = devices.Find(device => device.IsDefault); Rebuild(); loaded = true;
            ShowStatus(devices.Count == 0 ? "No speakers or headphones found. Connect an output, then press Refresh devices."
                : primary == null ? "Set speakers or headphones as Windows' default output, then refresh. Use virtual cable devices in Soundboard."
                : devices.Count < 2 ? "Connect another speaker or headphones, then refresh." : "Select additional devices, then start multiple outputs.", primary == null);
        }
        private void ShowStatus(string message, bool error = false)
        {
            Ui.Status(status, message, error); empty.Text = message;
            empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        private void Rebuild()
        {
            var chosen = new HashSet<string>(items.Where(item => item.Selected && !item.IsPrimary).Select(item => item.Id)); items.Clear();
            foreach (var device in devices)
            {
                bool isPrimary = primary != null && device.Id == primary.Id;
                var item = new TileItem(device.Id, device.Name, device, chosen.Contains(device.Id), device.FormFactor == 3 || device.FormFactor == 5 ? "\uE7F6" : device.FormFactor == 9 ? "\uE7F4" : "\uE767", isPrimary, "SECONDARY OUTPUT");
                item.SelectionChanged += value => UpdateControls(); items.Add(item);
            }
            Tiles.SetItems(items); UpdateControls();
        }
        private async Task Start()
        {
            if (busy || disposed || mirror.IsRunning) return;
            // Re-read Windows' default before starting; it may have changed since this tab opened.
            if (!await LoadDevices()) return;
            var ids = SecondaryIds(); if (primary == null || ids.Count == 0) return;
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
        { bool editable = !busy && !mirror.IsRunning; refresh.IsEnabled = editable; foreach (var item in items) if (item.Editable != (editable && !item.IsPrimary)) item.Editable = editable && !item.IsPrimary; start.IsEnabled = editable && primary != null && SecondaryIds().Count > 0; stop.IsEnabled = !busy && mirror.IsRunning; }
        internal List<string> SecondaryIds() { return items.Where(item => item.Selected && !item.IsPrimary).Select(item => item.Id).ToList(); }
        public void Dispose() { if (disposed) return; disposed = true; mirror.Stopped -= OnStopped; mirror.Dispose(); }
    }
}
