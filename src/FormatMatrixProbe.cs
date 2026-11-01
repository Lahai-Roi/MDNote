// Format-matrix probe: inline style arrangements edited through the window's own key,
// composition, paste and command paths on an offscreen window (no SendInput, generated text
// only). The report groups failures by signature and writes a PASS marker only when none.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
internal static class FormatMatrixProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    // A run is bounded even without MDNOTE_FORMAT_MATRIX_BUDGET: the 2026-10-31 VM run had no
    // budget and was still going after six minutes. The cooperative check stops a healthy run;
    // the watchdog stops one whose dispatcher is stuck.
    const long DefaultBudget = 600000;
    const long WatchdogGrace = 30000;
    const int PartialSeconds = 3;
    static readonly string[] Actions = { "type-X", "compose-ko", "compose-ja", "backspace", "delete", "enter", "paste-P", "select-Z", "toggle-bold", "toggle-italic" };
    static readonly string[] Carets = { "after-open", "middle", "before-close", "just-outside" };

    sealed class State {
        internal MainWindow Window;
        internal Dictionary<string,FormatMatrixKit.FailureGroup> Groups = new Dictionary<string,FormatMatrixKit.FailureGroup>();
        internal Dictionary<string,int> Runs = new Dictionary<string,int>();
        internal Dictionary<string,int> Fails = new Dictionary<string,int>();
        internal int Fixtures, Checks, Failures;
        internal bool Hide;
        internal string Mode;
        internal long Budget;
        internal DateTime Started;
        internal bool Stopped;
        // Partial-report state: the check that is running now, and what the link guard caught.
        // A hang or the watchdog leaves both in the report.
        internal string Current;
        internal int BlockedOpens;
        internal readonly List<string> BlockedUrls = new List<string>();
        internal DateTime NextPartial;
        internal string ReportFile;
        internal int Arrangements;
        internal int Planned;
        internal bool BudgetDefaulted;
        internal bool Faulted;
        internal volatile bool Finished;
        internal bool OverBudget { get { return Budget > 0 && (DateTime.UtcNow - Started).TotalMilliseconds >= Budget; } }
    }

    internal static int Run(string folder) {
        string dir = Path.GetFullPath(folder);
        Directory.CreateDirectory(dir);
        string passFile = Path.Combine(dir, "format-matrix-PASS.txt");
        string failFile = Path.Combine(dir, "format-matrix-FAIL.txt");
        string reportFile = Path.Combine(dir, "format-matrix-report.txt");
        string timeoutFile = Path.Combine(dir, "format-matrix-TIMEOUT.txt");
        if (File.Exists(passFile)) File.Delete(passFile);
        if (File.Exists(failFile)) File.Delete(failFile);
        if (File.Exists(timeoutFile)) File.Delete(timeoutFile);
        var state = new State();
        state.Started = DateTime.UtcNow;
        state.NextPartial = state.Started.AddSeconds(PartialSeconds);
        state.ReportFile = reportFile;
        bool oldSelf = Program.IsSelfTest;
        Action<string,string> oldOpener = Markdown.LinkOpener;
        bool oldHide = Static<bool>("hideMarkers");
        bool oldLive = ProbeDefaults.Live;
        MainWindow window = null;
        string file = Path.Combine(dir, "format-matrix-generated.md");
        string modeFilter = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_MATRIX_MODE");
        string caretFilter = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_MATRIX_CARET");
        string only = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_MATRIX_ONLY");
        int cap = EnvInt("MDNOTE_FORMAT_MATRIX_CASES", 0);
        string budgetText = Environment.GetEnvironmentVariable("MDNOTE_FORMAT_MATRIX_BUDGET");
        state.Budget = EnvLong("MDNOTE_FORMAT_MATRIX_BUDGET", DefaultBudget);
        state.BudgetDefaulted = String.IsNullOrEmpty(budgetText);
        int arrangementsCount = 0;
        int planned = 0;
        try {
            Program.IsSelfTest = true;
            // The guard --link-probe installs: every external open the product would make is
            // captured here instead, so a simulated or stray input can never launch a browser.
            Markdown.LinkOpener = delegate(string url, string document) {
                state.BlockedOpens++;
                if (state.BlockedUrls.Count < 8) state.BlockedUrls.Add(url);
            };
            StartWatchdog(state, timeoutFile);
            Static("hideMarkers", true);
            ProbeDefaults.Live = true;
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            File.WriteAllText(file, "# 제목\n\n본문\n", new UTF8Encoding(false));
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 1000, Height = 720 };
            window.Show();
            window.Left = -20000;
            window.Top = -20000;
            Invoke(window, "LoadFileCore", file);
            Invoke(window, "SetMode", true);
            Pump();
            state.Window = window;
            // AvalonEdit's URL hyperlinks have their own Process.Start (vendor
            // VisualLineLinkText.cs:109) and do not pass through Markdown.OpenLink. SourceEditor
            // already turns them off; repeat it on the probe's editor so a changed default cannot
            // reopen that path under a test.
            SourceEditor probeSource = Source(window);
            if (probeSource != null) { probeSource.Options.EnableHyperlinks = false; probeSource.Options.EnableEmailHyperlinks = false; }
            List<FormatMatrixKit.Arrangement> arrangements = FormatMatrixKit.Arrangements();
            arrangementsCount = arrangements.Count;
            string[] modes = { "basic", "live" };
            planned = arrangements.Count * FormatMatrixKit.Containers.Length * Carets.Length * modes.Length;
            state.Arrangements = arrangementsCount;
            state.Planned = planned;
            int stride = cap > 0 && planned > cap ? (planned + cap - 1) / cap : 1;
            int index = 0;
            foreach (string mode in modes) {
                if (!String.IsNullOrEmpty(modeFilter) && !String.Equals(modeFilter, mode, StringComparison.OrdinalIgnoreCase)) continue;
                state.Mode = mode;
                state.Hide = String.Equals(mode, "basic", StringComparison.Ordinal);
                Static("hideMarkers", state.Hide);
                Invoke(window, "UpdateLivePreview");
                Pump();
                for (int ai = 0; ai < arrangements.Count && !state.Stopped; ai++) {
                    FormatMatrixKit.Arrangement arrangement = arrangements[ai];
                    for (int ci = 0; ci < FormatMatrixKit.Containers.Length && !state.Stopped; ci++) {
                        string container = FormatMatrixKit.Containers[ci];
                        for (int ki = 0; ki < Carets.Length && !state.Stopped; ki++) {
                            string caret = Carets[ki];
                            index++;
                            if (stride > 1 && (index - 1) % stride != 0) continue;
                            if (!String.IsNullOrEmpty(caretFilter) && !String.Equals(caretFilter, caret, StringComparison.OrdinalIgnoreCase)) continue;
                            if (!String.IsNullOrEmpty(only) && arrangement.Name.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0 && container.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            if (state.OverBudget) { state.Stopped = true; break; }
                            state.Fixtures++;
                            RunFixture(state, arrangement, container, caret);
                            if (state.OverBudget) state.Stopped = true;
                            MaybeWritePartial(state);
                        }
                    }
                }
            }
        } catch (Exception ex) {
            state.Failures++;
            state.Faulted = true;
            FormatMatrixKit.AddFailure(state.Groups, "fatal | run", OneLine(ex.ToString()));
            WriteReport(state.ReportFile, ComposeReport(state, state.Arrangements, state.Planned));
        } finally {
            if (window != null) {
                try {
                    Set(window, "documentOperationBusy", false);
                    Set(window, "saved", Prop<string>(window, "text"));
                    Set(window, "recoveredDocument", false);
                    window.Close();
                } catch (Exception ex) {
                    FormatMatrixKit.AddFailure(state.Groups, "fatal | close", OneLine(ex.Message));
                }
            }
            Static("hideMarkers", oldHide);
            ProbeDefaults.Live = oldLive;
            Markdown.LinkOpener = oldOpener;
            Program.IsSelfTest = oldSelf;
            state.Current = null;
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
        }
        state.Finished = true;
        string report = ComposeReport(state, arrangementsCount, planned);
        bool pass = state.Failures == 0 && !state.Stopped;
        try {
            File.WriteAllText(reportFile, report, new UTF8Encoding(false));
            if (pass) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
            else File.WriteAllText(failFile, report, new UTF8Encoding(false));
        } catch (IOException) {
            return 1;
        }
        return pass ? 0 : 1;
    }

    // ---------------------------------------------------------------- report
    static string ComposeReport(State state, int arrangements, int planned) {
        bool final = state.Finished;
        var text = new StringBuilder();
        text.Append("# FormatMatrixProbe\n\n");
        text.Append("- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- Run state: ").Append(final ? "finished" : state.Faulted ? "aborted (exception)" : "running (partial report, rewritten every few seconds)").Append('\n');
        text.Append("- Fixtures run: ").Append(state.Fixtures.ToString(CultureInfo.InvariantCulture)).Append(" of ").Append(planned.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (!final && state.Current != null) text.Append("- Current check: ").Append(state.Current).Append('\n');
        text.Append("- Action checks: ").Append(state.Checks.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- Arrangements: ").Append(arrangements.ToString(CultureInfo.InvariantCulture)).Append(" x containers ").Append(FormatMatrixKit.Containers.Length.ToString(CultureInfo.InvariantCulture)).Append(" x carets ").Append(Carets.Length.ToString(CultureInfo.InvariantCulture)).Append(" x 2 modes\n");
        text.Append("- Elapsed seconds: ").Append((DateTime.UtcNow - state.Started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- Budget: ").Append(state.Budget > 0 ? state.Budget.ToString(CultureInfo.InvariantCulture) + " ms" + (state.BudgetDefaulted ? " (default)" : "") : "none").Append(state.Stopped ? " (stopped)" : "").Append('\n');
        text.Append("- Hard stop: format-matrix-TIMEOUT.txt and exit 3 at budget + ").Append(WatchdogGrace.ToString(CultureInfo.InvariantCulture)).Append(" ms\n");
        text.Append("- Modes: basic (HideMarkers true) and live (HideMarkers false)\n");
        text.Append("- Controls: MDNOTE_FORMAT_MATRIX_MODE, _CARET, _ONLY, _CASES, _BUDGET\n");
        text.Append("- External opens blocked by the link guard: ").Append(state.BlockedOpens.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (state.BlockedUrls.Count > 0) {
            text.Append("- Blocked targets: ");
            for (int i = 0; i < state.BlockedUrls.Count; i++) { if (i > 0) text.Append(", "); text.Append(FormatMatrixKit.Esc(state.BlockedUrls[i])); }
            text.Append('\n');
        }
        text.Append("- Hyperlink generators: disabled on the probe editor (AvalonEdit opens URLs with its own Process.Start)\n");
        text.Append("\n## Check totals\n");
        var names = new List<string>(state.Runs.Keys);
        names.Sort(StringComparer.Ordinal);
        if (names.Count == 0) text.Append("(none)\n");
        for (int i = 0; i < names.Count; i++) {
            string name = names[i];
            int runs, fails;
            state.Runs.TryGetValue(name, out runs);
            state.Fails.TryGetValue(name, out fails);
            text.Append("- ").Append(name).Append(": ").Append(runs.ToString(CultureInfo.InvariantCulture)).Append(" run, ").Append(fails.ToString(CultureInfo.InvariantCulture)).Append(" failed\n");
        }
        text.Append("\n## Failures by signature\n");
        text.Append("Signature = check  arrangement | container | caret | action | mode. Each group lists its count and the first 3 examples (before/after source).\n");
        var groups = new List<FormatMatrixKit.FailureGroup>(state.Groups.Values);
        groups.Sort(CompareGroups);
        if (groups.Count == 0) text.Append("(none)\n");
        int shown = groups.Count > 300 ? 300 : groups.Count;
        for (int group = 0; group < shown; group++) {
            FormatMatrixKit.FailureGroup item = groups[group];
            text.Append("\n### ").Append(item.Signature).Append("  -  ").Append(item.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = 0; i < item.Examples.Count; i++) {
                text.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(". ").Append(item.Examples[i]).Append('\n');
            }
        }
        if (groups.Count > shown) text.Append("\n(").Append((groups.Count - shown).ToString(CultureInfo.InvariantCulture)).Append(" more signatures are not listed; the largest counts are above.)\n");
        text.Append("\n## Failures by dimension\n");
        Dimension(text, groups, "action", 3);
        Dimension(text, groups, "container", 1);
        Dimension(text, groups, "caret", 2);
        Dimension(text, groups, "mode", 4);
        text.Append("\n## Notes\n");
        text.Append("- style-flags and span-style run in basic mode only: in live mode the caret line is drawn as raw source by design, so the drawn style at the typed character is not a property of the surface.\n");
        text.Append("- balance in live mode runs for insert, Enter and toggle actions only: Backspace, Delete and a selection typed over are plain source edits there and may remove a marker on purpose.\n");
        text.Append("- visible compares the parsed line in basic mode; in live mode the same fixture checks that the caret line stays raw instead.\n");
        text.Append("- middle equals after-open for a one-character focus run (nested and adjacent arrangements).\n");
        string result = final ? (state.Failures == 0 && !state.Stopped ? "PASS" : "FAIL") : state.Faulted ? "ABORTED" : "RUNNING";
        text.Append("\nResult: ").Append(result).Append('\n');
        return text.ToString();
    }
    static int CompareGroups(FormatMatrixKit.FailureGroup a, FormatMatrixKit.FailureGroup b) {
        int order = b.Count.CompareTo(a.Count);
        if (order != 0) return order;
        return String.CompareOrdinal(a.Signature, b.Signature);
    }
    // A partial report lands every few seconds, so a hang still leaves the counters and the check
    // that was running. The final write replaces it.
    static void MaybeWritePartial(State state) {
        if (state.Finished || state.Faulted) return;
        if (DateTime.UtcNow < state.NextPartial) return;
        state.NextPartial = DateTime.UtcNow.AddSeconds(PartialSeconds);
        // A report write must never end the run it is describing.
        try { WriteReport(state.ReportFile, ComposeReport(state, state.Arrangements, state.Planned)); } catch (Exception) { }
    }
    static void WriteReport(string path, string text) {
        try { File.WriteAllText(path, text, new UTF8Encoding(false)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    // Hard bound for a stuck dispatcher, where no cooperative budget check can run. A healthy run
    // stops through OverBudget well before this and sets Finished before composing its report.
    static void StartWatchdog(State state, string timeoutFile) {
        System.Threading.Thread thread = new System.Threading.Thread(delegate() {
            long elapsed = 0;
            while (true) {
                elapsed = (long)(DateTime.UtcNow - state.Started).TotalMilliseconds;
                if (elapsed >= state.Budget + WatchdogGrace) break;
                System.Threading.Thread.Sleep(1000);
            }
            if (state.Finished) return;
            try {
                File.WriteAllText(timeoutFile,
                    "TIMEOUT after " + elapsed + " ms (budget " + state.Budget + " ms)\n" +
                    "Fixtures started: " + state.Fixtures + ", checks: " + state.Checks + "\n" +
                    "Last check: " + (state.Current == null ? "(none)" : state.Current) + "\n",
                    new UTF8Encoding(false));
            } catch (Exception) { }
            Environment.Exit(3);
        });
        thread.IsBackground = true;
        thread.Name = "format-matrix watchdog";
        thread.Start();
    }
    // Failure counts along one axis of the signature, so one action that fails everywhere is
    // visible as a single line instead of hundreds of groups.
    static void Dimension(StringBuilder text, List<FormatMatrixKit.FailureGroup> groups, string label, int slot) {
        var counts = new Dictionary<string,int>();
        var order = new List<string>();
        for (int i = 0; i < groups.Count; i++) {
            string[] parts = groups[i].Signature.Split(new string[] { " | " }, StringSplitOptions.None);
            if (parts.Length != 5) continue;
            string key = parts[slot];
            int count;
            if (!counts.TryGetValue(key, out count)) order.Add(key);
            counts[key] = count + groups[i].Count;
        }
        order.Sort(StringComparer.Ordinal);
        text.Append("- ").Append(label).Append(": ");
        if (order.Count == 0) text.Append("(none)");
        for (int i = 0; i < order.Count; i++) {
            if (i > 0) text.Append(", ");
            text.Append(order[i]).Append('=').Append(counts[order[i]].ToString(CultureInfo.InvariantCulture));
        }
        text.Append('\n');
    }

    // ---------------------------------------------------------------- fixture driver
    static void RunFixture(State state, FormatMatrixKit.Arrangement arrangement, string container, string caret) {
        FormatMatrixKit.Fixture fixture = FormatMatrixKit.Build(arrangement, container);
        for (int i = 0; i < Actions.Length; i++) {
            if (state.OverBudget) { state.Stopped = true; return; }
            string action = Actions[i];
            state.Checks++;
            string signature = arrangement.Name + " | " + container + " | " + caret + " | " + action + " | " + state.Mode;
            state.Current = signature;
            // The check goes into the partial report before it runs, so a hang inside this action
            // still names it (the write itself is time-gated).
            MaybeWritePartial(state);
            try {
                RunAction(state, container, caret, action, fixture, signature);
            } catch (Exception ex) {
                state.Failures++;
                FormatMatrixKit.AddFailure(state.Groups, "exception  " + signature,
                    "before=[" + FormatMatrixKit.Esc(fixture.Source) + "] after=(not read) " + OneLine(ex.GetType().Name + ": " + ex.Message));
            }
            if (state.OverBudget) { state.Stopped = true; return; }
        }
        state.Current = null;
    }
    static void Note(State state, string check, string signature, bool ok, string detail, string before, string after) {
        int runs;
        state.Runs.TryGetValue(check, out runs);
        state.Runs[check] = runs + 1;
        if (ok) return;
        int fails;
        state.Fails.TryGetValue(check, out fails);
        state.Fails[check] = fails + 1;
        state.Failures++;
        FormatMatrixKit.AddFailure(state.Groups, check + "  " + signature,
            "before=[" + FormatMatrixKit.Esc(before) + "] after=[" + FormatMatrixKit.Esc(after) + "] " + FormatMatrixKit.Esc(detail));
    }

    // ---------------------------------------------------------------- one action
    static void RunAction(State state, string container, string caret, string action, FormatMatrixKit.Fixture fixture, string signature) {
        if (state.OverBudget) { state.Stopped = true; return; }
        MainWindow window = state.Window;
        bool hide = state.Hide;
        string seed = fixture.Source;
        Load(window, seed);
        SourceEditor source = Source(window);
        MarkdownLivePreview.Session session = Session(window);
        if (source == null || session == null || session.Options == null) {
            Note(state, "open", signature, false, "source=" + (source != null) + " session=" + (session != null), seed, seed);
            return;
        }
        Note(state, "mode", signature, session.Options.HideMarkers == hide, "hide=" + session.Options.HideMarkers + " wanted=" + hide, seed, seed);
        string seedFault = FormatMatrixKit.SourceFault(seed);
        Note(state, "seed-balance", signature, seedFault == null, seedFault, seed, seed);
        DocumentLine line = session.Document.GetLineByNumber(fixture.LineNumber);
        string seedVisible = FormatMatrixKit.VisibleParts(session, line, null);
        if (hide) Note(state, "seed-visible", signature, seedVisible == fixture.LineVisible, "visible=[" + seedVisible + "] expected=[" + fixture.LineVisible + "]", seed, seed);
        if (session.Options.HideMarkers != hide || seedFault != null || (hide && seedVisible != fixture.LineVisible)) return;

        // ---- caret: the focus pair's own edges and middle, in visible-text terms
        int lineOffset = line.Offset;
        int caretRelative = CaretRelative(fixture, caret);
        int visibleIndex = VisibleIndex(fixture, caret);
        source.Select(lineOffset + caretRelative, 0);
        Pump();
        Note(state, "caret-settled", signature, source.CaretIndex == lineOffset + caretRelative,
            "settled=" + source.CaretIndex + " wanted=" + (lineOffset + caretRelative), seed, seed);
        if (!hide) {
            // The live mode draws the caret line as source: after the caret is placed, that line
            // must be the raw one (checked again after every action).
            DocumentLine caretLine = session.Document.GetLineByOffset(Math.Min(source.CaretIndex, session.Document.TextLength));
            Note(state, "seed-raw", signature, session.Raw(caretLine), "raw=" + session.Raw(caretLine), seed, seed);
        }
        if (action == "select-Z") {
            if (fixture.VisStart + fixture.VisLen >= fixture.Offsets.Count) {
                Note(state, "select", signature, false, "no visible character after the focus", seed, seed);
                return;
            }
            int start = fixture.Offsets[fixture.VisStart + fixture.VisLen - 1];
            int end = fixture.Offsets[fixture.VisStart + fixture.VisLen] + 1;
            if (end <= start || lineOffset + end > source.Document.TextLength) {
                Note(state, "select", signature, false, "start=" + start + " end=" + end, seed, seed);
                return;
            }
            source.Select(lineOffset + start, end - start);
            Pump();
            visibleIndex = fixture.VisStart + fixture.VisLen - 1;
        }
        string preLeft = hide && action == "enter" ? FormatMatrixKit.Flags(session, lineOffset + fixture.Offsets[Math.Max(0, visibleIndex - 1)]) : null;
        string preRight = hide && action == "enter" && visibleIndex < fixture.Offsets.Count ? FormatMatrixKit.Flags(session, lineOffset + fixture.Offsets[visibleIndex]) : null;

        // ---- the action itself
        bool imeBusy = Apply(window, action, source);
        if (imeBusy) Note(state, "ime-busy", signature, false, "the composition still holds the editor after its result", seed, source.Document.Text);

        string afterText = source.Document.Text;
        MarkdownLivePreview.Session after = Session(window);
        if (after == null) { Note(state, "after-session", signature, false, "", seed, afterText); return; }
        if (hide || KeepBalance(action)) {
            string fault = FormatMatrixKit.SourceFault(afterText);
            Note(state, "balance", signature, fault == null, fault, seed, afterText);
        }
        DocumentLine target = after.Document.GetLineByOffset(Math.Min(source.CaretIndex, after.Document.TextLength));
        if (!hide) Note(state, "live-raw", signature, after.Raw(target), "raw=" + after.Raw(target), seed, afterText);
        if (action == "enter") {
            EnterChecks(state, container, fixture, signature, hide, visibleIndex, preLeft, preRight, after, seed, afterText);
        } else if (hide) {
            var offsets = new List<int>();
            DocumentLine targetLine = after.Document.GetLineByNumber(target.LineNumber);
            string visible = FormatMatrixKit.VisibleParts(after, targetLine, offsets);
            string expected = ExpectedVisible(action, fixture.LineVisible, visibleIndex);
            if (expected != null) Note(state, "visible", signature, visible == expected, "visible=[" + visible + "] expected=[" + expected + "]", seed, afterText);
            string leak = FormatMatrixKit.MarkupFault(visible);
            Note(state, "markup-leak", signature, leak == null, "visible=[" + visible + "] draws " + leak, seed, afterText);
            if (IsTyping(action)) {
                string typed = TypedText(action);
                int at = visible.IndexOf(typed, StringComparison.Ordinal);
                if (at < 0) Note(state, "typed-missing", signature, false, "typed=[" + typed + "] visible=[" + visible + "]", seed, afterText);
                else if (at < offsets.Count) {
                    int relative = offsets[at];
                    int neighbor;
                    if (Neighbor(after, targetLine, relative, typed.Length, out neighbor)) {
                        // The typed character must look like the visible character it joins: inside a
                        // run it takes the run's flags, just outside it takes none of them.
                        string got = FormatMatrixKit.Flags(after, targetLine.Offset + relative);
                        string want = FormatMatrixKit.Flags(after, targetLine.Offset + neighbor);
                        string joins = after.Document.GetText(targetLine.Offset + neighbor, 1);
                        Note(state, "style-flags", signature, got == want, "typed=" + got + " joins [" + joins + "] at " + neighbor + " with " + want, seed, afterText);
                        string gotSpan = FormatMatrixKit.SpanSignature(after, targetLine, relative);
                        string wantSpan = FormatMatrixKit.SpanSignature(after, targetLine, neighbor);
                        Note(state, "span-style", signature, gotSpan == wantSpan, "typed=" + gotSpan + " neighbor=" + wantSpan, seed, afterText);
                    }
                }
            }
        }

        // ---- one undo restores the seed exactly, one redo puts the result back
        if (state.OverBudget) { state.Stopped = true; return; }
        string afterSource = source.Document.Text;
        Invoke(window, "Undo");
        Pump();
        Note(state, "undo", signature, source.Document.Text == seed, "undone=[" + source.Document.Text + "] expected=[" + seed + "]", seed, afterSource);
        Invoke(window, "Redo");
        Pump();
        Note(state, "redo", signature, source.Document.Text == afterSource, "redone=[" + source.Document.Text + "] expected=[" + afterSource + "]", seed, afterSource);
    }

    // ---------------------------------------------------------------- Enter
    // The split must keep each line's style set, continue the container prefix (bullet, number,
    // task, quote; a heading keeps its marker on the first line only) and, inside a table cell,
    // write "<br>" instead of a new source line.
    static void EnterChecks(State state, string container, FormatMatrixKit.Fixture fixture, string signature, bool hide, int visibleIndex, string preLeft, string preRight,
            MarkdownLivePreview.Session after, string seed, string afterText) {
        if (container == "table") {
            DocumentLine row = after.Document.GetLineByNumber(Math.Min(fixture.LineNumber, after.Document.LineCount));
            string rowText = after.Document.GetText(row);
            Note(state, "enter-br", signature, rowText.IndexOf("<br>", StringComparison.Ordinal) >= 0, "row=[" + rowText + "]", seed, afterText);
            int pipes = FormatMatrixKit.Count(rowText, '|');
            Note(state, "enter-pipes", signature, pipes == 4, "pipes=" + pipes, seed, afterText);
            if (!hide) return;
            var offsets = new List<int>();
            string visible = FormatMatrixKit.VisibleParts(after, row, offsets);
            string expected = Insert(fixture.LineVisible, visibleIndex, "\n");
            Note(state, "enter-visible", signature, visible == expected, "visible=[" + visible + "] expected=[" + expected + "]", seed, afterText);
            string rowLeak = FormatMatrixKit.MarkupFault(visible);
            Note(state, "markup-leak", signature, rowLeak == null, "visible=[" + visible + "] draws " + rowLeak, seed, afterText);
            int nl = visible.IndexOf('\n');
            if (nl > 0 && nl - 1 < offsets.Count) {
                string got = FormatMatrixKit.Flags(after, row.Offset + offsets[nl - 1]);
                Note(state, "enter-style-left", signature, got == preLeft, "got=" + got + " want=" + preLeft, seed, afterText);
            }
            if (nl >= 0 && nl + 1 < offsets.Count) {
                string got = FormatMatrixKit.Flags(after, row.Offset + offsets[nl + 1]);
                Note(state, "enter-style-right", signature, got == preRight, "got=" + got + " want=" + preRight, seed, afterText);
            }
            return;
        }
        bool two = after.Document.LineCount >= fixture.LineNumber + 1;
        Note(state, "enter-lines", signature, two, "lines=" + after.Document.LineCount, seed, afterText);
        if (!two) return;
        string second = after.Document.GetText(after.Document.GetLineByNumber(fixture.LineNumber + 1));
        string prefix = ContinuationPrefix(container);
        bool prefixOk = prefix == null ? !second.StartsWith("#", StringComparison.Ordinal) : second.StartsWith(prefix, StringComparison.Ordinal);
        Note(state, "enter-prefix", signature, prefixOk, "line2=[" + second + "] want=[" + (prefix == null ? "no heading" : prefix) + "]", seed, afterText);
        if (!hide) return;
        var firstOffsets = new List<int>();
        var secondOffsets = new List<int>();
        DocumentLine firstLine = after.Document.GetLineByNumber(fixture.LineNumber);
        DocumentLine secondLine = after.Document.GetLineByNumber(fixture.LineNumber + 1);
        string firstVisible = FormatMatrixKit.VisibleParts(after, firstLine, firstOffsets);
        string secondVisible = FormatMatrixKit.VisibleParts(after, secondLine, secondOffsets);
        if (visibleIndex >= 0 && visibleIndex <= fixture.LineVisible.Length) {
            string wantFirst = fixture.LineVisible.Substring(0, visibleIndex);
            string wantSecond = fixture.LineVisible.Substring(visibleIndex);
            Note(state, "enter-visible", signature, firstVisible == wantFirst && secondVisible == wantSecond,
                "line1=[" + firstVisible + "] want=[" + wantFirst + "] line2=[" + secondVisible + "] want=[" + wantSecond + "]", seed, afterText);
        }
        string leftLeak = FormatMatrixKit.MarkupFault(firstVisible);
        string rightLeak = FormatMatrixKit.MarkupFault(secondVisible);
        Note(state, "markup-leak", signature, leftLeak == null && rightLeak == null,
            "line1=[" + firstVisible + "] draws " + leftLeak + ", line2=[" + secondVisible + "] draws " + rightLeak, seed, afterText);
        if (firstVisible.Length > 0) {
            string got = FormatMatrixKit.Flags(after, firstLine.Offset + firstOffsets[firstVisible.Length - 1]);
            Note(state, "enter-style-left", signature, got == preLeft, "got=" + got + " want=" + preLeft, seed, afterText);
        }
        if (secondVisible.Length > 0) {
            string got = FormatMatrixKit.Flags(after, secondLine.Offset + secondOffsets[0]);
            Note(state, "enter-style-right", signature, got == preRight, "got=" + got + " want=" + preRight, seed, afterText);
        }
    }

    // ---------------------------------------------------------------- expectations
    static int CaretRelative(FormatMatrixKit.Fixture f, string caret) {
        switch (caret) {
            case "after-open": return f.OpenStart + f.OpenLen;
            case "middle": return f.Offsets[f.VisStart + f.VisLen / 2];
            case "before-close": return f.CloseStart;
            default: return f.CloseStart + f.CloseLen;
        }
    }
    static int VisibleIndex(FormatMatrixKit.Fixture f, string caret) {
        switch (caret) {
            case "after-open": return f.VisStart;
            case "middle": return f.VisStart + f.VisLen / 2;
            default: return f.VisStart + f.VisLen;
        }
    }
    static bool IsTyping(string action) {
        return action == "type-X" || action == "compose-ko" || action == "compose-ja" || action == "paste-P" || action == "select-Z";
    }
    static string TypedText(string action) {
        switch (action) {
            case "type-X": return "X";
            case "compose-ko": return "한";
            case "compose-ja": return "日本";
            case "paste-P": return "P";
            case "select-Z": return "Z";
            default: return "";
        }
    }
    static bool KeepBalance(string action) {
        return action == "type-X" || action == "compose-ko" || action == "compose-ja" || action == "paste-P" ||
            action == "enter" || action == "toggle-bold" || action == "toggle-italic";
    }
    static string ContinuationPrefix(string container) {
        switch (container) {
            case "bullet": return "- ";
            case "number": return "2. ";
            case "task": return "- [ ] ";
            case "quote": return "> ";
            case "quote-bullet": return "> - ";
            default: return null;
        }
    }
    static string ExpectedVisible(string action, string visible, int index) {
        switch (action) {
            case "type-X": return Insert(visible, index, "X");
            case "compose-ko": return Insert(visible, index, "한");
            case "compose-ja": return Insert(visible, index, "日本");
            case "paste-P": return Insert(visible, index, "P");
            case "select-Z": return index >= 0 && index + 2 <= visible.Length ? visible.Remove(index, 2).Insert(index, "Z") : null;
            case "backspace": return index > 0 ? visible.Remove(index - 1, 1) : null;
            case "delete": return index >= 0 && index < visible.Length ? visible.Remove(index, 1) : null;
            default: return visible;
        }
    }
    static string Insert(string value, int at, string text) {
        if (value == null || at < 0 || at > value.Length) return null;
        return value.Substring(0, at) + text + value.Substring(at);
    }
    // The visible character the typed text joined: the source character immediately left, else
    // the one immediately right (a hidden marker in between means that side was not joined).
    static bool Neighbor(MarkdownLivePreview.Session session, DocumentLine line, int relative, int length, out int neighbor) {
        neighbor = -1;
        MarkdownLivePreview.LineInfo info = session.Info(line);
        int left = relative - 1;
        if (left >= 0 && !FormatMatrixKit.HiddenAt(info, left)) { neighbor = left; return true; }
        int right = relative + length;
        if (right >= 0 && right < info.Text.Length && !FormatMatrixKit.HiddenAt(info, right)) { neighbor = right; return true; }
        return false;
    }

    // ---------------------------------------------------------------- action paths
    static bool Apply(MainWindow window, string action, SourceEditor source) {
        if (action == "type-X") { source.TextArea.PerformTextInput("X"); Pump(); return false; }
        if (action == "select-Z") { source.TextArea.PerformTextInput("Z"); Pump(); return false; }
        if (action == "compose-ko") return Compose(source, "한", 1);
        if (action == "compose-ja") return Compose(source, "日本", 2);
        if (action == "backspace") { EditingCommands.Backspace.Execute(null, source.TextArea); Pump(); return false; }
        if (action == "delete") { EditingCommands.Delete.Execute(null, source.TextArea); Pump(); return false; }
        if (action == "enter") { Enter(window); return false; }
        if (action == "paste-P") { Invoke(window, "LivePaste", new System.Windows.DataObject(), true, "P"); Pump(); return false; }
        if (action == "toggle-bold") { Invoke(window, "ToggleFormat", "bold"); Pump(); return false; }
        if (action == "toggle-italic") { Invoke(window, "ToggleFormat", "italic"); Pump(); return false; }
        return false;
    }
    // The composition path the other probes use: a counted result of `length` characters arrives
    // through the editor's own TextInput hook. True when the editor is still holding it.
    static bool Compose(SourceEditor source, string text, int length) {
        source.SimulateImeCommit(length);
        source.TextArea.PerformTextInput(text);
        Pump();
        bool busy = source.ImeBusy;
        source.ResetCompositionSimulation();
        return busy;
    }
    static void Load(MainWindow window, string text) {
        Invoke(window, "SetText", text);
        Pump();
    }
    static void Enter(MainWindow window) {
        SourceEditor source = Source(window);
        CommandResult result = (CommandResult)Invoke(window, "SourceEnter");
        if (result == null) { source.TextArea.PerformTextInput("\n"); Pump(); return; }
        Invoke(window, "ApplyCommandResult", result, -1, 0);
        Pump();
    }
    static SourceEditor Source(MainWindow window) { return Field<SourceEditor>(window, "source"); }
    static MarkdownLivePreview.Session Session(MainWindow window) { return Field<MarkdownLivePreview.Session>(window, "livePreview"); }
    static string OneLine(string text) {
        if (text == null) return "null";
        string value = text.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length > 220 ? value.Substring(0, 220) + "..." : value;
    }

    // ---------------------------------------------------------------- reflection
    static void Pump() {
        LongLineBench.Idle();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
        LongLineBench.Idle();
    }
    static object Invoke(MainWindow window, string name, params object[] args) {
        MethodInfo best = null;
        MethodInfo[] methods = typeof(MainWindow).GetMethods(Members);
        for (int m = 0; m < methods.Length; m++) {
            MethodInfo method = methods[m];
            if (method.Name != name) continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != args.Length) continue;
            bool fit = true;
            for (int i = 0; i < parameters.Length; i++) {
                if (args[i] == null) {
                    if (parameters[i].ParameterType.IsValueType) { fit = false; break; }
                    continue;
                }
                if (!parameters[i].ParameterType.IsInstanceOfType(args[i])) { fit = false; break; }
            }
            if (fit) { best = method; break; }
        }
        if (best == null) throw new MissingMemberException("MainWindow has no method " + name + "/" + args.Length);
        return best.Invoke(window, args);
    }
    static T Field<T>(MainWindow window, string name) {
        FieldInfo field = typeof(MainWindow).GetField(name, Members);
        if (field == null) throw new MissingMemberException("MainWindow has no field " + name);
        return (T)field.GetValue(window);
    }
    static void Set(MainWindow window, string name, object value) {
        FieldInfo field = typeof(MainWindow).GetField(name, Members);
        if (field == null) throw new MissingMemberException("MainWindow has no field " + name);
        field.SetValue(window, value);
    }
    static T Prop<T>(MainWindow window, string name) {
        PropertyInfo property = typeof(MainWindow).GetProperty(name, Members);
        if (property == null) throw new MissingMemberException("MainWindow has no property " + name);
        return (T)property.GetValue(window, null);
    }
    static T Static<T>(string name) {
        FieldInfo field = typeof(MainWindow).GetField(name, Members);
        if (field == null) throw new MissingMemberException("MainWindow has no static field " + name);
        return (T)field.GetValue(null);
    }
    static void Static(string name, object value) {
        FieldInfo field = typeof(MainWindow).GetField(name, Members);
        if (field == null) throw new MissingMemberException("MainWindow has no static field " + name);
        field.SetValue(null, value);
    }
    static int EnvInt(string name, int fallback) {
        string value = Environment.GetEnvironmentVariable(name);
        int number;
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) return number;
        return fallback;
    }
    static long EnvLong(string name, long fallback) {
        string value = Environment.GetEnvironmentVariable(name);
        long number;
        if (!String.IsNullOrEmpty(value) && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) return number;
        return fallback;
    }
}
}
