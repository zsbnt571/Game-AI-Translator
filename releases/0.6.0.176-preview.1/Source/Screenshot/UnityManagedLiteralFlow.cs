using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ScreenshotTranslationUiTester;

// A bounded literal-only IL analysis. Unknown values stay unknown; no resolver,
// field/getter evaluation or game code execution occurs. Only proven literal
// String.Concat arguments may be combined into a complete display value.
internal static class UnityManagedLiteralFlow
{
    internal sealed record Literal(string Text,int Offset);
    internal sealed record Display(Literal Literal,MethodReference Call,int CallOffset);
    internal sealed record Result(Display[] Displays,bool Limited)
    { internal string? UnsupportedReason {get;init;} }
    private sealed class State
    {
        internal Literal[]?[] Locals;
        internal List<Literal[]?> Stack;
        internal State(int locals){Locals=new Literal[]?[locals];Stack=[];}
        internal State Clone()=>new(0){Locals=(Literal[]?[])Locals.Clone(),Stack=new(Stack)};
    }
    private sealed class Unsupported : Exception {}
    internal static Result Read(MethodDefinition method,Func<MethodReference,int> displayArgument,Action check,Func<FieldReference,string?>? fieldLiteral=null,Func<MethodReference,string?>? methodLiteral=null)
    {
        if(!method.HasBody)return new([],false);
        // Each handler starts with unknown locals. A leave crossing finally
        // also invalidates locals: a finally block may replace their values.
        // This keeps complete literals inside try/catch available without
        // pretending to evaluate exceptional control flow or game getters.
        if(method.Body.ExceptionHandlers.Any(h=>h.HandlerType==ExceptionHandlerType.Filter))
            return new([],false){UnsupportedReason="异常过滤器中的文字流暂不展开"};
        var code=method.Body.Instructions.ToArray();if(code.Length==0)return new([],false);
        if(method.Body.Variables.Count>256)return new([],true);
        var indexes=code.Select((item,index)=>(item,index)).ToDictionary(x=>x.item,x=>x.index);
        var leaders=new HashSet<int>{0};var escaped=new HashSet<int>();
        foreach(var handler in method.Body.ExceptionHandlers)
            foreach(var boundary in new[]{handler.TryStart,handler.TryEnd,handler.HandlerStart,handler.HandlerEnd})
                if(boundary is not null)leaders.Add(indexes[boundary]);
        foreach(var item in code)
        {
            if(item.Operand is Instruction target)leaders.Add(indexes[target]);
            else if(item.Operand is Instruction[] targets)foreach(var branch in targets)leaders.Add(indexes[branch]);
            if(item.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch or FlowControl.Return or FlowControl.Throw)
            {int next=indexes[item]+1;if(next<code.Length)leaders.Add(next);}
            if(item.OpCode.Code is Code.Ldloca or Code.Ldloca_S&&item.Operand is VariableDefinition variable)escaped.Add(variable.Index);
        }
        if(leaders.Count>2048)return new([],true);
        var starts=leaders.Order().ToArray();var ends=new Dictionary<int,int>();
        for(int i=0;i<starts.Length;i++)ends[starts[i]]=i+1<starts.Length?starts[i+1]:code.Length;
        var states=new Dictionary<int,State>{{0,new(method.Body.Variables.Count)}};
        var pending=new Queue<int>();var queued=new HashSet<int>();pending.Enqueue(0);queued.Add(0);
        foreach(var handler in method.Body.ExceptionHandlers)
        {
            var state=new State(method.Body.Variables.Count);
            if(handler.HandlerType==ExceptionHandlerType.Catch)state.Stack.Add(null);
            int start=indexes[handler.HandlerStart];states[start]=state;
            if(queued.Add(start))pending.Enqueue(start);
        }
        int steps=0;int stepLimit=Math.Min(500000,Math.Max(2048,code.Length*16));
        Literal[]? Pop(State state){if(state.Stack.Count==0)throw new Unsupported();var v=state.Stack[^1];state.Stack.RemoveAt(state.Stack.Count-1);return v;}
        void Push(State state,Literal[]? value){if(state.Stack.Count>=256)throw new Unsupported();state.Stack.Add(value);}
        int Local(Instruction item)=>item.OpCode.Code switch
        {
            Code.Ldloc_0 or Code.Stloc_0=>0,Code.Ldloc_1 or Code.Stloc_1=>1,
            Code.Ldloc_2 or Code.Stloc_2=>2,Code.Ldloc_3 or Code.Stloc_3=>3,
            _=>item.Operand is VariableDefinition v?v.Index:-1
        };
        void Transfer(State state,Instruction item,List<Display>? output)
        {
            if((++steps&255)==0)check();if(steps>stepLimit)throw new OverflowException();
            switch(item.OpCode.Code)
            {
                case Code.Nop: case Code.Break: return;
                case Code.Ldstr:
                    Push(state,item.Operand is string text&&text.Length is >0 and <=6000&&!text.Contains('\0')?[new(text,item.Offset)]:null);return;
                case Code.Ldsfld:
                    string? fieldText=item.Operand is FieldReference field?fieldLiteral?.Invoke(field):null;
                    Push(state,fieldText is not null?[new(fieldText,item.Offset)]:null);return;
                case Code.Dup:
                    if(state.Stack.Count==0)throw new Unsupported();Push(state,state.Stack[^1]);return;
                case Code.Pop: Pop(state);return;
                case Code.Ldloc: case Code.Ldloc_S: case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                    int read=Local(item);if(read<0||read>=state.Locals.Length)throw new Unsupported();Push(state,state.Locals[read]);return;
                case Code.Stloc: case Code.Stloc_S: case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                    int write=Local(item);if(write<0||write>=state.Locals.Length)throw new Unsupported();var value=Pop(state);state.Locals[write]=escaped.Contains(write)?null:value;return;
                case Code.Call: case Code.Callvirt: case Code.Newobj:
                    if(item.Operand is not MethodReference call)throw new Unsupported();
                    int count=call.Parameters.Count;
                    if(count>state.Stack.Count)throw new Unsupported();
                    // Only proven, literal-only String.Concat is evaluated.
                    // Dynamic interpolation, arbitrary getters and format arguments
                    // remain unknown; partial phrases must not enter the catalogue.
                    if(item.OpCode.Code==Code.Call&&!call.HasThis&&call.DeclaringType.FullName=="System.String"
                        &&call.DeclaringType.Scope is AssemblyNameReference core&&core.Name is "mscorlib" or "System.Runtime" or "System.Private.CoreLib"
                        &&call.Name=="Concat"&&call.ReturnType.FullName=="System.String"&&count is >=2 and <=4
                        &&call.Parameters.All(p=>p.ParameterType.FullName=="System.String"))
                    {
                        var args=new Literal[]?[count];for(int n=count-1;n>=0;n--)args[n]=Pop(state);
                        // Branch alternatives can be correlated across arguments;
                        // taking their Cartesian product would invent sentences.
                        if(args.Any(a=>a is null||a.Length!=1)){Push(state,null);return;}
                        string joined=string.Concat(args.Select(a=>a![0].Text));
                        Push(state,joined.Length<=6000?[new(joined,item.Offset)]:null);return;
                    }
                    // The summary identifies the exact displayed argument. TMP
                    // may have a trailing synchronization flag, not display text.
                    int argument=displayArgument(call);
                    if(output is not null&&argument>=0&&argument<count&&state.Stack[state.Stack.Count-count+argument] is {} literals)
                        foreach(var literal in literals)output.Add(new(literal,call,item.Offset));
                    for(int n=0;n<count;n++)Pop(state);
                    if(call.HasThis&&item.OpCode.Code!=Code.Newobj)Pop(state);
                    if(item.OpCode.Code==Code.Newobj||call.ReturnType.FullName!="System.Void")
                    {
                        string? returned=item.OpCode.Code is Code.Call or Code.Callvirt&&count==0&&call.ReturnType.FullName=="System.String"?methodLiteral?.Invoke(call):null;
                        Push(state,returned is {Length:>0 and <=6000}&&!returned.Contains('\0')?[new(returned,item.Offset)]:null);
                    }
                    return;
                case Code.Calli: case Code.Jmp: throw new Unsupported();
                case Code.Leave: case Code.Leave_S:
                    state.Stack.Clear();
                    if(method.Body.ExceptionHandlers.Any(h=>h.HandlerType is ExceptionHandlerType.Finally or ExceptionHandlerType.Fault))
                        Array.Clear(state.Locals);
                    return;
                case Code.Ret: if(method.ReturnType.FullName!="System.Void")Pop(state);return;
                case Code.Castclass:
                    var cast=Pop(state);Push(state,item.Operand is TypeReference type&&type.FullName is "System.String" or "System.Object"?cast:null);return;
            }
            int pops=Count(item.OpCode.StackBehaviourPop,false),pushes=Count(item.OpCode.StackBehaviourPush,true);
            for(int n=0;n<pops;n++)Pop(state);for(int n=0;n<pushes;n++)Push(state,null);
        }
        IEnumerable<int> Successors(int start)
        {
            int end=ends[start];var last=code[end-1];
            if(last.OpCode.FlowControl is FlowControl.Return or FlowControl.Throw)yield break;
            if(last.Operand is Instruction target&&last.OpCode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)yield return indexes[target];
            else if(last.Operand is Instruction[] targets)foreach(var branch in targets)yield return indexes[branch];
            if(last.OpCode.FlowControl!=FlowControl.Branch&&end<code.Length)yield return end;
        }
        try
        {
            while(pending.Count>0)
            {
                check();int start=pending.Dequeue();queued.Remove(start);var state=states[start].Clone();
                for(int i=start;i<ends[start];i++)Transfer(state,code[i],null);
                foreach(int next in Successors(start))
                {
                    bool changed;
                    if(!states.TryGetValue(next,out var previous)){states[next]=state.Clone();changed=true;}
                    else changed=Merge(previous,state);
                    if(changed&&queued.Add(next))pending.Enqueue(next);
                }
            }
            // Emit only after all incoming branches have been merged. Emitting
            // during convergence would admit literals later tainted by a getter.
            var output=new List<Display>();
            foreach(int start in starts)
            {
                if(!states.TryGetValue(start,out var incoming))continue;var state=incoming.Clone();
                for(int i=start;i<ends[start];i++)Transfer(state,code[i],output);
            }
            return new(output.Distinct().ToArray(),false);
        }
        catch(Unsupported){return new([],false){UnsupportedReason="部分脚本文字流包含暂不支持的指令或栈结构"};}
        catch(OverflowException){return new([],true);}
    }
    private static int Count(StackBehaviour behavior,bool push)
    {
        if(behavior is StackBehaviour.Pop0 or StackBehaviour.Push0)return 0;
        string name=behavior.ToString();if(!name.StartsWith(push?"Push":"Pop",StringComparison.Ordinal))throw new Unsupported();
        return name.Split('_').Length;
    }
    private static bool Merge(State target,State incoming)
    {
        if(target.Stack.Count!=incoming.Stack.Count||target.Locals.Length!=incoming.Locals.Length)throw new Unsupported();
        bool changed=false;
        Literal[]? Join(Literal[]? left,Literal[]? right)
        {
            if(left is null)return null;
            if(right is null){changed=true;return null;}
            var union=left.Concat(right).Distinct().OrderBy(x=>x.Offset).ToArray();
            if(union.Length>8){changed=true;return null;}
            if(left.SequenceEqual(union))return left;changed=true;return union;
        }
        for(int i=0;i<target.Locals.Length;i++)target.Locals[i]=Join(target.Locals[i],incoming.Locals[i]);
        for(int i=0;i<target.Stack.Count;i++)target.Stack[i]=Join(target.Stack[i],incoming.Stack[i]);
        return changed;
    }
}
