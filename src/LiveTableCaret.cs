using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Utils;

namespace MDNote {
// Table-cell caret for the live preview: inside a drawn table row the caret is placed from the
// cell geometry (CellGeometry), never past the cell box. A drawn table row projects its cells
// as zero-width filler/hidden tokens, so AvalonEdit's own caret measures the wrong column for a
// caret at a cell's end (it lands on the next cell's text start). The stock caret layer keeps
// running (its state is read by the IME probe and the composition window), but its brush is
// transparent while this caret is active, and this layer paints the caret instead.
// Attached and detached by the Session.
internal static partial class MarkdownLivePreview {
    internal sealed partial class Session {
        // The caret rectangle this session draws itself, in TextView coordinates (the scroll
        // offset is already subtracted), or Rect.Empty while AvalonEdit's stock caret is in
        // charge. Geometry is reported even when the editor is not focused (the rectangle is
        // the same one the user sees while focused; only OnRender is gated by focus).
        internal Rect DrawnTableCaret { get { return tableCaretRect; } }
        // Rect.Empty, not the default zero-size rectangle: a zero-size Rect is not IsEmpty.
        Rect tableCaretRect = Rect.Empty;
        TableCaretLayer tableCaretLayer;
        // Re-entrancy guard: measuring builds a visual line, whose generators/transformers can
        // raise editor events that would re-enter this update.
        bool tableCaretUpdating;
        // Coalesced update: AvalonEdit's own caret also measures in a dispatcher callback, and
        // doing the row measurement inside a caret/document listener would run layout work on
        // the typing path while the editor is still publishing the change.
        DispatcherOperation tableCaretWork;
        // Scroll offset the current rectangle was measured at. A scroll moves every line by
        // the same delta, so the rectangle shifts by it at once instead of waiting for the
        // queued remeasure (the stock caret layer already moved). Kept current after shifts.
        Vector tableCaretScroll;
        // The brush the stock caret had before it was made transparent: the ink of the drawn
        // caret, and exactly what Detach puts back. Kept current while hidden, so a theme
        // switch during a table row never restores the pre-theme brush.
        Brush tableCaretStockBrush;
        bool tableCaretStockHidden;
        DependencyPropertyDescriptor tableCaretForeground;
        DependencyPropertyDescriptor tableCaretViewForeground;
        DispatcherOperation tableCaretReassertWork;
        // One-shot LayoutUpdated listener armed while a measurement had no layout to read.
        bool tableCaretLayoutOnce;
        static readonly Brush TableCaretHiddenBrush = Brushes.Transparent;

        void AttachTableCaret() {
            if (tableCaretLayer != null) return;
            tableCaretLayer = new TableCaretLayer(this);
            // Above the stock caret: the stock layer keeps its state (Caret.Show/Hide, the
            // composition window reads its rectangle), only its pixels are covered here.
            View.InsertLayer(tableCaretLayer, KnownLayer.Caret, LayerInsertionPosition.Above);
            Editor.TextArea.Caret.PositionChanged += TableCaretCaretChanged;
            Editor.TextArea.SelectionChanged += TableCaretSelectionChanged;
            Editor.TextArea.IsKeyboardFocusWithinChanged += TableCaretFocusChanged;
            View.ScrollOffsetChanged += TableCaretScrollChanged;
            View.VisualLinesChanged += TableCaretViewChanged;
            // A dark/light switch re-creates the host's caret brush (source.CaretBrush =
            // Foreground), which would show the stock caret beside this one.
            tableCaretForeground = DependencyPropertyDescriptor.FromProperty(Control.ForegroundProperty, typeof(SourceEditor));
            if (tableCaretForeground != null) tableCaretForeground.AddValueChanged(Editor, TableCaretForegroundChanged);
            tableCaretViewForeground = DependencyPropertyDescriptor.FromProperty(TextBlock.ForegroundProperty, typeof(TextView));
            if (tableCaretViewForeground != null) tableCaretViewForeground.AddValueChanged(View, TableCaretForegroundChanged);
            // AvalonEdit's CaretBrush is a plain field, not a DependencyProperty: a host that
            // reassigns it raises no notification. A layout pass follows every theme switch and
            // most editor work, so check there rather than waiting for the next blink.
            Editor.LayoutUpdated += TableCaretLayoutUpdated;
            tableCaretScroll = View.ScrollOffset;
            QueueTableCaretUpdate();
        }

