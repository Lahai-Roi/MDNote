using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {
// 2.5.0: a ```mermaid fenced block drawn as a diagram in the editor (basic and live view are one engine).
// The block stays an ordinary fenced code block for the block cache; a session only lays a picture over
// it: the opener line carries a MermaidElement and the lines below are folded like a display formula's.
// Nothing in MermaidRender or the session part names MermaidEngine / SvgToWpf: both are reached only
// through MermaidGate, and only after a mermaid block was built, so a document without one never
// touches the engine, the Pele resource or chakra.dll (MermaidLiveProbe checks this).
internal sealed class MermaidPicture {
    // A frozen drawing with its origin at (0,0); null = the SVG did not convert.
    internal Drawing Drawing;
    internal double Width, Height;
    // The SVG key (source + font + size + pixelsPerDip) and the theme the drawing was converted for.
    internal string Key;
    internal bool Dark;
}
static class MermaidRender {
    // Lines after the opener, the closer included. A longer block stays a code block. Only the scan of
    // one block reads this far, and never on a keystroke (the session rescans on its idle timer).
    internal const int BlockLines = 400;
    // MermaidEngine.MaxSourceChars; kept as an own constant so this class names no engine type.
    internal const int SourceLimit = 20000;
    internal const int DebounceMilliseconds = 300;
    const int Capacity = 64;
    internal const int Unknown = 0, Pending = 1, Ready = 2, Failed = 3;
    // The idle pre-render pass (Session.MermaidPreRenderNow): the block cap of one pass, and how many
    // viewports below the view its range reaches (nothing above the view top: a fold there moves the text in view).
    // Light theme only: Pele's default alt (#e6e6e6) mixed half with the white page is #f3f3f3, the node
    // fill itself, so a subgraph box and the boxes in it read as one colour (user 11-15). A blue-grey alt
    // keeps the cluster, notes and done tasks apart from the nodes. Screen, print and HTML use this value.
    internal const string LightSurfaceAlt = "#cfd6e0";
    internal const int PreRenderBlocks = 8;
    internal const int PreRenderBelow = 2;
    // Probe counters: engine requests sent, SVG conversions, picture cache hits.
    internal static long DiagRequests, DiagConverts, DiagPictureHits;
    // Block scans (closer + source), and the ones made while a line was laid out (first sight only).
    internal static long DiagScans, DiagLayoutScans;
    // Idle pre-render passes, the document lines their walks read, and the blocks they prepared.
    internal static long DiagPrePasses, DiagPreLines, DiagPreBlocks;
    internal static string LastError;
    // Probe only: a negative control that shows the idle pre-render pass itself is what made the blocks
    // just below the view ready (their source is not cached without it).
    internal static bool PreRenderOff;
    // Probe only: a negative control for the caret anchor (MermaidAnchorSchedule) that leaves the settle
    // out, so the flip's jump shows the anchor is what keeps the caret line still.
    internal static bool CaretAnchorOff;
    // Raised on the requesting dispatcher when a request finished (ok or failed); the argument is the key.
    internal static event Action<string> Completed;

    // "```mermaid" / "~~~mermaid" (up to three leading spaces; the info string's first word, any case).
    internal static bool IsMermaidFence(string text) {
        if (text == null) return false;
        int p = 0, length = text.Length;
        while (p < length && p < 4 && text[p] == ' ') p++;
        if (p > 3 || p >= length) return false;
        char marker = text[p];
        if (marker != '`' && marker != '~') return false;
        int start = p;
        while (p < length && text[p] == marker) p++;
        if (p - start < 3) return false;
        while (p < length && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p + 7 > length) return false;
        if (string.Compare(text, p, "mermaid", 0, 7, StringComparison.OrdinalIgnoreCase) != 0) return false;
        p += 7;
        return p >= length || text[p] == ' ' || text[p] == '\t';
    }
    // The part of a key after the source: a font, size or DPI change makes a new key (a new render).
    internal static string Suffix(string family, double size, double pixelsPerDip) {
        return "\u0001" + family + "\u0001" + size.ToString("0.###", CultureInfo.InvariantCulture) + "\u0001" + pixelsPerDip.ToString("0.###", CultureInfo.InvariantCulture);
    }

    // ---- caches (UI thread only) ---------------------------------------------------------

    sealed class Svg { internal string Key, Text, Error; }
    static readonly Dictionary<string, LinkedListNode<Svg>> svgs = new Dictionary<string, LinkedListNode<Svg>>(StringComparer.Ordinal);
    static readonly LinkedList<Svg> svgOrder = new LinkedList<Svg>();
    static readonly Dictionary<string, LinkedListNode<MermaidPicture>> pictures = new Dictionary<string, LinkedListNode<MermaidPicture>>(StringComparer.Ordinal);
    static readonly LinkedList<MermaidPicture> pictureOrder = new LinkedList<MermaidPicture>();
    static readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
    internal static int PendingCount { get { return pending.Count; } }
    internal static int CachedSvgs { get { return svgs.Count; } }
    internal static int CachedPictures { get { return pictures.Count; } }
    internal static void ClearCaches() { svgs.Clear(); svgOrder.Clear(); pictures.Clear(); pictureOrder.Clear(); }

    static string PictureKey(string key, bool dark) { return key + (dark ? "\u0001D" : "\u0001L"); }

    // Unknown (never asked or evicted), Pending, Ready (an SVG is cached) or Failed (the error is cached,
    // so a broken source is not asked again on every redraw).
    internal static int State(string key) {
        if (key == null) return Failed;
        if (pending.Contains(key)) return Pending;
        LinkedListNode<Svg> node;
        if (!svgs.TryGetValue(key, out node)) return Unknown;
        svgOrder.Remove(node); svgOrder.AddFirst(node);
        return node.Value.Text != null ? Ready : Failed;
    }
    static void Store(string key, string svg, string error) {
        LinkedListNode<Svg> node;
        if (svgs.TryGetValue(key, out node)) { svgOrder.Remove(node); svgs.Remove(key); }
        while (svgs.Count >= Capacity && svgOrder.Last != null) {
            svgs.Remove(svgOrder.Last.Value.Key);
            svgOrder.RemoveLast();
        }
        if (svg == null && error == null) error = "no result";
        if (error != null) LastError = error;
        svgs[key] = svgOrder.AddFirst(new Svg { Key = key, Text = error == null ? svg : null, Error = error });
    }
    // The drawing of a cached SVG in one theme, or null (nothing cached, a failed render or a failed
    // conversion). A theme switch converts from the SVG cache; the engine is not asked again.
    internal static MermaidPicture Picture(string key, bool dark, string family, double pixelsPerDip) {
        if (key == null) return null;
        string pictureKey = PictureKey(key, dark);
        LinkedListNode<MermaidPicture> node;
        if (pictures.TryGetValue(pictureKey, out node)) {
            pictureOrder.Remove(node); pictureOrder.AddFirst(node);
            DiagPictureHits++;
            return node.Value.Drawing == null ? null : node.Value;
        }
        LinkedListNode<Svg> svg;
        if (!svgs.TryGetValue(key, out svg) || svg.Value.Text == null) return null;
        var picture = new MermaidPicture { Key = key, Dark = dark };
        try {
            DiagConverts++;
            double width, height;
            string error;
            picture.Drawing = MermaidGate.Convert(svg.Value.Text, dark, family, pixelsPerDip, out width, out height, out error);
            picture.Width = width; picture.Height = height;
            if (picture.Drawing == null) LastError = error;
            else if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height)) { picture.Drawing = null; LastError = "no size"; }
        } catch (Exception ex) {
            picture.Drawing = null;
            LastError = ex.GetType().Name + ": " + ex.Message;
        }
        while (pictures.Count >= Capacity && pictureOrder.Last != null) {
            MermaidPicture last = pictureOrder.Last.Value;
            pictures.Remove(PictureKey(last.Key, last.Dark));
            pictureOrder.RemoveLast();
        }
        pictures[pictureKey] = pictureOrder.AddFirst(picture);
        return picture.Drawing == null ? null : picture;
    }
    // One request per key at a time: a key that is waiting or already cached is not sent again.
    internal static void Request(string key, string source, string family, double size, double pixelsPerDip, Dispatcher reply) {
        if (key == null || source == null || pending.Contains(key) || svgs.ContainsKey(key)) return;
        if (source.Length > SourceLimit) { Store(key, null, "too large"); return; }
        pending.Add(key);
        DiagRequests++;
        try {
            MermaidGate.Request(key, source, family, size, pixelsPerDip, reply, Done);
        } catch (Exception ex) {
            // A missing engine type or resource: remembered as this key's failure, the block stays code.
            pending.Remove(key);
            Store(key, null, ex.GetType().Name + ": " + ex.Message);
        }
    }
    static void Done(string key, string svg, string error) {
        pending.Remove(key);
        Store(key, svg, error);
        Action<string> completed = Completed;
        if (completed == null) return;
        foreach (Delegate handler in completed.GetInvocationList()) {
            try { ((Action<string>)handler)(key); } catch (Exception ex) { LastError = ex.GetType().Name + ": " + ex.Message; }
        }
    }
}

