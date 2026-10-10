using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using global::Wpf.Ui.Controls;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class FluentMainWindow : FluentWindow
    {
        private readonly ContentControl content = new ContentControl();
        private readonly Dictionary<string, global::Wpf.Ui.Controls.Button> navigation = new Dictionary<string, global::Wpf.Ui.Controls.Button>();
        private readonly Dictionary<string, UIElement> pages = new Dictionary<string, UIElement>();
        internal readonly ShutdownPage Shutdown;
        internal readonly LockPage Lock;
        internal readonly SoundPage Sound;
        internal readonly bool Preview;
        internal string CurrentPage { get; private set; }
        internal FluentMainWindow(bool preview = false)
        {
            Preview = preview;
            Title = preview ? "UtilityTool · Preview" : "UtilityTool"; Width = MinWidth = 1040; Height = MinHeight = 740;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ExtendsContentIntoTitleBar = true; WindowBackdropType = WindowBackdropType.Mica;
            Background = new SolidColorBrush(Color.FromRgb(28, 32, 39)); Foreground = Brushes.WhiteSmoke;
            UseLayoutRounding = true;
            var layout = new Grid { Background = new LinearGradientBrush(Color.FromRgb(28, 32, 39), Color.FromRgb(23, 28, 36), 90) };
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var titleBar = new TitleBar { Title = Title, ShowMinimize = true, ShowMaximize = true, ShowClose = true, ForceShutdown = false };
            layout.Children.Add(titleBar);
            var nav = new DockPanel { Margin = new Thickness(20, 8, 20, 0), LastChildFill = false };
            var settings = Nav("Settings", "\uE713"); DockPanel.SetDock(settings, Dock.Right); nav.Children.Add(settings);
            foreach (var item in new[] { new[] { "Home", "\uE80F" }, new[] { "Shutdown", "\uE823" }, new[] { "Lock", "\uE72E" }, new[] { "Sound", "\uE767" } }) nav.Children.Add(Nav(item[0], item[1]));
            Grid.SetRow(nav, 1); layout.Children.Add(nav); Grid.SetRow(content, 2); layout.Children.Add(content);
            Shutdown = new ShutdownPage(preview); Lock = new LockPage(this, preview); Sound = new SoundPage(preview);
            pages["Home"] = new HomePage(SelectPage, preview); pages["Shutdown"] = Shutdown; pages["Lock"] = Lock; pages["Sound"] = Sound; pages["Settings"] = new SettingsPage(preview);
            Content = layout; SelectPage("Home");
            Loaded += async delegate { Lock.InitializeSavedLocks(); if (Lock.NeedsLoading) await Lock.LoadApps(); };
            Closing += OnClosing;
            Closed += delegate { Shutdown.Dispose(); Lock.Dispose(); Sound.Dispose(); };
        }
        private global::Wpf.Ui.Controls.Button Nav(string name, string glyph)
        {
            var button = Ui.Button(name); button.Padding = new Thickness(16, 9, 16, 9);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(Ui.Text(name)); button.Content = panel;
            button.Click += delegate { SelectPage(name); }; navigation[name] = button; return button;
        }
        internal async void SelectPage(string name)
        {
            if (!pages.ContainsKey(name)) return;
            CurrentPage = name; content.Content = pages[name];
            foreach (var pair in navigation) pair.Value.Appearance = pair.Key == name ? ControlAppearance.Primary : ControlAppearance.Transparent;
            if (name == "Lock" && Lock.NeedsLoading) await Lock.LoadApps();
            if (name == "Sound" && Sound.NeedsLoading) await Sound.LoadDevices();
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!Lock.CanClose()) e.Cancel = true;
            // X closes the app. Saved locks remain available for the next launch.
        }
    }
}
