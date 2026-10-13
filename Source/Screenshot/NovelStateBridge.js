/* Engine-owned JSON snapshots. No script evaluation or official save-slot writes. */
function createFusionNovelState(g,engine,data){
    'use strict';
    const limit=4*1024*1024,chunkSize=24576;
    let active=true,busy=false,epoch=0,serial=0,buffer=null;
    function api(){
        if(engine==='tyrano'){
            const kag=g.TYRANO&&g.TYRANO.kag,menu=kag&&kag.menu;
            return menu&&typeof menu.snapSave==='function'&&typeof menu.loadGameData==='function'?{owner:menu,kag}:null;
        }
        const manager=g.GameManager,gs=g.gs;
        return engine==='vnmaker'&&manager&&typeof manager.prepareSaveGame==='function'&&gs&&gs.GameManager===manager&&
            typeof manager.prepareLoadGame==='function'&&gs.Audio&&typeof gs.Audio.reset==='function'&&
            gs.GlobalEventManager&&typeof gs.GlobalEventManager.clear==='function'&&g.vn&&typeof g.vn.Object_Scene==='function'&&
            g.SceneManager&&typeof g.SceneManager.switchTo==='function'&&typeof g.SceneManager.clear==='function'?{owner:manager}:null;
    }
    function clone(value){
        let count=0,characters=0;const ancestors=new Set();
        function budget(length){characters+=length;if(characters>limit)throw Error('状态快照超过大小限制。');}
        function walk(v,depth){
            if(++count>250000||depth>80)throw Error('状态快照超过结构限制。');
            if(typeof v==='string'){budget(v.length);return v;}
            if(v===null||typeof v==='boolean')return v;
            if(typeof v==='number'&&Number.isFinite(v))return v;
            if(!v||typeof v!=='object'||ancestors.has(v))throw Error('状态快照包含无法保存的数据。');
            const array=Array.isArray(v);
            if(array&&(count+=v.length)>250000)throw Error('状态快照超过结构限制。');
            ancestors.add(v);const out=array?new Array(v.length):Object.create(null);
            for(const key of Object.keys(v)){
                budget(key.length);
                if(key==='__proto__'||key==='constructor'||key==='prototype')throw Error('状态快照含不支持的字段。');
                const d=Object.getOwnPropertyDescriptor(v,key);
                if(!d||!Object.prototype.hasOwnProperty.call(d,'value'))throw Error('状态快照包含动态属性。');
                if(d.value===undefined){if(Array.isArray(v))out[key]=null;continue;}
                out[key]=walk(d.value,depth+1);
            }
            ancestors.delete(v);return out;
        }
        if(!value||typeof value!=='object'||Array.isArray(value))throw Error('游戏尚未提供完整状态快照。');
        const result=walk(value,0),raw=JSON.stringify(result);
        if(raw.length<3||raw.length>limit)throw Error('状态快照为空或超过大小限制。');
        return {value:result,raw};
    }
    const status=()=>({available:active&&!!api(),busy,engine,maxCharacters:limit,chunkSize});
    function check(token,owner){if(!active||epoch!==token||!api()||api().owner!==owner)throw Error('游戏连接或场景接口已变化，请重新读取。');}
    function stop(){active=false;epoch++;buffer=null;if(data)data.suspend(false,true);}
    function run(operation){
        if(!active||busy)return Promise.reject(Error('状态操作尚未结束，请稍候。'));
        const a=api();if(!a)return Promise.reject(Error('当前游戏未提供匹配的状态快照接口。'));
        busy=true;const token=++epoch;if(data)data.suspend(true,false);
        const finish=()=>{busy=false;if(data)data.suspend(false,false);};let pending;
        try{pending=operation(a,()=>check(token,a.owner));}catch(error){pending=Promise.reject(error);}
        return Promise.resolve(pending).then(value=>{finish();return value;},error=>{finish();throw error;});
    }
    function staged(q,mode){
        if(!buffer||buffer.mode!==mode||q.transfer!==buffer.id||Date.now()-buffer.at>120000){buffer=null;throw Error('状态传输已过期，请重试。');}
        buffer.at=Date.now();return buffer;
    }
    function command(q){
        if(q.op==='stateStatus')return status();
        if(!active)throw Error('游戏连接已关闭。');
        if(q.op==='stateCapture')return run((a,checkCurrent)=>{
            buffer=null;let capture;
            if(engine==='tyrano'){
                capture=new Promise((resolve,reject)=>{
                    // Keep this operation busy until its actual callback/error. A late callback
                    // cannot be confused with the next capture after the desktop times out.
                    try{const pending=a.owner.snapSave('Fusion 状态快照',resolve,'false');
                        if(pending&&typeof pending.then==='function')pending.catch(reject);
                    }catch(e){reject(e);}
                });
            }else{
                capture=Promise.resolve(a.owner.prepareSaveGame());
            }
            return capture.then(()=>{
                checkCurrent();const result=clone(engine==='tyrano'?a.owner.snap:a.owner.saveGame);
                checkCurrent();buffer={id:String(++serial),mode:'read',raw:result.raw,at:Date.now()};
                return {transfer:buffer.id,length:buffer.raw.length,engine,chunkSize};
            });
        });
        if(busy)throw Error('状态操作尚未结束，请稍候。');
        if(q.op==='stateRead'){
            const b=staged(q,'read');if(!Number.isInteger(q.offset)||q.offset<0||q.offset>=b.raw.length)throw Error('状态读取位置无效。');
            let end=Math.min(b.raw.length,q.offset+chunkSize);
            if(end<b.raw.length&&/[\uD800-\uDBFF]/.test(b.raw[end-1]))end--;
            return {text:b.raw.slice(q.offset,end)};
        }
        if(q.op==='stateBegin'){
            if(!api()||!Number.isInteger(q.length)||q.length<3||q.length>limit)throw Error('状态快照大小或游戏接口不符。');
            buffer={id:String(++serial),mode:'write',length:q.length,raw:'',at:Date.now()};return {transfer:buffer.id};
        }
        if(q.op==='stateWrite'){
            const b=staged(q,'write');
            if(q.offset!==b.raw.length||typeof q.text!=='string'||!q.text.length||q.text.length>chunkSize||b.raw.length+q.text.length>b.length)throw Error('状态传输顺序或长度不符。');
            b.raw+=q.text;return {received:b.raw.length};
        }
        if(q.op==='stateClear'){if(buffer&&q.transfer===buffer.id)buffer=null;return {cleared:true};}
        if(q.op==='stateLoad'){
            const b=staged(q,'write');if(b.raw.length!==b.length)throw Error('状态快照尚未传输完整。');
            const payload=clone(JSON.parse(b.raw)).value;buffer=null;
            return run((a,checkCurrent)=>{
                checkCurrent();if(data)data.invalidate();
                let loaded;
                if(engine==='tyrano')loaded=Promise.resolve(a.owner.loadGameData(payload));
                else{
                    loaded=Promise.resolve(a.owner.prepareLoadGame())
                        .then(()=>{checkCurrent();a.owner.loadedSaveGame=payload;return g.gs.Audio.reset();})
                        .then(()=>{checkCurrent();return g.gs.GlobalEventManager.clear();})
                        .then(()=>{checkCurrent();return g.SceneManager.switchTo(new g.vn.Object_Scene());})
                        .then(()=>{checkCurrent();return g.SceneManager.clear();});
                }
                return loaded.then(()=>{checkCurrent();return {submitted:true,engine,completion:'engine-call-returned'};});
            });
        }
        throw Error('不支持的状态快照请求。');
    }
    return {command,stop};
}
if(typeof module==='object'&&module.exports)module.exports=createFusionNovelState;
