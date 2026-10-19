using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace UtilityTool.Backend
{
    internal sealed class RunningApp
    {
        public IntPtr WindowHandle { get; private set; }
        public int ProcessId { get; private set; }
        public long StartTimeTicks { get; private set; }
        public string WindowTitle { get; private set; }
        public string ProcessName { get; private set; }
        public string ExecutablePath { get; private set; }
        public string AppUserModelId { get; private set; }
        public bool IsMinimized { get; private set; }
        public InstalledApp App { get; private set; }
        public bool CanGuard { get { return App != null && string.IsNullOrEmpty(UnavailableReason); } }
        public string UnavailableReason { get; private set; }
        internal RunningApp(RunningAppWindowSnapshot window, RunningAppProcessSnapshot process, InstalledApp app, string unavailable)
        {
            WindowHandle = window.WindowHandle; WindowTitle = window.WindowTitle; IsMinimized = window.IsMinimized;
            ProcessId = process.ProcessId; StartTimeTicks = process.StartTimeTicks; ProcessName = process.ProcessName;
            ExecutablePath = process.ExecutablePath; AppUserModelId = process.AppUserModelId; App = app; UnavailableReason = unavailable ?? "";
        }
    }
    // Immutable raw metadata makes selection/filtering testable without enumerating the desktop.
    internal sealed class RunningAppProcessSnapshot
    {
        internal readonly int ProcessId;
        internal readonly long StartTimeTicks;
        internal readonly string ProcessName, ExecutablePath, AppUserModelId, UnavailableReason;
        internal RunningAppProcessSnapshot(int processId, long startTimeTicks, string processName, string executablePath, string appUserModelId = "", string unavailableReason = "")
        { ProcessId = processId; StartTimeTicks = startTimeTicks; ProcessName = processName ?? ""; ExecutablePath = executablePath ?? ""; AppUserModelId = appUserModelId ?? ""; UnavailableReason = unavailableReason ?? ""; }
    }
    internal sealed class RunningAppWindowSnapshot
    {
        internal readonly IntPtr WindowHandle;
        internal readonly int OwnerProcessId;
        internal readonly string WindowTitle;
        internal readonly bool IsVisible, IsMinimized, IsToolWindow, IsCloaked, IsApplicationFrame;
        internal readonly System.Collections.ObjectModel.ReadOnlyCollection<RunningAppProcessSnapshot> Processes;
        internal RunningAppWindowSnapshot(IntPtr windowHandle, string windowTitle, int ownerProcessId, IEnumerable<RunningAppProcessSnapshot> processes,
            bool isVisible = true, bool isMinimized = false, bool isToolWindow = false, bool isCloaked = false, bool isApplicationFrame = false)
        {
            WindowHandle = windowHandle; WindowTitle = (windowTitle ?? "").Trim(); OwnerProcessId = ownerProcessId;
            IsVisible = isVisible; IsMinimized = isMinimized; IsToolWindow = isToolWindow; IsCloaked = isCloaked; IsApplicationFrame = isApplicationFrame;
            Processes = new List<RunningAppProcessSnapshot>(processes ?? new RunningAppProcessSnapshot[0]).AsReadOnly();
        }
    }
    internal sealed class RunningAppCatalog
    {
        private readonly Func<List<RunningAppWindowSnapshot>> snapshot;
        private readonly int ownProcessId;
        private readonly string ownExecutable, windowsDirectory;
        private static readonly HashSet<string> criticalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "explorer", "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "SearchUI", "TextInputHost", "RuntimeBroker",
            "SystemSettings", "SecHealthUI", "SecurityHealthSystray", "SecurityHealthService", "CredentialUIBroker", "consent", "LogonUI", "winlogon", "lsass", "services", "smss", "csrss", "dwm", "sihost", "taskhostw", "LockApp"
        };
        private static readonly string[] criticalPackages = {
            "Microsoft.AAD.BrokerPlugin_", "Microsoft.AccountsControl_", "Microsoft.LockApp_", "Microsoft.SecHealthUI_", "Microsoft.Windows.SecHealthUI_",
            "Microsoft.Windows.ShellExperienceHost_", "Microsoft.Windows.StartMenuExperienceHost_", "MicrosoftWindows.Client.CBS_", "MicrosoftWindows.Client.Core_", "windows.immersivecontrolpanel_"
        };
        internal RunningAppCatalog(Func<List<RunningAppWindowSnapshot>> snapshot = null, int? ownProcessId = null, string ownExecutable = null, string windowsDirectory = null)
        {
            this.snapshot = snapshot ?? ReadNativeWindows; this.ownProcessId = ownProcessId ?? CurrentProcessId();
            this.ownExecutable = ownExecutable ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UtilityTool.exe");
            this.windowsDirectory = windowsDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        }
        internal List<RunningApp> Read()
        {
            var result = new List<RunningApp>(); var windows = snapshot() ?? new List<RunningAppWindowSnapshot>(); var seen = new HashSet<IntPtr>();
            foreach (var window in windows)
            {
                if (window == null || window.WindowHandle == IntPtr.Zero || !window.IsVisible || window.IsToolWindow || window.IsCloaked || string.IsNullOrWhiteSpace(window.WindowTitle) || !seen.Add(window.WindowHandle)) continue;
                try
                {
                    var process = SelectProcess(window); if (process == null || IsExcluded(process)) continue;
                    InstalledApp app = null; string unavailable = process.UnavailableReason;
                    string name = string.IsNullOrWhiteSpace(process.ProcessName) ? window.WindowTitle : Path.GetFileNameWithoutExtension(process.ProcessName);
                    if (string.IsNullOrWhiteSpace(name)) name = window.WindowTitle;
                    string appId = process.AppUserModelId;
                    if (appId.IndexOf('!') > 0) app = new InstalledApp(appId, name, process.ExecutablePath);
                    else if (!string.IsNullOrWhiteSpace(process.ExecutablePath) && Path.IsPathRooted(process.ExecutablePath)) app = new InstalledApp("running:" + Path.GetFullPath(process.ExecutablePath).ToUpperInvariant(), name, process.ExecutablePath);
                    if (string.IsNullOrEmpty(unavailable) && process.StartTimeTicks <= 0) unavailable = "Windows could not verify this process instance. Refresh and try again.";
                    if (string.IsNullOrEmpty(unavailable) && app == null) unavailable = "Windows did not provide an executable or Store-app identity for this window.";
                    if (string.IsNullOrEmpty(unavailable) && IsWithin(process.ExecutablePath, windowsDirectory)) unavailable = "Windows applications in the system folder are not supported by the current app-lock policy.";
                    if (string.IsNullOrEmpty(unavailable) && app != null && !InstalledAppCatalog.IsSelectable(app)) unavailable = "This app is not supported by the current app-lock policy.";
                    result.Add(new RunningApp(window, process, app, unavailable));
                }
                catch (Exception error)
                {
                    // Bad metadata for one window must not hide unrelated programs.
                    var process = window.Processes.FirstOrDefault(value => value != null && value.ProcessId == window.OwnerProcessId);
                    if (process != null && !IsExcluded(process)) result.Add(new RunningApp(window, process, null, "This window's app information is unavailable: " + error.Message));
                }
            }
            return result.OrderBy(app => app.WindowTitle, StringComparer.CurrentCultureIgnoreCase).ThenBy(app => app.ProcessId).ThenBy(app => app.WindowHandle.ToInt64()).ToList();
        }
        // Re-read at selection time; do not turn an old row/PID into a fresh app rule.
        internal RunningApp ReadWindow(IntPtr topLevelHandle)
        { return topLevelHandle == IntPtr.Zero ? null : Read().FirstOrDefault(app => app.WindowHandle == topLevelHandle); }
        private RunningAppProcessSnapshot SelectProcess(RunningAppWindowSnapshot window)
        {
            if (!window.IsApplicationFrame) return window.Processes.FirstOrDefault(value => value != null && value.ProcessId == window.OwnerProcessId);
            // A shared frame is not the app. Prefer its packaged user-app child.
            return window.Processes.Where(value => value != null && value.ProcessId != window.OwnerProcessId && !IsExcluded(value))
                .OrderByDescending(value => value.AppUserModelId.IndexOf('!') > 0).ThenByDescending(value => string.IsNullOrEmpty(value.UnavailableReason)).ThenBy(value => value.ProcessId).FirstOrDefault();
        }
        private bool IsExcluded(RunningAppProcessSnapshot process)
        {
            if (process.ProcessId <= 0 || process.ProcessId == ownProcessId || SamePath(process.ExecutablePath, ownExecutable)) return true;
            foreach (string prefix in criticalPackages) if (process.AppUserModelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            string name;
            try { name = Path.GetFileNameWithoutExtension(process.ProcessName); } catch (ArgumentException) { name = ""; }
            return criticalNames.Contains(name) && (string.IsNullOrEmpty(process.ExecutablePath) || IsWithin(process.ExecutablePath, windowsDirectory));
        }
        private static bool SamePath(string left, string right)
        { try { return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); } catch (ArgumentException) { return false; } catch (NotSupportedException) { return false; } catch (IOException) { return false; } }
        private static bool IsWithin(string path, string directory)
        { try { return !string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(directory) && Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); } catch (ArgumentException) { return false; } catch (NotSupportedException) { return false; } catch (IOException) { return false; } }
        private static int CurrentProcessId() { using (var process = Process.GetCurrentProcess()) return process.Id; }

        private static List<RunningAppWindowSnapshot> ReadNativeWindows()
        {
            var windows = new List<RunningAppWindowSnapshot>(); var processes = new Dictionary<int, RunningAppProcessSnapshot>();
            uint session; if (!ProcessIdToSessionId((uint)CurrentProcessId(), out session)) throw new Win32Exception(Marshal.GetLastWin32Error());
            bool enumerated = EnumWindows(delegate(IntPtr window, IntPtr unused)
            {
                try
                {
                    if (!IsWindowVisible(window)) return true;
                    int length = Math.Min(4096, GetWindowTextLength(window)); if (length <= 0) return true;
                    var title = new StringBuilder(length + 1); GetWindowText(window, title, title.Capacity); if (string.IsNullOrWhiteSpace(title.ToString())) return true;
                    long styles = IntPtr.Size == 8 ? GetWindowLongPtr(window, -20).ToInt64() : GetWindowLong(window, -20);
                    if ((styles & 0x80) != 0) return true; // WS_EX_TOOLWINDOW
                    int cloaked; if (DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                    int owner = AppWindows.ProcessId(window); var values = new List<RunningAppProcessSnapshot>();
                    foreach (int id in AppWindows.WindowProcessIds(window))
                    {
                        RunningAppProcessSnapshot process; if (!processes.TryGetValue(id, out process)) { process = ReadNativeProcess(id, session); processes[id] = process; }
                        if (process != null) values.Add(process);
                    }
                    windows.Add(new RunningAppWindowSnapshot(window, title.ToString(), owner, values, isMinimized: IsIconic(window), isApplicationFrame: AppWindows.IsApplicationFrame(window)));
                }
                catch (Exception) { /* A closing/inaccessible window must not abort enumeration. */ }
                return true;
            }, IntPtr.Zero);
            if (!enumerated) throw new Win32Exception(Marshal.GetLastWin32Error());
            return windows;
        }
        private static RunningAppProcessSnapshot ReadNativeProcess(int id, uint expectedSession)
        {
            uint session; if (!ProcessIdToSessionId((uint)id, out session) || session != expectedSession) return null;
            IntPtr handle = OpenProcess(0x1000, false, id);
            if (handle == IntPtr.Zero) return new RunningAppProcessSnapshot(id, 0, "", "", unavailableReason: "Windows denied access to this process's app information. Its window can be listed, but cannot be added safely.");
            try
            {
                var path = new StringBuilder(32768); int length = path.Capacity; string executable = QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : "";
                string name = string.IsNullOrEmpty(executable) ? "" : Path.GetFileName(executable), appId = ""; uint appLength = 0;
                if (GetApplicationUserModelId(handle, ref appLength, null) == 122 && appLength > 0 && appLength < 32768)
                { var modelId = new StringBuilder((int)appLength); if (GetApplicationUserModelId(handle, ref appLength, modelId) == 0) appId = modelId.ToString(); }
                NativeFileTime created, exited, kernel, user; long started = 0;
                if (GetProcessTimes(handle, out created, out exited, out kernel, out user)) started = DateTime.FromFileTimeUtc(((long)created.High << 32) | created.Low).Ticks;
                return new RunningAppProcessSnapshot(id, started, name, executable, appId);
            }
            catch (Exception error) { return new RunningAppProcessSnapshot(id, 0, "", "", unavailableReason: "Windows could not read this process's app information: " + error.Message); }
            finally { CloseHandle(handle); }
        }
        private delegate bool EnumCallback(IntPtr window, IntPtr unused);
        [StructLayout(LayoutKind.Sequential)] private struct NativeFileTime { internal uint Low, High; }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumCallback callback, IntPtr unused);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(uint process, out uint session);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder appId);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out NativeFileTime created, out NativeFileTime exited, out NativeFileTime kernel, out NativeFileTime user);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr process);
    }
}
