using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {
// --math-probe <folder>: LaTeX math display (MathRender.cs + the MarkdownLivePreview hooks). Documents are
// generated here; no user file is read. Report <folder>\math-probe.txt, math-probe-PASS.txt only when every
// check held (math-probe-FAIL.txt otherwise), PNGs of the light, dark, source and no-reveal views.
static class MathProbe {
    static bool all = true;
    static readonly StringBuilder report = new StringBuilder();
    static Application app;
    static string folder;

    static void Check(string name, bool ok, string detail) {
        report.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (detail != null) report.Append(": ").Append(detail);
        report.Append('\n');
        if (!ok) all = false;
    }
    static void Pump(MathEnv env) {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
        env.Editor.UpdateLayout();
        env.Editor.TextArea.TextView.EnsureVisualLines();
    }
    static string N(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }

    internal sealed class MathEnv : IDisposable {
        internal SourceEditor Editor;
        internal MarkdownLivePreview.Session Session;
        internal MarkdownLivePreview.Settings Settings;
        internal Window Window;
        public void Dispose() {
            try { if (Session != null) Session.Dispose(); } catch (Exception) { }
            try { if (Window != null) Window.Close(); } catch (Exception) { }
        }
    }
    static MathEnv Open(string text, bool hideMarkers, bool dark) {
        var env = new MathEnv();
        env.Editor = new SourceEditor {
            FontFamily = new FontFamily("Malgun Gothic"), FontSize = 16, WordWrap = true,
            Background = dark ? new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24)) : Brushes.White,
            Foreground = dark ? Brushes.White : Brushes.Black
        };
        env.Editor.TextArea.TextView.LineHeight = double.NaN;
        env.Editor.Text = text;
        env.Settings = new MarkdownLivePreview.Settings {
            FontSize = 16, Family = "Malgun Gothic", LinePercent = 150, Dark = dark, WordWrap = true,
            BaseDirectory = folder, HideMarkers = hideMarkers
        };
        env.Session = MarkdownLivePreview.Attach(env.Editor, env.Settings);
        env.Window = new Window {
            Left = -20000, Top = 0, Width = 900, Height = 1000, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, Content = env.Editor
        };
        env.Window.Show();
        Pump(env);
        return env;
    }
    static void Caret(MathEnv env, int line, int column) {
        env.Editor.TextArea.Caret.Line = line;
        env.Editor.TextArea.Caret.Column = column;
        Pump(env);
    }
    static MarkdownLivePreview.LineInfo Info(MathEnv env, int number) {
        return env.Session.Info(env.Editor.Document.GetLineByNumber(number));
    }
    static int MathElements(MathEnv env, int number) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        if (visual == null) return -1;
        int count = 0;
        foreach (VisualLineElement element in visual.Elements) if (element is MathElement) count++;
        return count;
    }
    static double LineHeight(MathEnv env, int number) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        return visual == null ? 0 : visual.Height;
    }
    // The opener's visual line spans the folded lines under it (up to line `last`), and the lines below it have none of their own.
    static bool Folded(MathEnv env, int opener, int last) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(opener);
        return visual != null && visual.LastDocumentLine.LineNumber == last;
    }
    static void Shot(MathEnv env, string name) {
        Pump(env);
        int width = (int)env.Editor.ActualWidth, height = (int)env.Editor.ActualHeight;
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext context = background.RenderOpen()) context.DrawRectangle(env.Editor.Background, null, new Rect(0, 0, width, height));
        bitmap.Render(background);
        bitmap.Render(env.Editor);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(Path.Combine(folder, name))) encoder.Save(stream);
    }
    // The darkest / brightest pixel luminance inside the rectangle of one visual line of a shot.
    static void Luminance(MathEnv env, int number, out double darkest, out double brightest) {
        darkest = 255; brightest = 0;
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        if (visual == null) return;
        int width = (int)env.Editor.ActualWidth;
        double top = visual.VisualTop - env.Editor.TextArea.TextView.VerticalOffset;
        int y = Math.Max(0, (int)top), h = Math.Max(1, Math.Min((int)env.Editor.ActualHeight - y, (int)Math.Ceiling(visual.Height)));
        var bitmap = new RenderTargetBitmap(width, (int)env.Editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext context = background.RenderOpen()) context.DrawRectangle(env.Editor.Background, null, new Rect(0, 0, width, env.Editor.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(env.Editor);
        var pixels = new byte[width * h * 4];
        bitmap.CopyPixels(new Int32Rect(0, y, width, h), pixels, width * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4) {
            double luminance = 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
            if (luminance < darkest) darkest = luminance;
            if (luminance > brightest) brightest = luminance;
        }
    }
    static bool Loaded(string name) {
        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static int Run(string outDir) {
        folder = Path.GetFullPath(outDir);
        Directory.CreateDirectory(folder);
        string pass = Path.Combine(folder, "math-probe-PASS.txt"), fail = Path.Combine(folder, "math-probe-FAIL.txt");
        try { File.Delete(pass); File.Delete(fail); } catch (IOException) { }
        report.Append("# MathProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        int exit = 0;
        try {
            app = Application.Current;
            if (app == null) app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // 1. A document without math never loads WpfMath (4,000,000 characters, "$" lookalikes included).
            Check("0 start: WpfMath not loaded", !Loaded("WpfMath") && !Loaded("XamlMath.Shared"), null);
            BigDocumentCase();
            SyntaxCases();
            RenderCases();
            CaretCases();
            EditCases();
            NavigationCases();
            ScrollCases();
            TypingCases();
            ThemeAndShots();
            PrintCases();
            TableCase();
        } catch (Exception ex) {
            exit = 1;
            all = false;
            report.Append("FAIL probe exception: ").Append(ex).Append('\n');
        }
        report.Append("\n").Append(all ? "RESULT PASS" : "RESULT FAIL").Append('\n');
        File.WriteAllText(Path.Combine(folder, "math-probe.txt"), report.ToString(), new UTF8Encoding(false));
        File.WriteAllText(all && exit == 0 ? pass : fail, report.ToString(), new UTF8Encoding(false));
        return all && exit == 0 ? 0 : 1;
    }

    static void BigDocumentCase() {
        var text = new StringBuilder(4100000);
        int n = 0;
        while (text.Length < 4000000) {
            n++;
            string lookalike = n % 5 == 0 ? "$ x$ is no formula" : n % 5 == 1 ? "$y $ is none either" : n % 5 == 2 ? "$z$7 is none" : n % 5 == 3 ? "`$q$` is code" : "plain words";
            text.Append("Paragraph ").Append(n).Append(" costs $5 and $10, escaped \\$a\\$ stays text, snake_case_name * star.\n\n").Append(lookalike).Append(".\n\n");
            if (n % 50 == 0) text.Append("$$\n\nlone dollar pair above a blank line is not a block\n\n");
        }
        long before = MathRender.DiagParses;
        using (MathEnv env = Open(text.ToString(), false, false)) {
            Caret(env, 1, 1);
            env.Editor.ScrollToVerticalOffset(env.Editor.ExtentHeight / 2);
            Pump(env);
            Check("1 big document without math: " + text.Length + " chars, no WpfMath/XamlMath loaded, no parse",
                !Loaded("WpfMath") && !Loaded("XamlMath.Shared") && MathRender.DiagParses == before,
                "WpfMath=" + Loaded("WpfMath") + ", XamlMath=" + Loaded("XamlMath.Shared") + ", parses=" + (MathRender.DiagParses - before));
        }
    }

    static string InlineSources(MathEnv env, string line) {
        env.Editor.Text = line;
        Pump(env);
        MarkdownLivePreview.LineInfo info = Info(env, 1);
        var found = new List<string>();
        foreach (MarkdownLivePreview.Replacement item in info.Replacements)
            if (item.Kind == MarkdownLivePreview.ReplacementKind.Math) found.Add(item.Source);
        return string.Join("|", found.ToArray());
    }
    // One entry per document line: "-" nothing, "O<n>:<latex>" block opener, "I<n>" inside (n lines left), "1:<latex>" one-line.
    static string BlockMap(MathEnv env, string document) {
        env.Editor.Text = document;
        Pump(env);
        var map = new List<string>();
        for (int i = 1; i <= env.Editor.Document.LineCount; i++) {
            MarkdownLivePreview.LineInfo info = Info(env, i);
            if (info.MathCount > 0) map.Add("O" + info.MathCount + ":" + info.MathLatex);
            else if (info.MathRemaining > 0) map.Add("I" + info.MathRemaining);
            else if (info.MathOne) map.Add("1:" + info.MathLatex);
            else map.Add("-");
        }
        return string.Join(",", map.ToArray());
    }
    static void InlineCase(MathEnv env, string name, string line, string expected) {
        string got = InlineSources(env, line);
        Check("2 inline " + name, got == expected, "'" + line + "' -> [" + got + "] expected [" + expected + "]");
    }
    static void BlockCase(MathEnv env, string name, string document, string expected) {
        string got = BlockMap(env, document);
        Check("2 block " + name, got == expected, got + (got == expected ? "" : " expected " + expected));
    }
    static void SyntaxCases() {
        using (MathEnv env = Open("x", false, false)) {
            InlineCase(env, "simple", "a $x$ b", "x");
            InlineCase(env, "two", "a $x_i^2$ and $\\frac{1}{2}$", "x_i^2|\\frac{1}{2}");
            InlineCase(env, "price text", "$5 and $10", "");
            InlineCase(env, "escaped dollars", "cost \\$5 and \\$10", "");
            InlineCase(env, "space after opener", "$ x$", "");
            InlineCase(env, "space before closer", "$x $", "");
            InlineCase(env, "digit after closer", "$x$5", "");
            InlineCase(env, "double dollar never inline", "$$x$$ here", "");
            InlineCase(env, "code span", "`$x$` and $y$", "y");
            InlineCase(env, "escaped dollar inside", "$a\\$b$", "a\\$b");
            InlineCase(env, "emphasis inside is not markup", "$a_i * b_j$ and *real*", "a_i * b_j");
            InlineCase(env, "empty", "$$", "");
            InlineCase(env, "heading", "## Title $E=mc^2$", "E=mc^2");
            InlineCase(env, "list item", "- item $x$", "x");
            InlineCase(env, "quote", "> quote $x$", "x");
            InlineCase(env, "link label", "[see $x$](http://example.com)", "x");
            InlineCase(env, "unclosed", "an $unclosed formula", "");
            InlineCase(env, "digit before opener ok", "2$x$", "x");
            InlineCase(env, "table header cell", "| a | $x$ |\n|---|---|\n| $y$ | b |", "x");
            InlineCase(env, "table two cells", "| $a_1$ | $b$ |\n|---|---|", "a_1|b");
            InlineCase(env, "table price is text", "| $5 and $10 | c |\n|---|---|", "");
            InlineCase(env, "table formula holding a column pipe is text", "| $a | b$ | c |\n|---|---|---|", "");
            InlineCase(env, "table escaped pipe is the cell's own pipe", "| $a\\|b$ | c |\n|---|---|", "a|b");
            BlockCase(env, "simple block", "$$\nx^2\n$$", "O2:x^2,I2,I1");
            BlockCase(env, "block with indent", "  $$\nx\n  $$", "O2:x,I2,I1");
            BlockCase(env, "block joins lines", "$$\na +\nb\n$$", "O3:a + b,I3,I2,I1");
            BlockCase(env, "blank line inside is not math", "$$\na\n\nb\n$$", "-,-,-,-,-");
            BlockCase(env, "unclosed", "$$\na\nb", "-,-,-");
            BlockCase(env, "one line", "$$ x^2 $$", "1:x^2");
            BlockCase(env, "one line compact", "$$x$$", "1:x");
            BlockCase(env, "math fence", "```math\n\\frac{a}{b}\n```", "O2:\\frac{a}{b},I2,I1");
            BlockCase(env, "plain fence untouched", "```\n$$\nx\n$$\n```", "-,-,-,-,-");
            BlockCase(env, "dollars in fence", "```\n$x$\n```", "-,-,-");
            BlockCase(env, "unclosed math fence is code", "```math\nx", "-,-");
            BlockCase(env, "four-space indent is not an opener", "    $$\nx\n    $$", "-,-,-");
            BlockCase(env, "text after block", "$$\nx\n$$\nafter", "O2:x,I2,I1,-");
            var longBlock = new StringBuilder("$$\n");
            for (int i = 0; i < 90; i++) longBlock.Append("x\n");
            longBlock.Append("$$");
            string longMap = BlockMap(env, longBlock.ToString());
            Check("2 block longer than the cap is not math", !longMap.Contains("O"), "first=" + longMap.Substring(0, Math.Min(12, longMap.Length)));
        }
    }

    static void RenderCases() {
        var sources = new[] {
            @"\frac{a}{b}", @"\sqrt{x^2+y^2}", @"x_i^2", @"\alpha+\beta=\gamma", @"\begin{pmatrix}a&b\\c&d\end{pmatrix}",
            @"\text{hello world}", @"\color{red}{x}", @"\sum_{i=1}^{n} i", @"\int_0^1 x^2\,dx", @"E=mc^2"
        };
        foreach (string source in sources) {
            MathVisual visual = MathRender.Get(source, false, 16, Brushes.Black, "Malgun Gothic");
            Check("3 render " + source, !visual.Error && visual.Drawing != null && visual.Width > 1 && visual.Height > 1,
                visual.Error ? visual.Message : "w=" + N(visual.Width) + " asc=" + N(visual.Ascent) + " desc=" + N(visual.Descent));
        }
        MathVisual display = MathRender.Get(@"\sum_{i=1}^{n} i", true, 16, Brushes.Black, "Malgun Gothic");
        MathVisual text = MathRender.Get(@"\sum_{i=1}^{n} i", false, 16, Brushes.Black, "Malgun Gothic");
        Check("3 display style is larger than text style", display.Height > text.Height, "display h=" + N(display.Height) + " text h=" + N(text.Height));
        long renders = MathRender.DiagRenders;
        MathRender.Get(@"\sum_{i=1}^{n} i", false, 16, Brushes.Black, "Malgun Gothic");
        Check("3 render cache hit", MathRender.DiagRenders == renders, "renders " + renders + " -> " + MathRender.DiagRenders);
        MathRender.Get(@"\sum_{i=1}^{n} i", false, 16, Brushes.White, "Malgun Gothic");
        Check("3 other colour renders again", MathRender.DiagRenders == renders + 1, "renders " + renders + " -> " + MathRender.DiagRenders);
        bool failed = false;
        string errorText = "";
        foreach (string bad in new[] { @"\frac{", @"\unknowncommand{x}", @"x^", "{{{{", @"\begin{pmatrix}a&b" }) {
            try {
                MathVisual visual = MathRender.Get(bad, false, 16, Brushes.Black, "Malgun Gothic");
                bool ok = MathRender.Check(bad) == !visual.Error;
                if (!ok) { failed = true; errorText += bad + " inconsistent; "; }
                errorText += bad + " -> " + (visual.Error ? "error" : "rendered") + "; ";
            } catch (Exception ex) { failed = true; errorText += bad + " threw " + ex.GetType().Name + "; "; }
        }
        Check("3 invalid sources never throw", !failed, errorText);
        bool frac = !MathRender.Check(@"\frac{") && !MathRender.Check(@"\unknowncommand{x}");
        Check("3 \\frac{ and an unknown command are source text", frac, "last error: " + MathRender.LastError);
        string deep = new string('{', 5000) + "x" + new string('}', 5000);
        Check("3 deep nesting is refused without a crash", !MathRender.Check(deep), null);
        Check("3 WpfMath loaded once a formula was rendered", Loaded("WpfMath") && Loaded("XamlMath.Shared"), null);
    }

    const string CaretDocument = "Plain intro\n\nInline $x^2$ here and $y_i$ too.\n\n$$\n\\frac{a}{b}\n$$\n\n$$ \\sqrt{2} $$\nend";
    static void CaretCases() {
        using (MathEnv env = Open(CaretDocument, false, false)) {
            Caret(env, 1, 1);
            Check("4 live: caret away -> formulas rendered",
                MathElements(env, 3) == 2 && MathElements(env, 5) == 1 && MathElements(env, 9) == 1 && !env.Session.Raw(env.Editor.Document.GetLineByNumber(3)),
                "inline=" + MathElements(env, 3) + " block=" + MathElements(env, 5) + " one-line=" + MathElements(env, 9));
            Check("4 live: rendered block folds its other lines to zero height", Folded(env, 5, 7) && LineHeight(env, 5) > 20,
                "h5=" + N(LineHeight(env, 5)) + " h6=" + N(LineHeight(env, 6)) + " h7=" + N(LineHeight(env, 7)));
            bool oneStop = true;
            foreach (VisualLineElement element in env.Editor.TextArea.TextView.GetVisualLine(3).Elements)
                if (element is MathElement && (element.VisualLength != 1 || element.DocumentLength < 3)) oneStop = false;
            Check("4 a formula is one visual column (one caret stop)", oneStop, null);
            Caret(env, 3, 3);
            Check("4 live: caret on the formula's line -> raw source",
                env.Session.Raw(env.Editor.Document.GetLineByNumber(3)) && MathElements(env, 3) == 0 && MathElements(env, 5) == 1,
                "inline=" + MathElements(env, 3) + " block=" + MathElements(env, 5));
            Caret(env, 6, 1);
            bool allRaw = env.Session.Raw(env.Editor.Document.GetLineByNumber(5)) && env.Session.Raw(env.Editor.Document.GetLineByNumber(6)) && env.Session.Raw(env.Editor.Document.GetLineByNumber(7));
            Check("4 live: caret inside a block -> all its lines raw",
                allRaw && MathElements(env, 5) == 0 && LineHeight(env, 6) > 10 && LineHeight(env, 7) > 10 && MathElements(env, 3) == 2,
                "raw=" + allRaw + " h6=" + N(LineHeight(env, 6)) + " inline=" + MathElements(env, 3));
            Caret(env, 9, 1);
            Check("4 live: one-line display raw under the caret, block rendered again",
                MathElements(env, 9) == 0 && MathElements(env, 5) == 1 && Folded(env, 5, 7),
                "one-line=" + MathElements(env, 9) + " block=" + MathElements(env, 5) + " h6=" + N(LineHeight(env, 6)));
            DocumentLine three = env.Editor.Document.GetLineByNumber(3), seven = env.Editor.Document.GetLineByNumber(7);
            env.Editor.Select(three.Offset, seven.EndOffset - three.Offset);
            Pump(env);
            bool selectionRaw = env.Session.Raw(three) && env.Session.Raw(env.Editor.Document.GetLineByNumber(5)) && MathElements(env, 3) == 0 && MathElements(env, 5) == 0;
            string copied = env.Editor.SelectedText;
            Check("4 live: a selection across formulas shows and copies the source",
                selectionRaw && copied.Contains("$x^2$") && copied.Contains("\\frac{a}{b}") && copied.Contains("$y_i$"),
                "raw=" + selectionRaw + ", copied " + copied.Length + " chars");
        }
        using (MathEnv env = Open(CaretDocument, true, false)) {
            Caret(env, 1, 1);
            Check("4 basic: caret away -> formulas rendered", MathElements(env, 3) == 2 && MathElements(env, 5) == 1 && MathElements(env, 9) == 1,
                "inline=" + MathElements(env, 3) + " block=" + MathElements(env, 5));
            Caret(env, 3, 1);
            Check("4 basic: caret on the line but off the formula -> still rendered", MathElements(env, 3) == 2, "inline=" + MathElements(env, 3));
            string line3 = "Inline $x^2$ here and $y_i$ too.";
            Caret(env, 3, line3.IndexOf("x^2", StringComparison.Ordinal) + 2);
            Check("4 basic: caret inside a formula -> that formula shows its source", MathElements(env, 3) == 1, "inline=" + MathElements(env, 3));
            Caret(env, 3, line3.IndexOf("$x^2$", StringComparison.Ordinal) + 1);
            Check("4 basic: caret at a formula's edge -> source", MathElements(env, 3) == 1, "inline=" + MathElements(env, 3));
            Caret(env, 3, line3.Length + 1);
            Check("4 basic: caret leaves the formula -> rendered again", MathElements(env, 3) == 2, "inline=" + MathElements(env, 3));
            Caret(env, 6, 1);
            bool blockRaw = env.Session.Raw(env.Editor.Document.GetLineByNumber(5)) && MathElements(env, 5) == 0 && LineHeight(env, 6) > 10;
            Check("4 basic: caret inside a block -> block source", blockRaw, "h6=" + N(LineHeight(env, 6)));
            Caret(env, 9, 2);
            Check("4 basic: caret in a one-line display -> source", MathElements(env, 9) == 0, "one-line=" + MathElements(env, 9));
        }
    }

    static void Press(MathEnv env, RoutedCommand command) {
        command.Execute(null, env.Editor.TextArea);
        Pump(env);
    }
    // Arrow keys walk through a rendered block line by line (it opens as source under the caret) and back out of it.
    static void NavigationCases() {
        foreach (bool hide in new[] { false, true }) {
            using (MathEnv env = Open(CaretDocument, hide, false)) {
                string mode = hide ? "basic" : "live";
                Caret(env, 1, 1);
                var down = new List<int>();
                for (int i = 0; i < 9; i++) { Press(env, EditingCommands.MoveDownByLine); down.Add(env.Editor.TextArea.Caret.Line); }
                string got = string.Join(",", down.ToArray());
                Check("10 " + mode + ": Down walks 1->10 through the block line by line", got == "2,3,4,5,6,7,8,9,10", got);
                bool outside = Folded(env, 5, 7) && MathElements(env, 5) == 1;
                Check("10 " + mode + ": after leaving the block it is rendered and folded again", outside, "elements=" + MathElements(env, 5));
                var up = new List<int>();
                for (int i = 0; i < 9; i++) { Press(env, EditingCommands.MoveUpByLine); up.Add(env.Editor.TextArea.Caret.Line); }
                string gotUp = string.Join(",", up.ToArray());
                Check("10 " + mode + ": Up walks back through the block", gotUp == "9,8,7,6,5,4,3,2,1" || gotUp == "9,8,5,4,3,2,1,1,1" || gotUp.StartsWith("9,8,7,6,5,4,3,2,1"), gotUp);
                Caret(env, 4, 1);
                Press(env, EditingCommands.MoveRightByCharacter);
                Check("10 " + mode + ": Right from the blank line above lands on the opener", env.Editor.TextArea.Caret.Line == 5, "line=" + env.Editor.TextArea.Caret.Line);
                Caret(env, 8, 1);
                Press(env, EditingCommands.MoveLeftByCharacter);
                int leftLine = env.Editor.TextArea.Caret.Line;
                Check("10 " + mode + ": Left from the blank line below lands on the block's last line", leftLine == 7, "line=" + leftLine);
            }
        }
    }

    // Scrolling so that the view starts inside a rendered block, and back, must not throw and must end up folded.
    static void ScrollCases() {
        var text = new StringBuilder();
        for (int i = 1; i <= 40; i++) text.Append("Paragraph ").Append(i).Append(" before.\n\n");
        text.Append("$$\n\\frac{a}{b}\n+ c\n$$\n\n");
        for (int i = 1; i <= 40; i++) text.Append("Paragraph ").Append(i).Append(" after $x_").Append(i).Append("$.\n\n");
        foreach (bool hide in new[] { false, true }) {
            using (MathEnv env = Open(text.ToString(), hide, false)) {
                string mode = hide ? "basic" : "live";
                string error = null;
                int opener = 81;
                try {
                    Caret(env, 1, 1);
                    TextView view = env.Editor.TextArea.TextView;
                    double top = view.GetVisualTopByDocumentLine(opener);
                    foreach (double offset in new[] { top + 5, top + 30, top - 40, 0, top + 60, top + 200, top - 5, 0 }) {
                        env.Editor.ScrollToVerticalOffset(Math.Max(0, offset));
                        Pump(env);
                    }
                    for (double offset = 0; offset < view.DocumentHeight; offset += 37) { env.Editor.ScrollToVerticalOffset(offset); Pump(env); }
                    env.Editor.ScrollToVerticalOffset(Math.Max(0, top - 60));
                    Pump(env);
                } catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
                bool folded = error == null && Folded(env, opener, opener + 3) && MathElements(env, opener) == 1;
                Check("11 " + mode + ": scrolling through and into a rendered block does not throw, block ends folded", error == null && folded,
                    error ?? ("folded=" + folded + " elements=" + MathElements(env, opener)));
            }
        }
    }

    static string LatexAt(MathEnv env, int number) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        if (visual == null) return null;
        foreach (VisualLineElement element in visual.Elements) { MathElement math = element as MathElement; if (math != null) return math.Latex; }
        return null;
    }
    static void EditCases() {
        using (MathEnv env = Open("$$\nx\n$$\n\ntext", false, false)) {
            TextDocument document = env.Editor.Document;
            Caret(env, 5, 1);
            Check("5 block opener parsed", Info(env, 1).MathCount == 2 && Info(env, 1).MathLatex == "x" && LatexAt(env, 1) == "x", "latex=" + Info(env, 1).MathLatex);
            document.Replace(document.GetLineByNumber(2).Offset, 1, "y");
            Pump(env);
            Check("5 editing an inner line refreshes the opener's formula", Info(env, 1).MathLatex == "y" && LatexAt(env, 1) == "y", "latex=" + Info(env, 1).MathLatex + " element=" + LatexAt(env, 1));
            document.Insert(document.GetLineByNumber(2).Offset, "\n");
            Pump(env);
            Check("5 a blank line inside ends the block", Info(env, 1).MathCount == 0 && Info(env, 3).MathRemaining == 0 && MathElements(env, 1) == 0 && LineHeight(env, 3) > 10,
                "count=" + Info(env, 1).MathCount + " h3=" + N(LineHeight(env, 3)));
            document.Remove(document.GetLineByNumber(2).Offset, 1);
            Pump(env);
            Check("5 removing it restores the block", Info(env, 1).MathCount == 2 && MathElements(env, 1) == 1 && Folded(env, 1, 3),
                "count=" + Info(env, 1).MathCount + " elements=" + MathElements(env, 1) + " h2=" + N(LineHeight(env, 2)));
        }
        using (MathEnv env = Open("$$\nx\ntext\n\nafter", false, false)) {
            TextDocument document = env.Editor.Document;
            Caret(env, 5, 1);
            Check("5 unclosed opener is text", Info(env, 1).MathCount == 0 && MathElements(env, 1) == 0, "count=" + Info(env, 1).MathCount);
            document.Insert(document.GetLineByNumber(3).EndOffset, "\n$$");
            Pump(env);
            Check("5 typing the closer below makes the opener a block", Info(env, 1).MathCount == 3 && MathElements(env, 1) == 1 && Folded(env, 1, 4) && LatexAt(env, 1) == "x text",
                "count=" + Info(env, 1).MathCount + " elements=" + MathElements(env, 1) + " latex=" + LatexAt(env, 1));
            document.Replace(document.GetLineByNumber(2).Offset, 1, "z");
            Pump(env);
            Check("5 editing a block above the caret while it is rendered refreshes the picture", LatexAt(env, 1) == "z text", "latex=" + LatexAt(env, 1));
            document.Remove(document.GetLineByNumber(4).Offset, document.GetLineByNumber(4).TotalLength);
            Pump(env);
            Check("5 deleting the closer turns the block back into text", Info(env, 1).MathCount == 0 && MathElements(env, 1) == 0 && LineHeight(env, 2) > 10,
                "count=" + Info(env, 1).MathCount + " h2=" + N(LineHeight(env, 2)));
        }
        using (MathEnv env = Open("$$\n\\frac{\n$$\n\nafter", false, false)) {
            Caret(env, 5, 1);
            Check("5 a block whose formula does not parse shows its source", MathElements(env, 1) == 0 && LineHeight(env, 2) > 10 && LineHeight(env, 3) > 10,
                "elements=" + MathElements(env, 1) + " h2=" + N(LineHeight(env, 2)));
        }
    }

    static void TypingCases() {
        foreach (bool hide in new[] { false, true }) {
            using (MathEnv env = Open("Start\n\na $x$ b $y$ c $z$ d\n\nend", hide, false)) {
                Caret(env, 1, 1);
                int rendered = MathElements(env, 3);
                DocumentLine line = env.Editor.Document.GetLineByNumber(3);
                env.Editor.TextArea.Caret.Offset = line.EndOffset;
                Pump(env);
                long parses = MathRender.DiagParses, renders = MathRender.DiagRenders, scans = MarkdownLivePreview.DiagBlockScans;
                int cached = MathRender.CachedVisuals;
                for (int i = 0; i < 50; i++) { env.Editor.TextArea.PerformTextInput("q"); Pump(env); }
                long scanned = MarkdownLivePreview.DiagBlockScans - scans;
                string mode = hide ? "basic" : "live";
                Check("6 " + mode + ": 50 characters typed on a line with 3 formulas: no parse, no render",
                    MathRender.DiagParses == parses && MathRender.DiagRenders == renders && MathRender.CachedVisuals == cached,
                    "parses +" + (MathRender.DiagParses - parses) + ", renders +" + (MathRender.DiagRenders - renders));
                Check("6 " + mode + ": typing scans a bounded number of block lines", scanned <= 50 * 6, "block scans +" + scanned + " for 50 characters");
                Check("6 " + mode + ": the typed line still shows its formulas " + (hide ? "rendered" : "as source"), hide ? MathElements(env, 3) == rendered && rendered == 3 : MathElements(env, 3) == 0,
                    "elements=" + MathElements(env, 3) + " (rendered before: " + rendered + ")");
            }
        }
    }

    const string SampleDocument =
        "# Heading with math $E=mc^2$\n\n" +
        "Inline math: the area is $\\pi r^2$, and $\\frac{a}{b}$ sits in the text. Prices: $5 and $10 stay text.\n\n" +
        "$$\n\\int_0^1 x^2\\,dx = \\frac{1}{3}\n$$\n\n" +
        "$$\\sum_{i=1}^{n} i = \\frac{n(n+1)}{2}$$\n\n" +
        "```math\n\\begin{pmatrix} a & b \\\\ c & d \\end{pmatrix}\n```\n\n" +
        "Greek: $\\alpha + \\beta = \\gamma$, $\\sqrt{x^2+y^2}$, $\\text{hello world}$, $\\color{red}{x}$.\n\n" +
        "Invalid: $\\frac{$ stays text.\n\n" +
        "End.";
    static void ThemeAndShots() {
        using (MathEnv env = Open(SampleDocument, false, false)) {
            Caret(env, 19, 1);
            Shot(env, "math-light.png");
            double darkest, brightest;
            Luminance(env, 5, out darkest, out brightest);
            Check("7 light: display formula ink is dark", darkest < 100, "darkest=" + N(darkest) + " brightest=" + N(brightest));
            Check("7 light: heading, inline, display, one-line, math fence and Greek lines each carry a formula",
                MathElements(env, 1) == 1 && MathElements(env, 3) == 2 && MathElements(env, 5) == 1 && MathElements(env, 9) == 1 && MathElements(env, 11) == 1 && MathElements(env, 15) == 4 && MathElements(env, 17) == 0,
                "h=" + MathElements(env, 1) + " i=" + MathElements(env, 3) + " d=" + MathElements(env, 5) + " one=" + MathElements(env, 9) + " fence=" + MathElements(env, 11) + " greek=" + MathElements(env, 15) + " invalid=" + MathElements(env, 17));
            VisualLine heading = env.Editor.TextArea.TextView.GetVisualLine(1);
            Check("7 heading line is taller than a body line (the formula follows the heading size)", heading != null && heading.Height > LineHeight(env, 3) * 0.9, "h1=" + N(LineHeight(env, 1)) + " h3=" + N(LineHeight(env, 3)));
            long renders = MathRender.DiagRenders;
            env.Editor.Foreground = Brushes.White;
            env.Editor.Background = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24));
            env.Settings.Dark = true;
            env.Session.Update(env.Settings);
            Pump(env);
            Check("7 dark: the theme change re-renders the formulas in the new colour", MathRender.DiagRenders > renders, "renders +" + (MathRender.DiagRenders - renders));
            Shot(env, "math-dark.png");
            Luminance(env, 5, out darkest, out brightest);
            Check("7 dark: display formula ink is light", brightest > 170, "darkest=" + N(darkest) + " brightest=" + N(brightest));
            Caret(env, 3, 5);
            Shot(env, "math-source.png");
            Caret(env, 6, 1);
            Shot(env, "math-block-source.png");
        }
        using (MathEnv env = Open(SampleDocument, true, false)) {
            Caret(env, 19, 1);
            Shot(env, "math-basic.png");
            Check("7 basic: same formulas rendered", MathElements(env, 3) == 2 && MathElements(env, 5) == 1 && MathElements(env, 15) == 4, "i=" + MathElements(env, 3) + " d=" + MathElements(env, 5));
        }
    }

    static int Count<T>(IEnumerable<System.Windows.Documents.Inline> inlines, string tag) where T : System.Windows.Documents.Inline {
        int count = 0;
        foreach (System.Windows.Documents.Inline inline in inlines) {
            var container = inline as InlineUIContainer;
            if (container != null && (tag == null || Equals(container.Tag, tag))) count++;
        }
        return count;
    }
    static void PrintCases() {
        FlowDocument document = Markdown.Parse("Inline $x^2$ and $a_i * b_j$ end\n\n$$\n\\frac{a}{b}\n$$\n\n$$ \\sqrt{2} $$\n\nBad $\\frac{$ stays.\n\n$5 and $10 text\n", 16, "Malgun Gothic", Brushes.Black, false, "");
        int inlineMath = 0, blockMath = 0, italics = 0, badMath = 0;
        string badText = "";
        foreach (Block block in document.Blocks) {
            var paragraph = block as Paragraph;
            if (paragraph != null) {
                int found = Count<System.Windows.Documents.Inline>(paragraph.Inlines, "math");
                string text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text;
                if (text.Contains("Bad") || text.Contains("Prices") || text.Contains("10 text")) { badMath += found; badText += text.Trim() + "|"; }
                else inlineMath += found;
                foreach (System.Windows.Documents.Inline inline in paragraph.Inlines) if (inline is Italic) italics++;
            }
            var container = block as BlockUIContainer;
            if (container != null && container.Child is Image) blockMath++;
        }
        Check("8 print: inline formulas become pictures, emphasis inside a formula is not emphasis", inlineMath == 2 && italics == 0, "inline pictures=" + inlineMath + " italics=" + italics);
        Check("8 print: display formulas become block pictures", blockMath == 2, "block pictures=" + blockMath);
        Check("8 print: an unparsable formula and a price stay text", badMath == 0 && badText.Contains("$\\frac{$") && badText.Contains("$5 and $10"), badText);
    }

    // ---- table cells ----

    static MarkdownLivePreview.TableRowElement RowOf(MathEnv env, int number) {
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        if (visual == null) return null;
        foreach (VisualLineElement element in visual.Elements) {
            var row = element as MarkdownLivePreview.TableRowElement;
            if (row != null) return row;
        }
        return null;
    }
    // Formulas the cell draws as objects (0 = its source is text), -1 when the row is not built.
    static int Formulas(MathEnv env, int number, int cell) {
        MarkdownLivePreview.TableRowElement row = RowOf(env, number);
        return row == null ? -1 : row.CellFormulas(cell);
    }
    static int ColumnOf(MathEnv env, int number, string needle, int shift) {
        string text = env.Editor.Document.GetText(env.Editor.Document.GetLineByNumber(number));
        int at = text.IndexOf(needle, StringComparison.Ordinal);
        return at < 0 ? 1 : at + 1 + shift;
    }
    // One caret move in one step (a click or an arrow key), unlike Caret(), whose Line then Column assignments are two moves.
    static void TCaret(MathEnv env, int line, int column) {
        env.Editor.TextArea.Caret.Position = new TextViewPosition(line, column);
        Pump(env);
    }
    static void Wait(int milliseconds) {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    // The darkest / brightest pixel luminance inside the first cell of a table row (its text band only).
    static void CellInk(MathEnv env, int number, out double darkest, out double brightest) {
        darkest = 255; brightest = 0;
        VisualLine visual = env.Editor.TextArea.TextView.GetVisualLine(number);
        MarkdownLivePreview.TableRowElement row = RowOf(env, number);
        if (visual == null || row == null) return;
        TextView view = env.Editor.TextArea.TextView;
        double top = visual.VisualTop - view.VerticalOffset;
        Point a = view.TranslatePoint(new Point(row.Row.Boundaries[0] + row.Row.Padding, top + 4), env.Editor);
        Point b = view.TranslatePoint(new Point(row.Row.Boundaries[1] - row.Row.Padding, top + visual.Height - 4), env.Editor);
        int x = Math.Max(0, (int)a.X), y = Math.Max(0, (int)a.Y);
        int w = Math.Max(1, Math.Min((int)env.Editor.ActualWidth - x, (int)(b.X - a.X))), h = Math.Max(1, Math.Min((int)env.Editor.ActualHeight - y, (int)(b.Y - a.Y)));
        var bitmap = new RenderTargetBitmap((int)env.Editor.ActualWidth, (int)env.Editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (DrawingContext context = background.RenderOpen()) context.DrawRectangle(env.Editor.Background, null, new Rect(0, 0, env.Editor.ActualWidth, env.Editor.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(env.Editor);
        var pixels = new byte[w * h * 4];
        bitmap.CopyPixels(new Int32Rect(x, y, w, h), pixels, w * 4, 0);
        for (int i = 0; i < pixels.Length; i += 4) {
            double luminance = 0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2];
            if (luminance < darkest) darkest = luminance;
            if (luminance > brightest) brightest = luminance;
        }
    }
    static int TableMath(FlowDocument document, out string leftover) {
        int count = 0;
        var text = new StringBuilder();
        foreach (Block block in document.Blocks) {
            var table = block as System.Windows.Documents.Table;
            if (table == null) continue;
            foreach (TableRowGroup group in table.RowGroups)
                foreach (TableRow row in group.Rows)
                    foreach (TableCell cell in row.Cells)
                        foreach (Block inner in cell.Blocks) {
                            var paragraph = inner as Paragraph;
                            if (paragraph == null) continue;
                            count += Count<System.Windows.Documents.Inline>(paragraph.Inlines, "math");
                            text.Append(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim()).Append('|');
                        }
        }
        leftover = text.ToString();
        return count;
    }

    // A row: header, delimiter, then body rows 3.. (line 7 is plain text below the table, where the caret rests).
    const string TableDocument =
        "| name | formula |\n|---|---|\n| $x^2$ | $\\frac{a}{b}$ and text |\n| price | $5 and $10 |\n| bad | $\\frac{$ |\n| esc | $a\\|b$ |\nend";
    const string WideFormula = "\\frac{x_1+x_2+x_3+x_4+x_5+x_6+x_7+x_8+x_9+x_{10}+x_{11}+x_{12}}{\\sqrt{\\sum_{i=1}^{n} y_i^2}}";

    static void TableCase() {
        // Live mode: a formula in a cell is an object of the cell text; the row under the caret shows its source.
        using (MathEnv env = Open(TableDocument, false, false)) {
            TCaret(env, 7, 1);
            DocumentLine escLine = env.Editor.Document.GetLineByNumber(6);
            string escSource = "";
            foreach (MarkdownLivePreview.Replacement item in Info(env, 6).Replacements)
                if (item.Kind == MarkdownLivePreview.ReplacementKind.Math) escSource = item.Source;
            Check("9 live: formulas in table cells are drawn", Formulas(env, 3, 0) == 1 && Formulas(env, 3, 1) == 1 && Formulas(env, 6, 1) == 1 && Info(env, 3).MathInline,
                "r3c0=" + Formulas(env, 3, 0) + " r3c1=" + Formulas(env, 3, 1) + " r6c1=" + Formulas(env, 6, 1));
            Check("9 live: a price and an unparsable formula stay cell text", Formulas(env, 4, 1) == 0 && Formulas(env, 5, 1) == 0 && !env.Session.Raw(env.Editor.Document.GetLineByNumber(4)),
                "price=" + Formulas(env, 4, 1) + " bad=" + Formulas(env, 5, 1));
            Check("9 live: an escaped pipe in a cell formula is the pipe (source a|b)", escSource == "a|b" && escLine.Length > 0, "source=[" + escSource + "]");
            Check("9 live: the document text is untouched", env.Editor.Document.Text == TableDocument, null);
            Shot(env, "math-table-light.png");
            double darkest, brightest;
            CellInk(env, 3, out darkest, out brightest);
            Check("9 live light: the cell formula ink is dark", darkest < 110, "darkest=" + N(darkest) + " brightest=" + N(brightest));

            long parses = MathRender.DiagParses, renders = MathRender.DiagRenders;
            TCaret(env, 3, ColumnOf(env, 3, "\\frac", 3));
            Check("9 live: caret in the row -> the row shows source, other rows stay drawn",
                env.Session.Raw(env.Editor.Document.GetLineByNumber(3)) && Formulas(env, 3, 0) == 0 && Formulas(env, 3, 1) == 0 && Formulas(env, 6, 1) == 1,
                "r3=" + Formulas(env, 3, 0) + "/" + Formulas(env, 3, 1) + " r6=" + Formulas(env, 6, 1));
            Shot(env, "math-table-source.png");
            TCaret(env, 7, 1);
            Check("9 live: caret away -> drawn again, from the cache (no parse, no render)",
                Formulas(env, 3, 0) == 1 && Formulas(env, 3, 1) == 1 && MathRender.DiagParses == parses && MathRender.DiagRenders == renders,
                "r3=" + Formulas(env, 3, 0) + "/" + Formulas(env, 3, 1) + " parses +" + (MathRender.DiagParses - parses) + " renders +" + (MathRender.DiagRenders - renders));

            DocumentLine three = env.Editor.Document.GetLineByNumber(3), six = env.Editor.Document.GetLineByNumber(6);
            env.Editor.Select(three.Offset, six.EndOffset - three.Offset);
            Pump(env);
            string copied = env.Editor.SelectedText;
            Check("9 live: a selection over rows shows and copies the source",
                Formulas(env, 3, 0) == 0 && Formulas(env, 6, 1) == 0 && copied.Contains("$x^2$") && copied.Contains("$a\\|b$"),
                "r3=" + Formulas(env, 3, 0) + " r6=" + Formulas(env, 6, 1));
            TCaret(env, 7, 1);
        }
        // No-reveal mode: the caret row stays formatted; only the formula the caret touches shows its source.
        using (MathEnv env = Open(TableDocument, true, false)) {
            TCaret(env, 7, 1);
            Check("9 basic: formulas in table cells are drawn", Formulas(env, 3, 0) == 1 && Formulas(env, 3, 1) == 1 && Formulas(env, 6, 1) == 1,
                "r3c0=" + Formulas(env, 3, 0) + " r3c1=" + Formulas(env, 3, 1));
            Shot(env, "math-table-basic.png");
            long parses = MathRender.DiagParses, renders = MathRender.DiagRenders;
            TCaret(env, 3, ColumnOf(env, 3, "text", 2));
            Check("9 basic: caret in the row but off the formulas -> both stay drawn",
                !env.Session.Raw(env.Editor.Document.GetLineByNumber(3)) && Formulas(env, 3, 0) == 1 && Formulas(env, 3, 1) == 1,
                "r3=" + Formulas(env, 3, 0) + "/" + Formulas(env, 3, 1) + " key=" + Info(env, 3).MathTouchKey + " caret col=" + env.Editor.TextArea.Caret.Column + " raw=" + env.Session.Raw(env.Editor.Document.GetLineByNumber(3)));
            TCaret(env, 3, ColumnOf(env, 3, "\\frac", 3));
            Check("9 basic: caret inside a formula -> only that one shows its source", Formulas(env, 3, 0) == 1 && Formulas(env, 3, 1) == 0,
                "r3=" + Formulas(env, 3, 0) + "/" + Formulas(env, 3, 1));
            Shot(env, "math-table-basic-source.png");
            TCaret(env, 3, ColumnOf(env, 3, "$x^2$", 0));
            Check("9 basic: caret at a formula's edge shows its source", Formulas(env, 3, 0) == 0 && Formulas(env, 3, 1) == 1,
                "r3=" + Formulas(env, 3, 0) + "/" + Formulas(env, 3, 1));
            TCaret(env, 7, 1);
            Check("9 basic: caret away -> all drawn again, from the cache",
                Formulas(env, 3, 0) == 1 && Formulas(env, 3, 1) == 1 && MathRender.DiagParses == parses && MathRender.DiagRenders == renders,
                "parses +" + (MathRender.DiagParses - parses) + " renders +" + (MathRender.DiagRenders - renders));
        }
        // Dark mode: the formula takes the cell text's colour.
        using (MathEnv env = Open(TableDocument, false, true)) {
            TCaret(env, 7, 1);
            Shot(env, "math-table-dark.png");
            Color ink = ((SolidColorBrush)env.Session.CellDefaults[0].ForegroundBrush).Color;
            double darkest, brightest;
            CellInk(env, 3, out darkest, out brightest);
            Check("9 dark: the cell formula is drawn in the light cell text colour", Formulas(env, 3, 0) == 1 && ink.R > 170 && brightest > 190,
                "ink=" + ink + " brightest=" + N(brightest) + " darkest=" + N(darkest));
        }
        // Layout: the column measures the formula's width and the row grows with its height.
        double plainWidth, plainHeight, wideWidth, wideHeight, formulaWidth, formulaHeight, slice, sliceWidth;
        using (MathEnv env = Open("| a | b |\n|---|---|\n| x | y |\nend", false, false)) {
            TCaret(env, 4, 1);
            plainWidth = RowOf(env, 3).Row.Widths[1];
            plainHeight = LineHeight(env, 3);
        }
        using (MathEnv env = Open("| a | b |\n|---|---|\n| x | $" + WideFormula + "$ |\nend", false, false)) {
            TCaret(env, 4, 1);
            MarkdownLivePreview.TableRowElement row = RowOf(env, 3);
            MathVisual visual = env.Session.CellMath(WideFormula, env.Session.CellDefaults[0]);
            formulaWidth = visual == null ? 0 : visual.Width;
            formulaHeight = visual == null ? 0 : visual.Height;
            wideWidth = row.Row.Widths[1];
            wideHeight = LineHeight(env, 3);
            slice = row.CellSliceHeight(1);
            sliceWidth = row.CellSliceWidth(1);
            Check("9 layout: the column is measured with the formula's width",
                visual != null && formulaWidth > 60 && wideWidth - row.Row.Padding * 2 >= formulaWidth - 0.5 && wideWidth > plainWidth + 40 && sliceWidth >= formulaWidth - 0.5,
                "formula w=" + N(formulaWidth) + ", column=" + N(wideWidth) + " (plain table " + N(plainWidth) + "), drawn slice=" + N(sliceWidth));
            Check("9 layout: the row grows to the formula's height and keeps the cell padding", formulaHeight > 30 && slice >= formulaHeight - 0.5 && wideHeight >= formulaHeight + 12 && wideHeight > plainHeight + 10,
                "formula h=" + N(formulaHeight) + ", cell slice=" + N(slice) + ", row " + N(wideHeight) + " (plain row " + N(plainHeight) + ")");
            Shot(env, "math-table-wide.png");
        }
        // Editing: a formula typed into a cell is drawn once the caret has left the row, and the idle re-sample widens the column.
        using (MathEnv env = Open("| a | b |\n|---|---|\n| x | 1 |\nend", false, false)) {
            TCaret(env, 4, 1);
            double before = RowOf(env, 3).Row.Widths[1];
            int at = env.Editor.Document.GetLineByNumber(3).Offset + env.Editor.Document.GetText(env.Editor.Document.GetLineByNumber(3)).IndexOf("1", StringComparison.Ordinal);
            env.Editor.Document.Replace(at, 1, "$" + WideFormula + "$");
            Pump(env);
            Wait(1500);
            Pump(env);
            MarkdownLivePreview.TableRowElement row = RowOf(env, 3);
            double after = row == null ? 0 : row.Row.Widths[1];
            Check("9 edit: a formula typed into a cell is drawn and its column widens", row != null && row.CellFormulas(1) == 1 && after > before + 40,
                "formulas=" + (row == null ? -1 : row.CellFormulas(1)) + ", column " + N(before) + " -> " + N(after));
            env.Editor.Document.Replace(at, env.Editor.Document.GetLineByNumber(3).EndOffset - at - 2, "$x$");
            Pump(env);
            Check("9 edit: editing the formula source redraws it", Formulas(env, 3, 1) == 1, "formulas=" + Formulas(env, 3, 1));
        }
        // Print / plain renderer: cells go through the same inline parser, so cell formulas are pictures too.
        FlowDocument printed = Markdown.Parse("| a | b |\n|---|---|\n| $x^2$ | $\\frac{a}{b}$ and text |\n| $5 and $10 | $\\frac{$ |\n", 16, "Malgun Gothic", Brushes.Black, false, "");
        string leftover;
        int cellPictures = TableMath(printed, out leftover);
        Check("9 print: formulas in table cells become pictures, a price and an unparsable formula stay text",
            cellPictures == 2 && leftover.Contains("$5 and $10") && leftover.Contains("$\\frac{$"), "pictures=" + cellPictures + " cells=" + leftover);
    }
}
}
