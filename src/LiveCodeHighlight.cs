using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {
// Live preview port of the basic view's fenced-code colors (CodeHighlight.cs, DecorateCode).
// The tokenizer, keyword set and language set are reused, not copied: the partial part of
// Markdown at the bottom of this file reaches the private statics in CodeHighlight.cs because
// every Markdown source file declares the same partial class. Only the color table is mirrored
// here, because DecorateCode builds FlowDocument Runs and cannot be called from the live view.
// The host supplies "fence language of this code body line, or null" as a delegate, so a
// colorize call never scans the document and costs O(line length). Fence lines and unknown
// languages are left untouched; only the foreground of colored tokens changes.
internal sealed class LiveCodeHighlight : DocumentColorizingTransformer {
    internal enum TokenKind { None=0, Comment=1, String=2, Number=3, Keyword=4 }
    internal struct Token { internal int Start, Length; internal TokenKind Kind; }
    // Same light/dark values as the inline table on CodeHighlight.cs:16.
    const string CommentTextHex="#5A7150", CommentDarkHex="#91A18C";
    const string StringTextHex="#92512D", StringDarkHex="#DAB48B";
    const string NumberTextHex="#246D58", NumberDarkHex="#A6C7B7";
    const string KeywordTextHex="#7541A0", KeywordDarkHex="#BAA8E0";
    static readonly Brush CommentText=Ui.Brush(CommentTextHex), CommentDark=Ui.Brush(CommentDarkHex);
    static readonly Brush StringText=Ui.Brush(StringTextHex), StringDark=Ui.Brush(StringDarkHex);
    static readonly Brush NumberText=Ui.Brush(NumberTextHex), NumberDark=Ui.Brush(NumberDarkHex);
    static readonly Brush KeywordText=Ui.Brush(KeywordTextHex), KeywordDark=Ui.Brush(KeywordDarkHex);
    readonly Func<DocumentLine,string> languageOf;
    readonly Func<bool> isDark;
    // Primary form: the host answers the fence language per line from its own per-line state
    // (null on fence lines and on every line outside a fenced block).
    internal LiveCodeHighlight(Func<DocumentLine,string> languageOf, Func<bool> isDark) {
        this.languageOf = languageOf;
        this.isDark = isDark;
    }
    // Convenience form for a host whose per-line state only distinguishes code body lines
    // (live LineInfo has Code/FenceLine but no information string): the language is recovered
    // from the block's opening fence by LanguageResolver and cached per block.
    internal LiveCodeHighlight(TextDocument document, Func<DocumentLine,bool> isCodeBody, Func<bool> isDark) {
        languageOf = LanguageResolver(document, isCodeBody);
        this.isDark = isDark;
    }
    protected override void ColorizeLine(DocumentLine line) {
        if (line == null || line.Length == 0 || languageOf == null) return;
        string language = languageOf(line);
        if (language == null || language.Length == 0) return;
        string text = CurrentContext.Document.GetText(line.Offset, line.Length);
        bool dark = isDark != null && isDark();
        foreach (Token token in Tokens(text, language)) {
            int start = line.Offset + token.Start;
            Brush brush = TokenBrush(token.Kind, dark);
            if (brush == null) continue;
            ChangeLinePart(start, start + token.Length, delegate(VisualLineElement element) {
                element.TextRunProperties.SetForegroundBrush(brush);
            });
        }
    }
    // Pure tokenizer for probes: the colored spans of one line of code, in document order.
    // Unknown or plain-text languages and uncolored words produce no span. Comments and
    // strings that span lines are colored on each line only (no state is carried across
    // lines, so the cost stays O(line); the basic view sees the whole block at once).
    internal static List<Token> Tokens(string text, string language) {
        var tokens = new List<Token>();
        if (text == null || text.Length == 0 || !Markdown.IsCodeTokenLanguage(language)) return tokens;
        int at = 0;
        while (at < text.Length) {
            Match match = Markdown.NextCodeToken(text, at);
            if (!match.Success || match.Length <= 0) break;
            TokenKind kind = TokenKindOf(match);
            if (kind != TokenKind.None) tokens.Add(new Token { Start=match.Index, Length=match.Length, Kind=kind });
            at = match.Index + match.Length;
        }
        return tokens;
    }
    static TokenKind TokenKindOf(Match match) {
        if (match.Groups["comment"].Success) return TokenKind.Comment;
        if (match.Groups["str"].Success) return TokenKind.String;
        if (match.Groups["num"].Success) return TokenKind.Number;
        if (match.Groups["word"].Success && Markdown.IsCodeTokenKeyword(match.Value)) return TokenKind.Keyword;
        return TokenKind.None;
    }
    internal static Brush TokenBrush(TokenKind kind, bool dark) {
        switch (kind) {
            case TokenKind.Comment: return dark ? CommentDark : CommentText;
            case TokenKind.String: return dark ? StringDark : StringText;
            case TokenKind.Number: return dark ? NumberDark : NumberText;
            case TokenKind.Keyword: return dark ? KeywordDark : KeywordText;
        }
        return null;
    }
    // Hex twin of TokenBrush for probes that assert colors without touching brushes.
    internal static string TokenColorHex(TokenKind kind, bool dark) {
        switch (kind) {
            case TokenKind.Comment: return dark ? CommentDarkHex : CommentTextHex;
            case TokenKind.String: return dark ? StringDarkHex : StringTextHex;
            case TokenKind.Number: return dark ? NumberDarkHex : NumberTextHex;
            case TokenKind.Keyword: return dark ? KeywordDarkHex : KeywordTextHex;
        }
        return null;
    }
    // Information string of a fence opener: "```python", "~~~js title=x", up to three leading
    // spaces (same shape as the live ReadFence and MDNote.cs's fence regex, which rejects a
    // backtick fence whose info string contains a backtick). Returns null for non-fence lines.
    internal static string FenceLanguage(string text) {
        if (text == null) return null;
        int p = 0;
        while (p < text.Length && p < 4 && text[p] == ' ') p++;
        if (p > 3 || p >= text.Length) return null;
        char c = text[p];
        if (c != '`' && c != '~') return null;
        int start = p;
        while (p < text.Length && text[p] == c) p++;
        if (p - start < 3) return null;
        string info = text.Substring(p).Trim();
        if (info.Length == 0) return null;
        if (c == '`' && info.IndexOf('`') >= 0) return null;
        return info;
    }
    // Fallback language source: walks back over the host's code body lines to the opening
    // fence and caches it. Lines are colorized top to bottom, so only the first line of a
    // block pays the walk; the following lines re-read the (short) fence line so an edited
    // information string is picked up on the next render.
    internal static Func<DocumentLine,string> LanguageResolver(TextDocument document, Func<DocumentLine,bool> isCodeBody) {
        return new FenceLanguages(document, isCodeBody).LanguageFor;
    }
    sealed class FenceLanguages {
        readonly TextDocument document;
        readonly Func<DocumentLine,bool> isCodeBody;
        DocumentLine fence, lastBody;
        internal FenceLanguages(TextDocument document, Func<DocumentLine,bool> isCodeBody) {
            this.document = document;
            this.isCodeBody = isCodeBody;
        }
        internal string LanguageFor(DocumentLine line) {
            if (line == null || isCodeBody == null || !isCodeBody(line)) { lastBody = null; return null; }
            if (fence != null && lastBody != null && line.PreviousLine == lastBody && Alive(fence)) {
                string current = FenceLanguage(document.GetText(fence.Offset, fence.Length));
                if (current != null) { lastBody = line; return current; }
            }
            DocumentLine previous = line.PreviousLine;
            while (previous != null && isCodeBody(previous)) previous = previous.PreviousLine;
            string found = previous == null ? null : FenceLanguage(document.GetText(previous.Offset, previous.Length));
            if (found == null) { lastBody = null; return null; }
            fence = previous;
            lastBody = line;
            return found;
        }
        // A whole-document replace detaches DocumentLine objects without IsDeleted, so the
        // cached fence is only trusted while the document still returns the same object.
        bool Alive(DocumentLine line) {
            return line != null && !line.IsDeleted && line.LineNumber >= 1 && line.LineNumber <= document.LineCount &&
                document.GetLineByNumber(line.LineNumber) == line;
        }
    }
}
// Thin wrappers over the basic view's tokenizer. Keep the rules themselves in CodeHighlight.cs:
// these three fields are private members of the same partial class, so nothing is duplicated.
public static partial class Markdown {
    internal static Match NextCodeToken(string text, int start) { return codeTokens.Match(text, start); }
    internal static bool IsCodeTokenKeyword(string word) { return codeKeywords.Contains(word); }
    internal static bool IsCodeTokenLanguage(string language) {
        if (language == null) return false;
        if (codeLanguages.Contains(language)) return true;
        return codeLanguages.Contains(language.Split(' ')[0]);
    }
}
}
