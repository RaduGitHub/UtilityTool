using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace UtilityTool.Backend
{
    // Best-effort guard for the current user session, not a Windows security boundary.
    internal sealed class AppGuardService : IDisposable
    {
        private readonly object sync = new object();
        private readonly HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> appIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<VersionedAppExecutable> versionedExecutables = new List<VersionedAppExecutable>();
        private readonly Func<List<GuardProcess>> snapshot;
        private readonly Action<GuardProcess> stop;
        private Timer timer;
        private WindowEventWatcher windowEvents;
        private readonly bool nativeEvents;
        private readonly HashSet<int> queuedProcesses = new HashSet<int>();
        private readonly HashSet<int> changedProcesses = new HashSet<int>();
        internal bool EventDriven { get { return windowEvents != null && windowEvents.Active; } }
        private int scanning;
        private bool armed;
        private int generation;
        private readonly HashSet<string> approved = new HashSet<string>();
        private readonly HashSet<string> rejected = new HashSet<string>();
        private readonly HashSet<string> pending = new HashSet<string>();
        internal Func<GuardProcess, bool> RequestAccess { get; set; }
        internal Func<GuardProcess, Task<bool>> RequestAccessAsync;
        internal int SessionVersion { get { lock (sync) return generation; } }
        internal event Action<string> StatusChanged;
        internal bool IsArmed { get { lock (sync) return armed; } }

        internal AppGuardService() : this(GetProcesses, StopProcess) { nativeEvents = true; }
        internal AppGuardService(Func<List<GuardProcess>> getProcesses, Action<GuardProcess> stopProcess)
        { snapshot = getProcesses; stop = stopProcess; }

        internal void Arm(IEnumerable<InstalledApp> apps, bool startTimer)
        {
            var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedVersions = new List<VersionedAppExecutable>();
            foreach (var app in apps)
            {
                if (!app.CanGuard) throw new InvalidOperationException("Cannot guard " + app.Name + ".");
                foreach (string path in AppGuardTargets.Executables(app)) selectedPaths.Add(path);
                var versioned = VersionedAppExecutable.From(app);
                if (versioned != null) selectedVersions.Add(versioned);
                // A Store app may expose an executable AND a stable app ID.
                if (app.Id.IndexOf('!') > 0) selectedIds.Add(app.Id);
            }
            if (selectedPaths.Count + selectedIds.Count + selectedVersions.Count == 0) throw new InvalidOperationException("Select at least one supported app.");
            lock (sync)
            {
                paths.Clear(); appIds.Clear(); approved.Clear(); rejected.Clear(); pending.Clear();
                paths.UnionWith(selectedPaths); appIds.UnionWith(selectedIds);
                versionedExecutables.Clear(); versionedExecutables.AddRange(selectedVersions);
                generation++;
                armed = true;
                if (timer != null) { timer.Dispose(); timer = null; }
                if (windowEvents != null) { windowEvents.Dispose(); windowEvents = null; }
                queuedProcesses.Clear(); changedProcesses.Clear();
                if (startTimer)
                {
                    if (nativeEvents) windowEvents = new WindowEventWatcher(OnWindowEvent);
                    timer = new Timer(delegate { ScanOnce(); }, null, 0, EventDriven ? 3000 : 750);
                }
            }
        }

        internal void Disarm()
        {
            lock (sync)
            {
                armed = false; generation++;
                if (timer != null) { timer.Dispose(); timer = null; }
                if (windowEvents != null) { windowEvents.Dispose(); windowEvents = null; }
                queuedProcesses.Clear(); changedProcesses.Clear();
                paths.Clear(); appIds.Clear(); approved.Clear(); rejected.Clear(); pending.Clear();
                versionedExecutables.Clear();
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
                lock (sync)
                {
                    if (!armed || generation != version) return;
                    approved.RemoveWhere(identity => !IsLive(identity, live));
                    rejected.RemoveWhere(identity => !IsLive(identity, live));
                }
                foreach (var process in currentProcesses)
                    ConsiderProcess(process, version);
            }
            catch (Exception error) { Publish("Guard scan failed: " + error.Message); }
            finally { Interlocked.Exchange(ref scanning, 0); }
        }

        private void OnWindowEvent(IntPtr window, uint type)
        {
            if (type == WindowEventWatcher.Destroy || type == WindowEventWatcher.Hide) return;
            if (!AppWindows.Visible(window)) return;
            foreach (int processId in AppWindows.WindowProcessIds(window)) QueueProcess(processId);
        }

        private void QueueProcess(int processId)
        {
            int version;
            lock (sync)
            {
                if (!armed || processId == 0) return;
                if (!queuedProcesses.Add(processId)) { changedProcesses.Add(processId); return; }
                version = generation;
            }
            // Only inspect the process that created/showed the window. Coalesce its
            // event burst; never rescan every process on every desktop notification.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    while (true)
                    {
                        var process = ReadProcess(processId);
                        if (process != null) ConsiderProcess(process, version);
                        lock (sync)
                        {
                            if (!armed || generation != version) return;
                            if (changedProcesses.Remove(processId)) continue;
                            queuedProcesses.Remove(processId); return;
                        }
                    }
                }
                catch (Exception error)
                {
                    lock (sync) { if (generation == version) { queuedProcesses.Remove(processId); changedProcesses.Remove(processId); } }
                    Publish("Guard event failed: " + error.Message);
                }
            });
        }

        private void ConsiderProcess(GuardProcess process, int version)
        {
            string identity = process.Id + ":" + process.StartTimeTicks;
            lock (sync)
            {
                if (!armed || generation != version) return;
                bool matches = paths.Contains(process.Path ?? "") || appIds.Contains(process.AppId ?? "");
                if (!matches) foreach (var versioned in versionedExecutables) if (versioned.Matches(process.Path)) { matches = true; break; }
                if (!matches) return;
                if (approved.Contains(identity) || pending.Contains(identity)) return;
                if (rejected.Contains(identity))
                {
                    try { stop(process); }
                    catch (Exception error) { Publish("Could not close " + process.Name + ": " + error.Message); }
                    return;
                }
                // Reserve under the same lock as the checks to avoid duplicate prompts.
                pending.Add(identity);
            }
            if (RequestAccessAsync != null) { ObserveAccess(process, identity, version); return; }
            bool allowed;
            try { allowed = RequestAccess != null && RequestAccess(process); }
            catch { lock (sync) { if (generation == version) pending.Remove(identity); } throw; }
            lock (sync)
            {
                if (!armed || generation != version) return;
                pending.Remove(identity);
                if (allowed) { approved.Add(identity); Publish("Access allowed: " + process.Name); }
                else
                {
                    rejected.Add(identity);
                    try { stop(process); Publish("Closed: " + process.Name); }
                    catch (Exception error) { Publish("Could not close " + process.Name + ": " + error.Message); }
                }
            }
        }

        private bool IsLive(string identity, HashSet<string> snapshotIdentities)
        {
            if (!nativeEvents) return snapshotIdentities.Contains(identity);
            // Minimized or temporarily hidden windows still belong to the same
            // approved process. Only revoke its approval when that process exits.
            string[] parts = identity.Split(':');
            try
            {
                using (var process = Process.GetProcessById(int.Parse(parts[0])))
                    return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == long.Parse(parts[1]);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        private async void ObserveAccess(GuardProcess process, string identity, int version)
        {
            try
            {
                bool allowed = await RequestAccessAsync(process).ConfigureAwait(false);
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
            catch (Exception error) { Publish("Could not show app lock: " + error.Message); }
            finally { lock (sync) { if (generation == version) pending.Remove(identity); } }
        }

        private void Publish(string message)
        {
            var handler = StatusChanged;
            if (handler != null) handler(message);
        }

        private static List<GuardProcess> GetProcesses()
        {
            var list = new List<GuardProcess>();
            // One desktop enumeration, instead of one per running process.
            foreach (int id in AppWindows.VisibleProcessIds())
            { var process = ReadProcess(id); if (process != null) list.Add(process); }
            return list;
        }

        private static GuardProcess ReadProcess(int id)
        {
            int ownId, ownSession;
            using (var own = Process.GetCurrentProcess()) { ownId = own.Id; ownSession = own.SessionId; }
            if (id == ownId) return null;
            try
            {
                using (var process = Process.GetProcessById(id))
                {
                    if (process.SessionId != ownSession) return null;
                    IntPtr handle = OpenProcess(0x1000, false, process.Id);
                    if (handle == IntPtr.Zero) return null;
                    try
                    {
                        var path = new StringBuilder(32768); int size = path.Capacity;
                        string executable = QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : "";
                        uint length = 0; string appId = "";
                        if (GetApplicationUserModelId(handle, ref length, null) == 122 && length > 0 && length < 32768)
                        {
                            var applicationId = new StringBuilder((int)length);
                            if (GetApplicationUserModelId(handle, ref length, applicationId) == 0) appId = applicationId.ToString();
                        }
                        return new GuardProcess(process.Id, process.ProcessName, executable, appId, process.StartTime.ToUniversalTime().Ticks);
                    }
                    finally { CloseHandle(handle); }
                }
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (Win32Exception) { return null; }
        }

        private static void StopProcess(GuardProcess target)
        {
            try
            {
                using (var current = Process.GetProcessById(target.Id))
                {
                    if (current.HasExited) return;
                    if (target.StartTimeTicks != 0 && current.StartTime.ToUniversalTime().Ticks != target.StartTimeTicks) return;
                }
            }
            catch (ArgumentException) { return; }
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
