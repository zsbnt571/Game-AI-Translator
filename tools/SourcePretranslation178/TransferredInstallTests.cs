using System.Text.Json.Nodes;
using ScreenshotTranslationUiTester;

static class TransferredInstallTests
{
    internal static void Run(string root,Action<string,string,bool,object?> check)
    {
        string Fixture(string name,bool own=true)
        {
            string folder=Path.Combine(root,name);Directory.CreateDirectory(folder);
            Directory.CreateDirectory(Path.Combine(folder,"js"));Directory.CreateDirectory(Path.Combine(folder,"data"));Directory.CreateDirectory(Path.Combine(folder,"save"));
            File.WriteAllText(Path.Combine(folder,"Game.exe"),"Non executable fixture");
            File.WriteAllText(Path.Combine(folder,"js","rmmz_core.js"),"// Not executed");
            File.WriteAllText(Path.Combine(folder,"data","System.json"),"{\"locale\":\"ja_JP\"}");
            File.WriteAllText(Path.Combine(folder,"index.html"),"<title>Fixture</title>");
            File.WriteAllText(Path.Combine(folder,"save","file1.rmmzsave"),"User save must survive");
            File.WriteAllText(Path.Combine(folder,"package.json"),"{\"main\":\"index.html\",\"window\":{\"width\":816},\"inject_js_end\":\".fusion-rpg-data.js\",\"inject-js-end\":\".fusion-rpg-data.js\"}");
            File.WriteAllText(Path.Combine(folder,RpgMakerDataAdapter.ScriptName),own?RpgMakerDataAdapter.Payload():"Unknown same-named component");
            return Path.Combine(folder,"Game.exe");
        }
        foreach(bool installFirst in new[]{false,true})
        {
            string id=installFirst?"transferred-adopt-install":"transferred-restore",exe=Fixture(id),folder=Path.GetDirectoryName(exe)!;
            string before=File.ReadAllText(Path.Combine(folder,"package.json"));
            if(installFirst)
            {
                RpgMakerDataAdapter.Install(exe);
                check(id,"Transferred exact component becomes a verified local installation",RpgMakerDataAdapter.Installed(exe),null);
                check(id,"Adoption does not rewrite game startup settings",File.ReadAllText(Path.Combine(folder,"package.json"))==before,null);
            }
            RpgMakerDataAdapter.Restore(exe);
            var restored=JsonNode.Parse(File.ReadAllText(Path.Combine(folder,"package.json")))!.AsObject();
            check(id,"Restore removes both owned startup entries",!restored.ContainsKey("inject_js_end")&&!restored.ContainsKey("inject-js-end"),null);
            check(id,"Restore removes only verified owned component",!File.Exists(Path.Combine(folder,RpgMakerDataAdapter.ScriptName)),null);
            check(id,"Original unrelated settings survive",restored["window"]!["width"]!.GetValue<int>()==816&&restored["main"]!.GetValue<string>()=="index.html",null);
            check(id,"Game data and save survive",File.ReadAllText(Path.Combine(folder,"save","file1.rmmzsave"))=="User save must survive"&&File.ReadAllText(Path.Combine(folder,"data","System.json"))=="{\"locale\":\"ja_JP\"}",null);
            check(id,"Transferred file backup retained",Directory.EnumerateDirectories(RpgMakerDataAdapter.Storage(exe),"transferred-*").Any(d=>File.ReadAllText(Path.Combine(d,"package.json"))==before&&File.ReadAllText(Path.Combine(d,RpgMakerDataAdapter.ScriptName))==RpgMakerDataAdapter.Payload()),null);
            RpgMakerDataAdapter.Install(exe);check(id,"Clean reinstall works after recovery",RpgMakerDataAdapter.Installed(exe),null);
            RpgMakerDataAdapter.Restore(exe);check(id,"Second restore also works",!File.Exists(Path.Combine(folder,RpgMakerDataAdapter.ScriptName)),null);
        }
        foreach(string scenario in new[]{"foreign","changed-startup","dangling-startup"})
        {
            string exe=Fixture(scenario,scenario!="foreign"),folder=Path.GetDirectoryName(exe)!,manifest=Path.Combine(folder,"package.json"),component=Path.Combine(folder,RpgMakerDataAdapter.ScriptName);
            if(scenario=="changed-startup")File.WriteAllText(manifest,File.ReadAllText(manifest).Replace("\"inject-js-end\":\".fusion-rpg-data.js\"","\"inject-js-end\":\"other-mod.js\""));
            if(scenario=="dangling-startup")File.Delete(component);
            string before=File.ReadAllText(manifest),script=File.Exists(component)?File.ReadAllText(component):"";bool refused=false;
            try{RpgMakerDataAdapter.Restore(exe);}catch(Exception ex)when(ex is IOException or InvalidOperationException){refused=true;}
            check(scenario,"Unverifiable state reports failure rather than silent success",refused,null);
            check(scenario,"Unverifiable game files remain unchanged",File.ReadAllText(manifest)==before&&(File.Exists(component)?File.ReadAllText(component):"")==script,null);
        }
        foreach(string scenario in new[]{"legacy-moved","legacy-restore","legacy-corrupt-backup","legacy-wrong-record","legacy-changed-manifest"})
        {
            string old=Fixture(scenario+"-source"),oldFolder=Path.GetDirectoryName(old)!;
            RpgMakerDataAdapter.Restore(old);RpgMakerDataAdapter.Install(old);
            string oldManifest=Path.Combine(oldFolder,"package.json"),oldScript=Path.Combine(oldFolder,RpgMakerDataAdapter.ScriptName);
            // Simulate a previously journalled payload, never execute fixture JS.
            File.AppendAllText(oldScript,"\n// Prior journalled build fixture: "+scenario);
            string recordPath=Path.Combine(RpgMakerDataAdapter.Storage(old),"install.json");
            var record=JsonNode.Parse(File.ReadAllText(recordPath))!.AsObject();record["ScriptHash"]=RpgMakerDataAdapter.Hash(File.ReadAllBytes(oldScript));
            if(scenario=="legacy-wrong-record")record["Exe"]=Path.Combine(oldFolder,"Different.exe");
            File.WriteAllText(recordPath,record.ToJsonString());
            string original=File.ReadAllText(Path.Combine(RpgMakerDataAdapter.Storage(old),"package.original.json"));
            if(scenario=="legacy-corrupt-backup")File.AppendAllText(Path.Combine(RpgMakerDataAdapter.Storage(old),"package.original.json"),"changed");
            string moved=Fixture(scenario+"-destination"),folder=Path.GetDirectoryName(moved)!,manifest=Path.Combine(folder,"package.json"),script=Path.Combine(folder,RpgMakerDataAdapter.ScriptName);
            File.Copy(oldManifest,manifest,true);File.Copy(oldScript,script,true);
            if(scenario=="legacy-changed-manifest")File.AppendAllText(manifest," ");
            string before=File.ReadAllText(manifest),payload=File.ReadAllText(script);bool failed=false;
            try{if(scenario=="legacy-restore")RpgMakerDataAdapter.Restore(moved);else RpgMakerDataAdapter.Install(moved);}catch(Exception ex)when(ex is IOException or InvalidOperationException){failed=true;}
            bool accepted=scenario is "legacy-moved" or "legacy-restore";
            check(scenario,"Only a complete matching local journal authorizes legacy recovery",failed!=accepted,null);
            if(accepted)
            {
                if(scenario=="legacy-moved")check(scenario,"Moved legacy component upgraded to current payload",File.ReadAllText(script)==RpgMakerDataAdapter.Payload()&&RpgMakerDataAdapter.Installed(moved),null);
                RpgMakerDataAdapter.Restore(moved);
                check(scenario,"Verified original bytes restored",File.ReadAllText(manifest)==original&&!File.Exists(script),null);
                check(scenario,"Source installation and destination save retained",File.ReadAllText(oldManifest)==before&&File.ReadAllText(oldScript)==payload&&File.Exists(recordPath)&&File.ReadAllText(Path.Combine(folder,"save","file1.rmmzsave"))=="User save must survive",null);
            }
            else check(scenario,"Rejected legacy recovery leaves game untouched",File.ReadAllText(manifest)==before&&File.ReadAllText(script)==payload&&!RpgMakerDataAdapter.Installed(moved),null);
        }
    }
}
