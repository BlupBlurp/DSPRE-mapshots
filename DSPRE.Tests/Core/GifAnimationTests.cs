using System;
using System.Collections.Generic;
using DSPRE;
using Xunit;

namespace DSPRE.Tests.Core
{
    public sealed class GifAnimationTests
    {
        [Fact]
        public void EncodeWritesLoopingGifWithOneFramePerImage()
        {
            RawImage red = Solid(0, 0, 255, 255);
            RawImage blue = Solid(255, 0, 0, 255);
            List<RawImage> frames = new List<RawImage> { red, blue };

            byte[] gif = GifAnimation.Encode(frames, 3);
            GifFile parsed = GifFile.Parse(gif);

            Assert.Equal(2, parsed.Width);
            Assert.Equal(1, parsed.Height);
            Assert.Equal(2, parsed.Frames.Count);
            Assert.Equal(3, parsed.DelayCentiseconds);
            Assert.True(parsed.LoopsForever);

            // Two writes of the same red pixel collapse into one LZW code; both frames still decode.
            byte[] first = parsed.Frames[0].Indices;
            byte[] second = parsed.Frames[1].Indices;
            Assert.Equal(2, first.Length);
            Assert.Equal(2, second.Length);
            Assert.Equal(first[0], first[1]);
            Assert.Equal(second[0], second[1]);
            Assert.NotEqual(first[0], second[0]);
            Assert.InRange(parsed.Palette[first[0] * 3 + 2], 250, 255);   // red
            Assert.InRange(parsed.Palette[second[0] * 3], 250, 255);      // blue
        }

        [Fact]
        public void EncodeKeepsTransparentPixelsOnTheClearIndex()
        {
            RawImage frame = new RawImage(2, 1);
            frame.SetPixel(0, 0, 255, 0, 0, 255);
            frame.SetPixel(1, 0, 0, 0, 0, 0);

            GifFile parsed = GifFile.Parse(GifAnimation.Encode(new[] { frame }, 4));

            Assert.True(parsed.Transparent);
            Assert.Equal(0, parsed.TransparentIndex);
            Assert.Equal(0, parsed.Frames[0].Indices[1]);
            Assert.NotEqual(0, parsed.Frames[0].Indices[0]);
        }

        [Fact]
        public void EncodeRoundTripsLargeNoisyFrameThroughDictionaryResets()
        {
            const int width = 256, height = 192;
            RawImage frame = new RawImage(width, height);
            uint state = 0x51ED270Bu;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    state = state * 1664525u + 1013904223u;
                    bool hole = (x - width / 2) * (x - width / 2) + (y - height / 2) * (y - height / 2) < 24 * 24;
                    if (hole) frame.SetPixel(x, y, 0, 0, 0, 0);
                    else frame.SetPixel(x, y, (byte)(state >> 16), (byte)(state >> 8), (byte)state, 255);
                }

            GifFile parsed = GifFile.Parse(GifAnimation.Encode(new[] { frame }, 3));
            byte[] indices = parsed.Frames[0].Indices;

            Assert.Equal(width * height, indices.Length);
            Assert.True(parsed.Transparent);
            Assert.Equal(0, indices[(height / 2) * width + width / 2]);
            Assert.NotEqual(0, indices[0]);
            Assert.True(new HashSet<byte>(indices).Count > 100);
        }

        private static RawImage Solid(byte r, byte g, byte b, byte a)
        {
            RawImage image = new RawImage(2, 1);
            image.SetPixel(0, 0, r, g, b, a);
            image.SetPixel(1, 0, r, g, b, a);
            return image;
        }

        /// <summary>A small GIF89a reader, enough to exercise the encoder's structure and LZW stream.</summary>
        private sealed class GifFile
        {
            public int Width, Height;
            public byte[] Palette;
            public bool Transparent;
            public int TransparentIndex;
            public int DelayCentiseconds;
            public bool LoopsForever;

            public sealed class Frame
            {
                public byte[] Indices;
            }

            public List<Frame> Frames = new List<Frame>();

            public static GifFile Parse(byte[] data)
            {
                GifFile file = new GifFile();
                int at = 0;
                Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(data, at, 6));
                at += 6;
                file.Width = data[at] | data[at + 1] << 8;
                file.Height = data[at + 2] | data[at + 3] << 8;
                int packed = data[at + 4];
                at += 7;
                int entries = 1 << ((packed & 7) + 1);
                file.Palette = new byte[entries * 3];
                Array.Copy(data, at, file.Palette, 0, file.Palette.Length);
                at += file.Palette.Length;

