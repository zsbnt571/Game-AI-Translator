using System.Text.RegularExpressions;
namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>A source heading separated from its paragraph by spacing, width and source syntax.</summary>
internal static class SourceHeadingBoundary
{
    internal static IReadOnlyDictionary<string,string> ObserveGroups(IReadOnlyList<NormalizedOcrLine> lines,
        ReadOnlyBitmapPixelBuffer? pixels)
    {
        var result=Observe(lines).ToDictionary(id=>id,id=>"SOURCE-HEADING-"+id,StringComparer.Ordinal);
        if(pixels is null)return result;
        bool HeadingCase(NormalizedOcrLine l)
        {
            var letters=l.SourceText.Where(char.IsLetter).ToArray();
            return letters.Length>=4&&letters.Count(char.IsUpper)>=letters.Length*.8f&&
                !Regex.IsMatch(l.SourceText,@"[.!?。！？:]$");
        }
        foreach(var last in lines.Where(HeadingCase))
        {
            var h=last.Bounds.Height;
            var body=lines.Where(l=>l.Bounds.Top>last.Bounds.Top+h*.8f&&
                l.Bounds.Top-last.Bounds.Bottom<=h*1.1f&&Math.Abs(l.Bounds.Left-last.Bounds.Left)<=h*.35f&&
                l.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=4&&!HeadingCase(l))
                .OrderBy(l=>l.Bounds.Top).FirstOrDefault();
            if(body is null)continue;
            var continuation=lines.Any(l=>l.SourceId!=body.SourceId&&
                l.Bounds.Top>body.Bounds.Top+h*.5f&&l.Bounds.Top-body.Bounds.Bottom<h*.6f&&
                Math.Abs(l.Bounds.Left-body.Bounds.Left)<=h*.35f&&
                l.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=4);
            if(!continuation)continue;
            // A visible rule between the heading and paragraph proves a real
            // structural boundary even when OCR heights and baselines overlap.
            var left=(int)Math.Ceiling(body.Bounds.Left+h*.2f);
            var right=(int)Math.Floor(body.Bounds.Right-h*.2f);
            if(right-left<h*8)continue;
            var separator=false;
            for(var y=Math.Max(2,(int)last.Bounds.Bottom-1);y<=Math.Min(pixels.Height-3,(int)body.Bounds.Top+1);y++)
            {
                var count=0;var support=0;
                var color=pixels.GetPixel((left+right)/2,y);
                for(var x=left;x<right;x+=3)
                {
                    count++;
                    int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
                    if(Distance(pixels.GetPixel(x,y),color)<24&&
                        Distance(pixels.GetPixel(x,y-2),color)>30&&Distance(pixels.GetPixel(x,y+2),color)>30)support++;
                }
                if(support>=count*.85f){separator=true;break;}
            }
            if(!separator)continue;
            var group=new List<NormalizedOcrLine>{last};var first=last;
            for(var n=0;n<2;n++)
            {
                var previous=lines.Where(l=>HeadingCase(l)&&l.Bounds.Top<first.Bounds.Top-h*.6f&&
                    first.Bounds.Top-l.Bounds.Bottom<=h*.5f&&Math.Abs(l.Bounds.Left-first.Bounds.Left)<=h*.35f&&
                    l.Bounds.Height>=h*.7f&&l.Bounds.Height<=h*1.5f).OrderByDescending(l=>l.Bounds.Top).FirstOrDefault();
                if(previous is null)break;
                group.Add(previous);first=previous;
            }
            var owner="SOURCE-HEADING-"+first.SourceId;
            foreach(var line in group)result[line.SourceId]=owner;
        }
        return result;
    }
    internal static IReadOnlySet<string> Observe(IReadOnlyList<NormalizedOcrLine> lines)
    {
        var found=new HashSet<string>(StringComparer.Ordinal);
        foreach(var title in lines)
        {
            // Local type hierarchy also establishes single-word/title-case
            // headings. Use the following paragraph, not canvas-wide median:
            // three adjacent cards can have differently tight OCR title boxes.
            var body=lines.Where(l=>l.SourceId!=title.SourceId&&
                l.Bounds.Top>title.Bounds.Top+title.Bounds.Height*.7f&&
                l.Bounds.Top-title.Bounds.Bottom<=title.Bounds.Height*.6f&&
                l.Bounds.Height<title.Bounds.Height/1.5f&&
                Math.Abs(l.Bounds.Left-title.Bounds.Left)<=title.Bounds.Height*.65f)
                .OrderBy(l=>l.Bounds.Top).FirstOrDefault();
            if(body is not null && title.SourceText.Count(char.IsLetter)>=2 &&
                title.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=8 &&
                !Regex.IsMatch(title.SourceText,@"[.!?。！？:]$"))
            {
                var continuation=lines.Where(l=>l.SourceId!=body.SourceId&&
                    l.Bounds.Top>body.Bounds.Top+body.Bounds.Height*.65f&&
                    l.Bounds.Top-body.Bounds.Bottom<=body.Bounds.Height*.7f&&
                    Math.Abs(l.Bounds.Left-body.Bounds.Left)<=body.Bounds.Height*.3f&&
                    l.Bounds.Height>=body.Bounds.Height*.7f&&l.Bounds.Height<=body.Bounds.Height*1.4f)
                    .OrderBy(l=>l.Bounds.Top).FirstOrDefault();
                if(continuation is not null&&body.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=4)
                {found.Add(title.SourceId);continue;}
            }
            // A larger, isolated short leading row and a left-aligned wrapped
            // sentence prove separate ownership even without all-uppercase text.
            var leading=title.SourceText.Trim();
            if(leading.Length<=32 && leading.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=2 &&
                leading.Count(char.IsLetter)>=2 && leading.All(c=>char.IsLetter(c)||char.IsWhiteSpace(c)))
            {
                var first=lines.Where(l=>l.Bounds.Top>title.Bounds.Bottom+title.Bounds.Height*.35f &&
                    l.Bounds.Top-title.Bounds.Bottom<=title.Bounds.Height*.8f &&
                    l.Bounds.Height<=title.Bounds.Height/1.1f && l.Bounds.Height>=title.Bounds.Height*.6f &&
                    l.Bounds.Width>=title.Bounds.Width*4 && Math.Abs(l.Bounds.Left-title.Bounds.Left)<=title.Bounds.Height*.3f &&
                    l.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=6)
                    .OrderBy(l=>l.Bounds.Top).FirstOrDefault();
                if(first is not null && lines.Any(l=>l.SourceId!=first.SourceId &&
                    l.Bounds.Top>first.Bounds.Top+first.Bounds.Height*.65f && l.Bounds.Top-first.Bounds.Bottom<=first.Bounds.Height*.5f &&
                    Math.Abs(l.Bounds.Left-first.Bounds.Left)<=first.Bounds.Height*.3f &&
                    l.Bounds.Height>=first.Bounds.Height*.8f && l.Bounds.Height<=first.Bounds.Height*1.25f &&
                    Regex.IsMatch(l.SourceText,@"[.!?。！？][""'”’]*$")))
                {found.Add(title.SourceId);continue;}
            }
            var letters=title.SourceText.Where(char.IsLetter).ToArray();
            // A compact uppercase heading followed by a complete mixed-case
            // description is a structural pair even at the same OCR height.
            // It must not become the first visual line of one prose sentence.
            if(letters.Length>=4&&letters.All(char.IsUpper)&&
                title.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<=4&&
                !Regex.IsMatch(title.SourceText,@"[.!?。！？:]$"))
            {
                var description=lines.Where(l=>l.Bounds.Top>title.Bounds.Top+title.Bounds.Height*.8f&&
                    l.Bounds.Top-title.Bounds.Bottom<=title.Bounds.Height*.65f&&
                    Math.Abs(l.Bounds.Left-title.Bounds.Left)<=title.Bounds.Height*.3f&&
                    l.Bounds.Height>=title.Bounds.Height*.65f&&l.Bounds.Height<=title.Bounds.Height*1.2f)
                    .OrderBy(l=>l.Bounds.Top).FirstOrDefault();
                if(description is not null&&description.Bounds.Width>title.Bounds.Width*1.25f&&
                    description.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=3&&
                    description.SourceText.Count(char.IsLower)>=4&&
                    Regex.IsMatch(description.SourceText,@"[.!?。！？][\""'”’]*$"))
                {found.Add(title.SourceId);continue;}
            }
            if(letters.Length<4||!letters.Any(char.IsUpper)||letters.Any(char.IsLower)||
                Regex.IsMatch(title.SourceText,@"[.!?。！？:]$")||title.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length<2)continue;
            var h=title.Bounds.Height;
            var rows=lines.Where(l=>l.Bounds.Top>title.Bounds.Top+h*.8f&&l.Bounds.Top<title.Bounds.Bottom+h*7&&
                Math.Abs(l.Bounds.Left-title.Bounds.Left)<=h*.25f&&l.Bounds.Height>=h*.6f&&l.Bounds.Height<=h*1.5f)
                .OrderBy(l=>l.Bounds.Top).Take(5).ToArray();
            if(rows.Length<3||rows[0].Bounds.Top-title.Bounds.Top>h*2)continue;
            var steps=rows.Zip(rows.Skip(1),(a,b)=>b.Bounds.Top+b.Bounds.Height/2-a.Bounds.Top-a.Bounds.Height/2).ToArray();
            var step=steps.Order().ElementAt(steps.Length/2);
            if(step<h*.55f||steps.Any(v=>v<step*.7f||v>step*1.3f))continue;
            var gap=rows[0].Bounds.Top+rows[0].Bounds.Height/2-title.Bounds.Top-h/2;
            if(gap<step*1.35f||gap>step*2.2f)continue;
            var width=rows.Select(l=>l.Bounds.Width).Order().ElementAt(rows.Length/2);
            if(title.Bounds.Width>width*.8f||rows.Count(l=>l.SourceText.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length>=4)<3)continue;
            if(!rows.Any(l=>Regex.IsMatch(l.SourceText,@"[.!?。！？]")))continue;
            found.Add(title.SourceId);
        }
        return found;
    }
}
