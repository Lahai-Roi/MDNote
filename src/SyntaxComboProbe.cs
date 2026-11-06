// Live-surface syntax-combination probe: overlapping inline syntax edited through the window's
// own command and key paths on an offscreen window (no SendInput, no real keyboard or mouse).
// Every fixture is generated here; no user file is read. The report and the PASS marker go into
// the output folder. A FAIL is the measurement, not a crash.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
internal static class SyntaxComboProbe {
    const BindingFlags Members = BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;

    internal static int Run(string folder) {
        string dir = Path.GetFullPath(folder);
        Directory.CreateDirectory(dir);
        string passFile = Path.Combine(dir, "syntaxcombo-PASS.txt");
        string failFile = Path.Combine(dir, "syntaxcombo-FAIL.txt");
        if (File.Exists(passFile)) File.Delete(passFile);
        if (File.Exists(failFile)) File.Delete(failFile);
        var report = new StringBuilder();
        bool all = true;
        int count = 0;
        Action<string,bool,string> check = delegate(string name,bool ok,string detail) {
            count++;
            all &= ok;
            report.Append(ok ? "PASS " : "FAIL ").Append(name);
            if (detail != null) report.Append(": ").Append(detail);
            report.Append('\n');
        };
        // ProbeDefaults is its own static class, not a MainWindow field; reading it through
        // MainWindow reflection returned null after the old-engine deletion.
        bool oldHide = Static<bool>("hideMarkers"), oldLive = ProbeDefaults.Live;
        MainWindow window = null;
        string file = Path.Combine(dir, "syntaxcombo-generated.md");
        try {
            if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
            Static("hideMarkers", true);
            ProbeDefaults.Live = true;
            File.WriteAllText(file, "# 제목\n\n본문\n", new UTF8Encoding(false));
            window = new MainWindow { ShowInTaskbar=false, ShowActivated=false, Width=900, Height=700 };
            window.Show();
            window.Left = -20000;
            Invoke(window, "LoadFileCore", file);
            Invoke(window, "SetMode", true);
            Pump();
            var session = Field<MarkdownLivePreview.Session>(window, "livePreview");
            check("window opens live with hidden markers",
                session != null && session.Options.HideMarkers && Prop<bool>(window, "CodeMode"),
                "session=" + (session != null) + ", hide=" + (session == null ? "-" : session.Options.HideMarkers.ToString()) +
                ", code=" + Prop<bool>(window, "CodeMode"));

            HiddenCases(window, check);
            StructureCases(window, check);
            RenderingCases(window, check);
            OverlapCases(window, check);
            CellPasteCase(window, check);
            ReviewCases(window, check);
            ReviewSpaceCommitCases(window, check);
            ShownCases(window, check);
        } catch (Exception ex) {
            check("exception", false, ex.ToString().Replace('\r',' ').Replace('\n',' '));
        } finally {
            if (window != null) {
                try { Set(window, "documentOperationBusy", false); Set(window, "saved", Prop<string>(window, "text")); Set(window, "recoveredDocument", false); window.Close(); }
                catch (Exception ex) { check("close", false, ex.Message.Replace('\r',' ').Replace('\n',' ')); }
            }
            Static("hideMarkers", oldHide);
            ProbeDefaults.Live = oldLive;
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException ex) { check("cleanup", false, ex.Message); }
        }
        report.Insert(0, "# SyntaxComboProbe\n\n- Timestamp: " + DateTime.Now.ToString("o", CultureInfo.InvariantCulture) +
            "\n- Checks: " + count.ToString(CultureInfo.InvariantCulture) + "\n\n");
        File.WriteAllText(Path.Combine(dir, "syntaxcombo-report.txt"), report.ToString(), new UTF8Encoding(false));
        if (all) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
        else File.WriteAllText(failFile, report.ToString(), new UTF8Encoding(false));
        return all ? 0 : 1;
    }

    // ---------------------------------------------------------------- table cell paste
    // A paste into a table cell must grow the typed column in the same frame as typing the same
    // text. The host's paste path (LivePaste -> PasteInCell -> ApplyCommandResult) is one bulk
    // document edit; before the fix it left the published width alone until the caret left the
    // table and the idle re-sample ran, so the pasted sentence wrapped inside a narrow column.
    static void CellPasteCase(MainWindow window, Action<string,bool,string> check) {
        const string text="| 항목 | 값 |\r\n| --- | --- |\r\n| x | y |\r\n";
        const string sentence="표 칸에 긴 문장을 붙여넣으면 그 칸의 폭이 바로 넓어져야 하고 다음 단어도 이어서 보여야 합니다";
        // 0: the cell text's end; 1: before the cell text (its padding); 2: the row's end, after
        // the last pipe. A paste in a row always clamps into a cell, so every spot grows that
        // cell's column; the typed reference is the cell text's own end.
        double typed=CellWidthAfter(window,text,sentence,true,0);
        for (int spot=0;spot<3;spot++) {
            double pasted=CellWidthAfter(window,text,sentence,false,spot);
            check("a paste into a table cell grows the column like typing (spot "+spot+")",
                typed>250&&Math.Abs(typed-pasted)<=2,
                "typed="+typed.ToString("0.##",CultureInfo.InvariantCulture)+", pasted="+pasted.ToString("0.##",CultureInfo.InvariantCulture));
        }
    }
    static double CellWidthAfter(MainWindow window,string text,string sentence,bool typed,int spot) {
        Load(window,text);
        SourceEditor source=Source(window);
        DocumentLine line=source.Document.GetLineByNumber(3);
        MarkdownLivePreview.LineInfo info=Session(window).Info(line);
        int column=spot==2?info.Cells.Count-1:0;
        int at=spot==0?info.Cells[column].End:spot==1?info.Cells[column].Start-1:line.EndOffset;
        source.Select(at,0);
        Pump();
        if (typed) TextInput(window,sentence);
        else Invoke(window,"LivePaste",new System.Windows.DataObject(),true,sentence);
        Pump();
        line=source.Document.GetLineByNumber(3);
        MarkdownLivePreview.RowLayout row=Session(window).Info(line).Row;
        return row==null||column>=row.Widths.Length?-1:row.Widths[column];
    }
    // ---------------------------------------------------------------- hidden markers (the default)
    static void HiddenCases(MainWindow window, Action<string,bool,string> check) {
        // 1. every inline syntax in one paragraph, with styles combined inside a span.
        string combined = "본문 **굵게** *기울임* ~~취소~~ `코드` <span style=\"color:#C03030;font-size:24px\">**빨강굵게**</span> 끝";
        Load(window, combined);
        var source = Source(window);
        var session = Session(window);
        var line = source.Document.GetLineByNumber(1);
        string visible = VisibleLine(session, line);
        check("combined paragraph parses every style",
            visible == "본문 굵게 기울임 취소 코드 빨강굵게 끝" && !session.Raw(line) && MarkupFault(visible) == null,
            "visible=[" + visible + "], raw=" + session.Raw(line) + ", fault=" + MarkupFault(visible));
        check("bold/italic/strike/code flags at their text",
            Flags(session, line, "굵게") == "B---" && Flags(session, line, "기울임") == "-I--" &&
            Flags(session, line, "취소") == "--S-" && Flags(session, line, "코드") == "---C" &&
            Flags(session, line, "빨강굵게") == "B---",
            "bold=" + Flags(session, line, "굵게") + ", italic=" + Flags(session, line, "기울임") +
            ", strike=" + Flags(session, line, "취소") + ", code=" + Flags(session, line, "코드") +
            ", span bold=" + Flags(session, line, "빨강굵게"));
        check("a size span keeps its own size inside the paragraph",
            Math.Abs(DrawnSize(window, OffsetOf(source, line, "빨강굵게")) - 24) < .01 &&
            SizeBox(window) == "24 px",
            "drawn=" + N(DrawnSize(window, OffsetOf(source, line, "빨강굵게"))) + ", box=[" + SizeBox(window) + "]");

        // 2. size on the visible text of a bold run puts the span outside the markers.
        Load(window, "**작성 팁**");
        source = Source(window); session = Session(window);
        line = source.Document.GetLineByNumber(1);
        int visibleStart = OffsetOf(source, line, "작성 팁");
        source.Select(visibleStart, "작성 팁".Length);
        Invoke(window, "SetFontSize", 20.0);
        Pump();
        string expected = "<span style=\"font-size:20px\">**작성 팁**</span>";
        visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
        check("size on visible bold text wraps outside the markers",
            source.Document.Text == expected && visible == "작성 팁" && MarkupFault(visible) == null,
            "text=[" + source.Document.Text + "], expected=[" + expected + "], visible=[" + visible + "]");
        check("the caret at the visible end of 20 px text reports 20 px",
            EndOfVisible(window, source, "작성 팁") && SizeBox(window) == "20 px",
            "box=[" + SizeBox(window) + "]");

        // 3. the same selection written with the markers included.
        Load(window, "**작성 팁**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset, line.Length);
        Invoke(window, "SetFontSize", 20.0);
        Pump();
        check("size on the selection with markers yields the same source",
            source.Document.Text == expected, "text=[" + source.Document.Text + "], expected=[" + expected + "]");

        // 4. a selection inside inline code takes the code run whole.
        Load(window, "`x`");
        source = Source(window);
        source.Select(1, 1);
        Invoke(window, "SetFontSize", 20.0);
        Pump();
        string codeExpected = "<span style=\"font-size:20px\">`x`</span>";
        visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
        check("size inside inline code wraps outside the backticks",
            source.Document.Text == codeExpected && visible == "x" && MarkupFault(visible) == null,
            "text=[" + source.Document.Text + "], expected=[" + codeExpected + "], visible=[" + visible + "]");
        check("the code span keeps the code style under the size",
            FlagsAt(Session(window), OffsetOf(source, source.Document.GetLineByNumber(1), "`x`") + 1) == "---C" &&
            Math.Abs(DrawnSize(window, OffsetOf(source, source.Document.GetLineByNumber(1), "`x`") + 1) - 20) < .01,
            "flags=" + FlagsAt(Session(window), OffsetOf(source, source.Document.GetLineByNumber(1), "`x`") + 1));

        // 5. a partial selection of a bold run keeps the span inside it.
        Load(window, "**작성 팁**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(OffsetOf(source, line, "작성"), "작성".Length);
        Invoke(window, "SetFontSize", 20.0);
        Pump();
        check("a partial bold selection nests the span inside the bold",
            source.Document.Text == "**<span style=\"font-size:20px\">작성</span> 팁**",
            "text=[" + source.Document.Text + "]");

        // 6. color on the same selection goes through the same normalization.
        Load(window, "**작성 팁**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(OffsetOf(source, line, "작성 팁"), 5);
        Invoke(window, "SetTextColor", Color.FromRgb(0x00, 0x00, 0xFF));
        Pump();
        check("color on visible bold text wraps outside the markers",
            source.Document.Text == "<span style=\"color:#0000FF\">**작성 팁**</span>",
            "text=[" + source.Document.Text + "]");

        // 7. typing on the outer edge of a closing pair stays inside it.
        Load(window, "**굵게**");
        source = Source(window); session = Session(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, "X");
        visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
        check("typing after **bold** stays bold",
            source.Document.Text == "**굵게X**" && Flags(Session(window), source.Document.GetLineByNumber(1), "X") == "B---",
            "text=[" + source.Document.Text + "], flags=" + Flags(Session(window), source.Document.GetLineByNumber(1), "X") + ", visible=[" + visible + "]");
        Undo(window);
        check("one undo takes the typed character back", source.Document.Text == "**굵게**", "text=[" + source.Document.Text + "]");

        // 8. the same for a size span: the box and the text keep the 20 px.
        Load(window, "<span style=\"font-size:20px\">크게</span>");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, "X");
        check("typing after a 20 px span stays 20 px",
            source.Document.Text == "<span style=\"font-size:20px\">크게X</span>" &&
            Math.Abs(DrawnSize(window, source.Document.GetLineByNumber(1).EndOffset - 8) - 20) < .01,
            "text=[" + source.Document.Text + "], drawn=" + N(DrawnSize(window, source.Document.GetLineByNumber(1).EndOffset - 8)));

        // 9. Backspace at the pair's edge deletes the visible character, never the tag.
        Load(window, "<span style=\"font-size:20px\">크게</span>");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        Backspace(window);
        check("Backspace before the closing tag takes the visible character",
            source.Document.Text == "<span style=\"font-size:20px\">크</span>",
            "text=[" + source.Document.Text + "]");
        Backspace(window);
        check("the last visible character takes the tag pair with it",
            source.Document.Text == "" && SourceFault(source.Document.Text) == null,
            "text=[" + source.Document.Text + "]");
        Undo(window);
        Undo(window);
        check("undo restores the deleted span text", source.Document.Text == "<span style=\"font-size:20px\">크게</span>",
            "text=[" + source.Document.Text + "]");

        // 10. Delete at the pair's edge deletes the first visible character.
        Load(window, "**굵게**");
        source = Source(window);
        source.Select(0, 0);
        Pump();
        Delete(window);
        check("Delete before the opening ** takes the visible character",
            source.Document.Text == "**게**", "text=[" + source.Document.Text + "]");

        // 11. Enter inside a pair splits it; Enter on its edges moves outside first.
        Load(window, "**굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 3, 0);
        Pump();
        Enter(window);
        check("Enter inside **굵게** splits the pair",
            source.Document.Text == "**굵**\n**게**" && SourceFault(source.Document.Text) == null,
            "text=[" + Flat(source.Document.Text) + "]");
        session = Session(window);
        check("both split lines keep the bold and hide the markers",
            VisibleLine(session, source.Document.GetLineByNumber(1)) == "굵" &&
            VisibleLine(session, source.Document.GetLineByNumber(2)) == "게" &&
            Flags(session, source.Document.GetLineByNumber(2), "게") == "B---",
            "line1=[" + VisibleLine(session, source.Document.GetLineByNumber(1)) + "], line2=[" + VisibleLine(session, source.Document.GetLineByNumber(2)) + "]");
        Undo(window);
        check("one undo takes the split back", source.Document.Text == "**굵게**", "text=[" + Flat(source.Document.Text) + "]");

        Load(window, "**굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 4, 0);
        Pump();
        int settled = source.CaretIndex;
        Enter(window);
        check("Enter at the closing markers' inner edge moves outside first",
            source.Document.Text == "**굵게**\n", "settled=" + settled + ", text=[" + Flat(source.Document.Text) + "]");
        Load(window, "**굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 2, 0);
        Pump();
        settled = source.CaretIndex;
        Enter(window);
        check("Enter at the opening markers' inner edge moves outside first",
            source.Document.Text == "\n**굵게**", "settled=" + settled + ", text=[" + Flat(source.Document.Text) + "]");

        // 12. Enter inside a span tag writes the very same tag on the next line.
        Load(window, "<span style=\"font-size:20px\">크게</span>");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 30, 0);
        Pump();
        Enter(window);
        check("Enter inside a span splits the tags",
            source.Document.Text == "<span style=\"font-size:20px\">크</span>\n<span style=\"font-size:20px\">게</span>" &&
            SourceFault(source.Document.Text) == null,
            "text=[" + Flat(source.Document.Text) + "]");
        session = Session(window);
        line = source.Document.GetLineByNumber(2);
        check("both span lines draw 20 px text", Math.Abs(DrawnSize(window, OffsetOf(source, line, "게")) - 20) < .01,
            "drawn=" + N(DrawnSize(window, OffsetOf(source, line, "게"))));

        // 13. list continuation survives the split.
        Load(window, "- **굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 5, 0);
        Pump();
        Enter(window);
        check("Enter inside a bullet's bold keeps the list item",
            source.Document.Text == "- **굵**\n- **게**" && SourceFault(source.Document.Text) == null,
            "text=[" + Flat(source.Document.Text) + "]");
        session = Session(window);
        check("the continued bullet hides its prefix", MarkupFault(VisibleLine(session, source.Document.GetLineByNumber(2))) == null,
            "line2=[" + VisibleLine(session, source.Document.GetLineByNumber(2)) + "]");

        // 14. a task item continues as an unchecked box.
        Load(window, "- [ ] **할일**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 9, 0);
        Pump();
        Enter(window);
        check("Enter inside a task item's bold continues the task",
            source.Document.Text == "- [ ] **할**\n- [ ] **일**" && SourceFault(source.Document.Text) == null,
            "text=[" + Flat(source.Document.Text) + "]");

        // 15. a quote continues its level.
        Load(window, "> **인용**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 5, 0);
        Pump();
        Enter(window);
        check("Enter inside a quote's bold keeps the quote",
            source.Document.Text == "> **인**\n> **용**" && SourceFault(source.Document.Text) == null,
            "text=[" + Flat(source.Document.Text) + "]");

        // 16. an H2 keeps its heading on the first line only, like a plain Enter.
        Load(window, "## **제목**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        int heading = Session(window).Info(line).Heading;
        source.Select(line.Offset + 6, 0);
        Pump();
        Enter(window);
        check("Enter inside a heading's bold splits the pair",
            source.Document.Text == "## **제**\n**목**" && heading == 2 && SourceFault(source.Document.Text) == null,
            "heading=" + heading + ", text=[" + Flat(source.Document.Text) + "]");
        // 17. a table cell keeps its pipes while the span around the bold wraps outside it.
        Load(window, "| 이름 | 값 |\n| --- | --- |\n| **굵은셀** | 보통 |\n");
        source = Source(window);
        line = source.Document.GetLineByNumber(3);
        source.Select(OffsetOf(source, line, "굵은셀"), 3);
        Invoke(window, "SetFontSize", 20.0);
        Pump();
        line = source.Document.GetLineByNumber(3);
        string row = source.Document.GetText(line);
        session = Session(window);
        check("a table cell takes the size span outside its bold markers",
            row == "| <span style=\"font-size:20px\">**굵은셀**</span> | 보통 |" &&
            Pipes(row) == 3 && HiddenRange(session.Info(line), row.IndexOf("**", StringComparison.Ordinal), 2) &&
            HiddenRange(session.Info(line), row.IndexOf("<span", StringComparison.Ordinal), "<span style=\"font-size:20px\">".Length),
            "row=[" + row + "], pipes=" + Pipes(row));

        // 18. Enter inside a cell's bold writes the cell's own "<br>" and keeps the pair.
        Load(window, "| a | b |\n| --- | --- |\n| **굵게** | x |\n");
        source = Source(window);
        line = source.Document.GetLineByNumber(3);
        source.Select(OffsetOf(source, line, "게"), 0);
        Pump();
        Enter(window);
        row = source.Document.GetText(source.Document.GetLineByNumber(3));
        check("Enter inside a cell's bold writes <br> and keeps the pair",
            row == "| **굵<br>게** | x |" && SourceFault(source.Document.Text) == null,
            "row=[" + row + "]");

        // 19. the paste caret snap uses the same inside edge.
        Load(window, "**굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        PasteSnap(window);
        check("a paste on the closing edge lands inside the bold",
            source.CaretIndex == line.Length - 2, "caret=" + source.CaretIndex + ", expected=" + (line.Length - 2));
        Load(window, "<span style=\"font-size:20px\">크게</span>");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        PasteSnap(window);
        check("a paste on the closing tag's edge lands inside the span",
            source.CaretIndex == line.Length - 7, "caret=" + source.CaretIndex + ", expected=" + (line.Length - 7));

        // 20. Backspace through a bold pair until the line is empty takes the markers too.
        Load(window, "**굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        Backspace(window);
        Backspace(window);
        check("Backspace empties the bold line without leaving markers",
            source.Document.Text == "" && SourceFault(source.Document.Text) == null,
            "text=[" + source.Document.Text + "]");

        // 21. the list marker still deletes as one unit from the item text's start.
        Load(window, "- **목록**");
        source = Source(window);
        bool handled = PressKey(window, Key.Back, 2);
        check("Backspace at a list item's text start removes the bullet as a unit",
            handled && source.Document.Text == "**목록**", "handled=" + handled + ", text=[" + Flat(source.Document.Text) + "]");

        // 22. <font> tags follow the same rules as <span>.
        Load(window, "<font color=\"blue\">파랑 **굵게**</font>");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        session = Session(window);
        visible = VisibleLine(session, line);
        check("<font> content still parses its own markup",
            visible == "파랑 굵게" && MarkupFault(visible) == null && Flags(session, line, "굵게") == "B---",
            "visible=[" + visible + "], flags=" + Flags(session, line, "굵게"));
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, "X");
        check("typing after the <font> close stays inside the font",
            // b56: the caret enters the whole stacked closing run, so the bold pair inside the
            // font keeps its text bold too (it used to stop at the font's own tag).
            source.Document.Text == "<font color=\"blue\">파랑 **굵게X**</font>",
            "text=[" + source.Document.Text + "]");

        // 23. an empty size pair (what the typing-size command writes) deletes as one unit.
        Load(window, "<span style=\"font-size:20px\"></span>");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 29, 0);
        Pump();
        Backspace(window);
        check("Backspace inside an empty size pair removes both tags",
            source.Document.Text == "" && SourceFault(source.Document.Text) == null,
            "text=[" + source.Document.Text + "]");
        Load(window, "<span style=\"font-size:20px\"></span> tail");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 36, 0);
        Pump();
        Backspace(window);
        check("Backspace after an empty size pair removes both tags",
            source.Document.Text == " tail" && SourceFault(source.Document.Text) == null,
            "text=[" + source.Document.Text + "]");
        Load(window, "<span style=\"font-size:20px\"></span> tail");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 29, 0);
        Pump();
        Delete(window);
        check("Delete inside an empty size pair removes both tags",
            source.Document.Text == " tail" && SourceFault(source.Document.Text) == null,
            "text=[" + source.Document.Text + "]");

        // 24. Typing at the visible end of bold inside a size span keeps both styles: the caret
        // goes inside every stacked closing marker at that boundary, not just the outermost.
        Load(window, "<span style=\"font-size:20px\">**abc**</span>");
        source = Source(window); session = Session(window);
        line = source.Document.GetLineByNumber(1);
        int abc = OffsetOf(source, line, "abc");
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, "X");
        line = source.Document.GetLineByNumber(1);
        check("typing at the end of bold inside a size span is bold and 20 px",
            source.Document.Text == "<span style=\"font-size:20px\">**abcX**</span>" &&
            FlagsAt(Session(window), abc + 3) == "B---" &&
            Math.Abs(DrawnSize(window, abc + 3) - 20) < .01,
            "text=[" + source.Document.Text + "], flags=" + FlagsAt(Session(window), abc + 3) +
            ", drawn=" + N(DrawnSize(window, abc + 3)));

        // 25. A Backspace that would leave the last space of the text inside the closing markers
        // puts the whitespace outside them, so the pair stays recognised (raw "**" never shows).
        Load(window, "<span style=\"font-size:20px\">**alpha beta gamma**</span>");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        for (int i = 0; i < 5; i++) Backspace(window);
        visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
        check("Backspace that would leave a space inside bold keeps it outside",
            source.Document.Text == "<span style=\"font-size:20px\">**alpha beta** </span>" &&
            visible == "alpha beta " && MarkupFault(visible) == null,
            "text=[" + source.Document.Text + "], visible=[" + visible + "]");
        Undo(window);
        check("one undo takes the moved space and the deleted glyph back together",
            source.Document.Text == "<span style=\"font-size:20px\">**alpha beta g**</span>",
            "text=[" + source.Document.Text + "]");

        // 26. A space typed on the pair's closing edge goes outside it; the pair stays valid.
        // 11-05 rule B: the caret on the outer edge of a closed run inside a size span is past the
        // span too, so the space leaves the span (it used to stay inside the tag at 20 px).
        Load(window, "<span style=\"font-size:20px\">**abc**</span>");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, " ");
        string spaced = "<span style=\"font-size:20px\">**abc**</span> ";
        int spaceAt = spaced.Length;
        check("a space typed at the end of bold inside a span leaves the span",
            source.Document.Text == spaced && Math.Abs(DrawnSize(window, spaceAt) - 20) > .01,
            "text=[" + source.Document.Text + "], drawn after the space=" + N(DrawnSize(window, spaceAt)));
        Load(window, "**ab**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, " ");
        visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
        check("a space typed at the end of bold stays outside the pair",
            source.Document.Text == "**ab** " && visible == "ab " && MarkupFault(visible) == null,
            "text=[" + source.Document.Text + "], visible=[" + visible + "]");
        // 11-05 rule B: the space after an already CLOSED bold is plain, and so is the character
        // after it; the toolbar shows bold off at the caret after that space.
        bool boldAfterSpace = ToolbarBold(window);
        TextInput(window, "c");
        check("the character after a space typed after closed bold is plain",
            source.Document.Text == "**ab** c" && Flags(Session(window), source.Document.GetLineByNumber(1), "c") == "----" && !boldAfterSpace,
            "text=[" + source.Document.Text + "], flags=" + Flags(Session(window), source.Document.GetLineByNumber(1), "c") + ", boldAfterSpace=" + boldAfterSpace);
        // Rule C: the same keys with the caret inside the pair (the run being typed) take the
        // space back inside with the next character.
        Load(window, "**ab**");
        source = Source(window);
        source.Select(source.Document.GetLineByNumber(1).EndOffset - 2, 0);
        Pump();
        TextInput(window, " ");
        TextInput(window, "c");
        check("the character after a space typed inside the pair takes the space back inside with it",
            source.Document.Text == "**ab c**" && Flags(Session(window), source.Document.GetLineByNumber(1), "c") == "B---",
            "text=[" + source.Document.Text + "], flags=" + Flags(Session(window), source.Document.GetLineByNumber(1), "c"));

        // 27. The same on the opening edge: a space there goes in front of the opening markers.
        Load(window, "**ab**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 2, 0);
        Pump();
        TextInput(window, " ");
        visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
        check("a space typed at the start of bold stays outside the pair",
            source.Document.Text == " **ab**" && visible == " ab" && MarkupFault(visible) == null,
            "text=[" + source.Document.Text + "], visible=[" + visible + "]");

        // 28. Strike and forward Delete follow the same whitespace rule.
        Load(window, "~~a b~~");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        Backspace(window);
        visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
        check("Backspace that would leave a space inside strike keeps it outside",
            source.Document.Text == "~~a~~ " && visible == "a " && MarkupFault(visible) == null,
            "text=[" + source.Document.Text + "], visible=[" + visible + "]");
        Load(window, "**a bc**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 2, 0);
        Pump();
        Delete(window);
        visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
        check("Delete that would leave a space after the opening bold keeps it outside",
            source.Document.Text == " **bc**" && visible == " bc" && MarkupFault(visible) == null,
            "text=[" + source.Document.Text + "], visible=[" + visible + "]");
        ContinueCases(window, check);
    }

    // ---------------------------------------------------------------- the space at a pair's edge
    // A phrase typed word by word at the visible end of a styled run: every space typed on the
    // closing edge stays outside the pair only until the next word starts, and that first
    // character takes the whitespace back inside with itself. The whole phrase therefore keeps
    // the style and no marker is ever drawn as text.
    static void ContinueCases(MainWindow window, Action<string,bool,string> check) {
        // Bold: the run being typed (caret inside the pair, rule C) keeps the phrase styled; the
        // same keys after an already closed bold (caret on the outer edge, rule B, 11-05) leave
        // the space and the phrase plain.
        Phrase(window, check, "bold", "**bold**", "**bold alpha beta gamma**", "B---", 0, 2);
        Phrase(window, check, "closed bold", "**bold**", "**bold** alpha beta gamma", "----", 0, 0);
        // 11-05 rule B for every inline style: italic, strike, bold+italic and the size/colour
        // tags behave like bold (the old italic/strike cases kept the phrase inside the closed run).
        Phrase(window, check, "italic", "*bold*", "*bold alpha beta gamma*", "-I--", 0, 1);
        Phrase(window, check, "closed italic", "*bold*", "*bold* alpha beta gamma", "----", 0, 0);
        Phrase(window, check, "strike", "~~bold~~", "~~bold alpha beta gamma~~", "--S-", 0, 2);
        Phrase(window, check, "closed strike", "~~bold~~", "~~bold~~ alpha beta gamma", "----", 0, 0);
        Phrase(window, check, "bold italic", "***bold***", "***bold alpha beta gamma***", "BI--", 0, 3);
        Phrase(window, check, "closed bold italic", "***bold***", "***bold*** alpha beta gamma", "----", 0, 0);
        Phrase(window, check, "bold in a 20 px span", "<span style=\"font-size:20px\">**bold**</span>",
            "<span style=\"font-size:20px\">**bold alpha beta gamma**</span>", "B---", 20, 9);
        // The caret on the outer edge of a closed bold inside a size span is past the span too, so the
        // space and the phrase leave both (before 11-05 the phrase stayed inside the span at 20 px).
        Phrase(window, check, "closed bold in a 20 px span", "<span style=\"font-size:20px\">**bold**</span>",
            "<span style=\"font-size:20px\">**bold**</span> alpha beta gamma", "----", 0, 0);
        Phrase(window, check, "a 20 px span", "<span style=\"font-size:20px\">bold</span>",
            "<span style=\"font-size:20px\">bold alpha beta gamma</span>", "----", 20, 7);
        Phrase(window, check, "closed 20 px span", "<span style=\"font-size:20px\">bold</span>",
            "<span style=\"font-size:20px\">bold</span> alpha beta gamma", "----", 0, 0);
        Phrase(window, check, "colour tag", "<font color=\"blue\">bold</font>",
            "<font color=\"blue\">bold alpha beta gamma</font>", "----", 0, 7);
        Phrase(window, check, "closed colour tag", "<font color=\"blue\">bold</font>",
            "<font color=\"blue\">bold</font> alpha beta gamma", "----", 0, 0);
        ClosedRunTypingCases(window, check);
        PendingToolbarCases(window, check);
        // Two spaces typed inside the pair are one gap; the next word moves both inside.
        Load(window, "**alpha**");
        var source = Source(window);
        var line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset - 2, 0);
        Pump();
        TextInput(window, " ");
        TextInput(window, " ");
        TextInput(window, "b");
        check("two spaces at the closing edge move inside with the next word",
            source.Document.Text == "**alpha  b**" && Flags(Session(window), source.Document.GetLineByNumber(1), "b") == "B---",
            "text=[" + source.Document.Text + "], flags=" + Flags(Session(window), source.Document.GetLineByNumber(1), "b"));
        // Moving the caret between the space and the character drops the state: the character is
        // written where the caret is now, outside the pair, exactly as before the feature.
        Load(window, "**ab**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, " ");
        source.Select(line.Offset, 0);
        Pump();
        source.Select(source.Document.GetLineByNumber(1).EndOffset, 0);
        Pump();
        TextInput(window, "c");
        check("a caret move after the space leaves the next character unstyled",
            source.Document.Text == "**ab** c" && Flags(Session(window), source.Document.GetLineByNumber(1), "c") == "----",
            "text=[" + source.Document.Text + "], flags=" + Flags(Session(window), source.Document.GetLineByNumber(1), "c"));
        // Enter drops it as well.
        Load(window, "**ab**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, " ");
        TextInput(window, "\n");
        TextInput(window, "c");
        check("Enter after the space drops the state",
            source.Document.Text == "**ab** \nc" && Flags(Session(window), source.Document.GetLineByNumber(2), "c") == "----",
            "text=[" + Flat(source.Document.Text) + "], flags=" + Flags(Session(window), source.Document.GetLineByNumber(2), "c"));
    }
    // Types `phrase` one character per PerformTextInput at the line's end over `seed`, then checks
    // the source, the drawn style and that one Ctrl+Z leaves a source the view still draws as
    // markup (Redo brings the phrase back): the space's move is one history entry, never a half
    // marker.
    static void Phrase(MainWindow window, Action<string,bool,string> check, string name, string seed, string expected, string flags, double size, int caretBack) {
        const string phrase = "alpha beta gamma";
        Load(window, seed);
        SourceEditor source = Source(window);
        DocumentLine line = source.Document.GetLineByNumber(1);
        string seedVisible = VisibleLine(Session(window), line);
        // caretBack 0: the caret on the closed run's outer edge; more: inside the pair (the run
        // being typed), that many source characters before the line's end.
        source.Select(line.EndOffset - caretBack, 0);
        Pump();
        // The space first (it is the character the user types between the words), then the phrase
        // one character per input.
        TextInput(window, " ");
        string fault = null;
        for (int i = 0; i < phrase.Length && fault == null; i++) {
            TextInput(window, phrase.Substring(i, 1));
            string visible = VisibleLine(Session(window), source.Document.GetLineByNumber(1));
            if (MarkupFault(visible) != null) fault = visible;
        }
        line = source.Document.GetLineByNumber(1);
        var session = Session(window);
        string drawn = VisibleLine(session, line);
        bool ok = fault == null && source.Document.Text == expected && drawn == seedVisible + " " + phrase &&
            Flags(session, line, "gamma") == flags && (size <= 0 || Math.Abs(DrawnSize(window, OffsetOf(source, line, "gamma")) - size) < .01);
        check("typing the phrase at the end of " + name + " keeps it styled",
            ok,
            "text=[" + source.Document.Text + "], expected=[" + expected + "], visible=[" + drawn + "], fault=[" + fault +
            "], flags=" + Flags(session, line, "gamma") + (size > 0 ? ", drawn=" + N(DrawnSize(window, OffsetOf(source, line, "gamma"))) : ""));
        string expectedText = source.Document.Text;
        Undo(window);
        string undone = source.Document.Text;
        bool sane = SourceFault(undone) == null && MarkupFault(VisibleLine(Session(window), source.Document.GetLineByNumber(1))) == null;
        Invoke(window, "Redo");
        Pump();
        check("one undo and redo around " + name + " keep real markup",
            sane && source.Document.Text == expectedText,
            "undone=[" + undone + "], sane=" + sane + ", redone=[" + source.Document.Text + "]");
    }

    // ---------------------------------------------------------------- typing follows the toolbar
    // 11-05 user decision: with the caret on a closed styled run's outer edge the toolbar shows the
    // style lit (FormatAt reads the pair's inside), and a typed character, an IME commit included,
    // takes that style: `**굵게**|` + X is `**굵게X**`. A space typed there stays outside and plain
    // and the toolbar shows bold off after it. Basic (markers hidden) and live (revealed caret line).
    static void ClosedRunTypingCases(MainWindow window, Action<string,bool,string> check) {
        string[] seeds = { "**굵게**", "*기울임*", "~~지움~~", "***둘***", "<font color=\"blue\">파랑</font>", "<span style=\"font-size:20px\">크게</span>" };
        string[] wanted = { "**굵게X**", "*기울임X*", "~~지움X~~", "***둘X***", "<font color=\"blue\">파랑X</font>", "<span style=\"font-size:20px\">크게X</span>" };
        string[] flags = { "B---", "-I--", "--S-", "BI--", "----", "----" };
        for (int pass = 0; pass < 2; pass++) {
            bool basic = pass == 0;
            string mode = basic ? "basic" : "live";
            HideMarkers(window, basic);
            for (int i = 0; i < seeds.Length; i++) {
                Load(window, seeds[i]);
                SourceEditor source = Source(window);
                source.Select(source.Document.GetLineByNumber(1).EndOffset, 0);
                Pump();
                bool lit = ToolbarBold(window);
                TextInput(window, "X");
                int x = source.Document.Text.IndexOf('X');
                check(mode + " closed run " + seeds[i] + ": the typed character takes the lit style",
                    source.Document.Text == wanted[i] && x > 0 && FlagsAt(Session(window), x + 1) == flags[i] && (lit == (i == 0 || i == 3)),
                    "text=[" + source.Document.Text + "], flags=" + (x > 0 ? FlagsAt(Session(window), x + 1) : "-") + ", boldLit=" + lit);
                Undo(window);
                check(mode + " closed run " + seeds[i] + ": one undo takes the character back",
                    source.Document.Text == seeds[i], "text=[" + source.Document.Text + "]");
            }
            // Inline code on the revealed line: the toolbar does not light code on the outer edge,
            // so the character stays outside it (R7). With markers hidden code keeps its older
            // inside answer (InputFormatProbe "basic overlap typing at the drawn end code").
            if (!basic) {
                Load(window, "`코드`");
                SourceEditor codeSource = Source(window);
                codeSource.Select(codeSource.Document.GetLineByNumber(1).EndOffset, 0);
                Pump();
                TextInput(window, "X");
                check(mode + " closed code span: the typed character stays outside the code",
                    codeSource.Document.Text == "`코드`X", "text=[" + codeSource.Document.Text + "]");
            }
            // Rule B: a space after the closed bold is outside and plain, the toolbar shows bold off,
            // and the character after it is plain too.
            Load(window, "**굵게**");
            SourceEditor spaceSource = Source(window);
            spaceSource.Select(spaceSource.Document.GetLineByNumber(1).EndOffset, 0);
            Pump();
            TextInput(window, " ");
            bool boldOff = !ToolbarBold(window);
            TextInput(window, "X");
            check(mode + " closed bold: the space is plain and bold is off after it",
                spaceSource.Document.Text == "**굵게** X" && boldOff, "text=[" + spaceSource.Document.Text + "], boldOff=" + boldOff);
            // IME commits: the normal path and the two that bypass TextEntering.
            foreach (string route in new[] { "normal", "redirected", "late" }) {
                Load(window, "**굵게**");
                SourceEditor editor = Source(window);
                editor.Select(editor.Document.GetLineByNumber(1).EndOffset, 0);
                Pump();
                int at = editor.CaretOffset;
                editor.SimulateImeCommit(1);
                if (route != "normal") {
                    typeof(SourceEditor).GetField("imeCommitTarget", Members).SetValue(editor, at);
                    typeof(SourceEditor).GetField("imeCommitTargetDocument", Members).SetValue(editor, editor.Document);
                    typeof(SourceEditor).GetField("imeCommitTargetTick", Members).SetValue(editor, Environment.TickCount);
                }
                bool accepted = true;
                if (route == "late") { Keyboard.ClearFocus(); accepted = editor.AcceptLateImeCommit("한"); }
                else editor.TextArea.PerformTextInput("한");
                bool released = !editor.ImeBusy;
                editor.ResetCompositionSimulation();
                Pump();
                int h = editor.Document.Text.IndexOf('한');
                check(mode + " " + route + " IME commit after closed bold takes the bold",
                    accepted && released && editor.Document.Text == "**굵게한**" && h > 0 && FlagsAt(Session(window), h) == "B---",
                    "text=[" + editor.Document.Text + "], accepted=" + accepted + ", released=" + released);
                Undo(window);
                check(mode + " " + route + " IME commit after closed bold undoes in one step",
                    editor.Document.Text == "**굵게**", "text=[" + editor.Document.Text + "]");
            }
            // 11-06: the caret stays INSIDE the pair after the commit, so the next Korean syllable of the
            // same composition (and of the next one) is bold too: `**굵게**` + 한 + 글 is `**굵게한글**`
            // with the caret before the closer (it used to end after the closer, and 글 came out plain).
            foreach (string route in new[] { "normal", "redirected", "late" }) {
                Load(window, "**굵게**");
                SourceEditor editor = Source(window);
                editor.Select(editor.Document.GetLineByNumber(1).EndOffset, 0);
                Pump();
                int at = editor.CaretOffset;
                editor.SimulateImeCommit(2);
                if (route != "normal") {
                    typeof(SourceEditor).GetField("imeCommitTarget", Members).SetValue(editor, at);
                    typeof(SourceEditor).GetField("imeCommitTargetDocument", Members).SetValue(editor, editor.Document);
                    typeof(SourceEditor).GetField("imeCommitTargetTick", Members).SetValue(editor, Environment.TickCount);
                }
                bool accepted = true;
                if (route == "late") Keyboard.ClearFocus();
                int caretAfterFirst = -1;
                foreach (string syllable in new[] { "한", "글" }) {
                    if (route == "late") accepted = editor.AcceptLateImeCommit(syllable) && accepted;
                    else editor.TextArea.PerformTextInput(syllable);
                    if (syllable == "한") caretAfterFirst = editor.CaretOffset;
                }
                bool released = !editor.ImeBusy;
                int caretAfter = editor.CaretOffset;
                editor.ResetCompositionSimulation();
                Pump();
                string text2 = editor.Document.Text;
                int h2 = text2.IndexOf('한'), g2 = text2.IndexOf('글');
                check(mode + " " + route + " two Korean syllables after closed bold stay inside the bold",
                    accepted && released && text2 == "**굵게한글**" && h2 > 0 && g2 > 0 && FlagsAt(Session(window), h2) == "B---" && FlagsAt(Session(window), g2) == "B---",
                    "text=[" + text2 + "], accepted=" + accepted + ", released=" + released);
                check(mode + " " + route + " the caret stays inside the closing markers after each syllable",
                    caretAfterFirst == "**굵게한".Length && caretAfter == "**굵게한글".Length,
                    "caretAfterFirst=" + caretAfterFirst + ", caretAfter=" + caretAfter + ", text=[" + text2 + "]");
            }
        }
        HideMarkers(window, true);
    }

    // 11-05 rule C and the toolbar: while the run is being typed, the space typed at its closing edge is
    // pending and the next character takes it back inside, so the button of that style must be lit
    // right after the space (no tool refresh is called here: the pending state itself asks for it),
    // and off again once the caret leaves.
    static void PendingToolbarCases(MainWindow window, Action<string,bool,string> check) {
        string[] seeds = { "**abc**", "*abc*", "~~abc~~", "***abc***" };
        int[] back = { 2, 1, 2, 3 };
        string[] buttons = { "boldButton", "italicButton", "strikeButton", "boldButton" };
        string[] second = { null, null, null, "italicButton" };
        for (int i = 0; i < seeds.Length; i++) {
            Load(window, seeds[i]);
            SourceEditor source = Source(window);
            source.Select(source.Document.GetLineByNumber(1).EndOffset - back[i], 0);
            Pump();
            TextInput(window, " ");
            bool lit = Object.Equals(Field<Button>(window, buttons[i]).Tag, "Active") &&
                (second[i] == null || Object.Equals(Field<Button>(window, second[i]).Tag, "Active"));
            bool spaceOutside = source.Document.Text == seeds[i] + " ";
            source.Select(0, 0);
            Pump();
            bool litAfterMove = Object.Equals(Field<Button>(window, buttons[i]).Tag, "Active");
            check("pending closing-edge space " + seeds[i] + ": the toolbar shows the style lit, and off after the caret moves away",
                lit && spaceOutside && !litAfterMove,
                "text=[" + source.Document.Text + "], lit=" + lit + ", litAfterMove=" + litAfterMove);
        }
    }

    // ---------------------------------------------------------------- overlapping inline styles
    // Editing strictly inside two or three exactly-overlapping emphasis pairs. Every fixture's
    // middle visible characters carry the whole overlap ("***", "** * ... * **", "~~***...***~~"),
    // so a character typed between them must keep that set and the hidden pairs on both sides must
    // stay whole. The same seven inputs run on the basic surface (Settings.HideMarkers, every
    // marker drawn as nothing) and the live surface (HideMarkers false, the caret line is source
    // text). Enter now follows one rule on both: InlineEnter closes and reopens every enclosing
    // pair (LiveMarkdownCommands.cs), and only a caret strictly inside a marker run - literal
    // source the live view shows - keeps the plain newline. Both surfaces must undo in one step.
    // The two fixtures with an inner pair (fixtures 2 and 3) select the two visible characters on
    // both sides of the hidden opener, which is the only style boundary their visible text has;
    // the other fixtures style every visible character alike, so their selection is the middle of
    // that one run.
    static void OverlapCases(MainWindow window, Action<string,bool,string> check) {
        string[] seeds = {
            "***굵은기울임***",
            "**굵게 *기울임* 굵게**",
            "*기울임 **굵게** 기울임*",
            "_**밑줄기울임굵게**_",
            "~~***세겹***~~"
        };
        string[] names = { "triple", "bold around italic", "italic around bold", "underscore around bold", "strike around triple" };
        string[] words = { "굵은기울임", "기울임", "굵게", "밑줄기울임굵게", "세겹" };
        int[] into = { 2, 1, 1, 3, 1 };
        string[] flags = { "BI--", "BI--", "BI--", "BI--", "BIS-" };
        bool[] across = { false, true, true, false, false };
        for (int pass = 0; pass < 2; pass++) {
            bool basic = pass == 0;
            HideMarkers(window, basic);
            string mode = basic ? "basic" : "live";
            for (int f = 0; f < seeds.Length; f++) {
                string name = mode + " overlap " + names[f];
                Load(window, seeds[f]);
                SourceEditor source = Source(window);
                DocumentLine line = source.Document.GetLineByNumber(1);
                MarkdownLivePreview.Session session = Session(window);
                int word = OffsetOf(source, line, words[f]);
                int caret = word < 0 ? -1 : word + into[f];
                string left = word < 0 ? "?" : FlagsAt(session, word + into[f] - 1);
                string right = word < 0 ? "?" : FlagsAt(session, word + into[f]);
                check(name + " fixture styles the characters at the caret",
                    word >= 0 && left == flags[f] && right == flags[f],
                    "word=" + word + ", caret=" + caret + ", left=" + left + ", right=" + right + ", wanted=" + flags[f]);
                if (caret < 0) continue;
                OverlapTyped(window, check, name + " Latin", seeds[f], caret, flags[f], false);
                OverlapTyped(window, check, name + " Korean", seeds[f], caret, flags[f], true);
                OverlapDelete(window, check, name + " Backspace", seeds[f], caret, flags[f], true);
                OverlapDelete(window, check, name + " Delete", seeds[f], caret, flags[f], false);
                OverlapEnter(window, check, name + " Enter", seeds[f], caret, flags[f]);
                OverlapPaste(window, check, name + " paste", seeds[f], caret, flags[f]);
                OverlapSelection(window, check, name + " selection", seeds[f], word, into[f], flags[f], across[f], basic);
            }
        }
        HideMarkers(window, true);
    }
    // One typed character between two visible characters of the run: one plain insert, no marker
    // touched, the whole overlap at the typed offset, one undo.
    static void OverlapTyped(MainWindow window, Action<string,bool,string> check, string name, string seed, int caret, string flags, bool korean) {
        Load(window, seed);
        SourceEditor source = Source(window);
        source.Select(caret, 0);
        Pump();
        bool released = true;
        if (korean) {
            // The probe's composed-syllable commit: the IME pending state plus the committed text,
            // exactly the path the window's own IME result takes.
            source.SimulateImeCommit(1);
            TextInput(window, "한");
            released = !source.ImeBusy;
            source.ResetCompositionSimulation();
            Pump();
        } else TextInput(window, "X");
        string typed = korean ? "한" : "X";
        string expected = seed.Insert(caret, typed);
        string text = source.Document.Text;
        MarkdownLivePreview.Session session = Session(window);
        string fault = text == expected ? LineFault(session, source.Document.GetLineByNumber(1)) : "text";
        check(name + " typed character keeps the overlap",
            released && text == expected && fault == null && BalanceFault(text) == null && !Doubled(text) && FlagsAt(session, caret) == flags,
            "text=[" + text + "], expected=[" + expected + "], flags=" + FlagsAt(session, caret) + ", wanted=" + flags +
            ", fault=" + fault + ", balance=" + BalanceFault(text) + ", released=" + released);
        Undo(window);
        check(name + " typed character one undo", source.Document.Text == seed, "text=[" + source.Document.Text + "]");
    }
    // Backspace or Delete between two visible characters: one glyph goes, the characters now on
    // both sides keep the whole overlap, no marker is taken, one undo.
    static void OverlapDelete(MainWindow window, Action<string,bool,string> check, string name, string seed, int caret, string flags, bool back) {
        Load(window, seed);
        SourceEditor source = Source(window);
        source.Select(caret, 0);
        Pump();
        if (back) Backspace(window); else Delete(window);
        string expected = seed.Remove(back ? caret - 1 : caret, 1);
        string text = source.Document.Text;
        MarkdownLivePreview.Session session = Session(window);
        string fault = text == expected ? LineFault(session, source.Document.GetLineByNumber(1)) : "text";
        check(name + " takes one glyph and keeps both neighbours styled",
            text == expected && fault == null && BalanceFault(text) == null && !Doubled(text) &&
            FlagsAt(session, caret - 1) == flags && FlagsAt(session, caret) == flags,
            "text=[" + text + "], expected=[" + expected + "], left=" + FlagsAt(session, caret - 1) +
            ", right=" + FlagsAt(session, caret) + ", wanted=" + flags + ", fault=" + fault + ", balance=" + BalanceFault(text));
        Undo(window);
        check(name + " one undo", source.Document.Text == seed, "text=[" + source.Document.Text + "]");
    }
    // A plain paste at the same spot: one insert, the pasted character carries the overlap, one undo.
    static void OverlapPaste(MainWindow window, Action<string,bool,string> check, string name, string seed, int caret, string flags) {
        Load(window, seed);
        SourceEditor source = Source(window);
        source.Select(caret, 0);
        Pump();
        bool pasted = (bool)Invoke(window, "LivePaste", new System.Windows.DataObject(), true, "P");
        Pump();
        string expected = seed.Insert(caret, "P");
        string text = source.Document.Text;
        MarkdownLivePreview.Session session = Session(window);
        string fault = text == expected ? LineFault(session, source.Document.GetLineByNumber(1)) : "text";
        check(name + " plain paste keeps the overlap",
            pasted && text == expected && fault == null && BalanceFault(text) == null && !Doubled(text) && FlagsAt(session, caret) == flags,
            "text=[" + text + "], expected=[" + expected + "], flags=" + FlagsAt(session, caret) +
            ", wanted=" + flags + ", fault=" + fault + ", balance=" + BalanceFault(text) + ", pasted=" + pasted);
        Undo(window);
        check(name + " one undo", source.Document.Text == seed, "text=[" + source.Document.Text + "]");
    }
    // Enter between two visible characters. Both surfaces share InlineEnter now: the window splits
    // each enclosing pair, so the two lines read as the caret's visible prefix and suffix, the pair
    // edge characters keep the whole overlap and no marker is left as text. A caret strictly inside
    // a marker run is the one spot that keeps the plain newline, and no fixture puts it there.
    static void OverlapEnter(MainWindow window, Action<string,bool,string> check, string name, string seed, int caret, string flags) {
        Load(window, seed);
        SourceEditor source = Source(window);
        DocumentLine line = source.Document.GetLineByNumber(1);
        MarkdownLivePreview.Session before = Session(window);
        int relative = caret - line.Offset;
        string prefix = VisibleRange(before, line, 0, relative);
        string suffix = VisibleRange(before, line, relative, line.Length);
        source.Select(caret, 0);
        Pump();
        Enter(window);
        string text = source.Document.Text;
        bool ok;
        string detail;
        if (source.Document.LineCount < 2) {
            ok = false;
            detail = "text=[" + Flat(text) + "], lineCount=" + source.Document.LineCount;
        } else {
            MarkdownLivePreview.Session after = Session(window);
            DocumentLine first = source.Document.GetLineByNumber(1);
            DocumentLine second = source.Document.GetLineByNumber(2);
            string firstVisible = VisibleLine(after, first);
            string secondVisible = VisibleLine(after, second);
            int firstAt = LastVisibleOffset(after, first);
            int secondAt = FirstVisibleOffset(after, second);
            ok = source.Document.LineCount == 2 && firstVisible == prefix && secondVisible == suffix &&
                LineFault(after, first) == null && LineFault(after, second) == null &&
                BalanceFault(text) == null && !Doubled(text) &&
                firstAt >= 0 && secondAt >= 0 && FlagsAt(after, firstAt) == flags && FlagsAt(after, secondAt) == flags;
            detail = "text=[" + Flat(text) + "], visible=[" + firstVisible + "]/[" + secondVisible + "], wanted=[" + prefix + "]/[" + suffix +
                "], edge flags=" + (firstAt < 0 ? "?" : FlagsAt(after, firstAt)) + "/" + (secondAt < 0 ? "?" : FlagsAt(after, secondAt)) +
                ", wanted=" + flags + ", balance=" + BalanceFault(text);
        }
        check(name + " splits every pair and keeps the full style set", ok, detail);
        Undo(window);
        check(name + " one undo", source.Document.Text == seed, "text=[" + Flat(source.Document.Text) + "]");
    }
    // Two visible characters with the hidden opener between them replaced by one character. With
    // the markers hidden the window must keep the pairs whole around the replacement. On the live
    // surface a selection over a marker is literal source text the user sees, so that same span is
    // replaced as typed; only selections with no marker inside are held to the pair rule there.
    static void OverlapSelection(MainWindow window, Action<string,bool,string> check, string name, string seed, int word, int into, string flags, bool across, bool basic) {
        Load(window, seed);
        SourceEditor source = Source(window);
        DocumentLine line = source.Document.GetLineByNumber(1);
        MarkdownLivePreview.Session session = Session(window);
        int from, to;
        if (across) {
            from = PreviousVisibleOffset(session, line, word - 1);
            to = word + 1;
        } else {
            from = word + into - 1;
            to = from + 2;
        }
        if (from < 0 || to > line.EndOffset) {
            check(name + " typing over two visible characters keeps the marker pairs whole", false,
                "selection=[" + from + "," + to + ") outside the fixture");
            return;
        }
        source.Select(from, to - from);
        Pump();
        TextInput(window, "Z");
        string text = source.Document.Text;
        MarkdownLivePreview.Session after = Session(window);
        bool strict = basic || !across;
        bool ok;
        string detail;
        if (strict) {
            string fault = LineFault(after, source.Document.GetLineByNumber(1));
            string expected = seed.Remove(from, to - from).Insert(from, "Z");
            ok = fault == null && BalanceFault(text) == null && !Doubled(text);
            if (!across) ok &= text == expected && FlagsAt(after, from) == flags;
            detail = "selection=[" + from + "," + to + "), text=[" + text + "], flags=" + (from < text.Length ? FlagsAt(after, from) : "?") +
                ", wanted=" + (across ? "-" : flags) + ", fault=" + fault + ", balance=" + BalanceFault(text) +
                (across ? "" : ", expected=[" + expected + "]");
        } else {
            ok = text == seed.Remove(from, to - from).Insert(from, "Z");
            detail = "selection=[" + from + "," + to + "), text=[" + text + "], expected=[" + seed.Remove(from, to - from).Insert(from, "Z") + "], raw caret-line rule";
        }
        check(name + (strict ? " typing over two visible characters keeps the marker pairs whole" : " replaces the raw selection literally"), ok, detail);
        Undo(window);
        check(name + " typing over a selection one undo", source.Document.Text == seed, "text=[" + source.Document.Text + "]");
    }
    static void HideMarkers(MainWindow window, bool hidden) {
        Static("hideMarkers", hidden);
        Invoke(window, "UpdateLivePreview");
        Pump();
    }
    static string LineFault(MarkdownLivePreview.Session session, DocumentLine line) {
        string visible = VisibleLine(session, line);
        string fault = MarkupFault(visible);
        if (fault == null && visible.IndexOf('_') >= 0) fault = "'_'";
        return fault;
    }
    // Marker counts odd on one line, or a tag left unpaired: the same parse rule SourceFault reads,
    // with the underscore pair the new fixtures add.
    static string BalanceFault(string text) {
        string fault = SourceFault(text);
        if (fault != null) return fault;
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int n = 0; n < lines.Length; n++)
            if (Count(lines[n], '_') % 2 != 0) return "line " + (n + 1) + " odd '_' count";
        return null;
    }
    static bool Doubled(string text) { return text != null && text.IndexOf("****", StringComparison.Ordinal) >= 0; }
    static bool SameMarkerCounts(string before, string after) {
        return Count(before, '*') == Count(after, '*') && Count(before, '_') == Count(after, '_') &&
            Count(before, '~') == Count(after, '~') && Count(before, '`') == Count(after, '`');
    }
    static bool HiddenAt(MarkdownLivePreview.LineInfo info, int relative) {
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Kind != MarkdownLivePreview.ReplacementKind.Hidden || replacement.Length <= 0) continue;
            if (relative >= replacement.Start && relative < replacement.Start + replacement.Length) return true;
        }
        return false;
    }
    // The visible characters of [from,to) on the line, in the same projection VisibleLine uses.
    static string VisibleRange(MarkdownLivePreview.Session session, DocumentLine line, int from, int to) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        string value = info.Text;
        var text = new StringBuilder();
        int at = 0;
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Start >= value.Length) break;
            int start = replacement.Start;
            if (start > at) for (int i = at; i < start; i++) if (i >= from && i < to) text.Append(value[i]);
            int end = Math.Min(value.Length, start + replacement.Length);
            if (end > at) at = end;
        }
        for (int i = at; i < value.Length; i++) if (i >= from && i < to) text.Append(value[i]);
        return text.ToString();
    }
    static int LastVisibleOffset(MarkdownLivePreview.Session session, DocumentLine line) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        for (int i = info.Text.Length - 1; i >= 0; i--) if (!HiddenAt(info, i)) return line.Offset + i;
        return -1;
    }
    static int FirstVisibleOffset(MarkdownLivePreview.Session session, DocumentLine line) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        for (int i = 0; i < info.Text.Length; i++) if (!HiddenAt(info, i)) return line.Offset + i;
        return -1;
    }
    static int PreviousVisibleOffset(MarkdownLivePreview.Session session, DocumentLine line, int relative) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        for (int i = Math.Min(relative, info.Text.Length - 1); i >= 0; i--) if (!HiddenAt(info, i)) return line.Offset + i;
        return -1;
    }

    // Review regressions: use the app history, not AvalonEdit's disabled undo stack.
    static void ReviewCases(MainWindow window, Action<string,bool,string> check) {
        string span="<span style=\"color:red\">";
        string[] splits={"**|~~ab~~**","**ab *|cd* ef**",span+"**ab|cd**</span>","**~~ab|cd~~**"};
        string[] expected={"\n**~~ab~~**","**ab** \n***cd* ef**",span+"**ab**</span>\n"+span+"**cd**</span>","**~~ab~~**\n**~~cd~~**"};
        for(int c=0;c<splits.Length;c++) {
            int caret=splits[c].IndexOf('|');
            string seed=splits[c].Remove(caret,1);
            Load(window,seed);
            SourceEditor source=Source(window);
            source.Select(caret,0);Pump();Enter(window);
            bool hidden=true;
            foreach(DocumentLine line in source.Document.Lines)
                hidden&=MarkupFault(VisibleLine(Session(window),line))==null;
            check("review1 nested Enter "+c,source.Document.Text==expected[c]&&hidden&&SourceFault(source.Document.Text)==null,
                "text=["+Flat(source.Document.Text)+"], hidden="+hidden);
            Undo(window);
            check("review1 nested Enter undo "+c,source.Document.Text==seed,"text=["+Flat(source.Document.Text)+"]");
        }
        const string color="<span style=\"color:#FF0000\">**bold**</span>";
        Load(window,color);
        SourceEditor editor=Source(window);
        int word=color.IndexOf("bold",StringComparison.Ordinal);
        CommandResult unwrapped=MarkdownCommands.SetSpanColor(editor.Document,word+1,0,null);
        if(unwrapped!=null)Apply(window,unwrapped);
        check("review2 removing color preserves b|old caret",unwrapped!=null&&editor.Document.Text=="**bold**"&&editor.CaretOffset==3,
            "text=["+editor.Document.Text+"], caret="+editor.CaretOffset);

        string[] wrappers={"<span style=\"font-size:20px\">**한글**</span>","prefix <span style=\"font-size:20px\">**한글**</span> tail"};
        foreach(string seed in wrappers) {
            Load(window,seed);editor=Source(window);
            int at=seed.IndexOf("한글",StringComparison.Ordinal);
            bool replaced=Session(window).ReplaceVisible(at,at+2,"");Pump();
            string target=seed.StartsWith("prefix",StringComparison.Ordinal)?"prefix  tail":"";
            check("review6 empty replacement removes nested wrappers",replaced&&editor.Document.Text==target,
                "text=["+editor.Document.Text+"]");
            Undo(window);
            check("review6 empty replacement undo restores wrappers",editor.Document.Text==seed,"text=["+editor.Document.Text+"]");
        }
        string table="| a | b |\n| --- | --- |\n| c | d |";
        foreach(bool above in new[]{false,true}) foreach(bool ime in new[]{false,true}) {
            string seed=above?"before\n\n\n"+table+"\ntail":table+"\n\nafter";
            Load(window,seed);editor=Source(window);
            int at=above?"before\n\n".Length:table.Length+1;
            editor.Select(at,0);Pump();
            string text=ime?"한":"x";
            if(ime)editor.SimulateImeCommit(1);
            TextInput(window,text);
            bool released=!editor.ImeBusy;
            if(ime)editor.ResetCompositionSimulation();
            string wanted=seed.Insert(at,above?text+"\n":"\n"+text);
            check("review4 table gap "+(above?"above":"below")+(ime?" IME":" char"),editor.Document.Text==wanted&&released&&!editor.Document.IsInUpdate,
                "text=["+Flat(editor.Document.Text)+"], released="+released);
            Undo(window);
            check("review4 table gap one undo",editor.Document.Text==seed,"text=["+Flat(editor.Document.Text)+"]");
        }
    }

    // Review X8: each closing-edge move is one app-history action, and commits which arrive
    // before the preview's TextEntering handler must take the same pending space inside.
    static void ReviewSpaceCommitCases(MainWindow window, Action<string,bool,string> check) {
        const string seed="**ab**";
        Load(window,seed);
        SourceEditor editor=Source(window);
        // 11-05: the pending closing-edge space belongs to the run being typed, so the caret starts
        // inside the pair (rule C); on the outer edge of a closed bold the space is plain (rule B).
        editor.Select(seed.Length-2,0);Pump();
        TextInput(window," ");TextInput(window,"c");Backspace(window);
        check("x8 backspace moves the space without exposing bold markers",editor.Document.Text=="**ab** ","text=["+editor.Document.Text+"]");
        string[] undone={"**ab c**","**ab** ",seed};
        foreach(string expected in undone) {
            Undo(window);
            check("x8 space/character/backspace undo exactly one action",editor.Document.Text==expected,"text=["+editor.Document.Text+"], expected=["+expected+"]");
        }
        for(int i=0;i<3;i++){Invoke(window,"Redo");Pump();}
        check("x8 space/character/backspace three redos restore result",editor.Document.Text=="**ab** ","text=["+editor.Document.Text+"]");
        foreach(string route in new[]{"normal","redirected","late"}) {
            bool late=route=="late";
            foreach(string initial in new[]{seed,"<span style=\"font-size:20px\">**ab**</span>"}) {
                Load(window,initial);editor=Source(window);
                editor.Select(initial.IndexOf("ab",StringComparison.Ordinal)+2,0);Pump();TextInput(window," ");
                string spaced=editor.Document.Text;
                int at=editor.CaretOffset;
                editor.SimulateImeCommit(1);
                if(route!="normal") {
                    typeof(SourceEditor).GetField("imeCommitTarget",Members).SetValue(editor,at);
                    typeof(SourceEditor).GetField("imeCommitTargetDocument",Members).SetValue(editor,editor.Document);
                    typeof(SourceEditor).GetField("imeCommitTargetTick",Members).SetValue(editor,Environment.TickCount);
                }
                bool accepted=true;
                if(late)Keyboard.ClearFocus();
                if(late)accepted=editor.AcceptLateImeCommit("한");
                else editor.TextArea.PerformTextInput("한");
                bool released=!editor.ImeBusy;
                editor.ResetCompositionSimulation();Pump();
                string expected=initial.Replace("ab","ab 한");
                check("x8 "+route+" IME commit keeps pending bold/size",accepted&&released&&editor.Document.Text==expected&&Flags(Session(window),editor.Document.GetLineByNumber(1),"한")=="B---",
                    "text=["+editor.Document.Text+"], accepted="+accepted+", released="+released);
                Undo(window);
                check("x8 IME pending-space move undoes in one step",editor.Document.Text==spaced,"text=["+editor.Document.Text+"]");
            }
        }
        // Detaching must remove the actual delegates, not merely decrement a diagnostic count.
        var old=Session(window);var area=Source(window).TextArea;
        Invoke(window,"DetachLivePreview",true);
        var handlers=(Delegate)typeof(ICSharpCode.AvalonEdit.Editing.TextArea).GetField("TextEntering",Members).GetValue(area);
        bool detached=true;
        if(handlers!=null)foreach(Delegate handler in handlers.GetInvocationList())if(Object.ReferenceEquals(handler.Target,old))detached=false;
        handlers=(Delegate)typeof(ICSharpCode.AvalonEdit.Editing.TextArea).GetField("TextEntered",Members).GetValue(area);
        if(handlers!=null)foreach(Delegate handler in handlers.GetInvocationList())if(Object.ReferenceEquals(handler.Target,old))detached=false;
        check("x8 disposed session leaves no TextEntering/TextEntered delegates",detached,"detached="+detached);
        Invoke(window,"UpdateLivePreview");Pump();
    }

    // ---------------------------------------------------------------- markers shown (the old rule)
    static void ShownCases(MainWindow window, Action<string,bool,string> check) {
        Static("hideMarkers", false);
        Invoke(window, "UpdateLivePreview");
        Pump();
        var session = Session(window);
        check("markers toggle back to the reveal rule", session != null && !session.Options.HideMarkers,
            "hide=" + (session == null ? "-" : session.Options.HideMarkers.ToString()));
        if (session == null) return;

        // The caret line keeps its raw source, the other lines stay formatted and parse inside a span.
        Load(window, "caret line here\n<span style=\"font-size:20px\">**굵은스팬**</span> tail");
        var source = Source(window);
        session = Session(window);
        var first = source.Document.GetLineByNumber(1);
        var second = source.Document.GetLineByNumber(2);
        source.Select(0, 0);
        Pump();
        session = Session(window);
        string visible = VisibleLine(session, second);
        check("a non-caret line still parses markup inside a span",
            session.Raw(first) && !session.Raw(second) && visible == "굵은스팬 tail" && MarkupFault(visible) == null &&
            Flags(session, second, "굵은스팬") == "B---",
            "raw=" + session.Raw(first) + "/" + session.Raw(second) + ", visible=[" + visible + "], flags=" + Flags(session, second, "굵은스팬"));

        // 11-05: the revealed caret line follows the toolbar too. FormatAt shows bold lit with the
        // caret after the visible closing "**", so a typed character there is written inside the
        // pair (it used to stay after the visible markers, R7); the markers stay whole.
        Load(window, "**굵게**");
        source = Source(window);
        var line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset, 0);
        Pump();
        TextInput(window, "X");
        check("with markers shown the typed character at the closing edge takes the lit style",
            source.Document.Text == "**굵게X**", "text=[" + source.Document.Text + "]");

        // The same for Backspace: the visible "*" is the character before the caret.
        Load(window, "**굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 6, 0);
        Pump();
        Backspace(window);
        check("with markers shown Backspace takes the marker character",
            source.Document.Text == "**굵게*", "text=[" + source.Document.Text + "]");

        // With the markers shown the key shares InlineEnter: a caret on the closing markers'
        // inner edge steps outside the pair first, then the plain newline is written there - the
        // same result the no-reveal view gives at this offset.
        Load(window, "**굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 4, 0);
        Pump();
        Enter(window);
        check("with markers shown Enter at the closing markers' inner edge moves outside first",
            source.Document.Text == "**굵게**\n", "text=[" + Flat(source.Document.Text) + "]");

        // The size command still normalizes: the selection is source text either way.
        Load(window, "**작성 팁**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(OffsetOf(source, line, "작성 팁"), 4);
        Invoke(window, "SetFontSize", 20.0);
        Pump();
        check("with markers shown the size span still wraps outside the bold",
            source.Document.Text == "<span style=\"font-size:20px\">**작성 팁**</span>",
            "text=[" + source.Document.Text + "]");

        Static("hideMarkers", true);
        Invoke(window, "UpdateLivePreview");
        Pump();
        session = Session(window);
        check("markers hide again for the next run", session != null && session.Options.HideMarkers,
            "hide=" + (session == null ? "-" : session.Options.HideMarkers.ToString()));
    }
    // ---------------------------------------------------------------- hidden structure (F2-F4)
    // Basic view: a fence line, a view switch and an inline marker pair are structure the
    // no-reveal mode draws as nothing. Each check drives the window's own key and paste paths.
    static void StructureCases(MainWindow window, Action<string,bool,string> check) {
        HideMarkers(window, true);

        // F2: a caret that names an offset inside a hidden fence line moves to the line on the
        // side it came from, so typed text and Backspace/Delete never rewrite the fence.
        string fenced = "앞 문단\n\n```js\nvar a = 1;\n```\n\n뒷 문단";
        Load(window, fenced);
        SourceEditor source = Source(window);
        DocumentLine open = source.Document.GetLineByNumber(3);
        DocumentLine body = source.Document.GetLineByNumber(4);
        DocumentLine close = source.Document.GetLineByNumber(5);
        // Stand on the paragraph above first: the snap moves the caret to the side it came from.
        source.Select(source.Document.GetLineByNumber(1).EndOffset, 0);
        Pump();
        source.Select(open.Offset + 2, 0);
        Pump();
        bool movedOff = source.CaretIndex != open.Offset && source.CaretIndex != open.EndOffset;
        TextInput(window, "X");
        check("typing on a hidden fence line lands on the code line, not in the fence",
            movedOff && source.Document.Text == fenced.Insert(body.Offset, "X"),
            "caret=" + source.CaretIndex + ", want=" + body.Offset + ", text=[" + Flat(source.Document.Text) + "]");
        Undo(window);
        check("typing on a hidden fence line is one undo", source.Document.Text == fenced, "text=[" + Flat(source.Document.Text) + "]");
        source = Source(window);
        close = source.Document.GetLineByNumber(5);
        body = source.Document.GetLineByNumber(4);
        int below = close.EndOffset + close.DelimiterLength;
        // Stand on the code line first so the fence snap has an unambiguous "came from above".
        source.Select(body.EndOffset, 0);
        Pump();
        source.Select(close.Offset + 1, 0);
        Pump();
        TextInput(window, "Y");
        check("typing on a hidden closing fence lands below the block",
            source.Document.Text == fenced.Substring(0, below) + "Y" + fenced.Substring(below),
            "caret=" + source.CaretIndex + ", want=" + below + ", text=[" + Flat(source.Document.Text) + "]");
        Undo(window);

        // The joins at a fence boundary, on a block with nothing between the paragraph and the
        // fence (the case a blank neighbour line would hide). The key there would join a fence
        // line to its neighbour or eat a marker character, so it takes both fence lines of the
        // block and keeps the code line as ordinary text (MarkdownCommands.DeleteFenceBlock).
        string tight = "앞 문단\n```js\nvar a = 1;\n```\n뒷 문단";
        string unTight = "앞 문단\nvar a = 1;\n뒷 문단";
        Load(window, tight);
        source = Source(window);
        source.Select(source.Document.GetLineByNumber(1).EndOffset, 0);
        Pump();
        Delete(window);
        check("Delete above a fence takes both fence lines and keeps the code line",
            source.Document.Text == unTight, "text=[" + Flat(source.Document.Text) + "]");
        Load(window, tight);
        source = Source(window);
        source.Select(source.Document.GetLineByNumber(3).EndOffset, 0);
        Pump();
        Delete(window);
        check("Delete at the last code line takes both fence lines and keeps the code line",
            source.Document.Text == unTight, "text=[" + Flat(source.Document.Text) + "]");
        Load(window, tight);
        source = Source(window);
        source.Select(source.Document.GetLineByNumber(5).Offset, 0);
        Pump();
        Backspace(window);
        check("Backspace under a closing fence takes both fence lines and keeps the code line",
            source.Document.Text == unTight, "text=[" + Flat(source.Document.Text) + "]");
        MarkdownLivePreview.Session tightSession = Session(window);
        bool tightPlain = tightSession != null &&
            !tightSession.Info(source.Document.GetLineByNumber(2)).FenceLine &&
            !tightSession.Info(source.Document.GetLineByNumber(2)).Code;
        check("no fence line or code block is left after the boundary keys", tightPlain,
            "text=[" + Flat(source.Document.Text) + "]");

        // F5: a fence line is structure. A selection that covers only one fence of a block must
        // not be typed or pasted over: the partner fence would stay behind (an opening fence
        // without its closer turns the rest of the document into code, a closing fence without
        // its opener becomes literal text). Delete, Backspace and Cut are the exception: they
        // take both fence lines of the block and keep the rest of its text (FenceSelectionDelete,
        // 11-06 user), the same text change the toolbar code-block toggle-off makes. Only a
        // selection holding both fences of the block is ordinary text.
        string fencedBlock = "앞\n\n```js\nvar a = 1;\n```\n\n뒤";
        string unFencedBlock = "앞\n\nvar a = 1;\n\n뒤";
        Load(window, fencedBlock);
        source = Source(window);
        DocumentLine fenceOpen = source.Document.GetLineByNumber(3);
        DocumentLine fenceClose = source.Document.GetLineByNumber(5);
        int fenceOpenStart = fenceOpen.Offset, fenceOpenLength = fenceOpen.Length;
        int fenceCloseStart = fenceClose.Offset, fenceCloseLength = fenceClose.Length;
        source.Select(fenceOpenStart, fenceOpenLength);
        Pump();
        TextInput(window, "X");
        check("typing over a lone hidden opening fence is swallowed",
            source.Document.Text == fencedBlock, "text=[" + Flat(source.Document.Text) + "]");
        Load(window, fencedBlock);
        source = Source(window);
        source.Select(fenceOpenStart, fenceOpenLength);
        Pump();
        Delete(window);
        check("Delete over a lone hidden opening fence takes both fences and keeps the code",
            source.Document.Text == unFencedBlock, "text=[" + Flat(source.Document.Text) + "]");
        Load(window, fencedBlock);
        source = Source(window);
        source.Select(fenceCloseStart, fenceCloseLength);
        Pump();
        Delete(window);
        check("Delete over a lone hidden closing fence takes both fences and keeps the code",
            source.Document.Text == unFencedBlock, "text=[" + Flat(source.Document.Text) + "]");
        // Cut removes the same range as Delete and puts the selection on the clipboard first. A
        // preview handler on Copy stands in for the clipboard here (the probe leaves the user's
        // clipboard untouched), so the captured text is what the cut would copy: the selection,
        // never the fence lines the rule takes along.
        Load(window, fencedBlock);
        source = Source(window);
        source.Select(fenceOpenStart, fenceOpenLength);
        Pump();
        string fenceCutCopy = null;
        ExecutedRoutedEventHandler fenceCopyCapture = delegate(object sender, ExecutedRoutedEventArgs args) {
            if (args.Command != ApplicationCommands.Copy) return;
            args.Handled = true;
            fenceCutCopy = source.SelectedText;
        };
        ExecutedRoutedEventArgs fenceCutArgs = CommandArguments(ApplicationCommands.Cut);
        MethodInfo fenceCut = typeof(MarkdownLivePreview.Session).GetMethod("HiddenMarkerDelete", Members);
        CommandManager.AddPreviewExecutedHandler(source.TextArea, fenceCopyCapture);
        try { fenceCut.Invoke(Session(window), new object[] { source.TextArea, fenceCutArgs }); }
        finally { CommandManager.RemovePreviewExecutedHandler(source.TextArea, fenceCopyCapture); }
        check("Cut over a lone hidden fence takes both fences and copies only the selection",
            fenceCutArgs.Handled && source.Document.Text == unFencedBlock &&
            fenceCutCopy == fencedBlock.Substring(fenceOpenStart, fenceOpenLength),
            "handled=" + fenceCutArgs.Handled + ", copy=[" + Flat(fenceCutCopy) + "], text=[" + Flat(source.Document.Text) + "]");
        Load(window, fencedBlock);
        source = Source(window);
        source.Select(fenceOpenStart, fenceOpenLength);
        Pump();
        ExecutedRoutedEventArgs fencePasteArgs = CommandArguments(ApplicationCommands.Paste);
        MethodInfo fencePaste = typeof(MarkdownLivePreview.Session).GetMethod("HiddenPasteWiden", Members);
        fencePaste.Invoke(Session(window), new object[] { source.TextArea, fencePasteArgs });
        check("the paste command over a lone hidden fence is swallowed",
            fencePasteArgs.Handled && source.Document.Text == fencedBlock,
            "handled=" + fencePasteArgs.Handled + ", text=[" + Flat(source.Document.Text) + "]");
        bool fenceCtrlV = (bool)Invoke(window, "LivePaste", new System.Windows.DataObject(), true, "P");
        check("the app's Ctrl+V over a lone hidden fence is swallowed",
            fenceCtrlV && source.Document.Text == fencedBlock,
            "handled=" + fenceCtrlV + ", text=[" + Flat(source.Document.Text) + "]");
        source.Select(fenceOpenStart, fenceCloseStart + fenceCloseLength - fenceOpenStart);
        Pump();
        string wholeBlockBefore = source.Document.Text;
        TextInput(window, "X");
        check("typing over a whole hidden block takes both fences",
            wholeBlockBefore == fencedBlock && source.Document.Text == "앞\n\nX\n\n뒤",
            "text=[" + Flat(source.Document.Text) + "]");
        Undo(window);
        check("the whole-block typing is one undo", source.Document.Text == fencedBlock, "text=[" + Flat(source.Document.Text) + "]");

        // F3: the view switch does not move the caret by itself, so a caret left inside a marker
        // the basic view now hides must be pulled out before the next character.
        HideMarkers(window, false);
        Load(window, "**굵게**");
        source = Source(window);
        DocumentLine line = source.Document.GetLineByNumber(1);
        source.Select(line.Offset + 1, 0);
        Pump();
        HideMarkers(window, true);
        MarkdownLivePreview.LineInfo info = Session(window).Info(line);
        int caret = source.CaretIndex;
        bool inside = caret > line.Offset && caret < line.EndOffset && HiddenRange(info, caret - line.Offset, 1);
        TextInput(window, "X");
        check("switching to basic moves a caret out of an opening marker",
            !inside && source.Document.Text == "X**굵게**",
            "caret=" + caret + ", inside=" + inside + ", text=[" + source.Document.Text + "]");
        HideMarkers(window, false);
        Load(window, "**굵게**");
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        source.Select(line.EndOffset - 1, 0);
        Pump();
        HideMarkers(window, true);
        TextInput(window, "X");
        check("switching to basic moves a caret out of a closing marker",
            source.Document.Text == "**굵게X**", "text=[" + source.Document.Text + "]");

        // F4: a selection that fully covers a hidden opener (or part of one) would leave its
        // partner behind as literal text, so the pair goes with it. Typing over the visible text
        // keeps the pair, which is what the markers are for.
        const string pair = "a **굵게** b";
        HideMarkers(window, true);
        Load(window, pair);
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        int opener = OffsetOf(source, line, "**");
        source.Select(opener, 2);
        Pump();
        TextInput(window, "Z");
        check("typing over a hidden opener takes the whole pair", source.Document.Text == "a Z b",
            "text=[" + source.Document.Text + "]");
        Load(window, pair);
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        opener = OffsetOf(source, line, "**");
        source.Select(opener + 1, 1);
        Pump();
        TextInput(window, "Z");
        check("typing over part of a hidden marker takes the whole pair", source.Document.Text == "a Z b",
            "text=[" + source.Document.Text + "]");
        Load(window, pair);
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        int textAt = OffsetOf(source, line, "굵게");
        source.Select(textAt, 2);
        Pump();
        TextInput(window, "Z");
        check("typing over the pair's text keeps both markers", source.Document.Text == "a **Z** b",
            "text=[" + source.Document.Text + "]");
        // The paste path widens the selection first (PrepareHiddenPaste, which HiddenPasteWiden
        // and the host's Ctrl+V share); the same call stands in for that stage so the check
        // needs no clipboard.
        Load(window, pair);
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        opener = OffsetOf(source, line, "**");
        source.Select(opener, 2);
        Pump();
        typeof(MarkdownLivePreview.Session).GetMethod("PrepareHiddenPaste", Members).Invoke(Session(window), null);
        Pump();
        Invoke(window, "LivePaste", new System.Windows.DataObject(), true, "Z");
        Pump();
        check("pasting over a hidden opener takes the whole pair", source.Document.Text == "a Z b",
            "text=[" + source.Document.Text + "]");
        // The pair growth only runs in the no-reveal mode: a live selection over a marker is
        // source text the user can see, so it is replaced literally.
        HideMarkers(window, false);
        Load(window, pair);
        source = Source(window);
        line = source.Document.GetLineByNumber(1);
        opener = OffsetOf(source, line, "**");
        source.Select(opener, 2);
        Pump();
        typeof(MarkdownLivePreview.Session).GetMethod("WidenHiddenSelection", Members).Invoke(Session(window), null);
        Pump();
        check("a live selection over a marker stays literal", source.SelectionStart == opener && source.SelectionLength == 2,
            "selection=" + source.SelectionStart + "+" + source.SelectionLength);
        HideMarkers(window, true);
    }
    // ---------------------------------------------------------------- CommonMark blocks and inline
    // The visible text per line, in both settings (basic: every line formatted, live: the caret
    // line raw). The same rules the print and TXT renderers read, so the three outputs agree.
    // LoadSettled adds one dispatcher frame: the reference-definition scan is queued by the
    // parse (never run inside it) and lands before the checks read a line.
    static void LoadSettled(MainWindow window, string text) { Load(window, text); Pump(); }
    static void RenderingCases(MainWindow window, Action<string,bool,string> check) {
        foreach (bool hidden in new[] { true, false }) {
            HideMarkers(window, hidden);
            string mode = hidden ? "basic" : "live";
            // ATX closing sequence: only a run set off by a space is structure.
            LoadSettled(window, "## 제목 ##\n### C#\n## ###\n\n본문\n");
            SourceEditor source = Source(window);
            MarkdownLivePreview.Session session = Session(window);
            if (session == null) { check("rendering " + mode + " session", false, "no live session"); return; }
            string closing1 = VisibleLine(session, source.Document.GetLineByNumber(1));
            string closing2 = VisibleLine(session, source.Document.GetLineByNumber(2));
            string closing3 = VisibleLine(session, source.Document.GetLineByNumber(3));
            check("rendering " + mode + " ATX closing sequence",
                closing1 == "제목" && closing2 == "C#" && closing3 == "" &&
                session.Info(source.Document.GetLineByNumber(1)).Heading == 2 &&
                MarkdownPlainText.Convert("## 제목 ##").Trim() == "제목" &&
                MarkdownPlainText.Convert("### C#").Trim() == "C#",
                "[" + closing1 + "]/[" + closing2 + "]/[" + closing3 + "] txt=[" +
                MarkdownPlainText.Convert("## 제목 ##").Trim() + "]/[" + MarkdownPlainText.Convert("### C#").Trim() + "]");

            // Named entities decode like numeric ones.
            LoadSettled(window, "&copy; &reg; &trade; &mdash; &ndash; &hellip; &rarr; &larr; &laquo;Q&raquo; &middot; &times; &deg;\n" +
                "&amp; &lt; &gt; &quot; &apos; &nbsp;\n");
            source = Source(window); session = Session(window);
            string named = VisibleLine(session, source.Document.GetLineByNumber(1));
            string basic = VisibleLine(session, source.Document.GetLineByNumber(2));
            check("rendering " + mode + " named entities",
                named == "© ® ™ — – … → ← «Q» · × °" && basic == "& < > \" '  ",
                "[" + named + "]/[" + basic + "]");

            // "_ _ _" and "___" are thematic breaks; a "---" under a paragraph stays its heading.
            LoadSettled(window, "위 문단\n\n___\n\n_ _ _\n\n***\n\n제목\n---\n");
            source = Source(window); session = Session(window);
            bool rules = session.Info(source.Document.GetLineByNumber(3)).Rule &&
                session.Info(source.Document.GetLineByNumber(5)).Rule &&
                session.Info(source.Document.GetLineByNumber(7)).Rule;
            bool setext = session.Info(source.Document.GetLineByNumber(9)).Heading == 2 &&
                session.Info(source.Document.GetLineByNumber(10)).SetextUnderline == 2 &&
                !session.Info(source.Document.GetLineByNumber(10)).Rule;
            check("rendering " + mode + " thematic breaks", rules && setext,
                "rules=" + rules + ", setext=" + setext);

            // A lazy continuation line stays in the quote; a block start ends it.
            LoadSettled(window, "> 첫 줄\n이어지는 줄\n\n> # 제목\n본문\n\n> 항목\n- 목록\n");
            source = Source(window); session = Session(window);
            bool quote = session.Info(source.Document.GetLineByNumber(1)).Quote &&
                session.Info(source.Document.GetLineByNumber(2)).Quote &&
                session.Info(source.Document.GetLineByNumber(2)).LazyQuote &&
                !session.Info(source.Document.GetLineByNumber(3)).Quote &&
                session.Info(source.Document.GetLineByNumber(4)).Quote &&
                !session.Info(source.Document.GetLineByNumber(5)).Quote &&
                session.Info(source.Document.GetLineByNumber(7)).Quote &&
                !session.Info(source.Document.GetLineByNumber(8)).Quote;
            check("rendering " + mode + " lazy quote continuation", quote,
                "1/2/3=" + session.Info(source.Document.GetLineByNumber(1)).Quote + "/" +
                session.Info(source.Document.GetLineByNumber(2)).Quote + "/" +
                session.Info(source.Document.GetLineByNumber(3)).Quote + " lazy=" +
                session.Info(source.Document.GetLineByNumber(2)).LazyQuote);

            // A link target with balanced parentheses and a title.
            LoadSettled(window, "[위키](https://example.com/a_(b))\n[설계서](https://example.com/spec \"3장\")\n");
            source = Source(window); session = Session(window);
            DocumentLine wiki = source.Document.GetLineByNumber(1), spec = source.Document.GetLineByNumber(2);
            string wikiVisible = VisibleLine(session, wiki), specVisible = VisibleLine(session, spec);
            string wikiUrl = session.LinkAt(wiki.Offset + 1), specUrl = session.LinkAt(spec.Offset + 1);
            check("rendering " + mode + " link target",
                wikiVisible == "위키" && wikiUrl == "https://example.com/a_(b)" &&
                specVisible == "설계서" && specUrl == "https://example.com/spec",
                "[" + wikiVisible + "]=" + wikiUrl + ", [" + specVisible + "]=" + specUrl);

            // A shortcut reference link needs a definition; without one the brackets stay text.
            LoadSettled(window, "설명은 [설계 문서] 참고.\n[설계 문서]: https://example.com/spec\n\n[없음] 표시\n");
            source = Source(window); session = Session(window);
            DocumentLine shortcut = source.Document.GetLineByNumber(1), missing = source.Document.GetLineByNumber(4);
            string shortcutVisible = VisibleLine(session, shortcut), missingVisible = VisibleLine(session, missing);
            int shortcutAt = OffsetOf(source, shortcut, "설계 문서");
            int missingAt = OffsetOf(source, missing, "없음");
            check("rendering " + mode + " shortcut reference",
                shortcutVisible == "설명은 설계 문서 참고." && shortcutAt >= 0 && session.LinkAt(shortcutAt) == "ref:설계 문서" &&
                missingVisible == "[없음] 표시" && (missingAt < 0 || session.LinkAt(missingAt) == null),
                "[" + shortcutVisible + "]=" + (shortcutAt < 0 ? "?" : session.LinkAt(shortcutAt)) +
                ", [" + missingVisible + "]");
        }
        HideMarkers(window, true);
    }
    // ---------------------------------------------------------------- window helpers
    static void Load(MainWindow window, string text) {
        Invoke(window, "SetText", text);
        Pump();
    }
    static SourceEditor Source(MainWindow window) { return Field<SourceEditor>(window, "source"); }
    static MarkdownLivePreview.Session Session(MainWindow window) { return Field<MarkdownLivePreview.Session>(window, "livePreview"); }
    static int OffsetOf(SourceEditor source, DocumentLine line, string needle) {
        int index = source.Document.GetText(line).IndexOf(needle, StringComparison.Ordinal);
        return index < 0 ? -1 : line.Offset + index;
    }
    static string Flags(MarkdownLivePreview.Session session, DocumentLine line, string needle) {
        int index = session.Info(line).Text.IndexOf(needle, StringComparison.Ordinal);
        if (index < 0) return "?";
        return FlagsAt(session, line.Offset + index);
    }
    static string FlagsAt(MarkdownLivePreview.Session session, int offset) {
        bool bold, italic, strike, code;
        int heading;
        session.FormatAt(offset, out bold, out italic, out strike, out code, out heading);
        return (bold ? "B" : "-") + (italic ? "I" : "-") + (strike ? "S" : "-") + (code ? "C" : "-");
    }
    // The caret on the outer edge of the "**" that closes the needle's bold run.
    static bool EndOfVisible(MainWindow window, SourceEditor source, string needle) {
        DocumentLine line = source.Document.GetLineByNumber(1);
        int at = OffsetOf(source, line, needle) + needle.Length + 2;
        source.Select(at, 0);
        Pump();
        return SizeBox(window) == "20 px";
    }
    static double DrawnSize(MainWindow window, int offset) {
        SourceEditor source = Source(window);
        source.Select(Math.Max(0, Math.Min(offset, source.Document.TextLength)), 0);
        Pump();
        return (double)Invoke(window, "LiveDrawnSize");
    }
    // The Bold button's real lit state at the caret (what the user sees), after a tool refresh.
    static bool ToolbarBold(MainWindow window) {
        Invoke(window, "UpdateTools");
        Pump();
        return Object.Equals(Field<Button>(window, "boldButton").Tag, "Active");
    }
    static string SizeBox(MainWindow window) {
        Invoke(window, "UpdateTools");
        Pump();
        return Field<ComboBox>(window, "sizes").Text;
    }
    static void TextInput(MainWindow window, string text) {
        Source(window).TextArea.PerformTextInput(text);
        Pump();
    }
    static void Backspace(MainWindow window) {
        SourceEditor source = Source(window);
        EditingCommands.Backspace.Execute(null, source.TextArea);
        Pump();
    }
    static void Delete(MainWindow window) {
        SourceEditor source = Source(window);
        EditingCommands.Delete.Execute(null, source.TextArea);
        Pump();
    }
    // The window's own Enter path: SourceStructuralKeys runs SourceEnter and applies its result;
    // a null result (plain line) falls to AvalonEdit's newline input, exactly like the key.
    static void Enter(MainWindow window) {
        SourceEditor source = Source(window);
        var result = (CommandResult)Invoke(window, "SourceEnter");
        if (result == null) { source.TextArea.PerformTextInput("\n"); Pump(); return; }
        Apply(window, result);
    }
    static void Apply(MainWindow window, CommandResult result) {
        Invoke(window, "ApplyCommandResult", result, -1, 0);
        Pump();
    }
    // The paste handler's caret half: HiddenPasteWiden runs SnapHiddenPasteCaret on an empty
    // selection before the paste itself lands.
    static void PasteSnap(MainWindow window) {
        typeof(MarkdownLivePreview.Session).GetMethod("SnapHiddenPasteCaret", Members)
            .Invoke(Session(window), null);
        Pump();
    }
    // The two-stage key raise the other probes use: PreviewKeyDown (the window's structural
    // handler), then KeyDown when that handler declined.
    static bool PressKey(MainWindow window, Key key, int offset) {
        SourceEditor source = Source(window);
        source.Select(offset, 0);
        Pump();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), 0, key) { RoutedEvent=Keyboard.PreviewKeyDownEvent };
        source.TextArea.RaiseEvent(args);
        bool handled = args.Handled;
        Pump();
        if (!handled) {
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source), 0, key) { RoutedEvent=Keyboard.KeyDownEvent };
            source.TextArea.RaiseEvent(down);
            Pump();
        }
        return handled;
    }
    static void Undo(MainWindow window) {
        Invoke(window, "Undo");
        Pump();
    }

    // ---------------------------------------------------------------- source checks
    // One line's visible text from the line's own parse: replacement runs are skipped, a cell
    // gap and a <br> still separate what the eye sees (the rule AppendVisibleLine uses).
    static string VisibleLine(MarkdownLivePreview.Session session, DocumentLine line) {
        MarkdownLivePreview.LineInfo info = session.Info(line);
        string value = info.Text;
        var text = new StringBuilder();
        int at = 0;
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Start >= value.Length) break;
            int start = replacement.Start;
            if (start > at) text.Append(value, at, start - at);
            int end = Math.Min(value.Length, start + replacement.Length);
            if (end > at) at = end;
            if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Column) text.Append(' ');
            else if (replacement.Kind == MarkdownLivePreview.ReplacementKind.LineBreak) text.Append('\n');
            else if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Glyph && replacement.Label != null) text.Append(replacement.Label);
        }
        if (at < value.Length) text.Append(value, at, value.Length - at);
        return text.ToString();
    }
    // A piece of markup the surface still draws as text, or null when the line is clean.
    static string MarkupFault(string visible) {
        if (visible == null) return "null";
        if (visible.IndexOf('*') >= 0) return "'*'";
        if (visible.IndexOf('`') >= 0) return "backtick";
        if (visible.IndexOf('~') >= 0) return "'~'";
        if (visible.IndexOf("<span", StringComparison.OrdinalIgnoreCase) >= 0) return "<span";
        if (visible.IndexOf("</span", StringComparison.OrdinalIgnoreCase) >= 0) return "</span";
        if (visible.IndexOf("<font", StringComparison.OrdinalIgnoreCase) >= 0) return "<font";
        if (visible.IndexOf("</font", StringComparison.OrdinalIgnoreCase) >= 0) return "</font";
        if (visible.StartsWith("- ", StringComparison.Ordinal)) return "- prefix";
        return null;
    }
    // Orphan tags and half markers in the source itself: the same rules the parser needs.
    static string SourceFault(string text) {
        if (text == null) return "null";
        string[] lines = text.Replace("\r", "").Split('\n');
        for (int n = 0; n < lines.Length; n++) {
            string line = lines[n];
            int span = CountOf(line, "<span") - CountOf(line, "</span");
            int font = CountOf(line, "<font") - CountOf(line, "</font");
            if (span != 0) return "line " + (n + 1) + " span tags unbalanced";
            if (font != 0) return "line " + (n + 1) + " font tags unbalanced";
            if (Count(line, '*') % 2 != 0) return "line " + (n + 1) + " odd '*' count";
            if (Count(line, '~') % 2 != 0) return "line " + (n + 1) + " odd '~' count";
            if (Count(line, '`') % 2 != 0) return "line " + (n + 1) + " odd backtick count";
        }
        return null;
    }
    static int CountOf(string text, string piece) {
        int count = 0;
        for (int at = text.IndexOf(piece, StringComparison.Ordinal); at >= 0; at = text.IndexOf(piece, at + piece.Length, StringComparison.Ordinal)) count++;
        return count;
    }
    static int Count(string text, char value) {
        int count = 0;
        for (int i = 0; i < text.Length; i++) if (text[i] == value) count++;
        return count;
    }
    static int Pipes(string row) { return Count(row, '|'); }
    // True when [start,start+length) is inside a hidden replacement run (the marker or tag is
    // parsed as markup, so the surface draws nothing there).
    static bool HiddenRange(MarkdownLivePreview.LineInfo info, int start, int length) {
        if (start < 0) return false;
        foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) {
            if (replacement.Kind != MarkdownLivePreview.ReplacementKind.Hidden || replacement.Length <= 0) continue;
            if (replacement.Start <= start && replacement.Start + replacement.Length >= start + length) return true;
        }
        return false;
    }
    static string Flat(string text) { return text == null ? "null" : text.Replace("\r", "").Replace("\n", "\\n"); }
    static string N(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }

    // ---------------------------------------------------------------- reflection
    static void Pump() {
        LongLineBench.Idle();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(delegate { }));
        LongLineBench.Idle();
    }
    static T Field<T>(MainWindow window, string name) { return (T)typeof(MainWindow).GetField(name, Members).GetValue(window); }
    static void Set(MainWindow window, string name, object value) { typeof(MainWindow).GetField(name, Members).SetValue(window, value); }
    static T Prop<T>(MainWindow window, string name) { return (T)typeof(MainWindow).GetProperty(name, Members).GetValue(window, null); }
    static object Invoke(MainWindow window, string name, params object[] args) { return typeof(MainWindow).GetMethod(name, Members).Invoke(window, args); }
    // WPF keeps ExecutedRoutedEventArgs' constructor internal, so a probe builds one through
    // reflection (the same seam FormatAbuseProbe uses). The session's rules also set Handled when
    // they swallow a key, and RoutedEventArgs refuses that while RoutedEvent is null (the real
    // CommandManager always supplies one). PreviewExecuted is the event the session's handlers are
    // registered on (CommandManager.AddPreviewExecutedHandler); without it the first refused Cut
    // throws InvalidOperationException instead of reporting the checks.
    static ExecutedRoutedEventArgs CommandArguments(ICommand command) {
        ConstructorInfo ctor = typeof(ExecutedRoutedEventArgs).GetConstructor(Members, null, new Type[] { typeof(ICommand), typeof(object) }, null);
        if (ctor == null) return null;
        ExecutedRoutedEventArgs args = (ExecutedRoutedEventArgs)ctor.Invoke(new object[] { command, null });
        args.RoutedEvent = CommandManager.PreviewExecutedEvent;
        return args;
    }
    static T Static<T>(string name) { return (T)typeof(MainWindow).GetField(name, Members).GetValue(null); }
    static void Static(string name, object value) { typeof(MainWindow).GetField(name, Members).SetValue(null, value); }
}
}
