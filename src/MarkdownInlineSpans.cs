using System;
using System.Collections.Generic;
using System.Text;
using LineInfo = MDNote.MarkdownLivePreview.LineInfo;
using Replacement = MDNote.MarkdownLivePreview.Replacement;
using ReplacementKind = MDNote.MarkdownLivePreview.ReplacementKind;
using StyledPart = MDNote.MarkdownLivePreview.StyledPart;
using LinkPart = MDNote.MarkdownLivePreview.LinkPart;

namespace MDNote {
// The inline pair map of one line: every pair the live preview parser found, with both
// markers' source positions and text. Enter, Backspace/Delete and selection replacement read
// this map instead of pairing raw marker runs themselves, so a link ("[" + "](url)") and a
// tag ("<span ...>" + "</span>") are pairs like emphasis, even though their two runs differ
// in length. It is built from the line's cached LineInfo only (no document access) and its
// cost is bounded by the line's markers, not by the line's length.
internal static class MarkdownInlineSpans {
    internal enum SpanKind { Emphasis, Code, Link, Tag, Atomic }

    // Content is [OpenStart+OpenLen, CloseStart). An Atomic span covers a unit that must never
    // be split (image, footnote reference, autolink, HTML comment) whole in OpenStart/OpenLen;
    // its CloseStart is -1.
    internal struct Span {
        internal SpanKind Kind;
        internal int OpenStart, OpenLen, CloseStart, CloseLen;
        internal string OpenText, CloseText;
    }

    const int MaxStepPasses = 8;

    // The line's pairs in source order (by opening offset). Emphasis and code use the parser's
    // own rule: a styled run sits between two same-length hidden runs. Links come from
    // info.Links and tags pair by name with the same depth counting the parser used.
    internal static List<Span> Build(LineInfo info, string text) {
        var spans = new List<Span>();
        if (info == null) return spans;
        if (text == null) text = info.Text == null ? string.Empty : info.Text;
        var byStart = new Dictionary<int, int>();
        var byEnd = new Dictionary<int, int>();
        for (int i = 0; i < info.Replacements.Count; i++) {
            Replacement run = info.Replacements[i];
            if (run.Length <= 0) continue;
            if (run.Kind == ReplacementKind.Image) {
                AddAtomic(spans, run.Start, run.Length, text);
                continue;
            }
            if (run.Kind != ReplacementKind.Hidden || run.Prefix) continue;
            if (!byStart.ContainsKey(run.Start)) byStart.Add(run.Start, i);
            int end = run.Start + run.Length;
            if (!byEnd.ContainsKey(end)) byEnd.Add(end, i);
        }
        var used = new bool[info.Replacements.Count];
        // Tags: the parser hides a tag pair only when FindTagClose paired it, so a stack per
        // name over the hidden runs reproduces the same pairing (case-insensitive, as there).
        var openers = new Dictionary<string, Stack<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < info.Replacements.Count; i++) {
            Replacement run = info.Replacements[i];
            if (run.Kind != ReplacementKind.Hidden || run.Prefix || run.Length <= 0) continue;
            if (used[i]) continue;
            string name;
            bool closing;
            if (!TagRun(text, run.Start, run.Length, out name, out closing)) continue;
            if (!closing) {
                Stack<int> stack;
                if (!openers.TryGetValue(name, out stack)) {
                    stack = new Stack<int>();
                    openers.Add(name, stack);
                }
                stack.Push(i);
                continue;
            }
            Stack<int> found;
            if (!openers.TryGetValue(name, out found) || found.Count == 0) continue;
            int openIndex = found.Pop();
            Span tag = MakeSpan(SpanKind.Tag, info.Replacements[openIndex], run, text);
            if (tag.OpenLen <= 0 || tag.CloseLen <= 0) continue;
            spans.Add(tag);
            used[i] = true;
            used[openIndex] = true;
        }
        // Links: the "[" run at link.Start and the run that ends the LinkPart are the two
        // markers. A footnote and an autolink are whole units, never split.
        if (info.Links != null) {
            for (int i = 0; i < info.Links.Count; i++) {
                LinkPart link = info.Links[i];
                if (link == null || link.Length <= 0) continue;
                bool footnote = link.Url != null && link.Url.StartsWith("#^", StringComparison.Ordinal);
                bool angle = link.Start >= 0 && link.Start < text.Length && text[link.Start] == '<';
                if (footnote || angle) {
                    AddAtomic(spans, link.Start, link.Length, text);
                    continue;
                }
                int openIndex, closeIndex;
                if (!byStart.TryGetValue(link.Start, out openIndex)) continue;
                if (!byEnd.TryGetValue(link.Start + link.Length, out closeIndex)) continue;
                if (openIndex == closeIndex || used[openIndex] || used[closeIndex]) continue;
                Replacement open = info.Replacements[openIndex];
                Replacement close = info.Replacements[closeIndex];
                if (open.Kind != ReplacementKind.Hidden || close.Kind != ReplacementKind.Hidden) continue;
                Span span = MakeSpan(SpanKind.Link, open, close, text);
                if (span.OpenLen <= 0 || span.CloseLen <= 0) continue;
                spans.Add(span);
                used[openIndex] = true;
                used[closeIndex] = true;
            }
        }
        // An HTML comment that closes on this line is one hidden run and one unit.
        for (int i = 0; i < info.Replacements.Count; i++) {
            if (used[i]) continue;
            Replacement run = info.Replacements[i];
            if (run.Kind != ReplacementKind.Hidden || run.Prefix || run.Length < 4) continue;
            if (run.Start < 0 || run.Start + 3 >= text.Length) continue;
            if (text[run.Start] == '<' && text[run.Start + 1] == '!' && text[run.Start + 2] == '-' && text[run.Start + 3] == '-') {
                AddAtomic(spans, run.Start, run.Length, text);
                used[i] = true;
            }
        }
        // Emphasis and code: the same adjacency rule PartnerMarker uses (a styled part starts
        // right after the opener and ends right before the closer, and both runs have the same
        // length). Tags are already claimed, and a link label's own Style.Link is not "styled".
        var seen = new HashSet<int>();
        for (int i = 0; i < info.Styles.Count; i++) {
            StyledPart part = info.Styles[i];
            bool styled = (part.Style & (MarkdownLivePreview.Style.Bold | MarkdownLivePreview.Style.Italic |
                MarkdownLivePreview.Style.Strike | MarkdownLivePreview.Style.Code)) != 0
                || part.Color != null || part.Size > 0 || part.SizeEm > 0;
            if (!styled) continue;
            int openIndex, closeIndex;
            if (!byEnd.TryGetValue(part.Start, out openIndex)) continue;
            if (!byStart.TryGetValue(part.Start + part.Length, out closeIndex)) continue;
            if (openIndex == closeIndex || used[openIndex] || used[closeIndex]) continue;
            Replacement open = info.Replacements[openIndex];
            Replacement close = info.Replacements[closeIndex];
            if (open.Kind != ReplacementKind.Hidden || close.Kind != ReplacementKind.Hidden) continue;
            if (open.Length <= 0 || close.Length <= 0 || open.Length > 4 || open.Length != close.Length) continue;
            if (open.Start >= 0 && open.Start < text.Length && text[open.Start] == '<') continue;
            if (!seen.Add(open.Start)) continue;
            SpanKind kind = open.Start >= 0 && open.Start < text.Length && text[open.Start] == '`'
                ? SpanKind.Code : SpanKind.Emphasis;
            Span span = MakeSpan(kind, open, close, text);
            if (span.OpenLen <= 0 || span.CloseLen <= 0) continue;
            spans.Add(span);
            used[openIndex] = true;
            used[closeIndex] = true;
        }
        spans.Sort(delegate(Span a, Span b) {
            int order = a.OpenStart.CompareTo(b.OpenStart);
            return order != 0 ? order : a.CloseStart.CompareTo(b.CloseStart);
        });
        return spans;
    }

