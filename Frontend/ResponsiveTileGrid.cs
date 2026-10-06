using System;
using System.Drawing;
using System.Windows.Forms;
namespace UtilityTool.Frontend
{
    // The minimum tile size is the existing design size; distribute spare width until another column fits.
    internal sealed class ResponsiveTileGrid
    {
        internal readonly int Columns, TileWidth, TileHeight, RowStep, GapX, ContentHeight;
        private readonly int remainder, left, top;
        internal ResponsiveTileGrid(int viewportWidth, int count, int reservedScrollbar, float dpi, Padding padding)
        {
            left = padding.Left; top = padding.Top;
            GapX = Math.Max(1, (int)Math.Round(14 * dpi));
            int gapY = Math.Max(1, (int)Math.Round(8 * dpi));
            int minimum = Math.Max(1, (int)Math.Round(156 * dpi));
            int available = Math.Max(1, viewportWidth - padding.Horizontal - reservedScrollbar);
            Columns = Math.Max(1, (available + GapX) / (minimum + GapX));
            // Lists shorter than one full row keep the default box size and unused space on the right.
            bool shortRow = count > 0 && count < Columns;
            if (count > 0) Columns = Math.Min(count, Columns);
            int tileSpace = shortRow ? Columns * minimum : Math.Max(1, available - (Columns - 1) * GapX);
            TileWidth = Math.Max(1, tileSpace / Columns); remainder = tileSpace % Columns;
            TileHeight = shortRow ? Math.Max(1, (int)Math.Round(148 * dpi)) : Math.Max(1, (int)Math.Round((tileSpace / (double)Columns) * 148 / 156));
            RowStep = TileHeight + gapY;
            int rows = (count + Columns - 1) / Columns;
            ContentHeight = rows == 0 ? 0 : padding.Vertical + (rows - 1) * RowStep + TileHeight;
        }
        internal Rectangle Bounds(int index, int scrollY)
        {
            int column = index % Columns;
            return new Rectangle(left + column * (TileWidth + GapX) + Math.Min(column, remainder),
                top + index / Columns * RowStep + scrollY, TileWidth + (column < remainder ? 1 : 0), TileHeight);
        }
        internal int FirstVisible(int scrollY) { return Math.Max(0, (-scrollY - top) / RowStep) * Columns; }
        internal int Hit(Point point, int scrollY, int count)
        {
            int y = point.Y - top - scrollY;
            if (point.X < left || y < 0 || y % RowStep >= TileHeight) return -1;
            int row = y / RowStep;
            for (int column = 0; column < Columns; column++)
            {
                int index = row * Columns + column;
                if (index >= count) return -1;
                var bounds = Bounds(index, scrollY);
                if (point.X >= bounds.Left && point.X < bounds.Right) return index;
            }
            return -1;
        }
    }
}
