using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using UtilityTool.Backend;

internal static class SoundboardRecordingPreviewChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var players = new List<FakePlayer>();
        bool worker = false;
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            using (var preview = new SoundboardRecordingPreview((path, output, volume) =>
            {
                worker = Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA && SynchronizationContext.Current == null;
                var player = new FakePlayer(path, output, volume); players.Add(player); return player;
            }))
            {
                check(!preview.IsPlaying && players.Count == 0, "Idle recording preview opens no audio output");
                preview.PlayAsync("trimmed.mp3", "headphones", 0.25f).GetAwaiter().GetResult();
                var first = players[0];
                check(worker && preview.IsPlaying && first.Path == "trimmed.mp3" && first.Output == "headphones" && first.Volume == 0.25f,
                    "Recording preview opens the selected local output at the requested volume on an MTA worker");
                first.EmitStopped(); preview.StopAsync().GetAwaiter().GetResult();
                check(!preview.IsPlaying && first.Disposed, "MP3 EOF closes recording preview and releases its player");

                preview.PlayAsync("trimmed.mp3", null, 0.4f).GetAwaiter().GetResult();
                var ending = players[1]; ending.DelayStop = true;
                var stale = ending.SnapshotStopped();
                var replay = preview.PlayAsync("trimmed.mp3", "speakers", 0.2f);
                if (!ending.StopRequested.Wait(3000)) throw new TimeoutException("Preview replay did not request stop.");
                check(!replay.IsCompleted && players.Count == 2, "Rapid replay waits for the old output's final stop callback");
                ending.EmitStopped(); replay.GetAwaiter().GetResult();
                var next = players[2]; stale(null);
                check(ending.Disposed && next.Plays == 1 && preview.IsPlaying && !next.Disposed,
                    "Replay starts once after cleanup and stale EOF cannot close the replacement preview");
                preview.StopAsync().GetAwaiter().GetResult();
                check(next.Disposed && !preview.IsPlaying, "Stop preview closes the current local output");
            }
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

        using (var entered = new ManualResetEventSlim())
        using (var resume = new ManualResetEventSlim())
        {
            FakePlayer queued = null;
            using (var preview = new SoundboardRecordingPreview((path, output, volume) =>
            {
                entered.Set(); if (!resume.Wait(3000)) throw new TimeoutException("Preview factory was not released.");
                return queued = new FakePlayer(path, output, volume);
            }))
            {
                var play = preview.PlayAsync("trimmed.mp3", null, 0.25f);
                if (!entered.Wait(3000)) throw new TimeoutException("Preview factory did not start.");
                var stop = preview.StopAsync(); resume.Set();
                play.GetAwaiter().GetResult(); stop.GetAwaiter().GetResult();
                check(queued.Plays == 1 && queued.Disposed && !preview.IsPlaying,
                    "Cancel after a queued preview waits for cleanup and cannot leave playback active");
            }
        }

        FakePlayer failed = null, recovered = null; int starts = 0;
        using (var preview = new SoundboardRecordingPreview((path, output, volume) =>
        {
            var player = new FakePlayer(path, output, volume) { FailPlay = starts++ == 0 };
            if (player.FailPlay) failed = player; else recovered = player; return player;
        }))
        {
            bool rejected = false;
            try { preview.PlayAsync("trimmed.mp3", null, 0.25f).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && failed.Disposed && !preview.IsPlaying, "Failed preview startup releases its player without waiting for an absent EOF callback");
            preview.PlayAsync("trimmed.mp3", null, 0.25f).GetAwaiter().GetResult();
            check(preview.IsPlaying && recovered.Plays == 1, "A failed preview operation does not block later playback");
            recovered.FailStop = true; rejected = false;
            try { preview.StopAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && recovered.Disposed && !preview.IsPlaying, "Stop failure still releases the preview player and resets playback state");
        }

        FakePlayer closing = null; int created = 0;
        var disposedPreview = new SoundboardRecordingPreview((path, output, volume) => { created++; return closing = new FakePlayer(path, output, volume); });
        disposedPreview.PlayAsync("trimmed.mp3", "headphones", 0.25f).GetAwaiter().GetResult();
        var late = closing.SnapshotStopped(); disposedPreview.Dispose(); disposedPreview.Dispose(); late(null);
        disposedPreview.StopAsync().GetAwaiter().GetResult();
        bool disposedRejected = false;
        try { disposedPreview.PlayAsync("trimmed.mp3", null, 0.25f).GetAwaiter().GetResult(); } catch (ObjectDisposedException) { disposedRejected = true; }
        check(closing.Disposed && !disposedPreview.IsPlaying && disposedRejected && created == 1,
            "Closing recording preview is idempotent, releases its player and rejects playback after close");

        int invalidStarts = 0;
        using (var preview = new SoundboardRecordingPreview((path, output, volume) => { invalidStarts++; return new FakePlayer(path, output, volume); }))
        {
            int invalid = 0;
            foreach (float value in new[] { -0.1f, 1.1f, float.NaN, float.PositiveInfinity })
                try { preview.PlayAsync("trimmed.mp3", null, value); } catch (ArgumentOutOfRangeException) { invalid++; }
            check(invalid == 4 && invalidStarts == 0, "Invalid preview volume is rejected before an output is opened");
        }

        SoundboardRecordingPreview.ValidateListeningOutput(DataFlow.Render, DeviceState.Active, "Speakers (Realtek Audio)", "Realtek Audio");
        check(true, "Recording preview accepts an active physical speaker output");
        int virtualRejected = 0;
        foreach (var names in new[] {
            new[] { "CABLE Input (VB-Audio Virtual Cable)", "VB-Audio Virtual Cable" },
            new[] { "Renamed headphones", "VB-Audio Virtual Cable" },
            new[] { "VoiceMeeter Input", "VoiceMeeter" },
            new[] { "Voicemod Virtual Audio Device", "Voicemod" } })
            try { SoundboardRecordingPreview.ValidateListeningOutput(DataFlow.Render, DeviceState.Active, names[0], names[1]); }
            catch (InvalidOperationException error) { if (error.Message.Contains("Listen on")) virtualRejected++; }
        check(virtualRejected == 4, "Local recording preview rejects virtual outputs, including a renamed virtual default, with a physical-output instruction");
        int unavailable = 0;
        try { SoundboardRecordingPreview.ValidateListeningOutput(DataFlow.Capture, DeviceState.Active, "Microphone", "Realtek Audio"); } catch (InvalidOperationException) { unavailable++; }
        try { SoundboardRecordingPreview.ValidateListeningOutput(DataFlow.Render, DeviceState.Unplugged, "Headphones", "Realtek Audio"); } catch (InvalidOperationException) { unavailable++; }
        check(unavailable == 2, "Recording preview rejects capture endpoints and unavailable headphones without opening a device");

        FakePlayer errorPlayer = null;
        using (var preview = new SoundboardRecordingPreview((path, output, volume) => errorPlayer = new FakePlayer(path, output, volume)))
        {
            preview.PlayAsync("trimmed.mp3", null, 0.25f).GetAwaiter().GetResult();
            var stale = errorPlayer.SnapshotStopped(); errorPlayer.EmitStopped(new InvalidOperationException("Output disconnected"));
            preview.StopAsync().GetAwaiter().GetResult();
            check(!preview.IsPlaying && errorPlayer.Disposed && preview.Error == "Output disconnected", "A delayed native preview failure is visible to the dialog after cleanup");
            preview.PlayAsync("snapshot.wav", "headphones", 0.25f).GetAwaiter().GetResult(); stale(new InvalidOperationException("Stale output failure"));
            check(preview.IsPlaying && preview.Error == null, "Starting another preview clears the error and ignores stale native failures");
        }
        FakePlayer immediate = null;
        using (var preview = new SoundboardRecordingPreview((path, output, volume) => immediate = new FakePlayer(path, output, volume) { StopOnPlay = new InvalidOperationException("Output failed to start") }))
        {
            bool rejected = false;
            try { preview.PlayAsync("snapshot.wav", null, 0.25f).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && !preview.IsPlaying && immediate.Disposed && preview.Error == "Output failed to start", "An immediate stopped/error callback fails PlayAsync before the dialog can report successful startup");
        }
        using (var preview = new SoundboardRecordingPreview((path, output, volume) => new FakePlayer(path, output, volume) { DelayStop = true }))
        {
            preview.PlayAsync("snapshot.wav", null, 0.25f).GetAwaiter().GetResult(); bool rejected = false;
            try { preview.StopAsync().GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            check(rejected && !preview.IsPlaying && preview.Error.Contains("finish stopping"), "A missing preview stop notification has a bounded wait and a useful error");
        }
        WaveSnapshotChecks(check);
    }

    private static void WaveSnapshotChecks(Action<bool, string> check)
    {
        string folder = Path.Combine(Path.GetTempPath(), "UtilityTool-WavePreviewChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); string wave = Path.Combine(folder, "snapshot.wav"), invalid = Path.Combine(folder, "invalid.wav");
        try
        {
            using (var writer = new WaveFileWriter(wave, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
                for (int i = 0; i < 514; i++) writer.WriteSample(0.5f);
            using (var mixer = new SoundboardMixer { Volume = 0.25f })
            {
                mixer.Add(SoundboardRecordingPreview.OpenPreviewClip(wave));
                var samples = new float[128]; int read, total = 0; bool unchanged = true;
                while ((read = mixer.Read(samples, 0, samples.Length)) != 0) { total += read; unchanged &= samples.Take(read).All(value => value == 0.125f); }
                check(total == 514 && unchanged && mixer.Read(samples, 0, samples.Length) == 0, "Direct WAV preview preserves exact native frames and volume without MP3 padding or decoder startup");
                using (var exclusive = new FileStream(wave, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    check(exclusive.Length > 0, "Direct WAV preview EOF releases the snapshot file for replacement");
            }
            using (var writer = new WaveFileWriter(invalid, new WaveFormat(44100, 16, 1))) writer.Write(new byte[200], 0, 200);
            bool rejected = false;
            try { var clip = SoundboardRecordingPreview.OpenPreviewClip(invalid); ((IDisposable)clip).Dispose(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "Direct WAV preview rejects an unsupported snapshot format without keeping its file open");
            using (var exclusive = new FileStream(invalid, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally { if (File.Exists(wave)) File.Delete(wave); if (File.Exists(invalid)) File.Delete(invalid); Directory.Delete(folder, false); }
    }

    private sealed class FakePlayer : ISoundboardRecordingPreviewPlayer
    {
        internal readonly string Path, Output;
        internal readonly float Volume;
        internal readonly ManualResetEventSlim StopRequested = new ManualResetEventSlim();
        internal int Plays;
        internal bool DelayStop, FailPlay, FailStop;
        internal Exception StopOnPlay;
        internal volatile bool Disposed;
        public event Action<Exception> Stopped;
        internal FakePlayer(string path, string output, float volume) { Path = path; Output = output; Volume = volume; }
        public void Play() { if (FailPlay) throw new InvalidOperationException("Fake startup failure"); Plays++; if (StopOnPlay != null) EmitStopped(StopOnPlay); }
        public void Stop()
        {
            StopRequested.Set();
            if (FailStop) throw new InvalidOperationException("Fake stop failure");
            if (!DelayStop) EmitStopped();
        }
        internal void EmitStopped(Exception error = null) { var handler = Stopped; if (handler != null) handler(error); }
        internal Action<Exception> SnapshotStopped() { return Stopped; }
        public void Dispose() { Disposed = true; StopRequested.Dispose(); }
    }
}
