using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;

namespace MDNote {
// Research only: input-to-frame cost through the whole MainWindow pipeline
// (title, history, outline, search hooks), not a bare editor. Generated data only.
public partial class MainWindow {
    // Bench-only deferred-operation breakdown for MeasurePostUnlockStall. Normal runs pay one
    // static bool check per probe; the dispatcher hooks exist only inside a file-bench open.
    internal sealed class BenchDeferredEntry {
        internal string Name; internal long Stamp; internal double Milliseconds;
    }
    internal static bool BenchDeferredActive;
    internal static long BenchUnlockTimestamp;
    // Window-wide LayoutUpdated passes, counted only while MeasurePostUnlockStall samples.
    internal int benchLayoutPasses;
    internal static readonly List<BenchDeferredEntry> BenchDeferredEvents = new List<BenchDeferredEntry>();
    static readonly Dictionary<DispatcherOperation,long> benchRunning = new Dictionary<DispatcherOperation,long>();
    static DispatcherHooks benchHooks;
    // The operation the UI thread is inside right now, readable from another thread. The completion
    // hook removes a blocked operation before the next operation (the sampler's own tick) can run, so
    // the in-thread view can never see what was holding the thread; a background sampler can.
    internal static volatile string BenchCurrentOperation;
    // Off unless a marker file sits next to the exe (see BenchStallBreakdown). Everything below reads
    // it, so a default run records and prints exactly what it did before the stall investigation.
    internal static bool BenchBreakdown;

