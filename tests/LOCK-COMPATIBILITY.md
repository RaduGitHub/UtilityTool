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

The refreshed WPF Lock page has isolated checks for Running programs and Locked
programs, search/loading states, reused rows/icons, manual executable entries,
missing saved programs, cancelled window selection, password-gated removal and
monitoring changes, and settings-write or target-update failures. These inject
discovery results and the window-picker result; they do not click a real app or
apply protection to its process. Saved monitoring and existing metadata are
restored from fixture files, including monitoring with an empty saved list.

Guard checks use synthetic process snapshots and an injected clock to verify
adding/removing targets during monitoring, targeted/all-program Lock now,
independent process identities, timed expiry and rejection of stale password
results after a relock or target removal. Password-store checks use dedicated
temporary files for independent per-app hashes, global-password fallback,
override reset and damaged-file repair. Dialog checks cover masked custom
passwords, duration selection, retry and fail-closed handling of unavailable
credentials. No real password or protection settings are read or changed.

Wrong passwords retain the curtain and support retry. Keep locked/Escape/Alt+F4
dismiss the prompt without terminating the app; clicking the curtain returns to
password entry. The fixture checks this policy without locking installed apps.

Foreground-handoff checks reproduce a native password edit retaining this thread's focus while a different HWND is foreground. They verify a fresh activation request, bounded retries, preserving an already focused Unlock button, cancellation after switching apps or dismissing the prompt, and Enter submission. These checks inject foreground snapshots and activation outcomes; they do not certify live Firefox foreground behavior.

The APPLock-style update implements its selected workflow in UtilityTool's own
curtain guard. It does not include APPLock's native hook DLL or claim equivalent
Windows activation interception. The same first-frame, elevation and fullscreen
limitations still apply. The compatibility table describes resolver coverage and
earlier installed-path inspection, not new live end-to-end testing of those apps.