    // The caret moved from the inner edge of a marker to that pair's outer side, the walk
    // InlineEnterEdge does today, with links and tags included: "**|ab**" -> "|**ab**",
    // "**ab|**" -> "**ab**|". A caret strictly inside an Atomic unit goes to the nearer edge
    // (a tie keeps it in front). The same direction may not reverse within one call, so nested
    // runs step through each pair in turn instead of oscillating.
    internal static int StepOut(List<Span> spans, int relative) {
        if (spans == null) return relative;
        int direction = 0;
        for (int pass = 0; pass < MaxStepPasses; pass++) {
            int moved = -1;
            for (int i = 0; i < spans.Count; i++) {
                Span span = spans[i];
                if (span.Kind == SpanKind.Atomic) {
                    int end = span.OpenStart + span.OpenLen;
                    if (span.OpenStart < relative && relative < end) {
                        moved = relative - span.OpenStart <= end - relative ? span.OpenStart : end;
                        direction = moved < relative ? -1 : 1;
                        break;
                    }
                    continue;
                }
                if (span.CloseLen <= 0) continue;
                // A caret strictly inside a marker run ("**a*|**b*") would split the run in two;
                // it leaves by the run's outer side, so the pair stays whole on one line.
                if (span.CloseStart < relative && relative < span.CloseStart + span.CloseLen && direction >= 0) {
                    moved = span.CloseStart + span.CloseLen;
                    direction = 1;
                    break;
                }
                if (span.OpenStart < relative && relative < span.OpenStart + span.OpenLen && direction <= 0) {
                    moved = span.OpenStart;
                    direction = -1;
                    break;
                }
                if (span.CloseStart == relative && direction >= 0) {
                    moved = span.CloseStart + span.CloseLen;
                    direction = 1;
                    break;
                }
                if (span.OpenStart + span.OpenLen == relative && direction <= 0) {
                    moved = span.OpenStart;
                    direction = -1;
                    break;
                }
            }
            if (moved < 0 || moved == relative) break;
            relative = moved;
        }
        return relative;
    }

    // The span whose opening or closing run is this Replacement, with the side it is on.
    // PairOf callers that used to re-pair raw runs ask here instead.
    internal static Span? SpanOf(List<Span> spans, Replacement marker, out bool isOpener) {
        isOpener = false;
        if (spans == null) return null;
        for (int i = 0; i < spans.Count; i++) {
            Span span = spans[i];
            if (span.Kind == SpanKind.Atomic) {
                if (marker.Start == span.OpenStart) {
                    isOpener = true;
                    return span;
                }
                continue;
            }
            if (span.OpenLen > 0 && marker.Start == span.OpenStart && marker.Length == span.OpenLen) {
                isOpener = true;
                return span;
            }
            if (span.CloseLen > 0 && marker.Start == span.CloseStart && marker.Length == span.CloseLen) {
                isOpener = false;
                return span;
            }
        }
        return null;
    }

