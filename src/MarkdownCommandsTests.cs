using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// Helpers live in their own class: names such as Inline/Task/Table would hide WPF types inside MainWindow.
internal static class MarkdownCommandsProbe {
    // --commands-probe <outDir>: pure MarkdownCommands checks. TextDocument needs no
    // dispatcher or window; every case builds a fresh document and applies the returned
    // edits in list order, exactly like the host will.
    internal static int Run(string outDir) {
        string dir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(dir);
        StringBuilder report = new StringBuilder();
        report.Append("# MarkdownCommands probe\n\n");
        bool all = true;
        int passed = 0;
        int failed = 0;
        int cases = 0;
        int exit = 0;
        try {
            List<CommandCase> list = BuildCommandCases();
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
            // The "#heading" rule the live jump shares with the basic view (AnchorRule): Korean
            // letters survive, text is trimmed and lower-cased, everything outside letters, digits,
            // '_', ' ' and '-' is dropped, and then every space becomes '-'.
            string[][] anchors = BuildAnchorCases();
            for (int i = 0; i < anchors.Length; i++) {
                cases++;
                string actual = anchors[i][0] == "target" ? AnchorRule.Target(anchors[i][1]) : AnchorRule.Slug(anchors[i][1]);
                if (actual == anchors[i][2]) {
                    passed++;
                    report.Append("PASS anchor ").Append(anchors[i][0]).Append(" \"").Append(anchors[i][1]).Append("\"\n");
                } else {
                    failed++;
                    all = false;
                    report.Append("FAIL anchor ").Append(anchors[i][0]).Append(" \"").Append(anchors[i][1])
                        .Append("\": expected \"").Append(anchors[i][2]).Append("\" actual \"").Append(actual).Append("\"\n");
                }
            }
            // The image reference rule the button and a drop share (bughunt4 P3-3): '#' and '?'
            // would start a fragment or a query for a Uri, a lone '%' is an invalid escape and a
            // space ends the target, so the reference percent-encodes all of them and both
            // renderers decode it before opening the file.
            string[][] references = BuildImageReferenceCases();
            for (int i = 0; i < references.Length; i++) {
                cases++;
                string actual = MarkdownCommands.ImageReference(references[i][0], references[i][1]);
                if (String.Equals(actual, references[i][2], StringComparison.Ordinal)) {
                    passed++;
                    report.Append("PASS image reference \"").Append(references[i][1]).Append("\"\n");
                } else {
                    failed++;
                    all = false;
                    report.Append("FAIL image reference \"").Append(references[i][1]).Append("\": expected \"")
                        .Append(references[i][2]).Append("\" actual \"").Append(actual).Append("\"\n");
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
                File.WriteAllText(Path.Combine(dir, "commands-probe.txt"), report.ToString(), new UTF8Encoding(false));
                string passFile = Path.Combine(dir, "commands-probe-PASS.txt");
                if (exit == 0 && all) File.WriteAllText(passFile, "PASS\n", new UTF8Encoding(false));
                else if (File.Exists(passFile)) File.Delete(passFile);
            } catch (Exception) {
                exit = 2;
            }
        }
        if (exit == 0 && !all) exit = 1;
        return exit;
    }

    sealed class CommandCase {
        internal string Name;
        internal string Input;
        internal string Expected;   // null means the command must return null
        internal Func<TextDocument, int, int, CommandResult> Command;
        internal int Repeat = 1;    // >1 runs the command again on its own result
        // A caret-only style toggle arms the style instead of writing an empty pair: the command
        // must return this pair with no edits, and Typed (when set) is the first input wrapped by
        // MarkdownCommands.WrapTyped, whose result is compared with Expected.
        internal string ArmOpen;
        internal string ArmClose;
        internal string Typed;
        // The armed pair plus this input must not be written at all: the host then inserts the
        // input plainly instead of wrapping it into a fence-starting run.
        internal bool TypedNull;
    }

    static void Add(List<CommandCase> cases, string name, string input, string expected,
        Func<TextDocument, int, int, CommandResult> command) {
        Add(cases, name, input, expected, command, 1);
    }

    static void Add(List<CommandCase> cases, string name, string input, string expected,
        Func<TextDocument, int, int, CommandResult> command, int repeat) {
        CommandCase item = new CommandCase();
        item.Name = name;
        item.Input = input;
        item.Expected = expected;
        item.Command = command;
        item.Repeat = repeat;
        cases.Add(item);
    }

    // A caret-only toggle: no edit, the armed pair, and the caret left where it was.
    static void AddArm(List<CommandCase> cases, string name, string input, string armOpen, string armClose,
        Func<TextDocument, int, int, CommandResult> command) {
        AddArm(cases, name, input, armOpen, armClose, null, input, command);
    }

    // A caret-only toggle followed by the first typed input, wrapped into the pair.
    static void AddArm(List<CommandCase> cases, string name, string input, string armOpen, string armClose,
        string typed, string expected, Func<TextDocument, int, int, CommandResult> command) {
        CommandCase item = new CommandCase();
        item.Name = name;
        item.Input = input;
        item.Expected = expected;
        item.Command = command;
        item.ArmOpen = armOpen;
        item.ArmClose = armClose;
        item.Typed = typed;
        cases.Add(item);
    }

    // A caret-only toggle whose first input must stay unwrapped (the pair would start a fence
    // line); the document is left unchanged and the host inserts the input as plain typing.
    static void AddArmNoWrap(List<CommandCase> cases, string name, string input, string armOpen, string armClose,
        string typed, Func<TextDocument, int, int, CommandResult> command) {
        CommandCase item = new CommandCase();
        item.Name = name;
        item.Input = input;
        item.Expected = input;
        item.Command = command;
        item.ArmOpen = armOpen;
        item.ArmClose = armClose;
        item.Typed = typed;
        item.TypedNull = true;
        cases.Add(item);
    }

    static Func<TextDocument, int, int, CommandResult> Inline(string marker) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.ToggleInline(document, start, length, marker);
        };
    }

    static Func<TextDocument, int, int, CommandResult> Heading(int level) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.SetHeading(document, start, length, level);
        };
    }

    static Func<TextDocument, int, int, CommandResult> ListAs(ListKind kind) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.ToggleList(document, start, length, kind);
        };
    }

    static Func<TextDocument, int, int, CommandResult> Task() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.ToggleTaskCheck(document, start);
        };
    }

    static Func<TextDocument, int, int, CommandResult> QuoteCommand() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.ToggleQuote(document, start, length);
        };
    }

    static Func<TextDocument, int, int, CommandResult> Rule() {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertRule(document, start);
        };
    }

    static Func<TextDocument, int, int, CommandResult> CodeBlock(string language) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertCodeBlock(document, start, length, language);
        };
    }

    static Func<TextDocument, int, int, CommandResult> Link(string url) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertLink(document, start, length, url);
        };
    }

    static Func<TextDocument, int, int, CommandResult> Image(string path, string alt) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertImage(document, start, path, alt);
        };
    }

    // A file drop (one result, so one undo step) at the caret.
    static Func<TextDocument, int, int, CommandResult> Images(string[] references, string[] alts) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertImages(document, start, references, alts);
        };
    }

    static Func<TextDocument, int, int, CommandResult> Table(int rows, int columns) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertTable(document, start, rows, columns);
        };
    }

    // The localized forms the live host passes (T("링크") / T("제목")).
    static Func<TextDocument, int, int, CommandResult> LinkLabel(string url, string placeholder) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertLink(document, start, length, url, placeholder);
        };
    }

    static Func<TextDocument, int, int, CommandResult> TableHead(int columns, string header) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.InsertTable(document, start, 0, columns, header);
        };
    }

    static Func<TextDocument, int, int, CommandResult> Span(string color) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.SetSpanColor(document, start, length, color);
        };
    }

    static Func<TextDocument, int, int, CommandResult> SpanSize(int pixels) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.SetSpanSize(document, start, length, pixels);
        };
    }

    static Func<TextDocument, int, int, CommandResult> TypingSize(int pixels) {
        return delegate(TextDocument document, int start, int length) {
            return MarkdownCommands.SetTypingSize(document, start, pixels);
        };
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

    static bool RunCase(CommandCase test, out string detail) {
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
        if (test.ArmOpen != null)
            return RunArmCase(test, input, inputStart, inputLength, expected, expectedStart, expectedLength, out detail);
        TextDocument edited = new TextDocument(input);
        CommandResult result = null;
        int start = inputStart;
        int length = inputLength;
        for (int pass = 0; pass < test.Repeat; pass++) {
            result = test.Command(edited, start, length);
            if (result == null) {
                detail = pass == 0 ? "command returned null" : "repeat returned null";
                return false;
            }
            string invalid = ValidateEdits(edited.TextLength, result);
            if (invalid != null) {
                detail = invalid;
                return false;
            }
            for (int i = 0; i < result.Edits.Count; i++) {
                SourceEdit edit = result.Edits[i];
                edited.Replace(edit.Offset, edit.Length, edit.Text);
            }
            start = result.SelectionStart;
            length = result.SelectionLength;
        }
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

    // Two steps, exactly as the host runs them: the toggle arms the style (no edit, no history
    // entry) and the first typed input is wrapped into the pair by WrapTyped.
    static bool RunArmCase(CommandCase test, string input, int inputStart, int inputLength,
        string expected, int expectedStart, int expectedLength, out string detail) {
        TextDocument document = new TextDocument(input);
        CommandResult arm = test.Command(document, inputStart, inputLength);
        if (arm == null) {
            detail = "expected an armed style, got null";
            return false;
        }
        if (arm.Edits.Count != 0) {
            detail = "armed style wrote " + arm.Edits.Count.ToString(CultureInfo.InvariantCulture) + " edit(s)";
            return false;
        }
        if (!String.Equals(arm.TypingOpen, test.ArmOpen, StringComparison.Ordinal)
            || !String.Equals(arm.TypingClose, test.ArmClose, StringComparison.Ordinal)) {
            detail = "arm expected [" + test.ArmOpen + "]/[" + test.ArmClose + "] actual ["
                + arm.TypingOpen + "]/[" + arm.TypingClose + "]";
            return false;
        }
        if (arm.SelectionStart != inputStart || arm.SelectionLength != 0) {
            detail = "arm moved the caret to " + arm.SelectionStart.ToString(CultureInfo.InvariantCulture)
                + "+" + arm.SelectionLength.ToString(CultureInfo.InvariantCulture)
                + " (was " + inputStart.ToString(CultureInfo.InvariantCulture) + ")";
            return false;
        }
        if (test.TypedNull) {
            CommandResult none = MarkdownCommands.WrapTyped(document, arm.SelectionStart, arm.TypingOpen, arm.TypingClose, test.Typed);
            detail = none == null ? null : "expected no wrap, got " + none.Edits.Count.ToString(CultureInfo.InvariantCulture) + " edit(s)";
            return none == null;
        }
        if (test.Typed == null) {
            detail = document.Text == expected ? null : "arm changed the text";
            return document.Text == expected;
        }
        CommandResult wrap = MarkdownCommands.WrapTyped(document, arm.SelectionStart, arm.TypingOpen, arm.TypingClose, test.Typed);
        if (wrap == null) {
            detail = "WrapTyped returned null";
            return false;
        }
        string invalid = ValidateEdits(document.TextLength, wrap);
        if (invalid != null) {
            detail = invalid;
            return false;
        }
        for (int i = 0; i < wrap.Edits.Count; i++) {
            SourceEdit edit = wrap.Edits[i];
            document.Replace(edit.Offset, edit.Length, edit.Text);
        }
        if (document.Text != expected) {
            detail = "typed text expected=\"" + Escape(expected) + "\" actual=\"" + Escape(document.Text) + "\"";
            return false;
        }
        if (wrap.SelectionStart != expectedStart || wrap.SelectionLength != expectedLength) {
            detail = "typed caret expected=" + expectedStart.ToString(CultureInfo.InvariantCulture)
                + "+" + expectedLength.ToString(CultureInfo.InvariantCulture)
                + " actual=" + wrap.SelectionStart.ToString(CultureInfo.InvariantCulture)
                + "+" + wrap.SelectionLength.ToString(CultureInfo.InvariantCulture);
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

    static List<CommandCase> BuildCommandCases() {
        List<CommandCase> cases = new List<CommandCase>();

        // ToggleInline: bold.
        AddArm(cases, "bold caret at word start arms the style", "hello ‸world", "**", "**", Inline("**"));
        AddArm(cases, "bold caret at word start wraps the first input", "hello ‸world", "**", "**", "abc", "hello **abc‸**world", Inline("**"));
        Add(cases, "bold selection", "«world» peace", "**«world»** peace", Inline("**"));
        Add(cases, "bold keeps end spaces outside", "« hello »", " **«hello»** ", Inline("**"));
        Add(cases, "bold removes outside markers", "**«world»**", "«world»", Inline("**"));
        Add(cases, "bold removes inside markers", "«**world**»", "«world»", Inline("**"));
        AddArm(cases, "bold caret with no word arms the style", "ab ‸", "**", "**", Inline("**"));
        AddArm(cases, "bold caret with no word wraps the first input", "ab ‸", "**", "**", "x", "ab **x‸**", Inline("**"));
        Add(cases, "bold multi-line adds per line", "«one\ntwo»", "**«one**\n**two»**", Inline("**"));
        Add(cases, "bold multi-line leaves wrapped line", "«**one**\ntwo»", "**«one**\n**two»**", Inline("**"));
        Add(cases, "bold multi-line all wrapped removes", "«**one**\n**two**»", "«one\ntwo»", Inline("**"));
        Add(cases, "bold multi-line toggles off inner selection", "**«one**\n**two»**", "«one\ntwo»", Inline("**"));
        Add(cases, "bold multi-line skips empty line", "«a\n\nb»", "**«a**\n\n**b»**", Inline("**"));
        Add(cases, "bold excludes next line at column zero", "«a\n»b", "**«a»**\nb", Inline("**"));
        Add(cases, "bold multi-line CRLF", "«a\r\nb»", "**«a**\r\n**b»**", Inline("**"));
        Add(cases, "bold multi-line CRLF removes", "«**a**\r\n**b**»", "«a\r\nb»", Inline("**"));
        Add(cases, "bold inside fence returns null", "```\n«x»\n```", null, Inline("**"));

        // ToggleInline: a caret-only toggle at an edge arms the style for the text typed next
        // (no empty pair in the source), and an inside-word toggle keeps the word but never
        // leaves it selected (Hangul input).
        AddArm(cases, "bold caret at word end arms the style", "가나다‸", "**", "**", Inline("**"));
        AddArm(cases, "bold caret at word end wraps the first input", "가나다‸", "**", "**", "abc", "가나다**abc‸**", Inline("**"));
        Add(cases, "bold caret inside word keeps caret collapsed", "가‸나다", "**가‸나다**", Inline("**"));
        AddArm(cases, "bold caret after space arms the style", "가나다 ‸", "**", "**", Inline("**"));
        AddArm(cases, "bold caret after space wraps the first input", "가나다 ‸", "**", "**", "가", "가나다 **가‸**", Inline("**"));
        AddArm(cases, "bold caret at line start wraps the first input", "‸abc", "**", "**", "x", "**x‸**abc", Inline("**"));
        Add(cases, "bold caret before closing marker steps out", "**가나‸**", "**가나**‸", Inline("**"));
        // An empty pair alone on its line is a thematic break ("****"), so a caret-only command
        // declines on structure (see "bold caret on a rule returns null"). Keep the pair inside
        // text so this tests the empty-pair removal itself.
        Add(cases, "bold caret between empty pair removes pair", "x **‸** y", "x ‸ y", Inline("**"));
        // Arming twice writes nothing either time; the host cancels the second toggle.
        AddArm(cases, "bold double toggle at word end stays armed without text", "가나다‸", "**", "**", Inline("**"));
        Add(cases, "bold double toggle inside word restores", "가‸나다", "가‸나다", Inline("**"), 2);
        AddArm(cases, "bold double toggle at empty line stays armed without text", "‸", "**", "**", Inline("**"));
        AddArm(cases, "italic caret at word end arms the style", "가나다‸", "*", "*", Inline("*"));
        AddArm(cases, "italic caret at word end wraps the first input", "가나다‸", "*", "*", "가", "가나다*가‸*", Inline("*"));
        Add(cases, "italic caret inside word keeps caret collapsed", "가‸나다", "*가‸나다*", Inline("*"));
        AddArm(cases, "italic caret after space arms the style", "가나다 ‸", "*", "*", Inline("*"));
        Add(cases, "italic caret before closing marker steps out", "*가나‸*", "*가나*‸", Inline("*"));
        AddArm(cases, "italic does not treat bold pair as marker", "**가나‸**", "*", "*", Inline("*"));
        // Same rule-line caveat as the bold empty-pair case above: the bold pair sits inside text.
        AddArm(cases, "italic caret inside bold empty pair is not italic pair", "x **‸** y", "*", "*", Inline("*"));
        AddArm(cases, "strike caret at word end arms the style", "가나다‸", "~~", "~~", Inline("~~"));
        AddArm(cases, "strike caret at word end wraps the first input", "가나다‸", "~~", "~~", "x", "가나다~~x‸~~", Inline("~~"));
        Add(cases, "strike caret inside word keeps caret collapsed", "가‸나다", "~~가‸나다~~", Inline("~~"));
        AddArm(cases, "strike caret after space arms the style", "가나다 ‸", "~~", "~~", Inline("~~"));
        Add(cases, "strike caret before closing marker steps out", "~~가나‸~~", "~~가나~~‸", Inline("~~"));

        // A pair written at a line start must not merge with a tilde run into a fence opener.
        Add(cases, "strike at line start reuses the opening run", "«~~abc»", "«~~abc»~~", Inline("~~"));
        Add(cases, "strike at line start leaves a single tilde alone", "«~abc»", null, Inline("~~"));
        Add(cases, "strike at line start leaves a tilde-only run alone", "«~~»", null, Inline("~~"));
        AddArmNoWrap(cases, "strike arm at line start does not wrap a typed tilde into a fence", "‸~~abc", "~~", "~~", "~", Inline("~~"));

        // ToggleInline: italic and strike.
        Add(cases, "italic wraps", "«x»", "*«x»*", Inline("*"));
        Add(cases, "italic does not treat bold as wrapped", "**«x»**", "***«x»***", Inline("*"));
        Add(cases, "italic removes outside", "*«x»*", "«x»", Inline("*"));
        Add(cases, "italic removes inside", "«*x*»", "«x»", Inline("*"));
        Add(cases, "strike wraps", "«x»", "~~«x»~~", Inline("~~"));
        Add(cases, "strike removes", "~~«x»~~", "«x»", Inline("~~"));

        // ToggleInline: code.
        Add(cases, "code wraps", "«x»", "`«x»`", Inline("`"));
        Add(cases, "code uses longer fence for backtick", "«a`b»", "`` «a`b» ``", Inline("`"));
        Add(cases, "code keeps a single tick beside a longer run", "«a```b»", "`«a```b»`", Inline("`"));
        Add(cases, "code pads a single tick beside leading backticks", "«``a»", "` «``a» `", Inline("`"));
        Add(cases, "code pads a single tick beside trailing backticks", "«a``»", "` «a``» `", Inline("`"));
        Add(cases, "code uses the shortest run away from a line start", "x «a``b»", "x `«a``b»`", Inline("`"));
        Add(cases, "code needing three ticks at a line start returns null", "«`a``b»", null, Inline("`"));
        Add(cases, "code removes a padded single tick", "«` ``a `»", "«``a»", Inline("`"));
        Add(cases, "code removes single fence", "`«x»`", "«x»", Inline("`"));
        Add(cases, "code removes long fence with padding", "`` «a`b» ``", "«a`b»", Inline("`"));
        Add(cases, "code removes long fence selected whole", "«`` a`b ``»", "«a`b»", Inline("`"));
        AddArm(cases, "code caret with no word arms the style", "‸", "`", "`", Inline("`"));
        AddArm(cases, "code caret with no word wraps the first input", "‸", "`", "`", "x", "`x‸`", Inline("`"));
        AddArm(cases, "code caret at word end wraps the first input", "가나다‸", "`", "`", "a", "가나다`a‸`", Inline("`"));
        Add(cases, "code multi-line", "«a\nb»", "`«a`\n`b»`", Inline("`"));
        Add(cases, "code multi-line CRLF", "«a\r\nb»", "`«a`\r\n`b»`", Inline("`"));
        Add(cases, "inline skips heading prefix", "# «title»", "# **«title»**", Inline("**"));
        Add(cases, "inline skips list prefix", "- «item»", "- **«item»**", Inline("**"));
        Add(cases, "inline keeps trailing space outside", "«word » tail", "**«word»**  tail", Inline("**"));

        // SetHeading.
        Add(cases, "heading h1", "«title»", "# «title»", Heading(1));
        Add(cases, "heading h2 multi-line", "«one\ntwo»", "## «one\n## two»", Heading(2));
        Add(cases, "heading replaces level", "## «title»", "### «title»", Heading(3));
        Add(cases, "heading toggles same level to body", "# «title»", "«title»", Heading(1));
        Add(cases, "heading level zero removes", "### «title»", "«title»", Heading(0));
        Add(cases, "heading keeps quote prefix", "> «title»", "> ## «title»", Heading(2));
        Add(cases, "heading removes list marker", "- «item»", "# «item»", Heading(1));
        Add(cases, "heading removes list and old heading", "- ## «item»", "# «item»", Heading(1));
        // The item's room after the quote markers is part of the item (ParsePrefix reads the marker
        // past it): a heading over a quoted item takes that room with the marker, so ">    - b"
        // becomes "> # b" instead of "> #    b". A one-level quote and a plain indented list keep
        // their existing answer.
        Add(cases, "heading replaces nested quoted list item", ">    - «b»", "> # «b»", Heading(1));
        Add(cases, "heading replaces quoted list item", "> - «b»", "> # «b»", Heading(1));
        Add(cases, "heading keeps indentation outside a quote", "  - «b»", "  # «b»", Heading(1));
        // ATX markers set a heading only within the first three columns (CommonMark; the live view
        // and the basic view read the same limit), and ParsePrefix finds a marker after the whole
        // leading run. A line indented four or more columns therefore has no heading slot: the
        // command takes that room with the marker and writes the marker at the line start. H3 on
        // "    - item" used to leave "    ### item", which both views kept as plain paragraph
        // text, so the button looked dead (b56c6 format-abuse, seed 7 step 34).
        Add(cases, "heading replaces a deeply indented list item", "    - «item»", "### «item»", Heading(3));
        Add(cases, "heading replaces a deeply indented task item", "    - [ ] «item»", "### «item»", Heading(3));
        Add(cases, "heading over deeply indented text writes at the line start", "    «text»", "### «text»", Heading(3));
        Add(cases, "heading strips deep indentation per line", "«    - one\n  - two»", "### «one\n  ### two»", Heading(3));
        Add(cases, "heading keeps three columns of indentation", "   - «item»", "   # «item»", Heading(1));
        Add(cases, "heading keeps a quoted slot after indentation", "    > - «b»", "    > # «b»", Heading(1));
        // A marker already written at that depth is plain text too, so the same command rewrites
        // it at the line start (and the next press toggles the real heading off).
        Add(cases, "heading normalizes a deeply indented marker", "    ### «deep»", "### «deep»", Heading(3));
        Add(cases, "heading normalizes a deeply indented marker then toggles", "    ### «deep»", "«deep»", Heading(3), 2);
        Add(cases, "heading level zero on a deeply indented marker removes the marker", "    ### «deep»", "    «deep»", Heading(0));
        // A heading after a list marker is the one both views read at any depth (the marker is
        // read after the item's own marker, not from the line start), so a nested item's heading
        // keeps its level: the same level toggles it off in place, and another level takes the
        // item's place at the line start like every other deep write.
        Add(cases, "heading toggles a deeply indented list heading off", "    - ### «t»", "    «t»", Heading(3));
        Add(cases, "heading replaces a deeply indented list heading", "    - ### «t»", "## «t»", Heading(2));
        // The heading writes its separator space even on an empty body: "#" alone is a heading,
        // but the first character typed after it would make "#제목" and the line would lose the
        // heading the button just drew (bughunt2 insert 2). The caret lands after the space.
        Add(cases, "heading on empty line", "‸", "# ‸", Heading(1));
        Add(cases, "heading on empty list item", "- ‸", "# ‸", Heading(1));
        Add(cases, "heading on empty quoted list item", "> - ‸", "> # ‸", Heading(1));
        Add(cases, "heading on empty quoted line", "> ‸", "> # ‸", Heading(1));
        Add(cases, "heading mixed levels keeps line and sets all", "«# one\ntwo»", "«## one\n## two»", Heading(2));
        Add(cases, "heading CRLF multi-line", "«one\r\ntwo»", "# «one\r\n# two»", Heading(1));
        Add(cases, "heading level zero on body returns null", "«x»", null, Heading(0));
        Add(cases, "heading invalid level returns null", "«x»", null, Heading(7));
        Add(cases, "heading inside fence returns null", "```\n«title»\n```", null, Heading(1));
        // A selection that spans a fence takes only the ordinary lines around it: the two fence
        // lines and the code between them keep their bytes (b56c9 format-abuse: cmd:h2 and cmd:h0
        // left a code fence unclosed on selections like this one).
        Add(cases, "heading over paragraphs around a fence leaves the fence alone",
            "«above\n```\ncode\n```\nbelow»", "## «above\n```\ncode\n```\n## below»", Heading(2));
        // The body command drops the ordinary heading's marker only: the "# code" line is code
        // text inside the block, the fences keep every byte, and the heading whose body is a
        // fence run keeps its marker (dropping it would leave "```" at the line start and open a
        // block with no partner).
        Add(cases, "level zero over paragraphs around a fence leaves the fence alone",
            "«# ```\n```\n# code\n```\n# below»", "«# ```\n```\n# code\n```\nbelow»", Heading(0));
        Add(cases, "heading caret on a fence line returns null", "‸```\ntext\n```", null, Heading(2));
        // A "====" under a closing fence is not a setext underline of the fence: the command used
        // to read the fence line as its paragraph, write "## " onto the fence and leave the block
        // unclosed (b56c9 format-abuse: cmd:h2 left a code fence unclosed).
        Add(cases, "heading on a rule under a closing fence returns null",
            "```\ncode\n```\n«====»\ntail", null, Heading(2));
        // A setext heading ("제목" + "====" / "----") is the heading the screen draws, so the
        // heading command writes the ATX marker and takes the underline with it in one command
        // (the old marker left the "====" as a body line or turned "----" into a rule), and the
        // body command turns it into a plain paragraph by dropping the underline alone
        // (bughunt4 P2-2).
        Add(cases, "heading over a setext h1 writes ATX and drops the underline", "«제목\n====»", "## «제목»", Heading(2));
        Add(cases, "heading over a setext h2 writes ATX and drops the underline", "«제목»\n----", "# «제목»", Heading(1));
        Add(cases, "heading same level on a setext heading returns to body", "«제목»\n====", "«제목»", Heading(1));
        Add(cases, "heading level zero on a setext heading drops the underline", "«제목»\n====", "«제목»", Heading(0));
        Add(cases, "heading caret on the underline edits its paragraph", "제목\n«====»", "## 제목‸", Heading(2));
        Add(cases, "heading over a setext heading keeps the next line", "«제목»\n====\n본문", "## «제목»\n본문", Heading(2));
        Add(cases, "heading over a setext heading CRLF", "«제목»\r\n====", "## «제목»", Heading(2));

        // ToggleList.
        Add(cases, "bullet add", "«a»", "- «a»", ListAs(ListKind.Bullet));
        Add(cases, "bullet add multi-line", "«a\nb»", "- «a\n- b»", ListAs(ListKind.Bullet));
        Add(cases, "bullet remove", "- «a»", "«a»", ListAs(ListKind.Bullet));
        Add(cases, "number add", "«a»", "1. «a»", ListAs(ListKind.Number));
        Add(cases, "number renumbers converted lines", "«1. a\n- b»", "«1. a\n2. b»", ListAs(ListKind.Number));
        Add(cases, "number indentation levels are independent", "«a\n  b\nc»", "1. «a\n  1. b\n2. c»", ListAs(ListKind.Number));
        Add(cases, "number converts to bullet", "1. «a»", "- «a»", ListAs(ListKind.Bullet));
        Add(cases, "task add", "«a»", "- [ ] «a»", ListAs(ListKind.Task));
        Add(cases, "task remove", "- [ ] «a»", "«a»", ListAs(ListKind.Task));
        Add(cases, "bullet converts to task", "- «a»", "- [ ] «a»", ListAs(ListKind.Task));
        Add(cases, "list marker goes after quote prefix", "> «a»", "> - «a»", ListAs(ListKind.Bullet));
        Add(cases, "list keeps indentation", "  «a»", "  - «a»", ListAs(ListKind.Bullet));
        Add(cases, "list CRLF", "«a»\r\nb", "- «a»\r\nb", ListAs(ListKind.Bullet));
        Add(cases, "list removes mixed indentation", "«- a\n  - b»", "«a\n  b»", ListAs(ListKind.Bullet));
        Add(cases, "list inside fence returns null", "```\n«a»\n```", null, ListAs(ListKind.Bullet));
        // Quoted lists (bughunt 11): the item's room after the quote markers is the nesting level,
        // the same key MarkdownStructuralInput uses, so a nested item starts at 1 and lists at
        // different quote depths never share a counter. Outside a quote nothing changes.
        Add(cases, "number quoted nested items keep their own levels",
            "«> 1. a\n>    1. b\n>    - c\n> 2. d»", "«> 1. a\n>    1. b\n>    2. c\n> 2. d»", ListAs(ListKind.Number));
        Add(cases, "number add three quoted lines",
            "«> a\n> b\n> c»", "«> 1. a\n> 2. b\n> 3. c»", ListAs(ListKind.Number));
        Add(cases, "number add in a nested quote",
            "«> > a\n> > b»", "«> > 1. a\n> > 2. b»", ListAs(ListKind.Number));
        Add(cases, "number nested quote level starts at one",
            "«> 1. a\n> > - b»", "«> 1. a\n> > 1. b»", ListAs(ListKind.Number));
        Add(cases, "number removes a quoted list",
            "«> 1. a\n>    1. b»", "«> a\n>    b»", ListAs(ListKind.Number));
        Add(cases, "list marker follows a quote's own indentation",
            "«>    hello»", "«>    1. hello»", ListAs(ListKind.Number));
        Add(cases, "number levels outside quotes stay independent",
            "«- a\n    - b\n- c»", "«1. a\n    1. b\n2. c»", ListAs(ListKind.Number));

        // ToggleTaskCheck.
        Add(cases, "task check flips unchecked", "- [ ] ‸task", "- [x] ‸task", Task());
        Add(cases, "task check flips checked", "- [x] ‸task", "- [ ] ‸task", Task());
        Add(cases, "task check flips uppercase", "- [X] ‸task", "- [ ] ‸task", Task());
        Add(cases, "task check ignores plain bullet", "- ‸item", null, Task());
        Add(cases, "task check ignores plain line", "‸text", null, Task());
        Add(cases, "task check inside fence returns null", "```\n- [ ] ‸x\n```", null, Task());
        Add(cases, "task check CRLF", "- [ ] ‸a\r\nb", "- [x] ‸a\r\nb", Task());
        // The box ends at "]": the screen draws "- [x]done" as a task, so the commands read the
        // same marker (bughunt2 insert 11) and never write a second box in front of it.
        Add(cases, "task check flips a box without a following space", "- [x]‸done", "- [ ]‸done", Task());
        Add(cases, "task toggle removes a box without a following space", "- [x]«done»", "«done»", ListAs(ListKind.Task));
        Add(cases, "bullet replaces a box without a following space", "- [ ]«done»", "- «done»", ListAs(ListKind.Bullet));
        Add(cases, "heading takes a box without a following space", "- [x]«done»", "# «done»", Heading(1));

        // ToggleQuote.
        Add(cases, "quote add", "«abc»", "> «abc»", QuoteCommand());
        Add(cases, "quote add multi-line", "«a\nb»", "> «a\n> b»", QuoteCommand());
        Add(cases, "quote adds marker on empty interior line", "«a\n\nb»", "> «a\n>\n> b»", QuoteCommand());
        Add(cases, "quote remove", "> «abc»", "«abc»", QuoteCommand());
        Add(cases, "quote add when one line is unquoted", "«> a\nb»", "> «> a\n> b»", QuoteCommand());
        Add(cases, "quote removes one nested level", "> > «a»", "> «a»", QuoteCommand());
        Add(cases, "quote inside fence returns null", "```\n«a»\n```", null, QuoteCommand());
        Add(cases, "quote CRLF multi-line", "«a\r\nb»", "> «a\r\n> b»", QuoteCommand());

        // InsertRule. The caret lands on the new empty line under the rule: typing right after
        // "---" would turn the rule into a paragraph ("---text").
        Add(cases, "rule after non-empty last line", "abc\n‸def", "abc\ndef\n\n---\n‸", Rule());
        Add(cases, "rule between non-empty lines", "‸abc\ndef", "abc\n\n---\n‸\ndef", Rule());
        Add(cases, "rule after non-empty document end", "abc‸", "abc\n\n---\n‸", Rule());
        Add(cases, "rule in empty document", "‸", "---\n‸", Rule());
        Add(cases, "rule after empty interior line", "abc\n‸\ndef", "abc\n\n---\n‸\ndef", Rule());
        Add(cases, "rule CRLF", "‸abc\r\ndef", "abc\r\n\r\n---\r\n‸\r\ndef", Rule());
        Add(cases, "rule after empty trailing line", "abc\n‸", "abc\n\n---\n‸", Rule());
        // A fence line and the code inside a fence are literal: a rule written there is code text,
        // and a "---" code line must never be removed as if it were a rule (bughunt2 insert 1).
        Add(cases, "rule inside a fence returns null", "```\n‸code\n```", null, Rule());
        Add(cases, "rule on a code line returns null", "```\n‸---\n```", null, Rule());
        Add(cases, "rule on a fence line returns null", "‸```\ntext\n```", null, Rule());
        Add(cases, "rule right after a closing fence is written", "```\ncode\n```\n‸below\ntail",
            "```\ncode\n```\nbelow\n\n---\n‸\ntail", Rule());

        // InsertCodeBlock.
        Add(cases, "code block wraps selection", "«a\nb»", "```\n«a\nb»\n```", CodeBlock(null));
        Add(cases, "code block language", "«a»", "```csharp\n«a»\n```", CodeBlock("csharp"));
        Add(cases, "code block empty selection", "‸", "```\n‸\n```", CodeBlock(null));
        Add(cases, "code block empty selection after text", "abc‸", "abc\n```\n‸\n```", CodeBlock(null));
        Add(cases, "code block CRLF", "«a\r\nb»", "```\r\n«a\r\nb»\r\n```", CodeBlock(null));
        Add(cases, "code block before following text", "«a»\nb", "```\n«a»\n```\nb", CodeBlock(null));
        Add(cases, "code block inside fence removes the fences", "```\n«x»\n```", "«x»", CodeBlock(null));

        // InsertLink.
        Add(cases, "link selection", "«text»", "[«text»](https://e)", Link("https://e"));
        Add(cases, "link empty selection", "‸", "[«링크»](u)", Link("u"));
        Add(cases, "link escapes url", "«x»", "[«x»](a%20b%29c)", Link("a b)c"));
        Add(cases, "link multi-line selection becomes one link per line", "«a\nb»", "«[a](u)\n[b](u)»", Link("u"));
        Add(cases, "link CRLF document", "«a»\r\nb", "[«a»](u)\r\nb", Link("u"));
        Add(cases, "link inside fence returns null", "```\n«text»\n```", null, Link("u"));
        Add(cases, "link on opening fence line returns null", "‸```\ntext\n```", null, Link("u"));
        Add(cases, "link selection ending at fence line returns null", "```\n«text\n»```", null, Link("u"));
        // A label's own brackets end it early and a trailing backslash escapes its closing "]", so
        // the selected text is escaped instead of dropped (bughunt2 insert 8; the live parser
        // reads "\x" as x and skips escaped brackets in a label).
        Add(cases, "link escapes a closing bracket in the label", "«a]b»", "[«a\\]b»](u)", Link("u"));
        Add(cases, "link escapes an opening bracket in the label", "«a[b»", "[«a\\[b»](u)", Link("u"));
        Add(cases, "link escapes a trailing backslash in the label", "«a\\»", "[«a\\\\»](u)", Link("u"));
        Add(cases, "link escapes brackets per line", "«a]b\nc[d»", "«[a\\]b](u)\n[c\\[d](u)»", Link("u"));

        // InsertImage.
        Add(cases, "image", "‸", "![그림](img.png)‸", Image("img.png", "그림"));
        Add(cases, "image path with spaces", "x‸", "x![alt](<a b.png>)‸", Image("a b.png", "alt"));
        Add(cases, "image empty alt", "‸", "![](p.png)‸", Image("p.png", ""));
        Add(cases, "image CRLF document", "x‸\r\ny", "x![alt](p.png)‸\r\ny", Image("p.png", "alt"));

        // Image files dropped on the source take the image button's three guards too, and one
        // image per source line must not split a table row (bughunt2 insert 9).
        string[] twoRefs = new string[] { "a.png", "b.png" };
        string[] twoAlts = new string[] { "a", "b" };
        Add(cases, "images inside a fence return null", "```\n‸\n```", null, Images(twoRefs, twoAlts));
        Add(cases, "images on a fence line return null", "‸```\ntext\n```", null, Images(twoRefs, twoAlts));
        Add(cases, "images inside inline code return null", "`ab‸cd`", null, Images(twoRefs, twoAlts));
        Add(cases, "images on a rule return null", "‸---", null, Images(twoRefs, twoAlts));
        // The delimiter row must match the header's cell count to be table structure (TableLineKind's
        // two-line rule); a short "|---|" row under a two-cell header is only a pipe paragraph.
        Add(cases, "images on a delimiter row return null", "| a | b |\n«|---|---|»", null, Images(twoRefs, twoAlts));
        // The whole document is compared: the header and delimiter rows stay above the row (11-01:
        // the old literal had dropped them, so the case could never pass).
        Add(cases, "several images in a table row stay in one cell", "| a |\n|---|\n| ‸x |", "| a |\n|---|\n| ![a](a.png) ![b](b.png)‸x |", Images(twoRefs, twoAlts));
        Add(cases, "one image in a table cell is inserted", "| a |\n|---|\n| ‸x |",
            "| a |\n|---|\n| ![p](p.png)‸x |", Images(new string[] { "p.png" }, new string[] { "p" }));
        Add(cases, "images below a paragraph land one per line", "ab‸",
            "ab![a](a.png)\n![b](b.png)‸", Images(twoRefs, twoAlts));

        // InsertTable.
        Add(cases, "table one body row", "‸", "| «제목1» | 제목2 |\n|---|---|\n|  |  |", Table(1, 2));
        Add(cases, "table zero body rows", "‸", "| «제목1» | 제목2 | 제목3 |\n|---|---|---|", Table(0, 3));
        Add(cases, "table CRLF after content", "abc‸\r\ndef",
            "abc\r\n| «제목1» | 제목2 |\r\n|---|---|\r\n|  |  |\r\ndef", Table(1, 2));
        Add(cases, "table rejects zero columns", "‸", null, Table(1, 0));
        // A grid written straight above another table joins it: the old header and its delimiter
        // row read as rows of the new grid, so one blank line keeps the two tables apart
        // (bughunt2 insert 5).
        Add(cases, "table above an existing table keeps a blank line", "para‸\n| a | b |\n|---|---|\n| 1 | 2 |",
            "para\n| «제목1» | 제목2 |\n|---|---|\n|  |  |\n\n| a | b |\n|---|---|\n| 1 | 2 |", Table(1, 2));
        Add(cases, "table above a pipe paragraph does not add a gap", "para‸\nsee a | b",
            "para\n| «제목1» | 제목2 |\n|---|---|\n|  |  |\nsee a | b", Table(1, 2));
        // Fenced code is literal text: the grid would draw as code, so the button declines the
        // same way the other block commands do (bughunt2 insert 6).
        Add(cases, "table inside a fence returns null", "```\n‸code\n```", null, Table(1, 2));
        Add(cases, "table on a fence line returns null", "‸```\ntext\n```", null, Table(1, 2));

        // SetSpanColor.
        Add(cases, "span wraps selection", "«text»", "<span style=\"color:#FF0000\">«text»</span>", Span("#FF0000"));
        Add(cases, "span replaces color from outside", "<span style=\"color:#FF0000\">«text»</span>",
            "<span style=\"color:#00FF00\">«text»</span>", Span("#00FF00"));
        Add(cases, "span replaces color from inside", "«<span style=\"color:#FF0000\">text</span>»",
            "«<span style=\"color:#0000FF\">text</span>»", Span("#0000FF"));
        Add(cases, "span removes from outside", "<span style=\"color:#FF0000\">«text»</span>", "«text»", Span(null));
        Add(cases, "span removes from inside", "«<span style=\"color:#FF0000\">text</span>»", "«text»", Span(null));
        Add(cases, "span null without span returns null", "«text»", null, Span(null));
        AddArm(cases, "span empty selection arms the style", "‸", "<span style=\"color:#FF0000\">", "</span>", Span("#FF0000"));
        AddArm(cases, "span empty selection wraps the first input", "‸", "<span style=\"color:#FF0000\">", "</span>", "한", "<span style=\"color:#FF0000\">한‸</span>", Span("#FF0000"));
        AddArm(cases, "span color caret at word end arms the style", "가나다‸", "<span style=\"color:#FF0000\">", "</span>", Span("#FF0000"));
        AddArm(cases, "span color caret at word end wraps the first input", "가나다‸", "<span style=\"color:#FF0000\">", "</span>", "한", "가나다<span style=\"color:#FF0000\">한‸</span>", Span("#FF0000"));
        AddArm(cases, "span color caret after space arms the style", "가나다 ‸", "<span style=\"color:#FF0000\">", "</span>", Span("#FF0000"));
        Add(cases, "span color caret inside word keeps caret collapsed", "가‸나다", "<span style=\"color:#FF0000\">가‸나다</span>", Span("#FF0000"));
        Add(cases, "span color caret inside color span replaces color", "<span style=\"color:#FF0000\">가‸나</span>", "<span style=\"color:#00FF00\">가‸나</span>", Span("#00FF00"));
        Add(cases, "span color caret inside word removes span", "<span style=\"color:#FF0000\">가‸나</span>", "가‸나", Span(null));
        Add(cases, "span color null caret before color close steps out", "<span style=\"color:#FF0000\">가나‸</span>", "<span style=\"color:#FF0000\">가나</span>‸", Span(null));
        Add(cases, "span color null caret before size close returns null", "<span style=\"font-size:24px\">가나‸</span>", null, Span(null));
        Add(cases, "span color null at plain caret returns null", "가나다‸", null, Span(null));
        Add(cases, "span same color returns null", "<span style=\"color:#FF0000\">«text»</span>", null, Span("#FF0000"));
        Add(cases, "span partial selection nests", "<span style=\"color:#FF0000\">«te»xt</span>",
            "<span style=\"color:#FF0000\"><span style=\"color:#00FF00\">«te»</span>xt</span>", Span("#00FF00"));
        Add(cases, "span adds missing hash", "«x»", "<span style=\"color:#FF0000\">«x»</span>", Span("FF0000"));
        Add(cases, "span CRLF document", "«a»\r\nb", "<span style=\"color:#FF0000\">«a»</span>\r\nb", Span("#FF0000"));
        Add(cases, "span inside fence returns null", "```\n«x»\n```", null, Span("#FF0000"));

        // SetSpanSize and combined color/font-size spans.
        Add(cases, "span size wraps selection", "«text»", "<span style=\"font-size:24px\">«text»</span>", SpanSize(24));
        Add(cases, "span size replaces from outside", "<span style=\"font-size:12px\">«text»</span>",
            "<span style=\"font-size:24px\">«text»</span>", SpanSize(24));
        Add(cases, "span size replaces from inside", "«<span style=\"font-size:12px\">text</span>»",
            "«<span style=\"font-size:24px\">text</span>»", SpanSize(24));
        Add(cases, "span size removes and unwraps", "<span style=\"font-size:24px\">«text»</span>",
            "«text»", SpanSize(0));
        Add(cases, "span size removes from inside and unwraps", "«<span style=\"font-size:24px\">text</span>»",
            "«text»", SpanSize(0));
        Add(cases, "span size adds to color span", "<span style=\"color:#FF0000\">«text»</span>",
            "<span style=\"color:#FF0000;font-size:24px\">«text»</span>", SpanSize(24));
        Add(cases, "span color adds to size span", "<span style=\"font-size:24px\">«text»</span>",
            "<span style=\"color:#FF0000;font-size:24px\">«text»</span>", Span("#FF0000"));
        Add(cases, "span color replaces on combined span", "<span style=\"color:#FF0000;font-size:24px\">«text»</span>",
            "<span style=\"color:#00FF00;font-size:24px\">«text»</span>", Span("#00FF00"));
        Add(cases, "span color removes and keeps size", "<span style=\"color:#FF0000;font-size:24px\">«text»</span>",
            "<span style=\"font-size:24px\">«text»</span>", Span(null));
        Add(cases, "span size removes and keeps color", "<span style=\"color:#FF0000;font-size:24px\">«text»</span>",
            "<span style=\"color:#FF0000\">«text»</span>", SpanSize(0));
        Add(cases, "span color after size property", "<span style=\"font-size:24px;color:#FF0000\">«text»</span>",
            "<span style=\"color:#00FF00;font-size:24px\">«text»</span>", Span("#00FF00"));
        Add(cases, "span size keeps other properties", "<span style=\"background:#EEE\">«text»</span>",
            "<span style=\"font-size:24px;background:#EEE\">«text»</span>", SpanSize(24));
        Add(cases, "span color null on size span returns null", "<span style=\"font-size:24px\">«text»</span>",
            null, Span(null));
        Add(cases, "span size same value returns null", "<span style=\"font-size:24px\">«text»</span>",
            null, SpanSize(24));
        Add(cases, "span size zero without size span returns null", "<span style=\"color:#FF0000\">«text»</span>",
            null, SpanSize(0));
        Add(cases, "span size empty selection returns null", "‸", null, SpanSize(24));
        AddArm(cases, "size typing caret arms the style", "‸", "<span style=\"font-size:24px\">", "</span>", TypingSize(24));
        AddArm(cases, "size typing caret wraps the first input", "‸", "<span style=\"font-size:24px\">", "</span>", "한", "<span style=\"font-size:24px\">한‸</span>", TypingSize(24));
        AddArm(cases, "size typing at word end wraps the first input", "가나다‸", "<span style=\"font-size:24px\">", "</span>", "한", "가나다<span style=\"font-size:24px\">한‸</span>", TypingSize(24));
        Add(cases, "span size negative returns null", "«text»", null, SpanSize(-1));
        Add(cases, "span size inside fence returns null", "```\n«x»\n```", null, SpanSize(24));

        // Regression: a TXT paragraph with several logical lines.
        Add(cases, "regression heading touches selected line only", "para one\n«para two»\npara three",
            "para one\n# «para two»\npara three", Heading(1));
        Add(cases, "regression list touches selected line only", "para one\n«para two»\npara three",
            "para one\n- «para two»\npara three", ListAs(ListKind.Bullet));
        Add(cases, "regression bold touches selected line only", "para one\n«para two»\npara three",
            "para one\n**«para two»**\npara three", Inline("**"));
        Add(cases, "regression quote first two lines only", "«para one\npara two\n»para three",
            "> «para one\n> para two\n»para three", QuoteCommand());
        Add(cases, "regression bullet excludes next line at column zero", "«aa\nbb\n»cc",
            "- «aa\n- bb\n»cc", ListAs(ListKind.Bullet));

        // Fenced code inside a selection keeps its text and the block command still applies to
        // the selection's other lines, instead of refusing the whole selection (bughunt2 insert 13).
        Add(cases, "heading applies around a fence", "«para\n```\ncode\n```\nafter»",
            "# «para\n```\ncode\n```\n# after»", Heading(1));
        Add(cases, "heading applies after a selection that starts in a fence", "```\n«code\n```\npara»",
            "```\n«code\n```\n# para»", Heading(1));
        Add(cases, "list applies around a fence", "«para\n```\ncode\n```\nafter»",
            "- «para\n```\ncode\n```\n- after»", ListAs(ListKind.Bullet));
        Add(cases, "quote applies around a fence", "«para\n```\ncode\n```\nafter»",
            "> «para\n```\ncode\n```\n> after»", QuoteCommand());
        Add(cases, "heading on a fence line still returns null", "«```»\ntext\n```", null, Heading(1));

        // Inline commands on structure (bughunt 2): table rows are edited inside their cells,
        // the delimiter row / setext underline / rule are skipped, and the pipes stay put.
        Add(cases, "bold on table cells keeps pipes and skips delimiter",
            "«| a | b |\n|---|---|\n| c | d |»",
            "| **«a** | **b** |\n|---|---|\n| **c** | **d»** |", Inline("**"));
        Add(cases, "bold skips a setext underline", "«title\n---\nbody»", "**«title**\n---\n**body»**", Inline("**"));
        Add(cases, "bold on a delimiter row returns null", "| a | b |\n«|---|---|»", null, Inline("**"));
        Add(cases, "bold on a rule returns null", "«---»", null, Inline("**"));
        Add(cases, "bold caret on a rule returns null", "---‸", null, Inline("**"));
        Add(cases, "span color on table cells keeps pipes",
            "«| a | b |\n|---|---|»",
            "«| <span style=\"color:#FF0000\">a</span> | <span style=\"color:#FF0000\">b</span> |\n|---|---|»", Span("#FF0000"));

        // A span closes only inside one line, so a multi-line selection is wrapped per line
        // (one command, one undo step).
        Add(cases, "span color wraps each line", "«a\nb»",
            "«<span style=\"color:#FF0000\">a</span>\n<span style=\"color:#FF0000\">b</span>»", Span("#FF0000"));
        Add(cases, "span size wraps each line", "«a\nb»",
            "«<span style=\"font-size:24px\">a</span>\n<span style=\"font-size:24px\">b</span>»", SpanSize(24));

        // A link closes its label inside one line as well: one link per line body / cell.
        Add(cases, "link on table cells keeps pipes", "«| a | b |\n|---|---|»",
            "«| [a](u) | [b](u) |\n|---|---|»", Link("u"));

        // Inline code (bughunt 8): a marker inside the backticks stays literal, so an emphasis
        // takes the whole run and a caret or link inside one declines.
        Add(cases, "bold at inline code inner edge returns null", "`‸abcd`", null, Inline("**"));
        Add(cases, "bold inside inline code word wraps the run", "`a‸b`", "**`a‸b`**", Inline("**"));
        Add(cases, "bold around inline code widens to the run", "`«abcd»`", "**«`abcd`»**", Inline("**"));
        Add(cases, "bold on part of inline code widens to the run", "`ab«cd»`", "**«`abcd`»**", Inline("**"));
        Add(cases, "link inside inline code returns null", "`ab‸cd`", null, Link("u"));
        Add(cases, "image inside inline code returns null", "`ab‸cd`", null, Image("p.png", "a"));
        Add(cases, "image inside fence returns null", "```\n‸\n```", null, Image("p.png", "a"));
        Add(cases, "span color at inline code inner edge returns null", "`‸ab`", null, Span("#FF0000"));
        Add(cases, "span color inside inline code wraps the run", "`a‸b`",
            "<span style=\"color:#FF0000\">`a‸b`</span>", Span("#FF0000"));

        // Toggling off at the inner edge right after the opening marker steps outside the run
        // instead of arming a second pair ("****x**abc**", bughunt 7).
        Add(cases, "bold caret right after opener steps outside", "**‸abc**", "‸**abc**", Inline("**"));
        Add(cases, "italic caret right after opener steps outside", "*‸abc*", "‸*abc*", Inline("*"));
        Add(cases, "strike caret right after opener steps outside", "~~‸abc~~", "‸~~abc~~", Inline("~~"));

        // Image labels drop brackets and line breaks: a ']' in a file name ended the alt label
        // early (bughunt 13).
        Add(cases, "image alt drops a closing bracket", "‸", "![ab](p.png)‸", Image("p.png", "a]b"));
        Add(cases, "image alt drops brackets and line breaks", "‸", "![ab](p.png)‸", Image("p.png", "[a]\r\nb"));

        // Markers followed by a tab are the same markers the screen draws (bughunt 14).
        Add(cases, "heading sees a tab after the marker", "#\t«title»", "«title»", Heading(1));
        Add(cases, "bullet sees a tab after the marker", "-\t«item»", "«item»", ListAs(ListKind.Bullet));
        Add(cases, "number sees a tab after the marker", "1.\t«a»", "«a»", ListAs(ListKind.Number));
        Add(cases, "quote sees a tab after the marker", ">\t«a»", "«a»", QuoteCommand());
        Add(cases, "task check sees a tab after the box", "- [ ]\t‸a", "- [x]\t‸a", Task());
        Add(cases, "task toggle sees a tab after the box", "- [ ]\t«a»", "«a»", ListAs(ListKind.Task));

        // The inserted placeholder text follows the UI language (bughunt 16): the host passes
        // T("링크") / T("제목") into the command.
        Add(cases, "link placeholder follows the UI language", "‸", "[«Talk»](u)", LinkLabel("u", "Talk"));
        Add(cases, "table header follows the UI language", "‸", "| «Title1» | Title2 |\n|---|---|", TableHead(2, "Title"));

        return cases;
    }

    // Rows are document path, image path, expected reference. A same-drive file under the
    // document's folder keeps a relative '/'-separated path; '#', '?', '%', the space and the
    // parentheses are percent-encoded (bughunt4 P3-3), so a file named "a#b%.png" is written as
    // "a%23b%25.png" and the loaders decode that once before opening the file.
    static string[][] BuildImageReferenceCases() {
        return new string[][] {
            new string[] { "C:\\docs\\doc.md", "C:\\docs\\pic.png", "pic.png" },
            new string[] { "C:\\docs\\doc.md", "C:\\docs\\a#b.png", "a%23b.png" },
            new string[] { "C:\\docs\\doc.md", "C:\\docs\\50%.png", "50%25.png" },
            new string[] { "C:\\docs\\doc.md", "C:\\docs\\a b.png", "a%20b.png" },
            new string[] { "C:\\docs\\doc.md", "C:\\docs\\a(1).png", "a%281%29.png" },
            new string[] { "C:\\docs\\doc.md", "C:\\docs\\img\\a#b% c.png", "img/a%23b%25%20c.png" }
        };
    }

    // Rows are operation ("slug" or "target"), input, expected.
    static string[][] BuildAnchorCases() {
        return new string[][] {
            new string[] { "slug", "제목", "제목" },
            new string[] { "slug", "한글 제목", "한글-제목" },
            new string[] { "slug", "Hello World", "hello-world" },
            new string[] { "slug", "  앞뒤 공백  ", "앞뒤-공백" },
            new string[] { "slug", "A & B", "a--b" },
            new string[] { "slug", "C# 기초", "c-기초" },
            new string[] { "slug", "snake_case 이름", "snake_case-이름" },
            new string[] { "slug", "dot.name", "dotname" },
            new string[] { "slug", "a  b", "a--b" },
            new string[] { "slug", "", "" },
            new string[] { "target", "#제목", "제목" },
            new string[] { "target", "#HELLO-World", "hello-world" },
            new string[] { "target", "#%ED%95%9C%EA%B8%80", "한글" }
        };
    }
}
public partial class MainWindow {
    internal static int VerifyMarkdownCommands(string outDir) { return MarkdownCommandsProbe.Run(outDir); }
}
}
