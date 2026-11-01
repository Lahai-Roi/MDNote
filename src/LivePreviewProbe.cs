using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// Headless GUI probe for the P1 live-preview behaviors. Generated document only;
// no user file is read. Writes a report and a PASS marker into the output folder.
internal static class LivePreviewProbe {
    const string DocumentText = "# Heading\n![missing](missing.png)\n\n| alpha | beta | gamma |\n| --- | --- | --- |\n| one | two | three |\n| four | five | six |\n\n- [ ] task\n\n[t](https://example.invalid/x)\nplain";
    const string LinkUrl = "https://example.invalid/x";

    internal static int Run(string outDir) {
        string dir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        report.Append("# LivePreviewProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        bool all = true;
        int exit = 0;
        Application app = null;
        bool ownApp = false;
        SourceEditor editor = null;
        MarkdownLivePreview.Session session = null;
        Window window = null;
        try {
            app = Application.Current;
            if (app == null) {
                app = new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
                ownApp = true;
            }
            editor = new SourceEditor {
                FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true,
                Background=Brushes.White, Foreground=Brushes.Black
            };
            editor.TextArea.TextView.LineHeight = double.NaN;
            editor.Text = DocumentText;
            var settings = new MarkdownLivePreview.Settings {
                FontSize=16, Family="Malgun Gothic", LinePercent=150, Dark=false,
                WordWrap=true, BaseDirectory=dir
            };
            session = MarkdownLivePreview.Attach(editor, settings);
            window = new Window {
                Left=-20000, Top=0, Width=900, Height=1100, ShowInTaskbar=false,
                ShowActivated=false, WindowStyle=WindowStyle.None, Content=editor
            };
            window.Show();
            Pump(editor);
            editor.TextArea.Caret.Line = 1;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);

            // a. Read-only sessions never reveal markup, from either flag.
            DocumentLine firstLine = editor.Document.GetLineByNumber(1);
            bool aCaret = session.Raw(firstLine);
            settings.ReadOnly = true;
            session.Update(settings);
            Pump(editor);
            bool aOption = session.Raw(firstLine);
            settings.ReadOnly = false;
            session.Update(settings);
            Pump(editor);
            bool aRestored = session.Raw(firstLine);
            editor.IsReadOnly = true;
            bool aEditor = session.Raw(firstLine);
            editor.IsReadOnly = false;
            Check(ref all, report, "a read-only Raw", aCaret && !aOption && aRestored && !aEditor,
                "caret=" + aCaret + ", option=" + aOption + ", restored=" + aRestored + ", editor=" + aEditor);

            // b. Update rebuilds derived state without touching the editor font.
            VisualLine heading = editor.TextArea.TextView.GetVisualLine(1);
            double heightBefore = heading == null ? 0 : heading.Height;
            var bigger = new MarkdownLivePreview.Settings {
                FontSize=24, Family="Malgun Gothic", LinePercent=150, Dark=false,
                WordWrap=true, BaseDirectory=dir
            };
            session.Update(bigger);
            Pump(editor);
            VisualLine grown = editor.TextArea.TextView.GetVisualLine(1);
            double heightAfter = grown == null ? 0 : grown.Height;
            double editorFont = editor.FontSize;
            session.Update(settings);
            Pump(editor);
            Check(ref all, report, "b Update font height", heightBefore > 0 && heightAfter > heightBefore + 1 && Math.Abs(editorFont - 16) < 0.01,
                "height " + N(heightBefore) + "->" + N(heightAfter) + ", editor font=" + N(editorFont));

            // c. A raw image line shows the source text and keeps the image as a
            // zero-document-length visual element.
            editor.TextArea.Caret.Line = 2;
            Pump(editor);
            DocumentLine imageLine = editor.Document.GetLineByNumber(2);
            VisualLine imageVisual = editor.TextArea.TextView.GetVisualLine(2);
            bool cRaw = session.Raw(imageLine);
            VisualLineElement imageElement = null;
            if (imageVisual != null) {
                foreach (VisualLineElement element in imageVisual.Elements) {
                    if (element.GetType().Name == "RawImageElement") imageElement = element;
                }
            }
            bool cLengths = imageElement != null && imageElement.DocumentLength == 0 && imageElement.VisualLength == 1;
            int cVisual = imageVisual == null ? -1 : imageVisual.VisualLength;
            // text + strut + raw image element + word-wrap filler
            int cExpected = imageLine.Length + 3;
            Check(ref all, report, "c raw image line", cRaw && cLengths && cVisual == cExpected,
                "raw=" + cRaw + ", element=" + (imageElement != null) + ", doc/visual=" +
                (imageElement == null ? "-" : imageElement.DocumentLength + "/" + imageElement.VisualLength) +
                ", line visual=" + cVisual + "/" + cExpected);

            // d. A raw table row keeps the column stops of its formatted neighbours.
            editor.TextArea.Caret.Line = 7;
            Pump(editor);
            DocumentLine formattedLine = editor.Document.GetLineByNumber(6);
            DocumentLine tableRawLine = editor.Document.GetLineByNumber(7);
            VisualLine formatted = editor.TextArea.TextView.GetVisualLine(6);
            VisualLine tableRaw = editor.TextArea.TextView.GetVisualLine(7);
            MarkdownLivePreview.LineInfo formattedInfo = session.Info(formattedLine);
            MarkdownLivePreview.LineInfo tableRawInfo = session.Info(tableRawLine);
            bool dRaw = session.Raw(tableRawLine) && !session.Raw(formattedLine);
            double worst = -1;
            string cells = "";
            if (formatted != null && tableRaw != null && formattedInfo.Cells != null && tableRawInfo.Cells != null &&
                formattedInfo.Cells.Count == tableRawInfo.Cells.Count) {
                worst = 0;
                int count = Math.Min(3, formattedInfo.Cells.Count);
                for (int c = 0; c < count; c++) {
                    double fx = CellX(formatted, formattedInfo.Cells[c].Start);
                    double rx = CellX(tableRaw, tableRawInfo.Cells[c].Start);
                    double dx = Math.Abs(fx - rx);
                    if (dx > worst) worst = dx;
                    if (c > 0) cells += ", ";
                    cells += N(fx) + "/" + N(rx);
                }
            }
            Check(ref all, report, "d raw table alignment", dRaw && worst >= 0 && worst <= 6,
                "max dx=" + N(worst) + " cells(formatted/raw)=" + cells);

            // e. The preview click on the checkbox toggles the task without moving
            // the caret (the line is formatted, so the checkbox exists).
            editor.TextArea.Caret.Line = 11;
            Pump(editor);
            CheckBox box = FindCheckBox(editor.TextArea.TextView);
            DocumentLine taskLine = editor.Document.GetLineByNumber(9);
            int taskOffset = taskLine.Offset + 3;
            int caretBefore = editor.TextArea.Caret.Offset;
            char taskBefore = editor.Document.GetCharAt(taskOffset);
            bool clickRaised = false;
            if (box != null) {
                var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left);
                // Real input tunnels PreviewMouseDown; WPF re-raises it as the direct
                // PreviewMouseLeftButtonDown on each element of the route (incl. TextArea).
                args.RoutedEvent = UIElement.PreviewMouseDownEvent;
                args.Source = box;
                box.RaiseEvent(args);
                clickRaised = args.Handled;
                Pump(editor);
            }
            char taskAfter = editor.Document.GetCharAt(taskOffset);
            bool caretSame = editor.TextArea.Caret.Offset == caretBefore;
            bool eFormatted = !session.Raw(taskLine);
            Check(ref all, report, "e checkbox preview click",
                box != null && clickRaised && taskBefore == ' ' && taskAfter == 'x' && caretSame && eFormatted,
                "box=" + (box != null) + ", handled=" + clickRaised + ", '" + taskBefore + "'->'" + taskAfter +
                "', caret=" + caretBefore + "/" + editor.TextArea.Caret.Offset + ", formatted=" + eFormatted);

            // f. Link lookup works from the parsed line, including a raw link line.
            DocumentLine linkLine = editor.Document.GetLineByNumber(11);
            string atLink = session.LinkAt(linkLine.Offset + 1);
            string outside = session.LinkAt(0);
            Check(ref all, report, "f link lookup", atLink == LinkUrl && outside == null,
                "at=" + (atLink == null ? "null" : atLink) + ", outside=" + (outside == null ? "null" : outside));

            // g. A press freezes the reveal set until EndPress applies it once.
            bool gBefore = session.Raw(editor.Document.GetLineByNumber(11));
            session.BeginPress();
            editor.TextArea.Caret.Line = 12;
            bool gFrozen = session.Raw(editor.Document.GetLineByNumber(11)) && !session.Raw(editor.Document.GetLineByNumber(12));
            session.EndPress();
            bool gApplied = !session.Raw(editor.Document.GetLineByNumber(11)) && session.Raw(editor.Document.GetLineByNumber(12));
            Pump(editor);
            Check(ref all, report, "g press freeze", gBefore && gFrozen && gApplied,
                "before=" + gBefore + ", frozen=" + gFrozen + ", applied=" + gApplied);

            // h. <font> tags follow the basic view (MDNote.cs:83,89-95): the tags are hidden
            // and colour / font-size apply; a tag with no readable colour keeps the text
            // unstyled. The basic parse of the same text is the reference.
            string fontText = "plain <font color=\"#C03030\">red words</font> tail\n\n" +
                "<font face=\"Arial\">face text</font> and <font color=\"blue\" style=\"font-size:22px\">blue big</font>\n";
            editor.Text = fontText;
            editor.TextArea.Caret.Line = 1;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            FlowDocument basicFont = Markdown.Parse(fontText, 16, "Malgun Gothic", Brushes.Black, false, "");
            Run basicRed = FindRun(basicFont, "red words"), basicFace = FindRun(basicFont, "face text"), basicBlue = FindRun(basicFont, "blue big");
            DocumentLine fontLine = editor.Document.GetLineByNumber(1), secondFontLine = editor.Document.GetLineByNumber(3);
            MarkdownLivePreview.LineInfo fontInfo = session.Info(fontLine), secondFontInfo = session.Info(secondFontLine);
            string firstText = editor.Document.GetText(fontLine);
            int open = firstText.IndexOf("<font", StringComparison.Ordinal);
            int openEnd = firstText.IndexOf('>', open) + 1;
            int redStart = firstText.IndexOf("red words", StringComparison.Ordinal);
            int close = firstText.IndexOf("</font>", StringComparison.Ordinal);
            bool hiddenTags = HasHidden(fontInfo, open, openEnd - open) && HasHidden(fontInfo, close, 7);
            StyledBrush red = StyleAt(fontInfo, redStart);
            string secondText = editor.Document.GetText(secondFontLine);
            int faceStart = secondText.IndexOf("face text", StringComparison.Ordinal);
            int blueStart = secondText.IndexOf("blue big", StringComparison.Ordinal);
            StyledBrush face = StyleAt(secondFontInfo, faceStart), blue = StyleAt(secondFontInfo, blueStart);
            bool hColour = basicRed != null && red.Color != null && basicRed.Foreground is SolidColorBrush &&
                SameColor(red.Color, (SolidColorBrush)basicRed.Foreground) &&
                blue.Color != null && basicBlue != null && basicBlue.Foreground is SolidColorBrush &&
                SameColor(blue.Color, (SolidColorBrush)basicBlue.Foreground);
            bool hFace = basicFace != null && face.Color == null;
            bool hSize = basicBlue != null && Math.Abs(basicBlue.FontSize - 22) < .01 && Math.Abs(blue.Size - 22) < .01;
            editor.TextArea.Caret.Line = 3;
            Pump(editor);
            VisualLine fontVisual = editor.TextArea.TextView.GetVisualLine(1);
            string fontGlyphs = fontVisual == null ? "" : GlyphText(fontVisual);
            bool hGlyphs = fontGlyphs == "plain red words tail";
            bool hInk = ElementColour(fontVisual, redStart) != null && SameColor(ElementColour(fontVisual, redStart), (SolidColorBrush)basicRed.Foreground);
            Check(ref all, report, "h font tags hide and colour text",
                hiddenTags && hColour && hFace && hSize && hGlyphs && hInk,
                "hidden=" + hiddenTags + ", colour=" + hColour + ", unstyled=" + hFace + ", size=" + hSize +
                ", glyphs=" + fontGlyphs + ", ink=" + hInk);

            // i. Setext headings: the text line takes the heading scale, the underline line
            // folds while the caret is elsewhere and is revealed on the caret line. A "---"
            // after a blank line stays a rule, a table delimiter row is untouched, and the
            // underline of an underline line is a rule (the basic view's order, MDNote.cs:51).
            editor.Text = "Title\n===\n\nSubtitle\n---\n\nBody text\n\nRule above\n\n---\n\n" +
                "| A | B |\n| --- | --- |\n| 1 | 2 |\n\nend\n\na\n===\n---\n";
            editor.TextArea.Caret.Line = 7;
            Pump(editor);
            MarkdownLivePreview.LineInfo title = session.Info(editor.Document.GetLineByNumber(1));
            MarkdownLivePreview.LineInfo titleUnderline = session.Info(editor.Document.GetLineByNumber(2));
            MarkdownLivePreview.LineInfo subtitle = session.Info(editor.Document.GetLineByNumber(4));
            MarkdownLivePreview.LineInfo subtitleUnderline = session.Info(editor.Document.GetLineByNumber(5));
            MarkdownLivePreview.LineInfo body = session.Info(editor.Document.GetLineByNumber(7));
            MarkdownLivePreview.LineInfo ruleAbove = session.Info(editor.Document.GetLineByNumber(9));
            MarkdownLivePreview.LineInfo rule = session.Info(editor.Document.GetLineByNumber(11));
            MarkdownLivePreview.LineInfo tableHeader = session.Info(editor.Document.GetLineByNumber(13));
            MarkdownLivePreview.LineInfo tableDelimiter = session.Info(editor.Document.GetLineByNumber(14));
            MarkdownLivePreview.LineInfo doubleText = session.Info(editor.Document.GetLineByNumber(19));
            MarkdownLivePreview.LineInfo doubleUnderline = session.Info(editor.Document.GetLineByNumber(20));
            MarkdownLivePreview.LineInfo doubleRule = session.Info(editor.Document.GetLineByNumber(21));
            bool iParse = title.Setext && title.Heading == 1 && titleUnderline.SetextUnderline == 1 &&
                subtitle.Setext && subtitle.Heading == 2 && subtitleUnderline.SetextUnderline == 2 &&
                !body.Setext && body.Heading == 0;
            bool iRules = rule.Rule && rule.SetextUnderline == 0 && !ruleAbove.Setext &&
                tableDelimiter.Delimiter && tableHeader.Header && !tableHeader.Setext &&
                doubleText.Heading == 1 && doubleUnderline.SetextUnderline == 1 && doubleRule.Rule && doubleRule.SetextUnderline == 0;
            VisualLine titleVisual = editor.TextArea.TextView.GetVisualLine(1);
            VisualLine bodyVisual = editor.TextArea.TextView.GetVisualLine(7);
            bool iFolded = !OwnVisualLine(editor.TextArea.TextView, 2) &&
                titleVisual != null && bodyVisual != null && !GlyphText(titleVisual).Contains("===");
            bool iScale = titleVisual != null && bodyVisual != null && titleVisual.Height >= bodyVisual.Height * 1.8;
            editor.TextArea.Caret.Line = 2;
            Pump(editor);
            VisualLine revealed = editor.TextArea.TextView.GetVisualLine(2);
            bool iReveal = OwnVisualLine(editor.TextArea.TextView, 2) && revealed != null && GlyphText(revealed).Contains("===");
            editor.TextArea.Caret.Line = 7;
            Pump(editor);
            bool iFoldAgain = !OwnVisualLine(editor.TextArea.TextView, 2);
            // Typing the underline under a line turns it into H1 and deleting it reverts;
            // the heading above must re-render both times (neighbour invalidation).
            editor.Text = "Title\nBody\n";
            editor.TextArea.Caret.Line = 1;
            Pump(editor);
            DocumentLine typed = editor.Document.GetLineByNumber(1);
            double before = editor.TextArea.TextView.GetVisualLine(1) == null ? 0 : editor.TextArea.TextView.GetVisualLine(1).Height;
            editor.Document.Insert(typed.EndOffset, "\n===");
            Pump(editor);
            MarkdownLivePreview.LineInfo typedTitle = session.Info(editor.Document.GetLineByNumber(1));
            VisualLine typedVisual = editor.TextArea.TextView.GetVisualLine(1);
            bool iTyped = typedTitle.Setext && typedTitle.Heading == 1 && typedVisual != null && typedVisual.Height > before + 8 &&
                !OwnVisualLine(editor.TextArea.TextView, 2);
            editor.Document.Remove(typed.EndOffset, 4);
            Pump(editor);
            MarkdownLivePreview.LineInfo reverted = session.Info(editor.Document.GetLineByNumber(1));
            VisualLine shrunk = editor.TextArea.TextView.GetVisualLine(1);
            bool iReverted = !reverted.Setext && reverted.Heading == 0 && shrunk != null && Math.Abs(shrunk.Height - before) < .6;
            Check(ref all, report, "i setext heading parse", iParse && iRules,
                "parse=" + iParse + " rules=" + iRules + " h1=" + title.Heading + "/" + titleUnderline.SetextUnderline +
                " h2=" + subtitle.Heading + "/" + subtitleUnderline.SetextUnderline + " rule=" + rule.Rule +
                " double=" + doubleText.Heading + "/" + doubleUnderline.SetextUnderline + "/" + doubleRule.Rule);
            Check(ref all, report, "i setext underline folds and reveals", iFolded && iReveal && iFoldAgain && iScale,
                "folded=" + iFolded + " reveal=" + iReveal + " again=" + iFoldAgain + " scale=" + iScale);
            Check(ref all, report, "i setext typing toggles", iTyped && iReverted,
                "typed=" + iTyped + " reverted=" + iReverted + " heights " + N(before) + "/" +
                N(typedVisual == null ? 0 : typedVisual.Height) + "/" + N(shrunk == null ? 0 : shrunk.Height));

            // j. The underline can be the last line (no trailing newline); a malformed font
            // tag stays literal instead of crashing or hiding text.
            editor.Text = "Last line\n===";
            editor.TextArea.Caret.Line = 1;
            Pump(editor);
            MarkdownLivePreview.LineInfo lastTitle = session.Info(editor.Document.GetLineByNumber(1));
            MarkdownLivePreview.LineInfo lastUnderline = session.Info(editor.Document.GetLineByNumber(2));
            bool jLast = editor.Document.LineCount == 2 && lastTitle.Setext && lastTitle.Heading == 1 &&
                lastUnderline.SetextUnderline == 1 && !OwnVisualLine(editor.TextArea.TextView, 2);
            editor.Text = "open <font color=\"red\">never closed\n\nlone </font\n";
            editor.TextArea.Caret.Line = 3;
            Pump(editor);
            MarkdownLivePreview.LineInfo openTag = session.Info(editor.Document.GetLineByNumber(1));
            MarkdownLivePreview.LineInfo loneClose = session.Info(editor.Document.GetLineByNumber(3));
            bool jLiteral = openTag.Styles.Count == 0 && openTag.Replacements.Count == 0 &&
                loneClose.Styles.Count == 0 && loneClose.Replacements.Count == 0 &&
                GlyphText(editor.TextArea.TextView.GetVisualLine(1)).Contains("<font");
            Check(ref all, report, "j setext last line and malformed tags", jLast && jLiteral,
                "last=" + jLast + " literal=" + jLiteral + " lines=" + editor.Document.LineCount);

            // k. A full-width space (U+3000, the Japanese/Chinese IME's space) is text, not a
            // marker separator: only an ASCII space or tab makes a list, heading or rule, the
            // rule the structural keys already read. Basic (every line formatted) and Live (the
            // caret line as source) share this parser, so both settings must parse the same,
            // and MD-to-TXT and the outline must read the lines the screen draws.
            editor.Text = "-　a\n1.　a\n#　a\n---　\n- a\n>　a\n　\n";
            editor.TextArea.Caret.Line = 1;
            editor.TextArea.Caret.Column = 1;
            settings.HideMarkers = true;   // Basic
            session.Update(settings);
            Pump(editor);
            bool kBasic = FullWidthSpaceParse(session, editor.Document);
            settings.HideMarkers = false;  // Live
            session.Update(settings);
            Pump(editor);
            bool kLive = FullWidthSpaceParse(session, editor.Document);
            bool kTxt = MarkdownPlainText.Convert("-　a") == "-　a" && MarkdownPlainText.Convert("1.　a") == "1.　a" &&
                MarkdownPlainText.Convert("#　a") == "#　a" && MarkdownPlainText.Convert("---　") == "---　" &&
                MarkdownPlainText.Convert("- a") == "- a" && MarkdownPlainText.Convert(">　a") == "　a" &&
                MarkdownPlainText.Convert("# a") == "a" && MarkdownPlainText.Convert("> a") == "a" &&
                MarkdownPlainText.Convert("　") == "　";
            editor.Text = "　# 제목\n#　제목\n# 제목\n";
            Pump(editor);
            int kHeadings;
            string kTitle;
            OutlineHeadings(editor, out kHeadings, out kTitle);
            bool kOutline = kHeadings == 1 && kTitle == "제목";
            Check(ref all, report, "k full-width space keeps lines paragraphs (basic and live)", kBasic && kLive,
                "basic=" + kBasic + ", live=" + kLive);
            Check(ref all, report, "k full-width space MD to TXT", kTxt,
                "list=" + MarkdownPlainText.Convert("-　a") + ", quote=" + MarkdownPlainText.Convert(">　a"));
            Check(ref all, report, "k full-width space outline", kOutline,
                "headings=" + kHeadings + ", title=" + (kTitle == null ? "null" : kTitle));

            // l. CommonMark block and inline rules on the rendered line, in both settings: the
            // heading's closing "#" run and named entities are markup, "_ _ _" is a rule, a lazy
            // quote line keeps the quote, and a link's balanced-paren target plus a shortcut
            // reference resolve (the same rules the print and TXT renderers read).
            editor.Text = "## 제목 ##\n### C#\n\n&copy; &mdash; &hellip;\n\n_ _ _\n\n> 인용\n이어지는 줄\n\n" +
                "[위키](https://example.invalid/a_(b))\n[문서] 참고\n\n[문서]: https://example.invalid/doc\n끝\n";
            editor.TextArea.Caret.Line = 15;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            // One more frame: the reference-definition scan is queued by the parse (never run
            // inside it) and lands here before the shortcut link is read.
            Pump(editor);
            bool lBasic = false, lLive = false;
            foreach (bool hide in new[] { true, false }) {
                settings.HideMarkers = hide;
                session.Update(settings);
                Pump(editor);
                MarkdownLivePreview.LineInfo common1 = session.Info(editor.Document.GetLineByNumber(1));
                MarkdownLivePreview.LineInfo common2 = session.Info(editor.Document.GetLineByNumber(2));
                MarkdownLivePreview.LineInfo common4 = session.Info(editor.Document.GetLineByNumber(4));
                MarkdownLivePreview.LineInfo common6 = session.Info(editor.Document.GetLineByNumber(6));
                MarkdownLivePreview.LineInfo common9 = session.Info(editor.Document.GetLineByNumber(9));
                string glyph1 = GlyphText(editor.TextArea.TextView.GetVisualLine(1));
                string glyph2 = GlyphText(editor.TextArea.TextView.GetVisualLine(2));
                // An entity is drawn as an embedded formatted-text object, so the WPF glyph runs
                // do not contain its label; read the visible text from the line parse and keep the
                // glyph read for the markup half (the raw "&copy;" must not be drawn as text).
                string glyph4 = GlyphText(editor.TextArea.TextView.GetVisualLine(4));
                string visible4 = VisibleLine(common4);
                string glyph9 = GlyphText(editor.TextArea.TextView.GetVisualLine(9));
                string glyph11 = GlyphText(editor.TextArea.TextView.GetVisualLine(11));
                string glyph12 = GlyphText(editor.TextArea.TextView.GetVisualLine(12));
                bool ok = common1.Heading == 2 && glyph1 == "제목" &&
                    common2.Heading == 3 && glyph2 == "C#" &&
                    editor.Document.GetText(editor.Document.GetLineByNumber(4)).IndexOf("&copy;", StringComparison.Ordinal) >= 0 &&
                    visible4 == "© — …" && glyph4.IndexOf("&", StringComparison.Ordinal) < 0 &&
                    common6.Rule && glyph9 == "이어지는 줄" && common9.Quote && common9.LazyQuote &&
                    glyph11 == "위키" && session.LinkAt(editor.Document.GetLineByNumber(11).Offset + 1) == "https://example.invalid/a_(b)" &&
                    glyph12 == "문서 참고" && session.LinkAt(editor.Document.GetLineByNumber(12).Offset + 2) == "ref:문서";
                if (hide) lBasic = ok; else lLive = ok;
            }
            settings.HideMarkers = false;
            session.Update(settings);
            Pump(editor);
            Check(ref all, report, "l CommonMark blocks and links (basic and live)", lBasic && lLive,
                "basic=" + lBasic + ", live=" + lLive);

            // m. An ATX heading may be indented at most three columns (the basic view's
            // MarkdownPlainText.AtxHeading): four spaces make ordinary paragraph text, and inside
            // a quote the spaces after the ">" are what count. A list item's heading is still
            // judged after its own marker.
            string mText = "   # three\n    # four\n>   # quoted three\n>     # quoted four\n- ### item heading\n";
            editor.Text = mText;
            editor.TextArea.Caret.Line = 5;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            string[] mPlain = { "   # three", "    # four" };
            int[] mExpected = { 1, 0 };
            bool mPlainOk = true;
            string mPlainDetail = "";
            for (int mi = 0; mi < mPlain.Length; mi++) {
                MarkdownLivePreview.LineInfo mLineInfo = session.Info(editor.Document.GetLineByNumber(mi + 1));
                int mBasicLevel; string mBasicBody;
                int mOracle = MarkdownPlainText.AtxHeading(mPlain[mi], out mBasicLevel, out mBasicBody) ? mBasicLevel : 0;
                bool mOk = mLineInfo.Heading == mExpected[mi] && mLineInfo.Heading == mOracle;
                if (!mOk) mPlainOk = false;
                mPlainDetail += " " + mi + ":" + mLineInfo.Heading + "/" + mOracle;
            }
            MarkdownLivePreview.LineInfo mQuoted = session.Info(editor.Document.GetLineByNumber(3));
            MarkdownLivePreview.LineInfo mQuotedFour = session.Info(editor.Document.GetLineByNumber(4));
            MarkdownLivePreview.LineInfo mItem = session.Info(editor.Document.GetLineByNumber(5));
            bool mRest = mQuoted.Heading == 1 && mQuotedFour.Heading == 0 && mItem.Heading == 3;
            string mGlyphs = GlyphText(editor.TextArea.TextView.GetVisualLine(1)) + "|" +
                GlyphText(editor.TextArea.TextView.GetVisualLine(2)) + "|" +
                GlyphText(editor.TextArea.TextView.GetVisualLine(3)) + "|" +
                GlyphText(editor.TextArea.TextView.GetVisualLine(4));
            bool mDraw = GlyphText(editor.TextArea.TextView.GetVisualLine(1)).Contains("three") &&
                GlyphText(editor.TextArea.TextView.GetVisualLine(2)).Contains("# four") &&
                GlyphText(editor.TextArea.TextView.GetVisualLine(3)).Contains("quoted three") &&
                GlyphText(editor.TextArea.TextView.GetVisualLine(4)).Contains("# quoted four");
            Check(ref all, report, "m ATX heading indentation matches the basic rule", mPlainOk && mRest && mDraw,
                "plain=" + mPlainDetail + " rest=" + mRest + " glyphs=" + mGlyphs);

            // n. A title is not part of a target: the quoted ("url \"t\"") and the parenthesized
            // ("url (t)") forms both leave the destination alone (the basic view's TargetParts),
            // for links and images alike. An angle autolink hides its brackets and opens the URL.
            editor.Text = "[설계서](https://example.invalid/spec (3장))\n[따옴표](https://example.invalid/x \"t\")\n" +
                "![샷](shot.png (실행 화면))\n<https://example.invalid/a_(b)>\n";
            editor.TextArea.Caret.Line = 1;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            DocumentLine nParen = editor.Document.GetLineByNumber(1);
            DocumentLine nQuote = editor.Document.GetLineByNumber(2);
            DocumentLine nImageLine = editor.Document.GetLineByNumber(3);
            DocumentLine nAngle = editor.Document.GetLineByNumber(4);
            MarkdownLivePreview.LineInfo nImageInfo = session.Info(nImageLine);
            MarkdownLivePreview.LineInfo nAngleInfo = session.Info(nAngle);
            string nImageSource = null;
            foreach (MarkdownLivePreview.Replacement nReplacement in nImageInfo.Replacements)
                if (nReplacement.Kind == MarkdownLivePreview.ReplacementKind.Image) nImageSource = nReplacement.Source;
            string nAngleGlyphs = GlyphText(editor.TextArea.TextView.GetVisualLine(4));
            bool nOk = session.LinkAt(nParen.Offset + 1) == "https://example.invalid/spec" &&
                session.LinkAt(nQuote.Offset + 1) == "https://example.invalid/x" &&
                nImageSource == "shot.png" &&
                session.LinkAt(nAngle.Offset + 1) == "https://example.invalid/a_(b)" &&
                HasHidden(nAngleInfo, 0, 1) && HasHidden(nAngleInfo, nAngle.Length - 1, 1) &&
                nAngleGlyphs == "https://example.invalid/a_(b)";
            Check(ref all, report, "n link and image titles plus angle autolink", nOk,
                "urls=" + session.LinkAt(nParen.Offset + 1) + " / " + session.LinkAt(nQuote.Offset + 1) +
                " image=" + (nImageSource == null ? "null" : nImageSource) +
                " angle=" + session.LinkAt(nAngle.Offset + 1) + " glyphs=" + nAngleGlyphs);

            // o. Small inline rules: a code span's one-space padding is stripped (UnpadCode), a
            // backslash at the line's end is a hard break and draws nothing, and an HTML comment
            // that closes on the same line is markup (GitHub hides it).
            editor.Text = "` code ` and `code`\n첫 줄\\\n둘째 줄\nx <!-- note --> y\n끝\n";
            editor.TextArea.Caret.Line = 5;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            MarkdownLivePreview.LineInfo oCode = session.Info(editor.Document.GetLineByNumber(1));
            string oGlyph1 = GlyphText(editor.TextArea.TextView.GetVisualLine(1));
            string oGlyph2 = GlyphText(editor.TextArea.TextView.GetVisualLine(2));
            string oGlyph4 = GlyphText(editor.TextArea.TextView.GetVisualLine(4));
            bool oOk = HasHidden(oCode, 1, 1) && HasHidden(oCode, 6, 1) && oGlyph1 == "code and code" &&
                oGlyph2 == "첫 줄" && oGlyph4.Replace(" ", "") == "xy" &&
                oGlyph4.IndexOf("note", StringComparison.Ordinal) < 0;
            Check(ref all, report, "o code span padding, trailing backslash, HTML comment", oOk,
                "code='" + oGlyph1 + "' backslash='" + oGlyph2 + "' comment='" + oGlyph4 + "'");

            // p. The delimiter row is structure for input too: a commit aimed at it lands in the
            // next row's first cell, and typing over a selection that starts on it changes
            // nothing (the same rule Delete, Cut and Paste read).
            editor.Text = "| a | b |\n| --- | --- |\n| c | d |\n";
            editor.TextArea.Caret.Line = 3;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            DocumentLine pDelimiter = editor.Document.GetLineByNumber(2);
            DocumentLine pRow = editor.Document.GetLineByNumber(3);
            int pClamped = editor.CellClamp == null ? -1 : editor.CellClamp(pDelimiter.Offset + 3);
            editor.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(editor.TextArea, pDelimiter.Offset, pRow.Offset + 3);
            editor.TextArea.Caret.Offset = pRow.Offset + 3;
            Pump(editor);
            bool pTouches = session.SelectionTouchesDelimiter();
            string pError = null;
            try { editor.TextArea.PerformTextInput("x"); } catch (Exception ex) { pError = ex.GetType().Name; }
            Pump(editor);
            bool pKept = pError == null && editor.Document.Text == "| a | b |\n| --- | --- |\n| c | d |\n";
            editor.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(editor.TextArea, pDelimiter.Offset + 3, pDelimiter.Offset + 3);
            editor.TextArea.Caret.Offset = pDelimiter.Offset + 3;
            Pump(editor);
            try { editor.TextArea.PerformTextInput("y"); } catch (Exception ex) { pError = ex.GetType().Name; }
            Pump(editor);
            bool pCaretKept = pError == null && editor.Document.Text == "| a | b |\n| --- | --- |\n| c | d |\n";
            Check(ref all, report, "p delimiter row keeps commits and caret input out",
                pClamped == pRow.Offset + 2 && pTouches && pKept && pCaretKept,
                "clamp=" + pClamped + "/" + (pRow.Offset + 2) + " touches=" + pTouches +
                " kept=" + pKept + "/" + pCaretKept + " error=" + pError);

            // q. CommonMark list numbering and nesting on the rendered line, the same rule the
            // print and TXT renderers read (bughunt3 rendering items 10 and 11): a blank line
            // between items keeps one list, so three "1." items separated by blank lines draw
            // 1, 2, 3; and a child marker must reach the parent item's content column (3 for
            // "1. "), so a two-space bullet is a sibling top-level list drawn at the same text x
            // and only a three-space one nests.
            editor.Text = "1. 하나\n\n1. 둘\n\n1. 셋\n\n끝\n";
            editor.TextArea.Caret.Line = 7;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            string qLabels = ListLabel(session, editor.Document.GetLineByNumber(1)) + "|" +
                ListLabel(session, editor.Document.GetLineByNumber(3)) + "|" +
                ListLabel(session, editor.Document.GetLineByNumber(5));
            bool qBlank = qLabels == "1.|2.|3.";
            editor.Text = "1. 첫 항목\n  - 얕은 항목\n- 기준\n끝\n";
            editor.TextArea.Caret.Line = 4;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            MarkdownLivePreview.LineInfo qOrdered = session.Info(editor.Document.GetLineByNumber(1));
            MarkdownLivePreview.LineInfo qShallow = session.Info(editor.Document.GetLineByNumber(2));
            VisualLine qShallowLine = editor.TextArea.TextView.GetVisualLine(2);
            VisualLine qSiblingLine = editor.TextArea.TextView.GetVisualLine(3);
            double qShallowX = qShallowLine == null ? -1 : CellX(qShallowLine, 4);
            double qSiblingX = qSiblingLine == null ? -2 : CellX(qSiblingLine, 2);
            bool qSibling = qOrdered.List != null && qOrdered.List.Ordered && qOrdered.List.Depth == 0 &&
                qShallow.List != null && !qShallow.List.Ordered && qShallow.List.Depth == 0 && qShallow.List.Indent == 2 &&
                Math.Abs(qShallowX - qSiblingX) < .6;
            editor.Text = "1. 첫 항목\n   - 깊은 항목\n끝\n";
            editor.TextArea.Caret.Line = 3;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            MarkdownLivePreview.LineInfo qDeep = session.Info(editor.Document.GetLineByNumber(2));
            bool qNest = qDeep.List != null && !qDeep.List.Ordered && qDeep.List.Depth == 1;
            Check(ref all, report, "q list numbering across blank lines and content-column nesting", qBlank && qSibling && qNest,
                "labels=" + qLabels + " sibling=" + qSibling + " x=" + N(qShallowX) + "/" + N(qSiblingX) + " nest=" + qNest);

            // r. The live "#anchor" click rule (MainWindow.OpenLiveAnchor): the fragment lands on the
            // heading line it names - ATX, setext, list and quote headings alike - and a fragment
            // that names nothing resolves to -1, which is the click's "move nothing". The scan picks
            // candidates by their raw text, so the view's parse still decides: a heading inside a
            // fence is not a target, and of two headings with the same text the first one wins.
            editor.Text = "# 첫째 제목\n\n본문\n\n## 둘째 제목\n\n셋째 제목\n===\n\n- ### 항목 제목\n\n" +
                "> # 인용 제목\n\n```\n# 코드 제목\n```\n\n끝\n\n## 첫째 제목\n";
            editor.TextArea.Caret.Line = 18;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);
            DocumentLine anchorFirst = editor.Document.GetLineByNumber(1);
            DocumentLine anchorSecond = editor.Document.GetLineByNumber(5);
            DocumentLine anchorSetext = editor.Document.GetLineByNumber(7);
            DocumentLine anchorList = editor.Document.GetLineByNumber(10);
            DocumentLine anchorQuote = editor.Document.GetLineByNumber(12);
            int anchorCaretBefore = editor.TextArea.Caret.Offset;
            int anchorFenced = MainWindow.FindLiveAnchorOffset(editor.Document, session, AnchorRule.Target("#코드-제목"));
            if (anchorFenced >= 0) MainWindow.JumpLiveAnchor(editor, anchorFenced);
            Pump(editor);
            int anchorMissing = MainWindow.FindLiveAnchorOffset(editor.Document, session, AnchorRule.Target("#없는-제목"));
            if (anchorMissing >= 0) MainWindow.JumpLiveAnchor(editor, anchorMissing);
            Pump(editor);
            bool anchorStill = editor.TextArea.Caret.Offset == anchorCaretBefore;
            Check(ref all, report, "r missing anchor moves nothing", anchorFenced == -1 && anchorMissing == -1 && anchorStill,
                "fenced=" + anchorFenced + ", missing=" + anchorMissing + ", caret " + anchorCaretBefore + "/" + editor.TextArea.Caret.Offset);
            int anchorDuplicate = MainWindow.FindLiveAnchorOffset(editor.Document, session, AnchorRule.Target("#첫째-제목"));
            int anchorSetextFound = MainWindow.FindLiveAnchorOffset(editor.Document, session, AnchorRule.Target("#셋째-제목"));
            int anchorListFound = MainWindow.FindLiveAnchorOffset(editor.Document, session, AnchorRule.Target("#항목-제목"));
            int anchorQuoteFound = MainWindow.FindLiveAnchorOffset(editor.Document, session, AnchorRule.Target("#인용-제목"));
            int anchorJump = MainWindow.FindLiveAnchorOffset(editor.Document, session, AnchorRule.Target("#둘째-제목"));
            if (anchorJump >= 0) MainWindow.JumpLiveAnchor(editor, anchorJump);
            Pump(editor);
            bool anchorLanded = anchorJump == anchorSecond.Offset && editor.TextArea.Caret.Line == 5 &&
                editor.TextArea.Caret.Offset == anchorSecond.Offset;
            Check(ref all, report, "r anchor jump lands on the heading",
                anchorDuplicate == anchorFirst.Offset && anchorJump == anchorSecond.Offset && anchorLanded &&
                anchorSetextFound == anchorSetext.Offset && anchorListFound == anchorList.Offset && anchorQuoteFound == anchorQuote.Offset,
                "first=" + anchorDuplicate + "/" + anchorFirst.Offset + " second=" + anchorJump + "/" + anchorSecond.Offset +
                " setext=" + anchorSetextFound + "/" + anchorSetext.Offset + " list=" + anchorListFound + "/" + anchorList.Offset +
                " quote=" + anchorQuoteFound + "/" + anchorQuote.Offset + " caret line=" + editor.TextArea.Caret.Line);
        } catch (Exception ex) {
            exit = 2;
            all = false;
            report.Append("FAIL exception ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            report.Append(ex.StackTrace).Append('\n');
        } finally {
            if (session != null) session.Dispose();
            if (window != null) window.Close();
            if (exit == 0 && !all) exit = 1;
            report.Append("\nResult: ").Append(exit == 0 ? "PASS" : "FAIL").Append('\n');
            try {
                File.WriteAllText(Path.Combine(dir, "livepreview-probe.txt"), report.ToString(), new UTF8Encoding(false));
                string pass = Path.Combine(dir, "livepreview-probe-PASS.txt");
                if (exit == 0) File.WriteAllText(pass, "PASS\n", new UTF8Encoding(false));
                else if (File.Exists(pass)) File.Delete(pass);
            } catch (Exception) {
                exit = 2;
            }
            if (ownApp && app != null) {
                try { app.Shutdown(); }
                catch (InvalidOperationException) { }
            }
        }
        Trace(report.ToString());
        return exit;
    }

    static void Trace(string text) {
        try { System.Diagnostics.Trace.WriteLine(text); }
        catch (Exception) { }
        try { Console.WriteLine(text); }
        catch (Exception) { }
    }

    static void Pump(SourceEditor editor) {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
        editor.UpdateLayout();
        editor.TextArea.TextView.EnsureVisualLines();
    }

    static double CellX(VisualLine line, int relativeOffset) {
        int column = line.GetVisualColumn(relativeOffset);
        return line.GetVisualPosition(column, VisualYPosition.LineTop).X;
    }

    sealed class StyledBrush {
        internal Brush Color;
        internal double Size;
    }

    // The style that covers a source offset, merging colour and size like the transformer does.
    static StyledBrush StyleAt(MarkdownLivePreview.LineInfo info, int offset) {
        var found = new StyledBrush();
        foreach (MarkdownLivePreview.StyledPart part in info.Styles) {
            if (offset < part.Start || offset >= part.Start + part.Length) continue;
            if (part.Color != null) found.Color = part.Color;
            if (part.Size > 0) found.Size = part.Size;
            else if (part.SizeEm > 0) found.Size = 16 * part.SizeEm;
        }
        return found;
    }

    static bool HasHidden(MarkdownLivePreview.LineInfo info, int start, int length) {
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements)
            if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Hidden && replacement.Start == start && replacement.Length == length) return true;
        return false;
    }

    static bool SameColor(Brush a, Brush b) {
        var first = a as SolidColorBrush; var second = b as SolidColorBrush;
        return first != null && second != null && first.Color == second.Color;
    }

    static Brush ElementColour(VisualLine visual, int relativeOffset) {
        if (visual == null) return null;
        foreach (VisualLineElement element in visual.Elements) {
            if (element.RelativeTextOffset != relativeOffset || element.TextRunProperties == null) continue;
            return element.TextRunProperties.ForegroundBrush;
        }
        return null;
    }

    static string GlyphText(VisualLine visual) {
        var text = new StringBuilder();
        if (visual == null) return "";
        foreach (TextLine line in visual.TextLines)
            foreach (IndexedGlyphRun run in line.GetIndexedGlyphRuns())
                if (run.GlyphRun.Characters != null) text.Append(new string(new List<char>(run.GlyphRun.Characters).ToArray()));
        return text.ToString();
    }

    // The text the renderer draws for a line, from its parse: hidden runs are source markup and
    // vanish, a Glyph replacement (a decoded entity) shows its label, a <br> is a line break and
    // a table cell gap is one space (the walk Session.VisibleText uses). An entity label is an
    // embedded formatted-text object drawn by its element, so the glyph runs do not contain it.
    static string VisibleLine(MarkdownLivePreview.LineInfo info) {
        string value = info.Text;
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

    // A visual line that starts at this document line. A folded underline has none of its
    // own: it joins the heading line above (the same merge the table delimiter uses).
    static bool OwnVisualLine(TextView view, int lineNumber) {
        VisualLine line = view.GetVisualLine(lineNumber);
        return line != null && line.FirstDocumentLine.LineNumber == lineNumber;
    }

    // The k check's text: lines 1-4 are paragraphs ("-　a", "1.　a", "#　a", "---　" keep a
    // full-width space as content), line 5 is a list with an ASCII space, line 6 a quote whose
    // ">" alone is hidden, leaving "　a" as its content, and line 7 a line of just U+3000: that
    // is content, not a blank line, so right under the quote line it continues that quote lazily
    // (a blank line is what would end the quote).
    static bool FullWidthSpaceParse(MarkdownLivePreview.Session session, TextDocument document) {
        return ParagraphLine(session, document.GetLineByNumber(1)) && ParagraphLine(session, document.GetLineByNumber(2)) &&
            ParagraphLine(session, document.GetLineByNumber(3)) && ParagraphLine(session, document.GetLineByNumber(4)) &&
            ListItemLine(session, document.GetLineByNumber(5)) && QuoteFullWidthLine(session, document.GetLineByNumber(6)) &&
            LazyFullWidthLine(session, document.GetLineByNumber(7));
    }

    // A line of just U+3000 is content, so under a quote paragraph it is a lazy continuation:
    // not a paragraph of its own, and not a blank line that would close the quote.
    static bool LazyFullWidthLine(MarkdownLivePreview.Session session, DocumentLine line) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        return !info.Code && !info.FenceLine && info.Quote && info.LazyQuote && info.Heading == 0 && !info.Setext &&
            info.SetextUnderline == 0 && !info.Rule && !info.Delimiter && !info.Header && !info.Table && info.List == null;
    }

    // A line the renderer draws as plain paragraph text: no heading, rule, table, quote or list.
    static bool ParagraphLine(MarkdownLivePreview.Session session, DocumentLine line) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        return !info.Code && !info.FenceLine && !info.Quote && info.Heading == 0 && !info.Setext &&
            info.SetextUnderline == 0 && !info.Rule && !info.Delimiter && !info.Header && !info.Table && info.List == null;
    }

    static bool ListItemLine(MarkdownLivePreview.Session session, DocumentLine line) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        return !info.Code && info.Heading == 0 && info.List != null &&
            info.ListStart == 0 && info.ListEnd == 2 && info.ListBody == 2;
    }

    // The number the live view draws on an ordered list line: the label of its Number
    // replacement ("2."), or "" when the line is not an ordered item.
    static string ListLabel(MarkdownLivePreview.Session session, DocumentLine line) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        if (info.List == null || !info.List.Ordered) return "";
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements)
            if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Number) return replacement.Label == null ? "" : replacement.Label;
        return info.List.Number.ToString(CultureInfo.InvariantCulture) + ".";
    }

    // ">　a": the quote marker is the line's only hidden prefix (one character), so the
    // full-width space is the quote's first content character and shows as "　a".
    static bool QuoteFullWidthLine(MarkdownLivePreview.Session session, DocumentLine line) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        if (!info.Quote || info.QuoteLevels != 1 || info.Replacements.Count != 1) return false;
        MarkdownLivePreview.Replacement prefix = info.Replacements[0];
        return prefix.Start == 0 && prefix.Length == 1 && prefix.Prefix &&
            prefix.Kind == MarkdownLivePreview.ReplacementKind.Spacer;
    }

    // The source outline's own scan (a private MainWindow static), called through reflection so
    // the probe reads the heading rule the panel uses without opening a window.
    static void OutlineHeadings(SourceEditor editor, out int count, out string title) {
        count = 0;
        title = null;
        MethodInfo scan = typeof(MainWindow).GetMethod("ScanSourceOutline", BindingFlags.Static | BindingFlags.NonPublic, null,
            new Type[] { typeof(ITextSource), typeof(System.Threading.CancellationToken) }, null);
        if (scan == null) throw new MissingMemberException("probe: MainWindow has no method 'ScanSourceOutline'");
        object roots = scan.Invoke(null, new object[] { editor.Document.CreateSnapshot(), System.Threading.CancellationToken.None });
        foreach (object node in (System.Collections.IEnumerable)roots) {
            count++;
            PropertyInfo titleProperty = node.GetType().GetProperty("Title");
            if (titleProperty != null) title = (string)titleProperty.GetValue(node, null);
        }
    }

    static Run FindRun(FlowDocument document, string needle) {
        foreach (Block block in document.Blocks) {
            var paragraph = block as Paragraph;
            if (paragraph == null) continue;
            Run run = FindRun(paragraph.Inlines, needle);
            if (run != null) return run;
        }
        return null;
    }

    static Run FindRun(InlineCollection inlines, string needle) {
        foreach (Inline inline in inlines) {
            var run = inline as Run;
            if (run != null && run.Text.IndexOf(needle, StringComparison.Ordinal) >= 0) return run;
            var span = inline as Span;
            if (span != null) {
                Run found = FindRun(span.Inlines, needle);
                if (found != null) return found;
            }
        }
        return null;
    }

    static CheckBox FindCheckBox(DependencyObject root) {
        int count;
        try { count = VisualTreeHelper.GetChildrenCount(root); }
        catch (InvalidOperationException) { return null; }
        for (int i = 0; i < count; i++) {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            CheckBox box = child as CheckBox;
            if (box != null && box.Tag is MarkdownLivePreview.Session.TaskTag) return box;
            CheckBox nested = FindCheckBox(child);
            if (nested != null) return nested;
        }
        return null;
    }

    static void Check(ref bool all, StringBuilder report, string name, bool ok, string detail) {
        report.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (detail != null) report.Append(": ").Append(detail);
        report.Append('\n');
        if (!ok) all = false;
    }

    static string N(double value) {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}

