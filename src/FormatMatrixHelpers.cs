// FormatMatrixKit: generated fixtures for the format-matrix probe. A fixture is one container
// line that holds one inline arrangement; the kit records the focus pair's marker offsets, the
// visible text and the source offset of every visible character, so a case can place its caret
// and compare what the surface draws. Nothing here reads a user file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
internal static class FormatMatrixKit {
    // ---------------------------------------------------------------- fixture model
    internal sealed class Segment {
        internal bool Markup;
        internal string Text;
    }
    internal sealed class Arrangement {
        internal string Name;
        internal readonly List<Segment> Segments = new List<Segment>();
        internal int FocusOpen, FocusText, FocusClose;
        // The same marker character in two runs ("***ab***") is one caret stop, so the focus
        // offsets widen over the neighbouring marker text.
        internal int OpenRunBefore, CloseRunAfter;
    }
    internal sealed class Fixture {
        internal string Source;
        internal string LineVisible;
        internal int LineNumber;
        internal int OpenStart, OpenLen, CloseStart, CloseLen;
        internal int VisStart, VisLen;
        internal List<int> Offsets;
    }
    internal sealed class LineBuilder {
        internal readonly StringBuilder Source = new StringBuilder();
        internal readonly StringBuilder Visible = new StringBuilder();
        internal readonly List<int> Offsets = new List<int>();
        internal int SourceLength { get { return Source.Length; } }
        internal int VisibleLength { get { return Visible.Length; } }
        internal void Markup(string text) { Source.Append(text); }
        internal void Text(string text) {
            if (text == null) return;
            for (int i = 0; i < text.Length; i++) {
                Offsets.Add(Source.Length);
                Source.Append(text[i]);
                Visible.Append(text[i]);
            }
        }
        // A drawn table-cell gap: the eye sees one space where the source holds the pipe and its
        // padding between two cells.
        internal void Gap() {
            Offsets.Add(Source.Length);
            Visible.Append(' ');
        }
    }

    // ---------------------------------------------------------------- styles
    internal sealed class StyleDef {
        internal string Short;
        internal string Open;
        internal string Close;
    }
    const string ColorOpen = "<span style=\"color:#FF0000\">";
    const string SizeOpen = "<span style=\"font-size:20px\">";
    const string SpanClose = "</span>";
    static readonly StyleDef[] Styles = {
        new StyleDef { Short = "bold", Open = "**", Close = "**" },
        new StyleDef { Short = "italicStar", Open = "*", Close = "*" },
        new StyleDef { Short = "italicUnder", Open = "_", Close = "_" },
        new StyleDef { Short = "strike", Open = "~~", Close = "~~" },
        new StyleDef { Short = "code", Open = "`", Close = "`" },
        // The link target is an in-document anchor: Markdown.OpenLink drops a "#" target before it
        // can reach its Process.Start branch, so not even a stray link activation in a self-test
        // run can start the user's browser.
        new StyleDef { Short = "link", Open = "[", Close = "](#x)" },
        new StyleDef { Short = "color", Open = ColorOpen, Close = SpanClose },
        new StyleDef { Short = "size", Open = SizeOpen, Close = SpanClose }
    };
    internal static readonly string[] Containers = {
        "paragraph", "bullet", "number", "task", "quote", "heading2", "quote-bullet", "table"
    };

