using System;
using System.Collections.Generic;
using System.IO;

namespace DSPRE
{
    /// <summary>
    /// Writes a looping GIF89a from raw BGRA frames, with no UI or image-library dependency.
    /// Pixels below half alpha become the transparent palette index; everything else is
    /// quantized into one global 255-colour palette so frames share their colours.
    /// </summary>
    public static class GifAnimation
    {
        /// <summary>Palette index kept clear for see-through pixels.</summary>
        private const int TransparentIndex = 0;
        private const int PaletteSize = 256;
        private const int ColourBits = 6;
        private const int ColourLevels = 1 << ColourBits;

        /// <summary>Field animations run at 30 steps a second, so one frame is 3.33 hundredths.</summary>
        public const int DefaultDelayCentiseconds = 3;

        private sealed class ColourEntry
        {
            public int R, G, B;
            public int Count;
        }

        private sealed class ColourBox
        {
            public int Start, End;
            public int MinR = 255, MaxR, MinG = 255, MaxG, MinB = 255, MaxB;
            public long Count;

            public ColourBox(int start, int end)
            {
                Start = start;
                End = end;
            }

            public int LongestRange => Math.Max(MaxR - MinR, Math.Max(MaxG - MinG, MaxB - MinB));

            public int LongestChannel
            {
                get
                {
                    int r = MaxR - MinR, g = MaxG - MinG, b = MaxB - MinB;
                    return r >= g && r >= b ? 0 : g >= b ? 1 : 2;
                }
            }

            public void Measure(List<ColourEntry> entries)
            {
                MinR = 255; MaxR = 0; MinG = 255; MaxG = 0; MinB = 255; MaxB = 0;
                Count = 0;
                for (int i = Start; i < End; i++)
                {
                    ColourEntry e = entries[i];
                    if (e.R < MinR) MinR = e.R;
                    if (e.R > MaxR) MaxR = e.R;
                    if (e.G < MinG) MinG = e.G;
                    if (e.G > MaxG) MaxG = e.G;
                    if (e.B < MinB) MinB = e.B;
                    if (e.B > MaxB) MaxB = e.B;
                    Count += e.Count;
                }
            }
        }

        /// <summary>
        /// Encodes frames of equal size. The delay is per frame in hundredths of a second;
        /// GIF's coarsest unit, so 3 is the closest to the field's 30 steps a second.
        /// </summary>
        public static byte[] Encode(IReadOnlyList<RawImage> frames, int delayCentiseconds = DefaultDelayCentiseconds)
        {
            if (frames == null || frames.Count == 0) throw new ArgumentException("At least one frame is needed.", nameof(frames));
            RawImage first = frames[0] ?? throw new ArgumentException("A frame is missing.", nameof(frames));
            if (first.Width <= 0 || first.Height <= 0) throw new ArgumentException("A frame is empty.", nameof(frames));
            foreach (RawImage frame in frames)
            {
                if (frame == null || frame.Width != first.Width || frame.Height != first.Height)
                    throw new ArgumentException("Every frame must have the same size.", nameof(frames));
            }

            int delay = Math.Clamp(delayCentiseconds, 1, 65535);
            byte[] palette = new byte[PaletteSize * 3];
            byte[] lookup = BuildPalette(frames, palette, out bool transparent);

            using MemoryStream stream = new MemoryStream();
            WriteHeader(stream, first.Width, first.Height, palette);
            WriteLoopExtension(stream);

            byte[] indices = new byte[first.Width * first.Height];
            for (int i = 0; i < frames.Count; i++)
            {
                WriteFrame(stream, frames[i], lookup, indices, delay, transparent);
            }

            stream.WriteByte(0x3B);   // trailer
            return stream.ToArray();
        }

