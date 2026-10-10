# UtilityTool

A Windows Forms app for .NET Framework 4.8. Open D:\Codes\UtilityTool as a local Windows folder in VS Code.

## Build and run

Press Ctrl+Shift+B to build. Choose Terminal > Run Task > Run UtilityTool to build and launch. Close a running instance before rebuilding. The default task calls the Windows C# compiler directly and works even when PowerShell scripts are disabled. These tasks run without debugging; F5 is not configured.

Alternatively use build.ps1 when your PowerShell policy allows scripts. Full Visual Studio can open UtilityTool.sln with the .NET desktop development workload and .NET Framework 4.8 targeting pack. Linux Codespaces can edit the source but cannot run this Windows UI.

## Source layout

Program.cs contains only the application entry point.

Frontend/AppTheme.cs defines the Graphite dark surfaces, rounded buttons, periwinkle accents, native window-frame colors and tab styling. Textures are cached to keep tab repainting fast.
Frontend/MainWindow.cs creates the window and coordinates tabs and closing.
Frontend/Tabs/HomeTab.cs contains the introduction.
Frontend/Tabs/ShutdownTab.cs contains shutdown controls and countdown presentation.
Frontend/DurationPicker.cs provides Windows Clock-style hour/minute/second fields with arrow buttons, direct typing, and keyboard up/down adjustments. Hours range from 00 to 99; minutes and seconds from 00 to 59. The default delay is one minute, and an all-zero duration is rejected. The countdown uses HH:MM:SS.
Frontend/Tabs/LockTab.cs contains app selection and password-guard actions.
Frontend/Tabs/SoundTab.cs contains audio-device selection and start/stop controls.
Frontend/AudioDevicePicker.cs uses cached device icons, names, selection marks and primary-output labels on a single drawing surface.
Frontend/AppChecklist.cs paints visible app tiles in one double-buffered control, with selection badges, keyboard navigation, tooltips and vertical scrolling.
Frontend/SelectionTileRenderer.cs shares icon spacing, labels, hover effects and selection styling between the Lock and Sound pickers.
Frontend/TileIconCache.cs scales icon images once per Windows DPI setting and reuses them while boxes resize. Frontend/DrawingBitmapCache.cs bounds each icon or label asset cache to 512 entries and 16 MiB. Labels keep a centered 144-logical-pixel wrapping area and their original font size, so box resizing does not redraw glyphs; color and DPI changes use separate cached labels. Refreshed icons release their old display images.
Frontend/TilePicker.cs shares visible-tile painting, scrolling, keyboard navigation, accessibility and resize handling between both pickers.
Frontend/TileRenderCache.cs reuses recently visible rendered tiles, with limits of 512 tiles and 32 MiB. Hover, focus, selection and incoming icons repaint only affected tiles. Wheel, scrollbar and keyboard scrolling reuse existing window pixels and repaint newly exposed rows, rather than invalidating the entire viewport. Changing tile sizes uses one reusable drawing buffer; item, font and icon changes invalidate cached content.
Frontend/ResponsiveTileGrid.cs grows tile boxes proportionally to fill each row until another minimum-sized column fits, then reduces box size and adds the column. Shrinking reverses that behavior. Icons remain 60 x 60 logical pixels, and fonts and selection badges keep their size while resizing; Windows DPI scaling still applies. The minimum box design size is 156 x 148 logical pixels, with DPI scaling and fixed gaps. Lists with fewer tiles than a full row use the default 156 x 148 logical-pixel boxes, aligned left, with empty space on the right. Full rows continue to grow and wrap responsively. Selection and scroll position are preserved while resizing. Both pickers use opaque surfaces, cached fonts and icons, and paint only visible tiles to reduce resizing cost.
Frontend/PasswordDialog.cs contains the masked custom-password dialog.
Frontend/AppLockOverlay.cs covers the target windows and centers the password prompt over the app.
Frontend/LockCoverWindow.cs renders the soft blur and frosted tint.

