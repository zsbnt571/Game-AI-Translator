-- Reversible per-widget CJK fallback, only for display strings changed by Fusion.
-- Keep sizes, materials, outlines and bindings. Shared runtime fonts gain only
-- an empty fallback face in memory; game files and existing faces stay intact.
local M={}
local records={}
-- Only paths, addresses and scalar metadata survive a tick. Never retain a
-- FindAllOf result or UObject/UScriptStruct wrapper across engine collection.
local firstKey,lastKey,recordCursor=nil,nil,nil
local recordCount=0
local scanClass,scanCursors,nextScanAt=1,{1,1},0
local classes={'TextBlock','RichTextBlock'}
local runtimeHints=false
local nextUpdateAt,hintsDirty=0,false
local hints,hintOrder,hintCursor={}, {},1
local recordLimit,scanLimit,stepSeconds,scanInterval=32,32,0.003,0.25
local eventHooks,eventActive,eventWindow,eventSpent,eventImmediate={},false,0,0,0
local showJobs,showKeys={},{}
local eventState,eventText
local settingFont=false
local scratchPath,scratchAddress
-- Native Slate can use a shared UFont directly, with no UMG TextBlock. Fill
-- only an EMPTY runtime fallback on game fonts. Default faces, sizes and
-- existing fallback/subtypeface rules remain owned by the game.
local sharedFonts,sharedOrder,sharedCursor={}, {},1
local sharedPending,sharedPendingCursor={},1
local nextSharedScan,sharedScanCursor=0,1
local stats={errors=0,applyErrors=0,restoreErrors=0,scanErrors=0,missingOriginal=0,
 scanCalls=0,scanned=0,budgetYields=0,lastEnumerationMs=0,maxEnumerationMs=0,
 lastUpdateMs=0,maxUpdateMs=0,hintVisits=0,hintErrors=0,updateCalls=0,updateSkipped=0,
 eventCalls=0,eventApplied=0,eventErrors=0,eventDeferred=0,eventThreadSkipped=0,
 shownCalls=0,shownNodes=0,shownApplied=0,shownErrors=0,shownDeferred=0,shownDropped=0,
 lastShownMs=0,maxShownMs=0,eventRegistrationErrors=0,sharedApplied=0,sharedRestored=0,sharedErrors=0,
 sharedScanCalls=0,maxSharedScanMs=0,timeBasis='os.clock'}
local function failed(kind)
 stats.errors=stats.errors+1;stats[kind]=stats[kind]+1
end
local function addRecord(key,r)
 r.previous=lastKey;r.following=nil
 if lastKey then records[lastKey].following=key else firstKey=key end
 records[key]=r;lastKey=key;recordCount=recordCount+1
 if not recordCursor then recordCursor=key end
end
local function removeRecord(key)
 local r=records[key];if not r then return end
 local following=r.following
 if r.previous then records[r.previous].following=following else firstKey=following end
 if following then records[following].previous=r.previous else lastKey=r.previous end
 records[key]=nil;recordCount=recordCount-1
 if recordCursor==key then recordCursor=following or firstKey end
end
local function valid(o) return o~=nil and o:IsValid() end
local function path(o)
 if not valid(o) then return nil end
 return o:GetFullName():match('%s(.+)$')
end
local function find(p) if p then local o=StaticFindObject(p);if valid(o) then return o end end end
local function face(f) return f.TypefaceFontName:ToString() end
local function sharedSignature(font,scale)
 local f=font.CompositeFont.FallbackTypeface
 local rows=f.Typeface.Fonts;if #rows~=1 then return end
 local row=rows[1];local data=row.Font
 return table.concat({row.Name:ToString(),path(data.FontFaceAsset)or'',data.FontFilename:ToString(),
  tostring(data.Hinting),tostring(data.LoadingPolicy),tostring(data.SubFaceIndex),tostring(scale or f.ScalingFactor)},'\0')
