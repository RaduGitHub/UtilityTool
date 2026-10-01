using System;
using System.Drawing;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using UtilityTool.Backend;
using System.IO;
namespace UtilityTool.Frontend.Tabs
{
    internal sealed class LockTab : BufferedTabPage
    {
        private readonly TextBox appSearch = new TextBox();
        private readonly AppChecklist installedApps = new AppChecklist();
        private readonly Label appStatus = new Label();
        private readonly Button refreshApps = new ModernButton();
        private readonly List<InstalledApp> appCatalog = new List<InstalledApp>();
        private readonly HashSet<string> selectedApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool filteringApps;
        private bool loadingApps;
        private bool appsLoaded;
        private readonly AppGuardService guard = new AppGuardService();
        private PasswordStore passwords;
        private readonly Button applyLocks = new ModernButton();
        private readonly Button unlock = new ModernButton();
        private readonly Button setPassword = new ModernButton();
        private readonly Label guardStatus = new Label();

        internal LockTab() : base("Lock")
        {
            try
            {
                passwords = new PasswordStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UtilityTool", "password.dat"));
            }
            catch (Exception error) { guardStatus.Text = "Password settings could not load: " + error.Message; }
            BackColor = AppTheme.Surface;
            var content = new FrostedLayout
            {
                Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 7
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            content.Controls.Add(new Label
            {
                Text = "Choose apps", Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 24, FontStyle.Bold)
            }, 0, 0);
            content.Controls.Add(new Label
            {
                Text = "Selected apps ask for your password when opened. Incorrect passwords close the app.\nKeep UtilityTool running. Task Manager can bypass this guard; it is not a Windows security boundary.",
                Dock = DockStyle.Fill
            }, 0, 1);
            var searchRow = new FrostedLayout { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            searchRow.Controls.Add(new Label { Text = "Search", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            appSearch.Dock = DockStyle.Fill;
            appSearch.AccessibleName = "Search installed apps";
            appSearch.TextChanged += delegate { FilterApps(); };
            searchRow.Controls.Add(appSearch, 1, 0);
            refreshApps.Text = "Refresh";
            refreshApps.Dock = DockStyle.Fill;
            refreshApps.Click += async delegate { await LoadInstalledApps(); };
            searchRow.Controls.Add(refreshApps, 2, 0);
            content.Controls.Add(searchRow, 0, 2);
            installedApps.Dock = DockStyle.Fill;
            installedApps.CheckOnClick = true;
            installedApps.AccessibleName = "Installed apps to protect";
            installedApps.ItemCheck += delegate(object sender, ItemCheckEventArgs e)
            {
                if (filteringApps) return;
                var app = (InstalledApp)installedApps.Items[e.Index].Tag;
                if (guard.IsArmed || (e.NewValue == CheckState.Checked && !app.CanGuard))
                {
                    e.NewValue = e.CurrentValue;
                    guardStatus.Text = guard.IsArmed ? "Unlock before changing selected apps." : "Windows did not provide an executable or packaged-app ID for this app.";
                    return;
                }
                if (e.NewValue == CheckState.Checked) selectedApps.Add(app.Id);
                else selectedApps.Remove(app.Id);
                UpdateAppStatus();
            };
            content.Controls.Add(installedApps, 0, 3);
            appStatus.Dock = DockStyle.Fill;
            appStatus.Text = "Open this tab to load installed apps.";
            content.Controls.Add(appStatus, 0, 4);
            var actions = new FlowLayoutPanel {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 0, 0, 12), Margin = new Padding(0)
            };
            applyLocks.Text = "Apply locks"; applyLocks.AutoSize = true;
            unlock.Text = "Unlock apps"; unlock.AutoSize = true;
            setPassword.Text = passwords != null && passwords.HasPassword ? "Change password" : "Set password";
            setPassword.AutoSize = true;
            actions.Controls.Add(setPassword); actions.Controls.Add(applyLocks); actions.Controls.Add(unlock);
            content.Controls.Add(actions, 0, 5);
            guardStatus.Dock = DockStyle.Fill;
            guardStatus.Padding = new Padding(0, 4, 0, 0);
            if (passwords != null) guardStatus.Text = "Guard is off. Select apps and apply locks.";
            content.Controls.Add(guardStatus, 0, 6);
            setPassword.Click += delegate { ConfigurePassword(); };
            applyLocks.Click += delegate { ApplyLocks(); };
            unlock.Click += delegate { UnlockApps(); };
            guard.StatusChanged += delegate(string status)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try { BeginInvoke((Action)delegate { if (!IsDisposed && guard.IsArmed) guardStatus.Text = status; }); }
                catch (InvalidOperationException) { }
            };
            guard.RequestAccess = delegate(GuardProcess process)
            {
                if (IsDisposed || !IsHandleCreated) return false;
                try
                {
                    return (bool)Invoke((Func<bool>)delegate
                    {
                        if (IsDisposed || !guard.IsArmed) return false;
                        using (var overlay = new AppLockOverlay(process))
                        {
                            return overlay.Prompt(passwords, FindForm());
                        }
                    });
                }
                catch (InvalidOperationException) { return false; }
            };
            Controls.Add(content);
            AppTheme.Apply(this);
            UpdateGuardControls();
        }

        internal async Task LoadInstalledApps()
        {
            if (loadingApps) return;
            loadingApps = true;
            refreshApps.Enabled = false;
            appStatus.Text = "Loading installed apps...";
            try
            {
                var apps = await InstalledAppCatalog.LoadAsync();
                if (IsDisposed) return;
                appCatalog.Clear();
                appCatalog.AddRange(apps);
                selectedApps.RemoveWhere(delegate(string id) { return !apps.Exists(delegate(InstalledApp app) { return string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase); }); });
                appsLoaded = true;
                FilterApps();
            }
            catch (Exception error)
            {
                if (!IsDisposed) appStatus.Text = "Could not load installed apps: " + error.Message + " Try Refresh.";
            }
            finally
            {
                loadingApps = false;
                if (!IsDisposed) UpdateGuardControls();
            }
        }

