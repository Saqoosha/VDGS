using System;

namespace VDGS.Vp8l
{
    public sealed class Vp8lException : Exception
    {
        public Vp8lException(string message) : base(message) { }
    }

    /// <summary>
    /// Pure C# decoder for simple-format lossless WebP (RIFF/WEBP/VP8L).
    /// Spec: RFC 9649 §3. No UnityEngine, no unsafe, no packages — so the same
    /// file links into both the BepInEx plugin (netstandard2.0) and xunit (net8).
    /// </summary>
    public static class Vp8lDecoder
    {
        /// <summary>
        /// RGBA8, row-major, top row first. Throws Vp8lException on anything but a
        /// simple-format lossless WebP ("RIFF....WEBPVP8L").
        /// </summary>
        public static byte[] Decode(byte[] webp, out int width, out int height)
        {
            if (webp == null) throw new Vp8lException("null input");
            var dec = new Decoder(webp);
            dec.Decode(out width, out height, out uint[] argb);
            var rgba = new byte[width * height * 4];
            int o = 0;
            for (int i = 0; i < argb.Length; i++)
            {
                uint p = argb[i];
                rgba[o++] = (byte)(p >> 16);
                rgba[o++] = (byte)(p >> 8);
                rgba[o++] = (byte)p;
                rgba[o++] = (byte)(p >> 24);
            }
            return rgba;
        }

        // --- constants -------------------------------------------------------

        const int NumLiteralCodes = 256;
        const int NumLengthCodes = 24;
        const int NumDistanceCodes = 40;
        const int NumCodeLengthCodes = 19;
        const int HuffmanTableBits = 8;
        const int HuffmanTableMask = (1 << HuffmanTableBits) - 1;
        const int LengthsTableBits = 7;
        const int MaxAllowedCodeLength = 15;
        const int CodeToPlaneCodes = 120;
        const uint ArgbBlack = 0xff000000u;
        const uint HashMul = 0x1e35a7bdu;

        static readonly int[] AlphabetSize = {
            NumLiteralCodes + NumLengthCodes, NumLiteralCodes, NumLiteralCodes,
            NumLiteralCodes, NumDistanceCodes
        };

        static readonly byte[] CodeLengthCodeOrder = {
            17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15
        };

        static readonly byte[] CodeLengthExtraBits = { 2, 3, 7 };
        static readonly byte[] CodeLengthRepeatOffsets = { 3, 3, 11 };

        // Packed as (y_offset << 4) | (8 - x_offset). Same table as libwebp.
        static readonly byte[] CodeToPlane = {
            0x18, 0x07, 0x17, 0x19, 0x28, 0x06, 0x27, 0x29, 0x16, 0x1a, 0x26, 0x2a,
            0x38, 0x05, 0x37, 0x39, 0x15, 0x1b, 0x36, 0x3a, 0x25, 0x2b, 0x48, 0x04,
            0x47, 0x49, 0x14, 0x1c, 0x35, 0x3b, 0x46, 0x4a, 0x24, 0x2c, 0x58, 0x45,
            0x4b, 0x34, 0x3c, 0x03, 0x57, 0x59, 0x13, 0x1d, 0x56, 0x5a, 0x23, 0x2d,
            0x44, 0x4c, 0x55, 0x5b, 0x33, 0x3d, 0x68, 0x02, 0x67, 0x69, 0x12, 0x1e,
            0x66, 0x6a, 0x22, 0x2e, 0x54, 0x5c, 0x43, 0x4d, 0x65, 0x6b, 0x32, 0x3e,
            0x78, 0x01, 0x77, 0x79, 0x53, 0x5d, 0x11, 0x1f, 0x64, 0x6c, 0x42, 0x4e,
            0x76, 0x7a, 0x21, 0x2f, 0x75, 0x7b, 0x31, 0x3f, 0x63, 0x6d, 0x52, 0x5e,
            0x00, 0x74, 0x7c, 0x41, 0x4f, 0x10, 0x20, 0x62, 0x6e, 0x30, 0x73, 0x7d,
            0x51, 0x5f, 0x40, 0x72, 0x7e, 0x61, 0x6f, 0x50, 0x71, 0x7f, 0x60, 0x70
        };

        enum TransformType { Predictor = 0, CrossColor = 1, SubtractGreen = 2, ColorIndexing = 3 }

        struct HuffmanCode
        {
            public byte Bits;
            public ushort Value;
        }

        sealed class HTreeGroup
        {
            public HuffmanCode[][] Trees = new HuffmanCode[5][];
        }

        sealed class Transform
        {
            public TransformType Type;
            public int Bits;
            public int XSize;
            public int YSize;
            public uint[] Data;
        }

