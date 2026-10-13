using System.Buffers.Binary;
using System.Text;

// Read-only x64 literal-call candidates. No executable patching or guessed FText
// layout. The bridge MUST query the existing namespace/key and compare its
// actual source before registering a translation.
internal static class NativeTextReader
{
 internal static IEnumerable<CatalogText> Scan(string file)
 {
  var info=new FileInfo(file);
  if(!info.Exists||info.Length is <4096 or >536870912)yield break;
  byte[] data=File.ReadAllBytes(file);
  foreach(var row in Scan(data))yield return row;
 }
 internal static IReadOnlyList<CatalogText> Scan(byte[] data)
 {
  var output=new List<CatalogText>();
  if(data.Length<4096||U16(0)!=0x5a4d)return output;
  int pe=I32(60);
  if(pe<64||pe>1048576||pe>data.Length-264||I32(pe)!=0x4550||U16(pe+4)!=0x8664||U16(pe+24)!=0x20b)return output;
  int count=U16(pe+6),optional=U16(pe+20),table=pe+24+optional;
  uint imageSize=U32(pe+80),headersSize=U32(pe+84);
  if(count is <1 or >96||optional is <112 or >4096||table<0||table>data.Length-40*count
   ||imageSize is <4096 or >2147483648||headersSize<table+40L*count||headersSize>data.Length)return output;
  var sections=new List<(int Raw,int Size,uint Rva,uint Virtual,uint Flags)>();
  for(int i=0;i<count;i++){
   int at=table+40*i;uint size=U32(at+16),raw=U32(at+20),rva=U32(at+12),flags=U32(at+36);
   uint mapped=Math.Max(U32(at+8),size);
   if(size>int.MaxValue||raw>int.MaxValue||(ulong)rva+mapped>imageSize
    ||size>0&&(raw<headersSize||(ulong)raw+size>(ulong)data.Length))return output;
   foreach(var previous in sections)
    if(size>0&&previous.Size>0&&(ulong)raw<(ulong)previous.Raw+(uint)previous.Size&&(ulong)previous.Raw<(ulong)raw+size
     ||mapped>0&&previous.Virtual>0&&(ulong)rva<(ulong)previous.Rva+previous.Virtual&&(ulong)previous.Rva<(ulong)rva+mapped)return output;
   sections.Add(((int)raw,(int)size,rva,mapped,flags));
  }
  var candidates=new List<(CatalogText Row,long Target)>();
  foreach(var s in sections.Where(s=>(s.Flags&0x60000020)==0x60000020&&(s.Flags&0x80000000)==0))
  for(int at=s.Raw,end=s.Raw+s.Size-40;at<=end&&candidates.Count<100000;at++){
   if(!Lea(at,0x4c,0x0d)||!Lea(at+7,0x4c,0x05)||!Lea(at+14,0x48,0x15))continue;
   int ret=at+21,len=ReturnStorage(ret);if(len==0||data[ret+len]!=0xe8)continue;
   long target=(long)s.Rva+(ret+len-s.Raw)+5+I32(ret+len+1);
   if(!sections.Any(x=>target>=x.Rva&&target<(long)x.Rva+x.Size&&(x.Flags&0x20000000)!=0))continue;
   string? key=Literal(at),ns=Literal(at+7),source=Literal(at+14);
   if(key is null||key.Length is <1 or >256||ns is null||ns.Length>512||source is null
    ||key.Any(char.IsControl)||ns.Any(char.IsControl)||!UnrealTextReader.Accept(source))continue;
   candidates.Add((new(ns,key,source,"en",new(),CatalogText.NativeLocalizedCandidate),target));
   at=ret+len+4;
   string? Literal(int op){
    long rva=(long)s.Rva+op-s.Raw+7+I32(op+3);
    var region=sections.FirstOrDefault(x=>rva>=x.Rva&&rva<(long)x.Rva+x.Size&&(x.Flags&0xe0000040)==0x40000040);
    if(region.Size==0)return null;
    int offset=checked(region.Raw+(int)(rva-region.Rva)),limit=Math.Min(region.Raw+region.Size,offset+12002);
    for(int n=offset;n+1<limit;n+=2)if(data[n]==0&&data[n+1]==0){
     try{return new UnicodeEncoding(false,false,true).GetString(data,offset,n-offset);}catch(DecoderFallbackException){return null;}
    }
    return null;
   }
  }
  // A lone arbitrary byte sequence does not establish even a reusable call
  // shape. Grouping is only an extraction filter; it never grants write access.
  var repeated=candidates.GroupBy(x=>x.Target).Where(g=>g.Count()>=2).Select(g=>g.Key).ToHashSet();
  foreach(var c in candidates)if(repeated.Contains(c.Target))output.Add(c.Row);
  return output;
  bool Lea(int at,byte rex,byte modrm)=>data[at]==rex&&data[at+1]==0x8d&&data[at+2]==modrm;
  int ReturnStorage(int at){
   if(data[at]!=0x48||data[at+1]!=0x8d)return 0;
   return data[at+2] switch{0x4d=>4,0x8d=>7,0x4c when data[at+3]==0x24=>5,0x8c when data[at+3]==0x24=>8,_=>0};
  }
  ushort U16(int p)=>BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p,2));
  uint U32(int p)=>BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p,4));
  int I32(int p)=>BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p,4));
 }
}
