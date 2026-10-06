using System;
using System.Collections.Generic;
using System.Drawing;
namespace UtilityTool.Frontend
{
    // Small reusable drawing assets survive changes to the surrounding tile geometry.
    internal sealed class DrawingBitmapCache<TKey> : IDisposable
    {
        private const long MaximumBytes = 16 * 1024 * 1024;
        private const int MaximumEntries = 512;
        private readonly Dictionary<TKey, Entry> entries = new Dictionary<TKey, Entry>();
        private readonly LinkedList<TKey> recent = new LinkedList<TKey>();
        private long bytes;
        private sealed class Entry { internal Bitmap Image; internal LinkedListNode<TKey> Node; }
        internal bool TryGet(TKey key, out Bitmap image)
        {
            Entry entry;
            if (!entries.TryGetValue(key, out entry)) { image = null; return false; }
            recent.Remove(entry.Node); recent.AddLast(entry.Node); image = entry.Image; return true;
        }
        // Takes ownership. Icon and label assets are bounded by their fixed design sizes.
        internal void Add(TKey key, Bitmap image)
        {
            long cost = (long)image.Width * image.Height * 4;
            if (cost > MaximumBytes) throw new ArgumentException("Drawing asset exceeds the cache budget.");
            Remove(key);
            while (entries.Count >= MaximumEntries || bytes + cost > MaximumBytes) Remove(recent.First.Value);
            entries.Add(key, new Entry { Image = image, Node = recent.AddLast(key) }); bytes += cost;
        }
        internal void Remove(TKey key)
        {
            Entry entry;
            if (!entries.TryGetValue(key, out entry)) return;
            entries.Remove(key); recent.Remove(entry.Node);
            bytes -= (long)entry.Image.Width * entry.Image.Height * 4; entry.Image.Dispose();
        }
        internal void Clear()
        {
            foreach (var entry in entries.Values) entry.Image.Dispose(); entries.Clear(); recent.Clear(); bytes = 0;
        }
        public void Dispose() { Clear(); }
    }
}
