using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using DeviceEnumerator = NAudio.CoreAudioApi.MMDeviceEnumerator;

namespace UtilityTool.Backend
{
    internal interface ISoundboardRecordingPreview : IDisposable
    {
        bool IsPlaying { get; }
        string Error { get; }
        Task PlayAsync(string mp3Path, string listeningOutputId, float volume);
        Task StopAsync();
    }

    // The injectable player keeps lifecycle checks independent of audio hardware.
    internal interface ISoundboardRecordingPreviewPlayer : IDisposable
    {
        event Action<Exception> Stopped;
        void Play();
        void Stop();
    }

    // Recording previews use their own local output, never the call-audio mixer.
    // FIFO worker operations also ensure that Cancel cannot race a queued Play.
    internal sealed class SoundboardRecordingPreview : ISoundboardRecordingPreview
    {
        private readonly object sync = new object();
        private readonly Func<string, string, float, ISoundboardRecordingPreviewPlayer> factory;
        private Task pending = Task.CompletedTask;
        private Task disposal;
        private PreviewRun current;
        private bool disposed, playing;
        private string error;

        public bool IsPlaying { get { lock (sync) return playing; } }
        public string Error { get { lock (sync) return error; } }

        internal SoundboardRecordingPreview(Func<string, string, float, ISoundboardRecordingPreviewPlayer> factory = null)
        { this.factory = factory ?? ((path, output, volume) => new WasapiRecordingPreviewPlayer(path, output, volume)); }

        public Task PlayAsync(string mp3Path, string listeningOutputId, float volume)
        {
            if (string.IsNullOrWhiteSpace(mp3Path)) throw new ArgumentException("Choose a recording to preview.", "mp3Path");
            if (float.IsNaN(volume) || float.IsInfinity(volume) || volume < 0 || volume > 1) throw new ArgumentOutOfRangeException("volume");
            lock (sync)
            {
                if (disposed) return Task.FromException(new ObjectDisposedException("Recording preview"));
                return Enqueue(() => Start(mp3Path, listeningOutputId, volume));
            }
        }

        public Task StopAsync()
        {
            lock (sync)
            {
                if (disposed) return disposal ?? Task.CompletedTask;
                return Enqueue(() => { try { StopCurrent(false); } catch (Exception failure) { CacheError(failure); throw; } });
            }
        }

