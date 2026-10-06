using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
namespace UtilityTool.Frontend
{
    internal sealed class TileIconCache : IDisposable
    {
        private readonly DrawingBitmapCache<Image> images = new DrawingBitmapCache<Image>();
        private int pixelSize;
        internal void Draw(Graphics graphics, Image source)
        {
            int size = Math.Max(1, (int)Math.Round(60 * graphics.DpiX / 96f));
            if (size != pixelSize) { images.Clear(); pixelSize = size; }
            Bitmap image;
            if (!images.TryGet(source, out image))
            {
                image = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
                try
                {
                    using (var target = Graphics.FromImage(image))
                    using (var attributes = new ImageAttributes())
                    {
                        target.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        target.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        target.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                    }
                    images.Add(source, image);
                }
                catch { image.Dispose(); throw; }
            }
            graphics.DrawImage(image, new Rectangle(0, 0, 60, 60));
        }
        internal void Remove(Image source) { images.Remove(source); }
        public void Dispose() { images.Dispose(); }
    }
}