// Every MermaidEngine / SvgToWpf reference lives here (the boundary MathEngine is for WpfMath): the
// methods are entered only after a mermaid block exists, so their types load only then.
static class MermaidGate {
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void Request(string key, string source, string family, double size, double pixelsPerDip, Dispatcher reply, Action<string, string, string> done) {
        MermaidEngine.Request(key, source, family, size, pixelsPerDip, reply, delegate(MermaidResult result) {
            if (result == null) done(key, null, "no result");
            else done(key, result.Svg, result.Error);
        });
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static Drawing Convert(string svg, bool dark, string family, double pixelsPerDip, out double width, out double height, out string error) {
        var theme = new SvgTheme();
        theme.FontFamily = family;
        theme.PixelsPerDip = pixelsPerDip;
        if (dark) {
            // Same as the editor's dark background (MarkdownLivePreview palette), so a filled --pele-bg shows no box.
            theme.Vars["--pele-bg"] = "#202124";
            theme.Vars["--pele-fg"] = "#e0e0e0";
            theme.Vars["--pele-muted"] = "#a0a0a0";
            theme.Vars["--pele-line"] = "#c8c8c8";
            // Boxes lift clearly off the #272727 page (user 11-15: #2b2b2b boxes read as no fill at all).
            theme.Vars["--pele-surface"] = "#3c3f44";
            theme.Vars["--pele-surface-alt"] = "#565a62";
            theme.Vars["--pele-border"] = "#7d7d7d";
            theme.Vars["--pele-accent"] = "#7aa2f7";
        } else theme.Vars["--pele-surface-alt"] = MermaidRender.LightSurfaceAlt;
        return SvgToWpf.Convert(svg, theme, out width, out height, out error);
    }
}

// The element for one diagram on its opener line: one visual column over the fence line's text, like a
// display formula. Slot and Scale are what the last Format laid out (the press hit test reads them).
internal sealed class MermaidElement : VisualLineElement {
    readonly MermaidPicture picture;
    readonly Func<double> available;
    internal double Slot, Scale = 1;
    internal MermaidElement(int documentLength, MermaidPicture picture, Func<double> available) : base(1, documentLength) {
        this.picture = picture; this.available = available;
    }
    internal MermaidPicture Picture { get { return picture; } }
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
        return new MermaidRun(this, TextRunProperties, picture, available);
    }
}
internal sealed class MermaidRun : TextEmbeddedObject {
    readonly MermaidElement owner;
    readonly TextRunProperties properties;
    readonly MermaidPicture picture;
    readonly Func<double> available;
    double slot, scale = 1;
    internal MermaidRun(MermaidElement owner, TextRunProperties properties, MermaidPicture picture, Func<double> available) {
        this.owner = owner; this.properties = properties; this.picture = picture; this.available = available;
    }
    public override int Length { get { return 1; } }
    public override TextRunProperties Properties { get { return properties; } }
    public override CharacterBufferReference CharacterBufferReference { get { return new CharacterBufferReference(); } }
    public override LineBreakCondition BreakBefore { get { return LineBreakCondition.BreakDesired; } }
    public override LineBreakCondition BreakAfter { get { return LineBreakCondition.BreakDesired; } }
    // The diagram fills the line to centre itself and shrinks to the view width, so its size follows the paragraph width.
    public override bool HasFixedSize { get { return false; } }
    public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
        double view = available == null ? 0 : available();
        double room = Math.Min(Math.Max(0, remainingParagraphWidth - 2), Math.Max(0, view));
        // Scaled down to the text width when wider, never scaled up.
        scale = picture.Width > room && room > 8 ? room / picture.Width : 1;
        slot = Math.Max(picture.Width * scale, room);
        owner.Slot = slot; owner.Scale = scale;
        double height = picture.Height * scale;
        return new TextEmbeddedObjectMetrics(slot, height, height);
    }
    public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) {
        return new Rect(0, 0, slot, picture.Height * scale);
    }
    public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) {
        if (picture.Drawing == null) return;
        // origin is the baseline, which Format put at the picture's bottom edge.
        double x = origin.X + Math.Max(0, (slot - picture.Width * scale) / 2);
        drawingContext.PushTransform(new TranslateTransform(x, origin.Y - picture.Height * scale));
        if (scale != 1) drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.DrawDrawing(picture.Drawing);
        if (scale != 1) drawingContext.Pop();
        drawingContext.Pop();
    }
}

