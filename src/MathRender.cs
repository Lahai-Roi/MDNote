using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {
// 2.2.0: LaTeX math display through WPF-Math (WpfMath.dll + XamlMath.Shared.dll).
// Nothing in this file's always-used types names a WpfMath type: every WpfMath reference sits in
// MathEngine, whose methods are only entered after a document actually holds a formula, so a
// document without math never loads the two assemblies (MathProbe checks the loaded-assembly list).
internal sealed class MathVisual {
    // The formula drawn at (OffsetX, OffsetY) from the object's top-left; the baseline is Ascent below the top.
    internal Drawing Drawing;
    internal double Width, Ascent, Descent, OffsetX;
    internal bool Error;
    internal string Source, Message;
    internal double Height { get { return Ascent + Descent; } }
}
static class MathRender {
    // Lines after a block opener, the closer included. A block longer than this is not math.
    internal const int BlockLines = 80;
    // An inline formula is searched for its closing "$" at most this far from the opening one.
    internal const int InlineLimit = 1000;
    internal const int SourceLimit = 2000;
    // Probe counters: real parses, real renders, cache hits.
    internal static long DiagParses, DiagRenders, DiagCacheHits;
    internal static string LastError;
    // True once any math syntax (an inline formula, a block opener or a "$$" / "```math" line) was seen in this process.
    // A document without any keeps every hook of the live view on its old, cheaper path.
    internal static bool Seen;

    // ---- syntax (pure text, no WpfMath) -------------------------------------------------

    // A line that is exactly "$$" (up to three leading spaces, trailing blanks allowed).
    internal static bool IsDollarLine(string text) {
        if (text == null) return false;
        int p = 0;
        while (p < text.Length && p < 4 && text[p] == ' ') p++;
        if (p > 3 || p + 2 > text.Length || text[p] != '$' || text[p + 1] != '$') return false;
        for (int i = p + 2; i < text.Length; i++) if (text[i] != ' ' && text[i] != '\t') return false;
        return true;
    }
    // "```math" / "~~~math" (up to three leading spaces, the info string is exactly "math").
    internal static bool IsMathFence(string text, out char fence, out int length) {
        fence = '\0'; length = 0;
        if (text == null) return false;
        int p = 0;
        while (p < text.Length && p < 4 && text[p] == ' ') p++;
        if (p > 3 || p >= text.Length || (text[p] != '`' && text[p] != '~')) return false;
        char marker = text[p];
        int start = p;
        while (p < text.Length && text[p] == marker) p++;
        if (p - start < 3) return false;
        if (!string.Equals(text.Substring(p).Trim(), "math", StringComparison.OrdinalIgnoreCase)) return false;
        fence = marker; length = p - start;
        return true;
    }
    // One line "$$ x $$": the formula between the two pairs, trimmed and without a "$$" of its own.
    internal static bool OneLineDisplay(string text, out string latex) {
        latex = null;
        if (text == null) return false;
        int p = 0;
        while (p < text.Length && p < 4 && text[p] == ' ') p++;
        if (p > 3) return false;
        string body = text.Substring(p).TrimEnd(' ', '\t');
        if (body.Length < 5 || !body.StartsWith("$$", StringComparison.Ordinal) || !body.EndsWith("$$", StringComparison.Ordinal)) return false;
        string inner = body.Substring(2, body.Length - 4);
        if (inner.IndexOf("$$", StringComparison.Ordinal) >= 0) return false;
        inner = inner.Trim();
        if (inner.Length == 0) return false;
        latex = inner;
        return true;
    }
    // The closing "$" of an inline formula opened at text[open], or -1. Typora / Pandoc /
    // markdown-it-katex rules: the opener is not followed by a blank, the closer is not preceded
    // by a blank and not followed by a digit, "\$" is a literal dollar, "$$" never belongs to an
    // inline formula, the formula is not empty and stays on the line. "$5 and $10" is text.
    internal static int InlineEnd(string text, int open, ref int budget) {
        if (open + 1 >= text.Length) return -1;
        char next = text[open + 1];
        if (next == ' ' || next == '\t' || next == '$') return -1;
        int stop = Math.Min(text.Length, open + 1 + InlineLimit);
        for (int j = open + 1; j < stop; j++) {
            budget--;
            char c = text[j];
            if (c == '\\') { j++; continue; }
            if (c != '$') continue;
            if (j + 1 < text.Length && text[j + 1] == '$') return -1;
            char before = text[j - 1];
            if (before == ' ' || before == '\t') continue;
            if (j + 1 < text.Length && text[j + 1] >= '0' && text[j + 1] <= '9') continue;
            return j;
        }
        return -1;
    }
    // Guard against input that would drive the recursive parser too deep (a stack overflow cannot be caught).
    internal static bool LooksSafe(string latex) {
        if (string.IsNullOrWhiteSpace(latex) || latex.Length > SourceLimit) return false;
        int depth = 0;
        for (int i = 0; i < latex.Length; i++) {
            char c = latex[i];
            if (c == '\\') { i++; continue; }
            if (c == '{') { if (++depth > 40) return false; }
            else if (c == '}') depth--;
        }
        return true;
    }

    // ---- parse / render cache -----------------------------------------------------------

    // latex -> parsed TexFormula (typed object here so this class names no WpfMath type); null = failed.
    static readonly Dictionary<string, object> parsed = new Dictionary<string, object>(StringComparer.Ordinal);
    sealed class Entry { internal string Key; internal MathVisual Visual; }
    static readonly Dictionary<string, LinkedListNode<Entry>> visuals = new Dictionary<string, LinkedListNode<Entry>>(StringComparer.Ordinal);
    static readonly LinkedList<Entry> lru = new LinkedList<Entry>();
    const int VisualCapacity = 300;
    internal static int CachedVisuals { get { return visuals.Count; } }
    internal static void ClearCaches() { parsed.Clear(); visuals.Clear(); lru.Clear(); }

    // True when the source parses. A failed parse is remembered, so a redraw never parses it again.
    internal static bool Check(string latex) {
        if (!LooksSafe(latex)) return false;
        object entry;
        if (parsed.TryGetValue(latex, out entry)) return entry != null;
        if (parsed.Count >= 512) parsed.Clear();
        try {
            DiagParses++;
            entry = MathEngine.Parse(latex);
        } catch (Exception ex) {
            // WpfMath throws its own TexException family, KeyNotFoundException for an unknown
            // symbol and IO/load errors when the assemblies are missing: all mean "show the source".
            entry = null;
            LastError = ex.GetType().Name + ": " + ex.Message;
        }
        parsed[latex] = entry;
        return entry != null;
    }
    static Color ColorOf(Brush brush) {
        SolidColorBrush solid = brush as SolidColorBrush;
        return solid == null ? Colors.Black : solid.Color;
    }
    // The drawn formula for one (source, style, size, colour, text font). Never null; Error = the
    // caller shows the source text instead.
    internal static MathVisual Get(string latex, bool display, double size, Brush foreground, string family) {
        if (size < 4) size = 4;
        if (size > 200) size = 200;
        Color color = ColorOf(foreground);
        string key = latex + "\u0001" + (display ? "D" : "T") + size.ToString("0.##", CultureInfo.InvariantCulture) + "\u0001" + color.ToString() + "\u0001" + family;
        LinkedListNode<Entry> node;
        if (visuals.TryGetValue(key, out node)) {
            lru.Remove(node); lru.AddFirst(node);
            DiagCacheHits++;
            return node.Value.Visual;
        }
        MathVisual visual;
        if (!Check(latex)) visual = new MathVisual { Error = true, Source = latex, Message = LastError };
        else {
            try {
                DiagRenders++;
                visual = MathEngine.Build(parsed[latex], latex, display, size, color, family);
            } catch (Exception ex) {
                visual = new MathVisual { Error = true, Source = latex, Message = ex.GetType().Name + ": " + ex.Message };
                LastError = visual.Message;
            }
        }
        while (visuals.Count >= VisualCapacity && lru.Last != null) {
            visuals.Remove(lru.Last.Value.Key);
            lru.RemoveLast();
        }
        visuals[key] = lru.AddFirst(new Entry { Key = key, Visual = visual });
        return visual;
    }

    // ---- print / plain renderer ---------------------------------------------------------

    // A vector picture of the formula for the FlowDocument renderers (print, plain view).
    // Inline pictures sit on the text baseline through a negative bottom margin of the descent.
    internal static FrameworkElement CreatePicture(string latex, bool display, double size, Brush foreground, string family, out double ascent, out double descent) {
        ascent = descent = 0;
        MathVisual visual = Get(latex, display, size, foreground, family);
        if (visual.Error || visual.Drawing == null || visual.Width <= 0 || visual.Height <= 0) return null;
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, visual.Width, visual.Height))));
        var inner = new DrawingGroup();
        inner.Transform = new TranslateTransform(visual.OffsetX, 0);
        inner.Children.Add(visual.Drawing);
        group.Children.Add(inner);
        group.Freeze();
        var picture = new Image { Source = new DrawingImage(group), Width = visual.Width, Height = visual.Height, Stretch = Stretch.None, Focusable = false };
        ascent = visual.Ascent; descent = visual.Descent;
        return picture;
    }
}

