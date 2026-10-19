using System;
using System.IO;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace UtilityTool.Backend
{
    internal static class SoundboardRecordingExport
    {
        internal static TimeSpan GetDuration(string wavePath)
        {
            using (var reader = OpenRecording(wavePath))
            { WaveFormat format = StandardFormat(reader); return FrameDuration(reader.Length / format.BlockAlign, format.SampleRate); }
        }

        internal static Task<string> CreatePreviewAsync(string wavePath, TimeSpan start, TimeSpan end, string temporaryFolder)
        { return Task.Run(() => EncodeSelection(wavePath, start, end, temporaryFolder)); }

        internal static Task<string> CreateWavePreviewAsync(string wavePath, TimeSpan start, TimeSpan end, string temporaryFolder)
        { return Task.Run(() => EncodeSelection(wavePath, start, end, temporaryFolder, false)); }

        internal static async Task<SoundboardSound> SaveAsync(SoundboardStore store, string wavePath, string name, string image, TimeSpan start, TimeSpan end, string temporaryFolder, bool reduceImageSize = false)
        {
            if (store == null) throw new ArgumentNullException("store");
            string generated = await CreatePreviewAsync(wavePath, start, end, temporaryFolder).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    // Recorded audio is already 128 kbps. Reduce only artwork
                    // here, so saving does not apply a second lossy MP3 encode.
                    using (var prepared = reduceImageSize ? SoundboardAssetOptimizer.Prepare(null, image) : null)
                        return store.Import(name, generated, prepared == null ? image : prepared.ImagePath);
                }).ConfigureAwait(false);
            }
            finally { if (File.Exists(generated)) File.Delete(generated); }
        }

        private static string EncodeSelection(string wavePath, TimeSpan start, TimeSpan end, string temporaryFolder, bool mp3 = true)
        {
            if (string.IsNullOrWhiteSpace(temporaryFolder)) throw new ArgumentException("Choose a temporary recording folder.", "temporaryFolder");
            string folder = Path.GetFullPath(temporaryFolder);
            using (var reader = OpenRecording(wavePath))
            {
                WaveFormat format = StandardFormat(reader); long totalFrames = reader.Length / format.BlockAlign;
                TimeSpan duration = FrameDuration(totalFrames, format.SampleRate);
                if (start < TimeSpan.Zero || end <= start || start >= duration || end.Ticks > duration.Ticks + TimeSpan.TicksPerMillisecond)
                    throw new ArgumentOutOfRangeException("end", "Select a start and end within the recording, with the end after the start.");
                if (end > duration) end = duration;
                long first = Math.Max(0, Math.Min(totalFrames, (long)Math.Round(start.TotalSeconds * format.SampleRate)));
                long last = Math.Max(0, Math.Min(totalFrames, (long)Math.Round(end.TotalSeconds * format.SampleRate)));
                if (last <= first) throw new ArgumentException("Select at least one complete audio frame.");
                var trimmed = new TrimmedWaveProvider(reader, format, first, last);
                ISampleProvider samples = new EdgeFadeProvider(trimmed.ToSampleProvider(), last - first);
                if (format.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
                if (format.SampleRate != 48000) samples = new WdlResamplingSampleProvider(samples, 48000);
                Directory.CreateDirectory(folder);
                string generated = Path.Combine(folder, "recording-preview-" + Guid.NewGuid().ToString("N") + (mp3 ? ".mp3" : ".wav"));
                try
                {
                    if (mp3)
                    {
                        NAudio.MediaFoundation.MediaFoundationApi.Startup();
                        MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(samples), generated, 128000);
                    }
                    else WaveFileWriter.CreateWaveFile(generated, new SampleToWaveProvider(samples));
                    if (!File.Exists(generated) || new FileInfo(generated).Length == 0) throw new InvalidDataException("The selected recording could not be encoded.");
                    return generated;
                }
                catch { if (File.Exists(generated)) File.Delete(generated); throw; }
            }
        }

        private static WaveFileReader OpenRecording(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("The temporary recording could not be found.", path);
            return new WaveFileReader(Path.GetFullPath(path));
        }
        private static WaveFormat StandardFormat(WaveFileReader reader)
        {
            WaveFormat format = reader.WaveFormat.AsStandardWaveFormat();
            // WaveFileReader represents an extensible fmt chunk as ExtraData,
            // rather than the marshalled WaveFormatExtensible used by WASAPI.
            if (format.Encoding == WaveFormatEncoding.Extensible)
            {
                var extra = reader.WaveFormat as WaveFormatExtraData;
                if (extra != null && extra.ExtraSize >= 22)
                {
                    var guidBytes = new byte[16]; Buffer.BlockCopy(extra.ExtraData, 6, guidBytes, 0, guidBytes.Length); var subtype = new Guid(guidBytes);
                    if (subtype == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_PCM)
                        format = new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
                    else if (subtype == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT)
                        format = WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, format.SampleRate, format.Channels, format.AverageBytesPerSecond, format.BlockAlign, format.BitsPerSample);
                }
            }
            bool pcm = format.Encoding == WaveFormatEncoding.Pcm && (format.BitsPerSample == 8 || format.BitsPerSample == 16 || format.BitsPerSample == 24 || format.BitsPerSample == 32);
            bool floating = format.Encoding == WaveFormatEncoding.IeeeFloat && (format.BitsPerSample == 32 || format.BitsPerSample == 64);
            if ((!pcm && !floating) || (format.Channels != 1 && format.Channels != 2) || format.SampleRate <= 0 || format.BlockAlign <= 0 || format.BlockAlign != format.Channels * (format.BitsPerSample / 8) || format.BlockAlign != reader.WaveFormat.BlockAlign || reader.Length % format.BlockAlign != 0)
                throw new InvalidDataException("This recording needs mono or stereo PCM or floating-point WAV audio with complete frames.");
            return format;
        }
        private static TimeSpan FrameDuration(long frames, int rate)
        { return TimeSpan.FromTicks((long)Math.Round(frames * (double)TimeSpan.TicksPerSecond / rate)); }

        // No whole-recording buffer: reads are bounded by the selected native
        // frames, and the standard format also handles extensible WAV headers.
        internal sealed class TrimmedWaveProvider : IWaveProvider
        {
            private readonly WaveFileReader reader;
            private long remaining;
            public WaveFormat WaveFormat { get; private set; }
            internal TrimmedWaveProvider(WaveFileReader reader, WaveFormat standard, long firstFrame, long lastFrame)
            { this.reader = reader; WaveFormat = standard; reader.Position = firstFrame * standard.BlockAlign; remaining = (lastFrame - firstFrame) * standard.BlockAlign; }
            public int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null) throw new ArgumentNullException("buffer");
                if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException("count");
                Array.Clear(buffer, offset, count);
                int requested = (int)Math.Min(remaining, count - count % WaveFormat.BlockAlign);
                if (requested == 0) return 0;
                int read = reader.Read(buffer, offset, requested);
                if (read == 0 || read % WaveFormat.BlockAlign != 0) throw new InvalidDataException("The recording ended before its declared audio frames.");
                remaining -= read; return read;
            }
        }
        private sealed class EdgeFadeProvider : ISampleProvider
        {
            private readonly ISampleProvider source;
            private readonly long totalFrames, fadeFrames;
            private long samplesRead;
            public WaveFormat WaveFormat { get { return source.WaveFormat; } }
            internal EdgeFadeProvider(ISampleProvider source, long totalFrames)
            { this.source = source; this.totalFrames = totalFrames; fadeFrames = Math.Min(totalFrames / 2, source.WaveFormat.SampleRate / 200); }
            public int Read(float[] buffer, int offset, int count)
            {
                int read = source.Read(buffer, offset, count);
                for (int i = 0; i < read; i++)
                {
                    long frame = (samplesRead + i) / WaveFormat.Channels;
                    float gain = fadeFrames == 0 ? 1 : (float)Math.Min(1.0, Math.Min(frame, totalFrames - frame - 1) / (double)fadeFrames);
                    float value = buffer[offset + i];
                    buffer[offset + i] = float.IsNaN(value) || float.IsInfinity(value) ? 0 : Math.Max(-1, Math.Min(1, value)) * gain;
                }
                samplesRead += read; return read;
            }
        }
    }
}
