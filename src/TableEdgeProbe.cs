using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
public partial class MainWindow {
    internal static int VerifyTableEdge(string outDir) {
        return TableEdgeProbeRunner.Run(outDir);
    }

// Nested so the probe can drive the window's own editor fields; the name shadows no WPF type.
internal static class TableEdgeProbeRunner {
    static readonly List<string> Lines = new List<string>();
    static int failures;

    internal static int Run(string outDir) {
        outDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(outDir);
        string pass = Path.Combine(outDir, "table-edge-probe-PASS.txt"), fail = Path.Combine(outDir, "table-edge-probe-FAIL.txt");
        if (File.Exists(pass)) File.Delete(pass);
        if (File.Exists(fail)) File.Delete(fail);
        partialPath = Path.Combine(outDir, "table-edge-probe-partial.txt");
        if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Lines.Clear();
        failures = 0;
        bool oldHide = MainWindow.hideMarkers;
        bool oldLive = ProbeDefaults.Live;
        try {
            foreach (bool hidden in new bool[] { true, false }) {
                RunViewSuite(hidden);
            }
            RunLiveUnpaddedWordSuite();
            RunStage3Task5TableImeDrawingSuite();
            RunStage3Task6aLiveCellDeleteSuite();
            RunInMemoryPasteAndEnterChecks();
        } catch (Exception ex) {
            Check("table-edge-probe unhandled exception", false, ex.ToString());
        } finally {
            MainWindow.hideMarkers = oldHide;
            ProbeDefaults.Live = oldLive;
        }
        Lines.Add("Summary: " + (failures == 0 ? "PASS" : "FAIL") + " (failures=" + failures + ")");
        partialPath = null;
        string report = string.Join("\n", Lines.ToArray()) + "\n";
        File.WriteAllText(Path.Combine(outDir, "table-edge-probe.txt"), report, new UTF8Encoding(false));
        File.WriteAllText(failures == 0 ? pass : fail, report, new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }
    // Flush what is known so far, so a later hang cannot erase earlier results.
    static string partialPath;

    static MainWindow CreateWindow(bool hideMarkers) {
        MainWindow.hideMarkers = hideMarkers;
        ProbeDefaults.Live = true;
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 760 };
        window.Show();
        window.textMode = false;
        window.SetMode(true);
        LongLineBench.Idle();
        window.UpdateLayout();
        return window;
    }

    static void CloseWindow(MainWindow window) {
        if (window == null) return;
        // Mark every document saved first: a dirty window asks to save on Close, which hangs a hidden-desktop run.
        try {
            window.FlushDocumentChanges(); window.saved = window.text; window.recoveredDocument = false; window.CaptureTab();
            foreach (var document in window.documents) { document.Saved = document.Text; document.Recovered = false; }
            window.Close();
        } catch (Exception) { }
    }

    static void Check(string name, bool passed, string detail) {
        if (!passed) failures++;
        Lines.Add((passed ? "[PASS] " : "[FAIL] ") + name + (passed || string.IsNullOrEmpty(detail) ? "" : " :: " + detail));
        if (partialPath != null) { try { File.WriteAllText(partialPath, string.Join("\n", Lines.ToArray()) + "\n", new UTF8Encoding(false)); } catch (Exception) { } }
    }

    static string FlatText(string value) {
        return (value ?? "").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    static void PutCaret(MainWindow window, int offset) {
        window.source.Select(offset, 0);
        window.source.CaretOffset = offset;
        LongLineBench.Idle();
    }

    static bool PressLiveKey(MainWindow window, Key key) {
        var area = window.source.TextArea;
        var source = PresentationSource.FromVisual(area);
        if (source == null) return false;
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key);
        down.RoutedEvent = Keyboard.PreviewKeyDownEvent;
        area.RaiseEvent(down);
        if (!down.Handled) {
            down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key);
            down.RoutedEvent = Keyboard.KeyDownEvent;
            area.RaiseEvent(down);
        }
        LongLineBench.Idle();
        return down.Handled;
    }

    static void RunCmd(MainWindow window, RoutedCommand command) {
        command.Execute(null, window.source.TextArea);
        LongLineBench.Idle();
    }

    static void PressBackspace(MainWindow window) {
        if (!PressLiveKey(window, Key.Back)) RunCmd(window, EditingCommands.Backspace);
    }

    static void PressDelete(MainWindow window) {
        if (!PressLiveKey(window, Key.Delete)) RunCmd(window, EditingCommands.Delete);
    }

    static void RunViewSuite(bool hideMarkers) {
        string mode = hideMarkers ? "basic" : "live";
        MainWindow window = CreateWindow(hideMarkers);
        try {
            RunBehavior1EmptyCell(window, mode);
            RunBehavior2SpaceAtCellEnd(window, mode);
            RunBehavior3PaddingDeleteBackspace(window, mode);
            RunBehavior4MiddleColumnHomeEndArrows(window, mode);
            RunBehavior5BoldWordSteps(window, mode);
            RunBehavior6MultiWordCell(window, mode);
            RunBehavior8SelectionCoveringTrailingPadding(window, mode);
            RunBehavior8SingleTrailingSpace(window, mode);
            RunTask1ParityChecks(window, mode);
            RunTask2OuterEdgeToggleChecks(window, mode);
            RunTask3NestedRunsChecks(window, mode);
            RunTask4CloseSpacePendingChecks(window, mode);
            RunTask6CrossCellAndBoundaryDeleteChecks(window, mode);
            RunEmptyCellPasteAndPipeChecks(window, mode);
            RunCloseSpaceBackspaceChecks(window, mode);
            RunOverflowRowShiftTabChecks(window, mode);
            RunReviewFindingsChecks(window, mode);
            RunEmptyCellBackspaceChecks(window, mode);
            RunGfmPipeChecks(window, mode);
            RunHuntChecks(window, mode);
        } finally {
            CloseWindow(window);
        }
    }

    // 1. Empty cell `|  |`: type "x" or Shift+Enter lands one space inside closing pipe -> `| x |`, never `|  x|`.
    static void RunBehavior1EmptyCell(MainWindow window, string mode) {
        string fixture = "| h1 | h2 |\n| --- | --- |\n|  | b |\n";
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        int stop = row.Offset + 2; // middle of "|  |"
        PutCaret(window, stop);
        window.source.TextArea.PerformTextInput("x");
        LongLineBench.Idle();
        string rowText = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B1 empty cell type x -> | x |", rowText == "| x | b |", "row=[" + rowText + "]");

        // Shift+Enter (`\n`) at empty cell stop -> | <br> | b |
        window.SetText(fixture);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        PutCaret(window, row.Offset + 2);
        window.source.TextArea.PerformTextInput("\n");
        LongLineBench.Idle();
        rowText = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B1 empty cell Shift+Enter -> | <br> |", rowText == "| <br> | b |", "row=[" + rowText + "]");

        // Regular Enter in non-last empty cell -> | <br> | b |
        window.SetText(fixture);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        PutCaret(window, row.Offset + 2);
        PressLiveKey(window, Key.Enter);
        rowText = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B1 empty cell Enter -> | <br> |", rowText == "| <br> | b |", "row=[" + rowText + "]");

        // Shift+Enter even on closing pipe-adjacent offset `row.Offset + 3` (`|  |` end) clamps to EmptyCellStop -> `| <br> |`
        window.SetText(fixture);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        PutCaret(window, row.Offset + 3);
        window.source.TextArea.PerformTextInput("\n");
        LongLineBench.Idle();
        rowText = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B1 empty cell pipe-edge Shift+Enter -> | <br> |", rowText == "| <br> | b |", "row=[" + rowText + "]");
    }

    // 2. Cell end `| **a** |`, type Space, then Shift+Enter / Enter: goes AFTER that space, in the same cell.
    static void RunBehavior2SpaceAtCellEnd(MainWindow window, string mode) {
        string fixture = "| h1 | h2 |\n| --- | --- |\n| **a** | b |\n";
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info = window.livePreview.Info(row);
        int cellEnd = row.Offset + info.Cells[0].End;
        PutCaret(window, cellEnd);
        window.source.TextArea.PerformTextInput(" ");
        LongLineBench.Idle();
        string afterSpace = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        int caretAfterSpace = window.source.CaretOffset;
        Check(mode + " B2 type Space at cell end stays in cell", afterSpace == "| **a**  | b |" && caretAfterSpace == cellEnd + 1,
            "row=[" + afterSpace + "], caret=" + caretAfterSpace + "/" + (cellEnd + 1));

        window.source.TextArea.PerformTextInput("\n");
        LongLineBench.Idle();
        string afterShiftEnter = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B2 Shift+Enter after space goes after space in same cell",
            afterShiftEnter == "| **a** <br> | b |", "row=[" + afterShiftEnter + "]");

