using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NAudio.Wave;

namespace UtilityTool.Backend
{
    // Opt-in preparation only. The store copies the returned paths before this
    // owner is disposed; originals are never rewritten or removed.
    internal static class SoundboardAssetOptimizer
    {
        internal const int MaximumImageSide = 512;
        internal const long MaximumDecodedImagePixels = 100000000;
        internal const long MinimumAudioBytes = 128 * 1024;
        private static readonly int[] Mp3Rates = { 32000, 44100, 48000 };

        internal static SoundboardOptimizedAssets Prepare(string mp3, string image = null, string temporaryRoot = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var prepared = new SoundboardOptimizedAssets(mp3, image, temporaryRoot);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (mp3 != null) PrepareAudio(prepared, mp3, cancellationToken);
                if (!string.IsNullOrWhiteSpace(image)) PrepareImage(prepared, image, cancellationToken);
                return prepared;
            }
            catch { prepared.Dispose(); throw; }
        }

        private static void PrepareAudio(SoundboardOptimizedAssets prepared, string path, CancellationToken token)
        {
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an MP3 file on your computer.");
            long originalBytes = new FileInfo(path).Length;
            // Short/already compact sounds gain little from another lossy pass.
            if (originalBytes < MinimumAudioBytes) return;
            using (var source = new MediaFoundationReader(path))
            {
                WaveFormat format = source.WaveFormat;
                if (format.Channels != 1 && format.Channels != 2 || format.SampleRate <= 0 || format.AverageBytesPerSecond <= 0 || source.Length <= 0)
                    throw new InvalidDataException("This MP3 needs valid mono or stereo audio.");
                double seconds = source.Length / (double)format.AverageBytesPerSecond;
                if (originalBytes * 8.0 / seconds <= 144000) return;
                string candidate = prepared.CreatePath("audio.mp3");
                MediaFoundationResampler resampler = null;
                try
                {
                    IWaveProvider input = source;
                    int rate = Mp3Rates.Contains(format.SampleRate) ? format.SampleRate : 44100;
                    if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || rate != format.SampleRate)
                    {
                        // Only convert when the Windows MP3 encoder needs it.
                        // Native rate/channels avoid needless processing of music.
                        resampler = new MediaFoundationResampler(source, new WaveFormat(rate, 16, format.Channels)) { ResamplerQuality = 60 };
                        input = resampler;
                    }
                    NAudio.MediaFoundation.MediaFoundationApi.Startup();
                    MediaFoundationEncoder.EncodeToMp3(new BoundedAudioProvider(input, token), candidate, 128000);
                }
                finally { if (resampler != null) resampler.Dispose(); }
                token.ThrowIfCancellationRequested();
                using (var decoded = new MediaFoundationReader(candidate))
                {
                    double candidateSeconds = decoded.Length / (double)decoded.WaveFormat.AverageBytesPerSecond;
                    if (decoded.Length == 0 || Math.Abs(candidateSeconds - seconds) > 0.12 + seconds * 0.00001)
                        throw new InvalidDataException("The smaller MP3 did not preserve the sound's duration.");
                }
                // A measurable reduction is required to justify re-encoding.
                if (new FileInfo(candidate).Length <= originalBytes * 0.90)
                { prepared.AudioPath = candidate; prepared.AudioReduced = true; }
            }
        }

        private static void PrepareImage(SoundboardOptimizedAssets prepared, string path, CancellationToken token)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (!File.Exists(path) || !new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico" }.Contains(extension))
                throw new ArgumentException("Choose a PNG, JPG, BMP, GIF or ICO image.");
            using (var source = OpenImage(path))
            {
                if ((long)source.Width * source.Height > MaximumDecodedImagePixels)
                    throw new ArgumentException("Choose an image with at most 100 million pixels to reduce its size.");
                if (source.FrameDimensionsList.Contains(FrameDimension.Time.Guid)) source.SelectActiveFrame(FrameDimension.Time, 0);
                ApplyOrientation(source);
                int width = source.Width, height = source.Height;
                double scale = Math.Min(1, MaximumImageSide / (double)Math.Max(width, height));
                int outputWidth = Math.Max(1, (int)Math.Round(width * scale)), outputHeight = Math.Max(1, (int)Math.Round(height * scale));
                token.ThrowIfCancellationRequested();
                // A fresh bitmap drops EXIF, comments, additional animation
                // frames and other source metadata while retaining transparency.
                using (var resized = new Bitmap(outputWidth, outputHeight, PixelFormat.Format32bppArgb))
                {
                    resized.SetResolution(96, 96);
                    using (var graphics = Graphics.FromImage(resized))
                    using (var attributes = new ImageAttributes())
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.CompositingQuality = CompositingQuality.HighQuality;
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        graphics.DrawImage(source, new Rectangle(0, 0, outputWidth, outputHeight), 0, 0, width, height, GraphicsUnit.Pixel, attributes);
                    }
                    token.ThrowIfCancellationRequested();
                    string png = prepared.CreatePath("image.png"); resized.Save(png, ImageFormat.Png);
                    string best = png;
                    if (!HasTransparency(resized))
                    {
                        string jpeg = prepared.CreatePath("image.jpg");
                        using (var settings = new EncoderParameters(1))
                        {
                            settings.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                            resized.Save(jpeg, ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == ImageFormat.Jpeg.Guid), settings);
                        }
                        if (new FileInfo(jpeg).Length < new FileInfo(png).Length) best = jpeg;
                    }
                    long originalBytes = new FileInfo(path).Length, optimizedBytes = new FileInfo(best).Length;
                    if (optimizedBytes < originalBytes || width > MaximumImageSide || height > MaximumImageSide)
                    {
                        prepared.ImagePath = best; prepared.ImageReduced = optimizedBytes < originalBytes;
                        prepared.ImageOptimized = true;
                    }
                }
            }
        }
        private static Image OpenImage(string path)
        {
            try { return Image.FromFile(path, true); }
            catch (ArgumentException failure) { throw new InvalidDataException("This image could not be decoded. Choose a valid image file.", failure); }
            // GDI+ also reports invalid/corrupt image data as OutOfMemory.
            catch (OutOfMemoryException failure) { throw new InvalidDataException("This image could not be decoded. Choose a valid or smaller image.", failure); }
        }
        private static bool HasTransparency(Bitmap image)
        {
            var locked = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[image.Width * 4];
                for (int y = 0; y < image.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(locked.Scan0, y * locked.Stride), row, 0, row.Length);
                    for (int x = 3; x < row.Length; x += 4) if (row[x] != 255) return true;
                }
                return false;
            }
            finally { image.UnlockBits(locked); }
        }
        private static void ApplyOrientation(Image image)
        {
            const int orientationId = 0x112;
            if (!image.PropertyIdList.Contains(orientationId)) return;
            var value = image.GetPropertyItem(orientationId).Value;
            if (value == null || value.Length < 2) return;
            var rotations = new[] { RotateFlipType.RotateNoneFlipNone, RotateFlipType.RotateNoneFlipX,
                RotateFlipType.Rotate180FlipNone, RotateFlipType.Rotate180FlipX, RotateFlipType.Rotate90FlipX,
                RotateFlipType.Rotate90FlipNone, RotateFlipType.Rotate270FlipX, RotateFlipType.Rotate270FlipNone };
            int orientation = BitConverter.ToUInt16(value, 0);
            if (orientation >= 1 && orientation <= 8) image.RotateFlip(rotations[orientation - 1]);
        }

        // The encoder pulls bounded chunks; no whole-song PCM buffer is kept.
        private sealed class BoundedAudioProvider : IWaveProvider
        {
            private readonly IWaveProvider source;
            private readonly CancellationToken cancellation;
            public WaveFormat WaveFormat { get { return source.WaveFormat; } }
            internal BoundedAudioProvider(IWaveProvider source, CancellationToken cancellation) { this.source = source; this.cancellation = cancellation; }
            public int Read(byte[] buffer, int offset, int count)
            {
                cancellation.ThrowIfCancellationRequested();
                int bounded = Math.Min(count, Math.Max(WaveFormat.BlockAlign, 65536 / WaveFormat.BlockAlign * WaveFormat.BlockAlign));
                return source.Read(buffer, offset, bounded - bounded % WaveFormat.BlockAlign);
            }
        }
    }

    internal sealed class SoundboardOptimizedAssets : IDisposable
    {
        private readonly string temporaryRoot;
        private readonly List<string> created = new List<string>();
        private string directory;
        private bool disposed;
        internal string AudioPath { get; set; }
        internal string ImagePath { get; set; }
        internal bool AudioReduced { get; set; }
        internal bool ImageReduced { get; set; }
        internal bool ImageOptimized { get; set; }
        internal string TemporaryDirectory { get { return directory; } }
        internal SoundboardOptimizedAssets(string audio, string image, string temporaryRoot)
        {
            AudioPath = audio; ImagePath = image;
            this.temporaryRoot = Path.GetFullPath(temporaryRoot ?? Path.Combine(Path.GetTempPath(), "UtilityTool", "AssetOptimization"));
        }
        internal string CreatePath(string file)
        {
            if (disposed) throw new ObjectDisposedException("SoundboardOptimizedAssets");
            if (!new[] { "audio.mp3", "image.png", "image.jpg" }.Contains(file)) throw new ArgumentException("Unexpected temporary asset filename.");
            if (directory == null)
            {
                directory = Path.GetFullPath(Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N")));
                VerifyDirectory(); Directory.CreateDirectory(directory);
            }
            string path = Path.Combine(directory, file);
            if (!created.Contains(path)) created.Add(path);
            return path;
        }
        private void VerifyDirectory()
        {
            Guid id;
            if (directory == null || !Guid.TryParseExact(Path.GetFileName(directory), "N", out id)
                || !string.Equals(Path.GetDirectoryName(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), temporaryRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The asset preparation folder is outside its temporary location.");
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            if (directory == null) return;
            VerifyDirectory();
            foreach (string file in created) if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory, false);
        }
    }
}