internal static partial class MarkdownLivePreview {
    internal sealed partial class Session {
        // One record per mermaid opener line this session has built. Closer is the closing fence found
        // by the last scan (null = unclosed, too long); the block's range is read from the two line
        // objects, so edits elsewhere never need a rescan. Dirty = an edit touched the range: the block
        // is a plain code block until the idle timer (or the caret leaving it) rescans. Shown is the
        // picture in use, held here so a cache eviction or a pending re-render keeps it on screen.
        // Wait = dirty for a reason other than an edit inside it (first seen while typing, a fence typed
        // above it): only the idle timer rescans it, never a caret move.
        sealed class MermaidBlock {
            internal DocumentLine Opener, Closer;
            internal string Source, Key, Suffix;
            internal bool Dirty, Folded, Wait;
            internal MermaidPicture Shown;
        }
        // null until the first mermaid opener is built: a document without one pays a null check.
        Dictionary<DocumentLine, MermaidBlock> mermaid;
        DispatcherTimer mermaidTimer;
        Action<string> mermaidCompleted;
        List<MermaidBlock> mermaidReleased;
        // First/last line number pairs of the blocks the current caret move flipped.
        List<int> mermaidFlips;
        // One pending MermaidSeeTop walk (a viewport that landed inside a block).
        DispatcherOperation mermaidTopWork;
        // The first visible line and scroll offset the last walk read. A layout that changed neither
        // (typing, a caret move inside the viewport) pays two compares and nothing else; a document
        // replace swaps the DocumentLine object, so the walk runs again.
        DocumentLine mermaidTopSeen;
        double mermaidTopSeenOffset = double.NaN;
        // One pending idle pre-render pass for the blocks just outside the view (MermaidPreRenderNow),
        // and the first visible line and offset it was scheduled for: a layout that changed neither has
        // nothing new outside the view, the same dedupe MermaidSeeTop makes.
        DispatcherOperation mermaidPreWork;
        DocumentLine mermaidPreSeen;
        double mermaidPreSeenOffset = double.NaN;
        // The anchor a caret move that flips folds keeps still: the caret's new line, its screen y in the
        // layout still on screen before the first flip, the caret line the move left, and the one pending
        // scroll that puts the line back at that height after the fold has been laid out.
        int mermaidAnchorLine;
        double mermaidAnchorY;
        int mermaidAnchorCaretLine;
        DispatcherOperation mermaidAnchorWork;
        bool mermaidEdited;
        // The edit being applied touches a line that is a fence (set before the change, read after it).
        bool mermaidFenceEdit;
        // When the document was last edited (DocumentChanging, also with no block yet): a line laid out
        // right after an edit never scans a block.
        bool mermaidEditSeen;
        int mermaidEditTick;
        internal int MermaidBlockCount { get { return mermaid == null ? 0 : mermaid.Count; } }
        // Probe: the blocks whose source lines are folded under a picture now.
        internal int MermaidFoldCount {
            get {
                int count = 0;
                if (mermaid != null) foreach (MermaidBlock block in mermaid.Values) if (MermaidFoldInstalled(block)) count++;
                return count;
            }
        }
        void MermaidEditing() { mermaidEditSeen = true; mermaidEditTick = Environment.TickCount; }
        bool MermaidEditRecent() {
            return mermaidEditSeen && unchecked(Environment.TickCount - mermaidEditTick) < MermaidRender.DebounceMilliseconds;
        }
        // "```" or "~~~" after at most three spaces: at most six characters of the line are read.
        bool MermaidFenceStart(DocumentLine line) {
            int length = line.Length, offset = line.Offset, p = 0;
            while (p < length && p < 3 && Document.GetCharAt(offset + p) == ' ') p++;
            if (p + 3 > length) return false;
            char marker = Document.GetCharAt(offset + p);
            if (marker != '`' && marker != '~') return false;
            return Document.GetCharAt(offset + p + 1) == marker && Document.GetCharAt(offset + p + 2) == marker;
        }

