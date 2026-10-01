using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilityTool.Frontend
{
    internal static class AppTheme
    {
        internal static readonly Color Ink = Color.FromArgb(240, 243, 249);
        internal static readonly Color Accent = Color.FromArgb(115, 150, 255);
        internal static readonly Color Surface = Color.FromArgb(29, 34, 44);

        internal static readonly Color Muted = Color.FromArgb(157, 169, 187);
        internal static readonly Color Panel = Color.FromArgb(39, 46, 59);
        internal static readonly Color AccentInk = Color.FromArgb(18, 25, 43);
        internal static readonly Color Warning = Color.FromArgb(241, 190, 115);
        internal static readonly Color Error = Color.FromArgb(255, 146, 155);

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        internal static void ApplyWindowFrame(Form window)
        {
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(window.Handle, 20, ref enabled, 4);
                int background = Surface.R | (Surface.G << 8) | (Surface.B << 16);
                int foreground = Ink.R | (Ink.G << 8) | (Ink.B << 16);
                DwmSetWindowAttribute(window.Handle, 35, ref background, 4);
                DwmSetWindowAttribute(window.Handle, 36, ref foreground, 4);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            // Unsupported attributes leave the standard Windows title bar intact.
        }

        internal static void Apply(Control root)
        {
            if (!(root is Label)) root.ForeColor = Ink;
            foreach (Control child in root.Controls)
            {
                var label = child as Label;
                if (label != null) label.BackColor = Color.Transparent;
                var flow = child as FlowLayoutPanel;
                if (flow != null) flow.BackColor = Color.Transparent;
                var text = child as TextBox;
                if (text != null) { text.BorderStyle = BorderStyle.FixedSingle; text.BackColor = Surface; }
                var list = child as CheckedListBox;
                if (list != null) { list.BorderStyle = BorderStyle.FixedSingle; list.BackColor = Panel; }
                var button = child as ModernButton;
                if (button != null)
                {
                    button.Primary = button.Text == "Apply locks" || button.Text == "Schedule shutdown"
                        || button.Text == "Unlock" || button.Text == "Save password";
                    button.Padding = new Padding(14, 5, 14, 5);
                    button.Margin = new Padding(0, 3, 10, 3);
                    button.MinimumSize = new Size(80, 34);
                }
                ApplyChildren(child);
            }
        }
        private static void ApplyChildren(Control root) { Apply(root); }

        internal static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            int diameter = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal class BufferedTabPage : TabPage
    {
        internal BufferedTabPage(string text) : base(text)
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }
    }

    internal sealed class FrostedLayout : TableLayoutPanel
    {
        private Bitmap texture;
        internal bool IsCard { get; set; }
        internal FrostedLayout()
        {
            DoubleBuffered = true;
            BackColor = AppTheme.Surface;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            if (texture != null) { texture.Dispose(); texture = null; }
            base.OnSizeChanged(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (ClientSize.Width == 0 || ClientSize.Height == 0) return;
            if (texture == null)
            {
                texture = new Bitmap(Width, Height);
                using (var graphics = Graphics.FromImage(texture))
                {
                    using (var gradient = new LinearGradientBrush(ClientRectangle,
                        AppTheme.Surface, Color.FromArgb(32, 38, 49), 35f))
                        graphics.FillRectangle(gradient, ClientRectangle);
                    if (IsCard && Width > 26 && Height > 26)
                    {
                        using (var shape = AppTheme.Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 12))
                        using (var panel = new SolidBrush(AppTheme.Panel))
                            graphics.FillPath(panel, shape);
                    }
                    using (var grain = new SolidBrush(Color.FromArgb(3, 185, 199, 225)))
                        for (int y = 3; y < Height; y += 9)
                            for (int x = y % 7; x < Width; x += 11)
                                graphics.FillRectangle(grain, x, y, 1, 1);
                }
            }
            // Transparent labels and buttons reuse the same pixels instead of redrawing thousands of dots.
            e.Graphics.DrawImageUnscaled(texture, Point.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && texture != null) { texture.Dispose(); texture = null; }
            base.Dispose(disposing);
        }
    }
    internal sealed class ModernButton : Button
    {
        internal bool Primary;
        private bool pressed;
        internal ModernButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw |
                ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, false);
            BackColor = Color.Transparent;
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            // The last password key can be released after mouse-down focuses this
            // button. ButtonBase clears its mouse-pressed state on any key-up,
            // causing mouse-up to lose the click. Let the mouse finish its click.
            if (pressed) { e.Handled = true; return; }
            base.OnKeyUp(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            // Paint the full parent background, including every rounded corner and outside edge.
            base.OnPaintBackground(e);
            if (Width < 18 || Height < 18) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            Color fill = !Enabled ? Color.FromArgb(42, 49, 62)
                : Primary ? (pressed ? Color.FromArgb(95, 124, 215) : hover ? Color.FromArgb(139, 169, 255) : AppTheme.Accent)
                : (pressed ? Color.FromArgb(66, 78, 98) : hover ? Color.FromArgb(62, 73, 91) : Color.FromArgb(54, 64, 81));
            using (var shape = AppTheme.Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 8))
            using (var brush = new SolidBrush(fill))
            using (var border = new Pen(Primary && Enabled ? fill : Color.FromArgb(71, 82, 103)))
            { e.Graphics.FillPath(brush, shape); e.Graphics.DrawPath(border, shape); }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                !Enabled ? Color.FromArgb(126, 138, 158) : Primary ? AppTheme.AccentInk : AppTheme.Ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -6, -6),
                    Primary ? AppTheme.AccentInk : AppTheme.Ink, fill);
        }
    }

    internal sealed class ModernTabs : TabControl
    {
        internal ModernTabs()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(130, 44);
            Padding = new Point(18, 8);
        }
        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            Invalidate();
        }
        internal Rectangle NavigationBounds(int index)
        {
            Rectangle bounds = GetTabRect(index);
            if (TabPages[index] is UtilityTool.Frontend.Tabs.SettingsTab)
                bounds.X = Math.Max(bounds.X, ClientSize.Width - bounds.Width - 4);
            return bounds;
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084 && TabCount > 0) // WM_NCHITTEST uses screen coordinates.
            {
                long position = message.LParam.ToInt64();
                var point = PointToClient(new Point((short)(position & 0xffff), (short)((position >> 16) & 0xffff)));
                if (point.X >= 0 && point.X < ClientSize.Width && point.Y >= 0 && point.Y < GetTabRect(0).Bottom)
                {
                    // The native tab strip only knows the original left-side rectangles.
                    // Keep the entire painted header interactive, including Settings on the right.
                    message.Result = new IntPtr(1); // HTCLIENT
                    return;
                }
            }
            if ((message.Msg == 0x0201 || message.Msg == 0x0202 || message.Msg == 0x0203) && TabCount > 0)
            {
                long position = message.LParam.ToInt64();
                var point = new Point((short)(position & 0xffff), (short)((position >> 16) & 0xffff));
                if (point.Y >= 0 && point.Y < GetTabRect(0).Bottom)
                {
                    // Use the painted header positions, including the right-aligned Settings tab.
                    for (int index = 0; message.Msg != 0x0202 && index < TabCount; index++)
                        if (NavigationBounds(index).Contains(point))
                        {
                            Focus();
                            SelectedIndex = index;
                            break;
                        }
                    message.Result = IntPtr.Zero;
                    return;
                }
            }
            base.WndProc(ref message);
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(AppTheme.Surface);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            for (int index = 0; index < TabCount; index++)
                OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, NavigationBounds(index), index,
                    index == SelectedIndex ? DrawItemState.Selected : DrawItemState.None));
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var background = new SolidBrush(AppTheme.Surface))
                e.Graphics.FillRectangle(background, e.Bounds);
            Rectangle bounds = NavigationBounds(e.Index);
            bool selected = e.Index == SelectedIndex;
            bounds.Inflate(-5, -5);
            using (var shape = AppTheme.Rounded(bounds, 9))
            using (var fill = new SolidBrush(selected ? AppTheme.Accent : AppTheme.Surface))
                e.Graphics.FillPath(fill, shape);
            TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, bounds,
                selected ? AppTheme.AccentInk : AppTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (selected && Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(bounds, -5, -5));
        }
    }
}
