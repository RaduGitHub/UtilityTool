using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using DeviceEnumerator = NAudio.CoreAudioApi.MMDeviceEnumerator;

namespace UtilityTool.Backend
{
    internal enum SoundboardRecordingSource { ComputerAudio, Microphone }

    internal interface ISoundboardCapture : IDisposable
    {
        WaveFormat WaveFormat { get; }
        event Action<byte[], int> DataAvailable;
        event Action<Exception> Stopped;
        void Start();
        void Stop();
    }

    // Original samples stream to a temporary WAV. MP3 encoding belongs to export.
    internal sealed class SoundboardRecorder : IDisposable
    {
        private readonly SemaphoreSlim lifecycle = new SemaphoreSlim(1, 1);
        private readonly Func<SoundboardRecordingSource, string, ISoundboardCapture> factory;
        private readonly string temporaryRoot;
        private volatile CaptureRun active;
        private volatile bool recording, hasRecording;
        private volatile float peak;
        private volatile string wavePath, error;
        private volatile Task disposalCompleted = Task.FromResult(0);
        private string ownedDirectory;
        private long durationTicks, recordingStarted, lastPacketTimestamp;
        private int disposed;

        internal bool IsRecording { get { return recording; } }
        internal bool HasRecording { get { return hasRecording; } }
        internal float Peak
        {
            get
            {
                // Silent loopback outputs stop sending packets. Decay the meter
                // without inserting samples or changing the recorded duration.
                long packet = Interlocked.Read(ref lastPacketTimestamp);
                return recording && packet != 0 && ElapsedTicks(packet) < TimeSpan.TicksPerMillisecond * 250 ? peak : 0;
            }
        }
        internal string WavePath { get { return wavePath; } }
        internal string Error { get { return error; } }
        internal Task DisposalCompleted { get { return disposalCompleted; } }
        internal TimeSpan Duration
        {
            get
            {
                long ticks = Interlocked.Read(ref durationTicks);
                if (recording)
                {
                    long started = Interlocked.Read(ref recordingStarted);
                    if (started != 0) ticks = Math.Max(ticks, ElapsedTicks(started));
                }
                return TimeSpan.FromTicks(Math.Max(0, ticks));
            }
        }

        internal SoundboardRecorder(Func<SoundboardRecordingSource, string, ISoundboardCapture> factory = null, string temporaryRoot = null)
        {
            this.factory = factory ?? ((source, id) => new NativeSoundboardCapture(source, id));
            this.temporaryRoot = Path.GetFullPath(temporaryRoot ?? Path.Combine(Path.GetTempPath(), "UtilityTool", "Recordings"));
        }

        internal static List<SoundboardDevice> ReadSources(SoundboardRecordingSource source)
        {
            if (source == SoundboardRecordingSource.Microphone) return SoundboardDevices.Read().Microphones;
            if (source != SoundboardRecordingSource.ComputerAudio) throw new ArgumentOutOfRangeException("source");
            return WindowsAudio.GetOutputs().OrderByDescending(device => device.IsDefault)
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(device => new SoundboardDevice(device.Id, device.Name)).ToList();
        }