        private void FilterApps()
        {
            filteringApps = true;
            string search = appSearch.Text.Trim();
            installedApps.BeginUpdate();
            try
            {
                installedApps.Items.Clear();
                foreach (var app in appCatalog)
                    if (app.Name.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0)
                        installedApps.AddApp(app, selectedApps.Contains(app.Id));
            }
            finally { installedApps.EndUpdate(); filteringApps = false; }
            if (appsLoaded) UpdateAppStatus();
        }

        private void UpdateAppStatus()
        {
            appStatus.Text = installedApps.Items.Count + " of " + appCatalog.Count + " apps shown; " + selectedApps.Count
                + " selected.\nWindows-registered apps for your account. Portable apps may not appear. Selections apply to this session.";
        }

        private bool CheckPassword(string title)
        {
            if (passwords == null || !passwords.HasPassword) return false;
            using (var dialog = new PasswordDialog(passwords, false, title))
                return dialog.ShowDialog(FindForm()) == DialogResult.OK;
        }

        private bool ConfigurePassword()
        {
            if (passwords == null || guard.IsArmed) return false;
            if (passwords.HasPassword && !CheckPassword("Verify current password")) return false;
            using (var dialog = new PasswordDialog(passwords, true, "Set UtilityTool password"))
            {
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return false;
            }
            setPassword.Text = "Change password";
            guardStatus.Text = "Custom password saved. Select apps and apply locks.";
            return true;
        }

        private void ApplyLocks()
        {
            if (passwords == null || guard.IsArmed) return;
            var chosen = appCatalog.FindAll(delegate(InstalledApp app) { return selectedApps.Contains(app.Id); });
            if (chosen.Count == 0) { guardStatus.Text = "Select at least one supported app."; return; }
            if (!passwords.HasPassword && !ConfigurePassword()) return;
            if (MessageBox.Show(FindForm(), "Selected apps will ask for a password, including apps already open. A wrong password or cancelling closes that instance and may lose unsaved work. Continue?",
                "Apply app locks", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            try
            {
                guard.Arm(chosen, true);
                guardStatus.Text = "Guard is active. Unlock apps or close UtilityTool using your custom password.";
                UpdateGuardControls();
            }
            catch (Exception error) { guardStatus.Text = "Could not apply locks: " + error.Message; }
        }

        private void UnlockApps()
        {
            if (!guard.IsArmed || !CheckPassword("Unlock selected apps")) return;
            guard.Disarm();
            guardStatus.Text = "Apps unlocked. Reopen any app that was closed.";
            UpdateGuardControls();
        }

        private void UpdateGuardControls()
        {
            installedApps.Enabled = !guard.IsArmed;
            refreshApps.Enabled = !guard.IsArmed && !loadingApps;
            applyLocks.Enabled = passwords != null && !guard.IsArmed;
            setPassword.Enabled = passwords != null && !guard.IsArmed;
            unlock.Enabled = guard.IsArmed;
        }

        internal bool CanClose()
        {
            if (!guard.IsArmed) return true;
            if (!CheckPassword("Enter password to close UtilityTool")) return false;
            guard.Disarm();
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) guard.Dispose();
            base.Dispose(disposing);
        }

        internal bool NeedsLoading { get { return !appsLoaded && !loadingApps; } }
    }
}
