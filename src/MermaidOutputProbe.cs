using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
namespace MDNote {
// --mermaid-output-probe <folder>: a ```mermaid fence in the two outputs that are not the editor —
// the print/rich-copy FlowDocument (Markdown.Parse -> Markdown.ParseCore) and the HTML export
// (HtmlExporter.Convert). Documents are generated here; no user file, no window, no clipboard.
// Report <folder>\mermaid-output-probe.txt (mermaid-output-probe-PASS.txt only when every check held,
// -FAIL.txt otherwise). Every wait has a deadline and the whole run is held under Budget milliseconds.
static class MermaidOutputProbe {
    const int Budget = 55000;
    const string Family = "Malgun Gothic";
    static bool All = true;
    static readonly StringBuilder Report = new StringBuilder();
    static readonly Stopwatch Total = new Stopwatch();
    static string Folder;

    static void Check(string name, bool ok, string detail) {
        Report.Append(ok ? "PASS " : "FAIL ").Append(name);
        if (detail != null && detail.Length > 0) Report.Append(": ").Append(detail);
        Report.Append('\n');
        if (!ok) All = false;
    }
    static void Info(string text) { Report.Append("INFO ").Append(text).Append('\n'); }
    static string N(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }
    static int Left() { long left = Budget - Total.ElapsedMilliseconds; return left < 1 ? 1 : (int)left; }
    static int Count(string text, string needle) {
        int count = 0, at = 0;
        while (true) {
            int hit = text.IndexOf(needle, at, StringComparison.Ordinal);
            if (hit < 0) return count;
            count++;
            at = hit + needle.Length;
        }
    }