        // --- bit reader ------------------------------------------------------

        sealed class BitReader
        {
            readonly byte[] m_Buf;
            int m_Pos; // bit index into m_Buf

            public BitReader(byte[] buf, int byteOffset)
            {
                m_Buf = buf;
                m_Pos = byteOffset * 8;
            }

            public int BitPos => m_Pos;

            public uint ReadBits(int n)
            {
                if (n == 0) return 0;
                if (n > 24) throw new Vp8lException("read > 24 bits");
                uint v = 0;
                for (int i = 0; i < n; i++)
                {
                    int byteIndex = m_Pos >> 3;
                    if (byteIndex >= m_Buf.Length) throw new Vp8lException("truncated bitstream");
                    v |= (uint)((m_Buf[byteIndex] >> (m_Pos & 7)) & 1) << i;
                    m_Pos++;
                }
                return v;
            }

            // Peek enough bits for Huffman lookup without advancing.
            public uint Prefetch()
            {
                int byteIndex = m_Pos >> 3;
                int bit = m_Pos & 7;
                uint v = 0;
                // Up to 4 bytes is enough for a 15-bit max code.
                for (int i = 0; i < 4; i++)
                {
                    int idx = byteIndex + i;
                    if (idx >= m_Buf.Length) break;
                    v |= (uint)m_Buf[idx] << (8 * i);
                }
                return v >> bit;
            }

            public void Advance(int n) { m_Pos += n; }
        }

        // --- decoder body ----------------------------------------------------

        sealed class Decoder
        {
            readonly byte[] m_Data;
            BitReader m_Br;
            int m_Width, m_Height;
            int m_WorkingWidth;
            readonly Transform[] m_Transforms = new Transform[4];
            int m_TransformCount;
            uint m_TransformsSeen;

            HTreeGroup[] m_Groups;
            int[] m_HuffmanImage; // meta group index per tile, or null
            int m_HuffmanBits;
            int m_HuffmanXSize;
            int m_ColorCacheBits;
            uint[] m_ColorCache;

            public Decoder(byte[] data) { m_Data = data; }

            public void Decode(out int width, out int height, out uint[] argb)
            {
                int vp8lOffset = FindVp8lPayload(out int payloadLen);
                if (vp8lOffset + payloadLen > m_Data.Length)
                    throw new Vp8lException("VP8L chunk length past end of file");

                m_Br = new BitReader(m_Data, vp8lOffset);
                if (m_Br.ReadBits(8) != 0x2f) throw new Vp8lException("bad VP8L signature");
                m_Width = (int)m_Br.ReadBits(14) + 1;
                m_Height = (int)m_Br.ReadBits(14) + 1;
                m_Br.ReadBits(1); // alpha_is_used hint
                if (m_Br.ReadBits(3) != 0) throw new Vp8lException("unsupported VP8L version");

                m_WorkingWidth = m_Width;
                DecodeImageStream(m_Width, m_Height, isLevel0: true, out argb);
                width = m_Width;
                height = m_Height;
            }

            int FindVp8lPayload(out int payloadLen)
            {
                if (m_Data.Length < 16) throw new Vp8lException("file too short");
                if (ReadFourCC(0) != 0x46464952u /* RIFF */) throw new Vp8lException("not RIFF");
                if (ReadFourCC(8) != 0x50424557u /* WEBP */) throw new Vp8lException("not WEBP");

                int offset = 12;
                while (offset + 4 <= m_Data.Length)
                {
                    uint fourcc = ReadFourCC(offset);
                    // FourCC alone is enough to reject lossy/extended; size may be absent
                    // in the unit-test stub that only carries "VP8 ".
                    if (fourcc == 0x20385056u /* "VP8 " */)
                        throw new Vp8lException("unsupported webp chunk: VP8 ");
                    if (fourcc == 0x58385056u /* VP8X */)
                        throw new Vp8lException("unsupported webp chunk: VP8X");
                    if (offset + 8 > m_Data.Length) break;
                    int size = m_Data[offset + 4] | (m_Data[offset + 5] << 8)
                             | (m_Data[offset + 6] << 16) | (m_Data[offset + 7] << 24);
                    if (size < 0) throw new Vp8lException("negative chunk size");
                    offset += 8;
                    if (fourcc == 0x4C385056u /* VP8L */)
                    {
                        payloadLen = size;
                        return offset;
                    }
                    offset += size + (size & 1); // pad to even
                }
                throw new Vp8lException("no VP8L chunk");
            }

            uint ReadFourCC(int o) =>
                (uint)(m_Data[o] | (m_Data[o + 1] << 8) | (m_Data[o + 2] << 16) | (m_Data[o + 3] << 24));

