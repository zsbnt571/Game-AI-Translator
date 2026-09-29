/* Fusion RPG Maker MV/MZ adapter. No arbitrary expressions or object paths.
 * Inert unless launched with a per-session Fusion pipe and secret. */
(function () {
    "use strict";
    function createAdapter(g,io) {
        var enabled = false, locks = Object.create(null), history = [], generation = 0;
        var party, variables, switches, timer = null, lastError = "";
        var advanced=typeof createFusionRpgAdvanced==="function"?createFusionRpgAdvanced(g,io):null;
        function fail(text) { throw new Error(text); }
        function integer(v, min, max) {
            if (typeof v !== "number" || !Number.isSafeInteger(v) || v < min || v > max) fail("数值超出此项目允许的范围。");
            return v;
        }
        function sync() {
            if (party !== g.$gameParty || variables !== g.$gameVariables || switches !== g.$gameSwitches) {
                party = g.$gameParty; variables = g.$gameVariables; switches = g.$gameSwitches;
                locks = Object.create(null); history = []; generation++;if(advanced){advanced.disable(false);if(enabled)advanced.enable();}
            }
        }
        function ready() { return !!(party && variables && switches && g.$dataSystem && g.$gameActors && g.$gameMap && g.$gameMap.mapId()>0); }
        function requireReady() { sync(); if (!ready()) fail("请先进入游戏或读取存档。"); }
        function mapReady() {
            requireReady();
            if (!g.$gameMap || !g.$dataMap || !g.$gamePlayer || party.inBattle() || !(g.SceneManager._scene instanceof g.Scene_Map)) fail("请先回到游戏地图。");
        }
        function descriptor(key) {
            if (typeof key !== "string" || key.length > 1200) fail("无效数据项。");
            var extra=advanced&&advanced.descriptor(key);if(extra)return extra;
            var p = key.split(":"), id = Number(p[1]), obj, field;
            function number(name, read, write, min, max, canLock) { return { key:key, name:name, type:"number", min:min, max:max, canLock:canLock !== false, read:read, write:write }; }
            function boolean(name, read, write, canLock) { return { key:key, name:name, type:"boolean", canLock:canLock !== false, read:read, write:write }; }
            if (key === "gold") return number("金币 / Gold", function(){return party.gold();}, function(v){party.gainGold(v - party.gold());}, 0, party.maxGold());
            if (!/^[1-9][0-9]*$/.test(p[1] || "")) fail("无效数据编号。");
            if (p.length === 2 && /^(item|weapon|armor)$/.test(p[0])) {
                obj = (p[0] === "item" ? g.$dataItems : p[0] === "weapon" ? g.$dataWeapons : g.$dataArmors)[id];
                if (!obj || !obj.name) fail("物品已不存在。");
                return number(obj.name, function(){return party.numItems(obj);}, function(v){party.gainItem(obj, v - party.numItems(obj), false);}, 0, party.maxItems(obj));
            }
            if (p[0] === "variable" && p.length === 2) {
                if (id >= g.$dataSystem.variables.length || typeof variables.value(id) !== "number") fail("此变量不是可修改的数值。");
                return number(g.$dataSystem.variables[id] || "变量 / Variable " + id, function(){return variables.value(id);}, function(v){variables.setValue(id,v);}, -2147483648,2147483647);
            }
            if (p[0] === "switch" && p.length === 2) {
                if (id >= g.$dataSystem.switches.length) fail("开关已不存在。");
                return boolean(g.$dataSystem.switches[id] || "开关 / Switch " + id, function(){return switches.value(id);}, function(v){switches.setValue(id,v);});
            }
            if (p[0] === "actor" && p.length === 3) {
                obj = g.$gameActors._data[id]; field = p[2];
                if (!obj) fail("角色尚未载入。");
                var name = obj.name() + " · ";
                if (field === "level") return number(name + "等级 / Level", function(){return obj.level;},function(v){obj.changeLevel(v,false);},1,obj.maxLevel());
                if (field === "hp") return number(name + "生命 / HP",function(){return obj.hp;},function(v){obj.setHp(v);},0,obj.mhp);
                if (field === "mp") return number(name + "魔力 / MP",function(){return obj.mp;},function(v){obj.setMp(v);},0,obj.mmp);
                if (field === "tp") return number(name + "技能点 / TP",function(){return obj.tp;},function(v){obj.setTp(v);},0,obj.maxTp());
            }
            if (p[0] === "event" && p.length === 4) {
                mapReady(); integer(id,1,999999); var eventId=Number(p[2]);field=p[3];
                if (id !== g.$gameMap.mapId() || !/^[1-9][0-9]*$/.test(p[2])) fail("地图已改变，请刷新事件。");
                obj=g.$gameMap.event(eventId);if(!obj || !obj.event())fail("事件已不存在。");
                var title=obj.event().name + " · ";
                if (/^[ABCD]$/.test(field)) {
                    var selfKey=[id,eventId,field];
                    return boolean(title + "独立开关 / Self " + field,function(){return g.$gameSelfSwitches.value(selfKey);},function(v){g.$gameSelfSwitches.setValue(selfKey,v);},false);
                }
                if(field === "x")return number(title+"X",function(){return obj.x;},function(v){obj.locate(v,obj.y);},0,g.$gameMap.width()-1,false);
                if(field === "y")return number(title+"Y",function(){return obj.y;},function(v){obj.locate(obj.x,v);},0,g.$gameMap.height()-1,false);
            }
            fail("此数据项尚不支持修改。");
        }
        function validate(d,v) { if(d.type === "boolean"){if(typeof v !== "boolean")fail("开关值应为开启或关闭。");}else if(d.type==="string"){if(typeof v!=="string"||v.length>2000)fail("文本值无效。");}else if(d.key.indexOf("field:")===0){if(typeof v!=="number"||!Number.isFinite(v))fail("数值无效。");}else integer(v,d.min,d.max); }
        function row(key) {
            var d=descriptor(key), value=d.read();
            var result={key:key,name:d.name,value:value,type:d.type,min:d.min,max:d.max,canLock:d.canLock,locked:Object.prototype.hasOwnProperty.call(locks,key)};
            var parts=key.split(":"),data=parts[0]==="item"?g.$dataItems:parts[0]==="weapon"?g.$dataWeapons:parts[0]==="armor"?g.$dataArmors:null,obj=data&&data[Number(parts[1])];
            if(obj){result.icon=obj.iconIndex;result.description=String(obj.description||"").slice(0,2000);}
            return result;
        }
        function eventConditions(q) {
            if(!advanced)fail("事件条件读取不可用。");
            var detail=advanced.command({op:"eventDetails",mapId:q.mapId,eventId:q.eventId,common:q.common});
            function describePage(page,index) {
            var c=page.conditions||{},rows=[];
            function add(key,target,kind) {
                var r;
                try {
                    // RPG variables may contain text. Display it honestly without
                    // forcing it to zero or preventing other conditions loading.
                    if(key.indexOf("variable:")===0&&typeof variables.value(Number(key.split(":")[1]))!=="number") {
                        var id=Number(key.split(":")[1]),value=variables.value(id);
                        r={key:key,name:g.$dataSystem.variables[id]||"变量 / Variable "+id,value:value==null?null:typeof value==="object"?JSON.stringify(value):value,type:"readonly",canLock:false,locked:false};
                    } else r=row(key);
                    r.target=target;r.condition=kind;
                    r.met=kind==="atLeast"?r.value>=target:r.value===target;
                } catch(error) { r={key:key,name:key,type:"unavailable",value:null,target:target,condition:kind,met:false,error:String(error.message||error)}; }
                rows.push(r);
            }
            if(c.switch1Valid)add("switch:"+c.switch1Id,true,"equals");
            if(c.switch2Valid)add("switch:"+c.switch2Id,true,"equals");
            if(c.variableValid)add("variable:"+c.variableId,c.variableValue,"atLeast");
            if(c.selfSwitchValid&&!q.common)add("event:"+detail.mapId+":"+detail.id+":"+c.selfSwitchCh,true,"equals");
            if(c.itemValid)add("item:"+c.itemId,1,"atLeast");
            if(c.actorValid)add("actor:"+c.actorId+":party",true,"equals");
            return {index:index,rows:rows,met:rows.every(function(r){return r.met;}),commandCount:page.list.filter(function(cmd){return cmd.code!==0&&cmd.code!==108&&cmd.code!==408;}).length};
            }
            var live=!q.common&&detail.mapId===g.$gameMap.mapId()?g.$gameMap.event(detail.id):null;
            var activePage=live&&Number.isInteger(live._pageIndex)?(live._erased?-1:live._pageIndex):null;
            if(q.allPages)return {activePage:activePage,pages:detail.pages.map(describePage)};
            var index=integer(q.page||0,0,detail.pages.length-1),result=describePage(detail.pages[index],index);
            result.activePage=activePage;return result;
        }

        function snapshot(q) {
            sync();var result={engine:g.Utils && g.Utils.RPGMAKER_NAME || "Unknown",ready:ready(),enabled:enabled,generation:generation,lockCount:Object.keys(locks).length,lastError:lastError,rows:[],total:0,inBattle:!!(g.$gameParty&&g.$gameParty.inBattle())};
            if(!result.ready)return result;
            var keys=[], i, scope=q.scope || "gold";
            var advancedKeys=advanced&&advanced.keys(scope,q);
            if(advancedKeys)keys=advancedKeys;
            else if(scope === "gold")keys=["gold"];
            else if(scope === "actors")g.$gameActors._data.forEach(function(a,id){if(a)["level","hp","mp","tp"].forEach(function(f){keys.push("actor:"+id+":"+f);});});
            else if(/^(items|weapons|armors)$/.test(scope)){
                var singular=scope.slice(0,-1), data=scope==="items"?g.$dataItems:scope==="weapons"?g.$dataWeapons:g.$dataArmors;
                data.forEach(function(x,id){if(id>0&&x&&x.name)keys.push(singular+":"+id);});
            } else if(scope === "variables" || scope === "switches") {
                var names=scope==="variables"?g.$dataSystem.variables:g.$dataSystem.switches;
                for(i=1;i<names.length;i++)if(scope!=="variables"||typeof variables.value(i)==="number")keys.push((scope==="variables"?"variable:":"switch:")+i);
            } else if(scope === "events") {
                mapReady();g.$gameMap.events().forEach(function(e){["A","B","C","D","x","y"].forEach(function(f){keys.push("event:"+g.$gameMap.mapId()+":"+e.eventId()+":"+f);});});
            } else if(scope === "locks")keys=Object.keys(locks);
            else fail("未知数据分类。");
            var query=String(q.query || "").toLowerCase();
            var aliases=Array.isArray(q.aliasKeys)?q.aliasKeys.slice(0,200):[];
            if(query)keys=keys.filter(function(k){try{var d=descriptor(k);return aliases.indexOf(k)>=0||(d.name+" "+k+" "+(q.scope==="fields"?d.read():"")+" "+(q.lookup?q.lookup(d.name):"")).toLowerCase().indexOf(query)>=0;}catch(_){return false;}});
            result.total=keys.length;var offset=integer(q.offset || 0,0,1000000);
            var limit=integer(q.limit||80,1,10000);result.rows=keys.slice(offset,offset+limit).map(row);return result;
        }
        function tick() {
            sync();if(!enabled || !ready())return;
            Object.keys(locks).forEach(function(k){try{var d=descriptor(k),v=locks[k];validate(d,v);if(d.read()!==v){d.write(v);if(d.read()!==v)fail("游戏不再接受此值。");}}catch(e){delete locks[k];lastError="已解除失效锁定："+e.message;}});
            if(advanced)advanced.tick();
        }
        function disable(){enabled=false;locks=Object.create(null);history=[];if(advanced)advanced.disable();if(timer!==null){g.clearInterval(timer);timer=null;}return {enabled:false};}
        function command(q) {
            if(!q || typeof q.op!=="string")fail("请求无效。");sync();
            if(q.op==="snapshot")return snapshot(q);
            if(q.op==="status")return {ready:ready(),enabled:enabled,generation:generation,lockCount:Object.keys(locks).length,inBattle:!!(party&&party.inBattle())};
            if(q.op==="disable")return disable();
            if(q.op==="enable"){
                if(!g.Utils || !/^(MV|MZ)$/.test(g.Utils.RPGMAKER_NAME))fail("当前窗口不是支持的 RPG Maker MV/MZ 游戏。");
                enabled=true;if(advanced)advanced.enable();if(timer===null)timer=g.setInterval(tick,250);return {enabled:true};
            }
            requireReady();if(!enabled)fail("游戏修改尚未开启。");
            if(q.generation!==generation)fail("存档或游戏状态已更换，请刷新后重新选择。");
            if(q.op==="eventConditions")return eventConditions(q);
            if(q.op==="describe")return {rows:(q.keys||[]).slice(0,80).map(row)};
            if(q.op==="exportLocks")return {entries:Object.keys(locks).map(function(key){return {key:key,value:locks[key]};})};
            if(q.op==="importLocks"){
                var entries=(q.entries||[]).slice(0,500).map(function(e){var d=descriptor(e.key);if(!d.canLock)fail("此项目不能锁定。");validate(d,e.value);return {descriptor:d,value:e.value};});
                var applied=[];try{entries.forEach(function(e){applied.push({descriptor:e.descriptor,value:e.descriptor.read()});e.descriptor.write(e.value);if(e.descriptor.read()!==e.value)fail("游戏未接受锁定值。");});}catch(error){applied.reverse().forEach(function(e){try{e.descriptor.write(e.value);}catch(_){}});throw error;}entries.forEach(function(e){locks[e.descriptor.key]=e.value;});return {lockCount:Object.keys(locks).length};
            }
            if(advanced){var response=advanced.command(q);if(response!==undefined){if(response.undo){history.push({event:response.undo});delete response.undo;if(history.length>30)history.shift();}return response;}}
            if(q.op==="unlockAll"){locks=Object.create(null);return {lockCount:0};}
            if(q.op==="set" || q.op==="lock" || q.op==="unlock"){
                var d=descriptor(q.key);
                if(q.op==="unlock"){delete locks[q.key];return row(q.key);}
                validate(d,q.value);if(q.op==="lock"&&!d.canLock)fail("此项目不能持续锁定。");
                var previous=d.read();d.write(q.value);var actual=d.read();
                if(actual!==q.value)fail("游戏没有接受目标值，已刷新为实际值。");
                history.push({key:q.key,value:previous,generation:generation});if(history.length>30)history.shift();
                if(q.op==="lock" || Object.prototype.hasOwnProperty.call(locks,q.key))locks[q.key]=actual;
                return row(q.key);
            }
            if(q.op==="undo"){
                if(!history.length)fail("没有可撤销的修改。");var old=history[history.length-1];if(old.event){advanced.command(old.event);history.pop();return {restored:true};}var target=descriptor(old.key);
                validate(target,old.value);target.write(old.value);if(target.read()!==old.value)fail("游戏未接受撤销值。");
                history.pop();delete locks[old.key];return row(old.key);
            }
            if(q.op==="map") {
                mapReady();var width=g.$gameMap.width(),height=g.$gameMap.height(),cells=[];
                if(width*height>40000)fail("当前地图过大，暂不能显示格子预览。");
                for(var y=0;y<height;y++)for(var x=0;x<width;x++)cells.push([2,4,6,8].some(function(dir){return g.$gameMap.isPassable(x,y,dir);})?1:0);
                return {id:g.$gameMap.mapId(),name:g.$gameMap.displayName() || "当前地图",width:width,height:height,x:g.$gamePlayer.x,y:g.$gamePlayer.y,cells:cells,events:g.$gameMap.events().map(function(e){return {id:e.eventId(),name:e.event().name,x:e.x,y:e.y};})};
            }
            if(q.op==="teleport") {
                mapReady();if(q.mapId!==g.$gameMap.mapId())fail("地图已改变，请重新读取地图。");
                if(g.$gameMap.isEventRunning() || g.$gamePlayer.isTransferring())fail("事件或地图切换进行中，请稍后传送。");
                integer(q.x,0,g.$gameMap.width()-1);integer(q.y,0,g.$gameMap.height()-1);
                // Explicit placement also permits blocked tiles; walking rules stay unchanged.
                g.$gamePlayer.locate(q.x,q.y);
                if(g.$gameTemp&&g.$gameTemp.clearDestination)g.$gameTemp.clearDestination();
                return {moved:true,mapId:q.mapId,x:g.$gamePlayer.x,y:g.$gamePlayer.y};
            }
            fail("不支持的操作。");
        }
        return {command:command,disable:disable,tick:tick};
    }
    if(typeof window === "undefined") { if(typeof module !== "undefined")module.exports=createAdapter;return; }
    try {
        if(window.top!==window)return;
        var nodeRequire=typeof require === "function"?require:window.nw && window.nw.require;
        if(!nodeRequire)return;
        var process=nodeRequire("process"),env=process.env,pipe=env.FUSION_RPG_PIPE,secret=env.FUSION_RPG_SECRET;
        function log(message){try{if(env.FUSION_RPG_LOG)nodeRequire("fs").appendFileSync(env.FUSION_RPG_LOG,new Date().toISOString()+" "+message+"\n");}catch(_){}}
        log("bridge loaded");
        if(!/^fusion-rpg-[a-f0-9]{32}$/.test(pipe || "") || !/^[a-f0-9]{64}$/.test(secret || ""))return;
        var adapter=createAdapter(window,{readMap:function(id){
            var path=nodeRequire("path"),fs=nodeRequire("fs"),root=env.FUSION_RPG_DATA_ROOT;
            if(typeof root!=="string"||!path.isAbsolute(root))throw new Error("游戏数据目录未确认，请从 Fusion 重新启动游戏。");
            var file=path.join(root,"data","Map"+String(id).padStart(3,"0")+".json");
            if(fs.statSync(file).size>32*1024*1024)throw new Error("地图文件过大。");return JSON.parse(fs.readFileSync(file,"utf8").replace(/^\uFEFF/,""));
        }}),translation=createFusionRpgTranslation(window),socket=nodeRequire("net").connect("\\\\.\\pipe\\"+pipe),buffer="";
        socket.setEncoding("utf8");
        socket.on("connect",function(){log("pipe connected");socket.write(JSON.stringify({hello:1,secret:secret,pid:process.pid})+"\n");});
        socket.on("data",function(chunk){
            buffer+=chunk;if(buffer.length>524288){socket.destroy();return;}
            var end;while((end=buffer.indexOf("\n"))>=0){var line=buffer.slice(0,end);buffer=buffer.slice(end+1);var request;
                try{request=JSON.parse(line);if(request.op==="snapshot")request.lookup=translation.lookup;var result=request.op.indexOf("translation")==0?translation.command(request):adapter.command(request);if(request.op==="snapshot"||request.op==="describe")result.rows.forEach(function(row){row.chinese=translation.lookup(row.name);translation.capture(row.name,null,false);if(row.description){row.descriptionChinese=translation.lookup(row.description);translation.capture(row.description,null,false);}});if(request.op==="maps")result.maps.forEach(function(m){m.chinese=translation.lookup(m.name);translation.capture(m.name,null,false);});if(request.op==="actorDetails")["skills","states"].forEach(function(kind){result[kind].forEach(function(e){e.chinese=translation.lookup(e.name);translation.capture(e.name,null,false);});});socket.write(JSON.stringify({id:request.id,ok:true,result:result})+"\n");}
                catch(error){socket.write(JSON.stringify({id:request && request.id || 0,ok:false,error:String(error.message || error).slice(0,300)})+"\n");}
            }
        });
        socket.on("error",function(error){log("pipe error: "+String(error.code||"unknown"));adapter.disable();translation.disable();});socket.on("close",function(){log("pipe closed");adapter.disable();translation.disable();});
        window.addEventListener("beforeunload",function(){adapter.disable();translation.disable();socket.destroy();});
    } catch (_) { /* Unsupported Node context leaves the game unchanged. */ }
}());