        /// <summary>Builds a global palette from every frame and a lookup for the quantized colours.</summary>
        private static byte[] BuildPalette(IReadOnlyList<RawImage> frames, byte[] palette, out bool transparent)
        {
            int bins = 1 << (ColourBits * 3);
            int[] counts = new int[bins];
            transparent = false;

            foreach (RawImage frame in frames)
            {
                byte[] pixels = frame.Bgra;
                for (int i = 0; i + 3 < pixels.Length; i += 4)
                {
                    if (pixels[i + 3] < 128)
                    {
                        transparent = true;
                        continue;
                    }
                    int key = ((pixels[i + 2] >> 2) << (ColourBits * 2))
                            | ((pixels[i + 1] >> 2) << ColourBits)
                            | (pixels[i] >> 2);
                    counts[key]++;
                }
            }

            List<ColourEntry> entries = new List<ColourEntry>();
            for (int key = 0; key < bins; key++)
            {
                if (counts[key] == 0) continue;
                entries.Add(new ColourEntry
                {
                    R = ((key >> (ColourBits * 2)) & (ColourLevels - 1)) * 4 + 2,
                    G = ((key >> ColourBits) & (ColourLevels - 1)) * 4 + 2,
                    B = (key & (ColourLevels - 1)) * 4 + 2,
                    Count = counts[key],
                });
            }

            List<ColourBox> boxes = new List<ColourBox>();
            if (entries.Count > 0)
            {
                ColourBox initial = new ColourBox(0, entries.Count);
                initial.Measure(entries);
                boxes.Add(initial);

                while (boxes.Count < PaletteSize - 1)
                {
                    ColourBox split = null;
                    int widest = 0;
                    foreach (ColourBox box in boxes)
                    {
                        if (box.End - box.Start < 2) continue;
                        int range = box.LongestRange;
                        if (range > widest) { widest = range; split = box; }
                    }
                    if (split == null || widest == 0) break;

                    int channel = split.LongestChannel;
                    Comparison<ColourEntry> byChannel = (a, b) =>
                        channel == 0 ? a.R.CompareTo(b.R) : channel == 1 ? a.G.CompareTo(b.G) : a.B.CompareTo(b.B);
                    entries.Sort(split.Start, split.End - split.Start, Comparer<ColourEntry>.Create(byChannel));

                    long half = split.Count / 2;
                    long sum = 0;
                    int at = split.Start + 1;
                    for (int i = split.Start; i < split.End - 1; i++)
                    {
                        sum += entries[i].Count;
                        if (sum >= half) { at = i + 1; break; }
                    }
                    if (at <= split.Start) at = split.Start + 1;
                    if (at >= split.End) at = split.End - 1;

                    ColourBox left = new ColourBox(split.Start, at);
                    ColourBox right = new ColourBox(at, split.End);
                    left.Measure(entries);
                    right.Measure(entries);
                    boxes.Remove(split);
                    boxes.Add(left);
                    boxes.Add(right);
                }
            }

            // Index zero is transparent; every other entry is a box's weighted average colour.
            for (int i = 0; i < boxes.Count; i++)
            {
                ColourBox box = boxes[i];
                long r = 0, g = 0, b = 0, n = 0;
                for (int e = box.Start; e < box.End; e++)
                {
                    ColourEntry entry = entries[e];
                    r += (long)entry.R * entry.Count;
                    g += (long)entry.G * entry.Count;
                    b += (long)entry.B * entry.Count;
                    n += entry.Count;
                }
                int colour = (i + 1) * 3;
                if (n == 0) continue;
                palette[colour] = (byte)(r / n);
                palette[colour + 1] = (byte)(g / n);
                palette[colour + 2] = (byte)(b / n);
            }

            byte[] lookup = new byte[bins];
            for (int key = 0; key < bins; key++)
            {
                if (counts[key] == 0) continue;
                int r = ((key >> (ColourBits * 2)) & (ColourLevels - 1)) * 4 + 2;
                int g = ((key >> ColourBits) & (ColourLevels - 1)) * 4 + 2;
                int b = (key & (ColourLevels - 1)) * 4 + 2;
                int best = 1, bestDistance = int.MaxValue;
                for (int p = 0; p < boxes.Count; p++)
                {
                    int at = (p + 1) * 3;
                    int dr = r - palette[at], dg = g - palette[at + 1], db = b - palette[at + 2];
                    int distance = dr * dr + dg * dg + db * db;
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = p + 1;
                }
                lookup[key] = (byte)best;
            }
            return lookup;
        }

        private static void WriteHeader(Stream stream, int width, int height, byte[] palette)
        {
            WriteAscii(stream, "GIF89a");
            WriteLE16(stream, width);
            WriteLE16(stream, height);
            stream.WriteByte(0xF7);   // global colour table, 256 entries
            stream.WriteByte(TransparentIndex);
            stream.WriteByte(0);
            stream.Write(palette, 0, palette.Length);
        }

        private static void WriteLoopExtension(Stream stream)
        {
            stream.WriteByte(0x21);
            stream.WriteByte(0xFF);
            stream.WriteByte(0x0B);
            WriteAscii(stream, "NETSCAPE2.0");
            stream.WriteByte(0x03);
            stream.WriteByte(0x01);
            WriteLE16(stream, 0);     // forever
            stream.WriteByte(0);
        }