        void DetachTableCaret() {
            Editor.TextArea.Caret.PositionChanged -= TableCaretCaretChanged;
            Editor.TextArea.SelectionChanged -= TableCaretSelectionChanged;
            Editor.TextArea.IsKeyboardFocusWithinChanged -= TableCaretFocusChanged;
            View.ScrollOffsetChanged -= TableCaretScrollChanged;
            View.VisualLinesChanged -= TableCaretViewChanged;
            Editor.LayoutUpdated -= TableCaretLayoutUpdated;
            if (tableCaretLayoutOnce) { Editor.LayoutUpdated -= TableCaretLayoutUpdatedOnce; tableCaretLayoutOnce = false; }
            if (tableCaretForeground != null) { tableCaretForeground.RemoveValueChanged(Editor, TableCaretForegroundChanged); tableCaretForeground = null; }
            if (tableCaretViewForeground != null) { tableCaretViewForeground.RemoveValueChanged(View, TableCaretForegroundChanged); tableCaretViewForeground = null; }
            if (tableCaretReassertWork != null) { tableCaretReassertWork.Abort(); tableCaretReassertWork = null; }
            if (tableCaretWork != null) { tableCaretWork.Abort(); tableCaretWork = null; }
            if (tableCaretLayer != null) {
                UIElementCollection layers = View.Layers;
                int index = layers.IndexOf(tableCaretLayer);
                if (index >= 0) layers.RemoveAt(index);
                tableCaretLayer.Dispose();
                tableCaretLayer = null;
            }
            tableCaretRect = Rect.Empty;
            ShowStockTableCaret();
        }

        void TableCaretCaretChanged(object sender, EventArgs e) { QueueTableCaretUpdate(); }
        void TableCaretSelectionChanged(object sender, EventArgs e) { QueueTableCaretUpdate(); }
        void TableCaretViewChanged(object sender, EventArgs e) { QueueTableCaretUpdate(); }
        void TableCaretFocusChanged(object sender, DependencyPropertyChangedEventArgs e) {
            if (tableCaretLayer != null) tableCaretLayer.FocusChanged(TableCaretFocused);
        }
        // The visible lines are laid out for the new offset by the next measure; meanwhile the
        // old rectangle is still the caret's true pixel position moved by the scroll delta, so
        // move it now and let the queued exact remeasure land after layout.
        void TableCaretScrollChanged(object sender, EventArgs e) { ShiftTableCaretForScroll(); QueueTableCaretUpdate(); }

        void ShiftTableCaretForScroll() {
            Vector now = View.ScrollOffset;
            double dx = now.X - tableCaretScroll.X, dy = now.Y - tableCaretScroll.Y;
            tableCaretScroll = now;
            if (tableCaretRect.IsEmpty || (dx == 0 && dy == 0)) return;
            tableCaretRect = new Rect(tableCaretRect.X - dx, tableCaretRect.Y - dy, tableCaretRect.Width, tableCaretRect.Height);
            if (tableCaretLayer != null) tableCaretLayer.ShiftCaret();
        }

        // The host sets Foreground and then CaretBrush = Foreground in one Theme() call, and a
        // direct reassignment of CaretBrush raises no notification at all. Neither may write the
        // brush from the callback itself: the stock layer could still paint this frame, and the
        // host may not have assigned the new brush yet. Queue the re-assert instead; it takes
        // whatever brush the caret really has by then.
        void TableCaretForegroundChanged(object sender, EventArgs e) { ScheduleTableCaretReassert(); }
        void TableCaretLayoutUpdated(object sender, EventArgs e) {
            if (TableCaretStockNeedsHide()) ScheduleTableCaretReassert();
        }