Backend/ShutdownService.cs validates delays and invokes Windows shutdown/cancel commands.
Backend/WindowsAudio.cs enumerates active playback endpoints and defines Windows Core Audio interop.
Backend/AudioMirrorService.cs captures primary-output audio and plays it on additional devices.
Backend/AudioFrameBuffer.cs keeps a bounded queue for each additional output.
Backend/InstalledApp.cs contains app identity and executable metadata.
Backend/InstalledAppCatalog.cs reads the Windows app catalog on a background STA thread.
Backend/AppIconService.cs loads Windows app icons progressively on a background STA thread, extracts desktop executable icons with their transparency masks at the requested size, falls back to Shell images for other apps, and reuses memory and disk caches. Cache versions invalidate older images when extraction changes.
Backend/PasswordStore.cs hashes, verifies, encrypts and saves the password verifier.
Backend/WindowBlur.cs creates a Gaussian-blurred snapshot in memory.
Backend/AppWindows.cs locates visible windows belonging to an app process.
Backend/AppGuardService.cs matches guarded processes, requests access and closes rejected instances in the current session.

## Shutdown

Enter a whole-number delay from 1 to 315,360,000 seconds and choose Schedule shutdown. Cancel pending shutdown aborts a pending Windows shutdown. Save your work: timed Windows shutdowns force apps to close. The OS schedule remains active after UtilityTool closes.

## Lock

Search the installed Windows app catalog and click an app tile to select it. The tiles match Sound: centered icons, names underneath, rounded highlights, corner selection badges and SELECTED captions. Full names are available on hover. Arrow keys navigate and Space toggles the focused app. App and icon loading starts in the background while Home is displayed. Icons appear as they become ready, prioritizing visible apps; refresh keeps existing icons in place. Icons are cached in memory and as PNG files under %LOCALAPPDATA%\UtilityTool\icons, so later launches reuse them. Desktop icons refresh when their executable changes; Store icons refresh weekly. Unavailable icons use a generic app icon. Only visible tiles are painted to keep large lists responsive. The grid wraps as the window resizes and uses vertical scrolling without a horizontal scrollbar. Portable/unregistered apps may be absent. Unsupported entries, tools whose executable is inside the Windows folder, and known Windows security/shell packages are hidden. Regular Store and third-party apps remain available; this is not an exact record of who installed each app.

Set a custom password (length restrictions temporarily disabled for testing, including single-letter passwords), then Apply locks. Selected apps open and show a UtilityTool password prompt, including any already running when locks are applied. A correct password allows that process instance until it exits; reopening it asks again. A wrong password displays "The password is incorrect, the app will close now." and closes the instance after the message is dismissed. Cancelling also closes the instance. Rejected instances are retried without repeated password prompts. Background processes without visible windows do not trigger prompts. Unsaved work may be lost. Each locked process has its own pending access request; a background app waiting for a password does not stop other app locks. Unlock apps requires the custom password. Background/tray mode is temporarily commented out. Closing the UtilityTool window or using Alt+F4 exits the app; exiting while locks are active requires the same password. Change password is available after unlocking and requires the existing password.

The password is never saved as plaintext. A random 32-byte salt and PBKDF2-HMAC-SHA256 verifier with 600,000 iterations are encrypted using Windows DPAPI for the current user and stored under %LOCALAPPDATA%\UtilityTool\password.dat. The custom password is independent of your Windows password. Selected apps and locked/unlocked status persist in %LOCALAPPDATA%\UtilityTool\locks.xml. Checkbox changes save automatically. Apply locks saves the locked status; Unlock apps saves the unlocked status. Startup restores active locks from saved app details immediately, when the main window opens. Password-authorized closing stops the current guard but preserves the locked status for the next launch. The password persists separately.

