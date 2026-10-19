using System;
using System.Collections.Generic;
using System.Linq;
using UtilityTool.Backend;

internal static class RunningAppCatalogChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var browser = Process(10, "browser.exe", @"D:\Apps\Browser.exe");
        var windows = new List<RunningAppWindowSnapshot> { Window(100, "Browser first", browser), Window(101, "Browser second", browser, minimized: true) };
        var catalog = Catalog(() => windows); var apps = catalog.Read();
        check(apps.Count == 2 && apps.All(app => app.ProcessId == browser.ProcessId && app.StartTimeTicks == browser.StartTimeTicks && app.CanGuard), "Running windows retain verified process identity and executable guard targets");
        check(apps[0].WindowTitle == "Browser first" && apps[0].ProcessName == "browser.exe" && apps[0].ExecutablePath == browser.ExecutablePath && apps[0].WindowHandle == new IntPtr(100), "Each running row exposes its window title, executable name, path and exact handle");
        check(apps.Single(app => app.WindowHandle == new IntPtr(101)).IsMinimized, "Minimized user windows remain available in the running-program list");
        check(apps.Select(app => app.App.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 && apps[0].App.Id == catalog.Read()[0].App.Id, "Two windows of one executable share a stable app identity across refreshes");
        check(apps[0].App.ExecutablePath == browser.ExecutablePath && apps[0].App.CanGuard && apps[0].UnavailableReason == "", "A running row supplies an InstalledApp compatible with the existing guard and icon loader");
        check(catalog.ReadWindow(new IntPtr(100)).WindowTitle == "Browser first" && catalog.ReadWindow(new IntPtr(999)) == null && catalog.ReadWindow(IntPtr.Zero) == null, "ReadWindow resolves an exact current top-level handle and safely rejects missing handles");
        windows = new List<RunningAppWindowSnapshot> { Window(100, "Replacement", Process(11, "replacement.exe", @"D:\Apps\Replacement.exe")) };
        check(catalog.ReadWindow(new IntPtr(100)).ProcessId == 11, "ReadWindow obtains fresh metadata instead of reusing the previous handle's process identity");

        windows = new List<RunningAppWindowSnapshot> {
            Window(1, "UtilityTool", Process(777, "UtilityTool.exe", @"D:\UtilityTool\UtilityTool.exe")),
            Window(2, "Another instance", Process(22, "UtilityTool.exe", @"d:\utilitytool\UTILITYTOOL.EXE")),
            Window(3, "UtilityTool docs in browser", browser),
            Window(4, "Explorer", Process(40, "explorer.exe", @"C:\Windows\explorer.exe")),
            Window(5, "Settings", Process(50, "SystemSettings.exe", @"C:\Windows\ImmersiveControlPanel\SystemSettings.exe")),
            Window(6, "Windows Security", Process(60, "SecHealthUI.exe", @"C:\Program Files\WindowsApps\Security\SecHealthUI.exe", "Microsoft.SecHealthUI_test!App")),
            Window(7, "Ordinary app with shell-like name", Process(70, "explorer.exe", @"D:\Apps\explorer.exe"))
        };
        apps = catalog.Read();
        check(apps.Count == 2 && apps.Any(app => app.WindowTitle == "UtilityTool docs in browser"), "UtilityTool's PID and executable are excluded without hiding unrelated titles that mention UtilityTool");
        check(apps.Any(app => app.ProcessId == 70) && !apps.Any(app => app.ProcessId == 40 || app.ProcessId == 50 || app.ProcessId == 60), "Critical Windows shell/settings/security windows are excluded without filtering a user's same-named executable");
        windows = new List<RunningAppWindowSnapshot> {
            Window(10, "Visible", browser), new RunningAppWindowSnapshot(new IntPtr(11), "Hidden", 10, new[] { browser }, isVisible: false),
            new RunningAppWindowSnapshot(new IntPtr(12), "Tool", 10, new[] { browser }, isToolWindow: true),
            new RunningAppWindowSnapshot(new IntPtr(13), "Other desktop", 10, new[] { browser }, isCloaked: true), Window(14, "   ", browser), Window(0, "No handle", browser), Window(10, "Duplicate HWND", browser), null
        };
        check(catalog.Read().Count == 1, "Hidden, cloaked, untitled, tool, invalid and duplicate windows do not flood the program list");

        var host = Process(80, "ApplicationFrameHost.exe", @"C:\Windows\System32\ApplicationFrameHost.exe");
        var camera = Process(81, "WindowsCamera.exe", @"C:\Program Files\WindowsApps\Camera\WindowsCamera.exe", "Microsoft.WindowsCamera_test!App");
        windows = new List<RunningAppWindowSnapshot> { new RunningAppWindowSnapshot(new IntPtr(20), "Camera", host.ProcessId, new[] { host, camera }, isApplicationFrame: true) };
        var storeApp = catalog.Read().Single();
        check(storeApp.ProcessId == camera.ProcessId && storeApp.StartTimeTicks == camera.StartTimeTicks && storeApp.AppUserModelId == camera.AppUserModelId && storeApp.App.Id == camera.AppUserModelId && storeApp.CanGuard, "A shared Store-app frame resolves to its real app PID and stable package identity");
        check(storeApp.WindowHandle == new IntPtr(20) && storeApp.WindowTitle == "Camera", "A hosted Store row retains the visible frame handle and title for window picking");
        windows.Add(new RunningAppWindowSnapshot(new IntPtr(21), "Host only", host.ProcessId, new[] { host }, isApplicationFrame: true));
        check(catalog.Read().Count == 1, "A shared Windows frame without a real user-app child is never treated as a lock target");
        var unknown = Process(82, "", "", unavailable: "Access denied");
        windows[0] = new RunningAppWindowSnapshot(new IntPtr(20), "Camera", host.ProcessId, new[] { host, unknown, camera }, isApplicationFrame: true);
        check(catalog.Read().Single().ProcessId == camera.ProcessId, "A known packaged child is preferred over inaccessible unrelated host children");
        var identityOnly = Process(83, "Camera", "", "Microsoft.WindowsCamera_test!App"); windows = new List<RunningAppWindowSnapshot> { Window(30, "Camera", identityOnly) };
        check(catalog.Read().Single().CanGuard && catalog.Read().Single().App.Id == identityOnly.AppUserModelId, "A verified Store identity can remain guardable when Windows does not expose an executable path");

        windows = new List<RunningAppWindowSnapshot> {
            Window(40, "Healthy", browser), Window(41, "Access denied", Process(90, "", "", unavailable: "Windows denied access")),
            Window(42, "No metadata", Process(91, "", "")), Window(43, "Unverified", new RunningAppProcessSnapshot(92, 0, "test.exe", @"D:\Apps\test.exe")),
            Window(44, "Malformed path", Process(93, "broken.exe", "bad\0.exe"))
        };
        apps = catalog.Read();
        check(apps.Count == 5 && apps.Single(app => app.WindowTitle == "Healthy").CanGuard, "Inaccessible and malformed process metadata cannot empty the list or hide healthy apps");
        check(apps.Where(app => app.WindowTitle != "Healthy").All(app => !app.CanGuard && !string.IsNullOrWhiteSpace(app.UnavailableReason)), "Unavailable rows retain a clear explanation and cannot silently become guard targets");
        check(apps.Single(app => app.WindowTitle == "Access denied").UnavailableReason == "Windows denied access", "An access-denied process keeps its specific availability explanation");
        windows = new List<RunningAppWindowSnapshot> { Window(50, "Notepad", Process(94, "notepad.exe", @"C:\Windows\System32\notepad.exe")) };
        var systemUserApp = catalog.Read().Single();
        check(!systemUserApp.CanGuard && systemUserApp.UnavailableReason.Contains("policy"), "Ordinary system-folder app windows stay visible with the current guard-policy limitation explained");
        windows = new List<RunningAppWindowSnapshot> { Window(61, "Zulu", browser), Window(60, "Alpha", browser), Window(62, "Middle", browser) };
        check(catalog.Read().Select(app => app.WindowTitle).SequenceEqual(new[] { "Alpha", "Middle", "Zulu" }), "Running windows have stable title-based ordering rather than desktop z-order flicker");
        var processes = new List<RunningAppProcessSnapshot> { browser }; var immutable = new RunningAppWindowSnapshot(new IntPtr(70), "Snapshot", 10, processes); processes.Clear();
        check(immutable.Processes.Count == 1, "Captured process metadata cannot be changed by mutating its source collection");
        check(Catalog(() => null).Read().Count == 0, "An empty metadata snapshot produces an empty result without touching real windows");
        int reads = 0; var none = Catalog(() => { reads++; return new List<RunningAppWindowSnapshot>(); }); none.ReadWindow(IntPtr.Zero);
        check(reads == 0, "An invalid pick-window handle does not enumerate the desktop");
    }
    private static RunningAppCatalog Catalog(Func<List<RunningAppWindowSnapshot>> snapshot)
    { return new RunningAppCatalog(snapshot, 777, @"D:\UtilityTool\UtilityTool.exe", @"C:\Windows"); }
    private static RunningAppProcessSnapshot Process(int id, string name, string path, string modelId = "", string unavailable = "")
    { return new RunningAppProcessSnapshot(id, 100000 + id, name, path, modelId, unavailable); }
    private static RunningAppWindowSnapshot Window(int handle, string title, RunningAppProcessSnapshot process, bool minimized = false)
    { return new RunningAppWindowSnapshot(new IntPtr(handle), title, process.ProcessId, new[] { process }, isMinimized: minimized); }
}
