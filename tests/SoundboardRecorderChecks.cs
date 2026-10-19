using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using UtilityTool.Backend;

internal static class SoundboardRecorderChecks
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string root = Path.Combine(output, "recorder-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        FakeCapture capture = null;
        var recorder = new SoundboardRecorder((source, id) => capture = new FakeCapture(new WaveFormat(48000, 16, 1)), root);
        recorder.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone").GetAwaiter().GetResult();
        string first = recorder.WavePath;
        check(recorder.IsRecording && !recorder.HasRecording && File.Exists(first) && capture.Starts == 1, "Recording starts only through the requested capture adapter and creates an owned temporary WAV");
        byte[] pcm = Shorts(0, 8192, -16384, 0); capture.Push(pcm);
        check(recorder.HasRecording && Math.Abs(recorder.Peak - 0.5f) < 0.000001f, "Microphone packets are streamed to disk and report their current PCM peak");
        recorder.StopAsync().GetAwaiter().GetResult();
        using (var read = new WaveFileReader(first))
        {
            byte[] restored = new byte[read.Length]; read.Read(restored, 0, restored.Length);
            check(!recorder.IsRecording && recorder.HasRecording && recorder.Peak == 0 && restored.SequenceEqual(pcm), "Stopping closes a valid WAV with the original PCM samples and no repeated audio");
            check(Math.Abs(recorder.Duration.Ticks - (long)(read.Length * (double)TimeSpan.TicksPerSecond / read.WaveFormat.AverageBytesPerSecond)) <= 1, "Stopped duration matches the WAV sample count");
        }
        var oldCapture = capture;
        recorder.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone").GetAwaiter().GetResult();
        check(!File.Exists(first) && oldCapture.Disposed && recorder.WavePath != first && !recorder.HasRecording, "Starting a fresh take clears only the previous owned recording");
        recorder.Dispose(); recorder.DisposalCompleted.GetAwaiter().GetResult();
        check(capture.Disposed && !recorder.IsRecording && !recorder.HasRecording && recorder.WavePath == null, "Disposing releases active capture and removes its temporary recording");

        CheckPeak(check, root, new WaveFormat(1000, 8, 1), new byte[] { 0, 128, 255 }, 1, "Unsigned 8-bit PCM peak is measured around its 128 midpoint");
        CheckPeak(check, root, new WaveFormat(1000, 24, 1), new byte[] { 0, 0, 128, 0, 0, 0 }, 1, "24-bit PCM peak correctly sign-extends negative full-scale samples");
        CheckPeak(check, root, new WaveFormat(1000, 32, 1), BitConverter.GetBytes(int.MinValue), 1, "32-bit PCM peak handles negative full scale without integer overflow");
        CheckPeak(check, root, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), Floats(-0.75f, 0.25f), 0.75f, "Stereo floating-point recording measures the highest channel peak");
        CheckPeak(check, root, new WaveFormatExtensible(48000, 32, 2), Floats(0.5f, -0.9f), 0.9f, "Extensible floating-point format is recognized for level metering");
        CheckPeak(check, root, WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, 1000, 1, 8000, 8, 64), BitConverter.GetBytes(-0.4), 0.4f, "64-bit floating-point packets report the correct level");
        CheckMeterDecay(check, root, SoundboardRecordingSource.ComputerAudio);
        CheckMeterDecay(check, root, SoundboardRecordingSource.Microphone);

        using (var pc = new SoundboardRecorder((source, id) => capture = new FakeCapture(new WaveFormat(1000, 16, 1)), root))
        {
            pc.StartAsync(SoundboardRecordingSource.ComputerAudio, "fake-speakers").GetAwaiter().GetResult();
            Thread.Sleep(50); capture.Push(Shorts(Enumerable.Repeat((short)8192, 20).ToArray())); Thread.Sleep(30);
            check(pc.Duration.TotalMilliseconds >= 70, "Computer-audio duration advances through periods without loopback packets");
            pc.StopAsync().GetAwaiter().GetResult();
            using (var read = new WaveFileReader(pc.WavePath))
            {
                var bytes = new byte[read.Length]; read.Read(bytes, 0, bytes.Length);
                var samples = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2)).ToArray();
                int start = Array.FindIndex(samples, value => value != 0), end = Array.FindLastIndex(samples, value => value != 0);
                check(start >= 20 && samples.Length - end - 1 >= 10 && end - start + 1 == 20, "Computer recording preserves leading and trailing silence around the captured packet");
                check(Math.Abs(pc.Duration.Ticks - (long)(read.Length * (double)TimeSpan.TicksPerSecond / read.WaveFormat.AverageBytesPerSecond)) <= 1, "Computer recording final duration is derived from its padded WAV");
            }
            pc.Dispose(); pc.DisposalCompleted.GetAwaiter().GetResult();
        }
        using (var quiet = new SoundboardRecorder((source, id) => capture = new FakeCapture(new WaveFormat(1000, 8, 1)), root))
        {
            quiet.StartAsync(SoundboardRecordingSource.ComputerAudio, "fake-speakers").GetAwaiter().GetResult(); Thread.Sleep(25);
            quiet.StopAsync().GetAwaiter().GetResult();
            using (var read = new WaveFileReader(quiet.WavePath))
            { var bytes = new byte[read.Length]; read.Read(bytes, 0, bytes.Length); check(quiet.HasRecording && bytes.Length >= 15 && bytes.All(value => value == 128), "A silent computer recording is valid silence, including the unsigned PCM midpoint"); }
            quiet.Dispose(); quiet.DisposalCompleted.GetAwaiter().GetResult();
        }
        using (var broken = new SoundboardRecorder((source, id) => capture = new FakeCapture(new WaveFormat(1000, 16, 1)), root))
        {
            broken.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone").GetAwaiter().GetResult(); capture.Push(Shorts(8192, -8192));
            capture.Fail(new IOException("Device disconnected")); broken.StopAsync().GetAwaiter().GetResult();
            check(!broken.IsRecording && capture.Disposed && broken.Error.Contains("Device disconnected") && broken.HasRecording, "A capture failure stops safely, preserves completed samples and exposes a readable error");
            using (var read = new WaveFileReader(broken.WavePath)) check(read.Length == 4, "A failed capture still closes the partial WAV header correctly");
            broken.Dispose(); broken.DisposalCompleted.GetAwaiter().GetResult();
        }
        using (var failedStart = new SoundboardRecorder((source, id) => capture = new FakeCapture(new WaveFormat(1000, 16, 1)) { StartFailure = new IOException("Cannot start capture") }, root))
        {
            bool rejected = false; try { failedStart.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone").GetAwaiter().GetResult(); } catch (IOException) { rejected = true; }
            check(rejected && capture.Disposed && !failedStart.IsRecording && !failedStart.HasRecording && failedStart.WavePath == null && failedStart.Error.Contains("Cannot start capture"), "Failed initialization rolls back capture and temporary WAV without a stale recording state");
            failedStart.Dispose(); failedStart.DisposalCompleted.GetAwaiter().GetResult();
        }
        using (var immediate = new SoundboardRecorder((source, id) => capture = new FakeCapture(new WaveFormat(1000, 16, 1)) { StopDuringStart = true }, root))
        {
            immediate.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone").GetAwaiter().GetResult();
            check(!immediate.IsRecording && capture.Disposed, "A stopped callback during Start cannot leave recording falsely active");
            immediate.Dispose(); immediate.DisposalCompleted.GetAwaiter().GetResult();
        }
        using (var stopped = new SoundboardRecorder((source, id) => capture = new FakeCapture(new WaveFormat(1000, 16, 1)) { StopFailure = new IOException("Cannot stop capture") }, root))
        {
            stopped.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone").GetAwaiter().GetResult(); capture.Push(Shorts(8192)); stopped.StopAsync().GetAwaiter().GetResult();
            check(capture.Disposed && !stopped.IsRecording && stopped.Error.Contains("Cannot stop capture"), "A Stop failure still disposes capture and makes the recorder inactive");
            stopped.Dispose(); stopped.DisposalCompleted.GetAwaiter().GetResult();
        }
        using (var entered = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var cancelled = new SoundboardRecorder((source, id) => { entered.Set(); release.Wait(); return capture = new FakeCapture(new WaveFormat(1000, 16, 1)); }, root);
            var start = cancelled.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone");
            if (!entered.Wait(3000)) throw new TimeoutException("Capture factory did not start.");
            var timer = System.Diagnostics.Stopwatch.StartNew(); cancelled.Dispose();
            check(timer.ElapsedMilliseconds < 500 && !cancelled.IsRecording, "Closing during capture initialization returns promptly instead of blocking the UI");
            release.Set(); bool rejected = false; try { start.GetAwaiter().GetResult(); } catch (ObjectDisposedException) { rejected = true; }
            cancelled.DisposalCompleted.GetAwaiter().GetResult();
            check(rejected && capture.Disposed && capture.Starts == 0 && cancelled.WavePath == null, "A cancelled initialization never starts capture and releases the newly created adapter");
        }
        using (var preserved = new SoundboardRecorder((source, id) => capture = new FakeCapture(new WaveFormat(1000, 16, 1)), root))
        {
            preserved.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone").GetAwaiter().GetResult(); capture.Push(Shorts(8192)); preserved.StopAsync().GetAwaiter().GetResult();
            string wave = preserved.WavePath, folder = Path.GetDirectoryName(wave), foreign = Path.Combine(folder, "untracked-preview.mp3"); File.WriteAllText(foreign, "owned by another component");
            preserved.Dispose(); preserved.DisposalCompleted.GetAwaiter().GetResult();
            check(!File.Exists(wave) && File.Exists(foreign), "Recorder disposal deletes its WAV without recursively deleting unrelated preview or export files");
            File.Delete(foreign); Directory.Delete(folder);
        }
    }
    private static void CheckPeak(Action<bool, string> check, string root, WaveFormat format, byte[] data, float expected, string label)
    {
        FakeCapture capture = null; var recorder = new SoundboardRecorder((source, id) => capture = new FakeCapture(format), root);
        try { recorder.StartAsync(SoundboardRecordingSource.Microphone, "fake-microphone").GetAwaiter().GetResult(); capture.Push(data); check(Math.Abs(recorder.Peak - expected) < 0.000001f, label); recorder.StopAsync().GetAwaiter().GetResult(); }
        finally { recorder.Dispose(); recorder.DisposalCompleted.GetAwaiter().GetResult(); }
    }
    private static void CheckMeterDecay(Action<bool, string> check, string root, SoundboardRecordingSource source)
    {
        FakeCapture capture = null; var recorder = new SoundboardRecorder((kind, id) => capture = new FakeCapture(new WaveFormat(1000, 16, 1)), root);
        try
        {
            recorder.StartAsync(source, "fake-source").GetAwaiter().GetResult(); capture.Push(Shorts(8192));
            check(Math.Abs(recorder.Peak - 0.25f) < 0.000001f, source + " meter reports a fresh packet immediately");
            Thread.Sleep(300);
            check(recorder.Peak == 0 && recorder.IsRecording && recorder.Duration.TotalMilliseconds >= 250, source + " meter decays after packets stop while recording and its clock continue");
            capture.Push(Shorts(-24576));
            check(Math.Abs(recorder.Peak - 0.75f) < 0.000001f, source + " meter resumes with the next real packet");
            recorder.StopAsync().GetAwaiter().GetResult();
            using (var read = new WaveFileReader(recorder.WavePath))
            {
                var bytes = new byte[read.Length]; read.Read(bytes, 0, bytes.Length);
                var samples = Enumerable.Range(0, bytes.Length / 2).Select(i => BitConverter.ToInt16(bytes, i * 2)).Where(value => value != 0).ToArray();
                check(samples.SequenceEqual(new short[] { 8192, -24576 }), source + " meter decay does not alter or discard captured samples");
            }
        }
        finally { recorder.Dispose(); recorder.DisposalCompleted.GetAwaiter().GetResult(); }
    }
    private static byte[] Shorts(params short[] values) { var result = new byte[values.Length * 2]; Buffer.BlockCopy(values, 0, result, 0, result.Length); return result; }
    private static byte[] Floats(params float[] values) { var result = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, result, 0, result.Length); return result; }
    private sealed class FakeCapture : ISoundboardCapture
    {
        public WaveFormat WaveFormat { get; private set; }
        public event Action<byte[], int> DataAvailable;
        public event Action<Exception> Stopped;
        internal bool Disposed, StopDuringStart;
        internal int Starts;
        internal Exception StartFailure, StopFailure;
        internal FakeCapture(WaveFormat format) { WaveFormat = format; }
        public void Start() { Starts++; if (StartFailure != null) throw StartFailure; if (StopDuringStart) Fail(null); }
        public void Stop() { Fail(StopFailure); if (StopFailure != null) throw StopFailure; }
        public void Dispose() { Disposed = true; }
        internal void Push(byte[] bytes) { var handler = DataAvailable; if (handler != null) handler(bytes, bytes.Length); }
        internal void Fail(Exception failure) { var handler = Stopped; if (handler != null) handler(failure); }
    }
}
