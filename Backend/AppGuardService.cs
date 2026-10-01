using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace UtilityTool.Backend
{
    // Best-effort guard for the current user session, not a Windows security boundary.
    internal sealed class AppGuardService : IDisposable
    {
        private readonly object sync = new object();
        private readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> appIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<List<GuardProcess>> snapshot;
        private readonly Action<GuardProcess> stop;
        private Timer timer;
        private int scanning;
        private bool armed;
        private int generation;
        private readonly HashSet<string> approved = new HashSet<string>();
        private readonly HashSet<string> rejected = new HashSet<string>();
        internal Func<GuardProcess, bool> RequestAccess;
        internal event Action<string> StatusChanged;
        internal bool IsArmed { get { lock (sync) return armed; } }

        internal AppGuardService() : this(GetProcesses, StopProcess) { }
        internal AppGuardService(Func<List<GuardProcess>> getProcesses, Action<GuardProcess> stopProcess)
        { snapshot = getProcesses; stop = stopProcess; }

        internal void Arm(IEnumerable<InstalledApp> apps, bool startTimer)
        {
            lock (sync)
            {
                paths.Clear(); appIds.Clear(); approved.Clear(); rejected.Clear();
                foreach (var app in apps)
                {
                    if (!app.CanGuard) throw new InvalidOperationException("Cannot guard " + app.Name + ".");
                    if (!string.IsNullOrEmpty(app.ExecutablePath)) paths.Add(app.ExecutablePath);
                    else appIds.Add(app.Id);
                }
                if (paths.Count + appIds.Count == 0) throw new InvalidOperationException("Select at least one supported app.");
                generation++;
                armed = true;
                if (timer != null) timer.Dispose();
                if (startTimer) timer = new Timer(delegate { ScanOnce(); }, null, 0, 750);
            }
        }

        internal void Disarm()
        {
            lock (sync)
            {
                armed = false; generation++;
                if (timer != null) { timer.Dispose(); timer = null; }
                paths.Clear(); appIds.Clear(); approved.Clear(); rejected.Clear();
            }
        }

        internal void ScanOnce()
        {
            if (Interlocked.Exchange(ref scanning, 1) != 0) return;
            try
            {
                int version;
                lock (sync) { if (!armed) return; version = generation; }
                var currentProcesses = snapshot();
                var live = new HashSet<string>();
                foreach (var item in currentProcesses) live.Add(item.Id + ":" + item.StartTimeTicks);
                lock (sync) { approved.IntersectWith(live); rejected.IntersectWith(live); }
                foreach (var process in currentProcesses)
                {
                    string identity = process.Id + ":" + process.StartTimeTicks;
                    lock (sync)
                    {
                        if (!armed || generation != version) return;
                        if (!paths.Contains(process.Path ?? "") && !appIds.Contains(process.AppId ?? "")) continue;
                        if (approved.Contains(identity)) continue;
                        if (rejected.Contains(identity))
                        {
                            try { stop(process); }
                            catch (Exception error) { Publish("Could not close " + process.Name + ": " + error.Message); }
                            continue;
                        }
                    }
                    bool allowed = RequestAccess != null && RequestAccess(process);
                    lock (sync)
                    {
                        if (!armed || generation != version) return;
                        if (allowed) { approved.Add(identity); Publish("Access allowed: " + process.Name); }
                        else
                        {
                            rejected.Add(identity);
                            try { stop(process); Publish("Closed: " + process.Name); }
                            catch (Exception error) { Publish("Could not close " + process.Name + ": " + error.Message); }
                        }
                    }
                }
            }
            catch (Exception error) { Publish("Guard scan failed: " + error.Message); }
            finally { Interlocked.Exchange(ref scanning, 0); }
        }

        private void Publish(string message)
        {
            var handler = StatusChanged;
            if (handler != null) handler(message);
        }

        private static List<GuardProcess> GetProcesses()
        {
            var list = new List<GuardProcess>();
            int ownId, ownSession;
            using (var own = Process.GetCurrentProcess()) { ownId = own.Id; ownSession = own.SessionId; }
            foreach (var process in Process.GetProcesses())
                using (process)
                {
                    if (process.Id == ownId) continue;
                    try { if (process.SessionId != ownSession) continue; }
                    catch (InvalidOperationException) { continue; }
                    IntPtr handle = OpenProcess(0x1000, false, process.Id);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        var path = new StringBuilder(32768); int size = path.Capacity;
                        string executable = QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : "";
                        uint length = 0; string appId = "";
                        if (GetApplicationUserModelId(handle, ref length, null) == 122 && length > 0 && length < 32768)
                        {
                            var id = new StringBuilder((int)length);
                            if (GetApplicationUserModelId(handle, ref length, id) == 0) appId = id.ToString();
                        }
                        if (AppWindows.Find(process.Id).Count == 0) continue;
                        list.Add(new GuardProcess(process.Id, process.ProcessName, executable, appId, process.StartTime.ToUniversalTime().Ticks));
                    }
                    catch (InvalidOperationException) { }
                    finally { CloseHandle(handle); }
                }
            return list;
        }

        private static void StopProcess(GuardProcess target)
        {
            using (var current = Process.GetProcessById(target.Id))
            {
                if (target.StartTimeTicks != 0 && current.StartTime.ToUniversalTime().Ticks != target.StartTimeTicks) return;
            }
            IntPtr handle = OpenProcess(0x1001, false, target.Id);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var path = new StringBuilder(32768); int size = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size)
                    || !string.Equals(path.ToString(), target.Path, StringComparison.OrdinalIgnoreCase)) return;
                if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { CloseHandle(handle); }
        }

        public void Dispose() { Disarm(); }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }

    internal sealed class GuardProcess
    {
        internal readonly int Id;
        internal readonly long StartTimeTicks;
        internal readonly string Name, Path, AppId;
        internal GuardProcess(int id, string name, string path, string appId, long startTimeTicks = 0) { Id = id; Name = name; Path = path; AppId = appId; StartTimeTicks = startTimeTicks; }
    }
}
