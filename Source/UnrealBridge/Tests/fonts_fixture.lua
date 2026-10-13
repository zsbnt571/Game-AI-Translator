-- Neutral reflected-font contract. No fonts, engine assets or renderer loaded.
local cases={}
local function test(name,run) cases[#cases+1]={name=name,run=run} end
local function new()
 local state={now=10,ignore=false,setters=0,text='继续',address=64,onThread=true,hooks={},unregistered={}}
 RegisterHook=function(path,pre,post)state.hooks[path]={pre=pre,post=post};return 11,12 end
 UnregisterHook=function(path,pre,post)assert(pre==11 and post==12);state.hooks[path]=nil;state.unregistered[#state.unregistered+1]=path end
 IsInGameThread=function()return state.onThread end
 local function object(path)
  return {IsValid=function()return true end,GetFullName=function()return 'Font '..path end}
 end
 local original,foreign=object('/Fixture/Original'),object('/Fixture/Foreign')
 local fallback=object('/Engine/EngineFonts/Roboto.Roboto')
 local registry={['/Fixture/Original']=original,['/Fixture/Foreign']=foreign,['/Engine/EngineFonts/Roboto.Roboto']=fallback}
 local function copyFont(f)
  local out={};for k,v in pairs(f)do out[k]=v end;return out
 end
 local scratchFont
 local scratch={IsValid=function()return true end,GetFullName=function()return 'TextBlock /Fixture/Scratch' end,
  GetAddress=function()return 900 end,IsA=function(_,c)return c=='/Script/UMG.TextBlock' end}
 setmetatable(scratch,{__index=function(_,k)if k=='Font' then return scratchFont end end,
  __newindex=function(_,k,v)assert(k=='Font');scratchFont=copyFont(v)end})
 registry['/Fixture/Scratch']=scratch
 registry['/Script/UMG.TextBlock']=object('/Script/UMG.TextBlock')
 local gs=object('/Script/Engine.Default__GameplayStatics')
 function gs:SpawnObject()return scratch end
 registry['/Script/Engine.Default__GameplayStatics']=gs
 FName=function(value)return {ToString=function()return value end}end
 state.live={FontObject=original,TypefaceFontName=FName('Regular')}
 local widget={IsValid=function()return true end,IsVisible=function()return true end,GetAddress=function()return state.address end,
  GetFullName=function()return 'TextBlock /Fixture/Widget' end,
  IsA=function(_,name)return name=='/Script/UMG.TextBlock' end,
  GetText=function()return {ToString=function()return state.text end}end,
  InvalidateLayoutAndVolatility=function()end}
 setmetatable(widget,{__index=function(_,key)
  if key=='Font' then return state.alias and state.live or copyFont(state.live) end
 end})
 function widget:SetFont(font)
  state.setters=state.setters+1
  if state.alias and state.live.FontObject==font.FontObject and state.live.TypefaceFontName:ToString()==font.TypefaceFontName:ToString() then return end
  if not state.ignore then state.live=copyFont(font);state.renderedFont=font.FontObject end
 end
 registry['/Fixture/Widget']=widget
 StaticFindObject=function(path)state.now=state.now+(state.lookupCost or 0);return registry[path]end
 LoadAsset=function(path)return registry[path]end
 FindAllOf=function(class)if class=='Font'then return state.shared or {}end;error('hinted UMG path must not enumerate separately')end
 os.clock=function()return state.now end
 local fonts=assert(load(FONTS_SOURCE,'@fonts.lua','t',_ENV))()
 fonts.useRuntimeHints();fonts.hint('/Fixture/Widget',64,'TextBlock')
 state.original,state.foreign,state.fallback,state.widget=original,foreign,fallback,widget
 state.registry=registry
 function state.menu()
  local panel={children={widget},visible=true,address=500}
  function panel:IsValid()return true end
  function panel:IsVisible()return self.visible end
  function panel:GetAddress()return self.address end
  function panel:GetFullName()return 'CanvasPanel /Fixture/Panel' end
  function panel:IsA(n)return n=='/Script/UMG.PanelWidget' end
  function panel:GetChildrenCount()return #self.children end
  function panel:GetChildAt(i)return self.children[i+1] end
  local menu={visible=true,address=600,WidgetTree={IsValid=function()return true end,RootWidget=panel}}
  function menu:IsValid()return true end
  function menu:IsVisible()return self.visible end
  function menu:GetAddress()return self.address end
  function menu:GetFullName()return 'UserWidget /Fixture/Menu' end
  function menu:IsA(n)return n=='/Script/UMG.UserWidget' end
  registry['/Fixture/Menu']=menu;registry['/Fixture/Panel']=panel
  function state.shown(method,b)
   local hook=assert(state.hooks['/Script/UMG.'..(method or 'UserWidget:AddToViewport')])
   assert(hook.pre()==nil);assert(hook.post({get=function()return b or menu end})==nil)
  end
  return menu,panel
 end
 function state.fire(class,context)
  local h=assert(state.hooks['/Script/UMG.'..(class or 'TextBlock')..':SetText'])
  assert(h.pre()==nil,'pre hook overrides native result')
  assert(h.post(context or {get=function()return widget end})==nil,'post hook overrides native result')
 end
 return state,fonts
end
test('successful font apply and exact original recovery',function()
 local state,fonts=new();fonts.update({['继续']=true},{},true)
 assert(fonts.count()==1 and state.live.FontObject==state.fallback)
 assert(fonts.restoreStep(32) and fonts.count()==0 and state.live.FontObject==state.original)
end)
test('borrowed font property cannot bypass the native equality guard',function()
 local state,fonts=new();state.alias=true;state.live.Size=22;state.live.LetterSpacing=7
 local material={};state.live.FontMaterial=material
 fonts.update({['继续']=true},{},true)
 assert(state.renderedFont==state.fallback,'only the property changed, not the rendered font')
 assert(state.live.Size==22 and state.live.LetterSpacing==7 and state.live.FontMaterial==material)
 assert(fonts.restoreStep(32) and state.renderedFont==state.original)
end)
test('ignored apply leaves recovery record and failure diagnostic',function()
 local state,fonts=new();state.ignore=true;fonts.update({['继续']=true},{},true)
 assert(fonts.count()==1 and state.live.FontObject==state.original)
 assert(fonts.diagnostics().errors>0,'silent no-op setter was reported successful')
 state.ignore=false;assert(fonts.restoreStep(32) and fonts.count()==0)
end)
test('ignored restore never clears ownership early',function()
 local state,fonts=new();fonts.update({['继续']=true},{},true);state.ignore=true
 assert(not fonts.restoreStep(32) and fonts.count()==1,'unconfirmed restore discarded its record')
 assert(state.live.FontObject==state.fallback)
 state.ignore=false;assert(fonts.restoreStep(32) and state.live.FontObject==state.original)
end)
test('external font is preserved after own override',function()
 local state,fonts=new();fonts.update({['继续']=true},{},true)
 state.live.FontObject=state.foreign;local previous=state.setters
 assert(fonts.restoreStep(32) and state.live.FontObject==state.foreign and state.setters==previous)
end)
test('protected existing Chinese font is never modified',function()
 local state,fonts=new();fonts.update({['继续']=true},{['继续']=true},true)
 assert(fonts.count()==0 and state.setters==0)
end)
test('setter prepares cached CJK font without polling or enumeration',function()
 local state,fonts=new();assert(fonts.startEvents(function()return true,{['继续']=true},{} end))
 assert(fonts.diagnostics().eventHooks>=2 and state.setters==0)
 state.fire();assert(state.live.FontObject==state.fallback and state.setters==1)
 state.fire();assert(state.setters==1,'same cached label repeatedly changes font')
 assert(fonts.diagnostics().eventApplied==1 and fonts.diagnostics().updateCalls==0)
 assert(fonts.restoreStep(32) and state.live.FontObject==state.original)
end)
test('setter prepares a recreated widget at the same path',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end)
 state.fire();state.address=128;state.live={FontObject=state.original,TypefaceFontName=FName('Regular')}
 state.fire();assert(state.live.FontObject==state.fallback and state.setters==2)
 assert(fonts.restoreStep(32) and state.live.FontObject==state.original)
end)
test('setter recovers exact game font reset but preserves a foreign font',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end)
 state.fire();state.live={FontObject=state.original,TypefaceFontName=FName('Regular')}
 state.fire();assert(state.setters==2 and state.live.FontObject==state.fallback)
 state.live={FontObject=state.foreign,TypefaceFontName=FName('Foreign')};state.fire()
 assert(state.setters==2 and state.live.FontObject==state.foreign)
 assert(fonts.restoreStep(32) and state.live.FontObject==state.foreign)
end)
test('native font reset adopts and restores latest face',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);state.fire()
 state.live={FontObject=state.original,TypefaceFontName=FName('NewFace')}
 local h=assert(state.hooks['/Script/UMG.TextBlock:SetFont'])
 h.post({get=function()return state.widget end})
 assert(state.live.FontObject==state.fallback and state.live.TypefaceFontName:ToString()=='None')
 assert(fonts.restoreStep(32));assert(state.live.FontObject==state.original and state.live.TypefaceFontName:ToString()=='NewFace')
