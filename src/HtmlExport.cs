using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
namespace MDNote {
/* 2.2.0: File > Save As > "HTML 파일 (*.html)". The current document becomes one standalone .html file: offline, no
   script, no network. The Markdown is read by the same rules the TXT converter (MarkdownPlainText) and
   the print parser (Markdown.ParseCore/Inline) use, so the page shows what the screen shows: every
   source line is its own line, "- [x]" is a checkbox, "[^1]" is a footnote, and the app's own
   <span style="color:..;font-size:..">/<font color=..> inline styles are kept. The conversion builds
   strings only (no WPF object), so it runs on a worker thread; HtmlExporter.Convert is the one entry the
   Save As and the probe share. */
public partial class MainWindow {
    bool htmlExporting;
    internal static bool IsHtmlFileName(string file){
        string extension;
        try{extension=Path.GetExtension(file);}catch(ArgumentException){return false;}
        return String.Equals(extension,".html",StringComparison.OrdinalIgnoreCase)||String.Equals(extension,".htm",StringComparison.OrdinalIgnoreCase);
    }
    /* The full path the HTML file is written to. The HTML type was chosen, so a name without .html/.htm
       gets ".html". The document's own file and a file another tab or window holds are refused with the
       Save As "already open" notice: the export would replace them with a web page. */
    string HtmlExportTarget(string target){
        string full=Path.GetFullPath(target);
        if(!IsHtmlFileName(full))full+=".html";
        bool ownPath=false;
        if(!String.IsNullOrEmpty(path)){try{ownPath=String.Equals(Path.GetFullPath(path),full,StringComparison.OrdinalIgnoreCase);}catch(Exception){}}
        if(ownPath||SaveTargetHeldElsewhere(full))throw new SaveTargetInUseException();
        return full;
    }
    /* Save As, HTML file type (SaveAsync): converts the open document on a worker thread behind the wait
       cursor and writes target. Nothing is shown after a success; a failure shows the one error box the
       save paths use and sets LastSaveFailure/LastSaveReported. The document itself is not touched. */
    async System.Threading.Tasks.Task ExportHtmlToAsync(string target){
        if(htmlExporting)return;
        string markdown=text;bool plain=PlainText;string documentPath=DocumentBasePath();
        string lang=language,label=T("[이미지: ");
        htmlExporting=true;
        var previousCursor=System.Windows.Input.Mouse.OverrideCursor;
        System.Windows.Input.Mouse.OverrideCursor=System.Windows.Input.Cursors.Wait;
        try{
            await System.Threading.Tasks.Task.Run(delegate{
                string html=HtmlExporter.Convert(markdown,documentPath,target,lang,label,plain);
                HtmlExporter.WriteFile(target,html);
            });
        }catch(Exception ex){
            LastSaveReported=true;LastSaveFailure=T("HTML 파일을 저장하지 못했습니다.\n")+ex.Message;
            if(!Program.IsSelfTest){EnsureVisibleForeground();MessageBox.Show(this,LastSaveFailure,"MDNote");}
        }finally{
            System.Windows.Input.Mouse.OverrideCursor=previousCursor;
            htmlExporting=false;
        }
    }
}
internal static class HtmlExporter {
    const string DefaultImageLabel="[이미지: ";
    const int MaxQuoteDepth=24;   // a deeper quote nest is cut into plain text so no input can exhaust the stack
    const int MaxInlineDepth=12;  // Markdown.Inline stops recursing past depth 12
    const string Css=
        "*{box-sizing:border-box}\n"+
        "body{margin:0;background:#fff;color:#222;font:16px/1.75 -apple-system,BlinkMacSystemFont,\"Segoe UI\",\"Malgun Gothic\",\"Apple SD Gothic Neo\",\"Noto Sans KR\",\"Noto Sans CJK KR\",\"Hiragino Sans\",\"Microsoft YaHei\",sans-serif;-webkit-text-size-adjust:100%}\n"+
        "article{max-width:820px;margin:0 auto;padding:32px 20px 64px;overflow-wrap:anywhere}\n"+
        "h1,h2,h3,h4,h5,h6{line-height:1.35;margin:1.5em 0 .6em}\n"+
        "h1{font-size:2em}h2{font-size:1.6em}h3{font-size:1.3em}h4{font-size:1.15em}h5{font-size:1.1em}h6{font-size:1.06em}\n"+
        "p{margin:0 0 .9em}\n"+
        "a{color:#2563d2}\n"+
        "pre{margin:.6em 0 1em;padding:14px;background:#f3f4f6;border:1px solid #d9dde3;border-radius:6px;overflow-x:auto;line-height:1.5}\n"+
        "pre.plain{white-space:pre-wrap;overflow-wrap:anywhere}\n"+
        "code{font-family:Consolas,\"Cascadia Mono\",\"Malgun Gothic\",monospace;font-size:.92em}\n"+
        "p code,li code,td code,th code{background:#eef0f3;padding:.1em .35em;border-radius:4px}\n"+
        "blockquote{margin:.8em 0 1em;padding:.2em 1em;border-left:4px solid #b8c0cc;color:#5f6774}\n"+
        "ul,ol{margin:.3em 0 .9em;padding-left:1.6em}\n"+
        "ul.task{list-style:none;padding-left:.2em}ul.task ul,ul.task ol{padding-left:1.6em}\n"+
        "li.task input{margin:0 .5em 0 0}\n"+
        ".table-wrap{overflow-x:auto;margin:.6em 0 1em}\n"+
        "table{border-collapse:collapse}\n"+
        "th,td{border:1px solid #c5ccd6;padding:6px 10px;text-align:left;vertical-align:top}\n"+
        "th{background:#eef1f5}\n"+
        "img{max-width:100%;height:auto}\n"+
        "hr{border:0;border-top:1px solid #d9dde3;margin:1.6em 0}\n"+
        ".footnote{font-size:.9em;color:#5f6774}\n"+
        "sup a{text-decoration:none}\n";
    static readonly Regex ColorPattern=new Regex(@"(?:color\s*:\s*|\bcolor\s*=\s*[""']?)(#[a-fA-F0-9]{3,8}\b|[a-zA-Z]+)");
    static readonly Regex SizePattern=new Regex(@"font-size\s*:\s*(\d+(?:\.\d+)?)\s*(px|pt|em|%)?",RegexOptions.IgnoreCase);

