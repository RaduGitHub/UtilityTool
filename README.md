# UtilityTool
Everyday Windows utilities in one application.

- Shutdown timers.
- App locking with custom passwords.
- Playback through multiple sound outputs.

The `ui-redesign` branch tries a native WPF frontend with [WPF UI](https://github.com/lepoco/wpfui) 4.3.0. The original WinForms app is preserved on `main`.

Open `UtilityTool.sln` in Visual Studio with the .NET desktop development workload and .NET Framework 4.8 targeting pack. Build, then press F5. In VS Code, open this folder, press Ctrl+Shift+B, then run the **Run UtilityTool** task. A .NET SDK (8 or later) or the portable SDK in `.tools` is needed for the VS Code build.

The executable and dependencies are in `bin/Debug/net48`. Distribute that whole folder, rather than just the EXE. `--preview` displays sample apps and devices without modifying system settings; `--legacy` opens the previous WinForms frontend.

See [STARTER.md](STARTER.md) for behavior, source layout and returning to the saved version.
