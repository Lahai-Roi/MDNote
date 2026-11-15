using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
namespace MDNote {
// 2.5.0: a ```mermaid fence becomes a picture in the two outputs that do not show the editor: the
// print/rich-copy FlowDocument (MDNote.cs ParseCore) and the HTML export (HtmlExport.cs Render). The
// screen has its own live path (MarkdownLivePreview), so nothing here is reached from typing and a
// document without a mermaid fence costs one string compare on the fence info string only.
// Every entry returns null on any failure, so a caller falls back to the plain code block it drew before.
static class MermaidOutput {
    internal const int RenderWaitMilliseconds = 3000;
    // A wider diagram is drawn scaled down (the picture is a vector DrawingImage, so it stays crisp).
    internal const double MaxDisplayWidth = 680;
    // The names SvgToWpf can read, so a sanitized picture converts to the same drawing as the original.
    static readonly string[] AllowedElements = new string[] {
        "svg","g","text","tspan","path","rect","circle","ellipse","line","polyline","polygon","title","desc"
    };
    static readonly string[] AllowedAttributes = new string[] {
        "xmlns","viewBox","width","height","x","y","x1","y1","x2","y2","cx","cy","r","rx","ry","d","points",
        "transform","fill","stroke","stroke-width","stroke-dasharray","stroke-linecap","stroke-linejoin",
        "fill-opacity","stroke-opacity","opacity","text-anchor","dominant-baseline","font-size","font-weight",
        "font-style","font-family","class","dx","dy","style"
    };
    // Elements whose character data is label text; text anywhere else is the source file's formatting.
    static readonly string[] TextElements = new string[] { "text","tspan","title","desc" };

    // The fence's first word, trimmed: "mermaid", "  Mermaid  title", and a language with no such name
    // ("mermaidx", "js") is not a mermaid fence.
    internal static bool IsMermaidInfo(string info) {
        if (info == null) return false;
        string value = info.Trim();
        if (value.Length == 0) return false;
        int end = 0;
        while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
        return string.Equals(value.Substring(0, end), "mermaid", StringComparison.OrdinalIgnoreCase);
    }

    // ---- one document's time budget and the render cache -------------------------------------
    // ParseCore and HtmlExporter.Convert run on the UI thread and wait for each block in turn. Inside a
    // BeginDocument/EndDocument pair the waits of all blocks together stay under
    // DocumentBudgetMilliseconds: a block gets what is left (at most RenderWaitMilliseconds), and once
    // nothing is left the remaining blocks are code blocks without the engine being asked. The pair
    // nests (a quote parses its text through the same entry); only the outermost Begin starts a budget.
    internal static int DocumentBudgetMilliseconds = 8000;
    // Probe only: a pause before every engine call, and counters.
    internal static int DiagDelayMilliseconds;
    internal static long DiagEngineCalls, DiagCacheHits, DiagBudgetSkips;
    internal static Action<string> DiagStyleDropped;
    const int CacheCapacity = 32;
    sealed class Rendered { internal string Key, Svg; internal double Width, Height; }
    static readonly object Sync = new object();
    static readonly Dictionary<string, LinkedListNode<Rendered>> Cache = new Dictionary<string, LinkedListNode<Rendered>>(StringComparer.Ordinal);
    static readonly LinkedList<Rendered> CacheOrder = new LinkedList<Rendered>();
    static int documentDepth, documentSpent;
    internal static void BeginDocument() { lock (Sync) { if (documentDepth++ == 0) documentSpent = 0; } }
    internal static void EndDocument() { lock (Sync) { if (documentDepth > 0) documentDepth--; } }
    internal static void ClearCache() { lock (Sync) { Cache.Clear(); CacheOrder.Clear(); } }
    // svg == null remembers an engine error for this source.
    static void Remember(string key, string svg, double width, double height) {
        lock (Sync) {
            LinkedListNode<Rendered> node;
            if (Cache.TryGetValue(key, out node)) { CacheOrder.Remove(node); Cache.Remove(key); }
            while (Cache.Count >= CacheCapacity && CacheOrder.Last != null) {
                Cache.Remove(CacheOrder.Last.Value.Key);
                CacheOrder.RemoveLast();
            }
            Cache[key] = CacheOrder.AddFirst(new Rendered { Key = key, Svg = svg, Width = width, Height = height });
        }
    }

