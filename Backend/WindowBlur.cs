using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Threading.Tasks;

namespace UtilityTool.Backend
{
    internal static class WindowBlur
    {
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr destination, uint flags);

        internal static async Task<Bitmap> CaptureAsync(IntPtr window, Size size)
        {
            // PrintWindow can block in another app. Never block the UtilityTool UI.
            var capture = Task.Run(delegate { return Capture(window, size); });
            if (await Task.WhenAny(capture, Task.Delay(1200)) == capture) return await capture;
            var cleanup = capture.ContinueWith(delegate(Task<Bitmap> finished)
            {
                if (finished.Status == TaskStatus.RanToCompletion && finished.Result != null) finished.Result.Dispose();
                else if (finished.IsFaulted) { var ignored = finished.Exception; }
            }, TaskScheduler.Default);
            return null;
        }

        // Ask the target HWND to render its client area, even when another app covers it.
        // Raw and blurred images stay in memory; never fall back to desktop pixels.
        internal static Bitmap Capture(IntPtr window, Size size)
        {
            if (window == IntPtr.Zero || size.Width < 1 || size.Height < 1 || (long)size.Width * size.Height > 16000000)
                return null;
            try
            {
                using (var source = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
                {
                    using (var graphics = Graphics.FromImage(source))
                    {
                        graphics.Clear(Color.Magenta);
                        IntPtr dc = graphics.GetHdc();
                        try
                        {
                            // PW_CLIENTONLY | PW_RENDERFULLCONTENT supports accelerated windows.
                            if (!PrintWindow(window, dc, 3) && !PrintWindow(window, dc, 1)) return null;
                        }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    bool painted = false;
                    for (int y = 0; y < source.Height; y += Math.Max(1, source.Height / 8))
                        for (int x = 0; x < source.Width; x += Math.Max(1, source.Width / 8))
                        {
                            Color pixel = source.GetPixel(x, y);
                            if ((pixel.R != 255 || pixel.G != 0 || pixel.B != 255) && (pixel.R != 0 || pixel.G != 0 || pixel.B != 0)) painted = true;
                        }
                    if (!painted) return null;
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
