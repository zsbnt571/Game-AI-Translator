using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Fusion.UnityEmbedded
{
    // A deliberately small JSON wire codec. Never resolves a type name or evaluates a value.
    internal static class BridgeJson
    {
        internal static object Parse(string input)
        {
            var parser = new Reader(input); var value = parser.Value(0); parser.White();
            if (parser.Position != input.Length) throw new FormatException("Trailing JSON content.");
            return value;
        }
        internal static string Write(object value) { var b = new StringBuilder(); Append(b, value); return b.ToString(); }
        static void Append(StringBuilder b, object value)
        {
            if (value == null) { b.Append("null"); return; }
            var text = value as string;
            if (text != null)
            {
                b.Append('"'); foreach (char c in text)
                {
                    switch(c) { case '"':b.Append("\\\"");break; case '\\':b.Append("\\\\");break; case '\n':b.Append("\\n");break;case '\r':b.Append("\\r");break;case '\t':b.Append("\\t");break;
                    default: if(c<32)b.Append("\\u").Append(((int)c).ToString("x4"));else b.Append(c);break; }
                } b.Append('"'); return;
            }
            if(value is bool) { b.Append((bool)value?"true":"false"); return; }
            var dictionary=value as IDictionary;
            if(dictionary!=null) { b.Append('{');bool first=true;foreach(DictionaryEntry item in dictionary){if(!first)b.Append(',');first=false;Append(b,(string)item.Key);b.Append(':');Append(b,item.Value);}b.Append('}');return; }
            var items=value as IEnumerable;
            if(items!=null){b.Append('[');bool first=true;foreach(var item in items){if(!first)b.Append(',');first=false;Append(b,item);}b.Append(']');return;}
            if(value is int||value is long||value is double||value is float){b.Append(Convert.ToString(value,CultureInfo.InvariantCulture));return;}
            throw new InvalidOperationException("Unsupported JSON value.");
        }
        sealed class Reader
        {
            readonly string input;internal int Position;internal Reader(string input){if(input.Length>32*1024*1024)throw new FormatException("JSON limit exceeded.");this.input=input;}
            internal void White(){while(Position<input.Length&&char.IsWhiteSpace(input[Position]))Position++;}
            char Take(){if(Position>=input.Length)throw new FormatException("Incomplete JSON.");return input[Position++];}
            void Expect(char c){White();if(Take()!=c)throw new FormatException("Unexpected JSON token.");}
            internal object Value(int depth)
            {
                if(depth>32)throw new FormatException("JSON depth exceeded.");White();if(Position>=input.Length)throw new FormatException("Missing value.");char c=input[Position];
                if(c=='"')return String();
                if(c=='{') {Position++;var result=new Dictionary<string,object>(StringComparer.Ordinal);White();if(Position<input.Length&&input[Position]=='}'){Position++;return result;}while(true){White();if(Take()!='"')throw new FormatException("Missing object key.");Position--;string key=String();Expect(':');if(result.ContainsKey(key))throw new FormatException("Duplicate key.");result.Add(key,Value(depth+1));White();char next=Take();if(next=='}')return result;if(next!=',')throw new FormatException("Missing comma.");}}
                if(c=='[') {Position++;var result=new List<object>();White();if(Position<input.Length&&input[Position]==']'){Position++;return result;}while(true){result.Add(Value(depth+1));White();char next=Take();if(next==']')return result;if(next!=',')throw new FormatException("Missing comma.");}}
                foreach(var literal in new[]{"true","false","null"})if(Position+literal.Length<=input.Length&&input.Substring(Position,literal.Length)==literal){Position+=literal.Length;return literal=="null"?null:(object)(literal=="true");}
                int start=Position;while(Position<input.Length&&"-+0123456789.eE".IndexOf(input[Position])>=0)Position++;
                double number;if(start==Position||!double.TryParse(input.Substring(start,Position-start),NumberStyles.Float,CultureInfo.InvariantCulture,out number)||double.IsInfinity(number)||double.IsNaN(number))throw new FormatException("Invalid number.");return number;
            }
            string String()
            {
                Expect('"');var b=new StringBuilder();while(true){char c=Take();if(c=='"')return b.ToString();if(c<32)throw new FormatException("Control character.");if(c!='\\'){b.Append(c);continue;}c=Take();switch(c){case '"':case '\\':case '/':b.Append(c);break;case 'b':b.Append('\b');break;case 'f':b.Append('\f');break;case 'n':b.Append('\n');break;case 'r':b.Append('\r');break;case 't':b.Append('\t');break;case 'u':if(Position+4>input.Length)throw new FormatException("Invalid unicode escape.");b.Append((char)int.Parse(input.Substring(Position,4),NumberStyles.HexNumber));Position+=4;break;default:throw new FormatException("Invalid escape.");}}
            }
        }
    }
}
