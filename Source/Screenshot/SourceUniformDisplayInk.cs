namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Local source material proof for bright uniform display glyphs, not a role or logo classifier.</summary>
internal static class SourceUniformDisplayInk
{
    internal sealed record Evidence(Rectangle Bounds,Rectangle InkBounds,Color Fill,int GlyphComponents,float MedianStrokeRatio,
        IReadOnlyList<Point> Pixels,string Reason);
    internal static Evidence? Observe(ReadOnlyBitmapPixelBuffer source,VisualBlock block,bool[,] protection)
    {
        if(block.SourceRole?.Role!="Heading"||block.Lines.Count!=1||block.SourceText.Length>80||block.Lines[0].Confidence<.94f)return null;
        var letters=block.SourceText.Count(char.IsLetter);
        var rect=Rectangle.Intersect(new(Point.Empty,source.Size),Rectangle.Ceiling(block.Bounds));
        var oriented=SourceOrientedText.Observe(block);
        if(letters<3||rect.Height<64||rect.Width<(oriented?.Height??rect.Height)*(oriented is null?2:1.5f)||rect.Width*rect.Height>800_000)return null;
        var w=rect.Width;var h=rect.Height;var colors=new Color[w*h];
        for(var y=0;y<h;y++)for(var x=0;x<w;x++)colors[y*w+x]=source.GetPixel(rect.X+x,rect.Y+y);
        static int Low(Color c)=>Math.Min(c.R,Math.Min(c.G,c.B));
        static int High(Color c)=>Math.Max(c.R,Math.Max(c.G,c.B));
        static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
        // A stable brilliant ink family is a positive source observation. Warm
        // gradient lettering does not acquire authority from this narrow path.
        var groups=colors.Where(c=>Low(c)>=235||High(c)>=235&&High(c)-Low(c)>=200)
            .GroupBy(c=>(c.R/16,c.G/16,c.B/16)).OrderByDescending(g=>g.Count()).Take(4);
        foreach(var group in groups)
        {
            if(group.Count()<w*h*.012f)continue;
            var fill=Color.FromArgb((int)group.Average(c=>c.R),(int)group.Average(c=>c.G),(int)group.Average(c=>c.B));
            var white=Low(fill)>=230;var fc=Math.Max(1,High(fill)-Low(fill));
            bool Hue(Color c)
            {
                var chroma=High(c)-Low(c);if(chroma<18||High(c)<55)return false;
                return Math.Max(Math.Abs((c.R-Low(c))/(float)chroma-(fill.R-Low(fill))/(float)fc),
                    Math.Max(Math.Abs((c.G-Low(c))/(float)chroma-(fill.G-Low(fill))/(float)fc),
                        Math.Abs((c.B-Low(c))/(float)chroma-(fill.B-Low(fill))/(float)fc)))<=.50f;
            }
            // A translucent foreground panel can change the same source ink's
            // RGB value across a word. Preserve its dominant hue and strong
            // source contrast rather than selecting only the uncovered letters.
            var seed=colors.Select((c,i)=>(oriented is null||NativeTextMaterial.Contains(block.Lines[0].Polygon,
                rect.X+i%w+.5f,rect.Y+i/w+.5f))&&
                (white?Distance(c,fill)<=22:High(c)>=150&&High(c)-Low(c)>=60&&Hue(c))).ToArray();
            var seen=new bool[seed.Length];var components=new List<(List<int> Pixels,Rectangle Bounds)>();
            for(var start=0;start<seed.Length;start++)
            {
                if(!seed[start]||seen[start])continue;
                var points=new List<int>();var q=new Queue<int>();q.Enqueue(start);seen[start]=true;
                var left=w;var top=h;var right=0;var bottom=0;
                while(q.Count>0)
                {
                    var i=q.Dequeue();points.Add(i);var x=i%w;var y=i/w;
                    left=Math.Min(left,x);right=Math.Max(right,x+1);top=Math.Min(top,y);bottom=Math.Max(bottom,y+1);
                    for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)
                    {var xx=x+dx;var yy=y+dy;if(xx<0||yy<0||xx>=w||yy>=h)continue;var j=yy*w+xx;
                        if(seed[j]&&!seen[j]){seen[j]=true;q.Enqueue(j);}}
                }
                components.Add((points,Rectangle.FromLTRB(left,top,right,bottom)));
            }
            var glyphs=components.Where(c=>c.Pixels.Count>=20&&c.Bounds.Height>=h*.22f&&c.Bounds.Height<=h*.95f&&
                c.Bounds.Width>=3&&c.Bounds.Width<=h*1.6f&&c.Pixels.Count<c.Bounds.Width*c.Bounds.Height*.9f).ToArray();
            if(glyphs.Length<Math.Max(2,(int)Math.Ceiling(letters*.45f))||glyphs.Length>letters*3)continue;
            var ink=glyphs.Select(c=>c.Bounds).Aggregate(Rectangle.Union);
            if(ink.Width<w*.70f||glyphs.Max(c=>c.Bounds.Bottom)-glyphs.Min(c=>c.Bounds.Bottom)>h*.45f)continue;
            // The proof comes from the complete row. Include detached terminals
            // and punctuation inside that source row, not just full-height glyphs.
            var band=new Rectangle(0,0,w,h);
            var chosen=components.Where(c=>band.Contains(c.Bounds)&&c.Bounds.Width<=h*1.6f&&c.Bounds.Height<=h*.95f).ToArray();
            var runs=new List<int>();
            for(var y=ink.Top;y<ink.Bottom;y++)
            {
                var length=0;
                for(var x=ink.Left;x<=ink.Right;x++)
                {if(x<ink.Right&&seed[y*w+x])length++;else{if(length>1)runs.Add(length);length=0;}}
            }
            if(runs.Count<8)continue;
            runs.Sort();var heights=glyphs.Select(c=>c.Bounds.Height).Order().ToArray();
            var strokeRatio=runs[runs.Count/2]/(float)heights[heights.Length/2];
            var pixels=new List<Point>();var distance=Enumerable.Repeat(-1,seed.Length).ToArray();var pending=new Queue<int>();
            foreach(var c in chosen)foreach(var i in c.Pixels){distance[i]=0;pending.Enqueue(i);}
            var radius=white?2:Math.Clamp((int)Math.Ceiling(h*.10f),8,26);
            bool Family(Color c)
            {
                if(white)return Low(c)>=145&&High(c)-Low(c)<=48;
                // A luminous glyph's dim halo can be below the core contrast
                // threshold. Keep hue agreement but follow that connected
                // halo only within the bounded source-scale distance above.
                var chroma=High(c)-Low(c);if(chroma<8||High(c)<20)return false;
                return Math.Max(Math.Abs((c.R-Low(c))/(float)chroma-(fill.R-Low(fill))/(float)fc),
                    Math.Max(Math.Abs((c.G-Low(c))/(float)chroma-(fill.G-Low(fill))/(float)fc),
                        Math.Abs((c.B-Low(c))/(float)chroma-(fill.B-Low(fill))/(float)fc)))<=.50f;
            }
            while(pending.Count>0)
            {
                var i=pending.Dequeue();var x=i%w;var y=i/w;
                if(protection[rect.X+x,rect.Y+y])return null;
                pixels.Add(new(rect.X+x,rect.Y+y));
                if(distance[i]>=radius)continue;
                for(var dy=-1;dy<=1;dy++)for(var dx=-1;dx<=1;dx++)
                {var xx=x+dx;var yy=y+dy;if(!band.Contains(xx,yy))continue;var j=yy*w+xx;
                    if(distance[j]<0&&Family(colors[j])){distance[j]=distance[i]+1;pending.Enqueue(j);}}
            }
            // Antialias pixels are a mixture of ink and local background, so
            // their hue need not match the core. A small source-scale closure
            // prevents those mixed edge colors becoming restoration donors.
            var edge=Math.Clamp((int)Math.Ceiling(h*.025f),2,6);
            var closed=new HashSet<Point>(pixels);
            foreach(var point in pixels)
            for(var dy=-edge;dy<=edge;dy++)for(var dx=-edge;dx<=edge;dx++)
            {
                if(dx*dx+dy*dy>edge*edge)continue;
                var xx=point.X+dx;var yy=point.Y+dy;
                if(!rect.Contains(xx,yy)||protection[xx,yy])continue;
                closed.Add(new(xx,yy));
            }
            pixels=closed.OrderBy(p=>p.Y).ThenBy(p=>p.X).ToList();
            if(pixels.Count<w*h*.015f||pixels.Count>w*h*.85f)continue;
            ink.Offset(rect.Location);
            return new(rect,ink,fill,glyphs.Length,strokeRatio,pixels,"UNIFORM_BRIGHT_SOURCE_GLYPH_ROW_WITH_BOUNDED_COLOR_HALO");
        }
        return null;
    }
}
