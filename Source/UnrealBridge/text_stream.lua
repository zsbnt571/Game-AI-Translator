-- Complete-source lookup for an OBSERVED growing text control. This index
-- prepares display text only; it never infers a localization namespace/key.
local M={}
local function plain(s)return type(s)=='string'and not s:find('[<>{}\\]')and not s:gsub('[\r\n\t]',''):find('%c')end
local function take(s,n)local at=utf8.offset(s,n+1);return at and s:sub(1,at-1)or s end
local function normalized(s)return (s:gsub('\r\n','\n'))end
local function cached(s,cache)
 local value=cache(s);if value then return value end
 local line=normalized(s);if line~=s then value=cache(line);if value then return value end end
 if line:find('\n',1,true)then return cache((line:gsub('\n','\r\n')))end
end
function M.new()
 local known,buckets,complete={},{},{};local count=0;local self={}
 function self.add(source,authoritative)
  -- A live observation may be a growing fragment. It is not a complete
  -- sentence candidate: longer fragments otherwise make a known full source
  -- ambiguous, or fill its prefix bucket before that full source arrives.
  if authoritative==false then return end
  if not plain(source)then return end
  local size=utf8.len(source);if not size or size<1 or size>6000 then return end
  local match=normalized(source)
  if count>=100000 and not known[match]then return end
  complete[match]=true
  if known[match]then return end
  known[match]=source;count=count+1
  -- Exact, authoritative short lines still end a typewriter stream. Only
  -- prefix inference requires a long source; do not delay "Go back." etc.
  if size<12 then return end
  local key=take(match,8);local bucket=buckets[key]
  if bucket==false then return end
  if not bucket then bucket={};buckets[key]=bucket end
  if #bucket>=64 then buckets[key]=false;return end
  bucket[#bucket+1]={source=source,match=match}
 end
 function self.resolve(source)
  if not plain(source)then return end
  source=normalized(source);if complete[source]then return end
  local size=utf8.len(source);if not size or size<8 then return end
  local bucket=buckets[take(source,8)];if not bucket then return end
  local found
  for _,full in ipairs(bucket)do
   if #full.match>#source and full.match:sub(1,#source)==source then if found then return end;found=full.source end
  end
  return found
 end
 function self.lookup(source,streaming,cache)
  local canonical=type(source)=='string'and complete[normalized(source)]and known[normalized(source)]
  if canonical then return cached(source,cache)or cached(canonical,cache),canonical end
  if not streaming then return cached(source,cache)end
  local full=self.resolve(source)
  if not full then return nil,nil,true end
  local translated=cached(full,cache)
  if type(translated)~='string'or not plain(translated)then return nil,full,true end
  local sourceSize,fullSize,outSize=utf8.len(normalized(source)),utf8.len(normalized(full)),utf8.len(translated)
  if not sourceSize or not fullSize or not outSize or outSize==0 then return nil,full,true end
  return take(translated,math.max(1,math.min(outSize,math.floor(outSize*sourceSize/fullSize)))),full,true
 end
 return self
end
return M
