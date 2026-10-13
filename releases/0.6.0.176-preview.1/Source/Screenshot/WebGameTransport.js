/* Local bounded JSON channel. No credentials, arbitrary paths or Node API cross the renderer boundary. */
function createFusionWebTransport(){
    "use strict";
    const env=process.env,dir=env.FUSION_WEB_SESSION,secret=env.FUSION_WEB_SECRET;
    if(!dir||!secret||!/^[A-Fa-f0-9]{64}$/.test(secret))return null;
    const fs=require('fs'),path=require('path');if(!path.isAbsolute(dir))return null;
    function read(name,max){const p=path.join(dir,name);try{const st=fs.lstatSync(p);if(!st.isFile()||st.isSymbolicLink()||st.size>max)return null;return JSON.parse(fs.readFileSync(p,'utf8'));}catch(_){return null;}}
    function write(name,value){const p=path.join(dir,name),t=p+'.tmp';const data=JSON.stringify(value);if(Buffer.byteLength(data)>1048576)throw Error('Reply too large');fs.writeFileSync(t,data,{encoding:'utf8',flag:'w'});fs.renameSync(t,p);}
    let active=true,last=0,pending=null;const seed=read('startup.json',32*1024*1024);
    return {
        startup:()=>JSON.stringify({enabled:env.FUSION_WEB_START==='1',entries:seed&&seed.secret===secret?seed.entries:{}}),
        hello:()=>{write('hello.json',{secret,protocol:131,pid:process.pid});return true;},
        request:()=>{
            if(!active)return '';
            try{if(fs.readFileSync(path.join(dir,'stop'),'utf8')===secret){active=false;return JSON.stringify({op:'translationDisable',id:-1});}}catch(_){}
            if(pending){try{write('response-'+pending.id+'.json',pending);pending=null;}catch(_){return '';}}
            const q=read('request.json',262144);if(!q||q.secret!==secret||!Number.isSafeInteger(q.id)||q.id<=last||!(/^(?:translation(?:Prepare|Enable|Disable|Poll|Retry|Apply|Status)|data(?:Enable|Disable|Status|Snapshot|Set|Lock|Unlock|Undo)|state(?:Status|Capture|Read|Begin|Write|Load|Clear))$/.test(q.op)))return '';
            last=q.id;return JSON.stringify(q);
        },
        reply:(raw)=>{if(typeof raw!=='string'||Buffer.byteLength(raw)>1048576)return false;const r=JSON.parse(raw);if(r.id!==last||!Number.isSafeInteger(r.id))return false;r.secret=secret;pending=r;try{write('response-'+r.id+'.json',r);pending=null;}catch(_){}return true;}
    };
}
