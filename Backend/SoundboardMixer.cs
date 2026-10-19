using System;
using System.Collections.Generic;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace UtilityTool.Backend
{
    // Local playback ends at EOF. The call mixer stays alive for its microphone.
    internal sealed class SoundboardMixer : ISampleProvider, IDisposable
    {
        private readonly object sync = new object();
        private readonly List<ISampleProvider> clips = new List<ISampleProvider>();
        private readonly ISampleProvider microphone;
        private readonly bool keepAlive;
        private float[] scratch = new float[0];
        private bool disposed;
        private volatile float maximumPeak;
        internal float MaximumPeak { get { return maximumPeak; } }
        internal volatile float Volume = SoundboardAudioService.DefaultVolume;
        public WaveFormat WaveFormat { get; private set; }
        internal SoundboardMixer(ISampleProvider microphone = null, bool keepAlive = false)
        { WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); this.microphone = microphone; this.keepAlive = keepAlive || microphone != null; }
        internal static ISampleProvider Normalize(ISampleProvider input)
        {
            if (input.WaveFormat.Channels == 1) input = new MonoToStereoSampleProvider(input);
            if (input.WaveFormat.Channels != 2) throw new InvalidOperationException("This sound needs a mono or stereo audio stream.");
            if (input.WaveFormat.SampleRate != 48000) input = new WdlResamplingSampleProvider(input, 48000);
            return input;
        }
        internal void Add(ISampleProvider clip)
        {
            lock (sync)
            {
                if (disposed) { Release(clip); throw new ObjectDisposedException("Soundboard"); }
                if (clips.Count == 16) { Release(clips[0]); clips.RemoveAt(0); }
                clips.Add(clip);
            }
        }
        internal void StopClips() { lock (sync) { foreach (var clip in clips) Release(clip); clips.Clear(); } }
        private static void Release(ISampleProvider clip) { var disposable = clip as IDisposable; if (disposable != null) disposable.Dispose(); }
        public int Read(float[] buffer, int offset, int count)
        {
            lock (sync)
            {
                // NAudio can pass WaveBuffer.FloatBuffer, a float view backed by
                // a byte[]. Array.Clear sees the byte[] runtime type and clears
                // only a quarter of the samples. Use typed float writes instead.
                for (int i = offset; i < offset + count; i++) buffer[i] = 0f;
                if (disposed) return 0;
                if (scratch.Length < count) scratch = new float[count];
                int available = 0;
                if (microphone != null)
                {
                    int voice = microphone.Read(scratch, 0, count);
                    // Array.Copy likewise rejects the byte-backed float view.
                    for (int i = 0; i < voice; i++) buffer[offset + i] = scratch[i];
                    available = voice;
                }
                for (int c = clips.Count - 1; c >= 0; c--)
                {
                    int read = 0; bool ended = false;
                    // A short read can be a decoder/resampler chunk, not EOF.
                    // Keep its final samples and retire the source only at zero.
                    while (read < count)
                    {
                        // Keep offset zero for resamplers whose internal filters
                        // do not honor nonzero output offsets. Mix each chunk
                        // before a subsequent EOF flush can reuse this scratch.
                        int next = clips[c].Read(scratch, 0, count - read);
                        if (next == 0) { ended = true; break; }
                        for (int i = 0; i < next; i++) buffer[offset + read + i] += scratch[i] * Volume;
                        read += next;
                    }
                    available = Math.Max(available, read);
                    if (ended) { Release(clips[c]); clips.RemoveAt(c); }
                }
                for (int i = offset; i < offset + count; i++)
                { buffer[i] = Math.Max(-1f, Math.Min(1f, buffer[i])); float peak = Math.Abs(buffer[i]); if (peak > maximumPeak) maximumPeak = peak; }
                return keepAlive ? count : available;
            }
        }
        public void Dispose() { lock (sync) { if (disposed) return; disposed = true; StopClips(); } }
    }

    internal sealed class SoundboardClip : ISampleProvider, IDisposable
    {
        private readonly MediaFoundationReader reader;
        private readonly ISampleProvider samples;
        private Action onEof;
        private bool ended;
        internal SoundboardClip(string path, Action onEof = null)
        {
            this.onEof = onEof;
            reader = new MediaFoundationReader(path);
            try { samples = SoundboardMixer.Normalize(reader.ToSampleProvider()); }
            catch { reader.Dispose(); throw; }
        }
        public WaveFormat WaveFormat { get { return samples.WaveFormat; } }
        public int Read(float[] buffer, int offset, int count)
        {
            if (ended || count == 0) return 0;
            int read = samples.Read(buffer, offset, count);
            if (read == 0) { ended = true; var finished = onEof; onEof = null; if (finished != null) finished(); }
            return read;
        }
        public void Dispose() { ended = true; onEof = null; reader.Dispose(); }
    }

    // Short, quiet output test. This source is added only to the local mixer.
    internal sealed class SoundboardTestTone : ISampleProvider
    {
        private int position;
        private const int Frames = 19200;
        public WaveFormat WaveFormat { get { return WaveFormat.CreateIeeeFloatWaveFormat(48000, 2); } }
        public int Read(float[] buffer, int offset, int count)
        {
            int read = Math.Min(count, Frames * 2 - position);
            for (int i = 0; i < read; i++)
            {
                int frame = (position + i) / 2;
                double fade = Math.Min(1, Math.Min(frame, Frames - frame - 1) / 720.0);
                buffer[offset + i] = (float)(Math.Sin(2 * Math.PI * 660 * frame / 48000) * 0.1 * fade);
            }
            position += read; return read;
        }
    }
}
