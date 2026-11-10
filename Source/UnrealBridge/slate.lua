-- Optional native Slate path. The loader owns engine calls; this module only
-- exchanges bounded, authenticated string copies with its isolated worker.
local M={}
function M.new(options)
 local o=options;local status={state='not-started'}
 local sent,ack,nextTick,nextHeartbeat=0,0,0,0
 local pending,reset={},false;local started,unavailable=false,false
 local fontSerial=0
 local function start()
  if started then return end;started=true
  if o.engine~='4.27' then unavailable=true;status.state='unsupported-engine';return end
  local ok,entry=pcall(function()return package.loadlib(o.script..'FusionSlate.dll','luaopen_FusionSlate')end)
  if not ok or type(entry)~='function' then unavailable=true;status.state='native-module-unavailable';return end
  local loaded=pcall(entry);if not loaded then unavailable=true;status.state='native-module-failed' end
 end
 local function update(source,text)
  if type(source)=='string' and #source>0 and #source<=24000 and type(text)=='string' and #text>0 and #text<=48000 then pending[source]=text end
 end
 local function clear()pending={};reset=true end
 local function tick(enabled,force)
  start();if unavailable then return end
  local now=os.clock();if not force and now<nextTick then return end;nextTick=now+0.15
  local ok,s=pcall(o.parse,'slate-status.json',512*1024)
  if ok and type(s)=='table' and s.secret==o.secret then
   status=s;ack=tonumber(s.id) or 0
   if s.state~='ready' then unavailable=true;return end
   for _,text in ipairs(s.texts or {}) do if type(text)=='string' and #text>0 and #text<=24000 then o.prepare(text) end end
  end
  if ack<sent then return end
  if not force and next(pending)==nil and not reset and now<nextHeartbeat and status.enabled==enabled then return end
  local entries,bytes,count={},0,0
  for source,text in pairs(pending) do
   if count>=128 or bytes+#source+#text>100000 then break end
   entries[source]=text;bytes=bytes+#source+#text;count=count+1
  end
  o.write('slate-command-'..(sent+1)..'.json',{secret=o.secret,id=sent+1,enabled=enabled,reset=reset,entries=entries})
  for source in pairs(entries) do pending[source]=nil end
  sent=sent+1;nextHeartbeat=now+1;reset=false
 end
 local function sources()return status.state=='ready' and status.texts or {} end
 local function fontState()
  start()
  if not unavailable then
   local ok,s=pcall(o.parse,'slate-status.json',512*1024)
   if ok and type(s)=='table' and s.secret==o.secret then status=s end
  end
  if status.state=='ready' and type(status.font)=='table' then return status.font end
  return {accepted=false,customFontActive=false,customFontSupported=false,independent=true,
   independentOfTranslation=true,restartRequired=false,reason='native-font-construction-unavailable'}
 end
 local function requestFont(request)
  local current=fontState();if current.customFontSupported~=true then return nil,current end
  fontSerial=math.max(fontSerial,tonumber(current.id)or 0,tonumber(current.pendingId)or 0)+1
  o.write('slate-font-command.json',{secret=o.secret,id=fontSerial,mode=request.mode,
   path=request.path or '',family=request.family or '',sha256=request.sha256 or '',expiresUnix=os.time()+5})
  return fontSerial,current
 end
 local function restoreStep()
  tick(false,true)
  return unavailable or status.state=='not-started' or status.state=='ready' and status.enabled==false and ack>=sent-1
 end
 local function diagnostics()
  return {state=status.state,enabled=status.enabled,entries=status.entries,captures=status.captures,richTextScopes=status.richTextScopes,
   displayHits=status.displayHits,measureHits=status.measureHits,layoutRefreshes=status.layoutRefreshes,errors=status.errors,
   pendingCommand=sent>ack,source='native-slate-local-symbols'}
 end
 return {tick=tick,update=update,clear=clear,sources=sources,status=diagnostics,restoreStep=restoreStep,
  fontState=fontState,requestFont=requestFont}
end
return M
