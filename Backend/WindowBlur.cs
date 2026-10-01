using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace UtilityTool.Backend
{
    internal static class WindowBlur
    {
        // Capture once before the cover is shown. Raw and blurred images stay in memory.
        internal static Bitmap Capture(Rectangle bounds, Rectangle desktop)
        {
            Rectangle visible = Rectangle.Intersect(bounds, desktop);
            if (visible.Width < 1 || visible.Height < 1 || (long)bounds.Width * bounds.Height > 16000000)
                return null;
            try
            {
                using (var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(source))
                    {
                        graphics.Clear(Color.FromArgb(224, 230, 238));
                        graphics.CopyFromScreen(visible.Location,
                            new Point(visible.Left - bounds.Left, visible.Top - bounds.Top),
                            visible.Size, CopyPixelOperation.SourceCopy);
                    }
                    return Create(source);
                }
            }
            catch (Win32Exception) { return null; }
            catch (ExternalException) { return null; }
        }

        internal static Bitmap Create(Bitmap source)
        {
            // Work at a bounded resolution, then stretch smoothly in the cover.
            double scale = Math.Min(1.0, 360.0 / Math.Max(source.Width, source.Height));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            try
            {
                using (var graphics = Graphics.FromImage(result))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height));
                }
                var data = result.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
                try
                {
                    int stride = data.Stride;
                    byte[] pixels = new byte[stride * height];
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    byte[] horizontal = new byte[pixels.Length];
                    byte[] blurred = new byte[pixels.Length];
                    const int radius = 12;
                    const double sigma = 4.5;
                    double[] kernel = new double[radius * 2 + 1];
                    double total = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        kernel[k + radius] = Math.Exp(-(k * k) / (2 * sigma * sigma));
                        total += kernel[k + radius];
                    }
                    for (int k = 0; k < kernel.Length; k++) kernel[k] /= total;
                    Pass(pixels, horizontal, width, height, stride, kernel, radius, true);
                    Pass(horizontal, blurred, width, height, stride, kernel, radius, false);
                    Marshal.Copy(blurred, 0, data.Scan0, blurred.Length);
                }
                finally { result.UnlockBits(data); }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private static void Pass(byte[] source, byte[] destination, int width, int height,
            int stride, double[] kernel, int radius, bool horizontal)
        {
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int offset = y * stride + x * 4;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double value = 0;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int sx = horizontal ? Math.Max(0, Math.Min(width - 1, x + k)) : x;
                            int sy = horizontal ? y : Math.Max(0, Math.Min(height - 1, y + k));
                            value += source[sy * stride + sx * 4 + channel] * kernel[k + radius];
                        }
                        destination[offset + channel] = (byte)Math.Round(value);
                    }
                    destination[offset + 3] = 255;
                }
        }
    }
}
