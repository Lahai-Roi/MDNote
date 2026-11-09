using System;using System.Text;using System.Collections.Generic;using System.Text.RegularExpressions;using System.Windows.Media;
namespace MDNote {public partial class MainWindow {
sealed class PasteNode {internal string Tag="",Text="",Raw="";internal List<PasteNode> Children=new List<PasteNode>();}
sealed class PasteStyle {internal bool Bold,Italic,Strike,Highlight;internal string Color="",Link="",Tags="";internal PasteStyle Copy(){return (PasteStyle)MemberwiseClone();}}
sealed class PasteChunk {internal string Text;internal PasteStyle Style;internal bool Break,Code;}
static string HtmlAttribute(string raw,string name){var m=Regex.Match(raw,@"\b"+Regex.Escape(name)+@"\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",RegexOptions.IgnoreCase);return m.Success?System.Net.WebUtility.HtmlDecode(m.Groups[1].Success?m.Groups[1].Value:m.Groups[2].Success?m.Groups[2].Value:m.Groups[3].Value):"";}
static PasteNode ReadPasteHtml(string html){var root=new PasteNode();var stack=new Stack<PasteNode>();stack.Push(root);foreach(Match token in Regex.Matches(html,@"<!--[\s\S]*?-->|<(?:[^'"">]|""[^""]*""|'[^']*')*>|[^<]+")){string part=token.Value;if(part.StartsWith("<!--"))continue;if(!part.StartsWith("<")){stack.Peek().Children.Add(new PasteNode {Text=System.Net.WebUtility.HtmlDecode(part)});continue;}var match=Regex.Match(part,@"^<\s*(/?)\s*([a-zA-Z0-9]+)");if(!match.Success)continue;string tag=match.Groups[2].Value.ToLowerInvariant();bool close=match.Groups[1].Length>0;if(close){bool found=false;foreach(var n in stack)if(n.Tag==tag){found=true;break;}if(found)while(stack.Count>1){if(stack.Pop().Tag==tag)break;}continue;}if((tag=="li"&&stack.Peek().Tag=="li")||((tag=="td"||tag=="th")&&(stack.Peek().Tag=="td"||stack.Peek().Tag=="th"))||(tag=="tr"&&stack.Peek().Tag=="tr")||(tag=="p"&&stack.Peek().Tag=="p"))stack.Pop();var node=new PasteNode {Tag=tag,Raw=part};stack.Peek().Children.Add(node);if(!part.EndsWith("/>")&&tag!="br"&&tag!="hr"&&tag!="img"&&tag!="meta"&&tag!="link"&&tag!="input"&&tag!="wbr"&&tag!="col"&&tag!="colgroup")stack.Push(node);}return root;}
static bool PasteHidden(PasteNode n){return n.Tag=="script"||n.Tag=="style"||n.Tag=="head";}
static bool PasteBlock(PasteNode n){string tag=n.Tag;return tag=="html"||tag=="body"||tag=="p"||tag=="div"||tag=="section"||tag=="article"||tag=="header"||tag=="footer"||tag=="ul"||tag=="ol"||tag=="li"||tag=="blockquote"||tag=="pre"||tag=="table"||tag=="hr"||(tag.Length==2&&tag[0]=='h'&&tag[1]>='1'&&tag[1]<='6');}
// #20: the live editor shows the source, so pasted text there is escaped only where it would
// otherwise turn into markup (backslash escapes, which the preview hides) instead of the rich
// path's blanket entities ("&#45;" for every hyphen).
[ThreadStatic] static bool softPasteLiteral;
// Runs are escaped in full: two runs kept as typed can still make markup together. RelaxPasteBrackets undoes the bracket escapes line by line afterwards.
static string PasteLiteral(string value){return softPasteLiteral?FullSoftLiteral(value):FullLiteralMarkdown(value);}
// 11-01 user: a pasted "[사진 1]" showed as "\[사진 1\]" in the source. A line keeps its brackets unescaped when it reads back the same.
static string RelaxPasteBrackets(string markdown){
    if(markdown==null||markdown.IndexOf("\\[",StringComparison.Ordinal)<0&&markdown.IndexOf("\\]",StringComparison.Ordinal)<0||markdown.Length>256*1024)return markdown;
    try{
        string[] lines=markdown.Split('\n');
        for(int i=0;i<lines.Length;i++){string line=lines[i];if(line.IndexOf('\\')<0)continue;
            string relaxed=Regex.Replace(line,@"(?<!\\)\\([\[\]])","$1");
            if(relaxed!=line&&Canonical(MarkdownPlainText.Convert(relaxed.TrimEnd('\r')))==Canonical(MarkdownPlainText.Convert(line.TrimEnd('\r'))))lines[i]=relaxed;}
        return String.Join("\n",lines);
    }catch(Exception){return markdown;}
}
static string SoftLiteral(string value){return MinimalLiteral(value,FullSoftLiteral);}
// 11-08 user ("백틱은 그대로 둠"): a backtick in pasted web text stays a backtick, so "`코드`" copied from a page pastes as inline code instead of "\`코드\`".
static string FullSoftLiteral(string value){var escaped=new StringBuilder(value.Length+8);foreach(char c in value){if(c=='\\'||c=='*'||c=='_'||c=='['||c==']'||c=='<'||c=='~')escaped.Append('\\');escaped.Append(c);}string result=Regex.Replace(escaped.ToString(),@"&(?=#?[A-Za-z0-9]+;)","\\&");result=Regex.Replace(result,@"(?m)^([ \t]*)([#>]|[-+=](?=[ \t]|\r?$))","$1\\$2");return Regex.Replace(result,@"(?m)^([ \t]*\d+)([.)])(?=[ \t]|\r?$)","$1\\$2");}
static PasteStyle HtmlStyle(PasteNode node,PasteStyle parent){var style=parent.Copy();string tag=node.Tag,css=HtmlAttribute(node.Raw,"style");if(tag=="b"||tag=="strong")style.Bold=true;if(tag=="i"||tag=="em")style.Italic=true;if(tag=="s"||tag=="del"||tag=="strike")style.Strike=true;if(tag=="mark")style.Highlight=true;if(tag=="sup"||tag=="sub"||tag=="kbd")style.Tags+=tag+",";var weight=Regex.Match(css,@"(?:^|;)\s*font-weight\s*:\s*([^;]+)",RegexOptions.IgnoreCase);if(weight.Success){string w=weight.Groups[1].Value.Trim();int number;style.Bold=w.Equals("bold",StringComparison.OrdinalIgnoreCase)||(Int32.TryParse(w,out number)&&number>=600);}var italic=Regex.Match(css,@"(?:^|;)\s*font-style\s*:\s*([^;]+)",RegexOptions.IgnoreCase);if(italic.Success)style.Italic=italic.Groups[1].Value.Trim().Equals("italic",StringComparison.OrdinalIgnoreCase);if(Regex.IsMatch(css,@"text-decoration[^:]*:\s*[^;]*line-through",RegexOptions.IgnoreCase))style.Strike=true;var color=Regex.Match(css,@"(?:^|;)\s*color\s*:\s*(#[0-9a-f]{3,8}|rgb\([^)]*\)|[a-z]+)",RegexOptions.IgnoreCase);if(color.Success){try{string value=color.Groups[1].Value;var rgb=Regex.Match(value,@"rgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\)");var parsed=rgb.Success?Color.FromRgb(Byte.Parse(rgb.Groups[1].Value),Byte.Parse(rgb.Groups[2].Value),Byte.Parse(rgb.Groups[3].Value)):(Color)ColorConverter.ConvertFromString(value);style.Color=NeutralPasteColor(parsed)?"":"#"+parsed.R.ToString("X2")+parsed.G.ToString("X2")+parsed.B.ToString("X2");}catch(FormatException){}catch(ArgumentException){}catch(OverflowException){}}if(tag=="a"){Uri uri;string href=HtmlAttribute(node.Raw,"href");if(Uri.TryCreate(href,UriKind.Absolute,out uri)&&(uri.Scheme=="http"||uri.Scheme=="https"||uri.Scheme=="mailto"))style.Link=uri.AbsoluteUri.Replace(")","%29");}return style;}
static string PastePlain(PasteNode root){var result=new StringBuilder();var nodes=new Stack<PasteNode>();nodes.Push(root);while(nodes.Count>0){var n=nodes.Pop();if(PasteHidden(n))continue;if(n.Tag=="br")result.Append('\n');else result.Append(n.Text);for(int i=n.Children.Count-1;i>=0;i--)nodes.Push(n.Children[i]);}return result.ToString();}
static void HtmlChunks(PasteNode node,PasteStyle parent,List<PasteChunk> chunks,int depth){if(PasteHidden(node)||PasteMsoIgnore(node))return;if(depth>64){chunks.Add(new PasteChunk {Text=PastePlain(node),Style=parent});return;}var style=HtmlStyle(node,parent);if(node.Tag=="br"){chunks.Add(new PasteChunk {Break=true,Text="",Style=style});return;}if(node.Tag=="img"){chunks.Add(new PasteChunk {Text=HtmlAttribute(node.Raw,"alt"),Style=style});return;}if(node.Tag=="code"){chunks.Add(new PasteChunk {Text=Regex.Replace(PastePlain(node),@"\r\n?|\n"," "),Style=style,Code=true});return;}if(node.Tag==""){string value=Regex.Replace(node.Text,@"[\t\r\n ]+"," ");chunks.Add(new PasteChunk {Text=value,Style=style});}foreach(var child in node.Children)HtmlChunks(child,style,chunks,depth+1);}
static bool SamePasteStyle(PasteStyle a,PasteStyle b){return a.Bold==b.Bold&&a.Italic==b.Italic&&a.Strike==b.Strike&&a.Highlight==b.Highlight&&a.Tags==b.Tags&&a.Color==b.Color&&a.Link==b.Link;}
static string HtmlInline(List<PasteNode> nodes,PasteStyle style){var chunks=new List<PasteChunk>();foreach(var node in nodes)HtmlChunks(node,style,chunks,0);var result=new StringBuilder();bool afterBreak=false;for(int i=0;i<chunks.Count;i++){var chunk=chunks[i];if(chunk.Break){result.Append('\n');afterBreak=true;continue;}string text=chunk.Text;if(i+1<chunks.Count&&!chunks[i+1].Break&&chunks[i+1].Code==chunk.Code&&SamePasteStyle(chunk.Style,chunks[i+1].Style)){var merged=new StringBuilder(text);while(i+1<chunks.Count&&!chunks[i+1].Break&&chunks[i+1].Code==chunk.Code&&SamePasteStyle(chunk.Style,chunks[i+1].Style))merged.Append(chunks[++i].Text);text=merged.ToString();}if(text.Length==0)continue;
        // Y1: an emphasis marker may not touch whitespace (a closer preceded by a space is not
        // right-flanking, so "**Word **" renders literal stars). Leading/trailing whitespace moves
        // outside the markers, and a run that is only whitespace keeps its text but loses the pair.
        string lead="",trail="",rendered;
        if(chunk.Code)rendered=MarkdownWriter.Code(text);
        else{
            int first=0,last=text.Length;
            while(first<last&&char.IsWhiteSpace(text[first]))first++;
            while(last>first&&char.IsWhiteSpace(text[last-1]))last--;
            lead=text.Substring(0,first);trail=text.Substring(last);
            /* HTML drops the whitespace that starts the line after a break, so an indented
               "<br>\n     text" (Excel's saved form) keeps only the break itself. */
            if(afterBreak&&lead.Trim(' ','\t','\r','\n').Length==0)lead="";
            rendered=PasteLiteral(text.Substring(first,last-first));
        }
        afterBreak=false;
        if(rendered.Length>0){
            // Y2: inside a run of the same kind the pasted text is already styled, so its own pair
            // is dropped (the nesting would close the outer run early). See PasteSupport.cs.
            bool bold=chunk.Style.Bold&&!PasteBoldSuppressed;
            bool italic=chunk.Style.Italic&&!PasteItalicSuppressed;
            bool strike=chunk.Style.Strike&&!PasteStrikeSuppressed;
            if(italic){string marker=bold?"_":"*";rendered=marker+rendered+marker;}
            if(bold)rendered="**"+rendered+"**";
            if(strike)rendered="~~"+rendered+"~~";
            if(chunk.Style.Highlight&&!PasteHighlightSuppressed)rendered="=="+rendered+"==";
            string[] tags=chunk.Style.Tags.Split(new[]{','},StringSplitOptions.RemoveEmptyEntries);
            for(int t=tags.Length-1;t>=0;t--)rendered="<"+tags[t]+">"+rendered+"</"+tags[t]+">";
        }
        if(lead.Length>0||trail.Length>0)rendered=lead+rendered+trail;   /* whitespace only, no escaping needed */
        var s=chunk.Style;if(s.Color!="")rendered="<span style=\"color:"+s.Color+"\">"+rendered+"</span>";if(s.Link!="")rendered="["+rendered+"]("+s.Link+")";result.Append(rendered);}return result.ToString();}
// #18: block siblings are separate Markdown paragraphs, so they are one blank line apart.
// A block that came through empty (an explicit <p><br></p>) still occupies its own single
// line, which keeps the blank-paragraph counts of the existing HTML paste tests unchanged.
static string JoinPasteBlocks(List<string> blocks){var result=new StringBuilder();for(int i=0;i<blocks.Count;i++){if(i>0)result.Append(!String.IsNullOrWhiteSpace(blocks[i-1])&&!String.IsNullOrWhiteSpace(blocks[i])?"\n\n":"\n");result.Append(blocks[i]);}return result.ToString();}
static string HtmlChildren(PasteNode node,PasteStyle style,int depth){if(depth>64)return PasteLiteral(PastePlain(node));var result=new List<string>();var inline=new List<PasteNode>();bool previousMso=false;foreach(var child in node.Children){if(PasteHidden(child))continue;if(PasteBlock(child)){if(inline.Count>0){string segment=HtmlInline(inline,style);if(!String.IsNullOrWhiteSpace(segment)){result.Add(segment);previousMso=false;}inline.Clear();}string block=HtmlBlock(child,style,depth+1);bool mso=PasteMsoList(child);/* #15: a Word list is one mso-list paragraph per item, so consecutive items are written on one newline (a blank line would end the list); the whitespace text between the tags is not a paragraph */if(mso&&previousMso&&result.Count>0)result[result.Count-1]=result[result.Count-1]+"\n"+block;else if(block.Length>0||child.Tag=="p"||child.Tag=="div")result.Add(block);previousMso=mso;}else inline.Add(child);}if(inline.Count>0){string segment=HtmlInline(inline,style);if(!String.IsNullOrWhiteSpace(segment))result.Add(segment);}return JoinPasteBlocks(result);}
// Rows are the table's own <tr>s wherever a producer nests them: Excel opens the clipboard
// fragment with "<col ...>", and a walk that only entered thead/tbody/tfoot left every row under
// that col element, so the whole range converted to nothing. A nested table's rows belong to the
// inner table and are not collected here.
static void HtmlRows(PasteNode node,List<PasteNode> rows){var pending=new Stack<PasteNode>();pending.Push(node);while(pending.Count>0){var current=pending.Pop();if(current.Tag=="tr"){rows.Add(current);continue;}for(int i=current.Children.Count-1;i>=0;i--){var child=current.Children[i];if(child.Tag!=""&&child.Tag!="table")pending.Push(child);}}}
static string HtmlBlock(PasteNode node,PasteStyle parent,int depth){if(PasteHidden(node))return "";var style=HtmlStyle(node,parent);if(depth>64)return PasteLiteral(PastePlain(node));string tag=node.Tag;if(tag=="pre"){string raw=Canonical(PastePlain(node));int longest=2;foreach(Match tick in Regex.Matches(raw,"`+"))longest=Math.Max(longest,tick.Length);string fence=new string('`',longest+1);return fence+"\n"+raw+"\n"+fence;}if(tag=="hr")return "---";if(tag=="p"&&PasteMsoList(node))return HtmlMsoList(node,style,depth+1);if(tag=="ul"||tag=="ol"){int number=1;Int32.TryParse(HtmlAttribute(node.Raw,"start"),out number);if(number<1)number=1;var items=new List<string>();foreach(var item in node.Children){if(item.Tag!="li")continue;int explicitNumber;if(Int32.TryParse(HtmlAttribute(item.Raw,"value"),out explicitNumber)&&explicitNumber>0)number=explicitNumber;string marker=tag=="ol"?number+". ":"- ";string content=HtmlChildren(item,HtmlStyle(item,style),depth+1);items.Add(marker+content.Replace("\n","\n"+new string(' ',marker.Length)));number++;}return String.Join("\n",items);}if(tag=="table")return HtmlTable(node,style,depth+1);string body=HtmlChildren(node,style,depth+1);if(tag=="blockquote")return "> "+body.Replace("\n","\n> ");if(tag.Length==2&&tag[0]=='h'&&tag[1]>='1'&&tag[1]<='6')return new string('#',tag[1]-'0')+" "+body;return body;}
// #5: Word/Excel merged cells arrive as colspan/rowspan, so a row can name fewer cells than the
// table has columns and the delimiter row no longer matched the header, which stopped the preview
// from drawing a table at all. The table is laid out on the full grid: a horizontal span fills its
// extra columns with empty cells and a vertical span keeps its columns empty on the rows it
// continues into, so the header, the delimiter row and every body row have the same cell count.
static string HtmlTable(PasteNode node,PasteStyle style,int depth){
    var rows=new List<PasteNode>();HtmlRows(node,rows);
    var grids=new List<Dictionary<int,string>>();var carry=new Dictionary<int,int>();int columns=0;
    foreach(var row in rows){
        var placed=new Dictionary<int,string>();int column=0;
        foreach(var cell in row.Children){
            if(cell.Tag!="td"&&cell.Tag!="th")continue;
            int held;while(carry.TryGetValue(column,out held)&&held>0){placed[column]="";if(held>1)carry[column]=held-1;else carry.Remove(column);column++;}
            string text=PasteTablePipes(HtmlChildren(cell,HtmlStyle(cell,style),depth)).Replace("\n","<br>");
            int colspan=PasteSpan(cell,"colspan"),rowspan=PasteSpan(cell,"rowspan");
            for(int k=0;k<colspan;k++){placed[column+k]=k==0?text:"";if(rowspan>1)carry[column+k]=rowspan-1;}
            column+=colspan;
        }
        while(carry.Count>0){int next=-1;foreach(int key in carry.Keys)if(key>=column&&(next<0||key<next))next=key;if(next<0)break;placed[next]="";int held=carry[next];if(held>1)carry[next]=held-1;else carry.Remove(next);column=next+1;}
        grids.Add(placed);if(column>columns)columns=column;
    }
    var lines=new List<string>();
    foreach(var placed in grids){var cells=new List<string>();for(int c=0;c<columns;c++){string text;cells.Add(placed.TryGetValue(c,out text)?text:"");}lines.Add("| "+String.Join(" | ",cells)+" |");}
    if(lines.Count>0){var separators=new string[columns];for(int i=0;i<columns;i++)separators[i]="---";lines.Insert(1,"| "+String.Join(" | ",separators)+" |");}
    return String.Join("\n",lines);
}
// A table cell's own "|" is written as "\|" so the row keeps its columns, but only where the
// renderer reads it as structure. The cell splitter (MarkdownLivePreview.TablePipes) skips "\x"
// pairs and code spans, whose closing backtick run has the same length; ParseInline never hides an
// escape inside a code span, so a `<code>a | b</code>` cell must keep "`a | b`" (writing
// "`a \| b`" draws the backslash).
static string PasteTablePipes(string markdown){
    if(markdown.IndexOf('|')<0)return markdown;
    var result=new StringBuilder(markdown.Length+8);
    int budget=markdown.Length*3;
    for(int p=0;p<markdown.Length;p++){
        char c=markdown[p];
        if(c=='\\'){result.Append(c);if(p+1<markdown.Length)result.Append(markdown[++p]);continue;}
        if(c=='`'&&budget>0){
            int run=1;while(p+run<markdown.Length&&markdown[p+run]=='`')run++;
            int close=PasteCodeClose(markdown,p+run,run,ref budget);
            if(close>=0){result.Append(markdown,p,close+run-p);p=close+run-1;continue;}
            result.Append(markdown,p,run);p+=run-1;continue;
        }
        budget-=2;
        if(c=='|'){result.Append("\\|");continue;}
        result.Append(c);
    }
    return result.ToString();
}
// A span on a Word/Excel cell; a missing or bad value is one cell, and an absurd one is capped so
// pasted HTML cannot grow the table without bound.
static int PasteSpan(PasteNode node,string name){int value;return Int32.TryParse(HtmlAttribute(node.Raw,name),out value)&&value>1?Math.Min(64,value):1;}
// #15: Word writes a list as paragraphs carrying "mso-list:l0 level1 lfo1" and hides the bullet or
// number inside a span marked "mso-list:Ignore"; no ul/ol is ever written. HtmlChunks drops the
// Ignore span, the level gives the nesting, and the hidden label decides bullet vs number.
// Hangul (and a Word level without a label span) draws the marker from the head's @list rules
// instead, and writes no label at all: a level whose mso-level-number-format is anything but
// "bullet" - or whose mso-level-text holds a "%1" counter - is an ordered item, so the paste
// writes "1. " and counts it rather than turning a numbered list into a bullet list.
static string HtmlMsoList(PasteNode node,PasteStyle style,int depth){
    string raw=HtmlAttribute(node.Raw,"style");
    int level=1;Match marker=Regex.Match(raw,@"mso-list\s*:[^;]*?\blevel(\d+)",RegexOptions.IgnoreCase);
    if(marker.Success)Int32.TryParse(marker.Groups[1].Value,out level);
    if(level<1)level=1;if(level>9)level=9;
    string label=MsoListLabel(node).Trim();Match number=Regex.Match(label,@"^(\d+)[.)]");
    string bullet;
    string list=MsoListId(raw);
    if(PasteListOrdered(list,level,number.Success)){
        int shown=0;if(number.Success)Int32.TryParse(number.Groups[1].Value,out shown);
        bullet=PasteListNumber(list+":"+level,shown)+". ";
    }else bullet="- ";
    string indent=new string(' ',(level-1)*2);
    string body=HtmlChildren(node,style,depth+1).TrimStart(' ','\t',(char)0x00A0);
    return indent+bullet+body.Replace("\n","\n"+indent+new string(' ',bullet.Length));
}
// "mso-list:l1 level1 lfo2" names the list the paragraph continues; the id keys its counter.
static string MsoListId(string raw){Match id=Regex.Match(raw,@"mso-list\s*:\s*([a-z]+\d+)",RegexOptions.IgnoreCase);return id.Success?id.Groups[1].Value.ToLowerInvariant():"";}
// The list definitions are read once per paste from the whole clipboard text, not from the parsed
// fragment: WrapClipboardFragment re-opens the head's tags but not the <style> body, which is
// where @list lives. "l1:1" -> "number-format|level-text", "l1" -> mso-list-type.
[ThreadStatic] static Dictionary<string,string> pasteListLevels;
[ThreadStatic] static Dictionary<string,string> pasteListTypes;
[ThreadStatic] static Dictionary<string,int> pasteListCount;
static string PasteCssValue(string body,string name){Match value=Regex.Match(body,@"(?:^|;)\s*"+Regex.Escape(name)+@"\s*:\s*([^;}]*)",RegexOptions.IgnoreCase);return value.Success?value.Groups[1].Value.Trim():"";}
static void ReadPasteListLevels(string html){
    pasteListLevels=null;pasteListTypes=null;pasteListCount=null;
    if(String.IsNullOrEmpty(html)||html.IndexOf("@list",StringComparison.OrdinalIgnoreCase)<0)return;
    var levels=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    var types=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    foreach(Match block in Regex.Matches(html,@"@list\s+(l\d+)\s*\{([^}]*)\}",RegexOptions.IgnoreCase))types[block.Groups[1].Value]=PasteCssValue(block.Groups[2].Value,"mso-list-type");
    foreach(Match block in Regex.Matches(html,@"@list\s+(l\d+)\s*:\s*level(\d+)\s*\{([^}]*)\}",RegexOptions.IgnoreCase))levels[block.Groups[1].Value+":"+block.Groups[2].Value]=PasteCssValue(block.Groups[3].Value,"mso-level-number-format")+"|"+PasteCssValue(block.Groups[3].Value,"mso-level-text");
    pasteListLevels=levels;pasteListTypes=types;
}
// Ordered unless the level draws a bullet. With no definition at all the paragraph's own hidden
// label decides, which is how a plain Word clipboard fragment has always been read.
static bool PasteListOrdered(string list,int level,bool labelNumber){
    string definition,format="",text="";
    if(list.Length>0&&pasteListLevels!=null&&pasteListLevels.TryGetValue(list+":"+level,out definition)){
        int bar=definition.IndexOf('|');format=definition.Substring(0,bar).Trim().ToLowerInvariant();text=definition.Substring(bar+1).Trim();
    }
    if(format.Length>0)return format!="bullet"&&format!="none"&&format!="image";
    if(text.Length>0)return Regex.IsMatch(text,@"%\d");
    string type;
    if(list.Length>0&&pasteListTypes!=null&&pasteListTypes.TryGetValue(list,out type)&&Regex.IsMatch(type,@"num",RegexOptions.IgnoreCase))return true;
    return labelNumber;
}
// The number an ordered item shows: the label's own number when the paragraph carries one (Word),
// otherwise the next number of that list and level (Hangul writes no label and starts at one).
static int PasteListNumber(string key,int shown){
    if(pasteListCount==null)pasteListCount=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
    int value=shown,last;
    if(value<=0){value=1;if(pasteListCount.TryGetValue(key,out last)&&last>0)value=last+1;}
    pasteListCount[key]=value;return value;
}
static string MsoListLabel(PasteNode node){return MsoListLabel(node,0);}
static string MsoListLabel(PasteNode node,int depth){
    if(depth>64)return "";
    if(PasteMsoIgnore(node))return PastePlain(node);
    foreach(var child in node.Children){string label=MsoListLabel(child,depth+1);if(label.Length>0)return label;}
    return "";
}
static bool PasteMsoList(PasteNode node){return node.Tag=="p"&&(Regex.IsMatch(HtmlAttribute(node.Raw,"style"),@"(?:^|;)\s*mso-list\s*:",RegexOptions.IgnoreCase)||PasteMsoIgnoreInside(node));}
static bool PasteMsoIgnore(PasteNode node){return Regex.IsMatch(HtmlAttribute(node.Raw,"style"),@"mso-list\s*:\s*Ignore",RegexOptions.IgnoreCase);}
static bool PasteMsoIgnoreInside(PasteNode node){var pending=new Stack<PasteNode>();pending.Push(node);while(pending.Count>0){var current=pending.Pop();foreach(var child in current.Children){if(PasteMsoIgnore(child))return true;pending.Push(child);}}return false;}
// CF_HTML offsets count UTF-8 bytes; parent context can carry selected text styles.
// https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format
static string WrapClipboardFragment(string prefix,string fragment){var path=new System.Collections.Generic.List<PasteNode>();foreach(Match token in Regex.Matches(prefix,@"<!--[\s\S]*?-->|<(?:[^'"">]|""[^""]*""|'[^']*')*>")){string part=token.Value;if(part.StartsWith("<!--"))continue;var tag=Regex.Match(part,@"^<\s*(/?)\s*([a-zA-Z0-9]+)");if(!tag.Success)continue;string name=tag.Groups[2].Value.ToLowerInvariant();if(tag.Groups[1].Length>0){for(int i=path.Count-1;i>=0;i--)if(path[i].Tag==name){path.RemoveRange(i,path.Count-i);break;}continue;}if(part.EndsWith("/>")||name=="br"||name=="hr"||name=="img"||name=="meta"||name=="link"||name=="input"||name=="wbr"||name=="col"||name=="colgroup")continue;if(path.Count>0&&((name=="p"&&path[path.Count-1].Tag=="p")||(name=="li"&&path[path.Count-1].Tag=="li")))path.RemoveAt(path.Count-1);path.Add(new PasteNode {Tag=name,Raw=part});}var result=new StringBuilder();foreach(var node in path)result.Append(node.Raw);result.Append(fragment);for(int i=path.Count-1;i>=0;i--)result.Append("</").Append(path[i].Tag).Append('>');return result.ToString();}
static string ClipboardFragment(string html,bool preserveContext=false){int from=html.IndexOf("<!--StartFragment-->",StringComparison.OrdinalIgnoreCase),to=html.IndexOf("<!--EndFragment-->",StringComparison.OrdinalIgnoreCase);if(from>=0&&to>=from+20){string selected=html.Substring(from+20,to-from-20);return preserveContext?WrapClipboardFragment(html.Substring(0,from),selected):selected;}if(html.StartsWith("Version:",StringComparison.OrdinalIgnoreCase)){string header=html.Substring(0,Math.Min(html.Length,4096));byte[] bytes=null;foreach(string scope in new[]{"Fragment","HTML"}){Match start=Regex.Match(header,@"(?im)^Start"+scope+@":\s*(\d+)"),end=Regex.Match(header,@"(?im)^End"+scope+@":\s*(\d+)");int first,last;if(start.Success&&end.Success&&Int32.TryParse(start.Groups[1].Value,out first)&&Int32.TryParse(end.Groups[1].Value,out last)&&first>=0&&last>=first){if(bytes==null)bytes=Encoding.UTF8.GetBytes(html);if(last<=bytes.Length)try{var utf8=new UTF8Encoding(false,true);string selected=utf8.GetString(bytes,first,last-first);return preserveContext?WrapClipboardFragment(utf8.GetString(bytes,0,first),selected):selected;}catch(DecoderFallbackException){}}}}return html;}
// A conversion that produced nothing is a failure, not an empty paste: null lets the paste read
// the next format (RTF, then the plain text) instead of silently inserting nothing at all.
static string ClipboardMarkdown(string html){
    string markdown;
    try{
        ReadPasteListLevels(html);
        markdown=MarkdownClipboard.NormalizeExternal(HtmlChildren(ReadPasteHtml(ClipboardFragment(html,true)),new PasteStyle(),0));
    }catch(Exception){return null;}
    return String.IsNullOrWhiteSpace(markdown)?null:markdown;
}
}}