        // Enter key in non-last column after typed space
        window.SetText(fixture);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        PutCaret(window, row.Offset + info.Cells[0].End);
        window.source.TextArea.PerformTextInput(" ");
        LongLineBench.Idle();
        PressLiveKey(window, Key.Enter);
        string afterEnter = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B2 Enter after space goes after space in same cell",
            afterEnter == "| **a** <br> | b |", "row=[" + afterEnter + "]");
    }

    // 3. Two typed trailing spaces + Left once + Delete keeps padding wall; leading extra space + Backspace before b removes one space.
    static void RunBehavior3PaddingDeleteBackspace(MainWindow window, string mode) {
        string fixture = "| h1 | h2 |\n| --- | --- |\n| a | b |\n";
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info = window.livePreview.Info(row);
        PutCaret(window, row.Offset + info.Cells[0].End);
        window.source.TextArea.PerformTextInput(" ");
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput(" ");
        LongLineBench.Idle();
        RunCmd(window, EditingCommands.MoveLeftByCharacter);
        PressLiveKey(window, Key.Delete);
        string rowAfterDel = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B3 two trailing spaces Left+Delete keeps padding wall",
            rowAfterDel == "| a   | b |", "row=[" + rowAfterDel + "]");

        // Leading extra space `| a |  b |`, Backspace before b -> `| a | b |`
        string leadFixture = "| h1 | h2 |\n| --- | --- |\n| a |  b |\n";
        window.SetText(leadFixture);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        int beforeB = row.Offset + info.Cells[1].Start;
        PutCaret(window, beforeB);
        PressLiveKey(window, Key.Back);
        string rowAfterBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B3 leading extra space Backspace removes one space",
            rowAfterBack == "| a | b |" && window.source.CaretOffset == beforeB - 1,
            "row=[" + rowAfterBack + "], caret=" + window.source.CaretOffset + "/" + (beforeB - 1));
    }

    // 4. Caret in middle column trailing padding (after typed space): End stays in cell, Home goes to cell start,
    //    Right goes to next cell start (not past first char), Left moves 1 step left inside same cell.
    //    Empty middle cell `| a |  | c |`: Home/End stay in empty cell; Left goes to end of a without skipping a char.
    static void RunBehavior4MiddleColumnHomeEndArrows(MainWindow window, string mode) {
        string fixture = "| h1 | h2 | h3 |\n| --- | --- | --- |\n| left | mid | right |\n";
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info = window.livePreview.Info(row);
        int midStart = row.Offset + info.Cells[1].Start;
        int midEnd = row.Offset + info.Cells[1].End;
        PutCaret(window, midEnd);
        window.source.TextArea.PerformTextInput(" ");
        LongLineBench.Idle();
        // Now row is "| left | mid  | right |", caret at midEnd + 1 (after typed space)
        int padCaret = midEnd + 1;
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        int midInnerEnd;
        int midInnerStart;
        MarkdownLivePreview.CellBounds(info.Cells, 1, out midInnerStart, out midInnerEnd);

        // End stays in middle cell
        RunCmd(window, EditingCommands.MoveToLineEnd);
        int endPos = window.source.CaretOffset;
        Check(mode + " B4 middle trailing padding End stays in cell",
            endPos >= row.Offset + info.Cells[1].End && endPos <= row.Offset + midInnerEnd,
            "caret=" + endPos + ", mid=[" + (row.Offset + info.Cells[1].Start) + ".." + (row.Offset + midInnerEnd) + "]");

        // Home from trailing padding goes to middle cell text start
        PutCaret(window, padCaret);
        RunCmd(window, EditingCommands.MoveToLineStart);
        Check(mode + " B4 middle trailing padding Home goes to cell start",
            window.source.CaretOffset == midStart,
            "caret=" + window.source.CaretOffset + "/" + midStart);

        // Right from trailing padding goes to next cell text start (before 'r' of right)
        PutCaret(window, padCaret);
        RunCmd(window, EditingCommands.MoveRightByCharacter);
        int nextCellStart = row.Offset + info.Cells[2].Start;
        Check(mode + " B4 middle trailing padding Right goes to next cell start",
            window.source.CaretOffset == nextCellStart,
            "caret=" + window.source.CaretOffset + "/" + nextCellStart);

        // Left from trailing padding moves 1 step left inside same cell (to midEnd)
        PutCaret(window, padCaret);
        RunCmd(window, EditingCommands.MoveLeftByCharacter);
        Check(mode + " B4 middle trailing padding Left moves one step inside cell",
            window.source.CaretOffset == midEnd,
            "caret=" + window.source.CaretOffset + "/" + midEnd);

        // Empty middle cell `| a |  | c |`: Home/End stay in empty cell; Left goes to end of `a` without skipping `a`.
        string emptyMid = "| h1 | h2 | h3 |\n| --- | --- | --- |\n| a |  | c |\n";
        window.SetText(emptyMid);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        int emptyStop = row.Offset + MarkdownLivePreview.CellCaretStart(info.Cells[1]);
        int aEnd = row.Offset + info.Cells[0].End;

        PutCaret(window, emptyStop);
        RunCmd(window, EditingCommands.MoveToLineStart);
        Check(mode + " B4 empty middle cell Home stays in empty cell",
            window.source.CaretOffset == emptyStop, "caret=" + window.source.CaretOffset + "/" + emptyStop);

        PutCaret(window, emptyStop);
        RunCmd(window, EditingCommands.MoveToLineEnd);
        Check(mode + " B4 empty middle cell End stays in empty cell",
            window.source.CaretOffset == emptyStop, "caret=" + window.source.CaretOffset + "/" + emptyStop);

        PutCaret(window, emptyStop);
        RunCmd(window, EditingCommands.MoveLeftByCharacter);
        Check(mode + " B4 empty middle cell Left goes to end of a without skipping",
            window.source.CaretOffset == aEnd, "caret=" + window.source.CaretOffset + "/" + aEnd);
    }

    // 5. `| a **bc** |`, caret after bc: Ctrl+Left before b; Ctrl+Backspace removes bc with pair (`| a  |` or `| a |`).
    //    `| x | **abc** |`, caret after abc: Ctrl+Left stops at cell start, never previous cell.
    static void RunBehavior5BoldWordSteps(MainWindow window, string mode) {
        string fixture = "| h |\n| --- |\n| a **bc** |\n";
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        string raw = window.source.Document.GetText(row);
        int beforeB = row.Offset + raw.IndexOf("bc", StringComparison.Ordinal);
        int afterBc = mode == "basic" ? row.Offset + raw.IndexOf("** |", StringComparison.Ordinal) + 2 : beforeB + 2;

        PutCaret(window, afterBc);
        RunCmd(window, EditingCommands.MoveLeftByWord);
        Check(mode + " B5 | a **bc** | Ctrl+Left moves before b",
            window.source.CaretOffset == beforeB,
            "caret=" + window.source.CaretOffset + "/" + beforeB + ", raw=[" + raw + "]");

        PutCaret(window, afterBc);
        RunCmd(window, EditingCommands.DeletePreviousWord);
        string afterDel = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B5 | a **bc** | Ctrl+Backspace removes bc with its pair",
            afterDel == "| a  |" || afterDel == "| a |",
            "row=[" + afterDel + "]");

        string twoCol = "| h1 | h2 |\n| --- | --- |\n| x | **abc** |\n";
        window.SetText(twoCol);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info = window.livePreview.Info(row);
        raw = window.source.Document.GetText(row);
        int beforeA = row.Offset + raw.IndexOf("abc", StringComparison.Ordinal);
        int afterAbc = mode == "basic" ? row.Offset + info.Cells[1].End : beforeA + 3;
        PutCaret(window, afterAbc);
        RunCmd(window, EditingCommands.MoveLeftByWord);
        Check(mode + " B5 | x | **abc** | Ctrl+Left stops at cell start (never cell 0)",
            window.source.CaretOffset >= row.Offset + info.Cells[1].Start && window.source.CaretOffset <= beforeA,
            "caret=" + window.source.CaretOffset + ", cell1Start=" + (row.Offset + info.Cells[1].Start) + ", beforeA=" + beforeA);
    }

    // 6. `| a b |`, caret before b: Ctrl+Backspace removes `a ` like body text; Ctrl+Right needs 1 press per word.
    static void RunBehavior6MultiWordCell(MainWindow window, string mode) {
        // Measure body text oracle on the same window
        window.SetText("a b\n");
        LongLineBench.Idle();
        DocumentLine bodyLine = window.source.Document.GetLineByNumber(1);
        PutCaret(window, bodyLine.Offset + 2); // before b
        RunCmd(window, EditingCommands.DeletePreviousWord);
        string bodyAfterWordBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));

        window.SetText("a b\n");
        LongLineBench.Idle();
        PutCaret(window, 0);
        int bodyPresses = 0;
        for (int i = 0; i < 6; i++) {
            int prev = window.source.CaretOffset;
            RunCmd(window, EditingCommands.MoveRightByWord);
            if (window.source.CaretOffset == prev) break;
            bodyPresses++;
            if (window.source.CaretOffset >= 3) break;
        }

        string table = "| h |\n| --- |\n| a b |\n";
        window.SetText(table);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info = window.livePreview.Info(row);
        PutCaret(window, row.Offset + info.Cells[0].Start + 2); // before b
        RunCmd(window, EditingCommands.DeletePreviousWord);
        string rowAfter = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B6 | a b | before b Ctrl+Backspace removes 'a ' like body text",
            rowAfter == "| " + bodyAfterWordBack + " |" && bodyAfterWordBack == "b",
            "row=[" + rowAfter + "], body=[" + bodyAfterWordBack + "]");

        window.SetText(table);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        int start = row.Offset + info.Cells[0].Start;
        int end = row.Offset + info.Cells[0].End;
        PutCaret(window, start);
        int tablePresses = 0;
        for (int i = 0; i < 6; i++) {
            int prev = window.source.CaretOffset;
            RunCmd(window, EditingCommands.MoveRightByWord);
            if (window.source.CaretOffset == prev || window.source.CaretOffset > end) break;
            tablePresses++;
            if (window.source.CaretOffset == end) break;
        }
        Check(mode + " B6 | a b | Ctrl+Right needs 1 press per word (matches body text)",
            tablePresses == 2 && tablePresses == bodyPresses,
            "tablePresses=" + tablePresses + ", bodyPresses=" + bodyPresses);
    }

    // 7. Live view, unpadded `|abc|def|`, caret after `abc`: Ctrl+Backspace removes `abc` only -> `||def|`; no pipe deleted.
    static void RunLiveUnpaddedWordSuite() {
        MainWindow window = CreateWindow(false);
        try {
            string fixture = "|h1|h2|\n|---|---|\n|abc|def|\n";
            window.SetText(fixture);
            LongLineBench.Idle();
            DocumentLine row = window.source.Document.GetLineByNumber(3);
            string raw = window.source.Document.GetText(row);
            int afterAbc = row.Offset + raw.IndexOf("abc", StringComparison.Ordinal) + 3;
            PutCaret(window, afterAbc);
            RunCmd(window, EditingCommands.DeletePreviousWord);
            string afterRow = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
            Check("live B7 unpadded |abc|def| Ctrl+Backspace -> ||def|",
                afterRow == "||def|", "row=[" + afterRow + "]");
        } finally {
            CloseWindow(window);
        }
    }

    // 8. Selection inside one cell covering trailing padding (`| **ab**  |`, select `b` to padding end), Delete:
    //    removes `b` and spaces, keeps pair balanced -> `| **a** |`, never `**a`.
    static void RunBehavior8SelectionCoveringTrailingPadding(MainWindow window, string mode) {
        if (mode != "basic") return; // In live view markers are raw text on screen
        string fixture = "| h |\n| --- |\n| **ab**  |\n";
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        string raw = window.source.Document.GetText(row);
        int bPos = row.Offset + raw.IndexOf('b');
        int padEndBeforeCanonical = row.Offset + raw.LastIndexOf('|') - 1;
        window.source.Select(bPos, padEndBeforeCanonical - bPos);
        LongLineBench.Idle();
        PressLiveKey(window, Key.Delete);
        string afterRow = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " B8 selection b..padding Delete keeps **a** balanced",
            afterRow == "| **a** |", "row=[" + afterRow + "]");
    }

    // ---- Stage 3 probe checks (Tasks 0..6) ----

    static int CountSubstrings(string text, string needle) {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(needle)) return 0;
        int count = 0, index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) {
            count++;
            index += needle.Length;
        }
        return count;
    }

    static bool HasStyleAtOffset(string text, bool table, int charIndex, out bool bold, out bool italic) {
        bold = false;
        italic = false;
        MarkdownLivePreview.LineInfo parsed = MarkdownLivePreview.ParseInlineText(text, table);
        if (parsed == null || parsed.Styles == null || charIndex < 0 || charIndex >= text.Length) return false;
        foreach (MarkdownLivePreview.StyledPart part in parsed.Styles) {
            if (charIndex < part.Start || charIndex >= part.Start + part.Length) continue;
            if ((part.Style & MarkdownLivePreview.Style.Bold) != 0) bold = true;
            if ((part.Style & MarkdownLivePreview.Style.Italic) != 0) italic = true;
        }
        return true;
    }

    static MethodInfo FindSetImeInlineMethod() {
        foreach (MethodInfo m in typeof(SourceEditor).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)) {
            if (m.Name != "SetImeInline") continue;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) return m;
        }
        return null;
    }

    static MethodInfo FindInsertCommittedTextMethod() {
        foreach (MethodInfo m in typeof(SourceEditor).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)) {
            if (m.Name != "InsertCommittedText") continue;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(string)) return m;
        }
        return null;
    }

    static void SafeCleanupIme(MethodInfo setIme, SourceEditor editor, Func<bool> oldLayout) {
        if (editor == null) return;
        try {
            if (setIme != null) setIme.Invoke(editor, new object[] { null });
        } catch (Exception) { }
        try {
            editor.ResetCompositionSimulation();
        } catch (Exception) { }
        if (oldLayout != null) editor.NonKoreanImeLayout = oldLayout;
        LongLineBench.Idle();
    }

    // Task 0: B8 single-trailing-space variant (`| **ab** |`, selection `b`..trailing space, Delete)
    // verifies balanced `**a**` AND canonical single trailing space before `|` preserved.
    static void RunBehavior8SingleTrailingSpace(MainWindow window, string mode) {
        if (mode != "basic") return;
        string fixture = "| h |\n| --- |\n| **ab** |\n";
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        string raw = window.source.Document.GetText(row);
        int bPos = row.Offset + raw.IndexOf('b');
        int beforePipe = row.Offset + raw.LastIndexOf('|');
        window.source.Select(bPos, beforePipe - bPos);
        LongLineBench.Idle();
        PressLiveKey(window, Key.Delete);
        string afterRow = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task0 B8 single trailing space selection b..space Delete -> | **a** |",
            afterRow == "| **a** |", "row=[" + afterRow + "]");
    }

    // Task 1: H10 removal & FormatAt/LiveDrawnSize parity (both Basic and Live views).
    static void RunTask1ParityChecks(MainWindow window, string mode) {
        // 1a. Typing "Q" at cell end right after closing run (`| **bold**| |`) -> `| **boldQ** |`
        window.SetText("| h |\n| --- |\n| **bold** |\n");
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info = window.livePreview.Info(row);
        window.source.Select(row.Offset + info.Cells[0].End, 0);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput("Q");
        LongLineBench.Idle();
        string afterQ = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task1 typing Q at **bold** cell end -> | **boldQ** |",
            afterQ == "| **boldQ** |", "row=[" + afterQ + "]");

        // 1b. IME commit "가" via InsertCommittedText and PerformTextInput at `| **bold**| |` -> `| **bold가** |`
        MethodInfo insertCommit = FindInsertCommittedTextMethod();
        window.SetText("| h |\n| --- |\n| **bold** |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        int commitAt = row.Offset + info.Cells[0].End;
        window.source.Select(commitAt, 0);
        LongLineBench.Idle();
        if (insertCommit != null) {
            insertCommit.Invoke(window.source, new object[] { commitAt, "가" });
            LongLineBench.Idle();
        }
        string afterImeCommit = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task1 InsertCommittedText 가 at **bold** cell end -> | **bold가** |",
            insertCommit != null && afterImeCommit == "| **bold가** |", "row=[" + afterImeCommit + "]");

        window.SetText("| h |\n| --- |\n| **bold** |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        window.source.Select(row.Offset + info.Cells[0].End, 0);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput("가");
        LongLineBench.Idle();
        string afterInputGa = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task1 PerformTextInput 가 at **bold** cell end -> | **bold가** |",
            afterInputGa == "| **bold가** |", "row=[" + afterInputGa + "]");

        // 1c. Typing " " at inner edge `| **bold|** |` then "x" vs outer edge `| **bold**| |` then " " then "x"
        window.SetText("| h |\n| --- |\n| **bold** |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        string raw = window.source.Document.GetText(row);
        int innerCloser = row.Offset + raw.IndexOf("**", raw.IndexOf("**", StringComparison.Ordinal) + 2);
        window.source.Select(innerCloser, 0);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput(" ");
        window.source.TextArea.PerformTextInput("x");
        LongLineBench.Idle();
        string afterInnerSpaceX = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task1 space then x at inner edge -> | **bold x** |",
            afterInnerSpaceX == "| **bold x** |", "row=[" + afterInnerSpaceX + "]");

        window.SetText("| h |\n| --- |\n| **bold** |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        window.source.Select(row.Offset + info.Cells[0].End, 0);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput(" ");
        LongLineBench.Idle();
        string afterOuterSpace = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        window.source.TextArea.PerformTextInput("x");
        LongLineBench.Idle();
        string afterOuterSpaceX = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task1 space then x at outer edge separates outside -> | **bold** x |",
            afterOuterSpace == "| **bold**  |" && afterOuterSpaceX == "| **bold** x |",
            "space=[" + afterOuterSpace + "], x=[" + afterOuterSpaceX + "]");

        // 1d. Clicking empty area right of cell text (TableOffsetAtPoint / PlaceTableCaret) and typing "Q"
        window.SetText("| wide header column | h2 |\n| --- | --- |\n| **bold** | x |\n");
        LongLineBench.Idle();
        window.source.Select(0, 0);
        LongLineBench.Idle();
        DocumentLine row3 = window.source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info3 = window.livePreview.Info(row3);
        window.UpdateLayout();
        VisualLine vl3 = window.livePreview.View.GetOrConstructVisualLine(row3);
        LongLineBench.Idle();
        Rect box0 = window.livePreview.TableCellBox(vl3, 0);
        Point clickPt = new Point(box0.Right - 4, box0.Top + box0.Height * 0.5);
        int hitOffset = -1;
        bool gotHit = !box0.IsEmpty && window.livePreview.TableOffsetAtPoint(clickPt, out hitOffset);
        bool placed = gotHit && window.livePreview.PlaceTableCaret(clickPt, false);
        LongLineBench.Idle();
        if (placed) {
            window.source.TextArea.PerformTextInput("Q");
            LongLineBench.Idle();
        }
        string afterClickQ = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task1 click empty right cell area continues **boldQ**",
            gotHit && placed && hitOffset == row3.Offset + info3.Cells[0].End && afterClickQ == "| **boldQ** | x |",
            "hit=" + hitOffset + ", expected=" + (row3.Offset + info3.Cells[0].End) + ", box=" + box0 + ", row=[" + afterClickQ + "]");

        // 1e. FormatAt and LiveDrawnSize parity at cell end and during closeSpacePending
        window.SetText("| h |\n| --- |\n| **bold** |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        int cellEnd = row.Offset + info.Cells[0].End;
        window.source.Select(cellEnd, 0);
        LongLineBench.Idle();
        bool b, it, st, co, hi;
        int hd;
        window.livePreview.FormatAt(cellEnd, out b, out it, out st, out co, out hi, out hd);
        Check(mode + " Task1 FormatAt at **bold** cell end reports Bold=true", b, "bold=" + b);

        window.SetText("| h |\n| --- |\n| <span style=\"font-size:20px\">big</span> |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        window.source.Select(row.Offset + info.Cells[0].End, 0);
        LongLineBench.Idle();
        double drawnSpanSize = window.LiveDrawnSize();
        Check(mode + " Task1 LiveDrawnSize at sized span cell end reports 20",
            Math.Abs(drawnSpanSize - 20.0) < 0.01, "size=" + drawnSpanSize);

        window.SetText("| h |\n| --- |\n| **<span style=\"font-size:20px\">big</span>** |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        raw = window.source.Document.GetText(row);
        int closeStars = row.Offset + raw.LastIndexOf("**", StringComparison.Ordinal);
        window.source.Select(closeStars, 0);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput(" ");
        LongLineBench.Idle();
        window.livePreview.FormatAt(window.source.CaretIndex, out b, out it, out st, out co, out hi, out hd);
        double pendingSize = window.LiveDrawnSize();
        Check(mode + " Task1 closeSpacePending LiveDrawnSize and FormatAt agree",
            b && Math.Abs(pendingSize - 20.0) < 0.01, "bold=" + b + ", size=" + pendingSize);
    }

    // Task 2: Toggle at closed run's outer edge (both Basic and Live views, body and table cell).
    static void CheckOuterToggleSingle(MainWindow window, string mode, string label, string innerMarkdown, string toggleKind, string outerTextExpected, bool inCell) {
        string docText = inCell ? "| h |\n| --- |\n| " + innerMarkdown + " |\n" : innerMarkdown + "\n";
        window.SetText(docText);
        LongLineBench.Idle();
        DocumentLine line = window.source.Document.GetLineByNumber(inCell ? 3 : 1);
        int outerOffset = inCell ? line.Offset + window.livePreview.Info(line).Cells[0].End : line.Offset + innerMarkdown.Length;
        window.source.Select(outerOffset, 0);
        LongLineBench.Idle();
        string beforeDoc = window.source.Document.Text;
        window.LiveInline(toggleKind);
        LongLineBench.Idle();
        string afterToggleDoc = window.source.Document.Text;
        bool b, it, st, co, hi;
        int hd;
        window.livePreview.FormatAt(window.source.CaretIndex, out b, out it, out st, out co, out hi, out hd);
        bool active = toggleKind == "bold" ? b : toggleKind == "italic" ? it : toggleKind == "strike" ? st : hi;
        bool armed = window.TypingStyleArmed;
        window.source.TextArea.PerformTextInput("x");
        LongLineBench.Idle();
        string actualLine = window.source.Document.GetText(window.source.Document.GetLineByNumber(inCell ? 3 : 1));
        string expectedLine = inCell ? "| " + outerTextExpected + " |" : outerTextExpected;
        Check(mode + " Task2 outer toggle " + toggleKind + " (" + label + ") turns off without arming",
            !active && !armed && afterToggleDoc == beforeDoc && actualLine == expectedLine,
            "active=" + active + ", armed=" + armed + ", line=[" + actualLine + "]");
    }

    static void RunTask2OuterEdgeToggleChecks(MainWindow window, string mode) {
        foreach (bool inCell in new bool[] { false, true }) {
            string place = inCell ? "cell" : "body";
            CheckOuterToggleSingle(window, mode, place + " **굵게**", "**굵게**", "bold", "**굵게**x", inCell);
            CheckOuterToggleSingle(window, mode, place + " *기울임*", "*기울임*", "italic", "*기울임*x", inCell);
            CheckOuterToggleSingle(window, mode, place + " ~~취소~~", "~~취소~~", "strike", "~~취소~~x", inCell);
            CheckOuterToggleSingle(window, mode, place + " ==형광==", "==형광==", "highlight", "==형광==x", inCell);

            // Twice toggle at **굵게**| resumes bold
            string docTwice = inCell ? "| h |\n| --- |\n| **굵게** |\n" : "**굵게**\n";
            window.SetText(docTwice);
            LongLineBench.Idle();
            DocumentLine line = window.source.Document.GetLineByNumber(inCell ? 3 : 1);
            int outer = inCell ? line.Offset + window.livePreview.Info(line).Cells[0].End : line.Offset + "**굵게**".Length;
            window.source.Select(outer, 0);
            LongLineBench.Idle();
            window.LiveInline("bold");
            LongLineBench.Idle();
            window.LiveInline("bold");
            LongLineBench.Idle();
            bool b, it, st, co, hi;
            int hd;
            window.livePreview.FormatAt(window.source.CaretIndex, out b, out it, out st, out co, out hi, out hd);
            window.source.TextArea.PerformTextInput("x");
            LongLineBench.Idle();
            string actualTwice = window.source.Document.GetText(window.source.Document.GetLineByNumber(inCell ? 3 : 1));
            string expectedTwice = inCell ? "| **굵게x** |" : "**굵게x**";
            Check(mode + " Task2 double LiveInline(bold) at " + place + " **굵게**| resumes bold",
                b && actualTwice == expectedTwice, "bold=" + b + ", line=[" + actualTwice + "]");

            // Color span outer edge
            string colorSpan = "<span style=\"color:#FF0000\">red</span>";
            window.SetText(inCell ? "| h |\n| --- |\n| " + colorSpan + " |\n" : colorSpan + "\n");
            LongLineBench.Idle();
            line = window.source.Document.GetLineByNumber(inCell ? 3 : 1);
            outer = inCell ? line.Offset + window.livePreview.Info(line).Cells[0].End : line.Offset + colorSpan.Length;
            window.source.Select(outer, 0);
            LongLineBench.Idle();
            window.LiveColor(null);
            LongLineBench.Idle();
            bool colorArmed = window.TypingStyleArmed;
            window.source.TextArea.PerformTextInput("x");
            LongLineBench.Idle();
            string actualColor = window.source.Document.GetText(window.source.Document.GetLineByNumber(inCell ? 3 : 1));
            string expectedColor = inCell ? "| " + colorSpan + "x |" : colorSpan + "x";
            Check(mode + " Task2 LiveColor(null) at " + place + " color span outer edge",
                !colorArmed && actualColor == expectedColor, "armed=" + colorArmed + ", line=[" + actualColor + "]");

            // Size span outer edge
            string sizeSpan = "<span style=\"font-size:20px\">big</span>";
            window.SetText(inCell ? "| h |\n| --- |\n| " + sizeSpan + " |\n" : sizeSpan + "\n");
            LongLineBench.Idle();
            line = window.source.Document.GetLineByNumber(inCell ? 3 : 1);
            outer = inCell ? line.Offset + window.livePreview.Info(line).Cells[0].End : line.Offset + sizeSpan.Length;
            window.source.Select(outer, 0);
            LongLineBench.Idle();
            window.SetFontSizeForProbe(window.fontSize);
            LongLineBench.Idle();
            double droppedSize = window.LiveDrawnSize();
            window.source.TextArea.PerformTextInput("x");
            LongLineBench.Idle();
            string actualSize = window.source.Document.GetText(window.source.Document.GetLineByNumber(inCell ? 3 : 1));
            string expectedSize = inCell ? "| " + sizeSpan + "x |" : sizeSpan + "x";
            Check(mode + " Task2 SetFontSize(baseSize) at " + place + " size span outer edge",
                Math.Abs(droppedSize - window.fontSize) < 0.01 && actualSize == expectedSize,
                "size=" + droppedSize + ", line=[" + actualSize + "]");
        }
    }

    // Task 3: Nested runs (`***테스트***` in body and table cell, both Basic and Live views).
    static void CheckNestedCasesForPlace(MainWindow window, string mode, bool inCell) {
        string place = inCell ? "cell" : "body";
        int lineNum = inCell ? 3 : 1;
        string fixture = inCell ? "| h |\n| --- |\n| ***테스트*** |\n" : "***테스트***\n";

        // 3a. Inner edge `***테스트|***` + LiveInline("italic") -> bold=true, italic=false, type "x" -> `***테스트*x**`
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine line = window.source.Document.GetLineByNumber(lineNum);
        string raw = window.source.Document.GetText(line);
        int innerAt = line.Offset + raw.IndexOf("***", raw.IndexOf("***", StringComparison.Ordinal) + 3);
        window.source.Select(innerAt, 0);
        LongLineBench.Idle();
        string beforeText = window.source.Document.Text;
        window.LiveInline("italic");
        LongLineBench.Idle();
        bool b, it, st, co, hi;
        int hd;
        window.livePreview.FormatAt(window.source.CaretIndex, out b, out it, out st, out co, out hi, out hd);
        bool armed1 = window.TypingStyleArmed;
        string midText1 = window.source.Document.Text;
        window.source.TextArea.PerformTextInput("x");
        LongLineBench.Idle();
        string line1 = window.source.Document.GetText(window.source.Document.GetLineByNumber(lineNum));
        string exp1 = inCell ? "| ***테스트*x** |" : "***테스트*x**";
        bool pb1, pi1;
        HasStyleAtOffset(line1, inCell, line1.IndexOf('x'), out pb1, out pi1);
        Check(mode + " Task3 " + place + " inner italic off -> ***테스트*x** (bold-only)",
            !armed1 && midText1 == beforeText && b && !it && line1 == exp1 && pb1 && !pi1,
            "b=" + b + ", it=" + it + ", armed=" + armed1 + ", line=[" + line1 + "], parsed=(" + pb1 + "," + pi1 + ")");

        // 3b. Inner edge `***테스트|***` + LiveInline("bold") -> bold=false, italic=true, type "x" -> `***테스트**x*`
        window.SetText(fixture);
        LongLineBench.Idle();
        line = window.source.Document.GetLineByNumber(lineNum);
        raw = window.source.Document.GetText(line);
        innerAt = line.Offset + raw.IndexOf("***", raw.IndexOf("***", StringComparison.Ordinal) + 3);
        window.source.Select(innerAt, 0);
        LongLineBench.Idle();
        window.LiveInline("bold");
        LongLineBench.Idle();
        window.livePreview.FormatAt(window.source.CaretIndex, out b, out it, out st, out co, out hi, out hd);
        window.source.TextArea.PerformTextInput("x");
        LongLineBench.Idle();
        string line2 = window.source.Document.GetText(window.source.Document.GetLineByNumber(lineNum));
        string exp2 = inCell ? "| ***테스트**x* |" : "***테스트**x*";
        bool pb2, pi2;
        HasStyleAtOffset(line2, inCell, line2.IndexOf('x'), out pb2, out pi2);
        Check(mode + " Task3 " + place + " inner bold off -> ***테스트**x* (italic-only)",
            !b && it && line2 == exp2 && !pb2 && pi2,
            "b=" + b + ", it=" + it + ", line=[" + line2 + "], parsed=(" + pb2 + "," + pi2 + ")");

        // 3c. Outer edge `***테스트***|` + LiveInline("italic") -> type "x" -> `***테스트*x**`
        window.SetText(fixture);
        LongLineBench.Idle();
        line = window.source.Document.GetLineByNumber(lineNum);
        int outerAt = inCell ? line.Offset + window.livePreview.Info(line).Cells[0].End : line.Offset + "***테스트***".Length;
        window.source.Select(outerAt, 0);
        LongLineBench.Idle();
        window.LiveInline("italic");
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput("x");
        LongLineBench.Idle();
        string line3 = window.source.Document.GetText(window.source.Document.GetLineByNumber(lineNum));
        Check(mode + " Task3 " + place + " outer italic off -> ***테스트*x**",
            line3 == exp1, "line=[" + line3 + "]");

        // 3d. Outer edge `***테스트***|` + LiveInline("italic") then LiveInline("bold") -> both OFF -> type "x" -> `***테스트***x`
        window.SetText(fixture);
        LongLineBench.Idle();
        line = window.source.Document.GetLineByNumber(lineNum);
        outerAt = inCell ? line.Offset + window.livePreview.Info(line).Cells[0].End : line.Offset + "***테스트***".Length;
        window.source.Select(outerAt, 0);
        LongLineBench.Idle();
        window.LiveInline("italic");
        LongLineBench.Idle();
        window.LiveInline("bold");
        LongLineBench.Idle();
        window.livePreview.FormatAt(window.source.CaretIndex, out b, out it, out st, out co, out hi, out hd);
        window.source.TextArea.PerformTextInput("x");
        LongLineBench.Idle();
        string line4 = window.source.Document.GetText(window.source.Document.GetLineByNumber(lineNum));
        string exp4 = inCell ? "| ***테스트***x |" : "***테스트***x";
        Check(mode + " Task3 " + place + " outer italic+bold off -> ***테스트***x",
            !b && !it && line4 == exp4, "b=" + b + ", it=" + it + ", line=[" + line4 + "]");
    }

    static void RunTask3NestedRunsChecks(MainWindow window, string mode) {
        CheckNestedCasesForPlace(window, mode, false);
        CheckNestedCasesForPlace(window, mode, true);
    }

    // Task 4: Close-space pending (`| **테스트** |`) — runs in both Basic and Live views (`CloseSpaceAllowed`).
    static void RunTask4CloseSpacePendingChecks(MainWindow window, string mode) {
        string fixture = "| h |\n| --- |\n| **테스트** |\n";

        // 4a-1: space, LiveInline("bold"), LiveInline("bold"), then "가" -> `| **테스트 가** |` (one run)
        window.SetText(fixture);
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        string raw = window.source.Document.GetText(row);
        int innerAt = row.Offset + raw.IndexOf("**", raw.IndexOf("**", StringComparison.Ordinal) + 2);
        window.source.Select(innerAt, 0);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput(" ");
        window.LiveInline("bold");
        window.LiveInline("bold");
        window.source.TextArea.PerformTextInput("가");
        LongLineBench.Idle();
        string row4a1 = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task4a double LiveInline(bold) during closeSpacePending -> | **테스트 가** |",
            row4a1 == "| **테스트 가** |", "row=[" + row4a1 + "]");

        // 4a-2: space, single LiveInline("bold"), then "가" -> `| **테스트** 가 |`
        window.SetText(fixture);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        raw = window.source.Document.GetText(row);
        innerAt = row.Offset + raw.IndexOf("**", raw.IndexOf("**", StringComparison.Ordinal) + 2);
        window.source.Select(innerAt, 0);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput(" ");
        window.LiveInline("bold");
        window.source.TextArea.PerformTextInput("가");
        LongLineBench.Idle();
        string row4a2 = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task4a single LiveInline(bold) during closeSpacePending -> | **테스트** 가 |",
            row4a2 == "| **테스트** 가 |", "row=[" + row4a2 + "]");

        // 4b: space, LiveInline("italic"), then "가" -> `| **테스트 *가*** |` (bold+italic)
        window.SetText(fixture);
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        raw = window.source.Document.GetText(row);
        innerAt = row.Offset + raw.IndexOf("**", raw.IndexOf("**", StringComparison.Ordinal) + 2);
        window.source.Select(innerAt, 0);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput(" ");
        window.LiveInline("italic");
        window.source.TextArea.PerformTextInput("가");
        LongLineBench.Idle();
        string row4b = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        bool pb, pi;
        HasStyleAtOffset(row4b, true, row4b.IndexOf('가'), out pb, out pi);
        Check(mode + " Task4b LiveInline(italic) during closeSpacePending -> | **테스트 *가*** |",
            row4b == "| **테스트 *가*** |" && pb && pi,
            "row=[" + row4b + "], bold=" + pb + ", italic=" + pi);
    }

    // Task 5: Table IME drawing suite (Live + Basic view checks, VisualLine / RowLayout / CellText).
    static void RunStage3Task5TableImeDrawingSuite() {
        MethodInfo setIme = FindSetImeInlineMethod();
        Check("Task5 SetImeInline(string) reflection seam exists", setIme != null, setIme == null ? "not found" : "ok");
        if (setIme == null) return;

        // 5(a) Live view (!HideMarkers): caret after space in trailing padding -> no duplicate ImeLead space
        MainWindow liveWin = CreateWindow(false);
        Func<bool> liveOldLayout = liveWin.source.NonKoreanImeLayout;
        try {
            liveWin.source.NonKoreanImeLayout = delegate { return false; };
            liveWin.SetText("| h |\n| --- |\n| **테스트** |\n");
            LongLineBench.Idle();
            DocumentLine row = liveWin.source.Document.GetLineByNumber(3);
            MarkdownLivePreview.LineInfo info = liveWin.livePreview.Info(row);
            liveWin.source.Select(row.Offset + info.Cells[0].End, 0);
            LongLineBench.Idle();
            liveWin.source.TextArea.PerformTextInput(" ");
            LongLineBench.Idle();
            liveWin.UpdateLayout();
            row = liveWin.source.Document.GetLineByNumber(3);
            liveWin.livePreview.View.GetOrConstructVisualLine(row);
            LongLineBench.Idle();
            info = liveWin.livePreview.Info(row);
            Check("live Task5a caret after trailing space on raw caret line has no duplicate ImeLead",
                info.ImeLead == 0 && info.LeadCell == -1,
                "ImeLead=" + info.ImeLead + ", LeadCell=" + info.LeadCell);
        } finally {
            SafeCleanupIme(setIme, liveWin.source, liveOldLayout);
            CloseWindow(liveWin);
        }

        // 5(b..e) Basic view (HideMarkers)
        MainWindow basicWin = CreateWindow(true);
        Func<bool> basicOldLayout = basicWin.source.NonKoreanImeLayout;
        try {
            basicWin.source.NonKoreanImeLayout = delegate { return false; };

            // 5(b)-1 & 5(c): `| **테스트|** |` + " " + SetImeInline("나") -> Bold Weight on "나" + TableImeStyleAt
            try {
                basicWin.SetText("| h |\n| --- |\n| **테스트** |\n");
                LongLineBench.Idle();
                DocumentLine row = basicWin.source.Document.GetLineByNumber(3);
                string raw = basicWin.source.Document.GetText(row);
                int innerRel = raw.IndexOf("**", raw.IndexOf("**", StringComparison.Ordinal) + 2);
                basicWin.source.Select(row.Offset + innerRel, 0);
                LongLineBench.Idle();
                basicWin.source.TextArea.PerformTextInput(" ");
                LongLineBench.Idle();
                setIme.Invoke(basicWin.source, new object[] { "나" });
                LongLineBench.Idle();
                basicWin.UpdateLayout();
                row = basicWin.source.Document.GetLineByNumber(3);
                basicWin.livePreview.View.GetOrConstructVisualLine(row);
                LongLineBench.Idle();
                MarkdownLivePreview.LineInfo info = basicWin.livePreview.Info(row);
                MarkdownLivePreview.CellText ct = info.Row != null && info.Row.Texts != null && info.Row.Texts.Length > 0 ? info.Row.Texts[0] : null;
                int naIdx = ct == null || ct.Text == null ? -1 : ct.Text.IndexOf('나');
                bool boldIme = naIdx >= 0 && ct.Properties != null && naIdx < ct.Properties.Length && ct.Properties[naIdx] != null &&
                    ct.Properties[naIdx].Typeface.Weight.ToOpenTypeWeight() >= FontWeights.Bold.ToOpenTypeWeight();
                Check("basic Task5b SetImeInline 나 after close-space in **테스트** draws Bold",
                    boldIme, "text=[" + (ct == null ? "null" : ct.Text) + "], naIdx=" + naIdx);

                int styleAt = basicWin.livePreview.TableImeStyleAt(info, info.ImeAt);
                Check("basic Task5c TableImeStyleAt during closeSpacePending returns inner closer offset",
                    info.Cells != null && info.Cells.Count > 0 && basicWin.source.CaretIndex > row.Offset + info.Cells[0].End && styleAt == innerRel,
                    "styleAt=" + styleAt + ", expected=" + innerRel + ", cellEnd=" + (info.Cells != null && info.Cells.Count > 0 ? info.Cells[0].End : -1));
            } finally {
                SafeCleanupIme(setIme, basicWin.source, null);
            }

            // 5(b)-2: `| ==테스트==| |` + " " + SetImeInline("나") -> highlight shade non-zero on "나"
            try {
                basicWin.SetText("| h |\n| --- |\n| ==테스트== |\n");
                LongLineBench.Idle();
                DocumentLine row = basicWin.source.Document.GetLineByNumber(3);
                string raw = basicWin.source.Document.GetText(row);
                int innerRel = raw.IndexOf("==", raw.IndexOf("==", StringComparison.Ordinal) + 2);
                basicWin.source.Select(row.Offset + innerRel, 0);
                LongLineBench.Idle();
                basicWin.source.TextArea.PerformTextInput(" ");
                LongLineBench.Idle();
                setIme.Invoke(basicWin.source, new object[] { "나" });
                LongLineBench.Idle();
                basicWin.UpdateLayout();
                row = basicWin.source.Document.GetLineByNumber(3);
                basicWin.livePreview.View.GetOrConstructVisualLine(row);
                LongLineBench.Idle();
                MarkdownLivePreview.LineInfo info = basicWin.livePreview.Info(row);
                MarkdownLivePreview.CellText ct = info.Row != null && info.Row.Texts != null && info.Row.Texts.Length > 0 ? info.Row.Texts[0] : null;
                int naIdx = ct == null || ct.Text == null ? -1 : ct.Text.IndexOf('나');
                bool shadedIme = naIdx >= 0 && ct.Shades != null && naIdx < ct.Shades.Length && ct.Shades[naIdx] != 0;
                Check("basic Task5b SetImeInline 나 after close-space in ==테스트== carries highlight shade",
                    shadedIme, "text=[" + (ct == null ? "null" : ct.Text) + "], naIdx=" + naIdx + ", shade=" + (naIdx >= 0 && ct.Shades != null && naIdx < ct.Shades.Length ? ct.Shades[naIdx].ToString() : "null"));
            } finally {
                SafeCleanupIme(setIme, basicWin.source, null);
            }

            // 5(d)-1: Moving caret into/out of trailing padding when NOT composing invalidates LayoutRow cache
            basicWin.SetText("| h |\n| --- |\n| ab  |\n");
            LongLineBench.Idle();
            DocumentLine padRow = basicWin.source.Document.GetLineByNumber(3);
            int padCellEnd = basicWin.livePreview.Info(padRow).Cells[0].End;
            basicWin.source.Select(padRow.Offset + padCellEnd + 1, 0);
            LongLineBench.Idle();
            basicWin.UpdateLayout();
            basicWin.livePreview.View.GetOrConstructVisualLine(padRow);
            LongLineBench.Idle();
            MarkdownLivePreview.LineInfo inPadInfo = basicWin.livePreview.Info(padRow);
            int inPadLeadCell = inPadInfo.Row != null ? inPadInfo.Row.LeadCell : -2;
            int inPadImeLead = inPadInfo.Row != null ? inPadInfo.Row.ImeLead : -2;

            basicWin.source.Select(padRow.Offset + padCellEnd, 0);
            LongLineBench.Idle();
            basicWin.UpdateLayout();
            basicWin.livePreview.View.GetOrConstructVisualLine(padRow);
            LongLineBench.Idle();
            MarkdownLivePreview.LineInfo outPadInfo = basicWin.livePreview.Info(padRow);
            int outPadLeadCell = outPadInfo.Row != null ? outPadInfo.Row.LeadCell : -2;
            int outPadImeLead = outPadInfo.Row != null ? outPadInfo.Row.ImeLead : -2;
            Check("basic Task5d caret into/out of trailing padding updates RowLayout LeadCell and ImeLead",
                inPadLeadCell == 0 && inPadImeLead == 1 && outPadLeadCell == -1 && outPadImeLead == 0,
                "in=(" + inPadLeadCell + "," + inPadImeLead + "), out=(" + outPadLeadCell + "," + outPadImeLead + ")");

            // 5(d)-2: Toggling Highlight / NoBold / NoItalic updates info.ImeKey while composing
            try {
                basicWin.SetText("| h |\n| --- |\n| ***테스트*** |\n");
                LongLineBench.Idle();
                DocumentLine keyRow = basicWin.source.Document.GetLineByNumber(3);
                int keyCellEnd = basicWin.livePreview.Info(keyRow).Cells[0].End;
                basicWin.source.Select(keyRow.Offset + keyCellEnd, 0);
                LongLineBench.Idle();
                setIme.Invoke(basicWin.source, new object[] { "나" });
                basicWin.UpdateLayout();
                basicWin.livePreview.View.Redraw(keyRow);
                basicWin.livePreview.View.GetOrConstructVisualLine(keyRow);
                string key0 = basicWin.livePreview.Info(keyRow).ImeKey;

                basicWin.LiveInline("highlight");
                LongLineBench.Idle();
                setIme.Invoke(basicWin.source, new object[] { "나" });
                basicWin.UpdateLayout();
                basicWin.livePreview.View.Redraw(keyRow);
                basicWin.livePreview.View.GetOrConstructVisualLine(keyRow);
                string keyH = basicWin.livePreview.Info(keyRow).ImeKey;

                basicWin.LiveInline("highlight");
                LongLineBench.Idle();
                basicWin.LiveInline("bold");
                LongLineBench.Idle();
                setIme.Invoke(basicWin.source, new object[] { "나" });
                basicWin.UpdateLayout();
                basicWin.livePreview.View.Redraw(keyRow);
                basicWin.livePreview.View.GetOrConstructVisualLine(keyRow);
                string keyB = basicWin.livePreview.Info(keyRow).ImeKey;

                basicWin.LiveInline("italic");
                LongLineBench.Idle();
                setIme.Invoke(basicWin.source, new object[] { "나" });
                basicWin.UpdateLayout();
                basicWin.livePreview.View.Redraw(keyRow);
                basicWin.livePreview.View.GetOrConstructVisualLine(keyRow);
                string keyI = basicWin.livePreview.Info(keyRow).ImeKey;

                Check("basic Task5d Highlight/NoBold/NoItalic toggles change info.ImeKey while composing",
                    !string.IsNullOrEmpty(key0) && !string.IsNullOrEmpty(keyH) &&
                    !string.IsNullOrEmpty(keyB) && !string.IsNullOrEmpty(keyI) &&
                    keyH != key0 && keyB != key0 && keyB != keyH &&
                    keyI != key0 && keyI != keyH && keyI != keyB,
                    "k0=[" + key0 + "], kH=[" + keyH + "], kB=[" + keyB + "], kI=[" + keyI + "]");
            } finally {
                SafeCleanupIme(setIme, basicWin.source, null);
            }

            // 5(e): GrowTypingColumn accounts for ImeLead spaces when NOT composing
            basicWin.SetText("| a | b |\n| --- | --- |\n| aaaaaaaaaaaaaaaaaaaaaaaa | b |\n");
            LongLineBench.Idle();
            DocumentLine growRow = basicWin.source.Document.GetLineByNumber(3);
            int growEnd = basicWin.livePreview.Info(growRow).Cells[0].End;
            basicWin.source.Select(growRow.Offset + growEnd, 0);
            LongLineBench.Idle();
            basicWin.UpdateLayout();
            basicWin.livePreview.View.GetOrConstructVisualLine(growRow);
            LongLineBench.Idle();
            double widthBefore = basicWin.livePreview.Info(growRow).Row.Widths[0];

            for (int i = 0; i < 12; i++) basicWin.source.TextArea.PerformTextInput(" ");
            LongLineBench.Idle();
            basicWin.UpdateLayout();
            growRow = basicWin.source.Document.GetLineByNumber(3);
            basicWin.livePreview.View.GetOrConstructVisualLine(growRow);
            LongLineBench.Idle();
            MarkdownLivePreview.LineInfo growAfter = basicWin.livePreview.Info(growRow);
            double widthAfter = growAfter.Row != null && growAfter.Row.Widths != null && growAfter.Row.Widths.Length > 0 ? growAfter.Row.Widths[0] : 0;
            Check("basic Task5e GrowTypingColumn widens column for ImeLead spaces when not composing",
                basicWin.source.ImeCompositionText == null && growAfter.ImeLead >= 10 && widthAfter > widthBefore,
                "imeLead=" + growAfter.ImeLead + ", widthBefore=" + widthBefore + ", widthAfter=" + widthAfter);
        } finally {
            SafeCleanupIme(setIme, basicWin.source, basicOldLayout);
            CloseWindow(basicWin);
        }
    }

    // Task 6(a): Delete paths inside a table cell in Live view (!HideMarkers).
    static void RunStage3Task6aLiveCellDeleteSuite() {
        MainWindow window = CreateWindow(false);
        try {
            // 1. `| **a|** |` + Key.Delete -> `| **a** |` (never `| **a* |`)
            window.SetText("| h |\n| --- |\n| **a** |\n");
            LongLineBench.Idle();
            DocumentLine row = window.source.Document.GetLineByNumber(3);
            string raw = window.source.Document.GetText(row);
            int innerClose = row.Offset + raw.IndexOf("**", raw.IndexOf("**", StringComparison.Ordinal) + 2);
            window.source.Select(innerClose, 0);
            LongLineBench.Idle();
            PressLiveKey(window, Key.Delete);
            string rowDel = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
            Check("live Task6a **a|** + Delete -> | **a** |",
                rowDel == "| **a** |", "row=[" + rowDel + "]");

            // 2. `| **|ab** |` + Ctrl+Back -> balanced (** count even, never `| ab** |`)
            window.SetText("| h |\n| --- |\n| **ab** |\n");
            LongLineBench.Idle();
            row = window.source.Document.GetLineByNumber(3);
            raw = window.source.Document.GetText(row);
            int afterOpen = row.Offset + raw.IndexOf('a');
            window.source.Select(afterOpen, 0);
            LongLineBench.Idle();
            RunCmd(window, EditingCommands.DeletePreviousWord);
            string rowCtrlBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
            Check("live Task6a **|ab** + Ctrl+Back stays balanced",
                CountSubstrings(rowCtrlBack, "**") % 2 == 0 && rowCtrlBack != "| ab** |",
                "row=[" + rowCtrlBack + "]");

            // 3. `| **a**| |` + Ctrl+Delete -> `| **a** |` (canonical trailing space preserved, never `| **a**|`)
            window.SetText("| h |\n| --- |\n| **a** |\n");
            LongLineBench.Idle();
            row = window.source.Document.GetLineByNumber(3);
            int outerEnd = row.Offset + window.livePreview.Info(row).Cells[0].End;
            window.source.Select(outerEnd, 0);
            LongLineBench.Idle();
            RunCmd(window, EditingCommands.DeleteNextWord);
            string rowOuterCtrlDel = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
            Check("live Task6a **a**| + Ctrl+Delete keeps canonical trailing space -> | **a** |",
                rowOuterCtrlDel == "| **a** |", "row=[" + rowOuterCtrlDel + "]");

            // 4. `| **a|** |` + Ctrl+Delete -> balanced (never `| **a |`)
            window.SetText("| h |\n| --- |\n| **a** |\n");
            LongLineBench.Idle();
            row = window.source.Document.GetLineByNumber(3);
            raw = window.source.Document.GetText(row);
            innerClose = row.Offset + raw.IndexOf("**", raw.IndexOf("**", StringComparison.Ordinal) + 2);
            window.source.Select(innerClose, 0);
            LongLineBench.Idle();
            RunCmd(window, EditingCommands.DeleteNextWord);
            string rowInnerCtrlDel = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
            Check("live Task6a **a|** + Ctrl+Delete stays balanced",
                CountSubstrings(rowInnerCtrlDel, "**") % 2 == 0 && rowInnerCtrlDel != "| **a |",
                "row=[" + rowInnerCtrlDel + "]");
        } finally {
            CloseWindow(window);
        }
    }

    // Task 6(b) & 6(c): Cross-cell selection Delete and Table boundary selections (both Basic and Live views).
    static void RunTask6CrossCellAndBoundaryDeleteChecks(MainWindow window, string mode) {
        // 6(b). Cross-cell selection `| **ab** | **cd** |` (`b`..`c`) + Key.Delete -> `| **a** | **d** |`
        // Cut is skipped because probe avoids clipboard.
        window.SetText("| h1 | h2 |\n| --- | --- |\n| **ab** | **cd** |\n");
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        string raw = window.source.Document.GetText(row);
        int startSel = row.Offset + raw.IndexOf('b');
        int endSel = row.Offset + raw.IndexOf('c') + 1;
        window.source.Select(startSel, endSel - startSel);
        LongLineBench.Idle();
        PressLiveKey(window, Key.Delete);
        string crossRow = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " Task6b cross-cell selection b..c Delete -> | **a** | **d** |",
            crossRow == "| **a** | **d** |", "row=[" + crossRow + "]");

        // 6(c)-1. Last row into paragraph below: select `c`..`hello` + Key.Delete
        window.SetText("| h1 | h2 |\n| --- | --- |\n| a | **bc** |\nhello world");
        LongLineBench.Idle();
        DocumentLine l3 = window.source.Document.GetLineByNumber(3);
        DocumentLine l4 = window.source.Document.GetLineByNumber(4);
        string r3 = window.source.Document.GetText(l3);
        int sel3Start = l3.Offset + r3.IndexOf('c');
        int sel4End = l4.Offset + "hello".Length;
        window.source.Select(sel3Start, sel4End - sel3Start);
        LongLineBench.Idle();
        PressLiveKey(window, Key.Delete);
        int linesBelow = window.source.Document.LineCount;
        string afterL3 = linesBelow >= 3 ? window.source.Document.GetText(window.source.Document.GetLineByNumber(3)) : "";
        string afterL4 = linesBelow >= 4 ? window.source.Document.GetText(window.source.Document.GetLineByNumber(4)) : "";
        bool tableBelowKept = linesBelow == 4 &&
            window.livePreview.Info(window.source.Document.GetLineByNumber(1)).Table &&
            window.livePreview.Info(window.source.Document.GetLineByNumber(2)).Delimiter &&
            window.livePreview.Info(window.source.Document.GetLineByNumber(3)).Table;
        Check(mode + " Task6c last row into paragraph below Delete keeps table and paragraph separate",
            tableBelowKept && afterL3 == "| a | **b** |" && afterL4 == " world",
            "lines=" + linesBelow + ", l3=[" + afterL3 + "], l4=[" + afterL4 + "]");

        // 6(c)-2. Paragraph above into header row: select `world`..`a` + Key.Delete
        window.SetText("hello world\n| **ab** | c |\n| --- | --- |\n| x | y |");
        LongLineBench.Idle();
        DocumentLine l1 = window.source.Document.GetLineByNumber(1);
        DocumentLine l2 = window.source.Document.GetLineByNumber(2);
        string r2 = window.source.Document.GetText(l2);
        int sel1Start = l1.Offset + "hello ".Length;
        int sel2End = l2.Offset + r2.IndexOf('a') + 1;
        window.source.Select(sel1Start, sel2End - sel1Start);
        LongLineBench.Idle();
        PressLiveKey(window, Key.Delete);
        int linesAbove = window.source.Document.LineCount;
        string afterL1 = linesAbove >= 1 ? window.source.Document.GetText(window.source.Document.GetLineByNumber(1)) : "";
        string afterL2 = linesAbove >= 2 ? window.source.Document.GetText(window.source.Document.GetLineByNumber(2)) : "";
        bool tableAboveKept = linesAbove == 4 &&
            window.livePreview.Info(window.source.Document.GetLineByNumber(2)).Header &&
            window.livePreview.Info(window.source.Document.GetLineByNumber(3)).Delimiter &&
            window.livePreview.Info(window.source.Document.GetLineByNumber(4)).Table;
        Check(mode + " Task6c paragraph above into header row Delete keeps table and paragraph separate",
            tableAboveKept && afterL1 == "hello " && afterL2 == "| **b** | c |",
            "lines=" + linesAbove + ", l1=[" + afterL1 + "], l2=[" + afterL2 + "]");
    }

    // In-memory command checks for behaviors 1 & 2 (no clipboard calls).
    static void RunInMemoryPasteAndEnterChecks() {
        var doc1 = new TextDocument("| h1 | h2 |\n| --- | --- |\n|  | b |\n");
        var row1 = doc1.GetLineByNumber(3);
        var cells1 = MarkdownLivePreview.SplitCells(doc1.GetText(row1));
        CommandResult pasteEmpty = MarkdownTableCommands.PasteInCell(doc1, row1.Offset + 2, 0, "x", cells1);
        ApplyToDoc(doc1, pasteEmpty);
        Check("inmem B1 PasteInCell at empty stop -> | x |",
            doc1.GetText(doc1.GetLineByNumber(3)) == "| x | b |",
            "row=[" + doc1.GetText(doc1.GetLineByNumber(3)) + "]");

        var doc1b = new TextDocument("| h1 | h2 |\n| --- | --- |\n|  | b |\n");
        var row1b = doc1b.GetLineByNumber(3);
        var cells1b = MarkdownLivePreview.SplitCells(doc1b.GetText(row1b));
        CommandResult pasteEmptyPipe = MarkdownTableCommands.PasteInCell(doc1b, row1b.Offset + 3, 0, "x", cells1b);
        ApplyToDoc(doc1b, pasteEmptyPipe);
        Check("inmem B1 PasteInCell at closing pipe edge -> | x |",
            doc1b.GetText(doc1b.GetLineByNumber(3)) == "| x | b |",
            "row=[" + doc1b.GetText(doc1b.GetLineByNumber(3)) + "]");

        var doc2 = new TextDocument("| h1 | h2 |\n| --- | --- |\n| **a**  | b |\n");
        var row2 = doc2.GetLineByNumber(3);
        var cells2 = MarkdownLivePreview.SplitCells(doc2.GetText(row2));
        int caretAfterSpace = row2.Offset + cells2[0].End + 1;
        CommandResult pasteAfterSpace = MarkdownTableCommands.PasteInCell(doc2, caretAfterSpace, 0, "b", cells2);
        ApplyToDoc(doc2, pasteAfterSpace);
        Check("inmem B2 PasteInCell after typed space -> | **a** b |",
            doc2.GetText(doc2.GetLineByNumber(3)) == "| **a** b | b |",
            "row=[" + doc2.GetText(doc2.GetLineByNumber(3)) + "]");
    }

    static void ApplyToDoc(TextDocument doc, CommandResult result) {
        if (result == null) return;
        foreach (SourceEdit edit in result.Edits) doc.Replace(edit.Offset, edit.Length, edit.Text);
    }

    static int CountUnescapedPipes(string text) {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0;
        bool escaped = false;
        for (int i = 0; i < text.Length; i++) {
            char ch = text[i];
            if (escaped) { escaped = false; continue; }
            if (ch == '\\') { escaped = true; continue; }
            if (ch == '|') count++;
        }
        return count;
    }

    static void RunEmptyCellPasteAndPipeChecks(MainWindow window, string mode) {
        CheckEmptyCellOffsetRange(window, mode, "empty-both", "| h1 | h2 |\n| --- | --- |\n|  |  |\n", 1, 6, true);
        CheckEmptyCellOffsetRange(window, mode, "ga-empty", "| h1 | h2 |\n| --- | --- |\n| 가 |  |\n", 3, 8, true);
        CheckEmptyCellOffsetRange(window, mode, "da-empty", "| h1 | h2 |\n| --- | --- |\n| 다 |  |\n", 5, 7, true);
        CheckEmptyCellOffsetRange(window, mode, "unclosed-empty", "| h1 | h2 | h3 |\n| --- | --- | --- |\n|   |   |   \n", 1, 12, false);
    }

    static void CheckEmptyCellOffsetRange(MainWindow window, string mode, string label, string fixture, int minRel, int maxRel, bool closedRow) {
        CheckEmptyCellAction(window, mode, label, "paste x", fixture, minRel, maxRel, closedRow, "x", false);
        CheckEmptyCellAction(window, mode, label, "paste tab+pipe", fixture, minRel, maxRel, closedRow, "a\tb | c", false);
        CheckEmptyCellAction(window, mode, label, "paste pipes", fixture, minRel, maxRel, closedRow, "가 | 나 | 다", false);
        CheckEmptyCellAction(window, mode, label, "type pipe", fixture, minRel, maxRel, closedRow, "|", true);
    }

    static void CheckEmptyCellAction(MainWindow window, string mode, string label, string actionName, string fixture, int minRel, int maxRel, bool closedRow, string payload, bool isType) {
        string failure = null;
        for (int rel = minRel; rel <= maxRel; rel++) {
            window.SetText(fixture);
            LongLineBench.Idle();
            int initialLines = window.source.Document.LineCount;
            DocumentLine row = window.source.Document.GetLineByNumber(3);
            string initialRow = window.source.Document.GetText(row);
            if (rel > initialRow.Length) break;
            int initialPipes = CountUnescapedPipes(initialRow);
            PutCaret(window, row.Offset + rel);
            try {
                if (isType) {
                    window.source.TextArea.PerformTextInput(payload);
                } else {
                    window.LivePaste(new System.Windows.DataObject(), true, payload);
                }
                LongLineBench.Idle();
            } catch (Exception ex) {
                failure = "rel=" + rel + " threw " + ex.GetType().Name + ": " + ex.Message;
                break;
            }
            int afterLines = window.source.Document.LineCount;
            string afterRow = afterLines >= 3 ? window.source.Document.GetText(window.source.Document.GetLineByNumber(3)) : "";
            if (afterLines != initialLines) {
                failure = "rel=" + rel + " lines=" + afterLines + "/" + initialLines + " row=[" + FlatText(afterRow) + "]";
                break;
            }
            bool insideCell = !closedRow || rel < initialRow.Length;
            if (isType) {
                if (insideCell && (CountUnescapedPipes(afterRow) != initialPipes || afterRow.IndexOf("\\|", StringComparison.Ordinal) < 0)) {
                    failure = "rel=" + rel + " pipes=" + CountUnescapedPipes(afterRow) + "/" + initialPipes + " row=[" + FlatText(afterRow) + "]";
                    break;
                }
            } else if (payload == "x") {
                if (CountUnescapedPipes(afterRow) != initialPipes || afterRow.IndexOf("x", StringComparison.Ordinal) < 0) {
                    failure = "rel=" + rel + " pipes=" + CountUnescapedPipes(afterRow) + "/" + initialPipes + " row=[" + FlatText(afterRow) + "]";
                    break;
                }
            } else {
                if (insideCell && (CountUnescapedPipes(afterRow) != initialPipes || afterRow.IndexOf("\\|", StringComparison.Ordinal) < 0)) {
                    failure = "rel=" + rel + " pipes=" + CountUnescapedPipes(afterRow) + "/" + initialPipes + " row=[" + FlatText(afterRow) + "]";
                    break;
                }
            }
        }
        Check(mode + " EmptyCell " + label + " " + actionName, failure == null, failure ?? "");
    }

    static void TypeChars(MainWindow window, string text) {
        for (int i = 0; i < text.Length; i++) {
            window.source.TextArea.PerformTextInput(text[i].ToString());
        }
        LongLineBench.Idle();
    }

    static void RunCloseSpaceBackspaceChecks(MainWindow window, string mode) {
        // 1. Empty last cell
        window.SetText("| h1 | h2 |\n| --- | --- |\n| x |  |\n");
        LongLineBench.Idle();
        DocumentLine row = window.source.Document.GetLineByNumber(3);
        PutCaret(window, row.Offset + 7);
        window.LiveInline("bold");
        LongLineBench.Idle();
        TypeChars(window, "abc abc");
        string emptyAfterType = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " GroupC empty last cell bold type",
            emptyAfterType == "| x | **abc abc** |", "row=[" + emptyAfterType + "]");
        for (int i = 0; i < 5; i++) PressBackspace(window);
        string emptyAfterBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " GroupC empty last cell bold 5x backspace",
            emptyAfterBack == "| x | **ab** |", "row=[" + emptyAfterBack + "]");

        // 2. Text-cell end
        window.SetText("| h1 | h2 |\n| --- | --- |\n| x | note |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        var info = window.livePreview.Info(row);
        PutCaret(window, row.Offset + info.Cells[1].End);
        window.LiveInline("bold");
        LongLineBench.Idle();
        TypeChars(window, "abc abc");
        for (int i = 0; i < 5; i++) PressBackspace(window);
        string textEndAfterBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " GroupC text-cell end bold 5x backspace",
            textEndAfterBack == "| x | note**ab** |", "row=[" + textEndAfterBack + "]");

        // 3. After <br> in cell
        window.SetText("| h1 | h2 |\n| --- | --- |\n| x | 1<br> |\n");
        LongLineBench.Idle();
        row = window.source.Document.GetLineByNumber(3);
        info = window.livePreview.Info(row);
        PutCaret(window, row.Offset + info.Cells[1].End);
        window.LiveInline("bold");
        LongLineBench.Idle();
        TypeChars(window, "abc abc");
        for (int i = 0; i < 5; i++) PressBackspace(window);
        string brAfterBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " GroupC after br in cell bold 5x backspace",
            brAfterBack == "| x | 1<br>**ab** |", "row=[" + brAfterBack + "]");

        // 4. Body text parity
        window.SetText("\n");
        LongLineBench.Idle();
        PutCaret(window, 0);
        window.LiveInline("bold");
        LongLineBench.Idle();
        TypeChars(window, "abc abc");
        for (int i = 0; i < 5; i++) PressBackspace(window);
        string bodyAfterBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " GroupC body text parity bold 5x backspace",
            bodyAfterBack == "**ab**", "line1=[" + bodyAfterBack + "]");
    }

    static void RunOverflowRowShiftTabChecks(MainWindow window, string mode) {
        string fixture =
            "| 가 | <br> |\n" +
            "| --- | --- |\n" +
            "|  |  |\n" +
            "|   | 제목3 |\n" +
            "|*日本語<br>|---|---|\n" +
            "| <br>***- 목록 항목<br>- 두 번째 항목<br>   |  |  |\n";
        window.SetText(fixture);
        LongLineBench.Idle();

        DocumentLine line5 = window.source.Document.GetLineByNumber(5);
        DocumentLine line6 = window.source.Document.GetLineByNumber(6);
        PutCaret(window, line6.Offset + 35);
        bool handled1 = window.SourceTabPathProbe(true);
        LongLineBench.Idle();
        if (window.livePreview != null && window.livePreview.View != null) {
            window.livePreview.View.GetOrConstructVisualLine(line5);
        }
        window.UpdateLayout();
        LongLineBench.Idle();

        bool visualColOk = true;
        string visualColErr = "";
        int visualCol = -1;
        try {
            visualCol = window.source.TextArea.Caret.VisualColumn;
        } catch (Exception ex) {
            visualColOk = false;
            visualColErr = ex.Message;
        }

        int caret1 = window.source.CaretOffset;
        DocumentLine caretLine1 = window.source.Document.GetLineByOffset(caret1);
        int caretRel1 = caret1 - caretLine1.Offset;
        Check(mode + " GroupD overflow row Shift-Tab from line 6 rel 35",
            handled1 && visualColOk && caretLine1.LineNumber == 5 && caretRel1 >= 1 && caretRel1 <= 17,
            "handled=" + handled1 + " visualColOk=" + visualColOk + " visualCol=" + visualCol +
            " visualColErr=" + visualColErr + " line=" + caretLine1.LineNumber + " rel=" + caretRel1);

        // Shift-Tab from line 6 empty 2nd cell (rel 37 and rel 38) stays on line 6 in cell 0 inside its pipes.
        int[] secondCellRels = new int[] { 37, 38 };
        for (int i = 0; i < secondCellRels.Length; i++) {
            int startRel = secondCellRels[i];
            PutCaret(window, line6.Offset + startRel);
            bool handled2 = window.SourceTabPathProbe(true);
            LongLineBench.Idle();
            int caret2 = window.source.CaretOffset;
            DocumentLine caretLine2 = window.source.Document.GetLineByOffset(caret2);
            int caretRel2 = caret2 - caretLine2.Offset;
            Check(mode + " GroupD Shift-Tab from line 6 empty 2nd cell rel " + startRel,
                handled2 && caretLine2.LineNumber == 6 && caretRel2 >= 1 && caretRel2 <= 36,
                "handled=" + handled2 + " line=" + caretLine2.LineNumber + " rel=" + caretRel2);
        }
    }

    static void RunReviewFindingsChecks(MainWindow window, string mode) {
        // Finding 1a: typing '\' before column pipe doubles it so '|' stays a column separator.
        window.SetText("|a|b|\n|---|---|\n");
        LongLineBench.Idle();
        PutCaret(window, 2);
        window.source.TextArea.PerformTextInput("\\");
        LongLineBench.Idle();
        string f1a = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding1 typing backslash before column pipe -> |a\\\\|b|",
            f1a == "|a\\\\|b|" && CountUnescapedPipes(f1a) == 3,
            "row1=[" + f1a + "], pipes=" + CountUnescapedPipes(f1a));

        // Finding 1b: typing '\' right before '\' of '\|' doubles it so '|' remains escaped.
        window.SetText("| a\\|b | c |\n|---|---|\n");
        LongLineBench.Idle();
        PutCaret(window, 3);
        window.source.TextArea.PerformTextInput("\\");
        LongLineBench.Idle();
        string f1b = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding1 typing backslash before escaped pipe -> | a\\\\\\|b | c |",
            f1b == "| a\\\\\\|b | c |" && CountUnescapedPipes(f1b) == 3,
            "row1=[" + f1b + "], pipes=" + CountUnescapedPipes(f1b));

        // Finding 1c: typing '\' inside code span (not before '|') in a valid GFM table is NOT doubled.
        window.SetText("| `ab` | c |\n|---|---|\n");
        LongLineBench.Idle();
        PutCaret(window, 4);
        window.source.TextArea.PerformTextInput("\\");
        LongLineBench.Idle();
        string f1c = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding1 typing backslash inside code span is not doubled",
            f1c == "| `a\\b` | c |",
            "row1=[" + f1c + "]");

        // Finding 2a: Delete on 2nd '\' of '\\|' removes both backslashes so '|' stays a column pipe.
        window.SetText("| a\\\\| b |\n|---|---|\n");
        LongLineBench.Idle();
        PutCaret(window, 4);
        PressDelete(window);
        string f2a = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding2 Delete on 2nd backslash of \\\\| -> | a| b |",
            f2a == "| a| b |" && CountUnescapedPipes(f2a) == 3,
            "row1=[" + f2a + "], pipes=" + CountUnescapedPipes(f2a));

        // Finding 2b: Backspace after both '\' of '\\|' removes both backslashes.
        window.SetText("| a\\\\| b |\n|---|---|\n");
        LongLineBench.Idle();
        PutCaret(window, 5);
        PressBackspace(window);
        string f2b = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding2 Backspace on 2nd backslash of \\\\| -> | a| b |",
            f2b == "| a| b |" && CountUnescapedPipes(f2b) == 3,
            "row1=[" + f2b + "], pipes=" + CountUnescapedPipes(f2b));

        // Finding 2c: Backspace after 2nd '\' of '\\\|' removes pair so '\|' remains escaped.
        window.SetText("| a\\\\\\|b | c |\n|---|---|\n");
        LongLineBench.Idle();
        PutCaret(window, 5);
        PressBackspace(window);
        string f2c = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding2 Backspace after 2nd backslash of \\\\\\| -> | a\\|b | c |",
            f2c == "| a\\|b | c |" && CountUnescapedPipes(f2c) == 3,
            "row1=[" + f2c + "], pipes=" + CountUnescapedPipes(f2c));

        // Finding 3a: Backspace on only space between '\' and closing pipe is a no-op.
        window.SetText("| a\\ | b |\n|---|---|\n");
        LongLineBench.Idle();
        PutCaret(window, 5);
        PressBackspace(window);
        string f3a = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding3 Backspace on only space between backslash and pipe keeps | a\\ | b |",
            f3a == "| a\\ | b |" && CountUnescapedPipes(f3a) == 3,
            "row1=[" + f3a + "], pipes=" + CountUnescapedPipes(f3a));

        // Finding 3b: two spaces between '\' and pipe -> 1st Backspace removes one, 2nd keeps the last space.
        window.SetText("| a\\  | b |\n|---|---|\n");
        LongLineBench.Idle();
        PutCaret(window, 6);
        PressBackspace(window);
        string f3b1 = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        PressBackspace(window);
        string f3b2 = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding3 Backspace from two spaces after backslash stops at one space",
            f3b1 == "| a\\ | b |" && f3b2 == "| a\\ | b |",
            "after1=[" + f3b1 + "], after2=[" + f3b2 + "]");

        // Finding 4: typing '|' over multi-cell selection after '\' keeps odd backslash run before '|'.
        window.SetText("| a\\x | b |\n|---|---|\n");
        LongLineBench.Idle();
        window.source.Select(4, 5);
        LongLineBench.Idle();
        window.source.TextArea.PerformTextInput("|");
        LongLineBench.Idle();
        string f4 = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " Finding4 typing pipe over multi-cell selection after backslash escapes pipe",
            CountUnescapedPipes(f4) == 3 && (f4.IndexOf("a\\|", StringComparison.Ordinal) >= 0 || f4.IndexOf("a\\\\\\|", StringComparison.Ordinal) >= 0),
            "row1=[" + f4 + "], pipes=" + CountUnescapedPipes(f4));

        // Finding 5: overflow row handling in basic vs live mode.
        window.SetText("| a | b |\n| - | - |\n| c | d | e |\n");
        LongLineBench.Idle();
        DocumentLine row3 = window.source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info3 = window.livePreview.Info(row3);
        if (mode == "basic") {
            PutCaret(window, row3.Offset + info3.Cells[1].Start);
            RunCmd(window, EditingCommands.MoveToLineEnd);
            window.source.TextArea.PerformTextInput("x");
            LongLineBench.Idle();
            string f5End = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
            Check(mode + " Finding5 End in overflow row lands in drawn column d -> | c | dx | e |",
                f5End == "| c | dx | e |",
                "row3=[" + f5End + "]");

            window.SetText("| a | b |\n| - | - |\n| c | d | e |\n");
            LongLineBench.Idle();
            row3 = window.source.Document.GetLineByNumber(3);
            info3 = window.livePreview.Info(row3);
            PutCaret(window, row3.Offset + info3.Cells[1].End);
            int beforeLines = window.source.Document.LineCount;
            bool tabAppended = window.SourceTabPathProbe(false);
            LongLineBench.Idle();
            int tabLine = window.source.Document.GetLineByOffset(window.source.CaretOffset).LineNumber;
            Check(mode + " Finding5 Tab from last drawn cell d appends row 4",
                tabAppended && window.source.Document.LineCount == beforeLines + 1 && tabLine == 4 &&
                window.source.Document.GetText(window.source.Document.GetLineByNumber(4)).TrimStart().StartsWith("|", StringComparison.Ordinal),
                "handled=" + tabAppended + ", lines=" + window.source.Document.LineCount + ", caretLine=" + tabLine);

            window.SetText("| a | b |\n| - | - |\n| c | d | e |\n");
            LongLineBench.Idle();
            row3 = window.source.Document.GetLineByNumber(3);
            info3 = window.livePreview.Info(row3);
            window.UpdateLayout();
            VisualLine vl3 = window.livePreview.View.GetOrConstructVisualLine(row3);
            LongLineBench.Idle();
            Rect box1 = window.livePreview.TableCellBox(vl3, 1);
            Point rightEdgePt = new Point(box1.Right + 20, box1.Top + box1.Height * 0.5);
            int hitOffset = -1;
            bool gotHit = !box1.IsEmpty && window.livePreview.TableOffsetAtPoint(rightEdgePt, out hitOffset);
            Check(mode + " Finding5 TableOffsetAtPoint on row 3 right edge clamps to <= d.End",
                gotHit && hitOffset <= row3.Offset + info3.Cells[1].End,
                "gotHit=" + gotHit + ", hit=" + hitOffset + ", dEnd=" + (row3.Offset + info3.Cells[1].End) + ", box1=" + box1);
        } else {
            PutCaret(window, row3.Offset + info3.Cells[1].End);
            int liveBeforeLines = window.source.Document.LineCount;
            bool tabMoved = window.SourceTabPathProbe(false);
            LongLineBench.Idle();
            Check(mode + " Finding5 live Tab from cell d moves to drawn cell e",
                tabMoved && window.source.Document.LineCount == liveBeforeLines &&
                window.source.CaretOffset >= row3.Offset + info3.Cells[2].Start &&
                window.source.CaretOffset <= row3.Offset + info3.Cells[2].End,
                "handled=" + tabMoved + ", lines=" + window.source.Document.LineCount + ", caret=" + window.source.CaretOffset);
        }

        // Finding 7: PasteInCell backslash count at clamped offset past trailing pipe.
        var doc7 = new TextDocument("| h |\n| --- |\n| a\\ |\n");
        var row7 = doc7.GetLineByNumber(3);
        var cells7 = MarkdownLivePreview.SplitCells(doc7.GetText(row7));
        CommandResult paste7 = MarkdownTableCommands.PasteInCell(doc7, row7.Offset + row7.Length, 0, "|", cells7);
        ApplyToDoc(doc7, paste7);
        string row7After = doc7.GetText(doc7.GetLineByNumber(3));
        Check(mode + " Finding7 PasteInCell at clamped offset after backslash escapes pipe",
            row7After == "| a\\| |" && CountUnescapedPipes(row7After) == 2,
            "row3=[" + row7After + "], pipes=" + CountUnescapedPipes(row7After));
    }

    static void CheckEmptyCellKey(MainWindow window, string mode, string caseName, string fixture, int startCell, int expectedCell, int keyKind) {
        window.SetText(fixture);
        LongLineBench.Idle();
        int initialLines = window.source.Document.LineCount;
        DocumentLine row3 = window.source.Document.GetLineByNumber(3);
        string initialRow = window.source.Document.GetText(row3);
        int initialPipes = CountUnescapedPipes(initialRow);
        int undoBefore = window.History.UndoDepth;
        int redoBefore = window.History.RedoDepth;
        MarkdownLivePreview.LineInfo info3 = window.livePreview.Info(row3);
        int startOffset = row3.Offset + MarkdownLivePreview.CellCaretStart(info3.Cells[startCell]);
        int expectedOffset = keyKind >= 2
            ? startOffset
            : (expectedCell < startCell
                ? row3.Offset + MarkdownLivePreview.CellEdge(info3.Cells[expectedCell], false)
                : startOffset);
        PutCaret(window, startOffset);
        if (keyKind == 0) PressBackspace(window);
        else if (keyKind == 1) RunCmd(window, EditingCommands.DeletePreviousWord);
        else if (keyKind == 2) PressDelete(window);
        else RunCmd(window, EditingCommands.DeleteNextWord);
        int afterLines = window.source.Document.LineCount;
        string afterRow = afterLines >= 3 ? window.source.Document.GetText(window.source.Document.GetLineByNumber(3)) : "";
        int afterCaret = window.source.CaretOffset;
        int afterPipes = CountUnescapedPipes(afterRow);
        int undoAfter = window.History.UndoDepth;
        int redoAfter = window.History.RedoDepth;
        bool ok = afterLines == initialLines &&
            afterRow == initialRow &&
            afterPipes == initialPipes &&
            afterCaret == expectedOffset &&
            undoAfter == undoBefore &&
            redoAfter == redoBefore;
        Check(mode + " " + caseName, ok,
            "caret=" + afterCaret + "/" + expectedOffset +
            ", lines=" + afterLines + "/" + initialLines +
            ", pipes=" + afterPipes + "/" + initialPipes +
            ", undo=" + undoAfter + "/" + undoBefore +
            ", redo=" + redoAfter + "/" + redoBefore +
            ", row=[" + FlatText(afterRow) + "]");
    }

    static void RunEmptyCellBackspaceChecks(MainWindow window, string mode) {
        string midTwoSpaces = "| h1 | h2 | h3 |\n| - | - | - |\n| aa |  | cc |\n";
        string midOneSpace = "| h1 | h2 | h3 |\n| - | - | - |\n| aa | | cc |\n";
        string midNoSpace = "| h1 | h2 | h3 |\n| - | - | - |\n| aa || cc |\n";
        string prevAlsoEmpty = "| h1 | h2 | h3 |\n| - | - | - |\n|  |  | cc |\n";
        string firstEmpty = "| h1 | h2 |\n| - | - |\n|  | bb |\n";

        // Middle empty cell -> previous cell end (all three empty forms)
        CheckEmptyCellKey(window, mode, "Empty cell Backspace |  | jumps to previous cell end", midTwoSpaces, 1, 0, 0);
        CheckEmptyCellKey(window, mode, "Empty cell Backspace | | jumps to previous cell end", midOneSpace, 1, 0, 0);
        CheckEmptyCellKey(window, mode, "Empty cell Backspace || jumps to previous cell end", midNoSpace, 1, 0, 0);

        // Previous cell also empty
        CheckEmptyCellKey(window, mode, "Empty cell Backspace |  |  | jumps to cell 0 empty stop", prevAlsoEmpty, 1, 0, 0);

        // Consecutive empty cells | aa ||| -> 1st Backspace to cell 1, 2nd Backspace to cell 0 end
        string consecutiveNoSpace = "| h1 | h2 | h3 |\n| - | - | - |\n| aa |||\n";
        window.SetText(consecutiveNoSpace);
        LongLineBench.Idle();
        int consecInitialLines = window.source.Document.LineCount;
        DocumentLine consecRow = window.source.Document.GetLineByNumber(3);
        string consecInitialRow = window.source.Document.GetText(consecRow);
        int consecInitialPipes = CountUnescapedPipes(consecInitialRow);
        int consecUndoBefore = window.History.UndoDepth;
        int consecRedoBefore = window.History.RedoDepth;
        MarkdownLivePreview.LineInfo consecInfo = window.livePreview.Info(consecRow);
        int cell2Start = consecRow.Offset + MarkdownLivePreview.CellCaretStart(consecInfo.Cells[2]);
        int cell1End = consecRow.Offset + MarkdownLivePreview.CellEdge(consecInfo.Cells[1], false);
        int cell0End = consecRow.Offset + MarkdownLivePreview.CellEdge(consecInfo.Cells[0], false);
        PutCaret(window, cell2Start);
        PressBackspace(window);
        int caretAfter1 = window.source.CaretOffset;
        PressBackspace(window);
        int caretAfter2 = window.source.CaretOffset;
        int consecAfterLines = window.source.Document.LineCount;
        string consecAfterRow = consecAfterLines >= 3 ? window.source.Document.GetText(window.source.Document.GetLineByNumber(3)) : "";
        Check(mode + " Empty cell Backspace | aa ||| two Backspaces step cell 2 -> cell 1 -> cell 0 end",
            caretAfter1 == cell1End &&
            caretAfter2 == cell0End &&
            consecAfterLines == consecInitialLines &&
            consecAfterRow == consecInitialRow &&
            CountUnescapedPipes(consecAfterRow) == consecInitialPipes &&
            window.History.UndoDepth == consecUndoBefore &&
            window.History.RedoDepth == consecRedoBefore,
            "caret1=" + caretAfter1 + "/" + cell1End + ", caret2=" + caretAfter2 + "/" + cell0End +
            ", undo=" + window.History.UndoDepth + "/" + consecUndoBefore + ", row=[" + FlatText(consecAfterRow) + "]");

        // First-cell empty stays unchanged
        CheckEmptyCellKey(window, mode, "Empty cell Backspace first cell stays unchanged", firstEmpty, 0, 0, 0);
        CheckEmptyCellKey(window, mode, "Empty cell Backspace Ctrl+Backspace first cell stays unchanged", firstEmpty, 0, 0, 1);

        // Overflow-column empty cell (live vs basic view clamping)
        if (mode == "live") {
            string overflowLive = "| a | b |\n| - | - |\n| c | d |  |\n";
            CheckEmptyCellKey(window, mode, "Empty cell Backspace overflow col 2 jumps to cell 1 end", overflowLive, 2, 1, 0);
            CheckEmptyCellKey(window, mode, "Empty cell Backspace Ctrl+Backspace overflow col 2 jumps to cell 1 end", overflowLive, 2, 1, 1);
        } else {
            string overflowBasicDrawn = "| a | b |\n| - | - |\n| c |  |  |\n";
            CheckEmptyCellKey(window, mode, "Empty cell Backspace overflow drawn cell 1 jumps to cell 0 end", overflowBasicDrawn, 1, 0, 0);
            CheckEmptyCellKey(window, mode, "Empty cell Backspace Ctrl+Backspace overflow drawn cell 1 jumps to cell 0 end", overflowBasicDrawn, 1, 0, 1);

            window.SetText("| a | b |\n| - | - |\n| c | d |  |\n");
            LongLineBench.Idle();
            DocumentLine row3 = window.source.Document.GetLineByNumber(3);
            MarkdownLivePreview.LineInfo info3 = window.livePreview.Info(row3);
            int overflowCaret = row3.Offset + MarkdownLivePreview.CellCaretStart(info3.Cells[2]);
            int expectedOverflowEnd = row3.Offset + MarkdownLivePreview.CellEdge(info3.Cells[1], false);
            CommandResult cmdRes = MarkdownTableCommands.BackspaceInCell(window.source.Document, overflowCaret, info3.Cells);
            Check(mode + " Empty cell Backspace BackspaceInCell overflow col 2 returns caret to cell 1 end",
                cmdRes != null && cmdRes.Edits.Count == 0 && cmdRes.SelectionStart == expectedOverflowEnd,
                "null=" + (cmdRes == null) +
                ", edits=" + (cmdRes == null ? -1 : cmdRes.Edits.Count) +
                ", sel=" + (cmdRes == null ? -1 : cmdRes.SelectionStart) + "/" + expectedOverflowEnd);
        }

        // Ctrl+Backspace in middle empty cell and previous-cell-also-empty
        CheckEmptyCellKey(window, mode, "Empty cell Backspace Ctrl+Backspace |  | jumps to previous cell end", midTwoSpaces, 1, 0, 1);
        CheckEmptyCellKey(window, mode, "Empty cell Backspace Ctrl+Backspace | | jumps to previous cell end", midOneSpace, 1, 0, 1);
        CheckEmptyCellKey(window, mode, "Empty cell Backspace Ctrl+Backspace || jumps to previous cell end", midNoSpace, 1, 0, 1);
        CheckEmptyCellKey(window, mode, "Empty cell Backspace Ctrl+Backspace |  |  | jumps to cell 0 empty stop", prevAlsoEmpty, 1, 0, 1);

        // Delete / Ctrl+Delete unchanged
        CheckEmptyCellKey(window, mode, "Empty cell Backspace Delete stays unchanged", midTwoSpaces, 1, 1, 2);
        CheckEmptyCellKey(window, mode, "Empty cell Backspace Ctrl+Delete stays unchanged", midTwoSpaces, 1, 1, 3);
    }

    static bool HasHiddenAt(MarkdownLivePreview.LineInfo info, int relOffset) {
        if (info == null || info.Replacements == null) return false;
        for (int i = 0; i < info.Replacements.Count; i++) {
            MarkdownLivePreview.Replacement r = info.Replacements[i];
            if (r.Kind == MarkdownLivePreview.ReplacementKind.Hidden &&
                relOffset >= r.Start && relOffset < r.Start + r.Length) {
                return true;
            }
        }
        return false;
    }

    static bool HasCodeSpanCrossing(MarkdownLivePreview.LineInfo info, int relOffset) {
        if (info == null || info.Styles == null) return false;
        for (int i = 0; i < info.Styles.Count; i++) {
            MarkdownLivePreview.StyledPart part = info.Styles[i];
            if ((part.Style & MarkdownLivePreview.Style.Code) != 0 &&
                part.Start <= relOffset && part.Start + part.Length > relOffset) {
                return true;
            }
        }
        return false;
    }

    static void RunGfmPipeChecks(MainWindow window, string mode) {
        // (a) Type '|' inside a code span in a cell -> '\|' saved, '|' shown (off-caret/Basic), cell count same, 1 undo step.
        window.SetText("| `ab` | c |\n| --- | --- |\n| 1 | 2 |\n");
        LongLineBench.Idle();
        DocumentLine row1 = window.source.Document.GetLineByNumber(1);
        int undoBefore = window.History.UndoDepth;
        PutCaret(window, row1.Offset + 4);
        window.source.TextArea.PerformTextInput("|");
        LongLineBench.Idle();
        row1 = window.source.Document.GetLineByNumber(1);
        string aRow1 = window.source.Document.GetText(row1);
        int aCaret = window.source.CaretOffset;
        int undoAfterType = window.History.UndoDepth;
        Check(mode + " GFM pipe type | inside code span -> \\| saved, 2 cells, caret after \\|, 1 undo step",
            aRow1 == "| `a\\|b` | c |" &&
            MarkdownLivePreview.SplitCells(aRow1).Count == 2 &&
            CountUnescapedPipes(aRow1) == 3 &&
            aCaret == row1.Offset + 6 &&
            undoAfterType == undoBefore + 1,
            "row1=[" + aRow1 + "], caret=" + aCaret + "/" + (row1.Offset + 6) + ", undo=" + undoAfterType + "/" + (undoBefore + 1));

        if (mode == "live") {
            PutCaret(window, window.source.Document.GetLineByNumber(3).Offset + 2);
        }
        bool aHidden = HasHiddenAt(window.livePreview.Info(window.source.Document.GetLineByNumber(1)), 4);
        Check(mode + " GFM pipe type | inside code span hides \\ of \\|",
            aHidden, "hiddenAt4=" + aHidden);

        RunCmd(window, ApplicationCommands.Undo);
        string aUndoRow1 = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " GFM pipe type | inside code span single Undo restores | `ab` | c |",
            aUndoRow1 == "| `ab` | c |" && window.History.UndoDepth == undoBefore,
            "row1=[" + aUndoRow1 + "], undo=" + window.History.UndoDepth + "/" + undoBefore);

        // (b) Backtick typed/deleted next to a pipe in another cell -> cell count same, no cross-pipe code span.
        window.SetText("| a | b |\n| --- | --- |\n| c | d |\n");
        LongLineBench.Idle();
        row1 = window.source.Document.GetLineByNumber(1);
        PutCaret(window, row1.Offset + 3);
        window.source.TextArea.PerformTextInput("`");
        LongLineBench.Idle();
        string bRow1Left = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
        Check(mode + " GFM pipe type backtick in cell 0 keeps 2 cells",
            MarkdownLivePreview.SplitCells(bRow1Left).Count == 2 && CountUnescapedPipes(bRow1Left) == 3,
            "row1=[" + bRow1Left + "], pipes=" + CountUnescapedPipes(bRow1Left));

        window.SetText("| `a | b |\n| --- | --- |\n| c | d |\n");
        LongLineBench.Idle();
        row1 = window.source.Document.GetLineByNumber(1);
        PutCaret(window, row1.Offset + 8);
        window.source.TextArea.PerformTextInput("`");
        LongLineBench.Idle();
        if (mode == "live") {
            PutCaret(window, window.source.Document.GetLineByNumber(3).Offset + 2);
        }
        row1 = window.source.Document.GetLineByNumber(1);
        string bRow1Right = window.source.Document.GetText(row1);
        int sepPipeIdx = bRow1Right.IndexOf('|', 1);
        MarkdownLivePreview.LineInfo bInfoRight = window.livePreview.Info(row1);
        bool bCrossPipe = sepPipeIdx >= 0 && HasCodeSpanCrossing(bInfoRight, sepPipeIdx);
        Check(mode + " GFM pipe type backtick in cell 1 keeps 2 cells and no cross-pipe code span",
            MarkdownLivePreview.SplitCells(bRow1Right).Count == 2 && CountUnescapedPipes(bRow1Right) == 3 && !bCrossPipe,
            "row1=[" + bRow1Right + "], crossPipe=" + bCrossPipe);

        window.SetText("| `a` | `b` |\n| --- | --- |\n| c | d |\n");
        LongLineBench.Idle();
        row1 = window.source.Document.GetLineByNumber(1);
        PutCaret(window, row1.Offset + 5);
        PressBackspace(window);
        if (mode == "live") {
            PutCaret(window, window.source.Document.GetLineByNumber(3).Offset + 2);
        }
        row1 = window.source.Document.GetLineByNumber(1);
        string bRow1Del = window.source.Document.GetText(row1);
        int delSepPipe = bRow1Del.IndexOf('|', 1);
        bool bDelCross = delSepPipe >= 0 && HasCodeSpanCrossing(window.livePreview.Info(row1), delSepPipe);
        Check(mode + " GFM pipe delete backtick next to pipe keeps 2 cells",
            MarkdownLivePreview.SplitCells(bRow1Del).Count == 2 && CountUnescapedPipes(bRow1Del) == 3 && !bDelCross,
            "row1=[" + bRow1Del + "], crossPipe=" + bDelCross);

        // (c) Paste "`a | b`" into a cell -> "`a \| b`" (no clipboard).
        window.SetText("| x | y |\n| --- | --- |\n|  | z |\n");
        LongLineBench.Idle();
        DocumentLine row3 = window.source.Document.GetLineByNumber(3);
        PutCaret(window, row3.Offset + 2);
        window.LivePaste(new DataObject(), true, "`a | b`");
        LongLineBench.Idle();
        string cRow3 = window.source.Document.GetText(window.source.Document.GetLineByNumber(3));
        Check(mode + " GFM pipe paste code with pipe into cell -> `a \\| b`",
            cRow3.IndexOf("`a \\| b`", StringComparison.Ordinal) >= 0 &&
            MarkdownLivePreview.SplitCells(cRow3).Count == 2 &&
            CountUnescapedPipes(cRow3) == 3,
            "row3=[" + cRow3 + "]");

        string htmlTableMd = ClipboardMarkdown("<table><tr><th>a</th><th>b</th></tr><tr><td><code>x | y</code></td><td>c</td></tr></table>");
        Check(mode + " GFM pipe HTML table paste with code pipe -> `x \\| y`",
            htmlTableMd != null && htmlTableMd.IndexOf("`x \\| y`", StringComparison.Ordinal) >= 0,
            "md=[" + FlatText(htmlTableMd) + "]");

        // (d) HTML export & plain-text copy of "| `a \| b` | c |" -> code text "a | b".
        string dMd = "| `a \\| b` | c |\n| --- | --- |\n| 1 | 2 |\n";
        string dHtml = HtmlExporter.Convert(dMd, "", "", "ko", null, false);
        Check(mode + " GFM pipe HtmlExporter.Convert unescapes \\| inside code span",
            dHtml.IndexOf("<code>a | b</code>", StringComparison.Ordinal) >= 0 &&
            dHtml.IndexOf("a \\| b", StringComparison.Ordinal) < 0,
            "hasCode=" + (dHtml.IndexOf("<code>a | b</code>", StringComparison.Ordinal) >= 0));

        string dPlain = MarkdownPlainText.Convert(dMd);
        Check(mode + " GFM pipe MarkdownPlainText.Convert unescapes \\| inside code span",
            dPlain.IndexOf("a | b", StringComparison.Ordinal) >= 0 &&
            dPlain.IndexOf("a \\| b", StringComparison.Ordinal) < 0,
            "plain=[" + FlatText(dPlain) + "]");

        // (e) Basic-view caret & Backspace/Delete over '\|' inside code behaves as one unit.
        string eFixture = "| `a\\|b` | c |\n| --- | --- |\n| 1 | 2 |\n";
        if (mode == "basic") {
            window.SetText(eFixture);
            LongLineBench.Idle();
            row1 = window.source.Document.GetLineByNumber(1);
            PutCaret(window, row1.Offset + 5);
            Check(mode + " GFM pipe caret between \\ and | inside code snaps after \\|",
                window.source.CaretOffset == row1.Offset + 6,
                "caret=" + window.source.CaretOffset + "/" + (row1.Offset + 6));

            PutCaret(window, row1.Offset + 6);
            PressBackspace(window);
            string eBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
            Check(mode + " GFM pipe Backspace after \\| inside code deletes both",
                eBack == "| `ab` | c |" && MarkdownLivePreview.SplitCells(eBack).Count == 2,
                "row1=[" + eBack + "]");

            window.SetText(eFixture);
            LongLineBench.Idle();
            row1 = window.source.Document.GetLineByNumber(1);
            PutCaret(window, row1.Offset + 4);
            PressDelete(window);
            string eDel = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
            Check(mode + " GFM pipe Delete before \\| inside code deletes both",
                eDel == "| `ab` | c |" && MarkdownLivePreview.SplitCells(eDel).Count == 2,
                "row1=[" + eDel + "]");
        } else {
            window.SetText(eFixture);
            LongLineBench.Idle();
            PutCaret(window, window.source.Document.GetLineByNumber(3).Offset + 2);
            bool eLiveHidden = HasHiddenAt(window.livePreview.Info(window.source.Document.GetLineByNumber(1)), 4);
            Check(mode + " GFM pipe off-caret row hides \\ of \\| inside code",
                eLiveHidden, "hiddenAt4=" + eLiveHidden);
        }

        // (f) Paragraph "`a \| b`" outside a table STILL shows the backslash.
        window.SetText("`a \\| b`\nsecond\n");
        LongLineBench.Idle();
        if (mode == "live") {
            PutCaret(window, window.source.Document.GetLineByNumber(2).Offset);
        }
        bool fHidden = HasHiddenAt(window.livePreview.Info(window.source.Document.GetLineByNumber(1)), 3);
        Check(mode + " GFM pipe paragraph code \\| does not hide backslash",
            !fHidden, "hiddenAt3=" + fHidden);

        if (mode == "basic") {
            window.SetText("`a \\| b`\n");
            LongLineBench.Idle();
            PutCaret(window, 4);
            Check(mode + " GFM pipe paragraph code \\| caret between \\ and | stays at 4",
                window.source.CaretOffset == 4, "caret=" + window.source.CaretOffset);

            PutCaret(window, 5);
            PressBackspace(window);
            string fBack = window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
            Check(mode + " GFM pipe paragraph code \\| Backspace deletes only |",
                fBack == "`a \\ b`", "line1=[" + fBack + "]");
        }

        string fHtml = HtmlExporter.Convert("`a \\| b`\n", "", "", "ko", null, false);
        Check(mode + " GFM pipe paragraph HtmlExporter.Convert keeps \\| inside code",
            fHtml.IndexOf("<code>a \\| b</code>", StringComparison.Ordinal) >= 0,
            "hasCode=" + (fHtml.IndexOf("<code>a \\| b</code>", StringComparison.Ordinal) >= 0));

        string fPlain = MarkdownPlainText.Convert("`a \\| b`\n");
        Check(mode + " GFM pipe paragraph MarkdownPlainText.Convert keeps \\| inside code",
            fPlain.IndexOf("a \\| b", StringComparison.Ordinal) >= 0,
            "plain=[" + FlatText(fPlain) + "]");
    }

    static void HuntCase(string name, Action body) {
        try {
            body();
        } catch (Exception ex) {
            Check(name, false, "EXCEPTION: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    static string LineText(MainWindow window, int lineNumber) {
        if (window.source.Document.LineCount < lineNumber) return "";
        return window.source.Document.GetText(window.source.Document.GetLineByNumber(lineNumber));
    }

    static void RunHuntChecks(MainWindow window, string mode) {
        string c;

        // Hunt 01 (Both views): Left / Shift+Left / Ctrl+Left trapped on \|
        c = mode + " Hunt 01 Left crosses escaped pipe in table cell";
        HuntCase(c, delegate {
            window.SetText("| a\\|b | c |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 5);
            RunCmd(window, EditingCommands.MoveLeftByCharacter);
            Check(c, window.source.CaretOffset == 3, "caret=" + window.source.CaretOffset);
        });

        c = mode + " Hunt 01 Left crosses escaped pipe inside table code span";
        HuntCase(c, delegate {
            window.SetText("| `a\\|b` | c |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 6);
            RunCmd(window, EditingCommands.MoveLeftByCharacter);
            Check(c, window.source.CaretOffset == 4, "caret=" + window.source.CaretOffset);
        });

        c = mode + " Hunt 01 Shift+Left crosses escaped pipe";
        HuntCase(c, delegate {
            window.SetText("| a\\|b | c |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 5);
            RunCmd(window, EditingCommands.SelectLeftByCharacter);
            Check(c, window.source.SelectionStart == 3 && window.source.SelectionLength == 2,
                "sel=(" + window.source.SelectionStart + "," + window.source.SelectionLength + ")");
        });

        c = mode + " Hunt 01 Ctrl+Left crosses escaped pipe";
        HuntCase(c, delegate {
            window.SetText("| a\\|b | c |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 5);
            RunCmd(window, EditingCommands.MoveLeftByWord);
            Check(c, window.source.CaretOffset <= 3, "caret=" + window.source.CaretOffset);
        });

        c = mode + " Hunt 01 Left crosses escaped pipe in non-table paragraph";
        HuntCase(c, delegate {
            window.SetText("a\\|b\n");
            LongLineBench.Idle();
            PutCaret(window, 3);
            RunCmd(window, EditingCommands.MoveLeftByCharacter);
            Check(c, window.source.CaretOffset == 1, "caret=" + window.source.CaretOffset);
        });

        c = mode + " Hunt 01 Mouse drag from mid escaped pipe snaps anchor and does not split pipe";
        HuntCase(c, delegate {
            window.SetText("| a\\|b | c |\n| - | - |\n");
            LongLineBench.Idle();
            DocumentLine row1Line = window.source.Document.GetLineByNumber(1);
            window.source.TextArea.TextView.EnsureVisualLines();
            VisualLine visual = window.source.TextArea.TextView.GetOrConstructVisualLine(row1Line);
            Rect box = window.livePreview.TableCellBox(visual, 0);
            Point pt = box.IsEmpty
                ? new Point(1, visual.VisualTop - window.source.TextArea.TextView.VerticalOffset + visual.Height / 2)
                : new Point(box.Left + 1, box.Top + box.Height / 2);
            window.livePreview.BeginTableDrag(4);
            window.livePreview.TableDragStep(pt);
            window.livePreview.EndTableDrag();
            LongLineBench.Idle();
            int selStart = window.source.SelectionStart;
            int selLen = window.source.SelectionLength;
            int selEnd = selStart + selLen;
            bool ok = selEnd != 4 && selStart != 4;
            PressBackspace(window);
            string row1 = LineText(window, 1);
            int pipes = CountUnescapedPipes(row1);
            Check(c, ok && pipes == 3,
                "sel=(" + selStart + "," + selLen + "), row1=[" + FlatText(row1) + "], pipes=" + pipes);
        });

        // Hunt 02 (basic only): Delete / Ctrl+Delete before hidden \ deletes \ too
        if (mode == "basic") {
            c = "basic Hunt 02 Delete before hidden backslash keeps escaped pipe";
            HuntCase(c, delegate {
                window.SetText("| a\\|b | c |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 2);
                PressDelete(window);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| \\|b | c |" && CountUnescapedPipes(row1) == 3 && MarkdownLivePreview.SplitCells(row1).Count == 2,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1) + ", cells=" + MarkdownLivePreview.SplitCells(row1).Count);
            });

            c = "basic Hunt 02 Delete before double backslash keeps column pipe";
            HuntCase(c, delegate {
                window.SetText("| a\\\\| b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 2);
                PressDelete(window);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| \\\\| b |" && CountUnescapedPipes(row1) == 3 && MarkdownLivePreview.SplitCells(row1).Count == 2,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1) + ", cells=" + MarkdownLivePreview.SplitCells(row1).Count);
            });

            c = "basic Hunt 02 Ctrl+Delete before hidden backslash keeps escaped pipe";
            HuntCase(c, delegate {
                window.SetText("| a\\|b | c |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 2);
                RunCmd(window, EditingCommands.DeleteNextWord);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| \\|b | c |" && CountUnescapedPipes(row1) == 3,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
            });
        }

        // Hunt 03 (Both views): Paste / Find & Replace ending with \ before | or \|
        c = mode + " Hunt 03 Paste trailing backslash before pipe preserves columns";
        HuntCase(c, delegate {
            window.SetText("| a| b |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 3);
            window.LivePaste(new DataObject(), false, "\\");
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            Check(c, row1 == "| a\\\\| b |" && CountUnescapedPipes(row1) == 3,
                "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
        });

        c = mode + " Hunt 03 Paste trailing backslash before escaped pipe preserves columns";
        HuntCase(c, delegate {
            window.SetText("| a\\| b | c |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 3);
            window.LivePaste(new DataObject(), false, "\\");
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            Check(c, row1 == "| a\\\\\\| b | c |" && CountUnescapedPipes(row1) == 3,
                "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
        });

        c = mode + " Hunt 03 Paste pipe and trailing backslash before pipe preserves columns";
        HuntCase(c, delegate {
            window.SetText("| a| b |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 3);
            window.LivePaste(new DataObject(), false, "x|y\\");
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            Check(c, row1 == "| ax\\|y\\\\| b |" && CountUnescapedPipes(row1) == 3,
                "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
        });

        c = mode + " Hunt 03 ReplaceOne trailing backslash before pipe preserves columns";
        HuntCase(c, delegate {
            window.SetText("| a| b |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 0);
            window.matchCase.IsChecked = false;
            window.matchRegex.IsChecked = false;
            try {
                window.query.Text = "a";
                window.replacement.Text = "\\";
                window.Find();
                window.ReplaceOne();
                LongLineBench.Idle();
                string row1 = LineText(window, 1);
                Check(c, row1 == "| \\\\| b |" && CountUnescapedPipes(row1) == 3,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
            } finally {
                window.query.Text = "";
                window.replacement.Text = "";
            }
        });

        c = mode + " Hunt 03 ReplaceAll trailing backslash before escaped pipe preserves columns";
        HuntCase(c, delegate {
            window.SetText("| a\\| b | c |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 0);
            window.matchCase.IsChecked = false;
            window.matchRegex.IsChecked = false;
            try {
                window.query.Text = "a";
                window.replacement.Text = "\\";
                window.ReplaceAll();
                LongLineBench.Idle();
                string row1 = LineText(window, 1);
                Check(c, row1 == "| \\\\\\| b | c |" && CountUnescapedPipes(row1) == 3,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
            } finally {
                window.query.Text = "";
                window.replacement.Text = "";
            }
        });

        // Hunt 04 (basic only): Typing \ over single-cell selection before | or \|
        if (mode == "basic") {
            c = "basic Hunt 04 Type backslash over selection before pipe preserves columns";
            HuntCase(c, delegate {
                window.SetText("| ab| c |\n| - | - |\n");
                LongLineBench.Idle();
                window.source.Select(3, 1);
                LongLineBench.Idle();
                window.source.TextArea.PerformTextInput("\\");
                LongLineBench.Idle();
                string row1 = LineText(window, 1);
                Check(c, row1 == "| a\\\\| c |" && CountUnescapedPipes(row1) == 3,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
            });

            c = "basic Hunt 04 Type backslash over selection before escaped pipe preserves columns";
            HuntCase(c, delegate {
                window.SetText("| ab\\| c | d |\n| - | - |\n");
                LongLineBench.Idle();
                window.source.Select(3, 1);
                LongLineBench.Idle();
                window.source.TextArea.PerformTextInput("\\");
                LongLineBench.Idle();
                string row1 = LineText(window, 1);
                Check(c, row1 == "| a\\\\\\| c | d |" && CountUnescapedPipes(row1) == 3,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
            });
        }

        // Hunt 05 (Both views for \\| / \\\|; live only for protecting space after \)
        c = mode + " Hunt 05 Backspace selected backslash in double-backslash pipe preserves columns";
        HuntCase(c, delegate {
            window.SetText("| a\\\\| b |\n| - | - |\n");
            LongLineBench.Idle();
            window.source.Select(4, 1);
            LongLineBench.Idle();
            PressBackspace(window);
            string row1 = LineText(window, 1);
            Check(c, CountUnescapedPipes(row1) == 3 && MarkdownLivePreview.SplitCells(row1).Count == 2,
                "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1) + ", cells=" + MarkdownLivePreview.SplitCells(row1).Count);
        });

        c = mode + " Hunt 05 Delete selected backslash in triple-backslash pipe preserves columns";
        HuntCase(c, delegate {
            window.SetText("| a\\\\\\|b | c |\n| - | - |\n");
            LongLineBench.Idle();
            window.source.Select(4, 1);
            LongLineBench.Idle();
            PressDelete(window);
            string row1 = LineText(window, 1);
            Check(c, CountUnescapedPipes(row1) == 3 && MarkdownLivePreview.SplitCells(row1).Count == 2,
                "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1) + ", cells=" + MarkdownLivePreview.SplitCells(row1).Count);
        });

        if (mode == "live") {
            c = "live Hunt 05 Backspace selected protecting space after backslash preserves columns";
            HuntCase(c, delegate {
                window.SetText("| a\\ | b |\n| - | - |\n");
                LongLineBench.Idle();
                window.source.Select(4, 1);
                LongLineBench.Idle();
                PressBackspace(window);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| a\\ | b |" && CountUnescapedPipes(row1) == 3,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
            });
        }

        // Hunt 06 (Both views for trailing space; live only for Right twice)
        c = mode + " Hunt 06 Right in last cell trailing space does not jump backwards";
        HuntCase(c, delegate {
            window.SetText("| a | b |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 7);
            window.source.TextArea.PerformTextInput(" ");
            LongLineBench.Idle();
            RunCmd(window, EditingCommands.MoveRightByCharacter);
            Check(c, window.source.CaretOffset >= 8, "caret=" + window.source.CaretOffset);
        });

        c = mode + " Hunt 06 Shift+Right in last cell trailing space does not select backwards";
        HuntCase(c, delegate {
            window.SetText("| a | b  |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 8);
            RunCmd(window, EditingCommands.SelectRightByCharacter);
            Check(c, window.source.CaretOffset >= 8 && !(window.source.SelectionStart == 7 && window.source.SelectionLength == 1),
                "caret=" + window.source.CaretOffset + ", sel=(" + window.source.SelectionStart + "," + window.source.SelectionLength + ")");
        });

        if (mode == "live") {
            c = "live Hunt 06 Right twice from last cell end does not ping-pong backwards";
            HuntCase(c, delegate {
                window.SetText("| a | b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 7);
                RunCmd(window, EditingCommands.MoveRightByCharacter);
                int c1 = window.source.CaretOffset;
                RunCmd(window, EditingCommands.MoveRightByCharacter);
                int c2 = window.source.CaretOffset;
                Check(c, c2 >= c1, "c1=" + c1 + ", c2=" + c2);
            });
        }

        // Hunt 09 (Both views): AddColumnRight after trailing odd \ without closing pipe
        c = mode + " Hunt 09 AddColumnRight after trailing backslash without closing pipe adds column";
        HuntCase(c, delegate {
            window.SetText("| a | b\\\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 8);
            CommandResult res = MarkdownTableCommands.AddColumnRight(window.source.Document, 8);
            if (res != null) window.ApplyCommandResult(res);
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            Check(c, MarkdownLivePreview.SplitCells(row1).Count == 3 && CountUnescapedPipes(row1) >= 3,
                "row1=[" + FlatText(row1) + "], cells=" + MarkdownLivePreview.SplitCells(row1).Count + ", pipes=" + CountUnescapedPipes(row1));
        });

        // Hunt 10 (Both views): Cross-pipe inline commands and ParseInline
        c = mode + " Hunt 10 Bold inside unclosed code across pipe stays in cell 0";
        HuntCase(c, delegate {
            window.SetText("| `aaa | b` |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 5);
            window.LiveInline("bold");
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            List<MarkdownLivePreview.Cell> cells = MarkdownLivePreview.SplitCells(row1);
            string cell1 = cells.Count >= 2 ? row1.Substring(cells[1].Start, cells[1].End - cells[1].Start) : "";
            Check(c, cells.Count == 2 && cell1 == "b`",
                "row1=[" + FlatText(row1) + "], cell1=[" + FlatText(cell1) + "]");
        });

        c = mode + " Hunt 10 Bold on selected text inside cross-pipe bold works in cell 0";
        HuntCase(c, delegate {
            window.SetText("| **a | b** |\n| - | - |\n");
            LongLineBench.Idle();
            window.source.Select(4, 1);
            LongLineBench.Idle();
            window.LiveInline("bold");
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            List<MarkdownLivePreview.Cell> cells = MarkdownLivePreview.SplitCells(row1);
            string cell1 = cells.Count >= 2 ? row1.Substring(cells[1].Start, cells[1].End - cells[1].Start) : "";
            Check(c, row1 != "| **a | b** |" && cells.Count == 2 && cell1 == "b**",
                "row1=[" + FlatText(row1) + "], cell1=[" + FlatText(cell1) + "]");
        });

        c = mode + " Hunt 10 ParseInline does not pair bold across cell pipe";
        HuntCase(c, delegate {
            window.SetText("| **a | b** |\n| - | - |\n| 1 | 2 |\n");
            LongLineBench.Idle();
            DocumentLine row1 = window.source.Document.GetLineByNumber(1);
            DocumentLine row3 = window.source.Document.GetLineByNumber(3);
            PutCaret(window, row3.Offset + 2);
            MarkdownLivePreview.LineInfo info1 = window.livePreview.Info(row1);
            Check(c, !HasHiddenAt(info1, 2), "hiddenAt2=" + HasHiddenAt(info1, 2));
        });

        c = mode + " Hunt 10 ParseInline does not pair link across cell pipe";
        HuntCase(c, delegate {
            window.SetText("| [a | b](https://x) |\n| - | - |\n| 1 | 2 |\n");
            LongLineBench.Idle();
            DocumentLine row1 = window.source.Document.GetLineByNumber(1);
            DocumentLine row3 = window.source.Document.GetLineByNumber(3);
            PutCaret(window, row3.Offset + 2);
            MarkdownLivePreview.LineInfo info1 = window.livePreview.Info(row1);
            Check(c, !HasHiddenAt(info1, 2), "hiddenAt2=" + HasHiddenAt(info1, 2));
        });

        // Hunt 11 (basic only): Backspace / Delete across hidden paired markers adjacent to \|
        if (mode == "basic") {
            c = "basic Hunt 11 Backspace after bold opener following escaped pipe deletes full unit";
            HuntCase(c, delegate {
                window.SetText("| \\|**a** | c |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 6);
                PressBackspace(window);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| **a** | c |" && CountUnescapedPipes(row1) == 3,
                    "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
            });

            c = "basic Hunt 11 Backspace at outer code closer after escaped pipe deletes full unit";
            HuntCase(c, delegate {
                window.SetText("| `a\\|` | b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 7);
                PressBackspace(window);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| `a` | b |", "row1=[" + FlatText(row1) + "]");
            });

            c = "basic Hunt 11 Delete at inner bold closer before escaped pipe deletes escaped pipe";
            HuntCase(c, delegate {
                window.SetText("| **a**\\|b | c |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 5);
                PressDelete(window);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| **a**b | c |", "row1=[" + FlatText(row1) + "]");
            });
        }

        // Hunt 13 (basic for InCellPadding snap + typing; Both for TableCellWall single-space Backspace)
        if (mode == "basic") {
            c = "basic Hunt 13 Caret at closed cell innerEnd snaps inside cell before typing";
            HuntCase(c, delegate {
                window.SetText("| a | b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 4);
                int caret = window.source.CaretOffset;
                window.source.TextArea.PerformTextInput("x");
                LongLineBench.Idle();
                string row1 = LineText(window, 1);
                Check(c, caret == 3 && row1 == "| ax | b |",
                    "caret=" + caret + ", row1=[" + FlatText(row1) + "]");
            });
        }

        if (mode == "basic") {
            c = "basic Hunt 13 Backspace at closed cell innerEnd keeps space before pipe";
            HuntCase(c, delegate {
                window.SetText("| a | b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 4);
                PressBackspace(window);
                string row1 = LineText(window, 1);
                Check(c, row1 != "| a| b |", "row1=[" + FlatText(row1) + "]");
            });
        } else {
            c = "live Hunt 13 Backspace at closed cell innerEnd matches body paragraph";
            HuntCase(c, delegate {
                window.SetText("a | b\n");
                LongLineBench.Idle();
                PutCaret(window, 2);
                PressBackspace(window);
                string bodyLine = LineText(window, 1);
                bool bodyRemovedSpace = bodyLine == "a| b";

                window.SetText("| a | b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 4);
                PressBackspace(window);
                string row1 = LineText(window, 1);
                bool cellRemovedSpace = row1 == "| a| b |";

                Check(c, cellRemovedSpace == bodyRemovedSpace,
                    "body=[" + FlatText(bodyLine) + "], row1=[" + FlatText(row1) + "]");
            });
        }

        // Hunt 14 (Both views): BackspaceBeforeTable into empty last cell
        c = mode + " Hunt 14 BackspaceBeforeTable into empty last cell lands at CellCaretStart";
        HuntCase(c, delegate {
            window.SetText("| a | b |\n| - | - |\n| c |  |\np\n");
            LongLineBench.Idle();
            DocumentLine row3 = window.source.Document.GetLineByNumber(3);
            DocumentLine row4 = window.source.Document.GetLineByNumber(4);
            List<MarkdownLivePreview.Cell> cells3 = MarkdownLivePreview.SplitCells(window.source.Document.GetText(row3));
            int expectedCaret = row3.Offset + MarkdownLivePreview.CellCaretStart(cells3[1]);
            PutCaret(window, row4.Offset);
            PressBackspace(window);
            Check(c, window.source.CaretOffset == expectedCaret,
                "caret=" + window.source.CaretOffset + ", expected=" + expectedCaret);
        });

        // Hunt 15 (basic only): Enter at last drawn cell of overflow last row
        if (mode == "basic") {
            c = "basic Hunt 15 Enter at last drawn cell of overflow last row exits table";
            HuntCase(c, delegate {
                window.SetText("| a | b |\n| - | - |\n| c | d | e |\n");
                LongLineBench.Idle();
                int beforeLines = window.source.Document.LineCount;
                DocumentLine row3 = window.source.Document.GetLineByNumber(3);
                PutCaret(window, row3.Offset + 7);
                PressLiveKey(window, Key.Enter);
                string row3Text = LineText(window, 3);
                Check(c, row3Text == "| c | d | e |" && window.source.Document.LineCount == beforeLines + 1,
                    "row3=[" + FlatText(row3Text) + "], lines=" + window.source.Document.LineCount + "/" + beforeLines);
            });
        }

        // Hunt 16 (basic only): Ctrl+Backspace after \| or \\
        if (mode == "basic") {
            c = "basic Hunt 16 Ctrl+Backspace after escaped pipe and space deletes hidden backslash";
            HuntCase(c, delegate {
                window.SetText("| a\\| b | c |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 6);
                RunCmd(window, EditingCommands.DeletePreviousWord);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| ab | c |", "row1=[" + FlatText(row1) + "]");
            });

            c = "basic Hunt 16 Ctrl+Backspace after double backslash and space deletes hidden backslash";
            HuntCase(c, delegate {
                window.SetText("| a\\\\ c | b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 6);
                RunCmd(window, EditingCommands.DeletePreviousWord);
                string row1 = LineText(window, 1);
                Check(c, row1 == "| ac | b |", "row1=[" + FlatText(row1) + "]");
            });
        }

        // Hunt 17 (basic only): Cell-start navigation landing before leading **
        if (mode == "basic") {
            c = "basic Hunt 17 Home on bold cell matches body paragraph";
            HuntCase(c, delegate {
                window.SetText("**a**\n");
                LongLineBench.Idle();
                PutCaret(window, 3);
                RunCmd(window, EditingCommands.MoveToLineStart);
                int bodyHome = window.source.CaretOffset;

                window.SetText("| **a** | b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 5);
                RunCmd(window, EditingCommands.MoveToLineStart);
                int cellHomeRel = window.source.CaretOffset - 2;

                Check(c, cellHomeRel == bodyHome,
                    "bodyHome=" + bodyHome + ", cellHomeRel=" + cellHomeRel);
            });

            c = "basic Hunt 17 Right into bold cell 1 does not stall on 0px step";
            HuntCase(c, delegate {
                window.SetText("x\n**a**\n");
                LongLineBench.Idle();
                PutCaret(window, 1);
                RunCmd(window, EditingCommands.MoveRightByCharacter);
                int bodyC1 = window.source.CaretOffset;
                RunCmd(window, EditingCommands.MoveRightByCharacter);
                int bodyC2 = window.source.CaretOffset;

                window.SetText("| x | **a** |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 3);
                RunCmd(window, EditingCommands.MoveRightByCharacter);
                int c1 = window.source.CaretOffset;
                RunCmd(window, EditingCommands.MoveRightByCharacter);
                int c2 = window.source.CaretOffset;

                Check(c, bodyC2 > 4 && c1 == 6 && c2 > 8,
                    "body=(" + bodyC1 + "," + bodyC2 + "), cell=(" + c1 + "," + c2 + ")");
            });

            c = "basic Hunt 17 Shift+Right from Home on bold cell does not select hidden opener only";
            HuntCase(c, delegate {
                window.SetText("| **a** | b |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 5);
                RunCmd(window, EditingCommands.MoveToLineStart);
                RunCmd(window, EditingCommands.SelectRightByCharacter);
                Check(c, !(window.source.SelectionStart == 2 && window.source.SelectionLength == 2),
                    "sel=(" + window.source.SelectionStart + "," + window.source.SelectionLength + ")");
            });
        }

        // Hunt 18 (Both views): TablePipeTyping Editor.Select outside EndUpdate()
        c = mode + " Hunt 18 Redo after typing pipe over selection places caret after escaped pipe";
        HuntCase(c, delegate {
            window.SetText("| abc | d |\n| - | - |\n");
            LongLineBench.Idle();
            window.source.Select(2, 3);
            LongLineBench.Idle();
            window.source.TextArea.PerformTextInput("|");
            LongLineBench.Idle();
            window.Undo();
            LongLineBench.Idle();
            window.Redo();
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            Check(c, row1 == "| \\| | d |" && window.source.CaretOffset == 4,
                "row1=[" + FlatText(row1) + "], caret=" + window.source.CaretOffset);
        });

        c = mode + " Hunt 18 Typing char after pipe over selection coalesces undo step";
        HuntCase(c, delegate {
            window.SetText("| abc | d |\n| - | - |\n");
            LongLineBench.Idle();
            window.source.Select(2, 3);
            LongLineBench.Idle();
            window.source.TextArea.PerformTextInput("|");
            LongLineBench.Idle();
            int d1 = window.History.UndoDepth;
            window.source.TextArea.PerformTextInput("x");
            LongLineBench.Idle();
            int d2 = window.History.UndoDepth;
            string row1 = LineText(window, 1);
            Check(c, row1 == "| \\|x | d |" && d2 == d1,
                "row1=[" + FlatText(row1) + "], d1=" + d1 + ", d2=" + d2);
        });

        // Hunt 19 (Both views): Multi-char TextComposition containing |
        c = mode + " Hunt 19 Multi-char TextComposition containing pipe escapes pipe";
        HuntCase(c, delegate {
            window.SetText("| a | b |\n| - | - |\n");
            LongLineBench.Idle();
            PutCaret(window, 3);
            window.source.TextArea.PerformTextInput("x|y");
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            Check(c, row1 == "| ax\\|y | b |" && CountUnescapedPipes(row1) == 3,
                "row1=[" + FlatText(row1) + "], pipes=" + CountUnescapedPipes(row1));
        });

        // Hunt 20 (basic only): Visible-text Find & Replace on \|
        if (mode == "basic") {
            c = "basic Hunt 20 Visible Replace on escaped pipe removes hidden backslash";
            HuntCase(c, delegate {
                window.SetText("| a \\| b | c |\n| - | - |\n");
                LongLineBench.Idle();
                PutCaret(window, 2);
                window.matchCase.IsChecked = false;
                window.matchRegex.IsChecked = false;
                try {
                    window.query.Text = "|";
                    window.replacement.Text = "x";
                    window.Find();
                    window.ReplaceOne();
                    LongLineBench.Idle();
                    string row1 = LineText(window, 1);
                    Check(c, row1 == "| a x b | c |" && row1.IndexOf("\\x", StringComparison.Ordinal) < 0,
                        "row1=[" + FlatText(row1) + "]");
                } finally {
                    window.query.Text = "";
                    window.replacement.Text = "";
                }
            });
        }

        // Hunt 21 (Both views): HTML <code>a \| b</code> preview + HTML table paste of literal a\|b in <code>
        c = mode + " Hunt 21 HTML code tag with escaped pipe hides backslash in preview";
        HuntCase(c, delegate {
            window.SetText("| <code>a \\| b</code> | c |\n| - | - |\n| 1 | 2 |\n");
            LongLineBench.Idle();
            DocumentLine row1 = window.source.Document.GetLineByNumber(1);
            DocumentLine row3 = window.source.Document.GetLineByNumber(3);
            PutCaret(window, row3.Offset + 2);
            MarkdownLivePreview.LineInfo info1 = window.livePreview.Info(row1);
            Check(c, HasHiddenAt(info1, 10), "hiddenAt10=" + HasHiddenAt(info1, 10));
        });

        c = mode + " Hunt 21 HTML table paste of literal backslash-pipe in code preserves backslash";
        HuntCase(c, delegate {
            string md = ClipboardMarkdown("<table><tr><th>h</th></tr><tr><td><code>a\\|b</code></td></tr></table>");
            string plain = md == null ? "" : MarkdownPlainText.Convert(md);
            Check(c, md != null && plain.IndexOf("a\\|b", StringComparison.Ordinal) >= 0,
                "md=[" + FlatText(md) + "], plain=[" + FlatText(plain) + "]");
        });

        // Hunt 22 (live only): Shift+Tab from below an overflow row
        if (mode == "live") {
            c = "live Hunt 22 Shift+Tab from below overflow row lands in overflow cell";
            HuntCase(c, delegate {
                window.SetText("| a | b |\n| - | - |\n| c | d | e |\n| f | g |\n");
                LongLineBench.Idle();
                DocumentLine row3 = window.source.Document.GetLineByNumber(3);
                DocumentLine row4 = window.source.Document.GetLineByNumber(4);
                List<MarkdownLivePreview.Cell> cells3 = MarkdownLivePreview.SplitCells(window.source.Document.GetText(row3));
                PutCaret(window, row4.Offset + 2);
                window.SourceTabPathProbe(true);
                LongLineBench.Idle();
                Check(c, cells3.Count >= 3 &&
                    window.source.SelectionStart == row3.Offset + cells3[2].Start &&
                    window.source.SelectionLength == cells3[2].End - cells3[2].Start,
                    "sel=(" + window.source.SelectionStart + "," + window.source.SelectionLength + "), expected=(" +
                    (cells3.Count >= 3 ? row3.Offset + cells3[2].Start : -1) + "," +
                    (cells3.Count >= 3 ? cells3[2].End - cells3[2].Start : -1) + ")");
            });
        }

        // Hunt 23a (Both views): Non-ASCII leading space is cell text in all splitters
        c = mode + " Hunt 23a Non-ASCII leading space is cell text in all splitters";
        HuntCase(c, delegate {
            string row = "\u3000| a |";
            int liveCount = MarkdownLivePreview.SplitCells(row).Count;
            int tableCmdCount = SplitRowCount(row);
            int cmdCount = TableCellsCount(row);
            int plainCount = MarkdownPlainText.Cells(row).Length;
            Check(c, liveCount == 2 && tableCmdCount == 2 && cmdCount == 2 && plainCount == 2,
                "live=" + liveCount + ", tableCmd=" + tableCmdCount + ", cmd=" + cmdCount + ", plain=" + plainCount);
        });

        // Hunt 23b (Both views): Single-pipe body row returns 0 cells in all splitters
        c = mode + " Hunt 23b Single-pipe body row returns 0 cells in all splitters";
        HuntCase(c, delegate {
            int plainPipe = MarkdownPlainText.Cells("|").Length;
            int plainPadded = MarkdownPlainText.Cells("  |  ").Length;
            int livePipe = MarkdownLivePreview.SplitCells("|").Count;
            int tableCmdPipe = SplitRowCount("|");
            int cmdPipe = TableCellsCount("|");
            int plainDouble = MarkdownPlainText.Cells("||").Length;
            Check(c, plainPipe == 0 && plainPadded == 0 && livePipe == 0 &&
                tableCmdPipe == 0 && cmdPipe == 0 && plainDouble == 1,
                "plainPipe=" + plainPipe + ", plainPadded=" + plainPadded + ", livePipe=" + livePipe +
                ", tableCmdPipe=" + tableCmdPipe + ", cmdPipe=" + cmdPipe + ", plainDouble=" + plainDouble);
        });

        // Hunt 23c (Both views): Table cell link URL and img alt unescape escaped pipe in preview and export
        c = mode + " Hunt 23c Table cell link URL and img alt unescape escaped pipe in preview and export";
        HuntCase(c, delegate {
            string md = "| [x](https://x/a\\|b) | <img src=\"p.png\" alt=\"a \\| b\"> |\n| - | - |\n| 1 | 2 |\n";
            window.SetText(md);
            LongLineBench.Idle();
            DocumentLine row1 = window.source.Document.GetLineByNumber(1);
            DocumentLine row3 = window.source.Document.GetLineByNumber(3);
            PutCaret(window, row3.Offset + 2);
            MarkdownLivePreview.LineInfo info1 = window.livePreview.Info(row1);
            string linkUrl = null;
            if (info1 != null && info1.Links != null) {
                for (int i = 0; i < info1.Links.Count; i++) {
                    if (info1.Links[i] != null && info1.Links[i].Url == "https://x/a|b") {
                        linkUrl = info1.Links[i].Url;
                        break;
                    }
                }
            }
            string imgAlt = null;
            if (info1 != null && info1.Replacements != null) {
                for (int i = 0; i < info1.Replacements.Count; i++) {
                    MarkdownLivePreview.Replacement rep = info1.Replacements[i];
                    if (rep.Kind == MarkdownLivePreview.ReplacementKind.Image) {
                        imgAlt = rep.Label;
                        break;
                    }
                }
            }
            string html = HtmlExporter.Convert(md, "", "", "ko", null, false);
            bool hasUrl = html.IndexOf("https://x/a|b", StringComparison.Ordinal) >= 0;
            bool hasAlt = html.IndexOf("alt=\"a | b\"", StringComparison.Ordinal) >= 0;
            Check(c, linkUrl == "https://x/a|b" && imgAlt == "a | b" && hasUrl && hasAlt,
                "linkUrl=[" + FlatText(linkUrl) + "], imgAlt=[" + FlatText(imgAlt) + "], hasUrl=" + hasUrl + ", hasAlt=" + hasAlt);
        });

        // Hunt 23d (Both views): Typing backslash over selection before leading pipe keeps leading pipe
        c = mode + " Hunt 23d Typing backslash over selection before leading pipe keeps leading pipe";
        HuntCase(c, delegate {
            window.SetText("| a | b |\n| - | - |\n");
            LongLineBench.Idle();
            window.source.Select(0, 3);
            LongLineBench.Idle();
            window.source.TextArea.PerformTextInput("\\");
            LongLineBench.Idle();
            string row1 = LineText(window, 1);
            int pipes = CountUnescapedPipes(row1);
            int cells = MarkdownLivePreview.SplitCells(row1).Count;
            Check(c, row1.StartsWith("|", StringComparison.Ordinal) && pipes == 3 && cells == 2,
                "row1=[" + FlatText(row1) + "], pipes=" + pipes + ", cells=" + cells);
        });
    }

    static int SplitRowCount(string text) {
        MethodInfo method = typeof(MarkdownTableCommands).GetMethod("SplitRow", BindingFlags.NonPublic | BindingFlags.Static);
        object rowCells = method.Invoke(null, new object[] { text });
        PropertyInfo countProp = rowCells.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.NonPublic);
        return (int)countProp.GetValue(rowCells, null);
    }

    static int TableCellsCount(string text) {
        MethodInfo method = typeof(MarkdownCommands).GetMethod("TableCells", BindingFlags.NonPublic | BindingFlags.Static);
        System.Collections.ICollection cells = (System.Collections.ICollection)method.Invoke(null, new object[] { text });
        return cells.Count;
    }
}
}
}
