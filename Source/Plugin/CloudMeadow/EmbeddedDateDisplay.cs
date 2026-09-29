using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Fusion.CloudMeadow
{
    // Embedded display only. Input is the native, already-local-time display;
    // this helper never reads a clock, converts a timezone or writes save data.
    internal static class EmbeddedDateDisplay
    {
        static readonly Regex Native = new Regex(@"\A(?<h>[0-9]{1,2}):(?<m>[0-9]{2}), (?<d>[0-9]{1,2}) (?<month>[A-Za-z]+)\z");
        static readonly string[] Months={"January","February","March","April","May","June","July","August","September","October","November","December"};
        internal static bool Scoped(CloudTextProfile p)
        {return p.Role==CloudTextRole.SaveField&&p.Context=="creationDateTime"&&p.RoleOwner!=null;}
        internal static bool TryFormat(CloudTextProfile p,string source,bool chinese,out string value,out string reason)
        {
            value=null;reason=null;if(!Scoped(p))return false;
            if(!chinese){reason="target-not-supported";return false;}
            var match=Native.Match(source??"");
            if(!match.Success){reason="unrecognized-native-format";return false;}
            int month=Array.IndexOf(Months,match.Groups["month"].Value)+1;
            if(month==0){reason="unrecognized-full-month";return false;}
            int hour=int.Parse(match.Groups["h"].Value,CultureInfo.InvariantCulture);
            int minute=int.Parse(match.Groups["m"].Value,CultureInfo.InvariantCulture);
            int day=int.Parse(match.Groups["d"].Value,CultureInfo.InvariantCulture);
            // Year is a separate native field and may still hold the previous
            // slot during this setter. Do not sample it; allow February 29.
            if(hour>23||minute>59||day<1||day>DateTime.DaysInMonth(2000,month))
            {reason="invalid-date-or-time-component";return false;}
            value=match.Groups["h"].Value+":"+match.Groups["m"].Value+"，"+
                month.ToString(CultureInfo.InvariantCulture)+"月"+day.ToString(CultureInfo.InvariantCulture)+"日";
            reason="local-native-H-mm-d-MMMM";return true;
        }
    }
}
