using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// --table-commands-probe <outDir>: pure MarkdownTableCommands checks. Every case builds a
// fresh TextDocument, applies the returned edits in list order (the list is sorted
// descending, exactly like the host) and compares text and caret with the marked
// expectation. Helper checks call FindTable and CellIndexAt directly.
internal static class MarkdownTableCommandsTests {
    const char CaretMark = '‸';

    internal static int Run(string outDir) {
        string dir;
        try {
            dir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(dir);
        } catch (Exception) {
            return 2;
        }
        StringBuilder report = new StringBuilder();
        report.Append("# MarkdownTableCommands probe\n\n");
        bool all = true;
        int passed = 0;
        int failed = 0;
        int cases = 0;
        int exit = 0;
        try {
            List<TableCase> list = BuildCases();
            for (int i = 0; i < list.Count; i++) {
                cases++;
                string detail;
                if (RunCase(list[i], out detail)) {
                    passed++;
                    report.Append("PASS ").Append(list[i].Name).Append('\n');
                } else {
                    failed++;
                    all = false;
                    report.Append("FAIL ").Append(list[i].Name);
                    if (detail != null) report.Append(": ").Append(detail);
                    report.Append('\n');
                }
            }
            List<HelperCase> helpers = BuildHelperCases();
            for (int i = 0; i < helpers.Count; i++) {
                cases++;
                string detail;
                if (RunHelperCase(helpers[i], out detail)) {
                    passed++;
                    report.Append("PASS ").Append(helpers[i].Name).Append('\n');
                } else {
                    failed++;
                    all = false;
                    report.Append("FAIL ").Append(helpers[i].Name);
                    if (detail != null) report.Append(": ").Append(detail);
                    report.Append('\n');
                }
            }
            List<InverseCase> inverses = BuildInverseCases();
            for (int i = 0; i < inverses.Count; i++) {
                cases++;
                string detail;
                if (RunInverseCase(inverses[i], out detail)) {
                    passed++;
                    report.Append("PASS ").Append(inverses[i].Name).Append('\n');
                } else {
                    failed++;
                    all = false;
                    report.Append("FAIL ").Append(inverses[i].Name);
                    if (detail != null) report.Append(": ").Append(detail);
                    report.Append('\n');
                }
            }
            // Stage 4: selections inside/through cells need a selection length the caret marker
            // cannot carry, so they run through their own list.
            List<CellSelectionCase> selections = BuildCellSelectionCases();
            for (int i = 0; i < selections.Count; i++) {
                cases++;
                string detail;
                if (RunCellSelectionCase(selections[i], out detail)) {
                    passed++;
                    report.Append("PASS ").Append(selections[i].Name).Append('\n');
                } else {
                    failed++;
                    all = false;
                    report.Append("FAIL ").Append(selections[i].Name);
                    if (detail != null) report.Append(": ").Append(detail);
                    report.Append('\n');
                }
            }
            // The rich-view cases need a real MainWindow (RichTextBox, table tool row, size box),
            // so they run on one offscreen self-test window exactly like the other UI probes.
            List<RichCase> rich = BuildRichCases();
            if (rich.Count > 0) {
                MainWindow window = null;
                try {
                    if (System.Windows.Application.Current == null) new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 };
                    window.Show();
                    window.Left = -20000;
                    for (int i = 0; i < rich.Count; i++) {
                        cases++;
                        string detail;
                        try {
                            detail = rich[i].Run(window);
                        } catch (Exception ex) {
                            detail = ex.GetType().Name + ": " + ex.Message;
                        }
                        if (detail == null) {
                            passed++;
                            report.Append("PASS ").Append(rich[i].Name).Append('\n');
                        } else {
                            failed++;
                            all = false;
                            report.Append("FAIL ").Append(rich[i].Name).Append(": ").Append(detail).Append('\n');
                        }
                    }
                } finally {
                    if (window != null) {
                        window.MarkProbeWindowClean();
                        window.Close();
                    }
                }
            }
        } catch (Exception ex) {
            exit = 2;
            all = false;
            report.Append("EXCEPTION ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
            report.Append(ex.StackTrace).Append('\n');
        } finally {
            report.Append("\nCases: ").Append(cases.ToString(CultureInfo.InvariantCulture))
                .Append("  PASS: ").Append(passed.ToString(CultureInfo.InvariantCulture))
                .Append("  FAIL: ").Append(failed.ToString(CultureInfo.InvariantCulture)).Append('\n');
            report.Append("Result: ").Append(exit == 0 && all ? "PASS" : "FAIL").Append('\n');
            try {
                File.WriteAllText(Path.Combine(dir, "table-commands-probe.txt"), report.ToString(), new UTF8Encoding(false));
                string passFile = Path.Combine(dir, "table-commands-probe-PASS.txt");
                if (exit == 0 && all) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
                else if (File.Exists(passFile)) File.Delete(passFile);
            } catch (Exception) {
                exit = 2;
            }
        }
        if (exit == 0 && !all) exit = 1;
        return exit;
    }

    sealed class TableCase {
        internal string Name;
        internal string Input;
        internal string Expected;   // null means the command must return null
        internal int ExpectedLength;   // 0 for a caret; the table insert selects its first header cell
        internal Func<TextDocument, int, CommandResult> Command;
    }

    sealed class HelperCase {
        internal string Name;
        internal string Input;
        internal string Expected;
        internal Func<TextDocument, int, string> Check;
    }

    // Indent then outdent over one edit list each: the document has to come back byte-identical,
    // which is the "outdent restores the original text exactly" contract.
    sealed class InverseCase {
        internal string Name;
        internal string First;
        internal string Second;
    }

    // A cell selection: the marker is the selection start, Length the selection length, and
    // Insert null means EnterInCell, empty means ClearCellSelection, anything else PasteInCell.
    sealed class CellSelectionCase {
        internal string Name;
        internal string Input;
        internal int Length;
        internal string Insert;
        internal string Expected;
    }

    sealed class RichCase {
        internal string Name;
        internal Func<MainWindow, string> Run;
    }

    static void AddRich(List<RichCase> cases, string name, Func<MainWindow, string> run) {
        RichCase item = new RichCase();
        item.Name = name;
        item.Run = run;
        cases.Add(item);
    }

    static void Add(List<TableCase> cases, string name, string input, string expected,
        Func<TextDocument, int, CommandResult> command) {
        Add(cases, name, input, expected, command, 0);
    }

    static void Add(List<TableCase> cases, string name, string input, string expected,
        Func<TextDocument, int, CommandResult> command, int expectedSelectionLength) {
        TableCase item = new TableCase();
        item.Name = name;
        item.Input = input;
        item.Expected = expected;
        item.ExpectedLength = expectedSelectionLength;
        item.Command = command;
        cases.Add(item);
    }

    static void AddHelper(List<HelperCase> cases, string name, string input, string expected,
        Func<TextDocument, int, string> check) {
        HelperCase item = new HelperCase();
        item.Name = name;
        item.Input = input;
        item.Expected = expected;
        item.Check = check;
        cases.Add(item);
    }