    internal static long BenchProbeStart() { return BenchDeferredActive ? Stopwatch.GetTimestamp() : 0; }
    internal static void BenchProbeEnd(string name,long start) {
        if(!BenchDeferredActive||start==0)return;
        long now=Stopwatch.GetTimestamp();
        BenchDeferredEvents.Add(new BenchDeferredEntry { Name=name, Stamp=now, Milliseconds=(now-start)*1000.0/Stopwatch.Frequency });
    }
    // A dispatcher operation's start-to-completion span is one uninterrupted UI-thread block; a
    // 2.6 s entry here names the priority of whatever held the thread after unlock (our named
    // probes above label the ones we posted ourselves).
    static System.Reflection.FieldInfo benchOperationMethod, benchOperationDelegate;
    // Names the code behind a dispatcher operation: DispatcherOperation keeps its delegate in a
    // private field, and for WPF's own render/layout callbacks that name is the whole answer
    // ("ContextLayoutManager.UpdateLayoutCallback" is a layout pass; our own hooks name their method).
    internal static string BenchOperationName(DispatcherOperation operation) {
        if (operation == null) return "-";
        try {
            if (benchOperationMethod == null) benchOperationMethod = typeof(DispatcherOperation).GetField("_method", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (benchOperationDelegate == null) benchOperationDelegate = typeof(DispatcherOperation).GetField("_delegate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            object target = benchOperationMethod == null ? null : benchOperationMethod.GetValue(operation);
            if (target == null && benchOperationDelegate != null) target = benchOperationDelegate.GetValue(operation);
            var method = target as Delegate;
            if (method != null) {
                System.Reflection.MethodInfo info = method.Method;
                return (info.DeclaringType == null ? "?" : info.DeclaringType.Name) + "." + info.Name;
            }
            if (target != null) return target.GetType().Name;
        } catch { }
        return operation.GetType().Name;
    }
    static void BenchOperationStarted(object sender,DispatcherHookEventArgs args) {
        if(args==null||args.Operation==null)return;
        benchRunning[args.Operation]=Stopwatch.GetTimestamp();
        // Resolving the delegate name costs a reflection lookup per operation, so it is part of the
        // marker-gated breakdown; without the marker an entry is just its priority, as before.
        if(BenchBreakdown)BenchCurrentOperation=args.Operation.Priority+" "+BenchOperationName(args.Operation);
    }
    static void BenchOperationCompleted(object sender,DispatcherHookEventArgs args) {
        if(args==null||args.Operation==null)return;
        long start;
        if(!benchRunning.TryGetValue(args.Operation,out start))return;
        benchRunning.Remove(args.Operation);
        BenchCurrentOperation=null;
        long now=Stopwatch.GetTimestamp();
        BenchDeferredEvents.Add(new BenchDeferredEntry { Name="dispatcher "+args.Operation.Priority+(BenchBreakdown?" "+BenchOperationName(args.Operation):""), Stamp=now, Milliseconds=(now-start)*1000.0/Stopwatch.Frequency });
    }
    internal static void BeginBenchDeferredCapture() {
        if(BenchDeferredActive)return;
        // One marker check for the whole open: the hooks and MeasurePostUnlockStall then share it.
        BenchBreakdown=BenchStallBreakdown();
        BenchDeferredActive=true;BenchUnlockTimestamp=0;BenchDeferredEvents.Clear();benchRunning.Clear();
        benchHooks=Dispatcher.CurrentDispatcher.Hooks;
        benchHooks.OperationStarted+=BenchOperationStarted;benchHooks.OperationCompleted+=BenchOperationCompleted;
    }
    internal static void EndBenchDeferredCapture() {
        if(!BenchDeferredActive)return;
        BenchDeferredActive=false;
        if(benchHooks!=null){benchHooks.OperationStarted-=BenchOperationStarted;benchHooks.OperationCompleted-=BenchOperationCompleted;benchHooks=null;}
    }

    internal static int RunMainWindowBench(string outputDirectory) {
        string outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        bool quick = outDir.TrimEnd('\\', '/').EndsWith("-quick", StringComparison.OrdinalIgnoreCase);
        if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var report = new StringBuilder();
        report.Append("# MainWindowBench\n\n- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        report.Append("- Each sample: action, next rendered frame, then ContextIdle. 5 warmup + 30 chars + 10 Enter per position.\n\n");
        report.Append("| Case | Chars | Open ms | Position | Char p50 | Char p95 | Char max | Enter p95 | Private MB |\n|---|---:|---:|---|---:|---:|---:|---:|---:|\n");
        string txt = Path.Combine(outDir, "bench-generated.txt"), md = Path.Combine(outDir, "bench-generated.md");
        int exit = 0;
        try {
            File.WriteAllText(txt, LivePreviewBench.GeneratePlain(quick ? 120000 : 12000000, 2001), new UTF8Encoding(false));
            File.WriteAllText(md, LivePreviewBench.GenerateMarkdown(quick ? 19000 : 1900000, 1001), new UTF8Encoding(false));
            // The md-live cases run the same MD file with the live preview attached.
            foreach (string run in new[] { "txt", "md", "md-live", "md-live-raw" }) {
                string file = run == "txt" ? txt : md;
                ProbeDefaults.Live = run.StartsWith("md-live", StringComparison.Ordinal);
                benchRawToggle = run == "md-live-raw";
                try { report.Append(MeasureWindow(file)); }
                catch (Exception ex) { exit = 1; report.Append("| ").Append(run).Append(" | FAIL ").Append(ex.GetType().Name).Append(": ").Append(ex.Message.Replace("|", "/").Replace("\n", " ")).Append(" |\n"); }
                finally { ProbeDefaults.Live = false; benchRawToggle = false; }
            }
        } finally {
            try { File.Delete(txt); } catch { }
            try { File.Delete(md); } catch { }
        }
        report.Append("\n## Per-char diagnostics\n\n").Append(diagnostics);
        File.WriteAllText(Path.Combine(outDir, "mainwindow.md"), report.ToString(), new UTF8Encoding(false));
        return exit;
    }

    // Same handlers as Ctrl+Z/Ctrl+Y, without sending global keys or touching the clipboard.
    // Run on the hidden test desktop, like the other MainWindow benches. Three independent
    // windows keep first-use/layout variation visible instead of reporting a single best run.
    internal static int RunUndoBench(string outputDirectory) {
        if (!Program.IsSelfTest || RecoveryStore.Enabled) throw new InvalidOperationException("Undo bench requires recovery disabled");
        string outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var previousContext = System.Threading.SynchronizationContext.Current;
        bool previousDefault = ProbeDefaults.Live, previousEditing = alwaysEditing;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var report = new StringBuilder("# Source-surface undo bench\n\n");
        report.Append("- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        report.Append("- Generated ASCII Markdown (~50 KB), tables; 30 independent source edits per trial.\n");
        report.Append("- Undo/Redo use the Ctrl+Z/Ctrl+Y handlers, not physical keyboard input. Samples include dispatcher/render + ContextIdle; correctness checks are outside timing.\n");
        report.Append("- Each step checks the applied source text and the document model. No performance threshold is assumed.\n\n");
        string generated = Path.Combine(outDir, "undo-generated-" + Guid.NewGuid().ToString("N") + ".md");
        int exit = 0;
        try {
            ProbeDefaults.Live = true; alwaysEditing = true;
            File.WriteAllText(generated, GenerateUndoMarkdown(), new UTF8Encoding(false));
            for (int trial = 1; trial <= 3; trial++) {
                MainWindow window = null;
                try {
                    window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=1000, Height=800 };
                    window.Show(); window.Left = -20000;
                    window.LoadFileCore(generated); if (!window.editing) window.SetMode(true);
                    LongLineBench.Idle();
                    if (!window.CodeMode || window.PlainText) throw new InvalidOperationException("Expected the Markdown source surface");
                    var source = window.source;
                    var expected = new List<string>(); expected.Add(window.text);
                    var targets = new List<int>();
                    int stride = Math.Max(1, source.Document.LineCount / 60);
                    for (int line = 1; line <= source.Document.LineCount && targets.Count < 30; line += stride) targets.Add(source.Document.GetLineByNumber(line).EndOffset);
                    if (targets.Count < 30) throw new InvalidOperationException("Generated bench has too few targets");
                    for (int i = 0; i < 30; i++) {
                        source.Select(Math.Min(targets[i], source.Document.TextLength), 0);
                        source.TextArea.PerformTextInput(" edit" + i.ToString("00", CultureInfo.InvariantCulture));
                        window.FlushDocumentChanges(); LongLineBench.Idle(); expected.Add(window.text);
                    }
                    if (source.Document.Text != expected[30]) throw new InvalidOperationException("Typing did not reach the source surface");
                    report.Append("## Trial ").Append(trial).Append("\n\n- chars ").Append(expected[0].Length).Append(", edits 30\n\n");
                    foreach (bool redo in new[] { false, true }) {
                        report.Append("| Direction | Step | ms | Status |\n|---|---:|---:|---|\n");
                        var samples = new List<double>();
                        for (int step = 0; step < 30; step++) {
                            if (redo ? !window.History.CanRedo : !window.History.CanUndo) throw new InvalidOperationException("History ended before 30 steps");
                            double ms = LongLineBench.Time(delegate { window.RestoreEditorHistory(redo); }, window, DispatcherPriority.ContextIdle);
                            samples.Add(ms);
                            VerifyUndoBenchState(window, expected[redo ? step + 1 : 29 - step]);
                            report.Append(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2:0.000} | PASS |\n",
                                redo ? "redo" : "undo", step + 1, ms));
                        }
                        AppendUndoBenchSummary(report, redo ? "redo" : "undo", samples);
                    }
                    // Do not pump between presses: the source and the model must advance immediately,
                    // while the preview is committed only once.
                    foreach (bool redo in new[] { false, true }) {
                        var watch = Stopwatch.StartNew();
                        for (int i = 0; i < 30; i++) { window.RestoreEditorHistory(redo); }
                        double sourceMs = watch.Elapsed.TotalMilliseconds;
                        string wanted = expected[redo ? 30 : 0];
                        if (source.Document.Text != wanted || window.text != wanted) throw new InvalidOperationException("Burst source not applied immediately");
                        double flushMs = LongLineBench.Time(delegate { window.FlushDocumentChanges(); }, window, DispatcherPriority.ContextIdle);
                        VerifyUndoBenchState(window, wanted);
                        report.Append(string.Format(CultureInfo.InvariantCulture, "\n- {0} burst x30: source {1:0.000} ms, following save/flush {2:0.000} ms, PASS\n",
                            redo ? "redo" : "undo", sourceMs, flushMs));
                    }
                } catch (Exception error) {
                    exit = 1; report.Append("\nFAIL trial ").Append(trial).Append(": ").Append(error.GetType().Name).Append(" ").Append(error.Message.Replace('\n', ' ')).Append('\n');
                } finally {
                    if (window != null) { window.saved = window.text; window.Close(); LongLineBench.Idle(); }
                }
            }
        } finally {
            ProbeDefaults.Live = previousDefault; alwaysEditing = previousEditing;
            System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
            try { File.Delete(generated); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        report.Append("\nResult: ").Append(exit == 0 ? "PASS" : "FAIL").Append('\n');
        File.WriteAllText(Path.Combine(outDir, "undo-bench.md"), report.ToString(), new UTF8Encoding(false));
        return exit;
    }
    static string GenerateUndoMarkdown() {
        var generated = new StringBuilder(); int section = 0;
        while (generated.Length < 50 * 1024) {
            generated.Append("## Section ").Append(section++).Append("\n\nUndo paragraph for independent history edits, with stable surrounding blocks.\n\n| Key | Value |\n| --- | --- |\n");
            for (int row = 0; row < 12; row++) generated.Append("| row ").Append(row).Append(" | Generated table contents for local undo measurement, not a user document. |\n");
            generated.Append('\n');
        }
        return generated.ToString();
    }
    static void VerifyUndoBenchState(MainWindow window, string expected) {
        if (window.text != expected || window.source.Text != expected) throw new InvalidOperationException("History source mismatch");
    }
    static void AppendUndoBenchSummary(StringBuilder report, string name, List<double> samples) {
        double total = 0; foreach (double ms in samples) total += ms;
        var sorted = new List<double>(samples); sorted.Sort();
        report.Append(string.Format(CultureInfo.InvariantCulture, "\n- {0} x{1}: p50 {2:0.000} ms, p95 {3:0.000} ms, max {4:0.000} ms, total {5:0.000} ms\n\n",
            name, samples.Count, LongLineBench.Percentile(sorted, .5), LongLineBench.Percentile(sorted, .95), sorted[sorted.Count - 1], total));
    }
    // Measures a user-provided file locally. Writes numbers only (no document text) to the report.
    // --generated-<N>m only opens the generated file through the editor/live paths.
    // The generated input is owned here and always removed.
    internal static int RunFileBench(string file, string outputDirectory) {
        if (!Program.IsSelfTest || RecoveryStore.Enabled) throw new InvalidOperationException("File bench requires recovery disabled");
        string generated = null;
        var context = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try {
            // --generated-<N>m picks the generated size in millions of characters (4 by default).
            int millions = 0;
            if (file.StartsWith("--generated-", StringComparison.Ordinal) && file.EndsWith("m", StringComparison.Ordinal) && !int.TryParse(file.Substring(12, file.Length - 13), out millions)) millions = 0;
            bool basicOnly = millions > 0;
            if (basicOnly) {
                Directory.CreateDirectory(Path.GetFullPath(outputDirectory));
                generated = Path.Combine(Path.GetFullPath(outputDirectory), "filebench-" + Guid.NewGuid().ToString("N") + ".md");
                File.WriteAllText(generated, LivePreviewBench.GenerateMarkdown(millions * 1000000, 1001), new UTF8Encoding(false));
                file = generated;
            }
            return RunFileBenchCore(file, outputDirectory, basicOnly);
        } finally {
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            if (generated != null) try { File.Delete(generated); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    static int RunFileBenchCore(string file, string outputDirectory, bool basicOnly) {
        string outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        string text = File.ReadAllText(file, Encoding.UTF8);
        var report = new StringBuilder("# FileBench (metrics only, no content)\n\n");
        int lines = 1, maxLine = 0, current = 0, over16k = 0, over30k = 0, fences = 0, pipes = 0, crlf = 0, lineStart = 0;
        for (int i = 0; i <= text.Length; i++) {
            bool end = i == text.Length || text[i] == '\n';
            if (!end) continue;
            current = i - lineStart;
            if (current > 0 && i > 0 && text[i - 1] == '\r') { current--; crlf++; }
            if (current > maxLine) maxLine = current;
            if (current > 16384) over16k++;
            if (current > 30000) over30k++;
            if (current >= 3 && (string.CompareOrdinal(text, lineStart, "```", 0, 3) == 0 || string.CompareOrdinal(text, lineStart, "~~~", 0, 3) == 0)) fences++;
            if (current > 0 && text[lineStart] == '|') pipes++;
            if (i < text.Length) lines++;
            lineStart = i + 1;
        }
        report.Append(string.Format(CultureInfo.InvariantCulture, "- chars {0}, lines {1}, CRLF lines {2}, max line {3}, lines >16K {4}, >30K {5}, fence lines {6}, table-like lines {7}\n\n",
            text.Length, lines, crlf, maxLine, over16k, over30k, fences, pipes));
        int exit = 0;
        if (basicOnly) {
            File.WriteAllText(Path.Combine(outDir, "filebench.md"), report.ToString(), new UTF8Encoding(false));
            return exit;
        }
        report.Append("| Mode | Open ms | Position | Char p50 | Char p95 | Char max | Enter p95 | Scroll p95 | Private MB |\n|---|---:|---|---:|---:|---:|---:|---:|---:|\n");
        // Only the attached live-preview editor remains: the preview-detached half and the whole
        // basic rich surface belonged to the retired engine.
        {
            var editor = new SourceEditor { FontFamily=new System.Windows.Media.FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true };
            editor.TextArea.TextView.LineHeight = double.NaN;
            IDisposable preview = LivePreviewSpike.Attach(editor, new LivePreviewSpike.Options { BaseDirectory=Path.GetDirectoryName(Path.GetFullPath(file)) });
            var window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false, ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            try {
                window.Show();
                LongLineBench.Idle();
                double open = LongLineBench.Time(delegate { editor.Text = text; }, editor.TextArea.TextView, DispatcherPriority.ApplicationIdle);
                var scroll = new List<double>();
                editor.UpdateLayout();
                double maximum = Math.Max(0, editor.ExtentHeight - editor.ViewportHeight);
                for (int i = 0; i < 40; i++) { double y = maximum * i / 39.0; scroll.Add(LongLineBench.Time(delegate { editor.ScrollToVerticalOffset(y); }, editor.TextArea.TextView, DispatcherPriority.ContextIdle)); }
                scroll.Sort();
                bool first = true;
                foreach (string position in new[] { "start", "middle", "end", "longest-line-middle" }) {
                    int total = editor.Document.TextLength;
                    int target = position == "start" ? 0 : position == "middle" ? total / 2 : total;
                    if (position == "longest-line-middle") {
                        ICSharpCode.AvalonEdit.Document.DocumentLine longest = editor.Document.GetLineByNumber(1);
                        foreach (var candidate in editor.Document.Lines) if (candidate.Length > longest.Length) longest = candidate;
                        editor.CaretOffset = longest.Offset + longest.Length / 2;
                    } else
                    editor.CaretOffset = editor.Document.GetLineByOffset(target).Offset;
                    editor.TextArea.Caret.BringCaretToView();
                    LongLineBench.Time(delegate { }, editor.TextArea.TextView, DispatcherPriority.ContextIdle);
                    for (int i = 0; i < 5; i++) LongLineBench.Time(delegate { editor.TextArea.PerformTextInput("가"); }, editor.TextArea.TextView, DispatcherPriority.ContextIdle);
                    var chars = new List<double>();
                    for (int i = 0; i < 30; i++) chars.Add(LongLineBench.Time(delegate { editor.TextArea.PerformTextInput("가"); }, editor.TextArea.TextView, DispatcherPriority.ContextIdle));
                    var enters = new List<double>();
                    for (int i = 0; i < 10; i++) enters.Add(LongLineBench.Time(delegate { editor.TextArea.PerformTextInput("\n"); }, editor.TextArea.TextView, DispatcherPriority.ContextIdle));
                    chars.Sort(); enters.Sort();
                    long memory;
                    using (var process = Process.GetCurrentProcess()) memory = process.PrivateMemorySize64;
                    report.Append(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3:0.0} | {4:0.0} | {5:0.0} | {6:0.0} | {7} | {8:0} |\n",
                        "live", first ? open.ToString("0.0", CultureInfo.InvariantCulture) : "", position,
                        LongLineBench.Percentile(chars, 0.5), LongLineBench.Percentile(chars, 0.95), chars[chars.Count - 1], LongLineBench.Percentile(enters, 0.95),
                        first ? LongLineBench.Percentile(scroll, 0.95).ToString("0.0", CultureInfo.InvariantCulture) : "", memory / 1048576.0));
                    first = false;
                }
            } finally {
                if (preview != null) preview.Dispose();
                window.Close();
                LongLineBench.Idle();
            }
        }
        // Whole MainWindow path with the B plan host. Self-test mode keeps recovery and preference
        // writes off (RecoveryStore.Enabled), so no copy of the document is written anywhere.
        if (string.Equals(Path.GetExtension(file), ".md", StringComparison.OrdinalIgnoreCase)) {
            report.Append("\n| MainWindow case | Chars | Open ms | Position | Char p50 | Char p95 | Char max | Enter p95 | Private MB |\n|---|---:|---:|---|---:|---:|---:|---:|---:|\n");
            ProbeDefaults.Live = true;
            try { report.Append(MeasureWindow(file)); }
            catch (Exception ex) { report.Append("| MainWindow live | FAIL ").Append(ex.GetType().Name).Append(" |\n"); }
            finally { ProbeDefaults.Live = false; }
        }
        File.WriteAllText(Path.Combine(outDir, "filebench.md"), report.ToString(), new UTF8Encoding(false));
        return exit;
    }

    // Unlock responsiveness: an Input-priority timer can only tick when nothing above Input is holding the
    // dispatcher, so every gap is one sample of how long a keystroke would have waited after unlock.
    // Counts window-wide LayoutUpdated passes while the stall sampler runs: a render loop that
    // re-arms itself every frame is otherwise invisible (it costs almost no CPU).
    EventHandler benchLayoutProbe;
    // Dispatcher operations the dispatcher had already popped when the thread stopped answering.
    // Those never reach OperationCompleted, so without this list a 13 s freeze leaves no trace: the
    // sampler sees the gap and the hooks see nothing holding the thread.
    internal static List<string> BenchStuckOperations() {
        var names = new List<string>();
        foreach (KeyValuePair<DispatcherOperation,long> item in benchRunning) {
            names.Add(string.Format(CultureInfo.InvariantCulture, "{0} {1} (started +{2:0} ms before unlock, {3:0.0} ms before now)",
                item.Key.Priority, BenchOperationName(item.Key), (item.Value - BenchUnlockTimestamp) * 1000.0 / Stopwatch.Frequency,
                (Stopwatch.GetTimestamp() - item.Value) * 1000.0 / Stopwatch.Frequency));
        }
        // Also count what the queue itself is holding: a long chain of Pending operations at one
        // priority explains a starved heartbeat even when each individual one is short.
        return names;
    }
    // Every still-animating object reachable from the window. A forever DoubleAnimation keeps
    // MediaContext's render timer running at display rate (63 fps here), which starves the Input
    // queue; the bar's own template part can be stopped while a leaked copy is not, so this walks
    // the whole visual tree instead of asking the one instance the code still holds.
    internal static string BenchAnimatedObjects(DependencyObject root) {
        var found = BenchAnimatedObjectsList(root);
        return found.Count == 0 ? "none" : string.Join(" | ", found.ToArray());
    }
    internal static List<string> BenchAnimatedObjectsList(DependencyObject root) {
        var found = new List<string>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        int guard = 0;
        while (pending.Count > 0 && guard++ < 40000) {
            DependencyObject node = pending.Pop();
            var animatable = node as System.Windows.Media.Animation.Animatable;
            if (animatable != null && animatable.HasAnimatedProperties) {
                var element = node as FrameworkElement;
                string name = element == null ? "" : " name=" + (element.Name == null || element.Name.Length == 0 ? "-" : element.Name) + " vis=" + element.Visibility;
                found.Add(node.GetType().Name + name);
            }
            var ui = node as UIElement;
            if (ui != null) {
                // A RenderTransform is a Freezable, not a Visual: recursing into it through
                // VisualTreeHelper throws, which is why the walk only descends real visuals below.
                var transform = ui.RenderTransform;
                var transformTarget = transform as System.Windows.Media.Animation.Animatable;
                if (transformTarget != null && transformTarget.HasAnimatedProperties) found.Add(transform.GetType().Name + "(RenderTransform)");
            }
            var visual = node as System.Windows.Media.Visual;
            if (visual == null) continue;
            int children = System.Windows.Media.VisualTreeHelper.GetChildrenCount(visual);
            for (int i = 0; i < children; i++) pending.Push(System.Windows.Media.VisualTreeHelper.GetChild(visual, i));
        }
        return found;
    }
    // Sampling this deep changes what is being measured: with the probes on, the stall itself did
    // not reproduce (6 runs clean) and with them off the same binary stalled at 562 ms. So the whole
    // block is opt-in, through a marker file next to the exe (the test harness starts the process
    // with CreateProcess and its own environment block, so an environment variable never arrives).
    static bool BenchStallBreakdown() {
        try {
            return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "stall-breakdown.on"));
        } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } catch (ArgumentException) { return false; }
    }
    // How long the UI thread takes to go genuinely idle (nothing left above ApplicationIdle) after
    // the unlock. This is the cheapest statement of the regression that does not add timers of its
    // own: the reported stall is 30 x ~100 ms of deferred work, so an idle frame either arrives in
    // tens of ms (b52) or keeps being pushed back for seconds (current). It still pushes a frame and
    // starts a timer, so it only runs with the breakdown marker on.
    static string MeasureIdleReach(MainWindow window) {
        if (!BenchBreakdown) return "";
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval=TimeSpan.FromSeconds(30) };
        bool timedOut = false;
        timer.Tick += delegate { timedOut = true; timer.Stop(); frame.Continue = false; };
        timer.Start();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { timer.Stop(); frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return string.Format(CultureInfo.InvariantCulture, "- time to ApplicationIdle after unlock: {0:0} ms{1}\n",
            watch.Elapsed.TotalMilliseconds, timedOut ? " (never: 30 s cap)" : "");
    }
    void MeasurePostUnlockStall(MainWindow window, StringBuilder report) {
        // Already resolved once per open by BeginBenchDeferredCapture, so the marker is read on disk
        // only once and the headline measurement never pays for the deep probes.
        bool breakdown = BenchBreakdown;
        benchLayoutProbe = delegate { benchLayoutPasses++; };
        var delays = new List<double>();
        var gapSamples = new List<string>();
        // Same 16 ms heartbeat at three priorities. A gap only in the lower ones means the thread was
        // not blocked but continuously fed with higher-priority work (Render/Loaded above Input), which
        // is what a self-re-posting Render hook looks like from the outside.
        var above = new List<double>();
        var normal = new List<double>();
        var frame = new DispatcherFrame();
        long start = Stopwatch.GetTimestamp(), last = start, lastNormal = start, lastAbove = start;
        // Read the bar's animation state at the moment of unlock, before the window starts: reading it
        // afterwards only proves what the 15 s window left behind, not what it started with.
        string barAtUnlock = "unknown";
        if (breakdown) try {
            System.Windows.Media.TranslateTransform shift = window.fileOpenBarShift;
            System.Windows.Controls.ControlTemplate template = window.fileOpenProgress == null ? null : window.fileOpenProgress.Template;
            System.Windows.Controls.Border part = template == null ? null : template.FindName("IndeterminateSegment", window.fileOpenProgress) as System.Windows.Controls.Border;
            System.Windows.Media.TranslateTransform partShift = part == null ? null : part.RenderTransform as System.Windows.Media.TranslateTransform;
            barAtUnlock = "stored shift animating=" + (shift != null && shift.HasAnimatedProperties ? 1 : 0)
                + ", template part " + (part == null ? "missing" : "vis=" + (part.Visibility == Visibility.Visible ? 1 : 0) + " animating=" + (partShift != null && partShift.HasAnimatedProperties ? 1 : 0))
                + ", same instance " + (shift != null && Object.ReferenceEquals(shift, partShift) ? 1 : 0);
        } catch (InvalidOperationException) { }
        if (breakdown) LayoutUpdated += benchLayoutProbe;
        var timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher) { Interval=TimeSpan.FromMilliseconds(16) };
        // The extra heartbeats are part of the diagnosis, not of the measurement: a 16 ms timer at
        // Normal/Send keeps the dispatcher cycling and masks exactly the starvation they would find,
        // which is why every run measured with them came out clean. They are only started with the
        // breakdown switch on; the headline number uses the single Input timer the report used.
        var normalTimer = breakdown ? new DispatcherTimer(DispatcherPriority.Normal, window.Dispatcher) { Interval=TimeSpan.FromMilliseconds(16) } : null;
        var aboveTimer = breakdown ? new DispatcherTimer(DispatcherPriority.Send, window.Dispatcher) { Interval=TimeSpan.FromMilliseconds(16) } : null;
        timer.Tick += delegate {
            long now = Stopwatch.GetTimestamp();
            double gap = (now - last) * 1000.0 / Stopwatch.Frequency;
            delays.Add(gap);
            // Record what the dispatcher had popped when the gap started: an operation that never
            // completes is exactly the case the hooks cannot report. Resolving those names walks the
            // running map and formats every entry, so it is part of the breakdown only.
            if (breakdown && gap > 100) {
                gapSamples.Add(string.Format(CultureInfo.InvariantCulture, "  - gap {0:0} ms ending at +{1:0} ms; still running: {2}",
                    gap, (now - start) * 1000.0 / Stopwatch.Frequency,
                    BenchStuckOperations().Count == 0 ? "none" : string.Join(" | ", BenchStuckOperations().ToArray())));
            }
            last = now;
            if ((now - start) / (double)Stopwatch.Frequency >= 15.0) { timer.Stop(); if (normalTimer != null) normalTimer.Stop(); if (aboveTimer != null) aboveTimer.Stop(); frame.Continue = false; }
        };
        if (normalTimer != null) normalTimer.Tick += delegate { long now = Stopwatch.GetTimestamp(); normal.Add((now - lastNormal) * 1000.0 / Stopwatch.Frequency); lastNormal = now; };
        if (aboveTimer != null) aboveTimer.Tick += delegate { long now = Stopwatch.GetTimestamp(); above.Add((now - lastAbove) * 1000.0 / Stopwatch.Frequency); lastAbove = now; };
        long cpuBefore;
        int gc2Before = GC.CollectionCount(2), gc0Before = GC.CollectionCount(0);
        // Layout counter: a hook that re-invalidates the window every frame shows up as a counter
        // that keeps climbing even though nothing on screen changes, and it is invisible in CPU
        // time. The read is cheap, but it exists only to feed the breakdown line below, so it is
        // gated with it: without the marker this method does the same single Input timer b52 did.
        int layoutBefore = window.benchLayoutPasses;
        using (var process = Process.GetCurrentProcess()) cpuBefore = process.TotalProcessorTime.Ticks;
        // Frame counter, not a timer: subscribing to CompositionTarget.Rendering does force frames, so
        // this is only attached when the breakdown switch is on.
        int frames = 0;
        EventHandler frameProbe = delegate { frames++; };
        if (breakdown) System.Windows.Media.CompositionTarget.Rendering += frameProbe;
        // Sample the animated set over the same window as the stall: a forever animation can be
        // started and stopped by the code under test, so the "after" read alone can miss it.
        var animatedSeen = new HashSet<string>();
        EventHandler animatedProbe = delegate { foreach (string name in BenchAnimatedObjectsList(window)) animatedSeen.Add(name); };
        if (breakdown) window.LayoutUpdated += animatedProbe;
        timer.Start(); if (normalTimer != null) normalTimer.Start(); if (aboveTimer != null) aboveTimer.Start();
        // Background sampler: the UI thread cannot report what it is inside, and the dispatcher hook
        // clears the record before the next operation can look at it. This thread only reads two
        // volatile fields, so it cannot change the timing it is measuring. A 2 ms poll for 15 s has
        // its own cost on a loaded machine, so it runs only with the breakdown marker on.
        var watched = new List<string>();
        bool watching = breakdown;
        DateTime watchStart = DateTime.UtcNow;
        var watcher = breakdown ? new System.Threading.Thread(new System.Threading.ThreadStart(delegate {
            string lastSeen = null;
            while (watching) {
                string current = BenchCurrentOperation;
                if (current != null && !Object.Equals(current, lastSeen)) {
                    lastSeen = current;
                    lock (watched) { if (watched.Count < 20) watched.Add(string.Format(CultureInfo.InvariantCulture,
                        "  - +{0:0} ms UI thread entered {1}", (DateTime.UtcNow - watchStart).TotalMilliseconds, current)); }
                }
                System.Threading.Thread.Sleep(2);
            }
        })) : null;
        if (watcher != null) { watcher.IsBackground = true; watcher.Start(); }
        Dispatcher.PushFrame(frame);
        watching = false;
        timer.Stop(); if (normalTimer != null) normalTimer.Stop(); if (aboveTimer != null) aboveTimer.Stop();
        LayoutUpdated -= benchLayoutProbe;
        if (breakdown) window.LayoutUpdated -= animatedProbe;
        if (breakdown) report.Append("- animated objects seen during the 15 s: ").Append(animatedSeen.Count == 0 ? "none" : string.Join(" | ", new List<string>(animatedSeen).ToArray())).Append('\n');
        long cpuAfter; using (var process = Process.GetCurrentProcess()) cpuAfter = process.TotalProcessorTime.Ticks;
        double longest = 0; int over100 = 0;
        foreach (double delay in delays) { if (delay > longest) longest = delay; if (delay > 100) over100++; }
        double normalLongest = 0, aboveLongest = 0;
        foreach (double value in normal) if (value > normalLongest) normalLongest = value;
        foreach (double value in above) if (value > aboveLongest) aboveLongest = value;
        report.Append(string.Format(CultureInfo.InvariantCulture, "- post-unlock stall (15 s): samples {0}, longest {1:0.0} ms, over-100ms {2}\n",
            delays.Count, longest, over100));
        foreach (string line in gapSamples) report.Append(line).Append('\n');
        lock (watched) { foreach (string line in watched) report.Append(line).Append('\n'); }
        AppendPostUnlockBreakdown(report);
        if (!breakdown) return;
        // Per-gap CPU: while the 16 ms Input timer is starved, was this process burning CPU (work on
        // this thread, whatever priority) or was the thread descheduled (something outside)? Sampling
        // only at timer turns cannot tell those apart, and they call for opposite fixes.
        int busyGaps = 0, idleGaps = 0; double busyMilliseconds = 0, idleMilliseconds = 0;
        var cpuSamples = new List<double>();
        for (int i = 0; i < 60; i++) {
            long at = Stopwatch.GetTimestamp();
            long before; using (var probe = Process.GetCurrentProcess()) before = probe.TotalProcessorTime.Ticks;
            System.Threading.Thread.Sleep(16);
            long after; using (var probe = Process.GetCurrentProcess()) after = probe.TotalProcessorTime.Ticks;
            double wall = (Stopwatch.GetTimestamp() - at) * 1000.0 / Stopwatch.Frequency;
            cpuSamples.Add((after - before) / 10000.0 / Math.Max(1.0, wall) * 100.0);
        }
        double cpuSum = 0; foreach (double value in cpuSamples) cpuSum += value;
        foreach (double delay in delays) {
            // A gap longer than the heartbeat that the process also filled with CPU is a busy thread;
            // one the process sat idle through is external scheduling or another process.
            if (delay <= 100) continue;
            double processShare = (cpuAfter - cpuBefore) / 10000.0 / 15000.0;
            if (processShare > 0.5) { busyGaps++; busyMilliseconds += delay; } else { idleGaps++; idleMilliseconds += delay; }
        }
        report.Append("- bar animation at unlock: ").Append(barAtUnlock).Append('\n');
        report.Append(string.Format(CultureInfo.InvariantCulture,
            "- post-unlock heartbeat by priority: Normal samples {0} longest {1:0.0} ms; Send samples {2} longest {3:0.0} ms; UI CPU {4:0.0} ms of 15000, Gen0 {5}, Gen2 {6}\n",
            normal.Count, normalLongest, above.Count, aboveLongest, (cpuAfter - cpuBefore) / 10000.0,
            GC.CollectionCount(0) - gc0Before, GC.CollectionCount(2) - gc2Before));
        report.Append(string.Format(CultureInfo.InvariantCulture,
            "- post-stall CPU while sampling (60 x 16 ms, 100% = one core): average {0:0.0}%, over-60%-of-a-core gaps {1} ({2:0.0} ms), under {3} ({4:0.0} ms)\n",
            cpuSum / Math.Max(1, cpuSamples.Count), busyGaps, busyMilliseconds, idleGaps, idleMilliseconds));
        report.Append(string.Format(CultureInfo.InvariantCulture,
            "- repaints after unlock: window layout passes {0}\n",
            window.benchLayoutPasses - layoutBefore));
        report.Append("- animated objects still live: ").Append(BenchAnimatedObjects(window)).Append('\n');
        // The open bar's own state, read from this partial class (the template part and its
        // animation are private to MainWindow). A forever DoubleAnimation keeps MediaContext's
        // render timer running at display rate, which starves the Input queue; an animation that
        // was started and never stopped is the most expensive thing an open can leave behind.
        string barState = "unknown";
        try {
            System.Windows.Controls.ControlTemplate template = window.fileOpenProgress == null ? null : window.fileOpenProgress.Template;
            System.Windows.Controls.Border segment = template == null ? null : template.FindName("IndeterminateSegment", window.fileOpenProgress) as System.Windows.Controls.Border;
            System.Windows.Media.TranslateTransform shift = segment == null ? null : segment.RenderTransform as System.Windows.Media.TranslateTransform;
            barState = "overlay " + (window.fileOpenOverlay == null ? "none" : window.fileOpenOverlay.Visibility.ToString())
                + ", indeterminate " + (window.fileOpenProgress == null ? -1 : (window.fileOpenProgress.IsIndeterminate ? 1 : 0))
                + ", stand-in wanted " + (fileOpenBarStandWanted ? 1 : 0)
                + ", segment " + (segment == null ? "missing" : "vis=" + (segment.Visibility == Visibility.Visible ? 1 : 0) + " animating=" + (shift != null && shift.HasAnimatedProperties ? 1 : 0));
        } catch (InvalidOperationException) { }
        report.Append("- open bar after unlock: ").Append(barState).Append('\n');
        if (frames > 0) report.Append("- frames rendered during the 15 s: ").Append(frames).Append('\n');
    }

