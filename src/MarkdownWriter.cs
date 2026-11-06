using System;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace MDNote {
internal sealed class BlockMeta {
    internal string Kind, Original, Signature;
    internal BlockMeta(string kind,string original){Kind=kind;Original=original;}
}
internal static class MarkdownWriter {
    internal static void Track(Block block,string kind,string raw){var meta=new BlockMeta(kind,raw);block.Tag=meta;meta.Signature=Block(block,false);}
    // A task item has two halves that have to agree: the ListItem tag ("task: " / "task:x") decides
    // the Markdown, while the checkbox in the first block is what the user actually sees. A tag left
    // behind by a multi-block toggle or a paste used to write "- [ ] " on a line with no box, so an
    // item counts as a task only while both halves are there (StructuralInput.EnsureTaskBox keeps
    // them in sync, RemoveTaskBox clears both).
    internal static bool TaskItem(ListItem item){var tag=item==null?null:item.Tag as string;return tag!=null&&tag.StartsWith("task:")&&TaskMarker(TaskParagraph(item.Blocks))!=null;}
    // The first paragraph a task box can live in: the item's first block, or the first paragraph of
    // a quote section. A nested list is not descended into (its items own their own boxes).
    internal static Paragraph TaskParagraph(BlockCollection blocks){
        foreach(Block block in blocks){
            var paragraph=block as Paragraph;
            if(paragraph!=null)return paragraph;
            var section=block as Section;
            if(section!=null){var found=TaskParagraph(section.Blocks);if(found!=null)return found;}
        }
        return null;
    }
    internal static InlineUIContainer TaskMarker(Paragraph paragraph){
        if(paragraph==null)return null;
        foreach(Inline inline in paragraph.Inlines){var marker=inline as InlineUIContainer;if(marker!=null&&Object.Equals(marker.Tag,"marker")&&marker.Child is System.Windows.Controls.CheckBox)return marker;}
        return null;
    }
    internal static bool IsAutoTableGap(Block block){var p=block as Paragraph;return p!=null&&Object.Equals(p.Tag,"mdnote:auto-table-gap")&&new TextRange(p.ContentStart,p.ContentEnd).Text.Length==0;}
    internal static string Write(FlowDocument doc){return JoinBlocks(doc.Blocks,true);}
    // The delimiter row follows the header cell's paragraph alignment. A left column keeps a
    // plain "---" unless the paragraph carries its own left value, which is how ":---" (the
    // align-left command and a parsed ":---") stays distinguishable from an untouched table.
    internal static TextAlignment ColumnAlignment(Table table,int column){if(table==null||column<0||table.RowGroups.Count==0||table.RowGroups[0].Rows.Count==0)return TextAlignment.Left;var row=table.RowGroups[0].Rows[0];if(column>=row.Cells.Count)return TextAlignment.Left;var paragraph=row.Cells[column].Blocks.FirstBlock as Paragraph;return paragraph!=null?paragraph.TextAlignment:row.Cells[column].TextAlignment;}
    internal static bool ExplicitLeftAlignment(TableCell cell){var paragraph=cell==null?null:cell.Blocks.FirstBlock as Paragraph;return paragraph!=null&&paragraph.TextAlignment==TextAlignment.Left&&paragraph.ReadLocalValue(System.Windows.Documents.Block.TextAlignmentProperty)!=DependencyProperty.UnsetValue;}
    // The parser ends a table only on a blank line or a line without "|"; a following block that
    // opens with a pipe would be read back as one more row, so only then is the separator needed.
    // A source that already had a blank line keeps it through the parsed blank paragraph block, so
    // an untouched table followed directly by a pipe-free block stays byte-identical on save.
    internal static bool TableSeparator(Block previous,string raw){if(!(previous is Table)||raw.Length==0)return false;int end=raw.IndexOf('\n');if(end<0)end=raw.Length;int pipe=raw.IndexOf('|');return pipe>=0&&pipe<end;}
    static string JoinBlocks(BlockCollection blocks,bool preserve){var parts=new List<string>();Block previous=null;foreach(Block block in blocks){if(IsAutoTableGap(block))continue;string raw=Block(block,preserve);if(TableSeparator(previous,raw))parts.Add("");parts.Add(raw);previous=block;}return String.Join("\n",parts);}
    internal static string Block(Block block,bool preserve){
        var meta=block.Tag as BlockMeta;string kind=meta==null?"":meta.Kind;string result="";
        var p=block as Paragraph;
        // An alert's title line (QuoteSection): its icon and default title are not source text, so the writer
        // gives back the marker line it was parsed from ("[!TIP]- title"), which keeps the quote an alert.
        if(p!=null&&kind.StartsWith("alert:"))return kind.Substring(6);
        if(p!=null){
            if(!kind.StartsWith("code:")&&!kind.StartsWith("list:")){
                FrameworkContentElement ancestor=p;while(ancestor!=null&&!(ancestor is FlowDocument))ancestor=ancestor.Parent as FrameworkContentElement;
                double normal=ancestor==null?16:((FlowDocument)ancestor).FontSize;
                if(kind.StartsWith("h:")||kind==""){int level=Markdown.HeadingLevel(p,normal);kind=level>0?"h:"+level:"";}
                if(p.BorderThickness.Left>0&&kind=="")kind="quote";else if(kind=="quote"&&p.BorderThickness.Left==0)kind="";
            }
            if(kind.StartsWith("code:")){string code=ParagraphText(p);int longest=2;foreach(System.Text.RegularExpressions.Match marker in System.Text.RegularExpressions.Regex.Matches(code,"`+"))longest=Math.Max(longest,marker.Length);string fence=new string('`',longest+1);result=fence+kind.Substring(5)+"\n"+code+"\n"+fence;}
            else {bool inCell=InTableCell(p);result=Inlines(p.Inlines,p,null,!inCell);if(!inCell&&(kind==""||kind=="blank"||kind.StartsWith("unlisted-indent:")))result=System.Text.RegularExpressions.Regex.Replace(result,@"(?m)^(\s*\d+)\.(?=\s|$)","$1&#46;");if(kind.StartsWith("unlisted-indent:")){int indent;if(Int32.TryParse(kind.Substring(16),out indent))result=new string(' ',Math.Max(0,Math.Min(256,indent)))+result;}if(kind.StartsWith("h:")){int level=Int32.Parse(kind.Substring(2));result=new string('#',level)+" "+result;}else if(kind.StartsWith("footnote:"))result="[^"+kind.Substring(9)+"]: "+result.Replace("\n","\n    ");else if(kind=="quote")result="> "+result.Replace("\n", "\n> ");else if(kind.StartsWith("list:"))result=kind.Substring(5)+result;}
        } else if(block is Table)result=TableBlock((Table)block,preserve);
        else if(block is System.Windows.Documents.List){var list=(System.Windows.Documents.List)block;var lines=new List<string>();int index=list.StartIndex;bool number=list.MarkerStyle==TextMarkerStyle.Decimal;foreach(ListItem item in list.ListItems){/* The "- [ ] " marker goes on the item's first block only, and only while the tag agrees with a visible box (see TaskItem). A quote item's first block is a Section: its box sits in the section's first paragraph, so the writer does not have to descend. */bool first=true;bool task=TaskItem(item);foreach(Block b in item.Blocks){string s=Block(b,preserve);string marker=number?index+". ":"- ";if(task&&first)marker+="["+((string)item.Tag).Substring(5)+"] ";string continuation=new string(' ',first?marker.Length:2);lines.Add((first?marker:"  ")+s.Replace("\n","\n"+continuation));first=false;}index++;}result=String.Join("\n",lines);}
        else if(block is Section){result=JoinBlocks(((Section)block).Blocks,preserve);if(kind=="quote")result="> "+result.Replace("\n","\n> ");}
        else if(block is BlockUIContainer)result="---";
        if(preserve&&meta!=null&&meta.Signature==result)return meta.Original;
        return result;
    }
    // #5: an RTF table keeps merged cells as TableCell.ColumnSpan/RowSpan, so a row can name fewer
    // cells than the table has columns. The Markdown row is laid out on the full grid: a spanned
    // column becomes an empty cell and a rowspan keeps its columns empty on the rows it continues
    // into, so every row and the delimiter row agree. A plain table keeps its old output.
    static string TableBlock(Table table,bool preserve){
        var grids=new List<Dictionary<int,string>>();var carry=new Dictionary<int,int>();var headerCells=new List<TableCell>();int columns=0;
        foreach(TableRowGroup g in table.RowGroups)foreach(TableRow row in g.Rows){
            var placed=new Dictionary<int,string>();int column=0;
            foreach(TableCell cell in row.Cells){
                int held;while(carry.TryGetValue(column,out held)&&held>0){placed[column]="";if(held>1)carry[column]=held-1;else carry.Remove(column);column++;}
                var content=new List<string>();foreach(Block b in cell.Blocks)content.Add(CellPipes(Block(b,preserve)));
                string text=String.Join("<br>",content).Replace("\n","<br>");
                int colspan=cell.ColumnSpan>1?Math.Min(64,cell.ColumnSpan):1;
                int rowspan=cell.RowSpan>1?Math.Min(64,cell.RowSpan):1;
                for(int k=0;k<colspan;k++){placed[column+k]=k==0?text:"";if(rowspan>1)carry[column+k]=rowspan-1;}
                if(grids.Count==0)for(int k=0;k<colspan;k++)headerCells.Add(cell);
                column+=colspan;
            }
            while(carry.Count>0){int next=-1;foreach(int key in carry.Keys)if(key>=column&&(next<0||key<next))next=key;if(next<0)break;placed[next]="";int held=carry[next];if(held>1)carry[next]=held-1;else carry.Remove(next);column=next+1;}
            grids.Add(placed);if(column>columns)columns=column;
        }
        var rows=new List<string>();
        foreach(var placed in grids){var cells=new List<string>();for(int c=0;c<columns;c++){string text;cells.Add(placed.TryGetValue(c,out text)?text:"");}rows.Add("| "+String.Join(" | ",cells)+" |");}
        if(rows.Count>0){var d=new List<string>();for(int i=0;i<columns;i++){TableCell header=i<headerCells.Count?headerCells[i]:null;TextAlignment alignment=header==null?TextAlignment.Left:ColumnAlignment(header);bool explicitLeft=header!=null&&ExplicitLeftAlignment(header);d.Add(alignment==TextAlignment.Center?":---:":alignment==TextAlignment.Right?"---:":explicitLeft?":---":"---");}rows.Insert(1,"| "+String.Join(" | ",d)+" |");}
        return String.Join("\n",rows);
    }
    // A "|" inside a closed `code` span is cell text, not a column separator (the renderer's
    // TablePipes skips the span whole, and MarkdownTableCommands.CellInsertText writes the same
    // rule), so it stays raw; every other pipe is escaped. An opener without its same-length
    // closer is not a span, so its pipes are escaped like plain text.
    static string CellPipes(string value){
        if(value==null||value.IndexOf('|')<0)return value;
        var escaped=new StringBuilder(value.Length+8);int budget=value.Length*3;
        for(int p=0;p<value.Length;){
            char c=value[p];
            if(c=='\\'){escaped.Append(c);if(p+1<value.Length)escaped.Append(value[p+1]);p+=2;continue;}
            if(c=='`'){
                int run=1;while(p+run<value.Length&&value[p+run]=='`')run++;
                int close=MarkdownPlainText.CodeClose(value,p+run,run,ref budget);
                if(close>=0){int end=close+run;escaped.Append(value,p,end-p);p=end;continue;}
                escaped.Append(value,p,run);p+=run;continue;
            }
            if(c=='|')escaped.Append('\\');
            escaped.Append(c);p++;
        }
        return escaped.ToString();
    }
    static TextAlignment ColumnAlignment(TableCell cell){if(cell==null)return TextAlignment.Left;var paragraph=cell.Blocks.FirstBlock as Paragraph;return paragraph!=null?paragraph.TextAlignment:cell.TextAlignment;}
    sealed class Fragment {internal string Text,Style,Link;}
    // One open Markdown wrapper on the render stack. Kind is link/span/strike/bold/italic and
    // Value is the link target, the span style text, or the italic marker ("_" inside bold, else "*").
    sealed class Wrap {internal string Kind,Value;internal int At;internal Wrap(string kind,string value){Kind=kind;Value=value;}}
    static string Inlines(InlineCollection items,Paragraph p,string link){return Inlines(items,p,link,true);}
    // lineMarkers=false inside a pipe-table cell: the cell is written on one line between pipes, so
    // a "-", "+", "#" or "N." there can never start a list or a heading and needs no entity.
    static string Inlines(InlineCollection items,Paragraph p,string link,bool lineMarkers){var fragments=new List<Fragment>();Collect(items,p,link,fragments);return Render(fragments,lineMarkers);}
    // A partial copy walks the ORIGINAL paragraph's inlines and drops only the text outside
    // [from,to): link/footnote metadata stays on the elements, and a run's inherited foreground
    // still equals the paragraph's, so theme-default colours produce no span.
    internal static string InlineRange(Paragraph paragraph,TextPointer from,TextPointer to){var fragments=new List<Fragment>();Collect(paragraph.Inlines,paragraph,null,fragments,from,to);return Render(fragments).TrimEnd('\r','\n');}
    // Adjacent fragments are rendered through a stack of open wrappers (link > span > strike > bold >
    // italic) instead of one Styled call per fragment, so a shared outer style keeps a single marker
    // pair. A bold-nested italic uses "_" and stays inside one bold pair only where the parser accepts
    // it (`**a _b_**`); directly beside a word character the shared bold closes and reopens
    // (`**a****_b_**`) instead of the unparsable `**a*b***`. Identical neighbours are merged first, so
    // inline code is fenced once (`a``b` -> `ab`) and a CRLF split across two runs normalizes as a
    // single line break.
    static string Render(List<Fragment> fragments){return Render(fragments,true);}
    static string Render(List<Fragment> fragments,bool lineMarkers){
        var merged=MergeFragments(fragments);var output=new StringBuilder();var open=new List<Wrap>();var trail=new StringBuilder();var lead=new StringBuilder();
        for(int i=0;i<merged.Count;i++){
            var f=merged[i];
            if(f.Style=="break"||f.Style=="raw"){FlushPending(output,open,trail,lead);output.Append(f.Style=="break"?"\n":f.Text);continue;}
            string[] style=f.Style.Split('|');string normalized=f.Text.Replace("\r\n","\n").Replace('\r','\n');int start=0;
            while(true){int newline=normalized.IndexOf('\n',start);string piece=newline<0?normalized.Substring(start):normalized.Substring(start,newline-start);EmitPiece(piece,style,f.Link,output,open,trail,lead,lineMarkers);if(newline<0)break;FlushPending(output,open,trail,lead);output.Append('\n');start=newline+1;}
        }
        FlushPending(output,open,trail,lead);return output.ToString();
    }
    // Raw markers (image markdown, footnote refs) and LineBreaks keep their own fragment: only text
    // runs of the same style and link become one, which is what the old per-fragment writer did.
    static List<Fragment> MergeFragments(List<Fragment> fragments){
        var result=new List<Fragment>();
        for(int i=0;i<fragments.Count;i++){
            var f=fragments[i];bool joined=false;var text=new StringBuilder(f.Text);
            if(f.Style!="break"&&f.Style!="raw")while(i+1<fragments.Count&&f.Style==fragments[i+1].Style&&String.Equals(f.Link,fragments[i+1].Link,StringComparison.Ordinal)){text.Append(fragments[++i].Text);joined=true;}
            result.Add(joined?new Fragment{Text=text.ToString(),Style=f.Style,Link=f.Link}:f);
        }
        // A CR ending one fragment and an LF starting the next are one line break, e.g. a soft break
        // that a copy split between runs of different styles.
        for(int i=0;i+1<result.Count;i++){var first=result[i];var next=result[i+1];if(first.Style=="break"||first.Style=="raw"||next.Style=="break"||next.Style=="raw")continue;if(first.Text.EndsWith("\r",StringComparison.Ordinal)&&next.Text.StartsWith("\n",StringComparison.Ordinal))first.Text=first.Text.Substring(0,first.Text.Length-1);}
        return result;
    }
    // Emphasis markers may not sit next to a space: a closing delimiter is right-flanking and an opening
    // one left-flanking, so the whitespace is held in trail/lead and released at the next core. Whitespace
    // after a core of the piece goes to trail, whitespace before the first one to lead. Link and span
    // wrappers are not emphasis: a piece that is only whitespace still opens them around the space.
    // Code stays one unit (its backticks protect spaces) and empty pieces emit no markers, keeping blank
    // lines blank.
    static void EmitPiece(string piece,string[] style,string link,StringBuilder output,List<Wrap> open,StringBuilder trail,StringBuilder lead,bool lineMarkers){
        if(piece.Length==0)return;
        var wrappers=Wrappers(style,link);
        if(style[3]=="1"){Transition(output,open,wrappers,trail,lead,'`');output.Append(Code(piece));return;}
        int at=0;bool staged=false;
        while(at<piece.Length){char c=piece[at];if(Char.IsWhiteSpace(c)){(staged?trail:lead).Append(c);at++;continue;}int end=at+1;while(end<piece.Length&&!Char.IsWhiteSpace(piece[end]))end++;Transition(output,open,wrappers,trail,lead,piece[at]);output.Append(Escape(piece.Substring(at,end-at),lineMarkers&&OnlySpacesSinceNewline(output)));staged=true;at=end;}
        if(!staged)Transition(output,open,OuterWrappers(wrappers),trail,lead,' ');
    }
    // Only link and span: a whitespace-only piece still opens them, while emphasis keeps its whitespace
    // deferral (the space stays outside "**"/"*" and the emphasis opens at the next core character).
    // The transition still runs for a whitespace-only piece even with no outer wrapper, so emphasis left
    // open by the previous run is closed before the held whitespace and the space is not dropped.
    static List<Wrap> OuterWrappers(List<Wrap> wrappers){var outer=new List<Wrap>();for(int i=0;i<wrappers.Count;i++){string kind=wrappers[i].Kind;if(kind!="link"&&kind!="span")break;outer.Add(wrappers[i]);}return outer;}
    // A rich paste inside an existing emphasis run drops that run's delimiter (PasteSupport.cs
    // PasteBoldSuppressed etc.); the style flags stay on the fragments, only their marker pair is
    // withheld, so the text keeps the outer run and cannot close it early.
    static List<Wrap> Wrappers(string[] style,string link){
        var list=new List<Wrap>();
        if(link!=null)list.Add(new Wrap("link",link));
        string css="";if(style[4]!="")css+="color:"+style[4]+";";if(style[5]!="")css+="font-size:"+style[5]+"px;";
        if(css!="")list.Add(new Wrap("span",css.TrimEnd(';')));
        bool bold=style[0]=="1"&&!MainWindow.PasteBoldSuppressed;
        bool italic=style[1]=="1"&&!MainWindow.PasteItalicSuppressed;
        bool strike=style[2]=="1"&&!MainWindow.PasteStrikeSuppressed;
        if(strike)list.Add(new Wrap("strike",""));
        if(bold)list.Add(new Wrap("bold",""));
        if(italic)list.Add(new Wrap("italic",bold?"_":"*"));
        return list;
    }
    static int CommonPrefix(List<Wrap> open,List<Wrap> wrappers){int count=Math.Min(open.Count,wrappers.Count),i=0;while(i<count&&open[i].Kind==wrappers[i].Kind&&String.Equals(open[i].Value,wrappers[i].Value,StringComparison.Ordinal))i++;return i;}
    // Open markers go on those the new fragment does not share, outermost first; markers are removed
    // innermost first. Link and span wrappers open before pending whitespace and close after it, so a
    // space stays inside "<span> b</span>" and "[ ](u)"; only emphasis markers move across whitespace.
    // A bold-nested italic is written with "_", which the parser only accepts beside a non-word character.
    // Opening one directly after a word character would force "*" and leave a "***" run when both close
    // ("**a*b***" reopens as bold "a*b" plus a literal star), so the shared bold is closed and reopened
    // around the italic part there: "**a****_b_**", which the parser reads as bold then bold+italic.
    static void Transition(StringBuilder output,List<Wrap> open,List<Wrap> wrappers,StringBuilder trail,StringBuilder lead,char next){
        int common=CommonPrefix(open,wrappers);
        if(ItalicOpenBreak(open,wrappers,common,output,trail,lead))common=Math.Min(common,OuterCount(open));
        else SwitchItalicClose(output,open,wrappers,common,trail,lead,next);
        if(common<wrappers.Count||common>0){
            CloseFrom(output,open,Math.Max(common,OuterCount(open)));Release(output,trail);CloseFrom(output,open,common);
            int inner=Math.Max(common,OuterCount(wrappers));OpenFrom(output,open,wrappers,common,inner);Release(output,lead);OpenFrom(output,open,wrappers,inner,wrappers.Count);
        }
        // No new wrappers: an emphasis run that ends here closes BEFORE the held whitespace, so
        // the closer is never preceded by a space ("**a **"); a link/span that is closing keeps
        // the space inside it, exactly like the branch above.
        else{CloseFrom(output,open,OuterCount(open));Release(output,trail);CloseFrom(output,open,0);Release(output,lead);}
    }
    static int OuterCount(List<Wrap> list){int i=0;while(i<list.Count&&(list[i].Kind=="link"||list[i].Kind=="span"))i++;return i;}
    static int KindIndex(List<Wrap> list,string kind){for(int i=0;i<list.Count;i++)if(list[i].Kind==kind)return i;return -1;}
    // True when a bold-nested italic "_" is about to open directly after a word character (no pending
    // whitespace), where the parser's (?<!\w)_..._(?!\w) would leave the marker as literal text.
    static bool ItalicOpenBreak(List<Wrap> open,List<Wrap> wrappers,int common,StringBuilder output,StringBuilder trail,StringBuilder lead){
        int italic=KindIndex(wrappers,"italic"),bold=KindIndex(wrappers,"bold");
        if(italic<0||italic<common||bold<0||italic<bold||bold>=common)return false;
        return trail.Length==0&&lead.Length==0&&output.Length>0&&IsWordChar(output[output.Length-1]);
    }
    // A "_" italic inside a still-open bold cannot close before a word character, so the recorded opener
    // is rewritten to "*": "*x*" stays one pair inside the bold (which closes later with "**"). The "*"
    // closer lands on the word character, not on the bold marker, so no "***" run is formed.
    static void SwitchItalicClose(StringBuilder output,List<Wrap> open,List<Wrap> wrappers,int common,StringBuilder trail,StringBuilder lead,char next){
        if(trail.Length>0||lead.Length>0||!IsWordChar(next)||KindIndex(wrappers,"italic")>=0)return;
        int openItalic=KindIndex(open,"italic"),openBold=KindIndex(open,"bold");
        if(openItalic<common||openBold<0||openBold>=common)return;
        var wrap=open[openItalic];if(wrap.Value!="_")return;
        output[wrap.At]='*';wrap.Value="*";
    }
    static void CloseFrom(StringBuilder output,List<Wrap> open,int keep){for(int i=open.Count-1;i>=keep;i--){output.Append(CloseMarker(open[i]));open.RemoveAt(i);}}
    static void OpenFrom(StringBuilder output,List<Wrap> open,List<Wrap> wrappers,int from,int to){for(int i=from;i<to;i++){var wrap=wrappers[i];wrap.At=output.Length;output.Append(OpenMarker(wrap));open.Add(wrap);}}
    static readonly System.Text.RegularExpressions.Regex wordChar=new System.Text.RegularExpressions.Regex(@"\w");
    // The parser's italic-underscore guards use .NET "\w" (L*, Mn, Nd, Pc), which is wider than
    // Char.IsLetterOrDigit: combining marks and connector punctuation count as word characters too.
    internal static bool IsWordChar(char c){return wordChar.IsMatch(c.ToString());}
    static void Release(StringBuilder output,StringBuilder buffer){if(buffer.Length>0){output.Append(buffer);buffer.Length=0;}}
    // At a line break/paragraph end the emphasis layers close before the trailing space and the
    // link/span layers after it, so "**a **" never appears (same rule as Transition).
    static void FlushPending(StringBuilder output,List<Wrap> open,StringBuilder trail,StringBuilder lead){CloseFrom(output,open,OuterCount(open));Release(output,trail);CloseFrom(output,open,0);Release(output,lead);}
    static string OpenMarker(Wrap wrap){switch(wrap.Kind){case "link":return "[";case "span":return "<span style=\""+wrap.Value+"\">";case "strike":return "~~";case "bold":return "**";default:return wrap.Value;}}
    static string CloseMarker(Wrap wrap){switch(wrap.Kind){case "link":return "]("+wrap.Value.Replace(")","%29")+")";case "span":return "</span>";case "strike":return "~~";case "bold":return "**";default:return wrap.Value;}}
    static double NormalSize(Paragraph paragraph){FrameworkContentElement parent=paragraph;while(parent!=null&&!(parent is FlowDocument))parent=parent.Parent as FrameworkContentElement;return parent is FlowDocument?((FlowDocument)parent).FontSize:16;}
    static void Collect(InlineCollection items,Paragraph paragraph,string link,List<Fragment> result){Collect(items,paragraph,link,result,null,null);}
    static void Collect(InlineCollection items,Paragraph paragraph,string link,List<Fragment> result,TextPointer from,TextPointer to){
        foreach(Inline item in items){
            if(from!=null&&(item.ContentEnd.CompareTo(from)<=0||item.ContentStart.CompareTo(to)>=0))continue;
            if(Object.Equals(item.Tag,"marker"))continue;
            if(item.Tag is string&&((string)item.Tag).StartsWith("image:")){result.Add(new Fragment{Text=((string)item.Tag).Substring(6),Style="raw"});continue;}
            var run=item as Run;
            if(run!=null){string text=run.Text;if(from!=null){int startIndex=0,endIndex=text.Length;if(from.CompareTo(run.ContentStart)>0)startIndex=Math.Max(0,Math.Min(text.Length,run.ContentStart.GetOffsetToPosition(from)));if(to.CompareTo(run.ContentEnd)<0)endIndex=Math.Max(0,Math.Min(text.Length,run.ContentStart.GetOffsetToPosition(to)));if(endIndex<=startIndex)continue;if(startIndex>0||endIndex<text.Length)text=text.Substring(startIndex,endIndex-startIndex);}if(text.Length==0)continue;
                bool bold=run.FontWeight.ToOpenTypeWeight()>=600&&Markdown.HeadingLevel(paragraph,NormalSize(paragraph))==0&&!TableHeaderBold(run,paragraph);
                bool italic=run.FontStyle==FontStyles.Italic;
                bool strike=Decorated(run,TextDecorationLocation.Strikethrough);
                bool code=Markdown.IsInlineCodeFont(run.FontFamily)&&!Markdown.IsInlineCodeFont(paragraph.FontFamily);
                string color="";var brush=run.Foreground as SolidColorBrush;var normal=paragraph.Foreground as SolidColorBrush;
                var ancestor=run.Parent as TextElement;while(ancestor!=null&&!(ancestor is Paragraph)){if(ancestor is Hyperlink){normal=ancestor.Foreground as SolidColorBrush;break;}ancestor=ancestor.Parent as TextElement;}
                if(brush!=null&&normal!=null&&brush.Color!=normal.Color)color="#"+brush.Color.R.ToString("X2")+brush.Color.G.ToString("X2")+brush.Color.B.ToString("X2");
                string size=Math.Abs(run.FontSize-paragraph.FontSize)>.01?run.FontSize.ToString("0.##",CultureInfo.InvariantCulture):"";
                result.Add(new Fragment{Text=text,Link=link,Style=(bold?"1":"0")+"|"+(italic?"1":"0")+"|"+(strike?"1":"0")+"|"+(code?"1":"0")+"|"+color+"|"+size});
            } else if(item is LineBreak)result.Add(new Fragment{Text="",Style="break",Link=null});
            else if(item is Span){var h=item as Hyperlink;if(h!=null&&h.Resources.Contains("FootnoteId")){result.Add(new Fragment {Text="[^"+(string)h.Resources["FootnoteId"]+"]",Style="raw"});continue;}Collect(((Span)item).Inlines,paragraph,h==null?link:(h.Tag as string??(h.NavigateUri==null?"":h.NavigateUri.ToString())),result,from,to);}
        }
    }
    // A Markdown table header is bold by construction (the parser and the cell editor put the weight
    // on the header TableCell), so the inherited weight must not serialize as a ** marker: the header
    // row is already recognised by position. A bold span written inside the cell keeps its own local
    // weight and still serializes as **.
    static bool TableHeaderBold(Run run,Paragraph paragraph){
        var cell=paragraph.Parent as TableCell;
        if(cell==null)return false;
        var row=cell.Parent as TableRow;
        if(row==null)return false;
        var group=row.Parent as TableRowGroup;var table=group==null?null:group.Parent as Table;
        if(table==null||table.RowGroups.Count==0||table.RowGroups[0].Rows.Count==0||!Object.ReferenceEquals(table.RowGroups[0].Rows[0],row))return false;
        for(TextElement element=run;element!=null&&!Object.ReferenceEquals(element,cell);element=element.Parent as TextElement)if(element.ReadLocalValue(TextElement.FontWeightProperty)!=DependencyProperty.UnsetValue)return false;
        return true;
    }
    internal static bool Decorated(Inline inline,TextDecorationLocation location){for(TextElement e=inline;e!=null&&!(e is Paragraph);e=e.Parent as TextElement){var i=e as Inline;if(i!=null&&i.TextDecorations!=null)foreach(TextDecoration d in i.TextDecorations)if(d.Location==location)return true;}return false;}
    internal static string Code(string value){int longest=0;foreach(System.Text.RegularExpressions.Match run in System.Text.RegularExpressions.Regex.Matches(value,"`+"))longest=Math.Max(longest,run.Length);string fence=new string('`',longest+1);bool pad=value.StartsWith("`")||value.EndsWith("`")||(value.StartsWith(" ")&&value.EndsWith(" ")&&value.Trim(' ').Length>0);return fence+(pad?" ":"")+value+(pad?" ":"")+fence;}
    internal static string ParagraphText(Paragraph paragraph){return new TextRange(paragraph.ContentStart,paragraph.ContentEnd).Text;}
    // A Markdown line "starts" while only spaces or tabs have been written since the last newline.
    // Leading whitespace from the paragraph text is released before its first run, so " - item"
    // reaches Escape with the space already in the output and still needs the "-" entity. Emphasis,
    // link and span wrappers emit their own marker character first ("**-", "<span>-", "[-"), which
    // already keeps "-", "+" and "#" away from the line start, so those stay unescaped.
    internal static bool OnlySpacesSinceNewline(StringBuilder output){for(int i=output.Length-1;i>=0;i--){char c=output[i];if(c=='\n')return true;if(c!=' '&&c!='\t')return false;}return true;}
    // Nothing inside a pipe-table cell can become a list or a heading: every cell block is joined
    // with "<br>" into one line between pipes, so the line-start escapes are only noise there.
    internal static bool InTableCell(Paragraph paragraph){for(FrameworkContentElement element=paragraph;element!=null;element=element.Parent as FrameworkContentElement)if(element is TableCell)return true;return false;}
    // b53: "#", "-", "+" only start markup at the beginning of a line and a lone "~" is plain text, so they are
    // kept literal elsewhere (the user saw "Ctrl&#43;F" and "2&#126;3" in saved files); "~~" still escapes.
    // #4: a literal backslash is written as "\\" first, so the Markdown the RTF paste produces
    // shows the backslash in the preview and in print instead of turning "\*" into an escape.
    static string Escape(string s,bool lineStart){s=s.Replace("\\","\\\\").Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace("*","&#42;").Replace("_","&#95;").Replace("[","&#91;").Replace("]","&#93;").Replace("`","&#96;");if(s.Contains("~~"))s=s.Replace("~","&#126;");if(lineStart&&s.Length>0&&(s[0]=='#'||s[0]=='-'||s[0]=='+'))s=(s[0]=='#'?"&#35;":s[0]=='-'?"&#45;":"&#43;")+s.Substring(1);return s.Replace("\r\n","<br>").Replace("\n","<br>");}
}
}
