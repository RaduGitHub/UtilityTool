# UtilityTool — WPF UI experiment

`main` preserves the original WinForms app and latest shutdown timer changes. `ui-redesign` contains this WPF experiment; it has not been merged into main.

## Build and run
Open UtilityTool.sln in Visual Studio with **.NET desktop development** and the **.NET Framework 4.8 targeting pack**. Restore packages, build, then run with F5. This app still targets .NET Framework 4.8; WPF UI 4.3.0 supplies Fluent controls.

In VS Code, open the repository folder and press Ctrl+Shift+B, then Terminal > Run Task > **Run UtilityTool**. The task uses build.ps1 and a .NET SDK (8 or later), preferring the existing portable .tools/dotnet installation. You can also run `dotnet build UtilityTool.sln`. Close running instances before rebuilding.

Use **bin/Debug/net48/UtilityTool.exe**, keeping its DLLs, configuration file and ThirdParty notices together when copying it to another computer. The root-level UtilityTool.exe may be an older build.

**Preview WPF UI (sample data)** passes --preview: sample apps/devices are displayed; shutdown, password, guard, audio and startup writes are disabled. **Run legacy WinForms UI** passes --legacy for the original frontend with the same backend. X closes both frontends; tray/background mode remains disabled.

## Source layout
Program.cs creates WPF resources and selects the frontend. Frontend/Wpf/FluentMainWindow.cs supplies the Fluent title bar, window controls and Home/Shutdown/Lock/Sound navigation, with Settings on the right. The initial window size is also the minimum size.

Frontend/Wpf/Pages has one file per page. Styles.xaml defines rounded app/device tiles; Ui.cs supplies shared cards/buttons/headings. TileGrid.cs virtualizes rows and recycles containers so only visible rows have controls. Boxes stretch until another column fits; icons/text remain fixed. Short lists keep default box sizes. Horizontal scrolling is disabled.

PasswordWindow.cs handles masked password creation/verification in the main app. The existing AppLockOverlay.cs, LockCoverWindow.cs and PasswordDialog.cs still handle external protected windows, preserving blur, positioning, dragging and focus behavior. Those external prompts retain their previous design in this experiment. Frontend/Tabs and other existing controls remain for --legacy.

Backend continues handling app discovery, icon caching, password storage, guard/process control, shutdown, startup and audio mirroring. No browser/default-app settings are modified.

## Shutdown
Hours accept 00–99; minutes/seconds accept 00–59. Typing shifts within a field: `5 → 4 → 3 → 2 → 1` displays `05 → 54 → 43 → 32 → 21`. Selecting another field starts a new sequence. Invalid minute/second values are rejected. Arrow buttons and Up/Down adjust each field independently; all-zero duration is rejected.

Save your work before shutdown. Windows may force apps to close when its timer ends. The OS schedule continues after UtilityTool closes. Cancel shutdown also attempts to cancel a Windows timer started elsewhere.

## Lock
Choose app tiles, set a custom password, then apply locks. Selections and locked/unlocked status are saved automatically. System tools/unsupported entries are hidden. Icons load progressively from the existing cache. Search and scrolling remain available while armed; changing selections requires unlocking.

Protected apps show the existing blur/password prompt. Correct passwords permit that process instance until it exits. Incorrect passwords or cancellation close the instance and may lose unsaved work. Closing UtilityTool while armed requires the password; saved locked status remains set for next launch. Stopping UtilityTool can bypass this guard; it is not a Windows security boundary.

Password length restrictions remain disabled for testing. Existing salted PBKDF2 hashes protected with Windows DPAPI are reused from %LOCALAPPDATA%/UtilityTool/password.dat. locks.xml and the icon cache are also reused. This migration does not reset them. Do not run both frontends at once; they share these files.

## Sound and startup
Choose a primary playback device and additional outputs. The existing Core Audio service mirrors sound without changing Windows defaults. Stop or close UtilityTool to release mirroring. Device selection is disabled while starting/stopping or running.

Settings contains **Start with Windows**, using the existing per-user startup entry. The setting changes only when toggled. Its command still includes --background, which is ignored; startup shows the normal window and restores saved locks.

## Verification
Run `tests/run-checks.ps1` on Windows after closing the app. It builds and exercises the preview UI, timer typing/bounds, navigation, window controls, virtualized resizing with 1,000 sample apps, password focus, the existing overlay against an owned test window, and fake guard/persistence services. It does not schedule shutdown, arm real apps or start audio playback. Screenshots and results are written under the chosen output directory (default: %TEMP%/UtilityTool-WpfChecks).

## Return to the saved version
Close UtilityTool, commit or stash outstanding edits, run `git switch main`, then rebuild. The original VS Code tasks/build script return with that branch. ui-redesign remains available. Use the executable from the current branch's build.
