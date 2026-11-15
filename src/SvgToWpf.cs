using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml;
namespace MDNote {
// Theme overrides for the Pele CSS custom properties. Pele's SVG carries its own light values
// (var(--pele-fg,#1f1f1f)); a dark page overrides the variables here instead of rewriting the markup.
internal sealed class SvgTheme {
    internal Dictionary<string, string> Vars = new Dictionary<string, string>();
    internal string FontFamily;
    internal double PixelsPerDip = 1.0;
}
// Pele (Mermaid) SVG -> frozen WPF Drawing. Only what the Pele renderer emits is understood: nested g/svg,
// shapes (path/rect/circle/polygon), text with absolute tspans, presentation attributes with inheritance,
// CSS custom properties from the root style, translate/scale/rotate/matrix transforms. Unknown elements are
// skipped and counted; nothing here throws to the caller.
static class SvgToWpf {
    internal const int MaxElements = 5000;
    internal static int DiagLastElements, DiagLastSkipped;

    sealed class TooManyException : Exception { }

    sealed class Ctx {
        internal SvgTheme Theme;
        internal Dictionary<string, string> Props = new Dictionary<string, string>();
        internal Dictionary<string, Pen> Pens = new Dictionary<string, Pen>();
        internal int Elements, Skipped;
    }

    // Returns a frozen DrawingGroup sized to the viewBox (origin 0,0), or null with error set.
    internal static Drawing Convert(string svg, SvgTheme theme, out double width, out double height, out string error) {
        width = 0; height = 0; error = null;
        DiagLastElements = 0; DiagLastSkipped = 0;
        Ctx ctx = new Ctx();
        ctx.Theme = theme == null ? new SvgTheme() : theme;
        try {
            if (svg == null || svg.Trim().Length == 0) { error = "empty svg"; return null; }
            XmlDocument doc = Load(svg);
            XmlElement root = doc.DocumentElement;
            if (root == null || root.LocalName != "svg") { error = "no svg root"; return null; }
            ctx.Elements = 1;
            SvgStyle rootStyle = new SvgStyle();
            ApplyAttributes(root, rootStyle, ctx);
            double vbX = 0, vbY = 0, vbW = 0, vbH = 0;
            bool hasViewBox = ParseViewBox(Attr(root, "viewBox"), out vbX, out vbY, out vbW, out vbH);
            double rootW = Num(Attr(root, "width"), double.NaN);
            double rootH = Num(Attr(root, "height"), double.NaN);
            if (double.IsNaN(rootW) || rootW <= 0) rootW = hasViewBox ? vbW : 0;
            if (double.IsNaN(rootH) || rootH <= 0) rootH = hasViewBox ? vbH : 0;
            if (rootW <= 0 || rootH <= 0) { error = "no size"; return null; }
            DrawingGroup drawing = new DrawingGroup();
            // The viewBox box maps onto the root width/height; Pele keeps them equal, so this is the identity
            // for its output but still makes a hand-scaled file land in the reported size.
            double scaleX = vbW > 0 ? rootW / vbW : 1;
            double scaleY = vbH > 0 ? rootH / vbH : 1;
            if (hasViewBox && (vbX != 0 || vbY != 0 || scaleX != 1 || scaleY != 1)) {
                Matrix fit = new Matrix(scaleX, 0, 0, scaleY, -vbX * scaleX, -vbY * scaleY);
                drawing.Transform = Frozen(new MatrixTransform(fit));
            }
            WalkChildren(root, rootStyle, drawing, ctx, 0);
            drawing.Freeze();
            DiagLastElements = ctx.Elements;
            DiagLastSkipped = ctx.Skipped;
            width = rootW;
            height = rootH;
            return drawing;
        } catch (TooManyException) {
            DiagLastElements = ctx.Elements; DiagLastSkipped = ctx.Skipped;
            error = "too many elements";
            return null;
        } catch (Exception ex) {
            DiagLastElements = ctx.Elements; DiagLastSkipped = ctx.Skipped;
            error = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
    }

    static XmlDocument Load(string svg) {
        XmlReaderSettings settings = new XmlReaderSettings();
        settings.DtdProcessing = DtdProcessing.Prohibit;
        settings.XmlResolver = null;
        settings.CheckCharacters = false;
        XmlDocument doc = new XmlDocument();
        doc.PreserveWhitespace = false;
        using (XmlReader reader = XmlReader.Create(new StringReader(svg), settings)) doc.Load(reader);
        return doc;
    }

    static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }

    static string Attr(XmlElement el, string name) {
        XmlAttribute attribute = el.Attributes[name];
        return attribute == null ? null : attribute.Value;
    }

    static double AttrNum(XmlElement el, string name, double fallback) { return Num(Attr(el, name), fallback); }

    // "4px", "1.5", "0.22" -> number; percentages and junk keep the fallback.
    static double Num(string text, double fallback) {
        if (text == null) return fallback;
        string value = text.Trim();
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 2).Trim();
        if (value.Length == 0 || value.EndsWith("%", StringComparison.OrdinalIgnoreCase)) return fallback;
        double result;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return result;
        return fallback;
    }

