/* Fusion desktop narrative adapters. Engine entry points are traced in the R2/R3
 * integration evidence. This is Fusion's bounded asynchronous implementation;
 * no MTool loader, evaluator, server or native payload is executed. */
function createFusionNovelTranslation(g, engine) {
    'use strict';
    let enabled=false, epoch=0, cache=new Map(), outputs=new Set(), pending=new Set(), queue=[], retry=new Map();
    const hooks=[], nodes=new Map(), attributes=new Map(), dirty=new Set(), settling=new Set();
    let observer=null, observedBody=null, installed=false, applied=0, walk=null, scanAgain=false;
    let watchedFonts=null;
    const fontLeases=new Map();
    let customFont=null;
    let nativeFontRevision=0,nativeFontConstructing=false;
    const nativeFontCopies=new WeakMap(),nativeOwnedFonts=new WeakSet(),nativeTextVersions=new WeakMap();
    const characters=typeof createFusionCharacterRuntime==='function'?createFusionCharacterRuntime(()=>{refresh();pump();}):null;
    let speakers=new WeakMap();const characterNodes=new Map(), characterFacts=new Map();let characterSerial=0;
    let nativeSpeakers=new WeakMap();
    function ordinary(source){return cache.has(key(source))?key(cache.get(key(source))):undefined;}
    function characterText(surface,source,identity){return characters?characters.render(surface,source,identity,()=>ordinary(source),()=>text(source)):text(source);}
    function restoreCharacterNode(element,row){
        if(row.owned&&element.childNodes.length===1&&element.firstChild===row.owned){element.removeChild(row.owned);for(const child of row.children)element.appendChild(child);}
        row.owned=null;row.children=null;
    }
    function refreshCharacters(){
        for(const [element,row] of characterNodes){
            if(element.isConnected===false){characters.release(row.surface);characterNodes.delete(element);continue;}
            const stat=row.kag.stat||{};if(stat.is_adding_text)continue;
            if(row.owned&&(element.childNodes.length!==1||element.firstChild!==row.owned)){characters.release(row.surface);characterNodes.delete(element);continue;}
            if(!row.owned&&element.textContent!==row.source){characters.release(row.surface);characterNodes.delete(element);continue;}
            const wanted=characterText(row.surface,row.source,row.identity);
            if(wanted===row.source){restoreCharacterNode(element,row);continue;}
            if(!row.owned){
                // Only plain glyph containers are replaced. Interactive/ruby/
                // authored markup keeps the existing ordinary display path.
                if(element.querySelector&&element.querySelector('a,button,input,ruby,img,svg,canvas'))continue;
                row.children=Array.from(element.childNodes);for(const child of row.children)element.removeChild(child);
                row.owned=element.ownerDocument.createTextNode(wanted);element.appendChild(row.owned);
            }else row.owned.nodeValue=wanted;
        }
    }
    customFont=typeof createFusionGameFontPolicy==='function'?createFusionGameFontPolicy(g,g.__fusionGameFontOptions,()=>{
        nativeFontRevision++;
        if(!enabled&&!fontIndependent())for(const element of Array.from(fontLeases.keys()))restoreFont(element);
        install();refresh();pump();if(engine==='vnmaker')refreshVnmakerFonts();
    }):null;
    function fontIndependent(){return !!(customFont&&customFont.independent&&customFont.independent());}
    function restoreFont(element){
        const lease=fontLeases.get(element);if(!lease)return;
        if(element.style&&element.style.getPropertyValue('font-family')===lease.owned&&element.style.getPropertyPriority('font-family')==='important'){
            if(lease.original)element.style.setProperty('font-family',lease.original,lease.priority);else element.style.removeProperty('font-family');
        }
        fontLeases.delete(element);
    }
    function applyFont(element,translated){
        const selected=(fontIndependent()||enabled&&translated)&&customFont&&customFont.family();
        if(!element||!element.style)return;
        if(!selected){restoreFont(element);return;}
        let lease=fontLeases.get(element);
        if(!lease||element.style.getPropertyValue('font-family')!==lease.owned||element.style.getPropertyPriority('font-family')!=='important'){
            if(!lease&&fontLeases.size>=4096)return;
            lease={original:element.style.getPropertyValue('font-family'),priority:element.style.getPropertyPriority('font-family')};
            fontLeases.set(element,lease);
        }
        // Set a CSS property rather than generating a stylesheet. Preserve all
        // authored sizes, styles and line boxes, and release only our own value.
        element.style.setProperty('font-family',JSON.stringify(selected),'important');lease.owned=element.style.getPropertyValue('font-family');
    }
    const key=s=>s.replace(/\r\n?/g,'\n');
    const valid=s=>typeof s==='string'&&s.trim().length>0&&s.length<=6000&&!s.includes('\0')&&/[A-Za-z\u00c0-\u2fff\u3040-\u9fff\uac00-\ud7af]/.test(s);
    const tokens=s=>s.match(/<\/?[A-Za-z][^>\r\n]{0,160}>|\[(?:[^\]\r\n]{1,160})\]|\{[^{}\r\n]{1,160}\}|%(?:\([A-Za-z_][A-Za-z_0-9]*\))?[sdif]|\\[A-Za-z]+(?:\[[^\]\r\n]*\])?/g)||[];
    function compatible(source,text){
        if(typeof text!=='string'||!text.trim()||text.length>12000||text.includes('\0')||text.includes('⟦RPG'))return false;
        const a=tokens(source),b=tokens(text);return a.length===b.length&&a.every((v,i)=>v===b[i]);
    }
    function text(source){
        if(!enabled||!valid(source))return source;
        if(characters&&characters.isOutput(source))return source;
        const normalized=key(source);if(cache.has(normalized))return key(cache.get(normalized));
        // A measurement wrapper can call a drawing wrapper with our result.
        // Never enqueue that translated output as a new source sentence.
        if(outputs.has(normalized))return source;
        if(!pending.has(normalized)&&pending.size<4096&&(!retry.has(normalized)||retry.get(normalized)<Date.now())){pending.add(normalized);queue.push(normalized);}
        return source;
    }
    function wrap(owner,name,make,fontAware){
        if(!owner||typeof owner[name]!=='function')return false;
        const previous=hooks.findIndex(h=>h.owner===owner&&h.name===name);
        if(previous>=0){const old=hooks[previous];if(owner[name]===old.replacement)return false;old.active=false;hooks.splice(previous,1);}
        const original=owner[name],wrapped=make(original),h={owner,name,original,active:true};
        h.replacement=function(){return (h.active&&(enabled||fontAware&&(fontIndependent()||engine==='vnmaker'&&nativeFontRevision>0))?wrapped:original).apply(this,arguments);};
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
            for(const tag of [kag.tag&&kag.tag.chara_ptext,tags&&tags.chara_ptext])wrap(tag,'start',original=>function(pm){
                const owner=this.kag||kag,id=pm&&pm.name,record=typeof id==='string'&&owner.stat&&owner.stat.charas&&owner.stat.charas[id];
                speakers.set(owner,record&&/^[A-Za-z_][A-Za-z_0-9]{0,79}$/.test(id)?{id:'tyrano:'+id,name:typeof record.jname==='string'?record.jname:id}:null);
                return original.apply(this,arguments);
            });
            for(const tag of [kag.tag&&kag.tag.text,tags&&tags.text])wrap(tag,'showMessage',original=>function(){
                const owner=this.kag||kag,stat=owner.stat||{};
                if(stat.is_script||stat.is_html)return original.apply(this,arguments);
                const args=Array.prototype.slice.call(arguments),source=args[0];let identity=characters&&characters.active()&&valid(source)&&source.indexOf('<')<0?speakers.get(owner):null;
                if(identity){
                    const facts=characterFacts.get(identity.id)||[];identity=Object.assign({},identity,{evidence:facts.slice(-3)});
                    if(typeof source==='string'&&source.length<=600&&facts[facts.length-1]!==source&&(characterFacts.size<256||characterFacts.has(identity.id)))characterFacts.set(identity.id,facts.concat([source]).slice(-3));
                }
                const surface='tyrano-'+(++characterSerial);
                if(identity)characterText(surface,source,identity);
                args[0]=identity?source:text(source);const result=original.apply(this,args);
                // The native layer owns the current message, so no display-name
                // matching or global source lookup is used to attach its actor.
                const layer=typeof owner.getMessageInnerLayer==='function'&&owner.getMessageInnerLayer();
                const found=layer&&typeof layer.find==='function'&&layer.find('.current_span');
                const element=found&&(typeof found.last==='function'?found.last()[0]:found[found.length-1]);
                if(identity&&element&&typeof source==='string'&&source.indexOf('<')<0&&characterNodes.size<16){
                    const old=characterNodes.get(element);if(old){restoreCharacterNode(element,old);characters.release(old.surface);}
                    if(element.textContent===source||stat.is_adding_text&&source.startsWith(element.textContent))characterNodes.set(element,{surface,source,identity,kag:owner,owned:null,children:null});
                    else if(characters){characters.release(surface);text(source);}
                }else if(characters){characters.release(surface);if(identity)text(source);}
                return result;
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
        const TextRenderer=g.Component_TextRenderer||(g.gs&&g.gs.Component_TextRenderer);
        const MessageRenderer=g.Component_MessageTextRenderer||(g.vn&&g.vn.Component_MessageTextRenderer);
        const MessageBehavior=g.Component_MessageBehavior||(g.vn&&g.vn.Component_MessageBehavior);
        const p=TextRenderer&&TextRenderer.prototype,m=MessageRenderer&&MessageRenderer.prototype,b=MessageBehavior&&MessageBehavior.prototype;
        wrap(p,'measureText',argument(0));wrap(p,'drawText',argument(4));
        wrap(p,'drawFormattedText',argument(4));
        wrap(b,'addMessage',original=>function(message,character){
            const renderer=this.object&&this.object.textRenderer;releaseVnmakerSpeaker(renderer);
            if(renderer&&characters&&characters.active()&&valid(message)){
                const identity=vnmakerIdentity(character);
                if(identity){
                    const facts=characterFacts.get(identity.id)||[];
                    identity.evidence=facts.slice(-3);
                    if(message.length<=600&&(characterFacts.size<256||characterFacts.has(identity.id)))characterFacts.set(identity.id,facts.concat([message]).slice(-3));
                    nativeSpeakers.set(renderer,{surface:'vnmaker-'+(++characterSerial),source:message,identity,behavior:this,adding:true,rendering:0});
                }
            }
            // Keep native source and database records untouched. Only the
            // renderer's own complete message argument may receive a variant.
            try{return original.apply(this,arguments);}catch(error){const row=nativeSpeakers.get(renderer);if(row)row.adding=false;releaseVnmakerSpeaker(renderer);throw error;}
            finally{const row=nativeSpeakers.get(renderer);if(row)row.adding=false;}
        });
        for(const name of ['clear','close','dispose','restoreMessages'])wrap(b,name,original=>function(){releaseVnmakerSpeaker(this.object&&this.object.textRenderer);return original.apply(this,arguments);});
        wrap(m,'clear',original=>function(){releaseVnmakerSpeaker(this);return original.apply(this,arguments);});
        wrap(m,'drawFormattedText',original=>function(){
            const args=Array.prototype.slice.call(arguments),value=args[4];
            const source=typeof value==='string'?value:value&&value.defaultText,row=nativeSpeakers.get(this);
            const identity=row&&row.source===source&&row.behavior.object&&row.behavior.object.textRenderer===this?row.identity:null;
            const result=identity?characterText(row.surface,source,identity):text(source);
            if(typeof value==='string')args[4]=result;
            else if(value&&typeof source==='string'&&result!==source)args[4]=Object.assign(Object.create(Object.getPrototypeOf(value)),value,{defaultText:result});
            if(row)row.rendering++;
            try{return original.apply(this,args);}finally{if(row)row.rendering--;}
        });
        vnmakerFonts();
        return hooks.length>0;
    }
    function releaseVnmakerSpeaker(renderer){
        if(!renderer)return;const row=nativeSpeakers.get(renderer);
        if(row&&(row.adding||row.rendering))return;
        if(row&&characters)characters.release(row.surface);nativeSpeakers.delete(renderer);
    }
    function vnmakerIdentity(character){
        if(!character||typeof character!=='object')return null;
        // Official DataManager.documentsByUid -> document.items.data. Match the
        // actual addMessage record object; names and sentence text are not IDs.
        const manager=g.DataManager||(g.gs&&g.gs.DataManager),documents=manager&&manager.documentsByUid;
        if(!documents||typeof documents!=='object')return null;
        let found=null;
        for(const uid of Object.keys(documents).slice(0,4096)){
            const doc=documents[uid];
            if(!/^[A-Za-z0-9]{32,64}$/.test(uid)||!doc||!doc.items||doc.items.data!==character||doc.uid!==undefined&&doc.uid!==uid)continue;
            if(found)return null;
            found={id:'vnmaker:'+uid,name:typeof character.name==='string'?character.name:uid};
        }
        return found;
    }
    function vnmakerFonts(){
        // Documented gs.Font API. Copies are presentation-only: the author's
        // font/name and serializable game objects are never replaced.
        const Font=g.gs&&g.gs.Font;
        if(typeof Font!=='function'||!Font.prototype||typeof Font.prototype.set!=='function')return;
        function fontCopy(font){
            const family=(enabled||fontIndependent())&&customFont&&customFont.family();
            if(!family||!font||nativeFontConstructing||nativeOwnedFonts.has(font))return font;
            const keys=['name','size','bold','italic','smallCaps','border','borderSize','shadow','shadowOffsetX','shadowOffsetY','underline','strikeThrough'];
            const signature=keys.map(k=>String(font[k])).join('|')+'|'+family+'|'+nativeFontRevision;
            let row=nativeFontCopies.get(font);
            if(!row||row.signature!==signature){
                let copy;nativeFontConstructing=true;
                try{copy=new Font(family,font.size);copy.set(font);copy.name=family;}finally{nativeFontConstructing=false;}
                if(copy.name!==family)throw Error('Font switching unavailable');
                nativeOwnedFonts.add(copy);row={copy,signature};nativeFontCopies.set(font,row);
            }
            // Colors are mutable engine objects. Keep their live value without
            // replacing size, spacing, effects or authored layout controls.
            for(const key of ['color','borderColor','shadowColor'])if(font[key]!==undefined)row.copy[key]=font[key];
            return row.copy;
        }
        for(const name of ['toString','measureChar','measureText','measureTextPlain'])wrap(Font.prototype,name,original=>function(){
            let target=this;try{target=fontCopy(this);}catch(_){return original.apply(this,arguments);}
            return original.apply(target,arguments);
        },true);
        const Behavior=g.Component_TextBehavior||(g.gs&&g.gs.Component_TextBehavior);
        const behavior=Behavior&&Behavior.prototype;
        wrap(behavior,'update',original=>function(){
            const result=original.apply(this,arguments);
            if(this.disposed!==true&&typeof this.refresh==='function'&&nativeTextVersions.get(this)!==nativeFontRevision){
                // Only the documented static-text refresh is called. Message
                // reveal state is never advanced or restarted to change fonts.
                this.refresh(true);nativeTextVersions.set(this,nativeFontRevision);
            }
            return result;
        },true);
    }
    function refreshVnmakerFonts(){
        const Font=g.gs&&g.gs.Font,family=(enabled||fontIndependent())&&customFont&&customFont.family();
        if(family){
            nativeFontConstructing=true;
            try{const probe=new Font(family,16);if(probe.name!==family)throw Error('Font switching unavailable');}finally{nativeFontConstructing=false;}
        }
        const manager=g.gs&&g.gs.ObjectManager&&g.gs.ObjectManager.current;
        const objects=manager&&manager.objects,Behavior=g.Component_TextBehavior||(g.gs&&g.gs.Component_TextBehavior);
        if(!Array.isArray(objects)||typeof Behavior!=='function')return;
        for(const object of objects.slice(0,512)){
            const behavior=object&&object.behavior;
            if(behavior instanceof Behavior&&behavior.disposed!==true&&typeof behavior.refresh==='function'){
                behavior.refresh(true);nativeTextVersions.set(behavior,nativeFontRevision);
            }
        }
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
    const eligible=node=>node&&node.nodeType===3&&eligibleElement(node.parentElement)&&!inputGlyph(node)&&!Array.from(characterNodes.keys()).some(element=>element.contains(node));
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
            restoreFont(node.parentElement);
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
        applyFont(node.parentElement,wanted!==r.source||outputs.has(key(wanted)));
    }
    function requestScan(){if(!enabled&&!fontIndependent())return;if(walk)scanAgain=true;else if(g.document&&g.document.body){dirty.add(g.document.body);walk=g.document.createTreeWalker(g.document.body,5);}}
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
            const generation=epoch;if(watchedFonts.ready)watchedFonts.ready.then(()=>{if((enabled||fontIndependent())&&epoch===generation)refresh();},()=>{});
        }
        return true;
    }
    function refresh(){
        for(const node of nodes.keys())dirty.add(node);
        for(const node of attributes.keys())dirty.add(node);
        if(engine==='choicescript'||engine==='tyrano')requestScan();
    }
    function pump(){
        if(!enabled&&!fontIndependent())return;
        if(enabled&&characters)refreshCharacters();
        for(const node of settling){const r=nodes.get(node);if(!r||node.isConnected===false){settling.delete(node);continue;}if(r.until<=Date.now())dirty.add(node);}
        let count=0;
        for(const node of dirty){dirty.delete(node);applyNode(node);if(++count>=256)break;}
        while(walk&&count++<512){const node=walk.nextNode();if(!node){walk=null;if(scanAgain){scanAgain=false;requestScan();}break;}applyNode(node);}
        // Detached scenes must not keep a strong reference or consume the next page's budget.
        for(const [node,r] of nodes)if(node.isConnected===false){if(node.nodeValue===r.shown)node.nodeValue=r.source;nodes.delete(node);settling.delete(node);}
        for(const [node,records] of attributes)if(node.isConnected===false){restoreAttributes(node,records);attributes.delete(node);}
        for(const element of fontLeases.keys())if(element.isConnected===false)restoreFont(element);
    }
    function install(){
        if(!enabled&&!fontIndependent())return false;
        const hooked=engine==='vnmaker'?vnmaker():engine==='tyrano'?tyrano():false;
        const page=(engine==='choicescript'||engine==='tyrano')&&dom();installed=!!(hooked||page);pump();return installed;
    }
    function restore(){
        enabled=false;
        if(engine==='vnmaker')refreshVnmakerFonts();
        for(const [element,row] of characterNodes)restoreCharacterNode(element,row);characterNodes.clear();characterFacts.clear();speakers=new WeakMap();nativeSpeakers=new WeakMap();if(characters)characters.reset(false);
        if(watchedFonts)watchedFonts.removeEventListener('loadingdone',refresh);watchedFonts=null;settling.clear();
        if(observer)observer.disconnect();observer=null;observedBody=null;walk=null;scanAgain=false;dirty.clear();installed=false;
        for(const [node,r] of nodes)if(node.nodeValue===r.shown&&r.source!==r.shown){node.nodeValue=r.source;r.shown=r.source;}
        for(const [node,records] of attributes)restoreAttributes(node,records);
        for(const element of fontLeases.keys())restoreFont(element);
        nodes.clear();attributes.clear();
        for(const h of hooks){h.active=false;if(h.owner[h.name]===h.replacement)h.owner[h.name]=h.original;}
        hooks.length=0;
        // Translation restoration must not undo a separately selected font.
        // Text remains original because text() still gates on enabled.
        if(fontIndependent()){install();refresh();pump();}
    }
    const status=()=>({enabled,epoch,engine,hooked:installed,live:nodes.size+attributes.size,applied,pending:pending.size});
    function command(q){
        if(q.op==='fontPreferences'){
            if(!customFont||!customFont.configure)throw Error('Font switching unavailable');
            if(engine==='vnmaker'&&q.mode==='custom'&&!(g.gs&&g.gs.Font&&g.gs.Font.prototype&&typeof g.gs.Font.prototype.set==='function'&&typeof g.gs.Font.prototype.toString==='function'))throw Error('Font switching unavailable');
            return customFont.configure(q).then(receipt=>Object.assign(receipt,{enabled,writebackPending:enabled||fontIndependent(),restorePending:(enabled||fontIndependent())&&q.mode==='default'}));
        }
        if(q.op==='translationPrepare'){restore();epoch++;cache=new Map();outputs.clear();pending.clear();queue=[];retry.clear();refresh();return Object.assign(status(),{characterRuntimeContract:characters?characters.prepare(q):null});}
        if(q.op==='translationEnable'){if(!q.prepared){restore();epoch++;cache.clear();outputs.clear();pending.clear();queue=[];retry.clear();}enabled=true;install();refresh();pump();return status();}
        if(q.op==='translationDisable'){restore();epoch++;pending.clear();queue=[];return status();}
        if(q.op==='translationPoll'){install();const role=characters?characters.poll(q,compatible):{};return Object.assign(status(),{texts:enabled?queue.splice(0,32):[]},role);}
        if(q.op==='translationRetry'){if(q.epoch===epoch&&typeof q.source==='string'){pending.delete(key(q.source));retry.set(key(q.source),Date.now()+30000);if(retry.size>4096)retry.delete(retry.keys().next().value);}return {accepted:0};}
        if(q.op==='translationApply'){
            let accepted=0;const sources=[];
            if((enabled||q.prime)&&q.epoch===epoch)for(const e of (Array.isArray(q.entries)?q.entries:[]).slice(0,256)){
                if(!e||!valid(e.source)||!compatible(e.source,e.text)||(cache.size>=150000&&!cache.has(key(e.source))))continue;
                cache.set(key(e.source),e.text);sources.push(e.source);outputs.add(key(e.text));pending.delete(key(e.source));retry.delete(key(e.source));accepted++;
            }
            queue=queue.filter(s=>!cache.has(s));
            // Read back after the entire mutation, so colliding normalized keys
            // cannot certify the earlier value that was overwritten.
            const receipt={accepted,epoch,stale:q.epoch!==epoch};
            if(typeof fusionSourcePairsHash==='function')receipt.sourcePairsHash=fusionSourcePairsHash(sources.map(source=>({source,text:cache.get(key(source))})));
            if(accepted){refresh();pump();}return receipt;
        }
        if(q.op==='translationStatus')return status();throw Error('Unsupported narrative translation request');
    }
    return {command,ensureInstalled:install,capture:text};
}
if(typeof module==='object'&&module.exports)module.exports=createFusionNovelTranslation;