        bool TableCaretStockNeedsHide() {
            if (disposed || tableCaretRect.IsEmpty || Editor.TextArea == null) return false;
            return !ReferenceEquals(Editor.TextArea.Caret.CaretBrush, TableCaretHiddenBrush);
        }

        void ScheduleTableCaretReassert() {
            if (disposed || tableCaretReassertWork != null || tableCaretRect.IsEmpty) return;
            // Normal, above Render: the hide must land before the next frame paints the stock
            // caret with the reassigned brush.
            tableCaretReassertWork = Editor.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(delegate {
                tableCaretReassertWork = null;
                ReassertStockTableCaret();
            }));
        }

        // Arm a one-shot layout hook for a measurement that could not read the visual lines.
        void QueueTableCaretAfterLayout() {
            if (disposed || tableCaretLayoutOnce) return;
            tableCaretLayoutOnce = true;
            Editor.LayoutUpdated += TableCaretLayoutUpdatedOnce;
        }
        void TableCaretLayoutUpdatedOnce(object sender, EventArgs e) {
            if (tableCaretLayoutOnce) { Editor.LayoutUpdated -= TableCaretLayoutUpdatedOnce; tableCaretLayoutOnce = false; }
            QueueTableCaretUpdate();
        }

        void QueueTableCaretUpdate() {
            if (disposed || tableCaretWork != null) return;
            tableCaretWork = Editor.Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(delegate {
                tableCaretWork = null;
                UpdateTableCaret();
            }));
        }

        void UpdateTableCaret() {
            // A document update has no final layout yet; AvalonEdit raises PositionChanged
            // again once it ends, so that pass measures the published state.
            if (disposed || tableCaretUpdating || Document.IsInUpdate) return;
            tableCaretUpdating = true;
            Rect rect;
            bool pending;
            try { rect = ComputeTableCaretRect(out pending); }
            catch (Exception) {
                // A caret helper must never break typing: an unmeasurable row falls back to the
                // stock caret, which the probes show as a failure instead of a crash.
                rect = Rect.Empty;
                pending = false;
            }
            finally { tableCaretUpdating = false; }
            tableCaretScroll = View.ScrollOffset;
            if (pending) {
                // The layout has not run since the last change, so there is nothing better to
                // measure yet. Never hand the row to the stock caret here: it would draw at the
                // projected column (the reason this layer exists) and blink there. Keep the
                // previous rectangle and remeasure as soon as the layout has run.
                if (!tableCaretRect.IsEmpty) {
                    HideStockTableCaret();
                    if (tableCaretLayer != null) tableCaretLayer.SetCaret(tableCaretRect);
                }
                QueueTableCaretAfterLayout();
                return;
            }
            tableCaretRect = rect;
            if (rect.IsEmpty) ShowStockTableCaret(); else HideStockTableCaret();
            if (tableCaretLayer != null) tableCaretLayer.SetCaret(rect);
        }