            void DecodeImageStream(int xsize, int ysize, bool isLevel0, out uint[] pixels)
            {
                int transformXSize = xsize;
                int transformYSize = ysize;

                if (isLevel0)
                {
                    while (m_Br.ReadBits(1) != 0)
                        ReadTransform(ref transformXSize, transformYSize);
                }

                int colorCacheBits = 0;
                if (m_Br.ReadBits(1) != 0)
                {
                    colorCacheBits = (int)m_Br.ReadBits(4);
                    if (colorCacheBits < 1 || colorCacheBits > 11)
                        throw new Vp8lException("bad color cache size");
                }

                ReadHuffmanCodes(transformXSize, transformYSize, colorCacheBits, allowMeta: isLevel0);

                m_ColorCacheBits = colorCacheBits;
                m_ColorCache = colorCacheBits > 0 ? new uint[1 << colorCacheBits] : null;
                m_WorkingWidth = transformXSize;

                pixels = new uint[transformXSize * transformYSize];
                DecodeImageData(pixels, transformXSize, transformYSize);

                if (isLevel0)
                    ApplyInverseTransforms(ref pixels, transformXSize, transformYSize);
            }

            void ReadTransform(ref int xsize, int ysize)
            {
                var type = (TransformType)m_Br.ReadBits(2);
                uint bit = 1u << (int)type;
                if ((m_TransformsSeen & bit) != 0)
                    throw new Vp8lException("duplicate transform");
                m_TransformsSeen |= bit;

                var tr = new Transform { Type = type, XSize = xsize, YSize = ysize };
                switch (type)
                {
                    case TransformType.Predictor:
                    case TransformType.CrossColor:
                        tr.Bits = 2 + (int)m_Br.ReadBits(3);
                        DecodeImageStream(
                            SubSampleSize(tr.XSize, tr.Bits),
                            SubSampleSize(tr.YSize, tr.Bits),
                            isLevel0: false, out tr.Data);
                        break;
                    case TransformType.ColorIndexing:
                    {
                        int numColors = (int)m_Br.ReadBits(8) + 1;
                        int bits = numColors > 16 ? 0 : numColors > 4 ? 1 : numColors > 2 ? 2 : 3;
                        tr.Bits = bits;
                        xsize = SubSampleSize(tr.XSize, bits);
                        DecodeImageStream(numColors, 1, isLevel0: false, out uint[] table);
                        tr.Data = ExpandColorMap(numColors, bits, table);
                        break;
                    }
                    case TransformType.SubtractGreen:
                        break;
                }
                m_Transforms[m_TransformCount++] = tr;
            }

            static uint[] ExpandColorMap(int numColors, int bits, uint[] table)
            {
                int finalNum = 1 << (8 >> bits);
                var map = new uint[finalNum];
                map[0] = table[0];
                // Palette is stored as deltas; unfold, then pad with transparent black.
                byte[] src = new byte[numColors * 4];
                for (int i = 0; i < numColors; i++)
                {
                    uint c = table[i];
                    src[i * 4] = (byte)c;
                    src[i * 4 + 1] = (byte)(c >> 8);
                    src[i * 4 + 2] = (byte)(c >> 16);
                    src[i * 4 + 3] = (byte)(c >> 24);
                }
                byte[] dst = new byte[finalNum * 4];
                for (int i = 0; i < 4; i++) dst[i] = src[i];
                for (int i = 4; i < 4 * numColors; i++)
                    dst[i] = (byte)(src[i] + dst[i - 4]);
                for (int i = 0; i < finalNum; i++)
                    map[i] = (uint)(dst[i * 4] | (dst[i * 4 + 1] << 8) | (dst[i * 4 + 2] << 16) | (dst[i * 4 + 3] << 24));
                return map;
            }

