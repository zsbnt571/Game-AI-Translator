using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ScreenshotTranslationUiTester.CorePipelineV2;

internal static partial class CorePipelineCorpusRunner
{
    // These snapshots never participate in material admission or reconstruction.
    private sealed record MaterialLineStages(string SourceId, Rectangle Bounds,
        bool[,] PrimaryBeforeRejection, bool[,] PrimaryAfterFirstRejection,
        bool[,] BeforeSecondary, bool[,] AfterSecondary, bool[,] AfterFinalRejection);

    private static bool CaptureDetailedRestorationFields(string output)
    {
        var selection=Environment.GetEnvironmentVariable("ST_RESTORATION_STAGE_FIELDS");
        if(string.IsNullOrWhiteSpace(selection))return false;
        var fixture=Path.GetFileName(output.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar));
        return selection.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Contains(fixture,StringComparer.OrdinalIgnoreCase);
    }

    private static bool[,] CopyMaskRegion(bool[,] source,Rectangle bounds)
    {
        var result=new bool[bounds.Width,bounds.Height];
        for(var y=0;y<bounds.Height;y++)for(var x=0;x<bounds.Width;x++)
            result[x,y]=source[bounds.Left+x,bounds.Top+y];
        return result;
    }

    private sealed record RestorationFieldOutput(Rectangle Roi,string Root);

    private sealed class RestorationStageCapture : IDisposable
    {
        private readonly Rectangle _roi;
        private readonly string _root;
        private readonly Bitmap _before;
        private readonly Bitmap _candidates;
        private readonly Bitmap _valid;
        private readonly Bitmap _weightsPreview;
        private readonly float[] _weights;
        private int _candidateSamples;
        private string _donorMeaning="NOT_CAPTURED_FOR_THIS_ROUTE";

        internal RestorationStageCapture(string output,Bitmap source,Bitmap before,VisualBlock block,
            GlyphMaskResult glyph,bool[,] authority,bool[,] materialClosure,Rectangle authorityBounds)
        {
            _roi=Clamp(Rectangle.Inflate(Rectangle.Union(Rectangle.Round(block.Bounds),authorityBounds),12,12),source.Size);
            _root=Path.Combine(output,"RESTORATION-FIELDS",SafeName(block.BlockId));
            Directory.CreateDirectory(_root);
            _before=before.Clone(_roi,PixelFormat.Format32bppArgb);
            _candidates=new Bitmap(_roi.Width,_roi.Height,PixelFormat.Format32bppArgb);
            _valid=new Bitmap(_roi.Width,_roi.Height,PixelFormat.Format32bppArgb);
            _weightsPreview=new Bitmap(_roi.Width,_roi.Height,PixelFormat.Format32bppArgb);
            _weights=new float[_roi.Width*_roi.Height];
            SaveBitmapCrop(source,_roi,Path.Combine(_root,"SOURCE.png"));
            _before.Save(Path.Combine(_root,"PRE-RESTORATION.png"),ImageFormat.Png);
            SaveMaskCrop(authority,_roi,Path.Combine(_root,"RUNTIME-AUTHORITY-BEFORE.png"),Color.White);
            SaveMaskCrop(materialClosure,_roi,Path.Combine(_root,"MATERIAL-CLOSURE.png"),Color.White);
            SaveMaskCrop(glyph.AfterComponentMerge,_roi,Path.Combine(_root,"AFTER-COMPONENT-MERGE.png"),Color.White);
            var lineRows=new List<object>();
            foreach(var line in glyph.MaterialStages)
            {
                var lineRoot=Path.Combine(_root,"MATERIAL-LINES",SafeName(line.SourceId));
                Directory.CreateDirectory(lineRoot);
                SaveBinaryMap(line.PrimaryBeforeRejection,Path.Combine(lineRoot,"01-primary-before-rejection.png"));
                SaveBinaryMap(line.PrimaryAfterFirstRejection,Path.Combine(lineRoot,"02-primary-after-first-rejection.png"));
                SaveBinaryMap(line.BeforeSecondary,Path.Combine(lineRoot,"03-before-secondary.png"));
                SaveBinaryMap(line.AfterSecondary,Path.Combine(lineRoot,"04-after-secondary.png"));
                SaveBinaryMap(SubtractMask(line.AfterSecondary,line.BeforeSecondary),Path.Combine(lineRoot,"05-secondary-additions.png"));
                SaveBinaryMap(line.AfterFinalRejection,Path.Combine(lineRoot,"06-after-final-rejection.png"));
                var local=new Rectangle(0,0,line.Bounds.Width,line.Bounds.Height);
                lineRows.Add(new {line.SourceId,OriginX=line.Bounds.X,OriginY=line.Bounds.Y,
                    Width=line.Bounds.Width,Height=line.Bounds.Height,
                    PrimaryBeforeRejection=CountMaskPixels(line.PrimaryBeforeRejection,local),
                    PrimaryAfterFirstRejection=CountMaskPixels(line.PrimaryAfterFirstRejection,local),
                    BeforeSecondary=CountMaskPixels(line.BeforeSecondary,local),
                    AfterSecondary=CountMaskPixels(line.AfterSecondary,local),
                    SecondaryAdditions=CountMaskPixels(SubtractMask(line.AfterSecondary,line.BeforeSecondary),local),
                    AfterFinalRejection=CountMaskPixels(line.AfterFinalRejection,local)});
            }
            File.WriteAllText(Path.Combine(_root,"MATERIAL-STAGES.json"),JsonSerializer.Serialize(new
            {
                SchemaVersion=2,block.BlockId,CoordinateSpace="SOURCE_PIXELS; local masks use each line OriginX/OriginY",
                Unit="unique true pixels within each line snapshot; sums across overlapping lines are not full-frame union counts",
                Stages=lineRows
            },JsonOptions));
        }

        internal void DonorExclusion(bool[,] exclusion,Point origin,bool outsideExcluded,string meaning)
        {
            _donorMeaning=meaning;
            using var image=new Bitmap(_roi.Width,_roi.Height,PixelFormat.Format32bppArgb);
            for(var y=0;y<_roi.Height;y++)for(var x=0;x<_roi.Width;x++)
            {
                var sx=_roi.Left+x-origin.X;var sy=_roi.Top+y-origin.Y;
                var excluded=sx<0||sy<0||sx>=exclusion.GetLength(0)||sy>=exclusion.GetLength(1)
                    ?outsideExcluded:exclusion[sx,sy];
                image.SetPixel(x,y,excluded?Color.White:Color.Black);
            }
            image.Save(Path.Combine(_root,"DONOR-EXCLUSION.png"),ImageFormat.Png);
        }

        internal void Candidate(int x,int y,Color candidate,float weight)
        {
            if(!_roi.Contains(x,y))throw new InvalidOperationException("Diagnostic ROI excludes an actual candidate sample");
            x-=_roi.Left;y-=_roi.Top;
            _candidates.SetPixel(x,y,Color.FromArgb(255,candidate));
            _valid.SetPixel(x,y,Color.White);
            _weights[y*_roi.Width+x]=weight;
            var level=Math.Clamp((int)MathF.Round(weight*255),0,255);
            _weightsPreview.SetPixel(x,y,Color.FromArgb(level,level,level));
            _candidateSamples++;
        }

        internal RestorationFieldOutput Finish(Bitmap restored,string mode)
        {
            _candidates.Save(Path.Combine(_root,"RECONSTRUCTION-CANDIDATE-PRE-BLEND.png"),ImageFormat.Png);
            _valid.Save(Path.Combine(_root,"CANDIDATE-VALID.png"),ImageFormat.Png);
            _weightsPreview.Save(Path.Combine(_root,"BLEND-WEIGHT.png"),ImageFormat.Png);
            using(var file=File.Create(Path.Combine(_root,"BLEND-WEIGHT.float32")))
            using(var writer=new BinaryWriter(file))foreach(var weight in _weights)writer.Write(weight);
            SaveBitmapCrop(restored,_roi,Path.Combine(_root,"POST-BLEND-PRE-TEXT.png"));
            using var changes=new Bitmap(_roi.Width,_roi.Height,PixelFormat.Format32bppArgb);
            var changed=0;
            for(var y=0;y<_roi.Height;y++)for(var x=0;x<_roi.Width;x++)
            {
                var different=_before.GetPixel(x,y).ToArgb()!=restored.GetPixel(_roi.Left+x,_roi.Top+y).ToArgb();
                changes.SetPixel(x,y,different?Color.White:Color.Black);
                if(different)changed++;
            }
            changes.Save(Path.Combine(_root,"ACTUAL-RESTORED-PIXELS.png"),ImageFormat.Png);
            File.WriteAllText(Path.Combine(_root,"FIELD-METADATA.json"),JsonSerializer.Serialize(new
            {
                SchemaVersion=2,CoordinateSpace="SOURCE_PIXELS",OriginX=_roi.X,OriginY=_roi.Y,
                Width=_roi.Width,Height=_roi.Height,RestorationMode=mode,
                CandidateMeaning="Diagnostic field of the exact candidate value evaluated at each CANDIDATE-VALID pixel, not a pre-existing complete intermediate image",
                CandidateSamples=_candidateSamples,CandidateCoverage=_candidateSamples>0?"CAPTURED_AT_INSTRUMENTED_ROUTE":"NOT_CAPTURED_FOR_THIS_ROUTE",
                BlendWeights="BLEND-WEIGHT.float32: little-endian IEEE754 float32, row-major ROI; use CANDIDATE-VALID. PNG is rounded weight*255 preview",
                AuthorityMeaning="Copied from the real runtime authority before restoration; never inferred from output differences",
                DonorExclusionMeaning=_donorMeaning,ActualChangedPixels=changed,
                ChangedPixelMeaning="Per-block before/after difference, unique within this ROI; overlapping block totals are not a global union"
            },JsonOptions));
            return new(_roi,_root);
        }

        public void Dispose()
        {
            _before.Dispose();_candidates.Dispose();_valid.Dispose();_weightsPreview.Dispose();
        }
    }

    private static void WriteFullFrameAuthorityAudit(string output,Bitmap before,Bitmap after,bool[,] authority)
    {
        long authorized=0,changed=0,outside=0;
        for(var y=0;y<before.Height;y++)for(var x=0;x<before.Width;x++)
        {
            if(authority[x,y])authorized++;
            if(before.GetPixel(x,y).ToArgb()==after.GetPixel(x,y).ToArgb())continue;
            changed++;if(!authority[x,y])outside++;
        }
        File.WriteAllText(Path.Combine(output,"FULL-FRAME-AUTHORITY-AUDIT.json"),JsonSerializer.Serialize(new
        {
            SchemaVersion=2,CoordinateSpace="SOURCE_PIXELS",OriginX=0,OriginY=0,before.Width,before.Height,
            CanvasPixels=(long)before.Width*before.Height,UniqueRuntimeAuthorityPixels=authorized,
            UniqueRestorationChangedPixels=changed,ChangedOutsideRuntimeAuthorityPixels=outside,
            Scope="Whole canvas, pre-restoration versus post-restoration before translated text",
            AuthoritySource="Union of each runtime block authority captured before restoration, not reverse-derived from changed pixels",
            Units="unique pixels; block totals may overlap and are not interchangeable with these union counts"
        },JsonOptions));
    }
}
