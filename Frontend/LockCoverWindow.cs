using System.Drawing;
using System.Windows.Forms;
using System;
using UtilityTool.Backend;
using System.Runtime.InteropServices;

namespace UtilityTool.Frontend
{
    internal sealed class LockCoverWindow : Form
    {
        private readonly IntPtr targetWindow;
        private DateTime controlsUsedUntil;
        private WindowDragMask dragMask;
        private Point targetOffset;
        private Padding framePadding;
        internal bool IsDragging { get; private set; }
        internal bool ClosePending { get; private set; }
        internal event Action<Rectangle> DragPositionChanged;
        [StructLayout(LayoutKind.Sequential)] private struct DragRect { internal int Left, Top, Right, Bottom; }
        internal bool UsingWindowControls { get { return IsDragging || DateTime.UtcNow < controlsUsedUntil; } }
        internal LockCoverWindow(IntPtr target = default(IntPtr), string name = "Locked app")
        {
            targetWindow = target;
            DoubleBuffered = true;
            FormBorderStyle = target == IntPtr.Zero ? FormBorderStyle.None : FormBorderStyle.FixedSingle;
            Text = name + " — Locked";
            ControlBox = true; MinimizeBox = true; MaximizeBox = true;
            ShowInTaskbar = false;
            TopMost = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = AppTheme.FluentSurface;
            HandleCreated += delegate
            {
                AppTheme.ApplyWindowFrame(this, BackColor);
                try { int square = 1; DwmSetWindowAttribute(Handle, 33, ref square, 4); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            };
        }
        internal void FitToTarget(Rectangle targetBounds)
        {
            if (IsDragging) return;
            Rectangle targetFrame, coverFrame;
            if (AppWindows.DisplayBounds(targetWindow, Handle, out targetFrame) && AppWindows.DisplayBounds(Handle, Handle, out coverFrame))
            {
                var measured = new Padding(coverFrame.Left - Left, coverFrame.Top - Top, Right - coverFrame.Right, Bottom - coverFrame.Bottom);
                // DWM may still report the previous frame during a size change.
                // Do not mistake that stale rectangle for huge invisible borders.
                if (measured.Left >= 0 && measured.Top >= 0 && measured.Right >= 0 && measured.Bottom >= 0 &&
                    measured.Left <= 64 && measured.Top <= 64 && measured.Right <= 64 && measured.Bottom <= 64)
                    framePadding = measured;
                var visible = new Rectangle(Left + framePadding.Left, Top + framePadding.Top,
                    Width - framePadding.Horizontal, Height - framePadding.Vertical);
                var fitted = AppWindows.FitVisibleFrame(targetFrame, Bounds, visible);
                if (Bounds != fitted) Bounds = fitted;
            }
            else if (Bounds != targetBounds) Bounds = targetBounds;
            targetOffset = new Point(targetBounds.Left - Left, targetBounds.Top - Top);
        }
        internal Point TargetLocation(Point coverLocation) { return new Point(coverLocation.X + targetOffset.X, coverLocation.Y + targetOffset.Y); }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message message)
        {
            if (targetWindow != IntPtr.Zero && message.Msg == 0x0010) // WM_CLOSE, including Alt+F4
            {
                CloseTarget(); message.Result = IntPtr.Zero; return;
            }
            if (targetWindow != IntPtr.Zero)
            {
                if (message.Msg == 0x0231) // WM_ENTERSIZEMOVE
                {
                    IsDragging = true;
                    if (dragMask == null) dragMask = WindowDragMask.TryCreate(targetWindow);
                }
                if (message.Msg == 0x0216) // WM_MOVING provides the next native drag position.
                {
                    var rect = (DragRect)Marshal.PtrToStructure(message.LParam, typeof(DragRect));
                    var bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    AppWindows.MoveWindow(targetWindow, TargetLocation(bounds.Location));
                    var changed = DragPositionChanged;
                    if (changed != null) changed(bounds);
                }
                if (message.Msg == 0x0232) // WM_EXITSIZEMOVE
                {
                    try { AppWindows.MoveWindow(targetWindow, TargetLocation(Location)); }
                    finally
                    {
                        RestoreTargetDrawing();
                        IsDragging = false;
                        controlsUsedUntil = DateTime.UtcNow.AddMilliseconds(250);
                    }
                }
            }
            if (targetWindow != IntPtr.Zero && message.Msg == 0x0112)
            {
                int command = message.WParam.ToInt32();
                int kind = command & 0xFFF0;
                if (kind == 0xF010)
                {
                    ResumeWindowControls();
                    // Drag the cover natively and move its target on each WM_MOVING,
                    // instead of dragging the target and polling it every 100 ms.
                    controlsUsedUntil = DateTime.UtcNow.AddMilliseconds(500);
                    base.WndProc(ref message);
                    return;
                }
                if (kind == 0xF020 || kind == 0xF030 || kind == 0xF060 || kind == 0xF120)
                {
                    controlsUsedUntil = DateTime.UtcNow.AddMilliseconds(500);
                    if (kind == 0xF060) CloseTarget();
                    else
                    {
                        ResumeWindowControls();
                        AppWindows.WindowCommand(targetWindow, command, message.LParam);
                    }
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref message);
        }

        private void CloseTarget()
        {
            // Detach BEFORE posting WM_CLOSE. Windows can hide an owned curtain
            // during its owner's shutdown, before the protected window is gone.
            // Keep its existing z-order; the overlay will release it on destruction.
            ClosePending = AppWindows.SetOwner(Handle, IntPtr.Zero);
            AppWindows.WindowCommand(targetWindow, 0xF060, IntPtr.Zero);
        }

        internal void ResumeWindowControls()
        {
            // A close can be refused (for example, an unsaved-work prompt).
            // Explicitly using another window control restores normal ownership.
            if (!ClosePending) return;
            AppWindows.SetOwner(Handle, targetWindow);
            ClosePending = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (ClientSize.Width == 0 || ClientSize.Height == 0) return;
            // An opaque curtain needs no capture, blur, or repeating texture.
            // Clear the entire client area on every paint, including after resizing.
            e.Graphics.Clear(BackColor);
            if (ClientSize.Height > 70 && ClientSize.Width > 280)
            {
                var badge = new Rectangle((ClientSize.Width - 270) / 2, ClientSize.Height - 54, 270, 30);
                TextRenderer.DrawText(e.Graphics, "Locked with UtilityTool", SystemFonts.MessageBoxFont,
                    badge, AppTheme.FluentMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        protected override void OnResize(System.EventArgs e) { base.OnResize(e); Invalidate(); }
        private void RestoreTargetDrawing()
        {
            if (dragMask == null) return;
            dragMask.Dispose(); dragMask = null;
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            RestoreTargetDrawing();
            base.OnHandleDestroyed(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) RestoreTargetDrawing();
            base.Dispose(disposing);
        }
    }
}