            void ReadHuffmanCodes(int xsize, int ysize, int colorCacheBits, bool allowMeta)
            {
                int numGroups = 1;
                m_HuffmanImage = null;
                m_HuffmanBits = 0;
                m_HuffmanXSize = 1;

                if (allowMeta && m_Br.ReadBits(1) != 0)
                {
                    // Keep bits in locals until after the recursive decode: nested
                    // ReadHuffmanCodes resets the instance fields to the no-meta defaults.
                    int huffBits = 2 + (int)m_Br.ReadBits(3);
                    int hx = SubSampleSize(xsize, huffBits);
                    int hy = SubSampleSize(ysize, huffBits);
                    DecodeImageStream(hx, hy, isLevel0: false, out uint[] entropy);
                    m_HuffmanBits = huffBits;
                    m_HuffmanXSize = hx;
                    m_HuffmanImage = new int[entropy.Length];
                    int maxGroup = 0;
                    for (int i = 0; i < entropy.Length; i++)
                    {
                        int g = (int)((entropy[i] >> 8) & 0xffff);
                        m_HuffmanImage[i] = g;
                        if (g > maxGroup) maxGroup = g;
                    }
                    numGroups = maxGroup + 1;
                }

                m_Groups = new HTreeGroup[numGroups];
                int maxAlphabet = AlphabetSize[0] + (colorCacheBits > 0 ? 1 << colorCacheBits : 0);
                var codeLengths = new int[maxAlphabet];

                for (int g = 0; g < numGroups; g++)
                {
                    var group = new HTreeGroup();
                    for (int t = 0; t < 5; t++)
                    {
                        int alphabet = AlphabetSize[t];
                        if (t == 0 && colorCacheBits > 0) alphabet += 1 << colorCacheBits;
                        Array.Clear(codeLengths, 0, codeLengths.Length);
                        ReadCodeLengths(alphabet, codeLengths);
                        group.Trees[t] = BuildHuffmanTable(codeLengths, alphabet);
                    }
                    m_Groups[g] = group;
                }
            }

            void ReadCodeLengths(int alphabetSize, int[] codeLengths)
            {
                if (m_Br.ReadBits(1) != 0)
                {
                    // Simple code length code.
                    int numSymbols = (int)m_Br.ReadBits(1) + 1;
                    int first8 = (int)m_Br.ReadBits(1);
                    int symbol0 = (int)m_Br.ReadBits(1 + 7 * first8);
                    if (symbol0 < alphabetSize) codeLengths[symbol0] = 1;
                    if (numSymbols == 2)
                    {
                        int symbol1 = (int)m_Br.ReadBits(8);
                        if (symbol1 < alphabetSize) codeLengths[symbol1] = 1;
                    }
                    return;
                }

                int numCodes = 4 + (int)m_Br.ReadBits(4);
                var clCodeLengths = new int[NumCodeLengthCodes];
                for (int i = 0; i < numCodes; i++)
                    clCodeLengths[CodeLengthCodeOrder[i]] = (int)m_Br.ReadBits(3);

                var clTable = BuildHuffmanTable(clCodeLengths, NumCodeLengthCodes);

                int maxSymbol;
                if (m_Br.ReadBits(1) != 0)
                {
                    int lengthNBits = 2 + 2 * (int)m_Br.ReadBits(3);
                    maxSymbol = 2 + (int)m_Br.ReadBits(lengthNBits);
                    if (maxSymbol > alphabetSize) throw new Vp8lException("max_symbol too large");
                }
                else maxSymbol = alphabetSize;

                int symbol = 0;
                int prev = 8;
                int remaining = maxSymbol;
                while (symbol < alphabetSize)
                {
                    if (remaining-- == 0) break;
                    int codeLen = ReadSymbol(clTable);
                    if (codeLen < 16)
                    {
                        codeLengths[symbol++] = codeLen;
                        if (codeLen != 0) prev = codeLen;
                    }
                    else
                    {
                        int slot = codeLen - 16;
                        int extra = (int)m_Br.ReadBits(CodeLengthExtraBits[slot]);
                        int repeat = extra + CodeLengthRepeatOffsets[slot];
                        if (symbol + repeat > alphabetSize)
                            throw new Vp8lException("code length repeat overflow");
                        int length = codeLen == 16 ? prev : 0;
                        while (repeat-- > 0) codeLengths[symbol++] = length;
                    }
                }
            }

            static HuffmanCode[] BuildHuffmanTable(int[] codeLengths, int size)
            {
                var count = new int[MaxAllowedCodeLength + 1];
                for (int s = 0; s < size; s++)
                {
                    int len = codeLengths[s];
                    if (len > MaxAllowedCodeLength) throw new Vp8lException("code length too long");
                    count[len]++;
                }
                if (count[0] == size) throw new Vp8lException("empty Huffman tree");

                var sorted = new ushort[size];
                var offset = new int[MaxAllowedCodeLength + 1];
                offset[1] = 0;
                for (int len = 1; len < MaxAllowedCodeLength; len++)
                {
                    if (count[len] > (1 << len)) throw new Vp8lException("over-subscribed Huffman tree");
                    offset[len + 1] = offset[len] + count[len];
                }
                for (int s = 0; s < size; s++)
                {
                    int len = codeLengths[s];
                    if (len > 0) sorted[offset[len]++] = (ushort)s;
                }

                int totalNonZero = offset[MaxAllowedCodeLength];
                // Measure then fill. Root is 256 entries; 2nd-level tables trail it.
                int totalSize = MeasureHuffmanTable(count, totalNonZero);
                var table = new HuffmanCode[totalSize];
                FillHuffmanTable(table, codeLengths, size, sorted, totalNonZero);
                return table;
            }

