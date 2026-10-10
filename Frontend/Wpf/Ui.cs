using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using global::Wpf.Ui.Controls;
namespace UtilityTool.Frontend.Wpf
{
    internal static class Ui
    {
        internal static Brush Muted { get { return (Brush)Application.Current.FindResource("ToolMuted"); } }
        internal static Brush Accent { get { return (Brush)Application.Current.FindResource("ToolAccent"); } }
        internal static System.Windows.Controls.TextBlock Text(string text, double size = 14, bool muted = false)
        { return new System.Windows.Controls.TextBlock { Text = text, FontSize = size, Foreground = muted ? Muted : Brushes.WhiteSmoke, TextWrapping = TextWrapping.Wrap }; }
        internal static StackPanel Heading(string title, string subtitle)
        { var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 24) }; var heading = Text(title, 30); heading.FontWeight = FontWeights.SemiBold; panel.Children.Add(heading); var detail = Text(subtitle, 14, true); detail.Margin = new Thickness(0, 8, 0, 0); panel.Children.Add(detail); return panel; }
        internal static Border Card(UIElement child, Thickness? padding = null)
        { return new Border { Background = (Brush)Application.Current.FindResource("ToolSurface"), BorderBrush = (Brush)Application.Current.FindResource("ToolBorder"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = padding ?? new Thickness(24), Child = child }; }
        internal static global::Wpf.Ui.Controls.Button Button(string text, bool primary = false)
        { return new global::Wpf.Ui.Controls.Button { Content = text, Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary, Padding = new Thickness(18, 10, 18, 10), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) }; }
        internal static Grid Page()
        { return new Grid { Margin = new Thickness(32, 28, 32, 24) }; }
        internal static void Status(System.Windows.Controls.TextBlock label, string message, bool error = false)
        { label.Text = message; label.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 157, 157)) : Accent; }
    }
}
