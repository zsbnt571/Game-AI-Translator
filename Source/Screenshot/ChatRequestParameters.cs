using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScreenshotTranslationUiTester;

// Profile-level transport options. Prompts, credentials and message ownership
// remain with the translation pipeline; custom null values remove defaults.
internal static class ChatRequestParameters
{
    internal static JsonObject Parse(string? text)
    {
        if(string.IsNullOrWhiteSpace(text))return new();
        if(text.Length>32768)throw new InvalidOperationException("高级 API 参数不能超过 32 KB。");
        try
        {
            using var doc=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=16});
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("高级 API 参数必须是 JSON 对象，例如 {}。");
            var result=new JsonObject();
            foreach(var property in doc.RootElement.EnumerateObject())
            {
                if(property.Name is "model" or "messages" or "stream")
                    throw new InvalidOperationException("高级 API 参数不能覆盖 model、messages 或 stream；模型和翻译消息由软件管理。");
                if(result.ContainsKey(property.Name))throw new InvalidOperationException("高级 API 参数含有重复字段，请为每个字段只保留一个值。");
                result.Add(property.Name,JsonNode.Parse(property.Value.GetRawText()));
            }
            return result;
        }
        catch(JsonException ex)
        {throw new InvalidOperationException($"高级 API 参数 JSON 格式错误（第 {(ex.LineNumber??0)+1} 行，第 {(ex.BytePositionInLine??0)+1} 列）。",ex);}
    }
    private static bool IsModel(string model,string name)=>model.Equals(name,StringComparison.OrdinalIgnoreCase)||model.StartsWith(name+"-",StringComparison.OrdinalIgnoreCase);
    private static bool IsReasoningModel(string model)=>model.StartsWith("gpt-5",StringComparison.OrdinalIgnoreCase)||model.StartsWith("gpt-6",StringComparison.OrdinalIgnoreCase)||
        new[]{"o1","o3","o4"}.Any(name=>IsModel(model,name));

    internal static JsonObject Patch(ApiSettings settings)
    {
        var custom=Parse(settings.AdvancedApiParameters);
        var result=new JsonObject();
        var host=Uri.TryCreate(settings.ApiUrl,UriKind.Absolute,out var endpoint)?endpoint.Host:"";
        bool openai=host.Equals("api.openai.com",StringComparison.OrdinalIgnoreCase);
        if(host.Equals("api.deepseek.com",StringComparison.OrdinalIgnoreCase))result["thinking"]=new JsonObject{["type"]="disabled"};
        if(openai)
        {
            var model=settings.Model.Trim();
            if(IsModel(model,"gpt-6-luna")||IsModel(model,"gpt-6-sol"))result["reasoning_effort"]="none";
            else if(IsModel(model,"gpt-6-astra")||IsModel(model,"gpt-6.1-sol"))result["reasoning_effort"]="low";
            string? effort=custom.ContainsKey("reasoning_effort")?custom["reasoning_effort"]?.ToString():result["reasoning_effort"]?.ToString();
            // Reasoning models reject sampling controls except in none mode.
            if(IsReasoningModel(model)&&effort!="none")result["temperature"]=null;
        }
        foreach(var pair in custom)result[pair.Key]=pair.Value?.DeepClone();
        return result;
    }
    internal static void Apply(JsonObject body,ApiSettings settings)
    {
        foreach(var pair in Patch(settings))
            if(pair.Value is null)body.Remove(pair.Key);else body[pair.Key]=pair.Value.DeepClone();
    }
    internal static string IdentitySuffix(ApiSettings settings)=>string.IsNullOrWhiteSpace(settings.AdvancedApiParameters)?"":"|api-parameters="+settings.AdvancedApiParameters;
    internal static string PluginParametersBase64(ApiSettings settings,int maximumTokens)
    {
        var body=new JsonObject{["temperature"]=.2,["max_tokens"]=maximumTokens};
        if(Uri.TryCreate(settings.ApiUrl,UriKind.Absolute,out var url)&&url.Host.Equals("api.openai.com",StringComparison.OrdinalIgnoreCase)&&IsReasoningModel(settings.Model.Trim()))
        {
            body.Remove("max_tokens");body["max_completion_tokens"]=Math.Max(4096,maximumTokens);
        }
        Apply(body,settings);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(body.ToJsonString()));
    }
}
