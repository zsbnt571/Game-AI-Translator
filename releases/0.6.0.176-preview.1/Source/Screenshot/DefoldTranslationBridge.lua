-- Display boundary bridge. The game retains its original strings and node geometry.
local F=rawget(_G,'__fusion_defold_133')
if not F then
 F={ready=false,enabled=false,cache={},count=0,cacheBytes=0,seen={},seenCount=0,seenBytes=0,pending={},epoch=0,revision=0,owners={},displayed={},displayCount=0,lastId=0,nextPoll=0,slices={},sliceCount=0,sliceExpiry=0}
 rawset(_G,'__fusion_defold_133',F)
 local arrays=setmetatable({},{__mode='k'})
 local function array(t) arrays[t]=true;return t end
 local function quote(s) return '"'..s:gsub('[%z\1-\31\\"]',function(c) local t={['"']='\\"',['\\']='\\\\',['\n']='\\n',['\r']='\\r',['\t']='\\t'};return t[c] or string.format('\\u%04x',c:byte()) end)..'"' end
 local function encode(v,depth)
  depth=depth or 0;if depth>12 then error('bridge json depth') end
  local t=type(v);if t=='string' then return quote(v) elseif t=='number' then return string.format('%.17g',v) elseif t=='boolean' then return v and 'true' or 'false' elseif t=='nil' then return 'null' end
  if t~='table' then error('bridge json type') end
  local out={};if arrays[v] then for i=1,#v do out[#out+1]=encode(v[i],depth+1) end;return '['..table.concat(out,',')..']' end
  for k,x in pairs(v) do if type(k)=='string' then out[#out+1]=quote(k)..':'..encode(x,depth+1) end end;return '{'..table.concat(out,',')..'}'
 end
 local function read(name,limit)
  local f=io.open(F.root..'/'..name,'rb');if not f then return end;local b=f:read(limit+1);f:close();if not b or #b>limit then return end
  local ok,v=pcall(json.decode,b);if ok and type(v)=='table' then return v end
 end
 local function write(name,value)
  local ok,b=pcall(encode,value);if not ok then return false end
  local p=F.root..'/'..name;local f=io.open(p..'.tmp','wb');if not f then return false end;local saved=f:write(b);f:close();if not saved then return false end
  os.remove(p);return os.rename(p..'.tmp',p)~=nil
 end
 F.root=os.getenv('FUSION_DEFOLD_SESSION');F.secret=os.getenv('FUSION_DEFOLD_SECRET')
 if F.root and F.secret and #F.secret==64 and json and json.decode then
  F.enabled=os.getenv('FUSION_DEFOLD_TRANSLATION')=='1'
  local startup=read('startup.json',33554432)
  if startup and startup.secret==F.secret and type(startup.entries)=='table' then
   for s,t in pairs(startup.entries) do if F.count<100000 and type(s)=='string' and type(t)=='string' and #s>0 and #s<=24000 and #t>0 and #t<=48000 then F.cache[s]=t;F.count=F.count+1;F.cacheBytes=F.cacheBytes+#s+#t end end
  end
  F.ready=true;F.hello=write('hello.json',{secret=F.secret,protocol=133,engine='defold'})
 end
 function F.capture(s)
  if type(s)=='string' and s:match('^%s*[A-Za-z]%s*$') then return end
  if F.ready and F.enabled and type(s)=='string' and #s>0 and #s<=24000 and not F.seen[s] and F.seenCount<100000 and F.seenBytes+#s<=16777216 and #F.pending<4096 then F.seen[s]=true;F.seenCount=F.seenCount+1;F.seenBytes=F.seenBytes+#s;F.pending[#F.pending+1]=s end
 end
 function F.translation(s)
  F.capture(s);if F.ready and F.enabled then local t=F.cache[s];if t then if not F.displayed[s] then F.displayed[s]=true;F.displayCount=F.displayCount+1 end;return t end end;return s
 end
 local raw_sub=string.sub
 local function now() return socket.gettime() end
 local function chars(s) local c={};for ch in s:gmatch('[%z\1-\127\194-\244][\128-\191]*') do c[#c+1]=ch end;return c end
 -- Observe the full source supplied to a reveal operation; never change its
 -- return value. The display can then request one complete sentence instead
 -- of translating each prefix. Ambiguous or old observations are not guessed.
 string.sub=function(s,a,b)
  local result=raw_sub(s,a,b)
  if F.ready and F.enabled and type(s)=='string' and a==1 and b and b>0 and #s>=8 and #s<=6000 and #result>0 and #result<#s and s:find('%a') and s:find('%s') then
   local t=now();if t>F.sliceExpiry then F.slices={};F.sliceCount=0;F.sliceExpiry=t+0.25 end
   if F.sliceCount<128 then local old=F.slices[result];if old==nil then F.sliceCount=F.sliceCount+1;F.slices[result]=s elseif old~=s then F.slices[result]=false end end
  end
  return result
 end
 local function observe(r,s)
  if r.source~=s then
   local growing=r.source and #r.source>0 and #s>#r.source and raw_sub(s,1,#r.source)==r.source
   if not growing then r.hold=nil;r.full=nil end
   r.source=s;r.growing=growing;r.due=now()+0.2;r.dirty=true
  end
  local full=now()<=F.sliceExpiry and F.slices[s]
  if type(full)=='string' then r.full=full end
  if r.full and raw_sub(r.full,1,#s)~=s then r.full=nil end
 end
 local function display(r,initial)
  local s=r.source
  if not F.enabled then r.dirty=false;r.hold=nil;return s end
  if r.full then
   local full=F.translation(r.full);r.dirty=false
   if full~=r.full then
    local sourceChars=chars(r.full);local shown=chars(s);local target=chars(full)
    local n=math.min(#target,math.floor(#target*#shown/math.max(1,#sourceChars)))
    local value=table.concat(target,'',1,n);r.hold=value;return value
   end
   return r.hold or s
  end
  if s:match('^%s*[A-Za-z]%s*$') then return s end
  -- A complete cached menu/message must not acquire a new debounce delay.
  -- Growing unknown prefixes keep the last Chinese result until stable; the
  -- observed-full-source path above always takes precedence over prefix caches.
  if not r.growing and F.cache[s] then local t=F.translation(s);r.dirty=false;r.hold=t;return t end
  if not initial and now()<(r.due or 0) then return r.hold or s end
  r.dirty=false
  local t=F.translation(s);if t~=s then r.hold=t;return t end
  return r.hold or s
 end
 local function fit(n,s,r)
  if not F.enabled or s==r.source or not gui.get_size then return s end
  local ok,value=pcall(function()
   local box=gui.get_size(n);if box.x<=0 then return s end
   local font=gui.get_font(n);local leading=gui.get_leading and gui.get_leading(n) or 1;local tracking=gui.get_tracking and gui.get_tracking(n) or 0
   local measure,api
   if resource and resource.get_text_metrics and gui.get_font_resource then
    font=gui.get_font_resource(font);api='resource'
    measure=function(text) return resource.get_text_metrics(font,text,{line_break=false,leading=leading,tracking=tracking}) end
   elseif gui.get_text_metrics then
    api='gui-legacy'
    -- Old engines use positional arguments and a GUI font id, not a resource
    -- hash or the new options table. No temporary text is written to the node.
    measure=function(text) return gui.get_text_metrics(font,text,1000000,false,leading,tracking) end
   else F.layoutApi='unavailable';return s end
   F.layoutApi=api
   local full=r.full and F.cache[r.full];local body=full or s
   local function reveal(value) if not full then return value end;local c=chars(value);return table.concat(c,'',1,math.min(#c,math.floor(#c*#chars(r.source)/math.max(1,#chars(r.full))))) end
   local stamp=body..'|'..tostring(font)..'|'..box.x..'|'..box.y..'|'..leading..'|'..tracking..'|'..api
   if r.layoutKey==stamp then return reveal(r.layout) end
   local out,line={},'';local count=0
   for _,ch in ipairs(chars(body)) do
    count=count+1;if count>6000 then return s end
    if ch=='\n' then out[#out+1]=line;line=''
    elseif ch~='\r' then
     local metrics=measure(line..ch)
     if #line>0 and metrics.width>box.x then out[#out+1]=line;line='' end
     line=line..ch
    end
   end
   out[#out+1]=line;r.layoutKey=stamp;r.layout=table.concat(out,'\n');return reveal(r.layout)
  end)
  if not ok then F.layoutFailures=(F.layoutFailures or 0)+1;F.layoutError=tostring(value):sub(1,160) end
  return ok and value or s
 end
 local get_text=gui and gui.get_text;local set_text=gui and gui.set_text
 -- Release builds omit reverse hash names. tostring(url) then gives identical
 -- <unknown> labels for different scenes, so ownership must use the hashes.
 local function url_key(u) return hash_to_hex(u.socket)..':'..hash_to_hex(u.path)..':'..hash_to_hex(u.fragment) end
 local function owner()
  local ok,u=pcall(msg.url);if not ok then return nil end;local key=url_key(u);local o=F.owners[key]
  if not o then if F.ownerCount and F.ownerCount>=256 then return end;o={nodes={},labels={},count=0,labelCount=0};F.owners[key]=o;F.ownerCount=(F.ownerCount or 0)+1 end;return o,key
 end
 local function remember(n,s)
  local o=owner();if not o then return end;local r=o.nodes[n]
  if not r then if o.count>=2048 then return end;r={};o.nodes[n]=r;o.count=o.count+1 end;observe(r,s);r.revision=F.revision;return r
 end
 if get_text and set_text then
  gui.set_text=function(n,s)
   if type(s)~='string' then return set_text(n,s) end
   local r=remember(n,s);local t=r and fit(n,display(r,false),r) or s;if r then r.last=t end;return set_text(n,t)
  end
  gui.get_text=function(n)
   local o=owner();local r=o and o.nodes[n];local actual=get_text(n)
   if r and actual==r.last then return r.source end;return actual
  end
  local create=gui.new_text_node
  if create then gui.new_text_node=function(p,s) local n=create(p,s);if type(s)=='string' then gui.set_text(n,s) end;return n end end
  local delete=gui.delete_node
  if delete then gui.delete_node=function(n) local o=owner();if o and o.nodes[n] then o.nodes[n]=nil;o.count=o.count-1 end;return delete(n) end end
  local clone=gui.clone_node
  if clone then gui.clone_node=function(n,...) local copy=clone(n,...);local o=owner();local r=o and o.nodes[n];if r then gui.set_text(copy,r.source) end;return copy end end
  local tree=gui.clone_tree
  if tree then gui.clone_tree=function(n,...)
   local copies=tree(n,...);local o=owner();local source={}
   if o and type(copies)=='table' then for old,r in pairs(o.nodes) do local ok,id=pcall(gui.get_id,old);if ok and copies[id] then source[copies[id]]=r.source end end;for copy,s in pairs(source) do gui.set_text(copy,s) end end
   return copies
  end end
  -- Games commonly resize or change a text font after setting its value.
  -- Invalidate only our layout; keep their geometry, scale and input unchanged.
  for _,name in ipairs({'set_size','set_font','set_leading','set_tracking','set_line_break'}) do
   local original=gui[name]
   if original then gui[name]=function(n,...)
    local result=original(n,...);local o=owner();local r=o and o.nodes[n]
    if r then r.layoutKey=nil;r.dirty=true;r.due=0;r.revision=-1 end
    return result
   end end
  end
 end
 local label_set=label and label.set_text
 local label_get=label and label.get_text
 if label_set then label.set_text=function(url,s)
  if type(s)~='string' then return label_set(url,s) end
  local o=owner();local ok,resolved=pcall(msg.url,url);local t=s
  if o and ok then local key=url_key(resolved);if o.labels[key] or o.labelCount<2048 then if not o.labels[key] then o.labelCount=o.labelCount+1;o.labels[key]={url=resolved} end;local r=o.labels[key];observe(r,s);t=display(r,false);r.last=t;r.revision=F.revision end end
  return label_set(url,t)
 end end
 if label_get then label.get_text=function(url)
  local actual=label_get(url);local o=owner();local ok,resolved=pcall(msg.url,url);local r=o and ok and o.labels[url_key(resolved)]
  return r and actual==r.last and r.source or actual
 end end
 function F.poll()
  if not F.ready then return end;local now=socket.gettime();if now<F.nextPoll then return end;F.nextPoll=now+0.05
  local stopped=read('stop.json',1024);if stopped and stopped.secret==F.secret then F.enabled=false;F.ready=false;F.revision=F.revision+1;return end
  if not F.hello then F.hello=write('hello.json',{secret=F.secret,protocol=133,engine='defold'});return end
  if F.reply then if write('response-'..F.reply.id..'.json',F.reply) then F.lastId=F.reply.id;F.reply=nil end;return end
  local q=read('request.json',262144);if not q or q.secret~=F.secret or type(q.id)~='number' or q.id<=F.lastId then return end
  local result={epoch=F.epoch};local ok=true
  if q.op=='translationPrepare' then
   F.epoch=F.epoch+1;if not q.preserveStartup then F.cache={};F.count=0;F.cacheBytes=0;F.enabled=false end;F.seen={};F.seenCount=0;F.seenBytes=0;F.pending={};F.revision=F.revision+1;result={epoch=F.epoch}
  elseif q.op=='translationEnable' then F.enabled=true;F.revision=F.revision+1
  elseif q.op=='translationDisable' then F.enabled=false;F.epoch=F.epoch+1;F.revision=F.revision+1;result={epoch=F.epoch}
  elseif q.op=='translationApply' then
   local n=0;if q.epoch~=F.epoch then result={epoch=F.epoch,stale=true,stored=0}
   else if type(q.entries)=='table' then for i=1,math.min(128,#q.entries) do local e=q.entries[i];if type(e)=='table' and type(e.source)=='string' and type(e.text)=='string' and #e.source>0 and #e.source<=24000 and #e.text>0 and #e.text<=48000 and (F.cache[e.source] or F.count<100000) then local old=F.cache[e.source];local cost=#e.text+(old and -#old or #e.source);if F.cacheBytes+cost<=67108864 then if not old then F.count=F.count+1 end;F.cacheBytes=F.cacheBytes+cost;F.cache[e.source]=e.text;n=n+1 end end end end;F.revision=F.revision+1;result={epoch=F.epoch,stored=n} end
  elseif q.op=='translationPoll' then
   local texts={};for i=1,math.min(128,#F.pending) do texts[#texts+1]=table.remove(F.pending,1) end
   result={epoch=F.epoch,texts=array(texts),ahead=array({}),displaySources=F.displayCount,nativePreprocessorHits=F.displayCount}
  elseif q.op=='translationState' then result={epoch=F.epoch,enabled=F.enabled,entries=F.count,displaySources=F.displayCount,ownerCount=F.ownerCount or 0,refreshError=F.refreshError,layoutApi=F.layoutApi,layoutFailures=F.layoutFailures or 0,layoutError=F.layoutError,revision=F.revision,pending=#F.pending}
  else ok=false end
  F.reply={id=q.id,secret=F.secret,ok=ok,result=result};if write('response-'..q.id..'.json',F.reply) then F.lastId=q.id;F.reply=nil end
 end
 function F.refresh(ids,initial)
  local o=owner();if not o then return end
  if initial and get_text then for i=1,#ids do local ok,n=pcall(gui.get_node,ids[i]);if ok then local good,s=pcall(gui.get_text,n);if good and type(s)=='string' then gui.set_text(n,s) end end end end
  local count=0
  for n,r in pairs(o.nodes) do
   if (initial or r.revision~=F.revision or r.dirty and now()>=(r.due or 0)) and count<32 then
    count=count+1;local good,actual=pcall(get_text,n)
    if not good then F.refreshError=tostring(actual);o.nodes[n]=nil;o.count=o.count-1
    else if actual~=r.last then observe(r,actual) end;local t=fit(n,display(r,initial),r);if actual~=t then local set=pcall(set_text,n,t);if set then r.last=t end else r.last=t end;r.revision=F.revision end
   end
  end
  for key,r in pairs(o.labels) do if (initial or r.revision~=F.revision or r.dirty and now()>=(r.due or 0)) and count<32 then
   count=count+1;local good,actual=true,r.last;if label_get then good,actual=pcall(label_get,r.url) end
   if not good then o.labels[key]=nil;o.labelCount=o.labelCount-1
   else if actual~=r.last then observe(r,actual) end;local t=display(r,initial);if actual~=t then local set=pcall(label_set,r.url,t);if set then r.last=t end else r.last=t end;r.revision=F.revision end
  end end
 end
 function F.wrap(env,ids,isgui)
  local init=env.init;env.init=function(self) if init then init(self) end;F.poll();F.refresh(isgui and ids or {},isgui) end
  local update=env.update;env.update=function(self,dt) if update then update(self,dt) end;F.poll();F.refresh({},false) end
  local final=env.final;env.final=function(self) local ok,u=pcall(msg.url);if ok then local key=url_key(u);if F.owners[key] then F.owners[key]=nil;F.ownerCount=F.ownerCount-1 end end;if final then final(self) end end
  local on_message=env.on_message;env.on_message=function(self,id,message,sender) if on_message then on_message(self,id,message,sender) end;if isgui and id==hash('layout_changed') then F.refresh(ids,true) end end
 end
end
return F

