using System.Text.Json;
using ScreenshotTranslationUiTester.CorePipelineV2;
namespace ScreenshotTranslationUiTester;

internal static class NativeContentContractSelfTests
{
    internal static int Run(string output,string c3Root)
    {
        Directory.CreateDirectory(output);var rows=new List<object>();var failed=0;
        void Check(string name,bool pass,object evidence){if(!pass)failed++;rows.Add(new{Name=name,Pass=pass,Evidence=evidence});}
        void Numeric(string name,string source,string target,bool expected)
        {
            var actual=CorePipelineEngine.NumericTokensMatch(source,target);
            Check(name,actual==expected,new{Source=source,Target=target,Expected=expected,Actual=actual,
                SourceTokens=NumericFidelityV2.Describe(source,target,true),TargetTokens=NumericFidelityV2.Describe(target,source,false)});
        }
        var numericCases=new (string,string,string,bool)[]
        {
            ("one_to_arabic","Receive one pill","获得1粒药丸",true),
            ("two_to_arabic","Receive two bottles of beer","获得2瓶啤酒",true),
            ("one_to_chinese","Receive one pill","获得一粒药丸",true),
            ("two_to_liang","Receive two bottles","获得两瓶",true),
            ("han_to_arabic","获得两瓶啤酒","获得2瓶啤酒",true),
            ("arabic_to_han","Receive 2 bottles","获得两瓶",true),
            ("quantity_changed","Receive two bottles","获得3瓶",false),
            ("quantity_dropped","Receive two bottles","获得啤酒",false),
            ("quantity_added","Receive beer","获得2瓶啤酒",false),
            ("repeated_preserved","two bottles and two pills","2瓶和2粒",true),
            ("repeated_lost","two bottles and two pills","2瓶和药丸",false),
            ("quantities_swapped","one red pill and two blue pills","2粒红药和1粒蓝药",false),
            ("per_condition_moved","supply one pill for every 5,000 defeated","每击败5,000只，补给1粒药",true),
            ("per_reward_swapped","supply one pill for every 5,000 defeated","每击败1只，补给5,000粒药",false),
            ("per_repetition_lost","one pill for every 5,000 and two bottles for every 5,000","每击败5,000只，补给1粒和2瓶",false),
            ("decimal_preserved","Gain 1.5 points","获得1.5点",true),
            ("decimal_changed","Gain 1.5 points","获得15点",false),
            ("decimal_han","1.5 points","一点五点",true),
            ("negative_kept","Lose -5 HP","失去-5生命",true),
            ("negative_lost","Lose -5 HP","失去5生命",false),
            ("negative_han","-5","负五",true),
            ("percent_kept","Gain 20%","获得百分之二十",true),
            ("percent_lost","Gain 20%","获得20",false),
            ("range_kept","43-48 damage","43至48伤害",true),
            ("range_end_changed","43-48 damage","43至49伤害",false),
            ("range_to_list","43-48 damage","43和48伤害",false),
            ("date_kept","2026/6/28 11:31","2026/6/28 11:31",true),
            ("date_swapped","2026/6/28","2026/28/6",false),
            ("date_localized","2026/6/28","2026年6月28日",true),
            ("ordinal_old","6th-gen","第六代",true),
            ("ordinal_mismatch","6th-gen","六代",false),
            ("magnitude_han","5,000 defeated","击败五千只",true),
            ("magnitude_loss","30,000 monsters","3,000只怪物",false),
            ("positional_han","2026","二零二六",true),
            ("fraction_old","33/5000 x 2","33/5000 × 2",true),
            ("identifier_old","G-5YNC","G-SYNC",true),
            ("identifier_oc","0C\nMultiple","原创角色\n多人",true),
            ("identifier_version","v2","v3",false),
            ("identifier_numeric_type","B2","2",false),
            ("identifier_word","OneDrive","OneDrive",true),
            ("identifier_one_hyphen","one-time reward","一次性奖励",true),
            ("noncount_the_one","You are the one","你就是那个人",true),
            ("noncount_one_another","Help one another","互相帮助",true),
            ("noncount_no_one","No one was there","那里没有人",true),
            ("noncount_one_of","one of the best","最好的之一",true),
            ("noncount_idiom","at one with nature","与自然融为一体",true),
            ("noncount_added_digit","You are the one","你是1个人",false),
            ("noncount_one_ordinal","Receive one pill","获得第1粒药",false)
        };
        foreach(var (name,source,target,expected) in numericCases)Numeric(name,source,target,expected);
        foreach(var (name,source,target,expected) in new (string,string,string,bool)[]
        {
            ("named_date","April 15, 2017","2017年4月15日",true),
            ("short_named_date","Apr 13","4月13日",true),
            ("birth_date","Date of Birth September 13, 1992","出生日期 1992年9月13日",true),
            ("joined_ocr_date","SpeakerNameApr 13\nHello.","发言者4月13日\n你好。",true),
            ("day_first_date","13 September 1992","1992年9月13日",true),
            ("abbreviation_dot","Sep. 13, 1992","1992年9月13日",true),
            ("date_changed_month","Apr 13","5月13日",false),
            ("date_changed_day","April 15, 2017","2017年4月16日",false),
            ("date_lost_year","April 15, 2017","4月15日",false),
            ("date_added_year","Apr 13","2017年4月13日",false),
            ("date_duplicate","Apr 13","4月13日和4月13日",false),
            ("date_recombined_pairs","Jan 2, 2024; March 4, 2025","2024年3月4日；2025年1月2日",false),
            ("date_leap","February 29, 2024","2024年2月29日",true),
            ("date_quantity_kept","Apr 13: two pills","4月13日：2粒药",true),
            ("date_quantity_changed","Apr 13: two pills","4月13日：3粒药",false),
            ("date_not_identifier","Apr13","4月13日",false),
            ("procedural_first","We must first decide on our plan.","我们必须先确定计划。",true),
            ("procedural_first_quantity","You should first collect two pills.","你应该先收集两粒药。",true),
            ("procedural_first_wrong_quantity","You should first collect two pills.","你应该先收集三粒药。",false),
            ("ordinary_ordinal_still_required","Open the first chest.","打开宝箱。",false),
            ("ordinary_ordinal_wrong","Open the first chest.","打开第三个宝箱。",false)
        })Numeric(name,source,target,expected);
        var realPath=Path.Combine(c3Root,"runs","C3-numeric-representation","results","NUMERIC-REPRESENTATION-PROBE.json");
        using(var json=JsonDocument.Parse(File.ReadAllText(realPath)))
            foreach(var row in json.RootElement.GetProperty("Rows").EnumerateArray())
                Numeric("actual_NEW006_"+row.GetProperty("Id").GetString(),row.GetProperty("Source").GetString()!,row.GetProperty("Translated").GetString()!,true);

        Numeric("article_one_preserved","A developer","一位开发者",true);
        Numeric("article_quantity_two_rejected","A developer","两位开发者",false);
        Numeric("article_one_budget_exhausted","A developer","一位开发者和一位助手",false);
        Numeric("article_does_not_hide_missing_quantity","A reward includes two pills","一个奖励包含药丸",false);
        Numeric("article_does_not_hide_extra_two","A reward includes two pills","一个奖励包含两粒药丸和两瓶啤酒",false);
        Numeric("article_does_not_hide_quantity_swap","A reward includes one red pill and two blue pills","一个奖励包含2粒红药和1粒蓝药",false);
        Numeric("idiom_one_piece","You are back in one piece","你完好无损地回来了",true);
        Numeric("idiom_one_place","Gathered all requests in one place","所有请求集中到了一处",true);
        Numeric("actual_one_place_classifier","Sir! Welcome. I've\ngathered all the requests\nfrom branches around the\nworld in one place.\nPlease check them out!","长官！欢迎光临。我已经把\n世界各地分部发来的所有请求\n都集中在一个地方了。\n请查看吧！",true);
        Numeric("locative_one_other_text","Keep your notes in one place","把笔记放在一个地方",true);
        Numeric("locative_one_location","Meet at one location","在一个地点见面",true);
        Numeric("locative_one_wrapped_spot","Gather in\none\nspot","集中在一个位置",true);
        Numeric("locative_one_no_classifier","Keep your notes in one place","集中存放笔记",true);
        Numeric("locative_one_reverse","在一个地点见面","Meet at one location",true);
        Numeric("locative_wrong_two","Keep your notes in one place","把笔记放在两个地方",false);
        Numeric("locative_duplicate_one","Keep your notes in one place","把笔记放在一个地方和一个地方",false);
        Numeric("locative_duplicate_with_article","A reward is in one place","一个奖励放在一个地方和一个地方",false);
        Numeric("locative_keeps_reward_one","Gather one pill in one place","在一个地方收集一粒药",true);
        Numeric("locative_does_not_hide_reward_one_loss","Gather one pill in one place","在一个地方收集药",false);
        Numeric("locative_does_not_hide_reward_two_loss","Gather two pills in one place","在一个地方收集药",false);
        Numeric("locative_wrong_reward_two","Gather one pill in one place","在一个地方收集两粒药",false);
        Numeric("locative_wrong_reward_order","At one location, one red pill and two blue pills","在一个地点，2粒红药和1粒蓝药",false);
        Numeric("locative_decimal_unchanged","Keep 1.5 liters in one place","在一个地方存放1.5升",true);
        Numeric("locative_decimal_changed","Keep 1.5 liters in one place","在一个地方存放15升",false);
        Numeric("locative_per_reward_unchanged","In one place, supply one pill for every 5000 defeated","在一个地方，每击败5000只补给一粒药",true);
        Numeric("locative_per_reward_changed","In one place, supply one pill for every 5000 defeated","在一个地方，每击败5000只补给两粒药",false);
        Numeric("oc_verbatim","0C","0C",true);
        Numeric("oc_reading","0C","OC",true);
        Numeric("oc_verbatim_structured","0C\nMultiple","0C\n多人",true);
        Numeric("oc_semantic_expansion_retained","0C\nMultiple","原创角色\n多人",true);
        Numeric("oc_changed_code","0C","0D",false);
        Numeric("oc_missing","0C","",false);
        Numeric("oc_missing_in_structure","0C\nMultiple","多人",false);
        Numeric("oc_duplicate","0C","0C 0C",false);
        Numeric("oc_duplicate_alias","0C","OC 原创角色",false);
        Numeric("oc_added","Multiple","0C 多人",false);
        Numeric("oc_numeric_zero_preserved","0C 0","0C 0",true);
        Numeric("oc_numeric_zero_changed","0C 0","0C 1",false);
        Numeric("numeric_zero_changed","0","1",false);
        Numeric("oc_numeric_added","0C","0C 2",false);
        Numeric("oc_existing_ambiguous_once_preserved","Once v","一旦",false);
        Numeric("oc_existing_name_ambiguity_preserved","Gomei followed their gaze.","五代目顺着他们的目光看去。",false);
        Numeric("chapter_ordinal","Chapter 1 - Part 2","第一章 - 第二部分",true);
        Numeric("first_ordinal","The first person","第一个人",true);
        Numeric("frequency","Once","一次",true);
        Numeric("semantic_hashtag_number","#musicmania2","#音乐狂人2",true);
        Numeric("semantic_hashtag_bad_number","#musicmania2","#音乐狂人3",false);
        Numeric("noncount_next","The next chapter","下一章",true);
        for(var quantity=1;quantity<=20;quantity++)
        {
            Numeric("repeat_kept_"+quantity,$"Receive {quantity} red pills and {quantity} blue pills",$"获得{quantity}粒红药和{quantity}粒蓝药",true);
            Numeric("repeat_missing_"+quantity,$"Receive {quantity} red pills and {quantity} blue pills",$"获得{quantity}粒红药和蓝药",false);
            Numeric("signed_changed_"+quantity,$"Lose -{quantity} HP",$"失去{quantity}生命",false);
        }

        Numeric("date_dash_localized","2026-09-05","2026年9月5日",true);
        Numeric("date_dot_localized","2026.09.05","2026年9月5日",true);
        Numeric("date_dot_changed","2026.09.05","2026年9月6日",false);
        Numeric("date_dash_swapped","2026-09-05","2026年5月9日",false);
        Numeric("second_duration","Wait one second","等一秒",true);
        Numeric("negative_range","-5--1","-5至-1",true);
        Numeric("noncount_once_clause","Once you are ready","一旦你准备好了",true);
        Numeric("noncount_first_place","In the first place","首先",true);
        var settings=new ApiSettings();
        var originalItem=new TranslationItem("CACHE","A\nB",StructuredTextRole.Unknown,["R1","R2"],TranslationIdentityContract.CoreV2Block);
        var otherLines=originalItem with{SourceIds=["R1"]};
        var legacyItem=originalItem with{IdentityContract=TranslationIdentityContract.LegacyAllocationV1};
        Check("cache_structure_identity",TranslationCacheKeyBuilder.Build([originalItem],settings)!=TranslationCacheKeyBuilder.Build([otherLines],settings),new{ContractVersion=CoreTranslationContentValidator.ContractVersion});
        Check("cache_control_plane_identity",TranslationCacheKeyBuilder.Build([originalItem],settings)!=TranslationCacheKeyBuilder.Build([legacyItem],settings),new{ContractVersion=CoreTranslationContentValidator.ContractVersion});

        TranslationItem Item(string source,int count=2,TranslationIdentityContract identity=TranslationIdentityContract.CoreV2Block)=>
            new("B1",source,StructuredTextRole.Unknown,Enumerable.Range(1,count).Select(x=>"R"+x).ToArray(),identity);
        void Structure(string name,TranslationItem item,string returned,string expected,bool accepted,bool recovered)
        {
            var events=new List<TranslationBoundaryEvent>();
            using var observer=TranslationBoundaryDiagnostics.Begin(events.Add);
            var result=CoreTranslationContentValidator.Validate(item,returned,name);
            Check(name,result.Accepted==accepted&&result.Recovered==recovered&&result.Text==expected,new{item.Text,Returned=returned,Result=result,Events=events});
        }
        Structure("actual_NEW009_new_game",Item("New game\nClick to begin the story"),@"新游戏\n点击开始故事","新游戏\n点击开始故事",true,true);
        Structure("actual_NEW009_load",Item("Load Game\nLoad a saved game"),@"读取存档\n读取已保存的游戏","读取存档\n读取已保存的游戏",true,true);
        Structure("actual_NEW009_preferences",Item("Preferences\nAdjust game settings"),@"设置\n调整游戏设置","设置\n调整游戏设置",true,true);
        Structure("shop_description",Item("A magical crystal.\nRaises maximum HP."),@"魔法水晶。\n提升最大HP。","魔法水晶。\n提升最大HP。",true,true);
        Structure("already_real_LF",Item("New game\nClick to begin"),"新游戏\n点击开始","新游戏\n点击开始",true,false);
        Structure("source_literal",Item(@"Show \n\n literally"),@"显示 \n\n 字面量",@"显示 \n\n 字面量",true,false);
        Structure("path_preserved",Item("Path\nC:\\new\\notes"),"路径\\nC:\\new\\notes","路径\\nC:\\new\\notes",true,false);
        Structure("code_preserved",Item("Example\nprint('\\n')"),"示例\\nprint('\\n')","示例\\nprint('\\n')",true,false);
        Structure("single_line_preserved",Item("Escape notation"),@"转义符\n",@"转义符\n",true,false);
        Structure("unverified_ocr_lines",Item("New game\nClick to begin",1),@"新游戏\n点击开始",@"新游戏\n点击开始",true,false);
        Structure("legacy_preserved",Item("New game\nClick to begin",2,TranslationIdentityContract.LegacyAllocationV1),@"新游戏\n点击开始",@"新游戏\n点击开始",true,false);
        Structure("double_escaped_rejected",Item("New game\nClick to begin"),@"新游戏\\n点击开始",@"新游戏\\n点击开始",false,false);
        Structure("wrong_line_count_rejected",Item("A\nB\nC",3),@"甲\n乙",@"甲\n乙",false,false);
        Structure("mixed_linebreak_rejected",Item("A\nB\nC",3),"甲\\n乙\n丙","甲\\n乙\n丙",false,false);
        Structure("numeric_revalidation",Item("Buy 2 bottles\nClick here"),@"购买3瓶\n点击这里",@"购买3瓶\n点击这里",false,false);
        Structure("variable_revalidation",Item("Hello {{user}}\nContinue"),@"你好\n继续",@"你好\n继续",false,false);
        Structure("variable_retained",Item("Hello {{user}}\nContinue"),@"你好{{user}}\n继续","你好{{user}}\n继续",true,true);

        string Envelope(string text)=>JsonSerializer.Serialize(new{choices=new[]{new{message=new{content=JsonSerializer.Serialize(new Dictionary<string,string>{{"B1",text}})}}}});
        var calls=0;var execution=TranslationRecoveryCoordinator.RunAsync([Item("New game\nClick to begin")],null,
            (_,_)=>Task.FromResult(Envelope(calls++==0?@"新游戏\\n点击开始":"新游戏\n点击开始")),null,default).GetAwaiter().GetResult();
        Check("production_bounded_retry",calls==2&&execution.Translations["B1"]=="新游戏\n点击开始"&&execution.Stats.RetryRequestCount==1,new{calls,execution.Stats});
        calls=0;
        try
        {
            TranslationRecoveryCoordinator.RunAsync([Item("New game\nClick to begin")],null,
                (_,_)=>{calls++;return Task.FromResult(Envelope(@"新游戏\\n点击开始"));},null,default).GetAwaiter().GetResult();
            Check("production_permanent_bad_structure",false,new{calls});
        }
        catch(TranslationRecoveryFailedException exception)
        {Check("production_permanent_bad_structure",calls<=TranslationRecoveryCoordinator.MaxTotalRecoveryRequests&&exception.RemainingMissingIds.SequenceEqual(new[]{"B1"}),new{calls,exception.RecoveryStats});}
        File.WriteAllText(Path.Combine(output,"CONTENT-CONTRACT-RESULTS.json"),JsonSerializer.Serialize(new
        {ContractVersion=CoreTranslationContentValidator.ContractVersion,Passed=rows.Count-failed,Failed=failed,RealApiCalls=0,Rows=rows},new JsonSerializerOptions{WriteIndented=true}));
        return failed==0?0:2;
    }
}