    // Points lists, dash arrays and viewBoxes: numbers separated by commas or whitespace.
    static List<double> ParseNumbers(string text) {
        List<double> list = new List<double>();
        if (text == null) return list;
        int i = 0;
        while (i < text.Length) {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',' || text[i] == ';')) i++;
            int start = i;
            if (i < text.Length && (text[i] == '-' || text[i] == '+')) i++;
            while (i < text.Length) {
                char c = text[i];
                if (char.IsDigit(c) || c == '.') { i++; continue; }
                if ((c == 'e' || c == 'E') && i > start) { i++; if (i < text.Length && (text[i] == '-' || text[i] == '+')) i++; continue; }
                break;
            }
            if (i == start) { i++; continue; }
            double value;
            if (double.TryParse(text.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) list.Add(value);
        }
        return list;
    }

    static bool ParseViewBox(string text, out double x, out double y, out double width, out double height) {
        x = 0; y = 0; width = 0; height = 0;
        List<double> numbers = ParseNumbers(text);
        if (numbers.Count < 4) return false;
        x = numbers[0]; y = numbers[1]; width = numbers[2]; height = numbers[3];
        return width > 0 && height > 0;
    }

    // SVG transform list -> one matrix. The list applies right to left ("translate(a) rotate(b)" rotates first),
    // so each new operation is composed in front of the running matrix.
    static Matrix ParseTransform(string text) {
        Matrix matrix = Matrix.Identity;
        if (text == null) return matrix;
        int i = 0;
        while (i < text.Length) {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            int nameStart = i;
            while (i < text.Length && char.IsLetter(text[i])) i++;
            if (i == nameStart) { i++; continue; }
            string name = text.Substring(nameStart, i - nameStart);
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length || text[i] != '(') continue;
            int open = i;
            int depth = 0;
            while (i < text.Length) {
                if (text[i] == '(') depth++;
                else if (text[i] == ')') { depth--; if (depth == 0) break; }
                i++;
            }
            if (i >= text.Length) break;
            string inside = text.Substring(open + 1, i - open - 1);
            i++;
            List<double> v = ParseNumbers(inside);
            Matrix op;
            if (name == "translate") {
                op = new Matrix(1, 0, 0, 1, v.Count > 0 ? v[0] : 0, v.Count > 1 ? v[1] : 0);
            } else if (name == "scale") {
                double sx = v.Count > 0 ? v[0] : 1;
                double sy = v.Count > 1 ? v[1] : sx;
                op = new Matrix(sx, 0, 0, sy, 0, 0);
            } else if (name == "rotate") {
                double degrees = v.Count > 0 ? v[0] : 0;
                double radians = degrees * Math.PI / 180.0;
                double cos = Math.Cos(radians);
                double sin = Math.Sin(radians);
                if (v.Count >= 3) {
                    double cx = v[1], cy = v[2];
                    op = new Matrix(cos, sin, -sin, cos, cx - cos * cx + sin * cy, cy - sin * cx - cos * cy);
                } else {
                    op = new Matrix(cos, sin, -sin, cos, 0, 0);
                }
            } else if (name == "matrix") {
                if (v.Count < 6) continue;
                op = new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
            } else {
                continue;
            }
            matrix = Matrix.Multiply(op, matrix);
        }
        return matrix;
    }

    // A nested <svg class="pele-icon" x y width height viewBox> maps its viewBox onto the x/y box:
    // content -> (content - viewBox origin) * scale + (x, y), written straight into one row-vector matrix.
    static Matrix NestedSvgMatrix(XmlElement el) {
        double vbX, vbY, vbWidth, vbHeight;
        if (!ParseViewBox(Attr(el, "viewBox"), out vbX, out vbY, out vbWidth, out vbHeight)) return Matrix.Identity;
        double width = AttrNum(el, "width", 0);
        double height = AttrNum(el, "height", 0);
        if (width <= 0 || height <= 0) return Matrix.Identity;
        double scaleX = width / vbWidth;
        double scaleY = height / vbHeight;
        return new Matrix(scaleX, 0, 0, scaleY, AttrNum(el, "x", 0) - vbX * scaleX, AttrNum(el, "y", 0) - vbY * scaleY);
    }

    // var(--name[, fallback]) chains. theme.Vars wins over the SVG's own definitions and over the fallback,
    // then the root style's custom properties, then the fallback text.
    static string ResolveValue(string raw, Ctx ctx, int depth) {
        if (raw == null) return null;
        string value = raw.Trim();
        if (depth > 8 || value.Length == 0) return null;
        if (!value.StartsWith("var(", StringComparison.Ordinal)) return value;
        int close = value.LastIndexOf(')');
        if (close < 5) return null;
        string inner = value.Substring(4, close - 4);
        string name = inner;
        string fallback = null;
        int comma = FindTopLevelComma(inner);
        if (comma >= 0) { name = inner.Substring(0, comma); fallback = inner.Substring(comma + 1); }
        name = name.Trim();
        if (name.Length == 0) return null;
        string resolved;
        if (name == "--pele-font" && ctx.Theme.FontFamily != null && ctx.Theme.FontFamily.Length > 0) resolved = ctx.Theme.FontFamily;
        else if (ctx.Theme.Vars != null && ctx.Theme.Vars.ContainsKey(name)) resolved = ctx.Theme.Vars[name];
        else if (ctx.Props.ContainsKey(name)) resolved = ctx.Props[name];
        else resolved = fallback;
        if (resolved == null) return null;
        return ResolveValue(resolved, ctx, depth + 1);
    }

    static int FindTopLevelComma(string text) {
        int depth = 0;
        for (int i = 0; i < text.Length; i++) {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (text[i] == ',' && depth == 0) return i;
        }
        return -1;
    }

    // none/currentColor/var()/hex/rgb()/named -> a Color. Returns false with none=true for values that should
    // paint nothing (none, transparent, an unresolved var without fallback).
    static bool ResolveColor(string raw, SvgStyle style, Ctx ctx, out bool none, out Color color) {
        none = false;
        color = Colors.Black;
        string value = raw;
        for (int guard = 0; guard < 10 && value != null; guard++) {
            value = value.Trim();
            if (value.Length == 0) { none = true; return true; }
            string lower = value.ToLowerInvariant();
            if (lower == "none" || lower == "transparent") { none = true; return true; }
            if (lower == "currentcolor") { value = style == null ? null : style.Color; continue; }
            if (lower.StartsWith("var(", StringComparison.Ordinal)) { value = ResolveValue(value, ctx, 0); continue; }
            if (TryColorLiteral(lower, out color)) return true;
            none = true;
            return false;
        }
        none = true;
        return false;
    }

    static bool TryColorLiteral(string text, out Color color) {
        color = Colors.Black;
        if (text.Length == 0) return false;
        if (text[0] == '#') {
            string hex = text.Substring(1);
            if (hex.Length == 3 || hex.Length == 4) {
                StringBuilder expanded = new StringBuilder();
                for (int i = 0; i < hex.Length; i++) expanded.Append(hex[i]).Append(hex[i]);
                hex = expanded.ToString();
            }
            // #rrggbb -> opaque ARGB; #rrggbbaa -> the same with its alpha in front.
            if (hex.Length == 6) hex = "ff" + hex;
            else if (hex.Length == 8) hex = hex.Substring(6, 2) + hex.Substring(0, 6);
            if (hex.Length != 8) return false;
            uint argb;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb)) return false;
            color = Color.FromArgb((byte)((argb >> 24) & 0xff), (byte)((argb >> 16) & 0xff), (byte)((argb >> 8) & 0xff), (byte)(argb & 0xff));
            return true;
        }
        if (text.StartsWith("rgb(", StringComparison.Ordinal) || text.StartsWith("rgba(", StringComparison.Ordinal)) {
            int open = text.IndexOf('(');
            int close = text.LastIndexOf(')');
            if (close <= open) return false;
            string inner = text.Substring(open + 1, close - open - 1).Replace('/', ' ');
            string[] parts = inner.Split(new char[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<double> values = new List<double>();
            foreach (string part in parts) {
                bool percent = part.EndsWith("%", StringComparison.Ordinal);
                string token = percent ? part.Substring(0, part.Length - 1) : part;
                double number;
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
                values.Add(percent ? number * 2.55 : number);
            }
            if (values.Count < 3) return false;
            byte alpha = 255;
            if (values.Count > 3) alpha = (byte)Clamp(values[3] <= 1 ? values[3] * 255 : values[3], 0, 255);
            color = Color.FromArgb(alpha, (byte)Clamp(values[0], 0, 255), (byte)Clamp(values[1], 0, 255), (byte)Clamp(values[2], 0, 255));
            return true;
        }
        try {
            object converted = ColorConverter.ConvertFromString(text);
            if (converted is Color) { color = (Color)converted; return true; }
        } catch (Exception) { }
        return false;
    }

    static double Clamp(double value, double min, double max) { return value < min ? min : (value > max ? max : value); }

    // Presentation properties after inheritance from svg/g. Values that may hold var() stay raw here and are
    // resolved when painting, so a theme override reaches them.
    sealed class SvgStyle {
        internal string Fill = "#000000";
        internal string Stroke = "none";
        internal double StrokeWidth = 1;
        internal string LineCap = "butt";
        internal string LineJoin = "miter";
        internal string DashArray;
        internal double FillOpacity = 1;
        internal double StrokeOpacity = 1;
        internal double FontSize = 16;
        internal bool Bold;
        internal bool Italic;
        internal string FontFamily;
        internal string Anchor = "start";
        internal string Color = "#000000";
        internal bool Underline;
        internal SvgStyle Copy() { return (SvgStyle)MemberwiseClone(); }
    }

    static List<KeyValuePair<string, string>> ParseDeclarations(string text) {
        List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
        if (text == null) return list;
        string[] parts = text.Split(';');
        foreach (string part in parts) {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string name = part.Substring(0, colon).Trim().ToLowerInvariant();
            string value = part.Substring(colon + 1).Trim();
            if (name.Length == 0 || value.Length == 0) continue;
            list.Add(new KeyValuePair<string, string>(name, value));
        }
        return list;
    }

    // The last value for a style property ("rx" on rects comes from the style attribute, never as an attribute).
    static string StyleProperty(XmlElement el, string name) {
        string found = null;
        List<KeyValuePair<string, string>> declarations = ParseDeclarations(Attr(el, "style"));
        foreach (KeyValuePair<string, string> pair in declarations) {
            if (pair.Key == name) found = pair.Value;
        }
        return found;
    }

    // Attributes first, then the style declarations in order (a style property overrides its attribute).
    // Custom properties (--_fg and friends) are collected before anything is resolved, since the same style
    // text may already use them.
    static void ApplyAttributes(XmlElement el, SvgStyle style, Ctx ctx) {
        List<KeyValuePair<string, string>> declarations = ParseDeclarations(Attr(el, "style"));
        foreach (KeyValuePair<string, string> pair in declarations) {
            if (pair.Key.StartsWith("--", StringComparison.Ordinal) && ctx.Theme.Vars != null && !ctx.Theme.Vars.ContainsKey(pair.Key)) ctx.Props[pair.Key] = pair.Value;
        }
        ApplyPresentation("fill", Attr(el, "fill"), style, ctx);
        ApplyPresentation("stroke", Attr(el, "stroke"), style, ctx);
        ApplyPresentation("stroke-width", Attr(el, "stroke-width"), style, ctx);
        ApplyPresentation("stroke-linecap", Attr(el, "stroke-linecap"), style, ctx);
        ApplyPresentation("stroke-linejoin", Attr(el, "stroke-linejoin"), style, ctx);
        ApplyPresentation("stroke-dasharray", Attr(el, "stroke-dasharray"), style, ctx);
        ApplyPresentation("fill-opacity", Attr(el, "fill-opacity"), style, ctx);
        ApplyPresentation("stroke-opacity", Attr(el, "stroke-opacity"), style, ctx);
        ApplyPresentation("font-size", Attr(el, "font-size"), style, ctx);
        ApplyPresentation("font-weight", Attr(el, "font-weight"), style, ctx);
        ApplyPresentation("font-style", Attr(el, "font-style"), style, ctx);
        ApplyPresentation("font-family", Attr(el, "font-family"), style, ctx);
        ApplyPresentation("text-anchor", Attr(el, "text-anchor"), style, ctx);
        ApplyPresentation("color", Attr(el, "color"), style, ctx);
        ApplyPresentation("text-decoration", Attr(el, "text-decoration"), style, ctx);
        foreach (KeyValuePair<string, string> pair in declarations) {
            if (pair.Key.StartsWith("--", StringComparison.Ordinal)) continue;
            ApplyPresentation(pair.Key, pair.Value, style, ctx);
        }
        // One Pele sample writes style="italic" (no property name); read it the way the eye would.
        string styleText = Attr(el, "style");
        if (declarations.Count == 0 && styleText != null) {
            string bare = styleText.Trim().ToLowerInvariant();
            if (bare == "italic" || bare == "oblique") style.Italic = true;
        }
    }

    static void ApplyPresentation(string name, string raw, SvgStyle style, Ctx ctx) {
        if (name == null || raw == null) return;
        string value = raw.Trim();
        if (value.Length == 0) return;
        switch (name) {
            case "fill": style.Fill = value; break;
            case "stroke": style.Stroke = value; break;
            case "stroke-width": style.StrokeWidth = Num(ResolveValue(value, ctx, 0) ?? value, style.StrokeWidth); break;
            case "stroke-linecap": style.LineCap = value.ToLowerInvariant(); break;
            case "stroke-linejoin": style.LineJoin = value.ToLowerInvariant(); break;
            case "stroke-dasharray": style.DashArray = value; break;
            case "fill-opacity": style.FillOpacity = Num(ResolveValue(value, ctx, 0) ?? value, style.FillOpacity); break;
            case "stroke-opacity": style.StrokeOpacity = Num(ResolveValue(value, ctx, 0) ?? value, style.StrokeOpacity); break;
            case "font-size": style.FontSize = Num(ResolveValue(value, ctx, 0) ?? value, style.FontSize); break;
            case "font-weight": style.Bold = IsBold(ResolveValue(value, ctx, 0) ?? value); break;
            case "font-style": style.Italic = IsItalic(ResolveValue(value, ctx, 0) ?? value); break;
            case "font-family": style.FontFamily = StripQuotes(ResolveValue(value, ctx, 0) ?? value); break;
            case "text-anchor": style.Anchor = value.ToLowerInvariant(); break;
            case "color": style.Color = value; break;
            case "text-decoration": style.Underline = value.IndexOf("underline", StringComparison.OrdinalIgnoreCase) >= 0; break;
            case "font": ApplyFontShorthand(value, style, ctx); break;
            default: break;
        }
    }

    static bool IsBold(string value) {
        string lower = value.Trim().ToLowerInvariant();
        if (lower == "bold" || lower == "bolder") return true;
        if (lower == "normal" || lower == "lighter") return false;
        double weight;
        if (double.TryParse(lower, NumberStyles.Float, CultureInfo.InvariantCulture, out weight)) return weight >= 600;
        return false;
    }

    static bool IsItalic(string value) {
        string lower = value.Trim().ToLowerInvariant();
        return lower == "italic" || lower == "oblique";
    }

    static string StripQuotes(string value) {
        if (value == null) return null;
        return value.Replace("\"", string.Empty).Replace("'", string.Empty).Trim();
    }

    // "font:16px sans-serif" (Pele's only form) and the usual CSS shorthand with style/weight in front.
    static void ApplyFontShorthand(string raw, SvgStyle style, Ctx ctx) {
        string value = ResolveValue(raw, ctx, 0) ?? raw;
        string[] tokens = value.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        int sizeIndex = -1;
        double size = double.NaN;
        for (int i = 0; i < tokens.Length; i++) {
            string token = tokens[i];
            int slash = token.IndexOf('/');
            if (slash >= 0) token = token.Substring(0, slash);
            if (token.EndsWith("px", StringComparison.OrdinalIgnoreCase)) token = token.Substring(0, token.Length - 2);
            double parsed;
            if (token.Length > 0 && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) {
                sizeIndex = i;
                size = parsed;
                break;
            }
        }
        if (sizeIndex < 0) return;
        style.FontSize = size;
        StringBuilder family = new StringBuilder();
        for (int i = sizeIndex + 1; i < tokens.Length; i++) {
            if (family.Length > 0) family.Append(' ');
            family.Append(tokens[i]);
        }
        if (family.Length > 0) style.FontFamily = StripQuotes(family.ToString());
        for (int i = 0; i < sizeIndex; i++) {
            string token = tokens[i].ToLowerInvariant();
            if (token == "italic" || token == "oblique") style.Italic = true;
            else if (token == "bold" || token == "bolder") style.Bold = true;
            else if (token == "normal") { style.Bold = false; style.Italic = false; }
        }
    }

    // "system-ui" and the like are not font names; Pele's own fallback chain ends in Segoe UI, MDNote's UI font.
    static readonly string[] BadFamilyNames = new string[] { "system-ui", "-apple-system", "ui-sans-serif", "ui-serif", "ui-monospace", "sans-serif", "serif", "monospace", "cursive", "fantasy" };

    static string PickFontFamily(SvgStyle style) {
        if (style.FontFamily == null) return "Segoe UI";
        string[] parts = style.FontFamily.Split(',');
        foreach (string part in parts) {
            string name = StripQuotes(part);
            if (name.Length == 0) continue;
            bool bad = false;
            foreach (string candidate in BadFamilyNames) { if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) { bad = true; break; } }
            if (bad) continue;
            return name;
        }
        return "Segoe UI";
    }

    // Pens repeat a few times per picture (one width, colour and dash set per edge group); brushes are cheap.
    static Pen MakePen(SvgStyle style, Ctx ctx) {
        if (style.Stroke == null) return null;
        bool none;
        Color color;
        if (!ResolveColor(style.Stroke, style, ctx, out none, out color) || none) return null;
        double thickness = style.StrokeWidth;
        if (!(thickness > 0)) return null;
        double alpha = color.A;
        if (style.StrokeOpacity < 1) alpha *= Clamp(style.StrokeOpacity, 0, 1);
        if (alpha <= 0) return null;
        byte strokeAlpha = (byte)Clamp(alpha, 0, 255);
        string key = new StringBuilder(64).Append(thickness.ToString("R", CultureInfo.InvariantCulture)).Append('|')
            .Append(strokeAlpha).Append('|').Append(color.R).Append(',').Append(color.G).Append(',').Append(color.B).Append('|')
            .Append(style.LineCap).Append('|').Append(style.LineJoin).Append('|').Append(style.DashArray == null ? string.Empty : style.DashArray).ToString();
        Pen pen;
        if (ctx.Pens.TryGetValue(key, out pen)) return pen;
        SolidColorBrush brush = new SolidColorBrush(Color.FromArgb(strokeAlpha, color.R, color.G, color.B));
        brush.Freeze();
        pen = new Pen(brush, thickness);
        pen.StartLineCap = MapCap(style.LineCap);
        pen.EndLineCap = MapCap(style.LineCap);
        pen.DashCap = MapCap(style.LineCap);
        pen.LineJoin = MapJoin(style.LineJoin);
        pen.MiterLimit = 4;
        if (style.DashArray != null) {
            string dash = style.DashArray.Trim();
            if (dash != "none" && dash.Length > 0) {
                List<double> numbers = ParseNumbers(dash);
                List<double> dashes = new List<double>();
                for (int i = 0; i < numbers.Count; i++) dashes.Add(numbers[i] / thickness);
                if (dashes.Count == 1) dashes.Add(dashes[0]);
                if (dashes.Count > 0) {
                    bool allZero = true;
                    for (int i = 0; i < dashes.Count; i++) if (dashes[i] > 0) { allZero = false; break; }
                    if (!allZero) pen.DashStyle = new DashStyle(dashes, 0);
                }
            }
        }
        pen.Freeze();
        ctx.Pens[key] = pen;
        return pen;
    }

    static PenLineCap MapCap(string cap) {
        if (cap == "round") return PenLineCap.Round;
        if (cap == "square") return PenLineCap.Square;
        return PenLineCap.Flat;
    }

    static PenLineJoin MapJoin(string join) {
        if (join == "round") return PenLineJoin.Round;
        if (join == "bevel") return PenLineJoin.Bevel;
        return PenLineJoin.Miter;
    }

    static Brush MakeBrush(SvgStyle style, Ctx ctx) {
        bool none;
        Color color;
        if (!ResolveColor(style.Fill, style, ctx, out none, out color)) return null;
        if (none) return null;
        double alpha = color.A;
        if (style.FillOpacity < 1) alpha *= Clamp(style.FillOpacity, 0, 1);
        if (alpha <= 0) return null;
        SolidColorBrush brush = new SolidColorBrush(Color.FromArgb((byte)Clamp(alpha, 0, 255), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    static void WalkChildren(XmlElement parent, SvgStyle inherited, DrawingGroup target, Ctx ctx, int depth) {
        foreach (XmlNode node in parent.ChildNodes) {
            XmlElement el = node as XmlElement;
            if (el == null) continue;
            ctx.Elements++;
            if (ctx.Elements > MaxElements) throw new TooManyException();
            try {
                WalkElement(el, inherited, target, ctx, depth);
            } catch (TooManyException) {
                throw;
            } catch (Exception) {
                ctx.Skipped++;
            }
        }
    }

    static void WalkElement(XmlElement el, SvgStyle inherited, DrawingGroup target, Ctx ctx, int depth) {
        string name = el.LocalName;
        if (name == "defs" || name == "style" || name == "script") { ctx.Skipped++; return; }
        // title/desc are Pele's hover tooltips: understood, deliberately not drawn, not a skip.
        if (name == "title" || name == "desc") return;
        // A tspan is read as a run by its text parent; one met out here has no place to sit.
        if (name == "tspan") { ctx.Skipped++; return; }
        if (name == "g" || name == "svg") {
            SvgStyle style = inherited.Copy();
            ApplyAttributes(el, style, ctx);
            Matrix matrix = ParseTransform(Attr(el, "transform"));
            // A nested svg's x/y box (Pele icons) places the content first, its own transform list wraps around it.
            if (name == "svg") matrix = Matrix.Multiply(NestedSvgMatrix(el), matrix);
            DrawingGroup group = new DrawingGroup();
            if (!matrix.IsIdentity) group.Transform = Frozen(new MatrixTransform(matrix));
            WalkChildren(el, style, group, ctx, depth + 1);
            if (group.Children.Count > 0) {
                group.Freeze();
                target.Children.Add(group);
            }
            return;
        }
        if (name == "rect" || name == "circle" || name == "ellipse" || name == "polygon" || name == "polyline" || name == "path") {
            SvgStyle style = inherited.Copy();
            ApplyAttributes(el, style, ctx);
            Geometry geometry = BuildGeometry(el, style, ctx);
            if (geometry == null) { ctx.Skipped++; return; }
            Brush brush = MakeBrush(style, ctx);
            Pen pen = MakePen(style, ctx);
            if (brush == null && pen == null) return;
            Matrix matrix = ParseTransform(Attr(el, "transform"));
            if (!matrix.IsIdentity) {
                // Geometry.Parse hands back a frozen geometry, which cannot take a transform; a clone can.
                if (geometry.IsFrozen) geometry = (Geometry)geometry.Clone();
                geometry.Transform = new MatrixTransform(matrix);
            }
            GeometryDrawing shape = new GeometryDrawing(brush, pen, Frozen(geometry));
            shape.Freeze();
            target.Children.Add(shape);
            return;
        }
        if (name == "text") {
            ApplyTextTransform(el, inherited, target, ctx);
            return;
        }
        ctx.Skipped++;
    }

    // A text with its own transform is drawn into a small group carrying that matrix.
    static void ApplyTextTransform(XmlElement el, SvgStyle inherited, DrawingGroup target, Ctx ctx) {
        Matrix matrix = ParseTransform(Attr(el, "transform"));
        if (matrix.IsIdentity) {
            if (!DrawText(el, inherited, target, ctx)) ctx.Skipped++;
            return;
        }
        DrawingGroup group = new DrawingGroup();
        group.Transform = Frozen(new MatrixTransform(matrix));
        if (!DrawText(el, inherited, group, ctx)) { ctx.Skipped++; return; }
        if (group.Children.Count > 0) {
            group.Freeze();
            target.Children.Add(group);
        }
    }

    static Geometry BuildGeometry(XmlElement el, SvgStyle style, Ctx ctx) {
        string name = el.LocalName;
        if (name == "rect") {
            double x = AttrNum(el, "x", 0);
            double y = AttrNum(el, "y", 0);
            double width = AttrNum(el, "width", 0);
            double height = AttrNum(el, "height", 0);
            if (!(width > 0) || !(height > 0)) return null;
            double rx = AttrNum(el, "rx", double.NaN);
            double ry = AttrNum(el, "ry", double.NaN);
            string styleRadius = StyleProperty(el, "rx");
            if (styleRadius != null) rx = Num(ResolveValue(styleRadius, ctx, 0) ?? styleRadius, rx);
            string styleRadiusY = StyleProperty(el, "ry");
            if (styleRadiusY != null) ry = Num(ResolveValue(styleRadiusY, ctx, 0) ?? styleRadiusY, ry);
            if (double.IsNaN(rx) && double.IsNaN(ry)) return new RectangleGeometry(new Rect(x, y, width, height));
            if (double.IsNaN(rx)) rx = ry;
            if (double.IsNaN(ry)) ry = rx;
            rx = Clamp(rx, 0, width / 2);
            ry = Clamp(ry, 0, height / 2);
            return new RectangleGeometry(new Rect(x, y, width, height), rx, ry);
        }
        if (name == "circle") {
            double r = AttrNum(el, "r", 0);
            if (!(r > 0)) return null;
            return new EllipseGeometry(new Point(AttrNum(el, "cx", 0), AttrNum(el, "cy", 0)), r, r);
        }
        if (name == "ellipse") {
            double rx = AttrNum(el, "rx", 0);
            double ry = AttrNum(el, "ry", 0);
            if (!(rx > 0) || !(ry > 0)) return null;
            return new EllipseGeometry(new Point(AttrNum(el, "cx", 0), AttrNum(el, "cy", 0)), rx, ry);
        }
        if (name == "polygon" || name == "polyline") {
            List<double> numbers = ParseNumbers(Attr(el, "points"));
            if (numbers.Count < 4) return null;
            StreamGeometry geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open()) {
                context.BeginFigure(new Point(numbers[0], numbers[1]), name == "polygon", name == "polygon");
                for (int i = 2; i + 1 < numbers.Count; i += 2) context.LineTo(new Point(numbers[i], numbers[i + 1]), true, false);
            }
            return geometry;
        }
        if (name == "path") {
            string data = Attr(el, "d");
            if (data == null || data.Trim().Length == 0) return null;
            try {
                return Geometry.Parse(data);
            } catch (Exception) {
                return null;
            }
        }
        return null;
    }

    // Text: the SVG y is the baseline. One run per piece of direct text and per tspan, each placed at its own
    // absolute x/y (Pele wraps and lays out multi-line labels itself); a piece without x/y follows the previous
    // one. text-anchor shifts a chunk (the pieces that share a start x) by its measured width.
    static bool DrawText(XmlElement el, SvgStyle inherited, DrawingGroup target, Ctx ctx) {
        SvgStyle style = inherited.Copy();
        ApplyAttributes(el, style, ctx);
        double anchorX = AttrNum(el, "x", double.NaN);
        double anchorY = AttrNum(el, "y", double.NaN);
        double dx = AttrNum(el, "dx", 0);
        double dy = AttrNum(el, "dy", double.NaN);
        List<TextRun> runs = new List<TextRun>();
        StringBuilder plain = new StringBuilder();
        foreach (XmlNode node in el.ChildNodes) {
            XmlElement child = node as XmlElement;
            if (child == null) {
                if (node.NodeType == XmlNodeType.Text || node.NodeType == XmlNodeType.CDATA || node.NodeType == XmlNodeType.SignificantWhitespace) plain.Append(node.Value);
                continue;
            }
            // Text children count toward the element cap like any other element.
            ctx.Elements++;
            if (ctx.Elements > MaxElements) throw new TooManyException();
            if (child.LocalName == "tspan") {
                AddRun(runs, plain.ToString(), anchorX, anchorY, 0, 0, style);
                plain.Length = 0;
                SvgStyle runStyle = style.Copy();
                ApplyAttributes(child, runStyle, ctx);
                AddRun(runs, child.InnerText, AttrNum(child, "x", double.NaN), AttrNum(child, "y", double.NaN), AttrNum(child, "dx", 0), AttrNum(child, "dy", 0), runStyle);
                continue;
            }
            if (child.LocalName == "title" || child.LocalName == "desc") continue;
        }
        AddRun(runs, plain.ToString(), anchorX, anchorY, 0, 0, style);
        if (runs.Count == 0) return false;
        DrawTextRuns(runs, anchorX, anchorY, dx, dy, target, ctx);
        // An empty text is the only failure here: a fill of none or an unusable font is understood and drawn as
        // nothing, which is not the same as an element the converter could not read.
        return true;
    }

    static void AddRun(List<TextRun> runs, string text, double x, double y, double dx, double dy, SvgStyle style) {
        if (text == null || text.Length == 0) return;
        // Whitespace-only pieces are the source file's formatting between tspans, not content.
        if (text.Trim().Length == 0) return;
        runs.Add(new TextRun(text, x, y, dx, dy, style));
    }

    static void ApplyAnchor(List<TextRun> runs, int first, int last) {
        if (first < 0 || last < first || last >= runs.Count) return;
        string anchor = runs[first].Style.Anchor;
        if (anchor != "middle" && anchor != "end") return;
        TextRun start = runs[first];
        TextRun end = runs[last];
        double total = end.X + (end.Formatted == null ? 0 : end.Formatted.WidthIncludingTrailingWhitespace) - start.X;
        double shift = anchor == "middle" ? -total / 2 : -total;
        for (int i = first; i <= last; i++) runs[i].X += shift;
    }

    sealed class TextRun {
        internal string Text;
        internal double X, Y, Dx, Dy;
        internal bool HasX, HasY;
        internal SvgStyle Style;
        internal FormattedText Formatted;
        internal TextRun(string text, double x, double y, double dx, double dy, SvgStyle style) {
            Text = text; X = x; Y = y; Dx = dx; Dy = dy; Style = style;
            HasX = !double.IsNaN(x); HasY = !double.IsNaN(y);
        }
    }

    static FormattedText Format(string text, SvgStyle style, Ctx ctx) {
        double size = style.FontSize > 0 ? style.FontSize : 16;
        FontStyle fontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
        FontWeight weight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
        double pixelsPerDip = ctx.Theme.PixelsPerDip;
        if (!(pixelsPerDip > 0)) pixelsPerDip = 1.0;
        try {
            return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily(PickFontFamily(style)), fontStyle, weight, FontStretches.Normal),
                size, Brushes.Black, null, TextFormattingMode.Ideal, pixelsPerDip);
        } catch (Exception) {
            // A family WPF cannot use should not cost the whole picture.
            try {
                return new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), fontStyle, weight, FontStretches.Normal),
                    size, Brushes.Black, null, TextFormattingMode.Ideal, pixelsPerDip);
            } catch (Exception) {
                return null;
            }
        }
    }

    static void DrawFormatted(FormattedText formatted, double x, double y, DrawingGroup target) {
        DrawingGroup group = new DrawingGroup();
        using (DrawingContext context = group.Open()) context.DrawText(formatted, new Point(x, y));
        group.Freeze();
        target.Children.Add(group);
    }

    static void PaintText(FormattedText formatted, SvgStyle style, Ctx ctx) {
        bool none;
        Color color;
        if (!ResolveColor(style.Fill, style, ctx, out none, out color) || none) return;
        double alpha = color.A;
        if (style.FillOpacity < 1) alpha *= Clamp(style.FillOpacity, 0, 1);
        if (alpha <= 0) return;
        SolidColorBrush brush = new SolidColorBrush(Color.FromArgb((byte)Clamp(alpha, 0, 255), color.R, color.G, color.B));
        brush.Freeze();
        formatted.SetForegroundBrush(brush);
        if (style.Underline) formatted.SetTextDecorations(TextDecorations.Underline);
    }

    static bool IsTextPaintable(SvgStyle style, Ctx ctx) {
        bool none;
        Color color;
        if (!ResolveColor(style.Fill, style, ctx, out none, out color) || none) return false;
        double alpha = color.A;
        if (style.FillOpacity < 1) alpha *= Clamp(style.FillOpacity, 0, 1);
        return alpha > 0;
    }

    // Runs are laid out in file order: a run with its own x sits there (Pele places every tspan absolutely), one
    // without x follows the previous run's right edge. A run without y keeps the current baseline. The text
    // element's own dx/dy shift the first run. A chunk of pieces sharing a start x moves as one under text-anchor.
    static void DrawTextRuns(List<TextRun> runs, double textX, double textY, double textDx, double textDy, DrawingGroup target, Ctx ctx) {
        int chunkFirst = 0;
        double cursorX = double.IsNaN(textX) ? 0 : textX;
        double cursorY = double.IsNaN(textY) ? 0 : textY;
        for (int i = 0; i < runs.Count; i++) {
            TextRun run = runs[i];
            if (run.HasX) {
                if (i > 0) ApplyAnchor(runs, chunkFirst, i - 1);
                chunkFirst = i;
                cursorX = run.X;
            }
            cursorX += run.Dx;
            if (run.HasY) cursorY = run.Y;
            cursorY += run.Dy;
            run.X = cursorX;
            run.Y = cursorY;
            if (i == 0) {
                run.X += textDx;
                if (!double.IsNaN(textDy)) run.Y += textDy;
                cursorX = run.X;
                cursorY = run.Y;
            }
            if (IsTextPaintable(run.Style, ctx)) {
                run.Formatted = Format(run.Text, run.Style, ctx);
                if (run.Formatted != null) {
                    PaintText(run.Formatted, run.Style, ctx);
                    cursorX += run.Formatted.WidthIncludingTrailingWhitespace;
                }
            }
        }
        ApplyAnchor(runs, chunkFirst, runs.Count - 1);
        for (int i = 0; i < runs.Count; i++) {
            TextRun run = runs[i];
            if (run.Formatted == null) continue;
            DrawFormatted(run.Formatted, run.X, run.Y - run.Formatted.Baseline, target);
        }
    }
}
}
