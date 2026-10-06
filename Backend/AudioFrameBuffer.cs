using System;
namespace UtilityTool.Backend
{
    // Each output has its own bounded FIFO because independent audio devices have different clocks.
    internal sealed class AudioFrameBuffer
    {
        private readonly byte[] bytes;
        private readonly int alignment;
        private int head;
        private int count;
        internal AudioFrameBuffer(int capacityFrames, int blockAlignment)
        {
            if (capacityFrames <= 0 || blockAlignment <= 0) throw new ArgumentOutOfRangeException();
            alignment = blockAlignment; bytes = new byte[checked(capacityFrames * blockAlignment)];
        }
        internal int Frames { get { return count / alignment; } }
        internal void Write(byte[] data, int length)
        {
            if (length < 0 || length > data.Length || length % alignment != 0) throw new ArgumentException("Incomplete audio frame.");
            int offset = Math.Max(0, length - bytes.Length);
            length -= offset;
            int discard = Math.Max(0, count + length - bytes.Length);
            head = (head + discard) % bytes.Length; count -= discard;
            int tail = (head + count) % bytes.Length;
            int first = Math.Min(length, bytes.Length - tail);
            Buffer.BlockCopy(data, offset, bytes, tail, first);
            Buffer.BlockCopy(data, offset + first, bytes, 0, length - first);
            count += length;
        }
        internal int Read(byte[] destination, int requestedFrames)
        {
            int length = Math.Min(count, checked(requestedFrames * alignment));
            if (length > destination.Length) throw new ArgumentException("Audio destination too small.");
            int first = Math.Min(length, bytes.Length - head);
            Buffer.BlockCopy(bytes, head, destination, 0, first);
            Buffer.BlockCopy(bytes, 0, destination, first, length - first);
            head = (head + length) % bytes.Length; count -= length;
            return length / alignment;
        }
    }
}