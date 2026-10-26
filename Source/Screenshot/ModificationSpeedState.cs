using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// UI range is derived from an implemented engine operation, never a variable
// whose name merely resembles speed. Step controls gestures; IntegerOnly is a
// backend constraint (MV/MZ currently advances complete extra scene updates).
internal sealed record ModificationSpeedState(bool Supported,bool Enabled,bool Busy,double Value,
    double Minimum,double Maximum,double Step,IReadOnlyList<double> Presets,string Reason,string ContextKey,bool IntegerOnly=false)
{
    // These describe the selected mechanism and its acknowledgement, never a
    // measured gameplay speed. Detailed evidence stays behind user disclosure.
    internal string BackendLabel { get; init; } = "";
    internal bool RateConfirmed { get; init; }
    internal string Feedback { get; init; } = "";
    internal string Diagnostics { get; init; } = "";
    private sealed class Context { internal readonly string Key=Guid.NewGuid().ToString("N"); }
    private static readonly ConditionalWeakTable<object,Context> contexts=new();
    internal static string ContextFor(object? connection)=>connection is null?"":contexts.GetValue(connection,_=>new()).Key;
    internal static readonly ModificationSpeedState Unavailable=new(false,false,false,1,1,1,1,[],"此引擎尚无可用的速度控制。","");
    internal static ModificationSpeedState ForWolf(double value,bool enabled,bool busy,object? connection)=>
        new(true,enabled&&!busy,busy,double.IsFinite(value)?Math.Clamp(value,.25,100):1,.25,100,.25,[.25,.5,1,2,4,10,25,50,100],
            enabled?"":"开启游戏修改后可调整速度。",ContextFor(connection));
    internal static ModificationSpeedState FromRpgRow(JsonElement row,bool enabled,bool busy,string contextKey="")
    {
        if(row.ValueKind!=JsonValueKind.Object||!row.TryGetProperty("key",out var key)||key.ValueKind!=JsonValueKind.String||key.GetString()!="option:speed"
            ||!row.TryGetProperty("type",out var type)||type.ValueKind!=JsonValueKind.String||type.GetString()!="number"
            ||!row.TryGetProperty("value",out var value)||value.ValueKind!=JsonValueKind.Number||!value.TryGetDouble(out double rate)||!double.IsFinite(rate)
            ||!row.TryGetProperty("min",out var min)||min.ValueKind!=JsonValueKind.Number||!min.TryGetDouble(out double lower)
            ||!row.TryGetProperty("max",out var max)||max.ValueKind!=JsonValueKind.Number||!max.TryGetDouble(out double upper))return Unavailable;
        double first=Math.Ceiling(Math.Max(1,lower)),last=Math.Floor(Math.Min(100,upper));
        if(!double.IsFinite(first)||!double.IsFinite(last)||first>last||rate<first||rate>last||rate!=Math.Truncate(rate))return Unavailable;
        return new(true,enabled&&!busy,busy,rate,first,last,1,new double[]{1,2,3,4,10,25,50,100}.Where(n=>n>=first&&n<=last).ToArray(),
            enabled?"支持整数加速；实际速度受游戏负载影响。":"开启游戏修改并进入游戏后可调整速度。",contextKey,true);
    }
    internal bool Accepts(double rate)=>Supported&&double.IsFinite(rate)&&rate>=Minimum&&rate<=Maximum&&(!IntegerOnly||rate==Math.Truncate(rate));
}

public sealed partial class MainForm
{
    private sealed class NovelSpeedValue { internal double Value=1; }
    private readonly ConditionalWeakTable<IGameDataConnection,NovelSpeedValue> _novelSpeeds=new();
    private double NovelSpeed(IGameDataConnection? connection)=>connection is null?1:_novelSpeeds.GetValue(connection,_=>new()).Value;
    private void ResetNovelSpeed(IGameDataConnection connection)=>_novelSpeeds.Remove(connection);
}