// Stage 2/3 of the live-as-basic audit (원문 비공개 편집). With Settings.HideMarkers on, the
// caret must never rest inside a hidden marker, Home/End must not stop before a hidden line
// prefix, a double-click word must not contain markup characters, and a hidden marker deletes
// as one unit. Every count is measured twice: with the setting OFF (today's reveal rule) and
// ON, so the report shows what changed. A FAIL here is the measurement, not a crash.
internal static class NoRevealProbe {
    const string DocumentText =
        "# Heading one\n" +
        "## Heading two\n" +
        "plain **bold** and *italic* and ~~strike~~ and `code` here\n" +
        "<span style=\"color:#C03030\">red span</span> and <font color=\"blue\">blue font</font> tail\n" +
        "> quoted **bold** text\n" +
        "> > nested quote\n" +
        "- bullet item\n" +
        "- [ ] task item\n" +
        "- [x] done item\n" +
        "1. numbered item\n" +
        "[t](https://example.invalid/x) and [ref][id] and [^n] note\n" +
        "![alt](missing.png)\n" +
        "line one<br>line two\n" +
        "| alpha | beta |\n" +
        "| --- | --- |\n" +
        "| one | two |\n" +
        "after table\n" +
        "---\n" +
        "[id]: https://example.invalid/def\n" +
        "[^n]: note text\n";

