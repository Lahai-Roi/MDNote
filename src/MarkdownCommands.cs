using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// P4: toolbar commands as pure source transformations. Each command reads only the
// touched lines (plus a bounded fence scan), never reads the whole document and never
// touches WPF. The host applies the returned edits in list order as one undo step.
internal enum ListKind {
    Bullet,
    Number,
    Task
}

// Replaces [Offset, Offset+Length) with Text. Edits in one CommandResult never overlap
// and are sorted by Offset descending; applying them in list order keeps offsets valid.
internal struct SourceEdit {
    internal int Offset;
    internal int Length;
    internal string Text;

    internal SourceEdit(int offset, int length, string text) {
        Offset = offset;
        Length = length;
        Text = text ?? string.Empty;
    }
}

internal sealed class CommandResult {
    internal readonly List<SourceEdit> Edits = new List<SourceEdit>();
    internal int SelectionStart;
    internal int SelectionLength;
    // A caret-only style toggle arms the style for the text typed next instead of writing an
    // empty marker pair. Edits stays empty and the host keeps these markers out of the document
    // until the first input, then wraps that input with them (WrapTyped): an abandoned style
    // never leaves raw markers behind, and the toggle itself records no history entry.
    internal string TypingOpen;
    internal string TypingClose;
    internal string TypingMarker;

    internal void Add(int offset, int length, string text) {
        Edits.Add(new SourceEdit(offset, length, text));
    }

    internal void SortEdits() {
        Edits.Sort(CompareEdits);
    }

    // Descending offset; a longer replacement at the same offset is applied first, so a
    // prefix removal runs before a prefix insertion that starts at the same slot.
    static int CompareEdits(SourceEdit a, SourceEdit b) {
        int order = b.Offset.CompareTo(a.Offset);
        if (order != 0) return order;
        return b.Length.CompareTo(a.Length);
    }
}

internal static class MarkdownCommands {
    // The prefix pieces recognised before body text. Offsets are relative to the line.
    sealed class LinePrefix {
        internal int IndentEnd;
        internal int QuoteEnd;
        internal int ListStart;
        internal int ListEnd;
        internal bool HasList;
        internal ListKind Kind;
        internal int TaskBoxStart;
        internal int HeadingStart;
        internal int HeadingEnd;
        internal int HeadingLevel;
        internal int BodyStart;
    }

    struct FenceMark {
        internal char Character;
        internal int Length;
        internal bool TailBlank;
    }

    // One touched line of a multi-line inline command.
    sealed class InlinePlan {
        internal int S;
        internal int E;
        internal bool Wrapped;
        internal int ContentStart;
        internal int ContentEnd;
        internal int LeftStart;
        internal int LeftEnd;
        internal int RightStart;
        internal int RightEnd;
    }

    #region ToggleInline
    internal static CommandResult ToggleInline(TextDocument document, int selectionStart, int selectionLength, string marker) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        if (!IsKnownMarker(marker)) return null;
        if (TouchesCode(document, selectionStart, selectionLength)) return null;
        // A caret-only command on structure (a delimiter row, a setext underline, a rule) would
        // arm markers for text typed on that line, which rewrites the block.
        if (selectionLength == 0 && !CaretInEditableText(document, selectionStart)) return null;
        // An inline code run is not text the emphasis can wrap: a marker written between its
        // backticks stays the marker itself. A caret strictly inside a word takes the whole run
        // (the word expands and WidenToCodeRun moves the pair outside the backticks, the same
        // answer colour and size give); a caret on the run's inner edge has nothing to wrap, so
        // the toggle declines.
        if (selectionLength == 0 && marker != "`" && CaretInsideInlineCode(document, selectionStart)
            && !IsStrictlyInsideWord(document, selectionStart)) return null;

        int caret = selectionStart;
        bool caretOnly = selectionLength == 0;
        int wordStart = 0, wordEnd = 0;
        bool insideWord = false;
        if (caretOnly && IsStrictlyInsideWord(document, selectionStart)) {
            // The style still applies to the whole word, but the word must not stay
            // selected: the next committed Hangul syllable would replace it.
            insideWord = ExpandWord(document, selectionStart, out wordStart, out wordEnd);
        }
        if (caretOnly) {
            LinkSpan caretLink;
            if (insideWord) {
                selectionStart = wordStart;
                selectionLength = wordEnd - wordStart;
            } else if (LinkAtCaret(document, selectionStart, out caretLink)) {
                // A caret inside a link's markup takes the whole link: a marker armed there would
                // be written between the brackets, where the live parser never reads it, so the
                // link would show raw markup instead of its text.
                selectionStart = caretLink.Start;
                selectionLength = caretLink.End - caretLink.Start;
            } else {
                // An edge caret applies the style to the text typed next instead of expanding a
                // word, so no selection is ever returned here.
                return ToggleInlineAtEdge(document, selectionStart, marker);
            }
        }
        // A link the selection cuts at an edge takes the whole span, exactly as a caret in its
        // markup does, before either answer below reads the text.
        WidenSelectionToLinks(document, ref selectionStart, ref selectionLength);
        // Rule A/B: a selection that mixes styled and plain text (or cuts a run) gets the
        // Word-like whole-selection answer instead of a second pair around part of a run.
        bool wholeSelection;
        CommandResult emphasis = ToggleEmphasisSelection(document, selectionStart, selectionLength, marker, out wholeSelection);
        if (wholeSelection) {
            if (emphasis == null) return null;
            if (insideWord) {
                emphasis.SelectionStart = MapCaretThroughEdits(emphasis.Edits, caret);
                emphasis.SelectionLength = 0;
            }
            emphasis.SortEdits();
            return emphasis;
        }

        // A code span shows its text as typed, so backticks around a link or an emphasis marker
        // put that markup on the screen (the user's 14:26 report: the pen turned the bold link
        // "**[다운로드 페이지](...)**" into raw code). The pen wraps the text between such markup
        // piece by piece instead; a selection that crosses none keeps the single wrap.
        List<InlinePlan> plans = marker == "`" ? CodePlansAroundMarkup(document, selectionStart, selectionLength) : null;
        if (plans == null) plans = BuildInlinePlans(document, selectionStart, selectionLength, marker);
        bool any = false;
        bool allWrapped = true;
        for (int i = 0; i < plans.Count; i++) {
            InlinePlan plan = plans[i];
            if (plan.S >= plan.E) continue;
            any = true;
            if (!plan.Wrapped) allWrapped = false;
        }
        if (!any) return null;

        CommandResult result = new CommandResult();
        int delta = 0;
        int firstStart = -1;
        int lastEnd = 0;
        for (int i = 0; i < plans.Count; i++) {
            InlinePlan plan = plans[i];
            if (plan.S >= plan.E) continue;
            if (allWrapped) {
                // Removing a delimiter can expose a backtick run that the delimiter (and the
                // padding space of a padded code span) was hiding at the line start; three or
                // more exposed backticks would turn the line into a fence opener.
                if (marker == "`" && RemovalOpensFence(document, plan)) return null;
                int leftLength = plan.LeftEnd - plan.LeftStart;
                int rightLength = plan.RightEnd - plan.RightStart;
                result.Add(plan.LeftStart, leftLength, string.Empty);
                delta -= leftLength;
                int contentStart = plan.ContentStart + delta;
                int contentEnd = contentStart + (plan.ContentEnd - plan.ContentStart);
                if (firstStart < 0) firstStart = contentStart;
                lastEnd = contentEnd;
                result.Add(plan.RightStart, rightLength, string.Empty);
                delta -= rightLength;
            } else if (!plan.Wrapped) {
                string lead, trail;
                if (marker == "`") {
                    CodeDelimiters(document, plan.S, plan.E, out lead, out trail);
                } else {
                    lead = marker;
                    trail = marker;
                }
                // A pair written at the start of a line merges with an existing run of the same
                // character; three or more backticks or tildes there would open a code fence.
                // "~~" reuses a run the selection already starts with and writes only the closing
                // pair (the simplest form that keeps the strike: "~~a" -> "~~a~~"); a run it
                // cannot share leaves the command a no-op. Padding keeps code delimiters apart
                // from backticks in the text, so only a three-plus backtick delimiter (which
                // cannot stay inline at a line start) reaches the no-op here.
                if (LineStartRun(document, plan.S, plan.E, lead, string.Empty) >= 3) {
                    if (marker == "~~" && StartsWithText(document, plan.S, plan.E, marker)
                        && LineStartRun(document, plan.S, plan.E, string.Empty, trail) < 3) {
                        lead = string.Empty;
                    } else {
                        return null;
                    }
                }
                if (lead.Length > 0) {
                    result.Add(plan.S, 0, lead);
                    delta += lead.Length;
                }
                int contentStart = plan.S + delta;
                int contentEnd = contentStart + (plan.E - plan.S);
                if (firstStart < 0) firstStart = contentStart;
                lastEnd = contentEnd;
                result.Add(plan.E, 0, trail);
                delta += trail.Length;
            } else {
                int contentStart = plan.ContentStart + delta;
                int contentEnd = contentStart + (plan.ContentEnd - plan.ContentStart);
                if (firstStart < 0) firstStart = contentStart;
                lastEnd = contentEnd;
            }
        }

