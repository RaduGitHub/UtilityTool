using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
namespace UtilityTool.Backend
{
    internal static class AppIconService
    {
        private static readonly object cacheGate = new object();
        private static readonly Dictionary<string, byte[]> memoryCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        // Progress batches are owned by the receiver; the final dictionary is owned by the caller.
        internal static Task<Dictionary<string, Bitmap>> LoadAsync(IEnumerable<InstalledApp> apps, int size, CancellationToken token,
            IProgress<Dictionary<string, Bitmap>> progress = null, string cacheDirectory = null)
        {
            var snapshot = new List<InstalledApp>(apps);
            var ready = new TaskCompletionSource<Dictionary<string, Bitmap>>(TaskCreationOptions.RunContinuationsAsynchronously);
            string directory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UtilityTool", "icons");
            var worker = new Thread(delegate()
            {
                var images = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                var pending = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                var missing = new List<InstalledApp>();
                var clock = Stopwatch.StartNew();
                bool published = false;
                try
                {
                    // Read every ready icon first, so one slow Shell item never delays cached icons.
                    foreach (var app in snapshot)
                    {
                        token.ThrowIfCancellationRequested();
                        string key = CacheKey(app, size);
                        Bitmap image = ReadCached(key, directory);
                        if (image == null) { missing.Add(app); continue; }
                        images[app.Id] = image; pending[app.Id] = image;
                        if (!published || pending.Count >= 6 || clock.ElapsedMilliseconds >= 40)
                        { Publish(pending, progress); published = true; clock.Restart(); }
                    }
                    Publish(pending, progress); clock.Restart();
                    foreach (var app in missing)
                    {
                        token.ThrowIfCancellationRequested();
                        string key = CacheKey(app, size);
                        // Another shortcut may use the same executable icon, loaded earlier in this pass.
                        Bitmap image = ReadCached(key, directory);
                        if (image == null)
                        {
                            image = GetIcon(!string.IsNullOrEmpty(app.ExecutablePath) ? app.ExecutablePath : "shell:AppsFolder\\" + app.Id, size);
                            if (image == null && !string.IsNullOrEmpty(app.ExecutablePath)) image = GetIcon("shell:AppsFolder\\" + app.Id, size);
                            if (image != null) StoreCached(key, image, directory);
                        }
                        if (image == null) continue;
                        images[app.Id] = image; pending[app.Id] = image;
                        if (!published || pending.Count >= 6 || clock.ElapsedMilliseconds >= 40)
                        { Publish(pending, progress); published = true; clock.Restart(); }
                    }
                    token.ThrowIfCancellationRequested(); Publish(pending, progress);
                    ready.SetResult(images);
                }
                catch (OperationCanceledException) { foreach (var image in images.Values) image.Dispose(); ready.SetCanceled(); }
                catch (Exception error) { foreach (var image in images.Values) image.Dispose(); ready.SetException(error); }
            }) { IsBackground = true, Name = "UtilityTool app icons" };
            worker.SetApartmentState(ApartmentState.STA); worker.Start();
            return ready.Task;
        }
        private static void Publish(Dictionary<string, Bitmap> pending, IProgress<Dictionary<string, Bitmap>> progress)
        {
            if (pending.Count == 0) return;
            if (progress != null)
            {
                var batch = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
                try { foreach (var pair in pending) batch[pair.Key] = (Bitmap)pair.Value.Clone(); progress.Report(batch); }
                catch { foreach (var image in batch.Values) image.Dispose(); throw; }
            }
            pending.Clear();
        }
        private static string CacheKey(InstalledApp app, int size)
        {
            string identity;
            if (!string.IsNullOrEmpty(app.ExecutablePath))
            {
                identity = app.ExecutablePath.ToUpperInvariant();
                try { var file = new FileInfo(app.ExecutablePath); identity += "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks; }
                catch (IOException) { identity += "|" + app.Id; }
                catch (UnauthorizedAccessException) { identity += "|" + app.Id; }
            }
            else
            {
                // Store app logos are periodically refreshed even if their app ID stays the same.
                identity = app.Id + "|" + (DateTime.UtcNow.Date.Ticks / TimeSpan.TicksPerDay / 7);
            }
            // Refresh old Shell thumbnails with incorrect alpha or padded legacy icons.
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes("v2|" + size + "|" + identity))).Replace("-", "");
        }
        private static Bitmap ReadCached(string key, string directory)
        {
            byte[] bytes;
            lock (cacheGate) memoryCache.TryGetValue(directory + "|" + key, out bytes);
            try
            {
                if (bytes == null)
                {
                    string path = Path.Combine(directory, key + ".png");
                    if (!File.Exists(path)) return null;
                    bytes = File.ReadAllBytes(path);
                }
                using (var stream = new MemoryStream(bytes))
                using (var image = new Bitmap(stream))
                {
                    var result = new Bitmap(image);
                    Remember(directory + "|" + key, bytes);
                    return result;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
        }
        private static void Remember(string key, byte[] bytes)
        {
            lock (cacheGate) { if (memoryCache.Count >= 512) memoryCache.Clear(); memoryCache[key] = bytes; }
        }
        private static void StoreCached(string key, Bitmap image, string directory)
        {
            byte[] bytes;
            using (var stream = new MemoryStream()) { image.Save(stream, ImageFormat.Png); bytes = stream.ToArray(); }
            Remember(directory + "|" + key, bytes);
            string temporary = null;
            try
            {
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, key + ".png");
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                if (File.Exists(path)) File.Delete(path); // Replace only this generated icon-cache entry.
                File.Move(temporary, path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { if (temporary != null) { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
        }
        private static Bitmap GetIcon(string path, int size)
        {
            if (Path.IsPathRooted(path))
            {
                Bitmap executableIcon = GetExecutableIcon(path, size);
                if (executableIcon != null) return executableIcon;
            }
            IShellItemImageFactory factory = null; IntPtr bitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) < 0 || factory == null) return null;
                if (factory.GetImage(new NativeSize { Width = size, Height = size }, 4, out bitmap) < 0 || bitmap == IntPtr.Zero) return null;
                return CopyBitmap(bitmap);
            }
            catch (COMException) { return null; }
            catch (ArgumentException) { return null; }
            finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); if (factory != null) Marshal.ReleaseComObject(factory); }
        }
        private static Bitmap GetExecutableIcon(string path, int size)
        {
            IntPtr icon = IntPtr.Zero;
            try
            {
                // Extract the requested size directly, preserving legacy transparency masks.
                // Shell thumbnails can instead center a tiny icon inside a bordered bitmap.
                if (SHDefExtractIcon(path, 0, 0, out icon, IntPtr.Zero, (uint)size) != 0 || icon == IntPtr.Zero) return null;
                using (var resource = Icon.FromHandle(icon)) return resource.ToBitmap();
            }
            catch (ArgumentException) { return null; }
            finally { if (icon != IntPtr.Zero) DestroyIcon(icon); }
        }
        private static Bitmap CopyBitmap(IntPtr bitmap)
        {
            NativeBitmap details;
            if (GetObject(bitmap, Marshal.SizeOf(typeof(NativeBitmap)), out details) == 0 || details.Width <= 0 || details.Height <= 0) return null;
            int stride = checked(details.Width * 4);
            var pixels = new byte[checked(stride * details.Height)];
            var header = new BitmapHeader { Size = 40, Width = details.Width, Height = -details.Height, Planes = 1, Bits = 32 };
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            try { if (GetDIBits(dc, bitmap, 0, (uint)details.Height, pixels, ref header, 0) == 0) return null; }
            finally { DeleteDC(dc); }
            bool hasAlpha = false;
            for (int index = 3; index < pixels.Length; index += 4) if (pixels[index] != 0) { hasAlpha = true; break; }
            if (!hasAlpha) for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            // GetDIBits supplies straight-alpha pixels; declaring them premultiplied
            // brightens partially transparent edges and corrupts cached PNG colors.
            var result = new Bitmap(details.Width, details.Height, PixelFormat.Format32bppArgb);
            var locked = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { for (int row = 0; row < details.Height; row++) Marshal.Copy(pixels, row * stride, IntPtr.Add(locked.Scan0, row * locked.Stride), stride); }
            finally { result.UnlockBits(locked); }
            return result;
        }
        [StructLayout(LayoutKind.Sequential)] private struct NativeSize { internal int Width, Height; }
        [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { internal int Type, Width, Height, WidthBytes; internal ushort Planes, Bits; internal IntPtr Pixels; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapHeader
        {
            internal uint Size; internal int Width, Height; internal ushort Planes, Bits;
            internal uint Compression, ImageSize; internal int XResolution, YResolution; internal uint ColorsUsed, ColorsImportant;
        }
        [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory { [PreserveSig] int GetImage(NativeSize size, uint flags, out IntPtr bitmap); }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItemImageFactory factory);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHDefExtractIcon(string path, int index, uint flags, out IntPtr largeIcon, IntPtr smallIcon, uint size);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObject(IntPtr bitmap, int size, out NativeBitmap details);
        [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] pixels, ref BitmapHeader header, uint usage);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr bitmap);
    }
}