    static Func<TextDocument, int, CommandResult> AddRow() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.AddRowBelow(document, caret);
        };
    }

    static Func<TextDocument, int, CommandResult> AddRowAtEndCommand() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.AddRowAtEnd(document, caret);
        };
    }

    static Func<TextDocument, int, CommandResult> RemoveRowCommand() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.RemoveRow(document, caret);
        };
    }

    static Func<TextDocument, int, CommandResult> AddColumn() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.AddColumnRight(document, caret);
        };
    }

    static Func<TextDocument, int, CommandResult> RemoveColumnCommand() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.RemoveColumn(document, caret);
        };
    }

    static Func<TextDocument, int, CommandResult> DeleteTableCommand() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.DeleteTable(document, caret);
        };
    }

    static Func<TextDocument, int, CommandResult> ParagraphAfterCommand() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.ParagraphAfter(document, caret);
        };
    }

    static Func<TextDocument, int, CommandResult> Align(int alignment) {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.AlignColumn(document, caret, alignment);
        };
    }

    // The table button's grid insert, with the body-row count MarkdownCommands.InsertTable takes
    // (the app's row count includes the header row).
    static Func<TextDocument, int, CommandResult> InsertTable(int bodyRows, int columns) {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.InsertTableSafely(document, caret, bodyRows, columns);
        };
    }

    // The same insert with the host's localized header word (T("제목") in the live host), so the
    // grid written next to a table uses the same word the plain insert uses.
    static Func<TextDocument, int, CommandResult> InsertTableLocalized(int bodyRows, int columns, string header) {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.InsertTableSafely(document, caret, bodyRows, columns, header);
        };
    }

    static Func<TextDocument, int, CommandResult> EnterOutside() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.EnterOutsideTable(document, caret, 0);
        };
    }

    static Func<TextDocument, int, CommandResult> IndentTableCommand() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.IndentTable(document, caret, 0);
        };
    }

    static Func<TextDocument, int, CommandResult> OutdentTableCommand() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.OutdentTable(document, caret, 0);
        };
    }

    // The two button states and their tooltips, read exactly the way the tool row reads them.
    static Func<TextDocument, int, string> TableLevelStateCheck() {
        return delegate(TextDocument document, int caret) {
            bool canIndent, canOutdent;
            string indentTip, outdentTip;
            MarkdownTableCommands.TableLevelState(document, caret, out canIndent, out canOutdent, out indentTip, out outdentTip);
            return (canIndent ? "indent" : "no-indent") + " " + (canOutdent ? "outdent" : "no-outdent")
                + " | " + (indentTip ?? "-") + " | " + (outdentTip ?? "-");
        };
    }

    // A table run that a list item indents is still a table for the live parser: every line of the
    // run has the table state, and the second line is the delimiter row.
    static Func<TextDocument, int, string> LiveTableCheck() {
        return delegate(TextDocument document, int caret) {
            int first, last;
            if (!MarkdownTableCommands.FindTable(document, caret, out first, out last)) return "no-table";
            // The same state the live preview's block scan derives: a pipe row whose next line is a
            // delimiter row opens a table, and every following pipe row belongs to it.
            int rows = 0;
            for (int n = first; n <= last; n++) {
                string text = document.GetText(document.GetLineByNumber(n));
                if (text.TrimStart().StartsWith("|", StringComparison.Ordinal)) rows++;
            }
            return "table " + first.ToString(CultureInfo.InvariantCulture) + " " + last.ToString(CultureInfo.InvariantCulture)
                + " rows=" + rows.ToString(CultureInfo.InvariantCulture);
        };
    }

    // The basic view parses the same source through Markdown.Parse: the indented lines still have to
    // come back as a Table block (a table is recognised however far its lines are indented, so the
    // indent that moves it under an item never turns it into anything else).
    static Func<TextDocument, int, string> BasicParseCheck() {
        return delegate(TextDocument document, int caret) {
            FlowDocument parsed = Markdown.Parse(document.Text, 16, "Malgun Gothic", System.Windows.Media.Brushes.Black, false, "");
            int tables = 0;
            foreach (Block block in parsed.Blocks) if (block is Table) tables++;
            return "tables=" + tables.ToString(CultureInfo.InvariantCulture);
        };
    }

    // Markdown.Cells feeds the basic view's table columns. A wrong split shows up as a wider
    // header: a pipe inside a `code` span or after an even backslash run is cell text, not a
    // column boundary, so the check reports the column count the print parser built.
    static Func<TextDocument, int, string> BasicTableColumnsCheck() {
        return delegate(TextDocument document, int caret) {
            FlowDocument parsed = Markdown.Parse(document.Text, 16, "Malgun Gothic", System.Windows.Media.Brushes.Black, false, "");
            foreach (Block block in parsed.Blocks) {
                Table table = block as Table;
                if (table != null) return "cols=" + table.Columns.Count.ToString(CultureInfo.InvariantCulture);
            }
            return "no-table";
        };
    }

    static Func<TextDocument, int, string> FindTableCheck() {
        return delegate(TextDocument document, int caret) {
            int firstLine, lastLine;
            bool found = MarkdownTableCommands.FindTable(document, caret, out firstLine, out lastLine);
            if (!found) return "false";
            return "true " + firstLine.ToString(CultureInfo.InvariantCulture)
                + " " + lastLine.ToString(CultureInfo.InvariantCulture);
        };
    }

    // A cell selection at the depth of a long table is still a cell edit: the "|"s and the row
    // stay. An extent scan that gave up on this row would call it "no table", the selection would
    // count as whole-table text, and the host's own delete would take the separator pipes
    // (bughunt3 tables item 2).
    static Func<TextDocument, int, string> DeepCellClearCheck(int selectionLength) {
        return delegate(TextDocument document, int caret) {
            List<MarkdownLivePreview.Cell> cells = MarkdownTableCommands.CellsAt(document, caret);
            CommandResult result = MarkdownTableCommands.ClearCellSelection(document, caret, selectionLength, cells);
            if (result == null) return "null";
            string invalid = ValidateEdits(document.TextLength, result);
            if (invalid != null) return invalid;
            TextDocument edited = new TextDocument(document.Text);
            for (int i = 0; i < result.Edits.Count; i++) {
                SourceEdit edit = result.Edits[i];
                edited.Replace(edit.Offset, edit.Length, edit.Text);
            }
            if (edited.LineCount != document.LineCount) {
                return "rows " + edited.LineCount.ToString(CultureInfo.InvariantCulture);
            }
            return "row=[" + Escape(edited.GetText(edited.GetLineByOffset(caret))) + "]";
        };
    }

    static Func<TextDocument, int, string> CellIndexCheck() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.CellIndexAt(document, caret).ToString(CultureInfo.InvariantCulture);
        };
    }

    static Func<TextDocument, int, string> DelimiterAlignmentCheck(int column) {
        return delegate(TextDocument document, int caret) {
            return MainWindow.DelimiterAlignment(document.Text, column).ToString(CultureInfo.InvariantCulture);
        };
    }

    // Delete at a cell's end is a wall: three presses must leave the row text and the caret
    // exactly as they were, so no press can step into the neighbour and eat its first character.
    static Func<TextDocument, int, string> CellEndDeleteRepeatCheck() {
        return delegate(TextDocument document, int caret) {
            TextDocument current = new TextDocument(document.Text);
            int at = caret;
            for (int press = 1; press <= 3; press++) {
                CommandResult result = MarkdownTableCommands.DeleteInCell(current, at, MarkdownTableCommands.CellsAt(current, at));
                if (result == null) return "press " + press.ToString(CultureInfo.InvariantCulture) + " returned null";
                string invalid = ValidateEdits(current.TextLength, result);
                if (invalid != null) return "press " + press.ToString(CultureInfo.InvariantCulture) + ": " + invalid;
                for (int i = 0; i < result.Edits.Count; i++) {
                    SourceEdit edit = result.Edits[i];
                    current.Replace(edit.Offset, edit.Length, edit.Text);
                }
                at = result.SelectionStart;
            }
            if (current.Text == document.Text && at == caret) return "same";
            int lineAt = Math.Max(0, Math.Min(at, current.TextLength));
            return "row=[" + Escape(current.GetText(current.GetLineByOffset(lineAt))) + "] caret="
                + at.ToString(CultureInfo.InvariantCulture) + "/" + caret.ToString(CultureInfo.InvariantCulture);
        };
    }

    static void ParseMarked(string marked, out string text, out int caret) {
        int at = marked.IndexOf(CaretMark);
        if (at < 0) throw new InvalidOperationException("missing caret marker: " + Escape(marked));
        text = marked.Remove(at, 1);
        caret = at;
    }

    static bool RunCase(TableCase test, out string detail) {
        string input;
        int caret;
        ParseMarked(test.Input, out input, out caret);
        if (test.Expected == null) {
            CommandResult nil = test.Command(new TextDocument(input), caret);
            detail = nil == null ? null : "expected null, got " + nil.Edits.Count.ToString(CultureInfo.InvariantCulture) + " edit(s)";
            return nil == null;
        }
        string expected;
        int expectedCaret;
        ParseMarked(test.Expected, out expected, out expectedCaret);
        CommandResult result = test.Command(new TextDocument(input), caret);
        if (result == null) {
            detail = "command returned null";
            return false;
        }
        string invalid = ValidateEdits(input.Length, result);
        if (invalid != null) {
            detail = invalid;
            return false;
        }
        TextDocument edited = new TextDocument(input);
        for (int i = 0; i < result.Edits.Count; i++) {
            SourceEdit edit = result.Edits[i];
            edited.Replace(edit.Offset, edit.Length, edit.Text);
        }
        string actual = edited.Text;
        if (actual != expected) {
            detail = "text expected=\"" + Escape(expected) + "\" actual=\"" + Escape(actual) + "\"";
            return false;
        }
        if (result.SelectionStart != expectedCaret || result.SelectionLength != test.ExpectedLength) {
            detail = "caret expected=" + expectedCaret.ToString(CultureInfo.InvariantCulture)
                + "+" + test.ExpectedLength.ToString(CultureInfo.InvariantCulture)
                + " actual=" + result.SelectionStart.ToString(CultureInfo.InvariantCulture)
                + "+" + result.SelectionLength.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        detail = null;
        return true;
    }

    static bool RunHelperCase(HelperCase test, out string detail) {
        string input;
        int caret;
        ParseMarked(test.Input, out input, out caret);
        string actual = test.Check(new TextDocument(input), caret);
        if (actual != test.Expected) {
            detail = "expected \"" + test.Expected + "\" actual \"" + actual + "\"";
            return false;
        }
        detail = null;
        return true;
    }

    static string ValidateEdits(int textLength, CommandResult result) {
        int limit = int.MaxValue;
        for (int i = 0; i < result.Edits.Count; i++) {
            SourceEdit edit = result.Edits[i];
            if (edit.Offset < 0 || edit.Length < 0 || edit.Offset + edit.Length > textLength)
                return "edit out of range offset=" + edit.Offset + " length=" + edit.Length;
            if (edit.Offset + edit.Length > limit)
                return "edits overlap or are not sorted descending at index " + i;
            limit = edit.Offset;
        }
        return null;
    }

    static string Escape(string text) {
        return text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    static List<InverseCase> BuildInverseCases() {
        List<InverseCase> cases = new List<InverseCase>();
        // Level-1 cases: indent then outdent is the exact same text. A table under a nested item is
        // deliberately not one step back (indent attaches it to the deepest item above, outdent
        // takes one level off), so that pair is checked separately below.
        AddInverse(cases, "inverse bullet item CRLF", "- item\r\n| a | b |\r\n| --- | --- |\r\n\r\nnext\r\n");
        AddInverse(cases, "inverse ordered item", "1. item\n| a |\n| --- |\n\nnext\n");
        AddInverse(cases, "inverse task item", "- [x] item\n| a |\n| --- |\n");
        AddInverse(cases, "inverse table with a blank line under it", "- item\n| a |\n| --- |\n\nnext\n");
        AddInverse(cases, "inverse table at the document end", "- item\n| a |\n| --- |");
        return cases;
    }

    static void AddInverse(List<InverseCase> cases, string name, string text) {
        InverseCase item = new InverseCase();
        item.Name = name;
        item.First = text;
        cases.Add(item);
    }

    // Indent then outdent over one applied edit list each. The text must come back exactly, the
    // caret must map through both steps, and each step must be a single non-overlapping edit set
    // (the host applies one set as one undo entry). The caret stays in the first table row on
    // purpose: that is where a caret sits while the user works in the table.
    static bool RunInverseCase(InverseCase test, out string detail) {
        TextDocument document = new TextDocument(test.First);
        int firstTableLine = -1;
        for (int n = 1; n <= document.LineCount; n++) {
            string text = document.GetText(document.GetLineByNumber(n));
            if (text.TrimStart().StartsWith("|", StringComparison.Ordinal)) { firstTableLine = n; break; }
        }
        if (firstTableLine < 0) { detail = "fixture has no table line"; return false; }
        int caret = document.GetLineByNumber(firstTableLine).Offset + 1;
        CommandResult indent = MarkdownTableCommands.IndentTable(document, caret, 0);
        if (indent == null) { detail = "indent returned null for a table below a list item"; return false; }
        string invalid = ValidateEdits(document.TextLength, indent);
        if (invalid != null) { detail = "indent " + invalid; return false; }
        string indented = Apply(document, indent);
        if (indented == test.First) { detail = "indent changed nothing"; return false; }
        // The indented table still has to be a table (its second line the delimiter row).
        int indentedCaret = Math.Max(0, Math.Min(indent.SelectionStart, indented.Length));
        int checkFirst, checkLast;
        if (!MarkdownTableCommands.FindTable(new TextDocument(indented), indentedCaret, out checkFirst, out checkLast)) {
            detail = "indented text is no longer a table: \"" + Escape(indented) + "\"";
            return false;
        }
        TextDocument second = new TextDocument(indented);
        CommandResult outdent = MarkdownTableCommands.OutdentTable(second, indentedCaret, 0);
        if (outdent == null) { detail = "outdent returned null on the indented table: \"" + Escape(indented) + "\""; return false; }
        invalid = ValidateEdits(second.TextLength, outdent);
        if (invalid != null) { detail = "outdent " + invalid; return false; }
        string restored = Apply(second, outdent);
        if (restored != test.First) {
            detail = "restored text differs expected=\"" + Escape(test.First) + "\" actual=\"" + Escape(restored) + "\"";
            return false;
        }
        detail = null;
        return true;
    }

    static string Apply(TextDocument document, CommandResult result) {
        TextDocument edited = new TextDocument(document.Text);
        for (int i = 0; i < result.Edits.Count; i++) {
            SourceEdit edit = result.Edits[i];
            edited.Replace(edit.Offset, edit.Length, edit.Text);
        }
        return edited.Text;
    }

    static void AddCellSelection(List<CellSelectionCase> cases, string name, string input, int length, string insert, string expected) {
        CellSelectionCase item = new CellSelectionCase();
        item.Name = name;
        item.Input = input;
        item.Length = length;
        item.Insert = insert;
        item.Expected = expected;
        cases.Add(item);
    }

    // Stage 4 cell selections: Enter (Insert null), clearing (empty insert) and a paste are the
    // three edits a cell selection can take; each must keep every "|" and the row's line.
    static List<CellSelectionCase> BuildCellSelectionCases() {
        List<CellSelectionCase> cases = new List<CellSelectionCase>();
        string table = "| a | b |\n|---|---|\n";
        AddCellSelection(cases, "enter over two cells keeps the pipes",
            table + "| ‸c | d |", 5, null,
            table + "| <br>‸ |  |");
        AddCellSelection(cases, "clear over two cells keeps the pipes",
            table + "| ‸c | d |", 5, string.Empty,
            table + "| ‸ |  |");
        AddCellSelection(cases, "paste over two cells keeps the pipes",
            table + "| ‸c | d |", 5, "Q",
            table + "| Q‸ |  |");
        AddCellSelection(cases, "paste newlines into a cell become cell breaks",
            table + "| c‸ | e |", 0, "x\ny",
            table + "| cx<br>y‸ | e |");
        AddCellSelection(cases, "paste replaces a selection inside one cell",
            table + "| c‸d | e |", 1, "1\n2",
            table + "| c1<br>2‸ | e |");
        AddCellSelection(cases, "paste when the selection leaves the table is left to the editor",
            "| a |\n|---|\n| ‸b |\nnext", 10, "x",
            null);
        // A selection that spans rows but stays inside the table is still a cell selection: every
        // touched cell's text goes, the pipes and both rows stay, and the paragraph break lands in
        // the first selected cell (the contract EnterInCell documents above its multi-line
        // branch; the old "left to the editor" expectation no longer matched it).
        AddCellSelection(cases, "enter with a multi-row selection clears the cells and keeps the pipes",
            "| a |\n|---|\n| ‸b |\n| c |", 8, null,
            "| a |\n|---|\n| <br>‸ |\n|  |");
        AddCellSelection(cases, "paste a tab into a cell stays literal",
            table + "| c‸ | e |", 0, "a\tb",
            table + "| ca\tb‸ | e |");
        AddCellSelection(cases, "paste a pipe into a cell is escaped and adds no column",
            table + "| c‸ | e |", 0, "a | b\nx\\|y",
            table + "| ca \\| b<br>x\\|y‸ | e |");
        // R5: escaped means an ODD run of backslashes in front of the pipe. "\\|" is a literal
        // backslash and a live pipe, so that pipe gets one more backslash; a pipe pasted directly
        // after a document backslash is already escaped.
        AddCellSelection(cases, "paste a pipe after two backslashes is escaped again",
            table + "| c‸ | e |", 0, "\\\\|x",
            table + "| c\\\\\\|x‸ | e |");
        AddCellSelection(cases, "paste a pipe after a document backslash is already escaped",
            table + "| c\\‸ | e |", 0, "|x",
            table + "| c\\|x‸ | e |");
        // The run is counted whole: two document backslashes are one literal backslash and a
        // live pipe, so the pasted pipe needs its own backslash.
        AddCellSelection(cases, "paste a pipe after two document backslashes is escaped",
            table + "| c\\\\‸ | e |", 0, "|",
            table + "| c\\\\\\|‸ | e |");
        // A selection over two body rows: every touched cell's text goes, both rows and all their
        // "|"s stay, and the insert lands in the first selected cell.
        string twoRows = table + "| ‸c | d |\n| e | f |";
        AddCellSelection(cases, "clear over two rows keeps both rows",
            twoRows, 15, string.Empty,
            table + "| ‸ |  |\n|  |  |");
        AddCellSelection(cases, "paste over two rows keeps both rows",
            twoRows, 15, "Q",
            table + "| Q‸ |  |\n|  |  |");
        // H1 inside a closed `code` span: the renderer's TablePipes skips span content and
        // ParseInline draws it literally, so a pipe there is written raw (no visible "\").
        // Anywhere else in the cell, including after an unclosed backtick, it is still escaped.
        AddCellSelection(cases, "paste a pipe inside a code span stays raw",
            table + "| `a‸b` | e |", 0, "|",
            table + "| `a|‸b` | e |");
        AddCellSelection(cases, "paste a code span with a pipe keeps the pipe raw",
            table + "| c‸ | e |", 0, "`a|b`",
            table + "| c`a|b`‸ | e |");
        AddCellSelection(cases, "paste a pipe after a code span is escaped",
            table + "| `a`‸ | e |", 0, "|",
            table + "| `a`\\|‸ | e |");
        AddCellSelection(cases, "paste a pipe after an unclosed backtick is escaped",
            table + "| `a‸ | e |", 0, "|",
            table + "| `a\\|‸ | e |");
        // A drop into a cell (an external plain text drop or an in-document move) carries the same
        // payload a paste does: CRLF from another window and a "|" that must stay cell text. The
        // drop paths call PasteInCell/CellInsertText, so these two cases are the rule they inherit
        // (LiveMarkdownCommands.SourcePreviewDrop, MoveSelectionTo).
        AddCellSelection(cases, "drop text with a line break and a pipe into a cell",
            table + "| c‸ | e |", 0, "x | y\r\nz",
            table + "| cx \\| y<br>z‸ | e |");
        AddCellSelection(cases, "drop a pipe inside a code span stays raw across a line break",
            table + "| c‸ | e |", 0, "`a|b`\r\nc",
            table + "| c`a|b`<br>c‸ | e |");
        // Whole-table selections are ordinary text (SelectionCoversWholeTables): the host's own
        // replacement takes those lines, so the cell command declines. A selection that covers
        // only part of the table - the header and its delimiter row included - stays a cell edit.
        string whole = "| a | b |\n| --- | --- |\n| c | d |";
        AddCellSelection(cases, "clear over the whole table is left to the editor",
            "‸" + whole, whole.Length, string.Empty, null);
        AddCellSelection(cases, "clear over the whole document is left to the editor",
            "‸" + whole + "\n\npara", whole.Length + 6, string.Empty, null);
        AddCellSelection(cases, "clear over two whole tables is left to the editor",
            "pre\n‸| a |\n|---|\nmid\n| b |\n|---|\ntail", 27, string.Empty, null);
        AddCellSelection(cases, "clear over the header and delimiter only keeps the lines",
            "‸" + whole, 23, string.Empty, "| ‸ |  |\n| --- | --- |\n| c | d |");
        return cases;
    }

    static bool RunCellSelectionCase(CellSelectionCase test, out string detail) {
        string input;
        int caret;
        ParseMarked(test.Input, out input, out caret);
        TextDocument document = new TextDocument(input);
        List<MarkdownLivePreview.Cell> cells = MarkdownTableCommands.CellsAt(document, caret);
        CommandResult result;
        if (test.Insert == null) result = MarkdownTableCommands.EnterInCell(document, caret, test.Length, cells);
        else if (test.Insert.Length == 0) result = MarkdownTableCommands.ClearCellSelection(document, caret, test.Length, cells);
        else result = MarkdownTableCommands.PasteInCell(document, caret, test.Length, test.Insert, cells);
        if (test.Expected == null) {
            detail = result == null ? null : "expected null, got " + result.Edits.Count.ToString(CultureInfo.InvariantCulture) + " edit(s)";
            return result == null;
        }
        if (result == null) { detail = "command returned null"; return false; }
        string invalid = ValidateEdits(input.Length, result);
        if (invalid != null) { detail = invalid; return false; }
        string expected;
        int expectedCaret;
        ParseMarked(test.Expected, out expected, out expectedCaret);
        string actual = Apply(document, result);
        if (actual != expected) {
            detail = "text expected=\"" + Escape(expected) + "\" actual=\"" + Escape(actual) + "\"";
            return false;
        }
        if (result.SelectionStart != expectedCaret || result.SelectionLength != 0) {
            detail = "caret expected=" + expectedCaret.ToString(CultureInfo.InvariantCulture)
                + " actual=" + result.SelectionStart.ToString(CultureInfo.InvariantCulture)
                + "+" + result.SelectionLength.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        detail = null;
        return true;
    }

    // The caret-only cell commands, run exactly the way the host calls them: the cells of the
    // caret line are supplied by the caller (the live host reads them from its line cache).
    static Func<TextDocument, int, CommandResult> CellEnter() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.EnterInCell(document, caret, 0, MarkdownTableCommands.CellsAt(document, caret));
        };
    }

    static Func<TextDocument, int, CommandResult> CellBackspace() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.BackspaceInCell(document, caret, MarkdownTableCommands.CellsAt(document, caret));
        };
    }

    static Func<TextDocument, int, CommandResult> CellDelete() {
        return delegate(TextDocument document, int caret) {
            return MarkdownTableCommands.DeleteInCell(document, caret, MarkdownTableCommands.CellsAt(document, caret));
        };
    }

    static List<TableCase> BuildCases() {
        List<TableCase> cases = new List<TableCase>();

        // AddRowBelow: body, header, delimiter, last column, three columns, CRLF, end, one column.
        // The new row keeps the table's own cell padding, and a padding-only cell's caret stop is
        // one space inside the pipe that closes it (MarkdownTableCommands.EmptyCellStop), so the
        // next typed character lands between the cell's two spaces ("|  |" becomes "| x |").
        Add(cases, "add row below body first column",
            "| a | b |\n|---|---|\n| ‸c | d |",
            "| a | b |\n|---|---|\n| c | d |\n| ‸ |  |", AddRow());
        Add(cases, "add row below header goes after delimiter",
            "| ‸a | b |\n|---|---|\n| c | d |",
            "| a | b |\n|---|---|\n| ‸ |  |\n| c | d |", AddRow());
        Add(cases, "add row below delimiter row",
            "| a | b |\n|---|‸---|\n| c | d |",
            "| a | b |\n|---|---|\n|  | ‸ |\n| c | d |", AddRow());
        Add(cases, "add row below body last column",
            "| a | b |\n|---|---|\n| c | ‸d |",
            "| a | b |\n|---|---|\n| c | d |\n|  | ‸ |", AddRow());
        Add(cases, "add row below three columns middle",
            "| a | b | c |\n|---|---|---|\n| d | ‸e | f |",
            "| a | b | c |\n|---|---|---|\n| d | e | f |\n|  | ‸ |  |", AddRow());
        Add(cases, "add row below CRLF document",
            "| a | b |\r\n|---|---|\r\n| c | ‸d |",
            "| a | b |\r\n|---|---|\r\n| c | d |\r\n|  | ‸ |", AddRow());
        Add(cases, "add row below header at document end",
            "| ‸a | b |\n|---|---|",
            "| a | b |\n|---|---|\n| ‸ |  |", AddRow());
        Add(cases, "add row below one column",
            "| ‸a |\n|---|",
            "| a |\n|---|\n| ‸ |", AddRow());
        // G8a: the caret lands inside the new row's cell even when the caret was at the old row's
        // last offset or the new row ends the document.
        Add(cases, "add row below last row at the document end",
            "| a | b |\n|---|---|\n| c | d‸ |",
            "| a | b |\n|---|---|\n| c | d |\n|  | ‸ |", AddRow());
        Add(cases, "add row below CRLF last row at the document end",
            "| a | b |\r\n|---|---|\r\n| c | d‸ |",
            "| a | b |\r\n|---|---|\r\n| c | d |\r\n|  | ‸ |", AddRow());
        Add(cases, "add row below a body row keeps the following line",
            "| a |\n|---|\n| ‸b |\nnext\n",
            "| a |\n|---|\n| b |\n| ‸ |\nnext\n", AddRow());
        Add(cases, "add row below outside table returns null",
            "plain ‸text", null, AddRow());

        // AddRowAtEnd (Tab in the last cell of the table's last row): one padding-only row with the
        // table's own cell padding and the caret in its first cell. The header row's padding
        // decides the cell width, so a spaced table gains "|  |  |" and a compact one "|||". The
        // outer pipes are always written: a padding-only row without them is not a row the
        // renderer keeps, which is also why AddRowBelow writes them for a table without them.
        Add(cases, "add row at end of a spaced table",
            "| a | b |\n|---|---|\n| c | ‸d |",
            "| a | b |\n|---|---|\n| c | d |\n| ‸ |  |", AddRowAtEndCommand());
        Add(cases, "add row at end of a header-only table",
            "| a | ‸b |\n|---|---|",
            "| a | b |\n|---|---|\n| ‸ |  |", AddRowAtEndCommand());
        Add(cases, "add row at end of a one column table",
            "| a |\n|---|\n| ‸b |",
            "| a |\n|---|\n| b |\n| ‸ |", AddRowAtEndCommand());
        Add(cases, "add row at end of a table without outer pipes",
            "Name | Age\n--- | ---\nKim | ‸30",
            "Name | Age\n--- | ---\nKim | 30\n| ‸| |", AddRowAtEndCommand());
        Add(cases, "add row at end of a compact table",
            "|a|b|\n|-|-|\n|c|‸d|",
            "|a|b|\n|-|-|\n|c|d|\n|‸||", AddRowAtEndCommand());
        Add(cases, "add row at end CRLF document",
            "| a | b |\r\n|---|---|\r\n| c | ‸d |",
            "| a | b |\r\n|---|---|\r\n| c | d |\r\n| ‸ |  |", AddRowAtEndCommand());
        Add(cases, "add row at end at the document end without a newline",
            "| a |\n|---|\n| ‸b |",
            "| a |\n|---|\n| b |\n| ‸ |", AddRowAtEndCommand());
        Add(cases, "add row at end keeps the following line",
            "| a |\n|---|\n| ‸b |\nnext\n",
            "| a |\n|---|\n| b |\n| ‸ |\nnext\n", AddRowAtEndCommand());
        // The caret line has to be the table's last row (or the header of a header-only table).
        // The delimiter row is structure, and a header with body rows below has its next cell in
        // the row below instead.
        Add(cases, "add row at end from the delimiter row returns null",
            "| a | b |\n|---|‸---|", null, AddRowAtEndCommand());
        Add(cases, "add row at end from the header of a body table returns null",
            "| ‸a | b |\n|---|---|\n| c | d |", null, AddRowAtEndCommand());
        Add(cases, "add row at end outside table returns null",
            "plain ‸text", null, AddRowAtEndCommand());

        // RemoveRow: next row, previous row, header, delimiter, outside, document end, CRLF, escapes.
        Add(cases, "remove row first column goes to next row",
            "| a | b |\n|---|---|\n| ‸c | d |\n| e | f |",
            "| a | b |\n|---|---|\n| ‸e | f |", RemoveRowCommand());
        Add(cases, "remove last row goes to previous row",
            "| a | b |\n|---|---|\n| c | d |\n| e | ‸f |",
            "| a | b |\n|---|---|\n| c | ‸d |", RemoveRowCommand());
        Add(cases, "remove row header returns null",
            "| ‸a | b |\n|---|---|\n| c | d |", null, RemoveRowCommand());
        Add(cases, "remove row delimiter returns null",
            "| a | b |\n|---|‸---|\n| c | d |", null, RemoveRowCommand());
        Add(cases, "remove row outside table returns null",
            "plain ‸text", null, RemoveRowCommand());
        // G4: the last body row has no row above it but the delimiter row, which the caret must
        // never enter (the next Delete would erase it). The header row takes the same column.
        Add(cases, "remove last row without newline goes to the header cell",
            "| a | b |\n|---|---|\n| ‸c | d |",
            "| ‸a | b |\n|---|---|", RemoveRowCommand());
        // An empty target cell has no text to put the caret before, so the caret takes that cell's
        // own stop one space inside its closing pipe (MarkdownTableCommands.EmptyCellStop).
        Add(cases, "remove last row goes to an empty header cell's end",
            "|  |  |\n|---|---|\n| ‸c | d |",
            "| ‸ |  |\n|---|---|", RemoveRowCommand());
        Add(cases, "remove last row goes to an empty row above",
            "| a |\n|---|\n|  |\n| ‸x |",
            "| a |\n|---|\n| ‸ |", RemoveRowCommand());
        Add(cases, "remove last of two body rows goes to the row above",
            "| a | b |\n|---|---|\n| c | d |\n| e | ‸f |",
            "| a | b |\n|---|---|\n| c | ‸d |", RemoveRowCommand());
        Add(cases, "remove last row keeps the caret column in the row above",
            "| a | b | c |\n|---|---|---|\n| d | e | f |\n| g | h | ‸i |",
            "| a | b | c |\n|---|---|---|\n| d | e | ‸f |", RemoveRowCommand());
        Add(cases, "remove last row clamps a wider column into the header",
            "| a | b |\n|---|---|\n| c | d |\n| e | f | ‸g |",
            "| a | b |\n|---|---|\n| c | ‸d |", RemoveRowCommand());
        Add(cases, "remove last row CRLF document goes to the row above",
            "| a | b |\r\n|---|---|\r\n| c | d |\r\n| e | ‸f |",
            "| a | b |\r\n|---|---|\r\n| c | ‸d |", RemoveRowCommand());
        Add(cases, "remove row CRLF document",
            "| a | b |\r\n|---|---|\r\n| ‸c | d |\r\n| e | f |",
            "| a | b |\r\n|---|---|\r\n| ‸e | f |", RemoveRowCommand());
        Add(cases, "remove row keeps escaped pipe in other row",
            "| a \\| x | b |\n|---|---|\n| c | d |\n| e | ‸f |",
            "| a \\| x | b |\n|---|---|\n| c | ‸d |", RemoveRowCommand());
        Add(cases, "remove row escaped pipe header keeps caret column",
            "| a \\| x | b |\n|---|---|\n| c | ‸d |\n| e | f |",
            "| a \\| x | b |\n|---|---|\n| e | ‸f |", RemoveRowCommand());

        // G5: the insert button on a table row (or on the line directly under the table) puts the
        // new grid below the old table's last row, one blank line on each side, so the new header
        // is never read back as one more row of the old table. The first header cell is selected
        // for typing. A blank line that already separates the table is reused, not doubled.
        string insertTwo = "| ‸제목1 | 제목2 |\n|---|---|\n|  |  |";
        Add(cases, "insert table under a table row reuses the blank line below",
            "| a | b |\n|---|---|\n| c | ‸d |\n\nnext\n",
            "| a | b |\n|---|---|\n| c | d |\n\n" + insertTwo + "\n\nnext\n",
            InsertTable(1, 2), 3);
        Add(cases, "insert table under the last row writes both blank lines",
            "| a | b |\n|---|---|\n| c | ‸d |\nnext\n",
            "| a | b |\n|---|---|\n| c | d |\n\n" + insertTwo + "\n\nnext\n",
            InsertTable(1, 2), 3);
        Add(cases, "insert table from the header row goes under the last row",
            "| ‸a | b |\n|---|---|\n| c | d |\n\nnext\n",
            "| a | b |\n|---|---|\n| c | d |\n\n" + insertTwo + "\n\nnext\n",
            InsertTable(1, 2), 3);
        Add(cases, "insert table on the line directly under a table",
            "| a |\n|---|\n| b |\n‸\nnext\n",
            "| a |\n|---|\n| b |\n\n| ‸제목1 |\n|---|\n|  |\n\nnext\n",
            InsertTable(1, 1), 3);
        Add(cases, "insert table on a paragraph directly under a table",
            "| a |\n|---|\n| b |\n‸next\n",
            "| a |\n|---|\n| b |\n\n| ‸제목1 |\n|---|\n|  |\n\nnext\n",
            InsertTable(1, 1), 3);
        Add(cases, "insert table under a table at the document end",
            "| a | b |\n|---|---|\n| c | ‸d |",
            "| a | b |\n|---|---|\n| c | d |\n\n" + insertTwo + "\n",
            InsertTable(1, 2), 3);
        Add(cases, "insert table outside a table keeps the line rule",
            "head‸\ntail\n",
            "head\n| ‸제목1 | 제목2 |\n|---|---|\n|  |  |\ntail\n",
            InsertTable(1, 2), 3);
        // The header word follows the UI language on both insert paths: the live host passes the
        // same T("제목") to the gap-separated grid and to MarkdownCommands.InsertTable. Both
        // select the whole placeholder run ("Title1"), exactly the first header cell typed over.
        Add(cases, "insert table near a table uses the localized header",
            "| a |\n|---|\n| b |\n‸next\n",
            "| a |\n|---|\n| b |\n\n| ‸Title1 |\n|---|\n|  |\n\nnext\n",
            InsertTableLocalized(1, 1, "Title"), 6);
        Add(cases, "insert table outside a table uses the localized header",
            "head‸\ntail\n",
            "head\n| ‸Title1 | Title2 |\n|---|---|\n|  |  |\ntail\n",
            InsertTableLocalized(1, 2, "Title"), 6);
        // The line directly above an existing table is not a table row, so InsertTableSafely
        // forwards to MarkdownCommands.InsertTable. The new grid must not be joined to the old
        // header: one blank line keeps the old table's header and delimiter row as its own, or the
        // grid would read them back as two more body rows (bughunt2 insert.md item 5).
        Add(cases, "insert table on the line above a table leaves a blank line",
            "para‸\n| a | b |\n|---|---|\n| 1 | 2 |",
            "para\n| ‸제목1 | 제목2 |\n|---|---|\n|  |  |\n\n| a | b |\n|---|---|\n| 1 | 2 |",
            InsertTable(1, 2), 3);
        Add(cases, "insert table from the start of the line above a one column table",
            "‸para\n| a |\n|---|\n| b |",
            "para\n| ‸제목1 |\n|---|\n|  |\n\n| a |\n|---|\n| b |",
            InsertTable(1, 1), 3);

        // Item 6: a GFM table without the outer pipes is exactly what the renderer draws, so the
        // commands read the same rows (before, every command declined and Tab typed a tab).
        Add(cases, "add row below a table without outer pipes",
            "Name | Age\n--- | ---\nKim | ‸30",
            "Name | Age\n--- | ---\nKim | 30\n| | ‸|", AddRow());
        Add(cases, "remove column first of a table without outer pipes keeps a pipe",
            "Name‸ | Age\n--- | ---\nKim | 30",
            "| ‸Age\n| ---\n| 30", RemoveColumnCommand());
        Add(cases, "remove last row of a table without outer pipes goes to the header",
            "Name | Age\n--- | ---\nKim | ‸30",
            "Name | ‸Age\n--- | ---", RemoveRowCommand());
        Add(cases, "enter after the last row of a table without outer pipes",
            "Name | Age\n--- | ---\nKim | 30‸",
            "Name | Age\n--- | ---\nKim | 30\n‸", EnterOutside());
        Add(cases, "delete a table without outer pipes keeps the next line",
            "Name‸ | Age\n--- | ---\nKim | 30\nnext\n",
            "‸next\n", DeleteTableCommand());
        Add(cases, "add column to a table without outer pipes",
            "Name | ‸Age\n--- | ---\nKim | 30",
            "Name | Age| ‸  |\n--- | ---| --- |\nKim | 30|   |", AddColumn());

        // AddColumnRight: first, last, middle, no trailing pipe, one column, outside.
        Add(cases, "add column right first column",
            "| ‸a | b |\n|---|---|\n| c | d |",
            "| a | ‸  | b |\n|---| --- |---|\n| c |   | d |", AddColumn());
        Add(cases, "add column right last column",
            "| a | ‸b |\n|---|---|\n| c | d |",
            "| a | b | ‸  |\n|---|---| --- |\n| c | d |   |", AddColumn());
        Add(cases, "add column right middle of three",
            "| a | ‸b | c |\n|---|---|---|\n| d | e | f |",
            "| a | b | ‸  | c |\n|---|---| --- |---|\n| d | e |   | f |", AddColumn());
        Add(cases, "add column right row without trailing pipe",
            "| ‸a | b\n|---|---|",
            "| a | ‸  | b\n|---| --- |---|", AddColumn());
        Add(cases, "add column right one column",
            "| ‸a |\n|---|",
            "| a | ‸  |\n|---| --- |", AddColumn());
        Add(cases, "add column right outside table returns null",
            "plain ‸text", null, AddColumn());

        // RemoveColumn: middle, last, first, one column, escaped pipe, outside.
        Add(cases, "remove column middle of three",
            "| a | ‸b | c |\n|---|---|---|\n| d | e | f |",
            "| a | ‸c |\n|---|---|\n| d | f |", RemoveColumnCommand());
        Add(cases, "remove column last of three",
            "| a | b | ‸c |\n|---|---|---|\n| d | e | f |",
            "| a | ‸b |\n|---|---|\n| d | e |", RemoveColumnCommand());
        Add(cases, "remove column first of three",
            "| ‸a | b | c |\n|---|---|---|\n| d | e | f |",
            "| ‸b | c |\n|---|---|\n| e | f |", RemoveColumnCommand());
        Add(cases, "remove column one column deletes table",
            "| a |\n|---|\n| ‸c |",
            "‸", RemoveColumnCommand());
        Add(cases, "remove column escaped pipe stays",
            "| a \\| x | ‸b |\n|---|---|\n| d | e |",
            "| ‸a \\| x |\n|---|\n| d |", RemoveColumnCommand());
        Add(cases, "remove column outside table returns null",
            "plain ‸text", null, RemoveColumnCommand());

        // DeleteTable: end, middle, alone, outside.
        Add(cases, "delete table at document end",
            "head\n| ‸a |\n|---|",
            "head\n‸", DeleteTableCommand());
        Add(cases, "delete table in the middle",
            "head\n| ‸a |\n|---|\ntail",
            "head\n‸tail", DeleteTableCommand());
        Add(cases, "delete table alone",
            "| ‸a |\n|---|",
            "‸", DeleteTableCommand());
        Add(cases, "delete table outside returns null",
            "plain ‸text", null, DeleteTableCommand());

        // ParagraphAfter: next line, document end, existing empty line, outside.
        Add(cases, "paragraph after next line",
            "| a |\n|---|\n| c | ‸d |\nnext",
            "| a |\n|---|\n| c | d |\n‸\nnext", ParagraphAfterCommand());
        Add(cases, "paragraph after at document end",
            "| a |\n|---|\n| c | ‸d |",
            "| a |\n|---|\n| c | d |\n‸\n", ParagraphAfterCommand());
        Add(cases, "paragraph after keeps existing empty line",
            "| ‸a |\n|---|\n\nnext",
            "| a |\n|---|\n‸\nnext", ParagraphAfterCommand());
        Add(cases, "paragraph after outside returns null",
            "plain ‸text", null, ParagraphAfterCommand());

        // AlignColumn: left, center, right, none, second column, dash count, caret shift, CRLF.
        Add(cases, "align column left",
            "| ‸a | b |\n| --- | --- |",
            "| ‸a | b |\n| :--- | --- |", Align(0));
        Add(cases, "align column center",
            "| ‸a | b |\n| --- | --- |",
            "| ‸a | b |\n| :---: | --- |", Align(1));
        Add(cases, "align column right",
            "| ‸a | b |\n| --- | --- |",
            "| ‸a | b |\n| ---: | --- |", Align(2));
        Add(cases, "align column none from center",
            "| ‸a | b |\n| :---: | --- |",
            "| ‸a | b |\n| --- | --- |", Align(3));
        Add(cases, "align column right second column",
            "| a | ‸b |\n| --- | --- |",
            "| a | ‸b |\n| --- | ---: |", Align(2));
        Add(cases, "align column keeps dash count",
            "| ‸a | b |\n| ----- | --- |",
            "| ‸a | b |\n| :-----: | --- |", Align(1));
        Add(cases, "align column caret on delimiter row shifts",
            "| a |\n| :---: ‸|",
            "| a |\n| --- ‸|", Align(3));
        // G6: the delimiter edit changes the length of a line above every body row, so a caret
        // there sits at the same position in its own row only when it moves with the edit; a caret
        // on the header row is above the edit and stays.
        Add(cases, "align column keeps the caret in its body-row cell",
            "| a | b |\n| --- | --- |\n| c | ‸d |",
            "| a | b |\n| --- | ---: |\n| c | ‸d |", Align(2));
        Add(cases, "align column keeps the caret in a later body row",
            "| a |\n| --- |\n| b |\n| ‸c |",
            "| a |\n| :---: |\n| b |\n| ‸c |", Align(1));
        Add(cases, "align column keeps the caret in a row below a longer delimiter",
            "| a |\n| :---: |\n| ‸b |",
            "| a |\n| --- |\n| ‸b |", Align(3));
        Add(cases, "align column caret on the header row stays put",
            "| ‸a | b |\n| --- | --- |",
            "| ‸a | b |\n| :---: | --- |", Align(1));
        Add(cases, "align column caret inside the delimiter core maps into the new core",
            "| a |\n| :---‸: |",
            "| a |\n| ‸--- |", Align(3));
        Add(cases, "align column CRLF document",
            "| ‸a | b |\r\n| --- | --- |",
            "| ‸a | b |\r\n| :---: | --- |", Align(1));
        Add(cases, "align column outside table returns null",
            "plain ‸text", null, Align(1));

        // Enter at a table boundary: a paragraph line, never a row. At the end of the last row one
        // newline opens a pipe-free line and the caret goes to its start, so the table ends there
        // and what is typed is a paragraph; at column 0 of the header (or of its delimiter row) one
        // newline goes in front of the whole table. Every other position is left to the editor
        // (null).
        Add(cases, "enter after the last row at the document end",
            "| a |\n|---|\n| b |‸",
            "| a |\n|---|\n| b |\n‸", EnterOutside());
        Add(cases, "enter after the last row with text below",
            "| a |\n|---|\n| b |‸\nnext",
            "| a |\n|---|\n| b |\n‸\nnext", EnterOutside());
        Add(cases, "enter after the last row with a blank line below",
            "| a |\n|---|\n| b |‸\n\nnext",
            "| a |\n|---|\n| b |\n‸\n\nnext", EnterOutside());
        Add(cases, "enter after the last row CRLF document",
            "| a |\r\n|---|\r\n| b |‸",
            "| a |\r\n|---|\r\n| b |\r\n‸", EnterOutside());
        Add(cases, "enter after a middle row is left to the editor",
            "| a |\n|---|\n| b |‸\n| c |", null, EnterOutside());
        Add(cases, "enter after the header row is left to the editor",
            "| a |‸\n|---|\n| b |", null, EnterOutside());
        Add(cases, "enter at column zero of the header goes above the table",
            "‸| a |\n|---|\n| b |",
            "‸\n| a |\n|---|\n| b |", EnterOutside());
        Add(cases, "enter at column zero of the delimiter row goes above the table",
            "| a |\n‸|---|\n| b |",
            "‸\n| a |\n|---|\n| b |", EnterOutside());
        Add(cases, "enter at column zero with text above",
            "head\n‸| a |\n|---|\n| b |",
            "head\n‸\n| a |\n|---|\n| b |", EnterOutside());
        Add(cases, "enter at column zero before an indented table",
            "- a\n‸  | x |\n  | --- |\n",
            "- a\n‸\n  | x |\n  | --- |\n", EnterOutside());
        Add(cases, "enter at column zero of the last row is left to the editor",
            "| a |\n|---|\n‸| b |", null, EnterOutside());
        Add(cases, "enter inside a cell is left to the editor",
            "| a |\n|---|\n| b‸ c |", null, EnterOutside());
        Add(cases, "enter outside any table returns null",
            "plain ‸text", null, EnterOutside());
        Add(cases, "enter in a fenced pipe block returns null",
            "```\n| a |‸\n|---|\n```", null, EnterOutside());

        // Stage 4, the caret-only cell rules. Enter inside a cell is the basic view's cell
        // paragraph break, saved as "<br>" (MarkdownWriter.cs:63), so the row never splits.
        // Backspace and Delete stay inside the cell: no cell is merged, no "|" is deleted and no
        // row's line break is removed. A hidden cell "<br>" is one unit for both keys.
        Add(cases, "enter in a cell writes a cell break",
            "| a | b |\n|---|---|\n| c‸d | e |",
            "| a | b |\n|---|---|\n| c<br>‸d | e |", CellEnter());
        Add(cases, "enter at a cell start inserts inside the cell",
            "| a | b |\n|---|---|\n| c |‸ d |",
            "| a | b |\n|---|---|\n| c | <br>‸d |", CellEnter());
        Add(cases, "enter at the last cell end stays in the cell",
            "| a | b |\n|---|---|\n| c | d‸ |",
            "| a | b |\n|---|---|\n| c | d<br>‸ |", CellEnter());
        Add(cases, "enter outside a table returns null",
            "plain ‸text", null, CellEnter());
        Add(cases, "backspace at a cell start moves to the previous cell end",
            "| a | b |\n|---|---|\n| c | ‸d |",
            "| a | b |\n|---|---|\n| c‸ | d |", CellBackspace());
        Add(cases, "backspace at the first cell start does nothing",
            "| a | b |\n|---|---|\n| ‸c | d |",
            "| a | b |\n|---|---|\n| ‸c | d |", CellBackspace());
        Add(cases, "backspace at the row start does nothing",
            "‸| a | b |\n|---|---|",
            "‸| a | b |\n|---|---|", CellBackspace());
        Add(cases, "backspace in a row's own indent is left to the structural rule",
            "  ‸| a |\n  | --- |", null, CellBackspace());
        Add(cases, "backspace removes a whole cell break",
            "| a<br>‸b | c |",
            "| a‸b | c |", CellBackspace());
        Add(cases, "backspace inside cell text is left to the editor",
            "| a‸b | c |", null, CellBackspace());
        Add(cases, "backspace past the last cell returns to its end",
            "| a | b |‸",
            "| a | b‸ |", CellBackspace());
        Add(cases, "delete at a cell end stays in the cell",
            "| a‸ | b |",
            "| a‸ | b |", CellDelete());
        Add(cases, "delete at the last cell end does nothing",
            "| a | b‸ |",
            "| a | b‸ |", CellDelete());
        Add(cases, "delete before a cell break removes the whole tag",
            "| a‸<br>b | c |",
            "| a‸b | c |", CellDelete());
        Add(cases, "delete on a pipe does nothing",
            "| a ‸| b |",
            "| a ‸| b |", CellDelete());
        Add(cases, "delete inside cell text is left to the editor",
            "| a‸b | c |", null, CellDelete());
        Add(cases, "delete at the row text end does nothing",
            "| a | b |‸",
            "| a | b |‸", CellDelete());
        Add(cases, "delete outside a table returns null",
            "plain ‸text", null, CellDelete());

        // Indent: a top-level table joins the list item directly above it on that item's own
        // indent plus the writer's two-space continuation unit (the same column MarkdownWriter
        // gives an item's later blocks, whatever the marker is), so the live parser and
        // Markdown.Parse both read the lines as a table inside the item rather than an indented
        // code block. Blank lines between the item and the table do not hide it. Outdent is the
        // exact inverse.
        Add(cases, "indent table under a bullet item",
            "- item\n‸| a | b |\n| --- | --- |\n| 1 | 2 |",
            "- item\n  ‸| a | b |\n  | --- | --- |\n  | 1 | 2 |", IndentTableCommand());
        Add(cases, "indent table under an ordered item",
            "1. item\n‸| a | b |\n| --- | --- |",
            "1. item\n  ‸| a | b |\n  | --- | --- |", IndentTableCommand());
        Add(cases, "indent table under a task item",
            "- [ ] item\n‸| a |\n| --- |",
            "- [ ] item\n  ‸| a |\n  | --- |", IndentTableCommand());
        Add(cases, "indent table under an already indented item",
            "  - item\n‸| a |\n| --- |",
            "  - item\n    ‸| a |\n    | --- |", IndentTableCommand());
        Add(cases, "indent table under a nested item goes one level deeper",
            "- a\n  - b\n‸| x |\n| --- |",
            "- a\n  - b\n    ‸| x |\n    | --- |", IndentTableCommand());
        Add(cases, "indent table under a tab indented item",
            "\t- item\n‸| a |\n| --- |",
            "\t- item\n\t\t‸| a |\n\t\t| --- |", IndentTableCommand());
        Add(cases, "indent table skips blank lines to reach the item",
            "- item\n\n‸| a |\n| --- |",
            "- item\n\n  ‸| a |\n  | --- |", IndentTableCommand());
        Add(cases, "indent table CRLF document",
            "- item\r\n‸| a |\r\n| --- |",
            "- item\r\n  ‸| a |\r\n  | --- |", IndentTableCommand());
        // A blank line under the table that is completely empty stays empty (the list rule's
        // contract): indenting it would only leave trailing whitespace. An already indented blank
        // line carries the item's continuation indent, so outdent takes one unit off it.
        Add(cases, "indent leaves an empty line under the table empty",
            "- item\n‸| a |\n| --- |\n\nnext",
            "- item\n  ‸| a |\n  | --- |\n\nnext", IndentTableCommand());
        Add(cases, "indent takes an indented blank line under the table",
            "- item\n‸| a |\n| --- |\n  \nnext",
            "- item\n  ‸| a |\n  | --- |\n  \nnext", IndentTableCommand());
        Add(cases, "indent from a body row",
            "- item\n| a | b |\n| --- | --- |\n| ‸1 | 2 |",
            "- item\n  | a | b |\n  | --- | --- |\n  | ‸1 | 2 |", IndentTableCommand());
        // A paragraph above is not a list item: indent is unavailable and the document is untouched,
        // which is the user's report (buttons did nothing, with no hint).
        Add(cases, "indent table below a paragraph is unavailable",
            "text\n‸| a |\n| --- |", null, IndentTableCommand());
        Add(cases, "indent table at the document start is unavailable",
            "‸| a |\n| --- |", null, IndentTableCommand());
        // A table already inside the item has nowhere deeper to go: a second unit would be an
        // indented code block.
        Add(cases, "indent table already inside the item is unavailable",
            "- item\n  ‸| a |\n  | --- |", null, IndentTableCommand());
        Add(cases, "indent a table above a heading is unavailable",
            "## head\n‸| a |\n| --- |", null, IndentTableCommand());
        Add(cases, "indent in a fenced block is unavailable",
            "```\n- item\n‸| a |\n| --- |\n```", null, IndentTableCommand());
        Add(cases, "indent outside a table is unavailable",
            "- item\n‸text", null, IndentTableCommand());

        // Outdent: one indent unit off every line, so a table at level 1 leaves the list.
        Add(cases, "outdent table inside a bullet item",
            "- item\n  ‸| a | b |\n  | --- | --- |",
            "- item\n‸| a | b |\n| --- | --- |", OutdentTableCommand());
        Add(cases, "outdent table inside an ordered item",
            "1. item\n   ‸| a |\n   | --- |",
            "1. item\n‸| a |\n| --- |", OutdentTableCommand());
        Add(cases, "outdent a nested item's table leaves the parent item",
            "- a\n  - b\n    ‸| x |\n    | --- |",
            "- a\n  - b\n  ‸| x |\n  | --- |", OutdentTableCommand());
        Add(cases, "outdent that table again leaves the list",
            "- a\n  - b\n  ‸| x |\n  | --- |",
            "- a\n  - b\n‸| x |\n| --- |", OutdentTableCommand());
        Add(cases, "outdent table of a tab indented item",
            "- item\n\t‸| a |\n\t| --- |",
            "- item\n‸| a |\n| --- |", OutdentTableCommand());
        Add(cases, "outdent takes its blank continuation line with it",
            "- item\n  ‸| a |\n  | --- |\n  \nnext",
            "- item\n‸| a |\n| --- |\n\nnext", OutdentTableCommand());
        Add(cases, "outdent table CRLF document",
            "- item\r\n  ‸| a |\r\n  | --- |",
            "- item\r\n‸| a |\r\n| --- |", OutdentTableCommand());
        Add(cases, "outdent a top-level table is unavailable",
            "‸| a |\n| --- |", null, OutdentTableCommand());
        Add(cases, "outdent outside a table is unavailable",
            "- item\n  ‸text", null, OutdentTableCommand());

        return cases;
    }

    static List<RichCase> BuildRichCases() {
        List<RichCase> cases = new List<RichCase>();
        AddRich(cases, "live Enter at a table boundary", delegate(MainWindow window) {
            return window.TableCommandsLiveEnterProbe();
        });
        AddRich(cases, "table indent and outdent in the live view", delegate(MainWindow window) {
            return window.TableCommandsLevelProbe();
        });
        AddRich(cases, "live row add keeps the caret in its cell", delegate(MainWindow window) {
            return window.TableCommandsRowAddProbe();
        });
        AddRich(cases, "live Tab at the table end adds a row and one undo takes it back", delegate(MainWindow window) {
            return window.TableCommandsTabEndRowProbe();
        });
        AddRich(cases, "live table insert after a table writes the gap", delegate(MainWindow window) {
            return window.TableCommandsInsertGapProbe();
        });
        AddRich(cases, "block commands keep table rows out of a spanning selection", delegate(MainWindow window) {
            return window.TableCommandsBlockSpanProbe();
        });
        AddRich(cases, "block buttons follow the table gate and the live view", delegate(MainWindow window) {
            return window.TableCommandsGateProbe();
        });
        AddRich(cases, "live Tab over a multi-cell selection never edits", delegate(MainWindow window) {
            return window.TableMultiCellTabProbe();
        });
        AddRich(cases, "live pipe typed inside a code span stays raw", delegate(MainWindow window) {
            return window.TableCodeSpanPipeTypingProbe();
        });
        AddRich(cases, "live delimiter row survives delete, paste and backspace", delegate(MainWindow window) {
            return window.TableDelimiterGuardProbe();
        });
        AddRich(cases, "live header plus delimiter selection clears cells, whole table deletes lines", delegate(MainWindow window) {
            return window.TableDelimiterWholeRowDeleteProbe();
        });
        AddRich(cases, "live Ctrl+A delete replaces the whole document", delegate(MainWindow window) {
            return window.TableDelimiterHeaderOnlySelectAllDeleteProbe();
        });
        AddRich(cases, "basic header plus delimiter selection clears cells, whole table deletes lines", delegate(MainWindow window) {
            return window.TableDelimiterWholeRowDeleteBasicProbe();
        });
        AddRich(cases, "header plus delimiter cut keeps lines, clears cells and copies", delegate(MainWindow window) {
            return window.TableDelimiterCutProbe();
        });
        AddRich(cases, "typing over a selection that starts on the delimiter row is swallowed", delegate(MainWindow window) {
            return window.TableDelimiterTypingFromRowProbe();
        });
        AddRich(cases, "live and basic table drag move clears the source cells", delegate(MainWindow window) {
            return window.TableDragMoveCellsProbe();
        });
        AddRich(cases, "live Delete from inside the delimiter row to the data row is swallowed", delegate(MainWindow window) {
            return window.TableDelimiterPartialToDataRowProbe();
        });
        AddRich(cases, "live and basic cell delete wall keeps the row", delegate(MainWindow window) {
            return window.TableCellWallProbe();
        });
        return cases;
    }

    static List<HelperCase> BuildHelperCases() {
        List<HelperCase> cases = new List<HelperCase>();
        AddHelper(cases, "find table basic", "| a | b |\n|---|---|\n| c | ‸d |", "true 1 3", FindTableCheck());
        AddHelper(cases, "find table without delimiter", "| a | b |\n| c | ‸d |", "false", FindTableCheck());
        AddHelper(cases, "find table delimiter with text", "| a | b |\n| x | y |\n| c | ‸d |", "false", FindTableCheck());
        AddHelper(cases, "find table outside paragraph", "plain ‸text", "false", FindTableCheck());
        AddHelper(cases, "find table colon dashes", "| ‸a |\n|:--:|\n| b |", "true 1 3", FindTableCheck());
        AddHelper(cases, "find table after paragraph", "para\n| a |\n|---|\n| ‸b |", "true 2 4", FindTableCheck());
        // Item 6: the renderer accepts rows without the outer pipes (GFM), so the commands must
        // find the same table; a pipe paragraph above a header is not part of it, and a pipe line
        // above a rule is not a table at all.
        AddHelper(cases, "find table without outer pipes", "Name | Age\n--- | ---\nKim | ‸30", "true 1 3", FindTableCheck());
        AddHelper(cases, "find table below a pipe paragraph", "see a | b\n| h | i |\n| --- | --- |\n| ‸x | y |", "true 2 4", FindTableCheck());
        AddHelper(cases, "find table ignores a pipe line above a rule", "a | ‸b\n---", "false", FindTableCheck());
        // A table the renderer draws past the old 2000-line window (bughunt3 output-scale item 6):
        // the command layer has to find it too, so the caret row of a very long table is still a
        // table row for the boundary keys and the tool row. 2500 body rows put the caret on line 2503.
        StringBuilder tall = new StringBuilder("| a | b |\n|---|---|\n");
        for (int r = 1; r <= 2500; r++) tall.Append("| r").Append(r).Append(" | v |\n");
        tall.Append("| ‸r2501 | v |");
        AddHelper(cases, "find table past the old 2000 line window", tall.ToString(), "true 1 2503", FindTableCheck());
        // The same depth for a cell selection: clearing two cells in row 2500 of a 2503-row table
        // keeps the row's pipes instead of being handed to the editor as whole-table text.
        StringBuilder deep = new StringBuilder("| a | b |\n|---|---|");
        for (int r = 0; r < 2501; r++) {
            deep.Append('\n');
            if (r == 2497) deep.Append("| ").Append(CaretMark).Append('c').Append(r).Append(" | v |");
            else deep.Append("| c").Append(r).Append(" | v |");
        }
        AddHelper(cases, "cell clear in row 2500 of a 2503-row table keeps the row",
            deep.ToString(), "row=[|  |  |]", DeepCellClearCheck(9));
        AddHelper(cases, "cell index without outer pipes", "Name | ‸Age", "1", CellIndexCheck());
        AddHelper(cases, "cell index middle", "| a | ‸b | c |", "1", CellIndexCheck());
        AddHelper(cases, "cell index first", "| ‸a | b | c |", "0", CellIndexCheck());
        AddHelper(cases, "cell index before leading pipe", "‸| a | b |", "0", CellIndexCheck());
        AddHelper(cases, "cell index escaped pipe", "| a \\| x | ‸b |", "1", CellIndexCheck());
        AddHelper(cases, "cell index code span pipe", "| `x|y` | ‸b |", "1", CellIndexCheck());
        AddHelper(cases, "cell index clamps to row cells", "| a | b |\n|---|---|\n| c |‸", "0", CellIndexCheck());
        AddHelper(cases, "three deletes at a cell end keep the row", "| a‸ | b |", "same", CellEndDeleteRepeatCheck());
        // The padding around a separator belongs to the cell before it: Delete there never takes
        // the pipe or the neighbour's text (the table-abuse probe's "| `" range).
        AddHelper(cases, "three deletes in a cell's padding keep the row", "| a ‸  | b |", "same", CellEndDeleteRepeatCheck());
        AddHelper(cases, "three deletes on a cell's separator pipe keep the row", "| a ‸| b |", "same", CellEndDeleteRepeatCheck());
        AddHelper(cases, "three deletes after a separator pipe keep the row", "| a |‸ b |", "same", CellEndDeleteRepeatCheck());
        AddHelper(cases, "three deletes in the padding before an unpadded pipe keep the row", "| ab|‸ `x` |", "same", CellEndDeleteRepeatCheck());
        AddHelper(cases, "delimiter alignment center", "| --- | :---: |‸", "1", DelimiterAlignmentCheck(1));
        AddHelper(cases, "delimiter alignment right", "| --- | ---: |‸", "2", DelimiterAlignmentCheck(1));
        AddHelper(cases, "delimiter alignment explicit left", "| :--- | --- |‸", "0", DelimiterAlignmentCheck(0));
        AddHelper(cases, "delimiter alignment plain left", "| --- | --- |‸", "0", DelimiterAlignmentCheck(0));
        AddHelper(cases, "delimiter alignment missing column", "| --- |‸", "0", DelimiterAlignmentCheck(3));

        // Button state: enabled only when the command would do something, and the tooltip carries
        // the reason whenever it would not (the user report was a dead button with no hint).
        AddHelper(cases, "state table below a bullet item",
            "- item\n‸| a |\n| --- |", "indent no-outdent | - | " + MarkdownTableCommands.OutdentNoIndentTip, TableLevelStateCheck());
        AddHelper(cases, "state table below a paragraph",
            "text\n‸| a |\n| --- |", "no-indent no-outdent | " + MarkdownTableCommands.IndentNoListTip + " | " + MarkdownTableCommands.OutdentNoIndentTip, TableLevelStateCheck());
        AddHelper(cases, "state table already inside the item",
            "- item\n  ‸| a |\n  | --- |", "no-indent outdent | " + MarkdownTableCommands.IndentInsideTip + " | -", TableLevelStateCheck());
        AddHelper(cases, "state table at the document start",
            "‸| a |\n| --- |", "no-indent no-outdent | " + MarkdownTableCommands.IndentNoListTip + " | " + MarkdownTableCommands.OutdentNoIndentTip, TableLevelStateCheck());
        AddHelper(cases, "state outside a table is both disabled",
            "text ‸here", "no-indent no-outdent | - | -", TableLevelStateCheck());

        // The indent result is still a table for both parsers: the live table state (the same scan
        // the preview's block cache runs) and Markdown.Parse, which reads table rows at any indent
        // so the move can never turn the lines into something other than a table.
        AddHelper(cases, "indented table stays a table in the live parser",
            "- item\n  ‸| a | b |\n  | --- | --- |\n  | 1 | 2 |", "table 2 4 rows=3", LiveTableCheck());
        AddHelper(cases, "indented table is a table block in the basic parser",
            "- item\n  ‸| a | b |\n  | --- | --- |\n  | 1 | 2 |", "tables=1", BasicParseCheck());
        AddHelper(cases, "top-level table is a table block in the basic parser",
            "‸| a | b |\n| --- | --- |", "tables=1", BasicParseCheck());
        AddHelper(cases, "table indented by four spaces is still a table block",
            "- item\n    ‸| a | b |\n    | --- | --- |", "tables=1", BasicParseCheck());

        // Markdown.Cells follows the live renderer (and MarkdownPlainText.Cells): a pipe inside a
        // `code` span is cell text for as long as the same-length backtick run keeps it open, and
        // a pipe after an EVEN backslash run is still the row's closing pipe (odd means escaped).
        AddHelper(cases, "basic table keeps a pipe inside a code span",
            "‸| `a|b` | c |\n| --- | --- |", "cols=2", BasicTableColumnsCheck());
        AddHelper(cases, "basic table keeps a pipe inside a double backtick span",
            "‸| ``a|b`` | c |\n| --- | --- |", "cols=2", BasicTableColumnsCheck());
        AddHelper(cases, "basic table takes a pipe after two backslashes as the row end",
            "‸| h \\\\|\n| --- |", "cols=1", BasicTableColumnsCheck());
        AddHelper(cases, "basic table keeps an escaped pipe inside a cell",
            "‸| a \\| b | c |\n| --- | --- |", "cols=2", BasicTableColumnsCheck());
        return cases;
    }
}
public partial class MainWindow {
    internal static int VerifyTableCommands(string outDir) { return MarkdownTableCommandsTests.Run(outDir); }
    internal void MarkProbeWindowClean() { saved = text; }

