using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// --paste-abuse-probe <folder>: seeded paste payloads (plain text, Markdown, CF_HTML, RTF, TSV) into
// every document context (prose start/middle/end, emphasis runs and their edges, list depths,
// quotes, headings, fenced and inline code, table header/body/empty/escaped cells, selections inside
// a line, across lines, across cells and across a table boundary, document start/end), in Basic
// (HideMarkers true) and Live (HideMarkers false) view. After every paste the app's own document and
// history must still satisfy: no exception, literal text inside a fence, a table row keeps its cell
// count and line count, a selection is replaced exactly, emphasis pairs stay usable, the output is
// bounded by the input, one paste is one undo step, and plain text keeps its line count.
// The clipboard is never read or written: the payload is built as a DataObject and driven through
// MainWindow.PasteData, the same entry the Ctrl+V handler uses.
// Report: <folder>\paste-abuse-probe.txt; paste-abuse-probe-PASS.txt only when every case kept
// every invariant. Default run is bounded to about five minutes.
internal static class PasteAbuseProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int FirstSeedDefault = 1, LastSeedDefault = 3, RandomStepsDefault = 30;
    const long DefaultBudgetMilliseconds = 300000;
    const int MaxFailuresPerKey = 2;

    internal sealed class Payload {
        internal string Kind, Text, Html, Rtf, Markdown;
        internal bool NoUnicode;
    }
    internal sealed class CaseInfo {
        internal string Kind;
        internal int Offset, Length, Line;
        internal int TableFirst = -1, TableLast = -1;
        internal int CellLine = -1, CellIndex, CellStart, CellEnd;
        internal bool Cell, CellMulti, Code, Gap, Exact;
    }
    internal sealed class ChangeInfo {
        internal int Offset, Removed, Inserted;
        internal string Text;
    }
    internal sealed class StepContext {
        internal MainWindow Window;
        internal CaseInfo Case;
        internal Payload Payload;
        internal bool Plain;
        internal string PreText, PostText, InsertedText;
        internal DocView PreDoc, PostDoc;
        internal int SelectionStart, SelectionLength;
    }
    internal sealed class FailureInfo {
        internal int Seed, Step;
        internal string Mode, Context, PayloadKind, Action;
        internal string Invariant, Detail, Selection, PayloadExcerpt, Before, After;
    }

    static string outRoot;
    static int currentSeed, currentStep;
    static string currentMode, currentContext, currentPayloadKind, currentAction;
    static string fixture;
    static bool all, budgetReached, dispatcherFailed;
    static bool capturing;
    static long budgetMilliseconds = DefaultBudgetMilliseconds;
    static int firstSeed = FirstSeedDefault, lastSeed = LastSeedDefault, randomSteps = RandomStepsDefault;
    static int modeFilter = -1;   // -1 = both, 0 = basic only, 1 = live only
    static Stopwatch watch;
    static readonly List<Payload> payloads = new List<Payload>();
    static readonly List<string> dispatcherNotes = new List<string>();
    static readonly List<ChangeInfo> changes = new List<ChangeInfo>();
    static readonly List<FailureInfo> failures = new List<FailureInfo>();
    static readonly Dictionary<string,int> failureKeys = new Dictionary<string,int>();
    static readonly Dictionary<string,int> opCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> checkCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> failCounts = new Dictionary<string,int>();
    static EventHandler<DocumentChangeEventArgs> changeHandler;

    internal static int Run(string folder) {
        string dir = Path.GetFullPath(folder);
        Directory.CreateDirectory(dir);
        outRoot = dir;
        string passFile = Path.Combine(dir, "paste-abuse-probe-PASS.txt");
        string failFile = Path.Combine(dir, "paste-abuse-probe-FAIL.txt");
        if (File.Exists(passFile)) File.Delete(passFile);
        if (File.Exists(failFile)) File.Delete(failFile);
        ReadOptions();
        BuildPayloads();
        all = true;
        budgetReached = false;
        watch = new Stopwatch();
        watch.Start();
        var reportBuilder = new StringBuilder();
        bool oldSelf = Program.IsSelfTest;
        bool oldHide = false;
        bool oldLive = ProbeDefaults.Live;
        Dispatcher dispatcher = null;
        DispatcherUnhandledExceptionEventHandler handler = null;
        try {
            Program.IsSelfTest = true;
            oldHide = Static<bool>("hideMarkers");
            ProbeDefaults.Live = true;
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            dispatcher = Application.Current.Dispatcher;
            handler = delegate(object sender, DispatcherUnhandledExceptionEventArgs args) { NoteDispatcher(args); };
            dispatcher.UnhandledException += handler;
            Application.Current.DispatcherUnhandledException += handler;
            RunFocusedChecks(reportBuilder);
            for (int seed = firstSeed; seed <= lastSeed; seed++) {
                for (int mode = 0; mode < 2; mode++) {
                    if (modeFilter >= 0 && modeFilter != mode) continue;
                    if (watch.ElapsedMilliseconds > budgetMilliseconds) { budgetReached = true; break; }
                    if (!RunSequence(seed, mode == 1, reportBuilder)) all = false;
                }
                if (budgetReached) break;
            }
        } catch (Exception ex) {
            all = false;
            reportBuilder.Append("\nFATAL: ").Append(OneLine(ex.ToString())).Append('\n');
        } finally {
            try { if (dispatcher != null && handler != null) dispatcher.UnhandledException -= handler; } catch (Exception) { }
            try { if (Application.Current != null && handler != null) Application.Current.DispatcherUnhandledException -= handler; } catch (Exception) { }
            try { Static("hideMarkers", oldHide); } catch (Exception) { }
            ProbeDefaults.Live = oldLive;
            Program.IsSelfTest = oldSelf;
            watch.Stop();
        }
        string text = ComposeReport(reportBuilder);
        try {
            File.WriteAllText(Path.Combine(dir, "paste-abuse-probe.txt"), text, new UTF8Encoding(false));
            if (all && !budgetReached) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
            else File.WriteAllText(failFile, text, new UTF8Encoding(false));
        } catch (IOException) {
            return 1;
        }
        return all && !budgetReached ? 0 : 1;
    }

    // A reduced run replays one seed/mode; the default (no variables) is the full suite.
    //   MDNOTE_PASTE_ABUSE_SEED=2 MDNOTE_PASTE_ABUSE_MODE=live MDNOTE_PASTE_ABUSE_STEPS=0
    //   MDNOTE_PASTE_ABUSE_BUDGET=600000
    static void ReadOptions() {
        firstSeed = FirstSeedDefault; lastSeed = LastSeedDefault; randomSteps = RandomStepsDefault;
        budgetMilliseconds = DefaultBudgetMilliseconds; modeFilter = -1;
        string value = Environment.GetEnvironmentVariable("MDNOTE_PASTE_ABUSE_SEED");
        int number;
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) {
            firstSeed = number; lastSeed = number;
        }
        value = Environment.GetEnvironmentVariable("MDNOTE_PASTE_ABUSE_STEPS");
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 0) randomSteps = number;
        value = Environment.GetEnvironmentVariable("MDNOTE_PASTE_ABUSE_BUDGET");
        if (!String.IsNullOrEmpty(value) && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out budgetMilliseconds) && budgetMilliseconds <= 0) budgetMilliseconds = DefaultBudgetMilliseconds;
        value = Environment.GetEnvironmentVariable("MDNOTE_PASTE_ABUSE_MODE");
        if (!String.IsNullOrEmpty(value)) {
            if (String.Equals(value, "basic", StringComparison.OrdinalIgnoreCase)) modeFilter = 0;
            else if (String.Equals(value, "live", StringComparison.OrdinalIgnoreCase)) modeFilter = 1;
        }
    }

    static bool RunSequence(int seed, bool liveMode, StringBuilder reportBuilder) {
        currentSeed = seed;
        currentMode = liveMode ? "live" : "basic";
        fixture = Fixture(seed);
        currentStep = 0;
        currentContext = "open";
        currentPayloadKind = "-";
        currentAction = "open";
        dispatcherFailed = false;
        MainWindow window = null;
        string surface = "(not opened)";
        int cases = 0;
        var timer = Stopwatch.StartNew();
        bool runOk = true;
        try {
            window = CreateWindow();
            AttachChanges(window);
            window.SetText(fixture);
            window.SetMode(true);
            Pump();
            Invoke(window, "SetLiveMode", liveMode);
            Pump();
            window.UpdateLayout();
            Invoke(window, "FlushDocumentChanges");
            bool code = Prop<bool>(window, "CodeMode");
            bool editing = Field<bool>(window, "editing");
            bool attached = Prop<bool>(window, "LivePreviewAttached");
            bool hide = Static<bool>("hideMarkers");
            surface = "code=" + code + ", editing=" + editing + ", attached=" + attached +
                ", live=" + (!hide) + ", sourceVisible=" + (Source(window) != null && Source(window).Visibility == Visibility.Visible);
            if (!code || !editing || !attached || hide != !liveMode) {
                FailOpen("a:open", "the edit surface did not attach: " + surface);
                runOk = false;
            } else {
                List<CaseInfo> contexts;
                try {
                    contexts = BuildContexts(window, DocText(window));
                } catch (Exception ex) {
                    FailOpen("a:fixture", OneLine(ex.Message));
                    return false;
                }
                runOk = RunCases(window, contexts, seed, liveMode, ref cases);
            }
            Invoke(window, "FlushDocumentChanges");
        } catch (Exception ex) {
            FailOpen("a:open", "seed exception: " + OneLine(ex.ToString()));
            runOk = false;
        } finally {
            CloseWindow(window);
            timer.Stop();
        }
        reportBuilder.Append("\n## seed ").Append(seed).Append(' ').Append(currentMode).Append('\n');
        reportBuilder.Append("- cases: ").Append(cases).Append('\n');
        reportBuilder.Append("- surface: ").Append(surface).Append('\n');
        reportBuilder.Append("- run time: ").Append(timer.ElapsedMilliseconds).Append(" ms\n");
        int count = CountFailures(seed, currentMode);
        reportBuilder.Append("- failures: ").Append(count).Append('\n');
        return runOk && count == 0 && !dispatcherFailed;
    }

    static bool RunCases(MainWindow window, List<CaseInfo> contexts, int seed, bool liveMode, ref int cases) {
        int index = 0;
        for (int c = 0; c < contexts.Count; c++) {
            for (int j = 0; j < 2; j++) {
                if (OutOfBudget()) return false;
                Payload payload = payloads[(c * 2 + j + seed * 7) % payloads.Count];
                if (!RunCase(window, contexts[c], payload, false, seed, index++)) { }
                cases++;
            }
        }
        // Every payload in one of the four most exposed contexts, so each format meets prose,
        // emphasis, a table cell and a fence at least once.
        int[] focus = new int[] { IndexOf(contexts, "para-middle"), IndexOf(contexts, "bold-inside"), IndexOf(contexts, "table-body"), IndexOf(contexts, "fence-content") };
        for (int p = 0; p < payloads.Count; p++) {
            if (OutOfBudget()) return false;
            Payload payload = payloads[p];
            bool plain = payload.Html != null && (p % 17) == 0;
            RunCase(window, contexts[focus[p % focus.Length]], payload, plain, seed, index++);
            cases++;
        }
        var random = new Random(seed * 486187739 + (liveMode ? 31 : 17));
        for (int step = 0; step < randomSteps; step++) {
            if (OutOfBudget()) return false;
            CaseInfo context = contexts[random.Next(contexts.Count)];
            Payload payload = (step % 2) == 0 ? payloads[random.Next(payloads.Count)] : RandomPayload(random);
            bool plain = random.Next(9) == 0;
            RunCase(window, context, payload, plain, seed, index++);
            cases++;
        }
        return true;
    }

    static bool OutOfBudget() {
        if (watch.ElapsedMilliseconds <= budgetMilliseconds) return false;
        budgetReached = true;
        all = false;
        return true;
    }
    static int IndexOf(List<CaseInfo> contexts, string kind) {
        for (int i = 0; i < contexts.Count; i++) if (String.Equals(contexts[i].Kind, kind, StringComparison.Ordinal)) return i;
        throw new InvalidOperationException("probe: context '" + kind + "' is missing");
    }

    static bool RunCase(MainWindow window, CaseInfo context, Payload payload, bool plain, int seed, int step) {
        currentStep = step;
        currentContext = context.Kind;
        currentPayloadKind = payload.Kind;
        currentAction = context.Kind + "/" + payload.Kind + (plain ? "/plain" : "");
        Bump(opCounts, "context:" + context.Kind);
        Bump(opCounts, "payload:" + payload.Kind);
        if (context.Offset < 0) return true;
        try {
            window.SetText(fixture);
            Pump();
            Invoke(window, "FlushDocumentChanges");
        } catch (Exception ex) {
            return FailOpen("a:reset", OneLine(ex.ToString()));
        }
        string preText = DocText(window);
        int start = Math.Min(context.Offset, preText.Length);
        int length = Math.Min(context.Length, preText.Length - start);
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return FailOpen("a:fixture", "the source editor is gone");
        source.Select(start, length);
        Pump();
        DocumentHistory history = HistoryOf(window);
        history.Clear();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        // The hidden-marker views snap a caret that was asked for inside a marker to the nearest
        // visible edge (MarkdownLivePreview.ActiveChanged -> SnapHiddenCaret/NormalizeTableCaret/
        // SnapEscapedPipeCaret, the documented caret stops of the Basic view); the paste acts on
        // the editor's real selection, so the checks use the selection the editor ended up with,
        // not the requested offset. This is the user's actual pre-paste selection, not a relaxed
        // expected value.
        start = source.SelectionStart;
        length = source.SelectionLength;
        preText = DocText(window);
        var ctx = new StepContext();
        ctx.Window = window;
        ctx.Case = context;
        ctx.Payload = payload;
        ctx.Plain = plain;
        ctx.PreText = preText;
        ctx.PreDoc = Build(preText);
        ctx.SelectionStart = start;
        ctx.SelectionLength = length;
        changes.Clear();
        Exception error = null;
        capturing = true;
        try {
            Invoke(window, "PasteData", MakeData(payload), plain);
            Pump();
        } catch (Exception ex) {
            error = ex;
        } finally {
            capturing = false;
        }
        if (error != null) return Fail(ctx, "a:exception", error.GetType().Name + ": " + OneLine(error.Message));
        if (dispatcherFailed) return Fail(ctx, "a:dispatcher", LastDispatcherNote());
        try { Invoke(window, "FlushDocumentChanges"); } catch (Exception) { }
        ctx.PostText = DocText(window);
        ctx.PostDoc = Build(ctx.PostText);
        ctx.InsertedText = ConcatInserted();
        bool ok = true;
        if (!CheckText(ctx)) ok = false;
        if (!CheckReplace(ctx)) ok = false;
        if (!CheckTableShape(ctx)) ok = false;
        if (!CheckCodeLiteral(ctx)) ok = false;
        if (!CheckPlainLines(ctx)) ok = false;
        if (!CheckGrowth(ctx)) ok = false;
        if (!CheckMarkers(ctx)) ok = false;
        if (!CheckUndo(ctx)) ok = false;
        return ok;
    }

    static DataObject MakeData(Payload payload) {
        var data = new DataObject();
        if (!payload.NoUnicode && payload.Text != null) data.SetData(DataFormats.UnicodeText, payload.Text);
        if (payload.Html != null) data.SetData(DataFormats.Html, payload.Html);
        if (payload.Rtf != null) data.SetData(DataFormats.Rtf, payload.Rtf);
        if (payload.Markdown != null) MarkdownClipboard.Set(data, payload.Markdown);
        return data;
    }

    static void NoteDispatcher(DispatcherUnhandledExceptionEventArgs args) {
        dispatcherFailed = true;
        args.Handled = true;
        if (dispatcherNotes.Count >= 20) return;
        string note = args.Exception == null ? "unknown" : args.Exception.GetType().Name + ": " + OneLine(args.Exception.Message);
        dispatcherNotes.Add("seed " + currentSeed + " " + currentMode + " " + currentAction + ": " + note);
    }
    static string LastDispatcherNote() {
        return dispatcherNotes.Count == 0 ? "unhandled dispatcher exception" : dispatcherNotes[dispatcherNotes.Count - 1];
    }

    static bool Fail(StepContext ctx, string invariant, string detail) {
        Bump(failCounts, invariant);
        all = false;
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Mode = currentMode;
        failure.Step = currentStep;
        failure.Context = currentContext;
        failure.PayloadKind = currentPayloadKind;
        failure.Action = currentAction;
        failure.Invariant = invariant;
        failure.Detail = OneLine(detail);
        if (ctx != null) {
            failure.Selection = ctx.SelectionStart + "+" + ctx.SelectionLength;
            failure.PayloadExcerpt = PayloadExcerpt(ctx.Payload, ctx.Plain);
            int line = LineOf(ctx.PreDoc, ctx.SelectionStart);
            failure.Before = Excerpt(ctx.PreDoc, line);
            failure.After = Excerpt(ctx.PostDoc, line);
        } else {
            failure.Selection = "(none)";
            failure.PayloadExcerpt = "(none)";
            failure.Before = "(none)";
            failure.After = "(none)";
        }
        string key = invariant + "|" + failure.Mode + "|" + failure.Context + "|" + failure.PayloadKind;
        int seen;
        if (!failureKeys.TryGetValue(key, out seen)) seen = 0;
        failureKeys[key] = seen + 1;
        if (seen < MaxFailuresPerKey) failures.Add(failure);
        return false;
    }
    static bool FailOpen(string invariant, string detail) {
        Bump(failCounts, invariant);
        all = false;
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Mode = currentMode;
        failure.Step = currentStep;
        failure.Context = currentContext;
        failure.PayloadKind = currentPayloadKind;
        failure.Action = currentAction;
        failure.Invariant = invariant;
        failure.Detail = OneLine(detail);
        failure.Selection = "(none)";
        failure.PayloadExcerpt = "(none)";
        failure.Before = "(none)";
        failure.After = "(none)";
        string key = invariant + "|" + failure.Mode + "|" + failure.Context;
        int seen;
        if (!failureKeys.TryGetValue(key, out seen)) seen = 0;
        failureKeys[key] = seen + 1;
        if (seen < MaxFailuresPerKey) failures.Add(failure);
        return false;
    }
    static int CountFailures(int seed, string mode) {
        int count = 0;
        for (int i = 0; i < failures.Count; i++) if (failures[i].Seed == seed && String.Equals(failures[i].Mode, mode, StringComparison.Ordinal)) count++;
        return count;
    }
    static string PayloadExcerpt(Payload payload, bool plain) {
        if (payload == null) return "(none)";
        string value = payload.Text != null ? payload.Text : payload.Html != null ? payload.Html : payload.Rtf != null ? payload.Rtf : payload.Markdown;
        return "kind=" + payload.Kind + (plain ? " plain" : "") + " text=[" + Short(value, 220) + "]";
    }

    // ---------------------------------------------------------------- report
    static string ComposeReport(StringBuilder runs) {
        var text = new StringBuilder();
        text.Append("# PasteAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- seeds: ").Append(firstSeed).Append("..").Append(lastSeed).Append(", ").Append(randomSteps)
            .Append(" random pastes per seed/mode plus the coverage matrix\n");
        text.Append("- modes: basic (HideMarkers true), live (HideMarkers false)\n");
        text.Append("- fixtures: generated here from the seed; no user file is read\n");
        text.Append("- payloads: ").Append(payloads.Count).Append(" catalog kinds plus seeded generated ones\n");
        text.Append("- surface: offscreen MainWindow, source editor, MainWindow.PasteData with a DataObject; clipboard untouched\n");
        text.Append("- runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms")
            .Append(budgetReached ? " (budget reached; run incomplete)" : "").Append('\n');
        if (dispatcherNotes.Count > 0) {
            text.Append("\n## Dispatcher exceptions\n");
            foreach (string note in dispatcherNotes) text.Append("- ").Append(note).Append('\n');
        }
        text.Append("\n## Operations\n");
        AppendCounts(text, opCounts);
        text.Append("\n## Invariant checks\n");
        AppendCounts(text, checkCounts);
        text.Append("\n## Invariant failures\n");
        AppendCounts(text, failCounts);
        text.Append(runs.ToString());
        text.Append("\n## Failure groups\n");
        if (failureKeys.Count == 0) {
            text.Append("- none\n");
        } else {
            var keys = new List<string>(failureKeys.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string key in keys) text.Append("- ").Append(key).Append(": ").Append(failureKeys[key]).Append('\n');
        }
        text.Append("\n## Failures\n");
        if (failures.Count == 0) {
            text.Append("- none\n");
        } else {
            foreach (FailureInfo failure in failures) {
                text.Append("\n### ").Append(failure.Invariant).Append(" seed ").Append(failure.Seed).Append(' ').Append(failure.Mode)
                    .Append(", ").Append(failure.Action).Append(", step ").Append(failure.Step).Append('\n');
                text.Append("- detail: ").Append(failure.Detail).Append('\n');
                text.Append("- payload: ").Append(failure.PayloadExcerpt).Append('\n');
                text.Append("- selection: ").Append(failure.Selection).Append('\n');
                text.Append("- before:\n").Append(failure.Before).Append('\n');
                text.Append("- after:\n").Append(failure.After).Append('\n');
            }
        }
        int occurrences = 0;
        foreach (KeyValuePair<string,int> pair in failureKeys) occurrences += pair.Value;
        text.Append("\nDistinct failures: ").Append(failureKeys.Count).Append(" (occurrences ").Append(occurrences)
            .Append(", first ").Append(MaxFailuresPerKey).Append(" of each shown)\n");
        text.Append("\nResult: ").Append(all && !budgetReached ? "PASS" : "FAIL").Append('\n');
        text.Append("Runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms\n");
        return text.ToString();
    }
    static void AppendCounts(StringBuilder text, Dictionary<string,int> map) {
        if (map.Count == 0) { text.Append("- (none)\n"); return; }
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys) text.Append("- ").Append(key).Append(": ").Append(map[key]).Append('\n');
    }
    // ---------------------------------------------------------------- focused checks
    // Deterministic rules the seeded payloads cannot pin down, run once before them: the TXT table
    // cell split, the HTML table cell pipe and the paste emphasis scan. A failure is reported like
    // any other invariant, with seed 0 and mode "focused", so the seeded summaries keep their own
    // counts.
    static void RunFocusedChecks(StringBuilder reportBuilder) {
        reportBuilder.Append("\n## focused checks\n");
        MainWindow window = null;
        try {
            window = CreateWindow();
            CheckPlainTableCells(reportBuilder);
            CheckPlainReferences(reportBuilder);
            CheckPlainRenderingRules(reportBuilder);
            CheckHtmlTableCode(reportBuilder, window);
            CheckPasteEmphasisLink(reportBuilder, window);
        } catch (Exception ex) {
            FocusedFail("focused-exception", OneLine(ex.ToString()));
            reportBuilder.Append("- focused-exception: FAIL ").Append(OneLine(ex.Message)).Append('\n');
        } finally {
            try { if (window != null) Invoke(window, "ArmPasteEmphasis", new object[] { null }); } catch (Exception) { }
            CloseWindow(window);
        }
    }
    static bool Focused(string check, bool pass, string detail, StringBuilder report) {
        Bump(checkCounts, "f:" + check);
        if (pass) report.Append("- ").Append(check).Append(": PASS\n");
        else report.Append("- ").Append(check).Append(": FAIL ").Append(OneLine(detail)).Append('\n');
        if (!pass) FocusedFail(check, detail);
        return pass;
    }
    static void FocusedFail(string check, string detail) {
        currentSeed = 0; currentMode = "focused"; currentStep = 0;
        currentContext = check; currentPayloadKind = "-"; currentAction = "focused/" + check;
        FailOpen("f:" + check, detail);
    }
    // A row ending in "\\|" is a literal backslash and a live closing pipe, and a pipe inside a
    // multi-backtick code span is cell text: both keep the header's column count
    // (MarkdownLivePreview.TablePipes, which the TXT converter follows), so no empty column is
    // written. Before that fix the first row carried an extra empty cell and the second split
    // "``x|y``" in two.
    static void CheckPlainTableCells(StringBuilder report) {
        string closed = MarkdownPlainText.Convert("| C:\\\\|\n|---|");
        Focused("txt-table-closing-pipe", closed == "C:\\", "txt=[" + Short(closed, 200) + "]", report);
        string code = MarkdownPlainText.Convert("| a ``x|y`` b | c |\n|---|---|\n| 1 | 2 |");
        Focused("txt-table-code-span", code == "a x|y b\tc\n1\t2", "txt=[" + Short(code, 200) + "]", report);
    }
    // The escaped-id reference resolves exactly like the renderer: Markdown.Para expands references
    // after ProtectEscapes (MDNote.cs), so a shortcut id carrying escapes is not found there either.
    // TXT must not drift from that oracle in either direction.
    static void CheckPlainReferences(StringBuilder report) {
        string markdown = "[\\*foo\\*][ref] 그리고 [\\*foo\\*]\n\n[ref]: https://example.com/x\n\n[\\*foo\\*]: https://example.com/y";
        string oracle = OracleText(markdown);
        string txt = MarkdownPlainText.Convert(markdown);
        Focused("txt-reference-escaped-id", txt == oracle, "oracle=[" + Short(oracle, 300) + "] txt=[" + Short(txt, 300) + "]", report);
    }
    // The CommonMark/GFM reading of the TXT converter's block and inline rules, checked against
    // the text a CommonMark viewer shows rather than against the print oracle (Markdown.ParseCore
    // is being moved onto the same MarkdownPlainText.AtxHeading/TableStart/DecodeEntities helpers):
    // the ATX closing "#" is structure only after a space, named and numeric entities decode, a
    // link target keeps balanced parentheses and hides its title, a shortcut reference with a
    // definition is a link, a pipe-less "---" under "a | b" is a setext heading, and a data row's
    // extra cells are dropped.
    static void CheckPlainRenderingRules(StringBuilder report) {
        PlainRule("txt-heading-c-hash", "### C#", "C#", report);
        PlainRule("txt-heading-closing-sequence", "## 제목 ##", "제목", report);
        PlainRule("txt-heading-trailing-hash", "# foo#", "foo#", report);
        PlainRule("txt-heading-close-inside-text", "### foo ### b", "foo ### b", report);
        PlainRule("txt-heading-hashes-only", "### ###", "", report);
        PlainRule("txt-heading-escaped-hash", "# foo \\#", "foo #", report);
        PlainRule("txt-entity-named", "&copy; 2026 &mdash; &hellip; &rarr; &middot; &times; &deg;", "© 2026 — … → · × °", report);
        PlainRule("txt-entity-numeric", "&#169; &#x2014;", "© —", report);
        PlainRule("txt-entity-apos", "&apos;", "'", report);
        PlainRule("txt-link-balanced-parens", "[위키](https://ko.wikipedia.org/wiki/대한민국_(동음이의))", "위키", report);
        PlainRule("txt-link-title", "[설계서](https://example.com/spec \"설계서 3장\")", "설계서", report);
        PlainRule("txt-link-title-with-paren", "[a](/x 'ti)tle')", "a", report);
        PlainRule("txt-image-paren", "![그림](img_(1).png)", "[이미지: 그림]", report);
        PlainRule("txt-reference-shortcut", "[설계 문서] 참고.\n[설계 문서]: https://example.com/spec", "설계 문서 참고.\n", report);
        PlainRule("txt-reference-paren", "[문서][a]\n[a]: https://en.wikipedia.org/wiki/A_(b)", "문서\n", report);
        PlainRule("txt-table-pipe-less-underline", "이름 | 나이\n---", "이름 | 나이", report);
        PlainRule("txt-table-pipe-less-single", "| a |\n---", "| a |", report);
        PlainRule("txt-table-extra-cells", "| a | b |\n| --- | --- |\n| 1 | 2 | 3 |", "a\tb\n1\t2", report);
        PlainRule("txt-table-short-delimiter", "| a | b |\n| --- |", "| a | b |\n| --- |", report);
    }
    static void PlainRule(string check, string markdown, string expected, StringBuilder report) {
        string txt = MarkdownPlainText.Convert(markdown);
        Focused(check, txt == expected, "expected=[" + Short(expected, 200) + "] txt=[" + Short(txt, 200) + "]", report);
    }
    static string OracleText(string markdown) {
        MethodInfo readable = typeof(MainWindow).GetMethod("ReadableText", Members);
        if (readable == null) throw new MissingMemberException("probe: MainWindow has no method 'ReadableText'");
        System.Windows.Documents.FlowDocument document = Markdown.Parse(markdown, 16, "Malgun Gothic", System.Windows.Media.Brushes.Black, false, "");
        return (string)readable.Invoke(null, new object[] { document });
    }
    // A pasted <code>a | b</code> cell keeps its pipe unescaped: the renderer's table splitter skips
    // code spans, and it never hides an escape inside one, so "`a \| b`" would draw the backslash.
    static void CheckHtmlTableCode(StringBuilder report, MainWindow window) {
        string html = "<table><tr><th>a</th><th>b</th></tr><tr><td><code>x | y</code></td><td>c</td></tr></table>";
        string converted = (string)Invoke(window, "ClipboardMarkdown", html);
        string expected = "| a | b |\n| --- | --- |\n| `x | y` | c |";
        Focused("html-table-code-pipe", converted == expected, "html=[" + Short(converted, 200) + "]", report);
    }
    // A rich paste reads the caret's line to decide whether it is inside italic/bold. A marker
    // inside a link target is hidden markup and must not open a run (MarkdownLivePreview.ParseInline
    // skips the "](url)" tail), so a caret after "[a](http://x/_y)" is outside every run and an
    // italic paste keeps its "*"; the caret inside "_z_" is still inside the run.
    static void CheckPasteEmphasisLink(StringBuilder report, MainWindow window) {
        string markdown = "[a](http://x/_y) and _z_";
        window.SetText(markdown);
        window.SetMode(true);
        Pump();
        Invoke(window, "FlushDocumentChanges");
        SourceEditor source = Source(window);
        int after = markdown.IndexOf("and ", StringComparison.Ordinal) + 1;
        source.Select(after, 0);
        Pump();
        int at = source.SelectionStart;
        Invoke(window, "ArmPasteEmphasis", source);
        bool inside = MainWindow.PasteItalicSuppressed;
        Focused("paste-italic-link-target", at == after && !inside, "caret=" + at + "/" + after + ", italicSuppressed=" + inside, report);
        int inRun = markdown.IndexOf("_z_", StringComparison.Ordinal) + 2;
        source.Select(inRun, 0);
        Pump();
        Invoke(window, "ArmPasteEmphasis", source);
        bool control = MainWindow.PasteItalicSuppressed;
        Focused("paste-italic-inside-run", source.SelectionStart == inRun && control, "caret=" + source.SelectionStart + "/" + inRun + ", italicSuppressed=" + control, report);
    }
    static string Excerpt(DocView doc, int line) {
        if (doc == null) return "(none)";
        int first, last;
        if (line < 0) {
            first = 0;
            last = Math.Min(doc.Count - 1, 8);
        } else {
            first = Math.Max(0, line - 5);
            last = Math.Min(doc.Count - 1, line + 6);
        }
        var text = new StringBuilder();
        for (int i = first; i <= last; i++) {
            text.Append(i + 1).Append("| ").Append(doc.Lines[i]).Append('\n');
        }
        string value = text.ToString();
        if (value.Length > 2200) value = value.Substring(0, 2200) + "...";
        return value;
    }
    static string Short(string value, int limit) {
        if (value == null) return "<null>";
        string text = OneLine(value);
        return text.Length <= limit ? text : text.Substring(0, limit) + "...";
    }
    static string OneLine(string value) {
        if (value == null) return "<null>";
        string text = value.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n").Replace("\t", "\\t");
        return text.Length <= 1200 ? text : text.Substring(0, 1200) + "...";
    }
    static void Bump(Dictionary<string,int> map, string key) {
        int value;
        if (!map.TryGetValue(key, out value)) value = 0;
        map[key] = value + 1;
    }
    static string ConcatInserted() {
        var text = new StringBuilder();
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            if (!String.IsNullOrEmpty(change.Text)) text.Append(change.Text);
        }
        return text.ToString();
    }

    // ---------------------------------------------------------------- dispatcher and reflection
    static void Pump() {
        LongLineBench.Idle();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
        LongLineBench.Idle();
    }
    static MainWindow CreateWindow() {
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Width = 900, Height = 700 };
        window.Show();
        window.Left = -20000;
        return window;
    }
    static void AttachChanges(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        changeHandler = delegate(object sender, DocumentChangeEventArgs args) {
            if (!capturing) return;
            var change = new ChangeInfo();
            change.Offset = args.Offset;
            change.Removed = args.RemovalLength;
            change.Inserted = args.InsertionLength;
            change.Text = args.InsertedText == null ? "" : args.InsertedText.Text;
            changes.Add(change);
        };
        source.Document.Changed += changeHandler;
    }
    static void DetachChanges(MainWindow window) {
        if (changeHandler == null || window == null) return;
        try {
            SourceEditor source = Source(window);
            if (source != null && source.Document != null) source.Document.Changed -= changeHandler;
        } catch (Exception) { }
        changeHandler = null;
    }
    static void CloseWindow(MainWindow window) {
        if (window == null) return;
        try {
            DetachChanges(window);
            Invoke(window, "FlushDocumentChanges");
            Set(window, "documentOperationBusy", false);
            Set(window, "saved", Prop<string>(window, "text"));
            Set(window, "recoveredDocument", false);
            window.Close();
        } catch (Exception) { }
    }
    static SourceEditor Source(MainWindow window) { return Field<SourceEditor>(window, "source"); }
    static MarkdownLivePreview.Session Session(MainWindow window) { return Field<MarkdownLivePreview.Session>(window, "livePreview"); }
    static DocumentHistory HistoryOf(MainWindow window) { return Prop<DocumentHistory>(window, "History"); }
    static string DocText(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return "";
        return source.Document.Text;
    }
    static FieldInfo RequireField(string name) {
        FieldInfo field = typeof(MainWindow).GetField(name, Members);
        if (field == null) throw new MissingMemberException("probe: MainWindow has no field '" + name + "'");
        return field;
    }
    static T Field<T>(MainWindow window, string name) { return (T)RequireField(name).GetValue(window); }
    static void Set(MainWindow window, string name, object value) { RequireField(name).SetValue(window, value); }
    static T Prop<T>(MainWindow window, string name) {
        PropertyInfo property = typeof(MainWindow).GetProperty(name, Members);
        if (property == null) throw new MissingMemberException("probe: MainWindow has no property '" + name + "'");
        return (T)property.GetValue(window, null);
    }
    static object Invoke(MainWindow window, string name, params object[] args) {
        MethodInfo method = typeof(MainWindow).GetMethod(name, Members);
        if (method == null) throw new MissingMemberException("probe: MainWindow has no method '" + name + "'");
        return method.Invoke(window, args);
    }
    static T Static<T>(string name) { return (T)RequireField(name).GetValue(null); }
    static void Static(string name, object value) { RequireField(name).SetValue(null, value); }
    // ---------------------------------------------------------------- fixture and contexts
    static string Fixture(int seed) {
        var text = new StringBuilder();
        Line(text, "# 표제 제목 " + seed.ToString(CultureInfo.InvariantCulture));
        Line(text, "");
        Line(text, "첫 번째 문단 시작 **굵은글자** 중간 *기울임글자* 그리고 `인라인코드` 끝.");
        Line(text, "둘째 문단 줄입니다.");
        Line(text, "");
        Line(text, "> 인용 첫 줄");
        Line(text, "> 인용 둘째 줄");
        Line(text, "");
        Line(text, "- 일 단계 항목");
        Line(text, "  - 이 단계 항목");
        Line(text, "    - 삼 단계 항목");
        Line(text, "- [ ] 미완료 항목");
        Line(text, "");
        Line(text, "## 소제목 " + seed.ToString(CultureInfo.InvariantCulture));
        Line(text, "");
        Line(text, "```text");
        Line(text, "코드 첫 줄 **그대로**");
        Line(text, "코드 둘째 줄 <b>tag</b>");
        Line(text, "```");
        Line(text, "");
        Line(text, "| 이름 | 상태 | 비고 |");
        Line(text, "| --- | :--: | --: |");
        Line(text, "| 김철수 | 진행 중 | 값 |");
        Line(text, "|  |  |  |");
        Line(text, "| a \\| b | `c|d` | 마지막 |");
        Line(text, "");
        Line(text, "표 아래 문단입니다.");
        Line(text, "마지막 줄.");
        return text.ToString();
    }
    static void Line(StringBuilder text, string value) {
        text.Append(value).Append('\n');
    }

    static List<CaseInfo> BuildContexts(MainWindow window, string text) {
        DocView doc = Build(text);
        var list = new List<CaseInfo>();
        int line;
        line = FindLine(doc, "## 소제목");
        Add(list, "heading", doc.Starts[line] + 3, 0, line);
        line = FindLine(doc, "둘째 문단 줄입니다.");
        Add(list, "para-start", doc.Starts[line], 0, line);
        Add(list, "para-middle", doc.Starts[line] + Math.Max(0, doc.Lines[line].Length / 2), 0, line);
        Add(list, "para-end", LineEnd(doc, line), 0, line);
        Add(list, "doc-start", 0, 0, 0);
        Add(list, "doc-end", text.Length, 0, doc.Count - 1);
        line = FindLine(doc, "> 인용 첫 줄");
        Add(list, "quote", doc.Starts[line] + 2, 0, line);
        line = FindLine(doc, "- 일 단계 항목");
        Add(list, "list-depth-0", doc.Starts[line] + 2, 0, line);
        line = FindLine(doc, "  - 이 단계 항목");
        Add(list, "list-depth-1", doc.Starts[line] + 4, 0, line);
        line = FindLine(doc, "    - 삼 단계 항목");
        Add(list, "list-depth-2", doc.Starts[line] + 6, 0, line);
        line = FindLine(doc, "- [ ] 미완료 항목");
        Add(list, "task-list", doc.Starts[line] + 6, 0, line);
        int proseLine = FindLine(doc, "첫 번째 문단 시작");
        string prose = doc.Lines[proseLine];
        Add(list, "bold-inside", InLine(doc, proseLine, prose.IndexOf("**굵은글자**", StringComparison.Ordinal) + 3), 0, proseLine);
        Add(list, "bold-edge-open", InLine(doc, proseLine, prose.IndexOf("**굵은글자**", StringComparison.Ordinal) + 2), 0, proseLine);
        Add(list, "bold-edge-close", InLine(doc, proseLine, prose.IndexOf("**굵은글자**", StringComparison.Ordinal) + 2 + "굵은글자".Length), 0, proseLine);
        int italic = prose.IndexOf("*기울임글자*", StringComparison.Ordinal);
        Add(list, "italic-inside", InLine(doc, proseLine, italic + 2), 0, proseLine);
        Add(list, "italic-edge-open", InLine(doc, proseLine, italic + 1), 0, proseLine);
        Add(list, "italic-edge-close", InLine(doc, proseLine, italic + 1 + "기울임글자".Length), 0, proseLine);
        Add(list, "inline-code", InLine(doc, proseLine, prose.IndexOf("`인라인코드`", StringComparison.Ordinal) + 2), 0, proseLine);
        line = FindLine(doc, "코드 첫 줄 **그대로**");
        Add(list, "fence-content", doc.Starts[line] + 2, 0, line).Code = true;
        line = FindLine(doc, "코드 둘째 줄 <b>tag</b>");
        Add(list, "fence-tag", doc.Starts[line] + 3, 0, line).Code = true;

        List<TableInfo> tables = FindTables(doc);
        TableInfo table = null;
        for (int i = 0; i < tables.Count; i++) if (doc.Lines[tables[i].Header].IndexOf("이름", StringComparison.Ordinal) >= 0) { table = tables[i]; break; }
        if (table == null) throw new InvalidOperationException("fixture: the marker table was not found");
        AddCell(list, doc, table, table.Header, 0, "table-header-cell", 0, 0);
        AddCell(list, doc, table, table.Header, 1, "table-header-middle", 1, 0);
        AddCell(list, doc, table, table.Header + 2, 0, "table-body", 1, 0);
        AddCell(list, doc, table, table.Header + 2, 1, "table-body-cell2", 2, 0);
        AddCell(list, doc, table, table.Header + 2, 2, "table-body-cell3", 1, 0);
        AddCell(list, doc, table, table.Header + 3, 0, "table-empty", 0, 0);
        AddCell(list, doc, table, table.Header + 4, 0, "table-escaped-pipe", 3, 0);
        AddCell(list, doc, table, table.Header + 4, 1, "table-code-cell", 2, 0);
        AddCell(list, doc, table, table.Header + 4, 2, "table-last-cell", 1, 0);
        AddCell(list, doc, table, table.Header + 2, 0, "table-last-text-end", "김철수".Length, 0);
        AddCell(list, doc, table, table.Header + 2, 0, "sel-table-cell", 0, 2).Exact = true;
        CaseInfo cells = AddCell(list, doc, table, table.Header + 2, 0, "sel-table-cells", 0, 0);
        cells.Exact = false;
        cells.CellMulti = true;
        cells.Length = CellSpan(doc, table.Header + 2, 1) - cells.Offset;
        int gapAbove = table.Header - 1;
        if (gapAbove < 0 || doc.Lines[gapAbove].Length != 0) throw new InvalidOperationException("fixture: no blank line above the table");
        Add(list, "table-gap-above", doc.Starts[gapAbove], 0, gapAbove).Gap = true;
        int gapBelow = table.Last + 1;
        if (gapBelow >= doc.Count || doc.Lines[gapBelow].Length != 0) throw new InvalidOperationException("fixture: no blank line below the table");
        Add(list, "table-gap-below", doc.Starts[gapBelow], 0, gapBelow).Gap = true;

        line = FindLine(doc, "둘째 문단 줄입니다.");
        Add(list, "sel-line", doc.Starts[line] + 3, 4, line);
        int selFrom = InLine(doc, proseLine, prose.IndexOf("중간", StringComparison.Ordinal));
        int selTo = doc.Starts[line] + 4;
        Add(list, "sel-lines", selFrom, selTo - selFrom, proseLine);
        line = FindLine(doc, "코드 첫 줄 **그대로**");
        Add(list, "sel-code", doc.Starts[line] + 2, 4, line).Code = true;
        line = FindLine(doc, "표 아래 문단입니다.");
        Add(list, "sel-table-out", doc.Starts[table.Header + 2] + 1, doc.Starts[line] + 4 - (doc.Starts[table.Header + 2] + 1), table.Header + 2);
        CheckFixtureTable(doc, table);
        return list;
    }
    static CaseInfo Add(List<CaseInfo> list, string kind, int offset, int length, int line) {
        var item = new CaseInfo();
        item.Kind = kind;
        item.Offset = offset;
        item.Length = length;
        item.Line = line;
        item.Exact = true;
        list.Add(item);
        return item;
    }
    static CaseInfo AddCell(List<CaseInfo> list, DocView doc, TableInfo table, int line, int cellIndex, string kind, int delta, int length) {
        var item = new CaseInfo();
        List<MarkdownLivePreview.Cell> cells = MarkdownLivePreview.SplitCells(doc.Lines[line]);
        if (cellIndex < 0 || cellIndex >= cells.Count) throw new InvalidOperationException("fixture: no cell " + cellIndex + " on line " + (line + 1));
        MarkdownLivePreview.Cell cell = cells[cellIndex];
        int position = cell.Start + delta;
        if (position < cell.Start) position = cell.Start;
        if (position > cell.End) position = cell.End;
        item.Kind = kind;
        item.Offset = doc.Starts[line] + position;
        item.Length = length;
        item.Line = line;
        item.Cell = true;
        item.CellLine = line;
        item.CellIndex = cellIndex;
        item.CellStart = doc.Starts[line] + cell.Start;
        item.CellEnd = doc.Starts[line] + cell.End;
        item.TableFirst = table.Header;
        item.TableLast = table.Last;
        item.Exact = length > 0;
        list.Add(item);
        return item;
    }
    static int InLine(DocView doc, int line, int relative) {
        return doc.Starts[line] + relative;
    }
    static int CellSpan(DocView doc, int line, int cellIndex) {
        List<MarkdownLivePreview.Cell> cells = MarkdownLivePreview.SplitCells(doc.Lines[line]);
        if (cellIndex < 0 || cellIndex >= cells.Count) throw new InvalidOperationException("fixture: no cell " + cellIndex + " on line " + (line + 1));
        return doc.Starts[line] + cells[cellIndex].End;
    }
    static int FindLine(DocView doc, string marker) {
        for (int i = 0; i < doc.Count; i++) if (doc.Lines[i].IndexOf(marker, StringComparison.Ordinal) >= 0) return i;
        throw new InvalidOperationException("fixture: no line contains [" + marker + "]");
    }
    static void CheckFixtureTable(DocView doc, TableInfo table) {
        int headerCells = MarkdownLivePreview.SplitCells(doc.Lines[table.Header]).Count;
        if (headerCells == 0) throw new InvalidOperationException("fixture: the table header has no cells");
        for (int i = table.Header; i <= table.Last; i++) {
            int cells = MarkdownLivePreview.SplitCells(doc.Lines[i]).Count;
            if (cells != headerCells) throw new InvalidOperationException("fixture: line " + (i + 1) + " has " + cells + " cells, header " + headerCells);
        }
    }
    // ---------------------------------------------------------------- payloads
    static void BuildPayloads() {
        payloads.Clear();
        Add("text-single", "한 줄 붙여넣기 plain text", null, null, null, false);
        Add("text-multiline-crlf", "첫 줄\r\n둘째 줄\r\n셋째 줄", null, null, null, false);
        Add("text-multiline-lf", "line1\nline2\nline3\nline4", null, null, null, false);
        Add("text-multiline-cr", "carriage\rreturn\rline", null, null, null, false);
        Add("text-tabs", "열1\t열2\t열3", null, null, null, false);
        Add("text-long-line", LongText(2048), null, null, null, false);
        Add("text-emoji", "😀👍🏽🇰🇷 이모지 🙂", null, null, null, false);
        Add("text-cjk", "한국어 日本語 中文 繁體", null, null, null, false);
        Add("text-controls", "\u0001\u0002\u0007\u000B\u000C\u001B\u007F", null, null, null, false);
        Add("text-whitespace", "   \n\t \n  ", null, null, null, false);
        Add("text-empty", "", null, null, null, false);
        Add("text-pipes", "a | b | c", null, null, null, false);
        Add("text-stars", "**굵게** *기울임* ~~취소~~", null, null, null, false);
        Add("text-backticks", "`코드` ``이중``", null, null, null, false);
        Add("text-br", "a<br>b", null, null, null, false);
        Add("md-list", "- 하나\n- 둘\n  - 셋\n- [ ] 할 일", null, null, null, false);
        Add("md-quote", "> 인용\n> 둘째\n\n> 새 인용", null, null, null, false);
        Add("md-table", "| a | b |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |", null, null, null, false);
        Add("md-fence", "```csharp\nint x = 1;\n```", null, null, null, false);
        Add("md-heading", "## 붙인 제목\n\n본문", null, null, null, false);
        Add("md-inline", "**B** *I* `C` [L](https://example.invalid)", null, null, null, false);
        Add("html-browser", "브라우저 굵게 기울임 링크",
            HtmlFragment("<p>브라우저 <b>굵게</b> <i>기울임</i> <a href=\"https://example.invalid/page\">링크</a></p>"), null, null, false);
        Add("html-word", "Word 문서 본문",
            CfHtml("<p class=MsoNormal><b><span style='font-size:14.0pt'>Word </span></b><span style='color:#C00000'>문서</span> 본문</p>"), null, null, false);
        Add("html-docs", "Docs body",
            HtmlFragment("<b style=\"font-weight:normal;\" id=\"docs-internal-guid-1234\"><p dir=\"ltr\"><span style=\"font-size:11pt;color:#000000;font-weight:700;\">Docs</span><span style=\"font-weight:400;\"> body</span></p></b>"), null, null, false);
        Add("html-notion", "Notion bold\none\ntwo\na\nb",
            "<meta charset='utf-8'><div><p><span style=\"color: rgb(55, 53, 47);\">Notion </span><span style=\"font-weight: 600;\">bold</span></p><ul><li>one</li><li>two</li></ul><p>a<br>b</p></div>", null, null, false);
        Add("html-table", "A\tB\r\n1\t2",
            HtmlFragment("<table><tr><td>A</td><td>B</td></tr><tr><td>1</td><td>2</td></tr></table>"), null, null, false);
        // #5: merged cells keep every row on the header's column count; #15: Word list paragraphs
        // become Markdown list items; #4: an RTF backslash is written as "\\" on the source.
        Add("html-merged-table", "제목 a b c",
            HtmlFragment("<table><tr><th colspan=\"2\">제목</th></tr><tr><td rowspan=\"2\">a</td><td>b</td></tr><tr><td>c</td></tr></table>"), null, null, false);
        Add("html-word-list", "첫째 항목 둘째 단계 번호 항목",
            HtmlFragment("<p class=MsoListParagraph style='margin-left:36.0pt;mso-list:l0 level1 lfo1'><![if !supportLists]><span style='mso-list:Ignore'>&#183;<span>&nbsp;</span></span><![endif]>첫째 항목</p>\r\n"
                +"<p class=MsoListParagraph style='margin-left:72.0pt;mso-list:l0 level2 lfo1'><![if !supportLists]><span style='mso-list:Ignore'>o<span>&nbsp;</span></span><![endif]>둘째 단계</p>\r\n"
                +"<p class=MsoListParagraph style='mso-list:l1 level1 lfo2'><![if !supportLists]><span style='mso-list:Ignore'>1.<span>&nbsp;</span></span><![endif]>번호 항목</p>"), null, null, false);
        Add("rtf-backslash", "C:\\path \\[x\\]", null,
            "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Malgun Gothic;}}\\f0\\fs24 C:\\\\path \\\\[x\\\\]}", null, false);
        Add("html-image", "before 그림 after",
            HtmlFragment("<p>before <img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==\" alt=\"그림\"> after</p>"), null, null, false);
        Add("html-br", "a\nb\nc\nd", HtmlFragment("<p>a<br>b<br/>c<br />d</p>"), null, null, false);
        Add("html-entities", "& <tag> \"q\" —",
            HtmlFragment("<p>&amp; &lt;tag&gt; &nbsp; &#45; &#x2014; &quot;q&quot;</p>"), null, null, false);
        Add("html-nested", "깊은 중첩",
            HtmlFragment(NestedHtml("깊은 중첩", 70)), null, null, false);
        Add("html-empty", "", HtmlFragment("<p></p>"), null, null, false);
        Add("html-empty-bold", "ab", HtmlFragment("<p>a<b></b>b</p>"), null, null, false);
        Add("html-space-bold", "a b", HtmlFragment("<p>a<b> </b>b</p>"), null, null, false);
        Add("html-context-bold", "부모 굵게",
            "<b><!--StartFragment-->부모 굵게<!--EndFragment--></b>", null, null, false);
        Add("rtf-simple", "plain bold text",
            null, "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Malgun Gothic;}}\\f0\\fs24 plain \\b bold\\b0{} text}", null, false);
        Add("rtf-styled", "normal italic red end",
            null, "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Malgun Gothic;}}{\\colortbl;\\red208\\green48\\blue48;}\\f0\\fs24 normal \\i italic\\i0{} \\cf1 red\\cf0{} end}", null, false);
        Add("tsv-cells", "이름\t값\t메모\r\n가\t1\t첫째\r\n나\t2\t둘째", null, null, null, false);
        Add("html-browser-notext", "브라우저 굵게",
            HtmlFragment("<p><b>브라우저</b> 굵게</p>"), null, null, true);
        Add("mdnote-markdown", null, null, null, "- 내부\n- 마크다운", false);
    }
    static Payload Add(string kind, string text, string html, string rtf, string markdown, bool noUnicode) {
        Payload payload = Make(kind, text, html, rtf, markdown, noUnicode);
        payloads.Add(payload);
        return payload;
    }
    static Payload Make(string kind, string text, string html, string rtf, string markdown, bool noUnicode) {
        var payload = new Payload();
        payload.Kind = kind;
        payload.Text = text;
        payload.Html = html;
        payload.Rtf = rtf;
        payload.Markdown = markdown;
        payload.NoUnicode = noUnicode;
        return payload;
    }
    static string LongText(int length) {
        const string alphabet = "가나다라마바사아자차카타파하 ABCdef0123456789|*`~<>[]_-+.";
        var text = new StringBuilder(length);
        for (int i = 0; i < length; i++) text.Append(alphabet[i % alphabet.Length]);
        return text.ToString();
    }
    static string HtmlFragment(string body) {
        return "<html><body><!--StartFragment-->" + body + "<!--EndFragment--></body></html>";
    }
    // CF_HTML offsets count UTF-8 bytes from the start of the whole string; every field is ten
    // digits so the header length stays fixed while the values are filled in.
    static string CfHtml(string fragment) {
        const string headTemplate = "Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
        string pre = "<html><body>";
        string post = "</body></html>";
        int header = headTemplate.Length;
        int startFragment = header + Encoding.UTF8.GetByteCount(pre);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = header + Encoding.UTF8.GetByteCount(pre + fragment + post);
        var text = new StringBuilder();
        text.Append("Version:0.9\r\n");
        text.Append("StartHTML:").Append(Pad10(0)).Append("\r\n");
        text.Append("EndHTML:").Append(Pad10(endHtml)).Append("\r\n");
        text.Append("StartFragment:").Append(Pad10(startFragment)).Append("\r\n");
        text.Append("EndFragment:").Append(Pad10(endFragment)).Append("\r\n");
        text.Append(pre).Append(fragment).Append(post);
        return text.ToString();
    }
    static string Pad10(int value) {
        string text = value.ToString(CultureInfo.InvariantCulture);
        return text.Length >= 10 ? text : new string('0', 10 - text.Length) + text;
    }
    static string NestedHtml(string inner, int depth) {
        var text = new StringBuilder();
        for (int i = 0; i < depth; i++) {
            if ((i % 3) == 0) text.Append("<span style=\"font-weight:700;\">");
            else if ((i % 3) == 1) text.Append("<span style=\"color:#2050A0;\">");
            else text.Append("<span style=\"font-size:12pt;\">");
        }
        text.Append(inner);
        for (int i = 0; i < depth; i++) text.Append("</span>");
        return text.ToString();
    }

    static readonly string[] RandomWords = new string[] { "가나다", "라마바", "日本語", "中文", "😀", "a", "b", "값", "x|y", "*별*", "`코드`", "<br>" };
    static Payload RandomPayload(Random random) {
        int kind = random.Next(7);
        if (kind == 0) {
            const string alphabet = "ab 가나|*`~<>\\[]_#-+.\t";
            int length = 1 + random.Next(90);
            var text = new StringBuilder(length);
            for (int i = 0; i < length; i++) text.Append(alphabet[random.Next(alphabet.Length)]);
            return Make("rand-mixed", text.ToString(), null, null, null, false);
        }
        if (kind == 1) {
            int lines = 1 + random.Next(6);
            var text = new StringBuilder();
            for (int i = 0; i < lines; i++) {
                if (i > 0) text.Append(random.Next(3) == 0 ? "\r\n" : random.Next(3) == 0 ? "\r" : "\n");
                text.Append(RandomWords[random.Next(RandomWords.Length)]);
            }
            return Make("rand-multiline", text.ToString(), null, null, null, false);
        }
        if (kind == 2) {
            string word = RandomWords[random.Next(RandomWords.Length)];
            string[] blocks = new string[] {
                "- " + word + "\n- 둘째\n  - 셋째",
                "> " + word + "\n> " + word,
                "| " + word + " | 값 |\n| --- | --- |\n| 1 | 2 |",
                "```\n" + word + "\n```",
                "### " + word,
                "**" + word + "** `" + word + "`"
            };
            return Make("rand-markdown", blocks[random.Next(blocks.Length)], null, null, null, false);
        }
        if (kind == 3) {
            int depth = random.Next(0, 72);
            string inner = RandomWords[random.Next(RandomWords.Length)];
            string body = NestedHtml(inner, depth);
            if (random.Next(4) == 0) body = "<p>" + body + "<br>끝</p>";
            string html = random.Next(2) == 0 ? HtmlFragment(body) : CfHtml(body);
            return Make("rand-html", inner, html, null, null, false);
        }
        if (kind == 4) {
            int rows = 1 + random.Next(4);
            var text = new StringBuilder();
            for (int r = 0; r < rows; r++) {
                if (r > 0) text.Append("\r\n");
                int columns = 1 + random.Next(4);
                for (int c = 0; c < columns; c++) {
                    if (c > 0) text.Append('\t');
                    text.Append(RandomWords[random.Next(RandomWords.Length)]);
                }
            }
            return Make("rand-tsv", text.ToString(), null, null, null, false);
        }
        if (kind == 5) {
            string word = RandomWords[random.Next(RandomWords.Length)];
            string rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Malgun Gothic;}}{\\colortbl;\\red208\\green48\\blue48;}\\f0\\fs24 " +
                word + " \\b bold\\b0{} \\i italic\\i0{} \\cf1 red\\cf0{} }";
            return Make("rand-rtf", word + " bold italic red", null, rtf, null, false);
        }
        return Make("rand-long", LongText(200 + random.Next(1800)), null, null, null, false);
    }
    // ---------------------------------------------------------------- invariants
    static bool CheckText(StepContext ctx) {
        Bump(checkCounts, "text");
        string mirror = null;
        try { mirror = Prop<string>(ctx.Window, "text"); } catch (Exception) { mirror = null; }
        if (mirror == null || !String.Equals(mirror, ctx.PostText, StringComparison.Ordinal)) {
            return Fail(ctx, "text", "window.text differs from the editor document: " + FirstDiff(ctx.PostText, mirror));
        }
        return true;
    }
    // A paste replaces the selection and nothing else. A caret in a table cell may be clamped to
    // the cell's own text start, so that case checks the whole cell span instead; a selection that
    // reaches across cells keeps the pipes on purpose, so only the text outside the table is held.
    static bool CheckReplace(StepContext ctx) {
        Bump(checkCounts, "b:replace");
        string pre = ctx.PreText, post = ctx.PostText;
        CaseInfo item = ctx.Case;
        if (item.CellMulti) {
            int tableStart = ctx.PreDoc.Starts[item.TableFirst];
            int tableEnd = LineEnd(ctx.PreDoc, item.TableLast);
            return CheckOutside(ctx, tableStart, tableEnd);
        }
        if (item.Cell && ctx.SelectionLength == 0) {
            int cellStart, cellEnd;
            if (CellBoundsAt(pre, ctx.SelectionStart, out cellStart, out cellEnd)) {
                return CheckOutside(ctx, cellStart, cellEnd);
            }
        }
        int start = Math.Min(ctx.SelectionStart, pre.Length);
        int end = Math.Min(start + ctx.SelectionLength, pre.Length);
        string prefix = pre.Substring(0, start);
        string suffix = pre.Substring(end);
        bool ok = true;
        if (!post.StartsWith(prefix, StringComparison.Ordinal)) {
            ok = Fail(ctx, "b:replace", "text before the selection changed: " + FirstDiff(prefix, post));
        }
        if (!post.EndsWith(suffix, StringComparison.Ordinal)) {
            string tail = post.Length >= suffix.Length ? post.Substring(post.Length - suffix.Length) : post;
            ok = Fail(ctx, "b:replace", "text after the selection changed: " + FirstDiff(suffix, tail));
        }
        return ok;
    }
    static bool CheckOutside(StepContext ctx, int keepFrom, int keepTo) {
        string pre = ctx.PreText, post = ctx.PostText;
        int from = Math.Min(keepFrom, pre.Length);
        int to = Math.Min(keepTo, pre.Length);
        bool ok = true;
        string prefix = pre.Substring(0, from);
        string suffix = pre.Substring(to);
        if (!post.StartsWith(prefix, StringComparison.Ordinal)) {
            ok = Fail(ctx, "b:replace", "text before the target cell/table changed: " + FirstDiff(prefix, post));
        }
        if (!post.EndsWith(suffix, StringComparison.Ordinal)) {
            string tail = post.Length >= suffix.Length ? post.Substring(post.Length - suffix.Length) : post;
            ok = Fail(ctx, "b:replace", "text after the target cell/table changed: " + FirstDiff(suffix, tail));
        }
        return ok;
    }
    // The owning cell of an offset, after any caret snap: a caret in a row's padding belongs to
    // the nearest cell, exactly the rule PasteInCell's ClampIntoCell uses.
    static bool CellBoundsAt(string text, int offset, out int cellStart, out int cellEnd) {
        cellStart = cellEnd = -1;
        DocView doc = Build(text);
        if (doc.Count == 0) return false;
        int line = LineOf(doc, Math.Max(0, Math.Min(offset, text.Length)));
        List<MarkdownLivePreview.Cell> cells = MarkdownLivePreview.SplitCells(doc.Lines[line]);
        if (cells.Count == 0) return false;
        int relative = offset - doc.Starts[line];
        MarkdownLivePreview.Cell cell = cells[cells.Count - 1];
        for (int i = 0; i < cells.Count; i++) {
            if (relative <= cells[i].End) { cell = cells[i]; break; }
        }
        cellStart = doc.Starts[line] + cell.Start;
        cellEnd = doc.Starts[line] + cell.End;
        return true;
    }
    static bool CheckTableShape(StepContext ctx) {
        CaseInfo item = ctx.Case;
        if (!item.Cell && !item.CellMulti) return true;
        Bump(checkCounts, "b:table");
        DocView pre = ctx.PreDoc, post = ctx.PostDoc;
        if (pre.Count != post.Count) {
            return Fail(ctx, "b:table", "a table-cell paste changed the document line count " + pre.Count + " -> " + post.Count);
        }
        int first = item.TableFirst, last = item.TableLast;
        if (first < 0 || last >= pre.Count || last >= post.Count) {
            return Fail(ctx, "b:table", "the target table is outside the document (" + first + ".." + last + ")");
        }
        // A payload that writes its own unescaped "|" names the user's delimiters: typing or pasting
        // a pipe starts the next cell (a multi-cell paste writes its text at the first cell's start,
        // not through CellInsertText's escaping), so the row it lands in gains cells on purpose.
        // Only the line(s) the paste replaces are exempt from the column comparison; every other
        // line of the table must keep its cells. HTML/RTF-derived text is judged by the conversion,
        // so only a text payload (or one pasted as plain text) carries this allowance.
        string pipeText = PayloadPipeText(ctx.Payload, ctx.Plain);
        bool pipePayload = pipeText != null && HasPipe(pipeText);
        int touchedFirst = LineOf(pre, Math.Max(0, Math.Min(ctx.SelectionStart, pre.Text.Length)));
        int touchedLast = LineOf(pre, Math.Max(0, Math.Min(ctx.SelectionStart + ctx.SelectionLength, pre.Text.Length)));
        int columns = MarkdownLivePreview.SplitCells(pre.Lines[first]).Count;
        for (int i = first; i <= last; i++) {
            bool touched = pipePayload && i >= touchedFirst && i <= touchedLast;
            int before = MarkdownLivePreview.SplitCells(pre.Lines[i]).Count;
            int after = MarkdownLivePreview.SplitCells(post.Lines[i]).Count;
            if (!touched && after != before) {
                return Fail(ctx, "b:table", "line " + (i + 1) + " has " + after + " cells, was " + before + ": [" + post.Lines[i] + "]");
            }
            if (before != columns) {
                return Fail(ctx, "b:table", "the target table was not rectangular before the paste (line " + (i + 1) + ")");
            }
        }
        if (!String.Equals(pre.Lines[first + 1], post.Lines[first + 1], StringComparison.Ordinal)) {
            return Fail(ctx, "b:table", "the delimiter row changed: [" + pre.Lines[first + 1] + "] -> [" + post.Lines[first + 1] + "]");
        }
        if (pre.Starts[first] != post.Starts[first]) {
            return Fail(ctx, "b:table", "the table header start moved " + pre.Starts[first] + " -> " + post.Starts[first]);
        }
        MarkdownLivePreview.Session session = Session(ctx.Window);
        SourceEditor source = Source(ctx.Window);
        if (session == null || source == null || source.Document == null) return true;
        if (source.Document.LineCount < last + 1) return Fail(ctx, "b:table", "the preview has fewer lines than the text model");
        // A pipe written into the header row makes it wider than its delimiter row, and GFM no longer
        // reads the pair as a table at all (MarkdownLivePreview.NextIsDelimiter): the block is
        // ordinary text from there, so there is no table shape left to compare. A header whose cell
        // count did not move keeps the ordinary preview checks.
        bool headerTouched = pipePayload && touchedFirst <= first && touchedLast >= first &&
            MarkdownLivePreview.SplitCells(post.Lines[first]).Count != columns;
        for (int i = first; i <= last; i++) {
            if (headerTouched) continue;
            MarkdownLivePreview.LineInfo info = session.Info(source.Document.GetLineByNumber(i + 1));
            bool touched = pipePayload && i >= touchedFirst && i <= touchedLast;
            if (info == null || !info.Table) {
                if (touched) continue;
                return Fail(ctx, "b:table", "the preview no longer reads line " + (i + 1) + " as a table line: [" + post.Lines[i] + "]");
            }
            if (i == first + 1) {
                if (!info.Delimiter) return Fail(ctx, "b:table", "the preview no longer reads the delimiter row as one: [" + post.Lines[i] + "]");
            } else if (!touched && (info.Cells == null || info.Cells.Count != columns)) {
                return Fail(ctx, "b:table", "the preview splits line " + (i + 1) + " into " + (info.Cells == null ? -1 : info.Cells.Count) + " cells, was " + columns + ": [" + post.Lines[i] + "]");
            }
        }
        return true;
    }
    // The text a paste writes verbatim: the plain-text clipboard (also when a richer format is forced
    // to text) or the Markdown clipboard. An HTML/RTF paste is converted by the app, so the payload
    // text is not what lands in the cell and the conversion keeps its strict check.
    static string PayloadPipeText(Payload payload, bool plain) {
        if (payload == null) return null;
        if (plain) return payload.Text;
        if (payload.Html != null || payload.Rtf != null) return null;
        return payload.Markdown != null ? payload.Markdown : payload.Text;
    }
    // Text inside a fenced code block is source, not markup: the exact clipboard text goes in.
    static bool CheckCodeLiteral(StepContext ctx) {
        if (!ctx.Case.Code) return true;
        Bump(checkCounts, "b:code");
        string expected;
        if (ctx.Plain) expected = ctx.Payload.NoUnicode ? null : ctx.Payload.Text;
        else if (ctx.Payload.Markdown != null) expected = ctx.Payload.Markdown;
        else expected = ctx.Payload.NoUnicode ? null : ctx.Payload.Text;
        if (expected == null) {
            if (!String.Equals(ctx.PreText, ctx.PostText, StringComparison.Ordinal)) {
                return Fail(ctx, "b:code", "a format-only payload changed the fenced code text by " + (ctx.PostText.Length - ctx.PreText.Length) + " characters");
            }
            return true;
        }
        int start = Math.Min(ctx.SelectionStart, ctx.PreText.Length);
        int end = Math.Min(start + ctx.SelectionLength, ctx.PreText.Length);
        string prefix = ctx.PreText.Substring(0, start);
        string suffix = ctx.PreText.Substring(end);
        if (!ctx.PostText.StartsWith(prefix, StringComparison.Ordinal) || !ctx.PostText.EndsWith(suffix, StringComparison.Ordinal)) {
            return Fail(ctx, "b:code", "the text around the fenced-code paste changed: " + FirstDiff(ctx.PreText, ctx.PostText));
        }
        int length = ctx.PostText.Length - (ctx.PreText.Length - (end - start));
        if (length < 0) return Fail(ctx, "b:code", "the fenced code text shrank by more than the selection");
        string got = ctx.PostText.Substring(start, length);
        if (!String.Equals(CanonicalNewlines(got), CanonicalNewlines(expected), StringComparison.Ordinal)) {
            return Fail(ctx, "b:code", "text inside a fence was converted instead of inserted literally: got=[" + Short(got, 240) + "], want=[" + Short(expected, 240) + "]");
        }
        return true;
    }
    // A plain-text paste keeps its own line count: N lines in, N lines inserted. Table cells turn
    // each line into "<br>" and the blank separator beside a table adds the table's own newline,
    // so those two contexts are exempt.
    static bool CheckPlainLines(StepContext ctx) {
        CaseInfo item = ctx.Case;
        if (!ctx.Plain && (ctx.Payload.Html != null || ctx.Payload.Rtf != null || ctx.Payload.Markdown != null)) return true;
        if (ctx.Payload.Text == null) return true;
        if (ctx.Plain && ctx.Payload.NoUnicode) return true;
        if (item.Cell || item.CellMulti || item.Gap) return true;
        Bump(checkCounts, "b:lines");
        int start = Math.Min(ctx.SelectionStart, ctx.PreText.Length);
        int end = Math.Min(start + ctx.SelectionLength, ctx.PreText.Length);
        if (!ctx.PostText.StartsWith(ctx.PreText.Substring(0, start), StringComparison.Ordinal) ||
            !ctx.PostText.EndsWith(ctx.PreText.Substring(end), StringComparison.Ordinal)) return true;   // b:replace reports this
        int length = ctx.PostText.Length - (ctx.PreText.Length - (end - start));
        if (length < 0) return true;
        string inserted = ctx.PostText.Substring(start, length);
        int want = CountNewlines(CanonicalNewlines(ctx.Payload.Text));
        int got = CountNewlines(CanonicalNewlines(inserted));
        if (want != got) {
            return Fail(ctx, "b:lines", "a plain-text paste of " + want + " line break(s) inserted " + got + ": [" + Short(inserted, 240) + "]");
        }
        return true;
    }
    static bool CheckGrowth(StepContext ctx) {
        Bump(checkCounts, "b:growth");
        long inserted = 0;
        for (int i = 0; i < changes.Count; i++) inserted += changes[i].Inserted;
        long input = 0;
        if (ctx.Payload.Text != null) input = Math.Max(input, ctx.Payload.Text.Length);
        if (ctx.Payload.Html != null) input = Math.Max(input, ctx.Payload.Html.Length);
        if (ctx.Payload.Rtf != null) input = Math.Max(input, ctx.Payload.Rtf.Length);
        if (ctx.Payload.Markdown != null) input = Math.Max(input, ctx.Payload.Markdown.Length);
        long bound = input * 4 + 200;
        if (inserted > bound) {
            return Fail(ctx, "b:growth", "the paste inserted " + inserted + " characters for an input of " + input + " (bound " + bound + ")");
        }
        return true;
    }
    static bool CheckUndo(StepContext ctx) {
        Bump(checkCounts, "g:undo");
        if (String.Equals(ctx.PreText, ctx.PostText, StringComparison.Ordinal)) {
            Bump(checkCounts, "g:noop");
            return true;
        }
        DocumentHistory history = HistoryOf(ctx.Window);
        if (!history.CanUndo) return Fail(ctx, "g:undo", "a changing paste left no undo entry");
        try {
            Invoke(ctx.Window, "Undo");
            Pump();
            Invoke(ctx.Window, "FlushDocumentChanges");
            string undone = DocText(ctx.Window);
            if (!String.Equals(undone, ctx.PreText, StringComparison.Ordinal)) {
                return Fail(ctx, "g:undo", "one undo did not restore the pre-paste text: " + FirstDiff(ctx.PreText, undone));
            }
            Invoke(ctx.Window, "Redo");
            Pump();
            Invoke(ctx.Window, "FlushDocumentChanges");
            string redone = DocText(ctx.Window);
            if (!String.Equals(redone, ctx.PostText, StringComparison.Ordinal)) {
                return Fail(ctx, "g:undo", "one redo did not restore the post-paste text: " + FirstDiff(ctx.PostText, redone));
            }
        } catch (Exception ex) {
            return Fail(ctx, "g:undo", OneLine(ex.ToString()));
        }
        return true;
    }
    static int CountNewlines(string value) {
        if (value == null) return 0;
        int count = 0;
        for (int i = 0; i < value.Length; i++) if (value[i] == '\n') count++;
        return count;
    }
    static string CanonicalNewlines(string value) {
        if (value == null) return "";
        return value.Replace("\r\n", "\n").Replace('\r', '\n');
    }
    static string FirstDiff(string want, string got) {
        if (want == null) want = "<null>";
        if (got == null) got = "<null>";
        int at = 0, limit = Math.Min(want.Length, got.Length);
        while (at < limit && want[at] == got[at]) at++;
        return "firstDiff=" + at + ", want=[" + DiffContext(want, at) + "], got=[" + DiffContext(got, at) + "]";
    }
    static string DiffContext(string value, int at) {
        int start = Math.Max(0, at - 24), end = Math.Min(value.Length, at + 24);
        return OneLine(value.Substring(start, end - start));
    }
    // A broken emphasis pair must not appear unless the payload itself carried that exact run.
    // A paste lands between characters the document already had, so a run the payload carries can
    // close an opener from that text or follow its space ("**굵" + " *" + "게**"): the pair is the
    // user's own characters concatenated, not markup the paste invented. A literal-text payload
    // therefore also explains new pairs with the marker characters it carries. HTML/RTF output is
    // judged strictly: a marker the conversion invents must be well formed.
    static bool CheckMarkers(StepContext ctx) {
        if (ctx.Case.Code) return true;
        Bump(checkCounts, "f:markers");
        Dictionary<string,int> before = BadMarkerCounts(ctx.PreDoc, null);
        var afterList = new List<BadMarker>();
        Dictionary<string,int> after = BadMarkerCounts(ctx.PostDoc, afterList);
        string allowance = MarkerAllowance(ctx.Payload, ctx.Plain);
        var created = new List<string>();
        var needed = new Dictionary<char,int>();
        foreach (KeyValuePair<string,int> pair in after) {
            int previous;
            if (!before.TryGetValue(pair.Key, out previous)) previous = 0;
            int count = pair.Value - previous;
            if (count <= 0) continue;
            string context = pair.Key.Substring(pair.Key.IndexOf('|') + 1);
            int literal = CountOccurrences(allowance, context);
            if (count <= literal) continue;
            created.Add(pair.Key);
            Bump(needed, MarkerChar(pair.Key), count - literal);
        }
        foreach (string key in created) {
            char marker = MarkerChar(key);
            int wanted;
            if (!needed.TryGetValue(marker, out wanted)) continue;
            if (wanted <= PayloadMarkerCount(ctx.Payload, ctx.Plain, marker)) continue;
            string context = key.Substring(key.IndexOf('|') + 1);
            BadMarker hit = null;
            foreach (BadMarker bad in afterList) if (String.Equals(bad.Key, key, StringComparison.Ordinal)) { hit = bad; break; }
            int lineNumber = hit == null ? 0 : LineOf(ctx.PostDoc, hit.Offset) + 1;
            string lineText = lineNumber > 0 && lineNumber <= ctx.PostDoc.Count ? ctx.PostDoc.Lines[lineNumber - 1] : MarkerLine(ctx.PostDoc, context);
            return Fail(ctx, "f:markers", "the paste left broken emphasis (" + key.Replace("|", ": ") + ") at line " + lineNumber + ": [" + lineText + "]");
        }
        return true;
    }
    // The marker character a bad-pair key names: "space *| *" and "empty **|**" both name '*'.
    static char MarkerChar(string key) {
        int bar = key.IndexOf('|');
        string run = bar < 0 ? key : key.Substring(0, bar);
        return run.Length == 0 ? '\0' : run[run.Length - 1];
    }
    static void Bump(Dictionary<char,int> map, char key, int amount) {
        int count;
        if (!map.TryGetValue(key, out count)) count = 0;
        map[key] = count + amount;
    }
    // The marker characters the payload itself writes as literal text. A payload the app converts
    // from HTML or RTF counts as 0: a marker the conversion invents there must be well formed.
    // Escaped characters and `code spans` are text the renderer does not read as markup, so they
    // are skipped exactly like BadMarkerCounts' own scan.
    static int PayloadMarkerCount(Payload payload, bool plain, char marker) {
        if (payload == null) return 0;
        if (!plain && (payload.Html != null || payload.Rtf != null)) return 0;
        string text = plain ? payload.Text : payload.Markdown != null ? payload.Markdown : payload.Text;
        if (text == null) return 0;
        int count = 0;
        int at = 0;
        while (at < text.Length) {
            char c = text[at];
            if (c == '\\') { at += 2; continue; }
            if (c == '`') {
                int run = 1;
                while (at + run < text.Length && text[at + run] == '`') run++;
                int close = FindBackticks(text, at + run, run);
                at = close >= 0 ? close + run : at + run;
                continue;
            }
            if (c == marker) count++;
            at++;
        }
        return count;
    }
    static string MarkerAllowance(Payload payload, bool plain) {
        if (payload == null) return "";
        if (plain) return payload.Text == null ? "" : payload.Text;
        if (payload.Html != null || payload.Rtf != null) return "";
        if (payload.Markdown != null) return payload.Markdown;
        return payload.Text == null ? "" : payload.Text;
    }
    static int CountOccurrences(string text, string piece) {
        if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(piece)) return 0;
        int count = 0;
        for (int at = text.IndexOf(piece, StringComparison.Ordinal); at >= 0; at = text.IndexOf(piece, at + piece.Length, StringComparison.Ordinal)) count++;
        return count;
    }
    static string MarkerLine(DocView doc, string piece) {
        for (int i = 0; i < doc.Count; i++) if (doc.Lines[i].IndexOf(piece, StringComparison.Ordinal) >= 0) return doc.Lines[i];
        return piece;
    }
    internal sealed class BadMarker {
        internal string Key;
        internal int Offset;
    }
    static void AddBadMarker(List<BadMarker> list, string kind, int lineStart, int offset, int contextLength, string line) {
        var bad = new BadMarker();
        int length = Math.Min(contextLength, Math.Max(0, line.Length - offset));
        bad.Key = kind + "|" + line.Substring(offset, length);
        bad.Offset = lineStart + offset;
        list.Add(bad);
    }
    // Runs of "*" and "~~" outside code spans, paired as opener/closer per run text. A pair whose
    // content is empty/whitespace, or a closer directly preceded by whitespace, is markup the
    // renderer cannot use.
    static void ScanMarkers(string line, int lineStart, List<BadMarker> list) {
        var open = new Dictionary<string,int>();
        int at = 0;
        while (at < line.Length) {
            char c = line[at];
            if (c == '\\') { at += 2; continue; }
            if (c == '`') {
                int run = 1;
                while (at + run < line.Length && line[at + run] == '`') run++;
                int close = FindBackticks(line, at + run, run);
                at = close >= 0 ? close + run : at + run;
                continue;
            }
            if (c == '*' || c == '~') {
                int run = 1;
                while (at + run < line.Length && line[at + run] == c) run++;
                string key = new string(c, run);
                int runStart = at, runEnd = at + run;
                bool spaceBefore = runStart > 0 && (line[runStart - 1] == ' ' || line[runStart - 1] == '\t');
                bool spaceAfter = runEnd < line.Length && (line[runEnd] == ' ' || line[runEnd] == '\t');
                int opener;
                if (open.TryGetValue(key, out opener)) {
                    open.Remove(key);
                    string between = line.Substring(opener + key.Length, runStart - opener - key.Length);
                    if (between.Trim().Length == 0) {
                        AddBadMarker(list, "empty " + key, lineStart, opener, runEnd - opener, line);
                    } else if (spaceBefore) {
                        AddBadMarker(list, "space " + key, lineStart, Math.Max(0, runStart - 1), runEnd - Math.Max(0, runStart - 1), line);
                    }
                } else if (!spaceAfter && runEnd < line.Length) {
                    open[key] = runStart;
                }
                at = runEnd;
                continue;
            }
            at++;
        }
    }
    static Dictionary<string,int> BadMarkerCounts(DocView doc, List<BadMarker> collected) {
        var list = new List<BadMarker>();
        for (int i = 0; i < doc.Count; i++) {
            if (doc.Fence[i]) continue;
            ScanMarkers(doc.Lines[i], doc.Starts[i], list);
        }
        var counts = new Dictionary<string,int>();
        foreach (BadMarker bad in list) {
            Bump(counts, bad.Key);
            if (collected != null) collected.Add(bad);
        }
        return counts;
    }

    // ---------------------------------------------------------------- document model
    // The text is read as plain lines and cells independently of the preview parser, so a parser
    // fault cannot hide a changed line or cell count. A separate check compares the preview's view.
    internal sealed class DocView {
        internal string Text;
        internal readonly List<int> Starts = new List<int>();
        internal readonly List<int> Ends = new List<int>();
        internal readonly List<int> Delims = new List<int>();
        internal readonly List<string> Lines = new List<string>();
        internal readonly List<bool> Fence = new List<bool>();
        internal int Count { get { return Lines.Count; } }
    }
    internal sealed class TableInfo {
        internal int Header, Delimiter, Last;
        internal int HeaderStart, HeaderEnd, BlockEnd;
        internal List<string> LineTexts = new List<string>();
    }
    static DocView Build(string value) {
        var doc = new DocView();
        doc.Text = value == null ? "" : value;
        int at = 0;
        while (true) {
            int start = at, end = at;
            while (end < doc.Text.Length && doc.Text[end] != '\n' && doc.Text[end] != '\r') end++;
            int delim = 0;
            if (end < doc.Text.Length) {
                delim = 1;
                if (doc.Text[end] == '\r' && end + 1 < doc.Text.Length && doc.Text[end + 1] == '\n') delim = 2;
            }
            doc.Starts.Add(start);
            doc.Ends.Add(end);
            doc.Delims.Add(delim);
            doc.Lines.Add(doc.Text.Substring(start, end - start));
            if (end + delim >= doc.Text.Length) break;
            at = end + delim;
        }
        bool inFence = false;
        char fenceChar = '\0';
        int fenceRun = 0;
        for (int i = 0; i < doc.Count; i++) {
            string line = doc.Lines[i];
            int p = 0;
            while (p < line.Length && p < 3 && line[p] == ' ') p++;
            char c = p < line.Length ? line[p] : '\0';
            int run = 0;
            if (c == '`' || c == '~') while (p + run < line.Length && line[p + run] == c) run++;
            bool fenceLine = (c == '`' || c == '~') && run >= 3;
            if (!inFence) {
                doc.Fence.Add(fenceLine);
                if (fenceLine) {
                    inFence = true;
                    fenceChar = c;
                    fenceRun = run;
                }
            } else {
                doc.Fence.Add(true);
                if (fenceLine && c == fenceChar && run >= fenceRun) {
                    bool tail = true;
                    for (int q = p + run; q < line.Length; q++) if (line[q] != ' ' && line[q] != '\t') { tail = false; break; }
                    if (tail) inFence = false;
                }
            }
        }
        if (doc.Delims[doc.Count - 1] > 0) {
            doc.Starts.Add(doc.Text.Length);
            doc.Ends.Add(doc.Text.Length);
            doc.Delims.Add(0);
            doc.Lines.Add("");
            doc.Fence.Add(inFence);
        }
        return doc;
    }
    static int LineEnd(DocView doc, int line) { return doc.Ends[line] + doc.Delims[line]; }
    static int LineOf(DocView doc, int offset) {
        if (doc.Count == 0) return 0;
        if (offset <= 0) return 0;
        if (offset >= doc.Text.Length) return doc.Count - 1;
        int low = 0, high = doc.Count - 1;
        while (low < high) {
            int mid = (low + high + 1) / 2;
            if (doc.Starts[mid] <= offset) low = mid;
            else high = mid - 1;
        }
        return low;
    }
    // Pipes that delimit cells: an unescaped "|" outside a `code span`, the same rule the table
    // commands and the preview use.
    static void ScanPipes(string text, List<int> pipes) {
        int at = 0;
        while (at < text.Length) {
            char c = text[at];
            if (c == '\\') { at += 2; continue; }
            if (c == '`') {
                int run = 1;
                while (at + run < text.Length && text[at + run] == '`') run++;
                int close = FindBackticks(text, at + run, run);
                at = close >= 0 ? close + run : at + run;
                continue;
            }
            if (c == '|') pipes.Add(at);
            at++;
        }
    }
    static int FindBackticks(string text, int from, int length) {
        int at = from;
        while (at < text.Length) {
            if (text[at] != '`') { at++; continue; }
            int run = 1;
            while (at + run < text.Length && text[at + run] == '`') run++;
            if (run == length) return at;
            at += run;
        }
        return -1;
    }
    static bool HasPipe(string text) {
        var pipes = new List<int>();
        ScanPipes(text, pipes);
        return pipes.Count > 0;
    }
    static void CellsOf(string text, out int[] starts, out int[] ends) {
        var pipes = new List<int>();
        ScanPipes(text, pipes);
        var startList = new List<int>();
        var endList = new List<int>();
        int segmentStart = 0;
        for (int i = 0; i <= pipes.Count; i++) {
            int segmentEnd = i < pipes.Count ? pipes[i] : text.Length;
            int a = segmentStart, b = segmentEnd;
            while (a < b && (text[a] == ' ' || text[a] == '\t')) a++;
            while (b > a && (text[b - 1] == ' ' || text[b - 1] == '\t')) b--;
            bool interior = i > 0 && i < pipes.Count;
            if (a != b || interior) { startList.Add(a); endList.Add(b); }
            segmentStart = segmentEnd + 1;
        }
        starts = startList.ToArray();
        ends = endList.ToArray();
    }
    static int CellCount(string text) {
        int[] starts, ends;
        CellsOf(text, out starts, out ends);
        return starts.Length;
    }
    // A delimiter row: optional spaces, one or more dashes, optional colons on either side, at
    // least one pipe and at least one cell (one dash is enough, per GFM).
    static bool IsDelimiterText(string text) {
        bool seenPipe = false, colonAfter = false;
        int dashes = 0, cells = 0;
        for (int i = 0; i <= text.Length; i++) {
            char c = i == text.Length ? '|' : text[i];
            if (c == ' ' || c == '\t') continue;
            if (c == '|') {
                if (i < text.Length) seenPipe = true;
                if (dashes > 0) cells++;
                else if (colonAfter) return false;
                dashes = 0;
                colonAfter = false;
            } else if (c == '-' && !colonAfter) {
                dashes++;
            } else if (c == ':') {
                if (colonAfter) return false;
                if (dashes > 0) colonAfter = true;
            } else return false;
        }
        return seenPipe && cells > 0;
    }
    static List<TableInfo> FindTables(DocView doc) {
        int count = doc.Count;
        var hasPipe = new bool[count];
        var delimiter = new bool[count];
        var rowLike = new bool[count];
        for (int i = 0; i < count; i++) {
            if (doc.Fence[i]) continue;
            hasPipe[i] = HasPipe(doc.Lines[i]);
            delimiter[i] = IsDelimiterText(doc.Lines[i]);
            rowLike[i] = hasPipe[i] && doc.Lines[i].TrimStart().StartsWith("|", StringComparison.Ordinal);
        }
        var tables = new List<TableInfo>();
        int at = 0;
        while (at < count - 1) {
            if (rowLike[at] && delimiter[at + 1] && CellCount(doc.Lines[at]) == CellCount(doc.Lines[at + 1])) {
                int last = at + 1;
                while (last + 1 < count && rowLike[last + 1]) last++;
                var table = new TableInfo();
                table.Header = at;
                table.Delimiter = at + 1;
                table.Last = last;
                table.HeaderStart = doc.Starts[at];
                table.HeaderEnd = LineEnd(doc, at);
                table.BlockEnd = LineEnd(doc, last);
                for (int i = at; i <= last; i++) table.LineTexts.Add(doc.Lines[i]);
                tables.Add(table);
                at = last + 1;
            } else at++;
        }
        return tables;
    }
}}
