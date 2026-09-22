using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace DreamClubKoreanPatcher
{
    // Derive locations from the user's original executable and the applied TU.
    // No TU version list: ambiguous or changed structures fail before ISO output.
    internal sealed class UpdatedExecutableLayout
    {
        private readonly byte[] reference, updated;
        private readonly List<Section> referenceSections, updatedSections;
        private readonly List<Function> referenceFunctions, updatedFunctions;
        private readonly Dictionary<int, int> functions = new Dictionary<int, int>();
        private readonly Dictionary<int, uint> expectedCode = new Dictionary<int, uint>();
        private readonly int cave, textHeader, textSize, suffixIndex, suffixTable;
        private const int OldCave = 0x691040;
        private const int HelperLength = 380;

        internal sealed class Section
        {
            public string Name;
            public int Header, Start, Size, RawSize, RawOffset;
        }
        private sealed class Function { public int Start, End; }

        public UpdatedExecutableLayout(string referencePath, string updatedPath)
        {
            reference = File.ReadAllBytes(referencePath);
            updated = File.ReadAllBytes(updatedPath);
            referenceSections = Sections(reference);
            updatedSections = Sections(updated);
            referenceFunctions = Functions(reference, referenceSections);
            updatedFunctions = Functions(updated, updatedSections);
            Section text = updatedSections.Single(s => s.Name == ".text");
            cave = (text.Start + text.Size + 15) & ~15;
            textHeader = text.Header + 8;
            textSize = text.RawSize;
            if (cave + HelperLength > text.Start + text.RawSize ||
                updated.Skip(cave).Take(HelperLength).Any(b => b != 0))
                throw Error("이름 치환 코드용 정렬 공간이 부족합니다.");

            suffixTable = MapPointerTable(0x85B8A0, 12);
            int suffixBlock = MapPointerTable(0x85B8A0 + 0x2FC * 4, 30);
            if (suffixBlock < suffixTable || ((suffixBlock - suffixTable) & 3) != 0)
                throw Error("이름 경칭 테이블 구조가 다릅니다.");
            suffixIndex = (suffixBlock - suffixTable) / 4;
            // Resolve whole functions, not short byte sequences at guessed offsets.
            foreach (int address in new[] { 0x1370F8, 0x1AE500, 0x1BC100, 0x1BBFC4 })
                MapCode(address);
        }

        public int MapData(int offset, int length)
        {
            Section section = referenceSections.FirstOrDefault(s =>
                offset >= s.Start && offset + length <= s.Start + Math.Max(s.Size, s.RawSize));
            if (section == null || section.Name == ".text") throw Error("번역 데이터 영역이 다릅니다.");
            Section target = updatedSections.Single(s => s.Name == section.Name);
            byte[] needle = Slice(reference, offset, length);
            List<int> matches = Find(updated, needle, target.Start, target.Start + Math.Max(target.Size, target.RawSize));
            if (matches.Count == 1) return matches[0];
            // Repeated short labels need unchanged surrounding bytes to disambiguate.
            int best = -1, score = -1; bool tied = false;
            foreach (int match in matches)
            {
                int left = 0, right = 0;
                while (left < 128 && offset-left-1 >= section.Start && match-left-1 >= target.Start &&
                    reference[offset-left-1] == updated[match-left-1]) ++left;
                while (right < 128 && offset+length+right < reference.Length && match+length+right < updated.Length &&
                    reference[offset+length+right] == updated[match+length+right]) ++right;
                int value = left + right;
                if (value > score) { best = match; score = value; tied = false; }
                else if (value == score) tied = true;
            }
            if (best < 0 || tied || score < 16)
                throw Error("번역 위치를 유일하게 확인하지 못했습니다: 0x" + offset.ToString("X"));
            return best;
        }

        private int MapPointerTable(int offset, int count)
        {
            byte[] pattern = new byte[count * 4];
            for (int i = 0; i < count; ++i)
            {
                int address = checked((int)(Be32(reference, offset + i*4) - 0x82000000));
                int end = address;
                while (end < reference.Length && reference[end] != 0) ++end;
                Put32(pattern, i*4, checked(0x82000000U + (uint)MapData(address, end-address+1)));
            }
            Section data = updatedSections.Single(s => s.Name == ".data");
            List<int> matches = Find(updated, pattern, data.Start, data.Start + data.RawSize);
            if (matches.Count != 1) throw Error("문자열 포인터 테이블이 변경되었거나 모호합니다.");
            return matches[0];
        }

        public void RemapManifest(string path, JavaScriptSerializer json)
        {
            var manifest = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            foreach (Dictionary<string, object> entry in (System.Collections.IEnumerable)manifest["entries"])
            {
                // Untranslated entries cannot write anything; preserve their IDs.
                if (Convert.ToString(entry["status"]) != "translated") continue;
                foreach (Dictionary<string, object> occurrence in (System.Collections.IEnumerable)entry["occurrences"])
                {
                    int old = Convert.ToInt32(occurrence["fileOffset"]);
                    int mapped = MapData(old, Convert.ToInt32(occurrence["byteLimit"]) + 1);
                    occurrence["fileOffset"] = mapped;
                    Section section = updatedSections.First(s => mapped >= s.Start && mapped < s.Start + s.RawSize);
                    // The existing relocator uses this manifest's PE-coordinate convention.
                    occurrence["virtualAddress"] = section.Start + mapped - section.RawOffset;
                }
            }
            File.WriteAllText(path, json.Serialize(manifest), new UTF8Encoding(false));
        }

        public int MapCode(int address)
        {
            if (address >= OldCave && address < OldCave + HelperLength) return cave + address - OldCave;
            Function function = referenceFunctions.FirstOrDefault(f => f.Start <= address && address < f.End);
            if (function == null) throw Error("참조 함수가 없습니다: " + address.ToString("X"));
            int mapped;
            if (!functions.TryGetValue(function.Start, out mapped))
            {
                uint[] signature = Normalize(reference, function, false);
                List<Function> candidates = updatedFunctions.Where(f => f.End-f.Start == function.End-function.Start &&
                    Normalize(updated, f, true).SequenceEqual(signature)).ToList();
                if (candidates.Count != 1) throw Error("필수 함수가 변경되었거나 모호합니다: 0x" + function.Start.ToString("X"));
                mapped = candidates[0].Start;
                functions.Add(function.Start, mapped);
            }
            return mapped + address - function.Start;
        }

        private uint[] Normalize(byte[] data, Function function, bool isUpdated)
        {
            int count = (function.End-function.Start)/4;
            uint[] words = new uint[count], result = new uint[count];
            for (int i=0;i<count;++i) words[i] = result[i] = Be32(data,function.Start+i*4);
            for (int i=0;i<count;++i)
            {
                uint word=words[i], op=word>>26;
                if (op==18 && (word&2)==0) result[i] &= 0xFC000003;
                // Only relocation immediates following lis of an image address.
                if (op==15 && ((word>>16)&31)==0 && (word&65535)>=0x8200 && (word&65535)<=0x82FF)
                {
                    result[i] &= 0xFFFF0000; uint reg=(word>>21)&31;
                    for (int j=i+1;j<Math.Min(i+9,count);++j)
                    {
                        uint next=words[j], code=next>>26;
                        if (((next>>16)&31)==reg && (code==14 || (code>=32 && code<=55))) result[j]&=0xFFFF0000;
                    }
                }
                // Name suffix IDs move when TU inserts entries. The complete 30-pointer
                // table was verified above, so only those three index bases may change.
                uint prefix=word&0xFFFF0000;
                if (prefix==0x38720000 || prefix==0x387E0000)
                    for (int group=0;group<3;++group)
                        if ((word&65535)==(uint)((isUpdated?suffixIndex:0x2FC)+group*10))
                            result[i]=prefix+(uint)(0x2FC+group*10);
            }
            return result;
        }

        public void ApplyCode(string referencePath, string translatedPath, string outputPath, string workRoot)
        {
            string choice=Path.Combine(workRoot,"reference_choice.exe"), name=Path.Combine(workRoot,"reference_name.exe");
            ChoiceTextCodePatcher.Apply(referencePath,referencePath,choice,Path.Combine(workRoot,"reference_choice.json"));
            NameTokenCodePatcher.Apply(referencePath,choice,name,Path.Combine(workRoot,"reference_name.json"));
            byte[] source=File.ReadAllBytes(name), translated=File.ReadAllBytes(translatedPath);
            var helperAddresses = new Dictionary<int,uint>();
            AddHelperAddress(helperAddresses, source, 0x691124, MapData(0xE7950,13));
            AddHelperAddress(helperAddresses, source, 0x691144, MapData(0xE7930,17)+12);
            Section oldText=referenceSections.Single(s=>s.Name==".text");
            for(int offset=oldText.Start;offset<oldText.Start+oldText.RawSize;offset+=4)
            {
                uint word=Be32(source,offset);
                if(word==Be32(reference,offset)) continue;
                if(helperAddresses.ContainsKey(offset)) word=helperAddresses[offset];
                int target=MapCode(offset);
                if(Be32(translated,target)!=Be32(updated,target)) throw Error("필수 코드가 번역 과정에서 변경되었습니다.");
                if((word>>26)==18 && (word&2)==0)
                {
                    int delta=(int)(word&0x03FFFFFC); if((delta&0x02000000)!=0) delta|=unchecked((int)0xFC000000);
                    int destination=MapCode(offset+delta), distance=destination-target;
                    if(distance < -0x2000000 || distance >= 0x2000000) throw Error("분기 범위를 초과했습니다.");
                    word=(word&0xFC000003)|((uint)distance&0x03FFFFFC);
                }
                expectedCode.Add(target,word); Put32(translated,target,word);
            }
            Buffer.BlockCopy(BitConverter.GetBytes(textSize),0,translated,textHeader,4);
            Verify(translated);
            File.WriteAllBytes(outputPath,translated);
            var report=new Dictionary<string,object> {
                {"functions",functions.ToDictionary(p=>"0x"+p.Key.ToString("X"),p=>"0x"+p.Value.ToString("X"))},
                {"helperOffset",cave},{"suffixTable",suffixTable},{"suffixIndex",suffixIndex},
                {"changedWords",expectedCode.ToDictionary(p=>"0x"+p.Key.ToString("X"),p=>p.Value.ToString("X8"))},
                {"versionNumberWhitelist",false}};
            File.WriteAllText(Path.Combine(workRoot,"updated_code_report.json"),new JavaScriptSerializer().Serialize(report),new UTF8Encoding(false));
        }

        public void Verify(byte[] flat)
        {
            foreach(var pair in expectedCode)
                if(Be32(flat,pair.Key)!=pair.Value) throw Error("재구성 후 선택지·이름 코드가 다릅니다.");
            if(BitConverter.ToInt32(flat,textHeader)!=textSize) throw Error("실행 섹션 크기가 다릅니다.");
            foreach(int offset in new[]{0xE7930,0xE7950})
            {
                int length=offset==0xE7930?17:13;
                int mapped=MapData(offset,length);
                if(!Slice(reference,offset,length).SequenceEqual(Slice(flat,mapped,length))) throw Error("이름 토큰 상수가 다릅니다.");
            }
            for(int i=0;i<30;++i)
            {
                int pointer=checked((int)(Be32(flat,suffixTable+(suffixIndex+i)*4)-0x82000000));
                if(pointer<0 || pointer+6>=flat.Length || Array.IndexOf(flat,(byte)0,pointer,7)<0)
                    throw Error("메일 경칭이 검증된 버퍼 크기를 초과합니다.");
            }
        }

        private static void AddHelperAddress(Dictionary<int,uint> words,byte[] source,int offset,int rva)
        {
            uint address=checked(0x82000000U+(uint)rva);
            words[offset]=(Be32(source,offset)&0xFFFF0000)|((address+0x8000)>>16);
            words[offset+4]=(Be32(source,offset+4)&0xFFFF0000)|(address&65535);
        }

        internal static List<Section> Sections(byte[] data)
        {
            int pe=BitConverter.ToInt32(data,60), table=pe+24+BitConverter.ToUInt16(data,pe+20);
            var result=new List<Section>();
            for(int i=0;i<BitConverter.ToUInt16(data,pe+6);++i)
            {
                int o=table+i*40;
                result.Add(new Section {Name=Encoding.ASCII.GetString(data,o,8).TrimEnd('\0'),Header=o,
                    Size=BitConverter.ToInt32(data,o+8),Start=BitConverter.ToInt32(data,o+12),
                    RawSize=BitConverter.ToInt32(data,o+16),RawOffset=BitConverter.ToInt32(data,o+20)});
            }
            return result;
        }
        private static List<Function> Functions(byte[] data,List<Section> sections)
        {
            Section pdata=sections.Single(s=>s.Name==".pdata"),text=sections.Single(s=>s.Name==".text");
            var starts=new SortedSet<int>();
            for(int o=pdata.Start;o<pdata.Start+pdata.Size;o+=8)
            {
                int start=unchecked((int)(Be32(data,o)-0x82000000));
                if(start>=text.Start && start<text.Start+text.Size) starts.Add(start);
            }
            int[] values=starts.Concat(new[]{text.Start+text.Size}).ToArray();
            var result=new List<Function>();
            for(int i=0;i<values.Length-1;++i) result.Add(new Function {Start=values[i],End=values[i+1]});
            return result;
        }
        private static List<int> Find(byte[] data,byte[] needle,int start,int end)
        {
            var found=new List<int>();end=Math.Min(end,data.Length);
            int[] skip=Enumerable.Repeat(needle.Length,256).ToArray();
            for(int i=0;i<needle.Length-1;++i) skip[needle[i]]=needle.Length-1-i;
            for(int i=start;i+needle.Length<=end;)
            {
                // Mail slots contain long zero-filled tails. Check their first
                // byte before the backwards scan to avoid quadratic zero scans.
                if(data[i]!=needle[0]) { ++i; continue; }
                int j=needle.Length-1;while(j>=0 && data[i+j]==needle[j]) --j;
                if(j<0) { found.Add(i);++i; } else i+=skip[data[i+needle.Length-1]];
            }
            return found;
        }
        private static byte[] Slice(byte[] data,int offset,int length) { var b=new byte[length];Buffer.BlockCopy(data,offset,b,0,length);return b; }
        internal static uint Be32(byte[] b,int o) { return ((uint)b[o]<<24)|((uint)b[o+1]<<16)|((uint)b[o+2]<<8)|b[o+3]; }
        internal static void Put32(byte[] b,int o,uint v) { for(int i=0;i<4;++i)b[o+i]=(byte)(v>>(24-i*8)); }
        private static InvalidDataException Error(string message) { return new InvalidDataException("이 TU는 현재 한글패치와 호환되지 않습니다. "+message); }
    }
}