    // ---------------------------------------------------------------- arrangements
    // single: the style alone. pair: one style writes its markers around another. triple: the
    // focus pair sits inside two more runs (twelve curated shapes). nested: the focus sits inside
    // one run with visible text on both sides. adjacent: a finished run touches the focus run.
    internal static List<Arrangement> Arrangements() {
        var list = new List<Arrangement>();
        for (int s = 0; s < Styles.Length; s++) list.Add(Single(Styles[s]));
        var seen = new HashSet<string>();
        for (int o = 0; o < Styles.Length; o++) {
            if (Styles[o].Short == "code") continue;   /* a code span is literal: it can only sit inside */
            for (int i = 0; i < Styles.Length; i++) {
                if (i == o) continue;
                Arrangement pair = Pair(Styles[o], Styles[i]);
                if (seen.Add(Compose(pair))) list.Add(pair);
            }
        }
        list.AddRange(Triples());
        list.AddRange(Nested());
        list.AddRange(Adjacent());
        return list;
    }
    static Segment Mk(string text) { return new Segment { Markup = true, Text = text }; }
    static Segment Vt(string text) { return new Segment { Markup = false, Text = text }; }
    static bool AllStars(string text) {
        if (String.IsNullOrEmpty(text)) return false;
        for (int i = 0; i < text.Length; i++) if (text[i] != '*') return false;
        return true;
    }
    static Arrangement Single(StyleDef style) {
        var a = new Arrangement { Name = "single-" + style.Short };
        a.Segments.Add(Mk(style.Open));
        a.Segments.Add(Vt("ab"));
        a.Segments.Add(Mk(style.Close));
        a.FocusOpen = 0;
        a.FocusText = 1;
        a.FocusClose = 2;
        return a;
    }
    static Arrangement Pair(StyleDef outer, StyleDef inner) {
        var a = new Arrangement { Name = "pair-" + outer.Short + "-" + inner.Short };
        a.Segments.Add(Mk(outer.Open));
        a.Segments.Add(Mk(inner.Open));
        a.Segments.Add(Vt("ab"));
        a.Segments.Add(Mk(inner.Close));
        a.Segments.Add(Mk(outer.Close));
        a.FocusOpen = 1;
        a.FocusText = 2;
        a.FocusClose = 3;
        if (AllStars(outer.Open + inner.Open)) a.OpenRunBefore = outer.Open.Length;
        if (AllStars(inner.Close + outer.Close)) a.CloseRunAfter = outer.Close.Length;
        return a;
    }
    // o1/c1 outermost, o3/c3 around the focus itself.
    static Arrangement Triple(string name, string o1, string c1, string o2, string c2, string o3, string c3) {
        var a = new Arrangement { Name = name };
        a.Segments.Add(Mk(o1));
        a.Segments.Add(Mk(o2));
        a.Segments.Add(Mk(o3));
        a.Segments.Add(Vt("ab"));
        a.Segments.Add(Mk(c3));
        a.Segments.Add(Mk(c2));
        a.Segments.Add(Mk(c1));
        a.FocusOpen = 2;
        a.FocusText = 3;
        a.FocusClose = 4;
        if (AllStars(o1 + o2 + o3)) a.OpenRunBefore = o1.Length + o2.Length;
        if (AllStars(c3 + c2 + c1)) a.CloseRunAfter = c2.Length + c1.Length;
        return a;
    }
    static List<Arrangement> Triples() {
        var list = new List<Arrangement>();
        list.Add(Triple("triple-01-bold-italic-strike", "**", "**", "*", "*", "~~", "~~"));
        list.Add(Triple("triple-02-bold-under-strike", "**", "**", "_", "_", "~~", "~~"));
        list.Add(Triple("triple-03-color-bold-strike", ColorOpen, SpanClose, "**", "**", "~~", "~~"));
        list.Add(Triple("triple-04-strike-bold-size", "~~", "~~", "**", "**", SizeOpen, SpanClose));
        list.Add(Triple("triple-05-link-bold-color", "[", "](#x)", "**", "**", ColorOpen, SpanClose));
        list.Add(Triple("triple-06-bold-link-size", "**", "**", "[", "](#x)", SizeOpen, SpanClose));
        list.Add(Triple("triple-07-italic-strike-color", "*", "*", "~~", "~~", ColorOpen, SpanClose));
        list.Add(Triple("triple-08-color-italic-size", ColorOpen, SpanClose, "*", "*", SizeOpen, SpanClose));
        list.Add(Triple("triple-09-strike-bold-code", "~~", "~~", "**", "**", "`", "`"));
        list.Add(Triple("triple-10-bold-italic-color", "**", "**", "*", "*", ColorOpen, SpanClose));
        list.Add(Triple("triple-11-size-bold-link", SizeOpen, SpanClose, "**", "**", "[", "](#x)"));
        list.Add(Triple("triple-12-under-link-strike", "_", "_", "[", "](#x)", "~~", "~~"));
        return list;
    }
    static Arrangement Nest(string name, string outerOpen, string outerClose, string focusOpen, string focusClose) {
        var a = new Arrangement { Name = name };
        a.Segments.Add(Mk(outerOpen));
        a.Segments.Add(Vt("a "));
        a.Segments.Add(Mk(focusOpen));
        a.Segments.Add(Vt("b"));
        a.Segments.Add(Mk(focusClose));
        a.Segments.Add(Vt(" c"));
        a.Segments.Add(Mk(outerClose));
        a.FocusOpen = 2;
        a.FocusText = 3;
        a.FocusClose = 4;
        return a;
    }
    static List<Arrangement> Nested() {
        var list = new List<Arrangement>();
        list.Add(Nest("nested-star", "**", "**", "*", "*"));
        list.Add(Nest("nested-bold", "*", "*", "**", "**"));
        list.Add(Nest("nested-strike", "~~", "~~", "**", "**"));
        list.Add(Nest("nested-code", "**", "**", "`", "`"));
        list.Add(Nest("nested-link", "[", "](#x)", "**", "**"));
        return list;
    }
    static Arrangement Adjacent(string name, string firstOpen, string firstText, string focusOpen, string focusClose) {
        var a = new Arrangement { Name = name };
        a.Segments.Add(Mk(firstOpen));
        a.Segments.Add(Vt(firstText));
        a.Segments.Add(Mk(firstOpen));
        a.Segments.Add(Mk(focusOpen));
        a.Segments.Add(Vt("b"));
        a.Segments.Add(Mk(focusClose));
        a.FocusOpen = 3;
        a.FocusText = 4;
        a.FocusClose = 5;
        return a;
    }
    static List<Arrangement> Adjacent() {
        var list = new List<Arrangement>();
        list.Add(Adjacent("adjacent-bold-italic", "**", "a", "*", "*"));
        list.Add(Adjacent("adjacent-bold-strike", "**", "a", "~~", "~~"));
        return list;
    }
    static string Compose(Arrangement a) {
        var text = new StringBuilder();
        foreach (Segment seg in a.Segments) text.Append(seg.Text);
        return text.ToString();
    }