    internal static int Run(string outDir) {
        Folder = Path.GetFullPath(outDir);
        Directory.CreateDirectory(Folder);
        Report.Append("# MermaidOutputProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        Total.Start();
        Thread thread = new Thread(RunOnSta);
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Check("budget: probe finished inside " + Budget + " ms", Total.ElapsedMilliseconds < Budget, Total.ElapsedMilliseconds + " ms");
        WriteReport();
        return All ? 0 : 1;
    }

    static void RunOnSta() {
        try {
            InfoChecks();
            // Warm-up: the first render pays for the resource inflate, chakra.dll, the bundle and the JIT
            // of the engine's P/Invokes, well past the 3 s a fence render may wait. Bring that up here with
            // a longer wait so the feature's own 3 s limit is measured against a live engine.
            MermaidResult warm = MermaidEngine.RenderSync("flowchart TD\n  A[시작] --> B[끝]\n", Family, 16, 1.0, Math.Min(15000, Left()));
            Info("warm-up: " + Describe(warm) + ", unavailable=" + MermaidEngine.Unavailable
                + (MermaidEngine.Unavailable ? ", startup error: " + MermaidEngine.StartupError : string.Empty));
            PictureChecks();
            SanitizeChecks();
            HtmlChecks();
            PrintChecks();
            RichCopyChecks();
            BudgetChecks();
        } catch (Exception ex) {
            All = false;
            Report.Append("FAIL probe exception: ").Append(ex).Append('\n');
        }
    }

    static string Describe(MermaidResult result) {
        if (result == null) return "null";
        if (result.Error != null) return "error=" + result.Error;
        return N(result.Width) + "x" + N(result.Height) + ", " + (result.Svg == null ? 0 : result.Svg.Length) + " chars";
    }

    // ---- a: the fence info string -----------------------------------------------------------

    static void InfoChecks() {
        Check("a. \"mermaid\"", MermaidOutput.IsMermaidInfo("mermaid"), null);
        Check("a. \" Mermaid \"", MermaidOutput.IsMermaidInfo(" Mermaid "), null);
        Check("a. \"mermaid title\"", MermaidOutput.IsMermaidInfo("mermaid title"), null);
        Check("a. empty is not mermaid", !MermaidOutput.IsMermaidInfo(""), null);
        Check("a. null is not mermaid", !MermaidOutput.IsMermaidInfo(null), null);
        Check("a. \"js\" is not mermaid", !MermaidOutput.IsMermaidInfo("js"), null);
        Check("a. \"mermaidx\" is not mermaid", !MermaidOutput.IsMermaidInfo("mermaidx"), null);
    }

    // ---- b, c: the print picture ------------------------------------------------------------

    static string Flowchart() {
        return "flowchart TD\n  A[시작] --> B{조건 확인}\n  B -->|예| C[처리]\n  B -->|아니오| D[종료]\n  C --> D\n";
    }
    // A left-to-right chain of 25 labelled nodes: far wider than the 680 DIP display cap.
    static string WideFlowchart() {
        var text = new StringBuilder("flowchart LR\n  ");
        for (int i = 0; i < 25; i++) {
            if (i > 0) text.Append(" --> ");
            text.Append('N').Append(i).Append("[노드 ").Append(i).Append(']');
        }
        return text.ToString();
    }
    static void PictureChecks() {
        // The engine's own size, for comparison with the display size.
        double svgWidth, svgHeight;
        string svg = MermaidOutput.RenderSvg(Flowchart(), Family, 16, out svgWidth, out svgHeight);
        Check("b. a small flowchart renders to SVG", svg != null && svgWidth > 0 && svgHeight > 0,
            svg == null ? "null" : N(svgWidth) + "x" + N(svgHeight) + ", " + svg.Length + " chars");
        Image light = MermaidOutput.CreatePicture(Flowchart(), Family, 16, false) as Image;
        Image dark = MermaidOutput.CreatePicture(Flowchart(), Family, 16, true) as Image;
        Check("b. light picture is an Image of a frozen DrawingImage", Good(light), Describe(light));
        Check("b. dark picture is an Image of a frozen DrawingImage", Good(dark), Describe(dark));
        // Whether the theme reached the drawing depends on the SVG using Pele's variables; reported, not required.
        Info("b. light and dark drawings " + (light != null && dark != null && SameDrawing(light, dark) ? "are identical" : "differ"));

        double wideSvgWidth, wideSvgHeight;
        MermaidOutput.RenderSvg(WideFlowchart(), Family, 16, out wideSvgWidth, out wideSvgHeight);
        Image wide = MermaidOutput.CreatePicture(WideFlowchart(), Family, 16, false) as Image;
        Check("b. a 2000-px-wide diagram is scaled to the display width", wide != null && wide.Width <= MermaidOutput.MaxDisplayWidth && wide.Width > 0 && wide.Height > 0,
            wide == null ? "null" : "svg w=" + N(wideSvgWidth) + " -> picture " + N(wide.Width) + "x" + N(wide.Height));
        Check("b. the scaled height keeps the diagram's proportions",
            wide != null && wideSvgWidth > 0 && wideSvgHeight > 0 && wide.Height > 0
                && Math.Abs(wide.Width / wide.Height - wideSvgWidth / wideSvgHeight) < 0.05,
            wide == null ? "null" : N(wide.Width) + "x" + N(wide.Height) + " vs " + N(wideSvgWidth) + "x" + N(wideSvgHeight));

        // c: a source Pele rejects draws no picture (the caller then keeps the code block).
        FrameworkElement broken = MermaidOutput.CreatePicture("flowchart TD\n  B[[[", Family, 16, false);
        Check("c. a broken source returns null", broken == null, broken == null ? "null" : "a picture");
        Check("c. an empty source returns null", MermaidOutput.CreatePicture("   ", Family, 16, false) == null, null);
        string over = "flowchart TD\n" + new string('a', MermaidEngine.MaxSourceChars);
        Check("c. a source over the engine cap returns null", MermaidOutput.CreatePicture(over, Family, 16, false) == null, null);
    }
    static bool Good(Image image) {
        var source = image == null ? null : image.Source as DrawingImage;
        return source != null && source.IsFrozen && image.Width > 0 && image.Height > 0 && source.Drawing != null;
    }
    static string Describe(Image image) {
        if (image == null) return "null";
        var source = image.Source as DrawingImage;
        Rect bounds = source == null || source.Drawing == null ? Rect.Empty : source.Drawing.Bounds;
        return N(image.Width) + "x" + N(image.Height) + ", frozen=" + (source != null && source.IsFrozen)
            + ", bounds=" + N(bounds.Width) + "x" + N(bounds.Height);
    }
    // Two pictures differ when either one has a colour the other lacks (the theme overrode the SVG's own).
    static bool SameDrawing(Image a, Image b) {
        string first = DrawingText(a), second = DrawingText(b);
        return first.Length > 0 && string.Equals(first, second, StringComparison.Ordinal);
    }
    static string DrawingText(Image image) {
        var source = image == null ? null : image.Source as DrawingImage;
        if (source == null || source.Drawing == null) return string.Empty;
        var text = new StringBuilder();
        Collect(source.Drawing, text, 0);
        return text.ToString();
    }
    static void Collect(Drawing drawing, StringBuilder text, int depth) {
        if (depth > 24 || drawing == null) return;
        var group = drawing as DrawingGroup;
        if (group != null) {
            foreach (Drawing child in group.Children) Collect(child, text, depth + 1);
            return;
        }
        var geometry = drawing as GeometryDrawing;
        if (geometry == null) return;
        var solid = geometry.Brush as SolidColorBrush;
        if (solid != null) text.Append(solid.Color.ToString());
        var pen = geometry.Pen;
        if (pen != null && pen.Brush != null) {
            var stroke = pen.Brush as SolidColorBrush;
            if (stroke != null) text.Append(stroke.Color.ToString());
        }
    }

    // ---- d: the HTML sanitizer --------------------------------------------------------------

    const string Hostile =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 120 60\" width=\"120\" height=\"60\" onload=\"alert(1)\">" +
        "<script>alert(2)</script>" +
        "<foreignObject><div>x</div></foreignObject>" +
        "<rect x=\"1\" y=\"1\" width=\"10\" height=\"10\" fill=\"#f00\" onload=\"alert(3)\" style=\"background:url(x)\"/>" +
        "<a href=\"javascript:alert(4)\"><text x=\"2\" y=\"18\">link</text></a>" +
        "<image href=\"x.png\" width=\"8\" height=\"8\"/>" +
        "<text x=\"2\" y=\"40\" font-size=\"12\">safe</text>" +
        "</svg>";
    static void SanitizeChecks() {
        string clean = MermaidOutput.SanitizeSvg(Hostile);
        Check("d. a hostile SVG sanitizes", clean != null, clean == null ? "null" : clean.Length + " chars");
        if (clean != null) {
            Check("d. no script survives", clean.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0, null);
            Check("d. no on* attribute survives", clean.IndexOf("onload", StringComparison.OrdinalIgnoreCase) < 0, null);
            Check("d. no foreignObject survives", clean.IndexOf("foreignObject", StringComparison.OrdinalIgnoreCase) < 0, null);
            Check("d. no javascript: survives", clean.IndexOf("javascript:", StringComparison.OrdinalIgnoreCase) < 0, null);
            Check("d. no image element survives", clean.IndexOf("<image", StringComparison.OrdinalIgnoreCase) < 0, null);
            Check("d. no url() style survives", clean.IndexOf("url(", StringComparison.OrdinalIgnoreCase) < 0, null);
            Check("d. the rect and its text survive",
                clean.IndexOf("<rect", StringComparison.Ordinal) >= 0 && clean.IndexOf(">safe<", StringComparison.Ordinal) >= 0, null);
        }
        Check("d. broken XML sanitizes to null", MermaidOutput.SanitizeSvg("<svg><rect></svg>") == null, null);
        Check("d. an empty string sanitizes to null", MermaidOutput.SanitizeSvg("") == null, null);
        string svg;
        double width, height;
        svg = MermaidOutput.RenderSvg(Flowchart(), Family, 16, out width, out height);
        string real = svg == null ? null : MermaidOutput.SanitizeSvg(svg);
        Check("d. a real render survives the sanitizer",
            real != null && real.Length > 0
                && (real.IndexOf("<path", StringComparison.Ordinal) >= 0 || real.IndexOf("<rect", StringComparison.Ordinal) >= 0)
                && real.IndexOf("<text", StringComparison.Ordinal) >= 0,
            real == null ? "null" : real.Length + " chars");
        // The labels are the diagram's content: the sanitizer must keep their text, Korean included.
        Check("d. the Korean labels survive the sanitizer",
            real != null && real.IndexOf("조건 확인", StringComparison.Ordinal) >= 0 && real.IndexOf("종료", StringComparison.Ordinal) >= 0,
            real == null ? "null" : "label text found=" + (real.IndexOf("조건 확인", StringComparison.Ordinal) >= 0));
        // A paint reference whose def the rebuild drops: the colour after it stays so the shape is drawn.
        string fallback = MermaidOutput.SanitizeSvg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">" +
            "<path d=\"M0 0L10 10\" fill=\"url(#g) #4e79a7\"/>" +
            "</svg>");
        Check("d. a url() paint keeps its colour fallback",
            fallback != null && fallback.IndexOf("url(", StringComparison.OrdinalIgnoreCase) < 0 && fallback.IndexOf("#4e79a7", StringComparison.Ordinal) >= 0,
            fallback == null ? "null" : fallback);
        // A style that would lay the picture over the whole exported page.
        string overlay = MermaidOutput.SanitizeSvg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\" " +
            "style=\"position:fixed;inset:0;z-index:99999;width:100vw;height:100vh;background:red\">" +
            "<rect x=\"1\" y=\"1\" width=\"8\" height=\"8\" fill=\"#f00\"/>" +
            "</svg>");
        Check("d. an overlay style does not survive",
            overlay != null && overlay.IndexOf("style=", StringComparison.OrdinalIgnoreCase) < 0 && overlay.IndexOf("position", StringComparison.OrdinalIgnoreCase) < 0
                && overlay.IndexOf("z-index", StringComparison.OrdinalIgnoreCase) < 0 && overlay.IndexOf("inset", StringComparison.OrdinalIgnoreCase) < 0
                && overlay.IndexOf("100vw", StringComparison.OrdinalIgnoreCase) < 0 && overlay.IndexOf("background", StringComparison.OrdinalIgnoreCase) < 0
                && overlay.IndexOf("<rect", StringComparison.Ordinal) >= 0,
            overlay == null ? "null" : overlay);
        string mixed = MermaidOutput.SanitizeSvg(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\" " +
            "style=\"--pele-bg:#fff;position:fixed;top:0;fill:#f00;height:100vh;font-family:'a;z-index:9'\">" +
            "<rect x=\"1\" y=\"1\" width=\"8\" height=\"8\" style=\"rx:var(--_r);left:0\"/>" +
            "</svg>");
        Check("d. a mixed style keeps only its custom and presentation declarations",
            mixed != null && mixed.IndexOf("--pele-bg:#fff;fill:#f00", StringComparison.Ordinal) >= 0 && mixed.IndexOf("rx:var(--_r)", StringComparison.Ordinal) >= 0
                && mixed.IndexOf("position", StringComparison.OrdinalIgnoreCase) < 0 && mixed.IndexOf("top:", StringComparison.OrdinalIgnoreCase) < 0
                && mixed.IndexOf("left:", StringComparison.OrdinalIgnoreCase) < 0 && mixed.IndexOf("100vh", StringComparison.OrdinalIgnoreCase) < 0
                && mixed.IndexOf("z-index", StringComparison.OrdinalIgnoreCase) < 0 && mixed.IndexOf("font-family", StringComparison.OrdinalIgnoreCase) < 0,
            mixed == null ? "null" : mixed);
        // Pele's own root style: every custom property (--_bg, --_fg, ...) must reach the page, or the
        // var(--_x) paints of the shapes resolve to nothing. What the rule drops is listed.
        var droppedList = new StringBuilder();
        int droppedCount = 0;
        MermaidOutput.DiagStyleDropped = delegate(string declaration) {
            droppedCount++;
            if (droppedList.Length < 600 && droppedList.ToString().IndexOf("[" + declaration + "]", StringComparison.Ordinal) < 0) droppedList.Append('[').Append(declaration).Append(']');
        };
        string rootClean;
        try { rootClean = svg == null ? null : MermaidOutput.SanitizeSvg(svg); }
        finally { MermaidOutput.DiagStyleDropped = null; }
        string rootStyle = RootStyle(svg), cleanStyle = RootStyle(rootClean);
        int custom = 0, customKept = 0;
        string missing = "";
        if (rootStyle != null) {
            foreach (string part in rootStyle.Split(';')) {
                string declaration = part.Trim();
                int colon = declaration.IndexOf(':');
                if (colon <= 2 || !declaration.StartsWith("--", StringComparison.Ordinal)) continue;
                custom++;
                string name = declaration.Substring(0, colon).Trim();
                if (cleanStyle != null && (cleanStyle.StartsWith(name + ":", StringComparison.Ordinal) || cleanStyle.IndexOf(";" + name + ":", StringComparison.Ordinal) >= 0)) customKept++;
                else missing += " " + name;
            }
        }
        Check("d. a real render's root keeps every custom property", custom > 0 && customKept == custom,
            "custom=" + custom + " kept=" + customKept + (missing.Length > 0 ? " missing:" + missing : ""));
        Check("d. a real render's root keeps no layout declaration",
            cleanStyle != null && cleanStyle.IndexOf("max-width", StringComparison.OrdinalIgnoreCase) < 0 && cleanStyle.IndexOf("height", StringComparison.OrdinalIgnoreCase) < 0
                && cleanStyle.IndexOf("position", StringComparison.OrdinalIgnoreCase) < 0,
            cleanStyle == null ? "null" : cleanStyle.Length + " chars");
        Info("d. real root style: " + (rootStyle ?? "null"));
        Info("d. sanitized root style: " + (cleanStyle ?? "null"));
        Info("d. declarations dropped from the real render (" + droppedCount + "): " + droppedList);
    }
    // The first style attribute of the markup, which is the root element's when it has one (entities left as written).
    static string RootStyle(string svg) {
        if (svg == null) return null;
        int close = svg.IndexOf('>');
        int at = svg.IndexOf(" style=\"", StringComparison.Ordinal);
        if (close < 0 || at < 0 || at > close) return null;
        at += 8;
        int end = svg.IndexOf('"', at);
        return end < 0 ? null : svg.Substring(at, end - at);
    }

