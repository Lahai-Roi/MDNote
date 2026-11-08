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

namespace MDNote {
// --format-abuse-probe <folder>: hundreds of random-but-seeded abnormal edit sequences on lists,
// quotes, headings, code fences, rules and inline formatting (Basic and Live view); after every
// step the document must still keep the format invariants: focus stays in the document, no
// emphasis pair gains a whitespace edge or loses its content, numbered items stay consecutive,
// Enter inside a list item continues the same marker, a block command never rewrites lines outside
// its selection, a command never leaves a fence open, the preview's heading/list parse agrees with
// an independent line parser, and undo/redo restore a whole segment exactly.
// Fixtures are generated here from the seed; no user file is read. The system clipboard is never
// touched: Cut runs the session's cut rule (which copies the selection itself since 2.3.1, so a
// preview Copy handler stands in for the clipboard) and deletes what the rule leaves; Paste drives
// MainWindow.LivePaste with an empty DataObject, exactly the app's plain/markdown paste entry.
// Report: <folder>\format-abuse-probe.txt; format-abuse-probe-PASS.txt only when every sequence
// kept every invariant.
internal static class FormatAbuseProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int FirstSeedDefault = 1, LastSeedDefault = 8, ActionsPerSeedDefault = 400, SegmentLength = 200, UndoCap = 700;
    const long DefaultBudgetMilliseconds = 300000;

    internal sealed class ChangeInfo {
        internal int Offset, Removed, Inserted;
        internal string Text;
    }
    internal sealed class FailureInfo {
        internal int Seed, Step;
        internal string Mode, Fixture, Action, Invariant, Detail, Before, After, Steps;
    }
    internal sealed class StepContext {
        internal MainWindow Window;
        internal string Action, Label, Payload;
        internal int Command;
        internal string PreText, PostText, InsertedText;
        internal DocView PreDoc, PostDoc;
        internal LineModel[] PreModel, PostModel;
        internal int[] Map;
        internal bool IsUndo;
        internal int SelectionStart, SelectionLength, PreCaret, PreCaretLine;
    }

    static string outRoot;
    static int currentSeed, currentStep;
    static string currentMode, currentFixture, currentAction;
    static bool dispatcherFailed;
    static readonly List<string> dispatcherNotes = new List<string>();
    static readonly List<string> stepNames = new List<string>();
    static readonly List<ChangeInfo> changes = new List<ChangeInfo>();
    static readonly Dictionary<string,int> opCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> checkCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> failCounts = new Dictionary<string,int>();
    static readonly List<FailureInfo> failures = new List<FailureInfo>();
    static bool capturing;
    static bool all;
    static EventHandler<DocumentChangeEventArgs> changeHandler;
    static int focusPre = -1, focusPost = -1;
    static Stopwatch watch;
    static long budgetMilliseconds = DefaultBudgetMilliseconds;
    static string skipPrefix;
    static int firstSeed = FirstSeedDefault, lastSeed = LastSeedDefault, actionsPerSeed = ActionsPerSeedDefault;
    static int modeFilter = -1;   // -1 = both, 0 = basic only, 1 = live only
    static bool budgetReached;
    static string cutBuffer = "";