        Rect ComputeTableCaretRect(out bool pending) {
            pending = false;
            if (!View.VisualLinesValid) { pending = true; return Rect.Empty; }
            TextArea area = Editor.TextArea;
            if (area == null || Document == null || area.Document != Document) return Rect.Empty;
            int offset = area.Caret.Offset;
            if (offset < 0 || offset > Document.TextLength) return Rect.Empty;
            DocumentLine line = Document.GetLineByOffset(offset);
            if (line == null) return Rect.Empty;
            LineInfo info = Info(line);
            if (!info.Table || info.Delimiter || info.Cells == null || info.Cells.Count == 0 || info.Row == null) return Rect.Empty;
            VisualLine visual = View.GetOrConstructVisualLine(line);
            if (visual == null) return Rect.Empty;
            TableRowElement element = null;
            foreach (VisualLineElement candidate in visual.Elements) { element = candidate as TableRowElement; if (element != null) break; }
            // A row the formatter re-wrapped no longer has one text line per physical row; the
            // stock mapping is the only safe answer there.
            if (element == null || !TableRowElement.RowAligned(visual, info.Row)) return Rect.Empty;
            int relative = offset - line.Offset;
            int cell, textIndex;
            if (!TableCellForOffset(info, element, relative, out cell, out textIndex)) return Rect.Empty;
            bool atWrapEnd = element.IsSoftWrapEnd(relative) && TableCaretAtWrapEnd(area.Caret);
            double x, rowTop, rowBottom;
            if (!CellGeometry(line, cell, textIndex, atWrapEnd, out x, out rowTop, out rowBottom)) return Rect.Empty;
            Rect box = TableCellBox(visual, cell);
            if (box.IsEmpty || box.Width <= 0) return Rect.Empty;
            // Height from the slice's own text line, exactly as the stock caret takes it
            // (TextTop..TextBottom); the row span is the fallback when that line is not found.
            double top = rowTop, bottom = rowBottom;
            int slice = TableCaretSlice(info.Row, cell, textIndex, atWrapEnd);
            if (slice >= 0) {
                int column = element.SliceStartColumn(cell, slice);
                if (column >= 0) {
                    TextLine own = visual.GetTextLine(column);
                    top = visual.GetTextLineVisualYPosition(own, VisualYPosition.TextTop) - View.VerticalOffset;
                    bottom = visual.GetTextLineVisualYPosition(own, VisualYPosition.TextBottom) - View.VerticalOffset;
                }
            }
            double height = bottom - top;
            if (height < 1) { top = rowTop; bottom = rowBottom; height = bottom - top; }
            if (height < 1) height = 1;
            double width = SystemParameters.CaretWidth;
            if (width < 1) width = 1;
            // Clamp into the cell box: never before its left edge and never past its right one.
            double caretX = x < box.Left ? box.Left : x;
            double limit = box.Right - width;
            if (limit < box.Left) limit = box.Left;
            if (caretX > limit) caretX = limit;
            return new Rect(caretX, top, width, height);
        }

