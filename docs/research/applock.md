# APPLock reference study and UtilityTool implementation

Review completed on 2026-10-19. Reference: [APPLock by Buskysnow](https://apps.microsoft.com/detail/9PG38STH0TKS), Microsoft Store product `9PG38STH0TKS`, installed package version `1.7.0.0` (x64).

The requested scope is core app locking, a similar program-list layout, per-app passwords, timed unlock and **Lock now**. Idle relocking, quotas and schedules are outside this update.

## What was observed

The installed application's main window exposes monitoring status, **Running programs** and **Locked programs**, search by program/window title, per-row locking and a window picker. The protected list can retain programs that are not currently running. Public Store screenshots show an unlock password dialog with a **Time allowed for this unlock** choice. Store metadata describes per-app passwords and immediate relocking.

Only the main list views were inspected interactively. No reference-app passwords, private configuration or user content were read, and no apps were protected using the reference application during this study. Published features and screenshots establish the intended workflow; they do not prove how every runtime edge case behaves.

## Static analysis

[Reverse Engineer Anything (REA)](https://github.com/morluto/rea) version `6.7.0` was installed locally in the research workspace. Its [managed-code analysis](https://github.com/morluto/rea/blob/main/docs/managed-code-analysis.md) reported no CLI metadata in `AppLock.exe`. Together with its runtime configuration this is consistent with a native/AOT build; the original C# source was not recovered.

REA's native wrapper could not admit its Windows workspace under its private-DACL checks. Those checks were left intact. Ghidra `12.1.4` was run directly on a hash-verified local copy of the native helper instead. This provided partial static evidence, not a complete reconstruction of the main application.

Inspected SHA-256 identities:

| Artifact | SHA-256 |
| --- | --- |
| `AppLock.exe` | `330014c8952dc22b96b6ac4cb16f165e36fb06f0fec2b2dafa103a701b806066` |
| `AppLockHook.dll` | `1fe36ce85b5343efb17190620f16a8b370199572c41ccac98e096c1f40f33846` |

The x64 helper exports functions for blocked process IDs, exempt windows, thumbnails, z-order management and hook synchronization. Static analysis of `SyncHooks` found `SetWindowsHookExW` calls for `WH_CBT` and `WH_CALLWNDPROC`. The CBT callback checks activation against blocked process IDs and exempt windows, can veto activation, and signals the host. This is consistent with activation interception, but does not establish the behavior of the whole product. Microsoft documents those hook mechanisms in [SetWindowsHookExW](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw) and [CBTProc](https://learn.microsoft.com/en-us/windows/win32/winmsg/cbtproc).

No reference binaries, decompiled code, proprietary images or extracted assets are included in UtilityTool.

## Fresh implementation in UtilityTool

| Reference workflow | UtilityTool behavior |
| --- | --- |
| Running / Locked lists | Virtualized running-window list and durable protected-program list; search by title, executable or path. |
| Select a program | Lock a row, pick a foreground window after a three-second switch period, or add an executable without launching it. |
| Monitoring | Add/remove targets while monitoring stays active; preserve saved membership and monitoring status across restarts. |
| Per-app credentials | Use the UtilityTool password by default, or set a separate masked app password after authenticating with the UtilityTool password. |
| Timed unlock | Until that process exits, or 1, 5, 15, 30 or 60 minutes. Timed grants are in memory, scoped to a process and its start time. |
| Lock now | Revoke one program's grants, or all grants, and recheck running targets. Late password results cannot undo relocking. |
| Incorrect password / dismissal | Keep the app locked and permit another attempt; do not terminate the app. |

Per-app credentials use the existing salted PBKDF2-SHA256/DPAPI storage under `%LOCALAPPDATA%\UtilityTool\AppPasswords`. Filenames are derived from a SHA-256 hash of the durable app identity. Existing global password and app selections are retained. An unreadable custom credential keeps its app locked until the authenticated settings action repairs it; it does not silently fall back to the global password.

## Verification and limits

The integrated build passed 805 automated checks with zero build warnings or errors. Tests use owned windows, injected discovery/process fixtures and isolated settings, covering live target updates, expired grants, relocking races, credential failure handling, persistence, password focus, dismissal and curtain positioning. Captures were reviewed at the minimum window size and a wider size. Existing Shutdown, Sound and Soundboard regression checks also passed.

This is a fresh implementation of the chosen workflow on UtilityTool's existing per-window curtain. The reference application's native hook DLLs and injection mechanism are not implemented. A first-frame privacy guarantee, elevated/protected app support and parity with every third-party app are not established. UtilityTool must remain running, and this remains a convenience guard for a shared Windows account rather than a Windows security boundary. The automated fixtures do not certify live compatibility with Firefox, League of Legends, Camera or APPLock itself.