    // ---- g: the render cache and one document's time budget -----------------------------------

    static string BudgetMd(string tag) {
        var text = new StringBuilder("앞 문단\n\n");
        for (int i = 1; i <= 6; i++)
            text.Append("```mermaid\nflowchart TD\n  A[").Append(tag).Append(' ').Append(i).Append("] --> B[끝 ").Append(i).Append("]\n```\n\n");
        return text.Append("뒤 문단\n").ToString();
    }
    static void BudgetChecks() {
        // The same document again: every block comes from the cache, the engine is not asked.
        long calls = MermaidOutput.DiagEngineCalls, hits = MermaidOutput.DiagCacheHits;
        string again = HtmlExporter.Convert(MermaidMd, "", Path.Combine(Folder, "g-again.html"), "ko");
        Check("g. a second export of the same block uses the cache",
            Count(again, "<figure class=\"mermaid\">") == 1 && MermaidOutput.DiagEngineCalls == calls && MermaidOutput.DiagCacheHits > hits,
            "figures=" + Count(again, "<figure class=\"mermaid\">") + " engine calls +" + (MermaidOutput.DiagEngineCalls - calls) + " cache hits +" + (MermaidOutput.DiagCacheHits - hits));

        // Six blocks against a slow engine (DiagDelayMilliseconds is a probe seam: a pause before every
        // engine call) and a shortened budget: the document ends within budget + 1 s, the first blocks
        // are pictures and the rest fall back to code without the engine.
        int budget = MermaidOutput.DocumentBudgetMilliseconds;
        Check("g. the document budget is 8 s", budget == 8000, budget + " ms");
        try {
            MermaidOutput.DocumentBudgetMilliseconds = 2000;
            MermaidOutput.DiagDelayMilliseconds = 600;
            long skips = MermaidOutput.DiagBudgetSkips;
            calls = MermaidOutput.DiagEngineCalls;
            Stopwatch watch = Stopwatch.StartNew();
            string html = HtmlExporter.Convert(BudgetMd("예산"), "", Path.Combine(Folder, "g-budget.html"), "ko");
            watch.Stop();
            int figures = Count(html, "<figure class=\"mermaid\">"), code = Count(html, "language-mermaid");
            Check("g. html: six slow blocks end within budget + 1 s", watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds + " ms");
            Check("g. html: the first blocks are figures, the rest code blocks", figures >= 1 && figures < 6 && figures + code == 6,
                "figures=" + figures + " code=" + code);
            Check("g. html: blocks past the budget do not reach the engine",
                MermaidOutput.DiagBudgetSkips > skips && MermaidOutput.DiagEngineCalls - calls <= figures + 1,
                "skips +" + (MermaidOutput.DiagBudgetSkips - skips) + " engine calls +" + (MermaidOutput.DiagEngineCalls - calls));

            skips = MermaidOutput.DiagBudgetSkips;
            watch = Stopwatch.StartNew();
            FlowDocument document = Markdown.Parse(BudgetMd("인쇄"), 16, Family, Brushes.Black, false, "");
            watch.Stop();
            int pictures = 0, codeParagraphs = 0;
            foreach (Block block in document.Blocks) {
                var found = block as BlockUIContainer;
                if (found != null && found.Child is Image) pictures++;
                var paragraph = block as Paragraph;
                if (paragraph != null && paragraph.FontFamily != null && paragraph.FontFamily.Source == "Consolas, Malgun Gothic") codeParagraphs++;
            }
            Check("g. print: six slow blocks end within budget + 1 s", watch.ElapsedMilliseconds < 3000, watch.ElapsedMilliseconds + " ms");
            Check("g. print: the first blocks are pictures, the rest code paragraphs",
                pictures >= 1 && pictures < 6 && pictures + codeParagraphs == 6 && MermaidOutput.DiagBudgetSkips > skips,
                "pictures=" + pictures + " code=" + codeParagraphs + " skips +" + (MermaidOutput.DiagBudgetSkips - skips));
        } finally {
            MermaidOutput.DocumentBudgetMilliseconds = budget;
            MermaidOutput.DiagDelayMilliseconds = 0;
        }
        // Outside the slow run the same document is all pictures again (nothing was remembered as failed).
        string after = HtmlExporter.Convert(BudgetMd("예산"), "", Path.Combine(Folder, "g-after.html"), "ko");
        Check("g. a block that ran out of budget is drawn on the next export", Count(after, "<figure class=\"mermaid\">") == 6,
            "figures=" + Count(after, "<figure class=\"mermaid\">"));
    }

