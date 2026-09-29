namespace ScreenshotTranslationUiTester.CorePipelineV2;

/// <summary>Read-only four-sided border evidence. It grants layout space, never cleanup authority.</summary>
internal static class SourceTextPanel
{
    internal sealed record Evidence(RectangleF Interior, Color Border, int Thickness, float Coverage)
    {public string Proof {get;init;}="FOUR_SIDED_BORDER";}
    internal static Evidence? ObserveControl(ReadOnlyBitmapPixelBuffer p,VisualBlock block,Color background)
    {
        if(block.SourceCell is {} cell)return new(cell.Bounds,cell.Surface,1,1){Proof=cell.Proof};
        var b=block.Bounds;var h=block.Lines.Select(l=>l.Bounds.Height).Order().ElementAt(block.Lines.Count/2);
        if(block.Lines.Count>2||h<8||h>p.Height*.12f)return null;
        var textExclusions=block.Lines.Select(line=>RectangleF.Inflate(line.Bounds,2,2)).ToArray();
        var clearRows=new Dictionary<(int Left,int Right,int Y),bool>();
        bool Same(int x,int y)=>x>=0&&y>=0&&x<p.Width&&y<p.Height&&Distance(p.GetPixel(x,y),background)<18;
        var center=(int)(b.Left+b.Width/2);var reach=(int)Math.Max(b.Width*2,p.Width*.3f);
        for(var yy=(int)b.Top-1;yy>=Math.Max(1,b.Top-h);yy--)
        {
            if(!Same(center,yy))continue;
            var l=center;var r=center;
            while(l>Math.Max(1,center-reach)&&Same(l-1,yy))l--;
            while(r<Math.Min(p.Width-2,center+reach)&&Same(r+1,yy))r++;
            if(l>b.Left+h*.2f||r<b.Right-h*.2f||r-l<b.Width*.95f)continue;
            var inset=Math.Max(4,(int)(h*.25f));var left=l+inset;var right=r-inset;
            if(left>=b.Right||right<=b.Left)continue;
            bool ClearRow(int y)
            {
                if(y<1||y>=p.Height-1)return false;
                var key=(left,right,y);
                if(clearRows.TryGetValue(key,out var cached))return cached;
                var count=0;var valid=0;
                for(var x=left;x<right;x+=2)
                {
                    if(textExclusions.Any(bounds=>bounds.Contains(x,y)))continue;
                    valid++;if(Same(x,y))count++;
                }
                var clear=valid>=4?count>=valid*.96f:
                    textExclusions.Any(bounds=>bounds.Contains(center,y));
                clearRows[key]=clear;
                return clear;
            }
            var top=yy;var bottom=yy;
            while(top>Math.Max(1,b.Top-h*2)&&ClearRow(top-1))top--;
            while(bottom<Math.Min(p.Height-2,b.Bottom+h*2)&&ClearRow(bottom+1))bottom++;
            if(top>=b.Top-2||bottom<b.Bottom-h*.15f||bottom-top>h*4.5f)continue;
            // A uniform unbounded scene region is not a control. Both horizontal
            // edges and both vertical edges must actually terminate in the source.
            if(ClearRow(top-1)||ClearRow(bottom+1))continue;
            var contrast=0;
            for(var n=1;n<=9;n++)
            {
                var x=left+(right-left)*n/10;
                bool EdgeContrast(int start,int direction)=>Enumerable.Range(1,5).Any(d=>
                    start+direction*d>=0&&start+direction*d<p.Height&&Distance(p.GetPixel(x,start+direction*d),background)>28);
                if(EdgeContrast(top,-1)&&EdgeContrast(bottom,1))contrast++;
            }
            if(contrast<7)continue;
            return new(RectangleF.FromLTRB(left,top+2,right,bottom-2),background,inset,contrast/9f)
                {Proof="ENCLOSED_SOURCE_CONTROL_INTERIOR"};
        }
        return null;
    }
    internal static Evidence? ObserveLeftInset(ReadOnlyBitmapPixelBuffer p,VisualBlock block)
    {
        if(block.Lines.Count!=1)return null;
        var b=block.Bounds;var h=b.Height;var cx=(int)(b.Left+b.Width*.5f);
        if(b.Width<p.Width*.2f||h>p.Height*.1f)return null;
        // Open ornamental dialogue frames need not have a visible right edge.
        // This proof retains the observed source width; it authorizes only an anchor.
        for(var bottom=(int)b.Bottom+3;bottom<Math.Min(p.Height-3,b.Bottom+h*5);bottom++)
        {
            var color=p.GetPixel(cx,bottom);
            if(Distance(color,p.GetPixel(cx,Math.Max(0,bottom-5)))<60)continue;
            var left=cx;var right=cx;var gap=0;var limit=Math.Max(4,(int)(h*.4f));
            for(var x=cx-1;x>1&&gap<limit;x--)if(Matches(p.GetPixel(x,bottom),color)){left=x;gap=0;}else gap++;
            gap=0;
            for(var x=cx+1;x<p.Width-2&&gap<limit;x++)if(Matches(p.GetPixel(x,bottom),color)){right=x;gap=0;}else gap++;
            if(left>b.Left+h*.5f||right<b.Right+h*.5f||right-left>b.Width*2.5f)continue;
            var bar=Enumerable.Range(1,19).Count(i=>Matches(p.GetPixel(left+(right-left)*i/20,bottom),color));
            if(bar<15)continue;
            for(var edge=(int)b.Left-3;edge>Math.Max(1,b.Left-h*1.5f);edge--)
            {
                var count=Enumerable.Range(1,15).Count(i=>Matches(p.GetPixel(edge,(int)b.Top+(bottom-(int)b.Top)*i/18),color));
                if(count<13)continue;
                var gapLeft=b.Left-edge;var gapRight=right-b.Right;
                if(gapLeft>h || gapRight<gapLeft*2.5f)continue;
                return new(RectangleF.FromLTRB(edge,b.Top-3,right,bottom),color,1,count/15f)
                    {Proof="SOURCE_LEFT_INSET_ON_OPEN_ORNAMENTAL_FRAME_NO_WIDTH_EXPANSION"};
            }
        }
        return null;
    }
    internal static Evidence? Observe(ReadOnlyBitmapPixelBuffer pixels, VisualBlock block,Action<object>? trace=null)
    {
        var b=block.Bounds;
        var h=block.Lines.Select(l=>l.Bounds.Height).Order().ElementAt(block.Lines.Count/2);
        if(b.Width<pixels.Width*.12f || b.Height>pixels.Height*.65f)return null;
        var x=(int)(b.Left+b.Width*.5f);
        var limit=(int)Math.Min(h*6,pixels.Height*.22f);
        var topMin=Math.Max(2,(int)b.Top-(int)(h*2));
        for(var top=(int)b.Top-2;top>=topMin;top-=2)
        {
            var color=pixels.GetPixel(x,top);
            if(Distance(color,pixels.GetPixel(x,Math.Min(pixels.Height-1,top+5)))<45)continue;
            var left=x;var right=x;
            // Native borders can be interrupted by a short bubble pointer and
            // inherit a little scene tint. The other three sides must still agree.
            var gapLimit=Math.Max(3,(int)(h*.9f));
            var gap=0;
            for(var xx=x-1;xx>1&&gap<=gapLimit;xx--)
                if(Matches(pixels.GetPixel(xx,top),color)){left=xx;gap=0;}else gap++;
            gap=0;
            for(var xx=x+1;xx<pixels.Width-2&&gap<=gapLimit;xx++)
                if(Matches(pixels.GetPixel(xx,top),color)){right=xx;gap=0;}else gap++;
            if(left>b.Left+2 || right<b.Left+b.Width*.7f || right-left>b.Width*2.5f)
            {trace?.Invoke(new{Stage="TOP_SPAN_REJECT",top,left,right});continue;}
            var bestCoverage=0f;var bestBottom=0;
            for(var bottom=(int)b.Bottom+2;bottom<Math.Min(pixels.Height-2,b.Bottom+limit);bottom+=2)
            {
                if(!Matches(pixels.GetPixel(x,bottom),color))continue;
                var leftSupport=Enumerable.Range(1,23).Count(i=>Enumerable.Range(-2,5).Any(d=>
                    left+d>=0&&left+d<pixels.Width&&Matches(pixels.GetPixel(left+d,top+(bottom-top)*i/24),color)));
                if(leftSupport<20)continue;
                // Artwork can interrupt the top border before its right corner.
                // Locate that corner from the lower edge and its vertical side;
                // do not mistake the end of the interrupted segment for the control.
                var edgeRight=right;var coverage=Coverage(pixels,color,left,edgeRight,top,bottom);
                if(edgeRight<b.Right || coverage<.84f)
                {
                    for(var candidate=(int)Math.Ceiling(b.Right);candidate<Math.Min(pixels.Width-2,b.Right+Math.Min(b.Width*.7f,h*6));candidate+=2)
                    {
                        if(!Matches(pixels.GetPixel(candidate,bottom),color))continue;
                        var score=Coverage(pixels,color,left,candidate,top,bottom);
                        if(score>coverage){coverage=score;edgeRight=candidate;}
                        if(score>=.96f)break;
                    }
                }
                if(coverage>bestCoverage){bestCoverage=coverage;bestBottom=bottom;}
                if(coverage<.84f||edgeRight<b.Right)continue;
                var topSupport=Enumerable.Range(1,19).Count(i=>Matches(pixels.GetPixel(left+(edgeRight-left)*i/20,top),color));
                if(topSupport<12)continue;
                // A flat paragraph background interrupted by letters is not a
                // border. Require contrast against its interior along every side.
                var inset=Math.Clamp((int)(h*.18f),4,16);
                if(!InteriorContrast(pixels,left,edgeRight,top,bottom,inset))
                {trace?.Invoke(new{Stage="CONTRAST_REJECT",top,left,right=edgeRight,bottom,coverage});continue;}
                var thickness=1;
                while(thickness<h*.22f && left+thickness<edgeRight &&
                    Matches(pixels.GetPixel(left+thickness,(top+bottom)/2),color))thickness++;
                return new(RectangleF.FromLTRB(left+thickness,top+thickness,edgeRight-thickness,bottom-thickness),
                    color,thickness,coverage);
            }
            trace?.Invoke(new{Stage="COVERAGE_REJECT",top,left,right,bestBottom,bestCoverage});
        }
        return null;
    }
    private static float Coverage(ReadOnlyBitmapPixelBuffer p,Color c,int l,int r,int t,int b)
    {
        var counts=new int[3]; const int n=24;
        for(var i=1;i<n;i++)
        {
            var y=t+(b-t)*i/n;var x=l+(r-l)*i/n;
            bool Near(int xx,int yy)=>Enumerable.Range(-2,5).Any(d=>
                xx+d>=0&&xx+d<p.Width&&Matches(p.GetPixel(xx+d,yy),c));
            if(Near(l,y))counts[0]++;
            if(Near(r,y))counts[1]++;
            if(Matches(p.GetPixel(x,b),c))counts[2]++;
        }
        return counts.Min()/(float)(n-1);
    }
    private static int Distance(Color a,Color b)=>Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)));
    private static bool Matches(Color a,Color b)
    {
        if(Distance(a,b)<32)return true;
        var hue=Math.Abs(a.GetHue()-b.GetHue());hue=Math.Min(hue,360-hue);
        return a.GetSaturation()>.35f&&b.GetSaturation()>.35f&&hue<18 &&
            Math.Abs(Math.Max(a.R,Math.Max(a.G,a.B))-Math.Max(b.R,Math.Max(b.G,b.B)))<55;
    }
    private static bool InteriorContrast(ReadOnlyBitmapPixelBuffer p,int l,int r,int t,int b,int d)
    {
        if(r-l<d*3||b-t<d*3)return false;
        var good=new int[4];
        for(var i=1;i<10;i++)
        {
            var x=l+(r-l)*i/10;var y=t+(b-t)*i/10;
            bool Edge(int xx,int yy,int dx,int dy,Color inside)=>Enumerable.Range(-2,5).Any(k=>
                xx+dx*k>=0&&xx+dx*k<p.Width&&yy+dy*k>=0&&yy+dy*k<p.Height&&
                Distance(p.GetPixel(xx+dx*k,yy+dy*k),inside)>38);
            if(Edge(x,t,0,1,p.GetPixel(x,t+d)))good[0]++;
            if(Edge(x,b,0,1,p.GetPixel(x,b-d)))good[1]++;
            if(Edge(l,y,1,0,p.GetPixel(l+d,y)))good[2]++;
            if(Edge(r,y,1,0,p.GetPixel(r-d,y)))good[3]++;
        }
        return good.Min()>=6;
    }
}
