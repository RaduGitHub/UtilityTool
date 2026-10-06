using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
namespace UtilityTool.Frontend
{
    // Both pickers use the same spacing, labels, hover and selection treatment.
    internal static class SelectionTileRenderer
    {
        internal static void Draw(Graphics graphics, Rectangle area, TileTextStyle textStyle, string name, bool selected,
            bool hovered, bool focused, bool enabled, bool showBadge, string caption, Action<Graphics> drawIcon)
        {
            var state = graphics.Save();
            try
            {
                graphics.TranslateTransform(area.X, area.Y); graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                int width = area.Width, height = area.Height;
                // Window resizing changes the box, never the icon, font or selection badge.
                // Scale only for the monitor's Windows DPI setting.
                float scale = graphics.DpiX / 96f;
                var bounds = new Rectangle(1, 1, width - 3, height - 3);
                bool highlighted = selected || hovered || focused;
                Color background = selected ? Color.FromArgb(46, 62, 86) : highlighted ? Color.FromArgb(49, 57, 71) : AppTheme.Panel;
                using (var shape = AppTheme.Rounded(bounds, 7))
                using (var fill = new SolidBrush(background))
                {
                    graphics.FillPath(fill, shape);
                    if (highlighted)
                        using (var border = new Pen(selected ? AppTheme.Accent : Color.FromArgb(71, 82, 103)))
                            graphics.DrawPath(border, shape);
                }
                var iconState = graphics.Save();
                graphics.TranslateTransform((float)Math.Round((width - 60 * scale) / 2), (float)Math.Round(8 * scale)); graphics.ScaleTransform(scale, scale);
                drawIcon(graphics); graphics.Restore(iconState);
                if (selected && showBadge)
                {
                    var check = new Rectangle(width - (int)(25 * scale), (int)(7 * scale), (int)(18 * scale), (int)(18 * scale));
                    using (var fill = new SolidBrush(AppTheme.Accent)) graphics.FillEllipse(fill, check);
                    using (var pen = new Pen(AppTheme.AccentInk, 2 * scale))
                        graphics.DrawLines(pen, new[] { new PointF(check.X + 4 * scale, check.Y + 9 * scale), new PointF(check.X + 8 * scale, check.Y + 12 * scale), new PointF(check.X + 14 * scale, check.Y + 5 * scale) });
                }
                // Keep name wrapping fixed too, so changing only the box never rerasterizes its labels.
                int labelWidth = Math.Min(width, (int)Math.Round(144 * scale));
                int labelLeft = (width - labelWidth) / 2;
                textStyle.DrawLabel(graphics, name, new Rectangle(labelLeft, (int)Math.Round(73 * scale), labelWidth, (int)Math.Round(51 * scale)),
                    false, enabled ? AppTheme.Ink : AppTheme.Muted, background);
                textStyle.DrawLabel(graphics, caption, new Rectangle(labelLeft, height - (int)Math.Round(21 * scale), labelWidth, (int)Math.Round(18 * scale)),
                    true, AppTheme.Accent, background);
                if (focused) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(bounds, -4, -4), AppTheme.Ink, AppTheme.Panel);
            }
            finally { graphics.Restore(state); }
        }
    }
    internal sealed class TileTextStyle : IDisposable
    {
        internal readonly Font NameFont, CaptionFont;
        private readonly DrawingBitmapCache<LabelKey> labels = new DrawingBitmapCache<LabelKey>();
        private struct LabelKey : IEquatable<LabelKey>
        {
            internal string Text;
            internal Size Size;
            internal int Ink, Background, Dpi;
            internal bool Caption;
            public bool Equals(LabelKey other)
            {
                return Text == other.Text && Size == other.Size && Ink == other.Ink && Background == other.Background && Dpi == other.Dpi && Caption == other.Caption;
            }
            public override bool Equals(object value) { return value is LabelKey && Equals((LabelKey)value); }
            public override int GetHashCode()
            {
                unchecked { return (((((Text.GetHashCode() * 397 ^ Size.GetHashCode()) * 397 ^ Ink) * 397 ^ Background) * 397 ^ Dpi) * 397) ^ (Caption ? 1 : 0); }
            }
        }
        internal TileTextStyle(Font font)
        {
            NameFont = new Font(font.FontFamily, font.Size * 0.9f);
            CaptionFont = new Font(font.FontFamily, font.Size * 0.82f);
        }
        internal void DrawLabel(Graphics graphics, string text, Rectangle bounds, bool caption, Color ink, Color background)
        {
            if (string.IsNullOrEmpty(text) || bounds.Width <= 0 || bounds.Height <= 0) return;
            var key = new LabelKey { Text = text, Size = bounds.Size, Ink = ink.ToArgb(), Background = background.ToArgb(),
                Dpi = (int)Math.Round(graphics.DpiX), Caption = caption };
            Bitmap image;
            if (!labels.TryGet(key, out image))
            {
                image = new Bitmap(bounds.Width, bounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    image.SetResolution(graphics.DpiX, graphics.DpiY);
                    using (var target = Graphics.FromImage(image))
                    {
                        target.Clear(background); target.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix |
                            (caption ? TextFormatFlags.VerticalCenter : TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                        TextRenderer.DrawText(target, text, caption ? CaptionFont : NameFont, new Rectangle(Point.Empty, bounds.Size), ink, flags);
                    }
                    labels.Add(key, image);
                }
                catch { image.Dispose(); throw; }
            }
            graphics.DrawImageUnscaled(image, bounds.Location);
        }
        public void Dispose() { labels.Dispose(); NameFont.Dispose(); CaptionFont.Dispose(); }
    }
}
