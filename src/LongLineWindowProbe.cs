using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// Generated-data, offscreen research gate. The bare-editor cases configure only the
// source surface; the TXT host case opens a generated .txt in a MainWindow to prove
// attach/detach and find-next reveal in the real path. Timing includes frame + ContextIdle.
internal static class LongLineWindowProbe {
    internal const int LongChars = 200000;
    const int Warmup = 5;
    const int Samples = 30;
    internal const string Prefix = "short first\nshort second\nshort third\n";
    const string Suffix = "\nshort fifth\nshort sixth\nshort seventh";
    internal const string Token = "QZXJPROBEMARK";
    internal const int TokenAt = 150000;
    // Every exception the dispatcher caught during this run. The document-swap cases below
    // compare this around each step: a surface that throws only through a queued callback
    // (window close) still fails the step instead of surfacing as one unnamed run failure.
    internal static int DispatchErrors;

    internal static int Run(string outDir) {
        var report = new StringBuilder();
        report.AppendLine("P9 long-line window research spike; generated text only; not product integration.");
        report.AppendLine("Malgun Gothic 16, 1000x800 offscreen; 5 warmup + 30 timed single-character inputs.");
        report.AppendLine("Each sample includes the next render frame and ContextIdle; no physical IME/GPU/manual mouse claim.");
        report.AppendLine("Wrap on: tall spacer folds in the vertical direction; wrap off: inline fixed-width folds.");
        report.AppendLine("Wrap on folding forces TextView.LineHeight to NaN and restores the app height with struts; a wrapped");
        report.AppendLine("continuation row keeps its own content height (the live preview has the same limitation).");
        report.AppendLine("Limitations: a viewport taller than the real window can retain folded edge rows; existing generators may still scan a full logical line.");
        report.AppendLine("An identical CaretOffset assignment raises no caret event; that redundant-set reveal case is not covered.");
        string directory = null, passFile = null;
        int exit = 0, failures = 0;
        Window window = null;
        LongLineWindow.Session session = null;
        Dispatcher dispatcher = null;
        DispatcherUnhandledExceptionEventHandler unhandled = null;
        System.Threading.SynchronizationContext previousContext = null;
        Exception dispatchError = null;
        try {
            directory = Path.GetFullPath(outDir);
            Directory.CreateDirectory(directory);
            passFile = Path.Combine(directory, "longline-window-PASS.txt");
            if (File.Exists(passFile)) File.Delete(passFile);
            if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            dispatcher = Dispatcher.CurrentDispatcher;
            // The TXT host case drives the real async snapshot search (>= 32K chars),
            // whose continuations must return to this dispatcher.
            previousContext = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            DispatchErrors = 0;
            unhandled = delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                DispatchErrors++;
                if (dispatchError == null) dispatchError = e.Exception;
                e.Handled = true;
            };
            dispatcher.UnhandledException += unhandled;

            string body = Generate(LongChars);
            string original = Prefix + body + Suffix;
            string expected = Prefix + body.Substring(0, LongChars / 2)
                + new string('가', Warmup + Samples) + body.Substring(LongChars / 2) + Suffix;
            var editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true };
            TextView view = editor.TextArea.TextView;
            view.LineHeight = double.NaN;
            editor.Text = original;
            int generatorsBefore = view.ElementGenerators.Count;
            session = (LongLineWindow.Session)LongLineWindow.Attach(editor);
            window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false,
                ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            window.Show();
            LongLineBench.Idle();
            Pump(editor);
            DocumentLine line = editor.Document.GetLineByNumber(4);
            SetCaret(editor, line.Offset + LongChars / 2);
            int initialStart = session.WindowStart(4);
            List<double> withWindow = Type(editor);
            int afterTypingStart = session.WindowStart(4);
            VisualLine visual = view.GetOrConstructVisualLine(line);
            int realChars = RealCharacters(visual), foldCount = Folds(visual);
            double windowP95 = LongLineBench.Percentile(withWindow, 0.95);
            report.AppendFormat(CultureInfo.InvariantCulture,
                "window typing: n={0}, p50={1:0.00} ms, p95={2:0.00} ms, max={3:0.00} ms, real_chars={4}, folds={5}, rows={6}\n",
                Samples, LongLineBench.Percentile(withWindow, 0.5), windowP95, withWindow[Samples - 1], realChars, foldCount, visual.TextLines.Count);
            Check(report, ref failures, "lazy", initialStart == afterTypingStart,
                "window_start_before=" + initialStart + ", after_35_inputs=" + afterTypingStart);

            Check(report, ref failures, "b text", EqualPieces(editor.Document, expected)
                && editor.Document.LineCount == 7 && line.Length == LongChars + Warmup + Samples,
                "actual_chars=" + editor.Document.TextLength + ", expected_chars=" + expected.Length
                + ", lines=" + editor.Document.LineCount + ", long_line_chars=" + line.Length + ", compare_chunk=2048");

            int end = line.EndOffset;
            SetCaret(editor, end); // Allowed equivalent of Ctrl+End within the long line.
            Point endPoint;
            bool endReal = RealCaret(editor, out endPoint) && editor.CaretOffset == end;
            int endStart = session.WindowStart(4);
            SetCaret(editor, line.Offset); // Allowed equivalent of Ctrl+Home within the long line.
            Point startPoint;
            bool startReal = RealCaret(editor, out startPoint) && editor.CaretOffset == line.Offset;
            Check(report, ref failures, "c end/start", endReal && startReal,
                string.Format(CultureInfo.InvariantCulture,
                    "end_offset={0}, end_window_start={1}, end_xy=({2:0.0},{3:0.0}), start_offset={4}, start_window_start={5}, start_xy=({6:0.0},{7:0.0})",
                    end, endStart, endPoint.X, endPoint.Y, line.Offset, session.WindowStart(4), startPoint.X, startPoint.Y));
            CheckNavigation(editor, session, report, ref failures);

            SetCaret(editor, line.Offset + 1024);
            visual = view.GetOrConstructVisualLine(line);
            int beforeScroll = session.WindowStart(4);
            double height = Math.Max(1, editor.ViewportHeight);
            double middleY = visual.VisualTop + visual.Height / 2;
            int anchor = EstimateOffset(visual, middleY);
            editor.ScrollToVerticalOffset(Math.Max(0, middleY - height / 2));
            Pump(editor);
            int afterScroll = session.WindowStart(4);
            visual = view.GetOrConstructVisualLine(line);
            double centerY = view.VerticalOffset + height / 2;
            bool centerReal = FoldOnRow(visual, visual.GetTextLineByVisualYPosition(centerY)) == null;
            Point anchorPoint = view.GetVisualPosition(new TextViewPosition(editor.Document.GetLocation(line.Offset + anchor)), VisualYPosition.LineMiddle);
            double drift = Math.Abs(anchorPoint.Y - view.VerticalOffset - height / 2);
            Check(report, ref failures, "d scroll", beforeScroll != afterScroll && centerReal && Finite(anchorPoint)
                && drift <= view.DefaultLineHeight * 2,
                string.Format(CultureInfo.InvariantCulture,
                    "window_start_before={0}, after={1}, estimated_anchor={2}, center_real={3}, anchor_drift={4:0.00} px, vertical_offset={5:0.0}",
                    beforeScroll, afterScroll, anchor, centerReal, drift, view.VerticalOffset));

            editor.Select(line.Offset, line.Length);
            // Keep the whole selection but expose its middle, so the render
            // really includes a prefix AND a suffix fold, not only the prefix.
            editor.CaretOffset = line.Offset + line.Length / 2;
            editor.TextArea.Caret.BringCaretToView();
            Pump(editor);
            int selected = editor.SelectedText.Length;
            int selectionFolds = Folds(view.GetOrConstructVisualLine(line));
            Check(report, ref failures, "e selection", selected == line.Length && selectionFolds == 2,
                "selected_chars=" + selected + ", line_chars=" + line.Length + ", folds=" + selectionFolds);
            editor.Select(editor.CaretOffset, 0);
            session.Dispose();
            session.Dispose(); // Idempotence; queued layout work must not reattach anything.
            Pump(editor);
            visual = view.GetOrConstructVisualLine(line);
            Check(report, ref failures, "f detach", !session.IsAttached && view.ElementGenerators.Count == generatorsBefore
                && Folds(visual) == 0 && RealCharacters(visual) == line.Length && double.IsNaN(view.LineHeight),
                "generators_before=" + generatorsBefore + ", after=" + view.ElementGenerators.Count
                + ", folds=" + Folds(visual) + ", real_chars=" + RealCharacters(visual) + ", line_chars=" + line.Length);
            session = null;

