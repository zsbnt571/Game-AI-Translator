using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Fusion.UnityEmbedded
{
    public sealed partial class UnityBridgeRuntime
    {
        const string CharacterContract="fusion-character-runtime-1";
        sealed class CharacterRow
        {
            internal Binding Binding;internal string RequestId,CharacterId,Source,DisplayName,Text,Framework;internal int NativeActorId;
            internal string[] Evidence;internal long Deadline;internal bool Fallback;
        }
        bool characterRuntime;long characterSerial;
        readonly Dictionary<int,CharacterRow> characterRows=new Dictionary<int,CharacterRow>();
        readonly Dictionary<string,List<string>> characterFacts=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        Type dialoguePanelType;long dialogueTypeRetryAt;

        void ResetCharacters(bool active)
        {characterRuntime=active;characterRows.Clear();characterFacts.Clear();}
        Dictionary<string,object> PrepareCharacters(Dictionary<string,object> request)
        {
            ResetCharacters(String(request,"characterRuntimeContract")==CharacterContract);
            return Map("epoch",epoch,"characterRuntimeContract",characterRuntime?CharacterContract:null);
        }
        // This is a narrow, verified framework relationship. A TMP name, face,
        // hierarchy label, or another component with identical text is never
        // evidence of the speaking actor. Unsupported Unity frameworks stay ordinary.
        bool NaninovelIdentity(Binding binding,string source,out string identity,out string name)
        {
            identity=name=null;
            if(!Alive(binding.Target)||!Active(binding.Target))return false;
            try
            {
                Type textType=null;
                for(Type candidate=binding.Target.GetType();candidate!=null;candidate=candidate.BaseType)
                    if(candidate.FullName=="Naninovel.UI.RevealableText"){textType=candidate;break;}
                if(textType==null)return false;
                var panelType=textType.Assembly.GetType("Naninovel.UI.RevealableTextPrinterPanel",false);
                if(panelType==null)return false;
                // Naninovel stores the full author-authored line separately
                // from TMP.text. Wait until its reveal finishes; prefixes are
                // not separate translation requests.
                var original=textType.GetProperty("Text");
                var progress=textType.GetProperty("RevealProgress");
                if(original==null||progress==null||original.GetValue(binding.Target,null) as string!=source)return false;
                double revealed=Convert.ToDouble(progress.GetValue(binding.Target,null));
                if(double.IsNaN(revealed)||revealed<0.999999)return false;
                var parent=binding.Target.GetType().GetMethods().FirstOrDefault(m=>m.Name=="GetComponentInParent"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0);
                if(parent==null)return false;
                object panel=parent.MakeGenericMethod(panelType).Invoke(binding.Target,null);
                if(!Alive(panel)||!Active(panel))return false;
                var targetProperty=panelType.GetProperty("RevealableText");
                // AuthorId is a protected, framework-owned property in the
                // verified Naninovel runtime, not the visible AuthorNameText.
                var actorProperty=panelType.GetProperty("AuthorId",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                if(targetProperty==null||actorProperty==null)return false;
                object actualTarget=targetProperty.GetValue(panel,null);
                if(!ReferenceEquals(actualTarget,binding.Target)&&(!il2Cpp||Id(actualTarget)!=binding.Id))return false;
                var actor=actorProperty.GetValue(panel,null) as string;
                if(string.IsNullOrEmpty(actor)||!Regex.IsMatch(actor,@"^[A-Za-z_][A-Za-z0-9_.-]{0,95}$"))return false;
                identity="naninovel:"+actor;
                var display=panelType.GetProperty("AuthorNameText");
                name=display==null?actor:display.GetValue(panel,null) as string;
                if(string.IsNullOrEmpty(name)||name.Length>200)name=actor;
                return true;
            }
            catch{return false;}
        }
        // Only members declared by the documented framework contract are read.
        // The type argument prevents similarly named game properties from being
        // treated as speaker metadata. A failed reflection path is ordinary text.
        static object CharacterMember(Type type,object value,string member)
        {
            if(type==null||value==null||!type.IsInstanceOfType(value))return null;
            var flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
            var property=type.GetProperty(member,flags);
            if(property!=null&&property.CanRead&&property.GetIndexParameters().Length==0)return property.GetValue(value,null);
            var field=type.GetField(member,flags);return field==null?null:field.GetValue(value);
        }
        bool DialogueSystemIdentity(Binding binding,string source,out string identity,out string name,out int actorId)
        {
            identity=name=null;actorId=0;
            if(!Alive(binding.Target)||!Active(binding.Target))return false;
            try
            {
                if(dialoguePanelType==null)
                {
                    if(timer.ElapsedMilliseconds<dialogueTypeRetryAt)return false;
                    dialogueTypeRetryAt=timer.ElapsedMilliseconds+5000;
                    dialoguePanelType=FindType("PixelCrushers.DialogueSystem.StandardUISubtitlePanel");
                }
                var panelType=dialoguePanelType;if(panelType==null)return false;
                var parent=binding.Target.GetType().GetMethods().FirstOrDefault(m=>m.Name=="GetComponentInParent"&&m.IsGenericMethodDefinition&&m.GetParameters().Length==0);
                if(parent==null)return false;
                object panel=parent.MakeGenericMethod(panelType).Invoke(binding.Target,null);
                if(!Alive(panel)||!Active(panel))return false;
                // Accumulated text and speaker-name decoration can contain more
                // than one actor. Do not split or infer ownership of that text.
                object accumulate=CharacterMember(panelType,panel,"accumulateText"),addName=CharacterMember(panelType,panel,"addSpeakerName");
                if(!(accumulate is bool)||!(addName is bool)||(bool)accumulate||(bool)addName)return false;
                object delayed=CharacterMember(panelType,panel,"delayTypewriterUntilOpen");
                if(delayed is bool&&(bool)delayed)
                {
                    object focused=CharacterMember(panelType,panel,"hasFocus");
                    if(!(focused is bool)||!(bool)focused)return false;
                }
                var field=CharacterMember(panelType,panel,"subtitleText");
                var fieldType=FindType("PixelCrushers.UITextField");if(field==null||fieldType==null||!fieldType.IsInstanceOfType(field))return false;
                object ui=CharacterMember(fieldType,field,"uiText"),tmp=CharacterMember(fieldType,field,"textMeshProUGUI");
                // UITextField prefers uiText when both are assigned. Refuse the
                // ambiguous assignment instead of binding its unused TMP target.
                if(Alive(ui)&&Alive(tmp))return false;
                object target=Alive(ui)?ui:tmp;
                if(!SameUnityObject(target,binding.Target))return false;
                var subtitleType=panelType.Assembly.GetType("PixelCrushers.DialogueSystem.Subtitle",false);
                var formattedType=panelType.Assembly.GetType("PixelCrushers.DialogueSystem.FormattedText",false);
                var infoType=panelType.Assembly.GetType("PixelCrushers.DialogueSystem.CharacterInfo",false);
                object subtitle=CharacterMember(panelType,panel,"currentSubtitle");
                object formatted=CharacterMember(subtitleType,subtitle,"formattedText");
                if(CharacterMember(formattedType,formatted,"text") as string!=source)return false;
                // GetTypewriter only resolves the effect attached to subtitleText.
                // No SetContent/ShowSubtitle/Start/Stop/Continue call is ever made.
                var getTypewriter=panelType.GetMethod("GetTypewriter",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,Type.EmptyTypes,null);
                if(getTypewriter==null)return false;
                object typewriter=getTypewriter.Invoke(panel,null);
                if(typewriter!=null)
                {
                    var effectType=panelType.Assembly.GetType("PixelCrushers.DialogueSystem.AbstractTypewriterEffect",false);
                    object playing=CharacterMember(effectType,typewriter,"isPlaying");
                    if(!(playing is bool)||(bool)playing)return false;
                }
                if(NativeRevealing(binding,source))return false;
                object info=CharacterMember(subtitleType,subtitle,"speakerInfo"),rawId=CharacterMember(infoType,info,"id");
                if(!(rawId is int)||(int)rawId<=0)return false;
                actorId=(int)rawId;string actorText=actorId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                identity="unityds:actor:"+actorText;
                name=CharacterMember(infoType,info,"Name") as string;
                if(string.IsNullOrEmpty(name)||name.Length>200||name.Any(char.IsControl))name=actorText;
                return true;
            }
            catch{return false;}
        }
        CharacterRow CharacterFor(Binding binding,string source)
        {
            if(!characterRuntime||!enabled||binding==null)return null;
            if(!Eligible(source)){characterRows.Remove(binding.Id);return null;}
            string identity,name,framework="naninovel";int actorId=0;
            if(!NaninovelIdentity(binding,source,out identity,out name))
            {
                if(!DialogueSystemIdentity(binding,source,out identity,out name,out actorId)){characterRows.Remove(binding.Id);return null;}
                framework="unityds";
            }
            CharacterRow row;
            if(characterRows.TryGetValue(binding.Id,out row)&&ReferenceEquals(row.Binding,binding)&&row.Source==source&&row.CharacterId==identity)return row;
            if(characterRows.Count>=32&&!characterRows.ContainsKey(binding.Id))return null;
            List<string> facts;characterFacts.TryGetValue(identity,out facts);
            row=new CharacterRow{Binding=binding,RequestId="u"+(++characterSerial),CharacterId=identity,Source=source,DisplayName=name,Framework=framework,NativeActorId=actorId,
                Evidence=facts==null?new string[0]:facts.ToArray(),Deadline=timer.ElapsedMilliseconds+3000};
            characterRows[binding.Id]=row;
            if(source.Length<=600&&(characterFacts.Count<256||facts!=null))
            {
                if(facts==null)characterFacts[identity]=facts=new List<string>();
                if(facts.Count==0||facts[facts.Count-1]!=source){facts.Add(source);if(facts.Count>3)facts.RemoveAt(0);}
            }
            return row;
        }
        bool TryBindingTranslation(Binding binding,string source,out string translated)
        {
            var row=CharacterFor(binding,source);
            if(row!=null&&row.Text!=null){translated=row.Text;return true;}
            return TryTranslation(source,out translated);
        }
        bool CharacterWaiting(Binding binding,string source)
        {
            var row=CharacterFor(binding,source);
            return row!=null&&!row.Fallback&&(row.Text!=null||timer.ElapsedMilliseconds<=row.Deadline);
        }
        void PollCharacters(Dictionary<string,object> request,Dictionary<string,object> response)
        {
            if(!characterRuntime)return;
            // Revalidate the exact native component/author before accepting a
            // delayed response, including when one control is reused.
            foreach(var pair in characterRows.ToArray())
            {
                Binding currentBinding;
                if(!bindings.TryGetValue(pair.Key,out currentBinding)||!ReferenceEquals(currentBinding,pair.Value.Binding))characterRows.Remove(pair.Key);
                else CharacterFor(currentBinding,currentBinding.Source);
            }
            foreach(var binding in bindings.Values.Where(b=>b.LastActive).Take(4096))CharacterFor(binding,binding.Source);
            var accepted=new List<string>();var changed=new HashSet<string>(StringComparer.Ordinal);
            object raw;var replies=request.TryGetValue("characterResults",out raw)?raw as IList:null;
            if(String(request,"characterRuntimeContract")==CharacterContract&&replies!=null)
            {
                for(int i=0;i<Math.Min(16,replies.Count);i++)
                {
                    var reply=replies[i] as Dictionary<string,object>;if(reply==null)continue;
                    var row=characterRows.Values.FirstOrDefault(r=>r.RequestId==String(reply,"requestId")&&r.Source==String(reply,"source")&&r.CharacterId==String(reply,"characterId"));
                    if(row==null)continue;
                    object flag;string text=String(reply,"text");
                    if(reply.TryGetValue("pending",out flag)&&flag is bool&&(bool)flag)row.Deadline=timer.ElapsedMilliseconds+3000;
                    else if(reply.TryGetValue("fallback",out flag)&&flag is bool&&(bool)flag)
                    {if(row.Text!=null)changed.Add(row.Source);row.Text=null;row.Fallback=true;}
                    else if(!string.IsNullOrEmpty(text)&&text.Length<=24000&&text.IndexOf('\0')<0)
                    {if(row.Text!=text)changed.Add(row.Source);row.Text=text;row.Fallback=false;}
                    else continue;
                    accepted.Add(row.RequestId);
                }
            }
            response["characterRuntimeContract"]=CharacterContract;
            response["characterRequests"]=characterRows.Values.Take(16).Select(r=>Map("requestId",r.RequestId,"source",r.Source,"characterId",r.CharacterId,"displayName",r.DisplayName,"evidence",r.Evidence,"framework",r.Framework,"nativeActorId",r.NativeActorId)).ToArray();
            response["characterAccepted"]=accepted.ToArray();
            if(changed.Count>0)ScheduleRefresh(changed);
        }
    }
}
