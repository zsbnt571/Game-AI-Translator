namespace ScreenshotTranslationUiTester;

internal static class RegionMaterialEvidence
{
    internal sealed record Decision(bool Reject,int Radius,int BroadInside,int PreservedOutside,string Reason,int AbruptEdges=0,int BoundarySamples=0);
    // A second substantial material connected across the text and an unowned
    // surrounding part is contrary evidence to a single planar substrate.
    // Thin glyph strokes and thin rules cannot establish that evidence alone.
    internal static Decision Check(Rectangle scan,Rectangle text,RegionColor[] colors,bool[] domain,
        bool[] donors,bool[,] authority,float lineHeight,CancellationToken token,Color? trustedCenter=null)
    {
        token.ThrowIfCancellationRequested();
        int w=scan.Width,h=scan.Height,n=w*h;
        var center=trustedCenter??RegionColor.Median(colors,donors);
        int radius=Math.Clamp((int)Math.Ceiling(lineHeight*.18f),3,8);
        var distance=new byte[n];var seen=new bool[n];var queue=new int[n];
        for(int i=0;i<n;i++)
        {
            var c=colors[i];int difference=Math.Max(Math.Abs(c.R-center.R),Math.Max(Math.Abs(c.G-center.G),Math.Abs(c.B-center.B)));
            if(domain[i]&&difference>=22)distance[i]=255;
        }
        // Distance from the actual alternate material edge, no inferred box.
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            int i=y*w+x;if(distance[i]==0)continue;
            int d=x==0||y==0?1:Math.Min(distance[i-1],Math.Min(distance[i-w],distance[i-w-1]))+1;
            distance[i]=(byte)Math.Min(distance[i],d);
        }
        for(int y=h-1;y>=0;y--)for(int x=w-1;x>=0;x--)
        {
            int i=y*w+x;if(distance[i]==0)continue;
            int d=x==w-1||y==h-1?1:Math.Min(distance[i+1],Math.Min(distance[i+w],distance[i+w+1]))+1;
            distance[i]=(byte)Math.Min(distance[i],d);
        }
        for(int seed=0;seed<n;seed++)
        {
            if(distance[seed]==0||seen[seed])continue;
            token.ThrowIfCancellationRequested();int head=0,tail=1,broad=0,outside=0,abrupt=0,boundary=0;
            seen[seed]=true;queue[0]=seed;
            while(head<tail)
            {
                int i=queue[head++],x=i%w,y=i/w,sx=scan.X+x,sy=scan.Y+y;
                if(distance[i]>=radius&&text.Contains(sx,sy))broad++;
                if(distance[i]>=radius&&!text.Contains(sx,sy)&&!authority[sx,sy])outside++;
                // A gradual tint crossing a color threshold is not a material
                // edge. Require an observed abrupt boundary in the preserved
                // surroundings, where source lettering cannot provide it.
                if(!text.Contains(sx,sy)&&!authority[sx,sy]&&distance[i]==1)
                {
                    for(int direction=0;direction<4;direction++)
                    {
                        int xx=x+(direction==0?-1:direction==1?1:0),yy=y+(direction==2?-1:direction==3?1:0);if(xx<0||yy<0||xx>=w||yy>=h)continue;
                        int j=yy*w+xx;if(!domain[j]||distance[j]!=0)continue;
                        boundary++;var a=colors[i];var b=colors[j];
                        if(Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B)))>=16)abrupt++;
                    }
                }
                for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                {
                    int xx=x+dx,yy=y+dy;if(xx<0||yy<0||xx>=w||yy>=h)continue;
                    int j=yy*w+xx;if(distance[j]>0&&!seen[j]){seen[j]=true;queue[tail++]=j;}
                }
            }
            if(broad>=radius*radius&&outside>=radius*radius&&abrupt>=radius*2&&abrupt*2>=boundary)
                return new(true,radius,broad,outside,"SECOND_MATERIAL_WITH_ABRUPT_RETAINED_CONTOUR_CROSSES_SOURCE_RUN",abrupt,boundary);
        }
        return new(false,radius,0,0,"NO_BROAD_SECOND_MATERIAL_CROSSING_SOURCE_RUN");
    }
}
