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
var literals=original.SelectMany(c=>c.Instructions).Where(i=>i.Type1==UndertaleInstruction.DataType.String&&i.ValueString!=null).Select(i=>i.ValueString.Resource.Content).Distinct(StringComparer.Ordinal).Where(s=>s.Length>=2&&s.Length<=6000&&!s.Contains('\0')&&Regex.IsMatch(s,@"\p{L}")&&!Regex.IsMatch(s,@"^(?:[a-z]+://|[\w./\\-]+\.(?:png|jpg|ogg|wav|json|ini|yy|gml))$",RegexOptions.IgnoreCase)&&!Regex.IsMatch(s,@"^[a-z0-9_./\\]+$" )).Take(100000).ToArray();
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
File.WriteAllText(Path.Combine(output,"catalog.json"),JsonConvert.SerializeObject(new {texts=literals,files=1,partial=true}));
File.WriteAllText(Path.Combine(output,"patch.json"),JsonConvert.SerializeObject(new {protocol=130,engine="GameMaker",saveName=Data.GeneralInfo.Name.Content,version=$"{Data.GeneralInfo.Major}.{Data.GeneralInfo.Minor}.{Data.GeneralInfo.Release}.{Data.GeneralInfo.Build}",bytecode=Data.GeneralInfo.BytecodeVersion,drawCallSites=replaced,catalog=literals.Length,originalCodeEntries=original.Length,logicStringsReplaced=0,logicMeasurementsReplaced=0,autoSurface,autoGui,fontInk,pixelFonts,fonts=Data.Fonts.Select(f=>new{name=f.Name.Content,size=f.EmSize,glyphs=f.Glyphs.Count})},Formatting.Indented));
