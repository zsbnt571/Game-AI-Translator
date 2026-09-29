using System.Drawing;
using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;

namespace ScreenshotTranslationUiTester;

internal static class MaterialRecoverySelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        var rows=new List<object>();var failed=0;
        static int Count(bool[,] a){var n=0;foreach(var set in a)if(set)n++;return n;}
        static void Fill(bool[,] a,Rectangle r)
        {for(var y=r.Top;y<r.Bottom;y++)for(var x=r.Left;x<r.Right;x++)a[x,y]=true;}
        static bool Same(bool[,] a,bool[,] b)
        {for(var y=0;y<a.GetLength(1);y++)for(var x=0;x<a.GetLength(0);x++)if(a[x,y]!=b[x,y])return false;return true;}
        static void Require(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        static bool[,] Glyphs(int width=120,int height=40)
        {
            var a=new bool[width,height];
            for(var i=0;i<4;i++)
            {
                var x=7+i*25;
                Fill(a,new(x,8,3,24));Fill(a,new(x+12,8,3,24));Fill(a,new(x,18,15,3));
            }
            return a;
        }
        void Test(string name,Action test)
        {
            try{test();rows.Add(new{Name=name,Pass=true});}
            catch(Exception ex){failed++;rows.Add(new{Name=name,Pass=false,Error=ex.Message});}
        }
        Test("No secondary additions preserve independently valid glyphs",()=>
        {
            var primary=Glyphs();var g=new Rectangle(0,0,120,40);
            var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(primary,g);
            Require(Count(validated)>0,"control glyphs rejected");var seed=(bool[,])validated.Clone();
            var result=CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(seed,g,validated);
            Require(Same(seed,validated)&&result.ReintroducedValidatedPrimaryPixels==0,"unchanged input changed");
        });
        Test("Secondary bridge rollback recovers validated glyphs and rejects panel fill",()=>
        {
            var primary=Glyphs();var g=new Rectangle(0,0,120,40);
            var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(primary,g);
            var seed=(bool[,])primary.Clone();Fill(seed,new(4,5,108,31));
            var ordinary=(bool[,])seed.Clone();CorePipelineCorpusRunner.RejectContainerFillComponents(ordinary,g);
            Require(Count(ordinary)==0,"positive counterexample did not trigger ordinary rejection");
            var result=CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(seed,g,validated);
            Require(Same(seed,validated)&&result.FinalRecoveredPrimaryPixels==Count(validated),"primary not recovered exactly");
            Require(!seed[4,5]&&!seed[110,34],"secondary panel pixels survived");
        });
        Test("Actual large container primary receives no unconditional protection",()=>
        {
            var primary=new bool[120,40];Fill(primary,new(2,2,115,35));var g=new Rectangle(0,0,120,40);
            var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(primary,g);
            Require(Count(validated)==0,"container validated as primary");
            CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(primary,g,validated);
            Require(Count(primary)==0,"container restored");
        });
        Test("Shelf and long horizontal rules stay rejected",()=>
        {
            var primary=new bool[160,40];Fill(primary,new(1,19,155,3));var g=new Rectangle(0,0,160,40);
            var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(primary,g);
            Require(Count(validated)==0,"shelf line validated");
            var seed=(bool[,])validated.Clone();
            CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(seed,g,validated);
            Require(Count(seed)==0,"shelf recovered");
        });
        Test("Independent secondary glyph components are retained",()=>
        {
            var primary=Glyphs();var g=new Rectangle(0,0,120,40);
            var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(primary,g);
            var seed=(bool[,])validated.Clone();Fill(seed,new(113,9,2,20));var expected=(bool[,])seed.Clone();
            var result=CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(seed,g,validated);
            Require(Same(seed,expected)&&result.OrdinaryRejectedPixels==0,"valid second-color component lost");
        });
        Test("Outline and shadow sized additions do not force rollback",()=>
        {
            var primary=Glyphs();var g=new Rectangle(0,0,120,40);
            var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(primary,g);
            var seed=(bool[,])validated.Clone();Fill(seed,new(4,8,1,24));Fill(seed,new(112,8,1,24));
            var expected=(bool[,])seed.Clone();CorePipelineCorpusRunner.RejectContainerFillComponents(expected,g);
            CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(seed,g,validated);
            Require(Same(seed,expected),"independent edge material changed");
        });
        Test("Rejected earlier structure cannot be resurrected from primary snapshot",()=>
        {
            var primary=Glyphs();var g=new Rectangle(0,0,120,40);
            var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(primary,g);
            var seed=(bool[,])validated.Clone();seed[7,8]=false;
            CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(seed,g,validated);
            Require(!seed[7,8],"earlier rejected pixel was resurrected");
        });
        Test("Local primary coordinates survive an offset global mask",()=>
        {
            var local=Glyphs();var global=new bool[190,95];var g=new Rectangle(31,27,120,40);
            for(var y=0;y<40;y++)for(var x=0;x<120;x++)global[g.X+x,g.Y+y]=local[x,y];
            var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(global,g);
            Fill(global,new(g.X+4,g.Y+5,108,31));
            CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(global,g,validated);
            for(var y=0;y<95;y++)for(var x=0;x<190;x++)
                Require(global[x,y]==(g.Contains(x,y)&&validated[x-g.X,y-g.Y]),"coordinate origin mismatch");
        });
        Test("Dense short-word components keep the ordinary structural decision",()=>
        {
            var primary=new bool[42,30];
            Fill(primary,new(3,3,3,24));Fill(primary,new(15,3,3,24));Fill(primary,new(3,13,15,3));
            Fill(primary,new(24,3,3,24));Fill(primary,new(36,3,3,24));Fill(primary,new(24,13,15,3));
            var g=new Rectangle(0,0,42,30);var validated=CorePipelineCorpusRunner.ValidatePrimarySeedForSecondaryRecovery(primary,g);
            var expected=(bool[,])validated.Clone();
            CorePipelineCorpusRunner.RejectContainerFillAfterSecondary(primary,g,validated);
            Require(Same(primary,expected),"short-word structure changed without secondary evidence");
        });
        File.WriteAllText(Path.Combine(output,"MATERIAL-RECOVERY-SELFTESTS.json"),JsonSerializer.Serialize(new
        {
            Pass=rows.Count-failed,Fail=failed,Rows=rows,RealApiCalls=0,
            Scope="Synthetic geometry contracts for actual product validation/recovery methods; real color/AA behavior is covered separately by source-image replay and visual inspection"
        },new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:1;
    }
}
