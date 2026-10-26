-- Fusion Unreal bridge, protocol 76. Native localization identities are updated
-- through initialized FPolyglotTextData. A live-unbound-widget fallback preserves
-- dynamic delegates when native identity lookup cannot support a label.
local folder,secret=os.getenv('FUSION_UNREAL_SESSION'),os.getenv('FUSION_UNREAL_SECRET')
if not folder or not secret or #secret~=64 then return end
local engineVersion=os.getenv('FUSION_UNREAL_ENGINE') or ''
if not engineVersion:match('^[45]%.%d%d?$') then engineVersion='unreported' end
local script=debug.getinfo(1,'S').source:sub(2):match('^(.*[/\\])')
local json=dofile(script..'json.lua')
local fonts=dofile(script..'fonts.lua')
local runtimeFactory=dofile(script..'runtime.lua')
local textApiFactory=dofile(script..'text_api.lua')
local widgetTextFactory=dofile(script..'widget_text.lua')
local textStream=dofile(script..'text_stream.lua').new()
local slateFactory=dofile(script..'slate.lua')
local function read(name,limit)
 local f=io.open(folder..'/'..name,'rb');if not f then return nil end
 local size=f:seek('end');f:seek('set');if not size or size>limit then f:close();error('IPC size limit') end
 local s=f:read('*a');f:close();return s
end
local function parse(name,limit) local s=read(name,limit);if s then return json.decode(s) end end
local function write(name,value)
 local encoded=json.encode(value)
 local path=folder..'/'..name;local f=assert(io.open(path..'.tmp','wb'))
 f:write(encoded);f:close();assert(os.rename(path..'.tmp',path))
end
local lib,intl,culture,textApi
local scratchPath
local scratchRowName='FusionScratch_'..(folder:match('([^/\\]+)[/\\]*$') or 'Session'):gsub('[^%w_]','')
local ready=false
local function objectPath(object) return object:GetFullName():match('%s(.+)$') end
local function scratchRow(path)
 local current=StaticFindObject(path)
 if not current or not current:IsValid() or objectPath(current)~=path or not current:IsA('/Script/Engine.DataTable') then return nil end
 local structure=current:GetRowStruct()
 if not structure or not structure:IsValid() or objectPath(structure)~='/Script/CoreUObject.PolyglotTextData' then return nil end
 -- FindRow returns borrowed memory. Resolve it anew in this game-thread
 -- callback; a previous wrapper's IsValid can survive UObject address reuse.
 return current:FindRow(scratchRowName)
end
local function ensureScratch()
 if scratchPath then
  local ok,currentRow=pcall(scratchRow,scratchPath)
  if ok and currentRow then return currentRow end
  scratchPath=nil
 end
 -- Use the engine's reflected NewObject path. The loader's direct native
 -- StaticConstructObject call has a different ABI on some UE builds.
 local gameplay=StaticFindObject('/Script/Engine.Default__GameplayStatics')
 local created=gameplay:SpawnObject(StaticFindObject('/Script/Engine.DataTable'),lib)
 assert(created:IsValid(),'Localization scratch construction failed')
 local path=assert(objectPath(created),'Localization scratch path unavailable')
 local current=StaticFindObject(path)
 assert(current and current:IsValid() and objectPath(current)==path and current:IsA('/Script/Engine.DataTable'),'Localization scratch type mismatch')
 current.RowStruct=StaticFindObject('/Script/CoreUObject.PolyglotTextData')
 current:AddRow(scratchRowName,{})
 local currentRow=assert(scratchRow(path),'Localization scratch row unavailable')
 -- Only a path and a private row-name marker survive the callback. Do not
 -- root the temporary object or retain a raw table/row wrapper between uses.
 scratchPath=path
 return currentRow
