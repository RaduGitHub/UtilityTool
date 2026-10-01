using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System;
using UtilityTool.Backend;
using System.Runtime.InteropServices;

namespace UtilityTool.Frontend
{
    internal sealed class LockCoverWindow : Form
    {
        private Bitmap blurred;
        private readonly IntPtr targetWindow;
        private DateTime controlsUsedUntil;
        internal bool IsDragging { get; private set; }
        internal event Action<Rectangle> DragPositionChanged;
        [StructLayout(LayoutKind.Sequential)] private struct DragRect { internal int Left, Top, Right, Bottom; }
        internal bool UsingWindowControls { get { return IsDragging || DateTime.UtcNow < controlsUsedUntil; } }
        internal void SetBackground(Bitmap background)
        {
            if (IsDisposed) { if (background != null) background.Dispose(); return; }
            if (blurred != null) blurred.Dispose();
            blurred = background;
            Invalidate();
        }
        internal LockCoverWindow(Bitmap background, IntPtr target = default(IntPtr), string name = "Locked app")
        {
            targetWindow = target;
            blurred = background;
            DoubleBuffered = true;
            FormBorderStyle = target == IntPtr.Zero ? FormBorderStyle.None : FormBorderStyle.FixedSingle;
            Text = name + " — Locked";
            ControlBox = true; MinimizeBox = true; MaximizeBox = true;
            ShowInTaskbar = false;
            TopMost = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = AppTheme.Surface;
            HandleCreated += delegate { AppTheme.ApplyWindowFrame(this); };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message message)
        {
            if (targetWindow != IntPtr.Zero)
            {
                if (message.Msg == 0x0231) IsDragging = true; // WM_ENTERSIZEMOVE
                if (message.Msg == 0x0216) // WM_MOVING provides the next native drag position.
                {
                    var rect = (DragRect)Marshal.PtrToStructure(message.LParam, typeof(DragRect));
                    var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    AppWindows.MoveWindow(targetWindow, bounds.Location);
                    var changed = DragPositionChanged;
                    if (changed != null) changed(bounds);
                }
                if (message.Msg == 0x0232) // WM_EXITSIZEMOVE
                {
                    AppWindows.MoveWindow(targetWindow, Location);
                    IsDragging = false;
                    controlsUsedUntil = DateTime.UtcNow.AddMilliseconds(250);
                }
            }
            if (targetWindow != IntPtr.Zero && message.Msg == 0x0112)
            {
                int command = message.WParam.ToInt32();
                int kind = command & 0xFFF0;
                if (kind == 0xF010)
                {
                    // Drag the cover natively and move its target on each WM_MOVING,
                    // instead of dragging the target and polling it every 100 ms.
                    controlsUsedUntil = DateTime.UtcNow.AddMilliseconds(500);
                    base.WndProc(ref message);
                    return;
                }
                if (kind == 0xF020 || kind == 0xF030 || kind == 0xF060 || kind == 0xF120)
                {
                    controlsUsedUntil = DateTime.UtcNow.AddMilliseconds(500);
                    AppWindows.WindowCommand(targetWindow, command, message.LParam);
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref message);
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