end)
test('native font event leaves unrelated font family untouched',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);state.fire()
 state.live={FontObject=state.foreign,TypefaceFontName=FName('Foreign')};local n=state.setters
 state.hooks['/Script/UMG.TextBlock:SetFont'].post({get=function()return state.widget end})
 assert(state.live.FontObject==state.foreign and state.setters==n)
 assert(fonts.restoreStep(32) and state.live.FontObject==state.foreign)
end)
test('setter uses current profile state and skips protected native Chinese',function()
 local state,fonts=new();local enabled=false;local owned={['继续']=true};local protected={}
 fonts.startEvents(function()return enabled,owned,protected end);state.fire();assert(state.setters==0)
 enabled=true;owned={};state.fire();assert(state.setters==0)
 owned={['继续']=true};protected={['继续']=true};state.fire();assert(state.setters==0)
 protected={};state.text='Continue';owned['Continue']=true;state.fire();assert(state.setters==0)
 state.text='继续';state.fire();assert(state.setters==1)
end)
test('setter rejects unknown or non-game thread and keeps fallback polling',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end)
 state.onThread=false;state.fire();assert(state.setters==0 and fonts.diagnostics().eventThreadSkipped==1)
 IsInGameThread=function()error('unsupported')end;state.fire();assert(state.setters==0)
 fonts.update({['继续']=true},{},true);assert(state.setters==1)
