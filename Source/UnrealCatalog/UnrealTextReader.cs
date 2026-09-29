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
  if(data.Length is <33 or >8*1024*1024||!data.AsSpan(0,16).SequenceEqual(Convert.FromHexString("0E147475674A03FC4A15909DC3377F1B"))||data[16]!=3)throw new InvalidDataException("Unsupported locres");
  long offset=BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(17));if(offset<33||offset>data.Length-4)throw new InvalidDataException();
  int at=(int)offset;int count=Count(data,ref at);var strings=new string[count];var expectedRefs=new int[count];
  for(int i=0;i<count;i++){strings[i]=ReadString(data,ref at,12000)??throw new InvalidDataException();expectedRefs[i]=Count(data,ref at);}
  if(at!=data.Length)throw new InvalidDataException();
  int bound=(int)offset;at=25;int expected=Count(data,ref at),namespaces=Count(data,ref at);var references=new int[count];
  var result=new Dictionary<string,(string,uint)>(StringComparer.Ordinal);
  for(int n=0;n<namespaces;n++) {
   Advance(ref at,4,bound);string ns=ReadString(data,ref at,512)??throw new InvalidDataException();int keys=Count(data,ref at);
   if(result.Count+keys>expected)throw new InvalidDataException();
   for(int k=0;k<keys;k++) {
    Advance(ref at,4,bound);string key=ReadString(data,ref at,256)??throw new InvalidDataException();
    int pos=at;Advance(ref at,8,bound);uint hash=BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));int index=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos+4));
    if(index<0||index>=count||!result.TryAdd(ns+"\0"+key,(strings[index],hash)))throw new InvalidDataException();references[index]++;
   }
  }
  if(at!=offset||result.Count!=expected||!references.SequenceEqual(expectedRefs))throw new InvalidDataException();return result;
 }
 private static int Count(byte[] data,ref int at){int pos=at;Advance(ref at,4,data.Length);int n=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));return n is >=0 and <=100000?n:throw new InvalidDataException();}
 private static void Advance(ref int at,int n,int limit){if(at<0||at>limit-n)throw new InvalidDataException();at+=n;}
}
