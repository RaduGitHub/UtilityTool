using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UtilityTool.Frontend
{
    // Native list-view double buffering keeps scrolling out of managed per-row painting.
    internal sealed class AppChecklist : ListView
    {
        private bool fittingColumn;
        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowStyle(IntPtr window, int index);

        private void FitColumn()
        {
            if (fittingColumn || !IsHandleCreated || Columns.Count == 0) return;
            fittingColumn = true;
            try
            {
                // Reserve scrollbar space even before Windows creates the vertical bar.
                int width = Math.Max(1, ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 6);
                if (Columns[0].Width != width) Columns[0].Width = width;
                if ((GetWindowStyle(Handle, -16) & 0x00100000) != 0)
                    ShowScrollBar(Handle, 0, false); // SB_HORZ only; vertical scrolling stays enabled.
            }
            finally { fittingColumn = false; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FitColumn();
        }

        protected override void WndProc(ref Message message)
        {
            // The second press arrives as WM_LBUTTONDBLCLK, not WM_LBUTTONDOWN.
            // Treat it as another click so rapid checkbox clicks are never skipped.
            if (message.Msg == 0x0203) message.Msg = 0x0201;
            int kind = message.Msg;
            if (kind == 0x000F || kind == 0x0085) FitColumn(); // Before client/non-client paint.
            base.WndProc(ref message);
            if (kind == 0x0005 || kind == 0x0047 || kind == 0x0030 ||
                kind == 0x104D || kind == 0x1008 || kind == 0x1009 || kind == 0x101E)
                FitColumn(); // Resize, font, item and column changes.
        }

        internal bool CheckOnClick { get; set; }
        internal AppChecklist()
        {
            View = View.Details;
            HeaderStyle = ColumnHeaderStyle.None;
            CheckBoxes = true;
            FullRowSelect = true;
            MultiSelect = false;
            HideSelection = false;
            BorderStyle = BorderStyle.FixedSingle;
            BackColor = AppTheme.Panel;
            ForeColor = AppTheme.Ink;
            Columns.Add("Installed app");
            DoubleBuffered = true;
            CheckOnClick = true;
        }

        internal void AddApp(UtilityTool.Backend.InstalledApp app, bool isChecked)
        {
            Items.Add(new ListViewItem(app.ToString()) { Tag = app, Checked = isChecked });
        }
        internal void SetItemChecked(int index, bool value) { Items[index].Checked = value; }
        internal bool GetItemChecked(int index) { return Items[index].Checked; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            var hit = HitTest(e.Location);
            // Native state-image clicks already toggle the checkbox.
            bool toggle = Enabled && CheckOnClick && e.Button == MouseButtons.Left && hit.Item != null
                && (hit.Location & ListViewHitTestLocations.StateImage) == 0;
            base.OnMouseDown(e);
            if (toggle) hit.Item.Checked = !hit.Item.Checked;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            FitColumn();
        }
    }
}