    // ---------------------------------------------------------------- fixture build
    static string Prefix(string container) {
        switch (container) {
            case "bullet": return "- ";
            case "number": return "1. ";
            case "task": return "- [ ] ";
            case "quote": return "> ";
            case "heading2": return "## ";
            case "quote-bullet": return "> - ";
            default: return "";
        }
    }
    internal static Fixture Build(Arrangement arrangement, string container) {
        var f = new Fixture();
        var b = new LineBuilder();
        if (container == "table") {
            // A three-column table; the arrangement sits in the middle cell of the data row, with
            // the same plain visible text before and after it inside that cell.
            f.LineNumber = 3;
            b.Markup("| ");
            b.Text("l");
            b.Markup(" | ");
            b.Gap();
            b.Text("m ");
            Emit(b, arrangement, f);
            b.Text(" n");
            b.Gap();
            b.Markup(" | ");
            b.Text("r");
            b.Markup(" |");
            f.Source = "| h1 | h2 | h3 |\n| --- | --- | --- |\n" + b.Source.ToString();
        } else {
            f.LineNumber = 1;
            b.Markup(Prefix(container));
            b.Text("m ");
            Emit(b, arrangement, f);
            b.Text(" n");
            f.Source = b.Source.ToString();
        }
        f.LineVisible = b.Visible.ToString();
        f.Offsets = b.Offsets;
        return f;
    }
    static void Emit(LineBuilder b, Arrangement a, Fixture f) {
        for (int i = 0; i < a.Segments.Count; i++) {
            Segment seg = a.Segments[i];
            if (seg.Markup) {
                if (i == a.FocusOpen) {
                    f.OpenStart = b.SourceLength - a.OpenRunBefore;
                    f.OpenLen = seg.Text.Length + a.OpenRunBefore;
                }
                if (i == a.FocusClose) {
                    f.CloseStart = b.SourceLength;
                    f.CloseLen = seg.Text.Length + a.CloseRunAfter;
                }
                b.Markup(seg.Text);
            } else {
                if (i == a.FocusText) {
                    f.VisStart = b.VisibleLength;
                    f.VisLen = seg.Text.Length;
                }
                b.Text(seg.Text);
            }
        }
    }

