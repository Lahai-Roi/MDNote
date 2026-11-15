using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {
// --mermaid-live-probe <folder>: a ```mermaid block drawn as a diagram in the editor (MermaidRender.cs + the
// MarkdownLivePreview hooks), in the basic and the live view. Documents are generated here; no user file and
// no clipboard is used. Report <folder>\mermaid-live-probe.txt, -PASS.txt only when every check held.
static class MermaidLiveProbe {
    static bool all = true;
    static readonly StringBuilder report = new StringBuilder();
    static string folder;

    static void Check(string name, bool ok, string detail) {
        report.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (detail != null) report.Append(": ").Append(detail);
        report.Append('\n');
        if (!ok) all = false;
        Trace((ok ? "PASS " : "FAIL ") + name);
    }
    static void Note(string text) { report.Append("INFO ").Append(text).Append('\n'); Trace("INFO " + text); }
    // Line by line, so a run that hangs still shows the last step it reached.
    static void Trace(string text) {
        try { if (folder != null) File.AppendAllText(Path.Combine(folder, "mermaid-live-trace.txt"), DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text + "\n", new UTF8Encoding(false)); } catch (Exception) { }
    }
    static string N(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }

    sealed class Env : IDisposable {
        internal SourceEditor Editor;
        internal MarkdownLivePreview.Session Session;
        internal MarkdownLivePreview.Settings Settings;
        internal Window Window;
        public void Dispose() {
            try { if (Session != null) Session.Dispose(); } catch (Exception) { }
            try { if (Window != null) Window.Close(); } catch (Exception) { }
        }
    }
    static MarkdownLivePreview.Settings NewSettings(bool hideMarkers, bool dark) {
        return new MarkdownLivePreview.Settings {
            FontSize = 16, Family = "Malgun Gothic", LinePercent = 150, Dark = dark, WordWrap = true,
            BaseDirectory = folder, HideMarkers = hideMarkers
        };
    }
    static void Pump(Env env) {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
        env.Editor.UpdateLayout();
        env.Editor.TextArea.TextView.EnsureVisualLines();
    }
    // Pumps the dispatcher (engine results arrive at Background priority) until the condition holds or the deadline passes.
    static bool WaitFor(Env env, Func<bool> condition, int milliseconds) {
        Stopwatch watch = Stopwatch.StartNew();
        while (true) {
            Pump(env);
            if (condition()) return true;
            if (watch.ElapsedMilliseconds >= milliseconds) return false;
            Thread.Sleep(10);
        }
    }
    static void PumpFor(Env env, int milliseconds) {
        WaitFor(env, delegate { return false; }, milliseconds);
    }
    static Env Open(string text, bool hideMarkers, bool dark) {
        var env = new Env();
        env.Editor = new SourceEditor {
            FontFamily = new FontFamily("Malgun Gothic"), FontSize = 16, WordWrap = true,
            Background = dark ? new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24)) : Brushes.White,
            Foreground = dark ? Brushes.White : Brushes.Black
        };
        env.Editor.TextArea.TextView.LineHeight = double.NaN;
        env.Editor.Text = text;
        env.Settings = NewSettings(hideMarkers, dark);
        env.Session = MarkdownLivePreview.Attach(env.Editor, env.Settings);
        env.Window = new Window {
            Left = -20000, Top = 0, Width = 900, Height = 1000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, Content = env.Editor
        };
        env.Window.Show();
        Pump(env);
        return env;
    }
    static void Caret(Env env, int line, int column) {
        env.Editor.TextArea.Caret.Line = line;
        env.Editor.TextArea.Caret.Column = column;
        Pump(env);
    }
    static MermaidElement Diagram(Env env, int number) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        if (visual == null) return null;
        foreach (VisualLineElement element in visual.Elements) {
            MermaidElement diagram = element as MermaidElement;
            if (diagram != null) return diagram;
        }
        return null;
    }
    static bool Shown(Env env, int number) { return Diagram(env, number) != null; }
    static double LineHeight(Env env, int number) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        return visual == null ? 0 : visual.Height;
    }
    // The opener's visual line spans the folded lines under it up to `last`.
    static bool Folded(Env env, int opener, int last) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(opener);
        return visual != null && visual.FirstDocumentLine.LineNumber == opener && visual.LastDocumentLine.LineNumber == last;
    }
    // The line has a visual line of its own (it is not folded under an opener).
    static bool OwnLine(Env env, int number) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        return visual != null && visual.FirstDocumentLine.LineNumber == number;
    }
    static bool ChakraLoaded() {
        try {
            foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
                if (string.Equals(module.ModuleName, "chakra.dll", StringComparison.OrdinalIgnoreCase)) return true;
        } catch (Exception) { }
        return false;
    }
    static string EngineState() {
        return "unavailable=" + MermaidEngine.Unavailable + " startup=" + (MermaidEngine.StartupError ?? "-") + " last=" + (MermaidRender.LastError ?? "-") +
            " pending=" + MermaidRender.PendingCount + " requests=" + MermaidRender.DiagRequests + " renders=" + MermaidEngine.DiagRenders;
    }

    internal static int Run(string outDir) {
        folder = Path.GetFullPath(outDir);
        Directory.CreateDirectory(folder);
        string pass = Path.Combine(folder, "mermaid-live-probe-PASS.txt"), fail = Path.Combine(folder, "mermaid-live-probe-FAIL.txt");
        try { File.Delete(pass); File.Delete(fail); File.Delete(Path.Combine(folder, "mermaid-live-trace.txt")); } catch (IOException) { }
        Trace("start");
        report.Append("# MermaidLiveProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        int exit = 0;
        Stopwatch total = Stopwatch.StartNew();
        try {
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // (a) runs first: the engine counters and the module list are process-wide.
            NoMermaidCase();
            bool engine = BlockCases(true, "x");
            if (engine) BlockCases(false, "y");
            else Check("b live: skipped, the engine gave no picture in the basic view", false, EngineState());
            BrokenCases();
            TypingCase();
            if (engine) {
                OpenerEditCase();
                NewOpenerCase();
                LeaveBrokenCase();
                FenceAboveCase();
                PressCase();
            } else Check("h-l: skipped, the engine gave no picture", false, EngineState());
        } catch (Exception ex) {
            exit = 1;
            all = false;
            report.Append("FAIL probe exception: ").Append(ex).Append('\n');
        }
        Note("probe time " + total.ElapsedMilliseconds + " ms");
        Check("z whole probe under 90 s", total.ElapsedMilliseconds < 90000, total.ElapsedMilliseconds + " ms");
        // The abuse checks of the same feature (--mermaid-abuse-probe); they write their own report and fail this run too.
        if (MainWindow.VerifyMermaidAbuse(folder) != 0) all = false;
        report.Append("\n").Append(all ? "RESULT PASS" : "RESULT FAIL").Append('\n');
        File.WriteAllText(Path.Combine(folder, "mermaid-live-probe.txt"), report.ToString(), new UTF8Encoding(false));
        File.WriteAllText(all && exit == 0 ? pass : fail, report.ToString(), new UTF8Encoding(false));
        return all && exit == 0 ? 0 : 1;
    }

    // (a) 2000 lines, code fences and the word "mermaid" in text but no mermaid block.
    static void NoMermaidCase() {
        var text = new StringBuilder();
        for (int n = 1; n <= 2000; n++) {
            int kind = n % 100;
            if (kind == 10) text.Append("```python\n");
            else if (kind == 11) text.Append("print('mermaid is only a word here')\n");
            else if (kind == 12) text.Append("```\n");
            else if (kind == 50) text.Append("```mermaidx\n");
            else if (kind == 51) text.Append("flowchart TD\n");
            else if (kind == 52) text.Append("```\n");
            else text.Append("Paragraph ").Append(n).Append(" mentions mermaid and `mermaid` without a block.\n");
        }
        using (Env env = Open(text.ToString(), true, false)) {
            Caret(env, 5, 1);
            env.Editor.TextArea.Caret.Offset = env.Editor.Document.GetLineByNumber(5).EndOffset;
            Pump(env);
            for (int i = 0; i < 50; i++) {
                env.Editor.TextArea.PerformTextInput("q");
                if (i % 10 == 9) Pump(env);
            }
            env.Editor.ScrollToVerticalOffset(env.Editor.ExtentHeight / 2);
            Pump(env);
            env.Editor.ScrollToVerticalOffset(env.Editor.ExtentHeight);
            Pump(env);
            PumpFor(env, 400);
            bool chakra = ChakraLoaded();
            Check("a no mermaid block: no record, no request, chakra.dll not loaded",
                !chakra && env.Session.MermaidBlockCount == 0 && MermaidRender.DiagRequests == 0 && MermaidRender.DiagConverts == 0,
                "chakra=" + chakra + " records=" + env.Session.MermaidBlockCount + " requests=" + MermaidRender.DiagRequests + " converts=" + MermaidRender.DiagConverts);
            Check("a no mermaid block: the engine never started", MermaidEngine.DiagEngineStarts == 0 && MermaidEngine.DiagRenders == 0,
                "starts=" + MermaidEngine.DiagEngineStarts + " renders=" + MermaidEngine.DiagRenders);
        }
    }

    // Lines: 1 text, 3 opener, 4-7 source, 8 closer, 10 text.
    const string BlockDocument =
        "제목 문단\n\n```mermaid\nflowchart TD\n  A[시작] --> B{조건}\n  B -->|예| C[처리]\n  B -->|아니오| D[종료]\n```\n\n끝 문단\n";

    // (b) (c) (d) (f) in one view. Returns false when the engine produced no picture at all.
    static bool BlockCases(bool hide, string typed) {
        string mode = hide ? "basic" : "live";
        using (Env env = Open(BlockDocument, hide, false)) {
            Caret(env, 10, 1);
            long firstRenders = MermaidEngine.DiagRenders;
            bool shown = WaitFor(env, delegate { return Shown(env, 3); }, 5000);
            MermaidElement diagram = Diagram(env, 3);
            Check("b " + mode + ": the block's lines are one picture within 5 s", shown && Folded(env, 3, 8),
                "shown=" + shown + " folded=" + Folded(env, 3, 8) + " " + EngineState());
            if (!shown || diagram == null) return false;
            Check("b " + mode + ": the picture has a height and the line holds it",
                diagram.Picture.Height > 0 && diagram.Picture.Width > 0 && LineHeight(env, 3) >= diagram.Picture.Height * diagram.Scale - 1,
                "picture=" + N(diagram.Picture.Width) + "x" + N(diagram.Picture.Height) + " scale=" + N(diagram.Scale) + " line=" + N(LineHeight(env, 3)));
            Check("b " + mode + ": never scaled up, not wider than the view", diagram.Scale <= 1 && diagram.Picture.Width * diagram.Scale <= env.Editor.TextArea.TextView.ActualWidth + 1,
                "scale=" + N(diagram.Scale) + " view=" + N(env.Editor.TextArea.TextView.ActualWidth));
            Note(mode + " first picture: engine renders +" + (MermaidEngine.DiagRenders - firstRenders));

            // (c) caret into the block and out again.
            long renders = MermaidEngine.DiagRenders, requests = MermaidRender.DiagRequests;
            Caret(env, 5, 3);
            Check("c " + mode + ": caret inside shows the source", !Shown(env, 3) && OwnLine(env, 4) && OwnLine(env, 5) && OwnLine(env, 7),
                "picture=" + Shown(env, 3) + " own4=" + OwnLine(env, 4) + " own5=" + OwnLine(env, 5) + " caretLine=" + env.Editor.TextArea.Caret.Line);
            Caret(env, 10, 1);
            Check("c " + mode + ": caret outside brings the picture back from the cache", Shown(env, 3) && Folded(env, 3, 8) && MermaidEngine.DiagRenders == renders && MermaidRender.DiagRequests == requests,
                "picture=" + Shown(env, 3) + " renders +" + (MermaidEngine.DiagRenders - renders) + " requests +" + (MermaidRender.DiagRequests - requests));

            // (d) ten quick edits inside a label, then out: one request, one render.
            Caret(env, 5, 7);
            renders = MermaidEngine.DiagRenders; requests = MermaidRender.DiagRequests;
            for (int i = 0; i < 10; i++) env.Editor.TextArea.PerformTextInput(typed);
            Check("d " + mode + ": typing sends no request", MermaidRender.DiagRequests == requests && MermaidEngine.DiagRenders == renders,
                "requests +" + (MermaidRender.DiagRequests - requests));
            string edited = env.Editor.Document.GetText(env.Editor.Document.GetLineByNumber(5));
            Pump(env);
            Caret(env, 10, 1);
            Check("d " + mode + ": leaving the edited block keeps a picture while the new one renders", Shown(env, 3) && Folded(env, 3, 8),
                "picture=" + Shown(env, 3) + " line5=" + edited);
            bool rendered = WaitFor(env, delegate {
                MermaidElement now = Diagram(env, 3);
                return MermaidRender.PendingCount == 0 && MermaidRender.DiagRequests > requests && now != null && now.Picture.Key != null && now.Picture.Key.IndexOf(edited, StringComparison.Ordinal) >= 0;
            }, 5000);
            PumpFor(env, 500);
            Check("d " + mode + ": the edited source is rendered once after the pause", rendered && MermaidRender.DiagRequests == requests + 1 && MermaidEngine.DiagRenders == renders + 1,
                "newPicture=" + rendered + " requests +" + (MermaidRender.DiagRequests - requests) + " renders +" + (MermaidEngine.DiagRenders - renders) + " " + EngineState());

            // (f) light -> dark: converted again from the cached SVG.
            renders = MermaidEngine.DiagRenders;
            long converts = MermaidRender.DiagConverts;
            env.Editor.Foreground = Brushes.White;
            env.Editor.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24));
            env.Settings = NewSettings(hide, true);
            env.Session.Update(env.Settings);
            Pump(env);
            MermaidElement dark = Diagram(env, 3);
            Check("f " + mode + ": dark theme rebuilds the picture without the engine",
                dark != null && dark.Picture.Dark && MermaidRender.DiagConverts == converts + 1 && MermaidEngine.DiagRenders == renders,
                "picture=" + (dark != null) + " dark=" + (dark != null && dark.Picture.Dark) + " converts +" + (MermaidRender.DiagConverts - converts) + " renders +" + (MermaidEngine.DiagRenders - renders));
        }
        return true;
    }

    // (e) a source the engine rejects, an unclosed fence and an empty block stay code blocks.
    static void BrokenCases() {
        foreach (bool hide in new[] { true, false }) {
            string mode = hide ? "basic" : "live";
            string text = "앞 문단\n\n```mermaid\nnonsense_diagram_" + mode + "\n  ??? !!! (((\n```\n\n뒤 문단\n";
            long requests = MermaidRender.DiagRequests;
            try {
                using (Env env = Open(text, hide, false)) {
                    Caret(env, 8, 1);
                    bool asked = WaitFor(env, delegate { return MermaidRender.DiagRequests > requests; }, 2000);
                    bool settled = WaitFor(env, delegate { return MermaidRender.PendingCount == 0; }, 5000);
                    PumpFor(env, 200);
                    Check("e " + mode + ": a broken source stays a code block", asked && settled && !Shown(env, 3) && OwnLine(env, 4) && OwnLine(env, 6),
                        "asked=" + asked + " settled=" + settled + " picture=" + Shown(env, 3) + " error=" + (MermaidRender.LastError ?? "-"));
                    long again = MermaidRender.DiagRequests;
                    Caret(env, 4, 1);
                    Caret(env, 8, 1);
                    PumpFor(env, 450);
                    Check("e " + mode + ": the failure is remembered (no second request)", MermaidRender.DiagRequests == again && !Shown(env, 3),
                        "requests +" + (MermaidRender.DiagRequests - again));
                }
            } catch (Exception ex) {
                Check("e " + mode + ": no exception", false, ex.ToString());
            }
        }
        long before = MermaidRender.DiagRequests;
        using (Env env = Open("문단\n\n```mermaid\nflowchart TD\n  A --> B\n\n닫는 줄 없음\n", true, false)) {
            Caret(env, 1, 1);
            PumpFor(env, 500);
            Check("e unclosed fence: plain code block, no request", MermaidRender.DiagRequests == before && !Shown(env, 3) && OwnLine(env, 4),
                "requests +" + (MermaidRender.DiagRequests - before));
        }
        using (Env env = Open("문단\n\n```mermaid\n```\n\n끝\n", true, false)) {
            Caret(env, 1, 1);
            PumpFor(env, 500);
            Check("e empty block: plain code block, no request", MermaidRender.DiagRequests == before && !Shown(env, 3) && OwnLine(env, 4),
                "requests +" + (MermaidRender.DiagRequests - before));
        }
    }

    // (g) 200 characters typed on a line elsewhere in a document with three blocks.
    static void TypingCase() {
        string text =
            "첫 문단\n\n```mermaid\nflowchart LR\n  A1[하나] --> B1[둘]\n```\n\n문단 2\n\n" +
            "```mermaid\nflowchart LR\n  A2[셋] --> B2[넷]\n```\n\n문단 3\n\n" +
            "```mermaid\nflowchart TD\n  A3[다섯] --> B3[여섯]\n```\n\n입력 줄\n";
        foreach (bool hide in new[] { true, false }) {
            string mode = hide ? "basic" : "live";
            using (Env env = Open(text, hide, false)) {
                env.Editor.TextArea.Caret.Offset = env.Editor.Document.GetLineByNumber(22).EndOffset;
                Pump(env);
                bool shown = WaitFor(env, delegate { return Shown(env, 3) && Shown(env, 10) && Shown(env, 17) && MermaidRender.PendingCount == 0; }, 8000);
                Check("g " + mode + ": three blocks drawn", shown, "b1=" + Shown(env, 3) + " b2=" + Shown(env, 10) + " b3=" + Shown(env, 17) + " " + EngineState());
                PumpFor(env, 400);
                long renders = MermaidEngine.DiagRenders, requests = MermaidRender.DiagRequests, converts = MermaidRender.DiagConverts, scans = MarkdownLivePreview.DiagBlockScans;
                Stopwatch watch = Stopwatch.StartNew();
                for (int i = 0; i < 200; i++) {
                    env.Editor.TextArea.PerformTextInput("q");
                    env.Editor.UpdateLayout();
                }
                watch.Stop();
                long scanned = MarkdownLivePreview.DiagBlockScans - scans;
                PumpFor(env, 600);
                Check("g " + mode + ": 200 characters typed elsewhere: no request, no render, no conversion",
                    MermaidEngine.DiagRenders == renders && MermaidRender.DiagRequests == requests && MermaidRender.DiagConverts == converts,
                    "renders +" + (MermaidEngine.DiagRenders - renders) + " requests +" + (MermaidRender.DiagRequests - requests) + " converts +" + (MermaidRender.DiagConverts - converts));
                Check("g " + mode + ": typing scans a bounded number of block lines", scanned <= 200 * 6, "block scans +" + scanned + " for 200 characters");
                Check("g " + mode + ": the three pictures stay", Shown(env, 3) && Shown(env, 10) && Shown(env, 17), "b1=" + Shown(env, 3) + " b2=" + Shown(env, 10) + " b3=" + Shown(env, 17));
                Note("g " + mode + ": " + N(watch.Elapsed.TotalMilliseconds / 200.0) + " ms per keystroke (typing + layout, 200 characters)");
            }
        }
    }

    // Document edits made through the document (the caret stays outside the block: in the basic view it
    // cannot rest on a hidden fence row), each followed by a layout pass and no dispatcher pump.
    static void InsertTyped(Env env, int offset, string text) {
        for (int i = 0; i < text.Length; i++) {
            env.Editor.Document.Insert(offset + i, text.Substring(i, 1));
            env.Editor.UpdateLayout();
            env.Editor.TextArea.TextView.EnsureVisualLines();
        }
    }

    // (h) the opener's info word edited and taken back: the picture returns without a render, whether
    // the edit is taken back before the idle timer (the record is kept) or after it (a new record).
    static void OpenerEditCase() {
        foreach (bool hide in new[] { true, false }) {
            string mode = hide ? "basic" : "live";
            using (Env env = Open(BlockDocument, hide, false)) {
                Caret(env, 10, 1);
                bool shown = WaitFor(env, delegate { return Shown(env, 3) && MermaidRender.PendingCount == 0; }, 5000);
                if (!shown) { Check("h " + mode + ": the block is drawn first", false, EngineState()); continue; }
                PumpFor(env, 400);
                TextDocument document = env.Editor.Document;
                long renders = MermaidEngine.DiagRenders, requests = MermaidRender.DiagRequests;
                int end = document.GetLineByNumber(3).EndOffset;

                document.Insert(end, "x");
                env.Editor.UpdateLayout();
                env.Editor.TextArea.TextView.EnsureVisualLines();
                Check("h " + mode + ": \"mermaidx\" is a code block and the record is kept", !Shown(env, 3) && OwnLine(env, 4) && env.Session.MermaidBlockCount == 1 && env.Session.MermaidFoldCount == 0,
                    "picture=" + Shown(env, 3) + " own4=" + OwnLine(env, 4) + " records=" + env.Session.MermaidBlockCount + " folds=" + env.Session.MermaidFoldCount);
                document.Remove(end, 1);
                bool back = WaitFor(env, delegate { return Shown(env, 3) && Folded(env, 3, 8); }, 3000);
                Check("h " + mode + ": typed back at once: the picture returns without a render", back && MermaidEngine.DiagRenders == renders && MermaidRender.DiagRequests == requests,
                    "picture=" + back + " renders +" + (MermaidEngine.DiagRenders - renders) + " requests +" + (MermaidRender.DiagRequests - requests));

                document.Insert(end, "x");
                env.Editor.UpdateLayout();
                // SourceEditor turns the document's own undo stack off (SizeLimit = 0; the app's history
                // lives in MainWindow), so an undo is made here the way that history applies one: the
                // whole opener line put back in one replace.
                DocumentLine opener = document.GetLineByNumber(3);
                document.Replace(opener.Offset, opener.Length, "```mermaid");
                string restored = document.GetText(document.GetLineByNumber(3));
                back = WaitFor(env, delegate { return Shown(env, 3) && Folded(env, 3, 8); }, 3000);
                Check("h " + mode + ": undone at once: the picture returns without a render", restored == "```mermaid" && back && MermaidEngine.DiagRenders == renders && MermaidRender.DiagRequests == requests,
                    "picture=" + back + " renders +" + (MermaidEngine.DiagRenders - renders) + " requests +" + (MermaidRender.DiagRequests - requests) +
                    " line3=" + restored + " caret=" + env.Editor.TextArea.Caret.Line + " records=" + env.Session.MermaidBlockCount + " folds=" + env.Session.MermaidFoldCount);

                end = document.GetLineByNumber(3).EndOffset;
                document.Insert(end, "x");
                PumpFor(env, 600);
                Check("h " + mode + ": after the pause the record of \"mermaidx\" is gone", env.Session.MermaidBlockCount == 0 && !Shown(env, 3) && OwnLine(env, 4),
                    "records=" + env.Session.MermaidBlockCount + " picture=" + Shown(env, 3));
                document.Remove(end, 1);
                back = WaitFor(env, delegate { return Shown(env, 3) && Folded(env, 3, 8); }, 3000);
                Check("h " + mode + ": typed back after the pause: the picture returns from the cache", back && MermaidEngine.DiagRenders == renders && MermaidRender.DiagRequests == requests,
                    "picture=" + back + " renders +" + (MermaidEngine.DiagRenders - renders) + " requests +" + (MermaidRender.DiagRequests - requests) +
                    " line3=" + document.GetText(document.GetLineByNumber(3)) + " caret=" + env.Editor.TextArea.Caret.Line + " records=" + env.Session.MermaidBlockCount + " folds=" + env.Session.MermaidFoldCount + " pending=" + MermaidRender.PendingCount);
            }
        }
    }

    // (i) a new opener line typed: no block is scanned while typing (not on the layout pass either),
    // in a document without a block and in one that has one. The picture comes after the pause.
    static void NewOpenerCase() {
        // Lines: 3 empty (the opener to be), 4-5 source, 6 closer, 8 text.
        using (Env env = Open("문단\n\n\nflowchart TD\n  A[새 블록] --> B[둘]\n```\n\n끝\n", true, false)) {
            Caret(env, 1, 1);
            PumpFor(env, 400);
            long scans = MermaidRender.DiagScans, layoutScans = MermaidRender.DiagLayoutScans, requests = MermaidRender.DiagRequests;
            InsertTyped(env, env.Editor.Document.GetLineByNumber(3).Offset, "```mermaid");
            Check("i no block yet: typing the opener scans nothing and asks nothing",
                MermaidRender.DiagScans == scans && MermaidRender.DiagLayoutScans == layoutScans && MermaidRender.DiagRequests == requests && env.Session.MermaidBlockCount == 1,
                "scans +" + (MermaidRender.DiagScans - scans) + " layout scans +" + (MermaidRender.DiagLayoutScans - layoutScans) + " requests +" + (MermaidRender.DiagRequests - requests) + " records=" + env.Session.MermaidBlockCount);
            bool shown = WaitFor(env, delegate { return Shown(env, 3) && Folded(env, 3, 6); }, 5000);
            Check("i no block yet: the picture comes after the pause", shown && MermaidRender.DiagScans > scans && MermaidRender.DiagLayoutScans == layoutScans,
                "picture=" + shown + " scans +" + (MermaidRender.DiagScans - scans) + " layout scans +" + (MermaidRender.DiagLayoutScans - layoutScans) + " " + EngineState());
        }
        // Lines: 3-6 a block, 8 empty (the opener to be), 9-10 source, 11 closer, 13 text.
        string text = "제목\n\n```mermaid\nflowchart TD\n  A[시작] --> B{조건}\n```\n\n\nflowchart LR\n  X[셋째] --> Y[넷째]\n```\n\n마지막\n";
        using (Env env = Open(text, true, false)) {
            Caret(env, 1, 1);
            bool first = WaitFor(env, delegate { return Shown(env, 3) && MermaidRender.PendingCount == 0; }, 5000);
            PumpFor(env, 400);
            long scans = MermaidRender.DiagScans, layoutScans = MermaidRender.DiagLayoutScans, requests = MermaidRender.DiagRequests;
            InsertTyped(env, env.Editor.Document.GetLineByNumber(8).Offset, "```mermaid");
            Check("i with a block: typing a second opener scans nothing and asks nothing",
                first && MermaidRender.DiagScans == scans && MermaidRender.DiagLayoutScans == layoutScans && MermaidRender.DiagRequests == requests && env.Session.MermaidBlockCount == 2,
                "first=" + first + " scans +" + (MermaidRender.DiagScans - scans) + " layout scans +" + (MermaidRender.DiagLayoutScans - layoutScans) + " requests +" + (MermaidRender.DiagRequests - requests) + " records=" + env.Session.MermaidBlockCount);
            Check("i with a block: the block above keeps its picture while the opener is typed", Shown(env, 3) && Folded(env, 3, 6), "picture=" + Shown(env, 3));
            bool shown = WaitFor(env, delegate { return Shown(env, 3) && Shown(env, 8) && Folded(env, 8, 11); }, 5000);
            Check("i with a block: both pictures after the pause", shown, "b1=" + Shown(env, 3) + " b2=" + Shown(env, 8) + " " + EngineState());
        }
    }

    // (j) leaving an edited block asks for the new source at once; when that source fails, the block
    // drops from the kept picture to its code view as the failure arrives.
    static void LeaveBrokenCase() {
        using (Env env = Open(BlockDocument, true, false)) {
            Caret(env, 10, 1);
            bool shown = WaitFor(env, delegate { return Shown(env, 3) && MermaidRender.PendingCount == 0; }, 5000);
            if (!shown) { Check("j: the block is drawn first", false, EngineState()); return; }
            PumpFor(env, 400);
            Caret(env, 5, 3);
            DocumentLine header = env.Editor.Document.GetLineByNumber(4);
            env.Editor.Document.Replace(header.Offset, header.Length, "nonsense_diagram_leave");
            env.Editor.UpdateLayout();
            long requests = MermaidRender.DiagRequests;
            Stopwatch watch = Stopwatch.StartNew();
            Caret(env, 10, 1);
            watch.Stop();
            Check("j: leaving the edited block sends the request at once", MermaidRender.DiagRequests == requests + 1,
                "requests +" + (MermaidRender.DiagRequests - requests) + " after " + watch.ElapsedMilliseconds + " ms (the idle timer is " + MermaidRender.DebounceMilliseconds + " ms)");
            bool dropped = WaitFor(env, delegate { return MermaidRender.PendingCount == 0 && !Shown(env, 3) && OwnLine(env, 4) && OwnLine(env, 7); }, 5000);
            PumpFor(env, 400);
            Check("j: the failed source drops the block to its code view", dropped && !Shown(env, 3) && OwnLine(env, 4) && MermaidRender.DiagRequests == requests + 1,
                "dropped=" + dropped + " picture=" + Shown(env, 3) + " requests +" + (MermaidRender.DiagRequests - requests) + " error=" + (MermaidRender.LastError ?? "-"));
        }
    }

    // (k) a fence typed far above a drawn block that is scrolled out of view: the block's fold is given
    // back with the edit (nothing is laid out there), and the idle pass drops the record.
    static void FenceAboveCase() {
        var text = new StringBuilder("첫 줄\n\n");
        for (int n = 1; n <= 80; n++) text.Append("문단 ").Append(n).Append('\n');
        // Lines: 84 opener, 85-86 source, 87 closer, 89 text.
        text.Append("\n```mermaid\nflowchart TD\n  A[위] --> B[아래]\n```\n\n끝\n");
        using (Env env = Open(text.ToString(), true, false)) {
            Caret(env, 1, 1);
            env.Editor.ScrollToVerticalOffset(env.Editor.ExtentHeight);
            bool shown = WaitFor(env, delegate { return Shown(env, 84) && MermaidRender.PendingCount == 0; }, 5000);
            PumpFor(env, 400);
            env.Editor.ScrollToVerticalOffset(0);
            Pump(env);
            bool offScreen = env.Editor.TextArea.TextView.GetVisualLine(84) == null;
            Check("k: the block is drawn, then scrolled out of view", shown && offScreen && env.Session.MermaidFoldCount == 1,
                "shown=" + shown + " offScreen=" + offScreen + " folds=" + env.Session.MermaidFoldCount + " " + EngineState());
            long scans = MermaidRender.DiagScans, renders = MermaidEngine.DiagRenders;
            int at = env.Editor.Document.GetLineByNumber(2).Offset;
            InsertTyped(env, at, "```");
            Check("k: a fence typed above unfolds the off-screen block with the edit, without a scan",
                env.Session.MermaidFoldCount == 0 && MermaidRender.DiagScans == scans && env.Session.MermaidBlockCount == 1,
                "folds=" + env.Session.MermaidFoldCount + " scans +" + (MermaidRender.DiagScans - scans) + " records=" + env.Session.MermaidBlockCount);
            PumpFor(env, 600);
            Check("k: after the pause the block inside the new code block has no record", env.Session.MermaidBlockCount == 0 && env.Session.MermaidFoldCount == 0,
                "records=" + env.Session.MermaidBlockCount + " folds=" + env.Session.MermaidFoldCount);
            env.Editor.ScrollToVerticalOffset(env.Editor.ExtentHeight);
            Pump(env);
            Check("k: scrolled back, the block is code", !Shown(env, 84) && OwnLine(env, 85) && OwnLine(env, 86), "picture=" + Shown(env, 84) + " own85=" + OwnLine(env, 85));
            env.Editor.ScrollToVerticalOffset(0);
            Pump(env);
            env.Editor.Document.Remove(at, 3);
            PumpFor(env, 500);
            env.Editor.ScrollToVerticalOffset(env.Editor.ExtentHeight);
            bool back = WaitFor(env, delegate { return Shown(env, 84) && Folded(env, 84, 87); }, 3000);
            Check("k: the fence removed, the picture returns without a render", back && MermaidEngine.DiagRenders == renders,
                "picture=" + back + " renders +" + (MermaidEngine.DiagRenders - renders));
        }
    }

    // (l) the press rule through MermaidPressTarget (the handler's own decision; the mouse event that
    // carries the point is not simulated here). -1 = the press is left to the editor.
    static double SlotLeft(Env env, MermaidElement diagram, int number) {
        VisualLine row = env.Editor.TextArea.TextView.GetVisualLine(number);
        TextLine line = row.GetTextLine(diagram.VisualColumn);
        return row.GetTextLineVisualXPosition(line, diagram.VisualColumn);
    }
    static void PressCase() {
        using (Env env = Open(BlockDocument, true, false)) {
            Caret(env, 10, 1);
            bool shown = WaitFor(env, delegate { return Shown(env, 3) && MermaidRender.PendingCount == 0; }, 5000);
            MermaidElement diagram = Diagram(env, 3);
            if (!shown || diagram == null) Check("l: the block is drawn first", false, EngineState());
            else {
                TextView view = env.Editor.TextArea.TextView;
                double y = view.GetVisualTopByDocumentLine(3) + LineHeight(env, 3) / 2;
                double left = SlotLeft(env, diagram, 3), width = diagram.Picture.Width * diagram.Scale, margin = (diagram.Slot - width) / 2;
                int inner = env.Editor.Document.GetLineByNumber(4).Offset;
                int centre = env.Session.MermaidPressTarget(new Point(left + diagram.Slot / 2, y));
                Check("l: a press on the picture goes to the first source line", centre == inner, "target=" + centre + " want=" + inner);
                if (margin < 12) Note("l: the picture fills the row (margin " + N(margin) + "), the side press is not checked");
                else {
                    int leftSide = env.Session.MermaidPressTarget(new Point(left + 4, y));
                    int rightSide = env.Session.MermaidPressTarget(new Point(left + diagram.Slot - 4, y));
                    Check("l: a press beside the picture of a block inside the document goes to the first source line too",
                        leftSide == inner && rightSide == inner, "left=" + leftSide + " right=" + rightSide + " want=" + inner + " margin=" + N(margin));
                }
                int text = env.Session.MermaidPressTarget(new Point(left + 4, view.GetVisualTopByDocumentLine(10) + 4));
                Check("l: a press on a text line is left to the editor", text == -1, "target=" + text);
            }
        }
        // The same block closing the document (no line after the closer).
        string ending = BlockDocument.Substring(0, BlockDocument.IndexOf("```\n\n끝", StringComparison.Ordinal) + 3);
        using (Env env = Open(ending, true, false)) {
            Caret(env, 1, 1);
            bool shown = WaitFor(env, delegate { return Shown(env, 3) && MermaidRender.PendingCount == 0; }, 5000);
            MermaidElement diagram = Diagram(env, 3);
            if (!shown || diagram == null) { Check("l end: the block is drawn first", false, EngineState() + " lines=" + env.Editor.Document.LineCount); return; }
            TextView view = env.Editor.TextArea.TextView;
            double y = view.GetVisualTopByDocumentLine(3) + LineHeight(env, 3) / 2;
            double left = SlotLeft(env, diagram, 3), width = diagram.Picture.Width * diagram.Scale, margin = (diagram.Slot - width) / 2;
            int length = env.Editor.Document.TextLength;
            int centre = env.Session.MermaidPressTarget(new Point(left + diagram.Slot / 2, y));
            Check("l end: a press on the picture goes to the first source line", centre == env.Editor.Document.GetLineByNumber(4).Offset, "target=" + centre);
            if (margin >= 12) {
                int side = env.Session.MermaidPressTarget(new Point(left + 4, y));
                Check("l end: a press beside the picture of the block that ends the document is the document's end", side == length, "target=" + side + " want=" + length);
            }
            int below = env.Session.MermaidPressTarget(new Point(left + 4, view.DocumentHeight + 6));
            Check("l end: a press below the block that ends the document is the document's end", below == length, "target=" + below + " want=" + length);
        }
        Note("l: the mouse event itself (PreviewMouseLeftButtonDown reaching the handler, Handled stopping the editor) needs a real-input test");
    }
}
}
