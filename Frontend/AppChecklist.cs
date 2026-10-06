using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using UtilityTool.Backend;
namespace UtilityTool.Frontend
{
    internal sealed class AppChecklist : TilePicker
    {
        private readonly Dictionary<string, Bitmap> icons = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Bitmap fallback;
        private readonly TileIconCache displayIcons = new TileIconCache();
        internal readonly List<AppChecklistItem> Items = new List<AppChecklistItem>();
        internal event ItemCheckEventHandler ItemCheck;
        internal int IconSize { get { return 120; } }
        internal bool CheckOnClick { get; set; }
        protected override int TileCount { get { return Items == null ? 0 : Items.Count; } }
        protected override string TileName(int index) { return Items[index].App.Name; }
        protected override bool TileChecked(int index) { return Items[index].Checked; }
        protected override bool TileCanToggle(int index) { return CheckOnClick; }
        protected override void ToggleTile(int index) { Items[index].Checked = !Items[index].Checked; }
        internal AppChecklist()
        {
            CheckOnClick = true;
            fallback = new Bitmap(IconSize, IconSize);
            using (var graphics = Graphics.FromImage(fallback)) graphics.DrawIcon(SystemIcons.Application, new Rectangle(0, 0, IconSize, IconSize));
        }
        internal void AddApp(InstalledApp app, bool isChecked)
        {
            Items.Add(new AppChecklistItem(this, app, Items.Count, isChecked)); ItemsChanged();
        }
        internal void SetIcons(Dictionary<string, Bitmap> images, bool replaceExisting = true)
        {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in images)
            {
                Bitmap old;
                if (icons.TryGetValue(pair.Key, out old) && !replaceExisting) continue;
                icons[pair.Key] = (Bitmap)pair.Value.Clone();
                if (old != null) { displayIcons.Remove(old); old.Dispose(); }
                changed.Add(pair.Key);
            }
            if (changed.Count != 0) RefreshTiles(delegate(int index) { return changed.Contains(Items[index].App.Id); });
        }
        internal void SetItemChecked(int index, bool value) { Items[index].Checked = value; }
        internal bool GetItemChecked(int index) { return Items[index].Checked; }
        internal void ChangeCheck(AppChecklistItem item, bool value)
        {
            if (item.IsChecked == value || item.Index >= Items.Count || Items[item.Index] != item) return;
            var args = new ItemCheckEventArgs(item.Index, value ? CheckState.Checked : CheckState.Unchecked, item.IsChecked ? CheckState.Checked : CheckState.Unchecked);
            var handler = ItemCheck; if (handler != null) handler(this, args);
            item.IsChecked = args.NewValue == CheckState.Checked; CheckChanged(item.Index);
        }
        protected override void DrawTile(Graphics graphics, int index, Rectangle bounds, bool hovered, bool focused, TileTextStyle textStyle)
        {
            var item = Items[index]; Bitmap image;
            if (!icons.TryGetValue(item.App.Id, out image)) image = fallback;
            SelectionTileRenderer.Draw(graphics, bounds, textStyle, item.App.Name, item.Checked, hovered,
                focused, Enabled, true, item.Checked ? "SELECTED" : "",
                delegate(Graphics iconGraphics) { displayIcons.Draw(iconGraphics, image); });
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { displayIcons.Dispose(); fallback.Dispose(); foreach (var image in icons.Values) image.Dispose(); icons.Clear(); }
            base.Dispose(disposing);
        }
    }
    internal sealed class AppChecklistItem
    {
        private readonly AppChecklist owner;
        internal readonly InstalledApp App;
        internal readonly int Index;
        internal bool IsChecked;
        internal object Tag { get { return App; } }
        internal Rectangle Bounds { get { return owner.ItemBounds(Index); } }
        internal bool Checked { get { return IsChecked; } set { owner.ChangeCheck(this, value); } }
        internal AppChecklistItem(AppChecklist picker, InstalledApp app, int index, bool selected) { owner = picker; App = app; Index = index; IsChecked = selected; }
    }
}