    // Pele source -> SVG text plus its size. Null when the engine cannot run in this process, the
    // source is empty or over the engine's cap, or Pele reports an error (bad syntax, time limit).
    // The same source is drawn once: the HTML export renders a page with a [TOC] twice, and a print
    // preview is followed by the print itself. An engine error is remembered too (a broken source is
    // not asked again); a timeout or a spent budget is not, since the next try may succeed.
    internal static string RenderSvg(string source, string fontFamily, double fontSize, out double width, out double height) {
        width = 0;
        height = 0;
        try {
            if (MermaidEngine.Unavailable) return null;
            if (source == null || source.Trim().Length == 0) return null;
            if (source.Length > MermaidEngine.MaxSourceChars) return null;
            if (double.IsNaN(fontSize) || fontSize < 4) fontSize = 16;
            string family = string.IsNullOrEmpty(fontFamily) ? "Segoe UI" : fontFamily;
            string key = source + "\u0001" + family + "\u0001" + fontSize.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            int wait = RenderWaitMilliseconds;
            bool scoped;
            lock (Sync) {
                LinkedListNode<Rendered> node;
                if (Cache.TryGetValue(key, out node)) {
                    CacheOrder.Remove(node); CacheOrder.AddFirst(node);
                    DiagCacheHits++;
                    width = node.Value.Width;
                    height = node.Value.Height;
                    return node.Value.Svg;
                }
                scoped = documentDepth > 0;
                if (scoped) {
                    int left = DocumentBudgetMilliseconds - documentSpent;
                    if (left <= 0) { DiagBudgetSkips++; return null; }
                    if (left < wait) wait = left;
                }
            }
            int started = Environment.TickCount;
            MermaidResult result = null;
            try {
                // Probe seam: a slow engine. The pause is part of this block's wait and of the budget.
                int delay = DiagDelayMilliseconds;
                if (delay > 0) {
                    if (delay > wait) delay = wait;
                    System.Threading.Thread.Sleep(delay);
                    wait -= delay;
                }
                if (wait > 0) {
                    DiagEngineCalls++;
                    result = MermaidEngine.RenderSync(source, family, fontSize, 1.0, wait);
                } else if (scoped) DiagBudgetSkips++;
            } finally {
                if (scoped) lock (Sync) { documentSpent += Math.Max(0, unchecked(Environment.TickCount - started)); }
            }
            if (result == null) return null;
            if (result.Error != null) {
                if (result.Error != "timeout" && result.Error != "unavailable" && result.Error != "reentrant") Remember(key, null, 0, 0);
                return null;
            }
            if (result.Svg == null || result.Svg.Length == 0) return null;
            width = result.Width;
            height = result.Height;
            Remember(key, result.Svg, width, height);
            return result.Svg;
        } catch (Exception) {
            width = 0;
            height = 0;
            return null;
        }
    }

    // The print renderer's picture: the SVG converted to one frozen Drawing, wrapped in an Image of the
    // diagram's own size (or MaxDisplayWidth wide with the height kept in proportion). Null on any failure.
    internal static FrameworkElement CreatePicture(string source, string fontFamily, double fontSize, bool dark) {
        try {
            double engineWidth, engineHeight;
            string svg = RenderSvg(source, fontFamily, fontSize, out engineWidth, out engineHeight);
            if (svg == null) return null;
            SvgTheme theme = dark ? DarkTheme() : new SvgTheme();
            if (!dark) theme.Vars["--pele-surface-alt"] = MermaidRender.LightSurfaceAlt;
            theme.FontFamily = string.IsNullOrEmpty(fontFamily) ? "Segoe UI" : fontFamily;
            theme.PixelsPerDip = 1.0;
            double convertedWidth, convertedHeight;
            string error;
            Drawing drawing = SvgToWpf.Convert(svg, theme, out convertedWidth, out convertedHeight, out error);
            if (drawing == null) return null;
            double width = convertedWidth > 0 ? convertedWidth : engineWidth;
            double height = convertedHeight > 0 ? convertedHeight : engineHeight;
            if (!(width > 0) || !(height > 0)) return null;
            if (width > MaxDisplayWidth) {
                height = height * (MaxDisplayWidth / width);
                width = MaxDisplayWidth;
            }
            var image = new DrawingImage(drawing);
            image.Freeze();
            return new Image { Source = image, Stretch = Stretch.Uniform, Width = width, Height = height, Focusable = false };
        } catch (Exception) {
            return null;
        }
    }

