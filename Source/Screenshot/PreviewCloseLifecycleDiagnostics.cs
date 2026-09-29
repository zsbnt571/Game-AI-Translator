namespace ScreenshotTranslationUiTester;

internal static class PreviewCloseLifecycleDiagnostics
{
    internal static int Run(string output,int iterations)
    {
        Directory.CreateDirectory(output);
        var timeline=Path.Combine(output,"preview-close-timeline.log");
        var results=new List<string>();
        void Write(string message)=>File.AppendAllText(timeline,$"{DateTimeOffset.Now:O} thread={Environment.CurrentManagedThreadId} {message}{Environment.NewLine}");
        for(var i=1;i<=iterations;i++)
        {
            var config=Path.Combine(output,$"settings-{i}.json");
            ConfigurationManager.Save(config,new ApiSettings{VisualModel=VisualModelKind.Off});
            Write($"ITERATION {i} MainForm constructor ENTER");
            using var main=new MainForm(config);
            Write($"ITERATION {i} MainForm constructor EXIT");
            main.Show();Application.DoEvents();
            using var image=new Bitmap(180,90);
            Write($"ITERATION {i} smoke assertion ENTER");
            var preview=main.OpenPreviewForSmoke(image);
            Write($"ITERATION {i} Preview.Close CALL");
            preview.Close();
            Write($"ITERATION {i} Preview.Close RETURN");
            foreach(var name in MainForm.MainPageNamesForSmoke)
                if(!main.NavigateForSmoke(name))throw new InvalidOperationException($"after preview blank {name}");
            Write($"ITERATION {i} smoke assertion EXIT");
            Write($"ITERATION {i} MainForm Dispose CALL");
            main.Dispose();
            Write($"ITERATION {i} MainForm Dispose RETURN");
            results.Add($"PASS iteration {i}");
        }
        File.WriteAllLines(Path.Combine(output,"preview-close-results.txt"),results.Append($"TOTAL={iterations} PASS={iterations} FAIL=0"));
        return 0;
    }
}
