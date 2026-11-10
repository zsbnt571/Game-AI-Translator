// Read-through display adapter. Game variables, strings, events and hit boxes stay owned by the game.
function fusion130_read(_path,_max) {
    if (!file_exists(_path)) return undefined;
    var _b=buffer_load(_path);
    if (_b<0) return undefined;
    if (buffer_get_size(_b)>_max) { buffer_delete(_b); return undefined; }
    var _s=buffer_read(_b,buffer_string); buffer_delete(_b);
    try { return json_parse(_s); } catch(_e) { return undefined; }
}
function fusion130_write(_path,_value) {
    var _s=json_stringify(_value);
    var _b=buffer_create(string_byte_length(_s)+1,buffer_fixed,1);
    buffer_write(_b,buffer_string,_s);
    buffer_save_ext(_b,_path+".tmp",0,string_byte_length(_s)); buffer_delete(_b);
    if(file_exists(_path)) file_delete(_path);
    file_rename(_path+".tmp",_path);
    return file_exists(_path);
}
function fusion130_init() {
    if(variable_global_exists("fusion130_state"))return;
    var _root=environment_get_variable("FUSION_GAMEMAKER_SESSION");
    var _secret=environment_get_variable("FUSION_GAMEMAKER_SECRET");
    var _session=environment_get_variable("FUSION_GAMEMAKER_SESSION_ID");
    global.fusion130_state={ready:false, enabled:false, epoch:0, cache:{}, seen:{}, fonts:{}, seenCount:0, cacheCount:0, fontCount:0, nextTick:0, lastId:0, reply:undefined, hello:false, translated:{}, uniqueTranslated:0, fontFailures:0, customPath:"", customCoverage:"", fontFallbacks:{}, fontFallbackCount:0, fontWarning:"", root:_root, secret:_secret, io:"fusion-translation/"+_session+"/"};
    var _s=global.fusion130_state;
    _s.roleEnabled=false;_s.roleSerial=0;_s.roleCursor=0;_s.roleRows=[];_s.rolePending={};_s.roleActive=undefined;_s.roleFacts={};_s.roleFactCount=0;
    if(string_length(_root)==0||string_length(_secret)!=64||string_length(_session)!=32||string_lettersdigits(_session)!=_session)return;
    directory_create(_s.io);
    _s.enabled=environment_get_variable("FUSION_GAMEMAKER_TRANSLATION")=="1";
    _s.independentFont=environment_get_variable("FUSION_GAME_FONT_INDEPENDENT")=="1";
    var _startup=fusion130_read(_root+"/startup.json",33554432);
    if(_s.enabled&&is_struct(_startup)&&variable_struct_exists(_startup,"secret")&&_startup.secret==_secret&&variable_struct_exists(_startup,"entries")&&is_struct(_startup.entries)) {
        var _keys=variable_struct_get_names(_startup.entries);
        for(var _i=0;_i<min(100000,array_length(_keys));_i++) {
            var _k=_keys[_i],_t=variable_struct_get(_startup.entries,_k);
            if(is_string(_t)&&string_length(_k)>0&&string_length(_k)<=6000&&string_length(_t)>0&&string_length(_t)<=12000) {variable_struct_set(_s.cache,_k,_t);_s.cacheCount++;}
        }
    }
    if(_s.enabled||_s.independentFont){
        var _fontStart=fusion130_read(_s.io+"font-startup.json",32768);
        if(is_struct(_fontStart)&&variable_struct_exists(_fontStart,"secret")&&_fontStart.secret==_secret){
            var _fontResult=fusion129_font_preferences(_fontStart);
            if(!_fontResult.accepted)_s.fontWarning="Custom font unavailable for this component; default translation font retained.";
        }
    }
    _s.ready=true;
    _s.hello=fusion130_write(_s.io+"hello.json",{secret:_secret,protocol:130,saveDirectory:game_save_id});
}
// The patcher emits a distinct getter/sink pair only after proving a linear
// value flow into the actual draw argument. Equal or nearby strings alone
// never enter this lane. Neither getter return values nor game variables change.
function fusion135_role_observe(_site,_box,_index,_source,_speaker) {
    fusion130_init();var _s=global.fusion130_state;
    if(!_s.ready||!_s.enabled||!_s.roleEnabled)return;
    variable_struct_remove(_s.rolePending,_site);
    if(!is_string(_source)||!is_string(_speaker)||string_length(_source)<1||string_length(_source)>6000||string_length(_speaker)<1||string_length(_speaker)>200)return;
    variable_struct_set(_s.rolePending,_site,{site:_site,box:_box,index:_index,source:_source,speaker:_speaker,owner:id,kind:event_type,event:event_number});
}
function fusion135_role_enter(_site,_text) {
    var _s=global.fusion130_state;
    if(!_s.ready||!_s.enabled||!_s.roleEnabled||!variable_struct_exists(_s.rolePending,_site))return undefined;
    var _p=variable_struct_get(_s.rolePending,_site);variable_struct_remove(_s.rolePending,_site);
    if(_p.source!=_text||_p.owner!=id||_p.kind!=event_type||_p.event!=event_number)return undefined;
    var _identity="chatterbox:"+string_lower(sha1_string_utf8(_p.speaker))+string_lower(string_copy(md5_string_utf8(_p.speaker),1,24));
    var _row=undefined,_slot=-1;
    for(var _i=0;_i<array_length(_s.roleRows);_i++){
        var _candidate=_s.roleRows[_i];
        if(_candidate.site==_site&&_candidate.box==_p.box&&_candidate.index==_p.index&&_candidate.owner==_p.owner){_row=_candidate;_slot=_i;break;}
    }
    if(!is_struct(_row)||_row.source!=_text||_row.characterId!=_identity){
        if(_slot<0&&array_length(_s.roleRows)>=32)return undefined;
        _s.roleSerial++;
        var _facts=(variable_struct_exists(_s.roleFacts,_identity)?variable_struct_get(_s.roleFacts,_identity):[]);
        _row={site:_site,box:_p.box,index:_p.index,owner:_p.owner,requestId:"gm"+string(_s.roleSerial),source:_text,characterId:_identity,displayName:_p.speaker,evidence:_facts,text:undefined,fallback:false,deadline:current_time+3000,lastSeen:current_time};
        if(_slot<0)array_push(_s.roleRows,_row);else _s.roleRows[_slot]=_row;
        if(string_length(_text)<=600&&(_s.roleFactCount<256||variable_struct_exists(_s.roleFacts,_identity))){
            var _next=[];for(var _j=max(0,array_length(_facts)-2);_j<array_length(_facts);_j++)array_push(_next,_facts[_j]);
            if(array_length(_next)==0||_next[array_length(_next)-1]!=_text)array_push(_next,_text);
            if(!variable_struct_exists(_s.roleFacts,_identity))_s.roleFactCount++;
            variable_struct_set(_s.roleFacts,_identity,_next);
        }
    }
    _row.lastSeen=current_time;return _row;
}
function fusion135_role_text_valid(_text) {
    if(!is_string(_text)||string_length(_text)<1||string_length(_text)>12000)return false;
    // In native runners chr(0) can be the empty string. Searching for it would
    // reject every valid reply; inspect actual characters instead.
    for(var _i=1;_i<=string_length(_text);_i++)if(ord(string_char_at(_text,_i))==0)return false;
    return true;
}
function fusion135_role_poll(_q) {
    var _s=global.fusion130_state,_live=[],_requests=[],_accepted=[];
    for(var _i=0;_i<array_length(_s.roleRows);_i++)if(current_time-_s.roleRows[_i].lastSeen<=2000)array_push(_live,_s.roleRows[_i]);
    _s.roleRows=_live;
    if(_s.enabled&&_s.roleEnabled&&variable_struct_exists(_q,"characterRuntimeContract")&&_q.characterRuntimeContract=="fusion-character-runtime-1"&&variable_struct_exists(_q,"characterResults")&&is_array(_q.characterResults)){
        for(var _i=0;_i<min(16,array_length(_q.characterResults));_i++){
            var _r=_q.characterResults[_i];
            if(!is_struct(_r)||!variable_struct_exists(_r,"requestId")||!variable_struct_exists(_r,"source")||!variable_struct_exists(_r,"characterId"))continue;
            for(var _j=0;_j<array_length(_live);_j++){
                var _row=_live[_j];
                if(_r.requestId!=_row.requestId||_r.source!=_row.source||_r.characterId!=_row.characterId)continue;
                if(variable_struct_exists(_r,"pending")&&_r.pending){_row.deadline=current_time+3000;}
                else if(variable_struct_exists(_r,"fallback")&&_r.fallback){_row.text=undefined;_row.fallback=true;}
                else if(variable_struct_exists(_r,"text")&&fusion135_role_text_valid(_r.text)){_row.text=_r.text;_row.fallback=false;}
                else continue;
                array_push(_accepted,_row.requestId);break;
            }
        }
    }
    if(_s.enabled&&_s.roleEnabled)for(var _i=0;_i<min(16,array_length(_live));_i++){
        var _row=_live[(_s.roleCursor+_i) mod array_length(_live)];array_push(_requests,{requestId:_row.requestId,source:_row.source,characterId:_row.characterId,displayName:_row.displayName,evidence:_row.evidence});
    }
    _s.roleCursor=(array_length(_live)>0?(_s.roleCursor+array_length(_requests)) mod array_length(_live):0);
    return {characterRuntimeContract:(_s.roleEnabled?"fusion-character-runtime-1":undefined),characterAccepted:_accepted,characterRequests:_requests};
}
function fusion130_release_fonts() {
    var _s=global.fusion130_state,_names=variable_struct_get_names(_s.fonts);
    for(var _j=0;_j<array_length(_names);_j++){var _owned=variable_struct_get(_s.fonts,_names[_j]);if(_owned.id>=0)font_delete(_owned.id);}
    _s.fonts={};_s.fontCount=0;_s.layouts={};_s.layoutCount=0;
}
function fusion129_font_room() {
    var _s=global.fusion130_state;
    if(_s.fontCount<32)return;
    var _keys=variable_struct_get_names(_s.fonts),_oldest="",_age=infinity;
    for(var _i=0;_i<array_length(_keys);_i++){
        var _entry=variable_struct_get(_s.fonts,_keys[_i]);
        if(_entry.id!=draw_get_font()&&_entry.used<_age){_age=_entry.used;_oldest=_keys[_i];}
    }
    if(_oldest!=""){
        var _entry=variable_struct_get(_s.fonts,_oldest);if(_entry.id>=0)font_delete(_entry.id);
        variable_struct_remove(_s.fonts,_oldest);_s.fontCount--;_s.layouts={};_s.layoutCount=0;
    }
}
// File names and cmap evidence are prepared by the host from verified bytes.
// An unsupported glyph uses the previous default translation route per string;
// it never turns a translated string back into its original language.
function fusion129_font_covers(_coverage,_text) {
    if(string_length(_coverage)!=16384)return false;
    for(var _i=1;_i<=string_length(_text);_i++){
        var _code=ord(string_char_at(_text,_i));
        if(_code<0||_code>65535)return false;
        var _hex=string_pos(string_char_at(_coverage,1+floor(_code/4)),"0123456789ABCDEF")-1;
        if(_hex<0||(_hex&(1<<(_code mod 4)))==0)return false;
    }
    return true;
}
function fusion129_font_fallback(_source) {
    var _s=global.fusion130_state;
    if(_s.fontFallbackCount<4096&&!variable_struct_exists(_s.fontFallbacks,_source)){
        variable_struct_set(_s.fontFallbacks,_source,true);_s.fontFallbackCount++;
    }
}
function fusion129_font_preferences(_q) {
    var _s=global.fusion130_state,_mode=(variable_struct_exists(_q,"mode")?_q.mode:"");
    var _bad={accepted:false,error:"Invalid custom font preference."};
    if(_mode!="default"&&_mode!="custom")return _bad;
    var _path="",_coverage="";
    if(_mode=="custom"){
        if(!variable_struct_exists(_q,"file")||!is_string(_q.file)||string_length(_q.file)!=68||
           !variable_struct_exists(_q,"coverage")||!is_string(_q.coverage)||string_length(_q.coverage)!=16384)return _bad;
        var _extension=string_copy(_q.file,65,4);
        if(_extension!=".ttf"&&_extension!=".otf")return _bad;
        for(var _i=1;_i<=64;_i++)if(string_pos(string_char_at(_q.file,_i),"0123456789ABCDEF")==0)return _bad;
        for(var _i=1;_i<=16384;_i++)if(string_pos(string_char_at(_q.coverage,_i),"0123456789ABCDEF")==0)return _bad;
        _path=_s.io+"fonts/"+_q.file;_coverage=_q.coverage;
        var _probe=-1;
        try { if(file_exists(_path))_probe=font_add(_path,16,false,false,32,128); } catch(_e) { _probe=-1; }
        if(_probe<0)return {accepted:false,error:"The running GameMaker engine could not load the selected font. The previous preference was retained."};
        font_delete(_probe);
    }
    if(_s.customPath!=_path||_s.customCoverage!=_coverage||_s.fontFallbackCount>0||_s.fontFailures>0)fusion130_release_fonts();
    _s.customPath=_path;_s.customCoverage=_coverage;_s.fontFallbacks={};_s.fontFallbackCount=0;_s.fontFailures=0;_s.fontWarning="";
    _s.independentFont=variable_struct_exists(_q,"independent")&&_q.independent==true;
    return {accepted:true,mode:_mode,enabled:_s.enabled,independent:_s.independentFont,writebackPending:_s.enabled||_s.independentFont,restorePending:(_s.enabled||_s.independentFont)&&_mode=="default",restartRequired:false};
}
// Keep the engine's automatic composition, cameras, viewports and custom
// surfaces. Only the automatic application target gets more raster pixels.
function fusion149_surface_tick() {
    var _s=global.fusion130_state;
    if(!fusion149_auto_surface()||!surface_exists(application_surface))return;
    var _active=_s.ready&&_s.enabled&&application_surface_is_enabled();
    var _views=0;
    if(view_enabled){for(var _i=0;_i<8;_i++)if(view_visible[_i]){_views++;if(view_surface_id[_i]!=-1)_active=false;}}
    if(_views>1)_active=false;
    if(!variable_struct_exists(_s,"raster")){
        if(!_active)return;
        _s.raster={id:application_surface,width:surface_get_width(application_surface),height:surface_get_height(application_surface),targetWidth:surface_get_width(application_surface),targetHeight:surface_get_height(application_surface),guiWidth:display_get_gui_width(),guiHeight:display_get_gui_height(),pinned:false};
    }
    var _r=_s.raster;
    // The runner may replace the handle on fullscreen/room changes while
    // retaining our dimensions. Do not adopt those dimensions as the baseline.
    if(_r.id!=application_surface)_r.id=application_surface;
    var _factor=1;
    if(_active){
        var _pos=application_get_position();
        _factor=clamp(ceil(min((_pos[2]-_pos[0])/max(1,_r.width),(_pos[3]-_pos[1])/max(1,_r.height))),1,8);
        while(_factor>1&&(_r.width*_factor>4096||_r.height*_factor>4096||_r.width*_r.height*_factor*_factor>8388608))_factor--;
    }
    var _w=_r.width*_factor,_h=_r.height*_factor;
    if(_w!=_r.targetWidth||_h!=_r.targetHeight){
        if(!_r.pinned&&_factor>1&&fusion149_auto_gui()){display_set_gui_size(_r.guiWidth,_r.guiHeight);_r.pinned=true;}
        surface_resize(application_surface,_w,_h);_r.targetWidth=_w;_r.targetHeight=_h;
    }
    // Surface resize is deferred by the runner. Restore automatic GUI size only
    // after the original target dimensions are actually back in use.
    if(_factor==1&&_r.pinned&&surface_get_width(application_surface)==_r.width&&surface_get_height(application_surface)==_r.height){display_set_gui_size(-1,-1);_r.pinned=false;}
}
function fusion149_surface_density() {
    if(!surface_exists(application_surface)||surface_get_target()!=application_surface)return 1;
    var _w=room_width,_h=room_height,_count=0;
    if(view_enabled){
        for(var _i=0;_i<8;_i++)if(view_visible[_i]){_count++;if(view_surface_id[_i]!=-1)return 1;}
        if(_count!=1||view_current<0||view_camera[view_current]<0)return 1;
        _w=camera_get_view_width(view_camera[view_current]);_h=camera_get_view_height(view_camera[view_current]);
    }
    return clamp(ceil(min(surface_get_width(application_surface)/max(1,_w),surface_get_height(application_surface)/max(1,_h))),1,8);
}
function fusion149_draw_surface(_surface,_x,_y) {
    var _s=global.fusion130_state;
    if(_surface==application_surface&&variable_struct_exists(_s,"raster")){
        var _r=_s.raster;
        if(surface_get_width(_surface)!=_r.width||surface_get_height(_surface)!=_r.height)
            return draw_surface_stretched(_surface,_x,_y,_r.width,_r.height);
    }
    return draw_surface(_surface,_x,_y);
}
function fusion130_tick() {
    var _s=global.fusion130_state;
    if(!_s.ready||current_time<_s.nextTick)return;
    _s.nextTick=current_time+50;
    if(file_exists(_s.io+"stop")){_s.enabled=false;_s.ready=false;_s.cache={};_s.cacheCount=0;fusion130_release_fonts();return;}
    if(!_s.hello){_s.hello=fusion130_write(_s.io+"hello.json",{secret:_s.secret,protocol:130,saveDirectory:game_save_id});return;}
    if(is_struct(_s.reply)){
        if(fusion130_write(_s.io+"response-"+string(_s.reply.id)+".json",_s.reply)){_s.lastId=_s.reply.id;_s.reply=undefined;}
        return;
    }
    var _q=fusion130_read(_s.io+"request.json",262144);
    if(!is_struct(_q)||!variable_struct_exists(_q,"secret")||_q.secret!=_s.secret||!variable_struct_exists(_q,"id")||_q.id<=_s.lastId||!variable_struct_exists(_q,"op"))return;
    var _r={epoch:_s.epoch},_ok=true,_error="";
    switch(_q.op) {
        case "fontPreferences":
            _r=fusion129_font_preferences(_q);_ok=_r.accepted;
            if(!_ok)_error=_r.error;
            break;
        case "translationPrepare":
            _s.epoch++;
            if(!variable_struct_exists(_q,"preserveStartup")||!_q.preserveStartup){_s.cache={};_s.cacheCount=0;_s.enabled=false;}
            _s.roleEnabled=variable_struct_exists(_q,"characterRuntimeContract")&&_q.characterRuntimeContract=="fusion-character-runtime-1";_s.roleCursor=0;_s.roleRows=[];_s.rolePending={};_s.roleActive=undefined;_s.roleFacts={};_s.roleFactCount=0;
            _s.seen={};_s.seenCount=0;_r={epoch:_s.epoch,characterRuntimeContract:(_s.roleEnabled?"fusion-character-runtime-1":undefined)}; break;
        case "translationEnable": _s.enabled=true; break;
        case "translationDisable":
            _s.enabled=false;_s.epoch++;_s.cache={};_s.cacheCount=0;
            _s.roleEnabled=false;_s.roleRows=[];_s.rolePending={};_s.roleActive=undefined;
            fusion130_release_fonts();_r={epoch:_s.epoch};break;
        case "translationPoll":
            var _roles=fusion135_role_poll(_q);
            var _texts=variable_struct_get_names(_s.seen),_out=[];
            for(var _i=0;_i<min(128,array_length(_texts));_i++){array_push(_out,_texts[_i]);variable_struct_remove(_s.seen,_texts[_i]);_s.seenCount--;}
            _r={epoch:_s.epoch,texts:_out,ahead:[],runtimeWarning:(_s.fontWarning!=""?_s.fontWarning:(_s.fontFallbackCount>0?"Custom font unavailable for this component; default translation font retained.":(_s.fontFailures>0?"部分字体准备失败，已保留原文。":""))),displaySources:_s.uniqueTranslated,fontCount:_s.fontCount,fontFallbackSources:_s.fontFallbackCount,characterRuntimeContract:_roles.characterRuntimeContract,characterAccepted:_roles.characterAccepted,characterRequests:_roles.characterRequests}; break;
        case "translationApply":
            var _n=0;
            if(!variable_struct_exists(_q,"epoch")||_q.epoch!=_s.epoch){_r={epoch:_s.epoch,stale:true,stored:0};break;}
            if(variable_struct_exists(_q,"entries")&&is_array(_q.entries)){
                for(var _i=0;_i<min(128,array_length(_q.entries));_i++){
                    var _p=_q.entries[_i];
                    if(is_struct(_p)&&variable_struct_exists(_p,"source")&&variable_struct_exists(_p,"text")&&is_string(_p.source)&&is_string(_p.text)&&string_length(_p.source)>0&&string_length(_p.source)<=6000&&string_length(_p.text)>0&&string_length(_p.text)<=12000){
                        if(variable_struct_exists(_s.cache,_p.source)||_s.cacheCount<100000){if(!variable_struct_exists(_s.cache,_p.source))_s.cacheCount++;variable_struct_set(_s.cache,_p.source,_p.text);_n++;}
                    }
                }
            }
            _r={epoch:_s.epoch,stored:_n};break;
        case "translationState":_r={epoch:_s.epoch,enabled:_s.enabled,entries:_s.cacheCount,displaySources:_s.uniqueTranslated,fontCount:_s.fontCount,fontFailures:_s.fontFailures,fontFallbackSources:_s.fontFallbackCount,customFont:_s.customPath!=""};break;
        default: _ok=false; break;
    }
    _s.reply={id:_q.id,secret:_s.secret,ok:_ok,result:_r,error:_error};
    if(fusion130_write(_s.io+"response-"+string(_q.id)+".json",_s.reply)){_s.lastId=_q.id;_s.reply=undefined;}
}
function fusion136_wrap(_text,_width) {
    if(_width<=0)return _text;
    var _out="",_line="",_lastSpace=0;
    for(var _i=1;_i<=string_length(_text);_i++){
        var _c=string_char_at(_text,_i);
        if(_c=="\r")continue;
        if(_c=="\n"){_out+=_line+"\n";_line="";_lastSpace=0;continue;}
        if(string_length(_line)>0&&string_width(_line+_c)>_width){
            if(_lastSpace>0){_out+=string_copy(_line,1,_lastSpace-1)+"\n";_line=string_delete(_line,1,_lastSpace);}
            else {
                // Keep closing punctuation with a preceding character, and
                // opening punctuation with its following character. Do not
                // leave a Chinese full stop by itself on the next line.
                var _tail=string_char_at(_line,string_length(_line));
                var _carry=string_length(_line)>1&&string_width(_tail+_c)<=_width&&(string_pos(_c,"。，、！？：；）］｝】》〉」』”’％,.;:!?%)]}")>0||string_pos(_tail,"（［｛【《〈「『“‘([{")>0);
                if(_carry){_out+=string_copy(_line,1,string_length(_line)-1)+"\n";_line=_tail;}
                else {_out+=_line+"\n";_line="";}
            }
            _lastSpace=0;
        }
        if(_c!=" "||string_length(_line)>0){_line+=_c;if(_c==" ")_lastSpace=string_length(_line);}
    }
    return _out+_line;
}
function fusion130_string_copy(_source,_start,_length) {
    var _result=string_copy(_source,_start,_length);
    fusion130_init();var _s=global.fusion130_state;
    // Runners clamp a zero start to the first character. Observe only when the
    // actual result agrees with the first-character slice; keep the game result.
    if(_s.ready&&_s.enabled&&is_string(_source)&&(_start==0||_start==1)&&_result==string_copy(_source,1,_length)&&_length>0&&_length<string_length(_source)&&string_length(_source)<=6000&&string_pos(" ",_source)>0){
        if(!variable_struct_exists(_s,"slices")||current_time>_s.sliceExpiry){_s.slices={};_s.sliceCount=0;_s.sliceExpiry=current_time+250;}
        if(_s.sliceCount<128){
            if(!variable_struct_exists(_s.slices,_result)){variable_struct_set(_s.slices,_result,_source);_s.sliceCount++;}
            else if(variable_struct_get(_s.slices,_result)!=_source)variable_struct_set(_s.slices,_result,"");
        }
    }
    return _result;
}
function fusion136_reveal(_layout,_visible,_full) {
    if(_visible==_full)return _layout;
    var _result=[_layout[0],_layout[1],_layout[2],_layout[3],_layout[4],_layout[5],_layout[6]];
    _result[0]=string_copy(_layout[0],1,floor(string_length(_layout[0])*string_length(_visible)/max(1,string_length(_full))));
    return _result;
}
// Font textures are generated at higher resolution and drawn back at their
// intended size. Game-owned transforms, measured logic strings and hit boxes
// remain untouched. Ink metrics are derived from the game's font atlas.
function fusion136_layout(_text,_width,_sep,_bounded) {
    // Some games deactivate room helpers during dialogue; the actual drawing
    // path must still service target restoration and display size changes.
    fusion130_init(); fusion130_tick(); fusion149_surface_tick();
    var _old=draw_get_font(),_s=global.fusion130_state;
    var _unchanged=[_text,_old,1,_sep,_width,false];
    var _independent=_s.independentFont&&_s.customPath!="";
    if(!_s.ready||(!_s.enabled&&!_independent)||!is_string(_text)||string_length(_text)==0||string_length(_text)>6000)return _unchanged;
    var _full=_text;
    var _role=(_s.enabled&&_s.roleEnabled&&is_struct(_s.roleActive)&&_s.roleActive.source==_text?_s.roleActive:undefined);
    if(!is_struct(_role)&&_s.enabled&&variable_struct_exists(_s,"slices")&&current_time<=_s.sliceExpiry&&variable_struct_exists(_s.slices,_text)){
        var _candidate=variable_struct_get(_s.slices,_text);if(string_length(_candidate)>string_length(_text))_full=_candidate;
    }
    if(!_independent&&string_length(_full)==1&&string_letters(_full)==_full)return _unchanged;
    if(_s.enabled&&(!is_struct(_role)||_role.fallback||current_time>_role.deadline)&&_s.seenCount<4096&&!variable_struct_exists(_s.seen,_full)){variable_struct_set(_s.seen,_full,true);_s.seenCount++;}
    var _out=(_s.enabled&&variable_struct_exists(_s.cache,_full)?variable_struct_get(_s.cache,_full):_full);
    if(is_struct(_role)&&is_string(_role.text))_out=_role.text;
    if(_out==_full&&!_independent)return _unchanged;
    // Missing glyphs in an original-language custom draw retain the game's
    // authored font; do not silently replace it with the translation fallback.
    if(_out==_full&&!fusion129_font_covers(_s.customCoverage,_out))return _unchanged;
    var _sourceWidth=string_width(_full),_lineHeight=string_height("M");
    var _ink=fusion136_font_ink(font_get_name(_old));
    // GUI events draw directly to the back buffer. A low-resolution Latin
    // atlas is not evidence that translated CJK must have the same pixel grid.
    // Ordinary draws also need enough pixels in their application surface;
    // a larger font atlas alone loses its strokes in a small application target.
    var _gui=event_type==ev_draw&&(event_number==64||event_number==72||event_number==73)&&surface_get_target()==-1;
    var _slot=(_bounded&&_sep>0?_sep:_lineHeight),_targetInk=_ink;
    var _density=(_gui?1:fusion149_surface_density()),_dense=_gui||_density>1;
    // Raster density supplies sharp strokes; compact labels do not need the
    // paragraph-sized em. Leave room above/below for the game's outline draws
    // instead of filling the whole line cell. GUI typography stays unchanged.
    var _readableInk=(_gui?14:(_dense?(_bounded?12:10):11));
    var _padding=(!_gui&&_dense?3:1);
    if(_ink>0&&_ink<_readableInk&&_slot>=10)_targetInk=max(_ink,min(_readableInk,_slot-_padding));
    var _pixel=fusion147_font_pixel(font_get_name(_old));
    var _sample=(_dense?_density:(_pixel?1:3));
    if(_gui){
        var _gw=display_get_gui_width(),_gh=display_get_gui_height();
        if(_gw>0&&_gh>0)_sample=clamp(ceil(max(window_get_width()/_gw,window_get_height()/_gh)),2,8);
    }
    var _smooth=!_pixel||_dense;
    var _defaultPath=program_directory+".fusion-gamemaker/bridge-font.otf",_fontPath=_defaultPath;
    if(_s.customPath!=""){
        if(fusion129_font_covers(_s.customCoverage,_out))_fontPath=_s.customPath;
        else fusion129_font_fallback(_full);
    }
    var _key=string(_old)+"|"+string(_targetInk)+"|"+string(_sample)+"|"+string(_slot)+"|"+string(_smooth)+"|"+_fontPath,_entry=undefined;
    if(variable_struct_exists(_s.fonts,_key))_entry=variable_struct_get(_s.fonts,_key);
    else{
        // More than 32 size/style combinations must not permanently suppress
        // subsequent translations. Evict an unused owned font, never a game font.
        fusion129_font_room();
        if(_s.fontCount>=32)return _unchanged;
        var _info=undefined,_size=16,_bold=false,_italic=false;
        // The built-in default draw font is -1 and has no font resource info.
        // It is still a valid original draw; retain the existing size fallback.
        try{_info=font_get_info(_old);}catch(_e){_info=undefined;}
        if(is_struct(_info)){_size=clamp(_info.size,6,96);_bold=_info.bold;_italic=_info.italic;}
        if(_targetInk>0)_size=clamp(_targetInk/0.92,6,96);
        var _aa=font_add_get_enable_aa();font_add_enable_aa(_smooth);
        var _points=round(_size*_sample);
        var _font=-1;
        try{_font=font_add(_fontPath,_points,_bold,_italic,32,128);}catch(_e){_font=-1;}
        if(_font<0&&_fontPath!=_defaultPath&&_out!=_full){
            fusion129_font_fallback(_full);_fontPath=_defaultPath;
            try{_font=font_add(_fontPath,_points,_bold,_italic,32,128);}catch(_e){_font=-1;}
        }
        if((_sample==1||_dense)&&_font>=0&&_targetInk>0&&_fontPath==_defaultPath){
            // Measure the em at raster resolution, then keep the original
            // logical line spacing. GUI glyphs never pass through the small
            // application surface and preserve strokes at the window scale.
            for(var _fit=0;_fit<2;_fit++){
                draw_set_font(_font);var _em=string_width("国");draw_set_font(_old);
                if(_em<=_targetInk*_sample||_points<=6)break;
                font_delete(_font);_points=max(6,floor(_points*_targetInk*_sample/max(1,_em)));
                _font=font_add(_defaultPath,_points,_bold,_italic,32,128);
                if(_font<0)break;
            }
        }
        font_add_enable_aa(_aa);
        _entry={id:_font,scale:1/_sample,used:current_time};
        variable_struct_set(_s.fonts,_key,_entry);_s.fontCount++;
        if(_font<0)_s.fontFailures++;
    }
    if(!is_struct(_entry)||_entry.id<0)return _unchanged;
    _entry.used=current_time;
    draw_set_font(_entry.id);
    if(!variable_struct_exists(_s,"layouts")){_s.layouts={};_s.layoutCount=0;}
    var _layoutKey=_key+"|"+string(_width)+"|"+string(_sep)+"|"+string(_bounded)+"|"+_full+"|"+_out;
    if(variable_struct_exists(_s.layouts,_layoutKey))return fusion136_reveal(variable_struct_get(_s.layouts,_layoutKey),_text,_full);
    var _scale=_entry.scale;
    // Plain labels have no box-width argument. Keep them within the source label
    // width; long paragraphs use their explicit width and retain readable height.
    if(!_bounded&&string_length(_full)<=80&&string_pos("\n",_full)==0&&_sourceWidth>0)
        _scale*=min(1,_sourceWidth/max(1,string_width(_out)*_scale));
    if(_bounded&&_width>0)_out=fusion136_wrap(_out,_width/_scale);
    var _result=[_out,_old,_scale,(_sep<0?_lineHeight:_sep)/_scale,(_width>0?_width/_scale:_width),true,_smooth];
    if(_s.layoutCount>=512){_s.layouts={};_s.layoutCount=0;}
    variable_struct_set(_s.layouts,_layoutKey,_result);_s.layoutCount++;
    if(_s.uniqueTranslated<100000&&!variable_struct_exists(_s.translated,_full)){variable_struct_set(_s.translated,_full,true);_s.uniqueTranslated++;}
    return fusion136_reveal(_result,_text,_full);
}
