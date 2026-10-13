-- Offline main.lua harness: no UE4SS or game is started.
local fixture={now=10,display={},writes={},registrations={},lookups={},culture='en',setTextCalls=0,blockRestore=false,tablesLoaded=FIXTURE_TABLES,
 spawns=0,findRows=0,staleRowAccesses=0,foreignRowAccesses=0,gameThreadDepth=0,
 loopRegistrations=0,callbackCount=0,cancelCalls=0,fontRecords=0,fontRestoreCalls=0,fontUpdateCalls=0,logs={},jobCost=0}
local deferred={}
local secret=string.rep('a',64)
local function value(text)return {ToString=function()return text end}end
local function ftext(ns,key,source)
 return {ns=ns,key=key,source=source,ToString=function()return fixture.display[ns..'\0'..key] or source end}
end
local sources={Pants='Pants',Shoes='Shoes'}
for key,source in pairs(sources) do fixture.display['Clothes\0'..key]=source end
if FIXTURE_ORIGINAL then fixture.display['Clothes\0Pants']=FIXTURE_ORIGINAL end
for id,text in pairs(FIXTURE_INITIAL_DISPLAYS or {}) do fixture.display[id]=text end
fixture.cultureDisplays={en=fixture.display}
function fixture.setCulture(language,displays)
 fixture.culture=language
 if displays then fixture.cultureDisplays[language]=displays end
 fixture.cultureDisplays[language]=fixture.cultureDisplays[language] or {}
 fixture.display=fixture.cultureDisplays[language]
end
local function newRow()
 local row={LocalizedStrings={values={}}}
 function row.LocalizedStrings:Empty()self.values={}end
 function row.LocalizedStrings:Add(k,v)self.values[k]=v end
 return row
end
local function object(path,class)
 return {valid=true,path=path,class=class,IsValid=function(self)return self.valid end,
  IsA=function(self,expected)return self.class==expected end,
  GetFullName=function(self)return 'Object '..self.path end}
end
local registry={}
local structure=object('/Script/CoreUObject.PolyglotTextData','/Script/CoreUObject.ScriptStruct')
local wrongStructure=object('/Script/Engine.TableRowBase','/Script/CoreUObject.ScriptStruct')
local function newTable(path)
 local dt=object(path,'/Script/Engine.DataTable');dt.rows={};dt.address=4096
 function dt:GetRowStruct()return self.RowStruct end
 function dt:AddRow(name)
  assert(fixture.gameThreadDepth>0,'AddRow outside game thread')
  if self.foreign then fixture.foreignRowAccesses=fixture.foreignRowAccesses+1;error('foreign table modified') end
  self.rows[name]=newRow()
 end
 function dt:FindRow(name)
  assert(fixture.gameThreadDepth>0,'FindRow outside game thread')
  if self.foreign then fixture.foreignRowAccesses=fixture.foreignRowAccesses+1;error('foreign table row read') end
  fixture.findRows=fixture.findRows+1
  return self.rows[name]
 end
 return dt
end
local function poison(row)
 for key in pairs(row) do row[key]=nil end
 local function fail()fixture.staleRowAccesses=fixture.staleRowAccesses+1;error('stale borrowed row touched')end
 setmetatable(row,{__index=fail,__newindex=fail})
end
function fixture.changeScratch(mode)
 local old=fixture.latestTable
 for _,row in pairs(old.rows) do poison(row) end
 if mode=='row-reallocated' then
  for key in pairs(old.rows) do old.rows[key]=newRow() end
 elseif mode=='row-missing' then old.rows={}
 elseif mode=='collected' then old.valid=false;registry[old.path]=nil
 else
  local replacement=newTable(old.path);replacement.address=old.address;replacement.foreign=true
  if mode=='address-reused-wrong-type' then replacement.class='/Script/Engine.Actor'
  elseif mode=='address-reused-wrong-struct' then replacement.RowStruct=wrongStructure
  elseif mode=='address-reused-no-marker' then replacement.RowStruct=structure;replacement.foreign=false
  elseif mode=='path-mismatch' then replacement.path='/Fixture/Unrelated'
  else error('unknown scratch scenario') end
  registry[old.path]=replacement
 end
 fixture.oldWrapperValid=old:IsValid();fixture.oldAddress=old.address
 fixture.replacementAddress=registry[old.path] and registry[old.path].address
