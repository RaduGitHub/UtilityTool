using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using UtilityTool.Backend;

internal static class SoundboardAssetOptimizerChecks
{
    internal static void Run(Action<bool, string> check, string output)
    {
        string root = Path.Combine(output, "optimizer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string temporary = Path.Combine(root, "scratch"), largeImage = Path.Combine(root, "photo.bmp");
        using (var image = new Bitmap(3000, 2000, PixelFormat.Format24bppRgb))
        {
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[data.Stride]; uint value = 42;
                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width * 3; x++) { value = unchecked(value * 1664525 + 1013904223); row[x] = (byte)(value >> 24); }
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            finally { image.UnlockBits(data); }
            image.Save(largeImage, ImageFormat.Bmp);
        }
        byte[] originalImageHash = Hash(largeImage);
        string folder;
        using (var prepared = SoundboardAssetOptimizer.Prepare(null, largeImage, temporary))
        {
            folder = prepared.TemporaryDirectory;
            using (var resized = Image.FromFile(prepared.ImagePath))
            {
                check(new FileInfo(largeImage).Length > 15 * 1024 * 1024 && prepared.ImageOptimized && prepared.ImageReduced && resized.Width == 512 && resized.Height == 341, "An actual image above the old 15 MiB limit becomes a small 512px thumbnail with its aspect ratio preserved");
                check(new FileInfo(prepared.ImagePath).Length < 15 * 1024 * 1024 && Path.GetExtension(prepared.ImagePath) == ".jpg" && resized.RawFormat.Guid == ImageFormat.Jpeg.Guid, "Opaque artwork chooses the smaller valid JPEG encoding");
            }
            check(Hash(largeImage).SequenceEqual(originalImageHash), "Image optimization leaves the original file unchanged");
        }
        check(!Directory.Exists(folder), "Disposing prepared artwork removes its tracked scratch files and empty GUID folder");

        string transparent = Path.Combine(root, "transparent.png");
        using (var image = new Bitmap(1024, 768, PixelFormat.Format32bppArgb))
        {
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.Clear(Color.Transparent);
                using (var colour = new SolidBrush(Color.FromArgb(96, 40, 80, 160))) graphics.FillRectangle(colour, 100, 100, 800, 500);
            }
            image.Save(transparent, ImageFormat.Png);
        }
        using (var prepared = SoundboardAssetOptimizer.Prepare(null, transparent, temporary))
        using (var resized = new Bitmap(prepared.ImagePath))
        {
            Color centre = resized.GetPixel(256, 192);
            check(Path.GetExtension(prepared.ImagePath) == ".png" && resized.Width == 512 && resized.Height == 384 && resized.GetPixel(0, 0).A == 0 && Math.Abs(centre.A - 96) <= 1, "Transparent artwork remains PNG with transparent and semitransparent pixels after resizing");
            check(Math.Abs(centre.R - 40) <= 3 && Math.Abs(centre.G - 80) <= 3 && Math.Abs(centre.B - 160) <= 3, "PNG resizing retains the visible RGB colour within alpha-rounding precision");
        }
        string animated = Path.Combine(root, "animated.gif"); MakeAnimatedGif(animated);
        using (var original = Image.FromFile(animated)) check(original.GetFrameCount(FrameDimension.Time) == 2, "The owned animation fixture has two distinct frames");
        using (var prepared = SoundboardAssetOptimizer.Prepare(null, animated, temporary))
        using (var resized = new Bitmap(prepared.ImagePath))
        {
            check(resized.GetPixel(0, 0).R > 240 && resized.GetPixel(0, 0).B < 10 && !resized.FrameDimensionsList.Contains(FrameDimension.Time.Guid), "Optimized animation keeps the first frame and drops later frames");
            check(!Encoding.ASCII.GetString(File.ReadAllBytes(prepared.ImagePath)).Contains("UTILITYTOOL_METADATA"), "Generated artwork strips source comments and metadata");
        }
        string smallImage = Path.Combine(root, "tiny.png");
        using (var image = new Bitmap(20, 10)) { image.Save(smallImage, ImageFormat.Png); }
        using (var prepared = SoundboardAssetOptimizer.Prepare(null, smallImage, temporary))
        using (var read = Image.FromFile(prepared.ImagePath)) check(read.Width == 20 && read.Height == 10, "Small artwork is never enlarged");
        string oriented = Path.Combine(root, "oriented.jpg"); MakeOrientedJpeg(oriented);
        using (var prepared = SoundboardAssetOptimizer.Prepare(null, oriented, temporary))
        using (var read = new Bitmap(prepared.ImagePath))
        {
            check(read.Width == 10 && read.Height == 20 && read.GetPixel(5, 3).R > 180 && read.GetPixel(5, 16).B > 180, "EXIF orientation is applied to the pixels before image metadata is stripped");
            check(!read.PropertyIdList.Contains(0x112) && !Encoding.ASCII.GetString(File.ReadAllBytes(prepared.ImagePath)).Contains("UTILITYTOOL_METADATA"), "The prepared portrait no longer depends on EXIF orientation or source comments");
        }

