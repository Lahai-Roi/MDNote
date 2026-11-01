using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// Generated text only. Run on the hidden desktop: Rendering must really fire, not be
// replaced by a geometry query that constructs/repairs the line being asserted.
// IMM result delivery is simulated through SourceEditor's existing composition seams
// and PerformTextInput. This is NOT a physical Korean keyboard/candidate-window test.
internal static class LiveTypingStabilityProbe {
    const int Keys=1024;
    // Long-session case: the owner's complaint was that typing gets slower the longer the
    // session runs. Two short buffers cannot show that, so this case types 6000 syllables
    // across a ~200 line document and samples frames every 10th key.
    const int LongKeys=6000,LongSampleEvery=10,LongEnterEvery=60,LongBackspaceEvery=200,LongMoveEvery=600;
    // Per-counter absolute caps for the long case. Entry/character budgets are the product's
    // own per-table bounds (512 entries / 32768 chars, at most 16 tables); line-like caches are
    // viewport bounded. Subscriptions (7-10, 14-17) are held to an exact constant instead.
    static readonly int[] LongCaps={512,17,9000,600000,9000,600000,1100,0,0,0,0,1,16,512,0,0,0,0};
    // How much a counter may add over the last 1000 keys. Character budgets can jump by a whole
    // measured cell, so they are checked against the absolute cap only.
    static readonly int[] LongGrowth={16,4,64,600000,64,600000,16,0,0,0,0,1,16,64,0,0,0,0};
    const BindingFlags Fields=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly FieldInfo DrawingField=typeof(VisualLine).GetField("visual",Fields);
    static readonly FieldInfo AllLinesField=typeof(TextView).GetField("allVisualLines",Fields);
    static readonly string[] CountNames={"line cache","tables","metrics","metric characters","shapes","shape characters","hot rows",
        "generators","transformers","renderers","line trackers","composition work slots","baseline cache","native visual lines",
        "document Changing handlers","document Changed handlers","caret handlers","selection handlers"};
    struct Sample {
        internal double Top,Height,RowTop,RowHeight,DrawnTop,DrawnHeight,CompositionTop,Em;
        internal int Offset;
        internal bool Valid;
        internal double[] Widths;
    }
    sealed class Frames : IDisposable {
        internal SourceEditor Editor;
        internal bool Enabled;
        internal int Total;
        internal readonly List<Sample> Samples=new List<Sample>();
        internal Frames(SourceEditor editor) {Editor=editor;CompositionTarget.Rendering+=OnRendering;}
        void OnRendering(object sender,EventArgs e) {
            if (!Enabled) return;
            Total++;if (Samples.Count<1024) Samples.Add(Capture(Editor));
        }
        public void Dispose() {CompositionTarget.Rendering-=OnRendering;}
    }
    internal static int Run(string outDir) {
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string pass=Path.Combine(outDir,"live-typing-probe-PASS.txt"),fail=Path.Combine(outDir,"live-typing-probe-FAIL.txt");
        if (File.Exists(pass)) File.Delete(pass);if (File.Exists(fail)) File.Delete(fail);
        var report=new StringBuilder();var timings=new StringBuilder("case\tkey\thandler_to_background_ms\tframes\tline_top\tline_height\tmax_frame_delta\tcache_counts\trow_build_us\trow_builds\n");
        bool all=true,oldSelfTest=Program.IsSelfTest;int checks=0,failures=0;
        double worstHandler=0,worstFrameDelta=0;int worstFrameCount=0,worstKeys=0;
        Application app=null;ShutdownMode oldShutdown=ShutdownMode.OnExplicitShutdown;
        Window window=null;SourceEditor editor=null;MarkdownLivePreview.Session session=null;Frames frames=null;
        Action<string,bool,string> check=delegate(string name,bool ok,string detail) {
            checks++;if (!ok) failures++;all&=ok;
            report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(": ").Append(detail).Append('\n');
        };
        try {
            Program.IsSelfTest=true;
            if (Application.Current==null) new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app=Application.Current;oldShutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            window=new Window {Content=editor,Width=1920,Height=1080,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,Left=0,Top=0};
            window.Show();Pump();frames=new Frames(editor);
            // A pre-existing, ordinarily sized paragraph, and four independently wrapping
            // cells. No resetting/trimming the buffer between keys or dropping slow keys.
            string paragraph="before\n\n본문 시작 "+Repeat("가나다라마바사 아자차카타파하 ",12)+"\n\ntail\n";
            string table="before\n\n| 첫째 제목 | 둘째 제목 | 셋째 제목 | 넷째 제목 |\n|---|---|---|---|\n| "+
                Repeat("가나다라마바사 아자차카타파하 ",5)+" | "+Repeat("이웃 셀 문장 ",12)+" | "+Repeat("셋째 칸 내용 ",12)+" | "+Repeat("넷째 칸 내용 ",12)+" |\n| 아래 | 행 | 유지 | 확인 |\n\nafter\n";
            string[] fixtures={paragraph,table};string[] names={"paragraph","four-column wrapped table"};
            for (int test=0;test<fixtures.Length;test++) {
                // A throwing case must not hide the remaining cases: report it as a FAIL
                // and continue with the next fixture.
                try {
                if (session!=null) {session.Dispose();session=null;}
                editor.Text=fixtures[test];editor.CaretIndex=0;
                session=Attach(editor);Pump();
                var target=editor.Document.GetLineByNumber(test==0 ? 3 : 5);
                int at=test==0 ? target.EndOffset : target.Offset+session.Info(target).Cells[0].End;
                editor.Select(at,0);editor.TextArea.Caret.BringCaretToView();Pump();
                // Warm the dispatch/formatter/input paths in another document first, then
                // restore this same fixture. Do not include JIT in the first 100-key mean.
                for (int warm=0;warm<24;warm++) Compose(editor,"가");
                Pump();editor.Text=fixtures[test];editor.CaretIndex=0;Pump();
                target=editor.Document.GetLineByNumber(test==0 ? 3 : 5);
                at=test==0 ? target.EndOffset : target.Offset+session.Info(target).Cells[0].End;
                editor.Select(at,0);editor.TextArea.Caret.BringCaretToView();Pump();
                var elapsed=new double[Keys];var counts=new List<int[]>();
                // Attribution: the product's own row build (RowLayout+TableProjection) versus
                // everything else (dispatcher, WPF text line formatting, drawing). If the
                // handler time grows with the wrapped row count but this stays flat, the
                // remaining cost is WPF formatting each physical row of the visual line.
                var rowBuild=new double[Keys];var rowBuilds=new int[Keys];
                // The counters are deltas per key. Sampling them once outside the loop made
                // both columns cumulative (key 50 vs key 974), which reads as a per-key
                // build count of ~1000 and hides that the row is built about once per key.
                int maxBuilds=0;
                int badFrames=0,missingFrames=0,badGeometryKeys=0,badInput=0,maxCallbacks=0,maxSlots=0,totalFrames=0;
                double worstDelta=0,maxTime=0,firstHeight=Capture(editor).Height;bool wrapped=false;
                long nativeBefore=LiveCompositionGeometry.NativeWrites;
                for (int key=0;key<Keys;key++) {
                    frames.Samples.Clear();frames.Total=0;frames.Enabled=true;
                    int length=editor.Document.TextLength,offset=editor.CaretIndex;
                    long callbacks=session.CompositionCallbacks;
                    long rowTicksBefore=MarkdownLivePreview.DiagRowTicks,rowsBefore=MarkdownLivePreview.DiagRows;
                    var watch=Stopwatch.StartNew();
                    Compose(editor,((char)(0xAC00+(key*37)%11172)).ToString());
                    bool reached=Drain(DispatcherPriority.Background,2000);watch.Stop();
                    elapsed[key]=watch.Elapsed.TotalMilliseconds;maxTime=Math.Max(maxTime,elapsed[key]);
                    if (!reached) {check(names[test]+" dispatcher starvation",false,"key="+key);break;}
                    // A Background marker alone can beat the compositor's next vsync. Keep
                    // this key open until at least one actual Rendering event, then idle.
                    if (frames.Total==0 && !AwaitFrame(frames,2000)) missingFrames++;
                    reached=Drain(DispatcherPriority.ApplicationIdle,2000);
                    Sample final=Capture(editor);frames.Enabled=false;
                    rowBuild[key]=(MarkdownLivePreview.DiagRowTicks-rowTicksBefore)*1000000.0/Stopwatch.Frequency;
                    rowBuilds[key]=(int)(MarkdownLivePreview.DiagRows-rowsBefore);
                    maxBuilds=Math.Max(maxBuilds,rowBuilds[key]);
                    if (!reached) {check(names[test]+" idle starvation",false,"key="+key);break;}
                    if (editor.Document.TextLength!=length+1 || editor.CaretIndex!=offset+1 ||
                        editor.Document.GetText(offset,1)!=((char)(0xAC00+(key*37)%11172)).ToString()) badInput++;
                    double delta=0;bool same=final.Valid && Close(final.Top,final.DrawnTop) && Close(final.Height,final.DrawnHeight);
                    foreach (Sample frame in frames.Samples) {
                        double difference=Difference(frame,final);delta=Math.Max(delta,difference);
                        if (difference>=.01) {badFrames++;same=false;}
                    }
                    if (!same && badGeometryKeys++<4) report.Append("FRAME ").Append(names[test]).Append(" key=").Append(key).Append(" final=").Append(Describe(final)).Append(" worst=").Append(Number(delta)).Append('\n');
                    if (frames.Total==0) missingFrames++;
                    totalFrames+=frames.Total;worstDelta=Math.Max(worstDelta,delta);wrapped|=final.Height>firstHeight+.01;
                    int callbackCount=(int)(session.CompositionCallbacks-callbacks);maxCallbacks=Math.Max(maxCallbacks,callbackCount);
                    int[] current=Counts(editor,session);counts.Add(current);maxSlots=Math.Max(maxSlots,current[11]);
                    timings.Append(names[test]).Append('\t').Append(key).Append('\t').Append(Number(elapsed[key])).Append('\t').Append(frames.Total)
                        .Append('\t').Append(Number(final.Top)).Append('\t').Append(Number(final.Height)).Append('\t').Append(Number(delta)).Append('\t').Append(Join(current))
                        .Append('\t').Append(rowBuild[key].ToString("F3",CultureInfo.InvariantCulture)).Append('\t').Append(rowBuilds[key]).Append('\n');
                }
                frames.Enabled=false;
                double first=Mean(elapsed,0,100),last=Mean(elapsed,Keys-100,100),ratio=last/Math.Max(.000001,first);
                check(names[test]+" 1024 Korean start/update/commit keys",counts.Count==Keys && badInput==0,"keys="+counts.Count+", bad inputs="+badInput);
                check(names[test]+" every rendered frame equals final geometry",badFrames==0 && badGeometryKeys==0 && missingFrames==0,
                    "frames="+totalFrames+", bad frames="+badFrames+", bad keys="+badGeometryKeys+", missing="+missingFrames+", worst delta="+Number(worstDelta));
                // b50: a wrapped table cell grows from ~2 to ~36 physical rows during the run and WPF formats one
                // TextLine per row, so the cell case is held to an absolute bound (no growth from our own caches is
                // checked by the bounded-count and row-build lines); the paragraph keeps the ratio bound.
                bool cellCase=test>0;
                check(names[test]+(cellCase?" last 100 mean <= 4ms":" last/first 100 mean <= 1.5"),counts.Count==Keys && (cellCase?last<=4:ratio<=1.5),
                    "first="+Number(first)+"ms, last="+Number(last)+"ms, ratio="+Number(ratio)+", worst key="+Number(maxTime)+"ms (one run)");
                int builds=0,maxBuildsLast=0;
                for (int p=Keys-100;p<Keys;p++) {builds+=rowBuilds[p];maxBuildsLast=Math.Max(maxBuildsLast,rowBuilds[p]);}
                report.Append("ATTR ").Append(names[test]).Append(" row_build_us first=").Append(Number(Mean(rowBuild,0,100)))
                    .Append(", last=").Append(Number(Mean(rowBuild,Keys-100,100))).Append(", builds_per_key_last=").Append(Number(builds/100.0))
                    .Append(", max_builds_per_key=").Append(maxBuilds).Append('\n');
                // The row layout must be built at most once per key for the edited row and
                // never for an unchanged row, so the last 100 keys may average at most 2.
                check(names[test]+" row layout builds per key <= 2",counts.Count==Keys && builds/100.0<=2.0,
                    "mean over last 100="+Number(builds/100.0)+", max in last 100="+maxBuildsLast+", max overall="+maxBuilds);
                check(names[test]+" coalesced composition callbacks",maxCallbacks<=8 && maxSlots<=1,"max callbacks/key="+maxCallbacks+", idle slots="+maxSlots+", native writes="+(LiveCompositionGeometry.NativeWrites-nativeBefore));
                VerifyCounts(names[test],counts,check);
                check(names[test]+" line wraps during sustained input",wrapped,"initial height="+Number(firstHeight)+", final="+Number(Capture(editor).Height));
                if (elapsed.Length>0) {
                    worstHandler=Math.Max(worstHandler,maxTime);worstFrameDelta=Math.Max(worstFrameDelta,worstDelta);
                    worstFrameCount=Math.Max(worstFrameCount,totalFrames);worstKeys=Math.Max(worstKeys,counts.Count);
                }
                Sample quiet=Capture(editor);int[] beforeQuiet=Counts(editor,session);Wait(650);Sample afterQuiet=Capture(editor);
                check(names[test]+" quiet active geometry stays fixed",Difference(quiet,afterQuiet)<.01,"delta="+Number(Difference(quiet,afterQuiet)));
                check(names[test]+" no continuing idle composition chain",session.PendingCompositionWork==0 && beforeQuiet[11]==0,"pending="+session.PendingCompositionWork);
                } catch (Exception ex) {check(names[test]+" case exception",false,ex.ToString());}
            }
            // Third case: a long single session (the owner's actual complaint). A throwing
            // case must not hide the click/br checks that follow.
            try {
                if (session!=null) {session.Dispose();session=null;}
                RunLongSession(editor,ref session,frames,outDir,check,report);
            } catch (Exception ex) {check("long session case exception",false,ex.ToString());}
            VerifyClicks(window,editor,ref session,check);
            VerifyBreaks(window,editor,ref session,check);
            try { VerifyCellGrowthAndComposition(window,editor,ref session,check); }
            catch (Exception ex) { check("cell growth case exception",false,ex.ToString()); }
            try { VerifyClickAtEndAndCompose(window,editor,ref session,check); }
            catch (Exception ex) { check("click at end case exception",false,ex.ToString()); }
            try { VerifyCaretRoundTrip(window,editor,ref session,check); }
            catch (Exception ex) { check("caret round trip case exception",false,ex.ToString()); }
            try { VerifyTableNavigation(window,editor,ref session,check); }
            catch (Exception ex) { check("table navigation case exception",false,ex.ToString()); }
            report.Append("NOTE No physical IME/candidate window assertion. Rendering, native selection replacement, raw markdown and queues are tested.\n");
        } catch (Exception ex) {check("exception",false,ex.ToString());}
        finally {
            if (frames!=null) frames.Dispose();
            if (editor!=null) editor.ResetCompositionSimulation();
            if (session!=null) session.Dispose();
            if (window!=null) window.Close();
            Program.IsSelfTest=oldSelfTest;if (app!=null) app.ShutdownMode=oldShutdown;
        }
        report.Append("WORST handler_ms=").Append(Number(worstHandler)).Append(", frame_delta=").Append(Number(worstFrameDelta))
            .Append(", frames_per_key=").Append(worstFrameCount).Append(", keys_sampled=").Append(worstKeys).Append('\n');
        report.Append("SUMMARY checks=").Append(checks).Append(", failures=").Append(failures).Append(", required keys/case=").Append(Keys).Append('\n');
        string result=report.ToString();
        File.WriteAllText(Path.Combine(outDir,"live-typing-probe.txt"),result,new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir,"live-typing-summary.txt"),result,new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir,"live-typing-keys.tsv"),timings.ToString(),new UTF8Encoding(false));
        File.WriteAllText(all ? pass : fail,result,new UTF8Encoding(false));return all ? 0 : 1;
    }
    // Third case: a long session. Type 6000 syllables into a ~200 line mixed document (headings,
    // paragraphs, a bullet list, a numbered list, four-column tables, code fences and links),
    // moving the caret every 600 syllables to an intact table cell, a list item or the document
    // end. Structural Enter runs every 60 syllables (the command MainWindow's key handler calls;
    // skipped inside a table row so the cell keeps the typing), and five backspaces every 200.
    // The expected text is built alongside on a plain TextDocument fed the same inserts and the
    // same CommandResult edits, so the check catches the live surface rewriting the buffer.
    // Frames are captured every 10th key only; the ratio compares paragraph keys only (the same
    // caret kind at both ends), so a table/list segment cannot masquerade as the paragraph slope.
    static void RunLongSession(SourceEditor editor,ref MarkdownLivePreview.Session session,Frames frames,string outDir,Action<string,bool,string> check,StringBuilder report) {
        var timings=new StringBuilder("key\toperation\tcaret_kind\thandler_ms\tdocument_lines\tframes\tmax_frame_delta\tcache_counts\n");
        string fixture=LongFixture();bool starved=false;
        try {
        if (session!=null) {session.Dispose();session=null;}
        editor.Text=fixture;editor.CaretIndex=0;session=Attach(editor);Pump();
        // Warm the heading/code/link/table paths in this document, then reset so JIT and first
        // layout are not charged to the measured keys.
        int tableAt=FindTableCaret(editor,session,0);
        if (tableAt>=0) {editor.Select(tableAt,0);editor.TextArea.Caret.BringCaretToView();for (int warm=0;warm<12;warm++) Compose(editor,"가");Pump();}
        int listAt=FindListCaret(editor);
        if (listAt>=0) {editor.Select(listAt,0);editor.TextArea.Caret.BringCaretToView();for (int warm=0;warm<12;warm++) Compose(editor,"가");Pump();}
        editor.Select(editor.Document.TextLength,0);editor.TextArea.Caret.BringCaretToView();for (int warm=0;warm<24;warm++) Compose(editor,"가");Pump();
        editor.Text=fixture;Pump();
        TextDocument expected=new TextDocument(fixture);
        expected.UndoStack.SizeLimit=0;
        int mirror=editor.Document.TextLength;editor.Select(mirror,0);editor.TextArea.Caret.BringCaretToView();Pump();
        var elapsed=new double[LongKeys];var kinds=new byte[LongKeys];
        var counts=new List<int[]>(LongKeys);
        int typed=0,enters=0,moves=0,backspaces=0,tableMoves=0,badInput=0,missingFrames=0,badFrames=0,badGeometryKeys=0,maxSlots=0,maxCallbacks=0,totalFrames=0,sampledKeys=0;
        double worstDelta=0,worstHandler=0;
        long memoryStart=ManagedBytes();
        for (int key=0;key<LongKeys;key++) {
            // Every 600 syllables switch between an intact table cell, a list item and the end.
            if (key>0&&key%LongMoveEvery==0) {
                int to=(key/LongMoveEvery)%3;
                int target;
                if (to==1) {target=FindTableCaret(editor,session,tableMoves);tableMoves++;}
                else if (to==2) target=FindListCaret(editor);
                else target=editor.Document.TextLength;
                if (target<0) target=editor.Document.TextLength;
                var moveWatch=Stopwatch.StartNew();
                editor.Select(target,0);editor.TextArea.Caret.BringCaretToView();mirror=target;Pump();moveWatch.Stop();
                moves++;
                timings.Append(key).Append("\tmove\t").Append(CaretKind(editor)).Append('\t').Append(Number(moveWatch.Elapsed.TotalMilliseconds)).Append('\t').Append(editor.Document.LineCount).Append("\t\t\t\n");
            }
            // Structural Enter exactly where the key handler would route it; inside a table row
            // the handler gets null and the row would be split, so the timed cell keeps typing.
            if (key>0&&key%LongEnterEvery==0&&CaretKind(editor)!=1) {
                var enterWatch=Stopwatch.StartNew();
                PressEnter(editor,expected,ref mirror);
                if (!Drain(DispatcherPriority.Background,2000)) {check("long session enter starvation",false,"key="+key);starved=true;break;}
                enterWatch.Stop();enters++;
                timings.Append(key).Append("\tenter\t").Append(CaretKind(editor)).Append('\t').Append(Number(enterWatch.Elapsed.TotalMilliseconds)).Append('\t').Append(editor.Document.LineCount).Append("\t\t\t\n");
            }
            if (key>0&&key%LongBackspaceEvery==0) {
                var backWatch=Stopwatch.StartNew();
                for (int b=0;b<5;b++) PressBackspace(editor,expected,ref mirror);
                if (!Drain(DispatcherPriority.Background,2000)) {check("long session backspace starvation",false,"key="+key);starved=true;break;}
                backWatch.Stop();backspaces+=5;
                timings.Append(key).Append("\tbackspace\t").Append(CaretKind(editor)).Append('\t').Append(Number(backWatch.Elapsed.TotalMilliseconds)).Append('\t').Append(editor.Document.LineCount).Append("\t\t\t\n");
            }
            char typedChar=(char)(0xAC00+(key*37)%11172);
            int length=editor.Document.TextLength,offset=editor.CaretIndex;
            long callbacks=session.CompositionCallbacks;
            bool sampled=key%LongSampleEvery==0;
            frames.Samples.Clear();frames.Total=0;frames.Enabled=sampled;
            var watch=Stopwatch.StartNew();
            Compose(editor,typedChar.ToString());
            bool reached=Drain(DispatcherPriority.Background,2000);watch.Stop();
            elapsed[key]=watch.Elapsed.TotalMilliseconds;worstHandler=Math.Max(worstHandler,elapsed[key]);
            if (!reached) {check("long session dispatcher starvation",false,"key="+key);starved=true;break;}
            double delta=0;
            if (sampled) {
                if (frames.Total==0&&!AwaitFrame(frames,2000)) missingFrames++;
                if (!Drain(DispatcherPriority.ApplicationIdle,2000)) {check("long session idle starvation",false,"key="+key);starved=true;break;}
                Sample final=Capture(editor);frames.Enabled=false;sampledKeys++;
                bool same=final.Valid&&Close(final.Top,final.DrawnTop)&&Close(final.Height,final.DrawnHeight);
                foreach (Sample frame in frames.Samples) {
                    double difference=Difference(frame,final);delta=Math.Max(delta,difference);
                    if (difference>=.01) {badFrames++;same=false;}
                }
                if (!same&&badGeometryKeys++<4) report.Append("FRAME long key=").Append(key).Append(" final=").Append(Describe(final)).Append(" worst=").Append(Number(delta)).Append('\n');
                totalFrames+=frames.Total;worstDelta=Math.Max(worstDelta,delta);
            } else frames.Enabled=false;
            expected.Insert(mirror,typedChar.ToString());mirror++;typed++;
            if (editor.Document.TextLength!=length+1||editor.CaretIndex!=mirror||editor.Document.GetText(offset,1)!=typedChar.ToString()) badInput++;
            kinds[key]=(byte)CaretKind(editor);
            int[] current=Counts(editor,session);counts.Add(current);
            maxSlots=Math.Max(maxSlots,current[11]);
            maxCallbacks=Math.Max(maxCallbacks,(int)(session.CompositionCallbacks-callbacks));
            timings.Append(key).Append("\ttype\t").Append(kinds[key]).Append('\t').Append(Number(elapsed[key])).Append('\t').Append(editor.Document.LineCount)
                .Append('\t').Append(frames.Total).Append('\t').Append(Number(delta)).Append('\t').Append(Join(current)).Append('\n');
        }
        frames.Enabled=false;Pump();
        long memoryEnd=ManagedBytes();
        var paragraph=new List<double>();
        for (int key=0;key<LongKeys;key++) if (kinds[key]==0&&elapsed[key]>0) paragraph.Add(elapsed[key]);
        int firstCount=Math.Min(300,paragraph.Count),lastCount=Math.Min(300,paragraph.Count);
        double firstSum=0,lastSum=0;
        for (int p=0;p<firstCount;p++) firstSum+=paragraph[p];
        for (int p=paragraph.Count-lastCount;p<paragraph.Count;p++) lastSum+=paragraph[p];
        double firstMean=firstCount>0 ? firstSum/firstCount : 0,lastMean=lastCount>0 ? lastSum/lastCount : 0;
        double ratio=lastMean/Math.Max(.000001,firstMean);
        check("long session 6000 Korean start/update/commit keys",!starved&&typed==LongKeys&&counts.Count==LongKeys&&badInput==0,
            "typed="+typed+", samples="+counts.Count+", bad inputs="+badInput+", enters="+enters+", moves="+moves+", backspaces="+backspaces);
        // Only paragraph keys are compared: the caret kind (not the wall-clock position) must
        // match at both ends, otherwise a table/list spell would hide a paragraph regression.
        check("long session paragraph last 300 / first 300 mean <= 1.5",paragraph.Count>=600&&ratio<=1.5,
            "paragraph keys="+paragraph.Count+", first="+Number(firstMean)+"ms, last="+Number(lastMean)+"ms, ratio="+Number(ratio)+", worst key="+Number(worstHandler)+"ms (one run)");
        check("long session sampled frames equal final geometry",sampledKeys>0&&badFrames==0&&missingFrames==0&&badGeometryKeys==0,
            "sampled keys="+sampledKeys+", frames="+totalFrames+", bad frames="+badFrames+", bad keys="+badGeometryKeys+", missing="+missingFrames+", worst delta="+Number(worstDelta));
        VerifyLongCounts("long session",counts,check);
        check("long session coalesced composition callbacks",maxCallbacks<=8&&maxSlots<=1,"max callbacks/key="+maxCallbacks+", idle slots="+maxSlots);
        long heap=memoryEnd-memoryStart;
        check("long session managed heap growth <= 30 MB",heap<=30L*1024*1024,
            "start="+Number(memoryStart/1048576.0)+"MB, end="+Number(memoryEnd/1048576.0)+"MB, delta="+Number(heap/1048576.0)+"MB");
        check("long session final text equals expected",editor.Document.Text==expected.Text,
            "lengths="+editor.Document.TextLength+"/"+expected.TextLength+FirstDifference(editor.Document.Text,expected.Text));
        check("long session structural enter/backspace/move ran",enters>=60&&moves>=8&&backspaces>=140,
            "enters="+enters+", moves="+moves+", backspaces="+backspaces);
        report.Append("LONG typed=").Append(typed).Append(", enters=").Append(enters).Append(", moves=").Append(moves).Append(", backspaces=").Append(backspaces)
            .Append(", paragraph keys=").Append(paragraph.Count).Append(", document lines=").Append(editor.Document.LineCount).Append('\n');
        } catch (Exception ex) {check("long session case exception",false,ex.ToString());}
        finally {frames.Enabled=false;}
        File.WriteAllText(Path.Combine(Path.GetFullPath(outDir),"live-typing-long.tsv"),timings.ToString(),new UTF8Encoding(false));
    }
    // ~200 generated lines mixing every block the live surface formats. No external files.
    static string LongFixture() {
        var text=new StringBuilder();
        text.Append("# 장문 세션 점검\n\n");
        text.Append("이 검사는 긴 세션에서 입력이 느려지는지 확인합니다. ");
        text.Append(Repeat("가나다라마바사 아자차카타파하 ",4));
        text.Append("\n\n");
        for (int block=0;block<9;block++) {
            text.Append("## 구역 ").Append(block+1).Append("\n\n");
            text.Append(Repeat("문단 내용 ",16)).Append("\n\n");
            text.Append("- 목록 첫째 ").Append(block).Append('\n');
            text.Append("- 목록 둘째 ").Append(block).Append('\n');
            text.Append("- 목록 셋째 ").Append(block).Append("\n\n");
            text.Append("1. 번호 첫째 ").Append(block).Append('\n');
            text.Append("2. 번호 둘째 ").Append(block).Append("\n\n");
            text.Append("| 제목 가 | 제목 나 | 제목 다 | 제목 라 |\n");
            text.Append("|---|---|---|---|\n");
            text.Append("| 셀 ").Append(block).Append(" 가 | 셀 ").Append(block).Append(" 나 | 셀 ").Append(block).Append(" 다 | 셀 ").Append(block).Append(" 라 |\n");
            text.Append("| 아래 | 행 | 유지 | 확인 |\n\n");
            text.Append("```cs\nint block = ").Append(block).Append(";\n```\n\n");
            text.Append("[링크 ").Append(block).Append("](https://example.com/page/").Append(block).Append(")와 본문 문장이 이어집니다.\n\n");
        }
        text.Append("마지막 문단 시작\n");
        return text.ToString();
    }
    // First untouched data row of the first table: starts with "| ", ends with "|", and sits
    // directly under a delimiter row. A row split by Enter no longer matches this shape. skip
    // lets later table visits use another intact block instead of growing one cell forever.
    static int FindTableCaret(SourceEditor editor,MarkdownLivePreview.Session session,int skip) {
        TextDocument document=editor.Document;int found=0;
        for (int number=1;number<=document.LineCount;number++) {
            DocumentLine line=document.GetLineByNumber(number);string text=document.GetText(line);
            if (text.Length<12||text[0]!='|'||text[text.Length-1]!='|'||text.IndexOf("셀 ",StringComparison.Ordinal)<0) continue;
            DocumentLine previous=line.PreviousLine;
            if (previous==null||document.GetText(previous).IndexOf("---",StringComparison.Ordinal)<0) continue;
            MarkdownLivePreview.LineInfo info=session.Info(line);
            if (!info.Table||info.Delimiter||info.Cells==null||info.Cells.Count<4) continue;
            if (found<skip) {found++;continue;}
            return line.Offset+info.Cells[0].End;
        }
        return -1;
    }
    static int FindListCaret(SourceEditor editor) {
        TextDocument document=editor.Document;
        for (int number=1;number<=document.LineCount;number++) {
            DocumentLine line=document.GetLineByNumber(number);string text=document.GetText(line);
            if (text.Length<4||text[0]!='-'||text[1]!=' ') continue;
            return line.EndOffset;
        }
        return -1;
    }
    // 0 paragraph, 1 table row, 2 list item, 3 structural (heading/quote/fence). Text only so
    // the per-key path never parses the line through the session. An empty line is paragraph.
    static int CaretKind(SourceEditor editor) {
        TextDocument document=editor.Document;
        int offset=Math.Min(Math.Max(0,editor.CaretIndex),document.TextLength);
        return LineKind(document.GetText(document.GetLineByOffset(offset)));
    }
    static int LineKind(string text) {
        int p=0;while (p<text.Length&&(text[p]==' '||text[p]=='\t')) p++;
        if (p>=text.Length) return 0;
        if (text[p]=='|') return 1;
        if ((text[p]=='-'||text[p]=='*'||text[p]=='+')&&p+1<text.Length&&text[p+1]==' ') return 2;
        if (text[p]=='#'||text[p]=='>') return 3;
        if (p+2<text.Length&&text[p]=='`'&&text[p+1]=='`'&&text[p+2]=='`') return 3;
        int digits=p;while (digits<text.Length&&digits-p<9&&text[digits]>='0'&&text[digits]<='9') digits++;
        if (digits>p&&digits+1<text.Length&&(text[digits]=='.'||text[digits]==')')&&text[digits+1]==' ') return 2;
        return 0;
    }
    // Same command MainWindow.SourceStructuralKeys runs for Enter. Applying the CommandResult in
    // list order (descending offsets) is what ApplyCommandResult does, without the history glue;
    // this standalone probe has no MainWindow key handler to raise the event on.
    static void PressEnter(SourceEditor editor,TextDocument expected,ref int mirror) {
        CommandResult result=MarkdownStructuralInput.Enter(editor.Document,editor.SelectionStart,editor.SelectionLength);
        if (result!=null) {ApplyStructural(editor,expected,result,ref mirror);return;}
        // The handler returns null outside a list, so AvalonEdit's own input path inserts the
        // newline. Copy what it actually inserted (a strategy may add indentation).
        int caret=editor.CaretIndex,before=editor.Document.TextLength;
        editor.TextArea.PerformTextInput("\n");
        int added=editor.Document.TextLength-before;
        if (added>0) {string inserted=editor.Document.GetText(caret,added);expected.Insert(mirror,inserted);mirror+=added;}
    }
    static void PressBackspace(SourceEditor editor,TextDocument expected,ref int mirror) {
        CommandResult result=MarkdownStructuralInput.Backspace(editor.Document,editor.CaretIndex);
        if (result!=null) {ApplyStructural(editor,expected,result,ref mirror);return;}
        TextDocument document=editor.Document;int caret=editor.CaretIndex;
        if (caret<=0) {mirror=0;editor.Select(0,0);return;}
        DocumentLine line=document.GetLineByOffset(caret);int remove=1;
        if (caret==line.Offset) {
            if (line.PreviousLine==null) {mirror=0;editor.Select(0,0);return;}
            remove=line.PreviousLine.DelimiterLength;
        }
        if (remove<=0) {mirror=caret;editor.Select(caret,0);return;}
        document.Remove(caret-remove,remove);expected.Remove(caret-remove,remove);
        mirror=caret-remove;editor.Select(mirror,0);
    }
    static void ApplyStructural(SourceEditor editor,TextDocument expected,CommandResult result,ref int mirror) {
        // ApplyCommandResult wraps the same parts in one document update; do that here so the
        // live surface sees one change, not one per renumber edit.
        editor.Document.BeginUpdate();
        try {
            for (int e=0;e<result.Edits.Count;e++) {
                SourceEdit edit=result.Edits[e];
                editor.Document.Replace(edit.Offset,edit.Length,edit.Text);
                expected.Replace(edit.Offset,edit.Length,edit.Text);
            }
        } finally {editor.Document.EndUpdate();}
        int start=Math.Max(0,Math.Min(result.SelectionStart,editor.Document.TextLength));
        int length=Math.Max(0,Math.Min(result.SelectionLength,editor.Document.TextLength-start));
        editor.Select(start,length);mirror=start;
    }
    // The complaint is about continuing growth, so a cache may fill and plateau but must not
    // keep adding entries late in the session: the last 1000 keys may add at most LongGrowth[c]
    // (character budgets are bounded only by the absolute cap, because one idle measurement of
    // a large cell legitimately adds its whole text). Subscriptions must be exactly constant and
    // the composition queue at most one slot.
    static void VerifyLongCounts(string name,List<int[]> counts,Action<string,bool,string> check) {
        if (counts.Count==0) return;
        int window=Math.Min(1000,counts.Count);
        for (int c=0;c<CountNames.Length;c++) {
            int first=counts[0][c],min=first,max=first;
            for (int key=1;key<counts.Count;key++) {min=Math.Min(min,counts[key][c]);max=Math.Max(max,counts[key][c]);}
            bool subscriptions=c>=7&&c<=10||c>=14;
            int start=counts[Math.Max(0,counts.Count-1-window)][c],end=counts[counts.Count-1][c];
            int windowGrowth=end-start;bool ok;string detail;
            if (subscriptions) {ok=first>=0&&min==max;detail="first="+first+", min="+min+", max="+max;}
            else if (c==11) {ok=first>=0&&max<=1;detail="first="+first+", max="+max;}
            else {ok=first>=0&&max<=first+LongCaps[c]&&windowGrowth<=LongGrowth[c];detail="first="+first+", min="+min+", max="+max+", last "+window+" growth="+windowGrowth+", cap="+LongCaps[c];}
            check(name+" bounded "+CountNames[c],ok,detail);
        }
    }
    // Forced collections (no finalizer wait: this runs on the dispatcher thread).
    static long ManagedBytes() {GC.Collect();GC.Collect();return GC.GetTotalMemory(true);}
    static string FirstDifference(string actual,string expected) {
        int limit=Math.Min(actual.Length,expected.Length),p=0;
        while (p<limit&&actual[p]==expected[p]) p++;
        if (p==limit&&actual.Length==expected.Length) return "";
        return ", first difference at "+p;
    }
    static MarkdownLivePreview.Session Attach(SourceEditor editor) {
        return MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
    }
    static void Compose(SourceEditor editor,string text) {
        editor.SimulateImeComposition();
        var composition=new TextComposition(InputManager.Current,editor.TextArea,text);
        editor.TextArea.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,composition) {RoutedEvent=TextCompositionManager.PreviewTextInputStartEvent});
        editor.TextArea.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice,composition) {RoutedEvent=TextCompositionManager.PreviewTextInputUpdateEvent});
        editor.SimulateImeCommit(text.Length);editor.TextArea.PerformTextInput(text);editor.ResetCompositionSimulation();
    }
    static int[] Counts(SourceEditor editor,MarkdownLivePreview.Session session) {
        int[] product=session.StabilityCounts(),result=new int[CountNames.Length];Array.Copy(product,result,product.Length);
        var native=AllLinesField==null ? null : AllLinesField.GetValue(editor.TextArea.TextView) as System.Collections.ICollection;
        result[13]=native==null ? -1 : native.Count;
        result[14]=Handlers(editor.Document,"Changing");result[15]=Handlers(editor.Document,"Changed");
        result[16]=Handlers(editor.TextArea.Caret,"PositionChanged");result[17]=Handlers(editor.TextArea,"SelectionChanged");return result;
    }
    static int Handlers(object owner,string name) {
        FieldInfo field=owner.GetType().GetField(name,Fields);if (field==null) return -1;
        var handlers=field.GetValue(owner) as Delegate;return handlers==null ? 0 : handlers.GetInvocationList().Length;
    }
    static void VerifyCounts(string name,List<int[]> counts,Action<string,bool,string> check) {
        if (counts.Count==0) return;
        for (int c=0;c<CountNames.Length;c++) {
            int first=counts[0][c],min=first,max=first,growth=0;
            for (int key=1;key<counts.Count;key++) {min=Math.Min(min,counts[key][c]);max=Math.Max(max,counts[key][c]);if (counts[key][c]>counts[key-1][c]) growth++;}
            // Detect linear prefix accumulation even if it eventually hits a budget and
            // plateaus (b49's text-keyed metric/shape caches did exactly that).
            bool subscriptions=c>=7 && c<=10 || c>=14;
            bool ok=first>=0 && (subscriptions ? max==min : !(growth>=16 && max-min>=16));
            check(name+" bounded "+CountNames[c],ok,"first="+first+", min="+min+", max="+max+", growth keys="+growth);
        }
    }
    static Sample Capture(SourceEditor editor) {
        var view=editor.TextArea.TextView;var caret=editor.TextArea.Caret;var sample=new Sample {Offset=caret.Offset,Widths=new double[0]};
        VisualLine visual=view.GetVisualLine(caret.Line);
        if (visual==null || visual.IsDisposed || !view.VisualLinesValid || !view.IsArrangeValid) return sample;
        int column=visual.GetVisualColumn(caret.Offset-visual.FirstDocumentLine.Offset);var row=visual.GetTextLine(column);
        sample.Top=visual.VisualTop-view.VerticalOffset;sample.Height=visual.Height;
        sample.RowTop=visual.GetTextLineVisualYPosition(row,VisualYPosition.LineTop)-view.VerticalOffset;sample.RowHeight=row.Height;
        var drawing=DrawingField==null ? null : DrawingField.GetValue(visual) as DrawingVisual;
        if (drawing==null || !view.IsAncestorOf(drawing)) return sample;
        sample.DrawnTop=drawing.TransformToAncestor(view).Transform(new Point()).Y;
        FieldInfo height=drawing.GetType().GetField("Height",Fields);if (height==null) return sample;
        sample.DrawnHeight=(double)height.GetValue(drawing);
        foreach (VisualLineElement element in visual.Elements) {
            var strut=element as MarkdownLivePreview.StrutElement;
            if (strut!=null && strut.Layout!=null) {sample.Widths=(double[])strut.Layout.Widths.Clone();break;}
        }
        Typeface face;double em;Point composition=LiveCompositionGeometry.Position(editor,out em,out face);
        sample.CompositionTop=composition.Y;sample.Em=em;sample.Valid=true;return sample;
    }
    static double Difference(Sample a,Sample b) {
        if (!a.Valid || !b.Valid || a.Offset!=b.Offset || a.Widths.Length!=b.Widths.Length) return Double.PositiveInfinity;
        double delta=0;
        double[] left={a.Top,a.Height,a.RowTop,a.RowHeight,a.DrawnTop,a.DrawnHeight,a.CompositionTop,a.Em};
        double[] right={b.Top,b.Height,b.RowTop,b.RowHeight,b.DrawnTop,b.DrawnHeight,b.CompositionTop,b.Em};
        for (int p=0;p<left.Length;p++) {if (Double.IsNaN(left[p]) || Double.IsNaN(right[p])) return Double.PositiveInfinity;delta=Math.Max(delta,Math.Abs(left[p]-right[p]));}
        for (int p=0;p<a.Widths.Length;p++) delta=Math.Max(delta,Math.Abs(a.Widths[p]-b.Widths[p]));return delta;
    }
    static string Describe(Sample s) {return "valid="+s.Valid+", offset="+s.Offset+", top="+Number(s.Top)+", height="+Number(s.Height)+", drawn top="+Number(s.DrawnTop)+", drawn height="+Number(s.DrawnHeight)+", widths="+s.Widths.Length;}
    static bool Close(double a,double b) {return !Double.IsNaN(a) && !Double.IsNaN(b) && Math.Abs(a-b)<.01;}
    static string Number(double value) {return value.ToString("F4",CultureInfo.InvariantCulture);}
    static double Mean(double[] values,int start,int length) {double sum=0;for (int p=start;p<start+length;p++) sum+=values[p];return sum/length;}
    static string Join(int[] values) {var text=new StringBuilder();for (int p=0;p<values.Length;p++) {if (p>0) text.Append(',');text.Append(values[p]);}return text.ToString();}
    static string Repeat(string value,int count) {var text=new StringBuilder();for (int p=0;p<count;p++) text.Append(value);return text.ToString();}
    static bool Drain(DispatcherPriority priority,int timeout) {
        bool reached=false;var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Send) {Interval=TimeSpan.FromMilliseconds(timeout)};
        EventHandler expired=delegate {timer.Stop();frame.Continue=false;};timer.Tick+=expired;timer.Start();
        DispatcherOperation marker=Dispatcher.CurrentDispatcher.BeginInvoke(priority,new Action(delegate {reached=true;frame.Continue=false;}));
        try {Dispatcher.PushFrame(frame);} finally {timer.Stop();timer.Tick-=expired;if (!reached) marker.Abort();}return reached;
    }
    static bool AwaitFrame(Frames frames,int timeout) {
        int before=frames.Total;var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Send) {Interval=TimeSpan.FromMilliseconds(timeout)};
        EventHandler rendering=delegate {if (frames.Total>before) frame.Continue=false;};
        EventHandler expired=delegate {frame.Continue=false;};timer.Tick+=expired;CompositionTarget.Rendering+=rendering;timer.Start();
        try {Dispatcher.PushFrame(frame);} finally {timer.Stop();timer.Tick-=expired;CompositionTarget.Rendering-=rendering;}return frames.Total>before;
    }
    static void Pump() {if (!Drain(DispatcherPriority.ApplicationIdle,2000)) throw new InvalidOperationException("Dispatcher did not become idle");}
    static void Wait(int milliseconds) {
        var frame=new DispatcherFrame();var timer=new DispatcherTimer(DispatcherPriority.Background) {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        EventHandler tick=delegate {timer.Stop();frame.Continue=false;};timer.Tick+=tick;timer.Start();
        try {Dispatcher.PushFrame(frame);} finally {timer.Stop();timer.Tick-=tick;}Pump();
    }
    // b52 (user feedback 9): typing Korean into a live table cell must (a) widen the column
    // per character instead of wrapping into a tall narrow cell, (b) keep the cell on one
    // visual line until the column reaches its cap, (c) never wrap and unwrap between keys,
    // and (d) draw the composition at the real insertion point (inside the cell, on the same
    // text row as the caret). Composition is simulated through the existing seams.
    static void VerifyCellGrowthAndComposition(Window window,SourceEditor editor,ref MarkdownLivePreview.Session session,Action<string,bool,string> check) {
        const int Keys=20;
        if (session!=null) {session.Dispose();session=null;}
        window.Width=1600;window.UpdateLayout();Pump();
        session=Attach(editor);
        // A brand-new table with the insert command's own short headers.
        editor.Text="앞 문단\n\n";editor.CaretIndex=editor.Document.TextLength;
        MarkdownLivePreview.Session live=session;Pump();
        // The product's own insert command, applied the way the toolbar applies it.
        CommandResult result=MarkdownCommands.InsertTable(editor.Document,editor.CaretIndex,2,3);
        if (result!=null) {
            editor.Document.BeginUpdate();
            try { for (int e=0;e<result.Edits.Count;e++) editor.Document.Replace(result.Edits[e].Offset,result.Edits[e].Length,result.Edits[e].Text); }
            finally { editor.Document.EndUpdate(); }
            editor.Select(result.SelectionStart,0);
        }
        Pump();
        DocumentLine row=null;
        for (int n=1;n<=editor.Document.LineCount;n++) {
            MarkdownLivePreview.LineInfo info=live.Info(editor.Document.GetLineByNumber(n));
            if (info.Table && !info.Delimiter && info.Cells!=null && info.Cells.Count>=3 && info.Row!=null) { row=editor.Document.GetLineByNumber(n);break; }
        }
        if (row==null) { check("b52 new table has a data row",false,"no data row in "+editor.Document.LineCount+" lines");return; }
        double cap=MarkdownLivePreview.ColumnCap(live.View.ActualWidth,live.Info(row).Cells.Count);
        double startWidth=live.Info(row).Row.Widths[0];
        check("b52 new live table column floor",startWidth>=MarkdownLivePreview.ColumnFloor(live.View.ActualWidth,live.Info(row).Cells.Count)-.75,
            "width="+N(startWidth)+", floor="+N(MarkdownLivePreview.ColumnFloor(live.View.ActualWidth,live.Info(row).Cells.Count)));
        int cellEnd=row.Offset+live.Info(row).Cells[0].End;
        editor.Select(cellEnd,0);editor.TextArea.Caret.BringCaretToView();Pump();
        var widths=new List<double>();var rows=new List<int>();
        int compositionBad=0,baselineBad=0,insideBad=0,caretBad=0;string firstBad=null,firstCaretBad=null;
        for (int key=0;key<Keys;key++) {
            Compose(editor,((char)(0xAC00+(key*11)%11172)).ToString());
            Pump();
            DocumentLine now=editor.Document.GetLineByNumber(row.LineNumber);
            MarkdownLivePreview.LineInfo info=live.Info(now);
            if (info.Row==null) { check("b52 typing keeps the live row",false,"row missing at key "+key);return; }
            widths.Add(info.Row.Widths[0]);rows.Add(info.Row.PhysicalRows);
            // Two independent oracles, neither of which reads the caret's own VisualColumn:
            // 1) the composition point must agree with the rectangle the caret is drawn at;
            // 2) it must agree with the x/row computed from the EXPECTED logical position
            //    (cell index + text index in that cell) through that cell's own slice.
            double size;Typeface face;
            Point composition=LiveCompositionGeometry.Position(editor,out size,out face);
            VisualLine visual=live.View.GetVisualLine(now.LineNumber);
            if (visual==null || Double.IsNaN(composition.X)) { compositionBad++;continue; }
            Rect rectangle=editor.TextArea.Caret.CalculateCaretRectangle();
            if (rectangle.IsEmpty) { compositionBad++;continue; }
            double rectangleTop=rectangle.Y-live.View.VerticalOffset,rectangleBottom=rectangleTop+rectangle.Height;
            bool sameRow=composition.Y>=rectangleTop-1.5 && composition.Y<rectangleBottom-1.5;
            if (!sameRow) { baselineBad++;if (firstBad==null) firstBad="key="+key+" compY="+N(composition.Y)+" caret="+N(rectangleTop)+".."+N(rectangleBottom)+" widths="+N(info.Row.Widths[0]); }
            if (Math.Abs(composition.X-(rectangle.X-live.View.HorizontalOffset))>1) {
                caretBad++;
                if (firstCaretBad==null) firstCaretBad="key="+key+" compX="+N(composition.X)+" caretX="+N(rectangle.X-live.View.HorizontalOffset);
            }
            double expectedX,expectedTop,expectedBottom;
            if (ExpectedCellPoint(live,now,0,editor.CaretIndex,false,out expectedX,out expectedTop,out expectedBottom)) {
                if (Math.Abs(composition.X-expectedX)>1) {
                    caretBad++;
                    if (firstCaretBad==null) {
                        var element=live.RowElement(visual);
                        int relOffset=editor.CaretIndex-now.Offset;
                        int canonical=element==null ? -1 : element.StartColumn(relOffset);
                        var text=info.Row.Texts[0];
                        int textIndex=-1;
                        if (text!=null) for (int p=0;p<text.Map.Length;p++) if (text.Map[p]==relOffset) { textIndex=p;break; }
                        var sliceColumns=live.Info(now).Row.Slices[0];
                        firstCaretBad="key="+key+" compX="+N(composition.X)+" expectedX="+N(expectedX)+" caretX="+N(rectangle.X-live.View.HorizontalOffset)+
                            " offset="+relOffset+" caretVC="+editor.TextArea.Caret.Position.VisualColumn+" canonicalVC="+canonical+
                            " textIndex="+textIndex+" slices="+(sliceColumns==null?0:sliceColumns.Count)+
                            " cellEnd="+info.Cells[0].End+" elem=["+(element==null?"-":element.DebugMap())+"]";
                    }
                }
                if (composition.Y<expectedTop-1.5 || composition.Y>=expectedBottom-1.5) {
                    baselineBad++;
                    if (firstBad==null) firstBad="key="+key+" compY="+N(composition.Y)+" expected row="+N(expectedTop)+".."+N(expectedBottom);
                }
            }
            // Left edge of the typed text of this cell, and its right edge: the composition
            // must be drawn within the cell's own column band.
            Rect box=live.TableCellBox(visual,0);
            if (!box.IsEmpty && (composition.X<box.Left-1 || composition.X>box.Right+1)) insideBad++;
        }
        bool monotonic=true,oscillating=false,single=true;
        for (int p=1;p<widths.Count;p++) if (widths[p]+.5<widths[p-1]) monotonic=false;
        for (int p=0;p<rows.Count;p++) {
            if (rows[p]<=1) continue;
            if (widths[p]<cap-1.5) single=false;
            if (p+1<rows.Count && rows[p+1]<rows[p]) oscillating=true;
        }
        check("b52 live typed column widths grow monotonically",monotonic&&widths.Count==Keys,"widths="+JoinDoubles(widths.ToArray()));
        check("b52 live typing grows the column",widths.Count==Keys&&widths[widths.Count-1]>startWidth+.5,
            "before="+N(startWidth)+", after="+(widths.Count==0 ? "-" : N(widths[widths.Count-1]))+", cap="+N(cap));
        check("b52 live typed cell keeps one line until the column cap",single,
            "rows="+JoinInts(rows)+", widths="+JoinDoubles(widths.ToArray())+", cap="+N(cap));
        check("b52 live typed cell never wraps and unwraps between keys",!oscillating,"rows="+JoinInts(rows));
        check("b52 live composition stays on the caret text row",baselineBad==0&&compositionBad==0,
            "off-row keys="+baselineBad+", unmeasurable="+compositionBad+(firstBad==null ? "" : ", first "+firstBad));
        check("b52 live composition x matches the caret and the cell's own slice",caretBad==0,
            "mismatched keys="+caretBad+(firstCaretBad==null ? "" : ", first "+firstCaretBad));
        check("b52 live composition stays inside the typed cell",insideBad==0,"outside keys="+insideBad);
        if (session!=null) {session.Dispose();session=null;}
        window.Width=1920;window.UpdateLayout();Pump();
    }
    static string N(double value) {return value.ToString("0.##",CultureInfo.InvariantCulture);}    static string JoinInts(List<int> values) {var text=new StringBuilder();for (int p=0;p<values.Count;p++) {if (p>0) text.Append(',');text.Append(values[p]);}return text.ToString();}
    // The expected composition point from the LOGICAL position, not from the caret's visual
    // column or the Reverse map: the cell index and the text index inside that cell pick the
    // cell's own slice, and that slice's TextLine measures x and its vertical span. The caller
    // passes the side of a soft-wrap boundary through atWrapEnd.
    static bool ExpectedCellPoint(MarkdownLivePreview.Session live,DocumentLine line,int cell,int offset,bool atWrapEnd,out double x,out double top,out double bottom) {
        x=Double.NaN;top=Double.NaN;bottom=Double.NaN;
        MarkdownLivePreview.LineInfo info=live.Info(line);
        if (info.Row==null || info.Cells==null || cell<0 || cell>=info.Cells.Count) return false;
        var text=info.Row.Texts[cell];
        if (text==null) return false;
        int source=offset-line.Offset;
        // CellText indices are their own coordinate space: the projection column -> offset map
        // is NOT it. Scan the cell text map and keep the index that actually sits in a slice.
        int index=-1;
        for (int p=0;p<text.Map.Length;p++) {
            if (text.Map[p]!=source) continue;
            if (!InAnySlice(info.Row.Slices[cell],p)) continue;
            if (index<0) index=p;
            bool sliceEnd=IsSliceEnd(info.Row.Slices[cell],p);
            if (atWrapEnd ? sliceEnd : !sliceEnd) { index=p;break; }
        }
        if (index<0) return false;
        return live.CellGeometry(line,cell,index,atWrapEnd,out x,out top,out bottom);
    }
    static bool InAnySlice(System.Collections.Generic.List<MarkdownLivePreview.CellSlice> slices,int index) {
        if (slices==null) return false;
        foreach (var slice in slices) if (index>=slice.Start && index<=slice.Start+slice.Length) return true;
        return false;
    }
    static bool IsSliceEnd(System.Collections.Generic.List<MarkdownLivePreview.CellSlice> slices,int index) {
        if (slices==null) return false;
        foreach (var slice in slices) if (slice.Length>0 && index==slice.Start+slice.Length) return true;
        return false;
    }
    static string JoinDoubles(double[] values) {var text=new StringBuilder();for (int p=0;p<values.Length;p++) {if (p>0) text.Append(',');text.Append(N(values[p]));}return text.ToString();}
    // Task 3: a click inside a cell's own rectangle (also right of its text, and right of the
    // whole table) places the caret in that cell; typing then stays in the cell and keeps the
    // row's pipe structure. One window width per fullscreen case the user reported.
    static void VerifyClicks(Window window,SourceEditor editor,ref MarkdownLivePreview.Session session,Action<string,bool,string> check) {
        string fixture="before\n\n| 첫째 | 둘째 | 셋째 | 넷째 |\n|---|---|---|---|\n| "+
            Repeat("긴 첫째 칸 문장이 열 폭을 아주 넓게 만듭니다 ",4)+" | "+Repeat("이웃 셀 긴 문장 ",5)+" | x | 끝 |\n| 짧음 | 둘 | 셋 | 넷 |\n| 하나 | 둘 | 셋 | "+
            Repeat("넷째 칸 긴 문장 ",5)+" |\n\nafter\n";
        int[] widths={1280,1920,2560};
        for (int w=0;w<widths.Length;w++) {
            if (session!=null) {session.Dispose();session=null;}
            window.Width=widths[w];window.UpdateLayout();Pump();
            session=Attach(editor);editor.Text=fixture;editor.CaretIndex=0;
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            for (int number=3;number<=7;number++) {
                try {
                DocumentLine line=editor.Document.GetLineByNumber(number);
                MarkdownLivePreview.LineInfo info=session.Info(line);
                if (info.Delimiter || !info.Table || info.Cells==null || info.Cells.Count==0) continue;
                for (int c=0;c<info.Cells.Count;c++) {
                    SafeClick(editor,session,number,c,0.5,false,widths[w],check);
                    SafeClick(editor,session,number,c,0.94,false,widths[w],check);
                }
                SafeClick(editor,session,number,info.Cells.Count-1,0,true,widths[w],check);
                } catch (Exception ex) {check("table row "+number+" w="+widths[w]+" exception",false,ex.ToString());}
            }
        }
        if (session!=null) {session.Dispose();session=null;}
        window.Width=1920;window.UpdateLayout();Pump();
    }
    // One throwing click must not hide the remaining clicks or the <br> cases.
    static void SafeClick(SourceEditor editor,MarkdownLivePreview.Session session,int number,int cell,double xFraction,bool rightOfTable,int width,Action<string,bool,string> check) {
        try { ClickCell(editor,session,number,cell,xFraction,rightOfTable,width,check); }
        catch (Exception ex) { check("table cell click w="+width+" row "+number+" cell "+cell+" exception",false,ex.ToString()); }
    }
    static void ClickCell(SourceEditor editor,MarkdownLivePreview.Session session,int number,int cell,double xFraction,bool rightOfTable,int width,Action<string,bool,string> check) {
        TextDocument document=editor.Document;
        if (number<1 || number>document.LineCount) return;
        DocumentLine line=document.GetLineByNumber(number);
        // Park the caret outside the table so this row keeps its formatted layout.
        editor.Select(document.GetLineByNumber(1).Offset,0);Pump();
        var view=editor.TextArea.TextView;
        VisualLine visual=view.GetOrConstructVisualLine(line);Pump();
        visual=view.GetVisualLine(number);
        MarkdownLivePreview.LineInfo info=session.Info(line);
        if (visual==null || !info.Table || info.Delimiter || info.Cells==null || cell>=info.Cells.Count) return;
        Rect box=session.TableCellBox(visual,cell);
        string label="table cell click w="+width+" row "+number+" cell "+cell+(rightOfTable ? " right of table" : " x="+xFraction.ToString("F2",CultureInfo.InvariantCulture));
        if (box.IsEmpty || box.Width<3 || box.Height<3) {check(label,false,"missing cell rectangle "+box);return;}
        double x=rightOfTable ? box.Right+Math.Min(60,Math.Max(8,box.Width*.25)) : box.Left+Math.Min(box.Width-2,Math.Max(2,box.Width*xFraction));
        double y=box.Top+Math.Min(box.Height-2,Math.Max(2,box.Height*.5));
        string before=document.GetText(line);MarkdownLivePreview.Cell range=info.Cells[cell];
        int start=line.Offset+range.Start,end=line.Offset+range.End;
        bool placed=session.PlaceTableCaret(new Point(x,y),false);
        int caret=editor.CaretIndex;
        bool inside=placed && caret>=start && caret<=end && (!rightOfTable || caret==end);
        check(label+" caret inside cell",inside,"handled="+placed+", caret="+caret+", cell="+start+".."+end+", width="+width);
        if (!inside) return;
        editor.TextArea.PerformTextInput("가");Pump();
        string after=document.GetText(line);
        int inserted=-1;
        if (after.Length==before.Length+1) {
            for (int p=0;p<before.Length;p++) if (after[p]!=before[p]) {inserted=p;break;}
            if (inserted<0) inserted=before.Length;
        }
        bool same=after.Length==before.Length+1 && inserted>=0 && after==before.Insert(inserted,"가") && Pipes(after)==Pipes(before);
        bool inCell=inserted>=range.Start && inserted<=range.End;
        check(label+" typing stays in the cell",same && inCell,
            "inserted="+inserted+", cell="+range.Start+".."+range.End+", pipes="+Pipes(after)+"/"+Pipes(before)+", line="+(after.Length<=120 ? after : after.Substring(0,120)));
        if (inserted>=0 && after.Length==before.Length+1 && after==before.Insert(inserted,"가")) document.Remove(inserted,1);
        editor.Select(document.GetLineByNumber(1).Offset,0);Pump();
    }
    static int Pipes(string text) {
        int count=0;for (int p=0;p<text.Length;p++) if (text[p]=='|') count++;return count;
    }
    // A click near the right end of a cell whose text is "end테스트" must land at that cell's
    // end, and composing there must leave the syllable AT the cell end (first jamo) and after it
    // (second jamo). The old behaviour put the first syllable at the cell start, because the
    // commit used a stale remembered offset. Also done on a cell that wraps to three rows.
    static void VerifyClickAtEndAndCompose(Window window,SourceEditor editor,ref MarkdownLivePreview.Session session,Action<string,bool,string> check) {
        if (session!=null) {session.Dispose();session=null;}
        window.Width=1280;window.UpdateLayout();Pump();
        string wrapped=Repeat("줄바꿈 되는 아주 긴 내용이 계속 이어지는 셀입니다 ",3);
        string fixture="before\n\n| 머리 | 둘째 |\n|---|---|\n| end테스트 | "+wrapped.Trim()+" |\n\nafter\n";
        session=Attach(editor);editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        TextDocument document=editor.Document;
        DocumentLine row=document.GetLineByNumber(5);
        for (int cell=0;cell<2;cell++) {
            editor.Select(document.GetLineByNumber(1).Offset,0);Pump();
            VisualLine visual=session.View.GetOrConstructVisualLine(row);Pump();
            visual=session.View.GetVisualLine(row.LineNumber);
            MarkdownLivePreview.LineInfo info=session.Info(row);
            if (visual==null || info.Cells==null || cell>=info.Cells.Count) {check("end-compose cell "+cell+" available",false,"row missing");continue;}
            MarkdownLivePreview.Cell range=info.Cells[cell];
            int cellEnd=row.Offset+range.End;
            Rect box=session.TableCellBox(visual,cell);
            if (box.IsEmpty || box.Width<6) {check("end-compose cell "+cell+" box",false,"box="+box);continue;}
            // Just inside the cell's right edge: the click must resolve to the cell's text end.
            Point point=new Point(box.Right-Math.Min(3,box.Width/4),box.Top+Math.Min(box.Height-2,box.Height*.5));
            bool placed=session.PlaceTableCaret(point,false);
            check("end-compose cell "+cell+" click at end",placed&&editor.CaretIndex==cellEnd,
                "handled="+placed+", caret="+editor.CaretIndex+", expected="+cellEnd+", rows="+(info.Row==null?0:info.Row.PhysicalRows)+", box="+box);
            if (!placed||editor.CaretIndex!=cellEnd) continue;
            string rowBefore=document.GetText(row);
            int columns=MarkdownLivePreview.SplitCells(rowBefore).Count;
            Compose(editor,"가");Pump();
            string actual=document.GetText(document.GetLineByNumber(row.LineNumber));
            bool firstAtEnd=actual.Length==rowBefore.Length+1 && actual.Substring(0,range.End)==rowBefore.Substring(0,range.End) && actual[range.End]=='가';
            check("end-compose cell "+cell+" first jamo at cell end",firstAtEnd,
                "cell end="+range.End+", line=["+(actual.Length<=140 ? actual : actual.Substring(0,140))+"]");
            Compose(editor,"각");Pump();
            actual=document.GetText(document.GetLineByNumber(row.LineNumber));
            MarkdownLivePreview.LineInfo afterInfo=session.Info(document.GetLineByNumber(row.LineNumber));
            int at=actual.IndexOf("가각",range.Start,StringComparison.Ordinal);
            int expectedAt=range.End;
            bool secondAfter=at==expectedAt&&afterInfo.Cells!=null&&afterInfo.Cells.Count>cell&&at<=afterInfo.Cells[cell].End;
            int columnsAfter=MarkdownLivePreview.SplitCells(actual).Count;
            check("end-compose cell "+cell+" second jamo follows inside the cell",secondAfter&&columnsAfter==columns,
                "index="+at+", expected="+expectedAt+", cell="+range.Start+".."+range.End+", columns "+columns+"->"+columnsAfter+", line=["+(actual.Length<=140 ? actual : actual.Substring(0,140))+"]");
            // Reset the source rather than relying on undo: the probe has no window history.
            editor.Text=fixture;editor.CaretIndex=0;
            editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
            row=editor.Document.GetLineByNumber(5);
        }
        window.Width=1920;window.UpdateLayout();Pump();
        if (session!=null) {session.Dispose();session=null;}
    }
    // Logical -> visual -> logical over real caret stops: every offset of every cell of a
    // wrapped row must return to itself, whatever physical row it lands on. This replaces the
    // old "Reverse[o] row == slice containing o" check, which only restated the projection.
    static void VerifyCaretRoundTrip(Window window,SourceEditor editor,ref MarkdownLivePreview.Session session,Action<string,bool,string> check) {
        if (session!=null) {session.Dispose();session=null;}
        window.Width=1000;window.UpdateLayout();Pump();
        string fixture="before\n\n| 첫머리 칸 | 둘째 칸 | 셋째 칸 |\n|---|---|---|\n| "+
            Repeat("감싸지는 첫 번째 셀 내용 ",4)+" | 짧은 둘 | "+Repeat("감싸지는 셋째 셀 ",4)+" |\n\n| 단일 | 행 |\n|---|---|\n\nempty\n\n| a |  |\n|---|---|\n\nlast\n";
        session=Attach(editor);editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        TextDocument document=editor.Document;
        int wrapped=0;
        for (int number=1;number<=document.LineCount;number++) {
            DocumentLine line=document.GetLineByNumber(number);
            MarkdownLivePreview.LineInfo info=session.Info(line);
            if (!info.Table||info.Delimiter||info.Cells==null||info.Cells.Count==0) continue;
            string detail;
            bool ok=session.CaretRoundTrip(line,out detail);
            if (info.Row!=null&&info.Row.PhysicalRows>1) wrapped++;
            check("caret round trip row "+number+(info.Row!=null?" rows="+info.Row.PhysicalRows:""),ok,detail==null?"all cell offsets return to themselves":detail);
        }
        check("caret round trip covers a wrapped row",wrapped>0,"wrapped rows="+wrapped);
        window.Width=1920;window.UpdateLayout();Pump();
        if (session!=null) {session.Dispose();session=null;}
    }
    // Arrow keys inside a wrapped row must stay in the caret's cell: Left/Right walk that
    // cell's document offsets (hops only at cell boundaries), Up/Down move between that cell's
    // own slices. Home/End stay in the cell, and a selection across cells must not delete '|'.
    static void VerifyTableNavigation(Window window,SourceEditor editor,ref MarkdownLivePreview.Session session,Action<string,bool,string> check) {
        if (session!=null) {session.Dispose();session=null;}
        window.Width=560;window.UpdateLayout();Pump();
        // The first cell must wrap to several rows for the Up/Down part, so it carries a long
        // sentence and the window is narrow enough that the column caps below its measured width.
        string fixture="before\n\n| 긴 첫 칸이 두세 줄로 감싸집니다 계속 이어지는 문장 하나 둘 셋 넷 다섯 여섯 | 둘째 | 셋째 |\n|---|---|---|\n| 짧음 | 가나 | 다라 |\n\nafter\n";
        session=Attach(editor);editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        TextDocument document=editor.Document;
        DocumentLine row=document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info=session.Info(row);
        session.View.GetOrConstructVisualLine(row);Pump();
        info=session.Info(row);
        MarkdownLivePreview.Cell first=info.Cells[0];
        int start=row.Offset+first.Start,end=row.Offset+first.End;
        // Right from the cell start walks forward inside the cell only; the only permitted
        // step out of the cell is a hop to the NEXT cell's start at the cell boundary.
        editor.Select(start,0);Pump();
        bool rightInside=true;int previous=start;
        int nextStart=info.Cells.Count>1 ? row.Offset+info.Cells[1].Start : end;
        for (int step=0;step<600;step++) {
            EditingCommands.MoveRightByCharacter.Execute(null,editor.TextArea);Pump();
            int now=editor.CaretIndex;
            bool allowed=(now>=start&&now<=end)||now==nextStart;
            if (!allowed) {rightInside=false;check("navigation right stays in cell0",false,"offset="+now+", cell="+start+".."+end+", next="+nextStart);break;}
            if (now==previous) break;
            previous=now;
            if (now==nextStart) break;
        }
        check("navigation right stays in cell0",rightInside&&previous<=nextStart,"last="+previous+", cell="+start+".."+end+", next="+nextStart+", rows="+(info.Row==null?0:info.Row.PhysicalRows));
        // Left from the cell end walks back to the cell start, never into the neighbour.
        editor.Select(end,0);Pump();
        bool leftInside=true;
        for (int step=0;step<600;step++) {
            EditingCommands.MoveLeftByCharacter.Execute(null,editor.TextArea);Pump();
            int now=editor.CaretIndex;
            if (now<start||now>end) {leftInside=false;check("navigation left stays in cell0",false,"offset="+now+", cell="+start+".."+end);break;}
            if (now==start) break;
        }
        check("navigation left stays in cell0",leftInside&&editor.CaretIndex==start,"caret="+editor.CaretIndex+", cell="+start+".."+end);
        // Up/Down inside a wrapped first cell stay in that cell and move between its slices.
        check("navigation fixture wraps the first cell",info.Row!=null&&info.Row.Slices[0]!=null&&info.Row.Slices[0].Count>1,
            "rows="+(info.Row==null?0:info.Row.PhysicalRows)+", slices="+(info.Row==null||info.Row.Slices[0]==null?0:info.Row.Slices[0].Count));
        if (info.Row!=null&&info.Row.Slices[0]!=null&&info.Row.Slices[0].Count>1) {
            int middle=start+Math.Min(2,first.End-first.Start);
            editor.Select(middle,0);Pump();
            EditingCommands.MoveDownByLine.Execute(null,editor.TextArea);Pump();
            bool down=editor.CaretIndex>=start&&editor.CaretIndex<=end;
            check("navigation down stays in cell0",down,"caret="+editor.CaretIndex+", cell="+start+".."+end);
            EditingCommands.MoveUpByLine.Execute(null,editor.TextArea);Pump();
            check("navigation up stays in cell0",editor.CaretIndex>=start&&editor.CaretIndex<=end,"caret="+editor.CaretIndex+", cell="+start+".."+end);
        }
        // A selection spanning two cells must clear only their text: never a '|'.
        editor.Select(start,end-start);Pump();
        string before=document.Text;int pipes=Pipes(before);
        editor.TextArea.PerformTextInput("Z");Pump();
        string after=document.Text;
        check("multi-cell typing keeps the pipes",Pipes(after)==pipes&&after.Length<before.Length&&after.IndexOf("|",StringComparison.Ordinal)>=0,
            "pipes="+pipes+"->"+Pipes(after)+", line=["+(document.GetText(row).Length<=140?document.GetText(row):document.GetText(row).Substring(0,140))+"]");
        // The probe editor has no window history (the AvalonEdit undo stack is disabled), so
        // each case starts from the fixture again instead of undoing the previous one.
        editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        row=editor.Document.GetLineByNumber(3);
        info=session.Info(row);first=info.Cells[0];
        start=row.Offset+first.Start;end=row.Offset+first.End;
        editor.Select(start,end-start);Pump();
        EditingCommands.Delete.Execute(null,editor.TextArea);Pump();
        after=document.Text;
        check("multi-cell delete keeps the pipes",Pipes(after)==pipes,"pipes="+pipes+"->"+Pipes(after)+", line=["+(document.GetText(row).Length<=140?document.GetText(row):document.GetText(row).Substring(0,140))+"]");
        // Every entry path into a rendered row must leave the caret inside a cell. Setting it
        // after an INTERIOR row's trailing '|' or in the hidden separator must snap it back.
        // (The last row's own end is deliberately kept: the table-boundary Enter rule matches
        // on column == line length there.)
        editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        row=editor.Document.GetLineByNumber(3);
        info=session.Info(row);
        first=info.Cells[0];
        start=row.Offset+first.Start;end=row.Offset+first.End;
        int lastCellEnd=row.Offset+info.Cells[info.Cells.Count-1].End;
        editor.TextArea.Caret.Offset=row.Offset+row.Length;Pump();
        bool snappedEnd=editor.CaretIndex>=start&&editor.CaretIndex<=lastCellEnd;
        check("caret after an interior row's pipe snaps into a cell",snappedEnd,"caret="+editor.CaretIndex+", cells "+start+".."+lastCellEnd+", row end="+(row.Offset+row.Length));
        if (info.Cells.Count>1) {
            int separator=row.Offset+first.End;
            editor.TextArea.Caret.Offset=separator;Pump();
            int afterSeparator=editor.CaretIndex;
            bool separatorInside=false;
            foreach (var cell in info.Cells) if (afterSeparator-row.Offset>=cell.Start&&afterSeparator-row.Offset<=cell.End) { separatorInside=true;break; }
            check("caret on a hidden separator snaps into a cell",separatorInside,"caret="+(afterSeparator-row.Offset)+", cells="+info.Cells.Count);
        }
        // ---- the row the caret is on (revealed, raw, still one row-major element) ----
        // Down from the header: the |---| line belongs to the table but is not a row, so the
        // move must cross it to the first body row. Returning to AvalonEdit instead moved by
        // visual line and stayed inside the revealed header row's own next physical row.
        editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine header=editor.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo headerInfo=session.Info(header);
        int headerRows=headerInfo.Row==null?0:headerInfo.Row.PhysicalRows;
        int lastCell=headerInfo.Cells.Count-1;
        int lastCellSlices=headerInfo.Row==null||headerInfo.Row.Slices[lastCell]==null?0:headerInfo.Row.Slices[lastCell].Count;
        editor.Select(header.Offset+headerInfo.Cells[lastCell].Start,0);Pump();
        EditingCommands.MoveDownByLine.Execute(null,editor.TextArea);Pump();
        int downLine=editor.Document.GetLineByOffset(editor.CaretIndex).LineNumber;
        check("navigation down from the header crosses the delimiter",headerRows>1&&lastCellSlices==1&&downLine==5,
            "header rows="+headerRows+", cell slices="+lastCellSlices+", caret line="+downLine+", offset="+editor.CaretIndex);
        // Up from the first body row: the same delimiter step, landing on the header.
        editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine body=editor.Document.GetLineByNumber(5);
        MarkdownLivePreview.LineInfo bodyInfo=session.Info(body);
        int bodyCell=bodyInfo.Cells.Count-1;
        editor.Select(body.Offset+bodyInfo.Cells[bodyCell].Start,0);Pump();
        EditingCommands.MoveUpByLine.Execute(null,editor.TextArea);Pump();
        int upLine=editor.Document.GetLineByOffset(editor.CaretIndex).LineNumber;
        int upCell=TableCellIndex(session.Info(editor.Document.GetLineByNumber(upLine)),editor.CaretIndex-editor.Document.GetLineByNumber(upLine).Offset);
        check("navigation up from the first body row reaches the header",upLine==3&&upCell==bodyCell,
            "caret line="+upLine+", cell="+upCell+"/"+bodyCell+", offset="+editor.CaretIndex);
        // A caret on a revealed pipe/padding stop (CellAt == -1) moves in document order: Right
        // enters the next cell, Down crosses to the next row's cell, never another cell's
        // physical row of the row-major projection.
        editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        header=editor.Document.GetLineByNumber(3);headerInfo=session.Info(header);
        int gapOffset=header.Offset+headerInfo.Cells[0].End+1;
        editor.TextArea.Caret.Offset=gapOffset;Pump();
        int gapRel=editor.CaretIndex-header.Offset;
        bool gapCell=-1==TableCellIndex(headerInfo,gapRel);
        EditingCommands.MoveRightByCharacter.Execute(null,editor.TextArea);Pump();
        int gapRight=editor.CaretIndex-header.Offset;
        check("navigation right from a pipe stop lands in a cell",gapCell&&TableCellIndex(headerInfo,gapRight)>=0,
            "gap="+gapRel+", after right="+gapRight+", cell="+TableCellIndex(headerInfo,gapRight));
        editor.TextArea.Caret.Offset=gapOffset;Pump();
        EditingCommands.MoveDownByLine.Execute(null,editor.TextArea);Pump();
        int gapDownLine=editor.Document.GetLineByOffset(editor.CaretIndex).LineNumber;
        check("navigation down from a pipe stop crosses to the next row",gapDownLine==5,
            "caret line="+gapDownLine+", offset="+editor.CaretIndex);
        // A selection across two cells of the row being edited must clear only their text: the
        // '|' delimiters and the column count survive. This is the row users actually select
        // in, and it is revealed (raw) because the caret is on it.
        editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        row=editor.Document.GetLineByNumber(5);info=session.Info(row);
        int twoFrom=row.Offset+info.Cells[0].Start,twoTo=row.Offset+info.Cells[1].End;
        editor.Select(twoFrom,twoTo-twoFrom);Pump();
        bool revealed=session.Info(row).Row!=null&&session.Info(row).Row.Raw;
        int columnsBefore=MarkdownLivePreview.SplitCells(editor.Document.GetText(row)).Count;
        int pipesBefore=Pipes(editor.Document.Text);
        editor.TextArea.PerformTextInput("Z");Pump();
        int columnsTyped=MarkdownLivePreview.SplitCells(editor.Document.GetText(editor.Document.GetLineByNumber(5))).Count;
        check("editing row: typing across two cells keeps the pipes",revealed&&Pipes(editor.Document.Text)==pipesBefore&&columnsTyped==columnsBefore,
            "raw="+revealed+", pipes="+pipesBefore+"->"+Pipes(editor.Document.Text)+", columns="+columnsBefore+"->"+columnsTyped+
            ", line=["+editor.Document.GetText(editor.Document.GetLineByNumber(5))+"]");
        editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        row=editor.Document.GetLineByNumber(5);info=session.Info(row);
        editor.Select(twoFrom,twoTo-twoFrom);Pump();
        EditingCommands.Delete.Execute(null,editor.TextArea);Pump();
        int columnsDeleted=MarkdownLivePreview.SplitCells(editor.Document.GetText(editor.Document.GetLineByNumber(5))).Count;
        check("editing row: deleting across two cells keeps the pipes",Pipes(editor.Document.Text)==pipesBefore&&columnsDeleted==columnsBefore,
            "pipes="+pipesBefore+"->"+Pipes(editor.Document.Text)+", columns="+columnsBefore+"->"+columnsDeleted+
            ", line=["+editor.Document.GetText(editor.Document.GetLineByNumber(5))+"]");
        // Home/End stay in the cell on every row; End at the last cell's end stays there, also on
        // the table's LAST row (the row-end slot after the trailing pipe is no longer a stop).
        editor.Text="before\n\n| 머리 | 둘째 |\n| --- | --- |\n| 하나 | 둘 |\n| 셋 | 넷 |\n\nafter\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine interior=editor.Document.GetLineByNumber(5),lastRowLine=editor.Document.GetLineByNumber(6);
        var interiorInfo=session.Info(interior);
        int interiorCellEnd=interiorInfo.Cells[interiorInfo.Cells.Count-1].End;
        editor.Select(interior.Offset+interiorInfo.Cells[interiorInfo.Cells.Count-1].Start,0);Pump();
        EditingCommands.MoveToLineEnd.Execute(null,editor.TextArea);Pump();
        int interiorOnce=editor.CaretIndex-interior.Offset;
        EditingCommands.MoveToLineEnd.Execute(null,editor.TextArea);Pump();
        int interiorTwice=editor.CaretIndex-interior.Offset;
        check("navigation end stays in an interior row's last cell",interiorOnce==interiorCellEnd&&interiorTwice==interiorCellEnd,
            "cell end="+interiorCellEnd+", end="+interiorOnce+", end again="+interiorTwice+", row length="+interior.Length);
        var lastRowInfo=session.Info(lastRowLine);
        int lastRowCellEnd=lastRowInfo.Cells[lastRowInfo.Cells.Count-1].End;
        editor.Select(lastRowLine.Offset+lastRowInfo.Cells[lastRowInfo.Cells.Count-1].Start,0);Pump();
        EditingCommands.MoveToLineEnd.Execute(null,editor.TextArea);Pump();
        int lastRowOnce=editor.CaretIndex-lastRowLine.Offset;
        EditingCommands.MoveToLineEnd.Execute(null,editor.TextArea);Pump();
        int lastRowTwice=editor.CaretIndex-lastRowLine.Offset;
        check("navigation end twice on the last row stays in its last cell",lastRowOnce==lastRowCellEnd&&lastRowTwice==lastRowCellEnd,
            "cell end="+lastRowCellEnd+", end="+lastRowOnce+", end again="+lastRowTwice+", row length="+lastRowLine.Length);
        EditingCommands.MoveToLineEnd.Execute(null,editor.TextArea);Pump();
        int lastRowThird=editor.CaretIndex-lastRowLine.Offset;
        editor.Select(lastRowLine.Offset+lastRowLine.Length,0);Pump();
        EditingCommands.MoveToLineStart.Execute(null,editor.TextArea);Pump();
        int homeFromEnd=editor.CaretIndex-lastRowLine.Offset;
        int lastRowLastCellStart=lastRowInfo.Cells[lastRowInfo.Cells.Count-1].Start;
        check("navigation end stays in the last cell and home returns to its start",
            lastRowThird==lastRowCellEnd&&homeFromEnd==lastRowLastCellStart,
            "end third="+lastRowThird+", home="+homeFromEnd+", last cell="+lastRowLastCellStart+", row length="+lastRowLine.Length);
        // Left with a non-empty selection: AvalonEdit clears the selection without moving the
        // caret (TextArea.ClearSelection) and then steps once from it, so the table handler
        // follows the same rule - the step is the row's own previous caret stop, never a raw
        // document-order +-1 - and only a whole-cell (Tab) selection collapses to its edge.
        editor.Text="before\n\nplain words here\n\nafter\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine plain=editor.Document.GetLineByNumber(3);
        editor.Select(plain.Offset+1,4);Pump();
        EditingCommands.MoveLeftByCharacter.Execute(null,editor.TextArea);Pump();
        int plainCaret=editor.CaretIndex-plain.Offset;
        int plainSelectionLength=editor.SelectionLength;
        editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        header=editor.Document.GetLineByNumber(3);headerInfo=session.Info(header);
        int plainCellStart=header.Offset+headerInfo.Cells[0].Start;
        editor.Select(plainCellStart,4);Pump();
        EditingCommands.MoveLeftByCharacter.Execute(null,editor.TextArea);Pump();
        int tableCaret=editor.CaretIndex-plainCellStart;
        check("navigation selection left follows the stock caret step",plainSelectionLength==0&&plainCaret==4&&
            editor.SelectionLength==0&&tableCaret==3,
            "stock caret="+plainCaret+", table caret="+tableCaret+", table selection="+editor.SelectionLength);
        // The same clear-and-step must land on a real caret stop: a selection that ends just
        // after an emoji's low surrogate steps Left to the stop in front of the pair, never to
        // the offset between the surrogates (the old document-order +-1 did).
        editor.Text="before\n\n| 머리 | 둘 |\n| --- | --- |\n| a\U0001F600b | c |\n\nafter\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine emojiRow=editor.Document.GetLineByNumber(5);
        MarkdownLivePreview.LineInfo emojiInfo=session.Info(emojiRow);
        int emoji=emojiInfo.Text.IndexOf("\U0001F600",StringComparison.Ordinal);
        int emojiHigh=emojiRow.Offset+emoji;
        editor.Select(emojiHigh+1,1);Pump();
        EditingCommands.MoveLeftByCharacter.Execute(null,editor.TextArea);Pump();
        int emojiCaret=editor.CaretIndex;
        bool emojiMid=emojiCaret>0&&emojiCaret<editor.Document.TextLength&&
            char.IsSurrogatePair(editor.Document.GetCharAt(emojiCaret-1),editor.Document.GetCharAt(emojiCaret));
        check("navigation selection left lands on a caret stop outside a surrogate pair",
            !emojiMid&&editor.SelectionLength==0&&emojiCaret==emojiHigh,
            "caret="+emojiCaret+", high="+emojiHigh+", mid="+emojiMid+", selection="+editor.SelectionLength);
        // The Tab rule is unchanged: Right on exactly one cell's text collapses to that cell's
        // end instead of stepping a second time to the neighbour's start.
        editor.Text="before\n\n| a | b |\n| --- | --- |\n| 하나 | 둘 |\n\nafter\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine tabRow=editor.Document.GetLineByNumber(5);
        MarkdownLivePreview.LineInfo tabInfo=session.Info(tabRow);
        int tabStart=tabRow.Offset+tabInfo.Cells[0].Start,tabEnd=tabRow.Offset+tabInfo.Cells[0].End;
        editor.Select(tabStart,tabEnd-tabStart);Pump();
        EditingCommands.MoveRightByCharacter.Execute(null,editor.TextArea);Pump();
        check("navigation right on a whole-cell selection keeps the cell edge",
            editor.SelectionLength==0&&editor.CaretIndex==tabEnd,
            "caret="+editor.CaretIndex+", cell end="+tabEnd+", selection="+editor.SelectionLength);
        // A redirected IME commit whose point sits after an interior row's trailing '|' is
        // pulled into the last cell, while the caret stays where the user clicked (SourceEditor's
        // documented contract). Nothing is written after the pipe, so no column appears.
        editor.Text="before\n\n| 머리 | 둘째 |\n| --- | --- |\n| 하나 | 둘 |\n| 셋 | 넷 |\n\nafter\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine commitRow=editor.Document.GetLineByNumber(5);
        var commitInfo=session.Info(commitRow);
        int commitCellEnd=commitRow.Offset+commitInfo.Cells[commitInfo.Cells.Count-1].End;
        int pipesAtCommit=Pipes(editor.Document.Text);
        editor.SimulateImeComposition();
        editor.CaretIndex=commitRow.Offset+commitRow.Length;
        editor.SimulateImeComposition();
        DocumentLine belowLine=editor.Document.GetLineByNumber(8);
        int clickOffset=belowLine.Offset+2;
        editor.Select(clickOffset,0);Pump();
        editor.BeginCompositionCommit();
        editor.TextArea.PerformTextInput("가");Pump();
        editor.ResetCompositionSimulation();
        string commitLine=editor.Document.GetText(editor.Document.GetLineByNumber(5));
        int commitColumns=MarkdownLivePreview.SplitCells(commitLine).Count;
        check("redirected commit lands in the last cell and keeps the click caret",
            commitLine.IndexOf("둘가",StringComparison.Ordinal)>=0&&commitColumns==2&&Pipes(editor.Document.Text)==pipesAtCommit&&
            editor.CaretIndex==clickOffset+1,
            "line=["+commitLine+"], columns="+commitColumns+", pipes="+pipesAtCommit+"->"+Pipes(editor.Document.Text)+
            ", caret="+editor.CaretIndex+"/"+(clickOffset+1)+", cell end="+(commitCellEnd-commitRow.Offset));
        // Badge syntax "[![alt](img.png)](https://x)": the inner image's "](" must not be read
        // as the outer link's target (ParseInline's LinkLabelEnd walks bracket depth). The line
        // is rendered (the caret is on line 1), so the whole "](https://x)" tail must stay
        // hidden and one link must name the outer URL.
        editor.Text="before\n\n[![alt](img.png)](https://x)\n\nafter\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine badgeLine=editor.Document.GetLineByNumber(3);
        string badgeText=editor.Document.GetText(badgeLine);
        MarkdownLivePreview.LineInfo badge=session.Info(badgeLine);
        int tail=badgeText.IndexOf("](https://x)",StringComparison.Ordinal);
        bool tailHidden=tail>=0&&badge!=null;
        if (tailHidden) for (int p=tail;p<tail+12;p++) {
            bool covered=false;
            foreach (MarkdownLivePreview.Replacement rep in badge.Replacements)
                if (rep.Kind==MarkdownLivePreview.ReplacementKind.Hidden&&p>=rep.Start&&p<rep.Start+rep.Length) {covered=true;break;}
            if (!covered) {tailHidden=false;break;}
        }
        check("badge link hides the outer target",tailHidden&&badge.Links!=null&&badge.Links.Count==1&&badge.Links[0].Url=="https://x",
            "tail="+tail+", hidden="+tailHidden+", links="+(badge==null||badge.Links==null?-1:badge.Links.Count)+
            ", url="+(badge!=null&&badge.Links!=null&&badge.Links.Count>0?badge.Links[0].Url:"-")+", text=["+badgeText+"]");
        // A code span that opens inside a link label and closes past the label must not claim the
        // hidden "](url)" tail: ParseInline invalidates that span at the label end, so the first
        // backtick stays literal text and the link keeps its own hidden target.
        editor.Text="before\n\n[a `b](https://u) c` d\n\nafter\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine spanLine=editor.Document.GetLineByNumber(3);
        string spanText=editor.Document.GetText(spanLine);
        MarkdownLivePreview.LineInfo span=session.Info(spanLine);
        int openTick=spanText.IndexOf("`",StringComparison.Ordinal);
        bool tickVisible=true;
        foreach (MarkdownLivePreview.Replacement rep in span.Replacements)
            if (rep.Kind==MarkdownLivePreview.ReplacementKind.Hidden&&openTick>=rep.Start&&openTick<rep.Start+rep.Length) {tickVisible=false;break;}
        check("code span past a link label stays literal",tickVisible&&span.Links!=null&&span.Links.Count==1&&span.Links[0].Url=="https://u",
            "tick="+openTick+", visible="+tickVisible+", links="+(span==null||span.Links==null?-1:span.Links.Count)+
            ", url="+(span!=null&&span.Links!=null&&span.Links.Count>0?span.Links[0].Url:"-")+", text=["+spanText+"]");
        // A fenced code line keeps its literal "\|": TablePipeDelete must not take the two
        // characters as the table-escape unit there (HiddenTextDelete's code guard), so one
        // Backspace removes only the character before the caret.
        editor.Text="```\na \\| b\n```\n";editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine codeLine=editor.Document.GetLineByNumber(2);
        editor.Select(codeLine.Offset+4,0);Pump();
        EditingCommands.Backspace.Execute(null,editor.TextArea);Pump();
        string codeAfter=editor.Document.GetText(editor.Document.GetLineByNumber(2));
        check("backspace on a code line keeps one character of \\|",codeAfter=="a \\ b",
            "line=["+codeAfter+"]");
        window.Width=1920;window.UpdateLayout();Pump();
        if (session!=null) {session.Dispose();session=null;}
    }
    static int TableCellIndex(MarkdownLivePreview.LineInfo info,int relative) {
        if (info==null||info.Cells==null) return -1;
        for (int c=0;c<info.Cells.Count;c++) if (relative>=info.Cells[c].Start&&relative<=info.Cells[c].End) return c;
        return -1;
    }
    // Task 4: <br>/<br/>/<br /> are a line break on non-caret lines (paragraph and table cell)
    // and stay raw on the caret's own line, like the other hidden markup.
    static void VerifyBreaks(Window window,SourceEditor editor,ref MarkdownLivePreview.Session session,Action<string,bool,string> check) {
        if (session!=null) {session.Dispose();session=null;}
        window.Width=1920;window.UpdateLayout();Pump();
        string fixture="앞\n\n가<br>나<br/>다<br />라\n\n| 머리 | 둘 |\n|---|---|\n| x<br>y | z<br />w |\n\n뒤\n";
        session=Attach(editor);editor.Text=fixture;editor.CaretIndex=0;
        editor.Select(editor.Document.GetLineByNumber(1).Offset,0);Pump();
        TextDocument document=editor.Document;var view=editor.TextArea.TextView;
        // Paragraph and cell <br> cases are reported separately so one exception cannot
        // hide the other case's checks.
        try {
        DocumentLine paragraph=document.GetLineByNumber(3);
        view.GetOrConstructVisualLine(paragraph);Pump();
        VisualLine visual=view.GetVisualLine(3);
        int rows=visual==null ? -1 : visual.TextLines.Count,length=visual==null ? -1 : visual.VisualLength;
        editor.Select(paragraph.Offset+1,0);Pump();
        VisualLine raw=view.GetVisualLine(3);
        int rawRows=raw==null ? -1 : raw.TextLines.Count,rawLength=raw==null ? -1 : raw.VisualLength;
        check("br paragraph breaks on a non-caret line",rows>=4 && length>=3 && length<=10,"rows="+rows+", visual length="+length);
        check("br paragraph stays raw on the caret line",rawRows==1 && rawLength>=15,"rows="+rawRows+", visual length="+rawLength+", text="+document.GetText(paragraph));
        } catch (Exception ex) {check("br paragraph exception",false,ex.ToString());}
        try {
        editor.Select(document.GetLineByNumber(1).Offset,0);Pump();
        DocumentLine rowLine=document.GetLineByNumber(7);
        view.GetOrConstructVisualLine(rowLine);Pump();
        MarkdownLivePreview.RowLayout layout=session.Info(rowLine).Row;
        VisualLine rowVisual=view.GetVisualLine(7);
        bool cellBreak=layout!=null && layout.Texts[0]!=null && layout.Texts[1]!=null &&
            layout.Texts[0].Text.Length==3 && layout.Texts[0].IsBreak(1) && layout.Texts[1].Text.Length==3 && layout.Texts[1].IsBreak(1) &&
            layout.Slices[0].Count==2 && layout.Slices[1].Count==2;
        bool noTag=layout!=null && layout.Texts[0]!=null && layout.Texts[1]!=null &&
            layout.Texts[0].Text.IndexOf('<')<0 && layout.Texts[1].Text.IndexOf('<')<0;
        check("br table cell wraps without the tag",cellBreak && noTag && rowVisual!=null && rowVisual.TextLines.Count>=2,
            "cells="+(layout==null||layout.Texts[0]==null ? "-" : Clean(layout.Texts[0].Text)+"/"+Clean(layout.Texts[1].Text))+
            ", slices="+(layout==null ? "-" : layout.Slices[0].Count+"/"+layout.Slices[1].Count)+", rows="+(rowVisual==null ? -1 : rowVisual.TextLines.Count));
        editor.Select(rowLine.Offset+2,0);Pump();
        layout=session.Info(rowLine).Row;
        bool rawCell=layout!=null && layout.Texts[0]!=null && layout.Texts[0].Text.IndexOf('<')>=0;
        check("br table cell keeps the tag on the caret row",rawCell,"cell="+(layout==null||layout.Texts[0]==null ? "-" : layout.Texts[0].Text));
        } catch (Exception ex) {check("br table cell exception",false,ex.ToString());}
        if (session!=null) {session.Dispose();session=null;}
    }
    static string Clean(string text) {
        var result=new StringBuilder();foreach (char c in text) result.Append(c==MarkdownLivePreview.CellText.BreakCharacter ? '|' : c);return result.ToString();
    }
}
public partial class MainWindow {
    internal static int VerifyLiveTypingStability(string outDir){return LiveTypingStabilityProbe.Run(outDir);}
}
}
