using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace UtilityTool.Frontend
{
    // One buffered surface for both pickers; layout and painting never iterate over the full catalog.
    internal abstract class TilePicker : ScrollableControl
    {
        private ResponsiveTileGrid grid;
        private float dpiScale = 1;
        private int hovered = -1, focused = -1, pressed = -1, updateDepth;
        private bool arranging;
        private readonly ToolTip tooltips = new ToolTip();
        private TileTextStyle textStyle;
        private readonly TileRenderCache renderedTiles = new TileRenderCache();
        private bool cacheReady;
        private Bitmap resizingTile;
        protected abstract int TileCount { get; }
        protected abstract string TileName(int index);
        protected virtual string TileTooltip(int index) { return TileName(index); }
        protected abstract bool TileChecked(int index);
        protected virtual bool TileCanToggle(int index) { return true; }
        protected abstract void ToggleTile(int index);
        protected abstract void DrawTile(Graphics graphics, int index, Rectangle bounds, bool hovered, bool focused, TileTextStyle textStyle);
        internal int LayoutColumns { get { return grid == null ? 1 : grid.Columns; } }
        [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.Style |= 0x00800000; return parameters; } // Fixed non-scrolling border.
        }
        protected TilePicker()
        {
            AutoScroll = true; TabStop = true; Padding = new Padding(8);
            BackColor = AppTheme.Surface; ForeColor = AppTheme.Ink;
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        internal void BeginUpdate() { updateDepth++; }
        internal void EndUpdate()
        {
            if (updateDepth > 0) updateDepth--;
            if (updateDepth != 0) return;
            renderedTiles.Clear();
            ClearHover(); pressed = -1;
            focused = TileCount == 0 ? -1 : Math.Max(0, Math.Min(focused, TileCount - 1));
            AutoScrollPosition = Point.Empty; ArrangeTiles(false); Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        }
        protected void ItemsChanged() { if (updateDepth == 0) EndUpdate(); }
        private void ArrangeTiles(bool preservePosition)
        {
            if (arranging || IsDisposed || updateDepth != 0) return;
            arranging = true;
            try
            {
                int previousScroll = -AutoScrollPosition.Y;
                int anchor = grid == null ? 0 : previousScroll / grid.RowStep * grid.Columns;
                double rowFraction = grid == null ? 0 : (previousScroll / (double)grid.RowStep) % 1;
                int reserve = VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth;
                var nextGrid = new ResponsiveTileGrid(ClientSize.Width, TileCount, reserve, dpiScale, Padding);
                if (grid == null || grid.TileWidth != nextGrid.TileWidth || grid.TileHeight != nextGrid.TileHeight)
                {
                    renderedTiles.Clear(); cacheReady = false;
                }
                grid = nextGrid;
                var minimum = new Size(0, grid.ContentHeight);
                if (AutoScrollMinSize != minimum) AutoScrollMinSize = minimum;
                VerticalScroll.SmallChange = Math.Max(16, (int)(24 * dpiScale));
                int desiredScroll = preservePosition ? anchor / grid.Columns * grid.RowStep + (int)Math.Round(rowFraction * grid.RowStep) : 0;
                desiredScroll = Math.Max(0, Math.Min(desiredScroll, Math.Max(0, grid.ContentHeight - ClientSize.Height)));
                if (-AutoScrollPosition.Y != desiredScroll) AutoScrollPosition = new Point(0, desiredScroll);
                if (HorizontalScroll.Visible && IsHandleCreated) ShowScrollBar(Handle, 0, false);
            }
            finally { arranging = false; }
        }
        internal Rectangle ItemBounds(int index)
        {
            if (grid == null) ArrangeTiles(false);
            return grid == null ? Rectangle.Empty : grid.Bounds(index, AutoScrollPosition.Y);
        }
        internal void EnsureVisible(int index)
        {
            if (index < 0 || index >= TileCount) return;
            var bounds = ItemBounds(index); int scroll = -AutoScrollPosition.Y;
            if (bounds.Top < Padding.Top) scroll += bounds.Top - Padding.Top;
            else if (bounds.Bottom > ClientSize.Height - Padding.Bottom) scroll += bounds.Bottom - ClientSize.Height + Padding.Bottom;
            bool moved = -AutoScrollPosition.Y != Math.Max(0, scroll);
            if (moved) AutoScrollPosition = new Point(0, Math.Max(0, scroll));
            // AutoScrollPosition moves existing pixels and invalidates the exposed strip.
            ClearHover();
        }
        private int HitTile(Point point) { return grid == null ? -1 : grid.Hit(point, AutoScrollPosition.Y, TileCount); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (grid == null || TileCount == 0) return;
            if (textStyle == null) textStyle = new TileTextStyle(Font);
            int first = grid.FirstVisible(AutoScrollPosition.Y);
            int last = Math.Min(TileCount, first + ((ClientSize.Height + grid.RowStep - 1) / grid.RowStep + 1) * grid.Columns);
            for (int index = first; index < last; index++)
            {
                var bounds = ItemBounds(index);
                if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
                bool isHovered = hovered == index, isFocused = Focused && focused == index && ShowFocusCues;
                // While sizing, reuse one scratch bitmap instead of allocating a cache for every visible tile.
                if (!cacheReady || !renderedTiles.CanStore(bounds.Size))
                { DrawUncachedTile(e.Graphics, index, bounds, isHovered, isFocused); continue; }
                int state = (isHovered ? 1 : 0) | (isFocused ? 2 : 0) | (Enabled ? 4 : 0) | (TileChecked(index) ? 8 : 0);
                Bitmap image;
                if (!renderedTiles.TryGet(index, bounds.Size, state, out image))
                {
                    image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                    try
                    {
                        image.SetResolution(e.Graphics.DpiX, e.Graphics.DpiY);
                        using (var graphics = Graphics.FromImage(image))
                        {
                            graphics.Clear(BackColor);
                            DrawTile(graphics, index, new Rectangle(Point.Empty, bounds.Size), isHovered, isFocused, textStyle);
                        }
                        renderedTiles.Store(index, state, image);
                    }
                    catch { image.Dispose(); throw; }
                }
                e.Graphics.DrawImageUnscaled(image, bounds.Location);
            }
            cacheReady = true;
        }
        private void DrawUncachedTile(Graphics target, int index, Rectangle bounds, bool isHovered, bool isFocused)
        {
            if (!renderedTiles.CanStore(bounds.Size))
            { DrawTile(target, index, bounds, isHovered, isFocused, textStyle); return; }
            int width = grid.TileWidth + 1, height = grid.TileHeight;
            if (resizingTile == null || resizingTile.Width != width || resizingTile.Height != height)
            {
                if (resizingTile != null) resizingTile.Dispose();
                resizingTile = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                resizingTile.SetResolution(target.DpiX, target.DpiY);
            }
            using (var graphics = Graphics.FromImage(resizingTile))
            {
                graphics.Clear(BackColor); graphics.SetClip(new Rectangle(Point.Empty, bounds.Size));
                DrawTile(graphics, index, new Rectangle(Point.Empty, bounds.Size), isHovered, isFocused, textStyle);
            }
            target.DrawImageUnscaledAndClipped(resizingTile, bounds);
        }
        protected void RefreshTiles(Predicate<int> changed)
        {
            renderedTiles.RemoveWhere(changed);
            if (grid == null) return;
            int first = grid.FirstVisible(AutoScrollPosition.Y);
            int last = Math.Min(TileCount, first + ((ClientSize.Height + grid.RowStep - 1) / grid.RowStep + 1) * grid.Columns);
            for (int index = first; index < last; index++) if (changed(index)) Invalidate(ItemBounds(index));
        }
        protected void CheckChanged(int index)
        {
            renderedTiles.Remove(index);
            Invalidate(ItemBounds(index)); AccessibilityNotifyClients(AccessibleEvents.StateChange, index);
        }
        private void InvalidateTile(int index) { if (index >= 0 && index < TileCount) Invalidate(ItemBounds(index)); }
        private void ClearHover()
        {
            InvalidateTile(hovered); hovered = -1; tooltips.SetToolTip(this, null); Cursor = Cursors.Default;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e); int next = HitTile(e.Location);
            if (next == hovered) return;
            if (hovered >= 0 && hovered < TileCount) Invalidate(ItemBounds(hovered));
            hovered = next; Cursor = next >= 0 && TileCanToggle(next) && Enabled ? Cursors.Hand : Cursors.Default;
            tooltips.SetToolTip(this, next >= 0 ? TileTooltip(next) : null);
            if (next >= 0) Invalidate(ItemBounds(next));
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ClearHover(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
            Focus(); pressed = HitTile(e.Location);
            if (pressed >= 0 && focused != pressed) { InvalidateTile(focused); focused = pressed; InvalidateTile(focused); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); int hit = HitTile(e.Location);
            if (Enabled && e.Button == MouseButtons.Left && pressed >= 0 && hit == pressed && TileCanToggle(hit)) ToggleTile(hit);
            pressed = -1;
        }
        protected override bool IsInputKey(Keys keyData)
        {
            var key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e); if (TileCount == 0 || grid == null) return;
            int next = Math.Max(0, focused), page = Math.Max(1, ClientSize.Height / grid.RowStep) * grid.Columns;
            switch (e.KeyCode)
            {
                case Keys.Space: case Keys.Enter:
                    if (Enabled && TileCanToggle(next)) ToggleTile(next);
                    break;
                case Keys.Left: next--; break; case Keys.Right: next++; break;
                case Keys.Up: next -= grid.Columns; break; case Keys.Down: next += grid.Columns; break;
                case Keys.Home: next = 0; break; case Keys.End: next = TileCount - 1; break;
                case Keys.PageUp: next -= page; break; case Keys.PageDown: next += page; break;
                default: return;
            }
            InvalidateTile(focused); focused = Math.Max(0, Math.Min(next, TileCount - 1));
            InvalidateTile(focused); EnsureVisible(focused);
            e.Handled = e.SuppressKeyPress = true; AccessibilityNotifyClients(AccessibleEvents.Focus, focused);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (focused < 0 && TileCount > 0) focused = 0; InvalidateTile(focused); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); InvalidateTile(focused); }
        protected override void OnFontChanged(EventArgs e)
        {
            if (textStyle != null) { textStyle.Dispose(); textStyle = null; }
            renderedTiles.Clear();
            base.OnFontChanged(e); Invalidate();
        }
        protected override void OnBackColorChanged(EventArgs e) { renderedTiles.Clear(); base.OnBackColorChanged(e); }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            using (var graphics = CreateGraphics()) dpiScale = graphics.DpiX / 96f;
            ArrangeTiles(false);
        }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ArrangeTiles(true); ClearHover(); Invalidate(); }
        // Native scrolling already moves existing pixels and invalidates newly exposed rows.
        // Invalidating the entire viewport here defeats that reuse on wide windows.
        protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); ClearHover(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); ClearHover(); } // Wheel scrolling does not raise Scroll.
        protected override AccessibleObject CreateAccessibilityInstance() { return new PickerAccessibility(this); }
        private sealed class PickerAccessibility : ControlAccessibleObject
        {
            private readonly TilePicker picker;
            internal PickerAccessibility(TilePicker owner) : base(owner) { picker = owner; }
            public override AccessibleRole Role { get { return AccessibleRole.List; } }
            public override int GetChildCount() { return picker.TileCount; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < picker.TileCount ? new TileAccessibility(picker, index) : null; }
            public override AccessibleObject GetFocused() { return picker.Focused && picker.focused >= 0 ? GetChild(picker.focused) : null; }
            public override AccessibleObject HitTest(int x, int y) { int index = picker.HitTile(picker.PointToClient(new Point(x, y))); return index >= 0 ? GetChild(index) : base.HitTest(x, y); }
        }
        private sealed class TileAccessibility : AccessibleObject
        {
            private readonly TilePicker picker; private readonly int index;
            internal TileAccessibility(TilePicker owner, int tileIndex) { picker = owner; index = tileIndex; }
            private bool Exists { get { return index < picker.TileCount; } }
            public override string Name { get { return Exists ? picker.TileName(index) : ""; } set { } }
            public override AccessibleRole Role { get { return AccessibleRole.CheckButton; } }
            public override Rectangle Bounds { get { return Exists ? picker.RectangleToScreen(picker.ItemBounds(index)) : Rectangle.Empty; } }
            public override AccessibleObject Parent { get { return picker.AccessibilityObject; } }
            public override string DefaultAction { get { return Exists && picker.TileChecked(index) ? "Deselect" : "Select"; } }
            public override AccessibleStates State
            {
                get
                {
                    if (!Exists) return AccessibleStates.Unavailable;
                    return AccessibleStates.Focusable | (picker.TileChecked(index) ? AccessibleStates.Checked : 0) |
                        (picker.Focused && picker.focused == index ? AccessibleStates.Focused : 0) |
                        (!picker.Enabled || !picker.TileCanToggle(index) ? AccessibleStates.Unavailable : 0) |
                        (!picker.ItemBounds(index).IntersectsWith(picker.ClientRectangle) ? AccessibleStates.Offscreen : 0);
                }
            }
            public override void DoDefaultAction() { if (Exists && picker.Enabled && picker.TileCanToggle(index)) picker.ToggleTile(index); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                renderedTiles.Dispose(); if (resizingTile != null) { resizingTile.Dispose(); resizingTile = null; }
                tooltips.Dispose(); if (textStyle != null) { textStyle.Dispose(); textStyle = null; }
            }
            base.Dispose(disposing);
        }
    }
}
