using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DreamClubKoreanPatcher
{
    // DREAM C CLUB (445007F0), original Japanese executable only.
    // XexTool -b produces a flat image: indexes here are RVAs, not PE raw offsets.
    internal static class ChoiceTextCodePatcher
    {
        private const int Offset = 0x1370F8;
        private const int Length = 0xBC;
        private const int TextOffset = 0x110000;
        private const int TextLength = 5771316;
        private const string OriginalTextHash =
            "332173B6336D2FA7E138F77E5E6AE3B4F719804161CF776E69DF895F7D694065";

        // Replaces only buffer initialization and splitting in 0x821370C0.
        // The surrounding calls, stack frame, display call and return stay intact.
        // r28 = first row; r26 = row byte offset (0/16); r29 = bytes in this row.
        // r31 = source. Both rows retain their original 16-byte allocation.
        private static readonly uint[] Instructions =
        {
            0x3B9E2D64, // addi r28,r30,0x2d64
            0x7F9CDA14, // add r28,r28,r27 (choice index * 32)
            0x3B400000, // li r26,0
            0x935C0000, // clear the same 32 bytes as the two original memset calls
            0x935C0004,
            0x935C0008,
            0x935C000C,
            0x935C0010,
            0x935C0014,
            0x935C0018,
            0x935C001C,
            0x3BA00000, // li r29,0
            0x893F0000, // read: lbz r9,0(r31)
            0x2B090000, // cmplwi cr6,r9,0
            0x419A0084, // beq done (0x821371b4)
            0x2B09006E, // cmplwi cr6,r9,0x6e: real n control, at a character boundary
            0x419A005C, // beq marker
            0x39400001, // li r10,1
            0x692B0020, // xori r11,r9,0x20
            0x396BFF5F, // addi r11,r11,-0xa1
            0x2B0B003B, // unsigned <= 0x3b iff lead is 81-9f or e0-fc
            0x41990014, // bgt width (ASCII and half-width kana remain one byte)
            0x897F0001, // lbz r11,1(r31)
            0x2B0B0000, // stop before an incomplete two-byte character at NUL
            0x419A005C, // beq done
            0x39400002, // li r10,2
            0x7D7D5214, // width: add r11,r29,r10
            0x2F0B000C, // cmpwi cr6,r11,12 (six full-width cells)
            0x41990038, // bgt newline: retry the entire character on the next row
            0x7D1CD214, // add r8,r28,r26
            0x7D08EA14, // add r8,r8,r29
            0x893F0000, // copy: lbz r9,0(r31)
            0x99280000, // stb r9,0(r8)
            0x39080001, // addi r8,r8,1
            0x3BFF0001, // addi r31,r31,1
            0x3BBD0001, // addi r29,r29,1
            0x354AFFFF, // addic. r10,r10,-1
            0x4082FFE8, // bne copy: copy the entire character without checking its trail
            0x4BFFFF98, // b read
            0x3BFF0001, // marker: addi r31,r31,1
            0x2F1D0000, // cmpwi cr6,r29,0
            0x419AFF8C, // beq read: ignore leading/consecutive native n, as before
            0x3B5A0010, // newline: addi r26,r26,16
            0x3BA00000, // li r29,0
            0x2F1A0020, // cmpwi cr6,r26,32
            0x4198FF7C, // blt read (maximum two rows)
            0x60000000  // nop; fall through to the original display call
        };

        public static void Apply(string originalPath, string translatedPath,
            string outputPath, string reportPath)
        {
            byte[] original = File.ReadAllBytes(originalPath);
            byte[] translated = File.ReadAllBytes(translatedPath);
            if (Instructions.Length * 4 != Length ||
                original.Length < TextOffset + TextLength ||
                translated.Length < TextOffset + TextLength ||
                Hash(original, TextOffset, TextLength) != OriginalTextHash)
                throw new InvalidDataException("선택지 코드와 호환되는 드림클럽 본편 원본이 아닙니다.");
            // Never layer this change over an unknown executable-code patch.
            for (int index = TextOffset; index < TextOffset + TextLength; ++index)
                if (original[index] != translated[index])
                    throw new InvalidDataException("선택지 수정 전 실행 코드가 이미 변경되어 있습니다.");

            byte[] output = (byte[])translated.Clone();
            for (int index = 0; index < Instructions.Length; ++index)
            {
                uint word = Instructions[index];
                for (int part = 0; part < 4; ++part)
                    output[Offset + index * 4 + part] = (byte)(word >> (24 - part * 8));
            }
            int changed = 0;
            for (int index = 0; index < output.Length; ++index)
                if (output[index] != translated[index])
                {
                    if (index < Offset || index >= Offset + Length)
                        throw new InvalidDataException("선택지 수정 허용 범위 밖의 변경입니다.");
                    ++changed;
                }
            File.WriteAllBytes(outputPath, output);
            Dictionary<string, object> report = new Dictionary<string, object>
            {
                { "virtualAddress", "0x821370F8" },
                { "reservedBytes", Length },
                { "changedBytes", changed },
                { "changedOutsideAllowedRange", 0 },
                { "rowCapacityBytes", 12 },
                { "rowBufferBytes", 16 },
                { "rows", 2 },
                { "originalCodeSha256", Hash(original, Offset, Length) },
                { "patchedCodeSha256", Hash(output, Offset, Length) }
            };
            File.WriteAllText(reportPath, new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
        }

        public static void Verify(byte[] flat)
        {
            if (flat.Length < Offset + Length)
                throw new InvalidDataException("재구성된 실행 파일의 선택지 코드가 없습니다.");
            for (int index = 0; index < Instructions.Length; ++index)
                for (int part = 0; part < 4; ++part)
                    if (flat[Offset + index * 4 + part] != (byte)(Instructions[index] >> (24 - part * 8)))
                        throw new InvalidDataException("XEX 재구성 후 선택지 코드 검증에 실패했습니다.");
        }

        private static string Hash(byte[] data, int offset, int length)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(data, offset, length)).Replace("-", "");
        }
    }
}