        internal Task StartAsync(SoundboardRecordingSource source, string deviceId)
        {
            if (!Enum.IsDefined(typeof(SoundboardRecordingSource), source)) throw new ArgumentOutOfRangeException("source");
            if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Choose an audio source before recording.", "deviceId");
            return Task.Run(async () =>
            {
                await lifecycle.WaitAsync().ConfigureAwait(false);
                CaptureRun run = null;
                try
                {
                    Exception startupFailure = null;
                    try
                    {
                        CheckAlive();
                        await FinishRun(active).ConfigureAwait(false);
                        DeleteRecording(); error = null;
                        CheckAlive();
                        var capture = factory(source, deviceId);
                        if (capture == null) throw new InvalidOperationException("This audio source could not be opened.");
                        try
                        {
                            CheckAlive();
                            WaveFormat format = capture.WaveFormat;
                            ValidateFormat(format);
                            string directory = Path.GetFullPath(Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N")));
                            VerifyOwnedDirectory(directory);
                            Directory.CreateDirectory(directory); ownedDirectory = directory;
                            string path = Path.Combine(directory, "recording.wav");
                            run = new CaptureRun(capture, new WaveFileWriter(path, format), source);
                            wavePath = path; active = run;
                            run.Data = (data, count) => WritePacket(run, data, count);
                            run.End = failure => CaptureStopped(run, failure);
                            capture.DataAvailable += run.Data; capture.Stopped += run.End;
                            run.Started = Stopwatch.GetTimestamp();
                            Interlocked.Exchange(ref recordingStarted, run.Started);
                            // Subscribe before Start: an immediate device failure must
                            // reach the completion task instead of leaving stale state.
                            CheckAlive();
                            recording = true;
                            run.StartInvoked = true;
                            try { capture.Start(); }
                            catch { run.Stopped.TrySetResult(true); throw; }
                            CheckAlive();
                            if (run.Stopped.Task.IsCompleted)
                            {
                                await FinishRun(run).ConfigureAwait(false);
                                if (run.Failure != null) throw new InvalidOperationException("Recording could not start: " + run.Failure.Message, run.Failure);
                            }
                        }
                        catch
                        {
                            if (run == null) capture.Dispose();
                            throw;
                        }
                    }
                    catch (Exception failure)
                    {
                        startupFailure = failure;
                        error = failure is ObjectDisposedException ? null : failure.Message;
                    }
                    if (startupFailure != null)
                    {
                        if (run != null) await FinishRun(run).ConfigureAwait(false);
                        DeleteRecording();
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startupFailure).Throw();
                    }
                }
                finally { lifecycle.Release(); }
            });
        }

        internal Task StopAsync()
        {
            return Task.Run(async () =>
            {
                await lifecycle.WaitAsync().ConfigureAwait(false);
                try { await FinishRun(active).ConfigureAwait(false); }
                finally { lifecycle.Release(); }
            });
        }

        private void WritePacket(CaptureRun run, byte[] data, int count)
        {
            if (data == null || count <= 0 || Volatile.Read(ref disposed) != 0) return;
            lock (run.Writing)
            {
                if (!run.AcceptData) return;
                try
                {
                    if (count > data.Length) throw new InvalidDataException("The audio source returned an invalid packet.");
                    int frames = count / run.Format.BlockAlign;
                    if (frames == 0) return;
                    // Loopback sends no packets while an output is silent. Place
                    // incoming samples on the recording clock, retaining gaps.
                    if (run.Source == SoundboardRecordingSource.ComputerAudio)
                    {
                        long elapsedFrames = TicksToFrames(ElapsedTicks(run.Started), run.Format.SampleRate);
                        WriteSilence(run, Math.Max(0, elapsedFrames - frames - run.Frames));
                    }
                    run.Writer.Write(data, 0, checked(frames * run.Format.BlockAlign));
                    run.Frames += frames;
                    peak = PacketPeak(data, frames, run.Format);
                    Interlocked.Exchange(ref lastPacketTimestamp, Stopwatch.GetTimestamp());
                    hasRecording = true;
                    Interlocked.Exchange(ref durationTicks, FramesToTicks(run.Frames, run.Format.SampleRate));
                }
                catch (Exception failure)
                {
                    run.Failure = failure; run.AcceptData = false; error = failure.Message;
                    CaptureStopped(run, failure);
                }
            }
        }

        private void CaptureStopped(CaptureRun run, Exception failure)
        {
            if (failure != null) { run.Failure = failure; error = "Recording stopped: " + failure.Message; }
            if (ReferenceEquals(active, run)) { recording = false; peak = 0; }
            run.Stopped.TrySetResult(true);
            // Capture callbacks run on the capture thread. Never join that same
            // thread from its callback; cleanup runs on an MTA worker instead.
            Task.Run(async () =>
            {
                await lifecycle.WaitAsync().ConfigureAwait(false);
                try { if (ReferenceEquals(active, run)) await FinishRun(run).ConfigureAwait(false); }
                catch (Exception cleanupFailure) { error = cleanupFailure.Message; System.Diagnostics.Trace.WriteLine(cleanupFailure); }
                finally { lifecycle.Release(); }
            });
        }