            // Identical editor/font/viewport/data/actions, only the window is absent.
            editor.Text = original;
            SetCaret(editor, editor.Document.GetLineByNumber(4).Offset + LongChars / 2);
            List<double> withoutWindow = Type(editor);
            double baselineP95 = LongLineBench.Percentile(withoutWindow, 0.95);
            report.AppendFormat(CultureInfo.InvariantCulture,
                "without window: n={0}, p50={1:0.00} ms, p95={2:0.00} ms, max={3:0.00} ms, p95_ratio={4:0.00}x\n",
                Samples, LongLineBench.Percentile(withoutWindow, 0.5), baselineP95, withoutWindow[Samples - 1], baselineP95 / Math.Max(0.001, windowP95));
            Check(report, ref failures, "a typing", windowP95 <= 40 && realChars <= LongLineWindow.WindowChars + 2
                && foldCount == 2 && EqualPieces(editor.Document, expected),
                string.Format(CultureInfo.InvariantCulture,
                    "window_p95={0:0.00} ms <= 40, without_p95={1:0.00} ms, samples={2}, warmup={3}, bounded_real_chars={4}",
                    windowP95, baselineP95, Samples, Warmup, realChars));
            CheckHorizontal(report, ref failures);
            CheckHorizontalEditKeepsRange(report, ref failures);
            CheckHorizontalTwoLines(report, ref failures);
            CheckHorizontalHugeLine(report, ref failures);
            CheckLivePreviewInteraction(directory, report, ref failures);
            MainWindow.VerifyTxtLongLineHost(directory, report, ref failures);
            MainWindow.VerifyFoldSearch(directory, report, ref failures);
            MainWindow.VerifyLongLineDocumentSwap(directory, report, ref failures);
            exit = failures == 0 ? 0 : 1;
        } catch (Exception ex) {
            exit = 2;
            report.AppendLine("FAIL exception " + ex.ToString());
        } finally {
            try { if (session != null) session.Dispose(); }
            catch (Exception ex) { exit = 2; report.AppendLine("FAIL dispose " + ex.ToString()); }
            try { if (window != null) { window.Close(); LongLineBench.Idle(); } }
            catch (Exception ex) { exit = 2; report.AppendLine("FAIL close " + ex.ToString()); }
            if (dispatcher != null && unhandled != null) dispatcher.UnhandledException -= unhandled;
            if (previousContext != null) System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
            if (dispatchError != null) { exit = 2; report.AppendLine("FAIL dispatcher exception " + dispatchError.ToString()); }
        }
        report.AppendLine("RESULT exit=" + exit + ", failed_checks=" + failures);
        try {
            if (directory == null) return 2;
            File.WriteAllText(Path.Combine(directory, "longline-window.txt"), report.ToString(), new UTF8Encoding(false));
            if (exit == 0) File.WriteAllText(passFile, "PASS long-line window research probe\r\n", new UTF8Encoding(false));
            else if (File.Exists(passFile)) File.Delete(passFile);
        } catch (Exception ex) {
            exit = 2;
            report.AppendLine("FAIL output " + ex.ToString());
            try {
                if (passFile != null && File.Exists(passFile)) File.Delete(passFile);
                if (directory != null) File.WriteAllText(Path.Combine(directory, "longline-window.txt"), report.ToString(), new UTF8Encoding(false));
            } catch { }
        }
        return exit;
    }

    internal static string Generate(int length) {
        string[] words = { "기억이", "바람은", "노트를", "새벽에", "골목의", "편지가", "창문을", "지도는", "문장이", "커피가",
            "quiet", "morning", "river", "station", "notebook", "draft", "window", "light", "street", "page" };
        var random = new Random(9009);
        var text = new StringBuilder(length + 32);
        while (text.Length < length) {
            if (text.Length > 0) text.Append(' ');
            text.Append(words[random.Next(words.Length)]);
        }
        text.Length = length;
        return text.ToString();
    }

    static void Pump(SourceEditor editor) {
        LongLineBench.Time(delegate { }, editor, DispatcherPriority.ContextIdle);
        LongLineBench.Idle();
        LongLineBench.Time(delegate { }, editor, DispatcherPriority.ContextIdle);
    }

    static void SetCaret(SourceEditor editor, int offset) {
        editor.Select(offset, 0);
        editor.TextArea.Caret.BringCaretToView();
        Pump(editor);
    }

    static List<double> Type(SourceEditor editor) {
        for (int i = 0; i < Warmup; i++)
            LongLineBench.Time(delegate { editor.TextArea.PerformTextInput("가"); }, editor, DispatcherPriority.ContextIdle);
        var samples = new List<double>();
        for (int i = 0; i < Samples; i++)
            samples.Add(LongLineBench.Time(delegate { editor.TextArea.PerformTextInput("가"); }, editor, DispatcherPriority.ContextIdle));
        samples.Sort();
        return samples;
    }

    internal static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds) {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMilliseconds) {
            LongLineBench.Idle();
            if (condition()) return true;
            System.Threading.Thread.Sleep(2);
        }
        return condition();
    }

    static bool CaretReal(SourceEditor editor) {
        TextView view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        DocumentLine line = editor.Document.GetLineByNumber(editor.TextArea.Caret.Line);
        VisualLine visual = view.GetOrConstructVisualLine(line);
        if (visual == null || visual.IsDisposed) return false;
        int relative = editor.CaretOffset - line.Offset;
        bool text = false;
        foreach (VisualLineElement element in visual.Elements) {
            VisualLineText piece = element as VisualLineText;
            if (piece != null && relative >= piece.RelativeTextOffset && relative <= piece.RelativeTextOffset + piece.DocumentLength) text = true;
        }
        if (!text) return false;
        Point point = visual.GetVisualPosition(visual.GetVisualColumn(relative), VisualYPosition.LineMiddle);
        double screenX = point.X - view.HorizontalOffset, screenY = point.Y - view.VerticalOffset;
        return screenX >= -1 && screenX <= editor.ViewportWidth + 1 && screenY >= -1 && screenY <= editor.ViewportHeight + 1;
    }

    static bool OffsetIsReal(SourceEditor editor, int offset) {
        if (offset < 0 || offset > editor.Document.TextLength) return false;
        TextView view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        DocumentLine line = editor.Document.GetLineByOffset(offset);
        VisualLine visual = view.GetOrConstructVisualLine(line);
        if (visual == null || visual.IsDisposed) return false;
        int relative = offset - line.Offset;
        foreach (VisualLineElement element in visual.Elements) {
            VisualLineText piece = element as VisualLineText;
            if (piece != null && relative >= piece.RelativeTextOffset && relative < piece.RelativeTextOffset + piece.DocumentLength) return true;
        }
        return false;
    }

    // Horizontal center of the viewport, on the long line's own row: with wrap off that line is one row, so the
    // viewport's vertical center can fall below the document and hit nothing.
    static int OffsetAtViewportCenter(SourceEditor editor, DocumentLine line) {
        TextView view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        VisualLine row = view.GetVisualLine(line.LineNumber);
        double y = row != null ? row.VisualTop + row.Height / 2
            : view.VerticalOffset + ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportHeight / 2;
        Point point = new Point(view.HorizontalOffset + ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportWidth / 2, y);
        TextViewPosition? position = view.GetPositionFloor(point);
        return position.HasValue ? editor.Document.GetOffset(position.Value.Location) : -1;
    }

    static int HorizontalFolds(VisualLine line) {
        int count = 0;
        foreach (VisualLineElement element in line.Elements)
            if (element is LongLineWindow.HFoldElement) count++;
        return count;
    }

    static int VerticalFolds(VisualLine line) {
        int count = 0;
        foreach (VisualLineElement element in line.Elements)
            if (element is LongLineWindow.FoldElement) count++;
        return count;
    }

    // -------------------------------------------------------------- wrap off
    // A 200K line on a surface with horizontal scrolling: the real window is the
    // caret plus the visible range, and every edit/selection reads the real document.
    static void CheckHorizontal(StringBuilder report, ref int failures) {
        string body = Generate(LongChars);
        string seeded = body.Substring(0, TokenAt) + Token + body.Substring(TokenAt + Token.Length);
        string oracle = Prefix + seeded + Suffix;
        var editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=false };
        TextView view = editor.TextArea.TextView;
        view.LineHeight=24; // the app's fixed 150% TXT paragraph height
        editor.HorizontalScrollBarVisibility=ScrollBarVisibility.Auto;
        editor.Text=oracle;
        int generatorsBefore=view.ElementGenerators.Count, transformsBefore=view.LineTransformers.Count;
        LongLineWindow.Session session=null;
        Window window=null;
        try {
            session=(LongLineWindow.Session)LongLineWindow.Attach(editor);
            window=new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false,
                ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            window.Show();
            LongLineBench.Idle();
            Pump(editor);
            DocumentLine line=editor.Document.GetLineByNumber(4);
            int mid=line.Offset+100000;
            SetCaret(editor,mid);
            int startBefore=session.WindowStart(4);
            List<double> typed=Type(editor);
            int afterTypingStart=session.WindowStart(4);
            oracle=oracle.Substring(0,mid)+new string('가',Warmup+Samples)+oracle.Substring(mid);
            VisualLine visual=view.GetOrConstructVisualLine(line);
            int realChars=RealCharacters(visual), folds=HorizontalFolds(visual);
            double p95=LongLineBench.Percentile(typed,0.95);
            report.AppendFormat(CultureInfo.InvariantCulture,
                "horizontal typing: n={0}, p50={1:0.00} ms, p95={2:0.00} ms, real_chars={3}, folds={4}, rows={5}\n",
                Samples,LongLineBench.Percentile(typed,0.5),p95,realChars,folds,visual.TextLines.Count);
            Check(report,ref failures,"h typing",p95<=40&&realChars<=LongLineWindow.WindowChars+2048&&folds==2
                &&startBefore==afterTypingStart&&view.LineHeight==24&&EqualPieces(editor.Document,oracle),
                string.Format(CultureInfo.InvariantCulture,
                    "p95={0:0.00} ms, real_chars={1}, folds={2}, window_start={3}/{4}, line_height={5:0.##}",
                    p95,realChars,folds,startBefore,afterTypingStart,view.LineHeight));

            EditingCommands.MoveToLineStart.Execute(null,editor.TextArea);
            Pump(editor);
            bool home=editor.CaretOffset==line.Offset&&CaretReal(editor);
            EditingCommands.MoveToLineEnd.Execute(null,editor.TextArea);
            Pump(editor);
            bool end=editor.CaretOffset==line.EndOffset&&CaretReal(editor);
            EditingCommands.MoveToDocumentStart.Execute(null,editor.TextArea);
            Pump(editor);
            bool docStart=editor.CaretOffset==0;
            EditingCommands.MoveToDocumentEnd.Execute(null,editor.TextArea);
            Pump(editor);
            bool docEnd=editor.CaretOffset==editor.Document.TextLength;
            Check(report,ref failures,"h line/document home-end",home&&end&&docStart&&docEnd,
                "home="+home+", line_end="+end+", ctrl_home="+docStart+", ctrl_end="+docEnd);

            SetCaret(editor,mid);
            VisualLine full=view.GetOrConstructVisualLine(line);
            double fullWidth=full.TextLines.Count==0?1000:full.TextLines[0].WidthIncludingTrailingWhitespace;
            editor.ScrollToHorizontalOffset(fullWidth*0.75);
            Pump(editor);
            int center=OffsetAtViewportCenter(editor,line);
            int centerRelative=center-line.Offset;
            bool realCenter=center>=line.Offset&&center<=line.EndOffset&&OffsetIsReal(editor,center);
            Check(report,ref failures,"h scroll reveal",centerRelative>140000&&centerRelative<160000&&realCenter
                &&session.WindowStart(4)>100000,
                "center_relative="+centerRelative+", window_start="+session.WindowStart(4)+", center_real="+realCenter);

            int at=Math.Max(line.Offset+1,Math.Min(line.EndOffset-1,center));
            // The 150K find token sits near the scrolled-to center; typing inside it would break the find check below.
            int tokenAt=oracle.IndexOf(Token,Prefix.Length);
            if(tokenAt>=0&&at>tokenAt&&at<tokenAt+Token.Length)at=tokenAt;
            SetCaret(editor,at);
            for(int i=0;i<5;i++)editor.TextArea.PerformTextInput("가");
            Pump(editor);
            oracle=oracle.Substring(0,at)+"가가가가가"+oracle.Substring(at);
            Check(report,ref failures,"h scroll typing",CaretReal(editor)&&EqualPieces(editor.Document,oracle)
                &&RealCharacters(view.GetOrConstructVisualLine(line))<=LongLineWindow.WindowChars+2048,
                "caret="+editor.CaretOffset+", length="+editor.Document.TextLength+"/"+oracle.Length);

            int from=line.Offset+120000;
            SetCaret(editor,from);
            for(int i=0;i<1000;i++)EditingCommands.SelectRightByCharacter.Execute(null,editor.TextArea);
            Pump(editor);
            bool selection=editor.SelectionStart==from&&editor.SelectionLength==1000
                &&String.CompareOrdinal(editor.SelectedText,0,oracle,from,1000)==0;
            Check(report,ref failures,"h selection 1000",selection,
                "start="+editor.SelectionStart+"/"+from+", length="+editor.SelectionLength
                +", prefix_matches="+(String.CompareOrdinal(editor.SelectedText,0,oracle,from,Math.Min(1000,editor.SelectedText.Length))==0));

            int tokenIndex=oracle.IndexOf(Token,Prefix.Length);
            SetCaret(editor,line.Offset);
            // Search.cs SelectMatch: select the match, then scroll to its logical line.
            editor.Select(tokenIndex,Token.Length);
            editor.ScrollToLine(editor.GetLineIndexFromCharacterIndex(tokenIndex));
            Pump(editor);
            bool found=editor.SelectionStart==tokenIndex&&editor.SelectionLength==Token.Length
                &&String.Equals(editor.SelectedText,Token,StringComparison.Ordinal);
            Check(report,ref failures,"h find 150k",found&&CaretReal(editor),
                "selection="+editor.SelectionStart+"+"+editor.SelectionLength+"/"+tokenIndex
                +", selected="+editor.SelectedText+", window_start="+session.WindowStart(4)+", caret_real="+CaretReal(editor));

            // Wrap on needs the tall spacer; the strut restores the app line height.
            editor.WordWrap=true;
            Pump(editor);
            Pump(editor);
            visual=view.GetOrConstructVisualLine(line);
            bool flatOn=double.IsNaN(view.LineHeight)&&VerticalFolds(visual)>0;
            int toggleAt=Math.Max(line.Offset+1,Math.Min(line.EndOffset-2,line.Offset+line.Length/2));
            SetCaret(editor,toggleAt);
            editor.TextArea.PerformTextInput("나");
            Pump(editor);
            oracle=oracle.Substring(0,toggleAt)+"나"+oracle.Substring(toggleAt);
            Check(report,ref failures,"h wrap on",flatOn&&VerticalFolds(view.GetOrConstructVisualLine(line))>0&&EqualPieces(editor.Document,oracle),
                "line_height_nan="+double.IsNaN(view.LineHeight)+", vertical_folds="+VerticalFolds(view.GetOrConstructVisualLine(line)));

            editor.WordWrap=false;
            Pump(editor);
            Pump(editor);
            bool flatOff=view.LineHeight==24;
            Check(report,ref failures,"h wrap off restore",flatOff&&HorizontalFolds(view.GetOrConstructVisualLine(line))>0,
                "line_height="+view.LineHeight.ToString(CultureInfo.InvariantCulture)+", horizontal_folds="+HorizontalFolds(view.GetOrConstructVisualLine(line)));

            session.Dispose();
            Pump(editor);
            visual=view.GetOrConstructVisualLine(line);
            Check(report,ref failures,"h detach",!session.IsAttached&&view.ElementGenerators.Count==generatorsBefore
                &&view.LineTransformers.Count==transformsBefore&&view.LineHeight==24
                &&HorizontalFolds(visual)==0&&RealCharacters(visual)==line.Length,
                "generators="+view.ElementGenerators.Count+"/"+generatorsBefore+", transforms="+view.LineTransformers.Count+"/"+transformsBefore
                +", line_height="+view.LineHeight.ToString(CultureInfo.InvariantCulture)+", real_chars="+RealCharacters(visual)+", line_chars="+line.Length);
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception horizontal " + ex.ToString());
        } finally {
            try { if (session != null) session.Dispose(); } catch (Exception) { }
            try { if (window != null) { window.Close(); LongLineBench.Idle(); } } catch (Exception) { }
        }
    }

    // --------------------------------------------------- wrap off, widened window
    // The horizontal window is as wide as the caret plus the visible range; a search
    // reveal or a scroll can widen it past WindowChars. An edit used to route through the
    // wrap-mode setter, which snaps End back to Start + WindowChars: the visible text and
    // the highlight fell into the fold on every keystroke. Both edges must move by the
    // edit's delta instead.
    static void CheckHorizontalEditKeepsRange(StringBuilder report, ref int failures) {
        string body = Generate(LongChars);
        var editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=false };
        TextView view = editor.TextArea.TextView;
        view.LineHeight = 24;
        LongLineWindow.Session session = null;
        Window window = null;
        try {
            session = (LongLineWindow.Session)LongLineWindow.Attach(editor);
            window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false,
                ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            window.Show();
            LongLineBench.Idle();
            editor.Text = Prefix + body + "\nshort last";
            Pump(editor);
            DocumentLine line = editor.Document.GetLineByNumber(4);
            // A long search match makes the reveal widen the window past WindowChars:
            // RevealHorizontalRange keeps WindowChars/2 on each side of the match.
            int matchAt = line.Offset + 100000;
            int matchLength = 40000;
            SetCaret(editor, matchAt + 1000);
            LongLineWindow.Reveal(editor, matchAt, matchLength);
            Pump(editor);
            int wideStart = session.WindowStart(4), wideEnd = session.WindowEnd(4);
            int wideWidth = wideEnd - wideStart;
            bool widened = wideWidth > LongLineWindow.WindowChars
                && matchAt >= line.Offset + wideStart && matchAt + matchLength <= line.Offset + wideEnd;
            // The far end of the widened range must be real text: with the wrap-mode setter
            // back in this path it folds to Start + WindowChars, losing the search highlight.
            int farEnd = matchAt + matchLength - 1000;
            bool farRealBefore = OffsetIsReal(editor, farEnd);
            editor.Select(matchAt, 0);
            Pump(editor);
            editor.TextArea.PerformTextInput("가");
            Pump(editor);
            int afterStart = session.WindowStart(4), afterEnd = session.WindowEnd(4);
            bool kept = afterStart == wideStart && afterEnd == wideEnd + 1;
            bool farRealAfter = OffsetIsReal(editor, farEnd + 1);
            bool caretReal = OffsetIsReal(editor, matchAt + 1);
            Check(report, ref failures, "h widened range survives an edit",
                widened && farRealBefore && kept && farRealAfter && caretReal,
                "widened=" + widened + ", before=" + wideStart + ".." + wideEnd + " (" + wideWidth
                + " chars), after=" + afterStart + ".." + afterEnd + " (expected .." + (wideEnd + 1) + ")"
                + ", kept=" + kept + ", far_end_real=" + farRealBefore + "/" + farRealAfter + ", caret_real=" + caretReal);
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception horizontal edit range " + ex);
        } finally {
            try { if (session != null) session.Dispose(); } catch (Exception) { }
            try { if (window != null) { window.Close(); LongLineBench.Idle(); } } catch (Exception) { }
        }
    }

    // Two long lines on screen at once, wrap off: both share the one HorizontalOffset, so a
    // rebuild for one line can push the other out of its own real window and the next pass
    // rebuilds again. Coverage for both lines must settle, and both must show real text at
    // the viewport.
    static void CheckHorizontalTwoLines(StringBuilder report, ref int failures) {
        string body = Generate(40000);
        string other = Generate(20000);
        var editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=false };
        TextView view = editor.TextArea.TextView;
        view.LineHeight = 24;
        LongLineWindow.Session session = null;
        Window window = null;
        try {
            session = (LongLineWindow.Session)LongLineWindow.Attach(editor);
            window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false,
                ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            window.Show();
            LongLineBench.Idle();
            // Lines 1-3 short, 4 is 40K, 5 is a short gap, 6 is 20K, then short lines:
            // with 24 px rows both long lines fit one 800 px viewport.
            editor.Text = Prefix + body + "\nshort gap\n" + other + "\nshort last";
            Pump(editor);
            DocumentLine first = editor.Document.GetLineByNumber(4), second = editor.Document.GetLineByNumber(6);
            int at = first.Offset + 1000;
            SetCaret(editor, at);
            Pump(editor);
            // Scroll far right so both windows have to move away from their initial placement.
            VisualLine full = view.GetOrConstructVisualLine(first);
            double width = full.TextLines.Count == 0 ? 1000 : full.TextLines[0].WidthIncludingTrailingWhitespace;
            int before = session.HorizontalRebuilds;
            for (int pass = 0; pass < 3; pass++) {
                editor.ScrollToHorizontalOffset(width * (0.45 + 0.15 * pass));
                Pump(editor);
            }
            int settled = session.HorizontalRebuilds;
            int rebuilds = settled - before;
            // A loop keeps rebuilding on every queued pass. Five more clean passes must add
            // none at all; the scroll steps above are the only source of work.
            for (int pass = 0; pass < 5; pass++) Pump(editor);
            int afterSettle = session.HorizontalRebuilds - settled;
            bool visible = view.GetVisualLine(first.LineNumber) != null && view.GetVisualLine(second.LineNumber) != null;
            int firstStart = session.WindowStart(4), firstEnd = session.WindowEnd(4);
            int secondStart = session.WindowStart(6), secondEnd = session.WindowEnd(6);
            bool bothWindows = firstStart >= 0 && firstEnd > firstStart && secondStart >= 0 && secondEnd > secondStart;
            // Both long lines must show real text where the viewport actually is, not a fold.
            bool firstShown = LineShowsRealTextAtViewport(editor, first);
            bool secondShown = LineShowsRealTextAtViewport(editor, second);
            Check(report, ref failures, "h two long lines settle",
                visible && bothWindows && firstShown && secondShown && afterSettle == 0 && rebuilds <= 24,
                "rebuilds=" + rebuilds + "/24, after_settle=" + afterSettle + ", visible=" + visible
                + ", windows=" + firstStart + ".." + firstEnd + " / " + secondStart + ".." + secondEnd
                + ", first_shows_real=" + firstShown + ", second_shows_real=" + secondShown);
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception horizontal two lines " + ex);
        } finally {
            try { if (session != null) session.Dispose(); } catch (Exception) { }
            try { if (window != null) { window.Close(); LongLineBench.Idle(); } } catch (Exception) { }
        }
    }

    // ------------------------------------------------------------ very long line
    // Wrap off, one multi-million-character line: the suffix fold's estimated width is
    // length * CharWidth. WPF's text layout has a hard ceiling on a line's width, so an
    // uncapped estimate can make layout throw or the scroll extent nonsensical. The cap
    // must keep layout working and keep caret/scroll mapping consistent.
    const int HugeChars = 3000000;

    static void CheckHorizontalHugeLine(StringBuilder report, ref int failures) {
        string body = Generate(HugeChars);
        var editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=false };
        TextView view = editor.TextArea.TextView;
        view.LineHeight = 24;
        LongLineWindow.Session session = null;
        Window window = null;
        try {
            session = (LongLineWindow.Session)LongLineWindow.Attach(editor);
            window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false,
                ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            window.Show();
            LongLineBench.Idle();
            editor.Text = "short first\n" + body + "\nshort last";
            Pump(editor);
            DocumentLine line = editor.Document.GetLineByNumber(2);
            SetCaret(editor, line.Offset + HugeChars / 2);
            Pump(editor);
            VisualLine visual = view.GetOrConstructVisualLine(line);
            double foldWidth = 0;
            int folds = 0;
            foreach (VisualLineElement element in visual.Elements) {
                LongLineWindow.HFoldElement fold = element as LongLineWindow.HFoldElement;
                if (fold == null) continue;
                folds++;
                foldWidth = Math.Max(foldWidth, fold.EstimatedWidth);
            }
            var scroll = (System.Windows.Controls.Primitives.IScrollInfo)view;
            double extent = scroll.ExtentWidth;
            bool finite = !double.IsNaN(extent) && !double.IsInfinity(extent) && extent > 0;
            bool caretReal = OffsetIsReal(editor, editor.CaretOffset) && CaretReal(editor);
            // Scroll far right: the window must move and the caret mapping must stay sane.
            int before = session.WindowStart(2);
            editor.ScrollToHorizontalOffset(extent * 0.9);
            Pump(editor);
            int center = OffsetAtViewportCenter(editor, line);
            int centerRelative = center - line.Offset;
            bool moved = session.WindowStart(2) != before;
            bool inLine = centerRelative >= 0 && centerRelative <= line.Length;
            // WPF refuses a line wider than ~3.58M px (int.MaxValue/600 twips). Both folds
            // share that budget, so each must stay at the capped width.
            bool widthOk = foldWidth <= LongLineWindow.MaxFoldWidth + 1;
            Check(report, ref failures, "h 3M line layout",
                folds >= 1 && finite && caretReal && inLine && widthOk && folds <= 2,
                "folds=" + folds + ", fold_width=" + foldWidth.ToString("0", CultureInfo.InvariantCulture)
                + ", extent=" + extent.ToString("0", CultureInfo.InvariantCulture) + ", finite=" + finite
                + ", caret_real=" + caretReal + ", scrolled_window_moved=" + moved
                + ", center_relative=" + centerRelative + "/" + line.Length);
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception horizontal huge line " + ex);
        } finally {
            try { if (session != null) session.Dispose(); } catch (Exception) { }
            try { if (window != null) { window.Close(); LongLineBench.Idle(); } } catch (Exception) { }
        }
    }

    static bool LineShowsRealTextAtViewport(SourceEditor editor, DocumentLine line) {
        TextView view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        VisualLine visual = view.GetOrConstructVisualLine(line);
        if (visual == null || visual.IsDisposed || visual.TextLines.Count == 0) return false;
        var scroll = (System.Windows.Controls.Primitives.IScrollInfo)view;
        // Where the viewport's middle column falls on this line's single row.
        double x = scroll.HorizontalOffset + scroll.ViewportWidth / 2;
        int column = ColumnAtX(visual, x);
        int relative = visual.GetRelativeOffset(column);
        return relative >= 0 && relative < line.Length && OffsetIsReal(editor, line.Offset + relative);
    }

    static int ColumnAtX(VisualLine visual, double x) {
        if (visual.TextLines.Count == 0) return 0;
        TextLine row = visual.TextLines[0];
        double width = row.WidthIncludingTrailingWhitespace;
        if (double.IsNaN(x) || double.IsInfinity(x) || x < 0) x = 0;
        if (x > width) x = width;
        CharacterHit hit = row.GetCharacterHitFromDistance(x);
        return Math.Max(0, Math.Min(visual.VisualLength, hit.FirstCharacterIndex));
    }

    // Live preview on the same editor: the host attaches the preview first on a
    // fresh open and after a TXT session on a mode switch, then hands the height
    // to the preview (PreviewActivated/BringToFront). A long line next to or inside
    // a table or quote must not get a second strut, a wrong row height or an
    // exception, and the wrap-on TXT surface must keep the app row height on short
    // lines through the long-line strut.
    static void CheckLivePreviewInteraction(string directory, StringBuilder report, ref int failures) {
        string body = Generate(LongChars);
        // 1-3 short text, 4-6 a real table, 7 a 20K row inside that table (over the
        // preview's 16K parse limit), 8 a short row after it, 9 the 200K paragraph, 10
        // a short quote, 11 a 200K quoted line, then the trailing short lines.
        string tableLong = "| " + body.Substring(0, 20000) + " | z |";
        string fixture = Prefix + "| a | b |\n|---|---|\n| x | y |\n" + tableLong + "\n| s | t |\n"
            + body + "\n> quoted short\n> " + body + Suffix;
        var settings = new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, BaseDirectory=directory };
        SourceEditor editor = null;
        Window window = null;
        LongLineWindow.Session session = null;
        MarkdownLivePreview.Session preview = null;
        try {
            // TXT first (app height 24), then the live preview, like LiveMarkdownHost.
            editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true };
            TextView view = editor.TextArea.TextView;
            view.LineHeight = 24;
            editor.Text = fixture;
            window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false,
                ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            window.Show();
            LongLineBench.Idle();
            session = (LongLineWindow.Session)LongLineWindow.Attach(editor);
            Pump(editor);
            preview = MarkdownLivePreview.Attach(editor, settings);
            session.PreviewActivated();
            session.BringToFront();
            DocumentLine longLine = editor.Document.GetLineByNumber(9);
            SetCaret(editor, longLine.Offset + 100000);
            Pump(editor);
            VisualLine longVisual = view.GetOrConstructVisualLine(longLine);
            bool ownsHeight = double.IsNaN(view.LineHeight);
            int longFolds = Folds(longVisual), longStruts = Struts(longVisual);
            VisualLine shortVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            double shortHeight = shortVisual.Height;
            bool shortStrut = Struts(shortVisual) == 0 && PreviewStruts(shortVisual) >= 1;
            VisualLine tableRowVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(6));
            bool tableRow = HasTableRow(tableRowVisual);
            double tableHeight = tableRowVisual.Height;
            // The 20K row inside the table: over the parse limit it stays native text,
            // and the long-line layer must fold it like any other long line (only the
            // side away from the window's own anchor) without a long-line strut, a
            // table row projection or a runaway height.
            VisualLine longRowVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(7));
            bool longRowFolded = Folds(longRowVisual) >= 1 && Struts(longRowVisual) == 0
                && !HasTableRow(longRowVisual) && !double.IsNaN(longRowVisual.Height) && longRowVisual.Height > 0;
            VisualLine afterRowVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(8));
            bool afterRow = HasTableRow(afterRowVisual) && afterRowVisual.Height < 24 * 3;
            // A long quoted line keeps exact text but no quote styling; it must still
            // lay out with a bounded fold and a finite height.
            DocumentLine quoted = editor.Document.GetLineByNumber(11);
            SetCaret(editor, quoted.Offset + 50);
            VisualLine quotedVisual = view.GetOrConstructVisualLine(quoted);
            Point caretPoint;
            bool quotedReal = RealCaret(editor, out caretPoint);
            VisualLine shortQuoteVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(10));
            report.AppendFormat(CultureInfo.InvariantCulture,
                "live long: line_height={0}, folds={1}, struts={2}, short_height={3:0.0}, table_height={4:0.0}, long_row_height={5:0.0}, quote_height={6:0.0}, quote_folds={7}\n",
                view.LineHeight.ToString(CultureInfo.InvariantCulture), longFolds, longStruts, shortHeight, tableHeight,
                longRowVisual.Height, quotedVisual.Height, Folds(quotedVisual));
            Check(report, ref failures, "i live owns height", ownsHeight && longFolds == 2 && longStruts == 0,
                "nan=" + ownsHeight + ", folds=" + longFolds + ", long_line_struts=" + longStruts);
            Check(report, ref failures, "i live short row height", shortStrut && Math.Abs(shortHeight - 24) <= 2,
                "long_line_struts=" + Struts(shortVisual) + ", preview_struts=" + PreviewStruts(shortVisual)
                + ", height=" + shortHeight.ToString(CultureInfo.InvariantCulture));
            Check(report, ref failures, "i live table row", tableRow && tableHeight > 0 && tableHeight < 24 * 2 + 4,
                "table_row_element=" + tableRow + ", height=" + tableHeight.ToString(CultureInfo.InvariantCulture));
            Check(report, ref failures, "i live long row in table", longRowFolded,
                "folds=" + Folds(longRowVisual) + ", long_line_struts=" + Struts(longRowVisual)
                + ", table_row_element=" + HasTableRow(longRowVisual) + ", height=" + longRowVisual.Height.ToString(CultureInfo.InvariantCulture));
            Check(report, ref failures, "i live row after long row", afterRow,
                "table_row_element=" + HasTableRow(afterRowVisual) + ", height=" + afterRowVisual.Height.ToString(CultureInfo.InvariantCulture));
            Check(report, ref failures, "i live long quote", quotedReal && Finite(caretPoint) && Folds(quotedVisual) >= 1
                && quotedVisual.Height > 0 && !double.IsNaN(quotedVisual.Height) && Math.Abs(shortQuoteVisual.Height - 24) <= 2,
                "caret_real=" + quotedReal + ", folds=" + Folds(quotedVisual) + ", height=" + quotedVisual.Height.ToString(CultureInfo.InvariantCulture)
                + ", short_quote_height=" + shortQuoteVisual.Height.ToString(CultureInfo.InvariantCulture));
            // MoveDownByLine out of the table row above into the folded 20K row: the
            // long-line navigation must land on real text with a finite screen row,
            // while the preview's own table projection stays out of that line.
            SetCaret(editor, editor.Document.GetLineByNumber(6).Offset + 1);
            EditingCommands.MoveDownByLine.Execute(null, editor.TextArea);
            Pump(editor);
            Point movedPoint;
            DocumentLine landed = editor.Document.GetLineByOffset(editor.CaretOffset);
            bool movedReal = RealCaret(editor, out movedPoint) && Finite(movedPoint)
                && landed.LineNumber >= 7 && landed.LineNumber <= 8;
            Check(report, ref failures, "i live move into folded table row", movedReal,
                "line=" + landed.LineNumber + ", caret=" + editor.CaretOffset
                + ", real=" + RealCaret(editor, out movedPoint));
            // Typing inside the folded long line rebuilds the window under the preview's
            // generators; no exception, exact text, folds bounded, height still the preview's.
            int typedAt = longLine.Offset + 100000;
            int expectedChars = longLine.Length + 1;
            if (editor.CaretOffset != typedAt) SetCaret(editor, typedAt);
            editor.TextArea.PerformTextInput("가");
            Pump(editor);
            DocumentLine typedLine = editor.Document.GetLineByNumber(9);
            string typedText = editor.Document.GetText(typedLine);
            Check(report, ref failures, "i live typing in folded line",
                typedText.Length == expectedChars && typedText.Substring(100000, 1) == "가"
                && Folds(view.GetOrConstructVisualLine(typedLine)) == 2 && double.IsNaN(view.LineHeight),
                "line_chars=" + typedText.Length + "/" + expectedChars
                + ", folds=" + Folds(view.GetOrConstructVisualLine(typedLine)));
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception live preview " + ex);
        } finally {
            try { if (preview != null) preview.Dispose(); } catch (Exception) { }
            try { if (session != null) session.Dispose(); } catch (Exception) { }
            try { if (window != null) { window.Close(); LongLineBench.Idle(); } } catch (Exception) { }
        }
        // The other order: the preview already owns height when the long-line session
        // attaches (a fresh Markdown open attaches the preview first).
        editor = null; window = null; session = null; preview = null;
        try {
            editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true };
            TextView view = editor.TextArea.TextView;
            view.LineHeight = double.NaN;
            editor.Text = fixture;
            window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false,
                ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            window.Show();
            LongLineBench.Idle();
            preview = MarkdownLivePreview.Attach(editor, settings);
            session = (LongLineWindow.Session)LongLineWindow.Attach(editor);
            DocumentLine longLine = editor.Document.GetLineByNumber(9);
            SetCaret(editor, longLine.Offset + 100000);
            Pump(editor);
            VisualLine longVisual = view.GetOrConstructVisualLine(longLine);
            VisualLine shortVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            Check(report, ref failures, "i preview first short row height",
                double.IsNaN(view.LineHeight) && Struts(shortVisual) == 0 && PreviewStruts(shortVisual) >= 1
                && Math.Abs(shortVisual.Height - 24) <= 2 && Folds(longVisual) >= 1 && Struts(longVisual) == 0,
                "nan=" + double.IsNaN(view.LineHeight) + ", short_height=" + shortVisual.Height.ToString(CultureInfo.InvariantCulture)
                + ", preview_struts=" + PreviewStruts(shortVisual) + ", long_folds=" + Folds(longVisual)
                + ", long_struts=" + Struts(longVisual));
            // Ownership is decided by the host, not assumed: a session that outlived a
            // TXT switch is told through PreviewActivated; a session created after the
            // preview attached reads the preview's NaN height and never queues flat mode.
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception preview first " + ex);
        } finally {
            try { if (preview != null) preview.Dispose(); } catch (Exception) { }
            try { if (session != null) session.Dispose(); } catch (Exception) { }
            try { if (window != null) { window.Close(); LongLineBench.Idle(); } } catch (Exception) { }
        }
        // Wrap-on TXT height path: the app row height must survive through the strut
        // on short lines while a long line forces TextView.LineHeight to NaN.
        editor = null; window = null; session = null; preview = null;
        try {
            editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true };
            TextView view = editor.TextArea.TextView;
            view.LineHeight = 24;
            editor.Text = Prefix + Generate(LongChars) + Suffix;
            window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false,
                ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            window.Show();
            LongLineBench.Idle();
            session = (LongLineWindow.Session)LongLineWindow.Attach(editor);
            DocumentLine longLine = editor.Document.GetLineByNumber(4);
            SetCaret(editor, longLine.Offset + 100000);
            Pump(editor);
            VisualLine shortVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            VisualLine longVisual = view.GetOrConstructVisualLine(longLine);
            Check(report, ref failures, "i txt wrap on row height",
                double.IsNaN(view.LineHeight) && Struts(shortVisual) >= 1 && Math.Abs(shortVisual.Height - 24) <= 2
                && Folds(longVisual) == 2 && longVisual.Height > 24,
                "nan=" + double.IsNaN(view.LineHeight) + ", struts=" + Struts(shortVisual)
                + ", short_height=" + shortVisual.Height.ToString(CultureInfo.InvariantCulture)
                + ", long_height=" + longVisual.Height.ToString(CultureInfo.InvariantCulture));
            editor.WordWrap = false;
            Pump(editor);
            Pump(editor);
            shortVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1));
            longVisual = view.GetOrConstructVisualLine(editor.Document.GetLineByNumber(4));
            Check(report, ref failures, "i txt wrap off row height",
                Math.Abs(view.LineHeight - 24) < 0.01 && Struts(shortVisual) == 0 && Math.Abs(shortVisual.Height - 24) <= 2
                && HorizontalFolds(longVisual) >= 1,
                "line_height=" + view.LineHeight.ToString(CultureInfo.InvariantCulture) + ", struts=" + Struts(shortVisual)
                + ", short_height=" + shortVisual.Height.ToString(CultureInfo.InvariantCulture)
                + ", horizontal_folds=" + HorizontalFolds(longVisual));
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception txt height " + ex);
        } finally {
            try { if (session != null) session.Dispose(); } catch (Exception) { }
            try { if (window != null) { window.Close(); LongLineBench.Idle(); } } catch (Exception) { }
        }
    }

    internal static int Struts(VisualLine line) {
        int count = 0;
        foreach (VisualLineElement element in line.Elements)
            if (element is LongLineWindow.StrutElement) count++;
        return count;
    }

    static int PreviewStruts(VisualLine line) {
        int count = 0;
        foreach (VisualLineElement element in line.Elements)
            if (element is MarkdownLivePreview.StrutElement) count++;
        return count;
    }

    static bool HasTableRow(VisualLine line) {
        foreach (VisualLineElement element in line.Elements)
            if (element is MarkdownLivePreview.TableRowElement) return true;
        return false;
    }

    // The TXT host case needs MainWindow's private fields; it lives in the
    // MainWindow partial at the bottom of this file (VerifyTxtLongLineHost).

    internal static void Check(StringBuilder report, ref int failures, string name, bool pass, string numbers) {
        if (!pass) failures++;
        report.Append(pass ? "PASS " : "FAIL ").Append(name).Append("; ").AppendLine(numbers);
    }

    internal static bool EqualPieces(TextDocument document, string expected) {
        if (document.TextLength != expected.Length) return false;
        for (int offset = 0; offset < expected.Length; offset += 2048) {
            int length = Math.Min(2048, expected.Length - offset);
            string actual = document.GetText(offset, length);
            if (string.CompareOrdinal(actual, 0, expected, offset, length) != 0) return false;
        }
        return true;
    }

    static int RealCharacters(VisualLine line) {
        int chars = 0;
        foreach (VisualLineElement element in line.Elements)
            if (element is VisualLineText) chars += element.DocumentLength;
        return chars;
    }

    static int Folds(VisualLine line) {
        int count = 0;
        foreach (VisualLineElement element in line.Elements)
            if (element is LongLineWindow.FoldElement) count++;
        return count;
    }

    static LongLineWindow.FoldElement FoldOnRow(VisualLine visual, TextLine row) {
        int first = visual.GetTextLineVisualStartColumn(row);
        foreach (VisualLineElement element in visual.Elements) {
            LongLineWindow.FoldElement fold = element as LongLineWindow.FoldElement;
            if (fold != null && fold.ObjectColumn >= first && fold.ObjectColumn < first + row.Length) return fold;
        }
        return null;
    }

    static int EstimateOffset(VisualLine visual, double y) {
        TextLine row = visual.GetTextLineByVisualYPosition(y);
        LongLineWindow.FoldElement fold = FoldOnRow(visual, row);
        if (fold == null) return visual.GetRelativeOffset(visual.GetVisualColumn(new Point(0, y), false));
        double top = visual.GetTextLineVisualYPosition(row, VisualYPosition.LineTop);
        double fraction = Math.Max(0, Math.Min(1, (y - top) / Math.Max(1, row.Height)));
        return fold.RelativeTextOffset + (int)Math.Round(fold.DocumentLength * fraction);
    }

    static bool Finite(Point point) {
        return !double.IsNaN(point.X) && !double.IsNaN(point.Y) && !double.IsInfinity(point.X) && !double.IsInfinity(point.Y);
    }

    static bool RealCaret(SourceEditor editor, out Point point) {
        TextView view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        DocumentLine line = editor.Document.GetLineByNumber(editor.TextArea.Caret.Line);
        VisualLine visual = view.GetOrConstructVisualLine(line);
        int relative = editor.CaretOffset - line.Offset;
        bool real = line.Length == 0;
        foreach (VisualLineElement element in visual.Elements) {
            if (element is VisualLineText && relative >= element.RelativeTextOffset
                && relative <= element.RelativeTextOffset + element.DocumentLength) real = true;
        }
        TextViewPosition position = editor.TextArea.Caret.Position;
        TextLine row = visual.GetTextLine(position.VisualColumn, position.IsAtEndOfLine);
        point = view.GetVisualPosition(position, VisualYPosition.LineMiddle);
        return real && FoldOnRow(visual, row) == null && Finite(point);
    }

    static void CheckNavigation(SourceEditor editor, LongLineWindow.Session session, StringBuilder report, ref int failures) {
        DocumentLine line = editor.Document.GetLineByNumber(4);
        SetCaret(editor, line.Offset + line.Length / 2);
        int before = editor.CaretOffset;
        EditingCommands.MoveRightByCharacter.Execute(null, editor.TextArea);
        Pump(editor);
        bool right = editor.CaretOffset == before + 1;
        EditingCommands.MoveLeftByCharacter.Execute(null, editor.TextArea);
        Pump(editor);
        bool left = editor.CaretOffset == before;
        bool allReal = true;
        int moves = 2, transitions = 0, previousStart = session.WindowStart(4);
        // Repeated pages force entry into folds rather than only exercising
        // positions that happen to fit inside the initial real-text window.
        for (int direction = 0; direction < 2; direction++) {
            for (int i = 0; i < 8; i++) {
                RoutedCommand command = direction == 0 ? EditingCommands.MoveDownByPage : EditingCommands.MoveUpByPage;
                command.Execute(null, editor.TextArea);
                Pump(editor);
                Point point;
                allReal = RealCaret(editor, out point) && allReal;
                int start = session.WindowStart(4);
                if (start != previousStart) transitions++;
                previousStart = start;
                moves++;
            }
        }
        RoutedCommand[] commands = { EditingCommands.MoveUpByLine, EditingCommands.MoveDownByLine,
            EditingCommands.MoveToLineEnd, EditingCommands.MoveToLineStart,
            EditingCommands.MoveRightByWord, EditingCommands.MoveLeftByWord,
            EditingCommands.SelectDownByPage, EditingCommands.SelectUpByPage,
            EditingCommands.SelectToLineEnd, EditingCommands.SelectToLineStart };
        foreach (RoutedCommand command in commands) {
            command.Execute(null, editor.TextArea);
            Pump(editor);
            Point point;
            allReal = RealCaret(editor, out point) && allReal;
            moves++;
        }
        EditingCommands.MoveToDocumentEnd.Execute(null, editor.TextArea);
        Pump(editor);
        Point endPoint;
        bool documentEnd = editor.CaretOffset == editor.Document.TextLength && RealCaret(editor, out endPoint);
        EditingCommands.MoveToDocumentStart.Execute(null, editor.TextArea);
        Pump(editor);
        Point startPoint;
        bool documentStart = editor.CaretOffset == 0 && RealCaret(editor, out startPoint);
        Check(report, ref failures, "c routed navigation", right && left && allReal && transitions > 0 && documentEnd && documentStart,
            "commands=" + (moves + 2) + ", page_window_transitions=" + transitions + ", all_real=" + allReal
            + ", right_plus_one=" + right + ", left_minus_one=" + left + ", ctrl_end=" + documentEnd + ", ctrl_home=" + documentStart);
        SetCaret(editor, line.Offset + line.Length / 2);
        int edgeBefore = session.WindowStart(4);
        SetCaret(editor, line.Offset + edgeBefore + LongLineWindow.Margin - 1);
        Point edgePoint;
        Check(report, ref failures, "c lazy edge", session.WindowStart(4) != edgeBefore && RealCaret(editor, out edgePoint),
            "margin=" + LongLineWindow.Margin + ", start_before=" + edgeBefore + ", after=" + session.WindowStart(4)
            + ", caret_relative=" + (editor.CaretOffset - line.Offset));
    }
}

