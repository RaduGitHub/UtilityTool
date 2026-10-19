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
        private List<TargetRule> targets = new List<TargetRule>();
        private readonly Dictionary<string, TargetRule> matchCache = new Dictionary<string, TargetRule>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<List<GuardProcess>> snapshot;
        private readonly Action<GuardProcess> stop;
        private readonly Func<DateTimeOffset> utcNow;
        private Timer timer;
        private WindowEventWatcher windowEvents;
        private readonly bool nativeEvents;
        private readonly HashSet<int> queuedProcesses = new HashSet<int>();
        private readonly HashSet<int> changedProcesses = new HashSet<int>();
        internal bool EventDriven { get { return windowEvents != null && windowEvents.Active; } }
        private int scanning;
        private int scanRequested;
        private bool armed;
        private int generation;
        private int targetVersion;
        private readonly Dictionary<string, AccessRequest> approved = new Dictionary<string, AccessRequest>();
        private readonly Dictionary<string, AccessRequest> rejected = new Dictionary<string, AccessRequest>();
        private readonly Dictionary<string, AccessRequest> pending = new Dictionary<string, AccessRequest>();
        // Only the legacy synchronous callback retains its close-on-denial policy.
        // A false modern asynchronous result never grants permission to stop an app.
        internal Func<GuardProcess, bool> RequestAccess { get; set; }
        internal Func<GuardProcess, Task<bool>> RequestAccessAsync;
        internal Func<GuardProcess, Task<GuardAccessResult>> RequestAccessWithPolicyAsync;
        internal int SessionVersion { get { lock (sync) return generation; } }
        internal event Action<string> StatusChanged;
        internal bool IsArmed { get { lock (sync) return armed; } }

        internal AppGuardService() : this(GetProcesses, StopProcess) { nativeEvents = true; }
        internal AppGuardService(Func<List<GuardProcess>> getProcesses, Action<GuardProcess> stopProcess, Func<DateTimeOffset> utcNow = null)
        { snapshot = getProcesses; stop = stopProcess; this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow); }

        internal void Arm(IEnumerable<InstalledApp> apps, bool startTimer)
        { StartMonitoring(apps, startTimer, false); }

        internal void StartMonitoring(IEnumerable<InstalledApp> apps, bool startTimer)
        { StartMonitoring(apps, startTimer, true); }

        private void StartMonitoring(IEnumerable<InstalledApp> apps, bool startTimer, bool allowEmpty)
        {
            var selected = ResolveTargets(apps);
            if (!allowEmpty && selected.Count == 0) throw new InvalidOperationException("Select at least one supported app.");
            lock (sync)
            {
                approved.Clear(); rejected.Clear(); pending.Clear();
                ReplaceTargets(selected, false);
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

        internal void UpdateTargets(IEnumerable<InstalledApp> apps)
        {
            int version;
            lock (sync)
            {
                if (!armed) throw new InvalidOperationException("Turn on protection before updating its apps.");
                version = generation;
            }
            // Resolve the entire selection before touching live rules. A missing
            // League installation or failed enumeration cannot partially apply it.
            var selected = ResolveTargets(apps);
            lock (sync)
            {
                if (!armed || generation != version) throw new InvalidOperationException("Protection changed while updating its apps. Try again.");
                ReplaceTargets(selected, true);
                RemoveStaleRequests(approved); RemoveStaleRequests(rejected); RemoveStaleRequests(pending);
            }
            RescanTargets();
        }

        internal void Relock(InstalledApp app = null)
        {
            lock (sync)
            {
                if (!armed) return;
                foreach (var rule in targets)
                    if (app == null || string.Equals(rule.App.Id, app.Id, StringComparison.OrdinalIgnoreCase))
                        rule.Version = NextTargetVersion();
                // Bump only the selected app's epoch. Old in-flight password
                // results cannot undo Lock now, and other prompts remain valid.
                RemoveStaleRequests(approved); RemoveStaleRequests(rejected); RemoveStaleRequests(pending);
            }
            RescanTargets();
        }

        internal InstalledApp GetProtectedApp(GuardProcess process)
        {
            lock (sync) { var rule = armed ? FindRule(process) : null; return rule == null ? null : rule.App; }
        }

        internal int GetTargetVersion(GuardProcess process)
        {
            lock (sync) { var rule = armed ? FindRule(process) : null; return rule == null ? 0 : rule.Version; }
        }

        internal bool IsApproved(InstalledApp app) { return HasAccess(approved, app); }
        internal bool IsPending(InstalledApp app) { return HasAccess(pending, app); }

        private bool HasAccess(Dictionary<string, AccessRequest> requests, InstalledApp app)
        {
            if (app == null) return false;
            lock (sync)
            {
                if (!armed) return false;
                bool approvals = ReferenceEquals(requests, approved);
                DateTimeOffset now = approvals ? utcNow() : default(DateTimeOffset);
                foreach (var request in requests.Values)
                    if (string.Equals(request.AppId, app.Id, StringComparison.OrdinalIgnoreCase) && CurrentRule(request) != null &&
                        (!approvals || !request.ExpiresAt.HasValue || request.ExpiresAt.Value > now)) return true;
                return false;
            }
        }

        internal bool Matches(InstalledApp app, GuardProcess process)
        {
            if (app == null || process == null) return false;
            try
            {
                TargetRule rule;
                lock (sync)
                {
                    foreach (var current in targets)
                        if (string.Equals(current.App.Id, app.Id, StringComparison.OrdinalIgnoreCase)) return current.Matches(process);
                    if (matchCache.TryGetValue(app.Id, out rule) && rule.SameApp(app)) return rule.Matches(process);
                }
                rule = new TargetRule(app);
                lock (sync) matchCache[app.Id] = rule;
                return rule.Matches(process);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.IO.IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static List<TargetRule> ResolveTargets(IEnumerable<InstalledApp> apps)
        {
            if (apps == null) throw new ArgumentNullException("apps");
            var selected = new List<TargetRule>();
            var ids = new Dictionary<string, TargetRule>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in apps)
            {
                var rule = new TargetRule(app);
                TargetRule previous;
                if (ids.TryGetValue(app.Id, out previous))
                {
                    if (!previous.SameApp(app)) throw new InvalidOperationException("Conflicting entries were selected for " + app.Name + ".");
                    continue;
                }
                ids.Add(app.Id, rule); selected.Add(rule);
            }
            return selected;
        }

        private void ReplaceTargets(List<TargetRule> selected, bool preserve)
        {
            foreach (var rule in selected)
            {
                if (preserve) foreach (var previous in targets)
                    if (previous.SameApp(rule.App)) { rule.Version = previous.Version; break; }
                if (rule.Version == 0) rule.Version = NextTargetVersion();
                matchCache[rule.App.Id] = rule;
            }
            targets = selected;
        }

        private int NextTargetVersion()
        {
            targetVersion = unchecked(targetVersion + 1);
            if (targetVersion == 0) targetVersion = unchecked(targetVersion + 1);
            return targetVersion;
        }

        private TargetRule CurrentRule(AccessRequest request)
        {
            foreach (var rule in targets)
                if (rule.Version == request.TargetVersion && string.Equals(rule.App.Id, request.AppId, StringComparison.OrdinalIgnoreCase) && rule.Matches(request.Process)) return rule;
            return null;
        }

        private TargetRule FindRule(GuardProcess process)
        {
            if (process == null) return null;
            // Retain an existing matched app when aliases share an executable;
            // adding another alias must not change an in-flight password's owner.
            string identity = Identity(process); AccessRequest existing;
            TargetRule rule;
            if ((pending.TryGetValue(identity, out existing) || approved.TryGetValue(identity, out existing) || rejected.TryGetValue(identity, out existing)) &&
                (rule = CurrentRule(existing)) != null && rule.Matches(process)) return rule;
            foreach (var candidate in targets) if (candidate.Matches(process)) return candidate;
            return null;
        }

        private void RemoveStaleRequests(Dictionary<string, AccessRequest> requests)
        {
            foreach (string identity in new List<string>(requests.Keys))
                if (CurrentRule(requests[identity]) == null) requests.Remove(identity);
        }

        private static string Identity(GuardProcess process) { return process.Id + ":" + process.StartTimeTicks; }

        private void RemoveExpiredApprovals()
        {
            DateTimeOffset now = utcNow();
            foreach (string identity in new List<string>(approved.Keys))
            {
                DateTimeOffset? expires = approved[identity].ExpiresAt;
                if (expires.HasValue && expires.Value <= now) approved.Remove(identity);
            }
        }

        private sealed class AccessRequest
        {
            internal readonly GuardProcess Process;
            internal readonly string AppId;
            internal readonly int TargetVersion;
            internal DateTimeOffset? ExpiresAt;
            internal AccessRequest(GuardProcess process, TargetRule rule)
            { Process = process; AppId = rule.App.Id; TargetVersion = rule.Version; }
        }

        private sealed class TargetRule
        {
            internal readonly InstalledApp App;
            private readonly HashSet<string> paths;
            private readonly string appId;
            private readonly VersionedAppExecutable versioned;
            internal int Version;
            internal TargetRule(InstalledApp app)
            {
                if (app == null) throw new ArgumentException("The selected app is missing.");
                if (string.IsNullOrWhiteSpace(app.Id) || !app.CanGuard) throw new InvalidOperationException("Cannot guard " + app.Name + ".");
                App = app;
                paths = new HashSet<string>(AppGuardTargets.Executables(app), StringComparer.OrdinalIgnoreCase);
                versioned = VersionedAppExecutable.From(app);
                // Store identity remains stable when its versioned EXE changes.
                appId = app.Id.IndexOf('!') > 0 ? app.Id : null;
                if (paths.Count == 0 && versioned == null && appId == null) throw new InvalidOperationException("Cannot locate " + app.Name + ".");
            }
            internal bool SameApp(InstalledApp app)
            {
                return string.Equals(App.Id, app.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(App.ExecutablePath ?? "", app.ExecutablePath ?? "", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(App.LaunchArguments ?? "", app.LaunchArguments ?? "", StringComparison.Ordinal);
            }
            internal bool Matches(GuardProcess process)
            {
                return process != null && (paths.Contains(process.Path ?? "") ||
                    (appId != null && string.Equals(appId, process.AppId, StringComparison.OrdinalIgnoreCase)) ||
                    (versioned != null && versioned.Matches(process.Path)));
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
                targets.Clear(); approved.Clear(); rejected.Clear(); pending.Clear();
            }
        }

        internal void ScanOnce()
        {
            if (Interlocked.Exchange(ref scanning, 1) != 0) return;
            Interlocked.Exchange(ref scanRequested, 0);
            try
            {
                int version;
                lock (sync) { if (!armed || targets.Count == 0) return; version = generation; }
                var currentProcesses = snapshot();
                var live = new HashSet<string>();
                foreach (var item in currentProcesses) live.Add(Identity(item));
                lock (sync)
                {
                    if (!armed || generation != version) return;
                    RemoveExpiredApprovals();
                    foreach (var requests in new[] { approved, rejected, pending })
                        foreach (string identity in new List<string>(requests.Keys))
                            if (!IsLive(identity, live)) requests.Remove(identity);
                }
                foreach (var process in currentProcesses)
                    ConsiderProcess(process, version);
            }
            catch (Exception error) { Publish("Guard scan failed: " + error.Message); }
            finally
            {
                Interlocked.Exchange(ref scanning, 0);
                if (Interlocked.Exchange(ref scanRequested, 0) != 0) ScanOnce();
            }
        }

        private void RescanTargets()
        {
            // A selection edit or Lock now during a scan needs one follow-up.
            // Ordinary reentrant status refreshes must not create a scan loop.
            Interlocked.Exchange(ref scanRequested, 1);
            ScanOnce();
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
                if (!armed || targets.Count == 0 || processId == 0) return;
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
            string identity = Identity(process);
            AccessRequest request;
            lock (sync)
            {
                if (!armed || generation != version) return;
                AccessRequest previousApproval;
                if (approved.TryGetValue(identity, out previousApproval) && previousApproval.ExpiresAt.HasValue && previousApproval.ExpiresAt.Value <= utcNow())
                    approved.Remove(identity);
                var rule = FindRule(process);
                if (rule == null) return;
                if (approved.ContainsKey(identity) || pending.ContainsKey(identity)) return;
                if (rejected.ContainsKey(identity))
                {
                    try { stop(process); }
                    catch (Exception error) { Publish("Could not close " + process.Name + ": " + error.Message); }
                    return;
                }
                // Reserve under the same lock as the checks to avoid duplicate prompts.
                request = new AccessRequest(process, rule);
                pending.Add(identity, request);
            }
            var policy = RequestAccessWithPolicyAsync;
            if (policy != null) { ObserveAccess(request, identity, version, policy); return; }
            var asynchronous = RequestAccessAsync;
            if (asynchronous != null)
            { ObserveAccess(request, identity, version, processValue => AdaptAccess(asynchronous, processValue)); return; }
            bool allowed;
            try { allowed = RequestAccess != null && RequestAccess(process); }
            catch { lock (sync) { RemovePending(request, identity); } throw; }
            lock (sync)
            {
                if (!IsCurrentRequest(request, identity, version)) return;
                pending.Remove(identity);
                if (allowed) { approved[identity] = request; Publish("Access allowed: " + process.Name); }
                else
                {
                    rejected[identity] = request;
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

        private bool IsCurrentRequest(AccessRequest request, string identity, int version)
        {
            AccessRequest current;
            return armed && generation == version && pending.TryGetValue(identity, out current) &&
                ReferenceEquals(current, request) && CurrentRule(request) != null;
        }

        private void RemovePending(AccessRequest request, string identity)
        {
            AccessRequest current;
            if (pending.TryGetValue(identity, out current) && ReferenceEquals(current, request)) pending.Remove(identity);
        }

        private static async Task<GuardAccessResult> AdaptAccess(Func<GuardProcess, Task<bool>> callback, GuardProcess process)
        { return new GuardAccessResult(await callback(process).ConfigureAwait(false)); }

        private async void ObserveAccess(AccessRequest request, string identity, int version, Func<GuardProcess, Task<GuardAccessResult>> callback)
        {
            try
            {
                var result = await callback(request.Process).ConfigureAwait(false);
                if (result == null) throw new InvalidOperationException("The app lock did not return an access decision.");
                lock (sync)
                {
                    if (!IsCurrentRequest(request, identity, version)) return;
                    // Validate and calculate before consuming the request, so a
                    // malformed policy or clock overflow cannot grant access.
                    GuardAccessResult.Validate(result.AllowedFor);
                    if (result.Allowed && result.AllowedFor.HasValue) request.ExpiresAt = utcNow().Add(result.AllowedFor.Value);
                    pending.Remove(identity);
                    if (result.Allowed) { approved[identity] = request; Publish("Access allowed: " + request.Process.Name); }
                    else Publish("App remains locked: " + request.Process.Name);
                }
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    if (IsCurrentRequest(request, identity, version))
                    { RemovePending(request, identity); Publish("Could not show app lock: " + error.Message); }
                }
            }
            finally { lock (sync) { RemovePending(request, identity); } }
        }

        private void Publish(string message)
        {
            var handler = StatusChanged;
            if (handler == null) return;
            // A closing UI subscriber must not turn a completed access check
            // into an unhandled async-void exception or block other subscribers.
            foreach (Action<string> subscriber in handler.GetInvocationList())
                try { subscriber(message); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
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
                        return new GuardProcess(process.Id, process.ProcessName, executable, appId, CreationTicks(handle));
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
            IntPtr handle = OpenProcess(0x1001, false, target.Id);
            if (handle == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 87) return; // The process has already exited.
                throw new Win32Exception(error);
            }
            try
            {
                // Bind both identity checks to the handle we would terminate.
                // A PID may be reused between opening separate process handles.
                if (target.StartTimeTicks != 0 && CreationTicks(handle) != target.StartTimeTicks) return;
                var path = new StringBuilder(32768); int size = path.Capacity;
                if (!QueryFullProcessImageName(handle, 0, path, ref size)
                    || !string.Equals(path.ToString(), target.Path, StringComparison.OrdinalIgnoreCase)) return;
                if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { CloseHandle(handle); }
        }

        private static long CreationTicks(IntPtr handle)
        {
            NativeFileTime created, exited, kernel, user;
            if (!GetProcessTimes(handle, out created, out exited, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return DateTime.FromFileTimeUtc(((long)created.High << 32) | created.Low).Ticks;
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeFileTime { internal uint Low, High; }

        public void Dispose() { Disarm(); }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out NativeFileTime created, out NativeFileTime exited, out NativeFileTime kernel, out NativeFileTime user);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }

    internal sealed class GuardAccessResult
    {
        internal readonly bool Allowed;
        internal readonly TimeSpan? AllowedFor;
        internal GuardAccessResult(bool allowed, TimeSpan? allowedFor = null)
        { Validate(allowedFor); Allowed = allowed; AllowedFor = allowedFor; }
        internal static void Validate(TimeSpan? duration)
        {
            if (duration.HasValue && (duration.Value < TimeSpan.FromSeconds(1) || duration.Value > TimeSpan.FromHours(24)))
                throw new ArgumentOutOfRangeException("allowedFor", "Choose an unlock duration between one second and 24 hours.");
        }
    }

    internal sealed class GuardProcess
    {
        internal readonly int Id;
        internal readonly long StartTimeTicks;
        internal readonly string Name, Path, AppId;
        internal GuardProcess(int id, string name, string path, string appId, long startTimeTicks = 0) { Id = id; Name = name; Path = path; AppId = appId; StartTimeTicks = startTimeTicks; }
    }
}
