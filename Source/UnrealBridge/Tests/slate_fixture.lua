local checks={}
local function check(name,value)assert(value,name);checks[#checks+1]={name=name,passed=true}end
local now=0;os.clock=function()return now end
local oldLoad=package.loadlib;package.loadlib=function()return function()end end
local state=nil;local files={};local texts={}
local m=SLATE.new({engine='4.27',script='fixture/',secret='secret',prepare=function(s)texts[s]=true end,
 parse=function()return state end,write=function(name,value)assert(files[name]==nil,'overwriting existing native command');files[name]=value end})
m.tick(true,true);check('first command is independently named',files['slate-command-1.json'].enabled)
m.update('New option','新选项');m.tick(true,true);check('pending command blocks a new write',files['slate-command-2.json']==nil)
state={state='ready',secret='wrong',id=1,enabled=true,texts={'Rejected'}};m.tick(true,true);check('invalid secret cannot acknowledge a command',files['slate-command-2.json']==nil and not texts.Rejected)
state={state='ready',secret='secret',id=1,enabled=true,texts={'Observed'}};m.tick(true,true);check('cache survives until acknowledgement',files['slate-command-2.json'].entries['New option']=='新选项' and texts.Observed)
check('restore waits while enable is unacknowledged',not m.restoreStep())
state={state='ready',secret='secret',id=2,enabled=true,texts={}};check('restore sends disable but does not claim completion',not m.restoreStep() and files['slate-command-3.json'].enabled==false)
state={state='ready',secret='secret',id=3,enabled=false,texts={}};check('restore completes only after disabled acknowledgement',m.restoreStep())
package.loadlib=function()return nil end
local unavailable=SLATE.new({engine='4.27',script='',secret='secret',write=function()error('unexpected write')end})
check('missing optional module does not block restoration',unavailable.restoreStep())
package.loadlib=oldLoad
return checks
