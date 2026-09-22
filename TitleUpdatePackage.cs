using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DreamClubKoreanPatcher
{
    // STFS layout reference: Xenia's stfs_xbox.h and stfs_container_device.cc.
    // Read both contiguous and chained files; never extract untrusted paths directly.
    internal sealed class TitleUpdatePackage
    {
        public uint MediaId, TitleId, Version;
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private byte[] data;
        private int header, tableCopies, totalBlocks, rootLevel, rootCopy;
        private readonly Dictionary<int, byte[]> hashTables = new Dictionary<int, byte[]>();

        public static bool IsPackage(string path)
        {
            if (!File.Exists(path)) return false;
            using (var stream=File.OpenRead(path))
            {
                byte[] bytes=new byte[4];if(stream.Read(bytes,0,4)!=4)return false;
                string magic=Encoding.ASCII.GetString(bytes);
                return magic=="LIVE" || magic=="PIRS" || magic=="CON " ||
                    (magic=="XEX2" && String.Equals(Path.GetExtension(path),".xexp",StringComparison.OrdinalIgnoreCase));
            }
        }

        public static TitleUpdatePackage Read(string path)
        {
            var p=new TitleUpdatePackage();p.data=File.ReadAllBytes(path);
            if(p.data.Length<24)throw Bad("TU 헤더가 잘렸습니다.");
            string magic=Encoding.ASCII.GetString(p.data,0,4);
            if(magic=="XEX2")
            {
                if((Be(p.data,4)&0x40)==0)throw Bad("XEX 업데이트 패치가 아닙니다.");
                p.Files.Add("default.xexp",p.data);return p;
            }
            if((magic!="LIVE" && magic!="PIRS" && magic!="CON ") || p.data.Length<0x400)throw Bad("STFS 형식이 아닙니다.");
            uint headerSize=Be(p.data,0x340);
            if(headerSize<0x400 || headerSize>p.data.Length)throw Bad("헤더 크기가 올바르지 않습니다.");
            p.header=checked((int)((headerSize+4095)&~4095U));
            if(Be(p.data,0x344)!=0xB0000 || Be(p.data,0x3A9)!=0 || p.data[0x379]!=0x24)
                throw Bad("타이틀 업데이트 STFS 파일이 아닙니다.");
            p.MediaId=Be(p.data,0x354);p.Version=Be(p.data,0x358);p.TitleId=Be(p.data,0x360);
            if(p.TitleId!=0x445007F0)throw Bad("XBOX 360 드림클럽 본편용 TU가 아닙니다.");
            if(p.header>p.data.Length)throw Bad("헤더 정렬 영역이 잘렸습니다.");
            p.CheckHash(p.data,0x344,p.header-0x344,p.data,0x32C);
            p.tableCopies=(p.data[0x37B]&1)!=0?1:2;p.rootCopy=p.tableCopies==1?0:((p.data[0x37B]>>1)&1);
            p.totalBlocks=checked((int)Be(p.data,0x395));
            if(p.totalBlocks<=0 || p.totalBlocks>0x4AF768)throw Bad("블록 수가 올바르지 않습니다.");
            p.rootLevel=p.totalBlocks>28900?2:p.totalBlocks>170?1:0;
            int count=p.data[0x37C]|(p.data[0x37D]<<8),block=Le24(p.data,0x37E);
            if(count<=0 || count>p.totalBlocks)throw Bad("파일 테이블 크기가 올바르지 않습니다.");
            var used=new HashSet<int>();var names=new List<string>();var directories=new HashSet<int>();
            for(int t=0;t<count;++t)
            {
                if(!used.Add(block))throw Bad("파일 테이블 블록이 반복됩니다.");
                byte[] table=p.DataBlock(block);
                for(int i=0;i<64;++i)
                {
                    int o=i*64,n=table[o+40]&63;
                    if(n==0) { names.Add(null);continue; }
                    if(n>40)throw Bad("파일 이름 길이가 올바르지 않습니다.");
                    string name=Encoding.ASCII.GetString(table,o,n);
                    if(name=="." || name==".." || name.EndsWith(".") || name.EndsWith(" ") ||
                        name.IndexOfAny(Path.GetInvalidFileNameChars())>=0 || name.Any(c=>c<32 || c>=127))
                        throw Bad("허용되지 않는 파일 이름입니다.");
                    string stem=Path.GetFileNameWithoutExtension(name).ToUpperInvariant();
                    if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem))throw Bad("예약된 파일 이름입니다.");
                    int parent=(table[o+50]<<8)|table[o+51];
                    if(parent!=65535)
                    {
                        if(parent>=names.Count || !directories.Contains(parent))throw Bad("상위 폴더 참조가 올바르지 않습니다.");
                        name=names[parent]+"/"+name;
                    }
                    int id=names.Count;names.Add(name);
                    if((table[o+40]&128)!=0) { directories.Add(id);continue; }
                    int length=checked((int)Be(table,o+52)),start=Le24(table,o+47),allocated=Le24(table,o+44);
                    if((length+4095L)/4096>allocated || allocated>p.totalBlocks || length>p.data.Length)throw Bad("파일 크기가 올바르지 않습니다.");
                    byte[] content=new byte[length];int pos=0;
                    while(pos<length)
                    {
                        if(!used.Add(start))throw Bad("TU 파일 블록이 중복되거나 순환합니다.");
                        byte[] bytes=p.DataBlock(start);int take=Math.Min(4096,length-pos);
                        Buffer.BlockCopy(bytes,0,content,pos,take);pos+=take;
                        start=p.NextBlock(start);
                    }
                    if(length!=0 && start!=0xFFFFFF)throw Bad("파일 블록 체인 길이가 다릅니다.");
                    if(p.Files.ContainsKey(name))throw Bad("중복된 파일 경로입니다.");
                    p.Files.Add(name,content);
                }
                block=p.NextBlock(block);
            }
            if(block!=0xFFFFFF)throw Bad("파일 테이블 체인 길이가 다릅니다.");
            if(!p.Files.ContainsKey("default.xexp"))throw Bad("default.xexp가 없습니다.");
            return p;
        }

        private byte[] DataBlock(int block)
        {
            if(block<0 || block>=totalBlocks)throw Bad("블록 위치가 범위를 벗어났습니다.");
            long physical=block,span=170;
            for(int level=0;level<3;++level)
            {
                physical+=((block+span)/span)*tableCopies;
                if(block<span)break;span*=170;
            }
            byte[] result=ReadBlock(physical);
            byte[] hash=HashTable(block,0);
            CheckHash(result,0,4096,hash,(block%170)*24);
            if((hash[(block%170)*24+20]&0x80)==0)throw Bad("사용되지 않는 블록입니다.");
            return result;
        }
        private int NextBlock(int block)
        {
            byte[] hash=HashTable(block,0);int o=(block%170)*24+21;
            return (hash[o]<<16)|(hash[o+1]<<8)|hash[o+2];
        }
        private byte[] HashTable(int block,int level)
        {
            int step0=170+tableCopies,step1=28900+171*tableCopies;
            int physical;
            if(level==2)physical=step1;
            else if(level==1)physical=block<28900?step0:(block/28900)*step1+tableCopies;
            else if(block<170)physical=0;
            else physical=(block/170)*step0+((block/28900)+1)*tableCopies+(block>=28900?tableCopies:0);
            byte[] expected;int hashOffset,copy;
            if(level==rootLevel) { expected=data;hashOffset=0x381;copy=rootCopy; }
            else
            {
                expected=HashTable(block,level+1);
                int divisor=level==0?170:28900;hashOffset=((block/divisor)%170)*24;
                copy=tableCopies==1?0:((expected[hashOffset+20]>>6)&1);
            }
            physical+=copy;byte[] table;
            if(!hashTables.TryGetValue(physical,out table))
            {
                table=ReadBlock(physical);CheckHash(table,0,4096,expected,hashOffset);hashTables.Add(physical,table);
            }
            return table;
        }
        private byte[] ReadBlock(long physical)
        {
            long start=header+physical*4096;
            if(start<0 || start+4096>data.Length)throw Bad("블록 데이터가 잘렸습니다.");
            byte[] result=new byte[4096];Buffer.BlockCopy(data,(int)start,result,0,4096);return result;
        }
        private void CheckHash(byte[] bytes,int start,int length,byte[] expected,int offset)
        {
            using(var hash=SHA1.Create())
                if(!hash.ComputeHash(bytes,start,length).SequenceEqual(expected.Skip(offset).Take(20)))throw Bad("TU 무결성 검증에 실패했습니다.");
        }
        internal static uint[] ExecutionInfo(string path)
        {
            byte[] bytes=File.ReadAllBytes(path);
            if(bytes.Length<24 || Encoding.ASCII.GetString(bytes,0,4)!="XEX2")throw Bad("XEX 헤더가 올바르지 않습니다.");
            uint count=Be(bytes,20);
            if(count>(bytes.Length-24)/8)throw Bad("XEX 헤더 목록이 잘렸습니다.");
            for(int i=0;i<count;++i)
                if(Be(bytes,24+i*8)==0x40006)
                {
                    uint o=Be(bytes,28+i*8);if(o>bytes.Length-24)throw Bad("실행 정보가 잘렸습니다.");
                    return new[]{Be(bytes,(int)o),Be(bytes,(int)o+4),Be(bytes,(int)o+8),Be(bytes,(int)o+12)};
                }
            throw Bad("게임 실행 정보가 없습니다.");
        }
        private static uint Be(byte[] b,int o) { return UpdatedExecutableLayout.Be32(b,o); }
        private static int Le24(byte[] b,int o) { return b[o]|(b[o+1]<<8)|(b[o+2]<<16); }
        private static InvalidDataException Bad(string message) { return new InvalidDataException("TU 파일: "+message); }
    }
}