    /* markdown -> one complete HTML page (CRLF newlines). documentPath is the .md the text belongs to
       ("" for an untitled document) and htmlPath the file the page will be saved as; both decide how an
       image path is written (see ResolveImage). plainText wraps a TXT document in one <pre>. */
    internal static string Convert(string markdown,string documentPath,string htmlPath,string language="ko",string imageLabel=null,bool plainText=false){
        if(markdown==null)markdown="";
        var context=new Context(documentPath,htmlPath,imageLabel??DefaultImageLabel);
        string normalized=markdown.Replace("\r\n","\n").Replace('\r','\n');
        var body=new StringBuilder(normalized.Length+normalized.Length/2+512);
        if(plainText){
            body.Append("<pre class=\"plain\">");AppendEscaped(body,normalized,0,normalized.Length,false);body.Append("</pre>\n");
        }else{
            string[] lines=normalized.Split('\n');
            ScanReferences(lines,context);
            new BlockWriter(context,body).Render(lines,0);
        }
        string title=context.Title;
        if(String.IsNullOrEmpty(title)){
            title=null;
            try{if(!String.IsNullOrEmpty(documentPath))title=Path.GetFileNameWithoutExtension(documentPath);}catch(ArgumentException){}
            if(String.IsNullOrEmpty(title))title="MDNote";
        }
        string lang=String.IsNullOrEmpty(language)?"ko":language;
        var page=new StringBuilder(body.Length+Css.Length+512);
        page.Append("<!DOCTYPE html>\n<html lang=\"");AppendEscaped(page,lang,0,lang.Length,true);
        page.Append("\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>");
        AppendEscaped(page,title,0,title.Length,false);
        page.Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<article>\n");
        page.Append(body);
        page.Append("</article>\n</body>\n</html>\n");
        return page.ToString().Replace("\n","\r\n");
    }
    // UTF-8 without a BOM: the <meta charset> says UTF-8 and a BOM would only show in some mail clients.
    internal static byte[] Encode(string html){return new UTF8Encoding(false).GetBytes(html);}
    internal static void WriteFile(string path,string html){File.WriteAllBytes(path,Encode(html));}

    // Everything a conversion shares: where the images live, the reference definitions, the title.
    sealed class Context {
        internal readonly string DocDir,HtmlDir,ImageLabel;
        internal readonly Dictionary<string,string> Links=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,int> Notes=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> NoteUsed=new HashSet<string>();
        internal readonly HashSet<string> HeadingIds=new HashSet<string>(StringComparer.Ordinal);
        internal string Title;
        internal Context(string documentPath,string htmlPath,string imageLabel){
            ImageLabel=imageLabel;
            DocDir=FolderOf(documentPath);HtmlDir=FolderOf(htmlPath);
        }
        internal int Number(string id){int number;return Notes.TryGetValue(id,out number)?number:1;}
        /* The id for a heading's <hN>, or null when its text slugs to nothing. The text and the slug are
           the ones the app's own "#heading" jump compares (SourceOutline.OutlineLabel = MarkdownPlainText
           of "("+raw, then AnchorRule.Slug), so a link that jumps in MDNote jumps in the page. The app
           takes the first of equal headings; here the first keeps the plain slug and the later ones get
           -1, -2, ... (and never an id already taken, so "제목 1" cannot collide with the second "제목"). */
        internal string HeadingId(string body){
            string plain=MarkdownPlainText.Convert("("+body);
            if(plain.Length>0&&plain[0]=='(')plain=plain.Substring(1);
            string slug=AnchorRule.Slug(plain);
            if(slug.Length==0)return null;
            string id=slug;
            for(int n=1;!HeadingIds.Add(id);n++)id=slug+"-"+n.ToString(CultureInfo.InvariantCulture);
            return id;
        }
    }
    static string FolderOf(string path){
        if(String.IsNullOrEmpty(path))return null;
        try{
            string full=Path.GetFullPath(path);
            string folder=Path.GetDirectoryName(full);
            return String.IsNullOrEmpty(folder)?null:folder;
        }catch(ArgumentException){return null;}catch(NotSupportedException){return null;}catch(PathTooLongException){return null;}
    }

    // ---- escaping ----
    static void AppendEscaped(StringBuilder sb,string value,int start,int length,bool attribute){
        int run=start,end=start+length;
        for(int i=start;i<end;i++){
            string replacement=null;
            switch(value[i]){
                case '&':replacement="&amp;";break;
                case '<':replacement="&lt;";break;
                case '>':replacement="&gt;";break;
                case '"':if(attribute)replacement="&quot;";break;
                case '\'':if(attribute)replacement="&#39;";break;
                case '\0':replacement="�";break;
            }
            if(replacement==null)continue;
            if(i>run)sb.Append(value,run,i-run);
            sb.Append(replacement);run=i+1;
        }
        if(end>run)sb.Append(value,run,end-run);
    }
    static void AppendAttribute(StringBuilder sb,string name,string value){
        sb.Append(' ').Append(name).Append("=\"");AppendEscaped(sb,value,0,value.Length,true);sb.Append('"');
    }
    // Text as the screen shows it: character references decoded, backslash escapes restored, then
    // escaped for HTML. A newline (a multi-line footnote) becomes <br>.
    static void AppendText(StringBuilder sb,string raw,bool code){
        string value=code?RestoreCodeEscapes(raw):RestoreEscapes(MarkdownPlainText.DecodeEntities(raw));
        int from=0;
        while(true){
            int newline=value.IndexOf('\n',from);
            if(newline<0){AppendEscaped(sb,value,from,value.Length-from,false);break;}
            AppendEscaped(sb,value,from,newline-from,false);sb.Append("<br>\n");from=newline+1;
        }
    }

    // ---- backslash escapes (copied from Markdown.ProtectEscapes/RestoreEscapes: that class owns
    // WPF statics, this file stays free of them) ----
    const string EscapablePunctuation="!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    internal static string ProtectEscapes(string value){
        if(value.IndexOf('\\')<0)return value;
        var result=new StringBuilder(value.Length);
        for(int i=0;i<value.Length;i++){char c=value[i];if(c=='\\'&&i+1<value.Length){int index=EscapablePunctuation.IndexOf(value[i+1]);if(index>=0){result.Append((char)(0xE000+index));i++;continue;}}result.Append(c);}
        return result.ToString();
    }
    static string RestoreEscapes(string value){return RestoreEscapeMarks(value,false);}
    static string RestoreCodeEscapes(string value){return RestoreEscapeMarks(value,true);}
    static string RestoreEscapeMarks(string value,bool code){
        bool marked=false;foreach(char c in value)if(c>=0xE000&&c<=0xE01F){marked=true;break;}
        if(!marked)return value;
        var result=new StringBuilder(value.Length+8);
        for(int i=0;i<value.Length;i++){char c=value[i];if(c>=0xE000&&c<=0xE01F){if(code)result.Append('\\');result.Append(EscapablePunctuation[c-0xE000]);}else result.Append(c);}
        return result.ToString();
    }
    // CommonMark code-span padding (Markdown.UnpadCode).
    static string UnpadCode(string value){return value.Length>=2&&value.StartsWith(" ")&&value.EndsWith(" ")&&value.Trim(' ').Length>0?value.Substring(1,value.Length-2):value;}