            static int MeasureHuffmanTable(int[] countIn, int totalNonZero)
            {
                var count = (int[])countIn.Clone();
                if (totalNonZero == 1) return 1 << HuffmanTableBits;

                int totalSize = 1 << HuffmanTableBits;
                int numOpen = 1;
                for (int len = 1; len <= HuffmanTableBits; len++)
                {
                    numOpen <<= 1;
                    numOpen -= count[len];
                    if (numOpen < 0) throw new Vp8lException("bad Huffman tree");
                }
                uint key = 0;
                uint mask = (uint)totalSize - 1;
                uint low = 0xffffffffu;
                int tableBits = HuffmanTableBits;
                int tableSize = 1 << tableBits;
                var count2 = (int[])count.Clone();
                // Replay 2nd-level sizing without writing.
                for (int len = 1; len <= HuffmanTableBits; len++)
                {
                    for (int c = count[len]; c > 0; c--)
                        key = GetNextKey(key, len);
                }
                for (int len = HuffmanTableBits + 1; len <= MaxAllowedCodeLength; len++)
                {
                    numOpen <<= 1;
                    numOpen -= count2[len];
                    if (numOpen < 0) throw new Vp8lException("bad Huffman tree");
                    for (; count2[len] > 0; count2[len]--)
                    {
                        if ((key & mask) != low)
                        {
                            tableBits = NextTableBitSize(count2, len);
                            tableSize = 1 << tableBits;
                            totalSize += tableSize;
                            low = key & mask;
                        }
                        key = GetNextKey(key, len);
                    }
                }
                return totalSize;
            }

            static void FillHuffmanTable(HuffmanCode[] table, int[] codeLengths, int size,
                                         ushort[] sorted, int totalNonZero)
            {
                var count = new int[MaxAllowedCodeLength + 1];
                for (int s = 0; s < size; s++) count[codeLengths[s]]++;

                int rootSize = 1 << HuffmanTableBits;
                if (totalNonZero == 1)
                {
                    var code = new HuffmanCode { Bits = 0, Value = sorted[0] };
                    for (int i = 0; i < rootSize; i++) table[i] = code;
                    return;
                }

                int tableOffset = 0;
                int tableBits = HuffmanTableBits;
                int tableSize = rootSize;
                uint key = 0;
                uint mask = (uint)rootSize - 1;
                uint low = 0xffffffffu;
                int symbol = 0;

                for (int len = 1, stepBits = 2; len <= HuffmanTableBits; len++, stepBits <<= 1)
                {
                    for (; count[len] > 0; count[len]--)
                    {
                        var code = new HuffmanCode { Bits = (byte)len, Value = sorted[symbol++] };
                        Replicate(table, tableOffset + (int)key, stepBits, tableSize, code);
                        key = GetNextKey(key, len);
                    }
                }

                for (int len = HuffmanTableBits + 1, stepBits = 2; len <= MaxAllowedCodeLength; len++, stepBits <<= 1)
                {
                    for (; count[len] > 0; count[len]--)
                    {
                        if ((key & mask) != low)
                        {
                            tableOffset += tableSize;
                            tableBits = NextTableBitSize(count, len);
                            tableSize = 1 << tableBits;
                            low = key & mask;
                            table[low] = new HuffmanCode
                            {
                                Bits = (byte)(tableBits + HuffmanTableBits),
                                Value = (ushort)(tableOffset - (int)low)
                            };
                        }
                        var code = new HuffmanCode
                        {
                            Bits = (byte)(len - HuffmanTableBits),
                            Value = sorted[symbol++]
                        };
                        Replicate(table, tableOffset + (int)(key >> HuffmanTableBits), stepBits, tableSize, code);
                        key = GetNextKey(key, len);
                    }
                }
            }

            static int NextTableBitSize(int[] count, int len)
            {
                int left = 1 << (len - HuffmanTableBits);
                while (len < MaxAllowedCodeLength)
                {
                    left -= count[len];
                    if (left <= 0) break;
                    ++len;
                    left <<= 1;
                }
                return len - HuffmanTableBits;
            }

            static uint GetNextKey(uint key, int len)
            {
                uint step = 1u << (len - 1);
                while ((key & step) != 0) step >>= 1;
                return step != 0 ? (key & (step - 1)) + step : key;
            }

            static void Replicate(HuffmanCode[] table, int start, int step, int end, HuffmanCode code)
            {
                int e = end;
                do
                {
                    e -= step;
                    table[start + e] = code;
                } while (e > 0);
            }