// The element for one formula on a live line. The text run is created from the final run
// properties (foreground, size), so a heading, a quote or the dark theme colours the formula
// like the text around it. One visual column: the formula is one caret stop, like a picture.
internal sealed class MathElement : VisualLineElement {
    readonly string latex, family;
    readonly bool display;
    readonly Func<double> available;
    internal MathElement(int documentLength, string latex, bool display, string family, Func<double> available) : base(1, documentLength) {
        this.latex = latex; this.display = display; this.family = family; this.available = available;
    }
    internal string Latex { get { return latex; } }
    internal bool Display { get { return display; } }
    // The drawn formula this element's run uses (the same cache entry), for hit tests against the picture.
    internal MathVisual Picture() {
        TextRunProperties properties = TextRunProperties;
        return properties == null ? null : MathRender.Get(latex, display, properties.FontRenderingEmSize, properties.ForegroundBrush, family);
    }
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
        TextRunProperties properties = TextRunProperties;
        MathVisual visual = MathRender.Get(latex, display, properties.FontRenderingEmSize, properties.ForegroundBrush, family);
        return new MathRun(properties, visual, display, available, DocumentLength);
    }
}
internal sealed class MathRun : TextEmbeddedObject {
    readonly TextRunProperties properties;
    readonly MathVisual visual;
    readonly bool display;
    readonly Func<double> available;
    readonly FormattedText fallback;
    double slot;
    internal MathRun(TextRunProperties properties, MathVisual visual, bool display, Func<double> available, int documentLength) {
        this.properties = properties; this.visual = visual; this.display = display; this.available = available;
        if (visual.Error) {
            fallback = new FormattedText((display ? "$$" : "$") + visual.Source + (display ? "$$" : "$"), properties.CultureInfo, FlowDirection.LeftToRight,
                properties.Typeface, properties.FontRenderingEmSize, properties.ForegroundBrush);
        }
    }
    internal MathVisual Visual { get { return visual; } }
    internal double Slot { get { return slot; } }
    public override int Length { get { return 1; } }
    public override TextRunProperties Properties { get { return properties; } }
    public override CharacterBufferReference CharacterBufferReference { get { return new CharacterBufferReference(); } }
    public override LineBreakCondition BreakBefore { get { return LineBreakCondition.BreakDesired; } }
    public override LineBreakCondition BreakAfter { get { return LineBreakCondition.BreakDesired; } }
    // A display formula fills the line to centre itself, so its width depends on the paragraph width.
    public override bool HasFixedSize { get { return !display; } }
    public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
        if (fallback != null) {
            slot = fallback.WidthIncludingTrailingWhitespace;
            return new TextEmbeddedObjectMetrics(slot, fallback.Height, fallback.Baseline);
        }
        slot = visual.Width;
        if (display) {
            double view = available == null ? 0 : available();
            double room = Math.Min(Math.Max(0, remainingParagraphWidth - 2), Math.Max(0, view));
            if (room > slot) slot = room;
        }
        return new TextEmbeddedObjectMetrics(slot, visual.Height, visual.Ascent);
    }
    public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) {
        if (fallback != null) return new Rect(0, 0, slot, fallback.Height);
        return new Rect(0, 0, slot, visual.Height);
    }
    public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) {
        // origin is the baseline (Format reported fallback.Baseline); DrawText takes the top-left corner.
        if (fallback != null) { drawingContext.DrawText(fallback, new Point(origin.X, origin.Y - fallback.Baseline)); return; }
        if (visual.Drawing == null) return;
        double x = origin.X + visual.OffsetX + (display ? Math.Max(0, (slot - visual.Width) / 2) : 0);
        drawingContext.PushTransform(new TranslateTransform(x, origin.Y - visual.Ascent));
        drawingContext.DrawDrawing(visual.Drawing);
        drawingContext.Pop();
    }
}

