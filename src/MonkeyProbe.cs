// Monkey probe: random actions on the live surface (AvalonEdit source + MarkdownLivePreview) of an
// offscreen window, so the app's own command, key and paste paths run without a real keyboard or
// mouse. Every fixture is generated here from the seed; no user file is read. The report goes to
// <folder>\monkey-report.txt; monkey-PASS.txt appears only when every seed keeps every invariant.
//
// The undo oracle is checked per 250-action segment, not once per 1500-action run: the app history
// keeps at most 512 entries (DocumentHistory.Add), so a single undo-all after 1500 actions could not
// reach the run start even when undo is correct. Each segment clears the history at its start and
// records that text; undo-all must return to it and redoing the same number of steps must return to
// the text before the undo. The last segment's check is the run-end check.
using System;
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
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
internal static class MonkeyProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    // The defaults run the whole suite. The environment variables below cut a run down to one
    // seed and an action count (the repro of a reported step), without changing the default run:
    //   MDNOTE_MONKEY_SEED=5 MDNOTE_MONKEY_STEPS=448
    // A reduced run writes the document text and caret that the failing action started from, so
    // the case can be replayed as a fixture: monkey-repro-doc.md and monkey-repro-info.txt.
    static int FirstSeed = 1, LastSeed = 8;
    static int ActionsPerSeed = 1500;
    static bool reducedRun;
    static string reproNotePath;
    const int SegmentLength = 250;
    const int InvariantInterval = 25;
    const int UndoCap = 3000;
    const long SlowActionLimit = 2000;
    const int TotalWeight = 105;

    internal sealed class Failure {
        internal int Step;
        internal string Action;
        internal string Error;
        internal string LastActions;
    }
    internal sealed class TimedAction {
        internal long Milliseconds;
        internal int Step;
        internal string Name;
    }

    static string outRoot;
    static string currentAction = "start";
    static int currentSeed, currentStep;
    static bool dispatcherFailed;
    static readonly List<string> dispatcherNotes = new List<string>();
    static readonly List<string> lastActions = new List<string>();
    // The state one action started from, kept only in a reduced run and written once a failure
    // (or a dispatcher exception) is seen: the fixture replays the very same action.
    static string preActionText;
    static int preActionCaret, preActionLine, preActionSelectionStart, preActionSelectionLength;
    static bool preActionLiveFormat, preActionAttached;
    static bool reproWritten;

    internal static int Run(string folder) {
        string dir = Path.GetFullPath(folder);
        Directory.CreateDirectory(dir);
        outRoot = dir;
        string passFile = Path.Combine(dir, "monkey-PASS.txt");
        string reportFile = Path.Combine(dir, "monkey-report.txt");
        if (File.Exists(passFile)) File.Delete(passFile);
        ReadReproOptions(dir);
        var report = new StringBuilder();
        bool all = true;
        var watch = new Stopwatch();
        watch.Start();
        bool oldSelf = Program.IsSelfTest;
        bool oldHide = false;
        bool oldLive = ProbeDefaults.Live;
        Dispatcher dispatcher = null;
        DispatcherUnhandledExceptionEventHandler handler = null;
        try {
            Program.IsSelfTest = true;
            oldHide = Static<bool>("hideMarkers");
            ProbeDefaults.Live = true;
            Static("hideMarkers", remainingHide.HasValue ? remainingHide.Value : true);
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // Closing one seed's window must not end the app when the entry point left the default
            // OnLastWindowClose in place.
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            dispatcher = Application.Current == null ? Dispatcher.CurrentDispatcher : Application.Current.Dispatcher;
            handler = delegate(object sender, DispatcherUnhandledExceptionEventArgs args) { NoteDispatcherException(args); };
            dispatcher.UnhandledException += handler;
            if (Application.Current != null) Application.Current.DispatcherUnhandledException += handler;
            for (int seed = FirstSeed; seed <= LastSeed; seed++) {
                if (!RunSeed(seed, report)) all = false;
            }
            if (!FocusedChecks(report)) all = false;
        } catch (Exception ex) {
            all = false;
            report.Append("\nFATAL: ").Append(OneLine(ex.ToString())).Append('\n');
        } finally {
            try { if (dispatcher != null && handler != null) dispatcher.UnhandledException -= handler; } catch (Exception) { }
            try { if (Application.Current != null && handler != null) Application.Current.DispatcherUnhandledException -= handler; } catch (Exception) { }
            try { Static("hideMarkers", oldHide); } catch (Exception) { }
            ProbeDefaults.Live = oldLive;
            Program.IsSelfTest = oldSelf;
            DeleteGenerated();
            watch.Stop();
        }
        var text = new StringBuilder();
        text.Append("# MonkeyProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- seeds: ").Append(FirstSeed).Append("..").Append(LastSeed).Append(", ").Append(ActionsPerSeed).Append(" actions each\n");
        text.Append("- segments: ").Append(SegmentLength).Append(" actions; history cleared per segment (document cap 512 entries)\n");
        text.Append("- surface: offscreen MainWindow, live view (markers hidden), generated documents only\n");
        if (dispatcherNotes.Count > 0) {
            text.Append("\n# Dispatcher exceptions\n");
            foreach (string note in dispatcherNotes) text.Append("- ").Append(note).Append('\n');
        }
        text.Append(report.ToString());
        text.Append("\nResult: ").Append(all ? "PASS" : "FAIL").Append('\n');
        text.Append("Runtime: ").Append(watch.ElapsedMilliseconds).Append(" ms\n");
        File.WriteAllText(reportFile, text.ToString(), new UTF8Encoding(false));
        if (all) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
        return all ? 0 : 1;
    }

    // ---------------------------------------------------------------- reduced run
    // One seed and an action count from the environment, so a reported step can be replayed with
    // the same generated document, the same action sequence and the same dispatcher drain. The
    // undo oracle runs at the segment boundaries it ran at in the full pass, so the document and
    // caret state at the target step match the full run.
    static void ReadReproOptions(string dir) {
        FirstSeed = 1; LastSeed = 8; ActionsPerSeed = 1500; reducedRun = false;
        reproNotePath = Path.Combine(dir, "monkey-repro-info.txt");
        string seed = Environment.GetEnvironmentVariable("MDNOTE_MONKEY_SEED");
        string steps = Environment.GetEnvironmentVariable("MDNOTE_MONKEY_STEPS");
        if (!String.IsNullOrEmpty(steps)) {
            int count;
            if (Int32.TryParse(steps, NumberStyles.Integer, CultureInfo.InvariantCulture, out count) && count > 0) {
                ActionsPerSeed = count;
                reducedRun = true;
            }
        }
        if (!String.IsNullOrEmpty(seed)) {
            int number;
            if (Int32.TryParse(seed, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) {
                FirstSeed = number;
                LastSeed = number;
                reducedRun = true;
            }
        }
        // The "mode" action flips hideMarkers, so a seed deep in a full pass can start in live
        // mode. A reduced run pins the starting mode the reported seed ran with.
        remainingHide = null;
        string hide = Environment.GetEnvironmentVariable("MDNOTE_MONKEY_HIDE");
        if (!String.IsNullOrEmpty(hide)) {
            bool value;
            if (hide == "1" || String.Equals(hide, "true", StringComparison.OrdinalIgnoreCase)) { remainingHide = true; reducedRun = true; }
            else if (hide == "0" || String.Equals(hide, "false", StringComparison.OrdinalIgnoreCase)) { remainingHide = false; reducedRun = true; }
        }
    }
    static bool? remainingHide;

    // ---------------------------------------------------------------- seeds
    static bool RunSeed(int seed, StringBuilder report) {
        var failures = new List<Failure>();
        var slowest = new List<TimedAction>();
        string generated = Path.Combine(outRoot, "monkey-generated-" + seed.ToString(CultureInfo.InvariantCulture) + ".md");
        MainWindow window = null;
        string surface = "(not opened)";
        string finalText = "";
        int actions = 0, undoChecks = 0, deepestUndo = 0;
        long foldGuards = MarkdownLivePreview.DiagFoldGuards, foldRecoveries = MarkdownLivePreview.DiagFoldRecoveries;
        currentSeed = seed;
        currentStep = 0;
        currentAction = "seed " + seed.ToString(CultureInfo.InvariantCulture);
        dispatcherFailed = false;
        reproWritten = false;
        lastActions.Clear();
        try {
            File.WriteAllText(generated, MonkeyDocument(seed), new UTF8Encoding(false));
            window = CreateWindow();
            currentWindow = window;
            Invoke(window, "LoadFileCore", generated);
            Pump();
            Invoke(window, "SetMode", true);
            Pump();
            Invoke(window, "FlushDocumentChanges");
            bool code = Prop<bool>(window, "CodeMode");
            bool editing = Field<bool>(window, "editing");
            bool attached = Prop<bool>(window, "LivePreviewAttached");
            surface = "code=" + code + ", editing=" + editing + ", attached=" + attached +
                ", live=" + (!Static<bool>("hideMarkers")) + ", hide=" + Static<bool>("hideMarkers");
            if (!code || !editing || !attached) {
                AddFailure(failures, 0, "open", "the live edit surface did not attach: " + surface);
            } else {
                RunActions(window, seed, failures, slowest, ref actions, ref undoChecks, ref deepestUndo);
            }
            Invoke(window, "FlushDocumentChanges");
            string text = Prop<string>(window, "text");
            finalText = text == null ? "" : text;
            // The fold repairs (visual line guard, layout recovery) keep the app from crashing, but a run
            // that needed one still has a hole in the fold bookkeeping: report it instead of hiding it.
            if (MarkdownLivePreview.DiagFoldGuards != foldGuards || MarkdownLivePreview.DiagFoldRecoveries != foldRecoveries)
                AddFailure(failures, actions, "fold.guard", "a collapsed-line repair ran: guards=" + (MarkdownLivePreview.DiagFoldGuards - foldGuards) +
                    ", recoveries=" + (MarkdownLivePreview.DiagFoldRecoveries - foldRecoveries));
        } catch (Exception ex) {
            AddFailure(failures, actions, "seed", OneLine(ex.ToString()));
        } finally {
            currentWindow = null;
            CloseWindow(window);
        }
        bool ok = failures.Count == 0 && !dispatcherFailed;
        report.Append("\n## seed ").Append(seed).Append('\n');
        report.Append("- surface: ").Append(surface).Append('\n');
        report.Append("- actions: ").Append(actions).Append('\n');
        report.Append("- final length: ").Append(finalText.Length).Append('\n');
        report.Append("- undo checks: ").Append(undoChecks).Append(", deepest single undo: ").Append(deepestUndo).Append('\n');
        if (dispatcherFailed) report.Append("- dispatcher: an unhandled exception surfaced (see the top section)\n");
        if (failures.Count == 0) {
            report.Append("- failures: 0\n");
        } else {
            report.Append("- failures: ").Append(failures.Count).Append('\n');
            foreach (Failure failure in failures) {
                report.Append("  - step ").Append(failure.Step).Append(' ').Append(failure.Action).Append(": ").Append(failure.Error).Append('\n');
                report.Append("    last 20: ").Append(failure.LastActions).Append('\n');
            }
        }
        report.Append("- slowest:\n");
        if (slowest.Count == 0) {
            report.Append("  - (none)\n");
        } else {
            foreach (TimedAction timed in slowest) {
                report.Append("  - ").Append(timed.Milliseconds).Append(" ms step ").Append(timed.Step).Append(' ').Append(timed.Name).Append('\n');
            }
        }
        return ok;
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
    // ---------------------------------------------------------------- focused checks
    // The reported crash of 2026-10-31 (monkey report, seed 8 undo): a caret moved inside a
    // caret-change notification on a rendered table row left VisualColumn = -1 marked valid
    // while AvalonEdit's own column revalidation was still running, and the next caret rectangle
    // threw ArgumentOutOfRangeException("visualColumn") from RestoreHistoryCore. The sequence
    // below drives those paths deterministically on the live surface: row starts, escaped pipes
    // and cell edges are assigned as offsets, the caret rectangle is measured after each one, and
    // the app's undo/redo runs over a table edit.
    static bool FocusedChecks(StringBuilder report) {
        var failures = new List<Failure>();
        MainWindow window = null;
        bool oldHide = Static<bool>("hideMarkers");
        int stops = 0;
        dispatcherFailed = false;
        currentSeed = 0;
        currentStep = 0;
        currentAction = "focused";
        try {
            Static("hideMarkers", false);       // live view: the caret line draws its source
            window = CreateWindow();
            string path = Path.Combine(outRoot, "monkey-generated-focused.md");
            File.WriteAllText(path, "| a | b |\r\n| --- | --- |\r\n| c | d |\r\n", new UTF8Encoding(false));
            Invoke(window, "LoadFileCore", path);
            Pump();
            Invoke(window, "SetMode", true);
            Pump();
            Invoke(window, "FlushDocumentChanges");
            SourceEditor source = Source(window);
            if (source == null) throw new InvalidOperationException("the focused live window has no source editor");
            if (!Field<bool>(window, "editing") || !Prop<bool>(window, "LivePreviewAttached"))
                throw new InvalidOperationException("the focused window did not open the live edit surface");
            string table = "| 가 | 나 |\r\n| --- | --- |\r\n| a\\|b | c |\r\n| d | e |\r\n\r\ntail";
            Invoke(window, "SetText", table);
            Pump();
            source = Source(window);
            MarkdownLivePreview.Session session = Session(window);
            DocumentLine row = source.Document.GetLineByNumber(3);
            MarkdownLivePreview.LineInfo rowInfo = session.Info(row);
            if (rowInfo == null || rowInfo.Cells == null || rowInfo.Cells.Count == 0)
                throw new InvalidOperationException("the table row did not parse: [" + OneLine(source.Document.Text) + "]");
            // A table edit first, so the history has a caret to restore in the undo pass.
            source.Select(row.Offset + rowInfo.Cells[0].End, 0);
            Pump();
            source.TextArea.PerformTextInput("Z");
            Pump();
            // Rebuild the list after the edit: the row grew by one character.
            source = Source(window);
            session = Session(window);
            var offsets = new List<int>();
            foreach (DocumentLine line in source.Document.Lines) {
                MarkdownLivePreview.LineInfo info = session.Info(line);
                if (info == null || !info.Table || info.Delimiter || info.Cells == null || info.Cells.Count == 0) continue;
                offsets.Add(line.Offset);
                offsets.Add(line.EndOffset);
                for (int i = 0; i + 1 < info.Text.Length; i++)
                    if (info.Text[i] == '\\' && info.Text[i + 1] == '|') offsets.Add(line.Offset + i + 1);
                foreach (MarkdownLivePreview.Cell cell in info.Cells) {
                    offsets.Add(line.Offset + cell.Start);
                    offsets.Add(line.Offset + cell.End);
                }
            }
            stops = offsets.Count;
            for (int i = 0; i < offsets.Count; i++) {
                source.Select(Math.Max(0, Math.Min(offsets[i], source.Document.TextLength)), 0);
                Pump();
                source.TextArea.Caret.BringCaretToView();
                Pump();
            }
            for (int pass = 0; pass < 2; pass++) {
                Invoke(window, "Undo");
                Pump();
                source.TextArea.Caret.BringCaretToView();
                Pump();
                Invoke(window, "Redo");
                Pump();
                source.TextArea.Caret.BringCaretToView();
                Pump();
            }
            CheckInvariants(window, stops, failures);
        } catch (Exception ex) {
            AddFailure(failures, 0, "focused:table-caret", OneLine(ex.ToString()));
        } finally {
            CloseWindow(window);
            Static("hideMarkers", oldHide);
        }
        report.Append("\n## focused checks\n");
        report.Append("- live table caret stops: ").Append(stops).Append('\n');
        report.Append("- table + caret moves + undo/redo: ").Append(failures.Count == 0 && !dispatcherFailed ? "0 failures" : "FAIL").Append('\n');
        foreach (Failure failure in failures) report.Append("  - ").Append(failure.Action).Append(": ").Append(failure.Error).Append('\n');
        if (dispatcherFailed) report.Append("  - dispatcher: an unhandled exception surfaced during the focused checks\n");
        return failures.Count == 0 && !dispatcherFailed;
    }
    // ---------------------------------------------------------------- action loop
    static void RunActions(MainWindow window, int seed, List<Failure> failures, List<TimedAction> slowest, ref int actions, ref int undoChecks, ref int deepestUndo) {
        var random = new Random(seed * 104729 + 17);
        DocumentHistory history = HistoryOf(window);
        int segments = (ActionsPerSeed + SegmentLength - 1) / SegmentLength;
        for (int segment = 0; segment < segments; segment++) {
            history.Clear();
            Invoke(window, "FlushDocumentChanges");
            Pump();
            string segmentStart = Prop<string>(window, "text");
            if (segmentStart == null) segmentStart = "";
            bool complete = true;
            for (int index = 0; index < SegmentLength; index++) {
                // A reduced run stops on the requested step; the segment's own undo check only
                // runs for a segment that ran in full, so the state matches the full pass.
                if (actions >= ActionsPerSeed) { complete = false; break; }
                actions++;
                DoAction(window, random, actions, failures, slowest);
                if (failures.Count > 0 || dispatcherFailed) return;
                if (index + 1 < SegmentLength && actions % InvariantInterval == 0) {
                    CheckInvariants(window, actions, failures);
                    if (failures.Count > 0 || dispatcherFailed) return;
                }
            }
            if (!complete) return;
            CheckInvariants(window, actions, failures);
            if (failures.Count > 0 || dispatcherFailed) return;
            CheckUndoOracle(window, segmentStart, actions, failures, ref undoChecks, ref deepestUndo);
            if (failures.Count > 0 || dispatcherFailed) return;
        }
    }
    // One weighted action. The name is chosen before the work so an exception and the dispatcher
    // hook always know which action was running; the duration covers the action plus the dispatcher
    // drain that follows it (layout and render included).
    static void DoAction(MainWindow window, Random random, int step, List<Failure> failures, List<TimedAction> slowest) {
        int roll = random.Next(TotalWeight);
        string name;
        int command = 0;
        string payload = null;
        if (roll < 25) { name = "type"; payload = TypingText(random); }
        else if (roll < 33) name = "enter";
        else if (roll < 43) name = "backspace";
        else if (roll < 49) name = "delete";
        else if (roll < 64) { command = random.Next(NavigationNames.Length); name = "nav:" + NavigationNames[command]; }
        else if (roll < 72) name = "caret";
        else if (roll < 78) name = "select";
        else if (roll < 90) { command = random.Next(CommandNames.Length); name = "cmd:" + CommandNames[command]; }
        else if (roll < 95) { command = random.Next(4); name = "table:" + TableNames[command]; }
        else if (roll < 98) { name = "paste"; payload = PasteSnippet(random); }
        else if (roll < 102) name = random.Next(2) == 0 ? "undo" : "redo";
        else if (roll < 103) name = "mode";
        else if (roll < 104) name = "txt";
        else name = "find";
        currentAction = name;
        currentStep = step;
        lastActions.Add(step.ToString(CultureInfo.InvariantCulture) + " " + name);
        if (lastActions.Count > 20) lastActions.RemoveAt(0);
        if (reducedRun) CapturePreAction(window);
        var watch = Stopwatch.StartNew();
        Exception error = null;
        try {
            Execute(window, random, name, command, payload);
            Pump();
        } catch (Exception ex) {
            error = ex;
        }
        watch.Stop();
        if (error != null) {
            // The top frames name the method that read past the document; the message alone
            // (type + range) cannot tell two such readers apart.
            AddFailure(failures, step, name, OneLine(error.GetType().Name + ": " + error.Message) + " @ " + TopFrames(error, 10));
            WriteReproDump(step, name, error.GetType().Name + ": " + error.Message);
            return;
        }
        if (dispatcherFailed) WriteReproDump(step, name, "dispatcher exception");
        NoteSlow(slowest, watch.ElapsedMilliseconds, name, step);
        if (watch.ElapsedMilliseconds > SlowActionLimit) {
            AddFailure(failures, step, name, "action took " + watch.ElapsedMilliseconds + " ms (limit " + SlowActionLimit + ")");
        }
    }
    static void Execute(MainWindow window, Random random, string name, int command, string payload) {
        if (name == "type") { Source(window).TextArea.PerformTextInput(payload); return; }
        if (name == "enter") { Enter(window); return; }
        if (name == "backspace") { BackspaceAction(window); return; }
        if (name == "delete") { DeleteAction(window); return; }
        if (name.StartsWith("nav:", StringComparison.Ordinal)) { NavigationCommand(command).Execute(null, Source(window).TextArea); return; }
        if (name == "caret") {
            SourceEditor source = Source(window);
            int length = source.Document.TextLength;
            source.Select(length == 0 ? 0 : random.Next(length + 1), 0);
            return;
        }
        if (name == "select") {
            SourceEditor source = Source(window);
            int length = source.Document.TextLength;
            int start = length == 0 ? 0 : random.Next(length + 1);
            int room = length - start;
            source.Select(start, room == 0 ? 0 : random.Next(room + 1));
            return;
        }
        if (name.StartsWith("cmd:", StringComparison.Ordinal)) { ExecuteCommand(window, random, command); return; }
        if (name.StartsWith("table:", StringComparison.Ordinal)) { ExecuteTable(window, random, command); return; }
        if (name == "paste") { Invoke(window, "LivePaste", new DataObject(), true, payload); return; }
        if (name == "undo") { Invoke(window, "Undo"); return; }
        if (name == "redo") { Invoke(window, "Redo"); return; }
        if (name == "mode") { Invoke(window, "SetLiveMode", Static<bool>("hideMarkers")); return; }
        if (name == "txt") {
            bool before = Prop<bool>(window, "PlainText");
            WaitTextModeIdle(window);
            Invoke(window, "ToggleTextMode");
            WaitTextMode(window, !before);
            WaitTextModeIdle(window);
            Invoke(window, "ToggleTextMode");
            WaitTextMode(window, before);
            if (Prop<bool>(window, "PlainText") != before) throw new InvalidOperationException("the TXT toggle did not return to Markdown");
            return;
        }
        FindAction(window, random);
    }
    // ---------------------------------------------------------------- keys
    // The window's own Enter path: SourceStructuralKeys runs SourceEnter and applies its result; a
    // null result (plain line) falls to AvalonEdit's newline input, exactly like the real key.
    static void Enter(MainWindow window) {
        if (Prop<bool>(window, "LiveFormat")) {
            CommandResult result = (CommandResult)Invoke(window, "SourceEnter");
            if (result == null) { Source(window).TextArea.PerformTextInput("\n"); return; }
            Invoke(window, "ApplyCommandResult", result, -1, 0);
            return;
        }
        Source(window).TextArea.PerformTextInput("\n");
    }
    // Backspace/Delete first see the TextArea's PreviewKeyDown (SourceStructuralKeys and the live
    // session's key input), then the vendor command when the app declined - the same split the
    // keys take, assembled without SendInput.
    static void BackspaceAction(MainWindow window) {
        if (RaisePreviewKey(window, Key.Back)) return;
        SourceEditor source = Source(window);
        if (source != null) EditingCommands.Backspace.Execute(null, source.TextArea);
    }
    static void DeleteAction(MainWindow window) {
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
    // ---------------------------------------------------------------- invariants
    // b: the editor's document and the window's text mirror stay identical. d: in the Basic view
    // (hideMarkers on) the caret line must not draw markup the parser recognised. A balanced span
    // tag the line still shows is a parse/render fault; an unterminated tag is literal source text a
    // user can type, so only the balanced shape fails. The same rule applies to the hidden "**" of
    // a recognised pair: a literal "**" the parser did not pair is not markup loss.
    static void CheckInvariants(MainWindow window, int step, List<Failure> failures) {
        try {
            Invoke(window, "FlushDocumentChanges");
            SourceEditor source = Source(window);
            if (source == null) { AddFailure(failures, step, "invariant:text", "the source editor is gone"); return; }
            string text = Prop<string>(window, "text");
            int length = source.Document.TextLength;
            if (text == null || text.Length != length || !String.Equals(text, source.Document.Text, StringComparison.Ordinal)) {
                AddFailure(failures, step, "invariant:text", "text=" + (text == null ? -1 : text.Length) + ", document=" + length +
                    (text == null ? "" : ", " + Diff(source.Document.Text, text)));
                return;
            }
            if (Static<bool>("hideMarkers")) CheckCaretLine(window, source, step, failures);
        } catch (Exception ex) {
            AddFailure(failures, step, "invariant:exception", OneLine(ex.ToString()));
        }
    }
    static void CheckCaretLine(MainWindow window, SourceEditor source, int step, List<Failure> failures) {
        if (!Prop<bool>(window, "LivePreviewAttached")) return;
        MarkdownLivePreview.Session session = Session(window);
        if (session == null || session.Options == null || !session.Options.HideMarkers) return;
        var document = source.Document;
        int offset = Math.Max(0, Math.Min(source.CaretIndex, document.TextLength));
        DocumentLine line = document.GetLineByOffset(offset);
        if (session.Raw(line)) {
            // Two documented exceptions draw source in the Basic view: a display-math block under the
            // caret or a broken one (2.2.0 rule), and the caret's own unclosed opening fence
            // (EditableFence, 11-05). Anything else is a stale caret line.
            MarkdownLivePreview.LineInfo rawInfo = session.Info(line);
            bool math = rawInfo != null && (rawInfo.MathCount > 0 || rawInfo.MathRemaining > 0 || rawInfo.MathOne);
            bool editableFence = session.EditableFence(line);
            if (!math && !editableFence) {
                AddFailure(failures, step, "invariant:reveal", "the caret line " + line.LineNumber + " draws raw markup in the Basic view: [" + OneLine(document.GetText(line)) + "]" +
                    (rawInfo == null ? " (no line info)" : " fenceLine=" + rawInfo.FenceLine + " table=" + rawInfo.Table + " code=" + rawInfo.Code +
                    " mathCount=" + rawInfo.MathCount + " mathRemaining=" + rawInfo.MathRemaining + " mathOne=" + rawInfo.MathOne));
            }
            return;
        }
        MarkdownLivePreview.LineInfo info = session.Info(line);
        if (info == null || info.Text == null) return;
        string visible = VisibleLine(info);
        string sourceLine = document.GetText(line);
        bool balancedSpan = CountOf(sourceLine, "<span") > 0 && CountOf(sourceLine, "<span") == CountOf(sourceLine, "</span");
        bool recognisedTag = false;
        int hiddenPairs = 0;
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Kind != MarkdownLivePreview.ReplacementKind.Hidden) continue;
            if (replacement.Start < 0 || replacement.Start + replacement.Length > info.Text.Length) continue;
            if (replacement.Length >= 5 && !recognisedTag) {
                string run = info.Text.Substring(replacement.Start, Math.Min(replacement.Length, 6));
                if (run.StartsWith("<span", StringComparison.OrdinalIgnoreCase) || run.StartsWith("</span", StringComparison.OrdinalIgnoreCase)) recognisedTag = true;
            }
            if (replacement.Length != 2) continue;
            if (String.CompareOrdinal(info.Text, replacement.Start, "**", 0, 2) == 0) hiddenPairs++;
        }
        if (balancedSpan && recognisedTag &&
            (visible.IndexOf("<span", StringComparison.OrdinalIgnoreCase) >= 0 || visible.IndexOf("</span", StringComparison.OrdinalIgnoreCase) >= 0)) {
            AddFailure(failures, step, "invariant:span", "the caret line still shows a recognised span tag: [" + OneLine(visible) + "] source=[" + OneLine(sourceLine) + "]");
            return;
        }
        if (hiddenPairs >= 2 && visible.IndexOf("**", StringComparison.Ordinal) >= 0) {
            AddFailure(failures, step, "invariant:bold", "the caret line still shows the ** of a recognised pair: [" + OneLine(visible) + "]");
        }
    }
    // c: undo all the way back to the segment's start, then redo exactly that many steps back to
    // the text the undo began with. The monkey's own undo action can leave a redo stack of entries
    // that were already undone before this check; replaying those would walk past the end state,
    // so the redo count is pinned to the undo count instead of "until CanRedo is false". A stall
    // detector stops a history that answers CanUndo but applies nothing.
    static void CheckUndoOracle(MainWindow window, string segmentStart, int step, List<Failure> failures, ref int checks, ref int deepest) {
        checks++;
        try {
            Invoke(window, "FlushDocumentChanges");
            string beforeUndo = Prop<string>(window, "text");
            DocumentHistory history = HistoryOf(window);
            bool hadRedo = history.CanRedo;
            int undoSteps = 0, stalls = 0;
            string previous = beforeUndo;
            while (history.CanUndo && undoSteps < UndoCap) {
                // A mode step (the TXT toggle's own entry) flips the text mode and may leave the
                // text as it was: the app keeps it as a step of its own on purpose, so it is
                // progress, not a stall. Only an ordinary entry that applies nothing is one.
                bool modeStep = history.UndoMode.HasValue;
                string top = DescribeTopEntry(history, false);
                Invoke(window, "Undo");
                undoSteps++;
                string now = Prop<string>(window, "text");
                if (!modeStep && String.Equals(now, previous, StringComparison.Ordinal)) {
                    stalls++;
                    if (stalls >= 3) { AddFailure(failures, step, "undo.stall", "undo did not change the text " + stalls + " times at step " + undoSteps + "; top entry " + top + ", can undo=" + history.CanUndo); break; }
                } else stalls = 0;
                previous = now;
            }
            Invoke(window, "FlushDocumentChanges");
            string undone = Prop<string>(window, "text");
            int redoSteps = 0;
            stalls = 0;
            string beforeRedo = undone;
            while (redoSteps < undoSteps && redoSteps < UndoCap) {
                bool modeStep = history.RedoMode.HasValue;
                string top = DescribeTopEntry(history, true);
                Invoke(window, "Redo");
                redoSteps++;
                string now = Prop<string>(window, "text");
                if (!modeStep && String.Equals(now, beforeRedo, StringComparison.Ordinal)) {
                    stalls++;
                    if (stalls >= 3) { AddFailure(failures, step, "redo.stall", "redo did not change the text " + stalls + " times at step " + redoSteps + "; top entry " + top + ", can redo=" + history.CanRedo); break; }
                } else stalls = 0;
                beforeRedo = now;
            }
            Invoke(window, "FlushDocumentChanges");
            string redone = Prop<string>(window, "text");
            if (undoSteps > deepest) deepest = undoSteps;
            if (!String.Equals(undone, segmentStart, StringComparison.Ordinal)) AddFailure(failures, step, "undo.all", "undid " + undoSteps + " steps; " + Diff(segmentStart, undone));
            if (redoSteps != undoSteps) AddFailure(failures, step, "redo.count", "redo=" + redoSteps + ", undo=" + undoSteps);
            if (!hadRedo && history.CanRedo) AddFailure(failures, step, "redo.left", "redo entries remain after replaying every undo step");
            if (!String.Equals(redone, beforeUndo, StringComparison.Ordinal)) AddFailure(failures, step, "redo.all", "redid " + redoSteps + " steps; " + Diff(beforeUndo, redone));
        } catch (Exception ex) {
            AddFailure(failures, step, "undo.exception", OneLine(ex.ToString()));
        }
    }
    // The newest entry of the undo (or redo) stack as a short text, so a stall names what the history
    // kept: a mode step, a compound entry or a plain replacement of the given lengths.
    static string DescribeTopEntry(DocumentHistory history, bool redo) {
        try {
            FieldInfo field = typeof(DocumentHistory).GetField(redo ? "redo" : "undo", BindingFlags.Instance | BindingFlags.NonPublic);
            System.Collections.IList list = field == null ? null : field.GetValue(history) as System.Collections.IList;
            if (list == null || list.Count == 0) return "(empty)";
            DocumentHistory.Edit edit = list[list.Count - 1] as DocumentHistory.Edit;
            if (edit == null) return "(unknown)";
            if (edit.BeforeMode.HasValue || edit.AfterMode.HasValue) return "mode";
            if (edit.Parts != null) return "parts=" + edit.Parts.Count;
            return "text at=" + edit.At + " removed=" + (edit.Removed == null ? -1 : edit.Removed.Length) + " inserted=" + (edit.Inserted == null ? -1 : edit.Inserted.Length) +
                (edit.Removed != null && String.Equals(edit.Removed, edit.Inserted, StringComparison.Ordinal) ? " (same text)" : "");
        } catch (Exception) { return "(unreadable)"; }
    }
    // ---------------------------------------------------------------- failure and report helpers
    static void CapturePreAction(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) { preActionText = null; preActionCaret = -1; preActionLine = 0; return; }
        preActionText = source.Document.Text;
        preActionCaret = Math.Max(0, Math.Min(source.CaretIndex, preActionText.Length));
        preActionLine = source.Document.GetLineByOffset(preActionCaret).LineNumber;
        preActionSelectionStart = source.SelectionStart;
        preActionSelectionLength = source.SelectionLength;
        try {
            preActionLiveFormat = Prop<bool>(window, "LiveFormat");
            preActionAttached = Prop<bool>(window, "LivePreviewAttached");
        } catch (Exception) { }
    }
    // The document and caret the failing action started from, plus the exception and the
    // dispatcher notes; RenderFixProbe replays exactly this state.
    static void WriteReproDump(int step, string action, string error) {
        if (!reducedRun || reproWritten || preActionText == null|| outRoot == null) return;
        reproWritten = true;
        try {
            File.WriteAllText(Path.Combine(outRoot, "monkey-repro-doc.md"), preActionText, new UTF8Encoding(false));
            SourceEditor after = currentWindow == null ? null : Source(currentWindow);
            string afterText = after == null || after.Document == null ? "(none)" : after.Document.Text;
            File.WriteAllText(Path.Combine(outRoot, "monkey-repro-after.md"), afterText, new UTF8Encoding(false));
            var info = new StringBuilder();
            info.Append("seed ").Append(currentSeed).Append(" step ").Append(step).Append(" action ").Append(action).Append('\n');
            info.Append("caret ").Append(preActionCaret).Append(" line ").Append(preActionLine).Append('\n');
            info.Append("selection ").Append(preActionSelectionStart).Append('+').Append(preActionSelectionLength)
                .Append(" liveFormat=").Append(preActionLiveFormat).Append(" attached=").Append(preActionAttached).Append('\n');
            if (currentWindow != null && Source(currentWindow) != null && Source(currentWindow).Document != null) {
                SourceEditor live = Source(currentWindow);
                info.Append("after caret ").Append(live.CaretIndex).Append(" selection ").Append(live.SelectionStart).Append('+').Append(live.SelectionLength).Append('\n');
            }
            info.Append("last actions: ").Append(LastActionsText()).Append('\n');
            info.Append("error ").Append(OneLine(error)).Append('\n');
            info.Append("dispatcher:\n");
            foreach (string note in dispatcherNotes) info.Append("  ").Append(note).Append('\n');
            string state;
            try { state = DescribeLive(); } catch (Exception describe) { state = "(describe failed: " + OneLine(describe.GetType().Name + ": " + describe.Message) + ")"; }
            info.Append("state after: ").Append(state).Append('\n');
            File.WriteAllText(reproNotePath, info.ToString(), new UTF8Encoding(false));
        } catch (Exception) { }
    }
    // Diagnosis of a reduced run: the height tree's collapsed lines, the built visual lines and
    // the fold registries at the moment the failure was seen.
    static MainWindow currentWindow;
    static object HeightTreeOf(MarkdownLivePreview.Session live) {
        FieldInfo field = typeof(ICSharpCode.AvalonEdit.Rendering.TextView).GetField("heightTree", BindingFlags.Instance | BindingFlags.NonPublic);
        return field == null || live == null ? null : field.GetValue(live.View);
    }
    static bool CollapsedAt(object heightTree, int number) {
        if (heightTree == null) return false;
        MethodInfo method = heightTree.GetType().GetMethod("GetIsCollapsed", BindingFlags.Instance | BindingFlags.Public);
        return method != null && (bool)method.Invoke(heightTree, new object[] { number });
    }
    static string DescribeLive() {
        MainWindow window = currentWindow;
        if (window == null) return "(no window)";
        SourceEditor source = Source(window);
        MarkdownLivePreview.Session live = Session(window);
        if (source == null || live == null) return "(no session)";
        var document = source.Document;
        var text = new StringBuilder();
        text.Append("visualLinesValid=").Append(live.View.VisualLinesValid).Append(", caret=").Append(source.CaretIndex).Append(", lines=").Append(document.LineCount);
        object heightTree = HeightTreeOf(live);
        bool valid = live.View.VisualLinesValid;
        IList<ICSharpCode.AvalonEdit.Rendering.VisualLine> visuals = valid ? live.View.VisualLines : null;
        text.Append("\n  collapsed:");
        for (int number = 1; number <= document.LineCount; number++) {
            if (!CollapsedAt(heightTree, number)) continue;
            string span = valid ? "none" : "(invalid)";
            if (visuals != null) foreach (ICSharpCode.AvalonEdit.Rendering.VisualLine visual in visuals) {
                if (visual.FirstDocumentLine.LineNumber <= number && number <= visual.LastDocumentLine.LineNumber) {
                    span = visual.FirstDocumentLine.LineNumber + ".." + visual.LastDocumentLine.LineNumber;
                }
            }
            text.Append(' ').Append(number).Append("(visual ").Append(span).Append(')');
        }
        text.Append("\n  visual:");
        if (visuals != null) foreach (ICSharpCode.AvalonEdit.Rendering.VisualLine visual in visuals) {
            text.Append(' ').Append(visual.FirstDocumentLine.LineNumber).Append("..").Append(visual.LastDocumentLine.LineNumber);
        }
        text.Append("\n  table-info:");
        for (int number = 1; number <= document.LineCount; number++) {
            MarkdownLivePreview.LineInfo info = live.Info(document.GetLineByNumber(number));
            if (info == null || !(info.Table || info.Header || info.Delimiter || info.SetextUnderline > 0)) continue;
            text.Append(' ').Append(number).Append('[').Append(info.Header ? 'H' : '-').Append(info.Delimiter ? 'D' : '-').Append(info.SetextUnderline > 0 ? 'S' : '-').Append(info.Row != null ? 'R' : '-').Append(']');
        }
        FieldInfo delimiters = typeof(MarkdownLivePreview.Session).GetField("delimiterSections", BindingFlags.Instance | BindingFlags.NonPublic);
        var map = delimiters == null ? null : delimiters.GetValue(live) as System.Collections.IDictionary;
        text.Append("\n  delimiterSections:");
        if (map != null) foreach (System.Collections.DictionaryEntry entry in map) {
            var key = (DocumentLine)entry.Key;
            var section = (ICSharpCode.AvalonEdit.Rendering.CollapsedLineSection)entry.Value;
            text.Append(" header=").Append(key.LineNumber);
            text.Append(section.IsCollapsed ? "=" + section.Start.LineNumber + ".." + section.End.LineNumber : "=open");
        }
        FieldInfo references = typeof(MarkdownLivePreview.Session).GetField("referenceSections", BindingFlags.Instance | BindingFlags.NonPublic);
        var sections = references == null ? null : references.GetValue(live) as System.Collections.IList;
        text.Append("\n  referenceSections:");
        if (sections != null) foreach (object item in sections) {
            var section = (ICSharpCode.AvalonEdit.Rendering.CollapsedLineSection)item;
            text.Append(section.IsCollapsed ? " " + section.Start.LineNumber + ".." + section.End.LineNumber : " open");
        }
        return text.ToString();
    }
    static void AddFailure(List<Failure> failures, int step, string action, string error) {
        var failure = new Failure();
        failure.Step = step;
        failure.Action = action;
        failure.Error = OneLine(error);
        failure.LastActions = LastActionsText();
        failures.Add(failure);
    }
    static void NoteSlow(List<TimedAction> slowest, long milliseconds, string name, int step) {
        var timed = new TimedAction();
        timed.Milliseconds = milliseconds;
        timed.Name = name;
        timed.Step = step;
        slowest.Add(timed);
        slowest.Sort(delegate(TimedAction left, TimedAction right) { return right.Milliseconds.CompareTo(left.Milliseconds); });
        if (slowest.Count > 10) slowest.RemoveAt(slowest.Count - 1);
    }
    static string LastActionsText() {
        if (lastActions.Count == 0) return "(none)";
        var builder = new StringBuilder();
        for (int i = 0; i < lastActions.Count; i++) {
            if (i > 0) builder.Append(" | ");
            builder.Append(lastActions[i]);
        }
        return builder.ToString();
    }
    static string OneLine(string value) {
        if (value == null) return "<null>";
        string text = value.Replace("\r\n", "\\n").Replace("\r", "\\r").Replace("\n", "\\n");
        return text.Length <= 2400 ? text : text.Substring(0, 2400) + "...";
    }
    static string TopFrames(Exception error, int count) {
        Exception root = error;
        while (root.InnerException != null) root = root.InnerException;
        string stack = root.StackTrace;
        if (stack == null) return "<no stack>";
        string[] lines = stack.Replace("\r\n", "\n").Split('\n');
        var top = new StringBuilder();
        for (int i = 0; i < lines.Length && i < count; i++) top.Append(lines[i].Trim()).Append(" | ");
        return top.ToString();
    }
    static string Diff(string want, string got) {
        if (want == null) want = "<null>";
        if (got == null) got = "<null>";
        int at = 0, limit = Math.Min(want.Length, got.Length);
        while (at < limit && want[at] == got[at]) at++;
        return "firstDiff=" + at + ", want=[" + Context(want, at) + "], got=[" + Context(got, at) + "]";
    }
    static string Context(string value, int at) {
        int start = Math.Max(0, at - 15), end = Math.Min(value.Length, at + 15);
        return OneLine(value.Substring(start, end - start));
    }
    static void NoteDispatcherException(DispatcherUnhandledExceptionEventArgs args) {
        // The same dispatcher is hooked twice (Dispatcher.UnhandledException and
        // Application.DispatcherUnhandledException), so one exception reaches this twice.
        args.Handled = true;
        if (dispatcherFailed) return;
        dispatcherFailed = true;
        if (dispatcherNotes.Count >= 20) return;
        string error = args.Exception == null ? "unknown" : args.Exception.GetType().Name + ": " + args.Exception.Message;
        if (args.Exception != null) {
            // Keep the stack (trimmed) even in a full run: the frames name the callback that
            // built the visual line, which a message alone never shows.
            string stack = OneLine(args.Exception.ToString());
            if (stack.Length > 1200) stack = stack.Substring(0, 1200) + "...";
            error = error + " @ " + stack;
        }
        dispatcherNotes.Add("seed " + currentSeed + " step " + currentStep + " " + currentAction + ": " + OneLine(error));
    }
    static void DeleteGenerated() {
        try {
            if (outRoot == null) return;
            foreach (string file in Directory.GetFiles(outRoot, "monkey-generated-*.md")) {
                try { File.Delete(file); } catch (IOException) { }
            }
        } catch (Exception) { }
    }
    // ---------------------------------------------------------------- dispatcher and reflection
    static void Pump() {
        LongLineBench.Idle();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
        LongLineBench.Idle();
    }
    // A mode toggle is dropped while a queued open/conversion owns the document, and a large body
    // converts asynchronously: wait for both before asking for the next state.
    static void WaitTextModeIdle(MainWindow window) {
        for (int i = 0; i < 400 && (Field<bool>(window, "documentOperationBusy") || Field<bool>(window, "fileOpenRunning")); i++) {
            Pump(); System.Threading.Thread.Sleep(5);
        }
    }
    static void WaitTextMode(MainWindow window, bool plain) {
        for (int i = 0; i < 800 && Prop<bool>(window, "PlainText") != plain; i++) {
            Pump(); System.Threading.Thread.Sleep(5);
        }
    }
    static SourceEditor Source(MainWindow window) { return Field<SourceEditor>(window, "source"); }
    static MarkdownLivePreview.Session Session(MainWindow window) { return Field<MarkdownLivePreview.Session>(window, "livePreview"); }
    static DocumentHistory HistoryOf(MainWindow window) {
        return (DocumentHistory)Prop<DocumentHistory>(window, "History");
    }
    static FieldInfo RequireField(string name) {
        FieldInfo field = typeof(MainWindow).GetField(name, Members);
        if (field == null) throw new MissingMemberException("probe: MainWindow has no field '" + name + "'");
        return field;
    }
    static T Field<T>(MainWindow window, string name) {
        return (T)RequireField(name).GetValue(window);
    }
    static void Set(MainWindow window, string name, object value) {
        RequireField(name).SetValue(window, value);
    }
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
    static T Static<T>(string name) {
        return (T)RequireField(name).GetValue(null);
    }
    static void Static(string name, object value) {
        RequireField(name).SetValue(null, value);
    }
    // ---------------------------------------------------------------- actions
    static readonly Color[] ColorChoices = new Color[] {
        Color.FromRgb(0xC0, 0x30, 0x30), Color.FromRgb(0x00, 0x00, 0xFF),
        Color.FromRgb(0x2E, 0x7D, 0x32), Color.FromRgb(0x8A, 0x2B, 0xE2)
    };
    static readonly string[] CommandNames = new string[] {
        "bold", "italic", "strike", "inline-code", "h0", "h1", "h2", "h3", "h4", "h5", "h6",
        "bullet", "numbered", "task", "quote", "code-block", "link", "color", "size", "clear-color"
    };
    static readonly string[] TableNames = new string[] { "insert", "row", "column", "tab" };
    // Toolbar commands go through the same entry points the buttons use (ToggleFormat, SetHeading,
    // ToggleListStyle, ToggleTaskList, Quote, ToggleCodeBlock, SetFontSize, SetTextColor,
    // ClearTextColor). The link button opens a modal URL dialog, so its post-dialog half is driven
    // through RunLiveCommand + MarkdownCommands.InsertLink exactly as LiveLink does.
    static void ExecuteCommand(MainWindow window, Random random, int index) {
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
            case 16: InsertLink(window); return;
            case 17: Invoke(window, "SetTextColor", ColorChoices[random.Next(ColorChoices.Length)]); return;
            case 18: Invoke(window, "SetFontSize", (double)(12 + random.Next(21))); return;
            default: Invoke(window, "ClearTextColor"); return;
        }
    }
    static void InsertLink(MainWindow window) {
        Func<TextDocument, int, int, CommandResult> command = delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertLink(document, start, length, "https://example.invalid/monkey");
        };
        Invoke(window, "RunLiveCommand", command);
    }
    // The toolbar table commands: InsertTable counts the header row (2..5 columns, 2..4 rows), and
    // EditTable(0..3) adds/removes a row or column at the caret. Tab/Shift+Tab use the probe seam
    // that runs the key handler's SourceTab path (a probe cannot set Keyboard.Modifiers).
    static void ExecuteTable(MainWindow window, Random random, int index) {
        if (index == 0) {
            Invoke(window, "InsertTable", 2 + random.Next(4), 2 + random.Next(3));
            return;
        }
        if (!PlaceCaretInTable(window, random)) {
            Invoke(window, "InsertTable", 3, 3);
            return;
        }
        if (index == 1) { Invoke(window, "EditTable", random.Next(2)); return; }
        if (index == 2) { Invoke(window, "EditTable", 2 + random.Next(2)); return; }
        Invoke(window, "SourceTabPathProbe", random.Next(2) == 0);
    }
    static bool PlaceCaretInTable(MainWindow window, Random random) {
        if (!Prop<bool>(window, "LivePreviewAttached")) return false;
        SourceEditor source = Source(window);
        MarkdownLivePreview.Session session = Session(window);
        if (source == null || session == null) return false;
        var document = source.Document;
        var rows = new List<DocumentLine>();
        foreach (DocumentLine line in document.Lines) {
            MarkdownLivePreview.LineInfo info = session.Info(line);
            if (info == null || !info.Table || info.Delimiter || info.Cells == null || info.Cells.Count == 0) continue;
            rows.Add(line);
        }
        if (rows.Count == 0) return false;
        DocumentLine row = rows[random.Next(rows.Count)];
        MarkdownLivePreview.LineInfo rowInfo = session.Info(row);
        MarkdownLivePreview.Cell cell = rowInfo.Cells[random.Next(rowInfo.Cells.Count)];
        int at = row.Offset + cell.Start + (cell.End - cell.Start) / 2;
        source.Select(Math.Max(0, Math.Min(at, document.TextLength)), 0);
        return true;
    }
    static readonly string[] NavigationNames = new string[] {
        "left", "right", "word-left", "word-right", "up", "down", "page-up", "page-down",
        "home", "end", "doc-start", "doc-end",
        "select-left", "select-right", "select-word-left", "select-word-right",
        "select-up", "select-down", "select-page-up", "select-page-down",
        "select-home", "select-end", "select-doc-start", "select-doc-end"
    };
    // Arrow/Home/End/PageUp/PageDown with and without Shift: the vendor command bindings the real
    // keys use. Shift+arrow cannot be raised as a key here (Keyboard.Modifiers is the physical
    // keyboard), so the Select* command of the same binding is executed instead.
    static RoutedUICommand NavigationCommand(int index) {
        switch (index) {
            case 0: return EditingCommands.MoveLeftByCharacter;
            case 1: return EditingCommands.MoveRightByCharacter;
            case 2: return EditingCommands.MoveLeftByWord;
            case 3: return EditingCommands.MoveRightByWord;
            case 4: return EditingCommands.MoveUpByLine;
            case 5: return EditingCommands.MoveDownByLine;
            case 6: return EditingCommands.MoveUpByPage;
            case 7: return EditingCommands.MoveDownByPage;
            case 8: return EditingCommands.MoveToLineStart;
            case 9: return EditingCommands.MoveToLineEnd;
            case 10: return EditingCommands.MoveToDocumentStart;
            case 11: return EditingCommands.MoveToDocumentEnd;
            case 12: return EditingCommands.SelectLeftByCharacter;
            case 13: return EditingCommands.SelectRightByCharacter;
            case 14: return EditingCommands.SelectLeftByWord;
            case 15: return EditingCommands.SelectRightByWord;
            case 16: return EditingCommands.SelectUpByLine;
            case 17: return EditingCommands.SelectDownByLine;
            case 18: return EditingCommands.SelectUpByPage;
            case 19: return EditingCommands.SelectDownByPage;
            case 20: return EditingCommands.SelectToLineStart;
            case 21: return EditingCommands.SelectToLineEnd;
            case 22: return EditingCommands.SelectToDocumentStart;
            default: return EditingCommands.SelectToDocumentEnd;
        }
    }
    static readonly string KoreanSyllables = "가나다라마바사아자차카타파하거너더러머버서어저처커터퍼허고노도로모보소오조초코토포호구누두루무부수우주추쿠투푸후기니디리미비시이지치키티피히한글테스트문장값메모";
    static readonly string LatinChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.,!?()";
    static readonly string[] TypingWords = new string[] { "문장", "테스트", "값", "메모", "그리고", "끝", "monkey", "note", "row", "alpha" };
    static string TypingText(Random random) {
        int kind = random.Next(10);
        if (kind < 4) {
            int count = 1 + random.Next(4);
            var value = new StringBuilder(count);
            for (int i = 0; i < count; i++) value.Append(KoreanSyllables[random.Next(KoreanSyllables.Length)]);
            return value.ToString();
        }
        if (kind < 7) {
            int count = 1 + random.Next(6);
            var value = new StringBuilder(count);
            for (int i = 0; i < count; i++) value.Append(LatinChars[random.Next(LatinChars.Length)]);
            return value.ToString();
        }
        if (kind < 9) return TypingWords[random.Next(TypingWords.Length)];
        return " ";
    }
    static string PasteSnippet(Random random) {
        switch (random.Next(8)) {
            case 0: return "**굵은 붙여넣기** 문장";
            case 1: return "- 목록 항목\n- 두 번째 항목";
            case 2: return "| 가 | 나 |\n| --- | --- |\n| 다 | 라 |";
            case 3: return "`인라인 코드` 와 *기울임*";
            case 4: return "<span style=\"font-size:18px\">붙인 글자</span>";
            case 5: return "> 인용 붙여넣기";
            case 6: return "여러 줄\n붙여넣기\n문장입니다.";
            default: return "한글 붙여넣기 값 42";
        }
    }
    static readonly string[] Queries = new string[] { "문장", "**", "|", "가", "코드", "테스트", "없는검색어", "42" };
    static void FindAction(MainWindow window, Random random) {
        Invoke(window, "OpenSearch", false);
        Pump();
        TextBox query = Field<TextBox>(window, "query");
        if (query != null) query.Text = Queries[random.Next(Queries.Length)];
        Pump();
        Invoke(window, "CloseSearch");
        Pump();
    }
    // ---------------------------------------------------------------- generated document
    // ~150 lines: headings, styled paragraphs (bold/italic/strike/code/link/image/size and colour
    // spans), nested bullet/numbered/task lists, quotes with a nested list, a fenced code block,
    // 3-6 column tables with long Korean cells and a table indented under a list item.
    static string MonkeyDocument(int seed) {
        var random = new Random(seed * 7919 + 31);
        string newline = seed % 2 == 0 ? "\r\n" : "\n";
        var lines = new List<string>();
        AddAll(lines, new string[] {
            "# 몽키 검증 문서 " + seed.ToString(CultureInfo.InvariantCulture),
            "",
            "## 서식 문단",
            "**굵게** *기울임* ~~취소선~~ `인라인 코드` 그리고 [링크](https://example.invalid/monkey) 끝.",
            "크기 <span style=\"font-size:20px\">큰 글자</span> 와 색 <span style=\"color:#C03030\">빨간 글자</span> 문장.",
            "![그림](guide-check.png)",
            "",
            "## 목록",
            "- 첫 글머리 **굵게**",
            "  - 중첩 항목 `코드`",
            "    - 삼단계 항목",
            "- [ ] 미완료 작업",
            "- [x] 완료 작업",
            "1. 첫 번호",
            "   1. 중첩 번호",
            "   2. 중첩 번호 둘",
            "2. 둘째 번호",
            "",
            "## 인용",
            "> 인용문 첫 줄 **강조**",
            "> - 인용 안 글머리",
            "> 1. 인용 안 번호",
            "",
            "## 코드",
            "```csharp",
            "static int Sum(int a, int b) { return a + b; }",
            "// 한글 주석 값 = 42",
            "```",
            "",
            "## 표",
            "| 이름 | 상태 | 설명 |",
            "| --- | :---: | ---: |",
            "| 아주 긴 한국어 셀 값 하나 | 완료 | 첫째 줄 설명입니다 |",
            "| 둘째 행 값 | 진행 중 | **굵은 셀** 과 `코드` |",
            "",
            "- 표를 품은 목록 항목:",
            "  | 열 하나 | 열 둘 | 열 셋 |",
            "  | --- | --- | --- |",
            "  | 가 | 나 | 다 |",
            ""
        });
        while (lines.Count < 150) {
            string[] block = NextMonkeyBlock(random);
            if (lines.Count + block.Length > 150) { lines.Add(""); continue; }
            AddAll(lines, block);
        }
        var text = new StringBuilder();
        foreach (string line in lines) text.Append(line).Append(newline);
        return text.ToString();
    }
    static void AddAll(List<string> lines, string[] block) {
        for (int i = 0; i < block.Length; i++) lines.Add(block[i]);
    }
    static string[] NextMonkeyBlock(Random random) {
        switch (random.Next(9)) {
            case 0: return new string[] { new string('#', 1 + random.Next(4)) + " " + Phrase(random), "" };
            case 1: return new string[] { Paragraph(random), Paragraph(random) };
            case 2: return BulletBlock(random);
            case 3: return NumberBlock(random);
            case 4: return QuoteBlock(random);
            case 5: return CodeBlock(random);
            case 6: return TableBlock(random);
            case 7: return new string[] { "![그림](guide-check.png)", "" };
            default: return new string[] { "" };
        }
    }
    static readonly string[] HeadingPhrases = new string[] { "제목 문장", "Heading with **bold**", "한글 제목", "Release notes", "작업 목록", "둘째 단계" };
    static string Phrase(Random random) { return HeadingPhrases[random.Next(HeadingPhrases.Length)]; }
    static readonly string[] ParagraphShapes = new string[] {
        "일반 문장입니다. **굵게** *기울임* ~~취소선~~ `인라인 코드` 끝.",
        "한글 문장과 English words 그리고 **강조**가 섞여 있습니다.",
        "A [링크](https://example.invalid/page) and `a|b*c-d` inline code.",
        "색상 <span style=\"color:#C03030\">빨간 글자</span> 와 크기 <span style=\"font-size:22px\">큰 글자</span>.",
        "**한글 굵게**와 *한글 기울임* 그리고 ~~한글 취소선~~.",
        "접두 <span style=\"font-size:12px\">작은 글자</span> 접미.",
        "![그림](guide-check.png) 그림 뒤 문장입니다."
    };
    static string Paragraph(Random random) { return ParagraphShapes[random.Next(ParagraphShapes.Length)]; }
    static readonly string[] ListTexts = new string[] { "항목 하나", "두 번째 항목", "**굵은 항목**", "`코드` 항목", "item with *star*", "완료된 일" };
    static string ListText(Random random) { return ListTexts[random.Next(ListTexts.Length)]; }
    static readonly string[] QuoteTexts = new string[] { "인용문 첫 줄", "두 번째 인용 줄 **강조**", "quote with `code`", "한글 인용 — 대시" };
    static string QuoteText(Random random) { return QuoteTexts[random.Next(QuoteTexts.Length)]; }
    static readonly string[] CodeLines = new string[] {
        "static int Sum(int a, int b) { return a + b; }",
        "var rows = items.Where(x => x.Ok).ToList();",
        "| pipe | and * star - dash",
        "한글 주석 // 값 = 42",
        "{\"key\": \"값\", \"n\": 1}",
        "if (a < b) { print(\"x\"); }"
    };
    static string CodeLine(Random random) { return CodeLines[random.Next(CodeLines.Length)]; }
    static string[] BulletBlock(Random random) {
        var lines = new List<string>();
        int items = 2 + random.Next(3);
        for (int i = 0; i < items; i++) {
            lines.Add("- " + ListText(random));
            if (random.Next(3) == 0) {
                lines.Add("  - " + ListText(random));
                if (random.Next(2) == 0) lines.Add("    - " + ListText(random));
            }
            if (random.Next(4) == 0) lines.Add("- [" + (random.Next(2) == 0 ? " " : "x") + "] " + ListText(random));
        }
        return lines.ToArray();
    }
    static string[] NumberBlock(Random random) {
        var lines = new List<string>();
        int items = 2 + random.Next(3);
        for (int i = 1; i <= items; i++) {
            lines.Add(i.ToString(CultureInfo.InvariantCulture) + ". " + ListText(random));
            if (i == 1 && random.Next(2) == 0) {
                lines.Add("   1. " + ListText(random));
                if (random.Next(2) == 0) lines.Add("      - " + ListText(random));
            }
        }
        return lines.ToArray();
    }
    static string[] QuoteBlock(Random random) {
        var lines = new List<string>();
        lines.Add("> " + QuoteText(random));
        if (random.Next(2) == 0) lines.Add("> - " + ListText(random));
        if (random.Next(3) == 0) lines.Add("> 1. " + ListText(random));
        return lines.ToArray();
    }
    static string[] CodeBlock(Random random) {
        var lines = new List<string>();
        lines.Add("```" + (random.Next(2) == 0 ? "csharp" : "text"));
        int count = 1 + random.Next(3);
        for (int i = 0; i < count; i++) lines.Add(CodeLine(random));
        lines.Add("```");
        return lines.ToArray();
    }
    static string[] TableBlock(Random random) {
        int columns = 3 + random.Next(4);
        var lines = new List<string>();
        var header = new List<string>();
        var align = new List<string>();
        for (int column = 0; column < columns; column++) {
            header.Add(Cell(random, true));
            align.Add(Alignment(random));
        }
        lines.Add("| " + String.Join(" | ", header.ToArray()) + " |");
        lines.Add("| " + String.Join(" | ", align.ToArray()) + " |");
        int rows = 1 + random.Next(3);
        for (int row = 0; row < rows; row++) {
            var cells = new List<string>();
            for (int column = 0; column < columns; column++) cells.Add(Cell(random, false));
            lines.Add("| " + String.Join(" | ", cells.ToArray()) + " |");
        }
        return lines.ToArray();
    }
    static readonly string[] HeaderWords = new string[] { "이름", "상태", "설명", "값", "메모", "수량" };
    static readonly string[] BodyWords = new string[] { "완료", "진행 중", "값 42", "첫째", "둘째", "확인" };
    static string Cell(Random random, bool header) {
        if (!header && random.Next(5) == 0) return "**굵은 값**";
        if (!header && random.Next(5) == 0) return "`코드`";
        if (!header && random.Next(4) == 0) return LongCell(random);
        string[] words = header ? HeaderWords : BodyWords;
        return words[random.Next(words.Length)];
    }
    static readonly string[] LongCellPieces = new string[] { "아주 긴 한국어 셀 내용입니다", "여러 단어가 들어간 긴 셀 값", "상태와 설명을 함께 적은 셀" };
    static string LongCell(Random random) {
        int count = 1 + random.Next(2);
        var value = new StringBuilder();
        for (int i = 0; i < count; i++) {
            if (i > 0) value.Append(' ');
            value.Append(LongCellPieces[random.Next(LongCellPieces.Length)]);
        }
        return value.ToString();
    }
    static string Alignment(Random random) {
        switch (random.Next(4)) {
            case 0: return ":---";
            case 1: return ":---:";
            case 2: return "---:";
            default: return "---";
        }
    }
    // ---------------------------------------------------------------- visible line
    // The line's drawn text: hidden runs are skipped, a cell gap and a <br> still separate what the
    // eye sees (the same rule Session.AppendVisibleLine uses).
    static string VisibleLine(MarkdownLivePreview.LineInfo info) {
        // Folded lines draw nothing at all (Session.AppendVisibleLine returns before the walk);
        // the Basic view folds a reference definition line while the caret is elsewhere.
        if (info.Delimiter || info.SetextUnderline > 0 || info.RefDefinition) return "";
        string value = info.Text;
        if (value == null) return "";
        var text = new StringBuilder();
        int at = 0;
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Start >= value.Length) break;
            int start = replacement.Start;
            if (start > at) text.Append(value, at, start - at);
            int end = Math.Min(value.Length, start + replacement.Length);
            if (end > at) at = end;
            if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Column) text.Append(' ');
            else if (replacement.Kind == MarkdownLivePreview.ReplacementKind.LineBreak) text.Append('\n');
            else if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Glyph && replacement.Label != null) text.Append(replacement.Label);
        }
        if (at < value.Length) text.Append(value, at, value.Length - at);
        return text.ToString();
    }
    static int CountOf(string text, string piece) {
        int count = 0;
        for (int at = text.IndexOf(piece, StringComparison.Ordinal); at >= 0; at = text.IndexOf(piece, at + piece.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
}