    // Names the deferred operations that ran after editing-unlocked, with the offset from unlock.
    // Our own probes ("tables-measure-slice", "unlock-*") and every dispatcher operation that held
    // the thread are both listed, so an unnamed multi-second entry points at WPF itself.
    internal static void AppendPostUnlockBreakdown(StringBuilder report) {
        if (!BenchDeferredActive && BenchUnlockTimestamp == 0) { report.Append("- post-unlock breakdown: not captured\n"); return; }
        int after = 0, before = 0; double afterMilliseconds = 0, held = 0;
        var lines = new List<string>();
        var beforeLines = new List<string>();
        double unlock = BenchUnlockTimestamp;
        foreach (var entry in BenchDeferredEvents) {
            if (entry.Stamp < unlock) {
                before++;
                // The open-phase probes are the other half of the story: they show what the
                // progress bar covered before the document became interactive.
                if (beforeLines.Count < 12 && (entry.Name.StartsWith("unlock-", StringComparison.Ordinal) || entry.Name.StartsWith("tables-measure", StringComparison.Ordinal) || entry.Name.StartsWith("tables-fit", StringComparison.Ordinal)))
                    beforeLines.Add(string.Format(CultureInfo.InvariantCulture, "  - {0:0} ms before  {1} ({2:0.0} ms)\n",
                        (unlock - entry.Stamp) * 1000.0 / Stopwatch.Frequency, entry.Name, entry.Milliseconds));
                continue;
            }
            after++; afterMilliseconds += entry.Milliseconds;
            if (entry.Name.StartsWith("dispatcher", StringComparison.Ordinal)) held = Math.Max(held, entry.Milliseconds);
            if (lines.Count < 40) lines.Add(string.Format(CultureInfo.InvariantCulture, "  - +{0:0} ms  {1} ({2:0.0} ms)\n",
                (entry.Stamp - unlock) * 1000.0 / Stopwatch.Frequency, entry.Name, entry.Milliseconds));
        }
        report.Append(string.Format(CultureInfo.InvariantCulture, "- post-unlock deferred work: {0} entries after unlock ({1:0.0} ms total, longest dispatcher block {2:0.0} ms), {3} before\n",
            after, afterMilliseconds, held, before));
        // Aggregated by name: a hook that re-posts itself thousands of times shows up as one line with a
        // large count, which is the shape a busy-loop looks like in this trace. Only with the breakdown
        // marker on; a default run prints the same per-entry list b52 printed.
        if (BenchBreakdown) {
            var totals = new Dictionary<string,double>();
            var counts = new Dictionary<string,KeyValuePair<int,double>>();
            foreach (var entry in BenchDeferredEvents) {
                if (entry.Stamp < unlock) continue;
                double sum; totals.TryGetValue(entry.Name, out sum); totals[entry.Name] = sum + entry.Milliseconds;
                KeyValuePair<int,double> item; counts.TryGetValue(entry.Name, out item);
                counts[entry.Name] = new KeyValuePair<int,double>(item.Key + 1, Math.Max(item.Value, entry.Milliseconds));
            }
            var names = new List<string>(totals.Keys); names.Sort(delegate(string a, string b) { int by = totals[b].CompareTo(totals[a]); return by != 0 ? by : string.CompareOrdinal(a, b); });
            report.Append("- after-unlock by name (count, total ms, longest ms):\n");
            int shown = 0;
            foreach (string name in names) {
                if (shown++ >= 14) { report.Append("  - ...\n"); break; }
                KeyValuePair<int,double> item = counts[name];
                report.Append(string.Format(CultureInfo.InvariantCulture, "  - {0} x{1} total {2:0.0} longest {3:0.0}\n", name, item.Key, totals[name], item.Value));
            }
        }
        foreach (string line in beforeLines) report.Append(line);
        foreach (string line in lines) report.Append(line);
        if (after > lines.Count) report.Append("  - ...\n");
    }
    internal static int CaptureLivePreview(string file, string png, int caretLine) {
        if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var editor = new SourceEditor { FontFamily=new System.Windows.Media.FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true };
        editor.TextArea.TextView.LineHeight = double.NaN;
        editor.Text = File.ReadAllText(file, Encoding.UTF8);
        var options = new LivePreviewSpike.Options { BaseDirectory=Path.GetDirectoryName(Path.GetFullPath(file)) };
        IDisposable preview = LivePreviewSpike.Attach(editor, options);
        var window = new Window { Left=-20000, Top=0, Width=900, Height=1100, ShowInTaskbar=false, ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
        try {
            window.Show();
            for (int pass = 0; pass < 2; pass++) {
                editor.TextArea.Caret.Line = pass == 0 ? 1 : Math.Max(1, Math.Min(caretLine, editor.Document.LineCount));
                // Let lazy image decoding and table measurement finish.
                for (int i = 0; i < 20; i++) { LongLineBench.Time(delegate { }, editor, DispatcherPriority.ContextIdle); System.Threading.Thread.Sleep(25); }
                editor.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var background = new System.Windows.Media.DrawingVisual();
                using (var context = background.RenderOpen()) context.DrawRectangle(System.Windows.Media.Brushes.White, null, new Rect(0, 0, editor.ActualWidth, editor.ActualHeight));
                bitmap.Render(background);
                bitmap.Render(editor);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                string target = pass == 0 ? png : Path.ChangeExtension(png, null) + "-caret" + caretLine.ToString(CultureInfo.InvariantCulture) + ".png";
                using (var stream = File.Create(target)) encoder.Save(stream);
            }
            return 0;
        } finally {
            preview.Dispose();
            window.Close();
        }
    }

    // P8 gate measurements with the B plan host (generated data only):
    // (1) 12M MD sustained typing with the search bar open: p50/p95/p99 and Gen2 collections;
    // (5) 20 tabs of 1.9M MD opened and closed: private memory before/after (forced GC only
    //     for the labelled "after close" figure).
    internal static int RunGateBench(string outputDirectory) {
        string outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        bool quick = outDir.TrimEnd('\\', '/').EndsWith("-quick", StringComparison.OrdinalIgnoreCase);
        if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        // No Application.Run here: install the dispatcher context so async UI code resumes on this thread as in the app.
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var report = new StringBuilder("# GateBench (live preview on, generated data)\n\n");
        string big = Path.Combine(outDir, "gate-12m.md"), tab = Path.Combine(outDir, "gate-tab.md");
        int exit = 0;
        ProbeDefaults.Live = true;
        MainWindow window = null;
        // Recovery drafts (every 2 s) run during typing, written to a scratch folder: generated data only.
        string recovery = Path.Combine(outDir, "recovery");
        // Folder-name switches isolate causes: "nosearch" keeps the search bar closed, "norecovery" disables drafts.
        bool noSearch = outDir.IndexOf("nosearch", StringComparison.OrdinalIgnoreCase) >= 0;
        if (outDir.IndexOf("norecovery", StringComparison.OrdinalIgnoreCase) < 0) RecoveryStore.TestRoot = recovery;
        try {
            File.WriteAllText(big, LivePreviewBench.GenerateMarkdown(quick ? 120000 : 12000000, 3001), new UTF8Encoding(false));
            File.WriteAllText(tab, LivePreviewBench.GenerateMarkdown(quick ? 19000 : 1900000, 3002), new UTF8Encoding(false));
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=1000, Height=800 };
            window.Show();
            window.Left = -20000;
            LongLineBench.Idle();
            double open = LongLineBench.Time(delegate { window.LoadFileCore(big); }, window, DispatcherPriority.ApplicationIdle);
            if (!window.editing) window.SetMode(true);
            if (!noSearch) { window.ToggleSearch(false); window.query.Text = "생성"; }
            LongLineBench.Idle();
            window.source.CaretIndex = window.source.Document.GetLineByOffset(window.source.Document.TextLength / 2).Offset;
            window.source.TextArea.Caret.BringCaretToView();
            LongLineBench.Idle();
            for (int i = 0; i < 10; i++) LongLineBench.Time(delegate { window.source.TextArea.PerformTextInput("가"); }, window, DispatcherPriority.ContextIdle);
            int gen2 = GC.CollectionCount(2), gen1 = GC.CollectionCount(1);
            var samples = new List<double>();
            int count = 300;
            for (int i = 0; i < count; i++) {
                string text = i % 40 == 39 ? "\n" : "가";
                samples.Add(LongLineBench.Time(delegate { window.source.TextArea.PerformTextInput(text); }, window, DispatcherPriority.ContextIdle));
            }
            samples.Sort();
            report.Append(string.Format(CultureInfo.InvariantCulture,
                "## (1) sustained typing, {0} chars MD, search bar open, recovery drafts on\n\n- open {1:0} ms, live attached {2}\n- {3} inputs: p50 {4:0.00} / p95 {5:0.00} / p99 {6:0.00} / max {7:0.00} ms\n- Gen2 collections during typing: {8}, Gen1: {9}\n- gate: p95 <= 2.5 ms and Gen2 = 0 -> {10}\n\n",
                window.source.Document.TextLength, open, window.LivePreviewAttached, count,
                LongLineBench.Percentile(samples, 0.5), LongLineBench.Percentile(samples, 0.95), LongLineBench.Percentile(samples, 0.99), samples[samples.Count - 1],
                GC.CollectionCount(2) - gen2, GC.CollectionCount(1) - gen1,
                LongLineBench.Percentile(samples, 0.95) <= 2.5 && GC.CollectionCount(2) == gen2 ? "PASS" : "FAIL"));
            for (int i = 0; i < 30; i++) { System.Threading.Thread.Sleep(100); LongLineBench.Idle(); }
            report.Append("- recovery timer enabled: ").Append(window.recoveryTimer.IsEnabled).Append(", last error: ").Append(RecoveryStore.LastError == null ? "none" : RecoveryStore.LastError.GetType().Name).Append("\n");
            report.Append("- recovery files written: ").Append(Directory.Exists(recovery) ? Directory.GetFiles(recovery, "*", SearchOption.AllDirectories).Length : 0).Append("\n\n");
            window.CloseSearch();
            // MD -> TXT -> MD on the large document (async path above 256K chars).
            foreach (bool toPlain in new[] { true, false }) {
                var convert = Stopwatch.StartNew();
                window.ToggleTextMode();
                while (window.PlainText != toPlain && convert.Elapsed.TotalSeconds < 60) { System.Threading.Thread.Sleep(10); LongLineBench.Idle(); }
                LongLineBench.Idle();
                report.Append(string.Format(CultureInfo.InvariantCulture, "- mode switch to {0}: {1:0} ms, done {2}, chars {3}, live attached {4}\n",
                    toPlain ? "TXT" : "MD", convert.Elapsed.TotalMilliseconds, window.PlainText == toPlain, window.source.Document.TextLength, window.LivePreviewAttached));
            }
            report.Append('\n');
            window.saved = window.text;
            // (5) tabs
            long baseline;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using (var process = Process.GetCurrentProcess()) baseline = process.PrivateMemorySize64;
            int tabLength = File.ReadAllText(tab, Encoding.UTF8).Length;
            var opened = new List<DocumentTab>();
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) {
                window.CreateTab();
                window.LoadFileCore(tab);
                opened.Add(window.activeDocument);
                LongLineBench.Idle();
            }
            double openAll = watch.Elapsed.TotalMilliseconds;
            long peak;
            using (var process = Process.GetCurrentProcess()) peak = process.PrivateMemorySize64;
            foreach (DocumentTab state in opened) { window.SwitchTab(state); window.saved = window.text; window.CloseTab(state); LongLineBench.Idle(); }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long after;
            using (var process = Process.GetCurrentProcess()) after = process.PrivateMemorySize64;
            report.Append(string.Format(CultureInfo.InvariantCulture,
                "## (5) 20 tabs x {0} chars MD\n\n- open all {1:0} ms, tabs now {2}\n- private MB: baseline {3:0}, peak {4:0}, after close + forced GC {5:0} (retained {6:0})\n",
                tabLength, openAll, window.documents.Count, baseline / 1048576.0, peak / 1048576.0, after / 1048576.0, (after - baseline) / 1048576.0));
        } catch (Exception ex) {
            exit = 1;
            report.Append("\nFAIL ").Append(ex.GetType().Name).Append(": ").Append(ex.Message.Replace("\n", " ")).Append('\n');
        } finally {
            if (window != null) { window.saved = window.text; window.Close(); }
            ProbeDefaults.Live = false;
            RecoveryStore.TestRoot = null;
            try { if (Directory.Exists(recovery)) Directory.Delete(recovery, true); } catch { }
            try { File.Delete(big); } catch { }
            try { File.Delete(tab); } catch { }
        }
        File.WriteAllText(Path.Combine(outDir, "gate.md"), report.ToString(), new UTF8Encoding(false));
        return exit;
    }

