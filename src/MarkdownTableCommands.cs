using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// GFM pipe-table commands as pure source transformations over the caret's table. Each
// command reads only the table lines (the scan stops at the table's own edges), returns edits
// in pre-edit offsets and never mutates the document; the host applies the edits as one undo
// step. Cell boundaries honour "\|" escapes and `code` spans. New cells are three spaces wide.
// Indent/outdent move the whole table in or out of the list item above it (see TableLevel).
internal static class MarkdownTableCommands {
    // The two edge scans stop at the first non-row line, so this cap only guards a pathological
    // document. It is far above any real table and matches the renderer's rule, which carries
    // TableStart for every pipe row and has no row limit (MarkdownLivePreview block state): a
    // table the screen draws is always a table for these commands, however many rows it has.
    // The structural keys (MarkdownStructuralInput) read a table's extent through FindTable as
    // well, so every path that calls a line a row - or not one - uses this one cap.
    internal const int MaxExpandLines = 200000;
    const string NewCell = "   ";
    const string NewDelimiterCell = " --- ";

    // One row split into cell content spans. Starts/Ends exclude the surrounding pipes;
    // Pipes[i] is the pipe that ends cell i, or -1 when the row ends after that cell.
    sealed class RowCells {
        internal readonly List<int> Starts = new List<int>();
        internal readonly List<int> Ends = new List<int>();
        internal readonly List<int> Pipes = new List<int>();
        internal int Count { get { return Starts.Count; } }
    }

    #region FindTable
    // The caret line is a table row when it holds a pipe that is not escaped and not inside a
    // `code` span, which is the renderer's own row rule (MarkdownLivePreview.HasTablePipe): GFM
    // tables do not need the outer pipes, so "Name | Age" / "--- | ---" is the same table the
    // screen draws, and every command above must read it as one. The table starts at the first
    // line of the pipe run whose next line is a delimiter row with the same cell count (the
    // renderer's NextIsDelimiter); a pipe paragraph directly above a table is not part of it.
    internal static bool FindTable(TextDocument document, int offset, out int firstLine, out int lastLine) {
        bool truncated;
        return FindTable(document, offset, out firstLine, out lastLine, out truncated);
    }

    // truncated: an edge scan stopped at MaxExpandLines with another row behind it, so firstLine
    // and lastLine (and a false answer) cannot be trusted as the table's real edges. A caller
    // that must not guess keeps the table rules, or swallows its key, instead of falling through
    // to a raw text edit that would delete a "|" or insert a tab character.
    internal static bool FindTable(TextDocument document, int offset, out int firstLine, out int lastLine, out bool truncated) {
        firstLine = 0;
        lastLine = 0;
        truncated = false;
        if (document == null || offset < 0 || offset > document.TextLength) return false;
        DocumentLine line = document.GetLineByOffset(offset);
        if (!IsTableRow(LineText(document, line))) return false;
        DocumentLine first = line;
        DocumentLine last = line;
        int scanned = 0;
        while (scanned < MaxExpandLines) {
            DocumentLine previous = first.PreviousLine;
            if (previous == null || !IsTableRow(LineText(document, previous))) break;
            first = previous;
            scanned++;
        }
        if (scanned >= MaxExpandLines) {
            DocumentLine beyond = first.PreviousLine;
            truncated = beyond != null && IsTableRow(LineText(document, beyond));
        }
        scanned = 0;
        while (scanned < MaxExpandLines) {
            DocumentLine next = last.NextLine;
            if (next == null || !IsTableRow(LineText(document, next))) break;
            last = next;
            scanned++;
        }
        if (scanned >= MaxExpandLines) {
            DocumentLine beyond = last.NextLine;
            if (beyond != null && IsTableRow(LineText(document, beyond))) truncated = true;
        }
        DocumentLine header = TableHeader(document, first, line);
        if (header == null) return false;
        firstLine = header.LineNumber;
        lastLine = last.LineNumber;
        return true;
    }

    // The header of the table that owns `caret`, or null when no header lies at or above it (the
    // caret line then only carries pipes and is not a table row to the renderer).
    static DocumentLine TableHeader(TextDocument document, DocumentLine first, DocumentLine caret) {
        for (DocumentLine current = first; current != null; current = current.NextLine) {
            if (IsHeaderRow(document, current)) return current;
            if (current == caret) break;
        }
        return null;
    }

    // Header + delimiter: the next line is a delimiter row with the same number of cells, the
    // two-line rule the renderer uses to open a table. MarkdownStructuralInput's selection scan
    // asks the same question, so the keys and the commands cannot open a table at different lines.
    internal static bool IsHeaderRow(TextDocument document, DocumentLine header) {
        DocumentLine next = header.NextLine;
        if (next == null) return false;
        if (!IsDelimiterRow(document, next)) return false;
        return CellCount(LineText(document, header)) == CellCount(LineText(document, next));
    }

    // Cell count as the renderer splits a row (pipes inside `code` and escaped pipes are text).
    internal static int CellCount(string text) {
        return SplitRow(text).Count;
    }

