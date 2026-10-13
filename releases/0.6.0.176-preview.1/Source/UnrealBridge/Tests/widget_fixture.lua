local checks={}
local function test(name,fn)
 local ok,err=pcall(fn);checks[#checks+1]={name=name,passed=ok,detail=not ok and tostring(err) or nil}
end
local function fixture()
 local objects={};local cache={Pants='裤子',Shoes='鞋子'};local state={enabled=true}
 local function text(s)return {ToString=function()return s end} end
 local function widget(path,source,address)
  local b={value=source,address=address or 1,writes=0,exists=true,bound=false}
  function b:IsValid()return self.exists end
  function b:IsA(c)return c=='/Script/UMG.TextBlock' end
  function b:GetFullName()return 'TextBlock '..path end
  function b:GetAddress()return self.address end
  function b:GetText()return text(self.value) end
  function b:SetText(t)self.writes=self.writes+1;self.value=t:ToString() end
  b.TextDelegate={FunctionName=text('None'),Object={IsValid=function()return false end,GetAddress=function()return 0 end}}
  objects[path]=b;return b
 end
 local api=MODULE.new({resolve=function(path)return objects[path] end,makeText=text,
  active=function()return state.enabled end,clock=function()return state.now or 0 end,
  lookup=function(s,growing)if state.stream then return state.stream.lookup(s,growing,function(k)return cache[k]end)end;return cache[s] end,
  protected=function(s)return s:find('中文',1,true)~=nil end,applied=function()if state.applied then state.applied()end end})
 local function binding(b,source,id)
  local path=b:GetFullName():match('%s(.+)$')..'.Binding';local d={source=source,address=id or 10,exists=true}
  function d:IsValid()return self.exists end
  function d:IsA(c)return c=='/Script/UMG.TextBinding' end
  function d:GetAddress()return self.address end
  function d:GetFullName()return 'TextBinding '..path end
  function d:GetOuter()return b end
  b.TextDelegate={FunctionName=text('GetTextValue'),Object=d};objects[path]=d
  function b:GetText()
   local v={value=text(d.source)};function v:get()return self.value end;function v:set(s)self.value=s end
   api.bound(d,v);return v:get()
  end
  return d
 end
 return api,widget,cache,objects,text,binding,state
end
test('cache writes and restores latest game source',function()
 local api,w=fixture();local b=w('/Engine/Transient.Game.Label','Pants')
 assert(api.visit(b,true) and b.value=='裤子');b.value='Shoes';assert(api.visit(b,true) and b.value=='鞋子')
 assert(api.restoreStep() and b.value=='Shoes')
end)
test('asset templates never written',function()
 local api,w=fixture();local b=w('/Game/UI/Menu.Menu_C:WidgetTree.Label','Pants')
 assert(not api.visit(b,true) and b.writes==0)
end)
test('bound and unknown delegates preserved',function()
 local api,w,_,_,text=fixture();local b=w('/Engine/Transient.Game.Label','Pants')
 b.TextDelegate.FunctionName=text('GetCaption');assert(not api.visit(b,true) and b.writes==0)
 b.TextDelegate=nil;assert(not api.visit(b,true) and b.writes==0)
end)
test('newer game text not overwritten on disable',function()
 local api,w=fixture();local b=w('/Engine/Transient.Game.Label','Pants');api.visit(b,true)
 b.value='Another writer';assert(api.restoreStep() and b.value=='Another writer')
end)
test('reused object path cannot inherit restoration',function()
 local api,w=fixture();local b=w('/Engine/Transient.Game.Label','Pants');api.visit(b,true)
 local newer=w('/Engine/Transient.Game.Label','New label',2);assert(api.restoreStep() and newer.writes==0)
end)
test('bound delegate added after our write is preserved',function()
 local api,w,_,_,text=fixture();local b=w('/Engine/Transient.Game.Label','Pants');api.visit(b,true)
 b.TextDelegate.FunctionName=text('Dynamic');local n=b.writes;assert(api.restoreStep() and b.writes==n)
end)
test('existing Chinese and blank cache preserved',function()
 local api,w,cache=fixture();cache.Pants='';local b=w('/Engine/Transient.Game.Label','Pants');api.visit(b,true);assert(b.writes==0)
 b.value='原有中文';assert(not api.visit(b,true) and b.writes==0)
end)
test('late cache applies without another setter',function()
 local api,w,cache=fixture();cache.Pants=nil;local b=w('/Engine/Transient.Game.Label','Pants');api.visit(b,true)
 assert(api.accepts('Pants'));cache.Pants='裤子';api.update();assert(b.value=='裤子')
end)
test('restoration retry after failure',function()
 local api,w=fixture();local b=w('/Engine/Transient.Game.Label','Pants');api.visit(b,true)
 local set=b.SetText;b.SetText=function()error('Failed setter')end
 assert(not api.restoreStep() and api.count()==1);b.SetText=set;assert(api.restoreStep() and b.value=='Pants')
end)
test('native binding cache hit never calls a setter or alters its source',function()
 local api,w,_,_,_,bind,state=fixture();local b=w('/Engine/Transient.Game.Label','unused');local d=bind(b,'Pants')
 assert(b:GetText():ToString()=='裤子' and b.writes==0 and d.source=='Pants')
 assert(api.accepts('Pants'));state.enabled=false;api.restoreStep()
 assert(b:GetText():ToString()=='Pants' and b.TextDelegate.Object==d and b.writes==0)
 state.enabled=true;assert(b:GetText():ToString()=='裤子')
end)
test('binding reevaluation follows new text and rejects stale permission',function()
 local api,w,_,_,_,bind=fixture();local b=w('/Engine/Transient.Game.Label','unused');local d=bind(b,'Pants')
 b:GetText();d.source='Shoes';assert(not api.accepts('Pants'));assert(b:GetText():ToString()=='鞋子')
 local s=api.sources();assert(#s==1 and s[1]=='Shoes' and d.source=='Shoes')
end)
test('binding destruction and replaced destination cannot confer ownership',function()
 local api,w,_,_,text,bind=fixture();local b=w('/Engine/Transient.Game.Label','unused');local d=bind(b,'Pants')
 b:GetText();b.TextDelegate.FunctionName=text('Other');assert(not api.accepts('Pants'));assert(b:GetText():ToString()=='Pants')
 assert(#api.sources()==0 and b.writes==0)
end)
test('blank translation does not blank a bound button',function()
 local api,w,cache,_,_,bind=fixture();local b=w('/Engine/Transient.Game.Label','unused');bind(b,'Pants');cache.Pants='  '
 assert(b:GetText():ToString()=='Pants' and b.writes==0)
end)
test('source publishing cannot starve behind update cursor',function()
 local api,w=fixture();local seen={}
 for i=1,96 do api.visit(w('/Engine/Transient.Game.Label'..i,'Source'..i,i),false) end
 for i=1,9 do api.update();for _,s in ipairs(api.sources())do seen[s]=true end end
 local n=0;for _ in pairs(seen)do n=n+1 end;assert(n==96)
end)
test('source snapshot is byte bounded',function()
 local api,w=fixture()
 for i=1,32 do api.visit(w('/Engine/Transient.Game.Label'..i,string.rep('x',23000)..i,i),false) end
 local n=0;for _,s in ipairs(api.sources())do n=n+#s end;assert(n>0 and n<=65536)
end)
test('binding observation capacity never blocks cached getters in later menus',function()
 local api,w,_,_,_,bind,state=fixture();local controls={}
 for i=1,1300 do
  local b=w('/Engine/Transient.Game.Menu'..i..'.Label','unused',i);local d=bind(b,'Pants',i+2000)
  controls[i]={b,d}
  assert(b:GetText():ToString()=='裤子','later cached binding stayed original: '..i)
  assert(api.status().bindingRecords<=512,'binding metadata became unbounded')
 end
 for _,i in ipairs({1,512,513,800,1300,1,1300})do
  local b,d=table.unpack(controls[i]);assert(b:GetText():ToString()=='裤子' and b.writes==0 and d.source=='Pants')
 end
 state.enabled=false;api.restoreStep()
 for _,row in ipairs(controls)do assert(row[1]:GetText():ToString()=='Pants' and row[1].TextDelegate.Object==row[2])end
end)
test('rotated binding observations do not confer permission for old text',function()
 local api,w,cache,_,_,bind=fixture()
 for i=1,600 do
  local b=w('/Engine/Transient.Game.Menu'..i..'.Label','unused',i);bind(b,'Source '..i,i+1000);b:GetText()
 end
 assert(not api.accepts('Source 1'),'evicted copied string retained write permission')
 assert(api.accepts('Source 600'),'current binding lost write permission')
 cache['Source 600']='当前文字'
 local seen=false;for i=1,40 do for _,s in ipairs(api.sources())do seen=seen or s=='Source 600' end end
 assert(seen and api.status().bindingRecords<=512)
end)
test('later unbound menus translate without losing any restoration owner',function()
 local api,w=fixture();local controls={}
 for i=1,1400 do
  local b=w('/Engine/Transient.Game.Menu'..i..'.Caption','Pants',i);controls[i]=b
  assert(api.visit(b,true) and b.value=='裤子','unbound cache blocked at '..i)
 end
 assert(api.status().ownedRecords==1400 and api.status().observations==0)
 controls[1].value='Latest game update';controls[2].exists=false
 local n=0;while not api.restoreStep() do n=n+1;assert(n<100)end
 assert(controls[1].value=='Latest game update' and controls[2].writes==1)
 for i=3,#controls do assert(controls[i].value=='Pants')end
end)

test('untranslated observations rotate independently of translated owners',function()
 local api,w,cache=fixture();local kept=w('/Engine/Transient.Game.Owned','Pants');api.visit(kept,true)
 for i=1,1400 do api.visit(w('/Engine/Transient.Game.Uncached'..i,'Unknown '..i,i+1),true) end
 assert(api.status().observations==512 and api.status().ownedRecords==1)
 assert(not api.accepts('Unknown 1') and api.accepts('Unknown 1400'))
 cache['Unknown 1400']='已找到';for i=1,20 do api.update()end
 assert(api.status().ownedRecords==2 and kept.value=='裤子')
 local n=0;while not api.restoreStep() do n=n+1;assert(n<100)end
 assert(kept.value=='Pants')
end)

test('destroyed owners are reclaimed within bounded updates',function()
 local api,w=fixture();local controls={}
 for i=1,900 do controls[i]=w('/Engine/Transient.Game.Caption'..i,'Pants',i);api.visit(controls[i],true)end
 for _,b in ipairs(controls)do b.exists=false end
 api.update();assert(api.count()==868,'a frame must visit no more than 32 records')
 for i=1,30 do api.update()end;assert(api.count()==0)
 local new=w('/Engine/Transient.Game.Caption1','Shoes',2000);api.visit(new,true);assert(new.value=='鞋子')
 assert(api.restoreStep() and new.value=='Shoes')
end)

test('failed writes retain restoration ownership under observation pressure',function()
 local api,w=fixture();local b=w('/Engine/Transient.Game.Failed','Pants');local setter=b.SetText
 b.SetText=function(self,t)setter(self,t);error('failure after native assignment')end
 assert(not api.visit(b,true) and b.value=='裤子')
 for i=1,600 do api.visit(w('/Engine/Transient.Game.Waiting'..i,'Unknown '..i,i+1),false)end
 assert(api.status().ownedRecords==1)
 b.SetText=setter;for i=1,20 do if api.restoreStep() then break end end
 assert(api.count()==0 and b.value=='Pants')
end)

test('source collection advances through owners beyond the old cap',function()
 local api,w,cache=fixture();local seen={}
 for i=1,900 do cache['Label '..i]='译文 '..i;api.visit(w('/Engine/Transient.Game.Caption'..i,'Label '..i,i),true)end
 for i=1,35 do api.update();for _,s in ipairs(api.sources())do seen[s]=true end end
 local count=0;for _ in pairs(seen)do count=count+1 end;assert(count==900)
end)
test('reenable revisits every restored cached control beyond the old cap',function()
 local api,w,_,_,_,_,state=fixture();local controls={}
 for i=1,1400 do controls[i]=w('/Engine/Transient.Game.Menu'..i..'.Caption','Pants',i);api.visit(controls[i],true)end
 for cycle=1,3 do
  state.enabled=false
  for i=1,50 do if api.restoreStep() then break end end
  assert(api.count()==0 and api.status().resumePending==1400)
  api.update();assert(api.count()==0,'disabled hints performed writes')
  for _,b in ipairs(controls)do assert(b.value=='Pants')end
  state.enabled=true;api.update();assert(api.count()==32,'resume exceeded per-frame work cap')
  for i=1,50 do api.update()end
  for _,b in ipairs(controls)do assert(b.value=='裤子')end
  assert(api.status().resumePending==0 and api.count()==1400)
 end
end)

test('reenable hints preserve changed text and replaced or rebound controls',function()
 local api,w,_,_,text,_,state=fixture();local list={}
 for i=1,4 do list[i]=w('/Engine/Transient.Game.Caption'..i,'Pants',i);api.visit(list[i],true)end
 state.enabled=false;assert(api.restoreStep());list[1].value='New game value'
 local replacement=w('/Engine/Transient.Game.Caption2','Pants',900)
 list[3].TextDelegate.FunctionName=text('Dynamic');list[4].exists=false
 local writes=list[3].writes;state.enabled=true;api.update()
 assert(list[1].value=='New game value' and replacement.writes==0 and list[3].writes==writes and list[4].value=='Pants')
 assert(api.status().resumePending==0 and api.count()==0)
end)
test('growing cached dialogue publishes full source and restores latest fragment',function()
 local api,w,cache,_,_,_,state=fixture();state.stream=STREAM.new()
 local full='After thinking for a while, I wrote my reply.';state.stream.add(full);cache[full]='想了一会儿后，我写下了回复。'
 cache['After thinking']='旧的半句话'
 local b=w('/Engine/Transient.Game.Dialogue','After t');api.visit(b,true)
 b.value='After thinking';api.visit(b,true)
 assert(b.value~='旧的半句话' and b.value~='After thinking' and api.requestSource('After thinking')==full)
 assert(api.sources()[1]==full)
 b.value='After thinking for a while';api.visit(b,true);assert(utf8.len(b.value))
 state.enabled=false;assert(api.restoreStep()and b.value=='After thinking for a while')
end)
test('ambiguous growing fragments defer then settle without blanking',function()
 local api,w,cache,_,_,_,state=fixture();state.stream=STREAM.new()
 state.stream.add('After thinking I left.');state.stream.add('After thinking I stayed.')
 local b=w('/Engine/Transient.Game.Dialogue','After t');api.visit(b,true)
 b.value='After thinking';api.visit(b,true)
 assert(b.value=='After thinking' and api.requestSource('After thinking')==false and #api.sources()==0)
 state.now=.4;cache['After thinking']='想过之后';api.update()
 assert(b.value=='想过之后' and api.sources()[1]=='After thinking')
end)
test('bound typewriter uses full cache without changing game source or setter',function()
 local api,w,cache,_,_,bind,state=fixture();state.stream=STREAM.new()
 local full='After thinking for a while, I wrote my reply.';state.stream.add(full);cache[full]='想了一会儿后，我写下了回复。'
 local b=w('/Engine/Transient.Game.Dialogue','unused');local d=bind(b,'After t');b:GetText()
 d.source='After thinking';local value=b:GetText():ToString()
 assert(value~='After thinking' and api.requestSource('After thinking')==full and b.writes==0 and d.source=='After thinking')
 state.enabled=false;api.restoreStep();assert(b:GetText():ToString()=='After thinking')
end)
test('visible owned label takes a newer cache value without reopening',function()
 local api,w,cache=fixture();local b=w('/Engine/Transient.Game.Caption','Pants');api.visit(b,true)
 cache.Pants='长裤';api.update();assert(b.value=='长裤' and b.writes==2)
 api.update();assert(b.writes==2,'unchanged cache rewrote the same widget')
 assert(api.restoreStep()and b.value=='Pants')
end)

test('failed font preparation retries without repeating the text setter',function()
 local api,w,_,_,_,_,state=fixture();local b=w('/Engine/Transient.Game.Caption','Pants');local n=0
 state.applied=function()n=n+1;if n==1 then error('Transient font callback failure')end end
 assert(not api.visit(b,true)and b.value=='裤子');api.update()
 assert(n==2 and b.writes==1 and api.status().applied==1)
 api.update();assert(n==2 and b.writes==1)
 assert(api.restoreStep()and b.value=='Pants')
end)

test('late cache setter failure keeps the prior translation owned',function()
 local api,w,cache=fixture();local b=w('/Engine/Transient.Game.Caption','Pants');api.visit(b,true)
 local setter=b.SetText;cache.Pants='长裤';b.SetText=function()error('Transient setter failure before assignment')end
 assert(not api.visit(b,true)and b.value=='裤子')
 b.SetText=setter;api.update();assert(b.value=='长裤')
 assert(api.restoreStep()and b.value=='Pants')
end)

return checks