    // True when the position lies on a marker the map knows: inside or on either edge of a
    // pair's opening or closing run, or on an atomic unit's edge. Enter uses it to tell a caret
    // the parser already placed ("**a**|*b*", between a closer and an opener) from plain text,
    // where the raw run snap (H9) still guards a literal "***" from being split.
    internal static bool TouchesMarker(List<Span> spans, int relative) {
        if (spans == null) return false;
        for (int i = 0; i < spans.Count; i++) {
            Span span = spans[i];
            if (span.Kind == SpanKind.Atomic) {
                if (relative >= span.OpenStart && relative <= span.OpenStart + span.OpenLen) return true;
                continue;
            }
            if (relative >= span.OpenStart && relative <= span.OpenStart + span.OpenLen) return true;
            if (span.CloseLen > 0 && relative >= span.CloseStart && relative <= span.CloseStart + span.CloseLen) return true;
        }
        return false;
    }

    static void AddAtomic(List<Span> spans, int start, int length, string text) {
        if (start < 0 || length <= 0 || start + length > text.Length) return;
        Span span = new Span();
        span.Kind = SpanKind.Atomic;
        span.OpenStart = start;
        span.OpenLen = length;
        span.CloseStart = -1;
        span.CloseLen = 0;
        span.OpenText = text.Substring(start, length);
        span.CloseText = string.Empty;
        spans.Add(span);
    }

    static Span MakeSpan(SpanKind kind, Replacement open, Replacement close, string text) {
        Span span = new Span();
        span.Kind = kind;
        span.OpenStart = open.Start;
        span.OpenLen = open.Length;
        span.CloseStart = close.Start;
        span.CloseLen = close.Length;
        span.OpenText = open.Start >= 0 && open.Start + open.Length <= text.Length
            ? text.Substring(open.Start, open.Length) : string.Empty;
        span.CloseText = close.Start >= 0 && close.Start + close.Length <= text.Length
            ? text.Substring(close.Start, close.Length) : string.Empty;
        return span;
    }

    // The hidden run is one whole tag: "<name ...>" or "</name>" (the shapes MatchesTag and
    // FindTagClose accept). Anything else (a stray "<", an autolink bracket) answers false.
    static bool TagRun(string text, int start, int length, out string name, out bool closing) {
        name = null;
        closing = false;
        if (text == null || start < 0 || length < 3 || start + length > text.Length) return false;
        if (text[start] != '<') return false;
        int p = start + 1;
        if (p < text.Length && text[p] == '/') {
            closing = true;
            p++;
        }
        int nameStart = p;
        while (p < text.Length && (char.IsLetterOrDigit(text[p]) || text[p] == '-')) p++;
        if (p == nameStart) return false;
        name = text.Substring(nameStart, p - nameStart);
        if (closing) {
            if (p >= text.Length || text[p] != '>') return false;
            return p + 1 - start == length;
        }
        if (p >= text.Length) return false;
        if (text[p] != ' ' && text[p] != '\t') return false;
        return text[start + length - 1] == '>';
    }

    // Enter's split at a settled caret: the closer texts for the left line and the opener
    // texts for the right line. Closers go inner-to-outer (CloseStart ascending) and openers
    // outer-to-inner (OpenStart ascending), which keeps the source order of crossed runs
    // ("***ab***" splits into "***a***" / "***b***"). A side whose content is blank gets no
    // pair, so "****", "[](" and "<span></span>" are never written. editStart/editEnd are the
    // whitespace run around the caret; the caller keeps that run outside the markers.
    internal static bool Split(List<Span> spans, string text, int relative, out int editStart, out int editEnd, out string closing, out string opening) {
        closing = string.Empty;
        opening = string.Empty;
        if (text == null) text = string.Empty;
        if (relative < 0) relative = 0;
        if (relative > text.Length) relative = text.Length;
        editStart = relative;
        editEnd = relative;
        if (spans == null) return false;
        var closeParts = new List<Span>();
        var openParts = new List<Span>();
        // Visible content means a character that is neither blank nor another pair's marker, so a
        // caret next to "~~" inside "**~~ab~~**" still sees the outer pair's left side as empty.
        bool[] mask = MarkerMask(spans, text.Length);
        for (int i = 0; i < spans.Count; i++) {
            Span span = spans[i];
            if (span.Kind == SpanKind.Atomic || span.CloseLen <= 0) continue;
            if (!(span.OpenStart < relative && span.CloseStart > relative)) continue;
            bool leftVisible = AnyContent(mask, text, span.OpenStart + span.OpenLen, relative);
            bool rightVisible = AnyContent(mask, text, relative, span.CloseStart);
            if (leftVisible && rightVisible) {
                closeParts.Add(span);
                openParts.Add(span);
            } else if (leftVisible != rightVisible) {
                // One side empty: closing on one line and reopening on the other would leave a
                // lone marker, and Split cannot remove the far marker. No pair split (the caller
                // breaks the line as it stands); StepOut already leaves a caret that touches the
                // marker outside the pair, so only a caret behind blanks reaches this.
                return false;
            }
        }
        if (closeParts.Count == 0 && openParts.Count == 0) return false;
        closeParts.Sort(delegate(Span a, Span b) { return a.CloseStart.CompareTo(b.CloseStart); });
        openParts.Sort(delegate(Span a, Span b) { return a.OpenStart.CompareTo(b.OpenStart); });
        for (int i = 0; i < closeParts.Count; i++) closing += closeParts[i].CloseText;
        for (int i = 0; i < openParts.Count; i++) opening += openParts[i].OpenText;
        while (editStart > 0 && (text[editStart - 1] == ' ' || text[editStart - 1] == '\t')) editStart--;
        while (editEnd < text.Length && (text[editEnd] == ' ' || text[editEnd] == '\t')) editEnd++;
        return true;
    }

