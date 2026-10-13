/* Known engine variable roots only. No eval, arbitrary scripts or official save writes. */
function createFusionNovelData(g,engine){
    'use strict';
    let enabled=false,suspended=false,root=null,generation=0,entries=new Map(),locks=new Map(),history=[],lastTick=0,serial=0;
    const scalar=v=>typeof v==='boolean'||typeof v==='number'&&Number.isFinite(v)||typeof v==='string'&&v.length<=2000&&!v.includes('\0');
    const safe=k=>k!=='__proto__'&&k!=='constructor'&&k!=='prototype';
    let choiceRoot=null,choiceScene=null;
    function current(){
        if(engine==='choicescript'){
            const stats=g.stats,scene=stats&&stats.scene;
            // Scene owns both namespaces. Never expose the interpreter, line counters,
            // navigation targets or reserved choice_* bookkeeping as editable fields.
            if(!stats||!scene||scene.stats!==stats||!scene.temps||typeof scene.temps!=='object')return null;
            if(!choiceRoot||choiceRoot.stats!==stats||choiceRoot.temps!==scene.temps||choiceScene!==scene){
                choiceRoot={stats,temps:scene.temps};choiceScene=scene;
            }
            return choiceRoot;
        }
        return engine==='tyrano'?g.TYRANO&&g.TYRANO.kag&&g.TYRANO.kag.stat&&g.TYRANO.kag.stat.f:engine==='vnmaker'?g.GameManager&&g.GameManager.variableStore:null;
    }
    function sync(){const next=current();if(next!==root){root=next;generation++;entries.clear();locks.clear();history=[];}return !!root&&typeof root==='object';}
    function resolve(e){
        if(!sync()||e.generation!==generation)throw Error('游戏数据已更换，请刷新。');
        let owner=root;
        for(let i=0;i<e.path.length;i++){
            const k=e.path[i];if(!safe(k)||owner!==e.parents[i])throw Error('数据对象已更换，请刷新。');
            const d=Object.getOwnPropertyDescriptor(owner,k);
            if(!d||!Object.prototype.hasOwnProperty.call(d,'value'))throw Error('此项目已不再是可编辑的数据字段。');
            if(i===e.path.length-1){if(!d.writable||!scalar(d.value))throw Error('此项目现在不可编辑。');return {owner,key:k,value:d.value};}
            owner=d.value;
        }
        throw Error('未选择有效数据。');
    }
    function write(e,value,expected){
        const t=resolve(e);if(t.value!==expected)throw Error('数值已变化，请刷新后重试。');
        if(!scalar(value)||typeof value!==typeof t.value)throw Error('修改后的值类型不符或超过限制。');
        t.owner[t.key]=value;
        if(t.owner[t.key]!==value){t.owner[t.key]=t.value;throw Error('游戏没有接受该值。');}
        return t.value;
    }
    function snapshot(){
        const rows=[];let count=0,characters=0,truncated=false;const seen=new Set();entries.clear();
        function walk(value,path,parents,depth){
            if(!value||typeof value!=='object'||seen.has(value))return;seen.add(value);
            if(depth>6){truncated=true;return;}
            for(const k of Object.keys(value)){
                if(++count>12000||rows.length>=2048){truncated=true;return;}if(!safe(k)||k.startsWith('_fusion'))continue;
                if(engine==='choicescript'&&depth>0&&(!/^[a-z][a-z0-9_]*$/.test(k)||/^choice_|^scene$|^sceneName$|^testEntryPoint$/.test(k)))continue;
                const d=Object.getOwnPropertyDescriptor(value,k);if(!d||!Object.prototype.hasOwnProperty.call(d,'value'))continue;
                const next=path.concat(k),owners=parents.concat(value);if(next.join('.').length>1024){truncated=true;continue;}
                if(scalar(d.value)&&d.writable){
                    const id=String(++serial),e={path:next,parents:owners,generation};
                    const row={id,name:next.join('.'),value:d.value,type:typeof d.value,locked:locks.has(JSON.stringify(next))};
                    const size=JSON.stringify(row).length+1;
                    // Three UTF-8 bytes per UTF-16 unit is an upper bound, including
                    // escaped JSON. Keep the complete transport reply under 1 MiB.
                    if(characters+size>240000){truncated=true;continue;}
                    entries.set(id,e);rows.push(row);characters+=size;
                }else if(d.value&&typeof d.value==='object'&&!ArrayBuffer.isView(d.value)&&(engine!=='choicescript'||depth===0))walk(d.value,next,owners,depth+1);
            }
        }
        if(sync())walk(root,[],[],0);
        return {enabled,ready:!!root&&typeof root==='object',generation,rows,truncated,locks:locks.size};
    }
    function tick(){
        if(!enabled||suspended||Date.now()-lastTick<200)return;lastTick=Date.now();sync();
        for(const [key,l] of locks){try{const t=resolve(l.entry);if(t.value!==l.value)write(l.entry,l.value,t.value);}catch(_){locks.delete(key);}}
    }
    function command(q){
        sync();
        if(q.op==='dataEnable'){if(engine!=='tyrano'&&engine!=='vnmaker'&&engine!=='choicescript')throw Error('此引擎尚未接入变量修改。');enabled=true;return {enabled,ready:!!root&&typeof root==='object',generation};}
        if(q.op==='dataDisable'){enabled=false;locks.clear();entries.clear();history=[];return {enabled:false};}
        if(q.op==='dataStatus')return {enabled,ready:!!root&&typeof root==='object',generation,locks:locks.size};
        if(suspended)throw Error('状态操作进行中，请稍候再修改变量。');
        if(!enabled)throw Error('请先开启修改。');
        if(q.op==='dataSnapshot')return snapshot();
        if(q.op==='dataUndo'){
            const h=history[history.length-1];if(!h)throw Error('没有可撤销的修改。');write(h.entry,h.before,h.after);locks.delete(JSON.stringify(h.entry.path));history.pop();return {restored:true};
        }
        const e=entries.get(String(q.entry));if(!e||q.generation!==generation)throw Error('列表已过期，请刷新。');
        const pathKey=JSON.stringify(e.path);
        if(q.op==='dataUnlock'){locks.delete(pathKey);return {unlocked:true};}
        if(q.op==='dataSet'||q.op==='dataLock'){
            if(q.op==='dataLock'&&!locks.has(pathKey)&&locks.size>=128)throw Error('锁定项目达到上限。');
            const before=write(e,q.value,q.expected);history.push({entry:e,before,after:q.value});if(history.length>64)history.shift();
            if(q.op==='dataLock'||locks.has(pathKey))locks.set(pathKey,{entry:e,value:q.value});return {written:true,value:q.value};
        }
        throw Error('不支持的修改请求。');
    }
    function invalidate(){generation++;entries.clear();locks.clear();history=[];}
    return {command,tick,invalidate,suspend:(value,clear)=>{suspended=value;if(clear){enabled=false;invalidate();}}};
}
if(typeof module==='object'&&module.exports)module.exports=createFusionNovelData;