                int gceFlags = 0, gceDelay = 0, gceTransparent = 0;
                while (at < data.Length)
                {
                    byte marker = data[at++];
                    if (marker == 0x3B) break;

                    if (marker == 0x21)
                    {
                        byte label = data[at++];
                        if (label == 0xF9)
                        {
                            int size = data[at++];
                            Assert.Equal(4, size);
                            gceFlags = data[at++];
                            gceDelay = data[at] | data[at + 1] << 8;
                            gceTransparent = data[at + 2];
                            at += 3;
                            at++;   // block terminator
                        }
                        else if (label == 0xFF)
                        {
                            int size = data[at++];
                            string app = System.Text.Encoding.ASCII.GetString(data, at, size);
                            at += size;
                            int sub = data[at++];
                            if (app == "NETSCAPE2.0")
                            {
                                Assert.Equal(3, sub);
                                Assert.Equal(1, data[at]);
                                file.LoopsForever = (data[at + 1] | data[at + 2] << 8) == 0;
                                at += 3;
                                at++;
                            }
                            else
                            {
                                at += sub;
                                at++;
                            }
                        }
                        else
                        {
                            while (true)
                            {
                                int size = data[at++];
                                if (size == 0) break;
                                at += size;
                            }
                        }
                        continue;
                    }

                    if (marker != 0x2C) throw new InvalidOperationException("Unexpected block " + marker);

                    int left = data[at] | data[at + 1] << 8;
                    int top = data[at + 2] | data[at + 3] << 8;
                    int width = data[at + 4] | data[at + 5] << 8;
                    int height = data[at + 6] | data[at + 7] << 8;
                    int imageFlags = data[at + 8];
                    at += 9;
                    if ((imageFlags & 0x80) != 0) at += 3 * (1 << ((imageFlags & 7) + 1));
                    Assert.Equal(0, left);
                    Assert.Equal(0, top);
                    Assert.Equal(file.Width, width);
                    Assert.Equal(file.Height, height);

                    file.Transparent = (gceFlags & 1) != 0;
                    file.TransparentIndex = gceTransparent;
                    file.DelayCentiseconds = gceDelay;

                    int minCodeSize = data[at++];
                    List<byte> stream = new List<byte>();
                    while (true)
                    {
                        int size = data[at++];
                        if (size == 0) break;
                        for (int i = 0; i < size; i++) stream.Add(data[at + i]);
                        at += size;
                    }
                    file.Frames.Add(new Frame { Indices = Decode(stream.ToArray(), minCodeSize) });
                }

                Assert.Equal(data.Length, at);
                return file;
            }

            private static byte[] Decode(byte[] data, int minCodeSize)
            {
                int clear = 1 << minCodeSize;
                int end = clear + 1;
                int codeSize = minCodeSize + 1;
                int next = clear + 2;
                Dictionary<int, byte[]> table = new Dictionary<int, byte[]>();
                for (int i = 0; i < clear; i++) table[i] = new[] { (byte)i };

                List<byte> output = new List<byte>();
                int bit = 0;
                byte[] previous = null;

                while (true)
                {
                    int code = 0;
                    for (int i = 0; i < codeSize; i++)
                    {
                        int index = bit >> 3;
                        if (index >= data.Length) throw new InvalidOperationException("LZW stream ended early.");
                        code |= ((data[index] >> (bit & 7)) & 1) << i;
                        bit++;
                    }

                    if (code == clear)
                    {
                        table.Clear();
                        for (int i = 0; i < clear; i++) table[i] = new[] { (byte)i };
                        codeSize = minCodeSize + 1;
                        next = clear + 2;
                        previous = null;
                        continue;
                    }
                    if (code == end) break;

                    byte[] entry;
                    if (code < next)
                    {
                        entry = table[code];
                    }
                    else if (code == next && previous != null)
                    {
                        entry = new byte[previous.Length + 1];
                        Array.Copy(previous, entry, previous.Length);
                        entry[previous.Length] = previous[0];
                    }
                    else throw new InvalidOperationException("Bad LZW code.");

                    output.AddRange(entry);
                    if (previous != null && next < 4096)
                    {
                        byte[] added = new byte[previous.Length + 1];
                        Array.Copy(previous, added, previous.Length);
                        added[previous.Length] = entry[0];
                        table[next++] = added;
                        if (next == 1 << codeSize && codeSize < 12) codeSize++;
                    }
                    previous = entry;
                }
                return output.ToArray();
            }
        }
    }
}