    // Selection replacement (typing over a selection, cut, paste): restore the markers the
    // selection cut off next to the inserted text instead of widening the selection to whole
    // pairs. "**a|b**" with Z -> "**aZ**"; a pair whose surviving content has no visible
    // character is removed whole, so "****" and "[](" never remain. The range was already
    // widened to marker edges by the caller. `caret` is the position right after the inserted
    // text (before any restored closer) in the returned line. The inserted text takes the format
    // of the first selected character: a cut closer is restored after the text when a selected
    // character lay inside the pair before it, a cut opener when a selected character lay before
    // the pair; when the selection began on the marker itself and went on past it, the marker is
    // restored in front of the text instead (the first selected character was on the far side).
    // The caller joins a selection that spans two lines into one string and one span list (Join).
    // An underscore pair the edit leaves next to a word character on its outer side stops parsing
    // (CommonMark: "m_ab_" is no emphasis), so such a pair is rewritten with "*" / "**" in the same
    // result (UnderscoreIntraword). The rewrite is one character for one, so the caret is unchanged.
    internal static string Rebalance(List<Span> spans, string text, int start, int end, string inserted, out int caret) {
        if (text == null) text = string.Empty;
        if (inserted == null) inserted = string.Empty;
        List<Span> marked;
        string markedText;
        if (!MarkUnderscores(spans, text, inserted, out marked, out markedText))
            return RebalanceCore(spans, text, start, end, inserted, out caret);
        string result = RebalanceCore(marked, markedText, start, end, inserted, out caret);
        return ResolveUnderscores(result);
    }

