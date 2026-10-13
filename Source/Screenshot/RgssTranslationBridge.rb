# Fusion RGSS bridge. ASCII source, Ruby 1.8/1.9/3.x compatible.
# Only display calls are translated. Database objects, variables and save data
# retain their original values. No eval or Marshal is used for IPC.
if ENV['FUSION_RGSS_SESSION'] && ENV['FUSION_RGSS_SECRET'] && !defined?(FusionRgss157)
module FusionRgss157
  class Json
    def self.dump(v)
      case v
      when String
        '"' + v.gsub(/["\\\x00-\x1f]/) { |c| b=c.unpack('C')[0]; b==34 ? '\\"' : b==92 ? '\\\\' : sprintf('\\u%04x',b) } + '"'
      when Array then '[' + v.collect { |x| dump(x) }.join(',') + ']'
      when Hash then '{' + v.collect { |k,x| dump(k.to_s)+':'+dump(x) }.join(',') + '}'
      when TrueClass then 'true'
      when FalseClass then 'false'
      when NilClass then 'null'
      when Integer then v.to_s
      else raise 'unsupported JSON value'
      end
    end
    def initialize(s); @s=s; @i=0; @nodes=0; end
    def byte; x=@s[@i,1]; x && x.unpack('C')[0]; end
    def ws; @i+=1 while byte && [9,10,13,32].include?(byte); end
    def parse
      v=value(0); ws; raise 'JSON trailing data' if @i!=@s.size; v
    end
    def string
      raise 'JSON string expected' if byte!=34; @i+=1; s=''
      while byte
        b=byte; @i+=1; return s.respond_to?(:force_encoding) ? s.force_encoding('UTF-8') : s if b==34
        raise 'JSON control byte' if b<32
        if b==92
          b=byte; @i+=1
          if b==117
            hex=@s[@i,4]; raise 'JSON escape' unless hex && hex =~ /\A[0-9a-fA-F]{4}\z/; @i+=4; n=hex.to_i(16)
            if n>=0xd800 && n<=0xdbff
              raise 'JSON surrogate' unless @s[@i,2]=='\\u'; @i+=2; hex=@s[@i,4]; raise 'JSON surrogate' unless hex && hex =~ /\A[0-9a-fA-F]{4}\z/; @i+=4; low=hex.to_i(16)
              raise 'JSON surrogate' unless low>=0xdc00 && low<=0xdfff; n=0x10000+(n-0xd800)*1024+low-0xdc00
            elsif n>=0xdc00 && n<=0xdfff then raise 'JSON surrogate'
            end
            s << [n].pack('U')
          else
            c={34=>'"',92=>'\\',47=>'/',98=>"\b",102=>"\f",110=>"\n",114=>"\r",116=>"\t"}[b]; raise 'JSON escape' unless c; s << c
          end
        else s << @s[@i-1,1]
        end
      end
      raise 'JSON truncated string'
    end
    def value(depth)
      @nodes+=1; raise 'JSON limit' if depth>24 || @nodes>20000; ws
      case byte
      when 34 then string
      when 123,91
        object=byte==123; close=object ? 125 : 93; @i+=1; out=object ? {} : []; ws
        if byte==close then @i+=1; return out; end
        loop do
          if object
            ws; k=string; raise 'JSON duplicate key' if out.has_key?(k); ws; raise 'JSON colon' unless byte==58; @i+=1; out[k]=value(depth+1)
          else out << value(depth+1)
          end
          ws; b=byte; @i+=1; break if b==close; raise 'JSON separator' unless b==44
        end
        out
      else
        rest=@s[@i,32]; raise 'JSON missing value' unless rest
        if rest[0,4]=='true' then @i+=4; true
        elsif rest[0,5]=='false' then @i+=5; false
        elsif rest[0,4]=='null' then @i+=4; nil
        elsif rest =~ /\A-?(?:0|[1-9][0-9]*)/ then n=$&; @i+=n.size; n.to_i
        else raise 'JSON value'
        end
      end
    end
  end
  @folder=ENV['FUSION_RGSS_SESSION']; @secret=ENV['FUSION_RGSS_SECRET']; @enabled=ENV['FUSION_RGSS_TRANSLATION']=='1'
  @cache={}; @seen={}; @owners={}; @bytes=0; @epoch=1; @busy=false; @pending=nil; @reply=nil; @last=0; @stopped=false; @dirty=[]; @draws=0; @font_blocked=0; @limited=0
  @mutex=Mutex.new; @font_name=nil; @settling={}
  @font_pool={}; @metrics={}; @changed={}; @invalidate_all=false
  @formatted_alias={}
  def self.sync
    @mutex.lock
    begin yield
    ensure @mutex.unlock
    end
  end
  def self.diagnostic(error)
    path=File.join(@folder,'bridge-error.txt')
    File.open(path,'wb') { |f| f.write((error.class.to_s+': '+error.to_s+"\n"+(error.backtrace || [])[0,8].join("\n"))[0,4096]) }
  rescue StandardError
    nil
  end
  def self.read(name,limit)
    p=File.join(@folder,name); return nil unless File.file?(p) && File.size(p)<=limit
    s=File.open(p,'rb') { |f| f.read(limit+1) }; return nil if s.size>limit
    v=Json.new(s).parse; v.is_a?(Hash) && v['secret']==@secret ? v : nil
  rescue StandardError => error
    diagnostic(error) unless error.is_a?(IOError) || error.is_a?(SystemCallError)
    nil
  end
  def self.write(name,value)
    temp=File.join(@folder,name+'.tmp'); path=File.join(@folder,name)
    File.open(temp,'wb') { |f| f.write(Json.dump(value)) }; File.delete(path) if File.exist?(path); File.rename(temp,path); true
  rescue StandardError
    false
  end
  def self.utf8(s)
    return nil unless s.is_a?(String) && s.size>0 && s.size<=24000 && !s.include?("\0")
    t=s.dup
    if t.respond_to?(:force_encoding)
      t.force_encoding('UTF-8'); t=s.encode('UTF-8') unless t.valid_encoding?
    else t.unpack('U*')
    end
    t
  rescue StandardError
    nil
  end
  def self.capture(s)
    return nil if @stopped
    t=utf8(s); return nil unless t && t.unpack('U*').size>1 && t.size<=24000 && t.unpack('U*').any? { |c| c>=128 || (c>=65 && c<=90) || (c>=97 && c<=122) }
    @seen[t]=true if @seen.size<2048; t
  rescue StandardError
    nil
  end
  def self.text(s)
    source=capture(s); source && @enabled && @cache[source] ? @cache[source] : s
  end
  # Compare complete display paragraphs across uniform color decoration. Do not
  # strip inline formatting, variables, event commands, pictures or timing tags.
  def self.paragraph(s)
    t=utf8(s); return nil unless t
    t=t.sub(/\A(?:(?:\\[cC]|\e[cC])\[[0-9]+\]|<c(?:2|3)?=[0-9a-fA-F,# ]+>)+/,'')
    t=t.sub(/(?:<\/c(?:2|3)?>)+\z/,'')
    return nil if t =~ /[\\<>\x00-\x08\x0b-\x1f]/
    t=t.gsub(/[ \t\r\n]+/,' ').strip
    t.empty? ? nil : t
  end
  def self.cache_pair(source,target)
    @changed[source]=true if @cache[source]!=target
    @cache[source]=target
    plain=paragraph(source); translated=paragraph(target)
    return unless plain && translated
    previous=@formatted_alias[plain]
    # Conflicting decorated entries must not choose a translation arbitrarily.
    if previous==false || (previous && previous[0]!=source && previous[1]!=translated)
      @formatted_alias[plain]=false
    else @formatted_alias[plain]=[source,translated]
    end
    @changed[plain]=true if previous!=@formatted_alias[plain]
  end
  def self.paragraph_translation(source)
    direct=@cache[source]
    return paragraph(direct) if direct
    entry=@formatted_alias[source]; entry ? entry[1] : nil
  end
  def self.bind_formatted(args,chars)
    source=paragraph(args[5]); return chars unless source && source.size<=12000
    return chars unless chars.is_a?(Array) && chars.size>1 && chars.size<=6000 && args[3].is_a?(Numeric) && args[3]>0
    # This is a display-helper contract, not a game-name exception. Preserve
    # every engine character, coordinate and event index. Only attach metadata.
    return chars unless chars.all? { |ch| ch.is_a?(Array) && ch.size>=15 && ch[5]==false && ch[0].is_a?(String) && ch[0].unpack('U*').size==1 && (ch[0] =~ /\s/ || ch.size>=17) && ch[1].is_a?(Numeric) && ch[2].is_a?(Numeric) && ch[3].is_a?(Numeric) && ch[4].is_a?(Numeric) && ch[8].is_a?(Color) }
    return chars if chars.any? { |ch| ch[0] =~ /[\x00-\x09\x0b-\x1f]/ }
    # A clipped measurement is not a complete paragraph; never fit undisplayed
    # source text into the visible prefix or drop it from the cache identity.
    return chars unless chars.collect { |ch| ch[0] }.join.gsub(/\s/,'')==source.gsub(/\s/,'')
    lines=[]; by_y={}; width=0
    chars.each do |ch|
      next if ch[0] =~ /\A\s*\z/
      line=by_y[ch[2]]
      unless line
        line=lines.size; by_y[ch[2]]=line; lines << [ch[1],ch[2],ch[4],0,ch]
      end
      item=lines[line]; item[0]=[item[0],ch[1]].min; item[2]=[item[2],ch[4]].max; item[3]+=1
      width=[width,ch[1]+ch[3]].max
    end
    return chars if lines.empty? || lines.size>64
    binding=[source,lines,[args[3],width-args[1]].min,nil]
    chars.each do |ch|
      line=by_y[ch[2]]
      ch.instance_variable_set(:@fusion162_paragraph,[binding,line]) if line && !(ch[0] =~ /\A\s*\z/)
    end
    capture(source) unless paragraph_translation(source)
    chars
  rescue StandardError
    chars
  end
  def self.paragraph_layout(bitmap,binding)
    return nil unless @enabled
    target=paragraph_translation(binding[0]); return nil unless target && target!=binding[0]
    cjk=target.unpack('U*').any? { |c| c>=128 }; return nil if cjk && !@font_name
    previous=binding[3]; return previous[1] if previous && previous[0]==target
    letters=target.unpack('U*'); lines=binding[1]; total=lines.inject(0) { |n,line| n+line[3] }; used=0; start=0; segments=[]
    # Match original display rows/pages without altering the engine's typewriter
    # indices or page pauses. All cached text is apportioned once, before drawing.
    lines.each_with_index do |line,index|
      used+=line[3]; finish=index==lines.size-1 ? letters.size : (letters.size*used.to_f/total).round
      segments << (letters[start...finish] || []).pack('U*')
      start=finish
    end
    # Keep closing punctuation with the preceding text instead of placing a
    # lone full stop on a later original row/page.
    (1...segments.size).each do |index|
      tail=segments[index].unpack('U*')
      while !tail.empty? && [33,37,41,44,46,58,59,63,93,125,0x2026,0x3001,0x3002,0x300d,0x300f,0x3011,0xff01,0xff09,0xff0c,0xff1a,0xff1b,0xff1f].include?(tail[0])
        segments[index-1] << [tail.shift].pack('U')
      end
      segments[index]=tail.pack('U*')
    end
    saved=font_copy(bitmap.font); result=[]
    begin
      lines.each_with_index do |line,index|
        template=line[4].dup; template[0]=segments[index]; template[1]=line[0]; template[2]=line[1]; template[3]=[binding[2]-(line[0]-lines[0][0])-4,1].max
        template[12]=cjk ? @font_name : template[12]; template[13]=[template[13],line[2]].min
        measure=font_copy(saved); measure.name=template[12]; measure.size=template[13]; measure.bold=template[6]; measure.italic=template[7]
        bitmap.font=measure; bitmap.font.name=measure.name
        while bitmap.font.size>8 && bitmap.text_size(template[0]).width>template[3]
          bitmap.font.size-=1
        end
        if bitmap.text_size(template[0]).width>template[3] then binding[3]=[target,nil]; return nil; end
        template[13]=bitmap.font.size; result << template
      end
    ensure bitmap.font=saved; bitmap.font.name=saved.name
    end
    binding[3]=[target,result]; result
  end
  def self.formatted_emit(bitmap,args,translate)
    receiver,ch=args; info=ch.instance_variable_get(:@fusion162_paragraph); state=@owners[bitmap.object_id]
    layout=translate && info && state ? paragraph_layout(bitmap,info[0]) : nil
    key=info ? [info[0].object_id,info[1]] : nil
    if layout
      painted=state[8] ||= {}; return if painted.has_key?(key) && painted[key]==layout
      painted[key]=layout; draw=layout[info[1]]
    else
      state[8] ||= {} if state
      state[8][key]=nil if state && key
      draw=ch
    end
    saved=font_copy(bitmap.font)
    # The original helper leaves the source glyph's style on the bitmap. Match
    # that state, even when this call paints a cached paragraph with a CJK font.
    saved.name=ch[12]; saved.size=ch[13]; saved.bold=ch[6]; saved.italic=ch[7]; saved.color=ch[8]
    was_busy=@busy; @busy=true
    begin
      # Some MKXP builds retain the Ruby name getter separately from the native
      # font copied by Bitmap#font=. Keep the renderer and getter in agreement.
      font=font_copy(saved); font.name=draw[12]; font.size=draw[13]; font.bold=draw[6]; font.italic=draw[7]
      bitmap.font=font; bitmap.font.name=font.name
      receiver.send(:fusion162_draw_formatted_char,bitmap,draw)
      @draws+=1 if layout
    ensure bitmap.font=saved; bitmap.font.name=saved.name; @busy=was_busy
    end
    state[7]=true if layout
  end
  def self.display_formatted(receiver,bitmap,ch)
    info=ch.is_a?(Array) ? ch.instance_variable_get(:@fusion162_paragraph) : nil
    return receiver.send(:fusion162_draw_formatted_char,bitmap,ch) if !info || @busy || @stopped
    begin
      capture(info[0][0]) unless paragraph_translation(info[0][0])
      args=[receiver,ch.dup]; tracked=track(bitmap,:fusion162_formatted,args)
      state=@owners[bitmap.object_id]; layout=tracked ? paragraph_layout(bitmap,info[0]) : nil
      key=[info[0].object_id,info[1]]; painted=state && state[8]
      if painted && painted.has_key?(key) && painted[key]!=layout
        replay(bitmap,@enabled)
      else formatted_emit(bitmap,args,tracked && @enabled)
      end
    rescue StandardError => error
      fail_display(error,bitmap)
      receiver.send(:fusion162_draw_formatted_char,bitmap,ch)
    end
  end
  def self.font_copy(font)
    f=Font.new; [:name,:size,:bold,:italic,:shadow,:outline,:color,:out_color].each do |k|
      next unless font.respond_to?(k) && f.respond_to?(k.to_s+'=')
      v=font.send(k); v=Color.new(v.red,v.green,v.blue,v.alpha) if v.is_a?(Color); v=v.dup if v.is_a?(Array)
      f.send(k.to_s+'=',v)
    end; f
  end
  def self.font_key(f)
    c=f.color; o=f.respond_to?(:out_color) ? f.out_color : nil
    [f.name,f.size,f.bold,f.italic,c.red,c.green,c.blue,c.alpha,
      f.respond_to?(:outline) ? f.outline : nil,f.respond_to?(:shadow) ? f.shadow : nil,
      o ? [o.red,o.green,o.blue,o.alpha] : nil]
  end
  def self.saved_font(font)
    key=font_key(font)
    @font_pool.clear if @font_pool.size>=512 && !@font_pool.has_key?(key)
    @font_pool[key] ||= font_copy(font)
  end
  def self.queue_all
    @owners.each { |id,state| @dirty << id unless state[6] || @dirty.include?(id) }
  end
  def self.release(bitmap)
    state=@owners.delete(bitmap.object_id); return unless state
    @settling.delete(bitmap.object_id)
    @dirty.delete(bitmap.object_id)
    state[1].dispose if state[1] && !state[1].disposed?; state[2].each { |x| x[3].dispose if x[3] && !x[3].disposed? }; @bytes-=state[3]
  end
  def self.copy_args(args)
    args.collect { |a| a.is_a?(Rect) ? Rect.new(a.x,a.y,a.width,a.height) : a.is_a?(Color) ? Color.new(a.red,a.green,a.blue,a.alpha) : a.is_a?(String) ? a.dup : a }
  end
  def self.copy_surface(src,depth=0)
    raise 'surface copy limit' if depth>=4 || src.disposed? || @owners.size>=128
    original=@owners[src.object_id]; cost=src.width*src.height*4*(original && original[1] ? 2 : 1)
    raise 'surface memory limit' if cost>16*1024*1024 || @bytes+cost>64*1024*1024
    saved=@busy; @busy=true; held=nil
    begin
      held=src.clone
      state=[held,original && original[1] ? original[1].clone : nil,[],cost,nil,{},true]; @owners[held.object_id]=state; @bytes+=cost
      if original
        original[2].each do |row|
          args=copy_args(row[1]); nested=nil
          if row[3] then nested=copy_surface(row[3],depth+1); args[row[0]==:blt ? 2 : 1]=nested; end
          state[2] << [row[0],args,row[2],nested,row[4]]
        end
      end
      held
    rescue StandardError
      held.dispose if held && !held.disposed?; raise
    ensure @busy=saved
    end
  end
  def self.glyph(row)
    return row[4] if row.size>=5
    return nil unless row[0]==:draw_text
    a=row[1]; i=a[0].is_a?(Rect) ? 1 : 4; s=a[i]
    return nil unless s.is_a?(String) && s.unpack('U*').size==1 && (a[i+1] || 0)==0
    r=a[0].is_a?(Rect) ? a[0] : Rect.new(a[0],a[1],a[2],a[3]); f=row[2]
    [s,r,font_key(f)]
  rescue StandardError
    nil
  end
  def self.glyph_runs(bitmap,rows,start,finish,stable)
    runs=[]; lanes={}
    (start...finish).each do |index|
      part=glyph(rows[index]); next unless part
      if part.size>=5
        advance=part[3]; space=part[4]
      else
        bitmap.font=rows[index][2]
        advance=[bitmap.text_size(part[0]).width,1].max
        space=[bitmap.text_size(' ').width,1].max
      end
      # Shadow/outline passes interleave several copies of each glyph. Treat
      # each baseline and paint style as separate tracks, never as repeated text.
      best=nil; distance=nil; gap=0
      key=[part[2],part[1].y]; active=lanes[key] ||= []
      active.each do |run|
        last=run[2]
        # Glyph bounds can differ (for example g/y versus e/o) in bitmap fonts.
        # The font and baseline identify a line; glyph height does not.
        delta=part[1].x-last[1].x
        next unless delta>=[run[3]*0.5,1].max
        extra=delta-run[3]
        next unless extra>=-1 && extra<=space*1.6+1
        # Preserve paint-pass order across skipped space glyphs. Nearest-x
        # matching swaps two shadow lanes at a word gap.
        score=run[4][-1]
        if distance.nil? || score<distance then best=run; distance=score; gap=extra; end
      end
      if best
        best[1] << ' ' if gap>2 && gap>=space*0.7
        best[1] << part[0]; best[2]=part; best[3]=advance; best[4] << index
        best[5]=[best[5],part[1].height].max
      else
        run=[part,part[0].dup,part,advance,[index],part[1].height]
        runs << run; active << run; active.shift if active.size>16
      end
    end
    plans={}; skipped={}
    runs.each do |run|
      first,source,last,advance,indices=run
      next if indices.size<2
      capture(source) if stable
      r=first[1]; width=last[1].x-r.x+advance
      plans[indices[0]]=[r.x,r.y,width,run[5],source]
      indices[1..-1].each { |index| skipped[index]=indices[0] }
    end
    [plans,skipped]
  end
  def self.plan(bitmap)
    state=@owners[bitmap.object_id]; return [{},{},{}] unless state
    return state[4] if state[4]
    plans={}; skipped={}; sources={}; rows=state[2]; i=0
    while i<rows.size
      if rows[i][0]==:draw_text
        finish=i+1; finish+=1 while finish<rows.size && rows[finish][0]==:draw_text
        p,s=glyph_runs(bitmap,rows,i,finish,true)
        plans.update(p); skipped.update(s)
        p.each_value { |args| sources[args[4]]=true }
        (i...finish).each do |n|
          row=rows[n]; next if glyph(row)
          args=row[1]; source=args[args[0].is_a?(Rect) ? 1 : 4]
          sources[source]=true if capture(source)
        end
        i=finish
      elsif rows[i][0]==:fusion162_formatted
        info=rows[i][1][1].instance_variable_get(:@fusion162_paragraph)
        sources[info[0][0]]=true if info; i+=1
      else
        nested=rows[i][3]
        sources.update(plan(nested)[2]) if nested && @owners[nested.object_id]
        i+=1
      end
    end
    state[5]=sources; state[4]=[plans,skipped,sources]
  end
  def self.draw_rows(bitmap,rows,translate,stable)
    i=0; last_font=nil
    all_plans,all_skipped=translate ? plan(bitmap) : [{},{}]
    while i<rows.size
      row=rows[i]
      if row[0]==:draw_text
        finish=i+1; finish+=1 while finish<rows.size && rows[finish][0]==:draw_text
        # Only use a complete paint lane. Never replace cached words inside a
        # longer line or combine independently translated shadow fragments.
        plans={}; skipped={}
        all_plans.each do |n,args|
          next unless n>=i && n<finish && @cache[args[4]]
          plans[n]=args
        end
        if !plans.empty?
          # Recompute membership cheaply from the cached plan's lane owner map.
          all_skipped.each { |n,owner| skipped[n]=true if plans.has_key?(owner) }
        end
        (i...finish).each do |index|
          next if skipped[index]
          item=rows[index]
          if last_font!=item[2] then bitmap.font=item[2]; last_font=item[2]; end
          # A lone character is layout, not a translation request. Whole runs
          # are replaced once per paint layer using their combined rectangle.
          draw(bitmap,plans[index] || item[1],translate && (!glyph(item) || plans[index]))
        end
        i=finish
      elsif row[0]==:fusion162_formatted
        formatted_emit(bitmap,row[1],translate); i+=1
      else
        replay(row[3],translate) if row[3] && @owners[row[3].object_id]
        bitmap.send(row[0],*row[1]); i+=1
      end
    end
  end
  def self.replay(bitmap,translate)
    state=@owners[bitmap.object_id]; return unless state && state[1] && !bitmap.disposed?
    saved=font_copy(bitmap.font); was_busy=@busy; @busy=true
    begin
      bitmap.clear; bitmap.blt(0,0,state[1],Rect.new(0,0,bitmap.width,bitmap.height))
      state[8]={}
      stable=!@settling[bitmap.object_id] || Time.now-@settling[bitmap.object_id]>=0.12
      draw_rows(bitmap,state[2],translate,stable)
      state[7]=translate
    ensure bitmap.font=saved; @busy=was_busy
    end
  end
  def self.track(bitmap,op,args)
    return if @busy || @stopped || bitmap.disposed?
    state=@owners[bitmap.object_id]
    if op==:clear then release(bitmap); return; end
    source=op==:blt ? args[2] : op==:stretch_blt ? args[1] : nil
    return unless op==:draw_text || op==:fusion162_formatted || state || (source && @owners[source.object_id])
    # A continuing typewriter must append to original pixels, not to the
    # previously translated line. This happens once, never once per character.
    if state && state[7] && op!=:fusion162_formatted
      replay(bitmap,false); state[7]=false
    end
    if !state
      cost=bitmap.width*bitmap.height*4
      if cost>16*1024*1024 || @bytes+cost>64*1024*1024 || @owners.size>=128 then @limited+=1; return; end
      @busy=true
      begin base=bitmap.clone
      ensure @busy=false
      end
      state=[bitmap,base,[],cost,nil,{},false,false]; @owners[bitmap.object_id]=state; @bytes+=cost
    end
    copied=copy_args(args)
    held=nil
    if [:blt,:stretch_blt].include?(op)
      index=op==:blt ? 2 : 1
      begin held=copy_surface(copied[index]); copied[index]=held
      rescue StandardError
        held=nil
      end
      unless held
        replay(bitmap,false); release(bitmap); @limited+=1; return
      end
    end
    if state[2].size>=2048
      held.dispose if held; replay(bitmap,false); release(bitmap); @limited+=1; return
    end
    row=[op,copied,op==:draw_text ? saved_font(bitmap.font) : nil,held]
    part=glyph(row)
    if part
      key=[part[2][0,4],part[0]]
      @metrics.clear if @metrics.size>=4096 && !@metrics.has_key?(key)
      advance=@metrics[key] ||= [bitmap.text_size(part[0]).width,1].max
      space_key=[part[2][0,4],' ']
      space=@metrics[space_key] ||= [bitmap.text_size(' ').width,1].max
      part += [advance,space]
    end
    row << part; state[2] << row; state[4]=nil
    if op==:fusion162_formatted
      info=args[1].instance_variable_get(:@fusion162_paragraph)
      state[5][info[0][0]]=true if info
      state[8] ||= {}
      return true
    end
    @settling[bitmap.object_id]=Time.now if op==:draw_text
    @dirty << bitmap.object_id unless @dirty.include?(bitmap.object_id)
    true
  end
  def self.draw(bitmap,args,translate=true)
    return bitmap.fusion157_draw_text(*args) if @stopped
    copy=args.dup; index=copy[0].is_a?(Rect) ? 1 : 4; source=copy[index]; translated=translate && @enabled ? text(source) : source
    if translated.is_a?(String) && translated!=source
      original=font_copy(bitmap.font); f=font_copy(original)
      if translated.unpack('U*').any? { |c| c>=128 }
        if @font_name then f.name=@font_name
        else @font_blocked+=1; return bitmap.fusion157_draw_text(*args)
        end
      end
      rect=copy[0].is_a?(Rect) ? copy[0] : Rect.new(copy[0],copy[1],copy[2],copy[3]); copy[index]=translated
      begin
        bitmap.font=f; f=bitmap.font; f.size=[f.size,rect.height].min if rect.height>=8
        while f.size>8 && bitmap.text_size(translated).width>rect.width && rect.width>0
          f.size-=1
        end
        bitmap.fusion157_draw_text(*copy); @draws+=1
      ensure bitmap.font=original
      end
    else bitmap.fusion157_draw_text(*copy)
    end
  end
  def self.fail_display(error,bitmap=nil)
    diagnostic(error) unless @failure
    @failure='A display operation could not be refreshed; original text is retained.'
    @enabled=false; @cache={}; @formatted_alias={}; @epoch+=1; @dirty=@owners.keys
    if bitmap
      begin replay(bitmap,false)
      rescue StandardError
        nil
      ensure
        begin release(bitmap)
        rescue StandardError
          nil
        end
        @dirty.delete(bitmap.object_id)
      end
    end
    nil
  end
  def self.display_draw(bitmap,args)
    return bitmap.fusion157_draw_text(*args) if @busy || @stopped
    tracked=false
    begin
      i=args[0].is_a?(Rect) ? 1 : 4; capture(args[i]); tracked=track(bitmap,:draw_text,args)
      draw(bitmap,args,tracked && args[i].is_a?(String) && args[i].unpack('U*').size>1)
      @owners[bitmap.object_id][7]=true if tracked && @enabled && @cache[args[i]] && !glyph(@owners[bitmap.object_id][2][-1])
    rescue StandardError => error
      state=@owners[bitmap.object_id]
      state[2].pop if tracked && state
      fail_display(error,bitmap)
      # The game's original operation remains authoritative, including its
      # own argument errors. Only the optional translation step falls back.
      bitmap.fusion157_draw_text(*args)
    end
  end
  def self.mutate(bitmap,op,args)
    return bitmap.send(('fusion157_'+op.to_s).to_sym,*args) if @busy || @stopped
    begin tracked=track(bitmap,op,args)
    rescue StandardError => error
      fail_display(error,bitmap); tracked=false
    end
    source=op==:blt ? args[2] : op==:stretch_blt ? args[1] : nil
    if !tracked && source && @owners[source.object_id]
      begin replay(source,false)
      rescue StandardError => error
        fail_display(error,source)
      end
      begin bitmap.send(('fusion157_'+op.to_s).to_sym,*args)
      ensure
        begin replay(source,@enabled)
        rescue StandardError => error
          fail_display(error,source)
        end
      end
    else bitmap.send(('fusion157_'+op.to_s).to_sym,*args)
    end
  end
  def self.hooks
    formatted=Object.private_method_defined?(:getFormattedText) || Object.method_defined?(:getFormattedText)
    drawing=Object.private_method_defined?(:drawSingleFormattedChar) || Object.method_defined?(:drawSingleFormattedChar)
    if formatted && drawing && !Object.private_method_defined?(:fusion162_get_formatted_text)
      Object.class_eval do
        alias_method :fusion162_get_formatted_text,:getFormattedText
        alias_method :fusion162_draw_formatted_char,:drawSingleFormattedChar
        def getFormattedText(*args)
          chars=fusion162_get_formatted_text(*args)
          FusionRgss157.bind_formatted(args,chars)
        end
        def drawSingleFormattedChar(bitmap,ch)
          FusionRgss157.display_formatted(self,bitmap,ch)
        end
        private :fusion162_get_formatted_text,:fusion162_draw_formatted_char,:getFormattedText,:drawSingleFormattedChar
      end
    end
    if defined?(Window_Base) && Window_Base.method_defined?(:convert_escape_characters) && !Window_Base.method_defined?(:fusion157_convert_escape)
      Window_Base.class_eval do
        alias_method :fusion157_convert_escape,:convert_escape_characters
        def convert_escape_characters(text); FusionRgss157.capture(text); fusion157_convert_escape(text); end
      end
    end
    if defined?(Game_Message) && Game_Message.method_defined?(:all_text) && !Game_Message.method_defined?(:fusion157_all_text)
      Game_Message.class_eval do
        alias_method :fusion157_all_text,:all_text
        def all_text; value=fusion157_all_text; FusionRgss157.capture(value); value; end
      end
    end
  end
  def self.refresh
    deadline=Time.now+0.004
    remaining=@dirty.size
    until @dirty.empty? || remaining<=0 || Time.now>deadline
      remaining-=1
      id=@dirty.shift; state=@owners[id]; next unless state
      if state[0].disposed? then release(state[0]); next; end
      if @enabled && @settling[id] && Time.now-@settling[id]<0.12
        @dirty << id; next
      end
      if @enabled
        sources=plan(state[0])[2]
        sources.each_key { |source| capture(source) unless @formatted_alias[source] }
        replay(state[0],true) if sources.keys.any? { |source| @cache.has_key?(source) || (state[8] && paragraph_translation(source)) }
      elsif state[7] || state[2].any? { |row| row[3] }
        replay(state[0],false)
      end
    end
  end
  def self.dispatch(q)
    result={'epoch'=>@epoch}; ok=true
    case q['op']
    when 'translationPrepare'
      @epoch+=1; @seen={}; unless q['preserveStartup'] then @cache={}; @formatted_alias={}; @enabled=false; end
      @invalidate_all=true; result={'epoch'=>@epoch}
    when 'translationEnable' then @enabled=true; @invalidate_all=true
    when 'translationDisable'
      if @enabled || !@cache.empty? then @enabled=false; @epoch+=1; @cache={}; @formatted_alias={}; @invalidate_all=true; end
      result={'epoch'=>@epoch,'restored'=>@dirty.empty? && !@invalidate_all,'restorePending'=>@dirty.size+(@invalidate_all ? 1 : 0)}
    when 'translationPoll'
      texts=@seen.keys[0,128]; texts.each { |t| @seen.delete(t) }
      result={'epoch'=>@epoch,'enabled'=>@enabled,'texts'=>texts,'ahead'=>[],'writebacksConfirmed'=>@draws,'fontBlockedBindings'=>@font_blocked,'writebackPending'=>@dirty.size,'runtimeWarning'=>@failure || (@limited>0 ? 'Some drawing surfaces exceeded the refresh limit; original text is retained.' : '')}
    when 'translationApply'
      stored=0
      if q['epoch']==@epoch && q['entries'].is_a?(Array)
        q['entries'][0,128].each do |pair|
          next unless pair.is_a?(Hash); source=utf8(pair['source']); target=utf8(pair['text']); next unless source && target && (@cache.has_key?(source) || @cache.size<100000)
          cache_pair(source,target); stored+=1
        end
      end
      result={'epoch'=>@epoch,'stored'=>stored,'stale'=>q['epoch']!=@epoch}
    when 'translationState' then result={'epoch'=>@epoch,'enabled'=>@enabled,'entries'=>@cache.size,'writebacksConfirmed'=>@draws,'fontBlockedBindings'=>@font_blocked,'font'=>@font_name.to_s,'limitedSurfaces'=>@limited}
    else ok=false; result={'message'=>'Unsupported RGSS translation operation.'}
    end
    {'secret'=>@secret,'id'=>q['id'],'ok'=>ok,'result'=>result}
  end
  def self.tick
    return if @stopped
    Thread.pass
    hooks
    if @invalidate_all
      @invalidate_all=false; queue_all
    end
    changes=@changed; @changed={}
    unless changes.empty?
      @owners.each do |id,state|
        next if state[6] || (!state[4] && !state[8])
        @dirty << id if !@dirty.include?(id) && changes.keys.any? { |source| state[5].has_key?(source) }
      end
    end
    @settling.keys.each do |id|
      if Time.now-@settling[id]>=0.12
        @settling.delete(id); @dirty << id unless @dirty.include?(id)
      end
    end
    q=nil; sync { q=@pending; @pending=nil }
    if q
      if q['op']=='stop'
        @enabled=false; @cache={}; @formatted_alias={}; @dirty=@owners.keys; @closing=true
      end
    end
    refresh
    if @closing && @dirty.empty?
      @owners.values.dup.each { |state| release(state[0]) }; @stopped=true
    end
  rescue StandardError => error
    fail_display(error)
    if q && q['id']
      sync { @reply={'secret'=>@secret,'id'=>q['id'],'ok'=>false,'result'=>{'message'=>'RGSS display refresh failed: '+error.class.to_s}} }
    end
  end
  def self.start
    return unless @secret =~ /\A[0-9A-Fa-f]{64}\z/ && File.directory?(@folder)
    ['Noto Sans CJK SC','Microsoft YaHei','SimHei','SimSun'].each { |name| if Font.exist?(name) then @font_name=name; break; end }
    class << Graphics
      alias_method :fusion157_update,:update
      def update; FusionRgss157.tick; fusion157_update; end
    end
    Bitmap.class_eval do
      alias_method :fusion157_draw_text,:draw_text
      def draw_text(*args)
        FusionRgss157.display_draw(self,args)
      end
      [:blt,:stretch_blt,:fill_rect,:gradient_fill_rect,:clear_rect,:clear,:set_pixel,:hue_change,:blur,:radial_blur].each do |op|
        next unless method_defined?(op); alias_method ('fusion157_'+op.to_s).to_sym,op
        define_method(op) { |*args| FusionRgss157.mutate(self,op,args) }
      end
      alias_method :fusion157_dispose,:dispose
      def dispose
        begin FusionRgss157.release(self)
        rescue StandardError => error
          FusionRgss157.fail_display(error)
        ensure fusion157_dispose
        end
      end
    end
    Thread.new do
      begin
      startup=read('startup.json',1024*1024)
      if startup && startup['entries'].is_a?(Hash)
        startup['entries'].each { |s,t| source=utf8(s); target=utf8(t); cache_pair(source,target) if source && target && @cache.size<2000 }
        @dirty=@owners.keys
      end
      hello={'secret'=>@secret,'protocol'=>157,'engine'=>'rgss','pid'=>Process.pid,'ruby'=>RUBY_VERSION,'font'=>@font_name.to_s}
      until @stopped
        write('hello.json',hello) unless @hello
        @hello=true if File.file?(File.join(@folder,'hello.json'))
        reply=nil; sync { reply=@reply }
        if reply && write('response-'+reply['id'].to_s+'.json',reply)
          @last=reply['id']; sync { @reply=nil }
        end
        if read('stop.json',1024)
          sync { @pending={'op'=>'stop'} } unless @closing
        elsif !@reply && !@pending
          candidates=Dir.entries(@folder).select { |n| n =~ /\Arequest-[0-9]{1,10}\.json\z/ && n[8,n.size-13].to_i>@last }
          name=candidates.max { |a,b| a[8,a.size-13].to_i <=> b[8,b.size-13].to_i }
          q=name ? read(name,262144) : nil
          if q && q['id'].is_a?(Integer) && q['id']>@last && q['op'].is_a?(String)
            # IPC and cache receipts do not depend on a focused graphics window.
            # All Bitmap and Font operations remain on the game render thread.
            response=dispatch(q); sync { @reply=response }
          end
        end
        sleep(0.03)
      end
      rescue StandardError => error
        diagnostic(error)
      end
    end
  end
end
begin
  if defined?(Bitmap) && Bitmap.method_defined?(:draw_text) && defined?(Font) && Font.respond_to?(:exist?) && defined?(Graphics) && Graphics.respond_to?(:update)
    FusionRgss157.start
  else
    # Native RGSS creates its graphics classes inside GameMain. Bootstrap at
    # the first Ruby event after those classes exist, before game text draws.
    set_trace_func proc { |event,file,line,id,context,kind|
      if event=='line' && defined?(Bitmap) && Bitmap.method_defined?(:draw_text) && defined?(Font) && Font.respond_to?(:exist?) && defined?(Graphics) && Graphics.respond_to?(:update)
        set_trace_func nil
        begin FusionRgss157.start
        rescue StandardError => error
          FusionRgss157.diagnostic(error)
        end
      end
    }
  end
rescue StandardError => error
  FusionRgss157.diagnostic(error)
  # Original game startup still proceeds if the optional bridge cannot start.
end
end
