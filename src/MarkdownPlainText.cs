using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;

namespace MDNote {
// Converts Markdown to the plain text that ReadableText(Markdown.Parse(...)) returns.
// oracle: ReadableText(Markdown.Parse(...)), see MarkdownPlainTextTests.
// The table cell split follows the live renderer (MarkdownLivePreview.TablePipes: odd backslash
// runs, code spans by run length), which is what the user sees; Markdown.Cells and this file use
// the same cell rule, so a pipe inside a closed `code` span is cell text in both. The table start
// (TableStart: delimiter row and header cell count), the ATX closing "#" (AtxHeading) and the link
// target scan (TargetEnd) are the live renderer's CommonMark/GFM rules too; Markdown.ParseCore can
// read the same helpers instead of its own regexes.
// The inline rules the live view applies are read here and in Markdown.Inline: one space is
// stripped from each end of a padded code span (Markdown.UnpadCode), a backslash at the line's end
// is a hard break and drops, an angle autolink loses its brackets (the address is the text), an
// HTML comment that closes on the same line is markup, and a link/image title in parentheses is
// not part of the destination. None of them touch text inside inline or fenced code.
// The list rules are CommonMark's too and the live view (MarkdownLivePreview.NextList) and the
// print parser (Markdown.NestLists) read the same two: a space/tab-only line between items keeps
// one list (one numbering, the line itself stays a blank line), and a deeper marker nests only
// when it reaches the parent item's content column ("1. " content starts at column 3, so a
// two-space bullet under it is a sibling list). ListContentColumn and BlankLine below are the
// shared scans. A paragraph line indented to an open item's content column is that item's
// continuation and keeps the list open (MarkdownLivePreview.ContinuationList, Markdown.NestLists).
// Linear in the input length: no FlowDocument is built and no Regex runs per character.
internal static class MarkdownPlainText {
    sealed class References {
        internal readonly Dictionary<string,string> Links=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,int> Notes=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        internal int Number(string id){int number;return Notes.TryGetValue(id,out number)?number:1;}
    }
    // Mirrors one open System.Windows.Documents.List built by Markdown.NestLists.
    sealed class ListLevel {
        internal int Indent;internal bool Ordered;internal bool Task;
        internal int Content; // column where this item's own text starts (marker plus its spaces)
        internal int Number; // next item number
        internal int Style;  // 0 none, 1 decimal, 2 bullet
    }
    const int MaxInlineDepth=12; // Markdown.Inline stops recursing past depth 12
    const string ImageLabel="[이미지: "; // Korean text; Convert's caller passes T("[이미지: ")
    static readonly string[] CodeLanguages={"javascript","js","typescript","ts","python","py","csharp","cs","c","cpp","java","json","css","go","rust","rs","sql","bash","sh","powershell","ps1","html","xml"};

    /* imageLabel is the "[이미지: " prefix an image link falls back to. A caller in a MainWindow passes
       T("[이미지: "); the default keeps the Korean text the ReadableText oracle (MDNote.cs) writes. */
    internal static string Convert(string markdown,string imageLabel=null){
        if(markdown==null)markdown="";
        // Markdown.ParseCore collapses CRLF and CR before splitting lines.
        string normalized=markdown.Replace("\r\n","\n").Replace('\r','\n');
        string[] lines=normalized.Split('\n');
        var references=new References();
        // YAML front matter is kept as plain source lines: its "---" delimiters are not rules
        // and nothing inside it is a definition or block markup (the live box and export agree).
        // The opener test is FrontMatterEnd's own first check, repeated here so an ordinary
        // document never loads the live-preview type (cold WPF statics) just to learn "no".
        int frontMatter=lines.Length>=2&&String.Equals(lines[0],"---",StringComparison.Ordinal)?MarkdownLivePreview.FrontMatterEnd(lines):0;
        ScanReferences(lines,references,frontMatter);
        var result=new StringBuilder(normalized.Length+16);
        Render(lines,references,result,imageLabel??ImageLabel,frontMatter);
        if(result.Length>0)result.Length--;        // ReadableText drops the final block newline
        return Canonical(result.ToString());
    }
    static string Canonical(string value){return value.Replace("\r\n","\n").Replace('\r','\n');}

    // Block structure (a marker's separator, indentation, a closing fence's tail, a heading's
    // closing "#") reads only an ASCII space or tab, the same rule the renderer and the
    // structural keys (MarkdownStructuralInput/MarkdownCommands) use. A full-width space
    // (U+3000, the Japanese/Chinese IME's space) stays text, so "-　item" is a paragraph here
    // too and MD-to-TXT draws the line the screen draws.
    static bool Space(char c){return c==' '||c=='\t';}
    static bool WhiteOnly(string value,int start){for(int i=start;i<value.Length;i++)if(!Space(value[i]))return false;return true;}
    // The live view's blank-line rule (MarkdownLivePreview.BlankLine): ASCII spaces and tabs
    // only, so a U+3000 line is content for all three renderers and ends a list everywhere.
    internal static bool BlankLine(string line){return WhiteOnly(line,0);}
    // "[TOC]" alone on a line (case-insensitive), the in-place table of contents. At most three
    // leading spaces/tabs, like every block marker, and nothing but spaces after the "]" so a
    // sentence that starts with it stays paragraph text. The live view, the print parser and the
    // HTML export read this one rule; a line inside a fence or a container is decided by the
    // caller's own block state.
    internal static bool TocLine(string line){
        if(line==null)return false;
        int p=0,columns=0;
        while(p<line.Length&&(line[p]==' '||line[p]=='\t')){columns+=line[p]=='\t'?4:1;if(columns>3)return false;p++;}
        return TocAt(line,p);
    }
    internal static bool TocAt(string text,int start){
        if(text==null||start<0||start+5>text.Length)return false;
        if(text[start]!='['||text[start+4]!=']')return false;
        char t=text[start+1],o=text[start+2],c=text[start+3];
        if((t!='t'&&t!='T')||(o!='o'&&o!='O')||(c!='c'&&c!='C'))return false;
        for(int i=start+5;i<text.Length;i++)if(text[i]!=' '&&text[i]!='\t')return false;
        return true;
    }
    // .NET \w: letters, non-spacing marks, decimal digits and connector punctuation, plus the joiners.
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

    // ^\s{0,3}(`{3,}|~{3,})(.*)$ ; info starts at the returned index.
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
        // A backtick in the info string disables the fence.
        return !(marker=='`'&&line.IndexOf('`',info)>=0);
    }
    // ^\s{0,3}marker{count,}\s*$
    static bool ClosingFence(string line,char marker,int count){
        int at=0;while(at<3&&at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length||line[at]!=marker)return false;
        int end=at;while(end<line.Length&&line[end]==marker)end++;
        if(end-at<count)return false;
        while(end<line.Length&&Space(line[end]))end++;
        return end==line.Length;
    }

