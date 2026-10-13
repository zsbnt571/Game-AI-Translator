using System.Diagnostics;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ScreenshotTranslationUiTester;

// This evidence describes a static display argument, not permission to bypass a
// runtime typewriter or permission to write an arbitrary object. Keep the origin
// so future reuse can be tied to the actual game build and display source.
internal sealed record EmbeddedCatalogSource(string Text,string Kind,string File,string ContentHash,
    string ModuleId,int MethodToken,int Offset,string Sink,string[] DisplayPath);

internal static class UnityManagedDisplayCatalog
{
    internal sealed record Limits(int Assemblies=256,int AssemblyBytes=32*1024*1024,int TotalBytes=256*1024*1024,
        int Types=100000,int Methods=200000,int Instructions=4000000,int MethodInstructions=100000,
        int WrapperDepth=4,int Entries=50000,int TextCharacters=8000000,int Milliseconds=8000);
    internal sealed record Result(EmbeddedCatalogSource[] Sources,int Files,string[] Warnings,bool Completed)
    {internal CatalogReadIssue[] Issues {get;init;}=[];}
    private sealed record Body(MethodDefinition Method,Instruction[] Instructions);
    private sealed record Wrapper(string[] Path);

    // Resolver use is prohibited. Parsing metadata and IL does not load or invoke
    // a game's assembly; a missing/foreign reference cannot trigger a disk search.
    private sealed class NoResolve : IAssemblyResolver
    {
        public AssemblyDefinition Resolve(AssemblyNameReference name)=>throw new AssemblyResolutionException(name);
        public AssemblyDefinition Resolve(AssemblyNameReference name,ReaderParameters parameters)=>throw new AssemblyResolutionException(name);
        public void Dispose(){}
    }
    private sealed class BudgetExceeded : Exception { internal BudgetExceeded(string message):base(message){} }

