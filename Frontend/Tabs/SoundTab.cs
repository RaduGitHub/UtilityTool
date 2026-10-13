using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Tabs
{
    internal sealed class SoundTab : BufferedTabPage
    {
        private readonly ComboBox source = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Dock = DockStyle.Fill };
        private readonly AudioDevicePicker additional = new AudioDevicePicker();
        private readonly ModernButton start = new ModernButton { Text = "Start multiple outputs", AutoSize = true };
        private readonly ModernButton stop = new ModernButton { Text = "Stop", AutoSize = true };
        private readonly ModernButton refresh = new ModernButton { Text = "Refresh devices", AutoSize = true };
        private readonly Label status = new Label { Dock = DockStyle.Fill, Text = "Open this tab to load your audio devices." };
        private readonly AudioMirrorService mirror = new AudioMirrorService();
        private readonly List<AudioOutputDevice> devices = new List<AudioOutputDevice>();
        private bool busy, running, loaded, updating;
        internal bool NeedsLoading { get { return !loaded && !busy; } }
        internal SoundTab() : base("Sound")
        {
            BackColor = AppTheme.Surface;
            source.AccessibleName = "Primary Windows audio output";
            var layout = new FrostedLayout { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 8 };
            foreach (int height in new[] { 50, 32, 42, 24 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.Controls.Add(new Label { Text = "Multiple sound outputs", Font = new Font("Segoe UI", 24, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
            layout.Controls.Add(new Label { Text = "Select the speakers or headphones you want to hear together.", Dock = DockStyle.Fill }, 0, 1);
            var primaryRow = new BufferedLayout { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = AppTheme.Surface };
            primaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            primaryRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            primaryRow.Controls.Add(new Label { Text = "Primary output", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
            source.Margin = new Padding(0, 8, 0, 8); primaryRow.Controls.Add(source, 1, 0);
            layout.Controls.Add(primaryRow, 0, 2);
            layout.Controls.Add(new Label { Text = "Click a device to select it. The primary output is already playing.", Dock = DockStyle.Fill, ForeColor = AppTheme.Muted }, 0, 3);
            additional.Margin = Padding.Empty;
            layout.Controls.Add(additional, 0, 4);
            var actions = new BufferedLayout { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 10, 0, 10), BackColor = AppTheme.Surface };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
            actions.Controls.Add(start, 0, 0); actions.Controls.Add(stop, 1, 0); actions.Controls.Add(refresh, 3, 0);
            layout.Controls.Add(actions, 0, 5);
            layout.Controls.Add(new Label { Text = "Keep Windows and your apps playing through the primary output. UtilityTool must stay open.\nBluetooth and other outputs may have a delay.", ForeColor = AppTheme.Muted, Dock = DockStyle.Fill, Margin = Padding.Empty }, 0, 6);
            status.Margin = new Padding(0, 5, 0, 0); layout.Controls.Add(status, 0, 7);
            Controls.Add(layout); AppTheme.Apply(this);
            AppTheme.UseOpaqueSurfaces(layout);
            source.BackColor = AppTheme.Panel; source.ForeColor = AppTheme.Ink;
            source.DrawMode = DrawMode.OwnerDrawFixed;
            source.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var background = new SolidBrush(selected ? AppTheme.Accent : AppTheme.Panel)) e.Graphics.FillRectangle(background, e.Bounds);
                string text = e.Index >= 0 ? source.Items[e.Index].ToString() : "No audio outputs available";
                TextRenderer.DrawText(e.Graphics, text, source.Font, Rectangle.Inflate(e.Bounds, -3, 0),
                    !source.Enabled ? AppTheme.Muted : selected ? AppTheme.AccentInk : AppTheme.Ink,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            foreach (var button in new[] { start, stop, refresh })
            {
                button.AutoSize = false; button.Dock = DockStyle.Fill;
                button.Margin = button == refresh ? Padding.Empty : new Padding(0, 0, 10, 0);
            }
            start.Primary = true;
            source.SelectedIndexChanged += delegate { if (!updating) { RebuildAdditional(); UpdateControls(); } };
            additional.SelectionChanged += delegate { if (!updating) UpdateControls(); };
            refresh.Click += async delegate { await LoadDevices(); };
            start.Click += async delegate { await StartMirroring(); };
            stop.Click += async delegate { await StopMirroring(); };
            mirror.Stopped += OnStopped;
            UpdateControls();
        }
        internal async Task LoadDevices()
        {
            if (busy || running) return;
            var previous = source.SelectedItem as AudioOutputDevice;
            busy = true; UpdateControls(); SetStatus("Loading audio output devices...", false);
            try
            {
                var available = await Task.Run(() => WindowsAudio.GetOutputs());
                if (IsDisposed) return;
                devices.Clear(); devices.AddRange(available);
                updating = true;
                source.Items.Clear();
                AudioOutputDevice selected = null;
                foreach (var device in devices)
                {
                    source.Items.Add(device);
                    if (previous != null && device.Id == previous.Id) selected = device;
                }
                if (selected == null) selected = devices.Find(device => device.IsDefault);
                source.SelectedItem = selected;
                updating = false; RebuildAdditional(); loaded = true;
                SetStatus(devices.Count == 0 ? "No speakers or headphones found. Connect a device, then refresh."
                    : selected == null ? "Set speakers or headphones as Windows' default output, then refresh. Use virtual cable devices in Soundboard."
                    : devices.Count == 1 ? "Only one output is available. Connect another speaker or headphones, then refresh."
                    : "Choose one or more additional outputs, then press Start multiple outputs.", false);
            }
            catch (Exception error) { if (!IsDisposed) SetStatus("Could not load audio devices: " + error.Message + " Press Refresh devices to try again.", true); }
            finally { updating = false; if (!IsDisposed) { busy = false; UpdateControls(); } }
        }
        private void RebuildAdditional()
        {
            var primary = source.SelectedItem as AudioOutputDevice;
            updating = true;
            try { additional.SetDevices(devices, primary == null ? null : primary.Id); }
            finally { updating = false; }
        }
        private async Task StartMirroring()
        {
            if (busy || running) return;
            var primary = source.SelectedItem as AudioOutputDevice;
            if (primary == null || additional.SelectedDevices.Count == 0) { SetStatus("Choose a primary output and at least one additional output.", true); return; }
            var ids = new List<string>();
            foreach (AudioOutputDevice device in additional.SelectedDevices) ids.Add(device.Id);
            busy = true; UpdateControls(); SetStatus("Starting audio mirroring...", false);
            try
            {
                await mirror.StartAsync(primary.Id, ids);
                if (IsDisposed) return;
                running = true;
                SetStatus("Playing " + primary.Name + " on " + ids.Count + " additional output" + (ids.Count == 1 ? "." : "s."), false);
            }
            catch (Exception error) { if (!IsDisposed) { running = false; SetStatus(error.Message, true); } }
            finally { if (!IsDisposed) { busy = false; UpdateControls(); } }
        }
        private async Task StopMirroring()
        {
            if (busy || !running) return;
            busy = true; UpdateControls();
            try { await mirror.StopAsync(); if (!IsDisposed) { running = false; SetStatus("Stopped. Windows continues playing through its original output.", false); } }
            catch (Exception error) { if (!IsDisposed) SetStatus(error.Message, true); }
            finally { if (!IsDisposed) { busy = false; UpdateControls(); } }
        }
        private void OnStopped(string error)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (IsDisposed || mirror.IsRunning) return;
                    running = false;
                    if (error != null) SetStatus(error, true);
                    UpdateControls();
                });
            }
            catch (InvalidOperationException) { }
        }
        private void SetStatus(string text, bool error) { status.Text = text; status.ForeColor = error ? AppTheme.Error : AppTheme.Accent; }
        private void UpdateControls()
        {
            source.Enabled = additional.Enabled = refresh.Enabled = !busy && !running;
            start.Enabled = !busy && !running && source.SelectedItem != null && additional.SelectedDevices.Count > 0;
            stop.Enabled = !busy && running;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { mirror.Stopped -= OnStopped; mirror.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