public partial class MainWindow {
    internal static int VerifyLongLineWindow(string outDir) { return LongLineWindowProbe.Run(outDir); }

    // TXT host case of the same probe. It lives here because it reads the private
    // surface fields (source, longLines) and drives the real search panel.
    internal static void VerifyTxtLongLineHost(string directory, StringBuilder report, ref int failures) {
        string file=Path.Combine(directory,"longline-window-txt.txt");
        MainWindow window=null;
        // alwaysEditing defaults to false under Program.IsSelfTest (LoadPreferences returns early), so
        // a loaded file opens read-only and PerformTextInput inserts nothing. A user opens it with the
        // saved 11-01 "항상 수정 기능 활성화" on (Preferences.cs:104), so the probe sets the same static
        // before the load (as MainWindowBench does) and restores it afterwards.
        bool previousEditing=alwaysEditing;
        try {
            string body=LongLineWindowProbe.Generate(LongLineWindowProbe.LongChars);
            string seeded=body.Substring(0,LongLineWindowProbe.TokenAt)+LongLineWindowProbe.Token
                +body.Substring(LongLineWindowProbe.TokenAt+LongLineWindowProbe.Token.Length);
            string expected=LongLineWindowProbe.Prefix+seeded+"\nshort fifth\nshort sixth\nshort seventh";
            File.WriteAllText(file,expected,new UTF8Encoding(false));
            window=new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=1000, Height=800 };
            window.Show();
            window.Left=-20000;
            alwaysEditing=true;
            window.LoadFileCore(file);
            PumpLongLineWindow(window);
            LongLineWindow.Session attached=window.longLines as LongLineWindow.Session;
            bool isAttached=window.PlainText&&attached!=null&&attached.IsAttached&&Object.ReferenceEquals(attached.Editor,window.source);
            LongLineWindowProbe.Check(report,ref failures,"g txt attach",isAttached,
                "plain="+window.PlainText+", attached="+(attached==null?"-":attached.IsAttached.ToString())
                +", line_height="+window.source.TextArea.TextView.LineHeight.ToString(CultureInfo.InvariantCulture));

            // TXT height path: wrap on forces TextView.LineHeight to NaN for the tall
            // spacer, and the long-line strut must keep the app row height on a short
            // line; the long line's folded height must stay finite and far taller.
            double appLine=window.fontSize*window.linePercent/100;
            double shortRow=TxtRowHeight(window,1), longRow=TxtRowHeight(window,4), shortStruts=TxtStruts(window,1);
            // The line right after the tall spacer must keep the app height too: the
            // strut is the only thing that restores it once LineHeight is NaN.
            double nextRow=TxtRowHeight(window,5);
            bool shortHolds=Math.Abs(shortRow-appLine)<=2&&shortStruts>=1&&Math.Abs(nextRow-appLine)<=2;
            bool longGrows=!double.IsNaN(longRow)&&longRow>appLine*2;
            LongLineWindowProbe.Check(report,ref failures,"g txt wrap on height",shortHolds&&longGrows,
                "app_line="+appLine.ToString(CultureInfo.InvariantCulture)+", short_row="+shortRow.ToString(CultureInfo.InvariantCulture)
                +", short_struts="+shortStruts.ToString(CultureInfo.InvariantCulture)+", next_row="+nextRow.ToString(CultureInfo.InvariantCulture)
                +", long_row="+longRow.ToString(CultureInfo.InvariantCulture)
                +", line_height="+window.source.TextArea.TextView.LineHeight.ToString(CultureInfo.InvariantCulture));

            DocumentLine line=window.source.Document.GetLineByNumber(4);
            int at=line.Offset+1000;
            window.source.Select(at,0);
            PumpLongLineWindow(window);
            window.source.TextArea.PerformTextInput("가");
            window.source.TextArea.PerformTextInput("나");
            PumpLongLineWindow(window);
            expected=expected.Substring(0,at)+"가나"+expected.Substring(at);
            LongLineWindowProbe.Check(report,ref failures,"g txt typing",
                LongLineWindowProbe.EqualPieces(window.source.Document,expected),
                "length="+window.source.Document.TextLength+"/"+expected.Length);

            expected=FindLongLineNext(window,expected,"g txt find wrap on",report,ref failures);

            window.SetWrap(false);
            PumpLongLineWindow(window);
            attached=window.longLines as LongLineWindow.Session;
            bool stillAttached=attached!=null&&attached.IsAttached&&Object.ReferenceEquals(attached.Editor,window.source);
            line=window.source.Document.GetLineByNumber(4);
            int at2=line.Offset+1000;
            window.source.Select(at2,0);
            PumpLongLineWindow(window);
            window.source.TextArea.PerformTextInput("다");
            PumpLongLineWindow(window);
            expected=expected.Substring(0,at2)+"다"+expected.Substring(at2);
            LongLineWindowProbe.Check(report,ref failures,"g txt wrap off typing",
                stillAttached&&LongLineWindowProbe.EqualPieces(window.source.Document,expected),
                "attached="+stillAttached+", line_height="+window.source.TextArea.TextView.LineHeight.ToString(CultureInfo.InvariantCulture)
                +", text="+LongLineWindowProbe.EqualPieces(window.source.Document,expected));
            // Wrap off restores the app height and drops the strut; the short row keeps
            // the app height without it and the long line is one bounded row.
            double offLine=window.fontSize*window.linePercent/100;
            double offShort=TxtRowHeight(window,1), offLong=TxtRowHeight(window,4), offStruts=TxtStruts(window,1);
            LongLineWindowProbe.Check(report,ref failures,"g txt wrap off height",
                Math.Abs(window.source.TextArea.TextView.LineHeight-offLine)<0.01&&Math.Abs(offShort-offLine)<=2
                &&offStruts==0&&!double.IsNaN(offLong)&&offLong>0&&offLong<offLine*2,
                "line_height="+window.source.TextArea.TextView.LineHeight.ToString(CultureInfo.InvariantCulture)
                +", app_line="+offLine.ToString(CultureInfo.InvariantCulture)+", short_row="+offShort.ToString(CultureInfo.InvariantCulture)
                +", short_struts="+offStruts.ToString(CultureInfo.InvariantCulture)+", long_row="+offLong.ToString(CultureInfo.InvariantCulture));

            expected=FindLongLineNext(window,expected,"g txt find wrap off",report,ref failures);

            // The real TXT -> live preview switch: the long-line session survives,
            // PreviewActivated hands the height to the preview, and the long-line strut
            // must disappear from short rows while the preview's own strut remains.
            window.SetWrap(true);
            PumpLongLineWindow(window);
            
            PumpLongLineWindow(window);
            window.ToggleTextMode();
            PumpLongLineWindow(window);
            bool switchedLive=!window.PlainText&&window.LivePreviewAttached&&double.IsNaN(window.source.TextArea.TextView.LineHeight);
            double liveShort=TxtRowHeight(window,1), liveStruts=TxtStruts(window,1), liveLongRow=TxtRowHeight(window,4);
            bool liveOwns=switchedLive&&liveStruts==0&&Math.Abs(liveShort-window.fontSize*window.linePercent/100)<=2
                &&!double.IsNaN(liveLongRow)&&liveLongRow>window.fontSize*window.linePercent/100*2;
            LongLineWindowProbe.Check(report,ref failures,"g txt to live hands over height",liveOwns,
                "plain="+window.PlainText+", attached="+window.LivePreviewAttached
                +", line_height="+window.source.TextArea.TextView.LineHeight.ToString(CultureInfo.InvariantCulture)
                +", short_row="+liveShort.ToString(CultureInfo.InvariantCulture)+", short_struts="+liveStruts.ToString(CultureInfo.InvariantCulture)
                +", long_row="+liveLongRow.ToString(CultureInfo.InvariantCulture));

            // A whole-document replace disposes the preview (it restores the app height)
            // and a new preview is attached for the same still-attached long-line
            // session; the hand-over must repeat, not leave a long-line strut behind.
            window.SetText(window.source.Document.Text);
            PumpLongLineWindow(window);
            bool afterReplace=window.LivePreviewAttached&&double.IsNaN(window.source.TextArea.TextView.LineHeight);
            double replaceShort=TxtRowHeight(window,1), replaceStruts=TxtStruts(window,1);
            LongLineWindowProbe.Check(report,ref failures,"g live re-render keeps preview height",
                afterReplace&&replaceStruts==0&&Math.Abs(replaceShort-window.fontSize*window.linePercent/100)<=2,
                "attached="+window.LivePreviewAttached
                +", line_height="+window.source.TextArea.TextView.LineHeight.ToString(CultureInfo.InvariantCulture)
                +", short_row="+replaceShort.ToString(CultureInfo.InvariantCulture)+", short_struts="+replaceStruts.ToString(CultureInfo.InvariantCulture));

            // Basic -> Live view: SetLiveMode only changes the caret-line reveal, so neither the
            // preview nor the long-line session is re-created, the preview keeps owning the line
            // height, and no app-height strut leaks back to a short row.
            window.SetLiveMode(false);
            PumpLongLineWindow(window);
            bool keptBasic=window.LivePreviewAttached&&window.longLines!=null;
            window.SetLiveMode(true);
            PumpLongLineWindow(window);
            double againShort=TxtRowHeight(window,1), againStruts=TxtStruts(window,1);
            LongLineWindowProbe.Check(report,ref failures,"g basic and live view keeps height",
                keptBasic&&window.LivePreviewAttached&&double.IsNaN(window.source.TextArea.TextView.LineHeight)
                &&againStruts==0&&Math.Abs(againShort-window.fontSize*window.linePercent/100)<=2,
                "kept="+keptBasic+", attached="+window.LivePreviewAttached
                +", line_height="+window.source.TextArea.TextView.LineHeight.ToString(CultureInfo.InvariantCulture)
                +", short_row="+againShort.ToString(CultureInfo.InvariantCulture)+", short_struts="+againStruts.ToString(CultureInfo.InvariantCulture));
            window.SetText(window.source.Document.Text);
            PumpLongLineWindow(window);
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception txt host " + ex.ToString());
        } finally {
            alwaysEditing=previousEditing;
            if(window!=null){
                try { window.text=window.source.Document.Text; window.saved=window.text; } catch (Exception) { }
                try { window.Close(); } catch (Exception) { }
                LongLineBench.Idle();
            }
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
        }
    }

    static void PumpLongLineWindow(MainWindow window) {
        window.UpdateLayout();
        LongLineBench.Idle();
        window.UpdateLayout();
        LongLineBench.Idle();
    }

    // ---------------------------------------------------------------- fold search
    // Search inside a long line: a match in the folded prefix, past the folded span in the
    // middle, and one in the folded suffix, in both wrap modes, on the TXT surface and under
    // the live preview, at 20K and 200K characters. Every press must leave the match
    // selected, laid out as real text and on screen, and the highlight must cover the match
    // itself; a character that stays folded must draw no rectangle (a fold's visual column
    // is the fold's own, so the old adorner put the highlight at the fold edge).
    internal static void VerifyFoldSearch(string directory, StringBuilder report, ref int failures) {
        string[] labels = { "20k", "200k" };
        int[] lengths = { 20000, LongLineWindowProbe.LongChars };
        for (int size = 0; size < labels.Length; size++)
            for (int surface = 0; surface < 2; surface++)
                VerifyFoldSearchCase(directory, labels[size], lengths[size], surface == 1, report, ref failures);
    }

    // One token, three times: start / middle / end of the long line, so find-next and
    // find-previous each cross a fold. Repeating it is what makes previous meaningful;
    // a unique token would just re-select itself. FoldRepeatToken appears once, which is
    // what the repeated-press case needs: with a single match the next press lands on the
    // same offset, so the caret does not move and no caret event can reveal it.
    const string FoldToken = "ZQFOLDMARK";
    const string FoldRepeatToken = "ZQREPEATONLY";
    const int FoldTokenLength = 10;
    const int FoldRepeatTokenLength = 12;

    static string FoldSearchText(int size) {
        string body = LongLineWindowProbe.Generate(size);
        int first = 2000, middle = size / 2, last = size - 2000, repeat = size * 3 / 4;
        var text = new StringBuilder(size + 256);
        text.Append(LongLineWindowProbe.Prefix);
        text.Append(body, 0, first);
        text.Append(FoldToken);
        text.Append(body, first, middle - first);
        text.Append(FoldToken);
        text.Append(body, middle, repeat - middle);
        text.Append(FoldRepeatToken);
        text.Append(body, repeat, last - repeat);
        text.Append(FoldToken);
        text.Append(body, last, size - last);
        text.Append("\nshort fifth\nshort sixth\nshort seventh");
        return text.ToString();
    }

    static int[] FoldTokenStarts(string text) {
        var starts = new List<int>();
        int at = LongLineWindowProbe.Prefix.Length;
        while (at < text.Length) {
            int found = text.IndexOf(FoldToken, at, StringComparison.Ordinal);
            if (found < 0) break;
            starts.Add(found);
            at = found + FoldToken.Length;
        }
        return starts.ToArray();
    }

    static void VerifyFoldSearchCase(string directory, string label, int size, bool live, StringBuilder report, ref int failures) {
        string text = FoldSearchText(size);
        string file = Path.Combine(directory, "longline-window-search-" + label + (live ? "-live.md" : "-txt.txt"));
        bool previousDefault = ProbeDefaults.Live;
        MainWindow window = null;
        try {
            File.WriteAllText(file, text, new UTF8Encoding(false));
            ProbeDefaults.Live = live;
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=1000, Height=800 };
            window.Show();
            window.Left = -20000;
            window.LoadFileCore(file);
            PumpLongLineWindow(window);
            if (live && (!window.LivePreviewAttached || window.PlainText)) {
                LongLineWindowProbe.Check(report, ref failures, label + " live attached", false,
                    "live=" + window.LivePreviewAttached + ", plain=" + window.PlainText);
                return;
            }
            int[] starts = FoldTokenStarts(text);
            bool resolved = starts.Length == 3 && starts[0] > 0 && starts[1] > starts[0] && starts[2] > starts[1];
            LongLineWindowProbe.Check(report, ref failures, label + (live ? " live" : " txt") + " tokens resolved", resolved,
                "starts=" + JoinInts(starts) + ", length=" + text.Length);
            if (!resolved) return;
            string prefix = label + (live ? " live" : " txt");
            window.SetWrap(true);
            PumpLongLineWindow(window);
            VerifyFoldSearchPasses(window, starts, prefix + " wrap on", report, ref failures);
            window.SetWrap(false);
            PumpLongLineWindow(window);
            PumpLongLineWindow(window);
            VerifyFoldSearchPasses(window, starts, prefix + " wrap off", report, ref failures);
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception fold search " + label + (live ? " live " : " txt ") + ex);
        } finally {
            ProbeDefaults.Live = previousDefault;
            if (window != null) {
                try { window.text = window.source.Document.Text; window.saved = window.text; } catch (Exception) { }
                try { window.Close(); } catch (Exception) { }
                LongLineBench.Idle();
            }
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
        }
    }

    static string JoinInts(int[] values) {
        var text = new StringBuilder();
        for (int i = 0; i < values.Length; i++) { if (i > 0) text.Append(','); text.Append(values[i]); }
        return text.ToString();
    }

    static void VerifyFoldSearchPasses(MainWindow window, int[] starts, string label, StringBuilder report, ref int failures) {
        DocumentLine line = window.source.Document.GetLineByNumber(4);
        SetFoldQuery(window, FoldToken);
        // Start at the line end so the first press wraps backwards into the folded prefix.
        window.source.Select(line.EndOffset - 1, 0);
        PumpLongLineWindow(window);
        VerifyFoldNext(window, 0, starts, label + " next wraps into the start fold", report, ref failures);
        VerifyFoldNext(window, 1, starts, label + " next across the fold to the middle", report, ref failures);
        VerifyFoldNext(window, 2, starts, label + " next across the fold to the end", report, ref failures);
        window.FindNext(true);
        PumpLongLineWindow(window);
        VerifyFoldSelection(window, 1, starts, label + " previous across the fold to the middle", report, ref failures);
        window.FindNext(true);
        PumpLongLineWindow(window);
        VerifyFoldSelection(window, 0, starts, label + " previous across the fold to the start", report, ref failures);
        window.FindNext(false);
        PumpLongLineWindow(window);
        VerifyFoldSelection(window, 1, starts, label + " next from the start fold to the middle", report, ref failures);
        VerifyFoldHighlight(window, label, report, ref failures);
        VerifyFoldRepeat(window, label, report, ref failures);
        SetFoldQuery(window, FoldToken);
    }

    static void VerifyFoldNext(MainWindow window, int token, int[] starts, string label, StringBuilder report, ref int failures) {
        SetFoldQuery(window, FoldToken);
        window.FindNext(false);
        PumpLongLineWindow(window);
        VerifyFoldSelection(window, token, starts, label, report, ref failures);
    }

    // Typing the query already searches; pressing next only has to move, so the cached map
    // is reused. Wait for the async snapshot search when the surface is large.
    static void SetFoldQuery(MainWindow window, string token) {
        if (window.searchHost.Visibility != Visibility.Visible) window.ToggleSearch(false);
        if (!String.Equals(window.query.Text, token, StringComparison.Ordinal)) {
            window.query.Text = token;
            PumpLongLineWindow(window);
        }
        LongLineWindowProbe.WaitUntil(delegate { return !window.LargeSourceSearch || window.LargeSearchCurrent(); }, 30000);
    }

    static void VerifyFoldSelection(MainWindow window, int token, int[] starts, string label, StringBuilder report, ref int failures) {
        int selectionStart = window.source.SelectionStart, length = window.source.SelectionLength;
        string selected = window.source.SelectedText;
        bool match = selectionStart == starts[token] && length == FoldTokenLength
            && String.Equals(selected, FoldToken, StringComparison.Ordinal);
        bool revealed = MatchLongLineRevealed(window, selectionStart, length);
        bool real = FoldOffsetReal(window, selectionStart) && FoldOffsetReal(window, selectionStart + length - 1);
        LongLineWindow.Session session = window.longLines as LongLineWindow.Session;
        int windowStart = session == null ? -1 : session.WindowStart(4);
        LongLineWindowProbe.Check(report, ref failures, label, match && revealed && real,
            "selection=" + selectionStart + "+" + length + "/" + starts[token] + ", selected=" + selected
            + ", revealed=" + revealed + ", real=" + real + ", window_start=" + windowStart);
    }

    static bool FoldOffsetReal(MainWindow window, int offset) {
        return LongLineWindow.IsRealOffset(window.source, offset);
    }

    // The drawn highlight must sit on the match's first character (the adorner and the probe
    // both work in the editor's own coordinates), and a folded character must produce no
    // rectangle at all - its visual column is the fold's, so the old code drew the box at the
    // fold edge instead of the character.
    static void VerifyFoldHighlight(MainWindow window, string label, StringBuilder report, ref int failures) {
        SearchHighlight highlight = window.sourceHighlight;
        int start = window.source.SelectionStart, length = window.source.SelectionLength;
        Rect expected = window.source.GetRectFromCharacterIndex(start, false);
        bool onMatch = false;
        double drawnLeft = double.NaN, drawnTop = double.NaN;
        if (highlight != null && highlight.SourceStart == start && highlight.SourceLength == length) {
            for (int i = 0; i < highlight.GeometryCount; i++) {
                Rect rect = highlight.GeometryAt(i);
                if (rect.IsEmpty) continue;
                if (Math.Abs(rect.Left - expected.Left) <= 2 && Math.Abs(rect.Top - expected.Top) <= 2) {
                    onMatch = true; drawnLeft = rect.Left; drawnTop = rect.Top; break;
                }
            }
        }
        LongLineWindowProbe.Check(report, ref failures, label + " highlight on match",
            onMatch && highlight != null && highlight.GeometryCount > 0,
            "rect_count=" + (highlight == null ? -1 : highlight.GeometryCount)
            + ", expected=(" + expected.Left.ToString("0.0", CultureInfo.InvariantCulture) + ","
            + expected.Top.ToString("0.0", CultureInfo.InvariantCulture) + "), drawn=("
            + drawnLeft.ToString("0.0", CultureInfo.InvariantCulture) + "," + drawnTop.ToString("0.0", CultureInfo.InvariantCulture) + ")"
            + ", source_start=" + (highlight == null ? -1 : highlight.SourceStart));
        if (highlight == null) return;
        // A match that stays inside a fold: the adorner must draw nothing for it.
        DocumentLine line = window.source.Document.GetLineByNumber(4);
        int folded = -1;
        int[] candidates = { line.Offset + line.Length / 4, line.Offset + line.Length / 2, line.Offset + 3 * line.Length / 4,
            line.Offset + 1, line.EndOffset - FoldTokenLength - 1 };
        foreach (int candidate in candidates) {
            if (candidate >= line.Offset && candidate + length <= line.EndOffset
                && !FoldOffsetReal(window, candidate) && !FoldOffsetReal(window, candidate + length - 1)) { folded = candidate; break; }
        }
        if (folded < 0) {
            LongLineWindowProbe.Check(report, ref failures, label + " folded highlight skipped", false, "no folded span found");
            return;
        }
        highlight.SourceStart = folded;
        highlight.SourceLength = length;
        highlight.Reposition();
        int foldedCount = highlight.GeometryCount;
        window.RefreshSearch();
        PumpLongLineWindow(window);
        LongLineWindowProbe.Check(report, ref failures, label + " folded highlight skipped", foldedCount == 0,
            "folded_offset=" + folded + ", rects=" + foldedCount);
    }

    // The same match pressed again raises no caret event (AvalonEdit ignores an identical
    // caret offset), so it cannot be scrolled back by the caret path; the search hook still
    // has to put it on screen. The query here has exactly one match, so a press cannot move
    // to a neighbour, and its match sits inside the folded suffix.
    static void VerifyFoldRepeat(MainWindow window, string label, StringBuilder report, ref int failures) {
        SetFoldQuery(window, FoldRepeatToken);
        window.source.Select(0, 0);
        PumpLongLineWindow(window);
        window.FindNext(false);
        PumpLongLineWindow(window);
        int start = window.source.SelectionStart;
        if (start < 0 || !String.Equals(window.source.SelectedText, FoldRepeatToken, StringComparison.Ordinal)) {
            LongLineWindowProbe.Check(report, ref failures, label + " repeat token found", false,
                "selection=" + window.source.SelectionStart + "+" + window.source.SelectionLength
                + ", selected=" + window.source.SelectedText);
            return;
        }
        int length = window.source.SelectionLength;
        TextView view = window.source.TextArea.TextView;
        view.EnsureVisualLines();
        DocumentLine line = window.source.Document.GetLineByNumber(4);
        VisualLine visual = view.GetOrConstructVisualLine(line);
        if (visual == null || visual.IsDisposed) return;
        var scroll = (System.Windows.Controls.Primitives.IScrollInfo)view;
        int column = visual.GetVisualColumn(start - line.Offset);
        Point position = visual.GetVisualPosition(column, VisualYPosition.LineMiddle);
        // Away from the match in the direction this mode scrolls: wrap on has a tall line,
        // wrap off a wide one.
        if (window.source.WordWrap) {
            double height = Math.Max(1, scroll.ViewportHeight), away = position.Y + height * 3;
            if (away > visual.VisualTop + visual.Height - 1) away = Math.Max(0, position.Y - height * 3);
            scroll.SetVerticalOffset(Math.Max(0, away));
        } else scroll.SetHorizontalOffset(Math.Max(0, position.X + scroll.ViewportWidth * 2));
        PumpLongLineWindow(window);
        bool scrolledAway = !MatchLongLineRevealed(window, start, length);
        window.FindNext(false);
        PumpLongLineWindow(window);
        bool match = window.source.SelectionStart == start && window.source.SelectionLength == length;
        bool revealed = MatchLongLineRevealed(window, start, length);
        LongLineWindowProbe.Check(report, ref failures, label + " repeat press reveals the same match",
            scrolledAway && match && revealed,
            "scrolled_away=" + scrolledAway + ", selection=" + window.source.SelectionStart + "+"
            + window.source.SelectionLength + "/" + start + "+" + length + ", revealed=" + revealed);
        window.source.Select(line.Offset, 0);
        PumpLongLineWindow(window);
    }

    // Height of one laid-out line in the TXT host, after forcing the visual line.
    static double TxtRowHeight(MainWindow window,int lineNumber) {
        TextView view=window.source.TextArea.TextView;
        view.EnsureVisualLines();
        DocumentLine line=window.source.Document.GetLineByNumber(lineNumber);
        VisualLine visual=view.GetOrConstructVisualLine(line);
        return visual==null?double.NaN:visual.Height;
    }

    static double TxtStruts(MainWindow window,int lineNumber) {
        TextView view=window.source.TextArea.TextView;
        DocumentLine line=window.source.Document.GetLineByNumber(lineNumber);
        VisualLine visual=view.GetOrConstructVisualLine(line);
        return visual==null?-1:LongLineWindowProbe.Struts(visual);
    }

    static string FindLongLineNext(MainWindow window,string expected,string label,StringBuilder report,ref int failures) {
        int tokenIndex=expected.IndexOf(LongLineWindowProbe.Token,LongLineWindowProbe.Prefix.Length);
        window.source.Select(0,0);
        PumpLongLineWindow(window);
        if(window.searchHost.Visibility!=Visibility.Visible)window.ToggleSearch(false);
        window.query.Text=String.Empty;
        PumpLongLineWindow(window);
        window.query.Text=LongLineWindowProbe.Token;
        PumpLongLineWindow(window);
        bool ready=LongLineWindowProbe.WaitUntil(delegate { return window.LargeSearchCurrent(); },30000);
        window.FindNext(false);
        PumpLongLineWindow(window);
        int start=window.source.SelectionStart,length=window.source.SelectionLength;
        string selected=window.source.SelectedText;
        bool match=start==tokenIndex&&length==LongLineWindowProbe.Token.Length
            &&String.Equals(selected,LongLineWindowProbe.Token,StringComparison.Ordinal);
        bool revealed=MatchLongLineRevealed(window,start,length);
        LongLineWindow.Session session=window.longLines as LongLineWindow.Session;
        LongLineWindowProbe.Check(report,ref failures,label,ready&&match&&revealed,
            "ready="+ready+", selection="+start+"+"+length+"/"+tokenIndex+", selected="+selected+", revealed="+revealed
            +", window_start="+(session==null?"-":session.WindowStart(4).ToString(CultureInfo.InvariantCulture)));
        window.CloseSearch();
        PumpLongLineWindow(window);
        return expected;
    }

    static bool MatchLongLineRevealed(MainWindow window,int start,int length) {
        SourceEditor source=window.source;
        TextView view=source.TextArea.TextView;
        view.EnsureVisualLines();
        if(start<0||length<=0||start+length>source.Document.TextLength)return false;
        DocumentLine line=source.Document.GetLineByOffset(start);
        VisualLine visual=view.GetOrConstructVisualLine(line);
        if(visual==null||visual.IsDisposed)return false;
        int begin=start-line.Offset,end=start+length-line.Offset;
        bool textStart=false,textEnd=false;
        foreach(VisualLineElement element in visual.Elements){
            VisualLineText piece=element as VisualLineText;
            if(piece==null)continue;
            int first=piece.RelativeTextOffset,last=first+piece.DocumentLength;
            if(begin>=first&&begin<=last)textStart=true;
            if(end>=first&&end<=last)textEnd=true;
        }
        if(!textStart||!textEnd)return false;
        int relative=source.CaretOffset-line.Offset;
        int column=visual.GetVisualColumn(relative);
        Point caret=visual.GetVisualPosition(column,VisualYPosition.LineMiddle);
        if(!source.WordWrap){
            double x=caret.X-view.HorizontalOffset;
            return x>=-1&&x<=source.ViewportWidth+1;
        }
        double y=caret.Y-view.VerticalOffset;
        return y>=-1&&y<=source.ViewportHeight+1;
    }

    // ------------------------------------ document replacement with the caret on a long line
    // Closing a tab or a window releases the surface by assigning a fresh, empty TextDocument
    // (Workspace.ReleaseTabSurfaces). The caret reset inside TextArea.OnDocumentChanged reaches
    // LongLineWindow.Session.CaretChanged before TextEditor raises DocumentChanged, so a session
    // whose caret sat on a >16,384-char line used to rebuild against the replaced document:
    // InvalidOperationException "Line belongs to wrong document", seen as process exit 0xc000041d
    // from the real close path. Every case below must raise no exception, synchronously or
    // through the dispatcher, and the window close must also stay clean.
    internal static void VerifyLongLineDocumentSwap(string directory, StringBuilder report, ref int failures) {
        string md = Path.Combine(directory, "longline-window-swap.md");
        string md2 = Path.Combine(directory, "longline-window-swap2.md");
        string txt = Path.Combine(directory, "longline-window-swap.txt");
        string other = Path.Combine(directory, "longline-window-swap-other.md");
        try {
            string body = LongLineWindowProbe.Generate(50000);
            File.WriteAllText(md, body + "\nshort second\nshort third", new UTF8Encoding(false));
            File.WriteAllText(md2, body + "\nshort second\nshort third", new UTF8Encoding(false));
            File.WriteAllText(txt, body, new UTF8Encoding(false));
            File.WriteAllText(other, "short one\nshort two", new UTF8Encoding(false));
            SwapCaseDirect(md, report, ref failures);
            SwapCaseTabClose(md, md2, report, ref failures);
            SwapCaseWindowClose(md, report, ref failures);
            SwapCaseOtherFile(md, other, report, ref failures);
            SwapCaseTextMode(txt, report, ref failures);
        } catch (Exception ex) {
            failures++;
            report.AppendLine("FAIL exception document swap fixture " + ex.ToString());
        } finally {
            try { if (File.Exists(md)) File.Delete(md); } catch (IOException) { }
            try { if (File.Exists(md2)) File.Delete(md2); } catch (IOException) { }
            try { if (File.Exists(txt)) File.Delete(txt); } catch (IOException) { }
            try { if (File.Exists(other)) File.Delete(other); } catch (IOException) { }
        }
    }

    static MainWindow SwapWindow(string file) {
        var window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=1000, Height=800 };
        window.Show();
        window.Left = -20000;
        window.LoadFileCore(file);
        PumpLongLineWindow(window);
        return window;
    }

    // Caret in the middle of the long first line: its real window starts well past column 0, so
    // the caret reset to (1,1) during a document swap falls outside it and the caret path rebuilds.
    // The replaced line is then laid out against the new document, which is the crash.
    static int SwapCaret(MainWindow window) {
        DocumentLine line = window.source.Document.GetLineByNumber(1);
        int at = line.Offset + Math.Min(25000, line.Length / 2);
        window.source.Select(at, 0);
        PumpLongLineWindow(window);
        return at;
    }

    static bool SwapSessionWindowed(MainWindow window) {
        var session = window.longLines as LongLineWindow.Session;
        return session != null && session.IsAttached && session.WindowStart(1) > 0;
    }

    static string SwapException(Exception error) {
        return error == null ? "none" : error.GetType().Name + ": " + error.Message;
    }

    // Marks every tab clean, then closes the window. Idle pumps the queued layout after the
    // close, so a callback that throws there lands in DispatchErrors and fails the step.
    static Exception CloseSwapWindow(MainWindow window) {
        if (window == null) return null;
        try { window.CaptureTab(); } catch (Exception) { }
        try { foreach (DocumentTab state in window.documents) { state.Saved = state.Text; state.Recovered = false; } } catch (Exception) { }
        try { window.saved = window.text; window.recoveredDocument = false; } catch (Exception) { }
        Exception error = null;
        try { if (window.IsLoaded) window.Close(); } catch (Exception ex) { error = ex; }
        LongLineBench.Idle();
        return error;
    }

    static void SwapReport(StringBuilder report, ref int failures, string name, int errorsBefore,
        bool prepared, Exception thrown, Exception closed, string numbers) {
        int errors = LongLineWindowProbe.DispatchErrors - errorsBefore;
        LongLineWindowProbe.Check(report, ref failures, name, prepared && thrown == null && closed == null && errors == 0,
            "prepared=" + prepared + ", throw=" + SwapException(thrown) + ", close=" + SwapException(closed)
            + ", dispatcher_errors=" + errors + (numbers.Length == 0 ? "" : ", " + numbers));
    }

    // (a) The release step itself: Workspace.ReleaseTabSurfaces assigns a fresh TextDocument on a
    // surface that still holds the caret on the long line.
    static void SwapCaseDirect(string file, StringBuilder report, ref int failures) {
        int errors = LongLineWindowProbe.DispatchErrors;
        MainWindow window = null;
        bool prepared = false;
        Exception thrown = null;
        try {
            window = SwapWindow(file);
            SwapCaret(window);
            prepared = SwapSessionWindowed(window);
            window.source.Document = new ICSharpCode.AvalonEdit.Document.TextDocument();
        } catch (Exception ex) { thrown = ex; }
        Exception closed = CloseSwapWindow(window);
        SwapReport(report, ref failures, "j1 direct document swap", errors, prepared, thrown, closed, "");
    }

    // (b) Tab close: the second tab (the active one) holds the long line; CloseTab releases it.
    static void SwapCaseTabClose(string file, string second, StringBuilder report, ref int failures) {
        int errors = LongLineWindowProbe.DispatchErrors;
        MainWindow window = null;
        bool prepared = false;
        Exception thrown = null;
        string numbers = "";
        try {
            window = SwapWindow(file);
            SwapCaret(window);              // the tab this switch leaves holds the long-line caret
            window.LoadFile(second);
            PumpLongLineWindow(window);
            SwapCaret(window);
            window.SwitchTab(window.documents[0]);
            PumpLongLineWindow(window);
            window.SwitchTab(window.documents[1]);
            PumpLongLineWindow(window);
            prepared = window.documents.Count == 2 && SwapSessionWindowed(window);
            numbers = "tabs=" + window.documents.Count;
            window.CloseTab(window.activeDocument);
            PumpLongLineWindow(window);
        } catch (Exception ex) { thrown = ex; }
        Exception closed = CloseSwapWindow(window);
        SwapReport(report, ref failures, "j2 tab close", errors, prepared, thrown, closed, numbers);
    }

    // (c) Window close: the Closed handler releases every tab surface.
    static void SwapCaseWindowClose(string file, StringBuilder report, ref int failures) {
        int errors = LongLineWindowProbe.DispatchErrors;
        MainWindow window = null;
        bool prepared = false;
        Exception thrown = null;
        try {
            window = SwapWindow(file);
            SwapCaret(window);
            prepared = SwapSessionWindowed(window);
            window.CaptureTab();
            window.saved = window.text;
            window.recoveredDocument = false;
            window.Close();
        } catch (Exception ex) { thrown = ex; }
        Exception closed = CloseSwapWindow(window);
        SwapReport(report, ref failures, "j3 window close", errors, prepared, thrown, closed, "");
    }

    // Same tab, another file, then the long file again: the TextDocument object stays, its whole
    // text is replaced, and the last load runs with the caret on the long line (reload).
    static void SwapCaseOtherFile(string file, string other, StringBuilder report, ref int failures) {
        int errors = LongLineWindowProbe.DispatchErrors;
        MainWindow window = null;
        bool prepared = false;
        Exception thrown = null;
        try {
            window = SwapWindow(file);
            SwapCaret(window);
            window.LoadFileCore(other);
            PumpLongLineWindow(window);
            window.LoadFileCore(file);
            PumpLongLineWindow(window);
            SwapCaret(window);
            window.LoadFileCore(file);
            PumpLongLineWindow(window);
            var session = window.longLines as LongLineWindow.Session;
            prepared = session != null && session.IsAttached;
        } catch (Exception ex) { thrown = ex; }
        Exception closed = CloseSwapWindow(window);
        SwapReport(report, ref failures, "j4 other file and reload in the same tab", errors, prepared, thrown, closed, "");
    }

    // (d) TXT <-> MD: the surface keeps its TextDocument, the live preview attaches and detaches.
    static void SwapCaseTextMode(string file, StringBuilder report, ref int failures) {
        int errors = LongLineWindowProbe.DispatchErrors;
        MainWindow window = null;
        bool prepared = false;
        Exception thrown = null;
        try {
            window = SwapWindow(file);
            SwapCaret(window);
            bool plain = window.PlainText;
            window.ToggleTextMode();
            PumpLongLineWindow(window);
            window.ToggleTextMode();
            PumpLongLineWindow(window);
            var session = window.longLines as LongLineWindow.Session;
            prepared = plain && window.PlainText && session != null && session.IsAttached;
        } catch (Exception ex) { thrown = ex; }
        Exception closed = CloseSwapWindow(window);
        SwapReport(report, ref failures, "j5 txt markdown toggle", errors, prepared, thrown, closed, "");
    }
}
}
