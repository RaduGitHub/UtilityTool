using System;
using System.Drawing;
using System.Globalization;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Tabs
{
    internal sealed class ShutdownTab : BufferedTabPage
    {
        private readonly TextBox shutdownSeconds = new TextBox();
        private readonly Button scheduleButton = new ModernButton();
        private readonly Button cancelButton = new ModernButton();
        private readonly Label shutdownStatus = new Label();
        private readonly System.Windows.Forms.Timer countdownTimer = new System.Windows.Forms.Timer();
        private DateTimeOffset? shutdownAt;
        internal ShutdownTab() : base("Shutdown")
        {
            BackColor = AppTheme.Surface;
            var content = new FrostedLayout
            {
                Dock = DockStyle.Fill, Padding = new Padding(36),
                ColumnCount = 1, RowCount = 5
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 192));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label
            {
                Text = "Schedule a shutdown", Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 24, FontStyle.Bold)
            }, 0, 0);
            content.Controls.Add(new Label
            {
                Text = "Choose how many seconds Windows should wait before shutting down.",
                Dock = DockStyle.Fill
            }, 0, 1);
            var card = new FrostedLayout {
                Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 2, IsCard = true
            };
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            content.Controls.Add(card, 0, 2);
            var input = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown };
            input.Controls.Add(new Label { Text = "Delay in seconds", AutoSize = true });
            shutdownSeconds.Text = "60";
            shutdownSeconds.Width = 220;
            shutdownSeconds.MaxLength = 9;
            shutdownSeconds.AccessibleName = "Shutdown delay in seconds";
            input.Controls.Add(shutdownSeconds);
            card.Controls.Add(input, 0, 0);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            scheduleButton.Text = "Schedule shutdown";
            scheduleButton.AutoSize = true;
            scheduleButton.Padding = new Padding(12, 5, 12, 5);
            scheduleButton.Click += async delegate { await ScheduleShutdown(); };
            cancelButton.Text = "Cancel pending shutdown";
            cancelButton.AutoSize = true;
            cancelButton.Padding = new Padding(12, 5, 12, 5);
            cancelButton.Click += async delegate { await CancelShutdown(); };
            actions.Controls.Add(scheduleButton);
            actions.Controls.Add(cancelButton);
            card.Controls.Add(actions, 0, 1);
            content.Controls.Add(new Label
            {
                Text = "Save your work first. Windows will force apps to close at the scheduled time.\nThe shutdown remains scheduled if you close UtilityTool. Cancel stops any pending Windows shutdown.",
                ForeColor = AppTheme.Warning, Dock = DockStyle.Fill
            }, 0, 3);
            shutdownStatus.Text = "Enter a whole number from 1 to 315,360,000 seconds.";
            shutdownStatus.Dock = DockStyle.Fill;
            content.Controls.Add(shutdownStatus, 0, 4);
            Controls.Add(content);
            AppTheme.Apply(this);
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += delegate { UpdateCountdown(); };
        }

        private async Task ScheduleShutdown()
        {
            int seconds;
            if (!ShutdownService.TryParseSeconds(shutdownSeconds.Text, out seconds))
            {
                shutdownStatus.Text = "Enter a whole number from 1 to 315,360,000 seconds.";
                shutdownStatus.ForeColor = AppTheme.Error;
                shutdownSeconds.Focus();
                return;
            }
            SetBusy(true);
            try
            {
                await Task.Run(delegate { ShutdownService.Execute(ShutdownService.ScheduleArguments(seconds)); });
                if (IsDisposed) return;
                shutdownAt = DateTimeOffset.Now.AddSeconds(seconds);
                shutdownStatus.ForeColor = AppTheme.Accent;
                UpdateCountdown();
                countdownTimer.Start();
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    shutdownStatus.Text = "Could not schedule shutdown: " + error.Message;
                    shutdownStatus.ForeColor = AppTheme.Error;
                }
            }
            finally { if (!IsDisposed) SetBusy(false); }
        }

        private async Task CancelShutdown()
        {
            SetBusy(true);
            try
            {
                await Task.Run(delegate { ShutdownService.Execute(ShutdownService.CancelArguments); });
                if (IsDisposed) return;
                countdownTimer.Stop();
                shutdownAt = null;
                shutdownStatus.ForeColor = AppTheme.Accent;
                shutdownStatus.Text = "Pending shutdown canceled.";
            }
            catch (Exception error)
            {
                if (!IsDisposed)
                {
                    shutdownStatus.Text = "Could not cancel shutdown: " + error.Message;
                    shutdownStatus.ForeColor = AppTheme.Error;
                }
            }
            finally { if (!IsDisposed) SetBusy(false); }
        }

        private void SetBusy(bool busy)
        {
            scheduleButton.Enabled = !busy && !shutdownAt.HasValue;
            cancelButton.Enabled = !busy;
            shutdownSeconds.Enabled = !busy && !shutdownAt.HasValue;
        }

        private void UpdateCountdown()
        {
            if (!shutdownAt.HasValue) return;
            long remaining = Math.Max(0, (long)Math.Ceiling((shutdownAt.Value - DateTimeOffset.Now).TotalSeconds));
            shutdownStatus.Text = remaining > 0
                ? "Windows shutdown scheduled. About " + remaining.ToString(CultureInfo.CurrentCulture) + " seconds remaining."
                : "Scheduled shutdown time reached. Windows is handling the shutdown.";
            if (remaining == 0) countdownTimer.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) countdownTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
