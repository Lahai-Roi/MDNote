using System;using System.Globalization;using System.Collections.Generic;using System.Windows;using System.Windows.Controls;using System.Windows.Documents;using System.Windows.Media;using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
namespace MDNote {
// What the last gutter paint actually drew, one entry per number: the line it numbers plus the box
// it painted into. A check reads this instead of re-deriving the paint formula, so a rectangle that
// escapes its own row or its own column is visible as painted, not only as computed. Entries are
// reused between paints, so recording costs no allocation on the paint path.
internal sealed class GutterNumber {internal int Line;internal double Left,Top,Width,Height,RowHeight,BandTop,BandBottom,Baseline;
/* What the row itself was when this number was painted: its first row's extent, the baseline the
   old rule gave it, and whether the virtual body baseline was used. A check compares the painted
   top against those, in the same coordinates the paint used. */
internal double RowTop,RowBottom,Plain;internal bool Virtual;}
internal sealed class GutterPainted {
    readonly System.Collections.Generic.List<GutterNumber> items=new System.Collections.Generic.List<GutterNumber>();
    int used;
    internal void Clear(){used=0;}
    internal GutterNumber Add(int line,double left,double top,double width,double height,double rowHeight){
        return Add(line,left,top,width,height,rowHeight,Double.NaN,Double.NaN);
    }
    internal GutterNumber Add(int line,double left,double top,double width,double height,double rowHeight,double bandTop,double bandBottom){
        if(used==items.Count)items.Add(new GutterNumber());
        var item=items[used++];
        item.Line=line;item.Left=left;item.Top=top;item.Width=width;item.Height=height;item.RowHeight=rowHeight;
        item.BandTop=bandTop;item.BandBottom=bandBottom;
        item.RowTop=Double.NaN;item.RowBottom=Double.NaN;item.Plain=Double.NaN;item.Virtual=false;
        return item;
    }
    internal int Count{get{return used;}}
    internal GutterNumber this[int index]{get{return items[index];}}
}
// Number only the logical lines intersecting the viewport. The editor owns
// wrapping/layout; a second full-document TextFormatter pass duplicates that work.
internal sealed class TextGutter:FrameworkElement {
SourceEditor editorSurface;internal SourceEditor Editor{get{return editorSurface;}set{if(editorSurface==value)return;editorSurface=value;invalidatedEditor=null;invalidatedVersion=null;}}
internal Brush Ink=Brushes.Gray;internal int IndexPasses=0,RowPasses,PaintPasses;internal double PaintMilliseconds;
internal readonly GutterPainted Painted=new GutterPainted();SourceEditor invalidatedEditor;object invalidatedVersion;string invalidatedFont;double invalidatedTop,invalidatedLeft,invalidatedWidth,invalidatedHeight;Brush invalidatedInk;
/* The caret column's last answer: the document and line it was counted on, the offset it was
   counted at and the column there. A caret move inside the same line moves the sample instead of
   recounting the line (UI bug hunt P3-7); the document is weak so a closed tab's text is not kept. */
WeakReference columnDocument;int columnLineStart,columnOffset,columnValue,columnLine;bool columnValid;ITextSourceVersion columnVersion;
internal TextGutter(){Width=38;HorizontalAlignment=HorizontalAlignment.Left;IsHitTestVisible=false;ClipToBounds=true;}
static double measuredDpi=-1,digitWidth;
internal static double GutterWidth(int lines,double dpi){if(measuredDpi!=dpi){digitWidth=0;for(int i=0;i<10;i++){var glyph=new FormattedText(i.ToString(CultureInfo.InvariantCulture),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Consolas"),11,Brushes.Black,dpi);digitWidth=Math.Max(digitWidth,glyph.WidthIncludingTrailingWhitespace);}measuredDpi=dpi;}int digits=1;for(int remaining=Math.Max(1,lines);remaining>=10;remaining/=10)digits++;/* #24: two fixed widths - 5 digits for ordinary files, 10 (the int maximum) beyond - so margins do not shift with line count */return Math.Max(24,Math.Ceiling(digitWidth*(digits<=5?5:10)+12));}
internal double WidthForLines(int lines,double dpi){return GutterWidth(lines,dpi);}
internal void UpdateWidth(){if(Editor==null||Visibility!=Visibility.Visible)return;double desired=WidthForLines(Editor.Document.LineCount,VisualTreeHelper.GetDpi(this).PixelsPerDip);if(Width!=desired)Width=desired;var margin=Editor.Margin;if(margin.Left!=desired)Editor.Margin=new Thickness(desired,margin.Top,margin.Right,margin.Bottom);}
internal void InvalidateIfChanged(){var editor=Editor;if(editor==null)return;UpdateWidth();/* the TextView top margin and editor padding move every number, so both belong to the invalidation key */var view=editor.TextArea.TextView;double topInset=view==null?0:view.Margin.Top;string font=editor.FontFamily.Source+"|"+editor.FontSize+"|"+TextBlock.GetLineHeight(editor)+"|"+editor.TextWrapping+"|"+VisualTreeHelper.GetDpi(this).PixelsPerDip+"|"+topInset+"|"+(view==null?0:view.Margin.Bottom)+"|"+editor.Padding.Top;/* typing inside a line leaves every number in place: compare the visible rows instead of the document version */object version=RowSignature(editor);if(invalidatedEditor==editor&&Object.Equals(invalidatedVersion,version)&&invalidatedFont==font&&invalidatedTop==editor.VerticalOffset&&invalidatedLeft==editor.HorizontalOffset&&invalidatedWidth==editor.ViewportWidth&&invalidatedHeight==editor.ViewportHeight&&invalidatedInk==Ink)return;invalidatedEditor=editor;invalidatedVersion=version;invalidatedFont=font;invalidatedTop=editor.VerticalOffset;invalidatedLeft=editor.HorizontalOffset;invalidatedWidth=editor.ViewportWidth;invalidatedHeight=editor.ViewportHeight;invalidatedInk=Ink;InvalidateVisual();}
static object RowSignature(SourceEditor editor){var view=editor.TextArea.TextView;if(view==null||!view.VisualLinesValid)return editor.Document.Version;long hash=editor.Document.LineCount;hash=hash*31+(long)Math.Round(view.Margin.Top*8);hash=hash*31+(long)Math.Round(editor.Padding.Top*8);foreach(var visualLine in view.VisualLines){hash=hash*31+visualLine.FirstDocumentLine.LineNumber*1000003L+(long)Math.Round(visualLine.VisualTop*8);if(visualLine.TextLines.Count>0)hash=hash*31+(long)Math.Round(visualLine.TextLines[0].Baseline*8);}return hash;}
internal int VisualRow(int logical){if(Editor==null||logical>=Editor.Document.LineCount)return -1;var rect=Editor.GetRectFromCharacterIndex(Editor.Document.GetLineByNumber(logical+1).Offset);var first=Editor.GetRectFromCharacterIndex(0);double height=TextBlock.GetLineHeight(Editor);return rect.IsEmpty||first.IsEmpty?-1:(int)Math.Round((rect.Top-first.Top)/height);}
// The top a number paints at on a row raised by a larger run: the virtual body baseline (the row
// top, plus half the extra space a body-size row gets from the line-spacing setting, plus the
// body ascent) minus the label's own ascent. A row too short to hold the number there (spacing at
// or under 100%, where the body ascent can exceed the whole row) starts it at the row's own top.
internal static double VirtualTop(double rowTop,double rowBottom,double lineSpacingExtra,double bodyAscent,double labelBaseline,double labelHeight){
    double top=rowTop+(lineSpacingExtra>0?lineSpacingExtra/2:0)+bodyAscent-labelBaseline;
    if(top<rowTop||top+labelHeight>rowBottom)top=rowTop;
    return top;
}
// True when the first visual row's own text carries a run larger than the body font. That row's
// baseline is raised by the bigger run, which is exactly the case the virtual baseline replaces.
// Elements of later rows of the same wrapped line do not count: the number belongs to the first.
static bool LargerThanRow(ICSharpCode.AvalonEdit.Rendering.VisualLine visualLine,double bodySize){
    if(visualLine.TextLines.Count==0)return false;
    int firstRowLength=visualLine.TextLines[0].Length;
    foreach(var element in visualLine.Elements){
        if(element==null||element.VisualColumn>=firstRowLength)continue;
        var properties=element.TextRunProperties;
        if(properties!=null&&properties.FontRenderingEmSize>bodySize+.5)return true;
    }
    return false;
}
// The body font's ascent and the extra space a body-size row gets from the line-spacing setting,
// both read from metrics already cached for the editor's own font and size.
static void BodyMetrics(SourceEditor editor,out double ascent,out double spacingExtra){
    var face=new Typeface(editor.FontFamily,editor.FontStyle,editor.FontWeight,editor.FontStretch);
    var metric=LiveCompositionGeometry.Metric(face,editor.FontSize);
    ascent=metric.Baseline;
    double lineHeight=TextBlock.GetLineHeight(editor);
    spacingExtra=Double.IsNaN(lineHeight)?0:lineHeight-metric.Height;
}
/* The status line's line and column (UI bug hunt P3-7). The column is a count over the line's
   prefix, and the old form copied that whole prefix to a string on every caret move: on a
   hundred-thousand-character line, every arrow key. The sample below keeps the last answer, so a
   move inside the same line counts only the characters between the two offsets, and an edit that
   leaves the line where it is shifts the sample instead of forcing a recount. Only the first
   question about a line, or a change covering the sample, walks the prefix, and that walk reads
   bounded pieces so no line is ever copied whole. */
internal void LineColumn(int offset,out int line,out int column){
    var doc=Editor.Document;offset=Math.Max(0,Math.Min(offset,doc.TextLength));var at=doc.GetLineByOffset(offset);line=at.LineNumber;
    TrackColumns(doc);
    /* 11-01 monkey: the status line can ask from a Changed handler that runs before ColumnChanged,
       when the sample still describes the text before this change; reading from it went past the
       document's end. The sample is only used at the version it was last made true for. */
    if(columnValid&&(!Object.Equals(columnVersion,doc.Version)||columnOffset>doc.TextLength||columnLineStart>doc.TextLength))columnValid=false;
    if(columnValid&&columnLine==line&&columnLineStart==at.Offset)column=offset>=columnOffset?columnValue+ColumnCount(doc,columnOffset,offset):columnValue-ColumnCount(doc,offset,columnOffset);
    else column=1+ColumnCount(doc,at.Offset,offset);
    columnLine=line;columnLineStart=at.Offset;columnOffset=offset;columnValue=column;columnValid=true;columnVersion=doc.Version;
}
void TrackColumns(TextDocument document){
    TextDocument previous=columnDocument==null?null:columnDocument.Target as TextDocument;
    if(Object.ReferenceEquals(previous,document))return;
    if(previous!=null)previous.Changed-=ColumnChanged;
    columnDocument=new WeakReference(document);columnValid=false;document.Changed+=ColumnChanged;
}
/* The document's own change feed keeps the sample true without a rescan: a change starting at or
   after the sample leaves its prefix alone, one ending at or before it moves the sample (its
   column changes by the characters the edit added or took away, since all of them are in front of
   the sample), and a line break (the line's start and its prefix both change) or a change covering
   the sample drops it, so the next question recounts from the line start. */
void ColumnChanged(object sender,DocumentChangeEventArgs change){
    if(!columnValid)return;
    TextDocument document=columnDocument==null?null:columnDocument.Target as TextDocument;
    if(!Object.ReferenceEquals(sender,document))return;
    /* A sample made during this change (a handler that ran first) already counts it. */
    if(Object.Equals(columnVersion,document.Version))return;
    if(LineBreakIn(change.RemovedText)||LineBreakIn(change.InsertedText)){columnValid=false;return;}
    if(change.Offset<columnLineStart)return;
    if(change.Offset>=columnOffset){columnVersion=document.Version;return;}
    if(change.Offset+change.RemovalLength<=columnOffset){columnOffset+=change.InsertionLength-change.RemovalLength;columnValue+=ColumnCount(change.InsertedText,0,change.InsertionLength)-ColumnCount(change.RemovedText,0,change.RemovalLength);columnVersion=document.Version;return;}
    columnValid=false;
}
static bool LineBreakIn(ITextSource text){return text!=null&&text.TextLength>0&&(text.IndexOf('\n',0,text.TextLength)>=0||text.IndexOf('\r',0,text.TextLength)>=0);}
/* Characters that take a column: a low surrogate belongs to the character before it, the way the
   status line has always counted it. The range is read in bounded pieces, never as one string the
   size of the line. */
static int ColumnCount(ITextSource text,int start,int end){
    int columns=0;
    while(start<end){int length=Math.Min(4096,end-start);string piece=text.GetText(start,length);for(int i=0;i<piece.Length;i++)if(!Char.IsLowSurrogate(piece[i]))columns++;start+=length;}
    return columns;
}
protected override void OnRender(DrawingContext dc){PaintPasses++;var editor=Editor;if(editor==null||!editor.IsVisible||editor.ActualHeight<=0||editor.ViewportWidth<=0)return;var paintWatch=System.Diagnostics.Stopwatch.StartNew();double bodyAscent,spacingExtra;BodyMetrics(editor,out bodyAscent,out spacingExtra);double lineHeight=TextBlock.GetLineHeight(editor);if(Double.IsNaN(lineHeight)||lineHeight<=0)lineHeight=editor.FontSize*1.5;double dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;var view=editor.TextArea.TextView;view.EnsureVisualLines();RowPasses++;Painted.Clear();/* the text is cut at the TextView's own box (editor border, ApplySourceInset's top margin, the end/typewriter margin at the bottom); the numbers are cut at the same rows, so a half-scrolled line loses its number where it loses its text */double clipTop=view.TranslatePoint(new Point(0,0),this).Y,clipBottom=view.TranslatePoint(new Point(0,view.ActualHeight),this).Y;clipTop=Math.Max(0,clipTop);clipBottom=Math.Min(ActualHeight,clipBottom);var clipRect=new Rect(0,clipTop,Math.Max(0,Width),Math.Max(0,clipBottom-clipTop));dc.PushClip(new RectangleGeometry(clipRect));foreach(var visualLine in view.VisualLines){int logical=visualLine.FirstDocumentLine.LineNumber-1;Rect rect=editor.GetRectFromCharacterIndex(editor.Document.GetLineByNumber(logical+1).Offset);if(rect.IsEmpty||rect.Bottom<0||rect.Top>=ActualHeight)continue;string value=(logical+1).ToString(CultureInfo.InvariantCulture);var label=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Consolas"),11,Ink,dpi);double rowHeight=rect.Height>0?rect.Height:lineHeight;/* line spacing under 100% makes a row shorter than an 11px label, and neighbouring numbers then draw into each other; a label taller than its own row shrinks to the row height */if(label.Height>rowHeight)label=new FormattedText(value,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Consolas"),Math.Max(1,11*Math.Max(1,rowHeight-2)/label.Height),Ink,dpi);double left=Math.Max(2,Width-label.Width-7);double oldTop=RowLabelTop(view,visualLine,label);double rowTop=RowTop(view,visualLine);double top=RowLabelTop(view,visualLine,label,spacingExtra,bodyAscent,rowTop);var painted=Painted.Add(logical+1,left,top,label.Width,label.Height,rowHeight);painted.Baseline=top+label.Baseline;painted.RowTop=rowTop;painted.RowBottom=rowTop+visualLine.TextLines[0].Height;painted.Plain=oldTop;painted.Virtual=top!=oldTop;dc.DrawText(label,new Point(left,top));}dc.Pop();dc.DrawLine(new Pen(Ink,.4),new Point(Width-1,0),new Point(Width-1,ActualHeight));PaintMilliseconds+=paintWatch.Elapsed.TotalMilliseconds;}
// Where this number's own top goes, in this gutter's coordinates. The first row's baseline is
// raised by a run larger than the body font, so a number on that baseline sat near the bottom of
// the line; such rows move to the virtual body baseline instead. Every other row keeps the top it
// has always had, so ordinary numbers do not move.
double RowLabelTop(ICSharpCode.AvalonEdit.Rendering.TextView view,ICSharpCode.AvalonEdit.Rendering.VisualLine visualLine,FormattedText label,double spacingExtra,double bodyAscent,double rowTop){
    if(!LargerThanRow(visualLine,Editor.FontSize))return RowLabelTop(view,visualLine,label);
    return VirtualTop(rowTop,rowTop+visualLine.TextLines[0].Height,spacingExtra,bodyAscent,label.Baseline,label.Height);
}
// The top the number has always been drawn at: its own baseline sat on the row's baseline.
double RowLabelTop(ICSharpCode.AvalonEdit.Rendering.TextView view,ICSharpCode.AvalonEdit.Rendering.VisualLine visualLine,FormattedText label){
    double baseline=view.TranslatePoint(new Point(0,visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0],ICSharpCode.AvalonEdit.Rendering.VisualYPosition.Baseline)-view.VerticalOffset),this).Y;
    return baseline-label.Baseline;
}
// The first row's top edge in this gutter's coordinates.
double RowTop(ICSharpCode.AvalonEdit.Rendering.TextView view,ICSharpCode.AvalonEdit.Rendering.VisualLine visualLine){
    return view.TranslatePoint(new Point(0,visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0],ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop)-view.VerticalOffset),this).Y;
}
}
public partial class MainWindow {
readonly TextGutter textGutter=new TextGutter();
void UpdateGutter(){if(area==null)return;bool show=editing;/* #24: line numbers while editing (MD source/live and TXT), hidden while reading */textGutter.Editor=source;textGutter.Ink=(Brush)Resources["Muted"];textGutter.Visibility=show?Visibility.Visible:Visibility.Collapsed;if(!area.Children.Contains(textGutter))area.Children.Add(textGutter);/* #11 (b52): the numbers sit in a column over the body, so the gutter is raised above it. */Panel.SetZIndex(textGutter,10);if(show)textGutter.UpdateWidth();else source.Margin=new Thickness(0);textGutter.InvalidateVisual();}
string CaretStatus(){long probe=BenchProbeStart();try{textGutter.Editor=source;int line,column;textGutter.LineColumn(source.CaretIndex,out line,out column);return CaretPositionText(line,column);}finally{BenchProbeEnd("caret-status",probe);}}
/* b55: the caret gets its own per-language pattern (the shared "줄"/"열" keys also label the table rows and
   columns, which read "Columns" in English). Korean is the untranslated key itself, so its status text is
   exactly what it always was: "8줄, 4열". */
string CaretPositionText(int line,int column){return string.Format(System.Globalization.CultureInfo.InvariantCulture,T("{0}줄, {1}열"),line,column);}
/* A caret move is the first chance to put the normal line back after a transient notice (a
   replace count, a refused link): the message goes and the line is rebuilt, caret position
   included. Only the notice slot is rebuilt; a line without one keeps the old in-place patch. */
void RefreshCaretStatus(){if(loading)return;if(statusNotice!=null){ClearStatusNotice();return;}string value=status.Text;int start=value.IndexOf("   [",StringComparison.Ordinal),end=start<0?-1:value.IndexOf("]   ",start,StringComparison.Ordinal);if(end>=0)status.Text=value.Substring(0,start)+"   ["+CaretStatus()+"]   "+value.Substring(end+4);}
}}