end
local lib={IsValid=function()return true end}
if FIXTURE_QUERY_SHAPE then
 function lib:GetClass()
  return {ForEachFunction=function(_,callback)
   local fields={
    FindTextInLocalizationTable={{'Namespace','StrProperty'},{'Key','StrProperty'},{'OutText','TextProperty'},{'SourceString','StrProperty'},{'ReturnValue','BoolProperty'}},
    GetTextId={{'Text','TextProperty'},{'OutNamespace','StrProperty'},{'OutKey','StrProperty'},{'ReturnValue','BoolProperty'}},
    GetTextSourceString={{'Text','TextProperty'},{'ReturnValue','StrProperty'}}}
   for name,properties in pairs(fields) do
    callback({GetFName=function()return value(name)end,ForEachProperty=function(_,cb)
     for _,p in ipairs(properties) do
      cb({GetFName=function()return value(p[1])end,GetClass=function()return {GetFName=function()return value(p[2])end}end})
     end
    end})
   end
  end}
 end
end
function lib:PolyglotDataToText(p)
 assert(fixture.gameThreadDepth>0,'native text write outside game thread')
 fixture.now=fixture.now+fixture.jobCost
 local id=p.Namespace..'\0'..p.Key
 fixture.registrations[#fixture.registrations+1]={ns=p.Namespace,key=p.Key,source=p.NativeString}
 for language,nextValue in pairs(p.LocalizedStrings.values) do
  if fixture.failKey==p.Key and nextValue~=p.NativeString then error('synthetic native apply failure')end
  if fixture.blockRestore and p.Namespace=='Clothes' and nextValue==p.NativeString then error('synthetic restoration refused') end
  fixture.cultureDisplays[language]=fixture.cultureDisplays[language] or {}
   if not ((fixture.ignoreApply and nextValue~=p.NativeString) or
           (fixture.ignoreRestore and nextValue==p.NativeString)) then
    fixture.cultureDisplays[language][id]=nextValue
   end
  fixture.event=(fixture.event or 0)+1
  fixture.writes[#fixture.writes+1]={ns=p.Namespace,key=p.Key,source=p.NativeString,text=nextValue,language=language,at=fixture.now,event=fixture.event}
 end
 return ftext(p.Namespace,p.Key,p.NativeString)
end
function lib:FindTextInLocalizationTable(ns,key,out)
 local id=ns..'\0'..key
 fixture.lookups[id]=(fixture.lookups[id] or 0)+1
 if FIXTURE_REFERENCE or fixture.lookupUnsupported then error('synthetic unsupported out parameter') end
 local text=fixture.display[ns..'\0'..key]
 if text==nil then return false end
 out.OutText=ftext(ns,key,sources[key] or text);return true
end
-- Match the pinned loader's lossy input FText conversion.
function lib:GetTextId(text,namespaceOut,keyOut)
 if not FIXTURE_IDENTITY or not text.ns or not text.key then return false end
 namespaceOut.OutNamespace=value(text.ns);keyOut.OutKey=value(text.key);return true
end
function lib:GetTextSourceString(text)return FIXTURE_IDENTITY and text.source or text:ToString()end
local function remote(v)return {get=function()return value(v)end}end
local tables={IsValid=function()return true end}
function tables:GetRegisteredStringTables()
 fixture.tableLists=(fixture.tableLists or 0)+1
 return fixture.tablesLoaded and {remote('Clothing')} or {}
end
function tables:GetKeysFromStringTable()
 fixture.event=(fixture.event or 0)+1;fixture.lastKeySnapshotEvent=fixture.event
 fixture.lastKeySnapshotAt=fixture.now
 fixture.keySnapshots=(fixture.keySnapshots or 0)+1;fixture.now=fixture.now+(fixture.keySnapshotCost or 0)
 return fixture.tableKeys or {remote('Pants'),remote('Shoes')}
end
function tables:GetTableNamespace()return value('Clothes')end
function tables:GetTableEntrySourceString(_,key)return value((fixture.tableSources or {})[key] or sources[key])end
local intl={IsValid=function()return true end,GetCurrentLanguage=function()return value(fixture.culture)end}
local gameplay={SpawnObject=function()
 assert(fixture.gameThreadDepth>0,'SpawnObject outside game thread')
 fixture.spawns=fixture.spawns+1
 local dt=newTable('/Fixture/Scratch_'..fixture.spawns);registry[dt.path]=dt;fixture.latestTable=dt
 return dt
end}
function StaticFindObject(path)
 if path:find('KismetTextLibrary',1,true)then return lib end
 if path:find('KismetInternationalizationLibrary',1,true)then return intl end
 if path:find('KismetStringTableLibrary',1,true)then return tables end
 if path:find('GameplayStatics',1,true)then return gameplay end
 if path=='/Script/CoreUObject.PolyglotTextData'then return structure end
 if path:find('/Fixture/',1,true)==1 then
  assert(fixture.gameThreadDepth>0,'scratch resolution outside game thread')
  return registry[path]
 end
 if registry[path] then return registry[path] end
 return {IsValid=function()return true end}
end
function FName(text)return value(text)end
function FText(text)return value(text)end
local binding=function()return ftext('Clothes','Pants',fixture.widgetSource or 'Pants')end
local widget={IsValid=function()return true end,IsVisible=function()return true end,GetParent=function()return nil end,GetText=binding,TextDelegate=binding,
 GetFullName=function()return 'TextBlock /Fixture/Text_1' end,
 SetText=function()fixture.setTextCalls=fixture.setTextCalls+1;error('SetText must not be called')end}
registry['/Fixture/Text_1']=widget
function FindAllOf(class)
 fixture.enumerations=(fixture.enumerations or 0)+1;fixture.now=fixture.now+(fixture.enumerationCost or 0)
 if class~='TextBlock' then return {} end
 if fixture.customBlocks then return fixture.customBlocks end
 if not fixture.visibleTexts then return {widget} end
 local result={}
 for index,text in ipairs(fixture.visibleTexts) do
  result[#result+1]={IsValid=function()return true end,IsVisible=function()return true end,GetParent=function()return nil end,GetText=function()return value(text)end,
   GetFullName=function()return 'TextBlock /Fixture/Text_'..index end}
  registry['/Fixture/Text_'..index]=result[#result]
 end
 return result
end
function fixture.addControl(path,source,hidden)
 local b={source=source,visible=not hidden,Text=value(source),writes=0}
 function b:IsValid()return true end
 function b:IsA(class)return class=='/Script/UMG.TextBlock' end
 function b:GetAddress()return 9000 end
 function b:GetFullName()return 'TextBlock '..path end
 function b:IsVisible()return self.visible end
 function b:GetParent()return nil end
 function b:GetText()assert(self.visible,'hidden binding invoked');return value(self.source)end
 function b:SetText(text)self.source=text:ToString();self.writes=self.writes+1 end
 b.TextDelegate={FunctionName=value('None'),Object={IsValid=function()return false end,GetAddress=function()return 0 end}}
 fixture.customBlocks={b};registry[path]=b;return b
end
local function upvalue(fn,name)
 for i=1,100 do local key,v=debug.getupvalue(fn,i);if not key then return nil end;if key==name then return v end end
end
function ExecuteInGameThread()error('legacy ExecuteInGameThread must not be registered')end
function ExecuteWithDelay()error('legacy ExecuteWithDelay must not be registered')end
function LoopAsync()error('legacy LoopAsync must not be registered')end
function LoopInGameThreadWithDelay(delay,fn)
 assert(delay==10,'expected single 10ms pump')
 if FIXTURE_REGISTRATION_ERROR then error('synthetic registration unavailable')end
 fixture.loopRegistrations=fixture.loopRegistrations+1;fixture.callback=fn
 return 17
end
function CancelDelayedAction(handle)
 assert(handle==17,'cancel owns exact loop handle')
 fixture.cancelCalls=fixture.cancelCalls+1
 if fixture.cancelBlocked then return false end
 fixture.cancelled=true;return true
end
if FIXTURE_NO_PUMP then LoopInGameThreadWithDelay=nil end
function fixture.loop()
 if fixture.cancelled or not fixture.callback then return end
 fixture.now=fixture.now+(fixture.frameSeconds or .01)
 if fixture.frameSeconds and fixture.lastCallback and fixture.now-fixture.lastCallback<.01 then return end
 fixture.lastCallback=fixture.now
 fixture.callbackCount=fixture.callbackCount+1
 fixture.gameThreadDepth=fixture.gameThreadDepth+1
 local ok,err=pcall(fixture.callback)
 fixture.gameThreadDepth=fixture.gameThreadDepth-1
 if not ok then error(err) end
end
function fixture.isBusy()return fixture.callback and upvalue(upvalue(fixture.callback,'pump'),'busy') or false end
function fixture.pump()
 local limit=0
 repeat fixture.loop();limit=limit+1;assert(limit<10000,'unbounded scheduling')until (limit>=6 and not fixture.isBusy()) or fixture.cancelled
end
function fixture.advance(seconds)fixture.now=fixture.now+seconds end
function fixture.originalBindingIntact()return widget.GetText==binding and widget.TextDelegate==binding end
function fixture.pants()return fixture.display['Clothes\0Pants']end
function fixture.external(value)fixture.display['Clothes\0Pants']=value end
-- A text value constructed later resolves the same identity registration.
-- This models the localization contract, not an actual engine load/render.
function fixture.newIdentityText(ns,key,source)return ftext(ns,key,source)end
os.getenv=function(key)
 if key=='FUSION_UNREAL_SESSION'then return FIXTURE_FOLDER end
 if key=='FUSION_UNREAL_SECRET'then return secret end
 if key=='FUSION_UNREAL_TRANSLATION'then return '1'end
end
os.time=function()return 1000+math.floor(fixture.now)end
os.clock=function()return fixture.now end
local realDofile=dofile
dofile=function(path)
 if path:match('fonts%.lua$')then
  local function restoreStep(limit)
   fixture.fontRestoreCalls=fixture.fontRestoreCalls+1
   if fixture.fontRestoreBlocked then return false end
   fixture.fontRecords=math.max(0,fixture.fontRecords-(limit or 32));return fixture.fontRecords==0
  end
  return {update=function(owned,protected,force)
   fixture.fontUpdateCalls=fixture.fontUpdateCalls+1
   if force then fixture.forcedFontUpdates=(fixture.forcedFontUpdates or 0)+1 end
   if fixture.fontUpdateError then error('synthetic font update failure')end
   end,restore=restoreStep,restoreStep=restoreStep,count=function()return fixture.fontRecords end,
   diagnostics=function()return {errors=0,recordCount=fixture.fontRecords}end,
   hint=function()end,useRuntimeHints=function()end,prepareWidget=function()end}
 end
 return realDofile(path)
end
print=function(text)fixture.logs[#fixture.logs+1]=text end
_FIXTURE=fixture
realDofile(FIXTURE_MAIN)
if not FIXTURE_NO_AUTOPUMP then fixture.pump() end
