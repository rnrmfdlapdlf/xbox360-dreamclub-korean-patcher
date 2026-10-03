using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DreamClubKoreanPatcher
{
    internal static class KaraokeCheatPatcher
    {
        private const int Start = 0x11FFB4;
        private const int End = 0x120140;
        private const int Refusal = 0x1200C4;
        private const int Approval = 0x120048;
        private const string Signature =
            "229F263ECFE752CBA32F527D6FAC58B5163EE8DADAC20C8B5E4A4B2C2E0CBF99";

        // Request type 3 selects event 8 (approval, next state 0x1B) or
        // event 9 (refusal, next state 0x10). Redirect the refusal entry to
        // the complete approval path, including song selection and its state.
        // Other requests, the original checks and their side effects remain intact.
        public static void Apply(string referencePath, string originalPath,
            string translatedPath, string outputPath, string reportPath,
            UpdatedExecutableLayout layout)
        {
            byte[] reference = File.ReadAllBytes(referencePath);
            byte[] original = File.ReadAllBytes(originalPath);
            byte[] translated = File.ReadAllBytes(translatedPath);
            if (reference.Length < End)
                throw new InvalidDataException("가라오케 치트의 원본 코드를 확인할 수 없습니다.");
            using (SHA256 hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(reference, Start, End - Start)).Replace("-", "") != Signature)
                    throw new InvalidDataException("가라오케 치트와 호환되는 원본 코드가 아닙니다.");

            int block = layout == null ? Start : 0x120010;
            int start = layout == null ? block : layout.MapCodeBlock(block, End - block);
            int refusal = start + Refusal - block;
            int approval = start + Approval - block;
            if (start < 0 || start + End - block > original.Length ||
                start + End - block > translated.Length ||
                refusal - start != Refusal - block || approval - start != Approval - block)
                throw new InvalidDataException("가라오케 요청 함수의 구조가 다릅니다.");
            for (int i = 0; i < End - block; ++i)
                if (original[start + i] != translated[start + i] ||
                    (layout == null && original[start + i] != reference[block + i]))
                    throw new InvalidDataException("가라오케 요청 코드가 이미 변경되어 있습니다.");
            if (UpdatedExecutableLayout.Be32(translated, refusal) != 0x817F0000)
                throw new InvalidDataException("가라오케 거절 분기의 명령이 다릅니다.");
            uint branch = 0x48000000U | ((uint)(approval - refusal) & 0x03FFFFFCU);
            UpdatedExecutableLayout.Put32(translated, refusal, branch);
            File.WriteAllBytes(outputPath, translated);
            File.WriteAllText(reportPath, new JavaScriptSerializer().Serialize(new
            {
                enabled = true, option = "가라오케 항상 승인",
                patchOffset = refusal, approvalOffset = approval,
                originalWord = "817F0000", patchedWord = branch.ToString("X8"),
                modifiedInstructionCount = 1, gameRuntimeTested = false
            }), new UTF8Encoding(false));
        }
    }
}
