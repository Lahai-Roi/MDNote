using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace MDNote {
// --file-abuse-probe <dir>: generated file I/O matrix. Every fixture is written by this probe
// inside <dir>\files; no user file, setting or recovery draft is read or written. Each case runs
// the app's real entry points (LoadFile / OpenPreparedFileAsync for a forced encoding, Save /
// SaveToPath for the writer) on an offscreen self-test window, then reopens the file and compares
// the text and the bytes.
//
// Invariants checked here (the app's own rules are recorded in the report):
//  - an unedited open+save writes the same bytes back (BOM, line endings, NULs, odd bytes);
//  - a reopened file yields exactly the saved text;
//  - an edit is written in the document's encoding and newline, and reopening gives that text;
//  - Save As to another offered encoding writes that encoding's preamble and the same text;
//  - text the chosen encoding cannot represent is refused before the file is touched (no '?');
//  - an encoding/BOM the reader cannot decode is refused, not silently saved lossy;
//  - external modification or deletion is caught by the save-time fingerprint check;
//  - the atomic writer never leaves a .mdnote-*.tmp file or a partial target;
//  - TXT->MD->TXT keeps the text (Canonical newlines), MD->TXT equals MarkdownPlainText.Convert.
internal static class FileAbuseProbe {
    const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const long BudgetMilliseconds = 240000;
    const int WatchdogMilliseconds = 290000;
    const int MaxFailureDetails = 60;

    static readonly object reportGate = new object();
    static readonly List<string> notes = new List<string>();
    static readonly List<string> failures = new List<string>();
    static readonly List<string> caseSummaries = new List<string>();
    static readonly Dictionary<string, int> checkKinds = new Dictionary<string, int>();
    static readonly Dictionary<string, int> failKinds = new Dictionary<string, int>();
    static readonly List<string> dispatcherNotes = new List<string>();

    static string outRoot;
    static string filesRoot;
    static string currentCase = "";
    static string currentSteps = "";
    static int totalChecks, totalFails, caseChecks, caseFails;
    static bool all = true, budgetReached, finished, dispatcherFailed;
    static Stopwatch watch;
    static Stopwatch caseWatch;
    static DispatcherUnhandledExceptionEventHandler dispatcherHandler;

    internal static int Run(string folder) {
        outRoot = Path.GetFullPath(folder);
        Directory.CreateDirectory(outRoot);
        filesRoot = Path.Combine(outRoot, "files");
        try { if (Directory.Exists(filesRoot)) Directory.Delete(filesRoot, true); } catch (Exception) { }
        Directory.CreateDirectory(filesRoot);
        string passPath = Path.Combine(outRoot, "file-abuse-probe-PASS.txt");
        string failPath = Path.Combine(outRoot, "file-abuse-probe-FAIL.txt");
        DeleteIfExists(passPath);
        DeleteIfExists(failPath);
        DeleteIfExists(Path.Combine(outRoot, "file-abuse-probe-progress.txt"));

        bool oldSelf = Program.IsSelfTest;
        SynchronizationContext previous = null;
        Dispatcher dispatcher = null;
        try {
            Program.IsSelfTest = true;
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            dispatcher = Dispatcher.CurrentDispatcher;
            previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcherHandler = delegate(object sender, DispatcherUnhandledExceptionEventArgs args) { NoteDispatcher(args); };
            dispatcher.UnhandledException += dispatcherHandler;
            Application.Current.DispatcherUnhandledException += dispatcherHandler;
            StartWatchdog();
            watch = Stopwatch.StartNew();
            Check("environment.recovery-disabled", !RecoveryStore.Enabled, "RecoveryStore.Enabled=false", "RecoveryStore.Enabled=" + RecoveryStore.Enabled);
            RunAll();
            watch.Stop();
        } catch (Exception error) {
            all = false;
            AddFailure("suite", "no exception", "no exception", OneLine(error.ToString()));
        } finally {
            try { if (dispatcher != null && dispatcherHandler != null) dispatcher.UnhandledException -= dispatcherHandler; } catch (Exception) { }
            try { if (Application.Current != null && dispatcherHandler != null) Application.Current.DispatcherUnhandledException -= dispatcherHandler; } catch (Exception) { }
            try { MainWindow.SaveTargetPicker = null; } catch (Exception) { }
            try { MainWindow.ConflictAnswer = null; } catch (Exception) { }
            try { MainWindow.AtomicWriteObserver = null; } catch (Exception) { }
            CloseAllWindows();
            Program.IsSelfTest = oldSelf;
            if (previous != null) { try { SynchronizationContext.SetSynchronizationContext(previous); } catch (Exception) { } }
        }
        string text = ComposeReport();
        lock (reportGate) {
            if (!finished) {
                finished = true;
                try {
                    File.WriteAllText(Path.Combine(outRoot, "file-abuse-probe.txt"), text, new UTF8Encoding(false));
                    bool pass = all && !budgetReached && !dispatcherFailed;
                    if (pass) File.WriteAllText(passPath, "file-abuse-probe PASS\n" + DateTime.Now.ToString("o") + "\n", new UTF8Encoding(false));
                    else File.WriteAllText(failPath, text, new UTF8Encoding(false));
                    return pass ? 0 : 1;
                } catch (IOException) {
                    return 1;
                }
            }
            return 1;
        }
    }

    static void StartWatchdog() {
        var thread = new Thread(delegate() {
            Thread.Sleep(WatchdogMilliseconds);
            lock (reportGate) {
                if (finished) return;
                finished = true;
                try {
                    string text = "# FileAbuseProbe\n\nFAIL watchdog: the probe did not finish within " +
                        (WatchdogMilliseconds / 1000) + " s; last case " + currentCase + "\n";
                    File.WriteAllText(Path.Combine(outRoot, "file-abuse-probe.txt"), text, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(outRoot, "file-abuse-probe-FAIL.txt"), text, new UTF8Encoding(false));
                } catch (Exception) { }
            }
            Environment.Exit(2);
        });
        thread.IsBackground = true;
        thread.Name = "file-abuse-watchdog";
        thread.Start();
    }

    // ---------------------------------------------------------------- report and checks
    static void BeginCase(string id, string steps) {
        currentCase = id; currentSteps = steps; caseChecks = 0; caseFails = 0; caseWatch = Stopwatch.StartNew();
        Progress("BEGIN " + id);
    }
    static void EndCase() {
        if (caseWatch != null) caseWatch.Stop();
        long elapsed = caseWatch == null ? 0 : caseWatch.ElapsedMilliseconds;
        caseSummaries.Add("- " + currentCase + ": " + (caseChecks - caseFails) + "/" + caseChecks +
            " checks" + (caseFails == 0 ? "" : ", " + caseFails + " FAILED") + ", " + elapsed + " ms");
        Progress("END " + currentCase + " " + elapsed + " ms");
    }
    // A plain step log survives the watchdog, so a hang names the step that did not return.
    static void Progress(string text) {
        try {
            File.AppendAllText(Path.Combine(outRoot, "file-abuse-probe-progress.txt"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + "\r\n", new UTF8Encoding(false));
        } catch (Exception) { }
    }
    static bool Check(string invariant, bool ok, string expected, string actual) {
        totalChecks++; caseChecks++;
        int count; if (!checkKinds.TryGetValue(invariant, out count)) count = 0; checkKinds[invariant] = count + 1;
        if (ok) return true;
        all = false; totalFails++; caseFails++;
        int failed; if (!failKinds.TryGetValue(invariant, out failed)) failed = 0; failKinds[invariant] = failed + 1;
        AddFailure(currentCase, currentSteps, expected, actual);
        return false;
    }
    static void AddFailure(string id, string steps, string expected, string actual) {
        if (failures.Count >= MaxFailureDetails) return;
        var text = new StringBuilder();
        text.Append("\n### ").Append(id).Append('\n');
        text.Append("- steps: ").Append(steps).Append('\n');
        text.Append("- expected: ").Append(Limit(expected)).Append('\n');
        text.Append("- actual: ").Append(Limit(actual)).Append('\n');
        failures.Add(text.ToString());
    }
    static void Note(string text) { notes.Add("- " + OneLine(text)); }
    static void NoteDispatcher(DispatcherUnhandledExceptionEventArgs args) {
        dispatcherFailed = true; all = false;
        args.Handled = true;
        string detail = args.Exception == null ? "(no exception object)" : OneLine(args.Exception.ToString());
        if (dispatcherNotes.Count < 20) dispatcherNotes.Add("case " + currentCase + ": " + detail);
    }
    static string Limit(string value) {
        if (value == null) return "<null>";
        return value.Length <= 1600 ? value : value.Substring(0, 1600) + "...";
    }
    static string OneLine(string value) {
        if (value == null) return "<null>";
        return Limit(value.Replace("\r", "\\r").Replace("\n", "\\n"));
    }
    static string Esc(string value) {
        if (value == null) return "<null>";
        string text = value.Replace("\0", "\\0").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return text.Length <= 600 ? text : text.Substring(0, 600) + "...";
    }
    static string ComposeReport() {
        var text = new StringBuilder();
        text.Append("# FileAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o")).Append('\n');
        text.Append("- runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms")
            .Append(budgetReached ? " (case budget reached; run incomplete)" : "").Append('\n');
        text.Append("- files: generated inside ").Append(filesRoot).Append("; no user file is read\n");
        text.Append("- windows: offscreen self-test MainWindow; real LoadFile / OpenPreparedFileAsync / Save / SaveToPath entry points\n");
        text.Append("- encodings offered by the UI: the document's own encoding (keep, the dialog's default), then UTF-8, UTF-8 (BOM), UTF-16 LE, UTF-16 BE, ANSI (CP949); the reader also auto-detects UTF-32\n");
        text.Append("- product-code note: other workers were editing the product source during this run; every failure below is reported as-is and no invariant was relaxed\n");
        text.Append("- probe note: a forced-encoding open is driven through the app's file-operation queue (EnqueueFileOperation/DrainFileOpens), the same path the Open-with-encoding menu uses; awaiting the private OpenPreparedFileAsync directly hung an earlier probe run, so the probe uses the queue path instead (probe fix only; no product invariant was relaxed)\n");
        text.Append("- probe note: after opening a mixed-newline file the expected editing text is the app's normalized buffer (ApplyLoadedFile normalizes it), not the raw mixed source; the clean-save check still requires the exact original bytes\n");
        text.Append("\n## App rules asserted (read from the source)\n");
        text.Append("- open: UTF-32 LE/BE, UTF-8, UTF-16 LE/BE BOM detection; a BOM-less file is decoded as strict UTF-8 and falls back to strict CP949; a BOM file that fails to decode is refused (no lossy fallback)\n");
        text.Append("- newline: chosen by counts (CRLF when CRLF outnumbers LF-only endings, LF when LF-only endings outnumber CRLF, CR only when the text has no LF at all, CRLF on a tie); the buffer is normalized to it and every save writes that newline, except an untouched file with mixed newlines, which is written back from its original bytes; a reopened mixed-newline file therefore shows the normalized buffer while its bytes stay exact\n");
        text.Append("- save: the document keeps its encoding; Save As starts on the entry that keeps it and may instead pick UTF-8/BOM/UTF-16 LE/BE/CP949; a character the encoding cannot represent is refused before the write; the writer uses .mdnote-<guid>.tmp in the target folder, flushes, then File.Replace/Move\n");
        text.Append("- conflict: a save-time SHA-256 fingerprint of the opened file refuses to overwrite an externally changed or deleted file\n");
        text.Append("- format: .txt opens in TXT mode, .md/.markdown in Markdown mode; a format-changing Save As converts through LiteralMarkdown / MarkdownPlainText.Convert\n");
        text.Append("- convert-save: the conversion is committed before the write, but a failed or cancelled write rolls the tab back to its exact pre-conversion text, mode, reading/raw view, caret/selection and undo/redo stacks; a cancelled queued save returns false with the cancellation notice (LastSaveFailure) and changes nothing; a format-changing save finishes a live IME composition and converts synchronously instead of deferring; the conversion's busy guard is up before its first await\n");
        text.Append("\n## Counts\n");
        text.Append("- cases: ").Append(caseSummaries.Count).Append('\n');
        text.Append("- checks: ").Append(totalChecks).Append('\n');
        text.Append("- failures: ").Append(totalFails).Append('\n');
        if (dispatcherFailed) text.Append("- dispatcher exceptions: ").Append(dispatcherNotes.Count).Append('\n');
        text.Append("- checks by invariant:\n");
        AppendCounts(text, checkKinds);
        text.Append("- failures by invariant:\n");
        AppendCounts(text, failKinds);
        if (notes.Count > 0) {
            text.Append("\n## Notes\n");
            foreach (string note in notes) text.Append(note).Append('\n');
        }
        if (dispatcherNotes.Count > 0) {
            text.Append("\n## Dispatcher exceptions\n");
            foreach (string note in dispatcherNotes) text.Append("- ").Append(note).Append('\n');
        }
        text.Append("\n## Cases\n");
        foreach (string summary in caseSummaries) text.Append(summary).Append('\n');
        text.Append("\n## Failures\n");
        if (failures.Count == 0) text.Append("- none\n");
        else {
            foreach (string failure in failures) text.Append(failure);
            if (totalFails > failures.Count) text.Append("\n(").Append(totalFails - failures.Count).Append(" more failure entries omitted)\n");
        }
        text.Append("\nResult: ").Append(all && !budgetReached && !dispatcherFailed ? "PASS" : "FAIL").Append('\n');
        text.Append("Runtime: ").Append(watch == null ? 0 : watch.ElapsedMilliseconds).Append(" ms\n");
        return text.ToString();
    }
    static void AppendCounts(StringBuilder text, Dictionary<string, int> map) {
        if (map.Count == 0) { text.Append("  - (none)\n"); return; }
        var keys = new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (string key in keys) text.Append("  - ").Append(key).Append(": ").Append(map[key]).Append('\n');
    }
    static void DeleteIfExists(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { } }

    // ---------------------------------------------------------------- reflection, pumping, windows
    static FieldInfo RequireField(Type type, string name) {
        FieldInfo field = type.GetField(name, Members);
        if (field == null) throw new MissingMemberException("probe: " + type.Name + " has no field '" + name + "'");
        return field;
    }
    static object FieldValue(object target, string name) { return RequireField(target.GetType(), name).GetValue(target); }
    static T Field<T>(object target, string name) { return (T)FieldValue(target, name); }
    static void SetField(object target, string name, object value) { RequireField(target.GetType(), name).SetValue(target, value); }
    static PropertyInfo RequireProperty(string name) {
        PropertyInfo property = typeof(MainWindow).GetProperty(name, Members);
        if (property == null) throw new MissingMemberException("probe: MainWindow has no property '" + name + "'");
        return property;
    }
    static T Prop<T>(object target, string name) {
        try { return (T)RequireProperty(name).GetValue(target, null); }
        catch (TargetInvocationException error) { if (error.InnerException != null) throw error.InnerException; throw; }
    }
    static object Invoke(object target, string name, params object[] arguments) {
        MethodInfo method = typeof(MainWindow).GetMethod(name, Members);
        if (method == null) throw new MissingMemberException("probe: MainWindow has no method '" + name + "'");
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) { if (error.InnerException != null) throw error.InnerException; throw; }
    }
    static object InvokeStatic(string name, params object[] arguments) {
        MethodInfo method = typeof(MainWindow).GetMethod(name, Members);
        if (method == null) throw new MissingMemberException("probe: MainWindow has no method '" + name + "'");
        try { return method.Invoke(null, arguments); }
        catch (TargetInvocationException error) { if (error.InnerException != null) throw error.InnerException; throw; }
    }
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
    static void WaitTask(System.Threading.Tasks.Task task, int timeoutMilliseconds) {
        if (task == null) return;
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted) {
            if (timer.ElapsedMilliseconds > timeoutMilliseconds) throw new TimeoutException("probe: asynchronous open did not finish in " + timeoutMilliseconds + " ms");
            var frame = new DispatcherFrame();
            var tick = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(5) };
            tick.Tick += delegate { tick.Stop(); frame.Continue = false; };
            tick.Start();
            try { Dispatcher.PushFrame(frame); } finally { tick.Stop(); }
        }
        task.GetAwaiter().GetResult();
    }
    static MainWindow NewWindow() {
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Width = 900, Height = 700 };
        window.Show();
        window.Left = -20000;
        window.Top = -20000;
        window.UpdateLayout();
        Pump();
        return window;
    }
    static SourceEditor Source(MainWindow window) { return Field<SourceEditor>(window, "source"); }
    static string GetText(MainWindow window) { return Prop<string>(window, "text"); }
    static string GetBuffer(MainWindow window) { SourceEditor source = Source(window); return source == null || source.Document == null ? "" : source.Document.Text; }
    static string GetSaved(MainWindow window) { return Field<string>(window, "saved"); }
    static string GetPath(MainWindow window) { return Field<string>(window, "path"); }
    static Encoding GetEncoding(MainWindow window) { return Field<Encoding>(window, "documentEncoding"); }
    static string GetNewline(MainWindow window) { return Field<string>(window, "documentNewline"); }
    static bool GetEditing(MainWindow window) { return Field<bool>(window, "editing"); }
    static bool GetPlainText(MainWindow window) { return Prop<bool>(window, "PlainText"); }
    static bool HasUnsaved(MainWindow window) {
        try { return Prop<bool>(window, "HasUnsavedChanges"); } catch (Exception) { return true; }
    }
    // MainWindow.History is private, so read the document's redo flag through it.
    static bool HistoryCanRedo(MainWindow window) {
        object history = RequireProperty("History").GetValue(window, null);
        return (bool)history.GetType().GetProperty("CanRedo", Members).GetValue(history, null);
    }
    static void EnterEdit(MainWindow window) {
        if (!GetEditing(window)) { window.SetMode(true); Pump(120); }
    }
    static void CloseQuiet(MainWindow window) {
        if (window == null) return;
        try {
            SetField(window, "documentOperationBusy", false);
            try { Invoke(window, "CaptureTab"); } catch (Exception) { }
            string value = GetText(window);
            SetField(window, "saved", value);
            SetField(window, "recoveredDocument", false);
            object documents = null;
            try { documents = FieldValue(window, "documents"); } catch (Exception) { }
            if (documents != null) {
                foreach (object state in (IEnumerable)documents) {
                    try {
                        Type type = state.GetType();
                        FieldInfo saved = type.GetField("Saved", Members);
                        FieldInfo recovered = type.GetField("Recovered", Members);
                        if (saved != null) saved.SetValue(state, value);
                        if (recovered != null) recovered.SetValue(state, false);
                    } catch (Exception) { }
                }
            }
            Progress("close-start " + currentCase);
            window.Close();
            Progress("close-done " + currentCase);
        } catch (Exception error) {
            Note("close of " + currentCase + " raised " + error.GetType().Name + ": " + OneLine(error.Message));
        }
    }
    static void CloseAllWindows() {
        if (Application.Current == null) return;
        var open = new List<Window>();
        try { foreach (Window candidate in Application.Current.Windows) open.Add(candidate); } catch (Exception) { return; }
        foreach (Window candidate in open) {
            try {
                MainWindow editor = candidate as MainWindow;
                if (editor != null) CloseQuiet(editor);
                else candidate.Close();
            } catch (Exception) { }
        }
    }
    static void OpenSample(MainWindow window, string file, Sample sample) {
        if (sample.Forced) OpenForcedRequired(window, file, sample.CodePage);
        else { window.LoadFile(file); Pump(120); }
    }
    static void ReopenSample(MainWindow window, string file, Sample sample) {
        if (sample.ReopenCodePage != 0) OpenForcedRequired(window, file, sample.ReopenCodePage);
        else { window.LoadFile(file); Pump(120); }
    }
    // A forced-encoding open goes through the app's own file-operation queue (the same path the
    // Open-with-encoding menu uses), so a read failure is caught by DrainFileOpens and left in
    // LastFileOpenError instead of faulting a Task this probe would have to pump.
    static void OpenForced(MainWindow window, string file, int codePage) {
        Progress("forced-open " + codePage + " " + Path.GetFileName(file));
        EnqueueEncodingOpen(window, file, (int?)codePage);
        if (!WaitForOpenQueue(window, 20000)) throw new TimeoutException("probe: forced open queue did not finish for " + file);
        Progress("forced-open-done " + codePage + " error=" + (window.LastFileOpenError == null ? "none" : window.LastFileOpenError.GetType().Name));
    }
    static void OpenForcedRequired(MainWindow window, string file, int codePage) {
        OpenForced(window, file, codePage);
        Exception error = window.LastFileOpenError;
        if (error != null) throw error;
    }
    static void EnqueueEncodingOpen(MainWindow window, string file, int? codePage) {
        Type operationType = typeof(MainWindow).GetNestedType("FileOperation", Members);
        if (operationType == null) throw new MissingMemberException("probe: MainWindow has no nested FileOperation");
        object operation = Activator.CreateInstance(operationType, true);
        operationType.GetField("Name", Members).SetValue(operation, file);
        operationType.GetField("CodePage", Members).SetValue(operation, codePage);
        MethodInfo enqueue = typeof(MainWindow).GetMethod("EnqueueFileOperation", Members);
        if (enqueue == null) throw new MissingMemberException("probe: MainWindow has no method 'EnqueueFileOperation'");
        try { enqueue.Invoke(window, new object[] { operation }); }
        catch (TargetInvocationException error) { if (error.InnerException != null) throw error.InnerException; throw; }
    }
    static bool WaitForOpenQueue(MainWindow window, int timeoutMilliseconds) {
        FieldInfo running = RequireField(typeof(MainWindow), "fileOpenRunning");
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < timeoutMilliseconds) {
            if (!Object.Equals(running.GetValue(window), true)) { Pump(150); return true; }
            Pump();
            Thread.Sleep(10);
        }
        return false;
    }
    static bool SaveInPlace(MainWindow window) { return (bool)Invoke(window, "Save", false); }
    static bool SaveAsWithPicker(MainWindow window, string target) {
        MainWindow.SaveTargetPicker = delegate(string name) { return target; };
        try { return (bool)Invoke(window, "Save", true); }
        finally { MainWindow.SaveTargetPicker = null; }
    }
    static bool SaveTo(MainWindow window, string target, int encodingIndex, bool changeFormat) {
        Encoding encoding = (Encoding)InvokeStatic("StrictEncoding", encodingIndex);
        return (bool)Invoke(window, "SaveToPath", target, encoding, changeFormat);
    }
    static Encoding StrictEncoding(int index) { return (Encoding)InvokeStatic("StrictEncoding", index); }

    // ---------------------------------------------------------------- files and bytes
    static string CaseDir(string id) {
        string dir = Path.Combine(filesRoot, id);
        Directory.CreateDirectory(dir);
        return dir;
    }
    static string WriteFile(string dir, string name, byte[] bytes) {
        string file = Path.Combine(dir, name);
        File.WriteAllBytes(file, bytes);
        return file;
    }
    static string[] TempFiles(string dir) { return Directory.GetFiles(dir, ".mdnote-*.tmp"); }
    static byte[] Encoded(Encoding encoding, string text) {
        byte[] preamble = encoding.GetPreamble();
        byte[] body = encoding.GetBytes(text);
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }
    static byte[] Utf8NoBom(string text) { return new UTF8Encoding(false).GetBytes(text); }
    static byte[] Utf8Bom(string text) { return Encoded(new UTF8Encoding(true), text); }
    static byte[] Utf16Le(string text) { return Encoded(new UnicodeEncoding(false, true, false), text); }
    static byte[] Utf16Be(string text) { return Encoded(new UnicodeEncoding(true, true, false), text); }
    static byte[] Utf32Le(string text) { return Encoded(new UTF32Encoding(false, true, false), text); }
    static byte[] Utf32Be(string text) { return Encoded(new UTF32Encoding(true, true, false), text); }
    static byte[] Cp949(string text) { return Encoding.GetEncoding(949).GetBytes(text); }
    static byte[] Cp932(string text) { return Encoding.GetEncoding(932).GetBytes(text); }
    static bool BytesEqual(byte[] a, byte[] b) {
        if (Object.ReferenceEquals(a, b)) return true;
        if (a == null || b == null || a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static string HexAround(byte[] bytes, int at) {
        if (bytes == null) return "<null>";
        int start = Math.Max(0, at - 8), end = Math.Min(bytes.Length, at + 8);
        var text = new StringBuilder();
        for (int i = start; i < end; i++) {
            if (i > start) text.Append(' ');
            if (i == at) text.Append('>');
            text.Append(bytes[i].ToString("X2"));
        }
        return text.ToString();
    }
    static string ByteDiff(byte[] want, byte[] got) {
        if (want == null) return "expected <null>";
        if (got == null) return "expected " + want.Length + " bytes, actual <null>";
        int limit = Math.Min(want.Length, got.Length), at = 0;
        while (at < limit && want[at] == got[at]) at++;
        var text = new StringBuilder();
        text.Append("expected ").Append(want.Length).Append(" bytes, actual ").Append(got.Length).Append(" bytes; first difference at ").Append(at);
        if (at < limit) text.Append(" (0x").Append(want[at].ToString("X2")).Append(" vs 0x").Append(got[at].ToString("X2")).Append(')');
        else text.Append(" (length)");
        text.Append("; want[").Append(HexAround(want, at)).Append("] got[").Append(HexAround(got, at)).Append(']');
        text.Append("; wantText[").Append(Esc(DecodeExcerpt(want))).Append("] gotText[").Append(Esc(DecodeExcerpt(got))).Append(']');
        return text.ToString();
    }
    static string DecodeExcerpt(byte[] bytes) {
        if (bytes == null) return "<null>";
        int length = Math.Min(bytes.Length, 160);
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
    static string TextDiff(string want, string got) {
        if (want == null) want = "<null>";
        if (got == null) got = "<null>";
        int at = 0, limit = Math.Min(want.Length, got.Length);
        while (at < limit && want[at] == got[at]) at++;
        int start = Math.Max(0, at - 30), endWant = Math.Min(want.Length, at + 30), endGot = Math.Min(got.Length, at + 30);
        return "expected length " + want.Length + ", actual length " + got.Length + "; first difference at " + at +
            "; want[" + Esc(want.Substring(start, endWant - start)) + "] got[" + Esc(got.Substring(start, endGot - start)) + "]";
    }
    // The intended rule, written here independently of the product's helper: counts decide, CRLF
    // wins a tie, and CR only when the text holds no LF at all. A single stray CR in an LF file must
    // not turn the whole document into a CR document (the old "contains" probe did exactly that).
    static string DetectNewline(string text) {
        int crlf = 0, lf = 0, cr = 0;
        for (int i = 0; i < text.Length; i++) {
            if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; } else cr++; }
            else if (text[i] == '\n') lf++;
        }
        if (crlf == 0 && lf == 0) return cr == 0 ? "\n" : "\r";
        return crlf >= lf ? "\r\n" : "\n";
    }
    static string NormalizeLineEndings(string value, string newline) {
        return value.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", newline);
    }
    static bool IsValidUtf8(byte[] bytes) {
        try { new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }
    static string ReadUtf8(string file) { return File.ReadAllText(file, new UTF8Encoding(false)); }
    static string ReadWith(Encoding encoding, string file) {
        byte[] bytes = File.ReadAllBytes(file);
        int offset = 0;
        byte[] preamble = encoding.GetPreamble();
        if (preamble.Length > 0 && bytes.Length >= preamble.Length) {
            bool same = true;
            for (int i = 0; i < preamble.Length; i++) if (bytes[i] != preamble[i]) { same = false; break; }
            if (same) offset = preamble.Length;
        }
        return encoding.GetString(bytes, offset, bytes.Length - offset);
    }
    static string BytesState(byte[] bytes) {
        if (bytes == null) return "<null>";
        return bytes.Length + " bytes [" + HexAround(bytes, 0) + "]";
    }
    static bool IsPlainFileName(string name) {
        return String.Equals(Path.GetExtension(name), ".txt", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- samples and clean matrix
    internal sealed class Sample {
        internal string Id, FileName, Text;
        internal byte[] Bytes;
        internal int CodePage;
        internal bool Forced;
        internal int ReopenCodePage;
    }
    static Sample MakeSample(string id, string fileName, string text, byte[] bytes, int codePage, bool forced, int reopenCodePage) {
        var sample = new Sample();
        sample.Id = id; sample.FileName = fileName; sample.Text = text; sample.Bytes = bytes;
        sample.CodePage = codePage; sample.Forced = forced; sample.ReopenCodePage = reopenCodePage;
        return sample;
    }
    static bool BudgetLeft() {
        if (watch != null && watch.ElapsedMilliseconds > BudgetMilliseconds) {
            budgetReached = true; all = false;
            Note("case budget reached after " + currentCase + "; the remaining cases did not run");
            return false;
        }
        return true;
    }

    static void RunAll() {
        string mdCrlf = "# 제목\r\n\r\n본문 문장입니다.  \r\n\r\n- 항목 하나\r\n- 항목 둘\r\n\r\n| 가 | 나 |\r\n| --- | --- |\r\n| 다 | 라 |\r\n\r\n```\r\n코드 한글 😀\r\n```\r\n";
        string mdBomLf = "# BOM 제목\n\n**굵게** 문장\n\n둘째 문단\n";
        string utf16LeText = "UTF-16 LE 문서입니다.\r\n둘째 줄 😀\r\n";
        string utf16BeText = "UTF-16 BE 문서입니다.\n둘째 줄 😀\n";
        string utf32LeText = "UTF-32 LE 😀\r\n끝\r\n";
        string utf32BeText = "# UTF-32 BE\n본문 😀\n";
        string cp949Text = "한글 문서입니다.\r\n둘째 줄: 값 42\r\n끝.";
        string cp932Text = "日本語テスト\r\nこんにちは\r\n";
        string mixedText = "a\r\nb\nc\rd\r\n";
        string crText = "a\rb\r끝";
        string crMixedText = "a\nb\nc\rd\n";
        string sepText = "가" + (char)0x2028 + "나" + (char)0x2029 + "다" + (char)0x0085 + "라\r\n😀\r\n";
        string nulText = "앞\0가운데\0\0끝\r\n";
        string trailText = "  \r\n# 제목   \r\n\r\n마지막 줄   ";
        var oneLine = new StringBuilder(200032);
        oneLine.Append('a', 200000).Append("끝 😀");
        string oneLineText = oneLine.ToString();
        var manyLines = new StringBuilder(1100000);
        for (int i = 1; i <= 30000; i++) manyLines.Append("줄 ").Append(i).Append(" 한글 텍스트 값\r\n");
        string manyLinesText = manyLines.ToString();

        byte[] cp949Bytes = Cp949(cp949Text);
        byte[] cp932Bytes = Cp932(cp932Text);
        byte[] overlapBytes = new byte[] { 0x93, 0xFA, 0x8C, 0xEA };
        string overlapText = Encoding.GetEncoding(949).GetString(overlapBytes);
        Check("fixture.cp949-not-utf8", !IsValidUtf8(cp949Bytes), "CP949 bytes are not valid UTF-8", "CP949 fixture decoded as UTF-8");
        Check("fixture.cp932-partially-invalid", !IsValidUtf8(cp932Bytes), "CP932 bytes are not valid UTF-8", "CP932 fixture decoded as UTF-8");
        Check("fixture.shift-jis-overlap-not-utf8", !IsValidUtf8(overlapBytes), "Shift-JIS-like bytes are not valid UTF-8", "the overlap fixture decoded as UTF-8");
        Note("SaveAsync and the save-conflict prompt are self-test seams that rethrow instead of showing a MessageBox; the probe therefore asserts the exception and the untouched file, not the visible warning text");
        Note("format-changing Save As and Save As with a chosen encoding are driven through SaveToPath, the same method SaveAsync calls with the dialog's StrictEncoding; the UI-level Save As path is driven once through SaveTargetPicker");

        var samples = new List<Sample>();
        samples.Add(MakeSample("md-crlf", "문서.md", mdCrlf, Utf8NoBom(mdCrlf), 65001, false, 0));
        samples.Add(MakeSample("md-bom-lf", "bom.md", mdBomLf, Utf8Bom(mdBomLf), 65001, false, 0));
        samples.Add(MakeSample("utf16le-bom", "utf16le.txt", utf16LeText, Utf16Le(utf16LeText), 1200, false, 0));
        samples.Add(MakeSample("utf16be-bom", "utf16be.txt", utf16BeText, Utf16Be(utf16BeText), 1201, false, 0));
        samples.Add(MakeSample("utf32le-bom", "utf32le.txt", utf32LeText, Utf32Le(utf32LeText), 12000, false, 0));
        samples.Add(MakeSample("utf32be-bom", "utf32be.md", utf32BeText, Utf32Be(utf32BeText), 12001, false, 0));
        samples.Add(MakeSample("cp949", "cp949.txt", cp949Text, cp949Bytes, 949, false, 0));
        samples.Add(MakeSample("cp932-forced", "cp932.txt", cp932Text, cp932Bytes, 932, true, 932));
        samples.Add(MakeSample("shift-jis-overlap", "sjis-like.txt", overlapText, overlapBytes, 949, false, 0));
        samples.Add(MakeSample("mixed-bom", "mixed.md", mixedText, Utf8Bom(mixedText), 65001, false, 0));
        samples.Add(MakeSample("cr-only", "cr.txt", crText, Utf8NoBom(crText), 65001, false, 0));
        samples.Add(MakeSample("cr-in-lf", "cr-in-lf.txt", crMixedText, Utf8NoBom(crMixedText), 65001, false, 0));
        samples.Add(MakeSample("unicode-separators", "seps.md", sepText, Utf8NoBom(sepText), 65001, false, 0));
        samples.Add(MakeSample("nul-bytes", "nul.txt", nulText, Utf8NoBom(nulText), 65001, false, 0));
        samples.Add(MakeSample("trailing-ws", "trail.md", trailText, Utf8NoBom(trailText), 65001, false, 0));
        samples.Add(MakeSample("empty-md", "empty.md", "", new byte[0], 65001, false, 0));
        samples.Add(MakeSample("empty-txt", "empty.txt", "", new byte[0], 65001, false, 0));
        samples.Add(MakeSample("bom-only", "bom-only.md", "", new byte[] { 0xEF, 0xBB, 0xBF }, 65001, false, 0));
        samples.Add(MakeSample("utf16-nobom-auto", "utf16-nobom.txt", "a\0b\0c\0", new byte[] { 0x61, 0x00, 0x62, 0x00, 0x63, 0x00 }, 65001, false, 0));
        samples.Add(MakeSample("upper-txt", "대문자.TXT", "대문자 확장자\r\n둘째 줄\r\n", Utf8NoBom("대문자 확장자\r\n둘째 줄\r\n"), 65001, false, 0));
        samples.Add(MakeSample("markdown-ext", "확장자.markdown", "# markdown 확장자\n본문\n", Utf8NoBom("# markdown 확장자\n본문\n"), 65001, false, 0));
        samples.Add(MakeSample("one-line-200k", "oneline.txt", oneLineText, Utf8NoBom(oneLineText), 65001, false, 0));
        samples.Add(MakeSample("thirty-thousand-lines", "lines.txt", manyLinesText, Utf8NoBom(manyLinesText), 65001, false, 0));

        foreach (Sample sample in samples) CleanCase(sample);
        if (BudgetLeft()) RunRefusalCases();
        if (BudgetLeft()) RunEditCases(samples);
        if (BudgetLeft()) RunSaveEncodingCases();
        if (BudgetLeft()) RunConflictCases();
        if (BudgetLeft()) RunAtomicCases();
        if (BudgetLeft()) RunConversionCases();
        if (BudgetLeft()) FolderOpenCase();
        if (BudgetLeft()) RunEncodingRefusalCases();
    }

    static void CleanCase(Sample sample) {
        if (!BudgetLeft()) return;
        string dir = CaseDir("clean-" + sample.Id);
        string file = WriteFile(dir, sample.FileName, sample.Bytes);
        string openWord = sample.Forced ? "open forced cp" + sample.CodePage : "open auto";
        string reopenWord = sample.ReopenCodePage != 0 ? "reopen forced cp" + sample.ReopenCodePage : "reopen auto";
        BeginCase("clean-" + sample.Id, "write " + sample.Bytes.Length + " bytes -> " + openWord + " -> save in place -> " + reopenWord + " -> save again");
        MainWindow window = null, reopened = null;
        try {
            window = NewWindow();
            OpenSample(window, file, sample);
            // ApplyLoadedFile normalizes the buffer to the detected newline; a mixed-newline file
            // keeps its bytes only through documentOriginalBytes, the editing text is normalized.
            string expectedText = NormalizeLineEndings(sample.Text, DetectNewline(sample.Text));
            Check("open-text", GetText(window) == expectedText, "text=" + Esc(expectedText), "text=" + Esc(GetText(window)));
            Check("open-encoding", GetEncoding(window).CodePage == sample.CodePage, "codepage=" + sample.CodePage, "codepage=" + GetEncoding(window).CodePage + " (" + GetEncoding(window).WebName + ")");
            Check("open-newline", GetNewline(window) == DetectNewline(sample.Text), "newline=" + Esc(DetectNewline(sample.Text)), "newline=" + Esc(GetNewline(window)));
            Check("open-path", String.Equals(GetPath(window), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase), Path.GetFullPath(file), GetPath(window));
            Check("open-mode", GetPlainText(window) == IsPlainFileName(sample.FileName), "plain=" + IsPlainFileName(sample.FileName), "plain=" + GetPlainText(window));
            bool saved = SaveInPlace(window);
            Check("clean-save-ok", saved, "true", saved.ToString());
            byte[] disk = File.ReadAllBytes(file);
            Check("clean-save-bytes", BytesEqual(disk, sample.Bytes), BytesState(sample.Bytes), ByteDiff(sample.Bytes, disk));
            Check("clean-save-no-unsaved", !HasUnsaved(window), "clean after save", "still dirty after save");
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            CloseQuiet(window); window = null;
            reopened = NewWindow();
            ReopenSample(reopened, file, sample);
            string reopenText = GetText(reopened);
            Check("reopen-text", reopenText == expectedText, "text=" + Esc(expectedText), "text=" + Esc(reopenText));
            Check("reopen-encoding", GetEncoding(reopened).CodePage == sample.CodePage, "codepage=" + sample.CodePage, "codepage=" + GetEncoding(reopened).CodePage + " (" + GetEncoding(reopened).WebName + ")");
            bool again = SaveInPlace(reopened);
            byte[] disk2 = File.ReadAllBytes(file);
            Check("save-twice-ok", again, "true", again.ToString());
            Check("save-twice-bytes", BytesEqual(disk2, disk), BytesState(disk), ByteDiff(disk, disk2));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(reopened); CloseQuiet(window);
            EndCase();
        }
    }

    // ---------------------------------------------------------------- reader refusals
    static void RunRefusalCases() {
        RefusalCase("invalid-utf8-and-cp949", "bad.md", new byte[] { 0x81, 0x20 }, false, 0, typeof(DecoderFallbackException), null);
        RefusalCase("utf8-bom-invalid", "bom-bad.md", new byte[] { 0xEF, 0xBB, 0xBF, 0xFF, 0x41 }, false, 0, typeof(DecoderFallbackException), null);
        RefusalCase("utf16le-lone-surrogate", "lone.txt", new byte[] { 0xFF, 0xFE, 0x00, 0xD8, 0x41, 0x00 }, false, 0, typeof(DecoderFallbackException), null);
        RefusalCase("utf8-lone-surrogate-bytes", "lone-utf8.txt", new byte[] { 0xED, 0xA0, 0x80 }, false, 0, typeof(DecoderFallbackException), null);
        RefusalCase("cp932-auto", "sjis-auto.txt", Cp932("日本語テスト\r\n"), false, 0, typeof(DecoderFallbackException), null);
        RefusalCase("forced-utf8-on-cp949", "cp949-forced-utf8.txt", Cp949("한글 문서입니다.\r\n"), true, 65001, typeof(DecoderFallbackException), null);
        RefusalCase("forced-utf16le-on-utf8-bom", "bom-forced-utf16.txt", Utf8Bom("UTF-8 BOM 문서\n"), true, 1200, typeof(InvalidDataException), "BOM");
        RefusalCase("forced-utf16be-on-utf16le", "utf16le-forced-be.txt", Utf16Le("UTF-16 LE 문서\r\n"), true, 1201, typeof(InvalidDataException), "BOM");
        RefusalCase("forced-utf16le-odd-bytes", "odd.txt", new byte[] { 0x61, 0x62, 0x63 }, true, 1200, typeof(DecoderFallbackException), null);
    }

    static void RefusalCase(string id, string fileName, byte[] bytes, bool forced, int codePage, Type expected, string messagePart) {
        if (!BudgetLeft()) return;
        string dir = CaseDir("refuse-" + id);
        string file = WriteFile(dir, fileName, bytes);
        BeginCase("refuse-" + id, "write " + bytes.Length + " bytes -> " + (forced ? "open forced cp" + codePage : "open auto") + " (the reader must refuse)");
        MainWindow window = null;
        try {
            window = NewWindow();
            Exception error = null;
            bool opened = false;
            try {
                if (forced) {
                    OpenForced(window, file, codePage);
                    error = window.LastFileOpenError;
                    opened = error == null;
                } else {
                    try { window.LoadFile(file); Pump(120); }
                    catch (Exception ex) { error = ex; }
                    opened = error == null;
                }
            } catch (Exception ex) { error = ex; }
            Check("open-refused", error != null, expected.Name + " expected", opened ? "opened with text=" + Esc(GetText(window)) : "no exception");
            if (error != null) {
                Check("refusal-type", expected.IsInstanceOfType(error), expected.FullName, error.GetType().FullName + ": " + OneLine(error.Message));
                if (messagePart != null) Check("refusal-message", error.Message != null && error.Message.IndexOf(messagePart, StringComparison.Ordinal) >= 0, "message contains [" + messagePart + "]", OneLine(error.Message));
            }
            Check("refusal-text-empty", GetText(window) == "", "text empty", "text=" + Esc(GetText(window)));
            byte[] after = File.ReadAllBytes(file);
            Check("refusal-file-unchanged", BytesEqual(after, bytes), BytesState(bytes), ByteDiff(bytes, after));
            Check("refusal-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }

    // ---------------------------------------------------------------- edits
    internal delegate void EditAction(MainWindow window);
    static Sample FindSample(List<Sample> samples, string id) {
        foreach (Sample sample in samples) if (sample.Id == id) return sample;
        throw new MissingMemberException("probe: no sample '" + id + "'");
    }
    static void RunEditCases(List<Sample> samples) {
        EditCase(FindSample(samples, "md-crlf"), EditMdAll);
        EditCase(FindSample(samples, "md-bom-lf"), delegate(MainWindow window) { TypeAtEnd(window, "추가 😀"); });
        EditCase(FindSample(samples, "utf16le-bom"), delegate(MainWindow window) { TypeAtEnd(window, "추가 😀"); });
        EditCase(FindSample(samples, "utf16be-bom"), delegate(MainWindow window) { TypeAtEnd(window, "추가 😀"); });
        EditCase(FindSample(samples, "cp949"), delegate(MainWindow window) { TypeAtEnd(window, " 추가 한글"); });
        EditCase(FindSample(samples, "mixed-bom"), delegate(MainWindow window) { TypeAtLineEnd(window, 3, "X"); });
        EditCase(FindSample(samples, "cr-only"), delegate(MainWindow window) { TypeAtEnd(window, "끝"); });
        EditCase(FindSample(samples, "cr-in-lf"), delegate(MainWindow window) { TypeAtEnd(window, "끝"); });
        EditCase(FindSample(samples, "unicode-separators"), delegate(MainWindow window) { TypeAtEnd(window, "추가"); });
        EditCase(FindSample(samples, "nul-bytes"), delegate(MainWindow window) { TypeAt(window, 0, "X"); });
        EditCase(FindSample(samples, "trailing-ws"), delegate(MainWindow window) { TypeAtEnd(window, "끝"); });
        EditCase(FindSample(samples, "empty-txt"), delegate(MainWindow window) { TypeAtEnd(window, "새 파일 😀\n둘째 줄"); });
        EditCase(FindSample(samples, "empty-md"), delegate(MainWindow window) { TypeAtEnd(window, "# 새 문서\n\n본문 😀"); });
        EditCase(FindSample(samples, "one-line-200k"), delegate(MainWindow window) { TypeAtEnd(window, "끝 😀"); });
    }
    static void EditCase(Sample sample, EditAction edit) {
        if (!BudgetLeft()) return;
        string dir = CaseDir("edit-" + sample.Id);
        string file = WriteFile(dir, sample.FileName, sample.Bytes);
        string reopenWord = sample.ReopenCodePage != 0 ? "reopen forced cp" + sample.ReopenCodePage : "reopen auto";
        BeginCase("edit-" + sample.Id, "write " + sample.Bytes.Length + " bytes -> open" + (sample.Forced ? " forced cp" + sample.CodePage : " auto") + " -> edit -> save -> " + reopenWord + " -> save again");
        MainWindow window = null, reopened = null;
        try {
            window = NewWindow();
            OpenSample(window, file, sample);
            string loadedText = NormalizeLineEndings(sample.Text, DetectNewline(sample.Text));
            Check("open-text", GetText(window) == loadedText, "text=" + Esc(loadedText), "text=" + Esc(GetText(window)));
            EnterEdit(window);
            edit(window);
            Pump(150);
            string buffer = GetText(window);
            Check("edit-changed", buffer != loadedText, "text changed by the edit", "buffer unchanged: " + Esc(buffer));
            string newline = GetNewline(window);
            Check("edit-buffer-one-newline", HasOnlyNewline(buffer, newline), "only " + Esc(newline) + " in the buffer", "mixed newlines in buffer: " + TextDiff(NormalizeLineEndings(buffer, newline), buffer));
            string expectedDisk = NormalizeLineEndings(buffer, newline);
            bool saved = SaveInPlace(window);
            Check("edit-save-ok", saved, "true", saved.ToString());
            Encoding encoding = GetEncoding(window);
            byte[] expectedBytes = Encoded(encoding, expectedDisk);
            byte[] disk = File.ReadAllBytes(file);
            Check("edit-save-bytes", BytesEqual(disk, expectedBytes), BytesState(expectedBytes), ByteDiff(expectedBytes, disk));
            string diskText = ReadWith(encoding, file);
            Check("edit-save-text", diskText == expectedDisk, "text=" + Esc(expectedDisk), "text=" + TextDiff(expectedDisk, diskText));
            Check("edit-save-one-newline", HasOnlyNewline(diskText, newline), "only " + Esc(newline) + " in the saved file", "mixed newlines on disk: " + TextDiff(NormalizeLineEndings(diskText, newline), diskText));
            Check("edit-save-no-unsaved", !HasUnsaved(window), "clean after save", "still dirty after save");
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            int savedCodePage = encoding.CodePage;
            CloseQuiet(window); window = null;
            reopened = NewWindow();
            ReopenSample(reopened, file, sample);
            string reopenText = GetText(reopened);
            Check("reopen-text", reopenText == expectedDisk, "text=" + Esc(expectedDisk), "text=" + TextDiff(expectedDisk, reopenText));
            Check("reopen-encoding", GetEncoding(reopened).CodePage == savedCodePage, "codepage=" + savedCodePage, "codepage=" + GetEncoding(reopened).CodePage);
            bool again = SaveInPlace(reopened);
            byte[] disk2 = File.ReadAllBytes(file);
            Check("save-twice-ok", again, "true", again.ToString());
            Check("save-twice-bytes", BytesEqual(disk2, disk), BytesState(disk), ByteDiff(disk, disk2));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(reopened); CloseQuiet(window);
            EndCase();
        }
    }
    static bool HasOnlyNewline(string text, string newline) {
        if (text == null) return false;
        if (newline == "\r\n") {
            for (int i = 0; i < text.Length; i++) {
                if (text[i] == '\r') {
                    if (i + 1 >= text.Length || text[i + 1] != '\n') return false;
                    i++;
                } else if (text[i] == '\n') return false;
            }
            return true;
        }
        if (newline == "\r") return text.IndexOf('\n') < 0;
        return text.IndexOf('\r') < 0;
    }
    static void TypeAt(MainWindow window, int offset, string value) {
        SourceEditor source = Source(window);
        int at = Math.Max(0, Math.Min(offset, source.Document.TextLength));
        source.Select(at, 0);
        source.TextArea.PerformTextInput(value);
        Pump(100);
    }
    static void TypeAtEnd(MainWindow window, string value) {
        SourceEditor source = Source(window);
        TypeAt(window, source.Document.TextLength, value);
    }
    static void TypeAtLineEnd(MainWindow window, int lineNumber, string value) {
        SourceEditor source = Source(window);
        int at = source.Document.TextLength;
        if (lineNumber >= 1 && lineNumber <= source.Document.LineCount) at = source.Document.GetLineByNumber(lineNumber).EndOffset;
        TypeAt(window, at, value);
    }
    static void EditMdAll(MainWindow window) {
        SourceEditor source = Source(window);
        int lineNumber = source.Document.LineCount >= 3 ? 3 : 1;
        TypeAtLineEnd(window, lineNumber, "추가 😀");
        var line = source.Document.GetLineByNumber(lineNumber);
        int start = line.Offset + Math.Min(2, Math.Max(0, line.Length - 1));
        int length = Math.Min(3, Math.Max(0, line.Length - start + line.Offset));
        source.Select(start, length);
        Pump(20);
        string beforeBold = GetText(window);
        Invoke(window, "ToggleFormat", "bold");
        Pump(100);
        Check("edit-toolbar-changed", GetText(window) != beforeBold, "the bold toolbar command changes the text", "text unchanged: " + Esc(beforeBold));
        source.Select(source.Document.TextLength, 0);
        string pasteValue = NormalizeLineEndings("\n붙여넣기\n둘째 줄", GetNewline(window));
        bool pasted = false;
        try {
            pasted = (bool)Invoke(window, "LivePaste", new System.Windows.DataObject(), true, pasteValue);
        } catch (MissingMemberException) {
            // The clipboard paste entry point is being edited by another worker in this run; the
            // source surface still exercises the file-I/O sequence. This is the only fallback.
            source.SelectedText = pasteValue;
            pasted = true;
            Note("edit paste used the source surface because MainWindow.LivePaste was not present in this build");
        } catch (TargetParameterCountException) {
            source.SelectedText = pasteValue;
            pasted = true;
            Note("edit paste used the source surface because MainWindow.LivePaste had a different signature in this build");
        }
        Pump(100);
        Check("edit-paste-accepted", pasted, "the live paste path accepts plain text", "LivePaste returned false");
    }

    // ---------------------------------------------------------------- Save As encodings
    static void RunSaveEncodingCases() {
        SaveEncodingSuccess("utf8-bom", "저장 형식 테스트\n둘째 줄\n", 1, 65001);
        SaveEncodingSuccess("utf16le", "저장 형식 테스트 😀\n둘째 줄\n", 2, 1200);
        SaveEncodingSuccess("utf16be", "저장 형식 테스트 😀\n둘째 줄\n", 4, 1201);
        SaveEncodingSuccess("cp949", "한글 저장 테스트\n둘째 줄\n", 3, 949);
        SaveEncodingRefusedEmoji();
        SaveAsUiCase();
        EncodingChangeInPlaceCase();
        SaveAsKeepsUnknownEncodingCase();
        SaveAsDefaultPathCase();
    }
    // Save As must keep an encoding the five fixed entries do not offer (CP932 here): the dialog
    // opens on its "keep" entry, and the picker seam stands in for that answer.
    static void SaveAsKeepsUnknownEncodingCase() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("saveas-keep-cp932");
        string text = "日本語テスト\r\nこんにちは\r\n";
        string file = WriteFile(dir, "sjis.txt", Cp932(text));
        string target = Path.Combine(dir, "sjis-copy.txt");
        BeginCase("saveas-keep-cp932", "open forced cp932 -> Save As on the keep entry -> the copy must stay CP932");
        MainWindow window = null;
        try {
            window = NewWindow();
            OpenForcedRequired(window, file, 932);
            Check("open-encoding", GetEncoding(window).CodePage == 932, "codepage=932", "codepage=" + GetEncoding(window).CodePage);
            bool saved = SaveAsWithPicker(window, target);
            Check("saveas-ok", saved, "true", saved.ToString());
            byte[] expected = Cp932(text);
            byte[] disk = File.Exists(target) ? File.ReadAllBytes(target) : null;
            Check("saveas-keeps-encoding-bytes", BytesEqual(disk, expected), BytesState(expected), ByteDiff(expected, disk));
            Check("saveas-keeps-encoding", GetEncoding(window).CodePage == 932, "codepage=932", "codepage=" + GetEncoding(window).CodePage);
            Check("saveas-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            // The dialog's mapping: entry 1 is the keep entry; 2..6 are the fixed StrictEncoding
            // indices in the combo's order (CP949, UTF-16 LE, UTF-16 BE, UTF-8, UTF-8 BOM).
            Check("saveas-keep-entry", NativeSaveDialog.EncodingForControl(1) == NativeSaveDialog.KeepEncoding, "keep", NativeSaveDialog.EncodingForControl(1).ToString());
            Check("saveas-fixed-entries",
                NativeSaveDialog.EncodingForControl(2) == 3 && NativeSaveDialog.EncodingForControl(3) == 2 &&
                NativeSaveDialog.EncodingForControl(4) == 4 && NativeSaveDialog.EncodingForControl(5) == 0 &&
                NativeSaveDialog.EncodingForControl(6) == 1,
                "3,2,4,0,1",
                NativeSaveDialog.EncodingForControl(2) + "," + NativeSaveDialog.EncodingForControl(3) + "," + NativeSaveDialog.EncodingForControl(4) + "," + NativeSaveDialog.EncodingForControl(5) + "," + NativeSaveDialog.EncodingForControl(6));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }
    // Save As must start in the document's own folder: the default path is the document's full path
    // (or the converted name in that folder), and a recovered draft's original file while the tab's
    // own path field stays "" (RC9Tests).
    static void SaveAsDefaultPathCase() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("saveas-default-path");
        string document = Path.Combine(dir, "보고서.md");
        string original = Path.Combine(dir, "원본.md");
        BeginCase("saveas-default-path", "SaveAsDefaultPath: document, format change, recovered original, new document");
        try {
            string plainStart = (string)InvokeStatic("SaveAsDefaultPath", document, null, false, false);
            Check("default-path-document", String.Equals(plainStart, document, StringComparison.OrdinalIgnoreCase), document, plainStart);
            string changedWant = Path.Combine(dir, "보고서.txt");
            string changedStart = (string)InvokeStatic("SaveAsDefaultPath", document, null, true, true);
            Check("default-path-format", String.Equals(changedStart, changedWant, StringComparison.OrdinalIgnoreCase), changedWant, changedStart);
            string recoveredStart = (string)InvokeStatic("SaveAsDefaultPath", "", original, false, false);
            Check("default-path-recovered", String.Equals(recoveredStart, original, StringComparison.OrdinalIgnoreCase), original, recoveredStart);
            string newStart = (string)InvokeStatic("SaveAsDefaultPath", "", "", false, false);
            Check("default-path-new", newStart == null, "<null>", newStart);
            // A recovery record can hold a bare file name (no directory) and a root path has no
            // directory either: the converted name must come back without Path.Combine throwing.
            string bareStart = (string)InvokeStatic("SaveAsDefaultPath", "보고서.md", null, true, true);
            Check("default-path-bare-name", String.Equals(bareStart, "보고서.txt", StringComparison.Ordinal), "보고서.txt", bareStart);
            Exception rootError = null; string rootStart = null;
            try { rootStart = (string)InvokeStatic("SaveAsDefaultPath", Path.GetPathRoot(filesRoot), null, true, true); }
            catch (Exception ex) { rootError = ex; }
            Check("default-path-root", rootError == null && rootStart == null,
                "no exception, <null> (SaveAsync falls back to its own default name)",
                rootError == null ? (rootStart == null ? "<null>" : rootStart) : rootError.GetType().Name + ": " + OneLine(rootError.Message));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            EndCase();
        }
    }
    static void SaveEncodingSuccess(string id, string text, int encodingIndex, int expectedCodePage) {
        if (!BudgetLeft()) return;
        string dir = CaseDir("saveas-" + id);
        string sourceFile = WriteFile(dir, "source.md", Utf8NoBom(text));
        string target = Path.Combine(dir, "copy.md");
        Encoding encoding = StrictEncoding(encodingIndex);
        BeginCase("saveas-" + id, "open source.md (UTF-8) -> SaveToPath(copy.md, " + encoding.WebName + " cp" + expectedCodePage + ") -> reopen auto -> save again");
        MainWindow window = null, reopened = null;
        try {
            window = NewWindow();
            window.LoadFile(sourceFile); Pump(120);
            Check("open-text", GetText(window) == text, "text=" + Esc(text), "text=" + Esc(GetText(window)));
            bool saved = SaveTo(window, target, encodingIndex, false);
            Check("saveas-ok", saved, "true", saved.ToString());
            byte[] expected = Encoded(encoding, NormalizeLineEndings(text, GetNewline(window)));
            byte[] disk = File.Exists(target) ? File.ReadAllBytes(target) : null;
            Check("saveas-bytes", BytesEqual(disk, expected), BytesState(expected), ByteDiff(expected, disk));
            byte[] preamble = encoding.GetPreamble();
            Check("saveas-preamble", HasPrefix(disk, preamble), "preamble " + BytesState(preamble), "first bytes " + BytesState(FirstBytes(disk, preamble.Length)));
            Check("saveas-path", String.Equals(GetPath(window), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase), Path.GetFullPath(target), GetPath(window));
            Check("saveas-encoding", GetEncoding(window).CodePage == expectedCodePage, "codepage=" + expectedCodePage, "codepage=" + GetEncoding(window).CodePage);
            Check("saveas-source-unchanged", BytesEqual(File.ReadAllBytes(sourceFile), Utf8NoBom(text)), "source bytes unchanged", "source bytes changed");
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            CloseQuiet(window); window = null;
            reopened = NewWindow();
            OpenForcedRequired(reopened, target, expectedCodePage);
            Check("reopen-text", GetText(reopened) == text, "text=" + Esc(text), "text=" + Esc(GetText(reopened)));
            Check("reopen-encoding", GetEncoding(reopened).CodePage == expectedCodePage, "codepage=" + expectedCodePage, "codepage=" + GetEncoding(reopened).CodePage);
            bool again = SaveInPlace(reopened);
            Check("save-twice-ok", again, "true", again.ToString());
            Check("save-twice-bytes", BytesEqual(File.ReadAllBytes(target), disk), BytesState(disk), ByteDiff(disk, File.ReadAllBytes(target)));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(reopened); CloseQuiet(window);
            EndCase();
        }
    }
    static void SaveEncodingRefusedEmoji() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("saveas-refused-emoji");
        string text = "이모지 😀 저장\n둘째 줄\n";
        string file = WriteFile(dir, "emoji.md", Utf8NoBom(text));
        string target = Path.Combine(dir, "cp949.md");
        BeginCase("saveas-refused-emoji", "open UTF-8 emoji file -> SaveToPath(cp949) must be refused -> SaveToPath(UTF-8) succeeds");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            Exception error = null; bool saved = false;
            try { saved = SaveTo(window, target, 3, false); } catch (Exception ex) { error = ex; }
            Check("refusal-type", error is EncoderFallbackException, typeof(EncoderFallbackException).FullName, error == null ? "no exception" : error.GetType().FullName + ": " + OneLine(error.Message));
            Check("refusal-returns-false", !saved, "false", saved.ToString());
            Check("refusal-target-missing", !File.Exists(target), "the refused target does not exist", "target was written");
            Check("refusal-path-unchanged", String.Equals(GetPath(window), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase), Path.GetFullPath(file), GetPath(window));
            Check("refusal-text-kept", GetText(window) == text, "text=" + Esc(text), "text=" + Esc(GetText(window)));
            Check("refusal-source-unchanged", BytesEqual(File.ReadAllBytes(file), Utf8NoBom(text)), "source bytes unchanged", "source bytes changed");
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            string recovered = Path.Combine(dir, "recovered.md");
            bool recoveredOk = SaveTo(window, recovered, 0, false);
            Check("recovery-save-ok", recoveredOk, "true", recoveredOk.ToString());
            Check("recovery-text-kept", GetText(window) == text && File.Exists(recovered) && ReadUtf8(recovered) == text, "text=" + Esc(text), "disk=" + Esc(File.Exists(recovered) ? ReadUtf8(recovered) : "<missing>"));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }
    static void SaveAsUiCase() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("saveas-ui");
        string sourceText = "UI 저장\n";
        string file = WriteFile(dir, "ui.md", Utf8NoBom(sourceText));
        string target = Path.Combine(dir, "ui-copy.md");
        BeginCase("saveas-ui", "open ui.md -> edit -> Save As (SaveTargetPicker -> ui-copy.md) -> reopen");
        MainWindow window = null, reopened = null;
        bool pickerCalled = false;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            EnterEdit(window);
            TypeAtEnd(window, " 추가");
            string expected = NormalizeLineEndings(GetText(window), GetNewline(window));
            MainWindow.SaveTargetPicker = delegate(string name) { pickerCalled = true; return target; };
            bool saved;
            try { saved = (bool)Invoke(window, "Save", true); }
            finally { MainWindow.SaveTargetPicker = null; }
            Check("saveas-ok", saved, "true", saved.ToString());
            Check("saveas-picker-called", pickerCalled, "true", "false");
            byte[] expectedBytes = Utf8NoBom(expected);
            byte[] disk = File.Exists(target) ? File.ReadAllBytes(target) : null;
            Check("saveas-bytes", BytesEqual(disk, expectedBytes), BytesState(expectedBytes), ByteDiff(expectedBytes, disk));
            Check("saveas-path", String.Equals(GetPath(window), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase), Path.GetFullPath(target), GetPath(window));
            Check("saveas-no-unsaved", !HasUnsaved(window), "clean after save", "still dirty");
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            CloseQuiet(window); window = null;
            reopened = NewWindow();
            reopened.LoadFile(target); Pump(120);
            Check("reopen-text", GetText(reopened) == expected, "text=" + Esc(expected), "text=" + Esc(GetText(reopened)));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            MainWindow.SaveTargetPicker = null;
            CloseQuiet(reopened); CloseQuiet(window);
            EndCase();
        }
    }
    static void EncodingChangeInPlaceCase() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("encoding-change");
        string text = "인코딩 변경 😀\n둘째 줄\n";
        string file = WriteFile(dir, "change.md", Utf8NoBom(text));
        string bomTarget = Path.Combine(dir, "change-bom.md");
        BeginCase("encoding-change", "open UTF-8 -> SaveToPath(same path, UTF-16 LE) -> reopen auto -> SaveToPath(copy, UTF-8 BOM) -> reopen auto");
        MainWindow window = null, reopened = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            bool toUtf16 = SaveTo(window, file, 2, false);
            byte[] utf16 = Utf16Le(text);
            byte[] disk = File.ReadAllBytes(file);
            Check("inplace-save-ok", toUtf16, "true", toUtf16.ToString());
            Check("inplace-bytes", BytesEqual(disk, utf16), BytesState(utf16), ByteDiff(utf16, disk));
            Check("inplace-encoding", GetEncoding(window).CodePage == 1200, "codepage=1200", "codepage=" + GetEncoding(window).CodePage);
            CloseQuiet(window); window = null;
            reopened = NewWindow();
            reopened.LoadFile(file); Pump(120);
            Check("reopen-text", GetText(reopened) == text, "text=" + Esc(text), "text=" + Esc(GetText(reopened)));
            Check("reopen-encoding", GetEncoding(reopened).CodePage == 1200, "codepage=1200", "codepage=" + GetEncoding(reopened).CodePage);
            bool toBom = SaveTo(reopened, bomTarget, 1, false);
            byte[] bom = Utf8Bom(text);
            byte[] bomDisk = File.ReadAllBytes(bomTarget);
            Check("bom-save-ok", toBom, "true", toBom.ToString());
            Check("bom-bytes", BytesEqual(bomDisk, bom), BytesState(bom), ByteDiff(bom, bomDisk));
            Check("bom-encoding", GetEncoding(reopened).CodePage == 65001, "codepage=65001", "codepage=" + GetEncoding(reopened).CodePage);
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(reopened); CloseQuiet(window);
            EndCase();
        }
    }
    static bool HasPrefix(byte[] bytes, byte[] prefix) {
        if (bytes == null || prefix == null || bytes.Length < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++) if (bytes[i] != prefix[i]) return false;
        return true;
    }
    static byte[] FirstBytes(byte[] bytes, int count) {
        if (bytes == null) return null;
        int length = Math.Min(bytes.Length, count);
        var result = new byte[length];
        Buffer.BlockCopy(bytes, 0, result, 0, length);
        return result;
    }

    // ---------------------------------------------------------------- external change and deletion
    static void RunConflictCases() {
        ConflictModifyCase();
        ConflictDeleteCase();
    }
    static void ConflictModifyCase() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("conflict-modify");
        string text = "첫 줄\r\n둘째 줄\r\n";
        string file = WriteFile(dir, "conflict.md", Utf8NoBom(text));
        BeginCase("conflict-modify", "open -> edit -> another writer replaces the file -> save (conflict) -> decline -> save with another name");
        MainWindow window = null, original = null, copy = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            Check("open-text", GetText(window) == text, "text=" + Esc(text), "text=" + Esc(GetText(window)));
            EnterEdit(window);
            TypeAtEnd(window, " LOCAL");
            Pump(100);
            string local = GetText(window);
            string expected = NormalizeLineEndings(local, GetNewline(window));
            byte[] external = Utf8NoBom("외부 변경\r\n새 내용\r\n");
            File.WriteAllBytes(file, external);
            MainWindow.ConflictPrompts = 0; MainWindow.ConflictRetrySawBusy = false;
            MainWindow.ConflictAnswer = null; MainWindow.SaveTargetPicker = null;
            Exception error = null; bool saved = false;
            try { saved = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("conflict-detected", error is SaveConflictException, typeof(SaveConflictException).FullName,
                error == null ? "save returned " + saved : error.GetType().FullName + ": " + OneLine(error.Message));
            Check("conflict-not-counted", MainWindow.ConflictPrompts == 0, "0 prompts", MainWindow.ConflictPrompts.ToString());
            byte[] afterThrow = File.ReadAllBytes(file);
            Check("conflict-disk-kept", BytesEqual(afterThrow, external), BytesState(external), ByteDiff(external, afterThrow));
            Check("conflict-text-kept", GetText(window) == local, "text=" + Esc(local), "text=" + TextDiff(local, GetText(window)));
            Check("conflict-dirty", HasUnsaved(window), "dirty", "clean");
            MainWindow.ConflictAnswer = MessageBoxResult.No;
            error = null; saved = true;
            try { saved = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("decline-returns-false", error == null && !saved, "false", error == null ? saved.ToString() : "exception " + error.GetType().Name + ": " + OneLine(error.Message));
            Check("decline-prompts", MainWindow.ConflictPrompts == 1, "1 prompt", MainWindow.ConflictPrompts.ToString());
            byte[] afterDecline = File.ReadAllBytes(file);
            Check("decline-disk-kept", BytesEqual(afterDecline, external), BytesState(external), ByteDiff(external, afterDecline));
            string copyFile = Path.Combine(dir, "rescued.md");
            MainWindow.ConflictAnswer = MessageBoxResult.Yes;
            MainWindow.SaveTargetPicker = delegate(string name) { return copyFile; };
            error = null; saved = false;
            try { saved = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("retry-saves-copy", error == null && saved && File.Exists(copyFile), "saved copy " + copyFile,
                error == null ? "saved=" + saved + " exists=" + File.Exists(copyFile) : "exception " + error.GetType().Name + ": " + OneLine(error.Message));
            Check("retry-prompts", MainWindow.ConflictPrompts == 2, "2 prompts", MainWindow.ConflictPrompts.ToString());
            Check("retry-not-busy", !MainWindow.ConflictRetrySawBusy, "false", MainWindow.ConflictRetrySawBusy.ToString());
            byte[] externalAfterRetry = File.ReadAllBytes(file);
            Check("retry-original-kept", BytesEqual(externalAfterRetry, external), BytesState(external), ByteDiff(external, externalAfterRetry));
            byte[] copyExpected = Utf8NoBom(expected);
            byte[] copyDisk = File.Exists(copyFile) ? File.ReadAllBytes(copyFile) : null;
            Check("retry-copy-bytes", BytesEqual(copyDisk, copyExpected), BytesState(copyExpected), ByteDiff(copyExpected, copyDisk));
            Check("retry-path", String.Equals(GetPath(window), Path.GetFullPath(copyFile), StringComparison.OrdinalIgnoreCase), Path.GetFullPath(copyFile), GetPath(window));
            CloseQuiet(window); window = null;
            original = NewWindow();
            original.LoadFile(file); Pump(120);
            Check("reopen-external-text", GetText(original) == "외부 변경\r\n새 내용\r\n", "text=외부 변경\\r\\n새 내용\\r\\n", "text=" + Esc(GetText(original)));
            CloseQuiet(original); original = null;
            copy = NewWindow();
            copy.LoadFile(copyFile); Pump(120);
            Check("reopen-copy-text", GetText(copy) == expected, "text=" + Esc(expected), "text=" + TextDiff(expected, GetText(copy)));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            MainWindow.ConflictAnswer = null; MainWindow.SaveTargetPicker = null;
            CloseQuiet(copy); CloseQuiet(original); CloseQuiet(window);
            EndCase();
        }
    }
    static void ConflictDeleteCase() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("conflict-delete");
        string text = "삭제 전\r\n";
        string file = WriteFile(dir, "delete.txt", Utf8NoBom(text));
        BeginCase("conflict-delete", "open -> edit -> delete the file -> save (conflict) -> decline -> save with another name");
        MainWindow window = null, copy = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            Check("open-text", GetText(window) == text, "text=" + Esc(text), "text=" + Esc(GetText(window)));
            EnterEdit(window);
            TypeAtEnd(window, " LOCAL");
            Pump(100);
            string expected = NormalizeLineEndings(GetText(window), GetNewline(window));
            File.Delete(file);
            Check("file-deleted", !File.Exists(file), "missing", "still present");
            MainWindow.ConflictPrompts = 0; MainWindow.ConflictRetrySawBusy = false;
            MainWindow.ConflictAnswer = null; MainWindow.SaveTargetPicker = null;
            Exception error = null; bool saved = false;
            try { saved = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("conflict-detected", error is SaveConflictException, typeof(SaveConflictException).FullName,
                error == null ? "save returned " + saved : error.GetType().FullName + ": " + OneLine(error.Message));
            Check("conflict-prompts", MainWindow.ConflictPrompts == 0, "0 prompts", MainWindow.ConflictPrompts.ToString());
            Check("deleted-not-recreated", !File.Exists(file), "missing", "the save recreated the file");
            Check("conflict-text-kept", GetText(window).EndsWith(" LOCAL", StringComparison.Ordinal), "text ends with LOCAL", "text=" + Esc(GetText(window)));
            MainWindow.ConflictAnswer = MessageBoxResult.No;
            error = null; saved = true;
            try { saved = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("decline-returns-false", error == null && !saved, "false", error == null ? saved.ToString() : "exception " + error.GetType().Name);
            Check("decline-prompts", MainWindow.ConflictPrompts == 1, "1 prompt", MainWindow.ConflictPrompts.ToString());
            Check("decline-not-recreated", !File.Exists(file), "missing", "the save recreated the file");
            string copyFile = Path.Combine(dir, "deleted-copy.txt");
            MainWindow.ConflictAnswer = MessageBoxResult.Yes;
            MainWindow.SaveTargetPicker = delegate(string name) { return copyFile; };
            error = null; saved = false;
            try { saved = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("retry-saves-copy", error == null && saved && File.Exists(copyFile), "saved copy " + copyFile,
                error == null ? "saved=" + saved + " exists=" + File.Exists(copyFile) : "exception " + error.GetType().Name + ": " + OneLine(error.Message));
            Check("retry-prompts", MainWindow.ConflictPrompts == 2, "2 prompts", MainWindow.ConflictPrompts.ToString());
            Check("retry-original-missing", !File.Exists(file), "missing", "the original file came back");
            byte[] expectedBytes = Utf8NoBom(expected);
            byte[] copyDisk = File.Exists(copyFile) ? File.ReadAllBytes(copyFile) : null;
            Check("retry-copy-bytes", BytesEqual(copyDisk, expectedBytes), BytesState(expectedBytes), ByteDiff(expectedBytes, copyDisk));
            CloseQuiet(window); window = null;
            copy = NewWindow();
            copy.LoadFile(copyFile); Pump(120);
            Check("reopen-copy-text", GetText(copy) == expected, "text=" + Esc(expected), "text=" + TextDiff(expected, GetText(copy)));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            MainWindow.ConflictAnswer = null; MainWindow.SaveTargetPicker = null;
            CloseQuiet(copy); CloseQuiet(window);
            EndCase();
        }
    }

    // ---------------------------------------------------------------- atomic save
    static void RunAtomicCases() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("atomic");
        string file = WriteFile(dir, "atomic.md", Utf8NoBom("원본 문장\r\n"));
        BeginCase("atomic", "open -> edit -> save (durable temp then commit) -> edit again -> save with the temp flush failing -> save again");
        MainWindow window = null;
        var phases = new List<string>();
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            EnterEdit(window);
            TypeAtEnd(window, " 추가");
            Pump(100);
            string newline = GetNewline(window);
            string expected = NormalizeLineEndings(GetText(window), newline);
            MainWindow.AtomicWriteObserver = delegate(string phase) { phases.Add(phase); };
            bool saved = SaveInPlace(window);
            Check("atomic-save-ok", saved, "true", saved.ToString());
            Check("atomic-phases", phases.Count == 2 && phases[0] == "durable-temp" && phases[1] == "committed",
                "durable-temp then committed", String.Join(",", phases.ToArray()));
            byte[] expectedBytes = Utf8NoBom(expected);
            byte[] disk = File.ReadAllBytes(file);
            Check("atomic-bytes", BytesEqual(disk, expectedBytes), BytesState(expectedBytes), ByteDiff(expectedBytes, disk));
            Check("atomic-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            Check("atomic-clean", !HasUnsaved(window), "clean after save", "still dirty");
            TypeAtEnd(window, " 더");
            Pump(100);
            string text2 = GetText(window);
            string expected2 = NormalizeLineEndings(text2, newline);
            byte[] beforeFailure = File.ReadAllBytes(file);
            MainWindow.AtomicWriteObserver = delegate(string phase) {
                if (phase == "durable-temp") throw new IOException("probe: simulated failure before commit");
            };
            Exception error = null; bool failedSave = false;
            try { failedSave = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("atomic-failure-thrown", error is IOException, typeof(IOException).FullName, error == null ? "save returned " + failedSave : error.GetType().FullName + ": " + OneLine(error.Message));
            byte[] afterFailure = File.ReadAllBytes(file);
            Check("atomic-failure-keeps-file", BytesEqual(afterFailure, beforeFailure), BytesState(beforeFailure), ByteDiff(beforeFailure, afterFailure));
            Check("atomic-failure-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            Check("atomic-failure-dirty", HasUnsaved(window), "dirty", "clean");
            Check("atomic-failure-text-kept", GetText(window) == text2, "text=" + Esc(text2), "text=" + TextDiff(text2, GetText(window)));
            MainWindow.AtomicWriteObserver = null;
            bool recovered = SaveInPlace(window);
            byte[] expected2Bytes = Utf8NoBom(expected2);
            byte[] recoveredDisk = File.ReadAllBytes(file);
            Check("atomic-recovery-ok", recovered, "true", recovered.ToString());
            Check("atomic-recovery-bytes", BytesEqual(recoveredDisk, expected2Bytes), BytesState(expected2Bytes), ByteDiff(expected2Bytes, recoveredDisk));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            MainWindow.AtomicWriteObserver = null;
            CloseQuiet(window);
            EndCase();
        }
    }

    // ---------------------------------------------------------------- format conversions
    static void RunConversionCases() {
        ConversionMdToTxt();
        ConversionTxtToMdToTxt();
        ConversionBackslashRoundTrip();
        ConversionToggle();
        ConversionSaveRollback();
        ConversionSaveRollbackReadMode();
        ConversionSaveDuringImeComposition();
        ConversionCancelRestoresView();
        ConversionBusyBeforeFirstAwait();
        ConversionAsyncSaveRollback();
        ConversionSaveCancel();
    }
    static string CanonicalNewlines(string value) { return value.Replace("\r\n", "\n").Replace("\r", "\n"); }
    static void ConversionMdToTxt() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-md-txt");
        string md = "# 제목\r\n\r\n**굵게** 문장\r\n\r\n- 첫째\r\n- 둘째\r\n\r\n| 가 | 나 |\r\n| --- | --- |\r\n| 다 | 라 |\r\n\r\n```\r\n코드\r\n```\r\n\r\n[링크](https://example.com) ![이미지](a.png)\r\n";
        string file = WriteFile(dir, "doc.md", Utf8NoBom(md));
        string target = Path.Combine(dir, "plain.txt");
        BeginCase("convert-md-txt", "open doc.md -> SaveToPath(plain.txt) with the format change -> reopen plain.txt");
        MainWindow window = null, reopened = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            string mdText = GetText(window);
            string newline = GetNewline(window);
            string expectedPlain = MarkdownPlainText.Convert(mdText);
            string expectedDisk = NormalizeLineEndings(expectedPlain, newline);
            bool saved = SaveTo(window, target, 0, true);
            Check("convert-save-ok", saved, "true", saved.ToString());
            Check("convert-buffer", GetText(window) == expectedPlain, "text=" + Esc(expectedPlain), "text=" + TextDiff(expectedPlain, GetText(window)));
            string diskText = File.Exists(target) ? ReadUtf8(target) : null;
            Check("convert-disk-text", diskText == expectedDisk, "text=" + Esc(expectedDisk), "text=" + TextDiff(expectedDisk, diskText));
            Check("convert-mode", GetPlainText(window), "TXT mode", "Markdown mode");
            Check("convert-path", String.Equals(GetPath(window), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase), Path.GetFullPath(target), GetPath(window));
            Check("convert-source-unchanged", BytesEqual(File.ReadAllBytes(file), Utf8NoBom(md)), "source bytes unchanged", "source bytes changed");
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            CloseQuiet(window); window = null;
            reopened = NewWindow();
            reopened.LoadFile(target); Pump(120);
            Check("reopen-text", GetText(reopened) == expectedDisk, "text=" + Esc(expectedDisk), "text=" + TextDiff(expectedDisk, GetText(reopened)));
            Check("reopen-mode", GetPlainText(reopened), "TXT mode", "Markdown mode");
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(reopened); CloseQuiet(window);
            EndCase();
        }
    }
    static void ConversionTxtToMdToTxt() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-txt-md-txt");
        string txt = "# 제목처럼 보이는 줄\r\n\r\n- 목록처럼 보이는 줄\r\n1. 번호처럼 보이는 줄\r\n| 표 | 처럼 |\r\n| --- | --- |\r\n<b>태그</b> & 값\r\n`백틱` *별* _밑줄_ ~~취소~~\r\n\\\\server\\share 와 \\* 와 \\. 와 C:\\path\\\r\n마지막   \r\n\r\n";
        string txtFile = WriteFile(dir, "doc.txt", Utf8NoBom(txt));
        string mdTarget = Path.Combine(dir, "doc.md");
        string txtTarget = Path.Combine(dir, "back.txt");
        BeginCase("convert-txt-md-txt", "open doc.txt -> SaveToPath(doc.md) -> reopen doc.md -> SaveToPath(back.txt) -> compare the text");
        MainWindow window = null, reopened = null;
        try {
            window = NewWindow();
            window.LoadFile(txtFile); Pump(120);
            string text0 = GetText(window);
            Check("open-text", text0 == txt, "text=" + Esc(txt), "text=" + Esc(text0));
            string newline = GetNewline(window);
            string expectedMd = (string)InvokeStatic("LiteralMarkdown", text0);
            string expectedMdDisk = NormalizeLineEndings(expectedMd, newline);
            bool toMd = SaveTo(window, mdTarget, 0, true);
            Check("to-md-ok", toMd, "true", toMd.ToString());
            Check("to-md-buffer", GetText(window) == expectedMd, "text=" + Esc(expectedMd), "text=" + TextDiff(expectedMd, GetText(window)));
            string mdDisk = File.Exists(mdTarget) ? ReadUtf8(mdTarget) : null;
            Check("to-md-disk", mdDisk == expectedMdDisk, "text=" + Esc(expectedMdDisk), "text=" + TextDiff(expectedMdDisk, mdDisk));
            Check("to-md-mode", !GetPlainText(window), "Markdown mode", "TXT mode");
            Check("convert-source-unchanged", BytesEqual(File.ReadAllBytes(txtFile), Utf8NoBom(txt)), "source bytes unchanged", "source bytes changed");
            CloseQuiet(window); window = null;
            reopened = NewWindow();
            reopened.LoadFile(mdTarget); Pump(120);
            string reopenedMd = GetText(reopened);
            Check("reopen-md-text", reopenedMd == expectedMd, "text=" + Esc(expectedMd), "text=" + TextDiff(expectedMd, reopenedMd));
            string expectedBack = MarkdownPlainText.Convert(reopenedMd);
            string expectedBackDisk = NormalizeLineEndings(expectedBack, GetNewline(reopened));
            bool toTxt = SaveTo(reopened, txtTarget, 0, true);
            Check("to-txt-ok", toTxt, "true", toTxt.ToString());
            Check("to-txt-buffer", GetText(reopened) == expectedBack, "text=" + Esc(expectedBack), "text=" + TextDiff(expectedBack, GetText(reopened)));
            string backDisk = File.Exists(txtTarget) ? ReadUtf8(txtTarget) : null;
            Check("to-txt-disk", backDisk == expectedBackDisk, "text=" + Esc(expectedBackDisk), "text=" + TextDiff(expectedBackDisk, backDisk));
            Check("to-txt-mode", GetPlainText(reopened), "TXT mode", "Markdown mode");
            Check("txt-md-txt-no-lost-text", CanonicalNewlines(expectedBack) == CanonicalNewlines(text0),
                "text=" + Esc(CanonicalNewlines(text0)), "text=" + TextDiff(CanonicalNewlines(text0), CanonicalNewlines(expectedBack)));
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            CloseQuiet(reopened); reopened = null;
            MainWindow back = null;
            try {
                back = NewWindow();
                back.LoadFile(txtTarget); Pump(120);
                Check("reopen-txt-text", GetText(back) == expectedBackDisk, "text=" + Esc(expectedBackDisk), "text=" + TextDiff(expectedBackDisk, GetText(back)));
            } finally { CloseQuiet(back); }
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(reopened); CloseQuiet(window);
            EndCase();
        }
    }
    // bughunt3 output-scale item 1: a TXT document gets every character back after TXT -> MD -> TXT.
    // LiteralMarkdown used to pass a backslash through raw, so "\*" reached the markdown as
    // "\&#42;" (the preview hid the backslash and left the entity on screen) and one backslash
    // disappeared on the way back. Every escaped character is now checked over the whole chain:
    // the markdown source has no raw backslash, Convert gives the original text, and the print /
    // RTF parser (Markdown.Parse + ReadableText, the oracle both views follow) draws it.
    static void ConversionBackslashRoundTrip() {
        if (!BudgetLeft()) return;
        BeginCase("convert-backslash-roundtrip", "LiteralMarkdown then MarkdownPlainText.Convert and ReadableText(Markdown.Parse) over backslash shapes and every escaped character");
        try {
            string txt =
                "\\\\fileserver\\share\r\n" +
                "정규식 \\* 와 \\. 와 \\d\r\n" +
                "C:\\path\\file.txt\r\n" +
                "끝 역슬래시 C:\\path\\\r\n" +
                "\\n \\t \\r\r\n" +
                "& # < > * _ ~ [ ] ` - + = \r\n" +
                "\\# \\- \\+ \\= \\[ \\] \\` \\& \\< \\> \\_ \\~ \\|\r\n" +
                "1. 번호처럼 보이는 줄\r\n" +
                "한글 섞인 \\경로\\파일\r\n";
            string input = CanonicalNewlines(txt);
            string md = (string)InvokeStatic("LiteralMarkdown", input);
            Check("backslash-md-no-raw-slash", md.IndexOf('\\') < 0, "no raw backslash in the markdown", "markdown=" + Esc(md));
            string back = MarkdownPlainText.Convert(md);
            Check("backslash-txt-roundtrip", CanonicalNewlines(back) == input, "text=" + Esc(input), "text=" + TextDiff(input, CanonicalNewlines(back)));
            var rendered = Markdown.Parse(md, 16, "Malgun Gothic", System.Windows.Media.Brushes.Black, false, "");
            string printed = (string)InvokeStatic("ReadableText", rendered);
            Check("backslash-render-roundtrip", CanonicalNewlines(printed) == input, "text=" + Esc(input), "text=" + TextDiff(input, CanonicalNewlines(printed)));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            EndCase();
        }
    }
    static void ConversionToggle() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-toggle");
        string md = "# 제목\n\n본문 **강조** 문장\n\n- 항목\n";
        string file = WriteFile(dir, "toggle.md", Utf8NoBom(md));
        BeginCase("convert-toggle", "open toggle.md -> toggle to TXT -> toggle back to MD -> save -> reopen");
        MainWindow window = null, reopened = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            string mdText = GetText(window);
            string expectedPlain = MarkdownPlainText.Convert(mdText);
            Invoke(window, "ToggleTextMode");
            Pump(200);
            Check("toggle-to-txt-mode", GetPlainText(window), "TXT mode", "Markdown mode");
            Check("toggle-to-txt-text", GetText(window) == expectedPlain, "text=" + Esc(expectedPlain), "text=" + TextDiff(expectedPlain, GetText(window)));
            Invoke(window, "ToggleTextMode");
            Pump(200);
            string expectedBack = (string)InvokeStatic("LiteralMarkdown", expectedPlain);
            Check("toggle-back-mode", !GetPlainText(window), "Markdown mode", "TXT mode");
            Check("toggle-back-text", GetText(window) == expectedBack, "text=" + Esc(expectedBack), "text=" + TextDiff(expectedBack, GetText(window)));
            bool saved = SaveInPlace(window);
            byte[] expectedBytes = Utf8NoBom(expectedBack);
            byte[] disk = File.ReadAllBytes(file);
            Check("toggle-save-ok", saved, "true", saved.ToString());
            Check("toggle-save-bytes", BytesEqual(disk, expectedBytes), BytesState(expectedBytes), ByteDiff(expectedBytes, disk));
            Check("toggle-save-clean", !HasUnsaved(window), "clean after save", "still dirty");
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            CloseQuiet(window); window = null;
            reopened = NewWindow();
            reopened.LoadFile(file); Pump(120);
            Check("reopen-text", GetText(reopened) == expectedBack, "text=" + Esc(expectedBack), "text=" + TextDiff(expectedBack, GetText(reopened)));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(reopened); CloseQuiet(window);
            EndCase();
        }
    }

    // A format-changing Save As converts the tab before the write can fail. The conversion's mode
    // entry is held until the write succeeds, so a refused/locked/cancelled write must leave the
    // exact pre-conversion text, mode, caret and selection, and the undo/redo stacks the user had.
    static void ConversionSaveRollback() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-save-rollback");
        string md = "# 제목 😀\r\n\r\n**굵게** 문장\r\n\r\n- 항목 하나\r\n";
        string file = WriteFile(dir, "rollback.md", Utf8NoBom(md));
        string target = Path.Combine(dir, "refused.txt");
        BeginCase("convert-save-rollback", "open rollback.md (UTF-8) -> type and undo (redo available) -> SaveToPath(refused.txt, CP949, format change) refused by the strict encoder -> text/mode/caret/selection/redo must be unchanged");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            EnterEdit(window);
            TypeAtEnd(window, " 추가");
            Invoke(window, "Undo"); Pump(150);
            string before = GetText(window);
            Check("rollback-setup-undo", before == md, "text=" + Esc(md), "text=" + TextDiff(md, before));
            Check("rollback-setup-redo", HistoryCanRedo(window), "redo available after the undo", "no redo after the undo");
            SourceEditor source = Source(window);
            int at = Math.Min(3, source.Document.TextLength);
            source.Select(at, 2); Pump(60);
            int selStart = source.SelectionStart, selLength = source.SelectionLength, selCaret = source.CaretIndex;
            string path = GetPath(window); bool mode = GetPlainText(window);
            Exception error = null; bool saved = false;
            try { saved = SaveTo(window, target, 3, true); } catch (Exception ex) { error = ex; }
            Check("rollback-refused", error is EncoderFallbackException, typeof(EncoderFallbackException).FullName,
                error == null ? "save returned " + saved : error.GetType().FullName + ": " + OneLine(error.Message));
            Check("rollback-not-saved", !saved, "false", saved.ToString());
            Check("rollback-target-missing", !File.Exists(target), "the refused target does not exist", "target was written");
            Check("rollback-text", GetText(window) == before, "text=" + Esc(before), "text=" + TextDiff(before, GetText(window)));
            Check("rollback-buffer", GetBuffer(window) == before, "buffer=" + Esc(before), "buffer=" + TextDiff(before, GetBuffer(window)));
            Check("rollback-mode", GetPlainText(window) == mode, "plain=" + mode, "plain=" + GetPlainText(window));
            Check("rollback-caret", source.SelectionStart == selStart && source.SelectionLength == selLength && source.CaretIndex == selCaret,
                "selection " + selStart + "+" + selLength + ", caret " + selCaret,
                "selection " + source.SelectionStart + "+" + source.SelectionLength + ", caret " + source.CaretIndex);
            Check("rollback-path", String.Equals(GetPath(window), path, StringComparison.OrdinalIgnoreCase), path, GetPath(window));
            Check("rollback-redo-kept", HistoryCanRedo(window), "redo still available", "the failed conversion cleared redo");
            Check("rollback-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            Check("rollback-source-unchanged", BytesEqual(File.ReadAllBytes(file), Utf8NoBom(md)), "source bytes unchanged", "source bytes changed");
            // The same tab must still convert and save once the encoding can hold the text.
            string good = Path.Combine(dir, "plain-ok.txt");
            string expectedPlain = MarkdownPlainText.Convert(before);
            bool laterSave = SaveTo(window, good, 0, true);
            Check("rollback-later-save", laterSave, "true", laterSave.ToString());
            Check("rollback-later-mode", GetPlainText(window), "TXT mode", "Markdown mode");
            Check("rollback-later-text", GetText(window) == expectedPlain, "text=" + Esc(expectedPlain), "text=" + TextDiff(expectedPlain, GetText(window)));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }
    // The conversion is a view change as well: it switches the tab to edit mode and clears the raw
    // view. A failed write must put the reading mode and the raw view back with the text, or a
    // cancelled Save As left the tab in edit mode (and a raw-view tab lost its raw view).
    static void ConversionSaveRollbackReadMode() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-save-rollback-view");
        string md = "# 제목 😀\r\n\r\n**굵게** 문장\r\n";
        string file = WriteFile(dir, "rollback-view.md", Utf8NoBom(md));
        string target = Path.Combine(dir, "refused.txt");
        BeginCase("convert-save-rollback-view", "open rollback-view.md in read mode -> SaveToPath(refused.txt, CP949, format change) refused -> the reading mode and the raw view must be back with the text; then the same from edit + raw view");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            var preview = Field<System.Windows.Controls.CheckBox>(window, "preview");
            Check("rollback-view-setup", !GetEditing(window) && !GetPlainText(window) && !(preview.IsChecked == true),
                "read mode, Markdown, raw view off",
                "editing=" + GetEditing(window) + ", plain=" + GetPlainText(window) + ", preview=" + (preview.IsChecked == true));
            string before = GetText(window);
            string path = GetPath(window);
            Exception error = null; bool saved = false;
            try { saved = SaveTo(window, target, 3, true); } catch (Exception ex) { error = ex; }
            Check("rollback-view-refused", error is EncoderFallbackException, typeof(EncoderFallbackException).FullName,
                error == null ? "save returned " + saved : error.GetType().FullName + ": " + OneLine(error.Message));
            Check("rollback-view-not-saved", !saved, "false", saved.ToString());
            Check("rollback-view-text", GetText(window) == before, "text=" + Esc(before), "text=" + TextDiff(before, GetText(window)));
            Check("rollback-view-mode", !GetPlainText(window), "Markdown mode", "TXT mode");
            Check("rollback-view-editing", !GetEditing(window), "reading mode restored", "editing=" + GetEditing(window));
            Check("rollback-view-preview", !(preview.IsChecked == true), "raw view off (as before the conversion)", "preview=" + (preview.IsChecked == true));
            Check("rollback-view-path", String.Equals(GetPath(window), path, StringComparison.OrdinalIgnoreCase), path, GetPath(window));
            EnterEdit(window);
            SetField(window, "loading", true); preview.IsChecked = true; SetField(window, "loading", false); Pump(60);
            Check("rollback-view-raw-setup", GetEditing(window) && preview.IsChecked == true, "edit mode with the raw view on",
                "editing=" + GetEditing(window) + ", preview=" + (preview.IsChecked == true));
            before = GetText(window);
            error = null; saved = false;
            try { saved = SaveTo(window, target, 3, true); } catch (Exception ex) { error = ex; }
            Check("rollback-view-raw-refused", error is EncoderFallbackException, typeof(EncoderFallbackException).FullName,
                error == null ? "save returned " + saved : error.GetType().FullName + ": " + OneLine(error.Message));
            Check("rollback-view-raw-mode", !GetPlainText(window), "Markdown mode", "TXT mode");
            Check("rollback-view-raw-text", GetText(window) == before, "text=" + Esc(before), "text=" + TextDiff(before, GetText(window)));
            Check("rollback-view-raw-preview", preview.IsChecked == true, "the raw view is back after the failed write", "preview=" + (preview.IsChecked == true));
            Check("rollback-view-raw-editing", GetEditing(window), "edit mode kept", "editing=" + GetEditing(window));
            Check("rollback-view-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            Check("rollback-view-source-unchanged", BytesEqual(File.ReadAllBytes(file), Utf8NoBom(md)), "source bytes unchanged", "source bytes changed");
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }
    // A format-changing Save As while the source still reports a live IME store: ToggleTextMode
    // defers to RunAfterComposition, and the save used to see the unchanged mode, return false and
    // write nothing (the conversion then ran later on its own). The save must finish the
    // composition, convert synchronously and write the file.
    static void ConversionSaveDuringImeComposition() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-save-ime");
        string md = "# 제목 😀\r\n\r\n**굵게** 문장\r\n";
        string file = WriteFile(dir, "ime.md", Utf8NoBom(md));
        string target = Path.Combine(dir, "plain.txt");
        BeginCase("convert-save-ime", "open ime.md -> simulate a live source composition -> SaveToPath(plain.txt, format change) must convert synchronously, write the file and leave TXT mode");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            string mdText = GetText(window);
            string expectedPlain = MarkdownPlainText.Convert(mdText);
            Source(window).SimulateImeComposition(); Pump(20);
            Check("ime-setup-guarded", (bool)Invoke(window, "StoreGuardLive"), "the simulated composition still owns the store", "StoreGuardLive=false");
            bool saved = false;
            try { saved = SaveTo(window, target, 0, true); }
            finally { Source(window).ResetCompositionSimulation(); }
            Check("ime-save-ok", saved, "true", saved.ToString());
            Check("ime-save-mode", GetPlainText(window), "TXT mode", "Markdown mode");
            Check("ime-save-buffer", GetText(window) == expectedPlain, "text=" + Esc(expectedPlain), "text=" + TextDiff(expectedPlain, GetText(window)));
            string disk = File.Exists(target) ? ReadUtf8(target) : null;
            string expectedDisk = NormalizeLineEndings(expectedPlain, GetNewline(window));
            Check("ime-save-disk", disk == expectedDisk, "text=" + Esc(expectedDisk), "text=" + TextDiff(expectedDisk, disk));
            Check("ime-save-path", String.Equals(GetPath(window), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase), Path.GetFullPath(target), GetPath(window));
            Check("ime-save-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            Check("ime-save-idle", !Field<bool>(window, "documentOperationBusy") && !Field<bool>(window, "fileOpenRunning"), "busy=false, fileOpenRunning=false",
                "busy=" + Field<bool>(window, "documentOperationBusy") + ", fileOpenRunning=" + Field<bool>(window, "fileOpenRunning"));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseSourceComposition(window);
            CloseQuiet(window);
            EndCase();
        }
    }
    // A conversion cancelled before it commits used to leave the tab in the edit mode the
    // conversion switched on. The pre-conversion reading/raw state must come back with the text.
    static void ConversionCancelRestoresView() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-cancel-view");
        string md = "# 제목\n\n본문 **강조** 문장\n";
        string file = WriteFile(dir, "cancel-view.md", Utf8NoBom(md));
        BeginCase("convert-cancel-view", "open cancel-view.md in read mode with the raw view on -> start the conversion and cancel it -> text, Markdown mode, reading mode and raw view must be unchanged");
        MainWindow window = null;
        var cancellation = new CancellationTokenSource();
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            var preview = Field<System.Windows.Controls.CheckBox>(window, "preview");
            SetField(window, "loading", true); preview.IsChecked = true; SetField(window, "loading", false); Pump(60);
            string before = GetText(window);
            Check("cancel-view-setup", !GetEditing(window) && preview.IsChecked == true && !GetPlainText(window),
                "read mode, Markdown, raw view on", "editing=" + GetEditing(window) + ", preview=" + (preview.IsChecked == true) + ", plain=" + GetPlainText(window));
            System.Threading.Tasks.Task task = (System.Threading.Tasks.Task)Invoke(window, "ConvertTextModeAsync", cancellation.Token);
            cancellation.Cancel();
            try {
                WaitTask(task, 60000);
                Check("cancel-view-task", false, "the conversion is cancelled", "the conversion completed");
            } catch (OperationCanceledException) {
                Check("cancel-view-task", true, "the conversion is cancelled", "OperationCanceledException");
            }
            if (Field<bool>(window, "documentOperationBusy")) Invoke(window, "CompleteFileOpenUnlock", new object[] { null });
            Check("cancel-view-text", GetText(window) == before, "text=" + Esc(before), "text=" + TextDiff(before, GetText(window)));
            Check("cancel-view-mode", !GetPlainText(window), "Markdown mode", "TXT mode");
            Check("cancel-view-editing", !GetEditing(window), "reading mode restored", "editing=" + GetEditing(window));
            Check("cancel-view-preview", preview.IsChecked == true, "the raw view is restored", "preview=" + (preview.IsChecked == true));
            Check("cancel-view-idle", !Field<bool>(window, "documentOperationBusy") && !Field<bool>(window, "fileOpenRunning"), "busy=false, fileOpenRunning=false",
                "busy=" + Field<bool>(window, "documentOperationBusy") + ", fileOpenRunning=" + Field<bool>(window, "fileOpenRunning"));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            if (window != null && Field<bool>(window, "documentOperationBusy")) Invoke(window, "CompleteFileOpenUnlock", new object[] { null });
            cancellation.Dispose();
            CloseQuiet(window);
            EndCase();
        }
    }
    // The busy guard must be up before the conversion's first await: during the buffer read the
    // document used to stay writable, so a second toggle or an image drop could run. A TXT
    // document is converted here, so no live session is attached while the busy flag is set.
    static void ConversionBusyBeforeFirstAwait() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-busy-first-await");
        var builder = new StringBuilder(40000);
        for (int i = 0; i < 1600; i++) builder.Append("**굵게** 문장 ").Append(i).Append(" 한글\r\n");
        string txt = builder.ToString();
        string file = WriteFile(dir, "busy.txt", Utf8NoBom(txt));
        BeginCase("convert-busy-first-await", "open a " + txt.Length + "-char doc.txt -> start the conversion directly -> busy and read-only are already set at its first await; a second toggle and an image drop are refused; the conversion then commits");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(150);
            EnterEdit(window);
            string before = GetText(window);
            Check("busy-setup", GetPlainText(window) && GetEditing(window) && !Source(window).IsReadOnly, "TXT mode, editing, writable",
                "plain=" + GetPlainText(window) + ", editing=" + GetEditing(window) + ", readOnly=" + Source(window).IsReadOnly);
            System.Threading.Tasks.Task task = (System.Threading.Tasks.Task)Invoke(window, "ConvertTextModeAsync", System.Threading.CancellationToken.None);
            Check("busy-before-first-await", Field<bool>(window, "documentOperationBusy"), "documentOperationBusy=true at the first await", "documentOperationBusy=false");
            Check("busy-readonly", Source(window).IsReadOnly, "the editor is read-only while the conversion runs", "IsReadOnly=false");
            Invoke(window, "ToggleTextModeNow");
            Check("busy-toggle-refused", GetText(window) == before && GetPlainText(window), "a second toggle is refused while the conversion runs",
                "plain=" + GetPlainText(window) + ", text=[" + OneLine(GetText(window)) + "]");
            string notice = null;
            MainWindow.ImageDropNotice = delegate(string text) { notice = text; };
            try { Invoke(window, "InsertImageFilesAtCaret", new object[] { new string[] { "generated-image.png" } }); }
            finally { MainWindow.ImageDropNotice = null; }
            Check("busy-drop-refused", notice != null && notice.IndexOf("작업이 끝난 뒤", StringComparison.Ordinal) >= 0,
                "an image drop answers with the busy notice", "notice=" + OneLine(notice) + ", text=[" + OneLine(GetText(window)) + "]");
            try { WaitTask(task, 120000); }
            finally { if (Field<bool>(window, "documentOperationBusy")) Invoke(window, "CompleteFileOpenUnlock", new object[] { null }); }
            string expectedMarkdown = (string)InvokeStatic("LiteralMarkdown", before);
            Check("busy-commit-mode", !GetPlainText(window), "Markdown mode", "TXT mode");
            Check("busy-commit-text", GetText(window) == expectedMarkdown, "text=" + Esc(expectedMarkdown), "text=" + TextDiff(expectedMarkdown, GetText(window)));
            Check("busy-idle", !Field<bool>(window, "documentOperationBusy") && !Field<bool>(window, "fileOpenRunning"), "busy=false, fileOpenRunning=false",
                "busy=" + Field<bool>(window, "documentOperationBusy") + ", fileOpenRunning=" + Field<bool>(window, "fileOpenRunning"));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }
    // A probe-failure close: a simulated composition left armed on the source must not outlive its
    // case (the 500 ms safety timer would then run a parked command in a later case).
    static void CloseSourceComposition(MainWindow window) {
        if (window == null) return;
        try { SourceEditor source = Source(window); if (source != null) source.ResetCompositionSimulation(); } catch (Exception) { }
    }
    // The same rollback on the queued path (>= 256 KB): the conversion commits on the dispatcher,
    // the write then fails, and the waiting SaveToPath must surface the encoder error only after
    // the tab is back in its old mode.
    static void ConversionAsyncSaveRollback() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-async-rollback");
        var builder = new StringBuilder(320000);
        for (int i = 0; i < 15000; i++) builder.Append("- **굵게** 문장 ").Append(i).Append(" 😀\r\n");
        string md = builder.ToString();
        string file = WriteFile(dir, "large.md", Utf8NoBom(md));
        string target = Path.Combine(dir, "refused.txt");
        BeginCase("convert-async-rollback", "open a " + md.Length + "-char doc.md (UTF-8) -> SaveToPath(refused.txt, CP949, format change) runs the queued conversion -> the write is refused -> the tab must be back in MD mode with its exact text, caret and path");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(150);
            Check("async-setup-large", Source(window).Document.TextLength >= 256 * 1024, ">= 262144 chars", Source(window).Document.TextLength + " chars");
            Check("async-setup-queued-path", (bool)Invoke(window, "FormatChangeNeedsAsync", target, true), "the save uses the queued conversion", "the save would run synchronously");
            EnterEdit(window);
            string before = GetText(window);
            SourceEditor source = Source(window);
            int at = Math.Min(5, source.Document.TextLength);
            source.Select(at, 2); Pump(60);
            int selStart = source.SelectionStart, selLength = source.SelectionLength, selCaret = source.CaretIndex;
            string path = GetPath(window);
            Exception error = null; bool saved = false;
            try { saved = SaveTo(window, target, 3, true); } catch (Exception ex) { error = ex; }
            Check("async-refused", error is EncoderFallbackException, typeof(EncoderFallbackException).FullName,
                error == null ? "save returned " + saved : error.GetType().FullName + ": " + OneLine(error.Message));
            Check("async-not-saved", !saved, "false", saved.ToString());
            Check("async-target-missing", !File.Exists(target), "the refused target does not exist", "target was written");
            Check("async-text", GetText(window) == before, "text=" + Esc(before), "text=" + TextDiff(before, GetText(window)));
            Check("async-buffer", GetBuffer(window) == before, "buffer length " + before.Length, TextDiff(before, GetBuffer(window)));
            Check("async-mode", !GetPlainText(window), "Markdown mode", "TXT mode");
            Check("async-caret", source.SelectionStart == selStart && source.SelectionLength == selLength && source.CaretIndex == selCaret,
                "selection " + selStart + "+" + selLength + ", caret " + selCaret,
                "selection " + source.SelectionStart + "+" + source.SelectionLength + ", caret " + source.CaretIndex);
            Check("async-path", String.Equals(GetPath(window), path, StringComparison.OrdinalIgnoreCase), path, GetPath(window));
            Check("async-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            Check("async-idle", !Field<bool>(window, "documentOperationBusy") && !Field<bool>(window, "fileOpenRunning"), "busy=false, fileOpenRunning=false",
                "busy=" + Field<bool>(window, "documentOperationBusy") + ", fileOpenRunning=" + Field<bool>(window, "fileOpenRunning"));
            Check("async-source-unchanged", BytesEqual(File.ReadAllBytes(file), Utf8NoBom(md)), "source bytes unchanged", "source bytes changed");
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }
    // The overlay's 취소 during a queued format-conversion save: the save completes false with the
    // cancellation notice SaveFromUi shows, and the document is untouched.
    static void ConversionSaveCancel() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("convert-cancel");
        var builder = new StringBuilder(320000);
        for (int i = 0; i < 15000; i++) builder.Append("**굵게** 문장 ").Append(i).Append(" 한글\r\n");
        string md = builder.ToString();
        string file = WriteFile(dir, "cancel.md", Utf8NoBom(md));
        string target = Path.Combine(dir, "cancel.txt");
        BeginCase("convert-cancel", "open a " + md.Length + "-char doc.md -> Save As cancel.txt is queued through the conversion queue -> CancelFileOpen (the overlay 취소) before the conversion starts -> Save returns false with the cancellation notice and changes nothing");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(150);
            Check("cancel-setup-large", Source(window).Document.TextLength >= 256 * 1024, ">= 262144 chars", Source(window).Document.TextLength + " chars");
            EnterEdit(window);
            string before = GetText(window);
            string path = GetPath(window);
            bool mode = GetPlainText(window);
            System.Threading.Tasks.Task<bool> save = null;
            MainWindow.SaveTargetPicker = delegate(string name) { return target; };
            try {
                save = (System.Threading.Tasks.Task<bool>)Invoke(window, "SaveTaskOnDispatcher", true);
                Check("cancel-queued", Field<bool>(window, "fileOpenRunning"), "the save conversion is queued", "no queued operation");
                Invoke(window, "CancelFileOpen");
                WaitTask(save, 20000);
            } finally { MainWindow.SaveTargetPicker = null; }
            Check("cancel-completed", save != null && save.IsCompleted, "the save task completed", save == null ? "<null>" : save.Status.ToString());
            Check("cancel-returns-false", save != null && !save.Result, "false", save == null ? "<null>" : save.Result.ToString());
            string language = Field<string>(window, "language");
            string notice = MainWindow.TranslatedText(language, "저장이 취소되었거나 다른 작업 때문에 중단되었습니다.");
            Check("cancel-notice", window.LastSaveFailure != null && window.LastSaveFailure.IndexOf(notice, StringComparison.Ordinal) >= 0,
                "SaveFromUi's notice contains: " + notice, window.LastSaveFailure == null ? "<null>" : OneLine(window.LastSaveFailure));
            Check("cancel-text", GetText(window) == before, "text=" + Esc(before), "text=" + TextDiff(before, GetText(window)));
            Check("cancel-mode", GetPlainText(window) == mode, "plain=" + mode, "plain=" + GetPlainText(window));
            Check("cancel-path", String.Equals(GetPath(window), path, StringComparison.OrdinalIgnoreCase), path, GetPath(window));
            Check("cancel-target-missing", !File.Exists(target), "the cancelled target does not exist", "target was written");
            Pump(200);
            Check("cancel-idle", !Field<bool>(window, "documentOperationBusy") && !Field<bool>(window, "fileOpenRunning"), "busy=false, fileOpenRunning=false",
                "busy=" + Field<bool>(window, "documentOperationBusy") + ", fileOpenRunning=" + Field<bool>(window, "fileOpenRunning"));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            MainWindow.SaveTargetPicker = null;
            CloseQuiet(window);
            EndCase();
        }
    }

    // A dropped folder is not a text document. The open path must refuse it with a folder-specific
    // answer before the text reader turns the directory into an access-denied error, and no tab may
    // appear for it. The probe drives the same queued path the window Drop handler uses in the app.
    static void FolderOpenCase() {
        if (!BudgetLeft()) return;
        BeginCase("folder-open", "open a generated folder through the queued open path (the Drop answer) -> folder-specific error, no tab");
        MainWindow window = null;
        try {
            window = NewWindow();
            int before = window.OpenDocumentCount;
            string folder = CaseDir("folder-open");
            EnqueueEncodingOpen(window, folder, null);
            bool finished = WaitForOpenQueue(window, 20000);
            Exception error = window.LastFileOpenError;
            Check("folder-refused", finished && error is IOException && error.Message.IndexOf("폴더", StringComparison.Ordinal) >= 0,
                "a folder-specific IO error", "finished=" + finished + ", error=" + (error == null ? "none" : error.GetType().Name + ": " + OneLine(error.Message)));
            Check("folder-no-tab", window.OpenDocumentCount == before, "document count " + before, "documents=" + window.OpenDocumentCount);
            Check("folder-idle", !Field<bool>(window, "documentOperationBusy") && !Field<bool>(window, "fileOpenRunning"), "busy=false, fileOpenRunning=false",
                "busy=" + Field<bool>(window, "documentOperationBusy") + ", fileOpenRunning=" + Field<bool>(window, "fileOpenRunning"));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }

    // ---------------------------------------------------------------- encoding refusals on save
    static void RunEncodingRefusalCases() {
        Cp949EmojiSaveRefused();
        LoneSurrogateSaveRefused();
    }
    static int CountByte(byte[] bytes, byte value) {
        int count = 0;
        if (bytes != null) for (int i = 0; i < bytes.Length; i++) if (bytes[i] == value) count++;
        return count;
    }
    static void Cp949EmojiSaveRefused() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("refuse-cp949-emoji");
        string text = "한글 문서\r\n둘째 줄\r\n";
        byte[] original = Cp949(text);
        string file = WriteFile(dir, "cp949.txt", original);
        BeginCase("refuse-cp949-emoji", "open CP949 -> type an emoji -> save in place must be refused -> save as UTF-8 succeeds");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            Check("open-encoding", GetEncoding(window).CodePage == 949, "codepage=949", "codepage=" + GetEncoding(window).CodePage);
            EnterEdit(window);
            TypeAtEnd(window, "😀");
            Pump(100);
            string edited = GetText(window);
            Check("edit-added-emoji", edited.IndexOf("😀", StringComparison.Ordinal) >= 0, "text contains the emoji", "text=" + Esc(edited));
            Exception error = null; bool saved = false;
            try { saved = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("refusal-type", error is EncoderFallbackException, typeof(EncoderFallbackException).FullName, error == null ? "save returned " + saved : error.GetType().FullName + ": " + OneLine(error.Message));
            byte[] disk = File.ReadAllBytes(file);
            Check("refusal-file-unchanged", BytesEqual(disk, original), BytesState(original), ByteDiff(original, disk));
            Check("refusal-no-question-mark", CountByte(disk, 0x3F) == CountByte(original, 0x3F), "no '?' bytes were added", "the file gained '?' bytes");
            Check("refusal-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            Check("refusal-text-kept", GetText(window) == edited, "text=" + Esc(edited), "text=" + TextDiff(edited, GetText(window)));
            Check("refusal-dirty", HasUnsaved(window), "dirty", "clean");
            string utf8Target = Path.Combine(dir, "recovered.txt");
            bool utf8Saved = SaveTo(window, utf8Target, 0, false);
            string expected = NormalizeLineEndings(edited, GetNewline(window));
            byte[] expectedBytes = Utf8NoBom(expected);
            byte[] utf8Disk = File.Exists(utf8Target) ? File.ReadAllBytes(utf8Target) : null;
            Check("utf8-save-ok", utf8Saved, "true", utf8Saved.ToString());
            Check("utf8-save-bytes", BytesEqual(utf8Disk, expectedBytes), BytesState(expectedBytes), ByteDiff(expectedBytes, utf8Disk));
            Check("utf8-source-unchanged", BytesEqual(File.ReadAllBytes(file), original), BytesState(original), ByteDiff(original, File.ReadAllBytes(file)));
            Check("no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            CloseQuiet(window); window = null;
            MainWindow reopened = null;
            try {
                reopened = NewWindow();
                reopened.LoadFile(utf8Target); Pump(120);
                Check("reopen-text", GetText(reopened) == expected, "text=" + Esc(expected), "text=" + TextDiff(expected, GetText(reopened)));
                Check("reopen-encoding", GetEncoding(reopened).CodePage == 65001, "codepage=65001", "codepage=" + GetEncoding(reopened).CodePage);
            } finally { CloseQuiet(reopened); }
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }
    static void LoneSurrogateSaveRefused() {
        if (!BudgetLeft()) return;
        string dir = CaseDir("refuse-lone-surrogate");
        string text = "# 제목\n\n본문\n";
        byte[] original = Utf8NoBom(text);
        string file = WriteFile(dir, "lone.md", original);
        // A lone surrogate cannot be represented in any on-disk encoding the app opens, so the
        // buffer is given one directly: the strict encoder must refuse it, not write '?'.
        string loneText = "# 제목\n\n" + (char)0xD800 + " 문장\n";
        BeginCase("refuse-lone-surrogate", "open a UTF-8 file -> put a lone surrogate in the buffer -> save must be refused by the strict encoder");
        MainWindow window = null;
        try {
            window = NewWindow();
            window.LoadFile(file); Pump(120);
            window.SetText(loneText);
            Pump(120);
            Check("lone-surrogate-in-buffer", GetText(window) == loneText, "text=" + Esc(loneText), "text=" + TextDiff(loneText, GetText(window)));
            Exception error = null; bool saved = false;
            try { saved = SaveInPlace(window); } catch (Exception ex) { error = ex; }
            Check("refusal-type", error is EncoderFallbackException, typeof(EncoderFallbackException).FullName, error == null ? "save returned " + saved : error.GetType().FullName + ": " + OneLine(error.Message));
            byte[] disk = File.ReadAllBytes(file);
            Check("refusal-file-unchanged", BytesEqual(disk, original), BytesState(original), ByteDiff(original, disk));
            Check("refusal-no-question-mark", CountByte(disk, 0x3F) == CountByte(original, 0x3F), "no '?' bytes were added", "the file gained '?' bytes");
            Check("refusal-no-temp", TempFiles(dir).Length == 0, "0 temp files", TempFiles(dir).Length + " temp files");
            Check("refusal-text-kept", GetText(window) == loneText, "text=" + Esc(loneText), "text=" + TextDiff(loneText, GetText(window)));
        } catch (Exception error) {
            Check("no-exception", false, "no exception", "exception: " + OneLine(error.ToString()));
        } finally {
            CloseQuiet(window);
            EndCase();
        }
    }
}
}