    // The dark page's Pele variables, the same values SvgToWpfProbe.DarkTheme uses so the probe and the
    // print/HTML outputs draw a dark diagram the same way.
    static SvgTheme DarkTheme() {
        SvgTheme theme = new SvgTheme();
        theme.Vars["--pele-bg"] = "#1e1e1e";
        theme.Vars["--pele-fg"] = "#e0e0e0";
        theme.Vars["--pele-muted"] = "#a0a0a0";
        theme.Vars["--pele-line"] = "#c8c8c8";
        theme.Vars["--pele-surface"] = "#3c3f44";
        theme.Vars["--pele-surface-alt"] = "#565a62";
        theme.Vars["--pele-border"] = "#7d7d7d";
        theme.Vars["--pele-accent"] = "#7aa2f7";
        return theme;
    }

    // The page-side twin of CreatePicture: the engine's SVG rebuilt with only the elements and
    // attributes a diagram needs, so the exported page can carry it inline. No script, no event
    // attribute, no external reference and no style the sanitizer does not understand survives.
    internal static string SanitizeSvg(string svg) {
        if (svg == null) return null;
        XDocument parsed;
        try { parsed = Load(svg); } catch (Exception) { return null; }
        if (parsed == null) return null;
        XElement root = parsed.Root;
        if (root == null || !SameName(root.Name.LocalName, "svg")) return null;
        var output = new StringBuilder(svg.Length + 64);
        int elements = 0;
        if (!WriteElement(root, output, 0, ref elements)) return null;
        return output.Length == 0 ? null : output.ToString();
    }

    static XDocument Load(string svg) {
        var settings = new XmlReaderSettings();
        settings.DtdProcessing = DtdProcessing.Prohibit;
        settings.XmlResolver = null;
        settings.CheckCharacters = false;
        using (var reader = XmlReader.Create(new StringReader(svg), settings)) return XDocument.Load(reader, LoadOptions.None);
    }

