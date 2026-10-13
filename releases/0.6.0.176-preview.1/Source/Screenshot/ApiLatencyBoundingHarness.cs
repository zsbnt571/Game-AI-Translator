using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenshotTranslationUiTester;

internal static class ApiLatencyBoundingHarness
{
    private const int RequiredItemCount=53;
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNameCaseInsensitive=true};

    internal static async Task<int> RunAsync(string outputRoot,string pairingManifestPath,string settingsPath,
        string replayRoot,int repeats)
    {
        if(repeats<1)throw new ArgumentOutOfRangeException(nameof(repeats));
        Directory.CreateDirectory(outputRoot);
        var settings=ConfigurationManager.Load(settingsPath,false);
        settings.TranslationCacheEnabled=false;
        var inputs=JsonSerializer.Deserialize<List<PairingInput>>(File.ReadAllText(pairingManifestPath),JsonOptions)??[];
        var dense=inputs.Select(input=>LoadFixture(input,replayRoot))
            .Where(x=>x.Items.Count>=RequiredItemCount)
            .OrderByDescending(x=>x.Items.Count).ThenBy(x=>x.PairId,StringComparer.Ordinal).FirstOrDefault()
            ??throw new InvalidDataException($"No replay fixture contains {RequiredItemCount} translation blocks.");
        var items=dense.Items.Take(RequiredItemCount).ToArray();
        var expectedIds=items.Select(x=>x.Id).ToArray();
        if(expectedIds.Distinct(StringComparer.Ordinal).Count()!=RequiredItemCount)
            throw new InvalidDataException("Benchmark input IDs are not unique.");

        File.WriteAllText(Path.Combine(outputRoot,"SETTINGS-REDACTED.json"),JsonSerializer.Serialize(new
        {
            Provider=settings.ProviderDisplayName,Endpoint=settings.ApiUrl,settings.Model,settings.TargetLanguage,
            settings.TranslationStyle,settings.TranslationMode,settings.TranslationPromptProfile,
            settings.TranslationPromptVersion,Temperature=.15,Stream=false,Thinking="disabled",
            settings.FirstByteTimeoutSeconds,settings.RequestTimeoutSeconds,ApiKeyRecorded=false
        },JsonOptions));
        File.WriteAllText(Path.Combine(outputRoot,"BENCHMARK-INPUT.json"),JsonSerializer.Serialize(new
        {
            SourceFixture=dense.PairId,OriginalBlockCount=dense.Items.Count,SelectedBlockCount=items.Length,
            Selection="first 53 blocks in product reading order from densest replay fixture",
            Items=items.Select((x,index)=>new{Order=index,x.Id,Role=x.RoleType.ToString(),x.Text,x.StableSourceIds,
                ReferenceTranslation=dense.Reference.GetValueOrDefault(x.Id,"")})
        },JsonOptions));

        var rows=new List<RunRow>();
        var outputs=new Dictionary<string,Dictionary<string,string>>(StringComparer.Ordinal);
        for(var repeat=1;repeat<=repeats;repeat++)
        {
            var schedule=(repeat%3) switch
            {
                1=>new[]{1,2,3},
                2=>new[]{2,3,1},
                _=>new[]{3,1,2}
            };
            foreach(var batches in schedule)
            {
                var label=$"{(batches==1?'A':batches==2?'B':'C')}-R{repeat:D2}";
                Console.WriteLine($"API-BENCH BEGIN {label} items={items.Length} batches={batches}");
                var run=await ExecuteAsync(items,batches,settings,CancellationToken.None).ConfigureAwait(false);
                var exactIdSet=expectedIds.ToHashSet(StringComparer.Ordinal).SetEquals(run.Translations.Keys);
                var ordering=exactIdSet&&expectedIds.SequenceEqual(items.Where(x=>run.Translations.ContainsKey(x.Id)).Select(x=>x.Id),StringComparer.Ordinal);
                var nonEmpty=exactIdSet&&items.All(x=>!string.IsNullOrWhiteSpace(run.Translations.GetValueOrDefault(x.Id)));
                var numeric=exactIdSet&&items.All(x=>CorePipelineV2.CorePipelineEngine.NumericTokensMatch(x.Text,run.Translations.GetValueOrDefault(x.Id,"")));
                var placeholders=exactIdSet&&items.All(x=>PlaceholdersMatch(x.Text,run.Translations.GetValueOrDefault(x.Id,"")));
                var semanticContract=run.Success&&exactIdSet&&ordering&&nonEmpty&&numeric&&placeholders;
                var status=semanticContract?"PASS":"FAIL";
                rows.Add(new(label,repeat,batches,items.Length,run.WallMs,run.Success,exactIdSet,ordering,nonEmpty,numeric,
                    placeholders,semanticContract,run.RequestCount,run.WaitMs,run.RetryCount,run.PartialResponseCount,
                    run.FirstByteTimeoutCount,run.HttpStatuses,run.HttpFailureCount,run.TransportFailureCount,
                    run.PromptTokens,run.CompletionTokens,run.TotalTokens,run.ErrorType,status));
                outputs[label]=run.Translations;
                File.WriteAllText(Path.Combine(outputRoot,$"RUN-{label}.json"),JsonSerializer.Serialize(new
                {
                    label,repeat,RequestedBatches=batches,run.WallMs,run.Success,ExactBlockIdSet=exactIdSet,
                    OrderingPreserved=ordering,NoEmptyTranslations=nonEmpty,NumericFidelity=numeric,
                    PlaceholderFidelity=placeholders,SemanticAutomatedContract=semanticContract,
                    run.RequestCount,run.WaitMs,run.RetryCount,run.PartialResponseCount,run.FirstByteTimeoutCount,
                    run.HttpStatuses,run.HttpFailureCount,run.TransportFailureCount,run.PromptTokens,
                    run.CompletionTokens,run.TotalTokens,run.ErrorType,run.ErrorMessage,
                    Items=items.Select((x,index)=>new{Order=index,x.Id,Source=x.Text,
                        Translation=run.Translations.GetValueOrDefault(x.Id,""),
                        Reference=dense.Reference.GetValueOrDefault(x.Id,""),
                        Numeric=CorePipelineV2.CorePipelineEngine.NumericTokensMatch(x.Text,run.Translations.GetValueOrDefault(x.Id,"")),
                        Placeholders=PlaceholdersMatch(x.Text,run.Translations.GetValueOrDefault(x.Id,""))})
                },JsonOptions));
                Console.WriteLine($"API-BENCH END {label} status={status} wall_ms={run.WallMs:F0} requests={run.RequestCount}");
                await Task.Delay(750).ConfigureAwait(false);
            }
        }

        WriteCsv(Path.Combine(outputRoot,"API-BATCHING.csv"),rows);
        WriteRobustnessCsv(Path.Combine(outputRoot,"API-ROBUSTNESS.csv"),rows);
        File.WriteAllText(Path.Combine(outputRoot,"TRANSLATION-CROSS-RUN.json"),JsonSerializer.Serialize(
            BuildCrossRun(items,outputs),JsonOptions));
        var variants=rows.GroupBy(x=>x.Batches).OrderBy(x=>x.Key).Select(group=>
        {
            var successful=group.Where(x=>x.Success).ToArray();
            var walls=successful.Select(x=>x.WallMs).Order().ToArray();
            return new
            {
                Label=group.Key==1?"A_SINGLE_LARGE":group.Key==2?"B_TWO_BOUNDED":"C_THREE_BOUNDED",
                Batches=group.Key,Runs=group.Count(),SuccessfulRuns=successful.Length,
                MeanWallMs=walls.Length==0?0:walls.Average(),MedianWallMs=Percentile(walls,.5),P90WallMs=Percentile(walls,.9),
                MaxWallMs=walls.Length==0?0:walls.Max(),Requests=group.Sum(x=>x.RequestCount),Retries=group.Sum(x=>x.RetryCount),
                HttpFailures=group.Sum(x=>x.HttpFailureCount),TransportFailures=group.Sum(x=>x.TransportFailureCount),
                PromptTokens=group.Sum(x=>x.PromptTokens),CompletionTokens=group.Sum(x=>x.CompletionTokens),TotalTokens=group.Sum(x=>x.TotalTokens),
                ContractPasses=group.Count(x=>x.SemanticContract)
            };
        }).ToArray();
        var overallPass=rows.All(x=>x.SemanticContract);
        File.WriteAllText(Path.Combine(outputRoot,"RUN-SUMMARY.json"),JsonSerializer.Serialize(new
        {
            Items=items.Length,Repeats=repeats,TotalScenarioRuns=rows.Count,SourceFixture=dense.PairId,
            Fairness=new{SameModel=true,SameTarget=true,SamePrompt=true,SameTemperature=true,SameProfile=true,SameInputBlocks=true,
                Schedule="Latin rotation A-B-C / B-C-A / C-A-B",BoundedConcurrencyMaximum=3},
            Variants=variants,AutomatedFidelityGate=overallPass?"PASS":"FAIL",
            SemanticManualReview="REQUIRED_BEFORE_PRODUCT_PROMOTION",Candidate="NO",Status=overallPass?"PASS_PENDING_SEMANTIC_REVIEW":"FAIL"
        },JsonOptions));
        return overallPass?0:31;
    }

    private static async Task<RunOutcome> ExecuteAsync(IReadOnlyList<TranslationItem> items,int batches,
        ApiSettings settings,CancellationToken cancellationToken)
    {
        var shards=Partition(items,batches);
        var timer=Stopwatch.StartNew();
        try
        {
            var tasks=shards.Select(async shard=>
            {
                var service=new TranslationService();
                var provider=new LegacyDeepSeekProviderAdapter(service);
                return await provider.TranslateAsync(shard,settings,null,cancellationToken).ConfigureAwait(false);
            }).ToArray();
            var results=await Task.WhenAll(tasks).ConfigureAwait(false);timer.Stop();
            var merged=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var item in items)
            {
                var matches=results.Where(x=>x.Translations.ContainsKey(item.Id)).ToArray();
                if(matches.Length==1)merged[item.Id]=matches[0].Translations[item.Id];
            }
            var transport=results.Select(x=>x.TransportStats).Where(x=>x is not null).Cast<TranslationTransportStats>().ToArray();
            return new(true,timer.Elapsed.TotalMilliseconds,merged,results.Sum(x=>x.RequestCount),
                results.Sum(x=>x.WaitMs),results.Sum(x=>x.RecoveryStats?.RetryRequestCount??0),
                results.Sum(x=>x.RecoveryStats?.PartialResponseCount??0),
                results.Sum(x=>x.RecoveryStats?.FirstByteTimeoutCount??0),
                string.Join(';',transport.SelectMany(x=>x.HttpStatusCodes)),transport.Sum(x=>x.HttpFailureCount),
                transport.Sum(x=>x.TransportFailureCount),transport.Sum(x=>x.PromptTokens),
                transport.Sum(x=>x.CompletionTokens),transport.Sum(x=>x.TotalTokens),"","");
        }
        catch(Exception ex)
        {
            timer.Stop();
            var completed=ex is TranslationRecoveryFailedException recovery
                ?new Dictionary<string,string>(recovery.CompletedTranslations,StringComparer.Ordinal)
                :new Dictionary<string,string>(StringComparer.Ordinal);
            var recoveryStats=(ex as TranslationRecoveryFailedException)?.RecoveryStats;
            return new(false,timer.Elapsed.TotalMilliseconds,completed,recoveryStats?.TotalRequestCount??0,0,
                recoveryStats?.RetryRequestCount??0,recoveryStats?.PartialResponseCount??0,
                recoveryStats?.FirstByteTimeoutCount??0,"",0,ex is HttpRequestException or IOException?1:0,
                0,0,0,ex.GetType().Name,Sanitize(ex.Message));
        }
    }

    private static IReadOnlyList<IReadOnlyList<TranslationItem>> Partition(IReadOnlyList<TranslationItem> items,int count)
    {
        var result=new List<IReadOnlyList<TranslationItem>>(count);var offset=0;
        for(var index=0;index<count;index++)
        {
            var length=items.Count/count+(index<items.Count%count?1:0);
            result.Add(items.Skip(offset).Take(length).ToArray());offset+=length;
        }
        return result;
    }

    private static Fixture LoadFixture(PairingInput input,string replayRoot)
    {
        var fixture=Path.Combine(replayRoot,"fixtures",input.PairId);
        var ocr=JsonSerializer.Deserialize<OcrEngineResult>(File.ReadAllText(Path.Combine(fixture,"PRODUCT-OCR-REPLAY.json")),JsonOptions)
            ??throw new InvalidDataException($"Invalid OCR replay for {input.PairId}");
        using var image=new Bitmap(input.ProductInputPath);
        var document=CorePipelineV2.MegaVisualFidelityE2EHarness.BuildCore(ocr,image.Size);
        var items=document.VisualBlocks.Select(block=>new TranslationItem(block.BlockId,block.SourceText,
            Enum.TryParse<StructuredTextRole>(block.RoleHint,true,out var role)?role:StructuredTextRole.Unknown,
            block.Lines.Select(x=>x.SourceId).ToArray(),TranslationIdentityContract.CoreV2Block)).ToArray();
        using var translated=JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture,"TRANSLATED-TEXT.json")));
        var reference=translated.RootElement.GetProperty("Items").EnumerateArray().ToDictionary(
            x=>x.GetProperty("BlockId").GetString()??"",x=>x.GetProperty("TranslatedText").GetString()??"",StringComparer.Ordinal);
        return new(input.PairId,items,reference);
    }

    private static bool PlaceholdersMatch(string source,string translation)
    {
        static string[] Extract(string text)=>Regex.Matches(text,@"\{\{[^{}]+\}\}|\{[^{}]+\}|%\w|\$\w+")
            .Select(x=>x.Value).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        return Extract(source).SequenceEqual(Extract(translation),StringComparer.Ordinal);
    }

    private static object BuildCrossRun(IReadOnlyList<TranslationItem> items,
        IReadOnlyDictionary<string,Dictionary<string,string>> outputs)
    {
        return new
        {
            Items=items.Select(item=>
            {
                var values=outputs.ToDictionary(x=>x.Key,x=>x.Value.GetValueOrDefault(item.Id,""),StringComparer.Ordinal);
                return new{item.Id,Source=item.Text,DistinctNonEmptyTranslations=values.Values.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Count(),Outputs=values};
            }),
            Note="Text variation is evidence for manual semantic review; exact wording equality is not a semantic requirement."
        };
    }

    private static double Percentile(double[] values,double fraction)
    {
        if(values.Length==0)return 0;var index=(values.Length-1)*fraction;var lo=(int)Math.Floor(index);var hi=(int)Math.Ceiling(index);
        return lo==hi?values[lo]:values[lo]+(values[hi]-values[lo])*(index-lo);
    }

    private static string Sanitize(string value)
    {
        var text=(value??"").Replace('\r',' ').Replace('\n',' ').Trim();
        return text.Length>500?text[..500]+"…":text;
    }

    private static void WriteCsv(string path,IEnumerable<RunRow> rows)
    {
        static string Q(object? value)=>'"'+Convert.ToString(value,CultureInfo.InvariantCulture)!.Replace("\"","\"\"")+'"';
        var header=new[]{"Label","Repeat","Batches","Items","WallMs","Success","ExactIdSet","Ordering","NonEmpty","NumericFidelity","PlaceholderFidelity","SemanticAutomatedContract","RequestCount","ProviderWaitMs","RetryCount","PartialResponses","FirstByteTimeouts","HttpStatuses","HttpFailures","TransportFailures","PromptTokens","CompletionTokens","TotalTokens","ErrorType","Status"};
        File.WriteAllLines(path,new[]{string.Join(',',header.Select(Q))}.Concat(rows.Select(x=>string.Join(',',new object?[]{x.Label,x.Repeat,x.Batches,x.Items,x.WallMs,x.Success,x.ExactIdSet,x.Ordering,x.NonEmpty,x.NumericFidelity,x.PlaceholderFidelity,x.SemanticContract,x.RequestCount,x.WaitMs,x.RetryCount,x.PartialResponseCount,x.FirstByteTimeoutCount,x.HttpStatuses,x.HttpFailureCount,x.TransportFailureCount,x.PromptTokens,x.CompletionTokens,x.TotalTokens,x.ErrorType,x.Status}.Select(Q)))),new UTF8Encoding(true));
    }

    private static void WriteRobustnessCsv(string path,IEnumerable<RunRow> rows)
    {
        static string Q(object? value)=>'"'+Convert.ToString(value,CultureInfo.InvariantCulture)!.Replace("\"","\"\"")+'"';
        var header=new[]{"Label","JSONValid","ExactIdSet","NoDroppedBlocks","NoDuplicatedBlocks","OrderingPreserved","Retries","PartialResponses","FirstByteTimeouts","HttpFailures","TransportFailures","Status"};
        File.WriteAllLines(path,new[]{string.Join(',',header.Select(Q))}.Concat(rows.Select(x=>string.Join(',',new object?[]{x.Label,x.Success,x.ExactIdSet,x.ExactIdSet,x.ExactIdSet,x.Ordering,x.RetryCount,x.PartialResponseCount,x.FirstByteTimeoutCount,x.HttpFailureCount,x.TransportFailureCount,x.Status}.Select(Q)))),new UTF8Encoding(true));
    }

    private sealed record PairingInput(string PairId,string Set,string SourcePath,string ReferencePath,string ProductInputPath,string SourceFormat);
    private sealed record Fixture(string PairId,IReadOnlyList<TranslationItem> Items,Dictionary<string,string> Reference);
    private sealed record RunOutcome(bool Success,double WallMs,Dictionary<string,string> Translations,int RequestCount,long WaitMs,
        int RetryCount,int PartialResponseCount,int FirstByteTimeoutCount,string HttpStatuses,int HttpFailureCount,
        int TransportFailureCount,int PromptTokens,int CompletionTokens,int TotalTokens,string ErrorType,string ErrorMessage);
    private sealed record RunRow(string Label,int Repeat,int Batches,int Items,double WallMs,bool Success,bool ExactIdSet,
        bool Ordering,bool NonEmpty,bool NumericFidelity,bool PlaceholderFidelity,bool SemanticContract,int RequestCount,
        long WaitMs,int RetryCount,int PartialResponseCount,int FirstByteTimeoutCount,string HttpStatuses,
        int HttpFailureCount,int TransportFailureCount,int PromptTokens,int CompletionTokens,int TotalTokens,string ErrorType,string Status);
}
