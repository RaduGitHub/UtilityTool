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

PasswordWindow.cs handles masked password creation/verification in the main app. AppLockOverlay.cs, LockCoverWindow.cs and PasswordDialog.cs handle external protected windows with a solid Privacy curtain and centered password form matching UtilityTool's dark theme. The curtain requires no screenshots or blur processing. Native positioning, dragging, window controls and focus behavior are preserved. Frontend/Tabs and other existing controls remain for --legacy.

Backend continues handling app discovery, icon caching, password storage, guard/process control, shutdown, startup and audio mirroring. No browser/default-app settings are modified.

## Shutdown
Hours accept 00–99; minutes/seconds accept 00–59. Typing shifts within a field: `5 → 4 → 3 → 2 → 1` displays `05 → 54 → 43 → 32 → 21`. Selecting another field starts a new sequence. Invalid minute/second values are rejected. Arrow buttons and Up/Down adjust each field independently; all-zero duration is rejected.

Save your work before shutdown. Windows may force apps to close when its timer ends. The OS schedule continues after UtilityTool closes. Cancel shutdown also attempts to cancel a Windows timer started elsewhere.

## Lock
The tab is temporarily hidden in both frontends. Saved lock selections/passwords remain intact, but saved locks do not automatically arm while there is no Unlock tab. The behavior below describes the preserved implementation for future testing.

Choose app tiles, set a custom password, then apply locks. Selections and locked/unlocked status are saved automatically. System tools/unsupported entries are hidden. Icons load progressively from the existing cache. Search and scrolling remain available while armed; changing selections requires unlocking.

Protected apps show an opaque Privacy curtain with masked password input; press Enter or click Unlock to submit. The curtain follows the protected window at its stacking level, and its title bar forwards move/minimize/maximize/close actions to that app. Correct passwords permit that process instance until it exits. Incorrect passwords or cancellation close the instance and may lose unsaved work. Closing UtilityTool while armed requires the password; saved locked status remains set for next launch. Stopping UtilityTool can bypass this guard; it is not a Windows security boundary.

During a title-bar drag, WindowDragMask temporarily suppresses the protected window's drawing region so its edges cannot trail behind the curtain. It preserves the original region (including custom shapes), moves the app to the final curtain position, and restores drawing on release or overlay disposal. It does not hide the native window or change its ownership or window state.

While armed, the guard listens for top-level window creation/show/activation events and checks only the process involved. A backup scan runs every three seconds and enumerates the desktop once, rather than once per process. Active curtains follow window events with a one-second backup; process-exit notifications release them promptly. If event hooks cannot be installed, the previous polling intervals are used. Disarming removes the hooks. Approved processes remain approved while minimized until the process exits.

Window-event notifications are asynchronous. The normal guard reduces launch/reopen delay but cannot promise to hide the first frame of an app launched outside UtilityTool. Strict password-before-launch behavior would require a controlled launcher or Windows-level restrictions. UtilityTool must already be running with locks armed.

Password length restrictions remain disabled for testing. Existing salted PBKDF2 hashes protected with Windows DPAPI are reused from %LOCALAPPDATA%/UtilityTool/password.dat. locks.xml and the icon cache are also reused. This migration does not reset them. Do not run both frontends at once; they share these files.

## Sound and startup
Windows' current default playback device is the selected primary tile. Choose additional outputs to mirror sound without changing Windows defaults. Stop or close UtilityTool to release mirroring. Device selection is disabled while starting/stopping or running.

Settings contains **Start with Windows**, using the existing per-user startup entry. The setting changes only when toggled. Its command still includes --background, which is ignored; startup shows the normal window. Lock restoration is paused while the tab is hidden.

## Soundboard
The WPF Soundboard tab shares the virtualized grid with the other tile pages. Create sounds with a name, optional image and MP3, or record PC/microphone audio from the Create new sound menu, then click a tile to play or replay it. The last clicked tile is selected for deletion. Clicking restarts the selected sound and replaces the previous clip rather than stacking copies. MP3 decoding streams from disk; voice and clips are peak-clamped when mixed. Clip gain starts at 25% and is adjusted live with Volume, leaving the microphone and Windows volume unchanged.

`RecordSoundWindow.cs` owns the recording workflow. Opening it enumerates devices; only Record starts capture. `SoundboardRecorder.cs` streams native WASAPI loopback/microphone samples to a WAV in a dedicated GUID folder under the user's temporary directory. Lifecycle operations run on MTA workers; the UI reads cached timer/peak values. Loopback silence gaps use elapsed-clock placement. Stop finalizes the WAV before trim/export actions become available. `SoundboardRecordingExport.cs` reads only the selected native frames and applies a short edge fade. Preview writes a standard floating-point, 48 kHz stereo WAV and caches it by recording path and trim bounds; direct WaveFileReader playback avoids MP3 codec startup. While playing, Preview and trim are disabled; Stop/EOF restore them. Preview errors are shown in the dialog. Saving separately encodes a 128 kbps MP3 and imports it through SoundboardStore. `SoundboardRecordingPreview.cs` opens a separate local output and never touches call routing. Save, Cancel and window close release streams and delete owned temporary WAV/MP3 files; record-again discards the previous unsaved take. The checks use generated sample packets and injected preview players, without capturing a real microphone or playing audio.