    internal static int Run(string folder) {
        string dir = Path.GetFullPath(folder);
        Directory.CreateDirectory(dir);
        outRoot = dir;
        string passFile = Path.Combine(dir, "format-abuse-probe-PASS.txt");
        string failFile = Path.Combine(dir, "format-abuse-probe-FAIL.txt");
        if (File.Exists(passFile)) File.Delete(passFile);
        if (File.Exists(failFile)) File.Delete(failFile);
        ReadOptions();
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
            File.WriteAllText(Path.Combine(dir, "format-abuse-probe.txt"), text, new UTF8Encoding(false));
            if (all && !budgetReached) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
            else File.WriteAllText(failFile, text, new UTF8Encoding(false));
        } catch (IOException) {
            return 1;
        }
        return all && !budgetReached ? 0 : 1;
    }

    // A reduced run replays one seed/mode; the default (no variables) is the full suite.
    //   MDNOTE_FORMAT_ABUSE_SEED=3 MDNOTE_FORMAT_ABUSE_MODE=live MDNOTE_FORMAT_ABUSE_STEPS=120
    //   MDNOTE_FORMAT_ABUSE_BUDGET=600000  MDNOTE_FORMAT_ABUSE_SKIP=cmd:
    static void ReadOptions() {
        firstSeed = FirstSeedDefault; lastSeed = LastSeedDefault; actionsPerSeed = ActionsPerSeedDefault;
        budgetMilliseconds = DefaultBudgetMilliseconds; skipPrefix = null; modeFilter = -1;
        string value = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_ABUSE_SEED");
        int number;
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) {
            firstSeed = number; lastSeed = number;
        }
        value = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_ABUSE_STEPS");
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) actionsPerSeed = number;
        value = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_ABUSE_BUDGET");
        if (!String.IsNullOrEmpty(value) && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out budgetMilliseconds) && budgetMilliseconds <= 0) budgetMilliseconds = DefaultBudgetMilliseconds;
        value = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_ABUSE_SKIP");
        if (!String.IsNullOrEmpty(value)) skipPrefix = value;
        value = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_ABUSE_MODE");
        if (!String.IsNullOrEmpty(value)) {
            if (String.Equals(value, "basic", StringComparison.OrdinalIgnoreCase)) modeFilter = 0;
            else if (String.Equals(value, "live", StringComparison.OrdinalIgnoreCase)) modeFilter = 1;
        }
    }

    static bool RunSequence(int seed, bool liveMode, StringBuilder reportBuilder) {
        currentSeed = seed;
        currentMode = liveMode ? "live" : "basic";
        currentFixture = "f" + (((seed - 1) % 4) + 1).ToString(CultureInfo.InvariantCulture) + "-" + currentMode;
        currentStep = 0;
        currentAction = "open";
        stepNames.Clear();
        dispatcherFailed = false;
        cutBuffer = "";
        MainWindow window = null;
        string surface = "(not opened)";
        int actions = 0;
        var timer = Stopwatch.StartNew();
        bool runOk = true;
        try {
            window = CreateWindow();
            AttachChanges(window);
            window.SetText(Fixture(seed));
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
                FailOpen("a:open", "the live edit surface did not attach: " + surface);
                runOk = false;
            } else if (!CheckFixture(window)) {
                runOk = false;
            } else {
                runOk = RunActions(window, seed, liveMode, ref actions);
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
        reportBuilder.Append("- fixture: ").Append(currentFixture).Append('\n');
        reportBuilder.Append("- surface: ").Append(surface).Append('\n');
        reportBuilder.Append("- actions: ").Append(actions).Append(" of ").Append(actionsPerSeed).Append('\n');
        reportBuilder.Append("- run time: ").Append(timer.ElapsedMilliseconds).Append(" ms\n");
        int count = CountFailures(seed, currentMode);
        reportBuilder.Append("- failures: ").Append(count).Append('\n');
        return runOk && count == 0 && !dispatcherFailed;
    }

    static bool RunActions(MainWindow window, int seed, bool liveMode, ref int actions) {
        int salt = liveMode ? 7919 : 17;
        var random = new Random(seed * 104729 + salt);
        DocumentHistory history = HistoryOf(window);
        int segments = (actionsPerSeed + SegmentLength - 1) / SegmentLength;
        for (int segment = 0; segment < segments; segment++) {
            if (watch.ElapsedMilliseconds > budgetMilliseconds) {
                budgetReached = true;
                all = false;
                return false;
            }
            history.Clear();
            Invoke(window, "FlushDocumentChanges");
            Pump();
            string segmentStart = DocText(window);
            for (int index = 0; index < SegmentLength; index++) {
                if (actions >= actionsPerSeed) break;
                actions++;
                if (!Step(window, random, actions)) return false;
            }
            if (!CheckUndoOracle(window, segmentStart)) return false;
        }
        return true;
    }

    static bool Step(MainWindow window, Random random, int step) {
        string action, payload;
        int command;
        ChooseAction(random, out action, out command, out payload);
        string label = Label(action, command, payload);
        currentStep = step;
        currentAction = label;
        stepNames.Add(step.ToString(CultureInfo.InvariantCulture) + " " + label);
        Bump(opCounts, label);
        if (skipPrefix != null && label.StartsWith(skipPrefix, StringComparison.Ordinal)) return true;
        var ctx = new StepContext();
        ctx.Window = window;
        ctx.Action = action;
        ctx.Label = label;
        ctx.Command = command;
        ctx.Payload = payload;
        ctx.PreText = DocText(window);
        ctx.PreDoc = Build(ctx.PreText);
        ctx.PreModel = ParseModel(ctx.PreDoc);
        SourceEditor source = Source(window);
        ctx.SelectionStart = source == null ? 0 : source.SelectionStart;
        ctx.SelectionLength = source == null ? 0 : source.SelectionLength;
        ctx.PreCaret = source == null ? -1 : source.CaretOffset;
        ctx.PreCaretLine = source == null ? -1 : source.TextArea.Caret.Line;
        ctx.IsUndo = action == "undo" || action == "redo";
        focusPre = -1;
        focusPost = -1;
        changes.Clear();
        Exception error = null;
        capturing = true;
        try {
            Execute(window, random, action, command, payload);
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
        ctx.PostModel = ParseModel(ctx.PostDoc);
        ctx.InsertedText = ConcatInserted();
        ctx.Map = BuildOffsetMap(ctx.PreText, changes);
        return CheckStep(ctx);
    }

    static void NoteDispatcher(DispatcherUnhandledExceptionEventArgs args) {
        dispatcherFailed = true;
        args.Handled = true;
        if (dispatcherNotes.Count >= 20) return;
        string note = args.Exception == null ? "unknown" : args.Exception.GetType().Name + ": " + OneLine(args.Exception.Message);
        dispatcherNotes.Add("seed " + currentSeed + " " + currentMode + " step " + currentStep + " " + currentAction + ": " + note);
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
        failure.Fixture = currentFixture;
        failure.Step = currentStep;
        failure.Action = ctx == null ? currentAction : ctx.Label;
        failure.Invariant = invariant;
        failure.Detail = OneLine(detail);
        failure.Steps = StepListText();
        if (ctx != null) {
            failure.Before = Excerpt(ctx.PreDoc, focusPre);
            failure.After = Excerpt(ctx.PostDoc, focusPost);
        } else {
            failure.Before = "(none)";
            failure.After = "(none)";
        }
        failures.Add(failure);
        return false;
    }
    static void FailOpen(string invariant, string detail) {
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture;
        failure.Step = 0;
        failure.Action = "open";
        failure.Invariant = invariant;
        failure.Detail = OneLine(detail);
        failure.Steps = "(none)";
        failure.Before = "(none)";
        failure.After = "(none)";
        failures.Add(failure);
        Bump(failCounts, invariant);
        all = false;
    }
    static int CountFailures(int seed, string mode) {
        int count = 0;
        for (int i = 0; i < failures.Count; i++) if (failures[i].Seed == seed && String.Equals(failures[i].Mode, mode, StringComparison.Ordinal)) count++;
        return count;
    }

    // ---------------------------------------------------------------- undo oracle
    static bool CheckUndoOracle(MainWindow window, string segmentStart) {
        Bump(checkCounts, "g:undo");
        string beforeUndo = "";
        int undoSteps = 0;
        try {
            Invoke(window, "FlushDocumentChanges");
            beforeUndo = DocText(window);
            DocumentHistory history = HistoryOf(window);
            int stalls = 0;
            string previous = beforeUndo;
            while (history.CanUndo && undoSteps < UndoCap) {
                Invoke(window, "Undo");
                undoSteps++;
                string now = DocText(window);
                if (String.Equals(now, previous, StringComparison.Ordinal)) {
                    stalls++;
                    if (stalls >= 3) break;
                } else stalls = 0;
                previous = now;
            }
            Invoke(window, "FlushDocumentChanges");
            string undone = DocText(window);
            int redoSteps = 0;
            stalls = 0;
            string beforeRedo = undone;
            while (redoSteps < undoSteps && redoSteps < UndoCap) {
                Invoke(window, "Redo");
                redoSteps++;
                string now = DocText(window);
                if (String.Equals(now, beforeRedo, StringComparison.Ordinal)) {
                    stalls++;
                    if (stalls >= 3) break;
                } else stalls = 0;
                beforeRedo = now;
            }
            Invoke(window, "FlushDocumentChanges");
            string redone = DocText(window);
            bool startSame = String.Equals(undone, segmentStart, StringComparison.Ordinal);
            if (startSame && redoSteps == undoSteps && String.Equals(redone, beforeUndo, StringComparison.Ordinal)) return true;
            string detail = "undo " + undoSteps + " steps to segment start: " + (startSame ? "same" : Diff(segmentStart, undone)) +
                "; redo " + redoSteps + "/" + undoSteps + " to before-undo: " + (String.Equals(redone, beforeUndo, StringComparison.Ordinal) ? "same" : Diff(beforeUndo, redone));
            return FailUndo("g:undo", detail);
        } catch (Exception ex) {
            return FailUndo("g:undo", OneLine(ex.ToString()));
        }
    }
    static bool FailUndo(string invariant, string detail) {
        Bump(failCounts, invariant);
        all = false;
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture;
        failure.Step = currentStep;
        failure.Action = "undo-oracle";
        failure.Invariant = invariant;
        failure.Detail = OneLine(detail);
        failure.Steps = StepListText();
        failure.Before = "(undo oracle at segment end)";
        failure.After = "(undo oracle at segment end)";
        failures.Add(failure);
        return false;
    }

    // ---------------------------------------------------------------- report
    static string ComposeReport(StringBuilder runs) {
        var text = new StringBuilder();
        text.Append("# FormatAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- seeds: ").Append(firstSeed).Append("..").Append(lastSeed).Append(", ").Append(actionsPerSeed)
            .Append(" steps per sequence, ").Append(SegmentLength).Append("-step history segments\n");
        text.Append("- modes: basic (HideMarkers true), live (HideMarkers false)\n");
        text.Append("- fixtures: generated from the seed only; no user file is read; no table content is generated\n");
        text.Append("- surface: offscreen MainWindow, live editor, real command/key/paste entry points; clipboard untouched\n");
        if (skipPrefix != null) text.Append("- skipped actions with prefix: ").Append(skipPrefix).Append('\n');
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
        text.Append("\n## Failures\n");
        if (failures.Count == 0) {
            text.Append("- none\n");
        } else {
            foreach (FailureInfo failure in failures) {
                text.Append("\n### seed ").Append(failure.Seed).Append(' ').Append(failure.Mode).Append(", fixture ").Append(failure.Fixture)
                    .Append(", step ").Append(failure.Step).Append(' ').Append(failure.Action).Append('\n');
                text.Append("- invariant: ").Append(failure.Invariant).Append('\n');
                text.Append("- detail: ").Append(failure.Detail).Append('\n');
                text.Append("- before:\n").Append(failure.Before).Append('\n');
                text.Append("- after:\n").Append(failure.After).Append('\n');
                text.Append("- steps: ").Append(failure.Steps).Append('\n');
            }
        }
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
    static string StepListText() {
        if (stepNames.Count == 0) return "(none)";
        return String.Join(" | ", stepNames.ToArray());
    }
    static string Excerpt(DocView doc, int line) {
        if (doc == null) return "(none)";
        int first, last;
        if (line < 0) {
            first = 0;
            last = Math.Min(doc.Count - 1, 7);
        } else {
            first = Math.Max(0, line - 4);
            last = Math.Min(doc.Count - 1, line + 5);
        }
        var text = new StringBuilder();
        for (int i = first; i <= last; i++) {
            text.Append(i + 1).Append("| ").Append(doc.Lines[i]).Append('\n');
        }
        string value = text.ToString();
        if (value.Length > 1600) value = value.Substring(0, 1600) + "...";
        return value;
    }
    static string Diff(string want, string got) {
        if (want == null) want = "<null>";
        if (got == null) got = "<null>";
        int at = 0, limit = Math.Min(want.Length, got.Length);
        while (at < limit && want[at] == got[at]) at++;
        return "firstDiff=" + at + ", want=[" + Context(want, at) + "], got=[" + Context(got, at) + "]";
    }
    static string Context(string value, int at) {
        int start = Math.Max(0, at - 18), end = Math.Min(value.Length, at + 18);
        return OneLine(value.Substring(start, end - start));
    }
    static string OneLine(string value) {
        if (value == null) return "<null>";
        string text = value.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");
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
    // Every change the step makes is kept with its offset and text, so pre-text line starts can be
    // mapped into the post text and "no line outside the selection changed" can be judged exactly.
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

    // ---------------------------------------------------------------- action choice and execution
    const int TotalWeight = 102;
    static readonly string[] NavNames = new string[] { "left", "right", "up", "down", "home", "end", "word-left", "word-right", "page-up", "page-down" };
    static readonly string[] SelectNames = new string[] { "left", "right", "up", "down", "home", "end", "word-left", "word-right", "page-up", "page-down", "doc-start", "doc-end" };
    static readonly string[] CommandNames = new string[] {
        "bold", "italic", "strike", "inline-code",
        "h0", "h1", "h2", "h3", "h4", "h5", "h6",
        "bullet", "numbered", "task", "quote", "code-block", "rule",
        "link", "size-small", "size-large", "color", "clear-color", "check-toggle"
    };
    static readonly string KoreanSyllables = "가나다라마바사아자차카타파하거너더러머버서어저처커터퍼허고노도로모보소오조초코토포호구누두루무부수우주추쿠투푸후기니디리미비시이지치키티피히값메모표행열";
    static readonly string LatinChars = "abcdefghijklmnopqrstuvwxyz0123456789";
    static readonly string[] PasteSnippets = new string[] {
        "붙여넣기 문장입니다.",
        "첫 줄\n둘째 줄\n셋째 줄\n",
        "**굵은 붙여넣기** 와 *기울임*",
        "- 목록 항목 하나\n- 목록 항목 둘\n",
        "1. 번호 하나\n2. 번호 둘\n",
        "> 인용 붙여넣기\n> 계속 인용\n",
        "```\n붙인 코드\n```\n",
        "[붙인 링크](https://example.com/paste) 와 `코드`",
        "# 붙인 제목\n본문 문장",
        "😀 日本語 붙여넣기"
    };
    static readonly bool[] PastePlain = new bool[] { true, true, false, false, false, false, false, false, false, true };

    static void ChooseAction(Random random, out string action, out int command, out string payload) {
        action = "type";
        command = 0;
        payload = null;
        int roll = random.Next(TotalWeight);
        if (roll < 16) { action = "type"; payload = TypingText(random); }
        else if (roll < 24) action = "place";
        else if (roll < 31) action = "backspace";
        else if (roll < 36) action = "delete";
        else if (roll < 40) { action = "tab"; command = random.Next(2); }
        else if (roll < 47) action = "enter";
        else if (roll < 49) action = "shift-enter";
        else if (roll < 57) { action = "nav"; command = random.Next(NavNames.Length); }
        else if (roll < 65) { action = "select"; command = random.Next(SelectNames.Length); }
        else if (roll < 68) action = "cut";
        else if (roll < 74) { action = "paste"; command = random.Next(PasteSnippets.Length + 1); }
        else if (roll < 96) { action = "cmd"; command = random.Next(CommandNames.Length); }
        else if (roll < 99) action = "undo";
        else if (roll < 101) action = "redo";
        else action = "mode";
    }
    static string Label(string action, int command, string payload) {
        if (action == "type") return "type:" + (payload == " " ? "space" : payload);
        if (action == "tab") return command == 0 ? "tab" : "shift-tab";
        if (action == "nav") return "nav:" + NavNames[command];
        if (action == "select") return "select:" + SelectNames[command];
        if (action == "paste") return "paste:" + (command >= PasteSnippets.Length ? "cut" : command.ToString(CultureInfo.InvariantCulture));
        if (action == "cmd") return "cmd:" + CommandNames[command];
        return action;
    }
    // Block commands edit the touched lines only; the rule button is an insert-at-caret command
    // (LiveRule uses the caret line, not the selection), so it is not held to the line-range rule.
    static bool IsBlockCommand(string label) {
        return label == "cmd:h0" || label == "cmd:h1" || label == "cmd:h2" || label == "cmd:h3" ||
            label == "cmd:h4" || label == "cmd:h5" || label == "cmd:h6" ||
            label == "cmd:bullet" || label == "cmd:numbered" || label == "cmd:task" ||
            label == "cmd:quote" || label == "cmd:code-block";
    }

    static void Execute(MainWindow window, Random random, string action, int command, string payload) {
        if (action == "type") { Source(window).TextArea.PerformTextInput(payload); return; }
        if (action == "place") { PlaceCaret(window, random); return; }
        if (action == "backspace") { int count = 1 + random.Next(12); for (int i = 0; i < count; i++) BackspaceOnce(window); return; }
        if (action == "delete") { int count = 1 + random.Next(12); for (int i = 0; i < count; i++) DeleteOnce(window); return; }
        if (action == "tab") { Invoke(window, "SourceTabPathProbe", command == 1); return; }
        if (action == "enter") { EnterStep(window); return; }
        if (action == "shift-enter") { ShiftEnterStep(window); return; }
        if (action == "nav") { NavigationCommand(command).Execute(null, Source(window).TextArea); return; }
        if (action == "select") { SelectStep(window, random, command); return; }
        if (action == "cut") { CutStep(window); return; }
        if (action == "paste") { PasteStep(window, command); return; }
        if (action == "cmd") { ExecuteCommand(window, command); return; }
        if (action == "undo") { int count = 1 + random.Next(3); for (int i = 0; i < count; i++) Invoke(window, "Undo"); return; }
        if (action == "redo") { int count = 1 + random.Next(3); for (int i = 0; i < count; i++) Invoke(window, "Redo"); return; }
        Invoke(window, "SetLiveMode", Static<bool>("hideMarkers"));
    }
    static void ExecuteCommand(MainWindow window, int index) {
        switch (index) {
            case 0: Invoke(window, "ToggleFormat", "bold"); return;
            case 1: Invoke(window, "ToggleFormat", "italic"); return;
            case 2: Invoke(window, "ToggleFormat", "strike"); return;
            case 3: Invoke(window, "ToggleFormat", "code"); return;
            case 4: Invoke(window, "SetHeading", 0); return;
            case 5: Invoke(window, "SetHeading", 1); return;
            case 6: Invoke(window, "SetHeading", 2); return;
            case 7: Invoke(window, "SetHeading", 3); return;
            case 8: Invoke(window, "SetHeading", 4); return;
            case 9: Invoke(window, "SetHeading", 5); return;
            case 10: Invoke(window, "SetHeading", 6); return;
            case 11: Invoke(window, "ToggleListStyle", false); return;
            case 12: Invoke(window, "ToggleListStyle", true); return;
            case 13: Invoke(window, "ToggleTaskList"); return;
            case 14: Invoke(window, "Quote"); return;
            case 15: Invoke(window, "ToggleCodeBlock"); return;
            case 16: Invoke(window, "LiveRule"); return;
            case 17: Invoke(window, "RunLiveCommand", new Func<TextDocument,int,int,CommandResult>(delegate(TextDocument d, int s, int l) {
                         return MarkdownCommands.InsertLink(d, s, l, "https://example.com/linked");
                     })); return;
            case 18: Invoke(window, "SetFontSize", 11.0); return;
            case 19: Invoke(window, "SetFontSize", 28.0); return;
            case 20: Invoke(window, "SetTextColor", Color.FromRgb(0xB9, 0x1C, 0x1C)); return;
            case 21: Invoke(window, "ClearTextColor"); return;
            default: Invoke(window, "RunLiveCommand", new Func<TextDocument,int,int,CommandResult>(delegate(TextDocument d, int s, int l) {
                         return MarkdownCommands.ToggleTaskCheck(d, s);
                     })); return;
        }
    }

    // ---------------------------------------------------------------- keys
    static void EnterStep(MainWindow window) {
        if (Prop<bool>(window, "LiveFormat")) {
            CommandResult result = (CommandResult)Invoke(window, "SourceEnter");
            if (result == null) { Source(window).TextArea.PerformTextInput("\n"); return; }
            Invoke(window, "ApplyCommandResult", result, -1, 0);
            return;
        }
        Source(window).TextArea.PerformTextInput("\n");
    }
    // Shift+Enter is the editor's own line break; SourceStructuralKeys only handles plain Enter, so
    // the command the key handler would fall through to is executed directly.
    static void ShiftEnterStep(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null) return;
        EditingCommands.EnterLineBreak.Execute(null, source.TextArea);
    }
    static void BackspaceOnce(MainWindow window) {
        if (RaisePreviewKey(window, Key.Back)) return;
        SourceEditor source = Source(window);
        if (source != null) EditingCommands.Backspace.Execute(null, source.TextArea);
    }
    static void DeleteOnce(MainWindow window) {
        if (RaisePreviewKey(window, Key.Delete)) return;
        SourceEditor source = Source(window);
        if (source != null) EditingCommands.Delete.Execute(null, source.TextArea);
    }
    static bool RaisePreviewKey(MainWindow window, Key key) {
        SourceEditor source = Source(window);
        if (source == null) return false;
        PresentationSource presentation = PresentationSource.FromVisual(source);
        if (presentation == null) return false;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, presentation, 0, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.TextArea.RaiseEvent(args);
        return args.Handled;
    }
    static RoutedUICommand NavigationCommand(int index) {
        switch (index) {
            case 0: return EditingCommands.MoveLeftByCharacter;
            case 1: return EditingCommands.MoveRightByCharacter;
            case 2: return EditingCommands.MoveUpByLine;
            case 3: return EditingCommands.MoveDownByLine;
            case 4: return EditingCommands.MoveToLineStart;
            case 5: return EditingCommands.MoveToLineEnd;
            case 6: return EditingCommands.MoveLeftByWord;
            case 7: return EditingCommands.MoveRightByWord;
            case 8: return EditingCommands.MoveUpByPage;
            default: return EditingCommands.MoveDownByPage;
        }
    }
    static RoutedUICommand SelectCommand(int index) {
        switch (index) {
            case 0: return EditingCommands.SelectLeftByCharacter;
            case 1: return EditingCommands.SelectRightByCharacter;
            case 2: return EditingCommands.SelectUpByLine;
            case 3: return EditingCommands.SelectDownByLine;
            case 4: return EditingCommands.SelectToLineStart;
            case 5: return EditingCommands.SelectToLineEnd;
            case 6: return EditingCommands.SelectLeftByWord;
            case 7: return EditingCommands.SelectRightByWord;
            case 8: return EditingCommands.SelectUpByPage;
            case 9: return EditingCommands.SelectDownByPage;
            case 10: return EditingCommands.SelectToDocumentStart;
            default: return EditingCommands.SelectToDocumentEnd;
        }
    }

    // ---------------------------------------------------------------- caret placement and selection
    // Line start, middle, end and the edges inside hidden marker runs (#, >, -, *, _, ~, `, the
    // list prefix and a task box). Offsets that would split a surrogate pair are moved after it,
    // so an emoji is never cut by a programmatic caret a real click could not reach.
    static void PlaceCaret(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        if (random.Next(8) == 0) {
            source.Select(random.Next(2) == 0 ? 0 : source.Document.TextLength, 0);
            return;
        }
        var document = source.Document;
        DocumentLine line = document.GetLineByNumber(1 + random.Next(document.LineCount));
        string text = document.GetText(line.Offset, line.Length);
        var choices = new List<int>();
        choices.Add(0);
        choices.Add(text.Length);
        choices.Add(text.Length / 2);
        for (int i = 0; i < text.Length && i < 400; i++) {
            char c = text[i];
            if (c == '*' || c == '_' || c == '~' || c == '`' || c == '#' || c == '>' || c == '-' || c == '[' || c == ']') {
                choices.Add(i);
                choices.Add(i + 1);
                if (i > 0) choices.Add(i - 1);
            }
        }
        MarkdownLivePreview.Session session = Session(window);
        MarkdownLivePreview.LineInfo info = session == null ? null : session.Info(line);
        if (info != null) {
            if (info.LeadEnd > 0) { choices.Add(info.LeadEnd); choices.Add(info.LeadEnd - 1); }
            if (info.List != null) { choices.Add(info.ListStart); choices.Add(info.ListEnd); choices.Add(info.ListBody); }
        }
        int column = choices[random.Next(choices.Count)];
        if (column < 0) column = 0;
        if (column > text.Length) column = text.Length;
        while (column > 0 && column < text.Length && Char.IsLowSurrogate(text[column])) column++;
        source.Select(line.Offset + column, 0);
    }
    static int RandomPosition(MainWindow window, Random random, out int lineIndex) {
        SourceEditor source = Source(window);
        lineIndex = 0;
        if (source == null || source.Document == null) return 0;
        var document = source.Document;
        lineIndex = random.Next(document.LineCount);
        DocumentLine line = document.GetLineByNumber(lineIndex + 1);
        string text = document.GetText(line.Offset, line.Length);
        int column;
        switch (random.Next(4)) {
            case 0: column = 0; break;
            case 1: column = text.Length; break;
            case 2: column = random.Next(text.Length + 1); break;
            default:
                column = text.Length / 2;
                for (int i = 0; i < text.Length && i < 400; i++) {
                    char c = text[i];
                    if (c == '*' || c == '_' || c == '~' || c == '`' || c == '#' || c == '>' || c == '-' || c == '[' || c == ']') { column = random.Next(2) == 0 ? i : i + 1; break; }
                }
                break;
        }
        if (column > text.Length) column = text.Length;
        while (column > 0 && column < text.Length && Char.IsLowSurrogate(text[column])) column++;
        return line.Offset + column;
    }
    static void SelectStep(MainWindow window, Random random, int command) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        if (random.Next(2) == 0) {
            RoutedUICommand extend = SelectCommand(command);
            int count = 1 + random.Next(6);
            for (int i = 0; i < count; i++) extend.Execute(null, source.TextArea);
            return;
        }
        int lineA, lineB;
        int a = RandomPosition(window, random, out lineA);
        int b = RandomPosition(window, random, out lineB);
        int start = Math.Min(a, b);
        int end = Math.Max(a, b);
        if (start == end && end < source.Document.TextLength) end++;
        source.Select(start, end - start);
    }

    // ---------------------------------------------------------------- cut and paste
    // Cut keeps the document half of the command. The session's own PreviewExecuted rule
    // (HiddenMarkerDelete) is invoked for the cut first. 2.3.1 (D1): in the formatted view that rule
    // now performs the cut itself (Editor.Copy() plus one update that changes only the selected
    // characters and writes the markers it cut off back) and sets Handled, so the payload has to be
    // read before it runs. The vendor clipboard write is stood in for by a preview Copy handler, the
    // same way the fence cut case does it. In live view (markers shown) the rule widens the
    // selection to whole pairs and leaves the removal to the probe, as before.
    static void CutStep(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        if (source.SelectionLength == 0) {
            if (source.CaretOffset < source.Document.TextLength) source.Select(source.CaretOffset, 1);
            else if (source.CaretOffset > 0) source.Select(source.CaretOffset - 1, 1);
        }
        if (source.SelectionLength == 0) return;
        MarkdownLivePreview.Session session = Session(window);
        if (session != null) {
            MethodInfo marker = typeof(MarkdownLivePreview.Session).GetMethod("HiddenMarkerDelete", Members);
            ExecutedRoutedEventArgs cutArgs = CutArguments();
            if (marker == null || cutArgs == null) throw new MissingMemberException("probe: the hidden-marker cut rule seam is missing");
            int lengthBefore = source.Document.TextLength;
            string selected = source.Document.GetText(source.SelectionStart, source.SelectionLength);
            ExecutedRoutedEventHandler copyCapture = delegate(object sender, ExecutedRoutedEventArgs args) {
                if (args.Command != ApplicationCommands.Copy) return;
                args.Handled = true;
            };
            CommandManager.AddPreviewExecutedHandler(source.TextArea, copyCapture);
            try { marker.Invoke(session, new object[] { source.TextArea, cutArgs }); }
            finally { CommandManager.RemovePreviewExecutedHandler(source.TextArea, copyCapture); }
            // The rule swallows the cut when the selection would break a fence or a table row
            // (Handled without a change); only a real removal becomes the next paste's payload.
            if (cutArgs.Handled) {
                if (source.Document.TextLength != lengthBefore) cutBuffer = selected;
                return;
            }
        }
        if (source.SelectionLength == 0) return;
        cutBuffer = source.Document.GetText(source.SelectionStart, source.SelectionLength);
        source.SelectedText = "";
    }
    // WPF keeps ExecutedRoutedEventArgs' constructor internal, so one is built through reflection
    // with ApplicationCommands.Cut. The session's cut rule sets Handled when it swallows a cut, and
    // RoutedEventArgs refuses that while RoutedEvent is null (the real CommandManager always supplies
    // one); these handlers are registered on PreviewExecuted, so that is the event set here.
    static ExecutedRoutedEventArgs CutArguments() {
        ConstructorInfo ctor = typeof(ExecutedRoutedEventArgs).GetConstructor(Members, null, new Type[] { typeof(ICommand), typeof(object) }, null);
        if (ctor == null) return null;
        ExecutedRoutedEventArgs args = (ExecutedRoutedEventArgs)ctor.Invoke(new object[] { ApplicationCommands.Cut, null });
        args.RoutedEvent = CommandManager.PreviewExecutedEvent;
        return args;
    }
    static void PasteStep(MainWindow window, int index) {
        string text;
        bool plain;
        if (index >= PasteSnippets.Length) {
            if (cutBuffer.Length == 0) return;
            text = cutBuffer;
            plain = true;
        } else {
            text = PasteSnippets[index];
            plain = PastePlain[index];
        }
        Invoke(window, "LivePaste", new System.Windows.DataObject(), plain, text);
    }

    // ---------------------------------------------------------------- payloads
    static string TypingText(Random random) {
        switch (random.Next(16)) {
            case 0: return "가나다";
            case 1: return RandomOf(random, KoreanSyllables, 1 + random.Next(3));
            case 2: return " ";
            case 3: return "*";
            case 4: return "**";
            case 5: return "~~";
            case 6: return "_";
            case 7: return "`";
            case 8: return "#";
            case 9: return ">";
            case 10: return "-";
            case 11: return "1.";
            case 12: return "12.";
            case 13: return "a";
            case 14: return RandomOf(random, LatinChars, 1 + random.Next(5));
            default: return "日本語";
        }
    }
    static string RandomOf(Random random, string alphabet, int count) {
        var text = new StringBuilder(count);
        for (int i = 0; i < count; i++) text.Append(alphabet[random.Next(alphabet.Length)]);
        return text.ToString();
    }
    // ---------------------------------------------------------------- document model
    internal sealed class DocView {
        internal string Text;
        internal readonly List<int> Starts = new List<int>();
        internal readonly List<int> Ends = new List<int>();
        internal readonly List<int> Delims = new List<int>();
        internal readonly List<string> Lines = new List<string>();
        internal readonly List<bool> Fence = new List<bool>();
        internal bool UnclosedFence;
        internal int Count { get { return Lines.Count; } }
    }
    internal sealed class LineModel {
        internal bool Skip;
        internal int Heading;
        internal bool Setext;
        internal bool Rule;
        internal int QuoteLevels;
        internal bool HasList, Ordered, Task;
        internal int ListNumber;
        internal int ListStart, ListEnd, ListBody;
    }
    internal sealed class ListPrefix {
        internal int Indent, Start, End, Number, Body;
        // The item's content column (from the scan start): the marker plus the spaces after it,
        // before a task box, counted with a tab as four columns (MarkdownLivePreview.ReadList).
        internal int Content;
        internal bool Ordered, Task;
        internal char BulletChar, Separator;
    }
    internal sealed class ScanList {
        internal int Indent, Content, Number;
        internal bool Ordered, Task;
        internal ScanList Parent;
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
        // A document ending in a newline has one more (empty) line, exactly like AvalonEdit's line
        // table, so line numbers stay comparable with the preview's DocumentLine numbers.
        if (doc.Delims[doc.Count - 1] > 0) {
            doc.Starts.Add(doc.Text.Length);
            doc.Ends.Add(doc.Text.Length);
            doc.Delims.Add(0);
            doc.Lines.Add("");
            doc.Fence.Add(inFence);
        }
        doc.UnclosedFence = inFence;
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
    static string NextLine(DocView doc, int line) {
        return line + 1 < doc.Count ? doc.Lines[line + 1] : "(end of document)";
    }
    // Pre-text position to post-text position across the step's change list. The changes arrive in
    // the order the document applied them, so the map is replayed change by change over every pre
    // position instead of summed as deltas.
    static int[] BuildOffsetMap(string preText, List<ChangeInfo> list) {
        int length = preText == null ? 0 : preText.Length;
        var map = new int[length + 1];
        for (int i = 0; i <= length; i++) map[i] = i;
        for (int c = 0; c < list.Count; c++) {
            ChangeInfo change = list[c];
            int start = change.Offset, removed = change.Removed, inserted = change.Inserted;
            for (int p = 0; p <= length; p++) {
                int value = map[p];
                if (value >= start + removed) map[p] = value + inserted - removed;
                else if (value > start) {
                    int local = value - start;
                    if (local > inserted) local = inserted;
                    map[p] = start + local;
                }
            }
        }
        return map;
    }
    static int Mapped(StepContext ctx, int offset) {
        if (offset < 0) return -1;
        if (ctx.Map == null) return offset;
        if (offset < ctx.Map.Length) return ctx.Map[offset];
        return ctx.Map.Length == 0 ? -1 : ctx.Map[ctx.Map.Length - 1] + (offset - (ctx.Map.Length - 1));
    }

    // ---------------------------------------------------------------- independent line parser
    // A line's heading and list shape, parsed here without the preview's own code so a parser
    // fault cannot hide itself. The rules mirror the app's Markdown shape (ATX up to six levels,
    // setext underlines, quote prefixes, list markers after a quote, headings inside an item).
    static LineModel[] ParseModel(DocView doc) {
        var models = new LineModel[doc.Count];
        ScanList previous = null;
        bool beforeSetext = false;
        // The quote state the live block cache carries into the next line: a lazy continuation
        // keeps the quote its paragraph line opened. It decides whether a blank-looking line is
        // blank: inside a quote a blank line keeps the list open only while the quote continues.
        int quote = 0;
        bool quoteParagraph = false;
        for (int i = 0; i < doc.Count; i++) {
            var model = new LineModel();
            models[i] = model;
            model.Skip = doc.Fence[i];
            if (model.Skip) {
                previous = null;
                beforeSetext = false;
                quote = 0;
                quoteParagraph = false;
                continue;
            }
            string text = doc.Lines[i];
            int length = text.Length;
            int p = 0, columns = 0;
            while (p < length && (text[p] == ' ' || text[p] == '\t')) {
                columns += text[p] == '\t' ? 4 : 1;
                p++;
            }
            int content = p;
            int quoteLevels = 0;
            while (content < length && text[content] == '>' && quoteLevels < 6) {
                int end = content + 1;
                if (end < length && (text[end] == ' ' || text[end] == '\t')) end++;
                quoteLevels++;
                content = end;
            }
            model.QuoteLevels = quoteLevels;
            int quoteBody = content;
            int indentStart = content;
            // A rule's own indentation starts at the container's content: the line's leading
            // spaces, or the spaces after the last quote marker (the markers are not indentation).
            int ruleIndent = quoteLevels > 0 ? 0 : columns;
            while (content < length && (text[content] == ' ' || text[content] == '\t')) {
                int width = text[content] == '\t' ? 4 : 1;
                columns += width;
                ruleIndent += width;
                content++;
            }
            // CommonMark and MarkdownLivePreview.ParseLine's headingIndent: at most three leading
            // whitespace characters set an ATX heading off; four or more make the line paragraph
            // text. A quote's own markers are not indentation, so the run after the last ">" is
            // what counts there (the marker's one spacer is not part of it). Like the preview,
            // the count is characters, so a tab counts one here. A list item's heading is judged
            // after its marker below and has no such limit.
            int headingIndent = quoteLevels > 0 ? content - indentStart : p;
            int h = content;
            while (h < length && text[h] == '#' && h - content < 7) h++;
            if (h > content && h - content <= 6 && headingIndent <= 3 && (h == length || text[h] == ' ' || text[h] == '\t')) {
                model.Heading = h - content;
                if (h < length) h++;
                content = h;
            }
            // A line with no content of its own does not end a list (the live block cache keeps
            // it open; the block after a blank line, a table row included, ends it there), and a
            // line of just U+3000 is content, so it is not blank and does not keep a list.
            bool blank = (quote <= 0 && BlankLine(text)) ||
                (quoteLevels > 0 && quoteLevels == quote && BlankLine(text, quoteBody));
            ScanList scanned = NextList(text, ListScanStart(text), previous);
            ScanList list = blank ? previous : scanned;
            // A marker-less line indented to an open item's content column continues that item
            // (MarkdownLivePreview.ContinuationList); a lazy line after a quote paragraph does not.
            if (list == null && !blank && previous != null && (quote <= 0 || quoteLevels > 0))
                list = ContinuationList(text, ListScanStart(text), previous, beforeSetext && quoteLevels == 0 && SetextUnderlineLevel(text) > 0);
            // A quote line indented to an open item's content column is a quote inside that item
            // (MarkdownLivePreview.QuoteContinuationList): the list stays open and the block's quote stays 0.
            bool itemQuote = false;
            if (list == null && !blank && quoteLevels > 0 && quote <= 0) {
                list = QuoteContinuationList(text, previous);
                itemQuote = list != null;
            }
            // The quote the next line reads: this line's own markers, or a lazy continuation of
            // the open paragraph that cannot start a block of its own.
            if (itemQuote) {
                quote = 0;
                quoteParagraph = false;
            } else if (quoteLevels > 0) {
                quote = quoteLevels;
                quoteParagraph = QuoteBodyParagraph(text, quoteBody);
            } else if (quote > 0 && quoteParagraph && !BlankLine(text) && !StartsBlock(text)) {
                quoteParagraph = true;
            } else {
                quote = 0;
                quoteParagraph = false;
            }
            int underline = beforeSetext && quoteLevels == 0 ? SetextUnderlineLevel(text) : 0;
            if (underline > 0) {
                previous = list;
                beforeSetext = SetextText(text, beforeSetext);
                continue;
            }
            if (model.Heading == 0 && quoteLevels == 0 && SetextText(text, beforeSetext)) {
                int level = NextSetextLevel(doc, i);
                if (level > 0) { model.Setext = true; model.Heading = level; }
            }
            if (IsRule(text, content, ruleIndent)) {
                model.Rule = true;
                previous = list;
                beforeSetext = SetextText(text, beforeSetext);
                continue;
            }
            if (columns <= 3 && quoteLevels == 0 && DefinitionLine(text, content)) {
                previous = list;
                beforeSetext = SetextText(text, beforeSetext);
                continue;
            }
            ListPrefix prefix;
            int listScan = quoteLevels > 0 ? indentStart : 0;
            if (ReadList(text, listScan, out prefix)) {
                model.HasList = true;
                model.Ordered = prefix.Ordered;
                model.Task = prefix.Task;
                model.ListStart = prefix.Start;
                model.ListEnd = prefix.End;
                model.ListBody = prefix.Body;
                model.ListNumber = scanned == null ? 0 : scanned.Number;
                int body = prefix.Body;
                int listQuote = body;
                int levels = quoteLevels;
                while (listQuote < length && text[listQuote] == '>' && levels < 6) {
                    int end = listQuote + 1;
                    if (end < length && (text[end] == ' ' || text[end] == '\t')) end++;
                    levels++;
                    listQuote = end;
                }
                if (levels > quoteLevels) body = listQuote;
                int lh = body;
                while (lh < length && text[lh] == '#' && lh - body < 7) lh++;
                if (lh > body && lh - body <= 6 && (lh == length || text[lh] == ' ' || text[lh] == '\t')) {
                    model.Heading = lh - body;
                }
            }
            previous = list;
            beforeSetext = SetextText(text, beforeSetext);
        }
        return models;
    }
    static char AtxAt(string text, int content, out int after) {
        after = content;
        int h = content;
        while (h < text.Length && text[h] == '#' && h - content < 7) h++;
        if (h > content && h - content <= 6 && (h == text.Length || text[h] == ' ' || text[h] == '\t')) {
            after = h < text.Length ? h + 1 : h;
            return (char)('0' + (h - content));
        }
        return '\0';
    }
    static bool ReadList(string text, out ListPrefix prefix) {
        return ReadList(text, 0, out prefix);
    }
    static bool ReadList(string text, int from, out ListPrefix prefix) {
        prefix = new ListPrefix();
        int p = Math.Max(0, Math.Min(from, text.Length));
        int column = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) {
            int width = text[p] == '\t' ? 4 : 1;
            prefix.Indent += width;
            column += width;
            p++;
        }
        prefix.Start = p;
        // The marker separator is an ASCII space or tab only, like MarkdownLivePreview.ReadList:
        // a full-width space (U+3000) is content, so "-　item" is not a list item.
        if (p + 1 < text.Length && "-*+".IndexOf(text[p]) >= 0 && (text[p + 1] == ' ' || text[p + 1] == '\t')) {
            prefix.BulletChar = text[p];
            p++;
            column++;
        } else {
            int start = p;
            while (p < text.Length && p - start < 9 && text[p] >= '0' && text[p] <= '9') p++;
            if (p == start || p + 1 >= text.Length || (text[p] != '.' && text[p] != ')') || (text[p + 1] != ' ' && text[p + 1] != '\t')) return false;
            prefix.Ordered = true;
            prefix.Separator = text[p];
            int number;
            prefix.Number = Int32.TryParse(text.Substring(start, p - start), out number) && number > 0 && number < 1000000 ? number : 1;
            column += p - start + 1;
            p++;
        }
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { column += text[p] == '\t' ? 4 : 1; p++; }
        prefix.Content = column;
        prefix.End = prefix.Body = p;
        prefix.Task = p + 2 < text.Length && text[p] == '[' && text[p + 2] == ']' && " xX".IndexOf(text[p + 1]) >= 0;
        if (prefix.Task) {
            p += 3;
            while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
            prefix.Body = p;
        }
        return true;
    }
    static int ListScanStart(string text) {
        int p = 0, levels = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        while (p < text.Length && text[p] == '>' && levels < 6) {
            p++;
            levels++;
            if (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        }
        return levels > 0 ? p : 0;
    }
    static ScanList NextList(string text, int start, ScanList previous) {
        ListPrefix prefix;
        if (!ReadList(text, start, out prefix)) return null;
        // A marker more indented than its predecessor is a child only when it reaches that
        // item's content column (CommonMark: "1. " content starts at column 3, so a two-space
        // bullet closes the item and opens a sibling list). A marker at the item's own column is
        // a sibling of it, and one to its left is already handled by the Indent test below.
        while (previous != null && (previous.Indent > prefix.Indent ||
            previous.Indent == prefix.Indent && (previous.Ordered != prefix.Ordered || previous.Task != prefix.Task) ||
            previous.Indent < prefix.Indent && prefix.Indent < previous.Content)) previous = previous.Parent;
        bool same = previous != null && previous.Indent == prefix.Indent;
        var state = new ScanList();
        state.Indent = prefix.Indent;
        state.Content = prefix.Content;
        state.Ordered = prefix.Ordered;
        state.Task = prefix.Task;
        state.Number = prefix.Ordered ? (same ? previous.Number + 1 : prefix.Number) : 0;
        state.Parent = same ? previous.Parent : previous;
        return state;
    }
    // The open item a marker-less line continues (MarkdownLivePreview.ContinuationList): the deepest
    // item whose content column the line's indentation reaches; null for a quote, thematic break,
    // definition or ATX heading (at most three columns of indentation) or when no column is reached.
    // underline: the "===" / "---" line under a paragraph line keeps the list open even when it reads as a thematic break.
    static ScanList ContinuationList(string text, int start, ScanList previous, bool underline) {
        int p = Math.Max(0, Math.Min(start, text.Length)), columns = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { columns += text[p] == '\t' ? 4 : 1; p++; }
        if (p >= text.Length || text[p] == '>') return null;
        if (!underline && IsRule(text, p, columns)) return null;
        int after;
        if (columns <= 3 && (AtxAt(text, p, out after) != '\0' || DefinitionLine(text, p))) return null;
        for (ScanList state = previous; state != null; state = state.Parent) if (columns >= state.Content) return state;
        return null;
    }
    // The open item a quote line continues (MarkdownLivePreview.QuoteContinuationList): the deepest item
    // whose content column the indentation before the line's first ">" reaches; null when the quote body
    // is a list marker (a list inside the quote) or no column is reached.
    static ScanList QuoteContinuationList(string text, ScanList previous) {
        if (previous == null) return null;
        int p = 0, columns = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { columns += text[p] == '\t' ? 4 : 1; p++; }
        if (p >= text.Length || text[p] != '>') return null;
        ListPrefix prefix;
        if (ReadList(text, ListScanStart(text), out prefix)) return null;
        for (ScanList state = previous; state != null; state = state.Parent) if (columns >= state.Content) return state;
        return null;
    }
    // The live block cache's blank line: ASCII spaces and tabs only, so a U+3000-only line is
    // content (it ends a list instead of keeping it open).
    static bool BlankLine(string text) { return BlankLine(text, 0); }
    static bool BlankLine(string text, int start) {
        for (int i = start; i < text.Length; i++) if (text[i] != ' ' && text[i] != '\t') return false;
        return true;
    }
    // "```" / "~~~" at the first non-blank character (MarkdownLivePreview.FenceStart).
    static bool FenceStart(string text, int start) {
        int p = start;
        if (p >= text.Length || (text[p] != '`' && text[p] != '~')) return false;
        char marker = text[p];
        int count = 0;
        while (p < text.Length && text[p] == marker) { count++; p++; }
        return count >= 3;
    }
    // The content after a quote's markers is paragraph text a following lazy line may continue
    // (MarkdownLivePreview.QuoteBodyParagraph).
    static bool QuoteBodyParagraph(string text, int bodyStart) {
        int p = bodyStart, indent = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { indent += text[p] == '\t' ? 4 : 1; p++; }
        if (p >= text.Length) return false;
        int after;
        if (AtxAt(text, p, out after) != '\0') return false;
        if (IsRule(text, p, indent)) return false;
        if (FenceStart(text, p)) return false;
        ListPrefix prefix;
        if (ReadList(text, p, out prefix)) return false;
        return true;
    }
    // The line begins a block of its own, so it cannot continue the open quote lazily
    // (MarkdownLivePreview.StartsBlock; the probe never writes a table pipe).
    static bool StartsBlock(string text) {
        int p = 0, columns = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { columns += text[p] == '\t' ? 4 : 1; p++; }
        if (p >= text.Length) return true;
        if (columns <= 3 && FenceStart(text, p)) return true;
        int after;
        if (AtxAt(text, p, out after) != '\0') return true;
        if (IsRule(text, p, columns)) return true;
        if (SetextUnderlineLevel(text) > 0) return true;
        ListPrefix prefix;
        if (ReadList(text, out prefix)) return true;
        if (columns <= 3 && DefinitionLine(text, p)) return true;
        return false;
    }
    static bool IsRule(string text, int start, int indent) {
        if (indent > 3) return false;
        char marker = '\0';
        int count = 0;
        for (int i = start; i < text.Length; i++) {
            char c = text[i];
            if (c == ' ' || c == '\t') continue;
            if (marker == '\0') marker = c;
            if ((c != '-' && c != '*' && c != '_') || c != marker) return false;
            count++;
        }
        return count >= 3;
    }
    static int SetextUnderlineLevel(string text) {
        int p = 0;
        while (p < text.Length && p < 4 && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p >= text.Length) return 0;
        char marker = text[p];
        if (marker != '=' && marker != '-') return 0;
        int count = 0;
        for (; p < text.Length && text[p] == marker; p++) count++;
        if (marker == '-' && count < 2) return 0;
        for (; p < text.Length; p++) if (text[p] != ' ' && text[p] != '\t') return 0;
        return marker == '=' ? 1 : 2;
    }
    static int NextSetextLevel(DocView doc, int line) {
        if (line + 1 >= doc.Count) return 0;
        return SetextUnderlineLevel(doc.Lines[line + 1]);
    }
    // "[id]: url" or "[^id]: text": a definition line is not paragraph text and is not a list.
    // Only the shape matters here; the probe never generates one on purpose.
    static bool DefinitionLine(string text, int start) {
        int p = start;
        if (p >= text.Length || text[p] != '[') return false;
        p++;
        if (p < text.Length && text[p] == '^') p++;
        int first = p;
        while (p < text.Length && p - first < 128 && text[p] != ']' && text[p] != '[') p++;
        if (p >= text.Length || text[p] != ']' || p == first) return false;
        p++;
        return p < text.Length && text[p] == ':';
    }
    static bool SetextText(string text, bool underlinedBefore) {
        if (text.Length == 0) return false;
        if (underlinedBefore && SetextUnderlineLevel(text) > 0) return false;
        int p = 0, columns = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) {
            columns += text[p] == '\t' ? 4 : 1;
            p++;
        }
        if (p >= text.Length) return false;
        if (text[p] == '>') return false;
        if (IsRule(text, p, columns)) return false;
        int after;
        if (AtxAt(text, p, out after) != '\0') return false;
        ListPrefix prefix;
        if (ReadList(text, out prefix)) return false;
        if (columns <= 3 && DefinitionLine(text, p)) return false;
        return true;
    }
    // ---------------------------------------------------------------- invariants
    static bool CheckStep(StepContext ctx) {
        MainWindow window = ctx.Window;
        Bump(checkCounts, "text");
        string mirror = null;
        try { mirror = Prop<string>(window, "text"); } catch (Exception) { mirror = null; }
        if (mirror == null || !String.Equals(mirror, ctx.PostText, StringComparison.Ordinal)) {
            return Fail(ctx, "text", "window.text differs from the editor document: " + Diff(ctx.PostText, mirror));
        }
        if (!ctx.IsUndo) {
            if (!CheckFenceCommand(ctx)) return false;
            if (!CheckBlockRange(ctx)) return false;
            if (!CheckEnterMarker(ctx)) return false;
            if (!CheckNumberedList(ctx)) return false;
            if (!CheckEmphasis(ctx)) return false;
        }
        if (!CheckParseAgreement(ctx)) return false;
        return CheckCaret(ctx);
    }
    static bool CheckCaret(StepContext ctx) {
        Bump(checkCounts, "h");
        SourceEditor source = Source(ctx.Window);
        if (source == null || source.Document == null) return Fail(ctx, "h", "the source editor is gone");
        int caret = source.CaretOffset;
        if (caret < 0 || caret > ctx.PostText.Length) {
            return Fail(ctx, "h", "caret " + caret + " is outside 0.." + ctx.PostText.Length);
        }
        int start = source.SelectionStart, length = source.SelectionLength;
        if (start < 0 || length < 0 || start + length > ctx.PostText.Length) {
            return Fail(ctx, "h", "selection " + start + "+" + length + " is outside 0.." + ctx.PostText.Length);
        }
        if (caret < start || caret > start + length) {
            return Fail(ctx, "h", "caret " + caret + " is outside its selection " + start + "+" + length);
        }
        return true;
    }

    // ---------------------------------------------------------------- block command line range
    // A heading/list/quote/code-block command applies to the lines its selection touches. No line
    // outside that range may change text: the offset map says where each outside line went.
    static bool CheckBlockRange(StepContext ctx) {
        if (ctx.Action != "cmd" || !IsBlockCommand(ctx.Label) || ctx.SelectionLength <= 0) return true;
        Bump(checkCounts, "i");
        if (ctx.PreDoc.Count == 0) return true;
        int first = LineOf(ctx.PreDoc, ctx.SelectionStart);
        int last = LineOf(ctx.PreDoc, ctx.SelectionStart + ctx.SelectionLength - 1);
        // The code button's documented toggle-off (MarkdownCommands.RemoveCodeBlock): a complete fenced
        // block under the selection loses BOTH fence lines, so the opener lies outside the selected
        // lines by construction. Accepted only when the result is exactly the document without that
        // pair (every other line, in order, byte for byte); anything else takes the strict check.
        if (ctx.Label == "cmd:code-block" && FencePairToggledOff(ctx, first, last)) return true;
        for (int i = 0; i < ctx.PreDoc.Count; i++) {
            if (i >= first && i <= last) continue;
            int mapped = Mapped(ctx, ctx.PreDoc.Starts[i]);
            if (mapped < 0 || mapped > ctx.PostText.Length) {
                focusPre = i;
                return Fail(ctx, "i", "a block command dropped line " + (i + 1) + " outside the selection: [" + ctx.PreDoc.Lines[i] + "]");
            }
            int line = LineOf(ctx.PostDoc, mapped);
            if (ctx.PostDoc.Starts[line] != mapped) {
                focusPre = i;
                focusPost = line;
                return Fail(ctx, "i", "a block command merged line " + (i + 1) + " (outside the selection) into the previous line: [" + ctx.PostDoc.Lines[line] + "]");
            }
            if (!String.Equals(ctx.PostDoc.Lines[line], ctx.PreDoc.Lines[i], StringComparison.Ordinal)) {
                focusPre = i;
                focusPost = line;
                return Fail(ctx, "i", "a block command on lines " + (first + 1) + ".." + (last + 1) + " rewrote line " + (i + 1) +
                    " outside it: was [" + ctx.PreDoc.Lines[i] + "], now [" + ctx.PostDoc.Lines[line] + "]");
            }
        }
        return true;
    }

    // True when a closed fence pair of the pre-document touches the selected lines first..last and
    // the post-document is exactly the pre-document without that pair's two fence lines.
    static bool FencePairToggledOff(StepContext ctx, int first, int last) {
        DocView pre = ctx.PreDoc, post = ctx.PostDoc;
        if (post == null || post.Count != pre.Count - 2) return false;
        bool inFence = false;
        char fenceChar = '\0';
        int fenceRun = 0, openLine = -1;
        for (int i = 0; i < pre.Count; i++) {
            string line = pre.Lines[i];
            int p = 0;
            while (p < line.Length && p < 3 && line[p] == ' ') p++;
            char c = p < line.Length ? line[p] : '\0';
            int run = 0;
            if (c == '`' || c == '~') while (p + run < line.Length && line[p + run] == c) run++;
            bool fenceLine = (c == '`' || c == '~') && run >= 3;
            if (!inFence) {
                if (!fenceLine) continue;
                inFence = true;
                fenceChar = c;
                fenceRun = run;
                openLine = i;
                continue;
            }
            if (!fenceLine || c != fenceChar || run < fenceRun) continue;
            bool tail = true;
            for (int q = p + run; q < line.Length; q++) if (line[q] != ' ' && line[q] != '\t') { tail = false; break; }
            if (!tail) continue;
            inFence = false;
            if (openLine > last || i < first) continue;
            int next = 0;
            bool same = true;
            for (int k = 0; k < pre.Count && same; k++) {
                if (k == openLine || k == i) continue;
                if (!String.Equals(pre.Lines[k], post.Lines[next], StringComparison.Ordinal)) same = false;
                next++;
            }
            if (same) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- Enter list continuation
    static bool CheckEnterMarker(StepContext ctx) {
        if (ctx.Action != "enter" || ctx.SelectionLength > 0 || ctx.PreCaret < 0) return true;
        int preLine = LineOf(ctx.PreDoc, ctx.PreCaret);
        LineModel model = ctx.PreModel[preLine];
        if (model.Skip || model.Rule) return true;
        string preText = ctx.PreDoc.Lines[preLine];
        Slots slots = ParseSlots(preText);
        if (!slots.HasList) return true;
        int column = ctx.PreCaret - ctx.PreDoc.Starts[preLine];
        int markerEnd = slots.ListEnd;
        if (slots.Kind == Slots.Task && markerEnd + 2 < preText.Length && preText[markerEnd] == '[' && preText[markerEnd + 2] == ']') markerEnd += 3;
        if (column < markerEnd) return true;
        // An empty quoted list item loses its marker on Enter (MarkdownStructuralInput.ExitEmptyQuote)
        // instead of continuing it; that is the app's own rule, not a marker-style break.
        if (slots.QuoteEnd > slots.IndentEnd && IsBlankText(preText.Substring(slots.ListEnd))) return true;
        // The Enter key on an empty list item outdents it or ends the list (11-01,
        // MarkdownStructuralInput.ExitEmptyListItem, the Word/Typora rule) instead of continuing it.
        if (IsBlankText(preText.Substring(markerEnd))) return true;
        Bump(checkCounts, "j");
        string head = preText.Substring(0, slots.ListStart);
        string expected;
        if (slots.Kind == Slots.Task) expected = head + "- [ ] ";
        else if (slots.Kind == Slots.Number) {
            int number = NumberAt(preText, slots);
            expected = head + (number + 1).ToString(CultureInfo.InvariantCulture) + slots.OrderedSeparator + " ";
        } else expected = head + slots.BulletChar + " ";
        int postCaret = Source(ctx.Window).CaretOffset;
        int postLine = LineOf(ctx.PostDoc, postCaret);
        string postText = ctx.PostDoc.Lines[postLine];
        if (!postText.StartsWith(expected, StringComparison.Ordinal)) {
            focusPre = preLine;
            focusPost = postLine;
            return Fail(ctx, "j", "Enter in a list item did not continue the marker: expected start [" + expected + "], line is [" + postText + "]");
        }
        return true;
    }
    // ---------------------------------------------------------------- numbered list numbering
    static bool CheckNumberedList(StepContext ctx) {
        if (ctx.SelectionLength > 0 || ctx.PreCaret < 0) return true;
        if (!(ctx.Action == "enter" || ctx.Action == "backspace" || ctx.Action == "delete" || ctx.Action == "tab")) return true;
        int preLine = LineOf(ctx.PreDoc, ctx.PreCaret);
        if (ctx.PreModel[preLine].Skip) return true;
        Slots pre = ParseSlots(ctx.PreDoc.Lines[preLine]);
        if (!pre.HasList || pre.Kind != Slots.Number) return true;
        string head = ctx.PreDoc.Lines[preLine].Substring(0, pre.ListStart);
        string preNumbers;
        if (!NumberRunOk(ctx.PreDoc, ctx.PreModel, preLine, head, out preNumbers)) return true;
        Bump(checkCounts, "k");
        int postCaret = Source(ctx.Window).CaretOffset;
        int postLine = LineOf(ctx.PostDoc, postCaret);
        if (ctx.PostModel[postLine].Skip) return true;
        Slots post = ParseSlots(ctx.PostDoc.Lines[postLine]);
        if (!post.HasList || post.Kind != Slots.Number) return true;
        string postHead = ctx.PostDoc.Lines[postLine].Substring(0, post.ListStart);
        string postNumbers;
        bool ok = NumberRunOk(ctx.PostDoc, ctx.PostModel, postLine, postHead, out postNumbers);
        if (!ok) {
            focusPre = preLine;
            focusPost = postLine;
            return Fail(ctx, "k", "numbered items at [" + postHead + "] are not 1,2,3... after " + ctx.Label +
                ": " + preNumbers + " -> " + postNumbers + " (doc says [" + postNumbers + "])");
        }
        return true;
    }
    static bool NumberRunOk(DocView doc, LineModel[] model, int line, string head, out string numbers) {
        var list = new StringBuilder();
        int start = line;
        while (start - 1 >= 0 && !model[start - 1].Skip) {
            Slots up = ParseSlots(doc.Lines[start - 1]);
            if (!up.HasList || !String.Equals(doc.Lines[start - 1].Substring(0, up.ListStart), head, StringComparison.Ordinal)) break;
            start--;
        }
        int expected = -1;
        bool ok = true;
        for (int i = start; i < doc.Count; i++) {
            if (model[i].Skip) break;
            Slots slots = ParseSlots(doc.Lines[i]);
            if (!slots.HasList || !String.Equals(doc.Lines[i].Substring(0, slots.ListStart), head, StringComparison.Ordinal)) break;
            if (slots.Kind != Slots.Number) continue;
            int value = NumberAt(doc.Lines[i], slots);
            if (list.Length > 0) list.Append(',');
            list.Append(value.ToString(CultureInfo.InvariantCulture));
            if (expected >= 0 && value != expected) ok = false;
            expected = value + 1;
        }
        numbers = list.ToString();
        return ok;
    }
    static bool IsBlankText(string text) {
        for (int i = 0; i < text.Length; i++) if (text[i] != ' ' && text[i] != '\t') return false;
        return true;
    }
    static int NumberAt(string text, Slots slots) {
        int value;
        if (slots.NumberLength > 0 && Int32.TryParse(text.Substring(slots.ListStart, slots.NumberLength), NumberStyles.None, CultureInfo.InvariantCulture, out value)) return value;
        return 1;
    }

    // ---------------------------------------------------------------- code fence balance
    static bool CheckFenceCommand(StepContext ctx) {
        if (ctx.Action != "cmd" || ctx.PreDoc.UnclosedFence) return true;
        Bump(checkCounts, "l");
        if (ctx.PostDoc.UnclosedFence) {
            int line = 0;
            for (int i = 0; i < ctx.PostDoc.Count; i++) {
                string value = ctx.PostDoc.Lines[i];
                int p = 0;
                while (p < value.Length && p < 3 && value[p] == ' ') p++;
                if (p < value.Length && (value[p] == '`' || value[p] == '~')) { line = i; break; }
            }
            focusPost = line;
            return Fail(ctx, "l", "the command " + ctx.Label + " left a code fence unclosed");
        }
        return true;
    }

    // ---------------------------------------------------------------- emphasis hygiene
    internal sealed class Slots {
        internal const int Bullet = 0, Number = 1, Task = 2;
        internal int IndentEnd, QuoteEnd, ListStart, ListEnd;
        internal bool HasList;
        internal int Kind;
        internal char BulletChar, OrderedSeparator;
        internal int NumberLength;
    }
    // The structural key parser (MarkdownStructuralInput.ParseSlots) reads the same line the key
    // handler does: indent, quote prefix, list marker after it, and a task box only when the
    // checkbox stands as a whole marker.
    static Slots ParseSlots(string text) {
        var slots = new Slots();
        int length = text.Length;
        int i = 0;
        while (i < length && (text[i] == ' ' || text[i] == '\t')) i++;
        slots.IndentEnd = i;
        while (i < length && text[i] == '>') {
            i++;
            if (i < length && text[i] == ' ') i++;
        }
        slots.QuoteEnd = i;
        while (i < length && (text[i] == ' ' || text[i] == '\t')) i++;
        slots.ListStart = i;
        slots.ListEnd = i;
        int markerEnd = -1;
        if (i < length) {
            char c = text[i];
            if ((c == '-' || c == '*' || c == '+') && i + 1 < length && text[i + 1] == ' ') {
                markerEnd = i + 2;
                slots.HasList = true;
                slots.Kind = Slots.Bullet;
                slots.BulletChar = c;
            } else if (c >= '0' && c <= '9') {
                int j = i;
                while (j < length && j - i < 9 && text[j] >= '0' && text[j] <= '9') j++;
                if (j < length && (text[j] == '.' || text[j] == ')') && j + 1 < length && text[j + 1] == ' ') {
                    markerEnd = j + 2;
                    slots.HasList = true;
                    slots.Kind = Slots.Number;
                    slots.OrderedSeparator = text[j];
                    slots.NumberLength = j - i;
                }
            }
        }
        if (slots.HasList) {
            slots.ListEnd = markerEnd;
            // The box does not need a space after it ("- [ ]done" is a task whose body is "done"),
            // so the keys read the same marker the screen draws; only a separator that is really
            // there is stepped over.
            if (slots.Kind == Slots.Bullet && markerEnd + 2 < length
                && text[markerEnd] == '[' && text[markerEnd + 2] == ']'
                && (text[markerEnd + 1] == ' ' || text[markerEnd + 1] == 'x' || text[markerEnd + 1] == 'X')) {
                slots.Kind = Slots.Task;
                slots.ListEnd = markerEnd + 3;
                if (markerEnd + 3 < length && (text[markerEnd + 3] == ' ' || text[markerEnd + 3] == '\t')) slots.ListEnd++;
            }
        }
        return slots;
    }

    internal sealed class EmphasisRun {
        internal int Start, End;
        internal string Text;
        internal char Char;
        internal bool SpaceBefore, SpaceAfter;
    }
    internal sealed class BadMarker {
        internal string Key, Kind, Run;
        internal int Offset;
        internal string Detail;
    }
    static void AddBad(List<BadMarker> list, string kind, string run, int offset, string line) {
        var bad = new BadMarker();
        // A long run ("****") and two equal runs with whitespace between ("** **") name the same
        // emptied pair; they share one key so a step that only re-spaces the pair is not new.
        string keyKind = kind, keyRun = run;
        if ((kind == "empty" || kind == "emptied") && run.Length >= 4) {
            keyKind = "empty";
            keyRun = run.Substring(0, 2);
        }
        bad.Key = keyKind + "|" + keyRun;
        bad.Kind = kind;
        bad.Run = run;
        bad.Offset = offset;
        bad.Detail = kind + " " + run + " in [" + line + "]";
        list.Add(bad);
    }
    // Runs of "*", "_" and "~~" outside code spans. A run that is a thematic break is skipped by
    // the caller. Bad shapes: a run of four or more (an empty pair merged by a deletion), two
    // equal runs with only whitespace between, an opener followed by whitespace whose matching
    // closer exists, and a closer preceded by whitespace whose opener exists.
    static List<EmphasisRun> RunsOf(string line) {
        var runs = new List<EmphasisRun>();
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
            if (c == '*' || c == '_' || c == '~') {
                int run = 1;
                while (at + run < line.Length && line[at + run] == c) run++;
                if (!(c == '~' && run != 2 && run < 4)) {
                    var entry = new EmphasisRun();
                    entry.Start = at;
                    entry.End = at + run;
                    entry.Text = new string(c, run);
                    entry.Char = c;
                    entry.SpaceBefore = at > 0 && (line[at - 1] == ' ' || line[at - 1] == '\t');
                    entry.SpaceAfter = at + run < line.Length && (line[at + run] == ' ' || line[at + run] == '\t');
                    runs.Add(entry);
                }
                at += run;
                continue;
            }
            at++;
        }
        return runs;
    }
    static void ScanEmphasisLine(string line, int lineStart, List<BadMarker> bad) {
        var runs = RunsOf(line);
        var open = new Dictionary<string,List<int>>();
        int[] partner = new int[runs.Count];
        for (int i = 0; i < runs.Count; i++) partner[i] = -1;
        var inert = new List<int>();
        for (int i = 0; i < runs.Count; i++) {
            EmphasisRun run = runs[i];
            bool canClose = !run.SpaceBefore;
            bool canOpen = run.End < line.Length && !run.SpaceAfter;
            List<int> stack;
            if (canClose && open.TryGetValue(run.Text, out stack) && stack.Count > 0) {
                int openerIndex = stack[stack.Count - 1];
                // A shorter/longer run of the same character between them means the two runs are
                // not one pair (the app's inline parser resolves the inner run first); pairing
                // them would invent a closer that the document never had.
                bool crossed = false;
                for (int k = openerIndex + 1; k < i; k++) if (runs[k].Char == run.Char && runs[k].Text != run.Text) { crossed = true; break; }
                if (!crossed) {
                    stack.RemoveAt(stack.Count - 1);
                    partner[i] = openerIndex;
                    partner[openerIndex] = i;
                    EmphasisRun opener = runs[openerIndex];
                    string between = line.Substring(opener.End, run.Start - opener.End);
                    if (between.Trim().Length == 0) AddBad(bad, "empty", run.Text, lineStart + opener.Start, line);
                    continue;
                }
            }
            if (canOpen) {
                if (!open.TryGetValue(run.Text, out stack)) { stack = new List<int>(); open[run.Text] = stack; }
                stack.Add(i);
            } else if (run.SpaceBefore) {
                inert.Add(i);
            }
        }
        // A run preceded by whitespace cannot close the pair; when it cannot open one either and
        // an opener of the same run is still unmatched, the line has a closer the renderer cannot
        // use (the "**a **" shape). A run that can open is a valid opener for the next pair, so
        // " _... _중첩_ " is not reported.
        foreach (int index in inert) {
            EmphasisRun closer = runs[index];
            // A later run of the same text may be the pair of that opener, so the inert run is
            // not the closer the line needed; only a run with nothing of its text after it can
            // name a pair that is genuinely left open.
            bool laterSame = false;
            for (int k = index + 1; k < runs.Count; k++) if (runs[k].Text == closer.Text) { laterSame = true; break; }
            if (laterSame) continue;
            int openerIndex = -1;
            for (int k = index - 1; k >= 0; k--) {
                EmphasisRun candidate = runs[k];
                if (candidate.Text != closer.Text || partner[k] >= 0) continue;
                if (candidate.End >= line.Length || candidate.SpaceAfter) continue;
                openerIndex = k;
                break;
            }
            if (openerIndex < 0) continue;
            EmphasisRun opener = runs[openerIndex];
            string between = line.Substring(opener.End, closer.Start - opener.End);
            if (between.Trim().Length == 0) AddBad(bad, "empty", closer.Text, lineStart + opener.Start, line);
            else AddBad(bad, "close-space", closer.Text, lineStart + closer.Start, line);
        }
        // A run followed by whitespace cannot open emphasis. Only the unambiguous shape is
        // reported: exactly two runs of that text and the second one ends the line or a word.
        int[] sameText = new int[runs.Count];
        for (int i = 0; i < runs.Count; i++) {
            int count = 0;
            for (int j = 0; j < runs.Count; j++) if (runs[j].Text == runs[i].Text) count++;
            sameText[i] = count;
        }
        for (int i = 0; i < runs.Count; i++) {
            EmphasisRun run = runs[i];
            if (!run.SpaceAfter || sameText[i] != 2 || partner[i] >= 0) continue;
            for (int j = i + 1; j < runs.Count; j++) {
                EmphasisRun close = runs[j];
                if (close.Text != run.Text || close.SpaceBefore || partner[j] >= 0) continue;
                bool terminated = close.End >= line.Length || close.SpaceAfter;
                string between = line.Substring(run.End, close.Start - run.End);
                if (terminated && between.Trim().Length > 0) AddBad(bad, "open-space", run.Text, lineStart + run.Start, line);
                break;
            }
        }
        // Adjacent equal runs with only whitespace between ("** **") are an empty pair. A run of
        // four or more with no partner is only reported when the step emptied a real pair on that
        // line (CheckEmphasis decides from the line's previous state and the change list).
        for (int i = 0; i < runs.Count; i++) {
            EmphasisRun run = runs[i];
            if (run.Text.Length >= 4 && run.Text.Length % 2 == 0 && partner[i] < 0) {
                AddBad(bad, "emptied", run.Text, lineStart + run.Start, line);
            }
            if (i + 1 < runs.Count && runs[i + 1].Text == run.Text && partner[i] < 0 && partner[i + 1] < 0) {
                int gap = runs[i + 1].Start - run.End;
                if (gap > 0) {
                    string between = line.Substring(run.End, gap);
                    if (between.Trim().Length == 0) AddBad(bad, "empty", run.Text, lineStart + run.Start, line);
                }
            }
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
    // The rule shape the emphasis scan has always skipped, independent of indentation: three or
    // more of one "-", "*" or "_" marker and nothing else but spaces (MarkdownLivePreview.IsRule
    // without its 0-3 column test). An indented line like "    * * *" is paragraph or list text
    // now, but it still holds no inline pair, so it must not count as broken emphasis.
    static bool RuleShape(string text) {
        char marker = '\0';
        int count = 0;
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (c == ' ' || c == '\t') continue;
            if (marker == '\0') marker = c;
            if ((c != '-' && c != '*' && c != '_') || c != marker) return false;
            count++;
        }
        return count >= 3;
    }
    static Dictionary<string,int> BadMarkerCounts(DocView doc, LineModel[] model, List<BadMarker> collected) {
        var list = new List<BadMarker>();
        for (int i = 0; i < doc.Count; i++) {
            if (doc.Fence[i] || model[i].Skip || model[i].Rule || RuleShape(doc.Lines[i])) continue;
            ScanEmphasisLine(doc.Lines[i], doc.Starts[i], list);
        }
        var counts = new Dictionary<string,int>();
        foreach (BadMarker bad in list) {
            Bump(counts, bad.Key);
            if (collected != null) collected.Add(bad);
        }
        return counts;
    }
    static bool CheckEmphasis(StepContext ctx) {
        Bump(checkCounts, "f");
        Dictionary<string,int> before = BadMarkerCounts(ctx.PreDoc, ctx.PreModel, null);
        var afterList = new List<BadMarker>();
        Dictionary<string,int> after = BadMarkerCounts(ctx.PostDoc, ctx.PostModel, afterList);
        foreach (KeyValuePair<string,int> pair in after) {
            int previous = 0;
            if (!before.TryGetValue(pair.Key, out previous)) previous = 0;
            if (pair.Value <= previous) continue;
            BadMarker hit = null, emptiedHit = null;
            foreach (BadMarker bad in afterList) {
                if (!String.Equals(bad.Key, pair.Key, StringComparison.Ordinal)) continue;
                if (hit == null) hit = bad;
                if (bad.Kind == "emptied" && emptiedHit == null) emptiedHit = bad;
            }
            // The same key covers an emptied long run and an adjacent "** **" pair. When the
            // document also holds a long run, its own emptied-pair evidence decides the increase.
            if (emptiedHit != null) {
                if (!LongRunWasPair(ctx, emptiedHit, emptiedHit.Run)) continue;
                hit = emptiedHit;
            }
            string kind = hit == null ? pair.Key.Substring(0, pair.Key.IndexOf('|')) : hit.Kind;
            string run = hit == null ? pair.Key.Substring(pair.Key.IndexOf('|') + 1) : hit.Run;
            int lineNumber = hit == null ? 0 : LineOf(ctx.PostDoc, hit.Offset) + 1;
            if (ctx.Action == "type" || ctx.Action == "paste") {
                string unit = (kind == "empty" || kind == "emptied") && run.Length >= 4 ? run.Substring(0, 2) : run;
                if (ctx.InsertedText.IndexOf(unit, StringComparison.Ordinal) >= 0) continue;
                // The brief exempts marker pairs typed literally. A single typed marker that
                // completes a pair ("*" typed next to an existing "*" giving "** **") is that
                // literal input even though the inserted text does not hold the whole run.
                if ((kind == "empty" || kind == "emptied") && run.Length > 0 && ctx.InsertedText.IndexOf(run[0]) >= 0) continue;
            }
            string lineText = lineNumber > 0 && lineNumber <= ctx.PostDoc.Count ? ctx.PostDoc.Lines[lineNumber - 1] : run;
            focusPost = lineNumber > 0 ? lineNumber - 1 : focusPost;
            return Fail(ctx, "f", "the step left broken emphasis (" + kind + " " + run + ") at line " + lineNumber + ": [" + lineText + "]");
        }
        return true;
    }
    // A run of four or more equal markers is an emptied pair only when the same line held a real
    // pair of half its length AND this step deleted text from between those two markers. A command
    // that merely adds a marker next to an existing pair, or literal tildes merged by a Backspace,
    // does not qualify.
    static bool LongRunWasPair(StepContext ctx, BadMarker hit, string run) {
        if (hit == null || run.Length < 4) return false;
        int postLine = LineOf(ctx.PostDoc, hit.Offset);
        int postStart = ctx.PostDoc.Starts[postLine];
        int preLine = -1;
        for (int i = 0; i < ctx.PreDoc.Count; i++) {
            if (Mapped(ctx, ctx.PreDoc.Starts[i]) == postStart) { preLine = i; break; }
        }
        if (preLine < 0) return false;
        string unit = run.Substring(0, 2);
        string preText = ctx.PreDoc.Lines[preLine];
        List<EmphasisRun> runs = RunsOf(preText);
        int pairStart = -1, pairEnd = -1;
        for (int i = 0; i < runs.Count && pairStart < 0; i++) {
            if (runs[i].Text != unit) continue;
            for (int j = i + 1; j < runs.Count; j++) {
                if (runs[j].Text != unit) continue;
                string between = preText.Substring(runs[i].End, runs[j].Start - runs[i].End);
                if (between.Trim().Length > 0) {
                    pairStart = ctx.PreDoc.Starts[preLine] + runs[i].End;
                    pairEnd = ctx.PreDoc.Starts[preLine] + runs[j].Start;
                }
                break;
            }
        }
        if (pairStart < 0) return false;
        for (int c = 0; c < changes.Count; c++) {
            ChangeInfo change = changes[c];
            if (change.Removed <= 0) continue;
            if (change.Offset >= pairStart && change.Offset <= pairEnd) return true;
        }
        return false;
    }
    static int CountOccurrences(string text, string piece) {
        if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(piece)) return 0;
        int count = 0;
        for (int at = text.IndexOf(piece, StringComparison.Ordinal); at >= 0; at = text.IndexOf(piece, at + piece.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    // ---------------------------------------------------------------- preview parse agreement
    static bool CheckParseAgreement(StepContext ctx) {
        Bump(checkCounts, "m:parse");
        MarkdownLivePreview.Session session = Session(ctx.Window);
        SourceEditor source = Source(ctx.Window);
        if (session == null || source == null || source.Document == null) return true;
        var document = source.Document;
        int count = Math.Min(ctx.PostDoc.Count, document.LineCount);
        for (int i = 0; i < count; i++) {
            LineModel mine = ctx.PostModel[i];
            MarkdownLivePreview.LineInfo info = session.Info(document.GetLineByNumber(i + 1));
            if (info == null) continue;
            if (mine.Skip || info.Code) continue;
            if (mine.Heading != info.Heading) {
                focusPost = i;
                return Fail(ctx, "m:heading", "the preview reads heading " + info.Heading + ", the line model " + mine.Heading +
                    " (setext=" + info.Setext + ") on line " + (i + 1) + ": [" + ctx.PostDoc.Lines[i] + "]");
            }
            if (mine.Setext != info.Setext) {
                focusPost = i;
                return Fail(ctx, "m:setext", "the preview reads setext=" + info.Setext + ", the line model " + mine.Setext +
                    " on line " + (i + 1) + ": [" + ctx.PostDoc.Lines[i] + "]");
            }
            bool theirList = info.List != null;
            if (mine.HasList != theirList) {
                focusPost = i;
                return Fail(ctx, "m:list", "the preview sees a list: " + theirList + ", the line model " + mine.HasList +
                    " on line " + (i + 1) + ": [" + ctx.PostDoc.Lines[i] + "]");
            }
            if (mine.HasList) {
                if (mine.Ordered != info.List.Ordered || mine.Task != info.List.Task) {
                    focusPost = i;
                    return Fail(ctx, "m:list", "the preview reads ordered=" + info.List.Ordered + " task=" + info.List.Task +
                        ", the line model ordered=" + mine.Ordered + " task=" + mine.Task + " on line " + (i + 1) + ": [" + ctx.PostDoc.Lines[i] + "]");
                }
                if (mine.Ordered && mine.ListNumber != info.List.Number) {
                    focusPost = i;
                    return Fail(ctx, "m:number", "the preview numbers the item " + info.List.Number + ", the line model " + mine.ListNumber +
                        " on line " + (i + 1) + ": [" + ctx.PostDoc.Lines[i] + "]");
                }
            }
        }
        return true;
    }
    // ---------------------------------------------------------------- fixtures
    // No pipe character and no "[id]:" definition line: this probe is about formatting outside
    // tables, so a table must never appear from the fixture or from a paste snippet.
    static string Fixture(int seed) {
        string nl = seed % 2 == 0 ? "\r\n" : "\n";
        var text = new StringBuilder();
        switch ((seed - 1) % 4) {
            case 0:
                Line(text, nl, "# 장 제목 1");
                Line(text, nl, "");
                Line(text, nl, "문단 **굵게** *기울임* ~~취소선~~ `인라인 코드` 그리고 보통 글자입니다.");
                Line(text, nl, "링크 [MDNote](https://example.com/page) 와 <span style=\"color:#B91C1C\">빨강 글자</span>, <span style=\"font-size:20px\">큰 글자</span>.");
                Line(text, nl, "둘째 줄 😀 日本語 텍스트<br>줄바꿈 표시.");
                Line(text, nl, "");
                Line(text, nl, "## 장 제목 2");
                Line(text, nl, "");
                Line(text, nl, "- 첫째 항목");
                Line(text, nl, "- 둘째 **강조** 항목");
                Line(text, nl, "  - 중첩 항목");
                Line(text, nl, "- [ ] 할 일 하나");
                Line(text, nl, "- [x] 끝난 일 하나");
                Line(text, nl, "");
                Line(text, nl, "1. 번호 하나");
                Line(text, nl, "2. 번호 둘");
                Line(text, nl, "3. 번호 셋");
                Line(text, nl, "");
                Line(text, nl, "> 인용 문장입니다");
                Line(text, nl, "> > 중첩 인용 문장");
                Line(text, nl, ">");
                Line(text, nl, "> - 인용 속 목록 하나");
                Line(text, nl, "> - 인용 속 목록 둘");
                Line(text, nl, "");
                Line(text, nl, "```text");
                Line(text, nl, "**코드 안 별표** 와 `백틱`");
                Line(text, nl, "```");
                Line(text, nl, "");
                Line(text, nl, "---");
                Line(text, nl, "");
                Line(text, nl, "### 장 제목 3");
                Line(text, nl, "마지막 문단.");
                break;
            case 1:
                Line(text, nl, "Setext 큰 제목");
                Line(text, nl, "==============");
                Line(text, nl, "");
                Line(text, nl, "문단 **굵게** *기울임* 그리고 `코드`.");
                Line(text, nl, "Setext 작은 제목");
                Line(text, nl, "----------------");
                Line(text, nl, "");
                Line(text, nl, "- 항목 하나");
                Line(text, nl, "- 항목 둘");
                Line(text, nl, "  - 하위 항목 하나");
                Line(text, nl, "  - 하위 항목 둘");
                Line(text, nl, "1. 번호 하나");
                Line(text, nl, "2. 번호 둘");
                Line(text, nl, "3. 번호 셋");
                Line(text, nl, "");
                Line(text, nl, "> 인용 하나");
                Line(text, nl, "> - 인용 목록 하나");
                Line(text, nl, "> - 인용 목록 둘");
                Line(text, nl, "> 1. 인용 번호 하나");
                Line(text, nl, "> 2. 인용 번호 둘");
                Line(text, nl, "");
                Line(text, nl, "```text");
                Line(text, nl, "코드 블록 내용");
                Line(text, nl, "```");
                Line(text, nl, "");
                Line(text, nl, "마지막 문단 😀.");
                break;
            case 2:
                Line(text, nl, "# 목록 시험 문서");
                Line(text, nl, "");
                Line(text, nl, "- 항목 하나");
                Line(text, nl, "- 항목 둘");
                Line(text, nl, "  - 중첩 하나");
                Line(text, nl, "  - 중첩 둘");
                Line(text, nl, "    - 깊은 항목");
                Line(text, nl, "- 항목 셋");
                Line(text, nl, "- [ ] 할 일 하나");
                Line(text, nl, "- [x] 끝난 일 하나");
                Line(text, nl, "");
                Line(text, nl, "1. 첫째");
                Line(text, nl, "2. 둘째");
                Line(text, nl, "   1. 하위 번호 하나");
                Line(text, nl, "   2. 하위 번호 둘");
                Line(text, nl, "3. 셋째");
                Line(text, nl, "");
                Line(text, nl, "> - 인용 목록 하나");
                Line(text, nl, "> - 인용 목록 둘");
                Line(text, nl, ">   - 인용 중첩 항목");
                Line(text, nl, "> 1. 인용 번호 하나");
                Line(text, nl, "> 2. 인용 번호 둘");
                Line(text, nl, "");
                Line(text, nl, "> > 깊은 인용");
                Line(text, nl, "> > 문장입니다");
                Line(text, nl, "");
                Line(text, nl, "문단 日本語 😀.");
                break;
            default:
                Line(text, nl, "# 코드와 글자");
                Line(text, nl, "");
                Line(text, nl, "`a * b` 와 ``코드 `안` 백틱`` 과 **굵게 _중첩_ 기울임**.");
                Line(text, nl, "<a href=\"https://example.com\">HTML 링크</a> 와 [일반 링크](https://example.com/2).");
                Line(text, nl, "");
                Line(text, nl, "~~~csharp");
                Line(text, nl, "int value = 1; // **굵지 않음**");
                Line(text, nl, "string text = \"`따옴표`\";");
                Line(text, nl, "~~~");
                Line(text, nl, "");
                Line(text, nl, "```");
                Line(text, nl, "꺼진 코드 **별표**");
                Line(text, nl, "```");
                Line(text, nl, "");
                Line(text, nl, "### 제목 단계 3");
                Line(text, nl, "#### 제목 단계 4");
                Line(text, nl, "");
                Line(text, nl, "> 인용 **강조** 문장");
                Line(text, nl, "- 마지막 목록 항목");
                Line(text, nl, "");
                Line(text, nl, "마지막 줄 Korean 日本語 😀.");
                break;
        }
        return text.ToString();
    }
    static void Line(StringBuilder text, string newline, string value) {
        text.Append(value).Append(newline);
    }
    // The fixture must already satisfy the line model, the preview parse and the emphasis hygiene
    // before a single step runs; a disagreement here means the generated document (or the model)
    // is wrong, not the product.
    static bool CheckFixture(MainWindow window) {
        string text = DocText(window);
        DocView doc = Build(text);
        LineModel[] model = ParseModel(doc);
        if (doc.UnclosedFence) { FailOpen("parse", "the fixture has an unclosed code fence"); return false; }
        MarkdownLivePreview.Session session = Session(window);
        SourceEditor source = Source(window);
        if (session == null || source == null) { FailOpen("parse", "no preview session"); return false; }
        int headings = 0, lists = 0;
        for (int i = 0; i < doc.Count; i++) {
            if (model[i].Heading > 0) headings++;
            if (model[i].HasList) lists++;
            MarkdownLivePreview.LineInfo info = session.Info(source.Document.GetLineByNumber(i + 1));
            if (info == null) continue;
            if (model[i].Skip || info.Code) continue;
            if (model[i].Heading != info.Heading) {
                FailOpen("parse", "fixture heading disagreement on line " + (i + 1) + ": preview " + info.Heading + ", model " + model[i].Heading + ": [" + doc.Lines[i] + "]");
                return false;
            }
            if (model[i].Setext != info.Setext) {
                FailOpen("parse", "fixture setext disagreement on line " + (i + 1) + ": preview " + info.Setext + ", model " + model[i].Setext + ": [" + doc.Lines[i] + "]");
                return false;
            }
            bool theirList = info.List != null;
            if (model[i].HasList != theirList) {
                FailOpen("parse", "fixture list disagreement on line " + (i + 1) + ": preview " + theirList + ", model " + model[i].HasList + ": [" + doc.Lines[i] + "]");
                return false;
            }
            if (model[i].HasList) {
                if (model[i].Ordered != info.List.Ordered || model[i].Task != info.List.Task || (model[i].Ordered && model[i].ListNumber != info.List.Number)) {
                    FailOpen("parse", "fixture list shape disagreement on line " + (i + 1) + ": preview ordered=" + info.List.Ordered + " task=" + info.List.Task +
                        " number=" + info.List.Number + ", model ordered=" + model[i].Ordered + " task=" + model[i].Task + " number=" + model[i].ListNumber + ": [" + doc.Lines[i] + "]");
                    return false;
                }
            }
        }
        if (headings == 0 || lists == 0) {
            FailOpen("parse", "the fixture is missing headings or lists (headings=" + headings + ", lists=" + lists + ")");
            return false;
        }
        var bad = new List<BadMarker>();
        BadMarkerCounts(doc, model, bad);
        if (bad.Count > 0) {
            FailOpen("parse", "the fixture already holds broken emphasis: " + bad[0].Detail);
            return false;
        }
        return true;
    }
}
}
