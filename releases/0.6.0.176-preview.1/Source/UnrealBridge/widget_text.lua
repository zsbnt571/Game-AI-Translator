-- Exact-text fallback for live UMG TextBlocks. Unbound controls use SetText;
-- native TextBinding getters use only their return value, preserving binding.
-- Localization identity lookup is still preferred. Revalidate the instance,
-- address, delegate and current source in the same callback as each write.
-- No UObject, FText or delegate wrapper survives a callback.
local M={}
function M.new(options)
 local self={};local records,bySource={},{};local first,last,cursor,sourceCursor;local recordCount=0;local writing=false
 local waitingFirst,waitingLast;local waitingCount=0
 local resumed,resumeOrder={},{};local resumeHead,resumeTail=1,0
 local bindings,bindingOrder={},{};local bindingCursor,bindingSlot=1,1;local bindingActive=false;local bindingHook
 local resolve=options.resolve or StaticFindObject
 local makeText=options.makeText or FText
 local clock=options.clock or os.clock
 local stats={applied=0,restored=0,released=0,errors=0,boundSkipped=0,unknownSkipped=0,limitSkipped=0,bindingHits=0,bindingErrors=0,bindingRotated=0,observationsRotated=0}
 local function instance(b)
  if not b or not b:IsValid() or not b:IsA('/Script/UMG.TextBlock') then return end
  local path=b:GetFullName():match('%s(.+)$')
  if not path or not path:match('^/Engine/Transient%.') or path:find('Default__',1,true) then return end
  local address=b:GetAddress();if type(address)~='number' or address<=0 then return end
  return path,address
 end
 local function boundWidget(binding)
  if not binding or not binding:IsValid() or not binding:IsA('/Script/UMG.TextBinding') then return end
  local b=binding:GetOuter();local key,address=instance(b);if not key then return end
  local d=b.TextDelegate
  if type(d)~='table' or not d.FunctionName or d.FunctionName:ToString()~='GetTextValue'
   or not d.Object or not d.Object:IsValid() or d.Object:GetAddress()~=binding:GetAddress() then return end
  return b,key,address
 end
 local function boundLive(key,r)
  local binding=resolve(key);local b,path,address=boundWidget(binding)
  if b and path==r.widget and address==r.address and binding:GetAddress()==r.bindingAddress then return b end
 end
 function self.bound(binding,value)
  if bindingActive or not options.active or not options.active() then return end
  bindingActive=true
  local ok=pcall(function()
   local b,path,address=boundWidget(binding);if not b then return end
   local source=value:get():ToString()
   if type(source)~='string' or #source==0 or #source>24000 or source:find('\0',1,true) or options.protected(source) then return end
   local key=binding:GetFullName():match('%s(.+)$');if not key then return end
   local r=bindings[key]
   if not r then
    -- These entries are a bounded observation index, not restoration owners:
    -- only the current getter result is replaced. A retained old menu must
    -- never fill the index and prevent later controls from using their cache.
    -- Rotate scalar observations in O(1), revalidating the live instance below
    -- on every invocation; source properties and delegates stay untouched.
    r={}
    if #bindingOrder>=512 then
     bindingSlot=math.min(bindingSlot,#bindingOrder)
     bindings[bindingOrder[bindingSlot]]=nil;bindingOrder[bindingSlot]=key
     bindingSlot=bindingSlot%#bindingOrder+1;stats.bindingRotated=stats.bindingRotated+1
    else bindingOrder[#bindingOrder+1]=key end
    bindings[key]=r
   end
   local previous=r.source
   if previous~=source then
    r.streaming=previous and #source>#previous and source:sub(1,#previous)==previous or false;r.changedAt=clock()
   end
   r.widget=path;r.address=address;r.bindingAddress=binding:GetAddress();r.source=source
   local translated,canonical,defer=options.lookup(source,r.streaming)
   if defer and not canonical and clock()-(r.changedAt or 0)>=0.35 then translated=options.lookup(source,false);defer=false end
   if canonical then r.requestSource=canonical elseif defer then r.requestSource=false else r.requestSource=source end
   if type(translated)~='string' or translated==source or translated:match('^%s*$') or #translated>48000 or translated:find('\0',1,true) then r.applied=nil;return end
   -- Modify only this getter result. Its source property, delegate and setter
   -- remain untouched, so changing numbers/quests continues to use game logic.
   value:set(makeText(translated))
   if value:get():ToString()~=translated then error('Binding return update not confirmed') end
   stats.bindingHits=stats.bindingHits+1
   if r.applied~=translated then options.applied(translated,b) end
   r.applied=translated
  end)
  bindingActive=false
  if not ok then stats.bindingErrors=stats.bindingErrors+1 end
 end
 function self.startBindings()
  if type(RegisterHook)~='function' or type(UnregisterHook)~='function' or type(IsInGameThread)~='function' then return false end
  local path='/Script/UMG.TextBinding:GetTextValue'
  local ok,pre,post=pcall(function()
   local fn=resolve(path);if not fn or not fn:IsValid() then return end
   local fields={};fn:ForEachProperty(function(p)
    fields[#fields+1]={p:GetFName():ToString(),p:GetClass():GetFName():ToString()};if #fields>2 then return true end
   end)
   if #fields~=1 or fields[1][1]~='ReturnValue' or fields[1][2]~='TextProperty' then return end
   return RegisterHook(path,function()end,function(context,value)
    if IsInGameThread() then self.bound(context:get(),value) end
   end)
  end)
  if ok and type(pre)=='number' and type(post)=='number' then bindingHook={path,pre,post};return true end
  return false
 end
 function self.closeBindings()
  if bindingHook then
   local ok=pcall(UnregisterHook,bindingHook[1],bindingHook[2],bindingHook[3])
   if ok then bindingHook=nil else stats.bindingErrors=stats.bindingErrors+1 end
  end
 end
 local function stopWaiting(key,r)
  if not r.waiting then return end
  if r.waitingPrevious then records[r.waitingPrevious].waitingNext=r.waitingNext else waitingFirst=r.waitingNext end
  if r.waitingNext then records[r.waitingNext].waitingPrevious=r.waitingPrevious else waitingLast=r.waitingPrevious end
  r.waiting=nil;r.waitingPrevious=nil;r.waitingNext=nil;waitingCount=waitingCount-1
 end
 local function forget(key)
  local r=records[key];if not r then return end
  stopWaiting(key,r)
  if r.previous then records[r.previous].following=r.following else first=r.following end
  if r.following then records[r.following].previous=r.previous else last=r.previous end
  if cursor==key then cursor=r.following or first end
  if sourceCursor==key then sourceCursor=r.following or first end
  local sources=bySource[r.source];sources[key]=nil;if next(sources)==nil then bySource[r.source]=nil end
  records[key]=nil;recordCount=recordCount-1
 end
 local function observe(key,address,source)
  -- Only unmodified observations can be evicted. Restoration ownership is
  -- proportional to live controls actually changed, never capped by menu
  -- history. Linked cursors keep collection/update/restore work frame-bounded.
  if waitingCount>=512 then forget(waitingFirst);stats.observationsRotated=stats.observationsRotated+1 end
  local r={address=address,source=source,previous=last,waiting=true,waitingPrevious=waitingLast}
  if last then records[last].following=key else first=key end
  if waitingLast then records[waitingLast].waitingNext=key else waitingFirst=key end
  records[key]=r;last=key;waitingLast=key;recordCount=recordCount+1;waitingCount=waitingCount+1
  cursor=cursor or key;sourceCursor=sourceCursor or key
  bySource[source]=bySource[source] or {};bySource[source][key]=true
  return r
 end
 local function descriptor(b)
  if not b or not b:IsValid() or not b:IsA('/Script/UMG.TextBlock') then return end
  local path=b:GetFullName():match('%s(.+)$')
  -- Asset widget trees and class defaults are not live controls. This route
  -- deliberately supports transient UMG instances only, without guessing flags.
  if not path or not path:match('^/Engine/Transient%.') or path:find('Default__',1,true) then return end
  local address=b:GetAddress();if type(address)~='number' or address<=0 then return end
  local d=b.TextDelegate
  if type(d)~='table' or not d.FunctionName or not d.Object then stats.unknownSkipped=stats.unknownSkipped+1;return end
  if d.FunctionName:ToString()~='None' or d.Object:IsValid() or d.Object:GetAddress()~=0 then
   stats.boundSkipped=stats.boundSkipped+1;return
  end
  local text=b:GetText():ToString()
  if type(text)~='string' or #text==0 or #text>24000 or text:find('\0',1,true) then return end
  return path,address,text
 end
 local function live(key,r)
  local b=resolve(key);local k,a,text=descriptor(b)
  if k~=key or a~=r.address then return end
  return b,text
 end
 local function owned(r,text)
  return r and (text==r.applied or text==r.previousApplied)
 end
 local function confirmed(r,b)
  if not r.confirmed then stats.applied=stats.applied+1;r.confirmed=true end
  r.previousApplied=nil
  -- Text readback and font preparation are separate. A transient failure in
  -- the latter must remain retryable even though the translated Text is set.
  if r.fontPending then options.applied(r.applied,b);r.fontPending=nil end
 end
 function self.visit(b,apply)
  if writing then return false end
  local success,result=pcall(function()
   local key,address,source=descriptor(b);if not key then return false end
   local r=records[key]
   if r and r.address~=address then forget(key);r=nil end
   local current=source
   if owned(r,current) then
    -- Resolve the latest cache against the saved original, never our own
    -- Chinese display. This also makes corrections visible on an open menu.
    source=r.source
    if apply and current==r.applied then confirmed(r,b) end
   end
   -- A game update replaces our previous source; never force the old label.
   local previous=r and r.source
   local growing=previous and #source>#previous and source:sub(1,#previous)==previous
   if r and source~=r.source then forget(key);stats.released=stats.released+1;r=nil end
   if options.protected(source) then return false end
   if not r then
    r=observe(key,address,source)
    r.streaming=growing or false;r.changedAt=clock()
   end
   if not apply then return true end
   local translated,canonical,defer=options.lookup(source,r.streaming)
   if defer and not canonical and clock()-(r.changedAt or 0)>=0.35 then translated=options.lookup(source,false);defer=false end
   if canonical then r.requestSource=canonical elseif defer then r.requestSource=false else r.requestSource=source end
   if type(translated)~='string' or translated==source or translated:match('^%s*$') or #translated>48000 or translated:find('\0',1,true) then return true end
   if current==translated and owned(r,current) then return true end
   -- Record ownership before invoking an engine operation, including failure
   -- after assignment. Disable retries can then restore a partial write.
   stopWaiting(key,r)
   if r.applied~=translated then r.previousApplied=r.applied end
   r.applied=translated;r.confirmed=false;r.fontPending=true;writing=true
   local ok=pcall(function() b:SetText(makeText(translated)) end)
   writing=false
   if not ok or b:GetText():ToString()~=translated then stats.errors=stats.errors+1;return false end
   confirmed(r,b)
   return true
  end)
  writing=false
  if not success then stats.errors=stats.errors+1;return false end
  return result
 end
 function self.sources()
  local out,unique={},{};local count=math.min(32,recordCount);local start=clock();local bytes=0
  local function add(source)
   if not unique[source] and bytes+#source<=65536 then
    out[#out+1]=source;unique[source]=true;bytes=bytes+#source
   end
  end
  for _=1,count do
   if not sourceCursor or clock()-start>=0.0015 then break end
   local key=sourceCursor;local r=records[key];sourceCursor=r.following or first
   local ok,b,text=pcall(live,key,r)
   if not ok or not b then forget(key)
   elseif text~=r.source and not owned(r,text) then forget(key)
   else if r.requestSource~=false then add(r.requestSource or r.source)end end
  end
  start=clock()
  for _=1,math.min(16,#bindingOrder) do
   if #bindingOrder==0 or clock()-start>=0.0015 then break end
   bindingCursor=math.min(bindingCursor,#bindingOrder);local key=bindingOrder[bindingCursor]
   local r=bindings[key];local ok,b=pcall(boundLive,key,r)
   if not ok or not b then bindings[key]=nil;table.remove(bindingOrder,bindingCursor)
   else
    -- Reevaluate its real binding so a stale captured source never becomes
    -- write permission after the game changes the value.
    local good=pcall(function()b:GetText()end)
    if good and r.requestSource~=false then add(r.requestSource or r.source) end
    bindingCursor=bindingCursor%#bindingOrder+1
   end
  end
  return out
 end
 function self.requestSource(source)
  -- A fragment suppression is permission from a still-live observed control,
  -- not from a historical prefix match. Runtime capture uses this same choice.
  local start,visited=clock(),0
  for key in pairs(bySource[source]or{})do
   if visited>=64 or clock()-start>=0.0015 then break end;visited=visited+1
   local r=records[key];local ok,b,text=pcall(live,key,r)
   if ok and b and (text==r.source or owned(r,text))then return r.requestSource end
  end
  for _,key in ipairs(bindingOrder)do
   if visited>=512 or clock()-start>=0.0015 then break end;visited=visited+1
   local r=bindings[key]
   if r.source==source then local ok,b=pcall(boundLive,key,r);if ok and b then return r.requestSource end end
  end
 end
 function self.accepts(source)
  -- The observation still has to be live; a historical string alone is not
  -- permission. The actual setter performs the complete validation again.
  local start=clock();local visited=0
  for key in pairs(bySource[source] or {}) do
   if visited>=64 or clock()-start>=0.0015 then break end;visited=visited+1
   local r=records[key]
   local ok,b,text=pcall(live,key,r)
   if ok and b and (text==source or owned(r,text)) then return true end
   forget(key)
  end
  for _,key in ipairs(bindingOrder) do
   if visited>=512 or clock()-start>=0.0015 then break end;visited=visited+1
   local r=bindings[key]
   if r.source==source then
    local ok,b=pcall(boundLive,key,r)
    if ok and b then
     local good=pcall(function()b:GetText()end)
     if good and r.source==source then return true end
    end
   end
  end
  return false
 end
 function self.update()
  local start=clock();local visited=0
  -- Restoration releases ownership, but remember scalar hints for those same
  -- controls until enable can revisit them. Otherwise an unchanged static
  -- label has no new setter/show event and can stay original indefinitely.
  -- Hints are consumed, not accumulated across toggles; there is no hard cap
  -- that can silently strand later live controls. All work shares this budget.
  while resumeHead<=resumeTail and visited<32 and clock()-start<0.003 and options.active() do
   local key=resumeOrder[resumeHead];local r=resumed[key]
   resumeOrder[resumeHead]=nil;resumeHead=resumeHead+1;resumed[key]=nil;visited=visited+1
   local ok,b,text=pcall(live,key,r)
   if ok and b and text==r.source then self.visit(b,true) end
  end
  if resumeHead>resumeTail then resumed={};resumeOrder={};resumeHead=1;resumeTail=0 end
  local count=math.min(32-visited,recordCount)
  for _=1,count do
   if not cursor or clock()-start>=0.003 then break end
   local key=cursor;local r=records[key];cursor=r.following or first
   if r then
    local ok,b=pcall(live,key,r)
    if ok and b then self.visit(b,true) else forget(key) end
   end
  end
 end
 function self.restoreStep()
  -- Getter hooks stop replacing results as soon as the enabled flag clears.
  bindings={};bindingOrder={};bindingCursor=1;bindingSlot=1
  local start=clock();local key=first
  for _=1,math.min(32,recordCount) do
   if not key or clock()-start>=0.003 then break end
   local r=records[key]
   local following=r.following
   local ok=pcall(function()
    local b,text=live(key,r)
    if b and owned(r,text) then
     writing=true;b:SetText(makeText(r.source));writing=false
     if b:GetText():ToString()~=r.source then error('Widget restore not confirmed') end
     stats.restored=stats.restored+1
    end
    if b and r.applied and b:GetText():ToString()==r.source then
     if not resumed[key] then resumeTail=resumeTail+1;resumeOrder[resumeTail]=key end
     resumed[key]={address=r.address,source=r.source}
    end
    forget(key)
   end)
   writing=false;if not ok then stats.errors=stats.errors+1 end
   key=following
  end
  return recordCount==0
 end
 function self.count() return recordCount end
 function self.status() local r={};for k,v in pairs(stats) do r[k]=v end;r.records=recordCount;r.observations=waitingCount;r.ownedRecords=recordCount-waitingCount;r.resumePending=resumeTail-resumeHead+1;r.bindingRecords=#bindingOrder;r.bindingHook=bindingHook~=nil;r.mode='live-umg-text';return r end
 return self
end
return M
