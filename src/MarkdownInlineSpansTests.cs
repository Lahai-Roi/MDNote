using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MDNote {
// --inline-spans-probe <outDir>: pure checks for MarkdownInlineSpans on single lines. Every
// case parses the line with the live preview's own inline parser (ParseInlineText), builds the
// span map and then runs Enter (StepOut + Split) or a selection replacement (Rebalance). The
// probe class is kept separate so helper names never land in MainWindow. Writes
// inline-spans-PASS.txt when every case passes, otherwise inline-spans-FAIL.txt with the
// failing cases, the same convention as the other probes.
internal static class MarkdownInlineSpansTests {
    internal static int Run(string outDir) {
        string dir;
        try {
            dir = Path.GetFullPath(outDir);
            Directory.CreateDirectory(dir);
        } catch (Exception) {
            return 2;
        }
        var failures = new List<string>();
        int cases = 0;
        int exit = 0;
        try {
            RunEnterCases(failures, ref cases);
            RunSelectCases(failures, ref cases);
            RunNestedRestoreCases(failures, ref cases);
            RunUnderscoreCases(failures, ref cases);
            RunBuildCases(failures, ref cases);
            RunCrCase(failures, ref cases);
            RunReviewFixCases(failures, ref cases);
        } catch (Exception ex) {
            exit = 2;
            failures.Add("EXCEPTION " + ex.GetType().Name + ": " + ex.Message);
        }
        try {
            string passFile = Path.Combine(dir, "inline-spans-PASS.txt");
            string failFile = Path.Combine(dir, "inline-spans-FAIL.txt");
            if (exit == 0 && failures.Count == 0) {
                if (File.Exists(failFile)) File.Delete(failFile);
                File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
            } else {
                if (File.Exists(passFile)) File.Delete(passFile);
                var report = new StringBuilder();
                for (int i = 0; i < failures.Count; i++) report.Append("FAIL ").Append(failures[i]).Append('\n');
                report.Append("\nCases: ").Append(cases.ToString(CultureInfo.InvariantCulture))
                    .Append("  FAIL: ").Append(failures.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                report.Append("Result: FAIL\n");
                File.WriteAllText(failFile, report.ToString(), new UTF8Encoding(false));
            }
        } catch (Exception) {
            exit = 2;
        }
        if (exit == 0 && failures.Count > 0) exit = 1;
        return exit;
    }

    static void RunEnterCases(List<string> failures, ref int cases) {
        // "m **[ab](#x)** n" with the caret on all four marker edges and inside the label.
        EnterCase(failures, ref cases, "enter link before opener", "m **[ab](#x)** n", 4, "m ", "**[ab](#x)** n");
        EnterCase(failures, ref cases, "enter link after bracket", "m **[ab](#x)** n", 5, "m ", "**[ab](#x)** n");
        EnterCase(failures, ref cases, "enter link inside label", "m **[ab](#x)** n", 6, "m **[a](#x)**", "**[b](#x)** n");
        EnterCase(failures, ref cases, "enter link before tail", "m **[ab](#x)** n", 7, "m **[ab](#x)**", " n");
        EnterCase(failures, ref cases, "enter crossed triple", "***ab***", 4, "***a***", "***b***");
        EnterCase(failures, ref cases, "enter adjacency after opener", "**a***b*", 2, "", "**a***b*");
        EnterCase(failures, ref cases, "enter adjacency inside closer run", "**a***b*", 4, "**a**", "*b*");
        EnterCase(failures, ref cases, "enter adjacency before neighbour", "**a***b*", 6, "**a**", "*b*");
        EnterCase(failures, ref cases, "enter adjacent bold strike", "**a**~~b~~", 5, "**a**", "~~b~~");
        EnterCase(failures, ref cases, "enter tag and bold",
            "<span style=\"color:red\">**ab**</span>", 27,
            "<span style=\"color:red\">**a**</span>", "<span style=\"color:red\">**b**</span>");
        EnterCase(failures, ref cases, "enter bold around code", "**`ab`**", 4, "**`a`**", "**`b`**");
        EnterCase(failures, ref cases, "enter padded code", "` a b `", 3, "` a`", "`b `");
        EnterCase(failures, ref cases, "enter reference link", "[ab][id]", 2, "[a][id]", "[b][id]");
        EnterCase(failures, ref cases, "enter right after link bracket", "m [ab](#x) n", 3, "m ", "[ab](#x) n");
        EnterCase(failures, ref cases, "enter plain bold", "m **ab** n", 5, "m **a**", "**b** n");
        // Table cells go through the same inline parse.
        EnterCase(failures, ref cases, "enter table cell bold", "| **ab** |", 5, true, "| **a**", "**b** |");
    }

    static void RunSelectCases(List<string> failures, ref int cases) {
        // Select-Z over "b** " (content b, closer, space): only the selected text goes, the
        // unselected a stays and the closer is restored after Z.
        SelectCase(failures, ref cases, "select replaces content and closer", "m **ab** n", 5, 9, "Z", "m **aZ**n", 6);
        // The whole content selected with the markers outside: the markers survive.
        SelectCase(failures, ref cases, "select replaces whole content", "m **ab** n", 4, 6, "Z", "m **Z** n", 5);
        // Deleting the whole content removes the empty pair ("****" never remains).
        SelectCase(failures, ref cases, "select deletes whole content", "m **ab** n", 4, 6, "", "m  n", 2);
        // Opener only inside. The text takes the format of the first selected character: a
        // selection that starts before the pair (visible "m a") keeps it plain and restores the
        // opener after it; one that starts on the opener (visible "a") puts it inside the pair.
        SelectCase(failures, ref cases, "select starting before opener keeps text plain", "m **ab** n", 0, 5, "Z", "Z**b** n", 1);
        SelectCase(failures, ref cases, "select starting on opener keeps text bold", "m **ab** n", 2, 5, "Z", "m **Zb** n", 5);
        SelectCase(failures, ref cases, "select deletes opener side", "m **ab** n", 2, 5, "", "m **b** n", 2);
        // Closer side mirror: a selection that begins on the closer (visible " n") is outside the pair.
        SelectCase(failures, ref cases, "select starting on closer keeps text plain", "m **ab** n", 6, 9, "Z", "m **ab**Zn", 9);
        // The same visible selection written with the markers outside or inside gives one result.
        SelectCase(failures, ref cases, "select whole pair with markers inside", "m **ab** n", 2, 8, "Z", "m **Z** n", 5);
        SelectCase(failures, ref cases, "select whole pair keeps blank text out", "m **ab** n", 2, 8, " ", "m   n", 3);
        // A selection across two pairs that took characters from both ends merges them.
        SelectCase(failures, ref cases, "select across two pairs merges", "**a** **b**", 2, 9, "Z", "**Z**", 3);
        // Nested pairs lose every empty level together, never "****".
        SelectCase(failures, ref cases, "select deletes nested content", "**~~a~~**", 4, 5, "", "", 0);
        SelectCase(failures, ref cases, "select replaces nested content", "**~~a~~**", 4, 5, "Z", "**~~Z~~**", 5);
        // A selection that runs from one line into the next is rebalanced as one joined line.
        JoinCase(failures, ref cases, "select across lines merges pairs", "m **ab** n", "p **cd** q", 5, 15, "Z", "m **aZd** q", 6);
        JoinCase(failures, ref cases, "select across lines deletes", "m **ab** n", "p **cd** q", 5, 15, "", "m **ad** q", 5);
        // Links: tail only, opener only, and the label emptied by a delete.
        SelectCase(failures, ref cases, "select replaces link tail", "m [ab](#x) n", 4, 10, "Z", "m [aZ](#x) n", 5);
        SelectCase(failures, ref cases, "select replaces link opener", "m [ab](#x) n", 2, 3, "Z", "m Z[ab](#x) n", 3);
        SelectCase(failures, ref cases, "select deletes link label", "m [ab](#x) n", 3, 5, "", "m  n", 2);
        // A tag whose content is deleted goes whole.
        SelectCase(failures, ref cases, "select deletes tag content",
            "<span style=\"color:red\">ab</span>", 24, 26, "", "", 0);
        // Both markers and characters outside the pair inside the selection: the pair disappears.
        SelectCase(failures, ref cases, "select replaces pair and its neighbour", "m **ab** n", 1, 8, "Z", "mZ n", 2);
        SelectCase(failures, ref cases, "select crossed triple closer", "***ab***", 4, 8, "Z", "***aZ***", 5);
        // A trailing space typed with the replacement moves outside the restored closer.
        SelectCase(failures, ref cases, "select replacement trailing space", "m **ab** n", 5, 9, "Z ", "m **aZ** n", 6);
        // The restored opener takes the whitespace after it with it (outside the pair).
        SelectCase(failures, ref cases, "select tag opener with space",
            "<span style=\"color:red\"> ab</span>", 0, 24, "Z",
            "Z <span style=\"color:red\">ab</span>", 1);
        // Only the gap between two pairs selected (visible " "): the new text is plain between them.
        SelectCase(failures, ref cases, "select gap between adjacent pairs", "**a** **b**", 3, 8, "Z", "**a**Z**b**", 6);
        SelectCase(failures, ref cases, "select deletes between adjacent pairs", "**a** **b**", 3, 8, "", "**ab**", 3);
        // A plain insertion (no selection) never repairs or removes a pair.
        SelectCase(failures, ref cases, "insert keeps pair", "**a**", 2, 2, "Z", "**Za**", 3);
        SelectCase(failures, ref cases, "no-op keeps pair", "**a**", 2, 2, "", "**a**", 2);
        // An atomic unit is replaced as plain text, never split or restored.
        SelectCase(failures, ref cases, "select replaces image", "m ![alt](x) n", 2, 11, "Z", "m Z n", 3);
    }

    // Nested pairs of different kinds: the restored closers keep their source order, inner to outer.
    static void RunNestedRestoreCases(List<string> failures, ref int cases) {
        SelectCase(failures, ref cases, "select restores link around bold in order", "m [**ab**](#x) n", 6, 15, "Z", "m [**aZ**](#x)n", 7);
        SelectCase(failures, ref cases, "select restores bold around code in order", "m **`ab`** n", 6, 11, "Z", "m **`aZ`**n", 7);
        string colored = "m [**<span style=\"color:#FF0000\">ab</span>**](#x) n";
        int start = colored.IndexOf("b</span>", StringComparison.Ordinal);
        int end = colored.IndexOf(") n", StringComparison.Ordinal) + 2;
        SelectCase(failures, ref cases, "select restores link bold tag in order", colored, start, end, "Z",
            "m [**<span style=\"color:#FF0000\">aZ</span>**](#x)n", colored.IndexOf("ab</span>", StringComparison.Ordinal) + 2);
    }

    // Underscore emphasis next to a word character stops parsing, so the pair is written with "*".
    static void RunUnderscoreCases(List<string> failures, ref int cases) {
        SelectCase(failures, ref cases, "select closer side rewrites underscore", "m _ab_ n", 4, 7, "Z", "m *aZ*n", 5);
        SelectCase(failures, ref cases, "select closer side rewrites double underscore", "m __ab__ n", 5, 9, "Z", "m **aZ**n", 6);
        SelectCase(failures, ref cases, "select opener side rewrites underscore", "m _ab_ n", 0, 3, "Z", "Z*ab* n", 1);
        SelectCase(failures, ref cases, "select delete rewrites underscore", "m _ab_ n", 4, 7, "", "m *a*n", 4);
        SelectCase(failures, ref cases, "select keeps a valid underscore pair", "m _ab_ n", 3, 5, "Z", "m _Z_ n", 4);
        SelectCase(failures, ref cases, "select keeps a valid underscore pair with space", "m _ab_ n", 4, 7, "Z ", "m _aZ_ n", 5);
        // Selection edits that leave a blank right inside a marker move the blank outside it.
        SelectCase(failures, ref cases, "select deletes last word keeps closer outside blank", "**a b**", 4, 5, "", "**a** ", 3);
        SelectCase(failures, ref cases, "select deletes first word keeps opener outside blank", "**a b**", 2, 3, "", " **b**", 3);
        SelectCase(failures, ref cases, "space over last character leaves the pair", "**ab**", 3, 4, " ", "**a** ", 6);
        SelectCase(failures, ref cases, "space over last character and closer leaves the pair", "**ab**", 3, 6, " ", "**a** ", 6);
        RepairCase(failures, ref cases, "backspace before opener repairs", "m _ab_ n", 1, 2, "", new int[] { 2, 5 });
        RepairCase(failures, ref cases, "delete after closer repairs", "m _ab_ n", 6, 7, "", new int[] { 2, 5 });
        RepairCase(failures, ref cases, "delete inside content repairs nothing", "m _ab_ n", 3, 4, "", new int[0]);
        RepairCase(failures, ref cases, "paste before opener repairs", "m _ab_ n", 2, 2, "P", new int[] { 2, 5 });
        RepairCase(failures, ref cases, "paste after closer repairs", "m _ab_ n", 6, 6, "P", new int[] { 2, 5 });
        RepairCase(failures, ref cases, "paste inside the pair repairs nothing", "m _ab_ n", 3, 3, "P", new int[0]);
        RepairCase(failures, ref cases, "paste of a space repairs nothing", "m _ab_ n", 2, 2, " ", new int[0]);
        RepairCase(failures, ref cases, "double underscore repairs both runs", "m __ab__ n", 1, 2, "", new int[] { 2, 3, 6, 7 });
        RepairCase(failures, ref cases, "star pair is never rewritten", "m **ab** n", 1, 2, "", new int[0]);
        Check(failures, ref cases, "rewrite replaces and rewrites",
            MarkdownInlineSpans.ReplaceWithRepairs("m _ab_ n", 1, 2, "", new List<int>(new int[] { 2, 5 })) == "m*ab* n", null);
        Check(failures, ref cases, "intraword opener", MarkdownInlineSpans.UnderscoreIntraword("m_ab_ n", 1, 1, true), null);
        Check(failures, ref cases, "spaced opener is fine", !MarkdownInlineSpans.UnderscoreIntraword("m _ab_ n", 2, 1, true), null);
        Check(failures, ref cases, "intraword closer", MarkdownInlineSpans.UnderscoreIntraword("m _ab_n", 5, 1, false), null);
        Check(failures, ref cases, "line edge is fine", !MarkdownInlineSpans.UnderscoreIntraword("_ab_", 0, 1, true) &&
            !MarkdownInlineSpans.UnderscoreIntraword("_ab_", 3, 1, false), null);
        Check(failures, ref cases, "punctuation neighbour is fine", !MarkdownInlineSpans.UnderscoreIntraword("(_ab_)", 1, 1, true) &&
            !MarkdownInlineSpans.UnderscoreIntraword("(_ab_)", 4, 1, false), null);
    }

    static void RepairCase(List<string> failures, ref int cases, string name, string text, int start, int end,
        string inserted, int[] expected) {
        List<int> got = MarkdownInlineSpans.UnderscoreRepairs(Build(text), text, start, end, inserted);
        bool ok = got.Count == expected.Length;
        for (int i = 0; ok && i < expected.Length; i++) ok = got[i] == expected[i];
        string detail = null;
        if (!ok) {
            var shown = new StringBuilder();
            for (int i = 0; i < got.Count; i++) shown.Append(i == 0 ? "" : ",").Append(got[i].ToString(CultureInfo.InvariantCulture));
            detail = "got [" + shown + "] expected " + expected.Length.ToString(CultureInfo.InvariantCulture) + " offsets";
        }
        Check(failures, ref cases, name, ok, detail);
    }

    static void RunBuildCases(List<string> failures, ref int cases) {
        SpanCase(failures, ref cases, "build crossed triple outer", "***ab***", 0,
            MarkdownInlineSpans.SpanKind.Emphasis, 0, 2, 5, 2);
        SpanCase(failures, ref cases, "build crossed triple inner", "***ab***", 1,
            MarkdownInlineSpans.SpanKind.Emphasis, 2, 1, 7, 1);
        SpanCase(failures, ref cases, "build bold strike", "**a**~~b~~", 1,
            MarkdownInlineSpans.SpanKind.Emphasis, 5, 2, 8, 2);
        SpanCase(failures, ref cases, "build code inside bold", "**`ab`**", 1,
            MarkdownInlineSpans.SpanKind.Code, 2, 1, 5, 1);
        SpanCase(failures, ref cases, "build tag pair", "<span style=\"color:red\">**ab**</span>", 0,
            MarkdownInlineSpans.SpanKind.Tag, 0, 24, 30, 7);
        SpanCase(failures, ref cases, "build inline link", "m [ab](#x) n", 0,
            MarkdownInlineSpans.SpanKind.Link, 2, 1, 5, 5);
        SpanCase(failures, ref cases, "build reference link", "[ab][id]", 0,
            MarkdownInlineSpans.SpanKind.Link, 0, 1, 3, 5);
        AtomicCase(failures, ref cases, "build footnote is atomic", "[^id]", 0, 5);
        AtomicCase(failures, ref cases, "build image is atomic", "![alt](x)", 0, 9);
        AtomicCase(failures, ref cases, "build autolink is atomic", "<https://x.y>", 0, 13);
        AtomicCase(failures, ref cases, "build comment is atomic", "m <!-- x --> n", 2, 10);
        PlainCase(failures, ref cases, "build plain line has no spans", "m ab n");

        // Atomic units step the caret to the nearer edge, in front on a tie.
        StepCase(failures, ref cases, "step footnote left edge", "[^id]", 2, 0);
        StepCase(failures, ref cases, "step footnote right edge", "[^id]", 4, 5);
        StepCase(failures, ref cases, "step image left edge", "![alt](x)", 4, 0);
        StepCase(failures, ref cases, "step autolink left edge", "<https://x.y>", 5, 0);
        StepCase(failures, ref cases, "step comment left edge", "m <!-- x --> n", 6, 2);
        // The link map names both sides of every marker and the replacement that is on it.
        SpanOfCase(failures, ref cases);
        // Enter trusts the parsed position on a marker and leaves plain text (and a literal,
        // unpaired run) to H9's raw-run snap.
        TouchCase(failures, ref cases, "touch boundary between closer and opener", "**a**~~b~~", 5, true);
        TouchCase(failures, ref cases, "touch inside closer run", "**a***b*", 4, true);
        TouchCase(failures, ref cases, "touch link tail edge", "m [ab](#x) n", 5, true);
        TouchCase(failures, ref cases, "touch plain text", "m ab n", 3, false);
        TouchCase(failures, ref cases, "touch inside content", "m **ab** n", 5, false);
        TouchCase(failures, ref cases, "touch literal run is not a marker", "a *** b", 4, false);
    }

    // Review defects: restored markers that touch cross trailing/leading whitespace together, and
    // Split never closes one side of a pair while the other side has nothing to reopen.
    static void RunReviewFixCases(List<string> failures, ref int cases) {
        // "m **~~ab~~** n": selecting "b~~** " widened to the markers, typing "Z " leaves the space
        // outside both restored closers (the outer bold must stay closed right after the strike).
        SelectCase(failures, ref cases, "select nested closers cross trailing space", "m **~~ab~~** n", 7, 13, "Z ", "m **~~aZ~~** n", 8);
        // Mirror: " Z" typed over "**~~a" puts the leading space in front of both restored openers.
        SelectCase(failures, ref cases, "select nested openers cross leading space", "m **~~ab~~** n", 2, 7, " Z", "m  **~~Zb~~** n", 8);
        // Single-pair controls stay as they were.
        SelectCase(failures, ref cases, "select single opener crosses leading space", "m **ab** n", 2, 5, " Z", "m  **Zb** n", 6);
        // Enter with the caret right behind a nested opener settles outside every pair.
        EnterCase(failures, ref cases, "enter after nested strike opener", "**~~ab~~**", 4, "", "**~~ab~~**");
        EnterCase(failures, ref cases, "enter after link opener inside bold", "**[ab](#x)**", 3, "", "**[ab](#x)**");
        // Split called directly with a caret that StepOut leaves alone (behind blanks).
        string blankLeft = "**  ab**";
        SplitCase(failures, ref cases, "split blank left side is no pair split", blankLeft, 4,
            new MarkdownInlineSpans.Span[] { Pair(0, 2, 6, 2, "**", blankLeft) }, false, null, null);
        string blankRight = "**ab  **";
        SplitCase(failures, ref cases, "split blank right side is no pair split", blankRight, 4,
            new MarkdownInlineSpans.Span[] { Pair(0, 2, 6, 2, "**", blankRight) }, false, null, null);
        string markerLeft = "**~~ab~~**";
        SplitCase(failures, ref cases, "split left side of only markers is no pair split", markerLeft, 4,
            new MarkdownInlineSpans.Span[] { Pair(0, 2, 8, 2, "**", markerLeft), Pair(2, 2, 6, 2, "~~", markerLeft) },
            false, null, null);
        string bothBlank = "**    **";
        SplitCase(failures, ref cases, "split blank pair is left alone", bothBlank, 4,
            new MarkdownInlineSpans.Span[] { Pair(0, 2, 6, 2, "**", bothBlank) }, false, null, null);
        string visible = "**ab cd**";
        SplitCase(failures, ref cases, "split visible both sides closes and reopens", visible, 5,
            new MarkdownInlineSpans.Span[] { Pair(0, 2, 7, 2, "**", visible) }, true, "**", "**");
    }

    static MarkdownInlineSpans.Span Pair(int openStart, int openLen, int closeStart, int closeLen, string marker, string text) {
        MarkdownInlineSpans.Span span = new MarkdownInlineSpans.Span();
        span.Kind = MarkdownInlineSpans.SpanKind.Emphasis;
        span.OpenStart = openStart;
        span.OpenLen = openLen;
        span.CloseStart = closeStart;
        span.CloseLen = closeLen;
        span.OpenText = marker;
        span.CloseText = marker;
        return span;
    }

    static void SplitCase(List<string> failures, ref int cases, string name, string text, int caret,
        MarkdownInlineSpans.Span[] spans, bool expected, string expectedClosing, string expectedOpening) {
        int editStart, editEnd;
        string closing, opening;
        bool got = MarkdownInlineSpans.Split(new List<MarkdownInlineSpans.Span>(spans), text, caret,
            out editStart, out editEnd, out closing, out opening);
        bool ok = got == expected;
        if (ok && expected) ok = closing == expectedClosing && opening == expectedOpening;
        if (ok && !expected) ok = closing.Length == 0 && opening.Length == 0;
        Check(failures, ref cases, name, ok,
            "got " + (got ? "split" : "no split") + " closing " + Describe(closing) + " opening " + Describe(opening));
    }

    static void TouchCase(List<string> failures, ref int cases, string name, string text, int caret, bool expected) {
        bool got = MarkdownInlineSpans.TouchesMarker(Build(text), caret);
        Check(failures, ref cases, name, got == expected, "got " + (got ? "yes" : "no") + " expected " + (expected ? "yes" : "no"));
    }

    static void RunCrCase(List<string> failures, ref int cases) {
        string text = "m **ab** n";
        MarkdownLivePreview.LineInfo info = MarkdownLivePreview.ParseInlineText(text, false);
        List<MarkdownInlineSpans.Span> spans = MarkdownInlineSpans.Build(info, text);
        int settled = MarkdownInlineSpans.StepOut(spans, 5);
        int editStart, editEnd;
        string closing, opening;
        bool split = MarkdownInlineSpans.Split(spans, text, settled, out editStart, out editEnd, out closing, out opening);
        bool ok = split && closing.IndexOf('\r') < 0 && opening.IndexOf('\r') < 0 && info.Text.IndexOf('\r') < 0;
        Check(failures, ref cases, "single line split writes no CR", ok, null);
    }

    static void EnterCase(List<string> failures, ref int cases, string name, string text, int caret,
        string expectedLeft, string expectedRight) {
        EnterCase(failures, ref cases, name, text, caret, false, expectedLeft, expectedRight);
    }

    static void EnterCase(List<string> failures, ref int cases, string name, string text, int caret, bool table,
        string expectedLeft, string expectedRight) {
        string detail;
        bool ok = RunEnter(text, caret, table, expectedLeft, expectedRight, out detail);
        Check(failures, ref cases, name, ok, detail);
    }

    static bool RunEnter(string text, int caret, bool table, string expectedLeft, string expectedRight, out string detail) {
        detail = null;
        MarkdownLivePreview.LineInfo info = MarkdownLivePreview.ParseInlineText(text, table);
        List<MarkdownInlineSpans.Span> spans = MarkdownInlineSpans.Build(info, text);
        int settled = MarkdownInlineSpans.StepOut(spans, caret);
        int editStart, editEnd;
        string closing, opening;
        bool split = MarkdownInlineSpans.Split(spans, text, settled, out editStart, out editEnd, out closing, out opening);
        string left, right;
        if (split) {
            left = text.Substring(0, editStart) + closing + text.Substring(editStart, settled - editStart);
            // The product's Enter drops what lies between the break and editEnd (the blanks after it,
            // LiveMarkdownCommands.InlineEnter), so the new line starts at the reopened marker.
            right = opening + text.Substring(editEnd);
        } else {
            left = text.Substring(0, settled);
            right = text.Substring(settled);
        }
        if (left == expectedLeft && right == expectedRight) return true;
        detail = "got " + Describe(left) + " | " + Describe(right) + " expected " +
            Describe(expectedLeft) + " | " + Describe(expectedRight) +
            " (settled " + settled.ToString(CultureInfo.InvariantCulture) + ", split " + (split ? "yes" : "no") + ")";
        return false;
    }

    static void SelectCase(List<string> failures, ref int cases, string name, string text,
        int start, int end, string inserted, string expected, int expectedCaret) {
        MarkdownLivePreview.LineInfo info = MarkdownLivePreview.ParseInlineText(text, false);
        List<MarkdownInlineSpans.Span> spans = MarkdownInlineSpans.Build(info, text);
        int caret;
        string result = MarkdownInlineSpans.Rebalance(spans, text, start, end, inserted, out caret);
        bool ok = result == expected && caret == expectedCaret;
        string detail = ok ? null : "got " + Describe(result) + " caret " + caret.ToString(CultureInfo.InvariantCulture) +
            " expected " + Describe(expected) + " caret " + expectedCaret.ToString(CultureInfo.InvariantCulture);
        Check(failures, ref cases, name, ok, detail);
    }

    // Two lines joined (first + last), the last line's spans moved right, then one Rebalance.
    static void JoinCase(List<string> failures, ref int cases, string name, string first, string last,
        int start, int end, string inserted, string expected, int expectedCaret) {
        List<MarkdownInlineSpans.Span> joined = MarkdownInlineSpans.Join(Build(first), Build(last), first.Length);
        int caret;
        string result = MarkdownInlineSpans.Rebalance(joined, first + last, start, end, inserted, out caret);
        bool ok = result == expected && caret == expectedCaret;
        string detail = ok ? null : "got " + Describe(result) + " caret " + caret.ToString(CultureInfo.InvariantCulture) +
            " expected " + Describe(expected) + " caret " + expectedCaret.ToString(CultureInfo.InvariantCulture);
        Check(failures, ref cases, name, ok, detail);
    }

    static void SpanCase(List<string> failures, ref int cases, string name, string text, int index,
        MarkdownInlineSpans.SpanKind kind, int openStart, int openLen, int closeStart, int closeLen) {
        List<MarkdownInlineSpans.Span> spans = Build(text);
        bool ok = spans.Count > index &&
            spans[index].Kind == kind &&
            spans[index].OpenStart == openStart && spans[index].OpenLen == openLen &&
            spans[index].CloseStart == closeStart && spans[index].CloseLen == closeLen;
        string detail = null;
        if (!ok) {
            detail = "spans " + spans.Count.ToString(CultureInfo.InvariantCulture);
            if (spans.Count > index) {
                detail += " first " + spans[index].Kind + " " +
                    spans[index].OpenStart.ToString(CultureInfo.InvariantCulture) + "+" +
                    spans[index].OpenLen.ToString(CultureInfo.InvariantCulture) + "-" +
                    spans[index].CloseStart.ToString(CultureInfo.InvariantCulture) + "+" +
                    spans[index].CloseLen.ToString(CultureInfo.InvariantCulture);
            }
        }
        Check(failures, ref cases, name, ok, detail);
    }

    static void AtomicCase(List<string> failures, ref int cases, string name, string text, int start, int length) {
        List<MarkdownInlineSpans.Span> spans = Build(text);
        bool ok = spans.Count == 1 &&
            spans[0].Kind == MarkdownInlineSpans.SpanKind.Atomic &&
            spans[0].OpenStart == start && spans[0].OpenLen == length;
        string detail = ok ? null : "spans " + spans.Count.ToString(CultureInfo.InvariantCulture);
        Check(failures, ref cases, name, ok, detail);
    }

    static void PlainCase(List<string> failures, ref int cases, string name, string text) {
        List<MarkdownInlineSpans.Span> spans = Build(text);
        Check(failures, ref cases, name, spans.Count == 0,
            spans.Count == 0 ? null : "spans " + spans.Count.ToString(CultureInfo.InvariantCulture));
    }

    static void StepCase(List<string> failures, ref int cases, string name, string text, int caret, int expected) {
        List<MarkdownInlineSpans.Span> spans = Build(text);
        int settled = MarkdownInlineSpans.StepOut(spans, caret);
        Check(failures, ref cases, name, settled == expected,
            settled == expected ? null : "settled " + settled.ToString(CultureInfo.InvariantCulture) +
            " expected " + expected.ToString(CultureInfo.InvariantCulture));
    }

    static void SpanOfCase(List<string> failures, ref int cases) {
        string text = "m **[ab](#x)** n";
        MarkdownLivePreview.LineInfo info = MarkdownLivePreview.ParseInlineText(text, false);
        List<MarkdownInlineSpans.Span> spans = MarkdownInlineSpans.Build(info, text);
        bool openerFound = false, closerFound = false;
        for (int i = 0; i < info.Replacements.Count; i++) {
            MarkdownLivePreview.Replacement run = info.Replacements[i];
            if (run.Kind != MarkdownLivePreview.ReplacementKind.Hidden || run.Length != 2) continue;
            bool isOpener;
            MarkdownInlineSpans.Span? found = MarkdownInlineSpans.SpanOf(spans, run, out isOpener);
            if (!found.HasValue || found.Value.Kind != MarkdownInlineSpans.SpanKind.Emphasis) continue;
            if (run.Start == 2 && isOpener && found.Value.CloseStart == 12) openerFound = true;
            if (run.Start == 12 && !isOpener && found.Value.OpenStart == 2) closerFound = true;
        }
        Check(failures, ref cases, "span lookup names both link-wrapped bold markers",
            openerFound && closerFound, null);
    }

    static List<MarkdownInlineSpans.Span> Build(string text) {
        MarkdownLivePreview.LineInfo info = MarkdownLivePreview.ParseInlineText(text, false);
        return MarkdownInlineSpans.Build(info, text);
    }

    static void Check(List<string> failures, ref int cases, string name, bool ok, string detail) {
        cases++;
        if (ok) return;
        failures.Add(name + (detail == null ? string.Empty : ": " + detail));
    }

    static string Describe(string value) {
        if (value == null) return "<null>";
        return value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }
}
}