        // The caret's wrap side, read from AvalonEdit's unvalidated position. Caret.Position
        // validates the visual column and may reassign the caret offset from inside a caret
        // listener; a click+type probe lost its input that way. A missing member (AvalonEdit
        // updated) only costs the rare wrap-end caret its previous-row side.
        static System.Reflection.PropertyInfo tableCaretRawPosition;
        static bool tableCaretRawPositionChecked;
        static bool TableCaretAtWrapEnd(Caret caret) {
            if (caret == null) return false;
            if (!tableCaretRawPositionChecked) {
                tableCaretRawPositionChecked = true;
                tableCaretRawPosition = typeof(Caret).GetProperty("NonValidatedPosition", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            }
            if (tableCaretRawPosition == null) return false;
            try {
                object value = tableCaretRawPosition.GetValue(caret, null);
                if (value == null) return false;
                return ((TextViewPosition)value).IsAtEndOfLine;
            } catch (Exception) { return false; }
        }

        // The cell that owns a source offset of this row, and the text index inside that cell.
        // A position in the gap between two cells belongs to the gap the row draws there: before
        // its pipe it is the previous cell's trailing padding (that cell's end), after the pipe
        // it is the next cell's leading space (that cell's start). The stock projection maps
        // those offsets to zero-width stops, which is why its caret x lands on the next cell.
        bool TableCellForOffset(LineInfo info, TableRowElement element, int relative, out int cell, out int textIndex) {
            cell = -1; textIndex = -1;
            List<Cell> cells = info.Cells;
            if (cells == null || cells.Count == 0) return false;
            if (relative < cells[0].Start) { cell = 0; textIndex = 0; return true; }
            for (int c = 0; c < cells.Count; c++) {
                if (relative < cells[c].Start) break;
                if (relative > cells[c].End) continue;
                int at = element.TextIndexOf(c, relative);
                if (at < 0) at = relative <= cells[c].Start ? 0 : TableCaretTextLength(info, c);
                cell = c; textIndex = Math.Max(0, at); return true;
            }
            for (int c = 0; c + 1 < cells.Count; c++) {
                if (relative >= cells[c + 1].Start) continue;
                string text = info.Text;
                int pipe = cells[c].End;
                while (pipe < cells[c + 1].Start && pipe < text.Length && text[pipe] != '|') pipe++;
                if (relative <= pipe) { cell = c; textIndex = TableCaretTextLength(info, c); }
                else { cell = c + 1; textIndex = 0; }
                return true;
            }
            // Trailing padding after the last cell's text (Normalize never leaves the caret past
            // the row's closing pipe) stays with the last cell.
            cell = cells.Count - 1;
            textIndex = TableCaretTextLength(info, cell);
            return true;
        }

        static int TableCaretTextLength(LineInfo info, int cell) {
            RowLayout row = info.Row;
            CellText text = row == null || row.Texts == null || cell < 0 || cell >= row.Texts.Length ? null : row.Texts[cell];
            return text == null ? 0 : text.Text.Length;
        }

        // The slice that owns a text index, with the same wrap-side rule CellGeometry uses.
        static int TableCaretSlice(RowLayout row, int cell, int textIndex, bool atWrapEnd) {
            if (row == null || row.Slices == null || cell < 0 || cell >= row.Slices.Length) return -1;
            List<CellSlice> slices = row.Slices[cell];
            if (slices == null || slices.Count == 0) return -1;
            for (int r = 0; r < slices.Count; r++) {
                int start = slices[r].Start, inside = start + slices[r].Length;
                if (textIndex >= start && textIndex < inside) return r;
                if (textIndex == inside) {
                    bool boundary = r + 1 < slices.Count && slices[r + 1].Start == inside;
                    if (!boundary) return r;
                    return atWrapEnd ? r : r + 1;
                }
            }
            return slices.Count - 1;
        }

        void HideStockTableCaret() {
            if (disposed || Editor.TextArea == null) return;
            Caret caret = Editor.TextArea.Caret;
            Brush current = caret.CaretBrush;
            if (ReferenceEquals(current, TableCaretHiddenBrush)) return;
            tableCaretStockBrush = current;
            tableCaretStockHidden = true;
            caret.CaretBrush = TableCaretHiddenBrush;
        }

        void ShowStockTableCaret() {
            if (!tableCaretStockHidden) return;
            // A host that reassigned CaretBrush while the stock caret was hidden owns the
            // newest brush; restore that, never the pre-assignment one.
            if (Editor.TextArea != null) {
                Brush current = Editor.TextArea.Caret.CaretBrush;
                if (!ReferenceEquals(current, TableCaretHiddenBrush)) tableCaretStockBrush = current;
            }
            tableCaretStockHidden = false;
            Brush brush = tableCaretStockBrush;
            tableCaretStockBrush = null;
            Editor.TextArea.Caret.CaretBrush = brush;
        }

        // Runs from a dispatcher callback (never from OnRender or a property-changed callback):
        // take the brush the host really assigned and hide it again.
        void ReassertStockTableCaret() {
            if (disposed || tableCaretRect.IsEmpty || Editor.TextArea == null) return;
            HideStockTableCaret();
            if (tableCaretLayer != null) tableCaretLayer.InvalidateVisual();
        }

        internal bool TableCaretFocused { get { return !disposed && Editor.TextArea.IsKeyboardFocusWithin; } }

        // Probe hooks: the blink timer must only run while the editor has keyboard focus, and the
        // one-frame scroll shift is only observable if the measurement can be forced.
        internal bool TableCaretBlinkRunning { get { return tableCaretLayer != null && tableCaretLayer.BlinkRunning; } }
        internal void UpdateTableCaretNowForProbe() { UpdateTableCaret(); }

        // The brush the stock caret would paint with: whatever it had before it was hidden, or
        // the editor's foreground (what AvalonEdit's CaretLayer falls back to).
        internal Brush TableCaretInk() {
            Brush brush = tableCaretStockBrush;
            if (brush == null) brush = View.GetValue(TextBlock.ForegroundProperty) as Brush;
            if (brush == null) brush = Editor.Foreground;
            if (brush == null) brush = Brushes.Black;
            return brush;
        }

        // Same shape as AvalonEdit's (internal) CaretLayer: a non-hit-testable layer that paints
        // one rectangle and blinks with the system caret blink time, solid again after a move.
        sealed class TableCaretLayer : FrameworkElement {
            readonly Session session;
            readonly DispatcherTimer blinkTimer = new DispatcherTimer();
            bool blink = true;
            bool drawing;
            internal TableCaretLayer(Session session) {
                this.session = session;
                IsHitTestVisible = false;
                Focusable = false;
                blinkTimer.Tick += BlinkTick;
            }
            void BlinkTick(object sender, EventArgs e) { blink = !blink; InvalidateVisual(); }
            internal bool BlinkRunning { get { return blinkTimer.IsEnabled; } }
            internal void SetCaret(Rect rect) {
                drawing = !rect.IsEmpty;
                blink = true;
                if (!drawing) blinkTimer.Stop();
                else StartBlink();
                InvalidateVisual();
            }
            // A scroll moves the painted rectangle without moving the caret: repaint only. The
            // blink phase and the timer carry on, exactly like AvalonEdit's own caret layer.
            internal void ShiftCaret() {
                if (!drawing) drawing = !session.DrawnTableCaret.IsEmpty;
                InvalidateVisual();
            }
            void StartBlink() {
                blinkTimer.Stop();
                // No blink while the editor does not own the keyboard: an unfocused caret is
                // not painted, and a timer that kept running would repaint invisible pixels.
                if (!session.TableCaretFocused) return;
                int milliseconds = GetCaretBlinkTime();
                // Same rule as AvalonEdit's CaretLayer: a disabled blink (<= 0) keeps it solid.
                if (milliseconds <= 0) return;
                blinkTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
                blinkTimer.Start();
            }
            // Like AvalonEdit's CaretLayer: losing the keyboard focus stops the blink timer,
            // gaining it shows the caret again (solid first).
            internal void FocusChanged(bool focused) {
                if (!drawing) { InvalidateVisual(); return; }
                if (focused) { blink = true; StartBlink(); }
                else blinkTimer.Stop();
                InvalidateVisual();
            }
            internal void Dispose() { blinkTimer.Stop(); blinkTimer.Tick -= BlinkTick; }
            protected override void OnRender(DrawingContext drawingContext) {
                base.OnRender(drawingContext);
                // A theme switch may have handed the stock caret a new brush after the last
                // dispatcher pass. Never write CaretBrush from here (the stock layer may be
                // painting in this same frame); queue the re-assert before the next frame.
                if (session.TableCaretStockNeedsHide()) session.ScheduleTableCaretReassert();
                if (!drawing || !blink) return;
                Rect rect = session.DrawnTableCaret;
                if (rect.IsEmpty || !session.TableCaretFocused) return;
                Brush ink = session.TableCaretInk();
                if (ink == null) return;
                drawingContext.DrawRectangle(ink, null, PixelSnapHelpers.Round(rect, PixelSnapHelpers.GetPixelSize(this)));
            }
            [DllImport("user32.dll")] static extern int GetCaretBlinkTime();
        }
    }
}
}
