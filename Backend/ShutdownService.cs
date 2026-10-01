using System;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace UtilityTool.Backend
{
    internal static class ShutdownService
    {
        internal const string CancelArguments = "/a";

        internal static bool TryParseSeconds(string text, out int seconds)
        {
            return int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out seconds)
                && seconds >= 1 && seconds <= 315360000;
        }

        internal static string ScheduleArguments(int seconds)
        {
            if (seconds < 1 || seconds > 315360000) throw new ArgumentOutOfRangeException("seconds");
            return "/s /t " + seconds.ToString(CultureInfo.InvariantCulture);
        }

        internal static void Execute(string arguments)
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
                Arguments = arguments, UseShellExecute = false, CreateNoWindow = true
            };
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Windows could not start the shutdown command.");
                process.WaitForExit();
                if (process.ExitCode != 0) throw new Win32Exception(process.ExitCode);
            }
        }
    }


}
