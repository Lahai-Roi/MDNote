using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// A long logical line keeps only a bounded real-text window. WordWrap on: the text
// outside [Start,End] becomes a tall spacer (one row per estimated wrapped row), so
// the height tree stays small and rows of 4K real characters stay bounded. WordWrap
// off: the outside text becomes an inline object whose WIDTH is the estimated pixel
// width of the folded characters, so the caret X and the horizontal scroll extent
// stay approximately right. The document is never projected or replaced: selection,
// copy, search and undo read the real TextDocument, and folded ranges are elements,
// not text. A window covers the caret and the horizontally visible range; it moves
// with hysteresis and re-anchors the scroll offset so the text does not jump.
// An identical CaretOffset assignment raises no PositionChanged event in AvalonEdit,
// so a caret MOVE into a fold is what the caret path reveals. A search press that does
// not move the caret (the same match pressed again after the user scrolled away) and a
// match the panel's ScrollToLine dragged out of view are handled by RevealRange, which
// Search calls after SelectMatch; the highlight draws only real text (RealSpans).
internal static class LongLineWindow {
    internal const int Threshold = 16384;
    internal const int WindowChars = 4096;
    internal const int Margin = 512;
    // Largest width one inline fold object may ask for. WPF's text engine counts a line in
    // twips and fails once the accumulated line width passes int.MaxValue/600 = 3579139.4 px:
    // TextEmbeddedObject.Format's Width is then rejected with "out of range" and the whole
    // layout throws (measured on a 3M-character line, Malgun Gothic 16). Both folds sit on
    // one row, so the budget is shared: two folds plus the real text must stay under it.
    // An uncapped 4M-character line asks for ~50M px on the prefix alone.
    // Folded characters past the cap still take the object's one column; they just stop
    // adding scroll width, and the caret path still centres the real text it scrolls to.
    internal const double MaxFoldWidth = 1000000;

    internal static IDisposable Attach(SourceEditor editor) {
        if (editor == null) throw new ArgumentNullException("editor");
        return new Session(editor);
    }

    // The search highlight needs to know which characters are laid out as real text, and a
    // search press needs the window that belongs to its editor. Weak per editor: a closed
    // window's editor and document must stay collectable even if a session was never
    // disposed.
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SourceEditor, Session> liveSessions =
        new System.Runtime.CompilerServices.ConditionalWeakTable<SourceEditor, Session>();

    internal static Session Find(SourceEditor editor) {
        if (editor == null) return null;
        Session session;
        return liveSessions.TryGetValue(editor, out session) && !session.IsDisposed ? session : null;
    }

    // True when the character is real text; no session (and every short line) is real.
    internal static bool IsRealOffset(SourceEditor editor, int offset) {
        Session session = Find(editor);
        return session == null || session.InRealWindow(offset);
    }

    // The parts of [start, end) laid out as real text, as from/to pairs in `spans`. A folded
    // character maps to the fold element's own visual column, so a rectangle drawn for it
    // lands at the fold edge (the top of the tall spacer, or the inline fold's face) instead
    // of its place. The search highlight draws only these parts; a fold shows no highlight
    // rather than a wrong one.
    internal static void RealSpans(SourceEditor editor, int start, int end, List<int> spans) {
        spans.Clear();
        if (end <= start) return;
        Session session = Find(editor);
        if (session == null) { spans.Add(start); spans.Add(end); return; }
        session.AppendRealSpans(start, end, spans);
    }

    // Search press hook: put the match inside the real window and on screen. It is the only
    // place that has to run for a hit that never becomes a caret move (the same match pressed
    // again), and Search calls it right after SelectMatch for every press.
    internal static void Reveal(SourceEditor editor, int start, int length) {
        Session session = Find(editor);
        if (session != null) session.RevealRange(start, length);
    }

    internal sealed class LineWindow {
        internal DocumentLine Line;
        internal int Start;
        internal int End;
        internal double CharsPerRow;
        internal double RowHeight;
        internal bool Measured;
        internal double Width;
        internal double FontSize;
        internal Typeface Typeface;
        // WordWrap=false state: estimated average advance for this line's folded text.
        internal bool Horizontal;
        internal double CharWidth;
        internal bool HorizontalMeasured;
    }

    // Two visual columns: an object and a synthetic line break (opposite order
    // for the suffix). The break isolates the tall object from real glyphs.
    // Neither synthetic column adds a character to the TextDocument.
    internal sealed class FoldElement : VisualLineElement {
        internal readonly bool Prefix;
        internal readonly double EstimatedHeight;
        readonly double descent;

        internal FoldElement(int length, bool prefix, double height, double descent)
            : base(2, length) {
            Prefix = prefix;
            EstimatedHeight = height;
            this.descent = descent;
        }