end)
test('missing hook APIs preserve existing hinted font preparation',function()
 local state,fonts=new();RegisterHook=nil;assert(not fonts.startEvents(function()return true,{},{} end))
 fonts.update({['继续']=true},{},true);assert(state.setters==1)
end)
test('bad setter context is contained and hooks are cleaned up',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end)
 state.fire(nil,{get=function()error('stale borrowed context')end});assert(fonts.diagnostics().eventErrors==1 and state.setters==0)
 state.fire();assert(state.setters==1)
 local count=fonts.diagnostics().eventHooks;fonts.closeEvents();assert(#state.unregistered==count and fonts.diagnostics().eventHooks==0)
 assert(fonts.restoreStep(32) and state.live.FontObject==state.original)
end)
test('RichText run styles are preserved when default override is disabled',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end)
 state.fire('RichTextBlock');assert(state.setters==0 and fonts.count()==0)
end)
test('serialized menu labels prepare on show without any text setter',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);state.menu()
 state.shown();assert(state.setters==1 and fonts.diagnostics().eventCalls==0)
 assert(fonts.diagnostics().shownApplied==1 and fonts.diagnostics().updateCalls==0)
 state.shown();assert(state.setters==1,'reopen needlessly rebuilds font')
 state.live={FontObject=state.original,TypefaceFontName=FName('Regular')};state.shown('Widget:SetVisibility')
 assert(state.setters==2 and state.live.FontObject==state.fallback)
 assert(fonts.restoreStep(32) and state.live.FontObject==state.original)
