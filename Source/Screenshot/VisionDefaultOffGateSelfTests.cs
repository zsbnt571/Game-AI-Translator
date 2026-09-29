using System.Text;

namespace ScreenshotTranslationUiTester;

internal static class VisionDefaultOffGateSelfTests
{
    internal static int Run(string output,string sourcePath)
    {
        Directory.CreateDirectory(output);
        var rows=new List<string[]>();var failures=new List<string>();
        void Check(string name,bool pass,string detail)
        {
            rows.Add([name,pass?"PASS":"FAIL",detail,"0"]);
            if(!pass)failures.Add($"{name}: {detail}");
        }

        Check("ApiSettings fresh default",new ApiSettings().VisualModel==VisualModelKind.Off,
            $"VisualModel={new ApiSettings().VisualModel}");
        var missing=Path.Combine(output,"missing-settings.json");
        var loaded=ConfigurationManager.Load(missing,false);
        Check("Missing settings default",loaded.VisualModel==VisualModelKind.Off,$"VisualModel={loaded.VisualModel}");
        var config=Path.Combine(output,"main-settings.json");
        ConfigurationManager.Save(config,new ApiSettings{VisualModel=VisualModelKind.PPDocLayoutS});
        using(var main=new MainForm(config))
            Check("Normal MainForm gate",main.CurrentSettings.VisualModel==VisualModelKind.Off,
                $"Loaded=PPDocLayoutS NormalCurrent={main.CurrentSettings.VisualModel}");

        using var source=new Bitmap(sourcePath);
        var vision=new VisionRuntimeManager(AppContext.BaseDirectory);
        try
        {
            var first=vision.AnalyzeAsync(source,VisualModelKind.Off,1,CancellationToken.None).GetAwaiter().GetResult();
            var firstPass=first.VisualRegions.Count==0&&first.ModelDiagnostics.Model=="Off"&&vision.RequestCount==0&&vision.WorkerStartCount==0;
            rows.Add(["OFF-A",firstPass?"PASS":"FAIL",
                $"Model={first.ModelDiagnostics.Model};Regions={first.VisualRegions.Count};VisionRequests={vision.RequestCount};WorkerStarts={vision.WorkerStartCount}","0"]);
            if(!firstPass)failures.Add("OFF-A started or retained Vision");

            var repeated=vision.AnalyzeAsync(source,VisualModelKind.Off,2,CancellationToken.None).GetAwaiter().GetResult();
            var repeatedPass=repeated.VisualRegions.Count==0&&repeated.ModelDiagnostics.Model=="Off"&&vision.RequestCount==0&&vision.WorkerStartCount==0;
            rows.Add(["OFF-B-REPEAT",repeatedPass?"PASS":"FAIL",
                $"Model={repeated.ModelDiagnostics.Model};Regions={repeated.VisualRegions.Count};VisionRequests={vision.RequestCount};WorkerStarts={vision.WorkerStartCount}","0"]);
            if(!repeatedPass)failures.Add("OFF-B repeat started or retained Vision");
        }
        finally
        {
            vision.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        WriteCsv(Path.Combine(output,"session-transition-stress.csv"),
            ["Scenario","Status","Evidence","RealApiCalls"],rows);
        File.WriteAllText(Path.Combine(output,"vision-default-off-gate.txt"),
            $"Status={(failures.Count==0?"PASS":"FAIL")}{Environment.NewLine}Failures={string.Join(" | ",failures)}{Environment.NewLine}RealApiCalls=0",
            new UTF8Encoding(false));
        return failures.Count==0?0:9;
    }

    private static void WriteCsv(string path,string[] header,IEnumerable<string[]> rows)
    {
        static string Q(string value)=>'"'+value.Replace("\"","\"\"")+'"';
        File.WriteAllLines(path,new[]{string.Join(',',header.Select(Q))}
            .Concat(rows.Select(row=>string.Join(',',row.Select(Q)))),new UTF8Encoding(false));
    }
}
