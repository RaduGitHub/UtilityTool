using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using DeviceEnumerator = NAudio.CoreAudioApi.MMDeviceEnumerator;

namespace UtilityTool.Backend
{
    internal sealed class SoundboardDevice
    {
        internal string Id { get; private set; }
        public string Name { get; private set; }
        internal SoundboardDevice(string id, string name) { Id = id; Name = name; }
        public override string ToString() { return Name; }
    }
    internal sealed class SoundboardDevices
    {
        internal readonly List<SoundboardDevice> Microphones = new List<SoundboardDevice>();
        internal readonly List<SoundboardDevice> VirtualOutputs = new List<SoundboardDevice>();
        internal static List<SoundboardDevice> ReadListeningOutputs()
        {
            var result = new List<SoundboardDevice>();
            using (var devices = new DeviceEnumerator())
            {
                string defaultName = "Unavailable";
                try { using (var device = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) defaultName = device.FriendlyName; } catch (System.Runtime.InteropServices.COMException) { }
                result.Add(new SoundboardDevice(null, "Windows default — " + defaultName));
                foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    using (device) if (!IsVirtualOutput(device.FriendlyName)) result.Add(new SoundboardDevice(device.ID, device.FriendlyName));
            }
            return result;
        }
        internal static bool IsVirtualOutput(string name)
        { string value = name.ToLowerInvariant(); return value.Contains("input") && (value.Contains("cable") || value.Contains("voicemeeter")); }
        private static bool IsVirtualMicrophone(string name)
        { string value = name.ToLowerInvariant(); return value.Contains("cable") || value.Contains("voicemeeter") || value.Contains("voicemod"); }
        internal static SoundboardDevices Read()
        {
            var result = new SoundboardDevices();
            using (var devices = new DeviceEnumerator())
            {
                string preferred = null;
                try { using (var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)) preferred = device.ID; } catch (System.Runtime.InteropServices.COMException) { }
                foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                    using (device) if (!IsVirtualMicrophone(device.FriendlyName)) result.Microphones.Add(new SoundboardDevice(device.ID, device.FriendlyName));
                result.Microphones.Sort((a, b) => a.Id == preferred ? -1 : b.Id == preferred ? 1 : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    using (device) if (IsVirtualOutput(device.FriendlyName)) result.VirtualOutputs.Add(new SoundboardDevice(device.ID, device.FriendlyName));
            }
            return result;
        }
    }
    internal interface ISoundboardSession : IDisposable
    {
        event Action<string> Failed;
        event Action<int> PlaybackFinished;
        string LocalOutputName { get; }
        float LocalPeak { get; }
        void Play(string path, int playbackId);
        void TestLocalOutput(int playbackId);
        void SetVolume(float volume);
        void StopClips();
    }
    internal sealed class SoundboardAudioService : IDisposable
    {
        internal const float DefaultVolume = 0.25f;
        private readonly object sync = new object();
        private readonly Func<string, string, string, ISoundboardSession> factory;
        private ISoundboardSession session;
        private string listeningOutputId;
        private bool disposed, routing, playing;
        private float volume = DefaultVolume;
        private int playbackId;
        internal bool IsRouting { get { lock (sync) return routing; } }
        internal string LocalOutputName { get { lock (sync) return session == null ? null : session.LocalOutputName; } }
        internal float LocalPeak { get { lock (sync) return session == null ? 0 : session.LocalPeak; } }
        internal bool IsPlaying { get { lock (sync) return playing; } }
        internal event Action<string> Stopped;
        internal event Action PlaybackFinished;
        internal SoundboardAudioService(Func<string, string, string, ISoundboardSession> factory = null)
        { this.factory = factory ?? ((mic, output, listening) => new WasapiSoundboardSession(mic, output, listening)); }
        internal Task PlayAsync(string path) { return Task.Run(() => { lock (sync) { CheckAlive(); EnsureLocalSession(); playing = true; try { session.Play(path, ++playbackId); } catch { playing = false; throw; } } }); }
        internal Task TestLocalOutputAsync() { return Task.Run(() => { lock (sync) { CheckAlive(); EnsureLocalSession(); playing = true; try { session.TestLocalOutput(++playbackId); } catch { playing = false; throw; } } }); }
        internal void SetVolume(float value)
        {
            if (float.IsNaN(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException("value");
            lock (sync) { if (disposed) return; volume = value; if (session != null) session.SetVolume(value); }
        }
        private void EnsureLocalSession() { if (session == null) SetSession(factory(null, null, listeningOutputId), false); }
        internal Task SetListeningOutputAsync(string id)
        {
            return Task.Run(() => { lock (sync) { CheckAlive(); if (routing) throw new InvalidOperationException("Stop call audio before changing your listening output."); CloseSession(); listeningOutputId = id; } });
        }
        internal Task StartRoutingAsync(string microphone, string virtualOutput)
        {
            if (string.IsNullOrEmpty(microphone) || string.IsNullOrEmpty(virtualOutput)) throw new ArgumentException("Choose a microphone and virtual audio output.");
            return Task.Run(() => { lock (sync) { CheckAlive(); CloseSession(); SetSession(factory(microphone, virtualOutput, listeningOutputId), true); } });
        }
        internal Task StopRoutingAsync() { return Task.Run(() => { lock (sync) CloseSession(); }); }
        internal Task StopClipsAsync() { return Task.Run(() => { lock (sync) { playing = false; ++playbackId; if (session != null) session.StopClips(); } }); }
        private void SetSession(ISoundboardSession value, bool calls)
        {
            session = value; routing = calls;
            value.SetVolume(volume);
            value.PlaybackFinished += id => ThreadPool.QueueUserWorkItem(ignored =>
            {
                lock (sync) { if (!ReferenceEquals(session, value) || id != playbackId || !playing) return; playing = false; }
                var finished = PlaybackFinished; if (finished != null) finished();
            });
            value.Failed += error => ThreadPool.QueueUserWorkItem(ignored =>
            {
                lock (sync) { if (!ReferenceEquals(session, value)) return; CloseSession(); }
                var stopped = Stopped; if (stopped != null) stopped(error);
            });
        }
        private void CheckAlive() { if (disposed) throw new ObjectDisposedException("Soundboard"); }
        private void CloseSession() { var previous = session; session = null; routing = playing = false; ++playbackId; if (previous != null) previous.Dispose(); }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; CloseSession(); } }
    }

