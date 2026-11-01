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
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// --large-doc-abuse-probe <folder>: generated 300K/3M Markdown with hundreds of tables, long lists,
// code blocks, one 100K-character line and Korean/emoji text. Seeded sequences drive open, caret
// jumps (Ctrl+End/Home, page bursts, random offsets, scroll), clicks and caret placement in tables
// and the long line, typing, Enter/Backspace/Delete, table commands, paste, undo/redo, view-mode
// switch and find next. After every step the probe requires: no exception, no UI step longer than
// 2 seconds, a typed/pasted edit at the caret's source offset, valid table cell counts, the caret
// inside the document and (after a move) its line on screen, the edited long line's full text
// intact, and exact undo/redo restoration per history segment. Fixtures are generated here; no user
// file is read and the system clipboard is never touched (Cut is not driven; Paste uses LivePaste
// with an empty DataObject).
// Report: <folder>\large-doc-abuse-probe.txt; large-doc-abuse-probe-PASS.txt only when every
// sequence kept every invariant.
internal static class LargeDocAbuseProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int SmallSize = 300000, LargeSize = 3000000;
    const int SmallTablesMin = 200, LargeTablesMin = 300;
    const int BlockThreshold = 16384;
    const int LongLineChars = 100000;
    const int SegmentLength = 30;
    const int UndoCap = 300;
    const int MaxFailures = 80;
    const int MaxSlowSteps = 24;
    const long DefaultBudgetMilliseconds = 210000;
    const long StepLimitMilliseconds = 2000;
    const string Emoji = "\U0001F600";
    const string FindQuery = "값";
    const string KoreanSyllables = "가나다라마바사아자차카타파하거너더러머버서어저처커터퍼허고노도로모보소오조초코토포호구누두루무부수우주추쿠투푸후기니디리미비시이지치키티피히값메모표행열";

    internal sealed class FixtureInfo {
        internal string Name, Path, Text, Newline;
        internal int Target, Chars, Lines, Tables, TableRows, ListItems, CodeBlocks;
        internal int LongLineStart, LongLineLength, LongLine;
    }
    internal sealed class ChangeInfo {
        internal int Offset, Removed, Inserted;
        internal string Text;
        // A short removal keeps its text for the failure detail (a deleted newline/pipe is the
        // difference between a documented separator edit and a lost cell).
        internal string RemovedText;
    }
    internal sealed class TableBlock {
        internal int Header, Delimiter, Last, HeaderCells, HeaderStart;
        internal string HeaderText, DelimiterText;
        internal List<string> Lines = new List<string>();
    }
    internal sealed class FailureInfo {
        internal int Seed, Step, Occurrences = 1;
        internal string Size, Mode, Fixture, Action, Invariant, Detail, Before, After, Steps, Changes;
        internal long DurationMs;
    }
    internal sealed class SlowStep {
        internal int Seed, Step;
        internal string Size, Mode, Action;
        internal long Ms;
    }
    internal sealed class SequenceResult {
        internal bool Ok;
        internal int Actions, UndoOracles;
        internal long OpenMs, UndoMs, RunMs;
        internal string Surface = "(not opened)";
    }
    internal sealed class StepContext {
        internal MainWindow Window;
        internal string Action, Label, OpKey, Payload;
        internal int Command;
        internal int PreCaret, PreSelectionStart, PreSelectionLength;
        internal int PreLength, PreLineCount, PreLineNumber, PreLineStart, PreLineLength;
        internal string PreLineText, PreLineDelimiter;
        internal bool PreLineWasRow;
        internal int PreCells;
        internal TableBlock PreTable;
        internal int PreTableStart;
        internal bool PreLongLine;
        internal int PreLongSliceStart, PreLongSliceEnd;
        internal string PreLongSlice;
        internal bool ClickPrepared;
        internal DocumentLine ClickRow;
        internal int ClickCellIndex, ClickSample;
        internal bool FindBackwards;
        internal int FindWaited;
        internal bool ModeTarget;
        internal int PostLength, PostLineCount, PostCaret;
        internal long DurationMs;
    }

    static string outRoot, reportDir;
    static StringBuilder reportRuns;
    static FixtureInfo currentFixture;
    static int currentSeed, currentStep, currentSequence;
    static string currentSize = "", currentMode = "", currentAction = "";
    static bool all, budgetReached, dispatcherFailed, capturing;
    static readonly List<string> dispatcherNotes = new List<string>();
    static readonly List<string> stepNames = new List<string>();
    static readonly List<ChangeInfo> changes = new List<ChangeInfo>();
    static readonly Dictionary<string,int> opCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> checkCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> failCounts = new Dictionary<string,int>();
    static readonly List<FailureInfo> failures = new List<FailureInfo>();
    static readonly Dictionary<string,FailureInfo> failureKeys = new Dictionary<string,FailureInfo>();
    static readonly List<SlowStep> slowSteps = new List<SlowStep>();
    static int omittedFailures;
    static EventHandler<DocumentChangeEventArgs> changeHandler;
    static MainWindow currentWindow;
    static ITextAnchor longAnchor;
    static long budgetMilliseconds = DefaultBudgetMilliseconds;
    static int stepOverride, sizeFilter = -1, modeFilter = -1, sequenceFilter = -1;
    static string skipPrefix;
    static Stopwatch watch;
    static readonly List<long> openTimes = new List<long>();

    internal static int Run(string folder) {
        string dir = Path.GetFullPath(folder);
        Directory.CreateDirectory(dir);
        outRoot = dir;
        string passFile = Path.Combine(dir, "large-doc-abuse-probe-PASS.txt");
        string failFile = Path.Combine(dir, "large-doc-abuse-probe-FAIL.txt");
        if (File.Exists(passFile)) File.Delete(passFile);
        if (File.Exists(failFile)) File.Delete(failFile);
        try { string staleCrash = Path.Combine(dir, "large-doc-abuse-probe-crash.txt"); if (File.Exists(staleCrash)) File.Delete(staleCrash); } catch (IOException) { }
        ReadOptions();
        all = true;
        budgetReached = false;
        watch = new Stopwatch();
        watch.Start();
        var runs = new StringBuilder();
        reportDir = dir;
        reportRuns = runs;
        // Writes a partial report if a sequence boundary was reached and the process later dies
        // outside the managed try/finally (the harness saw a CLR unhandled-exception exit code).
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args) {
            try {
                string crash = "CRASH " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) + " " + currentSize + " " + currentMode +
                    " sequence " + currentSeed + " step " + currentStep + " " + currentAction + ":\n" +
                    (args.ExceptionObject == null ? "null" : args.ExceptionObject.ToString());
                File.WriteAllText(Path.Combine(dir, "large-doc-abuse-probe-crash.txt"), crash, new UTF8Encoding(false));
                WriteReportSnapshot("crash");
            } catch (Exception) { }
        };
        bool oldSelf = Program.IsSelfTest;
        bool oldHide = false;
        bool oldLive = ProbeDefaults.Live;
        Dispatcher dispatcher = null;
        DispatcherUnhandledExceptionEventHandler handler = null;
        System.Threading.SynchronizationContext previousContext = System.Threading.SynchronizationContext.Current;
        FixtureInfo small = null, large = null;
        try {
            Program.IsSelfTest = true;
            oldHide = Static<bool>("hideMarkers");
            ProbeDefaults.Live = true;
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            dispatcher = Application.Current.Dispatcher;
            // The app runs under WPF's DispatcherSynchronizationContext: an async continuation after
            // StartLargeSearch's await must return to the UI thread before touching matchCount.Text.
            // Without this the probe's continuation ran on the thread pool and the search crashed
            // the process with a cross-thread InvalidOperationException (the setup MainWindowBench uses).
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            handler = delegate(object sender, DispatcherUnhandledExceptionEventArgs args) { NoteDispatcher(args); };
            dispatcher.UnhandledException += handler;
            Application.Current.DispatcherUnhandledException += handler;
            string fixtureError;
            small = BuildFixture(SmallSize, 101, "300K");
            large = BuildFixture(LargeSize, 202, "3M");
            if (!CheckGenerated(small, out fixtureError)) { all = false; runs.Append("\nFATAL fixture 300K: ").Append(fixtureError).Append('\n'); small = null; }
            if (!CheckGenerated(large, out fixtureError)) { all = false; runs.Append("\nFATAL fixture 3M: ").Append(fixtureError).Append('\n'); large = null; }
            if (small != null) WriteFixtureFile(small);
            if (large != null) WriteFixtureFile(large);
            var fixtures = new FixtureInfo[] { small, small, large, large };
            var lives = new bool[] { false, true, false, true };
            var seeds = new int[] { 11, 12, 13, 14 };
            var steps = new int[] { 70, 70, 45, 45 };
            if (stepOverride > 0) for (int i = 0; i < steps.Length; i++) steps[i] = stepOverride;
            for (int i = 0; i < fixtures.Length; i++) {
                if (sequenceFilter >= 0 && sequenceFilter != i) continue;
                if (fixtures[i] == null) continue;
                if (sizeFilter >= 0 && (fixtures[i].Target == SmallSize ? 0 : 1) != sizeFilter) continue;
                if (modeFilter >= 0 && (lives[i] ? 1 : 0) != modeFilter) continue;
                if (watch.ElapsedMilliseconds > budgetMilliseconds) { budgetReached = true; all = false; break; }
                SequenceResult result = RunSequence(fixtures[i], lives[i], seeds[i], steps[i], i, runs);
                if (!result.Ok) all = false;
                WriteReportSnapshot("after sequence " + i);
                if (budgetReached) break;
            }
        } catch (Exception ex) {
            all = false;
            runs.Append("\nFATAL: ").Append(OneLine(ex.ToString())).Append('\n');
        } finally {
            try { if (dispatcher != null && handler != null) dispatcher.UnhandledException -= handler; } catch (Exception) { }
            try { if (Application.Current != null && handler != null) Application.Current.DispatcherUnhandledException -= handler; } catch (Exception) { }
            try { Static("hideMarkers", oldHide); } catch (Exception) { }
            ProbeDefaults.Live = oldLive;
            Program.IsSelfTest = oldSelf;
            try { System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext); } catch (Exception) { }
            RemoveFixtureFile(small);
            RemoveFixtureFile(large);
            watch.Stop();
        }
        string text = ComposeReport(runs);
        try {
            File.WriteAllText(Path.Combine(dir, "large-doc-abuse-probe.txt"), text, new UTF8Encoding(false));
            if (all && !budgetReached) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
            else File.WriteAllText(failFile, text, new UTF8Encoding(false));
        } catch (IOException) {
            return 1;
        }
        return all && !budgetReached ? 0 : 1;
    }

    static void WriteReportSnapshot(string reason) {
        if (reportDir == null || reportRuns == null) return;
        try {
            string text = ComposeReport(reportRuns);
            File.WriteAllText(Path.Combine(reportDir, "large-doc-abuse-probe.txt"), text + "\nSnapshot: " + reason + " (the run may still be in progress).\n", new UTF8Encoding(false));
        } catch (Exception) { }
    }

    // Optional reduced runs while developing the probe, same shape as the other abuse probes:
    //   MDNOTE_LARGE_DOC_STEPS=20 MDNOTE_LARGE_DOC_SIZE=300k MDNOTE_LARGE_DOC_MODE=live
    //   MDNOTE_LARGE_DOC_SEQUENCE=0..3 MDNOTE_LARGE_DOC_BUDGET=120000 MDNOTE_LARGE_DOC_SKIP=table:
    static void ReadOptions() {
        budgetMilliseconds = DefaultBudgetMilliseconds;
        stepOverride = 0; sizeFilter = -1; modeFilter = -1; sequenceFilter = -1; skipPrefix = null;
        string value = Environment.GetEnvironmentVariable("MDNOTE_LARGE_DOC_STEPS");
        int number;
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) stepOverride = number;
        value = Environment.GetEnvironmentVariable("MDNOTE_LARGE_DOC_BUDGET");
        if (!String.IsNullOrEmpty(value) && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out budgetMilliseconds) && budgetMilliseconds <= 0) budgetMilliseconds = DefaultBudgetMilliseconds;
        value = Environment.GetEnvironmentVariable("MDNOTE_LARGE_DOC_SKIP");
        if (!String.IsNullOrEmpty(value)) skipPrefix = value;
        value = Environment.GetEnvironmentVariable("MDNOTE_LARGE_DOC_SIZE");
        if (!String.IsNullOrEmpty(value)) {
            if (String.Equals(value, "300k", StringComparison.OrdinalIgnoreCase)) sizeFilter = 0;
            else if (String.Equals(value, "3m", StringComparison.OrdinalIgnoreCase)) sizeFilter = 1;
        }
        value = Environment.GetEnvironmentVariable("MDNOTE_LARGE_DOC_MODE");
        if (!String.IsNullOrEmpty(value)) {
            if (String.Equals(value, "basic", StringComparison.OrdinalIgnoreCase)) modeFilter = 0;
            else if (String.Equals(value, "live", StringComparison.OrdinalIgnoreCase)) modeFilter = 1;
        }
        value = Environment.GetEnvironmentVariable("MDNOTE_LARGE_DOC_SEQUENCE");
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number >= 0) sequenceFilter = number;
    }
    // ---------------------------------------------------------------- generated fixtures
    static readonly string[] KoreanSentences = new string[] {
        "그날 밤 우리는 조용히 창가에 앉아 있었다",
        "오래된 노트의 모서리가 손끝에 닿았다",
        "비가 그친 뒤 골목에는 희미한 빛이 남아 있었다",
        "그는 천천히 고개를 들고 멀리 산등성이를 바라보았다",
        "문장은 짧았지만 의미는 오래 남았다",
        "기억은 언제나 예상보다 늦게 도착한다",
        "창문 너머로 바람이 지나가는 소리가 들렸다",
        "우리는 같은 페이지를 여러 번 넘겼다",
        "답장은 없었지만 편지는 계속 쌓였다",
        "새벽의 도시는 낯설 만큼 조용했다",
        "책상 위에는 식은 커피와 접힌 지도가 있었다",
        "내일은 조금 더 천천히 걸어보기로 했다"
    };
    static readonly string[] EnglishSentences = new string[] {
        "The rain had stopped before the first train arrived",
        "She wrote the same sentence twice and crossed it out",
        "A quiet room can hold more noise than a crowded street",
        "He kept the old receipt folded inside the notebook",
        "The map was wrong, but nobody wanted to admit it",
        "Morning light moved slowly across the wooden floor",
        "The station clock was three minutes fast again",
        "Outside the window, the river carried the color of the sky"
    };

    static FixtureInfo BuildFixture(int target, int seed, string name) {
        var fixture = new FixtureInfo();
        fixture.Name = name;
        fixture.Target = target;
        fixture.Newline = "\n";
        string nl = fixture.Newline;
        var text = new StringBuilder(target + LongLineChars + 8192);
        var random = new Random(seed);
        text.Append("# 대용량 검사 문서 ").Append(name).Append(nl).Append(nl);
        text.Append("생성 데이터 문단입니다. 표, 목록, 코드, 한글 ").Append(Emoji).Append(" 이모지가 이어집니다.").Append(nl).Append(nl);
        AppendTableBlock(text, nl, random, fixture);
        int tableSpacing = target >= LargeSize ? 6000 : 300;
        int maxTables = target >= LargeSize ? 460 : 220;
        int nextTableAt = 2000;
        int longAt = target / 4, hugeListAt = target * 3 / 5;
        bool longDone = false, hugeListDone = false;
        int guard = 0;
        while (text.Length < target && guard++ < 200000) {
            if (!longDone && text.Length >= longAt) {
                AppendLongLine(text, nl, random, fixture);
                longDone = true;
                continue;
            }
            if (!hugeListDone && text.Length >= hugeListAt) {
                AppendHugeList(text, nl, random, fixture);
                hugeListDone = true;
                continue;
            }
            if (text.Length >= nextTableAt && fixture.Tables < maxTables) {
                AppendTableBlock(text, nl, random, fixture);
                nextTableAt = text.Length + tableSpacing * (70 + random.Next(61)) / 100;
                continue;
            }
            int roll = random.Next(100);
            if (roll < 38) AppendProse(text, nl, random);
            else if (roll < 58) AppendListBlock(text, nl, random, fixture);
            else if (roll < 74) AppendCodeBlock(text, nl, random, fixture);
            else if (roll < 84) AppendHeading(text, nl, random);
            else if (roll < 94) AppendQuote(text, nl, random);
            else text.Append(nl);
        }
        if (!longDone) {
            text.Append(nl);
            AppendLongLine(text, nl, random, fixture);
        }
        // Distribution gives the 3M document its hundreds of tables; the small document tops up
        // here so its table count is deterministic instead of depending on block sizes.
        int wanted = (target == SmallSize ? SmallTablesMin : LargeTablesMin) + 20;
        while (fixture.Tables < wanted) AppendTableBlock(text, nl, random, fixture);
        fixture.Text = text.ToString();
        fixture.Chars = fixture.Text.Length;
        fixture.Lines = CountLines(fixture.Text);
        fixture.LongLine = LineNumberAt(fixture.Text, fixture.LongLineStart);
        return fixture;
    }

    static void AppendTableBlock(StringBuilder text, string nl, Random random, FixtureInfo fixture) {
        int columns = 3 + random.Next(4);
        text.Append('|');
        for (int c = 0; c < columns; c++) {
            text.Append(' ').Append(c == 0 ? "번호" : c == 1 ? "항목" : c == 2 ? "값" : "열" + (c + 1).ToString(CultureInfo.InvariantCulture)).Append(" |");
        }
        text.Append(nl).Append('|');
        for (int c = 0; c < columns; c++) {
            string rule = c == 1 ? ":---" : c == 2 ? "---:" : c == 3 ? ":---:" : "---";
            text.Append(' ').Append(rule).Append(" |");
        }
        text.Append(nl);
        int rows = 4 + random.Next(9);
        for (int r = 0; r < rows; r++) {
            text.Append('|');
            for (int c = 0; c < columns; c++) {
                text.Append(' ');
                if (c == 0) text.Append(fixture.Tables + 1).Append('-').Append(r + 1);
                else text.Append(CellText(random));
                text.Append(" |");
            }
            text.Append(nl);
        }
        text.Append(nl);
        fixture.Tables++;
        fixture.TableRows += rows;
    }
    static string CellText(Random random) {
        switch (random.Next(9)) {
            case 0: return "값 " + random.Next(10000).ToString(CultureInfo.InvariantCulture);
            case 1: return "한글 셀 " + random.Next(100).ToString(CultureInfo.InvariantCulture);
            case 2: return Emoji + " 이모지";
            case 3: return "`code_" + random.Next(1000).ToString(CultureInfo.InvariantCulture) + "`";
            case 4: return "**굵은** 셀";
            case 5: return "日本語";
            case 6: return "문장 " + random.Next(1000).ToString(CultureInfo.InvariantCulture);
            case 7: return "가나다라";
            default: return "항목";
        }
    }
    static void AppendLongLine(StringBuilder text, string nl, Random random, FixtureInfo fixture) {
        fixture.LongLineStart = text.Length;
        int start = text.Length;
        while (text.Length - start < LongLineChars) {
            switch (random.Next(5)) {
                case 0: text.Append("한글 문장 조각"); break;
                case 1: text.Append("The quick brown fox jumps over the lazy dog"); break;
                case 2: text.Append(Emoji).Append(" 이모지"); break;
                case 3: text.Append("값 ").Append(random.Next(100000).ToString(CultureInfo.InvariantCulture)); break;
                default: text.Append("문단"); break;
            }
            text.Append(' ');
        }
        fixture.LongLineLength = text.Length - start;
        text.Append(nl).Append(nl);
    }
    static void AppendHugeList(StringBuilder text, string nl, Random random, FixtureInfo fixture) {
        text.Append("## 긴 번호 목록").Append(nl).Append(nl);
        for (int i = 1; i <= 250; i++) {
            text.Append(i.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(Sentence(random)).Append(nl);
            fixture.ListItems++;
        }
        text.Append(nl);
    }
    static void AppendListBlock(StringBuilder text, string nl, Random random, FixtureInfo fixture) {
        int items = 5 + random.Next(16);
        for (int i = 0; i < items; i++) {
            if (random.Next(100) < 25) text.Append("  ");
            text.Append(random.Next(3) == 0 ? "- [x] " : random.Next(3) == 0 ? "- [ ] " : "- ").Append(Sentence(random)).Append(nl);
            fixture.ListItems++;
        }
        text.Append(nl);
    }
    static void AppendCodeBlock(StringBuilder text, string nl, Random random, FixtureInfo fixture) {
        string language = random.Next(3) == 0 ? "csharp" : random.Next(2) == 0 ? "text" : "javascript";
        text.Append("```").Append(language).Append(nl);
        int lines = 10 + random.Next(41);
        for (int i = 0; i < lines; i++) {
            text.Append("line ").Append(i + 1).Append(": value_").Append(random.Next(10000).ToString(CultureInfo.InvariantCulture));
            text.Append(" = compute(").Append(random.Next(1000).ToString(CultureInfo.InvariantCulture)).Append("); // ").Append(Sentence(random)).Append(nl);
        }
        text.Append("```").Append(nl).Append(nl);
        fixture.CodeBlocks++;
    }
    static void AppendHeading(StringBuilder text, string nl, Random random) {
        int level = 1 + random.Next(4);
        text.Append(new string('#', level)).Append(' ').Append(Sentence(random)).Append(nl).Append(nl);
    }
    static void AppendQuote(StringBuilder text, string nl, Random random) {
        int lines = 1 + random.Next(3);
        for (int i = 0; i < lines; i++) text.Append("> ").Append(Sentence(random)).Append(nl);
        text.Append(nl);
    }
    static void AppendProse(StringBuilder text, string nl, Random random) {
        int lines = 2 + random.Next(6);
        for (int i = 0; i < lines; i++) text.Append(Sentence(random)).Append(nl);
        text.Append(nl);
    }
    static string Sentence(Random random) {
        string sentence = random.Next(100) < 65 ? KoreanSentences[random.Next(KoreanSentences.Length)] : EnglishSentences[random.Next(EnglishSentences.Length)];
        if (random.Next(100) < 4) sentence = sentence + " " + Emoji;
        return sentence + ".";
    }
    static int CountLines(string text) {
        int count = 1;
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') count++;
        return count;
    }
    static int LineNumberAt(string text, int offset) {
        int line = 1, limit = Math.Min(offset, text.Length);
        for (int i = 0; i < limit; i++) if (text[i] == '\n') line++;
        return line;
    }
    static bool CheckGenerated(FixtureInfo fixture, out string error) {
        error = null;
        int minTables = fixture.Target == SmallSize ? SmallTablesMin : LargeTablesMin;
        if (fixture.Text == null || fixture.Chars < fixture.Target * 9 / 10) {
            error = "generated " + (fixture.Text == null ? 0 : fixture.Chars) + " chars for target " + fixture.Target;
            return false;
        }
        if (fixture.Tables < minTables) { error = "generated " + fixture.Tables + " tables, wanted at least " + minTables; return false; }
        if (fixture.LongLineLength < LongLineChars) { error = "long line is " + fixture.LongLineLength + " chars"; return false; }
        if (fixture.CodeBlocks < 4) { error = "generated " + fixture.CodeBlocks + " code blocks"; return false; }
        if (fixture.ListItems < 100) { error = "generated " + fixture.ListItems + " list items"; return false; }
        if (fixture.Text.IndexOf(Emoji, StringComparison.Ordinal) < 0 || fixture.Text.IndexOf("한글", StringComparison.Ordinal) < 0) {
            error = "fixture lost its Korean/emoji sample";
            return false;
        }
        return true;
    }
    static void WriteFixtureFile(FixtureInfo fixture) {
        fixture.Path = Path.Combine(outRoot, "large-doc-abuse-" + fixture.Name + ".md");
        File.WriteAllText(fixture.Path, fixture.Text, new UTF8Encoding(false));
    }
    static void RemoveFixtureFile(FixtureInfo fixture) {
        if (fixture == null || String.IsNullOrEmpty(fixture.Path)) return;
        try { if (File.Exists(fixture.Path)) File.Delete(fixture.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    // ---------------------------------------------------------------- sequence
    static SequenceResult RunSequence(FixtureInfo fixture, bool liveMode, int seed, int steps, int sequence, StringBuilder runs) {
        var result = new SequenceResult();
        currentFixture = fixture;
        currentSeed = seed;
        currentSequence = sequence;
        currentSize = fixture.Name;
        currentMode = liveMode ? "live" : "basic";
        currentStep = 0;
        currentAction = "open";
        stepNames.Clear();
        dispatcherFailed = false;
        longAnchor = null;
        MainWindow window = null;
        var timer = Stopwatch.StartNew();
        try {
            window = CreateWindow();
            currentWindow = window;
            Static("hideMarkers", !liveMode);
            result.OpenMs = OpenWindow(window, fixture, liveMode);
            AttachChanges(window);
            if (CheckOpen(window, fixture, liveMode, result.OpenMs)) {
                if (FindLongLine(window, fixture)) {
                    int actions = 0, oracles = 0;
                    long undoMs = 0;
                    result.Ok = RunActions(window, seed, liveMode, steps, ref actions, ref undoMs, ref oracles);
                    result.Actions = actions;
                    result.UndoMs = undoMs;
                    result.UndoOracles = oracles;
                } else result.Ok = false;
            } else result.Ok = false;
            try { Invoke(window, "FlushDocumentChanges"); } catch (Exception) { }
        } catch (Exception ex) {
            FailOpen("a:open", "seed exception: " + OneLine(ex.ToString()));
            result.Ok = false;
        } finally {
            currentWindow = null;
            CloseWindow(window);
            timer.Stop();
        }
        result.RunMs = timer.ElapsedMilliseconds;
        result.Surface = "open " + result.OpenMs + " ms, actions " + result.Actions + ", undo oracles " + result.UndoOracles + " (" + result.UndoMs + " ms)";
        openTimes.Add(result.OpenMs);
        int count = CountFailures(seed, currentMode);
        runs.Append("\n## sequence ").Append(sequence).Append(" ").Append(fixture.Name).Append(' ').Append(currentMode).Append('\n');
        runs.Append("- fixture: ").Append(fixture.Chars).Append(" chars, ").Append(fixture.Lines).Append(" lines, ").Append(fixture.Tables)
            .Append(" tables (").Append(fixture.TableRows).Append(" rows), ").Append(fixture.ListItems).Append(" list items, ")
            .Append(fixture.CodeBlocks).Append(" code blocks, long line ").Append(fixture.LongLineLength).Append(" chars on line ").Append(fixture.LongLine).Append('\n');
        runs.Append("- open: ").Append(result.OpenMs).Append(" ms\n");
        runs.Append("- actions: ").Append(result.Actions).Append(" of ").Append(steps).Append('\n');
        runs.Append("- undo oracle: ").Append(result.UndoOracles).Append(" segments, ").Append(result.UndoMs).Append(" ms\n");
        runs.Append("- run time: ").Append(result.RunMs).Append(" ms\n");
        runs.Append("- failures: ").Append(count).Append('\n');
        return result;
    }

    static long OpenWindow(MainWindow window, FixtureInfo fixture, bool liveMode) {
        var openWatch = Stopwatch.StartNew();
        Invoke(window, "LoadFileCore", fixture.Path);
        Pump();
        if (!Field<bool>(window, "editing")) { Invoke(window, "SetMode", true); Pump(); }
        int waited = 0;
        while (waited < 15000) {
            SourceEditor source = Source(window);
            bool ready = source != null && source.Document != null && Field<bool>(window, "editing") && Prop<bool>(window, "LivePreviewAttached");
            if (ready) break;
            Pump(25);
            waited += 25;
        }
        openWatch.Stop();
        if (Static<bool>("hideMarkers") != !liveMode) { Invoke(window, "SetLiveMode", liveMode); Pump(); }
        window.UpdateLayout();
        try { Invoke(window, "FlushDocumentChanges"); } catch (Exception) { }
        Pump();
        return openWatch.ElapsedMilliseconds;
    }

    static bool CheckOpen(MainWindow window, FixtureInfo fixture, bool liveMode, long openMs) {
        SourceEditor source = Source(window);
        bool code = Prop<bool>(window, "CodeMode");
        bool editing = Field<bool>(window, "editing");
        bool attached = Prop<bool>(window, "LivePreviewAttached");
        bool hide = Static<bool>("hideMarkers");
        bool visible = source != null && source.Visibility == Visibility.Visible;
        string surface = "code=" + code + ", editing=" + editing + ", attached=" + attached + ", live=" + (!hide) +
            ", sourceVisible=" + visible + ", document=" + (source == null || source.Document == null ? 0 : source.Document.TextLength);
        bool ok = true;
        if (!code || !editing || !attached || hide != !liveMode || !visible) {
            FailOpen("a:open", "the live edit surface did not attach: " + surface);
            ok = false;
        }
        if (openMs > StepLimitMilliseconds) {
            FailOpen("t:open", "opening " + fixture.Name + " blocked for " + openMs + " ms: " + surface);
            ok = false;
        }
        if (!ok) return false;
        string loaded = DocText(window);
        // Load-time buffer normalization is a documented rule (HANDOFF/current-state b25: the buffer
        // is normalized on load and an unchanged save keeps the original bytes), so the comparison
        // ignores line-ending style but nothing else.
        string wantedText = NormalizeNewlines(fixture.Text), loadedText = NormalizeNewlines(loaded);
        if (!String.Equals(loadedText, wantedText, StringComparison.Ordinal)) {
            FailOpen("open-text", "loaded document differs from the generated file (" + loaded.Length + " vs " + fixture.Text.Length + " chars): " + Diff(wantedText, loadedText));
            return false;
        }
        return true;
    }

    static bool FindLongLine(MainWindow window, FixtureInfo fixture) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) { FailOpen("fixture", "the opened document is gone"); return false; }
        DocumentLine best = null;
        foreach (DocumentLine line in source.Document.Lines) {
            if (line.Length > BlockThreshold && (best == null || line.Length > best.Length)) best = line;
        }
        if (best == null) { FailOpen("fixture", "the loaded document has no line above " + BlockThreshold + " characters"); return false; }
        if (best.Length != fixture.LongLineLength) {
            FailOpen("fixture", "the longest loaded line is " + best.Length + " characters, generated " + fixture.LongLineLength);
            return false;
        }
        longAnchor = source.Document.CreateAnchor(best.Offset + best.Length / 2);
        return true;
    }

    static bool RunActions(MainWindow window, int seed, bool liveMode, int steps, ref int actions, ref long undoMs, ref int oracles) {
        var random = new Random(seed * 104729 + (liveMode ? 7919 : 17));
        DocumentHistory history = HistoryOf(window);
        while (actions < steps) {
            if (watch.ElapsedMilliseconds > budgetMilliseconds) { budgetReached = true; all = false; return false; }
            Invoke(window, "FlushDocumentChanges");
            Pump();
            history.Clear();
            string segmentStart = DocText(window);
            for (int i = 0; i < SegmentLength && actions < steps; i++) {
                if (watch.ElapsedMilliseconds > budgetMilliseconds) { budgetReached = true; all = false; return false; }
                actions++;
                if (!Step(window, random, actions)) return false;
            }
            var undoWatch = Stopwatch.StartNew();
            bool ok = CheckUndoOracle(window, segmentStart);
            undoWatch.Stop();
            undoMs += undoWatch.ElapsedMilliseconds;
            oracles++;
            if (!ok) return false;
        }
        return true;
    }

    static bool CheckUndoOracle(MainWindow window, string segmentStart) {
        Bump(checkCounts, "undo");
        try {
            Invoke(window, "FlushDocumentChanges");
            string beforeUndo = DocText(window);
            string mirror = Prop<string>(window, "text");
            if (mirror == null || !String.Equals(mirror, beforeUndo, StringComparison.Ordinal))
                return FailUndo("model", "window.text differs from the editor document at the segment end: " + Diff(beforeUndo, mirror));
            DocumentHistory history = HistoryOf(window);
            int undoSteps = 0;
            while (history.CanUndo && undoSteps < UndoCap) {
                if (watch.ElapsedMilliseconds > budgetMilliseconds) { budgetReached = true; all = false; return false; }
                Invoke(window, "Undo");
                undoSteps++;
                Pump();
            }
            Invoke(window, "FlushDocumentChanges");
            string undone = DocText(window);
            if (!String.Equals(undone, segmentStart, StringComparison.Ordinal))
                return FailUndo("undo", "undo " + undoSteps + " steps did not restore the segment start (" + undone.Length + " vs " + segmentStart.Length + " chars): " + Diff(segmentStart, undone));
            int redoSteps = 0;
            while (redoSteps < undoSteps && redoSteps < UndoCap && history.CanRedo) {
                if (watch.ElapsedMilliseconds > budgetMilliseconds) { budgetReached = true; all = false; return false; }
                Invoke(window, "Redo");
                redoSteps++;
                Pump();
            }
            Invoke(window, "FlushDocumentChanges");
            string redone = DocText(window);
            if (redoSteps != undoSteps) return FailUndo("undo", "redo restored " + redoSteps + " of " + undoSteps + " undo steps");
            if (!String.Equals(redone, beforeUndo, StringComparison.Ordinal))
                return FailUndo("undo", "redo did not restore the segment end (" + redone.Length + " vs " + beforeUndo.Length + " chars): " + Diff(beforeUndo, redone));
            return true;
        } catch (Exception ex) {
            return FailUndo("undo", "history exception: " + ex.GetType().Name + ": " + OneLine(ex.Message));
        }
    }

    static bool Step(MainWindow window, Random random, int step) {
        currentStep = step;
        string action, payload;
        int command;
        ChooseAction(random, out action, out command, out payload);
        var ctx = new StepContext();
        ctx.Window = window;
        ctx.Action = action;
        ctx.Command = command;
        ctx.Payload = payload;
        ctx.Label = Label(action, command, payload);
        ctx.OpKey = ctx.Label;
        try {
            Prepare(window, random, ctx);
        } catch (Exception ex) {
            currentAction = ctx.Label;
            stepNames.Add(step.ToString(CultureInfo.InvariantCulture) + " " + ctx.Label);
            Bump(opCounts, ctx.OpKey);
            return Fail(ctx, "a:prepare", ex.GetType().Name + ": " + OneLine(ex.Message));
        }
        currentAction = ctx.Label;
        stepNames.Add(step.ToString(CultureInfo.InvariantCulture) + " " + ctx.Label);
        Bump(opCounts, ctx.OpKey);
        if (skipPrefix != null && ctx.Label.StartsWith(skipPrefix, StringComparison.Ordinal)) return true;
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return Fail(ctx, "h", "the source editor is gone");
        TextDocument doc = source.Document;
        ctx.PreCaret = source.CaretOffset;
        ctx.PreSelectionStart = source.SelectionStart;
        ctx.PreSelectionLength = source.SelectionLength;
        ctx.PreLength = doc.TextLength;
        ctx.PreLineCount = doc.LineCount;
        int caret = Math.Max(0, Math.Min(ctx.PreCaret, doc.TextLength));
        DocumentLine preLine = doc.GetLineByOffset(caret);
        ctx.PreLineNumber = preLine.LineNumber;
        ctx.PreLineStart = preLine.Offset;
        ctx.PreLineLength = preLine.Length;
        ctx.PreLineDelimiter = preLine.DelimiterLength > 0 ? doc.GetText(preLine.EndOffset, preLine.DelimiterLength) : "";
        if (preLine.Length <= 200000) ctx.PreLineText = doc.GetText(preLine.Offset, preLine.Length);
        ctx.PreLineWasRow = IsTableRowText(ctx.PreLineText);
        if (ctx.PreLineWasRow) ctx.PreCells = CellCount(ctx.PreLineText);
        if (ctx.Action == "table") {
            ctx.PreTable = FindTableBlock(doc, preLine.LineNumber, 800);
            if (ctx.PreTable != null) ctx.PreTableStart = ctx.PreTable.HeaderStart;
        }
        ctx.PreLongLine = preLine.Length > BlockThreshold;
        if (ctx.PreLongLine) CaptureLongSlice(doc, preLine, ctx);
        DocumentLine clickLine = ctx.ClickPrepared ? ctx.ClickRow : null;
        changes.Clear();
        Exception error = null;
        var stepWatch = Stopwatch.StartNew();
        capturing = true;
        try {
            Execute(window, random, ctx);
            Invoke(window, "FlushDocumentChanges");
            Pump();
        } catch (Exception ex) {
            error = ex;
        } finally {
            capturing = false;
            stepWatch.Stop();
        }
        ctx.DurationMs = stepWatch.ElapsedMilliseconds;
        RecordSlowStep(ctx);
        if (error != null) return Fail(ctx, "a:exception", error.GetType().Name + ": " + OneLine(error.Message));
        if (dispatcherFailed) return Fail(ctx, "a:dispatcher", LastDispatcherNote());
        doc = source.Document;
        if (doc == null) return Fail(ctx, "h", "the document was replaced during the step");
        ctx.PostLength = doc.TextLength;
        ctx.PostLineCount = doc.LineCount;
        ctx.PostCaret = source.CaretOffset;
        if (ctx.DurationMs > StepLimitMilliseconds)
            return Fail(ctx, "t", "the step blocked the UI thread for " + ctx.DurationMs + " ms");
        if (!CheckCaretBounds(ctx, source)) return false;
        if (!CheckChangeAccounting(ctx)) return false;
        if (!CheckTextLanded(ctx)) return false;
        if (!CheckTableCommand(ctx, source)) return false;
        if (!CheckTableAfterEdit(ctx, source)) return false;
        if (!CheckEnterInTable(ctx)) return false;
        if (!CheckLongLineText(ctx, source)) return false;
        if (!CheckCaretInCell(ctx, source, clickLine)) return false;
        if (!CheckFindResult(ctx, source)) return false;
        if (!CheckModeSwitch(ctx, source)) return false;
        if (IsCaretMoveAction(ctx.Action) && !CheckCaretVisible(ctx, source)) return false;
        return true;
    }
    // ---------------------------------------------------------------- action choice and preparation
    const int TotalWeight = 128;
    static readonly string[] NavNames = new string[] { "doc-end", "doc-start", "page-down", "page-up", "down", "up", "line-end", "line-start" };
    static readonly string[] TableNames = new string[] {
        "rowadd", "rowremove", "coladd", "colremove", "delete", "paragraph",
        "align-left", "align-center", "align-right", "outdent", "indent"
    };

    static void ChooseAction(Random random, out string action, out int command, out string payload) {
        action = "type";
        command = 0;
        payload = TypingText(random);
        int roll = random.Next(TotalWeight);
        if (roll < 16) { action = "type"; payload = TypingText(random); }
        else if (roll < 22) action = "click";
        else if (roll < 30) action = "backspace";
        else if (roll < 34) action = "delete";
        else if (roll < 42) action = "enter";
        else if (roll < 54) { action = "nav"; command = random.Next(NavNames.Length); }
        else if (roll < 60) action = "scroll";
        else if (roll < 66) action = "jump";
        else if (roll < 70) action = "longline";
        else if (roll < 74) action = "longline-page";
        else if (roll < 84) { action = "table"; command = random.Next(TableNames.Length); }
        else if (roll < 90) { action = "paste"; command = random.Next(10); payload = PasteSnippet(command); }
        else if (roll < 96) action = "find";
        else if (roll < 100) action = "mode";
        else if (roll < 106) action = "undo";
        else if (roll < 110) action = "redo";
        else { action = "type"; payload = TypingText(random); }
    }
    static string Label(string action, int command, string payload) {
        if (action == "type") return "type:" + PayloadKind(payload);
        if (action == "click") return "click:table";
        if (action == "nav") return "nav:" + NavNames[command];
        if (action == "table") return "table:" + TableNames[command];
        if (action == "paste") return "paste:" + command.ToString(CultureInfo.InvariantCulture);
        if (action == "find") return "find:next";
        if (action == "mode") return "mode:switch";
        if (action == "scroll") return "scroll";
        if (action == "jump") return "jump:random";
        if (action == "longline") return "caret:longline";
        if (action == "longline-page") return "caret:longline-page";
        return action;
    }
    static void Prepare(MainWindow window, Random random, StepContext ctx) {
        if (ctx.Action == "type") {
            ctx.Label = "type:" + PayloadKind(ctx.Payload);
            ctx.OpKey = ctx.Label;
            EnsureCaretVisible(window);
            return;
        }
        if (ctx.Action == "paste") {
            ctx.Label = "paste:" + ctx.Command.ToString(CultureInfo.InvariantCulture);
            ctx.OpKey = ctx.Label;
            EnsureCaretVisible(window);
            return;
        }
        if (ctx.Action == "enter" || ctx.Action == "backspace" || ctx.Action == "delete") {
            ctx.OpKey = ctx.Action;
            EnsureCaretVisible(window);
            return;
        }
        if (ctx.Action == "click") {
            ctx.OpKey = "click:table";
            PrepareClick(window, random, ctx);
            return;
        }
        if (ctx.Action == "table") {
            ctx.Label = "table:" + TableNames[ctx.Command];
            ctx.OpKey = ctx.Label;
            PrepareTableCommand(window, random, ctx);
            return;
        }
        if (ctx.Action == "longline-page") {
            ctx.OpKey = "caret:longline-page";
            PrepareLongLinePage(window, random, ctx);
            return;
        }
        if (ctx.Action == "find") {
            ctx.FindBackwards = random.Next(4) == 0;
            ctx.Label = "find:" + (ctx.FindBackwards ? "prev" : "next");
            ctx.OpKey = ctx.Label;
            PrepareFind(window);
            return;
        }
        if (ctx.Action == "mode") {
            ctx.ModeTarget = !Static<bool>("hideMarkers");
            ctx.Label = "mode:" + (ctx.ModeTarget ? "basic" : "live");
            ctx.OpKey = "mode:switch";
            return;
        }
        ctx.OpKey = ctx.Label;
    }
    static void FallbackType(StepContext ctx) {
        ctx.Action = "type";
        ctx.Command = 0;
        ctx.Payload = "가";
        ctx.Label = "type:korean";
        ctx.OpKey = "type:korean";
        EnsureCaretVisible(ctx.Window);
    }
    static void PrepareClick(MainWindow window, Random random, StepContext ctx) {
        SourceEditor source = Source(window);
        MarkdownLivePreview.Session session = Session(window);
        if (source == null || session == null || source.Document == null) { FallbackType(ctx); return; }
        DocumentLine row = FindTableRowNear(source, session, random);
        if (row == null) { FallbackType(ctx); return; }
        MarkdownLivePreview.LineInfo info = session.Info(row);
        if (info == null || info.Cells == null || info.Cells.Count == 0) { FallbackType(ctx); return; }
        ctx.ClickPrepared = true;
        ctx.ClickRow = row;
        ctx.ClickCellIndex = random.Next(info.Cells.Count);
        ctx.ClickSample = random.Next(3);
        source.ScrollToLine(Math.Max(0, row.LineNumber - 1));
        window.UpdateLayout();
        session.View.EnsureVisualLines();
    }
    static void PrepareTableCommand(MainWindow window, Random random, StepContext ctx) {
        SourceEditor source = Source(window);
        MarkdownLivePreview.Session session = Session(window);
        if (source == null || session == null || source.Document == null) { FallbackType(ctx); return; }
        DocumentLine row = FindTableRowNear(source, session, random);
        if (row == null) { FallbackType(ctx); return; }
        MarkdownLivePreview.LineInfo info = session.Info(row);
        if (info == null || info.Cells == null || info.Cells.Count == 0) { FallbackType(ctx); return; }
        int cell = random.Next(info.Cells.Count);
        int start = row.Offset + info.Cells[cell].Start;
        int end = row.Offset + info.Cells[cell].End;
        int at = end > start ? start + random.Next(end - start) : start;
        // Setup: put the target row on screen first (the user scrolled to it), then place the caret
        // in the cell. A one-shot BringCaretToView from an offscreen caret can be invalidated by the
        // asynchronous table layout, which is probe setup, not the command under test.
        source.ScrollToLine(Math.Max(0, row.LineNumber - 1));
        window.UpdateLayout();
        try { session.View.EnsureVisualLines(); } catch (Exception) { }
        source.Select(at, 0);
        Pump();
    }
    static void PrepareFind(MainWindow window) {
        try {
            Border host = Field<Border>(window, "searchHost");
            if (host == null || host.Visibility != Visibility.Visible) { Invoke(window, "ShowSearch"); Pump(); }
            TextBox query = Field<TextBox>(window, "query");
            if (query != null && !String.Equals(query.Text, FindQuery, StringComparison.Ordinal)) {
                query.Text = FindQuery;
                Pump();
            }
        } catch (Exception) { }
    }
    static DocumentLine FindTableRowNear(SourceEditor source, MarkdownLivePreview.Session session, Random random) {
        if (source == null || source.Document == null) return null;
        TextDocument doc = source.Document;
        int count = doc.LineCount;
        if (count <= 0) return null;
        int start = 1 + random.Next(count);
        int limit = Math.Min(count, 400);
        for (int i = 0; i < limit; i++) {
            int number = ((start - 1 + i) % count) + 1;
            DocumentLine line = doc.GetLineByNumber(number);
            MarkdownLivePreview.LineInfo info = session.Info(line);
            if (info != null && info.Table && !info.Delimiter && info.Cells != null && info.Cells.Count > 0) return line;
        }
        return null;
    }
    // Probe setup only: a user typing has the caret on screen. If a scroll left it elsewhere, put it
    // on a visible line first so the step tests the edit, not an offscreen programmatic caret.
    static void EnsureCaretVisible(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        try {
            TextView view = source.TextArea.TextView;
            if (!view.VisualLinesValid) { window.UpdateLayout(); Pump(); }
            if (view.VisualLinesValid && view.VisualLines.Count > 0) {
                int caretLine = source.TextArea.Caret.Line;
                bool visible = false;
                foreach (VisualLine visual in view.VisualLines) {
                    if (caretLine >= visual.FirstDocumentLine.LineNumber && caretLine <= visual.LastDocumentLine.LineNumber) { visible = true; break; }
                }
                if (visible) return;
                DocumentLine target = source.Document.GetLineByNumber(Math.Min(source.Document.LineCount, view.VisualLines[0].FirstDocumentLine.LineNumber + 1));
                source.Select(target.Offset, 0);
                source.TextArea.Caret.BringCaretToView();
            } else {
                source.Select(Math.Min(source.CaretOffset, source.Document.TextLength), 0);
            }
            Pump();
        } catch (Exception) { }
    }
    static string PayloadKind(string payload) {
        if (payload == null || payload.Length == 0) return "none";
        if (payload == Emoji) return "emoji";
        if (payload.IndexOf(Emoji, StringComparison.Ordinal) >= 0) return "emoji-mixed";
        if (payload.IndexOf('|') >= 0) return "pipe";
        if (payload.IndexOf('`') >= 0) return "backtick";
        if (payload.IndexOf('*') >= 0) return "star";
        if (payload.IndexOf('~') >= 0) return "tilde";
        if (payload == " ") return "space";
        for (int i = 0; i < payload.Length; i++) if (payload[i] >= '가' && payload[i] <= '힣') return "korean";
        return "text";
    }
    static string TypingText(Random random) {
        switch (random.Next(12)) {
            case 0: return "가나다";
            case 1: return RandomOf(random, KoreanSyllables, 1 + random.Next(3));
            case 2: return " ";
            case 3: return "|";
            case 4: return "*";
            case 5: return "`";
            case 6: return "~~";
            case 7: return "**";
            case 8: return RandomOf(random, "abcdefghijklmnopqrstuvwxyz0123456789", 1 + random.Next(5));
            case 9: return "日本語";
            case 10: return Emoji;
            default: return "한글 이모지 " + Emoji + " 값";
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

    // ---------------------------------------------------------------- execution
    static void Execute(MainWindow window, Random random, StepContext ctx) {
        if (ctx.Action == "type") { Source(window).TextArea.PerformTextInput(ctx.Payload); return; }
        if (ctx.Action == "click") { ExecuteClick(window, ctx); return; }
        if (ctx.Action == "backspace") {
            int count = 1 + random.Next(20);
            for (int i = 0; i < count; i++) {
                BackspaceOnce(window);
                if ((i & 7) == 7) Pump();
            }
            return;
        }
        if (ctx.Action == "delete") {
            int count = 1 + random.Next(12);
            for (int i = 0; i < count; i++) {
                DeleteOnce(window);
                if ((i & 7) == 7) Pump();
            }
            return;
        }
        if (ctx.Action == "enter") { EnterStep(window); return; }
        if (ctx.Action == "nav") { ExecuteNavigation(window, random, ctx.Command); return; }
        if (ctx.Action == "scroll") { ExecuteScroll(window, random); return; }
        if (ctx.Action == "jump") { ExecuteJump(window, random); return; }
        if (ctx.Action == "longline") { ExecuteLongLineCaret(window, random); return; }
        if (ctx.Action == "longline-page") { ExecuteLongLinePage(window, random); return; }
        if (ctx.Action == "table") { Invoke(window, "EditTable", ctx.Command); return; }
        if (ctx.Action == "paste") { Invoke(window, "LivePaste", new System.Windows.DataObject(), true, ctx.Payload); return; }
        if (ctx.Action == "find") { ExecuteFind(window, ctx); return; }
        // SetLiveMode takes the live flag and writes hideMarkers = !live (LiveMarkdownHost.cs);
        // ModeTarget is the desired hideMarkers value.
        if (ctx.Action == "mode") { Invoke(window, "SetLiveMode", !ctx.ModeTarget); return; }
        if (ctx.Action == "undo") { Invoke(window, "Undo"); return; }
        if (ctx.Action == "redo") { Invoke(window, "Redo"); return; }
        Source(window).TextArea.PerformTextInput("가");
    }
    static void ExecuteClick(MainWindow window, StepContext ctx) {
        SourceEditor source = Source(window);
        MarkdownLivePreview.Session session = Session(window);
        if (source == null || session == null || source.Document == null || !ctx.ClickPrepared) return;
        DocumentLine row = ctx.ClickRow;
        if (row == null || row.IsDeleted) return;
        window.UpdateLayout();
        session.View.EnsureVisualLines();
        VisualLine visual = session.View.GetOrConstructVisualLine(row);
        Rect box = session.TableCellBox(visual, ctx.ClickCellIndex);
        bool placed = false;
        if (!box.IsEmpty && box.Width > 2 && box.Height > 2) {
            double x = box.Left + box.Width * (ctx.ClickSample == 0 ? 0.04 : ctx.ClickSample == 1 ? 0.5 : 0.96);
            if (x > box.Right - 0.5) x = box.Right - 0.5;
            if (x < box.Left + 0.5) x = box.Left + 0.5;
            double y = ctx.ClickSample == 0 ? box.Top + Math.Min(box.Height * 0.25, 3) : box.Top + box.Height * 0.5;
            session.BeginPress();
            try { placed = session.PlaceTableCaret(new Point(x, y), false); }
            finally { session.EndPress(); }
        }
        if (placed) return;
        MarkdownLivePreview.LineInfo info = session.Info(row);
        if (info == null || info.Cells == null || info.Cells.Count == 0) return;
        int cell = Math.Min(ctx.ClickCellIndex, info.Cells.Count - 1);
        int start = row.Offset + info.Cells[cell].Start;
        int end = row.Offset + info.Cells[cell].End;
        int at = ctx.ClickSample == 0 ? start : ctx.ClickSample == 1 ? (start + end) / 2 : end;
        source.Select(at, 0);
    }
    static void ExecuteNavigation(MainWindow window, Random random, int command) {
        SourceEditor source = Source(window);
        if (source == null) return;
        var area = source.TextArea;
        if (command == 0) { EditingCommands.MoveToDocumentEnd.Execute(null, area); return; }
        if (command == 1) { EditingCommands.MoveToDocumentStart.Execute(null, area); return; }
        if (command == 2) {
            int presses = 2 + random.Next(3);
            for (int i = 0; i < presses; i++) { EditingCommands.MoveDownByPage.Execute(null, area); Pump(); }
            return;
        }
        if (command == 3) {
            int presses = 2 + random.Next(3);
            for (int i = 0; i < presses; i++) { EditingCommands.MoveUpByPage.Execute(null, area); Pump(); }
            return;
        }
        if (command == 4) { int moves = 1 + random.Next(20); for (int i = 0; i < moves; i++) EditingCommands.MoveDownByLine.Execute(null, area); return; }
        if (command == 5) { int moves = 1 + random.Next(20); for (int i = 0; i < moves; i++) EditingCommands.MoveUpByLine.Execute(null, area); return; }
        if (command == 6) { EditingCommands.MoveToLineEnd.Execute(null, area); return; }
        EditingCommands.MoveToLineStart.Execute(null, area);
    }
    static void ExecuteScroll(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        if (source == null) return;
        double maximum = Math.Max(0, source.ExtentHeight - source.ViewportHeight);
        source.ScrollToVerticalOffset(maximum * random.NextDouble());
        Pump();
    }
    static void ExecuteJump(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        double maximum = Math.Max(0, source.ExtentHeight - source.ViewportHeight);
        source.ScrollToVerticalOffset(maximum * random.NextDouble());
        Pump();
        PlaceCaretAtVisiblePoint(window, random);
    }
    // The hit-test half of a click: a point that is on screen in the current layout, resolved by the
    // same TextView.GetPosition the editor's mouse handler uses. This is a user-visible placement,
    // not a synthetic select far off screen.
    static void PlaceCaretAtVisiblePoint(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        TextView view = source.TextArea.TextView;
        window.UpdateLayout();
        view.EnsureVisualLines();
        if (!view.VisualLinesValid || view.VisualLines.Count == 0) return;
        VisualLine visual = view.VisualLines[random.Next(view.VisualLines.Count)];
        double top = view.VerticalOffset, height = ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportHeight;
        double y = Math.Max(top + 2, Math.Min(top + height - 2, visual.VisualTop + visual.Height * 0.5));
        double x = view.HorizontalOffset + Math.Max(4, Math.Min(60, ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportWidth * 0.4));
        ICSharpCode.AvalonEdit.TextViewPosition? position = view.GetPosition(new Point(x, y));
        if (!position.HasValue) return;
        int offset = source.Document.GetOffset(position.Value.Location);
        source.Select(offset, 0);
        // A real press on a point that lands in a row whose top is just above the viewport puts the
        // caret at that row's middle, i.e. above the top edge; the product's own mouse handler then
        // reveals it (SelectionMouseHandler.ExtendSelectionToMouse calls
        // textArea.Caret.BringCaretToView(5.0) right after placing the caret). This placement used the
        // same GetPosition hit test but skipped that reveal, so the check below judged the product
        // against a caret a click would never leave off screen. Use the editor's own reveal here.
        source.TextArea.Caret.BringCaretToView();
        Pump();
    }
    static void PrepareLongLinePage(MainWindow window, Random random, StepContext ctx) {
        SourceEditor source = Source(window);
        if (source == null) { FallbackType(ctx); return; }
        DocumentLine line = LongLineFor(source);
        if (line == null) { FallbackType(ctx); return; }
        PlaceLongLineCaretVisible(window, source, line);
    }
    static void ExecuteLongLinePage(MainWindow window, Random random) {
        int presses = 1 + random.Next(3);
        for (int i = 0; i < presses; i++) {
            EditingCommands.MoveDownByPage.Execute(null, Source(window).TextArea);
            Pump();
        }
    }
    static void PlaceLongLineCaretVisible(MainWindow window, SourceEditor source, DocumentLine line) {
        TextView view = source.TextArea.TextView;
        window.UpdateLayout();
        view.EnsureVisualLines();
        LongLineWindow.Session session = LongLineWindow.Find(source);
        int start = session == null ? -1 : session.WindowStart(line.LineNumber);
        int relative = start >= 0 && start < line.Length ? Math.Min(line.Length - 1, start + 8) : 0;
        // Scroll the line's real window to the viewport centre (the user's scroll), then place the
        // caret at that on-screen character through the same hit test a click uses.
        VisualLine visual = view.GetOrConstructVisualLine(line);
        int column = Math.Max(0, Math.Min(visual.VisualLength, visual.GetVisualColumn(relative)));
        Point documentPoint = visual.GetVisualPosition(column, VisualYPosition.LineMiddle);
        double viewportHeight = ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportHeight;
        source.ScrollToVerticalOffset(Math.Max(0, documentPoint.Y - viewportHeight / 2));
        Pump();
        window.UpdateLayout();
        view.EnsureVisualLines();
        visual = view.GetOrConstructVisualLine(line);
        column = Math.Max(0, Math.Min(visual.VisualLength, visual.GetVisualColumn(relative)));
        documentPoint = visual.GetVisualPosition(column, VisualYPosition.LineMiddle);
        ICSharpCode.AvalonEdit.TextViewPosition? position = view.GetPosition(documentPoint);
        if (position.HasValue) {
            int offset = source.Document.GetOffset(position.Value.Location);
            source.Select(offset, 0);
        }
        Pump();
    }
    static void ExecuteLongLineCaret(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        if (source == null) return;
        DocumentLine line = LongLineFor(source);
        if (line == null) return;
        PlaceLongLineCaretVisible(window, source, line);
    }
    static DocumentLine LongLineFor(SourceEditor source) {
        if (source == null || source.Document == null) return null;
        TextDocument doc = source.Document;
        if (longAnchor != null && !longAnchor.IsDeleted && longAnchor.Offset >= 0 && longAnchor.Offset <= doc.TextLength) {
            DocumentLine anchored = doc.GetLineByOffset(longAnchor.Offset);
            if (anchored.Length > BlockThreshold) return anchored;
        }
        DocumentLine best = null;
        foreach (DocumentLine line in doc.Lines) {
            if (line.Length > BlockThreshold && (best == null || line.Length > best.Length)) best = line;
        }
        if (best != null) longAnchor = doc.CreateAnchor(best.Offset + best.Length / 2);
        return best;
    }
    static void ExecuteFind(MainWindow window, StepContext ctx) {
        Invoke(window, "FindNext", ctx.FindBackwards);
        int waited = 0;
        while (waited < 10000 && !FindSettled(window)) {
            Pump(25);
            waited += 25;
        }
        Pump();
        ctx.FindWaited = waited;
    }
    static bool FindSettled(MainWindow window) {
        try {
            if (!Prop<bool>(window, "LargeSourceSearch")) return true;
            object value = Invoke(window, "LargeSearchCurrent");
            return value is bool && (bool)value;
        } catch (Exception) {
            return true;
        }
    }
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
    // ---------------------------------------------------------------- invariants
    static bool CheckCaretBounds(StepContext ctx, SourceEditor source) {
        Bump(checkCounts, "caret");
        int caret = source.CaretOffset;
        if (caret < 0 || caret > ctx.PostLength)
            return Fail(ctx, "h", "caret " + caret + " is outside the document 0.." + ctx.PostLength);
        int line = source.TextArea.Caret.Line;
        if (line < 1 || line > source.Document.LineCount)
            return Fail(ctx, "h", "caret line " + line + " is outside 1.." + source.Document.LineCount);
        return true;
    }
    static bool CheckChangeAccounting(StepContext ctx) {
        Bump(checkCounts, "text");
        int delta = 0;
        for (int i = 0; i < changes.Count; i++) delta += changes[i].Inserted - changes[i].Removed;
        if (ctx.PostLength != ctx.PreLength + delta)
            return Fail(ctx, "text", "document length changed " + ctx.PreLength + " -> " + ctx.PostLength + " but the recorded changes sum " + delta + " (a change was hidden or the document was replaced)");
        return true;
    }
    static bool CheckTextLanded(StepContext ctx) {
        Bump(checkCounts, "landing");
        if (ctx.Action != "type" && ctx.Action != "paste") return true;
        if (ctx.PreSelectionLength != 0) return true;
        bool inserted = false, atCaret = false;
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            if (change.Inserted <= 0) continue;
            inserted = true;
            if (change.Offset == ctx.PreCaret) atCaret = true;
        }
        if (!atCaret && HiddenEdgeSpaceMove(ctx)) atCaret = true;
        if (!inserted)
            return Fail(ctx, "landing", "the step inserted no text at the pre-step caret " + ctx.PreCaret + ": " + DescribeChanges());
        if (!atCaret)
            return Fail(ctx, "landing", "the insertion did not start at the pre-step caret " + ctx.PreCaret + ": " + DescribeChanges());
        return true;
    }
    // The no-reveal view (Settings.HideMarkers) moves a space typed on an emphasis marker's
    // own edge out of the pair in the same document update, so the pair keeps parsing
    // (MarkdownLivePreview.HiddenCloseTypingEntering: "** ab**" is literal, a tag opener keeps
    // the space inside the tag). The landing rule allows exactly that documented shape: the
    // caret sits at the end of a marker run, the step adds only whitespace on the caret's own
    // line after the caret, and the view is the no-reveal one. Every other relocated insertion
    // still fails.
    static bool HiddenEdgeSpaceMove(StepContext ctx) {
        if (ctx.PreLineText == null || ctx.PreSelectionLength != 0) return false;
        if (!Static<bool>("hideMarkers")) return false;
        bool sawInsert = false;
        int insertedAt = -1, insertedLength = 0;
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            if (change.Removed > 0 || change.Inserted <= 0) return false;
            if (sawInsert) return false;
            string text = change.Text;
            if (text == null || text.Length == 0 || text.Length != change.Inserted) return false;
            for (int c = 0; c < text.Length; c++) if (!Char.IsWhiteSpace(text[c])) return false;
            sawInsert = true;
            insertedAt = change.Offset;
            insertedLength = text.Length;
        }
        if (!sawInsert || insertedAt <= ctx.PreCaret) return false;
        int rel = ctx.PreCaret - ctx.PreLineStart;
        if (rel <= 0 || rel > ctx.PreLineText.Length) return false;
        if (insertedAt + insertedLength > ctx.PreLineStart + ctx.PreLineLength) return false;
        char marker = ctx.PreLineText[rel - 1];
        if (marker != '*' && marker != '~' && marker != '_' && marker != '`') return false;
        int runStart = rel - 1;
        while (runStart > 0 && ctx.PreLineText[runStart - 1] == marker) runStart--;
        // A single "*" is a list bullet or a literal star, not an emphasis opener.
        if (marker != '`') return rel - runStart >= 2;
        return true;
    }
    static bool CheckTableAfterEdit(StepContext ctx, SourceEditor source) {
        if (ctx.Action == "table" || ctx.Action == "undo" || ctx.Action == "redo") return true;
        if (!ctx.PreLineWasRow || ctx.PreLineText == null) return true;
        if (ctx.PreSelectionLength != 0) return true;
        if (ChangesTouchRowSeparator(ctx)) return true;
        Bump(checkCounts, "cells");
        TextDocument doc = source.Document;
        int mapped = MapOffset(changes, ctx.PreCaret);
        if (mapped < 0) mapped = 0;
        if (mapped > doc.TextLength) mapped = doc.TextLength;
        DocumentLine postLine = doc.GetLineByOffset(mapped);
        string postText = GetLineText(doc, postLine);
        if (!IsTableRowText(postText))
            return Fail(ctx, "cells", "the edited table row is not a row any more: was [" + ShortText(ctx.PreLineText, 220) + "], now [" + ShortText(postText, 220) + "]");
        int cells = CellCount(postText);
        if (cells != ctx.PreCells)
            return Fail(ctx, "cells", "the edited row changed from " + ctx.PreCells + " to " + cells + " cells without a pipe or newline edit: was [" +
                ShortText(ctx.PreLineText, 220) + "], now [" + ShortText(postText, 220) + "]");
        TableBlock block = FindTableBlock(doc, postLine.LineNumber, 500);
        if (block == null) return true;
        for (int i = block.Header; i <= block.Last; i++) {
            if (i == block.Delimiter) continue;
            string lineText = GetLineText(doc, doc.GetLineByNumber(i));
            int lineCells = CellCount(lineText);
            if (lineCells != block.HeaderCells)
                return Fail(ctx, "cells", "row " + (i + 1) + " has " + lineCells + " cells, header " + block.HeaderCells + ": [" + ShortText(lineText, 220) + "]");
        }
        return true;
    }
    static bool ChangesTouchRowSeparator(StepContext ctx) {
        int lineEnd = ctx.PreLineStart + ctx.PreLineLength;
        int firstPipe = FirstPipe(ctx.PreLineText);
        int lastPipe = LastPipe(ctx.PreLineText);
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            string text = change.Text;
            if (text != null && (text.IndexOf('|') >= 0 || text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)) return true;
            // The characters a change really removed. Document.Changed reports the caret offset, not
            // the position of the character in the old text: a Delete run at the end of a row removes
            // " |\n| ---" one character at a time from one offset, so the pre-line slice names a '|'
            // for the step that removed the newline and the line merge looked like a cell change. The
            // documented exception (a pipe or newline edit changes separators by user intent) applies
            // to what was removed, which is what this text is.
            string removedText = change.RemovedText;
            if (removedText != null && (removedText.IndexOf('|') >= 0 || removedText.IndexOf('\n') >= 0 || removedText.IndexOf('\r') >= 0)) return true;
            int start = change.Offset, end = change.Offset + change.Removed;
            if (change.Removed > 0 && end >= lineEnd) return true;
            if (change.Removed > 0 && start < ctx.PreLineStart) return true;
            if (change.Removed > 0 && ctx.PreLineText != null && change.Offset >= ctx.PreLineStart && end <= ctx.PreLineStart + ctx.PreLineText.Length) {
                string removed = ctx.PreLineText.Substring(change.Offset - ctx.PreLineStart, change.Removed);
                if (removed.IndexOf('|') >= 0 || removed.IndexOf('\n') >= 0 || removed.IndexOf('\r') >= 0) return true;
            }
            if (start < ctx.PreLineStart + firstPipe) return true;
            if (lastPipe >= 0 && start > ctx.PreLineStart + lastPipe) return true;
        }
        return false;
    }
    static bool CheckEnterInTable(StepContext ctx) {
        if (ctx.Action != "enter" || !ctx.PreLineWasRow || ctx.PreLineText == null) return true;
        Bump(checkCounts, "enter-table");
        SourceEditor source = Source(ctx.Window);
        if (source != null && source.Document != null && source.Document.LineCount != ctx.PreLineCount)
            return Fail(ctx, "enter-table", "Enter inside a table row changed the line count " + ctx.PreLineCount + " -> " + source.Document.LineCount + " (expected the cell <br> edit): " + DescribeChanges());
        for (int i = 0; i < changes.Count; i++) {
            string text = changes[i].Text;
            if (text != null && (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0))
                return Fail(ctx, "enter-table", "Enter inside a table row inserted a line break instead of <br>: " + DescribeChanges());
        }
        return true;
    }
    static bool CheckTableCommand(StepContext ctx, SourceEditor source) {
        if (ctx.Action != "table" || ctx.PreTable == null) return true;
        if (!BlockValid(ctx.PreTable)) return true;
        Bump(checkCounts, "command");
        TextDocument doc = source.Document;
        string label = ctx.Label;
        int mapped = MapOffset(changes, ctx.PreTableStart);
        if (mapped < 0 || mapped > doc.TextLength) {
            if (label == "table:delete") return true;
            return Fail(ctx, "command", "the table header mapped outside the document (" + mapped + ") after " + label);
        }
        DocumentLine headerLine = doc.GetLineByOffset(mapped);
        if (headerLine.Offset != mapped) {
            if (label == "table:delete") return true;
            return Fail(ctx, "command", "the table header merged into the previous line at " + mapped + " (line starts " + headerLine.Offset + ") after " + label);
        }
        TableBlock post = FindTableBlock(doc, headerLine.LineNumber, 3);
        if (post == null || post.Header != headerLine.LineNumber) {
            if (label == "table:delete" || (label == "table:colremove" && ctx.PreTable.HeaderCells <= 1)) return true;
            return Fail(ctx, "command", "the table is gone after " + label + ": line [" + ShortText(GetLineText(doc, headerLine), 220) + "]");
        }
        int rowsBefore = ctx.PreTable.Last - ctx.PreTable.Header + 1;
        int rowsAfter = post.Last - post.Header + 1;
        // RemoveRow is a documented no-op when the caret is on the header or the delimiter row
        // (MarkdownTableCommands.RemoveRow returns null for firstLine/firstLine+1), so those two
        // caret positions keep the row count; a body row still removes exactly one row.
        bool caretOnHeaderOrDelimiter = ctx.PreLineNumber == ctx.PreTable.Header || ctx.PreLineNumber == ctx.PreTable.Delimiter;
        int allowedRows = label == "table:rowadd" ? 1 : label == "table:rowremove" ? (caretOnHeaderOrDelimiter ? 0 : -1) : 0;
        if (rowsAfter - rowsBefore != allowedRows)
            return Fail(ctx, "command", "table rows " + rowsBefore + " -> " + rowsAfter + " after " + label);
        int allowedColumns = label == "table:coladd" ? 1 : label == "table:colremove" ? -1 : 0;
        if (post.HeaderCells - ctx.PreTable.HeaderCells != allowedColumns)
            return Fail(ctx, "command", "table columns " + ctx.PreTable.HeaderCells + " -> " + post.HeaderCells + " after " + label);
        for (int i = post.Header; i <= post.Last; i++) {
            if (i == post.Delimiter) continue;
            string lineText = GetLineText(doc, doc.GetLineByNumber(i));
            int cells = CellCount(lineText);
            if (cells != post.HeaderCells)
                return Fail(ctx, "command", "row " + (i + 1) + " has " + cells + " cells, header " + post.HeaderCells + " after " + label + ": [" + ShortText(lineText, 220) + "]");
        }
        return true;
    }
    static bool CheckLongLineText(StepContext ctx, SourceEditor source) {
        if (ctx.Action == "undo" || ctx.Action == "redo") return true;
        if (!ctx.PreLongLine || ctx.PreLongSlice == null || changes.Count == 0) return true;
        bool intersects = false;
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            if (change.Offset <= ctx.PreLongSliceEnd && change.Offset + change.Removed >= ctx.PreLongSliceStart) { intersects = true; break; }
        }
        if (!intersects) return true;
        int postStart;
        bool overlap;
        string expected = ApplyChangesToSlice(ctx, out postStart, out overlap);
        if (overlap || expected == null) {
            Bump(checkCounts, "longline-skip");
            return true;
        }
        Bump(checkCounts, "longline");
        TextDocument doc = source.Document;
        if (postStart < 0 || postStart + expected.Length > doc.TextLength)
            return Fail(ctx, "long-line", "the edited line's expected slice " + postStart + "+" + expected.Length + " is outside the document " + doc.TextLength);
        string got = doc.GetText(postStart, expected.Length);
        if (!String.Equals(got, expected, StringComparison.Ordinal))
            return Fail(ctx, "long-line", "the edited line's full text changed (expected " + expected.Length + " chars at " + postStart + ", got " + got.Length + "): " + Diff(expected, got));
        return true;
    }
    static bool CheckCaretInCell(StepContext ctx, SourceEditor source, DocumentLine clickLine) {
        if (ctx.Action != "click") return true;
        Bump(checkCounts, "cell-caret");
        TextDocument doc = source.Document;
        int caret = source.CaretOffset;
        DocumentLine line = doc.GetLineByOffset(caret);
        string text = GetLineText(doc, line);
        if (!IsTableRowText(text)) return true;
        var pipes = new List<int>();
        ScanPipes(text, pipes);
        if (pipes.Count == 0) return true;
        int relative = caret - line.Offset;
        int first = pipes[0], lastPipe = pipes[pipes.Count - 1];
        bool leading = true, trailing = true;
        for (int p = 0; p < first; p++) if (text[p] != ' ' && text[p] != '\t') { leading = false; break; }
        for (int p = lastPipe + 1; p < text.Length; p++) if (text[p] != ' ' && text[p] != '\t') { trailing = false; break; }
        int indent = 0;
        while (indent < text.Length && (text[indent] == ' ' || text[indent] == '\t')) indent++;
        bool safe = true;
        if (leading) {
            if (indent > 0 && relative <= indent) safe = true;
            else if (relative <= first) safe = false;
        }
        if (trailing && relative > lastPipe) safe = false;
        if (!safe)
            return Fail(ctx, "h", "click placed the caret " + relative + " outside the cell span (leading pipe " + first + ", trailing pipe " + lastPipe + ", length " + text.Length + "): [" + ShortText(text, 220) + "]");
        return true;
    }
    static bool CheckFindResult(StepContext ctx, SourceEditor source) {
        if (ctx.Action != "find") return true;
        Bump(checkCounts, "find");
        if (ctx.FindWaited >= 10000) return Fail(ctx, "find", "the large-source search did not settle within " + ctx.FindWaited + " ms");
        int length = source.SelectionLength;
        int start = source.SelectionStart;
        if (length <= 0) return Fail(ctx, "find", "find next selected nothing for [" + FindQuery + "]");
        if (start < 0 || start + length > source.Document.TextLength)
            return Fail(ctx, "find", "find next selected an invalid range " + start + "+" + length);
        string selected = source.Document.GetText(start, length);
        if (!String.Equals(selected, FindQuery, StringComparison.OrdinalIgnoreCase))
            return Fail(ctx, "find", "find next selected [" + ShortText(selected, 80) + "] instead of [" + FindQuery + "]");
        return true;
    }
    static bool CheckModeSwitch(StepContext ctx, SourceEditor source) {
        if (ctx.Action != "mode") return true;
        Bump(checkCounts, "mode");
        bool hide = Static<bool>("hideMarkers");
        if (hide != ctx.ModeTarget)
            return Fail(ctx, "mode", "the view mode did not switch: hideMarkers=" + hide + ", wanted " + ctx.ModeTarget);
        if (!Prop<bool>(ctx.Window, "LivePreviewAttached"))
            return Fail(ctx, "mode", "the live preview detached on the view-mode switch");
        if (ctx.PostLength != ctx.PreLength || changes.Count != 0)
            return Fail(ctx, "mode", "the view-mode switch changed the document (" + ctx.PreLength + " -> " + ctx.PostLength + "): " + DescribeChanges());
        return true;
    }
    static bool IsCaretMoveAction(string action) {
        return action == "nav" || action == "click" || action == "jump" || action == "longline" || action == "longline-page" ||
            action == "type" || action == "enter" || action == "backspace" || action == "delete" ||
            action == "paste" || action == "table" || action == "find" || action == "mode";
    }
    static bool CheckCaretVisible(StepContext ctx, SourceEditor source) {
        Bump(checkCounts, "visible");
        try {
            TextView view = source.TextArea.TextView;
            view.EnsureVisualLines();
            DocumentLine line = source.Document.GetLineByNumber(source.TextArea.Caret.Line);
            // The realized visual line is what the user actually sees; GetOrConstruct could build a
            // different line from a stale window state and hide a real on-screen/off-screen answer.
            VisualLine visual = view.GetVisualLine(line.LineNumber);
            if (visual == null || visual.IsDisposed) {
                ctx.Window.UpdateLayout();
                Pump();
                view.EnsureVisualLines();
                visual = view.GetVisualLine(line.LineNumber);
            }
            if (visual == null || visual.IsDisposed)
                return Fail(ctx, "visible", "the caret line " + line.LineNumber + " is not a realized visual line (document lines " + source.Document.LineCount +
                    ", caret " + source.CaretOffset + "/" + source.Document.TextLength + ", vertical " + view.VerticalOffset.ToString("0.0", CultureInfo.InvariantCulture) +
                    ".." + (view.VerticalOffset + ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportHeight).ToString("0.0", CultureInfo.InvariantCulture) +
                    ", extent " + ((System.Windows.Controls.Primitives.IScrollInfo)view).ExtentHeight.ToString("0.0", CultureInfo.InvariantCulture) + RealizedLines(view) + ConstructedTop(source, line) + CaretLineDetail(source, line) + ")");
            // A long line's prefix/suffix folds shift visual columns relative to document columns;
            // the same GetVisualColumn mapping the long-line window uses is the only valid one.
            int relative = source.CaretOffset - line.Offset;
            int column = Math.Max(0, Math.Min(visual.GetVisualColumn(Math.Max(0, Math.Min(relative, line.Length))), visual.VisualLength));
            Point position = visual.GetVisualPosition(column, VisualYPosition.LineMiddle);
            double top = view.VerticalOffset, bottom = view.VerticalOffset + ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportHeight;
            if (position.Y < top - 1 || position.Y > bottom + 1) {
                // One more settle pass, so a reveal queued at Render/Loaded priority is not judged
                // against the layout it just invalidated.
                ctx.Window.UpdateLayout();
                Pump();
                view.EnsureVisualLines();
                line = source.Document.GetLineByNumber(source.TextArea.Caret.Line);
                visual = view.GetVisualLine(line.LineNumber);
                if (visual == null || visual.IsDisposed)
                    return Fail(ctx, "visible", "the caret line " + line.LineNumber + " is not a realized visual line after a settle pass (document lines " + source.Document.LineCount +
                        ", vertical " + view.VerticalOffset.ToString("0.0", CultureInfo.InvariantCulture) + ".." + (view.VerticalOffset + ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportHeight).ToString("0.0", CultureInfo.InvariantCulture) +
                        ", extent " + ((System.Windows.Controls.Primitives.IScrollInfo)view).ExtentHeight.ToString("0.0", CultureInfo.InvariantCulture) + RealizedLines(view) + ConstructedTop(source, line) + CaretLineDetail(source, line) + ")");
                relative = source.CaretOffset - line.Offset;
                column = Math.Max(0, Math.Min(visual.GetVisualColumn(Math.Max(0, Math.Min(relative, line.Length))), visual.VisualLength));
                position = visual.GetVisualPosition(column, VisualYPosition.LineMiddle);
                top = view.VerticalOffset;
                bottom = view.VerticalOffset + ((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportHeight;
            }
            if (position.Y < top - 1 || position.Y > bottom + 1)
                return Fail(ctx, "visible", "the caret line " + line.LineNumber + " is not on screen (caret Y " + position.Y.ToString("0.0", CultureInfo.InvariantCulture) +
                    ", viewport " + top.ToString("0.0", CultureInfo.InvariantCulture) + ".." + bottom.ToString("0.0", CultureInfo.InvariantCulture) +
                    ", visualTop " + visual.VisualTop.ToString("0.0", CultureInfo.InvariantCulture) + ", visualHeight " + visual.Height.ToString("0.0", CultureInfo.InvariantCulture) +
                    ", extent " + ((System.Windows.Controls.Primitives.IScrollInfo)view).ExtentHeight.ToString("0.0", CultureInfo.InvariantCulture) +
                    ", caretColumn " + (source.TextArea.Caret.Column - 1) + CaretLineDetail(source, line, visual) + ")");
            if (line.Length > BlockThreshold && !LongLineWindow.IsRealOffset(source, source.CaretOffset))
                return Fail(ctx, "long-line:caret", "the caret sits in a folded part of the " + line.Length + "-character line at relative " + (source.CaretOffset - line.Offset));
            return true;
        } catch (Exception ex) {
            return Fail(ctx, "visible", "the caret visibility check threw " + ex.GetType().Name + ": " + OneLine(ex.Message));
        }
    }

    static string CaretLineDetail(SourceEditor source, DocumentLine line) {
        return CaretLineDetail(source, line, null);
    }
    // Diagnostic half of the "not a realized visual line" answer: the realized range says whether the
    // viewport sits somewhere else entirely, and the constructed top says where this line really is.
    static string RealizedLines(TextView view) {
        try {
            if (!view.VisualLinesValid) return ", realized invalid";
            if (view.VisualLines.Count == 0) return ", realized none";
            return ", realized " + view.VisualLines[0].FirstDocumentLine.LineNumber + ".." + view.VisualLines[view.VisualLines.Count - 1].LastDocumentLine.LineNumber + " (" + view.VisualLines.Count + ")";
        } catch (Exception ex) { return ", realized threw " + ex.GetType().Name; }
    }
    static string ConstructedTop(SourceEditor source, DocumentLine line) {
        try {
            VisualLine visual = source.TextArea.TextView.GetOrConstructVisualLine(line);
            if (visual == null) return ", construct none";
            return ", constructTop " + visual.VisualTop.ToString("0.0", CultureInfo.InvariantCulture) + "/h" + visual.Height.ToString("0.0", CultureInfo.InvariantCulture);
        } catch (Exception ex) { return ", construct threw " + ex.GetType().Name; }
    }
    static string CaretLineDetail(SourceEditor source, DocumentLine line, VisualLine visual) {
        int relative = source.CaretOffset - line.Offset;
        var text = new StringBuilder();
        text.Append(", line length ").Append(line.Length).Append(", relative ").Append(relative);
        if (line.Length > BlockThreshold) {
            LongLineWindow.Session session = LongLineWindow.Find(source);
            text.Append(", window ").Append(session == null ? "none" : session.WindowStart(line.LineNumber) + ".." + session.WindowEnd(line.LineNumber));
        }
        if (visual != null) {
            foreach (VisualLineElement element in visual.Elements) {
                if (element.DocumentLength <= 0) continue;
                if (relative < element.RelativeTextOffset || relative > element.RelativeTextOffset + element.DocumentLength) continue;
                text.Append(", element ").Append(element.GetType().Name).Append(" rel=").Append(element.RelativeTextOffset)
                    .Append(" docLen=").Append(element.DocumentLength).Append(" visCol=").Append(element.VisualColumn).Append(" visLen=").Append(element.VisualLength);
                LongLineWindow.FoldElement fold = element as LongLineWindow.FoldElement;
                if (fold != null) text.Append(" foldHeight=").Append(fold.EstimatedHeight.ToString("0.0", CultureInfo.InvariantCulture));
                LongLineWindow.HFoldElement horizontal = element as LongLineWindow.HFoldElement;
                if (horizontal != null) text.Append(" foldWidth=").Append(horizontal.EstimatedWidth.ToString("0.0", CultureInfo.InvariantCulture));
                break;
            }
        }
        return text.ToString();
    }

    // ---------------------------------------------------------------- text model
    static string GetLineText(TextDocument doc, DocumentLine line) {
        if (doc == null || line == null) return "";
        return doc.GetText(line.Offset, line.Length);
    }
    static bool IsTableRowText(string text) {
        if (text == null) return false;
        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '|') return false;
        return HasPipe(text);
    }
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
    static int FirstPipe(string text) {
        var pipes = new List<int>();
        ScanPipes(text, pipes);
        return pipes.Count == 0 ? -1 : pipes[0];
    }
    static int LastPipe(string text) {
        var pipes = new List<int>();
        ScanPipes(text, pipes);
        return pipes.Count == 0 ? -1 : pipes[pipes.Count - 1];
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
    static TableBlock FindTableBlock(TextDocument doc, int lineNumber, int maxUp) {
        if (doc == null || lineNumber < 1 || lineNumber > doc.LineCount) return null;
        string text = GetLineText(doc, doc.GetLineByNumber(lineNumber));
        if (!IsTableRowText(text) && !IsDelimiterText(text)) return null;
        int start = Math.Max(1, lineNumber - maxUp);
        int header = -1;
        for (int n = lineNumber; n >= start; n--) {
            string lineText = GetLineText(doc, doc.GetLineByNumber(n));
            if (!IsTableRowText(lineText)) break;
            if (n + 1 <= doc.LineCount) {
                string next = GetLineText(doc, doc.GetLineByNumber(n + 1));
                if (IsDelimiterText(next) && CellCount(lineText) == CellCount(next)) { header = n; break; }
            }
        }
        if (header < 0) return null;
        int last = header + 1;
        while (last + 1 <= doc.LineCount && IsTableRowText(GetLineText(doc, doc.GetLineByNumber(last + 1)))) last++;
        var block = new TableBlock();
        block.Header = header;
        block.Delimiter = header + 1;
        block.Last = last;
        block.HeaderStart = doc.GetLineByNumber(header).Offset;
        block.HeaderCells = CellCount(GetLineText(doc, doc.GetLineByNumber(header)));
        for (int n = header; n <= last; n++) block.Lines.Add(GetLineText(doc, doc.GetLineByNumber(n)));
        block.HeaderText = block.Lines[0];
        block.DelimiterText = block.Lines[1];
        return block;
    }
    static bool BlockValid(TableBlock block) {
        if (block == null || block.Lines.Count < 2) return false;
        for (int i = 1; i < block.Lines.Count; i++) if (CellCount(block.Lines[i]) != block.HeaderCells) return false;
        return true;
    }
    static int MapOffset(List<ChangeInfo> list, int offset) {
        int value = offset;
        for (int i = 0; i < list.Count; i++) {
            ChangeInfo change = list[i];
            int start = change.Offset, end = change.Offset + change.Removed;
            if (value >= end) value += change.Inserted - change.Removed;
            else if (value > start) value = start + Math.Min(value - start, change.Inserted);
        }
        return value;
    }
    static void CaptureLongSlice(TextDocument doc, DocumentLine line, StepContext ctx) {
        int start = line.Offset;
        int end = line.EndOffset + line.DelimiterLength;
        if (line.PreviousLine != null && line.PreviousLine.Length <= 20000) start = line.PreviousLine.Offset;
        if (line.NextLine != null && line.NextLine.Length <= 20000) end = line.NextLine.EndOffset + line.NextLine.DelimiterLength;
        if (end - start > 200000) { start = line.Offset; end = line.EndOffset + line.DelimiterLength; }
        ctx.PreLongSliceStart = start;
        ctx.PreLongSliceEnd = end;
        ctx.PreLongSlice = doc.GetText(start, end - start);
    }
    static string ApplyChangesToSlice(StepContext ctx, out int postStart, out bool overlap) {
        postStart = ctx.PreLongSliceStart;
        overlap = false;
        var local = new StringBuilder(ctx.PreLongSlice);
        int beforeShift = 0;
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            int start = change.Offset, end = change.Offset + change.Removed;
            int sliceStart = ctx.PreLongSliceStart + beforeShift;
            int sliceEnd = ctx.PreLongSliceEnd + beforeShift;
            if (end <= sliceStart) { beforeShift += change.Inserted - change.Removed; continue; }
            if (start >= sliceEnd) continue;
            int localStart = start - sliceStart;
            int localEnd = end - sliceStart;
            if (localStart < 0 || localEnd > local.Length || localStart > localEnd) { overlap = true; return null; }
            local.Remove(localStart, localEnd - localStart);
            if (change.Text != null && change.Text.Length > 0) local.Insert(localStart, change.Text);
        }
        postStart = ctx.PreLongSliceStart + beforeShift;
        return local.ToString();
    }

    // ---------------------------------------------------------------- report
    static string ComposeReport(StringBuilder runs) {
        var text = new StringBuilder();
        text.Append("# LargeDocAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- fixtures: generated here, 300K and 3M characters, hundreds of tables, long lists, code blocks, one 100K-character line, Korean/emoji text; no user file is read\n");
        text.Append("- sequences: 300K basic, 300K live, 3M basic, 3M live; CLI-seeded actions and random positions\n");
        text.Append("- surface: offscreen MainWindow, the app's own open/edit/command/paste/search entry points; the clipboard is never touched\n");
        text.Append("- step limit: ").Append(StepLimitMilliseconds).Append(" ms per action; open is measured separately\n");
        if (stepOverride > 0) text.Append("- reduced run: ").Append(stepOverride).Append(" steps per sequence\n");
        if (sizeFilter >= 0) text.Append("- reduced run: size filter ").Append(sizeFilter == 0 ? "300k" : "3m").Append('\n');
        if (modeFilter >= 0) text.Append("- reduced run: mode filter ").Append(modeFilter == 0 ? "basic" : "live").Append('\n');
        if (sequenceFilter >= 0) text.Append("- reduced run: sequence filter ").Append(sequenceFilter).Append('\n');
        if (skipPrefix != null) text.Append("- skipped actions with prefix: ").Append(skipPrefix).Append('\n');
        text.Append("- runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms")
            .Append(budgetReached ? " (budget reached; run incomplete)" : "").Append('\n');
        text.Append("\n## Invariant applicability (documented product rules)\n");
        text.Append("- Enter inside a table cell writes <br>, not a newline (LiveSyncProbe \"Enter in a table cell writes <br>\"); the enter step therefore requires the line and cell counts to stay.\n");
        text.Append("- A step that types or pastes a literal \"|\" or a newline changes table separators by user intent, so the cell-count check is not applied to that step; a selection replacement is likewise an ordinary edit.\n");
        text.Append("- The caret:longline-page action is setup plus one to three real EditingCommands.MoveDownByPage presses from a visible character of the long line's real window; the placement itself is checked by caret:longline.\n");
        text.Append("- Undo and redo are checked by the full-text segment oracle; the local table and long-line checks are not applied to history steps.\n");
        text.Append("- A table click may place the caret anywhere between the row's leading and trailing pipes; before the leading pipe or after the trailing pipe fails.\n");
        text.Append("- Find drives the product's deferred large-source search; the step waits for LargeSearchCurrent and then requires the selection text to be the query.\n");
        text.Append("- \"Line brought into view\" means the caret line intersects the TextView viewport after ApplicationIdle. Probe setup scrolls for a click/table target and is not counted as the product's own move.\n");
        text.Append("- A change that crosses the long-line context boundary (a merge/split at the neighbouring line edge) is not compared as a local slice; the segment undo oracle still compares the full document.\n");
        text.Append("\n## Probe corrections (justified from documented product rules, not invariant relaxations)\n");
        text.Append("- table:rowremove on the header or delimiter row is a documented no-op (MarkdownTableCommands.RemoveRow returns null for firstLine/firstLine+1). The probe now expects zero rows removed at those two caret positions; a body row still requires exactly one.\n");
        text.Append("- The random-position action used to assign the caret to an offscreen offset and call Caret.BringCaretToView() once. That is not a product command path and its single reveal is invalidated by the asynchronous table layout. It now scrolls to a random offset and places the caret at an on-screen hit-test point (TextView.GetPosition, the seam the mouse handler uses); Ctrl+End/Home and PageDown bursts still run through EditingCommands and keep the same visibility check.\n");
        text.Append("- The long-line placement action now scrolls the line's real window to the viewport centre and sets the caret through TextView.GetPosition at that on-screen point. Assigning a deep offset into the folded spacer and calling BringCaretToView() afterwards was a synthetic path whose reveal the product made stale; the visibility and real-offset checks are unchanged. PageDown/PageUp through a long line remain covered by the nav action's EditingCommands path.\n");
        text.Append("- SetLiveMode(bool live) writes hideMarkers = !live (LiveMarkdownHost.cs). The mode action stored the desired hideMarkers value and passed it straight to SetLiveMode, so Basic never switched to Live; it now passes !ModeTarget. The check still requires hideMarkers to equal the desired value.\n");
        text.Append("- The visibility check passed the caret's document column to VisualLine.GetVisualPosition. In a long line the prefix fold shifts visual columns, so the check computed a wrong (far below) Y for a visible caret. It now maps the document-relative caret with VisualLine.GetVisualColumn, the same mapping LongLineWindow uses; the on-screen test itself is unchanged.\n");
        text.Append("- LiveMarkdownCommands now has SourceEnter() and SourceEnter(bool); the probe's name-only reflection lookup threw AmbiguousMatchException on Enter. Invocation now selects the overload by argument count and assignability (the Enter action uses the parameterless SourceEnter).\n");
        text.Append("- The app's async large-source search resumes after Task.Delay/Task.Run on the WPF DispatcherSynchronizationContext (MainWindowBench installs one). The probe did not, so StartLargeSearch's continuation ran on the thread pool and set matchCount.Text cross-thread (InvalidOperationException; the process exited with 0xE0434352). The probe now installs and restores that context, matching the app.\n");
        text.Append("- The random-position placement resolved an on-screen point with TextView.GetPosition (the mouse handler's hit test) and then set the caret with Select, but skipped the reveal the same mouse handler performs: SelectionMouseHandler.ExtendSelectionToMouse calls textArea.Caret.BringCaretToView(5.0) immediately after placing the caret. A point in a row whose top is just above the viewport put the caret at that row's middle, above the edge, and nothing scrolled it (3M basic jump:random, caret 8 px above the viewport). The placement now calls Caret.BringCaretToView() like that path; the visibility check and every other invariant are unchanged.\n");
        text.Append("- The separator exception for the cell check reads the characters a change really removed (Document.Changed.RemovedText), not a slice of the pre-step line at the change's offset. Document.Changed reports the caret offset, so a Delete run at the end of a row removes \" |\\n| ---\" one character at a time from one offset and the old slice named a pipe for the step that removed the newline; a merged row then read as a lost cell (3M live delete). The documented exception (a pipe or newline edit changes separators by user intent) is unchanged.\n");
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
        text.Append("\n## Step durations\n");
        AppendStepDurations(text);
        text.Append(runs.ToString());
        text.Append("\n## Failures\n");
        if (failures.Count == 0) text.Append("- none\n");
        else {
            foreach (FailureInfo failure in failures) {
                text.Append("\n### ").Append(failure.Size).Append(' ').Append(failure.Mode).Append(" sequence ").Append(failure.Seed)
                    .Append(", step ").Append(failure.Step).Append(' ').Append(failure.Action).Append('\n');
                text.Append("- invariant: ").Append(failure.Invariant).Append('\n');
                text.Append("- detail: ").Append(failure.Detail).Append('\n');
                if (failure.Occurrences > 1) text.Append("- occurrences: ").Append(failure.Occurrences).Append('\n');
                if (failure.DurationMs > 0) text.Append("- step duration: ").Append(failure.DurationMs).Append(" ms\n");
                text.Append("- before:\n").Append(failure.Before).Append('\n');
                text.Append("- after:\n").Append(failure.After).Append('\n');
                text.Append("- steps: ").Append(failure.Steps).Append('\n');
                if (!String.IsNullOrEmpty(failure.Changes)) text.Append("- changes: ").Append(failure.Changes).Append('\n');
            }
            if (omittedFailures > 0) text.Append("\n- ").Append(omittedFailures).Append(" further failures omitted\n");
        }
        text.Append("\nResult: ").Append(all && !budgetReached ? "PASS" : "FAIL").Append('\n');
        text.Append("Runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms\n");
        return text.ToString();
    }
    static void AppendStepDurations(StringBuilder text) {
        long maximum = 0, over = 0;
        for (int i = 0; i < slowSteps.Count; i++) {
            if (slowSteps[i].Ms > maximum) maximum = slowSteps[i].Ms;
            if (slowSteps[i].Ms > StepLimitMilliseconds) over++;
        }
        text.Append("- steps recorded: ").Append(slowSteps.Count).Append(", slowest ").Append(maximum).Append(" ms, over ").Append(StepLimitMilliseconds)
            .Append(" ms: ").Append(over).Append('\n');
        var copy = new List<SlowStep>(slowSteps);
        copy.Sort(delegate(SlowStep a, SlowStep b) {
            int by = b.Ms.CompareTo(a.Ms);
            if (by != 0) return by;
            return String.CompareOrdinal(a.Action, b.Action);
        });
        int shown = Math.Min(MaxSlowSteps, copy.Count);
        if (shown > 0) {
            text.Append("\n| size | mode | sequence | step | action | ms |\n|---|---|---|---:|---|---:|\n");
            for (int i = 0; i < shown; i++) {
                SlowStep slow = copy[i];
                text.Append("| ").Append(slow.Size).Append(" | ").Append(slow.Mode).Append(" | ").Append(slow.Seed).Append(" | ")
                    .Append(slow.Step).Append(" | ").Append(slow.Action).Append(" | ").Append(slow.Ms).Append(" |\n");
            }
        }
        text.Append('\n');
    }
    static void AppendCounts(StringBuilder text, Dictionary<string,int> map) {
        if (map.Count == 0) { text.Append("- (none)\n"); return; }
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys) text.Append("- ").Append(key).Append(": ").Append(map[key]).Append('\n');
    }
    static void RecordSlowStep(StepContext ctx) {
        var entry = new SlowStep();
        entry.Seed = currentSeed;
        entry.Step = currentStep;
        entry.Size = currentSize;
        entry.Mode = currentMode;
        entry.Action = ctx.Label;
        entry.Ms = ctx.DurationMs;
        slowSteps.Add(entry);
        if (slowSteps.Count > 500) slowSteps.RemoveAt(0);
    }
    static string StepListText() {
        if (stepNames.Count == 0) return "(none)";
        int first = Math.Max(0, stepNames.Count - 20);
        return String.Join(" | ", stepNames.GetRange(first, stepNames.Count - first).ToArray());
    }
    static string DescribeChanges() {
        var text = new StringBuilder();
        int shown = 0;
        for (int i = 0; i < changes.Count; i++) {
            ChangeInfo change = changes[i];
            if (shown > 0) text.Append("; ");
            text.Append("@").Append(change.Offset).Append(" -").Append(change.Removed).Append(" +").Append(change.Inserted)
                .Append(" [").Append(OneLine(ShortText(change.Text, 80))).Append(']')
                .Append(change.RemovedText == null ? "" : " minus[" + OneLine(ShortText(change.RemovedText, 80)) + "]");
            shown++;
            if (shown >= 8) { if (changes.Count > 8) text.Append("; ..."); break; }
        }
        return text.ToString();
    }
    static bool Fail(StepContext ctx, string invariant, string detail) {
        Bump(failCounts, invariant);
        all = false;
        string key = invariant + "|" + (ctx == null ? currentAction : ctx.Action) + "|" + ShortText(detail, 120);
        FailureInfo existing;
        if (failureKeys.TryGetValue(key, out existing)) {
            existing.Occurrences++;
            return false;
        }
        if (failures.Count >= MaxFailures) { omittedFailures++; return false; }
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Step = currentStep;
        failure.Size = currentSize;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture == null ? "" : currentFixture.Name;
        failure.Action = ctx == null ? currentAction : ctx.Label;
        failure.Invariant = invariant;
        failure.Detail = OneLine(detail);
        failure.DurationMs = ctx == null ? 0 : ctx.DurationMs;
        failure.Before = LineEvidence(ctx, true);
        failure.After = LineEvidence(ctx, false);
        failure.Steps = StepListText();
        failure.Changes = DescribeChanges();
        failures.Add(failure);
        failureKeys[key] = failure;
        return false;
    }
    static void FailOpen(string invariant, string detail) {
        Bump(failCounts, invariant);
        all = false;
        string key = invariant + "|open|" + ShortText(detail, 120);
        FailureInfo existing;
        if (failureKeys.TryGetValue(key, out existing)) { existing.Occurrences++; return; }
        if (failures.Count >= MaxFailures) { omittedFailures++; return; }
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Step = currentStep;
        failure.Size = currentSize;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture == null ? "" : currentFixture.Name;
        failure.Action = "open";
        failure.Invariant = invariant;
        failure.Detail = OneLine(detail);
        failure.Before = "(none)";
        failure.After = "(none)";
        failure.Steps = StepListText();
        failures.Add(failure);
        failureKeys[key] = failure;
    }
    static bool FailUndo(string invariant, string detail) {
        Bump(failCounts, invariant);
        all = false;
        string key = invariant + "|undo|" + ShortText(detail, 120);
        FailureInfo existing;
        if (failureKeys.TryGetValue(key, out existing)) { existing.Occurrences++; return false; }
        if (failures.Count >= MaxFailures) { omittedFailures++; return false; }
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Step = currentStep;
        failure.Size = currentSize;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture == null ? "" : currentFixture.Name;
        failure.Action = "undo-oracle";
        failure.Invariant = invariant;
        failure.Detail = OneLine(detail);
        failure.Before = "(segment boundary)";
        failure.After = "(segment boundary)";
        failure.Steps = StepListText();
        failures.Add(failure);
        failureKeys[key] = failure;
        return false;
    }
    static int CountFailures(int seed, string mode) {
        int count = 0;
        for (int i = 0; i < failures.Count; i++) if (failures[i].Seed == seed && String.Equals(failures[i].Mode, mode, StringComparison.Ordinal)) count++;
        return count;
    }
    static string LineEvidence(StepContext ctx, bool before) {
        if (ctx == null) return "(none)";
        if (before) {
            string flatBefore = OneLine(ctx.PreLineText);
            if (flatBefore.Length > 700) flatBefore = flatBefore.Substring(0, 700) + "...";
            return ctx.PreLineNumber + "| " + flatBefore;
        }
        SourceEditor source = Source(ctx.Window);
        if (source == null || source.Document == null) return "(post document gone)";
        // The line the caret actually reached: for a jump/command this is the new caret line, not
        // the pre-step caret mapped through the change list.
        int at = source.CaretOffset;
        if (at < 0) at = 0;
        if (at > source.Document.TextLength) at = source.Document.TextLength;
        DocumentLine line = source.Document.GetLineByOffset(at);
        string flat = OneLine(GetLineText(source.Document, line));
        if (flat.Length > 700) flat = flat.Substring(0, 700) + "...";
        return line.LineNumber + "| " + flat;
    }
    static string NormalizeNewlines(string value) {
        if (value == null) return "";
        if (value.IndexOf('\r') < 0) return value;
        return value.Replace("\r\n", "\n").Replace('\r', '\n');
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
    static string ShortText(string value, int limit) {
        if (value == null) return "<null>";
        if (value.Length <= limit) return value;
        return value.Substring(0, limit) + "...";
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
    static void NoteDispatcher(DispatcherUnhandledExceptionEventArgs args) {
        dispatcherFailed = true;
        args.Handled = true;
        if (dispatcherNotes.Count >= 20) return;
        string note = args.Exception == null ? "unknown" : args.Exception.GetType().Name + ": " + OneLine(args.Exception.Message);
        if (args.Exception != null && args.Exception.StackTrace != null) {
            string[] frames = args.Exception.StackTrace.Replace("\r", "").Split('\n');
            note += " @" + OneLine(String.Join(" < ", frames, 0, Math.Min(4, frames.Length)));
        }
        dispatcherNotes.Add("sequence " + currentSeed + " " + currentSize + " " + currentMode + " step " + currentStep + " " + currentAction + ": " + note);
    }
    static string LastDispatcherNote() {
        return dispatcherNotes.Count == 0 ? "unhandled dispatcher exception" : dispatcherNotes[dispatcherNotes.Count - 1];
    }

    // ---------------------------------------------------------------- dispatcher and reflection
    static void Pump() {
        LongLineBench.Idle();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { }));
        LongLineBench.Idle();
    }
    static void Pump(int milliseconds) {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Pump();
    }
    static MainWindow CreateWindow() {
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Width = 900, Height = 700 };
        window.Show();
        window.Left = -20000;
        window.Top = -20000;
        window.UpdateLayout();
        Pump();
        return window;
    }
    static void AttachChanges(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return;
        DetachChanges(window);
        changeHandler = delegate(object sender, DocumentChangeEventArgs args) {
            if (!capturing) return;
            var change = new ChangeInfo();
            change.Offset = args.Offset;
            change.Removed = args.RemovalLength;
            change.Inserted = args.InsertionLength;
            change.Text = args.InsertedText == null ? "" : args.InsertedText.Text;
            if (args.RemovalLength > 0 && args.RemovalLength <= 64 && args.RemovedText != null) change.RemovedText = args.RemovedText.Text;
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
            try { Invoke(window, "CloseSearch"); } catch (Exception) { }
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
        // Overloads are resolved by argument count and assignability: the product now has both
        // SourceEnter() and SourceEnter(bool), and a name-only lookup throws AmbiguousMatchException.
        MethodInfo[] methods = typeof(MainWindow).GetMethods(Members);
        for (int m = 0; m < methods.Length; m++) {
            MethodInfo method = methods[m];
            if (!String.Equals(method.Name, name, StringComparison.Ordinal)) continue;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != args.Length) continue;
            bool matches = true;
            for (int i = 0; i < parameters.Length; i++) {
                if (args[i] == null) continue;
                if (!parameters[i].ParameterType.IsAssignableFrom(args[i].GetType())) { matches = false; break; }
            }
            if (matches) return method.Invoke(window, args);
        }
        throw new MissingMemberException("probe: MainWindow has no method '" + name + "' with " + args.Length + " matching argument(s)");
    }
    static T Static<T>(string name) { return (T)RequireField(name).GetValue(null); }
    static void Static(string name, object value) { RequireField(name).SetValue(null, value); }
}
}