    internal static Result Read(string managed,CancellationToken cancellation,Limits? limits=null)
    {
        limits??=new();
        // Complete the established pass first. Extra flow analysis has its own
        // bounded budget and cannot crowd out a later assembly's old entries.
        var direct=ReadPass(managed,cancellation,limits,false,[]);
        int remaining=limits.Entries-direct.Sources.Length;
        int characters=limits.TextCharacters-direct.Sources.Sum(s=>s.Text.Length);
        if(remaining<=0||characters<=0)return direct with{Completed=false,
            Warnings=[..direct.Warnings,"脚本显示文字达到本次容量上限"],
            Issues=[..direct.Issues,new(CatalogIssueKind.Limited,"Managed","脚本显示文字达到本次容量上限")]};
        var known=direct.Sources.Select(Key).ToHashSet(StringComparer.Ordinal);
        var flow=ReadPass(managed,cancellation,limits with{Entries=remaining,TextCharacters=characters},true,known);
        return new([..direct.Sources,..flow.Sources],Math.Max(direct.Files,flow.Files),
            direct.Warnings.Concat(flow.Warnings).Distinct(StringComparer.Ordinal).ToArray(),direct.Completed&&flow.Completed)
            {Issues=direct.Issues.Concat(flow.Issues).Distinct().ToArray()};
    }
    private static string Key(EmbeddedCatalogSource source)=>$"{source.File}|{source.ContentHash}|{source.MethodToken}|{source.Offset}|{source.Sink}";
    private static Result ReadPass(string managed,CancellationToken cancellation,Limits limits,bool flowPass,HashSet<string> known)
    {
        var watch=Stopwatch.StartNew();var sources=new List<EmbeddedCatalogSource>();var warnings=new List<string>();var issues=new List<CatalogReadIssue>();
        cancellation.ThrowIfCancellationRequested();
        int files=0,bytes=0,types=0,methods=0,instructions=0,textCharacters=0;bool complete=true;
        void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if(watch.ElapsedMilliseconds>limits.Milliseconds)throw new BudgetExceeded("脚本显示文字分析达到时间上限");
        }
        void Warn(string text,CatalogIssueKind kind=CatalogIssueKind.Failed){complete=false;if(warnings.Count<32){warnings.Add(text);issues.Add(new(kind,"Managed",text));}}
        if(!Directory.Exists(managed))return new([],0,[],true);
        try
        {
            if((File.GetAttributes(managed)&FileAttributes.ReparsePoint)!=0)return new([],0,["脚本目录是链接，未展开"],false){Issues=[new(CatalogIssueKind.Unsupported,"Managed","脚本目录是链接，未展开")]};
            var paths=new List<string>();
            // Stop during enumeration, before materializing an unbounded list.
            foreach(string path in Directory.EnumerateFiles(managed,"*.dll",SearchOption.TopDirectoryOnly))
            {
                Check();if(paths.Count>=limits.Assemblies)throw new BudgetExceeded("脚本程序集数量达到本次上限");paths.Add(path);
            }
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            foreach(string path in paths)
            {
                Check();string name=Path.GetFileName(path);
                // Known platform libraries do not supply game display sources.
                if(PlatformAssembly(Path.GetFileNameWithoutExtension(path)))continue;
                try
                {
                    var info=new FileInfo(path);
                    if((info.Attributes&FileAttributes.ReparsePoint)!=0){Warn(name+"：程序集是链接，未展开",CatalogIssueKind.Unsupported);continue;}
                    if(info.Length>limits.AssemblyBytes){Warn(name+"：程序集超过本次大小上限",CatalogIssueKind.Limited);continue;}
                    if(info.Length>limits.TotalBytes-bytes)throw new BudgetExceeded("脚本程序集总容量达到本次上限");
                    // One bounded immutable image binds provenance to exactly the
                    // bytes Cecil reads, even if the file changes during a scan.
                    byte[] image;
                    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                    {
                        long length=stream.Length;if(length>limits.AssemblyBytes||length>limits.TotalBytes-bytes)throw new BudgetExceeded("脚本程序集读取大小已改变");
                        image=new byte[checked((int)length)];stream.ReadExactly(image);
                    }
                    bytes+=image.Length;Check();string hash=Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
                    using var memory=new MemoryStream(image,false);using var resolver=new NoResolve();
                    using var assembly=AssemblyDefinition.ReadAssembly(memory,new ReaderParameters{ReadSymbols=false,ReadingMode=ReadingMode.Deferred,AssemblyResolver=resolver});
                    files++;var bodies=new List<Body>();var todo=new Stack<TypeDefinition>(assembly.MainModule.Types.Reverse());
                    while(todo.Count>0)
                    {
                        Check();if(++types>limits.Types)throw new BudgetExceeded("脚本类型数量达到本次上限");
                        var type=todo.Pop();foreach(var nested in type.NestedTypes.Reverse())todo.Push(nested);
                        foreach(var method in type.Methods)
                        {
                            Check();if(++methods>limits.Methods)throw new BudgetExceeded("脚本方法数量达到本次上限");
                            if(!method.HasBody)continue;
                            var body=method.Body;
                            if(body.Instructions.Count>limits.MethodInstructions){Warn(name+"：大型脚本方法未展开",CatalogIssueKind.Limited);continue;}
                            if(body.Instructions.Count>limits.Instructions-instructions)throw new BudgetExceeded("脚本指令数量达到本次上限");
                            instructions+=body.Instructions.Count;bodies.Add(new(method,body.Instructions.ToArray()));
                        }
                    }
                    var constants=LiteralFields(bodies,Check);
                    var constantMethods=LiteralMethods(bodies,Check);
                    string? FieldLiteral(FieldReference field)
                    {
                        bool local=field.DeclaringType.Scope is ModuleDefinition module&&module==assembly.MainModule
                            ||field.DeclaringType.Scope is AssemblyNameReference scope&&scope.FullName==assembly.Name.FullName;
                        return local?constants.GetValueOrDefault(field.FullName):null;
                    }
                    string? MethodLiteral(MethodReference method)
                    {
                        bool local=method.DeclaringType.Scope is ModuleDefinition module&&module==assembly.MainModule
                            ||method.DeclaringType.Scope is AssemblyNameReference scope&&scope.FullName==assembly.Name.FullName;
                        return local&&!method.ContainsGenericParameter?constantMethods.GetValueOrDefault(method.FullName):null;
                    }
                    var wrappers=new Dictionary<string,Wrapper>(StringComparer.Ordinal);
                    // A wrapper summary is admitted only when its final string
                    // parameter is forwarded without transformation immediately
                    // into a known display argument. Each pass uses the previous
                    // pass, making the advertised depth bound real.
                    for(int depth=0;depth<limits.WrapperDepth;depth++)
                    {
                        var additions=new Dictionary<string,Wrapper>(StringComparer.Ordinal);
                        foreach(var body in bodies)
                        {
                            Check();var method=body.Method;
                            if(wrappers.ContainsKey(method.FullName)||method.HasGenericParameters||method.Parameters.Count==0
                                ||method.Parameters[^1].ParameterType.FullName!="System.String")continue;
                            // A load at the sink is not proof of forwarding if the
                            // argument was reassigned or its address escaped.
                            if(body.Instructions.Any(item=>item.OpCode.Code is Code.Starg or Code.Starg_S or Code.Ldarga or Code.Ldarga_S
                                &&item.Operand is ParameterDefinition parameter&&parameter.Index==method.Parameters.Count-1))continue;
                            for(int i=1;i<body.Instructions.Length;i++)
                            {
                                if((i&255)==0)Check();var item=body.Instructions[i];
                                if(!Call(item,out var call)||!SinkPath(call,assembly,wrappers,out var sinkPath))continue;
                                if(DisplaySetter(call)&&call.Parameters.Count!=1)continue;
                                if(ForwardedParameter(body.Instructions[i-1],method)!=method.Parameters.Count-1)continue;
                                additions[method.FullName]=new([method.FullName,..sinkPath]);break;
                            }
                        }
                        if(additions.Count==0)break;foreach(var pair in additions)wrappers.Add(pair.Key,pair.Value);
                    }
                    foreach(var body in bodies)
                    {
                        var emitted=new HashSet<(int Offset,string Sink)>();
                        void AddSource(string text,int offset,string[] sinkPath,string kind)
                        {
                            if(!emitted.Add((offset,sinkPath[^1])))return;
                            var source=new EmbeddedCatalogSource(text,kind,name,hash,assembly.MainModule.Mvid.ToString("D"),
                                body.Method.MetadataToken.ToInt32(),offset,sinkPath[^1],[body.Method.FullName,..sinkPath]);
                            if(known.Contains(Key(source)))return;
                            if(sources.Count>=limits.Entries||text.Length>limits.TextCharacters-textCharacters)throw new BudgetExceeded("脚本显示文字达到本次容量上限");
                            textCharacters+=text.Length;
                            sources.Add(source);
                        }
                        Check();for(int i=0;!flowPass&&i+1<body.Instructions.Length;i++)
                        {
                            if((i&255)==0)Check();var item=body.Instructions[i];
                            if(item.OpCode.Code!=Code.Ldstr||item.Operand is not string text||string.IsNullOrWhiteSpace(text)||text.Length>6000||text.Contains('\0'))continue;
                            // No lexical scan for arbitrary ldstr. The immediately
                            // following operation must consume it as a proved
                            // display string (direct setter or summarized wrapper).
                            if(!Call(body.Instructions[i+1],out var call)||!SinkPath(call,assembly,wrappers,out var sinkPath))continue;
                            if(DisplaySetter(call)&&call.Parameters.Count!=1)continue;
                            AddSource(text,item.Offset,sinkPath,"managed-display-literal");
                        }
                        if(!flowPass||!body.Instructions.Any(item=>item.OpCode.Code==Code.Ldstr||item.OpCode.Code==Code.Ldsfld&&item.Operand is FieldReference field&&FieldLiteral(field) is not null
                            ||Call(item,out var method)&&MethodLiteral(method) is not null)
                            ||!body.Instructions.Any(item=>Call(item,out var call)&&SinkPath(call,assembly,wrappers,out _)))continue;
                        var flow=UnityManagedLiteralFlow.Read(body.Method,call=>SinkPath(call,assembly,wrappers,out _)?DisplaySetter(call)?0:call.Parameters.Count-1:-1,Check,FieldLiteral,MethodLiteral);
                        if(flow.Limited)Warn(name+"：部分方法的文字流分析达到本次上限",CatalogIssueKind.Limited);
                        if(flow.UnsupportedReason is not null&&!warnings.Contains(name+"："+flow.UnsupportedReason))Warn(name+"："+flow.UnsupportedReason,CatalogIssueKind.Unsupported);
                        foreach(var display in flow.Displays)
                            if(!string.IsNullOrWhiteSpace(display.Literal.Text)&&SinkPath(display.Call,assembly,wrappers,out var sinkPath))
                                AddSource(display.Literal.Text,display.Literal.Offset,sinkPath,"managed-display-flow-literal");
                    }
                }
                catch(OperationCanceledException){throw;}
                catch(BudgetExceeded){throw;}
                catch(Exception ex)when(ex is not OutOfMemoryException)
                {Warn(name+"：脚本元数据未能可靠读取");}
            }
        }
        catch(OperationCanceledException){throw;}
        catch(BudgetExceeded ex){Warn(ex.Message,CatalogIssueKind.Limited);}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){Warn("脚本目录未能完整读取");}
        return new(sources.ToArray(),files,warnings.ToArray(),complete){Issues=issues.Distinct().ToArray()};
    }
    private static bool PlatformAssembly(string name)=>name is "mscorlib" or "netstandard" or "System" or "Microsoft.CSharp" or "0Harmony"
        ||name.StartsWith("System.",StringComparison.Ordinal)||name.StartsWith("Microsoft.",StringComparison.Ordinal)
        ||name.StartsWith("UnityEngine",StringComparison.Ordinal)||name.StartsWith("UnityEditor",StringComparison.Ordinal)
        ||name.StartsWith("BepInEx",StringComparison.Ordinal)||name.StartsWith("Mono.",StringComparison.Ordinal);
    private static Dictionary<string,string> LiteralFields(List<Body> bodies,Action check)
    {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        var writes=new Dictionary<string,int>(StringComparer.Ordinal);var escaped=new HashSet<string>(StringComparer.Ordinal);
        foreach(var body in bodies)
        {
            check();
            foreach(var item in body.Instructions)
            {
                if(item.Operand is not FieldReference field)continue;
                if(item.OpCode.Code==Code.Stsfld)writes[field.FullName]=writes.GetValueOrDefault(field.FullName)+1;
                if(item.OpCode.Code==Code.Ldsflda)escaped.Add(field.FullName);
            }
            if(body.Method.Name!=".cctor"||!body.Method.IsStatic||body.Method.Body.HasExceptionHandlers)continue;
            var code=body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
            if(code.Length<3||code[^1].OpCode.Code!=Code.Ret||(code.Length-1)%2!=0)continue;
            // Admit only straight-line, literal-only initializers. Calls,
            // branches, mutable fields and address escapes stay unknown.
            bool pure=true;
            for(int i=0;i+1<code.Length;i+=2)
                if(code[i].OpCode.Code!=Code.Ldstr||code[i].Operand is not string text||text.Length is 0 or >6000||text.Contains('\0')
                    ||code[i+1].OpCode.Code!=Code.Stsfld||code[i+1].Operand is not FieldDefinition f||!f.IsStatic||!f.IsInitOnly
                    ||f.DeclaringType!=body.Method.DeclaringType||f.FieldType.FullName!="System.String"){pure=false;break;}
            if(pure)for(int i=0;i+1<code.Length;i+=2)values[((FieldReference)code[i+1].Operand).FullName]=(string)code[i].Operand;
        }
        foreach(string key in values.Keys.ToArray())if(writes.GetValueOrDefault(key)!=1||escaped.Contains(key))values.Remove(key);
        return values;
    }
    private static Dictionary<string,string> LiteralMethods(List<Body> bodies,Action check)
    {
        var values=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var body in bodies)
        {
            check();var method=body.Method;
            // Only a local, non-dispatching, argument-free literal return.
            // Nothing is invoked, no field is read, and no branch is guessed.
            // A virtual getter may be overridden even when this body is pure.
            if(method.IsVirtual||method.HasGenericParameters||method.DeclaringType.HasGenericParameters
                ||method.Parameters.Count!=0||method.ReturnType.FullName!="System.String"||method.Body.HasExceptionHandlers)continue;
            var code=body.Instructions.Where(i=>i.OpCode.Code!=Code.Nop).ToArray();
            if(code.Length==2&&code[0].OpCode.Code==Code.Ldstr&&code[1].OpCode.Code==Code.Ret
                &&code[0].Operand is string text&&text.Length is >0 and <=6000&&!text.Contains('\0'))
                values[method.FullName]=text;
        }
        return values;
    }
    private static bool Call(Instruction instruction,out MethodReference call)
    {if(instruction.OpCode.Code is Code.Call or Code.Callvirt&&instruction.Operand is MethodReference method&&!method.ContainsGenericParameter){call=method;return true;}call=null!;return false;}
    private static int ForwardedParameter(Instruction instruction,MethodDefinition method)=>instruction.OpCode.Code switch
    {
        Code.Ldarg or Code.Ldarg_S=>instruction.Operand is ParameterDefinition p?p.Index:-99,
        Code.Ldarg_0=>method.HasThis?-1:0,Code.Ldarg_1=>method.HasThis?0:1,
        Code.Ldarg_2=>method.HasThis?1:2,Code.Ldarg_3=>method.HasThis?2:3,_=>-99
    };
    private static bool SinkPath(MethodReference call,AssemblyDefinition assembly,Dictionary<string,Wrapper> wrappers,out string[] path)
    {
        if(DisplaySetter(call)){path=[call.FullName];return true;}
        bool local=call.DeclaringType.Scope is ModuleDefinition module&&module==assembly.MainModule
            ||call.DeclaringType.Scope is AssemblyNameReference reference&&reference.FullName==assembly.Name.FullName;
        if(local&&wrappers.TryGetValue(call.FullName,out var wrapper)){path=wrapper.Path;return true;}
        path=[];return false;
    }
    private static bool DisplaySetter(MethodReference call)
    {
        if(call.Name is not ("set_text" or "SetText")||!call.HasThis||call.ReturnType.FullName!="System.Void"||call.Parameters.Count is <1 or >2
            ||call.Parameters[0].ParameterType.FullName!="System.String"||call.DeclaringType.Scope is not AssemblyNameReference scope)return false;
        if(call.Parameters.Count==2&&(call.Name!="SetText"||call.Parameters[1].ParameterType.FullName!="System.Boolean"))return false;
        return call.DeclaringType.FullName switch
        {
            "UnityEngine.UI.Text"=>call.Name=="set_text"&&scope.Name is "UnityEngine.UI" or "UnityEngine",
            "UnityEngine.TextMesh"=>call.Name=="set_text"&&scope.Name is "UnityEngine.TextRenderingModule" or "UnityEngine",
            "TMPro.TMP_Text" or "TMPro.TextMeshPro" or "TMPro.TextMeshProUGUI"=>scope.Name is "Unity.TextMeshPro" or "TextMeshPro",_=>false
        };
    }
}
