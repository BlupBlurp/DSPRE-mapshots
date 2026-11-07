using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using DSPRE;
using Xunit;

namespace DSPRE.Tests.Core
{
    public sealed class AnimatedPngTests
    {
        [Fact]
        public void EncodeWritesLosslessFramesWithExactDelays()
        {
            RawImage first = Frame(255, 0, 0, 0, 255, 0, 0, 0, 255, 0);
            RawImage second = Frame(255, 255, 0, 0, 255, 255, 255, 0, 255, 64);

            byte[] png = AnimatedPng.Encode(new[] { first, second }, 2, 30);
            Assert.Equal(0x89, png[0]);
            Assert.Equal((byte)'P', png[1]);
            Assert.Equal((byte)'N', png[2]);
            Assert.Equal((byte)'G', png[3]);

            List<PngChunk> chunks = Parse(png);
            Assert.Equal("IHDR", chunks[0].Type);
            PngChunk ihdr = chunks[0];
            Assert.Equal(2, ReadBE32(ihdr.Data, 0));
            Assert.Equal(2, ReadBE32(ihdr.Data, 4));
            Assert.Equal(8, ihdr.Data[8]);
            Assert.Equal(6, ihdr.Data[9]);

            PngChunk actl = Find(chunks, "acTL");
            Assert.Equal(2, ReadBE32(actl.Data, 0));
            Assert.Equal(0, ReadBE32(actl.Data, 4));

            List<PngChunk> controls = FindAll(chunks, "fcTL");
            Assert.Equal(2, controls.Count);
            Assert.Equal(0, ReadBE32(controls[0].Data, 0));
            Assert.Equal(2, ReadBE32(controls[0].Data, 4));
            Assert.Equal(2, ReadBE32(controls[0].Data, 8));
            Assert.Equal(0, ReadBE32(controls[0].Data, 12));
            Assert.Equal(0, ReadBE32(controls[0].Data, 16));
            Assert.Equal(2, ReadBE16(controls[0].Data, 20));
            Assert.Equal(30, ReadBE16(controls[0].Data, 22));
            Assert.Equal(1, controls[0].Data[24]);
            Assert.Equal(0, controls[0].Data[25]);

            Assert.Equal(1, ReadBE32(controls[1].Data, 0));
            PngChunk idat = Find(chunks, "IDAT");
            PngChunk fdat = Find(chunks, "fdAT");
            Assert.Equal(2, ReadBE32(fdat.Data, 0));

            Assert.Equal(Scanlines(first), Inflate(idat.Data));
            byte[] secondData = new byte[fdat.Data.Length - 4];
            Array.Copy(fdat.Data, 4, secondData, 0, secondData.Length);
            Assert.Equal(Scanlines(second), Inflate(secondData));
            Assert.Equal("IEND", chunks[chunks.Count - 1].Type);
        }

        private static RawImage Frame(byte r0, byte g0, byte b0,
            byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, byte alpha2)
        {
            RawImage frame = new RawImage(2, 2);
            frame.SetPixel(0, 0, r0, g0, b0, 255);
            frame.SetPixel(1, 0, r1, g1, b1, 255);
            frame.SetPixel(0, 1, r2, g2, b2, alpha2);
            frame.SetPixel(1, 1, 0, 0, 0, 0);
            return frame;
        }

        private static byte[] Scanlines(RawImage frame)
        {
            byte[] raw = new byte[frame.Height * (1 + frame.Width * 4)];
            int p = 0;
            for (int y = 0; y < frame.Height; y++)
            {
                raw[p++] = 0;
                for (int x = 0; x < frame.Width; x++)
                {
                    int i = (y * frame.Width + x) * 4;
                    raw[p++] = frame.Bgra[i + 2];
                    raw[p++] = frame.Bgra[i + 1];
                    raw[p++] = frame.Bgra[i];
                    raw[p++] = frame.Bgra[i + 3];
                }
            }
            return raw;
        }

        private static byte[] Inflate(byte[] data)
        {
            using MemoryStream input = new MemoryStream(data);
            using ZLibStream z = new ZLibStream(input, CompressionMode.Decompress);
            using MemoryStream output = new MemoryStream();
            z.CopyTo(output);
            return output.ToArray();
        }

        private sealed class PngChunk
        {
            public string Type;
            public byte[] Data;
        }

        private static List<PngChunk> Parse(byte[] png)
        {
            List<PngChunk> chunks = new List<PngChunk>();
            int at = 8;
            while (at < png.Length)
            {
                int length = ReadBE32(png, at);
                string type = Encoding.ASCII.GetString(png, at + 4, 4);
                byte[] data = new byte[length];
                Array.Copy(png, at + 8, data, 0, length);
                chunks.Add(new PngChunk { Type = type, Data = data });
                at += 12 + length;
            }
            Assert.Equal(png.Length, at);
            return chunks;
        }

        private static PngChunk Find(List<PngChunk> chunks, string type)
        {
            foreach (PngChunk chunk in chunks) if (chunk.Type == type) return chunk;
            throw new InvalidOperationException("Missing chunk " + type);
        }

        private static List<PngChunk> FindAll(List<PngChunk> chunks, string type)
        {
            List<PngChunk> found = new List<PngChunk>();
            foreach (PngChunk chunk in chunks) if (chunk.Type == type) found.Add(chunk);
            return found;
        }

        private static int ReadBE32(byte[] data, int at)
        {
            return (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
        }

        private static int ReadBE16(byte[] data, int at)
        {
            return (data[at] << 8) | data[at + 1];
        }
    }
}
