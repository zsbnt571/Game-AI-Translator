// Run by the unmodified, separately distributed UndertaleModCli. No game names.
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UndertaleModLib;
using UndertaleModLib.Models;
using UndertaleModLib.Compiler;
using Underanalyzer.Decompiler;
using Underanalyzer.Decompiler.AST;
EnsureDataLoaded();
string output=Environment.GetEnvironmentVariable("FUSION_GM_BUILD");
string bridge=Environment.GetEnvironmentVariable("FUSION_GM_BRIDGE");
if (string.IsNullOrEmpty(output)||string.IsNullOrEmpty(bridge)) throw new IOException("Missing build paths");
if(Data.IsYYC()||Data.GeneralInfo.BytecodeVersion!=17||!Data.IsVersionAtLeast(2,3)) throw new IOException("Only GameMaker 2.3+ VM bytecode 17 is supported by this candidate");
if(Data.Code.Any(c=>c.Name.Content.Contains("fusion130_"))) throw new IOException("Already patched; use original resource");
var original=Data.Code.Where(c=>c.ParentEntry==null).ToArray();
var shapes=new Dictionary<string,int[]> {
 {"string_copy",new[]{3,0}},
 {"draw_text",new[]{3,2}}, {"draw_text_ext",new[]{5,2}},
 {"draw_text_transformed",new[]{6,2}}, {"draw_text_ext_transformed",new[]{8,2}},
 {"draw_text_colour",new[]{8,2}}, {"draw_text_color",new[]{8,2}},
 {"draw_text_ext_colour",new[]{10,2}}, {"draw_text_ext_color",new[]{10,2}},
 {"draw_text_transformed_colour",new[]{11,2}}, {"draw_text_transformed_color",new[]{11,2}},
 {"draw_text_ext_transformed_colour",new[]{13,2}}, {"draw_text_ext_transformed_color",new[]{13,2}}
};
var calls=original.SelectMany(c=>c.Instructions).Where(i=>i.Kind==UndertaleInstruction.Opcode.Call&&i.ValueFunction!=null&&shapes.ContainsKey(i.ValueFunction.Name.Content)).ToArray();
if(calls.Length==0) throw new IOException("No supported standard text calls; custom renderers require further work");
if(calls.Any(i=>i.ArgumentsCount!=shapes[i.ValueFunction.Name.Content][0]))throw new IOException("Unexpected text argument layout");
var displayCatalog=FusionGameMakerDisplayCatalog.Read(Data,original);
var literals=displayCatalog.Entries.Select(e=>e.text).ToArray();
string gml=File.ReadAllText(bridge);
// Only opt into automatic-target raster sizing when the game does not own the
// application surface. Games with custom composition retain their own policy.
var originalFunctions=original.SelectMany(c=>c.Instructions).Where(i=>i.ValueFunction!=null).Select(i=>i.ValueFunction.Name.Content).ToHashSet(StringComparer.Ordinal);
var surfaceCopies=original.SelectMany(c=>c.Instructions).Where(i=>i.Kind==UndertaleInstruction.Opcode.Call&&i.ValueFunction?.Name.Content=="draw_surface"&&i.ArgumentsCount==3).ToArray();
bool safeSurfaceReads=original.All(c=>c.Instructions.Select((i,n)=>new{i,n}).Where(p=>p.i.ValueVariable?.Name.Content=="application_surface").All(p=>p.n+1<c.Instructions.Count&&surfaceCopies.Contains(c.Instructions[p.n+1])));
bool autoSurface=safeSurfaceReads&&!originalFunctions.Overlaps(new[]{"application_surface_enable","application_surface_draw_enable","surface_resize","display_set_gui_maximise"});
bool autoGui=!originalFunctions.Contains("display_set_gui_size");
gml+="\nfunction fusion149_auto_surface(){return "+(autoSurface?"true":"false")+";}\nfunction fusion149_auto_gui(){return "+(autoGui?"true":"false")+";}\n";
// Read alpha bounds rather than equating the font's em size with visible ink.
// Texture work is bounded and performed while preparing the adapter, not in a frame.
var fontInk=new Dictionary<string,int>();
var pixelFonts=new Dictionary<string,bool>();
using(var textures=new UndertaleModLib.Util.TextureWorker()) {
 foreach(var f in Data.Fonts.Take(128)) {
  try {
   if(f.Texture==null)continue;
   using var img=textures.GetTextureFor(f.Texture,f.Name.Content,true);
   if(img.Width>4096||img.Height>4096)continue;
   byte[] rgba=img.ToByteArray(ImageMagick.MagickFormat.Rgba);int iw=(int)img.Width,ih=(int)img.Height;
   var heights=new List<int>();int solid=0,soft=0;
   foreach(var glyph in f.Glyphs.Where(x=>x.Character>=65&&x.Character<=90).Take(26)) {
    int top=ih,bottom=-1;
    for(int y=glyph.SourceY;y<Math.Min(ih,glyph.SourceY+glyph.SourceHeight);y++)
     for(int x=glyph.SourceX;x<Math.Min(iw,glyph.SourceX+glyph.SourceWidth);x++)
      {int alpha=rgba[(y*iw+x)*4+3];if(alpha>=128){top=Math.Min(top,y);bottom=Math.Max(bottom,y);}if(alpha>=250)solid++;else if(alpha>5)soft++;}
    if(bottom>=top)heights.Add(bottom-top+1);
   }
   if(heights.Count>0){heights.Sort();fontInk[f.Name.Content]=heights[heights.Count/2];pixelFonts[f.Name.Content]=solid>0&&soft<=solid/20;}
  }catch(Exception){/* A font without readable alpha keeps the runtime fallback. */}
 }
}
gml+="\nfunction fusion136_font_ink(_name) { switch(_name) {";
foreach(var p in fontInk)gml+="case "+JsonConvert.SerializeObject(p.Key)+": return "+p.Value+";";
gml+="} return -1; }\n";
gml+="\nfunction fusion147_font_pixel(_name) { switch(_name) {";
foreach(var p in pixelFonts)gml+="case "+JsonConvert.SerializeObject(p.Key)+": return "+(p.Value?"true":"false")+";";
gml+="} return false; }\n";
foreach(string name in calls.Select(i=>i.ValueFunction.Name.Content).Distinct()) {
 if(name=="string_copy")continue; // Observation only; return the exact original substring.
 int argc=shapes[name][0], text=shapes[name][1];
 string args=string.Join(",",Enumerable.Range(0,argc).Select(i=>"_a"+i));
 bool ext=name.Contains("_ext"),trans=name.Contains("_transformed");
 int suffix=ext?(trans?8:5):(trans?6:3);
 var mapped=new List<string>{"_a0","_a1","_v[0]"};
 mapped.Add("_v[3]");mapped.Add("_v[4]");
 mapped.Add(trans?"_a"+(ext?5:3)+"*_v[2]":"_v[2]");
 mapped.Add(trans?"_a"+(ext?6:4)+"*_v[2]":"_v[2]");
 mapped.Add(trans?"_a"+(ext?7:5):"0");
 mapped.AddRange(Enumerable.Range(suffix,argc-suffix).Select(i=>"_a"+i));
 string target=ext?name:name.Replace("draw_text","draw_text_ext");
 if(!trans)target=target.Replace("draw_text_ext","draw_text_ext_transformed");
 // The layout chooses raster density from the actual draw target: readable
 // high-density GUI text or native-size surface text. Restore texture state.
 gml+="\nfunction fusion130_"+name+"("+args+") { var _v=fusion136_layout(_a2,"+(ext?"_a4,_a3,true":"-1,-1,false")+"); if(!_v[5]) return "+name+"("+args+"); var _filter=gpu_get_texfilter_ext(0); try { gpu_set_texfilter_ext(0,_v[6]); return "+target+"("+string.Join(",",mapped)+"); } finally { gpu_set_texfilter_ext(0,_filter); draw_set_font(_v[1]); } }\n";
}
gml+="\nfusion130_init();\n";
var group=new CodeImportGroup(Data){AutoCreateAssets=true,MainThreadAction=MainThreadAction};
group.QueueReplace("gml_GlobalScript_fusion130_bridge",gml);
// A non-drawing room instance keeps the bounded mailbox responsive even on a
// screen which draws no standard text. It has no sprite, collision or input events.
var controller=new UndertaleGameObject{Name=Data.Strings.MakeString("obj_fusion130_bridge"),Visible=false,Persistent=false};
Data.GameObjects.Add(controller);
group.QueueReplace(controller.EventHandlerFor(EventType.Create,Data),"fusion130_init();");
group.QueueReplace(controller.EventHandlerFor(EventType.Step,Data),"fusion130_tick();fusion149_surface_tick();");
uint nextLayer=Data.Rooms.SelectMany(r=>r.Layers).Select(l=>l.LayerId).DefaultIfEmpty(0u).Max();
foreach(var room in Data.Rooms){
    var layer=new UndertaleRoom.Layer{LayerName=Data.Strings.MakeString("FusionTranslation"),Data=new UndertaleRoom.Layer.LayerInstancesData(),LayerType=UndertaleRoom.LayerType.Instances,LayerDepth=0,LayerId=checked(++nextLayer),IsVisible=true};
    var instance=new UndertaleRoom.GameObject{InstanceID=checked(Data.GeneralInfo.LastObj++),ObjectDefinition=controller,X=0,Y=0};
    room.GameObjects.Add(instance);layer.InstancesData.Instances.Add(instance);room.Layers.Add(layer);
}
group.Import();
string testName=Environment.GetEnvironmentVariable("FUSION_GM_TEST_NAME");
if(!string.IsNullOrEmpty(testName)) {
 if(!Regex.IsMatch(testName,@"^FusionGameMakerTest_[A-F0-9]{16}$"))throw new IOException("Invalid test namespace");
 Data.GeneralInfo.Name=Data.Strings.MakeString(testName);
}
int replaced=0;
foreach(var call in calls) {
 string name="fusion130_"+call.ValueFunction.Name.Content;
 var target=Data.Functions.FirstOrDefault(f=>f.Name.Content=="gml_Script_"+name||f.Name.Content==name);
 if(target==null)throw new IOException("Missing compiled wrapper: "+name);
 call.ValueFunction=target;replaced++;
}
if(autoSurface){
    var target=Data.Functions.First(f=>f.Name.Content=="gml_Script_fusion149_draw_surface"||f.Name.Content=="fusion149_draw_surface");
    foreach(var call in surfaceCopies)call.ValueFunction=target;
}
File.WriteAllText(Path.Combine(output,"catalog.json"),JsonConvert.SerializeObject(new {schema="fusion-gamemaker-display/1",texts=literals,files=1,partial=true,entries=displayCatalog.Entries,failures=displayCatalog.Failures,limited=displayCatalog.Limited,excludedLocaleEntries=displayCatalog.ExcludedLocaleEntries,unresolvedSinks=displayCatalog.UnresolvedSinks}));
File.WriteAllText(Path.Combine(output,"patch.json"),JsonConvert.SerializeObject(new {protocol=130,engine="GameMaker",saveName=Data.GeneralInfo.Name.Content,version=$"{Data.GeneralInfo.Major}.{Data.GeneralInfo.Minor}.{Data.GeneralInfo.Release}.{Data.GeneralInfo.Build}",bytecode=Data.GeneralInfo.BytecodeVersion,drawCallSites=replaced,catalog=literals.Length,originalCodeEntries=original.Length,logicStringsReplaced=0,logicMeasurementsReplaced=0,autoSurface,autoGui,fontInk,pixelFonts,fonts=Data.Fonts.Select(f=>new{name=f.Name.Content,size=f.EmSize,glyphs=f.Glyphs.Count})},Formatting.Indented));