end)
test('menu show handles replaced control and respects foreign font',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);state.menu();state.shown()
 state.address=128;state.live={FontObject=state.original,TypefaceFontName=FName('Regular')};state.shown()
 assert(state.setters==2)
 state.live.FontObject=state.foreign;state.shown();assert(state.setters==2 and state.live.FontObject==state.foreign)
end)

test('foreign font remains protected across polling and later menu events',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);state.menu();state.shown()
 state.live.FontObject=state.foreign
 for i=1,5 do
  state.now=state.now+.2;fonts.hint('/Fixture/Widget',64,'TextBlock')
  fonts.update({['继续']=true},{},true);state.shown();state.fire()
 end
 assert(state.setters==1 and state.live.FontObject==state.foreign)
 assert(fonts.restoreStep(32) and state.live.FontObject==state.foreign)
end)
test('hidden subtree and non-game thread never mutate',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);local menu,panel=state.menu()
 panel.visible=false;state.shown();assert(state.setters==0)
 panel.visible=true;state.onThread=false;state.shown();assert(state.setters==0)
 state.onThread=true;state.shown('UserWidget:AddToPlayerScreen');assert(state.setters==1)
end)
test('menu pending traversal resumes next frame without polling',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);state.menu();state.lookupCost=.004
 state.shown();assert(state.setters==0 and fonts.diagnostics().shownPending==1)
 state.lookupCost=0;fonts.flushEvents();assert(state.setters==1 and fonts.diagnostics().shownPending==0)
end)
test('hidden or detached ancestor cancels a delayed child',function()
 for _,mode in ipairs({'hidden','detached','replaced'})do
  local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);local menu,panel=state.menu();state.lookupCost=.004
  state.shown();fonts.flushEvents();assert(state.setters==0)
  if mode=='hidden' then panel.visible=false elseif mode=='detached' then panel.children={} else panel.address=501 end
  state.lookupCost=0;fonts.flushEvents();assert(state.setters==0,mode)
 end
end)
test('disable clears pending tree and destroyed root is discarded',function()
 for _,mode in ipairs({'disabled','destroyed'})do
  local state,fonts=new();local active=true;fonts.startEvents(function()return active,{['继续']=true},{} end);state.menu();state.lookupCost=.004
  state.shown();if mode=='disabled' then active=false else state.registry['/Fixture/Menu']=nil end
  state.lookupCost=0;fonts.flushEvents();assert(state.setters==0 and fonts.diagnostics().shownPending==0)
 end
end)
test('deep cyclic or wide menu traversal is bounded',function()
 local state,fonts=new();fonts.startEvents(function()return true,{['继续']=true},{} end);local menu,panel=state.menu()
 panel.children={menu};state.shown();assert(fonts.diagnostics().shownPending==0 and state.setters==0)
 for i=1,2000 do panel.children[i]=state.widget end
 state.shown();assert(fonts.diagnostics().shownPending==1)
 for i=1,8 do fonts.flushEvents() end
 assert(fonts.diagnostics().shownPending==0 and state.setters==1)
end)
local function sharedFixture()
 local state,fonts=new();local serial=1000
 local function font(path,rows,scale)
  serial=serial+1;local address=serial
  local current={Fonts=rows or {}}
  local fallback=setmetatable({ScalingFactor=scale or 1},{__index=function(_,k)if k=='Typeface'then return current end end,
   __newindex=function(_,k,v)assert(k=='Typeface');local list={};for i,row in ipairs(v.Fonts)do list[i]=row end;current={Fonts=list}end})
  local f={FontCacheType=1,CompositeFont={DefaultTypeface={Fonts={{Name=FName('Regular')}}},FallbackTypeface=fallback}}
  function f:IsValid()return true end;function f:GetFullName()return 'Font '..path end
  function f:GetAddress()return address end;function f:IsA(c)return c=='/Script/Engine.Font'end
  state.registry[path]=f;return f
 end
 local face={IsValid=function()return true end,GetFullName=function()return 'FontFace /Engine/CJK.Face'end}
 local row={Name=FName('Fallback'),Font={FontFaceAsset=face,FontFilename=FName(''),Hinting=0,LoadingPolicy=0,SubFaceIndex=0}}
 local engine=font('/Engine/EngineFonts/Roboto.Roboto',{row})
 state.fallback=engine;local game=font('/Game/UI/Body.Body',nil,.8);state.shared={game}
 return state,fonts,game,engine,font
