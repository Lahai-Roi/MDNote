using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// Review probes for tonight's live preview table work. Generated fixtures only.
// 1. An append at a cell end after the row was resized while off screen must use the
//    current column width, not the slices shaped for the previous width.
// 2. A table drag whose pointer leaves the text viewport must scroll the view and keep
//    extending the selection instead of stopping at the visible edge.
// 3. A table press whose capture fails must not enter drag mode, and the autoscroll
//    must stop once the button is up or the capture is gone (no runaway scroll).
// 4. Empty <br> segments (trailing and doubled) must have their own caret stop so
//    Left/Right reach every source offset of the cell and Map/Reverse round-trip.
// 5. A very long Korean cell next to a short one must give the short column its one-line
//    width and the same column grid while typing and after the caret leaves the table.
// 6. Inline links on a formatted line: unmatched "[" before a link, brackets in the label and
//    an image target with its own parentheses must render like CommonMark/GFM.
// 7. Two tables with no blank line between: the second "| --- |" row is a data row of the
//    first table, not a header delimiter, and stays visible.
internal static class LiveTableReviewProbe {
    static StringBuilder report;
    static bool all;
    static int cases;
    internal static int Run(string outDir) {
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string pass=Path.Combine(outDir,"live-table-review-probe-PASS.txt");
        string fail=Path.Combine(outDir,"live-table-review-probe-FAIL.txt");
        if (File.Exists(pass)) File.Delete(pass);
        if (File.Exists(fail)) File.Delete(fail);
        report=new StringBuilder("Live table review: stale append widths, drag autoscroll and capture stop, <br> caret stops.\n");
        all=true;cases=0;
        bool oldSelfTest=Program.IsSelfTest;Application app=null;
        ShutdownMode oldShutdown=ShutdownMode.OnExplicitShutdown;
        try {
            Program.IsSelfTest=true;
            if (Application.Current==null) new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app=Application.Current;oldShutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            // One failing case must not hide the remaining ones.
            try {RunResizeAppend();} catch (Exception ex) {Check("resize-append exception",false,ex.ToString());}
            try {RunLongCellStability();} catch (Exception ex) {Check("long-cell exception",false,ex.ToString());}
            try {RunDragAutoscroll();} catch (Exception ex) {Check("drag-autoscroll exception",false,ex.ToString());}
            try {RunDragCaptureCases();} catch (Exception ex) {Check("drag-capture exception",false,ex.ToString());}
            try {RunBreakStops();} catch (Exception ex) {Check("br-stops exception",false,ex.ToString());}
            try {RunTableTypingCases();} catch (Exception ex) {Check("table-typing exception",false,ex.ToString());}
            try {RunLinkLabelCutCase();} catch (Exception ex) {Check("link-label-cut exception",false,ex.ToString());}
            try {RunInlineLinkCases();} catch (Exception ex) {Check("inline-link exception",false,ex.ToString());}
            try {RunAdjacentTableRows();} catch (Exception ex) {Check("adjacent-table exception",false,ex.ToString());}
        } catch (Exception ex) {all=false;report.Append("EXCEPTION ").Append(ex).Append('\n');}
        finally {Program.IsSelfTest=oldSelfTest;if (app!=null) app.ShutdownMode=oldShutdown;}
        report.Append("Cases: ").Append(cases).Append("\nResult: ").Append(all ? "PASS" : "FAIL").Append('\n');
        try {
            File.WriteAllText(Path.Combine(outDir,"live-table-review-probe.txt"),report.ToString(),new UTF8Encoding(false));
            File.WriteAllText(all ? pass : fail,report.ToString(),new UTF8Encoding(false));
        } catch (IOException) {return 1;}
        return all ? 0 : 1;
    }
    static void Check(string name,bool ok,string detail) {
        cases++;all&=ok;
        report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(' ').Append(detail).Append('\n');
    }
    // Case 1 ---------------------------------------------------------------------
    // A long cell so the wide row still wraps into more than one slice (the append path
    // copies the already-broken prefix, which is what goes stale across a width change).
    static readonly string ResizeCell=RepeatText("alpha beta gamma delta epsilon zeta eta theta iota kappa lambda ",8);
    static void RunResizeAppend() {
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=CreateWindow(editor,1800,900);
            session=Attach(editor);
            editor.Text=ResizeFixture();editor.CaretIndex=0;Pump();
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            DocumentLine row=FindDataRow(session,editor.Document);
            if (row==null) {Check("resize-append fixture table",false,"no data row");return;}
            var view=editor.TextArea.TextView;
            editor.ScrollToLine(row.LineNumber);Pump();
            view.GetOrConstructVisualLine(row);Pump();
            MarkdownLivePreview.LineInfo info=session.Info(row);
            if (info.Cells==null || info.Cells.Count==0) {Check("resize-append fixture cells",false,"no cells");return;}
            double wide=session.LayoutRow(info,session.Raw(row)).Widths[0];
            // Append at the end of the first cell so the fast path runs at the wide width.
            int cellEnd=row.Offset+info.Cells[0].End;
            editor.Select(cellEnd,0);Pump();
            editor.TextArea.PerformTextInput("Q");Pump();
            editor.TextArea.PerformTextInput("W");Pump();
            // Force the wide raw row to be laid out so the append path has run at least
            // once before the row leaves the viewport.
            editor.ScrollToLine(row.LineNumber);Pump();
            view.GetOrConstructVisualLine(row);Pump();
            // Scroll the row (and the caret, which stays in the cell) out of view so its
            // parse is evicted, then resize. The row is laid out again at the new width.
            editor.Select(cellEnd+2,0);Pump();
            editor.ScrollToLine(1);Pump();
            window.Width=420;window.UpdateLayout();Pump();
            editor.ScrollToLine(row.LineNumber);Pump();
            view.GetOrConstructVisualLine(row);Pump();
            info=session.Info(row);
            MarkdownLivePreview.RowLayout layout=session.LayoutRow(info,session.Raw(row));
            double narrow=layout.Widths[0];
            string detail;
            bool matched=CompareFresh(session,info,layout,0,out detail);
            Check("resize-append slices match current width",matched&&wide!=narrow,
                detail+", wide="+Number(wide)+", narrow="+Number(narrow)+", raw="+session.Raw(row)+", rows="+layout.PhysicalRows);
            // The reported sequence types once more after scrolling back.
            editor.TextArea.PerformTextInput("E");Pump();
            info=session.Info(row);
            layout=session.LayoutRow(info,session.Raw(row));
            bool retyped=CompareFresh(session,info,layout,0,out detail);
            Check("resize-append after retype",retyped,detail);
            Check("resize-append typed text kept",info.Text == editor.Document.GetText(editor.Document.GetLineByNumber(row.LineNumber)),
                "source and parse text agree");
        } finally {
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
        }
    }
    static string ResizeFixture() {
        var text=new StringBuilder();
        for (int i=1;i<=60;i++) text.Append("line ").Append(i).Append('\n');
        text.Append('\n');
        text.Append("| header one | header two | header three |\n");
        text.Append("|---|---|---|\n");
        text.Append("| ").Append(ResizeCell).Append(" | b | c |\n");
        text.Append("| x | y | z |\n");
        text.Append("\ntail\n");
        return text.ToString();
    }
    // Compare the row's cached slices with a fresh format of the same text at the same
    // width. A stale append would keep the previous width's wrap points here.
    static bool CompareFresh(MarkdownLivePreview.Session session,MarkdownLivePreview.LineInfo info,MarkdownLivePreview.RowLayout layout,int c,out string detail) {
        List<MarkdownLivePreview.CellSlice> actual=layout.Slices[c];
        double width=Math.Max(1,layout.Widths[c]-layout.Padding*2-1);
        List<MarkdownLivePreview.CellSlice> fresh=session.FormatCell(info,info.Cells[c],layout.Texts[c],layout.Table,width,false,null);
        detail="column="+Number(layout.Widths[c])+", text width="+Number(width)+", actual="+Describe(actual)+", fresh="+Describe(fresh);
        if (actual.Count!=fresh.Count) return false;
        for (int i=0;i<actual.Count;i++)
            if (actual[i].Start!=fresh[i].Start || actual[i].Length!=fresh[i].Length || Math.Abs(actual[i].Width-fresh[i].Width)>.01) return false;
        return true;
    }
    static string Describe(List<MarkdownLivePreview.CellSlice> slices) {
        var text=new StringBuilder();
        for (int i=0;i<slices.Count;i++) {
            if (i>0) text.Append(';');
            text.Append(slices[i].Start).Append('+').Append(slices[i].Length).Append('@').Append(Number(slices[i].Width));
        }
        return text.ToString();
    }
    static DocumentLine FindDataRow(MarkdownLivePreview.Session session,TextDocument document) {
        for (int number=1;number<=document.LineCount;number++) {
            DocumentLine line=document.GetLineByNumber(number);
            MarkdownLivePreview.LineInfo info=session.Info(line);
            if (info.Table && !info.Delimiter && !info.Header && info.Cells!=null && info.Cells.Count>=3) return line;
        }
        return null;
    }
    // Case 5 ---------------------------------------------------------------------
    // A 60-syllable Korean cell next to a nine-character one. The settled table used to
    // stretch to the whole view and squeeze the short column below its one-line width (its
    // cell wrapped to two lines), while typing in the long cell laid the table out
    // narrower, so the table jumped as soon as the caret left it. The allocation now gives
    // the short column its content width first and bounds the table's total instead of
    // capping the column being typed in.
    static readonly string LongKoreanCell=RepeatText("가나다라마바사아자차카타파하",5).Substring(0,60);
    static void RunLongCellStability() {
        const string ShortCell="첫 번째 줄에ほんご";
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=CreateWindow(editor,900,700);
            session=Attach(editor);
            editor.Text=LongCellFixture(ShortCell);editor.CaretIndex=0;Pump();
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            DocumentLine row=FindColumns(session,editor.Document,2);
            if (row==null) {Check("long-cell fixture table",false,"no two-column data row");return;}
            var view=editor.TextArea.TextView;
            editor.ScrollToLine(row.LineNumber);Pump();
            view.GetOrConstructVisualLine(row);Pump();
            MarkdownLivePreview.LineInfo info=session.Info(row);
            editor.Select(row.Offset+info.Cells[0].End,0);Pump();
            // Type in the long cell: the column grows to the typed line and the table's
            // total stays at the view width (the slack comes from the long column itself).
            editor.TextArea.PerformTextInput("가");Pump();
            view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(row.LineNumber));Pump();
            List<int> typingNumbers;List<MarkdownLivePreview.RowLayout> typing=TableRows(session,editor.Document,out typingNumbers);
            bool typingSpans=typing.Count>0 && Total(typing[0])>=typing[0].BaseWidth-4.5;
            // Leave the table; the idle re-sample publishes the settled grid.
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            Wait(600);
            typeof(MarkdownLivePreview.Session).GetMethod("TableIdle",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(session,new object[] {null,EventArgs.Empty});
            Pump();
            List<int> settledNumbers;List<MarkdownLivePreview.RowLayout> settled=TableRows(session,editor.Document,out settledNumbers);
            bool oneSlice=true,fits=true,spans=true,stable=true;
            string sliceDetail="",fitDetail="",stableDetail="";
            for (int i=0;i<settled.Count;i++) {
                MarkdownLivePreview.RowLayout layout=settled[i];
                if (layout.Slices.Length<2 || layout.Slices[1]==null || layout.Slices[1].Count!=1) {
                    oneSlice=false;
                    sliceDetail+=" row"+settledNumbers[i]+" slices="+(layout.Slices.Length<2 || layout.Slices[1]==null ? -1 : layout.Slices[1].Count);
                }
                double total=Total(layout);
                if (total>layout.BaseWidth+.05 || total>view.ActualWidth+.05) {
                    fits=false;
                    fitDetail+=" row"+settledNumbers[i]+" total="+Number(total)+" base="+Number(layout.BaseWidth)+" view="+Number(view.ActualWidth);
                }
                if (total<layout.BaseWidth-4.5) spans=false;
            }
            for (int i=0;i<typing.Count;i++) {
                int number=typingNumbers[i],at=settledNumbers.IndexOf(number);
                if (at<0) { stable=false;stableDetail+=" row"+number+" missing after leave";continue; }
                MarkdownLivePreview.RowLayout before=typing[i],after=settled[at];
                if (before.PhysicalRows!=after.PhysicalRows) {
                    stable=false;
                    stableDetail+=" row"+number+" lines "+before.PhysicalRows+"->"+after.PhysicalRows;
                }
                if (before.Boundaries.Length!=after.Boundaries.Length) {
                    stable=false;
                    stableDetail+=" row"+number+" columns "+before.Boundaries.Length+"->"+after.Boundaries.Length;
                } else for (int c=0;c<before.Boundaries.Length;c++)
                    if (Math.Abs(before.Boundaries[c]-after.Boundaries[c])>3) {
                        stable=false;
                        stableDetail+=" row"+number+" x"+c+" "+Number(before.Boundaries[c])+"->"+Number(after.Boundaries[c]);
                    }
            }
            Check("long-cell short column stays one line",oneSlice&&settled.Count>=2,
                sliceDetail.Length==0 ? settled.Count+" table rows, one slice on the short column" : sliceDetail);
            Check("long-cell table fits the view",fits&&settled.Count>=2,
                fitDetail.Length==0 ? "total "+Number(settled.Count==0 ? 0 : Total(settled[0]))+"/"+Number(settled.Count==0 ? 0 : settled[0].BaseWidth) : fitDetail);
            Check("long-cell settled table spans the view",spans&&settled.Count>=2,
                "total="+Number(settled.Count==0 ? 0 : Total(settled[0]))+", base="+Number(settled.Count==0 ? 0 : settled[0].BaseWidth));
            Check("long-cell typing already spans the view",typingSpans,
                "typing total="+Number(typing.Count==0 ? 0 : Total(typing[0]))+", base="+Number(typing.Count==0 ? 0 : typing[0].BaseWidth));
            Check("long-cell grid stable when the caret leaves",stable&&typing.Count>=2&&settled.Count>=2,
                stableDetail.Length==0 ? "rows and boundaries agree within 3px" : stableDetail);
        } finally {
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
        }
    }
    static string LongCellFixture(string shortCell) {
        var text=new StringBuilder();
        for (int i=1;i<=6;i++) text.Append("line ").Append(i).Append('\n');
        text.Append('\n');
        text.Append("| 긴 칸 | 짧은 칸 |\n");
        text.Append("|---|---|\n");
        text.Append("| ").Append(LongKoreanCell).Append(" | ").Append(shortCell).Append(" |\n");
        text.Append("\ntail one\ntail two\n");
        return text.ToString();
    }
    static DocumentLine FindColumns(MarkdownLivePreview.Session session,TextDocument document,int columns) {
        for (int number=1;number<=document.LineCount;number++) {
            DocumentLine line=document.GetLineByNumber(number);
            MarkdownLivePreview.LineInfo info=session.Info(line);
            if (info.Table && !info.Delimiter && !info.Header && info.Cells!=null && info.Cells.Count==columns) return line;
        }
        return null;
    }
    // Every header and body row of every table, laid out as the formatted projection so the
    // caret's own raw row is compared on the same footing.
    static List<MarkdownLivePreview.RowLayout> TableRows(MarkdownLivePreview.Session session,TextDocument document,out List<int> numbers) {
        numbers=new List<int>();
        var layouts=new List<MarkdownLivePreview.RowLayout>();
        for (int number=1;number<=document.LineCount;number++) {
            MarkdownLivePreview.LineInfo info=session.Info(document.GetLineByNumber(number));
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0) continue;
            numbers.Add(number);
            layouts.Add(session.LayoutRow(info,false));
        }
        return layouts;
    }
    static double Total(MarkdownLivePreview.RowLayout layout) {
        return layout.Boundaries[layout.Boundaries.Length-1]-layout.Boundaries[0];
    }
    // Case 2 ---------------------------------------------------------------------
    static void RunDragAutoscroll() {
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=CreateWindow(editor,900,460);
            session=Attach(editor);
            editor.Text=DragFixture();editor.CaretIndex=0;Pump();
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            DocumentLine row=FindDataRow(session,editor.Document);
            if (row==null) {Check("drag fixture table",false,"no data row");return;}
            var view=editor.TextArea.TextView;
            editor.ScrollToLine(row.LineNumber);Pump();
            VisualLine visual=view.GetOrConstructVisualLine(row);Pump();
            Rect box=session.TableCellBox(visual,0);
            if (box.IsEmpty || box.Width<4 || box.Height<4) {Check("drag fixture cell box",false,"box="+box);return;}
            int anchorAt=row.Offset+session.Info(row).Cells[0].Start;
            editor.Select(anchorAt,0);Pump();
            visual=view.GetOrConstructVisualLine(row);Pump();
            box=session.TableCellBox(visual,0);
            bool placed=session.PlaceTableCaret(new Point(box.Left+Math.Min(6,box.Width/2),box.Top+box.Height/2),false);
            int anchor=editor.CaretOffset;
            double before=view.VerticalOffset;
            session.BeginTableDrag(anchor);
            session.TableDragStep(new Point(view.ActualWidth*.5,view.ActualHeight+40));
            Wait(250);
            double after=view.VerticalOffset;
            int end=editor.CaretOffset;
            Check("drag below viewport scrolls",after>before+.5,"from="+Number(before)+", to="+Number(after));
            Check("drag below viewport extends selection",placed && editor.SelectionLength>0 && end>anchor,
                "placed="+placed+", anchor="+anchor+", caret="+end+", selection="+editor.SelectionLength);
            session.EndTableDrag();
            // A stopped timer must leave the offset alone once the drag is over.
            Wait(60);
            double stopped=view.VerticalOffset;Wait(160);
            Check("drag timer stops on release",Math.Abs(view.VerticalOffset-stopped)<2,"offset="+Number(stopped)+" -> "+Number(view.VerticalOffset));
        } finally {
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
        }
    }
    static string DragFixture() {
        var text=new StringBuilder("head\n\n| a | b | c |\n|---|---|---|\n");
        for (int i=0;i<40;i++) text.Append("| row ").Append(i).Append(" alpha beta | b").Append(i).Append(" | c").Append(i).Append(" |\n");
        text.Append("\ntail one\ntail two\ntail three\n");
        return text.ToString();
    }
    // Case 3 ---------------------------------------------------------------------
    // A press that cannot capture must not start a drag (no MouseUp/LostMouseCapture
    // would ever end it), and a marked drag must stop the autoscroll as soon as the
    // button is up or the capture is gone instead of scrolling until the next click.
    static void RunDragCaptureCases() {
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=CreateWindow(editor,900,460);
            session=Attach(editor);
            editor.Text=DragFixture();editor.CaretIndex=0;Pump();
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            DocumentLine row=FindDataRow(session,editor.Document);
            if (row==null) {Check("drag-capture fixture table",false,"no data row");return;}
            var view=editor.TextArea.TextView;
            editor.ScrollToLine(row.LineNumber);Pump();
            view.GetOrConstructVisualLine(row);Pump();
            int anchor=row.Offset+session.Info(row).Cells[0].Start;
            // WPF refuses capture on a disabled element, so the press must keep the drag
            // off instead of leaving an anchor/autoscroll with no release to end it.
            editor.TextArea.IsEnabled=false;Pump();
            bool captured=session.BeginTablePressDrag(anchor);
            bool active=session.TableDragActive;
            editor.TextArea.IsEnabled=true;Pump();
            if (editor.TextArea.IsMouseCaptured) editor.TextArea.ReleaseMouseCapture();
            Check("press without capture does not start drag",!captured && !active,
                "captured="+captured+", active="+active);
            // Settle the caret and keyboard focus first, like the mouse path does, so the
            // offset below only moves if the autoscroll timer really scrolls.
            VisualLine visual=view.GetOrConstructVisualLine(row);Pump();
            Rect box=session.TableCellBox(visual,0);
            if (box.IsEmpty || box.Width<4 || box.Height<4) {Check("drag-capture cell box",false,"box="+box);return;}
            bool placed=session.PlaceTableCaret(new Point(box.Left+Math.Min(6,box.Width/2),box.Top+box.Height/2),false);
            if (placed) anchor=editor.CaretOffset;
            Pump();
            // The probe holds no mouse button and takes no capture: the first autoscroll
            // tick of a pointer-guarded drag must end it, not scroll the view.
            session.BeginTableDragWithPointerGuard(anchor);
            double before=view.VerticalOffset;
            session.TableDragStep(new Point(view.ActualWidth*.5,view.ActualHeight+40));
            Wait(250);
            double after=view.VerticalOffset;
            Check("guarded autoscroll stops without capture",!session.TableDragActive && Math.Abs(after-before)<2,
                "from="+Number(before)+", to="+Number(after)+", active="+session.TableDragActive);
            session.EndTableDrag();
        } finally {
            if (editor!=null) editor.TextArea.IsEnabled=true;
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
        }
    }
    // Case 4 ---------------------------------------------------------------------
    static void RunBreakStops() {
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=CreateWindow(editor,1400,900);
            session=Attach(editor);
            editor.Text="head\n\n| one | two | three |\n|---|---|---|\n| foo<br> | a<br><br>b | plain |\n\ntail\n";
            editor.CaretIndex=0;Pump();
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            DocumentLine row=editor.Document.GetLineByNumber(5);
            var view=editor.TextArea.TextView;
            editor.ScrollToLine(5);Pump();
            view.GetOrConstructVisualLine(row);Pump();
            VisualLine visual=view.GetVisualLine(5);
            MarkdownLivePreview.LineInfo info=session.Info(row);
            MarkdownLivePreview.RowLayout layout=session.LayoutRow(info,false);
            Check("br fixture row projection",info.Cells!=null && info.Cells.Count==3 && visual!=null && layout!=null,
                "cells="+(info.Cells==null ? -1 : info.Cells.Count)+", visual="+(visual!=null)+", layout="+(layout!=null));
            if (info.Cells==null || info.Cells.Count<2 || visual==null) return;
            // foo<br>: the empty tail after the tag owns the cell-end offset.
            CheckBreakStop("br trailing",visual,info.Cells[0].Start,info.Cells[0].End,info.Cells[0].End);
            // a<br><br>b: the empty segment between the two tags owns the junction offset.
            string middle=info.Text.Substring(info.Cells[1].Start,info.Cells[1].End-info.Cells[1].Start);
            int junction=info.Cells[1].Start+middle.IndexOf('>')+1;
            Check("br middle cell text",middle=="a<br><br>b","text="+middle);
            CheckBreakStop("br junction",visual,info.Cells[1].Start,info.Cells[1].End,junction);
        } finally {
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
        }
    }
    static void CheckBreakStop(string label,VisualLine visual,int startRel,int endRel,int targetRel) {
        int column=visual.GetVisualColumn(targetRel);
        Check(label+" stop round-trip",visual.GetRelativeOffset(column)==targetRel,
            "target="+targetRel+", column="+column+", back="+visual.GetRelativeOffset(column));
        int before=visual.GetNextCaretPosition(column-1,LogicalDirection.Forward,CaretPositioningMode.Normal,false);
        Check(label+" stop is canonical",before==column,"next(col-1)="+before+", column="+column);
        List<int> forward;bool forwardOk=WalkStops(visual,startRel,endRel,out forward);
        Check(label+" Left/Right walk visits every stop",forwardOk && forward.Contains(targetRel),
            "visited="+Join(forward)+", target="+targetRel);
        List<int> backward;bool backwardOk=WalkBack(visual,startRel,endRel,out backward);
        Check(label+" backward walk visits every stop",backwardOk && (backward.Contains(targetRel) || targetRel==endRel),
            "visited="+Join(backward)+", target="+targetRel);
        // The empty segment must be its own physical row, not folded onto the first line.
        bool emptyRow=false;
        if (visual.TextLines.Count>=2) {
            TextLine line=visual.GetTextLine(column);
            emptyRow=!Object.ReferenceEquals(line,visual.TextLines[0]);
        }
        Check(label+" empty physical row",emptyRow,"textLines="+visual.TextLines.Count);
    }
    static bool WalkStops(VisualLine visual,int startRel,int endRel,out List<int> visited) {
        visited=new List<int>();
        int column=visual.GetVisualColumn(startRel),last=startRel,guard=0;
        while (guard++<4000) {
            int next=visual.GetNextCaretPosition(column,LogicalDirection.Forward,CaretPositioningMode.Normal,false);
            if (next<0) break;
            int rel=visual.GetRelativeOffset(next);
            if (visual.GetVisualColumn(rel)!=next || rel<=last) return false;
            visited.Add(rel);last=rel;column=next;
            if (rel>=endRel) break;
        }
        return visited.Count>0 && last>=endRel;
    }
    static bool WalkBack(VisualLine visual,int startRel,int endRel,out List<int> visited) {
        visited=new List<int>();
        int column=visual.GetVisualColumn(endRel),last=endRel,guard=0;
        while (guard++<4000) {
            int next=visual.GetNextCaretPosition(column,LogicalDirection.Backward,CaretPositioningMode.Normal,false);
            if (next<0) break;
            int rel=visual.GetRelativeOffset(next);
            if (visual.GetVisualColumn(rel)!=next || rel>=last) return false;
            visited.Add(rel);last=rel;column=next;
            if (rel<=startRel) break;
        }
        return visited.Count>0 && last<=startRel;
    }
    static string Join(List<int> values) {
        var text=new StringBuilder();
        for (int i=0;i<values.Count;i++) {if (i>0) text.Append(',');text.Append(values[i]);}
        return text.ToString();
    }
    // Case 6 ---------------------------------------------------------------------
    // Typing and caret rules of a table row: a typed "|" is cell text ("\|"), the escaped pair
    // is one caret unit, a caret never leaves the row's cells, and a revealed closer behaves
    // like the text it looks like. Every case is driven through a real SourceEditor and its
    // attached preview; both marker modes are covered.
    static void RunTableTypingCases() {
        foreach (bool hide in new[]{false,true}) {
            SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
            try {
                editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
                window=CreateWindow(editor,900,700);
                session=Attach(editor,hide);
                string mode=hide?"hidden":"raw";
                // H1: a typed pipe inside a cell is escaped and adds no column.
                string fixture="| a | b |\n| --- | --- |\n| x | y |\n\ntail\n";
                editor.Text=fixture;editor.CaretIndex=0;Pump();
                DocumentLine line=editor.Document.GetLineByNumber(1);
                MarkdownLivePreview.LineInfo info=session.Info(line);
                editor.Select(line.Offset+info.Cells[0].End,0);Pump();
                editor.TextArea.PerformTextInput("|");Pump();
                info=session.Info(editor.Document.GetLineByNumber(1));
                string row=editor.Document.GetText(editor.Document.GetLineByNumber(1));
                Check("table pipe typed in header cell "+mode,row=="| a\\| | b |"&&info.Cells!=null&&info.Cells.Count==2
                    &&editor.CaretOffset==line.Offset+info.Cells[0].End,"row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count)+", caret="+editor.CaretOffset);
                editor.Text=fixture;editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(3);info=session.Info(line);
                editor.Select(line.Offset+info.Cells[0].End,0);Pump();
                editor.TextArea.PerformTextInput("|");Pump();
                info=session.Info(editor.Document.GetLineByNumber(3));
                row=editor.Document.GetText(editor.Document.GetLineByNumber(3));
                Check("table pipe typed in body cell "+mode,row=="| x\\| | y |"&&info.Cells!=null&&info.Cells.Count==2,
                    "row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count));
                // H2: the caret never rests between "\" and "|", and both deletes take the pair.
                string escaped="| a\\|b | c |\n| --- | --- |\n| x | y |\n\ntail\n";
                editor.Text=escaped;editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(1);info=session.Info(line);
                int between=line.Offset+info.Cells[0].Start+2;
                editor.Select(between,0);Pump();
                Check("escaped pipe caret snaps after the unit "+mode,editor.CaretOffset==between+1,
                    "caret="+editor.CaretOffset+", between="+between);
                EditingCommands.Backspace.Execute(null,editor.TextArea);Pump();
                row=editor.Document.GetText(editor.Document.GetLineByNumber(1));
                info=session.Info(editor.Document.GetLineByNumber(1));
                Check("backspace takes the escaped pipe pair "+mode,row=="| ab | c |"&&info.Cells.Count==2,
                    "row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count));
                editor.Text=escaped;editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(1);info=session.Info(line);
                int beforeUnit=line.Offset+info.Cells[0].Start+1;
                editor.Select(beforeUnit,0);Pump();
                EditingCommands.Delete.Execute(null,editor.TextArea);Pump();
                row=editor.Document.GetText(editor.Document.GetLineByNumber(1));
                info=session.Info(editor.Document.GetLineByNumber(1));
                Check("delete takes the escaped pipe pair "+mode,row=="| ab | c |"&&info.Cells.Count==2,
                    "row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count));
                // R7: on a revealed line the closer is text, so a typed character stays after it.
                if (!hide) {
                    editor.Text="**ab**\n\ntail\n";editor.CaretIndex=0;Pump();
                    line=editor.Document.GetLineByNumber(1);
                    editor.Select(line.Offset+6,0);Pump();
                    editor.TextArea.PerformTextInput("x");Pump();
                    Check("revealed closer stays text",editor.Text=="**ab**x\n\ntail\n",
                        "text=["+editor.Text.Replace("\n","\\n")+"]");
                }
                // H10: the end of a cell is the cell's boundary, even when a closer ends there.
                string boldCell="| h | b |\n| --- | --- |\n| **bold** | y |\n\ntail\n";
                editor.Text=boldCell;editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(3);info=session.Info(line);
                editor.Select(line.Offset+info.Cells[0].End,0);Pump();
                editor.TextArea.PerformTextInput("Q");Pump();
                row=editor.Document.GetText(editor.Document.GetLineByNumber(3));
                info=session.Info(editor.Document.GetLineByNumber(3));
                Check("typed text at a bold cell end stays at the cell end "+mode,row=="| **bold**Q | y |"&&info.Cells.Count==2,
                    "row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count));
                // Typing over a selection that sits in a row's padding/pipe keeps the row.
                string padSel="| a | b |\n| --- | --- |\n| c | d |\n\ntail\n";
                editor.Text=padSel;editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(1);info=session.Info(line);
                int padStart=line.Offset+info.Cells[1].End;
                int padEnd=line.Offset+line.Length;
                editor.Select(padStart,padEnd-padStart);Pump();
                editor.TextArea.PerformTextInput("Z");Pump();
                row=editor.Document.GetText(editor.Document.GetLineByNumber(1));
                info=session.Info(editor.Document.GetLineByNumber(1));
                Check("typing over a row padding selection keeps the row "+mode,info.Cells!=null&&info.Cells.Count==2&&row.IndexOf('|')>=0,
                    "row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count));
                // H7/H8b: a caret or selection endpoint of a row stays inside a cell.
                string rows="| a | b |\n| --- | --- |\n| c | d |\n\ntail\n";
                editor.Text=rows;editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(3);info=session.Info(line);
                int rowEnd=line.Offset+line.Length;
                int lastEnd=line.Offset+info.Cells[1].End;
                editor.Select(line.Offset,0);Pump();
                Check("caret before the leading pipe enters the first cell "+mode,editor.CaretOffset==line.Offset+info.Cells[0].Start,
                    "caret="+editor.CaretOffset+", cell="+(line.Offset+info.Cells[0].Start));
                DocumentLine below=editor.Document.GetLineByNumber(4);
                editor.Select(below.Offset,0);Pump();
                EditingCommands.SelectLeftByCharacter.Execute(null,editor.TextArea);Pump();
                Check("shift-left from below stays in the last cell "+mode,editor.CaretOffset==lastEnd,
                    "caret="+editor.CaretOffset+", cellEnd="+lastEnd+", rowEnd="+rowEnd);
                editor.Select(lastEnd,0);Pump();
                EditingCommands.SelectToLineEnd.Execute(null,editor.TextArea);Pump();
                Check("shift-end in a row stays in the last cell "+mode,editor.CaretOffset==lastEnd,
                    "caret="+editor.CaretOffset+", cellEnd="+lastEnd+", rowEnd="+rowEnd);
                // An interior row's own line end is not a cell stop either.
                editor.Text=rows;editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(1);info=session.Info(line);
                editor.Select(line.Offset+line.Length,0);Pump();
                Check("interior row end caret clamps to the last cell "+mode,
                    editor.CaretOffset==line.Offset+info.Cells[1].End,"caret="+editor.CaretOffset+", cells="+info.Cells[1].Start+".."+info.Cells[1].End);
                // R6: a colon-only or short delimiter row is not a table.
                editor.Text="| a | b |\n|:|-|\n\ntail\n";editor.CaretIndex=0;Pump();
                MarkdownLivePreview.LineInfo bad=session.Info(editor.Document.GetLineByNumber(1));
                Check("colon-only delimiter is not a table "+mode,!bad.Table,
                    "table="+bad.Table+", header="+bad.Header);
                editor.Text="| a | b |\n|-|\n\ntail\n";editor.CaretIndex=0;Pump();
                bad=session.Info(editor.Document.GetLineByNumber(1));
                Check("mismatched one-dash delimiter is not a table "+mode,!bad.Table,
                    "table="+bad.Table+", header="+bad.Header);
                // H11: the composition point of a right-aligned cell stays inside its cell.
                string right="| left | right |\n| --- | ---: |\n| aa | bb |\n\ntail\n";
                editor.Text=right;editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(3);info=session.Info(line);
                editor.Select(line.Offset+info.Cells[1].End,0);Pump();
                editor.ScrollToLine(3);Pump();
                VisualLine visual=editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(3));
                Rect box=session.TableCellBox(visual,1);
                double compSize;Typeface compFace;Point comp=LiveCompositionGeometry.Position(editor,out compSize,out compFace);
                Check("composition point stays in the right-aligned cell "+mode,
                    !box.IsEmpty&&!Double.IsNaN(comp.X)&&comp.X>=box.Left-.5&&comp.X<=box.Right+.5,
                    "x="+Number(comp.X)+", box="+Number(box.Left)+".."+Number(box.Right)+", drawn="+Number(session.DrawnTableCaret.X));
                // E1: deleting a whole cell's text leaves the caret where the delete ended (the
                // now empty cell's leading padding); it must not be clamped one offset right, so
                // the next character lands exactly at the caret. The long typing probe saw 600
                // misplaced inputs from that one-offset clamp.
                editor.Text="| 셀 0 가 | b |\n| --- | --- |\n";editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(1);info=session.Info(line);
                int deleteFrom=line.Offset+info.Cells[0].End-5;
                editor.Select(line.Offset+info.Cells[0].End,0);Pump();
                editor.Document.Remove(deleteFrom,5);Pump();
                int afterDelete=editor.CaretIndex;
                editor.TextArea.PerformTextInput("가");Pump();
                row=editor.Document.GetText(editor.Document.GetLineByNumber(1));
                info=session.Info(editor.Document.GetLineByNumber(1));
                // The raw row draws the empty cell's padding, so the caret stays exactly where
                // the delete ended; the hidden row snaps it to the cell's drawn start (same
                // drawn position), where the typed character still lands at the caret.
                Check("delete that empties a cell keeps the caret and the typed text "+mode,
                    editor.Document.GetText(afterDelete,1)=="가"&&info.Cells!=null&&info.Cells.Count==2&&row.IndexOf("가",StringComparison.Ordinal)>0,
                    "caret="+editor.CaretIndex+", afterDelete="+afterDelete+", row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count));
                // E2: a column removal applied in one update must publish the new grid at once
                // (the idle re-sample is deferred while the caret stays inside the table).
                editor.Text="| a | b | c |\n| --- | --- | --- |\n| 1 | 2 | 3 |\n| 4 | 5 | 6 |\n";editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(4);info=session.Info(line);
                editor.Select(line.Offset+info.Cells[1].Start,0);Pump();
                CommandResult remove=MarkdownTableCommands.RemoveColumn(editor.Document,editor.CaretIndex);
                if (remove!=null) {
                    editor.Document.BeginUpdate();
                    try { foreach (SourceEdit edit in remove.Edits) editor.Document.Replace(edit.Offset,edit.Length,edit.Text); }
                    finally { editor.Document.EndUpdate(); }
                    editor.Select(Math.Min(remove.SelectionStart,editor.Document.TextLength),0);Pump();
                }
                info=session.Info(editor.Document.GetLineByNumber(4));
                Check("column removal republishes the grid "+mode,
                    remove!=null&&info.Cells!=null&&info.Cells.Count==2&&info.Row!=null&&info.Row.Texts.Length==2,
                    "remove="+(remove!=null)+", cells="+(info.Cells==null?-1:info.Cells.Count)+", row="+(info.Row==null?-1:info.Row.Texts.Length));
                // E4: typing over a selection that covers a cell's padding and a separator pipe
                // keeps every pipe; the text goes to the nearest cell edge, never the raw range.
                editor.Text="| <br> |  |\n|:---|---|\n| x | y |\n";editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(1);info=session.Info(line);
                int selStart=line.Offset+info.Cells[0].End;
                int selEnd=line.Offset+info.Cells[1].Start;
                editor.Select(selStart,selEnd-selStart);Pump();
                editor.TextArea.PerformTextInput("Z");Pump();
                row=editor.Document.GetText(editor.Document.GetLineByNumber(1));
                info=session.Info(editor.Document.GetLineByNumber(1));
                Check("typing over padding and a separator keeps the pipes "+mode,
                    info.Cells!=null&&info.Cells.Count==2&&row.StartsWith("|",StringComparison.Ordinal)&&row.EndsWith("|",StringComparison.Ordinal),
                    "row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count));
                // E5: Backspace never takes a row's leading pipe (the row emptied to "|   " keeps
                // it, and the block keeps its line). A row's line break is structure too.
                editor.Text="| a | b |\n| --- | --- |\n| c | d |\n|   \n";editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(4);
                int linesBefore=editor.Document.LineCount;
                editor.Select(line.Offset+1,0);Pump();
                EditingCommands.Backspace.Execute(null,editor.TextArea);Pump();
                Check("backspace keeps a row's leading pipe "+mode,
                    editor.Document.GetText(editor.Document.GetLineByNumber(4))=="|   "&&editor.Document.LineCount==linesBefore,
                    "line=["+editor.Document.GetText(editor.Document.GetLineByNumber(4))+"]");
                // E6: Enter after a lone literal "*" in a cell writes the <br> before the marker,
                // so the marker keeps the whitespace on its right and is not split into a pair.
                editor.Text="| a | b |\n| --- | --- |\n| *     | y |\n";editor.CaretIndex=0;Pump();
                line=editor.Document.GetLineByNumber(3);info=session.Info(line);
                int star=line.Offset+info.Cells[0].Start+1;
                CommandResult enter=MarkdownTableCommands.EnterInCell(editor.Document,star,0,info.Cells);
                if (enter!=null) {
                    editor.Document.BeginUpdate();
                    try { foreach (SourceEdit edit in enter.Edits) editor.Document.Replace(edit.Offset,edit.Length,edit.Text); }
                    finally { editor.Document.EndUpdate(); }
                    editor.Select(Math.Min(enter.SelectionStart,editor.Document.TextLength),0);Pump();
                }
                row=editor.Document.GetText(editor.Document.GetLineByNumber(3));
                info=session.Info(editor.Document.GetLineByNumber(3));
                Check("cell enter after a lone marker writes the break before it "+mode,
                    enter!=null&&row=="| <br>*     | y |"&&info.Cells!=null&&info.Cells.Count==2,
                    "row=["+row+"], cells="+(info.Cells==null?-1:info.Cells.Count));
            } finally {
                if (session!=null) session.Dispose();
                if (window!=null) window.Close();
            }
        }
    }
    // E3: a link label owns its inline markup (GFM parses emphasis and tags inside "[...]"),
    // so the J4 widening sees the pair under the link and a cut of the label's visible text
    // takes the markers, the span and the label with it - it never leaves "****" on screen.
    static void RunLinkLabelCutCase() {
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=CreateWindow(editor,900,700);
            session=Attach(editor,false);
            string plainLine="[**링크**](https://example.com/linked)";
            string nestedLine="- ##### [<span style=\"font-size:28px\">**링크**</span>](https://example.com/linked)셋째 줄";
            editor.Text="앞\n\n"+plainLine+"\n"+nestedLine+"\n뒤\n";editor.CaretIndex=0;Pump();
            MarkdownLivePreview.LineInfo plain=session.Info(editor.Document.GetLineByNumber(3));
            int boldOpen=plain.Text.IndexOf("**",StringComparison.Ordinal);
            int boldText=plain.Text.IndexOf("링크",StringComparison.Ordinal);
            bool plainPair=boldOpen>=0&&boldText==boldOpen+2&&HasHiddenRun(plain,boldOpen,2)&&HasHiddenRun(plain,boldText+2,2)
                &&HasStyled(plain,boldText,2,MarkdownLivePreview.Style.Bold);
            Check("link label parses its own emphasis",plainPair,
                "text=["+plain.Text+"], bold="+boldText+", hidden="+HiddenSummary(plain));
            DocumentLine target=editor.Document.GetLineByNumber(4);
            MarkdownLivePreview.LineInfo info=session.Info(target);
            int at=info.Text.IndexOf("링크",StringComparison.Ordinal);
            bool nestedPair=at>=0&&HasHiddenRun(info,at-2,2)&&HasHiddenRun(info,at+2,2)&&HasStyled(info,at,2,MarkdownLivePreview.Style.Bold);
            Check("nested link label parses the span and emphasis",nestedPair,
                "at="+at+", text=["+info.Text+"], hidden="+HiddenSummary(info));
            editor.Select(target.Offset+at,2);Pump();
            var cut=typeof(MarkdownLivePreview.Session).GetMethod("HiddenMarkerDelete",BindingFlags.Instance|BindingFlags.NonPublic);
            var ctor=typeof(ExecutedRoutedEventArgs).GetConstructor(BindingFlags.Instance|BindingFlags.NonPublic,null,
                new Type[]{typeof(ICommand),typeof(object)},null);
            if (cut==null||ctor==null) {Check("link label cut seam",false,"missing reflection seam");return;}
            var cutArgs=(ExecutedRoutedEventArgs)ctor.Invoke(new object[]{ApplicationCommands.Cut,null});
            // The rule sets Handled when it swallows the cut, and RoutedEventArgs refuses that while
            // RoutedEvent is null; these handlers are registered on PreviewExecuted.
            cutArgs.RoutedEvent=CommandManager.PreviewExecutedEvent;
            cut.Invoke(session,new object[]{editor.TextArea,cutArgs});
            int widened=editor.SelectionLength;
            editor.SelectedText="";Pump();
            string after=editor.Document.GetText(editor.Document.GetLineByNumber(4));
            Check("cut of link label text leaves no emptied pair",after=="- ##### [](https://example.com/linked)셋째 줄",
                "widened="+widened+", line=["+after+"]");
        } finally {
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
        }
    }
    // Inline links on a formatted line, as CommonMark/GFM renders them: a bare unmatched "["
    // before a link does not swallow it, a bracket pair inside the label stays visible, a plain
    // "[note]" next to a link is text, and a badge's image target may hold its own parentheses
    // (LinkLabelEnd skips it with ImageEnd, not the first ")"). The expected visible text is
    // the line the view draws for a caret elsewhere; one link must carry the outer URL.
    static void RunInlineLinkCases() {
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=CreateWindow(editor,900,700);
            session=Attach(editor,false);
            RunLinkCase(editor,session,"[a](u)","a","u",null);
            RunLinkCase(editor,session,"[![a](i.png)](u)","","u","i.png");
            RunLinkCase(editor,session,"[![a](i(1).png)](u)","","u","i(1).png");
            RunLinkCase(editor,session,"[![a](i.png \"x)y]z\")](u)","","u","i.png");
            RunLinkCase(editor,session,"[note] [b](u)","[note] b","u",null);
            RunLinkCase(editor,session,"[note] see [![a](i.png)](u)","[note] see ","u","i.png");
            RunLinkCase(editor,session,"[unclosed [b](u)","[unclosed b","u",null);
            RunLinkCase(editor,session,"[x [y] z](u)","x [y] z","u",null);
        } finally {
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
        }
    }
    static void RunLinkCase(SourceEditor editor,MarkdownLivePreview.Session session,string fixture,string expected,string url,string imageSource) {
        editor.Text="before\n\n"+fixture+"\n\nafter\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        MarkdownLivePreview.LineInfo info=session.Info(editor.Document.GetLineByNumber(3));
        int[] offsets;string[] visible=session.VisibleText(out offsets).Split('\n');
        string shown=visible.Length>2 ? visible[2] : "<missing>";
        bool linksOk=info!=null&&info.Links!=null&&info.Links.Count==1&&info.Links[0].Url==url;
        bool imageOk=true;
        if (imageSource!=null) {
            imageOk=false;
            if (info!=null) foreach (MarkdownLivePreview.Replacement rep in info.Replacements)
                if (rep.Kind==MarkdownLivePreview.ReplacementKind.Image&&rep.Source==imageSource) { imageOk=true;break; }
        }
        Check("inline link "+fixture,shown==expected&&linksOk&&imageOk,
            "shown=["+shown+"], expected=["+expected+"], links="+(info==null||info.Links==null?-1:info.Links.Count)+
            ", url="+(info!=null&&info.Links!=null&&info.Links.Count>0?info.Links[0].Url:"-")+
            ", image="+imageOk+", hidden="+(info==null?"-":HiddenSummary(info)));
    }
    // Two tables with no blank line between: GFM ends a table only at a blank or non-table line,
    // so the second "| --- |" row is a data row of the first table, not a header delimiter. The
    // row above it must keep Header=false (a header's next line is folded by StrutGenerator) and
    // its own Delimiter=false; both rows stay visible as ordinary cells.
    static void RunAdjacentTableRows() {
        SourceEditor editor=null;Window window=null;MarkdownLivePreview.Session session=null;
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=CreateWindow(editor,900,700);
            session=Attach(editor,false);
            editor.Text="before\n\n| a | b |\n| --- | --- |\n| c | d |\n| e | f |\n| --- | --- |\n| g | h |\n\nafter\n";editor.CaretIndex=0;
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            MarkdownLivePreview.LineInfo header=session.Info(editor.Document.GetLineByNumber(3));
            MarkdownLivePreview.LineInfo delimiter=session.Info(editor.Document.GetLineByNumber(4));
            MarkdownLivePreview.LineInfo row=session.Info(editor.Document.GetLineByNumber(6));
            MarkdownLivePreview.LineInfo dashRow=session.Info(editor.Document.GetLineByNumber(7));
            bool firstTable=header.Table&&header.Header&&!header.Delimiter&&
                delimiter.Table&&delimiter.Delimiter&&!delimiter.Header;
            bool secondRows=row.Table&&!row.Header&&!row.Delimiter&&
                dashRow.Table&&!dashRow.Header&&!dashRow.Delimiter&&dashRow.Cells!=null&&dashRow.Cells.Count==2;
            VisualLine dashVisual=session.View.GetOrConstructVisualLine(editor.Document.GetLineByNumber(7));
            bool unfolded=dashVisual!=null&&!dashVisual.IsDisposed&&dashVisual.FirstDocumentLine.LineNumber==7;
            int[] offsets;string[] visible=session.VisibleText(out offsets).Split('\n');
            bool shown=visible.Length>6&&visible[6].IndexOf("---",StringComparison.Ordinal)>=0;
            Check("adjacent tables keep the second dash row",firstTable&&secondRows&&unfolded&&shown,
                "first table="+firstTable+", rows="+secondRows+", unfolded="+unfolded+", shown="+shown+
                ", header(row above)="+(row==null?"-":row.Header.ToString())+", dash row cells="+(dashRow==null||dashRow.Cells==null?-1:dashRow.Cells.Count));
        } finally {
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
        }
    }
    static bool HasHiddenRun(MarkdownLivePreview.LineInfo info,int start,int length) {
        foreach (MarkdownLivePreview.Replacement rep in info.Replacements)
            if (rep.Kind==MarkdownLivePreview.ReplacementKind.Hidden && rep.Start==start && rep.Length==length) return true;
        return false;
    }
    static bool HasStyled(MarkdownLivePreview.LineInfo info,int start,int length,MarkdownLivePreview.Style style) {
        foreach (MarkdownLivePreview.StyledPart part in info.Styles)
            if (part.Start==start && part.Length==length && (part.Style&style)!=0) return true;
        return false;
    }
    static string HiddenSummary(MarkdownLivePreview.LineInfo info) {
        var text=new StringBuilder();
        foreach (MarkdownLivePreview.Replacement rep in info.Replacements)
            if (rep.Kind==MarkdownLivePreview.ReplacementKind.Hidden) text.Append(' ').Append(rep.Start).Append('+').Append(rep.Length);
        return text.ToString();
    }
    static MarkdownLivePreview.Session Attach(SourceEditor editor,bool hide) {
        return MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=hide});
    }
    static string RepeatText(string value,int count) {
        var text=new StringBuilder();
        for (int i=0;i<count;i++) text.Append(value);
        return text.ToString();
    }
    // Shared helpers -------------------------------------------------------------
    static Window CreateWindow(SourceEditor editor,double width,double height) {
        var window=new Window {Content=editor,Width=width,Height=height,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,Left=0,Top=0};
        window.Show();Pump();return window;
    }
    static MarkdownLivePreview.Session Attach(SourceEditor editor) {
        return MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
    }
    static string Number(double value) {return value.ToString("F2",CultureInfo.InvariantCulture);}
    static bool Drain(DispatcherPriority priority,int timeout) {
        bool reached=false;var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Send) {Interval=TimeSpan.FromMilliseconds(timeout)};
        EventHandler expired=delegate {timer.Stop();frame.Continue=false;};timer.Tick+=expired;timer.Start();
        DispatcherOperation marker=Dispatcher.CurrentDispatcher.BeginInvoke(priority,new Action(delegate {reached=true;frame.Continue=false;}));
        try {Dispatcher.PushFrame(frame);} finally {timer.Stop();timer.Tick-=expired;if (!reached) marker.Abort();}return reached;
    }
    static void Pump() {if (!Drain(DispatcherPriority.ApplicationIdle,2000)) throw new InvalidOperationException("Dispatcher did not become idle");}
    static void Wait(int milliseconds) {
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        EventHandler tick=delegate {timer.Stop();frame.Continue=false;};timer.Tick+=tick;timer.Start();
        try {Dispatcher.PushFrame(frame);} finally {timer.Stop();timer.Tick-=tick;}Pump();
    }
}
}
