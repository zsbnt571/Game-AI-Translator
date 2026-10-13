(()=>{
    'use strict';
    // Only isolated test copies supply this path; normal game profiles stay unchanged.
    const p=process.env.FUSION_WEB_TEST_DATA;if(!p)return;
    const fs=require('fs'),path=require('path'),root=path.dirname(process.execPath);
    try{if(fs.readFileSync(path.join(root,'.fusion-test-copy'),'utf8').trim()!=='FusionTranslationTestCopy/1'||!path.resolve(p).startsWith(root+path.sep))return;}catch(_){return;}
    const electron=require('electron');fs.mkdirSync(p,{recursive:true});
    function diagnostic(name,value){try{fs.writeFileSync(path.join(p,name+'.json'),JSON.stringify(value));}catch(_){}}
    diagnostic('runtime',{versions:process.versions,hidden:process.env.FUSION_WEB_TEST_HIDDEN==='1'});
    process.on('uncaughtException',error=>{diagnostic('main-error',{error:String(error&&error.stack||error)});electron.app.exit(1);});
    electron.app.setPath('userData',p);
    // sessionData was introduced after the older Tyrano Electron releases.
    try{electron.app.setPath('sessionData',p);}catch(_){}
    electron.app.commandLine.appendSwitch('mute-audio');
    electron.shell.beep=function(){};
    electron.app.on('web-contents-created',(_,contents)=>{
        contents.setAudioMuted(true);
        contents.on('console-message',(_,level,message)=>{if(level>=2)diagnostic('renderer-error',{message:String(message).slice(0,4000)});});
        contents.on('did-fail-load',(_,code,description)=>diagnostic('load-error',{code,description}));
    });
    if(process.env.FUSION_WEB_TEST_HIDDEN==='1'){
        for(const name of ['openExternal','openPath','openItem','showItemInFolder'])if(typeof electron.shell[name]==='function')electron.shell[name]=function(){throw Error('Background test stopped before an external helper launch.');};
        const child=require('child_process');
        for(const name of ['spawn','spawnSync','exec','execSync','execFile','execFileSync','fork'])child[name]=function(){throw Error('Background test stopped before a game helper launch.');};
        electron.BrowserWindow.prototype.show=function(){};
        electron.BrowserWindow.prototype.focus=function(){};
        electron.BrowserWindow.prototype.restore=function(){};
    }
})();