end
test('native Slate empty fallback added with scale/default preserved and restored',function()
 local s,f,g,e=sharedFixture();local original=g.CompositeFont.DefaultTypeface
 f.update({['继续']=true},{},true)
 assert(#g.CompositeFont.FallbackTypeface.Typeface.Fonts==1 and g.CompositeFont.FallbackTypeface.ScalingFactor==.8)
 assert(g.CompositeFont.DefaultTypeface==original and f.diagnostics().sharedApplied==1)
 assert(f.restoreStep()and #g.CompositeFont.FallbackTypeface.Typeface.Fonts==0 and #e.CompositeFont.FallbackTypeface.Typeface.Fonts==1)
end)
test('shared fallback preserves foreign fallback and non-runtime fonts',function()
 local s,f,g,e=sharedFixture();g.CompositeFont.FallbackTypeface.Typeface=e.CompositeFont.FallbackTypeface.Typeface
 f.update({['继续']=true},{},true);assert(f.diagnostics().sharedApplied==0)
 g.CompositeFont.FallbackTypeface.Typeface.Fonts={};g.FontCacheType=0;s.now=s.now+2
 f.update({['继续']=true},{},true);assert(f.diagnostics().sharedApplied==0)
end)
test('shared font replacement cannot inherit restore ownership',function()
 local s,f,g,e,make=sharedFixture();f.update({['继续']=true},{},true)
 local newer=make('/Game/UI/Body.Body',e.CompositeFont.FallbackTypeface.Typeface.Fonts)
 assert(f.restoreStep()and #newer.CompositeFont.FallbackTypeface.Typeface.Fonts==1)
end)
test('foreign shared fallback mutation survives disable',function()
 local s,f,g=sharedFixture();f.update({['继续']=true},{},true)
 g.CompositeFont.FallbackTypeface.ScalingFactor=.55
 assert(f.restoreStep()and #g.CompositeFont.FallbackTypeface.Typeface.Fonts==1 and g.CompositeFont.FallbackTypeface.ScalingFactor==.55)
end)
test('shared enumeration rotates beyond the first page',function()
 local s,f,g,e,make=sharedFixture();s.shared={}
 for i=1,270 do local item=make('/Game/UI/Font'..i);item.FontCacheType=0;s.shared[i]=item end
 s.shared[270]=g
 for i=1,40 do s.now=s.now+1.1;f.update({['继续']=true},{},true)end
 assert(#g.CompositeFont.FallbackTypeface.Typeface.Fonts==1)
end)
local result={}
for _,case in ipairs(cases)do
 local ok,message=pcall(case.run);result[#result+1]={name=case.name,passed=ok,detail=ok and '' or tostring(message)}
end
return result