            int ReadSymbol(HuffmanCode[] table)
            {
                uint val = m_Br.Prefetch();
                uint idx = val & HuffmanTableMask;
                var entry = table[idx];
                int nbits = entry.Bits - HuffmanTableBits;
                if (nbits > 0)
                {
                    // Value is an offset from this root slot to the 2nd-level table start
                    // (libwebp: table += table->value), so add idx back in.
                    m_Br.Advance(HuffmanTableBits);
                    val = m_Br.Prefetch();
                    entry = table[idx + entry.Value + (val & ((1u << nbits) - 1))];
                }
                m_Br.Advance(entry.Bits);
                return entry.Value;
            }

            void DecodeImageData(uint[] data, int width, int height)
            {
                int total = width * height;
                int src = 0;
                int lastCached = 0;
                int lenCodeLimit = NumLiteralCodes + NumLengthCodes;
                int colorCacheLimit = lenCodeLimit + (m_ColorCache != null ? m_ColorCache.Length : 0);
                int mask = m_HuffmanBits == 0 ? ~0 : (1 << m_HuffmanBits) - 1;
                int row = 0, col = 0;
                HTreeGroup group = GroupFor(col, row);

                while (src < total)
                {
                    if ((col & mask) == 0) group = GroupFor(col, row);

                    int code = ReadSymbol(group.Trees[0]);
                    if (code < NumLiteralCodes)
                    {
                        int red = ReadSymbol(group.Trees[1]);
                        int blue = ReadSymbol(group.Trees[2]);
                        int alpha = ReadSymbol(group.Trees[3]);
                        data[src] = ((uint)alpha << 24) | ((uint)red << 16) | ((uint)code << 8) | (uint)blue;
                        src++;
                        col++;
                        if (col >= width)
                        {
                            col = 0;
                            row++;
                            FlushCache(data, ref lastCached, src);
                        }
                    }
                    else if (code < lenCodeLimit)
                    {
                        int length = GetCopyDistance(code - NumLiteralCodes);
                        int distSymbol = ReadSymbol(group.Trees[4]);
                        int distCode = GetCopyDistance(distSymbol);
                        int dist = PlaneCodeToDistance(width, distCode);
                        if (src < dist || src + length > total)
                            throw new Vp8lException("LZ77 out of range");
                        for (int i = 0; i < length; i++)
                            data[src + i] = data[src + i - dist];
                        src += length;
                        col += length;
                        while (col >= width)
                        {
                            col -= width;
                            row++;
                        }
                        if ((col & mask) != 0) group = GroupFor(col, row);
                        FlushCache(data, ref lastCached, src);
                    }
                    else if (code < colorCacheLimit)
                    {
                        FlushCache(data, ref lastCached, src);
                        data[src] = m_ColorCache[code - lenCodeLimit];
                        src++;
                        col++;
                        if (col >= width)
                        {
                            col = 0;
                            row++;
                            FlushCache(data, ref lastCached, src);
                        }
                    }
                    else throw new Vp8lException("bad Huffman symbol");
                }
                FlushCache(data, ref lastCached, src);
            }

            HTreeGroup GroupFor(int x, int y)
            {
                if (m_HuffmanImage == null) return m_Groups[0];
                int idx = (y >> m_HuffmanBits) * m_HuffmanXSize + (x >> m_HuffmanBits);
                return m_Groups[m_HuffmanImage[idx]];
            }

            void FlushCache(uint[] data, ref int lastCached, int src)
            {
                if (m_ColorCache == null) { lastCached = src; return; }
                int shift = 32 - m_ColorCacheBits;
                while (lastCached < src)
                {
                    uint c = data[lastCached++];
                    m_ColorCache[(int)((c * HashMul) >> shift)] = c;
                }
            }

            int GetCopyDistance(int symbol)
            {
                if (symbol < 4) return symbol + 1;
                int extraBits = (symbol - 2) >> 1;
                int offset = (2 + (symbol & 1)) << extraBits;
                return offset + (int)m_Br.ReadBits(extraBits) + 1;
            }

            static int PlaneCodeToDistance(int xsize, int planeCode)
            {
                if (planeCode > CodeToPlaneCodes) return planeCode - CodeToPlaneCodes;
                int distCode = CodeToPlane[planeCode - 1];
                int yoffset = distCode >> 4;
                int xoffset = 8 - (distCode & 0xf);
                int dist = yoffset * xsize + xoffset;
                return dist >= 1 ? dist : 1;
            }

