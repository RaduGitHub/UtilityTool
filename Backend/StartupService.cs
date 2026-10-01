using System;
using Microsoft.Win32;
namespace UtilityTool.Backend
{
    internal static class StartupService
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal static string Command(string executable) { return "\"" + executable + "\" --background"; }
        internal static bool IsEnabled(string executable)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && string.Equals(key.GetValue("UtilityTool") as string,
                    Command(executable), StringComparison.OrdinalIgnoreCase);
        }
        internal static void SetEnabled(bool enabled, string executable)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new InvalidOperationException("Windows startup settings are unavailable.");
                if (enabled) key.SetValue("UtilityTool", Command(executable), RegistryValueKind.String);
                else key.DeleteValue("UtilityTool", false);
            }
        }
    }
}
