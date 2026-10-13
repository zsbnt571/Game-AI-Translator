using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

internal static class UnrealTextReader
{
 private static readonly Encoding Utf8=new UTF8Encoding(false,true),Utf16=new UnicodeEncoding(false,false,true);
 internal static bool Accept(string text)=>text.Length is >=2 and <=6000&&text.Any(char.IsLetter)
  &&!text.Any(c=>char.IsControl(c)&&c is not '\n' and not '\r' and not '\t')
  &&!Regex.IsMatch(text,@"\|\s*(?:plural|ordinal|gender|select)\s*\(",RegexOptions.IgnoreCase);
 internal static IEnumerable<CatalogText> Scan(byte[] data) {
  for(int i=0;i<data.Length-30;i++) {
   // Kismet EX_TextConst / LocalizedText stores three string expressions in
   // source, key, namespace order, not the serialized FText/FString layout.
   // Accept only literal UTF-8/UTF-16 expressions with the same bounded GUID
   // identity checks as Base histories. Never treat standalone FString,
   // invariant/literal text or StringTable references as localizable identities.
   if(data[i]==0x29&&data[i+1]==0x01) {
    int scriptAt=i+2;var scriptSource=ReadScriptString(data,ref scriptAt,6000);
    if(scriptSource is not null&&Accept(scriptSource)) {
     var scriptKey=ReadScriptString(data,ref scriptAt,256);
     if(scriptKey is not null&&IsGuidKey(scriptKey)) {
      var scriptNamespace=ReadScriptString(data,ref scriptAt,512);
      if(scriptNamespace is not null&&!scriptNamespace.Any(char.IsControl)) {
       // This byte scan does not establish that the surrounding bytes belong
       // to a UFunction script. Runtime must confirm the existing localization
       // identity and exact source before allowing any registration.
       yield return new(scriptNamespace,scriptKey,scriptSource,"en",new(),CatalogText.BlueprintLocalizedCandidate);i=scriptAt-1;continue;
      }
     }
    }
   }
   // Serialized FText: flags, Base history, namespace, GUID key, native string.
   // This conservative reader deliberately excludes arbitrary adjacent strings,
   // invariant text and format histories. Locres provides non-GUID identities.
   if(data[i]>31||data[i+1]!=0||data[i+2]!=0||data[i+3]!=0||data[i+4]!=0)continue;
   int p=i+5;var ns=ReadString(data,ref p,512);if(ns is null||ns.Any(char.IsControl))continue;
   var key=ReadString(data,ref p,256);if(key is null||!IsGuidKey(key))continue;
   var source=ReadString(data,ref p,6000);if(source is null||!Accept(source))continue;
   yield return new(ns,key,source,"en",new());i=p-1;
  }
 }
 private static bool IsGuidKey(string value)=>value.Length==32&&value.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
 private static string? ReadScriptString(byte[] data,ref int at,int limit) {
  if(at<0||at>=data.Length)return null;
  byte token=data[at++];int width=token==0x1f?1:token==0x34?2:0;
  if(width==0)return null;
  int start=at,maxBytes=width==1?limit*4:limit*2;
  while(at<=data.Length-width&&at-start<=maxBytes) {
   if(data[at]==0&&(width==1||data[at+1]==0)) {
    try {string value=(width==1?Utf8:Utf16).GetString(data,start,at-start);at+=width;return value.Length<=limit?value:null;}
    catch(DecoderFallbackException){return null;}
   }
   at+=width;
  }
  return null;
 }
 internal static string? ReadString(byte[] data,ref int at,int limit) {
  if(at<0||at>data.Length-4)return null;
  int count=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));at+=4;if(count==0)return "";
  if(count< -limit-1||count>limit*4+1)return null;
  int length=Math.Abs(count)*(count<0?2:1);if(length>data.Length-at)return null;
  try {string s=(count<0?Utf16:Utf8).GetString(data,at,length);at+=length;
   return s.EndsWith('\0')&&!s[..^1].Contains('\0')&&s.Length<=limit+1?s[..^1]:null;
  }catch(DecoderFallbackException){return null;}
 }
 internal static Dictionary<string,(string Text,uint Hash)> ReadLocres(byte[] data) {
  // Read-only format support, not a package or localization-resource writer.
  // Legacy stores inline strings; compact (1) adds a shared string table;
  // optimized (2/3) adds key hashes, total entries and table reference counts.
  // Do not infer a native culture or identity source from a filename here.
  if(data.Length is <4 or >8*1024*1024)throw new InvalidDataException("Invalid locres size");
  var magic=Convert.FromHexString("0E147475674A03FC4A15909DC3377F1B");
  int version=0,at=0;
  if(data.Length>=16&&data.AsSpan(0,16).SequenceEqual(magic)) {
   if(data.Length<17||(version=data[16])>3)throw new InvalidDataException("Unsupported locres version");
   at=17;
  }
  int bound=data.Length;string[] strings=[];int[] expectedRefs=[],references=[];
  if(version>=1) {
   int pointer=at;Advance(ref at,8,data.Length);
   long offset=BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pointer));
   int minimum=at+(version>=2?8:4);
   if(offset<minimum||offset>data.Length-4)throw new InvalidDataException("Invalid locres string table offset");
   bound=(int)offset;int tableAt=bound,count=BoundedCount(ref tableAt,data.Length);
   strings=new string[count];references=new int[count];expectedRefs=new int[count];
   for(int i=0;i<count;i++) {
    strings[i]=Text(ref tableAt,data.Length,12000);
    if(version>=2)expectedRefs[i]=BoundedCount(ref tableAt,data.Length);
   }
   if(tableAt!=data.Length)throw new InvalidDataException("Unexpected locres string table suffix");
  }
  int expected=version>=2?BoundedCount(ref at,bound):-1,namespaces=BoundedCount(ref at,bound);
  var result=new Dictionary<string,(string,uint)>(StringComparer.Ordinal);
  var seenNamespaces=new HashSet<string>(StringComparer.Ordinal);
  for(int n=0;n<namespaces;n++) {
   if(version>=2)Advance(ref at,4,bound);
   string ns=Text(ref at,bound,512);
   if(ns.Any(char.IsControl)||!seenNamespaces.Add(ns))throw new InvalidDataException("Invalid locres namespace");
   int keys=BoundedCount(ref at,bound);
   if(result.Count+keys>100000||(expected>=0&&result.Count+keys>expected))throw new InvalidDataException("Invalid locres entry count");
   for(int k=0;k<keys;k++) {
    if(version>=2)Advance(ref at,4,bound);
    string key=Text(ref at,bound,256);
    if(key.Length==0||key.Any(char.IsControl))throw new InvalidDataException("Invalid locres key");
    int pos=at;Advance(ref at,4,bound);uint hash=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
    string value;
    if(version>=1) {
     int index=BoundedCount(ref at,bound);
     if(index>=strings.Length)throw new InvalidDataException("Invalid locres string index");
     value=strings[index];references[index]++;
    } else value=Text(ref at,bound,12000);
    if(!result.TryAdd(ns+"\0"+key,(value,hash)))throw new InvalidDataException("Duplicate locres identity");
   }
  }
  if(at!=bound||(expected>=0&&result.Count!=expected)||(version>=2&&!references.SequenceEqual(expectedRefs)))throw new InvalidDataException("Locres structure mismatch");
  return result;
  int BoundedCount(ref int cursor,int end) {
   int pos=cursor;Advance(ref cursor,4,end);int count=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
   return count is >=0 and <=100000?count:throw new InvalidDataException("Invalid locres count");
  }
  string Text(ref int cursor,int end,int limit) {
   if(cursor>end-4)throw new InvalidDataException("Truncated locres string");
   string value=ReadString(data,ref cursor,limit)??throw new InvalidDataException("Invalid locres string");
   if(cursor>end)throw new InvalidDataException("Locres string crosses table boundary");
   return value;
  }
 }
 private static void Advance(ref int at,int n,int limit){if(at<0||at>limit-n)throw new InvalidDataException();at+=n;}
}