    static bool benchRawToggle;
    // Diagnostics: measure the middle first to separate first-use effects from position effects.
    static readonly bool benchOrderSwap = Environment.GetEnvironmentVariable("MDNOTE_BENCH_ORDER") == "middle-first";
    static readonly StringBuilder diagnostics = new StringBuilder();
    static string MeasureWindow(string file) {
        var window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=1000, Height=800 };
        window.Show();
        window.Left = -20000;
        LongLineBench.Idle();
        var result = new StringBuilder();
        try {
            double open = LongLineBench.Time(delegate { window.LoadFileCore(file); }, window, DispatcherPriority.ApplicationIdle);
            if (!window.editing) window.SetMode(true);
            // Same buffer with the preview detached ("?먮Ц ?몄쭛"), to separate preview cost from host cost.
            if (benchRawToggle) { window.loading = true; window.preview.IsChecked = true; window.loading = false; window.SwitchSurface(); }
            LongLineBench.Time(delegate { }, window, DispatcherPriority.ContextIdle);
            string name = "MainWindow " + (window.PlainText ? "TXT" : window.LivePreviewAttached ? "MD live" : "MD raw") + " " + Path.GetExtension(file);
            int length = window.source.Document.TextLength;
            string[] positions = benchOrderSwap ? new[] { "middle", "start", "end", "longest-line-middle" } : new[] { "start", "middle", "end", "longest-line-middle" };
            bool first = true;
            foreach (string position in positions) {
                int total = window.source.Document.TextLength;
                int target = position == "start" ? 0 : position == "middle" ? total / 2 : total;
                if (position == "longest-line-middle") {
                    ICSharpCode.AvalonEdit.Document.DocumentLine longest = window.source.Document.GetLineByNumber(1);
                    foreach (var candidate in window.source.Document.Lines) if (candidate.Length > longest.Length) longest = candidate;
                    window.source.CaretIndex = longest.Offset + longest.Length / 2;
                } else
                window.source.CaretIndex = window.source.Document.GetLineByOffset(target).Offset;
                window.source.TextArea.Caret.BringCaretToView();
                LongLineBench.Time(delegate { }, window, DispatcherPriority.ContextIdle);
                Action type = delegate { window.source.TextArea.PerformTextInput("가"); };
                for (int i = 0; i < 5; i++) LongLineBench.Time(type, window, DispatcherPriority.ContextIdle);
                var chars = new List<double>();
                MarkdownLivePreview.DiagParses = MarkdownLivePreview.DiagParseTicks = MarkdownLivePreview.DiagDraws = 0;
                window.source.MeasureCalls = 0; window.source.MeasureMilliseconds = 0;
                int built = 0, scrolls = 0, layouts = 0; double sync = 0;
                var view = window.source.TextArea.TextView;
                var builtLines = new List<string>();
                EventHandler<ICSharpCode.AvalonEdit.Rendering.VisualLineConstructionStartEventArgs> onBuild = delegate(object s, ICSharpCode.AvalonEdit.Rendering.VisualLineConstructionStartEventArgs a) { built++; };
                double extentBefore = window.source.ExtentHeight;
                var scrollKinds = new List<string>();
                System.Windows.Controls.ScrollChangedEventHandler onScroll = delegate(object s, System.Windows.Controls.ScrollChangedEventArgs a) {
                    scrolls++;
                    if (scrollKinds.Count < 6) scrollKinds.Add(string.Format(CultureInfo.InvariantCulture, "[src={0} dV={1:0.#} dH={2:0.#} dEw={3:0.#} dEh={4:0.#} dVw={5:0.#} dVh={6:0.#} Ew={7:0.#}]",
                        a.OriginalSource == null ? "-" : a.OriginalSource.GetType().Name, a.VerticalChange, a.HorizontalChange, a.ExtentWidthChange, a.ExtentHeightChange, a.ViewportWidthChange, a.ViewportHeightChange, a.ExtentWidth));
                };
                EventHandler onLayout = delegate { layouts++; };
                view.VisualLineConstructionStarting += onBuild;
                window.source.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent, onScroll);
                window.LayoutUpdated += onLayout;
                Action timed = delegate { var watch = Stopwatch.StartNew(); type(); sync += watch.Elapsed.TotalMilliseconds; };
                var gcMarks = new List<string>();
                for (int i = 0; i < 30; i++) {
                    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
                    chars.Add(LongLineBench.Time(timed, window, DispatcherPriority.ContextIdle));
                    gcMarks.Add(GC.CollectionCount(2) != g2 ? "G2" : GC.CollectionCount(1) != g1 ? "G1" : GC.CollectionCount(0) != g0 ? "G0" : "");
                    if (builtLines.Count < 12 && view.VisualLinesValid) builtLines.Add(window.source.ExtentHeight.ToString("0.#", CultureInfo.InvariantCulture) + "/" + view.VisualLines.Count.ToString(CultureInfo.InvariantCulture) + "/" + (view.VisualLines.Count > 0 ? view.VisualLines[0].Height.ToString("0.#", CultureInfo.InvariantCulture) : "-"));
                }
                view.VisualLineConstructionStarting -= onBuild;
                window.source.RemoveHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent, onScroll);
                window.LayoutUpdated -= onLayout;
                diagnostics.Append(string.Format(CultureInfo.InvariantCulture, "- {0} {1}: per char sync {7:0.00} ms, line builds {8:0.0}, scroll events {9:0.0}, layout passes {10:0.0}, parses {2:0.0} ({3:0.000} ms), bg draws {4:0.0}, editor measures {5:0.0} ({6:0.000} ms)\n",
                    name, position, MarkdownLivePreview.DiagParses / 30.0, MarkdownLivePreview.DiagParseTicks * 1000.0 / Stopwatch.Frequency / 30.0,
                    MarkdownLivePreview.DiagDraws / 30.0, window.source.MeasureCalls / 30.0, window.source.MeasureMilliseconds / 30.0,
                    sync / 30.0, built / 30.0, scrolls / 30.0, layouts / 30.0));
                diagnostics.Append("  extent/visualLines/line1 height after each char: ").Append(string.Join(",", builtLines.ToArray())).Append(string.Format(CultureInfo.InvariantCulture, "; extent {0:0.0} -> {1:0.0}\n", extentBefore, window.source.ExtentHeight)).Append("  scroll events: ").Append(string.Join(" ", scrollKinds.ToArray())).Append('\n');
                var order = new StringBuilder();
                for (int i = 0; i < chars.Count; i++) order.Append(chars[i].ToString("0.0", CultureInfo.InvariantCulture)).Append(gcMarks[i]).Append(' ');
                diagnostics.Append("  samples in order: ").Append(order).Append('\n');
                var enters = new List<double>();
                for (int i = 0; i < 10; i++) enters.Add(LongLineBench.Time(delegate { window.source.TextArea.PerformTextInput("\n"); }, window, DispatcherPriority.ContextIdle));
                chars.Sort();
                enters.Sort();
                long memory;
                using (var process = Process.GetCurrentProcess()) memory = process.PrivateMemorySize64;
                result.Append(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3} | {4:0.0} | {5:0.0} | {6:0.0} | {7} | {8:0} |\n",
                    name, length, first ? open.ToString("0", CultureInfo.InvariantCulture) : "", position,
                    LongLineBench.Percentile(chars, 0.5), LongLineBench.Percentile(chars, 0.95), chars[chars.Count - 1],
                    enters.Count == 0 ? "-" : LongLineBench.Percentile(enters, 0.95).ToString("0.0", CultureInfo.InvariantCulture), memory / 1048576.0));
                first = false;
            }
            var undo = new List<double>();
            for (int i = 0; i < 10; i++) undo.Add(LongLineBench.Time(delegate { window.Undo(); }, window, DispatcherPriority.ContextIdle));
            undo.Sort();
            result.Append(string.Format(CultureInfo.InvariantCulture, "| {0} undo x10 | | | | {1:0.0} | {2:0.0} | {3:0.0} | | |\n",
                name, LongLineBench.Percentile(undo, 0.5), LongLineBench.Percentile(undo, 0.95), undo[undo.Count - 1]));
        } finally {
            window.saved = window.text;
            window.Close();
            LongLineBench.Idle();
        }
        return result.ToString();
    }

    // Input-to-frame latency on a large generated body. The idle measurements above never dispatch
    // user input, so a body that is only slow once the user starts clicking, scrolling or dragging
    // looked fine. Each sample runs the real handler the app runs (wheel / click / PageDown /
    // Ctrl+End / window.Width on the source surface), then waits for the next rendered frame plus
    // a ContextIdle drain, exactly like LongLineBench.Time does for the synthetic typing benches.
    // A second pass samples the UI thread with a 16 ms Input-priority heartbeat so a constant
    // freeze and a per-input slowdown can be told apart.
    internal static int RunInteractionBench(string file, string outputDirectory) {
        if (!Program.IsSelfTest || RecoveryStore.Enabled) throw new InvalidOperationException("Interaction bench requires recovery disabled");
        string generated = null;
        var context = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try {
            // --generated-<N>m picks the generated size in millions of characters (4 by default).
            int millions = 4;
            if (file.StartsWith("--generated-", StringComparison.Ordinal) && file.EndsWith("m", StringComparison.Ordinal)) int.TryParse(file.Substring(12, file.Length - 13), out millions);
            string outDir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(outDir);
            // A case folder owns its document: it holds input.md and the folder name names the
            // surface (case-a..case-d). Without it the bench generates the old 4M body.
            string caseInput = Path.Combine(outDir, "input.md");
            if (File.Exists(caseInput)) return RunInteractionBenchCore(caseInput, outDir);
            generated = Path.Combine(outDir, "interact-" + Guid.NewGuid().ToString("N") + ".md");
            File.WriteAllText(generated, LivePreviewBench.GenerateMarkdown(Math.Max(1, millions) * 1000000, 1001), new UTF8Encoding(false));
            return RunInteractionBenchCore(generated, outDir);
        } finally {
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            if (generated != null) try { File.Delete(generated); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    sealed class InteractionSamples {
        internal string Name;
        internal List<double> Milliseconds;
    }
    static int RunInteractionBenchCore(string file, string outDir) {
        if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        bool previousDefault = ProbeDefaults.Live, previousEditing = alwaysEditing;
        MainWindow window = null;
        var report = new StringBuilder("# InteractionBench (generated data only)\n\n");
        string target = Path.Combine(outDir, "interaction-bench.md");
        int exit = 1;
        // Case mode (folder name "case-a".."case-d", document copied in as input.md): one run
        // measures exactly one surface, so repeated runs of the same case are independent.
        int interactionCase = InteractionCaseFromFolder(outDir);
        bool caseMode = interactionCase != 0;
        bool caseRead = interactionCase == 2 || interactionCase == 3;
        bool caseLive = interactionCase == 3 || interactionCase == 4;
        try {
            ProbeDefaults.Live = caseLive; alwaysEditing = !caseRead;
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=1000, Height=800 };
            window.Show(); window.Left = -20000;
            LongLineBench.Idle();
            // The async open path (queue + drain) is the one the user sees; the drain alone can hold
            // the thread for seconds, so this bench opens through the same queue RunFileBench uses.
            double open = OpenThroughQueue(window, file);
            if (window.fileOpenRunning || window.documentOperationBusy) throw new InvalidOperationException("Open did not finish");
            // A read case turns editing off after the open finished (the same click the user makes),
            // so every action below is measured on the surface a reader actually gets.
            if (caseRead) { if (window.editing) window.SetMode(false); }
            else if (!window.editing) window.SetMode(true);
            LongLineBench.Idle();
            bool sourceShown = window.source.Visibility == Visibility.Visible;
            if (!sourceShown) throw new InvalidOperationException("The source surface is not visible");
            if (caseMode) {
                if (true != caseLive) throw new InvalidOperationException("Case live preview state was not applied");
                if (window.editing == caseRead) throw new InvalidOperationException("Case edit state was not applied");
            } else if (window.PlainText) throw new InvalidOperationException("Interaction bench needs the Markdown source surface");
            string surfaceInfo = "lines " + window.source.Document.LineCount.ToString(CultureInfo.InvariantCulture) + ", preview attached " + (window.LivePreviewAttached ? 1 : 0);
            report.Append("- ").Append(caseMode ? "case " + "abcd"[interactionCase - 1] : "legacy bench")
                .Append(": edit ").Append(window.editing ? "on" : "off").Append(", live ").Append(ProbeDefaults.Live ? "on" : "off").Append('\n');
            report.Append("- generated ").Append(window.source.Document.TextLength).Append(" chars, ").Append(surfaceInfo)
                .Append(", open+drain ").Append(open.ToString("0", CultureInfo.InvariantCulture)).Append(" ms, wrap ").Append(window.wordWrap ? "on" : "off").Append('\n');
            // Every sample stops waiting after this cap and records the cap, so a document that
            // never finishes a frame still produces a number instead of hanging. The live source
            // surface is the only surface the app has now, so case and legacy runs share the rows.
            RunInteractionCaseActions(window, !caseRead, report, target, 12000.0);
            if (!caseMode) AppendBarAnimationCheck(window, file, target, report);
            exit = 0;
            if (!caseMode) {
                // Stall sampler: an Input-priority timer can only tick when nothing above Input holds
                // the dispatcher, so every gap is one sample of how long a keystroke would have waited.
                var stalls = new List<double>();
                var frame = new DispatcherFrame();
                long start = Stopwatch.GetTimestamp(), last = start;
                var timer = new DispatcherTimer(DispatcherPriority.Input, window.Dispatcher) { Interval=TimeSpan.FromMilliseconds(16) };
                timer.Tick += delegate {
                    long now = Stopwatch.GetTimestamp();
                    stalls.Add((now - last) * 1000.0 / Stopwatch.Frequency);
                    last = now;
                    if ((now - start) / (double)Stopwatch.Frequency >= 30.0) { timer.Stop(); frame.Continue = false; }
                };
                timer.Start();
                Dispatcher.PushFrame(frame);
                timer.Stop();
                var stallSorted = new List<double>(stalls); stallSorted.Sort();
                int over100 = 0, over200 = 0; double stallMax = 0;
                foreach (double value in stalls) { if (value > 100) over100++; if (value > 200) over200++; if (value > stallMax) stallMax = value; }
                report.Append(string.Format(CultureInfo.InvariantCulture,
                    "\n- idle stall heartbeat (30 s, 16 ms): samples {0}, p95 {1:0.0} ms, max {2:0.0} ms, over-100ms {3}, over-200ms {4}\n",
                    stalls.Count, LongLineBench.Percentile(stallSorted, .95), stallMax, over100, over200));
            }
        } catch (Exception error) {
            report.Append("\nFAIL ").Append(error.GetType().Name).Append(": ").Append(error.Message.Replace('\n', ' ')).Append('\n');
            exit = 1;
        } finally {
            if (window != null) { window.saved = window.text; window.Close(); LongLineBench.Idle(); }
            ProbeDefaults.Live = previousDefault; alwaysEditing = previousEditing;
        }
        report.Append("\nResult: ").Append(exit == 0 ? "PASS" : "FAIL").Append('\n');
        File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
        return exit;
    }
    // ---- Case mode: one surface per run (edit/read x live off/on) ----
    // The folder name picks the surface, so a repeated run of the same case is independent:
    // "casea" edit + live off, "caseb" read + live off, "casec" read + live on, "cased" edit + live on.
    // An empty result means the legacy bench (the same live-surface rows with the default markup).
    static int InteractionCaseFromFolder(string outDir) {
        string name = Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToLowerInvariant();
        var letters = new StringBuilder(name.Length);
        foreach (char c in name) if (c >= 'a' && c <= 'z') letters.Append(c);
        string token = letters.ToString();
        int at = token.IndexOf("case", StringComparison.Ordinal);
        if (at < 0 || at + 5 > token.Length) return 0;
        char letter = token[at + 4];
        if (letter == 'a') return 1;
        if (letter == 'b') return 2;
        if (letter == 'c') return 3;
        if (letter == 'd') return 4;
        return 0;
    }
    sealed class InteractionActionSamples {
        internal string Name;
        internal readonly List<double> Latency = new List<double>();
        internal readonly List<double> Stall = new List<double>();
        // Offset the first sample moved the surface by. A row whose action did nothing measures
        // an idle frame, not the action, so the report must show it.
        internal double Moved = Double.NaN;
    }
    // The surface the user actually touches: the live host's AvalonEdit editor, the only
    // editing surface the app has now.
    static UIElement InteractionSurface(MainWindow window) { return window.source; }
    static double InteractionExtent(MainWindow window) { return window.source.ExtentHeight; }
    static double InteractionViewport(MainWindow window) { return window.source.ViewportHeight; }
    static void InteractionScrollTo(MainWindow window, double fraction) {
        double maximum = Math.Max(0, InteractionExtent(window) - InteractionViewport(window));
        double offset = maximum * fraction;
        if (offset < 1) offset = 0;
        window.source.ScrollToVerticalOffset(offset);
    }
    // A wheel notch through the real handler: FastScroll is registered on the source editor, and a
    // routed PreviewMouseWheel raised on that element reaches it (the same call live-sync-probe
    // uses). Delta -120 scrolls down, +120 up.
    static void InteractionWheelStep(MainWindow window, int delta) {
        var args = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta);
        args.RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent;
        InteractionSurface(window).RaiseEvent(args);
    }
    static void InteractionPageDown(MainWindow window) {
        window.source.PageDown();
    }
    // Ctrl+End / Ctrl+Home: move the caret to the document edge and bring it into view, which is
    // the whole user-visible effect of the key pair (the app binds End/Home, not the scroll only).
    static void InteractionJump(MainWindow window, bool end) {
        window.source.CaretIndex = end ? window.source.Document.TextLength : 0;
        if (end) window.source.ScrollToEnd(); else window.source.ScrollToHome();
        window.source.TextArea.Caret.BringCaretToView();
    }
    static System.Windows.Controls.Primitives.ScrollBar InteractionVerticalScrollBar(DependencyObject root) {
        if (root == null || !(root is System.Windows.Media.Visual)) return null;
        var bar = root as System.Windows.Controls.Primitives.ScrollBar;
        if (bar != null) {
            if (bar.Orientation == System.Windows.Controls.Orientation.Vertical && bar.IsVisible && bar.Maximum > bar.Minimum) return bar;
            return null;
        }
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) {
            var found = InteractionVerticalScrollBar(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
    // A thumb drag lands at a value, so the bench moves the vertical scrollbar there. WPF only
    // honours that when the ScrollBar is the ScrollViewer's own, so the offset is checked: the
    // fallback is the offset jump a drag produces anyway. Returns true when the bar itself moved
    // the surface.
    static bool InteractionDragScrollBar(MainWindow window, double fraction) {
        var bar = InteractionVerticalScrollBar(InteractionSurface(window));
        if (bar != null) {
            double before = InteractionOffset(window);
            bar.Value = bar.Minimum + (bar.Maximum - bar.Minimum) * fraction;
            if (Math.Abs(InteractionOffset(window) - before) > 0.5) return true;
        }
        InteractionScrollTo(window, fraction);
        return false;
    }
    static void InteractionClickText(MainWindow window) {
        var point = new Point(Math.Max(4, window.source.ActualWidth * 0.5), Math.Max(4, window.source.ActualHeight * 0.4));
        int index = window.source.GetCharacterIndexFromPoint(point, true);
        if (index < 0) return;
        window.source.Select(index, 0);
    }
    static void InteractionDragSelectLines(MainWindow window) {
        var document = window.source.Document;
        var line = document.GetLineByOffset(Math.Min(window.source.CaretIndex, document.TextLength));
        var last = document.GetLineByNumber(Math.Min(document.LineCount, line.LineNumber + 20));
        window.source.Select(line.Offset, Math.Max(0, last.EndOffset - line.Offset));
    }
    // Typing goes through the same entry the app uses: AvalonEdit's PerformTextInput. The hidden
    // desktop has no keyboard focus, so no real key events exist; this is the documented bench
    // substitute.
    static void InteractionPlaceTypingCaret(MainWindow window) {
        window.source.CaretIndex = window.source.Document.TextLength / 2;
    }
    static void InteractionTypeChar(MainWindow window) {
        window.source.TextArea.PerformTextInput("가");
    }
    // Longest single UI-thread block the last action queued: our own handler work inside it is the
    // synchronous part the sample already timed, and every dispatcher operation the action posted
    // (WPF layout, our named probes) is one block. The larger of the two is the freeze the window
    // would have shown.
    static double InteractionLongestBlock() {
        double longest = 0;
        foreach (var entry in BenchDeferredEvents) if (entry.Milliseconds > longest) longest = entry.Milliseconds;
        return longest;
    }
    // Idle with a cap: a document whose layout never finishes must not turn a drain into a hang.
    static double InteractionCaseIdle(double cap) {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval=TimeSpan.FromMilliseconds(cap) };
        var watch = Stopwatch.StartNew();
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }
    static double InteractionOffset(MainWindow window) {
        return window.source.VerticalOffset;
    }
    static void InteractionCaseSample(InteractionActionSamples samples, Action action, MainWindow window, double cap) {
        // Drain first so the previous action's queued work cannot be counted against this one.
        InteractionCaseIdle(cap);
        BenchDeferredEvents.Clear();
        double before = InteractionOffset(window);
        var watch = Stopwatch.StartNew();
        action();
        double sync = watch.Elapsed.TotalMilliseconds;
        double frame = TimedInput(delegate { }, window, cap);
        double block = InteractionLongestBlock();
        samples.Latency.Add(sync + frame);
        samples.Stall.Add(Math.Max(sync, block));
        if (Double.IsNaN(samples.Moved)) samples.Moved = Math.Abs(InteractionOffset(window) - before);
    }
    // Rows are appended as each group finishes (a group can take minutes on a 4M body), so the
    // already-written rows are skipped by index rather than duplicated.
    static void AppendInteractionCaseRows(StringBuilder report, string target, List<InteractionActionSamples> actions, ref int written) {
        while (written < actions.Count) {
            var samples = actions[written++];
            report.Append("| ").Append(samples.Name).Append(" | ").Append(samples.Latency.Count);
            if (samples.Latency.Count == 0) { report.Append(" | - | - | - | - | - |\n"); continue; }
            var latency = new List<double>(samples.Latency); latency.Sort();
            var stall = new List<double>(samples.Stall); stall.Sort();
            report.Append(string.Format(CultureInfo.InvariantCulture, " | {0:0.0} | {1:0.0} | {2:0.0} | {3:0.0} | {4:0} |\n",
                LongLineBench.Percentile(latency, .5), LongLineBench.Percentile(latency, .95), latency[latency.Count - 1], stall[stall.Count - 1],
                Double.IsNaN(samples.Moved) ? "-" : samples.Moved.ToString("0", CultureInfo.InvariantCulture)));
        }
        if (target != null) File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
    }
    // The reader's and the editor's actions on the one surface this case owns. Each row is one
    // input; the latency is input -> next frame (+ ContextIdle drain), the stall is the longest
    // uninterrupted UI-thread block that input queued. Rows are written as each group finishes, so a
    // document that stops finishing frames still leaves the groups before it in the report.
    static void RunInteractionCaseActions(MainWindow window, bool edit, StringBuilder report, string target, double cap) {
        BeginBenchDeferredCapture();
        try {
            // "moved" is the offset change of the first sample: a 0 there means the action did
            // nothing and the row below measures an idle frame, not the input.
            report.Append("| Action | Samples | lat p50 | lat p95 | lat max | stall max | moved px |\n|---|---:|---:|---:|---:|---:|---:|\n");
            File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
            int written = 0;
            var actions = new List<InteractionActionSamples>();
            // The first input on a fresh window pays template and first-use costs.
            InteractionScrollTo(window,0);
            InteractionCaseIdle(cap);
            TimedInput(delegate { InteractionWheelStep(window,-120); }, window, cap);
            TimedInput(delegate { InteractionClickText(window); }, window, cap);
            InteractionCaseIdle(cap);
            foreach (string where in new[] { "top", "middle", "end" }) {
                var wheel = new InteractionActionSamples { Name = "wheel: " + where };
                InteractionScrollTo(window,where == "top" ? 0 : where == "middle" ? 0.5 : 1.0);
                InteractionCaseIdle(cap);
                int delta = where == "end" ? 120 : -120;
                for (int i = 0; i < 3; i++) InteractionCaseSample(wheel, delegate { InteractionWheelStep(window,delta); }, window, cap);
                actions.Add(wheel);
                AppendInteractionCaseRows(report, target, actions, ref written);
            }
            var pageDown = new InteractionActionSamples { Name = "PageDown x10" };
            InteractionScrollTo(window,0);
            InteractionCaseIdle(cap);
            for (int i = 0; i < 10; i++) InteractionCaseSample(pageDown, delegate { InteractionPageDown(window); }, window, cap);
            actions.Add(pageDown);
            AppendInteractionCaseRows(report, target, actions, ref written);
            var drag = new InteractionActionSamples { Name = "scrollbar drag to middle" };
            // The warm-up must land somewhere the samples do not: a pre-call to the same value left
            // the first sample measuring a no-op (caught by the moved column).
            InteractionScrollTo(window,0.05);
            InteractionCaseIdle(cap);
            bool barFound = InteractionDragScrollBar(window,0.02);
            InteractionCaseSample(drag, delegate { InteractionDragScrollBar(window,0.5); }, window, cap);
            InteractionCaseSample(drag, delegate { InteractionDragScrollBar(window,0.1); }, window, cap);
            InteractionCaseSample(drag, delegate { InteractionDragScrollBar(window,0.5); }, window, cap);
            actions.Add(drag);
            AppendInteractionCaseRows(report, target, actions, ref written);
            var click = new InteractionActionSamples { Name = "click on text" };
            for (int i = 0; i < 3; i++) {
                InteractionScrollTo(window,0.3 + i * 0.2);
                InteractionCaseIdle(cap);
                InteractionCaseSample(click, delegate { InteractionClickText(window); }, window, cap);
            }
            actions.Add(click);
            AppendInteractionCaseRows(report, target, actions, ref written);
            var select = new InteractionActionSamples { Name = "drag-select 20 lines" };
            for (int i = 0; i < 3; i++) {
                InteractionScrollTo(window,0.3 + i * 0.2);
                InteractionCaseIdle(cap);
                InteractionClickText(window);
                InteractionCaseIdle(cap);
                InteractionCaseSample(select, delegate { InteractionDragSelectLines(window); }, window, cap);
            }
            actions.Add(select);
            AppendInteractionCaseRows(report, target, actions, ref written);
            var jumpEnd = new InteractionActionSamples { Name = "jump to end (Ctrl+End)" };
            InteractionScrollTo(window,0);
            InteractionCaseIdle(cap);
            InteractionCaseSample(jumpEnd, delegate { InteractionJump(window,true); }, window, cap);
            actions.Add(jumpEnd);
            var jumpHome = new InteractionActionSamples { Name = "jump to home (Ctrl+Home)" };
            InteractionCaseSample(jumpHome, delegate { InteractionJump(window,false); }, window, cap);
            actions.Add(jumpHome);
            AppendInteractionCaseRows(report, target, actions, ref written);
            var resize = new InteractionActionSamples { Name = "window resize" };
            for (int i = 0; i < 3; i++) {
                double width = i % 2 == 0 ? 900 : 1000;
                InteractionCaseSample(resize, delegate { window.Width = width; }, window, cap);
            }
            actions.Add(resize);
            AppendInteractionCaseRows(report, target, actions, ref written);
            if (edit) {
                var typing = new InteractionActionSamples { Name = "typing (each char)" };
                InteractionScrollTo(window,0.5);
                InteractionCaseIdle(cap);
                InteractionPlaceTypingCaret(window);
                InteractionCaseIdle(cap);
                for (int i = 0; i < 30; i++) InteractionCaseSample(typing, delegate { InteractionTypeChar(window); }, window, cap);
                actions.Add(typing);
                AppendInteractionCaseRows(report, target, actions, ref written);
            }
            report.Append("\n- scrollbar drag ");
            if (barFound) report.Append("used the vertical scrollbar (moving its thumb value moved the surface)"); else report.Append("fell back to the offset jump a thumb drag produces (the template ScrollBar value does not drive this surface)");
            report.Append("; typing is simulated input (the hidden desktop has no keyboard focus)\n");
            report.Append("- stall max is the longer of the synchronous handler time and the longest dispatcher block it queued\n");
            File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
        } finally { EndBenchDeferredCapture(); }
    }
    // One input, then the next rendered frame and a ContextIdle drain (the same shape as
    // LongLineBench.Time, so the numbers are comparable with --mainwindow-bench), but the wait is
    // capped so a whole-document layout that never finishes is reported as the cap instead of
    // hanging the bench.
    static double TimedInput(Action action, FrameworkElement window, double capMilliseconds) {
        window.InvalidateVisual();
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        bool seen = false;
        EventHandler rendering = null;
        rendering = delegate {
            if (seen) return;
            seen = true;
            System.Windows.Media.CompositionTarget.Rendering -= rendering;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate { frame.Continue = false; }));
        };
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval=TimeSpan.FromMilliseconds(capMilliseconds) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        System.Windows.Media.CompositionTarget.Rendering += rendering;
        timer.Start();
        try {
            action();
            Dispatcher.PushFrame(frame);
        } finally {
            timer.Stop();
            System.Windows.Media.CompositionTarget.Rendering -= rendering;
        }
        return watch.Elapsed.TotalMilliseconds;
    }
    static void AppendInteractionRow(StringBuilder report, string target, string name, List<double> samples) {
        if (samples == null || samples.Count == 0) {
            report.Append("| ").Append(name).Append(" | 0 | - | - | - |\n");
            if (target != null) File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
            return;
        }
        var sorted = new List<double>(samples); sorted.Sort();
        report.Append(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2:0.0} | {3:0.0} | {4:0.0} |\n",
            name, sorted.Count, LongLineBench.Percentile(sorted, .5), LongLineBench.Percentile(sorted, .95), sorted[sorted.Count - 1]));
        if (target != null) File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
    }
    // The queued open with its background drain, run to the "editing-unlocked" phase.
    static double OpenThroughQueue(MainWindow window, string file) {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval=TimeSpan.FromMinutes(5) };
        bool timedOut = false;
        var watch = Stopwatch.StartNew();
        window.FileOpenMeasurement = delegate(string phase) {
            if (phase == "editing-unlocked") window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
        };
        timer.Tick += delegate { timedOut = true; timer.Stop(); window.CancelFileOpen(); frame.Continue = false; };
        timer.Start();
        window.QueueFileOpen(file);
        Dispatcher.PushFrame(frame);
        timer.Stop();
        window.FileOpenMeasurement = null;
        if (timedOut) throw new InvalidOperationException("Open timed out");
        return watch.Elapsed.TotalMilliseconds;
    }
    // (a) Does the indeterminate bar move while the UI thread is busy? A render-clock animation
    // only paints if a frame is rendered, and a whole-document layout blocks the render pass. The
    // bench samples the segment's on-screen X offset during the open drain through a separate
    // thread polling the visual tree, so a bar that never moves shows up as zero distinct offsets.
    static void AppendBarAnimationCheck(MainWindow window, string file, string target, StringBuilder report) {
        report.Append("\n## (a) indeterminate bar during the open drain\n\n");
        File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
        var window2 = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=1000, Height=800 };
        window2.Show(); window2.Left = -20000;
        LongLineBench.Idle();
        var offsets = new List<double>();
        var indeterminate = new List<bool>();
        long drainStart = 0, drainEnd = 0;
        window2.FileOpenMeasurement = delegate(string phase) {
            if (phase == "first-layout-done") drainStart = Stopwatch.GetTimestamp();
            if (phase == "editing-unlocked") drainEnd = Stopwatch.GetTimestamp();
        };
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval=TimeSpan.FromMinutes(5) };
        timer.Tick += delegate { timer.Stop(); window2.CancelFileOpen(); frame.Continue = false; };
        // The watchdog swaps the steady-state bar for a whole-document layout: exactly the situation
        // the user reports. It runs on the dispatcher at Input, so it can only fire while the drain
        // is between formatting turns (which is when the bar is supposed to keep moving).
        var sampler = new DispatcherTimer(DispatcherPriority.Render) { Interval=TimeSpan.FromMilliseconds(120) };
        sampler.Tick += delegate {
            if (window2.fileOpenProgress == null) return;
            var shift = FindSegmentTransform(window2.fileOpenProgress);
            if (shift != null) { offsets.Add(shift.X); indeterminate.Add(window2.fileOpenProgress.IsIndeterminate); }
        };
        window2.FileOpenMeasurement = delegate(string phase) {
            if (phase == "first-layout-done") { drainStart = Stopwatch.GetTimestamp(); sampler.Start(); }
            if (phase == "editing-unlocked") { drainEnd = Stopwatch.GetTimestamp(); sampler.Stop(); window2.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; })); }
        };
        timer.Start();
        window2.QueueFileOpen(file);
        Dispatcher.PushFrame(frame);
        timer.Stop(); sampler.Stop();
        window2.FileOpenMeasurement = null;
        int distinct = 0; double previous = double.NaN;
        foreach (double value in offsets) { if (Double.IsNaN(previous) || Math.Abs(value - previous) > 0.01) distinct++; previous = value; }
        report.Append(string.Format(CultureInfo.InvariantCulture,
            "- samples during the drain {0}, distinct segment X offsets {1}, indeterminate throughout {2}, drain {3:0} ms\n",
            offsets.Count, distinct, indeterminate.Count > 0 && indeterminate.TrueForAll(delegate(bool value) { return value; }) ? 1 : 0,
            drainEnd != 0 && drainStart != 0 ? (drainEnd - drainStart) * 1000.0 / Stopwatch.Frequency : 0.0));
        File.WriteAllText(target, report.ToString(), new UTF8Encoding(false));
        window2.saved = window2.text; window2.Close(); LongLineBench.Idle();
    }
    static System.Windows.Media.TranslateTransform FindSegmentTransform(System.Windows.Controls.ProgressBar bar) {        var segment = bar.Template == null ? null : bar.Template.FindName("IndeterminateSegment", bar) as System.Windows.Controls.Border;
        return segment == null ? null : segment.RenderTransform as System.Windows.Media.TranslateTransform;
    }
}
}
