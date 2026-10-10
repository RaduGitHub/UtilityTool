using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace UtilityTool.Frontend.Wpf
{
    internal sealed class TileItem : INotifyPropertyChanged
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Glyph { get { return Icon == null ? fallback : ""; } }
        private readonly string fallback;
        private bool selected, editable = true;
        private ImageSource icon;
        public object Tag { get; private set; }
        public bool Selected { get { return selected; } set { if (selected == value) return; selected = value; Changed("Selected"); if (SelectionChanged != null) SelectionChanged(this); } }
        public bool Editable { get { return editable; } set { editable = value; Changed("Editable"); } }
        public ImageSource Icon { get { return icon; } set { icon = value; Changed("Icon"); Changed("Glyph"); } }
        internal event Action<TileItem> SelectionChanged;
        public event PropertyChangedEventHandler PropertyChanged;
        internal TileItem(string id, string name, object tag, bool selected = false, string glyph = "\uE8A5") { Id = id; Name = name; Tag = tag; this.selected = selected; fallback = glyph; }
        private void Changed(string name) { if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs(name)); }
    }
    internal sealed class TileRow : INotifyPropertyChanged
    {
        public List<TileItem> Items { get; private set; }
        public int Columns { get; private set; }
        private double width;
        public double Width { get { return width; } set { if (width == value) return; width = value; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Width")); } }
        public event PropertyChangedEventHandler PropertyChanged;
        internal TileRow(List<TileItem> items, int columns, double width) { Items = items; Columns = columns; Width = width; }
    }
    // Virtualize whole rows: only visible tiles have WPF visuals. On resize, icons
    // and type stay fixed; boxes stretch until another 160px column fits.
    internal sealed class TileGrid : ListBox
    {
        private readonly ObservableCollection<TileRow> rows = new ObservableCollection<TileRow>();
        private List<TileItem> items = new List<TileItem>();
        private int columns;
        private int reflowVersion;
        internal int Columns { get { return columns; } }
        internal TileGrid()
        {
            Background = Brushes.Transparent; BorderThickness = new Thickness(0); Padding = new Thickness(0);
            ScrollViewer.SetHorizontalScrollBarVisibility(this, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(this, ScrollBarVisibility.Auto);
            ScrollViewer.SetCanContentScroll(this, true);
            VirtualizingPanel.SetIsVirtualizing(this, true); VirtualizingPanel.SetVirtualizationMode(this, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(this, ScrollUnit.Pixel);
            var container = new Style(typeof(ListBoxItem));
            container.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); container.Setters.Add(new Setter(PaddingProperty, new Thickness(0))); container.Setters.Add(new Setter(MarginProperty, new Thickness(0))); container.Setters.Add(new Setter(FocusableProperty, false));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            var containerTemplate = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = presenter }; container.Setters.Add(new Setter(TemplateProperty, containerTemplate)); ItemContainerStyle = container;
            var row = new FrameworkElementFactory(typeof(ItemsControl)); row.SetBinding(ItemsControl.ItemsSourceProperty, new Binding("Items")); row.SetBinding(WidthProperty, new Binding("Width")); row.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
            row.SetValue(ItemsControl.ItemTemplateProperty, Application.Current.FindResource("ToolTileTemplate"));
            var uniform = new FrameworkElementFactory(typeof(UniformGrid)); uniform.SetBinding(UniformGrid.ColumnsProperty, new Binding("Columns"));
            row.SetValue(ItemsControl.ItemsPanelProperty, new ItemsPanelTemplate(uniform));
            ItemTemplate = new DataTemplate { VisualTree = row }; ItemsSource = rows;
            SizeChanged += delegate { Reflow(false); };
        }
        internal void SetItems(IEnumerable<TileItem> value) { items = new List<TileItem>(value); Reflow(true); }
        private void Reflow(bool force)
        {
            int next = Math.Max(1, (int)Math.Floor(Math.Max(160, ActualWidth - 20) / 160));
            double width = Math.Max(160, ActualWidth - 20);
            if (!force && next == columns) { foreach (var existing in rows) existing.Width = items.Count < columns ? items.Count * 160 : width; return; }
            var scroll = FindScroll(this);
            double scrollOffset = scroll == null ? 0 : scroll.VerticalOffset;
            int anchor = (int)(scrollOffset / 156) * columns;
            int version = ++reflowVersion;
            columns = next; rows.Clear();
            bool shortRow = items.Count < columns;
            for (int offset = 0; offset < items.Count; offset += columns)
            { int count = Math.Min(columns, items.Count - offset); rows.Add(new TileRow(items.GetRange(offset, count), shortRow ? count : columns, shortRow ? count * 160 : width)); }
            if (scroll != null && !force && anchor > 0)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { if (version == reflowVersion) scroll.ScrollToVerticalOffset((anchor / columns) * 156 + scrollOffset % 156); }));
        }
        private static ScrollViewer FindScroll(DependencyObject root)
        { if (root is ScrollViewer) return (ScrollViewer)root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var value = FindScroll(VisualTreeHelper.GetChild(root, i)); if (value != null) return value; } return null; }
        internal static BitmapImage ConvertIcon(System.Drawing.Bitmap bitmap)
        {
            using (var stream = new MemoryStream())
            { bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); stream.Position = 0; var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image; }
        }
    }
}
