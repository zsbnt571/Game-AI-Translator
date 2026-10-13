using System.Text.Json;

namespace ScreenshotTranslationUiTester;

internal static class TranslationBoundarySelfTests
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();var failures=0;
        foreach(var sample in new[]{(Name:"ACTUAL_LF",Text:"line one\nline two"),
            (Name:"LITERAL_BACKSLASH_N",Text:@"line one\nline two"),
            (Name:"WINDOWS_PATH",Text:@"C:\new\notes"),(Name:"CODE",Text:@"print('\n')")})
        foreach(var wrapper in new[]{"OBJECT","WRAPPED_ARRAY","FENCED_OBJECT"})
        {
            var events=new List<TranslationBoundaryEvent>();
            try
            {
                var value="  "+sample.Text+"  ";
                var inner=wrapper=="WRAPPED_ARRAY"
                    ?JsonSerializer.Serialize(new{translations=new[]{new{id="BOUNDARY-BLOCK",translation=value}}})
                    :JsonSerializer.Serialize(new Dictionary<string,string>{{"BOUNDARY-BLOCK",value}});
                if(wrapper=="FENCED_OBJECT")inner="```json\n"+inner+"\n```";
                var body=JsonSerializer.Serialize(new{choices=new[]{new{message=new{content=inner}}}});
                TranslationService.PartialParseOutcome parsed;
                using(TranslationBoundaryDiagnostics.Begin(events.Add))
                    parsed=TranslationService.ParseCoreV2PartialResponseForRecovery(body,["BOUNDARY-BLOCK"],sample.Name);
                var actual=parsed.Translations["BOUNDARY-BLOCK"];
                var pass=actual==sample.Text&&events.Any(e=>e.Stage=="OuterContentDecoded"&&e.Value==inner)&&
                    events.Any(e=>e.Stage=="ParserScalarDecoded"&&e.Value==value)&&
                    events.Any(e=>e.Stage=="ParserReturned"&&e.Value==sample.Text)&&!TranslationBoundaryDiagnostics.Enabled;
                if(!pass)failures++;
                rows.Add(new{sample.Name,Wrapper=wrapper,Pass=pass,Input=sample.Text,Actual=actual,Events=events});
            }
            catch(Exception ex){failures++;rows.Add(new{sample.Name,Wrapper=wrapper,Pass=false,Error=ex.GetType().Name});}
        }
        var secret="synthetic-boundary-credential-only";SafeDiagnosticOutput.RegisterCredential(secret);
        var redactionEvents=new List<TranslationBoundaryEvent>();
        using(TranslationBoundaryDiagnostics.Begin(redactionEvents.Add))
            TranslationBoundaryDiagnostics.Record("SYNTHETIC_OUTPUT","request",null,"Bearer "+secret+" / "+secret);
        var redacted=!redactionEvents[0].Value.Contains(secret,StringComparison.Ordinal)&&
            redactionEvents[0].Value.Contains("REDACTED",StringComparison.OrdinalIgnoreCase);
        if(!redacted)failures++;
        rows.Add(new{Name="Diagnostic output redaction",Pass=redacted,RealCredentialRead=false});
        File.WriteAllText(Path.Combine(output,"TRANSLATION-BOUNDARY-SELFTESTS.json"),JsonSerializer.Serialize(new
        {Pass=rows.Count-failures,Fail=failures,Rows=rows,RealApiCalls=0,
            Scope="Actual Core parser outer/inner decoding and output observer; HTTP transport requires the separately recorded live run"},
            new JsonSerializerOptions{WriteIndented=true}));
        return failures==0?0:1;
    }
}
