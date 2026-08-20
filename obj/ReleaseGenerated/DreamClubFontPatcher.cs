using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DreamClubFontPatcher
{
    public static class Program
    {
        private static readonly byte[] FontResourceLeads =
        {
            0x81, 0x82, 0x83, 0x84,
            0x87, 0x88, 0x89, 0x8A, 0x8B, 0x8C, 0x8D, 0x8E, 0x8F,
            0x90, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98,
            0x99, 0x9A, 0x9B, 0x9C, 0x9D, 0x9E, 0x9F,
            0xE0, 0xE1, 0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8,
            0xE9, 0xEA
        };

        private static readonly byte[] KoreanFontResourceLeads =
        {
            // No E5/E6 glyphs occur in the verified executable text pools.
            // Keep E0/E1 available for the more common original Japanese.
            0xE5, 0xE6, 0xE1, 0xE3, 0xE2, 0xEA,
            0xE0, 0xE4, 0xE7, 0xE8, 0xE9
        };

        private const string OriginalWarning = "";

        private const string KoreanWarning =
            "드림클럽은 자동 저장을 사용합니다.\n" +
            "게임에서 사용 중인 저장 장치를 분리하면\n" +
            "저장할 수 없으니 주의해 주세요.\n";

        private const string JapaneseCompareText = "";
        private const float Font00GlyphEmSize = 35.0f;
        private const float Font01GlyphEmSize = 35.0f;
        private const int GlyphErosionNumerator = 1;
        private const int GlyphErosionDenominator = 2;

        private sealed class GlyphMapping
        {
            public char Character;
            public byte Lead;
            public byte Trail;
        }

        private sealed class GlyphMapManifest
        {
            public GlyphMapEntry[] mappings { get; set; }
        }

        private sealed class GlyphMapEntry
        {
            public string character { get; set; }
            public int codePoint { get; set; }
            public int lead { get; set; }
            public int trail { get; set; }
            public string codeHex { get; set; }
        }

        private sealed class TranslationManifest
        {
            public TranslationEntry[] entries { get; set; }
            public WideTranslationEntry[] utf16beEntries { get; set; }
        }

        private sealed class TranslationEntry
        {
            public string id { get; set; }
            public string sourceText { get; set; }
            public string translation { get; set; }
            public string status { get; set; }
            public TranslationOccurrence[] occurrences { get; set; }
        }

        private sealed class TranslationOccurrence
        {
            public int byteLimit { get; set; }
            public int xexFileOffset { get; set; }
        }

        private sealed class WideTranslationEntry
        {
            public string id { get; set; }
            public string sourceText { get; set; }
            public string translation { get; set; }
            public string status { get; set; }
            public int byteLimit { get; set; }
            public int xexFileOffset { get; set; }
        }

        private static uint ReadBe32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) |
                   ((uint)data[offset + 1] << 16) |
                   ((uint)data[offset + 2] << 8) |
                   data[offset + 3];
        }

        private static int FindUnique(byte[] data, byte[] needle)
        {
            int found = -1;
            for (int i = 0; i <= data.Length - needle.Length; ++i)
            {
                if (data[i] != needle[0])
                {
                    continue;
                }
                int j = 1;
                for (; j < needle.Length && data[i + j] == needle[j]; ++j)
                {
                }
                if (j != needle.Length)
                {
                    continue;
                }
                if (found >= 0)
                {
                    throw new InvalidDataException("The warning string occurs more than once.");
                }
                found = i;
            }
            if (found < 0)
            {
                throw new InvalidDataException("The original warning string was not found.");
            }
            return found;
        }

        private static List<GlyphMapping> BuildMappings()
        {
            return BuildMappings(KoreanWarning, true, 0xE0);
        }

        private static List<GlyphMapping> BuildMappings(
            string text, bool hangulOnly, byte lead)
        {
            List<GlyphMapping> mappings = new List<GlyphMapping>();
            Dictionary<char, GlyphMapping> byCharacter = new Dictionary<char, GlyphMapping>();
            int slot = 0;
            foreach (char character in text)
            {
                if ((hangulOnly &&
                     (character < 0xAC00 || character > 0xD7A3)) ||
                    (!hangulOnly && character <= 0x7F) ||
                    byCharacter.ContainsKey(character))
                {
                    continue;
                }
                int trailValue = 0x40 + slot;
                if (trailValue >= 0x7F)
                {
                    ++trailValue;
                }
                if (trailValue > 0xFC)
                {
                    throw new InvalidOperationException(
                        "Too many Korean glyphs for the E0 font page.");
                }
                GlyphMapping mapping = new GlyphMapping
                {
                    Character = character,
                    Lead = lead,
                    Trail = (byte)trailValue
                };
                mappings.Add(mapping);
                byCharacter.Add(character, mapping);
                ++slot;
            }
            return mappings;
        }

        private static string ExpandEscapedControls(string text)
        {
            if (text == null)
            {
                return String.Empty;
            }
            return text.Replace("\\r", "\r")
                       .Replace("\\n", "\n")
                       .Replace("\\t", "\t");
        }

        private static TranslationManifest LoadTranslationManifest(string path)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            TranslationManifest manifest =
                serializer.Deserialize<TranslationManifest>(
                    File.ReadAllText(path, Encoding.UTF8));
            if (manifest == null || manifest.entries == null)
            {
                throw new InvalidDataException(
                    "The translation manifest has no entries.");
            }
            return manifest;
        }

        private static List<GlyphMapping> BuildManifestMappings(
            TranslationManifest manifest)
        {
            List<GlyphMapping> mappings = new List<GlyphMapping>();
            Dictionary<char, GlyphMapping> byCharacter =
                new Dictionary<char, GlyphMapping>();
            int pageIndex = 0;
            int slotInPage = 0;
            foreach (TranslationEntry entry in manifest.entries)
            {
                if (!String.Equals(
                        entry.status, "translated",
                        StringComparison.OrdinalIgnoreCase) ||
                    String.IsNullOrEmpty(entry.translation))
                {
                    throw new InvalidDataException(
                        "Untranslated system-menu entry: " + entry.id);
                }
                string text = ExpandEscapedControls(entry.translation);
                foreach (char character in text)
                {
                    if (character < 0xAC00 || character > 0xD7A3 ||
                        byCharacter.ContainsKey(character))
                    {
                        continue;
                    }
                    if (pageIndex >= KoreanFontResourceLeads.Length)
                    {
                        throw new InvalidOperationException(
                            "The E0-EA Korean font pages are full.");
                    }
                    int trailValue = 0x40 + slotInPage;
                    if (trailValue >= 0x7F)
                    {
                        ++trailValue;
                    }
                    if (trailValue > 0xFC)
                    {
                        ++pageIndex;
                        slotInPage = 0;
                        if (pageIndex >= KoreanFontResourceLeads.Length)
                        {
                            throw new InvalidOperationException(
                                "The E0-EA Korean font pages are full.");
                        }
                        trailValue = 0x40;
                    }
                    GlyphMapping mapping = new GlyphMapping
                    {
                        Character = character,
                        Lead = KoreanFontResourceLeads[pageIndex],
                        Trail = (byte)trailValue
                    };
                    mappings.Add(mapping);
                    byCharacter.Add(character, mapping);
                    ++slotInPage;
                }
            }
            return mappings;
        }

        private static List<GlyphMapping> LoadExplicitMappings(string path)
        {
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = Int32.MaxValue;
            GlyphMapManifest manifest =
                serializer.Deserialize<GlyphMapManifest>(
                    File.ReadAllText(path, Encoding.UTF8));
            if (manifest == null || manifest.mappings == null ||
                manifest.mappings.Length == 0)
            {
                throw new InvalidDataException(
                    "The explicit glyph map has no mappings.");
            }

            HashSet<char> characters = new HashSet<char>();
            HashSet<ushort> codes = new HashSet<ushort>();
            List<GlyphMapping> mappings = new List<GlyphMapping>();
            foreach (GlyphMapEntry entry in manifest.mappings)
            {
                if (String.IsNullOrEmpty(entry.character) ||
                    entry.character.Length != 1 ||
                    Array.IndexOf(
                        FontResourceLeads, (byte)entry.lead) < 0 ||
                    entry.trail < 0x40 || entry.trail > 0xFC ||
                    entry.trail == 0x7F)
                {
                    throw new InvalidDataException(
                        "Invalid explicit glyph mapping: " +
                        entry.codeHex);
                }
                char character = entry.character[0];
                ushort code = (ushort)(
                    (entry.lead << 8) | entry.trail);
                if (!characters.Add(character) || !codes.Add(code))
                {
                    throw new InvalidDataException(
                        "Duplicate explicit glyph mapping: " +
                        entry.codeHex);
                }
                mappings.Add(new GlyphMapping
                {
                    Character = character,
                    Lead = (byte)entry.lead,
                    Trail = (byte)entry.trail
                });
            }
            return mappings;
        }

        private static byte[] EncodeMappedText(
            string text, List<GlyphMapping> mappings)
        {
            Dictionary<char, GlyphMapping> byCharacter = new Dictionary<char, GlyphMapping>();
            foreach (GlyphMapping mapping in mappings)
            {
                byCharacter.Add(mapping.Character, mapping);
            }
            List<byte> encoded = new List<byte>();
            foreach (char character in text)
            {
                GlyphMapping mapping;
                if (byCharacter.TryGetValue(character, out mapping))
                {
                    encoded.Add(mapping.Lead);
                    encoded.Add(mapping.Trail);
                }
                else if (character <= 0x7F)
                {
                    encoded.Add((byte)character);
                }
                else
                {
                    Encoding sjis = Encoding.GetEncoding(
                        932,
                        EncoderFallback.ExceptionFallback,
                        DecoderFallback.ExceptionFallback);
                    byte[] originalBytes =
                        sjis.GetBytes(character.ToString());
                    encoded.AddRange(originalBytes);
                }
            }
            return encoded.ToArray();
        }

        private static byte[] EncodeReplacement(List<GlyphMapping> mappings)
        {
            return EncodeMappedText(KoreanWarning, mappings);
        }

        private static byte[] EncodeJapaneseComparison(
            List<GlyphMapping> encodedMappings,
            List<GlyphMapping> copiedMappings)
        {
            Encoding sjis = Encoding.GetEncoding(932);
            List<byte> encoded = new List<byte>();
            encoded.AddRange(sjis.GetBytes(
                "O:" + JapaneseCompareText + "\n"));
            encoded.Add((byte)'G');
            encoded.Add((byte)':');
            encoded.AddRange(EncodeMappedText(
                JapaneseCompareText, encodedMappings));
            encoded.Add((byte)'\n');
            encoded.Add((byte)'C');
            encoded.Add((byte)':');
            encoded.AddRange(EncodeMappedText(
                JapaneseCompareText, copiedMappings));
            encoded.Add((byte)'\n');
            return encoded.ToArray();
        }

        private static byte[] EncodeJapaneseRawCopyComparison(
            List<GlyphMapping> copiedMappings)
        {
            Encoding sjis = Encoding.GetEncoding(932);
            List<byte> encoded = new List<byte>();
            encoded.AddRange(sjis.GetBytes(
                "O:" + JapaneseCompareText + "\n"));
            encoded.Add((byte)'C');
            encoded.Add((byte)':');
            encoded.AddRange(EncodeMappedText(
                JapaneseCompareText, copiedMappings));
            encoded.Add((byte)'\n');
            return encoded.ToArray();
        }

        private static void PatchXex(
            string inputPath, string outputPath, byte[] replacement)
        {
            Encoding sjis = Encoding.GetEncoding(932);
            byte[] original = sjis.GetBytes(OriginalWarning);
            if (replacement.Length > original.Length)
            {
                throw new InvalidOperationException("The replacement warning exceeds the original slot.");
            }

            byte[] xex = File.ReadAllBytes(inputPath);
            int offset = FindUnique(xex, original);
            Array.Clear(xex, offset, original.Length);
            Buffer.BlockCopy(replacement, 0, xex, offset, replacement.Length);
            File.WriteAllBytes(outputPath, xex);
            Console.WriteLine(
                "Patched XEX warning at 0x{0:X}: {1} -> {2} bytes",
                offset, original.Length, replacement.Length);
        }

        private static void PatchXexManifest(
            string inputPath, string outputPath,
            TranslationManifest manifest, List<GlyphMapping> mappings)
        {
            Encoding sjis = Encoding.GetEncoding(
                932,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            byte[] xex = File.ReadAllBytes(inputPath);
            int patchedOccurrences = 0;
            foreach (TranslationEntry entry in manifest.entries)
            {
                string sourceText =
                    ExpandEscapedControls(entry.sourceText);
                string translatedText =
                    ExpandEscapedControls(entry.translation);
                byte[] sourceBytes = sjis.GetBytes(sourceText);
                byte[] replacement =
                    EncodeMappedText(translatedText, mappings);
                if (entry.occurrences == null ||
                    entry.occurrences.Length == 0)
                {
                    throw new InvalidDataException(
                        "No XEX occurrence for " + entry.id);
                }
                foreach (TranslationOccurrence occurrence
                         in entry.occurrences)
                {
                    int offset = occurrence.xexFileOffset;
                    int byteLimit = occurrence.byteLimit;
                    if (offset <= 0 ||
                        byteLimit != sourceBytes.Length ||
                        offset + byteLimit >= xex.Length)
                    {
                        throw new InvalidDataException(
                            "Invalid XEX occurrence for " + entry.id);
                    }
                    for (int byteIndex = 0;
                         byteIndex < sourceBytes.Length; ++byteIndex)
                    {
                        if (xex[offset + byteIndex] !=
                            sourceBytes[byteIndex])
                        {
                            throw new InvalidDataException(
                                "XEX source mismatch for " + entry.id +
                                " at 0x" + offset.ToString("X"));
                        }
                    }
                    if (replacement.Length > byteLimit)
                    {
                        throw new InvalidDataException(
                            "Translation exceeds " + entry.id +
                            ": " + replacement.Length + " > " +
                            byteLimit);
                    }
                    Array.Clear(xex, offset, byteLimit);
                    Buffer.BlockCopy(
                        replacement, 0, xex, offset,
                        replacement.Length);
                    ++patchedOccurrences;
                }
            }
            int patchedWideEntries = 0;
            if (manifest.utf16beEntries != null)
            {
                Encoding utf16be = Encoding.BigEndianUnicode;
                foreach (WideTranslationEntry entry
                         in manifest.utf16beEntries)
                {
                    byte[] sourceBytes = utf16be.GetBytes(
                        ExpandEscapedControls(entry.sourceText));
                    byte[] replacement = utf16be.GetBytes(
                        ExpandEscapedControls(entry.translation));
                    int offset = entry.xexFileOffset;
                    if (offset <= 0 ||
                        sourceBytes.Length != entry.byteLimit ||
                        replacement.Length > entry.byteLimit ||
                        offset + entry.byteLimit >= xex.Length)
                    {
                        throw new InvalidDataException(
                            "Invalid UTF-16BE entry: " + entry.id);
                    }
                    for (int byteIndex = 0;
                         byteIndex < sourceBytes.Length; ++byteIndex)
                    {
                        if (xex[offset + byteIndex] !=
                            sourceBytes[byteIndex])
                        {
                            throw new InvalidDataException(
                                "UTF-16BE source mismatch for " +
                                entry.id + " at 0x" +
                                offset.ToString("X"));
                        }
                    }
                    Array.Clear(xex, offset, entry.byteLimit);
                    Buffer.BlockCopy(
                        replacement, 0, xex, offset,
                        replacement.Length);
                    ++patchedWideEntries;
                }
            }
            File.WriteAllBytes(outputPath, xex);
            Console.WriteLine(
                "Patched {0} system-menu XEX strings",
                patchedOccurrences);
            Console.WriteLine(
                "Patched {0} UTF-16BE keyboard UI strings",
                patchedWideEntries);
        }

        private static void GetVariant(
            int mappingIndex, out int tileSize, out bool tiled,
            out int byteXor, out int channelMode)
        {
            if (mappingIndex < 32)
            {
                int[] tileSizes = { 16, 24, 32, 40 };
                tileSize = tileSizes[mappingIndex / 8];
                tiled = ((mappingIndex / 4) & 1) != 0;
                byteXor = mappingIndex & 3;
                channelMode = 0;
                return;
            }
            tileSize = 16;
            tiled = false;
            byteXor = 1;
            channelMode = mappingIndex - 31;
        }

        private static string DescribeVariant(int mappingIndex)
        {
            int tileSize;
            bool tiled;
            int byteXor;
            int channelMode;
            GetVariant(
                mappingIndex, out tileSize, out tiled,
                out byteXor, out channelMode);
            return String.Format(
                "tile={0} layout={1} byte-xor={2} channel={3}",
                tileSize, tiled ? "tiled" : "linear",
                byteXor, channelMode);
        }

        private static Bitmap RenderGlyph(
            char character, string fontPath, int tileSize,
            float glyphEmSize, int channelMode)
        {
            PrivateFontCollection fonts = new PrivateFontCollection();
            fonts.AddFontFile(fontPath);
            FontFamily family = fonts.Families[0];
            Bitmap mask = new Bitmap(
                tileSize, tileSize, PixelFormat.Format32bppArgb);
            Bitmap bitmap = new Bitmap(
                tileSize, tileSize, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(mask))
            {
                graphics.Clear(Color.Black);
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddString(
                        character.ToString(),
                        family,
                        (int)FontStyle.Regular,
                        glyphEmSize,
                        new PointF(0, 0),
                        StringFormat.GenericTypographic);
                    RectangleF bounds = path.GetBounds();
                    using (Matrix transform = new Matrix())
                    {
                        float targetX;
                        float targetY;
                        if (character == ',')
                        {
                            // ASCII punctuation is not backed by a valid
                            // single-byte atlas cell in this game. Pin the
                            // custom comma's outline two pixels from the left
                            // while keeping it pinned to the bottom cell edge.
                            targetX = 2.0f - bounds.Left;
                            targetY = tileSize - bounds.Bottom;
                        }
                        else if (character == '\'' || character == '"')
                        {
                            // Place quotation marks at cap height. Centering
                            // their small outlines vertically makes them look
                            // like malformed middle dots.
                            targetX =
                                (tileSize - bounds.Width) * 0.5f - bounds.X;
                            targetY = 5.0f - bounds.Top;
                        }
                        else
                        {
                            targetX =
                                (tileSize - bounds.Width) * 0.5f - bounds.X;
                            targetY =
                                (tileSize - bounds.Height) * 0.5f - bounds.Y;
                        }
                        transform.Translate(
                            targetX, targetY);
                        path.Transform(transform);
                    }
                    graphics.FillPath(Brushes.White, path);
                }
            }
            byte[,] coverage = new byte[tileSize, tileSize];
            for (int y = 0; y < tileSize; ++y)
            {
                for (int x = 0; x < tileSize; ++x)
                {
                    coverage[x, y] = mask.GetPixel(x, y).R;
                }
            }
            for (int y = 0; y < tileSize; ++y)
            {
                for (int x = 0; x < tileSize; ++x)
                {
                    int center = coverage[x, y];
                    int minimum = center;
                    for (int offsetY = -1; offsetY <= 1; ++offsetY)
                    {
                        for (int offsetX = -1; offsetX <= 1; ++offsetX)
                        {
                            int sampleX = x + offsetX;
                            int sampleY = y + offsetY;
                            int sample =
                                sampleX < 0 || sampleX >= tileSize ||
                                sampleY < 0 || sampleY >= tileSize
                                ? 0
                                : coverage[sampleX, sampleY];
                            if (sample < minimum)
                            {
                                minimum = sample;
                            }
                        }
                    }
                    int erosionNumerator =
                        character == ',' ||
                        character == '\'' ||
                        character == '"'
                        ? 0
                        : GlyphErosionNumerator;
                    byte value = (byte)(
                        (center *
                         (GlyphErosionDenominator - erosionNumerator) +
                         minimum * erosionNumerator +
                         GlyphErosionDenominator / 2) /
                        GlyphErosionDenominator);
                    Color output;
                    switch (channelMode)
                    {
                        case 1:
                            output = Color.FromArgb(value, 255, 255, 255);
                            break;
                        case 2:
                            output = Color.FromArgb(255, value, value, value);
                            break;
                        case 3:
                            output = Color.FromArgb(value, 0, 0, 0);
                            break;
                        case 4:
                            output = Color.FromArgb(255, value, 0, 0);
                            break;
                        case 5:
                            // The Dream C Club font atlas stores an inverse
                            // coverage mask in RGB: untouched background is
                            // white and the glyph stroke is dark. The font
                            // render state uses that mask when compositing.
                            byte inverse = (byte)(255 - value);
                            output = Color.FromArgb(
                                255, inverse, inverse, inverse);
                            break;
                        default:
                            output = Color.FromArgb(
                                value, value, value, value);
                            break;
                    }
                    bitmap.SetPixel(x, y, output);
                }
            }
            mask.Dispose();
            fonts.Dispose();
            return bitmap;
        }

        private static ushort Encode565(Color color)
        {
            int r = (color.R * 31 + 127) / 255;
            int g = (color.G * 63 + 127) / 255;
            int b = (color.B * 31 + 127) / 255;
            return (ushort)((r << 11) | (g << 5) | b);
        }

        private static int Tiled2D(
            int x, int y, int pitchAligned, int bytesPerBlockLog2)
        {
            int outerBlocks =
                (((y >> 5) * (pitchAligned >> 5)) + (x >> 5)) << 6;
            int innerBlocks = (((y >> 1) & 7) << 3) | (x & 7);
            int outerInnerBytes =
                (outerBlocks | innerBlocks) << bytesPerBlockLog2;
            int bank = (y >> 4) & 1;
            int pipe = ((x >> 3) & 3) ^ (((y >> 3) & 1) << 1);
            return ((y & 1) << 4) |
                   (pipe << 6) |
                   (bank << 11) |
                   (outerInnerBytes & 15) |
                   (((outerInnerBytes >> 4) & 1) << 5) |
                   (((outerInnerBytes >> 5) & 7) << 8) |
                   ((outerInnerBytes >> 8) << 12);
        }

        private static byte[] EncodeDxt5Block(Color[] colors)
        {
            byte[] block = new byte[16];
            byte alpha0 = 255;
            byte alpha1 = 0;
            byte[] alphaPalette = { 255, 0, 219, 182, 146, 109, 73, 36 };
            block[0] = alpha0;
            block[1] = alpha1;
            ulong alphaBits = 0;
            for (int i = 0; i < 16; ++i)
            {
                int best = 0;
                int bestDistance = Int32.MaxValue;
                for (int candidate = 0; candidate < alphaPalette.Length; ++candidate)
                {
                    int distance = Math.Abs(colors[i].A - alphaPalette[candidate]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
                alphaBits |= (ulong)best << (i * 3);
            }
            for (int i = 0; i < 6; ++i)
            {
                block[2 + i] = (byte)(alphaBits >> (i * 8));
            }

            ushort color0 = Encode565(Color.White);
            ushort color1 = Encode565(Color.Black);
            block[8] = (byte)color0;
            block[9] = (byte)(color0 >> 8);
            block[10] = (byte)color1;
            block[11] = (byte)(color1 >> 8);
            int[] levels = { 255, 0, 170, 85 };
            uint colorBits = 0;
            for (int i = 0; i < 16; ++i)
            {
                int luminance = (colors[i].R * 299 + colors[i].G * 587 +
                                 colors[i].B * 114 + 500) / 1000;
                int best = 0;
                int bestDistance = Int32.MaxValue;
                for (int candidate = 0; candidate < levels.Length; ++candidate)
                {
                    int distance = Math.Abs(luminance - levels[candidate]);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
                colorBits |= (uint)best << (i * 2);
            }
            block[12] = (byte)colorBits;
            block[13] = (byte)(colorBits >> 8);
            block[14] = (byte)(colorBits >> 16);
            block[15] = (byte)(colorBits >> 24);
            return block;
        }

        private static int ResourceIndex(byte lead)
        {
            for (int i = 0; i < FontResourceLeads.Length; ++i)
            {
                if (FontResourceLeads[i] == lead)
                {
                    return i;
                }
            }
            throw new ArgumentOutOfRangeException("lead");
        }

        private sealed class NativeBlock
        {
            public byte[] Raw;
            public byte[] Coverage;
            public ushort Signature;
            public int BinaryError;
        }

        private static int GetPageBase(
            byte[] xpr, byte lead, out int width, out int height)
        {
            int resourceIndex = ResourceIndex(lead);
            int descriptor = 0x10 + resourceIndex * 0x10;
            int metadata = checked((int)ReadBe32(xpr, descriptor + 4));
            uint fetchDword0 = ReadBe32(xpr, metadata + 0x28);
            uint fetchDword1 = ReadBe32(xpr, metadata + 0x2C);
            uint dimensions = ReadBe32(xpr, metadata + 0x30);
            int textureFormat = (int)(fetchDword1 & 0x3F);
            int endian = (int)((fetchDword1 >> 6) & 3);
            bool tiled = (fetchDword0 & 0x80000000U) != 0;
            width = checked((int)(dimensions & 0x1FFF) + 1);
            height = checked((int)((dimensions >> 13) & 0x1FFF) + 1);
            if (textureFormat != 20 || endian != 1 || tiled ||
                width != 512 || height != 512)
            {
                throw new InvalidDataException(String.Format(
                    "Unexpected font page for lead {0:X2}: " +
                    "{1}x{2}, format={3}, endian={4}, tiled={5}.",
                    lead, width, height, textureFormat, endian, tiled));
            }
            int headerSize = checked((int)ReadBe32(xpr, 4));
            int basePage = checked((int)(fetchDword1 >> 12));
            int baseOffset = checked(0x0C + headerSize + basePage * 4096);
            if (baseOffset < 0 ||
                baseOffset + width * height / 2 > xpr.Length)
            {
                throw new InvalidDataException("Truncated font texture.");
            }
            return baseOffset;
        }

        private static void CopyOriginalJapaneseGlyphs(
            string inputPath, string outputPath,
            List<GlyphMapping> targetMappings)
        {
            Encoding sjis = Encoding.GetEncoding(932);
            byte[] sourceXpr = File.ReadAllBytes(inputPath);
            byte[] outputXpr = (byte[])sourceXpr.Clone();
            foreach (GlyphMapping targetMapping in targetMappings)
            {
                byte[] sourceCode =
                    sjis.GetBytes(targetMapping.Character.ToString());
                if (sourceCode.Length != 2)
                {
                    throw new InvalidOperationException(
                        "Expected a two-byte CP932 source glyph: " +
                        targetMapping.Character);
                }

                int sourceWidth;
                int sourceHeight;
                int sourceBase = GetPageBase(
                    sourceXpr, sourceCode[0],
                    out sourceWidth, out sourceHeight);
                int targetWidth;
                int targetHeight;
                int targetBase = GetPageBase(
                    outputXpr, targetMapping.Lead,
                    out targetWidth, out targetHeight);
                if (sourceWidth != targetWidth ||
                    sourceHeight != targetHeight)
                {
                    throw new InvalidDataException(
                        "Source and target font pages differ in size.");
                }

                int sourceCellIndex =
                    sourceCode[1] - 0x40;
                int targetCellIndex =
                    targetMapping.Trail - 0x40;
                int sourceCellX = sourceCellIndex & 15;
                int sourceCellY = sourceCellIndex >> 4;
                int targetCellX = targetCellIndex & 15;
                int targetCellY = targetCellIndex >> 4;
                int blocksPerRow = sourceWidth / 4;

                for (int blockY = 0; blockY < 8; ++blockY)
                {
                    for (int blockX = 0; blockX < 8; ++blockX)
                    {
                        int source = sourceBase +
                            (((sourceCellY * 8 + blockY) *
                              blocksPerRow) +
                             sourceCellX * 8 + blockX) * 16;
                        int destination = targetBase +
                            (((targetCellY * 8 + blockY) *
                              blocksPerRow) +
                             targetCellX * 8 + blockX) * 16;
                        Buffer.BlockCopy(
                            sourceXpr, source, outputXpr,
                            destination, 16);
                    }
                }

                Console.WriteLine(
                    "Copied native glyph U+{0:X4} {1}: " +
                    "{2:X2}{3:X2} -> {4:X2}{5:X2}",
                    (int)targetMapping.Character,
                    targetMapping.Character,
                    sourceCode[0], sourceCode[1],
                    targetMapping.Lead, targetMapping.Trail);
            }
            File.WriteAllBytes(outputPath, outputXpr);
        }

        private static void Decode565(
            ushort value, out byte red, out byte green, out byte blue)
        {
            red = (byte)((((value >> 11) & 31) * 255 + 15) / 31);
            green = (byte)((((value >> 5) & 63) * 255 + 31) / 63);
            blue = (byte)(((value & 31) * 255 + 15) / 31);
        }

        private static Color[] DecodeDxt5Block(byte[] block)
        {
            byte[] alpha = new byte[8];
            alpha[0] = block[0];
            alpha[1] = block[1];
            if (alpha[0] > alpha[1])
            {
                for (int index = 1; index <= 6; ++index)
                {
                    alpha[index + 1] = (byte)(
                        ((7 - index) * alpha[0] +
                         index * alpha[1] + 3) / 7);
                }
            }
            else
            {
                for (int index = 1; index <= 4; ++index)
                {
                    alpha[index + 1] = (byte)(
                        ((5 - index) * alpha[0] +
                         index * alpha[1] + 2) / 5);
                }
                alpha[6] = 0;
                alpha[7] = 255;
            }

            ulong alphaBits = 0;
            for (int index = 0; index < 6; ++index)
            {
                alphaBits |= (ulong)block[2 + index] << (index * 8);
            }

            ushort color0 = (ushort)(block[8] | (block[9] << 8));
            ushort color1 = (ushort)(block[10] | (block[11] << 8));
            byte[] red = new byte[4];
            byte[] green = new byte[4];
            byte[] blue = new byte[4];
            Decode565(color0, out red[0], out green[0], out blue[0]);
            Decode565(color1, out red[1], out green[1], out blue[1]);

            // BC3 always uses the four-color interpolation mode.
            red[2] = (byte)((2 * red[0] + red[1]) / 3);
            green[2] = (byte)((2 * green[0] + green[1]) / 3);
            blue[2] = (byte)((2 * blue[0] + blue[1]) / 3);
            red[3] = (byte)((red[0] + 2 * red[1]) / 3);
            green[3] = (byte)((green[0] + 2 * green[1]) / 3);
            blue[3] = (byte)((blue[0] + 2 * blue[1]) / 3);

            uint colorBits = (uint)(
                block[12] | (block[13] << 8) |
                (block[14] << 16) | (block[15] << 24));
            Color[] pixels = new Color[16];
            for (int pixel = 0; pixel < 16; ++pixel)
            {
                int alphaIndex =
                    (int)((alphaBits >> (pixel * 3)) & 7);
                int colorIndex =
                    (int)((colorBits >> (pixel * 2)) & 3);
                pixels[pixel] = Color.FromArgb(
                    alpha[alphaIndex],
                    red[colorIndex],
                    green[colorIndex],
                    blue[colorIndex]);
            }
            return pixels;
        }

        private static byte[] BuildCoverage(byte[] raw)
        {
            byte[] canonical = new byte[16];
            for (int index = 0; index < 16; ++index)
            {
                canonical[index] = raw[index ^ 1];
            }
            Color[] pixels = DecodeDxt5Block(canonical);
            byte[] coverage = new byte[16];
            for (int pixel = 0; pixel < 16; ++pixel)
            {
                Color color = pixels[pixel];
                int luminance =
                    (color.R * 299 + color.G * 587 +
                     color.B * 114 + 500) / 1000;
                // Native background is approximately 250 and the
                // fullwidth-I stroke is approximately 180.
                int value = (252 - luminance) * 255 / 72;
                coverage[pixel] = (byte)Math.Max(
                    0, Math.Min(255, value));
            }
            return coverage;
        }

        private static ushort BuildSignature(byte[] coverage)
        {
            ushort signature = 0;
            for (int pixel = 0; pixel < 16; ++pixel)
            {
                if (coverage[pixel] >= 128)
                {
                    signature |= (ushort)(1 << pixel);
                }
            }
            return signature;
        }

        private static int BuildBinaryError(
            byte[] coverage, ushort signature)
        {
            int error = 0;
            for (int pixel = 0; pixel < 16; ++pixel)
            {
                int expected =
                    (signature & (1 << pixel)) != 0 ? 255 : 0;
                error += Math.Abs(coverage[pixel] - expected);
            }
            return error;
        }

        private static Dictionary<ushort, List<NativeBlock>>
            BuildNativeDictionary(byte[] xpr)
        {
            int width;
            int height;
            int pageBase = GetPageBase(
                xpr, 0x82, out width, out height);
            int blockPitch = width / 4;
            Dictionary<ushort, List<NativeBlock>> dictionary =
                new Dictionary<ushort, List<NativeBlock>>();
            for (int blockY = 0; blockY < height / 4; ++blockY)
            {
                for (int blockX = 0; blockX < width / 4; ++blockX)
                {
                    int source = pageBase +
                        (blockY * blockPitch + blockX) * 16;
                    byte[] raw = new byte[16];
                    Buffer.BlockCopy(xpr, source, raw, 0, 16);
                    byte[] coverage = BuildCoverage(raw);
                    ushort signature = BuildSignature(coverage);
                    NativeBlock candidate = new NativeBlock
                    {
                        Raw = raw,
                        Coverage = coverage,
                        Signature = signature,
                        BinaryError =
                            BuildBinaryError(coverage, signature)
                    };
                    List<NativeBlock> candidates;
                    if (!dictionary.TryGetValue(
                        signature, out candidates))
                    {
                        candidates = new List<NativeBlock>();
                        dictionary.Add(signature, candidates);
                    }

                    bool duplicate = false;
                    foreach (NativeBlock existing in candidates)
                    {
                        int byteIndex = 0;
                        while (byteIndex < 16 &&
                               existing.Raw[byteIndex] ==
                               candidate.Raw[byteIndex])
                        {
                            ++byteIndex;
                        }
                        if (byteIndex == 16)
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (!duplicate)
                    {
                        candidates.Add(candidate);
                        candidates.Sort(delegate(
                            NativeBlock left, NativeBlock right)
                        {
                            return left.BinaryError.CompareTo(
                                right.BinaryError);
                        });
                        if (candidates.Count > 8)
                        {
                            candidates.RemoveAt(candidates.Count - 1);
                        }
                    }
                }
            }
            Console.WriteLine(
                "Built native BC3 dictionary: {0} signatures.",
                dictionary.Count);
            return dictionary;
        }

        private static int PopCount(ushort value)
        {
            int count = 0;
            uint bits = value;
            while (bits != 0)
            {
                bits &= bits - 1;
                ++count;
            }
            return count;
        }

        private static long CoverageError(
            byte[] target, byte[] candidate)
        {
            long error = 0;
            for (int pixel = 0; pixel < 16; ++pixel)
            {
                int difference = target[pixel] - candidate[pixel];
                error += difference * difference;
            }
            return error;
        }

        private static NativeBlock FindNativeBlock(
            Dictionary<ushort, List<NativeBlock>> dictionary,
            byte[] target)
        {
            ushort targetSignature = BuildSignature(target);
            List<NativeBlock> exact;
            if (dictionary.TryGetValue(targetSignature, out exact))
            {
                NativeBlock bestExact = exact[0];
                long bestExactError =
                    CoverageError(target, bestExact.Coverage);
                for (int index = 1; index < exact.Count; ++index)
                {
                    long error =
                        CoverageError(target, exact[index].Coverage);
                    if (error < bestExactError)
                    {
                        bestExact = exact[index];
                        bestExactError = error;
                    }
                }
                return bestExact;
            }

            NativeBlock best = null;
            long bestScore = Int64.MaxValue;
            foreach (KeyValuePair<ushort, List<NativeBlock>> pair
                in dictionary)
            {
                int differingPixels = PopCount(
                    (ushort)(pair.Key ^ targetSignature));
                long signaturePenalty =
                    (long)differingPixels * 16 * 255 * 255;
                foreach (NativeBlock candidate in pair.Value)
                {
                    long score = signaturePenalty +
                        CoverageError(target, candidate.Coverage);
                    if (score < bestScore)
                    {
                        best = candidate;
                        bestScore = score;
                    }
                }
            }
            if (best == null)
            {
                throw new InvalidDataException(
                    "The native BC3 dictionary is empty.");
            }
            return best;
        }

        private static byte[] ReadMaskBlock(
            Bitmap glyph, int blockX, int blockY)
        {
            byte[] target = new byte[16];
            for (int pixelY = 0; pixelY < 4; ++pixelY)
            {
                for (int pixelX = 0; pixelX < 4; ++pixelX)
                {
                    target[pixelY * 4 + pixelX] =
                        glyph.GetPixel(
                            blockX * 4 + pixelX,
                            blockY * 4 + pixelY).R;
                }
            }
            return target;
        }

        private static byte[][] ReadFullwidthIBlocks(byte[] xpr)
        {
            int width;
            int height;
            int pageBase = GetPageBase(
                xpr, 0x82, out width, out height);
            int blockPitch = width / 4;
            const int cellBlockX = 8 * 8;
            const int cellBlockY = 2 * 8;
            byte[][] blocks = new byte[64][];
            for (int blockY = 0; blockY < 8; ++blockY)
            {
                for (int blockX = 0; blockX < 8; ++blockX)
                {
                    int source = pageBase +
                        ((cellBlockY + blockY) * blockPitch +
                         cellBlockX + blockX) * 16;
                    byte[] raw = new byte[16];
                    Buffer.BlockCopy(xpr, source, raw, 0, 16);
                    blocks[blockY * 8 + blockX] = raw;
                }
            }
            return blocks;
        }

        private static byte[] SelectFullwidthITemplateBlock(
            byte[][] templateBlocks, byte[] target,
            int blockX, int blockY, out bool ink)
        {
            int coverageSum = 0;
            for (int pixel = 0; pixel < 16; ++pixel)
            {
                coverageSum += target[pixel];
            }

            // More than roughly one fully covered pixel makes this an
            // occupied 4x4 block. This intentionally produces a sharp
            // 8x8 block-font test without inventing new BC3 channel data.
            ink = coverageSum >= 900;
            int sourceBlockX;
            if (ink)
            {
                // The original fullwidth I has a clean 8-pixel vertical
                // stroke in block columns 4 and 5.
                sourceBlockX = 4 + (blockX & 1);
            }
            else
            {
                // Columns 0..3 of the same I cell are clean background.
                // Preserve their horizontal phase.
                sourceBlockX = blockX & 3;
            }
            return templateBlocks[blockY * 8 + sourceBlockX];
        }

        private static void PatchFont(
            string inputPath, string outputPath, string fontPath,
            List<GlyphMapping> mappings, string previewPath,
            float glyphEmSize, bool useNativeBlocks)
        {
            byte[] xpr = File.ReadAllBytes(inputPath);
            Dictionary<ushort, List<NativeBlock>> nativeDictionary =
                useNativeBlocks ? BuildNativeDictionary(xpr) : null;

            using (Bitmap preview = new Bitmap(
                32 * mappings.Count, 32,
                PixelFormat.Format32bppArgb))
            using (Graphics previewGraphics = Graphics.FromImage(preview))
            {
                previewGraphics.Clear(Color.Black);
                for (int mappingIndex = 0; mappingIndex < mappings.Count; ++mappingIndex)
                {
                    GlyphMapping mapping = mappings[mappingIndex];
                    int width;
                    int height;
                    int baseOffset = GetPageBase(
                        xpr, mapping.Lead, out width, out height);
                    int blocksPerRow = width / 4;

                    using (Bitmap glyph = RenderGlyph(
                        mapping.Character, fontPath, 32,
                        glyphEmSize, useNativeBlocks ? 2 : 1))
                    {
                        previewGraphics.DrawImageUnscaled(
                            glyph, mappingIndex * 32, 0);
                        for (int blockY = 0; blockY < 8; ++blockY)
                        {
                            for (int blockX = 0; blockX < 8; ++blockX)
                            {
                                byte[] encoded = null;
                                NativeBlock nativeBlock = null;
                                if (useNativeBlocks)
                                {
                                    nativeBlock = FindNativeBlock(
                                        nativeDictionary,
                                        ReadMaskBlock(glyph, blockX, blockY));
                                }
                                else
                                {
                                    Color[] pixels = new Color[16];
                                    for (int pixelY = 0;
                                         pixelY < 4; ++pixelY)
                                    {
                                        for (int pixelX = 0;
                                             pixelX < 4; ++pixelX)
                                        {
                                            pixels[pixelY * 4 + pixelX] =
                                                glyph.GetPixel(
                                                    blockX * 4 + pixelX,
                                                    blockY * 4 + pixelY);
                                        }
                                    }
                                    encoded = EncodeDxt5Block(pixels);
                                }

                                int cellIndex =
                                    mapping.Trail - 0x40;
                                int cellX = cellIndex & 15;
                                int cellY = cellIndex >> 4;
                                int globalBlockX =
                                    cellX * 8 + blockX;
                                int globalBlockY =
                                    cellY * 8 + blockY;
                                int destination = baseOffset +
                                    (globalBlockY * blocksPerRow +
                                     globalBlockX) * 16;
                                if (useNativeBlocks)
                                {
                                    Buffer.BlockCopy(
                                        nativeBlock.Raw, 0,
                                        xpr, destination, 16);
                                }
                                else
                                {
                                    // The XPR fetch constant specifies Xenos
                                    // endian 8-in-16, so canonical BC3 bytes
                                    // are stored with adjacent bytes swapped.
                                    for (int byteIndex = 0;
                                         byteIndex < 16; ++byteIndex)
                                    {
                                        xpr[destination + (byteIndex ^ 1)] =
                                            encoded[byteIndex];
                                    }
                                }
                            }
                        }
                    }
                }
                preview.Save(previewPath, ImageFormat.Png);
            }
            File.WriteAllBytes(outputPath, xpr);
            Console.WriteLine(
                "Patched {0} mapped glyph cells in {1} ({2})",
                mappings.Count, outputPath,
                useNativeBlocks ? "native blocks" : "encoded BC3");
        }

        private static void WriteManifest(
            string path, List<GlyphMapping> mappings, int replacementLength,
            string mode, string displayText)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("Dream C Club " + mode);
            builder.AppendLine(
                "Glyph method: white RGB with anti-aliased coverage in alpha");
            builder.AppendLine(
                "Texture: linear k_DXT4_5 / BC3, Xenos 8-in-16 endian");
            builder.AppendLine(
                "Fetch swizzle: 0x00000D10 -> RGBA (swizzle field 0x688)");
            builder.AppendLine();
            builder.AppendLine(displayText);
            builder.AppendLine("Replacement byte length: " + replacementLength);
            builder.AppendLine("Glyph mappings:");
            for (int mappingIndex = 0;
                 mappingIndex < mappings.Count; ++mappingIndex)
            {
                GlyphMapping mapping = mappings[mappingIndex];
                builder.AppendFormat(
                    "U+{0:X4} {1} -> {2:X2}{3:X2}\r\n",
                    (int)mapping.Character, mapping.Character,
                    mapping.Lead, mapping.Trail);
            }
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static int Main(string[] args)
        {
            bool japaneseCompare =
                args.Length == 8 &&
                string.Equals(
                    args[7], "--japanese-compare",
                    StringComparison.OrdinalIgnoreCase);
            bool japaneseRawCopy =
                args.Length == 8 &&
                string.Equals(
                    args[7], "--japanese-raw-copy",
                    StringComparison.OrdinalIgnoreCase);
            bool systemMenuManifest =
                args.Length == 9 &&
                string.Equals(
                    args[7], "--system-menu-manifest",
                    StringComparison.OrdinalIgnoreCase);
            bool explicitSystemMenuManifest =
                args.Length == 10 &&
                string.Equals(
                    args[7], "--system-menu-manifest-map",
                    StringComparison.OrdinalIgnoreCase);
            bool fontMapOnly =
                args.Length == 9 &&
                string.Equals(
                    args[7], "--font-map-only",
                    StringComparison.OrdinalIgnoreCase);
            if (args.Length != 7 &&
                !japaneseCompare && !japaneseRawCopy &&
                !systemMenuManifest && !explicitSystemMenuManifest &&
                !fontMapOnly)
            {
                Console.Error.WriteLine(
                    "Usage: DreamClubKoreanPatcher <unencrypted.xex> <font00.xpr> " +
                    "<font01.xpr> <medium.ttf> <bold.ttf> <output-dir> " +
                    "<output-xex-name> " +
                    "[--japanese-compare|--japanese-raw-copy|" +
                    "--system-menu-manifest <manifest.json>|" +
                    "--system-menu-manifest-map <manifest.json> " +
                    "<glyph-map.json>|--font-map-only <glyph-map.json>]");
                return 2;
            }

            Directory.CreateDirectory(args[5]);
            TranslationManifest translationManifest =
                systemMenuManifest || explicitSystemMenuManifest
                ? LoadTranslationManifest(args[8])
                : null;
            List<GlyphMapping> mappings =
                explicitSystemMenuManifest || fontMapOnly
                ? LoadExplicitMappings(
                    explicitSystemMenuManifest ? args[9] : args[8])
                : systemMenuManifest
                ? BuildManifestMappings(translationManifest)
                : japaneseCompare || japaneseRawCopy
                ? BuildMappings(JapaneseCompareText, false, 0xE0)
                : BuildMappings();
            List<GlyphMapping> nativeMappings = japaneseCompare
                ? BuildMappings(JapaneseCompareText, false, 0xE1)
                : null;
            byte[] replacement = systemMenuManifest
                || explicitSystemMenuManifest || fontMapOnly
                ? null
                : japaneseCompare
                ? EncodeJapaneseComparison(mappings, nativeMappings)
                : japaneseRawCopy
                ? EncodeJapaneseRawCopyComparison(mappings)
                : EncodeReplacement(mappings);
            string mode = systemMenuManifest
                || explicitSystemMenuManifest || fontMapOnly
                ? "Korean system-menu translation patch"
                : japaneseCompare
                ? "Japanese original/generated/raw-copy comparison patch"
                : japaneseRawCopy
                ? "Japanese original/raw-cell-copy comparison patch"
                : "Korean autosave warning development patch";
            string displayText = systemMenuManifest
                || explicitSystemMenuManifest
                ? String.Format(
                    "Translated entries: {0}\nMapped Hangul glyphs: {1}",
                    translationManifest.entries.Length,
                    mappings.Count)
                : fontMapOnly
                ? String.Format(
                    "Mapped Hangul glyphs: {0}", mappings.Count)
                : japaneseCompare
                ? "O:" + JapaneseCompareText + "\n" +
                  "G:" + JapaneseCompareText + "\n" +
                  "C:" + JapaneseCompareText
                : japaneseRawCopy
                ? "O:" + JapaneseCompareText + "\n" +
                  "C:" + JapaneseCompareText
                : KoreanWarning;
            string outputXex = Path.Combine(args[5], args[6]);
            string outputFont00 = Path.Combine(args[5], "font00.xpr");
            string outputFont01 = Path.Combine(args[5], "font01.xpr");
            try
            {
                if (fontMapOnly)
                {
                    File.Copy(args[0], outputXex, true);
                }
                else if (systemMenuManifest || explicitSystemMenuManifest)
                {
                    PatchXexManifest(
                        args[0], outputXex,
                        translationManifest, mappings);
                }
                else
                {
                    PatchXex(args[0], outputXex, replacement);
                }
                if (japaneseRawCopy)
                {
                    CopyOriginalJapaneseGlyphs(
                        args[1], outputFont00, mappings);
                    CopyOriginalJapaneseGlyphs(
                        args[2], outputFont01, mappings);
                }
                else if (japaneseCompare)
                {
                    string tempFont00 =
                        Path.Combine(args[5], "font00_encoded_tmp.xpr");
                    string tempFont01 =
                        Path.Combine(args[5], "font01_encoded_tmp.xpr");
                    PatchFont(
                        args[1], tempFont00, args[3], mappings,
                        Path.Combine(
                            args[5], "font00_encoded_preview.png"),
                        Font00GlyphEmSize, false);
                    CopyOriginalJapaneseGlyphs(
                        tempFont00, outputFont00, nativeMappings);
                    PatchFont(
                        args[2], tempFont01, args[3], mappings,
                        Path.Combine(
                            args[5], "font01_encoded_preview.png"),
                        Font01GlyphEmSize, false);
                    CopyOriginalJapaneseGlyphs(
                        tempFont01, outputFont01, nativeMappings);
                    File.Delete(tempFont00);
                    File.Delete(tempFont01);
                    mappings.AddRange(nativeMappings);
                }
                else
                {
                    PatchFont(
                        args[1], outputFont00, args[3], mappings,
                        Path.Combine(
                            args[5], "font00_glyph_preview.png"),
                        Font00GlyphEmSize, false);
                    PatchFont(
                        args[2], outputFont01, args[3], mappings,
                        Path.Combine(
                            args[5], "font01_glyph_preview.png"),
                        Font01GlyphEmSize, false);
                }
                WriteManifest(
                    Path.Combine(args[5], "patch_manifest.txt"),
                    mappings,
                    replacement == null ? 0 : replacement.Length,
                    mode, displayText);
                Console.WriteLine("Output: " + args[5]);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "Patch failed: " + exception.GetType().FullName);
                try
                {
                    Console.Error.WriteLine(exception.Message);
                    Console.Error.WriteLine(exception.StackTrace);
                }
                catch
                {
                }
                return 1;
            }
        }
    }
}