        result.SortEdits();
        if (insideWord) {
            result.SelectionStart = MapCaretThroughEdits(result.Edits, caret);
            result.SelectionLength = 0;
        } else {
            result.SelectionStart = firstStart;
            result.SelectionLength = lastEnd - firstStart;
        }
        return result;
    }

    static bool IsKnownMarker(string marker) {
        return marker == "**" || marker == "*" || marker == "~~" || marker == "`";
    }

    static List<InlinePlan> BuildInlinePlans(TextDocument document, int start, int length, string marker) {
        List<DocumentLine> lines = TouchedLines(document, start, length);
        int end = start + length;
        List<InlinePlan> plans = new List<InlinePlan>();
        for (int i = 0; i < lines.Count; i++) {
            DocumentLine line = lines[i];
            string text = LineText(document, line);
            LinePrefix prefix = ParsePrefix(text);
            int role = LineRole(document, line, prefix);
            // Structure is skipped, never wrapped: a marker on a delimiter row ends the table
            // and a marker on a setext underline or a rule ends that block.
            if (role == 2) continue;
            if (role == 1) {
                // A table row is edited inside its cells: markers go around each cell's text and
                // never around the pipes that keep the row a row.
                foreach (CellSpan cell in TableCells(text)) {
                    int a = Math.Max(start, line.Offset + cell.Start);
                    int b = Math.Min(end, line.Offset + cell.End);
                    WidenToCodeRun(text, line.Offset, ref a, ref b);
                    while (a < b && char.IsWhiteSpace(document.GetCharAt(a))) a++;
                    while (b > a && char.IsWhiteSpace(document.GetCharAt(b - 1))) b--;
                    InlinePlan plan = new InlinePlan();
                    plan.S = a;
                    plan.E = b;
                    if (a < b) DescribeWrap(document, a, b, marker, plan);
                    plans.Add(plan);
                }
                continue;
            }
            int from = Math.Max(start, line.Offset + prefix.BodyStart);
            int to = Math.Min(end, line.Offset + line.Length);
            WidenToCodeRun(text, line.Offset, ref from, ref to);
            while (from < to && char.IsWhiteSpace(document.GetCharAt(from))) from++;
            while (to > from && char.IsWhiteSpace(document.GetCharAt(to - 1))) to--;
            InlinePlan linePlan = new InlinePlan();
            linePlan.S = from;
            linePlan.E = to;
            if (from < to) DescribeWrap(document, from, to, marker, linePlan);
            plans.Add(linePlan);
        }
        return plans;
    }

    static void DescribeWrap(TextDocument document, int s, int e, string marker, InlinePlan plan) {
        if (marker == "`") {
            DescribeCodeWrap(document, s, e, plan);
            return;
        }
        int m = marker.Length;
        string text = document.GetText(s, e - s);
        bool inside = text.Length >= 2 * m
            && text.StartsWith(marker, StringComparison.Ordinal)
            && text.EndsWith(marker, StringComparison.Ordinal);
        // "*x*" is italic-wrapped, but a bold "**x**" is not an italic span.
        if (inside && marker == "*" && (text[1] == '*' || text[text.Length - 2] == '*')) inside = false;
        if (inside) {
            plan.Wrapped = true;
            plan.ContentStart = s + m;
            plan.ContentEnd = e - m;
            plan.LeftStart = s;
            plan.LeftEnd = s + m;
            plan.RightStart = e - m;
            plan.RightEnd = e;
            return;
        }
        bool before = HasMarkerBefore(document, s, marker);
        bool after = HasMarkerAfter(document, e, marker);
        if (before && after) {
            plan.Wrapped = true;
            plan.ContentStart = s;
            plan.ContentEnd = e;
            plan.LeftStart = s - m;
            plan.LeftEnd = s;
            plan.RightStart = e;
            plan.RightEnd = e + m;
            return;
        }
        // Mixed edges: the selection returned by a multi-line add starts after the
        // opening marker on the first line and stops before the closing marker on the
        // last line, so one marker is outside and the other inside the selection edge.
        bool starts = text.Length >= m && text.StartsWith(marker, StringComparison.Ordinal);
        bool ends = text.Length >= m && text.EndsWith(marker, StringComparison.Ordinal);
        if (marker == "*") {
            if (starts && text.Length > 1 && text[1] == '*') starts = false;
            if (ends && text.Length >= 2 && text[text.Length - 2] == '*') ends = false;
        }
        if (starts && ends) return;
        if (before && ends) {
            plan.Wrapped = true;
            plan.ContentStart = s;
            plan.ContentEnd = e - m;
            plan.LeftStart = s - m;
            plan.LeftEnd = s;
            plan.RightStart = e - m;
            plan.RightEnd = e;
            return;
        }
        if (starts && after) {
            plan.Wrapped = true;
            plan.ContentStart = s + m;
            plan.ContentEnd = e;
            plan.LeftStart = s;
            plan.LeftEnd = s + m;
            plan.RightStart = e;
            plan.RightEnd = e + m;
        }
    }

    static void DescribeCodeWrap(TextDocument document, int s, int e, InlinePlan plan) {
        int lead = 0;
        while (s + lead < e && document.GetCharAt(s + lead) == '`') lead++;
        int trail = 0;
        while (e - trail - 1 >= s + lead && document.GetCharAt(e - trail - 1) == '`') trail++;
        if (lead > 0 && lead == trail && e - s > lead * 2) {
            int leftEnd = s + lead;
            int rightStart = e - lead;
            bool padded = lead > 0 && leftEnd < rightStart
                && document.GetCharAt(leftEnd) == ' '
                && document.GetCharAt(rightStart - 1) == ' ';
            if (padded) {
                // CommonMark keeps the spaces when the span holds nothing but spaces.
                bool contentSpaces = true;
                for (int i = leftEnd; i < rightStart; i++) {
                    if (document.GetCharAt(i) != ' ') { contentSpaces = false; break; }
                }
                if (!contentSpaces) {
                    leftEnd++;
                    rightStart--;
                }
            }
            plan.Wrapped = true;
            plan.ContentStart = leftEnd;
            plan.ContentEnd = rightStart;
            plan.LeftStart = s;
            plan.LeftEnd = leftEnd;
            plan.RightStart = rightStart;
            plan.RightEnd = e;
            return;
        }
        int length = document.TextLength;
        int runEnd = s;
        if (s > 0 && document.GetCharAt(s - 1) == ' ' && s > 1 && document.GetCharAt(s - 2) == '`') runEnd = s - 1;
        int runStart = runEnd;
        while (runStart > 0 && document.GetCharAt(runStart - 1) == '`') runStart--;
        int before = runEnd - runStart;
        int runBegin = e;
        if (e + 1 < length && document.GetCharAt(e) == ' ' && document.GetCharAt(e + 1) == '`') runBegin = e + 1;
        int runFinish = runBegin;
        while (runFinish < length && document.GetCharAt(runFinish) == '`') runFinish++;
        int after = runFinish - runBegin;
        if (before > 0 && before == after) {
            plan.Wrapped = true;
            plan.ContentStart = s;
            plan.ContentEnd = e;
            plan.LeftStart = runStart;
            plan.LeftEnd = s;
            plan.RightStart = e;
            plan.RightEnd = runFinish;
        }
    }

    static bool HasMarkerBefore(TextDocument document, int offset, string marker) {
        int m = marker.Length;
        if (offset < m) return false;
        // A caret offset cannot pass the text, but a range built before an edit can; past the
        // text there is no marker to find and offset-m+marker.Length would run off the rope.
        if (offset > document.TextLength) return false;
        if (string.CompareOrdinal(document.GetText(offset - m, m), marker) != 0) return false;
        if (marker == "*" && offset - m - 1 >= 0 && document.GetCharAt(offset - m - 1) == '*') return false;
        return true;
    }

    static bool HasMarkerAfter(TextDocument document, int offset, string marker) {
        int m = marker.Length;
        int length = document.TextLength;
        if (offset + m > length) return false;
        if (string.CompareOrdinal(document.GetText(offset, m), marker) != 0) return false;
        if (marker == "*" && offset + m < length && document.GetCharAt(offset + m) == '*') return false;
        return true;
    }

    // The shortest backtick delimiter the span can use: a run length that does not occur inside
    // the text (CommonMark pairs runs of exactly the delimiter's length, so a longer run next to
    // it is fine). The old "longest + 1" rule made needlessly long runs that turned a line start
    // into a fence opener. A delimiter longer than one tick, and a single tick whose neighbour at
    // either edge is a backtick, get one space of padding inside so the delimiter never merges
    // with a run in the text.
    static void CodeDelimiters(TextDocument document, int s, int e, out string lead, out string trail) {
        List<int> runs = new List<int>();
        int longest = 0;
        int run = 0;
        for (int i = s; i < e; i++) {
            if (document.GetCharAt(i) == '`') {
                run++;
            } else if (run > 0) {
                runs.Add(run);
                if (run > longest) longest = run;
                run = 0;
            }
        }
        if (run > 0) {
            runs.Add(run);
            if (run > longest) longest = run;
        }
        int fence = 1;
        while (fence <= longest && runs.Contains(fence)) fence++;
        string ticks = new string('`', fence);
        bool pad = fence > 1
            || (s < e && (document.GetCharAt(s) == '`' || document.GetCharAt(e - 1) == '`'));
        if (pad) {
            lead = ticks + " ";
            trail = " " + ticks;
        } else {
            lead = ticks;
            trail = ticks;
        }
    }

    // The length of the backtick or tilde run that would start the line after inserting lead at
    // s and trail at e, or 0 when the line does not start with that character after at most
    // three spaces. Only the line prefix is read. Inline commands use it to keep a pair from
    // turning a line into a fence opener (three or more backticks or tildes at its start).
    static int LineStartRun(TextDocument document, int s, int e, string lead, string trail) {
        DocumentLine line = document.GetLineByOffset(s);
        int column = s - line.Offset;
        if (column > 3) return 0;
        for (int i = 0; i < column; i++) {
            if (document.GetCharAt(line.Offset + i) != ' ') return 0;
        }
        char c;
        if (lead.Length > 0) c = lead[0];
        else if (s < e) c = document.GetCharAt(s);
        else if (trail.Length > 0) c = trail[0];
        else return 0;
        if (c != '`' && c != '~') return 0;
        int count = 0;
        int k = 0;
        while (k < lead.Length) {
            if (lead[k] != c) return count;
            count++;
            if (count >= 3) return count;
            k++;
        }
        int p = s;
        bool trailWritten = false;
        while (true) {
            if (p == e && !trailWritten) {
                trailWritten = true;
                for (int t = 0; t < trail.Length; t++) {
                    if (trail[t] != c) return count;
                    count++;
                    if (count >= 3) return count;
                }
            }
            if (p >= line.EndOffset || document.GetCharAt(p) != c) return count;
            count++;
            if (count >= 3) return count;
            p++;
        }
    }

    // True when removing this plan's opening delimiter would leave three or more backticks at
    // the start of its line (a padded span hides such a run behind its delimiter and space).
    static bool RemovalOpensFence(TextDocument document, InlinePlan plan) {
        DocumentLine line = document.GetLineByOffset(plan.LeftStart);
        int column = plan.LeftStart - line.Offset;
        if (column > 3) return false;
        for (int i = 0; i < column; i++) {
            if (document.GetCharAt(line.Offset + i) != ' ') return false;
        }
        int run = 0;
        for (int i = plan.ContentStart; i < plan.RightStart; i++) {
            if (document.GetCharAt(i) != '`') return false;
            run++;
            if (run >= 3) return true;
        }
        return false;
    }

    static bool StartsWithText(TextDocument document, int s, int e, string text) {
        if (s + text.Length > e) return false;
        if (s < 0 || s + text.Length > document.TextLength) return false;
        return string.CompareOrdinal(document.GetText(s, text.Length), text) == 0;
    }

    static bool ExpandWord(TextDocument document, int caret, out int start, out int end) {
        DocumentLine line = document.GetLineByOffset(caret);
        int left = caret;
        int right = caret;
        while (left > line.Offset && IsWordChar(document.GetCharAt(left - 1))) left--;
        while (right < line.EndOffset && IsWordChar(document.GetCharAt(right))) right++;
        start = left;
        end = right;
        return left < right;
    }

    static bool IsWordChar(char c) {
        return char.IsLetterOrDigit(c) || c == '_'
            || (c >= '가' && c <= '힣')
            || (c >= 'ㄱ' && c <= '㆏');
    }

    // Strictly inside means a word character on BOTH sides of the caret. Word start/end,
    // whitespace, punctuation and line start/end are all edges.
    static bool IsStrictlyInsideWord(TextDocument document, int caret) {
        if (caret <= 0 || caret >= document.TextLength) return false;
        DocumentLine line = document.GetLineByOffset(caret);
        if (caret <= line.Offset || caret >= line.EndOffset) return false;
        return IsWordChar(document.GetCharAt(caret - 1)) && IsWordChar(document.GetCharAt(caret));
    }

    // Caret-only toggle at an edge: open/close the style around the caret without touching
    // the neighbouring word, so the style applies to the text typed next.
    static CommandResult ToggleInlineAtEdge(TextDocument document, int caret, string marker) {
        int m = marker.Length;
        // An empty pair of this marker right at the caret is switched off as a whole.
        if (HasMarkerBefore(document, caret, marker) && HasMarkerAfter(document, caret, marker)) {
            CommandResult remove = new CommandResult();
            remove.Add(caret - m, m + m, string.Empty);
            remove.SelectionStart = caret - m;
            remove.SelectionLength = 0;
            remove.SortEdits();
            return remove;
        }
        // A closing marker right after the caret ends a run still open on this line: step
        // past it without changing the text.
        if (HasMarkerAfter(document, caret, marker) && HasOpenMarkerBefore(document, caret, marker)) {
            CommandResult move = new CommandResult();
            move.SelectionStart = caret + m;
            move.SelectionLength = 0;
            return move;
        }
        // A caret right after an opening marker stands on that run's inner edge: step out in
        // front of the opener so the next character is written outside the run. Arming a new pair
        // here wrote a second one inside the first ("****x**abc**").
        if (HasMarkerBefore(document, caret, marker) && HasOpenMarkerBefore(document, caret, marker)) {
            CommandResult outside = new CommandResult();
            outside.SelectionStart = caret - m;
            outside.SelectionLength = 0;
            return outside;
        }
        // Otherwise arm the style for the text typed next. The pair is written only with that
        // first input (WrapTyped), so a caret move or a second toggle never leaves "****" in the
        // source and the empty pair never becomes its own undo step.
        CommandResult result = new CommandResult();
        result.TypingOpen = marker;
        result.TypingClose = marker;
        result.TypingMarker = marker;
        result.SelectionStart = caret;
        result.SelectionLength = 0;
        return result;
    }

    // The first input after a caret-only style toggle: the armed markers are written around the
    // typed text as one command, so the style never exists in the source without its text and one
    // undo takes both back. The caret lands before the closing markers, inside the new pair.
    internal static CommandResult WrapTyped(TextDocument document, int caret, string open, string close, string text) {
        if (!Validate(document, caret, 0)) return null;
        if (open == null || close == null || string.IsNullOrEmpty(text)) return null;
        // The armed pair plus the first input must not start the line with a fence run; the host
        // then inserts the input plainly, exactly as if the style had not been armed.
        if (LineStartRun(document, caret, caret, open + text + close, string.Empty) >= 3) return null;
        CommandResult result = new CommandResult();
        result.Add(caret, 0, open + text + close);
        result.SelectionStart = caret + open.Length + text.Length;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }

    // True when the line text before the caret holds an unmatched opening marker of this
    // style, so the marker right after the caret closes a run that contains that text.
    static bool HasOpenMarkerBefore(TextDocument document, int caret, string marker) {
        DocumentLine line = document.GetLineByOffset(caret);
        string text = LineText(document, line);
        int end = caret - line.Offset;
        int count = 0;
        int i = 0;
        while (i + marker.Length <= end) {
            if (IsMarkerAt(text, i, marker)) {
                count++;
                i += marker.Length;
            } else {
                i++;
            }
        }
        return (count & 1) == 1;
    }

    // One marker occurrence inside a line. A single "*" that belongs to a "**" pair is not
    // an italic marker, like HasMarkerBefore / HasMarkerAfter.
    static bool IsMarkerAt(string text, int index, string marker) {
        int m = marker.Length;
        if (index + m > text.Length) return false;
        if (string.CompareOrdinal(text, index, marker, 0, m) != 0) return false;
        if (marker == "*") {
            if (index > 0 && text[index - 1] == '*') return false;
            if (index + 1 < text.Length && text[index + 1] == '*') return false;
        }
        return true;
    }

    // Post-edit caret for a command that keeps the text around the caret in place. Edits
    // wholly before the caret shift it; an insertion exactly at the caret moves it after
    // the inserted text.
    static int MapCaretThroughEdits(List<SourceEdit> edits, int caret) {
        int delta = 0;
        for (int i = 0; i < edits.Count; i++) {
            SourceEdit edit = edits[i];
            if (edit.Offset + edit.Length <= caret) {
                delta += edit.Text.Length - edit.Length;
            } else if (edit.Offset < caret) {
                return edit.Offset + delta + edit.Text.Length;
            }
        }
        return caret + delta;
    }
    #endregion

    #region Shared line helpers
    static bool Validate(TextDocument document, int start, int length) {
        if (document == null || start < 0 || length < 0) return false;
        int total = document.TextLength;
        return start <= total && length <= total - start;
    }

    static string LineText(TextDocument document, DocumentLine line) {
        return document.GetText(line.Offset, line.Length);
    }

    // The selection ending exactly at column 0 of the next line does not touch that line.
    static List<DocumentLine> TouchedLines(TextDocument document, int start, int length) {
        List<DocumentLine> lines = new List<DocumentLine>();
        DocumentLine first = document.GetLineByOffset(start);
        DocumentLine last;
        if (length == 0) {
            last = first;
        } else {
            last = document.GetLineByOffset(start + length - 1);
        }
        DocumentLine current = first;
        while (current != null) {
            lines.Add(current);
            if (current == last) break;
            current = current.NextLine;
        }
        return lines;
    }

    // A marker's own separator: the live parser treats a space and a tab alike after "#", ">",
    // a list marker and a task box, so the commands must read the same marker the screen does
    // (otherwise pressing the button again writes a second marker in front of the first).
    static bool IsSpaceOrTab(char c) {
        return c == ' ' || c == '\t';
    }

    static LinePrefix ParsePrefix(string text) {
        LinePrefix prefix = new LinePrefix();
        int length = text.Length;
        int i = 0;
        while (i < length && (text[i] == ' ' || text[i] == '\t')) i++;
        prefix.IndentEnd = i;
        while (i < length && text[i] == '>') {
            i++;
            if (i < length && IsSpaceOrTab(text[i])) i++;
        }
        prefix.QuoteEnd = i;
        // The room an item has after the quote markers counts like a top-level indentation: the
        // screen reads a list marker after these spaces (ReadList skips them) and
        // MarkdownStructuralInput keys its items on the prefix up to the marker, so ">   1. b" is
        // a nested quoted item here too and its number does not join the ones at "> ". A line
        // with no marker still keeps BodyStart right after the quote.
        int markerStart = i;
        while (markerStart < length && (text[markerStart] == ' ' || text[markerStart] == '\t')) markerStart++;
        prefix.ListStart = markerStart;
        prefix.ListEnd = markerStart;
        // A heading is still read right after the quote, not after that room.
        prefix.HeadingStart = i;
        prefix.HeadingEnd = i;

        int markerEnd = -1;
        if (markerStart < length) {
            char c = text[markerStart];
            if ((c == '-' || c == '*' || c == '+') && markerStart + 1 < length && IsSpaceOrTab(text[markerStart + 1])) {
                markerEnd = markerStart + 2;
                prefix.HasList = true;
                prefix.Kind = ListKind.Bullet;
            } else if (c >= '0' && c <= '9') {
                int j = markerStart;
                while (j < length && j - markerStart < 9 && text[j] >= '0' && text[j] <= '9') j++;
                if (j < length && (text[j] == '.' || text[j] == ')') && j + 1 < length && IsSpaceOrTab(text[j + 1])) {
                    markerEnd = j + 2;
                    prefix.HasList = true;
                    prefix.Kind = ListKind.Number;
                }
            }
        }
        if (prefix.HasList) {
            // The whole run of spaces/tabs after a marker belongs to the marker. The live parser's
            // ReadList skips that run as the item's content column, so the commands must take it
            // too: reading only the first space left the rest as body text, and a marker removed
            // off "-  x" left "  x" (user's basic-view video 17:0x: the numbered item and its
            // removal stayed indented, the task item did the same).
            int separator = markerEnd;
            while (separator < length && IsSpaceOrTab(text[separator])) separator++;
            prefix.ListEnd = separator;
            // The box does not need a space after it: the live parser's ReadList ends the box at
            // "]" ("- [x]done" is a checked task with the body "done"), so the commands must read
            // the same marker the screen draws, and only a space that is really there is skipped.
            if (prefix.Kind == ListKind.Bullet && separator + 2 < length
                && text[separator] == '[' && text[separator + 2] == ']'
                && (text[separator + 1] == ' ' || text[separator + 1] == 'x' || text[separator + 1] == 'X')) {
                prefix.Kind = ListKind.Task;
                prefix.TaskBoxStart = separator;
                int afterBox = separator + 3;
                while (afterBox < length && IsSpaceOrTab(text[afterBox])) afterBox++;
                prefix.ListEnd = afterBox;
            }
        }

        // A heading marker follows the list marker on a list line and the quote markers on a line
        // without a list, exactly as before the item's own room was read.
        i = prefix.HasList ? prefix.ListEnd : prefix.HeadingStart;
        if (i < length && text[i] == '#') {
            int j = i;
            while (j < length && text[j] == '#') j++;
            int level = j - i;
            if (level <= 6 && (j == length || IsSpaceOrTab(text[j]))) {
                prefix.HeadingLevel = level;
                prefix.HeadingStart = i;
                prefix.HeadingEnd = j < length ? j + 1 : j;
            }
        }

        if (prefix.HeadingLevel > 0) prefix.BodyStart = prefix.HeadingEnd;
        else if (prefix.HasList) prefix.BodyStart = prefix.ListEnd;
        else prefix.BodyStart = prefix.QuoteEnd;
        return prefix;
    }

    // Newline of the first touched line; a last line without a delimiter reuses the
    // preceding line's style instead of hard-coding "\n".
    static string NewlineFor(TextDocument document, DocumentLine line) {
        if (line.DelimiterLength > 0) return document.GetText(line.EndOffset, line.DelimiterLength);
        DocumentLine previous = line.PreviousLine;
        if (previous != null && previous.DelimiterLength > 0) return document.GetText(previous.EndOffset, previous.DelimiterLength);
        return "\n";
    }

    static bool TryFenceMark(string text, out FenceMark mark) {
        mark = new FenceMark();
        int i = 0;
        while (i < text.Length && i < 3 && text[i] == ' ') i++;
        if (i >= text.Length) return false;
        char c = text[i];
        if (c != '`' && c != '~') return false;
        int start = i;
        while (i < text.Length && text[i] == c) i++;
        int length = i - start;
        if (length < 3) return false;
        mark.Character = c;
        mark.Length = length;
        // A closing fence may be followed only by spaces or tabs. A marker run with text after it
        // ("```text") is an opening fence or, inside a block, plain code: it never ends the block.
        // The live parser's ReadFence and the probe's own line parser read the same rule.
        mark.TailBlank = true;
        for (; i < text.Length; i++) {
            if (text[i] != ' ' && text[i] != '\t') { mark.TailBlank = false; break; }
        }
        return true;
    }

    static bool IsFenceLine(TextDocument document, DocumentLine line) {
        FenceMark mark;
        return TryFenceMark(LineText(document, line), out mark);
    }

    static bool IsInsideCode(TextDocument document, DocumentLine line) {
        if (IsFenceLine(document, line)) return true;
        FenceMark open;
        return OpenFenceBefore(document, line, out open);
    }

    // The fence that is open at `line`, read from the lines above it with IsInsideCode's rule.
    // A fence starting more than 400 lines above answers "not inside", never a guess.
    static bool OpenFenceBefore(TextDocument document, DocumentLine line, out FenceMark open) {
        DocumentLine openLine;
        return OpenFenceBefore(document, line, out open, out openLine);
    }

    // The same answer plus the line that opened the block, so a toggle-off can take that line
    // apart. The scan is bounded like the one-argument form.
    static bool OpenFenceBefore(TextDocument document, DocumentLine line, out FenceMark open, out DocumentLine openLine) {
        open = new FenceMark();
        openLine = null;
        List<FenceMark> fences = new List<FenceMark>();
        List<DocumentLine> fenceLines = new List<DocumentLine>();
        DocumentLine current = line.PreviousLine;
        int scanned = 0;
        while (current != null && scanned < 400) {
            FenceMark mark;
            if (TryFenceMark(LineText(document, current), out mark)) { fences.Add(mark); fenceLines.Add(current); }
            current = current.PreviousLine;
            scanned++;
        }
        // The opening fence may be more than 400 lines above; the brief treats a
        // limited scan as "not inside code" rather than guessing.
        if (current != null) return false;
        bool inside = false;
        FenceMark opened = new FenceMark();
        DocumentLine openedLine = null;
        for (int i = fences.Count - 1; i >= 0; i--) {
            FenceMark mark = fences[i];
            if (!inside) {
                inside = true;
                opened = mark;
                openedLine = fenceLines[i];
            } else if (mark.Character == opened.Character && mark.Length >= opened.Length && mark.TailBlank) {
                inside = false;
                openedLine = null;
            }
        }
        if (inside) {
            open = opened;
            openLine = openedLine;
        }
        return inside;
    }

    // The lines of a selection a block command may rewrite. Fenced code is literal text, so its
    // fence lines and its body keep their bytes and only the other lines of the selection take
    // the heading / list / quote. A selection that is code only has no editable line at all.
    static List<DocumentLine> EditableLines(TextDocument document, int start, int length) {
        List<DocumentLine> all = TouchedLines(document, start, length);
        List<DocumentLine> editable = new List<DocumentLine>();
        if (all.Count == 0) return editable;
        FenceMark open;
        bool inside = OpenFenceBefore(document, all[0], out open);
        for (int i = 0; i < all.Count; i++) {
            DocumentLine line = all[i];
            FenceMark mark;
            if (TryFenceMark(LineText(document, line), out mark)) {
                // The fence line itself is structure; it also opens or closes the block that the
                // following lines of the selection belong to.
                if (!inside) {
                    inside = true;
                    open = mark;
                } else if (mark.Character == open.Character && mark.Length >= open.Length && mark.TailBlank) {
                    inside = false;
                }
                continue;
            }
            if (inside) continue;
            editable.Add(line);
        }
        return editable;
    }

    // True when dropping these line ranges leaves a fence opener at the line start: the body of
    // "# ```" or "- ```" is a fence marker, and with the marker gone the line opens a block that
    // no line closes, so the rest of the document becomes code. A start of -1 is not removed.
    static bool RemovalStartsFence(string text, int firstStart, int firstEnd, int secondStart, int secondEnd) {
        var builder = new StringBuilder(text.Length);
        int at = 0;
        if (firstStart >= 0) {
            builder.Append(text, at, firstStart - at);
            at = firstEnd;
        }
        if (secondStart >= 0 && secondStart >= at) {
            builder.Append(text, at, secondStart - at);
            at = secondEnd;
        }
        builder.Append(text, at, text.Length - at);
        FenceMark mark;
        return TryFenceMark(builder.ToString(), out mark);
    }

    static bool TouchesCode(TextDocument document, int start, int length) {
        List<DocumentLine> lines = TouchedLines(document, start, length);
        if (lines.Count == 0) return false;
        DocumentLine first = lines[0];
        if (IsInsideCode(document, first)) return true;
        for (int i = 0; i < lines.Count; i++) {
            if (IsFenceLine(document, lines[i])) return true;
        }
        return false;
    }

    // The insert point for a picture whose caret is in a fenced block (its fences included): the
    // end of the block's closing fence line, with `prefix` the line break that starts the
    // picture's own line there. -1 when the caret's block has no closing fence within the
    // bounded scan (a block left open to the document's end) or the caret is in no block at all.
    // The scans are the ones RemoveCodeBlock uses, bounded at 400 lines each way.
    static int BelowCodeBlock(TextDocument document, int caret, out string prefix) {
        prefix = string.Empty;
        if (caret < 0 || caret > document.TextLength) return -1;
        DocumentLine line = document.GetLineByOffset(caret);
        DocumentLine opener;
        FenceMark open = new FenceMark();
        if (IsFenceLine(document, line) && !OpenFenceBefore(document, line, out open)) {
            opener = line;
            TryFenceMark(LineText(document, line), out open);
        } else if (!OpenFenceBefore(document, line, out open, out opener)) return -1;
        if (opener == null) return -1;
        DocumentLine current = opener.NextLine;
        int scanned = 0;
        while (current != null && scanned < 400) {
            FenceMark mark;
            if (TryFenceMark(LineText(document, current), out mark)
                && mark.Character == open.Character && mark.Length >= open.Length && mark.TailBlank) {
                prefix = NewlineFor(document, current);
                return current.EndOffset;
            }
            current = current.NextLine;
            scanned++;
        }
        return -1;
    }

    #region InlineSegments
    // One editable run of an inline command: a text line's body, or one table cell's text. The
    // range never includes a line's structural markup (prefix, table pipe, delimiter row, setext
    // underline or rule), so an inline command cannot rewrite the structure of a table or a
    // heading underline.
    sealed class LineSegment {
        internal int Start;
        internal int End;
    }

    // "===" / "---" underline level, read like the live parser's SetextUnderlineLevel.
    static int SetextLevel(string text) {
        int p = 0;
        while (p < text.Length && p < 4 && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p > 3 || p >= text.Length) return 0;
        char marker = text[p];
        if (marker != '=' && marker != '-') return 0;
        int count = 0;
        for (; p < text.Length && text[p] == marker; p++) count++;
        // 11-01: one "-" is a list being typed, not an H2 underline (the live view's rule).
        if (marker == '-' && count < 2) return 0;
        for (; p < text.Length; p++) if (text[p] != ' ' && text[p] != '\t') return 0;
        return marker == '=' ? 1 : 2;
    }

    // A thematic break of three or more '-'/'*'/'_' markers (the live view's IsRule rule), with
    // at most three leading whitespace columns before the markers (a tab counts four).
    static bool IsRuleText(string text, int start, int indent) {
        if (indent > 3) return false;
        char marker = '\0';
        int count = 0;
        for (int i = start; i < text.Length; i++) {
            char c = text[i];
            if (c == ' ' || c == '\t') continue;
            if (marker == '\0') marker = c;
            if ((c != '-' && c != '*' && c != '_') || c != marker) return false;
            count++;
        }
        return count >= 3;
    }

    // The whitespace columns the rule's container content starts with: the line's leading spaces
    // on a plain line, the spaces after the quote markers on a quoted one, and nothing after a
    // list or heading prefix (the command reads the body after that marker, and the live view's
    // whole-line rule already accepted those spaces as the marker's own spacing).
    static int RuleIndent(string text, LinePrefix prefix) {
        int from = prefix.HasList ? prefix.ListEnd
            : prefix.HeadingLevel > 0 ? prefix.HeadingStart
            : prefix.QuoteEnd > prefix.IndentEnd ? prefix.QuoteEnd : 0;
        int columns = 0;
        for (int i = from; i < prefix.BodyStart && i < text.Length; i++) {
            if (text[i] == '\t') columns += 4;
            else if (text[i] == ' ') columns++;
            else break;
        }
        if (prefix.HasList || prefix.HeadingLevel > 0) return columns;
        // A plain or quoted body may still be indented after its body start (">    ---"): the
        // quote's own spacer is already consumed, so these columns are the break's indentation.
        for (int i = prefix.BodyStart; i < text.Length; i++) {
            if (text[i] == '\t') columns += 4;
            else if (text[i] == ' ') columns++;
            else break;
        }
        return columns;
    }

    // A delimiter row: cells of optional spaces, an optional colon, one or more dashes and an
    // optional colon, at least one cell and at least one pipe (the live parser's IsDelimiter).
    static bool IsDelimiterText(string text) {
        bool seenPipe = false;
        int p = 0, length = text.Length;
        while (p < length && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p < length && text[p] == '|') { seenPipe = true; p++; }
        int cells = 0;
        while (true) {
            while (p < length && (text[p] == ' ' || text[p] == '\t')) p++;
            if (p >= length) break;   // a trailing pipe or spaces end the row; no empty cell
            if (text[p] == ':') p++;
            int dashes = 0;
            while (p < length && text[p] == '-') { dashes++; p++; }
            if (dashes == 0) return false;
            if (p < length && text[p] == ':') p++;
            while (p < length && (text[p] == ' ' || text[p] == '\t')) p++;
            cells++;
            if (p >= length) break;
            if (text[p] != '|') return false;
            seenPipe = true;
            p++;
            if (p >= length) break;   // the trailing pipe owns no cell
        }
        return seenPipe && cells > 0;
    }

    // One cell of a pipe row: its text without the surrounding padding.
    struct CellSpan {
        internal int Start;
        internal int End;
    }

    // Pipes that separate cells: an escaped pipe and a pipe inside an inline code span are text,
    // exactly like the table command's own split.
    static List<int> TablePipes(string text) {
        List<int> pipes = new List<int>();
        int i = 0;
        while (i < text.Length) {
            char c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '`') {
                int fence = 1;
                while (i + fence < text.Length && text[i + fence] == '`') fence++;
                int close = CodeRunEnd(text, i + fence, fence);
                i = close >= 0 ? close + fence : i + fence;
                continue;
            }
            if (c == '|') pipes.Add(i);
            i++;
        }
        return pipes;
    }

    // Cell interiors of a pipe row, trimmed like the live parser's SplitCells; a trailing pipe
    // owns no cell and an empty middle cell still counts as a column.
    static List<CellSpan> TableCells(string text) {
        List<CellSpan> cells = new List<CellSpan>();
        List<int> pipes = TablePipes(text);
        int start = 0;
        for (int i = 0; i <= pipes.Count; i++) {
            int end = i < pipes.Count ? pipes[i] : text.Length;
            int a = start, b = end;
            while (a < b && (text[a] == ' ' || text[a] == '\t')) a++;
            while (b > a && (text[b - 1] == ' ' || text[b - 1] == '\t')) b--;
            if (a != b || (i > 0 && i < pipes.Count)) {
                CellSpan cell = new CellSpan();
                cell.Start = a;
                cell.End = b;
                cells.Add(cell);
            }
            start = end + 1;
        }
        return cells;
    }

    // 0 = not a table line, 1 = table row (header or body), 2 = the delimiter row. The contiguous
    // run of pipe rows is read top-down with the live parser's block state: a header is a pipe
    // row whose next line is a delimiter row with the same column count, and only the line right
    // under the header is the delimiter (a later delimiter-looking body row stays a row). The
    // scan is bounded like the fence scan, so one command never reads the whole document.
    static int TableLineKind(TextDocument document, DocumentLine line) {
        string text = LineText(document, line);
        if (TableCells(text).Count == 0) return 0;
        DocumentLine top = line;
        for (int i = 0; i < 400; i++) {
            DocumentLine previous = top.PreviousLine;
            if (previous == null) break;
            if (TableCells(LineText(document, previous)).Count == 0) break;
            top = previous;
        }
        bool table = false;
        DocumentLine tableStart = null;
        for (DocumentLine current = top; current != null; current = current.NextLine) {
            string currentText = LineText(document, current);
            bool pipe = TableCells(currentText).Count > 0;
            bool delimiter = IsDelimiterText(currentText) && table && tableStart != null && current == tableStart.NextLine;
            bool header = !table && pipe && NextIsDelimiterRow(document, current);
            table = (table && pipe) || header;
            if (header) tableStart = current;
            if (current == line) return delimiter ? 2 : (table ? 1 : 0);
        }
        return 0;
    }

    // The next line is a delimiter row with this line's column count (the live parser's rule).
    static bool NextIsDelimiterRow(TextDocument document, DocumentLine line) {
        DocumentLine next = line.NextLine;
        if (next == null) return false;
        string delimiterText = LineText(document, next);
        return IsDelimiterText(delimiterText)
            && TableCells(delimiterText).Count == TableCells(LineText(document, line)).Count;
    }

    // A setext underline line ("---"/"===" under a paragraph line). A "---" after a table row or
    // with no paragraph above is a rule, not an underline; both are structure either way.
    static bool IsSetextUnderlineLine(TextDocument document, DocumentLine line) {
        if (SetextLevel(LineText(document, line)) == 0) return false;
        DocumentLine previous = line.PreviousLine;
        if (previous == null) return false;
        string text = LineText(document, previous);
        if (text.Trim().Length == 0) return false;
        LinePrefix prefix = ParsePrefix(text);
        if (prefix.QuoteEnd > prefix.IndentEnd) return false;
        if (prefix.HeadingLevel > 0) return false;
        if (prefix.HasList) return false;
        if (SetextLevel(text) > 0) return false;
        if (IsRuleText(text, prefix.BodyStart, RuleIndent(text, prefix))) return false;
        if (TableLineKind(document, previous) != 0) return false;
        return true;
    }

    // 0 = ordinary text line, 1 = table row (editable inside its cells), 2 = structure an inline
    // command must leave alone (table delimiter row, setext underline, rule).
    static int LineRole(TextDocument document, DocumentLine line, LinePrefix prefix) {
        int kind = TableLineKind(document, line);
        if (kind != 0) return kind;
        string text = LineText(document, line);
        if (IsRuleText(text, prefix.BodyStart, RuleIndent(text, prefix))) return 2;
        if (IsSetextUnderlineLine(document, line)) return 2;
        return 0;
    }

    // The editable pieces of an inline command's selection: one per text line body and one per
    // table cell. Empty when every touched line is structure.
    static List<LineSegment> InlineSegments(TextDocument document, int start, int length) {
        List<LineSegment> segments = new List<LineSegment>();
        int end = start + length;
        List<DocumentLine> lines = TouchedLines(document, start, length);
        for (int i = 0; i < lines.Count; i++) {
            DocumentLine line = lines[i];
            string text = LineText(document, line);
            LinePrefix prefix = ParsePrefix(text);
            int role = LineRole(document, line, prefix);
            if (role == 2) continue;
            int lineEnd = line.Offset + line.Length;
            if (role == 1) {
                foreach (CellSpan cell in TableCells(text)) {
                    int a = Math.Max(start, line.Offset + cell.Start);
                    int b = Math.Min(end, line.Offset + cell.End);
                    if (b > a) AddSegment(segments, a, b);
                }
                continue;
            }
            int from = Math.Max(start, line.Offset + prefix.BodyStart);
            int to = Math.Min(end, lineEnd);
            if (to > from) AddSegment(segments, from, to);
        }
        return segments;
    }

    static void AddSegment(List<LineSegment> segments, int start, int end) {
        LineSegment segment = new LineSegment();
        segment.Start = start;
        segment.End = end;
        segments.Add(segment);
    }

    // An inline emphasis or span command takes an inline code run whole: a marker written
    // between its backticks stays the marker itself ("`**abcd**`"). The selection is widened to
    // the run's markers, the same rule NormalizeInlineSelection applies to spans.
    static void WidenToCodeRun(string text, int lineOffset, ref int a, ref int b) {
        if (a >= b) return;
        foreach (InlineRun run in InlineRuns(text)) {
            if (!run.Code) continue;
            int open = lineOffset + run.OpenStart;
            int close = lineOffset + run.CloseEnd;
            if (b <= open || a >= close) continue;
            if (a <= open && b >= close) continue;
            if (open < a) a = open;
            if (close > b) b = close;
        }
    }

    // One link of a line ("[label](url)", "[label][id]", "![alt](src)", "[^id]", "<autolink>"),
    // absolute offsets with Start at the opening bracket. A shortcut reference ("[label]") is
    // not read: whether it is a link depends on a definition elsewhere, so it stays plain text.
    struct LinkSpan {
        internal int Start;
        internal int End;
    }

    // The links of one line, read with the live parser's shapes (the same escapes and inline code
    // runs are skipped, the label ends at its matching "]" and the target at its own balanced ")").
    // Only the line's text is read, so the cost stays O(line).
    static List<LinkSpan> LineLinks(string text, int lineOffset) {
        List<LinkSpan> links = new List<LinkSpan>();
        // A shared scan budget keeps a line full of unmatched brackets linear, the same guard
        // the live parser's own scan uses.
        int budget = text.Length * 4;
        int i = 0;
        while (i < text.Length && budget > 0) {
            char c = text[i];
            if (c == '\\') { i += 2; budget--; continue; }
            if (c == '`') {
                int fence = 1;
                while (i + fence < text.Length && text[i + fence] == '`') fence++;
                int close = CodeRunEnd(text, i + fence, fence);
                i = close >= 0 ? close + fence : i + fence;
                continue;
            }
            if (c == '!' && i + 1 < text.Length && text[i + 1] == '[') {
                int end = LinkSpanEnd(text, i + 1, ref budget);
                if (end > 0) { AddLink(links, lineOffset, i, end); i = end; continue; }
                i += 2;
                continue;
            }
            if (c == '[') {
                // Footnote reference [^id]; a definition "[^id]:" stays plain text.
                if (i + 1 < text.Length && text[i + 1] == '^') {
                    int note = text.IndexOf(']', i + 2);
                    if (note > i + 2 && (note + 1 >= text.Length || text[note + 1] != ':')) {
                        AddLink(links, lineOffset, i, note + 1);
                        i = note + 1;
                        continue;
                    }
                }
                int end = LinkSpanEnd(text, i, ref budget);
                if (end > 0) { AddLink(links, lineOffset, i, end); i = end; continue; }
            }
            if (c == '<') {
                int end = AutolinkSpanEnd(text, i);
                if (end > 0) { AddLink(links, lineOffset, i, end); i = end; continue; }
            }
            i++;
            budget--;
        }
        return links;
    }

    static void AddLink(List<LinkSpan> links, int lineOffset, int start, int end) {
        LinkSpan span = new LinkSpan();
        span.Start = lineOffset + start;
        span.End = lineOffset + end;
        links.Add(span);
    }

    // The offset just past a link opening at `open`, or -1 when the bracket is plain text. The
    // label ends at its matching "]" with bracket depth (the live parser's LinkLabelEnd reads the
    // outer label of "[![alt](img)](url)"), and the target must close on this line.
    static int LinkSpanEnd(string text, int open, ref int budget) {
        int depth = 1;
        int p = open + 1;
        while (p < text.Length && budget > 0) {
            budget--;
            char c = text[p];
            if (c == '\\') { p += 2; continue; }
            if (c == '[') { depth++; p++; continue; }
            if (c == ']') {
                depth--;
                if (depth == 0) break;
            }
            p++;
        }
        if (p >= text.Length || budget <= 0) return -1;
        int labelEnd = p;
        if (labelEnd + 1 < text.Length && text[labelEnd + 1] == '(') {
            int close = LinkTargetEnd(text, labelEnd + 2, ref budget);
            return close >= 0 ? close + 1 : -1;
        }
        if (labelEnd + 1 < text.Length && text[labelEnd + 1] == '[') {
            int idEnd = text.IndexOf(']', labelEnd + 2);
            return idEnd >= 0 ? idEnd + 1 : -1;
        }
        return -1;
    }

    // The ")" that ends a link target: a balanced inner pair, an angle-bracket destination and a
    // quoted title hold ")" of their own (the same scan the live parser's ImageEnd makes).
    static int LinkTargetEnd(string text, int start, ref int budget) {
        int depth = 0;
        bool angle = false;
        char quote = '\0';
        for (int p = start; p < text.Length && budget > 0; p++) {
            budget--;
            char c = text[p];
            if (c == '\\') { p++; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '<') angle = true;
            else if (c == '>') angle = false;
            else if (!angle && (c == '"' || c == '\'') && p > start && char.IsWhiteSpace(text[p - 1])) quote = c;
            else if (!angle && c == '(') depth++;
            else if (!angle && c == ')') {
                if (depth == 0) return p;
                depth--;
            }
        }
        return -1;
    }

    // "<https://…>", "<mailto:…>" or "<a@b.c>": a scheme and no space inside, or one "@" with
    // text on both sides. Raw tags ("<span style=…>", "<br>") have neither and stay plain text.
    static int AutolinkSpanEnd(string text, int at) {
        if (at + 1 >= text.Length) return -1;
        char first = text[at + 1];
        bool letter = (first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z') || (first >= '0' && first <= '9');
        if (!letter) return -1;
        int close = text.IndexOf('>', at + 1, Math.Min(text.Length - at - 1, 2048));
        if (close <= at + 1) return -1;
        for (int p = at + 1; p < close; p++) {
            char c = text[p];
            if (c == ' ' || c == '\t' || c == '<') return -1;
        }
        int colon = text.IndexOf(':', at + 1, close - at - 1);
        if (colon > at + 1) {
            for (int p = at + 1; p < colon; p++) {
                char c = text[p];
                bool schemeLetter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                if (schemeLetter) continue;
                bool tail = p > at + 1 && ((c >= '0' && c <= '9') || c == '+' || c == '.' || c == '-');
                if (!tail) return -1;
            }
            return close + 1;
        }
        int atSign = text.IndexOf('@', at + 1, close - at - 1);
        if (atSign > at + 1 && atSign < close - 1 && text.IndexOf('@', atSign + 1, close - atSign - 1) < 0) return close + 1;
        return -1;
    }

    // A marker written between a link's brackets or parentheses stays literal text (the live
    // parser reads the label's own markup only inside the brackets and skips the hidden target
    // whole), so a selection that cuts a link at either edge takes the whole span and the pair
    // lands outside that markup. A link already covered by the selection is left alone.
    static void WidenSelectionToLinks(TextDocument document, ref int start, ref int length) {
        if (length <= 0) return;
        int linkStart, linkEnd;
        if (TryLinkAt(document, start, out linkStart, out linkEnd)) {
            length += start - linkStart;
            start = linkStart;
        }
        int end = start + length;
        if (end > start && TryLinkAt(document, end, out linkStart, out linkEnd)) length = linkEnd - start;
    }

    // The link whose markup holds the offset (strictly between its brackets, the label or the
    // target), read from that offset's own line only.
    static bool TryLinkAt(TextDocument document, int offset, out int linkStart, out int linkEnd) {
        linkStart = 0;
        linkEnd = 0;
        DocumentLine line = document.GetLineByOffset(offset);
        string text = LineText(document, line);
        int relative = offset - line.Offset;
        List<LinkSpan> links = LineLinks(text, line.Offset);
        for (int i = 0; i < links.Count; i++) {
            if (relative > links[i].Start - line.Offset && relative < links[i].End - line.Offset) {
                linkStart = links[i].Start;
                linkEnd = links[i].End;
                return true;
            }
        }
        return false;
    }

    // The link whose markup holds the caret (strictly between its brackets, the label or the
    // target), read from the caret's own line only.
    static bool LinkAtCaret(TextDocument document, int caret, out LinkSpan link) {
        link = new LinkSpan();
        int start, end;
        if (!TryLinkAt(document, caret, out start, out end)) return false;
        link.Start = start;
        link.End = end;
        return true;
    }

    // The pen's plans for a selection that crosses link or emphasis markup, or null when it
    // crosses none. A link keeps its brackets and target outside the code and its label inside
    // ("[`t`](u)", a code-styled link for the live parser and for GFM); an image, a footnote mark
    // and an autolink stay whole and unstyled; every "**", "*", "_" or "~~" marker inside the
    // selection stays outside the backticks, so each pair still closes. The text between is
    // wrapped piece by piece with the usual per-line plans (whitespace trimmed, an existing code
    // run kept whole), and the caller's all-or-nothing toggle runs over all the pieces. Only the
    // touched lines are read.
    static List<InlinePlan> CodePlansAroundMarkup(TextDocument document, int start, int length) {
        if (length <= 0) return null;
        int end = start + length;
        List<LinkSpan> cuts = new List<LinkSpan>();
        foreach (DocumentLine line in TouchedLines(document, start, length)) {
            string text = LineText(document, line);
            foreach (LinkSpan link in LineLinks(text, line.Offset)) {
                int labelEnd = LinkLabelClose(text, link.Start - line.Offset, link.End - line.Offset);
                if (labelEnd < 0) {
                    AddLink(cuts, 0, link.Start, link.End);
                    continue;
                }
                AddLink(cuts, 0, link.Start, link.Start + 1);
                AddLink(cuts, 0, line.Offset + labelEnd, link.End);
                // The label's own markup ("[**t**](u)", "[![i](s)](u)") stays outside the code too.
                string label = text.Substring(link.Start - line.Offset + 1, line.Offset + labelEnd - link.Start - 1);
                foreach (InlineRun run in InlineRuns(label)) {
                    if (run.Code) continue;
                    AddLink(cuts, link.Start + 1, run.OpenStart, run.OpenEnd);
                    AddLink(cuts, link.Start + 1, run.CloseStart, run.CloseEnd);
                }
                foreach (LinkSpan inner in LineLinks(label, link.Start + 1)) cuts.Add(inner);
            }
            foreach (InlineRun run in InlineRuns(text)) {
                if (run.Code) continue;
                AddLink(cuts, line.Offset, run.OpenStart, run.OpenEnd);
                AddLink(cuts, line.Offset, run.CloseStart, run.CloseEnd);
            }
        }
        cuts.RemoveAll(cut => cut.End <= start || cut.Start >= end);
        if (cuts.Count == 0) return null;
        cuts.Sort((x, y) => x.Start.CompareTo(y.Start));
        List<InlinePlan> plans = new List<InlinePlan>();
        int from = start;
        foreach (LinkSpan cut in cuts) {
            if (cut.Start > from) plans.AddRange(BuildInlinePlans(document, from, Math.Min(cut.Start, end) - from, "`"));
            if (cut.End > from) from = cut.End;
        }
        if (from < end) plans.AddRange(BuildInlinePlans(document, from, end - from, "`"));
        return plans;
    }

    // The "]" that closes the label of an inline or reference link opening at `open` (line
    // relative, the span ending before `end`), or -1 for an image, a footnote mark, an autolink or
    // an empty label: none of those has text a code span could style, so they stay whole.
    static int LinkLabelClose(string text, int open, int end) {
        if (text[open] != '[' || (open + 1 < end && text[open + 1] == '^')) return -1;
        int depth = 1;
        for (int p = open + 1; p < end; p++) {
            char c = text[p];
            if (c == '\\') { p++; continue; }
            if (c == '[') depth++;
            else if (c == ']') {
                depth--;
                if (depth == 0) return p > open + 1 ? p : -1;
            }
        }
        return -1;
    }

    // The text a command may rewrite at this offset: a table row's cell interior, otherwise the
    // line body after its structural prefix. A pair crossing this edge belongs to another piece.
    static void PieceBounds(TextDocument document, DocumentLine line, int offset, out int start, out int end) {
        string text = LineText(document, line);
        LinePrefix prefix = ParsePrefix(text);
        if (LineRole(document, line, prefix) == 1) {
            int relative = offset - line.Offset;
            foreach (CellSpan cell in TableCells(text)) {
                if (relative >= cell.Start && relative <= cell.End) {
                    start = line.Offset + cell.Start;
                    end = line.Offset + cell.End;
                    return;
                }
            }
        }
        start = line.Offset + prefix.BodyStart;
        end = line.Offset + line.Length;
    }

    // One editable piece of the selection (a line body or a table cell) with the style runs that
    // touch it, for the whole-selection emphasis toggle.
    sealed class EmphasisPart {
        internal int Start;
        internal int End;
        internal int LineOffset;
        internal string Text;
        internal List<InlineRun> Runs;
    }

    // Rule A: a selection that is not entirely in the style takes the style as a whole, and one
    // that already has it loses it as a whole (Word/Google Docs). A run the selection cuts is
    // split at the spot nearest the selection edge that the parser's flanking rules accept (a
    // marker may not sit next to whitespace on its inner side), and plain text next to a run is
    // folded into that run instead of a second pair ("****" never renders as a pair at all).
    // handled=false keeps the older per-line wrap: either no run of this style is touched, or
    // every run is covered whole (then that answer already leaves or removes them correctly).
    // handled=true with a null result is a deliberate no-op: the command refuses rather than
    // writing a pair the parser would not read.
    static CommandResult ToggleEmphasisSelection(TextDocument document, int selectionStart, int selectionLength, string marker, out bool handled) {
        handled = false;
        if (marker == "`" || selectionLength <= 0) return null;
        List<LineSegment> segments = InlineSegments(document, selectionStart, selectionLength);
        if (segments.Count == 0) return null;
        List<EmphasisPart> parts = new List<EmphasisPart>();
        bool touches = false, mixed = false, partial = false, adjacent = false, allCovered = true;
        for (int i = 0; i < segments.Count; i++) {
            EmphasisPart part = new EmphasisPart();
            part.Start = segments[i].Start;
            part.End = segments[i].End;
            DocumentLine line = document.GetLineByOffset(part.Start);
            part.LineOffset = line.Offset;
            part.Text = LineText(document, line);
            int pieceStart, pieceEnd;
            PieceBounds(document, line, part.Start, out pieceStart, out pieceEnd);
            // Inline code is a unit: a marker written between its backticks stays literal text.
            if (marker != "`") WidenToCodeRun(part.Text, line.Offset, ref part.Start, ref part.End);
            if (part.Start < pieceStart) part.Start = pieceStart;
            if (part.End > pieceEnd) part.End = pieceEnd;
            part.Runs = new List<InlineRun>();
            int covered = 0;
            foreach (InlineRun run in InlineRuns(part.Text)) {
                if (run.Code || !IsStyleRun(part.Text, run, marker)) continue;
                int open = line.Offset + run.OpenStart;
                int close = line.Offset + run.CloseEnd;
                bool overlaps = close > part.Start && open < part.End;
                // A run that ends at the selection start or opens at its end is joined rather
                // than repeated: a second pair there would read as "****".
                bool joins = close == part.Start || open == part.End;
                if (!overlaps && !joins) continue;
                // A pair that leaves this line body or cell (across a table pipe) is not edited
                // here: its other half is not part of what this command may rewrite.
                if (open < pieceStart || close > pieceEnd) { handled = true; return null; }
                part.Runs.Add(run);
                if (joins) adjacent = true;
                if (!overlaps) continue;
                int a = Math.Max(part.Start, open);
                int b = Math.Min(part.End, close);
                if (b > a) covered += b - a;
                int contentStart = line.Offset + run.OpenEnd;
                int contentEnd = line.Offset + run.CloseStart;
                int u0 = Math.Max(part.Start, contentStart);
                int u1 = Math.Min(part.End, contentEnd);
                if (u1 > u0 && (u0 > contentStart || u1 < contentEnd)) partial = true;
            }
            parts.Add(part);
            if (part.Runs.Count == 0) { allCovered = false; continue; }
            touches = true;
            int total = part.End - part.Start;
            if (covered < total) allCovered = false;
            if (covered > 0 && covered < total) mixed = true;
        }
        if (!touches) return null;
        if (!mixed && !partial && !adjacent) return null;
        handled = true;
        CommandResult result = new CommandResult();
        for (int i = 0; i < parts.Count; i++) {
            bool ok = allCovered
                ? RemoveEmphasisPart(parts[i], result)
                : ApplyEmphasisPart(document, parts[i], marker, result);
            if (!ok) return null;
        }
        if (result.Edits.Count == 0) return null;
        FinishSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }

    // The style a marker writes. Only the command's own spelling counts as a run: a "__" pair is
    // bold to the screen, but it is a different run the parser pairs on its own, so taking it in
    // here would nest two runs of one style and split the wrong marker.
    static bool IsStyleRun(string text, InlineRun run, string marker) {
        int length = run.OpenEnd - run.OpenStart;
        if (length != marker.Length) return false;
        return string.CompareOrdinal(text, run.OpenStart, marker, 0, length) == 0;
    }

    static string RunMarker(EmphasisPart part, InlineRun run) {
        return part.Text.Substring(run.OpenStart, run.OpenEnd - run.OpenStart);
    }

    // Every piece of the selection carries the style: take it out of the piece. A run that also
    // covers text outside the selection is split, and the split marker moves across whitespace
    // to the nearest spot where it can actually close or open.
    static bool RemoveEmphasisPart(EmphasisPart part, CommandResult result) {
        List<InlineRun> all = InlineRuns(part.Text);
        for (int r = 0; r < part.Runs.Count; r++) {
            InlineRun run = part.Runs[r];
            int openStart = part.LineOffset + run.OpenStart;
            int openEnd = part.LineOffset + run.OpenEnd;
            int closeStart = part.LineOffset + run.CloseStart;
            int closeEnd = part.LineOffset + run.CloseEnd;
            int u0 = Math.Max(part.Start, openEnd);
            int u1 = Math.Min(part.End, closeStart);
            if (u0 >= u1) continue;
            string marker = RunMarker(part, run);
            if (u0 <= openEnd) {
                result.Add(openStart, openEnd - openStart, string.Empty);
            } else {
                int p = SplitLeft(part, all, u0, openEnd);
                if (p <= openEnd) result.Add(openStart, openEnd - openStart, string.Empty);
                else result.Add(p, 0, marker);
            }
            if (u1 >= closeStart) {
                result.Add(closeStart, closeEnd - closeStart, string.Empty);
            } else {
                // The run goes on after the selection: a new opener there, and the run's own
                // closer stays (removing it left the rest of the line unpaired, the user's
                // 14:37 video: "같을 때만**" showed its stars).
                int q = SplitRight(part, all, u1, closeStart);
                if (q >= closeStart) result.Add(closeStart, closeEnd - closeStart, string.Empty);
                else result.Add(q, 0, marker);
            }
        }
        return true;
    }

    // Where a split closer may go left of `at` (absolute, not before `limit`): whitespace, a
    // <span>/<font> opening tag and the opener of a nested run that starts at the selection are
    // stepped over, so the new pair never lands between a tag and its text (the tag pair would
    // cross the run) or against another marker ("***" shows raw stars).
    static int SplitLeft(EmphasisPart part, List<InlineRun> runs, int at, int limit) {
        string text = part.Text;
        int p = at - part.LineOffset, floor = limit - part.LineOffset;
        bool moved = true;
        while (moved) {
            moved = false;
            while (p > floor && char.IsWhiteSpace(text[p - 1])) p--;
            if (p > floor && text[p - 1] == '>') {
                int lt = text.LastIndexOf('<', p - 1);
                if (lt >= floor && (TagStart(text, lt, "span") || TagStart(text, lt, "font"))) { p = lt; moved = true; continue; }
            }
            foreach (InlineRun other in runs) {
                if (!other.Code && other.OpenEnd == p && other.OpenStart >= floor) { p = other.OpenStart; moved = true; break; }
            }
        }
        return part.LineOffset + p;
    }

    // The mirror of SplitLeft for the opener written after the selection.
    static int SplitRight(EmphasisPart part, List<InlineRun> runs, int at, int limit) {
        string text = part.Text;
        int q = at - part.LineOffset, ceiling = limit - part.LineOffset;
        bool moved = true;
        while (moved) {
            moved = false;
            while (q < ceiling && char.IsWhiteSpace(text[q])) q++;
            if (q < ceiling && (ClosingTagAt(text, q, "</span>") || ClosingTagAt(text, q, "</font>"))) { q += 7; moved = true; continue; }
            foreach (InlineRun other in runs) {
                if (!other.Code && other.CloseStart == q && other.CloseEnd <= ceiling) { q = other.CloseEnd; moved = true; break; }
            }
        }
        return part.LineOffset + q;
    }

    static bool ClosingTagAt(string text, int at, string tag) {
        return at + tag.Length <= text.Length && string.Compare(text, at, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    // Some piece of the selection is plain: the style goes onto that text. A gap that touches a
    // run of the same marker is folded into it (one pair around the union), two touching runs of
    // the same marker merge, and a free gap gets one pair of the command's own markers.
    static bool ApplyEmphasisPart(TextDocument document, EmphasisPart part, string marker, CommandResult result) {
        int cursor = part.Start;
        for (int r = 0; r <= part.Runs.Count; r++) {
            int leftIndex = r - 1;
            int rightIndex = r < part.Runs.Count ? r : -1;
            int gapEnd = rightIndex < 0 ? part.End
                : Math.Min(part.LineOffset + part.Runs[rightIndex].OpenStart, part.End);
            if (gapEnd > cursor
                && !ApplyEmphasisGap(document, part, cursor, gapEnd, leftIndex, rightIndex, marker, result)) return false;
            if (rightIndex >= 0) {
                int close = part.LineOffset + part.Runs[rightIndex].CloseEnd;
                if (close > cursor) cursor = close;
            }
        }
        return true;
    }

    static bool ApplyEmphasisGap(TextDocument document, EmphasisPart part, int gapStart, int gapEnd, int leftIndex, int rightIndex, string marker, CommandResult result) {
        bool leftTouches = leftIndex >= 0 && part.LineOffset + part.Runs[leftIndex].CloseEnd == gapStart;
        bool rightTouches = rightIndex >= 0 && part.LineOffset + part.Runs[rightIndex].OpenStart == gapEnd;
        if (leftTouches && rightTouches) {
            InlineRun mergeLeft = part.Runs[leftIndex];
            InlineRun mergeRight = part.Runs[rightIndex];
            if (string.CompareOrdinal(RunMarker(part, mergeLeft), RunMarker(part, mergeRight)) == 0) {
                result.Add(part.LineOffset + mergeLeft.CloseStart, mergeLeft.CloseEnd - mergeLeft.CloseStart, string.Empty);
                result.Add(part.LineOffset + mergeRight.OpenStart, mergeRight.OpenEnd - mergeRight.OpenStart, string.Empty);
                return true;
            }
        }
        int a = gapStart;
        int b = gapEnd;
        while (a < b && char.IsWhiteSpace(part.Text[a - part.LineOffset])) a++;
        while (b > a && char.IsWhiteSpace(part.Text[b - part.LineOffset - 1])) b--;
        if (a >= b) return true;
        if (leftTouches) {
            InlineRun left = part.Runs[leftIndex];
            string insert = RunMarker(part, left);
            if (!CanWriteEmphasisMarkers(document, insert, b)) return false;
            result.Add(part.LineOffset + left.CloseStart, left.CloseEnd - left.CloseStart, string.Empty);
            result.Add(b, 0, insert);
            return true;
        }
        if (rightTouches) {
            InlineRun right = part.Runs[rightIndex];
            string insert = RunMarker(part, right);
            if (!CanWriteEmphasisMarkers(document, insert, a)) return false;
            // The opener is written in front of the run it extends and added first, so the edit
            // list stays in ascending order for the selection map.
            result.Add(a, 0, insert);
            result.Add(part.LineOffset + right.OpenStart, right.OpenEnd - right.OpenStart, string.Empty);
            return true;
        }
        if (!CanWriteEmphasisMarkers(document, marker, a) || !CanWriteEmphasisMarkers(document, marker, b)) return false;
        result.Add(a, 0, marker);
        result.Add(b, 0, marker);
        return true;
    }

    // A tilde pair written at the line start would merge with a tilde run into a fence opener;
    // the command declines instead of turning the rest of the document into code.
    static bool CanWriteEmphasisMarkers(TextDocument document, string insert, int at) {
        if (insert[0] != '~') return true;
        return LineStartRun(document, at, at, insert, string.Empty) < 3;
    }

    // A caret strictly inside an inline code run (between its markers, including the run's inner
    // edges). Text typed there would land inside the code, so an emphasis toggle declines.
    static bool CaretInsideInlineCode(TextDocument document, int caret) {
        DocumentLine line = document.GetLineByOffset(caret);
        string text = LineText(document, line);
        int relative = caret - line.Offset;
        foreach (InlineRun run in InlineRuns(text)) {
            if (!run.Code) continue;
            if (relative > run.OpenStart && relative < run.CloseEnd) return true;
        }
        return false;
    }

    // True when the caret stands inside an inline code run, or the selection cuts through one:
    // a link's label is not read for inline markers (InlineRuns skips labels for the same reason),
    // so a run split by the label's edge would show raw backticks. A selection that covers a run
    // whole is left to the caller: the label keeps the run's own text.
    static bool SelectionTouchesInlineCode(TextDocument document, int start, int length) {
        int end = start + length;
        List<DocumentLine> lines = TouchedLines(document, start, length);
        for (int i = 0; i < lines.Count; i++) {
            DocumentLine line = lines[i];
            string text = LineText(document, line);
            foreach (InlineRun run in InlineRuns(text)) {
                if (!run.Code) continue;
                int open = line.Offset + run.OpenStart;
                int close = line.Offset + run.CloseEnd;
                bool touches;
                if (length == 0) touches = start > open && start < close;
                else touches = start < close && end > open && !(start <= open && end >= close);
                if (touches) return true;
            }
        }
        return false;
    }

    // A caret may take a caret-only command only on ordinary text or inside one table cell's
    // text. The delimiter row, a setext underline, a rule and a row's pipes/padding are
    // structure: a marker or tag written there rewrites the block.
    static bool CaretInEditableText(TextDocument document, int caret) {
        DocumentLine line = document.GetLineByOffset(caret);
        string text = LineText(document, line);
        LinePrefix prefix = ParsePrefix(text);
        int role = LineRole(document, line, prefix);
        if (role == 0) return true;
        if (role == 2) return false;
        int relative = caret - line.Offset;
        foreach (CellSpan cell in TableCells(text)) {
            if (relative >= cell.Start && relative <= cell.End) return true;
        }
        return false;
    }
    #endregion

    // Post-edit position of an original offset. Selection starts move after an insertion
    // at exactly that offset; selection ends stop before it (so markers stay outside).
    static int MapPosition(List<SourceEdit> edits, int position, bool includeInsertionAtPosition) {
        int delta = 0;
        for (int i = 0; i < edits.Count; i++) {
            SourceEdit edit = edits[i];
            if (edit.Offset + edit.Length <= position) {
                if (edit.Length > 0 || includeInsertionAtPosition || edit.Offset + edit.Length < position) {
                    delta += edit.Text.Length - edit.Length;
                }
            } else if (edit.Offset >= position) {
                break;
            } else {
                return edit.Offset + delta;
            }
        }
        return position + delta;
    }

    static void FinishSelection(CommandResult result, int originalStart, int originalEnd) {
        bool caret = originalStart == originalEnd;
        int start = MapPosition(result.Edits, originalStart, true);
        int end = MapPosition(result.Edits, originalEnd, caret);
        if (end < start) end = start;
        result.SelectionStart = start;
        result.SelectionLength = end - start;
        result.SortEdits();
    }
    #endregion

    #region SetHeading
    internal static CommandResult SetHeading(TextDocument document, int selectionStart, int selectionLength, int level) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        if (level < 0 || level > 6) return null;
        // Fenced code keeps its text; the heading applies to the selection's other lines, so a
        // selection that only touches a fence still formats the paragraph next to it.
        List<DocumentLine> lines = EditableLines(document, selectionStart, selectionLength);
        if (lines.Count == 0) return null;
        // A setext underline is not a heading line of its own: it belongs to the paragraph above
        // it (the same rule the inline commands skip the underline by), so the command reads and
        // edits that paragraph line and takes the underline with it. Writing the marker on the
        // underline instead left the old "====" as a body line under the new ATX heading, or
        // turned a "----" into a rule.
        List<DocumentLine> targets = new List<DocumentLine>();
        for (int i = 0; i < lines.Count; i++) {
            DocumentLine line = lines[i];
            if (IsSetextUnderlineLine(document, line)) {
                DocumentLine above = line.PreviousLine;
                if (above == null) continue;
                // A fence line is not a paragraph: a "---" under a closing fence is a rule, and
                // reading the fence line as its paragraph wrote the ATX marker onto the fence,
                // which stopped it closing the block (b56c9 format-abuse: cmd:h2 left a code
                // fence unclosed).
                if (IsInsideCode(document, above)) continue;
                line = above;
            }
            // A selection that covers both the paragraph and its underline reaches the same line
            // twice; it is one target.
            if (targets.Count > 0 && targets[targets.Count - 1].Offset == line.Offset) continue;
            targets.Add(line);
        }
        if (targets.Count == 0) return null;
        bool allAtLevel = true;
        for (int i = 0; i < targets.Count; i++) {
            if (HeadingLevelAt(document, targets[i]) != level) allAtLevel = false;
        }
        int target = allAtLevel ? 0 : level;

        CommandResult result = new CommandResult();
        // A selection that starts at the head of a deeply indented line selects the room the
        // command takes with the marker ("    - one"). Finished through the ordinary offset map,
        // that start would stay at the line head (the removal there covers it) and the answer
        // would select the written "### " itself, so the next typed text would delete the heading
        // of the first line while the other lines keep theirs. The start belongs after the
        // marker, as it already is for a plain line. A line that had its own marker keeps the
        // old start: the marker was selected and the new marker takes its place ("# one" -> "## ").
        int freshStart = -1;
        for (int i = 0; i < targets.Count; i++) {
            DocumentLine line = targets[i];
            string text = LineText(document, line);
            LinePrefix prefix = ParsePrefix(text);
            DocumentLine underline = SetextUnderlineFor(document, line);
            int setext = underline == null ? 0 : SetextLevel(LineText(document, underline));
            bool removeList = prefix.HasList;
            // ATX markers set a heading only within the first three columns of the line, or of the
            // quote's content: MarkdownLivePreview.ParseLine's headingIndent and the basic view
            // read the same limit, and a marker after a list marker is read after that marker at
            // any depth. ParsePrefix reads a marker after the whole leading run, so on a plain
            // line indented four or more columns it found one both views draw as text; the
            // command must see no heading there and write its marker at the line start instead
            // (b56c6 format-abuse: H3 on "    - item" left "    ### item", a line the screen kept
            // as plain text, so the button looked dead).
            bool deepRoom = prefix.QuoteEnd == prefix.IndentEnd && prefix.IndentEnd > 3;
            bool deepLine = deepRoom && !prefix.HasList;
            bool removeHeading = prefix.HeadingLevel > 0 && (deepLine || target == 0 || prefix.HeadingLevel != target);
            // A setext heading already at the requested level is left as it is (the order of the
            // title and the underline is the document's own), and the body command turns it into a
            // plain paragraph by dropping the underline alone.
            bool addHeading = target > 0 && (deepLine || prefix.HeadingLevel != target) && target != setext;
            bool dropUnderline = underline != null && target != setext;
            // Dropping a marker must not leave a fence opener at the line start, or the block it
            // opens has no partner and the rest of the document turns into code (b56c9
            // format-abuse: cmd:h0 left a code fence unclosed). The line keeps its marker; a
            // heading adds its own marker, so it can never be the one that exposes a run.
            if (!addHeading && (removeList || removeHeading)
                && RemovalStartsFence(text, removeList ? prefix.ListStart : -1, removeList ? prefix.ListEnd : -1,
                    removeHeading ? prefix.HeadingStart : -1, removeHeading ? prefix.HeadingEnd : -1)) continue;
            if (!removeList && !removeHeading && !addHeading && !dropUnderline) continue;

            // A heading written at a four-or-more-column slot is taken out of that room: the room
            // goes with the marker when the item's marker does (one replacement, one undo step).
            int slot = deepRoom && addHeading ? line.Offset : line.Offset + prefix.QuoteEnd;
            // A heading written over a list item takes the item's place: the room between the quote
            // markers and the marker is the item's own indentation (ListStart reads past it), so the
            // heading replaces the room with the marker. Outside a quote ListStart is QuoteEnd and
            // the indentation stays, as before. Otherwise ">    - b" would keep its old room in
            // front of the text ("> #    b").
            int listStart = removeList && addHeading ? (deepRoom ? 0 : prefix.QuoteEnd) : prefix.ListStart;
            if (removeList) result.Add(line.Offset + listStart, prefix.ListEnd - listStart, string.Empty);
            else if (deepRoom && addHeading) result.Add(line.Offset, prefix.IndentEnd, string.Empty);
            if (removeHeading) result.Add(line.Offset + prefix.HeadingStart, prefix.HeadingEnd - prefix.HeadingStart, string.Empty);
            if (addHeading) {
                // The separator space is written even on an empty body: "#" alone is a heading,
                // but the first character typed after it would make "#제목" and the line would
                // drop out of the heading the button just drew. The caret lands after the space.
                string heading = new string('#', target) + " ";
                result.Add(slot, 0, heading);
                if (i == 0 && deepRoom && prefix.HeadingLevel == 0 && selectionStart == line.Offset)
                    freshStart = slot + heading.Length;
            }
            if (dropUnderline) RemoveSetextUnderline(result, underline);
        }

        if (result.Edits.Count == 0) return null;
        FinishSelection(result, selectionStart, selectionStart + selectionLength);
        if (freshStart >= 0) {
            // The end keeps the mapped position; only the start skips the written marker.
            int end = result.SelectionStart + result.SelectionLength;
            if (end < freshStart) end = freshStart;
            result.SelectionStart = freshStart;
            result.SelectionLength = end - freshStart;
        }
        return result;
    }

    // The level the screen draws on this line: an ATX marker, or the setext underline under the
    // paragraph (both commands and the toolbar caption read the level this way).
    static int HeadingLevelAt(TextDocument document, DocumentLine line) {
        LinePrefix prefix = ParsePrefix(LineText(document, line));
        // CommonMark: four or more columns of the line's own indentation make an ATX marker
        // paragraph text (MarkdownLivePreview.ParseLine measures the same limit at the line
        // level). A marker after a list marker is a heading at any depth there, and a quote's own
        // markers are not indentation, so those two forms keep the level ParsePrefix found.
        if (prefix.HeadingLevel > 0 && (prefix.HasList || prefix.QuoteEnd > prefix.IndentEnd || prefix.IndentEnd <= 3)) return prefix.HeadingLevel;
        DocumentLine underline = SetextUnderlineFor(document, line);
        return underline == null ? 0 : SetextLevel(LineText(document, underline));
    }

    // The underline that makes this paragraph a setext heading, or null. The inline commands'
    // IsSetextUnderlineLine is the one rule: a "---" under a table row, a quote or a list, and a
    // "---" with no paragraph above it, are not this line's heading underline.
    static DocumentLine SetextUnderlineFor(TextDocument document, DocumentLine line) {
        DocumentLine next = line.NextLine;
        if (next == null || SetextLevel(LineText(document, next)) == 0) return null;
        return IsSetextUnderlineLine(document, next) ? next : null;
    }

    // Removes the underline with its line break, so the heading line becomes a plain paragraph
    // (or the ATX line written on it) without an empty line left behind. A last line without a
    // delimiter takes the break in front of it instead, exactly like the other block commands.
    static void RemoveSetextUnderline(CommandResult result, DocumentLine underline) {
        int offset = underline.Offset;
        int length = underline.TotalLength;
        if (underline.DelimiterLength == 0 && underline.PreviousLine != null) {
            offset = underline.PreviousLine.Offset + underline.PreviousLine.Length;
            length = underline.Offset + underline.Length - offset;
        }
        result.Add(offset, length, string.Empty);
    }
    #endregion

    #region ToggleList
    internal static CommandResult ToggleList(TextDocument document, int selectionStart, int selectionLength, ListKind kind) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        if (kind != ListKind.Bullet && kind != ListKind.Number && kind != ListKind.Task) return null;
        // Fenced code keeps its text; the list marker goes on the selection's other lines only.
        List<DocumentLine> lines = EditableLines(document, selectionStart, selectionLength);
        if (lines.Count == 0) return null;
        bool allKind = true;
        for (int i = 0; i < lines.Count; i++) {
            LinePrefix prefix = ParsePrefix(LineText(document, lines[i]));
            if (!prefix.HasList || prefix.Kind != kind) allKind = false;
        }

        CommandResult result = new CommandResult();
        Dictionary<string, int> counters = new Dictionary<string, int>();
        for (int i = 0; i < lines.Count; i++) {
            DocumentLine line = lines[i];
            string text = LineText(document, line);
            LinePrefix prefix = ParsePrefix(text);
            string marker;
            if (allKind) {
                marker = string.Empty;
            } else if (kind == ListKind.Bullet) {
                marker = "- ";
            } else if (kind == ListKind.Task) {
                marker = "- [ ] ";
            } else {
                // The counter's level is the item's whole prefix: the quote markers plus the room
                // the item has after them, the same key MarkdownStructuralInput uses to keep
                // "> 1. a", ">    1. b" and "> > 1. c" in separate numbered runs. Outside a quote
                // that prefix is the leading indentation, so plain lists number as before.
                string level = text.Substring(0, prefix.ListStart);
                int number;
                if (!counters.TryGetValue(level, out number)) number = 0;
                number++;
                counters[level] = number;
                marker = number.ToString(CultureInfo.InvariantCulture) + ". ";
            }
            int offset = line.Offset + prefix.ListStart;
            if (prefix.HasList) result.Add(offset, prefix.ListEnd - prefix.ListStart, marker);
            else result.Add(offset, 0, marker);
        }

        if (result.Edits.Count == 0) return null;
        FinishSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }
    #endregion

    #region ToggleTaskCheck
    internal static CommandResult ToggleTaskCheck(TextDocument document, int caretOffset) {
        if (!Validate(document, caretOffset, 0)) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        if (IsInsideCode(document, line)) return null;
        string text = LineText(document, line);
        LinePrefix prefix = ParsePrefix(text);
        if (!prefix.HasList || prefix.Kind != ListKind.Task) return null;

        int offset = line.Offset + prefix.TaskBoxStart + 1;
        string replacement = document.GetCharAt(offset) == ' ' ? "x" : " ";
        CommandResult result = new CommandResult();
        result.Add(offset, 1, replacement);
        result.SelectionStart = caretOffset;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region ToggleQuote
    internal static CommandResult ToggleQuote(TextDocument document, int selectionStart, int selectionLength) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        // Fenced code keeps its text; the quote marker goes on the selection's other lines only.
        List<DocumentLine> lines = EditableLines(document, selectionStart, selectionLength);
        if (lines.Count == 0) return null;
        bool allQuoted = true;
        for (int i = 0; i < lines.Count; i++) {
            LinePrefix prefix = ParsePrefix(LineText(document, lines[i]));
            if (prefix.QuoteEnd <= prefix.IndentEnd) allQuoted = false;
        }

        CommandResult result = new CommandResult();
        for (int i = 0; i < lines.Count; i++) {
            DocumentLine line = lines[i];
            string text = LineText(document, line);
            LinePrefix prefix = ParsePrefix(text);
            if (allQuoted) {
                int start = line.Offset + prefix.IndentEnd;
                int end = start + 1;
                if (end < line.Offset + text.Length && IsSpaceOrTab(document.GetCharAt(end))) end++;
                result.Add(start, end - start, string.Empty);
            } else {
                result.Add(line.Offset, 0, text.Length == 0 ? ">" : "> ");
            }
        }

        if (result.Edits.Count == 0) return null;
        FinishSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }
    #endregion

    #region InsertRule
    internal static CommandResult InsertRule(TextDocument document, int caretOffset) {
        if (!Validate(document, caretOffset, 0)) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        // A fence line and every line inside a fenced block are literal code: a "---" written
        // there is code text, and a "---" code line is not a rule this command may remove.
        if (IsInsideCode(document, line)) return null;
        string newline = NewlineFor(document, line);
        bool terminated = line.DelimiterLength > 0;
        int offset = terminated ? line.Offset + line.TotalLength : line.Offset + line.Length;
        string leading = string.Empty;
        if (!terminated && !(line.Length == 0 && line.Offset == 0)) leading += newline;
        if (line.Length > 0) leading += newline;
        string trailing = string.Empty;
        if (offset < document.TextLength) {
            trailing = newline;
            DocumentLine next = document.GetLineByOffset(offset);
            if (next != null && next.Length > 0) trailing += newline;
        }
        // The caret always lands on the new empty line under the rule, never right after "---":
        // the first typed character there would turn the rule into a paragraph ("---text").
        CommandResult result = new CommandResult();
        result.Add(offset, 0, leading + "---" + (trailing.Length > 0 ? trailing : newline));
        result.SelectionStart = offset + leading.Length + 3 + newline.Length;
        result.SelectionLength = 0;
        return result;
    }
    #endregion

    #region InsertCodeBlock
    internal static CommandResult InsertCodeBlock(TextDocument document, int selectionStart, int selectionLength, string language) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        // The guide's promise: pressing the code button inside a block turns it back into plain
        // lines without touching their text ("블록 안에서 다시 누르면 내용은 그대로 두고 일반
        // 문단으로 돌아옵니다"). A press outside every block still adds one.
        if (TouchesCode(document, selectionStart, selectionLength)) return RemoveCodeBlock(document, selectionStart, selectionLength);
        string fence = "```" + (language ?? string.Empty);

        if (selectionLength == 0) {
            DocumentLine line = document.GetLineByOffset(selectionStart);
            string newline = NewlineFor(document, line);
            int offset;
            string leading;
            BlockStart(document, line, newline, out offset, out leading);
            string trailing = offset < document.TextLength ? newline : string.Empty;
            CommandResult caretResult = new CommandResult();
            caretResult.Add(offset, 0, leading + fence + newline + newline + "```" + trailing);
            caretResult.SelectionStart = offset + leading.Length + fence.Length + newline.Length;
            caretResult.SelectionLength = 0;
            return caretResult;
        }

        List<DocumentLine> lines = TouchedLines(document, selectionStart, selectionLength);
        DocumentLine first = lines[0];
        DocumentLine last = lines[lines.Count - 1];
        string separator = NewlineFor(document, first);
        CommandResult result = new CommandResult();
        result.Add(first.Offset, 0, fence + separator);
        if (last.DelimiterLength > 0) {
            result.Add(last.Offset + last.TotalLength, 0, "```" + separator);
        } else {
            result.Add(document.TextLength, 0, separator + "```");
        }
        FinishSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }

    // Toggle-off for the code button: one complete fenced block around the caret or selection
    // loses its two fence lines (with their own line endings) and keeps every content line
    // byte for byte, as one command. A caret or selection that reaches outside the block, or a
    // block whose closing fence the bounded scan cannot find, declines so the press cannot cut
    // another block or a paragraph open. The lines around the caret are the only ones read.
    static CommandResult RemoveCodeBlock(TextDocument document, int selectionStart, int selectionLength) {
        int selectionEnd = selectionStart + selectionLength;
        // 11-01 user: a mouse drag over the whole block also takes the empty line around it; the
        // blank edges are not part of what the user picked, so they do not make the selection
        // "leave the block".
        if (selectionLength > 0) {
            int trimStart = selectionStart, trimEnd = selectionEnd;
            while (trimStart < trimEnd && char.IsWhiteSpace(document.GetCharAt(trimStart))) trimStart++;
            while (trimEnd > trimStart && char.IsWhiteSpace(document.GetCharAt(trimEnd - 1))) trimEnd--;
            if (trimEnd > trimStart) { selectionStart = trimStart; selectionEnd = trimEnd; }
        }
        DocumentLine first = document.GetLineByOffset(selectionStart);
        DocumentLine opener = null;
        FenceMark open = new FenceMark();
        if (IsFenceLine(document, first) && !OpenFenceBefore(document, first, out open)) {
            // A fence line outside any block is an opener (a stray closer above has no partner
            // and is not a block this command may take apart).
            opener = first;
            TryFenceMark(LineText(document, first), out open);
        } else {
            DocumentLine openLine;
            if (!OpenFenceBefore(document, first, out open, out openLine)) return null;
            opener = openLine;
        }
        if (opener == null || selectionStart < opener.Offset) return null;
        DocumentLine closer = null;
        DocumentLine current = opener.NextLine;
        int scanned = 0;
        while (current != null && scanned < 400) {
            FenceMark mark;
            if (TryFenceMark(LineText(document, current), out mark)
                && mark.Character == open.Character && mark.Length >= open.Length && mark.TailBlank) {
                closer = current;
                break;
            }
            current = current.NextLine;
            scanned++;
        }
        if (closer == null) return null;
        int blockEnd = closer.Offset + closer.TotalLength;
        if (selectionEnd > blockEnd) return null;
        int openerEnd = opener.Offset + opener.TotalLength;
        int contentStart = openerEnd;
        int contentEnd = closer.Offset;
        if (contentStart > contentEnd) return null;
        // A closer that is the document's last line owns the break in front of it, or the
        // content would keep a trailing empty line after the block is gone.
        int closeStart = closer.Offset;
        if (closer.DelimiterLength == 0 && closer.PreviousLine != null) closeStart = closer.PreviousLine.EndOffset;
        CommandResult result = new CommandResult();
        if (closeStart <= openerEnd) {
            // An empty block (or one whose only content the closer's break covers) is one
            // removal; two edits there would overlap and the host rejects the command.
            result.Add(opener.Offset, closer.Offset + closer.TotalLength - opener.Offset, string.Empty);
        } else {
            result.Add(opener.Offset, opener.TotalLength, string.Empty);
            result.Add(closeStart, closer.Offset + closer.TotalLength - closeStart, string.Empty);
        }
        FinishSelection(result, contentStart, contentEnd);
        return result;
    }
    #endregion

    #region InsertLink
    internal static CommandResult InsertLink(TextDocument document, int selectionStart, int selectionLength, string url) {
        return InsertLink(document, selectionStart, selectionLength, url, null);
    }

    // placeholder is the host's localized "Link" label (T("링크")); null keeps the default, so the
    // existing probes and tests are unchanged.
    internal static CommandResult InsertLink(TextDocument document, int selectionStart, int selectionLength, string url, string placeholder) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        // A link around a fence line would replace its marker run and unbalance the block; the
        // block commands use the same guard.
        if (TouchesCode(document, selectionStart, selectionLength)) return null;
        // A link's label is not read for inline markers (InlineRuns skips labels for the same
        // reason), so a label edge cut through an inline code run would leave raw backticks in
        // the source; the command declines there instead of writing a link that renders
        // differently in each view.
        if (SelectionTouchesInlineCode(document, selectionStart, selectionLength)) return null;
        string label = String.IsNullOrEmpty(placeholder) ? "링크" : placeholder;
        string target = EscapeUrl(url);
        CommandResult result = new CommandResult();
        if (selectionLength == 0) {
            // Structure (a delimiter row, a setext underline, a rule) is not a place for a link.
            if (!CaretInEditableText(document, selectionStart)) return null;
            result.Add(selectionStart, 0, "[" + label + "](" + target + ")");
            result.SelectionStart = selectionStart + 1;
            result.SelectionLength = label.Length;
            result.SortEdits();
            return result;
        }
        // A link has to close its label and target inside one line, so a multi-line selection or
        // a table row becomes one link per line body / cell. The pieces are merged into a single
        // command, which the host records as one undo step.
        List<LineSegment> segments = InlineSegments(document, selectionStart, selectionLength);
        if (segments.Count == 0) return null;
        if (segments.Count == 1 && segments[0].Start == selectionStart && segments[0].End == selectionStart + selectionLength) {
            string single = EscapeLinkLabel(document.GetText(selectionStart, selectionLength));
            result.Add(selectionStart, selectionLength, "[" + single + "](" + target + ")");
            result.SelectionStart = selectionStart + 1;
            result.SelectionLength = single.Length;
            result.SortEdits();
            return result;
        }
        for (int i = 0; i < segments.Count; i++) {
            LineSegment segment = segments[i];
            string text = EscapeLinkLabel(document.GetText(segment.Start, segment.End - segment.Start));
            result.Add(segment.Start, segment.End - segment.Start, "[" + text + "](" + target + ")");
        }
        if (result.Edits.Count == 0) return null;
        FinishSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }

    static string EscapeUrl(string url) {
        string value = url ?? string.Empty;
        return value.Replace(")", "%29").Replace(" ", "%20");
    }

    // A selected label's own "[" and "]" would end the label early ("a]b" became "[a]b](u)", which
    // neither parser reads as a link) and a trailing "\" would escape the closing "]". The live
    // parser reads "\x" as the character x and skips escaped brackets in a label, so the label is
    // escaped instead of losing its characters (which is what ImageAlt does for image labels).
    static string EscapeLinkLabel(string label) {
        string value = label ?? string.Empty;
        StringBuilder escaped = new StringBuilder(value.Length + 4);
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            if (c == '\\') {
                bool atEnd = i + 1 >= value.Length;
                char next = atEnd ? '\0' : value[i + 1];
                if (atEnd || next == '[' || next == ']') escaped.Append('\\');
                escaped.Append('\\');
            } else if (c == '[' || c == ']') {
                escaped.Append('\\').Append(c);
            } else {
                escaped.Append(c);
            }
        }
        return escaped.ToString();
    }
    #endregion

    #region InsertImage
    internal static CommandResult InsertImage(TextDocument document, int caretOffset, string path, string alt) {
        if (!Validate(document, caretOffset, 0)) return null;
        // An image token written inside a fence or an inline code run is literal text, not a
        // picture. 11-05 user decision: a caret in a closed fenced block puts the picture on a new
        // line right under the block's closing fence instead (BelowCodeBlock); an inline code run
        // and a block left open to the document's end still decline.
        string prefix = string.Empty;
        if (TouchesCode(document, caretOffset, 0)) {
            int below = BelowCodeBlock(document, caretOffset, out prefix);
            if (below < 0) return null;
            caretOffset = below;
        } else {
            if (CaretInsideInlineCode(document, caretOffset)) return null;
            if (!CaretInEditableText(document, caretOffset)) return null;
        }
        string target = path ?? string.Empty;
        if (target.IndexOf(' ') >= 0) target = "<" + target + ">";
        string text = prefix + "![" + ImageAlt(alt) + "](" + target + ")";
        CommandResult result = new CommandResult();
        result.Add(caretOffset, 0, text);
        result.SelectionStart = caretOffset + text.Length;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region ImageReference
    // The one image path rule for both views, the image button and file drops. A saved document
    // keeps the path relative to its folder with '/' ("../" allowed) when the image is on the same
    // drive; an unsaved document and an image on another drive use the absolute path. Spaces,
    // parentheses, '%', '#' and '?' are percent-encoded, and both renderers decode the reference
    // once before opening the file (Markdown.Inline and MarkdownLivePreview.ImageCache.Resolve).
    internal static string ImageReference(string documentPath, string imagePath) {
        string full;
        try {
            full = Path.GetFullPath(imagePath ?? string.Empty);
        } catch (Exception) {
            return EscapeImageReference((imagePath ?? string.Empty).Replace('\\', '/'));
        }
        string reference = null;
        if (!String.IsNullOrEmpty(documentPath)) {
            try {
                string folder = Path.GetDirectoryName(Path.GetFullPath(documentPath));
                if (!String.IsNullOrEmpty(folder) && String.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
                    reference = RelativeImagePath(folder, full);
            } catch (Exception) {
                reference = null;
            }
        }
        if (reference == null) reference = full.Replace('\\', '/');
        return EscapeImageReference(reference);
    }

    static string RelativeImagePath(string folder, string file) {
        string root = Path.GetPathRoot(file) ?? string.Empty;
        string[] from = ImagePathSegments(folder, root);
        string[] to = ImagePathSegments(file, root);
        int common = 0;
        while (common < from.Length && common < to.Length && String.Equals(from[common], to[common], StringComparison.OrdinalIgnoreCase)) common++;
        var parts = new List<string>();
        for (int i = common; i < from.Length; i++) parts.Add("..");
        for (int i = common; i < to.Length; i++) parts.Add(to[i]);
        return String.Join("/", parts.ToArray());
    }

    static string[] ImagePathSegments(string path, string root) {
        string rest = path.Length > root.Length ? path.Substring(root.Length) : string.Empty;
        return rest.Split(new char[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // The reference is percent-encoded so the target holds no space (TargetParts ends it there),
    // no '#' or '?' (a Uri reads them as a fragment/query) and no lone '%' (an invalid escape for
    // a Uri). Every renderer decodes the reference once, and the file Uri is built from the
    // decoded path with these same characters escaped again (Markdown.ImageUri), so a name like
    // "a#b%.png" opens under its real name. '%' first: the escapes below must not be escaped again.
    static string EscapeImageReference(string value) {
        return value.Replace("%", "%25").Replace("#", "%23").Replace("?", "%3F").Replace("(", "%28").Replace(")", "%29").Replace(" ", "%20");
    }

    // The image label may not hold a bracket or a line break: this app's own parsers read the
    // image token as "![label](target)" with no escape support for the rich view (MDNote.cs's
    // "[^\]]*") and Find("]") in the live view, so a ']' in a file name ended the label early
    // ("![a]b](a]b.png)"). Brackets and control characters are dropped instead of escaped, the
    // rule ImageMarkdown already used for ']'.
    static string ImageAlt(string alt) {
        string value = alt ?? string.Empty;
        StringBuilder cleaned = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++) {
            char c = value[i];
            if (c == '[' || c == ']' || c == '\r' || c == '\n' || c == '\t') continue;
            cleaned.Append(c);
        }
        return cleaned.ToString();
    }

    internal static string ImageMarkdown(string reference, string alt) {
        return "![" + ImageAlt(alt) + "](" + (reference ?? string.Empty) + ")";
    }

    // Drop/insert of several images at one offset: one command result, so the host records one
    // undo step; the images land in file order, one per line. A table row is the single line of the
    // block, so its drop stays on that line, the images separated by a space like text typed in the
    // cell the caret is in (UI bug hunt P3-8).
    internal static CommandResult InsertImages(TextDocument document, int caretOffset, IList<string> references, IList<string> alts) {
        if (!Validate(document, caretOffset, 0)) return null;
        if (references == null || alts == null || references.Count == 0 || references.Count != alts.Count) return null;
        // The image button's three guards apply to a drop as well: a token written inside a fence
        // or an inline code run is literal text, and structure (a delimiter row, a rule) never
        // takes one.
        // A caret in a closed fenced block takes the pictures right under the block (InsertImage).
        string prefix = string.Empty;
        if (TouchesCode(document, caretOffset, 0)) {
            int below = BelowCodeBlock(document, caretOffset, out prefix);
            if (below < 0) return null;
            caretOffset = below;
        } else {
            if (CaretInsideInlineCode(document, caretOffset)) return null;
            if (!CaretInEditableText(document, caretOffset)) return null;
        }
        DocumentLine line = document.GetLineByOffset(caretOffset);
        // One image per source line is right in a paragraph, but a table row is one line: a second
        // image written as a new source line would split the row in two. The row takes the whole
        // drop instead, since a single image drop into a cell already writes it at the caret there.
        bool row = references.Count > 1 && TableLineKind(document, line) == 1;
        string newline = NewlineFor(document, line);
        var text = new StringBuilder(prefix);
        for (int i = 0; i < references.Count; i++) {
            if (i > 0) text.Append(row ? " " : newline);
            text.Append(ImageMarkdown(references[i], alts[i]));
        }
        CommandResult result = new CommandResult();
        result.Add(caretOffset, 0, text.ToString());
        result.SelectionStart = caretOffset + text.Length;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }

    internal const string ImageFileFilter = "Image|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff";

    internal static bool IsImageFile(string path) {
        switch (Path.GetExtension(path ?? string.Empty).ToLowerInvariant()) {
        case ".png": case ".jpg": case ".jpeg": case ".gif": case ".bmp": case ".tif": case ".tiff": return true;
        default: return false;
        }
    }

    // A drop is handled as images only when every dropped file is one: a mixed drop keeps the
    // older open-as-document answer for all of them.
    internal static bool AllImageFiles(IList<string> files) {
        if (files == null || files.Count == 0) return false;
        foreach (string file in files) if (!IsImageFile(file)) return false;
        return true;
    }

    // Image files that are still on disk: a name that no longer exists must not become a broken
    // reference, so a multi-file drop keeps the existing images in file order and an empty answer
    // tells the caller to leave the drop to the open-as-document path.
    internal static string[] ExistingImageFiles(IList<string> files) {
        var existing = new List<string>();
        if (files == null) return existing.ToArray();
        foreach (string file in files) if (IsImageFile(file) && File.Exists(file)) existing.Add(file);
        return existing.ToArray();
    }
    #endregion

    #region InsertTable
    internal static CommandResult InsertTable(TextDocument document, int caretOffset, int rows, int columns) {
        return InsertTable(document, caretOffset, rows, columns, null);
    }

    // header is the host's localized "Heading"/"标题" word (T("제목")); null keeps the default, so
    // the document's header placeholder follows the UI language instead of always being Korean.
    internal static CommandResult InsertTable(TextDocument document, int caretOffset, int rows, int columns, string header) {
        if (!Validate(document, caretOffset, 0)) return null;
        if (rows < 0 || columns < 1) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        // A fence line and every line inside a fenced block are literal code: a grid written there
        // draws as code text, not as a table (the other block commands decline the same way).
        if (IsInsideCode(document, line)) return null;
        string newline = NewlineFor(document, line);
        int offset;
        string leading;
        BlockStart(document, line, newline, out offset, out leading);

        string head = String.IsNullOrEmpty(header) ? "제목" : header;
        StringBuilder text = new StringBuilder();
        text.Append('|');
        for (int c = 1; c <= columns; c++) text.Append(' ').Append(head).Append(c.ToString(CultureInfo.InvariantCulture)).Append(" |");
        text.Append(newline).Append('|');
        for (int c = 0; c < columns; c++) text.Append("---|");
        for (int r = 0; r < rows; r++) {
            text.Append(newline).Append('|');
            for (int c = 0; c < columns; c++) text.Append("  |");
        }
        if (offset < document.TextLength) {
            text.Append(newline);
            // A grid written straight against another table's header joins it: the old header and
            // its delimiter row read as rows of the new grid and the old table loses its shape.
            // One blank line keeps the two tables apart, the same gap the insert-below path
            // (MarkdownTableCommands.InsertTableSafely) and MarkdownWriter write. Only a real
            // header (a pipe row over its delimiter row) counts; a pipe paragraph stays adjacent.
            if (NextIsDelimiterRow(document, document.GetLineByOffset(offset))) text.Append(newline);
        }

        CommandResult result = new CommandResult();
        result.Add(offset, 0, leading + text.ToString());
        result.SelectionStart = offset + leading.Length + 2;
        result.SelectionLength = head.Length + 1;
        return result;
    }

    // Places a block on a new line after the caret's line; a line without a delimiter
    // either terminates first or, when it is an empty trailing line, is reused.
    static void BlockStart(TextDocument document, DocumentLine line, string newline, out int offset, out string leading) {
        if (line.DelimiterLength > 0) {
            offset = line.Offset + line.TotalLength;
            leading = string.Empty;
        } else if (line.Length == 0) {
            offset = line.Offset;
            leading = string.Empty;
        } else {
            offset = document.TextLength;
            leading = newline;
        }
    }
    #endregion

    #region SetSpanStyle
    const string SpanPrefix = "<span style=\"";
    const string SpanClose = "</span>";
    const string ColorProperty = "color";
    const string SizeProperty = "font-size";

    sealed class SpanMatch {
        internal bool Inside;
        internal int SpanStart;
        internal int SpanEnd;
        internal int StyleStart;
        internal int StyleLength;
        internal int InnerStart;
        internal int InnerEnd;
    }

    // Sets the color property of a span around the selection; null removes it.
    internal static CommandResult SetSpanColor(TextDocument document, int selectionStart, int selectionLength, string cssColor) {
        string color = null;
        if (cssColor != null) {
            color = NormalizeColor(cssColor);
            if (color == null) return null;
        }
        if (selectionLength == 0) return SetSpanPropertyAtCaret(document, selectionStart, ColorProperty, color);
        return SetSpanProperty(document, selectionStart, selectionLength, ColorProperty, color);
    }

    // Empty selection: the property applies to text typed next. Strictly inside a word
    // style the word instead, but keep the result a caret (never a replacement selection).
    static CommandResult SetSpanPropertyAtCaret(TextDocument document, int caret, string property, string value) {
        if (!Validate(document, caret, 0)) return null;
        if (TouchesCode(document, caret, 0)) return null;
        // Structure (a delimiter row, a setext underline, a rule, a row's pipes) never receives
        // a tag, and a tag armed inside an inline code run would be written as literal text
        // between the backticks. A strict inside-word caret is let through: the word expands and
        // NormalizeInlineSelection moves the tag outside the whole code run.
        if (!CaretInEditableText(document, caret)) return null;
        if (value != null && CaretInsideInlineCode(document, caret) && !IsStrictlyInsideWord(document, caret)) return null;
        if (IsStrictlyInsideWord(document, caret)) {
            int wordStart, wordEnd;
            if (ExpandWord(document, caret, out wordStart, out wordEnd)) {
                CommandResult word = SetSpanProperty(document, wordStart, wordEnd - wordStart, property, value);
                if (word == null) return null;
                word.SelectionStart = MapSpanWordCaret(word, caret, wordStart);
                word.SelectionLength = 0;
                return word;
            }
        }
        if (value == null) {
            // Removing a property needs a span to step out of; anything else is a no-op.
            if (!IsBeforeSpanClose(document, caret, property)) return null;
            CommandResult move = new CommandResult();
            move.SelectionStart = caret + SpanClose.Length;
            move.SelectionLength = 0;
            return move;
        }
        // Arm the property for the text typed next; the tag pair is written with that input
        // (WrapTyped), so an abandoned colour or size never leaves an empty span in the source.
        CommandResult result = new CommandResult();
        result.TypingOpen = SpanOpening(property, value);
        result.TypingClose = SpanClose;
        result.SelectionStart = caret;
        result.SelectionLength = 0;
        return result;
    }

    // The opening tag both setters write; the host arms the caret-only case with the same text.
    internal static string SpanOpening(string property, string value) {
        return "<span style=\"" + property + ":" + value + "\">";
    }

    // Caret kept inside a word whose span was wrapped, unwrapped or restyled. A wrapping
    // edit starts at the word (or at an emphasis marker before it, once the selection was
    // normalized); an unwrapping edit starts at the span start and keeps the word text; a
    // style-only edit sits before the word.
    static int MapSpanWordCaret(CommandResult result, int caret, int wordStart) {
        int delta = 0;
        for (int i = 0; i < result.Edits.Count; i++) {
            SourceEdit edit = result.Edits[i];
            if (edit.Offset + edit.Length <= wordStart) delta += edit.Text.Length - edit.Length;
        }
        // The edits may be sorted descending after a merged multi-segment command, so the whole
        // delta in front of the word is summed first and the containing edit is found on its own.
        for (int i = 0; i < result.Edits.Count; i++) {
            SourceEdit edit = result.Edits[i];
            if (edit.Offset + edit.Length <= wordStart) continue;
            if (edit.Offset > wordStart) continue;
            // A wrap keeps the word after its opening tag (which may start before the word
            // once an emphasis marker was taken in); an unwrap removes the tag and keeps the
            // word text at the edit's own start.
            int opening = edit.Text.Length - edit.Length - SpanClose.Length;
            if (opening > 0) return edit.Offset + delta + opening + (caret - edit.Offset);
            int removedOpening = edit.Length - edit.Text.Length - SpanClose.Length;
            return caret + delta - Math.Max(0, removedOpening);
        }
        return caret + delta;
    }

    // True when the caret sits directly before a span close with the requested property.
    static bool IsBeforeSpanClose(TextDocument document, int caret, string property) {
        if (caret + SpanClose.Length > document.TextLength) return false;
        if (string.CompareOrdinal(document.GetText(caret, SpanClose.Length), SpanClose) != 0) return false;
        DocumentLine line = document.GetLineByOffset(caret);
        string window = document.GetText(line.Offset, caret - line.Offset);
        int index = window.LastIndexOf(SpanPrefix, StringComparison.Ordinal);
        if (index < 0) return false;
        int quote = window.IndexOf('"', index + SpanPrefix.Length);
        if (quote < 0 || quote + 1 >= window.Length || window[quote + 1] != '>') return false;
        string style = window.Substring(index + SpanPrefix.Length, quote - index - SpanPrefix.Length);
        return HasStyleProperty(style, property);
    }

    static bool HasStyleProperty(string style, string property) {
        for (int start = 0; start < style.Length; ) {
            int end = style.IndexOf(';', start);
            if (end < 0) end = style.Length;
            string raw = style.Substring(start, end - start).Trim();
            start = end + 1;
            int colon = raw.IndexOf(':');
            if (colon <= 0 || colon + 1 >= raw.Length) continue;
            string name = raw.Substring(0, colon).Trim();
            string value = raw.Substring(colon + 1).Trim();
            if (value.Length > 0 && name.Equals(property, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Sets the font-size property in px; 0 removes it and keeps every other property.
    internal static CommandResult SetSpanSize(TextDocument document, int selectionStart, int selectionLength, int pixels) {
        if (pixels < 0) return null;
        string size = pixels == 0 ? null : pixels.ToString(CultureInfo.InvariantCulture) + "px";
        return SetSpanProperty(document, selectionStart, selectionLength, SizeProperty, size);
    }

    // Explicit caret command keeps SetSpanSize's existing selection-only contract.
    internal static CommandResult SetTypingSize(TextDocument document, int caret, int pixels) {
        if (pixels < 0) return null;
        string size = pixels == 0 ? null : pixels.ToString(CultureInfo.InvariantCulture) + "px";
        return SetSpanPropertyAtCaret(document, caret, SizeProperty, size);
    }

    #region InlineSpanSelection
    // One inline pair of a line: an emphasis marker pair ("**", "__", "*", "_", "~~") or an
    // inline code run. Offsets are line-relative; [OpenEnd, CloseStart) is the styled content.
    struct InlineRun {
        internal int OpenStart, OpenEnd, CloseStart, CloseEnd;
        internal bool Code;
    }

    // Moves the selection ends so an inline span lands outside the emphasis/code runs the
    // selection covers whole: with both markers inside the new tag the live view hides them and
    // parses the content ("<span ...>**작성 팁**</span>"), while a tag around half a marker
    // would leave raw markup on the line. A run end splits moves to that run's far edge, and an
    // inline code run is always taken whole, so a span never lands between backticks
    // ("`x`" -> "<span ...>`x`</span>"). A partial selection of a bold run keeps the old
    // behaviour and nests the span inside it. Only the selection's own line is read (O(line)),
    // and a multi-line selection keeps the literal wrap.
    internal static void NormalizeInlineSelection(TextDocument document, ref int selectionStart, ref int selectionLength) {
        if (selectionLength <= 0) return;
        int end = selectionStart + selectionLength;
        DocumentLine line = document.GetLineByOffset(selectionStart);
        if (end > line.EndOffset) return;
        string text = LineText(document, line);
        List<InlineRun> runs = InlineRuns(text);
        if (runs.Count == 0) return;
        int start = selectionStart - line.Offset, stop = end - line.Offset;
        // Nested runs ("***x***", "**`x`**") widen one pass at a time; the loop stops as soon
        // as a pass finds nothing, so an ordinary selection costs one walk over the runs.
        for (int pass = 0; pass < 8; pass++) {
            bool changed = false;
            foreach (InlineRun run in runs) {
                if (start > run.OpenStart && start < run.OpenEnd) { start = run.OpenStart; changed = true; }
                if (stop > run.CloseStart && stop < run.CloseEnd) { stop = run.CloseEnd; changed = true; }
                bool covers = start <= run.OpenEnd && stop >= run.CloseStart;
                bool overlapsCode = run.Code && start < run.CloseStart && stop > run.OpenEnd;
                bool splitsOpen = start < run.OpenEnd && stop > run.OpenEnd && stop < run.CloseStart;
                bool splitsClose = start > run.OpenEnd && start < run.CloseStart && stop > run.CloseStart;
                if (!covers && !overlapsCode && !splitsOpen && !splitsClose) continue;
                int opened = Math.Min(start, run.OpenStart), closed = Math.Max(stop, run.CloseEnd);
                if (opened != start || closed != stop) { start = opened; stop = closed; changed = true; }
            }
            if (!changed) break;
        }
        selectionStart = line.Offset + start;
        selectionLength = stop - start;
    }

    // The inline runs of one line, read with the live parser's rules (the same token order and
    // the same opener/closer whitespace and intraword tests). Link labels, image spans and
    // <span>/<font> tags are skipped: the live parser does not read markers inside them either,
    // so a span wrapped around those would only show its own tags as text.
    static List<InlineRun> InlineRuns(string text) {
        List<InlineRun> runs = new List<InlineRun>();
        int[] opens = { -1, -1, -1, -1, -1 };
        string[] tokens = { "**", "__", "*", "_", "~~" };
        for (int i = 0; i < text.Length; ) {
            char c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '`') {
                int fence = 1;
                while (i + fence < text.Length && text[i + fence] == '`') fence++;
                int close = CodeRunEnd(text, i + fence, fence);
                if (close >= 0) {
                    runs.Add(new InlineRun { OpenStart=i, OpenEnd=i + fence, CloseStart=close, CloseEnd=close + fence, Code=true });
                    i = close + fence;
                } else i += fence;
                continue;
            }
            if (c == '<' && (TagStart(text, i, "span") || TagStart(text, i, "font"))) {
                int tagEnd = text.IndexOf('>', i + 5);
                if (tagEnd >= 0) { i = tagEnd + 1; continue; }
            }
            if (c == '[' || (c == '!' && i + 1 < text.Length && text[i + 1] == '[')) {
                int after = LinkEnd(text, c == '!' ? i + 1 : i);
                if (after > 0) { i = after; continue; }
            }
            int kind = -1;
            for (int t = 0; t < tokens.Length; t++) if (MatchesAt(text, i, tokens[t])) { kind = t; break; }
            if (kind < 0) { i++; continue; }
            int length = tokens[kind].Length;
            if (c == '_' && i > 0 && i + length < text.Length && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + length])) { i += length; continue; }
            if (opens[kind] < 0) {
                if (i + length < text.Length && !char.IsWhiteSpace(text[i + length])) opens[kind] = i;
            } else if (i > opens[kind] + length && !char.IsWhiteSpace(text[i - 1])) {
                int open = opens[kind];
                runs.Add(new InlineRun { OpenStart=open, OpenEnd=open + length, CloseStart=i, CloseEnd=i + length });
                opens[kind] = -1;
            }
            i += length;
        }
        return runs;
    }

    static bool MatchesAt(string text, int at, string token) {
        if (at < 0 || at + token.Length > text.Length) return false;
        return string.CompareOrdinal(text, at, token, 0, token.Length) == 0;
    }

    // The offset of a backtick run of exactly `fence` characters, or -1.
    static int CodeRunEnd(string text, int start, int fence) {
        for (int p = start; p < text.Length; ) {
            if (text[p] != '`') { p++; continue; }
            int run = 1;
            while (p + run < text.Length && text[p + run] == '`') run++;
            if (run == fence) return p;
            p += run;
        }
        return -1;
    }

    // "<span" / "<font" followed by whitespace, like the live parser's MatchesTag.
    static bool TagStart(string text, int at, string name) {
        if (at < 0 || at + name.Length + 1 >= text.Length || text[at] != '<') return false;
        for (int k = 0; k < name.Length; k++) if (char.ToLowerInvariant(text[at + 1 + k]) != name[k]) return false;
        char next = text[at + 1 + name.Length];
        return next == ' ' || next == '\t';
    }

    // The offset just past a link starting at `at` ("[label](url)", "[label][id]"), or -1 when
    // the bracket is plain text (whose label keeps its inline markers).
    static int LinkEnd(string text, int at) {
        int labelEnd = text.IndexOf(']', at + 1);
        if (labelEnd <= at + 1) return -1;
        if (labelEnd + 1 < text.Length && text[labelEnd + 1] == '(') {
            int close = text.IndexOf(')', labelEnd + 2);
            return close >= 0 ? close + 1 : -1;
        }
        if (labelEnd + 1 < text.Length && text[labelEnd + 1] == '[') {
            int idEnd = text.IndexOf(']', labelEnd + 2);
            return idEnd >= 0 ? idEnd + 1 : -1;
        }
        return -1;
    }
    #endregion

    // One property edit shared by both setters: replace/add/remove only the requested
    // property, keep the rest, and unwrap the span when the style ends up empty.
    static CommandResult SetSpanProperty(TextDocument document, int selectionStart, int selectionLength, string property, string value) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        // The tag must enclose an emphasis or code run from the outside, never cut one of its
        // markers: the live parser hides a marker pair only when both markers sit in the tag.
        NormalizeInlineSelection(document, ref selectionStart, ref selectionLength);
        if (TouchesCode(document, selectionStart, selectionLength)) return null;
        if (selectionLength == 0) return null;
        List<LineSegment> segments = InlineSegments(document, selectionStart, selectionLength);
        if (segments.Count == 0) return null;
        // A span is drawn only inside one line (the live parser looks for its close on the same
        // line), so a multi-line selection and a table row are wrapped in pieces: one span per
        // line body or cell, merged into one command (one undo step). The pipes of a table row
        // stay outside every tag. One segment that is exactly the selection keeps the older
        // single-range path, so its result selection still sits inside the new tag.
        if (segments.Count == 1 && segments[0].Start == selectionStart && segments[0].End == selectionStart + selectionLength)
            return SetSpanPropertyRange(document, selectionStart, selectionLength, property, value);
        return MergeSpanSegments(document, segments, property, value, selectionStart, selectionStart + selectionLength);
    }

    static CommandResult MergeSpanSegments(TextDocument document, List<LineSegment> segments, string property, string value, int originalStart, int originalEnd) {
        CommandResult merged = new CommandResult();
        for (int i = 0; i < segments.Count; i++) {
            LineSegment segment = segments[i];
            int start = segment.Start;
            int length = segment.End - segment.Start;
            if (length <= 0) continue;
            NormalizeInlineSelection(document, ref start, ref length);
            if (length <= 0 || TouchesCode(document, start, length)) continue;
            CommandResult part = SetSpanPropertyRange(document, start, length, property, value);
            if (part == null) continue;
            for (int e = 0; e < part.Edits.Count; e++) merged.Edits.Add(part.Edits[e]);
        }
        if (merged.Edits.Count == 0) return null;
        FinishSelection(merged, originalStart, originalEnd);
        return merged;
    }

    static CommandResult SetSpanPropertyRange(TextDocument document, int selectionStart, int selectionLength, string property, string value) {
        SpanMatch match;
        if (!TryFindSpan(document, selectionStart, selectionLength, out match)) {
            if (value == null) return null;
            string selected = document.GetText(selectionStart, selectionLength);
            string opening = SpanOpening(property, value);
            CommandResult wrap = new CommandResult();
            wrap.Add(selectionStart, selectionLength, opening + selected + SpanClose);
            wrap.SelectionStart = selectionStart + opening.Length;
            wrap.SelectionLength = selectionLength;
            wrap.SortEdits();
            return wrap;
        }

        string oldStyle = document.GetText(match.StyleStart, match.StyleLength);
        string newStyle = MergeStyle(oldStyle, property, value);
        if (string.Equals(newStyle, oldStyle, StringComparison.Ordinal)) return null;

        CommandResult result = new CommandResult();
        if (newStyle.Length == 0) {
            string inner = document.GetText(match.InnerStart, match.InnerEnd - match.InnerStart);
            result.Add(match.SpanStart, match.SpanEnd - match.SpanStart, inner);
            result.SelectionStart = match.SpanStart;
            result.SelectionLength = match.InnerEnd - match.InnerStart;
            result.SortEdits();
            return result;
        }
        result.Add(match.StyleStart, match.StyleLength, newStyle);
        int delta = newStyle.Length - match.StyleLength;
        if (match.Inside) {
            result.SelectionStart = match.SpanStart;
            result.SelectionLength = (match.SpanEnd - match.SpanStart) + delta;
        } else {
            result.SelectionStart = match.InnerStart + delta;
            result.SelectionLength = match.InnerEnd - match.InnerStart;
        }
        result.SortEdits();
        return result;
    }

    // Canonical style text: color first, then font-size, then other properties in their
    // original order. A null value drops its property; property text without a name:value
    // pair is discarded.
    static string MergeStyle(string style, string property, string value) {
        string color = null, size = null;
        List<string> others = new List<string>();
        for (int start = 0; start < style.Length; ) {
            int end = style.IndexOf(';', start);
            if (end < 0) end = style.Length;
            string raw = style.Substring(start, end - start).Trim();
            start = end + 1;
            if (raw.Length == 0) continue;
            int colon = raw.IndexOf(':');
            if (colon < 0) continue;
            string name = raw.Substring(0, colon).Trim();
            string oldValue = raw.Substring(colon + 1).Trim();
            if (name.Length == 0 || oldValue.Length == 0) continue;
            if (name.Equals(ColorProperty, StringComparison.OrdinalIgnoreCase)) color = oldValue;
            else if (name.Equals(SizeProperty, StringComparison.OrdinalIgnoreCase)) size = oldValue;
            else others.Add(raw);
        }
        if (string.Equals(property, ColorProperty, StringComparison.Ordinal)) color = value;
        else size = value;

        StringBuilder builder = new StringBuilder();
        if (color != null) builder.Append(ColorProperty).Append(':').Append(color);
        if (size != null) {
            if (builder.Length > 0) builder.Append(';');
            builder.Append(SizeProperty).Append(':').Append(size);
        }
        for (int i = 0; i < others.Count; i++) {
            if (builder.Length > 0) builder.Append(';');
            builder.Append(others[i]);
        }
        return builder.ToString();
    }

    static string NormalizeColor(string cssColor) {
        string value = cssColor.Trim();
        if (value.Length == 0) return null;
        if (value[0] != '#') value = "#" + value;
        return value;
    }

    static bool TryFindSpan(TextDocument document, int selectionStart, int selectionLength, out SpanMatch match) {
        match = null;
        int selectionEnd = selectionStart + selectionLength;
        string selected = document.GetText(selectionStart, selectionLength);
        if (selected.StartsWith(SpanPrefix, StringComparison.Ordinal) && selected.EndsWith(SpanClose, StringComparison.Ordinal)) {
            int styleStart = SpanPrefix.Length;
            int quote = selected.IndexOf('"', styleStart);
            if (quote >= styleStart && quote + 1 < selected.Length && selected[quote + 1] == '>') {
                int innerStart = quote + 2;
                int innerEnd = selected.Length - SpanClose.Length;
                if (innerEnd >= innerStart) {
                    match = new SpanMatch();
                    match.Inside = true;
                    match.SpanStart = selectionStart;
                    match.SpanEnd = selectionEnd;
                    match.StyleStart = selectionStart + styleStart;
                    match.StyleLength = quote - styleStart;
                    match.InnerStart = selectionStart + innerStart;
                    match.InnerEnd = selectionStart + innerEnd;
                    return true;
                }
            }
        }
        if (selectionEnd + SpanClose.Length > document.TextLength) return false;
        if (string.CompareOrdinal(document.GetText(selectionEnd, SpanClose.Length), SpanClose) != 0) return false;
        // Only the selected line is inspected, like the other line-based commands.
        int windowStart = document.GetLineByOffset(selectionStart).Offset;
        string window = document.GetText(windowStart, selectionStart - windowStart);
        int index = window.LastIndexOf(SpanPrefix, StringComparison.Ordinal);
        if (index < 0) return false;
        string tag = window.Substring(index);
        if (!tag.EndsWith("\">", StringComparison.Ordinal)) return false;
        if (tag.IndexOf('"', SpanPrefix.Length) != tag.Length - 2) return false;
        int styleLength = tag.Length - 2 - SpanPrefix.Length;
        if (styleLength < 0) return false;
        match = new SpanMatch();
        match.Inside = false;
        match.SpanStart = windowStart + index;
        match.SpanEnd = selectionEnd + SpanClose.Length;
        match.StyleStart = match.SpanStart + SpanPrefix.Length;
        match.StyleLength = styleLength;
        match.InnerStart = selectionStart;
        match.InnerEnd = selectionEnd;
        return true;
    }
    #endregion
}
}
