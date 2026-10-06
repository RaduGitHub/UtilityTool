using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace UtilityTool.Backend
{
    internal sealed class AudioMirrorService : IDisposable
    {
        private readonly object gate = new object();
        private Thread worker;
        private CancellationTokenSource cancellation;
        private bool disposed;
        internal event Action<string> Stopped;
        internal bool IsRunning { get { lock (gate) return worker != null; } }
        internal Task StartAsync(string sourceId, IEnumerable<string> outputIds)
        {
            if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Choose a primary output.");
            string[] outputs = outputIds.Where(delegate(string id) { return !string.IsNullOrWhiteSpace(id); }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (outputs.Length == 0) throw new ArgumentException("Choose at least one additional output.");
            if (outputs.Any(delegate(string id) { return string.Equals(id, sourceId, StringComparison.OrdinalIgnoreCase); }))
                throw new ArgumentException("The primary output already plays Windows audio. Choose a different additional output.");
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("AudioMirrorService");
                if (worker != null) throw new InvalidOperationException("Stop the current audio mirror first.");
                cancellation = new CancellationTokenSource();
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var token = cancellation.Token;
                worker = new Thread(delegate() { Run(sourceId, outputs, token, started); }) { IsBackground = true, Name = "UtilityTool audio mirror" };
                worker.SetApartmentState(ApartmentState.MTA);
                worker.Start();
                return started.Task;
            }
        }
        internal Task StopAsync() { return Task.Run((Action)Stop); }
        private void Stop()
        {
            Thread current;
            lock (gate) { current = worker; if (cancellation != null) cancellation.Cancel(); }
            if (current != null && current != Thread.CurrentThread && !current.Join(3000))
                throw new TimeoutException("Audio is still stopping. Wait a moment before starting again.");
        }
        private void Run(string sourceId, string[] outputIds, CancellationToken token, TaskCompletionSource<bool> started)
        {
            IMMDeviceEnumerator enumerator = null; IAudioClient captureAudio = null; IAudioCaptureClient capture = null;
            IntPtr format = IntPtr.Zero;
            var outputs = new List<MirrorOutput>();
            string failure = null;
            Exception startupFailure = null;
            bool startupCancelled = false;
            try
            {
                enumerator = WindowsAudio.CreateEnumerator();
                captureAudio = WindowsAudio.Open(enumerator, sourceId);
                WindowsAudio.Check(captureAudio.GetMixFormat(out format));
                int blockAlignment = (ushort)Marshal.ReadInt16(format, 12);
                int sampleRate = Marshal.ReadInt32(format, 4);
                bool unsignedSilence = Marshal.ReadInt16(format) == 1 && Marshal.ReadInt16(format, 14) == 8;
                if (blockAlignment <= 0 || sampleRate <= 0) throw new InvalidOperationException("Unsupported audio format.");
                WindowsAudio.Check(captureAudio.Initialize(0, WindowsAudio.Loopback, 1000000, 0, format, IntPtr.Zero));
                object service; Guid iid = WindowsAudio.CaptureClientId;
                WindowsAudio.Check(captureAudio.GetService(ref iid, out service)); capture = (IAudioCaptureClient)service;
                foreach (string id in outputIds)
                {
                    token.ThrowIfCancellationRequested();
                    outputs.Add(new MirrorOutput(enumerator, id, format, blockAlignment, sampleRate));
                }
                WindowsAudio.Check(captureAudio.Start());
                foreach (var output in outputs) WindowsAudio.Check(output.Client.Start());
                token.ThrowIfCancellationRequested();
                started.TrySetResult(true);
                byte[] packet = new byte[Math.Max(blockAlignment, sampleRate / 10 * blockAlignment)];
                while (!token.IsCancellationRequested)
                {
                    uint packetFrames; WindowsAudio.Check(capture.GetNextPacketSize(out packetFrames));
                    while (packetFrames > 0 && !token.IsCancellationRequested)
                    {
                        IntPtr data; uint frames, flags; ulong position, timestamp;
                        WindowsAudio.Check(capture.GetBuffer(out data, out frames, out flags, out position, out timestamp));
                        try
                        {
                            int length = checked((int)frames * blockAlignment);
                            if (length > packet.Length) packet = new byte[length];
                            if ((flags & 2) != 0)
                            {
                                Array.Clear(packet, 0, length);
                                if (unsignedSilence) for (int i = 0; i < length; i++) packet[i] = 128;
                            }
                            else Marshal.Copy(data, packet, 0, length);
                            foreach (var output in outputs) output.Queue.Write(packet, length);
                        }
                        finally { WindowsAudio.Check(capture.ReleaseBuffer(frames)); }
                        foreach (var output in outputs) output.Pump();
                        WindowsAudio.Check(capture.GetNextPacketSize(out packetFrames));
                    }
                    foreach (var output in outputs) output.Pump();
                    token.WaitHandle.WaitOne(5);
                }
            }
            catch (OperationCanceledException) { startupCancelled = true; }
            catch (Exception error)
            {
                failure = "Audio mirroring stopped: " + error.Message + " Check the devices, then refresh and try again.";
                startupFailure = new InvalidOperationException(failure, error);
            }
            finally
            {
                if (captureAudio != null) captureAudio.Stop();
                foreach (var output in outputs) output.Dispose();
                WindowsAudio.Release(capture); WindowsAudio.Release(captureAudio); WindowsAudio.Release(enumerator);
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                lock (gate) { worker = null; cancellation.Dispose(); cancellation = null; }
                var stopped = Stopped; if (stopped != null) stopped(failure);
                // Complete failed starts only after cleanup, so an immediate retry can safely open fresh clients.
                if (startupFailure != null) started.TrySetException(startupFailure);
                else if (startupCancelled) started.TrySetCanceled();
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            try { Stop(); } catch (TimeoutException) { } // The background worker still owns and cleans up its audio handles.
        }

        private sealed class MirrorOutput : IDisposable
        {
            internal IAudioClient Client;
            internal readonly AudioFrameBuffer Queue;
            private IAudioRenderClient render;
            private uint capacity;
            private readonly int alignment;
            private readonly byte[] scratch;
            internal MirrorOutput(IMMDeviceEnumerator enumerator, string id, IntPtr format, int blockAlignment, int sampleRate)
            {
                alignment = blockAlignment;
                try
                {
                    Client = WindowsAudio.Open(enumerator, id);
                    // Windows converts the source format to each device's channel layout and sample rate.
                    WindowsAudio.Check(Client.Initialize(0, WindowsAudio.ConvertPcm, 1000000, 0, format, IntPtr.Zero));
                    WindowsAudio.Check(Client.GetBufferSize(out capacity));
                    object service; Guid iid = WindowsAudio.RenderClientId;
                    WindowsAudio.Check(Client.GetService(ref iid, out service)); render = (IAudioRenderClient)service;
                    Queue = new AudioFrameBuffer(sampleRate / 2, alignment);
                    scratch = new byte[checked((int)capacity * alignment)];
                }
                catch { Dispose(); throw; }
            }
            internal void Pump()
            {
                uint padding; WindowsAudio.Check(Client.GetCurrentPadding(out padding));
                int frames = Math.Min(Queue.Frames, (int)(capacity - padding));
                if (frames <= 0) return;
                IntPtr buffer; WindowsAudio.Check(render.GetBuffer((uint)frames, out buffer));
                uint flags = 2; // Release silence if a managed copy fails after acquiring the native buffer.
                try
                {
                    Queue.Read(scratch, frames);
                    Marshal.Copy(scratch, 0, buffer, checked(frames * alignment)); flags = 0;
                }
                finally { WindowsAudio.Check(render.ReleaseBuffer((uint)frames, flags)); }
            }
            public void Dispose()
            {
                if (Client != null) Client.Stop();
                WindowsAudio.Release(render); WindowsAudio.Release(Client); render = null; Client = null;
            }
        }
    }
}