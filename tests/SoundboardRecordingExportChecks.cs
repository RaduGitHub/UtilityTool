using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using UtilityTool.Backend;

internal static class SoundboardRecordingExportChecks
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string root = Path.Combine(Path.GetFullPath(output), "recording-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); string temporary = Path.Combine(root, "temporary");
        var formats = new[] { new WaveFormat(44100, 16, 1), WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new WaveFormatExtensible(48000, 32, 2), new WaveFormatExtensible(44100, 24, 2), new WaveFormat(48000, 24, 2), new WaveFormat(22050, 32, 1), WaveFormat.CreateCustomFormat(WaveFormatEncoding.IeeeFloat, 32000, 1, 256000, 8, 64), new WaveFormat(16000, 8, 1) };
        string primary = null, firstPreview = null; byte[] primaryBytes = null;
        for (int index = 0; index < formats.Length; index++)
        {
            var format = formats[index]; string wave = Path.Combine(root, "source-" + index + ".wav"); CreateWave(wave, format, 3);
            byte[] sourceBytes = File.ReadAllBytes(wave);
            check(Math.Abs((SoundboardRecordingExport.GetDuration(wave) - TimeSpan.FromSeconds(3)).TotalSeconds) < 1.0 / format.SampleRate, "Recording duration reads complete native frames for " + format);
            string preview = SoundboardRecordingExport.CreatePreviewAsync(wave, TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.8), temporary).GetAwaiter().GetResult();
            var decoded = Decode(preview);
            check(decoded.Rate == 48000 && decoded.Channels == 2 && Math.Abs(decoded.Duration - 0.6) < 0.075 && decoded.Samples.Any(value => Math.Abs(value) > 0.01), "A selected 0.6-second " + format + " recording encodes as playable 48 kHz stereo MP3 without adding a long tail");
            double wanted = ToneEnergy(decoded, 0, 880), before = ToneEnergy(decoded, 0, 440), after = ToneEnergy(decoded, 0, 1320);
            check(wanted > 0.005 && wanted > before * 20 && wanted > after * 20, "The MP3 contains the selected middle section rather than audio before or after the trim for " + format);
            check(File.ReadAllBytes(wave).SequenceEqual(sourceBytes) && Path.GetDirectoryName(preview) == Path.GetFullPath(temporary) && Path.GetFileName(preview).StartsWith("recording-preview-", StringComparison.Ordinal), "Preview owns only a generated file in the requested folder and preserves the raw recording for " + format);
            if (format.Channels == 1)
                check(ToneEnergy(decoded, 1, 880) > 0.005 && ChannelDifference(decoded) < 0.005, "Mono recording content is duplicated consistently to both playback channels");
            else check(ToneEnergy(decoded, 1, 1100) > 0.005, "Stereo recording keeps the right-channel source content");
            if (index == 0) { primary = wave; primaryBytes = sourceBytes; firstPreview = preview; }
            else File.Delete(preview);
        }

        using (var reader = new WaveFileReader(primary))
        {
            long startFrame = 44100, endFrame = 44110;
            var provider = new SoundboardRecordingExport.TrimmedWaveProvider(reader, reader.WaveFormat.AsStandardWaveFormat(), startFrame, endFrame);
            var bytes = Enumerable.Repeat((byte)0xcc, 18).ToArray(); var selected = new List<byte>(); int read;
            while ((read = provider.Read(bytes, 3, 13)) > 0) { check(read % reader.WaveFormat.BlockAlign == 0, "Native trim emits complete sample frames with unaligned caller buffer lengths"); selected.AddRange(bytes.Skip(3).Take(read)); }
            check(selected.Count == (endFrame - startFrame) * reader.WaveFormat.BlockAlign && bytes.Skip(3).Take(13).All(value => value == 0) && bytes.Take(3).All(value => value == 0xcc) && bytes.Skip(16).All(value => value == 0xcc), "The bounded trim stops at its exact final frame, clears EOF buffers and preserves bytes outside the caller's range");
            reader.Position = startFrame * reader.WaveFormat.BlockAlign; var expected = new byte[selected.Count]; reader.Read(expected, 0, expected.Length);
            check(selected.SequenceEqual(expected), "Frame-aligned seeking retains the exact selected raw WAV bytes");
        }
        string precise = Path.Combine(root, "three-frames.wav"); CreateWave(precise, new WaveFormat(48000, 16, 1), 3.0 / 48000);
        check(SoundboardRecordingExport.GetDuration(precise).Ticks == 625, "Sub-millisecond recordings retain frame precision rather than rounding duration to zero");

        string wavePreview = SoundboardRecordingExport.CreateWavePreviewAsync(primary, TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.8), temporary).GetAwaiter().GetResult();
        using (var reader = new WaveFileReader(wavePreview))
        {
            var samples = reader.ToSampleProvider(); var buffer = new float[4096]; int read, total = 0; float peak = 0;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0) { total += read; for (int index = 0; index < read; index++) peak = Math.Max(peak, Math.Abs(buffer[index])); }
            check(Path.GetExtension(wavePreview) == ".wav" && samples.WaveFormat.SampleRate == 48000 && samples.WaveFormat.Channels == 2 && Math.Abs(total / 96000.0 - 0.6) < 0.002 && peak > 0.01f,
                "Unsaved recording preview is a directly readable, exact trimmed floating-point WAV without MP3 codec startup");
        }
        File.Delete(wavePreview);
        check(File.ReadAllBytes(primary).SequenceEqual(primaryBytes), "Preparing a fast WAV preview leaves the raw recording unchanged");

        foreach (var range in new[] { new[] { -0.1, 1.0 }, new[] { 1.0, 1.0 }, new[] { 2.0, 1.0 }, new[] { 0.0, 3.005 }, new[] { 3.0, 3.0005 }, new[] { 0.0, 0.0 } })
        {
            int count = Directory.GetFiles(temporary).Length; bool failed = false;
            try { SoundboardRecordingExport.CreatePreviewAsync(primary, TimeSpan.FromTicks((long)Math.Round(range[0] * TimeSpan.TicksPerSecond)), TimeSpan.FromTicks((long)Math.Round(range[1] * TimeSpan.TicksPerSecond)), temporary).GetAwaiter().GetResult(); } catch (ArgumentException) { failed = true; }
            check(failed && Directory.GetFiles(temporary).Length == count && File.ReadAllBytes(primary).SequenceEqual(primaryBytes), "Invalid recording trim " + range[0] + "–" + range[1] + " seconds leaves source and temporary files unchanged");
        }
        string roundingPreview = SoundboardRecordingExport.CreatePreviewAsync(primary, TimeSpan.FromSeconds(2.7), TimeSpan.FromTicks(TimeSpan.FromSeconds(3).Ticks + 5000), temporary).GetAwaiter().GetResult();
        check(Math.Abs(Decode(roundingPreview).Duration - 0.3) < 0.075, "A half-millisecond UI overrun is clamped to the source's final frame"); File.Delete(roundingPreview);

        var store = new SoundboardStore(Path.Combine(root, "library"));
        check(store.Load().Count == 0 && !Directory.Exists(store.Root), "Preview encoding creates no saved sound library");
        var existing = store.Import("Existing", firstPreview); File.Delete(firstPreview);
        check(!File.Exists(firstPreview) && Directory.GetFiles(temporary).Length == 0 && File.Exists(existing.AudioPath), "Explicit preview cancellation cleanup removes only the temporary MP3 and preserves a separately imported copy");
        string catalog = Path.Combine(store.Root, "sounds.xml"); byte[] previousCatalog = File.ReadAllBytes(catalog), existingBytes = File.ReadAllBytes(existing.AudioPath);
        string invalidImage = Path.Combine(root, "invalid.png"); File.WriteAllText(invalidImage, "not an image");
        bool importFailed = false;
        try { SoundboardRecordingExport.SaveAsync(store, primary, "Failed image", invalidImage, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), temporary).GetAwaiter().GetResult(); } catch (Exception error) { if (!(error is ArgumentException) && !(error is OutOfMemoryException)) throw; importFailed = true; }
        check(importFailed && Directory.GetFiles(temporary).Length == 0 && File.ReadAllBytes(catalog).SequenceEqual(previousCatalog) && File.ReadAllBytes(existing.AudioPath).SequenceEqual(existingBytes), "Failed recording import removes its encoded MP3 and preserves the existing library byte-for-byte");
        importFailed = false;
        using (var blocked = new FileStream(catalog, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try { SoundboardRecordingExport.SaveAsync(store, primary, "Blocked commit", null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), temporary).GetAwaiter().GetResult(); } catch (IOException) { importFailed = true; }
        }
        check(importFailed && Directory.GetFiles(temporary).Length == 0 && store.Load().Count == 1 && File.ReadAllBytes(catalog).SequenceEqual(previousCatalog) && Directory.GetDirectories(Path.Combine(store.Root, "assets")).Length == 1, "An import commit failure cleans both generated MP3 and partial owned assets while keeping the prior library");

        var saved = SoundboardRecordingExport.SaveAsync(store, primary, "  Recorded middle  ", null, TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.8), temporary).GetAwaiter().GetResult();
        check(saved.Name == "Recorded middle" && store.Load().Count == 2 && File.Exists(saved.AudioPath) && Directory.GetFiles(temporary).Length == 0 && Math.Abs(Decode(saved.AudioPath).Duration - 0.6) < 0.075, "Saving imports the selected MP3 into the normal sound library and removes only its generated export file");
        check(File.ReadAllBytes(primary).SequenceEqual(primaryBytes) && File.ReadAllBytes(existing.AudioPath).SequenceEqual(existingBytes), "Preview, validation errors and saving leave the original WAV and previously saved audio untouched");

        string invalidWave = Path.Combine(root, "invalid.wav"); File.WriteAllText(invalidWave, "not a WAV");
        bool rejected = false; try { SoundboardRecordingExport.CreatePreviewAsync(invalidWave, TimeSpan.Zero, TimeSpan.FromSeconds(1), temporary).GetAwaiter().GetResult(); } catch (FormatException) { rejected = true; }
        check(rejected && Directory.GetFiles(temporary).Length == 0, "An invalid WAV creates no partial exported MP3");
        string multichannel = Path.Combine(root, "surround.wav"); CreateWave(multichannel, new WaveFormat(48000, 16, 3), 0.2);
        rejected = false; try { SoundboardRecordingExport.CreatePreviewAsync(multichannel, TimeSpan.Zero, TimeSpan.FromSeconds(0.1), temporary).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
        check(rejected && Directory.GetFiles(temporary).Length == 0, "Unsupported surround recording formats fail cleanly instead of mixing channel data incorrectly");
        rejected = false; try { SoundboardRecordingExport.CreatePreviewAsync(primary, TimeSpan.Zero, TimeSpan.FromSeconds(1), " ").GetAwaiter().GetResult(); } catch (ArgumentException) { rejected = true; }
        check(rejected && Directory.GetFiles(temporary).Length == 0, "Missing temporary folder is rejected without exporting into the working directory");
    }

    private sealed class Decoded
    {
        internal int Rate, Channels;
        internal float[] Samples;
        internal double Duration { get { return Samples.Length / (double)(Rate * Channels); } }
    }
    private static Decoded Decode(string path)
    {
        using (var reader = new MediaFoundationReader(path))
        {
            var samples = reader.ToSampleProvider(); var result = new List<float>(); var buffer = new float[4096]; int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0) result.AddRange(buffer.Take(read));
            return new Decoded { Rate = samples.WaveFormat.SampleRate, Channels = samples.WaveFormat.Channels, Samples = result.ToArray() };
        }
    }
    private static double ToneEnergy(Decoded decoded, int channel, double frequency)
    {
        int frames = decoded.Samples.Length / decoded.Channels, first = frames / 4, last = frames * 3 / 4; double real = 0, imaginary = 0;
        for (int frame = first; frame < last; frame++) { double phase = 2 * Math.PI * frequency * frame / decoded.Rate, value = decoded.Samples[frame * decoded.Channels + channel]; real += value * Math.Cos(phase); imaginary += value * Math.Sin(phase); }
        return Math.Sqrt(real * real + imaginary * imaginary) / (last - first);
    }
    private static double ChannelDifference(Decoded decoded)
    { double sum = 0; for (int i = 0; i < decoded.Samples.Length; i += 2) sum += Math.Abs(decoded.Samples[i] - decoded.Samples[i + 1]); return sum / (decoded.Samples.Length / 2); }
    private static void CreateWave(string path, WaveFormat format, double seconds)
    {
        WaveFormat standard = format.AsStandardWaveFormat(); int total = (int)Math.Round(seconds * format.SampleRate), sampleBytes = format.BitsPerSample / 8;
        using (var writer = new WaveFileWriter(path, format))
        {
            var block = new byte[1024 * format.BlockAlign];
            for (int start = 0; start < total; start += 1024)
            {
                int frames = Math.Min(1024, total - start);
                for (int frame = 0; frame < frames; frame++) for (int channel = 0; channel < format.Channels; channel++)
                {
                    int sample = start + frame, section = sample / format.SampleRate; double frequency = channel == 0 ? section == 0 ? 440 : section == 1 ? 880 : 1320 : section == 0 ? 660 : section == 1 ? 1100 : 1540;
                    double value = 0.18 * Math.Sin(2 * Math.PI * frequency * sample / format.SampleRate); int offset = frame * format.BlockAlign + channel * sampleBytes; byte[] bytes;
                    if (standard.Encoding == WaveFormatEncoding.IeeeFloat) bytes = sampleBytes == 8 ? BitConverter.GetBytes(value) : BitConverter.GetBytes((float)value);
                    else if (sampleBytes == 1) bytes = new[] { (byte)Math.Round(128 + value * 127) };
                    else if (sampleBytes == 2) bytes = BitConverter.GetBytes((short)Math.Round(value * 32767));
                    else if (sampleBytes == 3) bytes = BitConverter.GetBytes((int)Math.Round(value * 8388607));
                    else bytes = BitConverter.GetBytes((int)Math.Round(value * int.MaxValue));
                    Buffer.BlockCopy(bytes, 0, block, offset, sampleBytes);
                }
                writer.Write(block, 0, frames * format.BlockAlign);
            }
        }
    }
}
