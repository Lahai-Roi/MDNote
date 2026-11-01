using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;

using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// The stock AvalonEdit selection layer (vendor/Editing/SelectionLayer.cs) converts a segment's
// two ends to visual columns and fills every physical row between them, extending a wrapped
// line to the viewport width. A live table row is ONE TableRowElement whose visual columns are
// row-major (physical row -> cell), so a selection inside one cell painted the whole row width,
// including the neighbouring cell. This layer replaces the stock layer while the live preview
// is attached (same z-order, no hit tests) and paints table rows per cell; every other line
// still goes through the stock BackgroundGeometryBuilder, so nothing else changes. The row under
// the caret is revealed as source (RowLayout.Raw) but is projected the same row-major way, so it
// takes the per-cell path too: only a line without a table row keeps the stock geometry.
internal sealed class LiveTableSelectionLayer : FrameworkElement {
    readonly TextArea textArea;
    readonly TextView textView;
    UIElement replaced;
    bool disposed;

    internal static LiveTableSelectionLayer Install(TextArea area) {
        if (area == null) throw new ArgumentNullException("area");
        return new LiveTableSelectionLayer(area);
    }

    LiveTableSelectionLayer(TextArea area) {
        textArea = area;
        textView = area.TextView;
        IsHitTestVisible = false;
        Focusable = false;
        // The stock selection layer is internal to AvalonEdit, so detaching must put the very
        // same instance back. InsertLayer(Replace) assigns through the layer collection's
        // indexer, which the WPF visual collection rejects while the old child is still
        // connected ("the index is already in use"). Inserting above the stock layer and
        // detaching it afterwards leaves this layer at the stock layer's z-order.
        textView.InsertLayer(this, KnownLayer.Selection, LayerInsertionPosition.Above);
        replaced = DetachStockLayer();
        textView.ScrollOffsetChanged += Invalidate;
        textView.VisualLinesChanged += Invalidate;
        textArea.SelectionChanged += Invalidate;
        TextDocument document = textView.Document;
        if (document != null) document.Changed += DocumentChanged;
    }

    UIElement DetachStockLayer() {
        // The stock layer's type is not public, so it is matched by name. Only one selection
        // layer ever exists in a TextView, and an unmatched (renamed) layer would simply leave
        // the stock painting in place instead of breaking the editor.
        UIElementCollection layers = textView.Layers;
        for (int i = 0; i < layers.Count; i++) {
            UIElement layer = layers[i];
            if (layer == null || Object.ReferenceEquals(layer, this)) continue;
            if (layer.GetType().Name != "SelectionLayer") continue;
            layers.RemoveAt(i);
            return layer;
        }
        return null;
    }

    void Invalidate(object sender, EventArgs e) {
        if (disposed) return;
        InvalidateVisual();
    }

    void DocumentChanged(object sender, DocumentChangeEventArgs e) {
        if (!disposed) InvalidateVisual();
    }

    internal void Dispose() {
        if (disposed) return;
        disposed = true;
        textView.ScrollOffsetChanged -= Invalidate;
        textView.VisualLinesChanged -= Invalidate;
        textArea.SelectionChanged -= Invalidate;
        TextDocument document = textView.Document;
        if (document != null) document.Changed -= DocumentChanged;
        UIElementCollection layers = textView.Layers;
        int index = layers.IndexOf(this);
        if (index < 0) return;
        UIElement previous = replaced;
        replaced = null;
        if (previous != null) {
            layers.RemoveAt(index);
            layers.Insert(index, previous);
        } else {
            layers.RemoveAt(index);
        }
    }

    protected override void OnRender(DrawingContext drawingContext) {
        base.OnRender(drawingContext);
        if (disposed) return;
        TextView view = textView;
        // The stock layer renders the background renderers that belong to the selection layer
        // (search results, current line). Replacing the layer must not drop them.
        IList<IBackgroundRenderer> renderers = view.BackgroundRenderers;
        for (int i = 0; i < renderers.Count; i++) {
            IBackgroundRenderer renderer = renderers[i];
            if (renderer != null && renderer.Layer == KnownLayer.Selection) renderer.Draw(view, drawingContext);
        }
        Selection selection = textArea.Selection;
        if (selection == null || selection.IsEmpty || view.Document == null) return;
        Brush brush = textArea.SelectionBrush;
        if (brush == null) return;
        List<List<Rect>> groups = CollectSelectionRectGroups(view, selection);
        if (groups.Count == 0) return;
        Pen border = textArea.SelectionBorder;
        for (int i = 0; i < groups.Count; i++) {
            BackgroundGeometryBuilder builder = new BackgroundGeometryBuilder();
            builder.AlignToWholePixels = true;
            builder.BorderThickness = border != null ? border.Thickness : 0;
            builder.CornerRadius = textArea.SelectionCornerRadius;
            List<Rect> rects = groups[i];
            for (int r = 0; r < rects.Count; r++) builder.AddRectangle(view, rects[r]);
            Geometry geometry = builder.CreateGeometry();
            if (geometry != null) drawingContext.DrawGeometry(brush, border, geometry);
        }
    }

