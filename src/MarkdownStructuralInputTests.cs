using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// --structural-probe <outDir>: table checks for MarkdownStructuralInput. Every case builds
// a fresh TextDocument, applies the returned edits in list order exactly like the host and
// compares text and selection with the «»/‸ marked expectation. The probe class is kept
// separate so helper names never land in MainWindow. The optional extra section drives the
// live view's list autoformat through a real window (see LiveAutoFormatProbe).
internal static class MarkdownStructuralInputProbe {
    internal static int Run(string outDir, Action<Action<string,bool,string>> extra) {
        string dir;
        try {
            dir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(dir);
        } catch (Exception) {
            return 2;
        }
        StringBuilder report = new StringBuilder();
        report.Append("# MarkdownStructuralInput probe\n\n");
        bool all = true;
        int passed = 0;
        int failed = 0;
        int cases = 0;
        int exit = 0;
        try {
            List<InputCase> list = BuildCases();
            for (int i = 0; i < list.Count; i++) {
                cases++;
                string detail = null;
                bool ok = RunCase(list[i], out detail);
                if (ok) {
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

            string sequenceDetail;
            cases++;
            if (RunEnterSequence(out sequenceDetail)) {
                passed++;
                report.Append("PASS sequence empty ordered item repeats 1..4\n");
            } else {
                failed++;
                all = false;
                report.Append("FAIL sequence empty ordered item repeats 1..4");
                if (sequenceDetail != null) report.Append(": ").Append(sequenceDetail);
                report.Append('\n');
            }
            cases++;
            if (RunBackspaceSequence(out sequenceDetail)) {
                passed++;
                report.Append("PASS sequence backspace removes marker then indentation\n");
            } else {
                failed++;
                all = false;
                report.Append("FAIL sequence backspace removes marker then indentation");
                if (sequenceDetail != null) report.Append(": ").Append(sequenceDetail);
                report.Append('\n');
            }
            cases++;
            if (RunAutoFormatUndo(out sequenceDetail)) {
                passed++;
                report.Append("PASS sequence autoformat undo restores the exact text\n");
            } else {
                failed++;
                all = false;
                report.Append("FAIL sequence autoformat undo restores the exact text");
                if (sequenceDetail != null) report.Append(": ").Append(sequenceDetail);
                report.Append('\n');
            }
            cases++;
            if (RunTableEndTabUndo(out sequenceDetail)) {
                passed++;
                report.Append("PASS sequence table-end Tab appends one row as one undo step\n");
            } else {
                failed++;
                all = false;
                report.Append("FAIL sequence table-end Tab appends one row as one undo step");
                if (sequenceDetail != null) report.Append(": ").Append(sequenceDetail);
                report.Append('\n');
            }
            cases++;
            if (RunBigTableChecks(out sequenceDetail)) {
                passed++;
                report.Append("PASS sequence table keys read a 2503-row table\n");
            } else {
                failed++;
                all = false;
                report.Append("FAIL sequence table keys read a 2503-row table");
                if (sequenceDetail != null) report.Append(": ").Append(sequenceDetail);
                report.Append('\n');
            }
            cases++;
            if (RunLineStructureChecks(out sequenceDetail)) {
                passed++;
                report.Append("PASS sequence caret-row line-structure detection\n");
            } else {
                failed++;
                all = false;
                report.Append("FAIL sequence caret-row line-structure detection");
                if (sequenceDetail != null) report.Append(": ").Append(sequenceDetail);
                report.Append('\n');
            }
            cases++;
            if (RunFenceDeleteChecks(out sequenceDetail)) {
                passed++;
                report.Append("PASS sequence Delete at a fence boundary is detected\n");
            } else {
                failed++;
                all = false;
                report.Append("FAIL sequence Delete at a fence boundary is detected");
                if (sequenceDetail != null) report.Append(": ").Append(sequenceDetail);
                report.Append('\n');
            }
            if (extra != null) {
                report.Append("\n## Live view list autoformat\n");
                Action<string, bool, string> extraCheck = delegate(string name, bool ok, string detail) {
                    cases++;
                    if (ok) {
                        passed++;
                        report.Append("PASS ").Append(name).Append('\n');
                    } else {
                        failed++;
                        all = false;
                        report.Append("FAIL ").Append(name);
                        if (detail != null) report.Append(": ").Append(detail);
                        report.Append('\n');
                    }                };
                try {
                    extra(extraCheck);
                } catch (Exception ex) {
                    failed++;
                    all = false;
                    report.Append("FAIL live view list autoformat: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');
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
                File.WriteAllText(Path.Combine(dir, "structural-probe.txt"), report.ToString(), new UTF8Encoding(false));
                string passFile = Path.Combine(dir, "structural-probe-PASS.txt");
                if (exit == 0 && all) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
                else if (File.Exists(passFile)) File.Delete(passFile);
            } catch (Exception) {
                exit = 2;
            }
        }
        if (exit == 0 && !all) exit = 1;
        return exit;
    }

    sealed class InputCase {
        internal string Name;
        internal string Input;
        internal string Expected;   // null means the command must return null
        internal Func<TextDocument, int, int, CommandResult> Command;
        internal bool RequireNoEdits;
    }

    static void Add(List<InputCase> cases, string name, string input, string expected,
        Func<TextDocument, int, int, CommandResult> command) {
        InputCase item = new InputCase();
        item.Name = name;
        item.Input = input;
        item.Expected = expected;
        item.Command = command;
        cases.Add(item);
    }

    static void AddNoEdits(List<InputCase> cases, string name, string input, string expected,
        Func<TextDocument, int, int, CommandResult> command) {
        InputCase item = new InputCase();
        item.Name = name;
        item.Input = input;
        item.Expected = expected;
        item.Command = command;
        item.RequireNoEdits = true;
        cases.Add(item);
    }

    static Func<TextDocument, int, int, CommandResult> EnterKey() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownStructuralInput.Enter(document, start, length);
        };
    }

    // The host's Delete / Backspace arm that joins a line into the ordered item above it (LiveMarkdownCommands.SourceDelete,
    // SourceBackspace): the line break goes and the items after it keep counting. Null leaves the key to the editor's own delete.
    static Func<TextDocument, int, int, CommandResult> JoinOrderedKey(bool back) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownStructuralInput.JoinOrdered(document, start, back);
        };
    }

