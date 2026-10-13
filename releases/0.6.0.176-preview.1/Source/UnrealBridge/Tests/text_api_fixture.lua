-- Real observed schema tokens, neutral functions only; no engine invocation.
local cases={}
local function test(name,run) cases[#cases+1]={name=name,run=run} end
local function str(value) return {ToString=function() return value end} end
local function field(spec)
 return {GetFName=function() return str(spec[1]) end,GetClass=function() return {GetFName=function() return str(spec[2]) end} end}
end
local shapes={
 ['out-last-3']={{'Namespace','StrProperty'},{'Key','StrProperty'},{'OutText','TextProperty'},{'ReturnValue','BoolProperty'}},
 ['out-middle-4']={{'Namespace','StrProperty'},{'Key','StrProperty'},{'OutText','TextProperty'},{'SourceString','StrProperty'},{'ReturnValue','BoolProperty'}},
 ['out-last-4']={{'Namespace','StrProperty'},{'Key','StrProperty'},{'SourceString','StrProperty'},{'OutText','TextProperty'},{'ReturnValue','BoolProperty'}},
}
local function spec(shape,name)
 local result={};for _,p in ipairs(shapes[shape]) do result[#result+1]={p[1],p[2]} end
 result[1][1]=name;return result
end
local function fixture(fields,identity)
 local lib={calls=0,identityCalls=0}
 function lib:GetClass()
  return {ForEachFunction=function(_,callback)
   callback({GetFName=function() return str('FindTextInLocalizationTable') end,ForEachProperty=function(_,cb)
    for _,p in ipairs(fields) do if cb(field(p)) then break end end
   end})
   if identity then callback({GetFName=function() return str('GetTextId') end,ForEachProperty=function(_,cb)
    for _,p in ipairs(identity) do if cb(field(p)) then break end end
   end}) end
  end}
 end
 function lib:FindTextInLocalizationTable(...)
  self.calls=self.calls+1;self.args=table.pack(...)
  local out=self.args[self.args.n];assert(type(out)=='table','out table lost')
  out.OutText=str('Neutral translated result');return true
 end
 function lib:GetTextId() self.identityCalls=self.identityCalls+1;return false end
 return lib
end
local entry={ns='Neutral original namespace',key='NeutralKey',source='Neutral source'}
for _,shape in ipairs({'out-last-3','out-middle-4','out-last-4'}) do
 for _,name in ipairs({'Namespace','NameSpace'}) do
  test(shape..' '..name..' exact schema',function()
   local lib=fixture(spec(shape,name));local api=TEXT_API.new(lib)
   local display,ready,reason=api.find(entry);local status=api.status()
   assert(status.metadata.FindTextInLocalizationTable.layout==shape,'real schema not recognized')
   assert(status.metadata.FindTextInLocalizationTable.fields[1].name==name,'raw diagnostic spelling changed')
   if shape=='out-middle-4' then
    assert(not ready and display==nil and reason=='native-out-argument-not-consumed','native bug guard lost')
    assert(lib.calls==0,'unsafe native call occurred')
   else
    assert(ready and display=='Neutral translated result','safe mock query failed')
    assert(lib.calls==1 and lib.args[1]==entry.ns and lib.args[2]==entry.key,'identity query args changed')
    if shape=='out-last-3' then assert(lib.args.n==3 and type(lib.args[3])=='table','3 argument order changed')
    else assert(lib.args.n==4 and lib.args[3]==entry.source and type(lib.args[4])=='table','4 argument order changed') end
   end
  end)
 end
end
local function reject(name,fields)
 test(name,function()
  local lib=fixture(fields);local api=TEXT_API.new(lib);local display,ready,reason=api.find(entry)
  assert(not ready and display==nil and reason=='unsupported-signature','unknown shape accepted')
  assert(lib.calls==0,'unknown shape called native')
 end)
end
for _,name in ipairs({'namespace','NameSPACE','NameSpace1','UnknownNamespace'}) do
 reject('unknown name '..name,spec('out-last-3',name))
end
local bad=spec('out-last-3','NameSpace');bad[1][2]='TextProperty';reject('alias still requires string type',bad)
bad=spec('out-last-3','NameSpace');bad[2][1]='key';reject('other names never canonicalized',bad)
bad=spec('out-last-3','NameSpace');bad[#bad+1]={'Unexpected','StrProperty'};reject('extra field rejected',bad)
bad=spec('out-last-3','NameSpace');bad[1],bad[2]=bad[2],bad[1];reject('alias in wrong position rejected',bad)
bad=spec('out-last-3','NameSpace');table.remove(bad,4);reject('missing return field rejected',bad)
bad=spec('out-middle-4','NameSpace');bad[4][2]='TextProperty';reject('middle source type still exact',bad)
test('alias is not applied to identity function',function()
 local id={{'Text','TextProperty'},{'OutNameSpace','StrProperty'},{'OutKey','StrProperty'},{'ReturnValue','BoolProperty'}}
 local lib=fixture(spec('out-last-3','Namespace'),id);local api=TEXT_API.new(lib)
 local identity,reason=api.inspect({})
 assert(identity==nil and reason=='unsupported-signature' and lib.identityCalls==0,'identity function name was generalized')
end)
local result={}
for _,case in ipairs(cases) do local ok,message=pcall(case.run);result[#result+1]={name=case.name,passed=ok,detail=ok and '' or tostring(message)} end
return result
