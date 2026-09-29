-- Pure reflected text queries. No registration, property writes, raw pointers,
-- game-specific identities, or remote metadata wrappers retained by this module.
local M={}
local names={'FindTextInLocalizationTable','GetTextId','GetTextSourceString'}
local wanted={};for _,n in ipairs(names) do wanted[n]=true end
local function value(x) if type(x)=='string' then return x end;if x~=nil then return x:ToString() end end
local function copy(t) local r={};for k,v in pairs(t) do r[k]=type(v)=='table' and copy(v) or v end;return r end
local function matches(fields,expected)
 if #fields~=#expected then return false end
 for i,p in ipairs(expected) do if fields[i].name~=p[1] or fields[i].type~=p[2] then return false end end
 return true
end
local function layout(n,fields)
 -- Pinned Lua reflection does not expose CPF_Parm/OutParm. These exact known
 -- name/type/order shapes describe arguments, not proof of native ABI safety.
 if n=='FindTextInLocalizationTable' then
  if matches(fields,{{'Namespace','StrProperty'},{'Key','StrProperty'},{'OutText','TextProperty'},{'ReturnValue','BoolProperty'}}) then return 'out-last-3' end
  if matches(fields,{{'Namespace','StrProperty'},{'Key','StrProperty'},{'OutText','TextProperty'},{'SourceString','StrProperty'},{'ReturnValue','BoolProperty'}}) then return 'out-middle-4' end
  if matches(fields,{{'Namespace','StrProperty'},{'Key','StrProperty'},{'SourceString','StrProperty'},{'OutText','TextProperty'},{'ReturnValue','BoolProperty'}}) then return 'out-last-4' end
 elseif n=='GetTextId' and matches(fields,{{'Text','TextProperty'},{'OutNamespace','StrProperty'},{'OutKey','StrProperty'},{'ReturnValue','BoolProperty'}}) then return 'identity-3'
 elseif n=='GetTextSourceString' and matches(fields,{{'Text','TextProperty'},{'ReturnValue','StrProperty'}}) then return 'source-1' end
 return 'unsupported-signature'
end
function M.new(lib)
 local self={};local metadata;local attempts={};local reasons={}
 local function describe()
  if metadata then return end
  local found={};local total=0;local complete=true
  local ok=pcall(function()
   lib:GetClass():ForEachFunction(function(fn)
    total=total+1;if total>512 then complete=false;return true end
    local n=value(fn:GetFName())
    if wanted[n] then
     local fields={};local bounded=true
     local good=pcall(function()
      fn:ForEachProperty(function(p)
       if #fields>=8 then bounded=false;return true end
       local pn,pt=value(p:GetFName()),value(p:GetClass():GetFName())
       -- Logs contain schema tokens only, never game strings/paths/errors.
       if type(pn)~='string' or #pn>64 or not pn:match('^[A-Za-z][A-Za-z0-9_]*$') then pn='unknown' end
       if pt~='StrProperty' and pt~='TextProperty' and pt~='BoolProperty' then pt='other' end
       fields[#fields+1]={name=pn,type=pt}
      end)
     end)
     found[n]={metadata=good and bounded and 'available' or 'unavailable',fields=good and fields or {},
      layout=good and bounded and layout(n,fields) or 'metadata-unavailable'}
    end
   end)
  end)
  metadata={}
  for _,n in ipairs(names) do
   metadata[n]=found[n] or {metadata=ok and complete and 'missing-function' or 'unavailable',
    layout=ok and complete and 'missing-function' or 'metadata-unavailable',fields={}}
  end
 end
 local function status(n,reason) reasons[n]=reason;return reason end
 local function permit(n)
  describe();local shape=metadata[n].layout
  if shape=='missing-function' or shape=='unsupported-signature' then return false,status(n,shape) end
  return true,shape
 end
 function self.find(e)
  local n='FindTextInLocalizationTable';local allowed,shape=permit(n)
  if not allowed then return nil,false,shape end
  if shape=='out-middle-4' then
   -- 44afb36d leaves a scalar-out table at argument slot 1. The following
   -- FString would consume that table; passing four args cannot repair it.
   return nil,false,status(n,'native-out-argument-not-consumed')
  end
  local out={};attempts[n]=(attempts[n] or 0)+1
  local ok,found=pcall(function()
   if shape=='out-last-4' then return lib:FindTextInLocalizationTable(e.ns,e.key,e.source or '',out) end
   -- Preserve the existing working 3-argument path when old reflection has
   -- no metadata enumeration; do not label absent metadata as absent API.
   return lib:FindTextInLocalizationTable(e.ns,e.key,out)
  end)
  if not ok then return nil,false,status(n,'call-failed') end
  if type(found)~='boolean' then return nil,false,status(n,'invalid-return-type') end
  if not found then return nil,true,status(n,'not-found') end
  local good,text=pcall(value,out.OutText)
  if not good or type(text)~='string' then return nil,false,status(n,'invalid-out-value') end
  status(n,'ready');return text,true,'ready'
 end
 function self.inspect(text)
  local n='GetTextId';local allowed,reason=permit(n)
  if not allowed then return nil,reason end
  local out={};attempts[n]=(attempts[n] or 0)+1
  -- Sharing the two trailing scalar-out tables is compatible with both the
  -- pinned stack bug and its native fix; fields retain their actual names.
  local ok,found=pcall(function() return lib:GetTextId(text,out,out) end)
  if not ok then return nil,status(n,'call-failed') end
  if type(found)~='boolean' then return nil,status(n,'invalid-return-type') end
  if not found then return nil,status(n,'identity-not-returned') end
  local good,ns,key=pcall(function() return value(out.OutNamespace),value(out.OutKey) end)
  if not good or type(ns)~='string' or type(key)~='string' then return nil,status(n,'invalid-out-value') end
  status(n,'returned-unverified')
  local sourceName='GetTextSourceString';allowed,reason=permit(sourceName)
  if not allowed then return nil,reason end
  attempts[sourceName]=(attempts[sourceName] or 0)+1
  local sourceOk,source=pcall(function() return value(lib:GetTextSourceString(text)) end)
  if not sourceOk then return nil,status(sourceName,'call-failed') end
  if type(source)~='string' then return nil,status(sourceName,'invalid-return-type') end
  status(sourceName,'returned-unverified')
  return {ns=ns,key=key,source=source}
 end
 function self.probeResult(reason)
  -- Only the two-distinct-identities round trip can promote FText inspection.
  reasons.identityRoundtrip=reason
 end
 function self.status()
  describe();local result={metadata=copy(metadata),attempts=copy(attempts),results=copy(reasons),
   nativeMarshaller='44afb36d',propertyFlags='not-exposed',queriesOnly=true}
  return result
 end
 return self
end
return M
