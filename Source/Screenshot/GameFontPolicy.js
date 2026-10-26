/* Explicit game font override; a failed or superseded load never replaces it. */
function createFusionGameFontPolicy(g, options, refresh) {
    'use strict';
    var selected='',status='default',env={},generation=0,ownedFace=null;
    try { env=g.process&&g.process.env||(typeof process==='object'&&process.env)||{}; } catch (_) {}
    function changed(){if(typeof refresh==='function')refresh();}
    function configure(q) {
        var mode=q.mode,family=String(q.family||'').trim(),path=String(q.path||'').trim();
        if((mode!=='default'&&mode!=='custom')||family.length>256||/[\x00-\x1f]/.test(family)||path.length>32767||/[\x00-\x1f]/.test(path)||mode==='custom'&&!family&&!path)
            return Promise.reject(Error('Invalid font preference'));
        var ticket=++generation,previousStatus=status;
        function commit(name,face){
            if(ticket!==generation)throw Error('Font selection was superseded');
            var oldFace=ownedFace,oldName=selected,oldStatus=previousStatus;
            try {
                if(face)g.document.fonts.add(face);
                ownedFace=face||null;selected=name;status=mode==='default'?'default':'ready';
                changed();
            }
            catch(error) {
                ownedFace=oldFace;selected=oldName;status=oldStatus;
                try { if(face&&g.document.fonts.delete)g.document.fonts.delete(face); } catch (_) {}
                // A renderer may have updated some controls before throwing.
                // Best-effort redraw under the old preference restores those;
                // failure still rejects the request rather than claiming success.
                try { changed(); } catch (_) {}
                throw error;
            }
            try { if(oldFace&&g.document.fonts.delete)g.document.fonts.delete(oldFace); } catch (_) {}
            return {accepted:true,mode:mode,restartRequired:false};
        }
        if(mode==='default')return Promise.resolve(commit('',null));
        if(!path)return Promise.resolve(commit(family,null));
        status='loading';
        try {
            if(!g.FontFace||!g.document||!g.document.fonts)throw Error('Font file loading unavailable');
            var source,fs=null;
            try {var loader=g.require||(typeof require==='function'?require:null);if(loader)fs=loader('fs');}catch(_){}
            if(fs){var size=fs.statSync(path).size;if(size<=0||size>33554432)throw Error('Font file size limit');source=new Uint8Array(fs.readFileSync(path));}
            else {var normalized=path.replace(/\\/g,'/');if(!/^(?:[A-Za-z]:\/|\/)/.test(normalized))throw Error('Font path must be absolute');
                var url=normalized.indexOf('//')===0?'file:'+normalized:'file:///'+normalized;
                source='url('+JSON.stringify(encodeURI(url).replace(/#/g,'%23').replace(/\?/g,'%3F'))+')';}
            g.__fusionFontSequence=(g.__fusionFontSequence||0)+1;
            var name='FusionGameCustomFont'+g.__fusionFontSequence;
            var face=new g.FontFace(name,source);
            return face.load().then(function(loaded){return commit(name,loaded);},function(error){
                if(ticket===generation)status=previousStatus;throw error;
            });
        } catch(error) {if(ticket===generation)status=previousStatus;return Promise.reject(error);}
    }
    options=options||{};
    var family=String(options.customFontFamily||env.FUSION_GAME_FONT_FAMILY||'').trim();
    var path=String(options.customFontPath||env.FUSION_GAME_FONT_PATH||'').trim();
    if(family||path)configure({mode:'custom',family:family,path:path}).catch(function(){if(!selected)status='font-load-failed';});
    return {family:function(){return selected;},status:function(){return status;},configure:configure};
}
if(typeof module==='object'&&module.exports)module.exports=createFusionGameFontPolicy;
