namespace ScreenshotTranslationUiTester;

internal static class RgssLanguageBranches
{
    internal sealed record Branch(string Language,string Group);
    private sealed class Frame(int start,int indent,RgssLanguageSwitch? setting,int expected)
    {
        internal int Start=start,Indent=indent,Else=-1,End=-1,Expected=expected;
        internal RgssLanguageSwitch? Setting=setting;
    }
    // Only standard event condition trees are understood. No save files,
    // Ruby conditions, page switch states or current language are evaluated.
    internal static IReadOnlyDictionary<RubyMarshalData.Node,Branch> Read(RubyMarshalData.Node root,IReadOnlyList<RgssLanguageSwitch> settings,Action check)
    {
        var result=new Dictionary<RubyMarshalData.Node,Branch>(ReferenceEqualityComparer.Instance);
        if(settings.Count==0)return result;
        var switches=settings.ToDictionary(s=>s.Id);var ordinary=new HashSet<RubyMarshalData.Node>(ReferenceEqualityComparer.Instance);
        var pending=new Stack<RubyMarshalData.Node>();var visited=new HashSet<RubyMarshalData.Node>(ReferenceEqualityComparer.Instance);pending.Push(root);
        while(pending.Count>0)
        {
            check();var node=pending.Pop();if(!visited.Add(node))continue;
            if(node.Kind=='['&&node.Items.Count>0&&node.Items.All(Command))
            {
                var tags=ListBranches(node.Items,switches,check);
                for(int i=0;i<node.Items.Count;i++)
                {
                    var command=node.Items[i];
                    if(!tags.TryGetValue(i,out var tag)){ordinary.Add(command);result.Remove(command);}
                    else if(!ordinary.Contains(command))
                    {if(result.TryGetValue(command,out var old)&&old!=tag){ordinary.Add(command);result.Remove(command);}else result[command]=tag;}
                }
            }
            foreach(var value in node.Fields.Values)pending.Push(value);
            foreach(var value in node.Items)pending.Push(value);
        }
        return result;
    }
    private static bool Command(RubyMarshalData.Node n)=>n.Kind=='o'&&n.Name=="RPG::EventCommand";
    private static bool Number(RubyMarshalData.Node n,string field,out int number)
    {number=0;if(!n.Fields.TryGetValue(field,out var value)||value.Kind!='i')return false;number=value.Number;return true;}
    private static Dictionary<int,Branch> ListBranches(IReadOnlyList<RubyMarshalData.Node> commands,IReadOnlyDictionary<int,RgssLanguageSwitch> switches,Action check)
    {
        var result=new Dictionary<int,Branch>();var stack=new Stack<Frame>();var frames=new List<Frame>();
        for(int i=0;i<commands.Count;i++)
        {
            check();var node=commands[i];if(!Number(node,"@code",out int code)||!Number(node,"@indent",out int indent)||indent is <0 or >128)return [];
            if(code==111)
            {
                if(stack.Count>0&&indent<=stack.Peek().Indent)return [];
                RgssLanguageSwitch? setting=null;int expected=-1;
                if(node.Fields.TryGetValue("@parameters",out var values)&&values.Items.Count==3&&values.Items.All(n=>n.Kind=='i')&&
                    values.Items[0].Number==0&&values.Items[2].Number is 0 or 1)
                {switches.TryGetValue(values.Items[1].Number,out setting);expected=values.Items[2].Number;}
                stack.Push(new(i,indent,setting,expected));
            }
            else if(code==411)
            {
                if(stack.Count==0||stack.Peek().Indent!=indent||stack.Peek().Else>=0)return [];
                stack.Peek().Else=i;
            }
            else if(code==412)
            {
                if(stack.Count==0||stack.Peek().Indent!=indent)return [];
                var frame=stack.Pop();frame.End=i;
                if(frame.Setting!=null&&frame.Else>frame.Start+1&&frame.End>frame.Else+1)frames.Add(frame);
            }
            else if(stack.Count>0&&indent<=stack.Peek().Indent&&code!=0)return [];
        }
        if(stack.Count>0)return [];
        var starts=frames.ToDictionary(f=>f.Start);var active=new List<Frame>();
        for(int i=0;i<commands.Count;i++)
        {
            check();active.RemoveAll(f=>f.End==i);
            var applicable=active.Where(f=>f.Else!=i).ToArray();
            string Locale(Frame f)=>i<f.Else?(f.Expected==0?f.Setting!.OnLanguage:f.Setting!.OffLanguage):(f.Expected==0?f.Setting!.OffLanguage:f.Setting!.OnLanguage);
            // Conflicting nested language controls do not grant evidence.
            if(applicable.Length>0&&applicable.Select(Locale).Distinct().Count()==1)
                result[i]=new(Locale(applicable[^1]),"rgss-switch:"+applicable[^1].Setting!.Id);
            if(starts.TryGetValue(i,out var start))active.Add(start);
        }
        return result;
    }
}
