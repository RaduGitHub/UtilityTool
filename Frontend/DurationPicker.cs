using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace UtilityTool.Frontend
{
    // Windows Clock-style duration entry; scheduling stays in ShutdownService.
    internal sealed class DurationPicker : UserControl
    {
        private readonly TextBox[] fields = new TextBox[3];
        private readonly int[] lastValues = { 0, 1, 0 };
        private readonly string[] units = { "Hours", "Minutes", "Seconds" };
        private readonly TableLayoutPanel layout;
        private readonly Font digitFont = new Font("Segoe UI", 34, FontStyle.Bold);
        private readonly Font colonFont = new Font("Segoe UI", 30);
        internal DurationPicker()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Size = new Size(440, 186);
            BackColor = AppTheme.Panel;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 4,
                BackColor = Color.Transparent, Margin = Padding.Empty, Padding = Padding.Empty };
            foreach (int width in new[] { 28, 8, 28, 8, 28 })
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
            foreach (int height in new[] { 36, 88, 36, 26 })
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            for (int index = 0; index < 3; index++)
            {
                int segment = index;
                var up = new DurationArrow(true) { Dock = DockStyle.Fill, Margin = Padding.Empty,
                    AccessibleName = "Increase " + units[index].ToLowerInvariant() };
                var down = new DurationArrow(false) { Dock = DockStyle.Fill, Margin = Padding.Empty,
                    AccessibleName = "Decrease " + units[index].ToLowerInvariant() };
                up.Click += delegate { Step(segment, 1); };
                down.Click += delegate { Step(segment, -1); };
                var field = new DurationNumberBox(index == 0 ? 99 : 59) { Text = lastValues[index].ToString("00", CultureInfo.InvariantCulture),
                    TextAlign = HorizontalAlignment.Center, MaxLength = 2, Font = digitFont,
                    BorderStyle = BorderStyle.None, BackColor = AppTheme.Surface, ForeColor = AppTheme.Ink,
                    AccessibleName = "Shutdown delay " + units[index].ToLowerInvariant(), TabIndex = index };
                fields[index] = field;
                var holder = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Surface,
                    Margin = new Padding(10, 6, 10, 6), TabIndex = index };
                holder.Controls.Add(field);
                holder.Resize += delegate { field.Bounds = new Rectangle(0,
                    Math.Max(0, (holder.ClientSize.Height - field.PreferredHeight) / 2),
                    holder.ClientSize.Width, field.PreferredHeight); };
                field.Enter += delegate { field.BackColor = Color.FromArgb(49, 59, 76); };
                field.Leave += delegate
                {
                    field.BackColor = AppTheme.Surface;
                    if (field.Text.Length == 0) field.Text = "00";
                    int value;
                    if (TryRead(segment, out value)) { lastValues[segment] = value; field.Text = value.ToString("00", CultureInfo.InvariantCulture); }
                };
                field.KeyDown += delegate(object sender, KeyEventArgs e)
                {
                    if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down)
                    { Step(segment, e.KeyCode == Keys.Up ? 1 : -1); e.SuppressKeyPress = true; }
                };
                layout.Controls.Add(up, index * 2, 0);
                layout.Controls.Add(holder, index * 2, 1);
                layout.Controls.Add(down, index * 2, 2);
                layout.Controls.Add(new Label { Text = units[index], Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.Muted, BackColor = Color.Transparent }, index * 2, 3);
            }
            for (int column = 1; column < 4; column += 2)
                layout.Controls.Add(new Label { Text = ":", Font = colonFont, Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter, ForeColor = AppTheme.Muted,
                    BackColor = Color.Transparent, Margin = Padding.Empty }, column, 1);
            Controls.Add(layout);
        }

        internal void ApplyAppearance()
        {
            // The shared theme styles ordinary inputs; this picker has a single shared frame.
            foreach (var field in fields) { field.BorderStyle = BorderStyle.None; field.BackColor = AppTheme.Surface; }
        }
        private int Maximum(int segment) { return segment == 0 ? 99 : 59; }
        private bool TryRead(int segment, out int value)
        {
            return int.TryParse(fields[segment].Text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value >= 0 && value <= Maximum(segment);
        }
        private void Step(int segment, int direction)
        {
            int value;
            if (!TryRead(segment, out value)) value = lastValues[segment];
            value = (value + direction + Maximum(segment) + 1) % (Maximum(segment) + 1);
            lastValues[segment] = value;
            fields[segment].Text = value.ToString("00", CultureInfo.InvariantCulture);
            fields[segment].Focus(); ((DurationNumberBox)fields[segment]).RestartEntry();
        }
        internal bool TryGetSeconds(out int seconds, out string error)
        {
            seconds = 0; error = null;
            for (int index = 0; index < fields.Length; index++)
            {
                int value;
                if (!TryRead(index, out value))
                {
                    error = "Enter " + units[index].ToLowerInvariant() + " from 00 to " + Maximum(index) + ".";
                    fields[index].Focus(); fields[index].SelectAll(); return false;
                }
                seconds += value * (index == 0 ? 3600 : index == 1 ? 60 : 1);
            }
            if (seconds > 0) return true;
            error = "Choose a duration greater than zero.";
            fields[2].Focus(); fields[2].SelectAll(); return false;
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            float scale = DeviceDpi / 96f;
            var bounds = new Rectangle(1, (int)(36 * scale), Math.Max(1, Width - 3), (int)(88 * scale) - 1);
            if (bounds.Width < 20) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var shape = AppTheme.Rounded(bounds, Math.Max(1, (int)(8 * scale))))
            using (var fill = new SolidBrush(AppTheme.Surface))
            using (var border = new Pen(Color.FromArgb(71, 82, 103)))
            { e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape); }
            using (var accent = new Pen(Enabled ? AppTheme.Accent : AppTheme.Muted, 2 * scale))
                e.Graphics.DrawLine(accent, bounds.Left + 8 * scale, bounds.Bottom, bounds.Right - 8 * scale, bounds.Bottom);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) { digitFont.Dispose(); colonFont.Dispose(); }
        }
    }
    // Validate every edit, including paste and undo, before publishing a changed value.
    internal sealed class DurationNumberBox : TextBox
    {
        private readonly int maximum;
        private string acceptedText = "";
        private bool restoring, editing;
        private int editStart, editLength;
        private bool freshEntry = true;
        internal DurationNumberBox(int max) { maximum = max; }
        internal void RestartEntry()
        {
            Text = TwoDigits();
            freshEntry = true;
            SelectAll();
        }
        private string TwoDigits()
        {
            int value;
            return (int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) ? value : 0)
                .ToString("00", CultureInfo.InvariantCulture);
        }
        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            RestartEntry();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) RestartEntry();
        }
        private bool Allowed(string text)
        {
            if (text.Length == 0) return true; // Allow clearing a field while replacing its digits.
            if (text.Length > 2) return false;
            foreach (char digit in text) if (digit < '0' || digit > '9') return false;
            int value;
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum;
        }
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled) return;
            if (!char.IsControl(e.KeyChar))
            {
                e.Handled = true;
                if (e.KeyChar < '0' || e.KeyChar > '9') return;
                // Keep the most recently entered digit and append the new one within this field.
                string candidate = (freshEntry ? "0" : TwoDigits().Substring(1, 1)) + e.KeyChar;
                if (!Allowed(candidate)) return;
                Text = candidate;
                freshEntry = false;
                Select(1, 1);
            }
        }
        protected override void OnTextChanged(EventArgs e)
        {
            if (restoring) return;
            if (!Allowed(Text))
            {
                int start = editing ? editStart : SelectionStart;
                int length = editing ? editLength : SelectionLength;
                restoring = true;
                try { Text = acceptedText; Select(Math.Min(start, TextLength), Math.Min(length, TextLength - Math.Min(start, TextLength))); }
                finally { restoring = false; }
                return;
            }
            acceptedText = Text;
            if (Text.Length == 0) freshEntry = true;
            base.OnTextChanged(e);
        }
        protected override void WndProc(ref Message message)
        {
            // Remember the selection before native replacement so rejected paste keeps it intact.
            bool replacement = message.Msg == 0x0102 || message.Msg == 0x0302 || message.Msg == 0x00C2;
            if (!replacement || restoring) { base.WndProc(ref message); return; }
            bool previousEditing = editing;
            int previousStart = editStart, previousLength = editLength;
            editing = true; editStart = SelectionStart; editLength = SelectionLength;
            try { base.WndProc(ref message); }
            finally { editing = previousEditing; editStart = previousStart; editLength = previousLength; }
        }
    }
    internal sealed class DurationArrow : Button
    {
        private readonly bool upwards;
        private bool hovering;
        internal DurationArrow(bool up)
        {
            upwards = up; TabStop = false; Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; BackColor = AppTheme.Panel;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovering = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovering = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = DeviceDpi / 96f;
            float x = Width / 2f, y = Height / 2f;
            if (hovering && Enabled)
                using (var shape = AppTheme.Rounded(new Rectangle((int)(x - 20 * scale), (int)(y - 14 * scale), (int)(40 * scale), (int)(28 * scale)), (int)(6 * scale)))
                using (var fill = new SolidBrush(Color.FromArgb(49, 59, 76))) e.Graphics.FillPath(fill, shape);
            using (var pen = new Pen(Enabled ? hovering ? AppTheme.Ink : AppTheme.Muted : Color.FromArgb(83, 95, 113), 1.6f * scale))
            {
                int direction = upwards ? 1 : -1;
                e.Graphics.DrawLines(pen, new[] { new PointF(x - 5 * scale, y + 2.5f * scale * direction),
                    new PointF(x, y - 2.5f * scale * direction), new PointF(x + 5 * scale, y + 2.5f * scale * direction) });
            }
        }
    }
}
