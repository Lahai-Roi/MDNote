using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
namespace MDNote {
// Printing only: the parser marks inline code with the inline-code font and no background, so the
// printed body paints the same grey box the old rich surface drew. The overlay element that used
// to track a RichTextBox's code spans was removed with the basic view (S5).
// The same walk bounds every image to the page's printable column: the paginator draws an
// InlineUIContainer at the element's own Width (the file width, at most ImageDecodeWidth's 1400),
// which ran past the paper because the old ActualWidth binding had no RichTextBox ancestor to
// read (MDNote.cs: no such surface exists in this build).
internal static class InlineCodeBackground {
    internal static void RestorePrintBackground(BlockCollection blocks){RestorePrintBackground(blocks,null);}
    static void RestorePrintBackground(BlockCollection blocks,FlowDocument document){foreach(Block block in blocks){var paragraph=block as Paragraph;if(paragraph!=null){if(!Markdown.IsInlineCodeFont(paragraph.FontFamily))RestoreInline(paragraph.Inlines,document??OwnerDocument(paragraph));}else if(block is Section)RestorePrintBackground(((Section)block).Blocks,document);else if(block is System.Windows.Documents.List)foreach(ListItem item in ((System.Windows.Documents.List)block).ListItems)RestorePrintBackground(item.Blocks,document);else if(block is Table)foreach(TableRowGroup group in ((Table)block).RowGroups)foreach(TableRow row in group.Rows)foreach(TableCell cell in row.Cells)RestorePrintBackground(cell.Blocks,document);}}
    static void RestoreInline(InlineCollection items,FlowDocument document){foreach(Inline inline in items){if(inline is Run&&Markdown.IsInlineCodeFont(inline.FontFamily))inline.Background=Ui.Brush("#D9E3F0");else if(inline is Span)RestoreInline(((Span)inline).Inlines,document);else{var container=inline as InlineUIContainer;var image=container==null?null:container.Child as Image;if(image!=null)ConstrainPrintWidth(image,document);}}}
    // The FlowDocument a block belongs to: its logical parent chain (Paragraph -> Section, list
    // item or table cell -> FlowDocument). The image binding needs that document as its source;
    // a top-level Paragraph's parent is already the document.
    static FlowDocument OwnerDocument(DependencyObject node){while(node!=null){var document=node as FlowDocument;if(document!=null)return document;var content=node as FrameworkContentElement;if(content!=null){node=content.Parent;continue;}var element=node as FrameworkElement;node=element==null?null:element.Parent;}return null;}
    static void ConstrainPrintWidth(Image image,FlowDocument document){if(image==null||document==null)return;var binding=new System.Windows.Data.MultiBinding();binding.Bindings.Add(new System.Windows.Data.Binding("PageWidth"){Source=document});binding.Bindings.Add(new System.Windows.Data.Binding("PagePadding"){Source=document});binding.Converter=PrintPageWidthConverter.Instance;image.SetBinding(FrameworkElement.MaxWidthProperty,binding);}
    // A page is the only surface with a fixed column, so the printable width is the page width
    // minus both page paddings. Printing.cs sets both after this walk; the multi-binding follows
    // either property changing, so no set order matters. A page without a size yet keeps the 700
    // the parser gives a surface with no page (the RTF copy).
    sealed class PrintPageWidthConverter:System.Windows.Data.IMultiValueConverter {
        internal static readonly PrintPageWidthConverter Instance=new PrintPageWidthConverter();
        public object Convert(object[] values,Type type,object parameter,System.Globalization.CultureInfo culture){
            double width=values!=null&&values.Length>0&&values[0] is double?(double)values[0]:Double.NaN;
            if(Double.IsNaN(width)||Double.IsInfinity(width)||width<160)return 700.0;
            Thickness padding=values!=null&&values.Length>1&&values[1] is Thickness?(Thickness)values[1]:new Thickness(0);
            double column=width-padding.Left-padding.Right;
            return column<40?40:column;
        }
        public object[] ConvertBack(object value,Type[] types,object parameter,System.Globalization.CultureInfo culture){throw new NotSupportedException();}
    }
}
}