// FUSION_DISPLAY_CATALOG_BEGIN -- also exercised by the independent CLI fixtures.
// Work backwards from display arguments. A bytecode literal, a member named
// "text", or a call to string_copy alone is not evidence of display use.
public sealed class FusionGameMakerDisplayCatalog
{
    public sealed class Entry { public string text,code,sink,rule; }
    public sealed class Result {
        public List<Entry> Entries=new List<Entry>();
        public List<string> Failures=new List<string>();
        public bool Limited; public int ExcludedLocaleEntries,UnresolvedSinks;
    }
    sealed class Context { public string Code,Local,Function; public UndertaleGameObject Owner; public bool Locale; }
    sealed class Value { public IExpressionNode Node; public Context Context; }
    sealed class Binding { public string Key; public Value Value; }
    sealed class Call { public IFunctionCallNode Node; public Context Context; }
    sealed class Limit : Exception { }
    readonly Result result=new Result();
    readonly Dictionary<string,List<Value>> bindings=new Dictionary<string,List<Value>>(StringComparer.Ordinal);
    readonly Dictionary<string,HashSet<int>> sinks=new Dictionary<string,HashSet<int>>(StringComparer.Ordinal);
    readonly List<Call> calls=new List<Call>();
    readonly HashSet<string> texts=new HashSet<string>(StringComparer.Ordinal);
    readonly Dictionary<string,UndertaleGameObject> owners=new Dictionary<string,UndertaleGameObject>(StringComparer.Ordinal);
    readonly Dictionary<string,int> definitions=new Dictionary<string,int>(StringComparer.Ordinal);
    readonly HashSet<string> mutated=new HashSet<string>(StringComparer.Ordinal);
    readonly HashSet<UndertaleGameObject> drawOverrides=new HashSet<UndertaleGameObject>();
    int nodes,steps,anonymous;
    const int MaxNodes=2000000,MaxSteps=1000000;
    static readonly Regex LocaleName=new Regex(@"(?:^|_)(?:lang(?:uage)?|locale|localization|localisation|translation)(?:$|_)|(?:Language|Locale|Localization|Localisation)$",RegexOptions.CultureInvariant);
    public static Result Read(UndertaleData data,UndertaleCode[] code) {
        var scan=new FusionGameMakerDisplayCatalog();
        foreach(string name in new[]{"draw_text","draw_text_ext","draw_text_transformed","draw_text_ext_transformed","draw_text_colour","draw_text_color","draw_text_ext_colour","draw_text_ext_color","draw_text_transformed_colour","draw_text_transformed_color","draw_text_ext_transformed_colour","draw_text_ext_transformed_color"})scan.sinks[name]=new HashSet<int>{2};
        // Use resource ownership, not code-name guesses, to join Create/Draw and
        // inherited events. Unrelated objects with identical field names stay apart.
        foreach(var obj in data.GameObjects)foreach(var events in obj.Events)foreach(var evt in events)foreach(var action in evt.Actions)
            if(action.CodeId!=null) {
                scan.owners[action.CodeId.Name.Content]=obj;
                if(action.CodeId.Name.Content.StartsWith("gml_Object_"+obj.Name.Content+"_Draw_",StringComparison.Ordinal)&&!action.CodeId.Instructions.Any(i=>i.ValueFunction?.Name.Content=="event_inherited"))scan.drawOverrides.Add(obj);
            }
        var global=new UndertaleModLib.Decompiler.GlobalDecompileContext(data);
        foreach(var c in code.Take(10000)) {
            try {
                var ast=new DecompileContext(global,c,null).DecompileToAST();
                var context=new Context{Code=c.Name.Content,Local=c.Name.Content,Owner=scan.owners.TryGetValue(c.Name.Content,out var o)?o:null,Locale=scan.HasLocale(ast)};
                if(context.Locale)scan.result.ExcludedLocaleEntries++;
                scan.Index(ast,context);
            } catch(Limit){scan.result.Limited=true;break;}
              catch(Exception ex){scan.result.Failures.Add(c.Name.Content+": "+ex.GetType().Name);}
        }
        if(code.Length>10000)scan.result.Limited=true;
        try {
            // Only infer identity/text-source parameter forwarding. Dynamic
            // concatenation and unknown formatting functions do not propagate it.
            for(int pass=0;pass<8;pass++) {
                bool changed=false;
                foreach(var call in scan.calls.ToArray()) {
                    string function=call.Context.Function;
                    if(call.Context.Locale||string.IsNullOrEmpty(function)||!scan.definitions.TryGetValue(function,out int count)||count!=1||!scan.sinks.TryGetValue(FunctionName(call.Node.FunctionName),out var args))continue;
                    foreach(int arg in args.ToArray())if(arg<call.Node.Arguments.Count)
                        foreach(int parameter in scan.Parameters(call.Node.Arguments[arg],call.Context,new HashSet<string>(),0)) {
                            if(!scan.sinks.TryGetValue(function,out var forwarded))scan.sinks[function]=forwarded=new HashSet<int>();
                            changed|=forwarded.Add(parameter);
                        }
                }
                if(!changed)break;
            }
            foreach(var call in scan.calls) {
                if(call.Context.Locale||!scan.sinks.TryGetValue(FunctionName(call.Node.FunctionName),out var args))continue;
                foreach(int arg in args)if(arg<call.Node.Arguments.Count) {
                    bool resolved=false;
                    foreach(var value in scan.Resolve(call.Node.Arguments[arg],call.Context,new List<string>(),new HashSet<string>(),0)) {
                        if(!scan.Text(value.text))continue;resolved=true;if(!scan.texts.Add(value.text))continue;
                        if(scan.result.Entries.Count>=100000){scan.result.Limited=true;break;}
                        value.sink=call.Node.FunctionName+"/argument"+arg;
                        scan.result.Entries.Add(value);
                    }
                    if(!resolved)scan.result.UnresolvedSinks++;
                }
            }
        } catch(Limit){scan.result.Limited=true;}
        return scan.result;
    }
    bool HasLocale(IBaseASTNode node,int depth=0) {
        if(++nodes>MaxNodes||depth>128)throw new Limit();
        if(node is VariableNode v&&LocaleName.IsMatch(v.Variable.Name.Content)||node is IFunctionCallNode f&&LocaleName.IsMatch(f.FunctionName??""))return true;
        return node.EnumerateChildren().Any(child=>HasLocale(child,depth+1));
    }
    void Index(IBaseASTNode node,Context context,int depth=0) {
        if(++nodes>MaxNodes||depth>128)throw new Limit();
        if(node is StructNode)return; // fields are followed only through their owning value
        if(node is WithLoopNode)return; // self changes here; no ownership proof
        if(node is FunctionDeclNode f) {
            string name=f.IsAnonymous?"":f.Name;
            if(name.Length>0)definitions[name]=definitions.TryGetValue(name,out int n)?n+1:1;
            var nested=new Context{Code=context.Code,Local=context.Local+"/function"+(++anonymous),Owner=context.Owner,Function=name,Locale=context.Locale};
            Index(f.Body,nested,depth+1);return;
        }
        if(node is AssignNode mutation&&mutation.AssignKind!=AssignNode.AssignType.Normal&&mutation.Variable is VariableNode target) {string key=Key(target,context);if(key!=null)mutated.Add(key);}
        if(node is AssignNode a&&a.AssignKind==AssignNode.AssignType.Normal&&a.Variable is VariableNode v&&(v.ArrayIndices==null||v.ArrayIndices.Count==0)) {
            string key=Key(v,context);
            if(key!=null) {if(!bindings.TryGetValue(key,out var list))bindings[key]=list=new List<Value>();list.Add(new Value{Node=a.Value,Context=context});}
        }
        if(node is FunctionCallNode call)calls.Add(new Call{Node=call,Context=context});
        foreach(var child in node.EnumerateChildren())Index(child,context,depth+1);
    }
    static bool Related(UndertaleGameObject a,UndertaleGameObject b) {
        if(a==null||b==null)return false;
        var seen=new HashSet<UndertaleGameObject>();
        for(var o=a;o!=null&&seen.Add(o);o=o.ParentId)if(o==b)return true;
        seen.Clear();for(var o=b;o!=null&&seen.Add(o);o=o.ParentId)if(o==a)return true;
        return false;
    }
    string Key(VariableNode v,Context c) {
        if(v.Left is VariableNode)return null;
        string type=(v.Left as InstanceTypeNode)?.InstanceType.ToString();
        if(v.Left is Int16Node i)type=((Underanalyzer.IGMInstruction.InstanceType)i.Value).ToString();
        if(type=="Local"||v.Variable.InstanceType.ToString()=="Local")return "local:"+c.Local+":"+v.Variable.Name.Content;
        if(type=="Global")return "global:"+v.Variable.Name.Content;
        if(type=="Self"&&c.Owner!=null)return "self:"+c.Owner.Name.Content+":"+v.Variable.Name.Content;
        return null;
    }
    IEnumerable<Value> Values(VariableNode v,Context context) {
        string key=Key(v,context);if(key==null)yield break;
        if(key.StartsWith("self:")) {
            foreach(var owner in owners.Values.Distinct().Where(o=>Related(o,context.Owner))) {
                bool blocked=false;var seen=new HashSet<UndertaleGameObject>();
                for(var o=owner;o!=null&&o!=context.Owner&&seen.Add(o);o=o.ParentId)if(drawOverrides.Contains(o)){blocked=true;break;}
                string sourceKey="self:"+owner.Name.Content+":"+v.Variable.Name.Content;
                if(!blocked&&!mutated.Contains(sourceKey)&&bindings.TryGetValue(sourceKey,out var list))foreach(var value in list)if(!value.Context.Locale)yield return value;
            }
        } else if(!mutated.Contains(key)&&bindings.TryGetValue(key,out var list))foreach(var value in list)if(!value.Context.Locale)yield return value;
    }
    void Step(int depth){if(++steps>MaxSteps)throw new Limit();if(depth>48)result.Limited=true;}
    static string FunctionName(string name)=>(name??"").StartsWith("gml_Script_",StringComparison.Ordinal)?name.Substring(11):name??"";
    IEnumerable<int> Parameters(IExpressionNode node,Context context,HashSet<string> active,int depth) {
        Step(depth);if(depth>48)yield break;
        if(node is FunctionCallNode f&&(f.FunctionName=="string_copy"&&f.Arguments.Count==3||f.FunctionName=="string"&&f.Arguments.Count==1)) {
            foreach(int p in Parameters(f.Arguments[0],context,active,depth+1))yield return p;yield break;
        }
        if(!(node is VariableNode v)||(v.ArrayIndices!=null&&v.ArrayIndices.Count>0))yield break;
        int arg=v.GetArgumentIndex(32,false);if(arg>=0){yield return arg;yield break;}
        string key=Key(v,context);if(key==null||!key.StartsWith("local:")||!active.Add(key))yield break;
        foreach(var value in Values(v,context))foreach(int p in Parameters(value.Node,value.Context,active,depth+1))yield return p;
        active.Remove(key);
    }
    // A selector is an array index (#n or #*) or an exact struct member (.name).
    IEnumerable<Entry> Resolve(IExpressionNode node,Context context,List<string> path,HashSet<string> active,int depth) {
        Step(depth);if(depth>48||context.Locale)yield break;
        if(path.Count==0&&Constant(node,out string constant)) {
            yield return new Entry{text=constant,code=context.Code,rule="display-flow/constant"};yield break;
        }
        if(node is FunctionCallNode f&&(f.FunctionName=="string_copy"&&f.Arguments.Count==3||f.FunctionName=="string"&&f.Arguments.Count==1)) {
            foreach(var value in Resolve(f.Arguments[0],context,path,active,depth+1)){value.rule="display-flow/typewriter-source";yield return value;}yield break;
        }
        if(node is ArrayInitNode array&&path.Count>0&&path[0].StartsWith("#")) {
            string selector=path[0];var rest=path.Skip(1).ToList();
            for(int i=0;i<array.Elements.Count;i++)if(selector=="#*"||selector=="#"+i)
                foreach(var value in Resolve(array.Elements[i],context,rest,active,depth+1))yield return value;
            yield break;
        }
        if(node is StructNode structure&&path.Count>0&&path[0].StartsWith(".")) {
            foreach(var a in structure.Body.Children.OfType<AssignNode>())if(a.AssignKind==AssignNode.AssignType.Normal&&a.Variable is VariableNode member&&"."+member.Variable.Name.Content==path[0])
                foreach(var value in Resolve(a.Value,context,path.Skip(1).ToList(),active,depth+1)){value.rule="display-flow/struct-member";yield return value;}
            yield break;
        }
        if(!(node is VariableNode v))yield break;
        var selectors=new List<string>();
        if(v.ArrayIndices!=null)foreach(var index in v.ArrayIndices)selectors.Add(index is Int16Node i?"#"+i.Value:index is Int32Node j?"#"+j.Value:"#*");
        selectors.AddRange(path);
        if(v.Left is VariableNode left) {
            selectors.Insert(0,"."+v.Variable.Name.Content);
            foreach(var value in Resolve(left,context,selectors,active,depth+1))yield return value;yield break;
        }
        string key=Key(v,context);if(key==null)yield break;
        key+="/"+string.Join("/",selectors);if(!active.Add(key))yield break;
        foreach(var assignment in Values(v,context))foreach(var value in Resolve(assignment.Node,assignment.Context,selectors,active,depth+1))yield return value;
        active.Remove(key);
    }
    bool Constant(IExpressionNode node,out string value,int depth=0) {
        value=null;if(depth>48)return false;
        if(node is StringNode text){value=text.Value.Content;return value.Length<=6000;}
        if(node is FunctionCallNode call&&call.FunctionName=="string"&&call.Arguments.Count==1)return Constant(call.Arguments[0],out value,depth+1);
        if(node is BinaryNode binary&&binary.Instruction.Kind.ToString()=="Add"&&Constant(binary.Left,out var left,depth+1)&&Constant(binary.Right,out var right,depth+1)&&left.Length+right.Length<=6000){value=left+right;return true;}
        return false;
    }
    bool Text(string text)=>text.Length>=2&&text.Length<=6000&&Regex.IsMatch(text,@"\p{L}")&&!Regex.IsMatch(text,@"[\x00-\x08\x0b\x0c\x0e-\x1f]")&&!Regex.IsMatch(text,@"^(?:[a-z]+://|[\w./\\-]+\.(?:png|jpg|ogg|wav|json|ini|yy|gml))$",RegexOptions.IgnoreCase);
}
