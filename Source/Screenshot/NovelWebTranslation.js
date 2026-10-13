/* Fusion desktop narrative adapters. Engine entry points are traced in the R2/R3
 * integration evidence. This is Fusion's bounded asynchronous implementation;
 * no MTool loader, evaluator, server or native payload is executed. */
function createFusionNovelTranslation(g, engine) {
    'use strict';
    let enabled=false, epoch=0, cache=new Map(), outputs=new Set(), pending=new Set(), queue=[], retry=new Map();
    const hooks=[], nodes=new Map(), attributes=new Map(), dirty=new Set(), settling=new Set();
    let observer=null, observedBody=null, installed=false, applied=0, walk=null, scanAgain=false;
    let watchedFonts=null;
    const key=s=>s.replace(/\r\n?/g,'\n');
    const valid=s=>typeof s==='string'&&s.trim().length>0&&s.length<=6000&&!s.includes('\0')&&/[A-Za-z\u00c0-\u2fff\u3040-\u9fff\uac00-\ud7af]/.test(s);
    const tokens=s=>s.match(/<\/?[A-Za-z][^>\r\n]{0,160}>|\[(?:[^\]\r\n]{1,160})\]|\{[^{}\r\n]{1,160}\}|%(?:\([A-Za-z_][A-Za-z_0-9]*\))?[sdif]|\\[A-Za-z]+(?:\[[^\]\r\n]*\])?/g)||[];
    function compatible(source,text){
        if(typeof text!=='string'||!text.trim()||text.length>12000||text.includes('\0')||text.includes('⟦RPG'))return false;
        const a=tokens(source),b=tokens(text);return a.length===b.length&&a.every((v,i)=>v===b[i]);
    }
    function text(source){
        if(!enabled||!valid(source))return source;
        const normalized=key(source);if(cache.has(normalized))return cache.get(normalized);
        // A measurement wrapper can call a drawing wrapper with our result.
        // Never enqueue that translated output as a new source sentence.
        if(outputs.has(normalized))return source;
        if(!pending.has(normalized)&&pending.size<4096&&(!retry.has(normalized)||retry.get(normalized)<Date.now())){pending.add(normalized);queue.push(normalized);}
        return source;
    }
    function wrap(owner,name,make){
        if(!owner||typeof owner[name]!=='function')return false;
        const previous=hooks.findIndex(h=>h.owner===owner&&h.name===name);
        if(previous>=0){const old=hooks[previous];if(owner[name]===old.replacement)return false;old.active=false;hooks.splice(previous,1);}
        const original=owner[name],wrapped=make(original),h={owner,name,original,active:true};
        h.replacement=function(){return (h.active&&enabled?wrapped:original).apply(this,arguments);};
        owner[name]=h.replacement;hooks.push(h);return true;
    }
    function argument(index,field){return original=>function(){
        const args=Array.prototype.slice.call(arguments),value=args[index];
        if(field&&value&&typeof value==='object'){
            const changed=text(value[field]);
            // Never put translated data into the engine's shared script/save object.
            if(changed!==value[field])args[index]=Object.assign(Object.create(Object.getPrototypeOf(value)),value,{[field]:changed});
        }else if(!field)args[index]=text(value);
        return original.apply(this,args);
    };}
    function tyrano(){
        const root=g.tyrano&&g.tyrano.plugin&&g.tyrano.plugin.kag;
        const live=g.TYRANO&&g.TYRANO.kag;
        for(const kag of [root,live]){
            if(!kag)continue;
            const tags=kag.ftag&&kag.ftag.master_tag;
            for(const tag of [kag.tag&&kag.tag.text,tags&&tags.text])wrap(tag,'showMessage',original=>function(){
                const stat=this.kag&&this.kag.stat||kag.stat||{};
                if(stat.is_script||stat.is_html)return original.apply(this,arguments);
                const args=Array.prototype.slice.call(arguments);args[0]=text(args[0]);return original.apply(this,args);
            });
            for(const name of ['mtext','ptext','glink']){
                const tag=tags&&tags[name]||kag.tag&&kag.tag[name];
                wrap(tag,'start',original=>function(pm){
                    if(!pm||typeof pm!=='object')return original.apply(this,arguments);
                    const args=Array.prototype.slice.call(arguments),copy=Object.assign({},pm);
                    for(const field of ['text'])if(typeof copy[field]==='string')copy[field]=text(copy[field]);
                    args[0]=copy;return original.apply(this,args);
                });
            }
        }
        return hooks.length>0;
    }
    function vnmaker(){
        const p=g.Component_TextRenderer&&g.Component_TextRenderer.prototype;
        const m=g.Component_MessageTextRenderer&&g.Component_MessageTextRenderer.prototype;
        wrap(p,'measureText',argument(0));wrap(p,'drawText',argument(4));
        wrap(p,'drawFormattedText',argument(4));wrap(m,'drawFormattedText',argument(4,'defaultText'));
        return hooks.length>0;
    }
    function eligibleElement(element){
        if(!element||element.isConnected===false)return false;
        for(let p=element;p;p=p.parentElement){
            if(/^(SCRIPT|STYLE|NOSCRIPT|INPUT|TEXTAREA|SELECT|OPTION|CODE|PRE|KBD|SAMP|SVG|CANVAS)$/.test(p.tagName)||p.isContentEditable)return false;
            if(p.getAttribute&&/^(textbox|combobox)$/.test(p.getAttribute('role')||''))return false;
            if(p.getAttribute&&((p.getAttribute('translate')||'').toLowerCase()==='no'||p.getAttribute('data-fusion-original')==='true'))return false;
        }
        return true;
    }
    function inputGlyph(node){
        const s=(node.nodeValue||'').trim(),parent=node.parentElement;
        // Tyrano may render each glyph in its own animated span. showMessage
        // already supplies the complete sentence; these spans are not labels.
        if(engine==='tyrano'&&s.length===1&&parent&&parent.closest&&parent.closest('.current_span')
            &&hooks.some(h=>h.active&&h.name==='showMessage'))return true;
        if(!/^[A-Za-z0-9]$/.test(s))return false;
        for(let p=node.parentElement;p;p=p.parentElement){
            if(!p.getAttribute)continue;
            const keys=p.getAttribute('aria-keyshortcuts')||p.getAttribute('data-key')||p.getAttribute('data-keycode')||'';
            if(keys.split(/[+\s]+/).some(k=>k.toLowerCase()===s.toLowerCase()))return true;
            if(p.tagName==='BUTTON'&&p.getAttribute('value')===s)return true;
        }
        return false;
    }
    const eligible=node=>node&&node.nodeType===3&&eligibleElement(node.parentElement)&&!inputGlyph(node);
    function engineRevealing(node){
        const kag=g.TYRANO&&g.TYRANO.kag,stat=kag&&kag.stat,p=node.parentElement;
        return engine==='tyrano'&&stat&&stat.is_adding_text&&p&&p.closest&&p.closest('.current_span');
    }
    function restoreAttributes(element,records){
        for(const [name,r] of records)if(element.getAttribute(name)===r.shown&&r.shown!==r.source)element.setAttribute(name,r.source);
    }
    function applyAttributes(element){
        let records=attributes.get(element);
        if(!eligibleElement(element)){if(records)restoreAttributes(element,records);attributes.delete(element);return;}
        if(!records)records=new Map();
        // Only display/accessibility labels. Values, IDs, keys and actions remain engine-owned.
        for(const name of ['title','aria-label']){
            const actual=element.getAttribute(name);let r=records.get(name);
            if(!valid(actual)){records.delete(name);continue;}
            if(!r||actual!==r.shown){r={source:actual,shown:actual};records.set(name,r);}
            const wanted=text(r.source);if(wanted!==actual){element.setAttribute(name,wanted);r.shown=wanted;applied++;}
        }
        if(records.size)attributes.set(element,records);else attributes.delete(element);
    }
    function applyNode(node){
        if(node.nodeType===1){applyAttributes(node);return;}
        let r=nodes.get(node);
        if(!eligible(node)){
            if(r&&node.nodeValue===r.shown&&r.shown!==r.source)node.nodeValue=r.source;
            nodes.delete(node);settling.delete(node);return;
        }
        const actual=node.nodeValue,now=Date.now();
        if(!r||actual!==r.shown){
            // Successive prefixes belong to one reveal, not separate sentences.
            // Do not replace them with cached partial translations mid-animation.
            const growing=r&&r.source&&actual.length>r.source.length&&actual.startsWith(r.source);
            const wait=growing||/^[A-Za-z]$/.test(actual.trim())?now+240:0;
            r={source:actual,shown:actual,until:wait};
            if(valid(actual))nodes.set(node,r);else{nodes.delete(node);settling.delete(node);return;}
        }
        if(engineRevealing(node)){r.until=now+240;settling.add(node);return;}
        if(r.until>now){settling.add(node);return;}settling.delete(node);
        const wanted=text(r.source);
        if(wanted!==actual){node.nodeValue=wanted;r.shown=wanted;applied++;}
    }
    function requestScan(){if(!enabled)return;if(walk)scanAgain=true;else if(g.document&&g.document.body){dirty.add(g.document.body);walk=g.document.createTreeWalker(g.document.body,5);}}
    function dom(){
        if(!g.document||!g.document.body||!g.MutationObserver)return false;
        if(observedBody!==g.document.body){
            if(observer)observer.disconnect();walk=null;scanAgain=false;observedBody=g.document.body;
            observer=new g.MutationObserver(changes=>{
                for(const c of changes){
                    if(c.type==='characterData'||c.type==='attributes')dirty.add(c.target);
                    if(c.type==='attributes'&&['translate','data-fusion-original','contenteditable','role','aria-keyshortcuts','data-key','data-keycode','value'].includes(c.attributeName))requestScan();
                    else if(c.type==='childList')requestScan();
                    if(dirty.size>4096){dirty.clear();requestScan();break;}
                }
            });
            observer.observe(g.document.body,{subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:['title','aria-label','translate','data-fusion-original','contenteditable','role','aria-keyshortcuts','data-key','data-keycode','value']});requestScan();
        }
        if(!watchedFonts&&g.document.fonts&&g.document.fonts.addEventListener){
            watchedFonts=g.document.fonts;watchedFonts.addEventListener('loadingdone',refresh);
            const generation=epoch;if(watchedFonts.ready)watchedFonts.ready.then(()=>{if(enabled&&epoch===generation)refresh();},()=>{});
        }
        return true;
    }
    function refresh(){
        for(const node of nodes.keys())dirty.add(node);
        for(const node of attributes.keys())dirty.add(node);
        if(engine==='choicescript'||engine==='tyrano')requestScan();
    }
    function pump(){
        if(!enabled)return;
        for(const node of settling){const r=nodes.get(node);if(!r||node.isConnected===false){settling.delete(node);continue;}if(r.until<=Date.now())dirty.add(node);}
        let count=0;
        for(const node of dirty){dirty.delete(node);applyNode(node);if(++count>=256)break;}
        while(walk&&count++<512){const node=walk.nextNode();if(!node){walk=null;if(scanAgain){scanAgain=false;requestScan();}break;}applyNode(node);}
        // Detached scenes must not keep a strong reference or consume the next page's budget.
        for(const [node,r] of nodes)if(node.isConnected===false){if(node.nodeValue===r.shown)node.nodeValue=r.source;nodes.delete(node);settling.delete(node);}
        for(const [node,records] of attributes)if(node.isConnected===false){restoreAttributes(node,records);attributes.delete(node);}
    }
    function install(){
        if(!enabled)return false;
        const hooked=engine==='vnmaker'?vnmaker():engine==='tyrano'?tyrano():false;
        const page=(engine==='choicescript'||engine==='tyrano')&&dom();installed=!!(hooked||page);pump();return installed;
    }
    function restore(){
        enabled=false;
        if(watchedFonts)watchedFonts.removeEventListener('loadingdone',refresh);watchedFonts=null;settling.clear();
        if(observer)observer.disconnect();observer=null;observedBody=null;walk=null;scanAgain=false;dirty.clear();installed=false;
        for(const [node,r] of nodes)if(node.nodeValue===r.shown&&r.source!==r.shown){node.nodeValue=r.source;r.shown=r.source;}
        for(const [node,records] of attributes)restoreAttributes(node,records);
        nodes.clear();attributes.clear();
        for(const h of hooks){h.active=false;if(h.owner[h.name]===h.replacement)h.owner[h.name]=h.original;}
        hooks.length=0;
    }
    const status=()=>({enabled,epoch,engine,hooked:installed,live:nodes.size+attributes.size,applied,pending:pending.size});
    function command(q){
        if(q.op==='translationPrepare'){restore();epoch++;cache=new Map();outputs.clear();pending.clear();queue=[];retry.clear();refresh();return status();}
        if(q.op==='translationEnable'){if(!q.prepared){restore();epoch++;cache.clear();outputs.clear();pending.clear();queue=[];retry.clear();}enabled=true;install();refresh();pump();return status();}
        if(q.op==='translationDisable'){restore();epoch++;pending.clear();queue=[];return status();}
        if(q.op==='translationPoll'){install();return Object.assign(status(),{texts:enabled?queue.splice(0,32):[]});}
        if(q.op==='translationRetry'){if(q.epoch===epoch&&typeof q.source==='string'){pending.delete(key(q.source));retry.set(key(q.source),Date.now()+30000);if(retry.size>4096)retry.delete(retry.keys().next().value);}return {accepted:0};}
        if(q.op==='translationApply'){
            let accepted=0;
            if((enabled||q.prime)&&q.epoch===epoch)for(const e of (Array.isArray(q.entries)?q.entries:[]).slice(0,256)){
                if(!e||!valid(e.source)||!compatible(e.source,e.text)||(cache.size>=150000&&!cache.has(key(e.source))))continue;
                cache.set(key(e.source),key(e.text));outputs.add(key(e.text));pending.delete(key(e.source));retry.delete(key(e.source));accepted++;
            }
            queue=queue.filter(s=>!cache.has(s));if(accepted){refresh();pump();}return {accepted};
        }
        if(q.op==='translationStatus')return status();throw Error('Unsupported narrative translation request');
    }
    return {command,ensureInstalled:install,capture:text};
}
if(typeof module==='object'&&module.exports)module.exports=createFusionNovelTranslation;
