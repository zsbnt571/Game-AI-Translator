/* Optional native-speaker lane. Ordinary source caches never receive its results. */
function createFusionCharacterRuntime(refresh) {
    "use strict";
    var contract="fusion-character-runtime-1",enabled=false,serial=0,rows=Object.create(null),surfaces=Object.create(null),limit=32;
    function reset(on){enabled=!!on;rows=Object.create(null);surfaces=Object.create(null);}
    function prepare(q){reset(q&&q.characterRuntimeContract===contract);return enabled?contract:null;}
    function validId(id){return typeof id==='string'&&/^(?:renpy|mv|mz|rgss|wolf|tyrano|naninovel|unityds|vnmaker):[A-Za-z_0-9][A-Za-z_0-9.:\/-]{0,140}$/.test(id);}
    function release(surface){var key=surfaces[surface];if(key){delete rows[key];delete surfaces[surface];}}
    function render(surface,source,identity,ordinary,capture) {
        if(!enabled||!validId(identity&&identity.id)||typeof source!=='string'||!source||source.length>6000||source.indexOf('\0')>=0){release(surface);return capture();}
        var id=surfaces[surface],row=id&&rows[id];
        if(!row||row.source!==source||row.characterId!==identity.id){
            release(surface);if(Object.keys(rows).length>=limit)return capture();
            id='c'+(++serial);row={requestId:id,source:source,characterId:identity.id,displayName:String(identity.name||identity.id).slice(0,200),evidence:[],text:null,fallback:false,deadline:Date.now()+3000};
            if(Array.isArray(identity.evidence))row.evidence=identity.evidence.filter(function(x){return typeof x==='string'&&x.length<=600;}).slice(-3);
            rows[id]=row;surfaces[surface]=id;
        }
        if(row.text!==null)return row.text;
        var prior=ordinary();if(prior!==undefined&&prior!==null)return prior;
        return row.fallback||Date.now()>row.deadline?capture():source;
    }
    function poll(q,compatible) {
        var changed=false,accepted=[];
        if(enabled&&q.characterRuntimeContract===contract&&Array.isArray(q.characterResults))q.characterResults.slice(0,16).forEach(function(result){
            var row=result&&rows[result.requestId];
            if(!row||row.source!==result.source||row.characterId!==result.characterId)return;
            if(result.pending===true){row.deadline=Date.now()+3000;accepted.push(row.requestId);return;}
            if(result.fallback===true){if(row.text!==null||!row.fallback)changed=true;row.text=null;row.fallback=true;accepted.push(row.requestId);}
            else if(typeof result.text==='string'&&compatible(row.source,result.text)){
                if(row.text!==result.text)changed=true;row.text=result.text;row.fallback=false;accepted.push(row.requestId);
            }
        });
        if(changed&&typeof refresh==='function')refresh();
        return {characterRuntimeContract:enabled?contract:null,characterAccepted:accepted,characterRequests:enabled?Object.keys(rows).slice(0,16).map(function(id){var r=rows[id];return {requestId:r.requestId,source:r.source,characterId:r.characterId,displayName:r.displayName,evidence:r.evidence};}):[]};
    }
    function isOutput(value){return Object.keys(rows).some(function(id){return rows[id].text===value;});}
    return {prepare:prepare,reset:reset,render:render,release:release,poll:poll,contract:contract,isOutput:isOutput,active:function(){return enabled;}};
}
if(typeof window==='undefined'&&typeof module!=='undefined')module.exports=createFusionCharacterRuntime;
