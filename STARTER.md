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
Frontend/Tabs/LockTab.cs contains app selection and password-guard actions.
Frontend/AppChecklist.cs uses the native double-buffered Windows list view for app selection and scrolling.
Frontend/PasswordDialog.cs contains the masked custom-password dialog.
Frontend/AppLockOverlay.cs covers the target windows and centers the password prompt over the app.
Frontend/LockCoverWindow.cs renders the soft blur and frosted tint.

Backend/ShutdownService.cs validates delays and invokes Windows shutdown/cancel commands.
Backend/InstalledApp.cs contains app identity and executable metadata.
Backend/InstalledAppCatalog.cs reads the Windows app catalog on a background STA thread.
Backend/PasswordStore.cs hashes, verifies, encrypts and saves the password verifier.
Backend/WindowBlur.cs creates a Gaussian-blurred snapshot in memory.
Backend/AppWindows.cs locates visible windows belonging to an app process.
Backend/AppGuardService.cs matches guarded processes, requests access and closes rejected instances in the current session.

## Shutdown

Enter a whole-number delay from 1 to 315,360,000 seconds and choose Schedule shutdown. Cancel pending shutdown aborts a pending Windows shutdown. Save your work: timed Windows shutdowns force apps to close. The OS schedule remains active after UtilityTool closes.

## Lock

Search the installed Windows app catalog and check apps. Portable/unregistered apps may be absent. Apps for which Windows supplies neither an executable path nor a packaged-app ID are marked guard unavailable and cannot be checked.

Set a custom password (8–256 characters), then Apply locks. Selected apps open and show a UtilityTool password prompt, including any already running when locks are applied. A correct password allows that process instance until it exits; reopening it asks again. A wrong password displays "The password is incorrect, the app will close now." and closes the instance after the message is dismissed. Cancelling also closes the instance. Rejected instances are retried without repeated password prompts. Background processes without visible windows do not trigger prompts. Unsaved work may be lost. Prompts are handled one at a time. Unlock apps requires the custom password. Closing the UtilityTool window or using Alt+F4 while locks are active requires the same password. Change password is available after unlocking and requires the existing password.

The password is never saved as plaintext. A random 32-byte salt and PBKDF2-HMAC-SHA256 verifier with 600,000 iterations are encrypted using Windows DPAPI for the current user and stored under %LOCALAPPDATA%\UtilityTool\password.dat. The custom password is independent of your Windows password. App selections and active locks last only for this UtilityTool session; the password persists.

This is a convenience guard, not a Windows security boundary. Someone using the same Windows account can bypass it with Task Manager, by terminating UtilityTool, tampering with its files/settings, or using other launch paths/processes. Detection happens after a process starts. A soft Gaussian-blurred snapshot with a frosted tint obscures app windows while the prompt is pending. The image is captured once before the cover appears, stretches if the window resizes, and is released after unlocking; no screenshots are written to disk. If capture fails, a light frosted cover is used. This is a visual blur, not Teams-style object/person segmentation. The app continues running underneath; the cover is not an OS access restriction. The guard cannot close protected/elevated apps that deny access and reports failures. Windows shutdown/logoff is allowed to close UtilityTool without a password. Strong protection requires separate Windows accounts and OS-enforced permissions/policies.

## VS Code project loading

UtilityTool.csproj now uses the SDK-style project format while continuing to target .NET Framework 4.8. UtilityTool.sln groups all frontend and backend source files into one project.

For this local checkout, a portable Microsoft .NET SDK and official .NET Framework 4.8 reference assemblies live under .tools/. Workspace settings use that SDK for the C# language server and new terminals. C# Dev Kit delegates project loading to the C# extension. After changing these settings, run Developer: Reload Window from the command palette.

The .tools/, bin/ and obj/ folders are ignored by Git. The portable SDK is not uploaded to GitHub. On another computer, install a .NET SDK and the .NET Framework 4.8 Developer Pack, then remove or update the local SDK paths in .vscode/settings.json. If a targeting pack is absent, the SDK can restore Microsoft's reference-assembly package automatically.

You can also build the solution with .tools\dotnet\dotnet.exe build UtilityTool.sln. The existing Ctrl+Shift+B and Run UtilityTool tasks still use the Windows Framework compiler and do not depend on the portable SDK.
