using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using UtilityTool.Backend;
namespace UtilityTool.Frontend
{
    internal sealed class AudioDevicePicker : TilePicker
    {
        internal readonly List<AudioDeviceTile> Items = new List<AudioDeviceTile>();
        private readonly Dictionary<string, Bitmap> deviceIcons = new Dictionary<string, Bitmap>();
        private readonly TileIconCache displayIcons = new TileIconCache();
        internal event EventHandler SelectionChanged;
        protected override int TileCount { get { return Items == null ? 0 : Items.Count; } }
        protected override string TileName(int index) { return Items[index].Device.Name; }
        protected override bool TileChecked(int index) { return Items[index].Checked; }
        protected override bool TileCanToggle(int index) { return !Items[index].IsPrimary; }
        protected override void ToggleTile(int index) { Items[index].Checked = !Items[index].Checked; }
        protected override string TileTooltip(int index)
        {
            var item = Items[index];
            return item.Device.Name + (item.IsPrimary ? "\nPrimary output: already plays the original audio." : "\nClick or press Space to include this output.")
                + (item.Device.IsDefault ? "\nWindows default output." : "");
        }
        internal AudioDevicePicker() { Dock = DockStyle.Fill; AccessibleName = "Audio output devices"; }
        internal List<AudioOutputDevice> SelectedDevices
        {
            get
            {
                var selected = new List<AudioOutputDevice>();
                foreach (var item in Items) if (item.Checked && !item.IsPrimary) selected.Add(item.Device);
                return selected;
            }
        }
        internal void SetDevices(IEnumerable<AudioOutputDevice> devices, string primaryId)
        {
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in SelectedDevices) selected.Add(device.Id);
            BeginUpdate();
            try
            {
                Items.Clear();
                foreach (var device in devices)
                {
                    bool primary = string.Equals(device.Id, primaryId, StringComparison.OrdinalIgnoreCase);
                    Items.Add(new AudioDeviceTile(this, device, primary, Items.Count, primary || selected.Contains(device.Id)));
                    GetDeviceIcon(device); // Cache vector drawing once, before painting or resizing.
                }
            }
            finally { EndUpdate(); }
            RaiseSelectionChanged();
        }
        internal void ChangeCheck(AudioDeviceTile item, bool value)
        {
            if (item.IsPrimary || item.IsChecked == value || item.Index >= Items.Count || Items[item.Index] != item) return;
            item.IsChecked = value; CheckChanged(item.Index); RaiseSelectionChanged();
        }
        private void RaiseSelectionChanged() { var handler = SelectionChanged; if (handler != null) handler(this, EventArgs.Empty); }
        protected override void DrawTile(Graphics graphics, int index, Rectangle bounds, bool hovered, bool focused, TileTextStyle textStyle)
        {
            var item = Items[index]; var icon = GetDeviceIcon(item.Device);
            SelectionTileRenderer.Draw(graphics, bounds, textStyle, item.Device.Name, item.Checked, hovered,
                focused, Enabled, !item.IsPrimary, item.IsPrimary ? "PRIMARY OUTPUT" : item.Checked ? "SELECTED" : "",
                delegate(Graphics iconGraphics) { displayIcons.Draw(iconGraphics, icon); });
        }
        private Bitmap GetDeviceIcon(AudioOutputDevice device)
        {
            uint kind = device.FormFactor == 9 || device.FormFactor == 3 || device.FormFactor == 5 ? device.FormFactor : 1;
            bool bluetooth = device.Name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 || device.Name.IndexOf("BT Adapter", StringComparison.OrdinalIgnoreCase) >= 0;
            string key = kind + "|" + bluetooth; Bitmap image;
            if (deviceIcons.TryGetValue(key, out image)) return image;
            image = new Bitmap(120, 120);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias; graphics.ScaleTransform(2, 2);
                DrawDeviceIcon(graphics, kind, bluetooth ? "Bluetooth" : "");
            }
            deviceIcons[key] = image; return image;
        }
        private static void DrawDeviceIcon(Graphics g, uint formFactor, string name)
        {
            using (var outline = new Pen(Color.FromArgb(167, 183, 205), 1.5f))
            using (var body = new LinearGradientBrush(new Rectangle(0, 0, 60, 60), Color.FromArgb(205, 219, 236), Color.FromArgb(91, 112, 141), 90f))
            using (var dark = new SolidBrush(Color.FromArgb(30, 40, 54)))
            using (var blue = new LinearGradientBrush(new Rectangle(0, 0, 60, 60), Color.FromArgb(108, 182, 255), Color.FromArgb(45, 83, 162), 45f))
            {
                if (formFactor == 9)
                {
                    g.FillRectangle(body, 3, 6, 54, 37); g.DrawRectangle(outline, 3, 6, 54, 37);
                    g.FillRectangle(blue, 7, 10, 46, 28);
                    using (var shine = new SolidBrush(Color.FromArgb(55, 255, 255, 255))) g.FillPolygon(shine, new[] { new Point(7, 10), new Point(32, 10), new Point(7, 34) });
                    g.FillRectangle(body, 26, 43, 8, 8); g.FillRectangle(body, 17, 51, 26, 4);
                }
                else if (formFactor == 3 || formFactor == 5)
                {
                    using (var band = new Pen(Color.FromArgb(183, 201, 224), 7)) g.DrawArc(band, 8, 6, 44, 44, 180, 180);
                    g.FillRectangle(dark, 6, 29, 13, 24); g.FillRectangle(dark, 41, 29, 13, 24);
                    g.FillRectangle(body, 9, 30, 8, 21); g.FillRectangle(body, 43, 30, 8, 21);
                    if (formFactor == 5) { g.DrawArc(outline, 27, 37, 23, 17, 0, 90); g.FillEllipse(dark, 27, 51, 8, 4); }
                }
                else
                {
                    foreach (int x in new[] { 7, 34 })
                    {
                        g.FillRectangle(body, x, 6, 20, 49); g.DrawRectangle(outline, x, 6, 20, 49);
                        g.FillEllipse(dark, x + 5, 13, 10, 10); g.FillEllipse(dark, x + 3, 31, 14, 14);
                        using (var cone = new SolidBrush(Color.FromArgb(73, 89, 112))) g.FillEllipse(cone, x + 6, 34, 8, 8);
                    }
                }
                if (name.IndexOf("Bluetooth", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("BT Adapter", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    g.FillEllipse(blue, 40, 38, 19, 22);
                    using (var mark = new Pen(Color.White, 1.5f))
                        g.DrawLines(mark, new[] { new Point(47, 43), new Point(53, 49), new Point(49, 53), new Point(49, 41), new Point(53, 45), new Point(47, 55) });
                }
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { displayIcons.Dispose(); foreach (var image in deviceIcons.Values) image.Dispose(); deviceIcons.Clear(); }
            base.Dispose(disposing);
        }
    }
    internal sealed class AudioDeviceTile
    {
        private readonly AudioDevicePicker owner;
        internal readonly AudioOutputDevice Device;
        internal readonly bool IsPrimary;
        internal readonly int Index;
        internal bool IsChecked;
        internal Rectangle Bounds { get { return owner.ItemBounds(Index); } }
        internal bool Checked { get { return IsChecked; } set { owner.ChangeCheck(this, value); } }
        internal AudioDeviceTile(AudioDevicePicker picker, AudioOutputDevice device, bool primary, int index, bool selected)
        { owner = picker; Device = device; IsPrimary = primary; Index = index; IsChecked = selected; }
    }
}