        private async Task FinishRun(CaptureRun run)
        {
            if (run == null || run.Finished) return;
            run.Finished = true;
            if (ReferenceEquals(active, run)) { recording = false; peak = 0; }
            long stopTicks = run.Started == 0 ? 0 : ElapsedTicks(run.Started);
            try
            {
                try { run.Capture.Stop(); }
                catch (Exception failure) { run.Failure = failure; error = "Unable to stop recording: " + failure.Message; }
                // Native Stop joins its worker and fires Stopped. The bounded
                // wait also handles adapters which return before that callback.
                if (run.StartInvoked && !run.Stopped.Task.IsCompleted)
                {
                    await Task.WhenAny(run.Stopped.Task, Task.Delay(1500)).ConfigureAwait(false);
                }
            }
            finally
            {
                run.Capture.DataAvailable -= run.Data; run.Capture.Stopped -= run.End;
                try { run.Capture.Dispose(); }
                catch (Exception failure) { if (error == null) error = failure.Message; }
                lock (run.Writing)
                {
                    run.AcceptData = false;
                    bool closed = true;
                    try
                    {
                        if (run.Source == SoundboardRecordingSource.ComputerAudio && run.Failure == null)
                            WriteSilence(run, Math.Max(0, TicksToFrames(stopTicks, run.Format.SampleRate) - run.Frames));
                    }
                    catch (Exception failure) { error = failure.Message; }
                    finally
                    {
                        try { run.Writer.Dispose(); }
                        catch (Exception failure) { closed = false; error = failure.Message; }
                    }
                    if (ReferenceEquals(active, run))
                    {
                        Interlocked.Exchange(ref durationTicks, FramesToTicks(run.Frames, run.Format.SampleRate));
                        hasRecording = closed && run.Frames > 0;
                        active = null;
                    }
                }
            }
        }

