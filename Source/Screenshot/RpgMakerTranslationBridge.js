/* Fusion RPG MV/MZ text cache and display hooks. Game data and saves stay original. */
function createFusionRpgTranslation(g) {
    "use strict";
    var enabled=false,cache=Object.create(null),outputs=Object.create(null),pending=Object.create(null),retry=Object.create(null),queue=[],windows=[],hooks=[],epoch=0,hookRevision=0,hookOwner={};
    var drawings=[],drawingBytes=0,replaying=false,rawAllText=null,displayDepth=0;
    var messagePage=null,messageCanvas=null,messageDrawScope=null,messageReplaying=false;
    var messageFontResult={messageRedrawPending:false,messageRedrawn:false};
    var characters=typeof createFusionCharacterRuntime==='function'?createFusionCharacterRuntime(function(){refresh(true);}):null;
    var characterFacts=Object.create(null);
    function speakerActor(raw){
        if(typeof raw!=='string'||!raw||raw.length>200)return null;
        raw=raw.replace(/\x1b/g,'\\');
        var match=/^\\N\[([1-9][0-9]{0,7})\]$/i.exec(raw),actor=null,id=0;
        if(match){id=Number(match[1]);actor=g.$gameActors&&g.$gameActors.actor(id);}
        else {
            // Only an explicit speaker field is matched. Do not scan dialogue,
            // compare names fuzzily, infer a face or choose the first duplicate.
            if(/[\\<>\r\n]/.test(raw)||!Array.isArray(g.$dataActors)||g.$dataActors.length>10000)return null;
            for(var n=1;n<g.$dataActors.length;n++){
                var data=g.$dataActors[n];if(!data||data.id!==n)continue;
                var live=g.$gameActors&&g.$gameActors._data&&g.$gameActors._data[n];
                var name=live&&typeof live.name==='function'?live.name():data.name;
                if(raw!==data.name&&raw!==name)continue;
                if(id&&id!==n)return null;id=n;
            }
            if(id)actor=g.$gameActors&&g.$gameActors.actor(id);
        }
        return actor&&typeof actor.actorId==='function'&&actor.actorId()===id?actor:null;
    }
    function authoredNameBox(source){
        // YEP Message Core's documented name-window protocol. Require its
        // actual runtime contract; a similar string in ordinary text is data.
        if(!(g.Imported&&g.Imported.YEP_MessageCore)||!g.Window_Message||typeof g.Window_Message.prototype.convertNameBox!=='function')return null;
        var escaped=String(source).replace(/\x1b/g,'\\').replace(/\\\\/g,'\x00'),rows=[],re=/\\N(?:C|R|[1-5])?<([^<>\r\n]{1,200})>/gi,m;
        while((m=re.exec(escaped)))rows.push(m);
        return rows.length===1?{name:rows[0][1],token:rows[0][0]}:null;
    }
    function plainMessage(source){
        var box=authoredNameBox(source),plain=box?source.replace(box.token,''):source;
        return typeof plain==='string'&&!/[\\<>\x00-\x09\x0b-\x1f]/.test(plain)?plain:null;
    }
    function messageText(source){
        if(!enabled||!characters)return text(source,null,true);
        var message=g.$gameMessage,raw=message&&typeof message.speakerName==='function'?message.speakerName():null;
        var box=raw===null?authoredNameBox(source):null;
        var actor=speakerActor(box?box.name:raw);
        var identity=null;
        if(actor){
            var kind=g.Utils&&g.Utils.RPGMAKER_NAME==='MV'?'mv':raw!==null?'mz':'mv';
            var id=kind+':actor:'+actor.actorId(),facts=characterFacts[id]||[];
            identity={id:id,name:typeof actor.name==='function'?actor.name():id,evidence:facts.slice(-3)};
            if(source&&facts[facts.length-1]!==source){facts=facts.concat([source]).slice(-3);if(Object.keys(characterFacts).length<256||characterFacts[id])characterFacts[id]=facts;}
        }
        return characters.render('message',source,identity,function(){return translated(source);},function(){return text(source,null,true);});
    }
    var customFont=typeof createFusionGameFontPolicy==='function'?createFusionGameFontPolicy(g,g.__fusionGameFontOptions,function(){if(enabled||fontIndependent()){install();refresh(true,null,true);}}):null;
    function fontIndependent(){return customFont&&customFont.independent&&customFont.independent();}
    function storedPairsHash(entries){
        // Identifies accepted pairs, not pixels or resource writes. Old browser
        // contexts without Node retain their count-only receipt contract.
        if(typeof fusionSourcePairsHash==="function")return fusionSourcePairsHash(entries);
        if(typeof require!=="function")return null;
        var crypto=require("crypto"),Bytes=require("buffer").Buffer,hash=crypto.createHash("sha256");
        function frame(length){var bytes=Bytes.alloc?Bytes.alloc(4):new Bytes(4);bytes.writeUInt32LE(length,0);hash.update(bytes);}
        var pairs=entries.slice().sort(function(a,b){return a.source<b.source?-1:a.source>b.source?1:0;});frame(pairs.length);
        pairs.forEach(function(pair){[pair.source,pair.text].forEach(function(text){var bytes=Bytes.from?Bytes.from(text,"utf8"):new Bytes(text,"utf8");frame(bytes.length);hash.update(bytes);});});
        return hash.digest("hex");
    }
    // YEP adds these layout markers at runtime; keep other tags and escape codes exact.
    function key(value){return String(value).replace(/\r\n/g,"\n").replace(/\x1b/g,"\\").replace(/<WordWrap>/gi,"");}
    var templates=Object.create(null),templateBuckets=Object.create(null);
    function registerTemplate(source,translation){
        if(!Object.prototype.hasOwnProperty.call(templates,source)){
            var marker=source.search(/\\[VNP]\[\d+\]|\\G\b/i),prefix=source.slice(0,Math.min(3,Math.max(0,marker)));
            (templateBuckets[prefix]||(templateBuckets[prefix]=[])).push(source);
        }
        templates[source]=translation;
    }
    function expand(value){
        // The engine may expand variables and actor names before custom windows draw.
        // Resolve source and translated template against the same current game state.
        // Match stock MV/MZ's two bounded variable passes and escaped literal
        // backslashes. A literal \\V marker must never read a game variable.
        var escaped=value.replace(/\\/g,"\x1b").replace(/\x1b\x1b/g,"\x00");
        for(var pass=0;pass<2;pass++)escaped=escaped.replace(/\x1bV\[(\d+)\]/gi,function(code,id){
            try{return g.$gameVariables?String(g.$gameVariables.value(Number(id))):code;}catch(_){return code;}
        });
        return escaped.replace(/\x1b([NP])\[(\d+)\]/gi,function(code,kind,id){
            id=Number(id);kind=kind.toUpperCase();
            try{
                var actor=kind==="N"&&g.$gameActors?g.$gameActors.actor(id):kind==="P"&&g.$gameParty?g.$gameParty.members()[id-1]:null;
                return actor&&actor.name?String(actor.name()):code;
            }catch(_){return code;}
        }).replace(/\x1bG/gi,function(code){return g.TextManager&&g.TextManager.currencyUnit||code;})
          .replace(/\x1b/g,"\\").replace(/\x00/g,"\\\\");
    }
    function templateTranslation(value){
        var result;
        var candidates=[];for(var length=0;length<=Math.min(3,value.length);length++){var bucket=templateBuckets[value.slice(0,length)];if(bucket)candidates=candidates.concat(bucket);}
        candidates.some(function(source){
            if(expand(source)!==value)return false;
            var candidate=expand(templates[source]);
            if(result!==undefined&&result!==candidate){result=null;return true;}
            result=candidate;return false;
        });
        return result===null?undefined:result;
    }
    function translated(value){
        if(Object.prototype.hasOwnProperty.call(cache,value))return cache[value];
        var normalized=key(value),result=cache[normalized];
        if(result===undefined)result=templateTranslation(normalized);
        if(result===undefined&&normalized.indexOf("\n")>=0){
            var lines=normalized.split("\n"),all=true;
            result=lines.map(function(line){if(!valid(line))return line;var found=cache[line];if(found===undefined)found=templateTranslation(line);if(found!==undefined)return found;all=false;return line;}).join("\n");
            if(!all)result=undefined;
        }
        if(result!==undefined&&/<WordWrap>/i.test(value)&&!/<WordWrap>/i.test(result))result="<WordWrap>"+result;
        return result;
    }
    var unicodeLetters=null;try{unicodeLetters=new RegExp("\\p{L}","u");}catch(_){/* Older NW.js uses the conservative fallback below. */}
    function valid(value){return typeof value==="string"&&value.length>=1&&value.length<=6000&&value.indexOf("\x00")<0&&(unicodeLetters?unicodeLetters.test(value):value.toLowerCase()!==value.toUpperCase()||/[\u3040-\u30ff\u4e00-\u9fff\uac00-\ud7af\u0620-\u064a\u066e-\u06d3\u0750-\u077f\u08a0-\u08c9\u0e01-\u0e2e\u0e30-\u0e3a\u0e40-\u0e46\u0e4d]/.test(value));}
    function compatible(source,translation){
        if(typeof translation!=="string"||!translation.trim()||translation.length>12000||translation.indexOf("\x00")>=0||translation.indexOf("⟦RPG")>=0)return false;
        function codes(value){return key(value).match(/<\/?[A-Za-z][^>\r\n]{0,160}>|\\(?:(?:RESETSHAKE|SHAKE|WAVE|SLIDE|CIRCLE)(?:<[^>\r\n]{0,160}>)?|[A-Za-z]+(?:\[[^\]\r\n]*\])?|[.!|><^{}$\\])/gi)||[];}
        var a=codes(source),b=codes(translation),aa=a.slice().sort(),bb=b.slice().sort();
        if(aa.length!==bb.length||aa.some(function(v,i){return v!==bb[i];}))return false;
        function control(v){return !/^\\[VN]\[\d+\]$/i.test(v);}
        a=a.filter(control);b=b.filter(control);return a.every(function(v,i){return v===b[i];});
    }
    function text(value,owner,priority) {
        if(!enabled||!valid(value)||/^(?:Loading\s+)?(?:img|audio|movies|data|js)[\/\\].+\.(?:png|jpg|jpeg|webp|ogg|m4a|wav|webm|mp4|json|js)(?:\?\S*)?$/i.test(value.trim()))return value;
        if(characters&&characters.isOutput(value))return value;
        var found=translated(value);if(found!==undefined)return found;
        if(outputs[value])return value;
        var originalValue=value;value=key(value);
        // A message may contain only a variable marker. Capture its current
        // display value, not the marker or the script that assigned it. Reads
        // use the same bounded V/N/P/G expansion as cached templates; neither
        // the message data nor the underlying game variables are overwritten.
        var expanded=expand(value);
        if(expanded!==value){
            value=expanded;
            found=translated(value);if(found!==undefined)return /<WordWrap>/i.test(originalValue)&&!/<WordWrap>/i.test(found)?"<WordWrap>"+found:found;
            if(!valid(value)||outputs[value])return originalValue;
        }
        if(owner){owner._fusionTexts=owner._fusionTexts||Object.create(null);owner._fusionTexts[value]=true;}
        if(Object.prototype.hasOwnProperty.call(cache,value))return cache[value];
        if(owner&&windows.indexOf(owner)<0&&windows.length<100)windows.push(owner);
        if(!pending[value]&&(!retry[value]||retry[value]<Date.now())&&Object.keys(pending).length<256){pending[value]=true;if(priority)queue.unshift(value);else queue.push(value);}
        return originalValue;
    }
    function wrap(proto,name,make) {
        if(!proto||typeof proto[name]!=="function"||proto[name]._fusionHookOwner===hookOwner)return;
        var original=proto[name],replacement=make(original);replacement._fusionHookOwner=hookOwner;
        proto[name]=replacement;hookRevision++;
        // A late plugin can replace an entry. Keep one ownership record per slot.
        hooks=hooks.filter(function(h){return h.proto!==proto||h.name!==name;});
        hooks.push({proto:proto,name:name,original:original,replacement:replacement});
    }
    function protectedWindow(w){return !!((g.Window_NameInput&&w instanceof g.Window_NameInput)||(g.Window_NameEdit&&w instanceof g.Window_NameEdit));}
    // Whole dialogue is translated before the engine reveals it. Never translate
    // its individual glyphs again, nor character keyboards or editable names.
    function passthrough(original){return function(){displayDepth++;try{return original.apply(this,arguments);}finally{displayDepth--;}};}
    function draw(original){return function(value){
        if(protectedWindow(this)||(g.Window_Message&&this instanceof g.Window_Message)||this._fusionDrawing)return passthrough(original).apply(this,arguments);
        var a=Array.prototype.slice.call(arguments);a[0]=text(value,this,false);this._fusionDrawing=true;
        displayDepth++;try{return original.apply(this,a);}finally{displayDepth--;this._fusionDrawing=false;}
    };}
    function forget(bitmap){drawings=drawings.filter(function(d){if(d.bitmap!==bitmap)return true;drawingBytes-=d.bytes;return false;});}
    function activeMessage(){return g.SceneManager&&g.SceneManager._scene&&g.SceneManager._scene._messageWindow;}
    function fontKey(){return (enabled||fontIndependent())&&customFont?customFont.family():'';}
    function messageFont(font,family){
        var prefix=/^(.*?\b(?:\d+(?:\.\d+)?|\.\d+)px(?:\/[^\s]+)?\s+).+$/.exec(font);
        return prefix?prefix[1]+family:null;
    }
    function blockMessagePage(page){if(page){page.blocked=true;page.image=null;page.ops=[];page.calls=[];}}
    // NW.js shipped with older MV games predates Canvas.getTransform. Track
    // only contexts we witness being created by Bitmap, from their known
    // identity state. Existing/unowned contexts remain ineligible for replay.
    var legacyTransforms=typeof WeakMap==='function'?new WeakMap():null;
    function messageTransform(ctx){
        try{
            if(typeof ctx.getTransform==='function')return ctx.getTransform();
            var state=legacyTransforms&&legacyTransforms.get(ctx);
            if(!state||!state.matrix||state.hooks.some(function(h){return ctx[h.name]!==h.wrapper;})||state.dimensions.some(function(d){var now=Object.getOwnPropertyDescriptor(ctx.canvas,d.name);return !now||now.set!==d.set||now.get!==d.get;}))return null;
            var m=state.matrix;return {a:m[0],b:m[1],c:m[2],d:m[3],e:m[4],f:m[5]};
        }catch(_){return null;}
    }
    function trackFreshCanvas(canvas){
        if(!legacyTransforms||!canvas||String(canvas.nodeName).toLowerCase()!=='canvas')return;
        var ctx=canvas.getContext('2d');
        if(!ctx||typeof ctx.getTransform==='function'||legacyTransforms.has(ctx))return;
        var state={matrix:[1,0,0,1,0,0],stack:[],hooks:[],dimensions:[]};legacyTransforms.set(ctx,state);
        function multiply(n){
            if(!state.matrix||n.some(function(v){return !isFinite(v);})){state.matrix=null;return;}
            var m=state.matrix;
            state.matrix=[m[0]*n[0]+m[2]*n[1],m[1]*n[0]+m[3]*n[1],m[0]*n[2]+m[2]*n[3],m[1]*n[2]+m[3]*n[3],m[0]*n[4]+m[2]*n[5]+m[4],m[1]*n[4]+m[3]*n[5]+m[5]];
            if(state.matrix.some(function(v){return !isFinite(v);}))state.matrix=null;
        }
        try{
            ['save','restore','setTransform','resetTransform','transform','translate','scale','rotate'].forEach(function(name){
                var original=ctx[name];if(typeof original!=='function'){if(name!=='resetTransform')throw Error('Missing canvas transform operation');return;}
                var wrapper=function(){
                    var value=original.apply(this,arguments);if(this!==ctx)return value;
                    var a=Array.prototype.slice.call(arguments).map(Number);
                    if(name==='save'){if(state.stack.length>=256){state.matrix=null;state.stack=[];}else state.stack.push(state.matrix&&state.matrix.slice());}
                    else if(name==='restore'){if(state.stack.length)state.matrix=state.stack.pop();}
                    else if(name==='resetTransform')state.matrix=[1,0,0,1,0,0];
                    else if(name==='setTransform')state.matrix=a.length===6&&a.every(isFinite)?a:null;
                    else if(name==='transform'){if(a.length!==6)state.matrix=null;else multiply(a);}
                    else if(name==='translate')multiply([1,0,0,1,a[0],a[1]]);
                    else if(name==='scale')multiply([a[0],0,0,a[1],0,0]);
                    else if(name==='rotate')multiply([Math.cos(a[0]),Math.sin(a[0]),-Math.sin(a[0]),Math.cos(a[0]),0,0]);
                    return value;
                };
                ctx[name]=wrapper;if(ctx[name]!==wrapper)throw Error('Read-only canvas transform');
                state.hooks.push({name:name,wrapper:wrapper});
            });
            ['width','height'].forEach(function(name){
                var owner=canvas,descriptor;
                while(owner&&!(descriptor=Object.getOwnPropertyDescriptor(owner,name)))owner=Object.getPrototypeOf(owner);
                if(!descriptor||typeof descriptor.get!=='function'||typeof descriptor.set!=='function')throw Error('Unobservable canvas reset');
                var get=function(){return descriptor.get.call(this);},set=function(value){descriptor.set.call(this,value);state.matrix=[1,0,0,1,0,0];state.stack=[];};
                Object.defineProperty(canvas,name,{configurable:true,enumerable:descriptor.enumerable,get:get,set:set});
                state.dimensions.push({name:name,get:get,set:set});
            });
        }catch(_){state.matrix=null;}
    }
    function legacyBitmapCanvas(original){return function(){
        var doc=g.document,old=doc&&doc.createElement;
        if((!enabled&&!fontIndependent())||!legacyTransforms||!doc||typeof old!=='function'||g.CanvasRenderingContext2D&&typeof g.CanvasRenderingContext2D.prototype.getTransform==='function')return original.apply(this,arguments);
        var create=function(){var element=old.apply(this,arguments);try{trackFreshCanvas(element);}catch(_){}return element;};
        try{doc.createElement=create;if(doc.createElement!==create)return original.apply(this,arguments);return original.apply(this,arguments);}
        finally{if(doc.createElement===create)doc.createElement=old;}
    };}
    function releaseMessageCanvas(){
        if(messageCanvas)messageCanvas.hooks.forEach(function(h){if(messageCanvas.ctx[h.name]===h.wrapper)messageCanvas.ctx[h.name]=h.original;});
        messageCanvas=null;
    }
    function watchMessageCanvas(page){
        var ctx=page.bitmap&&page.bitmap._context;
        if(messageCanvas&&messageCanvas.ctx===ctx)return;
        releaseMessageCanvas();
        if(!ctx||!messageTransform(ctx)||typeof ctx.setTransform!=='function'||typeof ctx.getImageData!=='function'||typeof ctx.putImageData!=='function'){blockMessagePage(page);return;}
        var watch={ctx:ctx,hooks:[]};messageCanvas=watch;
        ['fillText','strokeText','clearRect','fillRect','strokeRect','drawImage','putImageData','fill','stroke','clip','reset'].forEach(function(name){
            var original=ctx[name];if(typeof original!=='function')return;
            var wrapper=function(){
                if(messageReplaying)return original.apply(this,arguments);
                var current=messagePage,scope=messageDrawScope,op=null;
                if(current&&current.ctx===this){
                    if((name==='fillText'||name==='strokeText')&&scope&&scope.page===current&&!current.blocked){
                        try{
                            var transform=messageTransform(this),styles={};
                            if(!transform)throw Error('Unknown message transform');
                            ['font','fillStyle','strokeStyle','globalAlpha','globalCompositeOperation','lineWidth','lineJoin','miterLimit','textAlign','textBaseline','direction','shadowBlur','shadowColor','shadowOffsetX','shadowOffsetY','filter','fontKerning','fontStretch','fontVariantCaps','letterSpacing','wordSpacing','textRendering'].forEach(function(key){if(key in this)styles[key]=this[key];},this);
                            var args=Array.prototype.slice.call(arguments),nativeFont=messageFont(styles.font,scope.nativeFamily);
                            if(!nativeFont||args.length<3||typeof args[0]!=='string'||!isFinite(Number(args[1]))||!isFinite(Number(args[2])))throw Error('Unsupported message draw');
                            op={original:original,args:args,styles:styles,nativeFont:nativeFont,matrix:[transform.a,transform.b,transform.c,transform.d,transform.e,transform.f]};
                            if(op.matrix.some(function(n){return !isFinite(n);}))throw Error('Invalid message transform');
                        }catch(_){blockMessagePage(current);}
                    }else if(name==='clip'||current.ops.length)blockMessagePage(current);
                    else current.image=null;
                }
                var value;
                try{value=original.apply(this,arguments);}catch(error){blockMessagePage(current);throw error;}
                if(op&&!current.blocked){
                    current.characters+=op.args[0].length;
                    if(current.ops.length>=2048||current.characters>32768)blockMessagePage(current);else current.ops.push(op);
                }
                return value;
            };
            try{ctx[name]=wrapper;if(ctx[name]!==wrapper)throw Error('Read-only canvas');watch.hooks.push({name:name,original:original,wrapper:wrapper});}
            catch(_){blockMessagePage(page);}
        });
    }
    function resetMessagePage(message){
        if(!message||message!==activeMessage()||!message.contents)return;
        var bitmap=message.contents;
        messagePage={owner:message,bitmap:bitmap,ctx:bitmap._context,width:bitmap.width,height:bitmap.height,image:null,ops:[],calls:[],characters:0,blocked:false,fontKey:fontKey(),painted:message._fusionShownText};
        watchMessageCanvas(messagePage);
    }
    function captureMessageDraw(bitmap,original,args){
        var page=messagePage,previous=messageDrawScope;
        if(!page||page.owner!==activeMessage()||page.bitmap!==bitmap||page.owner.contents!==bitmap||page.ctx!==bitmap._context)return original.apply(bitmap,args);
        // The current original page also needs a bounded paint journal so a
        // font chosen before translation can redraw without restarting reveal.
        // This records Canvas operations only, never translation input/cache.
        if(page.blocked)return original.apply(bitmap,args);
        if(page.width!==bitmap.width||page.height!==bitmap.height||!messageCanvas||messageCanvas.hooks.some(function(h){return page.ctx[h.name]!==h.wrapper;})){blockMessagePage(page);return original.apply(bitmap,args);}
        try{
            if(!page.image){
                if(!(page.width>0&&page.height>0)||page.width*page.height*4>8*1024*1024)throw Error('Message image limit');
                page.image=page.ctx.getImageData(0,0,page.width,page.height);
            }
            messageDrawScope={page:page,nativeFamily:String(bitmap.fontFace||'sans-serif')};
            if(!messageReplaying&&page.calls.length<2048){
                var style={};['fontFace','fontSize','fontBold','fontItalic','textColor','outlineColor','outlineWidth','paintOpacity'].forEach(function(k){style[k]=bitmap[k];});
                page.calls.push({args:Array.prototype.slice.call(args),style:style,original:original});
            }
        }catch(_){blockMessagePage(page);}
        try{return original.apply(bitmap,args);}finally{messageDrawScope=previous;}
    }
    function repaintMessageText(message,wanted){
        var page=messagePage;
        // Never call startMessage/updateMessage here: that would replay waits,
        // name-box effects, choices and plugin callbacks. Only finished plain
        // pages whose complete paint journal matches the source are eligible.
        message._fusionTextRedrawPending=true;
        if(message._textState||!page||page.owner!==message||page.bitmap!==message.contents||page.blocked||!page.image||!page.calls.length)return false;
        if(page.width!==page.bitmap.width||page.height!==page.bitmap.height||!messageTransform(page.ctx)||!messageCanvas||messageCanvas.hooks.some(function(h){return page.ctx[h.name]!==h.wrapper;}))return false;
        var before=plainMessage(page.painted),after=plainMessage(wanted),calls=page.calls,first=calls[0],style=JSON.stringify(first.style);
        if(before===null||after===null||!after||calls.map(function(c){return c.args[0];}).join('')!==before.replace(/\n/g,''))return false;
        if(calls.some(function(c){return JSON.stringify(c.style)!==style||(c.args[5]&&c.args[5]!=='left')||c.args.slice(1,5).some(function(x){return !isFinite(Number(x));});}))return false;
        if(page.original&&page.original.text===wanted){
            messageReplaying=true;
            try{page.ctx.putImageData(page.original.image,0,0);page.calls=page.original.calls;page.ops=page.original.ops;page.painted=wanted;page.fontKey=page.original.fontKey;message._fusionShownText=wanted;message._fusionTextRedrawPending=false;if(page.bitmap._setDirty)page.bitmap._setDirty();else if(page.bitmap._baseTexture&&page.bitmap._baseTexture.update)page.bitmap._baseTexture.update();return true;}
            catch(_){return false;}finally{messageReplaying=false;}
        }
        var bitmap=page.bitmap,x=Math.min.apply(null,calls.map(function(c){return c.args[1];})),y=Math.min.apply(null,calls.map(function(c){return c.args[2];}));
        var height=first.args[4],width=bitmap.width-x-4,maxLines=Math.floor((bitmap.height-y)/height),saved={},lines=[],image,oldOps=page.ops;
        if(!(height>0&&width>0&&maxLines>0)||typeof bitmap.measureTextWidth!=='function')return false;
        Object.keys(first.style).forEach(function(k){saved[k]=bitmap[k];bitmap[k]=first.style[k];});
        try{
            after.split('\n').forEach(function(part){
                var line='';Array.from(part).forEach(function(ch){if(line&&bitmap.measureTextWidth(line+ch)>width){lines.push(line);line='';}line+=ch;});lines.push(line);
            });
            if(lines.length>maxLines||lines.some(function(line){return bitmap.measureTextWidth(line)>width;}))return false;
            image=page.ctx.getImageData(0,0,page.width,page.height);
            if(!page.original)page.original={text:page.painted,image:image,calls:calls,ops:oldOps,fontKey:page.fontKey};
            messageReplaying=true;page.ctx.putImageData(page.image,0,0);messageReplaying=false;
            page.calls=[];page.ops=[];page.characters=0;
            lines.forEach(function(line,index){captureMessageDraw(bitmap,first.original,[line,x,y+index*height,width,height,'left']);});
            page.painted=wanted;page.fontKey=fontKey();
            message._fusionShownText=wanted;message._fusionTextRedrawPending=false;
            if(bitmap._setDirty)bitmap._setDirty();else if(bitmap._baseTexture&&bitmap._baseTexture.update)bitmap._baseTexture.update();
            return true;
        }catch(_){page.calls=calls;page.ops=oldOps;if(image)try{messageReplaying=true;page.ctx.putImageData(image,0,0);}catch(ignore){}return false;}
        finally{messageReplaying=false;Object.keys(saved).forEach(function(k){bitmap[k]=saved[k];});}
    }
    function repaintMessageFont(){
        var message=activeMessage(),page=messagePage,key=fontKey();
        messageFontResult={messageRedrawPending:false,messageRedrawn:false};
        if(!message||!g.$gameMessage||!g.$gameMessage.isBusy())return;
        if(!page||page.owner!==message||page.bitmap!==message.contents||page.ctx!==message.contents._context||page.width!==page.bitmap.width||page.height!==page.bitmap.height||page.blocked){messageFontResult.messageRedrawPending=true;return;}
        if(page.fontKey===key)return;
        if(!page.image||!page.ops.length||!messageCanvas||messageCanvas.hooks.some(function(h){return page.ctx[h.name]!==h.wrapper;})){messageFontResult.messageRedrawPending=true;return;}
        var ctx=page.ctx,before;
        try{before=ctx.getImageData(0,0,page.width,page.height);}catch(_){messageFontResult.messageRedrawPending=true;return;}
        messageReplaying=true;
        try{
            ctx.putImageData(page.image,0,0);
            page.ops.forEach(function(op){
                ctx.save();try{
                    Object.keys(op.styles).forEach(function(name){ctx[name]=op.styles[name];});
                    ctx.font=key?messageFont(op.styles.font,JSON.stringify(key)):op.nativeFont;
                    ctx.setTransform.apply(ctx,op.matrix);op.original.apply(ctx,op.args);
                }finally{ctx.restore();}
            });
            if(page.bitmap._setDirty)page.bitmap._setDirty();else if(page.bitmap._baseTexture&&page.bitmap._baseTexture.update)page.bitmap._baseTexture.update();
            page.fontKey=key;messageFontResult.messageRedrawn=true;
        }catch(error){
            try{ctx.putImageData(before,0,0);if(page.bitmap._setDirty)page.bitmap._setDirty();else if(page.bitmap._baseTexture&&page.bitmap._baseTexture.update)page.bitmap._baseTexture.update();}catch(_){}
            throw error;
        }finally{messageReplaying=false;}
    }
    function bitmapHooks(){
        if(!g.Bitmap)return;
        wrap(g.Bitmap.prototype,"_createCanvas",legacyBitmapCanvas);
        // Both drawing and width measurement use this engine helper. Override
        // only for the call, retaining size, outline and the bitmap's own face.
        wrap(g.Bitmap.prototype,"_makeFontNameText",function(original){return function(){
            var family=fontKey(),old=this.fontFace;
            if(!family)return original.apply(this,arguments);
            try{this.fontFace=family;return original.apply(this,arguments);}finally{this.fontFace=old;}
        };});
        ["clear","clearRect","blt","fillRect","gradientFillRect","destroy"].forEach(function(name){wrap(g.Bitmap.prototype,name,function(original){return function(){if(!replaying)forget(this);if(!messageReplaying&&messagePage&&messagePage.bitmap===this){if(name==='clear')resetMessagePage(messagePage.owner);else if(name==='destroy')blockMessagePage(messagePage);}return original.apply(this,arguments);};});});
        wrap(g.Bitmap.prototype,"drawText",function(original){return function(value,x,y,width,height,align){
            if(replaying||(!enabled&&!fontIndependent())||displayDepth||/^[A-Za-z]$/.test(String(value).trim()))return captureMessageDraw(this,original,arguments);
            var args=Array.prototype.slice.call(arguments),translated=text(value,null,true),ctx=this._context,record=null;
            if(valid(value)&&!outputs[value]&&ctx&&typeof ctx.getImageData==="function"&&Number(width)>0&&Number(height)>0){
                // Keep the most recent text draws within a bounded background budget.
                var left=Math.max(0,Math.floor(x)),top=Math.max(0,Math.floor(y));
                var w=Math.min(2048,this.width-left,Math.ceil(width)),h=Math.min(256,this.height-top,Math.ceil(height+8));
                if(w>0&&h>0)try{
                    while(drawings.length&&(drawingBytes+w*h*4>16*1024*1024||drawings.length>=256))drawingBytes-=drawings.shift().bytes;
                    record={bitmap:this,source:value,args:args.slice(),original:original,x:left,y:top,image:ctx.getImageData(left,top,w,h),bytes:w*h*4,styles:{}};
                    ["fontFace","fontSize","fontBold","fontItalic","textColor","outlineColor","outlineWidth","paintOpacity"].forEach(function(key){record.styles[key]=this[key];},this);
                }catch(_){}
            }
            args[0]=translated;var result=captureMessageDraw(this,original,args);
            if(record){record.shown=translated;drawings.push(record);drawingBytes+=record.bytes;}
            return result;
        };});
    }
    function wrapMessageCharacter(original){return function(state){
        // Engines and plugins commonly wrap only at ASCII spaces. Chinese
        // dialogue needs a boundary check before both static and animated glyphs.
        if(enabled&&state&&this._fusionShownText!==this._fusionOriginal&&!this._checkWordWrapMode){
            var ch=state.text.charAt(state.index),left=Number(state.left||0),limit=this.contents&&this.contents.width;
            if(ch&&ch!=="\x1b"&&ch!=="\n"&&ch!=="\f"&&state.x>left&&typeof this.textWidth==="function"){
                if(typeof this.wordwrapWidth==="function")limit=this.wordwrapWidth();
                var width=this.textWidth(ch);
                if(limit>left&&isFinite(width)&&state.x+width>limit){
                    var index=state.index;this.processNewLine(state);state.index=index;
                    // Stock MV processNewLine consumes a newline; this one was
                    // inserted before a character, so leave that character pending.
                    return;
                }
            }
        }
        return original.apply(this,arguments);
    };}
    function redraw(){
        replaying=true;
        try{drawings.forEach(function(d){
            var replacement=enabled?translated(d.source):undefined;
            var wanted=replacement===undefined?d.source:replacement;
            var desiredFont=fontKey();
            if(d.shown===wanted&&(d.fontKey||'')===desiredFont||!d.bitmap._context)return;
            var current={};try{
                Object.keys(d.styles).forEach(function(key){current[key]=d.bitmap[key];d.bitmap[key]=d.styles[key];});
                d.bitmap._context.putImageData(d.image,d.x,d.y);var args=d.args.slice();args[0]=wanted;d.original.apply(d.bitmap,args);d.shown=wanted;d.fontKey=desiredFont;
                if(d.bitmap._setDirty)d.bitmap._setDirty();else if(d.bitmap._baseTexture&&d.bitmap._baseTexture.update)d.bitmap._baseTexture.update();
            }catch(_){}finally{Object.keys(current).forEach(function(key){d.bitmap[key]=current[key];});}
        });}finally{replaying=false;}
    }
    function install() {
        // Constructors exist after plugin loading; boot-scene messages also need hooks.
        if(!g.Window_Base||!g.Window_Message)return false;
        wrap(g.Window_Base.prototype,"drawText",draw);wrap(g.Window_Base.prototype,"drawTextEx",draw);
        // Vanilla MV inherits this method; plugins may replace it on Message.
        // Cover both slots without hiding subsequent base-method replacements.
        wrap(g.Window_Base.prototype,"processNormalCharacter",function(original){return passthrough(wrapMessageCharacter(original));});
        wrap(g.Window_Base.prototype,"flushTextState",passthrough);
        [g.Window_NameInput,g.Window_NameEdit].forEach(function(type){if(type)["refresh","drawChar","drawItem"].forEach(function(name){wrap(type.prototype,name,passthrough);});});
        wrap(g.Window_Message.prototype,"updateMessage",function(original){var call=passthrough(original);return function(){var value=call.apply(this,arguments);if(!this._textState&&this._fusionTextRedrawPending&&g.$gameMessage&&rawAllText&&g.$gameMessage.isBusy())repaintMessageText(this,messageText(rawAllText.call(g.$gameMessage)));return value;};});
        wrap(g.Window_Message.prototype,"processNormalCharacter",wrapMessageCharacter);
        if(g.Window_ChoiceList)["drawText","drawTextEx"].forEach(function(name){if(Object.prototype.hasOwnProperty.call(g.Window_ChoiceList.prototype,name))wrap(g.Window_ChoiceList.prototype,name,draw);});
        if(g.Game_Message&&g.Game_Message.prototype.allText&&g.Game_Message.prototype.allText._fusionHookOwner!==hookOwner)rawAllText=g.Game_Message.prototype.allText;
        wrap(g.Game_Message&&g.Game_Message.prototype,"allText",function(original){return function(){return messageText(original.apply(this,arguments));};});
        wrap(g.Window_Message.prototype,"startMessage",function(original){return function(){
            if(g.$gameMessage&&rawAllText){this._fusionOriginal=rawAllText.call(g.$gameMessage);this._fusionShownText=messageText(this._fusionOriginal);}
            this._fusionTextRedrawPending=false;
            resetMessagePage(this);
            return original.apply(this,arguments);
        };});
        wrap(g.Window_Message.prototype,"newPage",function(original){return function(){var value=original.apply(this,arguments);resetMessagePage(this);return value;};});
        bitmapHooks();return true;
    }
    function refresh(all,changed,fontOnly){
        var list=windows;windows=[];
        if(g.SceneManager&&g.SceneManager._scene&&g.SceneManager._scene._windowLayer)list=list.concat(g.SceneManager._scene._windowLayer.children||[]);
        list.filter(function(w,i){return list.indexOf(w)===i;}).forEach(function(w){try{if(w.visible&&typeof w.refresh==="function"&&w.refresh.length===0&&!(w instanceof g.Window_Message)&&(all||Object.keys(w._fusionTexts||{}).some(function(key){return changed&&changed[key];}))){w._fusionTexts=Object.create(null);w.refresh();}}catch(_){}});
        var message=g.SceneManager&&g.SceneManager._scene&&g.SceneManager._scene._messageWindow;
        if(!fontOnly&&message&&(message._textState||message.pause||(typeof message.isAnySubWindowActive==="function"&&message.isAnySubWindowActive()))&&g.$gameMessage&&rawAllText&&g.$gameMessage.isBusy()){
            var source=rawAllText.call(g.$gameMessage),wanted=messageText(source);
            if(message._fusionShownText!==wanted){
                repaintMessageText(message,wanted);
            }
        }
        redraw();repaintMessageFont();
    }
    function disable(){enabled=false;if(characters)characters.reset(false);characterFacts=Object.create(null);epoch++;queue=[];pending=Object.create(null);refresh(true);if(!fontIndependent()){drawings=[];drawingBytes=0;}return {enabled:false,epoch:epoch};}
    function command(q) {
        if(q.op==="fontPreferences") {
            if(!customFont||!customFont.configure)throw new Error("Font switching unavailable");
            return customFont.configure(q).then(function(receipt){var active=enabled||fontIndependent();return Object.assign(receipt,{enabled:enabled,writebackPending:active,restorePending:active&&q.mode==='default',messageRedrawPending:active&&messageFontResult.messageRedrawPending,messageRedrawn:active&&messageFontResult.messageRedrawn});});
        }
        if(q.op==="translationPrepare") {enabled=false;characterFacts=Object.create(null);epoch++;cache=Object.create(null);templates=Object.create(null);templateBuckets=Object.create(null);outputs=Object.create(null);pending=Object.create(null);retry=Object.create(null);queue=[];return {enabled:false,epoch:epoch,characterRuntimeContract:characters?characters.prepare(q):null};}
        if(q.op==="translationEnable") {if(!q.prepared){epoch++;cache=Object.create(null);templates=Object.create(null);templateBuckets=Object.create(null);outputs=Object.create(null);pending=Object.create(null);retry=Object.create(null);queue=[];}enabled=true;if(install())refresh(true);return {enabled:true,epoch:epoch};}
        if(q.op==="translationDisable")return disable();
        if(q.op==="translationPoll"){if(enabled){var revision=hookRevision;if(install()&&hookRevision!==revision)refresh(true);if(g.$gameMessage&&rawAllText&&g.$gameMessage.isBusy())messageText(rawAllText.call(g.$gameMessage));else if(characters)characters.release('message');}var role=characters?characters.poll(q,compatible):{};return Object.assign({enabled:enabled,epoch:epoch,texts:enabled?queue.splice(0,8):[]},role);}
        if(q.op==="translationRetry") {if(enabled&&q.epoch===epoch&&typeof q.source==="string"){delete pending[q.source];retry[q.source]=Date.now()+30000;}return {accepted:0};}
        if(q.op==="translationApply") {
            if((!enabled&&!q.prime)||q.epoch!==epoch)return {accepted:0,epoch:epoch,stale:true};var count=0,changed=Object.create(null),acceptedPairs=[];
            (Array.isArray(q.entries)?q.entries:[]).slice(0,256).forEach(function(entry){if(entry&&valid(entry.source)&&compatible(entry.source,entry.text)){var normalized=key(entry.source);cache[entry.source]=entry.text;cache[normalized]=key(entry.text);if(/\\[VNP]\[\d+\]|\\G\b/i.test(normalized))registerTemplate(normalized,key(entry.text));changed[entry.source]=changed[normalized]=true;outputs[entry.text]=true;delete pending[entry.source];delete pending[normalized];delete retry[entry.source];acceptedPairs.push({source:entry.source,text:entry.text});count++;}});
            acceptedPairs=acceptedPairs.map(function(pair){return {source:pair.source,text:cache[pair.source]};});
            queue=queue.filter(function(s){return !Object.prototype.hasOwnProperty.call(cache,s);});if(count&&enabled)refresh(false,changed);var receipt={accepted:count,epoch:epoch},proof=storedPairsHash(acceptedPairs);if(proof!==null)receipt.sourcePairsHash=proof;return receipt;
        }
        throw new Error("未知翻译操作。");
    }
    return {command:command,disable:disable,ensureInstalled:install,lookup:function(value){return translated(value)||"";},capture:text};
}
if(typeof window==="undefined"&&typeof module!=="undefined")module.exports=createFusionRpgTranslation;
