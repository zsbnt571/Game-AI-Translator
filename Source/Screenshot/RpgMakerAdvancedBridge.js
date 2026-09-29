/* Runtime-only RPG editing. No evaluation of expressions; map files are read, never written. */
function createFusionRpgAdvanced(g,io) {
    "use strict";
    var maps=Object.create(null),options={speed:1,experience:1,move:0,hpMax:false,mpMax:false,tpMax:false,clickTeleport:false,alwaysSpeed:true},hooks=[],restores=[],changedMaps=Object.create(null),loadingMap=0,interpreterRecords=[],nextInterpreter=1;
    var mapRevision=0,mapSequence=0,mapSyncCache=null;
    function fail(t){throw new Error(t);}
    function integer(v,min,max){if(!Number.isSafeInteger(v)||v<min||v>max)fail("数值不在允许范围内。");return v;}
    function num(key,name,read,write,min,max,lock){return {key:key,name:name,type:"number",read:read,write:write,min:min,max:max,canLock:lock!==false};}
    function bool(key,name,read,write,lock){return {key:key,name:name,type:"boolean",read:read,write:write,canLock:lock!==false};}
    function label(key,name,type,read){return {key:key,name:name,type:type,read:read,write:function(){fail("请使用右侧详情操作。");},canLock:false};}
    function wrap(object,key,replacement){if(!object||typeof object[key]!=="function")return;var old=object[key],next=replacement(old);object[key]=next;hooks.push({object:object,key:key,old:old,next:next});}
    function install(){
        if(hooks.length)return;
        ["setup","refresh","changeTileset"].forEach(function(key){wrap(g.Game_Map&&g.Game_Map.prototype,key,function(old){return function(){var result=old.apply(this,arguments);mapRevision++;return result;};});});
        wrap(g.SceneManager,g.SceneManager&&typeof g.SceneManager.updateScene==="function"?"updateScene":"updateMain",function(old){return function(){
            var scene=this._scene,result=old.apply(this,arguments);
            var accelerate=options.alwaysSpeed||(g.Input&&g.Input.isPressed("ok"));
            var playable=(g.Scene_Map&&scene instanceof g.Scene_Map)||(g.Scene_Battle&&scene instanceof g.Scene_Battle);
            if(accelerate&&playable)for(var i=1;i<options.speed&&this._scene===scene&&!this._nextScene;i++)result=old.apply(this,arguments);
            return result;
        };});
        wrap(g.Game_Actor&&g.Game_Actor.prototype,"gainExp",function(old){return function(exp){return old.call(this,Math.round(exp*options.experience));};});
        wrap(g.Game_Player&&g.Game_Player.prototype,"realMoveSpeed",function(old){return function(){return Math.max(1,Math.min(8,old.apply(this,arguments)+options.move));};});
        wrap(g.Scene_Map&&g.Scene_Map.prototype,"processMapTouch",function(old){return function(){
            if(options.clickTeleport&&g.TouchInput.isTriggered()&&!g.$gameMap.isEventRunning()&&!g.$gamePlayer.isTransferring()){
                var x=g.$gameMap.canvasToMapX(g.TouchInput.x),y=g.$gameMap.canvasToMapY(g.TouchInput.y);
                if(g.$gameMap.isValid(x,y))placePlayer(x,y);return;
            }return old.apply(this,arguments);
        };});
        wrap(g.DataManager,"loadMapData",function(old){return function(id){loadingMap=id;return old.apply(this,arguments);};});
        wrap(g.DataManager,"onLoad",function(old){return function(object){
            if(object===g.$dataMap&&changedMaps[loadingMap])object.events=JSON.parse(JSON.stringify(changedMaps[loadingMap].events));
            return old.apply(this,arguments);
        };});
    }
    function placePlayer(x,y){
        // RPG Player.locate synchronizes followers, vehicle and camera itself.
        // No pending transfer means Scene_Map does not fade or reload this map.
        g.$gamePlayer.locate(x,y);
        if(g.$gameTemp&&g.$gameTemp.clearDestination)g.$gameTemp.clearDestination();
        return {moved:true,mapId:current(),x:g.$gamePlayer.x,y:g.$gamePlayer.y};
    }
    function current(){return g.$gameMap&&g.$gameMap.mapId()||0;}
    function mapData(id){
        integer(id,1,999999);
        if(id===current())return g.$dataMap;
        if(!g.$dataMapInfos||!g.$dataMapInfos[id])fail("地图目录中没有此编号。");
        if(!maps[id]){if(!io||!io.readMap)fail("此游戏未提供其他地图读取。");maps[id]=io.readMap(id);}
        return changedMaps[id]||maps[id];
    }
    function eventData(map,id){var data=mapData(map),event=data&&data.events&&data.events[id];if(!event)fail("事件不存在，请刷新。");return event;}
    function flags(data){return (g.$dataTilesets&&g.$dataTilesets[data.tilesetId]||{}).flags||[];}
    function tile(data,x,y,z){return data.data&&data.data[(z*data.height+y)*data.width+x]||0;}
    function regionRule(data,x,y){
        if(!g.Imported||!g.Imported.YEP_RegionRestrictions)return null;
        var region=tile(data,x,y,5);if(!region)return null;
        var p=g.Yanfly&&g.Yanfly.Param||{},allow=(p.RRAllAllow||[]).concat(p.RRPlayerAllow||[]),deny=(p.RRAllRestrict||[]).concat(p.RRPlayerRestrict||[]);
        // A remote map has not passed through DataManager.onLoad. Read its notes
        // without replacing $dataMap or invoking setup on the player's live map.
        var re=/<(?:PLAYER|ALL) (ALLOW|RESTRICT) REGION:\s*(\d+(?:\s*,\s*\d+)*|\d+\s+TO\s+\d+)\s*>/gi,match;
        while((match=re.exec(data.note||""))){
            var values=match[2].match(/\d+/g).map(Number),target=match[1].toUpperCase()==="ALLOW"?allow:deny;
            if(/\bTO\b/i.test(match[2])){for(var n=values[0];n<=Math.min(values[1],255);n++)target.push(n);}else Array.prototype.push.apply(target,values);
        }
        if(deny.indexOf(region)>=0)return false;if(allow.indexOf(region)>=0)return true;return null;
    }
    function pass(data,x,y,id){
        if(id===current()&&g.$gamePlayer&&g.$gamePlayer.isMapPassable&&g.$gameMap.roundXWithDirection&&g.$gameMap.roundYWithDirection){
            // Ask whether the PLAYER can enter this tile. Map.isPassable alone
            // skips plugins such as YEP Region Restrictions on parallax maps.
            return [2,4,6,8].some(function(d){
                var fromX=g.$gameMap.roundXWithDirection(x,10-d),fromY=g.$gameMap.roundYWithDirection(y,10-d);
                return g.$gameMap.isValid(fromX,fromY)&&g.$gamePlayer.isMapPassable(fromX,fromY,d);
            });
        }
        var rule=regionRule(data,x,y);if(rule!==null)return rule;
        if(id===current()&&g.$gameMap.isPassable)return [2,4,6,8].some(function(d){return g.$gameMap.isPassable(x,y,d);});
        var f=flags(data);
        for(var z=3;z>=0;z--){var t=tile(data,x,y,z),v=f[t]||0;if(!t||v&16)continue;return (v&15)!==15;}return false;
    }
    function snapshotMap(id){
        id=id||current();var data=mapData(id);if(!data||data.width*data.height>250000)fail("地图数据无效或过大。");
        var cells=[],terrain=[],regions=[],kinds=[],f=flags(data);
        for(var y=0;y<data.height;y++)for(var x=0;x<data.width;x++){
            var allowed=pass(data,x,y,id),tag=0,water=false;
            for(var z=3;z>=0;z--){var t=tile(data,x,y,z);if(!tag)tag=(f[t]||0)>>12;if(g.Tilemap&&g.Tilemap.isWaterTile&&g.Tilemap.isWaterTile(t))water=true;}
            cells.push(allowed?1:0);terrain.push(tag);regions.push(tile(data,x,y,5));kinds.push(water?2:tag?3:allowed?1:0);
        }
        var live=liveMapState(id,data);
        return {id:id,name:data.displayName||(g.$dataMapInfos&&g.$dataMapInfos[id]||{}).name||"地图 "+id,width:data.width,height:data.height,current:live.current,x:live.x,y:live.y,cells:cells,terrain:terrain,regions:regions,kinds:kinds,events:live.events};
    }
    function liveMapState(id,data){
        var events=(data.events||[]).filter(Boolean).map(function(e){var live=id===current()&&g.$gameMap.event(e.id);return live&&live._erased?null:{id:e.id,name:e.name,x:live?live.x:e.x,y:live?live.y:e.y};}).filter(Boolean);
        return {id:id,current:id===current(),x:id===current()?g.$gamePlayer.x:-1,y:id===current()?g.$gamePlayer.y:-1,events:events};
    }
    function syncMap(q){
        // During transfer, $dataMap can already belong to the destination while
        // $gameMap still describes the source. Wait for setup to finish.
        if(!g.$dataMap||g.$gamePlayer.isTransferring()||(loadingMap>0&&loadingMap!==current()))return {pending:true};
        var id=q.mapId||current(),data=mapData(id),f=flags(data),c=mapSyncCache;
        if(!c||c.id!==id||c.current!==current()||c.data!==data||c.tiles!==data.data||c.flags!==f||c.width!==data.width||c.height!==data.height||c.revision!==mapRevision){
            c=mapSyncCache={id:id,current:current(),data:data,tiles:data.data,flags:f,width:data.width,height:data.height,revision:mapRevision,token:++mapSequence};
        }
        var result={pending:false,currentMapId:current(),revision:c.token,live:liveMapState(id,data)};
        if(q.revision!==c.token)result.map=snapshotMap(id);
        return result;
    }
    function remember(key,read,write,value){var old=read();if(!restores.some(function(r){return r.key===key;}))restores.push({key:key,read:read,write:write,old:old,last:value});else restores.filter(function(r){return r.key===key;})[0].last=value;write(value);}
    function field(path){
        if(!Array.isArray(path)||path.length<2||path.length>10)fail("字段路径无效。");
        var roots={system:g.$gameSystem,party:g.$gameParty,actors:g.$gameActors,variables:g.$gameVariables,switches:g.$gameSwitches,selfSwitches:g.$gameSelfSwitches,player:g.$gamePlayer};
        var obj=roots[path[0]];if(!obj)fail("不支持的数据根。");
        for(var i=1;i<path.length;i++){
            var k=String(path[i]);if(["__proto__","prototype","constructor"].indexOf(k)>=0||!Object.prototype.hasOwnProperty.call(obj,k))fail("字段路径已经变化。");
            if(i===path.length-1)return {object:obj,key:k};obj=obj[k];if(!obj||typeof obj!=="object")fail("字段路径已经变化。");
        }
    }
    function descriptor(key){
        var p=key.split(":"),id=Number(p[1]),f=p[2],actor;
        if(p[0]==="option"){
            var name=p[1];
            if(Object.prototype.hasOwnProperty.call(options,name)){
                var names={speed:"游戏速度 / Game speed",experience:"经验倍率 / Experience",move:"移动速度加值 / Movement",hpMax:"生命保持最大 / Max HP",mpMax:"魔力保持最大 / Max MP",tpMax:"技能点保持最大 / Max TP",clickTeleport:"点击地图传送 / Click teleport",alwaysSpeed:"持续加速 / Always accelerate (关闭时按住确认键)"};
                return typeof options[name]==="boolean"?bool(key,names[name],function(){return options[name];},function(v){options[name]=v;},false):num(key,names[name],function(){return options[name];},function(v){options[name]=v;},name==="move"?-2:1,name==="experience"?100:4,false);
            }
            if(name==="through")return bool(key,"穿墙 / Walk through",function(){return g.$gamePlayer.isThrough();},function(v){remember(key,function(){return g.$gamePlayer.isThrough();},function(x){g.$gamePlayer.setThrough(x);},v);},false);
            if(["encounter","menu","save"].indexOf(name)>=0){var cap=name[0].toUpperCase()+name.slice(1);return bool(key,{encounter:"随机遇敌 / Encounters",menu:"游戏菜单 / Menu",save:"游戏存档 / Saving"}[name],function(){return g.$gameSystem["is"+cap+"Enabled"]();},function(v){remember(key,function(){return g.$gameSystem["is"+cap+"Enabled"]();},function(x){g.$gameSystem[(x?"enable":"disable")+cap]();},v);},false);}
        }
        if(p[0]==="actor"&&p.length===2){actor=g.$gameActors.actor(id);if(!actor)fail("角色不存在。");return label(key,actor.name(),"actor",function(){return actor.level;});}
        if(p[0]==="actor"&&p.length>=3){
            if(["level","hp","mp","tp"].indexOf(f)>=0)return null;
            actor=g.$gameActors.actor(id);if(!actor)fail("角色不存在。");var title=actor.name()+" · ";
            if(f==="experience")return num(key,title+"经验 / EXP",function(){return actor.currentExp();},function(v){actor.changeExp(v,false);},0,2147483647);
            if(f==="class")return num(key,title+"职业 / Class",function(){return actor._classId;},function(v){if(!g.$dataClasses[v])fail("职业不存在。");actor.changeClass(v,true);},1,g.$dataClasses.length-1,false);
            if(f==="party")return bool(key,title+"在队伍中 / In party",function(){return g.$gameParty.members().indexOf(actor)>=0;},function(v){if(v)g.$gameParty.addActor(id);else g.$gameParty.removeActor(id);},false);
            if(/^param[0-7]$/.test(f)){var param=Number(f.slice(5));return num(key,title+["最大生命 / Max HP","最大魔力 / Max MP","攻击 / Attack","防御 / Defense","魔法攻击 / Magic attack","魔法防御 / Magic defense","敏捷 / Agility","幸运 / Luck"][param],function(){return actor.param(param);},function(v){actor.addParam(param,v-actor.param(param));},param===0?1:0,999999);}
            if(f==="skill"||f==="state"){var entry=integer(Number(p[3]),1,999999),data=f==="skill"?g.$dataSkills:g.$dataStates;if(!data[entry])fail("项目不存在。");return bool(key,title+data[entry].name,function(){return f==="skill"?actor.isLearnedSkill(entry):actor.isStateAffected(entry);},function(v){if(f==="skill")actor[v?"learnSkill":"forgetSkill"](entry);else actor[v?"addState":"removeState"](entry);},false);}
        }
        if(p[0]==="event"&&p.length===3){var e=eventData(id,Number(p[2]));return label(key,e.name,"event",function(){return (e.pages||[]).length;});}
        if(p[0]==="interpreter"){var active=findInterpreter(id);return label(key,active.name,"event",function(){return active.interpreter._index;});}
        if(p[0]==="commonEvent"){var common=g.$dataCommonEvents[id];if(!common)fail("公共事件不存在。");return label(key,common.name,"event",function(){return common.list.length;});}
        if(p[0]==="event"&&p.length===4&&/^[ABCD]$/.test(p[3])){var event=eventData(id,Number(p[2])),self=[id,Number(p[2]),p[3]];return bool(key,event.name+" · 独立开关 / Self "+p[3],function(){return g.$gameSelfSwitches.value(self);},function(v){g.$gameSelfSwitches.setValue(self,v);},false);}
        if(p[0]==="field"){
            var path=JSON.parse(decodeURIComponent(key.slice(6))),ref=field(path),value=ref.object[ref.key],type=typeof value;
            if(["boolean","number","string"].indexOf(type)<0)fail("只能编辑具体值。");
            var d={key:key,name:path.join(" › "),type:type,min:-2147483648,max:2147483647,canLock:true,read:function(){return field(path).object[ref.key];},write:function(v){var current=field(path);if(typeof v!==typeof current.object[current.key])fail("字段类型已变化。");current.object[current.key]=v;if(g.$gameMap.requestRefresh)g.$gameMap.requestRefresh();}};return d;
        }
        return null;
    }
    function keys(scope,q){
        if(scope==="common")return ["gold"].concat(["through","encounter","menu","save","clickTeleport","hpMax","mpMax","tpMax","speed","move","experience","alwaysSpeed"].map(function(k){return "option:"+k;}));
        if(scope==="characters")return (g.$dataActors||[]).filter(Boolean).map(function(a){return "actor:"+a.id;});
        if(scope==="mapEvents")return (mapData(q.mapId||current()).events||[]).filter(Boolean).map(function(e){return "event:"+(q.mapId||current())+":"+e.id;});
        if(scope==="interpreters")return runningInterpreters().map(function(r){return "interpreter:"+r.id;});
        if(scope==="commonEvents")return (g.$dataCommonEvents||[]).filter(Boolean).map(function(e){return "commonEvent:"+e.id;});
        if(scope==="fields"){
            var out=[],seen=[],limit=30000;var roots={system:g.$gameSystem,party:g.$gameParty,actors:g.$gameActors,variables:g.$gameVariables,switches:g.$gameSwitches,selfSwitches:g.$gameSelfSwitches,player:g.$gamePlayer};
            function walk(obj,path){if(!obj||typeof obj!=="object"||path.length>9||seen.indexOf(obj)>=0)return;seen.push(obj);Object.keys(obj).forEach(function(k){if(--limit<0||["__proto__","prototype","constructor"].indexOf(k)>=0)return;var v=obj[k],next=path.concat(k);if(["number","boolean","string"].indexOf(typeof v)>=0&&String(v).length<=2000&&(typeof v!=="number"||Number.isFinite(v)))out.push("field:"+encodeURIComponent(JSON.stringify(next)));else walk(v,next);});}
            Object.keys(roots).forEach(function(k){walk(roots[k],[k]);});return out;
        }
        return null;
    }
    function runningInterpreters(){
        var active=[],seen=[];
        function add(interpreter,name){
            if(!interpreter||seen.indexOf(interpreter)>=0||!interpreter.isRunning||!interpreter.isRunning())return;
            seen.push(interpreter);
            var record=interpreterRecords.filter(function(r){return r.interpreter===interpreter&&r.list===interpreter._list;})[0];
            if(!record)record={id:nextInterpreter++,interpreter:interpreter,list:interpreter._list};
            record.name=name;active.push(record);add(interpreter._childInterpreter,name+" · 子事件");
        }
        add(g.$gameMap&&g.$gameMap._interpreter,"地图主事件");
        add(g.$gameTroop&&g.$gameTroop._interpreter,"战斗事件");
        if(g.$gameMap&&g.$gameMap.events)g.$gameMap.events().forEach(function(e){add(e._interpreter,"并行事件 #"+e.eventId());});
        ((g.$gameMap&&g.$gameMap._commonEvents)||[]).forEach(function(e){add(e._interpreter,"公共事件 #"+e._commonEventId);});
        interpreterRecords=active;return active;
    }
    function findInterpreter(id){var r=runningInterpreters().filter(function(x){return x.id===id;})[0];if(!r)fail("此事件已经结束或重新开始，请刷新运行中事件。");return r;}
    function executableIndex(list,index){
        integer(index,0,list.length-1);
        // Continuation rows are consumed by their preceding RPG command.
        var continuation={401:101,405:105,408:108,655:355};
        if(continuation[list[index].code]){var target=continuation[list[index].code];while(index>0&&list[index].code!==target)index--;if(list[index].code!==target)fail("这条续行没有对应的主指令。");}
        return index;
    }
    function battleAction(q){
        if(!g.$gameParty.inBattle()||!g.BattleManager)fail("当前不在战斗中。");
        var b=g.BattleManager,action=q.action;
        if(b._phase==="battleEnd"||b._phase==="aborting")fail("战斗已经结束。");
        if(action==="victory")b.processVictory();
        else if(action==="escape"){b._escaped=true;b.processAbort();}
        else if(action==="defeat")b.processDefeat();
        else if(["enemyOne","enemyMax","partyOne","partyZero"].indexOf(action)>=0){
            var members=action.indexOf("enemy")===0?g.$gameTroop.members():(g.$gameParty.battleMembers?g.$gameParty.battleMembers():g.$gameParty.members());
            members.forEach(function(a){a.setHp(action==="enemyMax"?a.mhp:action==="partyZero"?0:1);});
        }else fail("未知战斗操作。");
        return {applied:true};
    }
    function eventDetails(q){
        if(q.runtimeToken){var r=findInterpreter(q.runtimeToken),ip=r.interpreter;return {id:r.id,mapId:ip._mapId||current(),name:r.name,common:false,runtimeToken:r.id,currentIndex:ip._index,pages:[{conditions:{},list:ip._list}],fields:[]};}
        var common=!!q.common,id=integer(q.eventId,1,999999),map=q.mapId||current(),e=common?g.$dataCommonEvents[id]:eventData(map,id);if(!e)fail("事件不存在。");
        return {id:id,mapId:map,name:e.name,common:common,x:e.x||0,y:e.y||0,pages:common?[{conditions:{switch1Valid:e.trigger>0,switch1Id:e.switchId},list:e.list}]:e.pages,fields:common?[]:["A","B","C","D"].map(function(s){return "event:"+map+":"+id+":"+s;}).concat(map===current()?["event:"+map+":"+id+":x","event:"+map+":"+id+":y"]:[])};
    }
    function command(q){
        if(q.op==="maps")return {currentMapId:current(),maps:(g.$dataMapInfos||[{id:current(),name:"当前地图",parentId:0}]).filter(Boolean).map(function(m){return {id:m.id,name:m.name,parentId:m.parentId||0,current:m.id===current()};})};
        if(q.op==="map")return snapshotMap(q.mapId);
        if(q.op==="mapLive")return syncMap(q);
        if(q.op==="eventDetails")return eventDetails(q);
        if(q.op==="actorDetails"){
            var a=g.$gameActors.actor(integer(q.actorId,1,999999));if(!a)fail("角色不存在。");
            var prefix="actor:"+q.actorId+":";
            return {name:a.name(),fields:["level","experience","hp","mp","tp","class","party","param0","param1","param2","param3","param4","param5","param6","param7"].map(function(f){return prefix+f;}),skills:(g.$dataSkills||[]).filter(Boolean).map(function(s){return {key:prefix+"skill:"+s.id,name:s.name,enabled:a.isLearnedSkill(s.id)};}),states:(g.$dataStates||[]).filter(Boolean).map(function(s){return {key:prefix+"state:"+s.id,name:s.name,enabled:a.isStateAffected(s.id)};})};
        }
        if(q.op==="battleAction")return battleAction(q);
        if(q.op==="eventRun"){
            if(!g.Scene_Map||!(g.SceneManager._scene instanceof g.Scene_Map))fail("请先回到游戏地图，再执行事件。");
            if(g.$gameParty.inBattle()||g.$gamePlayer.isTransferring()||g.$gameMap.isEventRunning()||(g.$gameMessage&&g.$gameMessage.isBusy())||(g.$gameTemp&&g.$gameTemp.isCommonEventReserved()))fail("请先结束当前事件、对话或战斗。");
            var detail=eventDetails(q),page=detail.pages[integer(q.page||0,0,detail.pages.length-1)];
            if(!q.common&&detail.mapId!==current())fail("请先传送到此地图，再执行地图事件。");
            var start=executableIndex(page.list,q.index||0),interpreter=g.$gameMap._interpreter;
            if(!interpreter||typeof interpreter.setup!=="function")fail("当前场景没有可用的事件解释器。");
            if(page.list[start].indent>0)fail("请从对应分支的主指令开始执行。");
            interpreter.setup(page.list,q.common?0:detail.id);interpreter._index=start;return {started:true,index:start};
        }
        if(q.op==="interpreterGoto"){
            var r=findInterpreter(integer(q.runtimeToken,1,2147483647)),ip=r.interpreter,index=executableIndex(ip._list,q.index);
            if(g.$gameMessage&&g.$gameMessage.isBusy())fail("请先关闭游戏内当前对话，再跳转指令。");
            ip._childInterpreter=null;ip._waitCount=0;ip._waitMode="";ip._index=index;return {jumped:true,index:index};
        }
        if(q.op==="recoverParty"){g.$gameParty.members().forEach(function(a){a.recoverAll();});return {recovered:true};}
        if(q.op==="eventCommandSet"){
            if(q.runtimeToken)fail("运行中的指令仅供定位；请在地图事件或公共事件中编辑。");
            install();var detail=eventDetails(q),page=detail.pages[integer(q.page,0,detail.pages.length-1)],index=integer(q.index,0,page.list.length-1),value=q.command;
            if(!value||!Number.isSafeInteger(value.code)||!Number.isSafeInteger(value.indent)||value.indent<0||!Array.isArray(value.parameters)||JSON.stringify(value).length>16000)fail("事件指令格式无效。");
            var previous=JSON.parse(JSON.stringify(page.list[index]));page.list[index]=JSON.parse(JSON.stringify(value));if(!q.common){changedMaps[detail.mapId]=mapData(detail.mapId);if(detail.mapId===current()&&g.$gameMap.requestRefresh)g.$gameMap.requestRefresh();}return {updated:true,undo:{op:"eventCommandSet",common:!!q.common,mapId:detail.mapId,eventId:detail.id,page:q.page,index:index,command:previous}};
        }
        if(q.op==="teleport"){
            var id=integer(q.mapId,1,999999),data=mapData(id);integer(q.x,0,data.width-1);integer(q.y,0,data.height-1);
            if(g.$gameMap.isEventRunning()||g.$gamePlayer.isTransferring()||g.$gameParty.inBattle())fail("请先结束当前事件或战斗。");
            // Teleport is explicit placement, independent of walking passability.
            if(!g.Scene_Map||!(g.SceneManager._scene instanceof g.Scene_Map))fail("请先回到游戏地图。");
            if(id===current())return placePlayer(q.x,q.y);
            g.$gamePlayer.reserveTransfer(id,q.x,q.y,g.$gamePlayer.direction(),0);return {reserved:true};
        }
        return undefined;
    }
    function tick(){
        if(!g.$gameParty)return;g.$gameParty.members().forEach(function(a){if(options.hpMax&&a.hp!==a.mhp)a.setHp(a.mhp);if(options.mpMax&&a.mp!==a.mmp)a.setMp(a.mmp);if(options.tpMax&&a.tp!==a.maxTp())a.setTp(a.maxTp());});
    }
    function disable(restore){
        if(restore!==false)restores.forEach(function(r){try{if(r.read()===r.last)r.write(r.old);}catch(_){}});restores=[];
        hooks.forEach(function(h){if(h.object[h.key]===h.next)h.object[h.key]=h.old;});hooks=[];
        options={speed:1,experience:1,move:0,hpMax:false,mpMax:false,tpMax:false,clickTeleport:false,alwaysSpeed:true};maps=Object.create(null);changedMaps=Object.create(null);interpreterRecords=[];mapSyncCache=null;
    }
    return {descriptor:descriptor,keys:keys,command:command,tick:tick,disable:disable,enable:install};
}
if(typeof window==="undefined"&&typeof module!=="undefined")module.exports=createFusionRpgAdvanced;
