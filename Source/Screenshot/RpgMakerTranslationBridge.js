/* Fusion RPG MV/MZ text cache and display hooks. Game data and saves stay original. */
function createFusionRpgTranslation(g) {
    "use strict";
    var enabled=false,cache=Object.create(null),outputs=Object.create(null),pending=Object.create(null),retry=Object.create(null),queue=[],windows=[],hooks=[],epoch=0;
    var drawings=[],drawingBytes=0,replaying=false,rawAllText=null;
    // YEP adds these layout markers at runtime; keep other tags and escape codes exact.
    function key(value){return String(value).replace(/\r\n/g,"\n").replace(/\x1b/g,"\\").replace(/<WordWrap>/gi,"");}
    var templates=Object.create(null);
    function expand(value){
        // The engine may expand variables and actor names before custom windows draw.
        // Resolve source and translated template against the same current game state.
        return value.replace(/\\([VNP])\[(\d+)\]/gi,function(code,kind,id){
            id=Number(id);kind=kind.toUpperCase();
            try{
                if(kind==="V"&&g.$gameVariables)return String(g.$gameVariables.value(id));
                var actor=kind==="N"&&g.$gameActors?g.$gameActors.actor(id):kind==="P"&&g.$gameParty?g.$gameParty.members()[id-1]:null;
                return actor&&actor.name?String(actor.name()):code;
            }catch(_){return code;}
        }).replace(/\\G\b/gi,function(code){return g.TextManager&&g.TextManager.currencyUnit||code;});
    }
    function templateTranslation(value){
        var result;
        Object.keys(templates).some(function(source){
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
    function valid(value){return typeof value==="string"&&value.length>=2&&value.length<=6000&&/[A-Za-z\u3040-\u30ff\u4e00-\u9fff\uac00-\ud7af]/.test(value);}
    function text(value,owner,priority) {
        if(!enabled||!valid(value))return value;
        var found=translated(value);if(found!==undefined)return found;
        if(outputs[value])return value;
        var originalValue=value;value=key(value);
        if(owner){owner._fusionTexts=owner._fusionTexts||Object.create(null);owner._fusionTexts[value]=true;}
        if(Object.prototype.hasOwnProperty.call(cache,value))return cache[value];
        if(owner&&windows.indexOf(owner)<0&&windows.length<100)windows.push(owner);
        if(!pending[value]&&(!retry[value]||retry[value]<Date.now())&&Object.keys(pending).length<256){pending[value]=true;if(priority)queue.unshift(value);else queue.push(value);}
        return originalValue;
    }
    function wrap(proto,name,make) {if(!proto||typeof proto[name]!=="function")return;var original=proto[name];var replacement=make(original);proto[name]=replacement;hooks.push({proto:proto,name:name,original:original,replacement:replacement});}
    function draw(original){return function(value){
        if((g.Window_Message&&this instanceof g.Window_Message)||this._fusionDrawing)return original.apply(this,arguments);
        var a=Array.prototype.slice.call(arguments);a[0]=text(value,this,false);this._fusionDrawing=true;
        try{return original.apply(this,a);}finally{this._fusionDrawing=false;}
    };}
    function forget(bitmap){drawings=drawings.filter(function(d){if(d.bitmap!==bitmap)return true;drawingBytes-=d.bytes;return false;});}
    function bitmapHooks(){
        if(!g.Bitmap)return;
        ["clear","clearRect","blt","fillRect","gradientFillRect","destroy"].forEach(function(name){wrap(g.Bitmap.prototype,name,function(original){return function(){if(!replaying)forget(this);return original.apply(this,arguments);};});});
        wrap(g.Bitmap.prototype,"drawText",function(original){return function(value,x,y,width,height,align){
            if(replaying||!enabled)return original.apply(this,arguments);
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
            args[0]=translated;var result=original.apply(this,args);
            if(record){record.shown=translated;drawings.push(record);drawingBytes+=record.bytes;}
            return result;
        };});
    }
    function redraw(){
        replaying=true;
        try{drawings.forEach(function(d){
            var replacement=enabled?translated(d.source):undefined;
            var wanted=replacement===undefined?d.source:replacement;
            if(d.shown===wanted||!d.bitmap._context)return;
            var current={};try{
                Object.keys(d.styles).forEach(function(key){current[key]=d.bitmap[key];d.bitmap[key]=d.styles[key];});
                d.bitmap._context.putImageData(d.image,d.x,d.y);var args=d.args.slice();args[0]=wanted;d.original.apply(d.bitmap,args);d.shown=wanted;
                if(d.bitmap._setDirty)d.bitmap._setDirty();else if(d.bitmap._baseTexture&&d.bitmap._baseTexture.update)d.bitmap._baseTexture.update();
            }catch(_){}finally{Object.keys(current).forEach(function(key){d.bitmap[key]=current[key];});}
        });}finally{replaying=false;}
    }
    function install() {
        if(hooks.length)return true;
        // Constructors exist after plugin loading; boot-scene messages also need hooks.
        if(!g.Window_Base||!g.Window_Message)return false;
        wrap(g.Window_Base.prototype,"drawText",draw);wrap(g.Window_Base.prototype,"drawTextEx",draw);
        if(g.Window_ChoiceList)["drawText","drawTextEx"].forEach(function(name){if(Object.prototype.hasOwnProperty.call(g.Window_ChoiceList.prototype,name))wrap(g.Window_ChoiceList.prototype,name,draw);});
        rawAllText=g.Game_Message&&g.Game_Message.prototype.allText;
        wrap(g.Game_Message&&g.Game_Message.prototype,"allText",function(original){return function(){return text(original.apply(this,arguments),null,true);};});
        wrap(g.Window_Message.prototype,"startMessage",function(original){return function(){
            if(g.$gameMessage&&rawAllText){this._fusionOriginal=rawAllText.call(g.$gameMessage);this._fusionShownText=text(this._fusionOriginal,null,true);}
            return original.apply(this,arguments);
        };});
        bitmapHooks();return true;
    }
    function refresh(all,changed){
        var list=windows;windows=[];
        if(g.SceneManager&&g.SceneManager._scene&&g.SceneManager._scene._windowLayer)list=list.concat(g.SceneManager._scene._windowLayer.children||[]);
        list.filter(function(w,i){return list.indexOf(w)===i;}).forEach(function(w){try{if(w.visible&&typeof w.refresh==="function"&&!(w instanceof g.Window_Message)&&(all||Object.keys(w._fusionTexts||{}).some(function(key){return changed&&changed[key];}))){w._fusionTexts=Object.create(null);w.refresh();}}catch(_){}});
        var message=g.SceneManager&&g.SceneManager._scene&&g.SceneManager._scene._messageWindow;
        if(message&&(message._textState||message.pause||(typeof message.isAnySubWindowActive==="function"&&message.isAnySubWindowActive()))&&g.$gameMessage&&rawAllText&&g.$gameMessage.isBusy()){
            var source=rawAllText.call(g.$gameMessage),wanted=text(source,null,true);
            if(message._fusionShownText!==wanted){
                var complete=!message._textState,paused=message.pause,wait=message._waitCount,fast=message._showFast;
                var end=message.onEndOfText,ownEnd=Object.prototype.hasOwnProperty.call(message,"onEndOfText");
                message.startMessage();message.pause=false;message._waitCount=0;
                // Repaint a finished dialogue without restarting choices or running end callbacks again.
                if(complete)message.onEndOfText=function(){this._textState=null;};
                try{message._showFast=true;if(typeof message.updateMessage==="function")message.updateMessage();}
                finally{
                    if(complete){if(ownEnd)message.onEndOfText=end;else delete message.onEndOfText;if(!message._textState){message.pause=paused;message._waitCount=wait;}}
                    message._showFast=fast;
                }
            }
        }
        redraw();
    }
    function disable(){enabled=false;epoch++;queue=[];pending=Object.create(null);refresh(true);drawings=[];drawingBytes=0;return {enabled:false,epoch:epoch};}
    function command(q) {
        if(q.op==="translationPrepare") {enabled=false;epoch++;cache=Object.create(null);templates=Object.create(null);outputs=Object.create(null);pending=Object.create(null);retry=Object.create(null);queue=[];return {enabled:false,epoch:epoch};}
        if(q.op==="translationEnable") {if(!q.prepared){epoch++;cache=Object.create(null);templates=Object.create(null);outputs=Object.create(null);pending=Object.create(null);retry=Object.create(null);queue=[];}enabled=true;if(install())refresh(true);return {enabled:true,epoch:epoch};}
        if(q.op==="translationDisable")return disable();
        if(q.op==="translationPoll"){if(enabled&&!hooks.length&&install())refresh(true);return {enabled:enabled,epoch:epoch,texts:enabled?queue.splice(0,8):[]};}
        if(q.op==="translationRetry") {if(enabled&&q.epoch===epoch&&typeof q.source==="string"){delete pending[q.source];retry[q.source]=Date.now()+30000;}return {accepted:0};}
        if(q.op==="translationApply") {
            if((!enabled&&!q.prime)||q.epoch!==epoch)return {accepted:0};var count=0,changed=Object.create(null);
            (Array.isArray(q.entries)?q.entries:[]).slice(0,256).forEach(function(entry){if(valid(entry.source)&&typeof entry.text==="string"&&entry.text.length<=12000){var normalized=key(entry.source);cache[entry.source]=entry.text;cache[normalized]=key(entry.text);if(/\\[VNP]\[\d+\]|\\G\b/i.test(normalized))templates[normalized]=key(entry.text);changed[entry.source]=changed[normalized]=true;outputs[entry.text]=true;delete pending[entry.source];delete pending[normalized];delete retry[entry.source];count++;}});
            queue=queue.filter(function(s){return !Object.prototype.hasOwnProperty.call(cache,s);});if(count&&enabled)refresh(false,changed);return {accepted:count};
        }
        throw new Error("未知翻译操作。");
    }
    return {command:command,disable:disable,lookup:function(value){return translated(value)||"";},capture:text};
}
if(typeof window==="undefined"&&typeof module!=="undefined")module.exports=createFusionRpgTranslation;
