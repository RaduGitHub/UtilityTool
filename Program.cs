using System;
using System.Windows.Forms;
using UtilityTool.Frontend;
namespace UtilityTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // bool background = Array.Exists(args, delegate(string arg) { return string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase); });
            // Application.Run(new MainWindow(background)); // Background mode temporarily disabled.
            if (Array.Exists(args, arg => arg == "--legacy")) { Application.Run(new MainWindow()); return; }
            var app = new System.Windows.Application();
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark });
            app.Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary { Source = new Uri("/UtilityTool;component/Frontend/Wpf/Styles.xaml", UriKind.Relative) });
            app.Run(new Frontend.Wpf.FluentMainWindow(Array.Exists(args, arg => arg == "--preview")));
        }
    }
}