        private static void WriteFrame(Stream stream, RawImage frame, byte[] lookup, byte[] indices,
            int delayCentiseconds, bool transparent)
        {
            int bins = 1 << (ColourBits * 3);
            byte[] pixels = frame.Bgra;
            for (int p = 0; p < indices.Length; p++)
            {
                int i = p * 4;
                if (pixels[i + 3] < 128)
                {
                    indices[p] = TransparentIndex;
                    continue;
                }
                int key = ((pixels[i + 2] >> 2) << (ColourBits * 2))
                        | ((pixels[i + 1] >> 2) << ColourBits)
                        | (pixels[i] >> 2);
                indices[p] = lookup[key & (bins - 1)];
            }

            // Restore to background before the next frame, so transparent holes do not keep old pixels.
            stream.WriteByte(0x21);
            stream.WriteByte(0xF9);
            stream.WriteByte(0x04);
            stream.WriteByte((byte)((2 << 2) | (transparent ? 1 : 0)));
            WriteLE16(stream, delayCentiseconds);
            stream.WriteByte(TransparentIndex);
            stream.WriteByte(0);

            stream.WriteByte(0x2C);
            WriteLE16(stream, 0);
            WriteLE16(stream, 0);
            WriteLE16(stream, frame.Width);
            WriteLE16(stream, frame.Height);
            stream.WriteByte(0);

            WriteLzw(stream, indices);
        }

        /// <summary>GIF LZW with a 256-colour initial alphabet, split into sub-blocks of at most 255 bytes.</summary>
        private static void WriteLzw(Stream stream, byte[] indices)
        {
            const int MinCodeSize = 8;
            const int ClearCode = 1 << MinCodeSize;
            const int EndCode = ClearCode + 1;
            const int FirstCode = ClearCode + 2;

            List<byte> packed = new List<byte>(indices.Length / 2 + 16);
            int bitBuffer = 0, bits = 0;
            void Emit(int code, int size)
            {
                bitBuffer |= code << bits;
                bits += size;
                while (bits >= 8)
                {
                    packed.Add((byte)(bitBuffer & 0xFF));
                    bitBuffer >>= 8;
                    bits -= 8;
                }
            }

            Dictionary<int, int> table = new Dictionary<int, int>();
            int codeSize = MinCodeSize + 1;
            int next = FirstCode;
            Emit(ClearCode, codeSize);

            int prefix = indices.Length > 0 ? indices[0] : 0;
            for (int i = 1; i < indices.Length; i++)
            {
                int value = indices[i];
                int key = (prefix << 8) | value;
                if (table.TryGetValue(key, out int found))
                {
                    prefix = found;
                    continue;
                }

                Emit(prefix, codeSize);
                // The decoder adds its entry one code later than the encoder, so grow the width after
                // writing this code, not after adding its entry. This is the non-early-change GIF rule.
                if (next > (1 << codeSize) - 1 && codeSize < 12) codeSize++;
                if (next < 4096)
                {
                    table[key] = next++;
                }
                else
                {
                    Emit(ClearCode, codeSize);
                    table.Clear();
                    codeSize = MinCodeSize + 1;
                    next = FirstCode;
                }
                prefix = value;
            }

            if (indices.Length > 0) Emit(prefix, codeSize);
            // The decoder's table grows one code behind the encoder, so a trailing code can still widen
            // the final code before the end marker.
            if (next > (1 << codeSize) - 1 && codeSize < 12) codeSize++;
            Emit(EndCode, codeSize);
            if (bits > 0) packed.Add((byte)(bitBuffer & 0xFF));

            stream.WriteByte(MinCodeSize);
            for (int i = 0; i < packed.Count; i += 255)
            {
                int count = Math.Min(255, packed.Count - i);
                stream.WriteByte((byte)count);
                stream.Write(packed.GetRange(i, count).ToArray(), 0, count);
            }
            stream.WriteByte(0);
        }

        private static void WriteAscii(Stream stream, string text)
        {
            for (int i = 0; i < text.Length; i++) stream.WriteByte((byte)text[i]);
        }

        private static void WriteLE16(Stream stream, int value)
        {
            stream.WriteByte((byte)(value & 0xFF));
            stream.WriteByte((byte)((value >> 8) & 0xFF));
        }
    }
}
