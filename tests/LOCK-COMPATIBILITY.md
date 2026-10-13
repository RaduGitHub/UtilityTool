# App lock compatibility checks

UtilityTool places a password curtain over ordinary desktop windows. The checks
below distinguish installed-app detection from tests against an app's live UI.

| Priority apps | Detection covered | Verification |
| --- | --- | --- |
| Firefox, Chrome, Edge | Selected executable, independent process approval | Installed shortcuts inspected; guard matching tested with isolated process snapshots |
| Discord | Root launcher, versioned `app-*` executables, updates while armed | Installed executable paths inspected; updater and version matching tested |
| Steam | `steam.exe` and its installed `steamwebhelper.exe` UI processes | Installed helper paths inspected; guard target resolution tested |
| VS Code, VLC, 7-Zip | Selected executable | Installed shortcuts inspected; guard matching tested |
| League of Legends | League client, client UI, game executable; custom install directories | Installed Riot metadata inspected; launcher resolution tested; curtain fit tested with a borderless fixture |
| Camera, Teams, WhatsApp | Store app identity, including Windows-hosted app windows | Installed app IDs inspected; identity matching and a separate-process host fixture tested |
| Spotify (Store version) | Store app identity | Identity matching tested with an isolated process snapshot |

The borderless fixture checks visible frame coverage, square curtain corners,
drag offsets, maximize/restore, and cleanup. Other checks cover multiple pending
locks, rapid password input, slow X-button shutdown and a refused close.

Background-launch regression checks use delayed single-window and multi-window
clients behind a Chrome-style fixture. They verify prompt ownership, z-order,
input focus, text preservation, click recovery and Enter to unlock. Foreground
snapshots and foreground notifications are supplied by the test fixture because
the isolated runner cannot become the actual Windows foreground application;
the windows, controls, z-order and password verification are real.

These are not live end-to-end certifications of every app. Elevated/protected
processes and exclusive fullscreen games need separate validation. New launcher
layouts may also need another resolver. A desktop curtain remains a best-effort
guard while UtilityTool is running.

Run `tests/run-checks.ps1` for the regression suite. The compiled check executable
also accepts `--common-app-report` to report detection targets without applying
locks or starting the listed apps.
