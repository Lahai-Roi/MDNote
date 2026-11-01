using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// --find-abuse-probe <folder>: seeded find/replace sequences on the window's own search bar,
// in both surfaces (basic: HideMarkers true, live: HideMarkers false). Every sequence drives the
// real entry points (ShowSearch/ShowReplace/FindNext/ReplaceOne/ReplaceAll/Undo/Redo/ToggleTextModeNow)
// and checks the count, navigation order and wrap, selection ranges, replace accounting, table cell
// counts, emphasis balance, one-step undo and highlight bounds. Fixtures are generated here from the
// seed; no user file is read. Report: <folder>\find-abuse-probe.txt, find-abuse-probe-PASS.txt only
// when every check held.
//
// The find bar offers one option besides the query: 대소문자 구분 (matchCase). There is no
// whole-word or regex toggle in this window, so the option matrix is case on/off only.
internal static class FindAbuseProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int FirstSeedDefault = 1, LastSeedDefault = 6;
    const long DefaultBudgetMilliseconds = 270000;
    const int MaxFailures = 200;

    sealed class FailureInfo {
        internal int Seed, Step, Occurrences = 1;
        internal string Mode, Fixture, Action, Query, Options, Invariant, Expected, Actual, Excerpt;
    }
    sealed class BehaviorInfo {
        internal string Key, Detail;
        internal int Count = 1;
    }

    static string outRoot;
    static Stopwatch watch;
    static bool all;
    static bool budgetReached;
    static int currentSeed, currentStep;
    static string currentMode = "", currentFixture = "", currentAction = "open", currentQuery = "", currentOptions = "case=off";
    static MainWindow currentWindow;
    static bool dispatcherFailed;
    static readonly List<string> dispatcherNotes = new List<string>();
    static readonly List<FailureInfo> failures = new List<FailureInfo>();
    static readonly Dictionary<string,FailureInfo> failureKeys = new Dictionary<string,FailureInfo>();
    static readonly List<BehaviorInfo> behaviors = new List<BehaviorInfo>();
    static readonly Dictionary<string,BehaviorInfo> behaviorKeys = new Dictionary<string,BehaviorInfo>();
    static readonly Dictionary<string,int> opCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> checkCounts = new Dictionary<string,int>();
    static readonly Dictionary<string,int> failCounts = new Dictionary<string,int>();
    static int omittedFailures;
    static int firstSeed, lastSeed;
    static long budgetMilliseconds = DefaultBudgetMilliseconds;
    static int modeFilter = -1; // -1 both, 0 basic only, 1 live only

    internal static int Run(string folder) {
        string dir;
        try { dir = Path.GetFullPath(folder); } catch (Exception) { dir = folder; }
        Directory.CreateDirectory(dir);
        outRoot = dir;
        string passFile = Path.Combine(dir, "find-abuse-probe-PASS.txt");
        string failFile = Path.Combine(dir, "find-abuse-probe-FAIL.txt");
        RemoveFile(passFile);
        RemoveFile(failFile);
        ReadOptions();
        // The app runs under WPF's DispatcherSynchronizationContext; without it the large-search
        // continuation resumes on the thread pool and touches the match counter cross-thread.
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
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
            File.WriteAllText(Path.Combine(dir, "find-abuse-probe.txt"), text, new UTF8Encoding(false));
            if (all && !budgetReached) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
            else File.WriteAllText(failFile, text, new UTF8Encoding(false));
        } catch (IOException) {
            return 1;
        }
        return all && !budgetReached ? 0 : 1;
    }

    static void RemoveFile(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }

    // Optional reduced runs: MDNOTE_FIND_ABUSE_SEED=2, MDNOTE_FIND_ABUSE_MODE=live,
    // MDNOTE_FIND_ABUSE_BUDGET=60000.
    static void ReadOptions() {
        firstSeed = FirstSeedDefault; lastSeed = LastSeedDefault;
        budgetMilliseconds = DefaultBudgetMilliseconds; modeFilter = -1;
        string value = Environment.GetEnvironmentVariable("MDNOTE_FIND_ABUSE_SEED");
        int number;
        if (!String.IsNullOrEmpty(value) && Int32.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) && number > 0) {
            firstSeed = number; lastSeed = number;
        }
        value = Environment.GetEnvironmentVariable("MDNOTE_FIND_ABUSE_BUDGET");
        if (!String.IsNullOrEmpty(value) && Int64.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out budgetMilliseconds) && budgetMilliseconds <= 0) budgetMilliseconds = DefaultBudgetMilliseconds;
        value = Environment.GetEnvironmentVariable("MDNOTE_FIND_ABUSE_MODE");
        if (!String.IsNullOrEmpty(value)) {
            if (String.Equals(value, "basic", StringComparison.OrdinalIgnoreCase)) modeFilter = 0;
            else if (String.Equals(value, "live", StringComparison.OrdinalIgnoreCase)) modeFilter = 1;
        }
    }

    static bool RunSequence(int seed, bool liveMode, StringBuilder reportBuilder) {
        currentSeed = seed;
        currentMode = liveMode ? "live" : "basic";
        currentFixture = "f" + (((seed - 1) % 2) + 1).ToString(CultureInfo.InvariantCulture) + "-" + currentMode;
        currentStep = 0;
        currentAction = "open";
        dispatcherFailed = false;
        MainWindow window = null;
        string surface = "(not opened)";
        int checksBefore = TotalChecks(), failuresBefore = failures.Count + omittedFailures;
        var timer = Stopwatch.StartNew();
        bool runOk = true;
        try {
            window = CreateWindow();
            currentWindow = window;
            string document = Fixture(seed);
            window.SetText(document);
            window.SetMode(true);
            Pump();
            Invoke(window, "SetLiveMode", liveMode);
            Pump();
            window.UpdateLayout();
            Invoke(window, "FlushDocumentChanges");
            bool editing = Field<bool>(window, "editing");
            bool attached = Prop<bool>(window, "LivePreviewAttached");
            bool hide = Static<bool>("hideMarkers");
            bool code = Prop<bool>(window, "CodeMode");
            surface = "code=" + code + ", editing=" + editing + ", attached=" + attached +
                ", hideMarkers=" + hide + ", searchText=" + (hide ? "visible" : "source");
            if (!code || !editing || !attached || hide != !liveMode) {
                Fail("open-surface", "editing live surface with hideMarkers=" + (!liveMode), surface, Excerpt(document, 0));
                runOk = false;
            } else {
                var random = new Random(seed * 104729 + (liveMode ? 7919 : 17));
                RunCountScenarios(window, liveMode, random);
                RunNavigationScenarios(window, liveMode, random);
                RunReplaceOneScenarios(window, liveMode, random);
                RunReplaceOneNoSelection(window);
                RunReplaceAllScenarios(window, liveMode, random);
                RunReplaceAllAfterEdit(window);
                RunEditWhileOpen(window, liveMode, random);
                RunSwitchWhileOpen(window, liveMode);
                RunCloseReopen(window, liveMode);
                RunCloseSelection(window);
                RunTabHighlight(window);
                RunLargeHighlight(window);
                RunReplaceComposition(window);
                RunRandomSequence(window, liveMode, random, 12);
                Invoke(window, "CloseSearch");
                Pump();
                try { Invoke(window, "SetLiveMode", liveMode); } catch (Exception) { }
            }
            Invoke(window, "FlushDocumentChanges");
        } catch (Exception ex) {
            Fail("sequence-exception", "no exception", ex.GetType().Name + ": " + OneLine(ex.Message), "(see message)");
            runOk = false;
        } finally {
            currentWindow = null;
            CloseWindow(window);
            timer.Stop();
        }
        int failedHere = failures.Count + omittedFailures - failuresBefore;
        int checksHere = TotalChecks() - checksBefore;
        reportBuilder.Append("\n## seed ").Append(seed).Append(' ').Append(currentMode).Append('\n');
        reportBuilder.Append("- fixture: ").Append(currentFixture).Append('\n');
        reportBuilder.Append("- surface: ").Append(surface).Append('\n');
        reportBuilder.Append("- checks: ").Append(checksHere).Append(", failures: ").Append(failedHere).Append('\n');
        reportBuilder.Append("- run time: ").Append(timer.ElapsedMilliseconds).Append(" ms\n");
        return runOk && failedHere == 0 && !dispatcherFailed;
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
        dispatcherNotes.Add("seed " + currentSeed + " " + currentMode + " step " + currentStep + " " + currentAction + ": " + note);
    }

    static void Fail(string invariant, string expected, string actual, string excerpt) {
        Bump(failCounts, invariant);
        all = false;
        string key = invariant + "|" + currentMode + "|" + currentOptions + "|" + currentQuery + "|" + currentAction + "|" + Short(expected, 80) + "|" + Short(actual, 80);
        FailureInfo existing;
        if (failureKeys.TryGetValue(key, out existing)) { existing.Occurrences++; return; }
        if (failures.Count >= MaxFailures) { omittedFailures++; return; }
        var failure = new FailureInfo();
        failure.Seed = currentSeed;
        failure.Step = currentStep;
        failure.Mode = currentMode;
        failure.Fixture = currentFixture;
        failure.Action = currentAction;
        failure.Query = currentQuery;
        failure.Options = currentOptions;
        failure.Invariant = invariant;
        failure.Expected = OneLine(expected);
        failure.Actual = OneLine(actual);
        failure.Excerpt = ExcerptText(excerpt);
        failures.Add(failure);
        failureKeys[key] = failure;
    }

    static void NoteBehavior(string key, string detail) {
        BehaviorInfo existing;
        if (behaviorKeys.TryGetValue(key, out existing)) { existing.Count++; return; }
        if (behaviors.Count >= 60) return;
        var behavior = new BehaviorInfo();
        behavior.Key = key;
        behavior.Detail = OneLine(detail);
        behaviors.Add(behavior);
        behaviorKeys[key] = behavior;
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
    static SourceEditor Source(MainWindow window) { return Field<SourceEditor>(window, "source"); }
    static MarkdownLivePreview.Session Session(MainWindow window) { return Field<MarkdownLivePreview.Session>(window, "livePreview"); }
    static string DocText(MainWindow window) {
        SourceEditor source = Source(window);
        if (source == null || source.Document == null) return "";
        return source.Document.Text;
    }

    // ---------------------------------------------------------------- text helpers
    static string OneLine(string value) {
        if (value == null) return "<null>";
        string text = value.Replace("\r\n", "\\n").Replace("\r", "\\n").Replace("\n", "\\n");
        return text.Length <= 900 ? text : text.Substring(0, 900) + "...";
    }
    static string Short(string value, int limit) {
        if (value == null) return "<null>";
        if (value.Length <= limit) return value;
        return value.Substring(0, limit) + "...";
    }
    static string ExcerptText(string text) {
        if (text == null) return "(none)";
        string flat = OneLine(text);
        return flat.Length <= 900 ? flat : flat.Substring(0, 900) + "...";
    }
    static string Excerpt(string text, int focus) {
        if (text == null) return "(none)";
        if (focus < 0) focus = 0;
        if (focus > text.Length) focus = text.Length;
        int start = Math.Max(0, focus - 220), end = Math.Min(text.Length, focus + 340);
        string piece = text.Substring(start, end - start);
        return "[" + start + "] " + OneLine(piece);
    }
    static string ContextAt(string text, int at) {
        if (text == null) return "<null>";
        if (at < 0) at = 0;
        if (at > text.Length) at = text.Length;
        int start = Math.Max(0, at - 40), end = Math.Min(text.Length, at + 40);
        return "at " + at + " [" + OneLine(text.Substring(start, end - start)) + "]";
    }
    static string DiffContext(string expected, string actual) {
        if (expected == null) expected = "<null>";
        if (actual == null) actual = "<null>";
        int at = 0, limit = Math.Min(expected.Length, actual.Length);
        while (at < limit && expected[at] == actual[at]) at++;
        return "firstDiff=" + at + " expected " + ContextAt(expected, at) + " actual " + ContextAt(actual, at);
    }
    static void Bump(Dictionary<string,int> map, string key) {
        int value;
        if (!map.TryGetValue(key, out value)) value = 0;
        map[key] = value + 1;
    }
    static int TotalChecks() {
        int total = 0;
        foreach (KeyValuePair<string,int> pair in checkCounts) total += pair.Value;
        return total;
    }
    // ---------------------------------------------------------------- the window's own search bar
    static TextBox QueryBox(MainWindow window) { return Field<TextBox>(window, "query"); }
    static TextBox ReplaceBox(MainWindow window) { return Field<TextBox>(window, "replacement"); }
    static CheckBox CaseBox(MainWindow window) { return Field<CheckBox>(window, "matchCase"); }
    static TextBlock CountBlock(MainWindow window) { return Field<TextBlock>(window, "matchCount"); }
    static TextBlock StatusBlock(MainWindow window) { return Field<TextBlock>(window, "status"); }
    static Button ReplaceOneButton(MainWindow window) { return Field<Button>(window, "replaceOneButton"); }
    static Button ReplaceAllButton(MainWindow window) { return Field<Button>(window, "replaceAllButton"); }
    static Border SearchHost(MainWindow window) { return Field<Border>(window, "searchHost"); }
    static StackPanel ReplaceRow(MainWindow window) { return Field<StackPanel>(window, "replaceRow"); }
    static bool BarOpen(MainWindow window) { return SearchHost(window) != null && SearchHost(window).Visibility == Visibility.Visible; }
    static bool ReplaceRowOpen(MainWindow window) { return ReplaceRow(window) != null && ReplaceRow(window).Visibility == Visibility.Visible; }
    static string QueryTextOf(MainWindow window) { return (string)Invoke(window, "QueryText"); }
    static StringComparison ComparisonOf(MainWindow window) {
        return CaseBox(window).IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
    }
    static void TypeQuery(MainWindow window, string query, bool caseOn) {
        currentQuery = query == null ? "" : query;
        currentOptions = "case=" + (caseOn ? "on" : "off");
        CheckBox caseBox = CaseBox(window);
        if (caseBox.IsChecked != caseOn) caseBox.IsChecked = caseOn;
        TextBox box = QueryBox(window);
        box.Text = query == null ? "" : query;
        box.CaretIndex = box.Text.Length;
        Pump();
    }
    static void SetReplacement(MainWindow window, string value) {
        TextBox box = ReplaceBox(window);
        box.Text = value == null ? "" : value;
        box.CaretIndex = box.Text.Length;
    }
    static void OpenFind(MainWindow window) { Invoke(window, "ShowSearch"); Pump(); }
    static void OpenReplace(MainWindow window) { Invoke(window, "ShowReplace"); Pump(); }
    static void CloseBar(MainWindow window) { Invoke(window, "CloseSearch"); Pump(); }
    // A real key route without the WPF input manager a probe cannot drive: the device-level
    // KeyEventArgs raised on the element, so the window's tunnelling preview handlers and the
    // element's own KeyDown handlers run exactly as they do for a key press.
    static bool RaiseKey(UIElement target, Key key, bool preview) {
        if (target == null) return false;
        PresentationSource presentation = PresentationSource.FromVisual(target);
        if (presentation == null) return false;
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, presentation, 0, key) { RoutedEvent = preview ? Keyboard.PreviewKeyDownEvent : Keyboard.KeyDownEvent };
        target.RaiseEvent(args);
        return args.Handled;
    }
    static void ResetDocument(MainWindow window, string document) {
        try { Invoke(window, "CloseSearch"); } catch (Exception) { }
        Pump();
        window.SetText(document);
        window.SetMode(true);
        Pump();
        window.UpdateLayout();
        Invoke(window, "FlushDocumentChanges");
        Pump();
    }

    // The text the product's Map() searches: the visible text while the surface hides markup,
    // the source otherwise. The offsets turn a visible index into the source offset.
    static string MapText(MainWindow window, out int[] offsets) {
        offsets = null;
        SourceEditor source = Source(window);
        MarkdownLivePreview.Session session = Session(window);
        bool attached = Prop<bool>(window, "LivePreviewAttached");
        if (session != null && !session.IsDisposed && attached && session.SearchHiddenMarkup) return session.VisibleText(out offsets);
        return source.Document.Text;
    }
    static List<int> MatchList(string text, string needle, StringComparison comparison) {
        var matches = new List<int>();
        if (needle == null || needle.Length == 0) return matches;
        int at = 0;
        while (at <= text.Length - needle.Length) {
            int found = text.IndexOf(needle, at, comparison);
            if (found < 0) break;
            matches.Add(found);
            at = found + needle.Length;
        }
        return matches;
    }
    static int VisibleIndex(int[] offsets, int sourceOffset) {
        if (offsets == null) return sourceOffset;
        int low = 0, high = offsets.Length, mid;
        while (low < high) {
            mid = (low + high) >> 1;
            if (offsets[mid] < sourceOffset) low = mid + 1;
            else high = mid;
        }
        return low;
    }
    // The source range of one search hit, exactly as Search.cs SourceVisibleRange computes it.
    static bool MatchSourceRange(int[] offsets, List<int> matches, int index, int queryLength, out int start, out int length) {
        start = 0; length = 0;
        if (matches == null || index < 0 || index >= matches.Count) return false;
        int at = matches[index];
        if (offsets == null) { start = at; length = queryLength; return true; }
        if (at < 0 || at >= offsets.Length) return false;
        int last = Math.Min(offsets.Length - 1, at + Math.Max(1, queryLength) - 1);
        start = offsets[at];
        length = offsets[last] + 1 - start;
        return length > 0;
    }
    static bool ParseCount(string value, out int current, out int total) {
        current = -1; total = -1;
        if (value == null) return false;
        int slash = value.IndexOf('/');
        if (slash < 0) return false;
        string left = value.Substring(0, slash).Trim(), right = value.Substring(slash + 1).Trim();
        return Int32.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out current) &&
            Int32.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out total);
    }
    static bool ParseStatus(string value, out int replaced, out int skipped) {
        replaced = -1; skipped = 0;
        if (String.IsNullOrEmpty(value)) return false;
        int at = 0;
        while (at < value.Length && value[at] >= '0' && value[at] <= '9') at++;
        if (at == 0) return false;
        if (!Int32.TryParse(value.Substring(0, at), NumberStyles.Integer, CultureInfo.InvariantCulture, out replaced)) return false;
        int marker = value.IndexOf("건너뛰", StringComparison.Ordinal);
        if (marker > 0) {
            // "N개를 바꿨습니다. M개는 구조에 걸려 건너뛰었습니다." - the skipped number sits before 개는.
            int countMarker = value.LastIndexOf("개는", marker, StringComparison.Ordinal);
            int tail = countMarker > 0 ? countMarker : marker;
            int end = tail;
            while (end > 0 && (value[end - 1] == ' ' || value[end - 1] == ',')) end--;
            int begin = end;
            while (begin > 0 && ((value[begin - 1] >= '0' && value[begin - 1] <= '9') || value[begin - 1] == ',')) begin--;
            string digits = value.Substring(begin, end - begin).Replace(",", "");
            Int32.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out skipped);
        }
        return true;
    }
    // ---------------------------------------------------------------- count, selection and highlight
    static void CheckCountAndHighlight(string label) {
        MainWindow window = currentWindow;
        if (window == null || !BarOpen(window)) return;
        string query = QueryTextOf(window);
        if (String.IsNullOrEmpty(query)) {
            string countText = CountBlock(window).Text;
            Check("count-empty", countText == "0 / 0", "0 / 0", countText, "empty query");
            Check("replace-buttons-empty", !ReplaceOneButton(window).IsEnabled && !ReplaceAllButton(window).IsEnabled,
                "both disabled", "one=" + ReplaceOneButton(window).IsEnabled + ", all=" + ReplaceAllButton(window).IsEnabled, "empty query");
            CheckHighlightBounds(label);
            return;
        }
        int[] offsets;
        string text = MapText(window, out offsets);
        StringComparison comparison = ComparisonOf(window);
        List<int> matches = MatchList(text, query, comparison);
        int reportedCurrent, reportedTotal;
        bool parsed = ParseCount(CountBlock(window).Text, out reportedCurrent, out reportedTotal);
        int focus = matches.Count > 0 ? matches[0] : 0;
        Check("count-total", parsed && reportedTotal == matches.Count,
            matches.Count.ToString(CultureInfo.InvariantCulture), CountBlock(window).Text, Excerpt(text, focus));
        SourceEditor source = Source(window);
        int selectionStart = source.SelectionStart, selectionLength = source.SelectionLength;
        int expectedCurrent = 0;
        if (selectionLength > 0) {
            int mapStart = VisibleIndex(offsets, selectionStart);
            int mapEnd = VisibleIndex(offsets, selectionStart + selectionLength);
            if (mapEnd - mapStart == query.Length && mapStart >= 0 && mapStart + query.Length <= text.Length) {
                StringComparison atComparison = comparison == StringComparison.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                if (String.Compare(text, mapStart, query, 0, query.Length, atComparison) == 0) {
                    int found = matches.BinarySearch(mapStart);
                    if (found >= 0) expectedCurrent = found + 1;
                }
            }
        }
        Check("count-current", parsed && reportedCurrent == expectedCurrent,
            expectedCurrent + " / " + matches.Count, CountBlock(window).Text, Excerpt(DocText(window), source.SelectionStart));
        bool wantEnabled = matches.Count > 0 && Field<bool>(window, "editing");
        Check("replace-buttons", wantEnabled == (ReplaceOneButton(window).IsEnabled && ReplaceAllButton(window).IsEnabled),
            wantEnabled ? "both enabled" : "both disabled",
            "one=" + ReplaceOneButton(window).IsEnabled + ", all=" + ReplaceAllButton(window).IsEnabled, Excerpt(text, focus));
        SearchHighlight highlight = Field<SearchHighlight>(window, "sourceHighlight");
        if (expectedCurrent > 0 && matches.Count > 0) {
            int start, length;
            MatchSourceRange(offsets, matches, expectedCurrent - 1, query.Length, out start, out length);
            string actual = highlight == null ? "<no highlight>" : "start=" + highlight.SourceStart + ", length=" + highlight.SourceLength;
            Check("highlight-current", highlight != null && highlight.SourceStart == start && highlight.SourceLength == length,
                "start=" + start + ", length=" + length, actual, Excerpt(DocText(window), start));
        } else if (matches.Count > 0) {
            int extras = ExtraCount(highlight);
            Check("highlight-extras", extras == Math.Min(matches.Count, 4000),
                Math.Min(matches.Count, 4000).ToString(CultureInfo.InvariantCulture), extras.ToString(CultureInfo.InvariantCulture), Excerpt(text, focus));
        }
        CheckHighlightBounds(label);
    }
    static readonly FieldInfo ExtraStartsField = typeof(SearchHighlight).GetField("extraStarts", Members);
    static readonly FieldInfo ExtraLengthsField = typeof(SearchHighlight).GetField("extraLengths", Members);
    static int ExtraCount(SearchHighlight highlight) {
        if (highlight == null) return -1;
        var starts = ExtraStartsField.GetValue(highlight) as List<int>;
        return starts == null ? -1 : starts.Count;
    }
    static void CheckHighlightBounds(string label) {
        MainWindow window = currentWindow;
        if (window == null) return;
        SearchHighlight highlight = Field<SearchHighlight>(window, "sourceHighlight");
        if (highlight == null) return;
        int documentLength = DocText(window).Length;
        if (highlight.SourceStart >= 0) {
            Check("highlight-inside", highlight.SourceLength > 0 && highlight.SourceStart + highlight.SourceLength <= documentLength,
                "inside 0.." + documentLength, "start=" + highlight.SourceStart + ", length=" + highlight.SourceLength, "(highlight " + label + ")");
        }
        var starts = ExtraStartsField.GetValue(highlight) as List<int>;
        var lengths = ExtraLengthsField.GetValue(highlight) as List<int>;
        if (starts == null || lengths == null) return;
        int count = Math.Min(starts.Count, lengths.Count);
        for (int i = 0; i < count; i++) {
            if (starts[i] < 0 || lengths[i] <= 0 || starts[i] + lengths[i] > documentLength) {
                Check("highlight-extra-inside", false, "inside 0.." + documentLength,
                    "extra " + i + " start=" + starts[i] + ", length=" + lengths[i] + " of " + count, "(highlight " + label + ")");
                break;
            }
        }
        if (count > 0) Check("highlight-extra-inside", true, "inside", "inside", "(highlight " + label + ")");
    }

    // ---------------------------------------------------------------- navigation order and wrap
    static void CheckNavigation(string label, string query, bool caseOn) {
        currentAction = "nav:" + label;
        MainWindow window = currentWindow;
        SourceEditor source = Source(window);
        int[] offsets;
        string text = MapText(window, out offsets);
        List<int> matches = MatchList(text, query, ComparisonOf(window));
        if (matches.Count <= 0) {
            NoteBehavior("nav-no-match:" + query, "query=[" + query + "] had no match, navigation not swept");
            CheckCountAndHighlight(label);
            return;
        }
        if (matches.Count > 60) {
            NoteBehavior("nav-too-many:" + query, "query=[" + query + "] matched " + matches.Count + " times, navigation not swept");
            return;
        }
        source.Select(0, 0);
        Pump();
        for (int i = 0; i < matches.Count; i++) {
            Invoke(window, "FindNext", false);
            Pump();
            int start, length;
            MatchSourceRange(offsets, matches, i, query.Length, out start, out length);
            Check("nav-next-order", source.SelectionStart == start && source.SelectionLength == length,
                "hit " + i + " source=" + start + "," + length,
                "selection=" + source.SelectionStart + "," + source.SelectionLength, Excerpt(DocText(window), start));
            CheckCountAndHighlight(label + "-next-" + i);
        }
        Invoke(window, "FindNext", false);
        Pump();
        int wrapStart, wrapLength;
        MatchSourceRange(offsets, matches, 0, query.Length, out wrapStart, out wrapLength);
        Check("nav-next-wrap", source.SelectionStart == wrapStart && source.SelectionLength == wrapLength,
            "wrap to " + wrapStart + "," + wrapLength, "selection=" + source.SelectionStart + "," + source.SelectionLength,
            Excerpt(DocText(window), wrapStart));
        Invoke(window, "FindNext", true);
        Pump();
        int lastStart, lastLength;
        MatchSourceRange(offsets, matches, matches.Count - 1, query.Length, out lastStart, out lastLength);
        Check("nav-previous-wrap", source.SelectionStart == lastStart && source.SelectionLength == lastLength,
            "wrap back to " + lastStart + "," + lastLength, "selection=" + source.SelectionStart + "," + source.SelectionLength,
            Excerpt(DocText(window), lastStart));
        for (int i = matches.Count - 2; i >= 0; i--) {
            Invoke(window, "FindNext", true);
            Pump();
            int start, length;
            MatchSourceRange(offsets, matches, i, query.Length, out start, out length);
            Check("nav-previous-order", source.SelectionStart == start && source.SelectionLength == length,
                "hit " + i + " source=" + start + "," + length,
                "selection=" + source.SelectionStart + "," + source.SelectionLength, Excerpt(DocText(window), start));
        }
        Invoke(window, "FindNext", true);
        Pump();
        Check("nav-previous-wrap-end", source.SelectionStart == lastStart && source.SelectionLength == lastLength,
            "wrap to " + lastStart + "," + lastLength, "selection=" + source.SelectionStart + "," + source.SelectionLength,
            Excerpt(DocText(window), lastStart));
        CheckCountAndHighlight(label + "-after-nav");
    }
    // ---------------------------------------------------------------- count-only scenarios
    static void RunCountScenarios(MainWindow window, bool liveMode, Random random) {
        currentAction = "count";
        OpenReplace(window);
        string[] queries = QuerySet();
        int rotate = random.Next(queries.Length);
        for (int k = 0; k < queries.Length; k++) {
            if (windowBudgetReached()) return;
            currentStep++;
            string query = queries[(k + rotate) % queries.Length];
            bool caseOn = random.Next(4) == 0;
            TypeQuery(window, query, caseOn);
            CheckCountAndHighlight("count-" + k);
        }
        currentStep++;
        TypeQuery(window, "", false);
        CheckCountAndHighlight("count-empty");
        currentStep++;
        TypeQuery(window, new string('x', 400) + "alpha", false);
        CheckCountAndHighlight("count-very-long");
        currentStep++;
        TypeQuery(window, "alpha", true);
        CheckCountAndHighlight("count-case-on");
        currentStep++;
        TypeQuery(window, "ALPHA", true);
        CheckCountAndHighlight("count-case-on-upper");
        currentStep++;
        TypeQuery(window, "ALPHA", false);
        CheckCountAndHighlight("count-case-off-upper");
        currentStep++;
        TypeQuery(window, "alpha", false);
        CheckCountAndHighlight("count-case-off");
        // Typing the query one character at a time: every prefix has its own count and highlight.
        string[] prefixes = new string[] { "a", "al", "alp", "alph", "alpha" };
        for (int i = 0; i < prefixes.Length; i++) {
            if (windowBudgetReached()) return;
            currentStep++;
            TypeQuery(window, prefixes[i], false);
            CheckCountAndHighlight("count-prefix-" + i);
        }
        string[] koreanPrefixes = new string[] { "검", "검색", "검색 " };
        for (int i = 0; i < koreanPrefixes.Length; i++) {
            if (windowBudgetReached()) return;
            currentStep++;
            TypeQuery(window, koreanPrefixes[i], false);
            CheckCountAndHighlight("count-korean-prefix-" + i);
        }
    }

    // ---------------------------------------------------------------- navigation scenarios
    static void RunNavigationScenarios(MainWindow window, bool liveMode, Random random) {
        currentAction = "nav";
        OpenReplace(window);
        string[] candidates = liveMode
            ? new string[] { "alpha", "검색", "셀", "😀", "**", "ab**c", "alpha beta" }
            : new string[] { "alpha", "검색", "셀", "😀", "bc", "ab", "링크", "code", "*", "|", "`", "#" };
        int[] order = Shuffle(random, candidates.Length);
        for (int k = 0; k < 3 && k < order.Length; k++) {
            if (windowBudgetReached()) return;
            currentStep++;
            string query = candidates[order[k]];
            bool caseOn = random.Next(2) == 0;
            currentAction = "nav:" + query;
            TypeQuery(window, query, caseOn);
            CheckNavigation("q" + k, query, caseOn);
        }
    }

    // ---------------------------------------------------------------- replace one
    sealed class ReplaceCase {
        internal string Name, Document, Query, Replacement;
        internal string WrittenReplacement; // null: Replacement lands exactly as typed
    }
    static void RunReplaceOneScenarios(MainWindow window, bool liveMode, Random random) {
        currentAction = "replace-one";
        ReplaceCase[] cases = ReplaceOneCases(liveMode);
        int[] order = Shuffle(random, cases.Length);
        for (int k = 0; k < order.Length; k++) {
            if (windowBudgetReached()) return;
            ReplaceCase item = cases[order[k]];
            currentStep++;
            currentAction = "replace-one:" + item.Name;
            ResetDocument(window, item.Document);
            OpenReplace(window);
            TypeQuery(window, item.Query, random.Next(2) == 0);
            CheckCountAndHighlight("one-" + item.Name + "-pre");
            SourceEditor source = Source(window);
            Invoke(window, "Find");
            Pump();
            if (source.SelectionLength == 0) {
                NoteBehavior("replace-one-no-match:" + item.Name, "query=[" + item.Query + "] document=[" + OneLine(item.Document) + "] had no match");
                continue;
            }
            SetReplacement(window, item.Replacement);
            int[] offsets;
            string text = MapText(window, out offsets);
            List<int> matches = MatchList(text, item.Query, ComparisonOf(window));
            int selectionStart = source.SelectionStart, selectionLength = source.SelectionLength;
            bool visible = offsets != null;
            bool clean = !visible || selectionLength == item.Query.Length;
            string before = DocText(window);
            Invoke(window, "FlushDocumentChanges");
            Invoke(window, "ReplaceOne");
            string status = StatusBlock(window).Text;
            Pump();
            Invoke(window, "FlushDocumentChanges");
            Pump();
            string after = DocText(window);
            string written = item.WrittenReplacement ?? item.Replacement;
            string expected = clean ? Splice(before, selectionStart, selectionLength, written) : "(hidden markup inside the match; product decides)";
            if (clean) {
                Check("replace-one-exact", String.Equals(after, expected, StringComparison.Ordinal), expected, after, DiffContext(expected, after));
            } else if (String.Equals(after, before, StringComparison.Ordinal)) {
                Check("replace-one-structural-refusal", !String.IsNullOrEmpty(status) && status.IndexOf("구조", StringComparison.Ordinal) >= 0,
                    "status names the structure block", status, Excerpt(before, selectionStart));
                NoteBehavior("replace-one-refused:" + currentMode, "query=[" + item.Query + "] selection=[" + OneLine(before.Substring(selectionStart, selectionLength)) + "] left unchanged, status=[" + status + "]");
            } else {
                NoteBehavior("replace-one-marker-edge:" + currentMode,
                    "query=[" + item.Query + "] rep=[" + item.Replacement + "] before=[" + OneLine(before) + "] after=[" + OneLine(after) + "]");
                if (item.Replacement.Length > 0) CheckChangedInside(before, after, selectionStart, selectionStart + selectionLength, "replace-one-scope");
            }
            CheckTables(after, "replace-one");
            if (visible) CheckEmphasis(before, after);
            CheckCountAndHighlight("one-" + item.Name + "-post");
            if (!String.Equals(after, before, StringComparison.Ordinal)) CheckUndoOne(before, after, "one-" + item.Name);
        }
    }
    static ReplaceCase MakeCase(string name, string document, string query, string replacement) {
        var item = new ReplaceCase();
        item.Name = name; item.Document = document; item.Query = query; item.Replacement = replacement;
        return item;
    }
    static ReplaceCase[] ReplaceOneCases(bool liveMode) {
        var list = new List<ReplaceCase>();
        list.Add(MakeCase("plain", "alpha beta alpha\n", "alpha", "X"));
        list.Add(MakeCase("bold-inside", "**alpha** tail\n", "alpha", "X"));
        list.Add(MakeCase("link-text", "[alpha text](https://x.invalid/alpha)\n", "alpha", "X"));
        list.Add(MakeCase("table-cell", "| alpha | b |\n| --- | --- |\n| alpha | beta |\n", "alpha", "Z"));
        list.Add(MakeCase("code-fence", "```\nalpha code\n```\n", "alpha", "Z"));
        list.Add(MakeCase("korean", "한글 검색 구간\n", "검색", "바뀜"));
        list.Add(MakeCase("emoji", "😀 test 😀\n", "😀", "★"));
        // Search.cs ReplacementForMatch escapes a "|" written into a table row as "\|".
        ReplaceCase pipe = MakeCase("table-pipe-replacement", "| a | b |\n| --- | --- |\n| alpha | beta |\n", "alpha", "A|B");
        pipe.WrittenReplacement = "A\\|B";
        list.Add(pipe);
        // bughunt3 tables item 6: inside a closed `code` span the pipe is cell text and stays raw,
        // the same rule MarkdownTableCommands.CellInsertText writes.
        ReplaceCase codePipe = MakeCase("table-code-pipe-replacement", "| `a` | b |\n| --- | --- |\n| c | d |\n", "a", "x|y");
        codePipe.WrittenReplacement = "x|y";
        list.Add(codePipe);
        if (liveMode) {
            list.Add(MakeCase("marker-edge-literal", "**ab**c\n", "ab**c", "XY"));
            list.Add(MakeCase("marker-pair-literal", "**alpha** tail\n", "**alpha**", "X"));
        } else {
            list.Add(MakeCase("marker-edge", "**ab**c\n", "bc", "XY"));
            list.Add(MakeCase("marker-pair", "한글 **검색** 구간\n", "검색 구간", "끝"));
        }
        return list.ToArray();
    }

    // ---------------------------------------------------------------- replace all
    sealed class ReplaceAllCase {
        internal string Name, Document, Query, Replacement;
        internal bool ExactExpected;
        internal string WrittenReplacement; // null: Replacement lands exactly as typed
    }
    // ReplaceOne with no match selected: the product finds the first hit itself and replaces it.
    static void RunReplaceOneNoSelection(MainWindow window) {
        currentAction = "replace-one-no-selection";
        currentStep++;
        ResetDocument(window, "alpha beta alpha\n");
        OpenReplace(window);
        TypeQuery(window, "alpha", false);
        SetReplacement(window, "X");
        CheckCountAndHighlight("one-nosel-pre");
        string before = DocText(window);
        Invoke(window, "FlushDocumentChanges");
        Invoke(window, "ReplaceOne");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        string after = DocText(window);
        string expected = "X beta alpha\n";
        Check("replace-one-no-selection", String.Equals(after, expected, StringComparison.Ordinal), expected, after, DiffContext(expected, after));
        CheckCountAndHighlight("one-nosel-post");
        if (!String.Equals(before, after, StringComparison.Ordinal)) CheckUndoOne(before, after, "one-nosel");
    }

    static ReplaceAllCase MakeAllCase(string name, string document, string query, string replacement, bool exactExpected) {
        var item = new ReplaceAllCase();
        item.Name = name; item.Document = document; item.Query = query; item.Replacement = replacement; item.ExactExpected = exactExpected;
        return item;
    }
    static ReplaceAllCase[] ReplaceAllCases(bool liveMode) {
        var list = new List<ReplaceAllCase>();
        list.Add(MakeAllCase("plain", "alpha alpha alpha\n", "alpha", "X", true));
        list.Add(MakeAllCase("bold-and-plain", "**alpha** and alpha\n", "alpha", "X", true));
        list.Add(MakeAllCase("table-cells", "| alpha | b |\n| --- | --- |\n| alpha | beta |\n", "alpha", "Z", true));
        list.Add(MakeAllCase("code-fence", "```\nalpha code\nalpha tail\n```\n", "alpha", "Z", true));
        list.Add(MakeAllCase("link-text", "[alpha text](https://x.invalid/alpha)\n", "alpha", "X", true));
        list.Add(MakeAllCase("korean", "한글 검색 구간 한글 검색 구간\n", "검색", "바뀜", true));
        list.Add(MakeAllCase("overlap-safe", "alpha alpha\n", "alpha", "alpha alpha", true));
        list.Add(MakeAllCase("empty-plain", "alpha beta alpha\n", "alpha ", "", true));
        list.Add(MakeAllCase("adjacent", "aaaa\n", "aa", "b", true));
        // Same table rule as replace-one: Search.cs ReplacementForMatch writes "\|" in a table row.
        ReplaceAllCase pipe = MakeAllCase("table-pipe-replacement", "| a | b |\n| --- | --- |\n| alpha | beta |\n", "alpha", "A|B", true);
        pipe.WrittenReplacement = "A\\|B";
        list.Add(pipe);
        // A pipe inside a closed `code` span stays raw (tables item 6), in 모두 바꾸기 too.
        ReplaceAllCase codePipe = MakeAllCase("table-code-pipe-replacement", "| `a` | b |\n| --- | --- |\n| c | d |\n", "a", "x|y", true);
        codePipe.WrittenReplacement = "x|y";
        list.Add(codePipe);
        if (!liveMode) list.Add(MakeAllCase("match-across-pipes", "| 가 | 나 |\n| --- | --- |\n", "가 나", "X", false));
        if (liveMode) {
            list.Add(MakeAllCase("marker-edge-literal", "**ab**c\n", "ab**c", "XY", true));
        } else {
            list.Add(MakeAllCase("marker-edge", "**ab**c\n", "bc", "XY", false));
            list.Add(MakeAllCase("empty-replacement", "**alpha** tail\n", "alpha", "", false));
            list.Add(MakeAllCase("marker-pair", "한글 **검색** 구간\n", "검색 구간", "끝", false));
        }
        return list.ToArray();
    }
    static void RunReplaceAllScenarios(MainWindow window, bool liveMode, Random random) {
        currentAction = "replace-all";
        ReplaceAllCase[] cases = ReplaceAllCases(liveMode);
        int[] order = Shuffle(random, cases.Length);
        for (int k = 0; k < order.Length; k++) {
            if (windowBudgetReached()) return;
            ReplaceAllCase item = cases[order[k]];
            currentStep++;
            currentAction = "replace-all:" + item.Name;
            ResetDocument(window, item.Document);
            OpenReplace(window);
            TypeQuery(window, item.Query, random.Next(2) == 0);
            SetReplacement(window, item.Replacement);
            SourceEditor source = Source(window);
            int[] offsets;
            string text = MapText(window, out offsets);
            StringComparison comparison = ComparisonOf(window);
            List<int> matches = MatchList(text, item.Query, comparison);
            var starts = new List<int>(); var lengths = new List<int>(); bool rangesOk = true; bool allClean = true;
            for (int i = 0; i < matches.Count; i++) {
                int start, length;
                if (!MatchSourceRange(offsets, matches, i, item.Query.Length, out start, out length)) { rangesOk = false; break; }
                starts.Add(start); lengths.Add(length);
                if (length != item.Query.Length) allClean = false;
                if (i > 0 && start < starts[i - 1] + lengths[i - 1]) rangesOk = false;
            }
            CheckCountAndHighlight("all-" + item.Name + "-pre");
            string before = DocText(window);
            Invoke(window, "FlushDocumentChanges");
            Invoke(window, "ReplaceAll");
            string status = StatusBlock(window).Text;
            Pump();
            Invoke(window, "FlushDocumentChanges");
            Pump();
            string after = DocText(window);
            int replaced, skipped;
            bool statusOk = ParseStatus(status, out replaced, out skipped);
            if (matches.Count == 0) {
                Check("replace-all-no-match", String.Equals(after, before, StringComparison.Ordinal), "unchanged", after, Excerpt(before, 0));
                NoteBehavior("replace-all-no-match:" + item.Name, "query=[" + item.Query + "] doc=[" + OneLine(item.Document) + "]");
                continue;
            }
            Check("replace-all-accounting", statusOk && replaced + skipped == matches.Count,
                "replaced+skipped=" + matches.Count, "replaced=" + replaced + ", skipped=" + skipped + ", status=[" + status + "]", Excerpt(before, matches[0]));
            bool canExact = item.ExactExpected && rangesOk && (offsets == null || allClean);
            if (canExact) {
                string expected = ApplyMatches(before, starts, lengths, item.WrittenReplacement ?? item.Replacement);
                Check("replace-all-exact", String.Equals(after, expected, StringComparison.Ordinal), expected, after, DiffContext(expected, after));
            } else {
                NoteBehavior("replace-all-hidden:" + currentMode + ":" + item.Name,
                    "query=[" + item.Query + "] rep=[" + item.Replacement + "] replaced=" + replaced + ", skipped=" + skipped + ", after=[" + OneLine(after) + "]");
                if (item.Replacement.Length > 0 && rangesOk) CheckChangedWithinMatches(before, after, starts, lengths, "replace-all-scope");
            }
            if (offsets != null && item.Replacement.Length > 0) CheckEmphasis(before, after);
            CheckTables(after, "replace-all");
            CheckLineCount(before, after);
            CheckCountAndHighlight("all-" + item.Name + "-post");
            if (statusOk && replaced > 0 && !String.Equals(after, before, StringComparison.Ordinal)) CheckUndoOne(before, after, "all-" + item.Name);
        }
    }

    // A replace-all that follows a document edit must still be one undo step of its own.
    static void RunReplaceAllAfterEdit(MainWindow window) {
        currentAction = "replace-all-after-edit";
        currentStep++;
        ResetDocument(window, "alpha alpha\n");
        SourceEditor source = Source(window);
        source.Document.Insert(0, "note ");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        OpenReplace(window);
        TypeQuery(window, "alpha", false);
        SetReplacement(window, "X");
        string before = DocText(window);
        CheckCountAndHighlight("all-after-edit-pre");
        Invoke(window, "ReplaceAll");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        string after = DocText(window);
        string expected = "note X X\n";
        Check("replace-all-after-edit", String.Equals(after, expected, StringComparison.Ordinal), expected, after, DiffContext(expected, after));
        CheckCountAndHighlight("all-after-edit-post");
        CheckUndoOne(before, after, "all-after-edit");
    }

    // ---------------------------------------------------------------- shared structure checks
    static string Splice(string text, int start, int length, string replacement) {
        if (start < 0) start = 0;
        if (start > text.Length) start = text.Length;
        if (length < 0) length = 0;
        if (start + length > text.Length) length = Math.Max(0, text.Length - start);
        if (replacement == null) replacement = "";
        return text.Substring(0, start) + replacement + text.Substring(start + length);
    }
    static string ApplyMatches(string text, List<int> starts, List<int> lengths, string replacement) {
        var builder = new StringBuilder();
        int offset = 0;
        for (int i = 0; i < starts.Count; i++) {
            builder.Append(text, offset, starts[i] - offset);
            builder.Append(replacement);
            offset = starts[i] + lengths[i];
        }
        builder.Append(text, offset, text.Length - offset);
        return builder.ToString();
    }
    static void CheckChangedInside(string before, string after, int rangeStart, int rangeEnd, string invariant) {
        int start, end;
        ChangedSpan(before, after, out start, out end);
        Check(invariant, start >= rangeStart && end <= rangeEnd,
            "changes inside " + rangeStart + ".." + rangeEnd, "changed " + start + ".." + end,
            Excerpt(before, rangeStart));
    }
    static void CheckChangedWithinMatches(string before, string after, List<int> starts, List<int> lengths, string invariant) {
        int start, end;
        ChangedSpan(before, after, out start, out end);
        for (int position = start; position < end; position++) {
            bool inside = false;
            for (int i = 0; i < starts.Count; i++) {
                if (position >= starts[i] && position < starts[i] + lengths[i]) { inside = true; break; }
            }
            if (!inside) {
                Check(invariant, false, "changes only inside match ranges",
                    "changed offset " + position + " lies outside every match range (span " + start + ".." + end + ")",
                    Excerpt(before, position));
                return;
            }
        }
        Check(invariant, true, "inside", "inside", "(changed span " + start + ".." + end + ")");
    }
    static void ChangedSpan(string before, string after, out int start, out int end) {
        int prefix = 0;
        int limit = Math.Min(before.Length, after.Length);
        while (prefix < limit && before[prefix] == after[prefix]) prefix++;
        int suffix = 0;
        while (suffix < limit - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        start = prefix;
        end = before.Length - suffix;
    }
    static List<string> LinesOf(string text) {
        return new List<string>(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
    }
    static int LineOffset(List<string> lines, int index) {
        int offset = 0;
        for (int i = 0; i < index && i < lines.Count; i++) offset += lines[i].Length + 1;
        return offset;
    }
    static List<string> SplitCells(string line) {
        var cells = new List<string>();
        var cell = new StringBuilder();
        bool escaped = false;
        for (int i = 0; i < line.Length; i++) {
            char value = line[i];
            if (escaped) { cell.Append(value); escaped = false; continue; }
            if (value == '\\') { cell.Append(value); escaped = true; continue; }
            // A pipe inside a closed `code` span is cell text, not a separator (the row parser's
            // own rule, MarkdownTableCommands.PipePositions).
            if (value == '`') {
                int end = CodeSpanEnd(line, i);
                if (end > i) { cell.Append(line, i, end - i); i = end - 1; continue; }
            }
            if (value == '|') { cells.Add(cell.ToString()); cell.Length = 0; continue; }
            cell.Append(value);
        }
        cells.Add(cell.ToString());
        if (cells.Count > 0 && cells[0].Trim().Length == 0) cells.RemoveAt(0);
        if (cells.Count > 0 && cells[cells.Count - 1].Trim().Length == 0) cells.RemoveAt(cells.Count - 1);
        return cells;
    }
    // The index after the run that closes the backtick span opening at start, or start when the
    // run has no same-length close.
    static int CodeSpanEnd(string text, int start) {
        int i = start;
        while (i < text.Length && text[i] == '`') i++;
        int run = i - start;
        int j = i;
        while (j < text.Length) {
            if (text[j] != '`') { j++; continue; }
            int close = j;
            while (j < text.Length && text[j] == '`') j++;
            if (j - close == run) return j;
        }
        return start;
    }
    static bool DelimiterCell(string cell) {
        string value = cell.Trim();
        if (value.Length == 0) return false;
        if (value[0] == ':') value = value.Substring(1);
        if (value.Length > 0 && value[value.Length - 1] == ':') value = value.Substring(0, value.Length - 1);
        if (value.Length == 0) return false;
        for (int i = 0; i < value.Length; i++) if (value[i] != '-') return false;
        return true;
    }
    static bool DelimiterRow(string line) {
        List<string> cells = SplitCells(line);
        if (cells.Count == 0) return false;
        for (int i = 0; i < cells.Count; i++) if (!DelimiterCell(cells[i])) return false;
        return true;
    }
    static bool TableRow(string line) { return line.TrimStart().StartsWith("|", StringComparison.Ordinal); }
    static void CheckTables(string text, string label) {
        List<string> lines = LinesOf(text);
        bool fence = false;
        for (int i = 0; i < lines.Count; i++) {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fence = !fence; continue; }
            if (fence || !TableRow(line)) continue;
            if (i + 1 >= lines.Count) continue;
            List<string> header = SplitCells(line);
            List<string> delimiter = SplitCells(lines[i + 1]);
            if (header.Count == 0 || delimiter.Count != header.Count || !DelimiterRow(lines[i + 1])) continue;
            int row = i + 2;
            while (row < lines.Count && TableRow(lines[row])) {
                List<string> cells = SplitCells(lines[row]);
                if (cells.Count != header.Count) {
                    Check("table-cells", false, "every row has " + header.Count + " cells",
                        "line " + (row + 1) + " has " + cells.Count + " cells: " + OneLine(lines[row]),
                        Excerpt(text, LineOffset(lines, row)));
                    return;
                }
                row++;
            }
            i = row - 1;
        }
        Check("table-cells", true, "valid", "valid", "(" + label + ")");
    }
    static void CheckLineCount(string before, string after) {
        int beforeLines = LinesOf(before).Count, afterLines = LinesOf(after).Count;
        Check("line-count", beforeLines == afterLines, "same lines=" + beforeLines, "lines=" + afterLines, Excerpt(after, 0));
    }
    static int Occurrences(string text, string needle) {
        int count = 0, at = 0;
        while (true) {
            at = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (at < 0) break;
            count++;
            at += needle.Length;
        }
        return count;
    }
    static void CheckEmphasis(string before, string after) {
        List<string> beforeLines = LinesOf(before), afterLines = LinesOf(after);
        bool fence = false;
        for (int i = 0; i < afterLines.Count; i++) {
            string line = afterLines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { fence = !fence; continue; }
            if (fence) continue;
            bool changed = i >= beforeLines.Count || !String.Equals(beforeLines[i], line, StringComparison.Ordinal);
            if (!changed || DelimiterRow(line)) continue;
            int stars = Occurrences(line, "**"), tildes = Occurrences(line, "~~"), ticks = Occurrences(line, "`");
            int spanOpen = Occurrences(line, "<span"), spanClose = Occurrences(line, "</span>");
            bool balanced = stars % 2 == 0 && tildes % 2 == 0 && ticks % 2 == 0 && spanOpen == spanClose;
            Check("emphasis-balanced", balanced, "balanced ** ~~ ` <span>",
                "**=" + stars + ", ~~=" + tildes + ", `=" + ticks + ", span=" + spanOpen + "/" + spanClose + ", line=[" + OneLine(line) + "]",
                Excerpt(after, LineOffset(afterLines, i)));
        }
    }
    static void CheckUndoOne(string before, string after, string label) {
        MainWindow window = currentWindow;
        Invoke(window, "Undo");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        string undone = DocText(window);
        Check("undo-one-step", String.Equals(undone, before, StringComparison.Ordinal), before, undone, DiffContext(before, undone));
        Invoke(window, "Redo");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        string redone = DocText(window);
        Check("redo-one-step", String.Equals(redone, after, StringComparison.Ordinal), after, redone, DiffContext(after, redone));
        CheckCountAndHighlight(label + "-after-redo");
    }
    // ---------------------------------------------------------------- document edits while open
    static void RunEditWhileOpen(MainWindow window, bool liveMode, Random random) {
        currentAction = "edit-while-open";
        ResetDocument(window, Fixture(currentSeed));
        OpenReplace(window);
        TypeQuery(window, "alpha", false);
        int beforeTotal = ReportedTotal(window);
        CheckCountAndHighlight("edit-pre");
        SourceEditor source = Source(window);
        string text = DocText(window);
        int insertAt = text.IndexOf("\n\n", StringComparison.Ordinal);
        insertAt = insertAt < 0 ? text.Length : insertAt + 2;
        source.Document.Insert(insertAt, "alpha ");
        Pump(400);
        Invoke(window, "FlushDocumentChanges");
        Pump();
        int insertedTotal = ReportedTotal(window);
        Check("edit-insert-count", insertedTotal == beforeTotal + 1, (beforeTotal + 1).ToString(CultureInfo.InvariantCulture),
            insertedTotal.ToString(CultureInfo.InvariantCulture), Excerpt(DocText(window), insertAt));
        CheckCountAndHighlight("edit-insert");
        // A second edit while the bar is open, then close and reopen before the debounce could run:
        // the reopened bar has to count the current text, not the pre-edit match list.
        source.Document.Insert(insertAt, "alpha ");
        CloseBar(window);
        OpenReplace(window);
        CheckCountAndHighlight("edit-close-open");
        source.Document.Remove(insertAt, 6);
        Pump(400);
        Invoke(window, "FlushDocumentChanges");
        Pump();
        int removeAt = DocText(window).IndexOf("alpha ", insertAt, StringComparison.Ordinal);
        if (removeAt >= 0) {
            source.Document.Remove(removeAt, 6);
            Pump(400);
            Invoke(window, "FlushDocumentChanges");
            Pump();
            int removedTotal = ReportedTotal(window);
            Check("edit-delete-count", removedTotal == beforeTotal, beforeTotal.ToString(CultureInfo.InvariantCulture),
                removedTotal.ToString(CultureInfo.InvariantCulture), Excerpt(DocText(window), Math.Min(removeAt, DocText(window).Length)));
            CheckCountAndHighlight("edit-delete");
        }
        CheckNavigation("edit-nav", "alpha", false);
        CheckHighlightBounds("edit-end");
    }

    // ---------------------------------------------------------------- surface switches while open
    static void RunSwitchWhileOpen(MainWindow window, bool liveMode) {
        currentAction = "switch-while-open";
        ResetDocument(window, Fixture(currentSeed));
        OpenReplace(window);
        TypeQuery(window, "alpha", false);
        CheckCountAndHighlight("switch-pre");
        SourceEditor source = Source(window);
        Invoke(window, "SetMode", false);
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        Check("switch-bar-open-read", BarOpen(window), "bar stays open", "open=" + BarOpen(window), "(reading mode)");
        CheckCountAndHighlight("switch-read");
        source.Select(0, 0);
        Invoke(window, "FindNext", false);
        Pump();
        CheckCountAndHighlight("switch-read-find");
        Invoke(window, "SetMode", true);
        Pump();
        CheckCountAndHighlight("switch-edit");
        Invoke(window, "SetLiveMode", !liveMode);
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        Check("switch-bar-open-live", BarOpen(window), "bar stays open", "open=" + BarOpen(window), "(live mode toggle)");
        CheckCountAndHighlight("switch-live");
        source.Select(0, 0);
        Invoke(window, "FindNext", false);
        Pump();
        CheckCountAndHighlight("switch-live-find");
        Invoke(window, "SetLiveMode", liveMode);
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        CheckCountAndHighlight("switch-live-back");
        bool? oldTextMode = Field<bool?>(window, "textMode");
        Invoke(window, "ToggleTextModeNow");
        Pump(500);
        Invoke(window, "FlushDocumentChanges");
        Pump();
        Check("switch-txt-bar-open", BarOpen(window), "bar stays open", "open=" + BarOpen(window), "(TXT conversion)");
        Check("switch-txt-mode", Field<bool?>(window, "textMode") == true, "textMode=true", "textMode=" + Field<bool?>(window, "textMode"), "(TXT conversion)");
        CheckCountAndHighlight("switch-txt");
        source.Select(0, 0);
        Invoke(window, "FindNext", false);
        Pump();
        CheckCountAndHighlight("switch-txt-find");
        Set(window, "textMode", oldTextMode);
        Invoke(window, "LayoutMode");
        Pump();
        window.SetText(Fixture(currentSeed));
        window.SetMode(true);
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        Invoke(window, "SetLiveMode", liveMode);
        Pump();
    }

    // ---------------------------------------------------------------- close and reopen
    static void RunCloseReopen(MainWindow window, bool liveMode) {
        currentAction = "close-reopen";
        ResetDocument(window, Fixture(currentSeed));
        OpenReplace(window);
        TypeQuery(window, "alpha", true);
        SetReplacement(window, "X");
        CheckCountAndHighlight("reopen-pre");
        string keptQuery = QueryBox(window).Text;
        CloseBar(window);
        SearchHighlight highlight = Field<SearchHighlight>(window, "sourceHighlight");
        Check("close-hides-bar", !BarOpen(window), "bar hidden", "open=" + BarOpen(window), "(after close)");
        Check("close-clears-highlight", highlight == null || (highlight.SourceStart < 0 && ExtraCount(highlight) == 0),
            "no current and no extras", highlight == null ? "<none>" : "start=" + highlight.SourceStart + ", extras=" + ExtraCount(highlight), "(after close)");
        OpenFind(window);
        Check("reopen-find", BarOpen(window) && !ReplaceRowOpen(window), "find bar open, replace row hidden",
            "open=" + BarOpen(window) + ", replaceRow=" + ReplaceRowOpen(window), "(reopen)");
        Check("reopen-query-kept", QueryBox(window).Text == keptQuery, keptQuery, QueryBox(window).Text, "(reopen)");
        CheckCountAndHighlight("reopen-find");
        OpenReplace(window);
        Check("reopen-replace-row", ReplaceRowOpen(window), "replace row visible", "visible=" + ReplaceRowOpen(window), "(reopen)");
        CheckCountAndHighlight("reopen-replace");
    }

    // ---------------------------------------------------------------- closed find hands its match back
    /* A closed find leaves the found text selected (Notepad/Word). The first plain arrow used to
       clear that selection and step once from the caret, which after a find sits at the match's end,
       so Left, Right, Right and the next letters landed after the found word instead of two
       characters into it (real input 2026-10-31). Enter, Esc and the arrow run through the window's
       own key routes: the query box's KeyDown (FindNext), then the window's PreviewKeyDown
       (SearchEscapeKey, SearchSelectionArrowKey). */
    static void RunCloseSelection(MainWindow window) {
        currentAction = "close-selection";
        currentStep++;
        ResetDocument(window, "alpha beta gamma\n");
        try { window.Activate(); } catch (Exception) { }
        Pump(200);
        SourceEditor source = Source(window);
        /* Focus assertions need a real keyboard focus; an offscreen window whose activation the
           desktop refuses records a note instead of failing the product check (WindowAbuseProbe
           does the same for every focused step). */
        source.Focus(); Keyboard.Focus(source.TextArea); Pump(200);
        bool focusAvailable = source.IsKeyboardFocusWithin;
        if (!focusAvailable) NoteBehavior("close-selection-no-focus:" + currentMode, "the window could not take keyboard focus in this run; the focus and arrow checks were skipped");
        OpenFind(window);
        TypeQuery(window, "beta", false);
        TextBox box = QueryBox(window);
        bool entered = RaiseKey(box, Key.Enter, false);
        Pump(200);
        Check("close-selection-enter", entered && source.SelectionStart == 6 && source.SelectionLength == 4 && source.SelectedText == "beta",
            "Enter selects beta at 6,4", "handled=" + entered + ", selection=" + source.SelectionStart + "," + source.SelectionLength + " [" + OneLine(source.SelectedText) + "]", Excerpt(DocText(window), 6));
        bool escaped = RaiseKey(box, Key.Escape, true);
        Pump(200);
        Check("close-selection-esc", !BarOpen(window) && escaped && source.SelectionStart == 6 && source.SelectionLength == 4 && source.SelectedText == "beta",
            "Esc closes the bar and keeps beta selected", "closed=" + !BarOpen(window) + ", handled=" + escaped + ", selection=" + source.SelectionStart + "," + source.SelectionLength + " [" + OneLine(source.SelectedText) + "]", Excerpt(DocText(window), 6));
        if (focusAvailable) {
            Check("close-selection-focus", source.IsKeyboardFocusWithin && !box.IsKeyboardFocusWithin,
                "keyboard in the editor", "editor=" + source.IsKeyboardFocusWithin + ", query=" + box.IsKeyboardFocusWithin, "(after Esc)");
            bool left = RaiseKey(source.TextArea, Key.Left, true);
            // The real route: an unhandled preview falls through to the editor's Left command.
            if (!left) System.Windows.Documents.EditingCommands.MoveLeftByCharacter.Execute(null, source.TextArea);
            Pump();
            Check("close-selection-left", left && source.SelectionLength == 0 && source.CaretIndex == 6,
                "Left collapses to beta's start (caret 6)", "handled=" + left + ", caret=" + source.CaretIndex + ", length=" + source.SelectionLength, Excerpt(DocText(window), 6));
        }
    }
    // ---------------------------------------------------------------- reported bugs (bughunt2)
    /* Item 3: the bar was closed in a second tab, and coming back to the first tab still drew the
       matches of the search that tab last ran. A hidden bar must leave no highlight on the tab it
       returns to. */
    static void RunTabHighlight(MainWindow window) {
        currentAction = "tab-highlight";
        ResetDocument(window, "alpha 하나\n\nalpha 둘\n");
        OpenReplace(window);
        TypeQuery(window, "alpha", false);
        CheckCountAndHighlight("tab-first");
        SearchHighlight before = Field<SearchHighlight>(window, "sourceHighlight");
        Check("tab-first-highlight", before != null && ExtraCount(before) == 2, "2 extras",
            before == null ? "<none>" : ExtraCount(before).ToString(CultureInfo.InvariantCulture), "(first tab)");
        object first = Field<object>(window, "activeDocument");
        Invoke(window, "CreateTab");
        Pump();
        Check("tab-second-active", !Object.ReferenceEquals(Field<object>(window, "activeDocument"), first),
            "a new tab is active", "same tab=" + Object.ReferenceEquals(Field<object>(window, "activeDocument"), first), "(new tab)");
        CloseBar(window);
        Invoke(window, "SwitchTab", first);
        Pump();
        SearchHighlight highlight = Field<SearchHighlight>(window, "sourceHighlight");
        string actual = highlight == null ? "<none>" : "start=" + highlight.SourceStart + ", extras=" + ExtraCount(highlight);
        Check("tab-highlight-cleared", !BarOpen(window) && (highlight == null || (highlight.SourceStart < 0 && ExtraCount(highlight) == 0)),
            "closed bar, no current match, no extras", "open=" + BarOpen(window) + ", " + actual, "(after switching back)");
        OpenFind(window);
        TypeQuery(window, "alpha", false);
        CheckCountAndHighlight("tab-reopen");
    }

    /* Item 4: a document past 32K (live view or TXT) searched a snapshot off the UI thread and the
       display path only ever cleared the overlay, so while the query was being typed a correct count
       sat above no highlight at all. */
    static void RunLargeHighlight(MainWindow window) {
        currentAction = "large-highlight";
        var builder = new StringBuilder();
        for (int i = 0; i < 3000; i++) builder.Append("alpha 문장 ").Append(i.ToString(CultureInfo.InvariantCulture)).Append("\n\n");
        string document = builder.ToString();
        ResetDocument(window, document);
        bool large = Prop<bool>(window, "LargeSourceSearch");
        if (!large) {
            NoteBehavior("large-search-not-applicable", "hideMarkers=" + Static<bool>("hideMarkers") + " keeps the visible-text map (short path); the large path is not exercised in this mode");
            return;
        }
        Check("large-search-applies", document.Length >= 32 * 1024, "at least 32K", document.Length.ToString(CultureInfo.InvariantCulture), "(fixture)");
        OpenReplace(window);
        TypeQuery(window, "alpha", false);
        bool counted = WaitForTotal(window, 3000, 6000);
        Check("large-search-count", counted, "3000", CountBlock(window).Text, "(after the snapshot scan)");
        if (!counted) return;
        window.UpdateLayout();
        Pump();
        CheckCountAndHighlight("large-search");
        SourceEditor source = Source(window);
        var view = source.TextArea.TextView;
        bool viewKnown = view != null && view.VisualLinesValid && view.VisualLines.Count > 0;
        if (viewKnown) {
            SearchHighlight highlight = Field<SearchHighlight>(window, "sourceHighlight");
            Check("large-search-rects", highlight != null && highlight.GeometryCount > 0, "at least one rectangle",
                highlight == null ? "<no highlight>" : highlight.GeometryCount.ToString(CultureInfo.InvariantCulture), "(large document, visible matches)");
        } else {
            NoteBehavior("large-search-noview", "the offscreen view had no visual lines, rectangle check skipped");
        }
    }

    /* Item 1: the replace buttons are Focusable=false, so a Korean syllable still being composed in
       the replace field was never committed and the document got the word cut off. The field values
       stand in for the IME update a probe cannot synthesize, the way UiFixProbe does for the find
       field; ReplacementText() is the product's own read. */
    static void RunReplaceComposition(MainWindow window) {
        currentAction = "replace-composition";
        ResetDocument(window, "alpha 하나\n\nalpha 둘\n");
        OpenReplace(window);
        TypeQuery(window, "alpha", false);
        SetReplacement(window, "바꾸");
        Set(window, "replaceComposing", "기");
        Set(window, "replaceComposingStart", 2);
        string value = (string)Invoke(window, "ReplacementText");
        Check("replace-composition-value", String.Equals(value, "바꾸기", StringComparison.Ordinal), "바꾸기", value, "(composing syllable)");
        SetReplacement(window, "바꾸기");
        string committed = (string)Invoke(window, "ReplacementText");
        Check("replace-composition-not-doubled", String.Equals(committed, "바꾸기", StringComparison.Ordinal), "바꾸기", committed, "(after commit)");
        SetReplacement(window, "바꾸");
        Set(window, "replaceComposing", "기");
        Set(window, "replaceComposingStart", 2);
        Invoke(window, "ReplaceAll");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        string all = DocText(window);
        Check("replace-composition-all", all.Contains("바꾸기 하나") && all.Contains("바꾸기 둘"),
            "both matches carry the composing syllable", Excerpt(all, 0), "(모두 바꾸기)");
        Check("replace-composition-count", ReportedTotal(window) == 0, "0", CountBlock(window).Text, "(after 모두 바꾸기)");
        ResetDocument(window, "alpha 하나\n");
        OpenReplace(window);
        TypeQuery(window, "alpha", false);
        SetReplacement(window, "바꾸");
        Set(window, "replaceComposing", "기");
        Set(window, "replaceComposingStart", 2);
        Invoke(window, "ReplaceOne");
        Pump();
        Invoke(window, "FlushDocumentChanges");
        Pump();
        string one = DocText(window);
        Check("replace-composition-one", one.Contains("바꾸기 하나"), "the match carries the composing syllable", Excerpt(one, 0), "(바꾸기)");
        Set(window, "replaceComposing", "");
        Set(window, "replaceComposingStart", -1);
    }
    // Pumps until the count reports the total the snapshot scan owes, then reports whether it did.
    static bool WaitForTotal(MainWindow window, int total, int atMostMilliseconds) {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < atMostMilliseconds) {
            Pump(60);
            if (ReportedTotal(window) == total) return true;
            if (windowBudgetReached()) break;
        }
        return ReportedTotal(window) == total;
    }

    // ---------------------------------------------------------------- a seeded stateful sequence
    static void RunRandomSequence(MainWindow window, bool liveMode, Random random, int actions) {
        currentAction = "random";
        ResetDocument(window, Fixture(currentSeed));
        OpenReplace(window);
        string[] queries = QuerySet();
        string[] replacements = new string[] { "X", "완료", "😀", "Y", "" };
        for (int step = 0; step < actions; step++) {
            if (windowBudgetReached()) return;
            currentStep++;
            int roll = random.Next(100);
            if (roll < 24) {
                currentAction = "random:query";
                TypeQuery(window, queries[random.Next(queries.Length)], random.Next(2) == 0);
                CheckCountAndHighlight("random-query");
            } else if (roll < 37) {
                currentAction = "random:nav";
                if (!BarOpen(window)) OpenReplace(window);
                Invoke(window, "FindNext", random.Next(3) == 0);
                Pump();
                CheckCountAndHighlight("random-nav");
            } else if (roll < 47) {
                currentAction = "random:replace-one";
                if (!BarOpen(window)) OpenReplace(window);
                SetReplacement(window, replacements[random.Next(replacements.Length)]);
                string before = DocText(window);
                Invoke(window, "ReplaceOne");
                Pump();
                Invoke(window, "FlushDocumentChanges");
                Pump();
                CheckTables(DocText(window), "random-replace-one");
                CheckCountAndHighlight("random-replace-one");
                if (!String.Equals(before, DocText(window), StringComparison.Ordinal) && random.Next(4) == 0) CheckUndoOne(before, DocText(window), "random-replace-one");
            } else if (roll < 55) {
                currentAction = "random:replace-all";
                if (!BarOpen(window)) OpenReplace(window);
                SetReplacement(window, replacements[random.Next(replacements.Length)]);
                string before = DocText(window);
                Invoke(window, "ReplaceAll");
                Pump();
                Invoke(window, "FlushDocumentChanges");
                Pump();
                CheckTables(DocText(window), "random-replace-all");
                CheckCountAndHighlight("random-replace-all");
                if (!String.Equals(before, DocText(window), StringComparison.Ordinal) && random.Next(4) == 0) CheckUndoOne(before, DocText(window), "random-replace-all");
            } else if (roll < 63) {
                currentAction = "random:undo";
                Invoke(window, random.Next(2) == 0 ? "Undo" : "Redo");
                Pump();
                Invoke(window, "FlushDocumentChanges");
                Pump();
                CheckTables(DocText(window), "random-undo");
                CheckCountAndHighlight("random-undo");
            } else if (roll < 73) {
                currentAction = "random:edit";
                EditDocument(window, random);
                Pump(400);
                Invoke(window, "FlushDocumentChanges");
                Pump();
                CheckTables(DocText(window), "random-edit");
                CheckCountAndHighlight("random-edit");
            } else if (roll < 82) {
                currentAction = "random:case";
                CheckBox caseBox = CaseBox(window);
                caseBox.IsChecked = !(caseBox.IsChecked == true);
                Pump();
                CheckCountAndHighlight("random-case");
            } else if (roll < 90) {
                currentAction = "random:surface";
                Invoke(window, "SetLiveMode", !Static<bool>("hideMarkers"));
                Pump();
                Invoke(window, "FlushDocumentChanges");
                Pump();
                CheckCountAndHighlight("random-surface");
            } else if (roll < 96) {
                currentAction = "random:selection";
                SourceEditor source = Source(window);
                int length = source.Document.TextLength;
                int at = length == 0 ? 0 : random.Next(length);
                int count = Math.Min(length - at, random.Next(6));
                source.Select(at, count);
                Pump();
                CheckCountAndHighlight("random-selection");
            } else {
                currentAction = "random:bar";
                if (BarOpen(window) && random.Next(2) == 0) CloseBar(window);
                else OpenReplace(window);
                if (BarOpen(window)) CheckCountAndHighlight("random-bar");
            }
            CheckHighlightBounds("random-" + step);
            if (Static<bool>("hideMarkers") != !liveMode && random.Next(3) != 0) {
                Invoke(window, "SetLiveMode", liveMode);
                Pump();
            }
        }
        Invoke(window, "SetLiveMode", liveMode);
        Pump();
    }
    static void EditDocument(MainWindow window, Random random) {
        SourceEditor source = Source(window);
        string text = DocText(window);
        int roll = random.Next(3);
        if (roll == 0) {
            if (text.Length == 0) { source.Document.Insert(0, "alpha "); return; }
            int at = random.Next(text.Length);
            at = text.LastIndexOf('\n', Math.Max(0, at - 1));
            at = at < 0 ? 0 : at + 1;
            source.Document.Insert(at, "alpha ");
            return;
        }
        int found = text.IndexOf("alpha", StringComparison.Ordinal);
        if (found < 0) found = text.IndexOf("검색", StringComparison.Ordinal);
        if (found >= 0) {
            int length = text.Substring(found).StartsWith("alpha", StringComparison.Ordinal) ? 5 : 2;
            source.Document.Remove(found, length);
            return;
        }
        source.Document.Insert(0, "alpha ");
    }

    // ---------------------------------------------------------------- checked helpers and report
    static bool Check(string invariant, bool ok, string expected, string actual, string excerpt) {
        Bump(checkCounts, invariant);
        if (ok) return true;
        Fail(invariant, expected, actual, excerpt);
        return false;
    }
    static bool windowBudgetReached() {
        if (watch == null || watch.ElapsedMilliseconds <= budgetMilliseconds) return false;
        budgetReached = true;
        all = false;
        return true;
    }
    static int ReportedTotal(MainWindow window) {
        int current, total;
        if (ParseCount(CountBlock(window).Text, out current, out total)) return total;
        return -1;
    }
    static int[] Shuffle(Random random, int count) {
        int[] order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        for (int i = count - 1; i > 0; i--) {
            int j = random.Next(i + 1);
            int swap = order[i]; order[i] = order[j]; order[j] = swap;
        }
        return order;
    }

    // ---------------------------------------------------------------- fixtures
    static string[] QuerySet() {
        return new string[] {
            "alpha", "ALPHA", "한글", "검색", "ㅎ", "ㅎㅏ", "こんにちは", "😀",
            "  ", "*", "|", "`", "#", "bc", "ab", "bold", "code", "링크", "text", "alpha beta", "끝", "셀"
        };
    }
    static string Fixture(int seed) {
        var text = new StringBuilder();
        text.Append("# 제목 alpha\n\n");
        text.Append("alpha beta ALPHA **bold alpha** tail\n\n");
        text.Append("**ab**c and x**y**z plus ~~strike alpha~~\n\n");
        text.Append("한글 검색 구간 한글 **검색** 구간\n\n");
        text.Append("ㅎㅏㄴ 부분 자모 그리고 ㅎ standalone\n\n");
        text.Append("こんにちは 検索 テスト\n\n");
        text.Append("😀 이모지 😀 😀\n\n");
        text.Append("공백  두 칸   세 칸\n\n");
        text.Append("별 * 별표 | 파이프 `백틱` # 해시\n\n");
        text.Append("| 이름 | 값 | 비고 |\n| --- | --- | --- |\n| alpha 셀 | 둘 | alpha |\n| 셋 | alpha\\|pipe | 끝 |\n\n");
        text.Append("[링크 alpha](https://example.invalid/alpha) 그리고 `alpha code`\n\n");
        text.Append("```text\nalpha inside fence\ncode line\n```\n\n");
        text.Append("tail alpha 끝 seed ").Append(seed.ToString(CultureInfo.InvariantCulture)).Append('\n');
        return text.ToString();
    }

    static string ComposeReport(StringBuilder runs) {
        var text = new StringBuilder();
        text.Append("# FindAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- seeds: ").Append(firstSeed).Append("..").Append(lastSeed).Append(", modes: basic (HideMarkers true) and live (HideMarkers false)\n");
        text.Append("- fixtures: generated from the seed; no user file is read\n");
        text.Append("- surface: offscreen MainWindow, the window's own search bar and command entry points\n");
        text.Append("- options: the bar offers 대소문자 구분 only (no whole-word or regex control exists) plus query text\n");
        text.Append("- search text: visible text when the surface hides markup, the source text otherwise (same rule as Search.cs Map)\n");
        text.Append("- runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms")
            .Append(budgetReached ? " (budget reached; run incomplete)" : "").Append('\n');
        if (dispatcherNotes.Count > 0) {
            text.Append("\n## Dispatcher exceptions\n");
            foreach (string note in dispatcherNotes) text.Append("- ").Append(note).Append('\n');
        }
        text.Append("\n## Invariant checks\n");
        AppendCounts(text, checkCounts);
        text.Append("\n## Invariant failures\n");
        AppendCounts(text, failCounts);
        text.Append(runs.ToString());
        text.Append("\n## Recorded behavior (not judged)\n");
        if (behaviors.Count == 0) text.Append("- none\n");
        else foreach (BehaviorInfo behavior in behaviors) {
            text.Append("- ").Append(behavior.Key).Append(" (x").Append(behavior.Count).Append("): ").Append(behavior.Detail).Append('\n');
        }
        text.Append("\n## Probe notes\n");
        text.Append("- probe fix: ParseStatus reads the skipped count before the message's '개는' marker; the first version looked before '건너뛰' and always parsed 0. No invariant or expected value was relaxed.\n");
        text.Append("- another worker was editing product sources while this lane ran; the failures below are as observed on this lane's build and are to be re-checked on the combined build.\n");
        text.Append("\n## Failures\n");
        if (failures.Count == 0) text.Append("- none\n");
        else foreach (FailureInfo failure in failures) {
            text.Append("\n### seed ").Append(failure.Seed).Append(' ').Append(failure.Mode).Append(", fixture ").Append(failure.Fixture)
                .Append(", step ").Append(failure.Step).Append(' ').Append(failure.Action).Append('\n');
            text.Append("- query: [").Append(OneLine(failure.Query)).Append("], options: ").Append(failure.Options).Append('\n');
            text.Append("- invariant: ").Append(failure.Invariant).Append('\n');
            text.Append("- expected: ").Append(failure.Expected).Append('\n');
            text.Append("- actual: ").Append(failure.Actual).Append('\n');
            text.Append("- excerpt: ").Append(failure.Excerpt).Append('\n');
            if (failure.Occurrences > 1) text.Append("- occurrences: ").Append(failure.Occurrences).Append('\n');
        }
        if (omittedFailures > 0) text.Append("\n- ").Append(omittedFailures).Append(" further distinct failures were counted but omitted from this list.\n");
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
}
}