        // Called with sync held. ContinueWith runs even after a failed operation,
        // and always uses an MTA worker rather than the dialog's UI context.
        private Task Enqueue(Action action)
        {
            pending = pending.ContinueWith(ignored => action(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
            return pending;
        }

        private void Start(string path, string listeningOutputId, float volume)
        {
            lock (sync) { error = null; }
            PreviewRun run = null;
            try
            {
                StopCurrent(false);
                lock (sync) { if (disposed) throw new ObjectDisposedException("Recording preview"); }
                var player = factory(path, listeningOutputId, volume);
                if (player == null) throw new InvalidOperationException("The recording preview could not open its sound output.");
                run = new PreviewRun(player);
                run.StoppedHandler = failure => PlaybackStopped(run, failure);
                player.Stopped += run.StoppedHandler;
                lock (sync)
                {
                    if (disposed) { player.Stopped -= run.StoppedHandler; Release(player); throw new ObjectDisposedException("Recording preview"); }
                    current = run; playing = true;
                }
                run.Started = true;
                try { player.Play(); }
                catch { run.Stopped.TrySetResult(true); throw; }
                // Some adapters report an immediate asynchronous failure from
                // Play itself. Surface it instead of claiming playback started.
                if (run.Stopped.Task.IsCompleted && run.Failure != null)
                    throw new InvalidOperationException(run.Failure.Message, run.Failure);
            }
            catch (Exception failure)
            {
                CacheError(failure);
                if (run != null) StopCurrent(true);
                throw;
            }
        }

        private void CacheError(Exception failure)
        { lock (sync) { if (!(failure is ObjectDisposedException)) error = failure.Message; } }

        private void PlaybackStopped(PreviewRun run, Exception error)
        {
            // Signal without waiting for the service lock: Stop may be joining
            // the native audio thread, including its final stop/reset callback.
            if (error != null) run.Failure = error;
            run.Stopped.TrySetResult(true);
            if (error != null) System.Diagnostics.Trace.WriteLine(error);
            lock (sync)
            {
                if (!ReferenceEquals(current, run)) return;
                if (error != null) this.error = error.Message;
                playing = false;
                if (!disposed) Enqueue(() => Finish(run));
            }
        }

        private void Finish(PreviewRun run)
        {
            lock (sync)
            {
                if (!ReferenceEquals(current, run)) return;
                current = null; playing = false;
            }
            StopAndRelease(run, true);
        }

        private void StopCurrent(bool suppressErrors)
        {
            PreviewRun run;
            lock (sync) { run = current; current = null; playing = false; }
            if (run != null) StopAndRelease(run, suppressErrors);
        }

        private static void StopAndRelease(PreviewRun run, bool suppressErrors)
        {
            Exception failure = null;
            try
            {
                if (run.Started)
                {
                    run.Player.Stop();
                    // WASAPI can report Stopped before raising its notification.
                    // This closes that gap before replay or deleting the MP3.
                    if (!run.Stopped.Task.Wait(2000))
                        throw new InvalidOperationException("The preview output did not finish stopping. Reconnect the selected speakers or headphones and try again.");
                }
            }
            catch (Exception error) { failure = error; }
            finally { run.Player.Stopped -= run.StoppedHandler; Release(run.Player); }
            if (failure != null)
            { if (!suppressErrors) throw failure; System.Diagnostics.Trace.WriteLine(failure); }
        }

        public void Dispose()
        {
            Task close;
            lock (sync)
            {
                if (!disposed) { disposed = true; disposal = Enqueue(() => StopCurrent(true)); }
                close = disposal;
            }
            close.GetAwaiter().GetResult();
        }

        private static void Release(IDisposable value)
        { try { value.Dispose(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); } }

        internal static void ValidateListeningOutput(DataFlow flow, DeviceState state, string endpointName, string deviceName)
        {
            if (flow != DataFlow.Render) throw new InvalidOperationException("Choose speakers or headphones under Listen on to preview this recording.");
            if (state != DeviceState.Active) throw new InvalidOperationException("Reconnect the selected speakers or headphones, then refresh outputs.");
            if (WindowsAudio.IsVirtualOutput(endpointName) || WindowsAudio.IsVirtualOutput(deviceName))
                throw new InvalidOperationException("Choose speakers or headphones under Listen on to preview this recording locally.");
        }

        internal static ISampleProvider OpenPreviewClip(string path)
        { return string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) ? (ISampleProvider)new WavePreviewClip(path) : new SoundboardClip(path); }

        // The export snapshot is already standard float48k/stereo. Reading it
        // directly avoids MP3 encoding and Media Foundation decoder startup.
        private sealed class WavePreviewClip : ISampleProvider, IDisposable
        {
            private readonly WaveFileReader reader;
            private readonly ISampleProvider samples;
            internal WavePreviewClip(string path)
            {
                reader = new WaveFileReader(path);
                try
                {
                    var format = reader.WaveFormat;
                    if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32 || format.Channels != 2 || format.SampleRate != 48000 || format.BlockAlign != 8 || reader.Length == 0 || reader.Length % format.BlockAlign != 0)
                        throw new InvalidDataException("The recording preview needs a standard stereo WAV snapshot.");
                    samples = reader.ToSampleProvider();
                }
                catch { reader.Dispose(); throw; }
            }
            public WaveFormat WaveFormat { get { return samples.WaveFormat; } }
            public int Read(float[] buffer, int offset, int count) { return samples.Read(buffer, offset, count); }
            public void Dispose() { reader.Dispose(); }
        }

        private sealed class PreviewRun
        {
            internal readonly ISoundboardRecordingPreviewPlayer Player;
            internal readonly TaskCompletionSource<bool> Stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Action<Exception> StoppedHandler;
            internal bool Started;
            internal volatile Exception Failure;
            internal PreviewRun(ISoundboardRecordingPreviewPlayer player) { Player = player; }
        }

        private sealed class WasapiRecordingPreviewPlayer : ISoundboardRecordingPreviewPlayer
        {
            private DeviceEnumerator devices;
            private MMDevice device;
            private SoundboardMixer mixer;
            private WasapiOut output;
            private bool disposed;
            public event Action<Exception> Stopped;

            internal WasapiRecordingPreviewPlayer(string path, string listeningOutputId, float volume)
            {
                try
                {
                    try
                    {
                        devices = new DeviceEnumerator();
                        device = string.IsNullOrEmpty(listeningOutputId)
                            ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                            : devices.GetDevice(listeningOutputId);
                        ValidateListeningOutput(device.DataFlow, device.State, device.FriendlyName, device.DeviceFriendlyName);
                    }
                    catch (System.Runtime.InteropServices.COMException error)
                    { throw new InvalidOperationException("Connect speakers or headphones, then choose them under Listen on to preview this recording.", error); }
                    mixer = new SoundboardMixer { Volume = volume };
                    mixer.Add(OpenPreviewClip(path));
                    output = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
                    output.Init(new SampleToWaveProvider(mixer));
                    output.PlaybackStopped += OutputStopped;
                }
                catch { Dispose(); throw; }
            }

            private void OutputStopped(object sender, StoppedEventArgs stopped)
            { var handler = Stopped; if (handler != null) handler(stopped.Exception); }
            public void Play() { if (disposed) throw new ObjectDisposedException("Recording preview"); output.Play(); }
            public void Stop() { if (output != null) output.Stop(); }
            public void Dispose()
            {
                if (disposed) return; disposed = true;
                if (output != null)
                {
                    try { output.Stop(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
                    output.PlaybackStopped -= OutputStopped; Release(output);
                }
                if (mixer != null) Release(mixer);
                if (device != null) Release(device);
                if (devices != null) Release(devices);
            }
        }
    }
}
