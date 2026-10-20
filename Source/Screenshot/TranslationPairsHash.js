/* Fusion content receipt, usable in browser-only and Node renderers.
 * SHA256 of count + ordinal UTF16-sorted pairs, each UTF8 string length-framed
 * with a little-endian uint32. This is cache identity, never pixel proof. */
function fusionSourcePairsHash(entries) {
    'use strict';
    if (!Array.isArray(entries) || entries.length > 256) throw Error('Receipt pair limit');
    var bytes=[], pairs=entries.slice().sort(function(a,b){return a.source<b.source?-1:a.source>b.source?1:0;});
    function frame(n){bytes.push(n&255,(n>>>8)&255,(n>>>16)&255,(n>>>24)&255);}
    function string(value){
        if(typeof value!=='string'||value.length>12000)throw Error('Invalid receipt string');
        var start=bytes.length;frame(0);
        for(var i=0;i<value.length;i++){
            var c=value.charCodeAt(i);
            if(c>=0xd800&&c<=0xdbff){
                if(++i>=value.length)throw Error('Invalid receipt UTF16');
                var low=value.charCodeAt(i);if(low<0xdc00||low>0xdfff)throw Error('Invalid receipt UTF16');
                c=0x10000+((c-0xd800)<<10)+low-0xdc00;
            }else if(c>=0xdc00&&c<=0xdfff)throw Error('Invalid receipt UTF16');
            if(c<128)bytes.push(c);
            else if(c<2048)bytes.push(192|(c>>>6),128|(c&63));
            else if(c<65536)bytes.push(224|(c>>>12),128|((c>>>6)&63),128|(c&63));
            else bytes.push(240|(c>>>18),128|((c>>>12)&63),128|((c>>>6)&63),128|(c&63));
        }
        var n=bytes.length-start-4;bytes[start]=n&255;bytes[start+1]=(n>>>8)&255;bytes[start+2]=(n>>>16)&255;bytes[start+3]=(n>>>24)&255;
        if(bytes.length>4*1024*1024)throw Error('Receipt byte limit');
    }
    frame(pairs.length);
    for(var i=0;i<pairs.length;i++){string(pairs[i].source);string(pairs[i].text);}
    var length=bytes.length;
    bytes.push(128);while(bytes.length%64!==56)bytes.push(0);
    var high=Math.floor(length/0x20000000),low=(length*8)>>>0;
    bytes.push((high>>>24)&255,(high>>>16)&255,(high>>>8)&255,high&255,(low>>>24)&255,(low>>>16)&255,(low>>>8)&255,low&255);
    var h=[0x6a09e667,0xbb67ae85,0x3c6ef372,0xa54ff53a,0x510e527f,0x9b05688c,0x1f83d9ab,0x5be0cd19];
    var k=[0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
        0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
        0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
        0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
        0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
        0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
        0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
        0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2];
    function r(x,n){return (x>>>n)|(x<<(32-n));}
    var w=new Array(64);
    for(var offset=0;offset<bytes.length;offset+=64){
        for(i=0;i<16;i++){var p=offset+4*i;w[i]=(bytes[p]<<24)|(bytes[p+1]<<16)|(bytes[p+2]<<8)|bytes[p+3];}
        for(i=16;i<64;i++){var x=w[i-15],y=w[i-2];w[i]=(w[i-16]+(r(x,7)^r(x,18)^(x>>>3))+w[i-7]+(r(y,17)^r(y,19)^(y>>>10)))|0;}
        var a=h[0],b=h[1],c=h[2],d=h[3],e=h[4],f=h[5],g=h[6],j=h[7];
        for(i=0;i<64;i++){
            var t1=(j+(r(e,6)^r(e,11)^r(e,25))+((e&f)^((~e)&g))+k[i]+w[i])|0;
            var t2=((r(a,2)^r(a,13)^r(a,22))+((a&b)^(a&c)^(b&c)))|0;
            j=g;g=f;f=e;e=(d+t1)|0;d=c;c=b;b=a;a=(t1+t2)|0;
        }
        h[0]=(h[0]+a)|0;h[1]=(h[1]+b)|0;h[2]=(h[2]+c)|0;h[3]=(h[3]+d)|0;
        h[4]=(h[4]+e)|0;h[5]=(h[5]+f)|0;h[6]=(h[6]+g)|0;h[7]=(h[7]+j)|0;
    }
    return h.map(function(n){return ('00000000'+(n>>>0).toString(16)).slice(-8);}).join('');
}
if(typeof module==='object'&&module.exports)module.exports=fusionSourcePairsHash;
