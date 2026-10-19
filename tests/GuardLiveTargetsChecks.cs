using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UtilityTool.Backend;

internal static class GuardLiveTargetsChecks
{
    internal static void Run(Action<bool, string> check, string output)
    {
        LiveUpdates(check);
        EmptyAndRemovedTargets(check);
        AtomicFailure(check);
        FalseAndProcessIdentity(check);
        CompiledMatching(check, output);
        MultiProcessRelock(check);
        TimedAccess(check);
    }

    private static readonly InstalledApp Alpha = new InstalledApp("owned-alpha", "Alpha", "owned-alpha.exe");
    private static readonly InstalledApp Beta = new InstalledApp("owned-beta", "Beta", "owned-beta.exe");
    private static readonly InstalledApp Gamma = new InstalledApp("owned-gamma", "Gamma", "owned-gamma.exe");
    private static GuardProcess Process(int id, InstalledApp app, long created = 1)
    { return new GuardProcess(id, app.Name, app.ExecutablePath, "", created); }

    private static void LiveUpdates(Action<bool, string> check)
    {
        var alpha = Process(201, Alpha); var beta = Process(202, Beta); var gamma = Process(203, Gamma);
        var processes = new List<GuardProcess> { alpha, beta, gamma };
        var requests = new Dictionary<int, List<TaskCompletionSource<bool>>>(); int stopped = 0;
        using (var guard = new AppGuardService(() => processes, process => stopped++))
        {
            guard.RequestAccessAsync = process =>
            {
                List<TaskCompletionSource<bool>> entries;
                if (!requests.TryGetValue(process.Id, out entries)) requests.Add(process.Id, entries = new List<TaskCompletionSource<bool>>());
                var pending = new TaskCompletionSource<bool>(); entries.Add(pending);
                if (process.Id == alpha.Id && entries.Count == 1) pending.SetResult(true);
                return pending.Task;
            };
            guard.Arm(new[] { Alpha, Beta }, false); guard.ScanOnce();
            int session = guard.SessionVersion, alphaVersion = guard.GetTargetVersion(alpha), betaVersion = guard.GetTargetVersion(beta);
            check(guard.IsApproved(Alpha) && guard.IsPending(Beta) && !requests.ContainsKey(gamma.Id),
                "Captured guard status distinguishes an approved app, its independently pending neighbor and an unselected app");

            var renamedAlpha = new InstalledApp(Alpha.Id, "Renamed Alpha", Alpha.ExecutablePath);
            guard.UpdateTargets(new[] { renamedAlpha, Beta, Gamma });
            check(guard.IsArmed && guard.SessionVersion == session && guard.IsApproved(Alpha) && guard.IsPending(Beta) && guard.IsPending(Gamma) &&
                requests[alpha.Id].Count == 1 && requests[beta.Id].Count == 1 && requests[gamma.Id].Count == 1 &&
                guard.GetTargetVersion(alpha) == alphaVersion && guard.GetTargetVersion(beta) == betaVersion &&
                ReferenceEquals(guard.GetProtectedApp(alpha), renamedAlpha),
                "Adding a live target preserves existing approvals, pending prompts and their epochs without a disarm gap");
            requests[beta.Id][0].SetResult(true); Wait(() => guard.IsApproved(Beta));
            int gammaVersion = guard.GetTargetVersion(gamma);

            guard.UpdateTargets(new[] { Beta, Gamma });
            check(!guard.IsApproved(Alpha) && guard.GetProtectedApp(alpha) == null && guard.GetTargetVersion(alpha) == 0 && guard.IsApproved(Beta),
                "Removing one target revokes only its permission and leaves other approved targets unchanged");
            guard.UpdateTargets(new[] { Alpha, Beta, Gamma });
            check(requests[alpha.Id].Count == 2 && guard.IsPending(Alpha) && !guard.IsApproved(Alpha) && guard.GetTargetVersion(alpha) != alphaVersion &&
                guard.IsApproved(Beta) && guard.GetTargetVersion(gamma) == gammaVersion,
                "Re-adding the same running app requires fresh access instead of inheriting its removed approval");

            guard.Relock(Beta);
            check(!guard.IsApproved(Beta) && guard.IsPending(Beta) && requests[beta.Id].Count == 2 && guard.GetTargetVersion(beta) != betaVersion &&
                requests[gamma.Id].Count == 1 && guard.GetTargetVersion(gamma) == gammaVersion,
                "Lock now immediately reprompts every matched process for that app while retaining unrelated pending access");

            var staleAlpha = requests[alpha.Id][1];
            guard.Relock(Alpha);
            var currentAlpha = requests[alpha.Id][2];
            staleAlpha.SetResult(true); Wait(() => staleAlpha.Task.IsCompleted);
            check(guard.IsPending(Alpha) && !guard.IsApproved(Alpha) && requests[alpha.Id].Count == 3,
                "A password result from before Lock now cannot approve or erase the replacement access request");
            currentAlpha.SetResult(true); Wait(() => guard.IsApproved(Alpha));
            check(guard.IsApproved(Alpha) && !guard.IsPending(Alpha), "The replacement request can still approve its exact process instance");

            var staleBeta = requests[beta.Id][1]; var staleGamma = requests[gamma.Id][0];
            guard.Relock();
            check(guard.IsPending(Alpha) && guard.IsPending(Beta) && guard.IsPending(Gamma) && !guard.IsApproved(Alpha) &&
                requests[alpha.Id].Count == 4 && requests[beta.Id].Count == 3 && requests[gamma.Id].Count == 2 && guard.GetTargetVersion(gamma) != gammaVersion,
                "Relock all revokes all approvals and in-flight epochs, then scans running targets immediately");
            staleBeta.SetResult(false); staleGamma.SetResult(true);
            check(guard.IsPending(Beta) && guard.IsPending(Gamma) && !guard.IsApproved(Gamma) && stopped == 0,
                "Old true and false completions cannot affect fresh relock requests or terminate any process");
        }
    }

