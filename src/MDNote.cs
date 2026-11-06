using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace MDNote {
public static partial class Markdown {
    internal static Action<string,int> DiagnosticStage=null;static void Stage(string name,int count){var observer=DiagnosticStage;if(observer!=null)observer(name,count);}
    // Links and images are not in this pattern: their target ends at MarkdownPlainText.TargetEnd
    // (balanced parentheses, an <angle> destination, a quoted title), the same scan the TXT
    // renderer and the live preview use, so Inline finds them itself (LinkAt). With the two
    // alternatives gone the groups are: 5 code, 6/7 bold, 8 strike, 9/10 italic.
    static readonly Regex token=new Regex(@"(<span\s+[^>]*>)([\s\S]*?)</span>|(<font\s+[^>]*>)([\s\S]*?)</font>|(?<!`)(?<ticks>`+)(?!`)([^\n]*?)(?<!`)\k<ticks>(?!`)|\*\*(.+?)\*\*|__(.+?)__|~~(.+?)~~|\*([^*\n]+)\*|(?<!\w)_([^_\n]+)_(?!\w)|<br\s*/?>",RegexOptions.IgnoreCase);
    internal static bool IsInlineCodeFont(FontFamily font){return font!=null&&(String.Equals(font.Source,"Consolas, Malgun Gothic",StringComparison.OrdinalIgnoreCase)||font.Source.EndsWith(", Consolas, Malgun Gothic",StringComparison.OrdinalIgnoreCase));}
    internal static FontFamily InlineCodeFont(string family){return new FontFamily(family+", Consolas, Malgun Gothic");}
    internal static double HeadingScale(int level){return level==1?2:level==2?1.6:level==3?1.3:level==4?1.15:level==5?1.1:1.06;}
    internal static int HeadingLevel(Paragraph paragraph,double normal){if(paragraph.FontWeight.ToOpenTypeWeight()<600||paragraph.FontSize<=normal*1.05)return 0;double ratio=paragraph.FontSize/normal,best=Double.MaxValue;int found=0;for(int level=1;level<=6;level++){double distance=Math.Abs(ratio-HeadingScale(level));if(distance<best){best=distance;found=level;}}return found;}
    static int ImageDecodeWidth(Uri uri){if(!uri.IsFile)return 0;try{using(var image=System.Drawing.Image.FromFile(uri.LocalPath))return Math.Min(1400,image.Width);}catch{return 0;}}
    static SolidColorBrush Brush(byte r,byte g,byte b){return new SolidColorBrush(Color.FromRgb(r,g,b));}
    static FlowDocument ParseCore(string text,double size,string family,Brush foreground,bool dark,string file,string imageLabel) {
        var doc=new FlowDocument {FontFamily=new FontFamily(family),FontSize=size,Foreground=foreground,PagePadding=new Thickness(18,24,0,24),ColumnWidth=100000};
        string[] lines=text.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
        for(int i=0;i<lines.Length;i++) {
            if(i%500==0)Stage("parse-lines",i);int start=i;string line=lines[i];
            int mathLast;string mathSource;
            if(TryDisplayMath(lines,i,out mathLast,out mathSource)&&MathRender.Check(mathSource)){
                double mathAscent,mathDescent;
                FrameworkElement mathPicture=MathRender.CreatePicture(mathSource,true,size,MathInk(dark),family,out mathAscent,out mathDescent);
                if(mathPicture!=null){mathPicture.HorizontalAlignment=HorizontalAlignment.Center;Add(doc,new BlockUIContainer(mathPicture){Margin=new Thickness(0,8,0,12)},lines,start,mathLast,"math");i=mathLast;continue;}
            }
            Match fence=Regex.Match(line,@"^\s{0,3}(`{3,}|~{3,})(.*)$");
            if(fence.Success && !(fence.Groups[1].Value[0]=='`'&&fence.Groups[2].Value.Contains("`"))) {
                char marker=fence.Groups[1].Value[0];int count=fence.Groups[1].Value.Length;var code=new StringBuilder();bool firstCodeLine=true;
                while(++i<lines.Length&&!Regex.IsMatch(lines[i],@"^\s{0,3}"+Regex.Escape(new string(marker,count))+marker+@"*\s*$")){if(!firstCodeLine)code.Append('\n');firstCodeLine=false;code.Append(lines[i]);}
                var p=new Paragraph(new Run(code.ToString())){FontFamily=new FontFamily("Consolas, Malgun Gothic"),FontSize=size*.9,Background=dark?Brush(55,61,73):Brush(235,238,242),BorderBrush=dark?Brush(91,103,120):Brush(173,184,198),BorderThickness=new Thickness(1),Padding=new Thickness(14),Margin=new Thickness(0,6,0,12)};DecorateCode(p,fence.Groups[2].Value.Trim(),dark);Add(doc,p,lines,start,i,"code:"+fence.Groups[2].Value.Trim());continue;
            }
            if(String.IsNullOrWhiteSpace(line)){var blank=Para("",size,dark,file,imageLabel);int indent=line.Replace("\t","    ").Length;if(indent>0)blank.Margin=new Thickness(indent*size*.5,0,0,0);Add(doc,blank,lines,start,i,indent>0?"unlisted-indent:"+indent:"blank");continue;}
            string definitionId,definitionBody;bool note;if(TryDefinition(line,out definitionId,out definitionBody,out note)){if(note){while(i+1<lines.Length&&(lines[i+1].StartsWith("    ")||lines[i+1].StartsWith("\t"))){definitionBody+="\n"+lines[++i].TrimStart();}string noteId=definitionId;var paragraph=Para(definitionBody,size,dark,file,imageLabel);paragraph.FontSize=size*.9;paragraph.Foreground=MutedInk(dark);var markerRun=new Run(NoteNumber(noteId)+"."){Tag="marker",FontSize=size*.75,BaselineAlignment=BaselineAlignment.Superscript,Foreground=AccentInk(dark)};var marker=new Hyperlink {Foreground=AccentInk(dark),ToolTip="[^"+noteId+"]"};marker.Inlines.Add(markerRun);marker.Resources["FootnoteRef"]=noteId;if(paragraph.Inlines.FirstInline==null)paragraph.Inlines.Add(marker);else paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline,marker);paragraph.Inlines.InsertAfter(marker,new Run(" "){Tag="marker"});Add(doc,paragraph,lines,start,i,"footnote:"+noteId);}else{var label=new TextBlock {Text=line,FontSize=size*.9,Foreground=MutedInk(dark),TextWrapping=TextWrapping.NoWrap};var paragraph=new Paragraph {Margin=new Thickness(0),LineHeight=Double.NaN};paragraph.Inlines.Add(new InlineUIContainer(label));Add(doc,paragraph,lines,start,i,"reference");}continue;}/* #1: a 0.1px hidden paragraph collapsed the whole RichTextBox layout (extent 0) in the app view */
            if(i+1<lines.Length&&MarkdownPlainText.TableStart(line,lines[i+1])) {
                var table=new Table {CellSpacing=0,Margin=new Thickness(0,6,0,12)};var group=new TableRowGroup();table.RowGroups.Add(group);
                string[] headers=Cells(line);for(int c=0;c<headers.Length;c++)table.Columns.Add(new TableColumn());
                string[] alignments=Cells(lines[i+1]);AddRow(group,headers,true,size,dark,file,imageLabel);AlignRow(group.Rows[group.Rows.Count-1],alignments);i++;
                while(i+1<lines.Length&&!String.IsNullOrWhiteSpace(lines[i+1])&&MarkdownTableCommands.HasTablePipe(lines[i+1])) {i++;AddRow(group,FitCells(Cells(lines[i]),headers.Length),false,size,dark,file,imageLabel);AlignRow(group.Rows[group.Rows.Count-1],alignments);}
                Add(doc,table,lines,start,i,"table");continue;
            }
            // CommonMark ATX heading, the same MarkdownPlainText.AtxHeading the TXT converter reads:
            // up to three leading spaces/tabs, and a closing "#" run only when a space or tab sets
            // it off ("### C#" keeps its "#", "## 제목 ##" loses the pair).
            int headingLevel;string headingBody;
            if(MarkdownPlainText.AtxHeading(line,out headingLevel,out headingBody)){var p=Para(headingBody,size,dark,file,imageLabel);p.FontSize=size*HeadingScale(headingLevel);p.FontWeight=FontWeights.Bold;p.LineHeight=Double.NaN;p.Margin=new Thickness(0,headingLevel==1?8:18,0,10);Add(doc,p,lines,start,i,"h:"+headingLevel);continue;}
            // Setext underline: one or more "=" (H1) or two or more "-" (H2), the same rule as the
            // live preview (MarkdownLivePreview.SetextUnderlineLevel) and the TXT converter. A
            // single "-" is a list marker being typed, not an underline.
            if(i+1<lines.Length&&!Regex.IsMatch(line,@"^[ \t]*(?:[-+*]|\d+[.)])[ \t]")&&Regex.IsMatch(lines[i+1],@"^[ \t]{0,3}(=+|-{2,})[ \t]*$")){var p=Para(line,size,dark,file,imageLabel);int level=lines[i+1].Trim()[0]=='='?1:2;p.FontSize=size*(level==1?2:1.6);p.FontWeight=FontWeights.Bold;p.LineHeight=Double.NaN;i++;Add(doc,p,lines,start,i,"h:"+level);continue;}
            if(Regex.IsMatch(line,@"^\s{0,3}([-*_])(?:\s*\1){2,}\s*$")){Add(doc,new BlockUIContainer(new System.Windows.Controls.Border {Height=1,Background=dark?Brush(110,118,130):Brush(220,222,226),Margin=new Thickness(0,10,0,10)}),lines,start,i,"rule");continue;}
            var quote=Regex.Match(line,@"^[ \t]*>[ \t]?(.*)$");
            if(quote.Success){var body=new StringBuilder(quote.Groups[1].Value);var lazy=new HtmlExporter.LazyQuote();lazy.Line(quote.Groups[1].Value);while(i+1<lines.Length){var next=Regex.Match(lines[i+1],@"^[ \t]*>[ \t]?(.*)$");if(next.Success){i++;body.Append('\n').Append(next.Groups[1].Value);lazy.Line(next.Groups[1].Value);}else if(lazy.Continues(lines,i+1)){i++;body.Append('\n').Append(lines[i].TrimStart(' ','\t'));}else break;}var section=QuoteSection(body.ToString(),size,family,foreground,dark,file,imageLabel);Add(doc,section,lines,start,i,"quote");continue;}
            var list=Regex.Match(line,@"^([ \t]*)(?:([-+*])|(\d+)[.)])[ \t]+(.*)$");
            if(list.Success){string body=list.Groups[4].Value;string prefix=list.Groups[1].Value+(list.Groups[3].Success?list.Groups[3].Value+". ":"- ");string bullet=list.Groups[3].Success?prefix:"•  ";var check=Regex.Match(body,@"^\[([ xX])\][ \t]*(.*)$");bool task=check.Success;if(check.Success){bullet=check.Groups[1].Value==" "?"☐  ":"☑  ";prefix+="["+(check.Groups[1].Value==" "?" ":"x")+"] ";body=check.Groups[2].Value;}int itemHeadingLevel;string itemHeadingBody;if(MarkdownPlainText.AtxHeading(body,out itemHeadingLevel,out itemHeadingBody)){var itemParagraph=Para(itemHeadingBody,size,dark,file,imageLabel);itemParagraph.FontSize=size*HeadingScale(itemHeadingLevel);itemParagraph.FontWeight=FontWeights.Bold;itemParagraph.LineHeight=Double.NaN;itemParagraph.Margin=new Thickness(0,itemHeadingLevel==1?8:18,0,10);if(task)InsertFirst(itemParagraph,TaskBox(itemParagraph,check.Groups[1].Value!=" "));Add(doc,itemParagraph,lines,start,i,"list:"+prefix);continue;}var itemQuote=Regex.Match(body,@"^>[ \t]?(.*)$");if(itemQuote.Success){var itemSection=QuoteSection(itemQuote.Groups[1].Value,size,family,foreground,dark,file,imageLabel);if(task){var itemFirst=itemSection.Blocks.FirstBlock as Paragraph;if(itemFirst!=null)InsertFirst(itemFirst,TaskBox(itemFirst,check.Groups[1].Value!=" "));}Add(doc,itemSection,lines,start,i,"listquote:"+prefix);continue;}var p=Para(body,size,dark,file,imageLabel);Inline marker;if(check.Success)marker=TaskBox(p,check.Groups[1].Value!=" ");else marker=new Run(bullet){Tag="marker"};InsertFirst(p,marker);p.Margin=new Thickness(12+Math.Min(100,list.Groups[1].Length*5),2,0,4);Add(doc,p,lines,start,i,"list:"+prefix);continue;}
            Add(doc,Para(line,size,dark,file,imageLabel),lines,start,i,"");
        }Stage("nest-start",lines.Length);NestLists(doc);Stage("parse-complete",lines.Length);return doc;
    }
    // Printing only: a list's marker slot sits in its margin, so list content starts at the item
    // text x and FlowDocument draws the marker left of it (the live projection's own rule).
    internal static void SetListIndent(System.Windows.Documents.List list){
        double ideal,error=0;
        var item=list.Parent as ListItem;
        if(item==null)ideal=ListSlot(list);
        else{
            var parent=item.Parent as System.Windows.Documents.List;
            ideal=18+ListSlot(list)-(parent==null?18:ListSlot(parent));
            error=ListError(parent);
        }
        double left=Math.Max(0,ideal-error);
        var margin=new Thickness(left,list.Margin.Top,list.Margin.Right,list.Margin.Bottom);
        if(list.Margin!=margin)list.Margin=margin;
    }
    static double ListSlot(System.Windows.Documents.List list){return list.MarkerStyle==TextMarkerStyle.Decimal?MarkdownLivePreview.NumberSlot(list.FontFamily,list.FontSize):18;}
    // A bullet under an ordered item would need a negative margin and FlowDocument rejects those, so
    // that list starts at its parent's text; the error is subtracted from the next-level margins.
    static double ListError(System.Windows.Documents.List list){
        if(list==null)return 0;
        var chain=new List<System.Windows.Documents.List>();
        for(var current=list;current!=null;){
            chain.Add(current);
            var item=current.Parent as ListItem;
            current=item==null?null:item.Parent as System.Windows.Documents.List;
        }
        double error=0;
        for(int i=chain.Count-1;i>=0;i--){
            var current=chain[i];var item=current.Parent as ListItem;
            var parent=item==null?null:item.Parent as System.Windows.Documents.List;
            double ideal=parent==null?ListSlot(current):18+ListSlot(current)-ListSlot(parent);
            error=Math.Max(0,error+current.Margin.Left-ideal);
        }
        return error;
    }
    // Content is the column where the item's own text starts (its marker plus the spaces after
    // it): a following marker left of that column is outside the item and opens a sibling list,
    // not a child (CommonMark: "1. " content starts at column 3, so a two-space bullet is not
    // nested). MarkdownPlainText.ListContentColumn is the one scan for this value.
    // A space/tab-only line does not end a list either (CommonMark: a loose list). It stays a
    // block of its own until the next block arrives: if that block is a list item the blank line
    // moves into the deepest open item, so one List numbers the items on and the print text keeps
    // the source's blank line where it was (the TXT conversion and the ReadableText oracle are
    // compared line for line, and a blank left between two Lists would sit after both). A blank
    // line after the end of the list stays where it is and the block below clears the levels,
    // which keeps an unchanged list's original text for copy and save.
    sealed class ListLevel {internal int Indent;internal int Content;internal System.Windows.Documents.List List;internal ListItem Last;internal bool Ordered,Task;}
    static void NestLists(FlowDocument document){
        var blocks=new List<Block>();foreach(Block block in document.Blocks)blocks.Add(block);var levels=new List<ListLevel>();var originals=new Dictionary<System.Windows.Documents.List,List<string>>();System.Windows.Documents.List rootList=null;var pending=new List<Block>();
        foreach(Block block in blocks){var paragraph=block as Paragraph;var section=block as Section;var meta=block.Tag as BlockMeta;string kind=meta==null?"":meta.Kind;bool listParagraph=paragraph!=null&&kind.StartsWith("list:");bool listQuote=section!=null&&kind.StartsWith("listquote:");if(!listParagraph&&!listQuote){if((kind=="blank"||kind.StartsWith("unlisted-indent:"))&&MarkdownPlainText.BlankLine(meta.Original)&&levels.Count>0){/* a U+3000-only line is content for the live view and TXT, so it ends the list there */pending.Add(block);continue;}pending.Clear();levels.Clear();continue;}if(pending.Count>0){if(levels.Count>0&&levels[levels.Count-1].Last!=null)foreach(Block blank in pending){document.Blocks.Remove(blank);levels[levels.Count-1].Last.Blocks.Add(blank);var blankMeta=blank.Tag as BlockMeta;if(blankMeta!=null&&rootList!=null)originals[rootList].Add(blankMeta.Original);}pending.Clear();}string prefix=listParagraph?kind.Substring(5):kind.Substring(10);var marker=Regex.Match(prefix,@"^(\s*)(?:(\d+)\. |- )(\[[ xX]\] )?");if(!marker.Success){levels.Clear();continue;}int indent=marker.Groups[1].Value.Replace("\t","    ").Length;bool ordered=marker.Groups[2].Success,task=marker.Groups[3].Success;int content;if(!MarkdownPlainText.ListContentColumn(meta.Original,out content))content=indent+(ordered?marker.Groups[2].Value.Length+2:2);
            while(levels.Count>0&&(levels[levels.Count-1].Indent>indent||(levels[levels.Count-1].Indent==indent&&(levels[levels.Count-1].Ordered!=ordered||levels[levels.Count-1].Task!=task))||(levels[levels.Count-1].Indent<indent&&indent<levels[levels.Count-1].Content)))levels.RemoveAt(levels.Count-1);
            ListLevel level=levels.Count==0?null:levels[levels.Count-1];if(level==null||level.Indent<indent){var list=new System.Windows.Documents.List {MarkerStyle=task?TextMarkerStyle.None:ordered?TextMarkerStyle.Decimal:TextMarkerStyle.Disc,Margin=new Thickness(18,2,0,5),Padding=new Thickness(0)};MarkHistoryReferences(list);if(ordered){int first;list.StartIndex=Int32.TryParse(marker.Groups[2].Value,out first)&&first>0&&first<1000000?first:1;}if(level==null){document.Blocks.InsertBefore(block,list);rootList=list;originals[list]=new List<string>();}else if(level.Last!=null)level.Last.Blocks.Add(list);SetListIndent(list);level=new ListLevel {Indent=indent,List=list,Ordered=ordered,Task=task};levels.Add(level);}
            level.Content=content; // each item is its own container for the next marker
            originals[rootList].Add(meta.Original);document.Blocks.Remove(block);string body=meta.Original.Length>=prefix.Length?meta.Original.Substring(prefix.Length):"";string tag=task?(prefix.Contains("[x]")?"task:x":"task: "):null;if(listQuote){section.Margin=new Thickness(0,2,0,4);MarkdownWriter.Track(section,"quote",body);var quoteItem=new ListItem();quoteItem.Blocks.Add(section);if(tag!=null)quoteItem.Tag=tag;level.List.ListItems.Add(quoteItem);level.Last=quoteItem;}else{if(!task&&paragraph.Inlines.FirstInline!=null&&Object.Equals(paragraph.Inlines.FirstInline.Tag,"marker"))paragraph.Inlines.Remove(paragraph.Inlines.FirstInline);paragraph.Margin=new Thickness(0,2,0,4);MarkdownWriter.Track(paragraph,"",body);var item=new ListItem(paragraph);if(tag!=null)item.Tag=tag;level.List.ListItems.Add(item);level.Last=item;}
        }foreach(var original in originals)MarkdownWriter.Track(original.Key,"listblock",String.Join("\n",original.Value));
    }
    static void Add(FlowDocument doc,Block block,string[] lines,int from,int to,string kind){MarkHistoryReferences(block);doc.Blocks.Add(block);MarkdownWriter.Track(block,kind,String.Join("\n",lines,from,Math.Min(to,lines.Length-1)-from+1));}
    // Keep the global-reference dependency on roots too: responsive parsing moves roots out of
    // their temporary documents, and reference-looking table rows can participate in the scan.
    static readonly DependencyProperty historyGlobalReferencesProperty=DependencyProperty.RegisterAttached("HistoryGlobalReferences",typeof(bool),typeof(Markdown),new PropertyMetadata(false));
    internal static bool HistoryHasGlobalReferences(Block block){return (bool)block.GetValue(historyGlobalReferencesProperty);}
    static void MarkHistoryReferences(FrameworkContentElement element){if(referenceContext!=null&&(referenceContext.Links.Count>0||referenceContext.Notes.Count>0))element.SetValue(historyGlobalReferencesProperty,true);}
    static Inline TaskBox(Paragraph paragraph,bool done){var box=new CheckBox {IsChecked=done,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,6,0),ToolTip=new ToolTip {Content="체크/해제 | 저장 필요",Padding=new Thickness(6,3,6,3),MinHeight=0,Height=Double.NaN}};var marker=new InlineUIContainer(box){Tag="marker",BaselineAlignment=BaselineAlignment.Center};box.ToolTipOpening+=delegate{var owner=Window.GetWindow(box) as MainWindow;if(owner!=null)((ToolTip)box.ToolTip).Content=new TextBlock {Text=owner.TaskTooltip(),FontSize=12,LineHeight=Double.NaN,LineStackingStrategy=LineStackingStrategy.MaxHeight,Margin=new Thickness(0)};};return marker;}
    static void InsertFirst(Paragraph paragraph,Inline inline){if(paragraph.Inlines.FirstInline==null)paragraph.Inlines.Add(inline);else paragraph.Inlines.InsertBefore(paragraph.Inlines.FirstInline,inline);}
    static Section QuoteSection(string body,double size,string family,Brush foreground,bool dark,string file,string imageLabel){var child=Parse(body,size,family,foreground,dark,file,imageLabel);var section=new Section {BorderThickness=new Thickness(3,0,0,0),BorderBrush=dark?Brush(152,162,181):Brush(130,140,160),Padding=new Thickness(14,5,8,5),Margin=new Thickness(0,5,0,10),Foreground=dark?Brush(194,200,210):Brush(95,103,116)};while(child.Blocks.FirstBlock!=null){var block=child.Blocks.FirstBlock;child.Blocks.Remove(block);section.Blocks.Add(block);}return section;}
    static bool IsBlock(string[] lines,int i){return Regex.IsMatch(lines[i],@"^[ \t]{0,3}(#{1,6}[ \t]|>|`{3,}|~{3,}|[-+*][ \t]|\d+[.)][ \t]|([-*_])(?:\s*\2){2,}\s*$)")||(i+1<lines.Length&&(Regex.IsMatch(lines[i+1],@"^[ \t]*(=+|-{2,})[ \t]*$")||MarkdownPlainText.TableStart(lines[i],lines[i+1])));}
    // A table's second line as MarkdownPlainText.TableDelimiter reads it: the same GFM row the live
    // renderer and the TXT converter accept, so "---" under "이름 | 나이" is a setext underline here
    // too and never opens a table.
    static bool TableDelimiterLine(string line){return MarkdownPlainText.TableDelimiter(line);}
    // The print/rich-copy split is the live renderer's own rule (MarkdownLivePreview.TablePipes):
    // an ODD backslash run escapes the pipe, and a `code` span closes only on the same-length
    // backtick run, so a pipe inside one stays cell text. MarkdownPlainText.Cells is that scan.
    static string[] Cells(string line){return MarkdownPlainText.Cells(line);}
    // #5: a source row can carry more or fewer cells than the header (a merged-cell paste or a
    // hand-edited row). A WPF table column is fixed by the header, so a data row pads with empty
    // cells and drops the overflow; MarkdownPlainText applies the same rule, so print and TXT agree.
    static string[] FitCells(string[] cells,int count){if(cells.Length==count)return cells;var fit=new string[count];for(int i=0;i<count;i++)fit[i]=i<cells.Length?cells[i]:"";return fit;}
    static void AlignRow(TableRow row,string[] alignment){for(int i=0;i<row.Cells.Count&&i<alignment.Length;i++){string marker=alignment[i].Trim();TextAlignment value=marker.StartsWith(":")&&marker.EndsWith(":")?TextAlignment.Center:marker.EndsWith(":")?TextAlignment.Right:TextAlignment.Left;row.Cells[i].TextAlignment=value;/* N14: a colon marker is an explicit alignment; the paragraph keeps its own value so the writer can tell ":---" from a plain "---" */if(marker.StartsWith(":")||marker.EndsWith(":")){var paragraph=row.Cells[i].Blocks.FirstBlock as Paragraph;if(paragraph!=null)paragraph.TextAlignment=value;}}}
    static void AddRow(TableRowGroup group,string[] cells,bool header,double size,bool dark,string file,string imageLabel){var row=new TableRow();group.Rows.Add(row);foreach(string cell in cells){var c=new TableCell(Para(cell.Trim(),size,dark,file,imageLabel)){BorderBrush=dark?Brush(112,124,142):Brush(173,184,198),BorderThickness=new Thickness(.5),Padding=new Thickness(10,6,10,6)};if(header){c.FontWeight=FontWeights.Bold;c.Background=dark?Brush(68,77,92):Brush(225,231,238);}row.Cells.Add(c);}}
    static Paragraph Para(string value,double size,bool dark,string file,string imageLabel){var p=new Paragraph{Margin=new Thickness(0),LineHeight=Double.NaN};Inline(p.Inlines,ExpandReferences(ProtectEscapes(value)),size,dark,file,imageLabel,0);return p;}
    // #4: CommonMark backslash escapes. The rich HTML/RTF paste writes "\*", "\[", "\_" and "\\"
    // (HtmlPaste.SoftLiteral) and the live preview draws the escaped character
    // (MarkdownLivePreview.ParseInline hides the "\"), so the print, TXT and RTF paths read the same
    // escapes. An escaped punctuation mark becomes a private-use sentinel before the inline patterns
    // run, so no pattern can take it for markup, and the sentinel is turned back into the literal
    // character where the text is emitted. Inline code keeps the pair: a code span has no escapes.
    internal const string EscapablePunctuation="!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    internal static string ProtectEscapes(string value){
        if(value.IndexOf('\\')<0)return value;
        var result=new StringBuilder(value.Length);
        for(int i=0;i<value.Length;i++){char c=value[i];if(c=='\\'&&i+1<value.Length){int index=EscapablePunctuation.IndexOf(value[i+1]);if(index>=0){result.Append((char)(0xE000+index));i++;continue;}}result.Append(c);}
        return result.ToString();
    }
    internal static string RestoreEscapes(string value){return RestoreEscapeMarks(value,false);}
    internal static string RestoreCodeEscapes(string value){return RestoreEscapeMarks(value,true);}
    static string RestoreEscapeMarks(string value,bool code){
        bool marked=false;foreach(char c in value)if(c>=0xE000&&c<=0xE01F){marked=true;break;}
        if(!marked)return value;
        var result=new StringBuilder(value.Length+8);
        for(int i=0;i<value.Length;i++){char c=value[i];if(c>=0xE000&&c<=0xE01F){if(code)result.Append('\\');result.Append(EscapablePunctuation[c-0xE000]);}else result.Append(c);}
        return result.ToString();
    }
    // Math pictures for the print / plain renderer: vector drawings from MathRender (the same engine and
    // syntax rules as the live preview). The ink colour follows the document's dark flag.
    static Brush MathInk(bool dark){return dark?Brush(235,235,235):Brush(32,33,36);}
    static bool AddMathPicture(InlineCollection dest,string value,int at,int open,int close,double size,bool dark){
        string source=RestoreCodeEscapes(value.Substring(open+1,close-open-1));
        if(!MathRender.Check(source))return false;
        double ascent,descent;
        FrameworkElement picture=MathRender.CreatePicture(source,false,size,MathInk(dark),referenceContext==null?"Malgun Gothic":referenceContext.Family,out ascent,out descent);
        if(picture==null)return false;
        if(open>at)dest.Add(new Run(RestoreEscapes(MarkdownPlainText.DecodeEntities(value.Substring(at,open-at)))));
        picture.Margin=new Thickness(0,0,0,-descent);
        dest.Add(new InlineUIContainer(picture){BaselineAlignment=BaselineAlignment.Baseline,Tag="math"});
        return true;
    }
    // A display formula starting at lines[i]: one line "$$ x $$", a "$$" ... "$$" block or a ```math fence
    // (no blank line inside, the closer within MathRender.BlockLines lines). last = its final line.
    static bool TryDisplayMath(string[] lines,int i,out int last,out string source){
        last=i;source=null;string line=lines[i];
        if(MathRender.OneLineDisplay(line,out source))return true;
        char marker='\0';int markerLength=0;bool dollar=MathRender.IsDollarLine(line);
        if(!dollar&&!MathRender.IsMathFence(line,out marker,out markerLength))return false;
        var body=new StringBuilder();
        for(int k=1;k<=MathRender.BlockLines;k++){
            int at=i+k;if(at>=lines.Length)return false;
            string text=lines[at];
            if(String.IsNullOrWhiteSpace(text))return false;
            bool closer=dollar?MathRender.IsDollarLine(text):Regex.IsMatch(text,@"^ {0,3}"+Regex.Escape(new string(marker,markerLength))+Regex.Escape(marker.ToString())+@"*[ \t]*$");
            if(closer){last=at;source=body.ToString();return true;}
            if(body.Length>0)body.Append(' ');
            body.Append(text.Trim());
        }
        return false;
    }
    static void Inline(InlineCollection dest,string value,double size,bool dark,string file,string imageLabel,int depth){
        if(depth>12){dest.Add(new Run(RestoreEscapes(value)));return;}
        // A backslash at a line's end is a hard line break: the character is markup and hides, the
        // live view's line-end rule (every source line is already its own block here, and a
        // multi-line footnote keeps its own newline). An escaped pair arrives as a sentinel, never
        // this raw backslash, so "\\" still restores to one "\".
        if(depth==0&&value.IndexOf('\\')>=0){
            if(value[value.Length-1]=='\\')value=value.Substring(0,value.Length-1);
            if(value.IndexOf("\\\n",StringComparison.Ordinal)>=0)value=value.Replace("\\\n","\n");
        }
        int at=0,p=0,index=0;var matches=token.Matches(value);
        while(p<value.Length){
            while(index<matches.Count&&matches[index].Index<p)index++; // a match inside a link, image or span is read again by the recursion
            bool image;int labelStart,labelEnd,addressStart,addressEnd,end;
            // Inline math "$x$" (the live preview's rules, MathRender.InlineEnd): a vector picture on the
            // text baseline. The span is skipped whole, so "_" and "*" inside a formula are never emphasis.
            // "$$" is never inline, and a formula that does not parse stays as typed.
            if(value[p]=='$'){
                if(p+1<value.Length&&value[p+1]=='$'){p+=2;continue;}
                int mathBudget=value.Length*4+16,mathClose=MathRender.InlineEnd(value,p,ref mathBudget);
                if(mathClose>p+1&&AddMathPicture(dest,value,at,p,mathClose,size,dark)){at=mathClose+1;p=at;continue;}
            }
            if(LinkAt(value,p,out image,out labelStart,out labelEnd,out addressStart,out addressEnd,out end)){
                if(p>at)dest.Add(new Run(RestoreEscapes(MarkdownPlainText.DecodeEntities(value.Substring(at,p-at)))));
                string address,title;TargetParts(value,addressStart,addressEnd,out address,out title);
                if(image)AddImage(dest,value,p,end,value.Substring(labelStart,labelEnd-labelStart),address,file,imageLabel);
                else AddLink(dest,value.Substring(labelStart,labelEnd-labelStart),address,size,dark,file,imageLabel,depth);
                at=end;p=end;continue;
            }
            if(value[p]=='<'){
                // An HTML comment that closes on this same line is markup and hides, exactly as in
                // the live view; one that spans lines is not tracked here and stays literal.
                int comment=MarkdownPlainText.CommentEnd(value,p);
                if(comment>p){
                    if(p>at)dest.Add(new Run(RestoreEscapes(MarkdownPlainText.DecodeEntities(value.Substring(at,p-at)))));
                    at=comment;p=comment;continue;
                }
                // A CommonMark angle autolink (<https://…>, <mailto:…>, <a@b.c>) is a link without
                // its brackets; a bare email address opens as a mail link.
                int autolink=MarkdownPlainText.AutolinkEnd(value,p);
                if(autolink>p){
                    if(p>at)dest.Add(new Run(RestoreEscapes(MarkdownPlainText.DecodeEntities(value.Substring(at,p-at)))));
                    string target=value.Substring(p+1,autolink-p-2);
                    string url=RestoreCodeEscapes(target.IndexOf(':')<0?"mailto:"+target:target);
                    var link=new Hyperlink {Foreground=dark?Brush(120,180,255):Brush(37,99,210),Tag=url,ToolTip=url};
                    link.Inlines.Add(new Run(RestoreCodeEscapes(target)));
                    dest.Add(link);
                    at=autolink;p=autolink;continue;
                }
            }
            if(index>=matches.Count){p++;continue;}
            Match m=matches[index];
            if(m.Index>p){p++;continue;}
            if(m.Index>at)dest.Add(new Run(RestoreEscapes(MarkdownPlainText.DecodeEntities(value.Substring(at,m.Index-at)))));at=m.Index+m.Length;
            if(m.Groups[1].Success||m.Groups[3].Success){var span=new Span();string attrs=m.Groups[1].Success?m.Groups[1].Value:m.Groups[3].Value;string content=m.Groups[1].Success?m.Groups[2].Value:m.Groups[4].Value;
                string tag=m.Groups[1].Success?"span":"font";int nesting=1;foreach(Match closing in Regex.Matches(value.Substring(m.Index+attrs.Length),@"</?"+tag+@"\b[^>]*>",RegexOptions.IgnoreCase)){if(closing.Value.StartsWith("</"))nesting--;else nesting++;if(nesting==0){int closeAt=m.Index+attrs.Length+closing.Index;content=value.Substring(m.Index+attrs.Length,closeAt-m.Index-attrs.Length);at=closeAt+closing.Length;break;}}
                var color=Regex.Match(attrs,@"(?:color\s*:\s*|\bcolor\s*=\s*[""']?)(#[a-fA-F0-9]{3,8}\b|[a-zA-Z]+)");
                if(color.Success){try{span.Foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Groups[1].Value));}catch{}}
                var fs=Regex.Match(attrs,@"font-size\s*:\s*(\d+(?:\.\d+)?)\s*(px|pt|em|%)?",RegexOptions.IgnoreCase);
                if(fs.Success){double n;if(Double.TryParse(fs.Groups[1].Value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out n)){string unit=fs.Groups[2].Value.ToLowerInvariant();span.FontSize=Math.Max(6,Math.Min(120,unit=="em"?n*size:unit=="%"?n*size/100:unit=="pt"?n*96/72:n));}}
                Inline(span.Inlines,content,size,dark,file,imageLabel,depth+1);dest.Add(span);
            } else if(m.Groups[5].Success){dest.Add(new Run(RestoreCodeEscapes(UnpadCode(m.Groups[5].Value))){FontFamily=InlineCodeFont(referenceContext==null?"Malgun Gothic":referenceContext.Family),Background=Brushes.Transparent});}
            else if(m.Groups[6].Success||m.Groups[7].Success){var b=new Bold {FontWeight=FontWeights.ExtraBold};Inline(b.Inlines,m.Groups[6].Success?m.Groups[6].Value:m.Groups[7].Value,size,dark,file,imageLabel,depth+1);dest.Add(b);}
            else if(m.Groups[8].Success){var s=new Span();Inline(s.Inlines,m.Groups[8].Value,size,dark,file,imageLabel,depth+1);StrikeRuns(s.Inlines);dest.Add(s);}
            else if(m.Groups[9].Success||m.Groups[10].Success){var s=new Italic();Inline(s.Inlines,m.Groups[9].Success?m.Groups[9].Value:m.Groups[10].Value,size,dark,file,imageLabel,depth+1);dest.Add(s);}
            else dest.Add(new LineBreak());
            p=at;
        }if(at<value.Length)dest.Add(new Run(RestoreEscapes(MarkdownPlainText.DecodeEntities(value.Substring(at)))));
    }
    // A link "[label](target)" or image "![alt](target)" at p. The closing ")" comes from
    // MarkdownPlainText.TargetEnd, the TXT renderer's and the live preview's scan: a balanced
    // parenthesis in the path, an <angle> destination and a quoted title are all understood, so
    // "[w](https://x/A_(b))" keeps its path and "![a](img_(1).png "t")" hides the title.
    static bool LinkAt(string value,int p,out bool image,out int labelStart,out int labelEnd,out int addressStart,out int addressEnd,out int end){
        image=false;labelStart=0;labelEnd=0;addressStart=0;addressEnd=0;end=0;
        char c=value[p];
        if(c=='!'){if(p+1>=value.Length||value[p+1]!='[')return false;image=true;}
        else if(c!='[')return false;
        int bracket=image?p+1:p;
        int close=value.IndexOf(']',bracket+1);
        if(close<0||close+1>=value.Length||value[close+1]!='(')return false;
        if(!image&&close<=p+1)return false; // a link label needs one character
        int url=MarkdownPlainText.TargetEnd(value,close+2);
        if(url<=close+2)return false; // "[]()" is text, the same rule the TXT renderer reads
        labelStart=bracket+1;labelEnd=close;addressStart=close+2;addressEnd=url;end=url+1;
        return true;
    }
    // "(dest)" or "(dest \"title\")": the title is not part of the URL (Hyperlink.Tag) or of the
    // image path; an <angle> destination may hold spaces.
    static void TargetParts(string value,int start,int stop,out string address,out string title){
        address="";title="";
        int at=start;
        while(at<stop&&Char.IsWhiteSpace(value[at]))at++;
        bool angle=false;
        if(at<stop&&value[at]=='<'){
            int gt=value.IndexOf('>',at+1);
            // Only a closed <...> followed by whitespace, the title or the end is a destination;
            // "<u>v" would otherwise lose its tail.
            if(gt>at&&gt<stop&&(gt+1==stop||Char.IsWhiteSpace(value[gt+1]))){address=value.Substring(at+1,gt-at-1);at=gt+1;angle=true;}
        }
        if(!angle){
            int tokenEnd=at;
            while(tokenEnd<stop&&!Char.IsWhiteSpace(value[tokenEnd]))tokenEnd++;
            address=value.Substring(at,tokenEnd-at);
            at=tokenEnd;
        }
        while(at<stop&&Char.IsWhiteSpace(value[at]))at++;
        if(at<stop&&(value[at]=='"'||value[at]=='\'')){
            char quote=value[at];
            int close=value.IndexOf(quote,at+1);
            if(close>at&&close<stop)title=value.Substring(at+1,close-at-1);
        }
    }
    // The link Run of the old regex branch, now from LinkAt: a footnote reference keeps its
    // superscript number, every other link reads its label as inline text.
    static void AddLink(InlineCollection dest,string label,string address,double size,bool dark,string file,string imageLabel,int depth){
        string url=RestoreCodeEscapes(address);bool footnote=url.StartsWith("#mdnote-footnote-");var link=new Hyperlink{Foreground=footnote?AccentInk(dark):(dark?Brush(120,180,255):Brush(37,99,210))};if(footnote)link.Inlines.Add(new Run(label){FontSize=size*.75,BaselineAlignment=BaselineAlignment.Superscript});else Inline(link.Inlines,label,size,dark,file,imageLabel,depth+1);link.Tag=url;if(footnote)link.Resources["FootnoteId"]=Uri.UnescapeDataString(url.Substring(17));link.ToolTip=url;dest.Add(link);
    }
    // The image Run of the old regex branch: the path is the target without its title, so
    // "![a](img_(1).png "t")" opens "img_(1).png"; a failed load keeps the "[이미지: alt]" Run.
    // The reference percent-encodes '#', '?' and '%' (MarkdownCommands.ImageReference), and the
    // decoded path is escaped again for the Uri, so a name holding them opens under its own name.
    static void AddImage(InlineCollection dest,string value,int start,int end,string alt,string address,string file,string imageLabel){
        string raw=value.Substring(start,end-start);
        try{string location=Uri.UnescapeDataString(RestoreCodeEscapes(address));Uri uri=ImageUri(location,file);if(!uri.IsFile&&uri.Scheme!="https"&&uri.Scheme!="http")throw new IOException("Unsupported image URI");var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=uri.IsFile?BitmapCacheOption.OnLoad:BitmapCacheOption.Default;bitmap.UriSource=uri;bitmap.DecodePixelWidth=ImageDecodeWidth(uri);bitmap.EndInit();var image=new Image {Source=bitmap,Width=bitmap.Width,Stretch=Stretch.Uniform,StretchDirection=System.Windows.Controls.StretchDirection.DownOnly,MaxWidth=700,MaxHeight=700,ToolTip=RestoreEscapes(alt)};/* No RichTextBox surface exists in this build (InlineCodeBackground.cs:5), so the old ActualWidth FindAncestor binding never resolved and MaxWidth came back as no limit. 700 is the width a surface without a page gets (the RTF copy); the print path binds MaxWidth to the page's printable column in InlineCodeBackground.RestorePrintBackground. */dest.Add(new InlineUIContainer(image){Tag="image:"+RestoreCodeEscapes(raw)});}
        catch{dest.Add(new Run((imageLabel??"[이미지: ")+RestoreEscapes(alt)+"]"){Foreground=Brush(130,140,155),Tag="image:"+RestoreCodeEscapes(raw)});}
    }
    // The image target as a Uri: an http(s) address downloads, every other target is a file path
    // relative to the document's folder (an absolute one keeps its drive). A '#' or a '?' in a
    // local path starts a fragment or a query for the Uri class and a lone '%' is an invalid
    // escape, so LocalImageUri escapes them once more; Uri.LocalPath decodes them back, and the
    // bitmap then opens the file under the name it really has (a#b.png, 50%.png).
    static Uri ImageUri(string location,string file){
        Uri uri;
        if(Uri.TryCreate(location,UriKind.Absolute,out uri)){
            if(uri.Scheme=="https"||uri.Scheme=="http")return uri;
            if(!uri.IsFile)throw new IOException("Unsupported image URI");
            return LocalImageUri(uri.LocalPath);
        }
        if(String.IsNullOrEmpty(file))throw new IOException("Save document first");
        return LocalImageUri(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file),location)));
    }
    internal static Uri LocalImageUri(string fullPath){
        // new Uri(path) keeps "%23" literally in LocalPath; UriBuilder escapes # % ? and round-trips them.
        if(fullPath.IndexOfAny(new[]{'#','%','?'})<0||fullPath.StartsWith(@"\\"))return new Uri(fullPath,UriKind.Absolute);
        UriBuilder builder=new UriBuilder();builder.Scheme=Uri.UriSchemeFile;builder.Host="";builder.Path=fullPath;return builder.Uri;
    }
    // CommonMark code-span padding: one leading and one trailing space hide when both are there and
    // the content is not all spaces, the live view's inline rule. MarkdownPlainText calls this same
    // helper, so print and TXT read one rule.
    internal static string UnpadCode(string value){return value.Length>=2&&value.StartsWith(" ")&&value.EndsWith(" ")&&value.Trim(' ').Length>0?value.Substring(1,value.Length-2):value;}
    static void StrikeRuns(InlineCollection inlines){foreach(Inline inline in inlines){if(inline is Run)inline.TextDecorations=TextDecorations.Strikethrough;else if(inline is Span)StrikeRuns(((Span)inline).Inlines);}}
    static Brush MutedInk(bool dark){return dark?Brush(173,180,190):Brush(104,112,120);}
    static Brush AccentInk(bool dark){return dark?Brush(120,180,255):Brush(37,99,210);}
    // Set by --link-probe so a simulated click never opens a browser.
    internal static Action<string,string> LinkOpener=null;
    internal static void OpenLink(string url,string file,Func<string,string> translate=null){
        try{Action<string,string> opener=LinkOpener;if(opener!=null){opener(url,file);return;}if(Program.IsSelfTest)return;Uri uri;if(Uri.TryCreate(url,UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https"||uri.Scheme=="mailto")){Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});return;}
            if(url.StartsWith("#"))return;
            if(file==""||Uri.IsWellFormedUriString(url,UriKind.Absolute))return;
            /* A relative document link may carry a "#heading" fragment ("notes.md#회의", also
               written with a percent-encoded path or a query). The fragment is not part of the
               file name: splitting it before the extension test is what lets ".md#회의" open at
               all, where the old read took the extension "#회의" and answered nothing. */
            string target=url,fragment=string.Empty;
            int hash=target.IndexOf('#');
            if(hash>=0){fragment=target.Substring(hash);target=target.Substring(0,hash);}
            int query=target.IndexOf('?');
            if(query>=0)target=target.Substring(0,query);
            if(target.Length==0)return;
            string local=System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file),Uri.UnescapeDataString(target)));
            string ext=System.IO.Path.GetExtension(local).ToLowerInvariant();
            if(ext==".md"||ext==".markdown"||ext==".txt")MainWindow.OpenLinkedDocument(local,fragment);
        }catch(Exception ex){string message=translate==null?"링크를 열 수 없습니다.\n":translate("링크를 열 수 없습니다.\n");MessageBox.Show(message+ex.Message,"MDNote");}
    }
}
public partial class MainWindow {
    static Window saveConfirmationDialog;
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    static void ForegroundSaveWindow(Window window){
        if(window==null)return;
        var owner=window as MainWindow;
        if(window.WindowState==WindowState.Minimized)window.WindowState=owner!=null&&owner.lastRestorableState==WindowState.Maximized?WindowState.Maximized:WindowState.Normal;
        if(!window.IsVisible){if(owner==null)return;window.Show();}window.Activate();
        IntPtr handle=new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if(handle==IntPtr.Zero)return;
        // A taskbar close is deferred out of WM_SYSCOMMAND. Re-raise the actual owner/dialog,
        // not ActiveWindow (which may be a different MDNote window), without leaving it topmost.
        if(!window.Topmost){SetWindowPos(handle,new IntPtr(-1),0,0,0,0,0x43);SetWindowPos(handle,new IntPtr(-2),0,0,0,0,0x43);}
        BringWindowToTop(handle);SetForegroundWindow(handle);window.Activate();
    }
    void PrepareSaveOwner(){ForegroundSaveWindow(this);}
    void SetupSaveDialogActivation(){SourceInitialized+=delegate{var hwnd=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);if(hwnd!=null)hwnd.AddHook(SaveCloseMessage);};}
    IntPtr SaveCloseMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){
        if(!closeApproved&&!sessionEnding&&(message==0x10||message==0x112&&(wParam.ToInt64()&0xFFF0)==0xF060)){
            var dialog=saveConfirmationDialog;
            if(dialog!=null){ForegroundSaveWindow(dialog.Owner);ForegroundSaveWindow(dialog);}
            else{bool dirty=HasUnsavedChanges;foreach(var tab in documents)if(tab!=activeDocument&&(tab.Recovered||tab.Text!=tab.Saved))dirty=true;if(dirty)PrepareSaveOwner();}
        }
        return IntPtr.Zero;
    }
    void ShowSaveConfirmation(Window dialog){
        var previous=saveConfirmationDialog;saveConfirmationDialog=dialog;
        EventHandler ready=delegate{ForegroundSaveWindow(dialog);};dialog.ContentRendered+=ready;
        // K3: a close queued while this prompt was open is processed once the prompt is answered.
        try{PrepareSaveOwner();dialog.ShowDialog();}finally{dialog.ContentRendered-=ready;saveConfirmationDialog=previous;ResumeDeferredCloses();}
    }
    /* A relative document link: a file that a tab or another window already holds is activated
       exactly like File > Open (user rule A26), and a "#heading" fragment then moves the caret in
       that document; a file no window holds opens in its own window, and the fragment is applied
       once the open has finished. */
    internal static void OpenLinkedDocument(string file,string fragment){
        MainWindow owner=WindowWithFile(Application.Current,file);
        if(owner!=null){if(owner.ActivateOpenedFile(file))owner.GoToLinkAnchor(file,fragment);return;}
        var window=new MainWindow();
        window.OpenFileWithAnchor(file,fragment);
        window.Show();
    }
    /* The open queue's completion reports after the document is applied and laid out, so the jump
       sees the new text; a self-test open is synchronous and jumps at once. A cancelled or failed
       open reports false and moves nothing (the queue shows its own error). */
    internal void OpenFileWithAnchor(string file,string fragment){
        if(Program.IsSelfTest){LoadFile(file);GoToLinkAnchor(file,fragment);return;}
        var operation=new FileOperation {Name=file};
        operation.Completion=new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        EnqueueFileOperation(operation);
        MainWindow target=this;
        operation.Completion.Task.ContinueWith(delegate(System.Threading.Tasks.Task<bool> task){
            bool opened;
            try{opened=task.Result;}catch(Exception){return;} // a failed open keeps the error the queue already reported
            if(!opened)return;
            target.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{target.GoToLinkAnchor(file,fragment);}));
        },System.Threading.Tasks.TaskScheduler.Default);
    }
    // A fragment is the live view's own "#heading" rule (OpenLiveAnchor: AnchorRule over the lines
    // the preview draws as headings). The jump belongs to the document the link named: a tab
    // switch during the open leaves the caret where the user put it. A TXT document has no
    // headings, and a preview that is still attaching after the open gets one retry when that
    // frame settles.
    internal void GoToLinkAnchor(string file,string fragment){
        if(String.IsNullOrEmpty(fragment))return;
        TryGoToLinkAnchor(file,fragment,true);
    }
    void TryGoToLinkAnchor(string file,string fragment,bool retry){
        if(!String.Equals(path,file,StringComparison.OrdinalIgnoreCase))return;
        if(PlainText||source==null||source.Document==null)return;
        if(LivePreviewAttached){OpenLiveAnchor(fragment);return;}
        if(!retry)return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{TryGoToLinkAnchor(file,fragment,false);}));
    }
}
public static class Program {
    [STAThread] public static int Main(string[] args) {
        if(args.Length==2&&args[0]=="--livepreview-spike"){IsSelfTest=false;return LivePreviewSpike.RunWindow(args[1]);}
        if(args.Length==2&&args[0]=="--livepreview-bench"){IsSelfTest=true;return LivePreviewBench.Run(args[1]);}
        if(args.Length==2&&args[0]=="--longline-bench"){IsSelfTest=true;return LongLineBench.Run(args[1]);}
        if(args.Length==4&&args[0]=="--livepreview-shot"){IsSelfTest=true;return MainWindow.CaptureLivePreview(args[1],args[2],int.Parse(args[3]));}
        if(args.Length==2&&args[0]=="--livepreview-probe"){IsSelfTest=true;return LivePreviewProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--noreveal-probe"){IsSelfTest=true;return NoRevealProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--livelayout-probe"){IsSelfTest=true;return MainWindow.VerifyLiveLayout(args[1]);}
        if(args.Length==2&&args[0]=="--live-host-probe"){IsSelfTest=true;return MainWindow.VerifyLiveHost(args[1]);}
        if(args.Length==2&&args[0]=="--link-probe"){IsSelfTest=true;return MainWindow.VerifyLinks(args[1]);}
        if(args.Length==2&&args[0]=="--livesync-probe"){IsSelfTest=true;return MainWindow.VerifyLiveSync(args[1]);}
        if(args.Length==2&&args[0]=="--scenario-probe"){IsSelfTest=true;return ScenarioProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--monkey-probe"){IsSelfTest=true;return MonkeyProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--renderfix-probe"){IsSelfTest=true;return RenderFixProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--uifix-probe"){IsSelfTest=true;return UiFixProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--syntaxcombo-probe"){IsSelfTest=true;return SyntaxComboProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--format-matrix-probe"){IsSelfTest=true;return FormatMatrixProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--noreveal-window-probe"){IsSelfTest=true;return NoRevealWindowProbe.RunNoRevealWindowProbe(args[1]);}
        if(args.Length==2&&args[0]=="--recovery-probe"){IsSelfTest=true;return MainWindow.VerifyRecoveryCleanup(args[1]);}
        if(args.Length==2&&args[0]=="--ui-shot"){IsSelfTest=true;return MainWindow.CaptureUiShots(args[1]);}
        if(args.Length==2&&args[0]=="--userfix-probe"){IsSelfTest=true;return MainWindow.VerifyUserFixes(args[1]);}
        if(args.Length==2&&args[0]=="--clipboard-probe"){IsSelfTest=true;return MainWindow.VerifyClipboard(args[1]);}
        if(args.Length==2&&args[0]=="--history-cap-probe"){IsSelfTest=true;return MainWindow.VerifyHistoryCap(args[1]);}
        if(args.Length==2&&args[0]=="--live-typing-probe"){IsSelfTest=true;return MainWindow.VerifyLiveTypingStability(args[1]);}
        if(args.Length==2&&args[0]=="--table-caret-probe"){IsSelfTest=true;return MainWindow.VerifyTableCaret(args[1]);}
        if(args.Length==2&&args[0]=="--settings-probe"){IsSelfTest=true;return MainWindow.RunSettingsMatrixProbe(args[1]);}
        if(args.Length==2&&args[0]=="--table-commands-probe"){IsSelfTest=true;return MainWindow.VerifyTableCommands(args[1]);}
        if(args.Length==2&&args[0]=="--live-table-review-probe"){IsSelfTest=true;return LiveTableReviewProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--table-abuse-probe"){IsSelfTest=true;return TableAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--format-abuse-probe"){IsSelfTest=true;return FormatAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--visual-shot-probe"){IsSelfTest=true;return VisualShotProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--paste-abuse-probe"){IsSelfTest=true;return PasteAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--history-stress-probe"){IsSelfTest=true;return HistoryStressProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--find-abuse-probe"){IsSelfTest=true;return FindAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--file-abuse-probe"){IsSelfTest=true;return FileAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--window-abuse-probe"){IsSelfTest=true;return WindowAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--mode-switch-abuse-probe"){IsSelfTest=true;return ModeSwitchAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--ime-abuse-probe"){IsSelfTest=true;return ImeAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--large-doc-abuse-probe"){IsSelfTest=true;return LargeDocAbuseProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--print-probe"){IsSelfTest=true;return PrintProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--drop-probe"){IsSelfTest=true;return DropProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--image-save-probe"){IsSelfTest=true;return ImageSaveProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--filechange-probe"){IsSelfTest=true;return FileChangeProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--htmlexport-probe"){IsSelfTest=true;return HtmlExportProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--update-probe"){IsSelfTest=true;return UpdateCheckProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--focusmode-probe"){IsSelfTest=true;return FocusModeProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--search-regex-probe"){IsSelfTest=true;return SearchRegexProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--math-probe"){IsSelfTest=true;return MathProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--md2txt-probe"){IsSelfTest=true;return MainWindow.VerifyMarkdownPlainText(args[1]);}
        if(args.Length==2&&args[0]=="--commands-probe"){IsSelfTest=true;return MainWindow.VerifyMarkdownCommands(args[1]);}
        if(args.Length==2&&args[0]=="--structural-probe"){IsSelfTest=true;return MainWindow.VerifyStructuralInput(args[1]);}
        if(args.Length==2&&args[0]=="--longline-window-probe"){IsSelfTest=true;return MainWindow.VerifyLongLineWindow(args[1]);}
        if(args.Length==2&&args[0]=="--ime-probe"){IsSelfTest=true;return MainWindow.VerifyIme(args[1]);}
        if(args.Length==2&&args[0]=="--ime-click-hang-probe"){IsSelfTest=true;return ImeClickHangProbe.Run(args[1]);}
        if(args.Length==2&&args[0]=="--inputformat-probe"){IsSelfTest=true;return MainWindow.VerifyInputFormat(args[1]);}
        if(args.Length==3&&args[0]=="--file-bench"){IsSelfTest=true;return MainWindow.RunFileBench(args[1],args[2]);}
        if(args.Length>=2&&args[0]=="--file-bench-interact"){IsSelfTest=true;return MainWindow.RunInteractionBench(args.Length>2?args[1]:"--generated-4m",args[args.Length-1]);}
        if(args.Length==2&&args[0]=="--mainwindow-bench"){IsSelfTest=true;return MainWindow.RunMainWindowBench(args[1]);}
        if(args.Length==2&&args[0]=="--undo-bench"){IsSelfTest=true;return MainWindow.RunUndoBench(args[1]);}
        if(args.Length==2&&args[0]=="--gate-bench"){IsSelfTest=true;return MainWindow.RunGateBench(args[1]);}
        if(args.Length==2&&args[0]=="--ui-probe"){IsSelfTest=true;var app=new Application();var window=new MainWindow();window.LoadFile(args[1]);app.Run(window);return 0;}
        if(args.Length==2&&args[0]=="--docfuzz-probe"){IsSelfTest=true;return MainWindow.VerifyFuzz(args[1]);}
        if(args.Length==2&&args[0]=="--toolbar-shot"){IsSelfTest=true;return MainWindow.RunToolbarShot(args[1]);}
        if(args.Length==2&&args[0]=="--rc9-probe"){IsSelfTest=true;var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};var window=new MainWindow {ShowInTaskbar=false};try{window.Show();window.VerifyRC9(args[1]);window.CaptureProbe();window.Close();app.Shutdown();return 0;}catch(Exception ex){File.WriteAllText(Directory.Exists(args[1])?Path.Combine(args[1],"probe-error.txt"):args[1],ex.ToString());app.Shutdown();return 1;}}
        if(args.Length>=3&&(args[0]=="--activation-server"||args[0]=="--activation-client"))return DocumentActivation.ActivationTest(args);
        if(args.Length>1&&args[0]=="--benchmark"){IsSelfTest=true;var timer=Stopwatch.StartNew();var benchmarkApp=new Application();var benchmarkWindow=new MainWindow();benchmarkWindow.Show();benchmarkWindow.UpdateLayout();timer.Stop();File.WriteAllText(args[1],"Main-to-first-layout milliseconds: "+timer.ElapsedMilliseconds+"\r\nWorking set bytes: "+Process.GetCurrentProcess().WorkingSet64+"\r\nManaged heap bytes: "+GC.GetTotalMemory(false)+"\r\nPreference writes disabled; blank document; excludes time before managed Main.\r\n");benchmarkWindow.Close();benchmarkApp.Shutdown();return 0;}
        // The hidden probe runner supplies one output folder. Keep the legacy sample + PNG
        // form, and support that same folder contract without bypassing the hidden desktop.
        if(args.Length==2&&args[0]=="--selftest"){Directory.CreateDirectory(args[1]);return SelfTest(new[]{"--selftest",Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"마크다운 작성 예제.md"),Path.Combine(args[1],"selftest.png")});}
        if(args.Length>0&&args[0]=="--selftest")return SelfTest(args);
        /* 이전 세션 계속하기 (Recovery.cs) restores at every launch, with or without a document
           argument: the argument file then becomes a tab of the first restored window (or selects
           the restored tab that already holds it). */
        return DocumentActivation.Run(args);
    }
    internal static bool IsSelfTest;
    static int SelfTest(string[] args){
        IsSelfTest=true;if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        try{
            string sample=File.ReadAllText(args[1]);
            var doc=Markdown.Parse(sample,16,"Malgun Gothic",Brushes.Black,false,args[1]);
            bool table=false,code=false;foreach(Block b in doc.Blocks){if(b is Table)table=true;if(b.Background!=null)code=true;}if(!table||!code)throw new Exception("Block rendering failed");
            // Inline rendering is checked on its own fixture: the example document quotes markers
            // inside inline code, so literal markers in the sample body are not a failed render.
            var inline=Markdown.Parse("**굵게** 그리고 <span style=\"color:#B91C1C\">색</span>",16,"Malgun Gothic",Brushes.Black,false,"");
            string plain=new TextRange(inline.ContentStart,inline.ContentEnd).Text;
            if(!plain.Contains("굵게")||!plain.Contains("색")||plain.Contains("**")||plain.Contains("<span style="))throw new Exception("Inline rendering failed");
            Markdown.Parse("# 빈 문서\n\n```\n끝나지 않은 코드",16,"Malgun Gothic",Brushes.White,true,"");
            Markdown.Parse("<span style=\"color:invalid;font-size:999999px\">text</span>\n\n[link](javascript:alert)",16,"Malgun Gothic",Brushes.Black,false,"");
            var window=new MainWindow();window.Width=940;window.Height=900;window.Show();window.UpdateLayout();window.ExportGuideAssets();window.VerifyWorkflow(System.IO.Path.GetDirectoryName(args[2]));window.SamplePath(args[1]);window.SetText(sample);window.UpdateLayout();
            Snapshot(window,args[2]);window.SetMode(true);window.UpdateLayout();Snapshot(window,args[2]+"-edit.png");window.CaptureCode(args[2]+"-code.png");
            // Clear the unsaved sample so test shutdown never opens a save prompt.
            window.SetText("");window.Close();File.WriteAllText(args[2]+".result.txt","PASS: live read/edit surface, typing, toolbar bold with undo/redo, save and reopen, live find, MD/TXT conversion roundtrip, multi-page print layout without changing editor state, guide documents, taskbar icon",Encoding.UTF8);return 0;
        }catch(Exception ex){File.WriteAllText(args[2]+".result.txt",ex.ToString());return 1;}
    }
    internal static void Snapshot(Window window,string path){window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle,new Action(delegate{}));window.UpdateLayout();var content=window.Content as FrameworkElement;double captureWidth=content.ActualWidth+content.Margin.Left+content.Margin.Right,captureHeight=content.ActualHeight+content.Margin.Top+content.Margin.Bottom;var drawing=new DrawingVisual();using(var context=drawing.RenderOpen()){var bounds=new Rect(0,0,captureWidth,captureHeight);context.DrawRectangle(window.Background??Brushes.White,null,bounds);}var image=new RenderTargetBitmap((int)Math.Ceiling(captureWidth),(int)Math.Ceiling(captureHeight),96,96,PixelFormats.Pbgra32);image.Render(drawing);image.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var stream=File.Create(path))encoder.Save(stream);}

}
}