    // ---- e: the HTML export -----------------------------------------------------------------

    const string MermaidMd =
        "# 제목\n\n" +
        "```mermaid\nflowchart TD\n  A[시작] --> B[끝]\n```\n\n" +
        "```js\nconsole.log(1);\n```\n";
    const string BrokenMd =
        "앞 문단\n\n" +
        "```mermaid\nflowchart TD\n  B[[[\n```\n\n" +
        "뒤 문단\n";
    static void HtmlChecks() {
        string html = HtmlExporter.Convert(MermaidMd, "", Path.Combine(Folder, "e.html"), "ko");
        int figures = Count(html, "<figure class=\"mermaid\">");
        Check("e. one mermaid block becomes one figure", figures == 1, "figures=" + figures);
        int figureStart = html.IndexOf("<figure class=\"mermaid\">", StringComparison.Ordinal);
        int figureEnd = figureStart < 0 ? -1 : html.IndexOf("</figure>", figureStart, StringComparison.Ordinal);
        string figure = figureStart < 0 || figureEnd < 0 ? "" : html.Substring(figureStart, figureEnd - figureStart);
        Check("e. the figure holds an inline SVG", figure.IndexOf("<svg", StringComparison.Ordinal) >= 0, figure.Length + " chars");
        Check("e. the sanitized SVG carries no script or handler",
            figure.IndexOf("<script", StringComparison.OrdinalIgnoreCase) < 0 && figure.IndexOf("onload", StringComparison.OrdinalIgnoreCase) < 0, null);
        Check("e. the js block is still one code block", Count(html, "language-js") == 1, "count=" + Count(html, "language-js"));
        Check("e. the figure rule is in the page CSS", html.IndexOf("figure.mermaid{margin:1em 0;text-align:center;--pele-surface-alt:" + MermaidRender.LightSurfaceAlt + "}", StringComparison.Ordinal) >= 0, null);
        Check("e. no raw mermaid code block is left", Count(html, "language-mermaid") == 0, "count=" + Count(html, "language-mermaid"));

        string broken = HtmlExporter.Convert(BrokenMd, "", Path.Combine(Folder, "e-broken.html"), "ko");
        Check("e. a broken mermaid block stays a code block", Count(broken, "language-mermaid") == 1, "count=" + Count(broken, "language-mermaid"));
        Check("e. a broken block makes no figure", Count(broken, "<figure class=\"mermaid\">") == 0, null);

        // A fence that is not mermaid must be byte-identical to the block the export wrote before.
        // (Convert returns CRLF, so the comparison is made on the LF form.)
        string plain = HtmlExporter.Convert("```\nx < y\n```\n", "", Path.Combine(Folder, "e-plain.html"), "ko").Replace("\r\n", "\n");
        Check("e. a plain fence is the same code block as before",
            plain.IndexOf("<pre><code>x &lt; y</code></pre>\n", StringComparison.Ordinal) >= 0, null);
        string tagged = HtmlExporter.Convert("```mermaidx\nx\n```\n", "", Path.Combine(Folder, "e-tag.html"), "ko");
        Check("e. \"mermaidx\" is not a mermaid fence",
            tagged.IndexOf("class=\"language-mermaidx\"", StringComparison.Ordinal) >= 0
                && tagged.IndexOf("<figure class=\"mermaid\">", StringComparison.Ordinal) < 0, null);
    }