    private static void EmptyAndRemovedTargets(Action<bool, string> check)
    {
        var process = Process(301, Alpha); var requests = new List<TaskCompletionSource<bool>>();
        using (var guard = new AppGuardService(() => new List<GuardProcess> { process }, value => { throw new Exception("No process may stop."); }))
        {
            bool rejected = false; try { guard.Arm(new InstalledApp[0], false); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && !guard.IsArmed, "Legacy Arm still rejects an empty initial selection without starting protection");
            guard.StartMonitoring(new InstalledApp[0], false); guard.ScanOnce();
            check(guard.IsArmed && requests.Count == 0, "A saved empty monitoring session can restart without requesting access to any app");
            guard.Disarm();
            guard.RequestAccessAsync = value => { var waiting = new TaskCompletionSource<bool>(); requests.Add(waiting); return waiting.Task; };
            guard.Arm(new[] { Alpha }, false); guard.ScanOnce();
            int session = guard.SessionVersion, epoch = guard.GetTargetVersion(process);
            guard.UpdateTargets(new InstalledApp[0]);
            check(guard.IsArmed && guard.SessionVersion == session && !guard.IsPending(Alpha) && !guard.IsApproved(Alpha) && guard.GetProtectedApp(process) == null,
                "Removing the last target leaves monitoring armed with no stale pending access or permissions");
            guard.UpdateTargets(new[] { Alpha });
            requests[0].SetResult(true);
            check(requests.Count == 2 && guard.IsPending(Alpha) && !guard.IsApproved(Alpha) && guard.GetTargetVersion(process) != epoch,
                "A removed pending request cannot unlock the same target when it is added again");
            requests[1].SetResult(true); Wait(() => guard.IsApproved(Alpha));
            check(guard.IsApproved(Alpha), "A re-added target accepts only its new request's completion");
            guard.Disarm(); bool updateRejected = false;
            try { guard.UpdateTargets(new[] { Alpha }); } catch (InvalidOperationException) { updateRejected = true; }
            check(updateRejected && !guard.IsArmed && guard.GetTargetVersion(process) == 0,
                "Live updates cannot silently turn protection back on after it was disabled");
        }
    }