    static string RebalanceCore(List<Span> spans, string text, int start, int end, string inserted, out int caret) {
        if (text == null) text = string.Empty;
        if (inserted == null) inserted = string.Empty;
        if (start < 0) start = 0;
        if (start > text.Length) start = text.Length;
        if (end < start) end = start;
        if (end > text.Length) end = text.Length;
        caret = start + inserted.Length;
        // A no-op call must not be able to empty a pair.
        if (start == end && inserted.Length == 0) return text;
        var edits = new List<Edit>();
        var restores = new List<Restore>();
        int seq = 0;
        // Which characters are a pair's own marker: a pair counts as holding content only when a
        // character that is neither whitespace nor another pair's marker is left inside it, so a
        // nested "**~~a~~**" loses both pairs when "a" goes and never keeps "****".
        bool[] mask = MarkerMask(spans, text.Length);
        Edit main = new Edit();
        main.Start = start;
        main.Length = end - start;
        main.Kind = 0;
        main.Seq = 0;
        main.Text = inserted;
        edits.Add(main);
        if (spans != null) {
            for (int i = 0; i < spans.Count; i++) {
                Span span = spans[i];
                if (span.Kind == SpanKind.Atomic || span.CloseLen <= 0) continue;
                int openEnd = span.OpenStart + span.OpenLen;
                // A plain insertion between characters touches no marker, so only a non-empty
                // selection can cut one off.
                bool cut = end > start;
                bool openIn = cut && span.OpenStart < end && openEnd > start;
                bool closeIn = cut && span.CloseStart < end && span.CloseStart + span.CloseLen > start;
                int closeEnd = span.CloseStart + span.CloseLen;
                // The inserted text takes the format of the first selected character, so each cut
                // marker is restored on the side that keeps that character's format. A deletion
                // has no text to place: every restore goes after the (empty) text, where two
                // restores that meet can still merge.
                bool place = inserted.Length > 0;
                if (openIn && closeIn) {
                    // Both markers go with the selection. When every selected character lay
                    // between them, the first one was inside the pair, so the pair is kept around
                    // the new text (the same selection reads as "**|ab|**" or "|**ab**|").
                    if (place && HasContent(inserted) && !AnyChar(mask, start, span.OpenStart) &&
                        !AnyChar(mask, closeEnd, end) &&
                        AnyChar(mask, Math.Max(start, openEnd), Math.Min(end, span.CloseStart))) {
                        restores.Add(MakeRestore(start, span.OpenStart, span.OpenText, seq++, true, true));
                        restores.Add(MakeRestore(end, span.CloseStart, span.CloseText, seq++, false, false));
                    }
                    continue;
                }
                if (closeIn) {
                    // A selected character before the closer is inside the pair, so the text goes
                    // inside and the closer after it. A selection that starts on the closer and runs
                    // on past it starts outside the pair: the closer is restored in front of the text.
                    bool pre = place && !AnyChar(mask, start, span.CloseStart) &&
                        AnyChar(mask, Math.Max(start, closeEnd), end);
                    bool left = openEnd <= start && AnyContent(mask, text, openEnd, start);
                    if (!left && (pre || !HasContent(inserted))) {
                        AddRemove(edits, span.OpenStart, span.OpenLen, seq++);
                        continue;
                    }
                    restores.Add(MakeRestore(pre ? start : end, span.CloseStart, span.CloseText, seq++, false, pre));
                } else if (openIn) {
                    // The mirror case: a selection that starts before the opener keeps the text
                    // plain and restores the opener after it; one that starts on the opener and has
                    // characters after it starts inside the pair, so the opener goes first.
                    bool pre = place && !AnyChar(mask, start, span.OpenStart) &&
                        AnyChar(mask, Math.Max(start, openEnd), end);
                    bool right = end <= span.CloseStart && AnyContent(mask, text, end, span.CloseStart);
                    if (!right && !(pre && HasContent(inserted))) {
                        AddRemove(edits, span.CloseStart, span.CloseLen, seq++);
                        continue;
                    }
                    restores.Add(MakeRestore(pre ? start : end, span.OpenStart, span.OpenText, seq++, true, pre));
                } else if (cut && openEnd <= start && end <= span.CloseStart) {
                    // Typing between characters leaves the pair alone; only a selection that
                    // took the content away can empty it.
                    if (!AnyContent(mask, text, openEnd, start) && !HasContent(inserted) &&
                        !AnyContent(mask, text, end, span.CloseStart)) {
                        AddRemove(edits, span.OpenStart, span.OpenLen, seq++);
                        AddRemove(edits, span.CloseStart, span.CloseLen, seq++);
                    } else {
                        // The selection ends on the closer (or starts on the opener) and leaves a blank
                        // right inside it ("**a b**" minus "b"): the pair would stop parsing. The marker is
                        // taken out and restored at the edit, so the blank-run repair below moves the
                        // blank outside it, the same way it does for a restored marker.
                        char leftLast = inserted.Length > 0 ? inserted[inserted.Length - 1] : (start > 0 ? text[start - 1] : 'x');
                        char rightFirst = inserted.Length > 0 ? inserted[0] : (end < text.Length ? text[end] : 'x');
                        if (end == span.CloseStart && (leftLast == ' ' || leftLast == '\t')) {
                            AddRemove(edits, span.CloseStart, span.CloseLen, seq++);
                            restores.Add(MakeRestore(end, span.CloseStart, span.CloseText, seq++, false, false));
                        }
                        if (start == openEnd && (rightFirst == ' ' || rightFirst == '\t')) {
                            AddRemove(edits, span.OpenStart, span.OpenLen, seq++);
                            restores.Add(MakeRestore(start, span.OpenStart, span.OpenText, seq++, true, true));
                        }
                    }
                }
            }
        }
        restores.Sort(delegate(Restore a, Restore b) {
            int order = a.Pos.CompareTo(b.Pos);
            if (order != 0) return order;
            order = (a.Opener ? 1 : 0).CompareTo(b.Opener ? 1 : 0);
            if (order != 0) return order;
            order = a.Anchor.CompareTo(b.Anchor);
            return order != 0 ? order : a.Seq.CompareTo(b.Seq);
        });
        var keep = new bool[restores.Count];
        for (int i = 0; i < keep.Length; i++) keep[i] = true;
        for (int i = 0; i + 1 < restores.Count; i++) {
            if (restores[i].Pos != restores[i + 1].Pos) continue;
            if (!BannedBoundary(restores[i].Text, restores[i + 1].Text)) continue;
            keep[i] = false;
            keep[i + 1] = false;
        }
        for (int i = 0; i < restores.Count; i++) {
            if (!keep[i]) continue;
            Edit insert = new Edit();
            insert.Start = restores[i].Pos;
            insert.Length = 0;
            // Kinds order the edits that share an offset: a restore in front of the text (closer,
            // then opener), the text itself, the restores after it, then removals.
            insert.Kind = restores[i].Pre ? (restores[i].Opener ? -1 : -2) : (restores[i].Opener ? 4 : 2);
            // The restores that share an offset and a kind keep the sorted order above (closers
            // inner-to-outer, openers outer-to-inner in source order), not the span-list order.
            insert.Seq = i;
            insert.Text = restores[i].Text;
            edits.Add(insert);
        }
        edits.Sort(delegate(Edit a, Edit b) {
            int order = a.Start.CompareTo(b.Start);
            if (order != 0) return order;
            order = a.Kind.CompareTo(b.Kind);
            return order != 0 ? order : a.Seq.CompareTo(b.Seq);
        });
        var built = new StringBuilder(text.Length + inserted.Length + 16);
        int pos = 0;
        var output = new List<Restored>();
        for (int i = 0; i < edits.Count; i++) {
            Edit edit = edits[i];
            if (edit.Start < pos || edit.Start > text.Length) continue;
            built.Append(text, pos, edit.Start - pos);
            if (edit.Kind == 0) caret = built.Length + (edit.Text == null ? 0 : edit.Text.Length);
            if (edit.Kind != 0 && edit.Kind != 5) {
                Restored item = new Restored();
                item.Pos = built.Length;
                item.Len = edit.Text == null ? 0 : edit.Text.Length;
                item.Opener = edit.Kind == 4 || edit.Kind == -1;
                output.Add(item);
            }
            if (edit.Text != null) built.Append(edit.Text);
            pos = edit.Start + edit.Length;
        }
        if (pos < text.Length) built.Append(text, pos, text.Length - pos);
        string result = built.ToString();
        if (caret < 0) caret = 0;
        if (caret > result.Length) caret = result.Length;
        // A restored marker must not sit against whitespace on its content side, or the pair
        // stops parsing ("**a **"); move that run outside the marker. A later restore's
        // recorded position is remapped through the move. Restored markers that touch (nested
        // pairs: "~~" then "**") move as one run in their order, so the whole run crosses the
        // whitespace together; the swap keeps the line length, so later positions stay valid.
        bool typedBlank = inserted.Length > 0 && inserted.Trim(' ', '\t').Length == 0;
        int idx = 0;
        while (idx < output.Count) {
            Restored item = output[idx];
            int groupStart = item.Pos;
            int groupEnd = item.Pos + item.Len;
            int last = idx;
            while (last + 1 < output.Count && output[last + 1].Opener == item.Opener && output[last + 1].Pos == groupEnd) {
                last++;
                groupEnd += output[last].Len;
            }
            int total = groupEnd - groupStart;
            idx = last + 1;
            if (groupStart < 0 || groupEnd > result.Length) continue;
            if (!item.Opener) {
                int from = groupStart;
                while (from > 0 && (result[from - 1] == ' ' || result[from - 1] == '\t')) from--;
                int run = groupStart - from;
                if (run <= 0) continue;
                string markers = result.Substring(groupStart, total);
                string spaces = result.Substring(from, run);
                result = result.Substring(0, from) + markers + spaces + result.Substring(groupEnd);
                // A caret that was behind the blank follows it out when the text typed there is only blank
                // ("**a|b**" + space gives "**a** |", the user left the pair); a replacement that carries
                // content stays inside the pair, before the moved blank.
                if (caret > from && caret <= groupStart) {
                    if (typedBlank) caret += total; else caret -= run;
                }
            } else {
                int to = groupEnd;
                while (to < result.Length && (result[to] == ' ' || result[to] == '\t')) to++;
                int run = to - groupEnd;
                if (run <= 0) continue;
                string markers = result.Substring(groupStart, total);
                string spaces = result.Substring(groupEnd, run);
                result = result.Substring(0, groupStart) + spaces + markers + result.Substring(to);
                // A deletion leaves the caret where the removed text was: behind the opener, so it
                // follows the opener across the blank ("** |b**" is drawn "  **|b**").
                if (inserted.Length == 0 && caret >= groupEnd && caret <= to) caret = to;
            }
        }
        if (caret < 0) caret = 0;
        if (caret > result.Length) caret = result.Length;
        return result;
    }