    internal sealed class WasapiSoundboardSession : ISoundboardSession
    {
        private DeviceEnumerator devices;
        private MMDevice localDevice, callDevice, micDevice;
        private WasapiOut localOutput, callOutput;
        private WasapiCapture capture;
        private SoundboardMixer localMixer, callMixer;
        private string localOutputName;
        private volatile bool disposed;
        private volatile int localGeneration;
        private volatile LocalPlaybackRun localRun;
        public event Action<string> Failed;
        public event Action<int> PlaybackFinished;
        // Device COM handles belong to the audio worker. UI status reads only this
        // cached string, rather than opening the device property store on the UI thread.
        public string LocalOutputName { get { return localOutputName; } }
        public float LocalPeak { get { return localMixer == null ? 0 : localMixer.MaximumPeak; } }
        internal bool LocalOutputRunning { get { return localOutput != null && localOutput.PlaybackState == PlaybackState.Playing; } }
        internal WasapiSoundboardSession(string microphone, string virtualOutput, string listeningOutput)
        {
            // Constructed on an MTA worker: capture shutdown never waits on the UI.
            try
            {
                devices = new DeviceEnumerator();
                if (virtualOutput != null)
                {
                    callDevice = devices.GetDevice(virtualOutput);
                    if (callDevice.DataFlow != DataFlow.Render || !SoundboardDevices.IsVirtualOutput(callDevice.FriendlyName)) throw new InvalidOperationException("Select a virtual audio output for calls.");
                    micDevice = devices.GetDevice(microphone);
                    if (micDevice.DataFlow != DataFlow.Capture) throw new InvalidOperationException("Select a microphone input.");
                    capture = new WasapiCapture(micDevice, true, 40);
                    var voice = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromMilliseconds(250), DiscardOnBufferOverflow = true, ReadFully = true };
                    callMixer = new SoundboardMixer(SoundboardMixer.Normalize(voice.ToSampleProvider()));
                    capture.DataAvailable += (sender, data) => { if (!disposed) voice.AddSamples(data.Buffer, 0, data.BytesRecorded); };
                    capture.RecordingStopped += (sender, stopped) => { if (stopped.Exception != null) Fail(stopped.Exception.Message); };
                    callOutput = OpenOutput(callDevice, callMixer, true);
                }
                // Local monitoring is optional if Windows has no playback endpoint.
                try { localDevice = listeningOutput == null ? devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : devices.GetDevice(listeningOutput); }
                catch (System.Runtime.InteropServices.COMException) { if (callOutput == null) throw new InvalidOperationException("Connect a sound output, then try again."); }
                if (localDevice != null && localDevice.DataFlow != DataFlow.Render) throw new InvalidOperationException("Choose speakers or headphones to hear your sounds.");
                if (localDevice != null && (callDevice == null || localDevice.ID != callDevice.ID))
                { localOutputName = localDevice.FriendlyName; localMixer = new SoundboardMixer(); localOutput = OpenOutput(localDevice, localMixer, false); }
                if (capture != null) capture.StartRecording();
            }
            catch { Dispose(); throw; }
        }
        private WasapiOut OpenOutput(MMDevice device, SoundboardMixer mixer, bool continuous)
        {
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 60);
            try
            {
                output.Init(new SampleToWaveProvider(mixer));
                output.PlaybackStopped += (sender, stopped) => { if (stopped.Exception != null) Fail(stopped.Exception.Message); };
                if (!continuous) output.PlaybackStopped += LocalPlaybackStopped;
                if (continuous) output.Play(); return output;
            }
            catch { output.Dispose(); throw; }
        }
        private void Fail(string message) { var handler = Failed; if (!disposed && handler != null) handler("Audio stopped: " + message); }
        public void Play(string path, int playbackId)
        {
            StopLocal();
            if (callMixer != null) callMixer.StopClips();
            SoundboardClip local = null, call = null;
            try
            {
                if (localMixer != null) local = new SoundboardClip(path);
                if (callMixer != null) call = new SoundboardClip(path, localMixer == null ? (Action)(() => { if (disposed) return; var finished = PlaybackFinished; if (finished != null) finished(playbackId); }) : null);
                if (local != null) { localMixer.Add(local); local = null; }
                if (call != null) { callMixer.Add(call); call = null; }
                StartLocal(playbackId);
            }
            finally { if (local != null) local.Dispose(); if (call != null) call.Dispose(); }
        }
        private void StopLocal()
        {
            ++localGeneration;
            if (localOutput != null)
            {
                // Join/flush before adding another clip, including during the
                // last-buffer drain, so a rapid replay cannot get lost at EOF.
                localOutput.Stop();
                var run = localRun;
                // WASAPI changes its state before the stop callback. Waiting for
                // that callback also covers the small Stop/Reset/event gap.
                if (run != null) run.Stopped.Task.GetAwaiter().GetResult();
                localRun = null;
            }
            if (localMixer != null) localMixer.StopClips();
        }
        private void StartLocal(int playbackId)
        {
            if (localOutput == null) return;
            var run = new LocalPlaybackRun(localGeneration, playbackId); localRun = run;
            try { localOutput.Play(); }
            catch { run.Stopped.TrySetResult(true); localRun = null; throw; }
        }
        private void LocalPlaybackStopped(object sender, StoppedEventArgs stopped)
        {
            var run = localRun; if (run == null) return;
            try
            {
                if (disposed || run.Generation != localGeneration || stopped.Exception != null) return;
                var finished = PlaybackFinished; if (finished != null) finished(run.Id);
            }
            finally { run.Stopped.TrySetResult(true); }
        }
        private sealed class LocalPlaybackRun
        {
            internal readonly int Generation, Id;
            internal readonly TaskCompletionSource<bool> Stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal LocalPlaybackRun(int generation, int id) { Generation = generation; Id = id; }
        }
        public void StopClips() { StopLocal(); if (callMixer != null) callMixer.StopClips(); }
        public void SetVolume(float volume) { if (localMixer != null) localMixer.Volume = volume; if (callMixer != null) callMixer.Volume = volume; }
        public void TestLocalOutput(int playbackId)
        {
            if (localMixer == null) throw new InvalidOperationException("Choose speakers or headphones under Listen on to hear the test tone.");
            if (localDevice.AudioEndpointVolume.Mute || localDevice.AudioEndpointVolume.MasterVolumeLevelScalar == 0) throw new InvalidOperationException("This output is muted or its volume is zero. Turn up its volume in Windows, then try again.");
            StopLocal();
            localMixer.Add(new SoundboardTestTone());
            StartLocal(playbackId);
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            // A disconnected device must not prevent the remaining handles closing.
            try { StopLocal(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); }
            if (capture != null) { try { capture.StopRecording(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); } Release(capture); }
            Release(localOutput); Release(callOutput); Release(localMixer); Release(callMixer);
            Release(micDevice); Release(callDevice); Release(localDevice); Release(devices);
        }
        private static void Release(IDisposable resource)
        { if (resource == null) return; try { resource.Dispose(); } catch (Exception error) { System.Diagnostics.Trace.WriteLine(error); } }
    }
}
