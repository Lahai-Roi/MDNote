using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {public partial class MainWindow {
internal static int VerifyTableCaret(string outDir){return TableCaretProbe.Run(outDir);}
// Generated fixtures only. Invoke the point-taking core used by PreviewMouseDown,
// not a Direct routed event raised on a child (which never reaches that handler).
internal static class TableCaretProbe {
    static StringBuilder report;
    static bool all;
    static int cases;
    static readonly double[,] Samples={{.5,.5},{.02,.5},{.98,.5},{.5,.02},{.5,.98},{.02,.02},{.98,.98}};
    // Basic-only wide cases: the empty right part of a wide column, the space below a
    // short cell in a tall row, and the area right of the table's last column.
    static readonly double[] WideSamples={.6,.8,.95,.99};
    static readonly double[] RowSamples={0,.5,1};
    public static int Run(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string pass=Path.Combine(outDir,"table-caret-probe-PASS.txt"),fail=Path.Combine(outDir,"table-caret-probe-FAIL.txt");
        if(File.Exists(pass))File.Delete(pass);if(File.Exists(fail))File.Delete(fail);
        report=new StringBuilder("Table caret: arranged cell boxes, centre/padding/edges; live surface.\n");all=true;cases=0;
        partialPath=Path.Combine(outDir,"table-caret-probe-partial.txt");
        bool oldSelf=Program.IsSelfTest,oldLive=ProbeDefaults.Live;
        try{
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            foreach(int width in new[]{900,480})RunView(width);
            RunListItemTableSuite(900);
            RunReviewSuiteFor(900);
            RunSelectionSuite(900);
            RunSelectionLayerSwap();
            RunEditModeSelectionSuite(900);
            RunCellEditingSuite(900);
            RunCellPasteSuite(900);
            RunSpaceAtCellEndSuite(900);
            RunDrawnCaretSuite(900);
            RunCellBreakClickSuite(900);
            RunCaretFrameSuite(900);
            RunTableBoundarySuite(900);
            RunUnicodeCaretSuite(900);
        }catch(Exception ex){all=false;report.Append("EXCEPTION ").Append(ex).Append('\n');}
        finally{Program.IsSelfTest=oldSelf;ProbeDefaults.Live=oldLive;}
        report.Append("Cases: ").Append(cases).Append("\nResult: ").Append(all?"PASS":"FAIL").Append('\n');
        partialPath=null;
        try{
            File.WriteAllText(Path.Combine(outDir,"table-caret-probe.txt"),report.ToString(),new UTF8Encoding(false));
            File.WriteAllText(all?pass:fail,report.ToString(),new UTF8Encoding(false));
        }catch(IOException){return 1;}
        return all?0:1;
    }
    // A list-item table suite in both views: fresh window each time so the fixture text cannot
    // leak between the cases, and every expectation is the serialized Markdown the app saves.
    static void RunListItemTableSuite(int width){
        MainWindow window=null;
        try{
            window=CreateWindow(width);
            RunListItemTable(window,width);
        }catch(Exception ex){Check("list-item table suite "+width,false,ex.ToString());}
        finally{CloseWindow(window);}
    }
    static void Check(string name,bool ok,string detail){cases++;all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(" ").Append(detail).Append('\n');Progress(null);}
    // Flush what is known so far, so a later hang cannot erase earlier results.
    static string partialPath;
    static void Progress(string note){
        if(partialPath==null||report==null)return;
        try{File.WriteAllText(partialPath,report.ToString()+(note==null?"":"RUNNING "+note+"\n"),new UTF8Encoding(false));}catch(Exception){}
    }
    // Idle with a 2 s cap: a layout/dispatch loop that never reaches ApplicationIdle records a FAIL instead of hanging.
    static void SafeIdle(string tag,string step){
        var frame=new System.Windows.Threading.DispatcherFrame();
        bool timedOut=false;
        var timer=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send);
        timer.Interval=TimeSpan.FromSeconds(2);
        timer.Tick+=delegate{timedOut=true;timer.Stop();frame.Continue=false;};
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(delegate{frame.Continue=false;}));
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
        if(timedOut)Check(tag+" idle timeout @"+step,false,"dispatcher never reached ApplicationIdle within 2 s");
    }
    // b52 feedback 2/3: the exact user sequence - a numbered list, Enter at its end, one Backspace
    // (the marker only, the indentation stays) and then a table. Tab/Shift+Tab inside a cell stay
    // cell navigation, so the whole table moves with the table tool row's outdent/indent
    // (EditTable 9/10, and Ctrl+[ / Ctrl+]). The source surface is checked, plus one undo and the save.
    internal static void RunListItemTable(MainWindow window,int width){
        string tag="live list-item table "+width;
        // The live surface edits the Markdown directly, so the user's sequence is the source
        // text and the whole point is that the table stays a table. A table that already sits
        // indented inside the item (the live view's own insert puts the lines on the item's
        // continuation line) - outdent ends the list, indent puts it back, and each move is one
        // history step.
        window.SetText("1. 하나\n1. 둘\n  | x | y |\n  | --- | --- |\n  | 1 | 2 |\n  \n마지막\n");
        window.SetMode(true);LongLineBench.Idle();
        var document=window.source.Document;
        int tableLine=3;
        window.source.Select(document.GetLineByNumber(tableLine).Offset+3,0);LongLineBench.Idle();
        Check(tag+" finds the indented table at the caret",MarkdownStructuralInput.TableAtCaret(document,window.source.CaretIndex),"line="+document.GetLineByOffset(window.source.CaretIndex).LineNumber);
        string inItem=window.text;
        window.EditTable(9);LongLineBench.Idle();
        string outdented=window.text;
        Check(tag+" outdent writes the table at column 0",outdented=="1. 하나\n1. 둘\n| x | y |\n| --- | --- |\n| 1 | 2 |\n\n마지막\n","text=["+FlatText(outdented)+"]");
        window.Undo();LongLineBench.Idle();
        Check(tag+" one undo restores the indented table",FlatText(window.text)==FlatText(inItem),"undone=["+FlatText(window.text)+"]");
        window.Redo();LongLineBench.Idle();
        Check(tag+" one redo outdents it again",FlatText(window.text)==FlatText(outdented),"redone=["+FlatText(window.text)+"]");
        window.EditTable(10);LongLineBench.Idle();
        // The blank separator line under the table stays completely empty (it is a blank line
        // for the item either way), so the indent restores the table lines but not the two
        // spaces that outdent removed from that line.
        string reindented="1. 하나\n1. 둘\n  | x | y |\n  | --- | --- |\n  | 1 | 2 |\n\n마지막\n";
        Check(tag+" indent puts the table back under the item",window.text==reindented,"indented=["+FlatText(window.text)+"]");
    }
    static void RunReviewSuiteFor(int width){
        MainWindow window=null;
        try{
            window=CreateWindow(width);
            RunReviewSuite(window,width);
        }catch(Exception ex){Check("review suite "+width,false,ex.ToString());}
        finally{CloseWindow(window);}
    }
    static string FlatText(string text){return text==null?"null":text.Replace("\r","").Replace("\n","\\n");}
    // Review items 1/2/3/6/8/9: the rules around a table inside a list item and the column grow
    // hold. Every case drives the real window, and the saved Markdown is the authority.
    internal static void RunReviewSuite(MainWindow window,int width){
        string tag="live review "+width;
        // Item 1: repeated Ctrl+] on a table already inside the item must be a no-op, never a
        // second indent (which would make the table an indented code block).
        window.SetText("1. a\n  | x | y |\n  | --- | --- |\n  | 1 | 2 |\n");
        window.SetMode(true);LongLineBench.Idle();
        var document=window.source.Document;
        window.source.Select(document.GetLineByNumber(2).Offset+3,0);LongLineBench.Idle();
        string before=window.text;
        for(int i=0;i<5;i++){window.EditTable(10);LongLineBench.Idle();}
        Check(tag+" repeated Ctrl+] keeps the table at the item indent",window.text==before&&MarkdownStructuralInput.TableAtCaret(window.source.Document,window.source.Document.GetLineByNumber(2).Offset+3),"text=["+FlatText(window.text)+"]");
        // Item 6: Tab at the line's home position is cell navigation, not a table move.
        window.SetText("1. a\n  | x | y |\n  | --- | --- |\n");
        window.SetMode(true);LongLineBench.Idle();
        string beforeTab=window.text;
        window.source.Select(window.source.Document.GetLineByNumber(2).Offset,0);LongLineBench.Idle();
        window.SourceTabKeyProbe(false);LongLineBench.Idle();
        Check(tag+" Tab at home in a table leaves the table alone",window.text==beforeTab,"text=["+FlatText(window.text)+"]");
    }
    static MainWindow CreateWindow(int width){
        ProbeDefaults.Live=true;
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=width,Height=760};
        window.Show();window.textMode=false;
        window.SetText("before\n\n| Left heading | Center heading | Right heading | Empty |\n| :--- | :---: | ---: | --- |\n| alpha | | omega | |\n|||||\n| | middle | | last |\n| longer words that wrap inside the cell and keep another physical row | **bold** | a\\|b | |\n\nafter\n");
        window.SetMode(true);LongLineBench.Idle();window.UpdateLayout();return window;
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        window.FlushDocumentChanges();window.saved=window.text;window.recoveredDocument=false;window.CaptureTab();
        foreach(var document in window.documents){document.Saved=document.Text;document.Recovered=false;}
        window.Close();
    }
    static Point Sample(Rect box,int index){
        // Stay strictly inside the border even on narrow cells.
        double x=Math.Max(.75,Math.Min(box.Width-.75,box.Width*Samples[index,0]));
        double y=Math.Max(.75,Math.Min(box.Height-.75,box.Height*Samples[index,1]));
        return new Point(box.Left+x,box.Top+y);
    }
    static void RunView(int width){
        MainWindow window=null;
        try{
            window=CreateWindow(width);
            try{RunLive(window,width);}
            catch(Exception ex){Check("cell input suite "+width,false,ex.ToString());}
            try{RunAdjacent(window,width);}catch(Exception ex){Check("adjacent input suite "+width,false,ex.ToString());}
            try{RunReentrant(window,width);}catch(Exception ex){Check("reentrant input suite "+width,false,ex.ToString());}
            try{RunFoldedHeader(window,width);}catch(Exception ex){Check("folded header suite "+width,false,ex.ToString());}
            try{RunColumnGrowth(window,width);}catch(Exception ex){Check("column growth suite "+width,false,ex.ToString());}
            // Word-mode drag after a double click, one representative width.
            if(width==900){try{RunWordDrag(window,width);}catch(Exception ex){Check("word drag suite "+width,false,ex.ToString());}}
        }finally{CloseWindow(window);}
    }
    // b52 (user feedback 8/9): a new table must be wide enough to type into, and a cell the
    // user types Korean into must grow its column instead of wrapping and unwrapping per
    // composed syllable. The allocation bounds the table's total width, so the growth stops
    // at the available width and the cell wraps inside it from then on.
    static void RunColumnGrowth(MainWindow window,int width){
        string tag="live column growth "+width;
        // A fresh table exactly as the insert command writes it: three short columns.
        window.SetText("앞 문단\n\n");window.SetMode(true);LongLineBench.Idle();
        int before=TableCount(window);
        window.InsertTable(3,2);window.FlushDocumentChanges();LongLineBench.Idle();window.UpdateLayout();
        Check(tag+" inserts a table",TableCount(window)==before+1,"tables="+before+"->"+TableCount(window));
        double[] widths=ColumnWidths(window);
        if(widths.Length<3){Check(tag+" new table columns",false,"widths="+widths.Length);return;}
        // The floor is a preference that yields to the view width (see ColumnFloor), so a
        // narrow window gets proportionally narrow columns by design.
        double view=LiveViewWidth(window);
        double allowed=MarkdownLivePreview.ColumnFloor(view,widths.Length);
        bool floored=true;
        for(int c=0;c<widths.Length;c++)floored&=widths[c]>=allowed-.75;
        Check(tag+" new table columns are at least the comfortable minimum",floored,
            "widths="+Join(widths)+", floor="+Fmt(allowed));
        // Typing 20 Korean syllables into a cell: the column grows into the table's
        // available width and never shrinks. The allocation now bounds the table's total
        // instead of the column (browser auto layout), so the growth is only expected while
        // the table still has room; TypeIntoCell checks that contract per key.
        bool room;
        double[] grown=TypeIntoCell(window,tag,out room);
        if(grown==null)return;
        bool grew=grown[grown.Length-1]>widths[0]+.5;
        Check(tag+" typing grows the column while the table has room",!room||grew,
            "before="+Fmt(widths[0])+", after="+Fmt(grown[grown.Length-1])+", room="+room);
    }
    static int TableCount(MainWindow window){
        // Count table STARTS (the header row is the line whose predecessor is not in
        // the same table), so the delimiter and data rows are not counted again.
        int liveCount=0;var document=window.source.Document;
        for(int n=1;n<=document.LineCount;n++){
            var info=window.livePreview.Info(document.GetLineByNumber(n));
            if(!info.Table||info.Delimiter)continue;
            var previous=n>1?window.livePreview.Info(document.GetLineByNumber(n-1)):null;
            if(previous==null||!previous.Table||previous.TableStart!=info.TableStart)liveCount++;
        }
        return liveCount;
    }
    static double LiveViewWidth(MainWindow window){return Math.Max(200,window.livePreview.View.ActualWidth);}
    static double[] ColumnWidths(MainWindow window){
        var session=window.livePreview;if(session==null)return new double[0];
        var document=window.source.Document;ICSharpCode.AvalonEdit.Document.DocumentLine row=null;
        for(int n=1;n<=document.LineCount;n++){
            var info=session.Info(document.GetLineByNumber(n));
            if(info.Table&&!info.Delimiter&&info.Cells!=null&&info.Cells.Count>0&&info.Row!=null){row=document.GetLineByNumber(n);break;}
        }
        if(row==null)return new double[0];
        double[] result=new double[session.Info(row).Row.Widths.Length];
        Array.Copy(session.Info(row).Row.Widths,result,result.Length);return result;
    }
    static string Join(double[] values){var text=new StringBuilder();for(int p=0;p<values.Length;p++){if(p>0)text.Append(',');text.Append(Fmt(values[p]));}return text.ToString();}
    static string Fmt(double value){return value.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture);}
    // Types 20 Korean syllables into the first cell of the new table, recording the column
    // width, the row's visual line count and the row's width budget after every key.
    // `room` reports whether the table still had space for the column to grow into.
    // Returns the widths; null when the fixture could not be set up.
    static double[] TypeIntoCell(MainWindow window,string tag,out bool room){
        room=false;
        const int Keys=20;
        var widths=new List<double>();var rows=new List<int>();var budgets=new List<double>();
        var session=window.livePreview;var document=window.source.Document;ICSharpCode.AvalonEdit.Document.DocumentLine line=null;
        for(int n=1;n<=document.LineCount;n++){
            var info=session.Info(document.GetLineByNumber(n));
            if(info.Table&&!info.Delimiter&&info.Cells!=null&&info.Cells.Count>=3&&info.Row!=null){line=document.GetLineByNumber(n);break;}
        }
        if(line==null){Check(tag+" typing fixture",false,"no data row");return null;}
        var cells=session.Info(line).Cells;int at=line.Offset+cells[0].End;
        window.source.Select(at,0);LongLineBench.Idle();window.source.TextArea.Caret.BringCaretToView();LongLineBench.Idle();
        for(int key=0;key<Keys;key++){
            window.source.TextArea.PerformTextInput("가");LongLineBench.Idle();
            ICSharpCode.AvalonEdit.Document.DocumentLine now=document.GetLineByNumber(line.LineNumber);
            var info=session.Info(now);
            if(info.Row==null){Check(tag+" typing keeps the row",false,"row missing at key "+key);return null;}
            widths.Add(info.Row.Widths[0]);rows.Add(info.Row.PhysicalRows);
            // The width TableWidths fitted this row to: RowWidth() minus the row's indent,
            // less the 4 px allowance inside TableWidths itself.
            budgets.Add(Math.Max(40,info.Row.BaseWidth-4));
        }
        bool monotonic=true;
        for(int p=1;p<widths.Count;p++)if(widths[p]+.5<widths[p-1])monotonic=false;
        Check(tag+" typed column widths grow monotonically",monotonic&&widths.Count==Keys,
            "widths="+Join(widths.ToArray()));
        // Browser auto layout (TableWidths): while the table has room, a one-line cell takes
        // its own one-line width, so the typed column grows by every key and the table total
        // stays under the available width. Once the total reaches that width the table holds
        // exactly there and the next keys wrap inside the cell instead of widening the table.
        // So the total never exceeds the available width, and a wrapped typed cell always
        // means a table that already fills it.
        double[] first=ColumnWidths(window);
        if(first.Length<2){Check(tag+" typed row widths",false,"widths="+first.Length);return widths.ToArray();}
        bool contract=true,oscillating=false,started=false;
        double lastTotal=0,lastAvailable=0;
        for(int p=0;p<rows.Count;p++){
            double total=widths[p];
            for(int c=1;c<first.Length;c++)total+=first[c];
            double available=budgets[p];
            lastTotal=total;lastAvailable=available;
            bool full=total>=available-1;
            if(total>available+1)contract=false;
            if(rows[p]>1){if(!full)contract=false;}
            else if(!full){
                room=true;
                // Until the text outgrows the column floor the column stays at the floor;
                // from its first growth on, every key with room left takes its new
                // one-line width (a still column there would be the old per-column cap).
                if(started&&widths[p]<=widths[p-1]+.5)contract=false;
            }
            if(p>0&&widths[p]>widths[p-1]+.5)started=true;
            // A wrap that comes back to one line on the next key is the flicker the user saw.
            if(p+1<rows.Count&&rows[p+1]<rows[p])oscillating=true;
        }
        Check(tag+" typed cell grows the table until it fills the width, then wraps inside it",contract,
            "rows="+JoinInts(rows)+", widths="+Join(widths.ToArray())+", last total="+Fmt(lastTotal)+"/"+Fmt(lastAvailable));
        Check(tag+" typed cell never wraps and unwraps between keys",!oscillating,
            "rows="+JoinInts(rows)+", widths="+Join(widths.ToArray()));
        return widths.ToArray();
    }
    static string JoinInts(List<int> values){var text=new StringBuilder();for(int p=0;p<values.Count;p++){if(p>0)text.Append(',');text.Append(values[p]);}return text.ToString();}
    static System.Windows.Controls.Primitives.ScrollBar FindScrollBar(DependencyObject root){
        if(root==null)return null;
        var bar=root as System.Windows.Controls.Primitives.ScrollBar;
        if(bar!=null)return bar;
        int count=VisualTreeHelper.GetChildrenCount(root);
        for(int i=0;i<count;i++){var found=FindScrollBar(VisualTreeHelper.GetChild(root,i));if(found!=null)return found;}
        return null;
    }
    // Compare the serialized table cell against the click, so a character that lands
    // in another cell or outside the row fails even when a cell did change.
    static List<string> MarkdownCells(string row){
        var cells=new List<string>();var cell=new StringBuilder();bool escaped=false;
        foreach(char ch in row){
            if(escaped){cell.Append(ch);escaped=false;}
            else if(ch=='\\'){cell.Append(ch);escaped=true;}
            else if(ch=='|'){cells.Add(cell.ToString().Trim());cell.Length=0;}
            else cell.Append(ch);
        }
        cells.Add(cell.ToString().Trim());
        if(cells.Count>0&&cells[0].Length==0)cells.RemoveAt(0);
        if(cells.Count>0&&cells[cells.Count-1].Length==0)cells.RemoveAt(cells.Count-1);
        return cells;
    }
    static string SerializedCell(string markdown,int row,int column){
        string[] lines=markdown.Replace("\r","").Split('\n');
        int line=row<=0?row:row+1; // MarkdownWriter inserts the delimiter after row 0.
        if(line<0||line>=lines.Length)return null;
        var cells=MarkdownCells(lines[line]);
        return column>=0&&column<cells.Count?cells[column]:null;
    }
    // A caret snapped onto a row boundary must move into the row's last cell before
    // the edit, so the typed character cannot create a paragraph outside the table.
    static void RunLive(MainWindow window,int width){
        var session=window.livePreview;Check("live fixture "+width,session!=null,"preview attached");if(session==null)return;
        var document=window.source.Document;var rows=new List<int>();
        foreach(var line in document.Lines){var info=session.Info(line);if(info.Table&&!info.Delimiter&&info.Cells!=null)rows.Add(line.LineNumber);}
        // Both the hidden formatted projection and already-revealed caret row must
        // resolve padding/empty cells. Every sample remeasures after toolbar layout.
        foreach(bool raw in new[]{false,true})foreach(int number in rows){
            var line=document.GetLineByNumber(number);int count=session.Info(line).Cells.Count;
            for(int c=0;c<count;c++)for(int sample=0;sample<Samples.GetLength(0);sample++){
                window.source.Select(raw?line.Offset:0,0);LongLineBench.Idle();
                window.source.ScrollToLine(number-1);window.UpdateLayout();session.View.EnsureVisualLines();
                VisualLine visual=session.View.GetOrConstructVisualLine(line);
                var info=session.Info(line);Rect box=session.TableCellBox(visual,c);
                string label="live "+width+" raw="+raw+" row="+number+" cell="+c+" sample="+sample;
                if(box.IsEmpty||box.Width<2||box.Height<2){Check(label,false,"missing cell rectangle");continue;}
                session.BeginPress();bool handled;
                try{handled=session.PlaceTableCaret(Sample(box,sample),false);}
                finally{session.EndPress();}
                int offset=window.source.CaretOffset-line.Offset;
                bool immediate=window.source.SelectionLength==0&&CaretInCellOrStop(info,c,offset);
                LongLineBench.Idle();offset=window.source.CaretOffset-line.Offset;
                bool settled=window.source.SelectionLength==0&&CaretInCellOrStop(info,c,offset);
                Check(label,handled&&immediate&&settled,"handled="+handled+", immediate="+immediate+", settled="+settled+", offset="+offset);
                // A legal source offset must round-trip through the canonical visual
                // stop, including empty cells and filled-cell ends before a gap.
                visual=session.View.GetOrConstructVisualLine(line);
                foreach(int endpoint in new[]{info.Cells[c].Start,info.Cells[c].End}){
                    int column=visual.GetVisualColumn(endpoint);
                    Check(label+" map="+endpoint,visual.GetRelativeOffset(column)==endpoint,"column="+column+", back="+visual.GetRelativeOffset(column));
                }
                if(handled&&settled)TypeLive(window,line,c,label);
            }
        }
    }
    static void RunReentrant(MainWindow window,int width){
        // Native caret/selection listeners were registered before Session. Deliberately
        // construct a row from that earlier Changed slot to make the ordering race
        // deterministic, even when this desktop does not focus the editor's caret.
        window.DetachLivePreview(false);bool inside=false,observed=true;int calls=0;
        EventHandler<ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs> changed=delegate(object sender,ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs args){
            if(inside||!window.LivePreviewAttached)return;
            inside=true;
            try{
                calls++;var document=window.source.Document;var line=document.GetLineByOffset(Math.Min(args.Offset,document.TextLength));
                var info=window.livePreview.Info(line);
                observed&=info.Text==document.GetText(line);
                VisualLine visual=window.livePreview.View.GetOrConstructVisualLine(line);
                observed&=ExactVisualSpan(window,line,visual);
            }finally{inside=false;}
        };
        window.source.Document.Changed+=changed;
        try{
            window.UpdateLivePreview();
            window.SetText("before\n\n| a | b |\n|---|---|\n| x | y |\n\nafter\n");LongLineBench.Idle();
            // 3 and 5 are table rows, 6 and 2 the blank separator lines below and above the table:
            // typing on those two also writes the separator newline (the gap rule TypeLive checks).
            foreach(int number in new[]{3,5,6,2}){
                var line=window.source.Document.GetLineByNumber(number);var info=window.livePreview.Info(line);
                int cell=info.Cells==null?-1:0;int offset=cell<0?line.EndOffset:line.Offset+info.Cells[0].End;
                window.source.Select(offset,0);LongLineBench.Idle();
                TypeLive(window,line,cell,"live reentrant "+width+" line="+number);
            }
            Check("live earlier Changed listener "+width,observed&&calls>=16,"fresh parses and exact consumed row lengths, calls="+calls);
        }finally{window.source.Document.Changed-=changed;}
    }
    // Word-mode drag after a double click. A synthetic double click cannot set
    // MouseButtonEventArgs.ClickCount, so the anchor word is fed directly and the real
    // hit-tested drag step runs afterwards: forward two words, backward one word, Hangul
    // syllables as one word, and a table cell that stops the selection at its own edge.
    static void RunWordDrag(MainWindow window,int width){
        window.SetText("alpha beta gamma delta\n\n가나다 라마바 사아자\n\n| one two | three four |\n| --- | --- |\n| five six | seven eight |\n\nafter\n");
        window.SetMode(true);LongLineBench.Idle();window.UpdateLayout();
        RunWordDragLive(window,width);
    }
    static string LiveWordAt(MarkdownLivePreview.Session session,Point point){
        int start,end;
        if(!session.WordAtPoint(point,out start,out end))return null;
        return session.Document.GetText(start,end-start);
    }
    static Point LiveWordPoint(MarkdownLivePreview.Session session,ICSharpCode.AvalonEdit.Document.DocumentLine line,string word){
        session.View.EnsureVisualLines();
        VisualLine visual=session.View.GetOrConstructVisualLine(line);
        string text=session.Document.GetText(line);
        int at=text.IndexOf(word,StringComparison.Ordinal);
        if(at<0)return new Point(Double.NaN,Double.NaN);
        Point a=visual.GetVisualPosition(visual.GetVisualColumn(at),VisualYPosition.LineMiddle)-session.View.ScrollOffset;
        Point b=visual.GetVisualPosition(visual.GetVisualColumn(at+word.Length),VisualYPosition.LineMiddle)-session.View.ScrollOffset;
        Point guess=new Point((a.X+b.X)/2,(a.Y+b.Y)/2);
        if(String.Equals(LiveWordAt(session,guess),word,StringComparison.Ordinal))return guess;
        // The visual position may sit a hair off the glyph on a hidden-markup line; walk
        // outwards a little so the probe still presses a point the hit test owns.
        for(int radius=2;radius<=64;radius+=2){
            for(int dx=-radius;dx<=radius;dx+=2)for(int dy=-radius;dy<=radius;dy+=2){
                if(Math.Abs(dx)!=radius&&Math.Abs(dy)!=radius)continue;
                Point candidate=new Point(guess.X+dx,guess.Y+dy);
                if(candidate.X<1||candidate.Y<1)continue;
                if(String.Equals(LiveWordAt(session,candidate),word,StringComparison.Ordinal))return candidate;
            }
        }
        return guess;
    }
    static Point FindLiveWordPoint(MarkdownLivePreview.Session session,Rect box,string word){
        double y=box.Top+box.Height/2;
        for(double x=box.Left+2;x<=box.Right-1;x+=3){
            Point point=new Point(x,y);
            if(String.Equals(LiveWordAt(session,point),word,StringComparison.Ordinal))return point;
        }
        return new Point(Double.NaN,Double.NaN);
    }
    static void RunWordDragLive(MainWindow window,int width){
        MarkdownLivePreview.Session session=window.livePreview;
        if(session==null){Check("word drag live fixture "+width,false,"preview not attached");return;}
        ICSharpCode.AvalonEdit.Document.TextDocument document=session.Document;
        window.source.ScrollToLine(0);window.UpdateLayout();LongLineBench.Idle();
        session.View.EnsureVisualLines();
        ICSharpCode.AvalonEdit.Document.DocumentLine first=document.GetLineByNumber(1);
        Point beta=LiveWordPoint(session,first,"beta");
        Point gamma=LiveWordPoint(session,first,"gamma");
        Point delta=LiveWordPoint(session,first,"delta");
        Check("word drag live finds words "+width,!Double.IsNaN(beta.X)&&!Double.IsNaN(gamma.X)&&!Double.IsNaN(delta.X),
            "beta="+beta+", gamma="+gamma+", delta="+delta);
        if(Double.IsNaN(beta.X)||Double.IsNaN(gamma.X)||Double.IsNaN(delta.X))return;
        int start,end;
        bool read=session.WordAtPoint(beta,out start,out end);
        string word=read?document.GetText(start,end-start):"-";
        Check("word drag live reads beta "+width,read&&word=="beta","word=["+word+"] point="+beta);
        if(!read)return;
        session.BeginWordDragForProbe(start,end);
        LongLineBench.Idle();window.UpdateLayout();session.View.EnsureVisualLines();
        delta=LiveWordPoint(session,first,"delta");
        session.WordDragStep(delta);
        string forward=document.GetText(window.source.SelectionStart,window.source.SelectionLength);
        session.EndWordDrag();
        Check("word drag live forward two "+width,forward=="beta gamma delta","selection=["+forward+"] point="+delta);
        read=session.WordAtPoint(gamma,out start,out end);
        word=read?document.GetText(start,end-start):"-";
        if(!read||word!="gamma"){Check("word drag live reads gamma "+width,false,"word=["+word+"]");return;}
        session.BeginWordDragForProbe(start,end);
        LongLineBench.Idle();window.UpdateLayout();session.View.EnsureVisualLines();
        beta=LiveWordPoint(session,first,"beta");
        session.WordDragStep(beta);
        string backward=document.GetText(window.source.SelectionStart,window.source.SelectionLength);
        session.EndWordDrag();
        Check("word drag live backward one "+width,backward=="beta gamma","selection=["+backward+"] point="+beta);
        // Hangul syllables are word characters, not separators.
        ICSharpCode.AvalonEdit.Document.DocumentLine hangul=document.GetLineByNumber(3);
        Point hangulFrom=LiveWordPoint(session,hangul,"라마바");
        Point hangulTo=LiveWordPoint(session,hangul,"사아자");
        if(Double.IsNaN(hangulFrom.X)||Double.IsNaN(hangulTo.X)||!session.WordAtPoint(hangulFrom,out start,out end)){
            Check("word drag live hangul points "+width,false,"from="+hangulFrom+", to="+hangulTo);return;
        }
        session.BeginWordDragForProbe(start,end);
        LongLineBench.Idle();window.UpdateLayout();session.View.EnsureVisualLines();
        hangulTo=LiveWordPoint(session,hangul,"사아자");
        session.WordDragStep(hangulTo);
        string hangulSelection=document.GetText(window.source.SelectionStart,window.source.SelectionLength);
        session.EndWordDrag();
        Check("word drag live hangul "+width,hangulSelection=="라마바 사아자","selection=["+hangulSelection+"] point="+hangulTo);
        // A word inside a table cell never extends past the cell.
        ICSharpCode.AvalonEdit.Document.DocumentLine row=null;
        foreach(ICSharpCode.AvalonEdit.Document.DocumentLine line in document.Lines){
            if(document.GetText(line).IndexOf("five six",StringComparison.Ordinal)>=0){row=line;break;}
        }
        if(row==null){Check("word drag live table row "+width,false,"no row");return;}
        window.source.ScrollToLine(row.LineNumber-1);window.UpdateLayout();LongLineBench.Idle();session.View.EnsureVisualLines();
        VisualLine visual=session.View.GetOrConstructVisualLine(row);
        Rect box=session.TableCellBox(visual,0);
        if(box.IsEmpty||box.Width<20){Check("word drag live cell box "+width,false,"box="+box);return;}
        Point five=FindLiveWordPoint(session,box,"five");
        if(Double.IsNaN(five.X)){Check("word drag live finds five "+width,false,"box="+box);return;}
        if(!session.WordAtPoint(five,out start,out end)){Check("word drag live reads five "+width,false,"no word");return;}
        session.BeginWordDragForProbe(start,end);
        LongLineBench.Idle();window.UpdateLayout();session.View.EnsureVisualLines();
        visual=session.View.GetOrConstructVisualLine(row);
        box=session.TableCellBox(visual,0);
        session.WordDragStep(new Point(box.Right-3,box.Top+box.Height/2));
        string cellSelection=document.GetText(window.source.SelectionStart,window.source.SelectionLength);
        int selectedEnd=window.source.SelectionStart+window.source.SelectionLength;
        MarkdownLivePreview.LineInfo info=session.Info(row);
        int cellEnd=info.Cells!=null&&info.Cells.Count>0?row.Offset+info.Cells[0].End:-1;
        session.EndWordDrag();
        Check("word drag live cell clamp "+width,
            cellSelection.Trim()=="five six"&&cellEnd>0&&selectedEnd<=cellEnd&&cellSelection.IndexOf("seven",StringComparison.Ordinal)<0,
            "selection=["+cellSelection+"] end="+selectedEnd+", cellEnd="+cellEnd+", box="+box);
    }
    static string CellText(TableCell cell){return new TextRange(cell.ContentStart,cell.ContentEnd).Text;}
    static int PipeCount(string row){
        int count=0;bool escaped=false;
        foreach(char ch in row){if(escaped){escaped=false;continue;}if(ch=='\\'){escaped=true;continue;}if(ch=='|')count++;}
        return count;
    }
    static bool SamePipes(string before,string after){
        string[] a=before.Replace("\r","").Split('\n'),b=after.Replace("\r","").Split('\n');
        if(a.Length!=b.Length)return false;
        for(int i=0;i<a.Length;i++)if(PipeCount(a[i])!=PipeCount(b[i]))return false;
        return true;
    }
    static void LiveBackspace(MainWindow window){
        var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window.source),0,Key.Back){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        window.source.TextArea.RaiseEvent(args);
        backPreviewHandled=args.Handled;backKeyDownHandled=false;
        if(!args.Handled){args.RoutedEvent=Keyboard.KeyDownEvent;window.source.TextArea.RaiseEvent(args);backKeyDownHandled=args.Handled;}
    }
    static bool backPreviewHandled,backKeyDownHandled;
    static void BackEvt(MainWindow window,string tag,bool readVisualColumn){
        var caret=window.source.TextArea.Caret;
        var sb=new StringBuilder("BACKEVT ").Append(tag).Append(" offset=").Append(caret.Offset).Append(" loc=").Append(caret.Line).Append(":").Append(caret.Column);
        if(readVisualColumn)sb.Append(" VC=").Append(caret.Position.VisualColumn);
        report.Append("PROJECTION DETAIL ").Append(sb.ToString()).Append('\n');
    }
    // Full col->offset map straight from TableProjection (canonical = Reverse[offset]==col), not via GetNextCaretPosition.
    static void BackMap(MainWindow window,ICSharpCode.AvalonEdit.Document.DocumentLine line,string when){
        var l=window.source.Document.GetLineByNumber(line.LineNumber);
        VisualLine visual=window.livePreview.View.GetOrConstructVisualLine(l);
        foreach(var element in visual.Elements){
            var row=element as MarkdownLivePreview.TableRowElement;
            if(row!=null)report.Append("PROJECTION DETAIL BACKMAP ").Append(when).Append(' ').Append(row.DebugMap()).Append('\n');
        }
    }
    // Diagnostics for the failing live row 3 cell 1 Backspace only (fixture text, safe to log).
    static void BackDiag(MainWindow window,ICSharpCode.AvalonEdit.Document.DocumentLine line,string when,string extra){
        var document=window.source.Document;var session=window.livePreview;
        var l=document.GetLineByNumber(line.LineNumber);string raw=document.GetText(l);
        var caretPos=window.source.TextArea.Caret.Position;int offset=window.source.CaretOffset,rel=offset-l.Offset;
        var sb=new StringBuilder();
        sb.Append("BACKDIAG ").Append(when).Append(" row=").Append(l.LineNumber).Append(" text=[").Append(raw.Replace("\r","\\r").Replace("\n","\\n")).Append("]")
          .Append(" caretOffset=").Append(offset).Append(" rel=").Append(rel).Append(" caretVC=").Append(caretPos.VisualColumn).Append(" caretLoc=").Append(caretPos.Line).Append(":").Append(caretPos.Column);
        try{
            VisualLine visual=session.View.GetOrConstructVisualLine(l);
            int canonical=visual.GetVisualColumn(rel);
            sb.Append(" canonicalVC(Reverse)=").Append(canonical).Append(" visualLen=").Append(visual.VisualLength).Append(" Map[vc-3..vc+3]=");
            for(int c=caretPos.VisualColumn-3;c<=caretPos.VisualColumn+3;c++){
                sb.Append(c).Append("->");
                if(c<0||c>visual.VisualLength)sb.Append("n/a");else sb.Append(visual.GetRelativeOffset(c));
                sb.Append(c<visual.VisualLength&&c>=0&&visual.GetNextCaretPosition(c-1,System.Windows.Documents.LogicalDirection.Forward,ICSharpCode.AvalonEdit.Document.CaretPositioningMode.Normal,false)==c?"*":"").Append(' ');
            }
            sb.Append("(* = canonical stop) prevStop(EveryCodepoint)=").Append(visual.GetNextCaretPosition(caretPos.VisualColumn,System.Windows.Documents.LogicalDirection.Backward,ICSharpCode.AvalonEdit.Document.CaretPositioningMode.EveryCodepoint,false));
        }catch(Exception ex){sb.Append(" visualError=").Append(ex.GetType().Name);}
        var info=session.Info(l);
        if(info.Cells!=null&&info.Cells.Count>1)sb.Append(" cell1=[").Append(info.Cells[1].Start).Append(",").Append(info.Cells[1].End).Append("]");
        if(info.Cells!=null){sb.Append(" cells=");foreach(var cell in info.Cells)sb.Append("[").Append(cell.Start).Append(",").Append(cell.End).Append("]");}
        sb.Append(" raw=").Append(info.Row!=null&&info.Row.Raw).Append(" sel=").Append(window.source.SelectionLength).Append(' ').Append(extra);
        report.Append("PROJECTION DETAIL ").Append(sb.ToString()).Append('\n');
    }
    // A visual line owns either one source row, or exactly the header plus its newline
    // and delimiter text. The TableRowElement itself must still own ONLY the header.
    static bool ExactVisualSpan(MainWindow window,ICSharpCode.AvalonEdit.Document.DocumentLine line,VisualLine visual){
        var info=window.livePreview.Info(line);var delimiter=line.NextLine;
        bool folded=info.Header&&delimiter!=null&&!window.livePreview.Raw(delimiter);
        int extra=folded?line.DelimiterLength+delimiter.Length:0,consumed=0,hidden=0;
        bool exact=visual.FirstDocumentLine==line&&visual.LastDocumentLine==(folded?delimiter:line);
        foreach(var element in visual.Elements){
            exact&=element.RelativeTextOffset==consumed;
            if(element is MarkdownLivePreview.TableRowElement)exact&=element.DocumentLength==line.Length&&consumed==0;
            if(element.DocumentLength>0&&consumed>=line.Length){
                hidden++;exact&=folded&&element is MarkdownLivePreview.InvisibleElement&&consumed==line.Length&&element.DocumentLength==extra;
            }
            consumed+=element.DocumentLength;
        }
        return exact&&consumed==line.Length+extra&&hidden==(folded?1:0);
    }
    static void RunFoldedHeader(MainWindow window,int width){
        foreach(bool wrap in new[]{true,false})foreach(double spacing in new[]{50.0,150.0})foreach(string newline in new[]{"\n","\r\n"}){
            string fixture=String.Join(newline,new[]{"before","","| head | title |","|---|---|","| first | body |","","after",""});
            window.SetText(fixture);window.SetMode(true);window.SetLinePercent(spacing);window.SetWrap(wrap);LongLineBench.Idle();
            var document=window.source.Document;var session=window.livePreview;var view=session.View;
            var header=document.GetLineByNumber(3);var delimiter=document.GetLineByNumber(4);var body=document.GetLineByNumber(5);
            string tag="live fold "+width+" spacing="+spacing+" newline="+newline.Length+" wrap="+wrap;
            window.source.Select(document.GetLineByNumber(7).Offset,0);LongLineBench.Idle();
            VisualLine visual=view.GetOrConstructVisualLine(header);
            Check(tag+" exact span",ExactVisualSpan(window,header,visual)&&view.GetOrConstructVisualLine(delimiter)==visual,"header and delimiter only");
            Check(tag+" no band",Math.Abs(view.GetVisualTopByDocumentLine(5)-visual.VisualTop-visual.Height)<=1,"first body row touches header");
            foreach(var row in new[]{header,body}){
                // Both native point-to-position and the app's cell hit test must stay on
                // this row. Freeze reveal just as the actual mouse press does.
                window.source.Select(0,0);LongLineBench.Idle();session.BeginPress();
                try{
                    visual=view.GetOrConstructVisualLine(row);var info=session.Info(row);int at=info.Cells[0].Start+1;
                    Point point=visual.GetVisualPosition(visual.GetVisualColumn(at),VisualYPosition.LineMiddle);
                    var hit=view.GetPosition(point);
                    bool native=hit.HasValue&&document.GetOffset(hit.Value.Location)==row.Offset+at;
                    bool placed=session.PlaceTableCaret(point-view.ScrollOffset,false);
                    Check(tag+" hit row="+row.LineNumber,native&&placed&&window.source.CaretOffset==row.Offset+at,"native="+native+" caret="+window.source.CaretOffset+" expected="+(row.Offset+at));
                }finally{session.EndPress();}
                LongLineBench.Idle();
                TypeLive(window,row,0,tag+" typing row="+row.LineNumber);
                int start=row.Offset+session.Info(row).Cells[0].Start,end=start+3;
                window.source.Select(start,end-start);LongLineBench.Idle();
                Check(tag+" selection row="+row.LineNumber,window.source.SelectionStart==start&&window.source.SelectionLength==3&&window.source.SelectedText==document.GetText(start,3),"source selection/copy payload exact");
                window.source.TextArea.PerformTextInput("Z");LongLineBench.Idle();
                Check(tag+" replace selection row="+row.LineNumber,document.Text==fixture.Remove(start,3).Insert(start,"Z")&&window.source.CaretOffset==start+1,"only selected header/body text changed");
                window.Undo();LongLineBench.Idle();
                int middle=row.Offset+session.Info(row).Cells[0].Start+1;
                MarkdownLivePreview.Cell cellInfo=session.Info(row).Cells[0];
                int cellEnd=row.Offset+cellInfo.End;
                window.source.Select(middle,0);LongLineBench.Idle();
                EditingCommands.SelectToLineEnd.Execute(null,window.source.TextArea);LongLineBench.Idle();
                // Shift+End stays inside the caret's own cell: the selection ends at the end of
                // that cell's text (before the next '|'), never after the row's trailing pipe.
                Check(tag+" Shift+End row="+row.LineNumber,window.source.SelectionStart==middle&&window.source.SelectionLength==cellEnd-middle&&window.source.SelectedText==document.GetText(middle,cellEnd-middle),"selection/copy must exclude the cell delimiter");
                window.source.Select(middle,0);LongLineBench.Idle();
                EditingCommands.MoveToLineEnd.Execute(null,window.source.TextArea);LongLineBench.Idle();
                Check(tag+" End row="+row.LineNumber,window.source.CaretOffset==cellEnd,"caret="+window.source.CaretOffset+" expected="+cellEnd);
                // Typing at that End must land inside the last cell of THIS row and must not add
                // a column: the old expectation inserted after the trailing '|', which created one.
                int columnsBefore=MarkdownLivePreview.SplitCells(document.GetText(row)).Count;
                window.source.TextArea.PerformTextInput("가");LongLineBench.Idle();
                string rowAfter=document.GetText(document.GetLineByNumber(row.LineNumber));
                int columnsAfter=MarkdownLivePreview.SplitCells(rowAfter).Count;
                MarkdownLivePreview.LineInfo afterInfo=session.Info(document.GetLineByNumber(row.LineNumber));
                int insertedAfter=rowAfter.IndexOf("가",StringComparison.Ordinal);
                // End stays in the caret's OWN cell (cell 0 here), not the row's last cell.
                bool inCell=insertedAfter>=0&&afterInfo.Cells!=null&&afterInfo.Cells.Count>0&&
                    insertedAfter<=afterInfo.Cells[0].End&&insertedAfter>=afterInfo.Cells[0].Start;
                Check(tag+" type after End row="+row.LineNumber,columnsAfter==columnsBefore&&inCell,"columns="+columnsBefore+"->"+columnsAfter+", inserted at "+insertedAfter+", row=["+rowAfter+"]");
                window.Undo();LongLineBench.Idle();
                Check(tag+" End restores source row="+row.LineNumber,document.Text==fixture,"undo restores the row");
                // Home goes to the start of the caret's own cell text, not before the leading '|'.
                window.source.Select(middle,0);LongLineBench.Idle();
                EditingCommands.MoveToLineStart.Execute(null,window.source.TextArea);LongLineBench.Idle();
                int homeExpected=row.Offset+session.Info(row).Cells[0].Start;
                Check(tag+" Home row="+row.LineNumber,window.source.CaretOffset==homeExpected,"caret="+window.source.CaretOffset+" expected="+homeExpected);
                // Home/End of a caret in the SECOND cell stay in that cell.
                if(session.Info(row).Cells.Count>1){
                    MarkdownLivePreview.Cell second=session.Info(row).Cells[1];
                    window.source.Select(row.Offset+second.Start+1,0);LongLineBench.Idle();
                    EditingCommands.MoveToLineEnd.Execute(null,window.source.TextArea);LongLineBench.Idle();
                    Check(tag+" End cell1 row="+row.LineNumber,window.source.CaretOffset==row.Offset+second.End,"caret="+window.source.CaretOffset+" expected="+(row.Offset+second.End));
                    EditingCommands.MoveToLineStart.Execute(null,window.source.TextArea);LongLineBench.Idle();
                    Check(tag+" Home cell1 row="+row.LineNumber,window.source.CaretOffset==row.Offset+second.Start,"caret="+window.source.CaretOffset+" expected="+(row.Offset+second.Start));
                }
            }
            window.source.Select(header.Offset+session.Info(header).Cells[0].Start+1,0);LongLineBench.Idle();
            EditingCommands.MoveDownByLine.Execute(null,window.source.TextArea);LongLineBench.Idle();
            Check(tag+" Down",window.source.TextArea.Caret.Line==5,"row="+window.source.TextArea.Caret.Line);
            EditingCommands.MoveUpByLine.Execute(null,window.source.TextArea);LongLineBench.Idle();
            Check(tag+" Up",window.source.TextArea.Caret.Line==3,"row="+window.source.TextArea.Caret.Line);
            Check(tag+" source preserved",document.Text==fixture,"navigation and undo keep delimiter intact");
        }
        window.SetLinePercent(150);window.SetWrap(true);LongLineBench.Idle();
    }
    static bool LiveProjection(MainWindow window,int caret){
        var session=window.livePreview;var document=window.source.Document;
        window.UpdateLayout();session.View.EnsureVisualLines();
        bool ok=true;
        foreach(var line in document.Lines){
            string raw=document.GetText(line);if(PipeCount(raw)==0)continue;
            var info=session.Info(line);ok&=ProjectionPart(info.Table&&info.Text==raw,"row="+line.LineNumber+" current table parse");
            if(info.Delimiter)continue;
            VisualLine visual=session.View.GetOrConstructVisualLine(line);
            int projected=0;foreach(var element in visual.Elements)if(element is MarkdownLivePreview.TableRowElement)projected++;
            ok&=ProjectionPart(projected==1&&ExactVisualSpan(window,line,visual)&&info.Row!=null,
                "row="+line.LineNumber+" projection count="+projected+", last="+visual.LastDocumentLine.LineNumber+", layout="+(info.Row!=null));
            if(info.Cells!=null&&info.Row!=null)for(int c=0;c<info.Cells.Count;c++){
                var cell=info.Cells[c];
                foreach(int endpoint in new[]{cell.Start,cell.End}){
                    // Formatted rows hide inline markup: the start of **bold** is
                    // intentionally mapped after the opening **, not onto its hidden
                    // syntax. Raw (caret) rows still require exact source endpoints.
                    var text=info.Row.Texts[c];
                    int expected=info.Row.Raw||endpoint==cell.End?endpoint:text.Map[0];
                    int column=visual.GetVisualColumn(endpoint),back=visual.GetRelativeOffset(column);
                    ok&=ProjectionPart(back==expected&&back>=cell.Start&&back<=cell.End&&visual.GetVisualColumn(back)==column,
                        "row="+line.LineNumber+" cell="+c+" endpoint="+endpoint+", canonical="+expected+", back="+back+", raw="+info.Row.Raw);
                }
            }
        }
        var caretLine=document.GetLineByOffset(caret);VisualLine caretVisual=session.View.GetOrConstructVisualLine(caretLine);
        int relative=caret-caretLine.Offset,columnAt=caretVisual.GetVisualColumn(relative);
        // A padding-only cell's own caret stop (MarkdownTableCommands.EmptyCellStop) has no column
        // of its own in a FORMATTED row: the padding run is hidden, so the stop shares the cell
        // end's column and the round-trip answers the cell end. A raw (caret) row maps source
        // characters one to one and must answer the stop itself.
        var caretInfo=session.Info(caretLine);
        bool formattedCaret=caretInfo.Row!=null&&!caretInfo.Row.Raw;
        int canonicalCaret=formattedCaret?CaretProjectionEnd(caretInfo,relative):relative;
        ok&=ProjectionPart(caretVisual.GetRelativeOffset(columnAt)==canonicalCaret,"caret source round-trip row="+caretLine.LineNumber+", relative="+relative+", canonical="+canonicalCaret);
        // Reading Position forces AvalonEdit's native caret-stop validation; checking
        // Offset alone would miss a caret that moves only on the next text input.
        var position=window.source.TextArea.Caret.Position;
        ok&=ProjectionPart(window.source.CaretOffset==caret&&caretVisual.GetRelativeOffset(position.VisualColumn)==canonicalCaret,
            "native caret row="+caretLine.LineNumber+", offset="+window.source.CaretOffset+"/"+caret+", visual="+position.VisualColumn+", canonical="+canonicalCaret);
        return ok;
    }
    // The offset a caret's visual column resolves to on a formatted row: an empty-cell stop maps
    // to the cell's own end (the hidden padding run's column), every other offset to itself.
    static int CaretProjectionEnd(MarkdownLivePreview.LineInfo info,int relative){
        if(info==null||info.Cells==null)return relative;
        for(int c=0;c<info.Cells.Count;c++){
            MarkdownLivePreview.Cell cell=info.Cells[c];
            if(cell.Start!=cell.End||relative!=cell.End-1)continue;
            string text=info.Text;
            int open=relative-1;
            while(open>=0&&(text[open]==' '||text[open]=='\t'))open--;
            if(open>=0&&text[open]=='|'&&cell.End-open-1>=2)return cell.End;
        }
        return relative;
    }
    static bool ProjectionPart(bool ok,string detail){if(!ok)report.Append("PROJECTION DETAIL ").Append(detail).Append('\n');return ok;}
    // fix13: the first text typed on the empty line a table needs as its separator writes that
    // newline too (the live surface's MarkdownLivePreview.TableGapMode). The probe predicts the
    // text and the caret from the rule so typing on such a line is checked against the intended
    // document, not the old adjacent one. 1: below a row, newline before the text; 2: above a
    // header, newline after it.
    static int GapMode(MainWindow window,int offset,out string newline){
        newline=null;
        var document=window.source.Document;var session=window.livePreview;
        if(document==null||session==null||offset<0||offset>document.TextLength)return 0;
        var line=document.GetLineByOffset(offset);
        if(line.Length!=0||offset!=line.Offset)return 0;
        if(session.Info(line).Code)return 0;
        var next=line.NextLine;bool above=next!=null&&session.Info(next).Header;
        if(!above){var previous=line.PreviousLine;if(previous==null||!session.Info(previous).Table)return 0;}
        if(line.DelimiterLength>0)newline=document.GetText(line.EndOffset,line.DelimiterLength);
        else{var previous=line.PreviousLine;newline=previous!=null&&previous.DelimiterLength>0?document.GetText(previous.EndOffset,previous.DelimiterLength):"\n";}
        return above?2:1;
    }
    // A caret inside a cell's text span, or on a padding-only cell's own caret stop one space
    // inside its closing pipe (MarkdownTableCommands.EmptyCellStop): the spot a click, a row add
    // or a word delete leaves on an empty cell before typing into it. A cell narrower than two
    // padding characters keeps its end and never answers the second form.
    static bool CaretInCellOrStop(MarkdownLivePreview.LineInfo info,int cell,int relative){
        if(info==null||info.Cells==null||cell<0||cell>=info.Cells.Count)return false;
        MarkdownLivePreview.Cell range=info.Cells[cell];
        if(relative>=range.Start&&relative<=range.End)return true;
        if(range.Start!=range.End||range.End<2)return false;
        string text=info.Text;
        int open=range.End-1;
        while(open>=0&&(text[open]==' '||text[open]=='\t'))open--;
        return open>=0&&text[open]=='|'&&range.End-open-1>=2&&relative==range.End-1;
    }
    static void TypeLive(MainWindow window,ICSharpCode.AvalonEdit.Document.DocumentLine line,int cell,string label){
        var document=window.source.Document;string before=document.Text,expected=before;
        // `plain` tracks the same typing without the separator newline, so the pipe check keeps
        // comparing like with like (SamePipes also compares the line count).
        string plain=before;
        int start=window.source.CaretOffset,caret=start,plainCaret=start;
        foreach(string input in new[]{"Q","한","back","back"}){
            bool diag=input=="back"&&(cell==0||cell==1)&&line.LineNumber==3;
            EventHandler<ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs> evChanging=null,evChanged=null;EventHandler evPos=null,evLines=null;
            if(input=="back"){
                if(diag){
                    BackDiag(window,line,"BEFORE-BACK "+label,"expectedAfter=["+expected.Remove(caret-1,1).Replace("\r","\\r").Replace("\n","\\n")+"] expectedCaret="+(caret-1));
                    BackMap(window,line,"BEFORE-BACK");
                    // (a) above. (b) Changing/Changed run inside the delete; (c) PositionChanged = AvalonEdit moving the caret.
                    // Only Offset/Line/Column are read there (reading Caret.Position would force a visual column validation and change the result).
                    evChanging=delegate(object s,ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs e){BackEvt(window,"b0 Document.Changing off="+e.Offset+" remove="+e.RemovalLength+" insert="+e.InsertionLength,false);};
                    evChanged=delegate(object s,ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs e){BackEvt(window,"b1 Document.Changed off="+e.Offset+" remove="+e.RemovalLength+" insert="+e.InsertionLength,false);};
                    evPos=delegate(object s,EventArgs e){BackEvt(window,"c Caret.PositionChanged",false);};
                    // (d) VisualLinesChanged = TextView rebuilt visual lines (re-projection done); reading VC here is intentional.
                    evLines=delegate(object s,EventArgs e){BackEvt(window,"d TextView.VisualLinesChanged",true);};
                    document.Changing+=evChanging;document.Changed+=evChanged;
                    window.source.TextArea.Caret.PositionChanged+=evPos;window.source.TextArea.TextView.VisualLinesChanged+=evLines;
                    foreach(var pr in new[]{System.Windows.Threading.DispatcherPriority.Render,System.Windows.Threading.DispatcherPriority.Loaded,System.Windows.Threading.DispatcherPriority.Background,System.Windows.Threading.DispatcherPriority.ApplicationIdle}){
                        string tag="e Dispatcher."+pr;
                        window.Dispatcher.BeginInvoke(new Action(delegate{BackEvt(window,tag,true);}),pr);
                    }
                    BackEvt(window,"a just before key",true);
                }
                expected=expected.Remove(caret-1,1);caret--;plain=plain.Remove(plainCaret-1,1);plainCaret--;LiveBackspace(window);
                if(diag){
                    BackEvt(window,"d0 right after key returned (sync)",true);
                    BackDiag(window,line,"AFTER-BACK "+label,"previewHandled="+backPreviewHandled+" keyDownHandled="+backKeyDownHandled+" docSameAsExpected="+(document.Text==expected));
                    BackMap(window,line,"AFTER-BACK(sync)");
                }
            }
            else{
                string gap;int gapMode=GapMode(window,caret,out gap);
                if(gapMode==1){expected=expected.Insert(caret,gap);caret+=gap.Length;}
                expected=expected.Insert(caret,input);caret+=input.Length;plain=plain.Insert(plainCaret,input);plainCaret+=input.Length;
                // Above a header the caret stays on the text, so the newline follows it without
                // moving the caret past it.
                if(gapMode==2)expected=expected.Insert(caret,gap);
                window.source.TextArea.PerformTextInput(input);
            }
            bool immediate=document.Text==expected&&window.source.CaretOffset==caret;
            LongLineBench.Idle();
            if(diag&&input=="back"){
                document.Changing-=evChanging;document.Changed-=evChanged;
                window.source.TextArea.Caret.PositionChanged-=evPos;window.source.TextArea.TextView.VisualLinesChanged-=evLines;
                BackEvt(window,"e2 after Idle pump",true);
                BackMap(window,line,"AFTER-IDLE");
            }
            bool projection=LiveProjection(window,caret),content=document.Text==expected&&SamePipes(before,plain);
            var info=window.livePreview.Info(line);
            bool inside=cell<0||CaretInCellOrStop(info,cell,caret-line.Offset);
            Check(label+" input="+input,immediate&&content&&projection&&inside,"immediate="+immediate+", source="+content+", projection/maps="+projection+", in cell="+inside+", caret="+window.source.CaretOffset+"/"+caret);
            if(!immediate||!content||!projection||!inside)throw new InvalidOperationException(label+" input="+input+" broke source/caret/projection");
        }
        // Two Backspaces delete exactly the typed text; a separator newline the rule wrote stays.
        Check(label+" restored",document.Text==expected&&window.source.CaretOffset==caret,"two Backspaces delete only the typed text");
    }
    static void RunAdjacent(MainWindow window,int width){
        // The fixture has blank lines immediately before and after the table. Those two are the
        // table-gap lines, so the first text typed on them also writes the separator newline and
        // TypeLive checks the intended document; the nonblank neighbours keep the plain insert.
        // Also exercise a newline that shifts DocumentLines.
        foreach(bool blank in new[]{true,false}){
            string fixture=(blank?"before\n\n":"before\n")+"| a | b |\n|---|---|\n| x | y |\n"+(blank?"\nafter\n":"after\n");
            foreach(bool above in new[]{true,false}){
                window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();
                var document=window.source.Document;int number=above?(blank?2:1):(blank?6:5);
                var line=document.GetLineByNumber(number);window.source.Select(line.EndOffset,0);LongLineBench.Idle();
                TypeLive(window,line,-1,"live adjacent "+width+" blank="+blank+" above="+above);
                // A real Enter above/below the table followed by text must retain the
                // table association and source maps despite all following line shifts.
                int at=window.source.CaretOffset;window.source.TextArea.PerformTextInput("\n");LongLineBench.Idle();
                int after=window.source.CaretOffset;bool newline=document.GetText(at,after-at).Replace("\r","")=="\n";
                Check("live adjacent newline "+width+"/"+blank+"/"+above,newline&&LiveProjection(window,after),"source line shift and projection");
                TypeLive(window,document.GetLineByOffset(after),-1,"live adjacent shifted "+width+"/"+blank+"/"+above);
            }
        }
    }
    // Feedback 11: a selection inside ONE cell of a wrapped live table row painted the whole
    // row width, including the neighbouring cell. The stock selection layer converts the
    // segment ends to visual columns and fills every physical row between them, and a table
    // row's visual columns are row-major, so the two ends landed in the wrong place.
    static void RunSelectionSuite(int width){
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try{
            // A bare editor on purpose: inside MainWindow the live surface's page insets make the
            // formatter's paragraph narrower than View.ActualWidth, so a table sized to
            // View.ActualWidth re-wraps its first row and the slice -> text line mapping this
            // suite measures is not the one users get.
            editor=new SourceEditor{FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=new Window{Left=-20000,Top=0,Width=width,Height=860,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,Content=editor};
            window.Show();LongLineBench.Idle();
            session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings{FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
            RunSelectionRects(editor,session,width);
        }catch(Exception ex){Check("selection rects suite "+width,false,ex.ToString());}
        finally{if(session!=null)session.Dispose();if(window!=null)window.Close();}
    }
    static string SelectionFixture(){
        var text=new StringBuilder("앞 문단 첫 줄은 선택 시작점이 될 만큼 충분히 깁니다.\n\n| first heading | second heading |\n| --- | --- |\n| ");
        for(int i=0;i<4;i++)text.Append("alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi ");
        text.Append("| b |\n\n뒤 문단도 선택 끝점이 될 만큼 충분히 깁니다.\n");
        return text.ToString();
    }
    static void RunSelectionRects(SourceEditor editor,MarkdownLivePreview.Session session,int width){
        string tag="selection rects "+width;
        var view=session.View;var document=editor.Document;
        editor.Text=SelectionFixture();editor.CaretIndex=0;LongLineBench.Idle();view.UpdateLayout();
        var row=document.GetLineByNumber(5);
        // Start from the paragraph above: a line in the reveal set is drawn as source (raw)
        // and keeps the stock geometry on purpose (checked separately below).
        editor.Select(document.GetLineByNumber(1).Offset+2,0);LongLineBench.Idle();
        VisualLine visual=view.GetOrConstructVisualLine(row);view.UpdateLayout();
        MarkdownLivePreview.LineInfo info=session.Info(row);
        if(info.Row==null||info.Cells==null||info.Cells.Count<2||info.Row.Slices[0]==null){
            Check(tag+" fixture row",false,"row="+(info.Row!=null)+", cells="+(info.Cells==null?-1:info.Cells.Count));return;
        }
        MarkdownLivePreview.RowLayout layout=info.Row;
        List<MarkdownLivePreview.CellSlice> slices=layout.Slices[0];
        Check(tag+" fixture cell0 wraps",slices.Count>=3,"slices="+slices.Count+", cells="+info.Cells.Count+", width="+Fmt(layout.Widths[0])+", para="+Fmt(layout.ParagraphWidth)+", view="+Fmt(view.ActualWidth));
        if(slices.Count<3)return;
        int from=0,to=0;
        bool frozen=false;
        session.BeginPress();
        try{
            var text0=layout.Texts[0];var text1=layout.Texts[1];
            int textFrom=slices[0].Start+slices[0].Length/2;
            int textTo=slices[slices.Count-1].Start+slices[slices.Count-1].Length;
            from=row.Offset+text0.Map[Math.Min(textFrom,text0.Map.Length-1)];
            to=row.Offset+text0.Map[Math.Min(textTo,text0.Map.Length-1)];
            ICSharpCode.AvalonEdit.Editing.SelectionSegment seg=Select(editor,from,to);
            visual=view.GetOrConstructVisualLine(row);view.UpdateLayout();
            info=session.Info(row);layout=info.Row;
            Check(tag+" frozen row is still a table",!layout.Raw,"raw="+layout.Raw);
            Rect box0=session.TableCellBox(visual,0),box1=session.TableCellBox(visual,1);
            List<List<Rect>> groups=LiveTableSelectionLayer.CollectSelectionRectGroups(view,editor.TextArea.Selection);
            List<Rect> rects=Flatten(groups);
            bool inside=box0.Width>4&&box1.Width>4&&rects.Count==layout.Slices[0].Count&&groups.Count==1;
            foreach(Rect rect in rects)inside&=rect.Left>=box0.Left-1&&rect.Right<=box0.Right+1&&rect.Right<=box1.Left+.5&&rect.Width>.5&&rect.Height>.5;
            Check(tag+" drag inside one cell stays in that cell",inside,Describe(groups)+" box0="+Fmt(box0)+" box1="+Fmt(box1));
            // One rectangle per physical row, on that row's own text line; the two ends follow
            // the selection (mid row 1 to the end of row 3).
            List<MarkdownLivePreview.CellSlice> current=layout.Slices[0];
            bool geometry=rects.Count==current.Count;
            if(geometry)for(int r=0;r<current.Count;r++){
                double x,y0,y1;
                int at=current[r].Start+Math.Min(current[r].Length/2,Math.Max(0,current[r].Length-1));
                if(!session.CellGeometry(row,0,at,false,out x,out y0,out y1)||Math.Abs(rects[r].Top-y0)>1.5||Math.Abs(rects[r].Bottom-y1)>1.5){geometry=false;break;}
            }
            MarkdownLivePreview.TableRowElement element=session.RowElement(visual);
            Check(tag+" one rectangle per physical row",geometry,Describe(groups)+" lines="+DescribeLines(visual)+" slices="+DescribeSlices(layout,0)+" starts="+(element==null?"-":DescribeStarts(element,0,layout)));
            double sx=0,sy0=0,sy1=0,ex=0,ey0=0,ey1=0;
            bool ends=rects.Count>0&&session.CellGeometry(row,0,textFrom,false,out sx,out sy0,out sy1)&&session.CellGeometry(row,0,textTo,true,out ex,out ey0,out ey1);
            ends&=rects.Count>0&&Math.Abs(rects[0].Left-sx)<=1.5&&Math.Abs(rects[rects.Count-1].Right-ex)<=1.5;
            Check(tag+" rectangle ends follow the selection",ends,rects.Count==0?"no rects":("first x="+Fmt(rects[0].Left)+"/"+Fmt(sx)+", last right="+Fmt(rects[rects.Count-1].Right)+"/"+Fmt(ex)));
            // The reported bug: the stock geometry painted the row to the viewport width.
            bool wide=false;
            if(seg!=null)foreach(Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(view,seg,false))wide|=rect.Right>box0.Right+20;
            Check(tag+" stock geometry painted past the cell",wide,"segment="+(seg!=null)+", box0 right="+Fmt(box0.Right));
            // A reversed drag (anchor at the end) paints exactly the same.
            editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(editor.TextArea,to,from);
            LongLineBench.Idle();
            visual=view.GetOrConstructVisualLine(row);view.UpdateLayout();
            List<Rect> reverse=Flatten(LiveTableSelectionLayer.CollectSelectionRectGroups(view,editor.TextArea.Selection));
            Check(tag+" reverse drag paints the same",SameRects(rects,reverse),Describe(groups)+" reversed="+reverse.Count);
            // The end of cell0 plus the start of cell1: each cell keeps its own rectangles.
            int from3=row.Offset+text0.Map[Math.Max(0,slices[slices.Count-1].Start+slices[slices.Count-1].Length-2)];
            int to3=row.Offset+text1.Map[text1.Map.Length-1];
            Select(editor,from3,to3);
            visual=view.GetOrConstructVisualLine(row);view.UpdateLayout();
            groups=LiveTableSelectionLayer.CollectSelectionRectGroups(view,editor.TextArea.Selection);
            bool split=groups.Count==2;
            if(split){
                foreach(Rect rect in groups[0])split&=rect.Left>=box0.Left-1&&rect.Right<=box0.Right+1;
                foreach(Rect rect in groups[1])split&=rect.Left>=box1.Left-1&&rect.Right<=box1.Right+1;
            }
            Check(tag+" two cells stay in their own boxes",split,Describe(groups)+" box0="+Fmt(box0)+" box1="+Fmt(box1));
            // Paragraph above -> table -> paragraph below: the paragraphs keep the stock
            // rectangles and every table rectangle stays inside a cell.
            int first=document.GetLineByNumber(1).Offset+3,last=document.GetLineByNumber(7).Offset+4;
            ICSharpCode.AvalonEdit.Editing.SelectionSegment whole=Select(editor,first,last-first);
            VisualLine dataLine=view.GetOrConstructVisualLine(row);view.UpdateLayout();
            VisualLine headerLine=view.GetOrConstructVisualLine(document.GetLineByNumber(3));view.UpdateLayout();
            double dataTop=dataLine.VisualTop-view.VerticalOffset;
            double dataBottom=dataTop+dataLine.Height;
            double headerTop=headerLine.VisualTop-view.VerticalOffset;
            double headerBottom=headerTop+headerLine.Height;
            groups=LiveTableSelectionLayer.CollectSelectionRectGroups(view,editor.TextArea.Selection);
            var mineText=new List<Rect>();bool tableInside=true;
            foreach(Rect rect in Flatten(groups)){
                double center=rect.Top+rect.Height/2;
                if(InBand(center,headerTop,headerBottom))tableInside&=InsideAnyCell(rect,session,headerLine);
                else if(InBand(center,dataTop,dataBottom))tableInside&=InsideAnyCell(rect,session,dataLine);
                else mineText.Add(rect);
            }
            var stockText=new List<Rect>();
            if(whole!=null)foreach(Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(view,whole,false)){
                double center=rect.Top+rect.Height/2;
                if(!InBand(center,headerTop,headerBottom)&&!InBand(center,dataTop,dataBottom))stockText.Add(rect);
            }
            Check(tag+" paragraphs paint like stock",SameRects(mineText,stockText),RectDiff(mineText,stockText));
            Check(tag+" every table rectangle is inside a cell",tableInside,Describe(groups));
            frozen=true;
        }finally{session.EndPress();}
        LongLineBench.Idle();
        if(!frozen)return;
        // The user's edit-mode state: the caret is ON the row, so it is revealed as source
        // (Raw) while still being ONE row-major TableRowElement across its wrapped slices. The
        // stock geometry then paints every physical row to the table's right edge, i.e. across
        // the neighbouring cells again, so the row must keep the per-cell path.
        if(session.IsDisposed){Check(tag+" raw session",false,"live preview gone");return;}
        Select(editor,from,to);
        visual=view.GetOrConstructVisualLine(row);view.UpdateLayout();
        info=session.Info(row);
        bool raw=info.Row!=null&&info.Row.Raw;
        layout=info.Row;
        Rect rawBox0=session.TableCellBox(visual,0),rawBox1=session.TableCellBox(visual,1);
        List<List<Rect>> rawGroups=LiveTableSelectionLayer.CollectSelectionRectGroups(view,editor.TextArea.Selection);
        List<Rect> rawRects=Flatten(rawGroups);
        bool rawInside=raw&&layout!=null&&layout.Slices[0].Count>1&&rawBox0.Width>4&&rawRects.Count==layout.Slices[0].Count&&rawGroups.Count==1;
        foreach(Rect rect in rawRects)rawInside&=rect.Left>=rawBox0.Left-1&&rect.Right<=rawBox0.Right+1&&rect.Right<=rawBox1.Left+.5&&rect.Width>.5;
        bool rawStockWide=false;double rawStockRight=0;
        ICSharpCode.AvalonEdit.Editing.SelectionSegment rawSeg=FirstSegment(editor.TextArea.Selection);
        if(rawSeg!=null)foreach(Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(view,rawSeg,false)){rawStockWide|=rect.Right>rawBox1.Left+2;if(rect.Right>rawStockRight)rawStockRight=rect.Right;}
        Check(tag+" revealed (caret) row paints per cell",rawInside,Describe(rawGroups)+" raw="+raw+", box0="+Fmt(rawBox0)+" box1="+Fmt(rawBox1)+", stockWide="+rawStockWide);
        // Premise only (informational): the per-cell layer clips raw rows regardless of what the stock builder does.
        // Since the raw caret-line padding became real space-width stops, the stock rects for this selection may
        // end inside box0; whether they overshoot is recorded, the real guarantees are the two checks around it.
        Check(tag+" revealed (caret) row stock geometry reference (informational)",rawBox1.Width>4,
            "stockWide="+rawStockWide+", stock segment="+(rawSeg!=null)+", box0 right="+Fmt(rawBox0.Right)+", box1 left="+Fmt(rawBox1.Left)+", stock right="+Fmt(rawStockRight));
        // The end of the revealed row's first cell plus the second cell: each cell keeps its own
        // rectangles, so neither selection paints the other cell.
        if(layout!=null&&layout.Texts.Length>1&&layout.Texts[0]!=null&&layout.Texts[1]!=null&&layout.Slices[0].Count>0){
            MarkdownLivePreview.CellText rawText0=layout.Texts[0],rawText1=layout.Texts[1];
            List<MarkdownLivePreview.CellSlice> rawSlices0=layout.Slices[0];
            MarkdownLivePreview.CellSlice rawLast=rawSlices0[rawSlices0.Count-1];
            int rawFrom=row.Offset+rawText0.Map[Math.Max(0,Math.Min(rawText0.Map.Length-1,rawLast.Start+rawLast.Length-2))];
            int rawTo=row.Offset+rawText1.Map[rawText1.Map.Length-1];
            Select(editor,rawFrom,rawTo);
            visual=view.GetOrConstructVisualLine(row);view.UpdateLayout();
            info=session.Info(row);
            rawBox0=session.TableCellBox(visual,0);rawBox1=session.TableCellBox(visual,1);
            rawGroups=LiveTableSelectionLayer.CollectSelectionRectGroups(view,editor.TextArea.Selection);
            bool rawSplit=rawGroups.Count==2&&rawBox0.Width>4&&rawBox1.Width>4;
            if(rawSplit){
                foreach(Rect rect in rawGroups[0])rawSplit&=rect.Left>=rawBox0.Left-1&&rect.Right<=rawBox0.Right+1;
                foreach(Rect rect in rawGroups[1])rawSplit&=rect.Left>=rawBox1.Left-1&&rect.Right<=rawBox1.Right+1;
            }
            Check(tag+" revealed (caret) two cells stay in their own boxes",rawSplit,Describe(rawGroups)+" box0="+Fmt(rawBox0)+" box1="+Fmt(rawBox1));
        }
        // Empty cell: a row selection never paints into it.
        editor.Text="앞 문단\n\n| heading | second |\n| --- | --- |\n| some text |  |\n\n뒤 문단\n";
        editor.CaretIndex=0;LongLineBench.Idle();view.UpdateLayout();
        document=editor.Document;var emptyRow=document.GetLineByNumber(5);
        editor.Select(document.GetLineByNumber(1).Offset,0);LongLineBench.Idle();
        session.BeginPress();
        try{
            info=session.Info(emptyRow);
            if(info.Cells==null||info.Cells.Count<2){Check(tag+" empty-cell fixture",false,"cells="+(info.Cells==null?-1:info.Cells.Count));return;}
            int emptyFrom=emptyRow.Offset+info.Cells[0].Start;
            Select(editor,emptyFrom,emptyRow.Offset+info.Cells[1].End);
            VisualLine emptyVisual=view.GetOrConstructVisualLine(emptyRow);view.UpdateLayout();
            Rect emptyBox=session.TableCellBox(emptyVisual,1);
            List<List<Rect>> emptyGroups=LiveTableSelectionLayer.CollectSelectionRectGroups(view,editor.TextArea.Selection);
            List<Rect> emptyRects=Flatten(emptyGroups);
            bool clear=emptyRects.Count>0&&emptyBox.Width>4;
            foreach(Rect rect in emptyRects)clear&=rect.Right<=emptyBox.Left+1;
            Check(tag+" empty cell stays unpainted",clear,Describe(emptyGroups)+" empty box="+Fmt(emptyBox));
        }finally{session.EndPress();}
        LongLineBench.Idle();
    }
    static string RectDiff(List<Rect> mine,List<Rect> stock){
        int count=Math.Min(mine.Count,stock.Count);
        for(int i=0;i<count;i++){
            if(Math.Abs(mine[i].Left-stock[i].Left)>1.5||Math.Abs(mine[i].Top-stock[i].Top)>1.5||
               Math.Abs(mine[i].Right-stock[i].Right)>1.5||Math.Abs(mine[i].Bottom-stock[i].Bottom)>1.5)
                return "first diff "+i+": mine="+Fmt(mine[i])+" stock="+Fmt(stock[i]);
        }
        return mine.Count==stock.Count?"same":("count mine="+mine.Count+" stock="+stock.Count);
    }
    static ICSharpCode.AvalonEdit.Editing.SelectionSegment Select(SourceEditor editor,int offset,int end){
        int length=end>offset?end-offset:0;if(end<offset)offset=end;
        editor.Select(offset,length);LongLineBench.Idle();
        return FirstSegment(editor.TextArea.Selection);
    }
    static ICSharpCode.AvalonEdit.Editing.SelectionSegment FirstSegment(ICSharpCode.AvalonEdit.Editing.Selection selection){
        if(selection==null)return null;
        foreach(ICSharpCode.AvalonEdit.Editing.SelectionSegment segment in selection.Segments)if(segment!=null)return segment;
        return null;
    }
    static List<Rect> Flatten(List<List<Rect>> groups){
        var rects=new List<Rect>();
        foreach(List<Rect> group in groups)foreach(Rect rect in group)rects.Add(rect);
        return rects;
    }
    static bool SameRects(List<Rect> mine,List<Rect> stock){
        if(mine.Count!=stock.Count)return false;
        for(int i=0;i<mine.Count;i++){
            if(Math.Abs(mine[i].Left-stock[i].Left)>1.5||Math.Abs(mine[i].Top-stock[i].Top)>1.5||
               Math.Abs(mine[i].Right-stock[i].Right)>1.5||Math.Abs(mine[i].Bottom-stock[i].Bottom)>1.5)return false;
        }
        return true;
    }
    static bool InBand(double value,double top,double bottom){return value>=top-1&&value<=bottom+1;}
    static string DescribeLines(VisualLine line){
        var text=new StringBuilder();
        int at=0;
        for(int i=0;i<line.TextLines.Count;i++){
            var tl=line.TextLines[i];
            if(i>0)text.Append(' ');
            text.Append(at).Append('+').Append(tl.Length).Append('@').Append(Fmt(line.GetTextLineVisualYPosition(tl,VisualYPosition.LineTop))).Append('/').Append(Fmt(tl.Height)).Append(" tws=").Append(tl.TrailingWhitespaceLength);
            at+=tl.Length;
        }
        return text.ToString();
    }
    static string DescribeSlices(MarkdownLivePreview.RowLayout layout,int cell){
        var text=new StringBuilder();
        List<MarkdownLivePreview.CellSlice> slices=layout.Slices[cell];
        for(int i=0;i<slices.Count;i++){if(i>0)text.Append(' ');text.Append(slices[i].Start).Append('+').Append(slices[i].Length);}
        return text.ToString();
    }
    static string DescribeStarts(MarkdownLivePreview.TableRowElement row,int cell,MarkdownLivePreview.RowLayout layout){
        var text=new StringBuilder();
        List<MarkdownLivePreview.CellSlice> slices=layout.Slices[cell];
        for(int i=0;i<slices.Count;i++){if(i>0)text.Append(' ');text.Append(row.SliceStartColumn(cell,i));}
        return text.ToString();
    }
    static bool InsideAnyCell(Rect rect,MarkdownLivePreview.Session session,VisualLine line){
        MarkdownLivePreview.LineInfo info=session.Info(line.FirstDocumentLine);
        if(info.Cells==null)return true;
        for(int c=0;c<info.Cells.Count;c++){
            Rect box=session.TableCellBox(line,c);
            if(box.Width<=0)continue;
            if(rect.Left>=box.Left-1&&rect.Right<=box.Right+1)return true;
        }
        return false;
    }
    static string Describe(List<List<Rect>> groups){
        var text=new StringBuilder();
        for(int g=0;g<groups.Count;g++){
            if(g>0)text.Append(" | ");
            for(int i=0;i<groups[g].Count;i++){if(i>0)text.Append(' ');text.Append('[').Append(Fmt(groups[g][i])).Append(']');}
        }
        return text.ToString();
    }
    static string Fmt(Rect rect){return Fmt(rect.Left)+","+Fmt(rect.Top)+".."+Fmt(rect.Right)+","+Fmt(rect.Bottom);}
    // The layer replaces the stock selection layer while attached and puts the same instance
    // back on detach (the stock layer's in-place Replace cannot be used: the WPF visual
    // collection rejects a set into an occupied index).
    static void RunSelectionLayerSwap(){
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try{
            editor=new SourceEditor{FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=new Window{Left=-20000,Top=0,Width=700,Height=500,ShowInTaskbar=false,ShowActivated=false,WindowStyle=WindowStyle.None,Content=editor};
            window.Show();LongLineBench.Idle();
            var view=editor.TextArea.TextView;
            int stockBefore=CountSelectionLayers(view),mineBefore=CountOwnLayers(view);
            session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings{FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
            int stockDuring=CountSelectionLayers(view),mineDuring=CountOwnLayers(view);
            session.Dispose();session=null;
            int stockAfter=CountSelectionLayers(view),mineAfter=CountOwnLayers(view);
            Check("selection layer swap",stockBefore==1&&mineBefore==0&&stockDuring==0&&mineDuring==1&&stockAfter==1&&mineAfter==0,
                "before="+stockBefore+"/"+mineBefore+", during="+stockDuring+"/"+mineDuring+", after="+stockAfter+"/"+mineAfter);
        }catch(Exception ex){Check("selection layer swap exception",false,ex.ToString());}
        finally{if(session!=null)session.Dispose();if(window!=null)window.Close();}
    }
    static int CountSelectionLayers(TextView view){
        int count=0;
        foreach(UIElement layer in view.Layers)if(layer!=null&&layer.GetType().Name=="SelectionLayer")count++;
        return count;
    }
    static int CountOwnLayers(TextView view){
        int count=0;
        foreach(UIElement layer in view.Layers)if(layer is LiveTableSelectionLayer)count++;
        return count;
    }
    // Feedback 11 in the product's own edit state: inside MainWindow the caret is on the row
    // being typed into, so that row is revealed as source (Raw) while the selection layer still
    // sees ONE row-major TableRowElement across its wrapped physical rows. A selection inside
    // the first cell must stay inside it, exactly like a rendered (non-caret) row.
    static void RunEditModeSelectionSuite(int width){
        MainWindow window=null;
        try{
            window=CreateWindow(width);
            MarkdownLivePreview.Session session=window.livePreview;
            if(session==null){Check("edit-mode selection attached",false,"no live preview");return;}
            var document=window.source.Document;
            window.SetText("앞 문단\n\n| 첫 머리 | 둘째 머리 |\n| --- | --- |\n| "+
                "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi rho sigma tau upsilon | b |\n\n뒤 문단\n");
            window.SetMode(true);LongLineBench.Idle();window.UpdateLayout();
            ICSharpCode.AvalonEdit.Document.DocumentLine row=null;
            for(int n=1;n<=document.LineCount;n++){
                var info=session.Info(document.GetLineByNumber(n));
                if(info.Table&&!info.Delimiter&&info.Cells!=null&&info.Cells.Count>=2)row=document.GetLineByNumber(n);
            }
            if(row==null){Check("edit-mode selection row",false,"no data row");return;}
            MarkdownLivePreview.LineInfo rowInfo=session.Info(row);
            // Select inside the FIRST cell and leave the caret at the selection end: that makes
            // this very row the caret row, i.e. the state the user drags in.
            int from=row.Offset+rowInfo.Cells[0].Start+2,to=row.Offset+rowInfo.Cells[0].End;
            window.source.Select(from,to-from);
            LongLineBench.Idle();window.UpdateLayout();
            rowInfo=session.Info(row);
            VisualLine visual=session.View.GetOrConstructVisualLine(row);window.UpdateLayout();
            var layout=rowInfo.Row;
            bool raw=layout!=null&&layout.Raw&&layout.Slices[0]!=null&&layout.Slices[0].Count>1&&layout.Texts.Length>1&&layout.Texts[1]!=null;
            Rect box0=EditModeCellBox(session,visual,0),box1=EditModeCellBox(session,visual,1);
            List<Rect> rects=Flatten(LiveTableSelectionLayer.CollectSelectionRectGroups(session.View,window.source.TextArea.Selection));
            bool inside=raw&&box0.Width>4&&rects.Count>0;
            foreach(Rect rect in rects)inside&=rect.Left>=box0.Left-1&&rect.Right<=box0.Right+1&&rect.Top>=box0.Top-1.5&&rect.Bottom<=box0.Bottom+1.5;
            Check("edit-mode selection on the caret row stays in its cell",inside,
                "raw="+raw+", rects="+rects.Count+", box0="+Fmt(box0)+" box1="+Fmt(box1)+", slices="+(layout==null||layout.Slices[0]==null?0:layout.Slices[0].Count)+", "+DescribeRects(rects));
        }catch(Exception ex){Check("edit-mode selection suite",false,ex.ToString());}
        finally{CloseWindow(window);}
    }
    // Stage 4: editing inside one live table cell. The caret row is revealed (raw) while it is
    // edited, so every edit is checked on that row's source; the row is then left (caret off it)
    // to prove the rendered projection turns the "<br>" into one cell line break. Each edit is one
    // history entry, so one undo restores the exact source.
    static void RunCellEditingSuite(int width){
        MainWindow window=null;
        try{
            window=CreateWindow(width);
            RunCellEditing(window,width);
        }catch(Exception ex){Check("cell editing suite "+width,false,ex.ToString());}
        finally{CloseWindow(window);}
    }
    static string CellFixture(){return "before\n\n| a | b |\n| --- | --- |\n| c | d |\n\nafter\n";}
    // The real key path a probe can raise: PreviewKeyDown (SourceStructuralKeys) and, when that
    // did not take the key, KeyDown so AvalonEdit's own command handles it. The return value is
    // the structural handler's answer, i.e. whether the cell rule took the key.
    static bool PressLiveKey(MainWindow window,System.Windows.Input.Key key){
        var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window.source),0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        window.source.TextArea.RaiseEvent(args);
        bool handled=args.Handled;
        LongLineBench.Idle();
        if(!handled){
            var down=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window.source),0,key){RoutedEvent=Keyboard.KeyDownEvent};
            window.source.TextArea.RaiseEvent(down);
            LongLineBench.Idle();
        }
        return handled;
    }
    // 11-08 user (video 205745): in a cell holding a sized span, text typed after the span ("테스트" at 40px, then "니거 ")
    // did not go with Backspace.
    static void SizedCellBackspace(MainWindow window,string tag){
        string span="<span style=\"font-size:40px\">테스트</span>";
        string fixture="before\n\n| a | b |\n| --- | --- |\n| "+span+"니거  | d |\n\nafter\n";
        window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();
        var row=window.source.Document.GetLineByNumber(5);
        int caret=row.Offset+window.source.Document.GetText(row).IndexOf("니거 ",StringComparison.Ordinal)+3;
        window.source.Select(caret,0);LongLineBench.Idle();
        var steps=new StringBuilder();
        for(int press=0;press<3;press++){PressLiveKey(window,Key.Back);steps.Append(" [").Append(FlatText(window.source.Document.GetText(window.source.Document.GetLineByNumber(5)))).Append("]@").Append(window.source.CaretIndex);}
        string expected="| "+span+" | d |";
        Check(tag+" Backspace deletes text typed after a sized span in a cell",window.source.Document.GetText(window.source.Document.GetLineByNumber(5))==expected,steps.ToString());
    }
    // 11-08 user (video 214334): a size picked in a cell before typing left the drawn caret body-sized.
    static void PendingSizeCellCaret(MainWindow window,string tag){
        window.SetText(CellFixture());window.SetMode(true);LongLineBench.Idle();
        var row=window.source.Document.GetLineByNumber(5);var info=window.livePreview.Info(row);
        window.source.Select(row.Offset+info.Cells[0].End,0);window.source.Focus();LongLineBench.Idle();
        window.livePreview.UpdateTableCaretNowForProbe();
        Rect before=window.livePreview.TableCaretPainted();
        window.SetFontSizeForProbe(40);LongLineBench.Idle();
        window.livePreview.UpdateTableCaretNowForProbe();
        Rect after=window.livePreview.TableCaretPainted();
        Check(tag+" a size picked in a cell sizes the drawn caret before typing",!before.IsEmpty&&!after.IsEmpty&&after.Height>before.Height*1.8&&Math.Abs(after.Bottom-before.Bottom)<1.5,
            "before="+before+" after="+after+" text=["+FlatText(window.text)+"]");
        window.SetText(CellFixture());LongLineBench.Idle();
    }
    static void RunCellEditing(MainWindow window,int width){
        string tag="live cell editing "+width;
        if(window.livePreview==null){Check(tag+" attached",false,"no live preview");return;}
        SizedCellBackspace(window,tag);
        PendingSizeCellCaret(window,tag);
        string fixture=CellFixture();
        // Enter on a caret row: one "<br>" inside the cell, never a new source line.
        window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();
        var document=window.source.Document;var row=document.GetLineByNumber(5);var info=window.livePreview.Info(row);
        int start=row.Offset+info.Cells[0].Start;
        window.source.Select(start+1,0);LongLineBench.Idle();
        bool handled=PressLiveKey(window,Key.Enter);
        string entered=fixture.Replace("| c | d |","| c<br> | d |");
        Check(tag+" Enter in a cell writes <br>",handled&&window.text==entered&&window.source.CaretIndex==start+5&&window.source.Document.LineCount==8,
            "handled="+handled+", text=["+FlatText(window.text)+"], caret="+window.source.CaretIndex+"/"+(start+5)+", lines="+window.source.Document.LineCount);
        // The rendered row (caret elsewhere) shows one cell line break and keeps its two cells.
        document=window.source.Document;
        window.source.Select(0,0);LongLineBench.Idle();
        MarkdownLivePreview.LineInfo rendered=window.livePreview.Info(document.GetLineByNumber(5));
        VisualLine visual=window.livePreview.View.GetOrConstructVisualLine(document.GetLineByNumber(5));
        rendered=window.livePreview.Info(document.GetLineByNumber(5));
        var layout=rendered.Row;
        bool showsBreak=layout!=null&&!layout.Raw&&layout.Texts[0]!=null&&layout.Texts[0].Breaks!=null&&layout.Slices[0]!=null&&layout.Slices[0].Count==2;
        Check(tag+" rendered row shows the cell break",showsBreak,
            "raw="+(layout==null?"null":layout.Raw.ToString())+", slices="+(layout==null||layout.Slices[0]==null?0:layout.Slices[0].Count)+", text=["+document.GetText(document.GetLineByNumber(5))+"]");
        Check(tag+" rendered row keeps its two cells",rendered.Cells!=null&&rendered.Cells.Count==2&&PipeCount(document.GetText(document.GetLineByNumber(5)))==3,
            "cells="+(rendered.Cells==null?-1:rendered.Cells.Count)+", row=["+document.GetText(document.GetLineByNumber(5))+"]");
        window.Undo();LongLineBench.Idle();
        Check(tag+" one undo restores the Enter edit",window.text==fixture&&window.source.Document.LineCount==8,"text=["+FlatText(window.text)+"]");
        // Shift+Enter reaches the same rule through the editor's newline text path (OnEnter runs
        // PerformTextInput("\n") for both Enter keys).
        window.SetText(fixture);LongLineBench.Idle();
        document=window.source.Document;row=document.GetLineByNumber(5);info=window.livePreview.Info(row);
        start=row.Offset+info.Cells[1].Start;
        window.source.Select(start+1,0);LongLineBench.Idle();
        window.source.TextArea.PerformTextInput("\n");LongLineBench.Idle();
        string soft=fixture.Replace("| c | d |","| c | d<br> |");
        Check(tag+" Shift+Enter in a cell writes <br>",window.text==soft&&window.source.CaretIndex==start+5&&window.source.Document.LineCount==8,
            "text=["+FlatText(window.text)+"], caret="+window.source.CaretIndex+"/"+(start+5)+", lines="+window.source.Document.LineCount);
        window.Undo();LongLineBench.Idle();
        Check(tag+" one undo restores the Shift+Enter edit",window.text==fixture,"text=["+FlatText(window.text)+"]");
        // Backspace at a cell's start moves into the previous cell instead of deleting a "|".
        window.SetText(fixture);LongLineBench.Idle();
        document=window.source.Document;row=document.GetLineByNumber(5);info=window.livePreview.Info(row);
        int previousEnd=row.Offset+info.Cells[0].End;
        int cellStart=row.Offset+info.Cells[1].Start;
        window.source.Select(cellStart,0);LongLineBench.Idle();
        handled=PressLiveKey(window,Key.Back);
        Check(tag+" Backspace at a cell start moves to the previous cell end",handled&&window.text==fixture&&window.source.CaretIndex==previousEnd,
            "handled="+handled+", text=["+FlatText(window.text)+"], caret="+window.source.CaretIndex+"/"+previousEnd);
        PressLiveKey(window,Key.Back);
        string deleted=fixture.Replace("| c | d |","|  | d |");
        Check(tag+" the next Backspace deletes inside the previous cell",window.text==deleted&&PipeCount(window.source.Document.GetText(window.source.Document.GetLineByNumber(5)))==3,
            "text=["+FlatText(window.text)+"]");
        window.Undo();LongLineBench.Idle();
        // Backspace at the first cell's start is swallowed: the row never joins the line above.
        window.SetText(fixture);LongLineBench.Idle();
        document=window.source.Document;row=document.GetLineByNumber(5);info=window.livePreview.Info(row);
        int firstStart=row.Offset+info.Cells[0].Start;
        window.source.Select(firstStart,0);LongLineBench.Idle();
        handled=PressLiveKey(window,Key.Back);
        Check(tag+" Backspace at the first cell start changes nothing",handled&&window.text==fixture&&window.source.CaretIndex==firstStart&&window.source.Document.LineCount==8,
            "handled="+handled+", text=["+FlatText(window.text)+"], caret="+window.source.CaretIndex+"/"+firstStart+", lines="+window.source.Document.LineCount);
        // Delete at a cell's end is a wall: the caret stays, so a run of presses can never step
        // into the next cell and eat its first character. The last cell's end is the same wall.
        window.SetText(fixture);LongLineBench.Idle();
        document=window.source.Document;row=document.GetLineByNumber(5);info=window.livePreview.Info(row);
        int end0=row.Offset+info.Cells[0].End,end1=row.Offset+info.Cells[1].End;
        window.source.Select(end0,0);LongLineBench.Idle();
        bool stayed=true;
        for(int press=0;press<3;press++){
            handled=PressLiveKey(window,Key.Delete);
            if(!handled||window.text!=fixture||window.source.CaretIndex!=end0) stayed=false;
        }
        Check(tag+" three Deletes at a cell end keep the caret and the row",stayed&&window.text==fixture&&window.source.CaretIndex==end0,
            "handled="+handled+", text=["+FlatText(window.text)+"], caret="+window.source.CaretIndex+"/"+end0);
        window.source.Select(end1,0);LongLineBench.Idle();
        handled=PressLiveKey(window,Key.Delete);
        Check(tag+" Delete at the last cell end does nothing",handled&&window.text==fixture&&window.source.CaretIndex==end1,
            "handled="+handled+", text=["+FlatText(window.text)+"], caret="+window.source.CaretIndex+"/"+end1);
        // A cell "<br>" is one unit for both keys.
        string brFixture="before\n\n| a | b |\n| --- | --- |\n| c<br>d | e |\n\nafter\n";
        string noBreak=brFixture.Replace("| c<br>d | e |","| cd | e |");
        window.SetText(brFixture);LongLineBench.Idle();
        document=window.source.Document;row=document.GetLineByNumber(5);
        int tagAt=document.GetText(row).IndexOf("<br>",StringComparison.Ordinal);
        window.source.Select(row.Offset+tagAt,0);LongLineBench.Idle();
        handled=PressLiveKey(window,Key.Delete);
        Check(tag+" Delete removes a whole <br>",handled&&window.text==noBreak,"handled="+handled+", text=["+FlatText(window.text)+"]");
        window.Undo();LongLineBench.Idle();
        document=window.source.Document;row=document.GetLineByNumber(5);
        tagAt=document.GetText(row).IndexOf("<br>",StringComparison.Ordinal);
        window.source.Select(row.Offset+tagAt+4,0);LongLineBench.Idle();
        handled=PressLiveKey(window,Key.Back);
        Check(tag+" Backspace removes a whole <br>",handled&&window.text==noBreak,"handled="+handled+", text=["+FlatText(window.text)+"]");
        window.Undo();LongLineBench.Idle();
        Check(tag+" one undo restores the <br>",window.text==brFixture,"text=["+FlatText(window.text)+"]");
        // Tab/Shift+Tab stay cell navigation. The handler walks the real path (SourceTab), which
        // skips the |---| row and declines outside a cell's text; Tab in the last cell of the last
        // row is the one edit, and it appends one empty row below the table.
        window.SetText(fixture);LongLineBench.Idle();
        document=window.source.Document;row=document.GetLineByNumber(5);info=window.livePreview.Info(row);
        int linesBefore=document.LineCount;
        window.source.Select(row.Offset+info.Cells[0].Start,0);LongLineBench.Idle();
        bool tabbed=window.SourceTabPathProbe(false);LongLineBench.Idle();
        var after=window.livePreview.Info(window.source.Document.GetLineByNumber(5));
        Check(tag+" Tab selects the next cell",tabbed&&window.source.SelectionStart==row.Offset+after.Cells[1].Start&&window.source.SelectionLength==after.Cells[1].End-after.Cells[1].Start&&window.text==fixture,
            "tabbed="+tabbed+", selection="+window.source.SelectionStart+"/"+window.source.SelectionLength+", text=["+FlatText(window.text)+"]");
        bool backTab=window.SourceTabPathProbe(true);LongLineBench.Idle();
        Check(tag+" Shift+Tab selects the previous cell",backTab&&window.source.SelectionStart==row.Offset+after.Cells[0].Start&&window.source.SelectionLength==after.Cells[0].End-after.Cells[0].Start&&window.text==fixture,
            "tabbed="+backTab+", selection="+window.source.SelectionStart+"/"+window.source.SelectionLength+", text=["+FlatText(window.text)+"]");
        // The |---| row is never a cell target: Tab from the header's last cell goes to the first
        // body cell, and Shift+Tab from the body's first cell to the header's last cell.
        var header=window.source.Document.GetLineByNumber(3);var headerInfo=window.livePreview.Info(header);
        window.source.Select(header.Offset+headerInfo.Cells[1].Start,0);LongLineBench.Idle();
        bool overTab=window.SourceTabPathProbe(false);LongLineBench.Idle();
        Check(tag+" Tab steps over the delimiter row",overTab&&window.source.SelectionStart==row.Offset+after.Cells[0].Start&&window.source.SelectionLength==after.Cells[0].End-after.Cells[0].Start&&window.text==fixture,
            "tabbed="+overTab+", selection="+window.source.SelectionStart+"/"+window.source.SelectionLength+", text=["+FlatText(window.text)+"]");
        window.source.Select(row.Offset+after.Cells[0].Start,0);LongLineBench.Idle();
        bool overBack=window.SourceTabPathProbe(true);LongLineBench.Idle();
        Check(tag+" Shift+Tab steps over the delimiter row",overBack&&window.source.SelectionStart==header.Offset+headerInfo.Cells[1].Start&&window.source.SelectionLength==headerInfo.Cells[1].End-headerInfo.Cells[1].Start&&window.text==fixture,
            "tabbed="+overBack+", selection="+window.source.SelectionStart+"/"+window.source.SelectionLength+", text=["+FlatText(window.text)+"]");
        // The table's end appends one empty row (Word/Typora) with the table's two columns, outer
        // pipes written, and puts the caret in its first cell; one undo takes the row back.
        window.source.Select(row.Offset+after.Cells[1].End,0);LongLineBench.Idle();
        int endCaret=window.source.CaretIndex;
        bool endTab=window.SourceTabPathProbe(false);LongLineBench.Idle();
        string appended=fixture.Replace("| c | d |\n","| c | d |\n|  |  |\n");
        document=window.source.Document;
        var appendedLine=document.GetLineByNumber(6);
        var appendedInfo=window.livePreview.Info(appendedLine);
        Check(tag+" Tab at the end of the table appends one empty row",
            endTab&&window.text==appended&&document.LineCount==linesBefore+1&&
            appendedInfo!=null&&appendedInfo.Cells!=null&&appendedInfo.Cells.Count==2&&
            window.source.CaretIndex==appendedLine.Offset+2,
            "tabbed="+endTab+", caret="+window.source.CaretIndex+"/"+(appendedLine.Offset+2)+", text=["+FlatText(window.text)+"], lines="+document.LineCount+", cells="+(appendedInfo==null||appendedInfo.Cells==null?-1:appendedInfo.Cells.Count)+", caret before="+endCaret);
        window.Undo();LongLineBench.Idle();
        Check(tag+" one undo takes the appended row back",
            window.text==fixture&&window.source.Document.LineCount==linesBefore,
            "text=["+FlatText(window.text)+"], lines="+window.source.Document.LineCount);
    }
    // A paste at a caret inside a cell lands where the caret is. The user types the backtick that
    // closes a `code` span and pastes plain text straight after it: the pair keeps its closing run
    // and the pasted text goes after it ("`a`X", never "`aX`"). The hidden-marker paste snap pulls
    // a caret on a closing run's outer edge back inside the pair so pasted text keeps the style,
    // but a cell is the input unit (H10 gives typed text the same exemption), so the cell's caret
    // wins. Both views run: basic hides the markers, live reveals the caret row as source.
    static void RunCellPasteSuite(int width){
        bool hide=hideMarkers,live=ProbeDefaults.Live;
        MainWindow window=null;
        try{
            ProbeDefaults.Live=true;
            foreach(bool formatted in new[]{true,false}){
                hideMarkers=formatted;
                window=new MainWindow{ShowInTaskbar=false,ShowActivated=false,Width=width,Height=760};
                window.Show();window.textMode=false;
                string mode=formatted?"basic":"live";
                RunCellPaste(window,width,mode,"a","","X");
                RunCellPaste(window,width,mode,"바지","","다음");
                RunCellPaste(window,width,mode,"바지","日本語","X");
                CloseWindow(window);window=null;
            }
        }catch(Exception ex){Check("cell paste suite "+width,false,ex.ToString());}
        finally{hideMarkers=hide;ProbeDefaults.Live=live;if(window!=null)CloseWindow(window);}
    }
    // "`before after" with the caret after `before`; one typed backtick closes the span there and
    // the paste goes right after that backtick, before `after` (which is empty for a cell end).
    static void RunCellPaste(MainWindow window,int width,string mode,string before,string after,string pasted){
        string tag="cell paste "+mode+" "+width+" [`"+before+"|"+after+"]";
        string fixture="| h | i |\n| --- | --- |\n| `"+before+after+" | 끝 |\n";
        string expected="| `"+before+"`"+pasted+after+" | 끝 |";
        window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();
        if(!window.LiveFormat){Check(tag,false,"the live source surface is not active");return;}
        var document=window.source.Document;var line=document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info=window.livePreview==null?null:window.livePreview.Info(line);
        if(info==null||info.Cells==null||info.Cells.Count<2){Check(tag,false,"table fixture did not parse as two cells");return;}
        int at=line.Offset+info.Cells[0].Start+1+before.Length;
        PutCaret(window,at);
        window.source.TextArea.PerformTextInput("`");LongLineBench.Idle();
        document=window.source.Document;line=document.GetLineByNumber(3);
        string typed=document.GetText(line);
        int afterType=window.source.CaretOffset;
        var data=new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText,pasted);
        window.PasteData(data,true);window.FlushDocumentChanges();LongLineBench.Idle();
        document=window.source.Document;line=document.GetLineByNumber(3);
        string row=document.GetText(line);
        int wantCaret=at+1+pasted.Length;
        Check(tag,row==expected&&window.source.CaretOffset==wantCaret,
            "typed=["+FlatText(typed)+"], row=["+FlatText(row)+"] want=["+FlatText(expected)+"], caret="+window.source.CaretOffset+"/"+wantCaret+", afterType="+afterType);
    }
    // TS1-TS5: one Space typed at the end of a cell (after Hangul/CJK text, optionally right after
    // an IME commit) must leave the caret inside that cell. Candidate causes the detail text
    // separates: (1) raw caret-line mode puts the caret in the cell padding / next cell column,
    // (2) a stale imeCommitPending redirects the Space to an old anchor (see "ime=" in the detail).
    static void RunSpaceAtCellEndSuite(int width){
        bool hide=hideMarkers,live=ProbeDefaults.Live;
        try{
            ProbeDefaults.Live=true;
            RunSpaceCase("TS1 raw cell0 Space",false,0,false);
            RunSpaceCase("TS2 raw last cell Space",false,1,false);
            RunSpaceCase("TS3 hidden cell0 Space (control)",true,0,false);
            RunSpaceCase("TS4 raw cell0 IME commit then Space",false,0,true);
            RunSpaceCase("TS5 raw last cell IME commit then Space",false,1,true);
            RunSpaceCase("TS4b hidden cell0 IME commit then Space",true,0,true);
            RunSpaceCase("TS5b hidden last cell IME commit then Space",true,1,true);
        }catch(Exception ex){Check("space at cell end suite",false,ex.ToString());}
        finally{hideMarkers=hide;ProbeDefaults.Live=live;}
    }
    static double CaretX(MainWindow window,int lineNumber,int visualColumn){
        var line=window.source.Document.GetLineByNumber(lineNumber);
        VisualLine visual=window.livePreview.View.GetOrConstructVisualLine(line);
        return visual.GetVisualPosition(visualColumn,VisualYPosition.LineMiddle).X;
    }
    // The stock caret rectangle in TextView coordinates: what is still drawn when the session
    // does not draw a table caret. Only its x is compared by the caret suites.
    static Rect StockCaretRect(MainWindow window,VisualLine visual){
        var session=window.livePreview;
        if(visual==null||session==null)return Rect.Empty;
        int column=window.source.TextArea.Caret.Position.VisualColumn;
        Point point=visual.GetVisualPosition(column,VisualYPosition.TextTop);
        return new Rect(point.X-session.View.HorizontalOffset,point.Y-session.View.VerticalOffset,Math.Max(1,SystemParameters.CaretWidth),Math.Max(1,visual.Height));
    }
    // The caret the user actually sees on this line: the drawn table caret when the session
    // owns it, otherwise the stock position.
    static Rect VisibleCaretRect(MainWindow window,VisualLine visual){
        var session=window.livePreview;
        if(session==null)return Rect.Empty;
        Rect drawn=session.DrawnTableCaret;
        return drawn.IsEmpty?StockCaretRect(window,visual):drawn;
    }
    static void RunSpaceCase(string name,bool hide,int cell,bool ime){
        MainWindow window=null;
        string tag="space-at-cell-end "+name;
        try{
            Progress(name+" begin");
            hideMarkers=hide;
            window=new MainWindow{ShowInTaskbar=false,ShowActivated=false,Width=900,Height=760};
            window.Show();window.textMode=false;
            string body=ime?"| 가 | 나 |":"| 가中文 | 나 |";
            window.SetText("| h | i |\n|---|---|\n"+body);
            window.SetMode(true);SafeIdle(tag,"open");window.UpdateLayout();Progress(name+" opened");
            var document=window.source.Document;var line=document.GetLineByNumber(3);
            string original=document.GetText(line);
            int end=CellEnd(window,line,cell);
            int relEnd=end-line.Offset;
            PutCaret(window,end);
            string inserted=(ime?"中文":"")+" ";
            string expected=original.Insert(relEnd,inserted);
            string state="";
            if(ime){
                window.source.SimulateImeCommit(2);
                Progress(name+" before IME input");
                window.source.TextArea.PerformTextInput("中文");SafeIdle(tag,"ime");
                state=" imeAfterCommit=["+window.source.CompositionState()+"]";
            }
            Progress(name+" before Space");
            window.source.TextArea.PerformTextInput(" ");SafeIdle(tag,"space");
            Progress(name+" after Space");
            window.UpdateLayout();
            document=window.source.Document;line=document.GetLineByNumber(3);
            string row=document.GetText(line);
            int caret=window.source.CaretOffset;
            int want=end+inserted.Length;
            var info=window.livePreview.Info(line);
            int cellsCount=info.Cells==null?-1:info.Cells.Count;
            int cellEnd=cellsCount>cell?line.Offset+info.Cells[cell].End:-1;
            // The "next" slot: the following cell's start, or the end of the row for the last cell.
            int nextAt=cellsCount>cell+1?line.Offset+info.Cells[cell+1].Start:line.EndOffset;
            int caretColumn=window.source.TextArea.Caret.Position.VisualColumn;
            VisualLine visual=window.livePreview.View.GetOrConstructVisualLine(line);
            int nextColumn=visual.GetVisualColumn(nextAt-line.Offset);
            double caretX=CaretX(window,3,caretColumn),nextX=CaretX(window,3,nextColumn);
            var session=window.livePreview;
            Rect drawn=session.DrawnTableCaret;
            // The caret the user sees on this row: the drawn one when the session owns it.
            Rect visible=VisibleCaretRect(window,visual);
            Rect box=session.TableCellBox(visual,cell);
            double nextXView=nextX-session.View.HorizontalOffset;
            bool inBox=box.Width>0&&!visible.IsEmpty&&visible.Left>=box.Left-0.6&&visible.Right<=box.Right+0.6;
            bool notNext=caret!=nextAt&&Math.Abs(visible.Left-nextXView)>0.5;
            bool ok=row==expected&&caret==want&&notNext&&inBox;
            string cells="";
            if(info.Cells!=null){for(int c=0;c<info.Cells.Count;c++)cells+=(c==0?"":",")+info.Cells[c].Start+".."+info.Cells[c].End;}
            Check(tag,ok,"hide="+hide+", raw="+(info.Row!=null&&info.Row.Raw)+", row=["+FlatText(row)+"] want=["+FlatText(expected)+"], caret="+caret+"/"+want+
                ", cellEnd(before)="+(end-line.Offset)+" cellEnd(after)="+(cellEnd<0?-1:cellEnd-line.Offset)+" nextAt="+(nextAt-line.Offset)+", cells(rel)="+cells+
                ", caretVC="+caretColumn+" nextVC="+nextColumn+", caretX="+Fmt(caretX)+" nextX="+Fmt(nextX)+
                ", drawn="+Fmt(drawn)+", box="+Fmt(box)+", inBox="+inBox+state+
                (ime?" imeAfterSpace=["+window.source.CompositionState()+"]":""));
        }catch(Exception ex){Check(tag,false,ex.ToString());}
        finally{
            if(window!=null){
                try{window.source.ResetCompositionSimulation();}catch(Exception){}
                CloseWindow(window);
            }
        }
    }
    // The drawn table caret itself. (c) a body cell in BOTH views: the drawn x is CellGeometry's
    // own x, inside the cell box. (a) basic view, right-aligned last column: End in the last cell
    // keeps the caret inside that cell's box (the stock path drew it past the border). (b) leaving
    // the table gives the stock caret brush back and clears the drawn rectangle.
    static void RunDrawnCaretSuite(int width){
        bool hide=hideMarkers,live=ProbeDefaults.Live;
        MainWindow window=null;
        try{
            ProbeDefaults.Live=true;
            foreach(bool hidden in new[]{true,false}){
                hideMarkers=hidden;
                string mode=hidden?"basic":"live";
                try{
                    window=new MainWindow{ShowInTaskbar=false,ShowActivated=false,Width=width,Height=760};
                    window.Show();window.textMode=false;
                    window.SetText("| 왼쪽 머리 | 오른쪽 머리 |\n| --- | ---: |\n| 가나다라마 | 마바사 |\n\n본문\n");
                    window.SetMode(true);SafeIdle("drawn caret "+mode,"open");window.UpdateLayout();
                    var session=window.livePreview;
                    if(session==null){Check("drawn caret "+mode+" attached",false,"no live preview");continue;}
                    var document=window.source.Document;
                    int bodyLine=5,rowLine=3;
                    // The stock brush on a plain line is exactly what leaving the table restores.
                    PutCaret(window,document.GetLineByNumber(bodyLine).Offset+1);LongLineBench.Idle();window.UpdateLayout();
                    Brush stock=window.source.CaretBrush;
                    var row=document.GetLineByNumber(rowLine);
                    var info=session.Info(row);
                    int at=row.Offset+info.Cells[0].Start+2;
                    PutCaret(window,at);LongLineBench.Idle();window.UpdateLayout();
                    document=window.source.Document;row=document.GetLineByNumber(rowLine);info=session.Info(row);
                    VisualLine visual=session.View.GetOrConstructVisualLine(row);
                    Rect box=session.TableCellBox(visual,0);
                    Rect drawn=session.DrawnTableCaret;
                    double cellX=Double.NaN,cellTop=Double.NaN,cellBottom=Double.NaN;
                    bool oracle=session.CellGeometry(row,0,2,false,out cellX,out cellTop,out cellBottom);
                    bool inside=box.Width>0&&!drawn.IsEmpty&&drawn.Left>=box.Left-0.6&&drawn.Right<=box.Right+0.6;
                    Check("drawn caret "+mode+" body cell x is CellGeometry x",oracle&&inside&&Math.Abs(drawn.Left-cellX)<=1,
                        "drawn="+Fmt(drawn)+", geometryX="+Fmt(cellX)+", box="+Fmt(box)+", caret="+window.source.CaretIndex+"/"+at+
                        ", raw="+(info.Row!=null&&info.Row.Raw)+", stock="+Fmt(StockCaretRect(window,visual)));
                    if(hidden){
                        // (a) End inside the right-aligned last cell.
                        int last=info.Cells.Count-1;
                        PutCaret(window,row.Offset+info.Cells[last].Start+1);LongLineBench.Idle();window.UpdateLayout();
                        EditingCommands.MoveToLineEnd.Execute(null,window.source.TextArea);LongLineBench.Idle();window.UpdateLayout();
                        document=window.source.Document;row=document.GetLineByNumber(rowLine);info=session.Info(row);
                        visual=session.View.GetOrConstructVisualLine(row);
                        int lastCell=info.Cells.Count-1;
                        box=session.TableCellBox(visual,lastCell);
                        drawn=session.DrawnTableCaret;
                        bool endInside=box.Width>0&&!drawn.IsEmpty&&drawn.Left>=box.Left-0.6&&drawn.Right<=box.Right+0.6;
                        Check("drawn caret basic right-aligned last cell End stays inside the cell",endInside,
                            "caret="+window.source.CaretIndex+", cell="+info.Cells[lastCell].Start+".."+info.Cells[lastCell].End+
                            ", drawn="+Fmt(drawn)+", box="+Fmt(box)+", stock="+Fmt(StockCaretRect(window,visual))+
                            ", text=["+FlatText(document.GetText(row))+"]");
                        // End again on the table's LAST row: the app moves the caret to the row's
                        // line end (after the trailing pipe). That is the offset the stock caret
                        // drew past the right border; the drawn caret keeps to the last cell.
                        EditingCommands.MoveToLineEnd.Execute(null,window.source.TextArea);LongLineBench.Idle();window.UpdateLayout();
                        document=window.source.Document;row=document.GetLineByNumber(rowLine);info=session.Info(row);
                        visual=session.View.GetOrConstructVisualLine(row);
                        lastCell=info.Cells.Count-1;
                        box=session.TableCellBox(visual,lastCell);
                        drawn=session.DrawnTableCaret;
                        bool lineEndInside=box.Width>0&&!drawn.IsEmpty&&drawn.Left>=box.Left-0.6&&drawn.Right<=box.Right+0.6;
                        // The native IME composition window origin is LiveCompositionGeometry's
                        // stock point, so this is where a composing syllable would be drawn.
                        double compSize;Typeface compFace;
                        Point comp=LiveCompositionGeometry.Position(window.source,out compSize,out compFace);
                        Check("drawn caret basic last row line end stays inside the last cell",lineEndInside,
                            "caret="+window.source.CaretIndex+"/"+row.EndOffset+", cell="+info.Cells[lastCell].Start+".."+info.Cells[lastCell].End+
                            ", drawn="+Fmt(drawn)+", box="+Fmt(box)+", stock="+Fmt(StockCaretRect(window,visual))+
                            ", compositionX="+Fmt(comp.X));
                    }
                    // (b) leaving the table restores the stock caret.
                    PutCaret(window,document.GetLineByNumber(bodyLine).Offset+1);LongLineBench.Idle();window.UpdateLayout();
                    Rect after=session.DrawnTableCaret;
                    Brush back=window.source.CaretBrush;
                    bool restored=after.IsEmpty&&ReferenceEquals(back,stock);
                    Check("drawn caret "+mode+" leaving the table restores the stock caret",restored,
                        "drawn="+Fmt(after)+", brush="+(back==null?"null":back.ToString())+", was="+(stock==null?"null":stock.ToString())+
                        ", caret="+window.source.CaretIndex);
                }catch(Exception ex){Check("drawn caret "+mode,false,ex.ToString());}
                finally{CloseWindow(window);window=null;}
            }
        }catch(Exception ex){Check("drawn caret suite",false,ex.ToString());}
        finally{hideMarkers=hide;ProbeDefaults.Live=live;if(window!=null)CloseWindow(window);}
    }
    // A mouse press on row k of a cell that holds <br> must put the caret at that character's
    // source offset (not the cell end), and a press right of a row's text must land at the end
    // of that row, before its <br>. The point comes from CellGeometry (the drawn text line of
    // that slice), so the expectation is independent of the hit-test under test. Synthetic text.
    static int CellTextIndexOfSource(MarkdownLivePreview.RowLayout row,int cell,int sourceRel){
        var text=row==null||row.Texts==null||cell>=row.Texts.Length?null:row.Texts[cell];
        if(text==null)return -1;
        for(int p=0;p<text.Text.Length;p++)if(text.Map[p]==sourceRel&&!text.IsBreak(p))return p;
        return -1;
    }
    static void RunCellBreakClickSuite(int width){
        bool hide=hideMarkers,live=ProbeDefaults.Live;
        string[] names={"three rows","doubled br","space run"};
        string[] bodies={"가가가<br>나나나<br>다다다","가가가<br><br>나나나<br><br>다다다","가가가                    라라라<br>나나나                    마마마<br>사사사                    아아아"};
        // Per body: the marker whose middle character is pressed, and the marker that ends the row
        // (the press right of its text expects the offset just after it, before the <br>).
        string[] pressed={"나나나","나나나","마마마"};
        MainWindow window=null;
        try{
            ProbeDefaults.Live=true;
            foreach(bool hidden in new[]{true,false})for(int f=0;f<bodies.Length;f++){
                hideMarkers=hidden;
                string tag="cell br click "+(hidden?"basic":"live")+" "+names[f];
                try{
                    window=new MainWindow{ShowInTaskbar=false,ShowActivated=false,Width=width,Height=760};
                    window.Show();window.textMode=false;
                    window.SetText("첫 줄\n\n| 하나 |\n| --- |\n| "+bodies[f]+" |\n");
                    window.SetMode(true);SafeIdle(tag,"open");window.UpdateLayout();
                    var session=window.livePreview;
                    if(session==null){Check(tag+" attached",false,"no live preview");continue;}
                    var document=window.source.Document;var row=document.GetLineByNumber(5);
                    string rowText=document.GetText(row);
                    int middle=rowText.IndexOf(pressed[f],StringComparison.Ordinal)+1;
                    // Left-edge press on each of the three characters of the pressed row.
                    foreach(int shift in new[]{0,1,2}){
                        int sourceRel=middle-1+shift;
                        PutCaret(window,document.GetLineByNumber(1).Offset);window.UpdateLayout();session.View.EnsureVisualLines();
                        var info=session.Info(row);
                        int ti=CellTextIndexOfSource(info.Row,0,sourceRel);
                        double x0=Double.NaN,top=Double.NaN,bottom=Double.NaN,x1=Double.NaN,t2=Double.NaN,b2=Double.NaN;
                        bool g0=ti>=0&&session.CellGeometry(row,0,ti,false,out x0,out top,out bottom);
                        bool g1=g0&&session.CellGeometry(row,0,ti+1,false,out x1,out t2,out b2);
                        string label=tag+" char "+shift;
                        if(!g0||!g1){Check(label,false,"no geometry ti="+ti+" g0="+g0+" g1="+g1);continue;}
                        double x=x1>x0?x0+(x1-x0)*0.3:x0+1;
                        session.BeginPress();bool handled;
                        try{handled=session.PlaceTableCaret(new Point(x,(top+bottom)/2),false);}
                        finally{session.EndPress();}
                        int caret=window.source.CaretOffset-row.Offset;
                        LongLineBench.Idle();int settled=window.source.CaretOffset-row.Offset;
                        Check(label,handled&&caret==sourceRel&&settled==sourceRel,
                            "handled="+handled+", caret="+caret+", settled="+settled+", expected="+sourceRel+", x="+Fmt(x)+", y="+Fmt((top+bottom)/2));
                    }
                    // Press right of the pressed row's text (inside the cell box): the end of THAT
                    // row, just before its <br> (or the cell end for the last row).
                    {
                        int endRel=middle+2;
                        PutCaret(window,document.GetLineByNumber(1).Offset);window.UpdateLayout();session.View.EnsureVisualLines();
                        var info=session.Info(row);
                        int ti=CellTextIndexOfSource(info.Row,0,middle);
                        double x0=Double.NaN,top=Double.NaN,bottom=Double.NaN;
                        bool g0=ti>=0&&session.CellGeometry(row,0,ti,false,out x0,out top,out bottom);
                        VisualLine visual=session.View.GetOrConstructVisualLine(row);
                        Rect box=session.TableCellBox(visual,0);
                        string label=tag+" right of row";
                        if(!g0||box.IsEmpty){Check(label,false,"no geometry ti="+ti+" g0="+g0);}
                        else{
                            session.BeginPress();bool handled;
                            try{handled=session.PlaceTableCaret(new Point(box.Right-2,(top+bottom)/2),false);}
                            finally{session.EndPress();}
                            int caret=window.source.CaretOffset-row.Offset;
                            LongLineBench.Idle();int settled=window.source.CaretOffset-row.Offset;
                            Check(label,handled&&caret==endRel&&settled==endRel,
                                "handled="+handled+", caret="+caret+", settled="+settled+", expected="+endRel+", box="+Fmt(box));
                        }
                    }
                }catch(Exception ex){Check(tag,false,ex.ToString());}
                finally{CloseWindow(window);window=null;}
            }
        }catch(Exception ex){Check("cell br click suite",false,ex.ToString());}
        finally{hideMarkers=hide;ProbeDefaults.Live=live;if(window!=null)CloseWindow(window);}
    }
    static string BrushName(Brush brush){return brush==null?"null":brush.ToString();}
    // F3/F4 of the LiveTableCaret review. (1) A scroll must move the drawn rectangle in the
    // same dispatcher turn: the stock caret has already moved and the layer must not paint the
    // previous position for a frame. (2) A measurement with no valid visual lines must keep the
    // drawn caret instead of falling back to the stock one at a projected (wrong) column. (3) A
    // theme switch or a direct CaretBrush reassignment must hide the stock brush again, and
    // leaving the table or detaching must hand back the newest brush. (4) The blink timer only
    // runs while the editor has keyboard focus.
    static void RunCaretFrameSuite(int width){
        bool live=ProbeDefaults.Live;
        MainWindow window=null;
        bool themeTouched=false,wasDark=false;
        try{
            ProbeDefaults.Live=true;
            window=new MainWindow{ShowInTaskbar=false,ShowActivated=false,Width=width,Height=760};
            window.Show();window.textMode=false;
            var fixture=new StringBuilder();
            for(int n=0;n<20;n++)fixture.Append("line ").Append(n).Append('\n');
            fixture.Append("| h | i |\n|---|---|\n| a | b |\n\n");
            for(int n=0;n<60;n++)fixture.Append("tail ").Append(n).Append('\n');
            window.SetText(fixture.ToString());window.SetMode(true);SafeIdle("caret frame "+width,"open");window.UpdateLayout();
            var session=window.livePreview;
            if(session==null){Check("caret frame "+width+" attached",false,"no live preview");return;}
            var document=window.source.Document;
            int rowLine=23,bodyLine=25;
            var row=document.GetLineByNumber(rowLine);
            var info=session.Info(row);
            int inCell=row.Offset+info.Cells[0].Start+1;
            PutCaret(window,inCell);SafeIdle("caret frame "+width,"caret");window.UpdateLayout();
            Rect before=session.DrawnTableCaret;
            Check("caret frame "+width+" draws in a table row",!before.IsEmpty,"rect="+Fmt(before)+", caret="+window.source.CaretIndex);
            if(before.IsEmpty)return;
            // (1) Scroll three lines and read the rectangle before any queued work can run.
            var view=session.View;
            var scroll=(IScrollInfo)view;
            scroll.CanVerticallyScroll=true;
            Vector scrollBefore=view.ScrollOffset;
            double lineHeight=view.DefaultLineHeight;
            if(Double.IsNaN(lineHeight)||lineHeight<1)lineHeight=20;
            scroll.SetVerticalOffset(scrollBefore.Y+lineHeight*3);
            Vector scrollAfter=view.ScrollOffset;
            Rect shifted=session.DrawnTableCaret;
            double dy=scrollAfter.Y-scrollBefore.Y;
            Check("caret frame "+width+" scroll shifts the drawn caret in the same turn",
                dy>0&&!shifted.IsEmpty&&Math.Abs(shifted.Top-(before.Top-dy))<=.01&&Math.Abs(shifted.Left-before.Left)<=.01,
                "dy="+Fmt(dy)+", scroll="+Fmt(scrollBefore.Y)+"->"+Fmt(scrollAfter.Y)+", before="+Fmt(before)+", after="+Fmt(shifted));
            SafeIdle("caret frame "+width,"scroll");window.UpdateLayout();SafeIdle("caret frame "+width,"scroll remeasure");
            before=session.DrawnTableCaret;
            // (2) Clear the visual lines (a horizontal-scroll change drops them) and measure with
            // no layout: the previous rectangle and the hidden stock brush must stay.
            bool canH=scroll.CanHorizontallyScroll;
            scroll.CanHorizontallyScroll=!canH;
            scroll.CanHorizontallyScroll=canH;
            bool linesValid=view.VisualLinesValid;
            session.UpdateTableCaretNowForProbe();
            Rect kept=session.DrawnTableCaret;
            bool hiddenWhilePending=ReferenceEquals(window.source.CaretBrush,Brushes.Transparent);
            Check("caret frame "+width+" keeps the drawn caret while the view has no layout",
                !linesValid&&!before.IsEmpty&&kept.Equals(before)&&hiddenWhilePending,
                "valid="+linesValid+", kept="+Fmt(kept)+", before="+Fmt(before)+", hidden="+hiddenWhilePending);
            window.UpdateLayout();SafeIdle("caret frame "+width,"pending layout");window.UpdateLayout();
            Check("caret frame "+width+" redraws after the layout returns",
                view.VisualLinesValid&&!session.DrawnTableCaret.IsEmpty,
                "valid="+view.VisualLinesValid+", drawn="+Fmt(session.DrawnTableCaret));
            // (3a) A real dark/light switch reassigns the host's caret brush.
            PutCaret(window,inCell);SafeIdle("caret frame "+width,"pre theme");window.UpdateLayout();
            wasDark=window.dark;themeTouched=true;
            window.SetDark(!wasDark);SafeIdle("caret frame "+width,"theme");window.UpdateLayout();SafeIdle("caret frame "+width,"theme ink");
            bool hiddenTheme=ReferenceEquals(window.source.CaretBrush,Brushes.Transparent);
            Check("caret frame "+width+" theme switch keeps the stock caret hidden",
                hiddenTheme&&!session.DrawnTableCaret.IsEmpty,
                "hidden="+hiddenTheme+", drawn="+Fmt(session.DrawnTableCaret)+", brush="+BrushName(window.source.CaretBrush)+", attached="+window.LivePreviewAttached);
            PutCaret(window,document.GetLineByNumber(bodyLine).Offset+1);SafeIdle("caret frame "+width,"theme body");window.UpdateLayout();SafeIdle("caret frame "+width,"theme body brush");
            Brush themed=window.source.CaretBrush;
            Check("caret frame "+width+" leaving the table restores the theme ink",
                !ReferenceEquals(themed,Brushes.Transparent)&&ReferenceEquals(themed,window.Foreground),
                "brush="+BrushName(themed)+", foreground="+BrushName(window.Foreground)+", drawn="+Fmt(session.DrawnTableCaret));
            // (3b) A direct reassignment with no Foreground change raises no property event.
            PutCaret(window,inCell);SafeIdle("caret frame "+width,"pre brush");window.UpdateLayout();
            Brush newest=Brushes.Red;
            window.source.CaretBrush=newest;
            window.InvalidateMeasure();window.UpdateLayout();SafeIdle("caret frame "+width,"direct brush");
            bool hiddenDirect=ReferenceEquals(window.source.CaretBrush,Brushes.Transparent);
            Check("caret frame "+width+" direct brush reassignment is hidden again",
                hiddenDirect&&!session.DrawnTableCaret.IsEmpty,
                "hidden="+hiddenDirect+", drawn="+Fmt(session.DrawnTableCaret)+", brush="+BrushName(window.source.CaretBrush));
            PutCaret(window,document.GetLineByNumber(bodyLine).Offset+1);SafeIdle("caret frame "+width,"direct brush body");window.UpdateLayout();SafeIdle("caret frame "+width,"direct brush restore");
            Check("caret frame "+width+" leaving the table restores the reassigned brush",
                ReferenceEquals(window.source.CaretBrush,newest),
                "brush="+BrushName(window.source.CaretBrush)+", want="+BrushName(newest)+", drawn="+Fmt(session.DrawnTableCaret));
            // (4) Blink only while the editor owns the keyboard.
            PutCaret(window,inCell);SafeIdle("caret frame "+width,"pre blink");window.UpdateLayout();SafeIdle("caret frame "+width,"blink");
            bool focused=session.TableCaretFocused,blinking=session.TableCaretBlinkRunning;
            Check("caret frame "+width+" blink timer only runs while focused",
                !blinking||focused,"focused="+focused+", blink="+blinking+", drawn="+Fmt(session.DrawnTableCaret));
            if(focused){
                window.Hide();SafeIdle("caret frame "+width,"unfocus");
                Check("caret frame "+width+" blink timer stops when the editor loses focus",
                    !session.TableCaretFocused&&!session.TableCaretBlinkRunning,
                    "focused="+session.TableCaretFocused+", blink="+session.TableCaretBlinkRunning);
                window.Show();SafeIdle("caret frame "+width,"refocus");window.UpdateLayout();
            }
            // (3c) Detach hands the newest brush back, never the pre-theme one.
            PutCaret(window,inCell);SafeIdle("caret frame "+width,"pre detach");window.UpdateLayout();
            Brush detachBrush=Brushes.Blue;
            window.source.CaretBrush=detachBrush;
            window.InvalidateMeasure();window.UpdateLayout();SafeIdle("caret frame "+width,"detach brush");
            bool hiddenBeforeDetach=ReferenceEquals(window.source.CaretBrush,Brushes.Transparent);
            window.DetachLivePreview(false);
            Check("caret frame "+width+" detach restores the newest brush",
                hiddenBeforeDetach&&ReferenceEquals(window.source.CaretBrush,detachBrush),
                "hiddenBefore="+hiddenBeforeDetach+", brush="+BrushName(window.source.CaretBrush)+", want="+BrushName(detachBrush));
        }catch(Exception ex){Check("caret frame suite "+width,false,ex.ToString());}
        finally{
            try{if(window!=null&&themeTouched)window.SetDark(wasDark);}catch(Exception){}
            ProbeDefaults.Live=live;
            if(window!=null)CloseWindow(window);
        }
    }
    // fix11: table boundaries from the real keyboard/mouse test - a document that starts with
    // a table, a paragraph directly under or above one, the tool row's paragraph action and the
    // no-reveal hidden-marker delete at a cell's edge. The product's editing surface hides
    // markers, so the suite turns that preference on and drives the real key path.
    static void RunTableBoundarySuite(int width){
        bool hide=hideMarkers,live=ProbeDefaults.Live;
        MainWindow window=null;
        try{
            hideMarkers=true;ProbeDefaults.Live=true;
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=width,Height=760};
            window.Show();window.textMode=false;window.SetMode(true);LongLineBench.Idle();
            RunTableBoundary(window,width);
        }catch(Exception ex){Check("table boundary suite "+width,false,ex.ToString());}
        finally{hideMarkers=hide;ProbeDefaults.Live=live;if(window!=null)CloseWindow(window);}
    }
    static void PutCaret(MainWindow window,int offset){
        window.source.Select(offset,0);window.source.CaretOffset=offset;LongLineBench.Idle();
    }
    static int CellStart(MainWindow window,ICSharpCode.AvalonEdit.Document.DocumentLine line,int cell){
        MarkdownLivePreview.LineInfo info=window.livePreview.Info(line);
        return line.Offset+info.Cells[cell].Start;
    }
    static int CellEnd(MainWindow window,ICSharpCode.AvalonEdit.Document.DocumentLine line,int cell){
        MarkdownLivePreview.LineInfo info=window.livePreview.Info(line);
        return line.Offset+info.Cells[cell].End;
    }
    // The formatted row's own column grid, at the current table width: `grid/cells`. A stale
    // published column count (an undo/redo of a column move) draws one empty extra column.
    static string GridShape(MainWindow window,ICSharpCode.AvalonEdit.Document.DocumentLine line){
        var document=window.source.Document;var session=window.livePreview;
        ICSharpCode.AvalonEdit.Document.DocumentLine now=document.GetLineByNumber(line.LineNumber);
        window.UpdateLayout();
        if(session.View.VisualLinesValid)session.View.GetOrConstructVisualLine(now);
        var info=session.Info(now);
        return (info.Row==null?-1:info.Row.Texts.Length)+"/"+(info.Cells==null?-1:info.Cells.Count);
    }
    static void RunTableBoundary(MainWindow window,int width){
        string tag="table boundary "+width;
        var document=window.source.Document;
        MarkdownLivePreview.Session session=window.livePreview;
        // B1: a document that starts with a table. Ctrl+Home must land on the first cell's text,
        // never before the hidden leading pipe; typing then stays in that cell.
        string head="| 맨앞 | 표 |\n|---|---|\n| a | b |\n";
        window.SetText(head);LongLineBench.Idle();
        document=window.source.Document;session=window.livePreview;
        EditingCommands.MoveToDocumentStart.Execute(null,window.source.TextArea);LongLineBench.Idle();
        int firstStart=CellStart(window,document.GetLineByNumber(1),0);
        Check(tag+" Ctrl+Home lands in the first cell",window.source.CaretOffset==firstStart,
            "caret="+window.source.CaretOffset+"/"+firstStart+", text=["+FlatText(document.Text)+"]");
        window.source.TextArea.PerformTextInput("s");LongLineBench.Idle();
        string typedHead=document.Text;
        Check(tag+" typing after Ctrl+Home stays in the first cell",
            typedHead.StartsWith("| s맨앞 | 표 |",StringComparison.Ordinal)&&MarkdownLivePreview.SplitCells(document.GetText(document.GetLineByNumber(1))).Count==2,
            "text=["+FlatText(typedHead)+"]");
        // The row start itself stays reachable by an explicit offset (the Enter-above-the-table
        // rule matches on the header's column 0); typing there still goes into the first cell,
        // never in front of the leading pipe.
        window.SetText(head);LongLineBench.Idle();document=window.source.Document;
        PutCaret(window,0);
        window.source.TextArea.PerformTextInput("s");LongLineBench.Idle();
        string typedRowStart=document.Text;
        Check(tag+" typing at the row start goes into the first cell",
            typedRowStart.StartsWith("| s맨앞 | 표 |",StringComparison.Ordinal),
            "text=["+FlatText(typedRowStart)+"]");
        // Home with the caret at the row's own line end: that slot is no longer a caret stop
        // (it clamps to the last cell's end), so Home comes back to that last cell's start,
        // never the line start and never in front of the trailing pipe.
        window.SetText(head);LongLineBench.Idle();document=window.source.Document;
        PutCaret(window,document.GetLineByNumber(3).EndOffset);
        EditingCommands.MoveToLineStart.Execute(null,window.source.TextArea);LongLineBench.Idle();
        int home=CellStart(window,document.GetLineByNumber(3),1);
        Check(tag+" Home from the row end stays in its last cell",window.source.CaretOffset==home,
            "caret="+window.source.CaretOffset+"/"+home+", line="+window.source.TextArea.Caret.Line+", text=["+FlatText(document.Text)+"]");
        // B2: Backspace at the start of a paragraph whose previous non-blank line is a table row
        // deletes nothing (the blank separator and the row survive) and steps into the last cell.
        string below="| a | b |\n|---|---|\n| c | d |\n\n중간 문단\n";
        window.SetText(below);LongLineBench.Idle();document=window.source.Document;
        int rowEnd=CellEnd(window,document.GetLineByNumber(3),1);
        PutCaret(window,document.GetLineByNumber(5).Offset);
        bool handled=PressLiveKey(window,Key.Back);
        Check(tag+" Backspace at a paragraph start under a table keeps the blank separator",
            handled&&document.Text==below&&window.source.CaretOffset==rowEnd,
            "handled="+handled+", caret="+window.source.CaretOffset+"/"+rowEnd+", text=["+FlatText(document.Text)+"]");
        // The paragraph already touching the table (the second Backspace of the old bug).
        string touching="| a | b |\n|---|---|\n| c | d |\n중간 문단\n";
        window.SetText(touching);LongLineBench.Idle();document=window.source.Document;
        rowEnd=CellEnd(window,document.GetLineByNumber(3),1);
        PutCaret(window,document.GetLineByNumber(4).Offset);
        handled=PressLiveKey(window,Key.Back);
        Check(tag+" Backspace at a paragraph start touching a table keeps the row",
            handled&&document.Text==touching&&window.source.CaretOffset==rowEnd,
            "handled="+handled+", caret="+window.source.CaretOffset+"/"+rowEnd+", text=["+FlatText(document.Text)+"]");
        // Delete at the last cell's end never pulls the paragraph below into the row.
        window.SetText(below);LongLineBench.Idle();document=window.source.Document;
        rowEnd=CellEnd(window,document.GetLineByNumber(3),1);
        PutCaret(window,rowEnd);
        handled=PressLiveKey(window,Key.Delete);
        Check(tag+" Delete at the last cell end leaves the paragraph below",
            handled&&document.Text==below&&window.source.CaretOffset==rowEnd,
            "handled="+handled+", caret="+window.source.CaretOffset+"/"+rowEnd+", text=["+FlatText(document.Text)+"]");
        // Delete at the end of a paragraph directly above a table never merges the header row;
        // the caret steps into the table's first cell instead.
        string above="위 문단\n\n| a | b |\n|---|---|\n| c | d |\n";
        window.SetText(above);LongLineBench.Idle();document=window.source.Document;
        int headerStart=CellStart(window,document.GetLineByNumber(3),0);
        PutCaret(window,document.GetLineByNumber(1).EndOffset);
        handled=PressLiveKey(window,Key.Delete);
        Check(tag+" Delete at a paragraph end above a table keeps the header row",
            handled&&document.Text==above&&window.source.CaretOffset==headerStart,
            "handled="+handled+", caret="+window.source.CaretOffset+"/"+headerStart+", text=["+FlatText(document.Text)+"]");
        // B3: the tool row's "아래 문단 추가" always leaves an empty line between the table and
        // the paragraph, also when the table ends the document or a paragraph follows already.
        string endTable="| 맨앞 | 표 |\n|---|---|\n| a | b |";
        window.SetText(endTable);LongLineBench.Idle();document=window.source.Document;
        PutCaret(window,CellStart(window,document.GetLineByNumber(3),0));
        window.EditTable(5);LongLineBench.Idle();
        window.source.TextArea.PerformTextInput("새 문단");LongLineBench.Idle();
        Check(tag+" 아래 문단 추가 at the document end keeps a blank line",
            document.Text==endTable+"\n\n새 문단",
            "text=["+FlatText(document.Text)+"]");
        string followed="| a | b |\n|---|---|\n| c | d |\n\n다음 문단\n";
        window.SetText(followed);LongLineBench.Idle();document=window.source.Document;
        PutCaret(window,CellStart(window,document.GetLineByNumber(3),0));
        window.EditTable(5);LongLineBench.Idle();
        window.source.TextArea.PerformTextInput("새 문단");LongLineBench.Idle();
        Check(tag+" 아래 문단 추가 above an existing paragraph keeps one blank line",
            document.Text=="| a | b |\n|---|---|\n| c | d |\n\n새 문단\n\n다음 문단\n",
            "text=["+FlatText(document.Text)+"]");
        // Enter at the end of the last row still opens a pipe-free line outside the table (the
        // basic view's "Enter makes a paragraph, never a row"), and the text typed there is a
        // paragraph rather than a new row; the blank separator line comes with that first text.
        string enterTable="| a | b |\n|---|---|\n| c | d |";
        window.SetText(enterTable);LongLineBench.Idle();document=window.source.Document;
        // The row's own line end (after the trailing pipe) is the boundary spot; End pressed
        // again at the last cell's end reaches it.
        PutCaret(window,document.GetLineByNumber(3).EndOffset);
        PressLiveKey(window,Key.Enter);
        int enterLine=window.source.TextArea.Caret.Line;
        window.source.TextArea.PerformTextInput("문단");LongLineBench.Idle();
        Check(tag+" Enter below a table makes a paragraph line, never a row",
            document.Text==enterTable+"\n\n문단"&&enterLine==4,
            "caret line="+enterLine+", text=["+FlatText(document.Text)+"]");
        // B4: the no-reveal delete at a cell edge. Backspace on the last visible character
        // before a hidden closing marker deletes that character; the next typed character
        // must stay in the SAME cell, never the previous one.
        foreach(string marker in new[]{"`","**","*","~~"}){
            string name=marker=="`"?"code":marker=="**"?"bold":marker=="*"?"italic":"strike";
            string fixture="| a | b |\n|---|---|\n| 제목 | "+marker+"굵게"+marker+" |\n";
            window.SetText(fixture);LongLineBench.Idle();document=window.source.Document;
            PutCaret(window,CellEnd(window,document.GetLineByNumber(3),1));
            PressLiveKey(window,Key.Back);
            ICSharpCode.AvalonEdit.Document.DocumentLine rowLine=document.GetLineByNumber(3);
            var backInfo=session.Info(rowLine);
            int backRelative=window.source.CaretOffset-rowLine.Offset;
            // The two edges of a hidden marker are one visual spot on purpose (a typed
            // character goes inside or outside the style), so the caret may sit on either;
            // what matters is that the delete left it inside the same cell.
            bool backInside=backInfo.Cells!=null&&backRelative>=backInfo.Cells[1].Start&&backRelative<=backInfo.Cells[1].End;
            window.source.TextArea.PerformTextInput("e");LongLineBench.Idle();
            var info=session.Info(document.GetLineByNumber(3));
            int at=document.GetText(document.GetLineByNumber(3)).IndexOf('e');
            bool inCell=info.Cells!=null&&info.Cells.Count==2&&at>=info.Cells[1].Start&&at<=info.Cells[1].End;
            Check(tag+" Backspace then type at a cell end with "+name+" stays in that cell",inCell&&backInside,
                "at="+at+", back="+backRelative+", cells="+(info.Cells==null?-1:info.Cells.Count)+
                ", backInside="+backInside+", text=["+FlatText(document.Text)+"]");
        }
        // The same through the real mouse press: the cell's right edge, then Backspace and e.
        string clickFixture="| 제목 | `코드` |\n|---|---|\n| a | b |\n";
        window.SetText(clickFixture);LongLineBench.Idle();document=window.source.Document;session=window.livePreview;
        window.UpdateLayout();session.View.EnsureVisualLines();
        ICSharpCode.AvalonEdit.Document.DocumentLine clickRow=document.GetLineByNumber(1);
        MarkdownLivePreview.RowLayout clickLayout=session.Info(clickRow).Row;
        Rect clickBox=session.TableCellBox(session.View.GetOrConstructVisualLine(clickRow),1);
        bool clicked=clickLayout!=null&&!clickBox.IsEmpty&&session.PlaceTableCaret(new Point(clickBox.Right-1,clickBox.Top+clickBox.Height/2),false);
        LongLineBench.Idle();
        PressLiveKey(window,Key.Back);
        window.source.TextArea.PerformTextInput("e");LongLineBench.Idle();
        var clickInfo=session.Info(document.GetLineByNumber(1));
        int clickAt=document.GetText(document.GetLineByNumber(1)).IndexOf('e');
        bool clickInCell=clicked&&clickInfo.Cells!=null&&clickInfo.Cells.Count==2&&clickAt>=clickInfo.Cells[1].Start&&clickAt<=clickInfo.Cells[1].End;
        Check(tag+" Backspace then type after a click at a cell end stays in that cell",clickInCell,
            "clicked="+clicked+", at="+clickAt+", cells="+(clickInfo.Cells==null?-1:clickInfo.Cells.Count)+", text=["+FlatText(document.Text)+"]");
        // The first cell's start is swallowed and typing stays in that cell.
        string startFixture="| `코드` | z |\n|---|---|\n| a | b |\n";
        window.SetText(startFixture);LongLineBench.Idle();document=window.source.Document;
        PutCaret(window,CellStart(window,document.GetLineByNumber(1),0));
        handled=PressLiveKey(window,Key.Back);
        window.source.TextArea.PerformTextInput("e");LongLineBench.Idle();
        var startInfo=session.Info(document.GetLineByNumber(1));
        int startAt=document.GetText(document.GetLineByNumber(1)).IndexOf('e');
        Check(tag+" Backspace then type at the first cell start stays in that cell",
            handled&&startInfo.Cells!=null&&startAt>=startInfo.Cells[0].Start&&startAt<=startInfo.Cells[0].End&&document.Text==startFixture.Insert(startAt,"e"),
            "handled="+handled+", at="+startAt+", text=["+FlatText(document.Text)+"]");
        // B5: undo/redo of a column removal must publish the new column grid at once; a stale
        // count projected one empty extra column until the idle re-sample redrew the table.
        window.SetText("| a | b | c |\n| --- | --- | --- |\n| 1 | 2 | 3 |\n| 4 | 5 | 6 |\n");LongLineBench.Idle();
        document=window.source.Document;session=window.livePreview;
        PutCaret(window,CellStart(window,document.GetLineByNumber(4),1));
        window.EditTable(3);LongLineBench.Idle();
        bool removed=document.Text=="| a | c |\n| --- | --- |\n| 1 | 3 |\n| 4 | 6 |\n";
        string removedGrid=GridShape(window,document.GetLineByNumber(4));
        window.Undo();
        string undoGrid=GridShape(window,document.GetLineByNumber(4));
        string undoText=document.Text;
        LongLineBench.Idle();
        string undoIdleGrid=GridShape(window,document.GetLineByNumber(4));
        window.Redo();
        string redoGrid=GridShape(window,document.GetLineByNumber(4));
        Check(tag+" column remove keeps the grid in step",removed&&removedGrid=="2/2",
            "removed="+removed+", grid="+removedGrid+", text=["+FlatText(document.Text)+"]");
        Check(tag+" undo of a column remove republishes the column count",
            undoText=="| a | b | c |\n| --- | --- | --- |\n| 1 | 2 | 3 |\n| 4 | 5 | 6 |\n"&&undoGrid=="3/3"&&undoIdleGrid=="3/3",
            "grid="+undoGrid+", after idle="+undoIdleGrid+", text=["+FlatText(undoText)+"]");
        Check(tag+" redo of a column remove republishes the column count",redoGrid=="2/2",
            "grid="+redoGrid+", text=["+FlatText(document.Text)+"]");
        // B6: a mouse selection of a whole table ends at the last row's own line end. The press
        // resume must keep that endpoint; clamping it into the last cell dropped the trailing
        // pipe, so Delete cleared cells instead of removing the table lines.
        string selectedTable="before\n\n| a | b |\n| --- | --- |\n| c | d |\n\nafter\n";
        window.SetText(selectedTable);LongLineBench.Idle();
        document=window.source.Document;session=window.livePreview;
        int wholeStart=document.GetLineByNumber(3).Offset;
        int wholeEnd=document.GetLineByNumber(5).EndOffset;
        window.source.Select(wholeStart,wholeEnd-wholeStart);LongLineBench.Idle();
        bool wholeCovers=MarkdownTableCommands.SelectionCoversWholeTables(document,window.source.SelectionStart,window.source.SelectionLength);
        bool wholeKept=window.source.SelectionStart==wholeStart&&window.source.SelectionLength==wholeEnd-wholeStart;
        Check(tag+" whole-table selection ending at the last row's end keeps its endpoint",wholeCovers&&wholeKept,
            "covers="+wholeCovers+", selection="+window.source.SelectionStart+"/"+window.source.SelectionLength+", expected="+wholeStart+"/"+(wholeEnd-wholeStart));
        handled=PressLiveKey(window,Key.Delete);
        // Delete removes exactly the selected characters, and the selection ends at the last
        // row's EndOffset without its line break (keeping that endpoint is the point of B6), so
        // that break stays: the blank line above the table, the break itself and the blank line
        // below leave "before" and "after" separated by three empty lines. Selecting the break
        // too would fold one of them away, as the whole-table case in MarkdownTableCommandsTests
        // shows ("\npara\n").
        Check(tag+" Delete of a whole-table selection removes the table lines",handled&&document.Text=="before\n\n\n\nafter\n",
            "handled="+handled+", text=["+FlatText(document.Text)+"]");
        // B7: undoing a column removal applies the command's parts top-down, so the last change
        // lands on a body row after the header and delimiter were restored. With the header
        // scrolled out of view nothing else reads its cell count: a rendered body row must
        // still take the header's cells, not the old, wider grid.
        var tallTable=new StringBuilder("| a | b | c |\n| --- | --- | --- |\n");
        for(int r=1;r<=60;r++)tallTable.Append("| ").Append(r).Append(" | ").Append(r+100).Append(" | ").Append(r+200).Append(" |\n");
        window.SetText(tallTable.ToString());LongLineBench.Idle();
        document=window.source.Document;session=window.livePreview;
        int tallLast=document.LineCount-1;
        PutCaret(window,CellStart(window,document.GetLineByNumber(tallLast),1));
        window.source.ScrollToLine(tallLast-1);LongLineBench.Idle();window.UpdateLayout();
        window.EditTable(3);LongLineBench.Idle();
        window.Undo();
        document=window.source.Document;
        var undoHeaderInfo=session.Info(document.GetLineByNumber(1));
        string bodyGrid=GridShape(window,document.GetLineByNumber(tallLast-2));
        int headerCells=undoHeaderInfo.Cells==null?-1:undoHeaderInfo.Cells.Count;
        Check(tag+" undo of a column removal keeps the grid at the header's cell count",
            headerCells>0&&bodyGrid==headerCells+"/"+headerCells&&window.source.TextArea.Caret.Line==tallLast,
            "grid="+bodyGrid+", header cells="+headerCells+", caret line="+window.source.TextArea.Caret.Line+"/"+tallLast+", rows="+document.LineCount+", text=["+FlatText(document.Text)+"]");
    }
    // E1 follow-up: a surrogate pair or a combining mark inside a table cell. The row projection
    // gives every source offset a canonical column, so without a rule the arrows stop in the
    // middle of a pair and Backspace/Delete take half of it, leaving a lone surrogate the UTF-8
    // encoder cannot save. A row must apply the same caret-stop rule TextUtilities.GetNextCaretPosition
    // applies to every plain line. Both hideMarkers modes run the real Left/Right commands and the
    // real Backspace/Delete key path, and a plain paragraph with the same cell text is the
    // authority for every stop and every deletion.
    static readonly string[] UnicodeValues={
        "😀",                                     /* U+1F600 */
        "👍🏽",                         /* U+1F44D U+1F3FD */
        "👨‍👩‍👧", /* ZWJ family */
        "é",                                          /* e + U+0301 */
        "𐀀"                                      /* U+20000 */
    };
    static readonly string[] UnicodeLabels={"U+1F600","U+1F44D+U+1F3FD","ZWJ-family","e+U+0301","U+20000"};
    static void RunUnicodeCaretSuite(int width){
        bool hide=hideMarkers,live=ProbeDefaults.Live;
        MainWindow window=null;
        try{
            ProbeDefaults.Live=true;
            // formatted: the basic view keeps every row formatted (HideMarkers on); live: the
            // caret row is revealed as source (HideMarkers off), so both projections are checked.
            foreach(bool formatted in new[]{true,false}){
                hideMarkers=formatted;
                window=new MainWindow{ShowInTaskbar=false,ShowActivated=false,Width=width,Height=760};
                window.Show();window.textMode=false;window.SetMode(true);LongLineBench.Idle();
                string mode=formatted?"basic":"live";
                for(int v=0;v<UnicodeValues.Length;v++)RunUnicodeValue(window,width,mode,UnicodeValues[v],UnicodeLabels[v]);
                if(formatted)RunRowLeftWalk(window,width);
                CloseWindow(window);window=null;
            }
        }catch(Exception ex){Check("unicode caret suite "+width,false,ex.ToString());}
        finally{hideMarkers=hide;ProbeDefaults.Live=live;if(window!=null)CloseWindow(window);}
    }
    // One value in a body cell between "가" and "나", so both directions have a character on
    // either side; the plain paragraph carries the same "가{value}나" text.
    static void RunUnicodeValue(MainWindow window,int width,string mode,string value,string label){
        string tag="unicode caret "+mode+" "+width+" "+label;
        string plain="앞 문단\n\n가"+value+"나\n";
        string table="| 머리 | 끝 |\n| --- | --- |\n| 가"+value+"나 | 끝 |\n";
        int plainStart,plainBack,plainDelete;List<int> plainLeft,plainRight;
        if(!PlainReference(window,plain,value,out plainStart,out plainLeft,out plainRight,out plainBack,out plainDelete)){
            Check(tag+" plain reference",false,"fixture or deletion did not run");return;
        }
        window.SetText(table);window.SetMode(true);LongLineBench.Idle();
        var document=window.source.Document;var line=document.GetLineByNumber(3);
        int at=document.GetText(line).IndexOf(value,StringComparison.Ordinal);
        if(at<0){Check(tag+" table fixture",false,"value not in the row");return;}
        int start=line.Offset+at,points=PointCount(value);
        // Put the caret on the row first: in live mode this is the reveal that switches the row
        // from the formatted projection to its raw source, which is the second mode under test.
        window.source.Select(start,0);LongLineBench.Idle();
        bool raw=window.livePreview!=null&&window.livePreview.Info(line).Row!=null&&window.livePreview.Info(line).Row.Raw;
        List<int> left=WalkLeft(window,start+value.Length,start,points+1);
        bool leftWhole=left.Count>0;foreach(int pos in left)leftWhole&=!MidPair(document.Text,pos);
        List<int> right=WalkRight(window,start,start+value.Length,points+1);
        bool rightWhole=right.Count>0;foreach(int pos in right)rightWhole&=!MidPair(document.Text,pos);
        Check(tag+" Left stops stay outside the pair and match the plain line",leftWhole&&SameSteps(left,start,plainLeft,plainStart),
            "row="+(raw?"raw":"formatted")+", stops="+JoinInts(left)+", plain="+JoinInts(plainLeft));
        Check(tag+" Right stops stay outside the pair and match the plain line",rightWhole&&SameSteps(right,start,plainRight,plainStart),
            "row="+(raw?"raw":"formatted")+", stops="+JoinInts(right)+", plain="+JoinInts(plainRight));
        // Backspace after the value takes the same code point the plain line takes (one UTF-16
        // unit for an ordinary character, the whole pair for a surrogate).
        window.SetText(table);window.SetMode(true);LongLineBench.Idle();
        document=window.source.Document;line=document.GetLineByNumber(3);start=line.Offset+document.GetText(line).IndexOf(value,StringComparison.Ordinal);
        string before=document.Text;
        window.source.Select(start+value.Length,0);LongLineBench.Idle();
        PressLiveKey(window,Key.Back);
        string afterBack=document.Text;
        Check(tag+" Backspace deletes one code point like the plain line",
            afterBack==before.Remove(start+value.Length-plainBack,plainBack)&&LoneSurrogateAt(afterBack)<0&&window.source.CaretOffset==start+value.Length-plainBack,
            "deleted="+(before.Length-afterBack.Length)+"/"+plainBack+", text="+EscapedText(afterBack));
        // Delete before the value takes the same code point(s) the plain line takes.
        window.SetText(table);window.SetMode(true);LongLineBench.Idle();
        document=window.source.Document;line=document.GetLineByNumber(3);start=line.Offset+document.GetText(line).IndexOf(value,StringComparison.Ordinal);
        before=document.Text;
        window.source.Select(start,0);LongLineBench.Idle();
        PressLiveKey(window,Key.Delete);
        string afterDelete=document.Text;
        Check(tag+" Delete deletes one code point like the plain line",
            afterDelete==before.Remove(start,plainDelete)&&LoneSurrogateAt(afterDelete)<0&&window.source.CaretOffset==start,
            "deleted="+(before.Length-afterDelete.Length)+"/"+plainDelete+", text="+EscapedText(afterDelete));
    }
    // The plain-line authority for one value: the arrow stops inside "가{value}나" and the spans
    // the stock Backspace/Delete remove there. That line is not a table row, so this is exactly
    // the caret rule every non-table line already uses.
    static bool PlainReference(MainWindow window,string plain,string value,out int start,out List<int> left,out List<int> right,out int back,out int delete){
        start=-1;left=null;right=null;back=-1;delete=-1;
        window.SetText(plain);window.SetMode(true);LongLineBench.Idle();
        var document=window.source.Document;var line=document.GetLineByNumber(3);
        int at=document.GetText(line).IndexOf(value,StringComparison.Ordinal);
        if(at<0)return false;
        start=line.Offset+at;
        int points=PointCount(value);
        left=WalkLeft(window,start+value.Length,start,points+1);
        right=WalkRight(window,start,start+value.Length,points+1);
        window.SetText(plain);window.SetMode(true);LongLineBench.Idle();
        document=window.source.Document;line=document.GetLineByNumber(3);start=line.Offset+document.GetText(line).IndexOf(value,StringComparison.Ordinal);
        int length=document.TextLength;
        window.source.Select(start+value.Length,0);LongLineBench.Idle();
        PressLiveKey(window,Key.Back);
        back=length-document.TextLength;
        window.SetText(plain);window.SetMode(true);LongLineBench.Idle();
        document=window.source.Document;line=document.GetLineByNumber(3);start=line.Offset+document.GetText(line).IndexOf(value,StringComparison.Ordinal);
        length=document.TextLength;
        window.source.Select(start,0);LongLineBench.Idle();
        PressLiveKey(window,Key.Delete);
        delete=length-document.TextLength;
        return back>0&&delete>0;
    }
    // Document offsets the real Left/Right commands visit from `from`, staying inside
    // [floor, ...] / [..., ceiling], at most `steps` presses.
    static List<int> WalkLeft(MainWindow window,int from,int floor,int steps){
        var seen=new List<int>();window.source.Select(from,0);LongLineBench.Idle();int last=from;
        for(int i=0;i<steps;i++){
            EditingCommands.MoveLeftByCharacter.Execute(null,window.source.TextArea);LongLineBench.Idle();
            int at=window.source.CaretOffset;
            if(at==last||at<floor)break;
            seen.Add(at);last=at;
        }
        return seen;
    }
    static List<int> WalkRight(MainWindow window,int from,int ceiling,int steps){
        var seen=new List<int>();window.source.Select(from,0);LongLineBench.Idle();int last=from;
        for(int i=0;i<steps;i++){
            EditingCommands.MoveRightByCharacter.Execute(null,window.source.TextArea);LongLineBench.Idle();
            int at=window.source.CaretOffset;
            if(at==last||at>ceiling)break;
            seen.Add(at);last=at;
        }
        return seen;
    }
    static bool SameSteps(List<int> mine,int mineStart,List<int> plain,int plainStart){
        if(mine==null||plain==null||mine.Count!=plain.Count)return false;
        for(int i=0;i<mine.Count;i++)if(mine[i]-mineStart!=plain[i]-plainStart)return false;
        return true;
    }
    static int PointCount(string value){
        int count=0;
        for(int i=0;i<value.Length;i++){count++;if(char.IsHighSurrogate(value[i])&&i+1<value.Length&&char.IsLowSurrogate(value[i+1]))i++;}
        return count;
    }
    static bool MidPair(string text,int offset){
        return text!=null&&offset>0&&offset<text.Length&&char.IsHighSurrogate(text[offset-1])&&char.IsLowSurrogate(text[offset]);
    }
    static int LoneSurrogateAt(string text){
        if(text==null)return -1;
        for(int i=0;i<text.Length;i++){
            if(char.IsHighSurrogate(text[i])){if(i+1>=text.Length||!char.IsLowSurrogate(text[i+1]))return i;i++;}
            else if(char.IsLowSurrogate(text[i]))return i;
        }
        return -1;
    }
    static string EscapedText(string value){
        if(value==null)return "<null>";
        var text=new StringBuilder("\"");
        foreach(char c in value){
            if(c=='\r')text.Append("\\r");
            else if(c=='\n')text.Append("\\n");
            else if(c=='\t')text.Append("\\t");
            else if(char.IsSurrogate(c)||c<32)text.Append("\\u").Append(((int)c).ToString("X4",System.Globalization.CultureInfo.InvariantCulture));
            else text.Append(c);
        }
        return text.Append('"').ToString();
    }
    // The reported symptom: Left from a row's line end stopped after a few presses. Counted
    // against the cell text's own code point boundaries (a hidden pipe or padding has no caret
    // stop of its own), the walk is complete and crosses from the last cell into the first; the
    // only stop the bug added was the surrogate pair's middle. The line-end caret is drawn at
    // the last cell's end already (the trailing pipe is zero width in the formatted row), so
    // the first press starts from that boundary and the walk visits every other boundary.
    static void RunRowLeftWalk(MainWindow window,int width){
        string tag="row left walk "+width;
        string row="| 가"+UnicodeValues[0]+"나 | 끝 |";
        window.SetText("| 머리 | 끝 |\n| --- | --- |\n"+row+"\n");window.SetMode(true);LongLineBench.Idle();
        var document=window.source.Document;var line=document.GetLineByNumber(3);
        string text=document.GetText(line);
        List<MarkdownLivePreview.Cell> cells=MarkdownLivePreview.SplitCells(text);
        if(cells==null||cells.Count<2){Check(tag+" fixture",false,"cells");return;}
        List<int> expected=TextStops(text,cells);
        int lineEnd=cells[cells.Count-1].End;
        var visited=new List<int>();
        window.source.Select(line.EndOffset,0);LongLineBench.Idle();
        int last=line.Length;
        for(int i=0;i<32;i++){
            EditingCommands.MoveLeftByCharacter.Execute(null,window.source.TextArea);LongLineBench.Idle();
            int rel=window.source.CaretOffset-line.Offset;
            if(rel==last)break;
            visited.Add(rel);last=rel;
        }
        bool exact=expected.Contains(lineEnd)&&visited.Count==expected.Count-1;
        foreach(int rel in expected)if(rel!=lineEnd)exact&=visited.Contains(rel);
        foreach(int rel in visited)exact&=expected.Contains(rel);
        bool crosses=visited.Contains(cells[0].Start)&&visited.Contains(cells[cells.Count-1].Start);
        Check(tag+" Left from the row end visits every cell text stop and crosses cells",exact&&crosses,
            "visited="+JoinInts(visited)+", expected="+JoinInts(expected)+", text=["+FlatText(text)+"]");
    }
    // Every code point boundary inside the row's cell texts, in document order.
    static List<int> TextStops(string text,List<MarkdownLivePreview.Cell> cells){
        var stops=new List<int>();
        foreach(MarkdownLivePreview.Cell cell in cells){
            int at=cell.Start;
            stops.Add(at);
            while(at<cell.End){
                at+=char.IsHighSurrogate(text[at])&&at+1<text.Length&&char.IsLowSurrogate(text[at+1])?2:1;
                stops.Add(at);
            }
        }
        return stops;
    }
    // A cell box measured from the ROW's own origin (the paragraph start of its visual line),
    // which is what LiveTableSelectionLayer measures. TableCellBox assumes x = 0 is the row
    // origin; the live surface's page inset inside MainWindow does not hold that, so this suite
    // derives the origin from the visual line instead.
    static Rect EditModeCellBox(MarkdownLivePreview.Session session,VisualLine line,int cell){
        if(line==null||line.TextLines.Count==0)return Rect.Empty;
        MarkdownLivePreview.RowLayout row=session.Info(line.FirstDocumentLine).Row;
        if(row==null||cell<0||cell>=row.Texts.Length)return Rect.Empty;
        var first=line.TextLines[0];
        var last=line.TextLines[line.TextLines.Count-1];
        double origin=line.GetTextLineVisualXPosition(first,line.GetTextLineVisualStartColumn(first))-session.View.HorizontalOffset;
        double top=line.GetTextLineVisualYPosition(first,VisualYPosition.LineTop)-session.View.VerticalOffset;
        double bottom=line.GetTextLineVisualYPosition(last,VisualYPosition.LineBottom)-session.View.VerticalOffset;
        return new Rect(origin+row.Boundaries[cell],top,row.Widths[cell],Math.Max(1,bottom-top));
    }
    static string DescribeRects(List<Rect> rects){
        var text=new StringBuilder();
        for(int i=0;i<rects.Count;i++){if(i>0)text.Append(' ');text.Append('[').Append(Fmt(rects[i])).Append(']');}
        return text.ToString();
    }
}
}}
