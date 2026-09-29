internal static class IsolationTests
{
    internal static void Run(Action<bool,string> check)
    {
        var before=Environment.GetEnvironmentVariable("FUSION_TEST_GAME_ROOT");
        var root=Path.Combine(AppContext.BaseDirectory,"isolation-fixture-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        bool Rejects(string path){try{IsolatedGamePath.Require(path);return false;}catch(InvalidOperationException){return true;}}
        try
        {
            Environment.SetEnvironmentVariable("FUSION_TEST_GAME_ROOT",null);
            check(Rejects(Path.Combine(root,"game")),"isolation requires an explicit root");
            Environment.SetEnvironmentVariable("FUSION_TEST_GAME_ROOT","relative-copy");
            check(Rejects(Path.Combine(root,"game")),"isolation rejects a relative root");
            Environment.SetEnvironmentVariable("FUSION_TEST_GAME_ROOT",Path.GetPathRoot(root));
            check(Rejects(Path.Combine(root,"game")),"isolation rejects an entire volume");
            Environment.SetEnvironmentVariable("FUSION_TEST_GAME_ROOT",root);
            check(IsolatedGamePath.Require(Path.Combine(root,"game","sample.exe"))==Path.Combine(root,"game","sample.exe"),"isolation accepts only children of the registered root");
            check(Rejects(root)&&Rejects(root+"-outside\\sample.exe")&&Rejects(Path.Combine(root,"..","outside.exe")),"isolation rejects root itself, prefix siblings and traversal");
        }
        finally
        {
            Environment.SetEnvironmentVariable("FUSION_TEST_GAME_ROOT",before);
            // This fixture creates no files and never operates on any game directory.
            Directory.Delete(root,false);
        }
    }
}
