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
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// --table-abuse-probe <folder>: hundreds of random-but-seeded abnormal edit sequences on tables and
// inline formatting in the live editor (Basic and Live view); after every step the document must
// still be a valid table/markup and the caret must stay inside a cell, and undo must restore the
// segment start exactly. Fixtures are generated here from the seed; no user file is read.
// Report: <folder>\table-abuse-probe.txt; table-abuse-probe-PASS.txt only when every sequence kept
// every invariant.
//
// Cut runs the app's own ApplicationCommands.Cut path with MainWindow.LiveTableCutHook installed,
// so the DataObject is captured instead of the system clipboard (the clipboard is shared with the
// user's desktop and a probe must not clobber it). Copy is not document-changing and is not driven.
internal static class TableAbuseProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int FirstSeedDefault = 1, LastSeedDefault = 8, ActionsPerSeedDefault = 400, SegmentLength = 200, UndoCap = 470;
    const long DefaultBudgetMilliseconds = 300000;

    internal sealed class ChangeInfo {
        internal int Offset, Removed, Inserted;
        internal string Text, RemovedText;
        internal bool Fallback;
    }
    internal sealed class TableInfo {
        internal int Header, Delimiter, Last;
        internal int HeaderStart, HeaderEnd, BlockEnd;
        internal List<string> LineTexts = new List<string>();
    }
    internal sealed class FailureInfo {
        internal int Seed, Step;
        internal string Mode, Fixture, Action, Invariant, Detail, Before, After, Steps, Changes;
    }
    internal sealed class StepContext {
        internal MainWindow Window;
        internal string Action, Label, Payload;
        internal int Command;
        internal string PreText, PostText, InsertedText;
        internal DocView PreDoc, PostDoc;
        internal int[] Map;
        internal List<TableInfo> PreTables;
        internal bool BlockCommand, IsUndo, SelectionMultiLine;
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
    static bool fallbackEdit;   // the press was not handled by the app's key path and ran the editor command
    static bool all;
    static MainWindow currentWindow;
    static EventHandler<DocumentChangeEventArgs> changeHandler;
    static int focusPre = -1, focusPost = -1;
    static Stopwatch watch;
    static long budgetMilliseconds = DefaultBudgetMilliseconds;
    static string skipPrefix;
    static int firstSeed = FirstSeedDefault, lastSeed = LastSeedDefault, actionsPerSeed = ActionsPerSeedDefault;
    static int modeFilter = -1;   // -1 = both, 0 = basic only, 1 = live only
    static bool budgetReached;

    internal static int Run(string folder) {
        string dir = Path.GetFullPath(folder);
        Directory.CreateDirectory(dir);
        outRoot = dir;
        string passFile = Path.Combine(dir, "table-abuse-probe-PASS.txt");
        string failFile = Path.Combine(dir, "table-abuse-probe-FAIL.txt");
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
            File.WriteAllText(Path.Combine(dir, "table-abuse-probe.txt"), text, new UTF8Encoding(false));
            if (all && !budgetReached) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
            else File.WriteAllText(failFile, text, new UTF8Encoding(false));
        } catch (IOException) {
            return 1;
        }
        return all && !budgetReached ? 0 : 1;
    }

    // A reduced run replays one seed/mode; the default (no variables) is the full suite.
    //   MDNOTE_TABLE_ABUSE_SEED=3 MDNOTE_TABLE_ABUSE_MODE=live MDNOTE_TABLE_ABUSE_STEPS=120
    //   MDNOTE_TABLE_ABUSE_BUDGET=600000  MDNOTE_TABLE_ABUSE_SKIP=table:
    static void ReadOptions() {
        firstSeed = FirstSeedDefault; lastSeed = LastSeedDefault; actionsPerSeed = ActionsPerSeedDefault;
        budgetMilliseconds = DefaultBudgetMilliseconds; skipPrefix = null; modeFilter = -1;
        string value = Environment.GetEnvironmentVariable("MDNOTE_TABLE_ABUSE_SEED");
        int number;
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) {
            firstSeed = number; lastSeed = number;
        }
        value = Environment.GetEnvironmentVariable("MDNOTE_TABLE_ABUSE_STEPS");
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) actionsPerSeed = number;
        value = Environment.GetEnvironmentVariable("MDNOTE_TABLE_ABUSE_BUDGET");
        if (!String.IsNullOrEmpty(value) && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out budgetMilliseconds) && budgetMilliseconds <= 0) budgetMilliseconds = DefaultBudgetMilliseconds;
        value = Environment.GetEnvironmentVariable("MDNOTE_TABLE_ABUSE_SKIP");
        if (!String.IsNullOrEmpty(value)) skipPrefix = value;
        value = Environment.GetEnvironmentVariable("MDNOTE_TABLE_ABUSE_MODE");
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
        MainWindow window = null;
        string surface = "(not opened)";
        int actions = 0;
        var timer = Stopwatch.StartNew();
        bool runOk = true;
        try {
            window = CreateWindow();
            currentWindow = window;
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
            currentWindow = null;
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
            if (!CheckUndoOracle(window, seed, segmentStart)) return false;
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
        ctx.PreTables = FindTables(ctx.PreDoc);
        SourceEditor source = Source(window);
        ctx.SelectionStart = source == null ? 0 : source.SelectionStart;
        ctx.SelectionLength = source == null ? 0 : source.SelectionLength;
        ctx.PreCaret = source == null ? -1 : source.CaretOffset;
        ctx.PreCaretLine = source == null ? -1 : source.TextArea.Caret.Line;
        if (source != null && ctx.SelectionLength > 0 && source.Document != null) {
            string selected = source.Document.GetText(ctx.SelectionStart, ctx.SelectionLength);
            ctx.SelectionMultiLine = selected.IndexOf('\n') >= 0 || selected.IndexOf('\r') >= 0;
        }
        ctx.BlockCommand = IsBlockCommand(label);
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
        failure.Detail = OneLine(detail) +
            (ctx == null ? "" : " [pre caret " + ctx.PreCaret + " line " + ctx.PreCaretLine + CaretSpot(ctx) + ", selection " + ctx.SelectionStart + "+" + ctx.SelectionLength +
            ", attached=" + (currentWindow == null ? "?" : Prop<bool>(currentWindow, "LivePreviewAttached").ToString()) + "]");
        failure.Steps = StepListText();
        failure.Changes = DescribeChanges();
        if (ctx != null) {
            int line = focusPre;
            if (line < 0 && ctx.PreTables.Count > 0) line = ctx.PreTables[0].Header;
            failure.Before = Excerpt(ctx.PreDoc, line);
            int afterLine = focusPost;
            if (afterLine < 0 && ctx.PostDoc != null && ctx.PreTables.Count > 0) {
                int mapped = Mapped(ctx, ctx.PreTables[0].HeaderStart);
                if (mapped >= 0 && mapped <= ctx.PostDoc.Text.Length) afterLine = LineOf(ctx.PostDoc, mapped);
            }
            failure.After = Excerpt(ctx.PostDoc, afterLine);
        } else {
            failure.Before = "(none)";
            failure.After = "(none)";
        }
        failures.Add(failure);
        return false;
    }
    // The caret's offset inside its own source line and the characters there: a key failure is
    // only readable when the log says which spot of the row the step started from.
    static string CaretSpot(StepContext ctx) {
        if (ctx.PreCaret < 0 || ctx.PreDoc == null || ctx.PreDoc.Count == 0) return "";
        int line = LineOf(ctx.PreDoc, ctx.PreCaret);
        int relative = ctx.PreCaret - ctx.PreDoc.Starts[line];
        string text = ctx.PreDoc.Lines[line];
        int from = Math.Max(0, relative - 6);
        int take = Math.Max(0, Math.Min(12, text.Length - from));
        return ", rel " + relative + " near [" + OneLine(from < text.Length ? text.Substring(from, take) : "") + "]";
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
    static bool CheckUndoOracle(MainWindow window, int seed, string segmentStart) {
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
            if (String.Equals(undone, segmentStart, StringComparison.Ordinal) && redoSteps == undoSteps &&
                String.Equals(redone, beforeUndo, StringComparison.Ordinal)) return true;
            string detail = "undo " + undoSteps + " steps to segment start: " + (String.Equals(undone, segmentStart, StringComparison.Ordinal) ? "same" : Diff(segmentStart, undone)) +
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
        text.Append("# TableAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- seeds: ").Append(firstSeed).Append("..").Append(lastSeed).Append(", ").Append(actionsPerSeed)
            .Append(" steps per sequence, ").Append(SegmentLength).Append("-step history segments\n");
        text.Append("- modes: basic (HideMarkers true), live (HideMarkers false)\n");
        text.Append("- fixtures: generated from the seed only; no user file is read\n");
        text.Append("- surface: offscreen MainWindow, live editor, real command/key/paste entry points\n");
        text.Append("- changes: -removed +inserted [inserted text] rm=[removed text] fb=the app key path left the press to the editor command\n");
        text.Append("- checks: text=window.text mirror, b=header+delimiter intact, b:parse=preview agrees, c=column cells, d=block line count, e=block command kept the table, f=emphasis hygiene, g:undo=undo/redo oracle, h=caret inside a cell\n");
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
                if (!String.IsNullOrEmpty(failure.Changes)) text.Append("- changes: ").Append(failure.Changes).Append('\n');
            }
        }
        text.Append("\nResult: ").Append(all && !budgetReached ? "PASS" : "FAIL").Append('\n');        text.Append("Runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms\n");
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
    static string DescribeChanges() {
        var text = new StringBuilder();
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            if (i > 0) text.Append("; ");
            text.Append("@").Append(change.Offset).Append(" -").Append(change.Removed).Append(" +").Append(change.Inserted)
                .Append(" [").Append(OneLine(change.Text)).Append(']');
            if (change.Removed > 0) text.Append(" rm=[").Append(OneLine(change.RemovedText)).Append(']');
            if (change.Fallback) text.Append(" fb");
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
    // mapped into the post text and a merge/split is attributed to the step that caused it.
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
            // The removed text names what a key or command actually took; without it a failure
            // that removed a "|" cannot be told apart from one that only removed cell text.
            change.RemovedText = args.RemovalLength > 0 && args.RemovalLength <= 2000 && args.RemovedText != null ? args.RemovedText.Text : "";
            change.Fallback = fallbackEdit;
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
    const int TotalWeight = 126;
    static readonly string[] NavNames = new string[] { "left", "right", "up", "down", "home", "end", "word-left", "word-right", "page-up", "page-down" };
    static readonly string[] SelectNames = new string[] { "left", "right", "up", "down", "home", "end", "word-left", "word-right", "page-up", "page-down", "doc-start", "doc-end" };
    static readonly string[] CommandNames = new string[] {
        "bold", "italic", "strike", "inline-code", "h0", "h1", "h2", "h3", "h4", "h5", "h6",
        "bullet", "numbered", "task", "quote", "code-block", "rule"
    };
    static readonly string[] TableNames = new string[] {
        "rowadd", "rowremove", "coladd", "colremove", "delete", "paragraph",
        "align-left", "align-center", "align-right", "outdent", "indent", "insert"
    };
    static readonly string KoreanSyllables = "가나다라마바사아자차카타파하거너더러머버서어저처커터퍼허고노도로모보소오조초코토포호구누두루무부수우주추쿠투푸후기니디리미비시이지치키티피히값메모표행열";
    static readonly string LatinChars = "abcdefghijklmnopqrstuvwxyz0123456789";

    static void ChooseAction(Random random, out string action, out int command, out string payload) {
        action = "type";
        command = 0;
        payload = null;
        int roll = random.Next(TotalWeight);
        if (roll < 18) { action = "type"; payload = TypingText(random); }
        else if (roll < 28) action = "click";
        else if (roll < 38) action = "backspace";
        else if (roll < 45) action = "delete";
        else if (roll < 52) { action = "tab"; command = random.Next(2); }
        else if (roll < 60) action = "enter";
        else if (roll < 70) { action = "nav"; command = random.Next(NavNames.Length); }
        else if (roll < 78) { action = "select"; command = random.Next(SelectNames.Length); }
        else if (roll < 83) action = "cut";
        else if (roll < 89) { action = "paste"; command = random.Next(10); payload = PasteSnippet(command); }
        else if (roll < 97) { action = "cmd"; command = random.Next(CommandNames.Length); }
        else if (roll < 106) { action = "table"; command = random.Next(TableNames.Length); }
        else if (roll < 110) action = "undo";
        else if (roll < 113) action = "redo";
        else action = "mode";
    }
    static string Label(string action, int command, string payload) {
        if (action == "type") return "type:" + (payload == " " ? "space" : payload);
        if (action == "tab") return command == 0 ? "tab" : "shift-tab";
        if (action == "nav") return "nav:" + NavNames[command];
        if (action == "select") return "select:" + SelectNames[command];
        if (action == "paste") return "paste:" + command.ToString(CultureInfo.InvariantCulture);
        if (action == "cmd") return "cmd:" + CommandNames[command];
        if (action == "table") return "table:" + TableNames[command];
        return action;
    }
    // Block commands must never rewrite a table line (B3); the code block button is excluded because
    // inside a cell it writes inline code instead of a fence, which may change cell text.
    static bool IsBlockCommand(string label) {
        return label == "cmd:h0" || label == "cmd:h1" || label == "cmd:h2" || label == "cmd:h3" ||
            label == "cmd:h4" || label == "cmd:h5" || label == "cmd:h6" ||
            label == "cmd:bullet" || label == "cmd:numbered" || label == "cmd:task" ||
            label == "cmd:quote" || label == "cmd:rule";
    }

    static void Execute(MainWindow window, Random random, string action, int command, string payload) {
        if (action == "type") { Source(window).TextArea.PerformTextInput(payload); return; }
        if (action == "click") { ClickCell(window, random); return; }
        if (action == "backspace") { int count = 1 + random.Next(25); for (int i = 0; i < count; i++) BackspaceOnce(window); return; }
        if (action == "delete") { int count = 1 + random.Next(25); for (int i = 0; i < count; i++) DeleteOnce(window); return; }
        if (action == "tab") { Invoke(window, "SourceTabPathProbe", command == 1); return; }
        if (action == "enter") { EnterStep(window); return; }
        if (action == "nav") { NavigationCommand(command).Execute(null, Source(window).TextArea); return; }
        if (action == "select") { SelectCommand(command).Execute(null, Source(window).TextArea); return; }
        if (action == "cut") { CutStep(window); return; }
        if (action == "paste") { Invoke(window, "LivePaste", new System.Windows.DataObject(), true, payload); return; }
        if (action == "cmd") { ExecuteCommand(window, command); return; }
        if (action == "table") { ExecuteTable(window, random, command); return; }
        if (action == "undo") { Invoke(window, "Undo"); return; }
        if (action == "redo") { Invoke(window, "Redo"); return; }
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
            default: Invoke(window, "LiveRule"); return;
        }
    }
    // The table tool row's actions: 0..8 are EditTable (row add/remove, column add/remove, delete
    // table, paragraph after the gap, align left/center/right), 9/10 outdent/indent the whole table
    // and 11 is the insert-table button (the app counts the header row).
    static void ExecuteTable(MainWindow window, Random random, int index) {
        if (index == 11) { Invoke(window, "InsertTable", 2 + random.Next(3), 1 + random.Next(3)); return; }
        Invoke(window, "EditTable", index);
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
    static void BackspaceOnce(MainWindow window) {
        if (RaisePreviewKey(window, Key.Back)) return;
        SourceEditor source = Source(window);
        if (source == null) return;
        fallbackEdit = true;
        try { EditingCommands.Backspace.Execute(null, source.TextArea); } finally { fallbackEdit = false; }
    }
    static void DeleteOnce(MainWindow window) {
        if (RaisePreviewKey(window, Key.Delete)) return;
        SourceEditor source = Source(window);
        if (source == null) return;
        fallbackEdit = true;
        try { EditingCommands.Delete.Execute(null, source.TextArea); } finally { fallbackEdit = false; }
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
    // ---------------------------------------------------------------- click and cut
    // A press in a cell, resolved through the session's point-to-cell mapping (the same seam the
    // real PreviewMouseDown uses). Start, middle, end and the padding near the box top are sampled;
    // a point that cannot be placed falls back to the cell's own source offsets.
    static void ClickCell(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        MarkdownLivePreview.Session session = Session(window);
        if (source == null || session == null || source.Document == null) return;
        var document = source.Document;
        var rows = new List<DocumentLine>();
        for (int number = 1; number <= document.LineCount; number++) {
            DocumentLine line = document.GetLineByNumber(number);
            MarkdownLivePreview.LineInfo info = session.Info(line);
            if (info != null && info.Table && !info.Delimiter && info.Cells != null && info.Cells.Count > 0) rows.Add(line);
        }
        if (rows.Count == 0) {
            source.Select(document.TextLength, 0);
            return;
        }
        DocumentLine pick = rows[random.Next(rows.Count)];
        int cellIndex = random.Next(session.Info(pick).Cells.Count);
        int sample = random.Next(3);
        source.ScrollToLine(pick.LineNumber - 1);
        window.UpdateLayout();
        session.View.EnsureVisualLines();
        DocumentLine row = document.GetLineByNumber(pick.LineNumber);
        VisualLine visual = session.View.GetOrConstructVisualLine(row);
        Rect box = session.TableCellBox(visual, cellIndex);
        bool placed = false;
        if (!box.IsEmpty && box.Width > 2 && box.Height > 2) {
            double x = box.Left + box.Width * (sample == 0 ? 0.04 : sample == 1 ? 0.5 : 0.96);
            if (x > box.Right - 0.5) x = box.Right - 0.5;
            if (x < box.Left + 0.5) x = box.Left + 0.5;
            double y = sample == 0 ? box.Top + Math.Min(box.Height * 0.25, 3) : box.Top + box.Height * 0.5;
            session.BeginPress();
            try {
                placed = session.PlaceTableCaret(new Point(x, y), false);
            } finally {
                session.EndPress();
            }
            Pump();
        }
        if (placed) return;
        MarkdownLivePreview.LineInfo info2 = session.Info(document.GetLineByNumber(row.LineNumber));
        if (info2.Cells == null || info2.Cells.Count == 0) return;
        int cell = Math.Min(cellIndex, info2.Cells.Count - 1);
        int start = row.Offset + info2.Cells[cell].Start;
        int end = row.Offset + info2.Cells[cell].End;
        int at = sample == 0 ? start : sample == 1 ? (start + end) / 2 : end;
        source.Select(at, 0);
        Pump();
    }
    // A real cut (ApplicationCommands.Cut) only when the selection reaches across a cell boundary:
    // that is the branch LiveTableCut handles, and the probe hook keeps the DataObject off the
    // system clipboard. Every other selection is left to the ordinary delete path instead of
    // replacing the user's clipboard.
    static void CutStep(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        if (source.SelectionLength == 0) {
            EditingCommands.SelectLeftByCharacter.Execute(null, source.TextArea);
            if (source.SelectionLength == 0) EditingCommands.SelectRightByCharacter.Execute(null, source.TextArea);
        }
        if (source.SelectionLength == 0) return;
        if (!Prop<bool>(window, "LiveFormat") || !Prop<bool>(window, "LivePreviewAttached")) return;
        int start = source.SelectionStart, length = source.SelectionLength;
        var cells = (List<MarkdownLivePreview.Cell>)Invoke(window, "SourceRowCells", start);
        if (cells == null || cells.Count == 0) return;
        if (!MarkdownTableCommands.SpansCellBoundary(source.Document, start, length, cells)) return;
        // The handler declines (and the vendor cut would take the system clipboard) when its own
        // clear cannot be built, so the same call gates the command here.
        if (MarkdownTableCommands.ClearCellSelection(source.Document, start, length, cells) == null) return;
        MainWindow.LiveTableCutHook = delegate(System.Windows.DataObject data) { };
        try {
            ApplicationCommands.Cut.Execute(null, source.TextArea);
        } finally {
            MainWindow.LiveTableCutHook = null;
        }
        Pump();
    }
    // ---------------------------------------------------------------- payloads
    static string TypingText(Random random) {
        switch (random.Next(10)) {
            case 0: return "가나다";
            case 1: return RandomOf(random, KoreanSyllables, 1 + random.Next(3));
            case 2: return " ";
            case 3: return "|";
            case 4: return "*";
            case 5: return "`";
            case 6: return "~~";
            case 7: return "**";
            case 8: return RandomOf(random, LatinChars, 1 + random.Next(5));
            default: return "日本語";
        }
    }
    static string RandomOf(Random random, string alphabet, int count) {
        var text = new StringBuilder(count);
        for (int i = 0; i < count; i++) text.Append(alphabet[random.Next(alphabet.Length)]);
        return text.ToString();
    }
    static string PasteSnippet(int index) {
        switch (index) {
            case 0: return "**굵은 붙여넣기** 문장";
            case 1: return "- 목록 항목\n- 두 번째 항목\n";
            case 2: return "| 가 | 나 |\n| --- | --- |\n| 다 | 라 |\n";
            case 3: return "`인라인 코드` 와 *기울임*";
            case 4: return "가 | 나 | 다";
            case 5: return "여러 줄\n붙여넣기\n문장입니다.\n";
            case 6: return "a\tb | c";
            case 7: return "| x | y | z |";
            case 8: return "<span style=\"font-size:18px\">붙인 글자</span>";
            default: return "한글 붙여넣기 값 42";
        }
    }
    // ---------------------------------------------------------------- document model
    // The text is read as plain lines and cells independently of the preview parser, so a parser
    // fault cannot hide a broken document; a separate check compares the preview's view with this
    // model line by line.
    internal sealed class DocView {
        internal string Text;
        internal readonly List<int> Starts = new List<int>();
        internal readonly List<int> Ends = new List<int>();
        internal readonly List<int> Delims = new List<int>();
        internal readonly List<string> Lines = new List<string>();
        internal readonly List<bool> Fence = new List<bool>();
        internal int Count { get { return Lines.Count; } }
    }
    internal sealed class BadMarker {
        internal string Key;
        internal int Offset;
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
    // Pipes that delimit cells: an unescaped "|" outside a `code span` (the same rule the table
    // commands and the preview use, implemented here so a broken split cannot hide itself).
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
    // Every content segment between the pipes, with the first and last dropped when empty (a
    // leading or trailing pipe names no cell); interior empty cells stay, exactly like the
    // preview's SplitCells.
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
    // The model's cell spans as "start..end,..." so a count failure names the separator run that
    // went missing instead of only the number.
    static string CellSpanText(string text) {
        int[] starts, ends;
        CellsOf(text, out starts, out ends);
        var spans = new StringBuilder();
        for (int i = 0; i < starts.Length; i++) {
            if (i > 0) spans.Append(',');
            spans.Append(starts[i]).Append("..").Append(ends[i]);
        }
        return spans.ToString();
    }
    // Structural column cells: the segments the source's own unescaped "|" separators name,
    // with no code-span pairing. The app's renderer reads a pipe inside a code span as cell
    // text (MarkdownLivePreview.TablePipes, and the "cell index code span pipe" table-command
    // test), so an inline edit that opens or closes a code span can change CellCount without a
    // single separator changing. The column-count invariant is about the separators the user
    // typed (a pipe or a newline), so it compares this count.
    static void ScanSeparators(string text, List<int> pipes) {
        int at = 0;
        while (at < text.Length) {
            char c = text[at];
            if (c == '\\') { at += 2; continue; }
            if (c == '|') pipes.Add(at);
            at++;
        }
    }
    static int SeparatorCellCount(string text) {
        if (text == null) return 0;
        var pipes = new List<int>();
        ScanSeparators(text, pipes);
        int cells = 0;
        int segmentStart = 0;
        for (int i = 0; i <= pipes.Count; i++) {
            int segmentEnd = i < pipes.Count ? pipes[i] : text.Length;
            int a = segmentStart, b = segmentEnd;
            while (a < b && (text[a] == ' ' || text[a] == '\t')) a++;
            while (b > a && (text[b - 1] == ' ' || text[b - 1] == '\t')) b--;
            bool interior = i > 0 && i < pipes.Count;
            if (a != b || interior) cells++;
            segmentStart = segmentEnd + 1;
        }
        return cells;
    }
    // A line the renderer keeps as a table line: a pipe the renderer reads as a separator (an
    // escaped or code-span pipe is text). Outer pipes are not required any more: the product
    // makes a header + delimiter pair with the same cell count a table, and every following
    // piped line stays a row (MarkdownLivePreview.ParseLine's "before.Table" state).
    static bool IsRowLine(string text) {
        return text != null && HasPipe(text);
    }
    // The app's table parser reads a "|" inside a code span as cell text, not a separator
    // (MarkdownLivePreview.TablePipes; its own fixture here writes "`코드|막대`" inside one
    // cell, and the table commands' "cell index code span pipe" test states the same rule). So
    // a step that only changed the backtick pairing of the line can change the cell split with
    // every separator untouched. That is inline markup, not the pipe/newline column edit this
    // invariant names, so it is excluded; a step that moved a separator still fails.
    static int BacktickCount(string text) {
        int count = 0;
        for (int i = 0; i < text.Length; i++) if (text[i] == '`') count++;
        return count;
    }
    static bool CodeSpanResplit(string pre, string post) {
        if (pre == null || post == null) return false;
        if (SeparatorCellCount(pre) != SeparatorCellCount(post)) return false;
        return BacktickCount(pre) != BacktickCount(post);
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
        for (int i = 0; i < count; i++) {
            if (doc.Fence[i]) continue;
            hasPipe[i] = HasPipe(doc.Lines[i]);
            delimiter[i] = IsDelimiterText(doc.Lines[i]);
        }
        var tables = new List<TableInfo>();
        int at = 0;
        while (at < count - 1) {
            // The renderer's two-line rule, outer pipes not required: a piped header and a
            // delimiter row with the same cell count open a table; every following piped line is
            // a row. A before-first-pipe cell is a cell like any other (SplitCells keeps it).
            if (hasPipe[at] && delimiter[at + 1] && CellCount(doc.Lines[at]) == CellCount(doc.Lines[at + 1])) {
                int last = at + 1;
                while (last + 1 < count && hasPipe[last + 1] && !doc.Fence[last + 1]) last++;
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
    // Pre-text position to post-text position across the step's change list. The changes arrive in
    // the order the document applied them (a table command applies its row edits bottom-up), so the
    // map is replayed change by change over every pre position instead of summed as deltas.
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
    // ---------------------------------------------------------------- invariants
    static bool CheckStep(StepContext ctx) {
        MainWindow window = ctx.Window;
        Bump(checkCounts, "text");
        string mirror = null;
        try { mirror = Prop<string>(window, "text"); } catch (Exception) { mirror = null; }
        if (mirror == null || !String.Equals(mirror, ctx.PostText, StringComparison.Ordinal)) {
            return Fail(ctx, "text", "window.text differs from the editor document: " + Diff(ctx.PostText, mirror));
        }
        // A selection that reaches past the table is an ordinary multi-line replacement: the app's
        // cell rules decline it on purpose, so only a selection that stays inside one table block
        // is held to the table invariants.
        bool insideTable = false;
        if (ctx.SelectionMultiLine && ctx.SelectionLength > 0) {
            int selectionEnd = ctx.SelectionStart + ctx.SelectionLength;
            foreach (TableInfo table in ctx.PreTables) {
                if (ctx.SelectionStart >= table.HeaderStart && selectionEnd <= table.BlockEnd) { insideTable = true; break; }
            }
        }
        bool skipStructural = ctx.SelectionMultiLine && !insideTable;
        if (!ctx.IsUndo && !skipStructural) {
            if (ctx.BlockCommand && !CheckBlockCommands(ctx)) return false;
            for (int i = 0; i < ctx.PreTables.Count; i++) {
                if (!CheckTable(ctx, ctx.PreTables[i])) return false;
            }
            if (!CheckMarkers(ctx)) return false;
        }
        return CheckCaret(ctx);
    }
    static bool CheckBlockCommands(StepContext ctx) {
        Bump(checkCounts, "e");
        DocView post = ctx.PostDoc;
        foreach (TableInfo table in ctx.PreTables) {
            int mapped = Mapped(ctx, table.HeaderStart);
            if (mapped < 0 || mapped > post.Text.Length) return Fail(ctx, "e", "a block command removed the table");
            int line = LineOf(post, mapped);
            focusPre = table.Header;
            focusPost = line;
            if (post.Starts[line] != mapped) return Fail(ctx, "e", "a block command merged the table into the previous line: [" + post.Lines[line] + "]");
            int last = line;
            // Every following piped line is a row, outer pipes or not (the same rule FindTables
            // and the renderer's block state use).
            while (last + 1 < post.Count && IsRowLine(post.Lines[last + 1]) && !post.Fence[last + 1]) last++;
            int count = last - line + 1;
            if (count != table.LineTexts.Count) return Fail(ctx, "e", "a block command changed the table's line count (" + table.LineTexts.Count + " -> " + count + ")");
            for (int i = 0; i < count; i++) {
                if (!String.Equals(post.Lines[line + i], table.LineTexts[i], StringComparison.Ordinal)) {
                    return Fail(ctx, "e", "a block command rewrote a table line: was [" + table.LineTexts[i] + "], now [" + post.Lines[line + i] + "]");
                }
            }
        }
        return true;
    }
    // A table that was already malformed before the step (a pasted block, an earlier selection
    // rewrite) cannot be blamed on this step; only tables that were whole at the start are checked.
    static bool TableWasValid(DocView doc, TableInfo table) {
        int headerCells = CellCount(doc.Lines[table.Header]);
        if (CellCount(doc.Lines[table.Delimiter]) != headerCells) return false;
        for (int i = table.Delimiter + 1; i <= table.Last; i++) if (CellCount(doc.Lines[i]) != headerCells) return false;
        return true;
    }
    // The pre-step text of a table line by its relative index (0 = header).
    static string PreLine(TableInfo table,int relative) {
        return table!=null && relative>=0 && relative<table.LineTexts.Count ? table.LineTexts[relative] : null;
    }
    // True when a row that had no literal leading/trailing pipe now begins/ends with one: that
    // edge cell's text became empty, so the model legitimately reads one cell less. The step
    // deleted cell text, not a separator pipe.
    static bool EdgeCellEmptied(string before,string after) {
        if (before==null || after==null) return false;
        string was=before.Trim(), now=after.Trim();
        if (!was.StartsWith("|",StringComparison.Ordinal) && now.StartsWith("|",StringComparison.Ordinal)) return true;
        if (!was.EndsWith("|",StringComparison.Ordinal) && now.EndsWith("|",StringComparison.Ordinal)) return true;
        return false;
    }
    // The delete button removes the table, and removing a column from a one-column table is the
    // same command's own fallback; both leave no table behind on purpose.
    static bool MayDeleteTable(StepContext ctx) {
        return ctx.Label == "table:delete" || ctx.Label == "table:colremove";
    }
    // Product rule: the header and its delimiter row may go together (a selection that starts at
    // or before the header and reaches the delimiter row's text is not guarded; a selection that
    // only partly cuts the row is swallowed). When one change of this step removed or replaced
    // that whole run, the table is gone or rewritten on purpose and its remaining lines are not
    // held to the old shape. The change's offsets index the document as it stood when that change
    // applied, not the pre-step text (earlier changes of the same step move them), so the removed
    // text itself decides: it carries the delimiter row only when the change took that row.
    static bool HeaderDelimiterRemoved(StepContext ctx, TableInfo table) {
        string delimiter = ctx.PreDoc.Lines[table.Delimiter];
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            if (change.Removed <= 0 || String.IsNullOrEmpty(change.RemovedText)) continue;
            if (change.RemovedText.IndexOf(delimiter, StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }
    static bool CheckTable(StepContext ctx, TableInfo table) {
        DocView post = ctx.PostDoc;
        if (!TableWasValid(ctx.PreDoc, table)) return true;
        if (HeaderDelimiterRemoved(ctx, table)) return true;
        focusPre = table.Header;
        Bump(checkCounts, "b");
        int mapped = Mapped(ctx, table.HeaderStart);
        if (mapped < 0 || mapped > post.Text.Length) {
            if (MayDeleteTable(ctx)) return true;
            return Fail(ctx, "b", "the table header start mapped outside the document");
        }
        int line = LineOf(post, mapped);
        focusPost = line;
        if (post.Starts[line] != mapped) return true;   // reported as a merge by the line check below
        bool piped = HasPipe(post.Lines[line]);
        if (!piped || post.Fence[line]) {
            // A step that opened a fence above the table (typing "~~~" on its own line) turns the
            // rows into code by Markdown semantics, not into damaged table lines.
            if (MayDeleteTable(ctx) || post.Fence[line]) return true;
            return Fail(ctx, "b", "the header row is no longer a table row: [" + post.Lines[line] + "]");
        }
        bool delimiterOk = line + 1 < post.Count && IsDelimiterText(post.Lines[line + 1]) && !post.Fence[line + 1];
        if (!delimiterOk) {
            if (MayDeleteTable(ctx)) return true;
            return Fail(ctx, "b", "the header is no longer followed by a delimiter row: next=[" + NextLine(post, line) + "]");
        }
        Bump(checkCounts, "c");
        int headerCells = CellCount(post.Lines[line]);
        // The renderer reads a pipe inside a code span as cell text, so typing one backtick in
        // the header changes its split while every separator stays; that keeps the delimiter
        // row's own columns below. A header whose edge cell text was emptied names one cell less
        // although no pipe moved, but that is the same count mismatch every other step produces:
        // GFM needs equal counts, so the pair is not a table and there is nothing to hold.
        bool headerResplit = CodeSpanResplit(PreLine(table,0), post.Lines[line]);
        int delimiterCells = CellCount(post.Lines[line + 1]);
        // GFM: the header and its delimiter row must name the same columns for the pair to be a
        // table (MarkdownLivePreview.NextIsDelimiter). A step that splits one of them (text typed
        // after a backslash that stopped escaping a separator, a "|" the row did not take as cell
        // text, a deleted edge cell) makes the product read the block as ordinary text; that is
        // the documented rule, not damage, so there is no table shape left to hold the remaining
        // lines to.
        if (delimiterCells != headerCells && !headerResplit) return true;
        // The delimiter row is the table's column count (equal to the header's, above). A body row
        // may name fewer cells (they are padded) or more (the extras are ignored): GFM reads a
        // ragged row as a row, and so does the renderer's block state, so a row's own count is not
        // held to the header's. The parse check below compares each row with its own text.
        int expectedRowCells = delimiterCells;
        int last = line + 1;
        // Every following piped line is this table's row (the renderer's block state).
        while (last + 1 < post.Count && IsRowLine(post.Lines[last + 1]) && !post.Fence[last + 1]) last++;
        Bump(checkCounts, "d");
        int expected = table.Last - table.Header + 1;
        int actual = last - line + 1;
        int allowed = ctx.Label == "table:rowadd" ? 1 : ctx.Label == "table:rowremove" ? -1 : 0;
        // Tab in the last cell of the table's last row appends one empty row
        // (MarkdownStructuralInput.TableCellMove -> MarkdownTableCommands.AddRowAtEnd). That one
        // extra line must be the block's last line, sit directly after the row that used to be the
        // table's last row, and carry the header's cell count; any other extra line still fails.
        bool tabAppend = false;
        if (ctx.Label == "tab" && actual - expected == 1) {
            int oldLastRow = Mapped(ctx, ctx.PreDoc.Starts[table.Last]);
            tabAppend = oldLastRow >= 0 && post.Starts[last - 1] == oldLastRow &&
                LineOf(post, oldLastRow) == last - 1 && CellCount(post.Lines[last]) == headerCells;
        }
        if (actual != expected && !(allowed != 0 && actual - expected == allowed) && !tabAppend) {
            return Fail(ctx, "d", "the table block has " + actual + " lines, expected " + expected + " after " + ctx.Label);
        }
        for (int i = table.Header + 1; i <= table.Last; i++) {
            int start = ctx.PreDoc.Starts[i];
            int end = LineEnd(ctx.PreDoc, i);
            int mappedStart = Mapped(ctx, start);
            int mappedEnd = Mapped(ctx, end);
            if (mappedStart == mappedEnd) continue;
            if (mappedStart > post.Text.Length) continue;
            int rowLine = LineOf(post, mappedStart);
            if (post.Starts[rowLine] != mappedStart) {
                focusPre = i;
                focusPost = rowLine;
                return Fail(ctx, "d", "source line " + (i + 1) + " was merged into the previous line: [" + post.Lines[rowLine] +
                    "] mapped=" + mappedStart + ", line=" + post.Starts[rowLine] + ".." + post.Ends[rowLine] + ", old=[" + ctx.PreDoc.Lines[i] + "]");
            }
        }
        return CheckParseAgreement(ctx, line, last, headerCells, expectedRowCells, table);
    }
    // The preview's own parse must agree with the text model: a delimiter row it does not accept
    // (or a row whose cells it splits differently) is the "1-2 dash alignment row is not a table"
    // class of bug. rowCells is the table's column count from its delimiter row (CheckTable); it is
    // reported with each failure, while a body row is compared with its own text-model split
    // because GFM reads a ragged row as a row.
    static bool CheckParseAgreement(StepContext ctx, int headerLine, int lastLine, int headerCells, int rowCells, TableInfo table) {
        Bump(checkCounts, "b:parse");
        MarkdownLivePreview.Session session = Session(ctx.Window);
        SourceEditor source = Source(ctx.Window);
        if (session == null || source == null || source.Document == null) return true;
        var document = source.Document;
        if (document.LineCount < lastLine + 1) return true;
        // The preview's cell split is a pure function of the line (MarkdownLivePreview.SplitCells:
        // an escaped pipe and a pipe inside a closed `code` span are cell text), so it must name
        // the model's cells whatever the block state says. The comparison sits before the
        // equal-count skip below, so that skip cannot hide the two splitters drifting apart: at
        // b56c6 seed 7 step 164 a deleted word turned the header's backtick run into a span that
        // closed over two separators, and only the model's own count said the pair was no table.
        int previewHeaderCells = MarkdownLivePreview.SplitCells(ctx.PostDoc.Lines[headerLine]).Count;
        if (previewHeaderCells != headerCells) {
            return Fail(ctx, "b:parse", "the preview splits the header line into " + previewHeaderCells +
                " cells, text model " + headerCells + ": [" + ctx.PostDoc.Lines[headerLine] + "]");
        }
        // GFM: a header and its delimiter row must name the same number of cells, or the pair is
        // not a table at all (MarkdownLivePreview.NextIsDelimiter). The text model already reads
        // that pair as plain text, so there is no table header for the preview to agree with; a
        // step that broke the count (the text after a backslash stopped escaping a separator, a
        // "|" the row no longer takes as cell text, a header whose edge cell was emptied, a
        // backtick that now closes a span over a separator) follows the product's documented rule
        // instead of disagreeing with it.
        if (headerCells != rowCells) return true;
        MarkdownLivePreview.LineInfo header = session.Info(document.GetLineByNumber(headerLine + 1));
        if (header == null || !header.Table || !header.Header) {
            return Fail(ctx, "b:parse", "the preview does not read the header line as a table header (model " +
                headerCells + " cells, delimiter " + rowCells + "): [" + ctx.PostDoc.Lines[headerLine] + "]");
        }
        if (header.Cells == null || header.Cells.Count != headerCells) {
            return Fail(ctx, "b:parse", "the preview splits the header into " + (header.Cells == null ? -1 : header.Cells.Count) + " cells, text model " + headerCells);
        }
        MarkdownLivePreview.LineInfo delimiter = session.Info(document.GetLineByNumber(headerLine + 2));
        if (delimiter == null || !delimiter.Delimiter) {
            return Fail(ctx, "b:parse", "the preview does not read the delimiter row as a delimiter: [" + ctx.PostDoc.Lines[headerLine + 1] + "]");
        }
        for (int i = headerLine + 2; i <= lastLine; i++) {
            // A delimiter-looking line inside the block is an ordinary body row: the product's
            // Delimiter flag belongs to the header's next line only (MarkdownLivePreview.ParseLine),
            // so it names no delimiter cells to compare. A header whose count no longer matches its
            // delimiter row never reaches this loop: that pair is not a table (NextIsDelimiter).
            if (IsDelimiterText(ctx.PostDoc.Lines[i])) continue;
            // GFM ragged rows: the header fixes the table's columns, and a body row may name fewer
            // cells (padded) or more (ignored). The preview must still split the line the way the
            // text model does; comparing it with the header's column count would fail a row the
            // product reads as a row on purpose.
            int modelCells = CellCount(ctx.PostDoc.Lines[i]);
            MarkdownLivePreview.LineInfo info = session.Info(document.GetLineByNumber(i + 1));
            if (info == null || !info.Table || info.Delimiter || info.Cells == null || info.Cells.Count != modelCells) {
                if (info != null && info.Cells != null && EdgeCellEmptied(PreLine(table,i-headerLine), ctx.PostDoc.Lines[i])) continue;
                if (info != null && CodeSpanResplit(PreLine(table,i-headerLine), ctx.PostDoc.Lines[i])) continue;
                focusPre = table.Header + (i - headerLine);
                focusPost = i;
                return Fail(ctx, "b:parse", "the preview disagrees about row " + (i + 1) + " (preview " +
                    (info == null || info.Cells == null ? -1 : info.Cells.Count) + ", model " + modelCells +
                    ", columns " + rowCells + "): [" + ctx.PostDoc.Lines[i] + "]");
            }
        }
        return true;
    }
    static bool CheckCaret(StepContext ctx) {
        Bump(checkCounts, "h");
        SourceEditor source = Source(ctx.Window);
        if (source == null || source.Document == null) return Fail(ctx, "h", "the source editor is gone");
        int caret = source.CaretOffset;
        if (caret < 0 || caret > ctx.PostText.Length) {
            return Fail(ctx, "h", "caret " + caret + " is outside 0.." + ctx.PostText.Length);
        }
        DocView doc = ctx.PostDoc;
        int line = LineOf(doc, caret);
        if (!doc.Fence[line]) {
            bool inRow = false;
            foreach (TableInfo table in FindTables(doc)) {
                if (line == table.Header || (line > table.Delimiter && line <= table.Last)) { inRow = true; break; }
            }
            if (inRow) {
                // The caret may sit anywhere inside the row's source span between its pipes, padding
                // included: typing there stays in a cell. Before the leading pipe or after the
                // trailing pipe the next character starts a new cell, which is the row-end bug.
                string lineText = doc.Lines[line];
                int relative = caret - doc.Starts[line];
                var pipes = new List<int>();
                ScanPipes(lineText, pipes);
                if (pipes.Count > 0) {
                    // A burst of deletes can leave a row with no cell text at all ("|   "): every
                    // segment is empty, so the row names no cell and there is no cell span a caret
                    // could be outside of. The preview reads the same split (an empty cell list
                    // hides the whole line), and typing at the pipe's padding is the first cell's
                    // own text spot, not a new column.
                    int[] cellStarts, cellEnds;
                    CellsOf(lineText, out cellStarts, out cellEnds);
                    if (cellStarts.Length == 0) return true;
                    int first = pipes[0], lastPipe = pipes[pipes.Count - 1];
                    bool leading = true, trailing = true;
                    for (int p = 0; p < first; p++) if (lineText[p] != ' ' && lineText[p] != '\t') { leading = false; break; }
                    for (int p = lastPipe + 1; p < lineText.Length; p++) if (lineText[p] != ' ' && lineText[p] != '\t') { trailing = false; break; }
                    int indent = 0;
                    while (indent < lineText.Length && (lineText[indent] == ' ' || lineText[indent] == '\t')) indent++;
                    bool safe = true;
                    if (leading) {
                        // A list-indented row's own indent is where Backspace outdents the table;
                        // the app keeps the caret there on purpose, so that spot is not a fault.
                        if (indent > 0 && relative <= indent) safe = true;
                        else if (relative <= first) safe = false;
                    }
                    if (trailing && relative > lastPipe) safe = false;
                    if (!safe) {
                        focusPost = line;
                        return Fail(ctx, "h", "caret " + relative + " in table row [" + lineText + "] is outside the cell span (leading pipe " +
                            first + ", trailing pipe " + lastPipe + ", length " + lineText.Length + "), pre caret " + ctx.PreCaret +
                            " on line " + ctx.PreCaretLine + ", selection " + ctx.SelectionStart + "+" + ctx.SelectionLength);
                    }
                }
                return true;
            }
        }
        return true;
    }
    // ---------------------------------------------------------------- emphasis hygiene
    // Runs of "*" and "~~" outside code spans, paired as opener/closer per run text. A pair whose
    // content is empty/whitespace, or a closer directly preceded by whitespace, is markup the
    // renderer cannot use; only occurrences created by this step fail.
    static void AddBadMarker(List<BadMarker> list, string kind, int lineStart, int offset, int contextLength, string line) {
        var bad = new BadMarker();
        int length = Math.Min(contextLength, Math.Max(0, line.Length - offset));
        bad.Key = kind + "|" + line.Substring(offset, length);
        bad.Offset = lineStart + offset;
        list.Add(bad);
    }
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
                        // The key stops at the run: what follows the closer may change on its own
                        // (text typed after it) without creating a second broken pair.
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
    static bool CheckMarkers(StepContext ctx) {
        Bump(checkCounts, "f");
        Dictionary<string,int> before = BadMarkerCounts(ctx.PreDoc, null);
        var afterList = new List<BadMarker>();
        Dictionary<string,int> after = BadMarkerCounts(ctx.PostDoc, afterList);
        foreach (KeyValuePair<string,int> pair in after) {
            int previous = 0;
            if (!before.TryGetValue(pair.Key, out previous)) previous = 0;
            if (pair.Value <= previous) continue;
            string context = pair.Key.Substring(pair.Key.IndexOf('|') + 1);
            int typed = CountOccurrences(ctx.InsertedText, context);
            if (pair.Value - previous <= typed) continue;
            // Deleting one "*" or typing a space inside "~~...~~" is ordinary text the user may
            // write; only the app's own commands and structural keys are held to this rule.
            if (LiteralEdit(ctx)) continue;
            BadMarker hit = null;
            foreach (BadMarker bad in afterList) if (String.Equals(bad.Key, pair.Key, StringComparison.Ordinal)) { hit = bad; break; }
            int lineNumber = hit == null ? 0 : LineOf(ctx.PostDoc, hit.Offset) + 1;
            string lineText = lineNumber > 0 && lineNumber <= ctx.PostDoc.Count ? ctx.PostDoc.Lines[lineNumber - 1] : FindLine(ctx.PostDoc, context);
            focusPost = lineNumber > 0 ? lineNumber - 1 : focusPost;
            return Fail(ctx, "f", "the step left broken emphasis (" + pair.Key.Replace("|", ": ") + ") at line " + lineNumber + ": [" + lineText + "]");
        }
        return true;
    }
    // Actions that only insert or remove the user's own characters. Commands and structural keys
    // (cmd:, table:, enter, tab) write markup themselves and stay under the f check.
    static bool LiteralEdit(StepContext ctx) {
        return ctx.Action == "type" || ctx.Action == "backspace" || ctx.Action == "delete" ||
            ctx.Action == "cut" || ctx.Action == "paste";
    }
    static int CountOccurrences(string text, string piece) {
        if (String.IsNullOrEmpty(text) || String.IsNullOrEmpty(piece)) return 0;
        int count = 0;
        for (int at = text.IndexOf(piece, StringComparison.Ordinal); at >= 0; at = text.IndexOf(piece, at + piece.Length, StringComparison.Ordinal)) count++;
        return count;
    }
    static string FindLine(DocView doc, string piece) {
        for (int i = 0; i < doc.Count; i++) if (doc.Lines[i].IndexOf(piece, StringComparison.Ordinal) >= 0) return doc.Lines[i];
        return piece;
    }
    // ---------------------------------------------------------------- fixtures
    static string Fixture(int seed) {
        string nl = seed % 2 == 0 ? "\r\n" : "\n";
        var text = new StringBuilder();
        switch ((seed - 1) % 4) {
            case 0:
                Line(text, nl, "# 표 시험 문서 " + seed.ToString(CultureInfo.InvariantCulture));
                Line(text, nl, "");
                Line(text, nl, "문단 **굵게** *기울임* ~~취소선~~ `인라인 코드` 그리고 보통 글자.");
                Line(text, nl, "");
                Line(text, nl, "| 이름 | 상태 | 설명 |");
                Line(text, nl, "| --- | :--: | --: |");
                Line(text, nl, "| 김철수 | 진행 중 | 아주 긴 한국어 셀 내용이 들어 있어 줄이 바뀌는 설명입니다 |");
                Line(text, nl, "| 日本語 | 완료 | **굵은 셀** 과 `코드` |");
                Line(text, nl, "| 😀 이모지 | 대기 | 값 |");
                Line(text, nl, "");
                Line(text, nl, "중간 문단 **강조** 와 `a|b` 코드.");
                Line(text, nl, "");
                Line(text, nl, "| 왼쪽 | 오른쪽 |");
                Line(text, nl, "| :-- | --: |");
                Line(text, nl, "| 하나 | 1 |");
                Line(text, nl, "| 둘 | 2 |");
                Line(text, nl, "");
                Line(text, nl, "- 목록 항목:");
                Line(text, nl, "  | 항목 | 값 |");
                Line(text, nl, "  | --- | :-: |");
                Line(text, nl, "  | 가 | 10 |");
                Line(text, nl, "  | 나 | 20 |");
                Line(text, nl, "");
                Line(text, nl, "끝 문단입니다.");
                break;
            case 1:
                Line(text, nl, "# 문서 " + seed.ToString(CultureInfo.InvariantCulture));
                Line(text, nl, "");
                Line(text, nl, "**하나** *둘* `셋`");
                Line(text, nl, "");
                Line(text, nl, "| 단일 열 |");
                Line(text, nl, "|-|");
                Line(text, nl, "| 값 하나 |");
                Line(text, nl, "| 값 둘 |");
                Line(text, nl, "");
                Line(text, nl, "설명 문단 **굵게**.");
                Line(text, nl, "");
                Line(text, nl, "| 제목 | 설명 | 비고 |");
                Line(text, nl, "| :-- | :-: | --: |");
                Line(text, nl, "| 가나다라마바사 | 아주 긴 셀 값이 여러 단어로 이어지는 줄바꿈 셀입니다 | 😀 |");
                Line(text, nl, "| 日本語 | `코드|막대` 셀 | **굵게** |");
                Line(text, nl, "");
                Line(text, nl, "마지막 문단.");
                break;
            case 2:
                Line(text, nl, "# 문서 " + seed.ToString(CultureInfo.InvariantCulture));
                Line(text, nl, "");
                Line(text, nl, "| 항목 | 값 | 메모 |");
                Line(text, nl, "| --- | --- | --- |");
                Line(text, nl, "| 첫째 | 1 | **굵은 메모** |");
                Line(text, nl, "| 둘째 | 2 | `코드` 값 |");
                Line(text, nl, "| 셋째 | 3 | 日本語 |");
                Line(text, nl, "");
                Line(text, nl, "문단 *기울임* ~~취소선~~ `인라인` 끝.");
                Line(text, nl, "");
                Line(text, nl, "| 이름 | 나이 |");
                Line(text, nl, "| :-- | --: |");
                Line(text, nl, "| 철수 | 30 |");
                Line(text, nl, "| 영희 | 25 |");
                Line(text, nl, "");
                Line(text, nl, "- 목록 안 표:");
                Line(text, nl, "  | 열 하나 | 열 둘 |");
                Line(text, nl, "  | --- | --- |");
                Line(text, nl, "  | 가 | 나 |");
                Line(text, nl, "  | 다 | 라 |");
                Line(text, nl, "");
                Line(text, nl, "끝.");
                break;
            default:
                Line(text, nl, "# 문서 " + seed.ToString(CultureInfo.InvariantCulture));
                Line(text, nl, "");
                Line(text, nl, "문단 **굵게** 와 *기울임*.");
                Line(text, nl, "");
                Line(text, nl, "| 가 | 나 | 다 | 라 |");
                Line(text, nl, "| --- | :-: | --: | :-- |");
                Line(text, nl, "| 1 | 2 | 3 | 4 |");
                Line(text, nl, "| 하나 | 둘 | 셋 | 넷 |");
                Line(text, nl, "| 😀 | 日本 | 값 | 끝 |");
                Line(text, nl, "");
                Line(text, nl, "중간 문단 `코드` 와 **강조**.");
                Line(text, nl, "");
                Line(text, nl, "| 항목 | 값 |");
                Line(text, nl, "| --- | --- |");
                Line(text, nl, "| 甲 | 100 |");
                Line(text, nl, "| 乙 | 200 |");
                Line(text, nl, "");
                Line(text, nl, "- 목록 항목 하나");
                Line(text, nl, "- 목록 항목 둘");
                Line(text, nl, "");
                Line(text, nl, "마지막 문단.");
                break;
        }
        return text.ToString();
    }
    static void Line(StringBuilder text, string newline, string value) {
        text.Append(value).Append(newline);
    }
    // The fixture must already satisfy the model and the preview parse before a single step runs;
    // a disagreement here means the generated document (or the model) is wrong, not the product.
    static bool CheckFixture(MainWindow window) {
        string text = DocText(window);
        DocView doc = Build(text);
        List<TableInfo> tables = FindTables(doc);
        if (tables.Count == 0) {
            FailOpen("parse", "the fixture has no tables");
            return false;
        }
        MarkdownLivePreview.Session session = Session(window);
        SourceEditor source = Source(window);
        if (session == null || source == null) { FailOpen("parse", "no preview session"); return false; }
        foreach (TableInfo table in tables) {
            for (int i = table.Header; i <= table.Last; i++) {
                string lineText = doc.Lines[i];
                int mine = CellCount(lineText);
                int theirs = MarkdownLivePreview.SplitCells(lineText).Count;
                if (i != table.Delimiter && mine != theirs) {
                    FailOpen("parse", "cell split disagreement on line " + (i + 1) + ": model " + mine + ", preview " + theirs + ": [" + lineText + "]");
                    return false;
                }
                MarkdownLivePreview.LineInfo info = session.Info(source.Document.GetLineByNumber(i + 1));
                if (info == null || !info.Table) {
                    FailOpen("parse", "the preview does not see line " + (i + 1) + " as a table line: [" + lineText + "]");
                    return false;
                }
                if (i == table.Header && !info.Header) {
                    FailOpen("parse", "the preview does not see line " + (i + 1) + " as a table header (delimiter row rejected?): [" + lineText + "] next=[" + doc.Lines[i + 1] + "]");
                    return false;
                }
                if (i == table.Delimiter && !info.Delimiter) {
                    FailOpen("parse", "the preview does not see line " + (i + 1) + " as a delimiter row: [" + lineText + "]");
                    return false;
                }
            }
        }
        return true;
    }
}
}
