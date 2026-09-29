using System;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Fusion.CloudMeadow
{
    // Scoped identities verified against Game.dll and the shipped serialized UI.
    internal static class CloudUiRoles
    {
        static readonly Type slots=Type("WorldSlotComponentManager"),save=Type("SaveSlotComponentManager"),graphics=Type("GraphicsAndAudioManager"),keys=Type("KeybindingsManager"),keyGroup=Type("KeybindingGroup"),access=Type("AccessibilityManager"),quest=Type("QuestLogWindow"),stats=Type("CharacterSelectionCardManager");
        static readonly string[] fields={"playerName","timePlayed","timePlayedHeaderText","creationDateTime","creationYear","inGameDateTime","inGameYear","loadingLabelText"};
        static Type Type(string n){return AccessTools.TypeByName("TeamNimbus.CloudMeadow.UI."+n);}
        internal static object Get(object o,string field){if(o==null)return null;var f=AccessTools.Field(o.GetType(),field);return f==null?null:f.GetValue(o);}
        static Component Parent(Component c,Type t){return t==null?null:c.GetComponentInParent(t);}
        static bool Ref(Component owner,string field,Component c){return Get(owner,field) as UnityEngine.Object==c;}
        static bool DirectPath(Component c,Component owner,string path){var t=owner.transform.Find(path);return t!=null&&t==c.transform;}
        internal static bool Resolve(Component c,CloudTextProfile p)
        {
            var owner=Parent(c,slots);
            if(c is Text&&owner!=null)
            {
                foreach(string f in fields)if(Ref(owner,f,c)){p.Context=f;break;}
                if(p.Context==null)
                {
                    foreach(string f in new[]{"creationDateTime","inGameDateTime"})
                    {var value=Get(owner,f) as Text;if(value!=null&&(value.transform.parent==c.transform||c.transform.parent==value.transform.parent&&c.name=="Header")){p.Context=f+"Header";break;}}
                    var toggle=Get(owner,"toggleClickable") as Component;
                    if(toggle!=null&&c.transform.IsChildOf(toggle.transform))p.Context="slot-toggle";
                }
                if(p.Context!=null){p.Role=CloudTextRole.SaveField;p.RoleOwner=owner;p.DisplayLabel=p.SingleLine=true;p.Coordinated=true;return true;}
            }
            owner=Parent(c,save);
            if(c is Text&&owner!=null&&c.transform.parent==owner.transform&&c.name=="Label")
            {p.Role=CloudTextRole.SaveField;p.RoleOwner=owner;p.Context="slot-label";p.Coordinated=p.DisplayLabel=true;return true;}
            owner=Parent(c,quest);
            if(c is TMP_Text&&owner!=null&&DirectPath(c,owner,"Info Panel/Footer/Goals Complete Label"))
            {p.Role=CloudTextRole.QuestCounter;p.RoleOwner=owner;p.Boundary=c.transform as RectTransform;p.DisplayLabel=true;return true;}
            owner=Parent(c,graphics)??Parent(c,keys)??Parent(c,access);
            if(c is Text&&owner!=null)
            {
                p.Role=CloudTextRole.Settings;p.RoleOwner=owner;p.Coordinated=p.DisplayLabel=true;
                p.SingleLine=false;p.Context="label";
                foreach(string f in new[]{"windowModeText","pixelScaleText","_splashScreenBrightnessLabel","alwaysRunText","disableMouseMoveText","_textAnimationsLabel","_motionSwayDistLabel","_motionSwaySpeedLabel","_motionImpactFactorValueLabel","_textSpeedLabel"})
                    if(Ref(owner,f,c)){p.Context=f;p.SingleLine=true;break;}
                if(DirectPath(c,owner,"AudioGroup/Sliders/MasterChannelManager/MasterToggle/MasterText"))p.Context="master-volume";
                if(DirectPath(c,owner,"RenderingControls/UIScalingGroup/Label"))p.Context="art-scale";
                var group=Parent(c,keyGroup);
                if(group!=null)foreach(string f in new[]{"_mainKey","_alternative1","_alternative2"})
                {if(Get(Get(group,f),"_text") as UnityEngine.Object==c){p.Context="key-code";p.SingleLine=true;break;}}
                return true;
            }
            if(c is Text&&c.transform.parent!=null)
            {
                var group=c.transform.parent;var tutorial=group.parent;
                if(tutorial!=null&&tutorial.name=="Tutorial"&&tutorial.GetComponent<Canvas>()!=null&&
                    ((group.name=="Movement"&&(c.name=="SprintText"||c.name=="Movement text"))||(group.name=="Interaction"&&c.name=="text")))
                {
                    p.Role=CloudTextRole.Tutorial;p.Coordinated=p.DisplayLabel=true;p.Context=c.name;
                    if(c.name=="SprintText")
                    {var cap=group.Find("ShiftKey");if(cap!=null)p.Keycap=cap.GetComponent<SpriteRenderer>();p.SingleLine=true;}
                    return true;
                }
            }
            owner=Parent(c,stats);
            if(c is Text&&owner!=null)foreach(string f in new[]{"healthText","physiqueValue","staminaValue","intuitionValue","swiftnessValue"})
                if(Ref(owner,f,c)){p.Role=CloudTextRole.StatField;p.RoleOwner=owner;p.Context=f;p.Coordinated=p.DisplayLabel=p.SingleLine=true;return true;}
            return false;
        }
        internal static bool Local(CloudTextProfile p,string source,bool chinese,out string value)
        {
            value=null;if(string.IsNullOrEmpty(source))return false;
            // Save names are user data, not a request to translate a character name.
            if(p.Role==CloudTextRole.SaveField&&p.Context=="playerName"&&source!="SAVE READ ERROR")value=source;
            if(p.Role==CloudTextRole.Settings)
            {
                switch(p.Context)
                {
                    case "key-code":
                        if(source=="Shift")value=source;
                        else {try{var code=(KeyCode)Enum.Parse(typeof(KeyCode),source,false);if(Enum.IsDefined(typeof(KeyCode),code))value=!chinese?source:code==KeyCode.Space?"空格键":code==KeyCode.Return||code==KeyCode.KeypadEnter?"回车键":source;}catch{}}
                        break;
                    case "windowModeText":if(source=="Fullscreen")value="全屏";else if(source=="Window Mode")value="窗口模式";break;
                    case "_splashScreenBrightnessLabel":if(source=="Light")value="明亮";else if(source=="Dark")value="暗色";break;
                    case "alwaysRunText":case "disableMouseMoveText":case "_textAnimationsLabel":
                        if(source=="True"||source=="On")value="开启";else if(source=="False"||source=="Off")value="关闭";break;
                    case "master-volume":if(source=="Master")value="主音量";break;
                    case "art-scale":if(source=="Art Scaling")value="画面缩放";break;
                }
            }
            if(!chinese&&p.Context!="key-code"&&p.Context!="playerName")value=null;
            // Display-only identity path; no change to translation token validation.
            if(value==null&&p.Coordinated&&Regex.IsMatch(source,@"^\s*[+−\-]?\d+(?:[.,:/\-]\d+)*(?:%|x)?\s*$"))value=source;
            return value!=null;
        }
    }
}