        internal int ObjectColumn { get { return VisualColumn + (Prefix ? 0 : 1); } }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
            if (startVisualColumn == ObjectColumn)
                return new FoldRun(TextRunProperties, EstimatedHeight, descent);
            return new TextEndOfLine(1);
        }
    }

    sealed class FoldRun : TextEmbeddedObject {
        readonly TextRunProperties properties;
        readonly double height;
        readonly double baseline;

        internal FoldRun(TextRunProperties properties, double height, double descent) {
            this.properties = properties;
            this.height = height;
            baseline = Math.Max(0, height - descent);
        }

        public override int Length { get { return 1; } }
        public override TextRunProperties Properties { get { return properties; } }
        public override CharacterBufferReference CharacterBufferReference { get { return new CharacterBufferReference(); } }
        public override LineBreakCondition BreakBefore { get { return LineBreakCondition.BreakDesired; } }
        public override LineBreakCondition BreakAfter { get { return LineBreakCondition.BreakDesired; } }
        public override bool HasFixedSize { get { return true; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            return new TextEmbeddedObjectMetrics(0, height, baseline);
        }
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) { return Rect.Empty; }
        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) { }
    }

    // WordWrap=false: one inline object of the estimated folded width. It takes one
    // visual column but no line break, so the real text after it stays on the same row.
    internal sealed class HFoldElement : VisualLineElement {
        internal readonly bool Prefix;
        internal readonly double EstimatedWidth;
        readonly double height;
        readonly double descent;

        internal HFoldElement(int length, bool prefix, double width, double height, double descent)
            : base(1, length) {
            Prefix = prefix;
            EstimatedWidth = width;
            this.height = height;
            this.descent = descent;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
            return new HFoldRun(TextRunProperties, EstimatedWidth, height, descent);
        }
    }

    sealed class HFoldRun : TextEmbeddedObject {
        readonly TextRunProperties properties;
        readonly double width;
        readonly double height;
        readonly double baseline;

        internal HFoldRun(TextRunProperties properties, double width, double height, double descent) {
            this.properties = properties;
            this.width = width;
            this.height = height;
            baseline = Math.Max(0, height - descent);
        }

        public override int Length { get { return 1; } }
        public override TextRunProperties Properties { get { return properties; } }
        public override CharacterBufferReference CharacterBufferReference { get { return new CharacterBufferReference(); } }
        // InlineObjectRun uses BreakDesired; NoWrap never breaks the line here anyway.
        public override LineBreakCondition BreakBefore { get { return LineBreakCondition.BreakDesired; } }
        public override LineBreakCondition BreakAfter { get { return LineBreakCondition.BreakDesired; } }
        public override bool HasFixedSize { get { return true; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            return new TextEmbeddedObjectMetrics(width, height, baseline);
        }
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) { return new Rect(0, 0, width, height); }
        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) { }
    }

    // WordWrap=true keeps the app's fixed line height out of the TextView because it
    // would clip the tall spacers (TextView.LineHeight is a paragraph height). The
    // strut restores the same minimum row height without a fixed paragraph height, the
    // same way the live preview keeps its spacing while View.LineHeight is NaN.
    internal sealed class StrutElement : VisualLineElement {
        readonly double height;
        readonly bool lineEmpty;

        internal StrutElement(double height, bool lineEmpty) : base(1, 0) {
            this.height = height;
            this.lineEmpty = lineEmpty;
        }

        public override bool IsWhitespace(int visualColumn) { return true; }
        public override bool HandlesLineBorders { get { return true; } }

        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) {
            if (!lineEmpty) return -1;
            if (direction == LogicalDirection.Forward) return visualColumn < VisualColumn ? VisualColumn : -1;
            return visualColumn > VisualColumn ? VisualColumn : -1;
        }

        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
            return new StrutRun(TextRunProperties, height);
        }
    }

    sealed class StrutRun : TextEmbeddedObject {
        readonly TextRunProperties properties;
        readonly double height;
        readonly double baseline;

        internal StrutRun(TextRunProperties properties, double height) {
            this.properties = properties;
            this.height = height;
            baseline = Math.Max(0, height * 0.8);
        }

        public override int Length { get { return 1; } }
        public override TextRunProperties Properties { get { return properties; } }
        public override CharacterBufferReference CharacterBufferReference { get { return new CharacterBufferReference(); } }
        public override LineBreakCondition BreakBefore { get { return LineBreakCondition.BreakPossible; } }
        public override LineBreakCondition BreakAfter { get { return LineBreakCondition.BreakPossible; } }
        public override bool HasFixedSize { get { return true; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            return new TextEmbeddedObjectMetrics(0, height, baseline);
        }
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) { return Rect.Empty; }
        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) { }
    }

    // Must stay after WindowGenerator in the list: at a long line start the prefix
    // fold has to win, or the whole folded prefix would be laid out as real text.
    // An element created in a line transformer would never receive TextRunProperties
    // (that pipeline sets them before transformers run), so this is a generator.
    sealed class StrutGenerator : VisualLineElementGenerator {
        readonly Session session;

        internal StrutGenerator(Session session) { this.session = session; }

        public override int GetFirstInterestedOffset(int startOffset) {
            if (!session.FlatHeight) return -1;
            int offset = CurrentContext.VisualLine.FirstDocumentLine.Offset;
            return startOffset <= offset ? offset : -1;
        }

        public override VisualLineElement ConstructElement(int offset) {
            if (!session.FlatHeight) return null;
            DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
            return new StrutElement(session.RestoreLineHeight, line.Length == 0);
        }
    }

    sealed class WindowGenerator : VisualLineElementGenerator {
        readonly Session session;
        LineWindow current;

        internal WindowGenerator(Session session) { this.session = session; }

        public override void StartGeneration(ITextRunConstructionContext context) {
            base.StartGeneration(context);
            current = session.ForGeneration(context.VisualLine.FirstDocumentLine);
        }

        public override void FinishGeneration() {
            current = null;
            base.FinishGeneration();
        }

        public override int GetFirstInterestedOffset(int startOffset) {
            if (current == null) return -1;
            int offset = current.Line.Offset;
            if (current.Start > 0 && startOffset <= offset) return offset;
            int suffix = offset + current.End;
            return current.End < current.Line.Length && startOffset <= suffix ? suffix : -1;
        }

        public override VisualLineElement ConstructElement(int offset) {
            if (current == null) return null;
            bool prefix = offset == current.Line.Offset && current.Start > 0;
            int length = prefix ? current.Start : current.Line.Length - current.End;
            if (length <= 0) return null;
            if (current.Horizontal) {
                // Uniform estimate; corrected per line once real text is measured.
                // WPF's text layout cannot lay out a paragraph wider than about 35.8M px
                // (it throws once the accumulated width overflows), and 4M characters at
                // ~7 px already exceeds that. Cap the object: past this point the extent
                // stops growing and the last visible columns sit at the cap, which keeps
                // layout alive, the scroll extent finite, and the caret mapping inside the
                // real window (the caret path still centres the real text it scrolls to).
                double width = length * current.CharWidth;
                if (double.IsNaN(width) || double.IsInfinity(width) || width > MaxFoldWidth) width = MaxFoldWidth;
                if (width < 1) width = 1;
                return new HFoldElement(length, prefix, width, session.HorizontalLineHeight, session.HorizontalDescent);
            }
            double rows = Math.Max(1, Math.Ceiling(length / current.CharsPerRow));
            double descent = Math.Max(0, session.View.DefaultLineHeight - session.View.DefaultBaseline);
            return new FoldElement(length, prefix, rows * current.RowHeight, descent);
        }
    }

    internal sealed class Session : IDisposable {
        readonly SourceEditor editor;
        readonly WindowGenerator generator;
        readonly StrutGenerator struts;
        readonly DependencyPropertyDescriptor lineHeightDescriptor;
        readonly Dictionary<DocumentLine, LineWindow> windows = new Dictionary<DocumentLine, LineWindow>();
        readonly Queue<DocumentLine> cacheOrder = new Queue<DocumentLine>();
        TextDocument document;
        DispatcherOperation pending;
        DispatcherOperation revealConfirm;
        LineWindow changingWindow;
        int changingStart;
        int changingEnd;
        // How many horizontal window rebuilds this session ran. A layout that cannot settle
        // rebuilds on every queued pass, so the probe bounds the count instead of timing it.
        internal int HorizontalRebuilds;
        bool disposed;
        bool busy;
        bool lastWrap;
        double lastCaretY = double.NaN;
        // A scroll that lands before the first layout pass after a caret reveal is
        // the search panel's ScrollToLine; it must not win over the reveal.
        bool revealImmediate;
        bool forceReveal;
        // The line height the app would use, and whether this layer replaced it with
        // NaN so the tall wrap-mode spacers are not clipped to one paragraph row.
        double restoreLineHeight;
        bool flatHeight;
        bool flatPending;
        // A live preview that took over this session owns line height and struts;
        // this layer must not add its own struts to preview rows.
        bool previewOwnsHeight;
        bool settingLineHeight;
        bool sampleValid;
        double sampleWidth;
        double sampleFontSize;
        Typeface sampleTypeface;

        internal TextView View { get { return editor.TextArea.TextView; } }
        internal bool IsAttached { get { return !disposed && View.ElementGenerators.Contains(generator); } }
        internal bool IsDisposed { get { return disposed; } }
        internal SourceEditor Editor { get { return editor; } }
        internal bool FlatHeight { get { return flatHeight && !disposed; } }
        internal double RestoreLineHeight { get { return restoreLineHeight; } }
        internal double HorizontalLineHeight {
            get {
                double height = View.LineHeight;
                return double.IsNaN(height) || height <= 0 ? View.DefaultLineHeight : height;
            }
        }
        internal double HorizontalDescent { get { return Math.Max(0, View.DefaultLineHeight - View.DefaultBaseline); } }

        internal Session(SourceEditor editor) {
            this.editor = editor;
            lastWrap = editor.WordWrap;
            restoreLineHeight = View.LineHeight;
            generator = new WindowGenerator(this);
            struts = new StrutGenerator(this);
            View.ElementGenerators.Insert(0, generator);
            View.ElementGenerators.Insert(1, struts);
            lineHeightDescriptor = DependencyPropertyDescriptor.FromProperty(TextView.LineHeightProperty, typeof(TextView));
            if (lineHeightDescriptor != null) lineHeightDescriptor.AddValueChanged(View, LineHeightChanged);
            editor.DocumentChanged += DocumentReplaced;
            editor.TextArea.Caret.PositionChanged += CaretChanged;
            editor.TextArea.TextEntering += TextEntering;
            editor.PreviewMouseDown += MouseDown;
            editor.PreviewMouseMove += MouseMove;
            CommandManager.AddPreviewExecutedHandler(editor, PreviewExecuted);
            View.VisualLinesChanged += LayoutChanged;
            View.ScrollOffsetChanged += ScrollChanged;
            liveSessions.Add(editor, this);
            TrackDocument();
            QueueLayout();
        }

        // The live preview owns TextView.LineHeight and supplies its own struts, so a
        // session that outlives a TXT -> Markdown switch stops managing both.
        internal void PreviewActivated() {
            flatHeight = false;
            flatPending = false;
            previewOwnsHeight = true;
            windows.Clear();
            cacheOrder.Clear();
        }

        // A live preview inserted its generators at 0/1 after this session attached.
        // The prefix fold must be constructed before a zero-length strut at the same
        // offset, or the whole folded prefix would be laid out as real text.
        internal void BringToFront() {
            if (disposed) return;
            View.ElementGenerators.Remove(generator);
            View.ElementGenerators.Remove(struts);
            View.ElementGenerators.Insert(0, generator);
            View.ElementGenerators.Insert(1, struts);
        }

        void LineHeightChanged(object sender, EventArgs e) {
            if (disposed || settingLineHeight) return;
            double value = View.LineHeight;
            if (double.IsNaN(value)) return;
            restoreLineHeight = value;
            // Row heights the window measured under the old height are stale now.
            windows.Clear();
            cacheOrder.Clear();
            // Flat mode owns the value: re-assert NaN so spacers stay unclipped and
            // the struts rebuild with the new height.
            if (flatHeight) SetLineHeight(double.NaN);
        }

        void SetLineHeight(double value) {
            bool previous = settingLineHeight;
            settingLineHeight = true;
            try { View.LineHeight = value; } finally { settingLineHeight = previous; }
        }

        internal int WindowStart(int lineNumber) {
            if (document == null || lineNumber < 1 || lineNumber > document.LineCount) return -1;
            LineWindow state;
            return windows.TryGetValue(document.GetLineByNumber(lineNumber), out state) ? state.Start : -1;
        }

        // Exclusive end of the real window, for the probes that check a widened horizontal
        // range survives an edit and that both visible long lines keep real text.
        internal int WindowEnd(int lineNumber) {
            if (document == null || lineNumber < 1 || lineNumber > document.LineCount) return -1;
            LineWindow state;
            return windows.TryGetValue(document.GetLineByNumber(lineNumber), out state) ? state.End : -1;
        }

        void TrackDocument() {
            if (document != null) {
                document.Changing -= DocumentChanging;
                document.Changed -= DocumentChanged;
            }
            document = editor.Document;
            windows.Clear();
            cacheOrder.Clear();
            changingWindow = null;
            lastCaretY = double.NaN;
            if (document != null) {
                document.Changing += DocumentChanging;
                document.Changed += DocumentChanged;
            }
        }

        // TextArea.OnDocumentChanged resets the caret before TextEditor raises DocumentChanged,
        // and Workspace.ReleaseTabSurfaces replaces the document while that caret can sit on a
        // long line. Until DocumentReplaced runs, `document` and every cached line belong to the
        // replaced document: handing one to the new TextView throws "Line belongs to wrong
        // document". One reference comparison, no document scan.
        bool DocumentCurrent() {
            return !disposed && document != null && Object.ReferenceEquals(document, editor.Document);
        }

        // A cached window may only reach the TextView while its line is alive in the live
        // document. IsDeleted is O(1) and covers a line joined away inside the same document.
        bool CanRebuild(LineWindow state) {
            return DocumentCurrent() && state != null && state.Line != null && !state.Line.IsDeleted;
        }

        void DocumentReplaced(object sender, EventArgs e) { TrackDocument(); QueueLayout(); }

        void DocumentChanging(object sender, DocumentChangeEventArgs e) {
            changingWindow = null;
            DocumentLine line = document.GetLineByOffset(e.Offset);
            LineWindow state;
            if (windows.TryGetValue(line, out state)) {
                changingWindow = state;
                changingStart = e.GetNewOffset(line.Offset + state.Start, AnchorMovementType.BeforeInsertion);
                changingEnd = e.GetNewOffset(line.Offset + state.End, AnchorMovementType.BeforeInsertion);
            }
        }

        void DocumentChanged(object sender, DocumentChangeEventArgs e) {
            if (changingWindow != null && !changingWindow.Line.IsDeleted) {
                if (changingWindow.Horizontal) {
                    // The horizontal window is as wide as the caret plus the visible range
                    // (RevealHorizontalRange/RebuildHorizontalRange widen it past WindowChars).
                    // The wrap-mode setter would snap it back to WindowChars and drop the
                    // highlight and the visible text on every edit, so both edges move by the
                    // change's delta here.
                    SetBounds(changingWindow, changingStart - changingWindow.Line.Offset,
                        changingEnd - changingWindow.Line.Offset);
                } else {
                    SetBounds(changingWindow, changingStart - changingWindow.Line.Offset);
                }
            }
            changingWindow = null;
            // AvalonEdit already invalidated the changed line. Do not force an
            // additional layout per input or enumerate the document/cache here.
        }

        internal LineWindow ForGeneration(DocumentLine line) {
            if (!DocumentCurrent() || line == null || line.IsDeleted || line.Length <= Threshold) return null;
            return CurrentWindow(line);
        }

        // A wrap toggle invalidates every cached window: the two modes use different
        // state fields, so a state from the other mode must never be reused.
        LineWindow CurrentWindow(DocumentLine line) {
            if (lastWrap != editor.WordWrap) {
                lastWrap = editor.WordWrap;
                windows.Clear();
                cacheOrder.Clear();
            }
            return editor.WordWrap ? GetWindow(line) : GetHorizontalWindow(line);
        }

        LineWindow GetWindow(DocumentLine line) {
            LineWindow state;
            if (windows.TryGetValue(line, out state)) {
                SetBounds(state, state.Start);
                return state;
            }
            // Bounded cache, keyed by line identity, not line number. Edits before
            // a line therefore require no suffix renumbering or offset sweep.
            while (windows.Count >= 64 && cacheOrder.Count > 0) windows.Remove(cacheOrder.Dequeue());
            state = new LineWindow();
            state.Line = line;
            double width = View.ActualWidth > 1 ? View.ActualWidth : Math.Max(100, editor.ActualWidth);
            state.CharsPerRow = Math.Max(8, width / Math.Max(1, View.WideSpaceWidth));
            state.RowHeight = View.DefaultLineHeight;
            int target = editor.TextArea.Caret.Line == line.LineNumber ? editor.TextArea.Caret.Column - 1 : 0;
            SetBounds(state, target - WindowChars / 2);
            windows.Add(line, state);
            cacheOrder.Enqueue(line);
            // A real wrapped line is being laid out: the tall spacer needs NaN now.
            // A queued layout applies it (never during measure).
            if (!flatHeight && !flatPending && !previewOwnsHeight && !double.IsNaN(restoreLineHeight)) flatPending = true;
            return state;
        }

        LineWindow GetHorizontalWindow(DocumentLine line) {
            LineWindow state;
            if (windows.TryGetValue(line, out state)) {
                EnsureHorizontalMetrics(state);
                // Keep both bounds: RebuildHorizontalRange can cover more than
                // WindowChars (caret plus visible range), and the wrap-mode setter
                // would snap End back to Start + WindowChars on every generation.
                SetBounds(state, state.Start, state.End);
                return state;
            }
            while (windows.Count >= 64 && cacheOrder.Count > 0) windows.Remove(cacheOrder.Dequeue());
            state = new LineWindow();
            state.Line = line;
            state.Horizontal = true;
            EnsureHorizontalMetrics(state);
            int target = editor.TextArea.Caret.Line == line.LineNumber ? editor.TextArea.Caret.Column - 1 : 0;
            SetBounds(state, target - WindowChars / 2);
            windows.Add(line, state);
            cacheOrder.Enqueue(line);
            return state;
        }

        Typeface Face() {
            return new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
        }

        void EnsureHorizontalMetrics(LineWindow state) {
            Typeface face = Face();
            if (state.HorizontalMeasured && state.FontSize == editor.FontSize && face.Equals(state.Typeface)) return;
            state.CharWidth = SampleCharWidth(face);
            state.HorizontalMeasured = true;
            state.FontSize = editor.FontSize;
            state.Typeface = face;
            state.RowHeight = View.DefaultLineHeight;
            state.Width = View.ActualWidth;
        }

        // Average advance once per font/size. The per-line measurement corrects it,
        // but the first frame needs a close estimate or the scroll extent is off by
        // the folded character count times the error.
        double SampleCharWidth(Typeface face) {
            if (sampleValid && sampleFontSize == editor.FontSize && face.Equals(sampleTypeface)) return sampleWidth;
            const string sample = "가나다라마바사아자차카타파하 ABCDEFGHIJKLMNOPQRSTUVWXYZ abcdefghijklmnopqrstuvwxyz 0123456789 .,-";
            double pixels = 1.0;
            try { pixels = VisualTreeHelper.GetDpi(View).PixelsPerDip; } catch (Exception) { }
            if (double.IsNaN(pixels) || pixels <= 0) pixels = 1.0;
            double width = double.NaN;
            try {
                var text = new FormattedText(sample, System.Globalization.CultureInfo.CurrentCulture, View.FlowDirection,
                    face, editor.FontSize, Brushes.Black, null, TextOptions.GetTextFormattingMode(View), pixels);
                width = text.WidthIncludingTrailingWhitespace / sample.Length;
            } catch (Exception) { }
            if (!Finite(width) || width <= 0) {
                double wide = View.WideSpaceWidth;
                width = wide > 1 ? wide : Math.Max(1, editor.FontSize * 0.6);
            }
            sampleWidth = width;
            sampleFontSize = editor.FontSize;
            sampleTypeface = face;
            sampleValid = true;
            return width;
        }

        void SetBounds(LineWindow state, int start) {
            int length = state.Line.Length;
            state.Start = Math.Max(0, Math.Min(Math.Max(0, length - WindowChars), start));
            if (state.Start > 0 && state.Start < length && char.IsLowSurrogate(document.GetCharAt(state.Line.Offset + state.Start))
                && char.IsHighSurrogate(document.GetCharAt(state.Line.Offset + state.Start - 1))) state.Start--;
            state.End = Math.Min(length, state.Start + WindowChars);
            if (state.End > 0 && state.End < length && char.IsHighSurrogate(document.GetCharAt(state.Line.Offset + state.End - 1))
                && char.IsLowSurrogate(document.GetCharAt(state.Line.Offset + state.End))) state.End++;
        }

        // Horizontal mode covers the caret and the visible range, so the size is not
        // fixed; only surrogate pairs are kept intact at both edges.
        void SetBounds(LineWindow state, int start, int end) {
            int length = state.Line.Length;
            if (start < 0) start = 0;
            if (start > length) start = length;
            if (end < start) end = start;
            if (end > length) end = length;
            if (start > 0 && start < length && char.IsLowSurrogate(document.GetCharAt(state.Line.Offset + start))
                && char.IsHighSurrogate(document.GetCharAt(state.Line.Offset + start - 1))) start--;
            if (end > 0 && end < length && char.IsHighSurrogate(document.GetCharAt(state.Line.Offset + end - 1))
                && char.IsLowSurrogate(document.GetCharAt(state.Line.Offset + end))) end = Math.Min(length, end + 1);
            if (end < start) end = start;
            state.Start = start;
            state.End = end;
        }

        static bool RealOffset(LineWindow state, int relative) {
            return relative >= state.Start && relative <= state.End;
        }

        void CaretChanged(object sender, EventArgs e) {
            if (busy || !DocumentCurrent() || editor.TextArea.Caret.Line > document.LineCount) return;
            DocumentLine line = document.GetLineByNumber(editor.TextArea.Caret.Line);
            if (line.Length > Threshold) {
                int relative = editor.TextArea.Caret.Column - 1;
                if (!editor.WordWrap) {
                    HorizontalCaretChanged(line, relative);
                    return;
                }
                LineWindow state = CurrentWindow(line);
                bool edge = !RealOffset(state, relative)
                    || (state.Start > 0 && relative - state.Start < Margin)
                    || (state.End < line.Length && state.End - relative < Margin);
                if (edge) {
                    VisualLine visual = View.GetVisualLine(line.LineNumber);
                    double screenY = Finite(lastCaretY) ? lastCaretY : ViewportHeight() / 2;
                    if (visual != null && !visual.IsDisposed && RealOffset(state, relative))
                        screenY = visual.GetVisualPosition(visual.GetVisualColumn(relative), VisualYPosition.LineMiddle).Y - View.VerticalOffset;
                    if (!Finite(screenY) || screenY < 0 || screenY > ViewportHeight()) screenY = ViewportHeight() / 2;
                    screenY = ClampScreenY(state, screenY);
                    Rebuild(state, StartForAnchor(state, relative, screenY), relative, screenY);
                    revealImmediate = true;
                    QueueRevealConfirm();
                }
            }
            RememberCaret();
        }

        // WordWrap=false: the caret and the visible range share one inline window.
        // Keep the caret on screen and rebuild around it when it approaches an edge.
        void HorizontalCaretChanged(DocumentLine line, int relative) {
            LineWindow state = CurrentWindow(line);
            bool edge = !RealOffset(state, relative)
                || (state.Start > 0 && relative - state.Start < Margin)
                || (state.End < line.Length && state.End - relative < Margin);
            if (edge) {
                double screenX = HorizontalCaretX(state, relative);
                if (!Finite(screenX) || screenX < HorizontalInset() || screenX > ViewportWidth() - HorizontalInset())
                    screenX = ViewportWidth() / 2;
                RebuildHorizontal(state, relative - WindowChars / 2, relative, screenX);
                revealImmediate = true;
            }
            RememberCaret();
        }

        // A search press is not always a caret move this layer can see. The panel selects
        // the match and then scrolls to the logical line start, i.e. the top of the tall
        // prefix fold, which can drag the viewport away from the revealed caret; and a
        // match that is already the caret (the same hit pressed again after scrolling away)
        // raises no event at all. Search calls this after that scroll: cover the whole
        // match with real text, put it where the caret was on screen, and let the reveal
        // confirmation keep the scroll that follows from undoing it. Only a search press
        // reaches here, never a keystroke or a caret callback.
        internal void RevealRange(int start, int length) {
            if (busy || !DocumentCurrent() || length <= 0) return;
            if (start < 0 || start >= document.TextLength) return;
            DocumentLine line = document.GetLineByOffset(start);
            if (line.Length <= Threshold) return;
            int beginning = start - line.Offset;
            int finish = Math.Min(line.Length, start + length - line.Offset);
            if (finish <= beginning) return;
            if (!editor.WordWrap) { RevealHorizontalRange(line, beginning, finish); return; }
            LineWindow state = CurrentWindow(line);
            VisualLine visual = View.GetVisualLine(line.LineNumber);
            if (visual != null && !visual.IsDisposed && Covered(state, beginning, finish)) {
                int column = visual.GetVisualColumn(beginning);
                double shown = visual.GetVisualPosition(column, VisualYPosition.LineMiddle).Y - View.VerticalOffset;
                if (shown >= 0 && shown <= ViewportHeight()) return;
            }
            double screenY = ClampScreenY(state, Finite(lastCaretY) ? lastCaretY : ViewportHeight() / 2);
            // StartForAnchor centres the visible part; widen it towards the match end when the
            // two would not share one fixed-size window (a long match, or one near a window edge).
            int target = StartForAnchor(state, beginning, screenY);
            if (finish + Margin - target > WindowChars) target = Math.Max(beginning - Margin, finish + Margin - WindowChars);
            Rebuild(state, target, beginning, screenY);
            revealImmediate = true;
            QueueRevealConfirm();
        }

        // The viewport is a horizontal range here, so the match has to be inside the real
        // window AND inside the visible columns; the search panel's ScrollToLine resets the
        // horizontal offset to 0 for a line start, which is what this restores.
        void RevealHorizontalRange(DocumentLine line, int beginning, int finish) {
            LineWindow state = CurrentWindow(line);
            double first = HorizontalCaretX(state, beginning), last = HorizontalCaretX(state, finish);
            if (Finite(first) && Finite(last) && Covered(state, beginning, finish)
                && first >= HorizontalInset() && last <= ViewportWidth() - HorizontalInset()) return;
            double target = Finite(first) ? first : ViewportWidth() / 2;
            if (target < HorizontalInset() || target > ViewportWidth() - HorizontalInset()) target = ViewportWidth() / 2;
            int from = beginning - Margin, to = finish + Margin;
            if (to - from > WindowChars * 8) { from = beginning - WindowChars / 2; to = finish + WindowChars / 2; }
            RebuildHorizontalRange(state, from, to, beginning, target);
            revealImmediate = true;
        }

        // End is exclusive for real text: the suffix fold starts at End.
        static bool Covered(LineWindow state, int beginning, int finish) {
            return beginning >= state.Start && finish <= state.End;
        }

        // True when the character is laid out as real text: a line without a window is drawn
        // natively, and a windowed line shows [Start, End) as real characters. A folded
        // character maps to the fold element's own visual column, so a highlight drawn for it
        // would sit at the fold edge instead of its place; the highlight skips those.
        internal bool InRealWindow(int offset) {
            if (!DocumentCurrent() || offset < 0 || offset >= document.TextLength) return true;
            DocumentLine line = document.GetLineByOffset(offset);
            if (line.Length <= Threshold) return true;
            LineWindow state;
            if (!windows.TryGetValue(line, out state)) return true;
            int relative = offset - line.Offset;
            return relative >= state.Start && relative < state.End;
        }

        // Splits [start, end) into the runs that are real text. Walks only the lines the range
        // covers (one for every search match) and each step is a dictionary hit plus two
        // comparisons, never a document scan.
        internal void AppendRealSpans(int start, int end, List<int> spans) {
            if (!DocumentCurrent()) { AppendSpan(spans, start, end); return; }
            end = Math.Min(end, document.TextLength);
            int at = Math.Max(0, start);
            while (at < end) {
                DocumentLine line = document.GetLineByOffset(at);
                int contentEnd = line.EndOffset;
                int stop = Math.Min(end, contentEnd + line.DelimiterLength);
                int realFrom = at, realTo = Math.Min(end, contentEnd);
                LineWindow state;
                if (line.Length > Threshold && windows.TryGetValue(line, out state)) {
                    realFrom = Math.Max(at, line.Offset + state.Start);
                    realTo = Math.Min(realTo, line.Offset + state.End);
                    // The line's own break is folded when the window stops short of its end.
                    if (stop > contentEnd && state.End >= line.Length) AppendSpan(spans, Math.Max(at, contentEnd), stop);
                } else if (stop > contentEnd) AppendSpan(spans, Math.Max(at, contentEnd), stop);
                AppendSpan(spans, realFrom, realTo);
                if (stop <= at) break; // no progress possible: stop instead of spinning
                at = stop;
            }
        }

        static void AppendSpan(List<int> spans, int from, int to) {
            if (to <= from) return;
            int count = spans.Count;
            if (count >= 2 && spans[count - 1] == from) { spans[count - 1] = to; return; }
            spans.Add(from);
            spans.Add(to);
        }

        // Near a viewport border there may not be enough characters for both the
        // visible rows and a 512-character safety margin. Give up pixel stability
        // there, not the safety margin.
        double ClampScreenY(LineWindow state, double screenY) {
            double inset = Margin * state.RowHeight / state.CharsPerRow + state.RowHeight;
            if (inset * 2 < ViewportHeight()) screenY = Math.Max(inset, Math.Min(ViewportHeight() - inset, screenY));
            return screenY;
        }

        // True while the caret sits inside real text AND on screen. A programmatic
        // scroll (search) can leave it real in the window but outside the viewport.
        bool CaretOnScreen(LineWindow state, VisualLine visual) {
            int relative = editor.TextArea.Caret.Column - 1;
            if (!RealOffset(state, relative)) return false;
            if (visual == null || visual.IsDisposed) return false;
            Point position = visual.GetVisualPosition(visual.GetVisualColumn(relative), VisualYPosition.LineMiddle);
            return position.X >= View.HorizontalOffset && position.X <= View.HorizontalOffset + ViewportWidth()
                && position.Y >= View.VerticalOffset && position.Y <= View.VerticalOffset + ViewportHeight();
        }

        double HorizontalCaretX(LineWindow state, int relative) {
            if (!RealOffset(state, relative)) return double.NaN;
            VisualLine visual = View.GetVisualLine(state.Line.LineNumber);
            if (visual == null || visual.IsDisposed) return double.NaN;
            return visual.GetVisualPosition(visual.GetVisualColumn(relative), VisualYPosition.LineMiddle).X - View.HorizontalOffset;
        }

        double ViewportWidth() { return Math.Max(1, ((IScrollInfo)View).ViewportWidth); }
        static double HorizontalInset() { return 40; }

        void RebuildHorizontal(LineWindow state, int start, int anchor, double screenX) {
            if (!CanRebuild(state)) return;
            bool previousBusy = busy;
            busy = true;
            try {
                HorizontalRebuilds++;
                SetBounds(state, start);
                RebuildHorizontalCore(state, anchor, screenX);
            } finally { busy = previousBusy; }
        }

        void RebuildHorizontalRange(LineWindow state, int start, int end, int anchor, double screenX) {
            if (!CanRebuild(state)) return;
            bool previousBusy = busy;
            busy = true;
            try {
                HorizontalRebuilds++;
                SetBounds(state, start, end);
                RebuildHorizontalCore(state, anchor, screenX);
            } finally { busy = previousBusy; }
        }

        void RebuildHorizontalCore(LineWindow state, int anchor, double screenX) {
            if (!CanRebuild(state)) return;
            View.Redraw(state.Line.Offset, state.Line.Length);
            VisualLine visual = View.GetOrConstructVisualLine(state.Line);
            int column = visual.GetVisualColumn(Math.Max(state.Start, Math.Min(state.End, anchor)));
            double newX = visual.GetVisualPosition(column, VisualYPosition.LineMiddle).X;
            ((IScrollInfo)View).SetHorizontalOffset(Math.Max(0, newX - screenX));
            View.EnsureVisualLines();
        }

        // The text line's character index is the visual column the element sits on.
        static int ColumnAtX(VisualLine visual, double x) {
            if (visual.TextLines.Count == 0) return 0;
            TextLine row = visual.TextLines[0];
            double width = row.WidthIncludingTrailingWhitespace;
            if (x < 0) x = 0;
            if (x > width) x = width;
            CharacterHit hit = row.GetCharacterHitFromDistance(x);
            return Math.Max(0, Math.Min(visual.VisualLength, hit.FirstCharacterIndex));
        }

        internal static HFoldElement FoldAtX(VisualLine visual, double x) {
            return FoldAtColumn(visual, ColumnAtX(visual, x));
        }

        static HFoldElement FoldAtColumn(VisualLine visual, int column) {
            foreach (VisualLineElement element in visual.Elements) {
                HFoldElement fold = element as HFoldElement;
                if (fold != null && column >= fold.VisualColumn && column < fold.VisualColumn + fold.VisualLength) return fold;
            }
            return null;
        }

        // The character under a document X as drawn, relative to the line start.
        // Inside an inline fold the pixel fraction of the fold is used (the width the
        // fold was laid out with); real text maps to its real column. An average
        // advance over the whole line is neither, and it is wrong for mixed
        // Korean/Latin text.
        static int WindowCharAtX(VisualLine visual, double x) {
            int column = ColumnAtX(visual, x);
            HFoldElement fold = FoldAtColumn(visual, column);
            if (fold != null) return OffsetAtX(visual, fold, x);
            return Math.Max(0, visual.GetRelativeOffset(column));
        }

        // Maps a point inside an inline fold to its document offset by the width
        // fraction, so a click can land near the character it points at.
        static int OffsetAtX(VisualLine visual, HFoldElement fold, double x) {
            TextLine row = visual.TextLines[0];
            int startColumn = Math.Min(fold.VisualColumn, row.Length);
            int endColumn = Math.Min(fold.VisualColumn + 1, row.Length);
            double left = row.GetDistanceFromCharacterHit(new CharacterHit(startColumn, 0));
            double right = row.GetDistanceFromCharacterHit(new CharacterHit(endColumn, 0));
            double fraction = (x - left) / Math.Max(1, right - left);
            if (fraction < 0) fraction = 0;
            if (fraction > 1) fraction = 1;
            return fold.RelativeTextOffset + (int)Math.Round(fraction * fold.DocumentLength);
        }

        // A per-line correction from real glyph widths. The estimate only affects the
        // folded width, so a small drift does not need a rebuild; larger drift does,
        // because the whole real text would slide against the scroll extent.
        bool MeasureHorizontalWindow(LineWindow state, VisualLine visual) {
            int chars = state.End - state.Start;
            if (chars <= 0 || visual.TextLines.Count == 0) return false;
            TextLine row = visual.TextLines[0];
            int first = Math.Min(visual.GetVisualColumn(state.Start), row.Length);
            int last = Math.Min(visual.GetVisualColumn(state.End), row.Length);
            double width = row.GetDistanceFromCharacterHit(new CharacterHit(last, 0))
                - row.GetDistanceFromCharacterHit(new CharacterHit(first, 0));
            double measured = width / chars;
            if (!Finite(measured) || measured <= 0) return false;
            double old = state.CharWidth;
            state.CharWidth = measured;
            state.HorizontalMeasured = true;
            return old > 0 && Math.Abs(old - measured) > old * 0.01;
        }

        double ViewportHeight() { return Math.Max(1, ((IScrollInfo)View).ViewportHeight); }
        static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        void RememberCaret() {
            if (!DocumentCurrent() || !View.VisualLinesValid) return;
            VisualLine visual = View.GetVisualLine(editor.TextArea.Caret.Line);
            if (visual == null || visual.IsDisposed) return;
            LineWindow state;
            int relative = editor.TextArea.Caret.Column - 1;
            if (windows.TryGetValue(visual.FirstDocumentLine, out state) && !RealOffset(state, relative)) return;
            lastCaretY = visual.GetVisualPosition(visual.GetVisualColumn(relative), VisualYPosition.LineMiddle).Y - View.VerticalOffset;
        }

        int StartForAnchor(LineWindow state, int relative, double screenY) {
            // Center the *visible* part, not just the caret's character offset.
            // A caret near the top needs more of the window below it.
            double bias = (ViewportHeight() / 2 - screenY) * state.CharsPerRow / state.RowHeight;
            int start = relative - WindowChars / 2 + (int)Math.Round(bias);
            return Math.Max(relative - WindowChars + Margin, Math.Min(relative - Margin, start));
        }

        void Rebuild(LineWindow state, int start, int anchor, double screenY) {
            if (!CanRebuild(state)) return;
            bool previousBusy = busy;
            busy = true;
            try {
                SetBounds(state, start);
                View.Redraw(state.Line.Offset, state.Line.Length);
                VisualLine visual = View.GetOrConstructVisualLine(state.Line);
                int column = visual.GetVisualColumn(Math.Max(state.Start, Math.Min(state.End, anchor)));
                double newY = visual.GetVisualPosition(column, VisualYPosition.LineMiddle).Y;
                ((IScrollInfo)View).SetVerticalOffset(Math.Max(0, newY - screenY));
                View.EnsureVisualLines();
            } finally { busy = previousBusy; }
        }

        // A search select is followed by ScrollToLine, which scrolls to the logical
        // line start - for a folded line that is the middle of the tall prefix spacer,
        // so it can move the freshly revealed caret off screen. Re-check once at Render
        // priority, before the frame and the highlight draw, and put it back.
        void QueueRevealConfirm() {
            if (disposed || revealConfirm != null) return;
            revealConfirm = View.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(delegate {
                revealConfirm = null;
                if (!disposed) ConfirmVerticalReveal();
            }));
        }

        void ConfirmVerticalReveal() {
            if (busy || !DocumentCurrent() || !editor.WordWrap) return;
            DocumentLine line = document.GetLineByNumber(editor.TextArea.Caret.Line);
            if (line.Length <= Threshold) return;
            LineWindow state = CurrentWindow(line);
            int relative = editor.TextArea.Caret.Column - 1;
            VisualLine visual = View.GetVisualLine(line.LineNumber);
            if (CaretOnScreen(state, visual)) return;
            double screenY = ClampScreenY(state, Finite(lastCaretY) ? lastCaretY : ViewportHeight() / 2);
            Rebuild(state, StartForAnchor(state, relative, screenY), relative, screenY);
        }

        void LayoutChanged(object sender, EventArgs e) { QueueLayout(); }

        // The first scroll after a caret reveal (before the next layout pass) is the
        // search panel's ScrollToLine. Remember it so the reveal can be put back.
        void ScrollChanged(object sender, EventArgs e) {
            if (!busy && !disposed && revealImmediate) forceReveal = true;
            QueueLayout();
        }

        void QueueLayout() {
            if (disposed || pending != null) return;
            // VisualLinesChanged runs inside MeasureOverride. Never redraw or
            // construct a visual line from that event (AvalonEdit forbids it).
            pending = View.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate {
                pending = null;
                if (!disposed) ProcessLayout();
            }));
        }

        static FoldElement FoldOnRow(VisualLine visual, TextLine row) {
            int first = visual.GetTextLineVisualStartColumn(row);
            int end = first + row.Length;
            foreach (VisualLineElement element in visual.Elements) {
                FoldElement fold = element as FoldElement;
                if (fold != null && fold.ObjectColumn >= first && fold.ObjectColumn < end) return fold;
            }
            return null;
        }

        static FoldElement FoldAtY(VisualLine visual, double y) {
            if (y < visual.VisualTop || y >= visual.VisualTop + visual.Height) return null;
            return FoldOnRow(visual, visual.GetTextLineByVisualYPosition(y));
        }

        int OffsetAtY(VisualLine visual, double y, out double screenY) {
            TextLine row = visual.GetTextLineByVisualYPosition(y);
            FoldElement fold = FoldOnRow(visual, row);
            if (fold != null) {
                double top = visual.GetTextLineVisualYPosition(row, VisualYPosition.LineTop);
                double fraction = Math.Max(0, Math.Min(1, (y - top) / Math.Max(1, row.Height)));
                screenY = y - View.VerticalOffset;
                return fold.RelativeTextOffset + (int)Math.Round(fraction * fold.DocumentLength);
            }
            TextViewPosition position = visual.GetTextViewPosition(new Point(0, y), false);
            screenY = visual.GetTextLineVisualYPosition(row, VisualYPosition.LineMiddle) - View.VerticalOffset;
            return document.GetOffset(position.Location) - visual.FirstDocumentLine.Offset;
        }

        bool NeedsMetrics(LineWindow state) {
            return !state.Measured || Math.Abs(state.Width - View.ActualWidth) > 0.5
                || state.FontSize != editor.FontSize
                || !new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch).Equals(state.Typeface);
        }

        bool MeasureWindow(LineWindow state, VisualLine visual) {
            int first = visual.GetVisualColumn(state.Start), end = visual.GetVisualColumn(state.End);
            int column = 0, chars = 0, rows = 0;
            double height = 0;
            foreach (TextLine row in visual.TextLines) {
                int next = column + row.Length;
                // Only complete real rows: neither tall objects nor the last,
                // partly filled row may bias characters-per-row downwards.
                if (column >= first && next <= end && row.NewlineLength == 0 && FoldOnRow(visual, row) == null) {
                    int count = visual.GetRelativeOffset(next) - visual.GetRelativeOffset(column);
                    if (count > 0) { chars += count; rows++; height += row.Height; }
                }
                column = next;
            }
            double oldChars = state.CharsPerRow, oldHeight = state.RowHeight;
            if (rows > 0 && chars > 0) {
                state.CharsPerRow = Math.Max(1, (double)chars / rows);
                state.RowHeight = height / rows;
            }
            state.Measured = true;
            state.Width = View.ActualWidth;
            state.FontSize = editor.FontSize;
            state.Typeface = new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch);
            return Math.Abs(oldChars - state.CharsPerRow) > 0.01 || Math.Abs(oldHeight - state.RowHeight) > 0.01;
        }

        void ProcessLayout() {
            if (disposed || busy) return;
            if (!DocumentCurrent()) {
                // The replacement did not reach this session (a caret callback aborted the
                // property change): resync here and rebuild on a fresh queued pass.
                TrackDocument();
                QueueLayout();
                return;
            }
            // The wrap change clears the window cache during the next measure; do not
            // mix vertical and horizontal states before that rebuild. Dropping them
            // here is safe: the pending measure constructs the new mode's windows.
            if (lastWrap != editor.WordWrap) {
                lastWrap = editor.WordWrap;
                windows.Clear();
                cacheOrder.Clear();
                UpdateHeightMode();
                return;
            }
            // A wrap change, or the first real long line, switches the paragraph height
            // policy; that needs a fresh measure before anything is anchored.
            if (UpdateHeightMode()) return;
            busy = true;
            try {
                if (editor.WordWrap) ProcessVerticalLayout(); else ProcessHorizontalLayout();
            } finally {
                busy = false;
                revealImmediate = false;
                forceReveal = false;
            }
        }

        // Wordwrap folding needs NaN paragraph height so a tall spacer is not clipped
        // to one row. Wrap off keeps the app's height: an inline fold is one row tall.
        bool UpdateHeightMode() {
            if (disposed) return false;
            if (previewOwnsHeight) { flatPending = false; return false; }
            if (!editor.WordWrap) {
                flatPending = false;
                if (!flatHeight) return false;
                flatHeight = false;
                SetLineHeight(restoreLineHeight);
                windows.Clear();
                cacheOrder.Clear();
                return true;
            }
            if (!flatPending || flatHeight || double.IsNaN(restoreLineHeight)) return false;
            flatPending = false;
            flatHeight = true;
            SetLineHeight(double.NaN);
            // Rows measured before the switch used the clipped height; drop them.
            windows.Clear();
            cacheOrder.Clear();
            return true;
        }

        void ProcessVerticalLayout() {
            View.EnsureVisualLines();
            var visible = new List<VisualLine>(View.VisualLines);
            double top = View.VerticalOffset, bottom = top + ViewportHeight();
            foreach (VisualLine visual in visible) {
                DocumentLine line = visual.FirstDocumentLine;
                if (visual.IsDisposed || line.IsDeleted || line.Length <= Threshold) continue;
                LineWindow state = GetWindow(line);
                double firstY = Math.Max(top, visual.VisualTop);
                double lastY = Math.Min(bottom, visual.VisualTop + visual.Height);
                if (lastY <= firstY) continue;
                bool foldedEdge = FoldAtY(visual, firstY + 0.01) != null || FoldAtY(visual, lastY - 0.01) != null;
                // A search select is followed by ScrollToLine, which can park the
                // viewport inside the tall prefix spacer right after the caret was
                // revealed. Put the caret back instead of unfolding the spacer.
                if (foldedEdge && forceReveal && editor.TextArea.Caret.Line == line.LineNumber) {
                    forceReveal = false;
                    if (!CaretOnScreen(state, visual)) {
                        int caretRelative = editor.TextArea.Caret.Column - 1;
                        double revealY = ClampScreenY(state, Finite(lastCaretY) ? lastCaretY : ViewportHeight() / 2);
                        Rebuild(state, StartForAnchor(state, caretRelative, revealY), caretRelative, revealY);
                        break;
                    }
                }
                bool metrics = NeedsMetrics(state);
                if (!foldedEdge && !metrics) continue;
                double screenY;
                int anchor = OffsetAtY(visual, (firstY + lastY) / 2, out screenY);
                if (metrics && !foldedEdge && editor.TextArea.Caret.Line == line.LineNumber) {
                    int caret = editor.TextArea.Caret.Column - 1;
                    if (RealOffset(state, caret)) {
                        double caretY = visual.GetVisualPosition(visual.GetVisualColumn(caret), VisualYPosition.LineMiddle).Y - top;
                        if (caretY >= 0 && caretY <= ViewportHeight()) { anchor = caret; screenY = caretY; }
                    }
                }
                bool changedMetrics = metrics && MeasureWindow(state, visual);
                int start = state.Start;
                if (foldedEdge) {
                    int centered = Math.Max(0, Math.Min(line.Length - WindowChars, anchor - WindowChars / 2));
                    // If a viewport is taller than the real window, centering
                    // it repeatedly cannot expose both edges. Do not oscillate.
                    bool fitsViewport = (state.End - state.Start) * state.RowHeight / state.CharsPerRow >= lastY - firstY;
                    double hysteresis = fitsViewport ? Math.Min(Margin, state.CharsPerRow * 2) : Margin;
                    if (!RealOffset(state, anchor) || Math.Abs(centered - start) >= hysteresis) start = centered;
                }
                if (changedMetrics || start != state.Start) {
                    Rebuild(state, start, anchor, screenY);
                    break; // Our snapshot was invalidated; continue on the next queued pass.
                }
            }
            RememberCaret();
        }

        // WordWrap=false: move the inline window when the visible span escapes it.
        // Both edges are mapped through the line's own layout: a pixel inside an
        // inline fold maps by that fold's laid-out width fraction, real text maps to
        // its real column. The old HorizontalOffset/CharWidth division ignored the
        // fold boundaries, which for mixed Korean/Latin text parked the window away
        // from the text under the viewport. Coverage is the only rebuild test: the
        // rebuild's Redraw invalidates the line again, and rebuilding on measured
        // drift could never settle, because the measurement depends on the range the
        // rebuild changes. Re-measuring alone keeps the next fold widths current.
        // After one rebuild the left edge is real text anchored at screen 0 and the
        // right edge sits Margin characters inside the window, so the next pass with
        // the same scroll offset is a no-op. The anchor keeps its screen X, so
        // re-anchoring does not scroll the text sideways.
        void ProcessHorizontalLayout() {
            View.EnsureVisualLines();
            var visible = new List<VisualLine>(View.VisualLines);
            double left = View.HorizontalOffset, right = left + ViewportWidth();
            foreach (VisualLine visual in visible) {
                DocumentLine line = visual.FirstDocumentLine;
                if (visual.IsDisposed || line.IsDeleted || line.Length <= Threshold) continue;
                LineWindow state = GetHorizontalWindow(line);
                MeasureHorizontalWindow(state, visual);
                int visStart = WindowCharAtX(visual, left);
                int visEnd = WindowCharAtX(visual, Math.Max(left, right - 0.01));
                if (visStart >= state.Start && visEnd <= state.End) continue;
                int caret = editor.TextArea.Caret.Line == line.LineNumber ? editor.TextArea.Caret.Column - 1 : -1;
                int start = visStart - Margin, end = visEnd + Margin;
                if (caret >= visStart && caret <= visEnd) {
                    start = Math.Min(start, caret - WindowChars / 2);
                    end = Math.Max(end, caret + WindowChars / 2);
                }
                if (start < 0) start = 0;
                if (start > line.Length) start = line.Length;
                if (end > line.Length) end = line.Length;
                if (end < start) end = start;
                RebuildHorizontalRange(state, start, end, visStart, 0);
                break; // Our snapshot was invalidated; continue on the next queued pass.
            }
            RememberCaret();
        }

        void TextEntering(object sender, TextCompositionEventArgs e) { CaretChanged(sender, EventArgs.Empty); }

        void MouseDown(object sender, MouseButtonEventArgs e) {
            if (e.ChangedButton == MouseButton.Left && !e.Handled) UnfoldMouse(e.GetPosition(View));
        }

        void MouseMove(object sender, MouseEventArgs e) {
            if (e.LeftButton == MouseButtonState.Pressed && !e.Handled) UnfoldMouse(e.GetPosition(View));
        }

        void UnfoldMouse(Point point) {
            if (busy || !DocumentCurrent() || point.X < 0 || point.Y < 0
                || point.X >= View.ActualWidth || point.Y >= View.ActualHeight) return;
            if (!editor.WordWrap) { UnfoldMouseHorizontal(point); return; }
            View.EnsureVisualLines();
            double y = point.Y + View.VerticalOffset;
            VisualLine visual = View.GetVisualLineFromVisualTop(y);
            if (visual == null || FoldAtY(visual, y) == null) return;
            double screenY;
            int relative = OffsetAtY(visual, y, out screenY);
            LineWindow state = CurrentWindow(visual.FirstDocumentLine);
            Rebuild(state, relative - WindowChars / 2, relative, screenY);
            // Leave the event unhandled: AvalonEdit now hits real text at this
            // same pixel, retaining its normal Shift-click/drag/word selection.
        }

        void UnfoldMouseHorizontal(Point point) {
            View.EnsureVisualLines();
            double x = point.X + View.HorizontalOffset;
            VisualLine visual = View.GetVisualLineFromVisualTop(point.Y + View.VerticalOffset);
            if (visual == null) return;
            HFoldElement fold = FoldAtX(visual, x);
            if (fold == null) return;
            int relative = OffsetAtX(visual, fold, x);
            LineWindow state = CurrentWindow(visual.FirstDocumentLine);
            RebuildHorizontal(state, relative - WindowChars / 2, relative, point.X);
            // Leave the event unhandled: AvalonEdit hits real text at this pixel.
        }

        static bool VerticalCommand(ICommand command, out int direction, out bool page, out bool select) {
            select = command == EditingCommands.SelectUpByLine || command == EditingCommands.SelectDownByLine
                || command == EditingCommands.SelectUpByPage || command == EditingCommands.SelectDownByPage;
            page = command == EditingCommands.MoveUpByPage || command == EditingCommands.MoveDownByPage
                || command == EditingCommands.SelectUpByPage || command == EditingCommands.SelectDownByPage;
            direction = command == EditingCommands.MoveUpByLine || command == EditingCommands.SelectUpByLine
                || command == EditingCommands.MoveUpByPage || command == EditingCommands.SelectUpByPage ? -1 : 1;
            return page || command == EditingCommands.MoveUpByLine || command == EditingCommands.MoveDownByLine
                || command == EditingCommands.SelectUpByLine || command == EditingCommands.SelectDownByLine;
        }

        void PreviewExecuted(object sender, ExecutedRoutedEventArgs e) {
            if (busy || !DocumentCurrent() || e.Handled) return;
            // This also protects Delete/Backspace/paste after scrolling away from
            // the caret. Copy still uses the original selection/document offsets.
            CaretChanged(sender, EventArgs.Empty);
            // Inline folds map every visual column to a real document offset, so the
            // native left/right/line/word commands stay exact without interception.
            if (!editor.WordWrap) return;
            int direction;
            bool page, select;
            if (VerticalCommand(e.Command, out direction, out page, out select)) {
                NavigateVertical(e, direction, page, select);
                return;
            }
            bool wordLeft = e.Command == EditingCommands.MoveLeftByWord || e.Command == EditingCommands.SelectLeftByWord;
            bool wordRight = e.Command == EditingCommands.MoveRightByWord || e.Command == EditingCommands.SelectRightByWord;
            if (!wordLeft && !wordRight) return;
            DocumentLine line = document.GetLineByNumber(editor.TextArea.Caret.Line);
            if (line.Length <= Threshold) return;
            LineWindow state = CurrentWindow(line);
            bool forward = wordRight;
            if (editor.FlowDirection == FlowDirection.RightToLeft) forward = !forward;
            int target = TextUtilities.GetNextCaretPosition(document, editor.CaretOffset,
                forward ? LogicalDirection.Forward : LogicalDirection.Backward, CaretPositioningMode.WordStart);
            if (target < line.Offset || target > line.EndOffset || RealOffset(state, target - line.Offset)) return;
            double screenY = Finite(lastCaretY) ? lastCaretY : ViewportHeight() / 2;
            Rebuild(state, target - line.Offset - WindowChars / 2, target - line.Offset, screenY);
            MoveCaret(new TextViewPosition(document.GetLocation(target)),
                e.Command == EditingCommands.SelectLeftByWord || e.Command == EditingCommands.SelectRightByWord, double.NaN);
            e.Handled = true;
        }

        void NavigateVertical(ExecutedRoutedEventArgs e, int direction, bool page, bool select) {
            TextArea area = editor.TextArea;
            View.EnsureVisualLines();
            TextViewPosition current = area.Caret.Position;
            VisualLine visual = View.GetOrConstructVisualLine(document.GetLineByNumber(current.Line));
            TextLine row = visual.GetTextLine(current.VisualColumn, current.IsAtEndOfLine);
            double x = area.Caret.DesiredXPos;
            if (!Finite(x)) x = visual.GetTextLineVisualXPosition(row, current.VisualColumn);
            VisualLine targetVisual = visual;
            TextLine targetRow;
            double y;
            if (page) {
                y = visual.GetTextLineVisualYPosition(row, VisualYPosition.LineMiddle) + direction * ViewportHeight();
                DocumentLine targetLine = View.GetDocumentLineByVisualTop(Math.Max(0, y));
                targetVisual = View.GetOrConstructVisualLine(targetLine);
                targetRow = targetVisual.GetTextLineByVisualYPosition(y);
            } else {
                int index = visual.TextLines.IndexOf(row) + direction;
                if (index >= 0 && index < visual.TextLines.Count) {
                    targetRow = visual.TextLines[index];
                } else {
                    DocumentLine neighbor = direction < 0 ? visual.FirstDocumentLine.PreviousLine : visual.LastDocumentLine.NextLine;
                    if (neighbor == null) return;
                    targetVisual = View.GetOrConstructVisualLine(neighbor);
                    targetRow = targetVisual.TextLines[direction < 0 ? targetVisual.TextLines.Count - 1 : 0];
                }
                y = targetVisual.GetTextLineVisualYPosition(targetRow, VisualYPosition.LineMiddle);
            }
            FoldElement fold = FoldOnRow(targetVisual, targetRow);
            if (fold == null) return; // Native movement on real rows remains exact.
            LineWindow state = CurrentWindow(targetVisual.FirstDocumentLine);
            if (!page) {
                double rowTop = targetVisual.GetTextLineVisualYPosition(targetRow, VisualYPosition.LineTop);
                // Adjacent-row movement must not jump to the giant object's
                // midpoint. Enter just one estimated row into its near edge.
                y = direction < 0 ? rowTop + targetRow.Height - state.RowHeight / 2 : rowTop + state.RowHeight / 2;
            }
            double screenY;
            int relative = OffsetAtY(targetVisual, y, out screenY);
            if (screenY < 0 || screenY > ViewportHeight())
                screenY = Math.Max(state.RowHeight / 2, Math.Min(ViewportHeight() - state.RowHeight / 2,
                    Finite(lastCaretY) ? lastCaretY : ViewportHeight() / 2));
            Rebuild(state, StartForAnchor(state, relative, screenY), relative, screenY);
            targetVisual = View.GetOrConstructVisualLine(state.Line);
            targetRow = targetVisual.GetTextLine(targetVisual.GetVisualColumn(relative));
            int column = targetVisual.GetVisualColumn(targetRow, x, false);
            int rowStart = targetVisual.GetTextLineVisualStartColumn(targetRow);
            if (column >= rowStart + targetRow.Length && column <= targetVisual.VisualLength)
                column = Math.Max(rowStart, rowStart + targetRow.Length - 1);
            MoveCaret(targetVisual.GetTextViewPosition(column), select, x);
            e.Handled = true;
        }

        void MoveCaret(TextViewPosition position, bool select, double desiredX) {
            TextArea area = editor.TextArea;
            int anchor = area.Selection.IsEmpty ? area.Caret.Offset : document.GetOffset(area.Selection.StartPosition.Location);
            bool previousBusy = busy;
            busy = true;
            try {
                area.Caret.Position = position;
                area.Caret.DesiredXPos = desiredX;
                if (select) area.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(area, anchor, area.Caret.Offset);
                else area.ClearSelection();
                MarkdownLivePreview.Session.SafeBringCaretToView(area);
            } finally { busy = previousBusy; }
            RememberCaret();
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            liveSessions.Remove(editor);
            if (pending != null) { pending.Abort(); pending = null; }
            if (revealConfirm != null) { revealConfirm.Abort(); revealConfirm = null; }
            editor.DocumentChanged -= DocumentReplaced;
            editor.TextArea.Caret.PositionChanged -= CaretChanged;
            editor.TextArea.TextEntering -= TextEntering;
            editor.PreviewMouseDown -= MouseDown;
            editor.PreviewMouseMove -= MouseMove;
            CommandManager.RemovePreviewExecutedHandler(editor, PreviewExecuted);
            View.VisualLinesChanged -= LayoutChanged;
            View.ScrollOffsetChanged -= ScrollChanged;
            if (document != null) {
                document.Changing -= DocumentChanging;
                document.Changed -= DocumentChanged;
            }
            View.ElementGenerators.Remove(generator);
            View.ElementGenerators.Remove(struts);
            if (lineHeightDescriptor != null) lineHeightDescriptor.RemoveValueChanged(View, LineHeightChanged);
            if (flatHeight) { flatHeight = false; SetLineHeight(restoreLineHeight); }
            windows.Clear();
            cacheOrder.Clear();
            changingWindow = null;
            document = null;
        }
    }
}
}
