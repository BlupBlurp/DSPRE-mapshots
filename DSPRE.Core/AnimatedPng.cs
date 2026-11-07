using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DSPRE
{
    /// <summary>
    /// Writes a lossless animated PNG from raw BGRA frames. APNG keeps every RGBA sample, so pixel
    /// art and the still PNG's transparency survive without the 256-colour limit of GIF.
    /// </summary>
    public static class AnimatedPng
    {
        /// <summary>One field step at 30 steps a second.</summary>
        public const int DefaultDelayNumerator = 1;
        public const int DefaultDelayDenominator = 30;

        public static byte[] Encode(IReadOnlyList<RawImage> frames,
            int delayNumerator = DefaultDelayNumerator, int delayDenominator = DefaultDelayDenominator)
        {
            if (frames == null || frames.Count == 0) throw new ArgumentException("At least one frame is needed.", nameof(frames));
            RawImage first = frames[0] ?? throw new ArgumentException("A frame is missing.", nameof(frames));
            if (first.Width <= 0 || first.Height <= 0) throw new ArgumentException("A frame is empty.", nameof(frames));
            foreach (RawImage frame in frames)
            {
                if (frame == null || frame.Width != first.Width || frame.Height != first.Height)
                    throw new ArgumentException("Every frame must have the same size.", nameof(frames));
                if (frame.Bgra == null || frame.Bgra.Length < frame.Width * frame.Height * 4)
                    throw new ArgumentException("A frame's pixels are incomplete.", nameof(frames));
            }

            int delayNum = Math.Clamp(delayNumerator, 1, 65535);
            int delayDen = Math.Clamp(delayDenominator, 1, 65535);

            using MemoryStream stream = new MemoryStream();
            stream.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            byte[] ihdr = new byte[13];
            WriteBE(ihdr, 0, first.Width);
            WriteBE(ihdr, 4, first.Height);
            ihdr[8] = 8;    // bit depth
            ihdr[9] = 6;    // colour type: RGBA
            ihdr[10] = 0;   // compression
            ihdr[11] = 0;   // filter
            ihdr[12] = 0;   // interlace
            WriteChunk(stream, "IHDR", ihdr);

            byte[] actl = new byte[8];
            WriteBE(actl, 0, frames.Count);
            WriteBE(actl, 4, 0);   // loop forever
            WriteChunk(stream, "acTL", actl);

            uint sequence = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                // Every frame covers the canvas and replaces its pixels. Disposing to background
                // keeps transparent areas from leaking into the next frame.
                byte[] control = new byte[26];
                WriteBE(control, 0, (int)sequence++);
                WriteBE(control, 4, first.Width);
                WriteBE(control, 8, first.Height);
                WriteBE(control, 12, 0);
                WriteBE(control, 16, 0);
                WriteBE16(control, 20, delayNum);
                WriteBE16(control, 22, delayDen);
                control[24] = 1;   // dispose: background
                control[25] = 0;   // blend: source
                WriteChunk(stream, "fcTL", control);

                byte[] compressed = Compress(frames[i]);
                if (i == 0)
                {
                    WriteChunk(stream, "IDAT", compressed);
                }
                else
                {
                    byte[] data = new byte[4 + compressed.Length];
                    WriteBE(data, 0, (int)sequence++);
                    Buffer.BlockCopy(compressed, 0, data, 4, compressed.Length);
                    WriteChunk(stream, "fdAT", data);
                }
            }

            WriteChunk(stream, "IEND", Array.Empty<byte>());
            return stream.ToArray();
        }

        /// <summary>RGBA scanlines with filter byte zero, zlib-compressed as APNG frame data.</summary>
        private static byte[] Compress(RawImage frame)
        {
            byte[] raw = new byte[frame.Height * (1 + frame.Width * 4)];
            int p = 0;
            for (int y = 0; y < frame.Height; y++)
            {
                raw[p++] = 0;
                for (int x = 0; x < frame.Width; x++)
                {
                    int i = (y * frame.Width + x) * 4;
                    raw[p++] = frame.Bgra[i + 2];   // R
                    raw[p++] = frame.Bgra[i + 1];   // G
                    raw[p++] = frame.Bgra[i];       // B
                    raw[p++] = frame.Bgra[i + 3];   // A
                }
            }

            using MemoryStream compressed = new MemoryStream();
            using (ZLibStream z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            return compressed.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] length = new byte[4];
            WriteBE(length, 0, data.Length);
            stream.Write(length, 0, 4);

            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes, 0, 4);
            stream.Write(data, 0, data.Length);

            byte[] crc = new byte[4];
            WriteBE(crc, 0, (int)Crc32(typeBytes, data));
            stream.Write(crc, 0, 4);
        }

        private static void WriteBE(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static void WriteBE16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 8);
            buffer[offset + 1] = (byte)value;
        }

        private static uint[] _crcTable;

        private static uint Crc32(byte[] type, byte[] data)
        {
            if (_crcTable == null)
            {
                _crcTable = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                    _crcTable[n] = c;
                }
            }
            uint crc = 0xFFFFFFFF;
            foreach (byte b in type) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (byte b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFF;
        }
    }
}