    // ---- block scanners (copies of the private MarkdownPlainText helpers; same rules, see there) ----
    static bool Space(char c){return c==' '||c=='\t';}
    static bool WhiteOnly(string value,int start){for(int i=start;i<value.Length;i++)if(!Space(value[i]))return false;return true;}
    static bool Word(char c){
        switch(Char.GetUnicodeCategory(c)){
            case UnicodeCategory.UppercaseLetter:case UnicodeCategory.LowercaseLetter:case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.ModifierLetter:case UnicodeCategory.OtherLetter:case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.DecimalDigitNumber:case UnicodeCategory.ConnectorPunctuation:return true;
        }
        return c==(char)0x200C||c==(char)0x200D;
    }
    static bool Same(string value,int start,string text){
        if(start<0||start+text.Length>value.Length)return false;
        for(int i=0;i<text.Length;i++){
            char a=value[start+i],b=text[i];
            if(a==b)continue;
            if(Char.ToLowerInvariant(a)!=Char.ToLowerInvariant(b))return false;
        }
        return true;
    }
    static bool Fence(string line,out char marker,out int count,out int info){
        marker='\0';count=0;info=line.Length;
        int at=0;while(at<3&&at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length)return false;
        char c=line[at];
        if(c!='`'&&c!='~')return false;
        int end=at;while(end<line.Length&&line[end]==c)end++;
        if(end-at<3)return false;
        marker=c;count=end-at;info=end;
        return true;
    }
    static bool FenceOpen(string line,out char marker,out int count,out int info){
        if(!Fence(line,out marker,out count,out info))return false;
        return !(marker=='`'&&line.IndexOf('`',info)>=0);   // a backtick in the info string disables the fence
    }
    static bool ClosingFence(string line,char marker,int count){
        int at=0;while(at<3&&at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length||line[at]!=marker)return false;
        int end=at;while(end<line.Length&&line[end]==marker)end++;
        if(end-at<count)return false;
        while(end<line.Length&&Space(line[end]))end++;
        return end==line.Length;
    }
    static bool TryDefinition(string line,out string id,out string body,out bool note){
        id=null;body=null;note=false;
        int at=0;while(at<3&&at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length||line[at]!='[')return false;
        int close=line.IndexOf(']',at+1);
        if(close<0)return false;
        string raw=line.Substring(at+1,close-at-1);
        if(raw.Length==0)return false;
        if(close+1>=line.Length||line[close+1]!=':')return false;
        int start=close+2;while(start<line.Length&&Space(line[start]))start++;
        string rest=line.Substring(start);
        if(raw[0]=='^'){note=true;id=raw.Substring(1);body=rest;return id.Length>0;}
        note=false;id=raw;
        if(rest.Length==0)return false;
        if(rest[0]=='<'){
            int gt=rest.IndexOf('>',1);
            if(gt>=2&&(gt+1>=rest.Length||Space(rest[gt+1]))){body=rest.Substring(1,gt-1);return true;}
        }
        int end=0;while(end<rest.Length&&!Space(rest[end]))end++;
        if(end==0)return false;
        body=rest.Substring(0,end);
        return true;
    }
    static bool SetextUnderline(string line,out int level){
        level=0;
        int at=0;while(at<3&&at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length)return false;
        char c=line[at];if(c!='='&&c!='-')return false;
        int end=at;while(end<line.Length&&line[end]==c)end++;
        if(c=='-'&&end-at<2)return false;
        while(end<line.Length&&Space(line[end]))end++;
        if(end!=line.Length)return false;
        level=c=='='?1:2;
        return true;
    }
    static bool ListStart(string line){
        int at=0;while(at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length)return false;
        char c=line[at];
        if(c=='-'||c=='+'||c=='*')return at+1<line.Length&&Space(line[at+1]);
        if(Char.IsDigit(c)){
            while(at<line.Length&&Char.IsDigit(line[at]))at++;
            if(at>=line.Length||(line[at]!='.'&&line[at]!=')'))return false;
            return at+1<line.Length&&Space(line[at+1]);
        }
        return false;
    }
    static bool HorizontalRule(string line){
        int at=0,indent=0;
        while(at<line.Length&&Space(line[at])){
            indent+=line[at]=='\t'?4:1;
            if(indent>3)return false;
            at++;
        }
        if(at>=line.Length)return false;
        char c=line[at];if(c!='-'&&c!='*'&&c!='_')return false;
        int count=1;int p=at+1;
        while(true){
            int skip=p;while(skip<line.Length&&Space(line[skip]))skip++;
            if(skip<line.Length&&line[skip]==c){count++;p=skip+1;continue;}
            break;
        }
        while(p<line.Length&&Space(line[p]))p++;
        return count>=3&&p==line.Length;
    }
    static bool QuoteLine(string line,out string body){
        body="";
        int at=0;while(at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length||line[at]!='>')return false;
        at++;
        if(at<line.Length&&Space(line[at]))at++;
        body=line.Substring(at);
        return true;
    }
    // CommonMark lazy continuation of a block quote, read on plain lines: a line with no ">" of its
    // own, directly after a quote line that is paragraph text, continues that quote's paragraph.
    // This is the string twin of MarkdownLivePreview.LazyQuoteLine / StartsBlock / QuoteBodyParagraph
    // (the live editor draws such a line inside the quote). The print renderer shares it.
    // Feed Line() each quote line's body in order, ask Continues() before taking a line without ">".
    internal sealed class LazyQuote {
        bool paragraph,fence;char fenceMarker;int fenceCount;
        // One quote line's body (the text after its first ">"): the quote is in a paragraph afterwards
        // unless that body is blank, a heading, a rule, a list item or a fence line (or inside a fence).
        internal void Line(string body){
            while(true){string inner;if(!QuoteLine(body,out inner))break;body=inner;}   // nested "> > text"
            int info;
            if(fence){paragraph=false;if(ClosingFence(body,fenceMarker,fenceCount))fence=false;return;}
            if(FenceOpen(body,out fenceMarker,out fenceCount,out info)){fence=true;paragraph=false;return;}
            paragraph=BodyParagraph(body);
        }
        static bool BodyParagraph(string body){
            if(WhiteOnly(body,0))return false;
            int level;string rest;
            if(MarkdownPlainText.AtxHeading(body,out level,out rest))return false;
            if(HorizontalRule(body))return false;
            if(ListStart(body))return false;
            return true;
        }
        // lines[index] has no ">" marker: true when it continues the quote's paragraph (the quote is in
        // a paragraph and the line is not blank and starts no block of its own).
        internal bool Continues(string[] lines,int index){
            return paragraph&&!StartsBlock(lines,index);
        }
        static bool StartsBlock(string[] lines,int index){
            string line=lines[index];
            if(WhiteOnly(line,0))return true;   // a blank line closes the quote
            char marker;int count,info,level;string rest;
            if(FenceOpen(line,out marker,out count,out info))return true;
            if(MarkdownPlainText.AtxHeading(line,out level,out rest))return true;
            if(HorizontalRule(line))return true;
            if(SetextUnderline(line,out level))return true;
            if(ListStart(line))return true;
            if(index+1<lines.Length&&MarkdownPlainText.TableStart(line,lines[index+1]))return true;
            string id,defBody;bool note;
            if(TryDefinition(line,out id,out defBody,out note))return true;
            return false;
        }
    }
    static bool ListLine(string line,out string indent,out int content,out bool ordered,out string number,out bool task,out bool checkedBox,out string body){
        indent="";content=0;ordered=false;number="";task=false;checkedBox=false;body="";
        int at=0,column=0;
        while(at<line.Length&&Space(line[at])){column+=line[at]=='\t'?4:1;at++;}
        indent=line.Substring(0,at);
        if(at>=line.Length)return false;
        char c=line[at];
        if(c=='-'||c=='+'||c=='*'){at++;column++;}
        else if(Char.IsDigit(c)){
            int digits=at;while(at<line.Length&&Char.IsDigit(line[at])){at++;column++;}
            number=line.Substring(digits,at-digits);ordered=true;
            if(at>=line.Length||(line[at]!='.'&&line[at]!=')'))return false;
            at++;column++;
        }else return false;
        if(at>=line.Length||!Space(line[at]))return false;
        while(at<line.Length&&Space(line[at])){column+=line[at]=='\t'?4:1;at++;}
        content=column;
        body=line.Substring(at);
        if(body.Length>=3&&body[0]=='['&&body[2]==']'&&(body[1]==' '||body[1]=='x'||body[1]=='X')){
            task=true;checkedBox=body[1]!=' ';
            int rest=3;while(rest<body.Length&&Space(body[rest]))rest++;
            body=body.Substring(rest);
        }
        return true;
    }
    static int StartIndex(string number){
        int first;
        if(Int32.TryParse(number,out first)&&first>0&&first<1000000)return first;
        return 1;
    }
    static string[] FitCells(string[] cells,int count){
        if(cells.Length==count)return cells;
        var fit=new string[count];
        for(int i=0;i<count;i++)fit[i]=i<cells.Length?cells[i]:"";
        return fit;
    }

