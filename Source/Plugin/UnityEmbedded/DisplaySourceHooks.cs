using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Fusion.UnityEmbedded
{
    // A cache key is not evidence of a complete sentence. These hooks authorize
    // only the concrete literal -> display call currently executing. They never
    // change a game's upstream string argument or persist permission by text.
    internal sealed class DisplaySourceHooks : IDisposable
    {
        const int MaximumAssemblies=64,MaximumMethods=50000,MaximumInstructions=1000000;
        const int MaximumILBytes=16*1024*1024,MaximumMethodBytes=128*1024;
        const int MaximumPatchedMethods=256,MaximumSites=4096,MaximumMilliseconds=4000;
        const int MaximumDepth=4,MaximumSourceLength=6000;
        readonly Harmony harmony;readonly Action<string> log;readonly int ownerId;
        readonly HashSet<Assembly> scanned=new HashSet<Assembly>();
        readonly Dictionary<MethodBase,Plan> plans=new Dictionary<MethodBase,Plan>();
        readonly Dictionary<int,Site> sites=new Dictionary<int,Site>();
        bool disposed,scanningStopped;int nextSite,ilBytes,instructionsRead;
        internal int AssembliesScanned {get;private set;}
        internal int MethodsScanned {get;private set;}
        internal int CallSitesInstalled {get;private set;}
        internal int MethodsPatched {get;private set;}
        internal int Failures {get;private set;}
        internal int BudgetStops {get;private set;}
        static readonly object gate=new object();static int nextOwner;
        static readonly Dictionary<int,DisplaySourceHooks> owners=new Dictionary<int,DisplaySourceHooks>();
        static readonly Dictionary<MethodBase,DisplaySourceHooks> methodOwners=new Dictionary<MethodBase,DisplaySourceHooks>();
        static readonly OpCode[] shortCodes=new OpCode[256],longCodes=new OpCode[256];
        static readonly MethodInfo rewrite=typeof(DisplaySourceHooks).GetMethod("Rewrite",BindingFlags.Static|BindingFlags.NonPublic);
        static readonly MethodInfo enter=typeof(DisplaySourceHooks).GetMethod("Enter",BindingFlags.Static|BindingFlags.NonPublic);
        static readonly MethodInfo leave=typeof(DisplaySourceHooks).GetMethod("Leave",BindingFlags.Static|BindingFlags.NonPublic);
        [ThreadStatic] static Scope scope;
        sealed class Scope {internal DisplaySourceHooks Owner;internal Site Site;internal string Source;internal Scope Previous;internal int Depth;internal bool Allowed,Consumed;}
        sealed class Instruction {internal int Offset;internal OpCode Code;internal object Value;}
        sealed class Body {internal MethodBase Method;internal List<Instruction> Instructions;internal readonly HashSet<int> UnsafeEntries=new HashSet<int>();}
        sealed class Proof {internal MethodBase Sink;internal int Depth;}
        sealed class Site {internal int Id;internal string Source;internal MethodInfo Called;internal OpCode CallCode;internal Proof Proof;internal MethodInfo Wrapper;}
        sealed class Plan {internal readonly List<Site> Sites=new List<Site>();internal bool Installed;}
        sealed class BudgetException : Exception {}

        static DisplaySourceHooks()
        {
            foreach(var field in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))
            {if(field.FieldType!=typeof(OpCode))continue;var code=(OpCode)field.GetValue(null);ushort value=unchecked((ushort)code.Value);if(value<256)shortCodes[value]=code;else if((value&0xff00)==0xfe00)longCodes[value&255]=code;}
        }
        internal DisplaySourceHooks(Harmony harmony,Action<string> log)
        {
            if(harmony==null)throw new ArgumentNullException("harmony");this.harmony=harmony;this.log=log;
            lock(gate){ownerId=++nextOwner;owners.Add(ownerId,this);}
        }
        static bool Platform(string name)
        {
            return name=="mscorlib"||name=="netstandard"||name=="System"||name=="Microsoft.CSharp"||name=="0Harmony"
                ||name.StartsWith("System.",StringComparison.Ordinal)||name.StartsWith("Microsoft.",StringComparison.Ordinal)
                ||name.StartsWith("UnityEngine",StringComparison.Ordinal)||name.StartsWith("UnityEditor",StringComparison.Ordinal)
                ||name.StartsWith("Unity.TextMeshPro",StringComparison.Ordinal)||name=="TextMeshPro"
                ||name.StartsWith("BepInEx",StringComparison.Ordinal)||name.StartsWith("Mono.",StringComparison.Ordinal)||name.StartsWith("MonoMod",StringComparison.Ordinal)
                ||name.StartsWith("Harmony",StringComparison.Ordinal)||name.StartsWith("Il2Cpp",StringComparison.Ordinal);
        }
        void Check(Stopwatch watch)
        {if(disposed||watch.ElapsedMilliseconds>=MaximumMilliseconds||MethodsScanned>=MaximumMethods||instructionsRead>=MaximumInstructions||ilBytes>=MaximumILBytes)throw new BudgetException();}
        static bool ReferencesDisplayAssembly(Assembly assembly)
        {
            // Every admitted chain terminates in a direct Unity/TMP reference
            // inside the same module. Other loaded libraries cannot contribute.
            foreach(var reference in assembly.GetReferencedAssemblies())
                if(reference.Name=="UnityEngine"||reference.Name=="UnityEngine.UI"||reference.Name=="UnityEngine.TextRenderingModule"||reference.Name=="Unity.TextMeshPro"||reference.Name=="TextMeshPro")return true;
            return false;
        }
        internal void Install()
        {
            if(disposed||scanningStopped)return;var watch=Stopwatch.StartNew();bool didScan=false;
            try
            {
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if(assembly==typeof(DisplaySourceHooks).Assembly||assembly is AssemblyBuilder||scanned.Contains(assembly)||Platform(assembly.GetName().Name))continue;
                    // Mark even a non-display assembly so repeated periodic calls
                    // do no repeated metadata work or unchanged logging.
                    scanned.Add(assembly);bool hasDisplayReference;try{hasDisplayReference=ReferencesDisplayAssembly(assembly);}catch{Failures++;continue;}
                    if(!hasDisplayReference)continue;Check(watch);
                    if(AssembliesScanned>=MaximumAssemblies)throw new BudgetException();AssembliesScanned++;didScan=true;
                    try{Scan(assembly,watch);}catch(BudgetException){throw;}catch{Failures++;}
                }
            }
            catch(BudgetException){BudgetStops++;scanningStopped=true;didScan=true;}
            if(didScan&&log!=null)log("Unity complete display hooks assemblies="+AssembliesScanned+" methods="+MethodsPatched+" sites="+CallSitesInstalled+" failures="+Failures+" budgetStops="+BudgetStops);
        }
        void Scan(Assembly assembly,Stopwatch watch)
        {
            Type[] types;
            try{types=assembly.GetTypes();}catch(ReflectionTypeLoadException ex){types=ex.Types;}
            var bodies=new List<Body>();
            foreach(var type in types)
            {
                Check(watch);if(type==null||type.ContainsGenericParameters)continue;
                var methods=new List<MethodBase>();
                // Do not patch constructors/type initializers: detouring them can
                // trigger game initialization before the game chooses to run it.
                try{methods.AddRange(type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly));}
                catch{Failures++;continue;}
                foreach(var method in methods)
                {
                    Check(watch);MethodsScanned++;
                    if(method.IsAbstract||method.ContainsGenericParameters||method.IsGenericMethod)continue;
                    try{var body=Read(method,watch);if(body!=null)bodies.Add(body);}catch(BudgetException){throw;}catch{Failures++;}
                }
            }
            var proofs=new Dictionary<MethodBase,Proof>();
            for(int depth=0;depth<MaximumDepth;depth++)
            {
                var added=new Dictionary<MethodBase,Proof>();
                foreach(var body in bodies)
                {
                    Check(watch);var method=body.Method as MethodInfo;if(!SupportedCall(method)||proofs.ContainsKey(method))continue;
                    int final=method.GetParameters().Length-1;
                    bool changed=false;
                    foreach(var instruction in body.Instructions)
                        if((instruction.Code==OpCodes.Starg||instruction.Code==OpCodes.Starg_S||instruction.Code==OpCodes.Ldarga||instruction.Code==OpCodes.Ldarga_S)&&Convert.ToInt32(instruction.Value)==final+(method.IsStatic?0:1)){changed=true;break;}
                    if(changed)continue;
                    for(int i=1;i<body.Instructions.Count;i++)
                    {
                        var item=body.Instructions[i];var called=item.Value as MethodInfo;Proof proof;
                        if(!Call(item)||body.UnsafeEntries.Contains(item.Offset)||!SupportedCall(called)||!SinkProof(called,assembly,proofs,out proof)||!SafeDispatch(called,item.Code,proof)||LoadArgument(body.Instructions[i-1])!=final+(method.IsStatic?0:1))continue;
                        if(proof.Depth>=MaximumDepth)continue;added[method]=new Proof{Sink=proof.Sink,Depth=proof.Depth+1};break;
                    }
                }
                if(added.Count==0)break;foreach(var pair in added)proofs.Add(pair.Key,pair.Value);
            }
            foreach(var body in bodies)
            {
                Check(watch);var plan=new Plan();
                for(int i=0;i+1<body.Instructions.Count;i++)
                {
                    if((i&255)==0)Check(watch);var literal=body.Instructions[i];var call=body.Instructions[i+1];string source=literal.Value as string;Proof proof;var called=call.Value as MethodInfo;
                    if(literal.Code!=OpCodes.Ldstr||string.IsNullOrEmpty(source)||source.Length>MaximumSourceLength||source.IndexOf('\0')>=0||!Call(call)||body.UnsafeEntries.Contains(call.Offset)||!SupportedCall(called)||!SinkProof(called,assembly,proofs,out proof)||!SafeDispatch(called,call.Code,proof))continue;
                    if(nextSite>=MaximumSites||MethodsPatched>=MaximumPatchedMethods)throw new BudgetException();
                    var site=new Site{Id=++nextSite,Source=source,Called=called,CallCode=call.Code,Proof=proof};sites.Add(site.Id,site);plan.Sites.Add(site);
                }
                if(plan.Sites.Count==0)continue;
                lock(gate){if(methodOwners.ContainsKey(body.Method)){Failures++;continue;}methodOwners.Add(body.Method,this);plans.Add(body.Method,plan);}
                try
                {
                    harmony.Patch(body.Method,null,null,new HarmonyMethod(rewrite));
                    if(!plan.Installed)throw new InvalidOperationException("Display call shape changed before installation.");
                    MethodsPatched++;CallSitesInstalled+=plan.Sites.Count;
                }
                catch(Exception error)
                {
                    Failures++;try{harmony.Unpatch(body.Method,rewrite);}catch{}
                    lock(gate){methodOwners.Remove(body.Method);plans.Remove(body.Method);}
                    if(log!=null){while(error is TargetInvocationException&&error.InnerException!=null)error=error.InnerException;log("Unity complete display hook failed reason="+error.GetType().Name);}
                }
            }
        }
        static bool SupportedCall(MethodInfo method)
        {
            if(method==null||method.IsAbstract||method.IsGenericMethod||method.ContainsGenericParameters||method.ReturnType!=typeof(void)||method.DeclaringType==null||method.DeclaringType.IsValueType||method.DeclaringType.ContainsGenericParameters||(method.CallingConvention&CallingConventions.VarArgs)!=0)return false;
            var parameters=method.GetParameters();if(parameters.Length==0||parameters.Length>12||parameters[parameters.Length-1].ParameterType!=typeof(string))return false;
            foreach(var parameter in parameters)if(parameter.ParameterType.IsByRef||parameter.ParameterType.IsPointer||parameter.ParameterType.ContainsGenericParameters)return false;
            return true;
        }
        static bool StandardSetter(MethodInfo method)
        {
            if(!SupportedCall(method)||method.IsStatic||method.Name!="set_text"||method.GetParameters().Length!=1)return false;
            string type=method.DeclaringType.FullName,assembly=method.DeclaringType.Assembly.GetName().Name;
            return type=="UnityEngine.UI.Text"&&(assembly=="UnityEngine.UI"||assembly=="UnityEngine")
                ||type=="UnityEngine.TextMesh"&&(assembly=="UnityEngine.TextRenderingModule"||assembly=="UnityEngine")
                ||(type=="TMPro.TMP_Text"||type=="TMPro.TextMeshPro"||type=="TMPro.TextMeshProUGUI")&&(assembly=="Unity.TextMeshPro"||assembly=="TextMeshPro");
        }
        static bool SinkProof(MethodInfo called,Assembly assembly,Dictionary<MethodBase,Proof> proofs,out Proof proof)
        {if(StandardSetter(called)){proof=new Proof{Sink=called};return true;}if(called.DeclaringType.Assembly==assembly&&proofs.TryGetValue(called,out proof))return true;proof=null;return false;}
        // A virtual wrapper may dispatch to an uninspected override. Direct
        // known setters remain guarded by the actual __originalMethod identity.
        static bool SafeDispatch(MethodInfo called,OpCode code,Proof proof)
        {return proof.Depth==0||code==OpCodes.Call||!called.IsVirtual||called.IsFinal||called.DeclaringType.IsSealed;}
        static bool Call(Instruction instruction){return instruction.Code==OpCodes.Call||instruction.Code==OpCodes.Callvirt;}
        static int LoadArgument(Instruction item)
        {if(item.Code==OpCodes.Ldarg||item.Code==OpCodes.Ldarg_S)return Convert.ToInt32(item.Value);if(item.Code==OpCodes.Ldarg_0)return 0;if(item.Code==OpCodes.Ldarg_1)return 1;if(item.Code==OpCodes.Ldarg_2)return 2;if(item.Code==OpCodes.Ldarg_3)return 3;return -1;}
        static bool Same(MethodBase a,MethodBase b)
        {return a!=null&&b!=null&&a.Module==b.Module&&a.MetadataToken==b.MetadataToken&&a.DeclaringType==b.DeclaringType;}
        static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
        {
            var list=new List<CodeInstruction>(instructions);DisplaySourceHooks owner;Plan plan;
            lock(gate){if(!methodOwners.TryGetValue(__originalMethod,out owner)||owner.disposed||!owner.plans.TryGetValue(__originalMethod,out plan))return list;}
            plan.Installed=false;
            var positions=new List<int>();int expected=0;
            for(int i=0;i+1<list.Count;i++)
            {
                if(list[i].opcode!=OpCodes.Ldstr)continue;var next=list[i+1];
                if(next.opcode!=OpCodes.Call&&next.opcode!=OpCodes.Callvirt)continue;
                bool candidate=false;foreach(var site in plan.Sites)if((string)list[i].operand==site.Source&&Same(next.operand as MethodBase,site.Called)&&next.opcode==site.CallCode){candidate=true;break;}
                if(!candidate)continue;
                // Another patch can add a jump/exception entry that bypasses
                // the proven literal load. Do not authorize such call sites.
                if(next.labels.Count!=0||next.blocks.Count!=0)return list;
                if(expected>=plan.Sites.Count||(string)list[i].operand!=plan.Sites[expected].Source||!Same(next.operand as MethodBase,plan.Sites[expected].Called)||next.opcode!=plan.Sites[expected].CallCode)return list;
                positions.Add(i+1);expected++;
            }
            if(expected!=plan.Sites.Count)return list;
            for(int i=0;i<positions.Count;i++)
            {
                var site=plan.Sites[i];if(site.Wrapper==null)site.Wrapper=owner.Wrapper(site);
                // Retain branch labels and exception-block markers on the original
                // instruction. The replacement has exactly the same stack shape.
                var replacement=new CodeInstruction(list[positions[i]]);replacement.opcode=OpCodes.Call;replacement.operand=site.Wrapper;list[positions[i]]=replacement;
            }
            plan.Installed=true;return list;
        }
        MethodInfo Wrapper(Site site)
        {
            var parameters=new List<Type>();if(!site.Called.IsStatic)parameters.Add(site.Called.DeclaringType);foreach(var parameter in site.Called.GetParameters())parameters.Add(parameter.ParameterType);
            string name="FusionCompleteDisplay"+ownerId+"_"+site.Id;
            try
            {
                var method=new DynamicMethod(name,typeof(void),parameters.ToArray(),typeof(DisplaySourceHooks),true);
                EmitWrapper(method.GetILGenerator(),site,parameters.Count);return method;
            }
            catch(NotSupportedException)
            {
                // Some Unity Mono profiles expose Reflection.Emit but reject
                // dynamic exception blocks. Harmony already ships a portable
                // DMD/Cecil generator; keep the same call and finally contract.
                return PortableWrapper(name,site,parameters.ToArray());
            }
        }
        MethodInfo PortableWrapper(string name,Site site,Type[] parameters)
        {
            var type=Type.GetType("MonoMod.Utils.DynamicMethodDefinition, MonoMod.Utils",false);
            if(type==null)throw new NotSupportedException("Portable display wrapper generator is unavailable.");
            var constructor=type.GetConstructor(new[]{typeof(string),typeof(Type),typeof(Type[])});
            var generator=type.GetMethod("GetILGenerator",Type.EmptyTypes);var generate=type.GetMethod("Generate",Type.EmptyTypes);
            if(constructor==null||generator==null||generate==null)throw new NotSupportedException("Portable display wrapper generator API is unavailable.");
            object definition=null;
            try
            {
                definition=constructor.Invoke(new object[]{name,typeof(void),parameters});
                var il=generator.Invoke(definition,null) as ILGenerator;if(il==null)throw new NotSupportedException("Portable display wrapper IL generator is unavailable.");
                EmitWrapper(il,site,parameters.Length);
                var method=generate.Invoke(definition,null) as MethodInfo;
                if(method==null)throw new InvalidOperationException("Portable display wrapper generation returned no method.");
                return method;
            }
            finally
            {
                // Generate returns an independently owned runtime method. DMD's
                // temporary Cecil definition is not retained by each call site.
                var disposable=definition as IDisposable;if(disposable!=null)disposable.Dispose();
            }
        }
        void EmitWrapper(ILGenerator il,Site site,int parameterCount)
        {
            var token=il.DeclareLocal(typeof(Scope));
            il.Emit(OpCodes.Ldc_I4,ownerId);il.Emit(OpCodes.Ldc_I4,site.Id);il.Emit(OpCodes.Ldarg,(short)(parameterCount-1));il.Emit(OpCodes.Call,enter);il.Emit(OpCodes.Stloc,token);
            il.BeginExceptionBlock();for(int i=0;i<parameterCount;i++)il.Emit(OpCodes.Ldarg,(short)i);il.Emit(site.CallCode,site.Called);
            il.BeginFinallyBlock();il.Emit(OpCodes.Ldloc,token);il.Emit(OpCodes.Call,leave);il.EndExceptionBlock();il.Emit(OpCodes.Ret);
        }
        static Scope Enter(int ownerId,int siteId,string source)
        {
            DisplaySourceHooks owner;Site site;var token=new Scope{Previous=scope,Depth=scope==null?1:scope.Depth+1,Source=source};
            lock(gate)
                if(owners.TryGetValue(ownerId,out owner)&&!owner.disposed&&owner.sites.TryGetValue(siteId,out site))
                {token.Owner=owner;token.Site=site;token.Allowed=token.Depth<=MaximumDepth&&source==site.Source;}
            // A rejected/deep scope shadows rather than inherits its caller's
            // permission. Finally restores the exact previous scope on throws.
            scope=token;return token;
        }
        static void Leave(Scope token)
        {if(token==null)return;if(ReferenceEquals(scope,token))scope=token.Previous;else scope=null;}
        internal bool IsCompleteSource(MethodBase actualSetter,string source)
        {
            var current=scope;
            if(disposed||current==null||!current.Allowed||current.Consumed||!ReferenceEquals(current.Owner,this)||current.Source!=source||!Same(current.Site.Proof.Sink,actualSetter))return false;
            // Unity setters may invoke dirty/layout callbacks that write another
            // label with the same source. Only the first exact setter entered
            // from this verified call can consume the capability.
            current.Consumed=true;return true;
        }
        Body Read(MethodBase method,Stopwatch watch)
        {
            var body=method.GetMethodBody();if(body==null)return null;var bytes=body.GetILAsByteArray();if(bytes==null||bytes.Length==0)return null;
            if(bytes.Length>MaximumMethodBytes)return null;if(bytes.Length>MaximumILBytes-ilBytes)throw new BudgetException();ilBytes+=bytes.Length;
            var result=new Body{Method=method,Instructions=new List<Instruction>()};int at=0;
            while(at<bytes.Length)
            {
                if((result.Instructions.Count&255)==0)Check(watch);instructionsRead++;int start=at;byte first=bytes[at++];OpCode code=first==0xfe?longCodes[Take(bytes,ref at,1)[0]]:shortCodes[first];object value=null;
                if(code.Size==0)throw new InvalidOperationException("Invalid IL opcode.");
                switch(code.OperandType)
                {
                    case OperandType.InlineNone:break;
                    case OperandType.ShortInlineVar:value=(int)Take(bytes,ref at,1)[0];break;
                    case OperandType.InlineVar:value=(int)BitConverter.ToUInt16(Take(bytes,ref at,2),0);break;
                    case OperandType.ShortInlineI:value=(int)(sbyte)Take(bytes,ref at,1)[0];break;
                    case OperandType.InlineI:value=BitConverter.ToInt32(Take(bytes,ref at,4),0);break;
                    case OperandType.ShortInlineBrTarget:value=(int)(sbyte)Take(bytes,ref at,1)[0];result.UnsafeEntries.Add(at+(int)value);break;
                    case OperandType.InlineBrTarget:value=BitConverter.ToInt32(Take(bytes,ref at,4),0);result.UnsafeEntries.Add(at+(int)value);break;
                    case OperandType.InlineI8:value=BitConverter.ToInt64(Take(bytes,ref at,8),0);break;
                    case OperandType.ShortInlineR:value=BitConverter.ToSingle(Take(bytes,ref at,4),0);break;
                    case OperandType.InlineR:value=BitConverter.ToDouble(Take(bytes,ref at,8),0);break;
                    case OperandType.InlineString:value=method.Module.ResolveString(BitConverter.ToInt32(Take(bytes,ref at,4),0));break;
                    case OperandType.InlineMethod:
                        int member=BitConverter.ToInt32(Take(bytes,ref at,4),0);
                        // A missing unrelated dependency must not discard all of
                        // a coroutine's otherwise readable display call sites.
                        try{value=method.Module.ResolveMethod(member,method.DeclaringType==null?null:method.DeclaringType.GetGenericArguments(),method.IsGenericMethod?method.GetGenericArguments():null);}catch{value=null;}
                        break;
                    case OperandType.InlineSwitch:
                        int count=BitConverter.ToInt32(Take(bytes,ref at,4),0);if(count<0||count>(bytes.Length-at)/4)throw new InvalidOperationException("Invalid IL switch.");int switchBase=at+count*4;
                        for(int n=0;n<count;n++)result.UnsafeEntries.Add(switchBase+BitConverter.ToInt32(Take(bytes,ref at,4),0));break;
                    case OperandType.InlineField:case OperandType.InlineType:case OperandType.InlineTok:case OperandType.InlineSig:Take(bytes,ref at,4);break;
                    default:throw new InvalidOperationException("Unsupported IL operand.");
                }
                result.Instructions.Add(new Instruction{Offset=start,Code=code,Value=value});
            }
            foreach(var clause in body.ExceptionHandlingClauses)
            {
                result.UnsafeEntries.Add(clause.TryOffset);result.UnsafeEntries.Add(clause.TryOffset+clause.TryLength);
                result.UnsafeEntries.Add(clause.HandlerOffset);result.UnsafeEntries.Add(clause.HandlerOffset+clause.HandlerLength);
                if(clause.Flags==ExceptionHandlingClauseOptions.Filter)result.UnsafeEntries.Add(clause.FilterOffset);
            }
            return result;
        }
        static byte[] Take(byte[] bytes,ref int offset,int length)
        {if(offset<0||length<0||length>bytes.Length-offset)throw new InvalidOperationException("Truncated IL.");var result=new byte[length];Buffer.BlockCopy(bytes,offset,result,0,length);offset+=length;return result;}
        public void Dispose()
        {
            if(disposed)return;disposed=true;
            foreach(var pair in plans)try{harmony.Unpatch(pair.Key,rewrite);}catch{}
            lock(gate){owners.Remove(ownerId);foreach(var pair in plans){DisplaySourceHooks owner;if(methodOwners.TryGetValue(pair.Key,out owner)&&ReferenceEquals(owner,this))methodOwners.Remove(pair.Key);}}
            plans.Clear();sites.Clear();if(scope!=null&&ReferenceEquals(scope.Owner,this))scope=null;
        }
    }
}
