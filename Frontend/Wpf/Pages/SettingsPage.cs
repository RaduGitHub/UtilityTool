using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UtilityTool.Backend;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class SettingsPage : Grid
    {
        internal SettingsPage(bool preview)
        {
            Margin = new Thickness(32, 28, 32, 24);
            var stack = new StackPanel(); Children.Add(stack); stack.Children.Add(Ui.Heading("Settings", "Make UtilityTool fit your routine."));
            var row = new DockPanel(); var toggle = new global::Wpf.Ui.Controls.ToggleSwitch(); DockPanel.SetDock(toggle, Dock.Right); row.Children.Add(toggle);
            var labels = new StackPanel(); labels.Children.Add(Ui.Text("Start with Windows", 17)); var description = Ui.Text("Open UtilityTool when you sign in. Your saved lock selections and status are restored.", 14, true); description.Margin = new Thickness(0, 8, 24, 0); labels.Children.Add(description); row.Children.Add(labels); stack.Children.Add(Ui.Card(row));
            var status = Ui.Text("", 13, true); status.Margin = new Thickness(0, 16, 0, 0); stack.Children.Add(status);
            bool updating = true;
            string executable = Process.GetCurrentProcess().MainModule.FileName;
            try { toggle.IsChecked = !preview && StartupService.IsEnabled(executable); }
            catch (Exception error) { Ui.Status(status, error.Message, true); }
            updating = false;
            RoutedEventHandler changed = delegate
            {
                if (updating) return;
                if (preview) { Ui.Status(status, "Preview only: startup settings were not changed."); return; }
                try { StartupService.SetEnabled(toggle.IsChecked == true, executable); Ui.Status(status, toggle.IsChecked == true ? "UtilityTool will start when you sign in." : "Start with Windows is off."); }
                catch (Exception error) { updating = true; toggle.IsChecked = StartupService.IsEnabled(executable); updating = false; Ui.Status(status, "Could not save startup setting: " + error.Message, true); }
            };
            toggle.Checked += changed; toggle.Unchecked += changed;
        }
    }
}