`SoundboardAssetOptimizer.cs` is an opt-in preparation step used by import/edit and recording artwork. It creates disposable scratch copies, resizes images to at most 512 pixels per side while retaining aspect ratio/transparency, and selects 128 kbps MP3 recompression only when it reduces storage. Import/Update then use the existing atomic catalog and rollback path. Original source files are preserved. Recording save optimizes only its image because its newly encoded audio already uses 128 kbps. Default imports preserve original audio/image bytes and retain the 15 MB / 64 megapixel image validation. MP3s have no file-size cap.

`Backend/SoundboardStore.cs` owns imports in `%LOCALAPPDATA%\UtilityTool\Soundboard`. MP3s and artwork are copied into `assets/<sound-id>`; `sounds.xml` stores names and relative filenames. Back up that entire folder to move the library. Deletion removes the library copies only. No library files are committed to Git.

`SoundboardStore.Update` retains the sound ID and stages replacements as generated filenames inside the existing sound folder. It validates new files before the atomic catalog commit, removes old copies after commit, and restores the original catalog/assets if cleanup fails. Legacy `audio.mp3` and `image.<extension>` entries remain supported. Name-only updates do not decode or replace the audio.

Soundboard library loading is independent of audio initialization, with separate busy flags. Metadata tiles appear before background artwork decoding; frozen artwork is cached by immutable asset path, and generation checks reject stale thumbnail results after reload or close. Thumbnail streams allow deletion of old owned assets. The UI avoids reading backend lock-protected state while Windows opens audio devices. The shared tile template adds a hover/focus options button only for items with an OptionsCommand, outside the playback toggle. CreateSoundWindow also handles editing; unchanged asset fields become null replacements.

`WindowsAudio.GetOutputs` isolates default-device, endpoint and property-store failures and skips virtual cable outputs from physical mirroring. SoundPage exposes loading/error/empty states and refresh recovery. A virtual Windows default never causes a physical primary to be silently substituted.

`Backend/SoundboardAudioService.cs` and `SoundboardMixer.cs` handle playback, optional microphone mixing, endpoint failures and disposal. `Frontend/Wpf/Pages/SoundboardPage.cs` and `CreateSoundWindow.cs` handle the grid and import dialog. NAudio.Core/Wasapi 2.2.1 provide MP3 decoding and shared WASAPI streams; the MIT notice is included in ThirdParty.

Local playback uses the saved listening output or the Windows default output. **Play in calls** stays visible below a GridSplitter with no inner scrolling panel. It requires an existing VB-CABLE/Voicemeeter virtual output. Select the physical microphone and virtual playback endpoint here; each call app must use the paired virtual capture endpoint. With VB-CABLE these are **CABLE Input** in UtilityTool and **CABLE Output** in Discord/Meet. The same shared virtual microphone can serve multiple calls. UtilityTool does not install drivers or change Windows' default devices.

**Listen on** can override the Windows default for UtilityTool's local soundboard playback only. **Test output** plays a quiet 400 ms tone exclusively in the local mixer; it never sends that tone to the call mixer. Status names the output used for each sound. A missing virtual device does not prevent local playback. Device selection is disabled during setup/teardown and while call routing is active. Playback commands do not depend on the initially unset template parameter for their enabled state; tile availability is controlled by `Editable`.

`Backend/SoundboardSettingsStore.cs` saves volume, listening output, microphone/virtual-output IDs and call-audio state in `Soundboard/settings.xml` using an atomic XML update. Slider changes are debounced and flushed on close. The main window initializes soundboard audio on Loaded, so a previously active route resumes without visiting the tab. Exact saved microphone and virtual-output IDs must be available; a missing device prevents restoration and shows a status message. Preview never loads or writes these settings or opens audio.

Stopping call audio or closing UtilityTool releases microphone capture and playback. The virtual microphone then carries no UtilityTool audio; switch call apps back to the physical microphone to speak normally. **Stop sounds** retains active microphone routing. **Stop call audio** immediately saves disabled startup intent before asynchronous stream cleanup, including when a saved device is missing. Closing preserves enabled startup intent for next launch.

The local mixer returns actual samples until decoder EOF and then returns zero, so WASAPI drains the final partial buffer and stops/resets. A new click stops/flushes the old playback before adding a fresh reader and starting it again, including near EOF. Playback completion updates the status and stale completion callbacks cannot end a newer replay. The call mixer stays alive for microphone capture while finished clips are disposed. File duration metadata is not used to cut off audio; encoded audio and reverberation in the MP3 are preserved.

## Verification
Run `tests/run-checks.ps1` on Windows after closing the app. Routine verification covers visible tabs, timer typing/bounds, navigation, window controls, shared grid resizing with 1,000 sample tiles, MP3 imports/edits and audio lifecycle/interoperability. Hidden Lock/password/curtain/guard checks are skipped by default; `-IncludeHiddenLock` enables them explicitly for future Lock work. `build.ps1` only builds and does not run tests. Checks generate short test MP3s in the test folder but do not record a microphone, schedule shutdown, arm real apps or broadcast audio. Screenshots and results are written under the chosen output directory (default: %TEMP%/UtilityTool-WpfChecks).

## Return to the saved version
Close UtilityTool, commit or stash outstanding edits, run `git switch main`, then rebuild. The original VS Code tasks/build script return with that branch. ui-redesign remains available. Use the executable from the current branch's build.