    // A pipe outside an escaped pair and outside a `code` span; the renderer's HasTablePipe rule,
    // shared with MarkdownStructuralInput so the keys see exactly the rows the screen draws.
    internal static bool HasTablePipe(string text) {
        // Most lines have no pipe at all; one vectorized index check keeps the escape/code scan
        // off the boundary lines of every table lookup.
        if (text.IndexOf('|') < 0) return false;
        int i = 0;
        while (i < text.Length) {
            char c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '`') {
                int end = CodeSpanEnd(text, i);
                if (end > i) { i = end; continue; }
                while (i < text.Length && text[i] == '`') i++;
                continue;
            }
            if (c == '|') return true;
            i++;
        }
        return false;
    }

    static bool IsTableRow(string text) {
        return HasTablePipe(text);
    }

    static bool IsDelimiterRow(TextDocument document, DocumentLine line) {
        if (line == null) return false;
        string text = LineText(document, line);
        RowCells cells = SplitRow(text);
        if (cells.Count < 1) return false;
        for (int i = 0; i < cells.Count; i++) {
            int length = cells.Ends[i] - cells.Starts[i];
            if (!IsDelimiterCell(text.Substring(cells.Starts[i], length))) return false;
        }
        return true;
    }

    // One delimiter cell: optional spaces, an optional leading colon, one or more dashes,
    // an optional trailing colon, optional spaces.
    static bool IsDelimiterCell(string cell) {
        int start = 0;
        int end = cell.Length;
        while (start < end && char.IsWhiteSpace(cell[start])) start++;
        while (end > start && char.IsWhiteSpace(cell[end - 1])) end--;
        if (start >= end) return false;
        if (cell[start] == ':') start++;
        if (end > start && cell[end - 1] == ':') end--;
        if (start >= end) return false;
        for (int i = start; i < end; i++) if (cell[i] != '-') return false;
        return true;
    }
    #endregion

    #region CellIndexAt
    // 0-based column of the caret: unescaped pipes before the caret minus the leading pipe,
    // ignoring pipes inside `code` spans and clamped to the line's cell count.
    internal static int CellIndexAt(TextDocument document, int offset) {
        if (document == null || offset < 0 || offset > document.TextLength) return 0;
        DocumentLine line = document.GetLineByOffset(offset);
        string text = LineText(document, line);
        int caret = offset - line.Offset;
        if (caret > text.Length) caret = text.Length;
        int[] pipes = PipePositions(text);
        int index = 0;
        for (int i = 0; i < pipes.Length; i++) if (pipes[i] < caret) index++;
        if (index > 0 && IsLeadingPipe(text, pipes[0])) index--;
        if (index < 0) index = 0;
        int count = SplitRow(text).Count;
        if (count < 1) return 0;
        if (index > count - 1) index = count - 1;
        return index;
    }
    #endregion

    #region Cells
    static string LineText(TextDocument document, DocumentLine line) {
        return document.GetText(line.Offset, line.Length);
    }

    static int[] PipePositions(string text) {
        List<int> pipes = new List<int>();
        int i = 0;
        while (i < text.Length) {
            char c = text[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '`') {
                int end = CodeSpanEnd(text, i);
                if (end > i) { i = end; continue; }
                while (i < text.Length && text[i] == '`') i++;
                continue;
            }
            if (c == '|') pipes.Add(i);
            i++;
        }
        return pipes.ToArray();
    }

    // True when the pipe at index is a column separator: the renderer's own list (TablePipes),
    // where an escaped "\|" and a pipe inside a `code` span are cell text, not separators.
    static bool IsSeparatorPipe(string text, int index) {
        int[] pipes = PipePositions(text);
        for (int i = 0; i < pipes.Length; i++) if (pipes[i] == index) return true;
        return false;
    }

    // Index after the run that closes the backtick span opening at start, or start when the
    // run has no matching close (GFM treats an unmatched run as literal text).
    static int CodeSpanEnd(string text, int start) {
        int i = start;
        while (i < text.Length && text[i] == '`') i++;
        int run = i - start;
        int j = i;
        while (j < text.Length) {
            if (text[j] != '`') { j++; continue; }
            int close = j;
            while (j < text.Length && text[j] == '`') j++;
            if (j - close == run) return j;
        }
        return start;
    }

    static RowCells SplitRow(string text) {
        RowCells cells = new RowCells();
        int[] pipes = PipePositions(text);
        if (pipes.Length == 0) return cells;
        int firstPipe = 0;
        int content = 0;
        if (IsLeadingPipe(text, pipes[0])) {
            firstPipe = 1;
            content = pipes[0] + 1;
        }
        for (int i = firstPipe; i < pipes.Length; i++) {
            cells.Starts.Add(content);
            cells.Ends.Add(pipes[i]);
            cells.Pipes.Add(pipes[i]);
            content = pipes[i] + 1;
        }
        if (content < text.Length && text.Substring(content).Trim().Length > 0) {
            cells.Starts.Add(content);
            cells.Ends.Add(text.Length);
            cells.Pipes.Add(-1);
        }
        return cells;
    }

    static bool IsLeadingPipe(string text, int index) {
        for (int i = 0; i < index; i++) if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }

    // Caret one space inside the cell so typing lands on the cell text; empty cells and
    // short rows keep the caret inside the row.
    static int CellCaret(RowCells cells, int column) {
        if (cells.Count == 0) return 0;
        if (column < 0) column = 0;
        if (column >= cells.Count) column = cells.Count - 1;
        int start = cells.Starts[column];
        int end = cells.Ends[column];
        int caret = start + 1;
        if (caret > end) caret = end;
        return caret;
    }

    // The caret stop of a cell that holds nothing but padding: one space in from the pipe that
    // closes it, so a typed character lands between the cell's two padding spaces ("|  |" becomes
    // "| x |"). A cell narrower than two keeps its end, because there is no room for a character to
    // leave a space on both sides; a cell with text never asks this. The stop is a legal caret stop
    // in both views: MarkdownLivePreview.HiddenSnap keeps it and ClampTableRowOffset accepts it,
    // MarkdownStructuralInput.CellContent selects it for Tab, and every reader that asks which cell
    // a caret is in counts a cell's padding as that cell (MarkdownTableCommands.CellIndexAtPadding).
    internal static int EmptyCellStop(string text, int start, int end) {
        if (end - start < 2) return end;
        for (int i = start; i < end; i++) if (text[i] != ' ' && text[i] != '\t') return end;
        return end - 1;
    }

    // The same stop read from the row text alone, for a caller that has the renderer's cell list:
    // its padding-only cell is a trimmed empty span at the cell's end, so the opening pipe is found
    // by the backward scan instead of a start offset. Reserved for cells a caller has already found
    // padding-only (Start == End); a text cell must not reach it.
    internal static int EmptyCellStopBefore(string text, int cellEnd) {
        if (text == null || cellEnd < 0 || cellEnd > text.Length) return cellEnd;
        int open = cellEnd - 1;
        while (open >= 0 && (text[open] == ' ' || text[open] == '\t')) open--;
        if (open < 0 || text[open] != '|') return cellEnd;
        if (cellEnd - open - 1 < 2) return cellEnd;
        return cellEnd - 1;
    }

    // The caret position inside one cell of a row text. A cell with text keeps the one-space-inside
    // stop (G8a, G4); a padding-only cell keeps EmptyCellStop (one space inside its closing pipe).
    static int CellCaretInRow(string text, RowCells cells, int column) {
        if (cells.Count == 0) return 0;
        if (column < 0) column = 0;
        if (column >= cells.Count) column = cells.Count - 1;
        int start = cells.Starts[column];
        int end = cells.Ends[column];
        bool empty = true;
        for (int i = start; i < end; i++) if (text[i] != ' ' && text[i] != '\t') { empty = false; break; }
        if (empty) return EmptyCellStop(text, start, end);
        return CellCaret(cells, column);
    }

    static int ColumnCount(TextDocument document, int firstLine) {
        RowCells cells = SplitRow(LineText(document, document.GetLineByNumber(firstLine + 1)));
        if (cells.Count > 0) return cells.Count;
        return SplitRow(LineText(document, document.GetLineByNumber(firstLine))).Count;
    }

    static string NewlineFor(TextDocument document, DocumentLine line) {
        if (line.DelimiterLength > 0) return document.GetText(line.EndOffset, line.DelimiterLength);
        DocumentLine previous = line.PreviousLine;
        if (previous != null && previous.DelimiterLength > 0) return document.GetText(previous.EndOffset, previous.DelimiterLength);
        return "\n";
    }
    #endregion

    #region AddRowBelow
    // One empty row: `columns` padding-only cells between the outer pipes. Both add-row commands
    // write the padding the table's own cells carry (EmptyCellText), so both create the same
    // "|  |  |" row, whose empty cell's caret stop (EmptyCellStop) types "| x |".
    static string EmptyRowText(int columns, string cell) {
        StringBuilder row = new StringBuilder();
        row.Append('|');
        for (int c = 0; c < columns; c++) row.Append(cell).Append('|');
        return row.ToString();
    }

    internal static CommandResult AddRowBelow(TextDocument document, int caretOffset) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        DocumentLine caretLine = document.GetLineByOffset(caretOffset);
        DocumentLine insertAfter = caretLine;
        if (caretLine.LineNumber == firstLine || caretLine.LineNumber == firstLine + 1)
            insertAfter = document.GetLineByNumber(firstLine + 1);
        int columns = ColumnCount(document, firstLine);
        if (columns < 1) columns = 1;
        int column = CellIndexAt(document, caretOffset);
        int offset = insertAfter.Offset + insertAfter.Length;
        string newline = NewlineFor(document, insertAfter);
        string rowText = EmptyRowText(columns, EmptyCellText(LineText(document, document.GetLineByNumber(firstLine))));
        CommandResult result = new CommandResult();
        result.Add(offset, 0, newline + rowText);
        result.SelectionStart = offset + newline.Length + CellCaretInRow(rowText, SplitRow(rowText), column);
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region AddRowAtEnd
    // Tab in the last cell of the table's last row appends one empty body row and puts the caret in
    // its first cell, like Word and Typora (MarkdownStructuralInput.TableCellMove answers the end of
    // the table with this instead of staying). The row keeps the table's column count and the
    // padding its own cells carry, so a spaced "| a | b |" table gains "|  |  |" and a compact
    // "|a|b|" table "|||"; the delimiter row (and with it the alignment) is never touched. The row
    // is written with the outer pipes EmptyRowText writes even for a table that omits them:
    // MarkdownLivePreview.SplitCells keeps only interior empty cells, so a padding-only row without
    // the outer pipes would not be a row the screen can draw (and the caret could not type into
    // it). One result means the host records the whole append as one undo step. The caret stays
    // inside the new first cell for a table whose header is the only row above the delimiter.
    internal static CommandResult AddRowAtEnd(TextDocument document, int caretOffset) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        DocumentLine caretLine = document.GetLineByOffset(caretOffset);
        // The append belongs to the last row, or to the header of a header-only table (the row
        // below it is the first body row). The delimiter row is structure and never carries it,
        // and a header with body rows below has its next cell in the row below instead.
        bool lastRow = caretLine.LineNumber == lastLine && caretLine.LineNumber != firstLine + 1;
        bool headerOnly = caretLine.LineNumber == firstLine && lastLine == firstLine + 1;
        if (!lastRow && !headerOnly) return null;
        if (MarkdownStructuralInput.IsInsideFence(document, caretLine)) return null;
        int columns = ColumnCount(document, firstLine);
        if (columns < 1) columns = 1;
        DocumentLine header = document.GetLineByNumber(firstLine);
        string rowText = EmptyRowText(columns, EmptyCellText(LineText(document, header)));
        DocumentLine last = document.GetLineByNumber(lastLine);
        string newline = NewlineFor(document, last);
        int offset = last.Offset + last.Length;
        CommandResult result = new CommandResult();
        result.Add(offset, 0, newline + rowText);
        result.SelectionStart = offset + newline.Length + CellCaretInRow(rowText, SplitRow(rowText), 0);
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }

    // One padding-only cell in the table's own padding width: the spaces the header's first cell
    // carries around its text ("| a | b |" answers two, a compact "|a|b|" none). A padding-only
    // header cell answers its own width, so an empty header keeps the table's cell shape.
    static string EmptyCellText(string headerText) {
        RowCells cells = SplitRow(headerText);
        if (cells.Count == 0) return NewCell;
        int start = cells.Starts[0];
        int end = cells.Ends[0];
        if (end <= start) return string.Empty;
        int contentStart = start;
        while (contentStart < end && (headerText[contentStart] == ' ' || headerText[contentStart] == '\t')) contentStart++;
        int contentEnd = end;
        while (contentEnd > contentStart && (headerText[contentEnd - 1] == ' ' || headerText[contentEnd - 1] == '\t')) contentEnd--;
        if (contentStart == contentEnd) return new string(' ', end - start);
        return new string(' ', (contentStart - start) + (end - contentEnd));
    }
    #endregion

    #region RemoveRow
    internal static CommandResult RemoveRow(TextDocument document, int caretOffset) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        int number = line.LineNumber;
        if (number == firstLine || number == firstLine + 1) return null;
        int column = CellIndexAt(document, caretOffset);
        int removeStart = line.Offset;
        int removeEnd = line.Offset + line.Length + line.DelimiterLength;
        if (line.DelimiterLength == 0 && line.PreviousLine != null) removeStart = line.PreviousLine.EndOffset;
        DocumentLine target;
        int targetOffset;
        if (number < lastLine) {
            target = line.NextLine;
            targetOffset = removeStart;
        } else {
            // G4: the row above the last body row is the delimiter row when only one body row is
            // left, and the caret may never sit there (the next Delete would erase the delimiter
            // and destroy the table). With no body row left above, the header row is the target;
            // either way the caret ends inside a real row's cell of the same column.
            target = line.PreviousLine;
            if (target.LineNumber == firstLine + 1) target = document.GetLineByNumber(firstLine);
            targetOffset = target.Offset;
        }
        string targetText = LineText(document, target);
        CommandResult result = new CommandResult();
        if (removeEnd > removeStart) result.Add(removeStart, removeEnd - removeStart, string.Empty);
        result.SelectionStart = targetOffset + CellCaretInRow(targetText, SplitRow(targetText), column);
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region InsertTableGap
    // G5: MarkdownCommands.InsertTable starts the grid on the line after the caret line, so a grid
    // made from a table row (or from the line directly under the table) lands on the old table's
    // last row and is read back as one more row of it, with cells from both tables. In that case
    // the new table goes below the old table's last row with one blank line on each side, reusing
    // a blank line that is already there instead of writing a second one. Every other caret keeps
    // MarkdownCommands.InsertTable's own placement. The first header cell is selected for typing,
    // exactly like the plain insert.
    // header is the host's localized "Heading" word (T("제목")), exactly like
    // MarkdownCommands.InsertTable; null keeps the default so a caller without a UI language
    // still writes the same grid. The grid this path writes must match that one byte for byte.
    internal static CommandResult InsertTableSafely(TextDocument document, int caretOffset, int rows, int columns) {
        return InsertTableSafely(document, caretOffset, rows, columns, null);
    }

    internal static CommandResult InsertTableSafely(TextDocument document, int caretOffset, int rows, int columns, string header) {
        if (document == null || caretOffset < 0 || caretOffset > document.TextLength) return null;
        if (rows < 0 || columns < 1) return null;
        string head = String.IsNullOrEmpty(header) ? "제목" : header;
        int firstLine, lastLine;
        bool atTable = FindTable(document, caretOffset, out firstLine, out lastLine);
        if (!atTable && !LineAfterTable(document, caretOffset, out firstLine, out lastLine))
            return MarkdownCommands.InsertTable(document, caretOffset, rows, columns, header);
        DocumentLine last = document.GetLineByNumber(lastLine);
        string newline = NewlineFor(document, last);
        // One blank line separates the old table from the new one. The blank line that already
        // follows the old table (if any) becomes the gap under the new one, so no second blank is
        // written; without one, the trailing newline opens it.
        DocumentLine next = last.NextLine;
        bool blankBelow = next != null && IsBlankText(LineText(document, next));
        int offset = last.Offset + last.Length;
        StringBuilder text = new StringBuilder();
        text.Append(newline).Append(newline);
        AppendTableText(text, newline, rows, columns, head);
        if (!blankBelow) text.Append(newline);
        CommandResult result = new CommandResult();
        result.Add(offset, 0, text.ToString());
        result.SelectionStart = offset + newline.Length * 2 + 2;
        // The first header placeholder is selected for typing: the localized word plus its
        // column number, exactly the run the grid wrote ("제목1", "Title1").
        result.SelectionLength = head.Length + 1;
        result.SortEdits();
        return result;
    }

    // The caret on the line directly under a table: that line is not a row, the line above it is
    // the table's last row.
    internal static bool LineAfterTable(TextDocument document, int caretOffset, out int firstLine, out int lastLine) {
        firstLine = 0;
        lastLine = 0;
        DocumentLine above = document.GetLineByOffset(caretOffset).PreviousLine;
        if (above == null) return false;
        int first, last;
        if (!FindTable(document, above.Offset, out first, out last) || last != above.LineNumber) return false;
        firstLine = first;
        lastLine = last;
        return true;
    }

    // The grid MarkdownCommands.InsertTable writes, kept identical so both entry points produce
    // the same table. header is the localized word already chosen for the header cells.
    static void AppendTableText(StringBuilder text, string newline, int rows, int columns, string header) {
        text.Append('|');
        for (int c = 1; c <= columns; c++) text.Append(' ').Append(header).Append(c.ToString(CultureInfo.InvariantCulture)).Append(" |");
        text.Append(newline).Append('|');
        for (int c = 0; c < columns; c++) text.Append("---|");
        for (int r = 0; r < rows; r++) {
            text.Append(newline).Append('|');
            for (int c = 0; c < columns; c++) text.Append("  |");
        }
    }
    #endregion

    #region AddColumnRight
    internal static CommandResult AddColumnRight(TextDocument document, int caretOffset) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        int column = CellIndexAt(document, caretOffset);
        DocumentLine caretLine = document.GetLineByOffset(caretOffset);
        CommandResult result = new CommandResult();
        int shift = 0;
        int caret = caretOffset;
        for (int n = firstLine; n <= lastLine; n++) {
            DocumentLine line = document.GetLineByNumber(n);
            RowCells cells = SplitRow(LineText(document, line));
            if (cells.Count == 0) continue;
            int c = column;
            if (c >= cells.Count) c = cells.Count - 1;
            string cell = n == firstLine + 1 ? NewDelimiterCell : NewCell;
            int pipe = cells.Pipes[c];
            string inserted;
            int insertAt;
            int cellStart;
            if (pipe >= 0) {
                insertAt = pipe + 1;
                inserted = cell + "|";
                cellStart = insertAt;
            } else {
                insertAt = line.Length;
                inserted = "|" + cell + "|";
                cellStart = insertAt + 1;
            }
            result.Add(line.Offset + insertAt, 0, inserted);
            if (line.LineNumber < caretLine.LineNumber) shift += inserted.Length;
            else if (line.LineNumber == caretLine.LineNumber) caret = line.Offset + shift + cellStart + 1;
        }
        result.SelectionStart = caret;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region RemoveColumn
    internal static CommandResult RemoveColumn(TextDocument document, int caretOffset) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        if (ColumnCount(document, firstLine) <= 1) return DeleteTable(document, caretOffset);
        int column = CellIndexAt(document, caretOffset);
        DocumentLine caretLine = document.GetLineByOffset(caretOffset);
        CommandResult result = new CommandResult();
        int shift = 0;
        int caret = caretOffset;
        for (int n = firstLine; n <= lastLine; n++) {
            DocumentLine line = document.GetLineByNumber(n);
            string text = LineText(document, line);
            RowCells cells = SplitRow(text);
            if (cells.Count == 0) continue;
            int c = column;
            if (c >= cells.Count) c = cells.Count - 1;
            int start = cells.Starts[c];
            int length = cells.Ends[c] - start;
            if (cells.Pipes[c] >= 0) length++;
            // A row that keeps no pipe is not a row the renderer draws (GFM needs at least one
            // pipe in every row), so dropping the first column of a table without outer pipes
            // keeps one: "Name | Age" becomes "| Age", not " Age".
            string remaining = text.Remove(start, length);
            string replacement = HasTablePipe(remaining) ? string.Empty : "|";
            result.Add(line.Offset + start, length, replacement);
            if (line.LineNumber < caretLine.LineNumber) {
                shift += replacement.Length - length;
            } else if (line.LineNumber == caretLine.LineNumber) {
                string afterText = replacement.Length > 0
                    ? remaining.Insert(Math.Min(start, remaining.Length), replacement)
                    : remaining;
                RowCells after = SplitRow(afterText);
                int index = c;
                int local = 0;
                if (after.Count > 0) {
                    if (index >= after.Count) index = after.Count - 1;
                    local = CellCaret(after, index);
                }
                caret = line.Offset + shift + local;
            }
        }
        result.SelectionStart = caret;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region DeleteTable
    internal static CommandResult DeleteTable(TextDocument document, int caretOffset) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        DocumentLine first = document.GetLineByNumber(firstLine);
        DocumentLine last = document.GetLineByNumber(lastLine);
        int end = last.Offset + last.Length + last.DelimiterLength;
        CommandResult result = new CommandResult();
        if (end > first.Offset) result.Add(first.Offset, end - first.Offset, string.Empty);
        result.SelectionStart = first.Offset;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region TableLevel
    // Indent/outdent move the whole table in or out of the list item directly above it. The table
    // is part of the item only through its line indent (the item's content indent), and a table
    // indented by spaces alone would read back as an indented code block, so indent re-indents every
    // table line to that item's content indent instead of adding a fixed unit. With no list item
    // above, indent is unavailable and says so; outdent removes one indent unit from every line, so
    // a table at level 1 leaves the list entirely. Both are the same prefix edit shape as the list
    // Tab rule (MarkdownStructuralInput.Tab), which keeps the two views and the save identical.
    internal const int TableIndentUnit = 2;
    internal const string IndentNoListTip = "바로 위에 목록 항목이 없어 표를 들여쓸 수 없습니다.";
    internal const string IndentInsideTip = "표가 이미 목록 항목 안에 있어 더 들여쓸 수 없습니다.";
    internal const string OutdentNoIndentTip = "들여쓰기가 없어 표를 내어쓸 수 없습니다.";

    // Both button states and both tooltips in one pass, so the tool row reads the table once and
    // never disagrees with what the commands will do. Called from the tool row update on every
    // pipeline flush, hence the single FindTable call.
    internal static void TableLevelState(TextDocument document, int offset, out bool canIndent, out bool canOutdent, out string indentTip, out string outdentTip) {
        canIndent = false;
        canOutdent = false;
        indentTip = null;
        outdentTip = null;
        if (document == null || offset < 0 || offset > document.TextLength) return;
        int firstLine, lastLine;
        if (!FindTable(document, offset, out firstLine, out lastLine)) return;
        // No fence scan here: this runs on every pipeline flush (a keystroke). A pipe row inside a
        // fence is not a table for the live preview either, so the tool row is not shown there, and
        // the command path re-checks the fence before it edits.
        canIndent = PlanTableLevel(document, firstLine, lastLine, true) != null;
        canOutdent = PlanTableLevel(document, firstLine, lastLine, false) != null;
        // The user was told nothing while the buttons looked live but did nothing, so every
        // unavailable state carries the reason that applies to it.
        if (!canIndent) indentTip = ListLineAbove(document, firstLine) == null ? IndentNoListTip : IndentInsideTip;
        if (!canOutdent) outdentTip = OutdentNoIndentTip;
    }

    internal static bool CanIndentTable(TextDocument document, int offset) {
        bool canIndent, canOutdent;
        string indentTip, outdentTip;
        TableLevelState(document, offset, out canIndent, out canOutdent, out indentTip, out outdentTip);
        return canIndent;
    }

    internal static bool CanOutdentTable(TextDocument document, int offset) {
        bool canIndent, canOutdent;
        string indentTip, outdentTip;
        TableLevelState(document, offset, out canIndent, out canOutdent, out indentTip, out outdentTip);
        return canOutdent;
    }

    internal static CommandResult IndentTable(TextDocument document, int selectionStart, int selectionLength) {
        return TableLevelEdit(document, selectionStart, selectionLength, true);
    }

    internal static CommandResult OutdentTable(TextDocument document, int selectionStart, int selectionLength) {
        return TableLevelEdit(document, selectionStart, selectionLength, false);
    }

    // What one move does: the table's line range, the indent every line gets when the move sets one
    // (indent, and outdent to a parent level), or "drop one unit from each line" when it does not
    // (outdent from any other indent), or "remove all indentation" for a table leaving a top-level
    // item. BlankLine/BlankRemove/BlankInsert carry the item's own blank continuation line directly
    // under the table: it belongs to the item, so it moves with the table and keeps indent and
    // outdent exact inverses of each other.
    sealed class TableLevelPlan {
        internal int FirstLine;
        internal int LastLine;
        internal string New;
        internal bool DropUnit;
        internal bool OutdentAll;
        internal int BlankLine;
        internal int BlankRemove;
        internal string BlankInsert;
    }

    static CommandResult TableLevelEdit(TextDocument document, int selectionStart, int selectionLength, bool indent) {
        if (document == null || selectionStart < 0 || selectionLength < 0) return null;
        if (selectionStart > document.TextLength || selectionLength > document.TextLength - selectionStart) return null;
        int firstLine, lastLine;
        if (!FindTable(document, selectionStart, out firstLine, out lastLine)) return null;
        // A pipe row inside a fenced block is literal text, not a table.
        if (MarkdownStructuralInput.IsInsideFence(document, document.GetLineByOffset(selectionStart))) return null;
        TableLevelPlan plan = PlanTableLevel(document, firstLine, lastLine, indent);
        if (plan == null) return null;
        CommandResult result = new CommandResult();
        for (int number = plan.FirstLine; number <= plan.LastLine; number++) {
            DocumentLine line = document.GetLineByNumber(number);
            string text = LineText(document, line);
            int remove = LeadingIndentLength(text);
            string replacement;
            if (plan.DropUnit) replacement = DropIndentUnit(text.Substring(0, remove));
            else if (plan.OutdentAll) replacement = string.Empty;
            else replacement = plan.New;
            if (remove == replacement.Length && string.CompareOrdinal(text.Substring(0, remove), replacement) == 0) continue;
            result.Add(line.Offset, remove, replacement);
        }
        if (plan.BlankLine > 0) {
            DocumentLine blank = document.GetLineByNumber(plan.BlankLine);
            result.Add(blank.Offset, plan.BlankRemove, plan.BlankInsert);
        }
        if (result.Edits.Count == 0) return null;
        MapTableSelection(result, selectionStart, selectionStart + selectionLength);
        return result;
    }

    static TableLevelPlan PlanTableLevel(TextDocument document, int firstLine, int lastLine, bool indent) {
        if (firstLine < 1 || lastLine < firstLine || lastLine > document.LineCount) return null;
        DocumentLine first = document.GetLineByNumber(firstLine);
        string firstText = LineText(document, first);
        string oldIndent = firstText.Substring(0, LeadingIndentLength(firstText));
        TableLevelPlan plan = new TableLevelPlan();
        plan.FirstLine = firstLine;
        plan.LastLine = lastLine;
        if (indent) {
            // A list item has to be directly above (blank lines and a lifted continuation
            // paragraph do not hide it, exactly like the basic view's RichTableIndent). The target
            // is that item's content indent, which is where its child block starts; a table already
            // there is inside the item and has nowhere deeper to go (a second level is a code
            // block), so the command declines and the button explains why.
            DocumentLine above = ListLineAbove(document, firstLine);
            if (above == null) return null;
            string aboveText = LineText(document, above);
            string itemIndent = aboveText.Substring(0, LeadingIndentLength(aboveText));
            string unit = IndentUnit(aboveText);
            if (IndentWidth(oldIndent) >= IndentWidth(itemIndent) + IndentWidth(unit)) return null;
            plan.New = itemIndent + unit;
            // The item's own blank continuation line directly under the table joins it, so indent
            // undoes outdent exactly. A line with nothing on it stays empty: indenting it would only
            // leave trailing whitespace behind.
            DocumentLine next = document.GetLineByNumber(lastLine).NextLine;
            if (next != null && next.Length > 0 && IsBlankText(LineText(document, next)) && LeadingIndentLength(LineText(document, next)) == 0) {
                plan.BlankLine = next.LineNumber;
                plan.BlankInsert = unit;
            }
        } else {
            // One level out. The level above the item the table currently belongs to: the nearest
            // list line above it that is shallower owns it, so the table takes that line's content
            // indent (no shallower line means the item is top level and the table leaves the list,
            // i.e. column 0). A table indented with no list line above it at all keeps only its
            // odd whitespace removed, one unit at a time.
            if (IndentWidth(oldIndent) <= 0) return null;
            DocumentLine above = ListLineAbove(document, firstLine);
            if (above == null) {
                plan.DropUnit = true;
                return plan;
            }
            // The item the table belongs to is the nearest list line above whose content indent is
            // no deeper than the table's own indent (a list line indented past the table is a child
            // the table does not sit under). One level out is that item's parent's content indent,
            // and no parent means the table leaves the list (column 0).
            DocumentLine owner = OwningListItem(document, above, IndentWidth(oldIndent));
            if (owner == null) {
                plan.DropUnit = true;
                return plan;
            }
            string ownerText = LineText(document, owner);
            int ownerContent = IndentWidth(ownerText.Substring(0, LeadingIndentLength(ownerText))) + IndentWidth(IndentUnit(ownerText));
            DocumentLine parent = ParentListLine(document, owner, ownerContent);
            if (parent == null) {
                plan.OutdentAll = true;
            } else {
                string parentText = LineText(document, parent);
                plan.New = parentText.Substring(0, LeadingIndentLength(parentText)) + IndentUnit(parentText);
            }
            // The item's own blank continuation line under the table leaves the list with it.
            DocumentLine next = document.GetLineByNumber(lastLine).NextLine;
            if (next != null && IsBlankText(LineText(document, next)) && LeadingIndentLength(LineText(document, next)) > 0) {
                string text = LineText(document, next);
                int remove = LeadingIndentLength(text);
                plan.BlankLine = next.LineNumber;
                plan.BlankRemove = remove;
                plan.BlankInsert = DropIndentUnit(text.Substring(0, remove));
            }
        }
        return plan;
    }

    // The nearest list line at or above `from` whose content indent (its own indent plus its marker
    // unit) is shallower than `contentIndent`. Null means no shallower list line exists. The search
    // starts at `from` itself, because an item written deeper than the line above it is one level out
    // from that line, not out of the list.
    static DocumentLine ParentListLine(TextDocument document, DocumentLine from, int contentIndent) {
        DocumentLine previous = from;
        int scanned = 0;
        while (previous != null && scanned < 200) {
            string text = LineText(document, previous);
            if (text.Trim().Length > 0 && LineStartsList(text) && !LineStartsQuote(text)) {
                string indent = text.Substring(0, LeadingIndentLength(text));
                if (IndentWidth(indent) + IndentWidth(IndentUnit(text)) < contentIndent) return previous;
            }
            previous = previous.PreviousLine;
            scanned++;
        }
        return null;
    }

    // The item the table belongs to: the nearest list line at or above `from` whose content indent is
    // no deeper than the table's own indent. A line indented past the table belongs to a child the
    // table does not sit under, so it is skipped. Null means the table is not under any list line
    // this rule can name, and the caller falls back to a plain one-unit removal.
    static DocumentLine OwningListItem(TextDocument document, DocumentLine from, int tableIndent) {
        DocumentLine previous = from;
        int scanned = 0;
        while (previous != null && scanned < 200) {
            string text = LineText(document, previous);
            if (text.Trim().Length > 0 && LineStartsList(text) && !LineStartsQuote(text)) {
                string indent = text.Substring(0, LeadingIndentLength(text));
                if (IndentWidth(indent) + IndentWidth(IndentUnit(text)) <= tableIndent) return previous;
            }
            previous = previous.PreviousLine;
            scanned++;
        }
        return null;
    }

    // The nearest non-blank line above the table when it is a list item's line (not inside a
    // quote, where the item belongs to the quote's own block), or null. A line with blank text is
    // skipped, which is what the basic view's RichTableIndent does for its blank paragraph.
    static DocumentLine ListLineAbove(TextDocument document, int firstLine) {
        DocumentLine previous = document.GetLineByNumber(firstLine).PreviousLine;
        int scanned = 0;
        while (previous != null && scanned < 200) {
            string text = LineText(document, previous);
            if (text.Trim().Length > 0) {
                if (!LineStartsList(text) || LineStartsQuote(text)) return null;
                return previous;
            }
            previous = previous.PreviousLine;
            scanned++;
        }
        return null;
    }

    // The line's first real character is a bullet or an ordered marker followed by a space, which
    // is the same rule MarkdownStructuralInput.ParseSlots uses for a list item.
    static bool LineStartsList(string text) {
        int at = LeadingIndentLength(text);
        if (at >= text.Length) return false;
        char c = text[at];
        if ((c == '-' || c == '*' || c == '+') && at + 1 < text.Length && text[at + 1] == ' ') return true;
        int digits = at;
        while (digits < text.Length && digits - at < 9 && text[digits] >= '0' && text[digits] <= '9') digits++;
        return digits < text.Length && (text[digits] == '.' || text[digits] == ')')
            && digits + 1 < text.Length && text[digits + 1] == ' ';
    }

    // A quote marker in front of the item: the item is then the quote's block, and the table move
    // does not run there.
    static bool LineStartsQuote(string text) {
        int at = LeadingIndentLength(text);
        return at < text.Length && text[at] == '>';
    }

    // One indent unit in the item line's own style: a tab when the item is written with one,
    // otherwise the writer's two-space continuation unit. That keeps the table lines byte-identical
    // to the item's own continuation lines, which is what the two views and the save compare.
    static string IndentUnit(string line) {
        return line.Length > 0 && line[0] == '\t' ? "\t" : new string(' ', TableIndentUnit);
    }

    static int LeadingIndentLength(string text) {
        int i = 0;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return i;
    }

    static bool IsBlankText(string text) {
        for (int i = 0; i < text.Length; i++) if (text[i] != ' ' && text[i] != '\t') return false;
        return true;
    }

    // Indent measured in columns with a tab at four, the same width the app writes a tab as.
    static int IndentWidth(string indent) {
        int width = 0;
        for (int i = 0; i < indent.Length; i++) width += indent[i] == '\t' ? 4 : 1;
        return width;
    }

    // One unit off the left of an existing indent, tab aware: a leading tab is one unit, otherwise
    // up to two leading spaces are.
    static string DropIndentUnit(string indent) {
        if (indent.Length == 0) return string.Empty;
        if (indent[0] == '\t') return indent.Substring(1);
        int spaces = 0;
        while (spaces < indent.Length && spaces < TableIndentUnit && indent[spaces] == ' ') spaces++;
        return indent.Substring(spaces);
    }

    // Post-edit position of a pre-edit offset: an insertion at the offset moves it, a removal
    // before it pulls it back.
    static int MapTablePosition(List<SourceEdit> edits, int position, bool includeInsertionAtPosition) {
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

    static void MapTableSelection(CommandResult result, int originalStart, int originalEnd) {
        bool caret = originalStart == originalEnd;
        int start = MapTablePosition(result.Edits, originalStart, true);
        int end = MapTablePosition(result.Edits, originalEnd, caret);
        if (end < start) end = start;
        result.SelectionStart = start;
        result.SelectionLength = end - start;
        result.SortEdits();
    }
    #endregion

    #region ParagraphAfter
    // One empty line between the table and the next line. The table's own line delimiter is
    // replaced by two delimiters; at the document end both are appended.
    internal static CommandResult ParagraphAfter(TextDocument document, int caretOffset) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        DocumentLine last = document.GetLineByNumber(lastLine);
        string newline = NewlineFor(document, last);
        DocumentLine next = last.NextLine;
        CommandResult result = new CommandResult();
        int caret;
        if (next == null) {
            result.Add(last.EndOffset, 0, newline + newline);
            caret = last.EndOffset + newline.Length;
        } else if (next.Length == 0) {
            caret = next.Offset;
        } else {
            result.Add(last.EndOffset, last.DelimiterLength, newline + newline);
            caret = last.EndOffset + newline.Length;
        }
        result.SelectionStart = caret;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }

    // The tool row's "아래 문단 추가" on the live surface: the empty paragraph line always has
    // one empty line between it and the last row, because a paragraph written directly under a
    // row is read back as one more row by other Markdown parsers. The row's own delimiter stays
    // and two newlines go in front of it, so whatever followed the table keeps its own line and
    // the caret opens a fresh paragraph line under the blank one.
    internal static CommandResult ParagraphAfterGap(TextDocument document, int caretOffset) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        if (MarkdownStructuralInput.IsInsideFence(document, document.GetLineByOffset(caretOffset))) return null;
        DocumentLine last = document.GetLineByNumber(lastLine);
        string newline = NewlineFor(document, last);
        CommandResult result = new CommandResult();
        result.Add(last.EndOffset, 0, newline + newline);
        result.SelectionStart = last.EndOffset + newline.Length * 2;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region EnterOutsideTable
    // Enter at a table boundary makes a paragraph line, never a row. The basic view answers this
    // with StructuralInput.cs:102 OutsideTableEnter: a table followed by nothing or by another
    // table keeps an empty gap paragraph (ViewContinuity.cs:41 EnsureTableGaps), Enter in that
    // paragraph only adds another empty paragraph, and a table row is never created. The live
    // surface's source needs the same separation, because a line written directly under the last
    // row is read back as one more row.
    //
    // Two positions are handled; every other position returns null so the editor's own newline (and
    // its indentation strategy) applies:
    //   end of the last row   - one newline and the caret on the empty line it opens, which is the
    //                           paragraph line the basic view's caret sits in. Anything already
    //                           under the table moves one line down and keeps its own line. The
    //                           live view's caret cannot stand past the row's trailing pipe, so
    //                           the last cell's own end answers the same rule.
    //   column 0 of the header or of its delimiter row - one newline in front of the whole table,
    //                           caret on the new line. The delimiter row is included because
    //                           splitting it (the editor's own newline) would destroy the table;
    //                           the delimiter row is normally hidden, so this is the raw-view case.
    // A caret inside a row (between two pipes) is table cell editing (Stage 4) and is left alone,
    // as is a caret in the middle of any row.
    internal static CommandResult EnterOutsideTable(TextDocument document, int selectionStart, int selectionLength) {
        return EnterOutsideTable(document, selectionStart, selectionLength, true);
    }
    internal static CommandResult EnterOutsideTable(TextDocument document, int selectionStart, int selectionLength, bool keyEnter) {
        if (document == null || selectionStart < 0 || selectionLength != 0) return null;
        if (selectionStart > document.TextLength) return null;
        DocumentLine line = document.GetLineByOffset(selectionStart);
        if (MarkdownStructuralInput.IsInsideFence(document, line)) return null;
        int column = selectionStart - line.Offset;
        int firstLine, lastLine;
        if (!FindTable(document, selectionStart, out firstLine, out lastLine)) return null;
        if (column == 0 && line.LineNumber <= firstLine + 1) return AboveTable(document, document.GetLineByNumber(firstLine));
        if (line.LineNumber == lastLine) {
            if (column == line.Length) return BelowTable(document, line);
            // The live view keeps the caret inside the row's cells (H7), so at the last row the
            // old line-end boundary is now the last cell's own trimmed end. The Enter key there
            // still leaves the table; Shift+Enter (the newline text path) is the cell break.
            if (keyEnter) {
                string row = LineText(document, line);
                RowCells cells = SplitRow(row);
                if (cells.Count > 0) {
                    int contentEnd = TrimEnd(row, cells.Starts[cells.Count - 1], cells.Ends[cells.Count - 1]);
                    // The caret may stand in the last cell's trailing padding (a typed space before
                    // the row's closing pipe), which is the cell's own text: only spaces or tabs
                    // between the trimmed end and the caret mean the same boundary, so Enter leaves
                    // the table the same way with or without trailing spaces. A pipe in between
                    // keeps the rule off the row's structure.
                    if (column >= contentEnd && IsBlankRange(row, contentEnd, column)) return BelowTable(document, line);
                }
            }
        }
        return null;
    }

    // One newline in front of the table; the caret stays on the new line, above the header row.
    static CommandResult AboveTable(TextDocument document, DocumentLine header) {
        CommandResult result = new CommandResult();
        result.Add(header.Offset, 0, NewlineFor(document, header));
        result.SelectionStart = header.Offset;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }

    // The paragraph line under the table, which is where the basic view's caret is (its
    // OutsideTableEnter, StructuralInput.cs:102, only adds another empty paragraph there and never
    // a row). One newline is written and the caret goes to the start of the line it opens; that
    // line has no pipe, so the table ends there and typed text is a paragraph. Writing the newline
    // here rather than leaving it to the editor keeps the table row's indentation off the new line,
    // which is what the basic view's plain empty paragraph does. Repeated Enter only adds empty
    // lines, like the basic view's one-more-empty-paragraph.
    static CommandResult BelowTable(TextDocument document, DocumentLine last) {
        string newline = NewlineFor(document, last);
        DocumentLine next = last.NextLine;
        CommandResult result = new CommandResult();
        int at;
        int caret;
        if (next == null) {
            // Nothing follows: the newline is the last row's own delimiter and the caret line is
            // the empty last line it opens.
            at = last.EndOffset;
            caret = at + newline.Length;
        } else {
            // A line follows: the newline opens an empty line in front of it and the caret goes
            // there; that line keeps its text one line further down.
            at = next.Offset;
            caret = at;
        }
        result.Add(at, 0, newline);
        result.SelectionStart = caret;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion

    #region CellEditing
    // Stage 4: editing inside one table cell on the live surface, where a row is a single
    // projected line. The basic view edits a cell as a FlowDocument paragraph, so Enter splits
    // the paragraph and the writer joins a cell's blocks with "<br>" (MarkdownWriter.cs:63),
    // selection edits keep the WPF table's cells, and a cell boundary is never crossed by
    // Backspace/Delete. These pure functions rewrite the same source text. The host passes the
    // cell spans the preview parsed for the caret line, so no table scan runs on a keystroke;
    // CellsAt is the standalone form (tests, clipboard) for a line that is a pipe row.
    internal const string CellBreak = "<br>";
    internal const int CellBreakLength = 4;

    // Cell spans of a pipe row, as the renderer reads them (escaped pipes and code spans are not
    // separators). Null when the line is not a pipe row at all.
    internal static List<MarkdownLivePreview.Cell> CellsAt(TextDocument document, int offset) {
        if (document == null || offset < 0 || offset > document.TextLength) return null;
        DocumentLine line = document.GetLineByOffset(offset);
        string text = LineText(document, line);
        if (!IsTableRow(text)) return null;
        List<MarkdownLivePreview.Cell> cells = MarkdownLivePreview.SplitCells(text);
        return cells.Count == 0 ? null : cells;
    }

    // The cell that owns an element-relative offset; a cell's own end belongs to that cell and
    // past the last cell the last one is answered.
    static int CellAt(List<MarkdownLivePreview.Cell> cells, int relative) {
        for (int c = 0; c < cells.Count; c++) if (relative <= cells[c].End) return c;
        return cells.Count - 1;
    }

    // Index of the cell a row offset belongs to when a cell's own padding counts as that cell, or
    // -1 on a separator pipe and outside the cells. A cell with text owns the run between its
    // trimmed end and the pipe that closes it; a padding-only cell is all padding, so every offset
    // between its opening pipe and its trimmed position (its caret stop included) belongs to it.
    // SourceTab and the paste caret use this: the old text-span test read a caret inside an empty
    // cell as "past the row's last cell" and moved on to the next row.
    internal static int CellIndexAtPadding(string text, List<MarkdownLivePreview.Cell> cells, int relative) {
        if (text == null || cells == null || cells.Count == 0) return -1;
        for (int c = 0; c < cells.Count; c++)
            if (relative >= cells[c].Start && relative <= cells[c].End) return c;
        for (int c = 0; c < cells.Count; c++) {
            if (cells[c].Start != cells[c].End) {
                if (relative <= cells[c].End) continue;
                int pipe = cells[c].End;
                while (pipe < text.Length && (text[pipe] == ' ' || text[pipe] == '\t')) pipe++;
                if (pipe < text.Length && text[pipe] == '|') {
                    if (relative < pipe) return c;
                    continue;
                }
                // The last cell's padding runs to the line's end when the row has no closing pipe.
                if (c == cells.Count - 1 && pipe == text.Length) return c;
                continue;
            }
            int open = cells[c].Start - 1;
            while (open >= 0 && (text[open] == ' ' || text[open] == '\t')) open--;
            if (open < 0 || text[open] != '|') continue;
            if (relative > open && relative < cells[c].End) return c;
        }
        return -1;
    }

    // The nearest cell text position: inside the containing cell, at the cell's start when the
    // caret is in its leading padding, and at the last cell's end past the row's text.
    static int ClampIntoCell(List<MarkdownLivePreview.Cell> cells, int relative) {
        for (int c = 0; c < cells.Count; c++) {
            if (relative > cells[c].End) continue;
            return relative < cells[c].Start ? cells[c].Start : relative;
        }
        return cells[cells.Count - 1].End;
    }

    static CommandResult CaretOnly(int offset) {
        CommandResult result = new CommandResult();
        result.SelectionStart = offset;
        result.SelectionLength = 0;
        return result;
    }

    static CommandResult DeleteRange(int offset, int length) {
        CommandResult result = new CommandResult();
        if (length > 0) result.Add(offset, length, string.Empty);
        result.SelectionStart = offset;
        result.SelectionLength = 0;
        return result;
    }

    // "<br>", "<br/>" and "<br />" (case-insensitive), the same tags the preview turns into a
    // cell line break (MarkdownLivePreview.LineBreakEnd).
    static int CellBreakEnd(string text, int start) {
        if (start < 0 || start >= text.Length || text[start] != '<') return -1;
        int p = start + 1;
        if (p + 1 >= text.Length) return -1;
        char b = text[p], r = text[p + 1];
        if ((b != 'b' && b != 'B') || (r != 'r' && r != 'R')) return -1;
        p += 2;
        while (p < text.Length && text[p] == ' ') p++;
        if (p < text.Length && text[p] == '/') p++;
        while (p < text.Length && text[p] == ' ') p++;
        if (p >= text.Length || text[p] != '>' || p - start > 8) return -1;
        return p + 1;
    }

    static int CellBreakBefore(string text, int end) {
        int from = Math.Max(0, end - 9);
        for (int start = from; start < end; start++) {
            if (text[start] != '<') continue;
            if (CellBreakEnd(text, start) == end) return start;
        }
        return -1;
    }

    // The basic view parses pasted text into cell paragraphs, which the writer joins with "<br>";
    // a blank line is an empty paragraph too, so every newline is exactly one break. Tab has no
    // fill-cells rule in either view, so it stays literal text. A pasted "|" is cell text, not a
    // new column, so it is written as "\|" unless it is already escaped. An escaped pipe has an
    // ODD run of backslashes in front of it ("\\|" is a literal backslash and a live pipe, so that
    // pipe still needs one more backslash); a pipe at the start of the pasted text counts the
    // document's own backslash run in front of the insertion point (R5).
    // A pipe inside a closed `code` span stays raw: TablePipes skips the span whole (a border
    // needs the same-length backtick run to close first) and ParseInline draws span text
    // literally, so "\|" there would show a backslash the user never typed. The cell's text
    // around the insertion point (the replaced selection is not kept) is what that scan reads.
    internal static string CellInsertText(TextDocument document, int selectionStart, int selectionLength, string value, List<MarkdownLivePreview.Cell> cells) {
        string canonical = value.Replace("\r\n", "\n").Replace('\r', '\n');
        if (canonical.IndexOf('|') >= 0) {
            string prefix = string.Empty, suffix = string.Empty;
            if (document != null && cells != null && cells.Count > 0 && selectionStart >= 0 && selectionStart <= document.TextLength) {
                DocumentLine line = document.GetLineByOffset(selectionStart);
                int relative = selectionStart - line.Offset;
                if (selectionLength <= 0) {
                    int at = ClampIntoCell(cells, relative);
                    int cell = CellAt(cells, at);
                    prefix = document.GetText(line.Offset + cells[cell].Start, at - cells[cell].Start);
                    suffix = document.GetText(line.Offset + at, cells[cell].End - at);
                } else {
                    int end = Math.Min(selectionStart + selectionLength, line.Offset + line.Length) - line.Offset;
                    int first = CellAt(cells, relative), last = CellAt(cells, end);
                    if (first == last && relative >= cells[first].Start && end <= cells[first].End) {
                        prefix = document.GetText(line.Offset + cells[first].Start, relative - cells[first].Start);
                        suffix = document.GetText(line.Offset + end, cells[first].End - end);
                    }
                }
            }
            // Code-span content ranges in the cell as the renderer would draw it: a backslash
            // hides the next character and a run closes only on the same-length run (TablePipes).
            string cellText = prefix + canonical + suffix;
            var opens = new List<int>();
            var closes = new List<int>();
            int budget = cellText.Length * 3;
            for (int p = 0; p < cellText.Length; ) {
                char c = cellText[p];
                if (c == '\\') { p += 2; continue; }
                if (c == '`') {
                    int run = 1;
                    while (p + run < cellText.Length && cellText[p + run] == '`') run++;
                    int close = MarkdownPlainText.CodeClose(cellText, p + run, run, ref budget);
                    if (close >= 0) { opens.Add(p + run); closes.Add(close); p = close + run; continue; }
                    p += run;
                    continue;
                }
                p++;
            }
            StringBuilder escaped = new StringBuilder(canonical.Length + 8);
            int backslashes = 0;
            if (document != null) {
                // The run in front of the insertion point: a "\\" pair is one literal backslash,
                // so only an ODD run already escapes the first pasted pipe.
                for (int p = Math.Min(selectionStart, document.TextLength) - 1; p >= 0; p--) {
                    if (document.GetCharAt(p) != '\\') break;
                    backslashes++;
                }
            }
            int span = 0;
            int offset = prefix.Length;
            for (int i = 0; i < canonical.Length; i++) {
                char c = canonical[i];
                int at = offset + i;
                while (span < opens.Count && closes[span] < at) span++;
                bool code = span < opens.Count && at >= opens[span];
                if (c == '|' && !code && (backslashes & 1) == 0) escaped.Append('\\');
                if (c == '\\') backslashes++;
                else backslashes = 0;
                escaped.Append(c);
            }
            canonical = escaped.ToString();
        }
        return canonical.Replace("\n", CellBreak);
    }

    // Enter inside a cell writes the basic view's cell paragraph break as "<br>", so the table
    // keeps its rows. A selection inside one cell is replaced; a selection across cells (or rows)
    // keeps every "|" and puts the break in the first selected cell.
    internal static CommandResult EnterInCell(TextDocument document, int selectionStart, int selectionLength, List<MarkdownLivePreview.Cell> cells) {
        if (document == null || cells == null || cells.Count == 0) return null;
        if (selectionStart < 0 || selectionLength < 0) return null;
        if (selectionStart > document.TextLength || selectionLength > document.TextLength - selectionStart) return null;
        // A selection that covers whole tables is ordinary text (the editor's own newline
        // replacement), not a cell selection.
        if (SelectionCoversWholeTables(document, selectionStart, selectionLength)) return null;
        int selectionEnd = selectionStart + selectionLength;
        DocumentLine line = document.GetLineByOffset(selectionStart);
        // A selection that reaches another line is still a table selection when it stays inside
        // the table: clear each selected cell's text and put the paragraph break in the first
        // one, like the basic view. CellSelectionEdit declines outside the table.
        if (selectionLength > 0 && document.GetLineByOffset(Math.Min(selectionEnd, document.TextLength)).LineNumber != line.LineNumber)
            return CellSelectionEdit(document, selectionStart, selectionLength, CellBreak);
        if (selectionLength == 0) {
            string rowText = LineText(document, line);
            int at = line.Offset + ClampIntoCell(cells, selectionStart - line.Offset);
            // H9 in a cell: an Enter inside a marker run splits at the run's outer edge, so a
            // pair stays on one side of the cell's <br>. The cell's "<br>" is the single-marker
            // edge variant (an inert lone marker must not become an opener); plain Enter keeps
            // its old edges.
            int edge = MarkdownStructuralInput.MarkerRunEdge(rowText, at - line.Offset, true);
            if (edge != at - line.Offset) at = line.Offset + ClampIntoCell(cells, edge);
            CommandResult point = new CommandResult();
            point.Add(at, 0, CellBreak);
            point.SelectionStart = at + CellBreakLength;
            point.SelectionLength = 0;
            return point;
        }
        int firstCell = CellAt(cells, selectionStart - line.Offset);
        int lastCell = CellAt(cells, selectionEnd - line.Offset);
        if (firstCell == lastCell && selectionStart - line.Offset >= cells[firstCell].Start && selectionEnd - line.Offset <= cells[firstCell].End) {
            CommandResult inside = new CommandResult();
            inside.Add(selectionStart, selectionLength, CellBreak);
            inside.SelectionStart = selectionStart + CellBreakLength;
            inside.SelectionLength = 0;
            return inside;
        }
        return CellSelectionEdit(document, selectionStart, selectionLength, CellBreak);
    }

    // Paste inside a table cell follows the same rule as Enter: the pasted lines become the
    // cell's paragraphs and are saved as "<br>". A selection that leaves the table keeps the
    // editor's own replacement (null).
    internal static CommandResult PasteInCell(TextDocument document, int selectionStart, int selectionLength, string insert, List<MarkdownLivePreview.Cell> cells) {
        if (document == null || cells == null || cells.Count == 0 || insert == null) return null;
        if (selectionStart < 0 || selectionLength < 0) return null;
        if (selectionStart > document.TextLength || selectionLength > document.TextLength - selectionStart) return null;
        // A paste over whole tables replaces those lines (the editor's own replacement) instead
        // of writing the text into the first cell.
        if (SelectionCoversWholeTables(document, selectionStart, selectionLength)) return null;
        string text = CellInsertText(document, selectionStart, selectionLength, insert, cells);
        DocumentLine line = document.GetLineByOffset(selectionStart);
        int selectionEnd = selectionStart + selectionLength;
        if (selectionLength > 0 && document.GetLineByOffset(Math.Min(selectionEnd, document.TextLength)).LineNumber != line.LineNumber)
            return CellSelectionEdit(document, selectionStart, selectionLength, text);
        if (selectionLength == 0) {
            int at = line.Offset + ClampIntoCell(cells, selectionStart - line.Offset);
            CommandResult point = new CommandResult();
            if (text.Length > 0) point.Add(at, 0, text);
            point.SelectionStart = at + text.Length;
            point.SelectionLength = 0;
            return point;
        }
        int firstCell = CellAt(cells, selectionStart - line.Offset);
        int lastCell = CellAt(cells, selectionEnd - line.Offset);
        if (firstCell == lastCell && selectionStart - line.Offset >= cells[firstCell].Start && selectionEnd - line.Offset <= cells[firstCell].End) {
            CommandResult inside = new CommandResult();
            inside.Add(selectionStart, selectionLength, text);
            inside.SelectionStart = selectionStart + text.Length;
            inside.SelectionLength = 0;
            return inside;
        }
        return CellSelectionEdit(document, selectionStart, selectionLength, text);
    }

    // Clearing a cell selection (Backspace/Delete/cut): the same "the pipes stay" edit as a paste.
    // A selection that covers whole tables is ordinary text instead (SelectionCoversWholeTables),
    // so the host's own replacement removes those lines.
    internal static CommandResult ClearCellSelection(TextDocument document, int selectionStart, int selectionLength, List<MarkdownLivePreview.Cell> cells) {
        if (SelectionCoversWholeTables(document, selectionStart, selectionLength)) return null;
        return PasteInCell(document, selectionStart, selectionLength, string.Empty, cells);
    }

    // The host's Delete/Backspace over a selection that covers whole tables: remove exactly the
    // selected text here instead of handing the key to the editor. The live press resume runs
    // MarkdownLivePreview.NormalizeTableCaret before the editor's command, and it pulls a
    // selection endpoint that sits at a row's line end into that row's last cell; the editor
    // then sees only part of the table and the cell rules clear cells (rule c) instead of
    // deleting the lines (rule b). Null when the selection does not cover whole tables, so the
    // partial rules and the ordinary editor path stay unchanged.
    internal static CommandResult DeleteWholeTableSelection(TextDocument document, int selectionStart, int selectionLength) {
        if (!SelectionCoversWholeTables(document, selectionStart, selectionLength)) return null;
        return DeleteRange(selectionStart, selectionLength);
    }

    // A selection that covers whole tables is ordinary text: Delete, Backspace, Cut, typing and a
    // paste replace it as one block, exactly like any other text selection, instead of being
    // caught by the cell-clearing or delimiter rules. A table counts as covered only when the
    // selection runs from the start of its first line to the end of its last line, so a selection
    // that touches only part of a table (a header with its delimiter row, some of its rows, a
    // partial delimiter row) keeps the table rules. Only the selection's own first and last lines
    // are examined, so a selection inside one table costs O(that table) and never scans the
    // document; a selection of the whole document is answered without any scan.
    internal static bool SelectionCoversWholeTables(TextDocument document, int selectionStart, int selectionLength) {
        if (document == null || selectionLength <= 0) return false;
        if (selectionStart < 0 || selectionStart > document.TextLength - selectionLength) return false;
        int selectionEnd = selectionStart + selectionLength;
        // Ctrl+A (or any selection of the whole document) is the editor's own replacement even
        // when the document is nothing but a table: the table rules must not keep their pipes.
        if (selectionStart == 0 && selectionEnd >= document.TextLength) return true;
        if (!TableEdgeSelectedWhole(document, selectionStart, selectionStart, selectionEnd)) return false;
        return TableEdgeSelectedWhole(document, selectionEnd - 1, selectionStart, selectionEnd);
    }

    // True when the line at offset belongs to no table, or to a table the [start, end] range
    // covers from its first line's start to its last line's end. A pipe row this scan cannot
    // place (its table's edges lie past the row cap) is not called "no table": the table rules
    // stay, so the selection is never handed to a raw edit that would delete a "|".
    static bool TableEdgeSelectedWhole(TextDocument document, int offset, int selectionStart, int selectionEnd) {
        if (offset < 0) offset = 0;
        if (offset > document.TextLength) offset = document.TextLength;
        DocumentLine line = document.GetLineByOffset(offset);
        if (!IsTableRow(LineText(document, line))) return true;
        int firstLine, lastLine;
        bool truncated;
        if (!FindTable(document, line.Offset, out firstLine, out lastLine, out truncated)) return !truncated;
        if (truncated) return false;
        DocumentLine first = document.GetLineByNumber(firstLine);
        DocumentLine last = document.GetLineByNumber(lastLine);
        return selectionStart <= first.Offset && selectionEnd >= last.EndOffset;
    }

    // True when a selection reaches across a cell boundary of its row, which is exactly when an
    // edit would delete a "|" (the basic view's table cannot lose a cell that way).
    internal static bool SpansCellBoundary(TextDocument document, int selectionStart, int selectionLength, List<MarkdownLivePreview.Cell> cells) {
        if (document == null || cells == null || cells.Count == 0 || selectionLength <= 0) return false;
        if (selectionStart < 0 || selectionStart > document.TextLength) return false;
        if (selectionLength > document.TextLength - selectionStart) return false;
        int selectionEnd = selectionStart + selectionLength;
        DocumentLine line = document.GetLineByOffset(selectionStart);
        if (document.GetLineByOffset(Math.Min(selectionEnd, document.TextLength)).LineNumber != line.LineNumber) return true;
        int first = selectionStart - line.Offset;
        int last = selectionEnd - line.Offset;
        if (first < cells[0].Start || last > cells[cells.Count - 1].End) return true;
        return CellAt(cells, first) != CellAt(cells, last);
    }

    // Every selected cell's interior is emptied in one result (the pipes stay), and the insert
    // goes to the first selected cell's start, which is where the basic view puts the paragraph
    // break. Rows between the endpoints join in, exactly like the basic view's table selection.
    static CommandResult CellSelectionEdit(TextDocument document, int selectionStart, int selectionLength, string insert) {
        int firstLine, lastLine;
        if (!FindTable(document, selectionStart, out firstLine, out lastLine)) return null;
        if (MarkdownStructuralInput.IsInsideFence(document, document.GetLineByOffset(selectionStart))) return null;
        int selectionEnd = selectionStart + selectionLength;
        DocumentLine lastRow = document.GetLineByNumber(lastLine);
        if (selectionEnd > lastRow.Offset + lastRow.Length) return null;
        int anchor = -1;
        CommandResult result = new CommandResult();
        for (int number = firstLine; number <= lastLine; number++) {
            if (number == firstLine + 1) continue;   /* the delimiter row carries no cell text */
            DocumentLine line = document.GetLineByNumber(number);
            if (line.Offset + line.Length < selectionStart || line.Offset > selectionEnd) continue;
            List<MarkdownLivePreview.Cell> cells = MarkdownLivePreview.SplitCells(LineText(document, line));
            if (cells.Count == 0) continue;
            if (anchor < 0) anchor = line.Offset + ClampIntoCell(cells, Math.Max(0, selectionStart - line.Offset));
            for (int c = 0; c < cells.Count; c++) {
                int a = Math.Max(selectionStart, line.Offset + cells[c].Start);
                int b = Math.Min(selectionEnd, line.Offset + cells[c].End);
                if (b <= a) continue;
                result.Add(a, b - a, string.Empty);
            }
        }
        if (anchor < 0) return null;
        if (insert != null && insert.Length > 0) {
            result.Add(anchor, 0, insert);
            result.SelectionStart = anchor + insert.Length;
        } else result.SelectionStart = anchor;
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }

    // Backspace never crosses a cell boundary: at a cell's start the caret moves to the end of
    // the previous cell without deleting anything, and the row's own start is swallowed so a
    // table line is never joined with the line above. A hidden cell "<br>" is one unit, so the
    // whole tag goes instead of one of its characters.
    internal static CommandResult BackspaceInCell(TextDocument document, int caretOffset, List<MarkdownLivePreview.Cell> cells) {
        if (document == null || cells == null || cells.Count == 0) return null;
        if (caretOffset < 0 || caretOffset > document.TextLength) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        string text = LineText(document, line);
        int relative = caretOffset - line.Offset;
        if (relative < 0 || relative > text.Length) return null;
        int breakStart = CellBreakBefore(text, relative);
        if (breakStart >= 0) return DeleteRange(line.Offset + breakStart, relative - breakStart);
        // A padding-only cell is all padding around its own caret stop (EmptyCellStop). Backspace
        // there would eat the left padding space (leaving "|  x|" or "| x|") or step out of the
        // cell the user is about to type into, so the key writes nothing and the caret stays,
        // exactly like the cell wall at a text cell's start. The closing pipe keeps the wall rule
        // below, and a text cell's padding stays the editor's own character deletion.
        if (PaddingOnlyCellAt(cells, text, relative) >= 0) return CaretOnly(caretOffset);
        // A caret in a cell's own trailing padding (only spaces or tabs between that cell's last
        // text character and the caret, no pipe) sits on text the writer typed. The cell rule
        // must not swallow the key there: the editor's own Backspace removes the space. The last
        // cell's padding is the same spot, so this check runs before the row-end clamp below; a
        // run that reaches a real pipe keeps the caret-only step.
        int paddingCell = PaddingCellAt(cells, relative);
        if (paddingCell >= 0 && relative > cells[paddingCell].End && IsBlankRange(text, cells[paddingCell].End, relative)) return null;
        if (relative > cells[cells.Count - 1].End) return CaretOnly(line.Offset + cells[cells.Count - 1].End);
        // A caret in the row's hidden leading run is the row's start: at column 0 the key is
        // swallowed where it is (the row never joins the line above), an indent stays the
        // structural rule's, and the rest steps to the first cell's text.
        if (relative < cells[0].Start) {
            if (relative == 0) return CaretOnly(caretOffset);
            if (relative <= LeadingIndentLength(text)) return null;
            return CaretOnly(line.Offset + cells[0].Start);
        }
        int cell = CellAt(cells, relative);
        if (relative <= cells[cell].Start) {
            if (cell > 0) return CaretOnly(line.Offset + cells[cell - 1].End);
            if (relative == 0) return CaretOnly(caretOffset);
            // Inside the row's own indentation (a table in a list item): the structural rule
            // removes one indent unit there, which is the basic view's behaviour as well.
            if (relative <= LeadingIndentLength(text)) return null;
            return CaretOnly(caretOffset);
        }
        return null;
    }

    // Delete stays inside the cell as well: at a cell's own end it does nothing (the caret stays),
    // so no "|" and no line break is removed. A hidden cell "<br>" in front of the caret goes as
    // one unit.
    internal static CommandResult DeleteInCell(TextDocument document, int caretOffset, List<MarkdownLivePreview.Cell> cells) {
        if (document == null || cells == null || cells.Count == 0) return null;
        if (caretOffset < 0 || caretOffset > document.TextLength) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        string text = LineText(document, line);
        int relative = caretOffset - line.Offset;
        if (relative < 0 || relative > text.Length) return null;
        int breakEnd = CellBreakEnd(text, relative);
        if (breakEnd > 0) return DeleteRange(caretOffset, breakEnd - relative);
        if (relative >= text.Length) return CaretOnly(caretOffset);
        // Only a pipe the renderer reads as a column separator is a wall. A pipe inside a `code`
        // span or an escaped "\|" is cell text, so the ordinary delete removes it (the same
        // separator list MarkdownLivePreview.TablePipeDelete reads).
        if (text[relative] == '|' && IsSeparatorPipe(text, relative)) return CaretOnly(caretOffset);
        // The row's hidden leading run: the indent is the editor's own text, the rest steps
        // into the first cell.
        if (relative < cells[0].Start) {
            if (relative <= LeadingIndentLength(text)) return null;
            return CaretOnly(line.Offset + cells[0].Start);
        }
        // A cell end is a wall: Delete never steps into the next cell. The padding between a
        // cell's text and the following "|" belongs to that same cell (a typed space there, and
        // the offset a delete left behind, stay inside it), so every spot from the cell's last
        // text character through its padding and separator does nothing. Stepping into the
        // neighbour from there made the first Delete a no-edit caret move, so the next Delete ate
        // the neighbour's first character.
        int cell = PaddingCellAt(cells, relative);
        if (cell >= 0 && relative >= cells[cell].End) return CaretOnly(caretOffset);
        return null;
    }

    // The padding-only cell whose padding holds the offset, or -1. The cell's opening pipe is the
    // boundary: its stop and every padding space after that pipe are inside it, while the pipe
    // itself is the cell end (the delete wall). A cell with text never answers here.
    static int PaddingOnlyCellAt(List<MarkdownLivePreview.Cell> cells, string text, int relative) {
        for (int c = 0; c < cells.Count; c++) {
            if (cells[c].Start != cells[c].End) continue;
            if (relative >= cells[c].End) continue;
            int open = cells[c].Start - 1;
            while (open >= 0 && (text[open] == ' ' || text[open] == '\t')) open--;
            if (open < 0 || text[open] != '|') continue;
            if (relative > open) return c;
        }
        return -1;
    }

    // The cell a row offset belongs to for the delete wall: a cell's own text and the padding
    // between its text and the following pipe are that cell's, so the gap after cell c answers c.
    // The run in front of the first cell's text belongs to no cell (-1); the caller keeps its own
    // rule for it.
    static int PaddingCellAt(List<MarkdownLivePreview.Cell> cells, int relative) {
        int cell = -1;
        for (int c = 0; c < cells.Count; c++) {
            if (relative < cells[c].Start) break;
            cell = c;
        }
        return cell;
    }
    #endregion

    #region TableGapKeys
    // Word-like keys at the paragraph next to a table. Backspace at the start of the line under
    // a table must not delete the blank separator (the next Backspace would then merge the
    // paragraph into the last row), and Delete at the end of a line above a table must not
    // merge the header row: both only move the caret into the table's own cell. A single blank
    // line between the paragraph and the row is skipped, so the same rule covers the paragraph
    // that already touches the table. Two or more blank lines stay ordinary text.
    internal static CommandResult BackspaceBeforeTable(TextDocument document, int caretOffset) {
        if (document == null || caretOffset <= 0 || caretOffset > document.TextLength) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        if (caretOffset != line.Offset) return null;
        // A piped paragraph is not a table row: only a line that belongs to a table is left to
        // the cell rule. Joining a detached pipe line here would move it directly under the
        // last row, where the renderer reads it as one more row (the block grows by the join).
        if (IsTableRow(LineText(document, line))) {
            int ownFirst, ownLast;
            if (FindTable(document, line.Offset, out ownFirst, out ownLast)) return null;
        }
        DocumentLine row = line.PreviousLine;
        if (row == null) return null;
        if (IsBlankText(LineText(document, row))) {
            row = row.PreviousLine;
            if (row == null || IsBlankText(LineText(document, row))) return null;
        }
        if (!IsTableRow(LineText(document, row)) || IsDelimiterRow(document, row)) return null;
        int firstLine, lastLine;
        if (!FindTable(document, row.Offset, out firstLine, out lastLine)) return null;
        if (row.LineNumber != lastLine) return null;
        if (MarkdownStructuralInput.IsInsideFence(document, row)) return null;
        string text = LineText(document, row);
        RowCells cells = SplitRow(text);
        if (cells.Count == 0) return null;
        int start = cells.Starts[cells.Count - 1];
        int end = cells.Ends[cells.Count - 1];
        return CaretOnly(row.Offset + TrimEnd(text, start, end));
    }

    // Delete at the end of a line whose next non-blank line is a table header: nothing is
    // deleted, the caret goes to the first header cell's text start.
    internal static CommandResult DeleteBeforeTable(TextDocument document, int caretOffset) {
        if (document == null || caretOffset < 0 || caretOffset > document.TextLength) return null;
        DocumentLine line = document.GetLineByOffset(caretOffset);
        if (caretOffset != line.Offset + line.Length) return null;
        if (IsTableRow(LineText(document, line))) return null;
        DocumentLine row = line.NextLine;
        if (row == null) return null;
        if (IsBlankText(LineText(document, row))) {
            row = row.NextLine;
            if (row == null || IsBlankText(LineText(document, row))) return null;
        }
        if (!IsTableRow(LineText(document, row)) || IsDelimiterRow(document, row)) return null;
        int firstLine, lastLine;
        if (!FindTable(document, row.Offset, out firstLine, out lastLine)) return null;
        if (row.LineNumber != firstLine) return null;
        if (MarkdownStructuralInput.IsInsideFence(document, row)) return null;
        string text = LineText(document, row);
        RowCells cells = SplitRow(text);
        if (cells.Count == 0) return null;
        int start = cells.Starts[0];
        int end = cells.Ends[0];
        while (start < end && (text[start] == ' ' || text[start] == '\t')) start++;
        return CaretOnly(row.Offset + start);
    }

    static int TrimEnd(string text, int start, int end) {
        while (end > start && (text[end - 1] == ' ' || text[end - 1] == '\t')) end--;
        while (start < end && (text[start] == ' ' || text[start] == '\t')) start++;
        return end;
    }

    // True when every character of the [from, to) range is a space or a tab.
    static bool IsBlankRange(string text, int from, int to) {
        if (from < 0) from = 0;
        if (to > text.Length) to = text.Length;
        for (int i = from; i < to; i++) if (text[i] != ' ' && text[i] != '\t') return false;
        return true;
    }
    #endregion

    #region AlignColumn
    // 0 left (:---), 1 center (:---:), 2 right (---:), 3 none (---). Only the delimiter
    // cell's core is rewritten; surrounding spaces stay and the dash count is kept.
    internal static CommandResult AlignColumn(TextDocument document, int caretOffset, int alignment) {
        int firstLine, lastLine;
        if (!FindTable(document, caretOffset, out firstLine, out lastLine)) return null;
        if (alignment < 0 || alignment > 3) return null;
        int column = CellIndexAt(document, caretOffset);
        DocumentLine line = document.GetLineByNumber(firstLine + 1);
        string text = LineText(document, line);
        RowCells cells = SplitRow(text);
        if (cells.Count < 1) return null;
        if (column >= cells.Count) column = cells.Count - 1;
        int coreStart = cells.Starts[column];
        int coreEnd = cells.Ends[column];
        while (coreStart < coreEnd && char.IsWhiteSpace(text[coreStart])) coreStart++;
        while (coreEnd > coreStart && char.IsWhiteSpace(text[coreEnd - 1])) coreEnd--;
        int dashStart = coreStart;
        int dashEnd = coreEnd;
        if (dashStart < dashEnd && text[dashStart] == ':') dashStart++;
        if (dashEnd > dashStart && text[dashEnd - 1] == ':') dashEnd--;
        int dashes = 0;
        if (dashEnd > dashStart) {
            bool valid = true;
            for (int i = dashStart; i < dashEnd; i++) if (text[i] != '-') { valid = false; break; }
            if (valid) dashes = dashEnd - dashStart;
        }
        if (dashes < 3) dashes = 3;
        string core;
        if (alignment == 0) core = ":" + new string('-', dashes);
        else if (alignment == 1) core = ":" + new string('-', dashes) + ":";
        else if (alignment == 2) core = new string('-', dashes) + ":";
        else core = new string('-', dashes);
        CommandResult result = new CommandResult();
        result.Add(line.Offset + coreStart, coreEnd - coreStart, core);
        // G6: the delimiter edit sits on the row below the header, so a caret on the header line is
        // before it and a caret on a body row is after it. Mapping the caret through the edit keeps
        // it at the same position inside its own row instead of sliding by the edit's length.
        result.SelectionStart = MapTablePosition(result.Edits, caretOffset, true);
        result.SelectionLength = 0;
        result.SortEdits();
        return result;
    }
    #endregion
}
}