            void ApplyInverseTransforms(ref uint[] pixels, int encodedWidth, int height)
            {
                int w = encodedWidth;
                for (int n = m_TransformCount - 1; n >= 0; n--)
                {
                    var tr = m_Transforms[n];
                    switch (tr.Type)
                    {
                        case TransformType.SubtractGreen:
                            AddGreenToBlueAndRed(pixels, w * height);
                            break;
                        case TransformType.Predictor:
                            PredictorInverse(tr, pixels, w, height);
                            break;
                        case TransformType.CrossColor:
                            ColorSpaceInverse(tr, pixels, w, height);
                            break;
                        case TransformType.ColorIndexing:
                        {
                            var expanded = new uint[tr.XSize * height];
                            ColorIndexInverse(tr, pixels, expanded, w, height);
                            pixels = expanded;
                            w = tr.XSize;
                            break;
                        }
                    }
                }
                if (w != m_Width) throw new Vp8lException("width mismatch after transforms");
            }

            static void AddGreenToBlueAndRed(uint[] pix, int n)
            {
                for (int i = 0; i < n; i++)
                {
                    uint argb = pix[i];
                    uint green = (argb >> 8) & 0xff;
                    uint redBlue = argb & 0x00ff00ffu;
                    redBlue += (green << 16) | green;
                    redBlue &= 0x00ff00ffu;
                    pix[i] = (argb & 0xff00ff00u) | redBlue;
                }
            }

            static void ColorSpaceInverse(Transform tr, uint[] pix, int width, int height)
            {
                int tile = 1 << tr.Bits;
                int tilesPerRow = SubSampleSize(width, tr.Bits);
                for (int y = 0; y < height; y++)
                {
                    int predRow = (y >> tr.Bits) * tilesPerRow;
                    for (int x = 0; x < width; )
                    {
                        uint colorCode = tr.Data[predRow + (x >> tr.Bits)];
                        byte g2r = (byte)colorCode;
                        byte g2b = (byte)(colorCode >> 8);
                        byte r2b = (byte)(colorCode >> 16);
                        int xEnd = (x & ~(tile - 1)) + tile;
                        if (xEnd > width) xEnd = width;
                        for (; x < xEnd; x++)
                        {
                            int i = y * width + x;
                            uint argb = pix[i];
                            sbyte green = (sbyte)(argb >> 8);
                            int newRed = (int)((argb >> 16) & 0xff);
                            int newBlue = (int)(argb & 0xff);
                            newRed = (newRed + ColorTransformDelta((sbyte)g2r, green)) & 0xff;
                            newBlue = (newBlue + ColorTransformDelta((sbyte)g2b, green)) & 0xff;
                            newBlue = (newBlue + ColorTransformDelta((sbyte)r2b, (sbyte)newRed)) & 0xff;
                            pix[i] = (argb & 0xff00ff00u) | ((uint)newRed << 16) | (uint)newBlue;
                        }
                    }
                }
            }

            static int ColorTransformDelta(sbyte t, sbyte c) => ((int)t * c) >> 5;