    // ---------------------------------------------------------------- surface reading
    // One line's visible text from the line's own parse, the rule the drawing uses: replacement
    // runs are skipped, a cell gap and a <br> still separate what the eye sees. Offsets holds the
    // line-relative source offset of each visible character (a drawn gap points at its run).
    internal static string VisibleParts(MarkdownLivePreview.Session session, DocumentLine line, List<int> offsets) {
        if (offsets != null) offsets.Clear();
        MarkdownLivePreview.LineInfo info = session.Info(line);
        string value = info.Text;
        var text = new StringBuilder();
        int at = 0;
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Start >= value.Length) break;
            int start = replacement.Start;
            if (start > at) {
                for (int i = at; i < start; i++) { text.Append(value[i]); if (offsets != null) offsets.Add(i); }
            }
            int end = Math.Min(value.Length, start + replacement.Length);
            if (end > at) at = end;
            if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Column) { text.Append(' '); if (offsets != null) offsets.Add(start); }
            else if (replacement.Kind == MarkdownLivePreview.ReplacementKind.LineBreak) { text.Append('\n'); if (offsets != null) offsets.Add(start); }
            else if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Glyph && replacement.Label != null) {
                for (int i = 0; i < replacement.Label.Length; i++) { text.Append(replacement.Label[i]); if (offsets != null) offsets.Add(start); }
            }
        }
        for (int i = at; i < value.Length; i++) { text.Append(value[i]); if (offsets != null) offsets.Add(i); }
        return text.ToString();
    }
    // The toolbar's own answer for the format the next character takes (FormatAt already moves a
    // caret on the inner edge of a hidden closing run to the pair's inside).
    internal static string Flags(MarkdownLivePreview.Session session, int offset) {
        bool bold, italic, strike, code;
        int heading;
        session.FormatAt(offset, out bold, out italic, out strike, out code, out heading);
        return (bold ? "B" : "-") + (italic ? "I" : "-") + (strike ? "S" : "-") + (code ? "C" : "-");
    }
    // The colour and size the surface draws at one offset, as one comparable string.
    internal static string SpanSignature(MarkdownLivePreview.Session session, DocumentLine line, int relative) {
        int red, green, blue;
        double size;
        if (!DrawnSpan(session, line, relative, out red, out green, out blue, out size)) return "-";
        return red + "," + green + "," + blue + "/" + N(size);
    }
    // True when the source character at [relative] is part of a hidden marker run, so it is not
    // a visible character a typed character could join.
    internal static bool HiddenAt(MarkdownLivePreview.LineInfo info, int relative) {
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Kind != MarkdownLivePreview.ReplacementKind.Hidden || replacement.Length <= 0) continue;
            if (relative >= replacement.Start && relative < replacement.Start + replacement.Length) return true;
        }
        return false;
    }
    static bool DrawnSpan(MarkdownLivePreview.Session session, DocumentLine line, int relative, out int red, out int green, out int blue, out double size) {
        red = -1;
        green = -1;
        blue = -1;
        size = 0;
        bool found = false;
        MarkdownLivePreview.LineInfo info = session.Info(line);
        foreach (MarkdownLivePreview.StyledPart part in info.Styles) {
            if (relative < part.Start || relative > part.Start + part.Length) continue;
            SolidColorBrush brush = part.Color as SolidColorBrush;
            if (brush != null) {
                red = brush.Color.R;
                green = brush.Color.G;
                blue = brush.Color.B;
                found = true;
            }
            if (part.Size > 0) {
                size = part.Size;
                found = true;
            } else if (part.SizeEm > 0) {
                size = part.SizeEm;
                found = true;
            }
        }
        return found;
    }
    // Orphan tags and half markers in the source itself: the same rules the parser needs.
    internal static string SourceFault(string text) {
        if (text == null) return "null";
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = lines[n];
            string label = "line " + (n + 1);
            int star = Count(line, '*');
            if (star % 2 != 0) return label + " odd '*' count " + star;
            int tilde = Count(line, '~');
            if (tilde % 2 != 0) return label + " odd '~' count " + tilde;
            int tick = Count(line, '`');
            if (tick % 2 != 0) return label + " odd backtick count " + tick;
            int under = Count(line, '_');
            if (under % 2 != 0) return label + " odd '_' count " + under;
            if (CountOf(line, "<span") != CountOf(line, "</span")) return label + " span tags unbalanced";
            if (Count(line, '[') != Count(line, ']')) return label + " bracket count " + Count(line, '[') + "/" + Count(line, ']');
            if (line.IndexOf("****", StringComparison.Ordinal) >= 0) return label + " empty bold pair";
            if (line.IndexOf("____", StringComparison.Ordinal) >= 0) return label + " empty underline pair";
            if (line.IndexOf("~~~~", StringComparison.Ordinal) >= 0) return label + " empty strike pair";
            if (line.IndexOf("````", StringComparison.Ordinal) >= 0) return label + " empty code pair";
            if (line.IndexOf("[](", StringComparison.Ordinal) >= 0) return label + " empty link label";
        }
        return null;
    }
    // A piece of markup the surface still draws as text, or null when the line is clean.
    internal static string MarkupFault(string visible) {
        if (visible == null) return "null";
        if (visible.IndexOf('*') >= 0) return "'*'";
        if (visible.IndexOf('_') >= 0) return "'_'";
        if (visible.IndexOf('`') >= 0) return "backtick";
        if (visible.IndexOf('~') >= 0) return "'~'";
        if (visible.IndexOf("<span", StringComparison.OrdinalIgnoreCase) >= 0) return "<span";
        if (visible.IndexOf("</span", StringComparison.OrdinalIgnoreCase) >= 0) return "</span";
        if (visible.IndexOf('[') >= 0) return "'['";
        if (visible.IndexOf(']') >= 0) return "']'";
        return null;
    }

    // ---------------------------------------------------------------- report
    internal sealed class FailureGroup {
        internal string Signature;
        internal int Count;
        internal readonly List<string> Examples = new List<string>();
    }
    internal static void AddFailure(Dictionary<string, FailureGroup> groups, string signature, string example) {
        FailureGroup group;
        if (!groups.TryGetValue(signature, out group)) {
            group = new FailureGroup { Signature = signature };
            groups.Add(signature, group);
        }
        group.Count++;
        if (group.Examples.Count < 3) group.Examples.Add(example);
    }

    // ---------------------------------------------------------------- text helpers
    internal static int Count(string text, char value) {
        int count = 0;
        for (int i = 0; i < text.Length; i++) if (text[i] == value) count++;
        return count;
    }
    internal static int CountOf(string text, string piece) {
        int count = 0;
        for (int at = text.IndexOf(piece, StringComparison.Ordinal); at >= 0; at = text.IndexOf(piece, at + piece.Length, StringComparison.Ordinal)) count++;
        return count;
    }
    internal static string N(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }
    internal static string Esc(string text) {
        if (text == null) return "null";
        string value = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        if (value.Length > 220) value = value.Substring(0, 220) + "...";
        return value;
    }
}
}
