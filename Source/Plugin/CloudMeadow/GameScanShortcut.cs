using System;
using UnityEngine;
namespace Fusion.InputSupport
{
    internal sealed class GameScanShortcut
    {
        internal readonly KeyCode Key;
        internal readonly bool Ctrl,Alt,Shift;
        internal GameScanShortcut(string text)
        {
            var parts=(text??"F8").Split('+');string key=parts[parts.Length-1].Trim();
            for(int i=0;i<parts.Length-1;i++)
            {string m=parts[i].Trim();if(m.Equals("Ctrl",StringComparison.OrdinalIgnoreCase))Ctrl=true;else if(m.Equals("Alt",StringComparison.OrdinalIgnoreCase))Alt=true;else if(m.Equals("Shift",StringComparison.OrdinalIgnoreCase))Shift=true;else throw new ArgumentException("Invalid shortcut modifier");}
            if(key.Length==1&&char.IsDigit(key[0]))key="Alpha"+key;
            if(key.Equals("Up",StringComparison.OrdinalIgnoreCase)||key.Equals("Down",StringComparison.OrdinalIgnoreCase)||key.Equals("Left",StringComparison.OrdinalIgnoreCase)||key.Equals("Right",StringComparison.OrdinalIgnoreCase))key+="Arrow";
            Key=(KeyCode)Enum.Parse(typeof(KeyCode),key,true);
        }
        internal bool Pressed()
        {
            return Input.GetKeyDown(Key)&&ModifiersMatch(Input.GetKey(KeyCode.LeftControl)||Input.GetKey(KeyCode.RightControl),Input.GetKey(KeyCode.LeftAlt)||Input.GetKey(KeyCode.RightAlt),Input.GetKey(KeyCode.LeftShift)||Input.GetKey(KeyCode.RightShift));
        }
        internal bool ModifiersMatch(bool ctrl,bool alt,bool shift){return Ctrl==ctrl&&Alt==alt&&Shift==shift;}
        public override string ToString(){return (Ctrl?"Ctrl+":"")+(Alt?"Alt+":"")+(Shift?"Shift+":"")+Key;}
    }
}
