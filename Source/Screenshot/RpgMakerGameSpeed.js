/* MV/MZ scene logic steps only. Never changes browser clocks, rAF, rendering or audio. */
function createFusionRpgSpeed(g,config){
    'use strict';
    config=config||{};
    var backend='rpg-scene-steps',owner={},key='__fusionRpgSpeedOwner',hooks=[],manager=null,wrapper=null,activation=null;
    var enabled=false,closed=false,rate=1,lease=0,extraDepth=0,reason='',lastSteps=0,totalSteps=0,budgetLimited=false,sceneBoundary=false;
    var now=g.performance&&typeof g.performance.now==='function'?g.performance.now.bind(g.performance):Date.now;
    function supported(){return !!(g.Utils&&/^(MV|MZ)$/.test(g.Utils.RPGMAKER_NAME)&&g.SceneManager&&typeof g.SceneManager.updateScene==='function');}
    function status(){return {supported:supported()&&!closed,enabled:enabled,speed:rate,minimum:1,maximum:100,integerOnly:true,backend:backend,nativeEngine:true,fallbackSafe:false,evidence:totalSteps?'scene-update-steps':'none',lastSteps:lastSteps,totalSteps:totalSteps,budgetLimited:budgetLimited,sceneBoundary:sceneBoundary,reason:reason};}
    function error(message){return {error:message,supported:supported(),enabled:enabled,speed:rate,minimum:1,maximum:100,integerOnly:true,backend:backend,fallbackSafe:false};}
    function restore(){
        enabled=false;rate=1;extraDepth=0;activation=null;
        for(var i=hooks.length-1;i>=0;i--){var h=hooks[i];try{if(h.object[h.name]===h.next)h.object[h.name]=h.old;}catch(_){}}
        hooks=[];manager=null;wrapper=null;try{if(g[key]===owner)delete g[key];}catch(_){}
    }
    function tick(){
        if(enabled&&config.leased!==false&&now()>=lease){restore();reason='速度连接已超时，已恢复普通逻辑步进，请重新开启修改。';}
        if(enabled&&(g[key]!==owner||g.SceneManager!==manager||manager.updateScene!==wrapper)){restore();reason='游戏运行时已变化，请重新开启修改。';}
    }
    function wrap(object,name,next){var old=object[name];hooks.push({object:object,name:name,old:old,next:next});object[name]=next;if(object[name]!==next)throw Error('游戏未接受场景逻辑变速挂钩。');}
    function playable(sm,scene){
        return !!scene&&((g.Scene_Map&&scene instanceof g.Scene_Map)||(g.Scene_Battle&&scene instanceof g.Scene_Battle))&&
            (typeof scene.isActive!=='function'||scene.isActive())&&(typeof scene.isStarted!=='function'||scene.isStarted())&&(typeof sm.isGameActive!=='function'||sm.isGameActive())&&!sm._nextScene&&(typeof sm.isSceneChanging!=='function'||!sm.isSceneChanging());
    }
    function enable(){
        tick();if(closed)return error('游戏速度连接已关闭。');
        if(!supported())return error('当前游戏尚未提供 MV/MZ 场景逻辑变速，请进入游戏后重试。');
        if(g[key]&&g[key]!==owner)return error('此游戏已有逻辑变速控制，未叠加第二份加速。');
        lease=now()+15000;if(enabled)return status();
        manager=g.SceneManager;var old=manager.updateScene,token={};activation=token;
        wrapper=function(){
            // A plugin can retain a wrapper after disable. It must never revive on the next grant.
            if(activation!==token||extraDepth>0)return old.apply(this,arguments);
            tick();lastSteps=0;budgetLimited=false;sceneBoundary=false;
            var scene=this._scene,eligible=enabled&&this===manager&&g[key]===owner&&playable(this,scene),started=now(),result;
            result=old.apply(this,arguments);lastSteps=1;totalSteps++;
            if(!eligible||rate===1||config.shouldAccelerate&& !config.shouldAccelerate())return result;
            for(var step=1;step<rate;step++){
                if(!enabled||this._scene!==scene||!playable(this,scene)){sceneBoundary=true;break;}
                if(now()-started>=8){budgetLimited=true;break;}
                // Input edge events belong to the browser's one input update. Held input remains visible.
                var depth=extraDepth;extraDepth=depth+1;try{result=old.apply(this,arguments);}finally{extraDepth=depth;}
                lastSteps++;totalSteps++;
            }
            return result;
        };
        try{
            g[key]=owner;if(g[key]!==owner)throw Error('无法取得场景逻辑变速所有权。');
            wrap(manager,'updateScene',wrapper);
            [g.Input,g.TouchInput].forEach(function(input){if(!input)return;['isTriggered','isRepeated','isReleased','isCancelled'].forEach(function(name){
                if(typeof input[name]!=='function')return;var previous=input[name];wrap(input,name,function(){return activation===token&&extraDepth>0?false:previous.apply(this,arguments);});
            });});
            enabled=true;reason='';return status();
        }catch(e){restore();return error(String(e.message||e));}
    }
    function command(q){
        tick();if(!q||typeof q.op!=='string')return error('无效的场景变速请求。');
        if(q.op==='speedStatus')return status();
        if(q.op==='speedDisable'){restore();reason='';return status();}
        if(q.op==='speedEnable')return enable();
        if(q.op!=='gameSpeed')return error('不支持的场景变速请求。');
        if(!enabled)return error(reason||'请先开启修改。');
        if(Object.prototype.hasOwnProperty.call(q,'rate')){
            var next=q.rate;if(typeof next!=='number'||!isFinite(next)||Math.floor(next)!==next||next<1||next>100)return error('MV/MZ 游戏速度须为 1 到 100 的整数。');
            rate=next;
        }
        lease=now()+15000;return status();
    }
    return {install:function(){},tick:tick,command:command,stop:function(){restore();closed=true;},status:status};
}
