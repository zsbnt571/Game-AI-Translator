using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace FusionYukar;
// Edits are executed by GameMain.update, never by a socket/background thread.
// Only the documented SystemData variable API and explicit numeric fields enter
// this table. Caller-supplied reflection paths are never accepted.
internal sealed class GameData
{
    const BindingFlags All=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    sealed class Entry {internal string Name;internal Func<object> Read;internal Action<object> Write;internal Type Type;internal int Generation;internal double Min=-1e12,Max=1e12;}
    sealed class Change {internal Entry Entry;internal object Before,After;}
    object root,party,system;int generation,serial;bool enabled;readonly Dictionary<string,Entry> entries=new Dictionary<string,Entry>();
    readonly Dictionary<string,Change> locks=new Dictionary<string,Change>();readonly List<Change> history=new List<Change>();
    // The provider itself reads the engine's data field. Keep it lazy so that
    // disabled updates and delayed IPC requests never even fetch that field.
    internal void Sync(Func<object> currentData)
    {
        if(!enabled||currentData==null)return;
        object next=currentData();
        var p=Runtime.Field(next,"party");var s=Runtime.Field(next,"system");
        if(!ReferenceEquals(root,next)||!ReferenceEquals(party,p)||!ReferenceEquals(system,s)){root=next;party=p;system=s;generation++;entries.Clear();locks.Clear();history.Clear();}
    }
    internal void Stop(){enabled=false;root=party=system=null;generation++;locks.Clear();entries.Clear();history.Clear();}
    internal void Tick(){if(!enabled)return;foreach(var pair in locks.ToArray())try{var e=pair.Value.Entry;Validate(e);object now=e.Read();if(!Equal(now,pair.Value.After))Write(e,pair.Value.After,now);}catch{locks.Remove(pair.Key);}}
    static bool Equal(object a,object b)=>a is string||a is bool?Equals(a,b):a!=null&&b!=null&&Convert.ToDouble(a,CultureInfo.InvariantCulture)==Convert.ToDouble(b,CultureInfo.InvariantCulture);
    void Validate(Entry e){if(e.Generation!=generation||root==null)throw new InvalidOperationException("游戏数据已更换，请刷新。");}
    object Write(Entry e,object value,object expected)
    {
        Validate(e);object before=e.Read();if(!Equal(before,expected))throw new InvalidOperationException("数值已变化，请刷新后重试。");
        object typed;
        if(e.Type==typeof(string)){if(!(value is string s)||s.Length>2000||s.IndexOf('\0')>=0)throw new InvalidOperationException("文本格式不符。");typed=s;}
        else if(e.Type==typeof(bool)){if(!(value is bool))throw new InvalidOperationException("开关类型不符。");typed=value;}
        else {if(value==null||value is string||value is bool)throw new InvalidOperationException("数值类型不符。");double n=Convert.ToDouble(value,CultureInfo.InvariantCulture);if(double.IsInfinity(n)||double.IsNaN(n)||n<e.Min||n>e.Max||(e.Type==typeof(int)&&n!=Math.Truncate(n)))throw new InvalidOperationException("数值超出允许范围。");typed=Convert.ChangeType(n,e.Type,CultureInfo.InvariantCulture);}
        try
        {
            e.Write(typed);
            if(!Equal(e.Read(),typed))throw new InvalidOperationException("游戏返回的值与请求不同。");
            return before;
        }
        catch(Exception error)
        {
            // A setter may assign and then throw. Read the same still-valid
            // entry before rolling back; never follow a replaced collection.
            bool restored=false;
            try{if(!Equal(e.Read(),before))e.Write(before);restored=Equal(e.Read(),before);}catch{}
            throw new InvalidOperationException(restored?"游戏未接受该值，已恢复原值。":"写入失败，无法确认已恢复，请刷新检查。",error);
        }
    }
    object Snapshot()
    {
        entries.Clear();var rows=new List<object>();bool truncated=false;int characters=0;
        void Add(Entry e)
        {
            if(rows.Count>=2048){truncated=true;return;}object value;
            try{value=e.Read();}catch{return;}
            if(value is string text&&text.Length>2000)return;if(value==null)return;
            characters+=e.Name.Length+Convert.ToString(value,CultureInfo.InvariantCulture).Length+100;if(characters>230000){truncated=true;return;}
            e.Generation=generation;string id=(++serial).ToString(CultureInfo.InvariantCulture);entries[id]=e;
            rows.Add(new{id,name=e.Name,value,type=e.Type==typeof(string)?"string":e.Type==typeof(bool)?"boolean":"number",locked=locks.ContainsKey(e.Name)});
        }
        if(root!=null&&party!=null)
        {
            var owner=party;var money=owner.GetType().GetField("money",All);var maximum=owner.GetType().GetField("MAX_MONEY",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static);
            if(money!=null&&money.FieldType==typeof(int)&&!money.IsInitOnly)Add(new Entry{Name="队伍.金钱",Type=typeof(int),Min=0,Max=maximum==null?999999:Convert.ToDouble(maximum.GetValue(null)),Read=()=>{if(!ReferenceEquals(Runtime.Field(root,"party"),owner))throw new InvalidOperationException("队伍已更换。");return money.GetValue(owner);},Write=v=>money.SetValue(owner,v)});
        }
        if(system!=null&&Runtime.Field(system,"Variables") is IDictionary variables)
        {
            object owner=system;
            foreach(DictionaryEntry pair in variables)
            {
                if(rows.Count>=2048){truncated=true;break;}if(!(pair.Key is string name)||name.Length>500||pair.Value==null)continue;
                var kind=pair.Value.GetType().GetMethod("getType",All,null,Type.EmptyTypes,null);if(kind==null)continue;
                string category=kind.Invoke(pair.Value,null)?.ToString();Type t=category=="STRING"?typeof(string):category=="DOUBLE"?typeof(double):null;if(t==null)continue;
                string getter=t==typeof(string)?"getString":"getDouble";
                var setter=owner.GetType().GetMethod("SetVariable",All,null,new[]{typeof(string),t},null);if(setter==null)continue;
                Add(new Entry{Name="变量."+name,Type=t,Read=()=>{
                    if(!ReferenceEquals(Runtime.Field(root,"system"),owner)||!ReferenceEquals(Runtime.Field(owner,"Variables"),variables)||!variables.Contains(name))throw new InvalidOperationException("变量集合已更换。");
                    object current=variables[name];if(current==null||current.GetType().GetMethod("getType",All)?.Invoke(current,null)?.ToString()!=category)throw new InvalidOperationException("变量类型已更换。");return current.GetType().GetMethod(getter,All,null,Type.EmptyTypes,null).Invoke(current,null);
                },Write=v=>setter.Invoke(owner,new[]{(object)name,v})});
            }
        }
        return new{enabled,ready=root!=null,generation,rows,truncated,locks=locks.Count};
    }
    internal object Command(Dictionary<string,object> q,Func<object> currentData=null)
    {
        string op=Runtime.Get(q,"op");
        if(op=="dataEnable"){
            enabled=true;
            try{Sync(currentData);}catch{Stop();throw;}
            return new{enabled,ready=root!=null,generation};
        }
        if(op=="dataDisable"){Stop();return new{enabled};}
        if(op=="dataStatus")return new{enabled,ready=root!=null,generation,locks=locks.Count};
        if(!enabled)throw new InvalidOperationException("请先开启修改。");
        Sync(currentData);
        if(op=="dataSnapshot")return Snapshot();
        if(op=="dataUndo"){if(history.Count==0)throw new InvalidOperationException("没有可撤销的修改。");var h=history[history.Count-1];Write(h.Entry,h.Before,h.After);locks.Remove(h.Entry.Name);history.RemoveAt(history.Count-1);return new{restored=true};}
        if(!q.TryGetValue("generation",out object gen)||Convert.ToInt32(gen)!=generation||!entries.TryGetValue(Runtime.Get(q,"entry"),out Entry e))throw new InvalidOperationException("列表已过期，请刷新。");
        if(op=="dataUnlock"){locks.Remove(e.Name);return new{unlocked=true};}
        if(op=="dataSet"||op=="dataLock"){
            if(op=="dataLock"&&!locks.ContainsKey(e.Name)&&locks.Count>=128)throw new InvalidOperationException("锁定项目达到上限。");
            object before=Write(e,q["value"],q["expected"]);var h=new Change{Entry=e,Before=before,After=e.Read()};history.Add(h);if(history.Count>64)history.RemoveAt(0);if(op=="dataLock"||locks.ContainsKey(e.Name))locks[e.Name]=h;return new{written=true,value=h.After};}
        throw new InvalidOperationException("不支持此修改请求。");
    }
}