    // ---- reference definitions and footnotes (MarkdownPlainText.ScanReferences / ExpandReferences) ----
    static void ScanReferences(string[] lines,Context context){
        char marker='\0';int length=0;
        for(int i=0;i<lines.Length;i++){
            string line=lines[i];
            char fenceMarker;int fenceCount;int fenceInfo;
            if(marker!='\0'){
                if(Fence(line,out fenceMarker,out fenceCount,out fenceInfo)&&fenceMarker==marker&&fenceCount>=length&&WhiteOnly(line,fenceInfo))marker='\0';
                continue;
            }
            if(FenceOpen(line,out fenceMarker,out fenceCount,out fenceInfo)){marker=fenceMarker;length=fenceCount;continue;}
            string id,body;bool note;
            if(TryDefinition(line,out id,out body,out note)){
                if(note){if(!context.Notes.ContainsKey(id))context.Notes[id]=context.Notes.Count+1;}
                else context.Links[id]=body;
            }
        }
    }
    static string ExpandReferences(string value,Context context){
        if(value.Length==0)return value;
        var result=new StringBuilder(value.Length+16);
        int at=0;int p=0;
        while(p<value.Length){
            int end;int kind;bool bang;string label;string id;
            if(!ReferenceMatch(value,p,out end,out kind,out bang,out label,out id)){p++;continue;}
            if(p>at)result.Append(value,at,p-at);
            if(kind==1){
                result.Append(value,p,end-p);
            }else if(id.Length>0&&id[0]=='^'&&context.Notes.ContainsKey(id.Substring(1))){
                result.Append('[').Append(context.Number(id.Substring(1))).Append("](#mdnote-footnote-").Append(Uri.EscapeDataString(id.Substring(1))).Append(')');
            }else{
                string address;
                if(context.Links.TryGetValue(id,out address)){
                    if(bang)result.Append('!');
                    result.Append('[').Append(label).Append("](").Append(address).Append(')');
                }else result.Append(value,p,end-p);
            }
            at=end;p=end;
        }
        if(at<value.Length)result.Append(value,at,value.Length-at);
        return result.ToString();
    }
    static bool ReferenceMatch(string value,int p,out int end,out int kind,out bool bang,out string label,out string id){
        end=0;kind=0;bang=false;label=null;id=null;
        char c=value[p];
        if(c=='`'){
            int run=p;while(run<value.Length&&value[run]=='`')run++;
            int q=run;while(q<value.Length&&value[q]!='`')q++;
            if(q>=value.Length)return false;
            int close=q;while(close<value.Length&&value[close]=='`')close++;
            kind=1;end=close;
            return true;
        }
        int bracket=p;
        bool hasBang=false;
        if(c=='!'){
            if(p+1>=value.Length||value[p+1]!='[')return false;
            bracket=p+1;hasBang=true;
        }else if(c!='[')return false;
        int labelEnd=value.IndexOf(']',bracket+1);
        if(labelEnd>=bracket+2&&labelEnd+1<value.Length&&value[labelEnd+1]=='['){
            int idEnd=value.IndexOf(']',labelEnd+2);
            if(idEnd>=labelEnd+2){
                kind=2;bang=hasBang;end=idEnd+1;
                label=value.Substring(bracket+1,labelEnd-bracket-1);
                id=value.Substring(labelEnd+2,idEnd-labelEnd-2);
                if(id.Length==0)id=label;
                return true;
            }
        }
        if(hasBang)return false;
        if(labelEnd>=p+2&&(labelEnd+1>=value.Length||value[labelEnd+1]!='(')){
            kind=3;end=labelEnd+1;
            label=value.Substring(p+1,labelEnd-p-1);
            id=label;
            return true;
        }
        return false;
    }

