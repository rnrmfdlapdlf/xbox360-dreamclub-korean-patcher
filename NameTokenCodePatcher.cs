using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DreamClubKoreanPatcher
{
    // Original Xbox 360 DREAM C CLUB only. All offsets address XexTool's flat image.
    // Preserve the native 16-byte token and the full-name token. Add a fallback
    // for exactly six circles, leaving translated honorifics/particles in the input.
    internal static class NameTokenCodePatcher
    {
        private const int TextOffset = 0x110000;
        private const int OriginalTextLength = 5771316;
        private const int TextRawLength = 5771776;
        private const string OriginalTextHash =
            "332173B6336D2FA7E138F77E5E6AE3B4F719804161CF776E69DF895F7D694065";

        // Bridge words are disassembled alongside their addresses below. They
        // occupy zero-filled alignment padding already present in the executable
        // section and physical XEX block; neither section allocation nor file grows.
        // The three mail callers allocate and clear 25 bytes. Saved names use
        // 13-byte fields (12 bytes plus NUL), and the 30 honorifics use <=6 bytes.
        // Byte 24 safely stores the bare-name length beyond the formatted string's
        // NUL. The original string remains available for native Japanese tokens.
        private static readonly uint[] DialogueBridge =
        {
            0x4BC16141, // 82691040: bl 0x822a7180
            0x2F030000, // 82691044: cmpwi cr6, r3, 0
            0x409A0008, // 82691048: bne cr6, 0x82691050
            0x4BB1D4C0, // 8269104C: b 0x821ae50c
            0x7E84A378, // 82691050: mr r4, r20
            0x38A0000C, // 82691054: li r5, 0xc
            0x7FC3F378, // 82691058: mr r3, r30
            0x4BC16125, // 8269105C: bl 0x822a7180
            0x2F030000, // 82691060: cmpwi cr6, r3, 0
            0x419A0008, // 82691064: beq cr6, 0x8269106c
            0x4BB1D590, // 82691068: b 0x821ae5f8
            0x895E000C, // 8269106C: lbz r10, 0xc(r30)
            0x2F0A0081, // 82691070: cmpwi cr6, r10, 0x81
            0x409A0010, // 82691074: bne cr6, 0x82691084
            0x895E000D, // 82691078: lbz r10, 0xd(r30)
            0x2F0A009B, // 8269107C: cmpwi cr6, r10, 0x9b
            0x419A0078, // 82691080: beq cr6, 0x826910f8
            0x2F1D0002, // 82691084: cmpwi cr6, r29, 2
            0x4198001C, // 82691088: blt cr6, 0x826910a4
            0x895EFFFE, // 8269108C: lbz r10, -2(r30)
            0x2F0A0081, // 82691090: cmpwi cr6, r10, 0x81
            0x409A0010, // 82691094: bne cr6, 0x826910a4
            0x895EFFFF, // 82691098: lbz r10, -1(r30)
            0x2F0A009B, // 8269109C: cmpwi cr6, r10, 0x9b
            0x419A0058, // 826910A0: beq cr6, 0x826910f8
            0x7F26CB78, // 826910A4: mr r6, r25
            0x38A10050, // 826910A8: addi r5, r1, 0x50
            0x2F100001, // 826910AC: cmpwi cr6, r16, 1
            0x419A0020, // 826910B0: beq cr6, 0x826910d0
            0x2F100000, // 826910B4: cmpwi cr6, r16, 0
            0x419A0020, // 826910B8: beq cr6, 0x826910d8
            0x2F10FFFF, // 826910BC: cmpwi cr6, r16, -1
            0x419A0018, // 826910C0: beq cr6, 0x826910d8
            0x7F06C378, // 826910C4: mr r6, r24
            0x38A1006A, // 826910C8: addi r5, r1, 0x6a
            0x4800000C, // 826910CC: b 0x826910d8
            0x7F46D378, // 826910D0: mr r6, r26
            0x38A1005D, // 826910D4: addi r5, r1, 0x5d
            0x7CDB3378, // 826910D8: mr r27, r6
            0x209F0040, // 826910DC: subfic r4, r31, 0x40
            0x7C7FE214, // 826910E0: add r3, r31, r28
            0x4BC15CDD, // 826910E4: bl 0x822a6dc0
            0x7FFFDA14, // 826910E8: add r31, r31, r27
            0x3BBD000C, // 826910EC: addi r29, r29, 0xc
            0x3BDE000C, // 826910F0: addi r30, r30, 0xc
            0x4BB1D5BC, // 826910F4: b 0x821ae6b0
            0x4BB1D500, // 826910F8: b 0x821ae5f8
        };

        private static readonly uint[] FormatterBridge =
        {
            0x7C6B1B78, // 826910FC: mr r11, r3
            0x3BC00000, // 82691100: li r30, 0
            0x7D5E58AE, // 82691104: lbzx r10, r30, r11
            0x2F0A0000, // 82691108: cmpwi cr6, r10, 0
            0x419A000C, // 8269110C: beq cr6, 0x82691118
            0x3BDE0001, // 82691110: addi r30, r30, 1
            0x4BFFFFF0, // 82691114: b 0x82691104
            0x4BC15AB9, // 82691118: bl 0x822a6bd0
            0x9BDF0018, // 8269111C: stb r30, 0x18(r31)
            0x4BB2AFE4, // 82691120: b 0x821bc104
        };

        private static readonly uint[] MailBridge =
        {
            0x3C80820E, // 82691124: lis r4, -0x7df2
            0x38847950, // 82691128: addi r4, r4, 0x7950
            0x4BC16D45, // 8269112C: bl 0x822a7e70
            0x2F030000, // 82691130: cmpwi cr6, r3, 0
            0x409A0008, // 82691134: bne cr6, 0x8269113c
            0x4BB2AE90, // 82691138: b 0x821bbfc8
            0x7C7D1B78, // 8269113C: mr r29, r3
            0x3863000C, // 82691140: addi r3, r3, 0xc
            0x3C80820E, // 82691144: lis r4, -0x7df2
            0x3884793C, // 82691148: addi r4, r4, 0x793c
            0x38A00004, // 8269114C: li r5, 4
            0x4BC16031, // 82691150: bl 0x822a7180
            0x2F030000, // 82691154: cmpwi cr6, r3, 0
            0x409A0010, // 82691158: bne cr6, 0x82691168
            0x3B200010, // 8269115C: li r25, 0x10
            0x7F5BD378, // 82691160: mr r27, r26
            0x48000048, // 82691164: b 0x826911ac
            0x7D7FE850, // 82691168: subf r11, r31, r29
            0x895D000C, // 8269116C: lbz r10, 0xc(r29)
            0x2F0A0081, // 82691170: cmpwi cr6, r10, 0x81
            0x409A0010, // 82691174: bne cr6, 0x82691184
            0x895D000D, // 82691178: lbz r10, 0xd(r29)
            0x2F0A009B, // 8269117C: cmpwi cr6, r10, 0x9b
            0x419A0034, // 82691180: beq cr6, 0x826911b4
            0x2F0B0002, // 82691184: cmpwi cr6, r11, 2
            0x4198001C, // 82691188: blt cr6, 0x826911a4
            0x895DFFFE, // 8269118C: lbz r10, -2(r29)
            0x2F0A0081, // 82691190: cmpwi cr6, r10, 0x81
            0x409A0010, // 82691194: bne cr6, 0x826911a4
            0x895DFFFF, // 82691198: lbz r10, -1(r29)
            0x2F0A009B, // 8269119C: cmpwi cr6, r10, 0x9b
            0x419A0014, // 826911A0: beq cr6, 0x826911b4
            0x3B20000C, // 826911A4: li r25, 0xc
            0x8B7C0018, // 826911A8: lbz r27, 0x18(r28)
            0x7FA3EB78, // 826911AC: mr r3, r29
            0x4BB2AE18, // 826911B0: b 0x821bbfc8
            0x387D0002, // 826911B4: addi r3, r29, 2
            0x4BFFFF6C, // 826911B8: b 0x82691124
        };


        private sealed class Change
        {
            public int Offset;
            public byte[] Before;
            public byte[] After;
            public string Purpose;
            public Change(int offset, byte[] before, byte[] after, string purpose)
            { Offset = offset; Before = before; After = after; Purpose = purpose; }
        }

        private static Change[] Changes()
        {
            return new[]
            {
                new Change(0x1AE500, Words(0x480F8C81), Words(0x484E2B41), "dialogue fallback"),
                new Change(0x1BC100, Words(0x480EAAD1), Words(0x484D4FFD), "mail name-prefix length"),
                new Change(0x1BBFC4, Words(0x480EBEAD), Words(0x484D5161), "mail token search"),
                new Change(0x1BBF78, Words(0x557B003E), Words(0x557A003E), "retain formatted length"),
                new Change(0x248, BitConverter.GetBytes(OriginalTextLength),
                    BitConverter.GetBytes(TextRawLength), "include existing padding in .text VirtualSize"),
                new Change(0x691040, new byte[188], Words(DialogueBridge), "dialogue bridge"),
                new Change(0x6910FC, new byte[40], Words(FormatterBridge), "formatter bridge"),
                new Change(0x691124, new byte[152], Words(MailBridge), "mail bridge")
            };
        }

        public static void Apply(string originalPath, string translatedPath,
            string outputPath, string reportPath)
        {
            byte[] original = File.ReadAllBytes(originalPath);
            byte[] translated = File.ReadAllBytes(translatedPath);
            if (original.Length < TextOffset + TextRawLength ||
                translated.Length < TextOffset + TextRawLength ||
                Hash(original, TextOffset, OriginalTextLength) != OriginalTextHash)
                throw new InvalidDataException("이름 치환 수정과 호환되는 드림클럽 본편 원본이 아닙니다.");
            ChoiceTextCodePatcher.Verify(translated);
            // The established choice fix is the only executable change allowed
            // before applying this patch. Reject unknown patches and second use.
            for (int i = TextOffset; i < TextOffset + TextRawLength; ++i)
            {
                if (i >= 0x1370F8 && i < 0x1371B4) continue;
                if (original[i] != translated[i])
                    throw new InvalidDataException("이름 치환 수정 전 알 수 없는 실행 코드 변경이 있습니다.");
            }
            for (int i = TextOffset + OriginalTextLength; i < TextOffset + TextRawLength; ++i)
                if (original[i] != 0)
                    throw new InvalidDataException("이름 치환 보조 코드용 정렬 공간이 비어 있지 않습니다.");
            VerifyNameData(translated);
            byte[] output = (byte[])translated.Clone();
            List<object> changes = new List<object>();
            int changedBytes = 0;
            foreach (Change change in Changes())
            {
                if (change.Before.Length != change.After.Length)
                    throw new InvalidDataException("이름 치환 코드 크기가 맞지 않습니다.");
                for (int i = 0; i < change.Before.Length; ++i)
                {
                    if (original[change.Offset + i] != change.Before[i] ||
                        translated[change.Offset + i] != change.Before[i])
                        throw new InvalidDataException("이름 치환 코드 원본 바이트가 맞지 않습니다.");
                    if (change.Before[i] != change.After[i]) ++changedBytes;
                }
                Buffer.BlockCopy(change.After, 0, output, change.Offset, change.After.Length);
                changes.Add(new Dictionary<string, object>
                {
                    { "flatOffset", "0x" + change.Offset.ToString("X") },
                    { "bytes", change.After.Length }, { "purpose", change.Purpose }
                });
            }
            Verify(output);
            File.WriteAllBytes(outputPath, output);
            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "format", "dream-club-name-token-v1" }, { "changedBytes", changedBytes },
                { "changedOutsideAllowedRanges", 0 }, { "helperBytes", 380 },
                { "newAllocationBytes", 0 }, { "mailNamePrefixMetadataOffset", 24 },
                { "nativeTokenPreserved", true }, { "fullNameTokenPreserved", true },
                { "translationDataChanged", false }, { "changes", changes },
                { "inputSha256", Hash(translated, 0, translated.Length) },
                { "outputSha256", Hash(output, 0, output.Length) }
            };
            File.WriteAllText(reportPath, new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
        }

        public static void Verify(byte[] flat)
        {
            foreach (Change change in Changes())
            {
                if (flat.Length < change.Offset + change.After.Length)
                    throw new InvalidDataException("이름 치환 실행 코드가 없습니다.");
                for (int i = 0; i < change.After.Length; ++i)
                    if (flat[change.Offset + i] != change.After[i])
                        throw new InvalidDataException("XEX 재구성 후 이름 치환 코드 검증에 실패했습니다.");
            }
            VerifyNameData(flat);
        }

        private static void VerifyNameData(byte[] flat)
        {
            for (int i = 0; i < 12; ++i)
            {
                if (flat[0xE7930 + i] != (i % 2 == 0 ? 0x81 : 0x9B) ||
                    flat[0xE7950 + i] != (i % 2 == 0 ? 0x81 : 0x9B))
                    throw new InvalidDataException("이름 자리표시자 상수가 다릅니다.");
            }
            if (ReadBe32(flat, 0xE793C) != 0x82B382F1 || flat[0xE795C] != 0)
                throw new InvalidDataException("원본 이름 토큰의 경칭 또는 종료 문자가 다릅니다.");
            for (int id = 0x2FC; id < 0x31A; ++id)
            {
                long offset = (long)ReadBe32(flat, 0x85B8A0 + id * 4) - 0x82000000L;
                if (offset < 0 || offset + 6 >= flat.Length)
                    throw new InvalidDataException("이름 경칭의 주소가 올바르지 않습니다.");
                int length = 0;
                while (length <= 6 && flat[(int)offset + length] != 0) ++length;
                if (length > 6)
                    throw new InvalidDataException("이름 경칭이 검증된 메일 버퍼 크기를 초과합니다.");
            }
        }

        private static uint ReadBe32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                ((uint)data[offset + 2] << 8) | data[offset + 3];
        }
        private static byte[] Words(params uint[] words)
        {
            byte[] bytes = new byte[words.Length * 4];
            for (int i = 0; i < words.Length; ++i)
                for (int j = 0; j < 4; ++j) bytes[i * 4 + j] = (byte)(words[i] >> (24 - j * 8));
            return bytes;
        }
        private static string Hash(byte[] data, int offset, int length)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data, offset, length)).Replace("-", "");
        }
    }
}