    private static void AtomicFailure(Action<bool, string> check)
    {
        var alpha = Process(401, Alpha); var beta = Process(402, Beta); int requests = 0;
        using (var guard = new AppGuardService(() => new List<GuardProcess> { alpha, beta }, value => { }))
        {
            guard.RequestAccessAsync = value => { requests++; return Task.FromResult(true); };
            guard.Arm(new[] { Alpha }, false); guard.ScanOnce();
            int epoch = guard.GetTargetVersion(alpha), session = guard.SessionVersion;
            bool failed = false; try { guard.UpdateTargets(FailedSelection()); } catch (InvalidOperationException) { failed = true; }
            guard.ScanOnce();
            check(failed && guard.IsArmed && guard.SessionVersion == session && guard.GetTargetVersion(alpha) == epoch &&
                guard.IsApproved(Alpha) && guard.GetProtectedApp(beta) == null && requests == 1,
                "A resolution failure after a valid new entry preserves the entire old rule set and its approvals atomically");

            using (var reached = new ManualResetEventSlim())
            using (var resume = new ManualResetEventSlim())
            {
                var update = Task.Run(() => guard.UpdateTargets(PausedSelection(reached, resume)));
                if (!reached.Wait(3000)) throw new TimeoutException("Owned target resolution did not pause.");
                check(guard.IsArmed && guard.IsApproved(Alpha) && guard.GetProtectedApp(beta) == null,
                    "Existing rules remain active while a slow replacement selection is being resolved");
                guard.ScanOnce(); resume.Set(); update.GetAwaiter().GetResult();
                check(guard.IsApproved(Alpha) && guard.IsApproved(Beta) && guard.SessionVersion == session && requests == 2,
                    "The completed replacement switches targets once and immediately scans only newly protected apps");
            }
        }
    }

    private static IEnumerable<InstalledApp> FailedSelection()
    { yield return Beta; yield return new InstalledApp("owned-unsupported", "Unsupported fixture"); }
    private static IEnumerable<InstalledApp> PausedSelection(ManualResetEventSlim reached, ManualResetEventSlim resume)
    { yield return Alpha; reached.Set(); if (!resume.Wait(3000)) throw new TimeoutException("Owned selection resolution was not released."); yield return Beta; }

    private static void FalseAndProcessIdentity(Action<bool, string> check)
    {
        int requests = 0, stopped = 0;
        using (var guard = new AppGuardService(() => new List<GuardProcess> { Process(501, Alpha) }, process => stopped++))
        {
            guard.RequestAccessAsync = process => { requests++; return Task.FromResult(false); };
            guard.StatusChanged += message => guard.ScanOnce();
            guard.Arm(new[] { Alpha }, false); guard.ScanOnce(); guard.ScanOnce();
            check(requests == 2 && stopped == 0 && !guard.IsApproved(Alpha) && !guard.IsPending(Alpha),
                "Modern false access completions remain retryable and never request process termination");
        }
        requests = stopped = 0;
        using (var guard = new AppGuardService(() => new List<GuardProcess> { Process(502, Alpha) }, process => stopped++))
        {
            guard.RequestAccess = process => { requests++; return false; };
            guard.Arm(new[] { Alpha }, false); guard.ScanOnce(); guard.ScanOnce();
            check(requests == 1 && stopped == 2, "The explicitly legacy synchronous denial contract retains its existing close fallback");
        }
        var processes = new List<GuardProcess> { Process(503, Alpha, 10) };
        var waiting = new List<TaskCompletionSource<bool>>();
        using (var guard = new AppGuardService(() => processes, process => { throw new Exception("No process may stop."); }))
        {
            guard.RequestAccessAsync = process => { var request = new TaskCompletionSource<bool>(); waiting.Add(request); return request.Task; };
            guard.Arm(new[] { Alpha }, false); guard.ScanOnce();
            processes[0] = Process(503, Alpha, 20); guard.ScanOnce(); waiting[0].SetResult(true);
            check(waiting.Count == 2 && guard.IsPending(Alpha) && !guard.IsApproved(Alpha),
                "A reused PID cannot inherit approval from a pending request for its earlier creation time");
            waiting[1].SetResult(true); Wait(() => guard.IsApproved(Alpha));
            processes.Clear(); guard.ScanOnce();
            check(!guard.IsApproved(Alpha) && !guard.IsPending(Alpha), "Captured approval and pending status are pruned when their process instance exits");
        }
    }

