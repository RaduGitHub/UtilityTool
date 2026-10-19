# DISCLAIMER
This is a project that I made for my usage primarily with things that I felt Windows did not have (Shutdown Timer, Soundboard, Multiple Sound Output).
Every security risk and whatnot is not checked, this will most likely not be updated by me anytime soon. There is some hidden functionality of having it run in background but I never finished it as this was good enough, everything else is written below.

# UtilityTool
Everyday Windows utilities in one application.

- Shutdown timers with saved times.
- Password protection for selected apps.
- Playback through multiple sound outputs.
- A soundboard with MP3 imports, audio recording and custom tile images.

The refreshed Lock tab has **Running programs** and **Locked programs** views. Existing protected apps, monitoring state and UtilityTool password are reused. Choose **Lock** beside a running program, then **Turn on monitoring** to apply its protection. **Remove lock**, **Turn off monitoring** and closing UtilityTool while monitoring is active require the UtilityTool password.

The `ui-redesign` branch tries a native WPF frontend with [WPF UI](https://github.com/lepoco/wpfui) 4.3.0. The original WinForms app is preserved on `main`.

Locked apps use an opaque Privacy curtain matching UtilityTool's dark theme, with a centered password prompt. Opening or returning to a protected app requests keyboard focus for its password field; a background launch leaves the app you are using focused. Enter submits the password. Choose **Time allowed for this unlock**: until the app exits, or 1, 5, 15, 30 or 60 minutes. **Lock now** revokes the selected program's current access; **Lock all now** revokes access for every protected program. The curtain hides the app's contents without taking screenshots or processing a blur.

A wrong password leaves the app locked and allows another try. **Keep locked** (or Escape) hides the prompt while keeping the curtain; click the curtain to return to the password field. The curtain's title bar still lets you move, minimize, maximize or close the protected app.

In Lock, search by program name, window title or executable. **Running programs** lists current windows; **Locked programs** retains protected programs even while they are closed. **Pick a window…** gives you three seconds to switch to the program to protect, and **Add app…** accepts a nonrunning app's `.exe` without launching it. Programs can be added or removed while monitoring; unrelated locks stay active. Unsupported windows show an explanation.

Each protected row has **Password…**. Verify the UtilityTool password, then choose a separate app password or **Use UtilityTool password**. Changing an app's password locks its current sessions again. The main password remains at `%LOCALAPPDATA%\UtilityTool\password.dat`; app overrides are stored under `%LOCALAPPDATA%\UtilityTool\AppPasswords` with hashed filenames. Both store salted PBKDF2 hashes protected with Windows DPAPI, rather than plaintext passwords. This desktop guard requires UtilityTool to keep running and does not provide Windows account isolation.

Shutdown has a **Saved times** list on the left and the timer on the right. **Add time** creates a named preset without changing the timer. Select a preset, then use **Modify selected** to change its name and duration or **Delete selected** to remove it. Double-click a preset (or press Enter with the list focused) to fill the timer; **Schedule shutdown** starts it. The panels resize with the window. Existing saved durations are retained, and the list is saved in `%LOCALAPPDATA%\UtilityTool\shutdown-presets.xml`, including when you remove every entry.

While locks are armed, Windows window events trigger protection and process-exit notifications release curtains. Backup scans run every three seconds. This reduces launch/reopen delay and background polling; it cannot guarantee that the first frame of an app launched outside UtilityTool is hidden.

Open `UtilityTool.sln` in Visual Studio with the .NET desktop development workload and .NET Framework 4.8 targeting pack. Build, then press F5. In VS Code, open this folder, press Ctrl+Shift+B, then run the **Run UtilityTool** task. A .NET SDK (8 or later) or the portable SDK in `.tools` is needed for the VS Code build.

The executable and dependencies are in `bin/Debug/net48`. Distribute that whole folder, rather than just the EXE. `--preview` displays sample sounds and devices without modifying system settings; `--legacy` opens the previous WinForms frontend. Soundboard is available in the WPF frontend.

For your full installed-app list and working controls, choose **Terminal → Run Task → Run UtilityTool**, rather than **Preview WPF UI (sample data)**. In Sound, Windows' default output is automatically shown as the selected primary tile; click other output tiles to add them as secondary devices. UtilityTool does not change Windows' default output. Refresh devices after changing it in Windows; it is also checked again before starting mirroring.

See [STARTER.md](STARTER.md) for behavior, source layout and returning to the saved version.

In **Soundboard**, open **Create new sound → Import MP3** to choose a name, MP3 and optional image. Click a tile to play or replay it; **Delete sound** removes the selected sound from the library. **Stop sounds** stops clips without stopping an active call microphone.

Use **Create new sound → Record audio** to capture **PC audio** from selected speakers/headphones, or a **Microphone**. Recording starts only when you press **Record**; a timer and input meter show progress. Press **Stop recording**, adjust the start/end sliders, and **Preview** the selected part through your local listening output. Preview plays a cached temporary WAV directly, without saving to the library or encoding an MP3. Stop preview before replaying or changing the trim; quick repeated clicks cannot restart its initial silence. Preview is never sent to calls. Choose a name and optional image, then **Save sound** to add a normal MP3 tile. **Record again** replaces the unsaved take. Saving or cancelling removes the temporary recording and preview files. PC audio captures everything playing through the selected output, including other applications.

In the import/edit dialog, **Reduce file size** optionally resizes artwork to fit within 512 × 512 pixels and compresses higher-bitrate MP3s to 128 kbps when doing so saves space. It affects new or replacement library copies; your original files are preserved. MP3 recompression is lossy, so leave the option off to retain the original audio bytes. Recordings already use 128 kbps; their **Reduce image size** option reduces only artwork. Without reduction, images are limited to 15 MB and 64 megapixels; MP3s have no fixed file-size limit.

Hover a sound and click its bottom-right **⋮** button to edit the name, image or MP3. Saving retains the same sound entry and removes replaced library copies after the new files have validated. Cancel leaves the sound unchanged. Source files selected from elsewhere on your PC are preserved.

The sound grid loads independently of audio-device startup. A **Loading sounds…** indicator appears while reading the library; tiles show immediately afterward, and artwork loads in the background with a cache for unchanged images.

Choose your speakers/headphones under **Listen on** and press **Test output** to hear a short beep. Playback status names the actual output. Local playback works without a virtual device. This selection affects UtilityTool only; Windows' default output remains unchanged. The test tone is local only, including while call routing is active. Stop call audio before changing the listening output.

Soundboard volume starts at **25%** and can be adjusted with the **Volume** slider. Your chosen volume and listening output are saved for next launch. Volume affects clips only, including their call output; Windows' volume and the microphone's level are unchanged. Clicking a tile restarts playback rather than layering copies. Local output stops once the MP3's final samples have played, and the status changes to **Playback finished**. Audio/reverb already present in a file is preserved.

Imported MP3s and artwork are copied to `%LOCALAPPDATA%\UtilityTool\Soundboard\assets\<sound-id>`. `sounds.xml` stores names and relative filenames. Originals are never deleted. Back up the whole `Soundboard` folder to preserve or move your library; it is not part of the Git repository.

Local playback needs no external audio application. **Play in calls** stays visible below a draggable divider; drag it to resize the grid and call section. It mixes your microphone and sounds into an already-installed VB-CABLE or Voicemeeter playback endpoint. For VB-CABLE, select **CABLE Input** in UtilityTool and **CABLE Output** as the microphone in both Discord and Meet. Press **Start call audio** once to save the selected devices and enable restoration on application startup, including while the Home tab is open. **Stop call audio** disables restoration. Closing UtilityTool releases audio while preserving the saved active state for next launch; switch call apps to your usual microphone while UtilityTool is closed. Missing saved devices leave call audio stopped with a status message. Preferences are stored in `%LOCALAPPDATA%\UtilityTool\Soundboard\settings.xml`. No driver is installed automatically, and Windows' default devices are not changed. Virtual-device setup: [VB-CABLE](https://vb-audio.com/Cable/).

**Sound** lists physical speakers, headphones and monitor outputs for mirroring. Virtual cable devices remain in Soundboard's call controls. Device-read failures leave other healthy outputs visible; **Refresh devices** retries a failed load while preserving available secondary selections. If Windows' default output is virtual, choose a physical default in Windows to enable mirroring.
