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
            Application.Run(new MainWindow());
        }
    }
}
