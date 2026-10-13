using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ScreenshotTranslationUiTester;

// An engine-family loader table, never a title-specific RVA or metadata decoder.
// Unknown tables and ABI anchors fail closed before anything is injected.
internal static class UnityNativeApiMap
{
    internal sealed class Image
    {
        internal readonly byte[] Bytes;
        internal readonly PEHeaders Headers;
        internal readonly Dictionary<string,int> Exports=new(StringComparer.Ordinal);
        internal Image(string file)
        {
            var info=new FileInfo(file);if(!info.Exists||info.Length is <64 or >536870912)throw new IOException("原生组件大小异常。");
            Bytes=File.ReadAllBytes(file);using var pe=new PEReader(new MemoryStream(Bytes));Headers=pe.PEHeaders;
            if(Headers.CoffHeader.Machine!=Machine.Amd64||Headers.PEHeader is null)throw new IOException("仅支持已识别的 x64 原生运行时。");
            var export=Headers.PEHeader.ExportTableDirectory;if(export.Size==0)return;
            int p=Offset(export.RelativeVirtualAddress,40);int funcs=I32(p+20),names=I32(p+24);
            if(funcs is <0 or >20000||names is <0 or >20000)throw new IOException("无效导出表。");
            int addresses=Offset(I32(p+28),checked(funcs*4)),strings=Offset(I32(p+32),checked(names*4)),ordinals=Offset(I32(p+36),checked(names*2));
            for(int i=0;i<names;i++)
            {
                int ordinal=BitConverter.ToUInt16(Bytes,ordinals+i*2);if(ordinal>=funcs)throw new IOException("无效导出序号。");
                int rva=I32(addresses+ordinal*4);if(rva>=export.RelativeVirtualAddress&&(long)rva<(long)export.RelativeVirtualAddress+export.Size)continue;
                string name=Ascii(I32(strings+i*4));if(name.Length>0&&Executable(rva))Exports.Add(name,rva);
            }
        }
        internal int I32(int p)=>BitConverter.ToInt32(Bytes,p);
        internal int Offset(int rva,int size=1)
        {
            if(rva<0||size<0)throw new IOException("无效 PE 范围。");
            foreach(var section in Headers.SectionHeaders)
                if(rva>=section.VirtualAddress&&(long)rva+size<=(long)section.VirtualAddress+section.SizeOfRawData)
                {long p=(long)section.PointerToRawData+rva-section.VirtualAddress;if(p<0||p+size>Bytes.Length)break;return checked((int)p);}
            throw new IOException("PE 地址越界。");
        }
        internal bool Executable(int rva)=>Headers.SectionHeaders.Any(s=>rva>=s.VirtualAddress&&(long)rva<(long)s.VirtualAddress+s.SizeOfRawData&&(s.SectionCharacteristics&SectionCharacteristics.MemExecute)!=0);
        internal string Ascii(int rva)
        {
            int p=Offset(rva),end=p;while(end<Bytes.Length&&end-p<256&&Bytes[end]!=0){if(Bytes[end]<32||Bytes[end]>126)return "";end++;}
            return end<Bytes.Length&&end-p<256?Encoding.ASCII.GetString(Bytes,p,end-p):"";
        }
        internal bool Prefix(int rva,byte[] expected)
        {
            for(int i=0;i<3;i++){int p=Offset(rva,5);if(Bytes[p]!=0xe9)break;rva=checked(rva+5+I32(p+1));if(!Executable(rva))return false;}
            int offset=Offset(rva,expected.Length);return Bytes.AsSpan(offset,expected.Length).SequenceEqual(expected);
        }
    }
    internal static readonly string[] Required=
    ["il2cpp_domain_get","il2cpp_domain_assembly_open","il2cpp_get_corlib","il2cpp_assembly_get_image","il2cpp_image_get_name","il2cpp_class_from_name","il2cpp_class_get_method_from_name","il2cpp_class_get_methods","il2cpp_class_get_parent","il2cpp_class_get_type","il2cpp_class_get_name","il2cpp_class_get_namespace","il2cpp_type_get_object","il2cpp_type_get_name","il2cpp_free","il2cpp_method_get_name","il2cpp_method_get_param","il2cpp_method_get_param_count","il2cpp_object_get_class","il2cpp_object_unbox","il2cpp_runtime_invoke","il2cpp_string_new","il2cpp_string_length","il2cpp_string_chars","il2cpp_array_length","il2cpp_gchandle_new","il2cpp_gchandle_new_weakref","il2cpp_gchandle_get_target","il2cpp_gchandle_free","il2cpp_object_new","il2cpp_resolve_icall","il2cpp_array_new"];
    // Optional callbacks use the same validated table as required APIs. Keep
    // their aliases in renamed-export players; absence still allows scanning.
    internal static readonly string[] Optional=["il2cpp_method_get_object","il2cpp_method_get_return_type","il2cpp_class_from_type"];
    internal static Dictionary<string,string> Create(UnityEmbeddedGame game)
    {
        if(game.Backend!="IL2CPP"||game.Architecture!="x64")throw new IOException("原生运行时架构不支持。");
        string playerPath=Path.Combine(game.Root,"UnityPlayer.dll");var version=FileVersionInfo.GetVersionInfo(playerPath);
        if(version.FileMajorPart is not (2021 or 2022)||version.FileMinorPart!=3)throw new IOException("此 Unity 系列尚未验证原生文本与字体接口。");
        var assembly=new Image(Path.Combine(game.Root,"GameAssembly.dll"));
        if(Required.All(assembly.Exports.ContainsKey))return Required.Concat(Optional.Where(assembly.Exports.ContainsKey)).ToDictionary(s=>s,s=>s,StringComparer.Ordinal);
        if(version.FileMajorPart!=2021||version.FileMinorPart!=3)throw new IOException("尚未识别此版本的原生 API 加载表。");
        var player=new Image(playerPath);var references=new List<(int At,string Name)>();
        foreach(var section in player.Headers.SectionHeaders.Where(s=>(s.SectionCharacteristics&SectionCharacteristics.MemExecute)!=0))
        {
            int begin=section.PointerToRawData,end=checked(begin+section.SizeOfRawData);if(begin<0||end>player.Bytes.Length)throw new IOException("加载表区域无效。");
            for(int p=begin;p+7<=end;p++)
            {
                if(player.Bytes[p]!=0x48||player.Bytes[p+1]!=0x8d||player.Bytes[p+2] is not (0x15 or 0x0d))continue;
                int at=checked(section.VirtualAddress+p-begin);long target=(long)at+7+player.I32(p+3);if(target<0||target>int.MaxValue)continue;
                string name;try{name=player.Ascii((int)target);}catch(IOException){continue;}
                if(assembly.Exports.ContainsKey(name))references.Add((at,name));
            }
        }
        if(references.Count is not (229 or 230)||references.Select(r=>r.Name).Distinct(StringComparer.Ordinal).Count()!=references.Count)throw new IOException("原生 API 加载表不完整或存在歧义。");
        for(int i=1;i<references.Count;i++)if(references[i].At-references[i-1].At!=(i==1?53:48))throw new IOException("原生 API 加载顺序未识别。");
        using var resource=typeof(UnityNativeApiMap).Assembly.GetManifestResourceStream("Fusion.Unity2021ApiOrder.txt")??throw new IOException("缺少运行时接口表。");
        using var reader=new StreamReader(resource,Encoding.ASCII);var canonical=reader.ReadToEnd().Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();
        if(canonical.Count!=229)throw new IOException("运行时接口表校验失败。");if(references.Count==230)canonical.Insert(27,"il2cpp_class_is_inited");
        var map=canonical.Select((n,i)=>(n,references[i].Name)).ToDictionary(x=>x.n,x=>x.Name,StringComparer.Ordinal);
        var anchors=new Dictionary<string,string>{["il2cpp_array_length"]="8B4118C3",["il2cpp_class_get_name"]="488B4110C3",["il2cpp_class_get_namespace"]="488B4118C3",["il2cpp_method_get_name"]="488B4118C3",["il2cpp_method_get_param_count"]="0FB64152C3",["il2cpp_object_get_class"]="488B01C3",["il2cpp_object_unbox"]="488D4110C3",["il2cpp_string_length"]="8B4110C3",["il2cpp_string_chars"]="488D4114C3"};
        foreach(var anchor in anchors)if(!assembly.Prefix(assembly.Exports[map[anchor.Key]],Convert.FromHexString(anchor.Value)))throw new IOException("原生 API 语义校验不匹配，未接入。");
        return Required.Concat(Optional.Where(map.ContainsKey)).ToDictionary(n=>n,n=>map[n],StringComparer.Ordinal);
    }
}
