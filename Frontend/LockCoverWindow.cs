using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilityTool.Frontend
{
    internal sealed class LockCoverWindow : Form
    {
        private readonly Bitmap blurred;
        internal LockCoverWindow(Bitmap background)
        {
            blurred = background;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = AppTheme.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width == 0 || ClientSize.Height == 0) return;
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (blurred != null)
                e.Graphics.DrawImage(blurred, ClientRectangle);
            using (var tint = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(110, 29, 34, 44), Color.FromArgb(150, 39, 46, 59), 90f))
                e.Graphics.FillRectangle(tint, ClientRectangle);
            using (var grain = new SolidBrush(Color.FromArgb(7, 190, 205, 230)))
                for (int y = 2; y < Height; y += 8)
                    for (int x = y % 5; x < Width; x += 9)
                        e.Graphics.FillRectangle(grain, x, y, 1, 1);
            if (ClientSize.Height > 70 && ClientSize.Width > 280)
            {
                var badge = new Rectangle((ClientSize.Width - 270) / 2, ClientSize.Height - 54, 270, 30);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var shape = AppTheme.Rounded(badge, 10))
                using (var panel = new SolidBrush(Color.FromArgb(220, 39, 46, 59)))
                    e.Graphics.FillPath(panel, shape);
                TextRenderer.DrawText(e.Graphics, "Locked with UtilityTool", SystemFonts.MessageBoxFont,
                    badge, AppTheme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnResize(System.EventArgs e) { base.OnResize(e); Invalidate(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && blurred != null) blurred.Dispose();
            base.Dispose(disposing);
        }
    }
}
