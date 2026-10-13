using System.Xml;
using System.Xml.Linq;
namespace ScreenshotTranslationUiTester;

internal static class XmlDisplayText
{
    internal static bool Read(string name,string body,Action<string,string?> add,CancellationToken cancellation,Action<CatalogIssueKind,string>? report)
    {
        body=body.TrimStart('\uFEFF');
        if(!new[]{".xml",".resx"}.Contains(Path.GetExtension(name),StringComparer.OrdinalIgnoreCase)&&!body.TrimStart().StartsWith("<?xml",StringComparison.Ordinal))return false;
        try
        {
            // Bound depth and work before constructing an XML tree.
            using(var probe=XmlReader.Create(new StringReader(body),new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16*1024*1024}))
            {
                int count=0;while(probe.Read())
                {cancellation.ThrowIfCancellationRequested();if(probe.Depth>64||++count>200000){report?.Invoke(CatalogIssueKind.Limited,"XML 文本结构超过本次读取上限");return true;}}
            }
            using var input=new StringReader(body);
            using var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=16*1024*1024});
            var document=XDocument.Load(reader,LoadOptions.PreserveWhitespace);int nodes=0;
            foreach(var element in document.Descendants())
            {
                cancellation.ThrowIfCancellationRequested();if(++nodes>200000){report?.Invoke(CatalogIssueKind.Limited,"XML 文本节点超过本次读取上限");break;}
                if(element.HasElements)continue;
                string field=element.Name.LocalName,parent=element.Parent?.Name.LocalName??"";
                bool resourceValue=field.Equals("value",StringComparison.OrdinalIgnoreCase)&&parent is "data" or "entry" or "string";
                if(!resourceValue&&!UnityTextResourceLanguageSelection.IsTextField(field)&&!field.Equals("string",StringComparison.OrdinalIgnoreCase))continue;
                // Typed .resx values may encode binary objects rather than text.
                if(resourceValue&&element.Parent!.Attributes().Any(a=>a.Name.LocalName is "type" or "mimetype"))continue;
                string? locale=null;
                foreach(var ancestor in element.AncestorsAndSelf().Take(64))
                {
                    var attribute=ancestor.Attributes().FirstOrDefault(a=>a.Name.LocalName is "lang" or "locale" or "language");
                    if(attribute is not null){locale=UnityTextResourceLanguageSelection.DeclaredLocale(attribute.Value);break;}
                }
                foreach(string value in SerializedDialogueText.VisibleParts(element.Value))add(value,locale);
            }
        }
        catch(XmlException){report?.Invoke(CatalogIssueKind.Failed,"XML 文本资源解析失败，外部实体未加载");}
        return true;
    }
}
