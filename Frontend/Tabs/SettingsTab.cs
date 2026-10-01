using System;
using System.Drawing;
using System.Windows.Forms;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Tabs
{
    internal sealed class SettingsTab : BufferedTabPage
    {
        internal SettingsTab() : base("\u2699 Settings")
        {
            AccessibleName = "Settings";
            BackColor = AppTheme.Surface;
            var layout = new FrostedLayout { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = "Settings", Font = new Font("Segoe UI", 24, FontStyle.Bold), AutoSize = true }, 0, 0);
            var startup = new CheckBox { Text = "Start UtilityTool when I sign in to Windows", AutoSize = true };
            var status = new Label { AutoSize = true, MaximumSize = new Size(410, 0), Text = "Startup launches stay in the system tray." };
            layout.Controls.Add(startup, 0, 1); layout.Controls.Add(status, 0, 2);
            Controls.Add(layout); AppTheme.Apply(this);
            try { startup.Checked = StartupService.IsEnabled(Application.ExecutablePath); }
            catch (Exception error) { startup.Enabled = false; status.Text = "Could not read startup setting: " + error.Message; }
            bool updating = false;
            startup.CheckedChanged += delegate
            {
                if (updating) return;
                try
                {
                    StartupService.SetEnabled(startup.Checked, Application.ExecutablePath);
                    status.ForeColor = AppTheme.Muted;
                    status.Text = startup.Checked ? "Enabled. UtilityTool will start in the tray at sign-in." : "Disabled. UtilityTool will not start automatically.";
                }
                catch (Exception error)
                {
                    updating = true; startup.Checked = !startup.Checked; updating = false;
                    status.ForeColor = AppTheme.Error;
                    status.Text = "Could not save startup setting: " + error.Message;
                }
            };
        }
    }
}
