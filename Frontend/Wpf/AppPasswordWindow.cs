using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using UtilityTool.Backend;
using FluentWindow = Wpf.Ui.Controls.FluentWindow;
using TitleBar = Wpf.Ui.Controls.TitleBar;

namespace UtilityTool.Frontend.Wpf
{
    internal sealed class AppPasswordWindow : FluentWindow
    {
        private readonly AppPasswordStore passwords;
        private readonly InstalledApp app;
        private readonly CheckBox useUtility = new CheckBox { Content = "Use UtilityTool password", Margin = new Thickness(0, 0, 0, 16) };
        private readonly PasswordBox password = new PasswordBox { Height = 44 }, confirmation = new PasswordBox { Height = 44 };
        private readonly StackPanel custom = new StackPanel();
        private readonly TextBlock status = Ui.Text("", 13, true);
        private readonly global::Wpf.Ui.Controls.Button save = Ui.Button("Save password", true), cancel = Ui.Button("Cancel");
        private bool busy, closed;
        internal AppPasswordWindow(Window owner, AppPasswordStore passwords, InstalledApp app)
        {
            if (passwords == null) throw new ArgumentNullException("passwords"); if (app == null) throw new ArgumentNullException("app");
            this.passwords = passwords; this.app = app; Owner = owner; Title = "App password";
            Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ExtendsContentIntoTitleBar = true;
            Background = new SolidColorBrush(Color.FromRgb(28, 32, 39)); Foreground = Brushes.WhiteSmoke;
            var layout = new Grid { Background = Background }; layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new TitleBar { Title = "UtilityTool", ShowMinimize = false, ShowMaximize = false, ForceShutdown = false });
            var body = new StackPanel { Margin = new Thickness(28) }; body.Children.Add(Ui.Heading("App password", "Choose how to unlock this app."));
            var name = Ui.Text(app.Name, 18); name.FontWeight = FontWeights.SemiBold; name.Margin = new Thickness(0, 0, 0, 20); System.Windows.Automation.AutomationProperties.SetName(name, "App name"); body.Children.Add(name);
            System.Windows.Automation.AutomationProperties.SetName(useUtility, "Use UtilityTool password"); useUtility.IsChecked = !passwords.HasOverride(app); body.Children.Add(useUtility);
            password.Style = confirmation.Style = (Style)Application.Current.FindResource("ToolPassword");
            System.Windows.Automation.AutomationProperties.SetName(password, "App password"); System.Windows.Automation.AutomationProperties.SetName(confirmation, "Confirm app password");
            var label = Ui.Text("New app password", 13); label.Margin = new Thickness(0, 0, 0, 6); custom.Children.Add(label); custom.Children.Add(password);
            label = Ui.Text("Confirm app password", 13); label.Margin = new Thickness(0, 12, 0, 6); custom.Children.Add(label); custom.Children.Add(confirmation); body.Children.Add(custom);
            status.MinHeight = 40; status.Margin = new Thickness(0, 12, 0, 0); body.Children.Add(status);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right }; actions.Children.Add(save); actions.Children.Add(cancel); body.Children.Add(actions);
            Grid.SetRow(body, 1); layout.Children.Add(body); Content = layout; save.IsDefault = true; cancel.IsCancel = true;
            useUtility.Checked += delegate { Changed(); }; useUtility.Unchecked += delegate { Changed(); }; password.PasswordChanged += delegate { ClearError(); }; confirmation.PasswordChanged += delegate { ClearError(); };
            save.Click += async delegate { await Save(); }; cancel.Click += delegate { if (!busy) DialogResult = false; };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (busy) e.Cancel = true; };
            Closed += delegate { closed = true; password.Clear(); confirmation.Clear(); };
            ContentRendered += delegate { if (useUtility.IsChecked == true) useUtility.Focus(); else password.Focus(); };
            Changed();
        }
        private void ClearError() { if (!busy && !closed) { status.Text = ""; status.Foreground = Ui.Muted; } }
        private void Changed()
        {
            if (busy || closed) return; ClearError(); custom.Visibility = useUtility.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
            if (IsVisible && useUtility.IsChecked != true) password.Focus();
        }
        private async Task Save()
        {
            if (busy || closed) return;
            bool useDefault = useUtility.IsChecked == true; string value = useDefault ? null : password.Password;
            if (!useDefault && value != confirmation.Password) { Ui.Status(status, "The passwords do not match.", true); confirmation.Focus(); return; }
            busy = true; UpdateControls(); Ui.Status(status, "Saving password…");
            try
            {
                await Task.Run(() => { if (useDefault) passwords.UseUtilityPassword(app); else passwords.Set(app, value); });
                busy = false; if (!closed) DialogResult = true;
            }
            catch (Exception error) { if (!closed) Ui.Status(status, "The app password could not be saved: " + error.Message, true); }
            finally { value = null; busy = false; if (!closed) UpdateControls(); }
        }
        private void UpdateControls() { useUtility.IsEnabled = password.IsEnabled = confirmation.IsEnabled = save.IsEnabled = cancel.IsEnabled = !busy; }
    }
}
