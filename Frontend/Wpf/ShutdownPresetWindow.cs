using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UtilityTool.Backend;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using TitleBar = Wpf.Ui.Controls.TitleBar;

namespace UtilityTool.Frontend.Wpf
{
    internal sealed class ShutdownPresetWindow : FluentWindow
    {
        internal readonly DurationInput Duration = new DurationInput();
        private readonly TextBox name = new TextBox { MaxLength = ShutdownPresetStore.MaximumNameLength, Height = 42 };
        private readonly TextBlock status = Ui.Text("", 13, true);
        private readonly ShutdownPreset editing;
        internal ShutdownPreset SavedPreset { get; private set; }
        internal ShutdownPresetWindow(Window owner, ShutdownPreset editing = null)
        {
            this.editing = editing; Owner = owner; Title = editing == null ? "Add saved time" : "Modify saved time";
            Width = 560; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ExtendsContentIntoTitleBar = true;
            Background = new SolidColorBrush(Color.FromRgb(28, 32, 39)); Foreground = Brushes.WhiteSmoke;
            var layout = new Grid { Background = Background }; layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TitleBar { Title = "UtilityTool", ShowMinimize = false, ShowMaximize = false, ForceShutdown = false });
            var body = new StackPanel { Margin = new Thickness(24) }; body.Children.Add(Ui.Heading(Title, editing == null ? "Add a time without changing the active timer." : "Change this saved name and time."));
            var nameLabel = Ui.Text("Name", 13); nameLabel.Margin = new Thickness(0, 0, 0, 6); body.Children.Add(nameLabel); body.Children.Add(name);
            var timeLabel = Ui.Text("Duration", 13); timeLabel.Margin = new Thickness(0, 16, 0, 8); body.Children.Add(timeLabel); Duration.HorizontalAlignment = HorizontalAlignment.Center; body.Children.Add(Duration);
            status.MinHeight = 36; status.Margin = new Thickness(0, 12, 0, 12); body.Children.Add(status);
            var save = Ui.Button("Save time", true); var cancel = Ui.Button("Cancel"); save.IsDefault = true; cancel.IsCancel = true;
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(save); actions.Children.Add(cancel); body.Children.Add(actions);
            Grid.SetRow(body, 1); layout.Children.Add(body); Content = layout;
            System.Windows.Automation.AutomationProperties.SetName(name, "Preset name"); name.Text = editing == null ? "" : editing.Name; Duration.SetTotalSeconds(editing == null ? 300 : editing.Seconds);
            name.TextChanged += delegate { status.Text = ""; };
            foreach (var field in new[] { Duration.Hours, Duration.Minutes, Duration.Seconds }) field.TextChanged += delegate { status.Text = ""; };
            save.Click += delegate
            {
                try { SavedPreset = ShutdownPresetStore.Normalize(new ShutdownPreset(editing == null ? Guid.NewGuid().ToString("N") : editing.Id, name.Text, Duration.TotalSeconds)); DialogResult = true; }
                catch (ArgumentOutOfRangeException) { Ui.Status(status, "Choose a time between 00:00:01 and 99:59:59.", true); Duration.Hours.Focus(); }
                catch (ArgumentException error) { Ui.Status(status, error.Message, true); if (string.IsNullOrWhiteSpace(name.Text)) name.Focus(); }
            };
            cancel.Click += delegate { DialogResult = false; }; ContentRendered += delegate { name.Focus(); name.SelectAll(); };
        }
    }
}
