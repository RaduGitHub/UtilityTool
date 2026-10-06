using System;
using System.Collections.Generic;
using System.Drawing;
namespace UtilityTool.Frontend
{
    // Keep recently visible tiles, rather than allocating a bitmap for the entire catalog.
    internal sealed class TileRenderCache : IDisposable
    {
        private const long MaximumBytes = 32 * 1024 * 1024;
        private const int MaximumTiles = 512;
        private readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private readonly LinkedList<int> recent = new LinkedList<int>();
        private long bytes;
        private sealed class Entry
        {
            internal Bitmap Image;
            internal int State;
            internal LinkedListNode<int> Node;
        }
        internal bool TryGet(int index, Size size, int state, out Bitmap image)
        {
            Entry entry;
            if (entries.TryGetValue(index, out entry))
            {
                if (entry.Image.Size == size && entry.State == state)
                {
                    recent.Remove(entry.Node); recent.AddLast(entry.Node); image = entry.Image; return true;
                }
                Remove(index);
            }
            image = null; return false;
        }
        internal bool CanStore(Size size) { return size.Width > 0 && size.Height > 0 && (long)size.Width * size.Height * 4 <= MaximumBytes; }
        // Takes ownership of the image.
        internal void Store(int index, int state, Bitmap image)
        {
            Remove(index);
            long cost = (long)image.Width * image.Height * 4;
            while (entries.Count >= MaximumTiles || bytes + cost > MaximumBytes) Remove(recent.First.Value);
            entries[index] = new Entry { Image = image, State = state, Node = recent.AddLast(index) }; bytes += cost;
        }
        internal void Remove(int index)
        {
            Entry entry;
            if (!entries.TryGetValue(index, out entry)) return;
            entries.Remove(index); recent.Remove(entry.Node);
            bytes -= (long)entry.Image.Width * entry.Image.Height * 4; entry.Image.Dispose();
        }
        internal void RemoveWhere(Predicate<int> matches)
        {
            var removed = new List<int>();
            foreach (int index in entries.Keys) if (matches(index)) removed.Add(index);
            foreach (int index in removed) Remove(index);
        }
        internal void Clear()
        {
            foreach (var entry in entries.Values) entry.Image.Dispose(); entries.Clear(); recent.Clear(); bytes = 0;
        }
        public void Dispose() { Clear(); }
    }
}
