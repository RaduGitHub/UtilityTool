using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace UtilityTool.Frontend
{
    // Retain the native edit control and keyboard behavior inside a rounded field.
    internal sealed class PasswordInputFrame : Panel
    {
        private readonly TextBox input;
        internal PasswordInputFrame(TextBox input)
        {
            this.input = input; Dock = DockStyle.Fill; Margin = new Padding(0);
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = AppTheme.FluentSurface;
            input.BorderStyle = BorderStyle.None; input.BackColor = Color.FromArgb(48, 53, 62); input.ForeColor = AppTheme.Ink;
            input.Font = new Font("Segoe UI", 12); input.UseSystemPasswordChar = true; input.MaxLength = 0;
            Controls.Add(input);
            input.GotFocus += delegate { Invalidate(); }; input.LostFocus += delegate { Invalidate(); };
            Click += delegate { input.Select(); input.Focus(); };
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (input != null) input.SetBounds(12, Math.Max(4, (Height - input.PreferredHeight) / 2), Math.Max(1, Width - 24), input.PreferredHeight);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if (Width < 20 || Height < 12) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = AppTheme.Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 8))
            using (var fill = new SolidBrush(input.BackColor))
            using (var border = new Pen(input.Focused ? Color.FromArgb(145, 201, 255) : Color.FromArgb(65, 72, 84), input.Focused ? 2 : 1))
            { e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape); }
        }
    }
}