    private static void CompiledMatching(Action<bool, string> check, string output)
    {
        string directory = Path.Combine(output, "owned-compiled-target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string updater = Path.Combine(directory, "Update.exe"); File.WriteAllText(updater, "Owned text fixture; never executed.");
        var app = new InstalledApp("owned-versioned", "Versioned fixture", updater, "--processStart=Tool.exe");
        var process = new GuardProcess(601, app.Name, Path.Combine(directory, "app-9.8.7", "Tool.exe"), "", 1);
        try
        {
            using (var guard = new AppGuardService(() => new List<GuardProcess>(), value => { }))
            {
                check(guard.Matches(app, process) && !guard.IsArmed && guard.GetProtectedApp(process) == null,
                    "Running-app matching works while protection is off without opening processes or arming the guard");
                guard.Arm(new[] { app }, false); File.Delete(updater);
                check(ReferenceEquals(guard.GetProtectedApp(process), app) && guard.GetTargetVersion(process) != 0 && guard.Matches(app, process),
                    "Protected-app and epoch queries reuse captured version rules without re-resolving the installation from disk");
            }
            var store = new InstalledApp("Owned.Store_family!App", "Store fixture");
            using (var guard = new AppGuardService(() => new List<GuardProcess>(), value => { }))
                check(guard.Matches(store, new GuardProcess(602, store.Name, "new-version.exe", store.Id, 1)) &&
                    !guard.Matches(store, new GuardProcess(603, "Unrelated Store fixture", "new-version.exe", "Other.Store_family!App", 2)),
                    "Cached Store matching uses the selected stable application identity without matching another Store app");
        }
        finally { if (File.Exists(updater)) File.Delete(updater); Directory.Delete(directory); }
    }

    private static void MultiProcessRelock(Action<bool, string> check)
    {
        var processes = new List<GuardProcess> { Process(701, Alpha), Process(702, Alpha), Process(703, Beta) };
        int requested = 0; bool relocking = false;
        var pending = new List<TaskCompletionSource<bool>>();
        using (var guard = new AppGuardService(() => processes, process => { throw new Exception("No process may stop."); }))
        {
            guard.RequestAccessAsync = process =>
            {
                requested++;
                if (!relocking) return Task.FromResult(true);
                var request = new TaskCompletionSource<bool>(); pending.Add(request); return request.Task;
            };
            guard.Arm(new[] { Alpha, Beta }, false); guard.ScanOnce();
            relocking = true; guard.Relock(Alpha);
            check(requested == 5 && pending.Count == 2 && !guard.IsApproved(Alpha) && guard.IsPending(Alpha) && guard.IsApproved(Beta),
                "Relock one app revokes all of its matching process identities while retaining another app's approval");
            pending[0].SetResult(true); Wait(() => guard.IsApproved(Alpha));
            check(guard.IsApproved(Alpha) && guard.IsPending(Alpha),
                "App status can report one approved process and another independently pending process for the same app");
        }
    }

    private static void TimedAccess(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var alpha = Process(801, Alpha); var beta = Process(802, Beta);
        var processes = new List<GuardProcess> { alpha, beta };
        var renewal = new List<TaskCompletionSource<GuardAccessResult>>(); int alphaRequests = 0, betaRequests = 0, stopped = 0;
        using (var guard = new AppGuardService(() => processes, process => stopped++, () => now))
        {
            guard.RequestAccessAsync = process => { throw new Exception("The preferred policy callback must win."); };
            guard.RequestAccessWithPolicyAsync = process =>
            {
                if (process.Id == beta.Id) { betaRequests++; return Task.FromResult(new GuardAccessResult(true)); }
                alphaRequests++;
                if (alphaRequests == 1) return Task.FromResult(new GuardAccessResult(true, TimeSpan.FromSeconds(5)));
                var request = new TaskCompletionSource<GuardAccessResult>(); renewal.Add(request); return request.Task;
            };
            guard.Arm(new[] { Alpha, Beta }, false); guard.ScanOnce();
            int betaVersion = guard.GetTargetVersion(beta);
            now = now.AddSeconds(4); guard.UpdateTargets(new[] { Alpha, Beta });
            check(guard.IsApproved(Alpha) && guard.IsApproved(Beta) && alphaRequests == 1 && betaRequests == 1,
                "A policy callback is preferred, and an unchanged live update preserves the timed grant without renewing its deadline");
            now = now.AddSeconds(1);
            check(!guard.IsApproved(Alpha) && guard.IsApproved(Beta),
                "IsApproved reports expiry at the exact selected deadline while an until-exit grant remains valid");
            guard.ScanOnce();
            check(alphaRequests == 2 && betaRequests == 1 && guard.IsPending(Alpha) && guard.IsApproved(Beta) && guard.GetTargetVersion(beta) == betaVersion && stopped == 0,
                "The next scan reprompts only an expired app without terminating processes or disturbing another app's permission");
            renewal[0].SetResult(new GuardAccessResult(true, TimeSpan.FromSeconds(3))); Wait(() => guard.IsApproved(Alpha));
            now = now.AddSeconds(3);
            typeof(AppGuardService).GetMethod("ConsiderProcess", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(guard, new object[] { alpha, guard.SessionVersion });
            check(alphaRequests == 3 && guard.IsPending(Alpha) && !guard.IsApproved(Alpha) && stopped == 0,
                "An event-driven process consideration also replaces an expired timed grant with a fresh request");
        }
        int invalid = 0;
        foreach (TimeSpan duration in new[] { TimeSpan.FromSeconds(-1), TimeSpan.Zero, TimeSpan.FromMilliseconds(999), TimeSpan.FromHours(24).Add(TimeSpan.FromTicks(1)) })
            try { new GuardAccessResult(true, duration); } catch (ArgumentOutOfRangeException) { invalid++; }
        check(invalid == 4 && new GuardAccessResult(true, TimeSpan.FromSeconds(1)).Allowed && new GuardAccessResult(true, TimeSpan.FromHours(24)).Allowed,
            "Timed decisions admit one second through 24 hours and reject out-of-range durations before access can be granted");
        stopped = 0;
        using (var guard = new AppGuardService(() => new List<GuardProcess> { alpha }, process => stopped++, () => now))
        {
            guard.RequestAccessWithPolicyAsync = process => Task.FromResult(new GuardAccessResult(true, TimeSpan.Zero));
            guard.Arm(new[] { Alpha }, false); guard.ScanOnce();
            check(!guard.IsApproved(Alpha) && !guard.IsPending(Alpha) && stopped == 0,
                "A malformed duration returned by a policy callback cannot approve or terminate the protected app");
            guard.RequestAccessWithPolicyAsync = process => Task.FromResult<GuardAccessResult>(null); guard.ScanOnce();
            check(!guard.IsApproved(Alpha) && !guard.IsPending(Alpha) && stopped == 0,
                "A missing policy decision fails without granting access or leaving an unresponsive pending reservation");
            guard.RequestAccessWithPolicyAsync = process => Task.FromResult(new GuardAccessResult(false)); guard.ScanOnce();
            check(!guard.IsApproved(Alpha) && !guard.IsPending(Alpha) && stopped == 0,
                "An explicit Keep locked policy never invokes the legacy process-close delegate");
        }
    }

    private static void Wait(Func<bool> ready)
    {
        var elapsed = Stopwatch.StartNew();
        while (!ready() && elapsed.ElapsedMilliseconds < 5000) Thread.Sleep(1);
        if (!ready()) throw new TimeoutException("Owned guard state did not settle.");
    }
}
