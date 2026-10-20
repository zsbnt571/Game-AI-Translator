using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace ScreenshotTranslationUiTester;

// Bounded read-only adaptations of anylang's MIT-licensed keying/unseal
// algorithms. Keys are the public editor-format constants, never API keys.
internal sealed class WolfArchiveCipher
{
    private readonly int seal;private readonly bool keyless;private readonly byte[] pass,pad,word,outer,read,aesKey,iv;
    private readonly ulong whole,names,body;
    private static readonly Dictionary<int,byte[]> Keys=new()
    {
        [300]=Convert.FromHexString("0F53E13E8EB54191521655AE34C98F79592F596B95199B1B359A2FDEC97C1296C314B50F53E13E8E"),
        [314]=Convert.FromHexString("31F90136A3E38D3C7BC37D25AD6328191BF78E6CC4E5E27682EA4FED61DAE0445BB6463B06D5CEB67858D07C82"),
        [331]=Convert.FromHexString("CA084C5D170DDAA1D727C8415438823254B7F9468E136BCAD05C9595E2DC0353609B4A3817F36959A4C79A4363E654AFDBBB4358"),
        [350]=Convert.FromHexString("D284CE28CE8882E42A182E4C06B4EA8406B8C6885AA09E7C5640BA3452CCC67C2E141268FE5C769486788E4CBE88669C1EE08E6C")
    };
    internal WolfArchiveCipher(byte[] header,ulong whole)
    {
        seal=(int)(WolfArchive.U32(header,44)>>16);keyless=(header[44]&1)!=0;this.whole=whole;
        pass=Keys.GetValueOrDefault(seal)??[];pad=word=outer=read=aesKey=iv=[];
        if(seal is 331 or 350)
        {
            word=header[49..64];byte[] unmasked=(byte[])header.Clone();Unmask(unmasked);names=WolfArchive.U64(unmasked,16);
            if(whole<1088||whole>int.MaxValue)throw new NotSupportedException("此新版 WOLF 封包尺寸尚未支持。");
            body=Body(seal,word,whole);outer=Planes(seal,word,true,pass);read=keyless?[]:Planes(seal,word,false,pass);(aesKey,iv)=AesKeys(seal,word);
        }
        else if(keyless){}
        else if(seal is 300 or 314)pad=Padded(pass);
        else if(seal==100)pass=Convert.FromHexString("C982F8B42C939E830EBCBC92688D59A14A9E7FB0ACAF1D8F8EB83B9EE889D9AD0000FFBC2DAB9D8B0FB4BB9A6985");
        else throw new NotSupportedException($"WOLF 封包加密类型 {seal} 尚未支持，未猜测密钥。");
    }
    internal void Unmask(byte[] head)
    {
        if(seal is not (331 or 350))return;
        byte[] mask=Addressed(head[49..64],seal);for(int i=0;i<32;i++)head[8+i]^=mask[i];
    }
    internal byte[] FilePad(byte[] name,byte[] chain)=>seal is 300 or 314&&!keyless?Padded(pass.Concat(name.Concat(chain).Take(Math.Max(0,2040-pass.Length))).ToArray()):[];
    internal void Turn(byte[] bytes,ulong dxa,ulong absolute,byte[]? filePad)
    {
        if(seal is 331 or 350)
        {
            if(read.Length>0)Stream(read,bytes,0,bytes.Length,dxa,seal);
            Shared(64,whole-64,(p,n,at)=>Stream(outer,bytes,p,n,at,seal));
            Shared(64,64+body,(p,n,at)=>AesCtr(bytes,p,n,aesKey,iv,at-64));
            Shared(names,whole,(p,n,at)=>AesCtr(bytes,p,n,aesKey,iv,(body+15)/16*16+at-names));return;
        }
        if(keyless)return;
        if(seal is 300 or 314){var key=filePad??pad;for(int i=0;i<bytes.Length;i++)bytes[i]^=key[(int)((dxa+(ulong)i)%7)];return;}
        if(seal==100){ChaCha(bytes,pass.AsSpan(0,32),pass.AsSpan(34,12),checked(dxa+64));return;}
        throw new NotSupportedException("WOLF 加密类型未实现。");
        void Shared(ulong from,ulong until,Action<int,int,ulong> action)
        {ulong lo=Math.Max(from,absolute),hi=Math.Min(until,checked(absolute+(ulong)bytes.Length));if(lo<hi)action((int)(lo-absolute),(int)(hi-lo),lo);}
    }
    internal static uint Crc(ReadOnlySpan<byte> b){uint c=uint.MaxValue;foreach(byte x in b){c^=x;for(int i=0;i<8;i++)c=(c>>1)^((c&1)!=0?0xedb88320u:0);}return ~c;}
    internal static byte[] Padded(byte[] source)
    {
        byte[] held=source.Length<4?source.Concat("DXBDXARC"u8.ToArray()).ToArray():source;
        uint a=Crc(held.Where((_,i)=>i%2==0).ToArray()),b=Crc(held.Where((_,i)=>i%2==1).ToArray());
        byte[] result=new byte[7];BinaryPrimitives.WriteUInt32LittleEndian(result,a);for(int i=0;i<3;i++)result[4+i]=(byte)(b>>(8*i));return result;
    }
    private sealed class Rolling(uint seed){private uint state=seed;internal uint Next(){state=unchecked(state*214013+2531011);return state>>16&0x7fff;}}
    private sealed class Shifting(uint seed){private uint state=seed;internal uint Next(){state^=state<<11;state^=state>>19;state^=state<<7;return state;}}
    private static byte Ror(byte v,int n)=>(byte)((v>>n)|(v<<(8-n)));
    private static byte Fold(byte[] w,bool other){int count=other?w[8]/4:w[11]/3,turn=other?2:3;byte r=0;for(int i=0;i<count;i++)r=(byte)(i^Ror((byte)(r^w[i%15]),turn));return r;}
    internal static byte[] Planes(int seal,byte[] w,bool other,byte[] salting)
    {
        byte third=Fold(w,other);var rng=new Rolling((uint)(w[2]*w[5]+w[12]+third));byte[] factor=new byte[3];factor[third%3]=(byte)rng.Next();
        if(!other&&seal==350)factor[1]=(byte)(rng.Next()%251);
        byte[] key=new byte[768];for(int i=0;i<256;i++){uint paired=rng.Next();key[i]=(byte)(factor[0]^rng.Next());key[i+256]=(byte)(factor[1]^(paired>>8));key[i+512]=(byte)(factor[2]^paired);}
        if(other)
        {
            byte[] saltSource=seal==350?"958"u8.ToArray():salting;byte[] salt=new byte[128];for(int i=0;i<128;i++)salt[i]=saltSource.Length==0?(byte)0:unchecked((byte)(i/saltSource.Length+saltSource[i%saltSource.Length]));
            int spread=7;if(seal==350){third=unchecked((byte)(third+0x22));spread=16;}
            for(int i=0;i<3;i++)
            {
                int told=third;for(int j=0;j<256;j++)
                {
                    bool dodge=false;byte one=salt[j&127],two=salt[(j+i)%128],was=key[i*256+j],mixed=(byte)(one^was),now=mixed;int round=(two|(one<<8))%spread;
                    switch(round)
                    {
                        case 1 when two%11==0:now=was;break;
                        case 2 when one%29==0:now=unchecked((byte)~mixed);break;
                        case 3 when (round+j)%37==0:now=(byte)(two^mixed);break;
                        case 4 when (one+two)%97==0:now=unchecked((byte)(one+mixed));break;
                        case 5 when j*round%123==0:now=(byte)(mixed^told);break;
                        case 6 when one==255&&two==0:now=0;dodge=true;break;
                        case 7 when seal==350&&(round+j)%51==0:now^=one;break;
                        case 8 when seal==350&&one%29==0:now^=one;break;
                    }
                    if((j+i)%(one%5+1)==0)now^=(byte)told;else if(dodge)now=unchecked((byte)~mixed);
                    key[i*256+j]=now;told+=i;
                }
            }
        }
        return key;
    }
    internal static void Stream(byte[] key,byte[] b,int offset,int count,ulong at,int seal)
    {
        if(seal==350)
        {for(int i=0;i<count;i++,at++){int a=(int)(at%256),c=(int)(at/256%256);b[offset+i]^=(byte)(key[a]^(7*a)^key[c]^(7*(c+256)));}return;}
        for(int i=0;i<count;i++,at++)b[offset+i]^=(byte)(key[at%256]^key[at/256%256+256]^key[at/65536%256+512]);
    }
    internal static (byte[],byte[]) AesKeys(int seal,byte[] w)
    {
        byte[] key=new byte[16],nonce=new byte[16];for(int i=0;i<15;i++)
        {
            if(seal==350)
            {
                int k=((i*7)^(3*w[i]))%15,n=((i*11)^(5*w[(i+3)%15]))%15;
                key[i]^=(byte)((i+(w[k]<<(i%3)))%251);nonce[i]^=(byte)(((w[n]>>(i%2))+i*i)%246);
                key[15]^=(byte)((7*(w[i]+i+1))%253);nonce[15]^=(byte)(11*unchecked((ushort)(w[i]-i*2))%256);
            }
            else{key[i]^=unchecked((byte)(w[i*7%15]+i*i));nonce[i]^=unchecked((byte)(w[i*11%15]-i*i));key[15]^=unchecked((byte)(w[i]+i*3));nonce[15]^=unchecked((byte)(w[i]+i*5));}
        }return (key,nonce);
    }
    internal static ulong Body(int seal,byte[] w,ulong whole)
    {
        if(seal!=350)return 1024;uint seed=(uint)(w[2]*w[4]+w[12]);var rng=new Shifting(seed==0?1:seed);rng.Next();if(whole>=rng.Next()%500+800)rng.Next();uint b=(uint)(whole-64);if(b>=rng.Next()%500+800)b=rng.Next()%500+800;return b;
    }
    internal static byte[] Addressed(byte[] w,int seal)
    {
        byte[] mask=new byte[32];var rng=new Rolling(seal==350?(uint)(12+w[9]*w[10]+w[3]):(uint)(w[0]+w[7]*w[12]));
        void Put(int n,uint v)=>BinaryPrimitives.WriteUInt16LittleEndian(mask.AsSpan(n*2,2),(ushort)v);
        for(int i=0;i<(seal==350?2:4);i++)for(int j=3;j>=0;j--)Put(i*4+j,rng.Next());
        if(seal==350)
        {ulong lo=(ulong)rng.Next()<<17,hi=(ulong)rng.Next()<<31;uint near=(uint)(lo|hi|rng.Next()),far=(uint)((lo>>32)|(hi>>32));BinaryPrimitives.WriteUInt32LittleEndian(mask.AsSpan(16,4),near);BinaryPrimitives.WriteUInt32LittleEndian(mask.AsSpan(20,4),far);for(int j=3;j>=0;j--)Put(12+j,rng.Next());}
        return mask;
    }
    internal static void AesCtr(byte[] b,int offset,int count,byte[] key,byte[] nonce,ulong from)
    {
        // WOLF changes three bytes in each expanded AES word. A standard
        // platform AES implementation silently produces incorrect archives.
        byte[] keys=Expand(key);UInt128 counter=BinaryPrimitives.ReadUInt128BigEndian(nonce)+from/16;int skip=(int)(from%16);Span<byte> block=stackalloc byte[16];
        for(int i=0;i<count;)
        {BinaryPrimitives.WriteUInt128BigEndian(block,counter++);SealBlock(block,keys);for(;skip<16&&i<count;skip++,i++)b[offset+i]^=block[skip];skip=0;}
    }
    private static readonly byte[] Sbox=Convert.FromHexString("637C777BF26B6FC53001672BFED7AB76CA82C97DFA5947F0ADD4A2AF9CA472C0B7FD9326363FF7CC34A5E5F171D8311504C723C31896059A071280E2EB27B27509832C1A1B6E5AA0523BD6B329E32F8453D100ED20FCB15B6ACBBE394A4C58CFD0EFAAFB434D338545F9027F503C9FA851A3408F929D38F5BCB6DA2110FFF3D2CD0C13EC5F974417C4A77E3D645D197360814FDC222A908846EEB814DE5E0BDBE0323A0A4906245CC2D3AC629195E479E7C8376D8DD54EA96C56F4EA657AAE08BA78252E1CA6B4C6E8DD741F4BBD8B8A703EB5664803F60E613557B986C11D9EE1F8981169D98E949B1E87E9CE5528DF8CA1890DBFE6426841992D0FB054BB16");
    private static byte[] Expand(byte[] key)
    {
        byte[] keys=new byte[176];key.CopyTo(keys,0);byte rcon=1;
        for(int i=4;i<44;i++)
        {
            byte a=keys[(i-1)*4],b=keys[(i-1)*4+1],c=keys[(i-1)*4+2],d=keys[(i-1)*4+3];
            if(i%4==0){(a,b,c,d)=((byte)(Sbox[b]^rcon),(byte)(Sbox[c]>>4),unchecked((byte)~Sbox[d]),Ror(Sbox[a],7));rcon=Twice(rcon);}
            keys[i*4]=(byte)(keys[(i-4)*4]^a);keys[i*4+1]=(byte)(keys[(i-4)*4+1]^b);keys[i*4+2]=(byte)(keys[(i-4)*4+2]^c);keys[i*4+3]=(byte)(keys[(i-4)*4+3]^d);
        }return keys;
    }
    private static byte Twice(byte b)=>unchecked((byte)((b<<1)^((b>>7)*0x1b)));
    private static void SealBlock(Span<byte> s,byte[] keys)
    {
        for(int i=0;i<16;i++)s[i]^=keys[i];Span<byte> old=stackalloc byte[16];
        for(int r=1;r<=10;r++)
        {
            for(int i=0;i<16;i++)s[i]=Sbox[s[i]];s.CopyTo(old);
            for(int column=0;column<4;column++)for(int row=0;row<4;row++)s[column*4+row]=old[((column+row)%4)*4+row];
            if(r<10)for(int i=0;i<16;i+=4)
            {byte a=s[i],all=(byte)(s[i]^s[i+1]^s[i+2]^s[i+3]);s[i]^=(byte)(all^Twice((byte)(s[i+1]^s[i])));s[i+1]^=(byte)(all^Twice((byte)(s[i+2]^s[i+1])));s[i+2]^=(byte)(all^Twice((byte)(s[i+2]^s[i+3])));s[i+3]^=(byte)(all^Twice((byte)(s[i+3]^a)));}
            for(int i=0;i<16;i++)s[i]^=keys[r*16+i];
        }
    }
    internal static void ChaCha(byte[] data,ReadOnlySpan<byte> key,ReadOnlySpan<byte> nonce,ulong from)
    {
        if(from/64>uint.MaxValue||(ulong)data.Length>(((ulong)uint.MaxValue+1)*64-from))throw new InvalidDataException("WOLF ChaCha 计数器越界。");
        uint[] state=new uint[16]{0x61707865,0x3320646e,0x79622d32,0x6b206574,0,0,0,0,0,0,0,0,0,0,0,0};for(int i=0;i<8;i++)state[4+i]=BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i*4,4));for(int i=0;i<3;i++)state[13+i]=BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(i*4,4));state[12]=(uint)(from/64);int skip=(int)(from%64),at=0;
        Span<byte> block=stackalloc byte[64];while(at<data.Length)
        {
            uint[] x=(uint[])state.Clone();for(int r=0;r<10;r++){Q(0,4,8,12);Q(1,5,9,13);Q(2,6,10,14);Q(3,7,11,15);Q(0,5,10,15);Q(1,6,11,12);Q(2,7,8,13);Q(3,4,9,14);}
            for(int i=0;i<16;i++)BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i*4,4),unchecked(x[i]+state[i]));for(;skip<64&&at<data.Length;skip++,at++)data[at]^=block[skip];skip=0;state[12]++;
            void Q(int a,int b,int c,int d){x[a]=unchecked(x[a]+x[b]);x[d]=BitOperations.RotateLeft(x[d]^x[a],16);x[c]=unchecked(x[c]+x[d]);x[b]=BitOperations.RotateLeft(x[b]^x[c],12);x[a]=unchecked(x[a]+x[b]);x[d]=BitOperations.RotateLeft(x[d]^x[a],8);x[c]=unchecked(x[c]+x[d]);x[b]=BitOperations.RotateLeft(x[b]^x[c],7);}
        }
    }
}
