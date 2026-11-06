using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// --mode-switch-abuse-probe <dir>: seeded sequences that mix edits (typing incl. Korean, Enter,
// Backspace, toolbar and table commands, paste) with view switches: basic <-> live, the raw
// (원문 편집) toggle, MD <-> TXT conversion, read mode (수정 모드 끔) on/off, zoom, random scrolls
// and caret placements inside tables, lists, bold runs, code blocks, line ends and hidden markup.
//
// After every switch the probe checks, independently of the switch code:
//  - no exception (step or dispatcher);
//  - the document text is unchanged by basic/live, raw, read mode and zoom;
//  - a TXT conversion equals MarkdownPlainText.Convert / LiteralMarkdown, and TXT -> MD -> TXT of
//    an unedited conversion loses no text;
//  - the caret keeps its source offset for view switches and lands on the documented nearest
//    plain/literal position for TXT; it always stays inside the document;
//  - the caret line that was on screen before the switch is on screen after it;
//  - the selection is preserved or cleared, never left as a different range;
//  - the undo history is untouched by view switches and gains exactly one mode-aware entry for a
//    TXT conversion (UndoMode == the mode before the switch);
//  - the live preview attachment and its Options.HideMarkers/ReadOnly follow the post state, the
//    format and table toolbars are not stale, and DrawnTableCaret is empty whenever the caret is
//    not on a table row.
// Segments of edits are undone to their exact starting text and redone to the pre-undo text, so a
// switch that corrupts the history is reported as well. Fixtures are generated here from the seed;
// no user file, setting, recovery draft or clipboard is read or written.
// Report: <dir>\mode-switch-abuse-probe.txt; mode-switch-abuse-probe-PASS.txt only when every
// sequence kept every invariant and the run finished inside its budget.
internal static class ModeSwitchAbuseProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int FirstSeedDefault = 1, LastSeedDefault = 6, ActionsPerSeedDefault = 180, SegmentLength = 40, UndoCap = 530;
    const long DefaultBudgetMilliseconds = 240000;
    const int MaxFailures = 120, StepListLimit = 36;

    internal sealed class FailureInfo {
        internal int Seed, Step, Occurrences = 1;
        internal string Mode, Fixture, Action, Invariant, Expected, Actual, Detail, Steps, Before, After;
    }
    internal sealed class StepContext {
        internal MainWindow Window;
        internal string Kind, Target, Label, Payload;
        internal int Command;
        internal string PreText, PostText;
        internal int PreCaret, PostCaret;
        internal int PreSelStart, PreSelLength, PostSelStart, PostSelLength;
        internal bool PrePlain, PostPlain, PreEditing, PostEditing, PreRaw, PostRaw, PreHide, PostHide;
        internal double PreFont, PostFont;
        internal int PreUndo, PostUndo, PreRedo, PostRedo;
        internal bool? PreUndoMode, PostUndoMode, PreRedoMode, PostRedoMode;
        internal bool PreVisible, CheckVisible;
        internal string PreVisibleDetail, PostVisibleDetail;
        internal string ToolbarBefore, ToolbarAfter;
        internal string PreToolbar, PostToolbar, RefreshedToolbar;
    }

    static string outRoot;
    static int currentSeed, currentStep;
    static string currentMode, currentFixture, currentAction = "open";
    static bool dispatcherFailed;
    static readonly List<string> dispatcherNotes = new List<string>();
    static readonly List<string> stepNames = new List<string>();
    static readonly Dictionary<string,int> opCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> checkCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> failCounts = new Dictionary<string,int>();
    static readonly List<FailureInfo> failures = new List<FailureInfo>();
    static readonly Dictionary<string,FailureInfo> failureKeys = new Dictionary<string,FailureInfo>();
    static int omittedFailures;
    static int failureEvents;
    static bool all;
    static bool budgetReached;
    static Stopwatch watch;
    static long budgetMilliseconds = DefaultBudgetMilliseconds;
    static int firstSeed = FirstSeedDefault, lastSeed = LastSeedDefault, actionsPerSeed = ActionsPerSeedDefault;
    static MainWindow currentWindow;
    static MethodInfo literalMethod;

    internal static int Run(string folder) {
        string dir = Path.GetFullPath(folder);
        Directory.CreateDirectory(dir);
        outRoot = dir;
        string passFile = Path.Combine(dir, "mode-switch-abuse-probe-PASS.txt");
        string failFile = Path.Combine(dir, "mode-switch-abuse-probe-FAIL.txt");
        RemoveFile(passFile);
        RemoveFile(failFile);
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
            File.WriteAllText(Path.Combine(dir, "mode-switch-abuse-probe.txt"), text, new UTF8Encoding(false));
            if (all && !budgetReached) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
            else File.WriteAllText(failFile, text, new UTF8Encoding(false));
        } catch (IOException) {
            return 1;
        }
        return all && !budgetReached ? 0 : 1;
    }

    // A reduced run replays one seed; the default (no variables) is the full suite.
    //   MDNOTE_MODE_SWITCH_ABUSE_SEED=2 MDNOTE_MODE_SWITCH_ABUSE_STEPS=60
    //   MDNOTE_MODE_SWITCH_ABUSE_BUDGET=60000
    static void ReadOptions() {
        firstSeed = FirstSeedDefault; lastSeed = LastSeedDefault; actionsPerSeed = ActionsPerSeedDefault;
        budgetMilliseconds = DefaultBudgetMilliseconds;
        string value = Environment.GetEnvironmentVariable("MDNOTE_MODE_SWITCH_ABUSE_SEED");
        int number;
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) {
            firstSeed = number; lastSeed = number;
        }
        value = Environment.GetEnvironmentVariable("MDNOTE_MODE_SWITCH_ABUSE_STEPS");
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) actionsPerSeed = number;
        value = Environment.GetEnvironmentVariable("MDNOTE_MODE_SWITCH_ABUSE_BUDGET");
        if (!String.IsNullOrEmpty(value) && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out budgetMilliseconds) && budgetMilliseconds <= 0) budgetMilliseconds = DefaultBudgetMilliseconds;
    }

    static void RemoveFile(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    static bool RunSequence(int seed, bool liveMode, StringBuilder reportBuilder) {
        currentSeed = seed;
        currentMode = liveMode ? "live" : "basic";
        currentFixture = "f" + (((seed - 1) % 2) + 1).ToString(CultureInfo.InvariantCulture) + "-" + currentMode;
        currentStep = 0;
        currentAction = "open";
        stepNames.Clear();
        dispatcherFailed = false;
        string fixture = Fixture(seed);
        MainWindow window = null;
        string surface = "(not opened)";
        int actions = 0;
        int checksBefore = TotalChecks(), failuresBefore = failureEvents;
        var timer = Stopwatch.StartNew();
        bool runOk = true;
        try {
            window = CreateWindow();
            currentWindow = window;
            window.SetText(fixture);
            window.SetMode(true);
            Pump();
            Invoke(window, "SetLiveMode", liveMode);
            Pump();
            window.UpdateLayout();
            Invoke(window, "FlushDocumentChanges");
            bool editing = Field<bool>(window, "editing");
            bool attached = Prop<bool>(window, "LivePreviewAttached");
            bool hide = Static<bool>("hideMarkers");
            bool plain = Prop<bool>(window, "PlainText");
            SourceEditor source = Source(window);
            surface = "editing=" + editing + ", attached=" + attached + ", hideMarkers=" + hide + ", plain=" + plain +
                ", sourceVisible=" + (source != null && source.Visibility == Visibility.Visible);
            if (!editing || !attached || hide != !liveMode || plain) {
                FailOpen("open-surface", "the live edit surface did not attach in the " + currentMode + " view: " + surface);
                runOk = false;
            } else if (!CheckFixtureTables(window)) {
                runOk = false;
            } else {
                if (!CheckTextRoundTrip(window, fixture, liveMode)) runOk = false;
                ResetBase(window, fixture, liveMode);
                CheckDirectedSwitches(window, fixture, liveMode);
                ResetBase(window, fixture, liveMode);
                if (!RunActions(window, seed, liveMode, ref actions)) runOk = false;
            }
            Invoke(window, "FlushDocumentChanges");
        } catch (Exception ex) {
            FailOpen("open-exception", "seed exception: " + OneLine(ex.ToString()));
            runOk = false;
        } finally {
            currentWindow = null;
            CloseWindow(window);
            timer.Stop();
        }
        int failedHere = failureEvents - failuresBefore;
        int checksHere = TotalChecks() - checksBefore;
        reportBuilder.Append("\n## seed ").Append(seed).Append(' ').Append(currentMode).Append('\n');
        reportBuilder.Append("- fixture: ").Append(currentFixture).Append('\n');
        reportBuilder.Append("- surface at open: ").Append(surface).Append('\n');
        reportBuilder.Append("- actions: ").Append(actions).Append(" of ").Append(actionsPerSeed).Append('\n');
        reportBuilder.Append("- checks: ").Append(checksHere).Append(", failures: ").Append(failedHere).Append('\n');
        reportBuilder.Append("- run time: ").Append(timer.ElapsedMilliseconds).Append(" ms\n");
        return runOk && failedHere == 0 && !dispatcherFailed;
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
                if (!Step(window, random)) return false;
            }
            if (!CheckUndoOracle(window, segmentStart)) return false;
            if (watch.ElapsedMilliseconds > budgetMilliseconds) {
                budgetReached = true;
                all = false;
                return false;
            }
        }
        return true;
    }

    static void NoteDispatcher(DispatcherUnhandledExceptionEventArgs args) {
        dispatcherFailed = true;
        args.Handled = true;
        if (dispatcherNotes.Count >= 20) return;
        string note = args.Exception == null ? "unknown" : args.Exception.GetType().Name + ": " + OneLine(args.Exception.Message);
        if (args.Exception != null && args.Exception.StackTrace != null) {
            string[] frames = args.Exception.StackTrace.Replace("\r","").Split('\n');
            note += " @" + OneLine(String.Join(" < ", frames, 0, Math.Min(4, frames.Length)));
        }
        dispatcherNotes.Add("seed " + currentSeed + " " + currentMode + " step " + currentStep + " " + currentAction + ": " + note);
    }
    static string LastDispatcherNote() {
        return dispatcherNotes.Count == 0 ? "unhandled dispatcher exception" : dispatcherNotes[dispatcherNotes.Count - 1];
    }

    // ---------------------------------------------------------------- failure bookkeeping
    static int TotalChecks() {
        int total = 0;
        foreach (KeyValuePair<string,int> pair in checkCounts) total += pair.Value;
        return total;
    }
    static void Bump(Dictionary<string,int> map, string key) {
        int value;
        if (!map.TryGetValue(key, out value)) value = 0;
        map[key] = value + 1;
    }
    static bool Fail(StepContext ctx, string invariant, string expected, string actual, string detail) {
        Bump(failCounts, invariant);
        failureEvents++;
        all = false;
        // Group by root shape, not by the offset/line the first occurrence happened to use: the
        // report lists one entry per distinct failure with the first seed/step as its witness.
        string key = invariant + "|" + (ctx == null ? currentAction : ctx.Label) + "|" + Shape(expected) + "|" + Shape(actual);
        FailureInfo existing;
        if (failureKeys.TryGetValue(key, out existing)) {
            existing.Occurrences++;
            return false;
        }
        if (failures.Count >= MaxFailures) { omittedFailures++; return false; }
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture;
        failure.Step = currentStep;
        failure.Action = ctx == null ? currentAction : ctx.Label;
        failure.Invariant = invariant;
        failure.Expected = OneLine(expected);
        failure.Actual = OneLine(actual);
        failure.Detail = OneLine(detail);
        failure.Steps = StepListText();
        failure.Before = ctx == null ? "(none)" : Excerpt(ctx.PreText, ctx.PreCaret);
        failure.After = ctx == null ? "(none)" : Excerpt(ctx.PostText, ctx.PostCaret);
        failures.Add(failure);
        failureKeys[key] = failure;
        return false;
    }
    static void FailOpen(string invariant, string detail) {
        Bump(failCounts, invariant);
        failureEvents++;
        all = false;
        if (failures.Count >= MaxFailures) { omittedFailures++; return; }
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture;
        failure.Step = 0;
        failure.Action = "open";
        failure.Invariant = invariant;
        failure.Expected = "a working " + currentMode + " edit surface";
        failure.Actual = OneLine(detail);
        failure.Detail = OneLine(detail);
        failure.Steps = StepListText();
        failure.Before = "(none)";
        failure.After = "(none)";
        failures.Add(failure);
        string key = invariant + "|open|" + Short(failure.Actual, 90);
        failureKeys[key] = failure;
    }
    static int CountFailures(int seed, string mode) {
        int count = 0;
        for (int i = 0; i < failures.Count; i++) if (failures[i].Seed == seed && String.Equals(failures[i].Mode, mode, StringComparison.Ordinal)) count++;
        return count;
    }

    // ---------------------------------------------------------------- one step
    static bool Step(MainWindow window, Random random) {
        string kind, payload;
        int command;
        ChooseAction(random, out kind, out command, out payload);
        currentStep++;
        var ctx = new StepContext();
        ctx.Window = window;
        ctx.Kind = kind;
        ctx.Command = command;
        ctx.Payload = payload;
        SourceEditor source = Source(window);
        if (source == null) {
            currentAction = kind;
            return Fail(ctx, "surface:source", "the source editor exists", "source is null", "");
        }
        SnapshotPre(window, ctx);
        ctx.Label = LabelFor(window, ctx);
        currentAction = ctx.Label;
        stepNames.Add(currentStep.ToString(CultureInfo.InvariantCulture) + " " + ctx.Label);
        if (stepNames.Count > 200) stepNames.RemoveAt(0);
        Bump(opCounts, ctx.Label);
        bool isSwitch = IsSwitchKind(kind);
        if (isSwitch) EnsureCaretVisibleBaseline(window, ctx);
        Exception error = null;
        try {
            Execute(window, random, ctx);
            Pump();
            Invoke(window, "FlushDocumentChanges");
            Pump();
            window.UpdateLayout();
            Pump();
        } catch (Exception ex) {
            error = ex;
        }
        if (error != null) {
            Fail(ctx, "exception", "no exception", error.GetType().Name + ": " + OneLine(error.Message), FirstFrames(error));
            return false;
        }
        if (dispatcherFailed) {
            Fail(ctx, "dispatcher", "no dispatcher exception", LastDispatcherNote(), "");
            return false;
        }
        SnapshotPost(window, ctx);
        // An invariant failure is recorded and the next step starts from whatever state the
        // switch left; only an exception, a dispatcher fault or the undo oracle ends a sequence.
        CheckStep(ctx);
        return true;
    }

    static bool IsSwitchKind(string kind) {
        return kind == "view" || kind == "raw" || kind == "txt" || kind == "read" || kind == "zoom";
    }

    static void ChooseAction(Random random, out string kind, out int command, out string payload) {
        kind = "type";
        command = 0;
        payload = null;
        int roll = random.Next(112);
        if (roll < 20) { kind = "type"; payload = TypingText(random); }
        else if (roll < 26) kind = "enter";
        else if (roll < 33) kind = "backspace";
        else if (roll < 45) { kind = "cmd"; command = random.Next(CommandNames.Length); }
        else if (roll < 54) { kind = "table"; command = random.Next(TableNames.Length); }
        else if (roll < 59) { kind = "paste"; command = random.Next(PasteSnippets.Length); payload = PasteSnippets[command]; }
        else if (roll < 62) kind = "select";
        else if (roll < 70) kind = "place";
        else if (roll < 73) kind = "scroll";
        else if (roll < 75) kind = "undo";
        else if (roll < 76) kind = "redo";
        else if (roll < 86) kind = "view";
        else if (roll < 93) kind = "raw";
        else if (roll < 100) kind = "txt";
        else if (roll < 107) kind = "read";
        else { kind = "zoom"; command = random.Next(2); }
    }

    static string LabelFor(MainWindow window, StepContext ctx) {
        string kind = ctx.Kind;
        if (kind == "type") return "type:" + (ctx.Payload == " " ? "space" : ctx.Payload);
        if (kind == "backspace") return "backspace";
        if (kind == "cmd") return "cmd:" + CommandNames[ctx.Command];
        if (kind == "table") return "table:" + TableNames[ctx.Command];
        if (kind == "paste") return "paste:" + ctx.Command.ToString(CultureInfo.InvariantCulture);
        if (kind == "place") return "place";
        if (kind == "scroll") return "scroll";
        if (kind == "view") {
            ctx.Target = ctx.PreHide ? "live" : "basic";
            return "view:" + ctx.Target;
        }
        if (kind == "raw") {
            ctx.Target = ctx.PreRaw ? "off" : "on";
            return "raw:" + ctx.Target;
        }
        if (kind == "txt") return ctx.PrePlain ? "txt:plain-to-md" : "txt:md-to-plain";
        if (kind == "read") {
            ctx.Target = ctx.PreEditing ? "on" : "off";
            return "read:" + ctx.Target;
        }
        if (kind == "zoom") return ctx.Command == 0 ? "zoom:in" : "zoom:out";
        return kind;
    }

    // ---------------------------------------------------------------- snapshots
    static void SnapshotPre(MainWindow window, StepContext ctx) {
        SourceEditor source = Source(window);
        ctx.PreText = DocText(window);
        ctx.PreCaret = source.CaretOffset;
        ctx.PreSelStart = source.SelectionStart;
        ctx.PreSelLength = source.SelectionLength;
        ctx.PrePlain = Prop<bool>(window, "PlainText");
        ctx.PreEditing = Field<bool>(window, "editing");
        ctx.PreHide = Static<bool>("hideMarkers");
        ctx.PreRaw = RawChecked(window);
        ctx.PreFont = Field<double>(window, "fontSize");
        ctx.PreUndo = HistoryCount(window, false);
        ctx.PreRedo = HistoryCount(window, true);
        DocumentHistory history = HistoryOf(window);
        ctx.PreUndoMode = history.UndoMode;
        ctx.PreRedoMode = history.RedoMode;
        ctx.PreToolbar = ToolbarTags(window);
    }

    static void SnapshotPost(MainWindow window, StepContext ctx) {
        SourceEditor source = Source(window);
        ctx.PostText = DocText(window);
        ctx.PostCaret = source == null ? -1 : source.CaretOffset;
        ctx.PostSelStart = source == null ? -1 : source.SelectionStart;
        ctx.PostSelLength = source == null ? -1 : source.SelectionLength;
        ctx.PostPlain = Prop<bool>(window, "PlainText");
        ctx.PostEditing = Field<bool>(window, "editing");
        ctx.PostHide = Static<bool>("hideMarkers");
        ctx.PostRaw = RawChecked(window);
        ctx.PostFont = Field<double>(window, "fontSize");
        ctx.PostUndo = HistoryCount(window, false);
        ctx.PostRedo = HistoryCount(window, true);
        DocumentHistory history = HistoryOf(window);
        ctx.PostUndoMode = history.UndoMode;
        ctx.PostRedoMode = history.RedoMode;
        if (IsSwitchKind(ctx.Kind)) {
            ctx.PostToolbar = ToolbarTags(window);
            // What the app itself computes for this caret is the definition of a fresh toolbar.
            // Updating it here (after the switch is over) must not change the tags the switch left.
            try { Invoke(window, "UpdateTools"); } catch (Exception) { }
            Pump();
            window.UpdateLayout();
            Pump();
            ctx.RefreshedToolbar = ToolbarTags(window);
        }
    }

    // ---------------------------------------------------------------- execution
    static void Execute(MainWindow window, Random random, StepContext ctx) {
        string kind = ctx.Kind;
        if (kind == "type") { Source(window).TextArea.PerformTextInput(ctx.Payload); return; }
        if (kind == "enter") { EnterStep(window); return; }
        if (kind == "backspace") { int count = 1 + random.Next(20); for (int i = 0; i < count; i++) BackspaceOnce(window); return; }
        if (kind == "cmd") { ExecuteCommand(window, ctx.Command); return; }
        if (kind == "table") { ExecuteTable(window, random, ctx.Command); return; }
        if (kind == "paste") { Invoke(window, "LivePaste", new System.Windows.DataObject(), true, ctx.Payload); return; }
        if (kind == "select") { SelectStep(window, random); return; }
        if (kind == "place") { PlaceCaret(window, random, ctx); return; }
        if (kind == "scroll") { ScrollStep(window, random); return; }
        if (kind == "undo") { Invoke(window, "Undo"); return; }
        if (kind == "redo") { Invoke(window, "Redo"); return; }
        if (kind == "view") { Invoke(window, "SetLiveMode", ctx.Target == "live"); return; }
        if (kind == "raw") { SetRaw(window, ctx.Target == "on"); return; }
        if (kind == "read") { Invoke(window, "SetMode", ctx.Target != "on"); return; }
        if (kind == "txt") { Invoke(window, "ToggleTextMode"); return; }
        if (kind == "zoom") { Invoke(window, "Zoom", ctx.Command == 0 ? 2.0 : -2.0); return; }
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

    static void ExecuteTable(MainWindow window, Random random, int index) {
        if (index == 11) { Invoke(window, "InsertTable", 2 + random.Next(3), 1 + random.Next(3)); return; }
        Invoke(window, "EditTable", index);
    }

    // The real 원문 편집 path: the check box's Checked/Unchecked handler calls SwitchSurface
    // itself (Editor.cs:176), so setting IsChecked drives exactly what a click drives.
    static void SetRaw(MainWindow window, bool on) {
        CheckBox preview = Field<CheckBox>(window, "preview");
        preview.IsChecked = on;
    }

    static bool RawChecked(MainWindow window) {
        CheckBox preview = Field<CheckBox>(window, "preview");
        return preview.IsChecked == true;
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
        if (source != null) EditingCommands.Backspace.Execute(null, source.TextArea);
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

    // ---------------------------------------------------------------- caret, selection, scroll
    // The same rule as FormatAbuseProbe: line start, end, middle and the edges inside hidden
    // marker runs (#, >, -, *, _, ~, `, list prefix, cell boundaries), never splitting a surrogate.
    static void PlaceCaret(MainWindow window, Random random, StepContext ctx) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) { ctx.Target = "none"; return; }
        var document = source.Document;
        if (random.Next(8) == 0) {
            source.Select(random.Next(2) == 0 ? 0 : document.TextLength, 0);
            ctx.Target = random.Next(2) == 0 ? "doc-start" : "doc-end";
            return;
        }
        DocumentLine line = document.GetLineByNumber(1 + random.Next(document.LineCount));
        string text = document.GetText(line.Offset, line.Length);
        var choices = new List<int>();
        choices.Add(0);
        choices.Add(text.Length);
        choices.Add(text.Length / 2);
        for (int i = 0; i < text.Length && i < 400; i++) {
            char c = text[i];
            if (c == '*' || c == '_' || c == '~' || c == '`' || c == '#' || c == '>' || c == '-' || c == '|' || c == '[' || c == ']') {
                choices.Add(i);
                choices.Add(i + 1);
                if (i > 0) choices.Add(i - 1);
            }
        }
        MarkdownLivePreview.Session session = Session(window);
        MarkdownLivePreview.LineInfo info = session == null ? null : session.Info(line);
        string category = "line";
        if (info != null) {
            if (info.LeadEnd > 0) { choices.Add(info.LeadEnd); choices.Add(info.LeadEnd - 1); }
            if (info.List != null) { choices.Add(info.ListStart); choices.Add(info.ListEnd); choices.Add(info.ListBody); category = "list"; }
            if (info.Table) category = info.Delimiter ? "table:delimiter" : "table:row";
            if (info.Code) category = "code";
            if (info.Cells != null) {
                for (int i = 0; i < info.Cells.Count && i < 20; i++) {
                    choices.Add(info.Cells[i].Start);
                    choices.Add(info.Cells[i].End);
                }
            }
        }
        int column = choices[random.Next(choices.Count)];
        if (column < 0) column = 0;
        if (column > text.Length) column = text.Length;
        while (column > 0 && column < text.Length && Char.IsLowSurrogate(text[column])) column++;
        if (column > 0 && column < text.Length && text[column - 1] == '\r' && text[column] == '\n') column++;
        source.Select(SnapOutOfHidden(window, line.Offset + column), 0);
        ctx.Target = category;
    }

    static void SelectStep(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        var document = source.Document;
        DocumentLine line = document.GetLineByNumber(1 + random.Next(document.LineCount));
        string text = document.GetText(line.Offset, line.Length);
        if (text.Length == 0) { source.Select(line.Offset, 0); return; }
        int start = random.Next(text.Length);
        int length = 1 + random.Next(Math.Max(1, Math.Min(24, text.Length - start)));
        if (start + length > text.Length) length = text.Length - start;
        while (length > 0 && start + length < text.Length && Char.IsLowSurrogate(text[start + length])) length--;
        int a = SnapOutOfHidden(window, line.Offset + start);
        int b = SnapOutOfHidden(window, line.Offset + start + length);
        if (b < a) { int swap = a; a = b; b = swap; }
        source.Select(a, b - a);
    }

    // The basic view (HideMarkers on) hides every marker run, and its own caret rule
    // (MarkdownLivePreview.SnapHiddenCaret/HiddenSnap) never lets a directly set caret rest inside
    // one. A mouse or Shift selection maps to visible columns too, so the probe must not manufacture
    // a pre-state a user cannot reach; a caret created there once made a later zoom look like it
    // moved the caret (the post-render caret guard pulled it out of a hidden &#45; entity). This
    // only shapes the probe's input state; every switch invariant below is unchanged.
    // A table row follows that same rule with one exception (HiddenSnap): a hidden run that lies
    // inside a cell's own text still takes the caret to its near edge, while the row's pipes and
    // the padding around them are the row projection's own caret stops, so a caret there stays.
    static int SnapOutOfHidden(MainWindow window, int offset) {
        if (!Static<bool>("hideMarkers")) return offset;
        MarkdownLivePreview.Session session = Session(window);
        SourceEditor source = Source(window);
        if (session == null || source == null || source.Document == null) return offset;
        var document = source.Document;
        offset = Math.Max(0, Math.Min(offset, document.TextLength));
        DocumentLine line = document.GetLineByOffset(offset);
        MarkdownLivePreview.LineInfo info = session.Info(line);
        if (info == null) return offset;
        int relative = offset - line.Offset;
        if (info.LeadEnd > 0 && relative < info.LeadEnd) return line.Offset + info.LeadEnd;
        bool table = info.Table && info.Cells != null && info.Cells.Count > 0;
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Length <= 0) continue;
            if (relative <= replacement.Start || relative >= replacement.Start + replacement.Length) continue;
            if (table && !InsideTableCell(info, replacement) && InCellPadding(info, relative)) continue;
            int start = replacement.Start, end = replacement.Start + replacement.Length;
            return line.Offset + (relative - start <= end - relative ? start : end);
        }
        return offset;
    }
    // True when the whole run lies inside one cell's text: only a cell's own markup takes the caret
    // back to a visible character; a run between two cells belongs to neither cell.
    static bool InsideTableCell(MarkdownLivePreview.LineInfo info, MarkdownLivePreview.Replacement replacement) {
        int end = replacement.Start + replacement.Length;
        for (int i = 0; i < info.Cells.Count; i++) {
            MarkdownLivePreview.Cell cell = info.Cells[i];
            if (replacement.Start >= cell.Start && end <= cell.End) return true;
        }
        return false;
    }
    // True when the offset sits in a cell's trailing padding: after that cell's text, before its
    // following pipe, with only spaces/tabs between. The product keeps the caret there (a typed
    // space at the cell's end is the cell's word gap); the padding on the next cell's side and the
    // pipes themselves still snap to a visible character.
    static bool InCellPadding(MarkdownLivePreview.LineInfo info, int relative) {
        for (int i = 0; i < info.Cells.Count; i++) {
            MarkdownLivePreview.Cell cell = info.Cells[i];
            if (relative <= cell.End) continue;
            int p = cell.End;
            while (p < info.Text.Length && (info.Text[p] == ' ' || info.Text[p] == '\t')) p++;
            if (p == cell.End) continue;
            if (p < info.Text.Length && info.Text[p] == '|') { if (relative < p) return true; continue; }
            // The last cell's padding runs to the line's end (the row has no closing pipe).
            if (p == info.Text.Length) return true;
        }
        return false;
    }

    static void ScrollStep(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        int lines = source.Document.LineCount;
        source.ScrollToLine(random.Next(lines));
    }

    // ---------------------------------------------------------------- payloads
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
    static readonly string[] PasteSnippets = new string[] {
        "**굵은 붙여넣기** 문장",
        "- 목록 항목\n- 두 번째 항목\n",
        "| 가 | 나 |\n| --- | --- |\n| 다 | 라 |\n",
        "`인라인 코드` 와 *기울임*",
        "여러 줄\n붙여넣기\n문장입니다.\n",
        "가 | 나 | 다",
        "a\tb | c",
        "<span style=\"font-size:18px\">붙인 글자</span>",
        "한글 붙여넣기 값 42"
    };

    static string TypingText(Random random) {
        switch (random.Next(12)) {
            case 0: return "가나다";
            case 1: return RandomOf(random, KoreanSyllables, 1 + random.Next(3));
            case 2: return " ";
            case 3: return "**";
            case 4: return "*";
            case 5: return "`";
            case 6: return "~~";
            case 7: return "|";
            case 8: return "abc";
            case 9: return RandomOf(random, LatinChars, 1 + random.Next(5));
            case 10: return "日本語";
            default: return "값";
        }
    }
    static string RandomOf(Random random, string alphabet, int count) {
        var text = new StringBuilder(count);
        for (int i = 0; i < count; i++) text.Append(alphabet[random.Next(alphabet.Length)]);
        return text.ToString();
    }

    // ---------------------------------------------------------------- invariants
    static bool CheckStep(StepContext ctx) {
        bool ok = true;
        MainWindow window = ctx.Window;
        SourceEditor source = Source(window);
        string mirror = null;
        Bump(checkCounts, "text:mirror");
        try { mirror = Prop<string>(window, "text"); } catch (Exception) { mirror = null; }
        if (mirror == null || !String.Equals(mirror, ctx.PostText, StringComparison.Ordinal)) {
            ok = Fail(ctx, "text:mirror", "window.text equals the source document", mirror == null ? "<null>" : Diff(ctx.PostText, mirror),
                "The document and the text mirror the save/title path uses disagree.");
        }
        Bump(checkCounts, "caret:range");
        if (ctx.PostCaret < 0 || ctx.PostCaret > ctx.PostText.Length) {
            ok = Fail(ctx, "caret:range", "0.." + ctx.PostText.Length, ctx.PostCaret.ToString(CultureInfo.InvariantCulture),
                "The caret left the document after the step.");
        }
        Bump(checkCounts, "selection:range");
        if (ctx.PostSelStart < 0 || ctx.PostSelLength < 0 || (long)ctx.PostSelStart + ctx.PostSelLength > ctx.PostText.Length) {
            ok = Fail(ctx, "selection:range", "inside the document", ctx.PostSelStart + "+" + ctx.PostSelLength,
                "The selection left the document after the step.");
        }
        if (IsSwitchKind(ctx.Kind)) {
            bool passed = CheckSwitch(ctx);
            ok = ok && passed;
        }
        bool overlay = CheckOverlay(ctx);
        return ok && overlay;
    }

    static bool CheckSwitch(StepContext ctx) {
        bool ok = true;
        CheckText(ctx, ref ok);
        CheckCaret(ctx, ref ok);
        CheckSelection(ctx, ref ok);
        CheckHistory(ctx, ref ok);
        CheckState(ctx, ref ok);
        CheckToolbar(ctx, ref ok);
        CheckVisible(ctx, ref ok);
        return ok;
    }

    // View switches must not touch the text. A TXT conversion must equal the documented converter
    // output (MarkdownPlainText.Convert for MD -> TXT, LiteralMarkdown for TXT -> MD).
    static void CheckText(StepContext ctx, ref bool ok) {
        if (ctx.Kind == "txt") {
            Bump(checkCounts, "text:txt");
            string expected = ctx.PrePlain ? Literal(ctx.PreText) : MarkdownPlainText.Convert(ctx.PreText);
            if (!String.Equals(ctx.PostText, expected, StringComparison.Ordinal)) {
                ok = Fail(ctx, "text:txt", expected, ctx.PostText,
                    "TXT conversion " + (ctx.PrePlain ? "TXT->MD" : "MD->TXT") + " differs from the converter");
            }
            return;
        }
        Bump(checkCounts, "text:view");
        if (!String.Equals(ctx.PostText, ctx.PreText, StringComparison.Ordinal)) {
            ok = Fail(ctx, "text:view", "unchanged", ctx.PostText,
                ctx.Label + " changed the document text (length " + ctx.PreText.Length + " -> " + ctx.PostText.Length + ")");
        }
    }

    // The same source offset for every non-TXT switch. TXT uses the app's documented mapping:
    // MD->TXT is LivePlainCaret (converted prefix length, line-start floor) and TXT->MD is
    // PlaceLiveLiteralCaret (literal escaped length inside the mapped line).
    static void CheckCaret(StepContext ctx, ref bool ok) {
        if (ctx.Kind == "txt") {
            Bump(checkCounts, "caret:txt");
            int expected = ExpectedTxtCaret(ctx);
            if (ctx.PostCaret != expected) {
                ok = Fail(ctx, "caret:txt", expected.ToString(CultureInfo.InvariantCulture), ctx.PostCaret.ToString(CultureInfo.InvariantCulture),
                    "TXT caret mapping " + (ctx.PrePlain ? "TXT->MD" : "MD->TXT") + " (pre caret " + ctx.PreCaret + "); " +
                    CaretContext(ctx.PreText, ctx.PreCaret) + " -> " + CaretContext(ctx.PostText, ctx.PostCaret));
            }
            return;
        }
        Bump(checkCounts, "caret:view");
        if (ctx.PostCaret != ctx.PreCaret) {
            // MarkdownLivePreview.SnapHiddenCaret/HiddenSnap (the basic view's own documented rule:
            // "an offset set directly (undo, search, a command) is pulled to the nearest visible
            // boundary") pulls a caret that sits inside a hidden marker run out of it when the basic
            // view becomes active. Switching live -> basic therefore moves exactly that caret to the
            // boundary the rule names; the text is unchanged and the destination is that boundary,
            // not an arbitrary offset. Accept the documented snap and count it on its own line.
            if (ctx.PostHide && !ctx.PreHide && SnapOutOfHidden(ctx.Window, ctx.PreCaret) == ctx.PostCaret) {
                Bump(checkCounts, "caret:view:snap");
                return;
            }
            string hint = ctx.PostHide && !ctx.PreHide
                ? " The basic view (HideMarkers on) documents pulling a directly set caret to a visible hidden-marker boundary (MarkdownLivePreview.SnapHiddenCaret); this is still a source-offset change across the switch."
                : "";
            ok = Fail(ctx, "caret:view", ctx.PreCaret.ToString(CultureInfo.InvariantCulture), ctx.PostCaret.ToString(CultureInfo.InvariantCulture),
                ctx.Label + " moved the caret although the text did not change; " +
                CaretContext(ctx.PreText, ctx.PreCaret) + " -> " + CaretContext(ctx.PostText, ctx.PostCaret) + hint);
        }
    }

    static int ExpectedTxtCaret(StepContext ctx) {
        MainWindow window = ctx.Window;
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return -1;
        if (!ctx.PrePlain) {
            int plain = ExpectedPlainCaret(ctx.PreText, ctx.PreCaret);
            int canonicalLength = Canonical(ctx.PostText).Length;
            if (plain > canonicalLength) plain = canonicalLength;
            return ActualSourceOffset(ctx.PostText, plain);
        }
        string canonical = Canonical(ctx.PreText);
        int caret = Math.Max(0, Math.Min(ctx.PreCaret, canonical.Length));
        int line = 0, lineStart = 0;
        for (int i = 0; i < caret; i++) if (canonical[i] == '\n') { line++; lineStart = i + 1; }
        var document = source.Document;
        DocumentLine target = document.GetLineByNumber(Math.Min(document.LineCount, line + 1));
        int column = caret > lineStart ? Literal(canonical.Substring(lineStart, caret - lineStart)).Length : 0;
        return target.Offset + Math.Min(column, target.Length);
    }

    // MarkdownLivePreview / TextMode's own rule (MainWindow.LivePlainCaret), reproduced here so a
    // wiring fault in a switch is not hidden by calling the same product method as the oracle.
    // The two characters around a caret are enough to tell an ordinary offset from a position no
    // user input produces: between CRLF, or between the halves of a surrogate pair.
    static string CaretContext(string value, int offset) {
        if (value == null) return "text=<null>";
        if (offset < 0) offset = 0;
        if (offset > value.Length) offset = value.Length;
        bool splitCrlf = offset > 0 && offset < value.Length && value[offset - 1] == '\r' && value[offset] == '\n';
        bool splitSurrogate = offset > 0 && offset < value.Length && Char.IsLowSurrogate(value[offset]) && Char.IsHighSurrogate(value[offset - 1]);
        string before = offset > 12 ? value.Substring(offset - 12, 12) : value.Substring(0, offset);
        string after = offset + 12 <= value.Length ? value.Substring(offset, 12) : value.Substring(offset);
        return "caret " + offset + " splitCRLF=" + splitCrlf + " splitSurrogate=" + splitSurrogate +
            " [" + OneLine(before) + "|" + OneLine(after) + "]";
    }

    static int ExpectedPlainCaret(string markdown, int rawCaret) {
        if (markdown == null) markdown = "";
        rawCaret = Math.Max(0, Math.Min(rawCaret, markdown.Length));
        int lineStart = rawCaret == 0 ? 0 : markdown.LastIndexOf('\n', rawCaret - 1) + 1;
        int plainLineStart = lineStart == 0 ? 0 : Canonical(MarkdownPlainText.Convert(markdown.Substring(0, lineStart))).Length + 1;
        if (rawCaret == lineStart) return plainLineStart;
        return Math.Max(plainLineStart, Canonical(MarkdownPlainText.Convert(markdown.Substring(0, rawCaret))).Length);
    }

    // A switch moves no caret and changes no text, so a selection may stay exactly as it was or be
    // cleared; a different non-empty range is the inconsistency this check looks for.
    static void CheckSelection(StepContext ctx, ref bool ok) {
        Bump(checkCounts, "selection:switch");
        bool preserved = ctx.PostSelStart == ctx.PreSelStart && ctx.PostSelLength == ctx.PreSelLength;
        bool cleared = ctx.PostSelLength == 0;
        if (!preserved && !cleared && !(ctx.Kind == "txt" && ctx.PreSelLength > 0 && ctx.PostSelLength == 0)) {
            ok = Fail(ctx, "selection", "preserved " + ctx.PreSelStart + "+" + ctx.PreSelLength + " or cleared",
                ctx.PostSelStart + "+" + ctx.PostSelLength, ctx.Label + " left a different selection");
        }
        if (ctx.Kind == "txt" && ctx.PreSelLength > 0 && ctx.PostSelLength != 0) {
            ok = Fail(ctx, "selection:txt", "cleared (0)", ctx.PostSelStart + "+" + ctx.PostSelLength,
                "A TXT conversion collapsed the caret but left the old selection range behind");
        }
    }

    // Pure view switches never add or drop undo entries. A TXT conversion records exactly one
    // mode-aware step whose BeforeMode is the pre-switch mode (DocumentHistory.Record in
    // MainWindow.CommitTextMode), so one undo returns the pre-switch text and mode.
    static void CheckHistory(StepContext ctx, ref bool ok) {
        if (ctx.Kind == "txt") {
            Bump(checkCounts, "history:txt");
            if (ctx.PostUndo != ctx.PreUndo + 1 || ctx.PostRedo != 0 || !Object.Equals(ctx.PostUndoMode, (bool?)ctx.PrePlain)) {
                ok = Fail(ctx, "history:txt", "undo +1, redo 0, UndoMode=" + ctx.PrePlain,
                    "undo " + ctx.PreUndo + "->" + ctx.PostUndo + ", redo " + ctx.PreRedo + "->" + ctx.PostRedo + ", UndoMode=" + ModeText(ctx.PostUndoMode),
                    "The TXT conversion did not record exactly one mode-aware undo step.");
            }
            return;
        }
        Bump(checkCounts, "history:view");
        if (ctx.PostUndo != ctx.PreUndo || ctx.PostRedo != ctx.PreRedo ||
            !Object.Equals(ctx.PostUndoMode, ctx.PreUndoMode) || !Object.Equals(ctx.PostRedoMode, ctx.PreRedoMode)) {
            ok = Fail(ctx, "history:view", "undo " + ctx.PreUndo + ", redo " + ctx.PreRedo + ", modes " + ModeText(ctx.PreUndoMode) + "/" + ModeText(ctx.PreRedoMode),
                "undo " + ctx.PostUndo + ", redo " + ctx.PostRedo + ", modes " + ModeText(ctx.PostUndoMode) + "/" + ModeText(ctx.PostRedoMode),
                ctx.Label + " changed the undo history although it changed no text");
        }
    }

    static string ModeText(bool? mode) {
        return mode.HasValue ? (mode.Value ? "plain" : "md") : "none";
    }

    static bool EnvHideMarkers() {
        return Program.IsSelfTest && String.Equals(Environment.GetEnvironmentVariable("MDNOTE_HIDE_MARKERS"), "1", StringComparison.Ordinal);
    }

    // The post state must follow the switch's documented effect, and the preview session's own
    // Options must carry it (HideMarkers, ReadOnly) so the drawn page is the mode the user chose.
    static void CheckState(StepContext ctx, ref bool ok) {
        Bump(checkCounts, "state:mode");
        if (ctx.Kind == "view") {
            if (ctx.PostHide == ctx.PreHide) {
                ok = Fail(ctx, "state:mode", "hideMarkers flips", "hideMarkers=" + ctx.PostHide, ctx.Label + " did not change the Basic/Live view");
            }
            if (ctx.PostPlain != ctx.PrePlain || ctx.PostEditing != ctx.PreEditing) {
                ok = Fail(ctx, "state:mode", "plain/editing unchanged", "plain=" + ctx.PostPlain + ", editing=" + ctx.PostEditing, ctx.Label);
            }
        } else if (ctx.Kind == "raw") {
            if (ctx.PostRaw == ctx.PreRaw) {
                ok = Fail(ctx, "state:mode", "raw toggle flips", "preview.IsChecked=" + ctx.PostRaw, ctx.Label + " did not flip the raw view");
            }
            if (ctx.PostPlain != ctx.PrePlain || ctx.PostEditing != ctx.PreEditing || ctx.PostHide != ctx.PreHide) {
                ok = Fail(ctx, "state:mode", "plain/editing/hideMarkers unchanged",
                    "plain=" + ctx.PostPlain + ", editing=" + ctx.PostEditing + ", hide=" + ctx.PostHide, ctx.Label);
            }
        } else if (ctx.Kind == "read") {
            if (ctx.PostEditing == ctx.PreEditing) {
                ok = Fail(ctx, "state:mode", "editing flips", "editing=" + ctx.PostEditing, ctx.Label + " did not flip the read mode");
            }
            if (ctx.PostPlain != ctx.PrePlain || ctx.PostHide != ctx.PreHide) {
                ok = Fail(ctx, "state:mode", "plain/hideMarkers unchanged", "plain=" + ctx.PostPlain + ", hide=" + ctx.PostHide, ctx.Label);
            }
        } else if (ctx.Kind == "zoom") {
            double expected = ctx.PreFont + (ctx.Command == 0 ? 2.0 : -2.0);
            expected = Math.Max(8.0, Math.Min(48.0, expected));
            if (Math.Abs(ctx.PostFont - expected) > 0.001) {
                ok = Fail(ctx, "state:zoom", N(expected) + " px", N(ctx.PostFont) + " px", ctx.Label + " did not apply exactly one zoom step");
            }
            if (ctx.PostPlain != ctx.PrePlain || ctx.PostEditing != ctx.PreEditing || ctx.PostHide != ctx.PreHide) {
                ok = Fail(ctx, "state:mode", "plain/editing/hideMarkers unchanged",
                    "plain=" + ctx.PostPlain + ", editing=" + ctx.PostEditing + ", hide=" + ctx.PostHide, ctx.Label);
            }
        } else if (ctx.Kind == "txt") {
            if (ctx.PostPlain == ctx.PrePlain) {
                ok = Fail(ctx, "state:mode", "PlainText flips", "plain=" + ctx.PostPlain, ctx.Label + " did not change the text mode");
            }
            if (!ctx.PostEditing) {
                ok = Fail(ctx, "state:mode", "editing (ToggleTextMode enters edit mode)", "editing=false", ctx.Label);
            }
            if (ctx.PostRaw) {
                ok = Fail(ctx, "state:mode", "raw view off (CommitTextMode clears preview.IsChecked)", "preview.IsChecked=true", ctx.Label);
            }
            if (ctx.PostHide != ctx.PreHide) {
                ok = Fail(ctx, "state:mode", "hideMarkers unchanged", "hide=" + ctx.PostHide, ctx.Label);
            }
        }
        Bump(checkCounts, "state:attach");
        bool expectedAttached = !ctx.PostPlain && !(ctx.PostEditing && ctx.PostRaw);
        bool attached = Prop<bool>(ctx.Window, "LivePreviewAttached");
        if (attached != expectedAttached) {
            ok = Fail(ctx, "state:attach", "attached=" + expectedAttached, "attached=" + attached,
                "plain=" + ctx.PostPlain + ", editing=" + ctx.PostEditing + ", raw=" + ctx.PostRaw);
        }
        if (attached) {
            MarkdownLivePreview.Session session = Session(ctx.Window);
            Bump(checkCounts, "state:settings");
            if (session == null || session.Options == null) {
                ok = Fail(ctx, "state:settings", "a session with Options", session == null ? "session=null" : "Options=null", ctx.Label);
            } else {
                // Settings.HideMarkersFromEnvironment is a documented probe seam: when a caller
                // sets MDNOTE_HIDE_MARKERS=1 the session hides markers whatever the view does.
                bool expectedHide = ctx.PostHide || EnvHideMarkers();
                if (session.Options.HideMarkers != expectedHide || session.Options.ReadOnly != !ctx.PostEditing) {
                    ok = Fail(ctx, "state:settings", "HideMarkers=" + expectedHide + ", ReadOnly=" + (!ctx.PostEditing),
                        "HideMarkers=" + session.Options.HideMarkers + ", ReadOnly=" + session.Options.ReadOnly,
                        "The preview session did not receive the state the switch left behind.");
                }
            }
        }
        Bump(checkCounts, "state:editbutton");
        Button editButton = Field<Button>(ctx.Window, "editButton");
        string editTag = editButton.Tag == null ? "" : editButton.Tag.ToString();
        if ((ctx.PostEditing ? "Active" : "") != editTag) {
            ok = Fail(ctx, "state:editbutton", ctx.PostEditing ? "Active" : "(none)", editTag, "The edit toggle did not follow the read mode state.");
        }
    }

    // ---------------------------------------------------------------- toolbar, overlay, visibility
    // The format tags after a switch must equal what UpdateTools computes for the same caret. A
    // stale toolbar changes under that second call; a fresh one does not.
    static void CheckToolbar(StepContext ctx, ref bool ok) {
        if (ctx.PostEditing && !ctx.PostPlain && ctx.PostToolbar != null && ctx.RefreshedToolbar != null) {
            Bump(checkCounts, "toolbar:stale");
            if (!String.Equals(ctx.PostToolbar, ctx.RefreshedToolbar, StringComparison.Ordinal)) {
                ok = Fail(ctx, "toolbar:stale", ctx.RefreshedToolbar, ctx.PostToolbar,
                    "The format buttons kept the pre-switch caret context; UpdateTools changed them to the current one.");
            }
        }
        Bump(checkCounts, "toolbar:table");
        bool attached = Prop<bool>(ctx.Window, "LivePreviewAttached");
        bool interested = ctx.PostEditing && !ctx.PostPlain && attached;
        bool inTable = interested && CaretOnTableRow(ctx.Window);
        bool expected = interested && inTable;
        Border host = Field<Border>(ctx.Window, "tableToolsHost");
        if ((host.Visibility == Visibility.Visible) != expected) {
            Pump();
            ctx.Window.UpdateLayout();
            Pump();
            if ((host.Visibility == Visibility.Visible) != expected) {
                ok = Fail(ctx, "toolbar:table", expected ? "Visible" : "Collapsed", host.Visibility.ToString(),
                    "editing=" + ctx.PostEditing + ", plain=" + ctx.PostPlain + ", attached=" + attached + ", caret on table=" + inTable);
            }
        }
    }

    static bool CaretOnTableRow(MainWindow window) {
        MarkdownLivePreview.Session session = Session(window);
        SourceEditor source = Source(window);
        if (session == null || source == null || source.Document == null) return false;
        int at = Math.Max(0, Math.Min(source.CaretOffset, source.Document.TextLength));
        MarkdownLivePreview.LineInfo info = session.Info(source.Document.GetLineByOffset(at));
        return info != null && info.Table;
    }

    // The session draws its own table caret; outside a table row that rectangle must be empty, or
    // the user sees a caret blink in the place the old cell used to be.
    static bool CheckOverlay(StepContext ctx) {
        MainWindow window = ctx.Window;
        MarkdownLivePreview.Session session = Session(window);
        if (session == null) return true;
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return true;
        window.UpdateLayout();
        Pump();
        int at = Math.Max(0, Math.Min(source.CaretOffset, source.Document.TextLength));
        DocumentLine line = source.Document.GetLineByOffset(at);
        MarkdownLivePreview.LineInfo info = session.Info(line);
        bool tableRow = info != null && info.Table;
        Bump(checkCounts, "table:overlay");
        if (!tableRow && !session.DrawnTableCaret.IsEmpty) {
            string lineText = source.Document.GetText(line.Offset, Math.Min(line.Length, 160));
            return Fail(ctx, "table:overlay", "DrawnTableCaret empty (caret is not on a table row)",
                "drawn " + Fmt(session.DrawnTableCaret), "caret line " + line.LineNumber + " [" + OneLine(lineText) + "]");
        }
        return true;
    }

    // The caret line's own pixel span, in the TextEditor's viewport coordinates. A character in a
    // hidden marker run has no rectangle of its own, so the whole caret line is unioned; a fully
    // folded line falls back to the document-space visual position minus the scroll offset.
    static bool LineVisible(MainWindow window, out string detail) {
        detail = "";
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) { detail = "no source"; return false; }
        var view = source.TextArea.TextView;
        view.EnsureVisualLines();
        int caret = Math.Max(0, Math.Min(source.CaretOffset, source.Document.TextLength));
        DocumentLine line = source.Document.GetLineByOffset(caret);
        Rect union = Rect.Empty;
        int from = line.Offset, to = line.EndOffset;
        if (to - from > 800) { from = Math.Max(from, caret - 60); to = Math.Min(to, caret + 60); }
        for (int i = from; i <= to; i++) {
            Rect rect = source.GetRectFromCharacterIndex(i);
            if (!rect.IsEmpty) union = union.IsEmpty ? rect : Rect.Union(union, rect);
        }
        double top, bottom;
        if (!union.IsEmpty) {
            top = union.Top;
            bottom = union.Bottom;
        } else {
            var position = new ICSharpCode.AvalonEdit.TextViewPosition(line.LineNumber, 1);
            double y = view.GetVisualPosition(position, VisualYPosition.LineTop).Y;
            top = y - source.VerticalOffset;
            bottom = top + Math.Max(1, source.FontSize * 1.3);
        }
        detail = "line " + line.LineNumber + ", rect " + (union.IsEmpty ? "(empty)" : Fmt(union)) +
            ", top=" + N(top) + ", bottom=" + N(bottom) + ", viewport=" + N(source.ViewportHeight) + ", offset=" + N(source.VerticalOffset);
        return bottom > 0 && top < source.ViewportHeight;
    }

    // A switch is only held to "the caret line stays visible" when it was on screen before the
    // switch; a user who scrolled the caret away did not ask the toggle to jump back to it.
    static void EnsureCaretVisibleBaseline(MainWindow window, StepContext ctx) {
        string detail;
        ctx.CheckVisible = false;
        ctx.PreVisible = false;
        if (LineVisible(window, out detail)) {
            ctx.CheckVisible = true;
            ctx.PreVisible = true;
            ctx.PreVisibleDetail = detail;
            return;
        }
        SourceEditor source = Source(window);
        if (source != null) {
            source.TextArea.Caret.BringCaretToView();
            Pump();
            window.UpdateLayout();
            Pump();
        }
        if (LineVisible(window, out detail)) {
            ctx.CheckVisible = true;
            ctx.PreVisible = true;
        }
        ctx.PreVisibleDetail = detail;
    }

    static void CheckVisible(StepContext ctx, ref bool ok) {
        if (!ctx.CheckVisible) return;
        Bump(checkCounts, "caret:visible");
        string detail;
        if (!LineVisible(ctx.Window, out detail)) {
            ok = Fail(ctx, "caret:visible", "the caret line stays on screen", "not visible after the switch: " + detail,
                "The caret line was visible before the switch: " + ctx.PreVisibleDetail);
        }
    }

    // ---------------------------------------------------------------- text round trip
    static string Literal(string value) {
        if (literalMethod == null) literalMethod = typeof(MainWindow).GetMethod("LiteralMarkdown", Members);
        if (literalMethod == null) throw new MissingMemberException("probe: MainWindow has no method 'LiteralMarkdown'");
        return (string)literalMethod.Invoke(null, new object[] { value });
    }

    static StepContext RoundContext(MainWindow window, string label, string pre, string post) {
        var ctx = new StepContext();
        ctx.Window = window;
        ctx.Kind = "txt";
        ctx.Label = label;
        ctx.PreText = pre;
        ctx.PostText = post;
        ctx.PreCaret = 0;
        ctx.PostCaret = 0;
        return ctx;
    }

    static bool RoundCheck(MainWindow window, string invariant, string label, string expected, string actual, string pre, string post) {
        Bump(checkCounts, "roundtrip");
        if (String.Equals(expected, actual, StringComparison.Ordinal)) return true;
        return Fail(RoundContext(window, label, pre, post), invariant, expected, actual,
            "TXT round trip check at " + label + " (no user edit involved)");
    }

    static bool RoundMode(MainWindow window, string invariant, string label, bool expected, string pre, string post) {
        Bump(checkCounts, "roundtrip:mode");
        bool actual = Prop<bool>(window, "PlainText");
        if (actual == expected) return true;
        return Fail(RoundContext(window, label, pre, post), invariant, expected ? "PlainText=true" : "PlainText=false",
            actual ? "PlainText=true" : "PlainText=false", "The mode-aware undo/redo did not restore the text mode.");
    }

    // MD -> TXT -> MD -> TXT on the untouched fixture: the converter output, one-step undo/redo
    // in both directions, and the lossless TXT -> MD -> TXT identity the brief requires.
    static bool CheckTextRoundTrip(MainWindow window, string fixture, bool liveMode) {
        bool ok = true;
        string md0 = DocText(window);
        Invoke(window, "ToggleTextMode");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        string plain1 = DocText(window);
        ok = RoundCheck(window, "roundtrip:md-to-txt", "roundtrip:md-to-txt", MarkdownPlainText.Convert(md0), plain1, md0, plain1) && ok;
        Invoke(window, "Undo");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        string back0 = DocText(window);
        ok = RoundCheck(window, "roundtrip:undo", "roundtrip:undo", md0, back0, plain1, back0) && ok;
        ok = RoundMode(window, "roundtrip:undo-mode", "roundtrip:undo-mode", false, plain1, back0) && ok;
        Invoke(window, "Redo");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        string redone = DocText(window);
        ok = RoundCheck(window, "roundtrip:redo", "roundtrip:redo", plain1, redone, back0, redone) && ok;
        ok = RoundMode(window, "roundtrip:redo-mode", "roundtrip:redo-mode", true, back0, redone) && ok;
        Invoke(window, "ToggleTextMode");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        string md2 = DocText(window);
        ok = RoundCheck(window, "roundtrip:txt-to-md", "roundtrip:txt-to-md", Literal(plain1), md2, redone, md2) && ok;
        Invoke(window, "ToggleTextMode");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        string plain3 = DocText(window);
        ok = RoundCheck(window, "roundtrip:no-loss", "roundtrip:no-loss", plain1, plain3, md2, plain3) && ok;
        for (int i = 0; i < 3; i++) { Invoke(window, "Undo"); Pump(); }
        Invoke(window, "FlushDocumentChanges");
        string undoneAll = DocText(window);
        ok = RoundCheck(window, "roundtrip:undo-all", "roundtrip:undo-all", md0, undoneAll, plain3, undoneAll) && ok;
        ok = RoundMode(window, "roundtrip:undo-all-mode", "roundtrip:undo-all-mode", false, plain3, undoneAll) && ok;
        for (int i = 0; i < 3; i++) { Invoke(window, "Redo"); Pump(); }
        Invoke(window, "FlushDocumentChanges");
        string redoneAll = DocText(window);
        ok = RoundCheck(window, "roundtrip:redo-all", "roundtrip:redo-all", plain3, redoneAll, undoneAll, redoneAll) && ok;
        ok = RoundMode(window, "roundtrip:redo-all-mode", "roundtrip:redo-all-mode", true, undoneAll, redoneAll) && ok;
        return ok;
    }

    // ---------------------------------------------------------------- directed switch sweep
    // A fixed caret context (table cell, bold run, code line, paragraph end, hidden marker) is
    // switched through every view type in a known order once, before the random sequences. This
    // guarantees each switch kind meets each difficult caret context even when the seed does not.
    static void ResetBase(MainWindow window, string fixture, bool liveMode) {
        try {
            if (Prop<bool>(window, "PlainText")) { Invoke(window, "ToggleTextMode"); Pump(); }
            if (!Field<bool>(window, "editing")) { Invoke(window, "SetMode", true); Pump(); }
            if (RawChecked(window)) { SetRaw(window, false); Pump(); }
            double font = Field<double>(window, "fontSize");
            if (Math.Abs(font - 16.0) > 0.001) {
                Invoke(window, "SetBaseFont", Field<string>(window, "family"), 16.0);
                Pump();
            }
            window.SetText(fixture);
            window.SetMode(true);
            Invoke(window, "SetLiveMode", liveMode);
            Pump();
            window.UpdateLayout();
            Invoke(window, "FlushDocumentChanges");
            Pump();
        } catch (Exception ex) {
            FailOpen("reset-base", "reset before a directed switch failed: " + OneLine(ex.ToString()));
        }
    }

    static List<KeyValuePair<string,int>> ContextOffsets(string text) {
        var list = new List<KeyValuePair<string,int>>();
        AddOffset(list, "table", text.IndexOf("김철수", StringComparison.Ordinal));
        int bold = text.IndexOf("**굵은 글자**", StringComparison.Ordinal);
        if (bold >= 0) AddOffset(list, "bold", bold + 3);
        AddOffset(list, "code", text.IndexOf("public void Test", StringComparison.Ordinal) + (text.IndexOf("public void Test", StringComparison.Ordinal) >= 0 ? 3 : 0));
        int paragraph = text.IndexOf("이 문단은", StringComparison.Ordinal);
        if (paragraph >= 0) {
            int end = paragraph;
            while (end < text.Length && text[end] != '\n' && text[end] != '\r') end++;
            AddOffset(list, "line-end", end);
        }
        AddOffset(list, "hidden", bold);
        AddOffset(list, "list", text.IndexOf("- 목록 항목", StringComparison.Ordinal) >= 0 ? text.IndexOf("- 목록 항목", StringComparison.Ordinal) + 2 : -1);
        // Near the end: the caret is scrolled into view from far down the document, so a switch
        // that changes line heights without preserving the view leaves it off screen.
        AddOffset(list, "deep", text.IndexOf("마지막 문단", StringComparison.Ordinal) >= 0 ? text.IndexOf("마지막 문단", StringComparison.Ordinal) + 2 : -1);
        return list;
    }

    static void AddOffset(List<KeyValuePair<string,int>> list, string label, int offset) {
        if (offset < 0) return;
        list.Add(new KeyValuePair<string,int>(label, offset));
    }

    static void CheckDirectedSwitches(MainWindow window, string fixture, bool liveMode) {
        List<KeyValuePair<string,int>> contexts = ContextOffsets(fixture);
        string[] chain = new string[] { "view", "view", "raw", "raw", "zoom:in", "zoom:out", "read:on", "read:off", "txt" };
        int directed = 0;
        for (int c = 0; c < contexts.Count; c++) {
            if (watch.ElapsedMilliseconds > budgetMilliseconds) { budgetReached = true; all = false; return; }
            ResetBase(window, fixture, liveMode);
            SourceEditor source = Source(window);
            if (source == null || source.Document == null) { FailOpen("directed", "no source for a directed switch"); return; }
            int at = Math.Max(0, Math.Min(contexts[c].Value, source.Document.TextLength));
            while (at > 0 && at < source.Document.TextLength && Char.IsLowSurrogate(source.Document.GetCharAt(at))) at++;
            if (at > 0 && at < source.Document.TextLength && source.Document.GetCharAt(at - 1) == '\r' && source.Document.GetCharAt(at) == '\n') at++;
            source.Select(at, 0);
            Pump();
            for (int i = 0; i < chain.Length; i++) {
                directed++;
                RunDirected(window, contexts[c].Key, chain[i], directed);
            }
        }
    }

    static string DirectedKind(string command) {
        if (command == "view") return "view";
        if (command == "raw") return "raw";
        if (command == "txt") return "txt";
        if (command == "read:on" || command == "read:off") return "read";
        return "zoom";
    }

    static void RunDirected(MainWindow window, string context, string command, int number) {
        if (Source(window) == null) {
            FailOpen("directed", "no source editor before the directed switch");
            return;
        }
        var ctx = new StepContext();
        ctx.Window = window;
        ctx.Kind = DirectedKind(command);
        ctx.Command = 0;
        ctx.Target = null;
        SnapshotPre(window, ctx);
        string baseLabel;
        if (ctx.Kind == "view" || ctx.Kind == "raw") {
            baseLabel = LabelFor(window, ctx);
        } else if (command == "zoom:in") {
            ctx.Command = 0; ctx.Target = "in"; baseLabel = "zoom:in";
        } else if (command == "zoom:out") {
            ctx.Command = 1; ctx.Target = "out"; baseLabel = "zoom:out";
        } else if (command == "read:on") {
            ctx.Target = "on"; baseLabel = "read:on";
        } else if (command == "read:off") {
            ctx.Target = "off"; baseLabel = "read:off";
        } else {
            baseLabel = ctx.PrePlain ? "txt:plain-to-md" : "txt:md-to-plain";
        }
        ctx.Label = "directed:" + context + ":" + baseLabel;
        currentAction = ctx.Label;
        currentStep++;
        stepNames.Add("D" + number.ToString(CultureInfo.InvariantCulture) + " " + ctx.Label);
        Bump(opCounts, ctx.Label);
        EnsureCaretVisibleBaseline(window, ctx);
        Exception error = null;
        try {
            ExecuteDirected(window, command, ctx);
            Pump();
            Invoke(window, "FlushDocumentChanges");
            Pump();
            window.UpdateLayout();
            Pump();
        } catch (Exception ex) {
            error = ex;
        }
        if (error != null) {
            Fail(ctx, "exception", "no exception", error.GetType().Name + ": " + OneLine(error.Message), FirstFrames(error));
            return;
        }
        if (dispatcherFailed) {
            Fail(ctx, "dispatcher", "no dispatcher exception", LastDispatcherNote(), "");
            return;
        }
        SnapshotPost(window, ctx);
        CheckStep(ctx);
    }

    static void ExecuteDirected(MainWindow window, string command, StepContext ctx) {
        if (command == "view") { Invoke(window, "SetLiveMode", ctx.Target == "live"); return; }
        if (command == "raw") { SetRaw(window, ctx.Target == "on"); return; }
        if (command == "zoom:in") { Invoke(window, "Zoom", 2.0); return; }
        if (command == "zoom:out") { Invoke(window, "Zoom", -2.0); return; }
        if (command == "read:on") { Invoke(window, "SetMode", false); return; }
        if (command == "read:off") { Invoke(window, "SetMode", true); return; }
        Invoke(window, "ToggleTextMode");
    }

    static bool CheckFixtureTables(MainWindow window) {
        MarkdownLivePreview.Session session = Session(window);
        SourceEditor source = Source(window);
        if (session == null || source == null || source.Document == null) {
            FailOpen("fixture", "the fixture has no live preview session");
            return false;
        }
        int tableLines = 0, headers = 0;
        foreach (DocumentLine line in source.Document.Lines) {
            MarkdownLivePreview.LineInfo info = session.Info(line);
            if (info == null || !info.Table) continue;
            tableLines++;
            if (info.Header) headers++;
        }
        if (headers == 0 || tableLines < 3) {
            FailOpen("fixture", "the generated fixture has no usable table (" + tableLines + " table lines, " + headers + " headers)");
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- undo oracle
    // Every segment starts with a cleared history and an exact text. Undoing every recorded step
    // must return that text; redoing the same number must return the pre-undo text. This runs
    // after the switches as well, so a switch that drops or corrupts an undo entry shows up.
    static bool CheckUndoOracle(MainWindow window, string segmentStart) {
        Bump(checkCounts, "undo:segment");
        try {
            if (!Field<bool>(window, "editing")) {
                Invoke(window, "SetMode", true);
                Pump();
            }
            Invoke(window, "FlushDocumentChanges");
            string beforeUndo = DocText(window);
            DocumentHistory history = HistoryOf(window);
            int undoSteps = 0, stalls = 0;
            string previous = beforeUndo;
            // Diagnosis aid (walk B1): one line per step with the stack top the step consumed, the
            // stacks' state after it, and whether a MismatchException / ResetMismatchedHistory fired.
            var undoTrace = new List<string>();
            var redoTrace = new List<string>();
            int mismatch0 = DocumentHistory.MismatchCount, reset0 = DocumentHistory.ResetCount;
            while (history.CanUndo && undoSteps < UndoCap) {
                string top = history.DiagTop(false);
                int mismatchBefore = DocumentHistory.MismatchCount, resetBefore = DocumentHistory.ResetCount;
                int depthBefore = history.UndoDepth;
                Invoke(window, "Undo");
                undoSteps++;
                string now = DocText(window);
                // A text-mode toggle entry leaves the text alone, so several in a row are normal. Only a step that left the
                // stack as it was too is a stall (seed 4 basic step 143 broke out after four mode entries with one entry left).
                bool popped = history.UndoDepth < depthBefore;
                undoTrace.Add("u" + undoSteps + "[" + top + " | " + (history.CanUndo ? "U" : "-") + (history.CanRedo ? "R" : "-")
                    + (String.Equals(now, previous, StringComparison.Ordinal) ? " same" : " moved")
                    + " mm+" + (DocumentHistory.MismatchCount - mismatchBefore) + " reset+" + (DocumentHistory.ResetCount - resetBefore) + "]");
                if (String.Equals(now, previous, StringComparison.Ordinal) && !popped) {
                    stalls++;
                    if (stalls >= 4) break;
                } else stalls = 0;
                previous = now;
            }
            Invoke(window, "FlushDocumentChanges");
            string undone = DocText(window);
            int redoSteps = 0;
            stalls = 0;
            string redoPrevious = undone;
            while (redoSteps < undoSteps && redoSteps < UndoCap) {
                string top = history.DiagTop(true);
                int mismatchBefore = DocumentHistory.MismatchCount, resetBefore = DocumentHistory.ResetCount;
                int redoDepthBefore = history.RedoDepth;
                Invoke(window, "Redo");
                redoSteps++;
                string now = DocText(window);
                bool redone1 = history.RedoDepth < redoDepthBefore;
                redoTrace.Add("r" + redoSteps + "[" + top + " | " + (history.CanUndo ? "U" : "-") + (history.CanRedo ? "R" : "-")
                    + (String.Equals(now, redoPrevious, StringComparison.Ordinal) ? " same" : " moved")
                    + " mm+" + (DocumentHistory.MismatchCount - mismatchBefore) + " reset+" + (DocumentHistory.ResetCount - resetBefore) + "]");
                if (String.Equals(now, redoPrevious, StringComparison.Ordinal) && !redone1) {
                    stalls++;
                    if (stalls >= 4) break;
                } else stalls = 0;
                redoPrevious = now;
            }
            Invoke(window, "FlushDocumentChanges");
            string redone = DocText(window);
            bool startSame = String.Equals(undone, segmentStart, StringComparison.Ordinal);
            if (startSame && redoSteps == undoSteps && String.Equals(redone, beforeUndo, StringComparison.Ordinal)) return true;
            string detail = "undo " + undoSteps + " steps to segment start: " + (startSame ? "same" : Diff(segmentStart, undone)) +
                "; redo " + redoSteps + "/" + undoSteps + " to before-undo: " + (String.Equals(redone, beforeUndo, StringComparison.Ordinal) ? "same" : Diff(beforeUndo, redone));
            string trace = "mismatches=" + (DocumentHistory.MismatchCount - mismatch0) + " resets=" + (DocumentHistory.ResetCount - reset0)
                + " lastMismatch=" + (DocumentHistory.LastMismatch ?? "-") + " final=" + (history.CanUndo ? "U" : "-") + (history.CanRedo ? "R" : "-")
                + " undo-trace(last 24 of " + undoTrace.Count + "): " + String.Join(" ", undoTrace.GetRange(Math.Max(0, undoTrace.Count - 24), Math.Min(24, undoTrace.Count)).ToArray())
                + " redo-trace(first 12 of " + redoTrace.Count + "): " + String.Join(" ", redoTrace.GetRange(0, Math.Min(12, redoTrace.Count)).ToArray());
            return FailUndo("undo:segment", detail, trace);
        } catch (Exception ex) {
            return FailUndo("undo:segment", ex.GetType().Name + ": " + OneLine(ex.Message));
        }
    }

    static bool FailUndo(string invariant, string detail) {
        return FailUndo(invariant, detail, null);
    }

    // trace: the per-step history diagnosis (walk B1), appended to the report detail uncut.
    static bool FailUndo(string invariant, string detail, string trace) {
        Bump(failCounts, invariant);
        failureEvents++;
        all = false;
        if (failures.Count >= MaxFailures) { omittedFailures++; return false; }
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture;
        failure.Step = currentStep;
        failure.Action = "undo-oracle";
        failure.Invariant = invariant;
        failure.Expected = "undo returns the segment start text exactly; redo restores the pre-undo text";
        failure.Actual = OneLine(detail);
        failure.Detail = OneLine(detail) + (trace == null ? "" : " || history: " + trace.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n"));
        failure.Steps = StepListText();
        failure.Before = "(segment start text)";
        failure.After = "(undo oracle at segment end)";
        failures.Add(failure);
        failureKeys[invariant + "|undo-oracle|" + Short(detail, 90)] = failure;
        return false;
    }

    // ---------------------------------------------------------------- report
    static string ComposeReport(StringBuilder runs) {
        var text = new StringBuilder();
        text.Append("# ModeSwitchAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- seeds: ").Append(firstSeed).Append("..").Append(lastSeed).Append(", ").Append(actionsPerSeed)
            .Append(" random steps per sequence, ").Append(SegmentLength).Append("-step history segments\n");
        text.Append("- starts: basic (HideMarkers true) and live (HideMarkers false) for every seed\n");
        text.Append("- fixtures: generated from the seed only; no user file, setting, recovery draft or clipboard is read or written\n");
        text.Append("- surface: offscreen MainWindow, real SetLiveMode / 원문 toggle / ToggleTextMode / SetMode / Zoom entry points\n");
        text.Append("- accepted product rule: switching live -> basic may pull a caret that sits inside a hidden marker run to the visible boundary the basic view draws (MarkdownLivePreview.SnapHiddenCaret/HiddenSnap: \"an offset set directly (undo, search, a command) is pulled to the nearest visible boundary\"). The check counts those moves as caret:view:snap and still fails any other caret change across a view switch.\n");
        text.Append("- budget: ").Append(budgetMilliseconds).Append(" ms")
            .Append(budgetReached ? " (reached; run incomplete)" : "").Append('\n');
        text.Append("- runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms\n");
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
            int occurrences = omittedFailures;
            foreach (FailureInfo counted in failures) occurrences += counted.Occurrences;
            text.Append("- distinct: ").Append(failures.Count).Append(", occurrences: ").Append(occurrences).Append('\n');
            foreach (FailureInfo failure in failures) {
                text.Append("\n### seed ").Append(failure.Seed).Append(' ').Append(failure.Mode).Append(", fixture ").Append(failure.Fixture)
                    .Append(", step ").Append(failure.Step).Append(' ').Append(failure.Action).Append('\n');
                text.Append("- invariant: ").Append(failure.Invariant).Append('\n');
                text.Append("- occurrences: ").Append(failure.Occurrences).Append('\n');
                text.Append("- expected: ").Append(failure.Expected).Append('\n');
                text.Append("- actual: ").Append(failure.Actual).Append('\n');
                if (!String.IsNullOrEmpty(failure.Detail)) text.Append("- detail: ").Append(failure.Detail).Append('\n');
                text.Append("- steps: ").Append(failure.Steps).Append('\n');
                text.Append("- before:\n").Append(failure.Before).Append('\n');
                text.Append("- after:\n").Append(failure.After).Append('\n');
            }
            if (omittedFailures > 0) text.Append("\n- ").Append(omittedFailures).Append(" further failures were omitted (report cap)\n");
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
        int start = Math.Max(0, stepNames.Count - StepListLimit);
        var text = new StringBuilder();
        if (start > 0) text.Append("...(").Append(start).Append(" earlier) | ");
        for (int i = start; i < stepNames.Count; i++) {
            if (i > start) text.Append(" | ");
            text.Append(stepNames[i]);
        }
        return text.ToString();
    }

    static string Excerpt(string value, int offset) {
        if (value == null) return "(none)";
        if (offset < 0) offset = 0;
        if (offset > value.Length) offset = value.Length;
        int line = 0;
        for (int i = 0; i < offset; i++) if (value[i] == '\n') line++;
        int at = 0, number = 0;
        var text = new StringBuilder();
        while (at <= value.Length && number <= line + 2) {
            int end = at;
            while (end < value.Length && value[end] != '\n' && value[end] != '\r') end++;
            if (number >= line - 2) {
                text.Append(number + 1).Append("| ").Append(value.Substring(at, end - at)).Append('\n');
            }
            int delim = 0;
            if (end < value.Length) {
                delim = 1;
                if (value[end] == '\r' && end + 1 < value.Length && value[end + 1] == '\n') delim = 2;
            }
            if (end + delim >= value.Length) break;
            at = end + delim;
            number++;
        }
        string result = text.ToString();
        if (result.Length > 1500) result = result.Substring(0, 1500) + "...";
        return result;
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
    static string Short(string value, int length) {
        string text = OneLine(value);
        return text.Length <= length ? text : text.Substring(0, length) + "...";
    }
    // Digits (offsets, counts) become '#': two occurrences of the same failure at different
    // offsets collapse into one report entry, while a different message stays distinct.
    static string Shape(string value) {
        string text = OneLine(value);
        var shape = new StringBuilder(Math.Min(text.Length, 80));
        for (int i = 0; i < text.Length && shape.Length < 80; i++) {
            char c = text[i];
            if (Char.IsDigit(c)) {
                if (i == 0 || !Char.IsDigit(text[i - 1])) shape.Append('#');
            } else shape.Append(c);
        }
        return shape.ToString();
    }
    static string N(double value) {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
    static string Fmt(Rect rect) {
        if (rect.IsEmpty) return "(empty)";
        return N(rect.Left) + ".." + N(rect.Right) + " x " + N(rect.Top) + ".." + N(rect.Bottom);
    }
    static string FirstFrames(Exception error) {
        string message = error.GetType().Name + ": " + OneLine(error.Message);
        if (error.StackTrace == null) return message;
        string[] frames = error.StackTrace.Replace("\r","").Split('\n');
        return message + " @" + OneLine(String.Join(" < ", frames, 0, Math.Min(3, frames.Length)));
    }

    // ---------------------------------------------------------------- host access
    // MainWindow.Canonical / ActualSourceOffset are private to its partial class; these copies
    // keep the caret oracle independent of the product code it is checking.
    static string Canonical(string value) {
        return value == null ? "" : value.Replace("\r\n", "\n").Replace('\r', '\n');
    }
    static int ActualSourceOffset(string value, int canonical) {
        int at = 0, count = 0;
        while (at < value.Length && count < canonical) {
            if (value[at] == '\r' && at + 1 < value.Length && value[at + 1] == '\n') at++;
            at++;
            count++;
        }
        return at;
    }
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
    static void CloseWindow(MainWindow window) {
        if (window == null) return;
        try {
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
    static int HistoryCount(MainWindow window, bool redo) {
        object history = HistoryOf(window);
        FieldInfo field = typeof(DocumentHistory).GetField(redo ? "redo" : "undo", BindingFlags.Instance|BindingFlags.NonPublic);
        if (field == null) throw new MissingMemberException("probe: DocumentHistory has no field '" + (redo ? "redo" : "undo") + "'");
        IList list = field.GetValue(history) as IList;
        return list == null ? 0 : list.Count;
    }
    static string ToolbarTags(MainWindow window) {
        return TagOf(Field<Button>(window, "boldButton")) + "," +
               TagOf(Field<Button>(window, "italicButton")) + "," +
               TagOf(Field<Button>(window, "strikeButton")) + "," +
               TagOf(Field<Button>(window, "codeButton")) + "," +
               TagOf(Field<Button>(window, "headingButton"));
    }
    static string TagOf(Button button) {
        if (button == null) return "?";
        return button.Tag == null ? "-" : button.Tag.ToString();
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

    // ---------------------------------------------------------------- fixtures
    // Long enough to scroll: about seven screens at 16 px, so a switch that changes line heights
    // can move the caret line off screen and a random scroll really leaves the caret behind.
    // The context markers (김철수, **굵은 글자**, public void Test, 이 문단은, - 목록 항목) appear
    // exactly once, in the leading block every directed sweep searches.
    static string Fixture(int seed) {
        string nl = seed % 2 == 0 ? "\r\n" : "\n";
        var text = new StringBuilder();
        Line(text, nl, "# 모드 전환 시험 문서 " + ((char)('A' + (seed - 1) % 2)).ToString() + seed.ToString(CultureInfo.InvariantCulture));
        Line(text, nl, "");
        Line(text, nl, "이 문단은 **굵은 글자** 와 *기울임* 및 `인라인 코드` 를 포함합니다.");
        Line(text, nl, "");
        Line(text, nl, "| 이름 | 상태 | 메모 |");
        Line(text, nl, "| --- | :-: | --: |");
        Line(text, nl, "| 김철수 | 진행 중 | 아주 긴 셀 값이 들어 있는 칸 |");
        Line(text, nl, "| 日本語 | **완료** | `코드` |");
        Line(text, nl, "");
        Line(text, nl, "- 목록 항목 하나");
        Line(text, nl, "- 목록 항목 둘 **굵게**");
        Line(text, nl, "");
        Line(text, nl, "  | 항목 | 값 |");
        Line(text, nl, "  | --- | --- |");
        Line(text, nl, "  | 가 | 10 |");
        Line(text, nl, "");
        Line(text, nl, "```csharp");
        Line(text, nl, "public void Test() { }");
        Line(text, nl, "int x = 1;");
        Line(text, nl, "```");
        Line(text, nl, "");
        Line(text, nl, "> 인용 **강조** 문장");
        Line(text, nl, "");
        Line(text, nl, "- [ ] 할 일 **중요**");
        Line(text, nl, "- [x] 끝난 일");
        Line(text, nl, "");
        Line(text, nl, "[ref]: https://example.invalid/x");
        Line(text, nl, "");
        for (int block = 1; block <= 6; block++) {
            Line(text, nl, "## " + block.ToString(CultureInfo.InvariantCulture) + "번째 절");
            Line(text, nl, "");
            Line(text, nl, "이 절의 **본문** 입니다. `코드` 와 *기울임* 그리고 보통 글자.");
            Line(text, nl, "");
            Line(text, nl, "| 열 하나 | 열 둘 | 열 셋 |");
            Line(text, nl, "| --- | :-: | --: |");
            Line(text, nl, "| 값 " + block + "-1 | 값 " + block + "-2 | 값 " + block + "-3 |");
            Line(text, nl, "| 값 " + block + "-4 | **값** | `값` |");
            Line(text, nl, "");
            Line(text, nl, "- 반복 목록 " + block + "-1");
            Line(text, nl, "- 반복 목록 " + block + "-2");
            Line(text, nl, "");
            Line(text, nl, "```text");
            Line(text, nl, "code line " + block + " one");
            Line(text, nl, "code line " + block + " two");
            Line(text, nl, "```");
            Line(text, nl, "");
            Line(text, nl, "> 반복 인용문 " + block);
            Line(text, nl, "");
        }
        Line(text, nl, "마지막 문단 **끝**.");
        return text.ToString();
    }
    static void Line(StringBuilder text, string newline, string value) {
        text.Append(value).Append(newline);
    }
}
}
