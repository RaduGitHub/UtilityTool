using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using UtilityTool.Backend;
using global::Wpf.Ui.Controls;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class PasswordWindow : FluentWindow
    {
        private readonly System.Windows.Controls.PasswordBox password = new System.Windows.Controls.PasswordBox(), confirmation = new System.Windows.Controls.PasswordBox();
        private readonly System.Windows.Controls.TextBlock status = Ui.Text("", 13);
        private readonly global::Wpf.Ui.Controls.Button confirm = Ui.Button("Confirm", true), cancel = Ui.Button("Cancel");
        private readonly PasswordStore store;
        private readonly bool create;
        private bool busy;
        internal PasswordWindow(Window owner, PasswordStore store, bool create, string title)
        {
            this.store = store; this.create = create;
            Owner = owner; Title = title; Width = 440; MinHeight = 260; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ExtendsContentIntoTitleBar = true;
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(28, 32, 39));
            var layout = new Grid { Background = Background }; layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TitleBar { Title = "UtilityTool", ShowMinimize = false, ShowMaximize = false, ForceShutdown = false });
            var body = new StackPanel { Margin = new Thickness(28) };
            var heading = new DockPanel(); var icon = Ui.Text("\uE72E", 30); icon.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"); icon.Foreground = Ui.Accent; icon.Margin = new Thickness(0, 0, 12, 0); heading.Children.Add(icon); var titleText = Ui.Text(title, 24); titleText.FontWeight = FontWeights.SemiBold; heading.Children.Add(titleText); body.Children.Add(heading);
            var hint = Ui.Text(create ? "Choose your own UtilityTool password." : "Enter your UtilityTool password.", 14, true); hint.Margin = new Thickness(0, 12, 0, 20); body.Children.Add(hint);
            var passwordLabel = Ui.Text("Password", 13); passwordLabel.Margin = new Thickness(0, 0, 0, 6); body.Children.Add(passwordLabel);
            password.Style = confirmation.Style = (Style)Application.Current.FindResource("ToolPassword"); password.Height = 44;
            System.Windows.Automation.AutomationProperties.SetName(password, "Password"); body.Children.Add(password);
            if (create) { var label = Ui.Text("Confirm password", 13); label.Margin = new Thickness(0, 12, 0, 6); body.Children.Add(label); confirmation.Height = 44; System.Windows.Automation.AutomationProperties.SetName(confirmation, "Confirm password"); body.Children.Add(confirmation); }
            status.MinHeight = 44; status.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(status);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; buttons.Children.Add(confirm); buttons.Children.Add(cancel); body.Children.Add(buttons);
            Grid.SetRow(body, 1); layout.Children.Add(body); Content = layout; confirm.IsDefault = true; cancel.IsCancel = true;
            confirm.Click += async delegate { await Submit(); }; cancel.Click += delegate { if (!busy) DialogResult = false; };
            ContentRendered += delegate { Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => { password.Focus(); System.Windows.Input.Keyboard.Focus(password); })); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (busy) e.Cancel = true; };
        }
        private async Task Submit()
        {
            if (busy) return;
            string value = password.Password;
            if (create && value != confirmation.Password) { Ui.Status(status, "The passwords do not match.", true); return; }
            busy = true; confirm.IsEnabled = cancel.IsEnabled = password.IsEnabled = confirmation.IsEnabled = false;
            try
            {
                bool success = await Task.Run(() => { if (create) { store.SetPassword(value); return true; } return store.Verify(value); });
                busy = false;
                if (success) { DialogResult = true; return; }
                Ui.Status(status, "The password is incorrect.", true);
            }
            catch (Exception error) { Ui.Status(status, error.Message, true); }
            finally { busy = false; confirm.IsEnabled = cancel.IsEnabled = password.IsEnabled = confirmation.IsEnabled = true; }
        }
    }
}
