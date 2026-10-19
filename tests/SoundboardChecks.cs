using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UtilityTool.Backend;
using UtilityTool.Frontend.Wpf;

internal static class SoundboardChecks
{
    // Offline comparison: use the actual playback adapter without sending audio
    // to a device. Reused byte buffers exercise WaveBuffer's float-array alias.
    internal static void VerifyWaveform(string path)
    {
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        foreach (int blockSamples in new[] { 960, 3840, 8192 })
        using (var mixer = new SoundboardMixer())
        using (var reference = new SoundboardClip(path))
        {
            mixer.Volume = 0.25f; mixer.Add(new SoundboardClip(path));
            var waveOutput = new SampleToWaveProvider(mixer);
            var bytes = new byte[blockSamples * 4]; var expected = new float[blockSamples]; var scratch = new float[blockSamples];
            int total = 0, read; float error = 0, peak = 0;
            while ((read = waveOutput.Read(bytes, 0, bytes.Length)) > 0)
            {
                int referenceRead = 0, next;
                while (referenceRead < blockSamples && (next = reference.Read(scratch, 0, blockSamples - referenceRead)) > 0)
                { Array.Copy(scratch, 0, expected, referenceRead, next); referenceRead += next; }
                if (read != referenceRead * 4) throw new Exception("Playback and decoder lengths differ.");
                for (int sample = 0; sample < referenceRead; sample++)
                {
                    float actual = BitConverter.ToSingle(bytes, sample * 4);
                    error = Math.Max(error, Math.Abs(actual - expected[sample] * 0.25f)); peak = Math.Max(peak, Math.Abs(actual));
                }
                for (int sample = read; sample < bytes.Length; sample += 4)
                    if (BitConverter.ToSingle(bytes, sample) != 0) throw new Exception("Unused playback buffer contains stale audio.");
                total += referenceRead;
            }
            if (total == 0 || error > 0.00001f || reference.Read(scratch, 0, scratch.Length) != 0 || bytes.Any(value => value != 0))
                throw new Exception("Playback waveform differs from the decoded MP3 or does not end in silence.");
            Console.WriteLine("PASS: Offline MP3 output, block=" + blockSamples + "; samples=" + total + "; duration=" + (total / 96000.0).ToString("0.000000") + "s; peak=" + peak.ToString("0.000000") + "; maximum error=" + error.ToString("G9") + "; EOF buffer silent.");
        }
    }
    internal static void Run(Action<bool, string> check, string output, FluentMainWindow owner, Action pump, Action<Window, string> capture)
    {
        var previous = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try { RunCore(check, output, owner, pump, capture); }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
    }
    private static void RunCore(Action<bool, string> check, string output, FluentMainWindow owner, Action pump, Action<Window, string> capture)
    {
        string root = Path.Combine(output, "soundboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string original = Path.Combine(root, "original.mp3"), artwork = Path.Combine(root, "original.png");
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        var tone = new OffsetSampleProvider(new SignalGenerator(44100, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.08 }) { Take = TimeSpan.FromMilliseconds(200) };
        MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(tone), original, 128000);
        using (var image = new System.Drawing.Bitmap(80, 80))
        { using (var graphics = System.Drawing.Graphics.FromImage(image)) graphics.Clear(System.Drawing.Color.CornflowerBlue); image.Save(artwork, System.Drawing.Imaging.ImageFormat.Png); }
        var store = new SoundboardStore(Path.Combine(root, "library"));
        SoundboardSettingsChecks.Run(check, output);
        SoundboardEditChecks.Run(check, output);
        SoundboardAssetOptimizerChecks.Run(check, output);
        SoundboardAssetImportChecks.Run(check, output);
        SoundboardRecorderChecks.Run(check, output);
        SoundboardRecordingExportChecks.Run(check, output);
        SoundboardRecordingPreviewChecks.Run(check);
        RecordSoundWindowChecks.Run(check, output, owner, pump, capture);
        SoundDeviceChecks.RunBackend(check); SoundDeviceChecks.RunUi(check, pump);
        check(store.Load().Count == 0 && !Directory.Exists(store.Root), "Reading an empty sound library creates no AppData files");
        var sound = store.Import("  Test & sound  ", original, artwork);
        var restored = new SoundboardStore(store.Root).Load().Single();
        check(restored.Name == "Test & sound" && restored.Id == sound.Id && File.Exists(restored.AudioPath) && File.Exists(restored.ImagePath), "MP3 and artwork copies persist with the sound name across library reloads");
        string xml = File.ReadAllText(Path.Combine(store.Root, "sounds.xml"));
        check(!xml.Contains(root) && xml.Contains("audio.mp3") && xml.Contains("image.png"), "Sound metadata uses portable relative filenames");
        using (var clip = new SoundboardClip(restored.AudioPath))
        { var data = new float[4096]; check(clip.WaveFormat.SampleRate == 48000 && clip.WaveFormat.Channels == 2 && clip.Read(data, 0, data.Length) > 0 && data.Any(value => Math.Abs(value) > 0.001), "A real MP3 decodes and resamples to the soundboard mix format"); }
        File.Delete(original); File.Delete(artwork);
        check(File.Exists(sound.AudioPath) && File.Exists(sound.ImagePath), "Imported sounds survive removal of the original MP3 and artwork");
        string invalid = Path.Combine(root, "invalid.mp3"); File.WriteAllText(invalid, "not an MP3");
        bool rejected = false; try { store.Import("Invalid", invalid); } catch { rejected = true; }
        check(rejected && store.Load().Count == 1 && Directory.GetDirectories(Path.Combine(store.Root, "assets")).Length == 1, "Invalid audio import leaves the existing library intact and removes partial copies");
        string catalog = Path.Combine(store.Root, "sounds.xml");
        File.WriteAllText(catalog, xml.Replace("audio.mp3", "../../outside.mp3"));
        rejected = false; try { store.Load(); } catch (InvalidDataException) { rejected = true; }
        check(rejected, "Metadata cannot point audio outside its owned sound folder"); File.WriteAllText(catalog, xml);

        var sessions = new List<FakeSession>();
        var audio = new SoundboardAudioService((mic, virtualOut, listening) => { var session = new FakeSession(mic, virtualOut, listening); sessions.Add(session); return session; });
        using (var page = new SoundboardPage(owner, false, store, audio, TestListeningDevices, callDevices: TestCallDevices))
        {
            // Host the real page in an owned window, without opening audio hardware.
            var fixture = new Window { Owner = owner, Width = 1040, Height = 740, Content = page, Background = owner.Background, Foreground = Brushes.WhiteSmoke };
            fixture.Show(); Wait(page.LoadSounds(), pump); Wait(page.InitializeAsync(), pump); pump();
            var createSound = Visuals<Wpf.Ui.Controls.Button>(page).Single(b => (string)b.Content == "Create new sound");
            check(createSound.ContextMenu != null && createSound.ContextMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header).SequenceEqual(new[] { "Import MP3", "Record audio" }), "Create new sound exposes both MP3 import and audio recording");
            createSound.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); pump();
            check(createSound.ContextMenu.IsOpen && ReferenceEquals(createSound.ContextMenu.PlacementTarget, createSound), "The create button opens its sound-source menu next to the button");
            capture(fixture, "Soundboard-create-options"); createSound.ContextMenu.IsOpen = false;
            var button = Visuals<ToggleButton>(page).Single(toggle => toggle.DataContext is TileItem);
            var tile = (TileItem)button.DataContext;
            WaitFor(() => tile.Icon != null, pump);
            check(tile.Name == sound.Name && tile.Icon != null && tile.Icon.IsFrozen && tile.PlayCommand != null, "Soundboard uses the shared tile grid with cached artwork and click playback");
            check(button.Command != null, "The sound tile binds its playback command (binding=" + (System.Windows.Data.BindingOperations.GetBindingExpression(button, ButtonBase.CommandProperty) == null ? "missing" : System.Windows.Data.BindingOperations.GetBindingExpression(button, ButtonBase.CommandProperty).Status.ToString()) + ")");
            check(ReferenceEquals(button.Command, tile.PlayCommand), "The sound tile uses its own playback command");
            check(ReferenceEquals(button.CommandParameter, tile), "The sound tile passes itself as the playback parameter");
            check(button.IsEnabled, "The sound tile is enabled after command and parameter binding finishes");
            typeof(ToggleButton).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(button, null); WaitFor(() => sessions.Count == 1 && sessions[0].Played == 1, pump);
            typeof(ToggleButton).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(button, null); WaitFor(() => sessions[0].Played == 2, pump);
            check(tile.Selected && sessions[0].Played == 2 && sessions[0].Microphone == null, "Repeated tile clicks replay the selected sound without starting microphone capture");
            var volume = Visuals<Slider>(page).Single();
            check(volume.Value == 25 && sessions[0].Volume == 0.25f, "Soundboard starts at a quieter 25 percent clip volume");
            volume.Value = 40;
            check(sessions[0].Volume == 0.4f && audio.IsPlaying, "Volume changes apply live while a sound is playing");
            sessions[0].Complete(sessions[0].LastPlaybackId - 1); pump();
            check(audio.IsPlaying, "An old completion callback cannot stop a newer replay");
            sessions[0].Complete(sessions[0].LastPlaybackId);
            WaitFor(() => !audio.IsPlaying && Visuals<TextBlock>(page).Any(text => text.Text == "Playback finished."), pump);
            check(!audio.IsPlaying, "MP3 completion clears the playing state and updates the visible status");
            check(Visuals<Wpf.Ui.Controls.Button>(page).Single(b => (string)b.Content == "Delete sound").IsEnabled, "The played sound can be deleted with the button below the grid");
            var test = Visuals<Wpf.Ui.Controls.Button>(page).Single(b => (string)b.Content == "Test output");
            test.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); WaitFor(() => sessions[0].Tests == 1 && test.IsEnabled, pump);
            check(sessions[0].Played == 2 && sessions[0].Microphone == null, "Test output plays a local tone without changing sound selection or starting microphone capture");
            var listeningPicker = Visuals<ComboBox>(page).Single(combo => System.Windows.Automation.AutomationProperties.GetName(combo) == "Listen on"); listeningPicker.SelectedIndex = 1;
            WaitFor(() => sessions[0].Disposed && test.IsEnabled, pump);
            test.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); WaitFor(() => sessions.Count == 2 && sessions[1].Tests == 1 && test.IsEnabled, pump);
            check(sessions[1].Listening == "test-headphones", "Changing Listen on releases the old output and sends the test tone to the selected headphones");
            check(sessions[1].Volume == 0.4f, "The chosen clip volume carries across listening-output changes");
            capture(fixture, "Soundboard-imported");
            var dialog = new CreateSoundWindow(fixture, store); dialog.Show(); pump(); capture(dialog, "Soundboard-create");
            var reduction = Visuals<CheckBox>(dialog).Single();
            check(reduction.IsChecked != true && System.Windows.Automation.AutomationProperties.GetName(reduction) == "Reduce sound file size", "Import exposes optional image/audio reduction and preserves original quality by default");
            check(Visuals<System.Windows.Controls.TextBox>(dialog).Count() == 3 && Visuals<Wpf.Ui.Controls.Button>(dialog).Count(b => (string)b.Content == "Browse…") == 2, "Create dialog has name, MP3 and optional image fields"); dialog.Close();
            fixture.Close();
        }
        check(sessions.All(session => session.Disposed), "Closing the soundboard releases its playback sessions");
        SoundboardPageChecks.Run(check, output, owner, sound, pump, capture);
        var saved = new SoundboardSettingsStore(Path.Combine(store.Root, "settings.xml")).Load();
        check(saved.Volume == 0.4f && saved.ListeningOutputId == "test-headphones", "Closing the page flushes the latest volume and listening-output preferences");
        StartupSettingsChecks(check, root, sound.AudioPath, pump, capture);
        using (var routing = new SoundboardAudioService((mic, virtualOut, listening) => { var session = new FakeSession(mic, virtualOut, listening); sessions.Add(session); return session; }))
        {
            routing.SetListeningOutputAsync("headphones").GetAwaiter().GetResult();
            routing.StartRoutingAsync("physical-mic", "virtual-output").GetAwaiter().GetResult(); var session = sessions.Last();
            check(routing.IsRouting && session.Microphone == "physical-mic" && session.Output == "virtual-output" && session.Listening == "headphones", "Call audio keeps the separately selected listening output");
            routing.TestLocalOutputAsync().GetAwaiter().GetResult(); check(session.Tests == 1 && session.Played == 0, "Test output remains local while call routing is active");
            rejected = false; try { routing.SetListeningOutputAsync("other-output").GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && routing.IsRouting && !session.Disposed, "Changing outputs while call audio is active is blocked without stopping the microphone");
            routing.PlayAsync(sound.AudioPath).GetAwaiter().GetResult(); routing.StopClipsAsync().GetAwaiter().GetResult();
            check(session.Stops == 1 && !session.Disposed && routing.IsRouting, "Stop sounds retains active microphone routing");
            routing.StopRoutingAsync().GetAwaiter().GetResult();
            check(session.Disposed && !routing.IsRouting, "Stop call audio releases the microphone and virtual output");
            routing.StartRoutingAsync("physical-mic", "virtual-output").GetAwaiter().GetResult();
        }
        check(sessions.Last().Disposed, "Disposing UtilityTool releases an active call-audio session");
        using (var failed = new SoundboardAudioService((mic, virtualOut, listening) => { throw new InvalidOperationException("Device unavailable"); }))
        { rejected = false; try { failed.StartRoutingAsync("mic", "out").GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; } check(rejected && !failed.IsRouting, "Failed call setup rolls back to stopped state"); }
        using (var failing = new SoundboardAudioService((mic, virtualOut, listening) => { var session = new FakeSession(mic, virtualOut, listening); sessions.Add(session); return session; }))
        {
            failing.StartRoutingAsync("mic", "out").GetAwaiter().GetResult(); var current = sessions.Last(); current.Fail();
            WaitFor(() => current.Disposed, pump); check(!failing.IsRouting, "A device failure disposes routing and returns to stopped state");
        }
        using (var mixer = new SoundboardMixer(new ConstantSamples(0.5f)))
        {
            mixer.Volume = 1; var clip = new ConstantSamples(0.8f); mixer.Add(clip);
            var buffer = new float[16]; mixer.Read(buffer, 3, 8);
            check(buffer.Skip(3).Take(8).All(value => value == 1) && buffer.Take(3).All(value => value == 0), "Voice and sounds mix with peak clamping and respect the buffer offset");
            mixer.StopClips(); mixer.Read(buffer, 0, 16);
            check(clip.Disposed && buffer.All(value => value == 0.5f), "Stopping clips disposes them while keeping microphone samples");
        }
        using (var mixer = new SoundboardMixer())
        {
            mixer.Volume = 1; var finite = new FiniteSamples(14, 4, 0.4f); mixer.Add(finite);
            var buffer = Enumerable.Repeat(0.9f, 20).ToArray();
            check(mixer.Read(buffer, 3, 12) == 12 && buffer.Skip(3).Take(12).All(value => value == 0.4f), "Short decoder chunks are combined without truncating the MP3");
            check(mixer.Read(buffer, 3, 12) == 2 && buffer.Skip(3).Take(2).All(value => value == 0.4f) && buffer.Skip(5).Take(10).All(value => value == 0), "The partial final block preserves its samples and clears unused output");
            check(finite.Disposed && mixer.Read(buffer, 3, 12) == 0 && buffer.Skip(3).Take(12).All(value => value == 0), "At EOF the source is disposed and the local mixer returns zero with no repeated tail");
            mixer.Add(new FiniteSamples(6, 6, 0.2f));
            check(mixer.Read(buffer, 3, 12) == 6, "A fresh replay can start after the previous source reached EOF");
        }
        using (var mixer = new SoundboardMixer(new ConstantSamples(0.2f)))
        {
            mixer.Volume = 0.25f; mixer.Add(new FiniteSamples(4, 4, 0.8f)); var buffer = new float[12];
            check(mixer.Read(buffer, 0, 12) == 12 && buffer.Take(4).All(value => Math.Abs(value - 0.4f) < 0.0001f) && buffer.Skip(4).All(value => value == 0.2f), "Only the clip is attenuated and call microphone audio continues after its EOF");
        }
        using (var mixer = new SoundboardMixer())
        {
            mixer.Volume = 0.25f; mixer.Add(new ConstantSamples(0.1f));
            var waveOutput = new SampleToWaveProvider(mixer); var bytes = new byte[3840]; bool stable = true;
            for (int block = 0; block < 100; block++)
            {
                stable &= waveOutput.Read(bytes, 0, bytes.Length) == bytes.Length;
                for (int sample = 0; sample < bytes.Length; sample += 4) stable &= Math.Abs(BitConverter.ToSingle(bytes, sample) - 0.025f) < 0.000001f;
            }
            check(stable, "Real output byte buffers stay at the intended volume across 100 reuse cycles without accumulating old audio");
        }
        using (var mixer = new SoundboardMixer())
        {
            mixer.Volume = 1; mixer.Add(new FiniteSamples(10, 4, 0.3f));
            var waveOutput = new SampleToWaveProvider(mixer); var bytes = new byte[256];
            for (int sample = 0; sample < bytes.Length; sample += 4) Buffer.BlockCopy(BitConverter.GetBytes(0.9f), 0, bytes, sample, 4);
            int read = waveOutput.Read(bytes, 16, 160); bool cleared = read == 40;
            for (int sample = 16; sample < 16 + 160; sample += 4) cleared &= Math.Abs(BitConverter.ToSingle(bytes, sample) - (sample < 56 ? 0.3f : 0f)) < 0.000001f;
            cleared &= BitConverter.ToSingle(bytes, 12) == 0.9f && BitConverter.ToSingle(bytes, 176) == 0.9f;
            check(cleared, "Partial EOF clears the entire wrapped output region and preserves samples outside its byte offset");
            read = waveOutput.Read(bytes, 16, 160);
            check(read == 0 && Enumerable.Range(0, 40).All(i => BitConverter.ToSingle(bytes, 16 + i * 4) == 0), "EOF leaves the reused byte-backed output completely silent");
        }
        using (var mixer = new SoundboardMixer(new ConstantSamples(0.2f)))
        {
            mixer.Volume = 0.25f; mixer.Add(new FiniteSamples(4, 4, 0.8f));
            var waveOutput = new SampleToWaveProvider(mixer); var bytes = new byte[64];
            int read = waveOutput.Read(bytes, 0, bytes.Length);
            check(read == bytes.Length && Enumerable.Range(0, 16).All(i => Math.Abs(BitConverter.ToSingle(bytes, i * 4) - (i < 4 ? 0.4f : 0.2f)) < 0.00001f), "Microphone and clips mix correctly into the real byte-backed output without array-type errors");
            read = waveOutput.Read(bytes, 0, bytes.Length);
            check(read == bytes.Length && Enumerable.Range(0, 16).All(i => BitConverter.ToSingle(bytes, i * 4) == 0.2f), "The wrapped call output keeps only microphone audio after the sound ends");
        }
        using (var mixer = new SoundboardMixer())
        {
            mixer.Volume = 1; mixer.Add(new DestructiveEofSamples()); var buffer = new float[12];
            check(mixer.Read(buffer, 0, 12) == 4 && buffer.Take(4).All(value => value == 0.3f) && buffer.Skip(4).All(value => value == 0), "A resampler EOF flush cannot corrupt final samples already copied into the output");
        }
        using (var mixer = new SoundboardMixer())
        using (var reference = new SoundboardClip(sound.AudioPath))
        {
            mixer.Volume = 0.25f; mixer.Add(new SoundboardClip(sound.AudioPath));
            var actual = new float[8192]; var expected = new float[8192]; bool same = true; int samples = 0, read;
            while ((read = reference.Read(expected, 0, expected.Length)) > 0)
            {
                int mixed = mixer.Read(actual, 0, actual.Length); same &= mixed == read;
                for (int i = 0; i < read; i++) same &= Math.Abs(actual[i] - expected[i] * 0.25f) < 0.00001f;
                same &= actual.Skip(read).All(value => value == 0); samples += read;
            }
            check(same && samples > 0 && mixer.Read(actual, 0, actual.Length) == 0, "Real MP3 mixing preserves the final resampled block and reaches exact decoder EOF");
        }
        using (var mixer = new SoundboardMixer())
        using (var reference = new SoundboardClip(sound.AudioPath))
        {
            mixer.Volume = 0.25f; mixer.Add(new SoundboardClip(sound.AudioPath));
            var waveOutput = new SampleToWaveProvider(mixer); var bytes = new byte[3840]; var expected = new float[960]; bool same = true; int read;
            while ((read = waveOutput.Read(bytes, 0, bytes.Length)) > 0)
            {
                int referenceRead = reference.Read(expected, 0, expected.Length); same &= read == referenceRead * 4;
                for (int sample = 0; sample < read / 4; sample++) same &= Math.Abs(BitConverter.ToSingle(bytes, sample * 4) - expected[sample] * 0.25f) < 0.00001f;
                for (int sample = read; sample < bytes.Length; sample += 4) same &= BitConverter.ToSingle(bytes, sample) == 0;
            }
            check(same && reference.Read(expected, 0, expected.Length) == 0, "MP3 output matches the independent decoded waveform through reused playback byte buffers until EOF");
        }
        int eof = 0;
        using (var clip = new SoundboardClip(sound.AudioPath, () => eof++))
        {
            var data = new float[4096]; clip.Read(data, 0, 0); check(eof == 0, "An empty read is not treated as MP3 completion");
            while (clip.Read(data, 0, data.Length) > 0) { }
            clip.Read(data, 0, data.Length); check(eof == 1, "Call-only clips notify completion once at EOF without stopping the microphone stream");
        }
        using (var clip = new SoundboardClip(sound.AudioPath, () => eof++)) { }
        check(eof == 1, "Cancelling or disposing a clip does not send a natural-completion notification");
        using (var left = new SoundboardClip(sound.AudioPath))
        using (var right = new SoundboardClip(sound.AudioPath))
        {
            var first = new float[2048]; var second = new float[2048]; left.Read(first, 0, first.Length); right.Read(second, 0, second.Length);
            check(first.SequenceEqual(second), "Local and call outputs read independent MP3 cursors from the start");
        }
        check(SoundboardDevices.IsVirtualOutput("CABLE Input (VB-Audio Virtual Cable)") && !SoundboardDevices.IsVirtualOutput("Speakers (Realtek Audio)"), "Call routing rejects physical speaker endpoints");
        var toneSamples = new float[48000]; var testTone = new SoundboardTestTone(); int toneLength = testTone.Read(toneSamples, 0, toneSamples.Length);
        check(toneLength == 38400 && testTone.Read(toneSamples, 0, 100) == 0 && toneSamples.All(value => Math.Abs(value) <= 0.101f), "The local output test is a finite, quiet tone with smooth edges");
        string preserved = Path.Combine(root, "preserved.mp3"); File.Copy(sound.AudioPath, preserved);
        store.Delete(sound.Id); check(store.Load().Count == 0 && !File.Exists(sound.AudioPath) && !File.Exists(sound.ImagePath) && File.Exists(preserved), "Deleting a sound removes owned copies and preserves files outside the library");
        owner.SelectPage("Soundboard"); pump();
        check(Visuals<Wpf.Ui.Controls.Button>(owner.Soundboard).Where(b => (string)b.Content == "Create new sound" || (string)b.Content == "Delete sound" || (string)b.Content == "Start call audio").All(b => !b.IsEnabled), "Sample preview cannot import sounds, delete sounds or start microphone routing");
        var callSettings = Visuals<Border>(owner.Soundboard).Single(card => System.Windows.Automation.AutomationProperties.GetName(card) == "Play in calls"); pump();
        capture(owner, "Soundboard-call-setup");
        check(owner.Soundboard.Tiles.ActualHeight >= 156, "Visible call setup keeps a complete sound tile row at minimum window size");
        check(!LogicalElements<ScrollViewer>(callSettings).Any() && Visuals<GridSplitter>(owner.Soundboard).Count() == 1, "Call setup has a draggable divider and no inner scrollbar hiding its buttons");
        foreach (var button in Visuals<Wpf.Ui.Controls.Button>(callSettings))
        {
            var point = button.TranslatePoint(new Point(0, 0), owner.Soundboard);
            check(button.ActualHeight > 0 && point.Y >= 0 && point.Y + button.ActualHeight <= owner.Soundboard.ActualHeight, "Call button '" + button.Content + "' is fully visible at minimum window size");
        }
        foreach (var combo in Visuals<ComboBox>(callSettings))
        { var point = combo.TranslatePoint(new Point(0, 0), callSettings); check(point.Y >= 0 && point.Y + combo.ActualHeight <= callSettings.ActualHeight, "Call device picker '" + System.Windows.Automation.AutomationProperties.GetName(combo) + "' is fully visible"); }
        owner.Height = 900; pump(); double originalHeight = callSettings.ActualHeight;
        var divider = Visuals<GridSplitter>(owner.Soundboard).Single();
        divider.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        divider.RaiseEvent(new DragDeltaEventArgs(0, -80) { RoutedEvent = Thumb.DragDeltaEvent });
        divider.RaiseEvent(new DragCompletedEventArgs(0, -80, false) { RoutedEvent = Thumb.DragCompletedEvent }); pump();
        check(callSettings.ActualHeight > originalHeight, "Dragging the divider gives more space to the call section");
        owner.Height = owner.MinHeight; pump();
        var bottom = callSettings.TranslatePoint(new Point(0, callSettings.ActualHeight), owner.Soundboard);
        check(bottom.Y <= owner.Soundboard.ActualHeight + 1 && owner.Soundboard.Tiles.ActualHeight >= 156, "Resizing back to minimum after moving the divider keeps both sections visible");
    }
    private static List<SoundboardDevice> TestListeningDevices()
    { return new List<SoundboardDevice> { new SoundboardDevice(null, "Windows default — Test speakers"), new SoundboardDevice("test-headphones", "Test headphones") }; }
    private static SoundboardDevices TestCallDevices()
    {
        var devices = new SoundboardDevices();
        devices.Microphones.Add(new SoundboardDevice("test-mic", "Test microphone")); devices.Microphones.Add(new SoundboardDevice("saved-mic", "Saved microphone " + new string('M', 120)));
        devices.VirtualOutputs.Add(new SoundboardDevice("test-cable", "CABLE Input (Test)")); devices.VirtualOutputs.Add(new SoundboardDevice("saved-cable", "CABLE Input (Saved) " + new string('C', 120))); return devices;
    }
    private static void StartupSettingsChecks(Action<bool, string> check, string root, string soundPath, Action pump, Action<Window, string> capture)
    {
        var library = new SoundboardStore(Path.Combine(root, "startup-library"));
        var preferences = new SoundboardSettingsStore(Path.Combine(library.Root, "settings.xml"));
        preferences.Save(new SoundboardSettings { Volume = 0.37f, ListeningOutputId = "test-headphones", MicrophoneId = "saved-mic", VirtualOutputId = "saved-cable", CallAudioEnabled = true });
        var sessions = new List<FakeSession>();
        var audio = new SoundboardAudioService((mic, cable, listening) => { var session = new FakeSession(mic, cable, listening); sessions.Add(session); return session; });
        var startup = new FluentMainWindow(false, (window, preview) => new SoundboardPage(window, preview, library, audio, TestListeningDevices, preferences, TestCallDevices), (window, preview) => new LockPage(window, true));
        startup.Show(); Wait(startup.Soundboard.InitializeAsync(), pump);
        check(startup.CurrentPage == "Home" && audio.IsRouting && sessions.Count == 1, "Saved active call audio resumes on application startup before opening the Soundboard tab");
        check(sessions[0].Microphone == "saved-mic" && sessions[0].Output == "saved-cable" && sessions[0].Listening == "test-headphones" && sessions[0].Volume == 0.37f, "Startup restores the exact selected devices and sound volume before starting call audio");
        Wait(startup.Soundboard.InitializeAsync(), pump); Wait(startup.Soundboard.LoadSounds(), pump);
        check(sessions.Count == 1 && !sessions[0].Disposed && audio.IsRouting, "Reopening the tab and repeating initialization do not replace or restart the active route");
        startup.SelectPage("Soundboard"); pump(); capture(startup, "Soundboard-restored-settings");
        foreach (var button in Visuals<Wpf.Ui.Controls.Button>(startup.Soundboard).Where(button => (string)button.Content == "Start call audio" || (string)button.Content == "Stop call audio" || (string)button.Content == "Refresh devices"))
        { var point = button.TranslatePoint(new Point(0, 0), startup.Soundboard); check(point.Y + button.ActualHeight <= startup.Soundboard.ActualHeight, "Long saved device names keep call button '" + button.Content + "' visible"); }
        Visuals<Slider>(startup.Soundboard).Single().Value = 12; startup.Close(); pump();
        var restored = preferences.Load();
        check(sessions[0].Disposed && restored.CallAudioEnabled && restored.Volume == 0.12f, "Closing the application releases audio while preserving active call state and the latest volume for next startup");

        audio = new SoundboardAudioService((mic, cable, listening) => { var session = new FakeSession(mic, cable, listening); sessions.Add(session); return session; });
        using (var page = new SoundboardPage(startup, false, library, audio, TestListeningDevices, preferences, TestCallDevices))
        {
            Wait(page.InitializeAsync(), pump); check(audio.IsRouting && sessions.Last().Volume == 0.12f, "The next application instance restores the previously active route and updated volume");
            var stopping = sessions.Last(); stopping.DisposeStarted = new System.Threading.ManualResetEventSlim(); stopping.AllowDispose = new System.Threading.ManualResetEventSlim();
            LogicalElements<Wpf.Ui.Controls.Button>(page).Single(button => (string)button.Content == "Stop call audio").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            WaitFor(() => stopping.DisposeStarted.IsSet, pump);
            check(!preferences.Load().CallAudioEnabled, "Stop saves disabled startup intent before asynchronous stream cleanup finishes"); stopping.AllowDispose.Set();
            WaitFor(() => !audio.IsRouting && !preferences.Load().CallAudioEnabled, pump);
            check(!preferences.Load().CallAudioEnabled, "Explicit Stop call audio saves the disabled state for future startup");
        }
        int calls = sessions.Count;
        audio = new SoundboardAudioService((mic, cable, listening) => { var session = new FakeSession(mic, cable, listening); sessions.Add(session); return session; });
        using (var page = new SoundboardPage(startup, false, library, audio, TestListeningDevices, preferences, TestCallDevices))
        {
            Wait(page.InitializeAsync(), pump); check(!audio.IsRouting && sessions.Count == calls, "Saved inactive call audio stays off without opening a playback or microphone session");
            Wait(audio.PlayAsync(soundPath), pump); check(sessions.Last().Microphone == null && sessions.Last().Volume == 0.12f, "Saved volume also applies to ordinary local-only playback");
        }
        preferences.Save(new SoundboardSettings { Volume = 0.22f, MicrophoneId = "disconnected-mic", VirtualOutputId = "saved-cable", CallAudioEnabled = true }); calls = sessions.Count;
        audio = new SoundboardAudioService((mic, cable, listening) => { var session = new FakeSession(mic, cable, listening); sessions.Add(session); return session; });
        using (var page = new SoundboardPage(startup, false, library, audio, TestListeningDevices, preferences, TestCallDevices))
        {
            Wait(page.InitializeAsync(), pump);
            check(sessions.Count == calls && !audio.IsRouting, "A missing saved microphone blocks auto-start instead of choosing another mic");
            check(LogicalElements<TextBlock>(page).Any(text => text.Text.Contains("Saved call audio could not start")), "A missing saved device shows an actionable status");
            check(preferences.Load().MicrophoneId == "disconnected-mic" && preferences.Load().CallAudioEnabled, "Unavailable saved devices and resume preference are preserved for reconnection");
            var stop = LogicalElements<Wpf.Ui.Controls.Button>(page).Single(button => (string)button.Content == "Stop call audio");
            check(stop.IsEnabled, "Saved auto-start can be stopped even while a device is disconnected");
            stop.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); WaitFor(() => !preferences.Load().CallAudioEnabled, pump);
        }
        calls = sessions.Count;
        using (var page = new SoundboardPage(startup, true, library, new SoundboardAudioService((mic, cable, listening) => { throw new Exception("Preview opened audio"); }), () => { throw new Exception("Preview read devices"); }, preferences, () => { throw new Exception("Preview read microphones"); }))
        {
            Wait(page.InitializeAsync(), pump); Visuals<Slider>(page).Single().Value = 99;
            check(sessions.Count == calls && preferences.Load().Volume == 0.22f, "Sample preview neither restores call audio nor changes saved preferences");
        }
    }
    private static void Wait(Task task, Action pump) { WaitFor(() => task.IsCompleted, pump); task.GetAwaiter().GetResult(); }
    private static void WaitFor(Func<bool> done, Action pump)
    { var timer = System.Diagnostics.Stopwatch.StartNew(); while (!done() && timer.ElapsedMilliseconds < 10000) { pump(); System.Threading.Thread.Sleep(5); } if (!done()) throw new TimeoutException("Soundboard check timed out."); }
    private static IEnumerable<T> Visuals<T>(DependencyObject root) where T : DependencyObject
    { if (root is T) yield return (T)root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static IEnumerable<T> LogicalElements<T>(DependencyObject root) where T : DependencyObject
    { if (root is T) yield return (T)root; foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var item in LogicalElements<T>(child)) yield return item; }
    private sealed class FakeSession : ISoundboardSession
    {
        internal readonly string Microphone, Output, Listening;
        internal int Played, Stops, Tests;
        internal volatile bool Disposed;
        internal System.Threading.ManualResetEventSlim DisposeStarted, AllowDispose;
        internal FakeSession(string mic, string output, string listening) { Microphone = mic; Output = output; Listening = listening; }
        public string LocalOutputName { get { return Listening ?? "Test speakers"; } }
        public float LocalPeak { get { return 0; } }
        internal int LastPlaybackId;
        internal float Volume;
        public void SetVolume(float value) { Volume = value; }
        public void Play(string path, int playbackId) { if (!File.Exists(path)) throw new FileNotFoundException(); LastPlaybackId = playbackId; System.Threading.Interlocked.Increment(ref Played); }
        public void TestLocalOutput(int playbackId) { LastPlaybackId = playbackId; System.Threading.Interlocked.Increment(ref Tests); }
        public void StopClips() { Stops++; }
        public void Dispose() { if (DisposeStarted != null) { DisposeStarted.Set(); if (!AllowDispose.Wait(10000)) throw new TimeoutException("Fixture disposal was not released"); } Disposed = true; }
        public event Action<string> Failed;
        public event Action<int> PlaybackFinished;
        internal void Complete(int id) { var handler = PlaybackFinished; if (handler != null) handler(id); }
        internal void Fail() { if (Failed != null) Failed("Fixture endpoint disconnected"); }
    }
    private sealed class ConstantSamples : ISampleProvider, IDisposable
    {
        private readonly float value;
        internal bool Disposed;
        internal ConstantSamples(float value) { this.value = value; }
        public WaveFormat WaveFormat { get { return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); } }
        public int Read(float[] buffer, int offset, int count) { for (int i = 0; i < count; i++) buffer[offset + i] = value; return count; }
        public void Dispose() { Disposed = true; }
    }
    private sealed class FiniteSamples : ISampleProvider, IDisposable
    {
        private int remaining;
        private readonly int chunk;
        private readonly float value;
        internal bool Disposed;
        internal FiniteSamples(int count, int chunk, float value) { remaining = count; this.chunk = chunk; this.value = value; }
        public WaveFormat WaveFormat { get { return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); } }
        public int Read(float[] buffer, int offset, int count) { int read = Math.Min(remaining, Math.Min(chunk, count)); for (int i = 0; i < read; i++) buffer[offset + i] = value; remaining -= read; return read; }
        public void Dispose() { Disposed = true; }
    }
    private sealed class DestructiveEofSamples : ISampleProvider
    {
        private bool first = true;
        public WaveFormat WaveFormat { get { return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); } }
        public int Read(float[] buffer, int offset, int count)
        {
            if (offset != 0) throw new Exception("Resampler scratch offset must be zero.");
            if (first) { first = false; for (int i = 0; i < 4; i++) buffer[i] = 0.3f; return 4; }
            // Some resampler flushes reuse scratch even when returning zero.
            for (int i = 0; i < buffer.Length; i++) buffer[i] = -0.9f; return 0;
        }
    }
}
