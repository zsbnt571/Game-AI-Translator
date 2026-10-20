/* Display-only adapters. Tagged links retain game identifiers and use the displayed geometry. */
(function(g){
    'use strict';const transport=g.__fusionWebTransport;if(!transport)return;
    let bridge,epoch=0,ready=false,enabled=false,startupAdoptable=false,cache=new Map(),queued=new Set(),queue=[],retry=new Map(),records=new Map(),sprites=new Map(),hooked=false,applied=0;
    const prefixes=new Map();let prefixRecovered=0,layoutFits=0;
    const rpg=typeof createFusionRpgTranslation==='function';
    if(rpg)bridge=createFusionRpgTranslation(g);
    const novel=!rpg&&typeof createFusionNovelTranslation==='function'&&typeof g.__fusionNovelEngine==='string';
    if(novel)bridge=createFusionNovelTranslation(g,g.__fusionNovelEngine);
    const data=novel&&typeof createFusionNovelData==='function'?createFusionNovelData(g,g.__fusionNovelEngine):null;
    const state=novel&&typeof createFusionNovelState==='function'?createFusionNovelState(g,g.__fusionNovelEngine,data):null;
    function valid(s){return typeof s==='string'&&s.length>0&&s.length<=6000&&!s.includes('\0')&&/[A-Za-z\u00c0-\u2fff\u3040-\u9fff\uac00-\ud7af]/.test(s);}
    function codes(s){return s.match(/\\[\[\]]|\[\/?(?:b|i|u|s|color|font|size|scale|scalex|scaley|tag|offsetx|offsety|spacing|opacity|outline|background|icon)(?:=[^\]\r\n]{0,256})?\]/gi)||[];}
    function compatible(s,t){const a=codes(s),b=codes(t);return a.length===b.length&&a.every((v,i)=>v===b[i]);}
    function spriteMarkup(s,height){return s.replace(/\\[\[\]]|\[(\/?)scale(?:=([-+\d.e]+))?\]/gi,(tag,close,value)=>{
        if(tag[0]==='\\')return tag;if(close)return '[/size]';const scale=Number(value);
        return Number.isFinite(scale)&&scale>0&&scale<=64?'[size='+(height*scale*0.62)+']':tag;
    });}
    function text(s){if(!enabled||!valid(s))return s;if(cache.has(s))return cache.get(s);if(!queued.has(s)&&queue.length<256&&(!retry.has(s)||retry.get(s)<Date.now())){queued.add(s);queue.push(s);}return s;}
    function indexSource(s){
        if(s.length<12||s.length>6000||!/[\s]/.test(s)||codes(s).length)return;
        const key=s.slice(0,8);let list=prefixes.get(key);if(list===false)return;
        if(!list){list=[];prefixes.set(key,list);}if(!list.includes(s)){if(list.length>=8)prefixes.set(key,false);else list.push(s);}
    }
    // A control may reveal by repeatedly setting a longer source string instead
    // of using the engine's typewriter. Its generation is bounded by continuity.
    function display(record,source){
        const now=Date.now(),changed=record.source!==source;
        if(changed){
            const growing=typeof record.source==='string'&&record.source.length>0&&source.length>record.source.length&&source.startsWith(record.source);
            // Some scripts assign the complete sentence, then immediately replay
            // it from character one (sometimes through an empty reset). Keep
            // only this control's just-observed complete source for that burst.
            // Do not infer a sentence from an arbitrary short shared prefix.
            const restart=record.restart&&now-record.restart.at<=240&&source.length>0&&source.length<record.restart.source.length&&record.restart.source.startsWith(source);
            if(restart){record.full=record.restart.source;record.growing=true;}
            else if(!growing){record.full=null;record.hold=null;record.growing=false;}
            else record.growing=true;
            record.source=source;record.due=now+240;
            if(cache.has(source)&&source.length>=12&&!codes(source).length&&(!record.growing||record.full===source))record.restart={source,at:now};
            else if(source&&record.restart&&!record.restart.source.startsWith(source))record.restart=null;
        }
        if(!enabled){record.full=null;record.hold=null;record.restart=null;record.growing=false;return {body:source,count:-1,translated:false};}
        if(record.full&&!record.full.startsWith(source))record.full=null;
        if(!record.full&&record.growing&&source.length>=8){
            const list=prefixes.get(source.slice(0,8)),matches=list&&list.filter(s=>s.startsWith(source));
            if(matches&&matches.length===1){record.full=matches[0];prefixRecovered++;}
        }
        if(record.full&&cache.has(record.full)){
            const body=cache.get(record.full),count=source===record.full?-1:Math.floor(Array.from(body).length*Array.from(source).length/Array.from(record.full).length);
            record.hold={body,count,translated:body!==record.full};return record.hold;
        }
        if(record.growing&&now<record.due&&record.hold)return record.hold;
        const body=record.growing&&now<record.due&&!cache.has(source)?source:text(source);
        if(body!==source){record.hold={body,count:-1,translated:true};return record.hold;}
        return record.growing&&record.hold?record.hold:{body:source,count:-1,translated:false};
    }
    function fitText(inst,record,view){
        const rt=inst._rendererText;
        if(!view.translated&&record.ownedReveal&&rt.SetDrawMaxCharacterCount){
            let count=-1;
            if(inst._typewriterEndTime>=0){const progress=Math.max(0,Math.min(1,(inst._runtime.GetWallTime()-inst._typewriterStartTime)/Math.max(.001,inst._typewriterEndTime-inst._typewriterStartTime)));count=Math.floor(rt.GetLengthInGraphemes()*progress);}
            rt.SetDrawMaxCharacterCount(count);record.ownedReveal=false;
        }
        if(!inst.GetWorldInfo||!rt.SetFontSizeScale||!rt.GetTextHeight||!rt.GetTextWidth||!rt.SetWordWrapMode)return;
        const wi=inst.GetWorldInfo(),width=wi.GetWidth(),height=wi.GetHeight(),scale=wi.GetSceneGraphScale();
        if(!Number.isFinite(width+height+scale)||width<=0||height<=4||scale<=0)return;
        if(!view.translated){if(record.fitted){rt.SetFontSizeScale(scale);rt.SetWordWrapMode(inst._wrapByWord?'word':'character');record.fitted=false;record.layoutKey=null;}return;}
        const key=JSON.stringify([view.body,width,height,scale,inst._ptSize,inst._faceName,inst._lineHeightOffset,inst._enableBBcode,inst._isBold,inst._isItalic]);
        rt.SetWordWrapMode('character');
        if(record.layoutKey!==key){
            const layer=wi.GetLayer(),zoom=layer.GetRenderScale()*layer.Get2DScaleFactorToZ(wi.GetTotalZElevation());
            rt.SetSize(width,height,zoom);rt.SetFontSize(inst._ptSize);rt.SetFontSizeScale(scale);
            const fits=()=>rt.GetTextHeight()<=height-2&&rt.GetTextWidth()<=width+.5;
            let fit=1;if(!fits()){let low=.125,high=1;rt.SetFontSizeScale(scale*low);
                if(fits()){for(let i=0;i<9;i++){const mid=(low+high)/2;rt.SetFontSizeScale(scale*mid);if(fits())low=mid;else high=mid;}fit=low;}else fit=.125;}
            record.fit=fit;record.layoutKey=key;layoutFits++;
        }
        rt.SetFontSizeScale(scale*record.fit);record.fitted=true;
        if(rt.SetDrawMaxCharacterCount){let count=view.count;
            if(inst._typewriterEndTime>=0){const progress=Math.max(0,Math.min(1,(inst._runtime.GetWallTime()-inst._typewriterStartTime)/Math.max(.001,inst._typewriterEndTime-inst._typewriterStartTime)));count=Math.floor(rt.GetLengthInGraphemes()*progress);}
            rt.SetDrawMaxCharacterCount(count);record.ownedReveal=true;}
    }
    function apply(inst){
        if(!inst||!inst._rendererText||typeof inst._text!=='string')return;
        const source=inst._text;let record=records.get(inst);
        if(!record&&records.size<512){record={source:null,shown:null};records.set(inst,record);}
        // A full bookkeeping cache must not suppress a visible control's translation.
        if(!record){record={source:null,shown:null};records.delete(records.keys().next().value);records.set(inst,record);}
        const view=display(record,source),wanted=view.body;
        // Compare actual renderer text as well: the engine can reset it while keeping the same source.
        const renderer=inst._rendererText,actual=typeof renderer.GetText==='function'?renderer.GetText():null;
        if(record.shown!==wanted||actual!==null&&actual!==wanted){renderer.SetText(wanted);record.shown=wanted;applied++;if(inst._runtime&&typeof inst._runtime.UpdateRender==='function')inst._runtime.UpdateRender();}
        fitText(inst,record,view);
    }
    function install(){
        if(rpg||novel){return bridge.ensureInstalled();}
        if(hooked)return true;const P=g.C3&&g.C3.Plugins&&g.C3.Plugins.Text&&g.C3.Plugins.Text.Instance&&g.C3.Plugins.Text.Instance.prototype;
        if(!P||typeof P._UpdateTextSettings!=='function'||typeof P._SetText!=='function'||typeof P.Draw!=='function')return false;
        ['_UpdateTextSettings','_UpdateTextSize','_SetText','Draw'].forEach(name=>{const original=P[name];if(typeof original!=='function')return;P[name]=function(){if(name==='Draw')apply(this);const result=original.apply(this,arguments);if(name!=='Draw')apply(this);return result;};});
        const release=P.Release;if(typeof release==='function')P.Release=function(){records.delete(this);return release.apply(this,arguments);};hooked=true;return true;
    }
    function spriteDraw(inst,renderer){
        const source=inst._text;if(!valid(source))return false;
        const C=g.C3,wi=inst.GetWorldInfo();let item=sprites.get(inst);
        if(!item){if(sprites.size>=256)return false;item={rt:null,shown:null,active:false,key:null};sprites.set(inst,item);}
        const view=display(item,source),wanted=view.body;
        item.active=false;if(!view.translated||!compatible(item.full||source,wanted))return false;
        if(!item.rt){const rt=C.New(C.Gfx.RendererText,inst._runtime.GetRenderer(),{timeout:5});rt.ontextureupdate=()=>inst._runtime.UpdateRender();item.rt=rt;}
        const rt=item.rt,layer=wi.GetLayer(),height=inst._sdkType.GetSpriteFont().GetCharacterHeight(),width=wi.GetWidth(),boxHeight=wi.GetHeight(),sceneScale=wi.GetSceneGraphScale();
        const zoom=layer.GetRenderScale()*layer.Get2DScaleFactorToZ(wi.GetTotalZElevation()),bb=!!inst._enableBBcode;
        const shown=bb?spriteMarkup(wanted,height):wanted,key=JSON.stringify([shown,width,boxHeight,height,inst._characterScale,sceneScale,zoom,bb,inst._horizontalAlign,inst._verticalAlign]);
        if(!Number.isFinite(width+boxHeight+sceneScale+zoom)||width<=0||boxHeight<=4||sceneScale<=0||zoom<=0)return false;
        if(item.key!==key){
            rt.SetText(shown);rt.SetFontName('Microsoft YaHei UI');rt.SetFontSize(Math.max(1,height*inst._characterScale*0.62));
            rt.SetBBCodeEnabled(bb);rt.SetHorizontalAlignment(['left','center','right'][inst._horizontalAlign]||'left');rt.SetVerticalAlignment(['top','center','bottom'][inst._verticalAlign]||'top');rt.SetWordWrapMode('character');rt.SetSize(width,boxHeight,zoom);
            // Measure the complete translated block, including tagged choices, before painting.
            // Never grow the game object: its neighbouring controls keep their own space.
            rt.SetFontSizeScale(sceneScale);const fits=()=>rt.GetTextHeight()<=boxHeight-2&&rt.GetTextWidth()<=width+0.5;
            let fit=1;if(!fits()){let low=0.125,high=1;rt.SetFontSizeScale(sceneScale*low);if(!fits())return false;
                for(let n=0;n<8;n++){const mid=(low+high)/2;rt.SetFontSizeScale(sceneScale*mid);if(fits())low=mid;else high=mid;}fit=low;rt.SetFontSizeScale(sceneScale*fit);}
            item.fit=fit;item.key=key;item.shown=shown;applied++;
        }
        if(typeof rt.SetColor==='function'&&typeof wi.GetUnpremultipliedColor==='function')rt.SetColor(wi.GetUnpremultipliedColor());else rt.SetColorRgb(1,1,1);
        let count=view.count;if(inst._typewriterEndTime>=0){const elapsed=inst._runtime.GetWallTime()-inst._typewriterStartTime,total=inst._typewriterEndTime-inst._typewriterStartTime;count=Math.ceil(rt.GetLengthInGraphemes()*Math.max(0,Math.min(1,elapsed/Math.max(0.001,total))));}rt.SetDrawMaxCharacterCount(count);
        const texture=rt.GetTexture();item.active=!!texture;if(texture&&renderer){renderer.SetTexture(texture);let quad=wi.GetBoundingQuad();if(inst._runtime.IsPixelRoundingEnabled())quad=wi.PixelRoundQuad(quad);renderer.Quad3(quad,rt.GetTexRect());}return item.active;
    }
    function translatedSprite(inst){try{spriteDraw(inst,null);const item=sprites.get(inst);return item&&item.active?item.rt:null;}catch(_){const item=sprites.get(inst);if(item)item.active=false;return null;}}
    function spriteTagAt(inst,rt,x,y){const wi=inst.GetWorldInfo(),dx=x-wi.GetX(),dy=y-wi.GetY(),angle=wi.GetAngle(),c=Math.cos(angle),s=Math.sin(angle),scale=rt.GetDrawScale();
        const fragment=rt.HitTestFragment((dx*c+dy*s+wi.GetWidth()*wi.GetOriginX())*scale,(-dx*s+dy*c+wi.GetHeight()*wi.GetOriginY())*scale),tag=fragment&&fragment.GetStyleTag('tag');return tag?tag.param:'';}
    let spriteHooked=false;
    function installAll(){
        install();if(rpg||novel||spriteHooked)return;const P=g.C3&&g.C3.Plugins&&g.C3.Plugins.Spritefont2&&g.C3.Plugins.Spritefont2.Instance&&g.C3.Plugins.Spritefont2.Instance.prototype;
        if(!P||typeof P.Draw!=='function'||!g.C3.Gfx||!g.C3.Gfx.RendererText)return;
        const draw=P.Draw;P.Draw=function(renderer){try{if(spriteDraw(this,renderer))return;}catch(_){const item=sprites.get(this);if(item)item.active=false;}return draw.apply(this,arguments);};
        const hit=P._GetTagAtPosition;if(typeof hit==='function')P._GetTagAtPosition=function(x,y){const rt=translatedSprite(this);return rt?spriteTagAt(this,rt,x,y):hit.apply(this,arguments);};
        const position=P._GetTagPosition;if(typeof position==='function')P._GetTagPosition=function(tag,index){const rt=translatedSprite(this);if(!rt)return position.apply(this,arguments);const f=rt.FindFragmentWithTag(tag,Math.floor(index));if(!f)return null;
            const wi=this.GetWorldInfo(),scale=rt.GetDrawScale(),rx=typeof rt.GetWidth==='function'?rt.GetWidth()/wi.GetWidth():scale,ry=typeof rt.GetHeight==='function'?rt.GetHeight()/wi.GetHeight():scale;
            const top=typeof f.GetFontBoundingBoxDescent==='function'?f.GetPosY()-(f.GetHeight()-f.GetFontBoundingBoxDescent())*scale:f.GetPosY();
            const x=f.GetPosX()/rx-wi.GetWidth()*wi.GetOriginX(),y=top/ry-wi.GetHeight()*wi.GetOriginY(),a=wi.GetAngle();
            return {x:wi.GetX()+x*Math.cos(a)-y*Math.sin(a),y:wi.GetY()+x*Math.sin(a)+y*Math.cos(a),width:f.GetWidth()*scale/rx,height:f.GetHeight()*scale/ry};};
        const count=P._GetTagCount;if(typeof count==='function')P._GetTagCount=function(tag){const rt=translatedSprite(this);return rt?rt.CountFragmentsWithTag(tag):count.apply(this,arguments);};
        const release=P.Release;P.Release=function(){const item=sprites.get(this);if(item){if(item.rt)item.rt.Release();sprites.delete(this);}return release.apply(this,arguments);};spriteHooked=true;
    }
    function refresh(){for(const inst of records.keys())apply(inst);for(const inst of sprites.keys()){spriteDraw(inst,null);inst._runtime.UpdateRender();}}
    function status(){return {enabled,epoch,live:records.size+sprites.size,applied,hooked:rpg||hooked,spriteHooked,prefixRecovered,layoutFits};}
    function command(q){
        if(q.op&&q.op.startsWith('state')){if(!state)throw Error('Game state adapter unavailable');return state.command(q);}
        if(q.op&&q.op.startsWith('data')){if(!data)throw Error('Game data adapter unavailable');return data.command(q);}
        if(q.id<0){if(state)state.stop();if(data)data.command({op:'dataDisable'});}
        // The desktop session attaches after first paint. Adopt its own startup
        // cache once instead of clearing visible translations and repainting them.
        if(q.op==='translationPrepare'){
            if(q.preserveStartup&&startupAdoptable&&enabled){startupAdoptable=false;return status();}
            startupAdoptable=false;
        }
        if(q.op==='translationEnable'&&q.prepared&&enabled)return status();
        if(rpg||novel){const result=bridge.command(q);if(result.epoch!==undefined)epoch=result.epoch;if(result.enabled!==undefined)enabled=result.enabled;return result;}
        if(q.op==='translationPrepare'){enabled=false;refresh();epoch++;cache=new Map();prefixes.clear();queued.clear();queue=[];retry.clear();return status();}
        if(q.op==='translationEnable'){if(!q.prepared){epoch++;cache.clear();prefixes.clear();queued.clear();queue=[];}enabled=true;install();refresh();return status();}
        if(q.op==='translationDisable'){enabled=false;epoch++;queue=[];queued.clear();refresh();for(const item of sprites.values())if(item.rt)item.rt.Release();sprites.clear();return status();}
        if(q.op==='translationPoll')return {...status(),texts:enabled?queue.splice(0,8):[]};
        if(q.op==='translationRetry'){if(q.epoch===epoch&&typeof q.source==='string'){queued.delete(q.source);retry.set(q.source,Date.now()+30000);if(retry.size>4096)retry.delete(retry.keys().next().value);}return {accepted:0};}
        if(q.op==='translationApply'){
            let accepted=0;const sources=[];
            if((enabled||q.prime)&&q.epoch===epoch)for(const e of (Array.isArray(q.entries)?q.entries:[]).slice(0,256)){
                if(e&&valid(e.source)&&typeof e.text==='string'&&e.text.trim()&&e.text.length<=12000&&!e.text.includes('\0')&&!e.text.includes('⟦RPG')&&compatible(e.source,e.text)&&(cache.has(e.source)||cache.size<150000)){
                    cache.set(e.source,e.text);sources.push(e.source);indexSource(e.source);queued.delete(e.source);retry.delete(e.source);accepted++;
                }
            }
            queue=queue.filter(s=>!cache.has(s));
            const receipt={accepted,epoch,stale:q.epoch!==epoch};
            if(typeof fusionSourcePairsHash==='function')receipt.sourcePairsHash=fusionSourcePairsHash(sources.map(source=>({source,text:cache.get(source)})));
            if(accepted)refresh();return receipt;
        }
        if(q.op==='translationStatus')return status();throw Error('Unsupported translation request');
    }
    const startup=JSON.parse(transport.startup()),prepare=command({op:'translationPrepare'});
    if(startup.enabled){let batch=[];for(const source of Object.keys(startup.entries||{}).slice(0,150000)){batch.push({source,text:startup.entries[source]});if(batch.length===256){command({op:'translationApply',prime:true,epoch:prepare.epoch,entries:batch});batch=[];}}if(batch.length)command({op:'translationApply',prime:true,epoch:prepare.epoch,entries:batch});command({op:'translationEnable',prepared:true});startupAdoptable=true;}
    g.__fusionWebInstall=installAll;
    if(g.document)g.document.addEventListener('load',installAll,true);
    function tick(){
        try{
            installAll();if(data)data.tick();if(!ready){transport.hello();ready=true;}
            const raw=transport.request();if(!raw)return;const q=JSON.parse(raw);let reply;
            try{
                const result=command(q);
                if(result&&typeof result.then==='function'){
                    result.then(value=>transport.reply(JSON.stringify({id:q.id,ok:true,result:value})),error=>transport.reply(JSON.stringify({id:q.id,ok:false,error:String(error&&error.message||error).slice(0,256)}))).catch(()=>{});
                    return;
                }
                reply={id:q.id,ok:true,result};
            }catch(e){reply={id:q.id,ok:false,error:String(e.message||e).slice(0,256)};}
            if(q.id<0){clearInterval(timer);return;}transport.reply(JSON.stringify(reply));
        }catch(_){}
    }
    const timer=setInterval(tick,50);tick();
})(typeof window==='object'?window:self);
