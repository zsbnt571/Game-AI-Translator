-- Current-display evidence only: no UE runtime, provider, native DLL or game.
local factory=assert(load(RUNTIME_SOURCE))()
local tests={}
local function test(name,run) tests[#tests+1]={name=name,run=run} end
local function setup(count)
 local f={now=10,blocks={},registry={},resolves=0,accepted=0,writeCalls=0}
 function f.widget(index,text)
  local path='/Fixture/Screen_'..index
  local b={text=text or 'Visible '..index,valid=true,visible=true,path=path,address=index}
  function b:IsValid() return self.valid end
  function b:IsVisible() return self.visible end
  function b:GetParent() return self.parent end
  function b:GetFullName() return 'TextBlock '..self.path end
  function b:GetAddress() return self.address end
  function b:GetText()
   if self.textError then error('synthetic getter unavailable') end
   local text=self.text;return {ToString=function() return text end}
  end
  function b:SetText() f.writeCalls=f.writeCalls+1;error('read-only capture wrote a widget') end
  return b
 end
 for index=1,count do
  local b=f.widget(index);f.blocks[index]=b;f.registry[b.path]=b
 end
 f.runtime=factory.new({clock=function()return f.now end,snapshotClock=function()return f.now end,
  find=function(class)return class=='TextBlock' and f.blocks or {} end,
  resolve=function(path)
   f.resolves=f.resolves+1;f.now=f.now+(f.resolveCost or 0)
   if f.resolveError then error('synthetic missing object resolver') end
   return f.registry[path]
  end,
  accept=function()f.accepted=f.accepted+1;return 'eligible'end,
  inspect=function()return nil,'identity-not-returned'end})
 for _=1,math.max(1,math.ceil(count/16)) do f.runtime.scan() end
 return f
end
local function only(f,expected)
 local result=f.runtime.observed()
 assert(#result==1 and result[1].text==expected,'snapshot retained old or lost new display')
 return result
end
local function none(f)
 assert(#f.runtime.observed()==0,'invalidated display remained in current snapshot')
 assert(f.runtime.status().visibleRead==false,'empty snapshot advertised a current read')
end

test('empty_runtime_does_not_advertise_successful_capture',function()
 local f=setup(0);none(f)
 assert(f.runtime.status().visibleReadEver==false,'empty runtime advertised historical text')
end)
test('lost_identity_does_not_block_current_display_capture',function()
 local f=setup(1)
 assert(not f.runtime.probe({{text='A',ns='x',key='a',source='A'},{text='B',ns='y',key='b',source='B'}}))
 only(f,'Visible 1')
 local s=f.runtime.status()
 assert(s.identityDetail=='identity-not-returned' and not s.identityReplacement,'identity guard weakened')
 assert(s.visibleRead and f.accepted==0 and f.writeCalls==0,'read-only capture took replacement ownership')
end)
test('destroyed_widget_is_removed_before_five_second_ttl',function()
 local f=setup(1);only(f,'Visible 1');f.registry['/Fixture/Screen_1']=nil
 none(f);assert(f.now==10,'test accidentally waited for expiry')
end)
test('invalid_widget_is_removed_before_next_discovery_scan',function()
 local f=setup(1);f.blocks[1].valid=false;none(f)
end)
test('hidden_local_widget_is_removed_before_next_discovery_scan',function()
 local f=setup(1);f.blocks[1].visible=false;none(f)
end)
test('hidden_ancestor_is_removed_before_next_discovery_scan',function()
 local f=setup(1);local parent=f.widget(2);parent.visible=false;f.blocks[1].parent=parent;none(f)
end)
test('cleared_text_is_not_replayed_from_previous_snapshot',function()
 local f=setup(1);f.blocks[1].text='';none(f)
end)
test('changed_text_is_read_without_waiting_for_discovery_scan',function()
 local f=setup(1);f.blocks[1].text='New current line';only(f,'New current line')
end)
test('path_reuse_resolves_new_object_instead_of_borrowed_wrapper',function()
 local f=setup(1);local old=f.blocks[1]
 old.GetText=function()error('stale wrapper accessed')end
 local new=f.widget(1,'Replacement scene');new.address=901
 f.registry[new.path]=new;only(f,'Replacement scene')
 assert(f.writeCalls==0 and f.accepted==0,'replacement inherited stale FText identity')
end)
test('resolver_path_mismatch_is_not_visibility_evidence',function()
 local f=setup(1);f.registry['/Fixture/Screen_1']=f.widget(2,'Other object');none(f)
end)
test('getter_failure_discards_stale_text_without_crashing',function()
 local f=setup(1);f.blocks[1].textError=true;none(f)
 assert(f.runtime.status().snapshotInvalidated==1,'invalidation not recorded')
end)
test('resolver_failure_discards_stale_text_without_crashing',function()
 local f=setup(1);f.resolveError=true;none(f)
end)
test('parent_cycle_is_unknown_and_does_not_replay_old_text',function()
 local f=setup(1);f.blocks[1].parent=f.blocks[1];none(f)
end)
test('invalid_nonzero_parent_is_not_a_visible_root',function()
 local f=setup(1);local parent=f.widget(2);parent.valid=false;f.blocks[1].parent=parent;none(f)
end)
test('null_parent_wrapper_remains_a_valid_root',function()
 local f=setup(1);local parent=f.widget(2);parent.valid=false;parent.address=0;f.blocks[1].parent=parent
 only(f,'Visible 1')
end)
test('current_snapshot_is_bounded_and_rotates_fairly',function()
 local f=setup(100);local seen={}
 for _=1,4 do
  local before=f.resolves;local rows=f.runtime.observed()
  assert(f.resolves-before==32 and #rows==32,'snapshot exceeded or failed item budget')
  assert(f.runtime.status().snapshotPartial,'partial scan reported whole screen coverage')
  for _,row in ipairs(rows) do seen[row.text]=true end
 end
 local count=0;for _ in pairs(seen)do count=count+1 end
 assert(count==100,'later captured widgets starved')
end)
test('slow_native_call_yields_after_one_and_next_snapshot_continues',function()
 local f=setup(6);f.resolveCost=.004;local seen={}
 for _=1,6 do
  local before=f.resolves;local rows=f.runtime.observed()
  assert(f.resolves-before==1 and #rows==1,'slow call did not yield or stale unverified items were published')
  seen[rows[1].text]=true
 end
 local count=0;for _ in pairs(seen)do count=count+1 end
 assert(count==6,'time-budget rotation starved later widgets')
end)
test('maintenance_cannot_starve_host_snapshot_rotation',function()
 local f=setup(128);local seen={}
 for _=1,4 do
  for _,row in ipairs(f.runtime.observed())do seen[row.text]=true end
  -- An adversarial stable cadence: sharing cursors would repeatedly return
  -- the first 32 items to the host while maintenance consumed the other 96.
  for _=1,3 do f.runtime.observed(true) end
 end
 local count=0;for _ in pairs(seen)do count=count+1 end
 assert(count==128,'maintenance advanced host cursor and starved real-time rows')
end)
test('long_text_byte_pages_do_not_permanently_skip_tail_labels',function()
 local f=setup(32);local seen={}
 for index,b in ipairs(f.blocks)do b.text=string.format('%02d',index)..string.rep('x',5998) end
 for _=1,4 do
  local rows=f.runtime.observed();local bytes=0
  for _,row in ipairs(rows)do bytes=bytes+#row.text;seen[row.text]=true end
  assert(bytes<=65536 and #rows==10,'source-byte bound not applied before publishing')
 end
 local count=0;for _ in pairs(seen)do count=count+1 end
 assert(count==32,'long labels past the IPC byte bound were permanently skipped')
end)
test('duplicate_current_labels_are_deduplicated_after_validation',function()
 local f=setup(2);f.blocks[2].text='Visible 1';only(f,'Visible 1')
 assert(f.resolves==2,'duplicate source bypassed object validation')
end)
test('reset_clears_actual_capture_status',function()
 local f=setup(1);only(f,'Visible 1');f.runtime.reset();none(f)
end)
local result={}
for _,case in ipairs(tests)do
 local ok,err=pcall(case.run);result[#result+1]={name=case.name,passed=ok,detail=ok and '' or tostring(err)}
end
return result