        private static void WriteSilence(CaptureRun run, long frames)
        {
            while (frames > 0)
            {
                int writeFrames = (int)Math.Min(frames, run.Silence.Length / run.Format.BlockAlign);
                run.Writer.Write(run.Silence, 0, writeFrames * run.Format.BlockAlign);
                run.Frames += writeFrames; frames -= writeFrames;
            }
        }
        private static bool IsFloat(WaveFormat format)
        {
            var extended = format as WaveFormatExtensible;
            return format.Encoding == WaveFormatEncoding.IeeeFloat || extended != null && extended.SubFormat == new Guid("00000003-0000-0010-8000-00AA00389B71");
        }
        private static bool IsPcm(WaveFormat format)
        {
            var extended = format as WaveFormatExtensible;
            return format.Encoding == WaveFormatEncoding.Pcm || extended != null && extended.SubFormat == new Guid("00000001-0000-0010-8000-00AA00389B71");
        }
        private static void ValidateFormat(WaveFormat format)
        {
            if (format == null || format.SampleRate <= 0 || format.Channels <= 0 || format.BlockAlign <= 0)
                throw new InvalidOperationException("This audio source has an invalid recording format.");
            bool supported = IsFloat(format) ? format.BitsPerSample == 32 || format.BitsPerSample == 64
                : IsPcm(format) && new[] { 8, 16, 24, 32 }.Contains(format.BitsPerSample);
            if (!supported || format.BlockAlign < format.Channels * (format.BitsPerSample / 8))
                throw new InvalidOperationException("This audio source needs a supported PCM or floating-point format.");
        }
        private static float PacketPeak(byte[] data, int frames, WaveFormat format)
        {
            int bytes = format.BitsPerSample / 8; bool floating = IsFloat(format); double maximum = 0;
            for (int frame = 0; frame < frames; frame++)
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    int offset = frame * format.BlockAlign + channel * bytes; double sample;
                    if (floating) sample = bytes == 4 ? BitConverter.ToSingle(data, offset) : BitConverter.ToDouble(data, offset);
                    else if (bytes == 1) sample = (data[offset] - 128) / 128.0;
                    else if (bytes == 2) sample = BitConverter.ToInt16(data, offset) / 32768.0;
                    else if (bytes == 3)
                    { int value = data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16; if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000); sample = value / 8388608.0; }
                    else sample = BitConverter.ToInt32(data, offset) / 2147483648.0;
                    if (!double.IsNaN(sample) && !double.IsInfinity(sample)) maximum = Math.Max(maximum, Math.Abs(sample));
                }
            return (float)Math.Min(1, maximum);
        }
        private static long ElapsedTicks(long started) { return (long)((Stopwatch.GetTimestamp() - started) * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency); }
        private static long FramesToTicks(long frames, int rate) { return (long)(frames * (double)TimeSpan.TicksPerSecond / rate); }
        private static long TicksToFrames(long ticks, int rate) { return (long)(ticks * (double)rate / TimeSpan.TicksPerSecond); }
        private void CheckAlive() { if (Volatile.Read(ref disposed) != 0) throw new ObjectDisposedException("SoundboardRecorder"); }
        private void VerifyOwnedDirectory(string directory)
        {
            string root = temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string leaf = Path.GetFileName(directory);
            Guid id;
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(leaf, "N", out id)
                || !string.Equals(Path.GetDirectoryName(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The recording folder is outside its temporary location.");
        }
        private void DeleteRecording()
        {
            string directory = ownedDirectory;
            wavePath = null; ownedDirectory = null; hasRecording = false; peak = 0;
            Interlocked.Exchange(ref durationTicks, 0); Interlocked.Exchange(ref recordingStarted, 0);
            Interlocked.Exchange(ref lastPacketTimestamp, 0);
            if (directory == null) return;
            VerifyOwnedDirectory(directory);
            string path = Path.Combine(directory, "recording.wav");
            if (File.Exists(path)) File.Delete(path);
            // Do not recursively delete preview/export files owned by another
            // component. The editor removes its own tracked files first.
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            recording = false; peak = 0;
            disposalCompleted = Task.Run(async () =>
            {
                await lifecycle.WaitAsync().ConfigureAwait(false);
                try
                {
                    try { await FinishRun(active).ConfigureAwait(false); }
                    finally { DeleteRecording(); }
                }
                catch (Exception failure) { error = failure.Message; System.Diagnostics.Trace.WriteLine(failure); }
                finally { lifecycle.Release(); }
            });
        }

        private sealed class CaptureRun
        {
            internal readonly ISoundboardCapture Capture;
            internal readonly WaveFileWriter Writer;
            internal readonly WaveFormat Format;
            internal readonly SoundboardRecordingSource Source;
            internal readonly object Writing = new object();
            internal readonly byte[] Silence;
            internal readonly TaskCompletionSource<bool> Stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Action<byte[], int> Data;
            internal Action<Exception> End;
            internal bool AcceptData = true, Finished, StartInvoked;
            internal long Started, Frames;
            internal Exception Failure;
            internal CaptureRun(ISoundboardCapture capture, WaveFileWriter writer, SoundboardRecordingSource source)
            {
                Capture = capture; Writer = writer; Format = capture.WaveFormat; Source = source;
                int size = Math.Max(Format.BlockAlign, 65536 / Format.BlockAlign * Format.BlockAlign);
                Silence = new byte[size];
                if (IsPcm(Format) && Format.BitsPerSample == 8) for (int i = 0; i < Silence.Length; i++) Silence[i] = 128;
            }
        }
    }

    internal sealed class NativeSoundboardCapture : ISoundboardCapture
    {
        private DeviceEnumerator enumerator;
        private MMDevice device;
        private WasapiCapture capture;
        public WaveFormat WaveFormat { get; private set; }
        public event Action<byte[], int> DataAvailable;
        public event Action<Exception> Stopped;
        internal NativeSoundboardCapture(SoundboardRecordingSource source, string id)
        {
            try
            {
                enumerator = new DeviceEnumerator(); device = enumerator.GetDevice(id);
                if (source == SoundboardRecordingSource.ComputerAudio)
                {
                    if (device.DataFlow != DataFlow.Render || WindowsAudio.IsVirtualOutput(device.FriendlyName))
                        throw new InvalidOperationException("Choose speakers or headphones to record computer audio.");
                    capture = new WasapiLoopbackCapture(device);
                }
                else
                {
                    if (device.DataFlow != DataFlow.Capture) throw new InvalidOperationException("Choose a microphone to record your voice.");
                    capture = new WasapiCapture(device, true, 40);
                }
                WaveFormat = capture.WaveFormat;
                capture.DataAvailable += (sender, packet) => { var data = DataAvailable; if (data != null) data(packet.Buffer, packet.BytesRecorded); };
                capture.RecordingStopped += (sender, stopped) => { var end = Stopped; if (end != null) end(stopped.Exception); };
            }
            catch { Dispose(); throw; }
        }
        public void Start() { capture.StartRecording(); }
        public void Stop() { if (capture != null) capture.StopRecording(); }
        public void Dispose()
        {
            try { if (capture != null) capture.Dispose(); }
            finally
            {
                capture = null;
                try { if (device != null) device.Dispose(); }
                finally { device = null; if (enumerator != null) enumerator.Dispose(); enumerator = null; }
            }
        }
    }
}
