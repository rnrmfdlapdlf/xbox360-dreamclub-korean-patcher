using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace DreamClubKoreanPatcher
{
    internal static class KaraokeScorePatcher
    {
        private const int Table = 0x76C5A8;
        private static readonly uint[] Expected = {
            0xBF800000, 0x3F800000, 0x3F800000, 0x3F99999A,
            0x3FB33333, 0x3FCCCCCD, 0x3FE66666, 0x40000000,
            8, 0x8276C5A8
        };

        // r14 points to the karaoke score table descriptor at 0x8276C5C8.
        // Bad input (0x8218AE5C and the other two lanes) uses index zero
        // after the combo resets. Missed notes (0x8218B9C0) use index zero too.
        // Replace only -1.0 with 0.0; positive awards and judgment stay intact.
        public static void Apply(string referencePath, string originalPath,
            string translatedPath, string outputPath, string reportPath,
            UpdatedExecutableLayout layout)
        {
            byte[] reference = File.ReadAllBytes(referencePath);
            byte[] original = File.ReadAllBytes(originalPath);
            byte[] translated = File.ReadAllBytes(translatedPath);
            if (reference.Length < Table + Expected.Length * 4)
                throw Invalid();
            for (int i = 0; i < Expected.Length; ++i)
                if (UpdatedExecutableLayout.Be32(reference, Table + i * 4) != Expected[i])
                    throw Invalid();
            int table = layout == null ? Table : layout.MapData(Table, Expected.Length * 4);
            if (layout != null)
            {
                // Both score consumers must retain the verified control flow.
                layout.MapCodeBlock(0x18AE2C, 0x1A0);
                layout.MapCodeBlock(0x18B950, 0xB0);
            }
            int length = Expected.Length * 4;
            if (table < 0 || table + length > original.Length || table + length > translated.Length ||
                !reference.Skip(Table).Take(length).SequenceEqual(original.Skip(table).Take(length)) ||
                !original.Skip(table).Take(length).SequenceEqual(translated.Skip(table).Take(length)) ||
                UpdatedExecutableLayout.Be32(original, table + 36) != 0x82000000U + (uint)table)
                throw Invalid();
            UpdatedExecutableLayout.Put32(translated, table, 0);
            File.WriteAllBytes(outputPath, translated);
            File.WriteAllText(reportPath, new JavaScriptSerializer().Serialize(new {
                enabled = true, option = "가라오케 일치율 감소 방지",
                patchOffset = table, originalPenalty = -1, patchedPenalty = 0,
                positiveAwardsUnchanged = true, timingUnchanged = true,
                gameRuntimeTested = false
            }), new UTF8Encoding(false));
        }
        private static Exception Invalid()
        {
            return new InvalidDataException("가라오케 일치율 점수표가 예상한 원본과 다릅니다.");
        }
    }
}
