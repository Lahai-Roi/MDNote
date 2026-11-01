using System;using System.IO;using System.Text;using System.Collections.Generic;using System.Text.RegularExpressions;using System.Windows;using System.Windows.Controls;using System.Windows.Documents;
namespace MDNote {public partial class MainWindow {
static string NormalizeRtfUnicode(string value){var result=new StringBuilder();foreach(char character in value){if(character>127)result.Append("\\u").Append(unchecked((short)character)).Append('?');else result.Append(character);}return result.ToString();}
// The clipboard read at the key press can be refused: another program holding the clipboard open
// answers CLIPBRD_E_CANT_OPEN for a few milliseconds. The read waits it out (ClipboardRetry), and
// a refusal that outlasts the retries is named in the status line instead of the key seeming to
// do nothing. False means nothing was pasted.
bool PasteClipboard(bool plain){
    if(!editing)return true;
    IDataObject data;
    if(!ClipboardRetry.TryGetDataObject(out data))return ShowClipboardBusy();
    /* The insertion path may still raise a clipboard exception from the captured object; as
       before, it must not escape the key handler. */
    try{return PasteData(data,plain);}
    catch(System.Runtime.InteropServices.ExternalException){return false;}
}
// The answer to a clipboard that stayed locked: nothing is inserted, as before, but the status
// line says why the key did nothing.
bool ShowClipboardBusy(){
    ShowStatusNotice(T("클립보드를 열 수 없습니다. 잠시 후 다시 붙여넣으세요."));
    return false;
}
// A paste is the one command whose input lives outside the document: the formats are read once,
// at the key press, into a snapshot, and only the insertion is queued through RunRichCommand
// (StructuralInput.cs). Re-reading the OLE object after the composition release could read a
// clipboard the user has already replaced, and inserting under the composition is the document
// write that kills the process. The snapshot is also what makes the paste show the content that
// was on the clipboard when the key was pressed.
sealed class PasteSnapshot {internal string Value,Markdown,Html,Rtf;internal bool HasHtml,HasRtf;}
// Every format is read in its own ClipboardRetry step: the clipboard lock is taken per call, and
// one refusal used to abort the rest of the read through a shared try/catch. A refused plain-text
// read leaves nothing to paste, so it answers null and the caller names the failure; a refused
// richer format only loses that format, which is what the swallowed exception did before.
PasteSnapshot CapturePaste(IDataObject data,bool plain){
    var snapshot=new PasteSnapshot();
    string value=null;
    if(!ClipboardRetry.Try(delegate{value=data.GetDataPresent(DataFormats.UnicodeText)?data.GetData(DataFormats.UnicodeText) as string:null;}))return null;
    snapshot.Value=value;
    /* 11-01: a plain paste needs MDNote's own source too, to write its visible text instead of the
       Markdown the copy put in the text format (LivePaste). */
    string markdown=null;
    if(ClipboardRetry.Try(delegate{markdown=MarkdownClipboard.Read(data);}))snapshot.Markdown=MarkdownClipboard.CanonicalNewlines(markdown);
    if(!plain){
        bool hasHtml=false;string html=null;
        if(ClipboardRetry.Try(delegate{hasHtml=data.GetDataPresent(DataFormats.Html);if(hasHtml)html=data.GetData(DataFormats.Html) as string;})&&hasHtml){
            if(!String.IsNullOrEmpty(html)&&html.Length<8*1024*1024){
                snapshot.Html=html;snapshot.HasHtml=true;
                /* A spreadsheet range is also on the clipboard as tab-separated text. When its
                   HTML converts to nothing (Excel's <col>-wrapped range used to), the same range
                   is written as a Markdown table here instead of one long line of text; the
                   HTML conversion itself now answers null, so every other failure falls through
                   to the RTF and then the plain text rather than inserting nothing. A code line
                   keeps the copied text verbatim (P6). */
                if(snapshot.Markdown==null&&PasteSpreadsheetRange(data)&&!PasteOnCodeLine()&&!PasteHtmlConvertible(html))
                    snapshot.Markdown=MarkdownClipboard.CanonicalNewlines(PasteTextTable(snapshot.Value));
            }
        }
        bool hasRtf=false;string rtf=null;
        if(ClipboardRetry.Try(delegate{hasRtf=data.GetDataPresent(DataFormats.Rtf);if(hasRtf)rtf=data.GetData(DataFormats.Rtf) as string;})&&hasRtf){
            if(!String.IsNullOrEmpty(rtf)&&rtf.Length<8*1024*1024){snapshot.Rtf=rtf;snapshot.HasRtf=true;}
        }
    }
    return snapshot;
}
// LivePaste reads the formats itself, so the captured content is mirrored into a fresh DataObject:
// the queued run must never depend on the OLE object the clipboard handed out at key time.
static IDataObject CapturedPasteData(PasteSnapshot snapshot,bool plain){
    var data=new DataObject();
    if(snapshot.Value!=null)data.SetData(DataFormats.UnicodeText,snapshot.Value);
    if(snapshot.Markdown!=null)MarkdownClipboard.Set(data,snapshot.Markdown);
    if(!plain&&snapshot.HasHtml)data.SetData(DataFormats.Html,snapshot.Html);
    if(!plain&&snapshot.HasRtf)data.SetData(DataFormats.Rtf,snapshot.Rtf);
    return data;
}
// A spreadsheet copy carries these beside the HTML; a Word document or a web page does not, so the
// extra conversion the table fallback needs stays off the ordinary paste path.
static bool PasteSpreadsheetRange(IDataObject data){
    bool spreadsheet=false;
    bool read=ClipboardRetry.Try(delegate{spreadsheet=data.GetDataPresent("Csv")||data.GetDataPresent("Biff8")||data.GetDataPresent("Biff12")||data.GetDataPresent("Biff5")
        ||data.GetDataPresent("XML Spreadsheet")||data.GetDataPresent("DataInterchangeFormat");});
    return read&&spreadsheet;
}
// The HTML path's own converter decides whether the HTML is usable at all: when it cannot turn the
// copy into Markdown, the spreadsheet text answers instead of a silent empty paste.
static bool PasteHtmlConvertible(string html){
    try{return !String.IsNullOrEmpty(ClipboardMarkdown(html));}
    catch(Exception){return false;}
}
// A fenced block and an inline code span take only the copied text (the P6 rule), so the table a
// capture would add for a spreadsheet range must not replace it there.
bool PasteOnCodeLine(){
    try{
        if(!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return false;
        int offset=source.SelectionStart;
        return livePreview.InCode(offset)||LiveInlineCodeAt(offset);
    }catch(Exception){return false;}   /* a paste must never fail on this probe */
}
// A spreadsheet range is also on the clipboard as tab-separated text. When its HTML converts to
// nothing, a clean rectangle of tab-separated fields becomes the same Markdown table instead of
// one long line of text. Excel quotes a field that holds a tab, a quote or a line break, so the
// quoted form is read the way Excel writes it (one cell holding a line break becomes "a<br>b").
static string PasteTextTable(string value){
    if(String.IsNullOrEmpty(value))return null;
    var rows=new List<List<string>>();
    var row=new List<string>();var field=new StringBuilder();bool quoted=false;
    for(int i=0;i<value.Length;i++){
        char c=value[i];
        if(quoted){
            if(c!='"'){field.Append(c);continue;}
            if(i+1<value.Length&&value[i+1]=='"'){field.Append('"');i++;continue;}
            quoted=false;continue;
        }
        if(c=='"'){if(field.Length==0){quoted=true;continue;}field.Append(c);continue;}
        if(c=='\t'){row.Add(field.ToString());field.Length=0;continue;}
        if(c=='\r'||c=='\n'){
            if(c=='\r'&&i+1<value.Length&&value[i+1]=='\n')i++;
            row.Add(field.ToString());field.Length=0;rows.Add(row);row=new List<string>();continue;
        }
        field.Append(c);
    }
    row.Add(field.ToString());rows.Add(row);
    while(rows.Count>0&&rows[rows.Count-1].Count==1&&rows[rows.Count-1][0].Length==0)rows.RemoveAt(rows.Count-1);
    if(rows.Count<2||rows.Count>1000)return null;
    int columns=rows[0].Count;
    if(columns<2||columns>64)return null;
    foreach(var line in rows)if(line.Count!=columns)return null;
    var lines=new List<string>();
    for(int r=0;r<rows.Count;r++){
        var cells=new List<string>();
        for(int c=0;c<columns;c++)cells.Add(PasteTablePipes(rows[r][c].Replace("\r\n","\n").Replace('\r','\n').Replace("\n","<br>")));
        lines.Add("| "+String.Join(" | ",cells.ToArray())+" |");
        if(r==0){var separators=new string[columns];for(int c=0;c<columns;c++)separators[c]="---";lines.Add("| "+String.Join(" | ",separators)+" |");}
    }
    return String.Join("\n",lines.ToArray());
}
bool PasteData(IDataObject data,bool plain){
    if(data==null||!editing||documentOperationBusy)return true;
    PasteSnapshot snapshot=CapturePaste(data,plain);
    if(snapshot==null)return ShowClipboardBusy();
    PasteCaptured(snapshot,plain);
    return true;
}
void PasteCaptured(PasteSnapshot snapshot,bool plain){if(snapshot==null||!editing||documentOperationBusy)return;if(source!=null&&source.ImeBusy){source.RunAfterComposition(delegate{PasteCaptured(snapshot,plain);});return;}/* #7: paste after the pending syllable lands */PasteCapturedNow(snapshot,plain);}
// Y2: a rich paste that lands inside an emphasis run must not repeat that run's own delimiter.
// The text is already bold/italic/struck, and the nested pair closes the outer one early
// ("**a**bold** b**" leaves the markers as literal text). The caret's line is read with the live
// parser's pairing rules (token order, escapes, code spans, link labels and span/font tags
// skipped), once per paste, and only for a caret without a selection. At the inner edge of a run
// a different kind that would start (or end) with the same marker character is dropped too,
// because the two runs would merge into "***" and pair differently.
[ThreadStatic] static bool pasteEmphasisContext,pasteInsideBold,pasteInsideItalic,pasteInsideStrike,pasteAtOpenEdge,pasteAtCloseEdge;
[ThreadStatic] static char pasteEdgeMarker;
internal static bool PasteBoldSuppressed{get{return pasteEmphasisContext&&(pasteInsideBold||((pasteAtOpenEdge||pasteAtCloseEdge)&&pasteEdgeMarker=='*'));}}
internal static bool PasteItalicSuppressed{get{return pasteEmphasisContext&&(pasteInsideItalic||((pasteAtOpenEdge||pasteAtCloseEdge)&&pasteEdgeMarker=='*'));}}
internal static bool PasteStrikeSuppressed{get{return pasteEmphasisContext&&(pasteInsideStrike||((pasteAtOpenEdge||pasteAtCloseEdge)&&pasteEdgeMarker=='~'));}}
static void ArmPasteEmphasis(SourceEditor editor){
    pasteEmphasisContext=false;pasteInsideBold=false;pasteInsideItalic=false;pasteInsideStrike=false;pasteAtOpenEdge=false;pasteAtCloseEdge=false;pasteEdgeMarker='\0';
    if(editor==null||editor.Document==null||editor.SelectionLength!=0)return;
    int caret=editor.SelectionStart;
    if(caret<0||caret>editor.Document.TextLength)return;
    var line=editor.Document.GetLineByOffset(caret);
    string text=editor.Document.GetText(line.Offset,line.Length);
    pasteEmphasisContext=true;
    PasteScanEmphasis(text,caret-line.Offset);
}
// One pass over the caret's line with the live parser's token order and opener/closer rules.
// Escapes, code spans, link targets and span/font tags are skipped; the walk is bounded by the
// parser's own budget so a pathological line cannot make a paste quadratic.
static void PasteScanEmphasis(string text,int column){
    int[] opens={-1,-1,-1,-1,-1};
    string[] tokens={"**","__","*","_","~~"};
    int budget=text.Length*4+64;
    // A link label owns its own inline markup while the "](url)" / "][id]" tail is hidden, so a
    // marker inside a URL never pairs with the line (MarkdownLivePreview.ParseInline). The walk
    // saves the opening state, walks the label and jumps the tail whole.
    int linkResume=-1,linkSkip=-1;int[] linkOpens=null;
    for(int i=0;i<text.Length;){
        if(budget--<=0)return;
        if(linkResume>=0&&i>=linkResume){
            if(linkOpens!=null){Array.Copy(linkOpens,opens,opens.Length);linkOpens=null;}
            i=linkSkip;linkResume=-1;linkSkip=-1;continue;
        }
        char c=text[i];
        if(c=='\\'){i+=2;continue;}
        if(c=='`'){
            int fence=1;while(i+fence<text.Length&&text[i+fence]=='`')fence++;
            int close=PasteCodeClose(text,i+fence,fence,ref budget);
            if(close<0){if(budget<=0)return;i+=fence;continue;}
            // A span that closes past the label's "]" belongs to the hidden tail: the walk keeps
            // the backtick literal and lets the label end restore the opening state (ParseInline).
            if(linkResume>=0&&close>=linkResume){i+=fence;continue;}
            i=close+fence;continue;
        }
        if(c=='!'&&linkResume<0&&i+2<text.Length&&text[i+1]=='['){
            // An image is one object: its alt text and target are never markers to the parser.
            int labelEnd=PasteFind(text,"]",i+2,ref budget);
            if(labelEnd>=0&&labelEnd+1<text.Length&&text[labelEnd+1]=='('){
                int imageClose=PasteParenClose(text,labelEnd+2,ref budget);
                if(imageClose>=0){i=imageClose+1;continue;}
            }
        }
        if(c=='['&&linkResume<0){
            int note=-1;
            if(i+2<text.Length&&text[i+1]=='^')note=PasteFind(text,"]",i+2,ref budget);
            if(note>i+2&&(note+1>=text.Length||text[note+1]!=':')){i=note+1;continue;}
            if(i==0||text[i-1]!='!'){
                int labelEnd=PasteLabelEnd(text,i,ref budget);
                if(labelEnd>i+1){
                    if(labelEnd+1<text.Length&&text[labelEnd+1]=='['){
                        int idEnd=PasteFind(text,"]",labelEnd+2,ref budget);
                        if(idEnd>=0){linkOpens=(int[])opens.Clone();linkResume=labelEnd;linkSkip=idEnd+1;i++;continue;}
                    }
                    if(labelEnd+1<text.Length&&text[labelEnd+1]=='('){
                        int close=PasteFind(text,")",labelEnd+2,ref budget);
                        if(close>=0){linkOpens=(int[])opens.Clone();linkResume=labelEnd;linkSkip=close+1;i++;continue;}
                    }
                }
            }
        }
        if(c=='<'&&(PasteTagAt(text,i,"span")||PasteTagAt(text,i,"font"))){
            // An inline tag's attributes are hidden markup, not emphasis ("font-family:'A_B'").
            int from=i+5;int limit=Math.Min(512,text.Length-from);
            int gt=limit>0?text.IndexOf('>',from,limit):-1;
            if(gt>=0){i=gt+1;continue;}
        }
        int kind=-1;for(int t=0;t<tokens.Length;t++)if(PasteToken(text,i,tokens[t])){kind=t;break;}
        if(kind<0){i++;continue;}
        int length=tokens[kind].Length;
        // The same intraword "_" guard ParseInline uses (char.IsLetterOrDigit, not .NET \w).
        if(c=='_'&&i>0&&i+length<text.Length&&char.IsLetterOrDigit(text[i-1])&&char.IsLetterOrDigit(text[i+length])){i+=length;continue;}
        if(opens[kind]<0){if(i+length<text.Length&&!char.IsWhiteSpace(text[i+length]))opens[kind]=i;}
        else if(i>opens[kind]+length&&!char.IsWhiteSpace(text[i-1])){
            int open=opens[kind];
            if(open+length<=column&&column<=i){
                if(kind<2)pasteInsideBold=true;else if(kind<4)pasteInsideItalic=true;else pasteInsideStrike=true;
                if(column==open+length){pasteAtOpenEdge=true;pasteEdgeMarker=c;}
                if(column==i){pasteAtCloseEdge=true;pasteEdgeMarker=c;}
            }
            opens[kind]=-1;
        }
        i+=length;
    }
}
// MarkdownLivePreview.Find: the token at or after start, with "\x" walked as one unit.
static int PasteFind(string text,string token,int start,ref int budget){
    if(token.Length==0)return -1;
    for(int p=start;p+token.Length<=text.Length;){
        if(--budget<=0)return -1;
        if(text[p]=='\\'){p+=2;continue;}
        if(String.CompareOrdinal(text,p,token,0,token.Length)==0)return p;
        p++;
    }
    return -1;
}
// MarkdownLivePreview.LinkLabelEnd: the "]" closing the label at open, counted by bracket depth
// with escapes as one unit (a nested "[" still counts; a nested image balances its own brackets).
static int PasteLabelEnd(string text,int open,ref int budget){
    int depth=1;int p=open+1;
    while(p<text.Length&&budget>0){
        budget--;
        char c=text[p];
        if(c=='\\'){p+=Math.Min(2,text.Length-p);continue;}
        if(c=='['){depth++;p++;continue;}
        if(c==']'){depth--;if(depth==0)return p;}
        p++;
    }
    return -1;
}
// MarkdownLivePreview.ImageEnd: the ")" ending a target, skipping escapes, a quoted title and a
// nested "(...)" pair.
static int PasteParenClose(string text,int start,ref int budget){
    int depth=0;bool angle=false;char quote='\0';
    for(int p=start;p<text.Length&&budget>0;){
        budget--;
        char c=text[p];
        if(c=='\\'){p+=2;continue;}
        if(quote!='\0'){if(c==quote)quote='\0';p++;continue;}
        if(c=='<')angle=true;
        else if(c=='>')angle=false;
        else if(!angle&&(c=='"'||c=='\'')&&p>start&&char.IsWhiteSpace(text[p-1]))quote=c;
        else if(!angle&&c=='(')depth++;
        else if(!angle&&c==')'){if(depth==0)return p;depth--;}
        p++;
    }
    return -1;
}
// MarkdownLivePreview.MatchesTag: "<span " or "<font " (the tag name then a space or tab).
static bool PasteTagAt(string text,int at,string name){
    if(at<0||at+name.Length+1>=text.Length)return false;
    for(int k=0;k<name.Length;k++)if(char.ToLowerInvariant(text[at+1+k])!=name[k])return false;
    char next=text[at+1+name.Length];
    return next==' '||next=='\t';
}
static bool PasteToken(string text,int at,string token){if(at<0||at+token.Length>text.Length)return false;for(int k=0;k<token.Length;k++)if(text[at+k]!=token[k])return false;return true;}
static int PasteCodeClose(string text,int start,int fence,ref int budget){
    for(int p=start;p<text.Length;){
        if(--budget<=0)return -1;
        if(text[p]!='`'){p++;continue;}
        int run=1;while(p+run<text.Length&&text[p+run]=='`')run++;
        if(run==fence)return p;
        p+=run;
    }
    return -1;
}
void PasteCapturedNow(PasteSnapshot snapshot,bool plain){
    if(!editing||documentOperationBusy)return;
    bool hadContext=pasteEmphasisContext,hadBold=pasteInsideBold,hadItalic=pasteInsideItalic,hadStrike=pasteInsideStrike,hadOpen=pasteAtOpenEdge,hadClose=pasteAtCloseEdge;
    char hadMarker=pasteEdgeMarker;
    if(!plain&&!hadContext&&(snapshot.HasHtml||snapshot.HasRtf))ArmPasteEmphasis(source);
    try{
        if(LivePaste(CapturedPasteData(snapshot,plain),plain,snapshot.Value))return;
    }finally{
        pasteEmphasisContext=hadContext;pasteInsideBold=hadBold;pasteInsideItalic=hadItalic;pasteInsideStrike=hadStrike;pasteAtOpenEdge=hadOpen;pasteAtCloseEdge=hadClose;pasteEdgeMarker=hadMarker;
    }
    /* This is the TXT (plain) path: LivePaste refuses a plain document, so the clipboard string is
       inserted here as the whole paste. It lands on the document's own newline like every LivePaste
       path (#18): a Ctrl+V of a CRLF copy used to write CRLF into an LF .txt buffer and mix endings. */
    if(snapshot.Value!=null){string value=NormalizeLineEndings(snapshot.Value,documentNewline);int at=source.SelectionStart;source.SelectedText=value;source.Select(at+value.Length,0);}
}
static bool NeutralPasteColor(System.Windows.Media.Color color){return Math.Max(color.R,Math.Max(color.G,color.B))-Math.Min(color.R,Math.Min(color.G,color.B))<=12||color==System.Windows.Media.Color.FromRgb(38,50,66);}
static void PasteRuns(InlineCollection inlines,System.Collections.Generic.List<Run> result){foreach(Inline inline in inlines){if(inline is Run)result.Add((Run)inline);else if(inline is Span&&!(inline is Hyperlink))PasteRuns(((Span)inline).Inlines,result);}}
void NormalizePasteDocument(FlowDocument imported){var runs=new System.Collections.Generic.List<Run>();foreach(var paragraph in EnumerateParagraphs(imported.Blocks))PasteRuns(paragraph.Inlines,runs);var originalSizes=new System.Collections.Generic.Dictionary<Run,double>();var originalColors=new System.Collections.Generic.Dictionary<Run,System.Windows.Media.Brush>();foreach(var run in runs){originalSizes[run]=run.FontSize;originalColors[run]=run.Foreground;}var counts=new System.Collections.Generic.Dictionary<System.Windows.Media.Color,int>();var sizes=new System.Collections.Generic.Dictionary<double,int>();foreach(var run in runs){var brush=run.Foreground as System.Windows.Media.SolidColorBrush;if(brush!=null&&NeutralPasteColor(brush.Color)){if(!counts.ContainsKey(brush.Color))counts[brush.Color]=0;counts[brush.Color]+=run.Text.Length;}if(!sizes.ContainsKey(run.FontSize))sizes[run.FontSize]=0;sizes[run.FontSize]+=run.Text.Length;}System.Windows.Media.Color? baseColor=null;int most=0;foreach(var item in counts)if(item.Value>most){baseColor=item.Key;most=item.Value;}double baseline=fontSize;most=0;foreach(var item in sizes)if(item.Value>most){baseline=item.Key;most=item.Value;}foreach(var paragraph in EnumerateParagraphs(imported.Blocks)){paragraph.FontSize=paragraph.FontSize/baseline*fontSize;paragraph.Foreground=Foreground;paragraph.FontFamily=new System.Windows.Media.FontFamily(family);}foreach(var run in runs){var brush=originalColors[run] as System.Windows.Media.SolidColorBrush;if(brush!=null&&baseColor.HasValue&&brush.Color==baseColor.Value)run.Foreground=Foreground;else run.Foreground=originalColors[run];run.FontSize=originalSizes[run]/baseline*fontSize;run.FontFamily=new System.Windows.Media.FontFamily(family);}imported.FontSize=fontSize;imported.Foreground=Foreground;}


}}
