using System.Diagnostics;
using System.Text.Json;

namespace ScreenshotTranslationUiTester;

// Opt-in local performance evidence. Never records field values or credentials.
internal static class UiPerformanceTrace
{
    private static readonly string? Output = Environment.GetEnvironmentVariable("FUSION_UI_TRACE");
    private static long layouts, fonts, handles, paints;
    internal static bool Enabled => !string.IsNullOrEmpty(Output);
    internal static void Write(string operation, object data)
    {
        if (!Enabled) return;
        File.AppendAllText(Output!, JsonSerializer.Serialize(new { utc = DateTimeOffset.UtcNow, operation, data }) + Environment.NewLine);
    }
    internal static IDisposable Measure(string operation) => new Span(operation);
    private sealed class Span(string name) : IDisposable
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly long l=layouts, f=fonts, h=handles, p=paints;
        public void Dispose() { if(Enabled) Write(name,new { ms=clock.Elapsed.TotalMilliseconds, layouts=layouts-l, fontChanges=fonts-f, handles=handles-h, paints=paints-p }); }
    }
    internal static void Attach(Form form)
    {
        if (!Enabled) return;
        void Hook(Control c)
        {
            c.Layout += (_,_) => layouts++;
            c.FontChanged += (_,_) => fonts++;
            c.HandleCreated += (_,_) => handles++;
            c.Paint += (_,_) => paints++;
            if(c is ComboBox box) box.DropDown += (_,_) => {
                var watch=Stopwatch.StartNew();
                box.BeginInvoke((Action)(()=>Write("dropdown-ready",new { field=box.AccessibleName,ms=watch.Elapsed.TotalMilliseconds })));
            };
            foreach(Control child in c.Controls) Hook(child);
        }
        Hook(form);
        var clock=Stopwatch.StartNew(); double last=0;
        var timer=new System.Windows.Forms.Timer{Interval=50};
        timer.Tick+=(_,_)=>{var now=clock.Elapsed.TotalMilliseconds;var gap=now-last;last=now;if(gap>200)Write("ui-heartbeat-gap",new{ms=gap});};
        var filter=new InputTrace(form);Application.AddMessageFilter(filter);
        form.FormClosed+=(_,_)=>{timer.Dispose();Application.RemoveMessageFilter(filter);Write("window-closed",new{form=form.GetType().Name});};
        timer.Start();Write("attached",new { form=form.GetType().Name, dpi=form.DeviceDpi });
    }
    private sealed class InputTrace(Form form) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if(m.Msg!=0x202 && m.Msg!=0x101)return false;
            var c=Control.FromHandle(m.HWnd);if(c?.FindForm()!=form)return false;
            var watch=Stopwatch.StartNew();var l=layouts;var f=fonts;var h=handles;
            var kind=c.GetType().Name;var field=c.AccessibleName;
            form.BeginInvoke((Action)(()=>Write("input-dispatched",new{kind,field,ms=watch.Elapsed.TotalMilliseconds,layouts=layouts-l,fontChanges=fonts-f,handles=handles-h})));
            return false;
        }
    }
}