    static void AddRemove(List<Edit> edits, int start, int length, int seq) {
        if (length <= 0) return;
        Edit edit = new Edit();
        edit.Start = start;
        edit.Length = length;
        // Last among the edits that share an offset, so an insert at the start of a removed run
        // still lands before it instead of being skipped as overlapping.
        edit.Kind = 5;
        edit.Seq = seq;
        edit.Text = string.Empty;
        edits.Add(edit);
    }

    static Restore MakeRestore(int pos, int anchor, string text, int seq, bool opener, bool pre) {
        Restore restore = new Restore();
        restore.Pos = pos;
        restore.Anchor = anchor;
        restore.Text = text;
        restore.Seq = seq;
        restore.Opener = opener;
        restore.Pre = pre;
        return restore;
    }

    // True for every character that is part of a pair's opening or closing run. An atomic unit
    // (image, footnote, autolink) is not a marker: it is drawn, so it counts as a character.
    static bool[] MarkerMask(List<Span> spans, int length) {
        var mask = new bool[length];
        if (spans == null) return mask;
        for (int i = 0; i < spans.Count; i++) {
            Span span = spans[i];
            if (span.Kind == SpanKind.Atomic || span.CloseLen <= 0) continue;
            MarkRun(mask, span.OpenStart, span.OpenLen);
            MarkRun(mask, span.CloseStart, span.CloseLen);
        }
        return mask;
    }

    static void MarkRun(bool[] mask, int start, int length) {
        int last = Math.Min(mask.Length, start + length);
        for (int p = Math.Max(0, start); p < last; p++) mask[p] = true;
    }

    // Any drawn character (whitespace included) in [start, end): not part of a marker run.
    static bool AnyChar(bool[] mask, int start, int end) {
        if (start < 0) start = 0;
        if (end > mask.Length) end = mask.Length;
        for (int i = start; i < end; i++)
            if (!mask[i]) return true;
        return false;
    }

    // Any drawn, non-blank character in [start, end): what keeps a pair from being empty.
    static bool AnyContent(bool[] mask, string text, int start, int end) {
        if (start < 0) start = 0;
        if (end > mask.Length) end = mask.Length;
        for (int i = start; i < end; i++)
            if (!mask[i] && text[i] != ' ' && text[i] != '\t') return true;
        return false;
    }

