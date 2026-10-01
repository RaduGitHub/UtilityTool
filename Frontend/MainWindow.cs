using System.Drawing;
using System.Windows.Forms;
using UtilityTool.Frontend.Tabs;
namespace UtilityTool.Frontend
{
    public sealed class MainWindow : Form
    {
        public MainWindow()
        {
            DoubleBuffered = true;
            Text = "UtilityTool";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 680);
            MinimumSize = new Size(800, 560);
            FormBorderStyle = FormBorderStyle.Sizable;
            ControlBox = true;
            MinimizeBox = true;
            MaximizeBox = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            HandleCreated += delegate { AppTheme.ApplyWindowFrame(this); };
            BackColor = AppTheme.Surface;
            var tabs = new ModernTabs { Dock = DockStyle.Fill };
            var lockTab = new LockTab();
            tabs.TabPages.Add(new HomeTab());
            tabs.TabPages.Add(new ShutdownTab());
            tabs.TabPages.Add(lockTab);
            tabs.SelectedIndexChanged += async delegate
            {
                if (tabs.SelectedTab == lockTab && lockTab.NeedsLoading) await lockTab.LoadInstalledApps();
            };
            Controls.Add(tabs);
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                // Windows shutdown/logoff must remain able to end this session.
                if (e.CloseReason == CloseReason.UserClosing && !lockTab.CanClose()) e.Cancel = true;
            };
        }
    }
}