    // Rectangles the selection would paint, in layer coordinates (document space minus the
    // scroll offset), grouped as they are drawn: one background geometry per group, so the
    // rectangles inside a group join exactly like the stock rounded selection. The probe reads
    // this directly; OnRender draws the same groups.
    internal static List<List<Rect>> CollectSelectionRectGroups(TextView view, Selection selection) {
        var groups = new List<List<Rect>>();
        if (view == null || selection == null || selection.IsEmpty) return groups;
        if (view.Document == null || !view.VisualLinesValid) return groups;
        ReadOnlyCollection<VisualLine> lines = view.VisualLines;
        bool virtualSpace = selection.EnableVirtualSpace;
        foreach (SelectionSegment segment in selection.Segments) {
            if (segment == null) continue;
            CollectSegment(view, lines, segment, virtualSpace, groups);
        }
        return groups;
    }

    static void CollectSegment(TextView view, ReadOnlyCollection<VisualLine> lines, SelectionSegment segment, bool virtualSpace, List<List<Rect>> groups) {
        int documentLength = view.Document.TextLength;
        int segmentStart = Math.Max(0, Math.Min(documentLength, segment.StartOffset));
        int segmentEnd = Math.Max(0, Math.Min(documentLength, segment.EndOffset));
        if (segmentEnd < segmentStart) return;
        var touched = new List<VisualLine>();
        for (int i = 0; i < lines.Count; i++) {
            VisualLine line = lines[i];
            int lineStart = line.FirstDocumentLine.Offset;
            int lineEnd = line.LastDocumentLine.Offset + line.LastDocumentLine.Length;
            if (lineStart > segmentEnd) break;
            if (lineEnd < segmentStart) continue;
            touched.Add(line);
        }
        if (touched.Count == 0) return;
        // The stock rectangles, exactly as the stock layer would paint them, attributed to the
        // visual line that draws them: a rendered table row's rectangles are replaced by
        // per-cell ones, every other line keeps its stock rectangle unchanged. Recomputing the
        // non-table lines from a sub-segment instead would not reproduce the stock layer's
        // full-width extension where the selection continues past the run's last line.
        var plain = new List<Rect>();
        foreach (Rect rect in BackgroundGeometryBuilder.GetRectsForSegment(view, segment, virtualSpace)) {
            double center = rect.Top + rect.Height / 2;
            VisualLine owner = null;
            for (int i = 0; i < touched.Count; i++) {
                double top = touched[i].VisualTop - view.VerticalOffset;
                if (center >= top - .5 && center <= top + touched[i].Height + .5) { owner = touched[i]; break; }
            }
            if (owner == null || RenderedRow(owner) == null) plain.Add(rect);
        }
        if (plain.Count > 0) groups.Add(plain);
        for (int i = 0; i < touched.Count; i++) {
            MarkdownLivePreview.TableRowElement row = RenderedRow(touched[i]);
            if (row == null) continue;
            AddRowGroups(view, touched[i], row, segmentStart, segmentEnd, groups);
        }
    }

    // A table row the live projection draws, or null when the line must keep the stock geometry
    // (a plain line, a revealed delimiter). A revealed (raw) row - the caret/selection line - is
    // still ONE row-major TableRowElement across its wrapped slices, exactly like a rendered row,
    // so the stock geometry would paint every physical row to the table's right edge and cover
    // the neighbouring cells. The pipes and padding it reveals are zero-width stops, so the
    // per-cell rectangles leave no visible gap between the cells either.
    static MarkdownLivePreview.TableRowElement RenderedRow(VisualLine line) {
        ReadOnlyCollection<VisualLineElement> elements = line.Elements;
        for (int i = 0; i < elements.Count; i++) {
            MarkdownLivePreview.TableRowElement row = elements[i] as MarkdownLivePreview.TableRowElement;
            if (row != null) return row;
        }
        return null;
    }

    // One table row (rendered or revealed): intersect the selection with each cell's own source
    // range and paint only that cell's slices. One group per cell keeps neighbouring cells from
    // joining into a single shape through the background geometry builder.
    static void AddRowGroups(TextView view, VisualLine line, MarkdownLivePreview.TableRowElement row, int segmentStart, int segmentEnd, List<List<Rect>> groups) {
        MarkdownLivePreview.LineInfo info = row.Info;
        MarkdownLivePreview.RowLayout layout = row.Row;
        if (info == null || layout == null || info.Cells == null || info.Cells.Count == 0) return;
        int lineOffset = line.FirstDocumentLine.Offset;
        int elementStart = lineOffset + row.RelativeTextOffset;
        int elementEnd = elementStart + row.DocumentLength;
        int start = Math.Max(segmentStart, elementStart);
        int end = Math.Min(segmentEnd, elementEnd);
        if (end < start) return;
        Vector scroll = view.ScrollOffset;
        int cells = Math.Min(info.Cells.Count, layout.Texts.Length);
        for (int c = 0; c < cells; c++) {
            MarkdownLivePreview.Cell cell = info.Cells[c];
            if (cell.End <= cell.Start) continue;
            int from = Math.Max(start, lineOffset + cell.Start);
            int to = Math.Min(end, lineOffset + cell.End);
            if (to < from) continue;
            List<Rect> rects = CellRects(view, line, row, layout, c, cell.Start, from - lineOffset, to - lineOffset, scroll);
            if (rects.Count > 0) groups.Add(rects);
        }
    }