    // ^\s{0,3}\[(\^?[^\]]+)\]:\s*(.*)$ with the reference/footnote body rules of Markdown.TryDefinition.
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
        if(raw[0]=='^'){
            note=true;id=raw.Substring(1);body=rest;
            return id.Length>0;
        }
        note=false;id=raw;
        // ^(?:<([^>]+)>|(\S+))(?:\s+.*)?$ : a failed <...> falls back to the plain token.
        if(rest.Length==0)return false;
        if(rest[0]=='<'){
            int gt=rest.IndexOf('>',1);
            if(gt>=2&&(gt+1>=rest.Length||Space(rest[gt+1]))){body=rest.Substring(1,gt-1);return true;}
        }
        int end=0;while(end<rest.Length&&!Space(rest[end]))end++;
        if(end==0)return false; // the regex needs one non-space character
        body=rest.Substring(0,end);
        return true;
    }

    // GFM delimiter row: cells of optional spaces, an optional leading colon, one or more dashes
    // and an optional trailing colon, at least one cell and at least one pipe. This is
    // MarkdownLivePreview.IsDelimiter, the screen's rule: a pipe-less "---" under "a | b" is a
    // setext underline, so it must not open a table the screen does not show.
    internal static bool TableDelimiter(string line){
        bool seenPipe=false;
        int at=0;
        while(at<line.Length&&Space(line[at]))at++;
        if(at<line.Length&&line[at]=='|'){seenPipe=true;at++;}
        int cells=0;
        while(true){
            while(at<line.Length&&Space(line[at]))at++;
            if(at>=line.Length)break; // a trailing pipe or spaces end the row; no empty cell
            if(line[at]==':')at++;
            int dashes=0;
            while(at<line.Length&&line[at]=='-'){dashes++;at++;}
            if(dashes==0)return false; // "|:|", "|::-|" and a blank cell are not delimiter cells
            if(at<line.Length&&line[at]==':')at++;
            while(at<line.Length&&Space(line[at]))at++;
            cells++;
            if(at>=line.Length)break;
            if(line[at]!='|')return false;
            seenPipe=true;at++;
            if(at>=line.Length)break; // the trailing pipe owns no cell
        }
        return seenPipe&&cells>0;
    }
    // GFM table start: the header row needs a table pipe and the delimiter row exactly as many
    // cells as the header (MarkdownLivePreview.NextIsDelimiter). A "| a | b |" header over
    // "---" or over "| --- |" is not a table; extra data cells are cut by FitCells.
    // MarkdownTableCommands.HasTablePipe/CellCount are already the renderer's row rule, shared
    // with the table commands and the structural keys, so the TXT table start reads them too.
    internal static bool TableStart(string line,string delimiter){
        if(!TableDelimiter(delimiter))return false;
        if(!MarkdownTableCommands.HasTablePipe(line))return false;
        return MarkdownTableCommands.CellCount(line)==MarkdownTableCommands.CellCount(delimiter);
    }

    // ^\s{0,3}(#{1,6})\s+(.*)$ with the CommonMark closing sequence: a trailing run of "#" is
    // structure only when it starts the content or follows a space/tab and is followed by spaces
    // only. "# C#" keeps its "#"; "## 제목 ##" loses the pair; "### ###" is an empty heading.
    // Markdown.ParseCore's heading regex and the list-item heading read the same rule.
    internal static bool AtxHeading(string line,out int level,out string body){
        level=0;body="";
        int at=0;while(at<3&&at<line.Length&&Space(line[at]))at++;
        int begin=at;while(at<line.Length&&line[at]=='#')at++;
        level=at-begin;
        if(level<1||level>6)return false;
        if(at>=line.Length||!Space(line[at]))return false;
        while(at<line.Length&&Space(line[at]))at++;
        int end=line.Length;
        while(end>at&&Space(line[end-1]))end--;
        int hash=end;
        while(hash>at&&line[hash-1]=='#')hash--;
        if(hash<end&&(hash==at||Space(line[hash-1]))){
            end=hash;
            while(end>at&&Space(line[end-1]))end--;
        }
        body=line.Substring(at,end-at);
        return true;
    }
    // ^\s{0,3}(=+|-{2,})\s*$ : the "=" run is one or more, the "-" run two or more. A single "-"
    // is the start of a list item being typed, not an underline, so a paragraph over it stays a
    // paragraph (MarkdownLivePreview.SetextUnderlineLevel reads the same rule).
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
    // ^\s*(?:[-+*]|\d+[.)])\s (setext guard in ParseCore)
    static bool ListStart(string line){
        int at=0;while(at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length)return false;
        char c=line[at];
        if(c=='-'||c=='+'||c=='*')return at+1<line.Length&&Space(line[at+1]);
        if(Char.IsDigit(c)){ // .NET \d matches every Nd character, not just ASCII
            while(at<line.Length&&Char.IsDigit(line[at]))at++;
            if(at>=line.Length||(line[at]!='.'&&line[at]!=')'))return false;
            return at+1<line.Length&&Space(line[at+1]);
        }
        return false;
    }
    // ^\s{0,3}([-*_])(?:\s*\1){2,}\s*$
    static bool HorizontalRule(string line){
        // A leading tab is four indentation columns, like the live view's IsRule: "\t---" is
        // paragraph text, not a rule, and only 0-3 columns of indentation allow one.
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
    // ^\s*>\s?(.*)$
    static bool QuoteLine(string line,out string body){
        body="";
        int at=0;while(at<line.Length&&Space(line[at]))at++;
        if(at>=line.Length||line[at]!='>')return false;
        at++;
        if(at<line.Length&&Space(line[at]))at++; // \s? eats one character only
        body=line.Substring(at);
        return true;
    }
    // ^(\s*)(?:([-+*])|(\d+)[.)])\s+(.*)$ plus the task marker prefix of ParseCore.
    // content is the column where the item's own text starts (the marker and the spaces after
    // it, a tab as four columns), CommonMark's content indentation for the next marker's
    // nesting test; the task marker is content, so it is not counted.
    static bool ListLine(string line,out string indent,out int content,out bool ordered,out string number,out bool task,out bool checkedBox,out string body){
        indent="";content=0;ordered=false;number="";task=false;checkedBox=false;body="";
        int at=0,column=0;
        while(at<line.Length&&Space(line[at])){column+=line[at]=='\t'?4:1;at++;}
        indent=line.Substring(0,at);
        if(at>=line.Length)return false;
        char c=line[at];
        if(c=='-'||c=='+'||c=='*'){at++;column++;}
        else if(Char.IsDigit(c)){ // \d
            int digits=at;while(at<line.Length&&Char.IsDigit(line[at])){at++;column++;}
            number=line.Substring(digits,at-digits);ordered=true;
            if(at>=line.Length||(line[at]!='.'&&line[at]!=')'))return false;
            at++;column++;
        } else return false;
        if(at>=line.Length||!Space(line[at]))return false;
        while(at<line.Length&&Space(line[at])){column+=line[at]=='\t'?4:1;at++;}
        content=column;
        body=line.Substring(at);
        // ^\[([ xX])\]\s*(.*)$
        if(body.Length>=3&&body[0]=='['&&body[2]==']'&&(body[1]==' '||body[1]=='x'||body[1]=='X')){
            task=true;checkedBox=body[1]!=' ';
            int rest=3;while(rest<body.Length&&Space(body[rest]))rest++;
            body=body.Substring(rest);
        }
        return true;
    }
    // The column where a list item's own text starts, the same value ListLine computes: the
    // print path (Markdown.NestLists) nests a following marker only when it reaches this
    // column, so "1. " (content column 3) plus a two-space bullet is a sibling list, not a
    // child (CommonMark). False for a line that is not a list item.
    internal static bool ListContentColumn(string line,out int content){
        string indent;bool ordered;string number;bool task;bool checkedBox;string body;
        int column;
        if(!ListLine(line,out indent,out column,out ordered,out number,out task,out checkedBox,out body)){content=0;return false;}
        content=column;
        return true;
    }

    // A quote line indented to an open item's content column ("  > text" under "- > text"): a quote inside
    // that item (CommonMark), not a quote of its own that ends the list. Its first non-blank character is
    // ">" and its indentation reaches `content`; a quote whose body is a list marker ("  > - a") keeps the
    // list-in-quote reading. MarkdownLivePreview.QuoteContinuationList, Markdown.NestLists, Render below and
    // the HTML export apply the same rule.
    internal static bool ItemQuoteLine(string line,int content){
        int at=0,column=0;
        while(at<line.Length&&Space(line[at])){column+=line[at]=='\t'?4:1;at++;}
        if(column<content||at>=line.Length||line[at]!='>')return false;
        int levels=0;
        while(at<line.Length&&line[at]=='>'&&levels<6){at++;levels++;if(at<line.Length&&Space(line[at]))at++;}
        string indent;bool ordered;string number;bool task;bool checkedBox;string body;int bodyColumn;
        return !ListLine(line.Substring(at),out indent,out bodyColumn,out ordered,out number,out task,out checkedBox,out body);
    }

    // Markdown.Cells: trim, drop one edge pipe, then split on unescaped pipes outside backtick spans.
    internal static string[] Cells(string line){
        var cells=new List<string>();
        var cell=new StringBuilder();
        string value=line.Trim();
        if(value.StartsWith("|"))value=value.Substring(1);
        // The closing pipe is structure unless an ODD backslash run escapes it: "\\|" is a literal
        // backslash and a live pipe, so the row still ends there (MarkdownLivePreview.TablePipes).
        bool closed=value.EndsWith("|")&&!PipeEscaped(value,value.Length-1);
        if(closed)value=value.Substring(0,value.Length-1);
        bool escaped=false;int budget=value.Length*3;
        for(int i=0;i<value.Length;i++){
            char c=value[i];
            if(escaped){cell.Append(c);escaped=false;continue;}
            // #4: the escape backslash stays in the cell (the inline pass reads it), exactly like
            // Markdown.Cells, so print and TXT keep the same literal text.
            if(c=='\\'){cell.Append(c);escaped=true;continue;}
            // A code span holds its pipes: a run of n backticks closes at the next run of exactly n
            // (MarkdownLivePreview.TablePipes/FindCodeEnd), not at the next single backtick, so a
            // pipe inside "``a`b|c``" stays cell text.
            if(c=='`'&&budget>0){
                int run=1;while(i+run<value.Length&&value[i+run]=='`')run++;
                int close=CodeClose(value,i+run,run,ref budget);
                if(close>=0){cell.Append(value,i,close+run-i);i=close+run-1;continue;}
                cell.Append(value,i,run);i+=run-1;continue;
            }
            budget-=2;
            if(c=='|'){cells.Add(cell.ToString());cell.Length=0;}
            else cell.Append(c);
        }
        cells.Add(cell.ToString());
        string[] result=cells.ToArray();
        // A backslash at the end of a cell is a literal backslash (GFM, and the live view draws it): only a backslash at the end
        // of the source line is a hard break that drops. The last cell of a row with no closing pipe is that line end.
        bool lineEndBackslash=!closed&&line.Length>0&&line[line.Length-1]=='\\';
        for(int k=0;k<result.Length;k++){
            if(k==result.Length-1&&lineEndBackslash)continue;
            result[k]=KeepCellBackslash(result[k]);
        }
        return result;
    }
    // The backslash that ends the cell's text (an odd run: a pair is one escaped backslash already) travels as the escape sentinel
    // for "\\", so the inline pass, which drops a backslash at the end of the text it gets, keeps it. Trailing spaces stay where
    // they are for the caller's Trim.
    static string KeepCellBackslash(string cell){
        int end=cell.Length;
        while(end>0&&Space(cell[end-1]))end--;
        int count=0;
        for(int i=end-1;i>=0&&cell[i]=='\\';i--)count++;
        if((count&1)==0)return cell;
        return cell.Substring(0,end-1)+(char)(0xE000+Markdown.EscapablePunctuation.IndexOf('\\'))+cell.Substring(end);
    }
    // Odd backslash run before the pipe: escaped (a backslash pair is one literal backslash).
    internal static bool PipeEscaped(string value,int at){
        int count=0;for(int i=at-1;i>=0&&value[i]=='\\';i--)count++;
        return (count&1)==1;
    }
    // The first run of exactly count backticks at or after start, mirroring
    // MarkdownLivePreview.FindCodeEnd; the shared budget keeps the scan linear.
    internal static int CodeClose(string value,int start,int count,ref int budget){
        int at=start;
        while(at<value.Length&&budget>0){
            if(value[at]!='`'){at++;budget--;continue;}
            int first=at;
            while(at<value.Length&&value[at]=='`'&&budget>0){at++;budget--;}
            if(at-first==count)return first;
        }
        return -1;
    }
    // #5: Markdown.FitCells - a data row is cut to the header's cell count (padded with empty
    // cells) so the oracle's table has one column per header cell.
    static string[] FitCells(string[] cells,int count){
        if(cells.Length==count)return cells;
        var fit=new string[count];
        for(int i=0;i<count;i++)fit[i]=i<cells.Length?cells[i]:"";
        return fit;
    }

    static bool IsCodeLanguage(string info){
        int end=info.IndexOf(' ');
        string first=end<0?info:info.Substring(0,end);
        for(int i=0;i<CodeLanguages.Length;i++)if(String.Equals(CodeLanguages[i],first,StringComparison.OrdinalIgnoreCase))return true;
        return false;
    }
    static int TrimEndNewlines(StringBuilder value){
        int length=value.Length;
        while(length>0&&(value[length-1]=='\r'||value[length-1]=='\n'))length--;
        return length;
    }
    static int StartIndex(string number){
        int first;
        if(Int32.TryParse(number,out first)&&first>0&&first<1000000)return first;
        return 1;
    }
    // Emits one document's blocks in order; quotes recurse with their own list state.
    static void Render(string[] lines,References references,StringBuilder result,string imageLabel,int frontMatter=0){
        var levels=new List<ListLevel>(); // NestLists drops its levels at every non-list block except a blank line
        bool inPre=false;
        for(int i=0;i<lines.Length;i++){
            string line=lines[i];
            if(i<frontMatter){result.Append(line).Append('\n');continue;}
            char fenceMarker;int fenceCount;int fenceInfo;
            if(FenceOpen(line,out fenceMarker,out fenceCount,out fenceInfo)){
                var code=new StringBuilder();
                bool first=true;
                while(++i<lines.Length&&!ClosingFence(lines[i],fenceMarker,fenceCount)){
                    if(!first)code.Append('\n');
                    first=false;
                    code.Append(lines[i]);
                }
                result.Append(code.ToString()).Append('\n');
                levels.Clear();
                continue;
            }
            // GitHub-allowed block HTML: a tag-only line is structure and leaves no text. <pre> and
            // </pre> toggle the literal body, where no markdown or block rule runs.
            int htmlTagStart;
            InlineHtml.Tag htmlTag=InlineHtml.BlockTagLine(line,out htmlTagStart);
            if(inPre){
                if(htmlTag!=null&&htmlTag.Name=="pre"&&htmlTag.Closing)inPre=false;
                else result.Append(line).Append('\n');
                continue;
            }
            if(htmlTag!=null){
                if(htmlTag.Name=="pre")inPre=!htmlTag.Closing;
                levels.Clear();
                continue;
            }
            InlineHtml.Tag htmlOpen;
            if(InlineHtml.SingleLineBlock(line,out htmlOpen)){
                string htmlBody=line.Substring(htmlOpen.End,htmlOpen.CloseStart-htmlOpen.End);
                if(htmlOpen.Name=="pre")result.Append(htmlBody);
                else AppendInline(result,htmlBody,references,imageLabel);
                result.Append('\n');
                levels.Clear();
                continue;
            }
            // A blank line does not end a list (CommonMark keeps the container open): the item
            // after it continues the numbering, the same rule the live view's block cache and
            // the print path (Markdown.NestLists) read. The blank line itself stays a blank line.
            if(WhiteOnly(line,0)){result.Append('\n');continue;}
            string definitionId,definitionBody;bool note;
            if(TryDefinition(line,out definitionId,out definitionBody,out note)){
                if(note){
                    while(i+1<lines.Length&&(lines[i+1].StartsWith("    ")||lines[i+1].StartsWith("\t")))definitionBody+="\n"+lines[++i].TrimStart();
                    result.Append(references.Number(definitionId)).Append(". ");
                    AppendInline(result,definitionBody,references,imageLabel);
                }
                // A plain reference definition leaves an empty paragraph, i.e. a blank line.
                result.Append('\n');
                levels.Clear();
                continue;
            }
            if(i+1<lines.Length&&TableStart(line,lines[i+1])){
                string[] header=Cells(line);
                AppendRow(result,header,references,imageLabel);
                i++;
                while(i+1<lines.Length&&!String.IsNullOrWhiteSpace(lines[i+1])&&MarkdownTableCommands.HasTablePipe(lines[i+1])){i++;AppendRow(result,FitCells(Cells(lines[i]),header.Length),references,imageLabel);}
                levels.Clear();
                continue;
            }
            string headingBody;int headingLevel;
            if(AtxHeading(line,out headingLevel,out headingBody)){AppendInline(result,headingBody,references,imageLabel);result.Append('\n');levels.Clear();continue;}
            if(i+1<lines.Length&&!ListStart(line)&&SetextUnderline(lines[i+1],out headingLevel)){
                AppendInline(result,line,references,imageLabel);
                result.Append('\n');
                // A setext heading indented to an open item's content column is a heading inside that item and the list stays
                // open (SetextInItem; the live view and the print path read the same pair the same way).
                int setextKeep,setextClose;
                if(levels.Count>0&&SetextInItem(line,lines[i+1],ContentColumns(levels),out setextKeep,out setextClose)){
                    if(levels.Count>setextClose+1)levels.RemoveRange(setextClose+1,levels.Count-setextClose-1);
                }else levels.Clear();
                i++;
                continue;
            }
            if(HorizontalRule(line)){levels.Clear();continue;} // BlockUIContainer contributes no text
            string quoteBody,nextQuote;
            if(QuoteLine(line,out quoteBody)){
                int quoteItem=ItemQuoteLevel(levels,line);
                var body=new StringBuilder(quoteBody);
                while(i+1<lines.Length&&QuoteLine(lines[i+1],out nextQuote)){i++;body.Append('\n').Append(nextQuote);}
                RenderQuote(body.ToString().Split('\n'),references,result,imageLabel);
                // A quote indented to an open item's content column is a quote inside that item: the list
                // stays open (items deeper than the one it sits in close), the same rule as the live view
                // (MarkdownLivePreview.QuoteContinuationList) and the print path (Markdown.NestLists).
                if(quoteItem>=0){if(levels.Count>quoteItem+1)levels.RemoveRange(quoteItem+1,levels.Count-quoteItem-1);continue;}
                levels.Clear();
                continue;
            }
            string indent;bool ordered;string number;bool task;bool checkedBox;string listBody;
            int contentColumn;
            if(ListLine(line,out indent,out contentColumn,out ordered,out number,out task,out checkedBox,out listBody)){
                int listIndent=0;
                for(int c=0;c<indent.Length;c++)listIndent+=indent[c]=='\t'?4:1;
                // A deeper marker nests only when it reaches the previous item's content column
                // (CommonMark: "1. " content starts at column 3, so a two-space bullet is a
                // sibling list, not a child); anything left of that column closes the item.
                while(levels.Count>0&&(levels[levels.Count-1].Indent>listIndent
                    ||(levels[levels.Count-1].Indent==listIndent&&(levels[levels.Count-1].Ordered!=ordered||levels[levels.Count-1].Task!=task))
                    ||(levels[levels.Count-1].Indent<listIndent&&listIndent<levels[levels.Count-1].Content)))levels.RemoveAt(levels.Count-1);
                if(levels.Count==0||levels[levels.Count-1].Indent<listIndent){
                    levels.Add(new ListLevel {Indent=listIndent,Ordered=ordered,Task=task,Style=task?0:ordered?1:2,Number=ordered?StartIndex(number):1});
                }
                ListLevel level=levels[levels.Count-1];
                level.Content=contentColumn; // each item is its own container for the next marker
                string marker;
                if(task)marker=checkedBox?"☑ ":"☐ ";
                else if(level.Style==1)marker=level.Number+". ";
                else if(level.Style==2)marker="- ";
                else marker="";
                result.Append(' ',(levels.Count-1)*2).Append(marker);
                if(listBody.Length>0&&listBody[0]=='>'){
                    // An item whose text is a quote ("- > text") holds that quote (Markdown.ParseCore's list branch),
                    // and the quote lines indented to the item's content column that follow it are the same quote.
                    string firstQuote=listBody.Substring(1);
                    if(firstQuote.Length>0&&Space(firstQuote[0]))firstQuote=firstQuote.Substring(1);
                    var itemQuote=new StringBuilder(firstQuote);string nextItemQuote;
                    while(i+1<lines.Length&&ItemQuoteLine(lines[i+1],contentColumn)&&QuoteLine(lines[i+1],out nextItemQuote)){i++;itemQuote.Append('\n').Append(nextItemQuote);}
                    RenderQuote(itemQuote.ToString().Split('\n'),references,result,imageLabel);
                }else{
                    // An item whose text is an ATX heading ("- ## text", "1. ### text", "- [ ] ## text") loses the
                    // heading marks and the closing "#" run like a top-level heading (Markdown.ParseCore's list branch
                    // reads the same AtxHeading); the list marker and the task box stay.
                    if(AtxHeading(listBody,out headingLevel,out headingBody))AppendInline(result,headingBody,references,imageLabel);
                    else AppendInline(result,listBody,references,imageLabel);
                    result.Append('\n');
                }
                level.Number++; // NestLists numbers every item, whatever the source number was
                continue;
            }
            AppendInline(result,line,references,imageLabel);
            result.Append('\n');
            // A paragraph line indented to an open item's content column continues that item (CommonMark),
            // so the list stays open: the same rule as the live view (MarkdownLivePreview.ContinuationList)
            // and the print path (Markdown.NestLists). Items deeper than the one it continues are closed.
            int keep=ContinuationLevel(levels,line);
            if(keep>=0){if(levels.Count>keep+1)levels.RemoveRange(keep+1,levels.Count-keep-1);continue;}
            levels.Clear();
        }
    }
    // A quote's lines (the text after each ">") as blocks. A quote whose first line is an alert marker prints its
    // title line (the text after the marker, else the default title) and then the body, the way the print
    // parser lays it out. Shared by a quote of its own and a quote that is a list item's body.
    static void RenderQuote(string[] quoteLines,References references,StringBuilder result,string imageLabel){
        int alertEnd,alertTitle;string alertWord;
        AlertKind alert=AlertMarker(quoteLines[0],0,out alertEnd,out alertTitle,out alertWord);
        if(alert!=AlertKind.None){
            if(alertTitle<quoteLines[0].Length)AppendInline(result,quoteLines[0].Substring(alertTitle).TrimEnd(' ','\t'),references,imageLabel);
            else result.Append(AlertDefaultTitle(alert,alertWord));
            result.Append('\n');
            var rest=new string[quoteLines.Length-1];
            Array.Copy(quoteLines,1,rest,0,rest.Length);
            Render(rest,references,result,imageLabel);
        }else Render(quoteLines,references,result,imageLabel);
    }
    // A setext heading ("b" over "===") whose text line reaches an open item's content column sits inside that item, and the list stays
    // open (CommonMark; the live view's ContinuationList keeps it open on both lines). keep: the deepest item the text line reaches;
    // close: the deepest item (at or above keep) the underline line reaches - the items below it close. False (the list ends, as it did
    // before) when the text line is a thematic break, reaches no item, or the underline reaches none.
    internal static bool SetextInItem(string line,string underline,int[] contents,out int keep,out int close){
        keep=-1;close=-1;
        if(HorizontalRule(line))return false;
        int first=IndentColumns(line),second=IndentColumns(underline);
        for(int k=contents.Length-1;k>=0&&keep<0;k--)if(first>=contents[k])keep=k;
        if(keep<0)return false;
        for(int k=keep;k>=0&&close<0;k--)if(second>=contents[k])close=k;
        return close>=0;
    }
    internal static bool HorizontalRuleLine(string line){return HorizontalRule(line);}
    static int IndentColumns(string line){
        int column=0;
        for(int at=0;at<line.Length&&Space(line[at]);at++)column+=line[at]=='\t'?4:1;
        return column;
    }
    static int[] ContentColumns(List<ListLevel> levels){
        var contents=new int[levels.Count];
        for(int k=0;k<contents.Length;k++)contents[k]=levels[k].Content;
        return contents;
    }
    // The deepest open item (index into levels) a quote line is indented into (ItemQuoteLine), -1 when none.
    static int ItemQuoteLevel(List<ListLevel> levels,string line){
        for(int k=levels.Count-1;k>=0;k--)if(ItemQuoteLine(line,levels[k].Content))return k;
        return -1;
    }
    // The deepest open item (index into levels) whose content column the line's indentation reaches, -1 when none.
    // Called only for a line that is plain paragraph text: every block shape was taken above.
    static int ContinuationLevel(List<ListLevel> levels,string line){
        int column=0;
        for(int at=0;at<line.Length&&Space(line[at]);at++)column+=line[at]=='\t'?4:1;
        for(int k=levels.Count-1;k>=0;k--)if(column>=levels[k].Content)return k;
        return -1;
    }

    static void AppendRow(StringBuilder result,string[] cells,References references,string imageLabel){
        for(int c=0;c<cells.Length;c++){
            if(c>0)result.Append('\t');
            var content=new StringBuilder();
            AppendInline(content,cells[c].Trim(),references,imageLabel);
            int length=content.Length;
            while(length>0&&content[length-1]=='\n')length--;
            for(int i=0;i<length;i++)result.Append(content[i]=='\n'?' ':content[i]);
        }
        result.Append('\n');
    }

    static void AppendInline(StringBuilder result,string value,References references,string imageLabel){
        // #4: the same CommonMark backslash-escape reading as Markdown.Inline (MDNote.cs), so this
        // converter and the print oracle stay identical for the escapes the rich paste writes.
        Inline(result,ExpandReferences(Markdown.ProtectEscapes(value),references),references,imageLabel,0);
    }
    static void Inline(StringBuilder result,string value,References references,string imageLabel,int depth){
        if(depth>MaxInlineDepth){result.Append(Markdown.RestoreEscapes(value));return;} // Markdown.Inline adds a raw Run here
        // A backslash at a line's end is a hard line break: the character is markup and drops (the
        // block renderer already ends this line, and a multi-line footnote keeps its own newline).
        // A "\\" pair arrives as a sentinel, so it still restores to one "\".
        if(depth==0&&value.IndexOf('\\')>=0){
            if(value[value.Length-1]=='\\')value=value.Substring(0,value.Length-1);
            if(value.IndexOf("\\\n",StringComparison.Ordinal)>=0)value=value.Replace("\\\n","\n");
        }
        int at=0;int p=0;
        Dictionary<int,InlineHtml.Tag> html=InlineHtml.Parse(value);
        while(p<value.Length){
            InlineHtml.Tag tag;
            if(value[p]=='<'&&html.TryGetValue(p,out tag)){
                // The pair must end past its own start (and its content must not run backwards);
                // a broken pair would keep p where it is forever, so step over the '<'.
                if(tag.CloseEnd<=p||tag.CloseStart<tag.End){p++;continue;}
                if(p>at)result.Append(Markdown.RestoreEscapes(DecodeEntities(value.Substring(at,p-at))));
                InlineHtml.Tag image=tag.Name=="picture"?InlineHtml.PictureImage(html,tag):tag.Name=="img"?tag:null;
                if(image!=null)result.Append(Markdown.RestoreEscapes(InlineHtml.Attribute(value,image,"alt")));
                else if(tag.Name=="br")result.Append('\n');
                else if(tag.Name=="wbr"){}
                else if(InlineHtml.Code(tag.Name))result.Append(Markdown.RestoreEscapes(DecodeEntities(value.Substring(tag.End,tag.CloseStart-tag.End))));
                else Inline(result,value.Substring(tag.End,tag.CloseStart-tag.End),references,imageLabel,depth+1);
                at=tag.CloseEnd;p=at;continue;
            }
            if(value[p]=='<'){
                InlineHtml.Tag literal=InlineHtml.Read(value,p);
                if(literal!=null){p=literal.End>p?literal.End:p+1;continue;}
            }
            int kind;int end;int contentStart;int contentEnd;
            if(!Token(value,p,out kind,out end,out contentStart,out contentEnd)||end<=p){p++;continue;}
            if(p>at)result.Append(Markdown.RestoreEscapes(DecodeEntities(value.Substring(at,p-at))));
            if(kind==1||kind==2){
                string htmlTag=kind==1?"span":"font";
                int closeStart;int closeEnd;
                if(BalancedTag(value,contentStart,htmlTag,out closeStart,out closeEnd)){contentEnd=closeStart;end=closeEnd;}
                Inline(result,value.Substring(contentStart,contentEnd-contentStart),references,imageLabel,depth+1);
            }else if(kind==3){
                result.Append(Markdown.RestoreCodeEscapes(Markdown.UnpadCode(value.Substring(contentStart,contentEnd-contentStart)))); // no HTML decode for inline code
            }else if(kind==4){
                // Images fall back to the "[이미지: alt]" Run; the probe parses with an empty file path.
                result.Append(imageLabel).Append(Markdown.RestoreEscapes(value.Substring(contentStart,contentEnd-contentStart))).Append(']');
            }else if(kind==11){
                result.Append('\n'); // <br>
            }else if(kind==12){
                // An angle autolink's address is its text; the brackets and the target are markup.
                result.Append(Markdown.RestoreCodeEscapes(value.Substring(contentStart,contentEnd-contentStart)));
            }else if(kind==13){
                // A same-line HTML comment is markup and emits nothing.
            }else{
                Inline(result,value.Substring(contentStart,contentEnd-contentStart),references,imageLabel,depth+1);
            }
            if(end<=p){p++;continue;}   // a rewritten end must never move the cursor backwards
            at=end;p=end;
        }
        if(at<value.Length)result.Append(Markdown.RestoreEscapes(DecodeEntities(value.Substring(at))));
    }

    // One match of Markdown.token at p, honouring the alternation order. The link and image
    // targets end at TargetEnd, not at the first ")", so a balanced path keeps its ")" and a
    // quoted title cannot leave its own ")" behind as text.
    static bool Token(string value,int p,out int kind,out int end,out int contentStart,out int contentEnd){
        kind=0;end=0;contentStart=0;contentEnd=0;
        char c=value[p];
        if(c=='<'){
            int openEnd;int closeStart;int closeEnd;
            if(TagOpen(value,p,"span",out openEnd,out closeStart,out closeEnd)){kind=1;contentStart=openEnd;contentEnd=closeStart;end=closeEnd;return true;}
            if(TagOpen(value,p,"font",out openEnd,out closeStart,out closeEnd)){kind=2;contentStart=openEnd;contentEnd=closeStart;end=closeEnd;return true;}
            if(BrTag(value,p,out end)){kind=11;return true;}
            // Same-line HTML comment: markup, so nothing of it is text.
            int comment=CommentEnd(value,p);
            if(comment>p){kind=13;end=comment;return true;}
            // Angle autolink: the brackets hide and the address itself is the text.
            int autolink=AutolinkEnd(value,p);
            if(autolink>p){kind=12;contentStart=p+1;contentEnd=autolink-1;end=autolink;return true;}
            return false;
        }
        if(c=='`'){
            if(p>0&&value[p-1]=='`')return false; // (?<!`)
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
            int url=TargetEnd(value,close+2);
            if(url<=close+2)return false;
            kind=4;contentStart=p+2;contentEnd=close;end=url+1;
            return true;
        }
        if(c=='['){
            int close=value.IndexOf(']',p+1);
            if(close<=p+1||close+1>=value.Length||value[close+1]!='(')return false;
            int url=TargetEnd(value,close+2);
            if(url<=close+2)return false;
            kind=5;contentStart=p+1;contentEnd=close;end=url+1;
            return true;
        }
        if(c=='~'){
            if(p+1<value.Length&&value[p+1]=='~'&&BoldClose(value,p,'~',out end,out contentEnd)){kind=8;contentStart=p+2;return true;}
            return false;
        }
        if(c=='='){
            if(p+1<value.Length&&value[p+1]=='='&&HighlightClose(value,p,out end,out contentEnd)){kind=14;contentStart=p+2;return true;}
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
            if(p>0&&Word(value[p-1]))return false; // (?<!\w)
            int stop=p+1;
            while(stop<value.Length&&value[stop]!='_'&&value[stop]!='\n')stop++;
            if(stop<=p+1||stop>=value.Length||value[stop]!='_')return false;
            if(stop+1<value.Length&&Word(value[stop+1]))return false; // (?!\w)
            kind=10;contentStart=p+1;contentEnd=stop;end=stop+1;
            return true;
        }
        return false;
    }
    // The ")" that closes a link or image target opening at [start] (the "(" index + 1), the same
    // scan MarkdownLivePreview.ImageEnd runs: a backslash escape, an <angle> destination, balanced
    // parentheses in the path and a quoted title (which may itself hold ")"). In
    // "https://en.wikipedia.org/wiki/A_(b))" the first ")" is inside the path, not the link end.
    internal static int TargetEnd(string value,int start){
        int depth=0;bool angle=false;char quote='\0';
        for(int p=start;p<value.Length;p++){
            char c=value[p];
            if(c=='\\'){p++;continue;}
            if(quote!='\0'){if(c==quote)quote='\0';continue;}
            if(c=='<')angle=true;
            else if(c=='>')angle=false;
            else if(!angle&&(c=='"'||c=='\'')&&p>start&&Char.IsWhiteSpace(value[p-1]))quote=c;
            else if(!angle&&c=='(')depth++;
            else if(!angle&&c==')'){if(depth==0)return p;depth--;}
        }
        return -1;
    }
    // The end of an HTML comment that opens at start ("<!--") and closes before the line ends, the
    // live view's same-line rule (MarkdownLivePreview.ParseInline). -1 when it does not close on
    // that line: a comment spanning lines stays literal there, so it stays literal here too.
    internal static int CommentEnd(string value,int start){
        if(start+4>value.Length||String.CompareOrdinal(value,start,"<!--",0,4)!=0)return -1;
        int close=value.IndexOf("-->",start+4,StringComparison.Ordinal);
        if(close<0)return -1;
        int line=value.IndexOf('\n',start+4);
        return line>=0&&line<close?-1:close+3;
    }
    // A CommonMark angle autolink starting at "<": a scheme target ("<https://…>", "<mailto:…>")
    // or an email ("<a@b.c>") closing on the same line. Returns the offset after the ">", or -1;
    // the same bounded scan MarkdownLivePreview.AutolinkEnd runs.
    internal static int AutolinkEnd(string value,int start){
        int p=start+1,limit=Math.Min(value.Length,start+2048);
        while(p<limit&&value[p]!='>'){
            char c=value[p];
            if(c==' '||c=='\t'||c=='<')return -1;
            p++;
        }
        if(p>=limit||value[p]!='>')return -1;
        string target=value.Substring(start+1,p-start-1);
        return IsAutolinkTarget(target)?p+1:-1;
    }
    // The CommonMark shape: a 2-32 character URI scheme followed by ":" and something, or a plain
    // email address. Anything else is an ordinary raw tag and stays literal.
    static bool IsAutolinkTarget(string value){
        int colon=value.IndexOf(':');
        if(colon>0){
            if(colon<2||colon>32||colon+1>=value.Length)return false;
            for(int k=0;k<colon;k++){
                char c=value[k];
                bool alpha=c>='a'&&c<='z'||c>='A'&&c<='Z';
                if(k==0 ? !alpha : !(alpha||c>='0'&&c<='9'||c=='+'||c=='.'||c=='-'))return false;
            }
            return true;
        }
        int at=value.IndexOf('@');
        if(at<=0||at!=value.LastIndexOf('@')||at+1>=value.Length)return false;
        return value.IndexOf('.',at+1)>at+1;
    }
    // "c c" closing pair for **, __ and ~~ : .+? never crosses a newline and needs one content char.
    static bool BoldClose(string value,int p,char c,out int end,out int contentEnd){
        end=0;contentEnd=0;
        if(p+2>=value.Length||value[p+2]=='\n')return false;
        for(int scan=p+3;scan+1<value.Length;scan++){
            if(value[scan]=='\n')return false;
            if(value[scan]==c&&value[scan+1]==c){contentEnd=scan;end=scan+2;return true;}
        }
        return false;
    }
    // ==text== highlight: the print parser's "==(?=\S)(.+?)(?<=\S)==" lookarounds, so the opener
    // must be followed and the closer preceded by a non-space character ("== x ==" stays literal)
    // and the pair never crosses a newline. "==" with no content char after the opener stays
    // literal too, like "===".
    static bool HighlightClose(string value,int p,out int end,out int contentEnd){
        end=0;contentEnd=0;
        if(p+2>=value.Length||Char.IsWhiteSpace(value[p+2]))return false;
        for(int scan=p+3;scan+1<value.Length;scan++){
            if(value[scan]=='\n')return false;
            if(value[scan]=='='&&value[scan+1]=='='&&!Char.IsWhiteSpace(value[scan-1])){contentEnd=scan;end=scan+2;return true;}
        }
        return false;
    }

    // (<tag\s+[^>]*>)([\s\S]*?)</tag> ; returns the first closing tag as the initial content end.
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
    // The nesting scan Markdown.Inline runs with "</?tag\b[^>]*>".
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
    // CommonMark decodes HTML character references. WebUtility.HtmlDecode covers the HTML 4 names
    // (&copy;, &mdash;, &hellip;, &rarr;, &middot;, &times;, &deg;, ...) and numeric references;
    // the HTML5-only "&apos;" that MarkdownLivePreview.DecodeEntity already decoded is added here,
    // so every renderer can read one table instead of a per-file switch.
    internal static string DecodeEntities(string value){
        if(value.IndexOf('&')<0)return value;
        var result=new StringBuilder(value.Length);
        int at=0;
        for(int p=0;p<value.Length;p++){
            if(value[p]!='&')continue;
            int semicolon=value.IndexOf(';',p+1,Math.Min(10,value.Length-p-1));
            if(semicolon<=p+1)continue;
            string decoded=DecodeEntity(value.Substring(p,semicolon+1-p));
            if(decoded==null)continue;
            result.Append(value,at,p-at).Append(decoded);
            at=semicolon+1;p=semicolon;
        }
        if(at==0)return value;
        if(at<value.Length)result.Append(value,at,value.Length-at);
        return result.ToString();
    }
    // One "&name;" or "&#123;" / "&#x7B;" reference; null when it is not one this app decodes.
    internal static string DecodeEntity(string entity){
        if(entity.Length<4||entity[0]!='&'||entity[entity.Length-1]!=';')return null;
        if(entity[1]=='#'){
            bool hex=entity[2]=='x'||entity[2]=='X';
            int value;
            if(!Int32.TryParse(entity.Substring(hex?3:2,entity.Length-(hex?4:3)),hex?NumberStyles.HexNumber:NumberStyles.None,CultureInfo.InvariantCulture,out value))return null;
            if(value<=0||value>0x10FFFF||(value>=0xD800&&value<=0xDFFF))return null;
            return Char.ConvertFromUtf32(value);
        }
        if(String.Equals(entity,"&apos;",StringComparison.Ordinal))return "'";
        string decoded=WebUtility.HtmlDecode(entity);
        return decoded==entity?null:decoded;
    }

    /* `:name:` emoji shortcodes for the rendered views (live/Basic and print). The flank rule keeps
       a shortcode from matching inside a word, a URL path or another shortcode: the opening colon
       may not follow a shortcode-name character, ":" or "/", and the closing colon may not be
       followed by one, so "a:b:c" stays literal. A run holding "://" before the opening colon is
       a URL, not a shortcode host (a query string in prose stays text). Names come from
       EmojiShortcodes (GitHub's set); the TXT converter never calls this, so MD->TXT keeps
       ":name:" exactly as typed. */
    internal static bool EmojiAt(string text,int start,out int end){
        end=start;
        if(text==null||start<0||start>=text.Length||text[start]!=':')return false;
        if(start>0&&!EmojiFlank(text[start-1]))return false;
        int p=start+1,nameStart=p;
        while(p<text.Length&&p-nameStart<64&&EmojiShortcodes.IsNameChar(text[p]))p++;
        if(p==nameStart||p>=text.Length||text[p]!=':')return false;
        if(p+1<text.Length&&!EmojiFlank(text[p+1]))return false;
        if(EmojiShortcodes.Lookup(text.Substring(nameStart,p-nameStart))==null)return false;
        int run=start-1;
        while(run>=0&&start-run<256&&!Char.IsWhiteSpace(text[run]))run--;
        if(run<start-1&&text.IndexOf("://",run+1,start-run-1,StringComparison.Ordinal)>=0)return false;
        end=p+1;
        return true;
    }
    static bool EmojiFlank(char c){
        return !(EmojiShortcodes.IsNameChar(c)||c==':'||c=='/');
    }
    // The same rule over a whole string. The input instance comes back when nothing matched.
    internal static string EmojiText(string value){
        if(value==null||value.IndexOf(':')<0)return value;
        StringBuilder result=null;int at=0;
        for(int p=0;p<value.Length;){
            int end;
            if(!EmojiAt(value,p,out end)||end<=p){p++;continue;}
            if(result==null)result=new StringBuilder(value.Length+8);
            if(p>at)result.Append(value,at,p-at);
            result.Append(EmojiShortcodes.Lookup(value.Substring(p+1,end-p-2)));
            at=end;p=end;
        }
        if(result==null)return value;
        if(at<value.Length)result.Append(value,at,value.Length-at);
        return result.ToString();
    }

    // Markdown.ExpandReferences: protects `code`, expands [label][id] and [label] against the definitions.
    static string ExpandReferences(string value,References references){
        if(value.Length==0)return value;
        var result=new StringBuilder(value.Length+16);
        int at=0;int p=0;
        while(p<value.Length){
            int end;int kind;bool bang;string label;string id;
            if(!ReferenceMatch(value,p,out end,out kind,out bang,out label,out id)){p++;continue;}
            if(end<=p){p++;continue;}   // a match that does not move the cursor stays literal text
            if(p>at)result.Append(value,at,p-at);
            if(kind==1){
                result.Append(value,p,end-p);
            }else if(id.Length>0&&id[0]=='^'&&references.Notes.ContainsKey(id.Substring(1))){
                // The footnote label is replaced by its number, even for [text][^id].
                result.Append('[').Append(references.Number(id.Substring(1))).Append("](#mdnote-footnote-").Append(Uri.EscapeDataString(id.Substring(1))).Append(')');
            }else{
                string address;
                if(references.Links.TryGetValue(id,out address)){
                    if(bang)result.Append('!');
                    result.Append('[').Append(label).Append("](").Append(address).Append(')');
                }else result.Append(value,p,end-p);
            }
            at=end;p=end;
        }
        if(at<value.Length)result.Append(value,at,value.Length-at);
        return result.ToString();
    }
    // `+[^`]*`+ | !?\[([^\]]+)\]\[([^\]]*)\] | \[([^\]]+)\](?!\()
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
                if(id.Length==0)id=label; // [text][] reuses the label as the id
                return true;
            }
        }
        if(hasBang)return false; // the shortcut alternative needs a '[' at p
        if(labelEnd>=p+2&&(labelEnd+1>=value.Length||value[labelEnd+1]!='(')){
            kind=3;end=labelEnd+1;
            label=value.Substring(p+1,labelEnd-p-1);
            id=label;
            return true;
        }
        return false;
    }

    // GitHub alert / Obsidian callout marker at the first content character of a quote's first line:
    // "[!TYPE]" (up to three spaces before it), an optional Obsidian fold sign "+" / "-", then the end
    // of the line or a space/tab and the optional title text. start is where the line's own content
    // begins (after the quote markers). markerEnd is just after the "]" and the fold sign, titleStart
    // the first character of the title (text.Length when there is none; the spaces between are part of
    // the hidden marker). typeWord is the word as written. O(marker length): nothing else is read.
    // The editor, the print parser, the HTML export and this converter all read this one scan.
    internal static AlertKind AlertMarker(string text,int start,out int markerEnd,out int titleStart,out string typeWord){
        markerEnd=start;titleStart=start;typeWord="";
        if(text==null||start<0||start>=text.Length)return AlertKind.None;
        int p=start,columns=0;
        while(p<text.Length&&Space(text[p])){columns+=text[p]=='\t'?4:1;if(columns>3)return AlertKind.None;p++;}
        if(p+3>=text.Length||text[p]!='['||text[p+1]!='!')return AlertKind.None;
        int word=p+2,q=word;
        while(q<text.Length&&q-word<32&&AlertWordChar(text[q]))q++;
        if(q==word||q>=text.Length||text[q]!=']')return AlertKind.None;
        int end=q+1;
        if(end<text.Length&&(text[end]=='+'||text[end]=='-'))end++;
        if(end<text.Length&&!Space(text[end]))return AlertKind.None;
        int title=end;
        while(title<text.Length&&Space(text[title]))title++;
        markerEnd=end;titleStart=title;typeWord=text.Substring(word,q-word);
        return AlertKindOf(typeWord);
    }
    static bool AlertWordChar(char c){return c>='a'&&c<='z'||c>='A'&&c<='Z'||c>='0'&&c<='9'||c=='-'||c=='_';}
    // The five GitHub types keep their own colour; the Obsidian names map to the nearest one and
    // anything else is a note.
    internal static AlertKind AlertKindOf(string word){
        switch((word??"").ToLowerInvariant()){
            case "tip":case "success":case "check":case "done":case "hint":return AlertKind.Tip;
            case "important":case "question":case "help":case "faq":case "example":case "quote":case "cite":return AlertKind.Important;
            case "warning":case "attention":case "failure":case "fail":case "missing":case "bug":return AlertKind.Warning;
            case "caution":case "danger":case "error":return AlertKind.Caution;
            default:return AlertKind.Note;
        }
    }
    // The title line when the marker carries no text of its own: English in every UI language. The
    // five GitHub names are fixed; an alias or unknown word is shown with its first letter capitalised.
    internal static string AlertDefaultTitle(AlertKind kind,string typeWord){
        string word=(typeWord??"").ToLowerInvariant();
        switch(word){
            case "note":return "Note";
            case "tip":return "Tip";
            case "important":return "Important";
            case "warning":return "Warning";
            case "caution":return "Caution";
        }
        if(word.Length>0)return Char.ToUpperInvariant(word[0])+word.Substring(1);
        switch(kind){
            case AlertKind.Tip:return "Tip";
            case AlertKind.Important:return "Important";
            case AlertKind.Warning:return "Warning";
            case AlertKind.Caution:return "Caution";
            default:return "Note";
        }
    }

    // Markdown.ScanReferences: definitions are collected outside fenced code only.
    static void ScanReferences(string[] lines,References references,int frontMatter=0){
        char marker='\0';int length=0;
        for(int i=0;i<lines.Length;i++){
            if(i<frontMatter)continue;
            string line=lines[i];
            char fenceMarker;int fenceCount;int fenceInfo;
            if(marker!='\0'){
                if(Fence(line,out fenceMarker,out fenceCount,out fenceInfo)&&fenceMarker==marker&&fenceCount>=length&&WhiteOnly(line,fenceInfo))marker='\0';
                continue;
            }
            if(FenceOpen(line,out fenceMarker,out fenceCount,out fenceInfo)){marker=fenceMarker;length=fenceCount;continue;}
            string id,body;bool note;
            if(TryDefinition(line,out id,out body,out note)){
                if(note){if(!references.Notes.ContainsKey(id))references.Notes[id]=references.Notes.Count+1;}
                else references.Links[id]=body;
            }
        }
    }
}