    // ---- f: the print / rich-copy FlowDocument ----------------------------------------------

    const string PrintMd =
        "앞 문단\n\n" +
        "```mermaid\nflowchart TD\n  A[시작] --> B[끝]\n```\n\n" +
        "뒤 문단\n";
    static void PrintChecks() {
        // The entry CreatePrintDocument calls for a Markdown document (Printing.cs line 13).
        FlowDocument document = Markdown.Parse(PrintMd, 16, Family, Brushes.Black, false, "");
        int pictures = 0;
        BlockUIContainer container = null;
        foreach (Block block in document.Blocks) {
            var found = block as BlockUIContainer;
            if (found == null || !(found.Child is Image)) continue;
            pictures++;
            container = found;
        }
        Check("f. one mermaid block is one BlockUIContainer holding an Image", pictures == 1, "pictures=" + pictures);
        if (container != null) {
            var image = (Image)container.Child;
            var source = image.Source as DrawingImage;
            Check("f. the picture has a size and a frozen DrawingImage", image.Width > 0 && image.Height > 0 && source != null && source.IsFrozen,
                Describe(image));
            Check("f. the picture is centered with the math block's margin",
                image.HorizontalAlignment == HorizontalAlignment.Center
                    && container.Margin.Left == 0 && container.Margin.Top == 8 && container.Margin.Right == 0 && container.Margin.Bottom == 12,
                "align=" + image.HorizontalAlignment + ", margin=" + container.Margin);
        }
        // The same document's non-mermaid blocks are untouched: the two text paragraphs around the picture.
        // (A blank source line is an empty Paragraph block, so only the ones carrying text are counted.)
        int paragraphs = 0;
        foreach (Block block in document.Blocks) {
            var paragraph = block as Paragraph;
            if (paragraph == null) continue;
            string text = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim();
            if (text.Length > 0) paragraphs++;
        }
        Check("f. the paragraphs around the picture are still paragraphs", paragraphs == 2, "paragraphs=" + paragraphs);

        // A broken mermaid fence falls back to the code paragraph the parser drew before.
        FlowDocument fallback = Markdown.Parse("```mermaid\nflowchart TD\n  B[[[\n```\n", 16, Family, Brushes.Black, false, "");
        int fallbackPictures = 0;
        bool codeParagraph = false;
        foreach (Block block in fallback.Blocks) {
            var found = block as BlockUIContainer;
            if (found != null && found.Child is Image) fallbackPictures++;
            var paragraph = block as Paragraph;
            if (paragraph != null && paragraph.FontFamily != null && paragraph.FontFamily.Source == "Consolas, Malgun Gothic") codeParagraph = true;
        }
        Check("f. a broken mermaid fence stays a code paragraph",
            codeParagraph && fallbackPictures == 0, "code=" + codeParagraph + ", pictures=" + fallbackPictures);

        // A document without a mermaid fence pays nothing: no picture, and the code fence is unchanged.
        FlowDocument ordinary = Markdown.Parse("```js\nconsole.log(1);\n```\n", 16, Family, Brushes.Black, false, "");
        bool ordinaryCode = false;
        int ordinaryPictures = 0;
        foreach (Block block in ordinary.Blocks) {
            var found = block as BlockUIContainer;
            if (found != null && found.Child is Image) ordinaryPictures++;
            var paragraph = block as Paragraph;
            if (paragraph != null && paragraph.FontFamily != null && paragraph.FontFamily.Source == "Consolas, Malgun Gothic") ordinaryCode = true;
        }
        Check("f. a fence that is not mermaid draws no picture and stays code", ordinaryPictures == 0 && ordinaryCode,
            "pictures=" + ordinaryPictures + ", code=" + ordinaryCode);
    }