    // Live surface: Enter at a table boundary writes a paragraph line, never a table row, and the
    // source toggle keeps today's plain newline. Drives the real key path (SourceStructuralKeys
    // through a PreviewKeyDown raised on the TextArea, the same shape other live probes use), so
    // the wiring is covered, not only the pure function.
    internal string TableCommandsLiveEnterProbe() {
        
        SetMode(true); LongLineBench.Idle();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string table = "| a | b |\n| --- | --- |\n| 1 | 2 |";
        SetText(table); LongLineBench.Idle();
        // End of the last row: one newline, and the caret goes to the start of the line it opened.
        source.Select(source.Document.GetLineByNumber(source.Document.LineCount).EndOffset, 0);
        bool taken = PressEnterKey();
        string below = text;
        if (!taken) return "the table Enter rule did not take the key";
        if (below != table + "\n") return "Enter below the table wrote \"" + Flat(below) + "\"";
        if (source.Document.GetLineByOffset(source.CaretIndex).Length != 0) return "caret is not on an empty line: \"" + Flat(source.Document.GetText(source.Document.GetLineByOffset(source.CaretIndex))) + "\"";
        if (source.Document.LineCount != 4) return "lines after Enter below the table: " + source.Document.LineCount;
        // Typing there stays a paragraph, and the first text on the empty line also writes the
        // blank separator with it (fix13), so the table keeps its rows under one blank line.
        source.TextArea.PerformTextInput("after"); LongLineBench.Idle();
        string typed = text;
        if (typed != table + "\n\nafter") return "typed text kept no blank separator: \"" + Flat(typed) + "\"";
        if (source.Document.LineCount != 5) return "lines after typing below the table: " + source.Document.LineCount;
        // The separator and the typed text are one undo step (fix13), so two undos still restore
        // the table: the first takes back the text, the second the Enter that opened the line.
        Undo(); LongLineBench.Idle();
        Undo(); LongLineBench.Idle();
        if (text != table) return "undo did not restore the table: \"" + Flat(text) + "\"";
        // Column 0 of a row belongs to the first cell in the live view (H7: the caret never rests
        // on a row's pipe), so Enter at the header's drawn start is the cell break. The pure
        // boundary rule still writes the line above when it is named with column 0, which is the
        // raw source view's path; check that contract directly.
        SetText(table); LongLineBench.Idle();
        source.Select(0, 0);
        LongLineBench.Idle();
        if (source.CaretIndex != source.Document.GetLineByNumber(1).Offset + 2) return "column 0 did not normalize into the first cell (at=" + source.CaretIndex + ")";
        PressEnterKey();
        if (text != "| <br>a | b |\n| --- | --- |\n| 1 | 2 |") return "live Enter at the first header cell wrote \"" + Flat(text) + "\"";
        Undo(); LongLineBench.Idle();
        if (text != table) return "undo did not restore the header cell: \"" + Flat(text) + "\"";
        TextDocument aboveDocument = new TextDocument(table);
        CommandResult above = MarkdownTableCommands.EnterOutsideTable(aboveDocument, 0, 0, true);
        if (above == null || above.Edits.Count != 1 || above.Edits[0].Offset != 0) return "the pure above-table rule did not write one newline at column 0";
        // Source toggle (preview detached): the boundary rule is off, so Enter is left to the
        // editor's own newline (a probe cannot observe that through PreviewKeyDown alone, so the
        // check is that the structural handler did not take the key).
        SetText(table); LongLineBench.Idle();
        loading = true; preview.IsChecked = true; loading = false;
        SwitchSurface(); LongLineBench.Idle();
        bool detached = !LivePreviewAttached;
        source.Select(source.Document.GetLineByNumber(source.Document.LineCount).EndOffset, 0);
        bool handled = PressEnterKey();
        string raw = text;
        loading = true; preview.IsChecked = false; loading = false;
        SwitchSurface(); LongLineBench.Idle();
        if (!detached) return "the source toggle did not detach the preview";
        if (handled) return "the source toggle still ran the table Enter rule";
        if (raw != table) return "the source toggle changed the document: \"" + Flat(raw) + "\"";
        return null;
    }
    bool PressEnterKey() {
        var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(source), 0, System.Windows.Input.Key.Enter) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        source.TextArea.RaiseEvent(args);
        LongLineBench.Idle();
        return args.Handled;
    }
    // The real structural key path (SourceStructuralKeys runs on PreviewKeyDown), without a
    // keyboard: the probe cannot set Keyboard.Modifiers, so only the plain keys are used here.
    bool PressStructuralKey(System.Windows.Input.Key key) {
        var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(source), 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        source.TextArea.RaiseEvent(args);
        LongLineBench.Idle();
        return args.Handled;
    }
    // The whole Delete key press: the structural handler sees it first on PreviewKeyDown, and when
    // the live rule declines the key the TextArea's own Delete command runs, exactly the two steps
    // a real key press reaches (a probe cannot drive the command routing itself).
    bool PressDeleteKey() {
        bool handled = PressStructuralKey(System.Windows.Input.Key.Delete);
        if (!handled) {
            EditingCommands.Delete.Execute(null, source.TextArea);
            LongLineBench.Idle();
        }
        return handled;
    }
    // The whole Backspace key press: the structural handler first, then the editor's own command
    // when the live rule declines the key (a probe cannot drive the command routing itself).
    bool PressBackspaceKey() {
        bool handled = PressStructuralKey(System.Windows.Input.Key.Back);
        if (!handled) {
            EditingCommands.Backspace.Execute(null, source.TextArea);
            LongLineBench.Idle();
        }
        return handled;
    }
    // Selects exactly this block for a delete. The live caret guard pulls a selection endpoint that
    // lands before a row's own leading pipe into that row's first cell, so the press freeze (the
    // test seam the layout probes use for exact table positions) holds the block the case names.
    // The caller's finally runs EndPress, which lets the caret settle after the key.
    void SelectDelimiterBlock(int start, int end) {
        livePreview.BeginPress();
        source.Select(start, end - start);
        LongLineBench.Idle();
    }
    // Item 1 in the real window: a selection across two cells of a live table row. Tab must move
    // the selection into the next cell (the next body row's first cell here, because the selection
    // ends in the header's last cell) and never let the editor's TabForward replace the selected
    // text and its "|" with one tab. The table text must stay byte-identical.
    internal string TableMultiCellTabProbe() {
        SetMode(true); LongLineBench.Idle();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string table = "| a | b |\n| --- | --- |\n| 1 | 2 |";
        SetText(table); LongLineBench.Idle();
        var header = source.Document.GetLineByNumber(1);
        var headerInfo = livePreview.Info(header);
        if (headerInfo.Cells == null || headerInfo.Cells.Count != 2) return "the header row did not parse into two cells";
        source.Select(header.Offset + headerInfo.Cells[0].Start, headerInfo.Cells[1].End - headerInfo.Cells[0].Start);
        LongLineBench.Idle();
        bool taken = PressStructuralKey(System.Windows.Input.Key.Tab);
        if (!taken) return "Tab over a multi-cell selection was not taken";
        if (text != table) return "Tab changed the table: \"" + Flat(text) + "\"";
        var body = source.Document.GetLineByNumber(3);
        var bodyInfo = livePreview.Info(body);
        if (bodyInfo.Cells == null || bodyInfo.Cells.Count < 1) return "the body row did not parse into cells";
        int expectedStart = body.Offset + bodyInfo.Cells[0].Start;
        int expectedLength = bodyInfo.Cells[0].End - bodyInfo.Cells[0].Start;
        if (source.SelectionStart != expectedStart || source.SelectionLength != expectedLength)
            return "Tab did not select the next cell (start=" + source.SelectionStart + ", length=" + source.SelectionLength
                + ", expected=" + expectedStart + "+" + expectedLength + ")";
        return null;
    }
    // H1 with a closed `code` span: a "|" typed inside the span is written raw (TablePipes
    // skips span content and ParseInline draws it literally), while the same key elsewhere in
    // the cell is still escaped. Each edit is one document update, so one undo takes it back.
    internal string TableCodeSpanPipeTypingProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string table = "| `ab` | c |\n| --- | --- |";
        SetText(table); LongLineBench.Idle();
        DocumentLine header = source.Document.GetLineByNumber(1);
        var info = livePreview.Info(header);
        if (info.Cells == null || info.Cells.Count != 2) return "the header row did not parse into two cells";
        // Between the code span's "a" and "b": cells[0].Start points at the opening backtick.
        source.Select(header.Offset + info.Cells[0].Start + 2, 0); LongLineBench.Idle();
        source.TextArea.PerformTextInput("|"); LongLineBench.Idle();
        string typed = "| `a|b` | c |\n| --- | --- |";
        if (text != typed) return "a pipe typed inside a code span wrote \"" + Flat(text) + "\"";
        info = livePreview.Info(source.Document.GetLineByNumber(1));
        if (info.Cells == null || info.Cells.Count != 2) return "the typed pipe split the cell (" + (info.Cells == null ? "-1" : info.Cells.Count.ToString(CultureInfo.InvariantCulture)) + " cells)";
        DocumentLine typedLine = source.Document.GetLineByNumber(1);
        if (source.CaretIndex != typedLine.Offset + info.Cells[0].Start + 3) return "the caret did not stay after the typed pipe (at " + source.CaretIndex + ")";
        Undo(); LongLineBench.Idle();
        if (text != table) return "undo did not take the raw pipe back: \"" + Flat(text) + "\"";
        // Outside the code span the key keeps the escape.
        string plain = "| ab | c |\n| --- | --- |";
        SetText(plain); LongLineBench.Idle();
        header = source.Document.GetLineByNumber(1);
        info = livePreview.Info(header);
        if (info.Cells == null || info.Cells.Count != 2) return "the plain header row did not parse into two cells";
        source.Select(header.Offset + info.Cells[0].End, 0); LongLineBench.Idle();
        source.TextArea.PerformTextInput("|"); LongLineBench.Idle();
        if (text != "| ab\\| | c |\n| --- | --- |") return "a pipe typed outside a code span wrote \"" + Flat(text) + "\"";
        return null;
    }
    // Item 12: on live mode's revealed caret line the delimiter row is structure. Deleting a
    // selection that starts or ends on it, pasting over that selection, and Backspace at its start
    // must leave the row (and the table) untouched; typing there is already kept out.
    internal string TableDelimiterGuardProbe() {
        SetMode(true); LongLineBench.Idle();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            hideMarkers = false;
            UpdateLivePreview();
            LongLineBench.Idle();
            string table = "| a | b |\n| --- | --- |\n| 1 | 2 |";
            SetText(table); LongLineBench.Idle();
            var delimiter = source.Document.GetLineByNumber(2);
            // A selection inside the revealed delimiter row: "--- |" of "| --- | --- |".
            source.Select(delimiter.Offset + 2, 5);
            LongLineBench.Idle();
            bool deleted = PressStructuralKey(System.Windows.Input.Key.Delete);
            if (!deleted) return "Delete over the delimiter selection was not swallowed";
            if (text != table) return "Delete changed the revealed delimiter row: \"" + Flat(text) + "\"";
            source.Select(delimiter.Offset + 2, 5);
            LongLineBench.Idle();
            bool pasted = LivePaste(null, true, "x");
            if (!pasted) return "a paste over the delimiter selection was not handled";
            if (text != table) return "the paste changed the revealed delimiter row: \"" + Flat(text) + "\"";
            source.Select(delimiter.Offset, 0);
            LongLineBench.Idle();
            bool back = PressStructuralKey(System.Windows.Input.Key.Back);
            if (!back) return "Backspace at the delimiter row start was not swallowed";
            if (text != table) return "Backspace joined the delimiter row into the header: \"" + Flat(text) + "\"";
            var header = source.Document.GetLineByNumber(1);
            if (!livePreview.Info(header).Table) return "the header is no longer a table in the live parser";
            if (!livePreview.Info(delimiter).Delimiter) return "the delimiter row is no longer a delimiter in the live parser";
        } finally {
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // Item 13a (updated product decision): a selection that only covers the header and its
    // delimiter row is still part of the table, so Delete keeps every line and clears the
    // selected cells (rule c). A selection that covers the WHOLE table deletes its lines as one
    // ordinary block (rule b), also when a paragraph follows: only the table's lines go.
    internal string TableDelimiterWholeRowDeleteProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            hideMarkers = false;
            UpdateLivePreview();
            LongLineBench.Idle();
            string table = "| a | b |\n| --- | --- |\n| 1 | 2 |";
            SetText(table); LongLineBench.Idle();
            DocumentLine header = source.Document.GetLineByNumber(1);
            DocumentLine data = source.Document.GetLineByNumber(3);
            // Header + delimiter only: the lines stay, the selected header cells go.
            SelectDelimiterBlock(header.Offset, data.Offset);
            PressDeleteKey();
            string cleared = "|  |  |\n| --- | --- |\n| 1 | 2 |";
            if (text != cleared) return "Delete over the header and delimiter lines wrote \"" + Flat(text) + "\"";
            if (source.CaretIndex != source.Document.GetLineByNumber(1).Offset + 2) return "the cleared header caret sits at " + source.CaretIndex;
            Undo(); LongLineBench.Idle();
            if (text != table) return "one undo after the header clear restored \"" + Flat(text) + "\"";
            // The whole table selected: its lines go as one block.
            header = source.Document.GetLineByNumber(1);
            data = source.Document.GetLineByNumber(3);
            SelectDelimiterBlock(header.Offset, data.EndOffset);
            PressDeleteKey();
            if (text != "") return "Delete over the whole table left \"" + Flat(text) + "\"";
            Undo(); LongLineBench.Idle();
            if (text != table) return "one undo after the whole-table delete restored \"" + Flat(text) + "\"";
            // A table followed by a paragraph: the table's lines go, the paragraph stays.
            string followed = "| a | b |\n| --- | --- |\n| 1 | 2 |\n\npara\n";
            SetText(followed); LongLineBench.Idle();
            header = source.Document.GetLineByNumber(1);
            data = source.Document.GetLineByNumber(3);
            // Through the last table line's own line break: the paragraph below keeps its
            // leading blank line (the expected "\npara\n"), so the end is the row's EndOffset
            // plus its delimiter, not its start.
            SelectDelimiterBlock(header.Offset, data.EndOffset + data.DelimiterLength);
            PressDeleteKey();
            if (text != "\npara\n") return "Delete over the table above a paragraph left \"" + Flat(text) + "\"";
        } finally {
            if (livePreview != null) livePreview.EndPress();
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // Item 13b (updated product decision): Ctrl+A selects the whole document, so Delete,
    // Backspace, typing and paste act on the document as a whole - a header-only table, and a
    // table followed by a paragraph, both become empty (or take the typed/pasted text).
    internal string TableDelimiterHeaderOnlySelectAllDeleteProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            hideMarkers = false;
            UpdateLivePreview();
            LongLineBench.Idle();
            string table = "| a | b |\n| --- | --- |";
            SetText(table); LongLineBench.Idle();
            source.SelectAll(); LongLineBench.Idle();
            PressDeleteKey();
            if (text != "") return "Ctrl+A then Delete left \"" + Flat(text) + "\"";
            if (source.Document.TextLength != 0) return "document length after Ctrl+A Delete: " + source.Document.TextLength;
            // The same selection over a table followed by a paragraph: the whole document goes.
            SetText("| a | b |\n| --- | --- |\n\npara\n"); LongLineBench.Idle();
            source.SelectAll(); LongLineBench.Idle();
            PressDeleteKey();
            if (text != "") return "Ctrl+A then Delete on a table and paragraph left \"" + Flat(text) + "\"";
            // Backspace over Ctrl+A removes the whole document too.
            SetText(table); LongLineBench.Idle();
            source.SelectAll(); LongLineBench.Idle();
            PressBackspaceKey();
            if (text != "") return "Ctrl+A then Backspace left \"" + Flat(text) + "\"";
            // Typing over Ctrl+A replaces the whole document.
            SetText(table); LongLineBench.Idle();
            source.SelectAll(); LongLineBench.Idle();
            source.TextArea.PerformTextInput("x"); LongLineBench.Idle();
            if (text != "x") return "typing over Ctrl+A left \"" + Flat(text) + "\"";
            // A paste over Ctrl+A replaces the whole document.
            SetText(table); LongLineBench.Idle();
            source.SelectAll(); LongLineBench.Idle();
            bool pasted = LivePaste(null, true, "p");
            LongLineBench.Idle();
            if (!pasted) return "the paste over Ctrl+A was not handled";
            if (text != "p") return "pasting over Ctrl+A left \"" + Flat(text) + "\"";
        } finally {
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // Item 13c (updated product decision): the basic view (markers hidden, the delimiter row
    // folded) follows the same rule. Header + delimiter only keeps every line and clears the
    // selected header cells; the whole table selected deletes its lines.
    internal string TableDelimiterWholeRowDeleteBasicProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            hideMarkers = true;
            UpdateLivePreview();
            LongLineBench.Idle();
            string table = "| a | b |\n| --- | --- |\n| 1 | 2 |";
            SetText(table); LongLineBench.Idle();
            DocumentLine header = source.Document.GetLineByNumber(1);
            DocumentLine data = source.Document.GetLineByNumber(3);
            SelectDelimiterBlock(header.Offset, data.Offset);
            PressDeleteKey();
            string cleared = "|  |  |\n| --- | --- |\n| 1 | 2 |";
            if (text != cleared) return "basic Delete over the header and delimiter lines wrote \"" + Flat(text) + "\"";
            if (source.Document.LineCount != 3) return "basic lines after the header clear: " + source.Document.LineCount;
            Undo(); LongLineBench.Idle();
            if (text != table) return "one basic undo after the header clear restored \"" + Flat(text) + "\"";
            header = source.Document.GetLineByNumber(1);
            data = source.Document.GetLineByNumber(3);
            SelectDelimiterBlock(header.Offset, data.EndOffset);
            PressDeleteKey();
            if (text != "") return "basic Delete over the whole table left \"" + Flat(text) + "\"";
            Undo(); LongLineBench.Idle();
            if (text != table) return "one basic undo after the whole-table delete restored \"" + Flat(text) + "\"";
        } finally {
            if (livePreview != null) livePreview.EndPress();
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // Item 13d: a selection that cuts the delimiter row partially is still structure. It starts
    // inside the row and ends in the first data row, so Delete leaves the text and the selection
    // exactly as they were.
    internal string TableDelimiterPartialToDataRowProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            hideMarkers = false;
            UpdateLivePreview();
            LongLineBench.Idle();
            string table = "| a | b |\n| --- | --- |\n| 1 | 2 |";
            SetText(table); LongLineBench.Idle();
            DocumentLine delimiter = source.Document.GetLineByNumber(2);
            DocumentLine data = source.Document.GetLineByNumber(3);
            int start = delimiter.Offset + 2;
            int end = data.Offset + 2;
            source.Select(start, end - start); LongLineBench.Idle();
            int keptStart = source.SelectionStart, keptLength = source.SelectionLength;
            bool taken = PressDeleteKey();
            if (!taken) return "Delete over the partial delimiter selection was not swallowed";
            if (text != table) return "Delete changed the partial delimiter table: \"" + Flat(text) + "\"";
            if (source.SelectionStart != keptStart || source.SelectionLength != keptLength)
                return "the guard moved the selection (start=" + source.SelectionStart + ", length=" + source.SelectionLength
                    + ", expected=" + keptStart + "+" + keptLength + ")";
        } finally {
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // Cut must answer exactly like Delete on the header-plus-delimiter run (cell clear, every
    // line kept) and still put the selected Markdown on the clipboard. Probes capture the payload
    // through LiveTableCutHook instead of the real clipboard. Both view modes.
    internal string TableDelimiterCutProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            for (int mode = 0; mode < 2; mode++) {
                hideMarkers = mode == 1;
                UpdateLivePreview();
                LongLineBench.Idle();
                string name = hideMarkers ? "basic" : "live";
                string table = "| a | b |\n| --- | --- |\n| 1 | 2 |";
                string cleared = "|  |  |\n| --- | --- |\n| 1 | 2 |";
                // Delete first: the cut has to produce the same document.
                SetText(table); LongLineBench.Idle();
                DocumentLine header = source.Document.GetLineByNumber(1);
                DocumentLine data = source.Document.GetLineByNumber(3);
                SelectDelimiterBlock(header.Offset, data.Offset);
                PressDeleteKey();
                if (text != cleared) return name + ": Delete over the header and delimiter lines wrote \"" + Flat(text) + "\"";
                Undo(); LongLineBench.Idle();
                if (text != table) return name + ": one undo after the Delete restored \"" + Flat(text) + "\"";
                // The same selection cut: same document, selection payload copied, one undo.
                header = source.Document.GetLineByNumber(1);
                data = source.Document.GetLineByNumber(3);
                SelectDelimiterBlock(header.Offset, data.Offset);
                string copied = null;
                System.Action<System.Windows.DataObject> previous = LiveTableCutHook;
                LiveTableCutHook = delegate(System.Windows.DataObject payload) { copied = MarkdownClipboard.Get(payload); };
                // Run the real command so every preview-executed handler sees a real
                // ExecutedRoutedEventArgs; a hand-built one has no RoutedEvent and the command
                // system refuses to raise it. The hook still captures the payload, so nothing
                // reaches the real clipboard when the live rule takes the cut.
                try { System.Windows.Input.ApplicationCommands.Cut.Execute(null, source.TextArea); }
                finally { LiveTableCutHook = previous; }
                if (copied == null) return name + ": the Cut over the header and delimiter lines was not taken";
                if (text != cleared) return name + ": Cut over the header and delimiter lines wrote \"" + Flat(text) + "\"";
                if (copied.IndexOf("a", StringComparison.Ordinal) < 0 || copied.IndexOf("---", StringComparison.Ordinal) < 0)
                    return name + ": Cut did not copy the selected text (\"" + Flat(copied) + "\")";
                Undo(); LongLineBench.Idle();
                if (text != table) return name + ": one undo after the Cut restored \"" + Flat(text) + "\"";
            }
        } finally {
            if (livePreview != null) livePreview.EndPress();
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // Raw view -> basic view leaves the caret on the delimiter row. A selection that starts on
    // that row and ends on a data row must not be replaced by typed text: the guard has to see the
    // selection endpoint, not only the caret line.
    internal string TableDelimiterTypingFromRowProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            string table = "| a | b |\n| --- | --- |\n| 1 | 2 |";
            SetText(table); LongLineBench.Idle();
            hideMarkers = true;
            UpdateLivePreview();
            LongLineBench.Idle();
            DocumentLine delimiter = source.Document.GetLineByNumber(2);
            DocumentLine data = source.Document.GetLineByNumber(3);
            livePreview.BeginPress();
            source.Select(delimiter.Offset, data.Offset + 2 - delimiter.Offset);
            LongLineBench.Idle();
            livePreview.EndPress();
            LongLineBench.Idle();
            int keptStart = source.SelectionStart, keptLength = source.SelectionLength;
            source.TextArea.PerformTextInput("x");
            LongLineBench.Idle();
            if (text != table) return "typing over a selection that starts on the delimiter row wrote \"" + Flat(text) + "\"";
            if (source.SelectionStart != keptStart || source.SelectionLength != keptLength)
                return "the guard moved the selection (start=" + source.SelectionStart + ", length=" + source.SelectionLength
                    + ", expected=" + keptStart + "+" + keptLength + ")";
        } finally {
            if (livePreview != null) livePreview.EndPress();
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // A drag move that starts on a table row clears the source cells with the delete rule (the
    // "|" separators stay) and inserts at the drop with the cell paste rule, as one undo step.
    // Both view modes: one whole row, then a run across the row's two cells.
    internal string TableDragMoveCellsProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            for (int mode = 0; mode < 2; mode++) {
                hideMarkers = mode == 1;
                UpdateLivePreview();
                LongLineBench.Idle();
                string name = hideMarkers ? "basic" : "live";
                string table = "| a | b |\n| --- | --- |\n| c | d |\n| e | f |";
                livePreview.BeginPress();
                // One whole data row dragged into the second cell of the row below.
                SetText(table); LongLineBench.Idle();
                DocumentLine sourceRow = source.Document.GetLineByNumber(3);
                DocumentLine destRow = source.Document.GetLineByNumber(4);
                var destInfo = livePreview.Info(destRow);
                if (destInfo.Cells == null || destInfo.Cells.Count != 2) return name + ": the destination row did not parse into two cells";
                int at = destRow.Offset + destInfo.Cells[1].End;
                source.Select(sourceRow.Offset, sourceRow.Length);
                // No dispatcher turn between the selection and the drag marker: the idle caret
                // guard would pull the row-end endpoint into the last cell and shorten the drag.
                source.NoteDragSource();
                bool moved = MoveSelectionTo(at);
                LongLineBench.Idle();
                // The source row keeps its pipes and empties both cells; the drop escapes the
                // dragged pipes as cell text (the cell paste rule).
                string movedRow = "| a | b |\n| --- | --- |\n|  |  |\n| e | f\\| c \\| d \\| |";
                if (!moved) return name + ": the whole-row drag move was not applied";
                if (text != movedRow) return name + ": the whole-row drag move wrote \"" + Flat(text) + "\"";
                int target = movedRow.IndexOf("\\| c", StringComparison.Ordinal);
                if (target < 0 || source.SelectionStart != target || source.SelectionLength != "\\| c \\| d \\|".Length)
                    return name + ": the moved row is not selected (start=" + source.SelectionStart + ", length=" + source.SelectionLength + ", expected=" + target + ")";
                Undo(); LongLineBench.Idle();
                if (text != table) return name + ": one undo after the whole-row move restored \"" + Flat(text) + "\"";
                // A selection from the first cell's text to the second cell's end.
                sourceRow = source.Document.GetLineByNumber(3);
                var sourceInfo = livePreview.Info(sourceRow);
                if (sourceInfo.Cells == null || sourceInfo.Cells.Count != 2) return name + ": the source row did not parse into two cells";
                int from = sourceRow.Offset + sourceInfo.Cells[0].Start;
                int to = sourceRow.Offset + sourceInfo.Cells[1].End;
                destRow = source.Document.GetLineByNumber(4);
                destInfo = livePreview.Info(destRow);
                at = destRow.Offset + destInfo.Cells[1].End;
                source.Select(from, to - from);
                source.NoteDragSource();
                moved = MoveSelectionTo(at);
                LongLineBench.Idle();
                string movedCells = "| a | b |\n| --- | --- |\n|  |  |\n| e | fc \\| d |";
                if (!moved) return name + ": the two-cell drag move was not applied";
                if (text != movedCells) return name + ": the two-cell drag move wrote \"" + Flat(text) + "\"";
                target = movedCells.IndexOf("c \\| d", StringComparison.Ordinal);
                if (target < 0 || source.SelectionStart != target || source.SelectionLength != "c \\| d".Length)
                    return name + ": the moved cells are not selected (start=" + source.SelectionStart + ", length=" + source.SelectionLength + ", expected=" + target + ")";
                Undo(); LongLineBench.Idle();
                if (text != table) return name + ": one undo after the two-cell move restored \"" + Flat(text) + "\"";
                // An IME result committed onto the delimiter row (its caret can sit there after
                // the raw view) goes into the first cell of the row below, never into the row that
                // keeps the table a table; with no data row it goes to the header's last cell.
                SetText(table); LongLineBench.Idle();
                DocumentLine delimiterRow = source.Document.GetLineByNumber(2);
                DocumentLine dataRow = source.Document.GetLineByNumber(3);
                var dataInfo = livePreview.Info(dataRow);
                if (dataInfo.Cells == null || dataInfo.Cells.Count == 0) return name + ": the data row did not parse into cells";
                int clampAt = source.CellClamp == null ? -1 : source.CellClamp(delimiterRow.Offset + 3);
                int clampWant = dataRow.Offset + dataInfo.Cells[0].Start;
                if (clampAt != clampWant) return name + ": the delimiter commit clamp wrote " + clampAt + ", expected " + clampWant;
                SetText("| a | b |\n| --- | --- |"); LongLineBench.Idle();
                delimiterRow = source.Document.GetLineByNumber(2);
                DocumentLine headerRow = source.Document.GetLineByNumber(1);
                var headerInfo = livePreview.Info(headerRow);
                if (headerInfo.Cells == null || headerInfo.Cells.Count == 0) return name + ": the header row did not parse into cells";
                clampAt = source.CellClamp == null ? -1 : source.CellClamp(delimiterRow.Offset + 3);
                clampWant = headerRow.Offset + headerInfo.Cells[headerInfo.Cells.Count - 1].End;
                if (clampAt != clampWant) return name + ": the header fallback clamp wrote " + clampAt + ", expected " + clampWant;
                // A drag whose selection holds only one fence of a code block is refused like
                // Delete/Cut: the partner fence must not be left behind as literal text.
                if (hideMarkers) {
                    string fenced = "앞\n\n```js\nvar a = 1;\n```\n\n뒤";
                    SetText(fenced); LongLineBench.Idle();
                    DocumentLine opening = source.Document.GetLineByNumber(3);
                    source.Select(opening.Offset, opening.Length);
                    source.NoteDragSource();
                    bool consumed = MoveSelectionTo(source.Document.GetLineByNumber(1).Offset);
                    LongLineBench.Idle();
                    if (!consumed) return name + ": a drag of one hidden fence was not refused";
                    if (text != fenced) return name + ": a drag of one hidden fence wrote \"" + Flat(text) + "\"";
                }
            }
        } finally {
            if (livePreview != null) livePreview.EndPress();
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // The table-abuse probe found a Delete burst at a cell's end that removed a three-character
    // "| `" range and cut the row's cell count. Every spot inside a row is a wall: a text delete
    // may only remove text of the cell the caret is in. Covered here: the row's padding (Delete
    // after a typed space and after a separator pipe), and Backspace at the visible start of a
    // cell whose text opens with hidden markup, in both view modes.
    internal string TableCellWallProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        bool wasHide = hideMarkers;
        try {
            for (int mode = 0; mode < 2; mode++) {
                hideMarkers = mode == 1;
                UpdateLivePreview();
                LongLineBench.Idle();
                string name = hideMarkers ? "basic" : "live";
                // Extra padding before a cell's pipe: Delete there must keep the padding, the pipe
                // and the cell count (the editor's own step took the pipe from that spot).
                string padded = "| a | b |\n| --- | --- |\n| c   | d |";
                SetText(padded); LongLineBench.Idle();
                DocumentLine row = source.Document.GetLineByNumber(3);
                var info = livePreview.Info(row);
                if (info.Cells == null || info.Cells.Count != 2) return name + ": the row did not parse into two cells";
                int padding = row.Offset + info.Cells[0].End + 1;
                source.Select(padding, 0); LongLineBench.Idle();
                int paddingCaret = source.CaretIndex;
                bool stayed = true;
                for (int press = 0; press < 3; press++) {
                    bool taken = PressDeleteKey();
                    if (!taken || text != padded || source.CaretIndex != paddingCaret) { stayed = false; break; }
                }
                if (!stayed) return name + ": Delete in the cell padding wrote \"" + Flat(text) + "\" (caret " + source.CaretIndex + "/" + paddingCaret + ")";
                // The space right after a separator pipe is the same wall. The folded basic view
                // pulls that caret stop to the next cell, so the live view drives this one.
                if (!hideMarkers) {
                    string piped = "| a | b |\n| --- | --- |\n| c | d |";
                    SetText(piped); LongLineBench.Idle();
                    row = source.Document.GetLineByNumber(3);
                    info = livePreview.Info(row);
                    int afterPipe = row.Offset + info.Cells[0].End + 2;
                    source.Select(afterPipe, 0); LongLineBench.Idle();
                    int pipeCaret = source.CaretIndex;
                    bool pipeStayed = true;
                    for (int press = 0; press < 3; press++) {
                        bool taken = PressDeleteKey();
                        if (!taken || text != piped || source.CaretIndex != pipeCaret) { pipeStayed = false; break; }
                    }
                    if (!pipeStayed) return "live: Delete after a separator pipe wrote \"" + Flat(text) + "\" (caret " + source.CaretIndex + "/" + pipeCaret + ")";
                }
                // Backspace at the cell start steps to the previous cell's end and never takes the
                // pipe or the cell's opening marker.
                string marker = "| a | b |\n| --- | --- |\n| c | `x` |";
                SetText(marker); LongLineBench.Idle();
                row = source.Document.GetLineByNumber(3);
                info = livePreview.Info(row);
                if (info.Cells == null || info.Cells.Count != 2) return name + ": the marker row did not parse into two cells";
                int previousEnd = row.Offset + info.Cells[0].End;
                int markerStart = row.Offset + info.Cells[1].Start;
                source.Select(markerStart, 0); LongLineBench.Idle();
                bool startBack = PressBackspaceKey();
                if (!startBack || text != marker || source.CaretIndex != previousEnd)
                    return name + ": Backspace at the cell start wrote \"" + Flat(text) + "\" (caret " + source.CaretIndex + "/" + previousEnd + ")";
                // One step inside the cell's visible text: Backspace may delete there, but the row
                // keeps its pipes and its two cells in both modes.
                source.Select(markerStart + 1, 0); LongLineBench.Idle();
                PressBackspaceKey();
                LongLineBench.Idle();
                DocumentLine after = source.Document.GetLineByOffset(Math.Min(source.CaretIndex, source.Document.TextLength));
                string afterText = source.Document.GetText(after);
                int pipes = 0;
                for (int p = 0; p < afterText.Length; p++) if (afterText[p] == '|') pipes++;
                var afterInfo = livePreview.Info(after);
                if (pipes != 3 || afterInfo.Cells == null || afterInfo.Cells.Count != 2)
                    return name + ": Backspace at the cell's visible start broke the row: \"" + Flat(afterText) + "\" (pipes " + pipes + ", cells " + (afterInfo.Cells == null ? -1 : afterInfo.Cells.Count) + ")";
            }
        } finally {
            hideMarkers = wasHide;
            UpdateLivePreview();
            LongLineBench.Idle();
        }
        return null;
    }
    // The user-reported flow, end to end in the real window: a normal table (no list) below a list
    // item, driven through the tool row's two buttons and Ctrl+[ / Ctrl+], in the live view and in
    // the basic view. Checks the written Markdown, the button enabled state, one undo per move, and
    // that a table under a paragraph refuses the indent with a tooltip instead of doing nothing.
    internal string TableCommandsLevelProbe() {
        return TableCommandsLevelLiveProbe();
    }
    internal string TableCommandsLevelLiveProbe() {
        
        SetMode(true); LongLineBench.Idle();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string fixture = "- item\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\nnext\n";
        SetText(fixture); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(2).Offset + 2, 0); LongLineBench.Idle();
        UpdateTableTools();
        LongLineBench.Idle();
        // The buttons say they can move this table before anything is clicked.
        if (!tableActions[10].IsEnabled) return "indent button is disabled for a table below a list item";
        // A table still at column 0 has no indent of its own, so outdent is correctly unavailable.
        if (tableActions[9].IsEnabled) return "outdent button is enabled for a table at column 0";
        EditTable(10); LongLineBench.Idle();
        string indented = "- item\n  | a | b |\n  | --- | --- |\n  | 1 | 2 |\n\nnext\n";
        if (text != indented) return "indent wrote \"" + Flat(text) + "\"";
        if (LivePreviewAttached) {
            var line = source.Document.GetLineByNumber(2);
            if (!livePreview.Info(line).Table) return "the indented header line is not a table in the live parser";
        }
        UpdateTableTools();
        if (tableActions[10].IsEnabled) return "indent button stayed enabled after the table moved into the item";
        if (!tableActions[9].IsEnabled) return "outdent button is disabled for a table inside a list item";
        if (tableActions[9].ToolTip as string == null) return "outdent button has no tooltip";
        Undo(); LongLineBench.Idle();
        if (text != fixture) return "one undo did not restore the original table: \"" + Flat(text) + "\"";
        // Ctrl+] is the same move: the key path runs IndentTable, not a second implementation.
        source.Select(source.Document.GetLineByNumber(2).Offset + 2, 0); LongLineBench.Idle();
        if (!RaiseTableLevelKeyProbe(true)) return "Ctrl+] was not handled in a table below a list item";
        LongLineBench.Idle();
        if (text != indented) return "Ctrl+] wrote \"" + Flat(text) + "\"";
        if (!RaiseTableLevelKeyProbe(false)) return "Ctrl+[ was not handled in the indented table";
        LongLineBench.Idle();
        if (text != fixture) return "Ctrl+[ wrote \"" + Flat(text) + "\"";
        // A table below a paragraph: the indent is refused and the button explains why.
        string noList = "text\n| a |\n| --- |\n";
        SetText(noList); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(2).Offset + 1, 0); LongLineBench.Idle();
        UpdateTableTools();
        LongLineBench.Idle();
        if (tableActions[10].IsEnabled) return "indent button is enabled for a table below a paragraph";
        string tip = tableActions[10].ToolTip as string;
        if (tip != T(MarkdownTableCommands.IndentNoListTip)) return "indent tooltip is \"" + tip + "\"";
        EditTable(10); LongLineBench.Idle();
        if (text != noList) return "a refused indent changed the document: \"" + Flat(text) + "\"";
        RaiseTableLevelKeyProbe(true);
        LongLineBench.Idle();
        if (text != noList) return "a refused Ctrl+] changed the document: \"" + Flat(text) + "\"";
        return null;
    }

    static string Flat(string value) {
        return value == null ? "null" : value.Replace("\r", "\\r").Replace("\n", "\\n");
    }
    // The rich cases share one window. A fresh SetMode starts a view transition even when the
    // window is already in edit mode, and a close with a transition still pending can take the
    // probe process down; the window is normally live from the first case on, so only switch when
    // it is not.
    void EnsureProbeEditSurface() {
        if (!LiveFormat) SetMode(true);
        LongLineBench.Idle();
    }
    // G8a end to end on the live surface: the row button adds a row and leaves the caret inside
    // that row's cell of the caret's own column, after the leading "| ", so the next typed
    // character stays in the cell instead of starting a new column. One undo restores the text
    // and the caret exactly, and redo brings the row and its cell caret back.
    internal string TableCommandsRowAddProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string table = "| a | b |\n| --- | --- |\n| c | d |";
        SetText(table); LongLineBench.Idle();
        int before = source.Document.GetLineByNumber(3).Offset + 6;   /* at "d", the second column */
        source.Select(before, 0); LongLineBench.Idle();
        UpdateTableTools(); LongLineBench.Idle();
        if (tableActions.Count < 1 || !tableActions[0].IsEnabled) return "the row add button is disabled inside a table";
        EditTable(0); LongLineBench.Idle();
        string added = table + "\n|  |  |";
        if (text != added) return "row add wrote \"" + Flat(text) + "\"";
        DocumentLine row = source.Document.GetLineByNumber(4);
        int relative = source.CaretIndex - row.Offset;
        if (relative != 5) return "the caret sits at " + relative + " in \"" + Flat(source.Document.GetText(row)) + "\" (expected 5: the second cell's own stop, one space inside its closing \"|\")";
        // The live row projection maps every source offset to a canonical caret stop; a caret
        // whose visual column is stale is revalidated by AvalonEdit and can be moved to the
        // nearest stop. The offset the row button writes must be such a stop on its own.
        var caret = source.TextArea.Caret;
        int written = caret.Offset;
        caret.Position = new ICSharpCode.AvalonEdit.TextViewPosition(caret.Location, -1);
        caret.CalculateCaretRectangle();
        if (caret.Offset != written) return "caret revalidation moved " + written + " -> " + caret.Offset + " (relative " + (caret.Offset - row.Offset) + ")";
        source.TextArea.PerformTextInput("x"); LongLineBench.Idle();
        string typed = table + "\n|  | x |";
        if (text != typed) return "typing in the new cell wrote \"" + Flat(text) + "\"";
        Undo(); LongLineBench.Idle();
        if (text != added) return "undo did not take the typed character back: \"" + Flat(text) + "\"";
        Undo(); LongLineBench.Idle();
        if (text != table) return "a second undo did not take the row back: \"" + Flat(text) + "\"";
        if (source.CaretIndex != before) return "undo restored the caret to " + source.CaretIndex + " instead of " + before;
        Redo(); LongLineBench.Idle();
        if (text != added) return "redo did not bring the row back: \"" + Flat(text) + "\"";
        if (source.CaretIndex - source.Document.GetLineByNumber(4).Offset != 5) return "redo did not restore the cell caret";
        // The abuse probe found the same button leaving the caret at the row's very start on
        // other shapes: CRLF, the header row, the document end and a row whose cells are empty.
        string detail = RowAddCase("body row CRLF",
            "| a | b |\r\n| --- | --- |\r\n| c | d |", 3, 6,
            "| a | b |\r\n| --- | --- |\r\n| c | d |\r\n|  |  |", 5);
        if (detail != null) return detail;
        detail = RowAddCase("header row",
            "| a | b |\n| --- | --- |\n| c | d |", 1, 2,
            "| a | b |\n| --- | --- |\n|  |  |\n| c | d |", 2);
        if (detail != null) return detail;
        detail = RowAddCase("empty cells at the document end",
            "# h\n\n|  |  |  |  |\r\n|---|---|---|---|\r\n|  가나다|  |  |  |", 5, 16,
            "# h\n\n|  |  |  |  |\r\n|---|---|---|---|\r\n|  가나다|  |  |  |\r\n|  |  |  |  |", 11);
        if (detail != null) return detail;
        detail = RowAddCase("indented header row",
            "#### 제목\n | 가 | 나 <span style=\"font-size:18px\">붙인 글자</span> |\n| --- | --- |\n|  |  |****`<span style=\"font-size:18px\">붙인 글자*", 2, 2,
            "#### 제목\n | 가 | 나 <span style=\"font-size:18px\">붙인 글자</span> |\n| --- | --- |\n|  |  |\n|  |  |****`<span style=\"font-size:18px\">붙인 글자*", 2);
        if (detail != null) return detail;
        // The abuse sequences flip the view with the mode key, and the row button then runs in
        // the formatted view (markers hidden, preview attached), where the row's pipe/padding run
        // is a hidden replacement instead of source text.
        detail = RowAddCase("formatted view empty row",
            "# h\n\n|  |  |  |  |\r\n|---|---|---|---|\r\n|  가나다|  |  |  |", 5, 16,
            "# h\n\n|  |  |  |  |\r\n|---|---|---|---|\r\n|  가나다|  |  |  |\r\n|  |  |  |  |", 11, true);
        if (detail != null) return detail;
        detail = RowAddCase("formatted view header row",
            "| a | b |\n| --- | --- |\n| c | d |", 1, 2,
            "| a | b |\n| --- | --- |\n|  |  |\n| c | d |", 2, true);
        return detail;
    }
    // One row-add shape: the caret goes to the given offset of the given 1-based line, the row
    // button runs, and the new row must keep the caret inside its cell of the same column.
    string RowAddCase(string name, string table, int caretLine, int caretColumn, string added, int expectedRelative) {
        return RowAddCase(name, table, caretLine, caretColumn, added, expectedRelative, false);
    }
    string RowAddCase(string name, string table, int caretLine, int caretColumn, string added, int expectedRelative, bool formatted) {
        if (formatted) SetLiveMode(false); else SetLiveMode(true);
        LongLineBench.Idle();
        SetText(table); LongLineBench.Idle();
        DocumentLine line = source.Document.GetLineByNumber(caretLine);
        source.Select(line.Offset + Math.Min(caretColumn, line.Length), 0); LongLineBench.Idle();
        EditTable(0); LongLineBench.Idle();
        if (text != added) return name + ": row add wrote \"" + Flat(text) + "\"";
        DocumentLine row = source.Document.GetLineByOffset(source.CaretIndex);
        int relative = source.CaretIndex - row.Offset;
        if (relative != expectedRelative) return name + ": the caret sits at " + relative + " in \"" + Flat(source.Document.GetText(row)) + "\" (expected " + expectedRelative + ")";
        var caret = source.TextArea.Caret;
        int written = caret.Offset;
        caret.Position = new ICSharpCode.AvalonEdit.TextViewPosition(caret.Location, -1);
        caret.CalculateCaretRectangle();
        if (caret.Offset != written) return name + ": caret revalidation moved relative " + (written - row.Offset) + " -> " + (caret.Offset - row.Offset) + " in \"" + Flat(source.Document.GetText(row)) + "\"";
        return null;
    }
    // Tab in the last cell of the table's last row, end to end on the live surface: the same Tab
    // path the key handler runs (SourceTabPathProbe, because a probe cannot set the keyboard's
    // modifiers). One padding-only row appears, the caret sits in its first cell, typing lands in
    // that cell, and one undo takes the row back and restores the caret from before the key.
    // Shift+Tab in the same cell keeps its old cell move (no row), a selection in that cell is
    // collapsed without deleting text, a header-only table gains its first body row, and the
    // detached 원문 view edits the same source.
    internal string TableCommandsTabEndRowProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string table = "| a | b |\n|---|---|\n| c | d |";
        string added = table + "\n|  |  |";
        SetText(table); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(3).Offset + 7, 0); LongLineBench.Idle();
        int before = source.CaretIndex;
        if (!SourceTabPathProbe(false)) return "the Tab path was not handled at the table's end";
        if (text != added) return "Tab at the table's end wrote \"" + Flat(text) + "\"";
        DocumentLine row = source.Document.GetLineByNumber(4);
        int relative = source.CaretIndex - row.Offset;
        if (relative != 2) return "the caret sits at " + relative + " in \"" + Flat(source.Document.GetText(row)) + "\" (expected 2: the first cell's own stop, one space inside its closing \"|\")";
        source.TextArea.PerformTextInput("x"); LongLineBench.Idle();
        if (text != table + "\n| x |  |") return "typing in the new row wrote \"" + Flat(text) + "\"";
        Undo(); LongLineBench.Idle();
        if (text != added) return "undo after typing did not keep the row: \"" + Flat(text) + "\"";
        Undo(); LongLineBench.Idle();
        if (text != table) return "one undo did not take the row back: \"" + Flat(text) + "\"";
        if (source.CaretIndex != before) return "undo restored the caret to " + source.CaretIndex + " instead of " + before;
        // Shift+Tab in the same cell is still the previous-cell move: no row is written.
        SetText(table); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(3).Offset + 7, 0); LongLineBench.Idle();
        if (!SourceTabPathProbe(true)) return "Shift+Tab was not handled in the last cell";
        if (text != table) return "Shift+Tab added a row: \"" + Flat(text) + "\"";
        if (source.SelectionLength == 0 || source.Document.GetText(source.SelectionStart, source.SelectionLength) != "c")
            return "Shift+Tab did not select the previous cell (selection " + source.SelectionStart + "+" + source.SelectionLength + ")";
        // A selection in the last cell is collapsed and the row added; no selected text goes.
        SetText(table); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(3).Offset + 6, 1); LongLineBench.Idle();
        if (!SourceTabPathProbe(false)) return "the Tab path did not take a selection in the last cell";
        if (text != added) return "Tab over a last-cell selection wrote \"" + Flat(text) + "\"";
        // A header-only table gains the first body row the same way.
        SetText("| a | b |\n|---|---|"); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(1).Offset + 7, 0); LongLineBench.Idle();
        if (!SourceTabPathProbe(false)) return "the Tab path did not take the header's last cell";
        if (text != "| a | b |\n|---|---|\n|  |  |") return "Tab in a header-only table wrote \"" + Flat(text) + "\"";
        // The detached 원문 view edits the same source: the append is not a live-preview rule.
        loading = true; preview.IsChecked = true; loading = false;
        SwitchSurface(); LongLineBench.Idle();
        bool detached = !LivePreviewAttached;
        SetText(table); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(3).Offset + 7, 0); LongLineBench.Idle();
        bool rawTaken = SourceTabPathProbe(false);
        string rawText = text;
        loading = true; preview.IsChecked = false; loading = false;
        SwitchSurface(); LongLineBench.Idle();
        if (!detached) return "the source toggle did not detach the preview";
        if (!rawTaken) return "the detached source view did not handle Tab at the table's end";
        if (rawText != added) return "the detached source view wrote \"" + Flat(rawText) + "\"";
        return null;
    }
    // G5 end to end on the live surface: the table button on a table row writes the grid below the
    // old table with one blank line on each side (the existing separator is reused) and selects
    // the new first header cell, all as one undo step.
    internal string TableCommandsInsertGapProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string fixture = "| a | b |\n|---|---|\n| c | d |\n\nnext\n";
        SetText(fixture); LongLineBench.Idle();
        int before = source.Document.GetLineByNumber(3).Offset + 6;
        source.Select(before, 0); LongLineBench.Idle();
        UpdateTableTools(); LongLineBench.Idle();
        InsertTable(2, 2); LongLineBench.Idle();
        string expected = "| a | b |\n|---|---|\n| c | d |\n\n| 제목1 | 제목2 |\n|---|---|\n|  |  |\n\nnext\n";
        if (text != expected) return "table insert wrote \"" + Flat(text) + "\"";
        int cellStart = expected.IndexOf("| 제목1", StringComparison.Ordinal);
        if (source.SelectionStart != cellStart + 2 || source.SelectionLength != 3) {
            return "selection is " + source.SelectionStart + "+" + source.SelectionLength
                + ", expected " + (cellStart + 2) + "+3 (the first header cell)";
        }
        Undo(); LongLineBench.Idle();
        if (text != fixture) return "one undo did not restore the document: \"" + Flat(text) + "\"";
        if (source.CaretIndex != before) return "undo restored the caret to " + source.CaretIndex + " instead of " + before;
        return null;
    }
    // R1 end to end: a selection that starts above a table and ends below it runs the block
    // command on the paragraph lines only; every table row keeps its exact text, one undo restores
    // the document, and a selection made only of table rows changes nothing.
    internal string TableCommandsBlockSpanProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string fixture = "위 문단\n| a | b |\n|---|---|\n| c | d |\n아래 문단";
        string quoted = "> 위 문단\n| a | b |\n|---|---|\n| c | d |\n> 아래 문단";
        string listed = "- 위 문단\n| a | b |\n|---|---|\n| c | d |\n- 아래 문단";
        string headed = "# 위 문단\n| a | b |\n|---|---|\n| c | d |\n# 아래 문단";
        SetText(fixture); LongLineBench.Idle();
        source.Select(0, fixture.Length); LongLineBench.Idle();
        LiveQuote(); LongLineBench.Idle();
        if (text != quoted) return "quote on a spanning selection wrote \"" + Flat(text) + "\"";
        Undo(); LongLineBench.Idle();
        if (text != fixture) return "one undo did not restore the quote change: \"" + Flat(text) + "\"";
        source.Select(0, fixture.Length); LongLineBench.Idle();
        LiveList(ListKind.Bullet); LongLineBench.Idle();
        if (text != listed) return "list on a spanning selection wrote \"" + Flat(text) + "\"";
        Undo(); LongLineBench.Idle();
        if (text != fixture) return "one undo did not restore the list change: \"" + Flat(text) + "\"";
        source.Select(0, fixture.Length); LongLineBench.Idle();
        LiveHeading(1); LongLineBench.Idle();
        if (text != headed) return "heading on a spanning selection wrote \"" + Flat(text) + "\"";
        Undo(); LongLineBench.Idle();
        if (text != fixture) return "one undo did not restore the heading change: \"" + Flat(text) + "\"";
        /* A selection made only of table rows changes nothing. */
        source.Select(fixture.IndexOf("| a", StringComparison.Ordinal), "| a | b |\n|---|---|\n| c | d |".Length); LongLineBench.Idle();
        LiveQuote(); LongLineBench.Idle();
        if (text != fixture) return "quote of only table rows changed the document: \"" + Flat(text) + "\"";
        return null;
    }
    // R2: the table gate turns the block buttons off on a table row and puts them back when the
    // caret leaves; a trip through the detached 원문 view never leaves them stuck.
    internal string TableCommandsGateProbe() {
        EnsureProbeEditSurface();
        if (!LivePreviewAttached || !LiveFormat) return "live surface did not attach (attached=" + LivePreviewAttached + ", live=" + LiveFormat + ")";
        string fixture = "| a |\n|---|\n| b |\n\n문단\n";
        SetText(fixture); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(2).Offset + 2, 0); LongLineBench.Idle();
        UpdateTools(); LongLineBench.Idle();
        if (quoteButton.IsEnabled || headingButton.IsEnabled || listButton.IsEnabled || taskButton.IsEnabled || ruleButton.IsEnabled)
            return "a block button is enabled on a table row";
        source.Select(source.Document.GetLineByNumber(5).Offset + 1, 0); LongLineBench.Idle();
        UpdateTools(); LongLineBench.Idle();
        if (!quoteButton.IsEnabled || !headingButton.IsEnabled || !listButton.IsEnabled || !taskButton.IsEnabled || !ruleButton.IsEnabled)
            return "a block button stayed disabled after the caret left the table";
        /* A trip through the detached 원문 view and back leaves them usable again. */
        loading = true; preview.IsChecked = true; loading = false; SwitchSurface(); LongLineBench.Idle();
        if (LivePreviewAttached) return "the source toggle did not detach the preview";
        loading = true; preview.IsChecked = false; loading = false; SwitchSurface(); LongLineBench.Idle();
        source.Select(source.Document.GetLineByNumber(5).Offset + 1, 0); LongLineBench.Idle();
        UpdateTools(); LongLineBench.Idle();
        if (!quoteButton.IsEnabled) return "the quote button stayed disabled after returning to the live view";
        return null;
    }
}
}
