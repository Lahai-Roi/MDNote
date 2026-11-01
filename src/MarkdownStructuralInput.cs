using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// P5: structural keys as pure source transformations. Enter continues a list, a quote or
// the indentation of a fenced code block and ends an empty item at its own level; Tab/Shift+Tab
// indent list items; Backspace removes a list marker, one indentation unit or one quote level.
// Every function reads the caret line and a bounded number of neighbours only, never the whole
// document, and returns edits in descending offset order plus the caret position after the
// edit. Null means "not handled": the host lets the editor run its own key behaviour.
internal static class MarkdownStructuralInput {
    const int NeighbourLimit = 200;
    const int FenceScanLimit = 400;
    // Bound of the scan for a table in the middle of a selection (SelectionCoversTable). This is
    // a work bound, not a table's row cap: a selection too long to read is reported as covering a
    // table, so the key is swallowed instead of replacing the selection with a tab character.
    const int SelectionScanLimit = 2000;

    // Prefix slots of one line. The rules match MarkdownCommands.ParsePrefix, so a line is
    // a list item exactly when the P4 toolbar commands think it is. That parser's helpers
    // are private to MarkdownCommands and this task keeps that file untouched, so the few
    // slots needed here are parsed locally with the same rules.
    sealed class LineSlots {
        internal int IndentEnd;
        internal int QuoteEnd;
        internal int ListStart;
        internal int ListEnd;
        internal bool HasList;
        internal ListKind Kind;
        internal char BulletChar;
        internal char OrderedSeparator;
        internal int NumberLength;
    }

