using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using static ScreenshotTranslationUiTester.DefoldWire;

namespace ScreenshotTranslationUiTester;

internal static class DefoldFontBuilder
{
    internal sealed record CoverageReport(string FontHash,int Requested,int Existing,int Added,int[] MissingFromFont,int[] Unsupported,int[] NoOutline,string OutputHash);
    internal sealed record Extension(byte[] Bytes,CoverageReport Coverage);
    internal static IEnumerable<int> DefaultCharacters()
        =>Enumerable.Range(0x20,0x250-0x20).Concat(Enumerable.Range(0x370,0x530-0x370))
            .Concat(Enumerable.Range(0x4e00,0x5200)).Concat(Enumerable.Range(0x3000,0x100))
            .Concat(Enumerable.Range(0xff01,0x60)).Concat(Enumerable.Range(0x2000,0x70))
            .Concat(Enumerable.Range(0x1100,0x100)).Concat(Enumerable.Range(0x3130,0x60))
            .Concat(Enumerable.Range(0xac00,0x2bb0));
    internal static byte[] Extend(byte[] original,string fontPath,IEnumerable<int>? characters=null)
        =>ExtendWithCoverage(original,fontPath,characters).Bytes;
    internal static Extension ExtendWithCoverage(byte[] original,string fontPath,IEnumerable<int>? characters=null)
    {
        if(original.Length>64*1024*1024)throw new InvalidDataException("原字体资源超过限制。");
        var fields=Read(original);
        Item? Field(int n)
        {var found=fields.Where(x=>x.Number==n).ToArray();if(found.Length>1)throw new InvalidDataException("字体含重复的单值字段。");return found.SingleOrDefault();}
        int Num(int n,int fallback=0)
        {var f=Field(n);if(f is not null&&f.Kind!=0)throw new NotSupportedException("不是已支持的旧版内嵌字库结构。");return checked((int)(f?.Value??(ulong)fallback));}
        float Real(int n,float fallback=0)
        {var f=Field(n);if(f is null)return fallback;if(f.Kind!=5||f.Data.Length!=4)throw new NotSupportedException("不是已支持的旧版内嵌字库结构。");float v=BitConverter.ToSingle(f.Data);if(!float.IsFinite(v))throw new InvalidDataException("字体度量无效。");return v;}
        int format=Num(10),channels=Num(20),padding=Num(17);float spread=Real(11,1);
        if(format is not (0 or 1)||channels is not (1 or 3 or 4)||padding is <0 or >8||spread is <0.1f or >32||Real(8) is <2 or >128)throw new InvalidDataException("字体格式或大小暂不支持内嵌字库扩展。");
        if(Field(21) is not {Kind:2} glyphData||fields.Any(x=>x.Number==1&&x.Kind!=2))throw new NotSupportedException("当前仅支持旧版内嵌字形资源，不处理分离的 glyph bank。");
        var present=new HashSet<int>();
        foreach(var glyph in fields.Where(x=>x.Number==1))
        {
            var row=Read(glyph.Data);var id=row.Where(x=>x.Number==1&&x.Kind==0).ToArray();
            if(id.Length!=1||id[0].Value>0x10ffff||!present.Add((int)id[0].Value))throw new InvalidDataException("原字体字符编号重复或无效。");
            int offset=checked((int)(row.SingleOrDefault(x=>x.Number==9&&x.Kind==0)?.Value??0));
            int size=checked((int)(row.SingleOrDefault(x=>x.Number==10&&x.Kind==0)?.Value??0));
            if(offset<0||size<0||offset>glyphData.Data.Length-size)throw new InvalidDataException("原字体字形数据越界。");
        }
        int[] supplied=(characters??DefaultCharacters()).Take(65537).ToArray();
        if(supplied.Length>65536||supplied.Any(c=>!Rune.IsValid(c)))throw new InvalidDataException("请求的字体字符范围无效。");
        int[] requested=supplied.Distinct().ToArray();
        int[] unsupported=requested.Where(c=>c>0xffff).Order().ToArray();
        int[] candidates=requested.Where(c=>c>=32&&c<=0xffff&&!Rune.IsControl(new(c))&&!Rune.IsWhiteSpace(new(c))&&!present.Contains(c)).ToArray();
        var coverage=GameFontCoverage.Check(fontPath,string.Concat(candidates.Select(char.ConvertFromUtf32)),65536);
        var missing=coverage.Missing.ToHashSet();var noOutline=new List<int>();
        using var collection=new PrivateFontCollection();collection.AddFontFile(fontPath);if(collection.Families.Length==0)throw new InvalidDataException("翻译字库未加载。");using var family=collection.Families[0];
        using var data=new MemoryStream();data.Write(glyphData.Data);
        int cellWidth=Num(18),cellHeight=Num(19),cellAscent=Num(25);float maxAscent=Real(8),maxDescent=Real(9);float em=Math.Clamp(MathF.Round(maxAscent*0.95f),8,96);
        int margin=format==1?(int)Math.Ceiling(spread):0;float baseline=em*family.GetCellAscent(FontStyle.Regular)/family.GetEmHeight(FontStyle.Regular);
        var added=new List<Item>();
        foreach(int ch in candidates)
        {
            // A .notdef outline is not a usable glyph for a missing codepoint.
            if(missing.Contains(ch))continue;
            using var path=new GraphicsPath();path.AddString(char.ConvertFromUtf32(ch),family,(int)FontStyle.Regular,em,PointF.Empty,StringFormat.GenericTypographic);
            if(path.PointCount==0){noOutline.Add(ch);continue;}RectangleF bounds=path.GetBounds();int left=(int)Math.Floor(bounds.Left)-margin,top=(int)Math.Floor(bounds.Top)-margin;
            int width=(int)Math.Ceiling(bounds.Right)-left+margin,ascent=Math.Max(0,(int)Math.Ceiling(baseline)-top),descent=Math.Max(0,(int)Math.Ceiling(bounds.Bottom+margin-baseline));
            int height=ascent+descent;if(width>160||height>160||width<1||height<1)throw new InvalidDataException("中文字形尺寸超出限制。");
            int w=width+padding*2,h=height+padding*2;using var bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb);using(var g=Graphics.FromImage(bitmap)){g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;using var matrix=new Matrix();matrix.Translate(-left+padding,-top+padding);path.Transform(matrix);g.FillPath(Brushes.White,path);}
            var bits=bitmap.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);byte[] rgba=new byte[bits.Stride*h];try{Marshal.Copy(bits.Scan0,rgba,0,rgba.Length);}finally{bitmap.UnlockBits(bits);}
            byte[] mask=new byte[w*h];for(int y=0;y<h;y++)for(int x=0;x<w;x++)mask[y*w+x]=rgba[y*bits.Stride+x*4+3];
            if(format==1)mask=Distance(mask,w,h,spread);
            int offset=checked((int)data.Position);data.WriteByte(0); // Native fontmap raw-glyph header.
            foreach(byte b in mask){data.WriteByte(b);if(channels>1){data.WriteByte(0);data.WriteByte(0);if(channels==4)data.WriteByte(b);}}
            added.Add(Bytes(1,Write([UInt(1,ch),UInt(2,width),Float(3,MathF.Ceiling(em)),Float(4,left),UInt(5,ascent),UInt(6,descent),UInt(9,offset),UInt(10,checked((int)data.Position-offset))])));
            cellWidth=Math.Max(cellWidth,w);cellHeight=Math.Max(cellHeight,h);cellAscent=Math.Max(cellAscent,ascent);maxAscent=Math.Max(maxAscent,ascent-margin);maxDescent=Math.Max(maxDescent,descent-margin);
            if(data.Length>60*1024*1024)throw new InvalidDataException("扩展字库超过大小限制。");
        }
        // Both existing and added glyphs fit the same atlas cell; reserve enough
        // vertical space for the largest ascent and descent combination.
        int maximumDescent=fields.Where(x=>x.Number==1).Concat(added).Select(x=>(int)(Read(x.Data).SingleOrDefault(y=>y.Number==6)?.Value??0)).DefaultIfEmpty(0).Max();
        cellHeight=Math.Max(cellHeight,cellAscent+maximumDescent+padding*2);
        int cacheHeight=Math.Max(Num(16),1024);fields.RemoveAll(x=>new[]{8,9,16,18,19,21,25}.Contains(x.Number));fields.AddRange(added);fields.AddRange([Float(8,maxAscent),Float(9,maxDescent),UInt(16,cacheHeight),UInt(18,cellWidth),UInt(19,cellHeight),Bytes(21,data.ToArray()),UInt(25,cellAscent)]);
        byte[] output=Write(fields);var readback=Read(output);
        if(!One(output,21).AsSpan(0,glyphData.Data.Length).SequenceEqual(glyphData.Data))throw new InvalidDataException("原字体图形数据未保持一致。");
        var byId=readback.Where(x=>x.Number==1).GroupBy(x=>Read(x.Data).Single(y=>y.Number==1).Value).ToDictionary(g=>g.Key,g=>g.ToArray());
        foreach(var glyph in added)
        {
            var row=Read(glyph.Data);int id=checked((int)row.Single(x=>x.Number==1).Value);
            var stored=byId.GetValueOrDefault((ulong)id)??[];
            int offset=checked((int)row.Single(x=>x.Number==9).Value),size=checked((int)row.Single(x=>x.Number==10).Value);
            if(stored.Length!=1||!stored[0].Data.AsSpan().SequenceEqual(glyph.Data)||offset<glyphData.Data.Length||size<2||offset>data.Length-size)
                throw new InvalidDataException("新增字形读回核对失败。");
        }
        if(GameFontCoverage.Check(fontPath,"",1).FontHash!=coverage.FontHash)throw new IOException("翻译字体在生成期间发生变化，未提交。");
        return new(output,new(coverage.FontHash,requested.Length,requested.Count(present.Contains),added.Count,
            coverage.Missing,unsupported,noOutline.ToArray(),CatalogOrigins.Hash(output)));
    }
    // A bounded two-pass distance transform, using subpixel coverage at the
    // edge. Defold's distance-field material places the face edge at 0.75.
    private static byte[] Distance(byte[] mask,int w,int h,float spread)
    {
        float[] To(bool inside){float[] d=new float[mask.Length];for(int i=0;i<d.Length;i++)d[i]=(mask[i]>=128)==inside?0:10000;const float diagonal=1.41421356f;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++){int i=y*w+x;if(x>0)d[i]=Math.Min(d[i],d[i-1]+1);if(y>0){d[i]=Math.Min(d[i],d[i-w]+1);if(x>0)d[i]=Math.Min(d[i],d[i-w-1]+diagonal);if(x+1<w)d[i]=Math.Min(d[i],d[i-w+1]+diagonal);}}
            for(int y=h-1;y>=0;y--)for(int x=w-1;x>=0;x--){int i=y*w+x;if(x+1<w)d[i]=Math.Min(d[i],d[i+1]+1);if(y+1<h){d[i]=Math.Min(d[i],d[i+w]+1);if(x>0)d[i]=Math.Min(d[i],d[i+w-1]+diagonal);if(x+1<w)d[i]=Math.Min(d[i],d[i+w+1]+diagonal);}}return d;}
        var a=To(true);var b=To(false);byte[] output=new byte[mask.Length];for(int i=0;i<output.Length;i++){float dist=mask[i] is >0 and <255?mask[i]/255f-0.5f:mask[i]>=128?b[i]-0.5f:0.5f-a[i];output[i]=(byte)Math.Clamp((int)MathF.Round((0.75f+dist/spread*0.25f)*255),0,255);}return output;
    }
}