            static void ColorIndexInverse(Transform tr, uint[] src, uint[] dst, int srcWidth, int height)
            {
                int bitsPerPixel = 8 >> tr.Bits;
                int width = tr.XSize;
                uint[] map = tr.Data;
                if (bitsPerPixel < 8)
                {
                    int pixelsPerByte = 1 << tr.Bits;
                    uint bitMask = (1u << bitsPerPixel) - 1;
                    int si = 0, di = 0;
                    for (int y = 0; y < height; y++)
                    {
                        int x = 0;
                        for (; x + pixelsPerByte <= width; x += pixelsPerByte)
                        {
                            uint packed = (src[si++] >> 8) & 0xff;
                            for (int k = 0; k < pixelsPerByte; k++)
                            {
                                dst[di++] = map[packed & bitMask];
                                packed >>= bitsPerPixel;
                            }
                        }
                        if (x < width)
                        {
                            uint packed = (src[si++] >> 8) & 0xff;
                            for (; x < width; x++)
                            {
                                dst[di++] = map[packed & bitMask];
                                packed >>= bitsPerPixel;
                            }
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < srcWidth * height; i++)
                        dst[i] = map[(src[i] >> 8) & 0xff];
                }
            }

            static void PredictorInverse(Transform tr, uint[] pix, int width, int height)
            {
                // First pixel: black. Rest of first row: left.
                pix[0] = AddPixels(pix[0], ArgbBlack);
                for (int x = 1; x < width; x++)
                    pix[x] = AddPixels(pix[x], pix[x - 1]);

                int tile = 1 << tr.Bits;
                int mask = tile - 1;
                int tilesPerRow = SubSampleSize(width, tr.Bits);

                for (int y = 1; y < height; y++)
                {
                    int row = y * width;
                    int predRow = (y >> tr.Bits) * tilesPerRow;
                    // Leftmost: top.
                    pix[row] = AddPixels(pix[row], pix[row - width]);

                    for (int x = 1; x < width; )
                    {
                        int mode = (int)((tr.Data[predRow + (x >> tr.Bits)] >> 8) & 0xf);
                        int xEnd = (x & ~mask) + tile;
                        if (xEnd > width) xEnd = width;
                        for (; x < xEnd; x++)
                        {
                            int i = row + x;
                            uint left = pix[i - 1];
                            uint pred = Predict(mode, left, pix, i - width);
                            pix[i] = AddPixels(pix[i], pred);
                        }
                    }
                }
            }

            static uint Predict(int mode, uint left, uint[] pix, int topIndex)
            {
                // TR is always topIndex+1: on the rightmost column that lands on the
                // leftmost pixel of the current row (already decoded), matching the border rule.
                uint t = pix[topIndex], tl = pix[topIndex - 1], tr = pix[topIndex + 1];
                switch (mode)
                {
                    case 0: return ArgbBlack;
                    case 1: return left;
                    case 2: return t;
                    case 3: return tr;
                    case 4: return tl;
                    case 5: return Average3(left, t, tr);
                    case 6: return Average2(left, tl);
                    case 7: return Average2(left, t);
                    case 8: return Average2(tl, t);
                    case 9: return Average2(t, tr);
                    case 10: return Average2(Average2(left, tl), Average2(t, tr));
                    case 11: return Select(t, left, tl);
                    case 12: return ClampedAddSubtractFull(left, t, tl);
                    case 13: return ClampedAddSubtractHalf(left, t, tl);
                    default: return ArgbBlack;
                }
            }

            static uint AddPixels(uint a, uint b)
            {
                uint ag = (a & 0xff00ff00u) + (b & 0xff00ff00u);
                uint rb = (a & 0x00ff00ffu) + (b & 0x00ff00ffu);
                return (ag & 0xff00ff00u) | (rb & 0x00ff00ffu);
            }

            static uint Average2(uint a, uint b) => (((a ^ b) & 0xfefefefeu) >> 1) + (a & b);
            static uint Average3(uint a, uint b, uint c) => Average2(Average2(a, c), b);

            static uint Select(uint a, uint b, uint c)
            {
                // Same as libwebp: returns a (T) when closer-or-tie, else b (L).
                int paMinusPb =
                    Sub3((int)(a >> 24), (int)(b >> 24), (int)(c >> 24)) +
                    Sub3((int)((a >> 16) & 0xff), (int)((b >> 16) & 0xff), (int)((c >> 16) & 0xff)) +
                    Sub3((int)((a >> 8) & 0xff), (int)((b >> 8) & 0xff), (int)((c >> 8) & 0xff)) +
                    Sub3((int)(a & 0xff), (int)(b & 0xff), (int)(c & 0xff));
                return paMinusPb <= 0 ? a : b;
            }

            static int Sub3(int a, int b, int c)
            {
                int pb = b - c, pa = a - c;
                return Math.Abs(pb) - Math.Abs(pa);
            }

            static uint ClampedAddSubtractFull(uint c0, uint c1, uint c2)
            {
                return ((uint)Clip255((int)(c0 >> 24) + (int)(c1 >> 24) - (int)(c2 >> 24)) << 24)
                     | ((uint)Clip255((int)((c0 >> 16) & 0xff) + (int)((c1 >> 16) & 0xff) - (int)((c2 >> 16) & 0xff)) << 16)
                     | ((uint)Clip255((int)((c0 >> 8) & 0xff) + (int)((c1 >> 8) & 0xff) - (int)((c2 >> 8) & 0xff)) << 8)
                     | (uint)Clip255((int)(c0 & 0xff) + (int)(c1 & 0xff) - (int)(c2 & 0xff));
            }

            static uint ClampedAddSubtractHalf(uint c0, uint c1, uint c2)
            {
                uint ave = Average2(c0, c1);
                return ((uint)Clip255((int)(ave >> 24) + ((int)(ave >> 24) - (int)(c2 >> 24)) / 2) << 24)
                     | ((uint)Clip255((int)((ave >> 16) & 0xff) + ((int)((ave >> 16) & 0xff) - (int)((c2 >> 16) & 0xff)) / 2) << 16)
                     | ((uint)Clip255((int)((ave >> 8) & 0xff) + ((int)((ave >> 8) & 0xff) - (int)((c2 >> 8) & 0xff)) / 2) << 8)
                     | (uint)Clip255((int)(ave & 0xff) + ((int)(ave & 0xff) - (int)(c2 & 0xff)) / 2);
            }

            static int Clip255(int a) => a < 0 ? 0 : a > 255 ? 255 : a;

            static int SubSampleSize(int size, int bits) => (size + (1 << bits) - 1) >> bits;
        }
    }
}