        void MermaidStart() {
            mermaid = new Dictionary<DocumentLine, MermaidBlock>();
            mermaidTimer = new DispatcherTimer(DispatcherPriority.Background, View.Dispatcher);
            mermaidTimer.Interval = TimeSpan.FromMilliseconds(MermaidRender.DebounceMilliseconds);
            mermaidTimer.Tick += MermaidTick;
            mermaidCompleted = MermaidCompleted;
            MermaidRender.Completed += mermaidCompleted;
        }
        void MermaidDispose() {
            if (mermaidTimer != null) { mermaidTimer.Stop(); mermaidTimer.Tick -= MermaidTick; mermaidTimer = null; }
            if (mermaidCompleted != null) { MermaidRender.Completed -= mermaidCompleted; mermaidCompleted = null; }
            if (mermaidTopWork != null) { mermaidTopWork.Abort(); mermaidTopWork = null; }
            if (mermaidPreWork != null) { mermaidPreWork.Abort(); mermaidPreWork = null; }
            MermaidAnchorCancel();
            mermaid = null;
            mermaidReleased = null;
        }
        // restart = an edit: the 300 ms count starts again. Otherwise a running timer keeps its time.
        void MermaidKick(bool restart) {
            if (disposed || mermaidTimer == null) return;
            if (restart) mermaidTimer.Stop();
            if (!mermaidTimer.IsEnabled) mermaidTimer.Start();
        }
        // Still this document's line (a whole-document replace detaches lines without marking them deleted).
        bool MermaidAlive(DocumentLine line) {
            return line != null && Object.ReferenceEquals(CurrentDocumentLine(line), line);
        }
        bool MermaidRange(MermaidBlock block, out int first, out int last) {
            first = last = 0;
            if (!MermaidAlive(block.Opener) || !MermaidAlive(block.Closer)) return false;
            first = block.Opener.LineNumber;
            last = block.Closer.LineNumber;
            return last > first;
        }
        bool MermaidFoldInstalled(MermaidBlock block) {
            CollapsedLineSection section;
            return mathSections.TryGetValue(block.Opener, out section) && section.IsCollapsed;
        }
        // The caret or a selection is in the block: its source shows. Reading mode never reveals it, and
        // the document-end stop after a block that closes the document keeps the picture (the rule
        // MathEndCaretOutside has for a display formula).
        bool MermaidActive(int first, int last) {
            if (Options.ReadOnly || Editor.IsReadOnly) return false;
            if (!BlockActive(first, last, caretLine, selectionStart, selectionEnd)) return false;
            if (caretLine != last || last != Document.LineCount) return true;
            if (!Editor.TextArea.Selection.IsEmpty) return true;
            return Editor.TextArea.Caret.Offset != Document.TextLength;
        }
        // Closer and source of one block: at most MermaidRender.BlockLines fence tests and one text
        // copy of at most SourceLimit characters. Never called from a keystroke on a known block.
        void MermaidScan(MermaidBlock block) {
            MermaidRender.DiagScans++;
            block.Dirty = false;
            block.Wait = false;
            block.Closer = null;
            block.Source = null;
            block.Key = null;
            block.Suffix = null;
            DocumentLine opener = block.Opener;
            if (!MermaidAlive(opener) || !Info(opener).Mermaid) return;
            char marker; int markerLength; bool blank;
            ReadFence(Document, opener, out marker, out markerLength, out blank);
            if (marker == '\0' || markerLength < 3) return;
            int count = 0;
            DocumentLine closer = null;
            for (DocumentLine next = opener.NextLine; next != null && count < MermaidRender.BlockLines; next = next.NextLine) {
                count++;
                char other; int otherLength; bool otherBlank;
                ReadFence(Document, next, out other, out otherLength, out otherBlank);
                if (other == marker && otherLength >= markerLength && otherBlank) { closer = next; break; }
            }
            if (closer == null) return;
            block.Closer = closer;
            int start = opener.Offset + opener.TotalLength, length = closer.Offset - start;
            if (count < 2 || length <= 0 || length > MermaidRender.SourceLimit) return;
            string text = Document.GetText(start, length);
            if (text.IndexOf('\r') >= 0) text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = text.TrimEnd('\n', ' ', '\t');
            if (text.Trim().Length == 0) return;
            block.Source = text;
        }
        void MermaidKeyOf(MermaidBlock block) {
            string suffix = MermaidRender.Suffix(Options.Family, Options.FontSize, PixelsPerDip());
            if (block.Key != null && string.Equals(block.Suffix, suffix, StringComparison.Ordinal)) return;
            block.Suffix = suffix;
            block.Key = block.Source + suffix;
        }
        // Brings block.Shown up to date from the caches. A source that is not rendered yet keeps the
        // previous picture (re-themed from the SVG cache when the theme changed); a failed one drops it.
        void MermaidRefreshPicture(MermaidBlock block) {
            MermaidKeyOf(block);
            bool dark = Options.Dark;
            MermaidPicture shown = block.Shown;
            if (shown != null && shown.Dark == dark && string.Equals(shown.Key, block.Key, StringComparison.Ordinal)) return;
            int state = MermaidRender.State(block.Key);
            if (state == MermaidRender.Ready) { block.Shown = MermaidRender.Picture(block.Key, dark, Options.Family, PixelsPerDip()); return; }
            if (state == MermaidRender.Failed) { block.Shown = null; return; }
            if (shown != null && shown.Dark != dark) {
                MermaidPicture themed = MermaidRender.Picture(shown.Key, dark, Options.Family, PixelsPerDip());
                if (themed != null) block.Shown = themed;
            }
            if (state == MermaidRender.Unknown) MermaidKick(false);
        }
        // True when the block is drawn as its picture now.
        bool MermaidDecide(MermaidBlock block) {
            if (block.Dirty) return false;
            int first, last;
            if (!MermaidRange(block, out first, out last)) return false;
            if (block.Source == null) { block.Shown = null; return false; }
            MermaidRefreshPicture(block);
            if (block.Shown == null) return false;
            return !MermaidActive(first, last);
        }
        // The fold under a drawn block lives in mathSections, so the display-formula paths (the opener's
        // visual line spanning the fold, the release before an edit, the clear on a rebuild) serve it too.
        void MermaidFold(MermaidBlock block, bool show) {
            CollapsedLineSection section;
            DocumentLine opener = block.Opener;
            if (mathSections.TryGetValue(opener, out section)) {
                if (show && section.IsCollapsed && section.Start == opener.NextLine && section.End == block.Closer) { block.Folded = true; return; }
                if (section.IsCollapsed) section.Uncollapse();
                mathSections.Remove(opener);
            }
            block.Folded = false;
            if (!show || opener.NextLine == null || block.Closer == null) return;
            mathSections.Add(opener, View.CollapseLines(opener.NextLine, block.Closer));
            block.Folded = true;
        }
        // Gives the block's fold back (also for an opener line that left the document).
        void MermaidUnfold(MermaidBlock block) {
            CollapsedLineSection section;
            if (mathSections.TryGetValue(block.Opener, out section)) {
                if (section.IsCollapsed) section.Uncollapse();
                mathSections.Remove(block.Opener);
            }
            block.Folded = false;
        }
        void MermaidDrop(MermaidBlock block) {
            MermaidUnfold(block);
            if (mermaid != null) mermaid.Remove(block.Opener);
        }
        // While the opener's visual line is built (StrutGenerator): the fold is brought in line with what
        // the line is about to draw. Nothing is scanned here after an edit: a block first seen while
        // typing, or one whose closer left the document, turns dirty and waits for the idle timer. Only a
        // block first seen with no edit behind it (a document just opened, a scroll) is scanned on the
        // spot, once (at most MermaidRender.BlockLines fence tests), so a cached picture shows at once.
        // A line that stopped being an opener (its info word edited, a fence typed above it) keeps its
        // record and picture, unfolded and dirty: the idle pass drops it if it still is no opener then,
        // and an edit that is taken back before that gets its picture back without a render.
        internal void PrepareMermaid(DocumentLine line, LineInfo info) {
            MermaidBlock block = null;
            if (mermaid != null) mermaid.TryGetValue(line, out block);
            if (!info.Mermaid) {
                if (block == null) return;
                MermaidUnfold(block);
                bool fresh = !block.Dirty;
                block.Dirty = true;
                block.Wait = true;
                MermaidKick(fresh);
                return;
            }
            if (disposed) return;
            if (block == null) {
                if (mermaid == null) MermaidStart();
                block = new MermaidBlock { Opener = line };
                mermaid[line] = block;
                if (MermaidEditRecent()) { block.Dirty = true; block.Wait = true; MermaidKick(true); }
                else { MermaidRender.DiagLayoutScans++; MermaidScan(block); }
            } else if (!block.Dirty && block.Closer != null && !MermaidAlive(block.Closer)) {
                block.Dirty = true;
                block.Wait = true;
                MermaidKick(true);
            }
            MermaidFold(block, MermaidDecide(block));
        }
        // A viewport that landed inside a block without ever laying out its opener (a wheel jump, a
        // scrollbar drag, Find, Ctrl+G): the opener above is never laid out, so the block is unknown
        // and its source stays visible until the user scrolls up past the fence. This finds the opener
        // of the fence run the first visible line sits in and prepares it on the same first-sight path
        // a laid-out opener takes. Called from VisualLinesChanged, which runs inside MeasureOverride, so
        // the walk is deferred to a dispatcher turn: a fold must never be installed while the visual
        // lines are being built. A block already in the map makes the walk a no-op, so the layout that
        // follows a fold settles instead of looping. Guard: one cached parse of the first visible line,
        // which is not fence code on any ordinary layout.
        internal void MermaidSeeTop() {
            if (disposed || mermaidTopWork != null) return;
            if (Document == null || Editor.Document != Document) return;
            // A fold must not move the layout under a held press, a busy host or an open composition
            // (the holds MermaidSync keeps). Nothing is remembered, so the next layout tries again.
            if (frozen || Options.Busy || compositionLive || Editor.ImeBusy) return;
            if (!View.VisualLinesValid || View.VisualLines.Count == 0) return;
            VisualLine first = View.VisualLines[0];
            if (first == null || first.IsDisposed) return;
            DocumentLine top = first.FirstDocumentLine;
            // A layout that neither scrolled nor moved the first line (typing, a caret move inside the
            // viewport) reads what the last walk already read: no wheel jump can have happened.
            if (Object.ReferenceEquals(top, mermaidTopSeen) && View.VerticalOffset == mermaidTopSeenOffset) return;
            mermaidTopSeen = top;
            mermaidTopSeenOffset = View.VerticalOffset;
            if (!Info(top).Code) return;
            mermaidTopWork = View.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(MermaidSeeTopNow));
        }
        void MermaidSeeTopNow() {
            mermaidTopWork = null;
            if (disposed || Editor.Document != Document) return;
            // The state can move between the two turns: the same holds apply here.
            if (frozen || Options.Busy || compositionLive || Editor.ImeBusy) return;
            if (!View.VisualLinesValid || View.VisualLines.Count == 0) return;
            VisualLine first = View.VisualLines[0];
            if (first == null || first.IsDisposed) return;
            DocumentLine top = CurrentDocumentLine(first.FirstDocumentLine);
            if (top == null) return;
            if (!Info(top).Code) return;
            // The run's opener, by the block cache's own state: Before.Fence is open on every line of a
            // run but its opener, so the walk costs one cached state per line and no parse. Capped like a
            // block scan; the line it stops on is the first fence line above, mermaid or not.
            DocumentLine opener = top;
            int walked = 0;
            BlockState before = blocks.Before(opener.LineNumber);
            while (before.Fence != '\0') {
                if (walked >= MermaidRender.BlockLines) return;
                opener = opener.PreviousLine;
                if (opener == null) return;
                walked++;
                before = blocks.Before(opener.LineNumber);
            }
            LineInfo info = Info(opener);
            if (!info.FenceLine || !info.Mermaid) return;
            // A known block is already handled by the layout pass; nothing to prepare here.
            if (mermaid != null && mermaid.ContainsKey(opener)) return;
            // The first-sight path of a laid-out opener: one scan, one request (or a cached picture).
            PrepareMermaid(opener, info);
            if (mermaid == null) return;
            MermaidBlock block;
            if (!mermaid.TryGetValue(opener, out block)) return;
            // A cached picture folded the block on the spot. The opener's own row is above the viewport
            // and has no visual line yet, so the rows in view are rebuilt by the same pass a fold on
            // screen uses, which keeps the text in view where it was. A request still pending folds
            // when its render lands (MermaidCompleted -> MermaidSync).
            if (!MermaidFoldInstalled(block)) return;
            var changed = new List<MermaidBlock>(1);
            changed.Add(block);
            var shows = new List<bool>(1);
            shows.Add(true);
            MermaidApply(changed, shows);
        }
        // ---- idle pre-render of the blocks just below the view ---------------------------------
        // A block's opener is only prepared when its own line is laid out, so a block just below the
        // viewport stays source until the reader scrolls it in: 300 ms idle count plus the render (11-15
        // user: "after opening, scrolling down takes 1-2 s before the code block is a picture"). This pass
        // prepares the openers from the first visible line through two viewports below the view, in idle
        // time, on the same first-sight path a laid-out opener takes (PrepareMermaid). Nothing new is
        // requested here: the engine queue, its time limit and the caches stay as they are. Scheduled from
        // VisualLinesChanged (the one-line hook in MarkdownLivePreview.cs) once per new view top, at
        // ApplicationIdle so a scroll or a keystroke always wins, with the holds MermaidSeeTop keeps.
        // Nothing above the first visible line is prepared: a fold there would move the text in view, and
        // MermaidApply's scroll correction only covers a block above the caret, not one below it. A block
        // that scrolled out above the view was laid out (and so prepared) on the way down.
        internal void MermaidPreRender() {
            if (disposed || MermaidRender.PreRenderOff) return;
            if (Document == null || Editor.Document != Document) return;
            // A pass folds pictures; that must not move the layout under a held press, a busy host or an
            // open composition (the holds MermaidSync and MermaidSeeTop keep). Nothing is remembered, so
            // the next layout tries again.
            if (frozen || Options.Busy || compositionLive || Editor.ImeBusy) return;
            // Never during typing: a block's scans wait for the idle timer there (MermaidEditRecent).
            if (MermaidEditRecent()) return;
            if (!View.VisualLinesValid || View.VisualLines.Count == 0) return;
            VisualLine first = View.VisualLines[0];
            if (first == null || first.IsDisposed) return;
            DocumentLine top = first.FirstDocumentLine;
            // One walk per view top: a layout that neither scrolled nor moved the first line has nothing
            // new next to the viewport. A document replace swaps the DocumentLine object, so the walk runs.
            if (Object.ReferenceEquals(top, mermaidPreSeen) && View.VerticalOffset == mermaidPreSeenOffset) return;
            mermaidPreSeen = top;
            mermaidPreSeenOffset = View.VerticalOffset;
            // One pending pass at a time. It reads the view when it runs, so a scroll that lands before it
            // IS its pass: the newest position is the one prepared, never an older one.
            if (mermaidPreWork != null) return;
            mermaidPreWork = View.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(MermaidPreRenderNow));
        }
        // A pass skipped by a hold leaves no view top remembered, so the next layout tries again.
        void MermaidPreForget() { mermaidPreSeen = null; mermaidPreSeenOffset = double.NaN; }
        void MermaidPreRenderNow() {
            mermaidPreWork = null;
            if (disposed || MermaidRender.PreRenderOff) return;
            // The state can move between the two turns: the same holds apply here.
            if (Document == null || !Object.ReferenceEquals(Editor.Document, Document)) { MermaidPreForget(); return; }
            if (frozen || Options.Busy || compositionLive || Editor.ImeBusy) { MermaidPreForget(); return; }
            if (MermaidEditRecent()) { MermaidPreForget(); return; }
            if (!View.VisualLinesValid || View.VisualLines.Count == 0) { MermaidPreForget(); return; }
            VisualLine first = View.VisualLines[0];
            if (first == null || first.IsDisposed) { MermaidPreForget(); return; }
            DocumentLine top = CurrentDocumentLine(first.FirstDocumentLine);
            if (top == null) { MermaidPreForget(); return; }
            double viewport = Editor.ViewportHeight;
            if (viewport <= 0) viewport = View.ActualHeight;
            if (viewport <= 0) { MermaidPreForget(); return; }
            // The range in document lines, from the view's own heights, clamped to the document.
            int firstNumber = top.LineNumber;
            int lastNumber = View.GetDocumentLineByVisualTop(View.VerticalOffset + viewport * (1 + MermaidRender.PreRenderBelow)).LineNumber;
            int lineCount = Document.LineCount;
            if (lastNumber > lineCount) lastNumber = lineCount;
            if (lastNumber < firstNumber) lastNumber = firstNumber;
            int prepared = 0, walked = 0;
            // The block cache's own rule, one cached state per line and no parse: a fence run opens on the
            // line whose Before has no fence open and whose After has one. Every other line is skipped
            // without reading its text, so a document with no mermaid opener in range costs nothing more.
            BlockState state = blocks.Before(firstNumber);
            for (int number = firstNumber; number <= lastNumber; number++) {
                walked++;
                BlockState after = blocks.After(number);
                bool opens = state.Fence == '\0' && after.Fence != '\0';
                state = after;
                if (!opens) continue;
                DocumentLine line = Document.GetLineByNumber(number);
                LineInfo info = Info(line);
                if (!info.Mermaid) continue;
                // A block the layout pass or an earlier pass already holds is that pass's; nothing here
                // scans or requests it a second time.
                if (mermaid != null && mermaid.ContainsKey(line)) continue;
                // The first-sight path of a laid-out opener: one scan, one request through the idle timer
                // (or a cached picture, which folds the block here for the scroll that will reach it).
                PrepareMermaid(line, info);
                prepared++;
                if (prepared >= MermaidRender.PreRenderBlocks) break;
            }
            MermaidRender.DiagPrePasses++;
            MermaidRender.DiagPreLines += walked;
            MermaidRender.DiagPreBlocks += prepared;
        }
        // The picture the opener line draws, or null when it is a plain fence line. The fold is the
        // one decision: no fold, no picture, so source lines are never hidden under nothing.
        internal MermaidPicture MermaidFoldedPicture(DocumentLine line) {
            if (mermaid == null) return null;
            MermaidBlock block;
            if (!mermaid.TryGetValue(line, out block)) return null;
            if (block.Dirty || block.Shown == null || !MermaidFoldInstalled(block)) return null;
            return block.Shown;
        }
        // Before an edit (after PrepareMathSections released the folds it touches): a block the edit
        // reaches turns dirty, by line numbers only. No scan is made here; the only text read is the
        // first characters of the edited lines, to see whether the edit removes or rewrites a fence. A
        // fence edit can change the pairing of every fence below it, so every block at or below such an
        // edit turns dirty and unfolds too (the idle timer sorts them out), also the ones off screen.
        void MermaidChanging(DocumentLine firstLine, DocumentLine lastLine) {
            int from = firstLine.LineNumber, to = lastLine.LineNumber;
            mermaidFenceEdit = to - from > MermaidFenceLines;
            if (!mermaidFenceEdit) {
                for (DocumentLine line = firstLine; line != null; line = line.NextLine) {
                    if (MermaidFenceStart(line)) { mermaidFenceEdit = true; break; }
                    if (Object.ReferenceEquals(line, lastLine)) break;
                }
            }
            foreach (MermaidBlock block in mermaid.Values) {
                if (!MermaidAlive(block.Opener)) { MermaidUnfold(block); mermaidEdited = true; continue; }
                int first = block.Opener.LineNumber;
                int last = MermaidAlive(block.Closer) ? block.Closer.LineNumber : first + MermaidRender.BlockLines;
                if (last < first) last = first + MermaidRender.BlockLines;
                bool wasFolded = block.Folded;
                if (to >= first && from <= last) {
                    block.Dirty = true;
                    mermaidEdited = true;
                    if (MermaidFoldInstalled(block)) MermaidFold(block, false);
                } else if (mermaidFenceEdit && first > to) {
                    block.Dirty = true;
                    block.Wait = true;
                    mermaidEdited = true;
                    if (MermaidFoldInstalled(block)) MermaidFold(block, false);
                }
                if (wasFolded && !MermaidFoldInstalled(block)) {
                    block.Folded = false;
                    if (mermaidReleased == null) mermaidReleased = new List<MermaidBlock>();
                    mermaidReleased.Add(block);
                }
            }
        }
        // After the edit: an opener whose fold was released is rebuilt (a block next to the edit folds
        // again from its cached picture), and the 300 ms idle count starts over. number..lastNumber are
        // the lines the edit left behind: when one of them is a fence now (and none was before), the
        // blocks at or below the edit turn dirty and unfold as in MermaidChanging.
        const int MermaidFenceLines = 16;
        void MermaidChanged(int number, int lastNumber) {
            if (!mermaidFenceEdit) {
                bool fence = lastNumber - number > MermaidFenceLines;
                for (int n = number; n <= lastNumber && !fence; n++) fence = MermaidFenceStart(Document.GetLineByNumber(n));
                if (fence) {
                    foreach (MermaidBlock block in mermaid.Values) {
                        if (!MermaidAlive(block.Opener) || block.Opener.LineNumber < number) continue;
                        mermaidEdited = true;
                        if (!block.Dirty) { block.Dirty = true; block.Wait = true; }
                        if (!MermaidFoldInstalled(block)) continue;
                        MermaidFold(block, false);
                        if (mermaidReleased == null) mermaidReleased = new List<MermaidBlock>();
                        mermaidReleased.Add(block);
                    }
                }
            }
            mermaidFenceEdit = false;
            if (mermaidReleased != null && mermaidReleased.Count > 0) {
                foreach (MermaidBlock block in mermaidReleased)
                    if (MermaidAlive(block.Opener)) View.Redraw(block.Opener.Offset, block.Opener.Length);
                mermaidReleased.Clear();
            }
            if (mermaidEdited) { mermaidEdited = false; MermaidKick(true); }
        }
        void MermaidTick(object sender, EventArgs e) {
            if (mermaidTimer != null) mermaidTimer.Stop();
            MermaidSync();
        }
        // A render finished: only a session that shows this source reacts.
        void MermaidCompleted(string key) {
            if (disposed || mermaid == null) return;
            foreach (MermaidBlock block in mermaid.Values) {
                if (block.Dirty || block.Key == null || !string.Equals(block.Key, key, StringComparison.Ordinal)) continue;
                MermaidSync();
                return;
            }
        }
        // The idle pass (300 ms after the last edit, on a block's first sight, when a render arrives):
        // dead records go, dirty blocks are rescanned, a source without a cached result is requested
        // once, and every block whose drawn state changed is folded / unfolded and redrawn.
        void MermaidSync() {
            if (disposed || mermaid == null || mermaid.Count == 0) return;
            // Not under a held press, a busy host or an open IME composition: the layout must not move there.
            if (frozen || Options.Busy || compositionLive || Editor.ImeBusy || Editor.Document != Document) { MermaidKick(true); return; }
            List<MermaidBlock> dead = null, changed = null;
            List<bool> shows = null;
            foreach (MermaidBlock block in mermaid.Values) {
                if (!MermaidAlive(block.Opener) || !Info(block.Opener).Mermaid) {
                    if (dead == null) dead = new List<MermaidBlock>();
                    dead.Add(block);
                    continue;
                }
                int first, last;
                if (block.Dirty || (block.Closer != null && !MermaidRange(block, out first, out last))) MermaidScan(block);
                if (block.Source != null && block.Closer != null) {
                    MermaidKeyOf(block);
                    if (MermaidRender.State(block.Key) == MermaidRender.Unknown)
                        MermaidRender.Request(block.Key, block.Source, Options.Family, Options.FontSize, PixelsPerDip(), View.Dispatcher);
                }
                bool was = MermaidFoldInstalled(block);
                MermaidPicture before = block.Shown;
                bool show = MermaidDecide(block);
                if (show == was && (!show || Object.ReferenceEquals(before, block.Shown))) continue;
                if (changed == null) { changed = new List<MermaidBlock>(); shows = new List<bool>(); }
                changed.Add(block);
                shows.Add(show);
            }
            bool all = false;
            if (dead != null) foreach (MermaidBlock block in dead) {
                if (MermaidFoldInstalled(block)) all = true;
                MermaidDrop(block);
            }
            if (changed != null) MermaidApply(changed, shows);
            if (all) View.Redraw();
        }
        // Folds / unfolds the changed blocks and redraws their lines. A block above the anchor line
        // changes height there, so the scroll offset follows and the text in view stays where it was.
        void MermaidApply(List<MermaidBlock> changed, List<bool> shows) {
            DocumentLine anchor = AnchorLine(caretLine);
            int anchorNumber = anchor == null || anchor.IsDeleted ? 0 : anchor.LineNumber;
            bool above = false;
            foreach (MermaidBlock block in changed) if (anchorNumber > 0 && block.Opener.LineNumber < anchorNumber) above = true;
            double before = above ? View.GetVisualTopByDocumentLine(anchorNumber) - View.VerticalOffset : 0;
            for (int i = 0; i < changed.Count; i++) {
                MermaidBlock block = changed[i];
                MermaidFold(block, shows[i]);
                int start = block.Opener.Offset;
                int end = MermaidAlive(block.Closer) ? block.Closer.EndOffset : block.Opener.EndOffset;
                View.Redraw(start, Math.Max(0, end - start));
            }
            // A caret move's anchor settle is the last word on the scroll (MermaidAnchorSettle).
            if (!above || mermaidAnchorWork != null) return;
            View.EnsureVisualLines();
            double after = View.GetVisualTopByDocumentLine(anchorNumber) - View.VerticalOffset;
            if (Math.Abs(after - before) <= 0.5) return;
            scrolling = true;
            try { Editor.ScrollToVerticalOffset(View.VerticalOffset + (after - before)); }
            finally { scrolling = false; }
        }
        // A flipped block's opener that is not on screen has no row built under the new fold state, so the
        // height tree still holds its old height (one text line under a picture that was just folded, the
        // picture under a source that was just opened) and every line below it reads a wrong screen top: the
        // anchor settle would then scroll to a position that is off by that difference (11-15, a press on a
        // line below a block whose source was above the viewport sent the view to the document top). Built
        // right after the flip, before any layout, the row also updates the extent the next measure reads.
        void MermaidRowNow(DocumentLine opener) {
            // No VisualLinesValid test: GetOrConstructVisualLine builds from the line cache and the last measure
            // size and does not need a valid layout (the caret code calls it the same way), so whether the
            // fold just invalidated the layout cannot decide whether the row is built.
            if (MermaidRender.CaretAnchorOff || disposed || !MermaidAlive(opener)) return;
            if (View.GetVisualLine(opener.LineNumber) != null) return;
            View.GetOrConstructVisualLine(opener);
        }
        // A caret / selection move (ApplyActiveChange, after the new active lines are stored): a block the
        // caret entered unfolds, one it left folds again. A dirty block the caret has left is rescanned
        // here once, so the previous picture comes back at once instead of after the idle timer. The
        // flipped ranges are handed to the caller's redraw pass through MermaidFlipped. A move that flips
        // a fold takes its anchor from the layout still on screen before the first flip, so the line under
        // the caret keeps its screen height (MermaidAnchorSchedule).
        void MermaidCaretMoved() {
            if (mermaidFlips != null) mermaidFlips.Clear();
            if (mermaid == null || mermaid.Count == 0) return;
            int anchorLine = 0;
            double anchorY = 0;
            foreach (MermaidBlock block in mermaid.Values) {
                // An unclosed fence that was already scanned has nothing to flip.
                if (block.Closer == null && !block.Dirty) continue;
                int first, last;
                if (!MermaidRange(block, out first, out last)) {
                    if (MermaidFoldInstalled(block)) {
                        if (anchorLine == 0) MermaidAnchorTake(out anchorLine, out anchorY);
                        MermaidFold(block, false);
                        MermaidRowNow(block.Opener);
                    }
                    if (!block.Dirty) { block.Dirty = true; MermaidKick(true); }
                    continue;
                }
                if (block.Dirty) {
                    if (block.Wait || MermaidActive(first, last) || !Info(block.Opener).Mermaid) continue;
                    MermaidScan(block);
                    if (!MermaidRange(block, out first, out last)) continue;
                    // The new source is asked for now, not after the idle timer. A request refused on the
                    // spot is a failure MermaidDecide reads below; one that fails later comes back
                    // through MermaidCompleted and the block drops to its code view then.
                    if (block.Source != null) {
                        MermaidKeyOf(block);
                        if (MermaidRender.State(block.Key) == MermaidRender.Unknown)
                            MermaidRender.Request(block.Key, block.Source, Options.Family, Options.FontSize, PixelsPerDip(), View.Dispatcher);
                    }
                    MermaidKick(false);
                }
                bool was = MermaidFoldInstalled(block);
                bool show = MermaidDecide(block);
                if (show == was) continue;
                if (anchorLine == 0) MermaidAnchorTake(out anchorLine, out anchorY);
                MermaidFold(block, show);
                MermaidRowNow(block.Opener);
                if (mermaidFlips == null) mermaidFlips = new List<int>();
                mermaidFlips.Add(first);
                mermaidFlips.Add(last);
            }
            if (anchorLine > 0 && !MermaidRender.CaretAnchorOff) MermaidAnchorSchedule(anchorLine, anchorY);
        }
        bool MermaidFlipped(int number) {
            if (mermaidFlips == null) return false;
            for (int i = 0; i + 1 < mermaidFlips.Count; i += 2)
                if (number >= mermaidFlips[i] && number <= mermaidFlips[i + 1]) return true;
            return false;
        }
        // Where a caret on a hidden fence row goes (SnapHiddenCaret, basic view). Two stops differ from
        // a code block's: the document-end stop on the closer of a block that closes the document stays
        // (Ctrl+End keeps the picture), and a caret arriving from below on a drawn block's opener goes to
        // the block's last source line, the line a code block's closing fence row would send it to.
        int MermaidCaretTarget(DocumentLine line, bool forward, int caretOffset, int target) {
            foreach (MermaidBlock block in mermaid.Values) {
                if (block.Dirty || block.Shown == null) continue;
                bool opener = Object.ReferenceEquals(block.Opener, line), closer = Object.ReferenceEquals(block.Closer, line);
                if (!opener && !closer) continue;
                int first, last;
                if (!MermaidRange(block, out first, out last)) return target;
                if (closer && last == Document.LineCount && caretOffset == Document.TextLength) return caretOffset;
                if (opener && !forward && last - first >= 2 && MermaidFoldInstalled(block)) return block.Closer.PreviousLine.EndOffset;
                return target;
            }
            return target;
        }
        // The press rule of a display formula: on the drawn picture the caret goes into the source (its
        // first line); beside the picture of a block that closes the document, or in the empty area
        // below it, the press is the document's end, the stop that keeps the picture. Beside the picture
        // of any other block the press counts as one on the picture, so the whole row answers one way
        // (left to the editor it landed on the first or the last source line, by where the caret was).
        void MermaidMouseDown(object sender, MouseButtonEventArgs e) {
            if (mermaid == null || mermaid.Count == 0) return;
            if (disposed || e.Handled || Options.Busy || e.ChangedButton != MouseButton.Left || e.ClickCount != 1) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            if (Options.ReadOnly || Editor.IsReadOnly) return;
            if (!MermaidPress(e.GetPosition(View))) return;
            e.Handled = true;
        }
        // The press itself (the handler's decision, shared with the abuse probe). The caret is set
        // without BringCaretToView: under the fold the caret sits inside the opener's visual line,
        // whose height is the whole picture, so a reveal here scrolls a tall picture's bottom into
        // view; the unfold that follows shrinks that row to one text line and leaves the source line
        // above the viewport. The caret move's own anchor (MermaidAnchorTake) restores the picture top.
        internal bool MermaidPress(Point point) {
            if (mermaid == null || mermaid.Count == 0) return false;
            int openerNumber;
            int target = MermaidPressTarget(new Point(point.X + View.HorizontalOffset, point.Y + View.VerticalOffset), out openerNumber);
            if (target < 0) return false;
            if (openerNumber > 0 && openerNumber <= Document.LineCount) {
                // A press that opens the block's source: the reveal waits for the caret move's anchor.
                Editor.Focus();
                Editor.TextArea.ClearSelection();
                Editor.TextArea.Caret.Offset = target;
                return true;
            }
            // The document-end stop of a block that closes the document keeps the picture: the offset is
            // unchanged by a caret that stays folded, so the editor's own reveal is what the stop wants.
            Editor.Focus();
            Editor.TextArea.ClearSelection();
            Editor.TextArea.Caret.Offset = target;
            Editor.TextArea.Caret.BringCaretToView();
            return true;
        }
        // Where a press at this point (view coordinates plus the scroll offsets) puts the caret, or -1
        // when the press is not on a drawn block and stays the editor's.
        internal int MermaidPressTarget(Point point) {
            int openerNumber;
            return MermaidPressTarget(point, out openerNumber);
        }
        // The same, with the opener line of the block the press landed on (0 = no block: the press is
        // the document-end stop, or not on a picture at all).
        internal int MermaidPressTarget(Point point, out int openerNumber) {
            openerNumber = 0;
            if (mermaid == null || mermaid.Count == 0) return -1;
            if (Document == null || Document.LineCount < 1 || !View.VisualLinesValid) return -1;
            double height = View.DocumentHeight;
            if (height <= 0) return -1;
            DocumentLine lastLine = Document.GetLineByNumber(Document.LineCount);
            MermaidBlock block = null;
            if (point.Y >= height) {
                foreach (MermaidBlock candidate in mermaid.Values)
                    if (Object.ReferenceEquals(candidate.Closer, lastLine) && MermaidFoldInstalled(candidate)) { block = candidate; break; }
                return block == null ? -1 : Document.TextLength;
            }
            VisualLine row = View.GetVisualLineFromVisualTop(point.Y);
            if (row == null || row.IsDisposed) return -1;
            if (!mermaid.TryGetValue(row.FirstDocumentLine, out block) || !MermaidFoldInstalled(block)) return -1;
            MermaidElement picture = null;
            foreach (VisualLineElement element in row.Elements) {
                picture = element as MermaidElement;
                if (picture != null) break;
            }
            if (picture == null || picture.Picture == null) return -1;
            DocumentLine inner = block.Opener.NextLine;
            if (Object.ReferenceEquals(block.Closer, lastLine)) {
                TextLine text = row.GetTextLine(picture.VisualColumn);
                double slotLeft = row.GetTextLineVisualXPosition(text, picture.VisualColumn);
                double width = picture.Picture.Width * picture.Scale;
                double left = slotLeft + Math.Max(0, (picture.Slot - width) / 2);
                if (point.X < left - 2 || point.X > left + width + 2) return Document.TextLength;
            }
            if (inner == null) return -1;
            openerNumber = block.Opener.LineNumber;
            return inner.Offset;
        }
        // The anchor of a caret move that flips folds: the row the caret's new line is drawn on in the
        // layout still on screen, read as that row's first document line and its screen y. A line hidden
        // inside a fold has no row of its own; the fold's row carries it, and its top is the picture's top
        // (the press rule of m12), so the settle keeps the picture where it was and the source appears
        // from there. A line that was not on screen before the move (Ctrl+End, Find, Go to line) has no
        // old screen height to keep: it is left to the editor's own reveal.
        void MermaidAnchorTake(out int line, out double y) {
            line = 0;
            y = 0;
            if (Document == null || caretLine < 1 || caretLine > Document.LineCount) return;
            int number = caretLine;
            foreach (VisualLine row in visible) {
                if (row.IsDisposed || row.FirstDocumentLine.IsDeleted) continue;
                if (number < row.FirstDocumentLine.LineNumber || number > row.LastDocumentLine.LineNumber) continue;
                line = row.FirstDocumentLine.LineNumber;
                y = row.VisualTop - View.VerticalOffset;
                return;
            }
        }
        // The one pending scroll that puts the anchor line back at its screen y after the fold has been
        // laid out. A caret that moves on to another line before it runs owns its own reveal, so the
        // settle is dropped.
        void MermaidAnchorSchedule(int line, double y) {
            MermaidAnchorCancel();
            // A picture pressed with its top above the viewport has a negative y: the source it opens is
            // shorter than the picture, so keeping that y leaves the whole source above the view. The
            // anchor stays inside the viewport: the opener's top at the viewport top at the most.
            double viewport = Editor.ViewportHeight;
            if (viewport <= 0) viewport = View.ActualHeight;
            double limit = viewport - View.DefaultLineHeight;
            if (limit < 0) limit = 0;
            if (y > limit) y = limit;
            if (y < 0) y = 0;
            mermaidAnchorLine = line;
            mermaidAnchorY = y;
            mermaidAnchorCaretLine = Editor.TextArea.Caret.Line;
            mermaidAnchorWork = View.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(MermaidAnchorSettle));
        }
        void MermaidAnchorCancel() {
            if (mermaidAnchorWork != null) { mermaidAnchorWork.Abort(); mermaidAnchorWork = null; }
            mermaidAnchorLine = 0;
            mermaidAnchorY = double.NaN;
            mermaidAnchorCaretLine = 0;
        }
        // After the flips have been laid out: the anchor line is put back at the screen y it had before
        // the fold changed the heights around it (clamped to the document), so the line under the caret
        // does not jump; only a caret line that is still outside the viewport is revealed after that.
        void MermaidAnchorSettle() {
            mermaidAnchorWork = null;
            int line = mermaidAnchorLine;
            double anchorY = mermaidAnchorY;
            int caretLine = mermaidAnchorCaretLine;
            mermaidAnchorLine = 0;
            mermaidAnchorY = double.NaN;
            mermaidAnchorCaretLine = 0;
            if (disposed || Document == null || !Object.ReferenceEquals(Editor.Document, Document)) return;
            if (line <= 0 || double.IsNaN(anchorY)) return;
            // A caret that moved on after the flip (a key, another click) owns its own reveal.
            if (caretLine > 0 && Editor.TextArea.Caret.Line != caretLine) return;
            if (line > Document.LineCount) line = Document.LineCount;
            View.EnsureVisualLines();
            double target = View.GetVisualTopByDocumentLine(line) - anchorY;
            double maximum = Editor.ExtentHeight - Editor.ViewportHeight;
            if (maximum < 0) maximum = 0;
            if (target < 0) target = 0;
            if (target > maximum) target = maximum;
            scrolling = true;
            try { Editor.ScrollToVerticalOffset(target); }
            finally { scrolling = false; }
            // The anchor line itself is in view by construction; the caret row (a click below a picture
            // that was opened) can still fall out at the bottom edge, and then the editor's own reveal
            // finishes the job.
            int caretNumber = Editor.TextArea.Caret.Line;
            if (caretNumber < 1 || caretNumber > Document.LineCount || View.ActualHeight <= 0) return;
            double caretTop = View.GetVisualTopByDocumentLine(caretNumber);
            if (caretTop < target - 0.5 || caretTop > target + View.ActualHeight - 4) Editor.TextArea.Caret.BringCaretToView();
        }
    }
}
}
