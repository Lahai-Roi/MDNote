using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
namespace MDNote {
// #27: an in-app copy keeps the exact Markdown source in a private clipboard format. The live
// surface takes the AvalonEdit selection, restoring a hidden list/task/quote/heading prefix when
// the selection starts at the first visible character. Paste prefers this format over the
// HTML/RTF that WPF and AvalonEdit add for other applications.
internal static class MarkdownClipboard {
    internal const string Format="MDNote.Markdown";
    static readonly Regex LivePrefix=new Regex(@"^[ \t]*(?:(?:>[ \t]?)+[ \t]*)?(?:(?:[-+*]|\d+[.)])[ \t]+)?(?:\[[ xX]\][ \t]+)?(?:#{1,6}[ \t]+)?",RegexOptions.Compiled);
    internal static void Set(IDataObject data,string markdown){
        if(data==null||String.IsNullOrEmpty(markdown))return;
        try{data.SetData(Format,markdown);}catch(System.Runtime.InteropServices.ExternalException){}
    }
    /* A drag or a probe reads through Get: an unreadable data object answers "no MDNote payload"
       and the caller falls back to its other formats. The paste reads through Read, where a busy
       clipboard must surface so the caller's ClipboardRetry step can wait and try again. */
    internal static string Get(IDataObject data){
        try{return Read(data);}
        catch(System.Runtime.InteropServices.ExternalException){return null;}
    }
    internal static string Read(IDataObject data){
        if(data==null)return null;
        return data.GetDataPresent(Format)?data.GetData(Format) as string:null;
    }
    internal static string CanonicalNewlines(string value){
        if(value==null)return null;
        return value.Replace("\r\n","\n").Replace('\r','\n');
    }
    // NBSP is a normal space in Markdown. A zero-width mark beside an emphasis marker
    // keeps "**bold**" from being recognized after an external HTML/RTF round trip.
    internal static string NormalizeExternal(string value){
        if(String.IsNullOrEmpty(value))return value;
        value=value.Replace((char)0x00A0,' ');
        string zeroWidth="["+(char)0x200B+(char)0xFEFF+"]";
        return Regex.Replace(value,zeroWidth+"(?=[*_~`])|(?<=[*_~`])"+zeroWidth,"");
    }
    internal static string LiveSelection(TextDocument document,int start,int length,bool extendPrefix){
        if(document==null||length<=0)return null;
        start=Math.Max(0,Math.Min(start,document.TextLength));
        length=Math.Max(0,Math.Min(length,document.TextLength-start));
        if(length==0)return null;
        // Stage 4: a selection that runs through table cells copies a table fragment instead of a
        // partial row.
        if(extendPrefix){
            string table=LiveTableSelection(document,start,length);
            if(table!=null)return table;
        }
        if(!extendPrefix)return document.GetText(start,length);
        DocumentLine line=document.GetLineByOffset(start);
        string lineText=document.GetText(line.Offset,line.Length);
        int prefix=LivePrefix.Match(lineText).Length;
        if(prefix>0&&start<=line.Offset+prefix)return document.GetText(line.Offset,length+(start-line.Offset));
        return document.GetText(start,length);
    }
    // The live source's table selection projection: one touched cell copies its clipped text
    // without table syntax, several touched cells become "| a | b |" rows with only the touched
    // columns and a delimiter row. The delimiter keeps the table's own alignments (":---", "---:",
    // ":---:"), so a copied column pastes back aligned. Null means the selection is not a table-cell selection.
    internal static string LiveTableSelection(TextDocument document,int start,int length){
        if(document==null||length<=0)return null;
        start=Math.Max(0,Math.Min(start,document.TextLength));
        length=Math.Max(0,Math.Min(length,document.TextLength-start));
        if(length==0)return null;
        int end=start+length;
        int firstLine,lastLine;
        if(!MarkdownTableCommands.FindTable(document,start,out firstLine,out lastLine))return null;
        DocumentLine lastRow=document.GetLineByNumber(lastLine);
        if(end>lastRow.Offset+lastRow.Length)return null;   /* a selection that leaves the table keeps the editor's text */
        var rows=new List<int>();
        var rowCells=new List<List<MarkdownLivePreview.Cell>>();
        for(int number=firstLine;number<=lastLine;number++){
            if(number==firstLine+1)continue;   /* the delimiter row is not a row */
            DocumentLine line=document.GetLineByNumber(number);
            List<MarkdownLivePreview.Cell> cells=MarkdownLivePreview.SplitCells(document.GetText(line.Offset,line.Length));
            if(!RowSelected(line,cells,start,end))continue;
            rows.Add(number);
            rowCells.Add(cells);
        }
        if(rows.Count==0)return null;
        int columnFirst=Int32.MaxValue,columnLast=-1,touched=0,touchedRow=-1,touchedColumn=-1;
        for(int r=0;r<rows.Count;r++){
            DocumentLine line=document.GetLineByNumber(rows[r]);
            for(int c=0;c<rowCells[r].Count;c++){
                int a=Math.Max(start,line.Offset+rowCells[r][c].Start);
                int b=Math.Min(end,line.Offset+rowCells[r][c].End);
                if(b<=a)continue;
                touched++;
                touchedRow=r;touchedColumn=c;
                if(c<columnFirst)columnFirst=c;
                if(c>columnLast)columnLast=c;
            }
        }
        if(touched==0)return null;
        if(touched==1){
            DocumentLine line=document.GetLineByNumber(rows[touchedRow]);
            int a=Math.Max(start,line.Offset+rowCells[touchedRow][touchedColumn].Start);
            int b=Math.Min(end,line.Offset+rowCells[touchedRow][touchedColumn].End);
            return document.GetText(a,b-a);
        }
        DocumentLine delimiter=document.GetLineByNumber(firstLine+1);
        List<MarkdownLivePreview.Cell> delimiterCells=MarkdownLivePreview.SplitCells(document.GetText(delimiter.Offset,delimiter.Length));
        var delimiters=new List<string>();
        for(int c=columnFirst;c<=columnLast;c++){
            string marker="---";
            if(c<delimiterCells.Count){
                string value=document.GetText(delimiter.Offset+delimiterCells[c].Start,delimiterCells[c].End-delimiterCells[c].Start);
                bool left=value.StartsWith(":",StringComparison.Ordinal),right=value.EndsWith(":",StringComparison.Ordinal);
                marker=left&&right?":---:":right?"---:":left?":---":"---";
            }
            delimiters.Add(marker);
        }
        var lines=new List<string>();
        for(int r=0;r<rows.Count;r++){
            DocumentLine line=document.GetLineByNumber(rows[r]);
            var cells=new List<string>();
            for(int c=columnFirst;c<=columnLast;c++){
                string value="";
                if(c<rowCells[r].Count){
                    int a=Math.Max(start,line.Offset+rowCells[r][c].Start);
                    int b=Math.Min(end,line.Offset+rowCells[r][c].End);
                    if(b>a)value=document.GetText(a,b-a);
                }
                cells.Add(value);
            }
            lines.Add("| "+String.Join(" | ",cells.ToArray())+" |");
        }
        if(lines.Count>0)lines.Insert(1,"| "+String.Join(" | ",delimiters.ToArray())+" |");
        return String.Join("\n",lines.ToArray());
    }
    /* A row belongs to the fragment only where the selection reaches its cells. The caret guard
       (MarkdownLivePreview.NormalizeTableCaret -> MoveCaretAndSelection) moves a selection end
       that sits in a row's non-cell slots - the leading pipe and the padding before the first
       cell - onto the first cell's start, so a whole-line selection that stops where the next
       row begins arrives here one pipe character inside that row. Those characters are the row's
       structure, not selected text: testing the cell span keeps the next row from copying as an
       extra empty row, while a row whose cells are all empty still keeps its shape (its span runs
       from the first cell's start to the last one's end). The start and end are exclusive, so a
       row that ends exactly where the selection starts, or starts exactly where it ends, is not
       selected either. A row with no cells at all falls back to the line's own span. */
    static bool RowSelected(DocumentLine line,List<MarkdownLivePreview.Cell> cells,int start,int end){
        if(cells==null||cells.Count==0)return line.Offset+line.Length>start&&line.Offset<end;
        int contentStart=line.Offset+cells[0].Start;
        int contentEnd=line.Offset+cells[cells.Count-1].End;
        return contentEnd>start&&contentStart<end;
    }
}
// The clipboard is one shared lock. Another program (a download manager, a messenger) can hold it
// open for a few milliseconds, and reading or writing it then fails with the clipboard-busy
// HRESULTs (CLIPBRD_E_CANT_OPEN 0x800401D0 and the three that follow it). Notepad and Word wait
// and try again; so do we. The wait runs only on an explicit Copy/Cut/Paste command, never on
// typing, so the short sleep on the UI thread costs nothing a typist can feel.
internal static class ClipboardRetry {
    internal const int Attempts=10;
    internal const int DelayMilliseconds=15;
    /* CLIPBRD_E_CANT_OPEN (0x800401D0), CANT_SET, CANT_EMPTY, CANT_CLOSE, CANT_FLUSH. Any other
       HRESULT is not a lock time-out: the owning program refused the format itself, and repeating
       the call cannot change that answer. */
    internal static bool Busy(Exception error){
        if(error==null)return false;
        uint code=(uint)error.HResult;
        return code>=0x800401D0u&&code<=0x800401D4u;
    }
    /* True means the action ran. False means the clipboard refused it: a busy lock that outlasted
       the last attempt, or another ExternalException such as a format the owning program no longer
       renders. The caller answers exactly as it did when its own try/catch swallowed that. */
    internal static bool Try(Action action){
        for(int attempt=1;attempt<=Attempts;attempt++){
            try{action();return true;}
            catch(System.Runtime.InteropServices.ExternalException error){
                if(!Busy(error))return false;
                if(attempt==Attempts)return false;
                System.Threading.Thread.Sleep(DelayMilliseconds);
            }
        }
        return false;
    }
    internal static bool Try<T>(Func<T> read,out T value){
        T result=default(T);
        bool ok=Try(delegate{result=read();});
        value=result;
        return ok;
    }
    internal static bool TryGetDataObject(out IDataObject data){
        IDataObject result=null;
        bool ok=Try(delegate{result=System.Windows.Clipboard.GetDataObject();});
        data=result;
        return ok;
    }
    internal static bool TryGetText(out string text){
        string result=null;
        bool ok=Try(delegate{result=System.Windows.Clipboard.GetText();});
        text=result;
        return ok;
    }
    internal static bool TrySetText(string text){
        return Try(delegate{System.Windows.Clipboard.SetText(text);});
    }
}
}
