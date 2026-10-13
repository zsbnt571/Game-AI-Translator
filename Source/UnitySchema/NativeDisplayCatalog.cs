using Cpp2IL.Core.Model.Contexts;
using Iced.Intel;
using LibCpp2IL;
using System.Diagnostics;
using System.Text.RegularExpressions;

// Bounded, read-only x64 data-flow. Only values reaching a known display input
// are emitted. Metadata strings alone are never a catalogue.
internal sealed class NativeDisplayCatalog(ApplicationAnalysisContext app)
{
    internal sealed record Source(string Text,string Assembly,int Token,long Offset,string Sink);
    internal sealed record Result(Source[] Sources,string[] Warnings,int Methods);
    private sealed class Node(string kind,string key="",TypeAnalysisContext? type=null,int[]? children=null)
    { internal string Kind=kind,Key=key; internal TypeAnalysisContext? Type=type; internal int[] Children=children??[]; }
    private sealed class State
    {
        internal Dictionary<Register,int> Registers=new();internal Dictionary<long,int> StackValues=new();internal Dictionary<int,int> Contents=new();internal long Stack;
        internal State Copy()=>new(){Registers=new(Registers),StackValues=new(StackValues),Contents=new(Contents),Stack=Stack};
    }
    private readonly List<Node> nodes=[new("unknown")];
    private readonly Dictionary<string,int> interned=new(StringComparer.Ordinal);
    private readonly Dictionary<int,HashSet<int>> definitions=new();
    private readonly List<(int Value,MethodAnalysisContext Method,ulong Address,string Sink)> sinks=new();
    private readonly Dictionary<ulong,MethodAnalysisContext> calls=new();
    private readonly Dictionary<int,MethodAnalysisContext> methodRefs=new();
    private readonly HashSet<ulong> allocations=new();
    private readonly Dictionary<ulong,bool> listAddBodies=new();
    private readonly InstructionInfoFactory infoFactory=new();
    private readonly Stopwatch watch=Stopwatch.StartNew();
    private readonly List<string> warnings=new();
    private int visits,methods,resolutionVisits;
    private static readonly Register[] arguments=[Register.RCX,Register.RDX,Register.R8,Register.R9];
    private static readonly Register[] volatileRegisters=[Register.RAX,Register.RCX,Register.RDX,Register.R8,Register.R9,Register.R10,Register.R11];
    private int vtableOffset;
    private void Warn(string message){if(!warnings.Contains(message)&&warnings.Count<16)warnings.Add(message);}
    internal object Describe()=>new{Nodes=nodes.Select((n,id)=>new{Id=id,n.Kind,n.Key,Type=n.Type?.FullName,n.Children,Definitions=definitions.TryGetValue(id,out var set)?set.ToArray():[]}).Take(8000),Sinks=sinks.Select(s=>new{s.Value,Method=s.Method.FullNameWithSignature,s.Address,s.Sink})};
    private void Check(){if(watch.ElapsedMilliseconds>12000||nodes.Count>120000||visits>2500000)throw new InvalidOperationException("Native display analysis budget reached");}
    private int Make(string kind,string key="",TypeAnalysisContext? type=null,params int[] children)
    {
        string id=kind+"|"+key+"|"+type?.DeclaringAssembly?.CleanAssemblyName+"|"+type?.FullName+"|"+string.Join(',',children);
        if(interned.TryGetValue(id,out int old))return old;
        Check();int result=nodes.Count;nodes.Add(new(kind,key,type,children));interned.Add(id,result);return result;
    }
    private int Union(int a,int b)
    {
        if(a==b)return a;if(a==0)return b;if(b==0)return a;
        var children=(nodes[a].Kind=="union"?nodes[a].Children:[a]).Concat(nodes[b].Kind=="union"?nodes[b].Children:[b]).Distinct().Order().ToArray();
        if(children.Length>32){Warn("Native collection variant limit reached");return 0;}
        return Make("union",children:children);
    }
    private void Define(int target,int value)
    {if(target==0||value==0||target==value)return;if(!definitions.TryGetValue(target,out var set))definitions[target]=set=new();if(set.Count<128)set.Add(value);else Warn("Native value variant limit reached");}
    private static string MethodKey(MethodAnalysisContext m)=>m.DeclaringType.DeclaringAssembly.CleanAssemblyName+"|"+m.FullNameWithSignature;
    private static MethodAnalysisContext BaseMethod(MethodAnalysisContext m)=>m is ConcreteGenericMethodAnalysisContext g?g.BaseMethodContext:m;
    private int Freeze(State state,int value)=>state.Contents.TryGetValue(value,out int contents)?contents:value;
    private void Append(State state,int receiver,int value){if(receiver!=0&&value!=0)state.Contents[receiver]=Union(state.Contents.GetValueOrDefault(receiver),Freeze(state,value));}
    private int Parameter(MethodAnalysisContext m,int i)=>Make("param",MethodKey(m)+"#"+i,m.Parameters[i].ParameterType);
    private int Returned(MethodAnalysisContext m)=>Make("return",MethodKey(m),m.ReturnType);
    private int Field(FieldAnalysisContext f)=>Make("field",f.DeclaringType.DeclaringAssembly.CleanAssemblyName+"|"+f.DeclaringType.FullName+"::"+f.Name,f.FieldType);
    private static Register Register64(Register r)=>r==Register.None?Register.None:r.GetFullRegister();
    private static bool TextType(TypeAnalysisContext? t)=>t?.FullName is "TMPro.TMP_Text" or "TMPro.TextMeshPro" or "TMPro.TextMeshProUGUI" or "UnityEngine.UI.Text" or "UnityEngine.TextMesh";
    private static bool Platform(string n)=>n is "mscorlib" or "netstandard"||n.StartsWith("System",StringComparison.Ordinal)||n.StartsWith("Unity",StringComparison.Ordinal)||n.StartsWith("Microsoft",StringComparison.Ordinal)||n.StartsWith("Il2Cpp",StringComparison.Ordinal)||n.StartsWith("BepInEx",StringComparison.Ordinal)||n.StartsWith("Fusion",StringComparison.Ordinal);
    private static int SinkParameter(MethodAnalysisContext m)
    {
        if(TextType(m.DeclaringType)&&m.Name is "set_text" or "SetText"&&m.Parameters.Count>0&&m.Parameters[0].ParameterType.FullName=="System.String")return 0;
        // This is a library API, not a game/controller-name rule. Only its end
        // value is display text; scramble alphabets remain excluded.
        if(m.IsStatic&&m.DeclaringType.Namespace=="DG.Tweening"&&m.Name=="DOText"&&m.Parameters.Count>=2&&TextType(m.Parameters[0].ParameterType)&&m.Parameters[1].ParameterType.FullName=="System.String")return 1;
        return -1;
    }
    internal Result Read()
    {
        // Layout checked against the bundled Interop class handlers. Unsupported
        // layouts retain direct-call analysis and skip virtual-call inference.
        var version=app.UnityVersion;
        if(app.Binary.is32Bit||app.Binary.InstructionSetId!=DefaultInstructionSets.X86_64)return new([], ["Native display tracing currently supports Windows x64 only"],0);
        vtableOffset=(version.Major==2021&&version.Minor>=2)||(version.Major==2022)||(version.Major==2023&&version.Minor<=1)?312:0;
        var keys=app.GetOrCreateKeyFunctionAddresses();
        foreach(ulong address in new[]{keys.il2cpp_object_new,keys.il2cpp_vm_object_new,keys.il2cpp_codegen_object_new})if(address!=0)allocations.Add(address);
        foreach(var pair in app.MethodsByAddress)
        {
            var distinct=pair.Value.GroupBy(MethodKey).Select(g=>g.First()).ToArray();
            if(distinct.Length==1)calls[pair.Key]=distinct[0];
            else if(distinct.Length>0&&distinct.All(m=>m.DeclaringType.FullName=="UnityEngine.Random"&&m.Name is "Range" or "RandomRangeInt"&&m.ReturnType.FullName=="System.Int32"&&m.Parameters.Count==2))calls[pair.Key]=distinct.First();
        }
        try
        {
            foreach(var assembly in app.Assemblies.Where(a=>!Platform(a.CleanAssemblyName)&&a.Types.Any(t=>t.Fields.Any(f=>TextType(f.FieldType))||t.Methods.Any(m=>m.Parameters.Any(p=>TextType(p.ParameterType))))).OrderBy(a=>a.CleanAssemblyName.StartsWith("Assembly-CSharp",StringComparison.Ordinal)?0:1))
            foreach(var method in assembly.Types.SelectMany(t=>t.Methods))
            {
                Check();if(method.UnderlyingPointer==0||method.IsAbstract||method.Parameters.Count>16)continue;
                if(++methods>30000)throw new InvalidOperationException("Native method limit reached");
                try{Analyze(method);}catch(Exception e)when(e is not OutOfMemoryException)
                {if(warnings.Count<8)warnings.Add(e.GetType().Name+": unsupported native method skipped");Check();}
            }
        }
        catch(InvalidOperationException){warnings.Add("Native display analysis reached its bounded limit; remaining text uses realtime translation");}
        var result=new List<Source>();var seen=new HashSet<string>();int characters=0;
        try{foreach(var sink in sinks)
        foreach(var text in Resolve(sink.Value,new(),0,new()))
        {
            if(text.Length is <2 or >6000||!Regex.IsMatch(text,@"\p{L}")||text.Contains('\0')||!seen.Add(text))continue;
            if(result.Count>=50000||(characters+=text.Length)>1000000)throw new InvalidOperationException("Native catalogue size limit reached");
            result.Add(new(text,sink.Method.DeclaringType.DeclaringAssembly.CleanAssemblyName,unchecked((int)sink.Method.Definition.token),(long)(sink.Address-sink.Method.UnderlyingPointer),sink.Sink));
        }}catch(InvalidOperationException){warnings.Add("Native text resolution budget reached");}
        return new(result.ToArray(),warnings.Distinct().ToArray(),methods);
    }
    private FieldAnalysisContext? FindField(TypeAnalysisContext? type,long offset,bool isStatic=false)
    {
        for(int depth=0;type!=null&&depth<24;depth++,type=type.BaseType)
        {
            var field=type.Fields.FirstOrDefault(f=>f.IsStatic==isStatic&&f.Offset==offset);
            if(field!=null)return field;
        }
        return null;
    }
    private int Memory(State state,in Instruction ins)
    {
        if(ins.IsIPRelativeMemoryOperand)
        {
            ulong address=ins.IPRelativeMemoryAddress;
            string? literal=LibCpp2IlMain.GetLiteralByAddress(address);
            if(literal!=null)return literal.Length<=6000?Make("text",literal):0;
            var usage=LibCpp2IlMain.GetRawTypeGlobalByAddress(address);
            if(usage?.Type==MetadataUsageType.TypeInfo&&usage.AsType().baseType is {} def)return Make("class",type:app.ResolveContextForType(def));
            var methodUsage=LibCpp2IlMain.GetMethodGlobalByAddress(address);
            MethodAnalysisContext? method=methodUsage?.Type==MetadataUsageType.MethodRef?app.ResolveContextForMethod(methodUsage.AsGenericMethodRef()):methodUsage?.Type==MetadataUsageType.MethodDef?app.ResolveContextForMethod(methodUsage.AsMethod()):null;
            if(method!=null){int id=Make("method",MethodKey(method));methodRefs[id]=method;return id;}
            return 0;
        }
        var reg=Register64(ins.MemoryBase);long offset=unchecked((long)ins.MemoryDisplacement64);
        if(ins.MemoryIndex!=Register.None)return 0;
        if(reg==Register.RSP)return state.StackValues.GetValueOrDefault(state.Stack+offset);
        var value=nodes[state.Registers.GetValueOrDefault(reg)];
        if(value.Type==null)return 0;
        if(value.Kind=="class"&&vtableOffset!=0&&offset==0xB8)return Make("static",type:value.Type);
        if(value.Kind=="class")return 0;
        if(value.Kind!="static"&&offset==0)return Make("class",type:value.Type);
        var field=FindField(value.Type,offset,value.Kind=="static");
        // List backing storage belongs to this collection, never to all lists
        // sharing the generic _items field definition.
        if(field?.Name=="_items"&&value.Type.FullName.StartsWith("System.Collections.Generic.List",StringComparison.Ordinal))return Make("elements",children:[state.Registers.GetValueOrDefault(reg)]);
        return field==null?0:Field(field);
    }
    private int Operand(State state,in Instruction ins,int index)
    {
        return ins.GetOpKind(index) switch
        {
            OpKind.Register=>state.Registers.GetValueOrDefault(Register64(ins.GetOpRegister(index))),
            OpKind.Memory=>Memory(state,ins),_=>0
        };
    }
    private MethodAnalysisContext? Callee(State state,in Instruction ins)
    {
        if(ins.Op0Kind is OpKind.NearBranch64 or OpKind.NearBranch32)
        {
            // Shared generic native code is disambiguated by its actual hidden
            // MethodInfo argument. Never pick the first overload at an address.
            foreach(int id in state.Registers.Values.Concat(state.StackValues.Values))if(methodRefs.TryGetValue(id,out var generic))
            {
                int at=generic.Parameters.Count+(generic.IsStatic?0:1);int hidden=at<4?state.Registers.GetValueOrDefault(arguments[at]):state.StackValues.GetValueOrDefault(state.Stack+32+(at-4)*8);
                if(hidden!=id)continue;
                if(generic.UnderlyingPointer==ins.NearBranchTarget||(BaseMethod(generic).Definition!=null&&app.MethodsByAddress.TryGetValue(ins.NearBranchTarget,out var shared)&&shared.Any(m=>BaseMethod(m).Definition==BaseMethod(generic).Definition)))return generic;
                if(generic.Name=="Add"&&generic.DeclaringType.FullName=="System.Collections.Generic.List`1<String>"&&nodes[state.Registers.GetValueOrDefault(Register.RCX)].Type?.FullName.StartsWith("System.Collections.Generic.List",StringComparison.Ordinal)==true&&ListAddBody(ins.NearBranchTarget,BaseMethod(generic).DeclaringType))return generic;
            }
            if(calls.TryGetValue(ins.NearBranchTarget,out var direct))return direct;
            var receiver=nodes[state.Registers.GetValueOrDefault(Register.RCX)];
            if(receiver.Type?.FullName.StartsWith("System.Collections.Generic.List",StringComparison.Ordinal)==true&&app.MethodsByAddress.TryGetValue(ins.NearBranchTarget,out var alternatives)&&alternatives.All(m=>m.DeclaringType?.FullName.StartsWith("System.Collections.Generic.List",StringComparison.Ordinal)==true&&m.Name.EndsWith("get_Item",StringComparison.Ordinal)))return alternatives.First(m=>m.Name=="get_Item");
            return null;
        }
        if(vtableOffset==0||ins.Op0Kind!=OpKind.Memory||ins.MemoryIndex!=Register.None)return null;
        var klass=nodes[state.Registers.GetValueOrDefault(Register64(ins.MemoryBase))];
        long offset=unchecked((long)ins.MemoryDisplacement64)-vtableOffset;
        if(klass.Kind!="class"||klass.Type?.Definition==null||offset<0||offset%16!=0)return null;
        var table=klass.Type.Definition.VTable;int slot=(int)(offset/16);
        if(slot>=table.Length||table[slot].Type!=MetadataUsageType.MethodDef)return null;
        return app.ResolveContextForMethod(table[slot].AsMethod());
    }
    private bool ListAddBody(ulong address,TypeAnalysisContext? receiverType)
    {
        if(listAddBodies.TryGetValue(address,out bool old))return old;
        listAddBodies[address]=false;
        var items=receiverType?.Fields.FirstOrDefault(f=>f.Name=="_items"&&!f.IsStatic);
        if(items==null||items.Offset>256||!app.Binary.TryMapVirtualAddressToRaw(address,out long raw))return false;
        // Native generic sharing can leave this small Add implementation absent
        // from MethodsByAddress. Require both its hidden List<string>.Add metadata
        // (above) and the actual item write, not an address/name guess.
        var decoder=Decoder.Create(64,app.Binary.ReadByteArrayAtRawAddress(raw,128));decoder.IP=address;Register array=Register.None;
        for(int i=0;i<24;i++){
            decoder.Decode(out var ins);if(ins.IsInvalid||ins.Mnemonic==Mnemonic.Int3)break;
            // Generic definitions can report offset zero. In that case infer
            // the backing-storage load from this verified Add body itself;
            // never read runtime memory using an assumed List field offset.
            if(ins.Mnemonic==Mnemonic.Mov&&ins.Op0Kind==OpKind.Register&&ins.Op1Kind==OpKind.Memory&&ins.MemoryBase==Register.RCX&&ins.MemoryIndex==Register.None&&ins.MemoryDisplacement64>=16&&ins.MemoryDisplacement64<=256&&(items.Offset==0||ins.MemoryDisplacement64==(ulong)items.Offset))array=ins.Op0Register;
            if(array!=Register.None&&ins.Mnemonic==Mnemonic.Mov&&ins.Op0Kind==OpKind.Memory&&ins.MemoryBase==array&&ins.MemoryIndex!=Register.None&&ins.MemoryIndexScale==8&&ins.MemoryDisplacement64==32&&ins.Op1Kind==OpKind.Register&&ins.Op1Register==Register.RDX)return listAddBodies[address]=true;
        }return false;
    }
    private void Analyze(MethodAnalysisContext method)
    {
        method.EnsureRawBytes();var bytes=method.RawBytes.ToArray();if(bytes.Length is 0 or >262144)return;
        var decoder=Decoder.Create(64,bytes);decoder.IP=method.UnderlyingPointer;
        var instructions=new List<Instruction>();while(decoder.IP<method.UnderlyingPointer+(ulong)bytes.Length){decoder.Decode(out var ins);if(ins.IsInvalid)break;instructions.Add(ins);if(instructions.Count>=30000)return;}
        var byAddress=instructions.Select((ins,index)=>(ins.IP,index)).ToDictionary(p=>p.IP,p=>p.index);
        var states=new Dictionary<int,HashSet<string>>();var queue=new Queue<(int Index,State State)>();
        var initial=new State();if(!method.IsStatic)initial.Registers[Register.RCX]=Make("this",method.FullNameWithSignature,method.DeclaringType);
        for(int p=0;p<method.Parameters.Count;p++){int at=p+(method.IsStatic?0:1);if(at<4)initial.Registers[arguments[at]]=Parameter(method,p);}
        void Schedule(int index,State next)
        {
            if(index<0||index>=instructions.Count)return;
            if(!states.TryGetValue(index,out var seen))states[index]=seen=new();
            string signature=next.Stack+"|"+string.Join(',',next.Registers.Where(p=>p.Value!=0).OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))+"|"+string.Join(',',next.StackValues.Where(p=>p.Value!=0).OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value))+"|"+string.Join(',',next.Contents.OrderBy(p=>p.Key).Select(p=>p.Key+"="+p.Value));
            if(seen.Count>=64){if(!warnings.Contains("Native branch variant limit reached"))warnings.Add("Native branch variant limit reached");return;}
            if(!seen.Add(signature))return;queue.Enqueue((index,next.Copy()));
        }
        Schedule(0,initial);
        int localVisits=0;
        while(queue.Count>0)
        {
            Check();if(++localVisits>80000){Warn("Native method traversal limit reached");break;}visits++;
            var work=queue.Dequeue();int index=work.Index;var state=work.State;var ins=instructions[index];
            // MSVC x64 relative jump tables: mov r32,[image+index*4+table];
            // add r64,image; jmp r64. Only schedule decoded in-method targets,
            // bounded by the preceding checked index. Preserve incoming state.
            if(ins.FlowControl==FlowControl.IndirectBranch&&index>=2&&instructions[index-1].Mnemonic==Mnemonic.Add&&instructions[index-2].Mnemonic==Mnemonic.Mov)
            {
                var read=instructions[index-2];var add=instructions[index-1];
                if(read.Op1Kind==OpKind.Memory&&read.MemoryIndexScale==4&&Register64(read.Op0Register)==Register64(ins.Op0Register)&&Register64(add.Op0Register)==Register64(ins.Op0Register))
                {
                    int baseId=state.Registers.GetValueOrDefault(Register64(add.Op1Register));
                    int bound=-1;
                    for(int back=index-3;back>=Math.Max(0,index-12);back--)if(instructions[back].Mnemonic==Mnemonic.Cmp&&instructions[back].Op0Kind==OpKind.Register&&instructions[back].Op1Kind is not (OpKind.Register or OpKind.Memory)&&Register64(instructions[back].Op0Register)==Register64(read.MemoryIndex)&&instructions[back+1].Mnemonic==Mnemonic.Ja){bound=(int)instructions[back].GetImmediate(1);break;}
                    if(nodes[baseId].Kind=="address"&&bound is >=0 and <128)
                    {
                        ulong image=ulong.Parse(nodes[baseId].Key);ulong table=image+read.MemoryDisplacement64;
                        var raw=app.Binary.ReadByteArrayAtRawAddress(app.Binary.MapVirtualAddressToRaw(table),checked((bound+1)*4));var destinations=new List<int>();
                        for(int j=0;j<=bound;j++){ulong address=image+(ulong)(uint)BitConverter.ToInt32(raw,j*4);if(!byAddress.TryGetValue(address,out int targetIndex)){destinations.Clear();break;}destinations.Add(targetIndex);}
                        foreach(int targetIndex in destinations)Schedule(targetIndex,state);
                    }
                }
                continue;
            }
            if(ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall||ins.FlowControl is FlowControl.UnconditionalBranch or FlowControl.IndirectBranch&&!byAddress.ContainsKey(ins.NearBranchTarget))
            {
                var target=Callee(state,ins);int result=0;
                if(target!=null)
                {
                    var values=new int[target.Parameters.Count];
                    for(int p=0;p<values.Length;p++){int at=p+(target.IsStatic?0:1);values[p]=Freeze(state,at<4?state.Registers.GetValueOrDefault(arguments[at]):state.StackValues.GetValueOrDefault(state.Stack+32+(at-4)*8));Define(Parameter(BaseMethod(target),p),values[p]);}
                    int sink=SinkParameter(target);if(sink>=0&&sink<values.Length&&values[sink]!=0)sinks.Add((values[sink],method,ins.IP,target.FullNameWithSignature));
                    if(target.DeclaringType.FullName=="System.String"&&target.Name=="Concat"&&target.IsStatic&&values.Length is >=2 and <=4&&target.Parameters.All(p=>p.ParameterType.FullName=="System.String"))
                        result=Make("concat",children:values);
                    else if(target.ReturnType.FullName!="System.Void")result=Make("invoke",MethodKey(BaseMethod(target)),target.ReturnType,new[]{Returned(BaseMethod(target))}.Concat(values.SelectMany((v,p)=>new[]{Parameter(BaseMethod(target),p),v})).ToArray());
                    if(target.DeclaringType.FullName=="UnityEngine.Random"&&target.Name is "Range" or "RandomRangeInt"&&target.ReturnType.FullName=="System.Int32")result=Make("random",MethodKey(method)+"@"+ins.IP);
                    if(target.DeclaringType.FullName.StartsWith("System.Collections.Generic.List",StringComparison.Ordinal))
                    {
                        int receiver=state.Registers.GetValueOrDefault(Register.RCX);
                        if(target.Name is "Add" or "AddWithResize"&&values.Length==1)Append(state,receiver,values[0]);
                        if(target.Name=="get_Item")result=nodes[state.Registers.GetValueOrDefault(Register.RDX)].Kind=="random"?Make("pick",children:[Freeze(state,receiver)]):Freeze(state,receiver);
                        if(target.Name==".ctor"&&receiver!=0)nodes[receiver].Type=target.DeclaringType;
                    }
                }
                else
                {
                    int klass=state.Registers.GetValueOrDefault(Register.RCX);
                    if(allocations.Contains(ins.NearBranchTarget)&&nodes[klass].Kind=="class")result=Make("call",MethodKey(method)+"@"+ins.IP,nodes[klass].Type);
                }
                foreach(var reg in volatileRegisters)state.Registers.Remove(reg);
                if(result!=0)state.Registers[Register.RAX]=result;
                if(ins.FlowControl is FlowControl.UnconditionalBranch or FlowControl.IndirectBranch){if(target!=null)Define(Returned(method),Freeze(state,result));continue;}
            }
            else if(ins.FlowControl==FlowControl.Return){Define(Returned(method),Freeze(state,state.Registers.GetValueOrDefault(Register.RAX)));continue;}
            else if(ins.Mnemonic==Mnemonic.Mov)
            {
                int value=Operand(state,ins,1);
                if(ins.Op0Kind==OpKind.Register)state.Registers[Register64(ins.Op0Register)]=value;
                else if(ins.Op0Kind==OpKind.Memory)
                {
                    if(Register64(ins.MemoryBase)==Register.RSP&&ins.MemoryIndex==Register.None)state.StackValues[state.Stack+unchecked((long)ins.MemoryDisplacement64)]=value;
                    else if(ins.MemoryIndex!=Register.None&&ins.MemoryIndexScale==8&&ins.MemoryDisplacement64==32)
                    {int array=state.Registers.GetValueOrDefault(Register64(ins.MemoryBase));if(nodes[array].Kind=="elements")Append(state,nodes[array].Children[0],value);}
                    else Define(Memory(state,ins),Freeze(state,value));
                }
            }
            else if(ins.Mnemonic==Mnemonic.Lea&&ins.IsIPRelativeMemoryOperand)state.Registers[Register64(ins.Op0Register)]=Make("address",ins.IPRelativeMemoryAddress.ToString());
            else if(ins.Mnemonic==Mnemonic.Add&&ins.Op0Kind==OpKind.Register&&ins.Op1Kind==OpKind.Register&&index+1<instructions.Count&&instructions[index+1].FlowControl==FlowControl.IndirectBranch){state.Registers.Remove(Register64(ins.Op0Register));}
            else if(ins.Mnemonic==Mnemonic.Push){state.Stack-=8;state.StackValues[state.Stack]=Operand(state,ins,0);}
            else if(ins.Mnemonic==Mnemonic.Pop){if(ins.Op0Kind==OpKind.Register)state.Registers[Register64(ins.Op0Register)]=state.StackValues.GetValueOrDefault(state.Stack);state.Stack+=8;}
            else if(ins.OpCount>=2&&ins.Op0Kind==OpKind.Register&&ins.Op0Register==Register.RSP&&ins.Op1Kind is not (OpKind.Register or OpKind.Memory)&&ins.Mnemonic is Mnemonic.Add or Mnemonic.Sub)
                state.Stack+=(ins.Mnemonic==Mnemonic.Sub?-1:1)*(long)ins.GetImmediate(1);
            else if(ins.FlowControl is not (FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch))
            {
                var info=infoFactory.GetInfo(ins);foreach(var reg in info.GetUsedRegisters())if(reg.Access is OpAccess.Write or OpAccess.ReadWrite or OpAccess.CondWrite or OpAccess.ReadCondWrite)state.Registers.Remove(Register64(reg.Register));
            }
            if(ins.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
            {if(byAddress.TryGetValue(ins.NearBranchTarget,out int jump))Schedule(jump,state);if(ins.FlowControl==FlowControl.UnconditionalBranch)continue;}
            if(ins.FlowControl is FlowControl.IndirectBranch or FlowControl.Exception)continue;
            Schedule(index+1,state);
        }
    }
    private HashSet<string> Resolve(int id,HashSet<int> path,int depth,Dictionary<int,int> bindings)
    {
        if(++resolutionVisits>500000||watch.ElapsedMilliseconds>20000)throw new InvalidOperationException("Native resolution limit");
        var output=new HashSet<string>(StringComparer.Ordinal);if(id==0||depth>48||!path.Add(id))return output;
        var node=nodes[id];
        if(bindings.TryGetValue(id,out int bound)&&bound!=id){path.Remove(id);return Resolve(bound,path,depth+1,new Dictionary<int,int>(bindings.Where(p=>p.Key!=id)));}
        if(node.Kind=="text")output.Add(node.Key);
        else if(node.Kind=="invoke")
        {
            var next=new Dictionary<int,int>(bindings);for(int i=1;i+1<node.Children.Length;i+=2)next[node.Children[i]]=node.Children[i+1];
            output=Resolve(node.Children[0],path,depth+1,next);
        }
        else if(node.Kind=="concat")
        {
            output.Add("");foreach(int child in node.Children)
            {
                var suffix=Resolve(child,path,depth+1,bindings);if(suffix.Count==0){output.Clear();break;}
                // Multi-valued operands may be correlated by a branch. Do not
                // invent Cartesian combinations which the game never displays.
                if(output.Count>1&&suffix.Count>1&&node.Children.Any(c=>!IndependentPick(c))){output.Clear();break;}
                var joined=new HashSet<string>();foreach(string left in output)foreach(string right in suffix)if(left.Length+right.Length<=6000&&joined.Count<128)joined.Add(left+right);output=joined;
            }
        }
        else
        {
            IEnumerable<int> children=node.Kind is "union" or "elements" or "pick"?node.Children:definitions.TryGetValue(id,out var set)?set:[];
            foreach(int child in children){output.UnionWith(Resolve(child,path,depth+1,bindings));if(output.Count>4096){output.Clear();break;}}
        }
        path.Remove(id);return output;
    }
    private bool IndependentPick(int id)
    {
        var node=nodes[id];if(node.Kind!="invoke"||!definitions.TryGetValue(node.Children[0],out var returns)||returns.Count!=1)return false;
        var value=nodes[returns.First()];return value.Kind=="pick"&&nodes[value.Children[0]].Kind=="param";
    }
}