    // ---- inline token scan (MarkdownPlainText.Token and its helpers; TargetEnd, CommentEnd and
    // AutolinkEnd are the shared internal scans and are called as they are) ----
    // kinds: 1 span, 2 font, 3 code, 4 image, 5 link, 6/7 bold, 8 strike, 9/10 italic, 11 br,
    // 12 angle autolink, 13 comment
    static bool Token(string value,int p,out int kind,out int end,out int contentStart,out int contentEnd){
        kind=0;end=0;contentStart=0;contentEnd=0;
        char c=value[p];
        if(c=='<'){
            int openEnd;int closeStart;int closeEnd;
            if(TagOpen(value,p,"span",out openEnd,out closeStart,out closeEnd)){kind=1;contentStart=openEnd;contentEnd=closeStart;end=closeEnd;return true;}
            if(TagOpen(value,p,"font",out openEnd,out closeStart,out closeEnd)){kind=2;contentStart=openEnd;contentEnd=closeStart;end=closeEnd;return true;}
            if(BrTag(value,p,out end)){kind=11;return true;}
            int comment=MarkdownPlainText.CommentEnd(value,p);
            if(comment>p){kind=13;end=comment;return true;}
            int autolink=MarkdownPlainText.AutolinkEnd(value,p);
            if(autolink>p){kind=12;contentStart=p+1;contentEnd=autolink-1;end=autolink;return true;}
            return false;
        }
        if(c=='`'){
            if(p>0&&value[p-1]=='`')return false;
            int run=p;while(run<value.Length&&value[run]=='`')run++;
            int ticks=run-p;
            for(int q=run;q<value.Length&&value[q]!='\n';q++){
                if(value[q]!='`')continue;
                int close=q;while(close<value.Length&&value[close]=='`')close++;
                if(close-q==ticks){kind=3;contentStart=run;contentEnd=q;end=close;return true;}
                q=close-1;
            }
            return false;
        }
        if(c=='!'){
            if(p+1>=value.Length||value[p+1]!='[')return false;
            int close=value.IndexOf(']',p+2);
            if(close<0||close+1>=value.Length||value[close+1]!='(')return false;
            int url=MarkdownPlainText.TargetEnd(value,close+2);
            if(url<=close+2)return false;
            kind=4;contentStart=p+2;contentEnd=close;end=url+1;
            return true;
        }
        if(c=='['){
            int close=value.IndexOf(']',p+1);
            if(close<=p+1||close+1>=value.Length||value[close+1]!='(')return false;
            int url=MarkdownPlainText.TargetEnd(value,close+2);
            if(url<=close+2)return false;
            kind=5;contentStart=p+1;contentEnd=close;end=url+1;
            return true;
        }
        if(c=='~'){
            if(p+1<value.Length&&value[p+1]=='~'&&BoldClose(value,p,'~',out end,out contentEnd)){kind=8;contentStart=p+2;return true;}
            return false;
        }
        if(c=='*'||c=='_'){
            if(p+1<value.Length&&value[p+1]==c&&BoldClose(value,p,c,out end,out contentEnd)){kind=c=='*'?6:7;contentStart=p+2;return true;}
            if(c=='*'){
                int close=p+1;
                while(close<value.Length&&value[close]!='*'&&value[close]!='\n')close++;
                if(close<=p+1||close>=value.Length||value[close]!='*')return false;
                kind=9;contentStart=p+1;contentEnd=close;end=close+1;
                return true;
            }
            if(p>0&&Word(value[p-1]))return false;
            int stop=p+1;
            while(stop<value.Length&&value[stop]!='_'&&value[stop]!='\n')stop++;
            if(stop<=p+1||stop>=value.Length||value[stop]!='_')return false;
            if(stop+1<value.Length&&Word(value[stop+1]))return false;
            kind=10;contentStart=p+1;contentEnd=stop;end=stop+1;
            return true;
        }
        return false;
    }
    static bool BoldClose(string value,int p,char c,out int end,out int contentEnd){
        end=0;contentEnd=0;
        if(p+2>=value.Length||value[p+2]=='\n')return false;
        for(int scan=p+3;scan+1<value.Length;scan++){
            if(value[scan]=='\n')return false;
            if(value[scan]==c&&value[scan+1]==c){contentEnd=scan;end=scan+2;return true;}
        }
        return false;
    }
    static bool TagOpen(string value,int p,string tag,out int openEnd,out int closeStart,out int closeEnd){
        openEnd=0;closeStart=0;closeEnd=0;
        if(!Same(value,p+1,tag))return false;
        int at=p+1+tag.Length;
        if(at>=value.Length||!Space(value[at]))return false;
        int gt=value.IndexOf('>',at);
        if(gt<0)return false;
        openEnd=gt+1;
        string closing="</"+tag+">";
        int close=-1;
        for(int i=openEnd;i<=value.Length-closing.Length;i++)if(value[i]=='<'&&Same(value,i,closing)){close=i;break;}
        if(close<0)return false;
        closeStart=close;closeEnd=close+closing.Length;
        return true;
    }
    static bool BalancedTag(string value,int from,string tag,out int closeStart,out int closeEnd){
        closeStart=0;closeEnd=0;
        int nesting=1;int at=from;
        while(true){
            int hit;int hitEnd;bool closing;
            if(!NextTag(value,at,tag,out hit,out hitEnd,out closing))return false;
            if(closing)nesting--;
            else nesting++;
            if(nesting==0){closeStart=hit;closeEnd=hitEnd;return true;}
            at=hitEnd;
        }
    }
    static bool NextTag(string value,int from,string tag,out int start,out int end,out bool closing){
        start=0;end=0;closing=false;
        int at=from;
        while(true){
            int lt=value.IndexOf('<',at);
            if(lt<0)return false;
            int q=lt+1;
            bool isClose=false;
            if(q<value.Length&&value[q]=='/'){isClose=true;q++;}
            if(Same(value,q,tag)){
                int after=q+tag.Length;
                if(after>=value.Length||!Word(value[after])){
                    int gt=value.IndexOf('>',after);
                    if(gt>=0){start=lt;end=gt+1;closing=isClose;return true;}
                }
            }
            at=lt+1;
        }
    }
    static bool BrTag(string value,int p,out int end){
        end=0;
        if(!Same(value,p+1,"br"))return false;
        int at=p+3;
        while(at<value.Length&&Space(value[at]))at++;
        if(at<value.Length&&value[at]=='/')at++;
        if(at>=value.Length||value[at]!='>')return false;
        end=at+1;
        return true;
    }
    // "(dest)" or "(dest \"title\")" (Markdown.TargetParts): the title is not part of the address.
    static void TargetParts(string value,int start,int stop,out string address,out string title){
        address="";title="";
        int at=start;
        while(at<stop&&Char.IsWhiteSpace(value[at]))at++;
        bool angle=false;
        if(at<stop&&value[at]=='<'){
            int gt=value.IndexOf('>',at+1);
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

    // ---- addresses ----
    // Percent-encoding for a path inside a src: only letters, digits, - . _ ~ and the separator stay
    // as they are; everything else (space, non-ASCII, # % ? and so on) is written as UTF-8 %XX.
    internal static string EncodePath(string path){
        var sb=new StringBuilder(path.Length+8);
        byte[] buffer=new byte[4];
        for(int i=0;i<path.Length;i++){
            char c=path[i];
            if(c=='\\')c='/';
            if(c>='a'&&c<='z'||c>='A'&&c<='Z'||c>='0'&&c<='9'||c=='-'||c=='.'||c=='_'||c=='~'||c=='/'){sb.Append(c);continue;}
            int units=Char.IsHighSurrogate(c)&&i+1<path.Length&&Char.IsLowSurrogate(path[i+1])?2:1;
            int count=Encoding.UTF8.GetBytes(path.ToCharArray(i,units),0,units,buffer,0);
            for(int b=0;b<count;b++)sb.Append('%').Append(buffer[b].ToString("X2",CultureInfo.InvariantCulture));
            i+=units-1;
        }
        return sb.ToString();
    }
    // The absolute file:// form for a file the page's folder cannot reach with a relative path
    // (another drive). Same escaping rule as Markdown.LocalImageUri, which owns the WPF statics.
    static string FileUri(string fullPath){
        try{
            if(fullPath.IndexOfAny(new[]{'#','%','?'})<0||fullPath.StartsWith(@"\\"))return new Uri(fullPath,UriKind.Absolute).AbsoluteUri;
            var builder=new UriBuilder();builder.Scheme=Uri.UriSchemeFile;builder.Host="";builder.Path=fullPath;
            return builder.Uri.AbsoluteUri;
        }catch(UriFormatException){return null;}
    }
    static bool SameFolder(string a,string b){
        if(a==null||b==null)return false;
        return String.Equals(a.TrimEnd('\\','/'),b.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);
    }
    // A path relative to the page's folder ("../pics/a%20b.png"), or the file:// URI when the two are
    // on different drives/shares.
    static string RelativeFrom(string htmlDir,string full){
        string rootA=Path.GetPathRoot(htmlDir)??"",rootB=Path.GetPathRoot(full)??"";
        if(!String.Equals(rootA.TrimEnd('\\','/'),rootB.TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase))return FileUri(full);
        char[] separators=new[]{'\\','/'};
        string[] from=htmlDir.Substring(rootA.Length).Split(separators,StringSplitOptions.RemoveEmptyEntries);
        string[] to=full.Substring(rootB.Length).Split(separators,StringSplitOptions.RemoveEmptyEntries);
        int common=0;
        while(common<from.Length&&common<to.Length-1&&String.Equals(from[common],to[common],StringComparison.OrdinalIgnoreCase))common++;
        var result=new StringBuilder();
        for(int i=common;i<from.Length;i++)result.Append("../");
        for(int i=common;i<to.Length;i++){if(i>common)result.Append('/');result.Append(EncodePath(to[i]));}
        return result.ToString();
    }
    static bool DriveOrShare(string location){
        if(location.Length>=2&&(location.StartsWith(@"\\")||location.StartsWith("//")))return true;
        return location.Length>=3&&Char.IsLetter(location[0])&&location[1]==':'&&(location[2]=='\\'||location[2]=='/');
    }
    /* The src for an image address (already without its title). The app reads the address the way
       Markdown.ImageUri does: percent-decoded, an http(s) address as it is, a drive/UNC/file: path as
       that file, anything else relative to the document's folder. In the page: http(s) and data:image
       stay unchanged; a relative path stays unchanged when the page is saved in the document's own
       folder (or the folder is unknown); every other local file becomes a path relative to the page's
       folder, or a file:// URI on another drive. Null: the address cannot be shown (the caller writes
       the "[이미지: alt]" text the app shows for a picture it cannot load). */
    static string ResolveImage(Context context,string address){
        string location;
        try{location=Uri.UnescapeDataString(address);}catch(UriFormatException){location=address;}
        location=location.Trim();
        if(location.Length==0)return null;
        Uri uri;string full;
        if(Uri.TryCreate(location,UriKind.Absolute,out uri)){
            string scheme=uri.Scheme;
            if(scheme==Uri.UriSchemeHttp||scheme==Uri.UriSchemeHttps)return address;
            if(scheme=="data")return location.StartsWith("data:image/",StringComparison.OrdinalIgnoreCase)?address:null;
            if(!uri.IsFile)return null;
            try{full=Path.GetFullPath(DriveOrShare(location)?location:uri.LocalPath);}catch(Exception){return null;}
        }else{
            if(context.DocDir==null||context.HtmlDir==null||SameFolder(context.DocDir,context.HtmlDir))return EncodePath(location);
            try{full=Path.GetFullPath(Path.Combine(context.DocDir,location));}catch(Exception){return EncodePath(location);}
        }
        if(context.HtmlDir==null)return FileUri(full);
        return RelativeFrom(context.HtmlDir,full);
    }
    // The href for a link address, or null when its scheme could run script or is unknown (the label
    // is then written as plain text). Relative paths and #fragments are kept as written.
    static string LinkTarget(string address){
        string url=address.Trim();
        if(url.Length==0)return null;
        var probe=new StringBuilder();
        for(int i=0;i<url.Length&&probe.Length<40;i++)if(url[i]>' ')probe.Append(url[i]);
        string sniff=probe.ToString();
        int colon=-1;
        for(int i=0;i<sniff.Length;i++){
            char c=sniff[i];
            if(c==':'){colon=i;break;}
            if(c=='/'||c=='?'||c=='#')break;
        }
        if(colon<=0)return url;
        for(int i=0;i<colon;i++){
            char c=sniff[i];
            bool letter=c>='a'&&c<='z'||c>='A'&&c<='Z';
            if(i==0?!letter:!(letter||c>='0'&&c<='9'||c=='+'||c=='.'||c=='-'))return url;   // not a scheme: a relative path
        }
        string scheme=sniff.Substring(0,colon).ToLowerInvariant();
        if(scheme.Length==1)return DriveOrShare(url)?FileUri(url):null;   // C:\folder\file.pdf
        if(scheme=="http"||scheme=="https"||scheme=="mailto"||scheme=="tel"||scheme=="ftp"||scheme=="file")return url;
        return null;
    }
    /* "#Heading%20One" the way the app matches it (AnchorRule.Target: percent-decoded, lower-cased) when
       the result is a plain slug, so it equals the heading's id; a browser compares the fragment with
       the id case-sensitively. Any other "#..." link stays as written. */
    static string AnchorHref(string href){
        if(href.Length<2||href[0]!='#')return href;
        string target;
        try{target=AnchorRule.Target(href);}catch(Exception){return href;}
        if(target.Length==0)return href;
        if(!String.Equals(AnchorRule.Slug(target),target,StringComparison.Ordinal))return href;   // a space, a symbol or an edge blank: not a heading id
        return "#"+target;
    }
    // A color the way the app's inline regex reads it. CSS #RGB / #RRGGBB pass; WPF's #ARGB and
    // #AARRGGBB become rgba(); a color name is letters only, so it cannot carry anything else.
    static string CssColor(string value){
        if(value[0]!='#')return value.ToLowerInvariant();
        string hex=value.Substring(1);
        if(hex.Length==3||hex.Length==6)return value;
        if(hex.Length==4||hex.Length==8){
            int step=hex.Length/4;
            int[] part=new int[4];
            for(int k=0;k<4;k++){part[k]=Int32.Parse(hex.Substring(k*step,step),NumberStyles.HexNumber,CultureInfo.InvariantCulture);if(step==1)part[k]*=17;}
            return "rgba("+part[1]+","+part[2]+","+part[3]+","+(part[0]/255.0).ToString("0.###",CultureInfo.InvariantCulture)+")";
        }
        return null;
    }
    // style="color:..;font-size:..px" from the open tag of <span ...> or <font ...>, or null. The
    // size rule is Markdown.Inline's: em and % are relative to the 16px base, pt is 96/72, 6..120px.
    static string SpanStyle(string openTag){
        var style=new StringBuilder();
        Match color=ColorPattern.Match(openTag);
        if(color.Success){string css=CssColor(color.Groups[1].Value);if(css!=null)style.Append("color:").Append(css).Append(';');}
        Match size=SizePattern.Match(openTag);
        if(size.Success){
            double n;
            if(Double.TryParse(size.Groups[1].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out n)){
                string unit=size.Groups[2].Value.ToLowerInvariant();
                double px=Math.Max(6,Math.Min(120,unit=="em"?n*16:unit=="%"?n*16/100:unit=="pt"?n*96/72:n));
                style.Append("font-size:").Append(px.ToString("0.##",CultureInfo.InvariantCulture)).Append("px;");
            }
        }
        return style.Length==0?null:style.ToString();
    }

    // ---- blocks and inlines ----
    sealed class BlockWriter {
        sealed class Level {internal int Indent,Content;internal bool Ordered,Task;}
        sealed class State {internal readonly List<Level> Levels=new List<Level>();internal bool Paragraph;}
        readonly Context context;readonly StringBuilder sb;readonly bool quiet;
        // quiet: a trial rendering (the title text) that must not mark footnote references as used.
        internal BlockWriter(Context context,StringBuilder sb,bool quiet=false){this.context=context;this.sb=sb;this.quiet=quiet;}

        void Flush(State s){if(s.Paragraph){sb.Append("</p>\n");s.Paragraph=false;}}
        void Pop(State s){
            Level top=s.Levels[s.Levels.Count-1];
            sb.Append(top.Ordered?"</li>\n</ol>":"</li>\n</ul>");
            s.Levels.RemoveAt(s.Levels.Count-1);
            if(s.Levels.Count==0)sb.Append('\n');
        }
        void CloseLists(State s){while(s.Levels.Count>0)Pop(s);}

        // Emits one document's blocks in order, the way MarkdownPlainText.Render reads them; quotes recurse.
        internal void Render(string[] lines,int depth){
            var s=new State();
            for(int i=0;i<lines.Length;i++){
                string line=lines[i];
                char fenceMarker;int fenceCount;int fenceInfo;
                if(FenceOpen(line,out fenceMarker,out fenceCount,out fenceInfo)){
                    Flush(s);CloseLists(s);
                    string language=CodeLanguage(line.Substring(fenceInfo));
                    sb.Append("<pre><code");
                    if(language.Length>0)sb.Append(" class=\"language-").Append(language).Append('"');
                    sb.Append('>');
                    bool first=true;
                    while(++i<lines.Length&&!ClosingFence(lines[i],fenceMarker,fenceCount)){
                        if(!first)sb.Append('\n');
                        first=false;
                        AppendEscaped(sb,lines[i],0,lines[i].Length,false);
                    }
                    sb.Append("</code></pre>\n");
                    continue;
                }
                if(WhiteOnly(line,0)){Flush(s);continue;}   // a blank line ends the paragraph, not the list
                string definitionId,definitionBody;bool note;
                if(TryDefinition(line,out definitionId,out definitionBody,out note)){
                    Flush(s);CloseLists(s);
                    if(note){
                        while(i+1<lines.Length&&(lines[i+1].StartsWith("    ",StringComparison.Ordinal)||lines[i+1].StartsWith("\t",StringComparison.Ordinal)))definitionBody+="\n"+lines[++i].TrimStart();
                        string key=context.Number(definitionId).ToString(CultureInfo.InvariantCulture);
                        sb.Append("<p class=\"footnote\" id=\"fn-").Append(key).Append("\"><a href=\"#fnref-").Append(key).Append("\">").Append(key).Append(".</a> ");
                        AppendInline(definitionBody);
                        sb.Append("</p>\n");
                    }
                    continue;   // a plain reference definition leaves no output
                }
                if(i+1<lines.Length&&MarkdownPlainText.TableStart(line,lines[i+1])){
                    Flush(s);CloseLists(s);
                    string[] header=MarkdownPlainText.Cells(line);
                    string[] alignments=MarkdownPlainText.Cells(lines[i+1]);
                    sb.Append("<div class=\"table-wrap\"><table>\n<thead>\n");
                    Row(header,alignments,true);
                    sb.Append("</thead>\n");
                    i++;
                    bool body=false;
                    while(i+1<lines.Length&&!String.IsNullOrWhiteSpace(lines[i+1])&&MarkdownTableCommands.HasTablePipe(lines[i+1])){
                        i++;
                        if(!body){sb.Append("<tbody>\n");body=true;}
                        Row(FitCells(MarkdownPlainText.Cells(lines[i]),header.Length),alignments,false);
                    }
                    if(body)sb.Append("</tbody>\n");
                    sb.Append("</table></div>\n");
                    continue;
                }
                string headingBody;int headingLevel;
                if(MarkdownPlainText.AtxHeading(line,out headingLevel,out headingBody)){Flush(s);CloseLists(s);WriteHeading(headingLevel,headingBody);continue;}
                if(i+1<lines.Length&&!ListStart(line)&&SetextUnderline(lines[i+1],out headingLevel)){
                    Flush(s);CloseLists(s);WriteHeading(headingLevel,line);
                    i++;
                    continue;
                }
                if(HorizontalRule(line)){Flush(s);CloseLists(s);sb.Append("<hr>\n");continue;}
                string quoteBody,nextQuote;
                if(QuoteLine(line,out quoteBody)){
                    Flush(s);CloseLists(s);
                    var quote=new StringBuilder(quoteBody);
                    var lazy=new LazyQuote();lazy.Line(quoteBody);
                    while(i+1<lines.Length){
                        if(QuoteLine(lines[i+1],out nextQuote)){i++;quote.Append('\n').Append(nextQuote);lazy.Line(nextQuote);}
                        else if(lazy.Continues(lines,i+1)){i++;quote.Append('\n').Append(lines[i].TrimStart(' ','\t'));}   // lazy continuation: part of the quote's paragraph
                        else break;
                    }
                    sb.Append("<blockquote>\n");
                    if(depth<MaxQuoteDepth)new BlockWriter(context,sb,quiet).Render(quote.ToString().Split('\n'),depth+1);
                    else{sb.Append("<p>");AppendText(sb,quote.ToString(),false);sb.Append("</p>\n");}
                    sb.Append("</blockquote>\n");
                    continue;
                }
                string indent;bool ordered;string number;bool task;bool checkedBox;string listBody;int contentColumn;
                if(ListLine(line,out indent,out contentColumn,out ordered,out number,out task,out checkedBox,out listBody)){
                    Flush(s);
                    int listIndent=0;
                    for(int c=0;c<indent.Length;c++)listIndent+=indent[c]=='\t'?4:1;
                    // A deeper marker nests only when it reaches the previous item's content column.
                    while(s.Levels.Count>0&&(s.Levels[s.Levels.Count-1].Indent>listIndent
                        ||(s.Levels[s.Levels.Count-1].Indent==listIndent&&(s.Levels[s.Levels.Count-1].Ordered!=ordered||s.Levels[s.Levels.Count-1].Task!=task))
                        ||(s.Levels[s.Levels.Count-1].Indent<listIndent&&listIndent<s.Levels[s.Levels.Count-1].Content)))Pop(s);
                    if(s.Levels.Count==0||s.Levels[s.Levels.Count-1].Indent<listIndent){
                        if(s.Levels.Count>0)sb.Append('\n');   // the new list sits inside the parent's open <li>
                        sb.Append(ordered?"<ol":"<ul");
                        if(ordered&&StartIndex(number)!=1)sb.Append(" start=\"").Append(StartIndex(number).ToString(CultureInfo.InvariantCulture)).Append('"');
                        if(task)sb.Append(" class=\"task\"");
                        sb.Append(">\n");
                        s.Levels.Add(new Level {Indent=listIndent,Ordered=ordered,Task=task});
                    }else sb.Append("</li>\n");
                    s.Levels[s.Levels.Count-1].Content=contentColumn;
                    sb.Append(task?"<li class=\"task\">":"<li>");
                    if(task)sb.Append(checkedBox?"<input type=\"checkbox\" disabled checked> ":"<input type=\"checkbox\" disabled> ");
                    ListItemBody(listBody,depth);
                    continue;
                }
                if(!s.Paragraph){CloseLists(s);sb.Append("<p>");s.Paragraph=true;}
                else sb.Append("<br>\n");
                AppendInline(line);
            }
            Flush(s);CloseLists(s);
        }
        // An item whose text is a heading ("- # Title") or a quote ("- > text") renders that block
        // inside the item, as the print parser does (ParseCore's list branch).
        void ListItemBody(string body,int depth){
            int level;string headingBody;
            if(MarkdownPlainText.AtxHeading(body,out level,out headingBody)){WriteHeading(level,headingBody);return;}
            if(body.Length>0&&body[0]=='>'){
                string rest=body.Substring(1);
                if(rest.Length>0&&Space(rest[0]))rest=rest.Substring(1);
                sb.Append("<blockquote>\n");
                if(depth<MaxQuoteDepth)new BlockWriter(context,sb,quiet).Render(new[]{rest},depth+1);
                sb.Append("</blockquote>");
                return;
            }
            AppendInline(body);
        }
        void WriteHeading(int level,string body){
            if(context.Title==null&&!quiet)context.Title=TitleText(body);
            sb.Append("<h").Append(level.ToString(CultureInfo.InvariantCulture));
            string id=quiet?null:context.HeadingId(body);
            if(id!=null)AppendAttribute(sb,"id",id);
            sb.Append('>');
            AppendInline(body);
            sb.Append("</h").Append(level.ToString(CultureInfo.InvariantCulture)).Append(">\n");
        }
        // The heading's text without markup: the inline rendering with its tags dropped.
        string TitleText(string body){
            var trial=new StringBuilder();
            new BlockWriter(context,trial,true).AppendInline(body);
            string text=Regex.Replace(trial.ToString(),"<[^>]*>","");
            text=System.Net.WebUtility.HtmlDecode(text);
            text=Regex.Replace(text,@"\s+"," ").Trim();
            return text.Length==0?null:text;
        }
        void Row(string[] cells,string[] alignments,bool header){
            sb.Append("<tr>");
            string tag=header?"th":"td";
            for(int c=0;c<cells.Length;c++){
                sb.Append('<').Append(tag);
                if(c<alignments.Length){
                    string marker=alignments[c].Trim();
                    bool left=marker.StartsWith(":",StringComparison.Ordinal),right=marker.EndsWith(":",StringComparison.Ordinal);
                    if(left||right)sb.Append(" style=\"text-align:").Append(left&&right?"center":right?"right":"left").Append('"');
                }
                sb.Append('>');
                AppendInline(cells[c].Trim());
                sb.Append("</").Append(tag).Append('>');
            }
            sb.Append("</tr>\n");
        }
        // class="language-x" from the first word of the fence's info string; only characters that are
        // safe in a class name survive.
        static string CodeLanguage(string info){
            info=info.Trim();
            var word=new StringBuilder();
            for(int i=0;i<info.Length&&!Char.IsWhiteSpace(info[i]);i++){
                char c=info[i];
                if(c>='a'&&c<='z'||c>='A'&&c<='Z'||c>='0'&&c<='9'||c=='_'||c=='+'||c=='#'||c=='-'||c=='.')word.Append(c);
            }
            return word.ToString();
        }

        void AppendInline(string value){Inline(ExpandReferences(ProtectEscapes(value),context),0);}
        void Inline(string value,int depth){
            if(depth>MaxInlineDepth){AppendText(sb,value,false);return;}
            // A backslash at a line's end is a hard break and drops; an escaped pair is already a sentinel.
            if(depth==0&&value.IndexOf('\\')>=0){
                if(value.Length>0&&value[value.Length-1]=='\\')value=value.Substring(0,value.Length-1);
                if(value.IndexOf("\\\n",StringComparison.Ordinal)>=0)value=value.Replace("\\\n","\n");
            }
            int at=0,p=0;
            while(p<value.Length){
                int kind;int end;int contentStart;int contentEnd;
                if(!Token(value,p,out kind,out end,out contentStart,out contentEnd)){p++;continue;}
                if(p>at)AppendText(sb,value.Substring(at,p-at),false);
                switch(kind){
                    case 1:case 2:{
                        int closeStart;int closeEnd;
                        if(BalancedTag(value,contentStart,kind==1?"span":"font",out closeStart,out closeEnd)){contentEnd=closeStart;end=closeEnd;}
                        string style=SpanStyle(value.Substring(p,contentStart-p));
                        if(style!=null)sb.Append("<span style=\"").Append(style).Append("\">");
                        Inline(value.Substring(contentStart,contentEnd-contentStart),depth+1);
                        if(style!=null)sb.Append("</span>");
                        break;
                    }
                    case 3:
                        sb.Append("<code>");
                        AppendText(sb,UnpadCode(value.Substring(contentStart,contentEnd-contentStart)),true);   // a code span has no character references
                        sb.Append("</code>");
                        break;
                    case 4:AppendImage(value,contentStart,contentEnd,end);break;
                    case 5:AppendLink(value,contentStart,contentEnd,end,depth);break;
                    case 6:case 7:Wrap("strong",value,contentStart,contentEnd,depth);break;
                    case 8:Wrap("del",value,contentStart,contentEnd,depth);break;
                    case 9:case 10:Wrap("em",value,contentStart,contentEnd,depth);break;
                    case 11:sb.Append("<br>\n");break;
                    case 12:{
                        string target=value.Substring(contentStart,contentEnd-contentStart);
                        string href=LinkTarget(RestoreCodeEscapes(target.IndexOf(':')<0?"mailto:"+target:target));
                        if(href!=null){sb.Append("<a");AppendAttribute(sb,"href",href);sb.Append('>');}
                        AppendText(sb,target,true);
                        if(href!=null)sb.Append("</a>");
                        break;
                    }
                    default:break;   // 13: a same-line HTML comment is markup and shows nothing
                }
                at=end;p=end;
            }
            if(at<value.Length)AppendText(sb,value.Substring(at),false);
        }
        void Wrap(string tag,string value,int start,int stop,int depth){
            sb.Append('<').Append(tag).Append('>');
            Inline(value.Substring(start,stop-start),depth+1);
            sb.Append("</").Append(tag).Append('>');
        }
        void AppendImage(string value,int altStart,int altEnd,int end){
            string alt=value.Substring(altStart,altEnd-altStart),address,title;
            TargetParts(value,altEnd+2,end-1,out address,out title);
            address=RestoreCodeEscapes(address);
            string source=ResolveImage(context,address);
            if(source==null){   // the app's text for a picture it cannot load
                sb.Append("<span class=\"missing-image\">");
                AppendText(sb,context.ImageLabel,true);
                AppendText(sb,alt,true);
                sb.Append("]</span>");
                return;
            }
            sb.Append("<img");AppendAttribute(sb,"src",source);AppendAttribute(sb,"alt",RestoreEscapes(alt));
            if(title.Length>0)AppendAttribute(sb,"title",RestoreEscapes(title));
            sb.Append('>');
        }
        void AppendLink(string value,int labelStart,int labelEnd,int end,int depth){
            string label=value.Substring(labelStart,labelEnd-labelStart),address,title;
            TargetParts(value,labelEnd+2,end-1,out address,out title);
            address=RestoreCodeEscapes(address);
            if(address.StartsWith("#mdnote-footnote-",StringComparison.Ordinal)){   // a footnote reference: its number, raised
                string key=Uri.EscapeDataString(label);
                sb.Append("<sup><a href=\"#fn-").Append(key).Append('"');
                if(!quiet&&context.NoteUsed.Add(key))sb.Append(" id=\"fnref-").Append(key).Append('"');
                sb.Append('>');
                AppendText(sb,label,false);
                sb.Append("</a></sup>");
                return;
            }
            string href=LinkTarget(address);
            if(href!=null)href=AnchorHref(href);
            if(href!=null){
                sb.Append("<a");AppendAttribute(sb,"href",href);
                if(title.Length>0)AppendAttribute(sb,"title",RestoreEscapes(title));
                sb.Append('>');
            }
            Inline(label,depth+1);
            if(href!=null)sb.Append("</a>");
        }
    }
}
}
