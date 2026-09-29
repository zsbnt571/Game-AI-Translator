namespace ScreenshotTranslationUiTester;

// Source glyph evidence can cross a background connectivity cut. It does not
// make the exterior a donor, nor authorize the rest of the OCR rectangle.
internal static class RegionSourceStrokes
{
    internal static bool[] Layout(Rectangle scan, RegionColor[] colors,bool[] cleanup,bool[] donors,bool[,] protectedPixels,Color? trustedCenter=null)
    {
        int w=scan.Width,h=scan.Height;
        var center=trustedCenter??RegionColor.Median(colors,donors);
        var result=(bool[])cleanup.Clone();
        // Connectivity barriers also include subtle bands in the interior
        // gradient. They remain preserved in cleanup, but do not make the
        // usable interior smaller than its observed background material.
        // Only two source pixels of matching material can join the layout;
        // no extrapolation across contrasting outlines, icons or to endpoints.
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            int i=y*w+x;
            if(protectedPixels[scan.X+x,scan.Y+y]){result[i]=false;continue;}
            if(result[i])continue;
            var c=colors[i];int difference=Math.Max(Math.Abs(c.R-center.R),Math.Max(Math.Abs(c.G-center.G),Math.Abs(c.B-center.B)));
            if(difference>32)continue;
            for(int dy=-2;dy<=2&&!result[i];dy++)for(int dx=-2;dx<=2;dx++)
            {
                if(dx*dx+dy*dy>4)continue;
                int xx=x+dx,yy=y+dy;
                if(xx>=0&&yy>=0&&xx<w&&yy<h&&cleanup[yy*w+xx]){result[i]=true;break;}
            }
        }
        return result;
    }
    internal static bool[] Complete(Rectangle scan, Rectangle text, RegionColor[] colors,
        bool[] interior, bool[] donors, bool[] rules, bool[,] authority,
        bool[,] sourceCore, bool[,] protectedPixels, CancellationToken token,Color? trustedCenter=null)
    {
        int w=scan.Width,h=scan.Height,n=w*h;
        var result=(bool[])interior.Clone();
        var background=trustedCenter??RegionColor.Median(colors,donors);
        var envelope=Rectangle.Intersect(scan,Rectangle.Inflate(text,2,2));
        var foreground=new bool[n];var seen=new bool[n];var queue=new int[n];
        for(int y=envelope.Top;y<envelope.Bottom;y++)for(int x=envelope.Left;x<envelope.Right;x++)
        {
            int i=(y-scan.Top)*w+x-scan.Left;var c=colors[i];
            int distance=Math.Max(Math.Abs(c.R-background.R),Math.Max(Math.Abs(c.G-background.G),Math.Abs(c.B-background.B)));
            foreground[i]=distance>=22&&!rules[i]&&!protectedPixels[x,y]&&authority[x,y];
        }
        for(int seed=0;seed<n;seed++)
        {
            if(!foreground[seed]||seen[seed])continue;
            token.ThrowIfCancellationRequested();
            int head=0,tail=1,core=0,inside=0,left=w,top=h,right=0,bottom=0;
            queue[0]=seed;seen[seed]=true;
            while(head<tail)
            {
                int i=queue[head++],x=i%w,y=i/w;
                left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
                if(sourceCore[scan.X+x,scan.Y+y])core++;
                if(interior[i])inside++;
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    int xx=x+dx,yy=y+dy;if(xx<0||yy<0||xx>=w||yy>=h)continue;
                    int j=yy*w+xx;if(!seen[j]&&foreground[j]){seen[j]=true;queue[tail++]=j;}
                }
            }
            // A source-supported stroke component, not a long frame segment or
            // an arbitrary connected piece of scenery outside the text run.
            if(core==0||inside==0||right-left+1>text.Height*1.5||bottom-top+1>text.Height*1.1)continue;
            for(int q=0;q<tail;q++)
            {
                int x=queue[q]%w,y=queue[q]/w;
                for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++)
                {
                    if(dx*dx+dy*dy>5)continue;
                    int xx=x+dx,yy=y+dy;if(xx<0||yy<0||xx>=w||yy>=h)continue;
                    int j=yy*w+xx,sx=scan.X+xx,sy=scan.Y+yy;
                    if(envelope.Contains(sx,sy)&&authority[sx,sy]&&!rules[j]&&!protectedPixels[sx,sy])result[j]=true;
                }
            }
        }
        return result;
    }
}
