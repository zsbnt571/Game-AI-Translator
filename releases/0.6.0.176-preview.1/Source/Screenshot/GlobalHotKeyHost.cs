using System.Runtime.InteropServices;
namespace ScreenshotTranslationUiTester;
internal sealed class GlobalHotKeyHost : NativeWindow, IDisposable
{
    private readonly int _id;private readonly Action _trigger;
    internal bool Registered{get;private set;}internal InputBinding? RegisteredBinding{get;private set;}internal DateTimeOffset? LastTrigger{get;private set;}internal IntPtr RegistrationHandle=>Handle;
    internal GlobalHotKeyHost(int id,Action trigger){_id=id;_trigger=trigger;CreateHandle(new CreateParams{Caption="ScreenshotTranslator.HotKey",Parent=new IntPtr(-3)});Log("HandleCreated");}
    internal bool Register(uint modifiers,uint key){if(Registered)Unregister();Registered=NativeMethods.RegisterHotKey(Handle,_id,modifiers,key);Log($"Register Result={Registered} Error={(Registered?0:Marshal.GetLastWin32Error())}");return Registered;}
    internal bool Register(InputBinding binding){if(binding.Kind!=InputBindingKind.Keyboard){Unregister();return false;}uint mods=NativeMethods.ModNoRepeat|(binding.Ctrl?NativeMethods.ModControl:0)|(binding.Alt?NativeMethods.ModAlt:0)|(binding.Shift?NativeMethods.ModShift:0)|(binding.Win?NativeMethods.ModWin:0);var result=Register(mods,(uint)binding.Key);RegisteredBinding=result?binding:null;return result;}
    internal void Unregister(){RegisteredBinding=null;if(!Registered)return;var result=NativeMethods.UnregisterHotKey(Handle,_id);Registered=false;Log($"Unregister Result={result}");}
    protected override void WndProc(ref Message m){if(m.Msg==NativeMethods.WmHotKey&&m.WParam.ToInt32()==_id){LastTrigger=DateTimeOffset.Now;Log("Last Hotkey Trigger");_trigger();return;}base.WndProc(ref m);}
    internal void Log(string action)=>AppLog.Write("hotkey",$"{action}; Registered={Registered}; RegistrationHandle=0x{Handle.ToInt64():X}");
    public void Dispose(){Unregister();if(Handle!=IntPtr.Zero){Log("HandleDestroyed");DestroyHandle();}}
}
