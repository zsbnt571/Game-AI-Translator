using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Brush=System.Windows.Media.Brush;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;

namespace ScreenshotTranslationUiTester;

// Persist only text and our formatting flags. Never load executable XAML,
// clipboard objects, images, or external resources from a note file.
internal static class WpfGameNotesFormatting
{
    internal static readonly Brush HighlightBrush=new SolidColorBrush(Color.FromRgb(125,94,0));
    internal static GameNoteParagraph[] Read(FlowDocument document)
    {
        var rows=new System.Collections.Generic.List<GameNoteParagraph>();
        void Blocks(BlockCollection blocks,string kind="")
        {
            foreach(var block in blocks)
                if(block is Paragraph paragraph){var runs=new System.Collections.Generic.List<GameNoteRun>();Inlines(paragraph.Inlines,runs);rows.Add(new(runs.ToArray(),kind));}
                else if(block is List list)foreach(var item in list.ListItems)Blocks(item.Blocks,list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin?"numbered":"bullet");
                else if(block is Section section)Blocks(section.Blocks,kind);
        }
        Blocks(document.Blocks);return rows.ToArray();
    }
    private static void Inlines(InlineCollection inlines,System.Collections.Generic.List<GameNoteRun> result)
    {
        foreach(var inline in inlines)
            if(inline is Run run)result.Add(new(run.Text,run.FontWeight>=FontWeights.Bold,run.FontStyle==FontStyles.Italic,run.TextDecorations.Any(d=>d.Location==TextDecorationLocation.Underline),run.Background is SolidColorBrush b&&b.Color.A>0));
            else if(inline is LineBreak)result.Add(new("\n"));
            else if(inline is Span span)Inlines(span.Inlines,result);
    }
    internal static FlowDocument Build(string text,GameNoteParagraph[]? rows)
    {
        var document=new FlowDocument{PagePadding=new(0)};
        rows??=text.Replace("\r\n","\n").Replace('\r','\n').Split('\n').Select(t=>new GameNoteParagraph([new(t)])).ToArray();
        List? list=null;string kind="";
        foreach(var row in rows)
        {
            var paragraph=new Paragraph{Margin=new(0,0,0,6)};
            foreach(var value in row.Runs)
            {
                var run=new Run(value.Text){FontWeight=value.Bold?FontWeights.Bold:FontWeights.Normal,FontStyle=value.Italic?FontStyles.Italic:FontStyles.Normal};
                if(value.Underline)run.TextDecorations=TextDecorations.Underline;
                if(value.Highlight){run.Background=HighlightBrush;run.Foreground=Brushes.White;}
                paragraph.Inlines.Add(run);
            }
            if(row.ListKind.Length==0){list=null;kind="";document.Blocks.Add(paragraph);}
            else
            {
                if(list is null||kind!=row.ListKind){list=new List{MarkerStyle=row.ListKind=="numbered"?TextMarkerStyle.Decimal:TextMarkerStyle.Disc,Margin=new(24,0,0,6)};document.Blocks.Add(list);kind=row.ListKind;}
                list.ListItems.Add(new ListItem(paragraph));
            }
        }
        if(document.Blocks.Count==0)document.Blocks.Add(new Paragraph());
        return document;
    }
}
