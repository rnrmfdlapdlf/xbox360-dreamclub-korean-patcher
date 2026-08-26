using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace DreamClubKoreanPatcher
{
    internal static class RuntimeMetadataBuilder
    {
        private const uint EmptyTextSlot = 0xFA0A1F00;

        private sealed class Segment
        {
            public int MarkerOffset;
            public int DataOffset;
            public int DataEnd;
        }

        private sealed class PoolString
        {
            public int RelativeOffset;
            public int FileOffset;
            public int ByteLength;
            public string Text;
        }

        private sealed class TextRecord
        {
            public int RecordOffset;
            public int FirstPointerOffset;
            public List<PoolString> Lines;
        }

        private sealed class DialogueNode
        {
            public string NodeType;
            public string RecordType;
            public int RecordOffset;
            public int FirstPointerOffset;
            public int? SpeakerId;
            public bool Voiced;
            public int? VoiceIndex;
            public List<PoolString> Lines;
        }

        private sealed class SupplementalNode
        {
            public string Text;
            public readonly List<Dictionary<string, object>> Locations =
                new List<Dictionary<string, object>>();
        }

        private sealed class PeSection
        {
            public string Name;
            public int RawOffset;
            public int RawSize;
            public int VirtualAddress;
        }

        private sealed class VerifiedRange
        {
            public string Section;
            public int Start;
            public int End;
        }

        private sealed class ManifestEntry
        {
            public string Text;
            public readonly List<Dictionary<string, object>> Occurrences =
                new List<Dictionary<string, object>>();
        }

        public static void BuildDialogueMetadata(
            string gameRoot, string outputRoot,
            JavaScriptSerializer serializer, Encoding shiftJis)
        {
            Directory.CreateDirectory(outputRoot);
            string[] ids = { "00", "01", "02", "03", "04", "05", "06", "07", "08", "09", "99" };
            foreach (string id in ids)
            {
                byte[] data = File.ReadAllBytes(Path.Combine(gameRoot, "s" + id + ".can"));
                List<Dictionary<string, object>> nodes = ParseDialogue(data, id, shiftJis);
                WriteJsonl(
                    Path.Combine(outputRoot, "s" + id + ".nodes.jsonl"),
                    nodes, serializer);
            }
        }

        public static void BuildSupplementalMetadata(
            string gameRoot, string dialogueRoot, string outputPath,
            JavaScriptSerializer serializer, Encoding shiftJis)
        {
            string[] ids = { "00", "01", "02", "03", "04", "05", "06", "07", "08", "09", "99" };
            HashSet<string> dialogueTexts = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                foreach (Dictionary<string, object> node in ReadJsonl(
                    Path.Combine(dialogueRoot, "s" + id + ".nodes.jsonl"), serializer))
                {
                    foreach (object value in ToArray(node["sourceLines"]))
                        dialogueTexts.Add(Convert.ToString(value));
                }
            }

            List<SupplementalNode> unique = new List<SupplementalNode>();
            Dictionary<string, SupplementalNode> byText =
                new Dictionary<string, SupplementalNode>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                byte[] data = File.ReadAllBytes(Path.Combine(gameRoot, "s" + id + ".can"));
                Segment lst = FindSegment(data, id + ".lst");
                Segment cmd = FindSegment(data, id + ".cmd");
                Segment psw = FindSegment(data, id + ".psw");
                Segment txt = FindSegment(data, id + ".txt");
                lst.DataEnd = cmd.MarkerOffset;
                psw.DataEnd = txt.MarkerOffset;

                HashSet<int> referenced = new HashSet<int>();
                foreach (Dictionary<string, object> node in ReadJsonl(
                    Path.Combine(dialogueRoot, "s" + id + ".nodes.jsonl"), serializer))
                {
                    foreach (object raw in ToArray(node["sourceLocations"]))
                    {
                        Dictionary<string, object> location =
                            (Dictionary<string, object>)raw;
                        referenced.Add(ParseHex(location["pswRelativeOffset"]));
                    }
                }

                Dictionary<int, List<int>> references = new Dictionary<int, List<int>>();
                for (int offset = lst.DataOffset; offset + 4 <= cmd.MarkerOffset; offset += 4)
                {
                    int value = unchecked((int)ReadBe32(data, offset));
                    List<int> offsets;
                    if (!references.TryGetValue(value, out offsets))
                    {
                        offsets = new List<int>();
                        references.Add(value, offsets);
                    }
                    offsets.Add(offset);
                }

                foreach (PoolString item in ParseStringPool(
                    data, psw.DataOffset, psw.DataEnd, shiftJis))
                {
                    if (referenced.Contains(item.RelativeOffset) ||
                        dialogueTexts.Contains(item.Text) ||
                        ScriptJapaneseCharacterCount(item.Text) == 0) continue;
                    SupplementalNode node;
                    if (!byText.TryGetValue(item.Text, out node))
                    {
                        node = new SupplementalNode { Text = item.Text };
                        byText.Add(item.Text, node);
                        unique.Add(node);
                    }
                    List<int> lstOffsets;
                    if (!references.TryGetValue(item.RelativeOffset, out lstOffsets))
                        lstOffsets = new List<int>();
                    node.Locations.Add(new Dictionary<string, object>
                    {
                        { "scenarioId", id },
                        { "pswRelativeOffset", Hex(item.RelativeOffset) },
                        { "shiftJisByteLength", item.ByteLength },
                        { "lstReferenceOffsets", lstOffsets.Select(Hex).ToArray() }
                    });
                }
            }

            List<Dictionary<string, object>> output = new List<Dictionary<string, object>>();
            for (int index = 0; index < unique.Count; ++index)
            {
                output.Add(new Dictionary<string, object>
                {
                    { "id", "psw_missing_" + index.ToString("00000") },
                    { "sourceText", unique[index].Text },
                    { "locations", unique[index].Locations.ToArray() }
                });
            }
            WriteJsonl(outputPath, output, serializer);
        }

        public static void BuildSongMetadata(
            string gameRoot, string outputRoot,
            JavaScriptSerializer serializer, Encoding shiftJis)
        {
            Directory.CreateDirectory(outputRoot);
            for (int song = 0; song <= 10; ++song)
            {
                string id = song.ToString("00");
                byte[] data = File.ReadAllBytes(Path.Combine(gameRoot, "song" + id + ".data"));
                List<Dictionary<string, object>> nodes = new List<Dictionary<string, object>>();
                int cursor = 0;
                while (cursor < data.Length)
                {
                    int end = Array.IndexOf(data, (byte)0, cursor);
                    if (end < 0) break;
                    int length = end - cursor;
                    if (length >= 4 && length <= 512)
                    {
                        string text;
                        if (TryDecode(data, cursor, length, shiftJis, out text) && IsLyricText(text))
                        {
                            int index = nodes.Count;
                            nodes.Add(new Dictionary<string, object>
                            {
                                { "id", "song" + id + "_L" + index.ToString("000") },
                                { "fileOffset", Hex(cursor) },
                                { "shiftJisByteLength", length },
                                { "sourceText", text }
                            });
                        }
                    }
                    cursor = end + 1;
                }
                if (nodes.Count == 0)
                    throw new InvalidDataException("노래 문자열을 찾지 못했습니다: song" + id + ".data");
                WriteJsonl(Path.Combine(outputRoot, "song" + id + ".nodes.jsonl"), nodes, serializer);
            }
        }

        public static void BuildDefaultManifest(
            string flatPath, string outputPath,
            JavaScriptSerializer serializer, Encoding shiftJis)
        {
            byte[] data = File.ReadAllBytes(flatPath);
            List<PeSection> sections = ReadPeSections(data);
            VerifiedRange[] ranges =
            {
                new VerifiedRange { Section = ".rdata", Start = 0x000D3614, End = 0x000D37D0 },
                new VerifiedRange { Section = ".rdata", Start = 0x000D5DF8, End = 0x000DE3A0 },
                new VerifiedRange { Section = ".rdata", Start = 0x000E337C, End = 0x000E6140 },
                new VerifiedRange { Section = ".rdata", Start = 0x000E7B8C, End = 0x000E8CE0 },
                new VerifiedRange { Section = ".data", Start = 0x0076DAB0, End = 0x00818338 },
                new VerifiedRange { Section = ".data", Start = 0x00823AC8, End = 0x00824320 },
                new VerifiedRange { Section = ".data", Start = 0x00833D70, End = 0x00835ED0 }
            };

            List<ManifestEntry> entries = new List<ManifestEntry>();
            Dictionary<string, ManifestEntry> byText =
                new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
            int offset = 0;
            while (offset < data.Length)
            {
                if (offset > 0 && data[offset - 1] != 0) { ++offset; continue; }
                int start = offset;
                int cursor = start;
                bool validBytes = true;
                while (cursor < data.Length && cursor - start <= 8192)
                {
                    byte value = data[cursor];
                    if (value == 0) break;
                    if ((value >= 0x20 && value <= 0x7E) || value == 9 || value == 10 ||
                        value == 13 || (value >= 0xA1 && value <= 0xDF))
                    {
                        ++cursor;
                        continue;
                    }
                    if (IsShiftJisLead(value) && cursor + 1 < data.Length &&
                        IsShiftJisTrail(data[cursor + 1]))
                    {
                        cursor += 2;
                        continue;
                    }
                    validBytes = false;
                    break;
                }
                int length = cursor - start;
                if (validBytes && cursor < data.Length && data[cursor] == 0 &&
                    length > 0 && length <= 8192)
                {
                    PeSection section = LocateSection(sections, start);
                    VerifiedRange range = ranges.FirstOrDefault(item =>
                        section != null && item.Section == section.Name &&
                        start >= item.Start && start <= item.End);
                    if (range != null)
                    {
                        string text;
                        bool decoded = TryDecode(data, start, length, shiftJis, out text);
                        if (!decoded) text = DecodeLoose(data, start, length, shiftJis);
                        if (LooksLikeText(text))
                        {
                            ManifestEntry entry;
                            if (!byText.TryGetValue(text, out entry))
                            {
                                entry = new ManifestEntry { Text = text };
                                byText.Add(text, entry);
                                entries.Add(entry);
                            }
                            int virtualAddress = section.VirtualAddress + start - section.RawOffset;
                            entry.Occurrences.Add(new Dictionary<string, object>
                            {
                                { "fileOffset", start },
                                { "virtualAddress", virtualAddress },
                                { "byteLimit", length }
                            });
                        }
                    }
                    offset = cursor + 1;
                }
                else offset = cursor + 1;
            }

            if (entries.Count < 1170)
                throw new InvalidDataException("실행 파일 번역 위치를 충분히 찾지 못했습니다.");
            List<Dictionary<string, object>> outputEntries = new List<Dictionary<string, object>>();
            for (int index = 0; index < entries.Count; ++index)
            {
                outputEntries.Add(new Dictionary<string, object>
                {
                    { "id", "EXE_" + index.ToString("0000") },
                    { "sourceText", entries[index].Text },
                    { "translation", "" },
                    { "status", index < 1170 ? "source_preserved" : "excluded" },
                    { "occurrences", entries[index].Occurrences.ToArray() }
                });
            }
            Dictionary<string, object> manifest = new Dictionary<string, object>
            {
                { "format", "dream-club-runtime-derived-v1" },
                { "entries", outputEntries.ToArray() }
            };
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, serializer.Serialize(manifest), new UTF8Encoding(false));
        }

        private static List<Dictionary<string, object>> ParseDialogue(
            byte[] data, string id, Encoding shiftJis)
        {
            Segment lst = FindSegment(data, id + ".lst");
            Segment cmd = FindSegment(data, id + ".cmd");
            Segment psw = FindSegment(data, id + ".psw");
            Segment txt = FindSegment(data, id + ".txt");
            lst.DataEnd = cmd.MarkerOffset;
            psw.DataEnd = txt.MarkerOffset;
            List<PoolString> strings = ParseStringPool(data, psw.DataOffset, psw.DataEnd, shiftJis);
            Dictionary<int, PoolString> byOffset = strings.ToDictionary(item => item.RelativeOffset);

            List<TextRecord> simple = ParseSlotRecords(data, lst, byOffset, 0x63);
            List<TextRecord> choices = ParseSlotRecords(data, lst, byOffset, 0x0B);
            HashSet<int> simplePointers = new HashSet<int>(simple.Select(item => item.FirstPointerOffset));
            List<DialogueNode> nodes = new List<DialogueNode>();
            foreach (TextRecord record in simple)
            {
                nodes.Add(new DialogueNode
                {
                    NodeType = "text-page", RecordType = "simple-text-0x63",
                    RecordOffset = record.RecordOffset,
                    FirstPointerOffset = record.FirstPointerOffset,
                    Lines = record.Lines
                });
            }

            for (int offset = lst.DataOffset; offset + 8 <= lst.DataEnd; offset += 4)
            {
                int firstRelative = unchecked((int)ReadBe32(data, offset));
                int secondRelative = unchecked((int)ReadBe32(data, offset + 4));
                PoolString first;
                PoolString second;
                if (!byOffset.TryGetValue(firstRelative, out first) ||
                    !byOffset.TryGetValue(secondRelative, out second) ||
                    secondRelative <= firstRelative || simplePointers.Contains(offset)) continue;
                int firstTerminator = psw.DataOffset + secondRelative - 1;
                if (firstTerminator < first.FileOffset || data[firstTerminator] != 0) continue;
                bool paddingOnly = true;
                for (int check = first.FileOffset + first.ByteLength;
                    check <= firstTerminator; ++check)
                {
                    if (data[check] != 0) { paddingOnly = false; break; }
                }
                if (!paddingOnly) continue;
                bool speaker = offset >= lst.DataOffset + 28 &&
                    ReadBe32(data, offset - 28) == 3 && ReadBe32(data, offset - 24) <= 10;
                nodes.Add(new DialogueNode
                {
                    NodeType = "text-page",
                    RecordType = speaker ? "voiced-dialogue" : "complex-two-line-candidate",
                    RecordOffset = offset,
                    FirstPointerOffset = offset,
                    SpeakerId = speaker ? (int?)ReadBe32(data, offset - 24) : null,
                    Voiced = speaker,
                    VoiceIndex = speaker ? (int?)ReadBe32(data, offset - 12) : null,
                    Lines = new List<PoolString> { first, second }
                });
            }
            foreach (TextRecord record in choices)
            {
                nodes.Add(new DialogueNode
                {
                    NodeType = "choice-option", RecordType = "choice-option-0x0B",
                    RecordOffset = record.RecordOffset,
                    FirstPointerOffset = record.FirstPointerOffset,
                    Lines = record.Lines
                });
            }
            nodes.Sort(delegate(DialogueNode left, DialogueNode right)
            {
                int compare = left.FirstPointerOffset.CompareTo(right.FirstPointerOffset);
                return compare != 0 ? compare : StringComparer.Ordinal.Compare(left.NodeType, right.NodeType);
            });

            List<Dictionary<string, object>> output = new List<Dictionary<string, object>>();
            for (int index = 0; index < nodes.Count; ++index)
            {
                DialogueNode node = nodes[index];
                string[] lines = node.Lines.Select(item => item.Text).ToArray();
                string source = String.Join("\n", lines);
                output.Add(new Dictionary<string, object>
                {
                    { "id", id + "_" + (node.NodeType == "choice-option" ? "C" : "N") + index.ToString("00000") },
                    { "storageOrder", index },
                    { "nodeType", node.NodeType },
                    { "recordType", node.RecordType },
                    { "lstRecordOffset", Hex(node.RecordOffset) },
                    { "speaker", new Dictionary<string, object> { { "id", node.SpeakerId }, { "name", null } } },
                    { "voiced", node.Voiced },
                    { "voiceIndexCandidate", node.VoiceIndex },
                    { "sourceLines", lines },
                    { "sourceText", source },
                    { "sourceLocations", node.Lines.Select(item =>
                        new Dictionary<string, object>
                        {
                            { "pswRelativeOffset", Hex(item.RelativeOffset) },
                            { "pswFileOffset", Hex(item.FileOffset) },
                            { "shiftJisByteLength", item.ByteLength }
                        }).ToArray() },
                    { "protectedTokens", ProtectedTokens(source, node.NodeType).ToArray() },
                    { "translationStatus", "untranslated" }
                });
            }
            return output;
        }

        private static List<TextRecord> ParseSlotRecords(
            byte[] data, Segment lst, Dictionary<int, PoolString> strings, uint opcode)
        {
            List<TextRecord> records = new List<TextRecord>();
            for (int offset = lst.DataOffset; offset + 36 <= lst.DataEnd; offset += 4)
            {
                if (ReadBe32(data, offset) != opcode) continue;
                List<PoolString> lines = new List<PoolString>();
                bool seenEmpty = false;
                bool valid = true;
                for (int slot = 0; slot < 8; ++slot)
                {
                    uint value = ReadBe32(data, offset + 4 + slot * 4);
                    if (value == EmptyTextSlot) { seenEmpty = true; continue; }
                    PoolString item;
                    if (seenEmpty || !strings.TryGetValue(unchecked((int)value), out item))
                    {
                        valid = false;
                        break;
                    }
                    lines.Add(item);
                }
                if (valid && lines.Count != 0)
                {
                    records.Add(new TextRecord
                    {
                        RecordOffset = offset,
                        FirstPointerOffset = offset + 4,
                        Lines = lines
                    });
                }
            }
            return records;
        }

        private static List<PoolString> ParseStringPool(
            byte[] data, int start, int end, Encoding shiftJis)
        {
            List<PoolString> result = new List<PoolString>();
            int cursor = start;
            while (cursor < end)
            {
                while (cursor < end && data[cursor] == 0) ++cursor;
                if (cursor >= end) break;
                int stringEnd = Array.IndexOf(data, (byte)0, cursor, end - cursor);
                if (stringEnd < 0) break;
                string text;
                if (TryDecode(data, cursor, stringEnd - cursor, shiftJis, out text))
                {
                    result.Add(new PoolString
                    {
                        RelativeOffset = cursor - start,
                        FileOffset = cursor,
                        ByteLength = stringEnd - cursor,
                        Text = text
                    });
                }
                cursor = stringEnd + 1;
            }
            return result;
        }

        private static Segment FindSegment(byte[] data, string name)
        {
            byte[] marker = Encoding.ASCII.GetBytes(name + "\0");
            int markerOffset = FindBytes(data, marker);
            if (markerOffset < 0)
                throw new InvalidDataException("CAN 세그먼트를 찾지 못했습니다: " + name);
            return new Segment
            {
                MarkerOffset = markerOffset,
                DataOffset = (markerOffset + marker.Length + 15) & ~15
            };
        }

        private static int FindBytes(byte[] data, byte[] value)
        {
            for (int offset = 0; offset <= data.Length - value.Length; ++offset)
            {
                bool match = true;
                for (int index = 0; index < value.Length; ++index)
                    if (data[offset + index] != value[index]) { match = false; break; }
                if (match) return offset;
            }
            return -1;
        }

        private static List<string> ProtectedTokens(string text, string nodeType)
        {
            List<string> result = new List<string>();
            int cursor = 0;
            while (cursor < text.Length)
            {
                if (text[cursor] != '○') { ++cursor; continue; }
                int end = cursor;
                while (end < text.Length && text[end] == '○') ++end;
                if (end - cursor >= 2) AddUnique(result, text.Substring(cursor, end - cursor));
                cursor = end;
            }
            if (nodeType == "choice-option" && text.IndexOf('n') >= 0) AddUnique(result, "n");
            int tail = text.Length;
            while (tail > 0 && IsAsciiControlLetter(text[tail - 1])) --tail;
            if (tail < text.Length) AddUnique(result, text.Substring(tail));
            return result;
        }

        private static bool IsAsciiControlLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') ||
                (value >= 'a' && value <= 'z') || value == '`';
        }

        private static void AddUnique(List<string> items, string value)
        {
            if (!items.Contains(value)) items.Add(value);
        }

        private static bool IsLyricText(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            int japanese = ScriptJapaneseCharacterCount(text);
            bool privateUse = text.Any(value => value >= 0xE000 && value <= 0xF8FF);
            bool halfwidth = text.Any(value => value >= 0xFF61 && value <= 0xFF9F);
            bool control = text.Any(value => value < 0x20 || value == 0x7F);
            return japanese >= 2 && (double)japanese / text.Length >= 0.25 &&
                !privateUse && !halfwidth && !control;
        }

        private static bool LooksLikeText(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            int japanese = BroadJapaneseCharacterCount(text);
            int visible = text.Count(value => value != '\r' && value != '\n' &&
                value != '\t' && value != ' ');
            int halfwidth = text.Count(value => value >= 0xFF61 && value <= 0xFF9F);
            int disallowed = text.Count(value => !IsAllowedTextCharacter(value));
            return japanese >= 1 && visible != 0 && halfwidth == 0 && disallowed == 0 &&
                (double)japanese / visible >= 0.35;
        }

        private static int ScriptJapaneseCharacterCount(string text)
        {
            return text.Count(value =>
                (value >= 0x3041 && value <= 0x309F) ||
                (value >= 0x30A1 && value <= 0x30FA) ||
                (value >= 0x31F0 && value <= 0x31FF) ||
                (value >= 0x3400 && value <= 0x4DBF) ||
                (value >= 0x4E00 && value <= 0x9FFF) ||
                (value >= 0xF900 && value <= 0xFAFF));
        }

        private static int BroadJapaneseCharacterCount(string text)
        {
            return text.Count(value =>
                (value >= 0x3000 && value <= 0x30FF) ||
                (value >= 0x31F0 && value <= 0x31FF) ||
                (value >= 0x3400 && value <= 0x4DBF) ||
                (value >= 0x4E00 && value <= 0x9FFF) ||
                (value >= 0xF900 && value <= 0xFAFF));
        }

        private static bool IsAllowedTextCharacter(char value)
        {
            return value == '\r' || value == '\n' || value == '\t' ||
                (value >= 0x20 && value <= 0x7E) ||
                (value >= 0x2010 && value <= 0x203B) ||
                (value >= 0x2190 && value <= 0x21FF) ||
                (value >= 0x2460 && value <= 0x27FF) ||
                (value >= 0x3000 && value <= 0x30FF) ||
                (value >= 0x31F0 && value <= 0x31FF) ||
                (value >= 0x3400 && value <= 0x4DBF) ||
                (value >= 0x4E00 && value <= 0x9FFF) ||
                (value >= 0xF900 && value <= 0xFAFF) ||
                (value >= 0xFF01 && value <= 0xFF60) ||
                (value >= 0xFFE0 && value <= 0xFFEE);
        }

        private static bool IsShiftJisLead(byte value)
        {
            return (value >= 0x81 && value <= 0x9F) ||
                (value >= 0xE0 && value <= 0xEF);
        }

        private static bool IsShiftJisTrail(byte value)
        {
            return (value >= 0x40 && value <= 0x7E) ||
                (value >= 0x80 && value <= 0xFC);
        }

        private static bool TryDecode(
            byte[] data, int offset, int length, Encoding encoding, out string text)
        {
            try
            {
                text = encoding.GetString(data, offset, length);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = null;
                return false;
            }
        }

        private static string DecodeLoose(
            byte[] data, int offset, int length, Encoding encoding)
        {
            StringBuilder result = new StringBuilder();
            int end = offset + length;
            for (int cursor = offset; cursor < end;)
            {
                int count = IsShiftJisLead(data[cursor]) && cursor + 1 < end ? 2 : 1;
                string part;
                if (TryDecode(data, cursor, count, encoding, out part)) result.Append(part);
                cursor += count;
            }
            return result.ToString();
        }

        private static List<PeSection> ReadPeSections(byte[] data)
        {
            List<PeSection> result = new List<PeSection>();
            if (data.Length < 0x40 || data[0] != 0x4D || data[1] != 0x5A) return result;
            int pe = BitConverter.ToInt32(data, 0x3C);
            if (pe < 0 || pe + 24 > data.Length || data[pe] != 'P' || data[pe + 1] != 'E')
                return result;
            int count = BitConverter.ToUInt16(data, pe + 6);
            int optional = BitConverter.ToUInt16(data, pe + 20);
            int table = pe + 24 + optional;
            for (int index = 0; index < count && table + index * 40 + 40 <= data.Length; ++index)
            {
                int item = table + index * 40;
                int nameLength = 0;
                while (nameLength < 8 && data[item + nameLength] != 0) ++nameLength;
                result.Add(new PeSection
                {
                    Name = Encoding.ASCII.GetString(data, item, nameLength),
                    RawSize = BitConverter.ToInt32(data, item + 16),
                    RawOffset = BitConverter.ToInt32(data, item + 20),
                    VirtualAddress = BitConverter.ToInt32(data, item + 12)
                });
            }
            return result;
        }

        private static PeSection LocateSection(IEnumerable<PeSection> sections, int offset)
        {
            return sections.FirstOrDefault(item =>
                offset >= item.RawOffset && offset < item.RawOffset + item.RawSize);
        }

        private static uint ReadBe32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static string Hex(int value)
        {
            return "0x" + value.ToString("X8");
        }

        private static int ParseHex(object value)
        {
            string text = Convert.ToString(value);
            return Convert.ToInt32(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? text.Substring(2) : text, 16);
        }

        private static object[] ToArray(object value)
        {
            object[] direct = value as object[];
            if (direct != null) return direct;
            System.Collections.IEnumerable enumerable = value as System.Collections.IEnumerable;
            if (enumerable == null || value is string)
                throw new InvalidDataException("JSON 배열이 필요합니다.");
            List<object> output = new List<object>();
            foreach (object item in enumerable) output.Add(item);
            return output.ToArray();
        }

        private static List<Dictionary<string, object>> ReadJsonl(
            string path, JavaScriptSerializer serializer)
        {
            return File.ReadLines(path, Encoding.UTF8)
                .Where(line => !String.IsNullOrWhiteSpace(line))
                .Select(line => serializer.Deserialize<Dictionary<string, object>>(line))
                .ToList();
        }

        private static void WriteJsonl(
            string path, IEnumerable<Dictionary<string, object>> rows,
            JavaScriptSerializer serializer)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, rows.Select(serializer.Serialize).ToArray(),
                new UTF8Encoding(false));
        }
    }
}