    // One element and its subtree. False for a picture the sanitizer refuses whole (too deep, too many
    // elements, a broken root): the caller then drops the figure and keeps the code block.
    static bool WriteElement(XElement element, StringBuilder output, int depth, ref int elements) {
        if (depth > 64) return false;
        elements++;
        if (elements > SvgToWpf.MaxElements) return false;
        string name = element.Name.LocalName;
        if (!Allowed(AllowedElements, name)) return false;
        output.Append('<').Append(name);
        foreach (XAttribute attribute in element.Attributes()) {
            // A prefixed declaration (xmlns:xlink) cannot be carried without its uses; the default one
            // stays so a browser reads the markup as SVG.
            if (attribute.IsNamespaceDeclaration) {
                if (!SameName(attribute.Name.LocalName, "xmlns")) continue;
                output.Append(" xmlns=\"");
                AppendEscaped(output, attribute.Value, true);
                output.Append('"');
                continue;
            }
            string attributeName = attribute.Name.LocalName;
            if (!Allowed(AllowedAttributes, attributeName)) continue;
            string value = attribute.Value;
            if (value == null) continue;
            if (value.IndexOf("javascript:", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            // A paint or filter reference (Pele's sankey gradient fill is url(#id)) would point at a def
            // this rebuild dropped, and a url() can also name an outside document. The token goes; the
            // colour written after it as a fallback stays, and an attribute left empty goes too.
            if (SameName(attributeName, "style")) {
                value = CleanStyle(value);
                if (value == null) continue;
            } else if (value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) {
                value = StripUrlFunctions(value);
                if (value.Length == 0) continue;
            }
            output.Append(' ').Append(attributeName).Append("=\"");
            AppendEscaped(output, value, true);
            output.Append('"');
        }
        output.Append('>');
        foreach (XNode node in element.Nodes()) {
            var child = node as XElement;
            if (child != null) {
                // An element outside the list takes its whole subtree with it (script, foreignObject, a, ...).
                if (!Allowed(AllowedElements, child.Name.LocalName)) continue;
                if (!WriteElement(child, output, depth + 1, ref elements)) return false;
                continue;
            }
            var text = node as XText;
            if (text == null || !Allowed(TextElements, name)) continue;
            string content = text.Value;
            if (content == null || content.Length == 0) continue;
            AppendEscaped(output, content, false);
        }
        output.Append("</").Append(name).Append('>');
        return true;
    }

    static bool Allowed(string[] names, string name) {
        for (int i = 0; i < names.Length; i++) if (SameName(names[i], name)) return true;
        return false;
    }

    static bool SameName(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    // "url(#grad) #4e79a7" -> "#4e79a7": every url(...) token is removed and the spaces it leaves are
    // squeezed, so the colour a paint fallback wrote survives. Null when nothing usable is left.
    static string StripUrlFunctions(string value) {
        var result = new StringBuilder(value.Length);
        int i = 0;
        while (i < value.Length) {
            int hit = value.IndexOf("url(", i, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) { result.Append(value, i, value.Length - i); break; }
            result.Append(value, i, hit - i);
            int close = value.IndexOf(')', hit);
            i = close < 0 ? value.Length : close + 1;
        }
        string text = result.ToString().Trim();
        while (text.IndexOf("  ", StringComparison.Ordinal) >= 0) text = text.Replace("  ", " ");
        return text;
    }

    // The ordinary properties Pele writes into a style attribute (pele.chakra.min.js: the root's
    // "color:var(--_fg)" and font block, a shape's "rx:var(--_r)", classDef/style fill and stroke, a
    // static member's underline). Nothing that places or sizes a box is here: position, inset, z-index,
    // top/left/right/bottom, width/height, max-width (Pele's root "max-width:100%;height:auto" goes).
    static readonly string[] StyleProperties = new string[] {
        "color","fill","fill-opacity","stroke","stroke-width","stroke-dasharray","stroke-dashoffset","stroke-linecap",
        "stroke-linejoin","stroke-opacity","opacity","font","font-family","font-size","font-weight","font-style",
        "letter-spacing","word-spacing","text-transform","direction","text-decoration","text-anchor","dominant-baseline","rx","ry"
    };
    // A style attribute rebuilt from the declarations it may keep: a custom property (--name) or one
    // of StyleProperties, with a value of plain characters only. Anything that could fetch, run or
    // escape (url(, expression, javascript, @import, a backslash, '<') drops the whole attribute; any
    // other declaration (a layout property, an odd value) is dropped by itself. Null = nothing left.
    static string CleanStyle(string value) {
        if (value.IndexOf("url(", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        if (value.IndexOf("expression", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        if (value.IndexOf("javascript", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        if (value.IndexOf("@import", StringComparison.OrdinalIgnoreCase) >= 0) return null;
        if (value.IndexOf('<') >= 0) return null;
        if (value.IndexOf('\\') >= 0) return null;
        StringBuilder kept = null;
        string[] declarations = value.Split(';');
        for (int i = 0; i < declarations.Length; i++) {
            string declaration = declarations[i].Trim();
            if (declaration.Length == 0) continue;
            int colon = declaration.IndexOf(':');
            string name = colon <= 0 ? null : declaration.Substring(0, colon).Trim();
            string text = colon <= 0 ? null : declaration.Substring(colon + 1).Trim();
            if (name == null || !SafeStyleName(name) || !SafeStyleValue(text)) {
                Action<string> dropped = DiagStyleDropped;
                if (dropped != null) dropped(declaration);
                continue;
            }
            if (kept == null) kept = new StringBuilder(value.Length);
            else kept.Append(';');
            kept.Append(name).Append(':').Append(text);
        }
        return kept == null ? null : kept.ToString();
    }
    static bool SafeStyleName(string name) {
        if (name.Length > 2 && name[0] == '-' && name[1] == '-') {
            for (int i = 2; i < name.Length; i++) {
                char c = name[i];
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')) return false;
            }
            return true;
        }
        return Allowed(StyleProperties, name);
    }
    // Letters (a font name may be Korean), digits and # , . % ( ) ' " _ - and spaces; quotes in pairs and
    // brackets balanced, so a value cut at a ';' inside a string is refused instead of half kept.
    static bool SafeStyleValue(string text) {
        if (text == null || text.Length == 0) return false;
        int depth = 0, singles = 0, doubles = 0;
        for (int i = 0; i < text.Length; i++) {
            char c = text[i];
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; if (depth < 0) return false; continue; }
            if (c == '\'') { singles++; continue; }
            if (c == '"') { doubles++; continue; }
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '#' || c == ',' || c == '.' || c == '%' || c == '_' || c == '-') continue;
            return false;
        }
        return depth == 0 && (singles & 1) == 0 && (doubles & 1) == 0;
    }

    // XML escaping; a value's own control characters are dropped so the rebuilt markup stays readable.
    static void AppendEscaped(StringBuilder output, string value, bool attribute) {
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            if (c == '&') { output.Append("&amp;"); continue; }
            if (c == '<') { output.Append("&lt;"); continue; }
            if (c == '>') { output.Append("&gt;"); continue; }
            if (attribute) {
                if (c == '"') { output.Append("&quot;"); continue; }
                if (c == '\'') { output.Append("&#39;"); continue; }
                if (c == '\t') { output.Append("&#9;"); continue; }
                if (c == '\n') { output.Append("&#10;"); continue; }
                if (c == '\r') { output.Append("&#13;"); continue; }
            }
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
            if (c == '￾' || c == '￿') continue;
            output.Append(c);
        }
    }
}
}
