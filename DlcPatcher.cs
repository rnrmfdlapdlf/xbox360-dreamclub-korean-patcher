using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using DreamClubDlcTools;

namespace DreamClubKoreanPatcher
{
    internal sealed class DlcBundle
    {
        public string format { get; set; } public int schema_version { get; set; } public string glyph_profile_id { get; set; }
        public DlcSourcePatch[] packages { get; set; } public DlcFilePatch[] file_patches { get; set; } public DlcGlyph[] glyphs { get; set; }
    }
    internal sealed class DlcGlyph { public string character { get; set; } public int lead { get; set; } public int trail { get; set; } }
    internal sealed class DlcSourcePatch
    {
        public string source_sha256,package_key; public long source_size { get; set; }
        public DlcOperation[] metadata { get; set; } public DlcSourceFile[] files { get; set; }
    }
    internal sealed class DlcSourceFile { public string path,source_sha256;public int size { get; set; } }
    internal sealed class DlcFilePatch { public string source_sha256;public DlcOperation[] operations { get; set; } }
    internal sealed class DlcOperation
    {
        public string id,source_sha256,translation,method;public int offset { get; set; } public int length { get; set; } public int lyric_index { get; set; }
    }
    internal sealed class DlcPatcher
    {
        private readonly DlcBundle bundle;
        private readonly Dictionary<char,ushort> glyphs;
        private readonly Action<string> log;
        private static readonly Encoding Sjis=Encoding.GetEncoding(932,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);
        public DlcPatcher(string applicationRoot,Action<string> log)
        {
            this.log=log;
            string path=Path.Combine(applicationRoot,"Runtime","Dlc","dlc_patch.json");
            bundle=new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue}.Deserialize<DlcBundle>(File.ReadAllText(path,Encoding.UTF8));
            if(bundle.format!="dreamclub-dlc-patch-v1" || bundle.schema_version!=1)throw new InvalidDataException("DLC 번역 데이터 버전이 다릅니다.");
            glyphs=bundle.glyphs.ToDictionary(g=>g.character.Single(),g=>(ushort)((g.lead<<8)|g.trail));
        }
        public static bool IsDlc(string path)
        {
            using(var stream=File.OpenRead(path))
            {
                if(stream.Length<0x364)return false;
                byte[] header=new byte[0x364];stream.Read(header,0,header.Length);
                string magic=Encoding.ASCII.GetString(header,0,4);
                return (magic=="LIVE" || magic=="PIRS" || magic=="CON ") && Be(header,0x344)==2;
            }
        }
        public static string OutputPath(string input)
        { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)),Path.GetFileNameWithoutExtension(input)+"_repacked"+Path.GetExtension(input)); }
        public string RunFolder(string input, Action<int> progress = null)
        {
            var source = new DirectoryInfo(Path.GetFullPath(input));
            if (!source.Exists || source.Parent == null)
                throw new IOException("DLC가 들어 있는 폴더를 선택해 주세요.");
            string output = source.FullName.TrimEnd(Path.DirectorySeparatorChar) + "_repacked";
            if (Directory.Exists(output) || File.Exists(output))
                throw new IOException("결과 폴더가 이미 있습니다: " + output);

            var files = new List<string>();
            var pending = new Stack<DirectoryInfo>();
            pending.Push(source);
            while (pending.Count > 0)
            {
                DirectoryInfo directory = pending.Pop();
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("연결된 폴더는 지원하지 않습니다: " + directory.FullName);
                foreach (FileInfo file in directory.GetFiles())
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("연결된 파일은 지원하지 않습니다: " + file.FullName);
                    if (IsDlc(file.FullName)) files.Add(file.FullName);
                }
                foreach (DirectoryInfo child in directory.GetDirectories()) pending.Push(child);
            }
            if (files.Count == 0)
                throw new InvalidDataException("폴더에 DLC 패키지 파일이 없습니다.설치 후 풀린 파일이 아닌 원본 DLC가 있는 폴더를 선택해 주세요.");

            files.Sort(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(output);
            var failures = new List<string>();
            string prefix = source.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            int completed = 0;
            foreach (string file in files)
            {
                string relative = file.Substring(prefix.Length);
                string destination = Path.Combine(output, relative);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    Run(file, destination);
                }
                catch (Exception error)
                {
                    string message = relative + " - " + error.Message;
                    failures.Add(message);
                    if (log != null) log("DLC 실패: " + message);
                }
                ++completed;
                if (progress != null) progress(completed * 100 / files.Count);
            }
                // Progress includes failed files because their processing has finished.
            if (failures.Count > 0)
                throw new IOException("DLC " + files.Count + "개 중 " + failures.Count +
                    "개 실패.성공한 파일은 " + output + "에 저장되었습니다.\n" + String.Join("\n", failures));
            return output;
        }
        internal static string Hash(byte[] b)
        {using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
        private static int Be(byte[] b,int o)
        {return checked((int)(((uint)b[o]<<24)|((uint)b[o+1]<<16)|((uint)b[o+2]<<8)|b[o+3]));}
        private static void Put(byte[] b,int o,int n)
        {b[o]=(byte)(n>>24);b[o+1]=(byte)(n>>16);b[o+2]=(byte)(n>>8);b[o+3]=(byte)n;}
        private byte[] Encode(string text)
        {
            if(String.IsNullOrWhiteSpace(text) || text.IndexOfAny(new[]{'\0','\r','\n','\t'})>=0 || text.Contains("\\n") || text.Contains("\\r"))
                throw new InvalidDataException("잘못된 DLC 번역 문자열입니다.");
            var bytes=new List<byte>();
            foreach(char c in text)
            {
                ushort code;
                if(glyphs.TryGetValue(c,out code)){bytes.Add((byte)(code>>8));bytes.Add((byte)code);}
                else bytes.AddRange(Sjis.GetBytes(c.ToString()));
            }
            return bytes.ToArray();
        }
        private static void VerifySpan(byte[] b,DlcOperation op)
        {
            if(op.offset<0 || op.length<=0 || op.offset>b.Length-op.length || Hash(b.Skip(op.offset).Take(op.length).ToArray())!=op.source_sha256)
                throw new InvalidDataException("DLC 원문 위치가 다릅니다: "+op.id);
        }
        private byte[] PatchFile(byte[] source,DlcOperation[] operations)
        {
            foreach(var op in operations)VerifySpan(source,op);
            if(operations.All(o=>o.method=="raw_line_splice.v1"))
            {
                using(var output=new MemoryStream())
                {
                    int position=0;
                    foreach(var op in operations.OrderBy(o=>o.offset))
                    {
                        if(op.offset<position)throw new InvalidDataException("DLC 번역 범위 중복");
                        output.Write(source,position,op.offset-position);byte[] text=Encode(op.translation);
                        output.Write(text,0,text.Length);position=op.offset+op.length;
                    }
                    output.Write(source,position,source.Length-position);return output.ToArray();
                }
            }
            if(!operations.All(o=>o.method=="song_rebuild.v1"))throw new InvalidDataException("지원하지 않는 DLC 기록 방식");
            if(Be(source,0)!=2 || Be(source,4)!=32 || Be(source,4)+Be(source,8)!=Be(source,12) || Be(source,12)+Be(source,16)!=source.Length)
                throw new InvalidDataException("가사 섹션 구조가 다릅니다.");
            int section=Be(source,12),count=Be(source,section+204),positionIn=checked(section+208+4*count);
            var byIndex=operations.ToDictionary(o=>o.lyric_index);
            if(byIndex.Keys.Any(i=>i<0 || i>=count))throw new InvalidDataException("가사 인덱스 오류");
            using(var output=new MemoryStream())
            {
                output.Write(source,0,positionIn);
                for(int i=0;i<count;++i)
                {
                    int end=Array.IndexOf(source,(byte)0,positionIn);
                    if(end<0)throw new InvalidDataException("가사 종료 문자 누락");
                    DlcOperation op;byte[] text;
                    if(byIndex.TryGetValue(i,out op))
                    {
                        if(op.offset!=positionIn || op.length!=end-positionIn)throw new InvalidDataException("가사 레코드 위치 오류");
                        text=Encode(op.translation);
                    }
                    else text=source.Skip(positionIn).Take(end-positionIn).ToArray();
                    if(text.Length>127)throw new InvalidDataException("가사 127바이트 제한 초과");
                    output.Write(text,0,text.Length);output.WriteByte(0);
                    while((output.Position&3)!=0)output.WriteByte(0);
                    int next=(end+4)&~3;
                    if(source.Skip(end).Take(next-end).Any(b=>b!=0))throw new InvalidDataException("가사 정렬 패딩 오류");
                    positionIn=next;
                }
                if(source.Skip(positionIn).Any(b=>b!=0))throw new InvalidDataException("가사 뒤 미확인 데이터");
                while((output.Position&15)!=0)output.WriteByte(0);
                byte[] result=output.ToArray();Put(result,16,result.Length-section);return result;
            }
        }
        public string Run(string sourcePath,string outputPath=null)
        {
            sourcePath=Path.GetFullPath(sourcePath);outputPath=outputPath??OutputPath(sourcePath);
            if(File.Exists(outputPath))throw new IOException("결과 파일이 이미 있습니다: "+outputPath);
            byte[] original=File.ReadAllBytes(sourcePath);string hash=Hash(original);
            var spec=bundle.packages.SingleOrDefault(p=>p.source_sha256==hash && p.source_size==original.LongLength);
            if(spec==null)throw new InvalidDataException("지원 원본 DLC와 해시가 다릅니다.다른 버전 또는 이미 패치한 파일인지 확인해 주세요.");
            var package=StfsPackage.Read(sourcePath);
            if(package.Files.Count!=spec.files.Length)throw new InvalidDataException("DLC 파일 목록 불일치");
            var replacements=new Dictionary<string,byte[]>();
            foreach(var file in spec.files)
            {
                byte[] data;
                if(!package.Files.TryGetValue(file.path,out data) || data.Length!=file.size || Hash(data)!=file.source_sha256)
                    throw new InvalidDataException("DLC 내부 원본 불일치: "+file.path);
                var patch=bundle.file_patches.SingleOrDefault(p=>p.source_sha256==file.source_sha256);
                if(patch!=null)replacements.Add(file.path,PatchFile(data,patch.operations));
            }
            var metadata=new Dictionary<int,byte[]>();
            foreach(var op in spec.metadata)
            {
                // The game matches this title to info.txt labels, so it is an ID.
                if (op.offset < 0xD11) continue;
                VerifySpan(original,op);
                if(op.method!="fixed_utf16_slot.v1")throw new InvalidDataException("메타데이터 기록 방식 오류");
                metadata.Add(op.offset,Encoding.BigEndianUnicode.GetBytes(op.translation));
            }
            string temporary=outputPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                byte[] rebuilt=package.Rebuild(replacements,metadata);File.WriteAllBytes(temporary,rebuilt);
                var verified=StfsPackage.Read(temporary);
                if(verified.Files.Count!=package.Files.Count)throw new InvalidDataException("재패킹 파일 수 불일치");
                foreach(var file in package.Files)
                {
                    byte[] expected=replacements.ContainsKey(file.Key)?replacements[file.Key]:file.Value;
                    if(!verified.Files[file.Key].SequenceEqual(expected))throw new InvalidDataException("재추출 검증 실패: "+file.Key);
                }
                foreach(var item in metadata)
                    if(!rebuilt.Skip(item.Key).Take(item.Value.Length).SequenceEqual(item.Value) || rebuilt[item.Key+item.Value.Length]!=0 || rebuilt[item.Key+item.Value.Length+1]!=0)
                        throw new InvalidDataException("패키지 설명 검증 실패");
                File.Move(temporary,outputPath);
                if(log!=null)log("DLC 완료: "+outputPath);
                return outputPath;
            }
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        public static void ApplySharedGlyphProfile(string applicationRoot,string globalMap)
        {
            var json=new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue};
            string frozen=Path.Combine(applicationRoot,"Runtime","Dlc","glyph_map.json");
            var expected=json.Deserialize<GlyphDocument>(File.ReadAllText(frozen,Encoding.UTF8));
            var current=json.Deserialize<GlyphDocument>(File.ReadAllText(globalMap,Encoding.UTF8));
            var codes=expected.mappings.ToDictionary(g=>g.character,g=>(g.lead<<8)|g.trail);
            foreach(var g in current.mappings)
                if(!codes.ContainsKey(g.character) || codes[g.character]!=((g.lead<<8)|g.trail))
                    throw new InvalidDataException("본편 글리프 코드가 DLC 공통 프로필과 다릅니다.번역 배포 데이터를 함께 갱신해야 합니다.");
            File.Copy(frozen,globalMap,true);
        }
        private sealed class GlyphDocument { public DlcGlyph[] mappings { get; set; } }
    }
}
