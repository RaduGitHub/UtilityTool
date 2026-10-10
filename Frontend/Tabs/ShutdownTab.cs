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
        private readonly DurationPicker shutdownDuration = new DurationPicker();
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
                ColumnCount = 1, RowCount = 6
            };
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 296));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            content.Controls.Add(new Label
            {
                Text = "Schedule a shutdown", Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 24, FontStyle.Bold)
            }, 0, 0);
            content.Controls.Add(new Label
            {
                Text = "Choose a delay in hours, minutes, and seconds. Type a value or use the arrows.",
                Dock = DockStyle.Fill
            }, 0, 1);
            var card = new FrostedLayout {
                Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 2, IsCard = true
            };
            card.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            card.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            content.Controls.Add(card, 0, 2);
            shutdownDuration.Anchor = AnchorStyles.Top;
            shutdownDuration.Margin = Padding.Empty;
            card.Controls.Add(shutdownDuration, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Anchor = AnchorStyles.Top, WrapContents = false, Margin = Padding.Empty };
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
            shutdownStatus.Text = "Choose a duration, then schedule shutdown.";
            shutdownStatus.Dock = DockStyle.Fill;
            content.Controls.Add(shutdownStatus, 0, 4);
            Controls.Add(content);
            AppTheme.Apply(this);
            cancelButton.Margin = new Padding(0, 3, 0, 3);
            shutdownDuration.ApplyAppearance();
            countdownTimer.Interval = 1000;
            countdownTimer.Tick += delegate { UpdateCountdown(); };
        }

        private async Task ScheduleShutdown()
        {
            int seconds;
            string errorMessage;
            if (!shutdownDuration.TryGetSeconds(out seconds, out errorMessage))
            {
                shutdownStatus.Text = errorMessage;
                shutdownStatus.ForeColor = AppTheme.Error;
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
            shutdownDuration.Enabled = !busy && !shutdownAt.HasValue;
        }

        private void UpdateCountdown()
        {
            if (!shutdownAt.HasValue) return;
            long remaining = Math.Max(0, (long)Math.Ceiling((shutdownAt.Value - DateTimeOffset.Now).TotalSeconds));
            shutdownStatus.Text = remaining > 0
                ? "Windows will shut down in " + FormatDuration(remaining) + " (at " + shutdownAt.Value.ToString("t", CultureInfo.CurrentCulture) + ")."
                : "Scheduled shutdown time reached. Windows is handling the shutdown.";
            if (remaining == 0) countdownTimer.Stop();
        }

        private static string FormatDuration(long seconds)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", seconds / 3600, seconds / 60 % 60, seconds % 60);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) countdownTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