// Every WpfMath / XamlMath reference lives here. NoInlining keeps a missing WpfMath.dll a catchable
// FileNotFoundException inside the caller's try instead of a JIT failure of the caller itself.
static class MathEngine {
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static object Parse(string latex) {
        return WpfMath.Parsers.WpfTeXFormulaParser.Instance.Parse(latex, (string)null);
    }
    // Records the root box of a render (its width / height / depth in em units) and draws nothing.
    sealed class Capture : XamlMath.Rendering.IElementRenderer {
        internal XamlMath.Boxes.Box Root;
        public void RenderCharacter(XamlMath.CharInfo character, double x, double y, XamlMath.Rendering.IBrush foreground) { }
        public void RenderElement(XamlMath.Boxes.Box box, double x, double y) { if (Root == null) Root = box; }
        public void RenderLine(XamlMath.Rendering.Point start, XamlMath.Rendering.Point end, XamlMath.Rendering.IBrush foreground) { }
        public void RenderRectangle(XamlMath.Rendering.Rectangle rectangle, XamlMath.Rendering.IBrush foreground) { }
        public void RenderTransformed(XamlMath.Boxes.Box box, IEnumerable<XamlMath.Rendering.Transformations.Transformation> transformations, double x, double y) { }
        public void FinishRendering() { }
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static MathVisual Build(object parsed, string latex, bool display, double size, Color color, string family) {
        var formula = (XamlMath.TexFormula)parsed;
        double scale = display ? size * 1.3 : size * 1.15;
        Brush brush = new SolidColorBrush(color);
        brush.Freeze();
        XamlMath.TexEnvironment environment = WpfMath.Rendering.WpfTeXEnvironment.Create(
            display ? XamlMath.TexStyle.Display : XamlMath.TexStyle.Text, scale, string.IsNullOrEmpty(family) ? "Segoe UI" : family, brush, null);
        var capture = new Capture();
        XamlMath.Rendering.TeXFormulaExtensions.RenderTo(formula, capture, environment, 0.0, 0.0);
        var drawing = new DrawingVisual();
        using (DrawingContext context = drawing.RenderOpen())
            WpfMath.Rendering.WpfTeXFormulaExtensions.RenderTo(formula, context, environment, scale, 0.0, 0.0);
        Rect bounds = drawing.ContentBounds;
        double width = 0, ascent = 0, descent = 0;
        if (capture.Root != null) { width = capture.Root.Width * scale; ascent = capture.Root.Height * scale; descent = capture.Root.Depth * scale; }
        var visual = new MathVisual { Source = latex };
        double offsetX = 0, offsetY = 0;
        if (!bounds.IsEmpty) {
            offsetX = Math.Max(0, -bounds.Left);
            offsetY = Math.Max(0, -bounds.Top);
            if (capture.Root == null) { ascent = Math.Max(1, bounds.Bottom * 0.75); descent = Math.Max(0, bounds.Bottom - ascent); width = bounds.Right; }
            else descent = Math.Max(descent, bounds.Bottom - ascent);
            width = Math.Max(width, bounds.Right);
        }
        visual.OffsetX = offsetX;
        visual.Width = width + offsetX;
        visual.Ascent = ascent + offsetY;
        visual.Descent = descent;
        DrawingGroup group = drawing.Drawing;
        if (offsetY > 0 && group != null) {
            var shifted = new DrawingGroup();
            shifted.Transform = new TranslateTransform(0, offsetY);
            shifted.Children.Add(group);
            group = shifted;
        }
        if (group != null) {
            try { group.Freeze(); } catch (InvalidOperationException) { }
        }
        visual.Drawing = group;
        return visual;
    }
}
}