    // Markup characters. The generated document above contains them only as markup, so a
    // word range that holds one came from a marker and not from the text.
    const string MarkupChars = "*#>|";

    internal sealed class Tally {
        internal int SeekStops, SeekInside, WalkStops, WalkInside, BeforePrefix;
        internal int WordRanges, WordMarked, Skipped;
        internal readonly List<string> Samples = new List<string>();
        internal void Note(string text) { if (Samples.Count < 10) Samples.Add(text); }
        internal string Line() {
            return "seek stops=" + SeekStops + " inside=" + SeekInside + ", walk stops=" + WalkStops +
                " inside=" + WalkInside + ", before prefix=" + BeforePrefix + ", words=" + WordRanges +
                " with markup=" + WordMarked + ", skipped lines=" + Skipped;
        }
    }

    internal static int Run(string outDir) {
        string dir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(dir);
        var report = new StringBuilder();
        report.Append("# NoRevealProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        bool all = true;
        int exit = 0;
        Application app = null;
        bool ownApp = false;
        SourceEditor editor = null;
        MarkdownLivePreview.Session session = null;
        Window window = null;
        try {
            app = Application.Current;
            if (app == null) {
                app = new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
                ownApp = true;
            }
            editor = new SourceEditor {
                FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true,
                Background=Brushes.White, Foreground=Brushes.Black
            };
            editor.TextArea.TextView.LineHeight = double.NaN;
            editor.Text = DocumentText;
            var settings = new MarkdownLivePreview.Settings {
                FontSize=16, Family="Malgun Gothic", LinePercent=150, Dark=false,
                WordWrap=true, BaseDirectory=dir
            };
            session = MarkdownLivePreview.Attach(editor, settings);
            window = new Window {
                Left=-20000, Top=0, Width=900, Height=1100, ShowInTaskbar=false,
                ShowActivated=false, WindowStyle=WindowStyle.None, Content=editor
            };
            window.Show();
            Pump(editor);
            editor.TextArea.Caret.Line = 1;
            editor.TextArea.Caret.Column = 1;
            Pump(editor);

            // Baseline: the setting off, i.e. the caret line is revealed and its markers are
            // ordinary text characters the caret can sit between.
            Tally off = Measure(editor, session);
            report.Append("- OFF ").Append(off.Line()).Append('\n');
            AppendSamples(report, off);

            // The setting on: every line stays formatted.
            settings.HideMarkers = true;
            session.Update(settings);
            Pump(editor);
            Tally on = Measure(editor, session);
            report.Append("- ON  ").Append(on.Line()).Append('\n');
            AppendSamples(report, on);

            // The probe must be able to see the condition it forbids: with the setting off the
            // marker characters are reachable, so a zero there would mean nothing was measured.
            Check(ref all, report, "off baseline reaches marker interiors", off.SeekInside > 0 || off.WalkInside > 0,
                off.Line());
            Check(ref all, report, "on: no seek stop inside a hidden marker", on.SeekInside == 0, on.Line());
            Check(ref all, report, "on: no key walk stop inside a hidden marker", on.WalkInside == 0, on.Line());
            Check(ref all, report, "on: Home/End never before a hidden prefix", on.BeforePrefix == 0, on.Line());
            Check(ref all, report, "on: double-click words carry no markup", on.WordMarked == 0,
                "checked=" + on.WordRanges + " marked=" + on.WordMarked);

            // C1b-overlap-2: a hidden run draws zero width, so its two visual columns name one
            // spot. The caret after the leading ** is before 굵; four Rights are 굵, 게, space, 기,
            // and the hidden * between the space and 기 must not cost a fifth press. The two
            // Lefts back test the mirror: one visible character per press, the hidden * skipped.
            editor.Text = "**굵게 *기울임* 굵게**\n";
            editor.Select(2, 0);
            Pump(editor);
            for (int step = 0; step < 4; step++) Press(editor, EditingCommands.MoveRightByCharacter);
            int afterItalic = editor.CaretOffset;
            Press(editor, EditingCommands.MoveLeftByCharacter);
            int beforeItalic = editor.CaretOffset;
            Press(editor, EditingCommands.MoveLeftByCharacter);
            int beforeSpace = editor.CaretOffset;
            Check(ref all, report, "basic caret: Right x4 crosses a hidden opener", afterItalic == 7,
                "caret=" + afterItalic + " expected 7 (after 기), text='" + Flat(editor.Text) + "'");
            // Left crosses one visible character too: the second press passes the hidden opener
            // and the space it sits on, so it reaches 4 (before the space). The key path adds that
            // step (the session's HiddenRunLeft key handler); the engine's own stops are
            // unchanged, so the host's caret snapping still sees the run's start column.
            Check(ref all, report, "basic caret: Left from after the italic's first syllable",
                beforeItalic == 6 && beforeSpace == 4,
                "caret=" + beforeItalic + " expected 6, then " + beforeSpace + " expected 4, text='" + Flat(editor.Text) + "'");
            // Shift+Right shares that movement path, so the selection also extends by visible
            // characters: four presses cover 굵, 게, space and 기 (the hidden * is inside the range
            // but is not a stop the key spends a press on).
            editor.Select(2, 0);
            Pump(editor);
            for (int step = 0; step < 4; step++) Press(editor, EditingCommands.SelectRightByCharacter);
            Check(ref all, report, "basic caret: Shift+Right extends by visible characters",
                editor.SelectionStart == 2 && editor.SelectionLength == 5,
                "selection=" + editor.SelectionStart + "," + editor.SelectionLength + " expected 2,5, text='" + Flat(editor.Text) + "'");
            // Shift+Left follows the same rule from the same positions: from offset 7 two presses
            // cover 기 and the space, with the hidden * inside the range (selection 4..7).
            editor.Select(7, 0);
            Pump(editor);
            Press(editor, EditingCommands.SelectLeftByCharacter);
            Press(editor, EditingCommands.SelectLeftByCharacter);
            Check(ref all, report, "basic caret: Shift+Left extends by visible characters",
                editor.SelectionStart == 4 && editor.SelectionLength == 3,
                "selection=" + editor.SelectionStart + "," + editor.SelectionLength + " expected 4,3, text='" + Flat(editor.Text) + "'");
            editor.Select(0, 0);
            Pump(editor);

            // The preference is the only way in (there is no menu item yet): a default file is
            // off and writes no attribute, a saved one round-trips.
            var defaults = new ViewPreferences();
            string defaultXml = defaults.Serialize();
            string savedXml = new ViewPreferences { HideMarkers=true }.Serialize();
            string settingsPath = Path.Combine(dir, "noreveal-settings.xml");
            File.WriteAllText(settingsPath, savedXml, new UTF8Encoding(false));
            ViewPreferences reloaded = ViewPreferences.Load(settingsPath);
            Check(ref all, report, "preference liveHideMarkers default off and saved",
                !defaults.HideMarkers && defaultXml.IndexOf("liveHideMarkers", StringComparison.Ordinal) < 0 &&
                savedXml.IndexOf("liveHideMarkers=\"true\"", StringComparison.Ordinal) >= 0 && reloaded.HideMarkers,
                "default=" + defaults.HideMarkers + " saved=" + reloaded.HideMarkers);

            // The reader's old 8192-character limit reset every setting when ten long recent paths
            // pushed the file past it; the limit is now 65536 and a stored path is capped at 520
            // characters, so a long recent list can no longer grow the file past the reader.
            var longRecent = new List<string>();
            for (int i = 0; i < 10; i++) longRecent.Add(Path.Combine(dir, new string('r', 760) + i + ".md"));
            string capXml = new ViewPreferences { Recent = longRecent }.Serialize();
            Check(ref all, report, "settings drop a recent path too long to store",
                capXml.IndexOf("Recent", StringComparison.Ordinal) < 0 && capXml.Length < 65536,
                "recent tags=" + (capXml.IndexOf("Recent", StringComparison.Ordinal) >= 0) + " length=" + capXml.Length);
            var builder = new StringBuilder("<MDNote font=\"Consolas\" size=\"20\" dark=\"true\" linePercent=\"150\" customLinePercent=\"175\" language=\"ko\" liveMode=\"true\">");
            for (int i = 0; i < 10; i++) builder.Append("<Recent>").Append(Path.Combine(dir, new string('q', 900) + i + ".md")).Append("</Recent>");
            builder.Append("</MDNote>");
            string bigPath = Path.Combine(dir, "big-recent-settings.xml");
            File.WriteAllText(bigPath, builder.ToString(), new UTF8Encoding(false));
            bool unreadable;
            ViewPreferences big = ViewPreferences.Load(bigPath, out unreadable);
            Check(ref all, report, "settings beyond the old 8192 limit still load the other values",
                builder.Length > 8192 && !unreadable && big.Dark && big.Family == "Consolas" && big.Size == 20 && big.Recent.Count == 0,
                "xml=" + builder.Length + " unreadable=" + unreadable + " dark=" + big.Dark + " font=" + big.Family + " size=" + big.Size + " recent=" + big.Recent.Count);
            string brokenPath = Path.Combine(dir, "broken-settings.xml");
            File.WriteAllText(brokenPath, "<MDNote", new UTF8Encoding(false));
            ViewPreferences broken = ViewPreferences.Load(brokenPath, out unreadable);
            Check(ref all, report, "an unparsable settings file is reported so it is kept as a .bak",
                unreadable && broken.Family == "Malgun Gothic" && broken.Size == 16 && !broken.Dark,
                "unreadable=" + unreadable + " font=" + broken.Family + " size=" + broken.Size);

            // A table cell draws its picture as one object position of the cell text: the row
            // stays formatted, the picture is scaled to the cell width (never wider than it),
            // and the "![alt](src)" source behind it is untouched. An image that cannot be
            // decoded keeps the source text, so the alt text is still there to be read.
            string cellImage = Path.Combine(dir, "gaps-cell.png");
            WriteProbePng(cellImage, 96, 64);
            string cellText = "| left | right |\n| --- | --- |\n| one | ![shot](gaps-cell.png) |\ntail\n";
            editor.Text = cellText;
            editor.TextArea.Caret.Line = 4;
            PumpImages(editor);
            DocumentLine cellRowLine = editor.Document.GetLineByNumber(3);
            VisualLine cellVisual = editor.TextArea.TextView.GetVisualLine(3);
            MarkdownLivePreview.TableRowElement cellRow = RowElement(cellVisual);
            bool aFormatted = cellRow != null && !session.Raw(cellRowLine);
            bool aCellImage = cellRow != null && cellRow.CellHasImage(1);
            double cellRoom = cellRow == null || cellRow.Row.Widths.Length < 2 ? 0 : cellRow.Row.Widths[1] - cellRow.Row.Padding * 2;
            double drawnWidth = MarkdownLivePreview.DiagImageWidth, drawnHeight = MarkdownLivePreview.DiagImageHeight;
            bool aPinned = drawnWidth > 1 && drawnWidth <= cellRoom + 0.5 && drawnHeight > 1;
            bool aTall = cellVisual != null && cellVisual.Height > 40;
            bool aText = editor.Document.Text == cellText;
            Check(ref all, report, "A table cell image draws inside its cell",
                aFormatted && aCellImage && aPinned && aTall && aText,
                "formatted=" + aFormatted + ", image=" + aCellImage + ", drawn=" + Num(drawnWidth) + "x" + Num(drawnHeight) +
                ", room=" + Num(cellRoom) + ", row height=" + Num(cellVisual == null ? 0 : cellVisual.Height) + ", text=" + aText);
            // The caret row follows the same reveal rule as the rest of its markup: with the
            // no-reveal mode on the cell keeps its picture, and with the setting off the row
            // is raw and shows the source instead.
            editor.TextArea.Caret.Line = 3;
            Pump(editor);
            VisualLine caretVisual = editor.TextArea.TextView.GetVisualLine(3);
            MarkdownLivePreview.TableRowElement caretRow = RowElement(caretVisual);
            bool aCaretNoReveal = !session.Raw(editor.Document.GetLineByNumber(3)) && caretRow != null &&
                caretRow.CellHasImage(1) && editor.Document.Text == cellText;
            settings.HideMarkers = false;
            session.Update(settings);
            editor.TextArea.Caret.Line = 3;
            Pump(editor);
            VisualLine rawVisual = editor.TextArea.TextView.GetVisualLine(3);
            MarkdownLivePreview.TableRowElement rawRow = RowElement(rawVisual);
            string rawGlyphs = rawVisual == null ? "-" : Glyphs(rawVisual);
            bool aCaretRaw = session.Raw(editor.Document.GetLineByNumber(3)) && rawRow != null && !rawRow.CellHasImage(1) &&
                rawGlyphs.IndexOf("![shot]", StringComparison.Ordinal) >= 0 && editor.Document.Text == cellText;
            settings.HideMarkers = true;
            session.Update(settings);
            Pump(editor);
            Check(ref all, report, "A caret row follows the reveal rule for the cell image", aCaretNoReveal && aCaretRaw,
                "no-reveal=" + aCaretNoReveal + ", raw=" + aCaretRaw + ", glyphs=" + rawGlyphs);
            string missingCell = "| left | right |\n| --- | --- |\n| one | ![gone](nope.png) |\ntail\n";
            editor.Text = missingCell;
            editor.TextArea.Caret.Line = 4;
            Pump(editor);
            VisualLine missingVisual = editor.TextArea.TextView.GetVisualLine(3);
            MarkdownLivePreview.TableRowElement missingRow = RowElement(missingVisual);
            string missingGlyphs = missingVisual == null ? "-" : Glyphs(missingVisual);
            bool aMissing = missingRow != null && !missingRow.CellHasImage(1) &&
                missingGlyphs.IndexOf("![gone]", StringComparison.Ordinal) >= 0 && editor.Document.Text == missingCell;
            Check(ref all, report, "A table cell image that fails keeps the alt source", aMissing,
                "row=" + (missingRow != null) + ", glyphs=" + missingGlyphs);

            // A reference definition line is the basic view's hidden line: with the no-reveal
            // mode on and the caret elsewhere it takes no height and draws nothing, and the
            // caret line brings the grey source back. The setting off keeps it as it was.
            string refText = "# title\n\nsee [k][id] here\n\n[id]: https://example.invalid/def \"t\"\n\nafter\n";
            editor.Text = refText;
            editor.TextArea.Caret.Line = 1;
            Pump(editor);
            Pump(editor);
            MarkdownLivePreview.LineInfo refInfo = session.Info(editor.Document.GetLineByNumber(5));
            VisualLine refVisual = editor.TextArea.TextView.GetVisualLine(5);
            double refHiddenHeight = refVisual == null ? -1 : refVisual.Height;
            string refHiddenGlyphs = Glyphs(refVisual);
            bool refOwnLine = OwnVisualLine(editor.TextArea.TextView, 5);
            bool bHidden = refInfo.RefDefinition && !refOwnLine &&
                refHiddenGlyphs.IndexOf("example.invalid/def", StringComparison.Ordinal) < 0;
            editor.TextArea.Caret.Line = 5;
            Pump(editor);
            VisualLine refShown = editor.TextArea.TextView.GetVisualLine(5);
            double refShownHeight = refShown == null ? -1 : refShown.Height;
            string refShownGlyphs = Glyphs(refShown);
            bool bShown = OwnVisualLine(editor.TextArea.TextView, 5) && refShown != null && refShownHeight > 5 &&
                refShownGlyphs.IndexOf("example.invalid/def", StringComparison.Ordinal) >= 0;
            settings.HideMarkers = false;
            session.Update(settings);
            editor.TextArea.Caret.Line = 1;
            Pump(editor);
            VisualLine refPlain = editor.TextArea.TextView.GetVisualLine(5);
            bool bSettingOff = refPlain != null && refPlain.Height > 5 && OwnVisualLine(editor.TextArea.TextView, 5) &&
                Glyphs(refPlain).IndexOf("example.invalid/def", StringComparison.Ordinal) >= 0;
            settings.HideMarkers = true;
            session.Update(settings);
            Pump(editor);
            Check(ref all, report, "B reference definition line folds unless the caret is on it",
                bHidden && bShown && bSettingOff && editor.Document.Text == refText,
                "hidden=" + bHidden + "(" + (refVisual == null ? "null" : refVisual.FirstDocumentLine.LineNumber + ".." +
                refVisual.LastDocumentLine.LineNumber) + ", " + Num(refHiddenHeight) + ", '" + refHiddenGlyphs + "') shown=" + bShown +
                "(" + Num(refShownHeight) + ") off=" + bSettingOff + " text=" + (editor.Document.Text == refText));

            // Stage 3d: an Alt+drag rectangle selection would need every line's own segment
            // widened to stay whole, so rectangle selection is turned off while markers are
            // hidden and comes back with the setting off.
            settings.HideMarkers = false;
            session.Update(settings);
            Pump(editor);
            bool rectangleOffSetting = editor.Options.EnableRectangularSelection;
            settings.HideMarkers = true;
            session.Update(settings);
            Pump(editor);
            Check(ref all, report, "rectangle selection stays on with the setting off and off with it on",
                rectangleOffSetting && !editor.Options.EnableRectangularSelection,
                "off=" + rectangleOffSetting + " on=" + editor.Options.EnableRectangularSelection);

            // A rectangle made while the setting was off becomes an ordinary selection, and its
            // two endpoints then follow the same widening rule as any other selection.
            settings.HideMarkers = false;
            session.Update(settings);
            Pump(editor);
            editor.Text = "a **bold** b\nc **two** d";
            Pump(editor);
            editor.ScrollTo(1, 0);
            Pump(editor);
            // Reveal both lines first: the rectangle's positions are visual columns, and only
            // the caret/selection lines carry their markers as real characters with the
            // setting off. A whole-document selection keeps them laid out that way.
            editor.Select(0, editor.Document.TextLength);
            Pump(editor);
            int rectStart = -1, rectEnd = -1;
            try {
                // Both endpoints sit inside the second `*` of an opening `**` pair: line 1
                // widens 3 -> 2, line 2 widens 16 -> 17 (the marker's end).
                editor.TextArea.Selection = new ICSharpCode.AvalonEdit.Editing.RectangleSelection(editor.TextArea,
                    new ICSharpCode.AvalonEdit.TextViewPosition(1, 4), new ICSharpCode.AvalonEdit.TextViewPosition(2, 4));
                ISegment segment = editor.TextArea.Selection.SurroundingSegment;
                if (segment != null) { rectStart = segment.Offset; rectEnd = segment.EndOffset; }
            } catch (Exception ex) { report.Append("FAIL rectangle construction threw ").Append(ex.GetType().Name).Append('\n'); all = false; }
            settings.HideMarkers = true;
            session.Update(settings);
            Pump(editor);
            Check(ref all, report, "rectangle selection becomes a plain widened selection",
                !(editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) &&
                editor.SelectionStart == 2 && editor.SelectionLength == 15,
                "type=" + editor.TextArea.Selection.GetType().Name + " start=" + editor.SelectionStart + " length=" + editor.SelectionLength +
                " rect=" + rectStart + ".." + rectEnd);

            // Unselected emphasis/code markers are transparent to character deletion; a
            // structural marker/tag/url still deletes as one unit. A selection that only
            // partly covers a marker is widened before it deletes, and typing over it takes
            // the marked pair whole so no orphan marker is left as text.
            EditCase(ref all, report, editor, session, "backspace after opening ** deletes visible space", "a **bold** b", 4, KeyAction.Backspace, "a**bold** b");
            EditCase(ref all, report, editor, session, "delete before closing ** deletes visible space", "a **bold** b", 8, KeyAction.Delete, "a **bold**b");
            HiddenBoundaryCases(ref all, report, editor, session);
            EditCase(ref all, report, editor, session, "backspace after ## un-formats", "## title", 3, KeyAction.Backspace, "title");
            EditCase(ref all, report, editor, session, "backspace after > removes the prefix", "> quoted", 2, KeyAction.Backspace, "quoted");
            EditCase(ref all, report, editor, session, "backspace after <br> removes the tag", "x<br>y", 5, KeyAction.Backspace, "xy");
            EditCase(ref all, report, editor, session, "backspace after an image removes it whole", "![alt](missing.png)", 18, KeyAction.Backspace, "");
            EditCase(ref all, report, editor, session, "backspace after a link target un-links", "[t](https://x)", 14, KeyAction.Backspace, "t");
            EditCase(ref all, report, editor, session, "partial marker selection takes the whole pair", "a **bold** b", -8, KeyAction.Backspace, "a  b");
            EditCase(ref all, report, editor, session, "backspace inside text stays one char", "a **bold** b", 6, KeyAction.Backspace, "a **bld** b");
            EditCase(ref all, report, editor, session, "typing over a partial marker selection", "a **bold** b", -8, KeyAction.Type, "a X b");
            EditCase(ref all, report, editor, session, "typing inside styled text replaces one char", "a **bold** b", -4, KeyAction.Type, "a **Xold** b");

            // b55: with the markers hidden, the caret at the text start of a line whose text opens
            // with an inline pair ("- **item**") draws on the same spot as the structural prefix's
            // end. A Backspace there is the line's own marker rule, not the pair's: the bullet, the
            // heading mark, the quote level or the task box goes and the text keeps its bold. Both
            // offsets that draw on that spot are the same case - the prefix's end and the far side
            // of the opening marker - and a plain line is the reference result.
            string[] leadLines = {
                "- **목록굵게** 항목", "2", "4", "**목록굵게** 항목",
                "# **제목**", "2", "4", "**제목**",
                "> **인용**", "2", "4", "**인용**",
                "- [ ] **할 일**", "6", "8", "**할 일**",
                "- 항목", "2", "2", "항목",
                "# 제목", "2", "2", "제목",
                "> 인용", "2", "2", "인용",
                "- [ ] 할 일", "6", "6", "할 일"
            };
            for (int i = 0; i < leadLines.Length; i += 4) {
                string leadText = leadLines[i];
                int leadEnd = Int32.Parse(leadLines[i + 1], CultureInfo.InvariantCulture);
                int afterMarker = Int32.Parse(leadLines[i + 2], CultureInfo.InvariantCulture);
                EditCase(ref all, report, editor, session, "backspace at the text start of '" + leadText + "'",
                    leadText, leadEnd, KeyAction.Backspace, leadLines[i + 3]);
                if (afterMarker != leadEnd) {
                    EditCase(ref all, report, editor, session, "backspace past the opening marker of '" + leadText + "'",
                        leadText, afterMarker, KeyAction.Backspace, leadLines[i + 3]);
                }
            }
            // The hidden-run rule is what the hidden markers are: with them shown they are real
            // characters, so the same caret offset takes the character it sits after.
            settings.HideMarkers = false;
            session.Update(settings);
            Pump(editor);
            EditCase(ref all, report, editor, session, "backspace past a shown ** takes one character",
                "- **abc**", 4, KeyAction.Backspace, "- *abc**");
            settings.HideMarkers = true;
            session.Update(settings);
            Pump(editor);

            // Deleting the whole inner text of an emphasis, strike or code pair takes the pair
            // with it (the no-reveal view would otherwise draw a bare "****"), in one undo step.
            // A typed replacement over the same selection keeps the pair.
            PairShellCase(ref all, report, editor, "backspace over the whole inner text of ** removes the pair",
                "본문 **굵게** 글자", 5, 2, KeyAction.Backspace, "본문  글자");
            PairShellCase(ref all, report, editor, "delete over the whole inner text of ** removes the pair",
                "본문 **굵게** 글자", 5, 2, KeyAction.Delete, "본문  글자");
            PairShellCase(ref all, report, editor, "backspace over the whole inner text of ~~ removes the pair",
                "a ~~지움~~ b", 4, 2, KeyAction.Backspace, "a  b");
            PairShellCase(ref all, report, editor, "delete over the whole inner text of `` removes the pair",
                "x `코드` y", 3, 2, KeyAction.Delete, "x  y");
            PairShellCase(ref all, report, editor, "typing over the whole inner text keeps the ** pair",
                "본문 **굵게** 글자", 5, 2, KeyAction.Type, "본문 **X** 글자");
            PairShellCase(ref all, report, editor, "typing over the whole inner text keeps the `` pair",
                "x `코드` y", 3, 2, KeyAction.Type, "x `X` y");
            WordMarkerCases(ref all, report, editor, session);

            // Stage 3d: a multi-line paste follows the single-line rule. Both endpoints that sit
            // inside a marker (or the last line's hidden prefix) are widened, so no half marker
            // survives the edit.
            PasteCase(ref all, report, editor, "multi-line paste over partial markers widens both ends",
                "a **bold** b\nc **two** d", 3, 18, "X", "a X d");
            PasteCase(ref all, report, editor, "multi-line paste ending inside a hidden prefix drops it whole",
                "alpha\n## title", 2, 6, "X", "alXtitle");
            PasteCase(ref all, report, editor, "multi-line paste without markup is unchanged",
                "one\ntwo", 1, 5, "X", "oXo");

            // A caret that sits inside a marker when the setting turns on (the offset was set
            // while markers were visible) would split it; the paste moves to the nearest edge.
            settings.HideMarkers = false;
            session.Update(settings);
            Pump(editor);
            editor.Text = "a **bold** b";
            Pump(editor);
            editor.TextArea.Caret.Offset = 3;
            Pump(editor);
            settings.HideMarkers = true;
            session.Update(settings);
            Pump(editor);
            bool caretInside = editor.CaretOffset == 3;
            bool pasted = SetClipboard("X");
            if (pasted) {
                ApplicationCommands.Paste.Execute(null, editor.TextArea);
                Pump(editor);
            }
            Check(ref all, report, "paste at a caret inside a hidden marker keeps it whole",
                pasted && editor.Document.Text == "a X**bold** b",
                "text='" + editor.Document.Text + "' caret was inside=" + caretInside + " clipboard=" + pasted);

            // Stage 3d regression found by the setting-ON probe run: a table row's leading
            // pipes are not a hidden line prefix, so the caret snap no longer pushes an empty
            // first cell's caret past its own pipe into the next cell.
            editor.Text = "before\n\n| Left heading | Center heading |\n| :--- | :---: |\n| alpha | |\n| | middle |\n\nafter\n";
            Pump(editor);
            editor.ScrollTo(1, 0);
            Pump(editor);
            int emptyCell = -1;
            string emptyDetail = TableCellClick(editor, session, 6, 0, out emptyCell);
            Check(ref all, report, "empty first cell keeps the caret in its cell", emptyCell == 2,
                "offset=" + emptyCell + " " + emptyDetail);

            TableGapCases(ref all, report, editor, session);
            DragMoveCases(ref all, report);
            PasteAndWordCases(ref all, report);

            FixedFindings(ref all, report, editor, session, settings, dir);
            VisibleSearchChecks(ref all, report, dir);
            ReaderUndoChecks(ref all, report);

            report.Append("\nNotes:\n");
            report.Append("- highlight (==text==) is not a live syntax: MarkdownLivePreview has no highlight\n");
            report.Append("  style, so the document uses a <span style=\"color:...\"> span for the same hidden tags.\n");
            report.Append("- lines whose markup covers the whole line (rule, table delimiter, setext underline)\n");
            report.Append("  have no visible character and are left out of the caret walk (").Append(on.Skipped + " lines).\n");
            report.Append("\nRules: a selection follows the single-line widening rule at both ends however many\n");
            report.Append("lines it spans, so a multi-line paste or delete never leaves half a marker or half a\n");
            report.Append("hidden line prefix; a caret inside a marker moves to the nearest edge before a paste;\n");
            report.Append("and Alt+drag rectangle selection is off while markers are hidden (a rectangle made\n");
            report.Append("earlier becomes an ordinary selection that follows the same rule).\n");
        } catch (Exception ex) {
            exit = 2;
            all = false;
            report.Append("FAIL exception ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            report.Append(ex.StackTrace).Append('\n');
        } finally {
            if (session != null) session.Dispose();
            if (window != null) window.Close();
            if (exit == 0 && !all) exit = 1;
            report.Append("\nResult: ").Append(exit == 0 ? "PASS" : "FAIL").Append('\n');
            try {
                File.WriteAllText(Path.Combine(dir, "noreveal-probe.txt"), report.ToString(), new UTF8Encoding(false));
                string pass = Path.Combine(dir, "noreveal-probe-PASS.txt");
                if (exit == 0) File.WriteAllText(pass, "PASS\n", new UTF8Encoding(false));
                else if (File.Exists(pass)) File.Delete(pass);
            } catch (Exception) {
                exit = 2;
            }
            if (ownApp && app != null) {
                try { app.Shutdown(); }
                catch (InvalidOperationException) { }
            }
        }
        Trace(report.ToString());
        return exit;
    }

    // fix13: the first text typed on the empty line directly under a table row (or directly above
    // a header row) writes the blank separator line in the same document update as the text, so
    // the table keeps its shape and one undo takes the newline and the text back together. Input
    // goes through the same TextArea path a key takes; the IME commit runs the editor's simulated
    // composition the way ImeProbe does. NoRevealProbe has no window, so the deltas the window
    // feeds to its history are recorded here to measure the undo step.
    static void TableGapCases(ref bool all, StringBuilder report, SourceEditor editor, MarkdownLivePreview.Session session) {
        report.Append("\n## fix13 table gap typing\n");
        DocumentHistory history = new DocumentHistory();
        bool record = true;
        editor.ContentDelta += delegate(object sender, DocumentChangeEventArgs change) {
            if (record) history.RecordDelta(change.Offset, change.RemovedText.Text, change.InsertedText.Text);
        };
        const string Below = "before\n\n| h1 | h2 |\n| --- | --- |\n| c1 | c2 |\n\nafter\n";
        const string BelowTyped = "before\n\n| h1 | h2 |\n| --- | --- |\n| c1 | c2 |\n\nk\nafter\n";
        const string Above = "before\n\n\n| h1 | h2 |\n| --- | --- |\n| c1 | c2 |\ntail\n";
        const string AboveTyped = "before\n\nk\n\n| h1 | h2 |\n| --- | --- |\n| c1 | c2 |\ntail\n";

        editor.Text = Below; Pump(editor);
        int blank = editor.Document.GetLineByNumber(6).Offset;
        editor.Select(blank, 0); Pump(editor);
        history.Clear();
        editor.TextArea.PerformTextInput("k"); Pump(editor);
        Check(ref all, report, "typing under a table keeps one blank line",
            editor.Document.Text == BelowTyped && editor.CaretOffset == blank + 2,
            "text=[" + Flat(editor.Document.Text) + "] caret=" + editor.CaretOffset + "/" + (blank + 2));
        record = false;
        bool undone = history.ApplySource(false, editor);
        record = true;
        Pump(editor);
        Check(ref all, report, "one undo takes back the newline and the typed text",
            undone && editor.Document.Text == Below, "undone=" + undone + " text=[" + Flat(editor.Document.Text) + "]");

        editor.Text = Above; Pump(editor);
        int aboveLine = editor.Document.GetLineByNumber(3).Offset;
        editor.Select(aboveLine, 0); Pump(editor);
        history.Clear();
        editor.TextArea.PerformTextInput("k"); Pump(editor);
        Check(ref all, report, "typing above a table keeps one blank line",
            editor.Document.Text == AboveTyped && editor.CaretOffset == aboveLine + 1,
            "text=[" + Flat(editor.Document.Text) + "] caret=" + editor.CaretOffset + "/" + (aboveLine + 1));

        editor.Text = Below; Pump(editor);
        blank = editor.Document.GetLineByNumber(6).Offset;
        editor.Select(blank, 0); Pump(editor);
        history.Clear();
        editor.SimulateImeCommit(1);
        editor.TextArea.PerformTextInput("한"); Pump(editor);
        editor.ResetCompositionSimulation();
        string imeTyped = BelowTyped.Replace("\nk\n", "\n한\n");
        Check(ref all, report, "an IME commit on that line keeps the blank line",
            editor.Document.Text == imeTyped && editor.CaretOffset == blank + 2 && !editor.ImeBusy,
            "text=[" + Flat(editor.Document.Text) + "] caret=" + editor.CaretOffset + "/" + (blank + 2) + " busy=" + editor.ImeBusy);

        // A cell is ordinary editing: no newline is written anywhere.
        editor.Text = Below; Pump(editor);
        var row = editor.Document.GetLineByNumber(5);
        var rowInfo = session.Info(row);
        editor.Select(row.Offset + rowInfo.Cells[0].Start + 2, 0); Pump(editor);
        history.Clear();
        editor.TextArea.PerformTextInput("x"); Pump(editor);
        Check(ref all, report, "typing inside a cell writes no newline",
            editor.Document.Text == Below.Replace("| c1 |", "| c1x |") && editor.Document.LineCount == 8,
            "text=[" + Flat(editor.Document.Text) + "] lines=" + editor.Document.LineCount);
    }

    const BindingFlags ProbeMembers = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static FieldInfo ProbeRequireField(string name) { var field = typeof(MainWindow).GetField(name, ProbeMembers); if (field == null) throw new MissingMemberException("probe: MainWindow has no field '" + name + "'"); return field; }
    static PropertyInfo ProbeRequireProperty(string name) { var property = typeof(MainWindow).GetProperty(name, ProbeMembers); if (property == null) throw new MissingMemberException("probe: MainWindow has no property '" + name + "'"); return property; }
    static T ProbeField<T>(MainWindow window, string name) { return (T)ProbeRequireField(name).GetValue(window); }
    static void ProbeSet(MainWindow window, string name, object value) { ProbeRequireField(name).SetValue(window, value); }
    static T ProbeProp<T>(MainWindow window, string name) { return (T)ProbeRequireProperty(name).GetValue(window, null); }
    static T ProbeStatic<T>(string name) { return (T)ProbeRequireField(name).GetValue(null); }
    static void ProbeStaticSet(string name, object value) { ProbeRequireField(name).SetValue(null, value); }
    static object ProbeCall(MainWindow window, string name, params object[] args) { var method = typeof(MainWindow).GetMethod(name, ProbeMembers); if (method == null) throw new MissingMemberException("probe: MainWindow has no method '" + name + "'"); return method.Invoke(window, args); }
    static void ProbePump() { LongLineBench.Idle(); Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); LongLineBench.Idle(); }

    // P2 items 3-4: an in-document drag move takes the same hidden-marker rules the paste path
    // applies. The source range is widened to whole pairs (so no "****" is left and the moved
    // block keeps its markers), the drop point is snapped out of a hidden prefix or marker, and a
    // drop in the blank band below the last line still runs the app's one-entry move instead of
    // AvalonEdit's insert-then-delete. Driven on a real MainWindow's edit surface; no input is sent.
    static void DragMoveCases(ref bool all, StringBuilder report) {
        report.Append("\n## internal drag move with hidden markers\n");
        bool oldHide = ProbeStatic<bool>("hideMarkers");
        bool oldDefault = ProbeDefaults.Live;
        MainWindow window = null;
        try {
            ProbeStaticSet("hideMarkers", true);
            ProbeDefaults.Live = true;
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=900, Height=700 };
            window.Show(); window.Left = -20000;
            window.SetText("**메모**\n끝");
            window.SetMode(true);
            ProbePump();
            SourceEditor source = ProbeField<SourceEditor>(window, "source");
            MarkdownLivePreview.Session session = ProbeField<MarkdownLivePreview.Session>(window, "livePreview");
            bool basic = session != null && session.Options.HideMarkers;
            // The visible text of the pair is dragged; both hidden markers travel with it.
            source.Select(2, 2);
            ProbePump();
            source.NoteDragSource();
            bool moved = (bool)ProbeCall(window, "MoveSelectionTo", source.Document.TextLength);
            ProbePump();
            string movedText = source.Document.Text;
            bool movedOk = basic && moved && movedText == "\n끝**메모**" &&
                source.SelectionStart == 2 && source.SelectionLength == 6;
            Check(ref all, report, "drag move of a hidden pair carries its markers", movedOk,
                "basic=" + basic + " moved=" + moved + " text='" + Flat(movedText) + "' sel=" + source.SelectionStart + "/" + source.SelectionLength);
            ProbeCall(window, "Undo"); ProbePump();
            Check(ref all, report, "one undo restores the dragged pair", source.Document.Text == "**메모**\n끝",
                "text='" + Flat(source.Document.Text) + "'");

            // A drop on a list line's left margin names line.Offset, in front of the hidden "- ";
            // the snapped drop point belongs after the prefix so the list stays a list.
            window.SetText("- 항목\n**메모**");
            window.SetMode(true);
            ProbePump();
            source.Select(5, 6);
            ProbePump();
            source.NoteDragSource();
            bool snapped = (bool)ProbeCall(window, "MoveSelectionTo", 0);
            ProbePump();
            string snappedText = source.Document.Text;
            Check(ref all, report, "drag drop on a list margin lands after the hidden prefix",
                basic && snapped && snappedText == "- **메모**항목\n",
                "moved=" + snapped + " text='" + Flat(snappedText) + "'");

            // The blank band below the last line has no caret position; the app's move path must
            // still take the drop so one undo restores it (two vendor changes would duplicate).
            window.SetText("**메모**\n끝\n");
            window.SetMode(true);
            ProbePump();
            source.Select(0, 6);
            ProbePump();
            source.NoteDragSource();
            DataObject data = MoveDragData(source, source.SelectedText);
            ICSharpCode.AvalonEdit.Rendering.TextView view = source.TextArea.TextView;
            view.EnsureVisualLines();
            DragEventArgs args = NewDropArgs(data, view, new Point(10, view.ActualHeight + 40),
                DragDropEffects.Move | DragDropEffects.Copy, DragDropKeyStates.LeftMouseButton);
            args.RoutedEvent = DragDrop.PreviewDropEvent;
            args.Source = source.TextArea;
            source.TextArea.RaiseEvent(args);
            ProbePump();
            string belowText = source.Document.Text;
            Check(ref all, report, "drop below the last line moves as the app's single entry",
                basic && args.Handled && belowText == "\n끝\n**메모**",
                "handled=" + args.Handled + " effects=" + args.Effects + " text='" + Flat(belowText) + "'");
            ProbeCall(window, "Undo"); ProbePump();
            Check(ref all, report, "one undo after the below-last-line drop restores the text",
                source.Document.Text == "**메모**\n끝\n", "text='" + Flat(source.Document.Text) + "'");
            ProbeCall(window, "Redo"); ProbePump();
            Check(ref all, report, "one redo after the below-last-line drop reapplies it",
                source.Document.Text == belowText, "text='" + Flat(source.Document.Text) + "'");
        } catch (Exception ex) {
            report.Append("FAIL internal drag move section threw ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            all = false;
        } finally {
            ProbeStaticSet("hideMarkers", oldHide);
            ProbeDefaults.Live = oldDefault;
            if (window != null) {
                try { ProbeSet(window, "documentOperationBusy", false); ProbeSet(window, "saved", ProbeProp<string>(window, "text")); ProbeCall(window, "CaptureTab"); window.Close(); }
                catch (Exception) { }
            }
        }
    }

    // P3 items 5-8. A word step no longer stops on a hidden marker (Ctrl+Left just outside a
    // closing run used to spend a press on the same drawn spot), a formatted paste inside an
    // inline code span stays literal, a multi-line paste into a list item or a quote continues
    // the caret line's prefix (the same continuation Enter writes there), and every paste lands
    // on the document's own line ending. Driven on a real MainWindow's edit surface; no input
    // is sent and the clipboard is never read or written (a DataObject stands in for it).
    static void PasteAndWordCases(ref bool all, StringBuilder report) {
        report.Append("\n## P3 word movement and paste\n");
        bool oldHide = ProbeStatic<bool>("hideMarkers");
        bool oldDefault = ProbeDefaults.Live;
        MainWindow window = null;
        try {
            ProbeStaticSet("hideMarkers", true);
            ProbeDefaults.Live = true;
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=900, Height=700 };
            window.Show(); window.Left = -20000;
            SourceEditor source = null;

            // 5. Ctrl+Left at the end of a bold run: the first press reaches the word start.
            // The closing run draws on the line end's own column, so a stop at its start looked
            // like the caret had not moved.
            window.SetText("**hello world**");
            window.SetMode(true);
            ProbePump();
            source = ProbeField<SourceEditor>(window, "source");
            source.Select(source.Document.TextLength, 0);
            ProbePump();
            EditingCommands.MoveLeftByWord.Execute(null, source.TextArea);
            ProbePump();
            int wordCaret = source.CaretIndex;
            Check(ref all, report, "ctrl+left outside a closing marker moves in one press", wordCaret == 8,
                "caret=" + wordCaret + "/8 text='" + Flat(source.Document.Text) + "'");
            EditingCommands.MoveLeftByWord.Execute(null, source.TextArea);
            ProbePump();
            int wordCaret2 = source.CaretIndex;
            Check(ref all, report, "ctrl+left reaches the previous word start", wordCaret2 == 2,
                "caret=" + wordCaret2 + "/2");

            // 6. A formatted paste inside an inline code span is inserted literally: the span has
            // no inline markup, so a converted "**굵게**" would draw its stars inside the code.
            window.SetText("a `code` b");
            window.SetMode(true);
            ProbePump();
            source.Select(5, 0);
            ProbePump();
            var rich = new DataObject();
            rich.SetData(DataFormats.UnicodeText, "굵게");
            rich.SetData(DataFormats.Html, "<b>굵게</b>");
            ProbeCall(window, "PasteData", rich, false);
            ProbePump();
            string codeText = source.Document.Text;
            Check(ref all, report, "formatted paste inside inline code stays literal", codeText == "a `co굵게de` b",
                "text='" + Flat(codeText) + "'");

            // 7. A multi-line paste into a list item or a quote continues the prefix Enter writes
            // on that line; a pasted line that already carries the prefix keeps its own.
            window.SetText("- 항목");
            window.SetMode(true);
            ProbePump();
            source.Select(source.Document.TextLength, 0);
            ProbePump();
            PastePlainData(window, "둘째\n셋째");
            string listText = source.Document.Text;
            Check(ref all, report, "multi-line paste continues a list prefix", listText == "- 항목둘째\n- 셋째",
                "text='" + Flat(listText) + "'");

            window.SetText("> 인용");
            window.SetMode(true);
            ProbePump();
            source.Select(source.Document.TextLength, 0);
            ProbePump();
            PastePlainData(window, "가\n나");
            string quoteText = source.Document.Text;
            Check(ref all, report, "multi-line paste continues a quote prefix", quoteText == "> 인용가\n> 나",
                "text='" + Flat(quoteText) + "'");

            window.SetText("- [ ] 항목");
            window.SetMode(true);
            ProbePump();
            source.Select(source.Document.TextLength, 0);
            ProbePump();
            PastePlainData(window, "가\n나");
            string taskText = source.Document.Text;
            Check(ref all, report, "multi-line paste continues a task prefix", taskText == "- [ ] 항목가\n- [ ] 나",
                "text='" + Flat(taskText) + "'");

            window.SetText("- 항목");
            window.SetMode(true);
            ProbePump();
            source.Select(source.Document.TextLength, 0);
            ProbePump();
            PastePlainData(window, "a\n- b");
            string keptText = source.Document.Text;
            Check(ref all, report, "a pasted line with its own prefix keeps it", keptText == "- 항목a\n- b",
                "text='" + Flat(keptText) + "'");

            window.SetText("본문");
            window.SetMode(true);
            ProbePump();
            source.Select(source.Document.TextLength, 0);
            ProbePump();
            PastePlainData(window, "가\n나");
            string plainText = source.Document.Text;
            Check(ref all, report, "a plain line gets no continuation prefix", plainText == "본문가\n나",
                "text='" + Flat(plainText) + "'");

            // 8. Ctrl+V of a plain-text clipboard keeps the document's line ending: a CRLF copy
            // into an LF document used to mix the buffer's newlines.
            window.SetText("a");
            ProbeSet(window, "documentNewline", "\n");
            window.SetMode(true);
            ProbePump();
            source.Select(1, 0);
            ProbePump();
            PastePlainData(window, "x\r\ny");
            string lfText = source.Document.Text;
            Check(ref all, report, "paste into an LF document keeps LF", lfText == "ax\ny",
                "text='" + Flat(lfText) + "'");

            window.SetText("a");
            ProbeSet(window, "documentNewline", "\r\n");
            window.SetMode(true);
            ProbePump();
            source.Select(1, 0);
            ProbePump();
            PastePlainData(window, "x\ny");
            string crlfText = source.Document.Text;
            Check(ref all, report, "paste into a CRLF document keeps CRLF", crlfText == "ax\r\ny",
                "text='" + Flat(crlfText) + "'");

            // 9. TXT (plain) mode has no LivePaste, so the clipboard string reaches the buffer
            // through the basic fallback instead. It used to be inserted as-is: a CRLF copy into
            // an LF .txt mixed the buffer's line endings (the save normalized the file, the
            // buffer and its undo offsets did not). Both directions follow the document rule now.
            ProbeSet(window, "textMode", true);
            window.SetText("a");
            ProbeSet(window, "documentNewline", "\n");
            window.SetMode(true);
            ProbePump();
            source.Select(1, 0);
            ProbePump();
            PastePlainData(window, "x\r\ny");
            string txtLfText = source.Document.Text;
            Check(ref all, report, "TXT paste into an LF document keeps LF", txtLfText == "ax\ny",
                "text='" + Flat(txtLfText) + "'");

            window.SetText("a");
            ProbeSet(window, "documentNewline", "\r\n");
            window.SetMode(true);
            ProbePump();
            source.Select(1, 0);
            ProbePump();
            PastePlainData(window, "x\ny");
            string txtCrlfText = source.Document.Text;
            Check(ref all, report, "TXT paste into a CRLF document keeps CRLF", txtCrlfText == "ax\r\ny",
                "text='" + Flat(txtCrlfText) + "'");
        } catch (Exception ex) {
            report.Append("FAIL P3 word and paste section threw ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            all = false;
        } finally {
            ProbeStaticSet("hideMarkers", oldHide);
            ProbeDefaults.Live = oldDefault;
            if (window != null) {
                try { ProbeSet(window, "documentOperationBusy", false); ProbeSet(window, "saved", ProbeProp<string>(window, "text")); ProbeCall(window, "CaptureTab"); window.Close(); }
                catch (Exception) { }
            }
        }
    }

    // A Ctrl+V of a plain-text clipboard: the DataObject carries UnicodeText only, and the
    // window's own paste path runs (the probe never reads or writes the real clipboard).
    static void PastePlainData(MainWindow window, string value) {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, value);
        ProbeCall(window, "PasteData", data, false);
        ProbePump();
    }

