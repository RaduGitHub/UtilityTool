using System.Drawing;
using System.Windows.Forms;
using UtilityTool.Frontend.Tabs;
namespace UtilityTool.Frontend
{
    public sealed class MainWindow : Form
    {
        /* Background/tray mode temporarily disabled.
        private readonly NotifyIcon trayIcon;
        private readonly ContextMenuStrip trayMenu;
        private bool exitRequested;
        private bool startInBackground;
        */

        public MainWindow()
            : this(false) { }

        internal MainWindow(bool background)
        {
            // startInBackground = background; // Temporarily launch as a normal window.
            DoubleBuffered = true;
            Text = "UtilityTool";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 680);
            FormBorderStyle = FormBorderStyle.Sizable;
            ControlBox = true;
            MinimizeBox = true;
            MaximizeBox = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            MinimumSize = Size; // Include the frame and title bar around the startup client area.
            HandleCreated += delegate { AppTheme.ApplyWindowFrame(this); };
            BackColor = AppTheme.Surface;
            var tabs = new ModernTabs { Dock = DockStyle.Fill };
            var lockTab = new LockTab();
            var soundTab = new SoundTab();
            tabs.TabPages.Add(new HomeTab());
            tabs.TabPages.Add(new ShutdownTab());
            tabs.TabPages.Add(lockTab);
            tabs.TabPages.Add(soundTab);
            tabs.TabPages.Add(new SettingsTab());
            tabs.SelectedIndexChanged += async delegate
            {
                if (tabs.SelectedTab == lockTab && lockTab.NeedsLoading) await lockTab.LoadInstalledApps();
                if (tabs.SelectedTab == soundTab && soundTab.NeedsLoading) await soundTab.LoadDevices();
            };
            Controls.Add(tabs);
            Shown += async delegate
            {
                if (!IsDisposed && lockTab.NeedsLoading) await lockTab.LoadInstalledApps();
            };
            HandleCreated += delegate
            {
                BeginInvoke((System.Action)delegate { if (!IsDisposed) lockTab.InitializeSavedLocks(); });
            };
            /* Background/tray mode temporarily disabled.
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Open UtilityTool", null, delegate { RestoreWindow(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit UtilityTool", null, delegate
            {
                // Show the owner before asking for a password, including from the tray.
                RestoreWindow();
                if (!lockTab.CanClose()) return;
                exitRequested = true;
                Close();
            });
            trayIcon = new NotifyIcon
            {
                Text = "UtilityTool — running in background",
                Icon = SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { RestoreWindow(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                // Windows shutdown/logoff must remain able to end this session.
                if (e.CloseReason == CloseReason.UserClosing && !exitRequested)
                {
                    e.Cancel = true;
                    Hide();
                }
            };
            */
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                // X and Alt+F4 exit; active locks still require the custom password.
                if (e.CloseReason == CloseReason.UserClosing && !lockTab.CanClose()) e.Cancel = true;
            };
        }

        /* Background/tray mode temporarily disabled.
        protected override void SetVisibleCore(bool value)
        {
            if (startInBackground && value)
            {
                startInBackground = false;
                if (!IsHandleCreated) CreateHandle();
                base.SetVisibleCore(false);
                return;
            }
            base.SetVisibleCore(value);
        }

        private void RestoreWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
                if (trayMenu != null) trayMenu.Dispose();
            }
            base.Dispose(disposing);
        }
        */
    }
}
