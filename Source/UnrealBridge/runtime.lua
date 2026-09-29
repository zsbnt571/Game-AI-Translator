-- Runtime discovery only. Never assigns widget Text or invokes SetText.
-- No UObject, remote property, FText or TArray survives a scan callback.
local M={}
local function stringValue(value)
 if type(value)=='string' then return value end
 if value~=nil then return value:ToString() end
end
local function bounded(value,maximum,empty)
 return type(value)=='string' and (empty or #value>0) and #value<=maximum and not value:find('\0',1,true)
end
local function arrayCount(value)
 if type(value)=='table' then return #value end
 return value:GetArrayNum()
end
local function arrayAt(value,index)
 local item=value[index]
 -- UFunction array returns in pinned UE4SS are ordinary Lua tables whose
 -- elements are RemoteUnrealParam wrappers, unlike direct TArray indexing.
 local ok,unwrapped=pcall(function() return item:get() end)
 return ok and unwrapped or item
end
function M.new(options)
 local self={}
 local lib=options.textlib
 local tablelib=options.tablelib
 local accept=assert(options.accept)
 local find=options.find or FindAllOf
 local name=options.name or FName
 local clock=options.clock or os.clock
 local identityReady=false
 local diagnostic={identity='not-tested',stringTables='not-tested',visibleSeen=0,identified=0,unidentified=0,tableIdentities=0,eligible=0,protected=0,conflicts=0,errors=0,visibility='parent-flags-only',visibilityUnknown=0,hiddenAncestors=0,
  scanCalls=0,enumerations=0,lastEnumerationMs=0,maxEnumerationMs=0,lastScanMs=0,maxScanMs=0,widgetHintErrors=0,
  tableCalls=0,lastTableMs=0,maxTableMs=0}
 local cursors={TextBlock=1,RichTextBlock=1}
 local classes={'TextBlock','RichTextBlock'}
 local nextClass=1
 local tableCursor=1
 local tableQueue,tableSeen={},{}
 local nextTables=0
 local seen={};local seenCount=0
 -- Capture is independent of replacement eligibility. Only copied strings are
 -- retained; a display string must never be promoted to a localization ID.
 local observed,observedOrder={},{}
 local snapshotClock=options.snapshotClock or os.time or clock
 local function forget(key)
  if not key or not observed[key] then return end
  observed[key]=nil
  for index,item in ipairs(observedOrder) do if item==key then table.remove(observedOrder,index);break end end
 end
 local function observe(display,class,key)
  -- Production UObject paths identify an observation slot, never a future
  -- write target. A reused path simply replaces the old copied display value.
  key=key or (class..'\0'..display)
  if not observed[key] then
   if #observedOrder>=128 then forget(observedOrder[1]) end
   observedOrder[#observedOrder+1]=key
  end
  observed[key]={text=display,class=class,seen=snapshotClock()}
 end
 local function visibleParents(widget,started)
  -- IsVisible only describes a widget's local flag. Follow reflected panel
  -- parents before capture so a hidden panel does not enqueue its labels.
  -- These temporary wrappers never leave this synchronous callback. This
  -- still cannot prove viewport attachment, clipping or switcher selection.
  local current=widget;local visited={}
  for depth=1,32 do
   -- Always allow the first local check: otherwise the enumeration's own
   -- elapsed time can indefinitely starve even a root widget of both classes.
   if depth>1 and clock()-started>=0.003 or visited[current] or not current:IsValid() then return nil end
   visited[current]=true
   if not current:IsVisible() then return false end
   local parent=current:GetParent()
   if parent==nil then return true end
   if not parent:IsValid() then
    -- UE4SS can return an invalid wrapper for a legitimate null parent.
    -- An invalid nonzero parent is not evidence of a visible root.
    if parent:GetAddress()==0 then return true end
    return nil
   end
   current=parent
  end
  return nil
 end
 local function inspect(text)
  if options.inspect then return options.inspect(text) end
  if options.textapi then return options.textapi.inspect(text) end
  -- Use one out table twice: pinned UE4SS can retain a scalar out argument
  -- on the Lua stack. A shared table works with that and normal consumption.
  local out={}
  if not lib:GetTextId(text,out,out) then return nil end
  return {ns=stringValue(out.OutNamespace),key=stringValue(out.OutKey),source=stringValue(lib:GetTextSourceString(text))}
 end
 local function valid(e)
  return type(e)=='table' and bounded(e.ns,4096,true) and bounded(e.key,4096,false) and bounded(e.source,6000,false)
 end
 local function publish(e,display,origin)
  if not valid(e) then return false end
  local id=e.ns..'\0'..e.key..'\0'..e.source
  local previous=seen[id]
  if previous then
   -- A table entry may be discovered before its widget becomes visible. Recheck
   -- changed display values so prior discovery cannot bypass later protection.
   if origin=='visible-ftext' and (not previous.visible or previous.display~=display) then
    local decision=accept(e,display,origin)
    if decision=='protected' then diagnostic.protected=diagnostic.protected+1 end
    if decision=='conflict' then diagnostic.conflicts=diagnostic.conflicts+1 end
    if not previous.visible then diagnostic.identified=diagnostic.identified+1 end
    previous.visible=true;previous.display=display
   end
   return false
  end
  if seenCount>=100000 then diagnostic.limit=true;return false end
  -- Native culture is inferred by the engine's Game category. Current UI
  -- language is not evidence of the source string's native culture.
  e.culture='';e.runtime=true;e.origin=origin
  local decision=accept(e,display,origin)
  if decision~=false then
   seen[id]={display=display,visible=origin=='visible-ftext'};seenCount=seenCount+1
   if origin=='string-table' then diagnostic.tableIdentities=diagnostic.tableIdentities+1 else diagnostic.identified=diagnostic.identified+1 end
   if decision=='protected' then diagnostic.protected=diagnostic.protected+1
   elseif decision=='conflict' then diagnostic.conflicts=diagnostic.conflicts+1
   elseif decision~='known' then diagnostic.eligible=diagnostic.eligible+1 end
   return true
  end
  return false
 end
 function self.probe(cases)
  identityReady=false
  local function failed(reason,detail)
   diagnostic.identity=reason;diagnostic.identityDetail=detail or reason
   if options.textapi then options.textapi.probeResult(reason) end
   return false
  end
  if type(cases)~='table' or #cases<2 then return failed('identity-probe-missing') end
  for _,test in ipairs(cases) do
   local ok,e,reason=pcall(inspect,test.text)
   if not ok then return failed('identity-api-unavailable','call-failed') end
   if not e and reason and reason~='identity-not-returned' then return failed('identity-api-unavailable',reason) end
   if not valid(e) or e.ns~=test.ns or e.key~=test.key then return failed('identity-roundtrip-failed',reason or 'identity-changed-or-lost') end
   if e.source~=test.source then return failed('source-roundtrip-failed','source-changed-or-lost') end
  end
  identityReady=true;diagnostic.identity=options.inspect and 'native-provider-ready' or 'reflected-identity-ready'
  diagnostic.identityDetail='two-identities-and-sources-confirmed'
  if options.textapi then options.textapi.probeResult(diagnostic.identity) end
  return true
 end
 function self.scan()
  local started=clock();local attempts=0;diagnostic.scanCalls=diagnostic.scanCalls+1
  local first=nextClass;nextClass=3-nextClass
  for offset=0,1 do
   local class=classes[(first+offset-1)%2+1]
   local began=clock();local ok,blocks=pcall(find,class)
   diagnostic.enumerations=diagnostic.enumerations+1
   diagnostic.lastEnumerationMs=math.max(0,(clock()-began)*1000)
   diagnostic.maxEnumerationMs=math.max(diagnostic.maxEnumerationMs,diagnostic.lastEnumerationMs)
   if not ok then diagnostic.errors=diagnostic.errors+1;blocks={} end
   blocks=blocks or {};local total=#blocks
   if total>0 then
    local cursor=math.min(cursors[class],total)
    for _=1,math.min(total,16) do
     local b=blocks[cursor];cursor=cursor%total+1;attempts=attempts+1
     local observed=false
     local good=pcall(function()
      if not b or not b:IsValid() then return end
      local named,key=pcall(function() return b:GetFullName() end)
      if not named or not bounded(key,4096,false) then key=nil end
      -- Native enumeration may already exceed the scan budget. Give this
      -- widget its own bounded parent check; otherwise every non-root label
      -- after a slow FindAllOf is permanently classified as unknown.
      local checked,visible=pcall(visibleParents,b,clock())
      if not checked or visible~=true then
       if checked and visible==false then diagnostic.hiddenAncestors=diagnostic.hiddenAncestors+1
       else diagnostic.visibilityUnknown=diagnostic.visibilityUnknown+1 end
       forget(key);return
      end
      local text=b:GetText();local display=stringValue(text)
      if not bounded(display,6000,false) then forget(key);return end
      diagnostic.visibleSeen=diagnostic.visibleSeen+1
      observe(display,class,key)
      if options.onWidget and key then
       local hinted=pcall(function()
        local objectPath=key:match('%s(.+)$')
        if objectPath then options.onWidget(objectPath,b:GetAddress(),class) end
       end)
       if not hinted then diagnostic.widgetHintErrors=diagnostic.widgetHintErrors+1 end
      end
      observed=true
      if not identityReady then diagnostic.unidentified=diagnostic.unidentified+1;return end
      local e=inspect(text)
      if not valid(e) then diagnostic.unidentified=diagnostic.unidentified+1;return end
      publish(e,display,'visible-ftext')
     end)
     if not good then
      diagnostic.errors=diagnostic.errors+1
      if observed then diagnostic.unidentified=diagnostic.unidentified+1 end
     end
     if clock()-started>=0.003 then break end
    end
    cursors[class]=cursor
   else
    cursors[class]=1
    for index=#observedOrder,1,-1 do
     local key=observedOrder[index]
     if observed[key].class==class then forget(key) end
    end
   end
   if clock()-started>=0.003 then break end
  end
  diagnostic.lastWidgetAttempts=attempts
  diagnostic.lastScanMs=math.max(0,(clock()-started)*1000)
  diagnostic.maxScanMs=math.max(diagnostic.maxScanMs,diagnostic.lastScanMs)
 end
 function self.observed()
  local result,unique={},{}
  local now=snapshotClock()
  for index=#observedOrder,1,-1 do
   local key=observedOrder[index];local item=observed[key]
   if now-item.seen>=5 then forget(key) end
  end
  for _,key in ipairs(observedOrder) do
   local item=observed[key]
   if not unique[item.text] then
    result[#result+1]={text=item.text,class=item.class};unique[item.text]=true
   end
  end
  return result
 end
 function self.tables()
  if not tablelib then diagnostic.stringTables='library-unavailable';return end
  local tableStarted=clock();diagnostic.tableCalls=diagnostic.tableCalls+1
  local processingItem=false
  local ok=pcall(function()
   if clock()>=nextTables then
    nextTables=clock()+5
    local tables=tablelib:GetRegisteredStringTables();local count=arrayCount(tables)
    diagnostic.registeredTables=count
    for index=1,math.min(count,1024) do
     local tableName=stringValue(arrayAt(tables,index))
     if bounded(tableName,4096,false) and not tableSeen[tableName] then
      tableSeen[tableName]=true;tableQueue[#tableQueue+1]={name=tableName,cursor=1}
     end
    end
    if count>1024 then diagnostic.limit=true end
   end
   if #tableQueue==0 then diagnostic.stringTables='ready-empty';return end
   local item=tableQueue[tableCursor]
   if not item then diagnostic.stringTables='ready';return end
   processingItem=true
   -- The loader synchronously materializes an entire returned TArray. Take a
   -- single key snapshot per registered table, copy its values immediately and
   -- never retain RemoteParam wrappers or refetch that array each frame.
   local id=name(item.name)
   if not item.keys then
    item.namespace=stringValue(tablelib:GetTableNamespace(id))
    local keys=tablelib:GetKeysFromStringTable(id);local size=arrayCount(keys)
    item.keys={}
    for index=1,math.min(size,100000) do
     local key=stringValue(arrayAt(keys,index))
     if bounded(key,4096,false) then item.keys[#item.keys+1]=key end
    end
    if size>100000 then diagnostic.limit=true end
   end
   local started=clock();local processed=0
   while item.cursor<=#item.keys and processed<32 do
    local key=item.keys[item.cursor];item.cursor=item.cursor+1;processed=processed+1
    local source=stringValue(tablelib:GetTableEntrySourceString(id,key))
    publish({ns=item.namespace,key=key,source=source},nil,'string-table')
    if clock()-started>=0.003 then break end
   end
   if item.cursor>#item.keys then item.keys={};tableCursor=tableCursor+1 end
   diagnostic.stringTables='ready';diagnostic.lastTableAttempts=processed
  end)
  if not ok then
   diagnostic.stringTables='api-call-failed';diagnostic.errors=diagnostic.errors+1
   if processingItem then tableCursor=tableCursor+1 end
  end
  diagnostic.lastTableMs=math.max(0,(clock()-tableStarted)*1000)
  diagnostic.maxTableMs=math.max(diagnostic.maxTableMs,diagnostic.lastTableMs)
 end
 function self.status()
  local result={};for k,v in pairs(diagnostic) do result[k]=v end
  result.visibleRead=true
  result.identityReplacement=identityReady
  if not identityReady then
   result.warning='运行时补译尚未完全接入，部分屏幕文字仍会保留原文。'
  elseif diagnostic.unidentified>0 then
   result.warning='已发现部分未能补译的屏幕文字，当前保留原文。'
  else result.warning='' end
  return result
 end
 function self.reset()
  seen={};seenCount=0;cursors={TextBlock=1,RichTextBlock=1};nextClass=1;tableCursor=1;tableQueue={};tableSeen={};nextTables=0
  observed,observedOrder={},{}
 end
 return self
end
return M