        string originalAudio = Path.Combine(root, "source-320.mp3"), compactAudio = Path.Combine(root, "source-128.mp3"), tinyAudio = Path.Combine(root, "short-320.mp3");
        EncodeTone(originalAudio, 320000, 8); EncodeTone(compactAudio, 128000, 10); EncodeTone(tinyAudio, 320000, 0.3);
        byte[] originalAudioHash = Hash(originalAudio);
        var before = Analyze(originalAudio);
        using (var prepared = SoundboardAssetOptimizer.Prepare(originalAudio, null, temporary))
        {
            folder = prepared.TemporaryDirectory; var after = Analyze(prepared.AudioPath);
            check(new FileInfo(originalAudio).Length >= SoundboardAssetOptimizer.MinimumAudioBytes && prepared.AudioReduced && new FileInfo(prepared.AudioPath).Length <= new FileInfo(originalAudio).Length * 0.90, "A 320 kbps MP3 becomes a meaningfully smaller 128 kbps file");
            check(Math.Abs(after.Seconds - before.Seconds) < 0.12 && after.Frames > 0, "The reduced MP3 is decodable and preserves the complete clip duration");
            check(after.Rms <= before.Rms * 1.02 + 0.0001 && after.Peak <= before.Peak * 1.05 + 0.0001, "Re-encoding applies no volume boost or amplitude normalization");
            check(Hash(originalAudio).SequenceEqual(originalAudioHash), "Audio optimization leaves the original MP3 unchanged");
        }
        check(!Directory.Exists(folder), "Disposing an optimized MP3 removes the temporary candidate");
        using (var prepared = SoundboardAssetOptimizer.Prepare(compactAudio, null, temporary))
            check(prepared.AudioPath == compactAudio && !prepared.AudioReduced && prepared.TemporaryDirectory == null, "An already compact 128 kbps MP3 avoids another lossy encoding pass");
        using (var prepared = SoundboardAssetOptimizer.Prepare(tinyAudio, null, temporary))
            check(prepared.AudioPath == tinyAudio && !prepared.AudioReduced && prepared.TemporaryDirectory == null, "Short MP3s below 128 KiB keep their original bytes");
        string invalid = Path.Combine(root, "invalid.png"); File.WriteAllText(invalid, "not an image");
        int beforeFolders = Directory.Exists(temporary) ? Directory.GetDirectories(temporary).Length : 0;
        bool rejected = false;
        try { using (var prepared = SoundboardAssetOptimizer.Prepare(originalAudio, invalid, temporary)) { } } catch (InvalidDataException) { rejected = true; }
        check(rejected && Directory.GetDirectories(temporary).Length == beforeFolders && Hash(originalAudio).SequenceEqual(originalAudioHash), "An image failure rolls back an already generated MP3 candidate and preserves original files");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); rejected = false;
            try { using (var prepared = SoundboardAssetOptimizer.Prepare(originalAudio, largeImage, temporary, cancel.Token)) { } } catch (OperationCanceledException) { rejected = true; }
            check(rejected && Directory.GetDirectories(temporary).Length == beforeFolders, "Cancelled preparation leaves no partially prepared assets");
        }
        var foreignOwner = SoundboardAssetOptimizer.Prepare(null, transparent, temporary);
        string foreign = Path.Combine(foreignOwner.TemporaryDirectory, "untracked.txt"), candidatePath = foreignOwner.ImagePath; File.WriteAllText(foreign, "another owner");
        foreignOwner.Dispose();
        check(File.Exists(foreign) && !File.Exists(candidatePath), "Preparation cleanup deletes only its tracked candidate files"); File.Delete(foreign); Directory.Delete(Path.GetDirectoryName(foreign), false);
    }
    private static void EncodeTone(string path, int bitrate, double seconds)
    {
        NAudio.MediaFoundation.MediaFoundationApi.Startup();
        var signal = new OffsetSampleProvider(new SignalGenerator(48000, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.08 }) { Take = TimeSpan.FromSeconds(seconds) };
        MediaFoundationEncoder.EncodeToMp3(new SampleToWaveProvider16(signal), path, bitrate);
    }
    private static Analysis Analyze(string path)
    {
        using (var reader = new MediaFoundationReader(path))
        {
            var input = reader.ToSampleProvider(); var samples = new float[4096]; long count = 0; double sum = 0, maximum = 0; int read;
            while ((read = input.Read(samples, 0, samples.Length)) > 0)
                for (int i = 0; i < read; i++) { double value = samples[i]; sum += value * value; maximum = Math.Max(maximum, Math.Abs(value)); count++; }
            return new Analysis { Frames = count / input.WaveFormat.Channels, Seconds = count / (double)input.WaveFormat.Channels / input.WaveFormat.SampleRate, Rms = Math.Sqrt(sum / count), Peak = maximum };
        }
    }
    private static byte[] Hash(string path) { using (var hash = SHA256.Create()) using (var file = File.OpenRead(path)) return hash.ComputeHash(file); }
    private sealed class Analysis { internal long Frames; internal double Seconds, Rms, Peak; }
    private static void MakeOrientedJpeg(string path)
    {
        byte[] jpeg;
        using (var image = new Bitmap(20, 10))
        using (var stream = new MemoryStream())
        {
            using (var graphics = Graphics.FromImage(image)) { graphics.Clear(Color.Red); graphics.FillRectangle(Brushes.Blue, 10, 0, 10, 10); }
            image.Save(stream, ImageFormat.Jpeg); jpeg = stream.ToArray();
        }
        byte[] metadata;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            var comment = Encoding.ASCII.GetBytes("UTILITYTOOL_METADATA\0");
            writer.Write(new byte[] { (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, (byte)'I', (byte)'I' });
            writer.Write((ushort)42); writer.Write((uint)8); writer.Write((ushort)2);
            writer.Write((ushort)0x112); writer.Write((ushort)3); writer.Write((uint)1); writer.Write((ushort)6); writer.Write((ushort)0);
            writer.Write((ushort)0x10E); writer.Write((ushort)2); writer.Write((uint)comment.Length); writer.Write((uint)38);
            writer.Write((uint)0); writer.Write(comment); metadata = stream.ToArray();
        }
        using (var writer = new BinaryWriter(File.Create(path)))
        { writer.Write(jpeg, 0, 2); writer.Write(new byte[] { 0xFF, 0xE1, (byte)((metadata.Length + 2) >> 8), (byte)(metadata.Length + 2) }); writer.Write(metadata); writer.Write(jpeg, 2, jpeg.Length - 2); }
    }
    private static void MakeAnimatedGif(string path)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("GIF89a")); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(new byte[] { 128, 0, 0, 255, 0, 0, 0, 0, 255 });
            writer.Write(new byte[] { 0x21, 0xFE });
            for (int part = 0; part < 25; part++) { var comment = Enumerable.Repeat((byte)'x', 200).ToArray(); Encoding.ASCII.GetBytes("UTILITYTOOL_METADATA").CopyTo(comment, 0); writer.Write((byte)comment.Length); writer.Write(comment); }
            writer.Write((byte)0);
            for (int frame = 0; frame < 2; frame++)
            {
                writer.Write(new byte[] { 0x21, 0xF9, 4, 0, 10, 0, 0, 0, 0x2C });
                writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write(new byte[] { 0, 2, 2, frame == 0 ? (byte)0x44 : (byte)0x4C, 1, 0 });
            }
            writer.Write((byte)0x3B);
        }
    }
}