    // The span map of two lines joined into one string (first line text + last line text): the
    // last line's spans move right by the first line's length. A selection that runs from one
    // line into another is rebalanced as if the two lines were one, since the text left of
    // the selection and the text right of it end up on one line.
    internal static List<Span> Join(List<Span> first, List<Span> last, int lastDelta) {
        var joined = new List<Span>();
        if (first != null) joined.AddRange(first);
        if (last == null) return joined;
        for (int i = 0; i < last.Count; i++) {
            Span span = last[i];
            span.OpenStart += lastDelta;
            if (span.CloseStart >= 0) span.CloseStart += lastDelta;
            joined.Add(span);
        }
        return joined;
    }

    // ---- underscore emphasis ----
    // Private-use characters stand in for the "_" of each underscore pair while Rebalance works,
    // one character per pair, so the pair can be found again in the result wherever its markers
    // ended up (a restored marker, a marker that moved past whitespace).
    const char SentinelBase = '\uE000';
    const int MaxSentinels = 256;

    static bool AllChar(string value, char c) {
        if (string.IsNullOrEmpty(value)) return false;
        for (int i = 0; i < value.Length; i++)
            if (value[i] != c) return false;
        return true;
    }

    // A pair whose two runs are underscores only ("_" or "__"). A stacked run that mixes kinds
    // ("**_") is not one: its other characters keep the pair alive next to a word.
    internal static bool IsUnderscorePair(Span span) {
        if (span.Kind != SpanKind.Emphasis || span.CloseLen <= 0) return false;
        if (span.OpenText == null || span.CloseText == null) return false;
        if (span.OpenText.Length != span.OpenLen || span.CloseText.Length != span.CloseLen) return false;
        return AllChar(span.OpenText, '_') && AllChar(span.CloseText, '_');
    }

    // True when the underscore run [start, start+length) of `text` can no longer open (opener) or
    // close (closer) emphasis: CommonMark reads an "_" that touches a letter or digit on its outer
    // side as part of the word ("m_ab_ n", "m _ab_n"). Pure, on the line text.
    internal static bool UnderscoreIntraword(string text, int start, int length, bool opener) {
        if (text == null) return false;
        int outer = opener ? start - 1 : start + length;
        if (outer < 0 || outer >= text.Length) return false;
        return char.IsLetterOrDigit(text[outer]);
    }

    // The text and span list with every underscore pair's markers swapped for its sentinel. False
    // when the line has no underscore pair (nothing to track) or already holds a sentinel character.
    static bool MarkUnderscores(List<Span> spans, string text, string inserted, out List<Span> marked, out string markedText) {
        marked = null;
        markedText = text;
        if (spans == null || spans.Count == 0) return false;
        bool any = false;
        for (int i = 0; i < spans.Count; i++)
            if (IsUnderscorePair(spans[i])) { any = true; break; }
        if (!any) return false;
        if (HasSentinel(text) || HasSentinel(inserted)) return false;
        char[] chars = text.ToCharArray();
        marked = new List<Span>(spans.Count);
        int pair = 0;
        for (int i = 0; i < spans.Count; i++) {
            Span span = spans[i];
            if (IsUnderscorePair(span) && pair < MaxSentinels &&
                span.OpenStart >= 0 && span.OpenStart + span.OpenLen <= chars.Length &&
                span.CloseStart >= 0 && span.CloseStart + span.CloseLen <= chars.Length) {
                char sentinel = (char)(SentinelBase + pair);
                pair++;
                for (int p = 0; p < span.OpenLen; p++) chars[span.OpenStart + p] = sentinel;
                for (int p = 0; p < span.CloseLen; p++) chars[span.CloseStart + p] = sentinel;
                span.OpenText = new string(sentinel, span.OpenLen);
                span.CloseText = new string(sentinel, span.CloseLen);
            }
            marked.Add(span);
        }
        markedText = new string(chars);
        return true;
    }