    static List<Rect> CellRects(TextView view, VisualLine line, MarkdownLivePreview.TableRowElement row, MarkdownLivePreview.RowLayout layout, int cell, int cellStart, int from, int to, Vector scroll) {
        var rects = new List<Rect>();
        List<MarkdownLivePreview.CellSlice> slices = layout.Slices[cell];
        MarkdownLivePreview.CellText text = layout.Texts[cell];
        if (slices == null || slices.Count == 0 || text == null) return rects;
        // An offset that falls inside a hidden run has no text index of its own. Snap it to the
        // cell edge it belongs to, like the table caret (LiveTableCaret.cs:288): at or before the
        // cell's start the cell's first text position, anywhere past it the cell's end. Sending
        // every such offset to 0 painted a selection that started after the cell's text across
        // the whole cell.
        int textFrom = row.TextIndexOf(cell, from);
        if (textFrom < 0) textFrom = from <= cellStart ? 0 : text.Text.Length;
        int textTo = row.TextIndexOf(cell, to);
        if (textTo < 0) textTo = to <= cellStart ? 0 : text.Text.Length;
        int first = SliceForStart(slices, textFrom);
        int last = SliceForEnd(slices, textTo);
        if (last < first) last = first;
        for (int slice = first; slice <= last; slice++) {
            int sliceStart = slices[slice].Start;
            int sliceLength = slices[slice].Length;
            int withinStart = slice == first ? textFrom - sliceStart : 0;
            int withinEnd = slice == last ? textTo - sliceStart : sliceLength;
            if (withinStart < 0) withinStart = 0;
            if (withinStart > sliceLength) withinStart = sliceLength;
            if (withinEnd < 0) withinEnd = 0;
            if (withinEnd > sliceLength) withinEnd = sliceLength;
            if (withinEnd <= withinStart) continue;
            // Absolute visual column where this slice's own text starts; the slice's TEXT line
            // is the physical row that draws it (a wrapped row has one per slice).
            int column = row.SliceStartColumn(cell, slice);
            if (column < 0) continue;
            TextLine textLine = line.GetTextLine(column);
            // The slice's own text line, start-first so an indent or a preceding element in the
            // same paragraph cannot shift the cell box away from the measured text. The
            // character range is clamped to the text line by the stock builder itself: passing
            // a column past its end to GetTextLineVisualXPosition throws (WPF cpFirst check).
            double origin = line.GetTextLineVisualXPosition(textLine, line.GetTextLineVisualStartColumn(textLine)) - scroll.X;
            double left = origin + layout.Boundaries[cell];
            double right = origin + layout.Boundaries[cell + 1];
            double lineTop = line.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop) - scroll.Y;
            double lineBottom = line.GetTextLineVisualYPosition(textLine, VisualYPosition.LineBottom) - scroll.Y;
            foreach (Rect piece in BackgroundGeometryBuilder.GetRectsFromVisualSegment(view, line, column + withinStart, column + withinEnd)) {
                // Only this slice's own text line: when the formatter re-wrapped the row (a table
                // a few pixels wider than the paragraph) the range can spill onto the next line.
                if (piece.Top < lineTop - .5 || piece.Bottom > lineBottom + .5) continue;
                // Never past the cell's own box: this is the whole point, the neighbour cell
                // and the viewport width are out of bounds by construction. A wrapped line's
                // extension to the view width is clipped here instead.
                double x0 = piece.Left < left ? left : piece.Left;
                double x1 = piece.Right > right ? right : piece.Right;
                if (x1 <= x0 + .01 || piece.Height <= 0) continue;
                rects.Add(new Rect(x0, piece.Top, x1 - x0, piece.Height));
            }
        }
        return rects;
    }

    // Slice that owns a text index used as a selection START: a soft-wrap boundary belongs to
    // the slice that begins there (typing and the caret continue on the next physical row).
    static int SliceForStart(List<MarkdownLivePreview.CellSlice> slices, int textIndex) {
        for (int r = 0; r < slices.Count; r++) {
            int start = slices[r].Start;
            int inside = start + slices[r].Length;
            if (textIndex < inside) return r;
            if (textIndex == inside) {
                if (r + 1 < slices.Count && slices[r + 1].Start == inside) return r + 1;
                return r;
            }
        }
        return slices.Count - 1;
    }

    // Slice that owns a text index used as a selection END: the same boundary belongs to the
    // slice that ends there, so the rectangle stops at the end of that physical row.
    static int SliceForEnd(List<MarkdownLivePreview.CellSlice> slices, int textIndex) {
        for (int r = 0; r < slices.Count; r++) {
            if (textIndex <= slices[r].Start + slices[r].Length) return r;
            if (r + 1 < slices.Count && textIndex < slices[r + 1].Start) return r;
        }
        return slices.Count - 1;
    }
}
}
