/* Construct's documented script runtime only. No DOM-to-worker guessing or global clock hooks. */
function createFusionConstructSpeed(g){
    'use strict';
    const backend='construct-time-scale',leaseMs=15000;
    const now=g.performance&&typeof g.performance.now==='function'?g.performance.now.bind(g.performance):Date.now;
    let runtime=null,registered=false,closed=false,authorized=false,owned=false,baseline=null,applied=1,rate=1,lease=0,pending=null;
    let current=null,state='idle',blocked=false;
    let reason='尚未取得 Construct 运行时；当前线程或导出版本可能不支持引擎倍率。';
    const error=message=>({error:message,backend});
    const unavailable=message=>({error:message,backend,enabled:false,supported:false,fallbackSafe:!runtime&&!authorized&&!owned});
    const same=(a,b)=>Number.isFinite(a)&&Number.isFinite(b)&&((a===0||b===0)?a===b:Math.abs(a-b)<=1e-7*Math.max(Math.abs(a),Math.abs(b)));
    function read(){const value=runtime.timeScale;if(typeof value!=='number'||!Number.isFinite(value)||value<0)throw Error('游戏返回了无效的时间倍率。');return value;}
    function failed(e){blocked=true;state='unavailable';current=null;reason=String(e.message||e);}
    function restore(){
        // Cancel the request before examining ownership: reset never briefly reapplies a pending rate.
        rate=1;blocked=false;state='idle';reason='';
        try{
            if(owned){current=read();if(same(current,applied)){runtime.timeScale=baseline;current=read();if(!same(current,baseline))throw Error('游戏未确认恢复原来的时间倍率。');}}else current=null;
            owned=false;baseline=null;
        }catch(e){failed(e);}
    }
    function observe(explicit){
        if(!authorized||rate===1||(blocked&&!explicit))return;
        try{
            current=read();
            if(current===0){owned=false;state='paused';reason='';return;}
            if(owned&&!same(current,applied))owned=false;
            if(!owned){
                if(explicit||baseline===null)baseline=current;
                else if(!same(current,baseline)){state='game-controlled';reason='';return;}
            }
            const wanted=baseline*rate;
            if(!Number.isFinite(wanted)||wanted<=0)throw Error('请求的引擎时间倍率超出范围。');
            if(owned&&same(current,wanted)){state='active';reason='';return;}
            const previous=current,previousOwned=owned;
            let actual,accepted=false,threw=false;
            try{runtime.timeScale=wanted;actual=read();accepted=same(actual,wanted);}catch(_){threw=true;}
            if(!accepted){
                if(!threw&&actual===0){current=0;owned=false;state='paused';reason='';return;}
                // Roll back this synchronous attempt only, then require an explicit user retry.
                let restored=false;
                try{current=read();if(current>0&&!same(current,previous)){runtime.timeScale=previous;current=read();}restored=same(current,previous);}catch(_){}
                owned=previousOwned&&restored;
                throw Error(restored?'引擎未接受此倍率，已恢复修改前的设置。':'引擎未确认倍率，也未确认恢复，请检查游戏状态。');
            }
            current=actual;applied=actual;owned=true;state='active';reason='';
        }catch(e){failed(e);}
    }
    function status(){
        const result={supported:!!runtime&&!closed,enabled:authorized,speed:rate,speedState:state,speedApplied:state==='active'&&owned,backend,evidence:state==='active'&&owned?'engine-property-readback':'none',reason:reason||''};
        if(Number.isFinite(current)&&current>=0)result.engineScale=current;
        if(baseline!==null){result.baselineScale=baseline;result.baseline=baseline;}
        return result;
    }
    function enableReady(){authorized=true;lease=now()+leaseMs;if(!blocked)reason='';return {...status(),nativeEngine:true};}
    function finishEnable(result){if(pending){const resolve=pending.resolve;pending=null;resolve(result);}}
    function install(){
        if(closed||registered||!g.document||g.__fusionWebEngine!=='construct3'||typeof g.runOnStartup!=='function')return;
        registered=true;
        try{g.runOnStartup(value=>{
            if(closed)return;
            // The public callback is the source of identity; runtime is never taken from a guessed global.
            if(!value||value.isInWorker!==false||!('timeScale' in value)){
                reason='当前 Construct 运行时不在已连接的页面线程，引擎倍率不可用。';finishEnable(unavailable(reason));return;
            }
            runtime=value;reason='';if(pending)finishEnable(enableReady());
        });}catch(_){reason='此 Construct 导出版本未提供可用的启动回调。';finishEnable(unavailable(reason));}
    }
    function tick(observeClock=true){
        install();const time=now();
        if(pending&&time>=pending.deadline)finishEnable(unavailable(reason||'未在启动阶段取得 Construct 运行时，请保存后重新启动游戏。'));
        if(authorized&&time>=lease){
            restore();authorized=false;if(!blocked)reason='速度连接已超时，已请求恢复修改前的倍率，请重新开启修改。';
        }
        if(observeClock)observe(false);
    }
    function stop(){closed=true;authorized=false;finishEnable(error('游戏速度连接已关闭。'));restore();if(blocked)throw Error(reason);}
    function command(q){
        tick(false);
        if(q.op==='speedStatus'){observe(false);return status();}
        if(q.op==='speedDisable'){authorized=false;finishEnable(error('修改已关闭。'));restore();return {...status(),enabled:false,speed:1};}
        if(closed)return error('游戏速度连接已关闭。');
        if(q.op==='speedEnable'){
            if(runtime)return enableReady();
            if(!g.document||g.__fusionWebEngine!=='construct3')return unavailable('当前线程没有可用的 Construct 引擎倍率。');
            if(pending)return error('正在等待 Construct 运行时。');
            return new Promise(resolve=>{pending={resolve,deadline:now()+8000};install();});
        }
        if(q.op!=='gameSpeed')return error('不支持的游戏速度请求。');
        if(!authorized)return error(reason||'请先开启修改。');
        lease=now()+leaseMs;
        if(Object.prototype.hasOwnProperty.call(q,'rate')){
            const next=q.rate;
            if(typeof next!=='number'||!Number.isFinite(next)||next<.25||next>100)return error('倍率必须在 0.25 到 100 之间。');
            if(next===1)restore();
            else{rate=next;blocked=false;observe(true);}
        }else observe(false);
        return {...status(),nativeEngine:true};
    }
    return {install,tick,command,stop};
}