    static Func<TextDocument, int, int, CommandResult> TabKey(bool shift) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownStructuralInput.Tab(document, start, length, shift);
        };
    }

    static Func<TextDocument, int, int, CommandResult> BackspaceKey() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownStructuralInput.Backspace(document, start);
        };
    }

    // The host's Delete arm (LiveMarkdownCommands.SourceDelete) for a fence boundary: the gate
    // asks whether the key touches a fence, then the pair of fence lines goes and the block's
    // text stays. Null leaves the key to the editor's ordinary character delete.
    static Func<TextDocument, int, int, CommandResult> FenceDeleteKey() {
        return delegate(TextDocument document, int start, int length) {
            if (!MarkdownStructuralInput.DeleteAtFenceBoundary(document, start)) return null;
            return MarkdownCommands.DeleteFenceBlock(document, start);
        };
    }

    // `count` one-letter lines, each with its line break: filler that puts a block far down a
    // long document (the pair read must not stop at a 400-line bound).
    static string Rows(int count) {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < count; i++) builder.Append("x\n");
        return builder.ToString();
    }

    // The editor's own Delete/Backspace/Cut over a selection (MarkdownLivePreview.
    // FenceSelectionDelete): the selected text and both fence lines of every block it touches.
    static Func<TextDocument, int, int, CommandResult> FenceSelectionKey() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.RemoveSelectionWithFences(document, start, length);
        };
    }

    static Func<TextDocument, int, int, CommandResult> OutdentTableKey() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownStructuralInput.OutdentTable(document, start, length);
        };
    }

    static Func<TextDocument, int, int, CommandResult> IndentTableKey() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownStructuralInput.IndentTable(document, start, length);
        };
    }

    static Func<TextDocument, int, int, CommandResult> AutoFormatKey() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownStructuralInput.AutoFormatListMarker(document, start, length);
        };
    }

    // Undo of one result must restore the exact input. The command's edits are applied in list
    // order (descending), so the text each one removed is captured from the untouched document;
    // undoing then walks ascending, where every offset left of the edit already holds its original
    // text again.
    static bool UndoRestores(string input, CommandResult result, out string detail) {
        TextDocument document = new TextDocument(input);
        List<string> removed = new List<string>();
        for (int i = 0; i < result.Edits.Count; i++) {
            SourceEdit edit = result.Edits[i];
            removed.Insert(0, document.GetText(edit.Offset, edit.Length));
            document.Replace(edit.Offset, edit.Length, edit.Text);
        }
        for (int i = 0; i < result.Edits.Count; i++) {
            SourceEdit edit = result.Edits[result.Edits.Count - 1 - i];
            document.Replace(edit.Offset, edit.Text.Length, removed[i]);
        }
        if (document.Text != input) {
            detail = "undo expected=\"" + Escape(input) + "\" actual=\"" + Escape(document.Text) + "\"";
            return false;
        }
        detail = null;
        return true;
    }

    static void ParseMarked(string marked, out string text, out int selectionStart, out int selectionLength) {
        int caret = marked.IndexOf('‸');
        if (caret >= 0) {
            text = marked.Remove(caret, 1);
            selectionStart = caret;
            selectionLength = 0;
            return;
        }
        int open = marked.IndexOf('«');
        if (open >= 0) {
            int close = marked.IndexOf('»', open + 1);
            if (close < 0) throw new InvalidOperationException("unclosed selection marker");
            text = marked.Substring(0, open) + marked.Substring(open + 1, close - open - 1) + marked.Substring(close + 1);
            selectionStart = open;
            selectionLength = close - open - 1;
            return;
        }
        text = marked;
        selectionStart = 0;
        selectionLength = 0;
    }

    static bool RunCase(InputCase test, out string detail) {
        string input;
        int inputStart, inputLength;
        ParseMarked(test.Input, out input, out inputStart, out inputLength);
        if (test.Expected == null) {
            CommandResult nil = test.Command(new TextDocument(input), inputStart, inputLength);
            detail = nil == null ? null : "expected null, got " + nil.Edits.Count.ToString(CultureInfo.InvariantCulture) + " edit(s)";
            return nil == null;
        }

        string expected;
        int expectedStart, expectedLength;
        ParseMarked(test.Expected, out expected, out expectedStart, out expectedLength);
        CommandResult result = test.Command(new TextDocument(input), inputStart, inputLength);
        if (result == null) {
            detail = "command returned null";
            return false;
        }
        if (test.RequireNoEdits && result.Edits.Count != 0) {
            detail = "expected no edits, got " + result.Edits.Count.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        string invalid = ValidateEdits(input.Length, result);
        if (invalid != null) {
            detail = invalid;
            return false;
        }
        TextDocument edited = new TextDocument(input);
        ApplyEdits(edited, result);
        string actual = edited.Text;
        if (actual != expected) {
            detail = "text expected=\"" + Escape(expected) + "\" actual=\"" + Escape(actual) + "\"";
            return false;
        }
        if (result.SelectionStart != expectedStart || result.SelectionLength != expectedLength) {
            detail = "selection expected=" + expectedStart.ToString(CultureInfo.InvariantCulture)
                + "+" + expectedLength.ToString(CultureInfo.InvariantCulture)
                + " actual=" + result.SelectionStart.ToString(CultureInfo.InvariantCulture)
                + "+" + result.SelectionLength.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        detail = null;
        return true;
    }

    static void ApplyEdits(TextDocument document, CommandResult result) {
        for (int i = 0; i < result.Edits.Count; i++) {
            SourceEdit edit = result.Edits[i];
            document.Replace(edit.Offset, edit.Length, edit.Text);
        }
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

    static bool RunEnterSequence(out string detail) {
        TextDocument document = new TextDocument("1. ");
        int caret = document.TextLength;
        for (int step = 0; step < 3; step++) {
            // The newline path (Shift+Enter, the IME's line break) keeps continuing an empty
            // item; the Enter key itself ends it (see "enter empty ordered item").
            CommandResult result = MarkdownStructuralInput.Enter(document, caret, 0, false);
            if (result == null) {
                detail = "Enter returned null at step " + (step + 1).ToString(CultureInfo.InvariantCulture);
                return false;
            }
            string invalid = ValidateEdits(document.TextLength, result);
            if (invalid != null) {
                detail = invalid;
                return false;
            }
            ApplyEdits(document, result);
            caret = result.SelectionStart;
        }
        string expected = "1. \n2. \n3. \n4. ";
        if (document.Text != expected) {
            detail = "text expected=\"" + Escape(expected) + "\" actual=\"" + Escape(document.Text) + "\"";
            return false;
        }
        if (caret != expected.Length) {
            detail = "caret expected=" + expected.Length.ToString(CultureInfo.InvariantCulture)
                + " actual=" + caret.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        detail = null;
        return true;
    }

    static bool RunBackspaceSequence(out string detail) {
        TextDocument document = new TextDocument("  - ");
        int caret = document.TextLength;
        CommandResult first = MarkdownStructuralInput.Backspace(document, caret);
        if (first == null) {
            detail = "first Backspace returned null";
            return false;
        }
        string invalid = ValidateEdits(document.TextLength, first);
        if (invalid != null) {
            detail = invalid;
            return false;
        }
        ApplyEdits(document, first);
        caret = first.SelectionStart;
        if (document.Text != "  ") {
            detail = "after first Backspace expected=\"  \" actual=\"" + Escape(document.Text) + "\"";
            return false;
        }
        if (caret != 2) {
            detail = "after first Backspace caret expected=2 actual=" + caret.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        CommandResult second = MarkdownStructuralInput.Backspace(document, caret);
        if (second == null) {
            detail = "second Backspace returned null";
            return false;
        }
        invalid = ValidateEdits(document.TextLength, second);
        if (invalid != null) {
            detail = invalid;
            return false;
        }
        ApplyEdits(document, second);
        caret = second.SelectionStart;
        if (document.Text != "") {
            detail = "after second Backspace expected empty actual=\"" + Escape(document.Text) + "\"";
            return false;
        }
        if (caret != 0) {
            detail = "after second Backspace caret expected=0 actual=" + caret.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        detail = null;
        return true;
    }

    static bool RunAutoFormatUndo(out string detail) {
        // Every autoformat case must undo back to its own input text (one undo entry = one result).
        string[] inputs = new string[] { "*", "+", "007.", "12.", "1.", "-", "0.", "a*", "  *" };
        for (int i = 0; i < inputs.Length; i++) {
            TextDocument document = new TextDocument(inputs[i]);
            CommandResult result = MarkdownStructuralInput.AutoFormatListMarker(document, document.TextLength, 0);
            if (result == null) continue;
            string invalid = ValidateEdits(document.TextLength, result);
            if (invalid != null) { detail = inputs[i] + ": " + invalid; return false; }
            if (!UndoRestores(inputs[i], result, out detail)) { detail = inputs[i] + ": " + detail; return false; }
        }
        detail = null;
        return true;
    }

    // Tab in the last cell of the table's last row is one insertion, so the host records it as one
    // undo entry: undoing that entry restores the exact text (the host's history also puts the
    // caret back before the key). The row text and the caret after the append are the case
    // expectations above; this sequence checks the one-entry contract.
    static bool RunTableEndTabUndo(out string detail) {
        string input = "| a | b |\n| --- | --- |\n| c | d |";
        TextDocument document = new TextDocument(input);
        int caret = document.GetLineByNumber(3).Offset + 7;
        CommandResult result = MarkdownStructuralInput.Tab(document, caret, 0, false);
        if (result == null) { detail = "Tab at the table end returned null"; return false; }
        if (result.Edits.Count != 1) {
            detail = "expected one edit for the appended row, got " + result.Edits.Count.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        return UndoRestores(input, result, out detail);
    }

    // A table is structure however many rows it has: the keys read its extent with the table
    // commands' own rule and cap (MarkdownTableCommands.MaxExpandLines, the renderer has none), so
    // a row far below the header is still a table row. Tab there moves to the next cell instead of
    // reaching the editor's TabForward, which replaced the selected cell text with one tab
    // character and left the table (bughunt3 tables item 1: the keys' own 400-line cap made a
    // table the screen drew invisible to the key).
    static bool RunBigTableChecks(out string detail) {
        StringBuilder builder = new StringBuilder("| a | b |\n| --- | --- |");
        for (int r = 0; r < 2501; r++) {
            builder.Append("\n| c").Append(r.ToString(CultureInfo.InvariantCulture))
                .Append(" | d").Append(r.ToString(CultureInfo.InvariantCulture)).Append(" |");
        }
        // A paragraph after the table keeps the document longer than the table, so the whole-table
        // selection below is decided by the table rule and not by the whole-document shortcut.
        builder.Append("\npost");
        TextDocument document = new TextDocument(builder.ToString());
        if (document.LineCount != 2504) {
            detail = "fixture line count " + document.LineCount.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        DocumentLine row = document.GetLineByNumber(2500);
        string rowText = document.GetText(row.Offset, row.Length);
        if (rowText != "| c2497 | d2497 |") {
            detail = "row 2500 is [" + Escape(rowText) + "]";
            return false;
        }
        int cell = row.Offset + 2;
        if (!MarkdownStructuralInput.TableAtCaret(document, cell)) {
            detail = "row 2500 is not a table row for the keys";
            return false;
        }
        CommandResult tab = MarkdownStructuralInput.Tab(document, cell, 5, false);
        if (tab == null) { detail = "Tab in row 2500 returned null"; return false; }
        if (tab.Edits.Count != 0) {
            detail = "Tab in row 2500 wrote " + tab.Edits.Count.ToString(CultureInfo.InvariantCulture) + " edit(s)";
            return false;
        }
        int expected = row.Offset + 10;
        if (tab.SelectionStart != expected || tab.SelectionLength != 5) {
            detail = "Tab in row 2500 selection expected=" + expected.ToString(CultureInfo.InvariantCulture)
                + "+5 actual=" + tab.SelectionStart.ToString(CultureInfo.InvariantCulture)
                + "+" + tab.SelectionLength.ToString(CultureInfo.InvariantCulture);
            return false;
        }
        // Whole-table detection at the same depth: both edges of the 2503-row table resolve, so a
        // selection that covers all of it is ordinary text for the table commands.
        DocumentLine first = document.GetLineByNumber(1);
        DocumentLine last = document.GetLineByNumber(2503);
        if (!MarkdownTableCommands.SelectionCoversWholeTables(document, first.Offset, last.EndOffset - first.Offset)) {
            detail = "a selection over the whole 2503-row table is not whole-table text";
            return false;
        }
        detail = null;
        return true;
    }

    // Editor.LineStructureChanged decides whether the caret-row check is posted after an edit:
    // a line break on either side counts at any length (a multi-line delete is one big removal,
    // which the old 8-character cap ignored), and a whole-document replacement is answered
    // without reading its buffer (Render on load, tab switch or discard rebuilt every line, and
    // the posted check is what reveals a caret the caller restored). The removal cases also
    // cover the RopeTextSource path of a long removal, which is scanned through
    // ITextSource.IndexOfAny instead of being materialized as one string.
    static bool RunLineStructureChecks(out string detail) {
        string error;
        if ((error = LineStructureCase("head\nAAAAAAAA\nBBBBBBBB\ntail\n", 5, 9, "", true)) != null) { detail = error; return false; }
        if ((error = LineStructureCase("head\nAAAAAAAAA\ntail\n", 5, 9, "", false)) != null) { detail = error; return false; }
        if ((error = LineStructureCase("head\ntail\n", 4, 0, "x\ny", true)) != null) { detail = error; return false; }
        if ((error = LineStructureCase("head\ntail\n", 4, 1, "", true)) != null) { detail = error; return false; }
        string longText = new string('a', 150) + "\n" + new string('b', 60) + "\ntail";
        if ((error = LineStructureCase(longText, 10, 205, "", true)) != null) { detail = error; return false; }
        TextDocument document = new TextDocument("old\r\ntext\r\n");
        DocumentChangeEventArgs whole = CaptureChange(document, delegate { document.Text = "new\ntext\nwith\nbreaks\n"; });
        if (whole == null) { detail = "whole-document replacement captured no change"; return false; }
        if (!MainWindow.LineStructureChanged(whole, document.TextLength)) {
            detail = "whole-document replacement was not treated as a line-structure edit";
            return false;
        }
        // The same answer without a line break in the new buffer: the whole-document case must
        // not depend on scanning the text (the old path returned false here).
        document = new TextDocument("old\r\ntext\r\n");
        whole = CaptureChange(document, delegate { document.Text = "single line without a break"; });
        if (whole == null || !MainWindow.LineStructureChanged(whole, document.TextLength)) {
            detail = "whole-document replacement without a line break was not treated as structural";
            return false;
        }
        document = new TextDocument("old\ntext\n");
        whole = CaptureChange(document, delegate { document.Remove(0, document.TextLength); });
        if (whole == null) { detail = "whole-document removal captured no change"; return false; }
        if (MainWindow.LineStructureChanged(whole, document.TextLength)) {
            detail = "whole-document removal counted as a line-structure edit";
            return false;
        }
        detail = null;
        return true;
    }

    static string LineStructureCase(string text, int offset, int length, string replacement, bool expected) {
        TextDocument document = new TextDocument(text);
        DocumentChangeEventArgs change = CaptureChange(document, delegate {
            if (length > 0) document.Remove(offset, length);
            if (replacement.Length > 0) document.Insert(offset, replacement);
        });
        if (change == null) return "no change captured for \"" + Escape(text) + "\"";
        bool actual = MainWindow.LineStructureChanged(change, document.TextLength);
        if (actual != expected) {
            return "text=\"" + Escape(text) + "\" offset=" + offset.ToString(CultureInfo.InvariantCulture)
                + " length=" + length.ToString(CultureInfo.InvariantCulture)
                + " actual=" + actual + " expected=" + expected;
        }
        return null;
    }

    static DocumentChangeEventArgs CaptureChange(TextDocument document, Action change) {
        DocumentChangeEventArgs captured = null;
        EventHandler<DocumentChangeEventArgs> handler = delegate(object sender, DocumentChangeEventArgs e) { captured = e; };
        document.Changed += handler;
        try { change(); } finally { document.Changed -= handler; }
        return captured;
    }

    // F2 (Delete): the host asks MarkdownStructuralInput.DeleteAtFenceBoundary before it answers
    // a Delete at a fence line; true sends the key to the fence-pair rule (MarkdownCommands.
    // DeleteFenceBlock) instead of the ordinary character delete. These are the boundary
    // positions the rule must catch and the ordinary edits it must leave to the editor
    // (bughunt2 insert 10; the key's own outcome is checked by the fence-pair cases above).
    static bool RunFenceDeleteChecks(out string detail) {
        string[][] cases = new string[][] {
            // text, caret offset, expected ("1" = the key is swallowed)
            new string[] { "```\ncode\n```", "3", "1" },          // end of the opening fence: the code line would become its info string
            new string[] { "```\ncode\n```", "0", "1" },          // deletes the first marker character
            new string[] { "```\ncode\n```", "1", "1" },          // inside the marker run
            new string[] { "```\ncode\n```", "5", "0" },          // inside the code text
            new string[] { "```\ncode\n```", "8", "1" },          // end of the code line: joins the closing fence
            new string[] { "```csharp\ncode", "9", "1" },         // end of the fence's info string
            new string[] { "text\n```\ncode", "4", "1" },         // end of the text line: joins the fence line
            new string[] { "text\n```", "2", "0" },               // inside the text, not at the line end
            new string[] { "```\ncode\n```\nnext", "12", "1" },   // end of the closing fence
            new string[] { "```\ncode\n```\nnext", "13", "0" },   // the next line's own text
            new string[] { "```", "3", "0" },                     // nothing follows the last fence line
            new string[] { "```\ncode\n```\n\nnext", "12", "0" }, // a blank line after the closing fence may be joined
            new string[] { "```\n\ncode\n```", "3", "0" },        // a blank line after the opening fence may be joined
            new string[] { "\n```\ncode\n```", "0", "0" }         // an empty line before the opening fence is just removed
        };
        for (int i = 0; i < cases.Length; i++) {
            TextDocument document = new TextDocument(cases[i][0]);
            int caret = Int32.Parse(cases[i][1], CultureInfo.InvariantCulture);
            bool expected = cases[i][2] == "1";
            bool actual = MarkdownStructuralInput.DeleteAtFenceBoundary(document, caret);
            if (actual != expected) {
                detail = "text=\"" + Escape(cases[i][0]) + "\" caret=" + cases[i][1]
                    + " expected=" + expected + " actual=" + actual;
                return false;
            }
        }
        detail = null;
        return true;
    }

    static List<InputCase> BuildCases() {
        List<InputCase> cases = new List<InputCase>();

        // Enter: bullet items keep the character and the indentation.
        Add(cases, "enter bullet end", "- a‸", "- a\n- ‸", EnterKey());
        Add(cases, "enter bullet middle", "- a‸bc", "- a\n- ‸bc", EnterKey());
        Add(cases, "enter bullet at body start", "- ‸abc", "- \n- ‸abc", EnterKey());
        Add(cases, "enter bullet empty item", "- ‸", "‸", EnterKey());
        Add(cases, "enter star bullet", "* a‸", "* a\n* ‸", EnterKey());
        Add(cases, "enter plus bullet", "+ a‸", "+ a\n+ ‸", EnterKey());
        Add(cases, "enter indented bullet", "  - a‸", "  - a\n  - ‸", EnterKey());
        Add(cases, "enter tab indented bullet", "\t- a‸", "\t- a\n\t- ‸", EnterKey());
        Add(cases, "enter bullet CRLF", "- a‸\r\nb", "- a\r\n- ‸\r\nb", EnterKey());
        // A marker's separator may be a tab, exactly like MarkdownCommands.ParsePrefix reads it;
        // before, the line looked like a paragraph and Enter wrote no continuation at all.
        Add(cases, "enter bullet with tab separator", "-\titem‸", "-\titem\n- ‸", EnterKey());
        Add(cases, "enter quoted bullet with tab separator", ">\t- item‸", ">\t- item\n>\t- ‸", EnterKey());
        Add(cases, "enter task with tab separator", "- [ ]\titem‸", "- [ ]\titem\n- [ ] ‸", EnterKey());

        // Enter: task items always continue unchecked with the same indentation.
        Add(cases, "enter task unchecked", "- [ ] a‸", "- [ ] a\n- [ ] ‸", EnterKey());
        Add(cases, "enter task checked", "- [x] a‸", "- [x] a\n- [ ] ‸", EnterKey());
        Add(cases, "enter task uppercase X", "- [X] a‸", "- [X] a\n- [ ] ‸", EnterKey());
        Add(cases, "enter task empty", "- [ ] ‸", "‸", EnterKey());
        Add(cases, "enter task without trailing space", "- [x]‸", "‸", EnterKey());
        Add(cases, "enter nested empty bullet outdents", "- a\n  - ‸", "- a\n- ‸", EnterKey());
        Add(cases, "enter nested empty task outdents", "- a\n  - [ ] ‸", "- a\n- [ ] ‸", EnterKey());
        // The box ends at "]": the screen draws "- [ ]x" as a task, so Enter, Tab and Backspace
        // read the same marker (bughunt2 insert 11) instead of treating the line as a bullet.
        Add(cases, "enter task without a space before its text", "- [ ]x‸", "- [ ]x\n- [ ] ‸", EnterKey());
        Add(cases, "enter task CRLF", "- [ ] a‸\r\nb", "- [ ] a\r\n- [ ] ‸\r\nb", EnterKey());

        // Enter: ordered items increase and renumber the following items.
        Add(cases, "enter ordered end", "1. a‸", "1. a\n2. ‸", EnterKey());
        Add(cases, "enter ordered middle", "2. a‸b", "2. a\n3. ‸b", EnterKey());
        Add(cases, "enter ordered multi digit", "9. a‸", "9. a\n10. ‸", EnterKey());
        Add(cases, "enter ordered renumbers following", "1. a‸\n1. b\n1. c", "1. a\n2. ‸\n3. b\n4. c", EnterKey());
        Add(cases, "enter ordered stops at blank line", "1. a‸\n1. b\n\n1. c", "1. a\n2. ‸\n3. b\n\n1. c", EnterKey());
        Add(cases, "enter ordered stops at other indent", "1. a‸\n  1. b", "1. a\n2. ‸\n  1. b", EnterKey());
        Add(cases, "enter ordered fixes following numbers", "1. a‸\n2. b\n3. c", "1. a\n2. ‸\n3. b\n4. c", EnterKey());
        Add(cases, "enter ordered keeps paren separator", "1) a‸", "1) a\n2) ‸", EnterKey());
        Add(cases, "enter ordered after larger number", "3. a‸\n4. b", "3. a\n4. ‸\n5. b", EnterKey());
        Add(cases, "enter ordered renumber skips bullets", "1. a‸\n- b", "1. a\n2. ‸\n- b", EnterKey());
        Add(cases, "enter ordered renumber continues past bullet", "1. a‸\n1. b\n- c\n1. d", "1. a\n2. ‸\n3. b\n- c\n4. d", EnterKey());
        // A nested item between the new item and the later siblings does not end the run: the
        // siblings after it are renumbered too, so the level never shows the same number twice
        // (bughunt2 insert 4).
        Add(cases, "enter ordered renumber past a nested item", "1. a‸\n   - child\n2. b",
            "1. a\n2. ‸\n   - child\n3. b", EnterKey());
        Add(cases, "enter ordered renumber past two nested items", "1. a‸\n   - one\n      - two\n2. b",
            "1. a\n2. ‸\n   - one\n      - two\n3. b", EnterKey());
        Add(cases, "enter ordered CRLF", "1. a‸\r\n1. b", "1. a\r\n2. ‸\r\n3. b", EnterKey());
        Add(cases, "enter empty ordered item", "1. ‸\n1. b", "‸\n1. b", EnterKey());

        // Enter: quote lines repeat the quote prefix, then the list rules.
        Add(cases, "enter quote end", "> a‸", "> a\n> ‸", EnterKey());
        Add(cases, "enter quote middle", "> a‸b", "> a\n> ‸b", EnterKey());
        Add(cases, "enter nested quote", "> > a‸", "> > a\n> > ‸", EnterKey());
        Add(cases, "enter quote without space", ">a‸", ">a\n>‸", EnterKey());
        Add(cases, "enter quote with heading", "> # t‸", "> # t\n> ‸", EnterKey());
        Add(cases, "enter caret inside quote prefix", "> ‸> a", null, EnterKey());
        Add(cases, "enter caret before quote space", ">‸ a", null, EnterKey());

        // Enter on an empty quote line leaves the quote: the line loses its prefix and nothing is
        // inserted, so the heading the user types next is outside the quote.
        Add(cases, "enter empty quote leaves the quote", "> ‸", "‸", EnterKey());
        Add(cases, "enter empty quote CRLF leaves the quote", "> ‸\r\nb", "‸\r\nb", EnterKey());
        Add(cases, "enter quote without space leaves the quote", ">‸", "‸", EnterKey());
        Add(cases, "enter nested empty quote leaves one level", "> > ‸", "> ‸", EnterKey());
        Add(cases, "enter indented empty quote keeps the line indent", "  > ‸", "  ‸", EnterKey());
        Add(cases, "enter empty quote at column zero still makes room", "‸> ", null, EnterKey());
        Add(cases, "enter empty quote inside a fence stays literal", "```\n> ‸\n```", "```\n> \n‸\n```", EnterKey());

        // Enter: quoted lists.
        Add(cases, "enter quoted bullet", "> - a‸", "> - a\n> - ‸", EnterKey());
        Add(cases, "enter quoted ordered renumbers", "> 1. a‸\n> 1. b", "> 1. a\n> 2. ‸\n> 3. b", EnterKey());
        Add(cases, "enter quoted ordered renumbers its own level only", "> 1. a‸\n>   1. b", "> 1. a\n> 2. ‸\n>   1. b", EnterKey());
        Add(cases, "enter quoted task", "> - [ ] a‸", "> - [ ] a\n> - [ ] ‸", EnterKey());
        // An empty quoted list item drops its marker first; the next Enter takes the quote.
        Add(cases, "enter quoted empty bullet removes the list marker", "> - ‸", "> ‸", EnterKey());
        Add(cases, "enter quoted empty ordered removes the list marker", "> 5. ‸", "> ‸", EnterKey());
        Add(cases, "enter quoted empty task removes the list marker", "> - [ ] ‸", "> ‸", EnterKey());
        Add(cases, "enter nested quoted empty bullet keeps the outer quote", "> > - ‸", "> > ‸", EnterKey());

        // Enter: plain lines and the column zero escape.
        Add(cases, "enter plain line", "abc‸", null, EnterKey());
        Add(cases, "enter empty document", "‸", null, EnterKey());
        Add(cases, "enter indented plain line", "  abc‸", null, EnterKey());
        Add(cases, "enter heading line", "# t‸", null, EnterKey());
        Add(cases, "enter list at column zero", "‸- a", null, EnterKey());
        Add(cases, "enter quote at column zero", "‸> a", null, EnterKey());
        Add(cases, "enter caret inside bullet marker", "-‸ a", null, EnterKey());
        Add(cases, "enter caret inside task box", "- [‸ ] a", null, EnterKey());

        // Enter inside a marker run splits at the run's outer edge, so "**굵게**" stays one
        // pair on one line (H9). A heading's own prefix is not a marker run.
        Add(cases, "enter inside opening bold run", "# *‸*굵게** *기울임*", "# \n‸**굵게** *기울임*", EnterKey());
        Add(cases, "enter inside closing bold run", "# **굵게*‸* *기울임*", "# **굵게**\n‸ *기울임*", EnterKey());
        Add(cases, "enter inside closing bold run CRLF", "# **굵게*‸* *기울임*\r\nx", "# **굵게**\r\n‸ *기울임*\r\nx", EnterKey());
        Add(cases, "enter inside closing strike run", "~~b~‸~", "~~b~~\n‸", EnterKey());
        Add(cases, "enter inside literal marker run takes the nearer edge", "a*‸*b", "a**\n‸b", EnterKey());
        Add(cases, "enter inside marker run in a fence stays literal", "```\n*‸*a**\n```", "```\n*\n‸*a**\n```", EnterKey());
        // A lone literal marker followed by whitespace stays where the caret is: plain Enter
        // splits like the editor's own key (the bullet marker case "-‸ a" above is the same
        // shape), and only the cell's "<br>" Enter asks for the single-marker edge
        // (MarkdownTableCommands.EnterInCell passes singleMarkerEdge: true). A marker that can
        // close (content before it) and a multi-character run keep the split at the run's edge.
        Add(cases, "enter after lone star before a space keeps the split", "x *‸ y", null, EnterKey());
        Add(cases, "enter after a closer star keeps the split", "x*‸ y", null, EnterKey());
        Add(cases, "enter after a bold run keeps the split", "x**‸ y", null, EnterKey());

        // Enter: fenced code keeps only the leading whitespace.
        Add(cases, "enter in fence repeats indent", "```\n  abc‸\n```", "```\n  abc\n  ‸\n```", EnterKey());
        Add(cases, "enter in fence at line start", "```\n‸  abc\n```", null, EnterKey());
        Add(cases, "enter in fence at indent end", "```\n  ‸abc\n```", "```\n  \n  ‸abc\n```", EnterKey());
        Add(cases, "enter in tilde fence", "~~~\na‸\n~~~", "~~~\na\n‸\n~~~", EnterKey());
        Add(cases, "enter after closed fence uses list", "```\na\n```\n- b‸", "```\na\n```\n- b\n- ‸", EnterKey());
        Add(cases, "enter in fence does not continue list", "```\n- a‸\n```", "```\n- a\n‸\n```", EnterKey());
        Add(cases, "enter in fence does not continue quote", "```\n> a‸\n```", "```\n> a\n‸\n```", EnterKey());
        Add(cases, "enter on fence line is plain", "‸```", "\n‸```", EnterKey());
        Add(cases, "enter in fence CRLF", "```\r\n  a‸\r\n```", "```\r\n  a\r\n  ‸\r\n```", EnterKey());

        // Enter: E0 replaces a selection, then continues the line.
        Add(cases, "enter selection plain line", "a«b»c", "a\n‸c", EnterKey());
        Add(cases, "enter selection in bullet", "- a«b»c", "- a\n- ‸c", EnterKey());
        Add(cases, "enter selection to bullet end", "- a«bc»", "- a\n- ‸", EnterKey());
        Add(cases, "enter selection in quote", "> a«b»c", "> a\n> ‸c", EnterKey());
        Add(cases, "enter selection in ordered item", "1. a«b»c\n1. d", "1. a\n2. ‸c\n3. d", EnterKey());
        Add(cases, "enter selection at list column zero", "«- a»", "\n‸", EnterKey());
        Add(cases, "enter selection in bullet CRLF", "- a«b»\r\nc", "- a\r\n- ‸\r\nc", EnterKey());
        Add(cases, "enter whole plain line selection", "«abc»", "\n‸", EnterKey());
        Add(cases, "enter multi line selection", "- a«b\nc»d", "- a\n- ‸d", EnterKey());
        Add(cases, "enter selection in task", "- [ ] a«b»c", "- [ ] a\n- [ ] ‸c", EnterKey());
        Add(cases, "enter selection in quote CRLF", "> a«b»c\r\nz", "> a\r\n> ‸c\r\nz", EnterKey());
        Add(cases, "enter selection in fence", "```\n  a«b»c\n```", "```\n  a\n  ‸c\n```", EnterKey());

        // Tab: one indent unit before the marker of every touched list line.
        Add(cases, "tab bullet", "- a‸", "  - a‸", TabKey(false));
        Add(cases, "tab bullet at line start", "‸- a", "  ‸- a", TabKey(false));
        Add(cases, "tab star bullet", "* a‸", "  * a‸", TabKey(false));
        Add(cases, "tab plus bullet", "+ a‸", "  + a‸", TabKey(false));
        Add(cases, "tab keeps two space unit", "  - a‸", "    - a‸", TabKey(false));
        Add(cases, "tab uses existing tab", "\t- a‸", "\t\t- a‸", TabKey(false));
        Add(cases, "tab ordered first in list becomes one", "5. a‸", "  1. a‸", TabKey(false));
        Add(cases, "tab ordered beside same indent sibling", "    - x\n  3. a‸", "    - x\n    3. a‸", TabKey(false));
        Add(cases, "tab ordered after shallower bullet becomes one", "- x\n5. a‸", "- x\n  1. a‸", TabKey(false));
        Add(cases, "tab task", "  - [ ] a‸", "    - [ ] a‸", TabKey(false));
        Add(cases, "tab empty task", "  - ‸", "    - ‸", TabKey(false));
        Add(cases, "tab multi line selection", "«- a\n- b»", "  «- a\n  - b»", TabKey(false));
        Add(cases, "tab selection ending at line start excludes next", "«- a\n»- b", "  «- a\n»- b", TabKey(false));
        Add(cases, "tab ordered multi line renumbers in order", "«1. a\n1. b»", "  «1. a\n  2. b»", TabKey(false));
        // The moved lines of one level number as one block, in document order, and the block
        // restarts at 1 at its new level (bughunt2 insert 3), like Word.
        Add(cases, "tab ordered multi line numbers as one block", "«3. a\n4. b»", "  «1. a\n  2. b»", TabKey(false));
        Add(cases, "tab ordered multi line numbers three as one block", "«7. a\n8. b\n9. c»",
            "  «1. a\n  2. b\n  3. c»", TabKey(false));
        Add(cases, "tab ordered multi line joins an existing run", "  1. x\n«1. a\n1. b»",
            "  1. x\n  «2. a\n  3. b»", TabKey(false));
        Add(cases, "tab ordered multi line skips a nested item", "«1. a\n   - child\n2. b»",
            "  «1. a\n     - child\n  2. b»", TabKey(false));
        Add(cases, "tab keeps selection inside line", "- «a»", "  - «a»", TabKey(false));
        Add(cases, "tab quoted bullet", "> - a‸", ">   - a‸", TabKey(false));
        // A quoted ordered item restarts at 1 at its new level, exactly like an unquoted one
        // (the reported "> 1. a" / "> 2. b" Enter then Tab showed "3.").
        Add(cases, "tab quoted ordered first in list becomes one", "> 5. a‸", ">   1. a‸", TabKey(false));
        Add(cases, "tab quoted ordered empty item becomes one", "> 1. a\n> 2. ‸", "> 1. a\n>   1. ‸", TabKey(false));
        Add(cases, "tab quoted ordered under a shallower parent becomes one", "> 1. a\n> 3. b‸", "> 1. a\n>   1. b‸", TabKey(false));
        // J5: a moved ordered item's runs stay consecutive; a run led by a bullet keeps the
        // moved item's number, and a new level's first item restarts at 1.
        Add(cases, "tab quoted ordered beside same level sibling continues the run", ">   7. x\n> 3. a‸", ">   7. x\n>   8. a‸", TabKey(false));
        // An item moved into a deeper level of the quote is the first there.
        Add(cases, "tab quoted nested ordered restarts at one", ">   3. a‸", ">     1. a‸", TabKey(false));
        Add(cases, "tab bullet CRLF", "- a‸\r\n- b", "  - a‸\r\n- b", TabKey(false));
        Add(cases, "tab bullet with tab separator", "-\titem‸", "  -\titem‸", TabKey(false));
        Add(cases, "tab multi line CRLF", "«- a\r\n- b»", "  «- a\r\n  - b»", TabKey(false));
        Add(cases, "tab plain line not handled", "plain‸", null, TabKey(false));
        Add(cases, "tab mixed selection not handled", "«- a\nplain»", null, TabKey(false));
        Add(cases, "tab quote line not handled", "> a‸", null, TabKey(false));
        Add(cases, "tab in fence not handled", "```\n- a‸\n```", null, TabKey(false));

        // Shift+Tab: one indent unit off every touched line; no indent means no edit.
        Add(cases, "shift tab removes two spaces", "  - a‸", "- a‸", TabKey(true));
        Add(cases, "shift tab removes two of four", "    - a‸", "  - a‸", TabKey(true));
        Add(cases, "shift tab removes one space", " - a‸", "- a‸", TabKey(true));
        Add(cases, "shift tab removes tab", "\t- a‸", "- a‸", TabKey(true));
        Add(cases, "shift tab removes tab only", "\t  - a‸", "  - a‸", TabKey(true));
        AddNoEdits(cases, "shift tab without indent swallows", "- a‸", "- a‸", TabKey(true));
        Add(cases, "shift tab at indent end", "  ‸- a", "‸- a", TabKey(true));
        Add(cases, "shift tab multi line", "«  - a\n- b»", "«- a\n- b»", TabKey(true));
        Add(cases, "shift tab mixed tab and spaces", "«\t- a\n  - b»", "«- a\n- b»", TabKey(true));
        AddNoEdits(cases, "shift tab multi line without indent", "«- a\n- b»", "«- a\n- b»", TabKey(true));
        AddNoEdits(cases, "shift tab task without indent", "- [ ] ‸", "- [ ] ‸", TabKey(true));
        AddNoEdits(cases, "shift tab quoted bullet without indent", "> - a‸", "> - a‸", TabKey(true));
        // Item 9: the quoted item's own indentation sits after the quote prefix, so Shift+Tab
        // removes the unit there instead of doing nothing at column 0 and swallowing the key.
        Add(cases, "shift tab quoted bullet removes the unit after the quote",
            ">   - a‸", "> - a‸", TabKey(true));
        Add(cases, "shift tab quoted ordered removes the unit after the quote",
            ">   1. a‸", "> 1. a‸", TabKey(true));
        Add(cases, "shift tab quoted one space removes it", ">  - a‸", "> - a‸", TabKey(true));
        // The moved item changes level, so the numbered runs it leaves and joins stay consecutive
        // (the same J5 rule the unquoted levels use).
        Add(cases, "shift tab quoted list renumbers the joined level",
            "> 1. a\n>   3. b‸\n>   4. c", "> 1. a\n> 2. b‸\n>   4. c", TabKey(true));
        // J5: moving an ordered item changes levels, so the runs it leaves and joins stay
        // consecutive; the first number of each run is kept.
        Add(cases, "shift tab outdent renumbers the joined level", "3. 번호 둘\n  1. 하‸위", "3. 번호 둘\n4. 하‸위", TabKey(true));
        // A moved block numbers as one block at the level it joins, in document order
        // (bughunt2 insert 3), so Shift+Tab on several lines never leaves the same number twice.
        Add(cases, "shift tab ordered multi line numbers as one block", "«  3. a\n  4. b»",
            "«1. a\n2. b»", TabKey(true));
        Add(cases, "shift tab ordered multi line joins an existing run", "1. x\n«  5. a\n  9. b»",
            "1. x\n«2. a\n3. b»", TabKey(true));
        Add(cases, "shift tab outdent into a bullet run keeps its number", "- a\n  1. 하‸위", "- a\n1. 하‸위", TabKey(true));
        Add(cases, "shift tab outdent joins the top level run", "1. a\n  1. b\n  2. c‸", "1. a\n  1. b\n2. c‸", TabKey(true));
        Add(cases, "tab indent continues the new level run", "1. a\n  1. x\n2. b‸", "1. a\n  1. x\n  2. b‸", TabKey(false));
        Add(cases, "shift tab bullet CRLF", "  - a‸\r\nb", "- a‸\r\nb", TabKey(true));
        Add(cases, "shift tab removes indent on one line only", "«- a\n  - b»", "«- a\n- b»", TabKey(true));
        Add(cases, "shift tab in fence not handled", "```\n  - a‸\n```", null, TabKey(true));
        Add(cases, "shift tab plain line not handled", "  text‸", null, TabKey(true));

        // Backspace B1: caret at body start removes marker and task box.
        Add(cases, "backspace bullet marker", "- ‸item", "‸item", BackspaceKey());
        Add(cases, "backspace indented bullet", "  - ‸item", "  ‸item", BackspaceKey());
        Add(cases, "backspace one space indented bullet", " - ‸item", " ‸item", BackspaceKey());
        Add(cases, "backspace task box", "- [ ] ‸item", "‸item", BackspaceKey());
        Add(cases, "backspace checked task box", "- [x] ‸item", "‸item", BackspaceKey());
        Add(cases, "backspace ordered marker", "12. ‸item", "‸item", BackspaceKey());
        Add(cases, "backspace nine digit ordered marker", "123456789. ‸x", "‸x", BackspaceKey());
        Add(cases, "backspace empty bullet item", "- ‸", "‸", BackspaceKey());
        Add(cases, "backspace empty indented bullet item", "  - ‸", "  ‸", BackspaceKey());
        // Delete / Backspace that takes the line break after an ordered item: the items below keep counting (FormatAbuseProbe k).
        Add(cases, "delete joins the next ordered item and renumbers the rest", "1. a‸\n2. b\n3. c\n4. d", "1. a‸2. b\n2. c\n3. d", JoinOrderedKey(false));
        Add(cases, "backspace at an item start joins it and renumbers the rest", "1. a\n‸2. b\n3. c", "1. a‸2. b\n2. c", JoinOrderedKey(true));
        Add(cases, "delete joins a blank line and the run below keeps counting", "2. a\n3. b‸\n\n2. c", "2. a\n3. b‸\n4. c", JoinOrderedKey(false));
        Add(cases, "delete joins a plain line and the run below keeps counting", "1. a‸\n문단\n1. b", "1. a‸문단\n2. b", JoinOrderedKey(false));
        Add(cases, "delete renumbers the item's own level only", "1. a\n   1. x‸\n   2. y\n   3. z\n2. b", "1. a\n   1. x‸   2. y\n   2. z\n2. b", JoinOrderedKey(false));
        Add(cases, "delete renumbers quoted ordered items", "> 1. a‸\n> 2. b\n> 3. c", "> 1. a‸> 2. b\n> 2. c", JoinOrderedKey(false));
        Add(cases, "delete with nothing to renumber is left to the editor", "1. a‸\n2. b", null, JoinOrderedKey(false));
        Add(cases, "delete mid line is left to the editor", "1. ‸a\n2. b\n3. c", null, JoinOrderedKey(false));
        Add(cases, "delete after a bullet is left to the editor", "- a‸\n1. b\n2. c", null, JoinOrderedKey(false));
        Add(cases, "backspace at the first line is left to the editor", "‸1. a\n2. b", null, JoinOrderedKey(true));
        Add(cases, "delete inside a fence is left to the editor", "~~~\n1. a‸\n2. b\n3. c\n~~~", null, JoinOrderedKey(false));
        Add(cases, "backspace empty task item", "- [ ] ‸", "‸", BackspaceKey());
        Add(cases, "backspace quoted bullet marker", "> - ‸item", "> ‸item", BackspaceKey());
        Add(cases, "backspace bullet with tab separator", "-\t‸item", "‸item", BackspaceKey());
        Add(cases, "backspace quoted tab separator", ">\t‸item", "‸item", BackspaceKey());
        Add(cases, "backspace bullet marker CRLF", "- ‸a\r\nb", "‸a\r\nb", BackspaceKey());

        // Backspace B2: caret after indentation removes one indent unit.
        Add(cases, "backspace two space indent", "  ‸item", "‸item", BackspaceKey());
        Add(cases, "backspace one space indent", " ‸item", "‸item", BackspaceKey());
        Add(cases, "backspace two of four spaces", "    ‸item", "  ‸item", BackspaceKey());
        Add(cases, "backspace tab indent", "\t‸item", "‸item", BackspaceKey());
        Add(cases, "backspace tab indent before spaces", "\t  ‸item", "  ‸item", BackspaceKey());
        Add(cases, "backspace space then tab removes one space", " \t‸x", "\t‸x", BackspaceKey());
        Add(cases, "backspace empty indented line", "  ‸", "‸", BackspaceKey());
        Add(cases, "backspace indent on plain line", "  ‸# t", "‸# t", BackspaceKey());
        Add(cases, "backspace indent before quote", "  ‸> a", "‸> a", BackspaceKey());
        Add(cases, "backspace indent CRLF", "  ‸a\r\nb", "‸a\r\nb", BackspaceKey());

        // Backspace B3: caret right after one quote level removes it.
        Add(cases, "backspace quote space", "> ‸abc", "‸abc", BackspaceKey());
        Add(cases, "backspace quote without space", ">‸abc", "‸abc", BackspaceKey());
        Add(cases, "backspace outer quote level", "> ‸> abc", "‸> abc", BackspaceKey());
        Add(cases, "backspace inner quote level", "> > ‸abc", "> ‸abc", BackspaceKey());
        Add(cases, "backspace only quote marker", ">‸", "‸", BackspaceKey());
        Add(cases, "backspace quote CRLF", "> ‸a\r\nb", "‸a\r\nb", BackspaceKey());

        // Backspace: caret elsewhere is left to the editor.
        Add(cases, "backspace inside list body", "- i‸tem", null, BackspaceKey());
        Add(cases, "backspace before list marker", "‸- item", null, BackspaceKey());
        Add(cases, "backspace at list body end", "- a‸", null, BackspaceKey());
        Add(cases, "backspace inside quote body", "> a‸", null, BackspaceKey());
        Add(cases, "backspace after quote space", ">‸ abc", null, BackspaceKey());
        Add(cases, "backspace after indented list marker", "  ‸- item", null, BackspaceKey());
        Add(cases, "backspace inside plain line", "abc‸", null, BackspaceKey());
        Add(cases, "backspace at end of indented line", "  abc‸", null, BackspaceKey());
        Add(cases, "backspace in fence quote", "```\n> ‸a\n```", null, BackspaceKey());
        Add(cases, "backspace in fence indent", "```\n  ‸a\n```", null, BackspaceKey());
        Add(cases, "backspace in fence list", "```\n- ‸a\n```", null, BackspaceKey());
        // 11-06 user (forum report: after inserting a code block the closing ``` could not be
        // deleted). A key that would break a closed fence pair takes the block's two fence lines
        // and keeps every other line as plain text, the same change the toolbar code button makes.
        // A fence still being typed (an opening fence with no closer, drawn as source) keeps the
        // ordinary character delete, and a code line that merely starts with a marker run is text.
        Add(cases, "backspace at the first code line takes the fence pair", "```\n‸code\n```",
            "‸code", BackspaceKey());
        Add(cases, "backspace at a line start right after a closing fence takes the pair",
            "```\ncode\n```\n‸below", "code\n‸below", BackspaceKey());
        Add(cases, "backspace at the end of a closing fence takes the pair", "```\ncode\n```‸",
            "code‸", BackspaceKey());
        Add(cases, "backspace at the end of an opening fence takes the pair", "```‸\ncode\n```",
            "‸code", BackspaceKey());
        Add(cases, "backspace inside the closing fence run takes the pair", "```\ncode\n``‸`",
            "code‸", BackspaceKey());
        Add(cases, "backspace in an unclosed code block keeps the ordinary key", "```\n‸code", null, BackspaceKey());
        Add(cases, "backspace at a code line after a fence-like code line keeps the ordinary key",
            "```\n```inner\n‸code\n```", null, BackspaceKey());
        Add(cases, "delete at the end of a code line takes the fence pair", "```\ncode‸\n```",
            "code‸", FenceDeleteKey());
        Add(cases, "delete at the end of a closing fence takes the fence pair", "```\ncode\n```‸\nnext",
            "code\n‸next", FenceDeleteKey());
        Add(cases, "delete at the end of a line before an opening fence takes the pair", "text‸\n```\ncode\n```",
            "text‸\ncode", FenceDeleteKey());
        Add(cases, "delete inside a fence-like code line keeps the ordinary key", "```\n``‸`inner\ncode\n```", null, FenceDeleteKey());
        // More shapes of the same rule: the empty block, tilde and longer closing runs, CRLF, text
        // above the opener, the opener's marker run, and blocks far from the top of the document.
        Add(cases, "backspace at the end of an empty block's closing fence takes the block", "```\n```‸",
            "‸", BackspaceKey());
        Add(cases, "backspace at the end of a tilde closing fence takes the pair", "~~~\ncode\n~~~‸",
            "code‸", BackspaceKey());
        Add(cases, "backspace at the end of a longer closing fence takes the pair", "```\ncode\n````‸",
            "code‸", BackspaceKey());
        Add(cases, "backspace at the end of a closing fence takes the pair CRLF", "```\r\ncode\r\n```‸",
            "code‸", BackspaceKey());
        Add(cases, "backspace at an opening fence under text takes the pair", "text\n‸```\ncode\n```",
            "text\n‸code", BackspaceKey());
        Add(cases, "backspace at an opening fence under an empty line is left to the editor", "\n‸```\ncode\n```",
            null, BackspaceKey());
        Add(cases, "backspace at the end of a closing fence far down the document takes the pair",
            Rows(450) + "```\ncode\n```‸", Rows(450) + "code‸", BackspaceKey());
        Add(cases, "backspace at the end of a block longer than the old scan bound takes the pair",
            "```\n" + Rows(450) + "```‸", Rows(449) + "x‸", BackspaceKey());
        Add(cases, "delete inside the opening fence run takes the pair", "`‸``\ncode\n```",
            "‸code", FenceDeleteKey());
        Add(cases, "delete before the opening fence takes the pair", "‸```\ncode\n```",
            "‸code", FenceDeleteKey());
        Add(cases, "delete inside the closing fence run takes the pair", "```\ncode\n`‸``",
            "code‸", FenceDeleteKey());
        Add(cases, "delete at the end of an opening fence takes the pair", "```‸\ncode\n```",
            "‸code", FenceDeleteKey());
        Add(cases, "delete at the end of an empty block's opening fence takes the block", "```‸\n```",
            "‸", FenceDeleteKey());
        Add(cases, "delete at the end of a code line takes the fence pair CRLF", "```\r\ncode‸\r\n```",
            "code‸", FenceDeleteKey());
        Add(cases, "delete at the end of a closing fence above a blank line is left to the editor",
            "```\ncode\n```‸\n\nnext", null, FenceDeleteKey());
        Add(cases, "delete at the end of an unclosed opening fence is left to the editor", "```‸\ncode",
            null, FenceDeleteKey());
        // A selection that touches one fence: the selected text and both fence lines of every
        // touched block go, the block's other lines stay as plain text. A selection that holds
        // both fences of a block is ordinary text for the editor (null here), and a fence still
        // being typed (no closer) is ordinary text too.
        Add(cases, "selection over the closing fence takes the pair", "```\ncode\n«```»",
            "code‸", FenceSelectionKey());
        Add(cases, "selection over the opening fence takes the pair", "«```»\ncode\n```",
            "‸code", FenceSelectionKey());
        Add(cases, "selection from code into the closing fence takes the pair", "```\nco«de\n```»",
            "co‸", FenceSelectionKey());
        Add(cases, "selection from the opening fence into code takes the pair", "«```\nco»de\n```",
            "‸de", FenceSelectionKey());
        Add(cases, "selection over the closing fence takes the pair CRLF", "```\r\ncode\r\n«```»",
            "code‸", FenceSelectionKey());
        Add(cases, "selection from one block into the next takes both pairs", "```\na«b\n```\ntext\n```\nc»d\n```",
            "a‸d", FenceSelectionKey());
        Add(cases, "selection of a whole block is ordinary text", "a\n«```\ncode\n```»\nb",
            null, FenceSelectionKey());
        Add(cases, "selection of the whole document is ordinary text", "«```\ncode\n```»",
            null, FenceSelectionKey());
        Add(cases, "selection inside the code text touches no fence", "```\n«cod»e\n```",
            null, FenceSelectionKey());
        Add(cases, "selection that ends where the closing fence line starts touches no fence", "```\n«code\n»```",
            null, FenceSelectionKey());
        Add(cases, "selection over an unclosed opening fence is ordinary text", "«```»\ncode",
            null, FenceSelectionKey());
        // A character of the info string is ordinary text, and a blank line may be joined: the
        // fence line keeps its shape either way, so the editor's own key still runs there.
        Add(cases, "backspace inside a fence info string is left to the editor", "```csharp‸", null, BackspaceKey());
        Add(cases, "backspace on a blank line after a closing fence is left to the editor",
            "```\ncode\n```\n‸\n", null, BackspaceKey());
        Add(cases, "backspace task box without a space", "- [x]‸done", "‸done", BackspaceKey());

        // Table outdent: one indent unit off every line of the table on the caret's line. The
        // list ends there, so the table (and the blank line after it) becomes top level.
        Add(cases, "table outdent two spaces", "- a\n  ‸| x | y |\n  | --- | --- |\n  | 1 | 2 |\n",
            "- a\n‸| x | y |\n| --- | --- |\n| 1 | 2 |\n", OutdentTableKey());
        Add(cases, "table outdent four spaces", "- a\n    ‸| x |\n    | --- |\n",
            "- a\n  ‸| x |\n  | --- |\n", OutdentTableKey());
        Add(cases, "table outdent one tab", "- a\n\t‸| x |\n\t| --- |\n",
            "- a\n‸| x |\n| --- |\n", OutdentTableKey());
        Add(cases, "table outdent keeps caret inside the table", "- a\n  ‸| x | y |\n  | --- | --- |\n",
            "- a\n‸| x | y |\n| --- | --- |\n", OutdentTableKey());
        Add(cases, "table outdent from a body row", "- a\n  | x |\n  | --- |\n  | ‸1 |\n",
            "- a\n| x |\n| --- |\n| ‸1 |\n", OutdentTableKey());
        Add(cases, "table outdent CRLF", "- a\r\n  ‸| x |\r\n  | --- |\r\n",
            "- a\r\n‸| x |\r\n| --- |\r\n", OutdentTableKey());
        Add(cases, "table outdent takes the item's blank line with it", "- a\n  ‸| x |\n  | --- |\n  \n",
            "- a\n‸| x |\n| --- |\n\n", OutdentTableKey());
        Add(cases, "table outdent leaves a following block alone", "- a\n  ‸| x |\n  | --- |\n  \n  next\n",
            "- a\n‸| x |\n| --- |\n\n  next\n", OutdentTableKey());
        Add(cases, "table outdent at top level is not handled", "| x |\n| --- |\n| ‸1 |\n",
            null, OutdentTableKey());
        Add(cases, "table outdent of a plain pipe line is not handled", "- a\n  ‸| not a table |\n",
            null, OutdentTableKey());
        Add(cases, "table outdent outside a table is not handled", "- a\n  ‸text\n  | x |\n  | --- |\n",
            null, OutdentTableKey());
        Add(cases, "table outdent in fence is not handled", "```\n  ‸| x |\n  | --- |\n```\n",
            null, OutdentTableKey());

        // Table indent: one unit before every line so the table joins the item above it. The
        // blank line directly under the table joins too, so indent undoes outdent exactly.
        Add(cases, "table indent under a list item", "- a\n‸| x | y |\n| --- | --- |\n",
            "- a\n  ‸| x | y |\n  | --- | --- |\n", IndentTableKey());
        Add(cases, "table indent from a body row", "- a\n| x |\n| --- |\n| ‸1 |\n",
            "- a\n  | x |\n  | --- |\n  | ‸1 |\n", IndentTableKey());
        Add(cases, "table indent adds a second level", "- a\n  | x |\n  | --- |\n- b\n‸| y |\n| --- |\n",
            "- a\n  | x |\n  | --- |\n- b\n  ‸| y |\n  | --- |\n", IndentTableKey());
        // The unit follows the table line's own indentation, so an unindented table always
        // takes the two-space unit even when the item above is written with tabs.
        Add(cases, "table indent uses the table's own unit", "- a\n\t| x |\n\t| --- |\n- b\n‸| y |\n| --- |\n",
            "- a\n\t| x |\n\t| --- |\n- b\n  ‸| y |\n  | --- |\n", IndentTableKey());
        Add(cases, "table indent CRLF", "- a\r\n‸| x |\r\n| --- |\r\n",
            "- a\r\n  ‸| x |\r\n  | --- |\r\n", IndentTableKey());
        Add(cases, "table indent leaves a blank line with its own indent", "- a\n‸| x |\n| --- |\n  \nnext\n",
            "- a\n  ‸| x |\n  | --- |\n  \nnext\n", IndentTableKey());
        // A line with nothing on it stays empty: indenting it would leave two trailing spaces.
        Add(cases, "table indent leaves a truly empty line empty", "- a\n‸| x |\n| --- |\n\nnext\n",
            "- a\n  ‸| x |\n  | --- |\n\nnext\n", IndentTableKey());
        // The outdent and the indent are exact inverses of each other.
        Add(cases, "table indent inverts the outdent", "- a\n‸| x |\n| --- |\n  \nnext\n",
            "- a\n  ‸| x |\n  | --- |\n  \nnext\n", IndentTableKey());
        Add(cases, "table indent without a list above is not handled", "text\n‸| x |\n| --- |\n",
            null, IndentTableKey());
        // A table already at the item's content indent has no deeper list level: a second unit
        // would turn it into an indented code block, so the command declines (no-op).
        Add(cases, "table indent inside the item is a no-op", "- a\n  ‸| x |\n  | --- |\n",
            null, IndentTableKey());
        Add(cases, "table indent twice is a no-op", "- a\n    ‸| x |\n    | --- |\n",
            null, IndentTableKey());
        Add(cases, "table indent under a nested item is a no-op", "- a\n  - b\n    ‸| x |\n    | --- |\n",
            null, IndentTableKey());
        // The ready check uses the same header + delimiter rule as MarkdownTableCommands and the
        // renderer (one cell each here), so the short one-dash delimiter cells a table command
        // accepts also identify a table here. A delimiter row with more cells than its header is
        // not a table for the renderer, and neither parser takes it for one.
        Add(cases, "table indent of a one-dash delimiter table", "- a\n‸| x |\n|-|\n",
            "- a\n  ‸| x |\n  |-|\n", IndentTableKey());
        Add(cases, "table outdent of a colon delimiter table", "- a\n  ‸| x |\n  |:-:|\n",
            "- a\n‸| x |\n|:-:|\n", OutdentTableKey());
        Add(cases, "table indent of a single colon table", "- a\n‸| x |\n|:-|\n",
            "- a\n  ‸| x |\n  |:-|\n", IndentTableKey());
        Add(cases, "table indent above a blank line still joins the list", "- a\n\n‸| x |\n| --- |\n",
            "- a\n\n  ‸| x |\n  | --- |\n", IndentTableKey());
        Add(cases, "table indent skips blank lines to the list", "- a\n\n\n‸| x |\n| --- |\n",
            "- a\n\n\n  ‸| x |\n  | --- |\n", IndentTableKey());
        Add(cases, "table indent outside a table is not handled", "- a\n‸text\n| x |\n| --- |\n",
            null, IndentTableKey());
        Add(cases, "table indent of a plain pipe line is not handled", "- a\n‸| not a table |\n",
            null, IndentTableKey());
        Add(cases, "table indent in fence is not handled", "```\n- a\n‸| x |\n| --- |\n```\n",
            null, IndentTableKey());

        // Tab/Shift+Tab inside a table are cell navigation: the next/previous cell of the row (or
        // the first/last cell of the neighbour row) is selected, and the table never moves. These
        // navigation cases compare the selection only (the table-end Tab below is the one edit).
        AddNoEdits(cases, "tab in a table selects the next cell", "- a\n  | x‸ | y |\n  | --- | --- |\n",
            "- a\n  | x | «y» |\n  | --- | --- |\n", TabKey(false));
        AddNoEdits(cases, "tab at the line indentation enters the first cell", "- a\n‸  | x | y |\n  | --- | --- |\n",
            "- a\n  | «x» | y |\n  | --- | --- |\n", TabKey(false));
        AddNoEdits(cases, "shift tab in a table selects the previous cell", "- a\n  | x | ‸y |\n  | --- | --- |\n",
            "- a\n  | «x» | y |\n  | --- | --- |\n", TabKey(true));
        AddNoEdits(cases, "tab in a plain table cell is cell navigation", "| x‸ | y |\n| --- | --- |\n",
            "| x | «y» |\n| --- | --- |\n", TabKey(false));
        // Tab in the last cell of the table's last row appends one empty row and puts the caret in
        // its first cell (Word/Typora), with or without the row's trailing pipe; the row keeps the
        // table's own cell padding and the outer pipes the add-row command writes. A selection in
        // that cell is collapsed and the row added, so no selected text goes, and one result is one
        // undo entry (checked by the sequence below). Shift+Tab and Tab in any earlier row keep
        // their old cell move.
        Add(cases, "tab in the last cell of the last row appends a row",
            "| h | i |\n| --- | --- |\n| c | ‸d |",
            "| h | i |\n| --- | --- |\n| c | d |\n| ‸ |  |", TabKey(false));
        Add(cases, "tab at the table end without a trailing pipe appends a row",
            "| h | i |\n| --- | --- |\n| c | d‸",
            "| h | i |\n| --- | --- |\n| c | d\n| ‸ |  |", TabKey(false));
        Add(cases, "tab at the table end CRLF appends a row",
            "| a | b |\r\n|---|---|\r\n| c | d‸ |",
            "| a | b |\r\n|---|---|\r\n| c | d |\r\n| ‸ |  |", TabKey(false));
        // A table without the outer pipes keeps its own padding for the new row; the outer pipes
        // are the add-row command's own, because a padding-only row without them is not a row the
        // renderer keeps (and the caret could not type into it).
        Add(cases, "tab at the end of a table without outer pipes appends a row",
            "Name | Age\n--- | ---\nKim | 30‸",
            "Name | Age\n--- | ---\nKim | 30\n| ‸| |", TabKey(false));
        AddNoEdits(cases, "tab in the last cell of a middle row goes to the next row",
            "| a | b |\n|---|---|\n| c | ‸d |\n| e | f |",
            "| a | b |\n|---|---|\n| c | d |\n| «e» | f |", TabKey(false));
        AddNoEdits(cases, "shift tab in the last cell moves to the previous cell",
            "| h | i |\n| --- | --- |\n| c | ‸d |",
            "| h | i |\n| --- | --- |\n| «c» | d |", TabKey(true));
        // A selection in the last cell is collapsed and the row added: the selected text stays.
        Add(cases, "tab selection in the last cell appends a row",
            "| h | i |\n| --- | --- |\n| c | «d» |",
            "| h | i |\n| --- | --- |\n| c | d |\n| ‸ |  |", TabKey(false));
        Add(cases, "tab selection in the last cell without a trailing pipe appends a row",
            "| h | i |\n| --- | --- |\n| c | «d»",
            "| h | i |\n| --- | --- |\n| c | d\n| ‸ |  |", TabKey(false));
        // A selection inside a table is navigation too, whatever it covers. Tab must never reach
        // the editor's TabForward, which replaces the selected text (and the "|" in it) with one
        // tab and ends the table. Tab takes the cell after the selection end and Shift+Tab the
        // cell before the selection start; no case edits the document.
        AddNoEdits(cases, "tab multi cell selection moves after its end",
            "| «a | b» | c |\n|---|---|---|\n| 1 | 2 | 3 |",
            "| a | b | «c» |\n|---|---|---|\n| 1 | 2 | 3 |", TabKey(false));
        AddNoEdits(cases, "shift tab multi cell selection moves before its start",
            "| a | «b | c» |\n|---|---|---|\n| 1 | 2 | 3 |",
            "| «a» | b | c |\n|---|---|---|\n| 1 | 2 | 3 |", TabKey(true));
        AddNoEdits(cases, "tab partial cell selection moves to the next cell",
            "| a | b«c» | z |\n|---|---|---|\n| 1 | 2 | 3 |",
            "| a | bc | «z» |\n|---|---|---|\n| 1 | 2 | 3 |", TabKey(false));
        AddNoEdits(cases, "tab multi row selection moves to the next row",
            "| a | b |\n|---|---|\n| «1 | 2» |\n| 3 | 4 |",
            "| a | b |\n|---|---|\n| 1 | 2 |\n| «3» | 4 |", TabKey(false));
        AddNoEdits(cases, "tab selection covering a table writes nothing",
            "«head\n| a | b |\n| --- | --- |\n| 1 | 2 |\ntail»",
            "head\n| a | b |\n| --- | --- |\n| 1 | 2 |\ntail‸", TabKey(false));
        // A selection that starts outside the table and ends in its last cell never appends a
        // row: the append belongs to the caret or to a selection inside that cell, so this key
        // writes nothing and only collapses onto its own anchor (the selection end for Tab).
        AddNoEdits(cases, "tab selection from a paragraph into the last cell does not append",
            "«hello\n| a | b |\n| --- | --- |\n| c | d» |",
            "hello\n| a | b |\n| --- | --- |\n| c | d‸ |", TabKey(false));
        // A cell's own pipe is text: an escaped "\|" and a pipe inside a `code` span are not
        // separators for the renderer or MarkdownTableCommands, so Tab lands on the next real
        // cell instead of on the rest of the same cell's text.
        AddNoEdits(cases, "tab in a table skips an escaped pipe",
            "| a‸ \\| b | c |\n| --- | --- |\n| 1 | 2 |",
            "| a \\| b | «c» |\n| --- | --- |\n| 1 | 2 |", TabKey(false));
        AddNoEdits(cases, "tab in a table skips a pipe inside a code span",
            "| `x‸|y` | z |\n| --- | --- |\n| 1 | 2 |",
            "| `x|y` | «z» |\n| --- | --- |\n| 1 | 2 |", TabKey(false));
        // The delimiter row is structure, not a row: no cell move ever targets it, and a row's
        // trailing pipe leaves no phantom cell past its last real one. Shift+Tab from a body
        // row's first cell therefore reaches the last real header cell.
        AddNoEdits(cases, "shift tab from a body row skips the delimiter row",
            "a | b |\n--- | --- |\n1‸ | 2 |",
            "a | «b» |\n--- | --- |\n1 | 2 |", TabKey(true));
        AddNoEdits(cases, "shift tab from a body row skips the delimiter row CRLF",
            "a | b |\r\n--- | --- |\r\n1‸ | 2 |",
            "a | «b» |\r\n--- | --- |\r\n1 | 2 |", TabKey(true));
        AddNoEdits(cases, "shift tab from a body row skips the delimiter row with outer pipes",
            "| a | b |\n|---|---|\n| 1‸ | 2 |",
            "| a | «b» |\n|---|---|\n| 1 | 2 |", TabKey(true));
        AddNoEdits(cases, "tab from the header row steps over the delimiter row",
            "| a | b‸ |\n|---|---|\n| 1 | 2 |",
            "| a | b |\n|---|---|\n| «1» | 2 |", TabKey(false));
        // Header-only table: there is no body row below the delimiter yet, so Tab in the header's
        // last cell adds the first body row; a selection there is collapsed and the row added.
        Add(cases, "tab in a header-only table adds the first body row",
            "| a | b‸ |\n|---|---|",
            "| a | b |\n|---|---|\n| ‸ |  |", TabKey(false));
        Add(cases, "tab in a header-only table without outer pipes adds the first body row",
            "Name | Age‸\n--- | ---",
            "Name | Age\n--- | ---\n| ‸| |", TabKey(false));
        Add(cases, "tab selection in a header-only table adds the first body row",
            "| a | «b» |\n|---|---|",
            "| a | b |\n|---|---|\n| ‸ |  |", TabKey(false));
        // The delimiter row is structure, so Tab on it never appends: the raw view keeps the key
        // where it is (the live view steps to the row on the other side, SourceTab).
        AddNoEdits(cases, "tab in the last cell of a header-only delimiter row stays",
            "| a | b |\n|---|---‸|",
            "| a | b |\n|---|---‸|", TabKey(false));
        // A selection that cannot move rests on the key's own anchor: the selection start for
        // Shift+Tab (the selection end stays the Tab anchor above).
        AddNoEdits(cases, "shift tab selection at the table start collapses to its start",
            "| «a» | b |\n|---|---|",
            "| ‸a | b |\n|---|---|", TabKey(true));
        // Item 6: rows without the outer pipes are the same rows the renderer draws (GFM). The
        // command/structural parsers used to need a leading pipe, so these keys were dead here.
        AddNoEdits(cases, "tab in a table without outer pipes selects the next cell",
            "Name‸ | Age\n--- | ---\nKim | 30",
            "Name | «Age»\n--- | ---\nKim | 30", TabKey(false));
        AddNoEdits(cases, "shift tab in a table without outer pipes selects the previous cell",
            "Name | ‸Age\n--- | ---\nKim | 30",
            "«Name» | Age\n--- | ---\nKim | 30", TabKey(true));
        AddNoEdits(cases, "tab finds a table below a pipe paragraph",
            "see a | b\n| h | i |\n| --- | --- |\n| x‸ | y |",
            "see a | b\n| h | i |\n| --- | --- |\n| x | «y» |", TabKey(false));
        Add(cases, "table indent without outer pipes",
            "- a\n‸Name | Age\n--- | ---\nKim | 30\n",
            "- a\n  ‸Name | Age\n  --- | ---\n  Kim | 30\n", IndentTableKey());
        Add(cases, "table outdent without outer pipes",
            "- a\n  ‸Name | Age\n  --- | ---\n  Kim | 30\n",
            "- a\n‸Name | Age\n--- | ---\nKim | 30\n", OutdentTableKey());
        Add(cases, "table indent of a pipe paragraph over a rule is not handled",
            "- a\n‸a | b\n---\n", null, IndentTableKey());
        // A list item outside any table keeps the indent rule.
        Add(cases, "tab list item outside a table still indents", "- a‸\n", "  - a‸\n", TabKey(false));

        // Autoformat: the space that commits "-" / "*" / "+" / "N." rewrites the marker to the
        // form the writer emits (every bullet "- ", an ordered item "<value>. "). The space itself
        // is part of the same result, so these expect the space in the text and the caret after it.
        Add(cases, "autoformat star becomes canonical bullet", "*‸", "- ‸", AutoFormatKey());
        Add(cases, "autoformat plus becomes canonical bullet", "+‸", "- ‸", AutoFormatKey());
        Add(cases, "autoformat leading zero number", "007.‸", "7. ‸", AutoFormatKey());
        Add(cases, "autoformat two digit with a leading zero", "012.‸", "12. ‸", AutoFormatKey());
        Add(cases, "autoformat star CRLF document", "*‸\r\nnext", "- ‸\r\nnext", AutoFormatKey());
        // A real table cell never converts: the marker must be the whole text after the indent, and
        // in a cell a pipe always sits in front of it.
        Add(cases, "autoformat declines in a table cell", "‸| * |\n|---|\n", null, AutoFormatKey());
        Add(cases, "autoformat declines in a fenced block", "```\n*‸\n```", null, AutoFormatKey());
        // Already canonical (or not a list) text: the editor inserts the space itself.
        Add(cases, "autoformat leaves a canonical bullet alone", "-‸", null, AutoFormatKey());
        Add(cases, "autoformat leaves a canonical number alone", "1.‸", null, AutoFormatKey());
        Add(cases, "autoformat declines zero", "0.‸", null, AutoFormatKey());
        Add(cases, "autoformat declines ten digits", "1234567890.‸", null, AutoFormatKey());
        Add(cases, "autoformat declines a word before the marker", "a*‸", null, AutoFormatKey());
        Add(cases, "autoformat declines a bare number", "12‸", null, AutoFormatKey());
        Add(cases, "autoformat declines a lone digit", "1‸", null, AutoFormatKey());
        Add(cases, "autoformat declines with no marker", "word‸", null, AutoFormatKey());
        // An indented marker declines, like the basic view (the marker must be the whole text
        // before the caret, indentation included).
        Add(cases, "autoformat declines an indented marker", "  *‸", null, AutoFormatKey());

        return cases;
    }
}
public partial class MainWindow {
    internal static int VerifyStructuralInput(string outDir) { return MarkdownStructuralInputProbe.Run(outDir, delegate(Action<string,bool,string> check) { LiveAutoFormatProbe.Run(outDir, check); LiveTableSpaceProbe.Run(outDir, check); }); }
    // Live view: the same automatic list conversion on the source surface, end to end.
    // TextEntering is driven through PerformTextInput (the path a real keystroke takes), so the
    // cases cover the wiring in LiveMarkdownCommands plus the app undo history: one Ctrl+Z has to
    // put the line back exactly as it was before the space.
    static class LiveAutoFormatProbe {
        static MainWindow window;

        internal static void Run(string outDir, Action<string,bool,string> check) {
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            bool old = ProbeDefaults.Live;
            ProbeDefaults.Live = true;
            try {
                window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 };
                window.Show(); window.Left = -20000;
                 window.SetMode(true); LongLineBench.Idle();
                if (!window.LivePreviewAttached || !window.LiveFormat) {
                    check("live autoformat needs the live surface", false, "attached=" + window.LivePreviewAttached + ", live=" + window.LiveFormat);
                    return;
                }
                Typed(check, "star bullet", "*", "a", "- a");
                Typed(check, "plus bullet", "+", "a", "- a");
                Typed(check, "leading zero ordered", "007.", "a", "7. a");
                Typed(check, "canonical bullet stays", "-", "a", "- a");
                Typed(check, "ordered stays", "1.", "a", "1. a");
                Literal(check, "quote marker is not a list", ">", "> b");
                Literal(check, "word before the marker", "a*", "a* b");
                Literal(check, "number without a dot", "1", "1 b");
                // A marker typed at the head of a table row is a cell edit, not a list.
                TableCell(check);
                Fenced(check);
                SourceToggle(check);
                Undo(check);
            } finally {
                if (window != null) { try { window.saved = window.text; window.Close(); } catch (Exception) { } window = null; }
                ProbeDefaults.Live = old;
            }
        }

        // The whole document typed with PerformTextInput, so every character goes through the same
        // TextEntering hook a keystroke does.
        static void Typed(Action<string,bool,string> check, string name, string marker, string rest, string expected) {
            Reset("");
            Type(marker); Type(" "); Type(rest);
            string saved = window.text;
            check("live autoformat " + name, saved == expected, "\"" + saved + "\"");
        }
        static void Literal(Action<string,bool,string> check, string name, string marker, string expected) {
            Reset("");
            Type(marker); Type(" b");
            string saved = window.text;
            check("live autoformat " + name + " stays literal", saved == expected, "\"" + saved + "\"");
        }
        static void TableCell(Action<string,bool,string> check) {
            // Inside a table cell the marker is never the whole text before the caret, so nothing
            // converts; the typed characters stay exactly as typed. H7 keeps the caret inside the
            // last row's last cell, so the marker lands in that cell (the row-end slot is not a
            // caret stop any more).
            Reset("| a |\n| --- |\n| b |");
            window.source.Select(window.source.Document.TextLength, 0);
            Type("*");
            Type(" ");
            string saved = window.text;
            check("live autoformat declines inside a table cell", saved == "| a |\n| --- |\n| b*  |", "\"" + saved + "\"");
        }
        static void Fenced(Action<string,bool,string> check) {
            Reset("```\n");
            Type("* "); Type("a");
            string saved = window.text;
            check("live autoformat inside a fenced block stays literal", saved == "```\n* a", "\"" + saved + "\"");
        }
        static void SourceToggle(Action<string,bool,string> check) {
            Reset("");
            window.loading = true; window.preview.IsChecked = true; window.loading = false;
            window.SwitchSurface(); LongLineBench.Idle();
            bool detached = !window.LivePreviewAttached;
            Type("* "); Type("a");
            string saved = window.text;
            window.loading = true; window.preview.IsChecked = false; window.loading = false;
            window.SwitchSurface(); LongLineBench.Idle();
            check("live autoformat is off with the source toggle", detached && saved == "* a", "detached=" + detached + ", \"" + saved + "\"");
        }
        static void Undo(Action<string,bool,string> check) {
            // A word before the marker is never converted.
            Reset("x");
            Type(" * ");
            bool literal = window.text == "x * ";
            // The autoformat itself is one undo entry: undoing straight after the space restores
            // the exact pre-space line, marker character and all.
            Reset("");
            Type("*");
            Type(" ");
            string converted = window.text;
            window.Undo(); LongLineBench.Idle();
            string restored = window.text;
            check("live autoformat undo restores the exact text",
                literal && converted == "- " && restored == "*",
                "literal=" + literal + ", converted=\"" + converted + "\", restored=\"" + restored + "\"");
        }

        static void Reset(string markdown) {
            window.SetText(markdown); LongLineBench.Idle();
            window.source.Select(window.source.Document.TextLength, 0);
            window.source.Focus(); LongLineBench.Idle();
        }
        static void Type(string value) {
            // One character per PerformTextInput, like the keyboard: the space must arrive as its
            // own TextEntering event, which is what the autoformat hooks.
            for (int i = 0; i < value.Length; i++) {
                window.source.TextArea.PerformTextInput(value.Substring(i, 1));
                LongLineBench.Idle();
            }
        }
    }

    // E1 (real keyboard/mouse test): a space typed inside a table cell must stay the word gap it
    // looks like. The cell's trailing spaces are its padding, so the character typed after the
    // space has to land after it; with the caret pulled back to the cell's end, "ab", Space, "cd"
    // reads back as "abcd". Every key goes through PerformTextInput (the TextEntering path a real
    // keystroke takes) in a real window with the product's Basic mode (markers hidden, every line
    // formatted), and the saved Markdown source is the authority.
    static class LiveTableSpaceProbe {
        static MainWindow window;

        internal static void Run(string outDir, Action<string,bool,string> check) {
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            bool oldLive = ProbeDefaults.Live, oldHide = hideMarkers;
            ProbeDefaults.Live = true; hideMarkers = true;
            try {
                window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 };
                window.Show(); window.Left = -20000;
                window.SetMode(true); LongLineBench.Idle();
                if (!window.LivePreviewAttached || !window.LiveFormat || window.livePreview == null || window.livePreview.Options == null || !window.livePreview.Options.HideMarkers) {
                    check("live table space needs the Basic live surface", false,
                        "attached=" + window.LivePreviewAttached + ", live=" + window.LiveFormat + ", hide=" +
                        (window.livePreview == null || window.livePreview.Options == null ? "-" : window.livePreview.Options.HideMarkers.ToString()));
                    return;
                }
                // The space itself is kept in the source (it is the cell's padding until a word
                // follows) and the next word must land after it without losing it.
                Typed(check, "space at the last cell's end", "| h |\n| --- |\n| ab |", 3, 0, new string[] { " " }, "| h |\n| --- |\n| ab  |");
                Typed(check, "word after a space in the last cell", "| h |\n| --- |\n| ab |", 3, 0, new string[] { " ", "c", "d" }, "| h |\n| --- |\n| ab cd |");
                Typed(check, "word after a space in a middle cell", "| h | i |\n| --- | --- |\n| ab | cd |", 3, 0, new string[] { " ", "x" }, "| h | i |\n| --- | --- |\n| ab x | cd |");
                Typed(check, "double space at the last cell's end", "| h |\n| --- |\n| ab |", 3, 0, new string[] { " ", " ", "x" }, "| h |\n| --- |\n| ab  x |");
                Typed(check, "Korean words keep the space between them", "| h |\n| --- |\n| 가 |", 3, 0, new string[] { " ", "나" }, "| h |\n| --- |\n| 가 나 |");
                Typed(check, "space then word in a one column header cell", "| ab |\n| --- |\n| x |", 1, 0, new string[] { " ", "c" }, "| ab c |\n| --- |\n| x |");
                // The caret the space leaves behind draws at the end of its own cell: a caret
                // stop that resolves to the neighbour's column would look like the text jumped
                // a column while the user is still in the middle of a word.
                CaretDrawsAtCellEnd(check);
            } finally {
                if (window != null) { try { window.saved = window.text; window.Close(); } catch (Exception) { } window = null; }
                ProbeDefaults.Live = oldLive; hideMarkers = oldHide;
            }
        }

        // Types the keys at the end of one cell's text and compares the saved Markdown.
        static void Typed(Action<string,bool,string> check, string name, string markdown, int lineNumber, int cell, string[] keys, string expected) {
            window.SetText(markdown); LongLineBench.Idle();
            ICSharpCode.AvalonEdit.Document.TextDocument document = window.source.Document;
            ICSharpCode.AvalonEdit.Document.DocumentLine line = document.GetLineByNumber(lineNumber);
            MarkdownLivePreview.LineInfo info = window.livePreview.Info(line);
            if (info == null || info.Cells == null || cell >= info.Cells.Count) {
                check("live table space " + name, false, "fixture has no cell " + cell + " on line " + lineNumber);
                return;
            }
            window.source.Select(line.Offset + info.Cells[cell].End, 0);
            window.source.Focus(); LongLineBench.Idle();
            for (int k = 0; k < keys.Length; k++) Type(keys[k]);
            string saved = window.text;
            string detail = "\"" + saved.Replace("\r", "\\r").Replace("\n", "\\n") + "\" expected \"" +
                expected.Replace("\r", "\\r").Replace("\n", "\\n") + "\", caret=" + window.source.CaretIndex;
            check("live table space " + name, saved == expected, detail);
        }
        static void Type(string value) {
            for (int i = 0; i < value.Length; i++) {
                window.source.TextArea.PerformTextInput(value.Substring(i, 1));
                LongLineBench.Idle();
            }
        }
        static void CaretDrawsAtCellEnd(Action<string,bool,string> check) {
            window.SetText("| h | i |\n| --- | --- |\n| ab | cd |"); LongLineBench.Idle();
            ICSharpCode.AvalonEdit.Document.TextDocument document = window.source.Document;
            ICSharpCode.AvalonEdit.Document.DocumentLine line = document.GetLineByNumber(3);
            MarkdownLivePreview.LineInfo info = window.livePreview.Info(line);
            if (info == null || info.Cells == null || info.Cells.Count < 2) {
                check("live table space caret draws at its cell end", false, "fixture has no two cells");
                return;
            }
            window.source.Select(line.Offset + info.Cells[0].End, 0);
            window.source.Focus(); LongLineBench.Idle();
            Type(" ");
            line = document.GetLineByNumber(3);
            info = window.livePreview.Info(line);
            ICSharpCode.AvalonEdit.Rendering.VisualLine visual = window.livePreview.View.GetOrConstructVisualLine(line);
            MarkdownLivePreview.TableRowElement row = visual == null ? null : window.livePreview.RowElement(visual);
            if (row == null) {
                check("live table space caret draws at its cell end", false, "row has no table element");
                return;
            }
            int caretColumn = row.GetVisualColumn(window.source.CaretIndex - line.Offset);
            int cellColumn = row.GetVisualColumn(info.Cells[0].End);
            check("live table space caret draws at its cell end", caretColumn == cellColumn,
                "caret column=" + caretColumn + ", cell end column=" + cellColumn + ", caret=" + window.source.CaretIndex +
                ", text=\"" + window.text.Replace("\n", "\\n") + "\"");
        }
    }
}
}