This is a convenience guard, not a Windows security boundary. Someone using the same Windows account can bypass it with Task Manager, by terminating UtilityTool, tampering with its files/settings, or using other launch paths/processes. Detection happens after a process starts. A soft Gaussian-blurred snapshot with a frosted tint covers the app while the prompt is pending. Its title bar forwards drag, minimize, maximize/restore and close commands to the underlying app without requiring a password; app content stays covered. Closing requests normal app closure and can show the app's own save confirmation. The password prompt is borderless and remains fixed to the app. Covers are owned by the target window and are not globally topmost; unrelated apps can cover them. A background app waits to display its password prompt until brought forward or its cover is clicked. The prompt is owned by the cover and tracks the app position, including moves and resizes. The target window renders its own client content through PrintWindow on a background worker, so overlapping apps are not captured. The image is captured once, stretches if the window resizes, and is released after unlocking; no screenshots are written to disk. Unsupported or timed-out captures retain a neutral frosted cover and never fall back to desktop pixels. If capture fails, a light frosted cover is used. This is a visual blur, not Teams-style object/person segmentation. The app continues running underneath; the cover is not an OS access restriction. The guard cannot close protected/elevated apps that deny access and reports failures. Windows shutdown/logoff is allowed to close UtilityTool without a password. Strong protection requires separate Windows accounts and OS-enforced permissions/policies.

## Sound

Open Sound to load active speakers, headphones, Bluetooth adapters and monitor outputs. The primary output defaults to the Windows multimedia default. Keep Windows and your apps playing through that output. Click one or more device icons to select additional outputs, then choose Start multiple outputs. Names appear below the icons, and selected devices show a check mark. The primary output is labeled and cannot be selected again as an additional output. Hover a device to see its full name; keyboard focus and Space also toggle selection. Start and Stop sit together on the left, with Refresh devices on the right. The primary device continues playing the original sound; UtilityTool mirrors its shared-mode audio to each checked output using WASAPI loopback. Windows converts the audio format for each device. Nothing changes the Windows default output, and audio is kept in memory rather than saved to disk.

Stop ends the additional playback. Closing UtilityTool also stops mirroring. Device selection and mirroring are not saved or started automatically. Stop and Refresh devices after changing outputs or reconnecting a device; an unavailable device stops the mirror and displays an error. The feature needs at least two active outputs. Independent device clocks and Bluetooth buffering can cause delay or imperfect synchronization; this does not promise sample-perfect synchronization. Audio routed by an app to a different device, exclusive-mode audio, and protected content may not be mirrored. Each output keeps its own Windows volume/mute settings.

## VS Code project loading

UtilityTool.csproj now uses the SDK-style project format while continuing to target .NET Framework 4.8. UtilityTool.sln groups all frontend and backend source files into one project.

For this local checkout, a portable Microsoft .NET SDK and official .NET Framework 4.8 reference assemblies live under .tools/. Workspace settings use that SDK for the C# language server and new terminals. C# Dev Kit delegates project loading to the C# extension. After changing these settings, run Developer: Reload Window from the command palette.

The .tools/, bin/ and obj/ folders are ignored by Git. The portable SDK is not uploaded to GitHub. On another computer, install a .NET SDK and the .NET Framework 4.8 Developer Pack, then remove or update the local SDK paths in .vscode/settings.json. If a targeting pack is absent, the SDK can restore Microsoft's reference-assembly package automatically.

You can also build the solution with .tools\dotnet\dotnet.exe build UtilityTool.sln. The existing Ctrl+Shift+B and Run UtilityTool tasks still use the Windows Framework compiler and do not depend on the portable SDK.

## Settings and startup
The cogwheel Settings tab sits beside Home, Shutdown and Lock and displays settings inside the main window. Start UtilityTool when I sign in to Windows registers the current executable for the current Windows user. Turning it off removes that registration. The startup command still includes --background, but that flag is temporarily ignored and startup launches show the main window; they restore selected apps and automatically arm the guard if the saved status is locked and the password is available. If you move UtilityTool.exe, enable startup again from its new location. Changes save immediately.