    // ---- h: the rich copy (RTF) document ------------------------------------------------------

    // WPF's RTF writer drops every UI container, so with Markdown.RichCopy set the parser writes the
    // formula, diagram, check box and key as text. The mermaid source is used nowhere else in this probe,
    // so an unchanged engine/cache count means MermaidOutput was not entered at all.
    const string RichMd =
        "앞 문단\n\n" +
        "```mermaid\nflowchart LR\n  R1[rich copy only] --> R2[text]\n```\n\n" +
        "$$x^2$$\n\n" +
        "inline $a+b$ here\n\n" +
        "- [ ] todo\n" +
        "- [x] done\n\n" +
        "<kbd>Ctrl</kbd> key\n\n" +
        "---\n\n" +
        "뒤 문단\n";
    static void CountContainers(BlockCollection blocks, ref int blockContainers, ref int inlineContainers, ref int mathInlines, ref int boxes) {
        foreach (Block block in blocks) {
            if (block is BlockUIContainer) blockContainers++;
            var section = block as Section;
            if (section != null) CountContainers(section.Blocks, ref blockContainers, ref inlineContainers, ref mathInlines, ref boxes);
            // Task items live inside List → ListItem, cells inside Table → row groups → rows → cells.
            var list = block as List;
            if (list != null) foreach (ListItem item in list.ListItems) CountContainers(item.Blocks, ref blockContainers, ref inlineContainers, ref mathInlines, ref boxes);
            var table = block as Table;
            if (table != null) foreach (TableRowGroup group in table.RowGroups) foreach (TableRow row in group.Rows) foreach (TableCell cell in row.Cells) CountContainers(cell.Blocks, ref blockContainers, ref inlineContainers, ref mathInlines, ref boxes);
            var paragraph = block as Paragraph;
            if (paragraph != null) CountInlineContainers(paragraph.Inlines, ref inlineContainers, ref mathInlines, ref boxes);
        }
    }
    static void CountInlineContainers(InlineCollection inlines, ref int inlineContainers, ref int mathInlines, ref int boxes) {
        foreach (Inline inline in inlines) {
            var span = inline as Span;
            if (span != null) { CountInlineContainers(span.Inlines, ref inlineContainers, ref mathInlines, ref boxes); continue; }
            var container = inline as InlineUIContainer;
            if (container == null) continue;
            inlineContainers++;
            if (object.Equals(container.Tag, "math")) mathInlines++;
            if (object.Equals(container.Tag, "marker") && container.Child is CheckBox) boxes++;
        }
    }
    static void RichCopyChecks() {
        int renders = MermaidEngine.DiagRenders;
        long calls = MermaidOutput.DiagEngineCalls, hits = MermaidOutput.DiagCacheHits, skips = MermaidOutput.DiagBudgetSkips;
        FlowDocument rich = null;
        string rtf = "", back = "";
        Markdown.RichCopy = true;
        try {
            rich = Markdown.Parse(RichMd, 16, Family, Brushes.Black, false, "");
            using (var stream = new MemoryStream()) {
                new TextRange(rich.ContentStart, rich.ContentEnd).Save(stream, DataFormats.Rtf);
                rtf = Encoding.ASCII.GetString(stream.ToArray());
                // Read the RTF back the way a paste target does: the check box characters are \uN escapes in the file.
                stream.Position = 0;
                var loaded = new FlowDocument();
                var range = new TextRange(loaded.ContentStart, loaded.ContentEnd);
                range.Load(stream, DataFormats.Rtf);
                back = range.Text;
            }
        } catch (Exception ex) {
            Check("h. rich copy document saves to RTF", false, ex.GetType().Name + ": " + ex.Message);
        } finally { Markdown.RichCopy = false; }
        Check("h. the flag is reset after the copy", !Markdown.RichCopy, null);
        try { File.WriteAllText(Path.Combine(Folder, "h-rich.rtf"), rtf, Encoding.ASCII); } catch (Exception) { }
        // 2.5.0 (user 11-15, Word paste): the source keeps its lines; WPF's RTF writer used to turn them into spaces.
        int flowAt = back.IndexOf("flowchart LR", StringComparison.Ordinal), richAt = back.IndexOf("R1[rich copy only]", StringComparison.Ordinal);
        string between = flowAt >= 0 && richAt > flowAt ? back.Substring(flowAt, richAt - flowAt) : "";
        Check("h. rich copy: the mermaid source keeps its line breaks", between.IndexOf('\n') >= 0 || between.IndexOf('\r') >= 0 || between.IndexOf('\u2028') >= 0 || between.IndexOf('\u000B') >= 0,
            "between=[" + between.Replace("\r", "\\r").Replace("\n", "\\n") + "] rtfLine=" + (rtf.IndexOf("\\line", StringComparison.Ordinal) >= 0));
        Check("h. rich copy: the code font is a name Word can find", rtf.IndexOf("Consolas;", StringComparison.Ordinal) >= 0 && rtf.IndexOf("Consolas, ", StringComparison.Ordinal) < 0, null);
        int blockContainers = 0, inlineContainers = 0, mathInlines = 0, boxes = 0;
        if (rich != null) CountContainers(rich.Blocks, ref blockContainers, ref inlineContainers, ref mathInlines, ref boxes);
        Check("h. rich copy: the document has no UI container", rich != null && blockContainers == 0 && inlineContainers == 0,
            "block=" + blockContainers + " inline=" + inlineContainers);
        Check("h. rich copy: the mermaid source is in the RTF", rtf.IndexOf("flowchart", StringComparison.Ordinal) >= 0 && back.IndexOf("rich copy only", StringComparison.Ordinal) >= 0, rtf.Length + " chars");
        Check("h. rich copy: the display formula is in the RTF", rtf.IndexOf("$$x^2$$", StringComparison.Ordinal) >= 0, null);
        Check("h. rich copy: the inline formula is in the RTF", rtf.IndexOf("$a+b$", StringComparison.Ordinal) >= 0, null);
        Check("h. rich copy: the key is in the RTF", rtf.IndexOf("Ctrl", StringComparison.Ordinal) >= 0, null);
        Check("h. rich copy: the task texts are in the RTF", rtf.IndexOf("todo", StringComparison.Ordinal) >= 0 && rtf.IndexOf("done", StringComparison.Ordinal) >= 0, null);
        bool open = rtf.IndexOf("\\u9744", StringComparison.Ordinal) >= 0 || rtf.IndexOf('☐') >= 0 || back.IndexOf('☐') >= 0;
        bool closed = rtf.IndexOf("\\u9745", StringComparison.Ordinal) >= 0 || rtf.IndexOf('☑') >= 0 || back.IndexOf('☑') >= 0;
        Check("h. rich copy: both check box characters are in the RTF", open && closed,
            "open=" + open + " closed=" + closed + " read back=" + (back.IndexOf('☐') >= 0) + "/" + (back.IndexOf('☑') >= 0));
        Check("h. rich copy: the text reads back from the RTF",
            back.IndexOf("flowchart", StringComparison.Ordinal) >= 0 && back.IndexOf("x^2", StringComparison.Ordinal) >= 0 && back.IndexOf("a+b", StringComparison.Ordinal) >= 0
                && back.IndexOf("Ctrl", StringComparison.Ordinal) >= 0 && back.IndexOf("todo", StringComparison.Ordinal) >= 0,
            back.Length + " chars");
        Check("h. rich copy: the mermaid engine and its cache are not used",
            MermaidEngine.DiagRenders == renders && MermaidOutput.DiagEngineCalls == calls && MermaidOutput.DiagCacheHits == hits && MermaidOutput.DiagBudgetSkips == skips,
            "renders +" + (MermaidEngine.DiagRenders - renders) + " engine calls +" + (MermaidOutput.DiagEngineCalls - calls)
                + " cache hits +" + (MermaidOutput.DiagCacheHits - hits) + " skips +" + (MermaidOutput.DiagBudgetSkips - skips));

        // The same document without the flag is the print document: pictures and controls as before.
        FlowDocument print = Markdown.Parse(RichMd, 16, Family, Brushes.Black, false, "");
        blockContainers = 0; inlineContainers = 0; mathInlines = 0; boxes = 0;
        CountContainers(print.Blocks, ref blockContainers, ref inlineContainers, ref mathInlines, ref boxes);
        int images = 0;
        foreach (Block block in print.Blocks) {
            var found = block as BlockUIContainer;
            if (found != null && found.Child is Image) images++;
        }
        Check("h. print: the mermaid block, the display formula and the rule are BlockUIContainers", blockContainers == 3 && images >= 1,
            "block=" + blockContainers + " images=" + images);
        Check("h. print: the inline formula, two check boxes and the key are InlineUIContainers", mathInlines == 1 && boxes == 2 && inlineContainers == 4,
            "inline=" + inlineContainers + " math=" + mathInlines + " boxes=" + boxes);
    }