// The five GitHub alert types (a quote whose first line is "[!NOTE]" and so on).
internal enum AlertKind { None, Note, Tip, Important, Warning, Caution }

// Colours and icon outlines shared by the editor, the print view and the HTML export. The icons are
// path data in a 16 x 16 box (the SVG / WPF path mini-language): Stroke is drawn as a 1.5 wide round
// outline, Dots as filled discs.
internal static class AlertArt {
    internal static string Css(AlertKind kind){
        switch(kind){
            case AlertKind.Tip:return "tip";
            case AlertKind.Important:return "important";
            case AlertKind.Warning:return "warning";
            case AlertKind.Caution:return "caution";
            default:return "note";
        }
    }
    // "#rrggbb" of the type's colour on a light or dark page.
    internal static string Hex(AlertKind kind,bool dark){
        switch(kind){
            case AlertKind.Tip:return dark?"#3fb950":"#1a7f37";
            case AlertKind.Important:return dark?"#ab7df8":"#8250df";
            case AlertKind.Warning:return dark?"#d29922":"#9a6700";
            case AlertKind.Caution:return dark?"#f85149":"#d1242f";
            default:return dark?"#4493f8":"#0969da";
        }
    }
    internal static string Stroke(AlertKind kind){
        switch(kind){
            case AlertKind.Tip:   // light bulb
                return "M5.6,10.8 L5.6,10 A4.7,4.7 0 1 1 10.4,10 L10.4,10.8 Z M6.4,12.8 L9.6,12.8 M7.1,14.7 L8.9,14.7";
            case AlertKind.Important:   // speech bubble with "!"
                return "M2.5,1.75 L13.5,1.75 A1.25,1.25 0 0 1 14.75,3 L14.75,9.75 A1.25,1.25 0 0 1 13.5,11 L9,11 L5.5,14.25 L5.5,11 L2.5,11 A1.25,1.25 0 0 1 1.25,9.75 L1.25,3 A1.25,1.25 0 0 1 2.5,1.75 Z M8,4.2 L8,7";
            case AlertKind.Warning:   // triangle with "!"
                return "M8,1.9 L14.9,13.9 L1.1,13.9 Z M8,6.2 L8,9.6";
            case AlertKind.Caution:   // octagon with "!"
                return "M5.2,1.4 L10.8,1.4 L14.6,5.2 L14.6,10.8 L10.8,14.6 L5.2,14.6 L1.4,10.8 L1.4,5.2 Z M8,4.6 L8,8.6";
            default:   // info circle
                return "M1.25,8 A6.75,6.75 0 1 0 14.75,8 A6.75,6.75 0 1 0 1.25,8 Z M8,7.2 L8,11.4";
        }
    }
    internal static string Dots(AlertKind kind){
        switch(kind){
            case AlertKind.Tip:return "";
            case AlertKind.Important:return Dot(8,8.8);
            case AlertKind.Warning:return Dot(8,11.7);
            case AlertKind.Caution:return Dot(8,11);
            default:return Dot(8,4.7);
        }
    }
    static string Dot(double x,double y){
        const double r=0.95;
        var c=CultureInfo.InvariantCulture;
        return "M"+(x-r).ToString(c)+","+y.ToString(c)+" A"+r.ToString(c)+","+r.ToString(c)+" 0 1 0 "+(x+r).ToString(c)+","+y.ToString(c)+" A"+r.ToString(c)+","+r.ToString(c)+" 0 1 0 "+(x-r).ToString(c)+","+y.ToString(c)+" Z";
    }
}
}