    #region Shared helpers
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
        if (length == 0) last = first;
        else last = document.GetLineByOffset(start + length - 1);
        DocumentLine current = first;
        while (current != null) {
            lines.Add(current);
            if (current == last) break;
            current = current.NextLine;
        }
        return lines;
    }

    // Newline of the caret line; a last line without a delimiter reuses the previous
    // line's style instead of hard-coding a platform newline.
    static string NewlineFor(TextDocument document, DocumentLine line) {
        if (line.DelimiterLength > 0) return document.GetText(line.EndOffset, line.DelimiterLength);
        DocumentLine previous = line.PreviousLine;
        if (previous != null && previous.DelimiterLength > 0) return document.GetText(previous.EndOffset, previous.DelimiterLength);
        return "\n";
    }

    // A marker's separator: the renderer and MarkdownCommands.ParsePrefix treat a space and a
    // tab alike after ">", a bullet and an ordered marker, so the structural keys read the same
    // line the screen draws ("-\titem" is a list item here too).
    static bool IsSpaceOrTab(char c) {
        return c == ' ' || c == '\t';
    }

    static LineSlots ParseSlots(string text) {
        LineSlots slots = new LineSlots();
        int length = text.Length;
        int i = 0;
        while (i < length && (text[i] == ' ' || text[i] == '\t')) i++;
        slots.IndentEnd = i;
        while (i < length && text[i] == '>') {
            i++;
            if (i < length && IsSpaceOrTab(text[i])) i++;
        }
        slots.QuoteEnd = i;
        // The item's own room inside the quote counts like a top-level indentation: the renderer
        // finds the marker after these spaces (ReadList skips them), so ">   1. a" is a nested
        // quoted list item and the structural keys have to read the same line the same way.
        while (i < length && (text[i] == ' ' || text[i] == '\t')) i++;
        slots.ListStart = i;
        slots.ListEnd = i;

        int markerEnd = -1;
        if (i < length) {
            char c = text[i];
            if ((c == '-' || c == '*' || c == '+') && i + 1 < length && IsSpaceOrTab(text[i + 1])) {
                markerEnd = i + 2;
                slots.HasList = true;
                slots.Kind = ListKind.Bullet;
                slots.BulletChar = c;
            } else if (c >= '0' && c <= '9') {
                int j = i;
                while (j < length && j - i < 9 && text[j] >= '0' && text[j] <= '9') j++;
                if (j < length && (text[j] == '.' || text[j] == ')') && j + 1 < length && IsSpaceOrTab(text[j + 1])) {
                    markerEnd = j + 2;
                    slots.HasList = true;
                    slots.Kind = ListKind.Number;
                    slots.OrderedSeparator = text[j];
                    slots.NumberLength = j - i;
                }
            }
        }
        if (slots.HasList) {
            slots.ListEnd = markerEnd;
            // The box does not need a space after it: the renderer ends the box at "]" ("- [x]done"
            // is a checked task whose body is "done"), so Enter, Tab and Backspace read the same
            // marker the screen draws. Only a separator that is really there is stepped over.
            if (slots.Kind == ListKind.Bullet && markerEnd + 2 < length
                && text[markerEnd] == '[' && text[markerEnd + 2] == ']'
                && (text[markerEnd + 1] == ' ' || text[markerEnd + 1] == 'x' || text[markerEnd + 1] == 'X')) {
                slots.Kind = ListKind.Task;
                slots.ListEnd = markerEnd + 3;
                if (markerEnd + 3 < length && IsSpaceOrTab(text[markerEnd + 3])) slots.ListEnd++;
            }
        }
        return slots;
    }

    // Post-edit position of an original offset. Selection starts move after an insertion
    // at exactly that offset; selection ends stop before it.
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

    static void MapSelection(CommandResult result, int originalStart, int originalEnd) {
        bool caret = originalStart == originalEnd;
        int start = MapPosition(result.Edits, originalStart, true);
        int end = MapPosition(result.Edits, originalEnd, caret);
        if (end < start) end = start;
        result.SelectionStart = start;
        result.SelectionLength = end - start;
        result.SortEdits();
    }

    static bool TryFenceMark(string text, out char character, out int length) {
        character = '\0';
        length = 0;
        int i = 0;
        while (i < text.Length && i < 3 && text[i] == ' ') i++;
        if (i >= text.Length) return false;
        char c = text[i];
        if (c != '`' && c != '~') return false;
        int start = i;
        while (i < text.Length && text[i] == c) i++;
        length = i - start;
        if (length < 3) return false;
        character = c;
        return true;
    }

    static bool IsFenceLine(TextDocument document, DocumentLine line) {
        char character;
        int length;
        return TryFenceMark(LineText(document, line), out character, out length);
    }

    // Visible to MarkdownTableCommands, which needs the same answer for a table boundary.
    internal static bool IsInsideFence(TextDocument document, DocumentLine line) {
        if (IsFenceLine(document, line)) return true;
        List<char> characters = new List<char>();
        List<int> lengths = new List<int>();
        DocumentLine current = line.PreviousLine;
        int scanned = 0;
        while (current != null && scanned < FenceScanLimit) {
            char character;
            int length;
            if (TryFenceMark(LineText(document, current), out character, out length)) {
                characters.Add(character);
                lengths.Add(length);
            }
            current = current.PreviousLine;
            scanned++;
        }
        // Like MarkdownCommands: a fence starting above the scan limit is treated as "not
        // inside code" instead of guessing from a truncated history.
        if (current != null) return false;
        bool inside = false;
        char openCharacter = '\0';
        int openLength = 0;
        for (int i = characters.Count - 1; i >= 0; i--) {
            if (!inside) {
                inside = true;
                openCharacter = characters[i];
                openLength = lengths[i];
            } else if (characters[i] == openCharacter && lengths[i] >= openLength) {
                inside = false;
            }
        }
        return inside;
    }

    // Same shape as MarkdownCommands.TouchesCode: the first touched line decides the block
    // and a fence line itself is never a structural key target.
    static bool TouchesFence(TextDocument document, List<DocumentLine> lines) {
        if (lines.Count == 0) return false;
        if (IsInsideFence(document, lines[0])) return true;
        for (int i = 0; i < lines.Count; i++) {
            if (IsFenceLine(document, lines[i])) return true;
        }
        return false;
    }
    #endregion

    #region Enter
    // Text inserted after the newline for the caret at column. Null means no rule applies
    // at this position. nextNumber is the new ordered number when the line continues an
    // ordered item; 0 means no renumbering is needed.
    static string ContinuationFor(TextDocument document, DocumentLine line, string text, LineSlots slots, int column, out int nextNumber, out string indent) {
        nextNumber = 0;
        indent = text.Substring(0, slots.IndentEnd);
        if (IsInsideFence(document, line)) {
            // E1c: inside a fence only the leading whitespace is repeated.
            if (column < slots.IndentEnd) return null;
            return indent;
        }
        bool quoted = slots.QuoteEnd > slots.IndentEnd;
        if (column == 0 && (slots.HasList || quoted)) return null;
        if (slots.HasList) {
            // A caret inside the task box counts as inside the marker.
            int markerEnd = slots.ListEnd;
            if (slots.Kind == ListKind.Task && markerEnd + 2 < text.Length && text[markerEnd] == '[' && text[markerEnd + 2] == ']') markerEnd += 3;
            if (column < markerEnd) {
                // The caret is inside the marker: a quote line still continues its quote.
                if (quoted && column >= slots.QuoteEnd) return text.Substring(0, slots.QuoteEnd);
                return null;
            }
            // Renumbering compares the item's own prefix, which includes the quote and the room
            // after it, so "> 1. a" and ">   1. b" are items at two different levels.
            indent = text.Substring(0, slots.ListStart);
            return ListContinuation(text, slots, out nextNumber);
        }
        if (quoted) {
            if (column >= slots.QuoteEnd) return text.Substring(0, slots.QuoteEnd);
            return null;
        }
        return null;
    }

    // E1a/E1b: the same indentation (and quote prefix) followed by the same marker kind.
    // Task items always continue as "- [ ] ".
    static string ListContinuation(string text, LineSlots slots, out int nextNumber) {
        nextNumber = 0;
        string head = text.Substring(0, slots.ListStart);
        if (slots.Kind == ListKind.Task) return head + "- [ ] ";
        if (slots.Kind == ListKind.Number) {
            int current = 1;
            int.TryParse(text.Substring(slots.ListStart, slots.NumberLength), NumberStyles.None, CultureInfo.InvariantCulture, out current);
            nextNumber = current + 1;
            return head + nextNumber.ToString(CultureInfo.InvariantCulture) + slots.OrderedSeparator.ToString() + " ";
        }
        return head + slots.BulletChar.ToString() + " ";
    }

    // A caret strictly inside a run of inline marker characters ("**", "~~", "`", "__") would
    // be split in two by Enter, leaving one half at each line end. The split point moves to the
    // run's outer edge first: the start of a run that follows content-less text (an opener) and
    // the end of a run that follows text (a closer), so "**굵게**" keeps one pair on one line.
    // A run with text on both sides (literal markup) takes the nearer end. Bounded to the line.
    // The cell's Enter path (MarkdownTableCommands.EnterInCell) asks for the single-marker edge.
    internal static int MarkerRunEdge(string text, int column) {
        return MarkerRunEdge(text, column, false);
    }
    // singleMarkerEdge: the cell's Enter writes a "<br>" and must not turn an inert marker run
    // into an opener. Ordinary Enter keeps its old edges: a lone "*" that is a list bullet or a
    // line's first character is a structural marker, and moving its split would write the line
    // break on the wrong side of the bullet.
    internal static int MarkerRunEdge(string text, int column, bool singleMarkerEdge) {
        if (text == null || column <= 0 || column >= text.Length) return column;
        char c = text[column];
        int start, end;
        if (IsMarkerChar(c)) {
            start = column;
            while (start > 0 && text[start - 1] == c) start--;
            end = column;
            while (end < text.Length && text[end] == c) end++;
        } else {
            // The caret right after a marker run is that run's other edge. A run with whitespace
            // on its right and no content on its left is inert ("** " in a cell's padding); a
            // split there would put the new break's non-space character after the marker and
            // turn it into an opener ("** <br>" becomes "**<br>", pairing a run in another cell
            // across the row's pipes). Moving the split to the run's start keeps the whitespace
            // on its right, so the marker stays literal. The run's length does not matter: a
            // marker run that whitespace follows can never open emphasis.
            char before = text[column - 1];
            if (!IsMarkerChar(before)) return column;
            start = column;
            while (start > 0 && text[start - 1] == before) start--;
            if (singleMarkerEdge && char.IsWhiteSpace(text[column]) && !HasContentBefore(text, start))
                return start;
            return column;
        }
        if (end - start < 2) return column;
        // Only a caret strictly inside the run is a split point; the run's own edges are the
        // positions Enter must keep (a caret before "**ab**" writes its line there).
        if (column <= start || column >= end) return column;
        char after = end < text.Length ? text[end] : '\0';
        bool contentAfter = end < text.Length && !char.IsWhiteSpace(after);
        if (HasContentBefore(text, start)) return end;
        if (contentAfter) return start;
        return column - start <= end - column ? start : end;
    }
    static bool IsMarkerChar(char c) {
        return c == '*' || c == '~' || c == '`' || c == '_';
    }
    // Text that can precede a closing marker: a word character or the closing punctuation of
    // a link/image/code unit. Whitespace or line start means the run cannot be a closer.
    static bool HasContentBefore(string text, int start) {
        if (start <= 0) return false;
        char before = text[start - 1];
        return char.IsLetterOrDigit(before) || before == ')' || before == ']' || before == '}' || before == '!' || before == '?';
    }

    // The Enter key. The editor's own newline text path (Shift+Enter, an IME Enter) calls the
    // overload below with endEmptyItem false and keeps its existing output, so only the plain
    // key ends an empty item.
    internal static CommandResult Enter(TextDocument document, int selectionStart, int selectionLength) {
        return Enter(document, selectionStart, selectionLength, true);
    }

    internal static CommandResult Enter(TextDocument document, int selectionStart, int selectionLength, bool endEmptyItem) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        bool selected = selectionLength > 0;
        int selectionEnd = selectionStart + selectionLength;
        DocumentLine line = document.GetLineByOffset(selectionStart);
        string text = LineText(document, line);
        int column = selectionStart - line.Offset;
        // H9: Enter inside a marker run splits at the run's outer edge, never inside it.
        bool moved = false;
        if (!selected && !IsInsideFence(document, line)) {
            int edge = MarkerRunEdge(text, column);
            if (edge != column) { column = edge; selectionStart = line.Offset + edge; moved = true; }
        }
        LineSlots slots = ParseSlots(text);
        // C2: Enter on an empty quote line leaves the quote instead of repeating its prefix, so a
        // heading typed next is outside it. A quoted list item's marker goes first and the next
        // Enter takes the quote. The caret must be past the prefix: at column zero the key still
        // makes room above the quote.
        int contentStart = slots.HasList ? slots.ListEnd : slots.QuoteEnd;
        if (!selected && slots.QuoteEnd > slots.IndentEnd && column >= contentStart && !IsInsideFence(document, line)) {
            CommandResult exit = ExitEmptyQuote(line, text, slots);
            if (exit != null) return exit;
        }
        // E1d: an empty item never continues itself. An indented one moves one level out on the
        // spot and a top-level one ends the list, so Enter on the item that Enter just wrote
        // leaves that item instead of adding another blank one. Cheap checks first; the fence
        // scan runs only for a list line whose body is blank.
        if (endEmptyItem && !selected && slots.HasList && slots.QuoteEnd == slots.IndentEnd
            && column >= slots.ListEnd && IsBlankText(text.Substring(slots.ListEnd))
            && !IsInsideFence(document, line)) {
            CommandResult emptyItem = ExitEmptyListItem(document, line, text, slots, selectionStart);
            if (emptyItem != null) return emptyItem;
        }
        int nextNumber;
        string indent;
        string continuation = ContinuationFor(document, line, text, slots, column, out nextNumber, out indent);
        // A line with no continuation of its own is normally left to the editor. When the H9
        // snap moved the split point, the newline must go at that edge instead, so the command
        // supplies just the line break (no continuation prefix).
        if (continuation == null && !selected) {
            if (!moved) return null;
            string edgeNewline = NewlineFor(document, line);
            CommandResult edgeResult = new CommandResult();
            edgeResult.Add(selectionStart, 0, edgeNewline);
            edgeResult.SelectionStart = selectionStart + edgeNewline.Length;
            edgeResult.SelectionLength = 0;
            return edgeResult;
        }

        string newline = NewlineFor(document, line);
        string inserted = newline + (continuation == null ? string.Empty : continuation);
        CommandResult result = new CommandResult();
        // E0: a selection is replaced by the newline plus whatever the line would continue.
        if (selected) result.Add(selectionStart, selectionLength, inserted);
        else result.Add(selectionStart, 0, inserted);

        if (nextNumber > 0) {
            // Renumber starts on the line after the remainder, so no edit can land inside
            // the replaced range.
            DocumentLine renumberFrom;
            if (selected) renumberFrom = document.GetLineByOffset(selectionEnd).NextLine;
            else renumberFrom = line.NextLine;
            AddRenumber(document, result, renumberFrom, indent, nextNumber + 1);
        }

        result.SortEdits();
        result.SelectionStart = selectionStart + inserted.Length;
        result.SelectionLength = 0;
        return result;
    }

    // C2: an empty quoted line loses one level and nothing is inserted, so the line the user is
    // leaving becomes the line they type on. An empty quoted list item drops its marker first
    // ("> - " becomes "> "); the next Enter takes the innermost quote level ("> > " becomes
    // "> ", "> " becomes an empty line). Outer quote levels and the line's own indentation stay.
    static CommandResult ExitEmptyQuote(DocumentLine line, string text, LineSlots slots) {
        int from;
        if (slots.HasList) {
            // The marker is the whole content: "> - ", "> 5. " and "> - [ ] " lose the marker.
            if (!IsBlankText(text.Substring(slots.ListEnd))) return null;
            from = slots.ListStart;
        } else {
            // A quote line with any text on it keeps the prefix (the ordinary Enter rule).
            if (!IsBlankText(text.Substring(slots.QuoteEnd))) return null;
            int levelStart;
            int levelEnd;
            if (!FindQuoteLevelEnd(text, slots, slots.QuoteEnd, out levelStart, out levelEnd)) return null;
            from = levelStart;
        }
        CommandResult result = new CommandResult();
        // The rest of the line is blank, so removing the level also takes its trailing spaces.
        result.Add(line.Offset + from, text.Length - from, string.Empty);
        result.SelectionStart = line.Offset + from;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }

    // E1d: Enter on an empty list item leaves the line it is on instead of writing another empty
    // item, the rule Word, Typora, Obsidian and Notion share. An indented item moves one indent
    // unit out on the spot ("  - " becomes "- ", "\t1. " becomes "1. "); a top-level item loses
    // its marker and becomes the empty paragraph line that ends the list ("- ", "1. " and
    // "- [ ] " become ""). Nothing is inserted, so the caret line is the line the key leaves and
    // the next Enter is the ordinary paragraph break. A quoted item keeps the C2 rule above: its
    // marker goes first and the quote level after it. The caller has already checked the fence,
    // the caret being past the marker and that only blank text follows it.
    static CommandResult ExitEmptyListItem(TextDocument document, DocumentLine line, string text, LineSlots slots, int caret) {
        CommandResult result = new CommandResult();
        if (slots.IndentEnd > 0) {
            // One unit goes - the width Tab writes and Shift+Tab removes - so the marker moves
            // out a level and keeps its own marker text ("  - " -> "- "). Numbered runs at the
            // level the item leaves and the one it joins stay consecutive, exactly like Shift+Tab.
            int remove = IndentRemoveLength(text, slots.IndentEnd);
            result.Add(line.Offset, remove, string.Empty);
            if (slots.Kind == ListKind.Number) {
                List<DocumentLine> moved = new List<DocumentLine>();
                moved.Add(line);
                RenumberTabRuns(document, result, moved, true);
            }
            // The caret keeps its place in the marker's own text: every edit that ends at or
            // before it moves it by its width change - the indent unit, this line's renumbered
            // marker ("9." -> "10.") and a renumbered run above it. An edit below cannot.
            int delta = 0;
            for (int i = 0; i < result.Edits.Count; i++) {
                SourceEdit edit = result.Edits[i];
                if (edit.Offset + edit.Length <= caret) delta += edit.Text.Length - edit.Length;
            }
            result.SelectionStart = caret + delta;
            result.SelectionLength = 0;
            result.SortEdits();
            return result;
        }
        // Top level: the marker and the blank tail go, so an empty paragraph line ends the list.
        result.Add(line.Offset, text.Length, string.Empty);
        result.SelectionStart = line.Offset;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }

    // E1a renumbering: consecutive items at the same indentation, at most 200 lines. A
    // bullet or task item at that indentation keeps its place in the run but does not
    // consume a number. A deeper item between them (a child of the item above) is skipped the
    // same way TabSiblingRun skips it: it neither joins this level nor ends the run, so the
    // siblings after it are renumbered too.
    static void AddRenumber(TextDocument document, CommandResult result, DocumentLine start, string indent, int next) {
        DocumentLine current = start;
        int scanned = 0;
        while (current != null && scanned < NeighbourLimit) {
            string text = LineText(document, current);
            LineSlots slots = ParseSlots(text);
            if (!slots.HasList) break;
            string own = text.Substring(0, slots.ListStart);
            if (string.CompareOrdinal(own, indent) != 0) {
                if (own.Length > indent.Length && own.StartsWith(indent, StringComparison.Ordinal)) {
                    current = current.NextLine;
                    scanned++;
                    continue;
                }
                break;
            }
            if (slots.Kind == ListKind.Number) {
                string expected = next.ToString(CultureInfo.InvariantCulture);
                if (string.CompareOrdinal(text.Substring(slots.ListStart, slots.NumberLength), expected) != 0) {
                    result.Add(current.Offset + slots.ListStart, slots.NumberLength, expected);
                }
                next++;
            }
            current = current.NextLine;
            scanned++;
        }
    }
    #endregion

    #region Autoformat
    // Autoformat, live surface. The basic view's only text trigger is a committed space
    // (StructuralInput.cs:109 AutoFormatListPrefix): when the space is typed and the whole text
    // before the caret on a plain top-level paragraph is "-", "*", "+" or "N." (1-9 digits, value
    // > 0), the paragraph becomes a list item and the writer emits the canonical form
    // (ListEditing.cs:15 ConvertParagraphToNewList, MarkdownWriter.cs:42): every bullet is written
    // as "- " and an ordered item as "<value>. ". Nothing else is a trigger there - "> " and
    // "- [ ] " stay literal, and an indented marker, a heading, a quote, an existing list item and
    // a table cell all decline, because the marker must be the entire text before the caret.
    //
    // The live surface's authority is the Markdown source, and it already reads "- " / "N. " as a
    // list item, so the port is exactly the writer's canonical form: "*" and "+" become "-", and a
    // typed number with leading zeros ("007.") becomes its value ("7."). Where the basic view would
    // have written what the source already holds ("- ", "1. ") this returns null and the editor
    // inserts the space itself.
    //
    // The space and the marker rewrite are one result, so the host applies them as one undo entry:
    // one Ctrl+Z restores the exact text from before the space.
    internal static CommandResult AutoFormatListMarker(TextDocument document, int selectionStart, int selectionLength) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        if (selectionLength != 0) return null;
        DocumentLine line = document.GetLineByOffset(selectionStart);
        int column = selectionStart - line.Offset;
        // The marker is the whole text before the caret and at most nine digits plus a dot, so
        // ordinary typing inside a sentence costs one range check and one character read.
        if (column < 1 || column > 10) return null;
        char first = document.GetCharAt(line.Offset);
        // '*' and '+' are the bullets the writer does not emit itself; a digit may start an ordered
        // marker. '-' already is canonical and everything else is not a marker (indented markers
        // included, exactly like the basic view).
        if (first != '*' && first != '+' && (first < '0' || first > '9')) return null;

        int replaced;
        string canonical;
        if (column == 1) {
            // One character before the caret can only be a lone bullet; a lone digit is not a
            // marker yet (the dot has not been typed).
            if (first != '*' && first != '+') return null;
            canonical = "-";
            replaced = 1;
        } else {
            if (document.GetCharAt(selectionStart - 1) != '.') return null;
            int digits = column - 1;
            int value = 0;
            for (int i = 0; i < digits; i++) {
                char c = document.GetCharAt(line.Offset + i);
                if (c < '0' || c > '9') return null;
                value = value * 10 + (c - '0');
            }
            if (value < 1) return null;
            canonical = value.ToString(CultureInfo.InvariantCulture);
            // "12." already is the canonical form, so the space would be the whole edit and the
            // editor's own insert is right; "007." is where this rule has to rewrite the digits.
            if (canonical.Length == digits) {
                bool same = true;
                for (int i = 0; i < digits; i++) if (document.GetCharAt(line.Offset + i) != canonical[i]) { same = false; break; }
                if (same) return null;
            }
            replaced = digits;
        }
        // Only a marker that Markdown does not already read as what it wants changes anything, so a
        // "12." line converts nothing and keeps the ordinary insert; the fence and table checks run
        // after that rejection so a normal space inside a sentence never scans either.
        if (IsInsideFence(document, line)) return null;
        if (TableAtCaret(document, selectionStart)) return null;

        CommandResult result = new CommandResult();
        result.Add(line.Offset, replaced, canonical);
        result.Add(selectionStart, 0, " ");
        result.SelectionStart = selectionStart + 1 + canonical.Length - replaced;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region Tab
    static string IndentUnit(string text, int indentEnd) {
        if (indentEnd > 0 && text[0] == '\t') return "\t";
        return "  ";
    }

    static int IndentRemoveLength(string text, int indentEnd) {
        if (indentEnd <= 0) return 0;
        if (text[0] == '\t') return 1;
        int spaces = 0;
        while (spaces < indentEnd && spaces < 2 && text[spaces] == ' ') spaces++;
        return spaces;
    }

    // 11-01 user: one Tab must nest the item and one Shift+Tab must lift it. Two spaces only nest
    // under a "- " item; under "1. " (or "10. ") the child starts at the parent's content column,
    // so a fixed two-space unit took two presses there. The unit is the previous sibling's marker
    // width, and Shift+Tab removes down to the parent's own indent. Only nearby lines are read
    // (NeighbourScan); a key press, not typing, so the short scan is fine.
    const int NeighbourScan = 64;
    static string TabIndentUnit(TextDocument document, DocumentLine line, string text, LineSlots slots) {
        string unit = IndentUnit(text, slots.IndentEnd);
        if (unit != "  " || slots.QuoteEnd > slots.IndentEnd) return unit;
        DocumentLine previous = line.PreviousLine;
        for (int i = 0; previous != null && i < NeighbourScan; i++, previous = previous.PreviousLine) {
            string other = LineText(document, previous);
            if (other.Trim().Length == 0) continue;
            LineSlots slot = ParseSlots(other);
            if (!slot.HasList || slot.QuoteEnd > slot.IndentEnd) return unit;
            if (slot.IndentEnd < slots.IndentEnd) return unit;
            if (slot.IndentEnd > slots.IndentEnd) continue;
            int width = slot.ListEnd - slot.ListStart;
            return width > 2 && width <= 8 ? new string(' ', width) : unit;
        }
        return unit;
    }
    static int TabRemoveLength(TextDocument document, DocumentLine line, string text, LineSlots slots) {
        int remove = IndentRemoveLength(text, slots.IndentEnd);
        if (remove <= 0 || text[0] == '\t') return remove;
        DocumentLine previous = line.PreviousLine;
        for (int i = 0; previous != null && i < NeighbourScan; i++, previous = previous.PreviousLine) {
            string other = LineText(document, previous);
            if (other.Trim().Length == 0) continue;
            LineSlots slot = ParseSlots(other);
            if (!slot.HasList || slot.QuoteEnd > slot.IndentEnd) return remove;
            if (slot.IndentEnd >= slots.IndentEnd) continue;
            int lift = slots.IndentEnd - slot.IndentEnd;
            for (int k = 0; k < lift; k++) if (text[k] != ' ') return remove;
            return lift;
        }
        return remove;
    }

    // One indent unit of a quoted item's own room, the part between the quote prefix and the
    // marker (">   - a" has two spaces there). Tab writes its unit at ListStart, so Shift+Tab
    // removes the same unit from the end of that room; the quote markers and the line's outer
    // indent stay. A line without a quote has no such room and keeps the line-start rule.
    static int QuotedListIndentLength(string text, LineSlots slots) {
        int start = slots.QuoteEnd;
        int end = slots.ListStart;
        if (start >= end) return 0;
        if (text[end - 1] == '\t') return 1;
        int spaces = 0;
        while (spaces < end - start && spaces < 2 && text[end - 1 - spaces] == ' ') spaces++;
        return spaces;
    }

    internal static CommandResult Tab(TextDocument document, int selectionStart, int selectionLength, bool shift) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        // Inside a pipe table Tab/Shift+Tab are cell navigation and never move the table: the
        // table only moves from Ctrl+[ / Ctrl+] and the two tool-row buttons. Leaving the key to
        // the editor was not enough, because the editor then inserts an indent at the line start
        // (which is exactly what moves the table's first line).
        if (selectionLength == 0) {
            CommandResult cell = TableTab(document, selectionStart, shift);
            if (cell != null) return cell;
        } else {
            CommandResult cell = TableTabFromSelection(document, selectionStart, selectionLength, shift);
            if (cell != null) return cell;
        }
        List<DocumentLine> lines = TouchedLines(document, selectionStart, selectionLength);
        if (lines.Count == 0) return null;
        // A fenced block keeps its text literal, like the P4 commands.
        if (TouchesFence(document, lines)) return null;
        for (int i = 0; i < lines.Count; i++) {
            LineSlots slots = ParseSlots(LineText(document, lines[i]));
            if (!slots.HasList) return null;
        }

        CommandResult result = new CommandResult();
        for (int i = 0; i < lines.Count; i++) {
            DocumentLine line = lines[i];
            string text = LineText(document, line);
            LineSlots slots = ParseSlots(text);
            if (!shift) {
                string unit = TabIndentUnit(document, line, text, slots);
                result.Add(line.Offset + slots.ListStart, 0, unit);
            } else if (slots.QuoteEnd > slots.IndentEnd) {
                // A quoted item's indentation lives after the quote prefix, not at column 0.
                // Removing it there is the outdent; the quote (and the indent in front of it)
                // stays, so Shift+Tab undoes what Tab wrote inside the quote.
                int remove = QuotedListIndentLength(text, slots);
                if (remove > 0) result.Add(line.Offset + slots.ListStart - remove, remove, string.Empty);
            } else {
                int remove = TabRemoveLength(document, line, text, slots);
                if (remove > 0) result.Add(line.Offset, remove, string.Empty);
            }
        }
        // J5: the moved items change levels, so the numbered runs they leave and join must stay
        // consecutive. One pass per level reads only that level's sibling runs (bounded by
        // NeighbourLimit); the first item's number is kept and its run counts on from there.
        RenumberTabRuns(document, result, lines, shift);
        // Shift+Tab on lines without indentation still returns a result (no edits, same
        // selection) so the host swallows the key instead of inserting a tab character.
        MapSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }
    // The old and new levels of the moved ordered items, renumbered as consecutive runs with one
    // pass per level. The moved items of one level number as one block in document order, so a
    // multi-line selection indented together keeps 1, 2, 3 instead of counting each line on its
    // own ("1. a / 2. b" used to become "1. / 1."). A bullet or task item keeps its place in the
    // run but does not consume a number (the same rule Enter uses while continuing a list).
    static void RenumberTabRuns(TextDocument document, CommandResult result, List<DocumentLine> touched, bool shift) {
        var skip = new HashSet<int>();
        for (int i = 0; i < touched.Count; i++) skip.Add(touched[i].LineNumber);
        // The prefix each moved line has after the move; a line the key leaves alone is absent.
        var moved = new Dictionary<int, string>();
        for (int i = 0; i < touched.Count; i++) {
            string newPrefix;
            if (!MovedPrefix(document, touched[i], shift, out newPrefix)) continue;
            moved[touched[i].LineNumber] = newPrefix;
        }
        if (moved.Count == 0) return;
        var seen = new HashSet<string>();
        // The old levels lose the items: the first numbered item that stays keeps its number, so
        // the rest of that level stay consecutive (the moved items are skipped, not counted).
        for (int i = 0; i < touched.Count; i++) {
            DocumentLine line = touched[i];
            string text = LineText(document, line);
            LineSlots slots = ParseSlots(text);
            string newPrefix;
            if (!moved.TryGetValue(line.LineNumber, out newPrefix)) continue;
            if (slots.Kind != ListKind.Number) continue;
            List<DocumentLine> oldRun = TabSiblingRun(document, line, text.Substring(0, slots.ListStart), skip);
            if (oldRun.Count == 0 || !seen.Add("old:" + RunKey(oldRun))) continue;
            int oldStart = -1;
            for (int r = 0; r < oldRun.Count && oldStart < 0; r++) {
                LineSlots other = ParseSlots(LineText(document, oldRun[r]));
                if (other.Kind == ListKind.Number) oldStart = NumberOfLine(document, oldRun[r]);
            }
            if (oldStart > 0) RenumberRun(document, result, oldRun, oldStart);
        }
        // The new levels gain them, in document order: a moved block that leads its level restarts
        // at 1 and one that follows an existing numbered item continues that run; a run led by
        // bullets keeps the moved item's own number.
        for (int i = 0; i < touched.Count; i++) {
            DocumentLine line = touched[i];
            LineSlots slots = ParseSlots(LineText(document, line));
            string newPrefix;
            if (!moved.TryGetValue(line.LineNumber, out newPrefix)) continue;
            if (slots.Kind != ListKind.Number) continue;
            List<DocumentLine> newRun = TabRunAfterMove(document, line, newPrefix, moved, skip);
            if (!seen.Add("new:" + RunKey(newRun))) continue;
            int anchorAt = newRun.IndexOf(line);
            int first = 1;
            if (anchorAt > 0) {
                first = -1;
                for (int r = 0; r <= anchorAt && first < 0; r++) {
                    LineSlots other = ParseSlots(LineText(document, newRun[r]));
                    if (other.Kind == ListKind.Number) first = NumberOfLine(document, newRun[r]);
                }
                if (first < 0) first = NumberOfLine(document, line);
            }
            RenumberRun(document, result, newRun, first);
        }
    }
    // One run as a key for the once-per-level rule above: two anchors of the same run name the
    // same line span.
    static string RunKey(List<DocumentLine> run) {
        return run[0].LineNumber.ToString(CultureInfo.InvariantCulture) + "-"
            + run[run.Count - 1].LineNumber.ToString(CultureInfo.InvariantCulture);
    }
    // The line's prefix at its new level, false when the key does not move it (a line without a
    // list marker, or a Shift+Tab whose indent unit is not there).
    static bool MovedPrefix(TextDocument document, DocumentLine line, bool shift, out string newPrefix) {
        newPrefix = null;
        string text = LineText(document, line);
        LineSlots slots = ParseSlots(text);
        if (!slots.HasList) return false;
        string oldPrefix = text.Substring(0, slots.ListStart);
        if (!shift) {
            newPrefix = oldPrefix + TabIndentUnit(document, line, text, slots);
        } else if (slots.QuoteEnd > slots.IndentEnd) {
            int remove = QuotedListIndentLength(text, slots);
            if (remove <= 0 || slots.ListStart - remove < slots.QuoteEnd) return false;
            newPrefix = text.Substring(0, slots.ListStart - remove);
        } else {
            int remove = TabRemoveLength(document, line, text, slots);
            if (remove <= 0 || remove > slots.ListStart) return false;
            newPrefix = text.Substring(remove, slots.ListStart - remove);
        }
        if (string.CompareOrdinal(oldPrefix, newPrefix) == 0) {
            newPrefix = null;
            return false;
        }
        return true;
    }
    // The consecutive items at one level around a moved line, read as if the move had happened:
    // a touched line counts at its new prefix, and a touched line that moves to another level
    // neither joins this run nor ends it (the items that stay become neighbours).
    static List<DocumentLine> TabRunAfterMove(TextDocument document, DocumentLine line, string prefix, Dictionary<int, string> moved, HashSet<int> touched) {
        var up = new List<DocumentLine>();
        int scanned = 0;
        for (DocumentLine current = line.PreviousLine; current != null && scanned < NeighbourLimit; current = current.PreviousLine, scanned++) {
            string text = LineText(document, current);
            LineSlots slots = ParseSlots(text);
            if (!slots.HasList) break;
            string own = EffectivePrefix(text, slots, current, moved);
            if (string.CompareOrdinal(own, prefix) == 0) { up.Insert(0, current); continue; }
            if (touched.Contains(current.LineNumber)) continue;
            // A nested item of an earlier sibling sits between the anchor and that sibling's own
            // level; it neither joins nor ends the run.
            if (own.Length > prefix.Length && own.StartsWith(prefix, StringComparison.Ordinal)) continue;
            break;
        }
        var down = new List<DocumentLine>();
        scanned = 0;
        for (DocumentLine current = line.NextLine; current != null && scanned < NeighbourLimit; current = current.NextLine, scanned++) {
            string text = LineText(document, current);
            LineSlots slots = ParseSlots(text);
            if (!slots.HasList) break;
            string own = EffectivePrefix(text, slots, current, moved);
            if (string.CompareOrdinal(own, prefix) == 0) { down.Add(current); continue; }
            if (touched.Contains(current.LineNumber)) continue;
            if (own.Length > prefix.Length && own.StartsWith(prefix, StringComparison.Ordinal)) continue;
            break;
        }
        var run = new List<DocumentLine>(up);
        run.Add(line);
        run.AddRange(down);
        return run;
    }
    // The prefix a line has after the move: its new one for a moved line, its own otherwise.
    static string EffectivePrefix(string text, LineSlots slots, DocumentLine line, Dictionary<int, string> moved) {
        string value;
        if (moved.TryGetValue(line.LineNumber, out value)) return value;
        return text.Substring(0, slots.ListStart);
    }
    static int NumberOfLine(TextDocument document, DocumentLine line) {
        string text = LineText(document, line);
        LineSlots slots = ParseSlots(text);
        int value = 0;
        if (slots.Kind == ListKind.Number) int.TryParse(text.Substring(slots.ListStart, slots.NumberLength), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        return value > 0 ? value : 1;
    }
    // The list items that stay at one level around `line`, read from the level it is leaving:
    // the anchor and every line in skip (the other moved items) are left out, so the run is what
    // remains there once the move has happened.
    static List<DocumentLine> TabSiblingRun(TextDocument document, DocumentLine line, string prefix, HashSet<int> skip) {
        var up = new List<DocumentLine>();
        int scanned = 0;
        for (DocumentLine current = line.PreviousLine; current != null && scanned < NeighbourLimit; current = current.PreviousLine, scanned++) {
            string text = LineText(document, current);
            LineSlots slots = ParseSlots(text);
            if (!slots.HasList) break;
            if (skip.Contains(current.LineNumber)) continue;
            string own = text.Substring(0, slots.ListStart);
            if (string.CompareOrdinal(own, prefix) != 0) {
                // A nested item of an earlier sibling sits between the anchor and that sibling's
                // own level; it neither joins nor ends the run.
                if (own.Length > prefix.Length && own.StartsWith(prefix, StringComparison.Ordinal)) continue;
                break;
            }
            up.Insert(0, current);
        }
        var down = new List<DocumentLine>();
        scanned = 0;
        for (DocumentLine current = line.NextLine; current != null && scanned < NeighbourLimit; current = current.NextLine, scanned++) {
            string text = LineText(document, current);
            LineSlots slots = ParseSlots(text);
            if (!slots.HasList) break;
            if (skip.Contains(current.LineNumber)) continue;
            string own = text.Substring(0, slots.ListStart);
            if (string.CompareOrdinal(own, prefix) != 0) {
                // A nested item of a later sibling sits between those two items the same way the
                // up scan's does: it neither joins nor ends the run, so the siblings keep
                // numbering past it.
                if (own.Length > prefix.Length && own.StartsWith(prefix, StringComparison.Ordinal)) continue;
                break;
            }
            down.Add(current);
        }
        var run = new List<DocumentLine>(up);
        run.AddRange(down);
        return run;
    }
    static void RenumberRun(TextDocument document, CommandResult result, List<DocumentLine> run, int start) {
        int expected = start;
        for (int i = 0; i < run.Count; i++) {
            string text = LineText(document, run[i]);
            LineSlots slots = ParseSlots(text);
            if (slots.Kind != ListKind.Number) continue;
            int current = 0;
            int.TryParse(text.Substring(slots.ListStart, slots.NumberLength), NumberStyles.None, CultureInfo.InvariantCulture, out current);
            if (current != expected) result.Add(run[i].Offset + slots.ListStart, slots.NumberLength, expected.ToString(CultureInfo.InvariantCulture));
            expected++;
        }
    }
    #endregion

    #region Table indentation
    // A pipe table written inside a list item is just its lines indented by the item's content
    // indent, so outdent/indent are one-unit prefix edits like the list Tab rule. They are NOT
    // bound to Tab/Shift+Tab: inside a table those keys stay cell navigation, so the table move
    // runs from the table tool row (and from any command the host routes here).
    //
    // The row rule, the two-line header rule and the extent cap are MarkdownTableCommands.FindTable's
    // own (that file's MaxExpandLines): the screen, the table commands and these keys must call the
    // same lines a table at any size, so no path decides "not a table" on its own. Rows may carry a
    // list item's content indent; neither rule requires the indent to be absent.

    // The table that owns the offset, as line numbers: the two-line header rule names the first
    // line and the contiguous pipe rows below it are the table, exactly as the renderer and the
    // table commands read it.
    static bool FindTableLines(TextDocument document, int offset, out int firstLine, out int lastLine) {
        bool truncated;
        return FindTableLines(document, offset, out firstLine, out lastLine, out truncated);
    }

    // truncated: an edge scan stopped at the row cap with another row behind it, so the answer
    // cannot name the table's real edges. A caller that cannot decide does nothing - it keeps the
    // table rules or swallows its key - and never falls through to the editor's own edit, which
    // would delete the "|" or write a tab character into the table.
    static bool FindTableLines(TextDocument document, int offset, out int firstLine, out int lastLine, out bool truncated) {
        return MarkdownTableCommands.FindTable(document, offset, out firstLine, out lastLine, out truncated);
    }

    // True when the caret sits in a pipe table (used by the hosts to decide whether the
    // table move applies before they touch the document).
    internal static bool TableAtCaret(TextDocument document, int offset) {
        int firstLine;
        int lastLine;
        return FindTableLines(document, offset, out firstLine, out lastLine);
    }

    // Tab/Shift+Tab inside a table are cell navigation: move to the next/previous cell and select
    // its content, so typing replaces the cell and repeated Tab walks the table. A caret in the
    // indentation before the first pipe, on a pipe, or anywhere else on a table line enters at the
    // first cell. The one edit is Tab in the last cell of the table's last row, which appends an
    // empty row (MarkdownTableCommands.AddRowAtEnd, the row AddRowBelow writes); every other key
    // only moves the selection, so the table's shape cannot change from these keys. The table only
    // moves from Ctrl+[ / Ctrl+] and the tool row. At the table's start Shift+Tab has no cell in
    // that direction: the key stays where its anchor is, and a null answer would let the editor's
    // TabForward insert a tab or replace text. A pipe row whose table lies past the extent cap is
    // swallowed for the same reason: a null there would reach that editor edit too.
    static CommandResult TableTab(TextDocument document, int selectionStart, bool shift) {
        DocumentLine line = document.GetLineByOffset(selectionStart);
        // A line inside a fence is literal text, never a row the renderer draws, so Tab keeps
        // its ordinary meaning there.
        if (IsInsideFence(document, line)) return null;
        int firstLine;
        int lastLine;
        bool truncated;
        bool found = FindTableLines(document, selectionStart, out firstLine, out lastLine, out truncated);
        if (truncated) return TableStay(selectionStart);
        if (!found) return null;
        return TableCellMove(document, firstLine, lastLine, line, selectionStart - line.Offset, shift, selectionStart, true);
    }

    // The move itself for one anchor (the caret, or a selection's edge): the column is the
    // anchor's own column and stayAt is where the key rests when no cell lies in that direction.
    // allowAppend is the Tab-in-the-last-cell rule: only the caret or a selection inside the
    // anchor row's last cell may add the row, so an append can never take a selection that
    // crossed a "|" with it.
    static CommandResult TableCellMove(TextDocument document, int firstLine, int lastLine, DocumentLine line, int caret, bool shift, int stayAt, bool allowAppend) {
        string text = LineText(document, line);
        List<int[]> cells = RowCells(text);
        if (cells.Count == 0) return TableStay(stayAt);
        int column = CellSpanAt(cells, caret);
        int targetLine = line.LineNumber;
        int targetColumn;
        if (column < 0) {
            // Outside the brackets (the indentation before the leading pipe, a separator pipe,
            // or past the row's last cell): Tab takes the first cell starting at or after the
            // caret and Shift+Tab the last one before it, so the key never moves backwards on
            // its own.
            targetColumn = CellAtOrAfter(cells, caret);
            if (shift) targetColumn = CellBefore(cells, caret);
            if (targetColumn < 0) return TableStay(stayAt);
        } else {
            targetColumn = shift ? column - 1 : column + 1;
        }
        if (targetColumn < 0) {
            targetLine = PreviousCellRow(document, firstLine, line.LineNumber, out targetColumn);
            if (targetLine <= 0) return TableStay(stayAt);
        } else if (targetColumn >= cells.Count) {
            // Past the row's last cell the next cell is the next row's first one; the delimiter
            // row is structure and never a target. At the table's end there is no next row: Tab
            // appends one empty row and puts the caret in its first cell (Word/Typora), while an
            // append the table rules refuse stays where the key started. A Shift+Tab move never
            // reaches this branch (its target is a cell on the same row or above).
            targetLine = NextCellRow(document, firstLine, lastLine, line.LineNumber, out targetColumn);
            if (targetLine <= 0) {
                if (!shift && allowAppend) {
                    CommandResult added = MarkdownTableCommands.AddRowAtEnd(document, line.Offset);
                    if (added != null) return added;
                }
                return TableStay(stayAt);
            }
        }
        DocumentLine target = document.GetLineByNumber(targetLine);
        string targetText = LineText(document, target);
        List<int[]> targetCells = RowCells(targetText);
        if (targetColumn < 0 || targetColumn >= targetCells.Count) return TableStay(stayAt);
        int[] span = CellContent(targetText, targetCells[targetColumn]);
        CommandResult result = new CommandResult();
        result.SelectionStart = target.Offset + span[0];
        result.SelectionLength = Math.Max(0, span[1] - span[0]);
        return result;
    }

    // A selection inside a table is navigation too, never a text edit: the editor's own Tab
    // replaces the selected text (and the "|" in it) with one tab, which ends the table. Tab
    // collapses onto the cell after the selection end and Shift+Tab onto the cell before the
    // selection start. Both moves, and the row append at the table's end, belong to a selection
    // that lies inside one table: one that starts or ends outside it (a paragraph above, a line
    // below) is swallowed - the key writes nothing and only collapses - so a selection that
    // crossed the table from outside never edits it. The append itself happens only for the caret
    // or a selection inside that row's last cell; Shift+Tab, an append the table rules refuse and
    // an edge the extent cap cannot place rest on the key's own anchor (the selection end for
    // Tab, the start for Shift+Tab) and write nothing.
    static CommandResult TableTabFromSelection(TextDocument document, int selectionStart, int selectionLength, bool shift) {
        int selectionEnd = selectionStart + selectionLength;
        int anchor = shift ? selectionStart : selectionEnd;
        DocumentLine anchorLine = document.GetLineByOffset(anchor);
        // A fenced block is literal text: its lines are never table rows, so the selection takes
        // the ordinary line rule (the fence guard in Tab answers null for it).
        if (IsInsideFence(document, anchorLine)) return null;
        int firstLine;
        int lastLine;
        bool truncated;
        if (FindTableLines(document, anchor, out firstLine, out lastLine, out truncated)) {
            if (truncated) return TableStay(anchor);
            DocumentLine first = document.GetLineByNumber(firstLine);
            DocumentLine last = document.GetLineByNumber(lastLine);
            if (selectionStart < first.Offset || selectionEnd > last.EndOffset) return TableStay(anchor);
            // The cell move answers the direction; at the table's end Tab appends a row and
            // Shift+Tab, or a Tab whose append was refused, rests on its own anchor (the
            // selection end for Tab and the selection start for Shift+Tab, like StayTab).
            bool append = AppendAllowed(document, anchorLine, selectionStart, selectionEnd);
            return TableCellMove(document, firstLine, lastLine, anchorLine, anchor - anchorLine.Offset, shift, anchor, append);
        }
        if (truncated) return TableStay(anchor);
        // The anchor is outside a table, but a table elsewhere in the selection is still
        // structure: the key writes nothing and only collapses the selection.
        if (!SelectionCoversTable(document, selectionStart, selectionLength)) return null;
        return TableStay(anchor);
    }

    // The caret, or a selection that lies inside one cell, is the "Tab at the end of the last
    // cell" the append belongs to. A selection that reaches into an earlier cell of the row has
    // crossed a "|", so adding a row for it would change the table from a selection that also
    // holds ordinary text: that key only collapses.
    static bool AppendAllowed(TextDocument document, DocumentLine anchorLine, int selectionStart, int selectionEnd) {
        if (selectionStart >= selectionEnd) return true;
        List<int[]> cells = RowCells(LineText(document, anchorLine));
        if (cells.Count == 0) return false;
        int[] last = cells[cells.Count - 1];
        int from = selectionStart - anchorLine.Offset;
        int to = selectionEnd - anchorLine.Offset;
        return from >= last[0] && to <= last[1];
    }

    // True when a table lies inside the selection even though both endpoints are outside it.
    // The two endpoints are answered by the table rule itself, so a selection that starts in the
    // middle of a table's row still covers that table; between them only a header line has to be
    // found, because every row of a table sits at or below its header. That is two line reads per
    // line and no table scan. A selection longer than SelectionScanLimit lines, and an edge the
    // extent cap cannot place, are reported as covering a table: the key is then swallowed
    // instead of reaching the editor's own TabForward, which is the safe answer when the scan
    // cannot decide.
    static bool SelectionCoversTable(TextDocument document, int selectionStart, int selectionLength) {
        int selectionEnd = selectionStart + selectionLength;
        int firstLine;
        int lastLine;
        bool truncated;
        bool found = FindTableLines(document, selectionStart, out firstLine, out lastLine, out truncated);
        if (found || truncated) return true;
        if (selectionEnd > selectionStart) {
            found = FindTableLines(document, selectionEnd - 1, out firstLine, out lastLine, out truncated);
            if (found || truncated) return true;
        }
        DocumentLine first = document.GetLineByOffset(selectionStart);
        DocumentLine last = document.GetLineByOffset(Math.Max(selectionStart, selectionEnd - 1));
        int scanned = 0;
        for (DocumentLine current = first.NextLine; current != null && current.LineNumber < last.LineNumber; current = current.NextLine) {
            if (scanned >= SelectionScanLimit) return true;
            scanned++;
            if (MarkdownTableCommands.IsHeaderRow(document, current)) return true;
        }
        return false;
    }

    // No cell lies past the table's last cell (Shift+Tab at an end, or a Tab whose append was
    // refused): the key stays at the caret with no edits, so the editor's own TabForward never
    // inserts a tab or replaces the cell the previous Tab selected.
    static CommandResult TableStay(int caret) {
        return StayAt(caret);
    }

    // Column whose cell bracket holds the column, or -1 when the caret is outside every cell
    // (the indentation before the leading pipe, or on a pipe).
    static int CellSpanAt(List<int[]> cells, int column) {
        for (int i = 0; i < cells.Count; i++) if (column >= cells[i][0] && column <= cells[i][1]) return i;
        return -1;
    }

    // First cell that starts at or after the caret, or -1.
    static int CellAtOrAfter(List<int[]> cells, int column) {
        for (int i = 0; i < cells.Count; i++) if (cells[i][0] >= column) return i;
        return -1;
    }

    // Last cell that ends before the caret, or -1.
    static int CellBefore(List<int[]> cells, int column) {
        for (int i = cells.Count - 1; i >= 0; i--) if (cells[i][1] <= column) return i;
        return -1;
    }

    // Last cell of the closest row above that has cells. The delimiter row is structure and is
    // stepped over, never selected; returns 0 when there is none in this table.
    static int PreviousCellRow(TextDocument document, int firstLine, int fromLine, out int column) {
        column = 0;
        DocumentLine current = document.GetLineByNumber(fromLine).PreviousLine;
        while (current != null && current.LineNumber >= firstLine) {
            if (current.LineNumber == firstLine + 1) { current = current.PreviousLine; continue; }
            List<int[]> cells = RowCells(LineText(document, current));
            if (cells.Count > 0) { column = cells.Count - 1; return current.LineNumber; }
            current = current.PreviousLine;
        }
        return 0;
    }

    // First cell of the closest row below that has cells. The delimiter row is structure and is
    // stepped over, never selected; returns 0 when there is none in this table.
    static int NextCellRow(TextDocument document, int firstLine, int lastLine, int fromLine, out int column) {
        column = 0;
        DocumentLine current = document.GetLineByNumber(fromLine).NextLine;
        while (current != null && current.LineNumber <= lastLine) {
            if (current.LineNumber == firstLine + 1) { current = current.NextLine; continue; }
            List<int[]> cells = RowCells(LineText(document, current));
            if (cells.Count > 0) return current.LineNumber;
            current = current.NextLine;
        }
        return 0;
    }

    // Cell brackets of one row, as the renderer and MarkdownTableCommands read the same row:
    // MarkdownLivePreview.SplitCells (which MarkdownTableCommands.CellsAt also uses) names the
    // cells, so a pipe escaped as "\|" and a pipe inside a `code` span are cell text and a row's
    // leading or trailing pipe owns no cell. Around each cell only its padding and the one
    // separator pipe are widened back into the bracket, so the gap scan needs no pipe rules of
    // its own: a caret in a cell's padding stays that cell's (the bracket begins after the pipe
    // in front of it and ends at the pipe behind it), and a trailing pipe leaves no empty slot.
    static List<int[]> RowCells(string text) {
        List<int[]> cells = new List<int[]>();
        List<MarkdownLivePreview.Cell> spans = MarkdownLivePreview.SplitCells(text);
        for (int i = 0; i < spans.Count; i++) {
            int from = spans[i].Start;
            while (from > 0 && (text[from - 1] == ' ' || text[from - 1] == '\t')) from--;
            while (from > 0 && text[from - 1] != '|') from--;
            int end = spans[i].End;
            while (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
            cells.Add(new int[] { from, end });
        }
        return cells;
    }

    // Cell content span (trimmed of the padding spaces) used as the new selection. A cell that
    // holds nothing but padding answers its own caret stop instead (MarkdownTableCommands
    // .EmptyCellStop), so the next typed character lands between the cell's two padding spaces
    // ("|  |" becomes "| x |"), the same spot an add-row or a click leaves the caret on.
    static int[] CellContent(string text, int[] cell) {
        int[] span = TrimSpan(text, cell[0], cell[1]);
        if (span[0] != span[1]) return span;
        int stop = MarkdownTableCommands.EmptyCellStop(text, cell[0], cell[1]);
        return new int[] { stop, stop };
    }

    static int[] TrimSpan(string text, int start, int end) {
        while (start < end && (text[start] == ' ' || text[start] == '\t')) start++;
        while (end > start && (text[end - 1] == ' ' || text[end - 1] == '\t')) end--;
        return new int[] { start, end };
    }

    // One indent unit off every line: a table inside a list item loses the item's continuation
    // indent, so the list ends there and the table becomes a top-level block. The item's own
    // blank continuation line right under the table goes with it (that is what "ends the list"
    // means in the source), so outdent and indent stay exact inverses. Lines with no indent are
    // left alone; a table that is not indented is not moved (null), so the host can leave the
    // key/button to its other meaning.
    internal static CommandResult OutdentTable(TextDocument document, int selectionStart, int selectionLength) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        int firstLine;
        int lastLine;
        if (!FindTableLines(document, selectionStart, out firstLine, out lastLine)) return null;
        if (TouchesFenceAt(document, firstLine)) return null;
        CommandResult result = new CommandResult();
        for (int number = firstLine; number <= lastLine; number++) {
            DocumentLine line = document.GetLineByNumber(number);
            string text = LineText(document, line);
            int remove = IndentRemoveLength(text, LeadingIndent(text));
            if (remove > 0) result.Add(line.Offset, remove, string.Empty);
        }
        if (result.Edits.Count == 0) return null;
        // The item's own continuation line right under the table ends the list with it, so the
        // outdent and the indent stay exact inverses (one blank line, not a whole run).
        DocumentLine next = document.GetLineByNumber(lastLine).NextLine;
        if (next != null && IsBlankText(LineText(document, next))) {
            string text = LineText(document, next);
            int remove = IndentRemoveLength(text, LeadingIndent(text));
            if (remove > 0) result.Add(next.Offset, remove, string.Empty);
        }
        MapSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }

    // One indent unit before every line, so the table joins the list item directly above it.
    // Without a list directly above there is nothing to indent under, so the command declines.
    // A table that already sits at the level of that item's content indent has nowhere deeper to
    // go: a second unit would turn it into an indented code block, so this is a no-op (the basic
    // view's RichTableIndent declines for the same reason). A blank line directly under the table
    // joins the item as well, which keeps the outdent and the indent exact inverses of each other.
    internal static CommandResult IndentTable(TextDocument document, int selectionStart, int selectionLength) {
        if (!Validate(document, selectionStart, selectionLength)) return null;
        int firstLine;
        int lastLine;
        if (!FindTableLines(document, selectionStart, out firstLine, out lastLine)) return null;
        if (TouchesFenceAt(document, firstLine)) return null;
        DocumentLine above = AboveTable(document, firstLine);
        if (above == null || !ParseSlots(LineText(document, above)).HasList) return null;
        // The deepest indent a list item above can own: its own list indentation plus one unit
        // (the item's content indent). A table already there is inside the item; a second unit
        // would turn it into an indented code block, so the command declines.
        string aboveText = LineText(document, above);
        LineSlots aboveSlots = ParseSlots(aboveText);
        int itemIndent = aboveSlots.ListStart;
        int contentIndent = itemIndent + IndentUnit(aboveText, aboveSlots.IndentEnd).Length;
        if (LeadingIndent(LineText(document, document.GetLineByNumber(firstLine))) >= contentIndent) return null;
        CommandResult result = new CommandResult();
        for (int number = firstLine; number <= lastLine; number++) {
            DocumentLine line = document.GetLineByNumber(number);
            string text = LineText(document, line);
            result.Add(line.Offset, 0, IndentUnit(text, LeadingIndent(text)));
        }
        // The item's own blank continuation line under the table joins it too, so indent is an
        // exact inverse of outdent. A completely empty line stays empty: indenting it only left
        // trailing whitespace behind (the outdent then removed it again), and an empty line is a
        // continuation line of the item either way. The document's own final empty line (the
        // newline that ends the last table row) keeps no indent either.
        DocumentLine next = document.GetLineByNumber(lastLine).NextLine;
        if (next != null && next.Length > 0 && IsBlankText(LineText(document, next)) && LeadingIndent(LineText(document, next)) == 0) {
            result.Add(next.Offset, 0, IndentUnit(LineText(document, next), 0));
        }
        MapSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }

    static bool IsBlankText(string text) {
        for (int i = 0; i < text.Length; i++) if (text[i] != ' ' && text[i] != '\t') return false;
        return true;
    }

    static int LeadingIndent(string text) {
        int i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i;
    }

    // Nearest non-blank line above the table, at most NeighbourLimit lines away.
    static DocumentLine AboveTable(TextDocument document, int firstLine) {
        DocumentLine previous = document.GetLineByNumber(firstLine).PreviousLine;
        int scanned = 0;
        while (previous != null && scanned < NeighbourLimit) {
            if (LineText(document, previous).Trim().Length > 0) return previous;
            previous = previous.PreviousLine;
            scanned++;
        }
        return null;
    }

    // A pipe row inside a fenced block is literal text, not a table.
    static bool TouchesFenceAt(TextDocument document, int lineNumber) {
        return IsInsideFence(document, document.GetLineByNumber(lineNumber));
    }
    #endregion

    #region Backspace
    static bool FindQuoteLevelEnd(string text, LineSlots slots, int column, out int levelStart, out int levelEnd) {
        levelStart = 0;
        levelEnd = 0;
        int i = slots.IndentEnd;
        while (i < slots.QuoteEnd) {
            int start = i;
            i++;
            if (i < slots.QuoteEnd && IsSpaceOrTab(text[i])) i++;
            if (i == column) {
                levelStart = start;
                levelEnd = i;
                return true;
            }
        }
        return false;
    }

    internal static CommandResult Backspace(TextDocument document, int caretOffset) {
        if (!Validate(document, caretOffset, 0)) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        int column = caretOffset - line.Offset;
        string text = LineText(document, line);
        // F1: a fence line is structure, like a table row. Backspace at its edge joins it with a
        // neighbour or shortens its marker run, and either changes what the block covers: "```"
        // merged with the first code line turns that line into the fence's info string, and a
        // merged closing fence lets the block swallow the rest of the document.
        if (FenceBoundaryBackspace(document, line, text, column)) return StayAt(caretOffset);
        char fenceChar;
        int fenceLength;
        if (TryFenceMark(text, out fenceChar, out fenceLength) || IsInsideFence(document, line)) return null;
        LineSlots slots = ParseSlots(text);

        // B1: right after the marker (or task box). The indentation stays.
        if (slots.HasList && column == slots.ListEnd) {
            CommandResult result = new CommandResult();
            result.Add(line.Offset + slots.ListStart, slots.ListEnd - slots.ListStart, string.Empty);
            result.SelectionStart = line.Offset + slots.ListStart;
            result.SelectionLength = 0;
            return result;
        }

        // B2: right after the indentation of a line without a list marker.
        if (!slots.HasList && column == slots.IndentEnd && slots.IndentEnd > 0) {
            int remove = IndentRemoveLength(text, slots.IndentEnd);
            if (remove <= 0) return null;
            CommandResult result = new CommandResult();
            result.Add(line.Offset, remove, string.Empty);
            result.SelectionStart = caretOffset - remove;
            result.SelectionLength = 0;
            return result;
        }

        // B3: right after one quote level.
        int levelStart;
        int levelEnd;
        if (FindQuoteLevelEnd(text, slots, column, out levelStart, out levelEnd)) {
            CommandResult result = new CommandResult();
            result.Add(line.Offset + levelStart, levelEnd - levelStart, string.Empty);
            result.SelectionStart = line.Offset + levelStart;
            result.SelectionLength = 0;
            return result;
        }
        return null;
    }

    // True when Backspace at this column would join a fence line with the line next to it, or
    // delete one of the fence's own marker characters. A blank line may still be joined: the
    // fence line keeps its text and its tail, so the block does not change.
    static bool FenceBoundaryBackspace(TextDocument document, DocumentLine line, string text, int column) {
        if (column == 0) {
            DocumentLine previous = line.PreviousLine;
            if (previous == null) return false;
            // Joining text into a fence line puts it after the marker run: a closing fence stops
            // closing and an opening fence turns the first code line into its info string.
            if (IsFenceStartLine(document, previous)) return HasVisibleChar(text);
            // A fence line needs its run at the start of the line; joining it onto a non-empty
            // line moves the run into the middle. An empty line before it adds nothing, so that
            // blank line can still be joined away.
            if (IsFenceStartLine(document, line)) return previous.Length > 0;
            return false;
        }
        char character;
        int length;
        if (!TryFenceMark(text, out character, out length)) return false;
        int runStart = 0;
        while (runStart < text.Length && text[runStart] == ' ') runStart++;
        // Backspace deletes the character just before the caret; a marker character there would
        // shorten the run that makes the line a fence.
        return column > runStart && column <= runStart + length;
    }

    // The fence grammar read from a line's first characters only (up to three spaces, then three
    // or more '`' or '~'): a Backspace at column 0 must stay cheap even next to a very long line.
    static bool IsFenceStartLine(TextDocument document, DocumentLine line) {
        int at = line.Offset;
        int end = line.EndOffset;
        int spaces = 0;
        while (at < end && spaces < 3 && document.GetCharAt(at) == ' ') { at++; spaces++; }
        if (at >= end) return false;
        char c = document.GetCharAt(at);
        if (c != '`' && c != '~') return false;
        int count = 0;
        while (at < end && count < 3 && document.GetCharAt(at) == c) { at++; count++; }
        return count >= 3;
    }

    static bool HasVisibleChar(string text) {
        for (int i = 0; i < text.Length; i++) if (text[i] != ' ' && text[i] != '\t') return true;
        return false;
    }

    // F2 (Delete): true when forward-deleting at this caret would join a fence line with the next
    // one or remove one of its marker characters - the other direction of the Backspace rule
    // above. The host's Delete key asks this and swallows the key. Only the caret line and its
    // neighbour are read, and a long marker run is read no further than the answer needs.
    internal static bool DeleteAtFenceBoundary(TextDocument document, int caretOffset) {
        if (document == null || caretOffset < 0 || caretOffset > document.TextLength) return false;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        int column = caretOffset - line.Offset;
        int start;
        int end;
        if (FenceRun(document, line, column, out start, out end)) {
            if (column >= start && column < end) return true;
            if (line.NextLine != null && column >= line.Length) return true;
            return false;
        }
        DocumentLine next = line.NextLine;
        return next != null && column >= line.Length && FenceRun(document, next, 0, out start, out end);
    }

    // A fence line's leading marker run as line columns [start, end). The run is read only as far
    // as the answer needs it: three markers prove the line is a fence, and one marker past
    // `column` proves the caret is inside the run.
    static bool FenceRun(TextDocument document, DocumentLine line, int column, out int start, out int end) {
        start = -1;
        end = -1;
        int at = line.Offset;
        int spaces = 0;
        while (at < line.EndOffset && spaces < 3 && document.GetCharAt(at) == ' ') { at++; spaces++; }
        if (at >= line.EndOffset) return false;
        char c = document.GetCharAt(at);
        if (c != '`' && c != '~') return false;
        int run = 0;
        int limit = Math.Max(3, column - (at - line.Offset) + 1);
        while (at < line.EndOffset && run < limit && document.GetCharAt(at) == c) { at++; run++; }
        if (run < 3) return false;
        start = at - run - line.Offset;
        end = at - line.Offset;
        return true;
    }

    // A no-edit result that keeps the caret: the host swallows the key instead of editing.
    static CommandResult StayAt(int caret) {
        CommandResult stay = new CommandResult();
        stay.SelectionStart = caret;
        stay.SelectionLength = 0;
        return stay;
    }
    #endregion
}
}