end
local entries,bySource,catalog,translations,changed={},{},{},{},{}
local preparedSources,preparedCount={},0
local preserved={}
local identities,blockedIdentities,protectedCultures,identityOwners={},{},{},{}
local candidates,candidateConflicts,retryCursor={},{},1
local function isCandidate(e) return e.provenance=='blueprintLocalizedCandidate' or e.provenance=='nativeLocalizedCandidate' end
local function bounded(value,maximum,empty)
 return type(value)=='string' and (empty or #value>0) and #value<=maximum and not value:find('\0',1,true)
end
local function validIdentity(e)
 return type(e)=='table' and bounded(e.ns,4096,true) and bounded(e.key,4096,false) and bounded(e.source,24000,false)
end
local runtime,widgetText,slate
local displayTranslations={}
local nextRuntimeScanAt=0
local timing={callbacks=0,requestReads=0,lastPumpMs=0,maxPumpMs=0,timeBasis='os.clock'}
local resumeAfterCulture=false
local enabled=false;local epoch,lastId,busy,stopped=0,0,false,false
local fontTexts,protectedFontTexts={},{}
local function protectDisplay(value)
 if bounded(value,48000,false) then protectedFontTexts[value]=true end
end
local integration={conflictingIdentities=0,invalidIdentities=0,duplicateIdentities=0,visibleCacheRefreshes=0,
 maintenanceCachePasses=0,incrementalFontPasses=0}
local fontUpdateNeeded=false
local lastRequest=os.time();local skipped=0
local jobs={};local enqueue=function(action) jobs[#jobs+1]=action end
local restore,batch
local deferredCultures={}
local function changeKey(e,language) return e.ns..'\0'..e.key..'\0'..language end
local function clearChange(key)
 local record=changed[key];if not record then return end
 local id=record.e.ns..'\0'..record.e.key
 if identityOwners[id] then identityOwners[id][key]=nil;if not next(identityOwners[id]) then identityOwners[id]=nil end end
 changed[key]=nil
end
local function releaseIdentity(id)
 for key in pairs(identityOwners[id] or {}) do changed[key]=nil end
 identityOwners[id]=nil
end
local function cultureProtected(id,language) return protectedCultures[id] and protectedCultures[id][language] end
local function protectCulture(id,language)
 protectedCultures[id]=protectedCultures[id] or {};protectedCultures[id][language]=true
end
local deferredRestoreCode='restore-culture-not-active'
local function deferredRestore()
 error({code=deferredRestoreCode,message='Localization restoration culture not active'},0)
end
local function restorationSummary(allChanges)
 local count,cultures=0,{}
 local pendingByCulture=deferredCultures
 if allChanges then
  pendingByCulture={}
  for _,change in pairs(changed) do pendingByCulture[change.culture]=(pendingByCulture[change.culture] or 0)+1 end
 end
 for language,pending in pairs(pendingByCulture) do count=count+pending;cultures[#cultures+1]=language end
 table.sort(cultures);return count,cultures
end
local lastStatusLog=0
local function statusLog(state,kind)
 -- Only fixed states and numeric counters enter the loader log: no source
 -- text, exception strings, identity keys, paths, or session credentials.
 local now=os.time()
 if state=='running' and now-lastStatusLog<30 then return end
 lastStatusLog=now
 local ok,count=pcall(fonts.count)
 local detailOk,details=pcall(function() return fonts.diagnostics and fonts.diagnostics() or {} end)
 local errors=detailOk and type(details)=='table' and tonumber(details.errors) or 0
 local runtimeOk,runtimeDetail=pcall(function() return runtime and runtime.status() or {} end)
 local apiOk,apiDetail=pcall(function() return textApi and textApi.status() or {} end)
 local apiStates={['out-last-3']=true,['out-last-4']=true,['out-middle-4']=true,['metadata-unavailable']=true,
  ['missing-function']=true,['unsupported-signature']=true,['native-out-argument-not-consumed']=true,
  ['call-failed']=true,['invalid-return-type']=true,['invalid-out-value']=true,['not-found']=true,['ready']=true,
  ['identity-not-returned']=true,['identity-changed-or-lost']=true,['source-changed-or-lost']=true,
  ['two-identities-and-sources-confirmed']=true}
 local function apiState(v) return apiStates[v] and v or 'not-tested' end
 local query=apiOk and apiDetail.metadata and apiDetail.metadata.FindTextInLocalizationTable or {}
 local queryResult=apiOk and apiDetail.results and apiDetail.results.FindTextInLocalizationTable
 local function numeric(t,key) return type(t)=='table' and tonumber(t[key]) or 0 end
 pcall(print,string.format('[FusionBridge] state=%s kind=%s pending=%d fontRecords=%d fontErrors=%d maxPumpMs=%.2f maxEnumerationMs=%.2f maxTableMs=%.2f maxFontUpdateMs=%.2f scans=%d fontHints=%d eventApplied=%d eventErrors=%d shownApplied=%d shownPending=%d shownErrors=%d maxShownMs=%.2f queryLayout=%s queryStage=%s identityDetail=%s\n',
  state,kind or 'none',batch and math.max(0,#jobs-batch.index+1) or 0,ok and tonumber(count) or -1,errors or 0,
  timing.maxPumpMs,numeric(runtimeOk and runtimeDetail,'maxEnumerationMs'),numeric(runtimeOk and runtimeDetail,'maxTableMs'),
  numeric(detailOk and details,'maxUpdateMs'),numeric(runtimeOk and runtimeDetail,'scanCalls'),numeric(detailOk and details,'hintVisits'),
  numeric(details,'eventApplied'),numeric(details,'eventErrors'),numeric(details,'shownApplied'),numeric(details,'shownPending'),numeric(details,'shownErrors'),numeric(details,'maxShownMs'),
  apiState(query.layout),apiState(queryResult),apiState(runtimeOk and runtimeDetail.identityDetail)))
end
-- One persistent game-thread callback owns all bridge state. The pinned loader
-- shares a Lua registry between its async and hook stacks, so bouncing between
-- LoopAsync/ExecuteWithDelay and game-thread callbacks is not safe even when a
-- Lua 'busy' flag prevents two translation batches from running together.
local function schedule(action,finished,restoring)
 assert(not batch,'Localization batch already active')
 busy=true;jobs={};batch={index=1,finished=finished,rollingBack=restoring or false}
 local current=batch
 enqueue(function() current.result=action() end)
end
local function failureText(current,err)
 local message=tostring(type(err)=='table' and err.message or err):sub(1,512)
 current.failure=current.failure and (current.failure..'; '..message):sub(1,1024) or message
end
local function rollback(current,err)
 if not current.failure then statusLog('failed',current.rollingBack and 'restoration' or 'batch') end
 failureText(current,err);enabled=false
 if type(err)=='table' and err.code==deferredRestoreCode then current.deferredFailure=true
 else current.nonDeferredFailure=true end
 if current.rollingBack then return end
 current.rollingBack=true;jobs={};current.index=1
 -- Queue even the restoration setup; an error in it must not escape the pump.
 enqueue(function() if restore then restore() end end)
end
local function advanceBatch(deadline)
 local current=batch;if not current then return end
 local count=0
 while current.index<=#jobs and count<32 and os.clock()<deadline do
  local job=jobs[current.index];count=count+1
  local ok,result=pcall(job)
  if ok then
   -- A bounded restoration step can keep its place until its records drain.
   if result==false then break end
   current.index=current.index+1
  else
   if current.rollingBack then current.index=current.index+1 end
   rollback(current,result)
  end
 end
 -- A catalog/apply batch can span many game frames. Newly written Chinese
 -- needs its font opportunity before yielding this frame, not only once the
 -- entire catalog finishes. Ownership and the font module's bounds still apply.
 local finished=current.index>#jobs
 if enabled and (fontUpdateNeeded or finished) then
   local ok,err=pcall(fonts.update,fontTexts,protectedFontTexts,fontUpdateNeeded)
   if not ok then rollback(current,err);return end
   if fontUpdateNeeded and not finished then integration.incrementalFontPasses=integration.incrementalFontPasses+1 end
   fontUpdateNeeded=false
 end
 if finished then
  batch=nil;busy=false;jobs={}
  -- The outer pump guard also covers completion/IPC errors. A job failure is
  -- never converted into a successful disable or restored acknowledgement.
   current.finished(not current.failure,current.failure or current.result,nil,
    current.deferredFailure and not current.nonDeferredFailure)
 end
end
local function existingChinese(e)
 local declared=(e.culture or ''):lower():gsub('_','-')
 if declared:match('^ja$') or declared:match('^ja%-') or declared:match('^ko$') or declared:match('^ko%-') then
  local native
  for language,value in pairs(e.localized or {}) do
   local normalized=language:lower():gsub('_','-')
   if normalized==declared then native=value;break end
  end
  -- A patched resource can retain Japanese/Korean package metadata while its
  -- actual source has already changed. Preserve that conflicting identity;
  -- declaring the whole package Japanese is not proof this string is original.
  if native and native~=e.source then return true end
  for language,value in pairs(e.localized or {}) do
   local normalized=language:lower():gsub('_','-')
   if (normalized=='zh' or normalized:match('^zh%-')) and value==e.source and value~=native then return true end
  end
  -- Unchanged Japanese/Korean source remains eligible, including Han-only
  -- words shared with Chinese. Do not guess their language from script alone.
  return false
 end
 local han=false
 for _,code in utf8.codes(e.source) do
  if code>=0x3040 and code<=0x30ff then return false end
  if code>=0x3400 and code<=0x9fff then han=true end
 end
 return han
end
local function prepareSource(source)
 if bounded(source,24000,false) then textStream.add(source,false) end
 if preparedSources[source] or bySource[source] or preparedCount>=100000
  or not bounded(source,24000,false) or existingChinese({source=source}) then return end
 preparedSources[source]=true;preparedCount=preparedCount+1;catalog[#catalog+1]=source
end
slate=slateFactory.new({folder=folder,script=script,secret=secret,engine=engineVersion,
 parse=parse,write=write,prepare=prepareSource})
local sourceArgument=false
local candidateLookup='not-tested'
local references={}
local function existingLookup(e)
 -- Candidate byte patterns are not proof of an identity. This string-only
 -- query may observe an existing identity, but must never create one to test it.
 if candidateLookup=='unavailable' then return nil end
 local value,ok=textApi.find(e)
 -- Failed query capabilities are remembered once, never worked around by
 -- creating a candidate identity or matching a rendered string to a key.
 candidateLookup=ok and 'available' or 'unavailable'
 return ok and value or nil
end
local function reference(e)
 assert(not isCandidate(e),'Candidate identity cannot use the registration fallback')
 local id=e.ns..'\0'..e.key
 if not references[id] then
  local row=ensureScratch()
  -- Newer UE adds a parameter after an out FText. This UE4SS build cannot
  -- marshal it. An empty minimal patch obtains an identity-backed FText without
  -- replacing its active display string; retain it before registering updates.
  row.Category=0;row.NativeCulture=e.culture or 'en';row.Namespace=e.ns;row.Key=e.key
  row.NativeString=e.source;row.bIsMinimalPatch=true;row.LocalizedStrings:Empty()
  references[id]=lib:PolyglotDataToText(row)
 end
 local value=references[id]:ToString()
 -- A minimal patch intentionally returns empty for an identity which has not
 -- been loaded yet. It is not evidence that the game's original text is empty.
 if value=='' then references[id]=nil;return nil end
 return value
end
local function lookup(e)
 if isCandidate(e) then return existingLookup(e) end
 if sourceArgument then return reference(e)
 else
  local value,ok=textApi.find(e)
  if not ok then sourceArgument=true;return reference(e) end
  return value
 end
end
local function native(e,text,language)
 local row=ensureScratch()
 row.Category=0;row.NativeCulture=e.culture or 'en';row.Namespace=e.ns;row.Key=e.key
 row.NativeString=e.source;row.bIsMinimalPatch=true;row.LocalizedStrings:Empty()
 for languageCode,value in pairs(e.localized or {}) do row.LocalizedStrings:Add(languageCode,value) end
 row.LocalizedStrings:Add(language,text);return lib:PolyglotDataToText(row)
end
local function original(e,language)
 local current=lookup(e);if current then return current end
 if isCandidate(e) then return nil end
 local localized=e.localized or {};return localized[language] or localized[language:match('^[^-]+')] or e.source
end
local function activeSource(e,language)
 if existingChinese(e) then return nil end
 local normalized=language:lower():gsub('_','-');local base=normalized:match('^[^-]+')
 local fallback,fallbackLanguage
 for locale,value in pairs(e.localized or {}) do
  local code=locale:lower():gsub('_','-')
  if code==normalized then
   if not bounded(value,24000,false) then return nil end
   if existingChinese({source=value,culture=locale,localized={[locale]=value}}) then return nil end
   return value
  elseif code==base then fallback=value;fallbackLanguage=locale end
 end
 if fallback then
  if not bounded(fallback,24000,false) then return nil end
  if existingChinese({source=fallback,culture=fallbackLanguage,localized={[fallbackLanguage]=fallback}}) then return nil end
  return fallback
 end
 return e.source
end
restore=function()
 fontTexts={}
 deferredCultures={}
 local started,steps=os.time(),0
 enqueue(function()
  if slate.restoreStep() then return end
  if os.time()-started>=5 then error('Slate text restoration incomplete') end
  return false
 end)
 enqueue(function()
  if not widgetText or widgetText.restoreStep() then return end
  if os.time()-started>=5 then error('Widget text restoration incomplete') end
  return false
 end)
 enqueue(function()
  steps=steps+1
  local done=fonts.restoreStep(32)
  if done and fonts.count()==0 then return end
  if steps>=256 or os.time()-started>=5 then error('Font restoration incomplete') end
  return false
 end)
 for id,change in pairs(changed) do
  enqueue(function()
    -- Active-culture lookup cannot prove ownership or readback for another
    -- culture. Keep the recovery record and never change the game's language.
   if intl:GetCurrentLanguage():ToString()~=change.culture then
     deferredCultures[change.culture]=(deferredCultures[change.culture] or 0)+1
     -- Keep the old culture's ownership until it can be read back. Its
     -- invisible entries must not freeze work in the currently active culture.
     return
   end
   local value=lookup(change.e)
   if value==nil then error('Localization restoration identity unavailable') end
   -- A later game or third-party write relinquishes our ownership. Do not
   -- restore a stale snapshot over that newer display value.
   if value==change.applied then
    native(change.e,change.original,change.culture)
    if lookup(change.e)~=change.original then error('Localization restoration not confirmed') end
   end
   clearChange(id)
  end)
 end
end
local function apply(e,text,result,requestSource)
 local function mark(key)if result then result[key]=result[key]+1 end end
 local source=activeSource(e,culture)
 if not source or requestSource and requestSource~=source then mark('protected');return end
 if not bounded(text,48000,false) then mark('unresolved');return end
 if text==source then mark('unchanged');return end
 local language=culture
 mark('queued')
 enqueue(function()
  local id=e.ns..'\0'..e.key;local ownerKey=changeKey(e,language);local prior=changed[ownerKey]
   if blockedIdentities[id] or candidateConflicts[id] or cultureProtected(id,language) then mark('protected');return end
   if not enabled or intl:GetCurrentLanguage():ToString()~=language then mark('unresolved');return end
  if isCandidate(e) then
   -- Recheck in the callback that performs the write, including on enable or
   -- cache delivery. A previous culture/visible scan is not authorization.
    if candidateConflicts[id] then mark('protected');return end
  end
  if not prior then
   local value=original(e,language)
   -- Preserve the game's existing Chinese and third-party localized display.
    if value~=source then
     if value then protectedFontTexts[value]=true;mark('protected') else mark('unresolved') end
     return
    end
   prior={e=e,original=value,culture=language};changed[ownerKey]=prior
   identityOwners[id]=identityOwners[id] or {};identityOwners[id][ownerKey]=true
  else
   local value=lookup(e)
   if value~=prior.applied and value~=prior.original then
    if value then protectedFontTexts[value]=true end
     protectCulture(id,language);clearChange(ownerKey);mark('protected');return
   end
    if value==text then mark('unchanged');return end
  end
  prior.applied=text
  native(e,text,language)
   if lookup(e)~=text then mark('failed');error('Localization apply not confirmed') end
   mark('applied')
  fontTexts[text]=true
  fontUpdateNeeded=true
 end)
end
local function applyAll()
 for _,e in ipairs(entries) do apply(e,translations[activeSource(e,culture) or '']) end
end
local function retryCachedIdentities(visible)
 if not enabled then return end
 local attempted,count={},0
 -- A widget can be recreated or a game can reset a localization display after
 -- the initial apply. Retry *all* known identities, not only Blueprint hints.
 -- Display text selects work; only its catalogued identity enters apply, whose
 -- live ownership check still protects unrelated/third-party localized values.
 for _,source in ipairs(visible) do
  local text=translations[source]
  if text then
   for _,e in ipairs(bySource[source] or {}) do
    local id=e.ns..'\0'..e.key
    if not attempted[id] then
     attempted[id]=true;count=count+1;apply(e,text,nil,source)
     if count>=32 then break end
    end
   end
  end
  if count>=32 then break end
 end
 -- Bound visited slots as well as writes so unresolved entries cannot cause
 -- an unbounded callback. Rotation continues past unloaded candidates.
 for _=1,math.min(16,#entries) do
  local e=entries[retryCursor];retryCursor=retryCursor%#entries+1
  local id=e.ns..'\0'..e.key
  if not attempted[id] then attempted[id]=true;apply(e,translations[activeSource(e,culture) or '']) end
 end
end
local function admitEntry(e)
 local source=activeSource(e,culture)
 if not source or not bounded(source,24000,false) then return end
 if not e.fusionAdmitted then
  if #entries>=100000 then return end
  e.fusionAdmitted=true;entries[#entries+1]=e
  if isCandidate(e) then candidates[#candidates+1]=e end
 end
 e.fusionSources=e.fusionSources or {};if e.fusionSources[source] then return end
 e.fusionSources[source]=true;textStream.add(source)
 if not bySource[source] then
  bySource[source]={};if not preparedSources[source] then catalog[#catalog+1]=source end
 end
 table.insert(bySource[source],e)
end
local function registerRuntime(e,display)
 if not validIdentity(e) then integration.invalidIdentities=integration.invalidIdentities+1;return false end
 local id=e.ns..'\0'..e.key;local existing=identities[id]
 if existing then
  if existing.source~=e.source then
   if not blockedIdentities[id] then integration.conflictingIdentities=integration.conflictingIdentities+1 end
   -- The same namespace/key now names another source. Our old ownership and
   -- cached FText must not later restore/register that stale source over it.
   blockedIdentities[id]=true;releaseIdentity(id);references[id]=nil
   protectDisplay(display);return 'conflict'
  end
  if blockedIdentities[id] or candidateConflicts[id] then return 'conflict' end
  local source=activeSource(existing,culture)
  if display and display~=source then
   local owned=changed[changeKey(existing,culture)]
   if not owned or display~=owned.applied then
    protectCulture(id,culture);protectedFontTexts[display]=true;clearChange(changeKey(existing,culture))
    return 'protected'
   end
  end
  if source and display==source then admitEntry(existing) end
  if enabled and source and display==source and translations[source] then
   integration.visibleCacheRefreshes=integration.visibleCacheRefreshes+1
   apply(existing,translations[source])
  end
  if source then textStream.add(source) end
  return 'known'
 end
 if #entries>=100000 then return false end
 -- Runtime discovery retains only copied strings. No widget or FText wrapper
 -- is retained across callbacks or used as an asynchronous write-back target.
 identities[id]=e
 local value=display or original(e,culture)
 local source=activeSource(e,culture)
 if not source or value~=source then
  protectDisplay(e.source);protectDisplay(value)
  preserved[#preserved+1]={e=e,value=value};skipped=skipped+1;return 'protected'
 end
 admitEntry(e)
 if enabled and translations[source] then apply(e,translations[source]) end
 return 'eligible'
end
local function discoverRuntime(includeTables)
 if not runtime then return end
 local now=os.clock()
 if now>=nextRuntimeScanAt then nextRuntimeScanAt=now+0.2;runtime.scan() end
 -- Polling current visible text does not wait for string-table key snapshots.
 -- Background table work is a separate queued job and still discovers late
 -- loaded identities without adding a second widget enumeration to the poll.
 if includeTables then enqueue(function() runtime.tables() end) end
end
local function runtimeWarning()
 if slate and (slate.status().captures or 0)>0 then return '已接入游戏界面文字；新出现的文字继续补译。' end
 if widgetText then
  local s=widgetText.status()
  if s.applied>0 or s.bindingHits>0 then
   return '已接入游戏文字控件；尚未覆盖的文字继续检测。'
  end
 end
 if #candidates>0 and candidateLookup=='unavailable' then
  return '已读取部分文字，但当前组件不能确认其替换身份；这些文字尚未写回游戏。'
 end
 if next(changed)~=nil then return '已写回已确认的游戏文字；其他界面文字继续检测。' end
 return runtime and runtime.status().warning or ''
end
local function runtimeStatus()
 local result=runtime and runtime.status() or {}
 result.pendingRestorations,result.pendingRestoreCultures=restorationSummary()
 result.restorationPending=result.pendingRestorations>0
 result.candidateLookup=candidateLookup
 result.integration=integration
 result.engineVersion=engineVersion
 result.ftextAccess='reflected-engine-operations-no-fixed-layout'
 result.ftextIdentityGate='two-distinct-identities-and-sources'
 result.textApi=textApi and textApi.status() or {}
 result.readOnlyCapture=true
 result.widgetText=widgetText and widgetText.status() or {}
 result.slate=slate and slate.status() or {}
 result.preparedControlSources=preparedCount
 result.readOnlySnapshotSeconds=0
 result.readOnlyDiscoveryRetentionSeconds=5
 result.readOnlySnapshotValidation='same-callback-path-and-parent-flags'
 result.readOnlySnapshotLimit=32
 result.performance={callbacks=timing.callbacks,requestReads=timing.requestReads,lastPumpMs=timing.lastPumpMs,
  maxPumpMs=timing.maxPumpMs,timeBasis=timing.timeBasis}
 return result
end
local function currentCulture()
 local nextCulture=intl:GetCurrentLanguage():ToString()
 if culture~=nextCulture then
  resumeAfterCulture=resumeAfterCulture or enabled
  restore();culture=nextCulture;epoch=epoch+1
  -- A source protected by its previous localized display can become the
  -- actual source after a language switch. Re-admit only a matching live
  -- identity; a cache value cannot authorize a different display or binding.
  for _,entry in pairs(identities) do
   enqueue(function()
    local e=entry;local id=e.ns..'\0'..e.key;local source=activeSource(e,culture)
    if source and not blockedIdentities[id] and not cultureProtected(id,culture) and original(e,culture)==source then
     admitEntry(e);if enabled then apply(e,translations[source]) end
    end
   end)
  end
  if enabled then applyAll() end
  return true
 end
 return false
end
local function poll()
 -- Reuse the bounded, rotating scan. The old second scan always stopped at
 -- element 512, so later TextBlocks could never receive foreground priority.
 local texts,readOnly,diagnostics={},{},{}
 local writable={};local bytes=0
 for _,source in ipairs(slate and slate.sources() or {}) do
  if not existingChinese({source=source}) and bytes+#source<=65536 then
   texts[#texts+1]=source;writable[source]=true;bytes=bytes+#source
   diagnostics[#diagnostics+1]={source=source,class='SlateText',cached=translations[source]~=nil,
    embeddedEligible=true,stage='native-slate-display-source'}
  end
 end
 for _,source in ipairs(widgetText and widgetText.sources() or {}) do
  if bytes+#source<=65536 then texts[#texts+1]=source;writable[source]=true;bytes=bytes+#source end
 end
 for _,item in ipairs(runtime and runtime.observed() or {}) do
  local text=item.text
  local requestSource=widgetText and widgetText.requestSource and widgetText.requestSource(text)
  if requestSource~=false then
  text=requestSource or text
  if bytes+#text>65536 then break end
  bytes=bytes+#text
  local known=bySource[text]
  if known and not writable[text] then texts[#texts+1]=text end
  -- Raw display capture alone never creates an FText identity or grants write
  -- ownership. The widget route above has its own live-instance validation.
  local embedded=writable[text]==true
  for _,e in ipairs(known or {}) do
   if not isCandidate(e) or changed[changeKey(e,culture)] then embedded=true;break end
  end
  local protected=not writable[text] and protectedFontTexts[text] or existingChinese({source=text})
  -- Equal source characters do not prove the same FText identity. A catalog
  -- translation can succeed while another widget with the same source stays
  -- original. Keep that observed residual available to the opt-in read-only
  -- window; do not manufacture an identity or invoke a widget setter.
  local residual=type(translations[text])=='string' and translations[text]~=text
  if (not embedded or residual) and not writable[text] and not protected then readOnly[#readOnly+1]=text end
  local stage=protected and 'protected-display' or writable[text] and 'live-widget-source' or not known and 'identity-unavailable'
   or candidateLookup=='unavailable' and not embedded and 'identity-query-unavailable'
   or translations[text] and 'translated-source-still-visible' or 'captured-awaiting-translation'
  diagnostics[#diagnostics+1]={source=text,class=item.class,catalogued=known~=nil,
   cached=translations[text]~=nil,embeddedEligible=embedded,stage=stage,
   readOnlyReason=not protected and not writable[text] and (residual and 'translated-source-still-observed' or not embedded and 'identity-unavailable') or nil}
  end
 end
 return texts,readOnly,diagnostics
end
local translationCatalogLoaded=false
local function ensureTranslationCatalog()
 if translationCatalogLoaded then return end
  local data=parse('catalog.json',32*1024*1024) or {protocol=76,entries={},skipped=0}
  assert(data.protocol==76,'Catalog version mismatch')
  skipped=data.skipped or 0
  -- Retain conflicts before scheduling any candidate writes. In particular,
  -- a duplicate row must not silently replace a different source under one ID.
  local candidateSources={}
  for _,e in ipairs(data.entries) do
   if validIdentity(e) then
    local id=e.ns..'\0'..e.key
    if candidateSources[id] and candidateSources[id]~=e.source then
     if not candidateConflicts[id] then integration.conflictingIdentities=integration.conflictingIdentities+1 end
     candidateConflicts[id]=true;blockedIdentities[id]=true
    else candidateSources[id]=e.source end
   else integration.invalidIdentities=integration.invalidIdentities+1
   end
  end
  for _,e in ipairs(data.entries) do
   enqueue(function()
    if #entries>=100000 then return end
    if validIdentity(e) then
     local id=e.ns..'\0'..e.key
     if candidateConflicts[id] then skipped=skipped+1;return end
     if identities[id] then integration.duplicateIdentities=integration.duplicateIdentities+1;return end
     identities[id]=e
     if existingChinese(e) then protectedFontTexts[e.source]=true;return end
     local value=original(e,culture)
     local source=activeSource(e,culture)
     if source and (value==source or (isCandidate(e) and value==nil)) then
      admitEntry(e)
     else skipped=skipped+1;preserved[#preserved+1]={e=e,value=value};protectDisplay(value) end
    end
   end)
  end
  -- Display-only strings carry no namespace/key and must never enter native
  -- registration. Prepare complete dialogue now; the live-widget path still
  -- validates instance, source and delegate at the moment of display.
  for _,source in ipairs(data.displaySources or {}) do
   if type(source)=='string' then enqueue(function()prepareSource(source);textStream.add(source)end) end
  end
 translationCatalogLoaded=true
 enqueue(function() discoverRuntime(true) end)
end
local function command(r)
 if r.op=='translationDisable' then resumeAfterCulture=false end
 -- Even an immediate disable after switching language must discover the new
 -- culture. Stop writes first, then retain its admission/restoration work.
 local stopping=r.op=='translationDisable' or r.op=='translationPrepare' and not r.retainStartup
 if stopping then enabled=false end
 local cultureChanged=currentCulture()
 if r.op=='translationPrepare' then
  ensureTranslationCatalog()
  epoch=epoch+1;if not r.retainStartup then if not cultureChanged then restore() end;translations={};displayTranslations={};slate.clear() end
  return {epoch=epoch}
 elseif r.op=='translationDisable' then epoch=epoch+1;if not cultureChanged then restore() end;return {epoch=epoch}
 elseif r.op=='translationEnable' then ensureTranslationCatalog();enabled=true;applyAll();return {epoch=epoch}
 elseif r.op=='translationApply' then
  if r.epoch~=epoch then return {epoch=epoch,stale=true,stored=0,applied=0} end
  if not enabled and not r.prime then return {epoch=epoch,stored=0,applied=0,disabled=true} end
   local result={epoch=epoch,stored=0,applied=0,queued=0,unchanged=0,protected=0,unresolved=0,failed=0}
  for _,p in ipairs(r.entries or {}) do
   if type(p.source)=='string' and bounded(p.text,48000,false) and (bySource[p.source] or preparedSources[p.source] or widgetText and widgetText.accepts(p.source)) then
    translations[p.source]=p.text
    slate.update(p.source,p.text);fontTexts[p.text]=true;fontUpdateNeeded=true
    result.stored=result.stored+1
     if enabled then for _,e in ipairs(bySource[p.source] or {}) do apply(e,p.text,result,p.source) end end
   end
  end
  if enabled and widgetText then enqueue(function() widgetText.update() end) end
   -- Jobs mutate this same result before the batch serializes its response.
   return result
 elseif r.op=='translationPoll' then
  discoverRuntime();local texts,readOnly,diagnostics=poll();retryCachedIdentities(texts)
  local ws=widgetText and widgetText.status() or {}
  return {epoch=epoch,texts=texts,readOnlyTexts=readOnly,textDiagnostics=diagnostics,
   writebacksConfirmed=ws.applied and ws.applied>0 and ws.applied or nil,
   nativePreprocessorHits=(ws.bindingHits or 0)+(slate.status().displayHits or 0),
   readOnlyCapability=#readOnly>0 and '已读取当前文字；未确认替换身份的内容可在独立译文窗显示。'
    or #diagnostics>0 and '已读取当前文字；本次没有需要独立显示的内容。'
    or '本次尚未读取到可确认的当前文字。',
   runtime=runtimeStatus(),runtimeWarning=runtimeWarning()}
 elseif r.op=='translationInspect' then
  local result={epoch=epoch,enabled=enabled,resolved=0,translated=0,originals=0,total=#entries,preserved=0,preserveTotal=#preserved,fontOverrides=fonts.count(),fontDiagnostics=fonts.diagnostics(),runtime=runtimeStatus(),runtimeWarning=runtimeWarning()}
  for _,e in ipairs(entries) do
   enqueue(function()
    local value=lookup(e);if value then result.resolved=result.resolved+1 end
    local source=activeSource(e,culture);local target=translations[source or '']
    if value~=nil and target~=nil and value==target then result.translated=result.translated+1 end
    if source and value==source then result.originals=result.originals+1 end
   end)
  end
  for _,p in ipairs(preserved) do enqueue(function() if original(p.e,culture)==p.value then result.preserved=result.preserved+1 end end) end
  return result
 elseif r.op=='translationCatalog' then
  local start=math.max(0,math.min(#catalog,tonumber(r.cursor) or 0));local nextCursor=math.min(#catalog,start+128);local page={}
  for i=start+1,nextCursor do page[#page+1]=catalog[i] end
  return {texts=page,total=#catalog,skipped=skipped,next=nextCursor,done=true,runtime=runtimeStatus(),runtimeWarning=runtimeWarning()}
 end
 error('Unknown operation')
end
local function encodeResponse(id,ok,result,recoverable)
 -- rxi/json encodes empty Lua tables as arrays, which is what queue text lists need.
 local pending,cultures=restorationSummary()
 write('response-'..id..'.json',{id=id,secret=secret,ok=ok,result=ok and result or {},error=not ok and tostring(result):sub(1,1024) or nil,
  errorCode=not ok and recoverable and deferredRestoreCode or nil,recoverable=not ok and recoverable or nil,
  pendingRestorations=pending,pendingRestoreCultures=cultures})
end
schedule(function()
  lib=StaticFindObject('/Script/Engine.Default__KismetTextLibrary')
  intl=StaticFindObject('/Script/Engine.Default__KismetInternationalizationLibrary')
  assert(lib:IsValid() and intl:IsValid(),'Localization library unavailable')
  textApi=textApiFactory.new(lib)
  ensureScratch()
  culture=intl:GetCurrentLanguage():ToString()
  local test={ns='Fusion76SelfTest',key=secret,source='Fusion neutral original',culture='en'}
  native(test,'Fusion neutral translated',culture);assert(lookup(test)=='Fusion neutral translated','Native replacement check failed')
  native(test,test.source,culture);assert(lookup(test)==test.source,'Native restoration check failed')
  local tablelib=StaticFindObject('/Script/Engine.Default__KismetStringTableLibrary')
  if not tablelib or not tablelib:IsValid() then tablelib=nil end
  widgetText=widgetTextFactory.new({
   active=function()return enabled and not stopped end,
   lookup=function(source,streaming)
    return textStream.lookup(source,streaming,function(s)return translations[s] or displayTranslations[s]end)
   end,
   -- A different localization display protects that FText identity, not every
   -- widget containing the same characters. In an English-localized Japanese
   -- game, the live source "Door" remains translatable. Its own current text
   -- and delegate are independently checked and restored by widget_text.
   protected=function(source)return existingChinese({source=source}) end,
   applied=function(text,b)
    fontTexts[text]=true;fontUpdateNeeded=true;fonts.hint(objectPath(b),b:GetAddress(),'TextBlock')
    fonts.prepareWidget(b,fontTexts,protectedFontTexts,text)
   end})
  widgetText.startBindings()
  runtime=runtimeFactory.new({textlib=lib,textapi=textApi,tablelib=tablelib,accept=registerRuntime,prepare=prepareSource,onWidget=function(path,address,class)
   fonts.hint(path,address,class)
   if enabled then local b=StaticFindObject(path);if b and b:IsValid() and b:GetAddress()==address then widgetText.visit(b,true) end end
  end})
  fonts.useRuntimeHints()
  if type(fonts.startEvents)=='function' then fonts.startEvents(function() return ready and enabled and not stopped, fontTexts, protectedFontTexts end,
   function(b) if enabled then widgetText.visit(b,true) end end) end
  -- Different identities with equal display text prove more than ToString.
  -- Source differs from display to detect the loader's lossy FText marshalling.
  local probeA={ns='Fusion78RuntimeProbe',key=secret..'-a',source='Fusion runtime source A',culture='en'}
  local probeB={ns='Fusion78RuntimeProbe',key=secret..'-b',source='Fusion runtime source B',culture='en'}
  local a=native(probeA,'Fusion runtime shared display',culture)
  local b=native(probeB,'Fusion runtime shared display',culture)
  runtime.probe({{text=a,ns=probeA.ns,key=probeA.key,source=probeA.source},{text=b,ns=probeB.ns,key=probeB.key,source=probeB.source}})
  native(probeA,probeA.source,culture);native(probeB,probeB.source,culture)
  if os.getenv('FUSION_UNREAL_TRANSLATION')=='1' then ensureTranslationCatalog() end
  local startup=parse('startup.json',32*1024*1024)
  if type(startup)=='table' and startup.secret==secret and type(startup.entries)=='table' then
   for source,text in pairs(startup.entries) do
    -- The host also validates formatting. This boundary still refuses malformed
    -- cache values; a cached source never creates an identity by itself.
    if bounded(source,24000,false) and bounded(text,48000,false) then translations[source]=text;textStream.add(source);fontTexts[text]=true;fontUpdateNeeded=true end
   end
  end
  if type(startup)=='table' and startup.secret==secret and type(startup.displayEntries)=='table' then
   for source,text in pairs(startup.displayEntries) do
    if bounded(source,24000,false) and bounded(text,48000,false) then displayTranslations[source]=text;textStream.add(source);fontTexts[text]=true;fontUpdateNeeded=true end
   end
  end
  enqueue(function() enabled=os.getenv('FUSION_UNREAL_TRANSLATION')=='1';if enabled then applyAll() end end)
end,function(ok,err)
 if not ok then write('hello.json',{protocol=76,secret=secret,error=tostring(err):sub(1,1024)});stopped=true end
 if ok then ready=true;lastRequest=os.time();write('hello.json',{protocol=76,secret=secret,total=#catalog,skipped=skipped,runtime=runtimeStatus(),runtimeWarning=runtimeWarning(),fontDiagnostics=fonts.diagnostics()});statusLog('ready','none') end
end)
local pumpHandle,shutdown,nextScanAt,nextRequestAt=nil,false,0,0
local function completeRequest(r,good,result,cancelled,deferred)
 if not good and not cancelled then enabled=false;stopped=not deferred end
 encodeResponse(r.id,good,result,deferred)
end
local function interruptForRestore(reason,r)
 local previous=batch
 batch=nil;busy=false;jobs={};enabled=false;resumeAfterCulture=false;epoch=epoch+1
 shutdown=not r
 statusLog('restoring',reason)
 schedule(function() restore();return {epoch=epoch} end,function(ok,result,cancelled,deferred)
  -- Complete the interrupted request only after the recovery attempt. A
  -- cancelled request never reports its partially applied batch as success.
  local priorOk,priorError=true,nil
  if previous then priorOk,priorError=pcall(previous.finished,false,'Localization batch cancelled',true) end
   if r then completeRequest(r,ok and priorOk,priorOk and result or priorError,nil,deferred and priorOk)
  else
   stopped=true
    local pending,cultures=restorationSummary(true)
    write('stopped.json',{secret=secret,restored=ok and fonts.count()==0 and next(changed)==nil and (not widgetText or widgetText.count()==0),
     pendingRestorations=pending,pendingRestoreCultures=cultures,
    error=not ok and tostring(result):sub(1,1024) or not priorOk and 'Interrupted response failed' or nil})
  end
  statusLog(ok and priorOk and 'restored' or 'failed',reason)
 end,true)
end
local function pump()
 if stopped then return end
 -- Control requests are checked even during a large catalog/apply/inspect
 -- batch. The desktop stop marker has priority over ordinary IPC work.
 if not shutdown then
  local stopOk,stopValue=pcall(read,'stop',128)
  local requestOk,r=false,nil
  -- UE callbacks run at most once per engine frame, not every requested 10ms.
  -- Count elapsed clock time: four callbacks would become 133ms at 30 FPS.
  local now=os.clock()
  if now>=nextRequestAt then
   nextRequestAt=now+0.04;timing.requestReads=timing.requestReads+1
   requestOk,r=pcall(parse,'request.json',262144)
  end
  local fresh=requestOk and type(r)=='table' and r.secret==secret and type(r.id)=='number' and r.id>lastId
  if stopOk and stopValue==secret then interruptForRestore('stop')
  elseif ready and (enabled or busy) and os.time()-lastRequest>30 then interruptForRestore('timeout')
  elseif fresh and ready and r.op=='translationDisable' and busy then
   lastId=r.id;lastRequest=os.time();interruptForRestore('disable',r)
  elseif fresh and ready and not busy then
   lastId=r.id;lastRequest=os.time()
    schedule(function() return command(r) end,function(ok,result,cancelled,deferred) completeRequest(r,ok,result,cancelled,deferred) end,
    r.op=='translationDisable' or r.op=='translationPrepare')
  end
 end
 if ready and type(fonts.flushEvents)=='function' then fonts.flushEvents() end
 advanceBatch(os.clock()+0.003)
 if ready then slate.tick(enabled and not stopped and not shutdown) end
 -- Maintenance gets a turn even if every engine frame carries a new poll.
 -- Start it after a completed response, so that response does not wait for a
 -- string-table key snapshot. Native calls themselves cannot be preempted.
  if not busy and ready and not stopped and not shutdown and os.clock()>=nextScanAt then
  nextScanAt=os.clock()+0.2
   if culture~=intl:GetCurrentLanguage():ToString() then
    schedule(function() currentCulture() end,
     function(ok,err,cancelled,deferred) if not ok and not cancelled and not deferred then error(err) end end)
   elseif next(deferredCultures) and deferredCultures[intl:GetCurrentLanguage():ToString()] then
    schedule(function()
      culture=intl:GetCurrentLanguage():ToString();restore()
      enqueue(function()
       if enabled then
        resumeAfterCulture=false;epoch=epoch+1;applyAll()
       end
      end)
     end,
     function(ok,err,cancelled,deferred) if not ok and not cancelled and not deferred then error(err) end end,true)
   elseif enabled then
    schedule(function()
      discoverRuntime()
      if widgetText then widgetText.update() end
      -- Known locres/table identities remain useful while the host is busy
      -- translating another batch. Do not require another IPC poll to reuse
      -- an already delivered translation after a game resets its display.
      local visible={}
      for _,item in ipairs(runtime and runtime.observed(true) or {}) do visible[#visible+1]=item.text end
      integration.maintenanceCachePasses=integration.maintenanceCachePasses+1
      retryCachedIdentities(visible)
      -- A native string-table snapshot is not preemptible. Let immediate
      -- cached display repairs run before starting lower-priority discovery.
      enqueue(function() runtime.tables() end)
     end,
     function(ok,err,cancelled,deferred) if not ok and not cancelled and not deferred then error(err) end end)
   end
 end
 if ready and not stopped then statusLog('running','none') end
end
local function pumpCallback()
 local started=os.clock();timing.callbacks=timing.callbacks+1
 local ok,err=pcall(pump)
 if not ok then
  statusLog('failed','pump')
  if shutdown then
   -- Recovery itself failed outside its guarded jobs. Keep the failure
   -- visible; do not retry forever or claim restoration was successful.
   enabled=false;stopped=true;batch=nil;busy=false;jobs={}
   pcall(write,'stopped.json',{secret=secret,restored=false,error='Localization recovery failed'})
  else
   local recovered,recoveryError=pcall(interruptForRestore,'pump-error')
   if not recovered then
    enabled=false;stopped=true;batch=nil;busy=false;jobs={}
    pcall(write,'stopped.json',{secret=secret,restored=false,error='Localization recovery unavailable'})
   end
  end
 end
 if stopped and pumpHandle then
  if widgetText then widgetText.closeBindings() end
  if type(fonts.closeEvents)=='function' then fonts.closeEvents() end
  local handle=pumpHandle;pumpHandle=nil
  local cancelled,value=pcall(CancelDelayedAction,handle)
  if not cancelled or value~=true then statusLog('failed','pump-cancel') end
 end
 timing.lastPumpMs=math.max(0,(os.clock()-started)*1000)
 timing.maxPumpMs=math.max(timing.maxPumpMs,timing.lastPumpMs)
end
-- These APIs exist in the shipped 44afb36d loader. No fallback to async
-- callbacks: an incompatible runtime must fail before any engine mutation.
if type(LoopInGameThreadWithDelay)~='function' or type(CancelDelayedAction)~='function'
 or type(fonts.restoreStep)~='function' or type(fonts.diagnostics)~='function'
 or type(fonts.hint)~='function' or type(fonts.useRuntimeHints)~='function' then
 stopped=true;batch=nil;busy=false;jobs={}
 write('hello.json',{protocol=76,secret=secret,error='Safe game-thread pump unavailable'})
 statusLog('failed','pump-unavailable')
else
 local ok,handle=pcall(LoopInGameThreadWithDelay,10,pumpCallback)
 if ok and type(handle)=='number' then pumpHandle=handle
 else
  stopped=true;batch=nil;busy=false;jobs={}
  write('hello.json',{protocol=76,secret=secret,error='Safe game-thread pump registration failed'})
  statusLog('failed','pump-registration')
 end
end