end
local function restoreShared(limit)
 local visited=0;local began=os.clock()
 while #sharedOrder>0 and visited<limit and (visited==0 or os.clock()-began<0.0015)do
  sharedCursor=math.min(sharedCursor,#sharedOrder);local key=sharedOrder[sharedCursor];local r=sharedFonts[key]
  local ok=pcall(function()
   local f=find(key)
   if f and f:GetAddress()==r.address and f:IsA('/Script/Engine.Font')and sharedSignature(f)==r.signature then
    f.CompositeFont.FallbackTypeface.Typeface.Fonts={}
    if #f.CompositeFont.FallbackTypeface.Typeface.Fonts~=0 then error('Shared fallback restoration not confirmed')end
    stats.sharedRestored=stats.sharedRestored+1
   end
   sharedFonts[key]=nil;table.remove(sharedOrder,sharedCursor)
  end)
  visited=visited+1
  if not ok then failed('sharedErrors');sharedCursor=sharedCursor%#sharedOrder+1 end
 end
end
local function updateShared(fallback,deadline)
 -- Retain scalar ownership only while the same loaded font still has our
 -- fallback. Recreated assets and another writer never inherit restoration.
 for _=1,math.min(8,#sharedOrder)do
  if os.clock()>=deadline then break end
  sharedCursor=math.min(sharedCursor,#sharedOrder);local key=sharedOrder[sharedCursor];local r=sharedFonts[key]
  local f=find(key)
  if not f or f:GetAddress()~=r.address or sharedSignature(f)~=r.signature then
   sharedFonts[key]=nil;table.remove(sharedOrder,sharedCursor)
  else sharedCursor=sharedCursor%#sharedOrder+1 end
 end
 if os.clock()>=nextSharedScan and sharedPendingCursor>#sharedPending then
  nextSharedScan=os.clock()+1;sharedPending={};sharedPendingCursor=1
  local began=os.clock();local list=FindAllOf('Font')or{}
  stats.sharedScanCalls=stats.sharedScanCalls+1
  stats.maxSharedScanMs=math.max(stats.maxSharedScanMs,(os.clock()-began)*1000)
  if sharedScanCursor>#list then sharedScanCursor=1 end
  for i=1,math.min(128,#list)do
   if i>1 and os.clock()>=deadline then break end
   local f=list[sharedScanCursor];sharedScanCursor=sharedScanCursor%#list+1;local key=path(f)
   if key and key:sub(1,6)=='/Game/' and not sharedFonts[key]then
    sharedPending[#sharedPending+1]={path=key,address=f:GetAddress()}
   end
  end
 end
 for _=1,8 do
  if sharedPendingCursor>#sharedPending or os.clock()>=deadline then break end
  local hint=sharedPending[sharedPendingCursor];sharedPendingCursor=sharedPendingCursor+1
  local f=find(hint.path)
  if f and f:GetAddress()==hint.address and f:IsA('/Script/Engine.Font')and f.FontCacheType==1
   and #f.CompositeFont.DefaultTypeface.Fonts>0 and #f.CompositeFont.FallbackTypeface.Typeface.Fonts==0 then
   local signature=sharedSignature(fallback,f.CompositeFont.FallbackTypeface.ScalingFactor)
   if signature then
    -- Assign the typeface only: a custom game's fallback scale is preserved.
    -- Own the expected result BEFORE invoking a possibly partial engine write.
    sharedFonts[hint.path]={address=hint.address,signature=signature};sharedOrder[#sharedOrder+1]=hint.path
    f.CompositeFont.FallbackTypeface.Typeface=fallback.CompositeFont.FallbackTypeface.Typeface
    if sharedSignature(f)~=signature then error('Shared fallback not confirmed')end
    stats.sharedApplied=stats.sharedApplied+1
   end
  end
 end
end
local function cjk(s)
 for _,n in utf8.codes(s) do
  if n>=0x2e80 and n<=0x9fff or n>=0xac00 and n<=0xd7af or n>=0x20000 and n<=0x3134f then return true end
 end
 return false
end
local function fontFor(b,rich)
 if rich then return b.DefaultTextStyleOverride.Font end
 return b.Font
end
local function setFont(b,rich,font)
 settingFont=true
 local ok,err=pcall(function()if rich then b:SetDefaultFont(font) else b:SetFont(font) end end)
 settingFont=false
 if not ok then error(err) end
 b:InvalidateLayoutAndVolatility()
end
local function ownsFont(r,f)
 local p,n=path(f.FontObject),face(f)
 if r.phase=='apply_pending' or r.phase=='restore_pending' then
  -- A reflected struct can alias the widget. A failed operation may leave the
  -- old pair, only FontObject changed, or both fields changed. A copied struct
  -- may leave the old pair until the setter succeeds. Accept only these states.
  return p==r.pendingFrom and n==r.pendingFromFace or
   p==r.pendingTo and (n==r.pendingFromFace or n==r.pendingToFace)
 end
 return p==r.applied and n==r.appliedFace
end
local function detachedFont(font)
 -- Font is a borrowed property reference in UE4SS. Editing it before SetFont
 -- can make the native equality guard skip the Slate update entirely. Copy
 -- the entire struct into our own temporary object, preserving materials,
 -- size, spacing and outlines without touching any live property first.
 local holder=scratchPath and find(scratchPath)
 if not holder or holder:GetAddress()~=scratchAddress or not holder:IsA('/Script/UMG.TextBlock') then
  local gs=find('/Script/Engine.Default__GameplayStatics')
  holder=gs:SpawnObject(StaticFindObject('/Script/UMG.TextBlock'),gs)
  if not valid(holder) or not holder:IsA('/Script/UMG.TextBlock') then error('Font scratch unavailable') end
  scratchPath=path(holder);scratchAddress=holder:GetAddress()
 end
 holder.Font=font
 return holder.Font
end
local function changeFont(b,r,f,target,targetPath,targetFace,phase)
 r.phase=phase
 r.pendingFrom=path(f.FontObject);r.pendingFromFace=face(f)
 r.pendingTo=targetPath;r.pendingToFace=targetFace
 local argument=detachedFont(f)
 argument.FontObject=target;argument.TypefaceFontName=FName(targetFace)
 setFont(b,r.rich,argument)
 -- A reflected setter can return without accepting the requested font. Keep
 -- the pending ownership record until its live fields confirm the change.
 -- This verifies state only, not glyph pixels or the renderer's next frame.
 local current=fontFor(b,r.rich)
 if path(current.FontObject)~=targetPath or face(current)~=targetFace then error('Font update not confirmed') end
end
local function apply(key,r,b,f)
 local fallback=find(r.applied)
 if not fallback then return end
 changeFont(b,r,f,fallback,r.applied,r.appliedFace,'apply_pending')
 r.phase='applied'
end
local function revert(key,r)
 -- Re-resolve paths rather than retain raw UObject wrappers across collection.
 local b=find(key)
 if not b then removeRecord(key);return end
 if b:GetAddress()~=r.address then removeRecord(key);return end
 local f=fontFor(b,r.rich)
 if not ownsFont(r,f) then removeRecord(key);return end
 local original=find(r.original)
 if not original then stats.missingOriginal=stats.missingOriginal+1;return end
 changeFont(b,r,f,original,r.original,r.originalFace,'restore_pending')
 -- A matching property pair is not proof that the setter/layout completed.
 -- Keep a pending record on failure, including when the source struct aliases.
 removeRecord(key)
end
local function visitRecords(action,limit,deadline,errorKind)
 local remaining=math.min(recordCount,limit);local visited=0
 while recordCursor and visited<remaining do
  if visited>0 and os.clock()>=deadline then stats.budgetYields=stats.budgetYields+1;break end
  local key=recordCursor;local r=records[key]
  recordCursor=r.following or firstKey;visited=visited+1
  local ok=pcall(action,key,r)
  if not ok then failed(errorKind) end
 end
 if recordCount>visited and visited>=limit then stats.budgetYields=stats.budgetYields+1 end
end
function M.restoreStep(limit)
 limit=type(limit)=='number' and math.max(1,math.min(recordLimit,math.floor(limit))) or recordLimit
 visitRecords(revert,limit,os.clock()+stepSeconds,'restoreErrors')
 restoreShared(8);sharedPending={};sharedPendingCursor=1;nextSharedScan=0
 return recordCount==0 and #sharedOrder==0
end
-- A restore is now one bounded step. The game-thread pump must keep draining
-- until true, or explicitly report incomplete restoration on its deadline.
M.restore=M.restoreStep
function M.useRuntimeHints() runtimeHints=true end
function M.hint(key,address,class)
 if type(key)~='string' or #key==0 or #key>4096 or type(address)~='number' or address<=0
  or (class~='TextBlock' and class~='RichTextBlock') then return end
 if not hints[key] then
  if #hintOrder>=128 then hints[table.remove(hintOrder,1)]=nil;hintCursor=math.max(1,hintCursor-1) end
  hintOrder[#hintOrder+1]=key
 end
 hints[key]={address=address,rich=class=='RichTextBlock',seen=os.clock()}
 hintsDirty=true
end
local function fallbackFont()
 local fallback=find('/Engine/EngineFonts/Roboto.Roboto')
 if not fallback then
  local loaded,o=pcall(LoadAsset,'/Engine/EngineFonts/Roboto.Roboto')
  if loaded and valid(o) then fallback=o elseif not loaded then failed('scanErrors') end
 end
 return fallback
end
local function consider(b,rich,fallback,fallbackPath,owned,protected,displayText)
 if not valid(b) or not b:IsVisible() then return end
 local text=displayText or b:GetText():ToString();if not owned[text] or protected[text] or not cjk(text) then return end
 -- Preserve RichText run styles, font ownership and all existing materials.
 if rich and not b.bOverrideDefaultStyle then return end
 local f=fontFor(b,rich);local originalPath=path(f.FontObject)
 if not originalPath or originalPath==fallbackPath then return end
 local key=path(b);if not key or records[key] then return end
 local r={original=originalPath,originalFace=face(f),applied=fallbackPath,appliedFace='None',rich=rich,address=b:GetAddress()}
 addRecord(key,r)
 changeFont(b,r,f,fallback,fallbackPath,r.appliedFace,'apply_pending');r.phase='applied'
end
local function prepareLive(b,rich,owned,protected,displayText)
 if not valid(b) then return false end
 if eventText and not displayText then eventText(b) end
 local text=displayText or b:GetText():ToString()
 if not owned[text] or protected[text] or not cjk(text) then return false end
 local key=path(b);if not key then return false end
 local r=records[key]
 if r and r.address~=b:GetAddress() then removeRecord(key);r=nil end
 local changed=false
 if r then
  local f=fontFor(b,r.rich)
  if not ownsFont(r,f) then
   if path(f.FontObject)==r.original and face(f)==r.originalFace then apply(key,r,b,f);changed=true
   else return false end
  elseif r.phase=='apply_pending' then apply(key,r,b,f);changed=true end
 else
  local fallback=find('/Engine/EngineFonts/Roboto.Roboto')
  if fallback then
   consider(b,rich,fallback,path(fallback),owned,protected,displayText)
   changed=records[key]~=nil and records[key].phase=='applied'
  end
 end
 -- Successful setters need no second priority scan. Polling still maintains
 -- ownership; an unresolved asset gets a copied hint without loading in hooks.
 if not records[key] then M.hint(key,b:GetAddress(),rich and 'RichTextBlock' or 'TextBlock') end
 return changed
end
local function eventTables()
 local onThread,gameThread=pcall(IsInGameThread)
 if not onThread or not gameThread then stats.eventThreadSkipped=stats.eventThreadSkipped+1;return end
 local ok,active,owned,protected=pcall(eventState)
 if ok and active and type(owned)=='table' and type(protected)=='table' then return owned,protected end
end
function M.prepareWidget(b,owned,protected,displayText)
 return prepareLive(b,false,owned,protected,displayText)
end
local function scalarNode(b,depth)
 if not valid(b) or depth>32 then return end
 local key=path(b);local address=b:GetAddress()
 if key and address>0 then return {key=key,address=address,depth=depth,child=0} end
end
local function queueShown(b)
 local node=scalarNode(b,0);if not node or showKeys[node.key] then return end
 if #showJobs>=16 then stats.shownDropped=stats.shownDropped+1;return end
 showKeys[node.key]=true;showJobs[#showJobs+1]={key=node.key,address=node.address,nodes={node},cursor=1,seen={[node.key]=true}}
end
local function drainShown(owned,protected,deadline)
 local visits=0;local started=os.clock()
 while #showJobs>0 and visits<64 and (visits==0 or os.clock()<deadline) do
  local job=showJobs[1];local node=job.nodes[job.cursor]
  local ok=pcall(function()
   local root=find(job.key)
   if not root or root:GetAddress()~=job.address or not root:IsVisible() then job.cursor=#job.nodes+1;return end
   if not node then return end
   local b=find(node.key)
   if not b or b:GetAddress()~=node.address or not b:IsVisible() then job.cursor=job.cursor+1;return end
   -- A panel can be hidden, detached or reordered while a large tree yields.
   -- Revalidate the entire scalar ancestry before preparing a delayed child.
   local link=node
   while link.parent do
    local parent=job.nodes[link.parent];local live=find(parent.key)
    if not live or live:GetAddress()~=parent.address or not live:IsVisible() then job.cursor=job.cursor+1;return end
    local child
    if link.edge=='root' then local tree=live.WidgetTree;if valid(tree) then child=tree.RootWidget end
    elseif live:GetChildrenCount()>link.edge then child=live:GetChildAt(link.edge) end
    if not valid(child) or child:GetAddress()~=link.address or path(child)~=link.key then job.cursor=job.cursor+1;return end
    link=parent
   end
   local function add(child,edge)
    local n=scalarNode(child,node.depth+1)
    if n and not job.seen[n.key] and #job.nodes<512 then
     n.parent=job.cursor;n.edge=edge;job.seen[n.key]=true;job.nodes[#job.nodes+1]=n
    end
   end
   if node.child==0 then
    stats.shownNodes=stats.shownNodes+1
    if b:IsA('/Script/UMG.TextBlock') or b:IsA('/Script/UMG.RichTextBlock') then
     if prepareLive(b,b:IsA('/Script/UMG.RichTextBlock'),owned,protected) then stats.shownApplied=stats.shownApplied+1 end
    elseif b:IsA('/Script/UMG.UserWidget') then
     local tree=b.WidgetTree;if valid(tree) then add(tree.RootWidget,'root') end
    end
   end
   if b:IsA('/Script/UMG.PanelWidget') then
    local total=math.min(b:GetChildrenCount(),512)
    -- Four children per turn: even a very wide menu yields within the budget.
    local finish=math.min(total,node.child+4)
    for i=node.child,finish-1 do add(b:GetChildAt(i),i) end
    node.child=finish
    if finish<total then return end
   end
   job.cursor=job.cursor+1
  end)
  visits=visits+1
  if not ok then stats.shownErrors=stats.shownErrors+1;job.cursor=job.cursor+1 end
  if job.cursor>#job.nodes then showKeys[job.key]=nil;table.remove(showJobs,1) end
 end
 if #showJobs>0 then stats.shownDeferred=stats.shownDeferred+1 end
 stats.lastShownMs=math.max(0,(os.clock()-started)*1000);stats.maxShownMs=math.max(stats.maxShownMs,stats.lastShownMs)
end
function M.flushEvents()
 if eventActive or #showJobs==0 then return end
 local owned,protected=eventTables()
 if not owned then showJobs={};showKeys={};return end
 eventActive=true
 local ok=pcall(drainShown,owned,protected,os.clock()+stepSeconds)
 eventActive=false
 if not ok then stats.shownErrors=stats.shownErrors+1 end
end
-- Standard UMG setters run before the next paint. The optional live-widget
-- visitor validates and applies cached text; prepare its font in that call.
-- This module changes font appearance only and never replaces a text binding.
function M.startEvents(state,textVisitor)
 if #eventHooks>0 or type(RegisterHook)~='function' or type(UnregisterHook)~='function'
  or type(IsInGameThread)~='function' then return false end
 eventState=state;eventText=textVisitor
 fallbackFont() -- Load outside a setter callback; callbacks only use live assets.
 for _,class in ipairs(classes) do
  local hookPath='/Script/UMG.'..class..':SetText'
  local ok,pre,post=pcall(RegisterHook,hookPath,function() end,function(context)
   if eventActive then return end
   local owned,protected=eventTables();if not owned then return end
   local started=os.clock()
   if started-eventWindow>=0.01 then eventWindow=started;eventSpent=0;eventImmediate=0 end
   -- A bounded single-control cache/font fix must not wait behind a menu-tree
   -- scan that consumed this turn's discovery budget. Preserve a separate cap
   -- for hot setters so frequently updated HUDs cannot cause unbounded work.
   if eventSpent>=stepSeconds and (not eventText or eventImmediate>=64) then stats.eventDeferred=stats.eventDeferred+1;return end
   eventImmediate=eventImmediate+1
   eventActive=true;stats.eventCalls=stats.eventCalls+1
   local success=pcall(function()
    if prepareLive(context:get(),class=='RichTextBlock',owned,protected) then stats.eventApplied=stats.eventApplied+1 end
   end)
   eventActive=false;eventSpent=eventSpent+math.max(0,os.clock()-started)
   if not success then stats.eventErrors=stats.eventErrors+1 end
   -- Never return a value: hook return values can override a UFunction result.
  end)
  if ok and type(pre)=='number' and type(post)=='number' then eventHooks[#eventHooks+1]={hookPath,pre,post}
  else stats.eventRegistrationErrors=stats.eventRegistrationErrors+1 end
 end
 -- Construction and hover handlers can restore the original font *after*
 -- SetText. A confirmed native setter is evidence of the latest game style.
 -- Reapply only when the game selects that same original font family; retain
 -- its newest face, size and materials for disable. A different font is left
 -- alone. No per-frame guessing about foreign font owners is needed.
 do
  local hookPath='/Script/UMG.TextBlock:SetFont'
  local ok,pre,post=pcall(RegisterHook,hookPath,function()end,function(context)
   if settingFont then return end
   local owned,protected=eventTables();if not owned then return end
   local success=pcall(function()
    local b=context:get();if not valid(b) then return end
    local key=path(b);local r=records[key];if not r or r.rich or r.address~=b:GetAddress() then return end
    local f=fontFor(b,false);local text=b:GetText():ToString()
    if path(f.FontObject)==r.original and owned[text] and not protected[text] and cjk(text) then
     r.originalFace=face(f);apply(key,r,b,f)
    end
   end)
   if not success then failed('applyErrors') end
  end)
  if ok and type(pre)=='number' and type(post)=='number' then eventHooks[#eventHooks+1]={hookPath,pre,post}
  else stats.eventRegistrationErrors=stats.eventRegistrationErrors+1 end
 end
 -- Serialized/bound labels often never call SetText when a menu is reopened.
 -- Follow only that shown widget's typed UI tree, not the global object list.
 for _,method in ipairs({'UserWidget:AddToViewport','UserWidget:AddToPlayerScreen','Widget:SetVisibility'}) do
  local hookPath='/Script/UMG.'..method
  local ok,pre,post=pcall(RegisterHook,hookPath,function() end,function(context)
   if eventActive then return end
   local owned,protected=eventTables();if not owned then return end
   eventActive=true;stats.shownCalls=stats.shownCalls+1
   local started=os.clock()
   local success=pcall(function()
    queueShown(context:get())
    if started-eventWindow>=0.01 then eventWindow=started;eventSpent=0 end
    if eventSpent<stepSeconds then drainShown(owned,protected,started+stepSeconds-eventSpent) end
   end)
   eventSpent=eventSpent+math.max(0,os.clock()-started);eventActive=false
   if not success then stats.shownErrors=stats.shownErrors+1 end
  end)
  if ok and type(pre)=='number' and type(post)=='number' then eventHooks[#eventHooks+1]={hookPath,pre,post}
  else stats.eventRegistrationErrors=stats.eventRegistrationErrors+1 end
 end
 return #eventHooks>0
end
function M.closeEvents()
 showJobs={};showKeys={}
 for index=#eventHooks,1,-1 do
  local h=eventHooks[index];local ok=pcall(UnregisterHook,h[1],h[2],h[3])
  if ok then table.remove(eventHooks,index) else stats.eventErrors=stats.eventErrors+1 end
 end
end
function M.update(owned,protected,force)
 local started=os.clock();local deadline=started+stepSeconds
 -- Repeated IPC responses often complete between actual capture/apply work.
 -- Maintain owned fonts at most10Hz unless a new observation or translation
 -- needs immediate attention; legacy standalone enumeration keeps its cadence.
 if runtimeHints and started<nextUpdateAt and not hintsDirty and not force then stats.updateSkipped=stats.updateSkipped+1;return end
 nextUpdateAt=started+0.1;hintsDirty=false;stats.updateCalls=stats.updateCalls+1
 protected=protected or {}
 local needsCjk=false
 for text in pairs(owned)do if not protected[text]and cjk(text)then needsCjk=true;break end end
 if needsCjk then
  local ok=pcall(function()local fallback=fallbackFont();if fallback then updateShared(fallback,deadline)end end)
  if not ok then failed('sharedErrors') end
 end
 -- Preserve native Chinese, untranslated strings, icon labels and Latin names.
 visitRecords(function(key,r)
   local b=find(key)
   if not b or b:GetAddress()~=r.address then removeRecord(key);return end
   local f=fontFor(b,r.rich)
   if not ownsFont(r,f) then
    if path(f.FontObject)==r.original and face(f)==r.originalFace and owned[b:GetText():ToString()] and not protected[b:GetText():ToString()] then apply(key,r,b,f)
    else r.phase='released' end
    -- Keep a scalar tombstone until destruction/restore: removing it here
    -- would let the next discovery overwrite the newly supplied foreign font.
    return
   end
   local text=b and b:GetText():ToString() or ''
   if r.phase=='restore_pending' or not owned[text] or protected[text] or not cjk(text) then
    local ok=pcall(revert,key,r);if not ok then failed('restoreErrors') end
   elseif r.phase=='apply_pending' then
    apply(key,r,b,f)
   end
 end,recordLimit,deadline,'applyErrors')
 local function hinted()
  if not runtimeHints or not next(owned) or #hintOrder==0 then return end
  local fallback=fallbackFont();if not fallback then return end
  local fallbackPath=path(fallback);local remaining=math.min(#hintOrder,scanLimit)
  for count=1,remaining do
   if #hintOrder==0 then break end
   if count>1 and os.clock()>=deadline then stats.budgetYields=stats.budgetYields+1;break end
   if hintCursor>#hintOrder then hintCursor=1 end
   local key=hintOrder[hintCursor];local hint=hints[key]
   if not hint or started-hint.seen>=5 then
    hints[key]=nil;table.remove(hintOrder,hintCursor)
   else
    hintCursor=hintCursor+1;stats.hintVisits=stats.hintVisits+1
    local ok=pcall(function()
     local b=find(key)
     -- A copied observation is a font priority hint, never a text identity or
     -- retained engine reference. Re-read the live text and address after apply.
     if b and b:GetAddress()==hint.address then consider(b,hint.rich,fallback,fallbackPath,owned,protected) end
    end)
    if not ok then failed('hintErrors') end
   end
  end
 end
 local hintedOk=pcall(hinted);if not hintedOk then failed('hintErrors') end
 local function scan()
  -- Production shares the runtime's observed widget paths, so a freshly
  -- translated label does not wait for a second full UObject-array rotation.
  if runtimeHints or not next(owned) or started<nextScanAt then return end
  -- One native class enumeration per interval. FindAllOf itself cannot be
  -- interrupted; measured cost is exposed, not promised as a hard 3 ms bound.
  nextScanAt=started+scanInterval
  local classIndex=scanClass;scanClass=scanClass%#classes+1
  local rich=classIndex==2
  local began=os.clock();stats.scanCalls=stats.scanCalls+1
  local ok,blocks=pcall(FindAllOf,classes[classIndex])
  stats.lastEnumerationMs=math.max(0,(os.clock()-began)*1000)
  stats.maxEnumerationMs=math.max(stats.maxEnumerationMs,stats.lastEnumerationMs)
  if not ok then failed('scanErrors');return end
  blocks=blocks or {};if #blocks==0 then scanCursors[classIndex]=1;return end
  local fallback=fallbackFont()
  if not fallback then return end
  local fallbackPath=path(fallback)
  local i=scanCursors[classIndex];if i>#blocks then i=1 end
  local seen=0
  while seen<math.min(#blocks,scanLimit) do
   -- Allow one widget after a slow enumeration, then yield. Native calls are
   -- not preemptible, and the cursor prevents starvation of later widgets.
   if seen>0 and os.clock()>=deadline then stats.budgetYields=stats.budgetYields+1;break end
   local b=blocks[i];seen=seen+1;stats.scanned=stats.scanned+1
   local applied=pcall(consider,b,rich,fallback,fallbackPath,owned,protected)
   if not applied then failed('applyErrors') end
   i=i%#blocks+1
  end
  scanCursors[classIndex]=i
  if seen>=scanLimit and #blocks>seen then stats.budgetYields=stats.budgetYields+1 end
 end
 local ok=pcall(scan);if not ok then failed('scanErrors') end
 stats.lastUpdateMs=math.max(0,(os.clock()-started)*1000)
 stats.maxUpdateMs=math.max(stats.maxUpdateMs,stats.lastUpdateMs)
end
function M.count() return recordCount+#sharedOrder end
function M.diagnostics()
 local result={recordCount=recordCount,recordLimit=recordLimit,scanLimit=scanLimit,scanIntervalMs=scanInterval*1000,
  hintCount=#hintOrder,runtimeHints=runtimeHints,eventHooks=#eventHooks,shownPending=#showJobs,sharedFontCount=#sharedOrder}
 for k,v in pairs(stats) do result[k]=v end
 return result
end
return M