    // ---- report ----------------------------------------------------------------------------

    static void WriteReport() {
        Report.Append('\n').Append("SUMMARY: ").Append(All ? "PASS" : "FAIL").Append('\n');
        Report.Append("NOTES\n");
        Report.Append("- A ```mermaid fence becomes a picture in the print/rich-copy FlowDocument (Markdown.Parse) and in the HTML export (HtmlExporter.Convert); every other fence is untouched.\n");
        Report.Append("- Checks: the info-string rule, the light and dark print picture and its 680 DIP display cap, the broken/empty/oversized fallbacks, the SVG sanitizer against a hostile document, the exported figure, and the FlowDocument block.\n");
        string text = Report.ToString();
        try { File.WriteAllText(Path.Combine(Folder, "mermaid-output-probe.txt"), text, new UTF8Encoding(false)); } catch (Exception) { }
        string pass = Path.Combine(Folder, "mermaid-output-probe-PASS.txt");
        string fail = Path.Combine(Folder, "mermaid-output-probe-FAIL.txt");
        try { if (File.Exists(All ? fail : pass)) File.Delete(All ? fail : pass); } catch (Exception) { }
        try { File.WriteAllText(All ? pass : fail, text, new UTF8Encoding(false)); } catch (Exception) { }
    }
}
}