    static string UnmarkUnderscores(string value) {
        if (!HasSentinel(value)) return value;
        char[] chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] >= SentinelBase && chars[i] < SentinelBase + MaxSentinels) chars[i] = '_';
        return new string(chars);
    }

    static bool HasSentinel(string value) {
        if (value == null) return false;
        for (int i = 0; i < value.Length; i++)
            if (value[i] >= SentinelBase && value[i] < SentinelBase + MaxSentinels) return true;
        return false;
    }

    // Every sentinel back to "_", or to "*" for a pair whose opener or closer ended up against a
    // word character. A marker whose partner is gone (a lone run) is written as "_" again.
    static string ResolveUnderscores(string result) {
        if (string.IsNullOrEmpty(result) || !HasSentinel(result)) return result;
        var firstRun = new Dictionary<int, int[]>();
        var lastRun = new Dictionary<int, int[]>();
        int p = 0;
        while (p < result.Length) {
            char c = result[p];
            if (c < SentinelBase || c >= SentinelBase + MaxSentinels) { p++; continue; }
            int run = 1;
            while (p + run < result.Length && result[p + run] == c) run++;
            int pair = c - SentinelBase;
            int[] found = new int[] { p, run };
            if (!firstRun.ContainsKey(pair)) firstRun[pair] = found;
            lastRun[pair] = found;
            p += run;
        }
        char[] chars = result.ToCharArray();
        for (int i = 0; i < chars.Length; i++) {
            char c = chars[i];
            if (c < SentinelBase || c >= SentinelBase + MaxSentinels) continue;
            int pair = c - SentinelBase;
            int[] open = firstRun[pair], close = lastRun[pair];
            bool broken = !object.ReferenceEquals(open, close) &&
                (UnderscoreIntraword(result, open[0], open[1], true) || UnderscoreIntraword(result, close[0], close[1], false));
            chars[i] = broken ? '*' : '_';
        }
        return new string(chars);
    }

    // A plain replacement of [start, end) by `inserted` that touches no marker (a Backspace or
    // Delete of one character, a paste at a caret): the offsets in `text` of the underscore
    // markers whose pair stops parsing because the new neighbour is a word character. The caller
    // rewrites each as "*" in the same document update. A pair with a marker inside the range goes
    // with the range and is not repaired.
    internal static List<int> UnderscoreRepairs(List<Span> spans, string text, int start, int end, string inserted) {
        var fixes = new List<int>();
        if (spans == null || text == null) return fixes;
        if (inserted == null) inserted = string.Empty;
        for (int i = 0; i < spans.Count; i++) {
            Span span = spans[i];
            if (!IsUnderscorePair(span)) continue;
            int openEnd = span.OpenStart + span.OpenLen, closeEnd = span.CloseStart + span.CloseLen;
            if (openEnd > text.Length || closeEnd > text.Length) continue;
            if (span.OpenStart < end && openEnd > start) continue;
            if (span.CloseStart < end && closeEnd > start) continue;
            char left = '\0', right = '\0';
            if (span.OpenStart == end) {
                // The run starts where the new text ends: its left neighbour is the new text's
                // last character, or what stood left of the range when nothing is inserted.
                if (inserted.Length > 0) left = inserted[inserted.Length - 1];
                else if (start > 0) left = text[start - 1];
            } else if (span.OpenStart > 0) left = text[span.OpenStart - 1];
            if (closeEnd == start) {
                if (inserted.Length > 0) right = inserted[0];
                else if (end < text.Length) right = text[end];
            } else if (closeEnd < text.Length) right = text[closeEnd];
            if (!char.IsLetterOrDigit(left) && !char.IsLetterOrDigit(right)) continue;
            for (int p = 0; p < span.OpenLen; p++) fixes.Add(span.OpenStart + p);
            for (int p = 0; p < span.CloseLen; p++) fixes.Add(span.CloseStart + p);
        }
        fixes.Sort();
        return fixes;
    }

    // `text` with [start, end) replaced by `inserted` and each offset of `fixes` (offsets in the
    // old text, outside the range) written as "*": the line UnderscoreRepairs asks for.
    internal static string ReplaceWithRepairs(string text, int start, int end, string inserted, List<int> fixes) {
        if (text == null) text = string.Empty;
        if (inserted == null) inserted = string.Empty;
        char[] chars = text.ToCharArray();
        if (fixes != null)
            for (int i = 0; i < fixes.Count; i++)
                if (fixes[i] >= 0 && fixes[i] < chars.Length) chars[fixes[i]] = '*';
        string fixedText = new string(chars);
        if (start < 0) start = 0;
        if (start > fixedText.Length) start = fixedText.Length;
        if (end < start) end = start;
        if (end > fixedText.Length) end = fixedText.Length;
        return fixedText.Substring(0, start) + inserted + fixedText.Substring(end);
    }

    // A restored closer directly followed by a restored opener must not glue into a run the
    // source-balance rule forbids ("****"); the two pairs then merge into one ("**aZb**").
    // Only a run that crosses the boundary counts, never a valid marker inside either text.
    static bool BannedBoundary(string left, string right) {
        if (left == null) left = string.Empty;
        if (right == null) right = string.Empty;
        // A sentinel is an underscore here, so two underscore pairs still never glue into "____".
        left = UnmarkUnderscores(left);
        right = UnmarkUnderscores(right);
        string[] banned = { "****", "____", "~~~~", "``" };
        for (int b = 0; b < banned.Length; b++) {
            string pattern = banned[b];
            for (int split = 1; split < pattern.Length; split++) {
                int rightCount = pattern.Length - split;
                if (left.Length < split || right.Length < rightCount) continue;
                if (String.CompareOrdinal(left, left.Length - split, pattern, 0, split) != 0) continue;
                if (String.CompareOrdinal(right, 0, pattern, split, rightCount) != 0) continue;
                return true;
            }
        }
        return false;
    }

    static bool HasContent(string value) {
        if (string.IsNullOrEmpty(value)) return false;
        for (int i = 0; i < value.Length; i++)
            if (value[i] != ' ' && value[i] != '\t') return true;
        return false;
    }

    static bool HasContent(string text, int start, int end) {
        if (text == null) return false;
        if (start < 0) start = 0;
        if (end > text.Length) end = text.Length;
        for (int i = start; i < end; i++)
            if (text[i] != ' ' && text[i] != '\t') return true;
        return false;
    }

    // `Pre` puts the restored marker in front of the inserted text instead of after it.
    struct Restore {
        internal int Pos, Anchor, Seq;
        internal string Text;
        internal bool Opener, Pre;
    }

    struct Restored {
        internal int Pos, Len;
        internal bool Opener;
    }

    struct Edit {
        internal int Start, Length, Kind, Seq;
        internal string Text;
    }
}
}