    // The internal-move drag data: the dragged text and the token the editor put on its own drag,
    // which is what lets InternalMoveTarget tell this move from an unrelated drop.
    static DataObject MoveDragData(SourceEditor editor, string selected) {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, selected);
        data.SetData(SourceEditor.DragTokenFormat, editor.DragSourceToken);
        return data;
    }

    // A DragEventArgs carrying the given point. WPF keeps the drop point in a private field of the
    // usable constructor form, so it is set through reflection (the same seam DropProbe uses);
    // GetPosition then answers the point the probe placed.
    static DragEventArgs NewDropArgs(IDataObject data, DependencyObject target, Point point, DragDropEffects allowed, DragDropKeyStates keys) {
        ConstructorInfo chosen = null;
        foreach (ConstructorInfo candidate in typeof(DragEventArgs).GetConstructors(ProbeMembers)) {
            ParameterInfo[] parameters = candidate.GetParameters();
            bool dataParameter = false, targetParameter = false, pointParameter = false;
            foreach (ParameterInfo parameter in parameters) {
                if (parameter.ParameterType == typeof(IDataObject)) dataParameter = true;
                else if (parameter.ParameterType == typeof(DependencyObject)) targetParameter = true;
                else if (parameter.ParameterType == typeof(Point)) pointParameter = true;
            }
            if (dataParameter && targetParameter && pointParameter && (chosen == null || parameters.Length > chosen.GetParameters().Length)) chosen = candidate;
        }
        if (chosen == null) throw new MissingMemberException("probe: DragEventArgs constructor");
        ParameterInfo[] form = chosen.GetParameters();
        object[] values = new object[form.Length];
        bool hasPoint = false;
        for (int i = 0; i < form.Length; i++) {
            Type type = form[i].ParameterType;
            if (type == typeof(IDataObject)) values[i] = data;
            else if (type == typeof(DragDropKeyStates)) values[i] = keys;
            else if (type == typeof(DragDropEffects)) values[i] = allowed;
            else if (type == typeof(DependencyObject)) values[i] = target;
            else if (type == typeof(Point)) { values[i] = point; hasPoint = true; }
            else if (type == typeof(bool)) values[i] = false;
            else values[i] = null;
        }
        DragEventArgs args = (DragEventArgs)chosen.Invoke(values);
        if (!hasPoint) {
            FieldInfo pointField = typeof(DragEventArgs).GetField("_dropPoint", ProbeMembers);
            if (pointField == null) foreach (FieldInfo field in typeof(DragEventArgs).GetFields(ProbeMembers)) {
                if (field.FieldType == typeof(Point)) { pointField = field; break; }
            }
            if (pointField == null) throw new MissingFieldException("probe: DragEventArgs drop point");
            pointField.SetValue(args, point);
        }
        return args;
    }

    // Find/Replace must run over the text the reader sees: with the markers hidden a hit is a
    // visible range, and its source range (the markers between the first and last character
    // included) is what the selection, the highlight and Replace use. These checks drive a real
    // window's own search bar; no keyboard or mouse input is sent.
    static void VisibleSearchChecks(ref bool all, StringBuilder report, string dir) {
        report.Append("\n## visible text Find/Replace\n");
        object oldHide = ProbeStatic<bool>("hideMarkers");
        bool oldDefault = ProbeDefaults.Live;
        MainWindow window = null;
        try {
            ProbeStaticSet("hideMarkers", true);
            ProbeDefaults.Live = true;
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=900, Height=700 };
            window.Show(); window.Left = -20000;
            window.SetText("# 제목\n\n한글 **검색** 구간 한글 검색 구간\n\n| 이름 | 값 |\n| --- | --- |\n| 표 셀 | 둘 |\n\n[보일 글자](https://example.invalid/x) 그리고 **Alpha** alpha ALPHA\n\n[id]: https://example.invalid/def\n\ntail paragraph\n");
            window.SetMode(true);
            ProbePump();
            SourceEditor source = ProbeField<SourceEditor>(window, "source");
            TextBox query = ProbeField<TextBox>(window, "query");
            TextBox replacement = ProbeField<TextBox>(window, "replacement");
            TextBlock count = ProbeField<TextBlock>(window, "matchCount");
            TextBlock status = ProbeField<TextBlock>(window, "status");
            CheckBox caseBox = ProbeField<CheckBox>(window, "matchCase");
            MarkdownLivePreview.Session session = ProbeField<MarkdownLivePreview.Session>(window, "livePreview");
            bool raw = session != null && session.Raw(source.Document.GetLineByNumber(3));
            Check(ref all, report, "search runs on a live surface with hidden markers",
                session != null && session.Options.HideMarkers && !session.Options.ReadOnly && !raw,
                "session=" + (session == null ? "-" : "hide=" + session.Options.HideMarkers + ", readOnly=" + session.Options.ReadOnly) + ", raw=" + raw);
            ProbeCall(window, "ShowSearch");
            ProbePump();
            string before = source.Text;

            // The reader sees "한글 검색 구간" twice; the first one is bold in the source. The count
            // follows the visible text and Find selects the source range with the markers inside.
            query.Text = "한글 검색 구간";
            ProbePump();
            bool counted = count.Text.EndsWith(" / 2", StringComparison.Ordinal);
            ProbeCall(window, "Find");
            ProbePump();
            int crossStart = source.SelectionStart;
            string crossText = source.SelectedText;
            bool crossAt = crossStart == source.Document.Text.IndexOf("한글 **검색** 구간", StringComparison.Ordinal);
            Check(ref all, report, "find across an emphasis pair counts and selects the source range",
                counted && count.Text == "1 / 2" && crossText == "한글 **검색** 구간" && crossAt,
                "count=" + count.Text + ", selected='" + crossText + "', start=" + crossStart);

            // Replace takes the formatting of the match's first visible character (the editor's
            // word rule), so a match that spans a marker pair is rewritten, not refused.
            string wordDocument = "한글 **검색** 구간\n";
            replacement.Text = "X";
            string wordPlain = WordReplace(window, source, query, replacement, wordDocument, "한글 검색");
            replacement.Text = "Y";
            string wordStyled = WordReplace(window, source, query, replacement, wordDocument, "검색 구간");
            replacement.Text = "시험";
            string wordInside = WordReplace(window, source, query, replacement, "**테스트예시**\n", "테스트");
            replacement.Text = "숨길";
            string wordLink = WordReplace(window, source, query, replacement, "[보일 글자](https://a.b)\n", "보일");
            Check(ref all, report, "replace keeps the first visible character's formatting",
                wordPlain == "X 구간\n" && wordStyled == "한글 **Y**\n" && wordInside == "**시험예시**\n" && wordLink == "[숨길 글자](https://a.b)\n",
                "plain='" + Flat(wordPlain) + "', styled='" + Flat(wordStyled) + "', inside='" + Flat(wordInside) + "', link='" + Flat(wordLink) + "'");

            // Replace all applies the same rule to every match in one undo step.
            window.SetText("**a** a\n");
            ProbePump();
            query.Text = "a";
            ProbeCall(window, "RefreshSearchNow");
            ProbePump();
            replacement.Text = "x";
            ProbeCall(window, "ReplaceAll");
            string allStatus = status.Text;
            ProbePump();
            string allText = source.Text;
            ProbeCall(window, "Undo");
            ProbePump();
            Check(ref all, report, "replace-all keeps each match's own formatting in one undo step",
                allText == "**x** x\n" && source.Text == "**a** a\n" && allStatus.StartsWith("2", StringComparison.Ordinal),
                "text='" + Flat(allText) + "', status='" + allStatus + "', undo=" + (source.Text == "**a** a\n"));

            // Structure the rewrite cannot touch (a match across a table's cell pipes) is left and
            // counted in the status instead of being half written.
            string tableDocument = "| 가 | 나 |\n| --- | --- |\n";
            window.SetText(tableDocument);
            ProbePump();
            query.Text = "가 나";
            ProbeCall(window, "RefreshSearchNow");
            ProbePump();
            replacement.Text = "X";
            ProbeCall(window, "ReplaceAll");
            string skipStatus = status.Text;
            ProbePump();
            Check(ref all, report, "replace-all leaves a match across table pipes and reports it",
                source.Text == tableDocument && skipStatus.StartsWith("0", StringComparison.Ordinal) && skipStatus.IndexOf("건너뛰", StringComparison.Ordinal) >= 0,
                "status='" + skipStatus + "', text unchanged=" + (source.Text == tableDocument));

            // A table cell is its own source range in the row; the row's pipes are hidden.
            window.SetText(before);
            ProbePump();
            query.Text = "표 셀";
            ProbePump();
            ProbeCall(window, "Find");
            ProbePump();
            Check(ref all, report, "a match inside a table cell finds the cell text",
                count.Text == "1 / 1" && source.SelectedText == "표 셀",
                "count=" + count.Text + ", selected='" + source.SelectedText + "'");

            // Link text is visible; the "](url)" behind it and a folded reference definition are not.
            query.Text = "보일 글자";
            ProbePump();
            ProbeCall(window, "Find");
            ProbePump();
            bool linkFound = count.Text == "1 / 1" && source.SelectedText == "보일 글자";
            query.Text = "example.invalid";
            ProbePump();
            bool hiddenUrl = count.Text.EndsWith(" / 0", StringComparison.Ordinal);
            Check(ref all, report, "link text is searchable and the URL behind it is not", linkFound && hiddenUrl,
                "link='" + (linkFound ? "found" : "missing") + "', url count=" + count.Text);

            query.Text = "alpha";
            ProbePump();
            bool insensitive = count.Text.EndsWith(" / 3", StringComparison.Ordinal);
            caseBox.IsChecked = true;
            ProbePump();
            bool sensitive = count.Text.EndsWith(" / 1", StringComparison.Ordinal);
            caseBox.IsChecked = false;
            ProbePump();
            Check(ref all, report, "case rule follows the visible text", insensitive && sensitive,
                "insensitive(3)=" + insensitive + ", sensitive(1)=" + sensitive);

            // Replace-all over a cross-format query: the first hit keeps its plain first character,
            // the second is plain anyway; one undo step restores the whole body.
            string plainDoc = "한글 **검색** 구간 한글 검색 구간\n";
            window.SetText(plainDoc);
            ProbePump();
            query.Text = "한글 검색 구간";
            ProbeCall(window, "RefreshSearchNow");
            ProbePump();
            replacement.Text = "바뀜";
            ProbeCall(window, "ReplaceAll");
            string crossAllStatus = status.Text;
            ProbePump();
            string afterAll = source.Text;
            ProbeCall(window, "Undo");
            ProbePump();
            Check(ref all, report, "replace-all rewrites every cross-format hit and one undo restores it",
                afterAll == "바뀜 바뀜\n" && crossAllStatus.StartsWith("2", StringComparison.Ordinal) && source.Text == plainDoc,
                "text='" + Flat(afterAll) + "', status='" + crossAllStatus + "', undo=" + (source.Text == plainDoc));

            if (Environment.GetEnvironmentVariable("MDNOTE_SEARCH_BENCH") == "1") MeasureVisibleSearch(report);
        } catch (Exception ex) {
            report.Append("FAIL visible search exception ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            // A reflected call wraps the real failure; the inner one names the broken step.
            for (Exception inner = ex.InnerException; inner != null; inner = inner.InnerException)
                report.Append("  inner ").Append(inner.GetType().Name).Append(": ").Append(inner.Message).Append('\n').Append(inner.StackTrace).Append('\n');
            all = false;
        } finally {
            ProbeStaticSet("hideMarkers", oldHide);
            ProbeDefaults.Live = oldDefault;
            if (window != null) {
                try { ProbeProp<string>(window, "text"); ProbeSet(window, "documentOperationBusy", false); ProbeSet(window, "saved", ProbeProp<string>(window, "text")); ProbeCall(window, "CaptureTab"); window.Close(); }
                catch (Exception) { }
            }
        }
    }

    // The b25 contract on the surface that reads Markdown now: reading mode lets the reader tick a
    // task box, and the window's own undo/redo (what the Keys handler calls for Ctrl+Z / Ctrl+Y)
    // must replay exactly that tick on the live source surface without leaving read mode or
    // touching the rest of the body. A general edit stays out of the read surface's reach.
    static void ReaderUndoChecks(ref bool all, StringBuilder report) {
        report.Append("\n## reading-mode task undo\n");
        object oldHide = ProbeStatic<bool>("hideMarkers");
        bool oldDefault = ProbeDefaults.Live;
        MainWindow window = null;
        try {
            ProbeStaticSet("hideMarkers", true);
            ProbeDefaults.Live = true;
            string body = "# 제목\n\n- [ ] 첫 일\n- [ ] 둘째 일\n\n본문 그대로 남는다\n";
            string ticked = body.Replace("- [ ] 첫 일", "- [x] 첫 일");
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=900, Height=700 };
            window.Show(); window.Left = -20000;
            window.SetText(body);
            window.SetMode(false);
            ProbePump();
            SourceEditor source = ProbeField<SourceEditor>(window, "source");
            MarkdownLivePreview.Session session = ProbeField<MarkdownLivePreview.Session>(window, "livePreview");
            bool reading = session != null && session.Options.ReadOnly && !ProbeField<bool>(window, "editing");
            if (session != null) session.ToggleTask(source.Document.GetLineByNumber(3), 2);
            ProbePump();
            string afterTick = source.Text;
            ProbeCall(window, "Undo");
            ProbePump();
            string afterUndo = source.Text;
            ProbeCall(window, "Redo");
            ProbePump();
            string afterRedo = source.Text;
            Check(ref all, report, "a reader's tick is undone and redone without leaving read mode",
                reading && afterTick == ticked && afterUndo == body && afterRedo == ticked && !ProbeField<bool>(window, "editing"),
                "readOnly=" + reading + ", tick='" + Flat(afterTick) + "', undo='" + Flat(afterUndo) + "', redo='" + Flat(afterRedo) + "', editing=" + ProbeField<bool>(window, "editing"));

            // Undo on the read surface is not a general edit path: a typed entry stays refused there,
            // so the body does not move and the redo stack (cleared by that typing) stays empty.
            window.SetMode(true);
            ProbePump();
            source.Document.Insert(source.Document.GetLineByNumber(1).EndOffset, "메모");
            ProbePump();
            string edited = source.Text;
            window.SetMode(false);
            ProbePump();
            ProbeCall(window, "Undo");
            ProbePump();
            // History is private on the window, so the refused press is read back through it.
            var history = (DocumentHistory)typeof(MainWindow).GetProperty("History", ProbeMembers).GetValue(window, null);
            // CanUndo stays true, so the press was refused rather than reset as a mismatch.
            bool refused = history != null && history.CanUndo && !history.CanRedo;
            Check(ref all, report, "reading-mode undo refuses an ordinary edit",
                !ProbeField<bool>(window, "editing") && edited != afterRedo && source.Text == edited && refused,
                "editing=" + ProbeField<bool>(window, "editing") + ", text='" + Flat(source.Text) + "', canRedo=" + (history != null && history.CanRedo));
        } catch (Exception ex) {
            report.Append("FAIL reading-mode task undo exception ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            all = false;
        } finally {
            ProbeStaticSet("hideMarkers", oldHide);
            ProbeDefaults.Live = oldDefault;
            if (window != null) {
                try { ProbeSet(window, "documentOperationBusy", false); ProbeSet(window, "saved", ProbeProp<string>(window, "text")); ProbeCall(window, "CaptureTab"); window.Close(); }
                catch (Exception) { }
            }
        }
    }

    // One Replace press on a fresh body: sets the text and the query, runs the window's own
    // ReplaceOne and returns the resulting source text.
    static string WordReplace(MainWindow window, SourceEditor source, TextBox query, TextBox replacement, string document, string text) {
        window.SetText(document);
        ProbePump();
        query.Text = text;
        ProbeCall(window, "RefreshSearchNow");
        ProbePump();
        ProbeCall(window, "ReplaceOne");
        ProbePump();
        return source.Text;
    }

    // One-run measurement (the report says "1회"): the visible map is O(document) and is built only
    // when a search asks for it, so this times the first build and then a search over the cache.
    static void MeasureVisibleSearch(StringBuilder report) {
        MainWindow window = null;
        try {
            ProbeStaticSet("hideMarkers", true);
            ProbeDefaults.Live = true;
            string document = LivePreviewBench.GenerateMarkdown(4000000, 77);
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=900, Height=700 };
            window.Show(); window.Left = -20000;
            window.SetText(document);
            window.SetMode(true);
            ProbePump();
            MarkdownLivePreview.Session session = ProbeField<MarkdownLivePreview.Session>(window, "livePreview");
            TextBox query = ProbeField<TextBox>(window, "query");
            int[] offsets = null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            string visible = session == null ? "" : session.VisibleText(out offsets);
            long build = clock.ElapsedMilliseconds;
            clock.Restart();
            if (session != null) session.VisibleText(out offsets);
            long cached = clock.ElapsedMilliseconds;
            ProbeCall(window, "ShowSearch");
            ProbePump();
            clock.Restart();
            query.Text = "측정용 문서";
            ProbePump();
            long search = clock.ElapsedMilliseconds;
            clock.Restart();
            query.Text = "없는 문자열";
            ProbePump();
            long missing = clock.ElapsedMilliseconds;
            report.Append("- visible map 1회: source=").Append(document.Length).Append(" chars, visible=").Append(visible.Length)
                .Append(", build=").Append(build).Append("ms, cached=").Append(cached).Append("ms, cached search=")
                .Append(search).Append("ms, miss search=").Append(missing).Append("ms, lines=")
                .Append(ProbeField<SourceEditor>(window, "source").Document.LineCount).Append(", offsets=").Append(offsets == null ? 0 : offsets.Length).Append('\n');
        } catch (Exception ex) {
            report.Append("- visible map measurement failed: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
        } finally {
            if (window != null) {
                try { ProbeSet(window, "documentOperationBusy", false); ProbeSet(window, "saved", ProbeProp<string>(window, "text")); ProbeSet(window, "recoveredDocument", false); window.Close(); }
                catch (Exception) { }
            }
        }
    }

    static void AppendSamples(StringBuilder report, Tally tally) {
        foreach (string sample in tally.Samples) report.Append("  - ").Append(sample).Append('\n');
    }

    static Tally Measure(SourceEditor editor, MarkdownLivePreview.Session session) {
        var tally = new Tally();
        ICSharpCode.AvalonEdit.Editing.TextArea area = editor.TextArea;
        ICSharpCode.AvalonEdit.Document.TextDocument document = editor.Document;
        // 1. Every logical offset, set directly (the undo/search/API path).
        for (int number = 1; number <= document.LineCount; number++) {
            ICSharpCode.AvalonEdit.Document.DocumentLine line = document.GetLineByNumber(number);
            MarkdownLivePreview.LineInfo info = session.Info(line);
            if (WholeLineHidden(info)) { tally.Skipped++; continue; }
            for (int at = 0; at <= line.Length; at++) {
                area.Caret.Offset = line.Offset + at;
                Pump(editor);
                int settled = area.Caret.Offset;
                tally.SeekStops++;
                if (settled < line.Offset || settled > line.EndOffset) continue;
                int relative = settled - line.Offset;
                if (InsideMarker(info, relative)) {
                    tally.SeekInside++;
                    tally.Note("seek line " + number + " offset " + relative);
                }
                if (relative == 0 && info.LeadEnd > 0) {
                    tally.BeforePrefix++;
                    tally.Note("seek line " + number + " stopped at the line start before a prefix");
                }
            }
        }
        // 2. Keyboard walk: Left/Right, Home/End and Ctrl+Left/Right over every line.
        for (int number = 1; number <= document.LineCount; number++) {
            ICSharpCode.AvalonEdit.Document.DocumentLine line = document.GetLineByNumber(number);
            MarkdownLivePreview.LineInfo info = session.Info(line);
            if (WholeLineHidden(info)) continue;
            area.Caret.Offset = line.Offset;
            Pump(editor);
            // Home
            Press(editor, EditingCommands.MoveToLineStart);
            Record(tally, line, info, area, "Home", true, false);
            Walk(editor, area, line, info, tally, EditingCommands.MoveRightByCharacter, "Right");
            // End
            Press(editor, EditingCommands.MoveToLineEnd);
            Record(tally, line, info, area, "End", false, true);
            Walk(editor, area, line, info, tally, EditingCommands.MoveLeftByCharacter, "Left");
            // Ctrl+Left / Ctrl+Right
            Press(editor, EditingCommands.MoveToLineStart);
            Walk(editor, area, line, info, tally, EditingCommands.MoveRightByWord, "Ctrl+Right");
            Press(editor, EditingCommands.MoveToLineEnd);
            Walk(editor, area, line, info, tally, EditingCommands.MoveLeftByWord, "Ctrl+Left");
        }
        // 3. Double-click word selection, read through the same projection the view uses.
        ICSharpCode.AvalonEdit.Rendering.TextView view = editor.TextArea.TextView;
        for (int number = 1; number <= document.LineCount; number++) {
            ICSharpCode.AvalonEdit.Document.DocumentLine line = document.GetLineByNumber(number);
            MarkdownLivePreview.LineInfo info = session.Info(line);
            if (WholeLineHidden(info)) continue;
            editor.ScrollTo(number - 1, 0);
            Pump(editor);
            ICSharpCode.AvalonEdit.Rendering.VisualLine visual = view.GetVisualLine(number);
            if (visual == null || visual.IsDisposed || visual.FirstDocumentLine.LineNumber != number) continue;
            for (int column = 0; column <= visual.VisualLength; column++) {
                TextLine textLine = visual.GetTextLine(Math.Min(column, visual.VisualLength));
                double x = visual.GetTextLineVisualXPosition(textLine, column) - view.HorizontalOffset;
                double y = visual.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop) - view.VerticalOffset + 2;
                int start, end;
                if (!session.WordAtPoint(new Point(x, y), out start, out end)) continue;
                if (end <= start) continue;
                tally.WordRanges++;
                string word = document.GetText(start, end - start);
                if (word.IndexOfAny(MarkupChars.ToCharArray()) >= 0) {
                    tally.WordMarked++;
                    tally.Note("word line " + number + " '" + word + "'");
                }
            }
        }
        return tally;
    }

    static void Press(SourceEditor editor, RoutedCommand command) {
        command.Execute(null, editor.TextArea);
        Pump(editor);
    }

    // Steps the caret to the next stop and records each landed offset, stopping when it leaves
    // the line or stops moving (the last stop of the line).
    static void Walk(SourceEditor editor, ICSharpCode.AvalonEdit.Editing.TextArea area, ICSharpCode.AvalonEdit.Document.DocumentLine line,
        MarkdownLivePreview.LineInfo info, Tally tally, RoutedCommand command, string label) {
        int previous = area.Caret.Offset;
        int guard = 0;
        while (guard++ <= line.Length + 4) {
            command.Execute(null, area);
            Pump(editor);
            int at = area.Caret.Offset;
            if (at == previous) break;
            previous = at;
            if (at < line.Offset || at > line.EndOffset) break;
            RecordAt(tally, line, info, at, label);
        }
    }

    static void Record(Tally tally, ICSharpCode.AvalonEdit.Document.DocumentLine line, MarkdownLivePreview.LineInfo info,
        ICSharpCode.AvalonEdit.Editing.TextArea area, string label, bool homeCheck, bool endCheck) {
        int at = area.Caret.Offset;
        if (at < line.Offset || at > line.EndOffset) return;
        RecordAt(tally, line, info, at, label);
        int relative = at - line.Offset;
        if ((homeCheck || endCheck) && relative < info.LeadEnd) {
            tally.BeforePrefix++;
            tally.Note(label + " line " + line.LineNumber + " stopped at " + relative + " before a prefix ending at " + info.LeadEnd);
        }
    }

    static void RecordAt(Tally tally, ICSharpCode.AvalonEdit.Document.DocumentLine line, MarkdownLivePreview.LineInfo info,
        int offset, string label) {
        tally.WalkStops++;
        int relative = offset - line.Offset;
        if (InsideMarker(info, relative)) {
            tally.WalkInside++;
            tally.Note(label + " line " + line.LineNumber + " offset " + relative);
        }
    }

    static bool InsideMarker(MarkdownLivePreview.LineInfo info, int relative) {
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Length <= 0) continue;
            if (relative > replacement.Start && relative < replacement.Start + replacement.Length) return true;
        }
        return false;
    }

    // A line whose markup covers all of its text (a rule, a table delimiter, a setext underline)
    // has no visible character, so its caret stops are not part of this contract.
    static bool WholeLineHidden(MarkdownLivePreview.LineInfo info) {
        return info.Rule || info.Delimiter || info.SetextUnderline > 0;
    }

    enum KeyAction { Backspace, Delete, Type, WordBackspace, WordDelete, Cut }

    // The command a non-typing case executes. The word keys are the same commands Ctrl+Backspace
    // and Ctrl+Delete are bound to in the editor build (EditingCommands.DeletePreviousWord/
    // DeleteNextWord), so the probe drives the real command path.
    static RoutedCommand KeyCommand(KeyAction action) {
        if (action == KeyAction.Backspace) return EditingCommands.Backspace;
        if (action == KeyAction.WordBackspace) return EditingCommands.DeletePreviousWord;
        if (action == KeyAction.WordDelete) return EditingCommands.DeleteNextWord;
        if (action == KeyAction.Cut) return ApplicationCommands.Cut;
        return EditingCommands.Delete;
    }

    // One edit case: the caret is placed (a negative offset means "select from |offset| for one
    // character"), the key is executed and the resulting source is compared.
    static void EditCase(ref bool all, StringBuilder report, SourceEditor editor, MarkdownLivePreview.Session session,
        string name, string text, int offset, KeyAction action, string expected) {
        editor.Text = text;
        Pump(editor);
        int at = offset < 0 ? -offset : offset;
        if (offset < 0) {
            editor.Select(at, 1);
            editor.TextArea.Caret.Offset = at + 1;
        } else {
            editor.Select(at, 0);
            editor.TextArea.Caret.Offset = at;
        }
        Pump(editor);
        try {
            if (action == KeyAction.Type) editor.TextArea.PerformTextInput("X");
            else KeyCommand(action).Execute(null, editor.TextArea);
        }
        catch (Exception ex) { report.Append("FAIL ").Append(name).Append(" threw ").Append(ex.GetType().Name).Append('\n'); all = false; return; }
        Pump(editor);
        string actual = editor.Document.Text;
        bool ok = actual == expected;
        report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(": '").Append(text).Append("' -> '").Append(actual).Append("' expected '").Append(expected).Append("'\n");
        if (!ok) all = false;
    }

    // Both edges and every interior offset of a hidden pair are visually identical. Freeze
    // the caret guard to exercise the command with an interior source offset too, as a press
    // or an API callback can leave one there before the next normalisation pass.
    static void HiddenBoundaryCases(ref bool all, StringBuilder report, SourceEditor editor, MarkdownLivePreview.Session session) {
        foreach (string marker in new[]{"**","~~","`","*"}) {
            string text="본문 "+marker+"굵게"+marker+" 글자";
            for (int side=0;side<2;side++) for (int delta=0;delta<=marker.Length;delta++) foreach (bool back in new[]{true,false}) {
                editor.Text=text;Pump(editor);
                int at=3+(side==0 ? 0 : marker.Length+2)+delta;
                string expected=side==0 ? back ? "본문"+marker+"굵게"+marker+" 글자" : "본문 "+marker+"게"+marker+" 글자" :
                    back ? "본문 "+marker+"굵"+marker+" 글자" : "본문 "+marker+"굵게"+marker+"글자";
                var undo=editor.Document.UndoStack;int limit=undo.SizeLimit;undo.SizeLimit=20;
                session.BeginPress();
                try {
                    editor.Select(at,0);editor.CaretOffset=at;
                    (back ? EditingCommands.Backspace : EditingCommands.Delete).Execute(null,editor.TextArea);
                } finally { session.EndPress(); }
                Pump(editor);
                string actual=editor.Document.Text;
                bool canUndo=undo.CanUndo;if(canUndo)undo.Undo();Pump(editor);
                Check(ref all,report,"hidden boundary "+marker+" "+(side==0?"open":"close")+"+"+delta+" "+(back?"Backspace":"Delete"),
                    actual==expected&&canUndo&&editor.Document.Text==text,
                    "at="+at+", text='"+actual+"', expected='"+expected+"', one undo="+(editor.Document.Text==text));
                undo.SizeLimit=limit;
            }
            // The last visible character takes its empty shell with it without a selection.
            foreach (bool back in new[]{true,false}) {
                string single="x "+marker+"게"+marker+" y";
                PairShellCase(ref all,report,editor,"last character beside "+marker+" "+(back?"Backspace":"Delete"),
                    single,back ? 3+marker.Length*2 : 2,0,back?KeyAction.Backspace:KeyAction.Delete,"x  y");
            }
        }
        foreach (string marker in new[]{"**","~~","`","*"}) foreach (bool back in new[]{true,false}) {
            string prefix="| head | h2 |\n| --- | --- |\n";
            string text=prefix+"| "+marker+"게"+marker+" | z |\n";
            PairShellCase(ref all,report,editor,"table last character beside "+marker+" "+(back?"Backspace":"Delete"),
                text,prefix.Length+(back ? 3+marker.Length*2 : 2),0,back?KeyAction.Backspace:KeyAction.Delete,prefix+"|  | z |\n");
        }
        EditCase(ref all,report,editor,session,"backspace past closing marker deletes a surrogate pair",
            "x **😀** y",8,KeyAction.Backspace,"x  y");
        EditCase(ref all,report,editor,session,"backspace skips adjacent closing markers",
            "x **~~굵게~~** y",12,KeyAction.Backspace,"x **~~굵~~** y");
        foreach (string newline in new[]{"\n","\r\n"}) {
            PairShellCase(ref all,report,editor,"backspace at opening pair joins a whole newline "+newline.Length,
                "앞"+newline+"**굵게**",3+newline.Length,0,KeyAction.Backspace,"앞**굵게**");
            PairShellCase(ref all,report,editor,"delete at closing pair joins a whole newline "+newline.Length,
                "**굵게**"+newline+"뒤",6,0,KeyAction.Delete,"**굵게**뒤");
        }
    }

    // P2 items 1-2: Ctrl+Backspace / Ctrl+Delete take the visual-line word range, which can start
    // or end on a zero-width marker run; and a selection that cuts only one side of a pair must be
    // widened before Backspace/Delete/Cut removes (or copies) it. Both follow the same pair rule
    // the whole-visible-text selection uses.
    static void WordMarkerCases(ref bool all, StringBuilder report, SourceEditor editor, MarkdownLivePreview.Session session) {
        // At the end of a pair the word range is only the hidden closing run. Taking the pair
        // whole is what keeps a lone "**" from being drawn as text.
        PairShellCase(ref all, report, editor, "ctrl+backspace at the end of ** takes the whole pair",
            "**굵게**", 6, 0, KeyAction.WordBackspace, "");
        PairShellCase(ref all, report, editor, "ctrl+delete at the start of ** takes the whole pair",
            "**굵게**", 0, 0, KeyAction.WordDelete, "");
        PairShellCase(ref all, report, editor, "ctrl+backspace keeps the text before the pair",
            "본문 **굵게**", 9, 0, KeyAction.WordBackspace, "본문 ");
        PairShellCase(ref all, report, editor, "ctrl+delete keeps the text after the pair",
            "**굵게** 글자", 2, 0, KeyAction.WordDelete, " 글자");
        PairShellCase(ref all, report, editor, "ctrl+backspace over a multi-word pair takes the pair",
            "x **hello world**", 17, 0, KeyAction.WordBackspace, "x ");
        PairShellCase(ref all, report, editor, "ctrl+backspace over a one-sided selection takes the pair",
            "a **bold** b", 4, 6, KeyAction.WordBackspace, "a  b");
        PairShellCase(ref all, report, editor, "ctrl+delete inside a table cell takes the cell's pair",
            "| **메모** | z |", 2, 0, KeyAction.WordDelete, "|  | z |");
        PairShellCase(ref all, report, editor, "ctrl+backspace inside a table cell takes the cell's pair",
            "| **메모** | z |", 8, 0, KeyAction.WordBackspace, "|  | z |");
        // The selection half of item 2: the range covers the visible text and the closing marker,
        // the opening marker is outside it.
        PairShellCase(ref all, report, editor, "backspace over a one-sided selection takes the pair",
            "a **bold** b", 4, 6, KeyAction.Backspace, "a  b");
        PairShellCase(ref all, report, editor, "delete over a one-sided selection takes the pair",
            "a **bold** b", 2, 7, KeyAction.Delete, "a  b");
        PairShellCase(ref all, report, editor, "cut over a one-sided selection takes the pair",
            "a **bold** b", 4, 6, KeyAction.Cut, "a  b");
    }

    // One whole-pair case: the inner text is selected and a delete, backspace or typed
    // replacement runs. A delete takes the two hidden markers with it and one undo puts the
    // pair and its text back; typing replaces only the text, the markers stay.
    static void PairShellCase(ref bool all, StringBuilder report, SourceEditor editor,
        string name, string text, int start, int length, KeyAction action, string expected) {
        editor.Text = text;
        Pump(editor);
        editor.Select(start, length);
        editor.TextArea.Caret.Offset = start + length;
        Pump(editor);
        // SourceEditor turns AvalonEdit's own stack off (SourceEditor.TrackDocument sets the
        // size limit to 0; the window keeps DocumentHistory instead), so this probe turns it on
        // for the one edit and reads the step count from it.
        ICSharpCode.AvalonEdit.Document.UndoStack undo = editor.Document.UndoStack;
        int limit = undo.SizeLimit;
        undo.SizeLimit = 50;
        try {
            if (action == KeyAction.Type) editor.TextArea.PerformTextInput("X");
            else KeyCommand(action).Execute(null, editor.TextArea);
            Pump(editor);
            string actual = editor.Document.Text;
            bool ok = actual == expected;
            report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(": '").Append(text).Append("' -> '").Append(actual).Append("' expected '").Append(expected).Append("'\n");
            if (!ok) all = false;
            if (action == KeyAction.Type) return;
            // One step has to bring the pair and its text back.
            bool canUndo = undo.CanUndo;
            if (canUndo) undo.Undo();
            Pump(editor);
            string restored = editor.Document.Text;
            bool okUndo = canUndo && restored == text;
            report.Append(okUndo ? "PASS " : "FAIL ").Append(name).Append(" one undo restores the pair: '").Append(restored).Append("' expected '").Append(text).Append("'\n");
            if (!okUndo) all = false;
        }
        catch (Exception ex) { report.Append("FAIL ").Append(name).Append(" threw ").Append(ex.GetType().Name).Append('\n'); all = false; }
        finally { undo.SizeLimit = limit; }
    }

    static void Pump(SourceEditor editor) {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
        editor.UpdateLayout();
        editor.TextArea.TextView.EnsureVisualLines();
    }

    // The centre of a formatted table row's cell box must place the caret inside that cell;
    // the cell range and the placement are reported so a failure names both.
    static string TableCellClick(SourceEditor editor, MarkdownLivePreview.Session session, int number, int cell, out int placedAt) {
        ICSharpCode.AvalonEdit.Document.DocumentLine line = editor.Document.GetLineByNumber(number);
        MarkdownLivePreview.LineInfo info = session.Info(line);
        editor.Select(0, 0);
        Pump(editor);
        ICSharpCode.AvalonEdit.Rendering.TextView view = editor.TextArea.TextView;
        view.EnsureVisualLines();
        ICSharpCode.AvalonEdit.Rendering.VisualLine visual = view.GetOrConstructVisualLine(line);
        Rect box = session.TableCellBox(visual, cell);
        bool placed = false;
        placedAt = -1;
        session.BeginPress();
        try {
            placed = session.PlaceTableCaret(new Point(box.Left + box.Width / 2, box.Top + box.Height / 2), false);
            placedAt = editor.CaretOffset - line.Offset;
        } finally { session.EndPress(); }
        Pump(editor);
        return "placed=" + placed + " range=" + info.Cells[cell].Start + ".." + info.Cells[cell].End +
            " box=" + box.Left.ToString("0.#", CultureInfo.InvariantCulture) + ".." + box.Right.ToString("0.#", CultureInfo.InvariantCulture);
    }

    // One paste case: the selection (a zero length places a bare caret) is set, the clipboard
    // carries the text, and Paste runs through the command the editor itself binds.
    static void PasteCase(ref bool all, StringBuilder report, SourceEditor editor,
        string name, string text, int start, int length, string paste, string expected) {
        editor.Text = text;
        Pump(editor);
        editor.Select(start, length);
        editor.TextArea.Caret.Offset = start + length;
        Pump(editor);
        if (!SetClipboard(paste)) {
            report.Append("FAIL ").Append(name).Append(": clipboard unavailable\n");
            all = false;
            return;
        }
        try { ApplicationCommands.Paste.Execute(null, editor.TextArea); }
        catch (Exception ex) { report.Append("FAIL ").Append(name).Append(" threw ").Append(ex.GetType().Name).Append('\n'); all = false; return; }
        Pump(editor);
        string actual = editor.Document.Text;
        bool ok = actual == expected;
        report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(": '").Append(text).Append("' -> '").Append(actual).Append("' expected '").Append(expected).Append("'\n");
        if (!ok) all = false;
    }

    // The system clipboard can be held by another process for a moment; retry instead of
    // turning a transient failure into a probe FAIL.
    static bool SetClipboard(string value) {
        for (int attempt = 0; attempt < 12; attempt++) {
            try { System.Windows.Clipboard.SetText(value); return true; }
            catch (System.Runtime.InteropServices.ExternalException) { }
            System.Threading.Thread.Sleep(100);
        }
        return false;
    }

    static void Trace(string text) {
        try { System.Diagnostics.Trace.WriteLine(text); }
        catch (Exception) { }
        try { Console.WriteLine(text); }
        catch (Exception) { }
    }

    static void Check(ref bool all, StringBuilder report, string name, bool ok, string detail) {
        report.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (detail != null) report.Append(": ").Append(detail);
        report.Append('\n');
        if (!ok) all = false;
    }

    // A small solid picture, so the cell-image checks use the real decode/cache path without
    // depending on any file outside the probe folder.
    static void WriteProbePng(string path, int width, int height) {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) {
            pixels[i] = 40; pixels[i + 1] = 90; pixels[i + 2] = 200; pixels[i + 3] = 255;
        }
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write)) encoder.Save(stream);
    }

    // The picture decodes on a worker and a later dispatcher pass redraws the row, so the
    // probe pumps until a cell layout has drawn one (or gives up after ~a second).
    static void PumpImages(SourceEditor editor) {
        long before = MarkdownLivePreview.DiagImageRuns;
        for (int i = 0; i < 60 && MarkdownLivePreview.DiagImageRuns == before; i++) {
            Pump(editor);
            System.Threading.Thread.Sleep(10);
        }
        Pump(editor);
    }

    static MarkdownLivePreview.TableRowElement RowElement(VisualLine visual) {
        if (visual == null) return null;
        foreach (VisualLineElement element in visual.Elements) {
            var row = element as MarkdownLivePreview.TableRowElement;
            if (row != null) return row;
        }
        return null;
    }

    static string Glyphs(VisualLine visual) {
        var text = new StringBuilder();
        if (visual == null) return "";
        foreach (TextLine line in visual.TextLines)
            foreach (IndexedGlyphRun run in line.GetIndexedGlyphRuns())
                if (run.GlyphRun.Characters != null) text.Append(new string(new List<char>(run.GlyphRun.Characters).ToArray()));
        return text.ToString();
    }

    static string Num(double value) {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // Findings from the two hidden-marker reviews (A1, A3, A5, S1, S2, A2, A4, S4, S6, S3,
    // A6). Every check states the contract; on the unfixed build a FAIL is the reproduction.
    static void FixedFindings(ref bool all, StringBuilder report, SourceEditor editor,
        MarkdownLivePreview.Session session, MarkdownLivePreview.Settings settings, string dir) {
        report.Append("\n## hidden-marker review findings\n");

        // A1: a folded "[id]: url" line is collapsed through the height tree. An edit that
        // touches it or its host line must release the fold before AvalonEdit validates
        // anything, or the view builds a visual line from a collapsed line (InvalidOperationException).
        string a1 = "# title\n\nhost paragraph\n[id]: https://example.invalid/def\n[other]: https://example.invalid/y\n\nafter\n";
        editor.Text = a1; Pump(editor);
        editor.TextArea.Caret.Line = 3; Pump(editor); Pump(editor);
        DocumentLine a1Host = editor.Document.GetLineByNumber(3);
        bool a1Folded = !OwnVisualLine(editor.TextArea.TextView, 4) && session.ReferenceBelow(a1Host) != null;
        report.Append("- A1 fold before edits: ").Append(a1Folded).Append('\n');
        StructuralCase(ref all, report, editor, "A1 type on the host line above a folded reference",
            a1, 3, delegate(SourceEditor e) {
                e.Document.Insert(e.Document.GetLineByNumber(3).EndOffset, "X");
            }, "# title\n\nhost paragraphX\n[id]: https://example.invalid/def\n[other]: https://example.invalid/y\n\nafter\n");
        StructuralCase(ref all, report, editor, "A1 delete the host line above a folded reference",
            a1, 3, delegate(SourceEditor e) {
                DocumentLine host = e.Document.GetLineByNumber(3);
                e.Document.Remove(host.Offset, host.Length + host.DelimiterLength);
            }, "# title\n\n[id]: https://example.invalid/def\n[other]: https://example.invalid/y\n\nafter\n");
        StructuralCase(ref all, report, editor, "A1 host line becomes a table header and delimiter",
            a1, 3, delegate(SourceEditor e) {
                DocumentLine host = e.Document.GetLineByNumber(3);
                DocumentLine reference = host.NextLine;
                e.Document.Replace(reference.Offset, reference.Length, "| --- | --- |");
                e.Document.Replace(host.Offset, host.Length, "| a | b |");
            }, "# title\n\n| a | b |\n| --- | --- |\n[other]: https://example.invalid/y\n\nafter\n");
        StructuralCase(ref all, report, editor, "A1 select the host line and the folded reference and delete",
            a1, 3, delegate(SourceEditor e) {
                DocumentLine host = e.Document.GetLineByNumber(3);
                DocumentLine reference = host.NextLine;
                e.Select(host.Offset, reference.EndOffset - host.Offset);
                e.TextArea.Caret.Offset = reference.EndOffset;
                Pump(e);
                EditingCommands.Delete.Execute(null, e.TextArea);
            }, "# title\n\n\n[other]: https://example.invalid/y\n\nafter\n");
        StructuralCase(ref all, report, editor, "A1 backspace at the start of the host line above a fold",
            a1, 3, delegate(SourceEditor e) {
                DocumentLine host = e.Document.GetLineByNumber(3);
                e.Select(host.Offset, 0);
                e.TextArea.Caret.Offset = host.Offset;
                Pump(e);
                EditingCommands.Backspace.Execute(null, e.TextArea);
            }, "# title\nhost paragraph\n[id]: https://example.invalid/def\n[other]: https://example.invalid/y\n\nafter\n");

        // The fold is released for the host edit and installed again before the pass ends:
        // typing on the host line must not leave the definition line permanently unfolded.
        string a1Typed = a1.Replace("host paragraph", "host paragraphX");
        string a1FoldError = null; bool a1Refolded = false;
        try {
            editor.Text = a1; Pump(editor);
            editor.TextArea.Caret.Line = 3; Pump(editor); Pump(editor);
            editor.Document.Insert(editor.Document.GetLineByNumber(3).EndOffset, "X");
            Pump(editor); Pump(editor);
            a1Refolded = editor.Document.Text == a1Typed && !OwnVisualLine(editor.TextArea.TextView, 4) &&
                session.ReferenceBelow(editor.Document.GetLineByNumber(3)) != null;
        } catch (Exception ex) { a1FoldError = ex.GetType().Name + ": " + ex.Message; }
        Check(ref all, report, "A1 typing on the host line keeps the reference folded", a1FoldError == null && a1Refolded,
            "folded=" + a1Refolded + " text=" + (editor.Document.Text == a1Typed) + " error=" + a1FoldError);

        // A5: Delete/Backspace at a line boundary joins lines through the newline. That is the
        // normal structural path; HiddenTextDelete must decline there (it must not run its own
        // selection replace across the boundary). The folded reference below is the crash case.
        string a5 = "본문 **강조**\n[id]: https://example.invalid/def\nafter\n";
        StructuralCase(ref all, report, editor, "A5 delete at the end of a line above a folded reference",
            a5, 1, delegate(SourceEditor e) {
                DocumentLine line = e.Document.GetLineByNumber(1);
                e.Select(line.EndOffset, 0);
                e.TextArea.Caret.Offset = line.EndOffset;
                Pump(e);
                EditingCommands.Delete.Execute(null, e.TextArea);
            }, "본문 **강조**[id]: https://example.invalid/def\nafter\n");
        StructuralCase(ref all, report, editor, "A5 delete at the end of a line above a heading",
            "본문 **강조**\n# 제목\nafter\n", 1, delegate(SourceEditor e) {
                DocumentLine line = e.Document.GetLineByNumber(1);
                e.Select(line.EndOffset, 0);
                e.TextArea.Caret.Offset = line.EndOffset;
                Pump(e);
                EditingCommands.Delete.Execute(null, e.TextArea);
            }, "본문 **강조**# 제목\nafter\n");
        StructuralCase(ref all, report, editor, "A5 delete at the end of a line above a list item",
            "본문 **강조**\n- 항목\nafter\n", 1, delegate(SourceEditor e) {
                DocumentLine line = e.Document.GetLineByNumber(1);
                e.Select(line.EndOffset, 0);
                e.TextArea.Caret.Offset = line.EndOffset;
                Pump(e);
                EditingCommands.Delete.Execute(null, e.TextArea);
            }, "본문 **강조**- 항목\nafter\n");
        StructuralCase(ref all, report, editor, "A5 backspace at the start of a line below a folded reference",
            "before\n[id]: https://example.invalid/def\nafter\n", 3, delegate(SourceEditor e) {
                DocumentLine line = e.Document.GetLineByNumber(3);
                e.Select(line.Offset, 0);
                e.TextArea.Caret.Offset = line.Offset;
                Pump(e);
                EditingCommands.Backspace.Execute(null, e.TextArea);
            }, "before\n[id]: https://example.invalid/defafter\n");

        // S1: Home must reach offset 0 on a line that starts with an inline marker; only a
        // structural prefix (heading, quote, list) owns a hidden leading run.
        foreach (string s1 in new[] { "**굵게** 본문", "*기울임* 본문", "~~취소~~ 본문", "`코드` 본문", "__밑줄__ 본문" }) {
            editor.Text = s1 + "\nplain line\n"; Pump(editor);
            DocumentLine line = editor.Document.GetLineByNumber(1);
            editor.Select(0, 0);
            editor.TextArea.Caret.Offset = line.EndOffset; Pump(editor);
            EditingCommands.MoveToLineStart.Execute(null, editor.TextArea); Pump(editor);
            int home = editor.TextArea.Caret.Offset - line.Offset;
            editor.TextArea.Caret.Offset = line.Offset; Pump(editor);
            string typed = null; string error = null;
            try { editor.TextArea.PerformTextInput("x"); } catch (Exception ex) { error = ex.GetType().Name; }
            Pump(editor);
            typed = editor.Document.Text;
            bool ok = error == null && home == 0 && typed == "x" + s1 + "\nplain line\n";
            Check(ref all, report, "S1 Home reaches the line start before an inline marker: " + s1, ok,
                "home=" + home + " typed='" + Flat(typed) + "' error=" + error);
        }
        editor.Text = "# 제목\n"; Pump(editor);
        editor.TextArea.Caret.Offset = editor.Document.GetLineByNumber(1).EndOffset; Pump(editor);
        EditingCommands.MoveToLineStart.Execute(null, editor.TextArea); Pump(editor);
        int headingHome = editor.TextArea.Caret.Offset - editor.Document.GetLineByNumber(1).Offset;
        Check(ref all, report, "S1 heading Home still stops after the hidden prefix", headingHome == 2,
            "home=" + headingHome);

        // S2: the overlap guard in HiddenTextDelete must fall through to the unit-delete path
        // for an inline pair (a structural neighbour is not a reason to swallow the key), and
        // must still refuse to delete across a table column separator. b55: the caret on the far
        // side of the opening pair is the plain line's text start, so the line's own marker goes
        // first and the pair keeps its text (the user's real Backspace case).
        StructuralCase(ref all, report, editor, "S2 backspace before bold text in a list item removes the marker",
            "- **abc**\n", 1, delegate(SourceEditor e) {
                e.Select(4, 0);
                e.TextArea.Caret.Offset = 4;
                Pump(e);
                EditingCommands.Backspace.Execute(null, e.TextArea);
            }, "**abc**\n");
        StructuralCase(ref all, report, editor, "S2 backspace before bold text in a heading removes the marker",
            "# **abc**\n", 1, delegate(SourceEditor e) {
                e.Select(4, 0);
                e.TextArea.Caret.Offset = 4;
                Pump(e);
                EditingCommands.Backspace.Execute(null, e.TextArea);
            }, "**abc**\n");
        StructuralCase(ref all, report, editor, "S2 backspace before bold text in a table cell keeps the row",
            "| a | **b** |\n| --- | --- |\n| x | y |\n", 1, delegate(SourceEditor e) {
                DocumentLine row = e.Document.GetLineByNumber(1);
                e.Select(row.Offset + row.Length - 5, 0);
                e.TextArea.Caret.Offset = row.Offset + row.Length - 5;
                Pump(e);
                EditingCommands.Backspace.Execute(null, e.TextArea);
            }, "| a | **b** |\n| --- | --- |\n| x | y |\n");

        // A2: deleting the only visible text of a (possibly nested) inline pair takes the
        // markup with it, in one edit, so no bare "****" / empty span is left behind.
        EmptyPairCase(ref all, report, editor, "A2 nested bold+italic three stars", "***가***", "가", "");
        EmptyPairCase(ref all, report, editor, "A2 nested strike inside bold", "**~~가~~**", "가", "");
        // The live parser does not re-parse markdown inside a span, so the span case is the
        // tag pair itself (a "**" there would be literal text, not hidden markup).
        EmptyPairCase(ref all, report, editor, "A2 colour span around the only text on a line", "<span style=\"color:red\">가</span>", "가", "");
        EmptyPairCase(ref all, report, editor, "A2 span around the only text on a line", "a <span style=\"color:red\">가</span> b", "가", "a  b");

        // A4: a table cell image is measured before its bitmap decodes. Once the pixels
        // arrive the table has to re-sample its columns so the picture gets its natural width
        // (up to the column cap) instead of the width of the "![..](..)" source text.
        string wideImage = Path.Combine(dir, "fix6-wide.png");
        WriteProbePng(wideImage, 400, 80);
        string wideTable = "| left | right |\n| --- | --- |\n| one | ![shot](fix6-wide.png) |\ntail\n";
        MarkdownLivePreview.DiagImageWidth = 0; MarkdownLivePreview.DiagImageHeight = 0;
        MarkdownLivePreview.ProbeImageDecodeDelay = 600;
        try {
            editor.Text = wideTable;
            editor.TextArea.Caret.Line = 4;
            Pump(editor);
            double early = MarkdownLivePreview.DiagImageWidth;
            double best = 0;
            for (int attempt = 0; attempt < 300 && best < 340; attempt++) {
                Pump(editor);
                if (MarkdownLivePreview.DiagImageWidth > best) best = MarkdownLivePreview.DiagImageWidth;
                System.Threading.Thread.Sleep(10);
            }
            Check(ref all, report, "A4 a decoded table cell image widens its column", best >= 340 && editor.Document.Text == wideTable,
                "drawn=" + Num(best) + " early=" + Num(early) + " text=" + (editor.Document.Text == wideTable));
        } catch (Exception ex) {
            report.Append("FAIL A4 a decoded table cell image widens its column threw ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            all = false;
        } finally {
            MarkdownLivePreview.ProbeImageDecodeDelay = 0;
        }

        // S4: a scroll that re-syncs the visible reference lines must reuse the folded run,
        // not walk the whole run again for every visible line.
        StringBuilder runText = new StringBuilder("host paragraph\n");
        for (int i = 0; i < 800; i++) runText.Append("[id").Append(i).Append("]: https://example.invalid/def").Append(i).Append('\n');
        runText.Append("\ntail\n");
        editor.Text = runText.ToString(); Pump(editor); Pump(editor);
        bool runFolded = !OwnVisualLine(editor.TextArea.TextView, 2);
        MarkdownLivePreview.DiagReferenceScanLines = 0;
        editor.ScrollToVerticalOffset(editor.VerticalOffset + 40); Pump(editor); Pump(editor);
        long scanned = MarkdownLivePreview.DiagReferenceScanLines;
        Check(ref all, report, "S4 a scroll reuses the reference run instead of rescanning it",
            runFolded && scanned <= 400, "folded=" + runFolded + " scanned=" + scanned + " run=800");

        // S6: a composition message for another window (the find box) must not start the
        // editor's composition machinery while the text area is not focused.
        MethodInfo composition = typeof(MarkdownLivePreview.Session).GetMethod("CompositionMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        if (composition == null) throw new MissingMemberException("probe: Session has no method 'CompositionMessage'");
        // Another control (the find box) owns the keyboard in the reported case; a disabled
        // editor cannot hold keyboard focus, so the same guard is measured without a user.
        bool wasEnabled = editor.IsEnabled;
        bool unfocused = false; long callbacksBefore = 0, callbacksAfter = 0;
        try {
            editor.IsEnabled = false;
            Pump(editor);
            unfocused = !editor.TextArea.IsKeyboardFocused && !editor.ImeBusy;
            callbacksBefore = session.CompositionCallbacks;
            object[] startArgs = new object[] { IntPtr.Zero, 0x010D, IntPtr.Zero, IntPtr.Zero, false };
            composition.Invoke(session, startArgs);
            Pump(editor); Pump(editor);
            callbacksAfter = session.CompositionCallbacks;
            object[] endArgs = new object[] { IntPtr.Zero, 0x010E, IntPtr.Zero, IntPtr.Zero, false };
            composition.Invoke(session, endArgs);
            Pump(editor);
        } finally {
            editor.IsEnabled = wasEnabled;
            Pump(editor);
        }
        Check(ref all, report, "S6 an unfocused composition message is ignored",
            unfocused && callbacksAfter == callbacksBefore,
            "unfocused=" + unfocused + " callbacks=" + (callbacksAfter - callbacksBefore));

        // S3: widening a selection keeps its direction: a caret that sat at the start keeps
        // the anchor at the far end (shift extension then grows from that end).
        MethodInfo widen = typeof(MarkdownLivePreview.Session).GetMethod("WidenHiddenSelection", BindingFlags.Instance | BindingFlags.NonPublic);
        if (widen == null) throw new MissingMemberException("probe: Session has no method 'WidenHiddenSelection'");
        editor.Text = "a **bold** b"; Pump(editor);
        editor.Select(3, 1); editor.TextArea.Caret.Offset = 3; Pump(editor);
        widen.Invoke(session, null); Pump(editor);
        int backAnchor = editor.Document.GetOffset(editor.TextArea.Selection.StartPosition.Location);
        bool backDirection = editor.SelectionStart == 2 && editor.SelectionLength == 2 && editor.CaretOffset == 2 && backAnchor == 4;
        editor.Text = "a **bold** b"; Pump(editor);
        editor.Select(2, 1); editor.TextArea.Caret.Offset = 3; Pump(editor);
        widen.Invoke(session, null); Pump(editor);
        int forwardAnchor = editor.Document.GetOffset(editor.TextArea.Selection.StartPosition.Location);
        bool forwardDirection = editor.SelectionStart == 2 && editor.SelectionLength == 2 && editor.CaretOffset == 4 && forwardAnchor == 2;
        Check(ref all, report, "S3 widening keeps the selection direction", backDirection && forwardDirection,
            "back caret/start/anchor=" + editor.CaretOffset + "/" + editor.SelectionStart + "/" + backAnchor +
            " forward=" + forwardAnchor + " ok=" + backDirection + "/" + forwardDirection);

        // S3b: an endpoint inside the invisible line prefix widens OUTWARD (the start to the
        // prefix's own start), never forward to the text, and a selection wholly inside the
        // prefix cannot collapse to nothing.
        editor.Text = "## 제목\n본문"; Pump(editor);
        editor.Select(1, 1); editor.TextArea.Caret.Offset = 2; Pump(editor);
        widen.Invoke(session, null); Pump(editor);
        bool prefixOutward = editor.SelectionStart == 0 && editor.SelectionLength == 3 && editor.CaretOffset == 3;
        editor.Text = "## 제목\n본문"; Pump(editor);
        editor.Select(1, 2); editor.TextArea.Caret.Offset = 3; Pump(editor);
        widen.Invoke(session, null); Pump(editor);
        bool prefixNotCollapsed = editor.SelectionStart == 0 && editor.SelectionLength == 3;
        Check(ref all, report, "S3b a prefix endpoint widens outward and never collapses",
            prefixOutward && prefixNotCollapsed,
            "outward start/length/caret=" + editor.SelectionStart + "/" + editor.SelectionLength + "/" + editor.CaretOffset +
            " ok=" + prefixOutward + "/" + prefixNotCollapsed);

        // A6: a multi-line drag that ends at the left edge of a hidden "# " prefix lands
        // inside the prefix; the whole prefix belongs to the selection (line.Offset), so
        // deleting the lines cannot leave a bare "#".
        editor.Text = "one\n# 제목\ntail line\n"; Pump(editor);
        DocumentLine a6Second = editor.Document.GetLineByNumber(2);
        DocumentLine a6Third = editor.Document.GetLineByNumber(3);
        editor.Select(a6Second.Offset + 1, a6Third.Offset - (a6Second.Offset + 1));
        editor.TextArea.Caret.Offset = a6Third.Offset;
        Pump(editor);
        string a6Error = null;
        try { session.SnapHiddenPrefixes(); } catch (Exception ex) { a6Error = ex.GetType().Name; }
        Pump(editor);
        int a6Start = editor.SelectionStart;
        bool a6Snapped = a6Error == null && a6Start == a6Second.Offset;
        string a6Text = null;
        if (a6Error == null) {
            try { EditingCommands.Delete.Execute(null, editor.TextArea); } catch (Exception ex) { a6Error = ex.GetType().Name; }
            Pump(editor);
            a6Text = editor.Document.Text;
        }
        Check(ref all, report, "A6 a multi-line selection includes the whole hidden prefix",
            a6Snapped && a6Error == null && a6Text == "one\ntail line\n",
            "start=" + (a6Start - a6Second.Offset) + " text='" + Flat(a6Text) + "' error=" + a6Error);

        // A3: attach/dispose cycles return every hook they install. The product counts the
        // installed handler sets; a leaked PreviewExecuted/TextEntering handler keeps it high.
        int setsBefore = MarkdownLivePreview.Session.LiveHandlerSets;
        string a3Error = null; bool attached = false, released = false;
        try {
            var scratch = new SourceEditor { FontFamily = new FontFamily("Malgun Gothic"), FontSize = 16, WordWrap = true };
            scratch.Text = "a **bold** b\n";
            var scratchSettings = new MarkdownLivePreview.Settings {
                FontSize = 16, Family = "Malgun Gothic", LinePercent = 150, Dark = false, WordWrap = true, BaseDirectory = dir
            };
            var scratchSession = MarkdownLivePreview.Attach(scratch, scratchSettings);
            attached = MarkdownLivePreview.Session.LiveHandlerSets == setsBefore + 1;
            scratchSession.Dispose();
            released = MarkdownLivePreview.Session.LiveHandlerSets == setsBefore;
        } catch (Exception ex) { a3Error = ex.GetType().Name + ": " + ex.Message; }
        Check(ref all, report, "A3 a disposed session releases its attach count", a3Error == null && attached && released,
            "attached=" + attached + " released=" + released + " error=" + a3Error);
    }

    static string Flat(string text) {
        return text == null ? "null" : text.Replace("\r", "").Replace("\n", "\\n");
    }

    // Runs one generated edit on its own editor and compares the whole source afterwards.
    // A collapsed-line layout failure leaves the visual line list mid-construction, so a
    // shared view could not run the next case; every case builds and drops its own session.
    static void StructuralCase(ref bool all, StringBuilder report, SourceEditor editor, string name,
        string text, int caretLine, Action<SourceEditor> edit, string expected) {
        SourceEditor caseEditor = new SourceEditor {
            FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true,
            Background=Brushes.White, Foreground=Brushes.Black
        };
        caseEditor.TextArea.TextView.LineHeight = double.NaN;
        Window caseWindow = null;
        MarkdownLivePreview.Session caseSession = null;
        string error = null, actual = null;
        try {
            caseEditor.Text = text;
            var caseSettings = new MarkdownLivePreview.Settings {
                FontSize=16, Family="Malgun Gothic", LinePercent=150, Dark=false,
                WordWrap=true, BaseDirectory=String.Empty, HideMarkers=true
            };
            caseSession = MarkdownLivePreview.Attach(caseEditor, caseSettings);
            caseWindow = new Window {
                Left=-20000, Top=0, Width=900, Height=1100, ShowInTaskbar=false,
                ShowActivated=false, WindowStyle=WindowStyle.None, Content=caseEditor
            };
            caseWindow.Show();
            Pump(caseEditor);
            caseEditor.TextArea.Caret.Line = caretLine;
            Pump(caseEditor); Pump(caseEditor);
            edit(caseEditor);
            Pump(caseEditor);
            caseEditor.TextArea.TextView.EnsureVisualLines();
            actual = caseEditor.Document.Text;
        } catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        if (error == null) {
            try { actual = caseEditor.Document.Text; } catch (Exception) { }
        }
        try { if (caseSession != null) caseSession.Dispose(); } catch (Exception) { }
        try { if (caseWindow != null) caseWindow.Close(); } catch (Exception) { }
        bool ok = error == null && actual == expected;
        report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(": ");
        if (error != null) report.Append("threw ").Append(error).Append("; ");
        report.Append("text='").Append(Flat(actual)).Append("' expected='").Append(Flat(expected)).Append("'\n");
        if (!ok) all = false;
    }

    // Selects the visible text of one inline pair, deletes it and expects the source that
    // remains; one undo has to bring back the exact original.
    static void EmptyPairCase(ref bool all, StringBuilder report, SourceEditor editor, string name,
        string text, string visible, string expected) {
        editor.Text = text;
        Pump(editor);
        int at = text.IndexOf(visible, StringComparison.Ordinal);
        string error = null, actual = null, restored = null;
        ICSharpCode.AvalonEdit.Document.UndoStack undo = editor.Document.UndoStack;
        int limit = undo.SizeLimit;
        undo.SizeLimit = 20;
        try {
            editor.Select(at, visible.Length);
            editor.TextArea.Caret.Offset = at + visible.Length;
            Pump(editor);
            EditingCommands.Backspace.Execute(null, editor.TextArea);
            Pump(editor);
            actual = editor.Document.Text;
            if (undo.CanUndo) undo.Undo();
            Pump(editor);
            restored = editor.Document.Text;
        } catch (Exception ex) {
            error = ex.GetType().Name + ": " + ex.Message;
        } finally {
            undo.SizeLimit = limit;
        }
        bool ok = error == null && actual == expected && restored == text;
        report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(": ");
        if (error != null) report.Append("threw ").Append(error).Append("; ");
        report.Append("text='").Append(Flat(actual)).Append("' expected='").Append(expected)
            .Append("' undo='").Append(Flat(restored)).Append("'\n");
        if (!ok) all = false;
    }

    // A visual line that starts at this document line. A folded line has none of its own.
    static bool OwnVisualLine(TextView view, int lineNumber) {
        VisualLine line = view.GetVisualLine(lineNumber);
        return line != null && line.FirstDocumentLine.LineNumber == lineNumber;
    }
}
}
