using System.Buffers.Binary;
using System.Text.RegularExpressions;

// Read-only preparation candidates, NEVER localization identities. Custom
// dialogue tables often serialize FString instead of FText. A complete bounded
// FString can prepare a translation, but only a separately validated live text
// control is allowed to display it. No source offset is ever used for writing.
internal static class DisplayTextReader
{
 internal static string DisplayLiteral(string s)=>Regex.Replace(s,@"\\[nrt]",m=>m.Value switch {"\\n"=>"\n","\\r"=>"\r","\\t"=>"\t",_=>m.Value});
 internal static bool Accept(string s)
 {
  if(s.Length is <12 or >6000 || s.Trim()!=s || !UnrealTextReader.Accept(s))return false;
  if(s.Contains('/')||s.Contains('\\')||s.Contains("::")||s.Contains("http:",StringComparison.OrdinalIgnoreCase)
   ||s.Contains("https:",StringComparison.OrdinalIgnoreCase)||s.Contains(".uasset",StringComparison.OrdinalIgnoreCase)
   ||Regex.IsMatch(s,@"(?:^|\s)(?:BP_|WBP_|SK_|MI_|/Script/)|[A-Za-z_]\w*\([^)]*\)\s*[;{]|[<>]=?"))return false;
  int latin=0,spaces=0,kana=0,han=0;
  foreach(char c in s){if(char.IsAsciiLetter(c))latin++;if(c==' ')spaces++;if(c is >= '\u3040' and <= '\u30ff')kana++;if(c is >= '\u3400' and <= '\u9fff')han++;}
  // Exclude one-word names, paths, object identifiers and binary debris. Short
  // labels still use structured FText extraction and runtime text discovery.
  return latin>=8&&spaces>=2&&Regex.IsMatch(s,@"[.!?…,:;\""'’—-]|\b(?:the|a|an|you|your|I|we|it|is|are|to|of|and|in)\b",RegexOptions.IgnoreCase)
   ||kana>=2&&kana+han>=6&&Regex.IsMatch(s,"[。！？、…「」]");
 }
 internal static IEnumerable<string> Scan(byte[] data)
 {
  for(int i=0;i<=data.Length-8;i++){
   int size=BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i));
   if(size is >6001 or <-6001 || size is >=-12 and <=12)continue;
   int width=size<0?2:1,length=Math.Abs(size)*width,start=i+4;
   if(length>data.Length-start||data[start+length-1]!=0||(width==2&&data[start+length-2]!=0))continue;
   // Reject binary and embedded terminators before allocating/decoding. This
   // keeps mesh integer arrays from creating millions of decoder exceptions.
   bool readable=true;
   for(int p=start;p<start+length-width;p+=width){
    int ch=width==1?data[p]:BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
    if(ch==0||ch<32&&ch is not 9 and not 10 and not 13){readable=false;break;}
   }
   if(!readable)continue;
   int end=i;var text=UnrealTextReader.ReadString(data,ref end,6000);
   if(text is not null){
    // Dialogue tables may store escaped line breaks. Decode only these three
    // display controls; do not interpret script expressions or path escapes.
    text=DisplayLiteral(text);
    if(Accept(text)){yield return text;i=end-1;}
   }
  }
 }
}
