using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
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
        internal readonly SoundboardPage Soundboard;
        internal readonly bool Preview;
        internal string CurrentPage { get; private set; }
        private WindowResizeBehavior resizing;
        internal FluentMainWindow(bool preview = false, Func<Window, bool, SoundboardPage> soundboardFactory = null)
        {
            Preview = preview;
            Title = preview ? "UtilityTool · Sample preview (actions disabled)" : "UtilityTool"; Width = MinWidth = 1040; Height = MinHeight = 740;
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
            foreach (var item in new[] { new[] { "Home", "\uE80F" }, new[] { "Shutdown", "\uE823" }, new[] { "Sound", "\uE767" }, new[] { "Soundboard", "\uE8D6" } }) nav.Children.Add(Nav(item[0], item[1]));
            Grid.SetRow(nav, 1); layout.Children.Add(nav); Grid.SetRow(content, 2); layout.Children.Add(content);
            Shutdown = new ShutdownPage(preview); Lock = new LockPage(this, preview); Sound = new SoundPage(preview);
            Soundboard = soundboardFactory == null ? new SoundboardPage(this, preview) : soundboardFactory(this, preview);
            pages["Home"] = new HomePage(SelectPage, preview); pages["Shutdown"] = Shutdown; pages["Sound"] = Sound; pages["Soundboard"] = Soundboard; pages["Settings"] = new SettingsPage(preview);
            Content = layout; SelectPage("Home");
            // Lock is temporarily hidden. Do not arm saved locks without an unlock tab.
            Loaded += async delegate { await Soundboard.InitializeAsync(); };
            Closing += OnClosing;
            Closed += delegate { if (resizing != null) resizing.Dispose(); Shutdown.Dispose(); Lock.Dispose(); Sound.Dispose(); Soundboard.Dispose(); };
        }
        protected override void SetWindowChrome()
        {
            base.SetWindowChrome();
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null && (ResizeMode == ResizeMode.CanResize || ResizeMode == ResizeMode.CanResizeWithGrip))
                chrome.ResizeBorderThickness = new Thickness(WindowResizeBehavior.BorderSize);
        }
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            // Attach after the Fluent title bar so the resize border takes priority.
            if (resizing == null) resizing = new WindowResizeBehavior(this);
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
            if (name == "Sound" && Sound.NeedsLoading) await Sound.LoadDevices();
            if (name == "Soundboard" && Soundboard.NeedsLoading) await Soundboard.LoadSounds();
        }
        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (!Lock.CanClose()) e.Cancel = true;
            // X closes the app and releases all audio streams.
        }
    }
}
