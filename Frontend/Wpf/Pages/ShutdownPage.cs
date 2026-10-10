using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class ShutdownPage : Grid, IDisposable
    {
        internal readonly DurationInput Duration = new DurationInput();
        private readonly global::Wpf.Ui.Controls.Button start = Ui.Button("Schedule shutdown", true), cancel = Ui.Button("Cancel shutdown");
        private readonly TextBlock status = Ui.Text("Choose a duration to get started.", 14, true);
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly bool preview;
        private DateTimeOffset? scheduled;
        private bool busy;
        internal ShutdownPage(bool preview)
        {
            this.preview = preview; Margin = new Thickness(32, 28, 32, 24);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); RowDefinitions.Add(new RowDefinition());
            Children.Add(Ui.Heading("Shutdown timer", "Give Windows a time to switch off."));
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 560, Margin = new Thickness(0, 0, 0, 64) };
            var cardBody = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Ui.Text("\uE823", 28); icon.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"); icon.Foreground = Ui.Accent; icon.HorizontalAlignment = HorizontalAlignment.Center; icon.Margin = new Thickness(0, 0, 0, 20); cardBody.Children.Add(icon);
            cardBody.Children.Add(Duration);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 0) }; actions.Children.Add(start); actions.Children.Add(cancel); cardBody.Children.Add(actions);
            center.Children.Add(Ui.Card(cardBody, new Thickness(32)));
            status.TextAlignment = TextAlignment.Center; status.Margin = new Thickness(0, 20, 0, 16); center.Children.Add(status);
            var note = Ui.Text("Save your work before the timer ends. A scheduled shutdown continues after UtilityTool closes.", 13, true); note.TextAlignment = TextAlignment.Center; center.Children.Add(note);
            Grid.SetRow(center, 1); Children.Add(center);
            start.Click += async delegate { await Schedule(); }; cancel.Click += async delegate { await Cancel(); }; timer.Tick += delegate { UpdateCountdown(); };
            // Cancellation stays available for a Windows timer started outside this session.
        }
        private async System.Threading.Tasks.Task Schedule()
        {
            if (busy || scheduled.HasValue) return;
            int seconds = Duration.TotalSeconds;
            if (seconds == 0) { Ui.Status(status, "Choose a duration longer than zero.", true); return; }
            if (preview) { Ui.Status(status, "Preview only: no shutdown has been scheduled."); return; }
            busy = true; UpdateControls();
            try { await System.Threading.Tasks.Task.Run(() => ShutdownService.Execute(ShutdownService.ScheduleArguments(seconds))); scheduled = DateTimeOffset.Now.AddSeconds(seconds); timer.Start(); UpdateCountdown(); }
            catch (Exception error) { Ui.Status(status, "Windows could not schedule shutdown: " + error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private async System.Threading.Tasks.Task Cancel()
        {
            if (busy) return;
            if (preview) { Ui.Status(status, "Preview only: Windows timers were not changed."); return; }
            busy = true; UpdateControls();
            try { await System.Threading.Tasks.Task.Run(() => ShutdownService.Execute(ShutdownService.CancelArguments)); scheduled = null; timer.Stop(); Ui.Status(status, "Shutdown cancelled."); }
            catch (Exception error) { Ui.Status(status, "Windows could not cancel shutdown: " + error.Message, true); }
            finally { busy = false; UpdateControls(); }
        }
        private void UpdateCountdown()
        { if (!scheduled.HasValue) return; var remaining = scheduled.Value - DateTimeOffset.Now; if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero; Ui.Status(status, "Shutting down in " + ((int)remaining.TotalHours).ToString("00") + ":" + remaining.Minutes.ToString("00") + ":" + remaining.Seconds.ToString("00")); }
        private void UpdateControls() { start.IsEnabled = !busy && !scheduled.HasValue; Duration.IsEnabled = !busy && !scheduled.HasValue; cancel.IsEnabled = !busy; }
        public void Dispose() { timer.Stop(); }
    }
    internal sealed class DurationInput : StackPanel
    {
        internal readonly DurationField Hours = new DurationField(99, "Hours"), Minutes = new DurationField(59, "Minutes", 1), Seconds = new DurationField(59, "Seconds");
        internal int TotalSeconds { get { return Hours.Value * 3600 + Minutes.Value * 60 + Seconds.Value; } }
        internal DurationInput()
        {
            Orientation = Orientation.Horizontal;
            Add(Hours, "HOURS"); Separator(); Add(Minutes, "MINUTES"); Separator(); Add(Seconds, "SECONDS");
        }
        private void Separator() { var separator = Ui.Text(":", 36, true); separator.Margin = new Thickness(12, 52, 12, 0); Children.Add(separator); }
        private void Add(DurationField field, string label)
        {
            var panel = new StackPanel { Width = 100 };
            var up = Ui.Button("\uE70E"); up.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"); up.Margin = new Thickness(0, 0, 0, 8); up.Appearance = global::Wpf.Ui.Controls.ControlAppearance.Transparent; up.Click += delegate { field.Step(1); }; panel.Children.Add(up);
            panel.Children.Add(field);
            var down = Ui.Button("\uE70D"); down.FontFamily = up.FontFamily; down.Margin = new Thickness(0, 8, 0, 8); down.Appearance = up.Appearance; down.Click += delegate { field.Step(-1); }; panel.Children.Add(down);
            var caption = Ui.Text(label, 11, true); caption.TextAlignment = TextAlignment.Center; panel.Children.Add(caption); Children.Add(panel);
        }
    }
    internal sealed class DurationField : global::Wpf.Ui.Controls.TextBox
    {
        private readonly int maximum;
        private bool fresh = true, updating;
        private string valid;
        internal int Value { get { return int.Parse(valid, CultureInfo.InvariantCulture); } }
        internal DurationField(int maximum, string name, int initial = 0)
        {
            this.maximum = maximum; valid = initial.ToString("00"); Text = valid;
            Style = (Style)Application.Current.FindResource(typeof(global::Wpf.Ui.Controls.TextBox));
            ShowClearButton = false;
            FontSize = 38; FontWeight = FontWeights.SemiBold; TextAlignment = TextAlignment.Center; Padding = new Thickness(4, 8, 4, 8);
            System.Windows.Automation.AutomationProperties.SetName(this, name);
            GotKeyboardFocus += delegate { fresh = true; SelectAll(); };
            PreviewMouseLeftButtonDown += delegate { fresh = true; };
            PreviewTextInput += delegate(object sender, TextCompositionEventArgs e) { e.Handled = true; foreach (char digit in e.Text) PushDigit(digit); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            { if (e.Key == Key.Up || e.Key == Key.Down) { Step(e.Key == Key.Up ? 1 : -1); e.Handled = true; } else if (e.Key == Key.Back || e.Key == Key.Delete) { SetValue(0); fresh = true; e.Handled = true; } };
            DataObject.AddPastingHandler(this, delegate(object sender, DataObjectPastingEventArgs e)
            { e.CancelCommand(); var text = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string; int number; if (text != null && text.Length <= 2 && text.Length > 0 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number <= maximum) { SetValue(number); fresh = true; } });
            TextChanged += delegate
            { if (updating) return; int value; if (Text.Length > 0 && Text.Length <= 2 && int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= maximum) { valid = value.ToString("00"); } else SetValue(Value); };
            LostKeyboardFocus += delegate { SetValue(Value); fresh = true; };
        }
        internal void PushDigit(char digit)
        {
            if (digit < '0' || digit > '9') return;
            int next = (fresh ? 0 : Value % 10) * 10 + digit - '0';
            if (next > maximum) return;
            SetValue(next); fresh = false;
        }
        internal void Step(int change) { SetValue((Value + change + maximum + 1) % (maximum + 1)); fresh = true; }
        private void SetValue(int number) { valid = number.ToString("00"); updating = true; Text = valid; CaretIndex = Text.Length; updating = false; }
    }
}
