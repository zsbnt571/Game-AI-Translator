-- Reversible per-widget CJK fallback, only for display strings changed by Fusion.
-- Keep sizes, materials, outlines, bindings and the game's font assets untouched.
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
local stats={errors=0,applyErrors=0,restoreErrors=0,scanErrors=0,missingOriginal=0,
 scanCalls=0,scanned=0,budgetYields=0,lastEnumerationMs=0,maxEnumerationMs=0,
 lastUpdateMs=0,maxUpdateMs=0,hintVisits=0,hintErrors=0,updateCalls=0,updateSkipped=0,timeBasis='os.clock'}
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
 if rich then b:SetDefaultFont(font) else b:SetFont(font) end
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
local function changeFont(b,r,f,target,targetPath,targetFace,phase)
 r.phase=phase
 r.pendingFrom=path(f.FontObject);r.pendingFromFace=face(f)
 r.pendingTo=targetPath;r.pendingToFace=targetFace
 f.FontObject=target;f.TypefaceFontName=FName(targetFace)
 setFont(b,r.rich,f)
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
 return recordCount==0
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
local function consider(b,rich,fallback,fallbackPath,owned,protected)
 if not valid(b) or not b:IsVisible() then return end
 local text=b:GetText():ToString();if not owned[text] or protected[text] or not cjk(text) then return end
 -- Preserve RichText run styles, font ownership and all existing materials.
 if rich and not b.bOverrideDefaultStyle then return end
 local f=fontFor(b,rich);local originalPath=path(f.FontObject)
 if not originalPath or originalPath==fallbackPath then return end
 local key=path(b);if not key or records[key] then return end
 local r={original=originalPath,originalFace=face(f),applied=fallbackPath,appliedFace='None',rich=rich,address=b:GetAddress()}
 addRecord(key,r)
 changeFont(b,r,f,fallback,fallbackPath,r.appliedFace,'apply_pending');r.phase='applied'
end
function M.update(owned,protected,force)
 local started=os.clock();local deadline=started+stepSeconds
 -- Repeated IPC responses often complete between actual capture/apply work.
 -- Maintain owned fonts at most10Hz unless a new observation or translation
 -- needs immediate attention; legacy standalone enumeration keeps its cadence.
 if runtimeHints and started<nextUpdateAt and not hintsDirty and not force then stats.updateSkipped=stats.updateSkipped+1;return end
 nextUpdateAt=started+0.1;hintsDirty=false;stats.updateCalls=stats.updateCalls+1
 protected=protected or {}
 -- Preserve native Chinese, untranslated strings, icon labels and Latin names.
 visitRecords(function(key,r)
   local b=find(key)
   if not b or b:GetAddress()~=r.address then removeRecord(key);return end
   local f=fontFor(b,r.rich)
   if not ownsFont(r,f) then removeRecord(key);return end
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
function M.count() return recordCount end
function M.diagnostics()
 local result={recordCount=recordCount,recordLimit=recordLimit,scanLimit=scanLimit,scanIntervalMs=scanInterval*1000,
  hintCount=#hintOrder,runtimeHints=runtimeHints}
 for k,v in pairs(stats) do result[k]=v end
 return result
end
return M
