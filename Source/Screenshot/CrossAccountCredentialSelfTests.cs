using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Opt-in, synthetic-only regression checks. No original credentials, network,
// model/harness process, game, or real user configuration is used by this test.
internal static class CrossAccountCredentialSelfTests
{
    internal static int Run(string output)
    {
        output = Path.GetFullPath(output);
        if (!AppDataPaths.HasExplicitRoot || !Path.GetFullPath(AppDataPaths.Root).StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Synthetic tests require an explicit data root inside their output directory.");
        Directory.CreateDirectory(output);
        using var marker = new FileStream(Path.Combine(output, "synthetic-test.started"), FileMode.CreateNew, FileAccess.Write);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        void Reject(Action action, string name)
        {
            bool rejected = false; try { action(); } catch { rejected = true; }
            Check(rejected, name);
        }
        const string badFormat = "dpapi:v1:not-valid-base64!";
        string invalidBlob = "dpapi:v1:" + Convert.ToBase64String(new byte[48]);
        string freshKey = "synthetic-local-key-" + Guid.NewGuid().ToString("N");
        string originalCipher = FusionSecrets.Protect(freshKey);
        string store = Path.Combine(output, "independent.json");
        try
        {
            Check(BuildIdentity.VersionLabel == "1.1.165", "Version label identifies current release");
            Check(BuildIdentity.DisplayVersion == "Fusion R1 · 1.1.165", "Displayed product version identifies current release");
            Reject(() => FusionSecrets.Unprotect(badFormat), "Strict decoder still rejects invalid format");
            Reject(() => FusionSecrets.Unprotect(invalidBlob), "Strict decoder still rejects invalid DPAPI blob");
            var settings = FusionSecrets.Deserialize<ApiSettings>(JsonSerializer.Serialize(new ApiSettings { ApiKey=badFormat, Model="preserved-model" }), store);
            Check(settings.ApiKey == "" && settings.Model == "preserved-model", "Safe decoder preserves nonsecret fields");
            Check(UnavailableCredentials.IsUnavailable(store, "/ApiKey"), "Unavailable key is explicitly marked");
            Check(!settings.AllowEmptyApiKey, "Empty-key authorization unchanged");
            var saved = JsonNode.Parse(FusionSecrets.Serialize(settings, store))!;
            Check(saved["ApiKey"]!.GetValue<string>() == badFormat, "Ordinary save preserves original unavailable ciphertext");
            settings.ApiKey = freshKey;
            string encrypted = FusionSecrets.Serialize(settings, store);
            Check(UnavailableCredentials.IsUnavailable(store, "/ApiKey"), "Serialization alone does not commit replacement");
            Check(FusionSecrets.Deserialize<ApiSettings>(encrypted, store).ApiKey == freshKey, "New current-user synthetic key round-trips");
            Check(!UnavailableCredentials.IsUnavailable(store, "/ApiKey"), "Successful reload clears stale marker");
            var secondStore = Path.Combine(output, "independent-second.json");
            _ = FusionSecrets.Deserialize<ApiSettings>(JsonSerializer.Serialize(new ApiSettings { ApiKey=invalidBlob }), store);
            var unrelated = FusionSecrets.Deserialize<ApiSettings>(JsonSerializer.Serialize(new ApiSettings()), secondStore);
            Check(JsonNode.Parse(FusionSecrets.Serialize(unrelated, secondStore))!["ApiKey"]!.GetValue<string>() == "", "Different storage paths do not share recovery state");
            Reject(() => FusionSecrets.Deserialize<FusionState>("{\"Profiles\":42}", store), "Structural JSON errors remain errors");
            Check(UnavailableCredentials.IsUnavailable(store, "/ApiKey"), "Failed structural load does not erase previous recovery state");
            var migratedPath = Path.Combine(output, "migrated.json");
            UnavailableCredentials.CopyStore(store, migratedPath);
            Check(JsonNode.Parse(FusionSecrets.Serialize(new ApiSettings {ApiKey=""}, migratedPath))!["ApiKey"]!.GetValue<string>() == invalidBlob, "Legacy path transfer preserves unavailable ciphertext");
            Check(UnavailableCredentials.IsUnavailable(store, "/ApiKey"), "Copying legacy provenance does not mutate source recovery state");

            string profileOne = Guid.NewGuid().ToString("N"), profileTwo = Guid.NewGuid().ToString("N");
            var state = new FusionState
            {
                SchemaVersion=3, CommonProfile=profileOne,
                Profiles=new() { [profileOne]=new ApiSettings {ApiKey=badFormat,ApiUrl="http://127.0.0.1:9/v1",Model="fixture-one"}, [profileTwo]=new ApiSettings {ApiKey=invalidBlob,ApiUrl="http://127.0.0.1:9/v1",Model="fixture-two"} },
                ProfileInfo=new() { [profileOne]=new(){Name="Synthetic one"}, [profileTwo]=new(){Name="Synthetic two"} },
                GameProfiles=new() { ["synthetic-game.exe"]=profileTwo },
                TranslationContent=TranslationContentSettings.From(new ApiSettings())
            };
            string fusionPath = Path.Combine(AppDataPaths.Root, "fusion-settings.json");
            File.WriteAllText(fusionPath, JsonSerializer.Serialize(state));
            var configuration = new FusionConfiguration();
            Check(configuration.State.Profiles.Count == 2 && configuration.State.GameProfiles["synthetic-game.exe"] == profileTwo, "Actual configuration loads both profiles and game association");
            Reject(() => configuration.RequireReady(false), "Translation operation still rejects missing key");
            configuration.Change(next => next.ProfileInfo[profileOne].Name = "Renamed synthetic one");
            var afterRename = JsonNode.Parse(File.ReadAllText(fusionPath))!;
            Check(afterRename["Profiles"]![profileOne]!["ApiKey"]!.GetValue<string>() == badFormat && afterRename["Profiles"]![profileTwo]!["ApiKey"]!.GetValue<string>() == invalidBlob, "Profile rename and atomic save retain both unavailable ciphertexts");
            var beforeFailedSave = File.ReadAllBytes(fusionPath);
            using (var held = new FileStream(fusionPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Reject(() => configuration.Change(next => next.Profiles[profileOne].ApiKey=freshKey), "Locked target rejects atomic profile replacement");
            Check(File.ReadAllBytes(fusionPath).SequenceEqual(beforeFailedSave), "Failed profile replacement leaves original file byte-identical");
            configuration.Change(next => next.ProfileInfo[profileTwo].Name="Another safe rename");
            Check(JsonNode.Parse(File.ReadAllText(fusionPath))!["Profiles"]![profileOne]!["ApiKey"]!.GetValue<string>() == badFormat, "Save after failed replacement still retains unavailable ciphertext");
            var replacement = ApiSettingsSnapshot.Copy(configuration.State.Profiles[profileOne]); replacement.ApiKey=freshKey;
            configuration.SaveProfile(profileOne, "Renamed synthetic one", replacement);
            var afterReplacement = JsonNode.Parse(File.ReadAllText(fusionPath))!;
            Check(afterReplacement["Profiles"]![profileTwo]!["ApiKey"]!.GetValue<string>() == invalidBlob, "Replacing one profile does not erase another unavailable key");
            Check(new FusionConfiguration().State.Profiles[profileOne].ApiKey == freshKey, "Replaced translation key survives actual reload");

            var regularPath = AppDataPaths.SettingsPath;
            File.WriteAllText(regularPath, JsonSerializer.Serialize(new ApiSettings {ApiKey=badFormat}));
            var regular = ConfigurationManager.Load(regularPath, false);
            ConfigurationManager.Save(regularPath, regular);
            Check(JsonNode.Parse(File.ReadAllText(regularPath))!["ApiKey"]!.GetValue<string>() == badFormat, "Actual settings Load and Save preserve unavailable key");
            regular.ApiKey=freshKey;
            ConfigurationManager.SaveStageHook=stage=>{if(stage=="AfterTempValidated")throw new InvalidOperationException("Synthetic save failure");};
            try {Reject(()=>ConfigurationManager.Save(regularPath,regular),"Interrupted settings save fails before commit");}
            finally {ConfigurationManager.SaveStageHook=null;}
            regular.ApiKey="";ConfigurationManager.Save(regularPath,regular);
            Check(JsonNode.Parse(File.ReadAllText(regularPath))!["ApiKey"]!.GetValue<string>() == badFormat, "Settings save interruption does not forget unavailable key");

            string aiPath = Path.Combine(AppDataPaths.Root, "synthetic-connections.json");
            string aiOne = Guid.NewGuid().ToString("N"), aiTwo = Guid.NewGuid().ToString("N");
            object Entry(string id, string key, string management) => new {Id=id,Name="Synthetic "+id[..5],Endpoint="http://127.0.0.1:9/v1",ApiKey=key,Model="synthetic-model",AllowEmptyApiKey=false,RequestTimeoutSeconds=60,Options=new AiAgentOptions {XaiManagementApiKey=management,ReasoningEffort="fixture-effort"}};
            File.WriteAllText(aiPath, JsonSerializer.Serialize(new {Schema=1,Profiles=new[] {Entry(aiOne,badFormat,invalidBlob),Entry(aiTwo,originalCipher,"")}}));
            var connections = new AiConnectionProfiles(aiPath);
            Check(connections.Choices().Count == 2 && connections.Settings(aiOne).ApiKey == "", "AI connections remain listed with unavailable key hidden");
            Check(connections.Options(aiOne).ReasoningEffort == "fixture-effort" && connections.Options(aiOne).XaiManagementApiKey == "", "AI options preserved while unavailable management key is hidden");
            Check(!connections.Settings(aiOne).AllowEmptyApiKey, "AI empty-key authorization unchanged");
            Reject(() => AiConnectionProfiles.Validate(aiOne,connections.Name(aiOne),connections.Settings(aiOne),connections.Options(aiOne)), "Public AI validation still rejects missing key");
            Reject(() => connections.Save(aiOne,connections.Name(aiOne),connections.Settings(aiOne),connections.Options(aiOne)), "AI Save cannot bypass missing-key validation");
            var validOptions = connections.Options(aiTwo); validOptions.ReasoningEffort="updated-synthetic-effort"; connections.SaveOptions(aiTwo,validOptions);
            var aiSaved = JsonNode.Parse(File.ReadAllText(aiPath))!["Profiles"]!.AsArray();
            var preserved = aiSaved.Single(n => n!["Id"]!.GetValue<string>() == aiOne)!;
            Check(preserved["ApiKey"]!.GetValue<string>() == badFormat && preserved["Options"]!["XaiManagementApiKey"]!.GetValue<string>() == invalidBlob, "Saving another AI connection retains both unavailable ciphertext fields");
            var newAi = connections.Settings(aiOne); newAi.ApiKey=freshKey;
            var beforeFailedAiSave = File.ReadAllBytes(aiPath);
            using (var held = new FileStream(aiPath,FileMode.Open,FileAccess.Read,FileShare.None))
                Reject(()=>connections.Save(aiOne,connections.Name(aiOne),newAi,connections.Options(aiOne)),"Locked target rejects atomic AI replacement");
            Check(File.ReadAllBytes(aiPath).SequenceEqual(beforeFailedAiSave),"Failed AI replacement leaves original file byte-identical");
            connections.SaveOptions(aiTwo,validOptions);
            Check(JsonNode.Parse(File.ReadAllText(aiPath))!["Profiles"]!.AsArray().Single(n=>n!["Id"]!.GetValue<string>()==aiOne)!["ApiKey"]!.GetValue<string>()==badFormat,"AI save after failed replacement still preserves unavailable key");
            connections.Save(aiOne,connections.Name(aiOne),newAi,connections.Options(aiOne));
            var restarted = new AiConnectionProfiles(aiPath);
            Check(restarted.Settings(aiOne).ApiKey == freshKey && restarted.Choices().Count == 2, "Re-entered AI key and connection identity survive reload");
            var managementStill = JsonNode.Parse(File.ReadAllText(aiPath))!["Profiles"]!.AsArray().Single(n => n!["Id"]!.GetValue<string>() == aiOne)!;
            Check(managementStill["Options"]!["XaiManagementApiKey"]!.GetValue<string>() == invalidBlob, "Replacing API key does not erase unavailable management key");
            var management = restarted.Options(aiOne); management.XaiManagementApiKey=freshKey; restarted.SaveOptions(aiOne,management);
            Check(new AiConnectionProfiles(aiPath).Options(aiOne).XaiManagementApiKey == freshKey, "Re-entered management key survives reload");
            File.WriteAllText(aiPath, JsonSerializer.Serialize(new {Schema=1,Profiles=new[] {Entry(aiOne,"","")}}));
            Reject(() => new AiConnectionProfiles(aiPath), "Ordinary missing AI key is not mistaken for unavailable ciphertext");
            File.WriteAllText(aiPath, JsonSerializer.Serialize(new {Schema=1,Profiles=Array.Empty<object>()}));
            _ = new AiConnectionProfiles(aiPath);
            Check(!UnavailableCredentials.IsUnavailable(aiPath,"/Profiles/"+aiOne+"/ApiKey"), "Successful reload replaces stale per-file recovery state");
            return Write(true, null);
        }
        catch (Exception ex) { return Write(false, ex is InvalidOperationException ? ex.Message : ex.GetType().Name); }
        int Write(bool passed, string? failure)
        {
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {passed,checks=checks.Count,checkNames=checks,failure,syntheticOnly=true,networkRequests=0,originalCredentialReads=0},new JsonSerializerOptions{WriteIndented=true}));
            return passed ? 0 : 1;
        }
    }
}
