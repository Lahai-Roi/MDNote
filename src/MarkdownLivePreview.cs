using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Utils;

namespace MDNote {
// Markdown live preview over the single AvalonEdit TextDocument (B plan, product path).
// The document, caret, selection, composition and undo remain native; the host owns
// the editor's font, wrap, colors and read-only state and passes the same values here.
internal static class LiveCompositionGeometry {
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Form { public int Style,X,Y,Left,Top,Right,Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
    struct Font {
        public int Height,Width,Escapement,Orientation,Weight;
        public byte Italic,Underline,Strike,CharSet,OutPrecision,ClipPrecision,Quality,Pitch;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr,SizeConst=32)] public string Face;
    }
    [System.Runtime.InteropServices.DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hwnd,IntPtr context);
    [System.Runtime.InteropServices.DllImport("imm32.dll")] static extern bool ImmSetCompositionWindow(IntPtr context,ref Form form);
    [System.Runtime.InteropServices.DllImport("imm32.dll")] static extern bool ImmGetCompositionWindow(IntPtr context,out Form form);
    [System.Runtime.InteropServices.DllImport("imm32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern bool ImmSetCompositionFontW(IntPtr context,ref Font font);
    [System.Runtime.InteropServices.DllImport("imm32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern bool ImmGetCompositionFontW(IntPtr context,out Font font);
    internal sealed class BaselineMetric { internal Typeface Face; internal double Size,Baseline,Height; }
    static readonly List<BaselineMetric> baselines=new List<BaselineMetric>();
    internal static int BaselineCacheCount { get { return baselines.Count; } }
    internal static long NativeWrites;
    // Cached ascent (Baseline) and full font height for one typeface size. The live task
    // checkbox centres itself on that band, so it asks the table the caret already uses.
    internal static BaselineMetric Metric(Typeface face,double size) {
        foreach (BaselineMetric entry in baselines) if (entry.Face.Equals(face) && entry.Size==size) return entry;
        FormattedText text=new FormattedText("가",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,face,size,Brushes.Black);
        BaselineMetric metric=new BaselineMetric {Face=face,Size=size,Baseline=text.Baseline,Height=text.Height};
        if (baselines.Count==16) baselines.RemoveAt(0);
        baselines.Add(metric);return metric;
    }
    static double Baseline(Typeface face,double size) { return Metric(face,size).Baseline; }
    internal static TextRunProperties CellFont(MarkdownLivePreview.CellText text,int relative) {
        if (text.Properties.Length==0) return text.Default;
        int target=Math.Max(text.Map[0],relative-1),lo=0,hi=text.Properties.Length-1;
        while (lo<hi) { int middle=(lo+hi)/2;if (text.Map[middle]<target) lo=middle+1;else hi=middle; }
        return text.Properties[lo];
    }
    // Resolve the actual TextCharacters run, not TableRowElement's default properties
    // or GetTextBounds on an inter-cell filler (whose height includes cell padding).
    internal static Point Position(SourceEditor editor,out double size,out Typeface face) {
        var view=editor.TextArea.TextView;var caret=editor.TextArea.Caret;
        // Observation must not build a line: constructing just the caret line updates the
        // height tree, but not the text layer's arranged transforms or scroll extent.
        size=editor.FontSize;face=new Typeface(editor.FontFamily,editor.FontStyle,editor.FontWeight,editor.FontStretch);
        var visual=view.GetVisualLine(caret.Line);
        if (visual==null || visual.IsDisposed) return new Point(Double.NaN,Double.NaN);
        // The caret's own validated visual column is the authority. Recomputing a column from
        // the offset (GetVisualColumn -> Reverse) can name a different physical row or cell of a
        // wrapped table row than the one the caret is drawn on, and the composition window then
        // appeared over the first character of the NEXT cell. Only an unvalidated (-1) column is
        // still derived from the offset.
        ICSharpCode.AvalonEdit.TextViewPosition position=caret.Position;
        int relative=caret.Offset-visual.FirstDocumentLine.Offset;
        int canonical=visual.GetVisualColumn(relative);
        // A row-major projected row re-projects after every edit, and AvalonEdit deliberately
        // keeps the caret's OLD visual column across a change. That column can be a filler
        // inside the gap between two columns (drawn in the padding), so the caret rectangle and
        // the composition window would land between cells. Only a column that still resolves to
        // the caret's own offset is trusted; otherwise the offset's canonical stop wins.
        int column=position.VisualColumn;
        bool trusted=column>=0 && (column==canonical || visual.GetRelativeOffset(column)==relative);
        if (!trusted) column=canonical;
        bool atEnd=position.IsAtEndOfLine && column==canonical;
        // The rectangle the caret is actually drawn at (TextView coordinates including scroll),
        // but only while it is drawn at a trusted column: a stale one belongs to another cell.
        Rect rectangle=trusted ? caret.CalculateCaretRectangle() : Rect.Empty;
        TextRunProperties properties=null;
        // Font of the cell that owns the caret offset. A wrapped cell interleaves its physical
        // rows with the other columns, so a run near a column edge can belong to a neighbour;
        // the cell's own source map decides. Interior positions win over a cell's end offset so
        // a boundary between two cells resolves to the cell that starts there.
        foreach (VisualLineElement element in visual.Elements) {
            var strut=element as MarkdownLivePreview.StrutElement;
            if (strut==null || strut.Layout==null) continue;
            MarkdownLivePreview.CellText chosen=null;
            foreach (MarkdownLivePreview.CellText text in strut.Layout.Texts) {
                if (text==null || text.Map.Length==0) continue;
                int first=text.Map[0],last=text.Map[text.Map.Length-1];
                if (relative<first || relative>last) continue;
                if (chosen==null) chosen=text;
                if (relative<last) { chosen=text; break; }
            }
            if (chosen!=null) properties=CellFont(chosen,relative);
            break;
        }
        if (properties==null) {
            // No cell layout (an ordinary line, or a row that has not been projected yet):
            // use the TextCharacters run of the caret's own text line.
            TextLine line=visual.GetTextLine(column,atEnd);
            int start=0;foreach (TextLine previous in visual.TextLines) { if (previous==line) break; start+=previous.Length; }
            int target=Math.Max(0,column-start-1),at=0;
            foreach (var span in line.GetTextRunSpans()) {
                if (span.Value is TextCharacters) properties=span.Value.Properties;
                if (target<at+span.Length) break;
                at+=span.Length;
            }
        }
        size=properties==null ? editor.FontSize : properties.FontRenderingEmSize;
        face=properties==null ? new Typeface(editor.FontFamily,editor.FontStyle,editor.FontWeight,editor.FontStretch) : properties.Typeface;
        // x comes from the rectangle itself: it is the physical row and column the caret is
        // drawn at, so the composing syllable never drifts to another row/cell of the same
        // projected row. y keeps the text top of that same row (baseline minus the cell's own
        // ascent) instead of the default line box the rectangle reports. When no trusted
        // rectangle exists, both are derived from the caret's own textual column.
        TextLine own=rectangle.IsEmpty ? visual.GetTextLine(column,atEnd) : visual.GetTextLineByVisualYPosition(rectangle.Y);
        double x=rectangle.IsEmpty ? visual.GetTextLineVisualXPosition(own,column) : rectangle.X;
        // H11: a right-aligned cell's stock caret x sits on the cell's right border, so the
        // composing syllable was drawn past the cell. Keep the composition point inside the
        // caret's own cell; the drawn table caret (LiveTableCaret.cs) is the pixel the caret is
        // actually seen at, and the cell box is the fallback while that rect is not ready.
        MarkdownLivePreview.Session session=MarkdownLivePreview.SessionFor(editor);
        if (session!=null) {
            double cellLeft,cellRight;
            if (session.CaretCellHorizontal(out cellLeft,out cellRight)) {
                Rect drawn=session.DrawnTableCaret;
                double localX=x-view.HorizontalOffset;
                if (!drawn.IsEmpty && drawn.Left>=cellLeft-0.5 && drawn.Left<=cellRight+0.5) localX=drawn.X;
                else {
                    double limit=cellRight-Math.Max(1,SystemParameters.CaretWidth);
                    if (limit<cellLeft) limit=cellLeft;
                    if (localX<cellLeft) localX=cellLeft;
                    if (localX>limit) localX=limit;
                }
                x=localX+view.HorizontalOffset;
            }
        }
        double baseline=visual.GetTextLineVisualYPosition(own,VisualYPosition.Baseline);
        return new Point(x,baseline-Baseline(face,size))-view.ScrollOffset;
    }
    // TextView coordinates (incl. scroll) to the target window's client area in device pixels.
    // The probe compares against this, and Apply positions the native window with it.
    internal static Point ToDevice(SourceEditor editor,System.Windows.Interop.HwndSource source,Point position) {
        var view=editor.TextArea.TextView;
        Point transformed=view.TransformToAncestor(source.RootVisual).Transform(position);
        return source.CompositionTarget.TransformToDevice.Transform(transformed);
    }
    // The composition form's area flag. CFS_RECT makes the IME draw and wrap the composing text
    // inside rcArea, the visible text body; the old 0x0020 (CFS_FORCE_POSITION) named only the
    // caret point, so a composition that outgrew one row continued at the composition window's
    // own left edge, i.e. the window's far left over the line-number gutter (real MS-IME,
    // 2026-10-30). The first row still starts at ptCurrentPos; rcArea only bounds/wraps it.
    // Notepad's edit control and SDL use CFS_RECT with ptCurrentPos for multiline areas.
    internal const int CompositionFormStyle=0x0001;
    // The text body in the owning window's client pixels, device scaled: the text view's own
    // rectangle, so the line-number gutter (the editor's left margin) and the editor's padding
    // are outside it. The probe compares the composition form against this.
    internal static Rect BodyArea(SourceEditor editor,System.Windows.Interop.HwndSource source) {
        if (source==null || source.RootVisual==null || !source.RootVisual.IsAncestorOf(editor.TextArea.TextView)) return Rect.Empty;
        var view=editor.TextArea.TextView;
        Rect bounds=view.TransformToAncestor(source.RootVisual).TransformBounds(new Rect(0,0,view.ActualWidth,view.ActualHeight));
        return Rect.Transform(bounds,source.CompositionTarget.TransformToDevice);
    }
    internal static void Apply(SourceEditor editor,System.Windows.Interop.HwndSource source) {
        if (source.RootVisual==null || !source.RootVisual.IsAncestorOf(editor.TextArea.TextView)) return;
        double size;Typeface face;Point position=Position(editor,out size,out face);
        if (Double.IsNaN(position.X) || Double.IsNaN(position.Y)) return;
        var view=editor.TextArea.TextView;
        // Keep the legacy/native font setters consistent with the actual cell run.
        // TableSpaceRun uses these properties only as an invisible embedded object;
        // the row's drawn TextCharacters retain their independent CellText properties.
        var visual=view.GetVisualLine(editor.TextArea.Caret.Line);
        if (visual==null) return;
        foreach (VisualLineElement element in visual.Elements) if (element is MarkdownLivePreview.TableRowElement) {
            element.TextRunProperties.SetFontRenderingEmSize(size);element.TextRunProperties.SetTypeface(face);
        }
        var transform=view.TransformToAncestor(source.RootVisual);
        Rect bounds=BodyArea(editor,source);
        position=transform.Transform(position);
        Matrix device=source.CompositionTarget.TransformToDevice;
        position=device.Transform(position);
        // A wide cell that is partly scrolled off leaves the point outside the text view; keep
        // the composition window inside the visible area instead of parking it off-screen.
        double x=Math.Min(Math.Max(position.X,bounds.Left),Math.Max(bounds.Left,bounds.Right));
        double y=Math.Min(Math.Max(position.Y,bounds.Top),Math.Max(bounds.Top,bounds.Bottom));
        string name=face.FontFamily.Source;int comma=name.IndexOf(',');if (comma>0) name=name.Substring(0,comma).Trim();
        // Quality 4 (ANTIALIASED) as SourceEditor.CompositionFont writes it: a different value made the two writers overwrite each other on every apply.
        var font=new Font {Height=-Math.Max(1,(int)Math.Round(size*VisualTreeHelper.GetDpi(editor).DpiScaleY)),Weight=face.Weight.ToOpenTypeWeight(),Italic=(byte)(face.Style==FontStyles.Normal ? 0 : 1),CharSet=1,Quality=4,Face=name.Length>31 ? name.Substring(0,31) : name};
        var form=new Form {Style=CompositionFormStyle,X=(int)x,Y=(int)y,Left=(int)bounds.Left,Top=(int)bounds.Top,Right=(int)bounds.Right,Bottom=(int)bounds.Bottom};
        IntPtr context=ImmGetContext(source.Handle);if (context==IntPtr.Zero) return;
        try {
            // Native setters can issue IMN_SETCOMPOSITIONFONT/WINDOW, which SourceEditor
            // observes too. Do not create a notification -> dispatcher -> setter loop.
            Font currentFont;Form currentForm;
            if (!ImmGetCompositionFontW(context,out currentFont) || currentFont.Height!=font.Height ||
                currentFont.Weight!=font.Weight || currentFont.Italic!=font.Italic || currentFont.Face!=font.Face) {
                ImmSetCompositionFontW(context,ref font);NativeWrites++;
            }
            if (!ImmGetCompositionWindow(context,out currentForm) || currentForm.Style!=form.Style ||
                currentForm.X!=form.X || currentForm.Y!=form.Y || currentForm.Left!=form.Left || currentForm.Top!=form.Top ||
                currentForm.Right!=form.Right || currentForm.Bottom!=form.Bottom) {
                ImmSetCompositionWindow(context,ref form);NativeWrites++;
            }
        } finally { ImmReleaseContext(source.Handle,context); }
    }
}
internal static partial class MarkdownLivePreview {
    internal sealed class Settings {
        internal double FontSize=16;
        // The font-size scale of every fixed px slot measured at the default 16 px (list
        // bullet/checkbox slots and their per-level step, the table column floor). Zoom is a
        // font-size change, so these must follow it; otherwise the bullet's ink outgrows its
        // slot (TextView clips at x=0) and a zoomed table keeps its 100% pixel width.
        internal double FontScale { get { return FontSize>0 ? FontSize/16 : 1; } }
        internal string Family="Malgun Gothic";
        internal double LinePercent=150;
        internal bool Dark;
        internal Color? TextColor;
        internal bool WordWrap=true;
        internal string BaseDirectory;
        internal bool ReadOnly;
        // Host text lookup for the link hover tooltip ("Ctrl+클릭하여 열기 · <url>"); null keeps
        // the Korean wording, which offscreen shots and probes use.
        internal Func<string,string> Translate;
        // A file open/convert is running: even checkboxes must not edit the document.
        internal bool Busy;
        // Stage 2 of the live-as-basic audit: edit mode never reveals the caret line's
        // markup. Rendered stays rendered (Raw() is always false); the 원문 편집 toggle
        // detaches the session and still shows the source. Off keeps every reveal rule.
        internal bool HideMarkers = HideMarkersFromEnvironment();
        // Stage 3 probe seam: app-level probes build Settings themselves and Program.IsSelfTest
        // skips the preferences file, so MDNOTE_HIDE_MARKERS=1 starts every live session with
        // the markers hidden. Unset in a normal run, where the preference alone decides.
        internal static bool HideMarkersFromEnvironment() {
            return Program.IsSelfTest && Environment.GetEnvironmentVariable("MDNOTE_HIDE_MARKERS") == "1";
        }
    }

    // Diagnostics for benches only (not thread-safe, UI thread).
    internal static long DiagParses, DiagParseTicks, DiagDraws;
    internal static long DiagMeasureCells, DiagMeasureTicks, DiagRows, DiagRowTicks, DiagTableTicks, DiagMarkers, DiagMarkerTicks;
    internal static long DiagBlockScans, DiagBlockTicks, DiagStructural;
    internal static long DiagLineBuilds;
    // Lines walked while looking for the bounds of a folded reference-definition run. The
    // probe reads this to prove a keystroke/scroll reuses the cached run instead of rescanning.
    internal static long DiagReferenceScanLines;
    // Probe seam: holds a worker decode for the given milliseconds so a test can lay the row
    // out before the bitmap arrives (the async-decode path is otherwise a race).
    internal static int ProbeImageDecodeDelay;
    // Last picture drawn inside a table cell and the count of such layout calls (probe).
    internal static long DiagImageRuns;
    internal static double DiagImageWidth, DiagImageHeight;

    internal static Session Attach(SourceEditor editor, Settings settings) {
        if (editor == null) throw new ArgumentNullException("editor");
        if (settings == null) throw new ArgumentNullException("settings");
        return new Session(editor, settings);
    }
    // One session per editor while it is attached. The composition geometry needs the caret's
    // drawn cell box, which lives in the session (LiveTableCaret.cs); a linear scan over the
    // open windows (one or two) is cheap and keeps the editor free of a back-reference.
    static readonly List<Session> attachedSessions = new List<Session>();
    internal static Session SessionFor(SourceEditor editor) {
        if (editor == null) return null;
        for (int i = 0; i < attachedSessions.Count; i++) {
            Session session = attachedSessions[i];
            if (!session.IsDisposed && Object.ReferenceEquals(session.Editor, editor)) return session;
        }
        return null;
    }

    static readonly double[] HeadingScale = { 1, 2, 1.6, 1.3, 1.15, 1.1, 1.06 };
    const int ParseLimit = 16384;
    const int MaxSpans = 256;
    static readonly FontFamily CodeFamily = new FontFamily("Consolas, Malgun Gothic");
    // The detached raw view (원문 toggle) shows source text, not prose: a Korean UI face draws
    // ASCII symbols with its own wide forms (Malgun Gothic's '<' is 0.70em, Consolas' 0.55em),
    // which reads as full-width markup. Keep the chosen face for Hangul and take ASCII from the
    // code face, the composite the inline-code runs already use. A face without Hangul already
    // draws ASCII itself, and a monospace face was picked for source on purpose, so both stay
    // untouched. Live-mode revealed lines keep the document face (the line must not change
    // font/width as the caret moves), so only the host's ApplySourceFace asks for this.
    // Cached: the host asks on every layout/render update and a fresh FontFamily each time
    // would re-run the family fallback resolution.
    internal static FontFamily SourceFace(string family) {
        if (String.IsNullOrEmpty(family)) return CodeFamily;
        family=family.Trim();
        if (family.Length==0) return CodeFamily;
        if (family.IndexOf(',')>=0) return new FontFamily(family);
        FontFamily cached;
        if (!sourceFaces.TryGetValue(family,out cached)) {
            cached=new FontFamily(HangulAndProportional(family) ? "Consolas, "+family : family);
            if (sourceFaces.Count>=16) sourceFaces.Clear();
            sourceFaces.Add(family,cached);
        }
        return cached;
    }
    static readonly Dictionary<string,FontFamily> sourceFaces=new Dictionary<string,FontFamily>();
    static bool HangulAndProportional(string family) {
        try {
            GlyphTypeface glyphs;
            if (!new Typeface(new FontFamily(family),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal).TryGetGlyphTypeface(out glyphs)) return false;
            ushort hangul,narrow,wide;
            if (!glyphs.CharacterToGlyphMap.TryGetValue(0xAC00,out hangul)) return false;
            if (!glyphs.CharacterToGlyphMap.TryGetValue('i',out narrow) || !glyphs.CharacterToGlyphMap.TryGetValue('W',out wide)) return false;
            double narrowWidth,wideWidth;
            if (!glyphs.AdvanceWidths.TryGetValue(narrow,out narrowWidth) || !glyphs.AdvanceWidths.TryGetValue(wide,out wideWidth)) return false;
            return Math.Abs(narrowWidth-wideWidth)>0.001;
        } catch (Exception) { return false; }
    }
    internal static double NumberSlot(FontFamily family,double size) {
        var text=new FormattedText("100.",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface(family,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal),size,Brushes.Black);
        return Math.Max(18,Math.Ceiling(text.WidthIncludingTrailingWhitespace+size*.75+1));
    }
    // b52: a content-width table was too narrow to type into (a three-character header
    // column is ~45 px, so the first syllable already wrapped). Every column now keeps a
    // comfortable floor (about eight Korean characters at the default 16 px) while the table
    // itself stays content-width: short columns do not stretch to the full view width, and a
    // table wider than the view still shares the available width as before.
    // The floor is a preference, not a promise: when the view cannot hold it for every column
    // (a narrow window, or a wide table) the columns share the view width instead, so the
    // table never spills past the body.
    internal const double MinimumColumnWidth=140;
    // RowLayout's cell inset; Widths hold the TEXT width, TableWidths adds this padding.
    internal const double CellPadding=10.5;
    // The per-column floor TableWidths gives every column. The per-column cap below no
    // longer limits a growing typed column (TableWidths bounds the table's total instead);
    // probes still read it, so a check and the layout keep one definition of a column's
    // share of the visible width.
    internal static double ColumnFloor(double viewWidth,int count) {
        return ColumnFloor(viewWidth,count,1);
    }
    // The floor is the "about eight Korean characters" of the default 16 px, so it follows the
    // font-size scale. Without it every short column kept its 100% pixel width at a larger zoom.
    internal static double ColumnFloor(double viewWidth,int count,double scale) {
        double available=Math.Max(40,viewWidth-4);
        double floor=MinimumColumnWidth*(scale>0?scale:1);
        return count>0 ? Math.Max(CellPadding*2+1,Math.Min(floor,available/count)) : floor;
    }
    internal static double ColumnCap(double viewWidth,int count) {
        return ColumnCap(viewWidth,count,1);
    }
    internal static double ColumnCap(double viewWidth,int count,double scale) {
        double available=Math.Max(200,viewWidth-40);
        return Math.Max(MinimumColumnWidth*(scale>0?scale:1),available/Math.Max(1,count));
    }
    // Shared by the native FlowDocument table and the live row projection.
    internal static double[] TableWidths(double[] preferredWidths,double[] minima,int count,double viewWidth,bool wrap) {
        return TableWidths(preferredWidths,minima,count,viewWidth,wrap,1);
    }
    internal static double[] TableWidths(double[] preferredWidths,double[] minima,int count,double viewWidth,bool wrap,double scale) {
        const double padding=10.5;
        double available=Math.Max(40,viewWidth-4),boundary=0;
        // The floor only applies as far as the view can pay for it: a narrow window (or a
        // wide table) shares the available width instead.
        double floor=ColumnFloor(viewWidth,count,scale);
        // want is the one-line content width (never under the floor); min is the width the
        // column can be squeezed to without losing a character boundary. The floor raises
        // want only: min stays the real content minimum so the allocation below compares
        // two actual measurements and cannot jump on a 1 px difference.
        var min=new double[count];var want=new double[count];var widths=new double[count];
        double preferred=0,minimum=0;
        for(int c=0;c<count;c++) {
            min[c]=Math.Max(padding*2+1,(c<minima.Length ? minima[c] : 0)+padding*2);
            want[c]=Math.Max(floor,(c<preferredWidths.Length ? preferredWidths[c] : 0)+padding*2);
            preferred+=want[c];minimum+=min[c];
        }
        if(preferred<=available) {
            // The content widths already fit: every column takes them (b52 behaviour).
            Array.Copy(want,widths,count);
        } else if(minimum>available) {
            // Not even the minimums fit the view: share it equally (or keep the minimums
            // when wrapping is off), as before.
            for(int c=0;c<count;c++) widths[c]=wrap ? Math.Max(padding*2+1,available/count) : min[c];
        } else {
            // Browser auto layout. Short columns keep their one-line width first; only when
            // the columns still unvisited could not pay their own minimum does a column take
            // its minimum, and the columns that gave up slack then share what is left in
            // proportion to it. The old equal-factor shrink (min + k*(want-min)) starved a
            // short column: Korean may break between any two syllables, so a long column's
            // minimum is tiny and it kept its slack, while a short column whose minimum sat
            // close to its content width lost the few pixels that held its cell on one line.
            int[] order=new int[count];
            for(int c=0;c<count;c++) order[c]=c;
            for(int c=1;c<count;c++) {
                int index=order[c],probe=c;
                while(probe>0 && want[order[probe-1]]>want[index]) { order[probe]=order[probe-1];probe--; }
                order[probe]=index;
            }
            double given=0,unvisited=minimum;
            for(int i=0;i<count;i++) {
                int c=order[i];
                if(want[c]<=available-given-unvisited) { widths[c]=want[c];given+=want[c]; }
                else { widths[c]=min[c];given+=min[c]; }
                unvisited-=min[c];
            }
            double slack=0;
            for(int c=0;c<count;c++) if(widths[c]<want[c]) slack+=want[c]-min[c];
            double left=available-given;
            if(slack>.001) {
                if(left>0) for(int c=0;c<count;c++) if(widths[c]<want[c]) widths[c]+=left*(want[c]-min[c])/slack;
            } else if(left>0&&count>0) for(int c=0;c<count;c++) widths[c]+=left/count;
        }
        // 0.01 px rounding on the running boundary, so the columns still meet exactly.
        for(int c=0;c<count;c++) {
            double next=Math.Floor((boundary+widths[c])*100)/100;widths[c]=next-boundary;boundary=next;
        }
        return widths;
    }

    // Persistent list context is part of the existing incremental block cache. Body
    // edits keep an equal state; only a structural marker edit propagates numbering.
    // Indent is the marker's column; Content is the column where the item's own text starts
    // (the marker plus the spaces after it), CommonMark's content indentation. A marker left
    // of the parent item's Content is outside that item, so it opens a sibling list instead
    // of nesting ("1. " content starts at column 3, so a two-space bullet is not a child).
    internal sealed class ListState {
        internal int Indent, Depth, Number, Content;
        internal bool Ordered, Task;
        internal DocumentLine Start;
        internal ListState Parent;
        internal bool Same(ListState other) {
            return other != null && Indent == other.Indent && Depth == other.Depth && Number == other.Number &&
                Content == other.Content && Ordered == other.Ordered && Task == other.Task && Start == other.Start &&
                (Parent == other.Parent || Parent != null && Parent.Same(other.Parent));
        }
    }
    internal struct ListPrefix {
        internal int Indent, Start, End, Number, Body;
        // The item's content column (from the scan start): the marker plus the spaces after
        // it, before any task marker, counted with a tab as four columns.
        internal int Content;
        internal bool Ordered, Task;
    }
    static bool ReadList(string text, out ListPrefix prefix) {
        return ReadList(text, 0, out prefix);
    }
    // `from` skips a blockquote's markers: a "> - item" line is a list item whose marker sits
    // after the quote prefix. Positions stay absolute; Indent counts the columns between from
    // and the marker (the item's own nesting inside the quote).
    // The separator after a marker is a space or a tab, the same ASCII rule
    // MarkdownStructuralInput.ParseSlots and MarkdownCommands.ParsePrefix read. A full-width
    // space (U+3000, the Japanese/Chinese IME's space) is content, so "-　item" stays a paragraph
    // for the screen, the structural keys and MD-to-TXT alike.
    static bool ReadList(string text, int from, out ListPrefix prefix) {
        prefix = new ListPrefix(); int p = Math.Max(0, Math.Min(from, text.Length)); int column = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { int width = text[p] == '\t' ? 4 : 1; prefix.Indent += width; column += width; p++; }
        prefix.Start = p;
        if (p + 1 < text.Length && "-*+".IndexOf(text[p]) >= 0 && (text[p + 1] == ' ' || text[p + 1] == '\t')) { p++; column++; }
        else {
            int start = p;
            while (p < text.Length && p - start < 9 && text[p] >= '0' && text[p] <= '9') p++;
            if (p == start || p + 1 >= text.Length || (text[p] != '.' && text[p] != ')') || (text[p + 1] != ' ' && text[p + 1] != '\t')) return false;
            column += p - start + 1;   // the digits and the "." or ")"
            prefix.Ordered = true;
            int number; prefix.Number = Int32.TryParse(text.Substring(start, p - start), out number) && number > 0 && number < 1000000 ? number : 1;
            p++;
        }
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { column += text[p] == '\t' ? 4 : 1; p++; }
        prefix.Content = column;
        prefix.End = prefix.Body = p;
        prefix.Task = p + 2 < text.Length && text[p] == '[' && text[p + 2] == ']' && " xX".IndexOf(text[p + 1]) >= 0;
        if (prefix.Task) { p += 3; while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++; prefix.Body = p; }
        return true;
    }
    // First character after the leading spaces and blockquote markers, the same prefix ParseLine
    // strips. The block cache reads a line's list marker from here too, so the numbering state
    // of "> 1. item" lines is the state of the list the user sees.
    static int ListScanStart(string text) {
        int p = 0, levels = 0;
        // Leading spaces before the first '>' are the line's own indentation, not the quote. A
        // line without a quote is scanned from the start, so ReadList counts that indentation
        // itself and the item's nesting depth is what the list markers show.
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        while (p < text.Length && text[p] == '>' && levels < 6) {
            p++; levels++;
            if (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        }
        return levels > 0 ? p : 0;
    }
    // Leading ">" markers of a line, the same scan ParseLine runs (spaces first, then a marker
    // with one optional spacer each, at most six levels). bodyStart is the content after them.
    static int QuoteMarkers(string text, out int bodyStart) {
        int p = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
        int levels = 0;
        while (p < text.Length && text[p] == '>' && levels < 6) {
            p++;
            if (p < text.Length && (text[p] == ' ' || text[p] == '\t')) p++;
            levels++;
        }
        bodyStart = p;
        return levels;
    }
    // The content after a quote's markers is paragraph text (not a heading, list or rule the
    // quote holds), which is what a following lazy continuation line may continue.
    static bool QuoteBodyParagraph(string text, int bodyStart) {
        int p = bodyStart, indent = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { indent += text[p] == '\t' ? 4 : 1; p++; }
        if (p >= text.Length) return false;
        if (AtxStart(text, p)) return false;
        if (IsRule(text, p, indent)) return false;
        if (FenceStart(text, p)) return false;
        ListPrefix prefix;
        if (ReadList(text, p, out prefix)) return false;
        return true;
    }
    // A line with no ">" of its own directly after a quote's paragraph line continues that
    // quote: CommonMark's lazy continuation. A blank line and any block start end it.
    static bool LazyQuoteLine(string text, BlockState before, TextDocument document, DocumentLine line) {
        if (before.Quote <= 0 || !before.QuoteParagraph) return false;
        int bodyStart;
        if (QuoteMarkers(text, out bodyStart) > 0) return false;
        return !StartsBlock(text, document, line);
    }
    // The line begins a block of its own; a lazy quote continuation stops before it.
    static bool StartsBlock(string text, TextDocument document, DocumentLine line) {
        int p = 0, columns = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { columns += text[p] == '\t' ? 4 : 1; p++; }
        if (p >= text.Length) return true;   // a blank line closes the quote
        if (columns <= 3 && FenceStart(text, p)) return true;
        if (AtxStart(text, p)) return true;
        if (IsRule(text, p, columns)) return true;
        if (SetextUnderlineLevel(text) > 0) return true;
        ListPrefix prefix;
        if (ReadList(text, out prefix)) return true;
        if (columns <= 3 && HasTablePipe(text) && NextIsDelimiter(document, line)) return true;
        bool note; int idStart, idEnd, colon, bodyStart;
        if (columns <= 3 && DefinitionLine(text, p, out note, out idStart, out idEnd, out colon, out bodyStart)) return true;
        return false;
    }
    // "```" / "~~~" at the first non-blank character (the fence shape ReadFence reads).
    static bool FenceStart(string text, int start) {
        int p = start;
        if (p >= text.Length || (text[p] != '`' && text[p] != '~')) return false;
        char marker = text[p];
        int count = 0;
        while (p < text.Length && text[p] == marker) { count++; p++; }
        return count >= 3;
    }
    static ListState NextList(string text, int start, DocumentLine line, ListState previous) {
        ListPrefix prefix; if (!ReadList(text, start, out prefix)) return null;
        // A marker more indented than its predecessor is a child only when it reaches that
        // item's content column (CommonMark: "1. " content starts at column 3, so a two-space
        // bullet closes the item and opens a sibling list). A marker at the item's own column
        // is a sibling of it, and one to its left is already handled by the Indent test below.
        while (previous != null && (previous.Indent > prefix.Indent ||
            previous.Indent == prefix.Indent && (previous.Ordered != prefix.Ordered || previous.Task != prefix.Task) ||
            previous.Indent < prefix.Indent && prefix.Indent < previous.Content)) previous = previous.Parent;
        bool same = previous != null && previous.Indent == prefix.Indent;
        return new ListState { Indent=prefix.Indent, Content=prefix.Content,
            Depth=same ? previous.Depth : previous == null ? 0 : previous.Depth + 1,
            Ordered=prefix.Ordered, Task=prefix.Task, Number=prefix.Ordered ? same ? previous.Number + 1 : prefix.Number : 0,
            Start=same ? previous.Start : line, Parent=same ? previous.Parent : previous };
    }
    // A line with no content of its own: ASCII spaces and tabs only (U+3000 is text). The
    // block cache keeps a list open across one, so the item after it continues the numbering.
    static bool BlankLine(string text) {
        return BlankLine(text, 0);
    }
    // The same from an offset, for a line whose quote markers were already skipped.
    static bool BlankLine(string text, int start) {
        for (int i = start; i < text.Length; i++) if (text[i] != ' ' && text[i] != '\t') return false;
        return true;
    }
    // The width the item's own marker takes in the live line: ordered numbers share one three-
    // digit slot, a task box its 18 px spacer and 22 px box, every other bullet the 18 px slot.
    // The slot follows the font size (the pixel values are the default 16 px): zoom is a
    // font-size change, so a fixed 18 px slot would put the disc's ink at a negative x and
    // TextView would clip it at the view's left edge.
    internal const double ListMarkerSlot=32;
    // 11-01: marker-to-text gap of bullets and numbers; a two-digit number fits the shared 30 px slot at 16 px.
    internal const double MarkerGapEm=0.35;
    // 11-01 user: every marker (bullet, number, task box) starts at the same left x of its slot.
    internal const double MarkerLeadEm=0.2;
    internal const double TaskBoxWidth=ListMarkerSlot-16*MarkerLeadEm;
    // The width of a number marker from its label ("7.", "12.", "100."), the same rule as ListSlotWidth.
    internal static double NumberMarkerWidth(string label,double numberWidth,double scale) {
        int digits=label==null?1:Math.Max(1,label.TrimEnd('.',')').Length);
        return digits<3 ? ListMarkerSlot*scale : Math.Max(ListMarkerSlot*scale,numberWidth);
    }
    internal static double ListSlotWidth(ListState state,double numberWidth) {
        return ListSlotWidth(state,numberWidth,1);
    }
    internal static double ListSlotWidth(ListState state,double numberWidth,double scale) {
        if (scale<=0) scale=1;
        if (state==null) return ListMarkerSlot*scale;
        // 11-01 user: bullet, number and task items start their text at the same x. Every marker
        // takes one slot; a number of three or more digits keeps the wider measured slot so its
        // label is never clipped at the view's left edge.
        if (state.Ordered) return state.Number<100 ? ListMarkerSlot*scale : Math.Max(ListMarkerSlot*scale,numberWidth);
        return ListMarkerSlot*scale;
    }
    // The x where a list item's text starts, relative to the line's list scan start. This is the
    // basic view's own rule (MDNote.cs SetListIndent/ListError): each level adds 18 px (at the
    // default 16 px; the step follows the font size like the marker slots), but a
    // level whose marker slot is narrower than its parent's may not start left of the parent's
    // text (FlowDocument cannot hold the negative margin a bullet under a decimal marker would
    // need), so such an item starts at the parent item's text x and the shortfall is carried to
    // deeper levels. The live line draws [indent][own marker], so the indent is this x minus the
    // item's own slot.
    internal static double ListContentWidth(ListState list,double numberWidth) {
        return ListContentWidth(list,numberWidth,1);
    }
    internal static double ListContentWidth(ListState list,double numberWidth,double scale) {
        if (list==null) return 0;
        if (scale<=0) scale=1;
        var chain=new List<ListState>();
        for (ListState state=list;state!=null&&chain.Count<=16;state=state.Parent) chain.Add(state);
        if (chain.Count!=list.Depth+1) return list.Depth*18*scale+ListSlotWidth(list,numberWidth,scale);
        double content=ListSlotWidth(chain[chain.Count-1],numberWidth,scale),error=0;
        for (int i=chain.Count-2;i>=0;i--) {
            double ideal=18*scale+ListSlotWidth(chain[i],numberWidth,scale)-ListSlotWidth(chain[i+1],numberWidth,scale)-error;
            double margin=ideal>0 ? ideal : 0;
            error=error+margin-ideal;
            content+=margin;
        }
        return content;
    }

    internal struct BlockState : IEquatable<BlockState> {
        internal char Fence;
        internal int FenceLength;
        internal bool Table;
        internal DocumentLine TableStart;
        internal int TableColumns;
        internal ListState List;
        // The List above survived a blank line (CommonMark keeps the list open), so a table line
        // now ends the run: a table at the document level is a new block, and the print and TXT
        // renderers clear their list state at it. Without the gap a table line keeps the item's
        // list context, because its rows carry no marker of their own.
        internal bool ListGap;
        // The line ends as ordinary paragraph text, so a following "===" / "---" line is
        // its setext underline. The next line needs this bit to know it is an underline
        // (and not a thematic break or literal text).
        internal bool Setext;
        // An open block quote: the marker levels this line opened (0 = none) and whether its
        // content is paragraph text, the state a lazy continuation line needs.
        internal int Quote;
        internal bool QuoteParagraph;
        // An open display-math block ("$$" ... "$$" or a ```math fence that closes within
        // MathRender.BlockLines): the lines still to come, the closer included (0 = none), and
        // the opener line. Lines inside are formula source: no fence, list or table state.
        internal int Math;
        internal DocumentLine MathStart;
        public bool Equals(BlockState other) {
            return Fence == other.Fence && FenceLength == other.FenceLength && Table == other.Table && TableStart == other.TableStart && TableColumns == other.TableColumns &&
                Math == other.Math && MathStart == other.MathStart &&
                ListGap == other.ListGap && Setext == other.Setext && Quote == other.Quote && QuoteParagraph == other.QuoteParagraph &&
                (List == other.List || List != null && List.Same(other.List));
        }
    }

    // End-of-line states and validity use compressed runs, not an object per document line.
    internal sealed class BlockCache : ILineTracker, IDisposable {
        readonly TextDocument document;
        readonly CompressingTreeList<BlockState> states = new CompressingTreeList<BlockState>(delegate(BlockState a, BlockState b) { return a.Equals(b); });
        readonly CompressingTreeList<bool> valid = new CompressingTreeList<bool>(delegate(bool a, bool b) { return a == b; });
        readonly WeakLineTracker tracker;
        int firstInvalid;

        internal BlockCache(TextDocument document) {
            this.document = document;
            Reset();
            tracker = WeakLineTracker.Register(document, this);
        }
        void Reset() {
            states.Clear();
            states.InsertRange(0, document.LineCount + 1, new BlockState());
            valid.Clear();
            valid.Add(true);
            valid.InsertRange(1, document.LineCount, false);
            firstInvalid = 1;
        }
        internal void Invalidate(int number) {
            number = Math.Max(1, number);
            if (number >= valid.Count) return;
            valid[number] = false;
            firstInvalid = Math.Min(firstInvalid, number);
        }
        internal BlockState Before(int number) {
            Ensure(number - 1);
            return states[number - 1];
        }
        internal BlockState After(int number) {
            if (number <= 0 || number >= valid.Count) return new BlockState();
            Ensure(number);
            return states[number];
        }
        internal bool IsKnown(int number) { return number > 0 && number < valid.Count && valid[number]; }
        internal DocumentLine KnownTableStart(int number) {
            // Changing uses only already-scanned states, never forces an offscreen scan.
            return number > 0 && number < states.Count ? states[number].TableStart : null;
        }
        void Ensure(int target) {
            while (firstInvalid <= target) {
                int number = firstInvalid;
                DocumentLine line = document.GetLineByNumber(number);
                BlockState old = states[number];
                long started=System.Diagnostics.Stopwatch.GetTimestamp();
                BlockState next = Scan(line, states[number - 1]);
                states[number] = next;
                valid[number] = true;
                if (!old.Equals(next) && number + 1 < valid.Count) valid[number + 1] = false;
                int invalid = valid.IndexOf(false);
                firstInvalid = invalid < 0 ? int.MaxValue : invalid;
                DiagBlockScans++; DiagBlockTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
            }
        }
        BlockState Scan(DocumentLine line, BlockState before) {
            // Inside a display-math block every line is formula source up to the closer.
            if (before.Math > 0) return before.Math == 1 ? new BlockState() : new BlockState { Math=before.Math - 1, MathStart=before.MathStart };
            char fence;
            int length;
            bool tailBlank;
            ReadFence(document, line, out fence, out length, out tailBlank);
            if (before.Fence != '\0') {
                if (fence == before.Fence && length >= before.FenceLength && tailBlank) return new BlockState();
                return before;
            }
            int math = MathOpen(document, line, before);
            if (math > 0) return new BlockState { Math=math, MathStart=line };
            if (length >= 3) return new BlockState { Fence=fence, FenceLength=length };
            string text = ShortText(document, line);
            bool pipe = HasTablePipe(text);
            bool table = pipe && (before.Table || NextIsDelimiter(document, line));
            // Block quote state for the next line. A line opens a quote with its ">" markers;
            // its paragraph state lets the next line continue the quote lazily when that line
            // cannot start a block of its own (CommonMark: "&gt; foo" then "bar" is one quote).
            int quoteBody;
            int quote = QuoteMarkers(text, out quoteBody);
            // A line with no content of its own does not end a list (CommonMark keeps the list
            // container open), so "1." items separated by blank lines keep counting 1, 2, 3.
            // A ">" line whose body is blank is the same for the list inside that quote (the
            // print and TXT renderers recurse on the stripped quote body and see a blank line
            // there), but only while the quote itself continues. A truly blank line inside a
            // quote closes the quote, and the print side reads two separate lists there.
            bool blank = (before.Quote <= 0 && BlankLine(text)) ||
                (quote > 0 && quote == before.Quote && BlankLine(text, quoteBody));
            int tableColumns=0;
            if (table) {
                if (before.Table) tableColumns=before.TableColumns;
                else for (int p=0;p<text.Length&&(text[p]==' '||text[p]=='\t');p++) tableColumns+=text[p]=='\t' ? 4 : 1;
            }
            // A table nested in a list item does not end the list: its rows carry no marker of
            // their own, so the item's state carries over and the list item after the table keeps
            // its depth (and its number). Every other line keeps the ordinary rule that a line
            // without a marker ends the run. A table after a blank line is that other block.
            ListState list = table ? before.ListGap ? null : before.List
                : blank ? before.List : NextList(text, ListScanStart(text), line, before.List);
            bool listGap = blank && list != null;
            bool quoteParagraph = false;
            if (quote > 0) quoteParagraph = QuoteBodyParagraph(text, quoteBody);
            else if (!table && LazyQuoteLine(text, before, document, line)) {
                quote = before.Quote;
                quoteParagraph = true;
            }
            return new BlockState { Table=table, TableStart=table ? before.Table ? before.TableStart : line : null,
                TableColumns=tableColumns,List=list,ListGap=listGap,
                Setext=!table && line.Length <= ParseLimit && SetextText(text, before.Setext),
                Quote=quote, QuoteParagraph=quoteParagraph };
        }
        public void BeforeRemoveLine(DocumentLine line) {
            int number = line.LineNumber;
            states.RemoveAt(number);
            valid.RemoveAt(number);
            // Removing an invalid tail must not leave the old minimum beyond the new first gap.
            firstInvalid = Math.Min(firstInvalid, number);
            Invalidate(number - 1);
            Invalidate(number);
        }
        public void SetLineLength(DocumentLine line, int newTotalLength) {
            Invalidate(line.LineNumber - 1);
            Invalidate(line.LineNumber);
        }
        public void LineInserted(DocumentLine insertionPos, DocumentLine newLine) {
            int number = newLine.LineNumber;
            states.Insert(number, new BlockState());
            valid.Insert(number, false);
            Invalidate(number - 1);
            Invalidate(number);
            // The inserted state's empty placeholder is not a convergence point: its
            // successor was scanned against the pre-insertion predecessor.
            Invalidate(number + 1);
        }
        // A whole-document replace rebuilds the line tree; old DocumentLine objects are
        // detached without IsDeleted, so every DocumentLine-keyed cache must be dropped.
        internal event Action Rebuilt;
        public void RebuildDocument() { Reset(); if (Rebuilt != null) Rebuilt(); }
        public void ChangeComplete(DocumentChangeEventArgs e) { }
        public void Dispose() { tracker.Deregister(); }
    }

    static string ShortText(TextDocument document, DocumentLine line) {
        // Do not copy a giant paragraph merely to determine its block state.
        int offset = line.Offset;
        int length = Math.Min(line.Length, ParseLimit);
        // Callers pass the document's current line, so this range is inside the text and the
        // two branches below never run. They are the last guard against a line whose stored
        // length no longer matches the document (see Session.Info, which maps such a line to
        // the current one before parsing): read only what the text has instead of throwing
        // out of a keystroke.
        if (offset < 0) { offset = 0; length = 0; }
        else if (offset > document.TextLength) { offset = document.TextLength; length = 0; }
        if (length > document.TextLength - offset) length = document.TextLength - offset;
        return document.GetText(offset, length);
    }

    // The number of lines that follow a display-math opener, the closer included, or 0 when the
    // line opens nothing: a line that is exactly "$$", or a "```math" fence, whose closer (a "$$"
    // line / the matching fence) comes within MathRender.BlockLines lines with no blank line in
    // between. The look-ahead is bounded, so the scan of one opener never reads more than that.
    static int MathOpen(TextDocument document, DocumentLine line, BlockState before) {
        if (before.Fence != '\0' || before.Math > 0 || before.Quote > 0) return 0;
        if (line.Length < 2 || line.Length > 64 || line.Offset + line.Length > document.TextLength) return 0;
        int p = 0;
        while (p < line.Length && p < 4 && document.GetCharAt(line.Offset + p) == ' ') p++;
        if (p > 3 || p >= line.Length) return 0;
        char first = document.GetCharAt(line.Offset + p);
        if (first != '$' && first != '`' && first != '~') return 0;
        string text = ShortText(document, line);
        char marker = '\0';
        int markerLength = 0;
        bool dollar = MathRender.IsDollarLine(text);
        if (!dollar && !MathRender.IsMathFence(text, out marker, out markerLength)) return 0;
        MathRender.Seen = true;
        int count = 0;
        for (DocumentLine next = line.NextLine; next != null && count < MathRender.BlockLines; next = next.NextLine) {
            count++;
            if (next.Length > ParseLimit) return 0;
            string value = ShortText(document, next);
            if (BlankLine(value)) return 0;
            if (dollar) {
                if (MathRender.IsDollarLine(value)) return count;
            } else {
                char other;
                int otherLength;
                bool otherBlank;
                ReadFence(document, next, out other, out otherLength, out otherBlank);
                if (other == marker && otherLength >= markerLength && otherBlank) return count;
            }
        }
        return 0;
    }
    // The formula source of an opener that MathOpen accepted: the lines between the opener and
    // the closer, trimmed and joined with spaces (TeX reads a line break as a blank).
    static string MathBlockSource(TextDocument document, DocumentLine opener, int count) {
        var source = new StringBuilder();
        DocumentLine at = opener.NextLine;
        for (int i = 1; i < count && at != null; i++, at = at.NextLine) {
            string value = ShortText(document, at).Trim();
            if (source.Length > 0) source.Append(' ');
            source.Append(value);
        }
        return source.ToString();
    }

    static void ReadFence(TextDocument document, DocumentLine line, out char fence, out int length, out bool tailBlank) {
        fence = '\0';
        length = 0;
        tailBlank = false;
        int p = 0;
        while (p < line.Length && p < 4 && document.GetCharAt(line.Offset + p) == ' ') p++;
        if (p > 3 || p >= line.Length) return;
        char c = document.GetCharAt(line.Offset + p);
        if (c != '`' && c != '~') return;
        int start = p;
        while (p < line.Length && document.GetCharAt(line.Offset + p) == c) p++;
        if (p - start < 3) return;
        fence = c;
        length = p - start;
        tailBlank = true;
        while (p < line.Length) {
            char t = document.GetCharAt(line.Offset + p++);
            if (t != ' ' && t != '\t') { tailBlank = false; break; }
        }
    }

    static bool NextIsDelimiter(TextDocument document, DocumentLine line) {
        DocumentLine next = line.NextLine;
        if (next == null || next.Length > ParseLimit) return false;
        string delimiter = ShortText(document, next);
        if (!IsDelimiter(delimiter)) return false;
        // GFM: the delimiter row has exactly as many cells as the header row. A short one-dash
        // row (`-|`) under a wider header is not a table, so it must not turn "text" plus the
        // next pipe line into one.
        return SplitCells(ShortText(document, line)).Count == SplitCells(delimiter).Count;
    }

    // A delimiter row: cells of optional spaces, an optional leading colon, one or more dashes
    // and an optional trailing colon, at least one cell, at least one pipe. A colon-only cell
    // (`|:|-|`) or a double colon (`|::-|`) is not a delimiter cell, exactly like
    // MarkdownTableCommands.IsDelimiterCell. Allocation-free: this runs for every line's
    // two-line lookahead while the block cache scans.
    static bool IsDelimiter(string text) {
        bool seenPipe = false;
        int p = 0, length = text.Length;
        while (p < length && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p < length && text[p] == '|') { seenPipe = true; p++; }
        int cells = 0;
        while (true) {
            while (p < length && (text[p] == ' ' || text[p] == '\t')) p++;
            if (p >= length) break;   // a trailing pipe or spaces end the row; no empty cell
            if (text[p] == ':') p++;
            int dashes = 0;
            while (p < length && text[p] == '-') { dashes++; p++; }
            if (dashes == 0) return false;
            if (p < length && text[p] == ':') p++;
            while (p < length && (text[p] == ' ' || text[p] == '\t')) p++;
            cells++;
            if (p >= length) break;
            if (text[p] != '|') return false;
            seenPipe = true;
            p++;
            if (p >= length) break;   // the trailing pipe owns no cell
        }
        return seenPipe && cells > 0;
    }

    static bool HasTablePipe(string text) { return TablePipes(text, null); }
    static bool TablePipes(string text, List<int> pipes) {
        bool found = false;
        int budget = text.Length * 3;
        for (int p = 0; p < text.Length; p++) {
            if (text[p] == '\\') { p++; continue; }
            if (text[p] == '`') {
                int run = 1;
                while (p + run < text.Length && text[p + run] == '`') run++;
                int close = FindCodeEnd(text, p + run, run, ref budget);
                p = close >= 0 ? close + run - 1 : p + run - 1;
            } else if (text[p] == '|') {
                if (pipes == null) return true;
                pipes.Add(p);
                found = true;
            }
        }
        return found;
    }
    internal static List<Cell> SplitCells(string text) {
        var pipes = new List<int>();
        TablePipes(text, pipes);
        var cells = new List<Cell>();
        int start = 0;
        for (int i = 0; i <= pipes.Count; i++) {
            int end = i < pipes.Count ? pipes[i] : text.Length;
            int a = start, b = end;
            while (a < b && (text[a] == ' ' || text[a] == '\t')) a++;
            while (b > a && (text[b - 1] == ' ' || text[b - 1] == '\t')) b--;
            if (a != b || (i > 0 && i < pipes.Count)) cells.Add(new Cell { Start=a, End=b });
            start = end + 1;
        }
        return cells;
    }
    // True when text[from..to) holds a column pipe ("\|" is an escaped one and does not count).
    static bool HasCellPipe(string text, int from, int to) {
        for (int i = from; i < to && i < text.Length; i++) {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '|') return true;
        }
        return false;
    }
    static void AddTableStops(LineInfo info) {
        if (info.Cells.Count == 0) { info.Hide(0, info.Text.Length); return; }
        // Consume pipe + adjacent padding in one run. This keeps generator ranges
        // disjoint even for empty cells, and leaves escaped/code-span pipes alone.
        var stops = new List<Replacement>();
        Cell first = info.Cells[0];
        if (first.Start > 0) stops.Add(new Replacement { Start=0, Length=first.Start, Kind=ReplacementKind.Hidden });
        for (int c = 1; c < info.Cells.Count; c++) {
            int start = info.Cells[c - 1].End;
            stops.Add(new Replacement { Start=start, Length=info.Cells[c].Start - start, Kind=ReplacementKind.Column, Column=c });
        }
        int end = info.Cells[info.Cells.Count - 1].End;
        if (end < info.Text.Length) stops.Add(new Replacement { Start=end, Length=info.Text.Length - end, Kind=ReplacementKind.Hidden });
        foreach (Replacement stop in stops) {
            for (int i = info.Replacements.Count - 1; i >= 0; i--) {
                Replacement old = info.Replacements[i];
                if (old.Start < stop.Start + stop.Length && stop.Start < old.Start + old.Length) info.Replacements.RemoveAt(i);
            }
            info.Replacements.Add(stop);
        }
    }

    [Flags]
    internal enum Style { None=0, Bold=1, Italic=2, Strike=4, Code=8, Link=16, Superscript=32, Muted=64, Small=128 }
    internal struct StyledPart {
        internal int Start, Length;
        internal Style Style;
        internal Brush Color;
        internal double Size;
        // Relative size from "1.2em" / "120%" tags; the transformer scales it by the base
        // font size, like the basic view's em/% rules.
        internal double SizeEm;
    }
    internal enum ReplacementKind { Hidden, Spacer, Bullet, Number, Task, Column, Image, Glyph, LineBreak, Math }
    internal struct Replacement {
        internal int Start, Length, Level, Column;
        internal double Width;
        internal string Label, Source;
        internal ReplacementKind Kind;
        // A structural line prefix (#, >, a list bullet/number/checkbox). Only this run is
        // "lead" markup: an inline pair at the line start stays an ordinary hidden marker
        // whose two edges are caret stops.
        internal bool Prefix;
        // A quote marker's gap scales with the font size; list prefix spacers (the item's own
        // indentation and the checkbox slot) are exact pixel widths. Quoted lists contain both.
        internal bool Scaled;
    }
    internal struct Cell {
        internal int Start, End;
    }
    internal sealed class LinkPart {
        internal int Start, Length;
        internal string Url;
    }
    internal sealed class LineInfo {
        internal string Text;
        internal int Heading, QuoteLevels, ListLevel;
        // Setext: this line is a heading because the next line underlines it; the
        // underline line itself carries SetextUnderline (1/2) and stays folded.
        internal bool Setext;
        internal int SetextUnderline;
        // A "[id]: url" reference definition line: muted source, and in the no-reveal mode
        // folded to nothing unless the caret is on it (Session.HideReference).
        internal bool RefDefinition;
        internal DocumentLine TableStart;
        // A table under a list item ("- item" then "  | a | b |") is drawn at the item's body x,
        // the same 18 px per nesting level the list markers use.
        internal double TableIndent;
        internal List<Cell> Cells;
        internal List<LinkPart> Links;
        internal RowLayout Row;
        internal ListState List;
        internal BlockState Before;
        internal int ListStart, ListEnd, ListBody;
        // End of the line's leading run of hidden markup (heading "# ", quote "> ", list
        // prefix, a line-leading "**"). 0 when there is no run or nothing visible after it.
        // Stage 3a: the run has no caret stop of its own, so the line starts at its text.
        internal int LeadEnd;
        internal bool Quote, Code, FenceLine, Rule, Table, Header, Delimiter;
        // Display math. MathCount > 0: this line opens a block of that many more lines (the
        // closer included) whose source is MathLatex. MathRemaining > 0: this line is inside such
        // a block (itself included; 1 = the closer) and MathOpener is the opener line. A one-line
        // "$$ x $$" has MathOne set and no block. Rendered, the opener line carries the formula
        // and the other lines are folded to nothing; while the caret or a selection touches the
        // block every line shows its source.
        internal int MathCount, MathRemaining;
        internal string MathLatex;
        internal DocumentLine MathOpener;
        internal bool MathOne;
        // The line holds at least one inline formula.
        internal bool MathInline;
        // A table row only: the inline formulas of the row the caret or a selection touches, as ",<start>,<start>,"
        // (line-relative starts), null when none. They show their source in the cell. Set before the row is laid out.
        internal string MathTouchKey;
        // The line has no ">" of its own and continues the quote above it (lazy continuation).
        internal bool LazyQuote;
        // How many quote levels the lazily continued line sits in (the line above's marker count):
        // the line is drawn indented by that many quote spacers, like the quote's own lines.
        internal int LazyLevels;
        internal readonly List<StyledPart> Styles = new List<StyledPart>();
        internal readonly List<Replacement> Replacements = new List<Replacement>();
        internal void Hide(int start, int length) {
            if (length > 0) Replacements.Add(new Replacement { Start=start, Length=length, Kind=ReplacementKind.Hidden });
        }
        internal void HidePrefix(int start, int length) {
            if (length > 0) Replacements.Add(new Replacement { Start=start, Length=length, Kind=ReplacementKind.Hidden, Prefix=true });
        }
        internal void AddStyle(int start, int length, Style style, Brush color) {
            AddStyle(start, length, style, color, 0);
        }
        internal void AddStyle(int start, int length, Style style, Brush color, double size) {
            AddStyle(start, length, style, color, size, 0);
        }
        internal void AddStyle(int start, int length, Style style, Brush color, double size, double sizeEm) {
            if (length > 0 && Styles.Count < MaxSpans) Styles.Add(new StyledPart { Start=start, Length=length, Style=style, Color=color, Size=size, SizeEm=sizeEm });
        }
    }

    internal sealed class Palette {
        internal readonly Brush Background, Foreground, Muted, Link, Code, Rule, InlineCode, TableHead, TableCell, QuoteText, QuoteBar;
        internal Palette(bool dark, Color? textColor) {
            Background = BrushOf(dark ? "#202124" : "#FFFFFF");
            Foreground = textColor.HasValue ? MainWindow.FrozenBrush(textColor.Value) : BrushOf(dark ? "#FFFFFF" : "#202124");
            Muted = BrushOf(dark ? "#ADB4BE" : "#687078");
            Link = BrushOf(dark ? "#8AB4F8" : "#1558B0");
            Code = BrushOf(dark ? "#3D434E" : "#F0F2F5");
            InlineCode = BrushOf(dark ? "#495363" : "#E1E6EE");
            QuoteText = BrushOf(dark ? "#DCE0E6" : "#687078");
            QuoteBar = BrushOf(dark ? "#98A2B5" : "#BAC1CA");
            TableHead = BrushOf(dark ? "#444D5C" : "#E1E7EE");
            TableCell = null;
            Rule = BrushOf(dark ? "#7D8796" : "#BAC1CA");
        }
    }
    static Brush BrushOf(string text) {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(text);
        brush.Freeze();
        return brush;
    }

    internal sealed class ImageEntry {
        internal Uri Uri;
        internal BitmapSource Bitmap;
        internal BitmapImage Download;
        internal EventHandler Completed;
        internal EventHandler<ExceptionEventArgs> Failed;
        internal bool Loading=true, Started;
        internal long Pixels;
    }
    internal sealed class ImageCache : IDisposable {
        const long PixelBudget = 40000000;
        readonly Session session;
        readonly Dictionary<string, LinkedListNode<ImageEntry>> entries = new Dictionary<string, LinkedListNode<ImageEntry>>(StringComparer.Ordinal);
        readonly LinkedList<ImageEntry> lru = new LinkedList<ImageEntry>();
        readonly HashSet<string> building = new HashSet<string>(StringComparer.Ordinal);
        DispatcherOperation redraw;
        long pixels;
        int files;
        bool disposed, retry;
        internal ImageCache(Session session) { this.session = session; }
        Uri Resolve(string source) {
            if (string.IsNullOrWhiteSpace(source)) return null;
            try {
                source = Uri.UnescapeDataString(source);
                Uri uri;
                if (!Uri.TryCreate(source, UriKind.Absolute, out uri)) {
                    if (string.IsNullOrEmpty(session.Options.BaseDirectory)) return null;
                    uri = Markdown.LocalImageUri(Path.GetFullPath(Path.Combine(session.Options.BaseDirectory, source)));
                }
                return uri.Scheme == Uri.UriSchemeFile || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
            } catch (ArgumentException) { return null; }
            catch (UriFormatException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }
        internal UIElement Object(string source, string alt) {
            ImageEntry entry = Touch(source);
            double width, height;
            BitmapSource bitmap = Pixels(entry, out width, out height);
            if (bitmap != null) {
                return new Image {
                    Source=bitmap, Width=width, Height=height,
                    Stretch=Stretch.Uniform, StretchDirection=StretchDirection.DownOnly,
                    ToolTip=alt, Focusable=false
                };
            }
            // The placeholder is UI text, not document text: the host hands its T() in through Settings.
            string label = "[이미지: ";
            Func<string, string> translate = session.Options.Translate;
            if (translate != null) label = translate(label);
            return new TextBlock {
                Text=label + (alt ?? "") + "]", Foreground=session.Colors.Muted,
                FontFamily=session.Editor.FontFamily, FontSize=session.Options.FontSize,
                Focusable=false
            };
        }
        // The decoded bitmap and the size Object() would draw it at, or null while it is
        // loading or after a failed load. A table cell draws the pixels itself, so it needs
        // the bitmap and the same cap without a UIElement; the cache and path resolution are
        // the ones Object() uses (no second loader).
        internal BitmapSource CellBitmap(string source, out double width, out double height) {
            return Pixels(Touch(source), out width, out height);
        }
        ImageEntry Touch(string source) {
            Uri uri = Resolve(source);
            ImageEntry entry = null;
            LinkedListNode<ImageEntry> node;
            if (!disposed && uri != null) {
                if (entries.TryGetValue(uri.AbsoluteUri, out node)) {
                    entry = node.Value;
                    lru.Remove(node);
                    lru.AddFirst(node);
                } else {
                    while (entries.Count >= 48 && Evict(null)) { }
                    if (entries.Count < 48) {
                        entry = new ImageEntry { Uri=uri };
                        entries.Add(uri.AbsoluteUri, lru.AddFirst(entry));
                        if (uri.IsFile) StartFiles();
                        else StartDownload(entry);
                    } else retry = true;
                }
            }
            if (entry != null) building.Add(entry.Uri.AbsoluteUri);
            return entry;
        }
        BitmapSource Pixels(ImageEntry entry, out double width, out double height) {
            width = height = 0;
            if (entry == null || entry.Bitmap == null) return null;
            BitmapSource bitmap = entry.Bitmap;
            double w = bitmap.Width, h = bitmap.Height;
            if (w <= 0 || h <= 0) return null;
            double maximum = Math.Max(1, Math.Min(700, session.View.ActualWidth - 24));
            double scale = Math.Min(1, Math.Min(maximum / w, 700 / h));
            width = w * scale;
            height = h * scale;
            return bitmap;
        }
        bool Evict(ImageEntry keep) {
            for (LinkedListNode<ImageEntry> node = lru.Last; node != null; node = node.Previous) {
                ImageEntry entry = node.Value;
                // Pin pending requests: the same URI must never start duplicate loads.
                if (entry == keep || entry.Loading || building.Contains(entry.Uri.AbsoluteUri) || session.UsesImage(entry.Uri)) continue;
                Detach(entry);
                pixels -= entry.Pixels;
                entry.Bitmap = null;
                entries.Remove(entry.Uri.AbsoluteUri);
                lru.Remove(node);
                return true;
            }
            return false;
        }
        void StartFiles() {
            if (disposed) return;
            // Bound concurrent byte buffers/decoders as well as retained bitmaps.
            foreach (ImageEntry entry in lru) {
                if (files >= 2) break;
                if (!entry.Uri.IsFile || !entry.Loading || entry.Started) continue;
                entry.Started = true;
                files++;
                StartFile(entry);
            }
        }
        void StartFile(ImageEntry entry) {
            var weak = new WeakReference(this);
            Dispatcher dispatcher = session.View.Dispatcher;
            string path = entry.Uri.LocalPath;
            Task.Factory.StartNew(delegate {
                BitmapSource bitmap = null;
                int probeDelay = ProbeImageDecodeDelay;
                if (probeDelay > 0) System.Threading.Thread.Sleep(probeDelay);
                try { bitmap = DecodeFile(path); }
                catch (Exception) { /* Bad/missing images stay placeholders; never fault the editor. */ }
                BitmapSource result = bitmap;
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                try {
                    dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                        ImageCache cache = weak.Target as ImageCache;
                        if (cache == null || cache.disposed) return;
                        cache.files--;
                        cache.Finish(entry, result);
                        cache.StartFiles();
                    }));
                } catch (InvalidOperationException) { }
            }, System.Threading.CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
        }
        static BitmapSource DecodeFile(string path) {
            // Everything here, including metadata and file IO, runs on a worker.
            if (new FileInfo(path).Length > 64L * 1024 * 1024) return null;
            byte[] bytes = File.ReadAllBytes(path);
            using (var stream = new MemoryStream(bytes, false)) {
                BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                if (decoder.Frames.Count == 0) return null;
                int width = decoder.Frames[0].PixelWidth, height = decoder.Frames[0].PixelHeight;
                if (width <= 0 || height <= 0) return null;
                double scale = Math.Min(1, Math.Min(1400.0 / width, Math.Sqrt(PixelBudget / ((double)width * height))));
                stream.Position = 0;
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = Math.Max(1, (int)(width * scale));
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
        void StartDownload(ImageEntry entry) {
            try {
                var bitmap = new BitmapImage();
                entry.Download = bitmap;
                entry.Completed = delegate { Finish(entry, bitmap); };
                entry.Failed = delegate { Finish(entry, null); };
                bitmap.DownloadCompleted += entry.Completed;
                bitmap.DownloadFailed += entry.Failed;
                bitmap.DecodeFailed += entry.Failed;
                bitmap.BeginInit();
                // Preserve the natural size for DownOnly (URI metadata is not yet
                // available). Reject oversized decoded results in Finish instead.
                bitmap.UriSource = entry.Uri;
                bitmap.EndInit();
                if (!bitmap.IsDownloading && entry.Loading) Finish(entry, bitmap);
            } catch (Exception) { Finish(entry, null); }
        }
        static void Detach(ImageEntry entry) {
            if (entry.Download != null) {
                entry.Download.DownloadCompleted -= entry.Completed;
                entry.Download.DownloadFailed -= entry.Failed;
                entry.Download.DecodeFailed -= entry.Failed;
            }
            entry.Download = null;
            entry.Completed = null;
            entry.Failed = null;
        }
        void Finish(ImageEntry entry, BitmapSource bitmap) {
            if (disposed || !entry.Loading) return;
            entry.Loading = false;
            Detach(entry);
            long count = 0;
            if (bitmap != null) {
                try {
                    count = (long)bitmap.PixelWidth * bitmap.PixelHeight;
                    if (count > PixelBudget || count <= 0) bitmap = null;
                    else if (bitmap.CanFreeze) bitmap.Freeze();
                } catch (Exception) { bitmap = null; }
            }
            if (bitmap != null) {
                while (pixels + count > PixelBudget && Evict(entry)) { }
                if (pixels + count <= PixelBudget) {
                    entry.Bitmap = bitmap;
                    entry.Pixels = count;
                    pixels += count;
                }
            }
            QueueRedraw();
        }
        internal bool SameUri(string source, Uri uri) {
            Uri resolved = Resolve(source);
            return resolved != null && resolved.AbsoluteUri == uri.AbsoluteUri;
        }
        internal void RefreshVisible() {
            building.Clear();
            if (disposed || !retry) return;
            foreach (ImageEntry entry in lru) {
                if (entry.Loading || session.UsesImage(entry.Uri)) continue;
                retry = false;
                QueueRedraw();
                break;
            }
        }
        void QueueRedraw() {
            if (redraw == null) redraw = session.View.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                redraw = null;
                if (!disposed) session.RedrawImages();
            }));
        }
        public void Dispose() {
            if (disposed) return;
            disposed = true;
            if (redraw != null) { redraw.Abort(); redraw = null; }
            foreach (ImageEntry entry in lru) { Detach(entry); entry.Bitmap = null; }
            entries.Clear();
            lru.Clear();
            building.Clear();
            pixels = 0;
        }
    }

    internal sealed class GlyphMeasure {
        readonly GlyphTypeface normal, bold;
        readonly double size;
        readonly Dictionary<char, double> regular = new Dictionary<char, double>();
        readonly Dictionary<char, double> strong = new Dictionary<char, double>();
        internal GlyphMeasure(FontFamily family, double size) {
            this.size = size;
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out normal);
            new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal).TryGetGlyphTypeface(out bold);
        }
        internal void Clear() { regular.Clear(); strong.Clear(); }
        double Advance(char c, bool header) {
            Dictionary<char, double> cache = header ? strong : regular;
            double width;
            if (cache.TryGetValue(c, out width)) return width;
            GlyphTypeface face = header ? bold : normal;
            ushort glyph;
            width = c == '\t' ? 2 : char.IsWhiteSpace(c) ? 0.35 : c >= 0x2E80 ? 1 : 0.6;
            if (face != null && face.CharacterToGlyphMap.TryGetValue(c, out glyph)) width = face.AdvanceWidths[glyph];
            if (cache.Count >= 4096) cache.Clear();
            cache[c] = width;
            return width;
        }
        internal bool TryDigits(string text, int start, int end, bool header, out double width) {
            width=0; GlyphTypeface face=header ? bold : normal;
            if (face==null || start==end) return false;
            for (int p=start;p<end;p++) {
                ushort glyph; char c=text[p];
                if (c<'0' || c>'9' || !face.CharacterToGlyphMap.TryGetValue(c,out glyph)) return false;
                width+=Advance(c,header);
            }
            width*=size; return true;
        }
        internal double Measure(string text, int start, int end, bool header) {
            double width = 0;
            int budget = end - start;
            for (int p = start; p < end; p++) {
                char c = text[p];
                if (c == '[' && budget > 0 && (p == start || text[p - 1] != '!')) {
                    int middle = -1, close = -1;
                    for (int q = p + 1; q + 1 < end && budget > 0; q++, budget--) {
                        if (text[q] == ']' && text[q + 1] == '(') { middle = q; break; }
                    }
                    if (middle >= 0) for (int q = middle + 2; q < end && budget > 0; q++, budget--) {
                        if (text[q] == ')') { close = q; break; }
                    }
                    if (close >= 0) {
                        for (int i = p + 1; i < middle; i++) width += Advance(text[i], header);
                        p = close;
                        continue;
                    }
                }
                if (c == '*' || c == '_' || c == '~' || c == '`') continue;
                width += Advance(c, header);
            }
            return width * size;
        }
    }
    internal sealed class CellProperties : TextRunProperties {
        readonly Typeface face; readonly double size; readonly Brush ink;
        internal CellProperties(Settings settings, Palette colors, bool header) {
            face=new Typeface(new FontFamily(settings.Family), FontStyles.Normal, header ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
            size=settings.FontSize; ink=colors.Foreground;
        }
        public override Typeface Typeface { get { return face; } }
        public override double FontRenderingEmSize { get { return size; } }
        public override double FontHintingEmSize { get { return size; } }
        public override Brush ForegroundBrush { get { return ink; } }
        public override Brush BackgroundBrush { get { return null; } }
        public override TextDecorationCollection TextDecorations { get { return null; } }
        public override TextEffectCollection TextEffects { get { return null; } }
        public override CultureInfo CultureInfo { get { return CultureInfo.CurrentCulture; } }
    }
    internal sealed class CellParagraph : TextParagraphProperties {
        readonly TextRunProperties properties; readonly double height;
        internal CellParagraph(TextRunProperties properties, double height) { this.properties=properties; this.height=height; }
        public override FlowDirection FlowDirection { get { return FlowDirection.LeftToRight; } }
        public override TextAlignment TextAlignment { get { return TextAlignment.Left; } }
        public override double LineHeight { get { return height; } }
        public override bool FirstLineInParagraph { get { return true; } }
        public override TextRunProperties DefaultTextRunProperties { get { return properties; } }
        public override TextWrapping TextWrapping { get { return TextWrapping.Wrap; } }
        public override TextMarkerProperties TextMarkerProperties { get { return null; } }
        public override double Indent { get { return 0; } }
    }
    internal sealed class CellText : TextSource {
        // A hidden <br> leaves one synthetic text position. FormatCell turns it into a slice
        // boundary (one physical row); it is never drawn and never part of a text run.
        internal const char BreakCharacter=(char)0x2028;
        internal readonly string Text;
        internal readonly int[] Map;
        internal readonly TextRunProperties[] Properties;
        internal readonly TextRunProperties Default;
        internal readonly TextRunCache RunCache=new TextRunCache();
        internal readonly string Source;
        internal readonly int SourceStart,SourceLength;
        internal readonly bool Plain;
        // Text indexes of BreakCharacter, null when the cell has no <br>.
        internal readonly bool[] Breaks;
        // An image inside the cell is one synthetic text position (ImageCharacter) drawing a
        // CellImageRun; ImageSlots is null when the cell has no image.
        internal const char ImageCharacter=(char)0xFFFC;
        internal readonly int[] ImageSlots;
        internal readonly BitmapSource[] ImageBitmaps;
        internal readonly double[] ImageWidths, ImageHeights;
        // The width the cell gives its text. A picture is pinned to it: the row paragraph that
        // finally draws the cell is wider than the cell, so Format() cannot use the width it
        // is handed. 0 means "no cell yet" (the width measurement pass).
        internal readonly double ContentWidth;
        // FormatCell formats one segment at a time; GetTextRun then reports the paragraph
        // end at that index instead of crossing the hidden break into the next row.
        internal int FormatEnd;
        internal bool IsBreak(int index) { return Breaks!=null && index>=0 && index<Breaks.Length && Breaks[index]; }
        internal bool IsImage(int index) { return ImageSlots!=null && index>=0 && index<ImageSlots.Length && ImageSlots[index]>=0; }
        internal bool HasImage { get { return ImageSlots!=null; } }
        // An inline formula inside the cell is one synthetic text position (MathCharacter) drawing a
        // CellMathRun; MathSlots is null when the cell draws no formula (its source stays text).
        internal const char MathCharacter=(char)0xFFFB;
        internal readonly int[] MathSlots;
        internal readonly MathVisual[] MathVisuals;
        internal bool IsMath(int index) { return MathSlots!=null && index>=0 && index<MathSlots.Length && MathSlots[index]>=0; }
        internal bool HasMath { get { return MathSlots!=null; } }
        internal CellMathRun MathRun(int index,TextRunProperties properties) { return new CellMathRun(properties,MathVisuals[MathSlots[index]]); }
        internal CellImageRun ImageRun(int index,TextRunProperties properties) {
            int slot=ImageSlots[index];
            return new CellImageRun(properties,ImageBitmaps[slot],ImageWidths[slot],ImageHeights[slot],ContentWidth);
        }
        internal int NextBreak(int index) {
            if (Breaks==null) return -1;
            for (int p=Math.Max(0,index);p<Breaks.Length;p++) if (Breaks[p]) return p;
            return -1;
        }
        internal CellText(CellText previous,LineInfo info,Cell cell) {
            Source=info.Text;SourceStart=cell.Start;SourceLength=cell.End-cell.Start;Plain=previous.Plain;
            Default=previous.Default;Text=previous.Text;Properties=previous.Properties;Breaks=previous.Breaks;
            ImageSlots=previous.ImageSlots;ImageBitmaps=previous.ImageBitmaps;ImageWidths=previous.ImageWidths;ImageHeights=previous.ImageHeights;
            MathSlots=previous.MathSlots;MathVisuals=previous.MathVisuals;
            ContentWidth=previous.ContentWidth;
            FormatEnd=Text.Length;
            int delta=cell.Start-previous.SourceStart;Map=new int[previous.Map.Length];
            for (int p=0;p<Map.Length;p++) Map[p]=previous.Map[p]+delta;
        }
        // Plain prose appended at the cell's end (typing path): the previous text is a
        // prefix of the new one, so only the appended source characters are added.
        internal CellText(CellText previous,LineInfo info,Cell cell,int appended) {
            Source=info.Text;SourceStart=cell.Start;SourceLength=cell.End-cell.Start;Plain=true;
            Default=previous.Default;Breaks=null;ContentWidth=previous.ContentWidth;
            int from=previous.SourceStart+previous.SourceLength;
            Text=String.Concat(previous.Text,info.Text.Substring(from,appended));
            Map=new int[Text.Length+1];
            Array.Copy(previous.Map,Map,previous.Text.Length);
            for (int p=0;p<appended;p++) Map[previous.Text.Length+p]=from+p;
            Map[Text.Length]=cell.End;
            Properties=new TextRunProperties[Text.Length];
            Array.Copy(previous.Properties,Properties,previous.Text.Length);
            for (int p=previous.Text.Length;p<Text.Length;p++) Properties[p]=Default;
            FormatEnd=Text.Length;
        }
        internal CellText(LineInfo info, Cell cell, Session session, bool raw, double contentWidth) : this(info,cell,session,raw,contentWidth,info.MathTouchKey) { }
        // touch: LineInfo.MathTouchKey (formulas drawn as source); the width measurement passes null.
        internal CellText(LineInfo info, Cell cell, Session session, bool raw, double contentWidth, string touch) {
            Source=info.Text;SourceStart=cell.Start;SourceLength=cell.End-cell.Start;Plain=Session.PlainCell(info,cell);
            Default=session.CellDefaults[info.Header ? 1 : 0];ContentWidth=contentWidth;
            var text=new StringBuilder(); var map=new List<int>(); var properties=new List<TextRunProperties>();
            var breaks=new List<int>();
            var images=new List<int>(); var bitmaps=new List<BitmapSource>(); var widths=new List<double>(); var heights=new List<double>();
            var formulas=new List<int>(); var drawings=new List<MathVisual>();
            var sourceProperties=new TextRunProperties[cell.End-cell.Start];
            for (int i=0;i<sourceProperties.Length;i++) sourceProperties[i]=Default;
            // Styles are bounded by MaxSpans, and built once per measured/visible row.
            foreach (StyledPart style in info.Styles) {
                int a=Math.Max(cell.Start,style.Start), b=Math.Min(cell.End,style.Start+style.Length);
                for (int p=a;p<b;p++) {
                    var value=new VisualLineElementTextRunProperties(sourceProperties[p-cell.Start]); Typeface face=value.Typeface;
                    value.SetTypeface(new Typeface((style.Style & Style.Code)!=0 ? CodeFamily : face.FontFamily,
                        (style.Style & Style.Italic)!=0 ? FontStyles.Italic : face.Style,
                        (style.Style & Style.Bold)!=0 ? FontWeights.ExtraBold : face.Weight, face.Stretch));
                    if ((style.Style & Style.Strike)!=0) value.SetTextDecorations(TextDecorations.Strikethrough);
                    if ((style.Style & Style.Link)!=0) value.SetForegroundBrush(session.Colors.Link);
                    if ((style.Style & Style.Code)!=0) value.SetBackgroundBrush(session.Colors.InlineCode);
                    if (style.Color!=null) value.SetForegroundBrush(style.Color);
                    if (style.Size>0) value.SetFontRenderingEmSize(style.Size);
                    sourceProperties[p-cell.Start]=value;
                }
            }
            int replacement=0;
            for (int p=cell.Start;p<cell.End;) {
                while (replacement<info.Replacements.Count && info.Replacements[replacement].Start+info.Replacements[replacement].Length<=p) replacement++;
                if (!raw && replacement<info.Replacements.Count) {
                    Replacement item=info.Replacements[replacement];
                    if (item.Start==p && item.Kind==ReplacementKind.LineBreak) {
                        // The tag's source range maps from this one position; the raw tag
                        // stays visible on the caret's own line because raw skips this branch.
                        breaks.Add(text.Length);
                        text.Append(BreakCharacter); map.Add(p); properties.Add(sourceProperties[p-cell.Start]);
                        p=Math.Min(cell.End,p+item.Length); continue;
                    }
                    if (item.Start==p && (item.Kind==ReplacementKind.Hidden || item.Kind==ReplacementKind.Glyph)) {
                        if (item.Kind==ReplacementKind.Glyph) foreach (char c in item.Label) { text.Append(c); map.Add(p); properties.Add(sourceProperties[p-cell.Start]); }
                        p=Math.Min(cell.End,p+item.Length); continue;
                    }
                    if (item.Start==p && item.Kind==ReplacementKind.Image) {
                        // The picture is one object position covering the "![alt](src)" span,
                        // like the inline object outside a table. Until the bitmap is decoded
                        // the cell keeps the source text, so nothing is lost while loading and
                        // a missing image still shows its alt text.
                        double width, height;
                        BitmapSource bitmap=session.CellBitmap(item.Source,out width,out height);
                        if (bitmap!=null) {
                            images.Add(text.Length); bitmaps.Add(bitmap); widths.Add(width); heights.Add(height);
                            text.Append(ImageCharacter); map.Add(p); properties.Add(sourceProperties[p-cell.Start]);
                            p=Math.Min(cell.End,p+item.Length); continue;
                        }
                    }
                    if (item.Start==p && item.Kind==ReplacementKind.Math && p+item.Length<=cell.End &&
                        (touch==null || touch.IndexOf(","+item.Start.ToString(CultureInfo.InvariantCulture)+",",StringComparison.Ordinal)<0)) {
                        // An inline formula is one object position covering the "$...$" span, drawn in the
                        // cell text's size and colour. A formula that does not parse, and one the caret or a
                        // selection touches, keeps its source as ordinary cell text.
                        MathVisual visual=session.CellMath(item.Source,sourceProperties[p-cell.Start]);
                        if (visual!=null) {
                            formulas.Add(text.Length); drawings.Add(visual);
                            text.Append(MathCharacter); map.Add(p); properties.Add(sourceProperties[p-cell.Start]);
                            p=Math.Min(cell.End,p+item.Length); continue;
                        }
                    }
                }
                text.Append(info.Text[p]); map.Add(p); properties.Add(sourceProperties[p-cell.Start]); p++;
            }
            map.Add(cell.End); Text=text.ToString(); Map=map.ToArray(); Properties=properties.ToArray(); FormatEnd=Text.Length;
            if (breaks.Count>0) {
                var marks=new bool[Text.Length];
                foreach (int at in breaks) marks[at]=true;
                Breaks=marks;
            }
            if (images.Count>0) {
                ImageSlots=new int[Text.Length];
                for (int i=0;i<ImageSlots.Length;i++) ImageSlots[i]=-1;
                for (int i=0;i<images.Count && i<ImageSlots.Length;i++) ImageSlots[images[i]]=i;
                ImageBitmaps=bitmaps.ToArray(); ImageWidths=widths.ToArray(); ImageHeights=heights.ToArray();
            }
            if (formulas.Count>0) {
                MathSlots=new int[Text.Length];
                for (int i=0;i<MathSlots.Length;i++) MathSlots[i]=-1;
                for (int i=0;i<formulas.Count && formulas[i]<MathSlots.Length;i++) MathSlots[formulas[i]]=i;
                MathVisuals=drawings.ToArray();
            }
        }
        public override TextRun GetTextRun(int index) {
            if (index>=Text.Length || index>=FormatEnd) return new TextEndOfParagraph(1);
            // A break ends the text line after consuming its own position; the next text
            // line continues with the following run. Never draw the marker itself.
            if (IsBreak(index)) return new TextEndOfLine(1);
            if (IsImage(index)) return ImageRun(index,Properties[index]);
            if (IsMath(index)) return MathRun(index,Properties[index]);
            int end=index+1; while (end<Text.Length && end<FormatEnd && Properties[end]==Properties[index] && !IsBreak(end) && !IsImage(end) && !IsMath(end)) end++;
            return new TextCharacters(Text,index,end-index,Properties[index]);
        }
        public override TextSpan<CultureSpecificCharacterBufferRange> GetPrecedingText(int limit) {
            int length=Math.Min(limit,Text.Length);
            return new TextSpan<CultureSpecificCharacterBufferRange>(length,new CultureSpecificCharacterBufferRange(CultureInfo.CurrentCulture,new CharacterBufferRange(Text,0,length)));
        }
        public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index) { return index; }
    }
    // The picture inside a table cell. A cell is not a visual-line element range, only a
    // TextSource inside the row projection, so the image is an embedded object run of that
    // source: one visual column for the whole "![alt](src)" span (the span's interior maps
    // to this column, like a hidden marker). Its width is pinned to the cell's text width,
    // so it is never drawn wider than the cell and the row stays the width the cell measured.
    internal sealed class CellImageRun : TextEmbeddedObject {
        // Below this the formatter is measuring a minimum width, not laying out a line.
        const double MinimumWidth=24;
        readonly TextRunProperties properties;
        readonly BitmapSource bitmap;
        readonly double naturalWidth, naturalHeight, maxWidth;
        double width, height;
        internal CellImageRun(TextRunProperties properties, BitmapSource bitmap, double naturalWidth, double naturalHeight, double maxWidth) {
            this.properties=properties; this.bitmap=bitmap;
            this.naturalWidth=Math.Max(1,naturalWidth); this.naturalHeight=Math.Max(1,naturalHeight);
            this.maxWidth=maxWidth;
            width=this.naturalWidth; height=this.naturalHeight;
        }
        public override int Length { get { return 1; } }
        public override TextRunProperties Properties { get { return properties; } }
        public override CharacterBufferReference CharacterBufferReference { get { return new CharacterBufferReference(); } }
        public override LineBreakCondition BreakBefore { get { return LineBreakCondition.BreakRestrained; } }
        public override LineBreakCondition BreakAfter { get { return LineBreakCondition.BreakRestrained; } }
        public override bool HasFixedSize { get { return false; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            double resolved=naturalWidth;
            // The row paragraph that draws a cell is wider than the cell itself, so the cell
            // width pins the picture; the width handed in only caps a pass that has no cell
            // yet (the width-measurement walk), where it is the remaining space of the line.
            if (maxWidth>0) { if (maxWidth<resolved) resolved=maxWidth; }
            else if (!double.IsNaN(remainingParagraphWidth) && !double.IsInfinity(remainingParagraphWidth) &&
                remainingParagraphWidth>=MinimumWidth && remainingParagraphWidth<resolved) resolved=remainingParagraphWidth;
            if (resolved<1) resolved=1;
            width=resolved;
            height=naturalHeight*(resolved/naturalWidth);
            // A small picture (an icon) sits on the text's ink band, like the live checkbox and
            // the inline image host; a larger one still stands on the baseline.
            double baseline=height;
            if (properties!=null) {
                var metric=LiveCompositionGeometry.Metric(properties.Typeface,properties.FontRenderingEmSize);
                if (height<=metric.Height) baseline=(height+metric.Baseline-(metric.Height-metric.Baseline))/2;
            }
            this.baseline=baseline;
            if (maxWidth>0) { DiagImageRuns++; DiagImageWidth=width; DiagImageHeight=height; }
            return new TextEmbeddedObjectMetrics(width,height,baseline);
        }
        double baseline;
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) { return new Rect(0,-baseline,width,height); }
        public override void Draw(DrawingContext context, Point origin, bool rightToLeft, bool sideways) {
            if (bitmap==null || width<=0 || height<=0) return;
            context.DrawImage(bitmap,new Rect(origin.X,origin.Y-baseline,width,height));
        }
    }
    // An inline formula inside a table cell: the same MathVisual the live line draws (MathRun), as a
    // fixed-size object of the cell's TextSource. The visual was rendered at the cell text's size and in
    // its colour, so the line it sits on grows to its height and the column measures its width.
    internal sealed class CellMathRun : TextEmbeddedObject {
        readonly TextRunProperties properties;
        readonly MathVisual visual;
        internal CellMathRun(TextRunProperties properties, MathVisual visual) { this.properties=properties; this.visual=visual; }
        public override int Length { get { return 1; } }
        public override TextRunProperties Properties { get { return properties; } }
        public override CharacterBufferReference CharacterBufferReference { get { return new CharacterBufferReference(); } }
        public override LineBreakCondition BreakBefore { get { return LineBreakCondition.BreakRestrained; } }
        public override LineBreakCondition BreakAfter { get { return LineBreakCondition.BreakRestrained; } }
        public override bool HasFixedSize { get { return true; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            return new TextEmbeddedObjectMetrics(visual.Width,visual.Height,visual.Ascent);
        }
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) { return new Rect(0,-visual.Ascent,visual.Width,visual.Height); }
        public override void Draw(DrawingContext context, Point origin, bool rightToLeft, bool sideways) {
            if (visual.Drawing==null) return;
            context.PushTransform(new TranslateTransform(origin.X+visual.OffsetX,origin.Y-visual.Ascent));
            context.DrawDrawing(visual.Drawing);
            context.Pop();
        }
    }
    internal struct CellSlice { internal int Start, Length; internal double Width, Height, Baseline; }
    internal struct CellMetric { internal double Preferred, Minimum; }
    internal sealed class CellShape { internal double Width; internal List<CellSlice> Slices; }
    internal sealed class TableLayout {
        internal const int Columns = 128;
        internal readonly DocumentLine Start;
        internal DocumentLine Next, SampleEnd;
        internal int Rows, Characters, MetricCharacters, ShapeCharacters;
        internal bool Dirty, PendingRemeasure;
        internal int Count, Version, PublishedCount;
        // Font-size scale the table's fixed pixel paddings follow (see Settings.FontScale).
        internal double Scale=1;
        internal readonly Dictionary<string,CellMetric> Metrics=new Dictionary<string,CellMetric>(StringComparer.Ordinal);
        internal readonly Dictionary<string,CellShape> Shapes=new Dictionary<string,CellShape>(StringComparer.Ordinal);
        // Remember row identities, not a metric/string history of every typed prefix.
        // Rows beyond the bounded sample are measured once, after leaving the table.
        internal readonly HashSet<DocumentLine> HotRows=new HashSet<DocumentLine>();
        internal double[] Widths = new double[Columns], Work, Minima = new double[Columns], WorkMinima;
        internal int[] Align = new int[Columns], WorkAlign;
        // b52: column widths grow while the caret types inside this table and only shrink in
        // the idle re-sample after the caret leaves it. A pure `Grow` value too, so a shrink
        // published by a stale sample is never applied to a column the user is typing in.
        // Grow holds the typed cell's own one-line width (text + IME allowance + 2); it is
        // not capped per column, because TableWidths already bounds the table's total and
        // takes the slack from the other columns instead of wrapping the typed cell.
        // GrowColumns is the column count the entries were recorded for: a column that was
        // inserted or removed renumbers them, so they are dropped instead of moving to a
        // neighbour (a live table is edited in place; the idle re-sample may never run while
        // the caret stays inside it).
        internal readonly double[] Grow = new double[Columns];
        internal int GrowColumns;
        internal TableLayout(DocumentLine start) { Start = start; Restart(); }
        internal void Restart() {
            Next = SampleEnd = Start;
            Rows = Characters = 0;
            // The idle re-sample rebuilds the whole column grid; keeping the old count
            // left the old, wider grid in effect after a table lost columns. Widths stay
            // as the interim estimate until CompleteTable publishes the new sample.
            Count = 0;
            Work = new double[Columns];
            WorkMinima = new double[Columns];
            WorkAlign = new int[Columns];
            // A fresh sample may shrink: it only starts once the caret left this table.
            Array.Clear(Grow,0,Grow.Length);
            GrowColumns=0;
            Dirty = false;
            PendingRemeasure = false;
        }
    }
    // The columns a row draws. GFM: the header row (and its delimiter row) owns the grid, so a
    // data row with more cells is cut to it and a shorter one keeps empty cells. A raw row (the
    // live caret/selection line) still reveals its own source, extra cells included, so the
    // text a user typed there never disappears while the caret is on the line.
    static int RowColumns(LineInfo info, TableLayout table, bool raw) {
        int count = raw ? info.Cells.Count : table.Count;
        if (count < 1) count = info.Cells.Count;
        return Math.Max(1, count);
    }
    internal sealed class RowLayout {
        internal double ParagraphWidth;
        internal readonly double[] Targets, Boundaries, Starts, SeparatorWidths;
        internal readonly double Padding, LeadWidth, Minimum;
        internal readonly bool Raw;
        internal readonly CellText[] Texts;
        internal readonly List<CellSlice>[] Slices;
        internal readonly int[] Align;
        internal readonly double[] Widths;
        internal int PhysicalRows, FirstColumn;
        internal readonly TableLayout Table;
        internal readonly int Version;
        internal readonly double ViewWidth;
        // The table's left inset (a table under a list item) and the width the columns were
        // fitted to: the paragraph the formatter hands the row, not just the view's width, so
        // the row never re-wraps onto a second visual line.
        internal readonly double Indent, BaseWidth;
        internal readonly string Source;
        // LineInfo.MathTouchKey the row was built for: a touched formula is drawn as source.
        internal readonly string MathKey;
        // Immutable once built, so a redraw of the same cached row reuses it instead of
        // rebuilding the O(line) source/reverse maps on every visual line construction.
        internal TableProjection Projection;
        internal RowLayout(LineInfo info, TableLayout table, Session session, bool raw) {
            Source=info.Text;MathKey=info.MathTouchKey;RowLayout previous=info.Row;
            Table=table; Version=table.Version; ViewWidth=session.View.ActualWidth;
            Indent=Math.Max(0,info.TableIndent);
            BaseWidth=Math.Max(1,session.RowWidth()-Indent);
            int count=RowColumns(info,table,raw);
            Targets=new double[count]; Boundaries=new double[count+1]; Starts=new double[count+1]; SeparatorWidths=new double[count];
            Texts=new CellText[count]; Slices=new List<CellSlice>[count]; Align=new int[count];
            Widths=TableWidths(table.Widths,table.Minima,count,BaseWidth,session.Options.WordWrap,session.Options.FontScale);
            Padding=CellPadding; Minimum=0; Raw=raw;
            Boundaries[0]=Indent;
            for (int c=0;c<count;c++) {
                Align[c]=c<TableLayout.Columns ? table.Align[c] : 0;
                Boundaries[c+1]=Boundaries[c]+Widths[c]; Starts[c+1]=Boundaries[c+1]; Targets[c]=Boundaries[c]+Padding;
                Slices[c]=new List<CellSlice>();
                if (c>=info.Cells.Count) continue;
                Cell cell=info.Cells[c];CellText oldText=null;List<CellSlice> oldSlices=null;bool appended=false;
                if (previous!=null && previous.Table==table && previous.Raw==raw && c<previous.Texts.Length) {
                    oldText=previous.Texts[c];oldSlices=previous.Slices[c];
                    if (oldText!=null && oldText.Plain && Session.PlainCell(info,cell) &&
                        oldText.Default==session.CellDefaults[info.Header ? 1 : 0]) {
                        if (oldText.SourceStart==cell.Start && cell.End-cell.Start>oldText.SourceLength &&
                            String.CompareOrdinal(oldText.Source,oldText.SourceStart,info.Text,cell.Start,oldText.SourceLength)==0) {
                            // Plain prose was appended at the cell's end: copy the previous
                            // text/map/properties and touch only the appended source chars,
                            // instead of rebuilding the whole cell on every key.
                            Texts[c]=new CellText(oldText,info,cell,cell.End-cell.Start-oldText.SourceLength);
                            appended=true;
                        } else if (oldText.SourceLength==cell.End-cell.Start &&
                            String.CompareOrdinal(oldText.Source,oldText.SourceStart,info.Text,cell.Start,oldText.SourceLength)==0) {
                            Texts[c]=oldText.SourceStart==cell.Start ? oldText : new CellText(oldText,info,cell);
                            if (previous.Widths[c]==Widths[c] || oldSlices.Count==1 && oldSlices[0].Width<=Widths[c]-Padding*2-1) {
                                Slices[c]=oldSlices;PhysicalRows=Math.Max(PhysicalRows,oldSlices.Count);continue;
                            }
                        }
                    }
                }
                if (Texts[c]==null) Texts[c]=new CellText(info,cell,session,raw,Math.Max(1,Widths[c]-Padding*2-1));
                // Appending plain prose changes only the last wrap slice. Retain the
                // already broken prefix rather than shaping 1..N syllables on each key.
                // The prefix is only valid at the width it was shaped for: a resize, or a
                // column-width publish, while this line was off screen leaves `previous`
                // with other widths, so the append case must fall back to a full format
                // instead of keeping the old wrap points.
                bool append=oldText!=null && previous.Widths[c]==Widths[c] && previous.Version==Version &&
                    previous.ViewWidth==ViewWidth &&
                    (appended || oldText.Plain && Texts[c].Plain && oldText.Default==Texts[c].Default &&
                    Texts[c].Text.Length>oldText.Text.Length &&
                    String.CompareOrdinal(Texts[c].Text,0,oldText.Text,0,oldText.Text.Length)==0);
                Slices[c]=session.FormatCell(info,cell,Texts[c],table,Math.Max(1,Widths[c]-Padding*2-1),
                    !raw && !table.Dirty && !table.PendingRemeasure,append ? oldSlices : null);
                PhysicalRows=Math.Max(PhysicalRows,Slices[c].Count);
            }
            PhysicalRows=Math.Max(1,PhysicalRows);
        }
    }

    internal sealed class TableToken {
        internal int Start, Length, TextStart;
        internal CellText Text;
        internal double Width, Height, Baseline;
        internal bool Break, Hidden;
    }
    // A row is projected in screen-row order, with native TextCharacters (not an
    // opaque UIElement). Source maps keep interior caret/IME/undo in TextDocument.
    internal sealed class TableProjection {
        internal readonly List<TableToken> Tokens=new List<TableToken>();
        internal readonly List<int> Map=new List<int>();
        internal readonly int[] Reverse;
        // Per cell, the projection column where each physical row's text starts, in row order.
        // Visual columns are row-major (physical row -> cell), so this is the only reliable way
        // to find a cell's own slice inside a wrapped row.
        internal List<int>[] SliceColumns;
        internal readonly string Source;
        internal TableProjection(LineInfo info, RowLayout row) {
            Source=info.Text; Reverse=new int[Source.Length+1]; for(int i=0;i<Reverse.Length;i++)Reverse[i]=-1;
            // One map entry per source character plus the per-row tokens: sizing them up
            // front avoids the doubling copies on every key of a long cell.
            Map.Capacity=Source.Length+row.PhysicalRows*2+8;
            Tokens.Capacity=row.PhysicalRows*4+8;
            SliceColumns=new List<int>[row.Texts.Length];
            for (int r=0;r<row.PhysicalRows;r++) {
                double height=0, baseline=0;
                for (int c=0;c<row.Texts.Length;c++) if (r<row.Slices[c].Count) { CellSlice s=row.Slices[c][r]; height=Math.Max(height,s.Height); baseline=Math.Max(baseline,s.Baseline); }
                // The 6.5 px band stays fixed: scaling it with the font let an 8 px document pack
                // table rows below the gutter label height (settings probe, overlapping numbers).
                double pad=6.5, top=r==0 ? pad : 0, bottom=r==row.PhysicalRows-1 ? pad : 0;
                // A column without a slice in this physical row emits no element. Its width
                // folds into the next filler, so later columns keep their x. A wrapped cell
                // that is taller than its neighbours (the row being typed into) then costs
                // one filler per extra row instead of one filler plus one gap per column,
                // which is what made the per-key cost grow with the wrapped row count.
                int lastPresent=-1;
                for (int c=0;c<row.Texts.Length;c++) if (row.Texts[c]!=null && r<row.Slices[c].Count) lastPresent=c;
                // A row shorter than the grid still has to reach the table's right edge.
                bool tailMissing=false;
                for (int c=lastPresent+1;c<row.Texts.Length;c++) if (row.Texts[c]==null) { tailMissing=true; break; }
                double advance=row.Indent;
                int breakAt=-1;
                for (int c=0;c<row.Texts.Length;c++) {
                    CellText text=row.Texts[c]; bool present=text!=null && r<row.Slices[c].Count;
                    if (!present) { advance+=row.Widths[c]; continue; }
                    CellSlice slice=row.Slices[c][r];
                    int at=text.Map[slice.Start];
                    double extra=Math.Max(0,row.Widths[c]-row.Padding*2-slice.Width);
                    double left=row.Padding+(row.Align[c]==1 ? extra : row.Align[c]==2 ? extra/2 : 0);
                    // Raw rows reveal the pipe/padding source (Hidden) right AFTER this filler.
                    // Mapping the filler to the cell start put a later offset before earlier
                    // ones, so the column->offset map was not monotonic and source-order caret
                    // stepping/revalidation broke. Map it to the start of the revealed gap.
                    int fillerAt=at;
                    if (r==0 && row.Raw && c<info.Cells.Count) { int ga=c==0 ? 0 : info.Cells[c-1].End; if (info.Cells[c].Start>ga) fillerAt=ga; }
                    Add(new TableToken { Length=1, Width=advance+left, Height=height+top+bottom, Baseline=baseline+top },fillerAt);
                    advance=0;
                    if (r==0 && c<info.Cells.Count) {
                        int a=c==0 ? 0 : info.Cells[c-1].End, b=info.Cells[c].Start;
                        Hidden(a,b,row.Raw);
                    }
                    // Column where this slice's TEXT starts. A raw row puts the revealed pipe
                    // tokens between the filler and the text, so it is not simply filler+1.
                    if (SliceColumns[c]==null) SliceColumns[c]=new List<int>();
                    SliceColumns[c].Add(Map.Count);
                    // Every zero-length slice is an empty segment: the whole cell, a leading
                    // or doubled <br>, or the empty tail after a trailing <br>. Each still
                    // needs its own canonical caret stop (its physical row is already in
                    // Slices); without one the reverse-map fill below sends that source
                    // offset into the next cell/next row (or EOL) and Left/Right skip it.
                    if (slice.Length==0) {
                        Set(at,Map.Count);
                        Add(new TableToken { Length=1, Width=0 },at);
                    } else {
                        var token=new TableToken { Length=slice.Length,Text=text,TextStart=slice.Start };
                        token.Start=Map.Count; Tokens.Add(token);
                        for (int p=slice.Start;p<slice.Start+slice.Length;p++) { int offset=text.Map[p]; Set(offset,Map.Count); Map.Add(offset); }
                        // A slice ending on a cell <br> ends at the hidden tag's source
                        // position (the marker's map entry), so the caret stop before the
                        // break stays on this row and the next row's first character owns
                        // its own stop.
                        int endOffset=text.Map[slice.Start+slice.Length];
                        Set(endOffset,Map.Count);
                        // The following gap belongs to the next cell. Keep the end
                        // of this slice mapped to itself, not to that gap's source.
                        // When nothing follows on this visual row, the line break itself
                        // carries that stop instead of an extra zero-width element.
                        if (c==lastPresent && c<row.Texts.Length-1 && !tailMissing && r+1<row.PhysicalRows) breakAt=endOffset;
                        else {
                            Add(new TableToken { Length=1 },endOffset);
                        }
                    }
                    if (text!=null && r==row.Slices[c].Count-1 && c==info.Cells.Count-1) Hidden(info.Cells[c].End,Source.Length,row.Raw);
                    // Formatted neighbours share one gap run by folding it into the next
                    // filler. The last column keeps its gap so the widest row spans the table.
                    advance=Math.Max(0,row.Widths[c]-left-slice.Width);
                    if (c==row.Texts.Length-1) {
                        Add(new TableToken { Length=1, Width=advance },text.Map[slice.Start+slice.Length]);
                        advance=0;
                    }
                }
                // A row with fewer cells than the grid keeps its right edge, so the line end
                // still lands at the table's last column instead of right after its text.
                if (tailMissing && advance>0) { Add(new TableToken { Length=1, Width=advance },Source.Length); advance=0; }
                if (r+1<row.PhysicalRows) Add(new TableToken { Length=1,Break=true },breakAt>=0 ? breakAt : Source.Length);
            }
            // A rendered row must not offer a caret stop at the row's end: that offset sits
            // after the trailing '|' (outside the table), and End/Right there created a new
            // column. The element still needs a terminal column (GetRelativeOffset excludes
            // its last one), so that column carries the last CELL's end instead. A raw row
            // shows the pipes, so its end is a real source position and keeps its own stop.
            int lastStop=Source.Length;
            if (!row.Raw && info.Cells!=null && info.Cells.Count>0) lastStop=info.Cells[info.Cells.Count-1].End;
            Map.Add(lastStop);
            Set(lastStop,Map.Count-1);
            // Hidden inline syntax maps to the next visible character in its own cell.
            int next=Map.Count-1;
            for (int p=Reverse.Length-1;p>=0;p--) { if (Reverse[p]<0) Reverse[p]=next; else next=Reverse[p]; }
            // A cell's padding -- the spaces between its text and the following pipe -- is that
            // cell's own: a caret stop inside it resolves to the cell's end column instead of the
            // neighbour's first column or a column past the pipe, so a space typed at the cell's
            // end draws where the text is. A raw row draws those characters as source and keeps
            // their own stops (Hidden above already mapped them).
            if (!row.Raw && info.Cells!=null) for (int c=0;c<info.Cells.Count;c++) {
                int from=info.Cells[c].End;
                int to=c+1<info.Cells.Count ? info.Cells[c+1].Start : Source.Length;
                if (from<0 || from>=Reverse.Length) continue;
                int column=Reverse[from];
                if (column<0) continue;
                int pipe=from;
                while (pipe<to && pipe<Source.Length && Source[pipe]!='|') pipe++;
                for (int p=from+1;p<=pipe && p<Reverse.Length;p++) Reverse[p]=column;
            }
            // A soft-wrap boundary (the end of slice k is the start of slice k+1) has two caret
            // stops: the trailing column of slice k (IsAtEndOfLine) and the first column of
            // slice k+1. The offset alone must resolve to the LATTER (typing continues on the
            // next row); pointing it at slice k would put the caret on the row above, and in a
            // wrapped row that row also holds other cells.
            for (int c=0;c<row.Texts.Length;c++) {
                if (row.Texts[c]==null || SliceColumns[c]==null || row.Slices[c]==null) continue;
                List<int> columns=SliceColumns[c];List<CellSlice> slices=row.Slices[c];
                for (int r=0;r+1<slices.Count && r+1<columns.Count;r++) {
                    int boundary=slices[r].Start+slices[r].Length;
                    if (boundary!=slices[r+1].Start) continue;
                    int offset=row.Texts[c].Map[boundary];
                    if (offset<0 || offset>=Reverse.Length) continue;
                    // Slice r+1's first TEXT column comes from its own start column.
                    int column=columns[r+1];
                    // The column must still be owned by the element (a column at VisualLength
                    // itself resolves to the line length, not to an offset) and must carry this
                    // very offset in the column -> offset map.
                    if (column>=Map.Count-1 || Map[column]!=offset) continue;
                    Reverse[offset]=column;
                }
            }
        }
        void Set(int source, int column) { if(source>=0 && source<Reverse.Length && Reverse[source]<0)Reverse[source]=column; }
        void Add(TableToken token,int source) { token.Start=Map.Count; Tokens.Add(token); Map.Add(source); }
        void Hidden(int a,int b,bool raw) {
            if (!raw || b<=a) return;
            var token=new TableToken {Start=Map.Count,Length=b-a,Hidden=true}; Tokens.Add(token);
            for(int p=a;p<b;p++) { Set(p,Map.Count); Map.Add(p); }
        }
    }
    internal sealed class TableRowElement : VisualLineElement {
        readonly TableProjection projection;
        internal readonly RowLayout Row;
        internal readonly LineInfo Info;
        internal TableRowElement(LineInfo info,RowLayout row) : this(info,row,ProjectionOf(info,row)) { row.FirstColumn=projection.Reverse[0]+1; }
        static TableProjection ProjectionOf(LineInfo info,RowLayout row) {
            if (row.Projection==null) row.Projection=new TableProjection(info,row);
            return row.Projection;
        }
        TableRowElement(LineInfo info,RowLayout row,TableProjection projection) : base(projection.Map.Count-1,info.Text.Length) { this.projection=projection;Row=row;Info=info; }
        public override int GetVisualColumn(int offset) { return VisualColumn+projection.Reverse[Math.Max(0,Math.Min(DocumentLength,offset-RelativeTextOffset))]; }
        public override int GetRelativeOffset(int column) { return RelativeTextOffset+projection.Map[Math.Max(0,Math.Min(VisualLength,column-VisualColumn))]; }
        // Columns (relative to this element) inside an element-relative source offset range.
        internal int StartColumn(int relative) { return projection.Reverse[Math.Max(0,Math.Min(DocumentLength,relative))]; }
        // Source offsets, in document order, that have a caret stop on this element. The
        // visual column order is row-major, so stepping by column would walk into another
        // cell's physical row; callers step offsets and ask for the canonical column.
        internal bool HasStop(int source) {
            if (source<0 || source>DocumentLength) return false;
            int at=projection.Reverse[source];
            return at>=0 && at<=VisualLength && projection.Map[at]==source && StopsAt(projection.Source,source,CaretPositioningMode.Normal);
        }
        // The caret-stop rule TextUtilities.GetNextCaretPosition applies to every plain line:
        // never in the middle of a surrogate pair (TextUtilities.cs:331), and outside
        // EveryCodepoint never in front of a combining mark (TextUtilities.cs:361), so one
        // arrow/Backspace step keeps a base letter with its marks. The projection gives each of
        // these offsets a canonical column, so the row must refuse them here: otherwise the
        // caret stops inside a pair and Backspace/Delete leaves half of one.
        static bool StopsAt(string text,int offset,CaretPositioningMode mode) {
            if (offset<=0 || offset>=text.Length) return true;
            char before=text[offset-1], after=text[offset];
            if (char.IsSurrogatePair(before,after)) return false;
            if (mode==CaretPositioningMode.EveryCodepoint) return true;
            return !IsCombiningMark(text,offset);
        }
        static bool IsCombiningMark(string text,int index) {
            char c=text[index];
            UnicodeCategory category=char.IsHighSurrogate(c) && index+1<text.Length && char.IsLowSurrogate(text[index+1])
                ? CharUnicodeInfo.GetUnicodeCategory(text,index) : char.GetUnicodeCategory(c);
            return category==UnicodeCategory.NonSpacingMark || category==UnicodeCategory.SpacingCombiningMark || category==UnicodeCategory.EnclosingMark;
        }
        internal int NextStop(int from) {
            for (int source=Math.Max(0,from);source<=DocumentLength;source++)
                if (source>from && HasStop(source)) return source;
            return -1;
        }
        internal int PrevStop(int from) {
            for (int source=Math.Min(DocumentLength,from);source>=0;source--)
                if (source<from && HasStop(source)) return source;
            return -1;
        }
        internal int NextWordStop(int from) {
            for (int source=Math.Max(0,from);source<=DocumentLength;source++) {
                if (source<=from || !HasStop(source)) continue;
                if (source==0 || source==DocumentLength ||
                    char.IsWhiteSpace(projection.Source[source-1])!=char.IsWhiteSpace(projection.Source[source])) return source;
            }
            return -1;
        }
        internal int PrevWordStop(int from) {
            for (int source=Math.Min(DocumentLength,from);source>=0;source--) {
                if (source>=from || !HasStop(source)) continue;
                if (source==0 || source==DocumentLength ||
                    char.IsWhiteSpace(projection.Source[source-1])!=char.IsWhiteSpace(projection.Source[source])) return source;
            }
            return -1;
        }
        // The cell that owns an element-relative offset. A cell's own end belongs to that cell
        // (before the next cell's '|'), and an empty cell owns its single offset, so a caret
        // between two cells never lands on the hidden separator or in the neighbour.
        internal static int CellAt(LineInfo info,int relative) {
            List<Cell> cells=info.Cells;
            if (cells==null || cells.Count==0) return -1;
            for (int c=0;c<cells.Count;c++) if (relative<=cells[c].End) return relative<cells[c].Start ? -1 : c;
            return cells.Count-1;
        }
        // Clamps an offset that sits past the row's own closing '|', which is where End used to
        // land and where typing then created a new column. The padding between the last cell's
        // text and that pipe is still INSIDE the row (a space typed there is the cell's own word
        // gap) and now has a caret stop inside the cell, so it stays; positions before the
        // leading '|' and the gaps between cells are real source positions on the caret's own row
        // (which reveals its pipes), so they are left alone; in particular the header's column 0
        // is what the "Enter above the table" rule matches on.
        internal static int Normalize(LineInfo info,int relative) {
            List<Cell> cells=info.Cells;
            if (cells==null || cells.Count==0) return relative;
            Cell last=cells[cells.Count-1];
            if (relative<=last.End) return relative;
            // The row's last pipe is the closest one at or after the cell's text; only an offset
            // past it is outside the row.
            int pipe=-1;
            for (int p=info.Text.Length-1;p>=last.End;p--) if (info.Text[p]=='|') { pipe=p;break; }
            if (pipe<0 || relative<=pipe) return relative;
            return last.End;
        }
        // True when the offset is the end of a soft-wrapped slice that continues in the same
        // cell: the caret is drawn at this row's end but typing continues on the next row.
        internal bool IsSoftWrapEnd(int relative) {
            foreach (List<CellSlice> slices in Row.Slices) {
                if (slices==null) continue;
                for (int r=0;r+1<slices.Count;r++)
                    if (slices[r].Start+slices[r].Length==relative && slices[r+1].Start==relative) return true;
            }
            return false;
        }
        // Hit only glyph tokens belonging to this cell, never the combined padding run
        // between columns. The cell is chosen from x against the column boxes before this call;
        // the physical row is clamped to the last slice this cell has, so a click below a short
        // cell (or in the padding) lands at that cell's end instead of inside a neighbour.
        internal int HitCell(VisualLine visual,RowLayout row,int cell,Point point) {
            CellText text=row.Texts[cell];
            if (text==null || text.Text.Length==0) return text==null ? 0 : text.Map[0];
            // A row that re-wrapped has one text line per slice no longer; the row-major column
            // map cannot name a physical row, so the cell's start is the only safe answer.
            if (!RowAligned(visual,row)) return text.Map[0];
            List<CellSlice> slices=row.Slices[cell];
            if (slices==null || slices.Count==0) return text.Map[0];
            // The slice of this cell under the pointer; below the cell's own rows use its last.
            // Each text line's own bottom edge (document coordinates, like point.Y): the earlier
            // loop added only the FIRST line's height to the first line's top for every row, so a
            // press below row 0 never matched and always resolved to the cell's last row.
            int slice=0;
            for (int r=0;r<visual.TextLines.Count && r<slices.Count;r++) {
                slice=r;
                if (point.Y<visual.GetTextLineVisualYPosition(visual.TextLines[r],VisualYPosition.LineBottom)) break;
            }
            CellSlice part=slices[slice];
            // The text line that draws this slice: the slice's first column is the filler, so
            // its text starts one column later and that column names the physical row.
            int column=SliceColumn(cell,slice);
            TextLine own=visual.GetTextLine(Math.Max(0,column));
            // Character indices of a TextLine are paragraph-absolute here (the visual line is
            // formatted as one paragraph), so the slice's own index is hit minus its start.
            int hit=visual.GetVisualColumn(own,Math.Max(point.X,0),false);
            int within=hit-column;
            int at=part.Start+Math.Max(0,Math.Min(part.Length,within));
            // A cell <br> ends its visual row. The click right of the row's text lands on
            // the following column, so resolve to the hidden tag itself (still this cell).
            if (at>part.Start && text.IsBreak(at-1)) at--;
            return text.Map[Math.Min(at,text.Map.Length-1)];
        }
        // True when the formatted row paragraph still has one text line per physical row. A row
        // that re-wrapped (wider than the paragraph) has no meaningful row-major column map; the
        // hit-test, caret and probe paths fall back to the stock mapping instead of trusting it.
        internal static bool RowAligned(VisualLine visual,RowLayout row) {
            return visual!=null && row!=null && visual.TextLines!=null && visual.TextLines.Count==row.PhysicalRows;
        }
        // Slice index of a cell-relative text position, so a vertical move stays in the cell.
        internal int SliceOf(int cell,int textIndex) {
            List<CellSlice> slices=Row.Slices[cell];
            if (slices==null || slices.Count==0) return 0;
            for (int r=0;r<slices.Count;r++)
                if (textIndex>=slices[r].Start && (textIndex<slices[r].Start+slices[r].Length || r==slices.Count-1)) return r;
            return slices.Count-1;
        }
        // Text index (in the cell's CellText) of a document offset, or -1 outside the cell.
        internal int TextIndexOf(int cell,int source) {
            CellText text=Row.Texts[cell];
            if (text==null) return -1;
            int first=text.Map[0],last=text.Map[text.Map.Length-1];
            if (source<first || source>last) return -1;
            if (source==last) return text.Text.Length;
            for (int p=0;p<text.Text.Length;p++) if (text.Map[p]==source && !text.IsBreak(p)) return p;
            for (int p=0;p<=text.Text.Length;p++) if (text.Map[p]==source) return p;
            return -1;
        }
        // X of a text index inside the cell's own slice, for Up/Down to keep the column.
        internal double CellX(VisualLine visual,int cell,int slice,int textIndex) {
            List<CellSlice> slices=Row.Slices[cell];
            if (slices==null || slices.Count==0) return 0;
            slice=Math.Max(0,Math.Min(slices.Count-1,slice));
            int column=SliceColumn(cell,slice);
            int within=Math.Max(0,Math.Min(slices[slice].Length,textIndex-slices[slice].Start));
            TextLine own=visual.GetTextLine(Math.Max(0,column));
            return visual.GetTextLineVisualXPosition(own,column+within);
        }
        // The closest text index of that cell's slice to a visual x (relative to the row).
        internal int CellIndexAt(VisualLine visual,int cell,int slice,double x) {
            List<CellSlice> slices=Row.Slices[cell];
            if (slices==null || slices.Count==0) return 0;
            slice=Math.Max(0,Math.Min(slices.Count-1,slice));
            int column=SliceColumn(cell,slice);
            TextLine own=visual.GetTextLine(Math.Max(0,column));
            int hit=visual.GetVisualColumn(own,Math.Max(x,0),false);
            int within=Math.Max(0,Math.Min(slices[slice].Length,hit-column));
            return slices[slice].Start+within;
        }
        // Absolute visual column where a cell's slice TEXT starts.
        int SliceColumn(int cell,int slice) {
            int column=VisualColumn;
            if (projection.SliceColumns[cell]!=null && slice>=0 && slice<projection.SliceColumns[cell].Count)
                column=VisualColumn+projection.SliceColumns[cell][slice];
            return Math.Min(column,VisualColumn+VisualLength);
        }
        // Absolute visual column of a cell's slice first TEXT character (probe oracle path).
        internal int SliceStartColumn(int cell,int slice) {
            if (projection.SliceColumns==null || cell<0 || cell>=projection.SliceColumns.Length) return -1;
            List<int> columns=projection.SliceColumns[cell];
            if (columns==null || slice<0 || slice>=columns.Count) return -1;
            return Math.Min(VisualColumn+columns[slice],VisualColumn+VisualLength);
        }
        // Probe-only dump: col->offset with '*' where Reverse[offset]==col (canonical), read directly.
        internal string DebugMap() {
            var sb=new System.Text.StringBuilder();
            sb.Append("elemVC=").Append(VisualColumn).Append(" visualLen=").Append(VisualLength).Append(" docLen=").Append(DocumentLength).Append(" map=");
            for(int i=0;i<projection.Map.Count;i++) { int s=projection.Map[i]; sb.Append(i).Append("->").Append(s).Append(s>=0 && s<projection.Reverse.Length && projection.Reverse[s]==i ? "*" : "").Append(' '); }
            sb.Append("tokens=");
            foreach(TableToken t in projection.Tokens) sb.Append('[').Append(t.Start).Append('+').Append(t.Length).Append(t.Break ? "B" : t.Hidden ? "H" : t.Text!=null ? "T" : "S").Append(']');
            return sb.ToString();
        }
        // Probe-only: the cell's text holds a decoded-image position (not the source text).
        internal bool CellHasImage(int cell) {
            CellText text=Row.Texts!=null && cell>=0 && cell<Row.Texts.Length ? Row.Texts[cell] : null;
            return text!=null && text.HasImage;
        }
        // Probe-only: the cell draws this many formulas as objects (0 = its source stays text).
        internal int CellFormulas(int cell) {
            CellText text=Row.Texts!=null && cell>=0 && cell<Row.Texts.Length ? Row.Texts[cell] : null;
            return text==null || text.MathVisuals==null ? 0 : text.MathVisuals.Length;
        }
        // Probe-only: width and height of the cell's first physical row's line (slice), and the cell's drawn text width.
        internal double CellSliceHeight(int cell) {
            List<CellSlice> slices=Row.Slices!=null && cell>=0 && cell<Row.Slices.Length ? Row.Slices[cell] : null;
            return slices==null || slices.Count==0 ? 0 : slices[0].Height;
        }
        internal double CellSliceWidth(int cell) {
            List<CellSlice> slices=Row.Slices!=null && cell>=0 && cell<Row.Slices.Length ? Row.Slices[cell] : null;
            return slices==null || slices.Count==0 ? 0 : slices[0].Width;
        }
        public override TextRun CreateTextRun(int column,ITextRunConstructionContext context) {
            int at=column-VisualColumn, lo=0, hi=projection.Tokens.Count-1;
            while(lo<hi) { int middle=(lo+hi+1)/2; if(projection.Tokens[middle].Start<=at)lo=middle;else hi=middle-1; }
            TableToken token=projection.Tokens[lo]; int delta=at-token.Start;
            if(token.Break)return new TextEndOfLine(1);
            if(token.Hidden)return new TextHidden(token.Length-delta);
            if(token.Text==null)return new TableSpaceRun(TextRunProperties,token.Width,token.Height,token.Baseline);
            int index=token.TextStart+delta, count=token.Length-delta;
            // A hidden cell <br> consumes its position as a line break; the formatter then
            // continues on the next text line with the following run.
            if(token.Text.IsBreak(index))return new TextEndOfLine(1);
            // The cell's image is one object position; the span behind it is hidden source.
            if(token.Text.IsImage(index))return token.Text.ImageRun(index,token.Text.Properties[index]);
            // An inline formula is one object position as well; the span behind it is hidden source.
            if(token.Text.IsMath(index))return token.Text.MathRun(index,token.Text.Properties[index]);
            TextRunProperties properties=token.Text.Properties[index]; int length=1;
            while(length<count && token.Text.Properties[index+length]==properties && !token.Text.IsBreak(index+length) && !token.Text.IsImage(index+length) && !token.Text.IsMath(index+length))length++;
            return new TextCharacters(token.Text.Text,index,length,properties);
        }
        public override int GetNextCaretPosition(int column,LogicalDirection direction,CaretPositioningMode mode) {
            // Caret order follows SOURCE order, not visual column order. A wrapped cell puts
            // its second physical row after the first row of every later cell, so stepping
            // by visual column made Backspace jump into (and delete across) other cells.
            // Each source offset has one canonical stop (Reverse); walk offsets instead.
            int rel=column-VisualColumn;
            if (direction==LogicalDirection.Forward && rel>=VisualLength) return -1;
            if (direction==LogicalDirection.Backward && rel<=0) return -1;
            // Start at the current offset itself: when the column is not the canonical stop
            // of its offset (zero-width filler next to it), the canonical stop of the SAME
            // offset lies ahead/behind in visual order and is a legal answer. Caret
            // revalidation asks GetNext(vc-1, Forward) and expects vc back; skipping the
            // same-offset stop made it pick the neighbouring offset (caret one too far).
            int from=rel<0 ? 0 : rel>VisualLength ? DocumentLength : projection.Map[rel];
            int stepOffset=direction==LogicalDirection.Forward ? 1 : -1;
            for(int source=from;source>=0 && source<=DocumentLength;source+=stepOffset) {
                int at=projection.Reverse[source];
                if (at<0 || at>VisualLength || projection.Map[at]!=source)continue;
                if (source==from && rel>=0 && rel<=VisualLength && (direction==LogicalDirection.Forward ? at<=rel : at>=rel))continue;
                if (mode==CaretPositioningMode.Normal || mode==CaretPositioningMode.EveryCodepoint || source==0 || source==DocumentLength ||
                    char.IsWhiteSpace(projection.Source[source-1])!=char.IsWhiteSpace(projection.Source[source])) {
                    // Same whole code point / grapheme rule as every non-table line: the delete
                    // and arrow paths ask this method for their next stop, so a refused offset
                    // is skipped and the key takes one code point.
                    if (StopsAt(projection.Source,source,mode)) return VisualColumn+at;
                }
            }
            return -1;
        }
    }
    internal sealed class TableSpaceRun : InvisibleRun {
        readonly double width,height,baseline;
        internal TableSpaceRun(TextRunProperties properties,double width,double height,double baseline) : base(properties,height) { this.width=width; this.height=height; this.baseline=baseline; }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) { return new TextEmbeddedObjectMetrics(width,height,baseline); }
    }

    internal sealed partial class Session : IDisposable {
        internal readonly SourceEditor Editor;
        internal readonly TextView View;
        internal Settings Options;
        /* The HideMarkers value Update last saw. Read back from Options it is invisible whenever the
           caller keeps one Settings instance and mutates it between calls (the probes do; the window
           builds a fresh one per update, but the session must not depend on that): next and Options
           are the same object by then, so "turned on" never read as a change and the one transition
           that has work to do - Alt+drag rectangles and a caret left inside a now-hidden marker -
           never ran. */
        bool markersWereHidden;
        // The drawn "[이미지: " placeholder prefix for the current UI language. Settings.Translate
        // is a method group on the host side, so every Settings carries a new delegate instance;
        // the resolved label, not the delegate identity, decides whether Update must redraw.
        string imageLabel = "[이미지: ";
        internal Palette Colors;
        internal readonly TextDocument Document;
        internal TextFormatter Formatter;
        internal TextRunProperties[] CellDefaults;
        // One space of the cell font, for the padding a raw row draws as real width.
        readonly CellSlice[] numberMetrics=new CellSlice[2];
        bool fastNumbers;
        // The mode Formatter was created with; the view picks up the window's mode after this
        // session attached, so every measurement re-checks (see EnsureFormatter).
        TextFormattingMode formatterMode;
        readonly BlockCache blocks;
        readonly Dictionary<DocumentLine, LineInfo> lines = new Dictionary<DocumentLine, LineInfo>();
        DocumentLine previousRowLine;
        RowLayout previousRow;
        readonly LinkedList<TableLayout> tables = new LinkedList<TableLayout>();
        GlyphMeasure glyphs;
        ImageCache images;
        string glyphFamily, imageBase;
        double glyphSize;
        // The width the formatter gives a line's first run (what the surface's page insets leave
        // of the view's width). A table row is sized from this, not only from View.ActualWidth,
        // so the row paragraph never spills past the paragraph width and re-wraps.
        double lastRowParagraphWidth;
        DispatcherOperation widthRedraw,formatterRedraw;
        internal double NumberWidth;
        readonly DispatcherTimer tableIdle;
        DispatcherOperation tableWork;
        BlockState changeState;
        int changeLineNumber, changeOldLineCount;
        bool changeStateKnown;
        readonly MarkerGenerator markers;
        readonly StrutGenerator struts;
        readonly LazyQuoteGenerator lazyQuotes;
        readonly FillGenerator fill;
        readonly MarkdownTransformer transformer;
        readonly LiveCodeHighlight codeColors;
        readonly BlockRenderer renderer;
        // Replaces the stock selection layer while this session is attached: a selection inside
        // one cell of a wrapped table row must stay inside that cell (LiveTableSelection.cs).
        readonly LiveTableSelectionLayer selection;
        readonly double oldHeight;
        // Alt+drag rectangle selection is switched off while markers are hidden; the editor's
        // own value comes back when the session detaches (원문 편집).
        readonly bool oldRectangularSelection;
        VisualLine[] visible = new VisualLine[0];
        int caretLine, selectionStart, selectionEnd;
        // The caret line the previous ActiveChange applied. A caret pulled back out of a hidden
        // fence line moves to the side it came from, so Up and Down both step over the blank band.
        int lastActiveLine;
        // A mouse press freezes the reveal set so the whole drag hit-tests the pre-press
        // layout; the pending change is applied once on release.
        bool frozen, pressFromMouse, scrolling;
        DispatcherTimer releaseTimer;
        bool disposed, finishingInput;
        // Link hover: the last hit-test offset is cached, so a move that stays on the same
        // offset (with the same modifier/read state) does no lookup at all. The tooltip is a
        // plain ToolTip at the mouse, shown only over a rendered line's link.
        ToolTip linkTip;
        string linkTipText;
        int hoverOffset=-1;
        bool hoverCtrl,hoverReadOnly;
        bool linkCursorSet;
        object oldLinkCursor;
        internal int LinkHoverChecks;
        // The document's link reference definitions ("[id]: url") for shortcut reference links
        // ("[label]"). A line scan fills the set; a small document scans on the spot, a large one
        // after typing settles, and every parse reuses the last set until the document changes.
        HashSet<string> referenceIds;
        object referenceIdsVersion;
        DispatcherOperation referenceScan;
        DispatcherTimer referenceIdle;
        const int PromptReferenceScanLimit = 4096;
        const int ShortcutReferenceLimit = 1048576;
        // fix13: the blank separator line a table needs is written with the first text typed on
        // it. The newline and the typed text share one document update, opened in TextEntering
        // and closed in TextEntered, so one undo takes both back. gapAbove marks the above-header
        // case, where the newline goes in after the text rather than before it.
        bool gapUpdate, gapAbove;
        string gapNewline;
        // The document the open gap update belongs to: EndUpdate must run on the same document
        // even when the editor was given another one in between (a mode switch or a tab release
        // can replace Editor.Document while this update is between Entering and Entered).
        TextDocument gapDocument;
        // Bug A (user video 16:54): text typed on the blank line directly above a "---" rule made
        // the pair a setext heading, so the typed line jumped to heading size and the rule
        // disappeared. Word and Notion keep the rule: the line break that separates the text from
        // the "---" is written with the typed text in the same update (one undo step), the way the
        // table gap above writes its blank separator. Opening/closing follows the same seam.
        bool setextGapUpdate;
        string setextGapNewline;
        TextDocument setextGapDocument;
        // A space typed on the outer edge of a hidden closing run stays OUTSIDE the pair (a space
        // right inside a closer is not right-flanking, so the pair would turn into literal text).
        // The next non-space input at that same spot takes the whitespace back inside with its own
        // character, in one document update, so a phrase typed word by word keeps its bold/size and
        // one undo still takes the move back. Arm is set by the space's own input event and turned
        // into Pending once the character is in the document; any caret move or other edit drops it.
        bool closeSpaceArm, closeSpacePending;
        int closeSpaceArmStart, closeSpaceArmEnd, closeSpaceStart, closeSpaceEnd, closeSpaceCaret;
        string closeSpaceArmMarkers, closeSpaceMarkers, closeSpaceSpaces;
        // The word a closing-edge space just merged in is still being typed. In the no-reveal
        // view the caret then sits on the pair's outer edge (both edges draw one spot), so the
        // word's next character would hit the 11-01 outer-edge rule and turn plain. This
        // one-input mark (document, version and caret) keeps that character inside the pair the
        // way it landed before that rule; a caret move or any other edit drops it.
        int closeWordCaret = -1;
        TextDocument closeWordDocument;
        ICSharpCode.AvalonEdit.Document.ITextSourceVersion closeWordVersion;
        System.Windows.Interop.HwndSource compositionSource;
        DispatcherOperation compositionWork,layoutWork,compositionReapply;
        bool compositionFrame;
        // A deferred re-apply is only valid for the layout it was queued for and the
        // composition session it belongs to, so a slow frame cannot overwrite a newer point.
        bool compositionLive;
        int compositionEpoch,layoutGeneration,compositionAppliedGeneration;
        internal long CompositionCallbacks,InputLayoutPasses;
        internal int PendingCompositionWork { get { return (compositionWork==null ? 0 : 1)+(compositionFrame ? 1 : 0); } }

        internal event Action<string> LinkActivated;
        internal bool IsDisposed { get { return disposed; } }
        // Probe-visible attach count: one per installed session, so a probe can prove that an
        // attach/dispose cycle returns every editor hook it took.
        internal static int LiveHandlerSets;
        // Counts, not cumulative work counters. The stability probe samples these after
        // every key to catch prefix-history caches or repeated Attach/subscription leaks.
        internal int[] StabilityCounts() {
            int metrics=0,metricCharacters=0,shapes=0,shapeCharacters=0,hot=0;
            foreach (TableLayout table in tables) {
                metrics+=table.Metrics.Count;metricCharacters+=table.MetricCharacters;
                shapes+=table.Shapes.Count;shapeCharacters+=table.ShapeCharacters;hot+=table.HotRows.Count;
            }
            return new[] {lines.Count,tables.Count,metrics,metricCharacters,shapes,shapeCharacters,hot,
                View.ElementGenerators.Count,View.LineTransformers.Count,View.BackgroundRenderers.Count,
                Document.LineTrackers.Count,PendingCompositionWork,LiveCompositionGeometry.BaselineCacheCount};
        }

        // Checkbox tag: the press interception toggles the stored task without moving
        // the caret, so the click never reveals the line.
        internal sealed class TaskTag {
            internal DocumentLine Line;
            internal int Column;
        }

        // Width shrink and alignment re-sampling wait for this quiet period after the
        // last edit; ordinary typing only widens columns incrementally.
        const int TableIdleMilliseconds = 500;

        internal Session(SourceEditor editor, Settings options) {
            Editor = editor;
            View = editor.TextArea.TextView;
            Options = options;
            markersWereHidden = options.HideMarkers;
            imageLabel = ImageLabelOf(options);
            attachedSessions.Add(this);
            oldRectangularSelection = Editor.Options.EnableRectangularSelection;
            Editor.Options.EnableRectangularSelection = !options.HideMarkers;
            Colors = new Palette(options.Dark, options.TextColor);
            CellDefaults=new TextRunProperties[] {new CellProperties(options,Colors,false),new CellProperties(options,Colors,true)};
            Document = editor.Document;
            formatterMode=TextOptions.GetTextFormattingMode(editor);
            Formatter = TextFormatter.Create(formatterMode);
            fastNumbers=formatterMode==TextFormattingMode.Ideal && !CultureInfo.CurrentCulture.TextInfo.IsRightToLeft;
            oldHeight = View.LineHeight;
            // NaN lets TextFormatter include enlarged runs in each row's actual height.
            // A zero-width visual strut gives the first wrapped row minimum paragraph spacing.
            View.LineHeight = double.NaN;
            tableIdle = new DispatcherTimer(DispatcherPriority.Background, View.Dispatcher);
            tableIdle.Interval = TimeSpan.FromMilliseconds(TableIdleMilliseconds);
            tableIdle.Tick += TableIdle;
            blocks = new BlockCache(Document);
            blocks.Rebuilt += DocumentRebuilt;
            glyphs = new GlyphMeasure(new FontFamily(options.Family), options.FontSize);
            glyphFamily = options.Family;
            glyphSize = options.FontSize;
            NumberWidth = MeasureNumberWidth(options);
            imageBase = options.BaseDirectory;
            images = new ImageCache(this);
            ReadActive(out caretLine, out selectionStart, out selectionEnd);
            lastActiveLine = caretLine;
            struts = new StrutGenerator(this);
            markers = new MarkerGenerator(this);
            lazyQuotes = new LazyQuoteGenerator(this);
            transformer = new MarkdownTransformer(this);
            renderer = new BlockRenderer(this);
            View.ElementGenerators.Insert(0, struts);
            View.ElementGenerators.Insert(1, lazyQuotes);
            View.ElementGenerators.Insert(2, markers);
            fill = new FillGenerator(this);
            View.ElementGenerators.Add(fill);
            View.LineTransformers.Add(transformer);
            // Stage 5: fenced code bodies get the basic view's token colours (LiveCodeHighlight.cs).
            codeColors = new LiveCodeHighlight(Document, delegate(DocumentLine line) { return blocks.Before(line.LineNumber).Fence != '\0' && blocks.After(line.LineNumber).Fence != '\0'; }, delegate { return Options.Dark; });
            View.LineTransformers.Add(codeColors);
            View.BackgroundRenderers.Add(renderer);
            selection = LiveTableSelectionLayer.Install(Editor.TextArea);
            Document.Changing += DocumentChanging;
            Document.Changed += DocumentChanged;
            View.SizeChanged += ViewSizeChanged;
            Editor.TextArea.Caret.PositionChanged += ActiveChanged;
            Editor.TextArea.SelectionChanged += ActiveChanged;
            Editor.TextArea.PreviewMouseLeftButtonDown += TextAreaPreviewMouseDown;
            Editor.TextArea.PreviewMouseLeftButtonUp += TextAreaPreviewMouseUp;
            Editor.TextArea.PreviewMouseMove += TableCaretDrag;
            Editor.TextArea.PreviewMouseMove += WordCaretDrag;
            Editor.TextArea.LostMouseCapture += TextAreaLostMouseCapture;
            Editor.TextArea.PreviewKeyDown += TextAreaKeyInput;
            Editor.TextArea.PreviewTextInput += TextAreaKeyInput;
            CommandManager.AddPreviewExecutedHandler(Editor.TextArea,HeaderLineEnd);
            CommandManager.AddPreviewExecutedHandler(Editor.TextArea,TableCaretNavigation);
            CommandManager.AddPreviewExecutedHandler(Editor.TextArea,HiddenRunLeft);
            CommandManager.AddPreviewExecutedHandler(Editor.TextArea,DocumentStartHome);
            CommandManager.AddPreviewExecutedHandler(Editor.TextArea,MultiCellDelete);
            CommandManager.AddPreviewExecutedHandler(Editor.TextArea,HiddenMarkerDelete);
            CommandManager.AddPreviewExecutedHandler(Editor.TextArea,HiddenPasteWiden);
            Editor.TextArea.TextEntering += MultiCellEntering;
            Editor.TextArea.TextEntering += HiddenSelectionEntering;
            Editor.TextArea.TextEntering += HiddenCloseTypingEntering;
            Editor.TextArea.TextEntering += HiddenCloseSpaceEntering;
            Editor.TextArea.TextEntering += TableRowEntering;
            Editor.TextArea.TextEntering += TablePipeTyping;
            Editor.TextArea.TextEntering += TableGapEntering;
            Editor.TextArea.TextEntering += HiddenFenceEntering;
            Editor.TextArea.PreviewMouseLeftButtonDown += FenceRowMouseDown;
            Editor.TextArea.TextEntering += SetextRuleEntering;
            Editor.TextArea.TextEntering += FenceEnterEntering;
            Editor.PreviewKeyDown += FenceEnterKeyDown;
            Editor.TextArea.TextEntered += TableGapEntered;
            Editor.TextArea.TextEntered += HiddenCloseSpaceEntered;
            Editor.TextArea.TextEntered += SetextRuleEntered;
            Editor.CaretGuardChangedForTable = CaretGuardChanged;
            Editor.CellClamp = ClampCommitOffset;
            Editor.CommitTextInserter = InsertCloseSpaceCommit;
            Editor.PressHandledElsewhere = PressClaimed;
            Editor.TableOffsetAt = TableOffsetAtPoint;
            AttachTableCaret();
            Editor.DocumentChanged += EditorDocumentChanged;
            View.VisualLinesChanged += VisualLinesChanged;
            Editor.TextArea.TextEntered += InputCompleted;
            TextCompositionManager.AddPreviewTextInputStartHandler(Editor.TextArea,CompositionUpdated);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(Editor.TextArea,CompositionUpdated);
            Editor.Loaded += CompositionLoaded;
            Editor.Unloaded += CompositionUnloaded;
            PresentationSource.AddSourceChangedHandler(Editor, CompositionSourceChanged);
            View.LayoutUpdated += ViewLayoutUpdated;
            View.ScrollOffsetChanged += ViewScrollOffsetChanged;
            View.PreviewMouseMove += LinkMouseMove;
            View.MouseLeave += LinkMouseLeave;
            Editor.PreviewKeyUp += LinkModifierChanged;
            HookComposition();
            LiveHandlerSets++;
        }

        void CompositionLoaded(object sender, RoutedEventArgs e) { HookComposition(); }
        void CompositionUnloaded(object sender, RoutedEventArgs e) { UnhookComposition(); }
        void CompositionSourceChanged(object sender, SourceChangedEventArgs e) { HookComposition(); }
        void HookComposition() {
            var source=PresentationSource.FromVisual(Editor.TextArea) as System.Windows.Interop.HwndSource;
            if (source==compositionSource) return;
            UnhookComposition(); compositionSource=source;
            if (source!=null) source.AddHook(CompositionMessage);
        }
        void UnhookComposition() {
            if (compositionSource!=null) compositionSource.RemoveHook(CompositionMessage);
            compositionSource=null;
        }
        IntPtr CompositionMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
            // A composition start/update belongs to the focused editor. The window still
            // receives WM_IME_* while another control owns the keyboard (the find box), and
            // acting on those would begin this editor's composition bookkeeping for text it
            // is not composing. The end message is always accepted so a stale live flag clears.
            if ((message==0x010D || message==0x010F) && !Editor.TextArea.IsKeyboardFocused && !Editor.ImeBusy) return IntPtr.Zero;
            if (message==0x010D || message==0x010F) {
                if (!compositionLive) { compositionLive=true;BumpLayoutGeneration(); }
                // Run after all HWND hooks, but before the next render. AvalonEdit's hook
                // uses character bounds, which include table padding/paragraph leading.
                FinishInputLayout();
                // Set the point from the layout that exists now, not on a later render: no
                // composition message changes the document, so no frame may follow it.
                // AvalonEdit's ImeSupport rewrites the window from character bounds on every
                // update and runs before this hook; in a table row those bounds sit several
                // pixels above the caret, and the IME paints the syllable there before any
                // dispatcher callback, so a queued correction comes too late to hide the
                // editor caret (b54 feedback 10). Re-apply on updates as well as on a start.
                if ((message==0x010D||message==0x010F) && View.VisualLinesValid && View.IsMeasureValid && View.IsArrangeValid) ApplyComposition();
                QueueComposition();
            } else if (message==0x010E) {
                compositionLive=false;
            }
            return IntPtr.Zero;
        }
        void CompositionUpdated(object sender,TextCompositionEventArgs e) { FinishInputLayout();QueueComposition(); }
        void InputCompleted(object sender,TextCompositionEventArgs e) { compositionLive=false;FinishInputLayout();QueueComposition(); }
        void BumpLayoutGeneration() { layoutGeneration++; }
        void ViewLayoutUpdated(object sender,EventArgs e) { CompositionLayoutUpdated(sender,e); }
        void ViewScrollOffsetChanged(object sender,EventArgs e) {
            // The text under a still pointer changes on a wheel scroll and no move event follows,
            // so a shown tooltip would describe the wrong link.
            hoverOffset=-1;HideLinkTip();SetLinkCursor(false);
            CompositionScrolled(sender,e);
        }
        void FinishInputLayout() {
            if (disposed || finishingInput || Editor.Document!=Document || Document.IsInUpdate) return;
            finishingInput=true;
            try {
                if (!frozen) ApplyActiveChange();
            } finally { finishingInput=false; }
            // One coalesced measure+arrange for this key at Normal priority, which the
            // dispatcher runs before the frame's Render-priority pass. b49 forced this from
            // every composition start/update/commit, so several passes per key installed
            // drawings whose arrange transforms still described the previous layout and a
            // frame showed the edited line at its old position. One pass, before the frame.
            if (layoutWork!=null) return;
            layoutWork=View.Dispatcher.BeginInvoke(DispatcherPriority.Normal,new Action(delegate {
                try { if (!disposed) SettleInputLayout(); }
                finally { layoutWork=null; }
            }));
        }
        void SettleInputLayout() {
            if (disposed || View.VisualLinesValid && View.IsMeasureValid && View.IsArrangeValid) return;
            // The document change already queued this line's measure at Normal priority,
            // earlier in the same queue. Run that pass now: the text layer's drawings and
            // their arrange transforms are installed before the frame's Render pass, so the
            // presented frame never shows the edited line at its previous position.
            View.UpdateLayout();
            if (!View.VisualLinesValid || !View.IsArrangeValid) { View.EnsureVisualLines(); View.UpdateLayout(); }
            InputLayoutPasses++;
            BumpLayoutGeneration();
        }
        int compositionFrameRetries;
        void QueueComposition() {
            if (disposed || compositionWork!=null) return;
            // One coalesced correction after native Caret.ShowInternal (Normal), not an
            // untracked Background callback for every start/update/commit/layout event.
            compositionWork=View.Dispatcher.BeginInvoke(DispatcherPriority.DataBind,new Action(delegate {
                try {
                    if (disposed) return;
                    CompositionCallbacks++;FinishInputLayout();
                    if (!compositionFrame) { compositionFrame=true;compositionFrameRetries=0;CompositionTarget.Rendering+=CompositionRendering; }
                } finally { compositionWork=null; }
            }));
        }
        void CompositionRendering(object sender,EventArgs e) {
            CompositionTarget.Rendering-=CompositionRendering;compositionFrame=false;
            if (disposed) return;
            // A frame is only presented after the pending measure/arrange ran. If that ever
            // is not the case, wait for the next frame instead of using stale geometry.
            if (!View.VisualLinesValid || !View.IsMeasureValid || !View.IsArrangeValid) {
                // Do not give up and leave the previous window in place: past the frame budget
                // the re-apply moves to the layout/scroll driven path below, which keeps
                // retrying until the composition ends.
                compositionFrameRetries++;
                if (compositionFrameRetries<=8) {
                    if (!compositionFrame) { compositionFrame=true;CompositionTarget.Rendering+=CompositionRendering; }
                } else {
                    compositionFrameRetries=0;RequestCompositionReapply();
                }
                return;
            }
            compositionFrameRetries=0;
            // ScrollViewer can adjust the offset after the DataBind pass. Use the final
            // arranged geometry on the very first presented frame, without rebuilding it.
            ApplyComposition();
        }
        // A column that grew or a cell that re-wrapped moves the caret inside the row and can
        // change the scroll extent; the deferred path must run again for that layout even when
        // no further input arrives.
        void RequestCompositionReapply() {
            if (disposed || compositionReapply!=null) return;
            if (!compositionLive && !Editor.ImeBusy) return;
            int generation=layoutGeneration;
            // Loaded runs after the pending measure/arrange; BringCaretToView first keeps a
            // widened cell on screen so the composition point stays inside the viewport.
            compositionReapply=View.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(delegate {
                compositionReapply=null;
                if (disposed) return;
                if (!compositionLive && !Editor.ImeBusy) return;
                // A newer layout already moved the point, or this one is still the one the
                // last apply used: either way this deferred pass must not overwrite it.
                if (generation!=layoutGeneration) { RequestCompositionReapply();return; }
                if (layoutGeneration==compositionAppliedGeneration) return;
                if (!View.VisualLinesValid || !View.IsMeasureValid || !View.IsArrangeValid) return;
                if (compositionSource!=null && Editor.TextArea.IsKeyboardFocused) SafeBringCaretToView(Editor.TextArea);
                ApplyComposition();
            }));
        }
        void CompositionScrolled(object sender,EventArgs e) {
            if (!compositionLive && !Editor.ImeBusy) return;
            BumpLayoutGeneration();RequestCompositionReapply();
        }
        void CompositionLayoutUpdated(object sender,EventArgs e) {
            if (!compositionLive && !Editor.ImeBusy) return;
            RequestCompositionReapply();
        }
        void ApplyComposition() {
            if (compositionSource==null || !Editor.TextArea.IsKeyboardFocused) return;
            // The native composition window and font belong to a live composition only. Outside
            // one this wrote the IMM state on every frame in which a line was laid out or the
            // caret moved (once per typed character), and each write made the IME send its own
            // WM_IME_NOTIFY back, so the correction pass kept re-posting with nothing to correct.
            if (!compositionLive && !Editor.ImeBusy) return;
            compositionAppliedGeneration=layoutGeneration;
            LiveCompositionGeometry.Apply(Editor,compositionSource);
        }

        static double MeasureNumberWidth(Settings settings) {
            // TextView clips at x=0, unlike FlowDocument's hanging marker margin.
            // Reserve a shared three-digit slot on ordered lines only, never unclip
            // the editor (which would paint scrolled text over the gutter/toolbars).
            return NumberSlot(new FontFamily(settings.Family),settings.FontSize);
        }
        readonly Dictionary<DocumentLine,CollapsedLineSection> delimiterSections=new Dictionary<DocumentLine,CollapsedLineSection>();
        // Folds the line under a table header (its delimiter row) and under a setext
        // heading (its "===" / "---" underline) until the caret enters that line.
        internal bool HideDelimiter(DocumentLine header){
            if(header.NextLine==null)return false;
            LineInfo info=Info(header);
            return (info.Header||info.Setext)&&!Raw(header.NextLine);
        }
        internal void PrepareDelimiter(DocumentLine header,bool hide){
            CollapsedLineSection section;
            if(delimiterSections.TryGetValue(header,out section)){
                if(hide&&section.IsCollapsed&&section.Start==header.NextLine&&section.End==header.NextLine)return;
                if(section.IsCollapsed)section.Uncollapse();delimiterSections.Remove(header);
            }
            if(hide)delimiterSections.Add(header,View.CollapseLines(header.NextLine,header.NextLine));
        }
        void ClearDelimiterSections(){foreach(var section in delimiterSections.Values)if(section.IsCollapsed)section.Uncollapse();delimiterSections.Clear();}
        internal bool Raw(DocumentLine line) {
            // Reading mode never reveals markup, but the caret still works normally.
            if (Options.ReadOnly || Editor.IsReadOnly) return false;
            // A display formula is one unit: all of its lines show source while the caret or a
            // selection is on any of them, in the no-reveal mode too (there is no other way to edit it).
            if (MathRender.Seen) {
                LineInfo mathInfo = Info(line);
                if (mathInfo.MathCount > 0 || mathInfo.MathRemaining > 0 || mathInfo.MathOne) return MathBlockActive(mathInfo, line) || MathBroken(mathInfo);
            }
            // The no-reveal mode (Settings.HideMarkers) keeps every line formatted in edit
            // mode too; only the 원문 편집 toggle, which detaches this session, shows source.
            // The one exception is the caret's own unclosed opening fence: that line is still
            // being typed ("```python" before Enter), so it is shown as source (EditableFence).
            if (Options.HideMarkers) return line.LineNumber == caretLine && EditableFence(line);
            int number = line.LineNumber;
            return number == caretLine || (HasSelectionLines(selectionStart, selectionEnd) && number >= selectionStart && number <= selectionEnd);
        }
        // First and last line number of the display-math block this line belongs to (a one-line
        // "$$ x $$" is a block of one line).
        internal void MathRange(LineInfo info, DocumentLine line, out int first, out int last) {
            int number = line.LineNumber;
            first = last = number;
            if (info.MathCount > 0) last = number + info.MathCount;
            else if (info.MathRemaining > 0) {
                last = number + info.MathRemaining - 1;
                if (info.MathOpener != null && !info.MathOpener.IsDeleted) first = info.MathOpener.LineNumber;
            }
        }
        static bool BlockActive(int first, int last, int caret, int start, int end) {
            return (caret >= first && caret <= last) || (HasSelectionLines(start, end) && start <= last && end >= first);
        }
        // A display formula that does not parse stays source text (the caret does not matter).
        bool MathBroken(LineInfo info) {
            if (info.MathCount <= 0 && !info.MathOne && info.MathOpener == null) return true;
            LineInfo opener = info.MathCount > 0 || info.MathOne ? info : Info(info.MathOpener);
            return !MathRender.Check(opener.MathLatex);
        }
        bool MathBlockActive(LineInfo info, DocumentLine line) {
            int first, last;
            MathRange(info, line, out first, out last);
            return BlockActive(first, last, caretLine, selectionStart, selectionEnd);
        }
        // The no-reveal mode keeps the caret's line formatted, so an inline formula shows its source
        // only while the caret touches it (inside it or at either edge) or a selection overlaps it.
        // mathTouch is the document offset of that formula's start (-1 = none), mathTouchLine its line.
        int mathTouch = -1, mathTouchLine;
        Func<double> mathAvailable;
        // The width a display formula centres itself in: the view width less the picture margin
        // (what ImageCache allows a picture).
        internal Func<double> MathAvailable {
            get {
                if (mathAvailable == null) mathAvailable = delegate { return Math.Max(0, View.ActualWidth - 24); };
                return mathAvailable;
            }
        }
        internal bool MathTouched(DocumentLine line, Replacement formula) {
            if (formula.Kind != ReplacementKind.Math || formula.Level != 0) return false;
            int start = line.Offset + formula.Start, end = start + formula.Length;
            int caret = Editor.TextArea.Caret.Offset;
            if (caret >= start && caret <= end) return true;
            if (Editor.TextArea.Selection.IsEmpty) return false;
            ISegment segment = Editor.TextArea.Selection.SurroundingSegment;
            return segment.Offset < end && segment.EndOffset > start;
        }
        // The touched formulas of a table row (see LineInfo.MathTouchKey). Only a row with a formula pays for it.
        internal string MathTouchKeyOf(DocumentLine line, LineInfo info) {
            if (!info.MathInline) return null;
            System.Text.StringBuilder key = null;
            foreach (Replacement item in info.Replacements) {
                if (item.Kind != ReplacementKind.Math || !MathTouched(line, item)) continue;
                if (key == null) key = new System.Text.StringBuilder(",");
                key.Append(item.Start.ToString(CultureInfo.InvariantCulture)).Append(',');
            }
            return key == null ? null : key.ToString();
        }
        // A display-math opener reads the lines below it (its closer, the formula source, a blank line
        // that ends the block), so an edit can change a block whose opener is above the edited line.
        // The openers within MathRender.BlockLines lines above the edit (a blank line ends the walk)
        // lose their block state and their parse, and so does every line between them and the edit.
        // Returns those lines, or null when there is no candidate: ordinary typing walks at most one
        // run of non-blank lines and tests only lines that start with '$', '`' or '~'.
        HashSet<DocumentLine> MathInvalidate(DocumentLine edited) {
            if (!MathRender.Seen || edited == null) return null;
            List<DocumentLine> path = null;
            int farthest = 0;
            DocumentLine at = edited.PreviousLine;
            for (int walked = 0; at != null && walked <= MathRender.BlockLines; walked++, at = at.PreviousLine) {
                if (at.Length == 0) break;
                if (path == null) path = new List<DocumentLine>();
                path.Add(at);
                if (at.Length > 64) continue;
                int p = 0;
                while (p < at.Length && p < 4 && Document.GetCharAt(at.Offset + p) == ' ') p++;
                if (p >= at.Length) break;
                char first = Document.GetCharAt(at.Offset + p);
                if (first != '$' && first != '`' && first != '~') continue;
                string text = ShortText(Document, at);
                char marker; int markerLength;
                if (!MathRender.IsDollarLine(text) && !MathRender.IsMathFence(text, out marker, out markerLength)) continue;
                blocks.Invalidate(at.LineNumber);
                farthest = path.Count;
            }
            if (farthest == 0) return null;
            var dirty = new HashSet<DocumentLine>();
            for (int i = 0; i < farthest; i++) { dirty.Add(path[i]); lines.Remove(path[i]); }
            return dirty;
        }
        int MathTouchAt(int caretNumber) {
            if (!MathRender.Seen) return -1;
            if (caretNumber < 1 || caretNumber > Document.LineCount) return -1;
            DocumentLine line = Document.GetLineByNumber(caretNumber);
            LineInfo info = Info(line);
            if (!info.MathInline) return -1;
            foreach (Replacement item in info.Replacements)
                if (item.Kind == ReplacementKind.Math && MathTouched(line, item)) return line.Offset + item.Start;
            return -1;
        }
        // A reference definition line is drawn at minimal height in the no-reveal mode while
        // the caret is elsewhere (the basic view never showed it); the caret line keeps the
        // grey source, and with the setting off the line keeps its ordinary grey text.
        internal bool HideReference(LineInfo info, DocumentLine line) {
            return info.RefDefinition && Options.HideMarkers &&
                !Active(line.LineNumber, caretLine, selectionStart, selectionEnd);
        }
        // Find/Replace follows the text the surface draws. With the no-reveal setting on (or a
        // read-only body, which never reveals markup) every marker run is left out of the visible
        // text and each visible character keeps the source offset it came from, so a search finds
        // "한글 검색 구간" in "한글 **검색** 구간" and maps the hit back to the source. The walk is
        // O(document) and runs only when a search asks for it; SourceSearch.cs caches the result by
        // document version and reuses it while the query changes.
        internal bool SearchHiddenMarkup { get { return Options.HideMarkers || Options.ReadOnly || Editor.IsReadOnly; } }
        object visibleVersion;
        string visibleSource;
        int[] visibleOffsets;
        bool visibleHidden;
        internal string VisibleText(out int[] sourceOffsets) {
            object version = Document.Version;
            if (visibleSource != null && Object.ReferenceEquals(visibleVersion, version) && visibleHidden == SearchHiddenMarkup) {
                sourceOffsets = visibleOffsets;
                return visibleSource;
            }
            var text = new StringBuilder(Document.TextLength);
            var offsets = new List<int>(Document.TextLength);
            foreach (DocumentLine line in Document.Lines) {
                AppendVisibleLine(line, text, offsets);
                // A line break is drawn but is not a character of the line: one marker keeps a query
                // from joining two lines, and it maps at the line's own delimiter.
                if (line.NextLine != null) { text.Append('\n'); offsets.Add(line.EndOffset); }
            }
            visibleVersion = version; visibleSource = text.ToString(); visibleOffsets = offsets.ToArray(); visibleHidden = SearchHiddenMarkup;
            sourceOffsets = visibleOffsets;
            return visibleSource;
        }
        // One line's visible characters. Replacement runs hold the markup the surface hides
        // (emphasis pairs, "# ", "> ", list prefixes, "[", "](url)", image spans, table pipes,
        // entities) and are skipped. A table row's cell gap and a <br> still separate what the eye
        // sees, so each keeps one character whose offset points at the run. Folded lines (a table
        // delimiter row, a setext underline, a hidden reference definition) draw nothing at all.
        void AppendVisibleLine(DocumentLine line, StringBuilder text, List<int> offsets) {
            LineInfo info = Info(line);
            if (info.Delimiter || info.SetextUnderline > 0 || (Options.HideMarkers && info.RefDefinition)) return;
            // A line past the parse limit keeps only its block state; its characters are all visible.
            string value = info.Text.Length == line.Length ? info.Text : Document.GetText(line);
            int at = 0;
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Start >= value.Length) break;
                int start = replacement.Start;
                if (start > at) for (int i = at; i < start; i++) { text.Append(value[i]); offsets.Add(line.Offset + i); }
                int end = Math.Min(value.Length, start + replacement.Length);
                if (end > at) at = end;
                if (replacement.Kind == ReplacementKind.Column) { text.Append(' '); offsets.Add(line.Offset + start); }
                else if (replacement.Kind == ReplacementKind.LineBreak) { text.Append('\n'); offsets.Add(line.Offset + start); }
                else if (replacement.Kind == ReplacementKind.Glyph && replacement.Label != null) {
                    for (int i = 0; i < replacement.Label.Length; i++) { text.Append(replacement.Label[i]); offsets.Add(line.Offset + start); }
                }
            }
            for (int i = at; i < value.Length; i++) { text.Append(value[i]); offsets.Add(line.Offset + i); }
        }
        // Find/Replace on the visible text. The replacement takes the formatting of the match's
        // first visible character: the marker pairs around that character stay (a closing run the
        // match covered moves behind the new text), a pair that starts inside the match goes away
        // together with the closing run it would otherwise leave behind, and an empty replacement
        // takes the pair it framed with it (the WidenEmptyPair rule; a search replace never goes
        // through the typing path). Returns false for a match that holds structure this cannot
        // rewrite safely (table pipe, image, <br>, entity, structural prefix); the caller reports
        // those and leaves the text alone.
        internal bool ReplaceVisible(int from, int to, string replacement) {
            if (from < 0 || to <= from || to > Document.TextLength) return false;
            DocumentLine line = Document.GetLineByOffset(from);
            if (to > line.EndOffset) return false;
            LineInfo info = Info(line);
            int start = from - line.Offset, end = to - line.Offset;
            if (replacement.Length==0) {
                // Empty inner pairs expose their enclosing pair in turn (**text** inside a span).
                // Expand before planning edits so the outer tags disappear in the same rewrite.
                for (int pass=0;pass<info.Replacements.Count;pass++) {
                    bool widened=false;
                    foreach (Replacement open in info.Replacements) {
                        if (open.Kind!=ReplacementKind.Hidden || open.Prefix || open.Start+open.Length!=start) continue;
                        Replacement? close=PairOf(info,open,true);
                        if (!close.HasValue || close.Value.Start!=end) continue;
                        start=open.Start;end=close.Value.Start+close.Value.Length;widened=true;break;
                    }
                    if (!widened) break;
                }
                from=line.Offset+start;to=line.Offset+end;
            }
            foreach (Replacement run in info.Replacements) {
                if (run.Length <= 0) continue;
                if (run.Start + run.Length <= start || run.Start >= end) continue;
                if (run.Kind == ReplacementKind.Hidden && !run.Prefix) continue;
                return false;
            }
            var offsets = new List<int>();
            var lengths = new List<int>();
            var texts = new List<string>();
            var kept = new List<Replacement>();
            foreach (StyledPart part in info.Styles) {
                bool holds = part.Start <= start && start < part.Start + part.Length;
                if (!holds && (part.Start < start || part.Start >= end)) continue;
                Replacement open, close;
                bool hasClose = FindMarker(info, part.Start + part.Length, out close);
                if (holds) {
                    if (replacement.Length > 0) {
                        if (hasClose && close.Start >= start && close.Start + close.Length <= end) {
                            bool seen = false;
                            foreach (Replacement item in kept) if (item.Start == close.Start && item.Length == close.Length) { seen = true; break; }
                            if (!seen) kept.Add(close);
                        }
                    } else {
                        // The span loses all of its text: the pair goes too, or "****" stays behind.
                        if (part.Start == start && FindMarkerEndingAt(info, part.Start, out open)) AddVisibleEdit(offsets, lengths, texts, line.Offset + open.Start, open.Length);
                        if (part.Start + part.Length == end && hasClose) AddVisibleEdit(offsets, lengths, texts, line.Offset + close.Start, close.Length);
                    }
                } else if (hasClose && close.Start >= end) {
                    // The pair opens inside the match and would close after it: the opening run is
                    // replaced away, so its closing run goes as well.
                    AddVisibleEdit(offsets, lengths, texts, line.Offset + close.Start, close.Length);
                }
            }
            kept.Sort(delegate(Replacement a, Replacement b) { return a.Start.CompareTo(b.Start); });
            var insert = new StringBuilder(replacement);
            foreach (Replacement run in kept) insert.Append(info.Text, run.Start, run.Length);
            int insertEdit = AddVisibleEdit(offsets, lengths, texts, from, to - from);
            texts[insertEdit] = insert.ToString();
            // Highest offset first, so every edit keeps the offsets of the earlier ones valid; the
            // match's own range is replaced last. One BeginChange makes the whole rewrite one undo.
            for (int i = 0; i < offsets.Count; i++)
                for (int j = i + 1; j < offsets.Count; j++)
                    if (offsets[j] > offsets[i]) {
                        int swapOffset = offsets[i]; offsets[i] = offsets[j]; offsets[j] = swapOffset;
                        int swapLength = lengths[i]; lengths[i] = lengths[j]; lengths[j] = swapLength;
                        string swapText = texts[i]; texts[i] = texts[j]; texts[j] = swapText;
                    }
            /* A plan that leaves the document is refused before the first edit: TextDocument.Replace
               throws out of a partial rewrite, which would leave this match half written. */
            for (int i = 0; i < offsets.Count; i++)
                if (offsets[i] < 0 || lengths[i] < 0 || offsets[i] + lengths[i] > Document.TextLength) return false;
            Editor.BeginChange();
            try { for (int i = 0; i < offsets.Count; i++) Document.Replace(offsets[i], lengths[i], texts[i]); }
            finally { Editor.EndChange(); }
            return true;
        }
        // A hidden marker run that closes a pair at this position, and one that opens a pair here.
        static bool FindMarker(LineInfo info, int at, out Replacement run) {
            foreach (Replacement candidate in info.Replacements) {
                if (candidate.Kind != ReplacementKind.Hidden || candidate.Prefix || candidate.Length <= 0) continue;
                if (candidate.Start == at) { run = candidate; return true; }
            }
            run = new Replacement(); return false;
        }
        static bool FindMarkerEndingAt(LineInfo info, int at, out Replacement run) {
            foreach (Replacement candidate in info.Replacements) {
                if (candidate.Kind != ReplacementKind.Hidden || candidate.Prefix || candidate.Length <= 0) continue;
                if (candidate.Start + candidate.Length == at) { run = candidate; return true; }
            }
            run = new Replacement(); return false;
        }
        // Returns the index of the edit for this exact range (the same range twice is one edit), so
        // the caller writing the match's text names that edit instead of assuming it was appended
        // last: a range that already had an edit would otherwise overwrite the marker removal.
        static int AddVisibleEdit(List<int> offsets, List<int> lengths, List<string> texts, int offset, int length) {
            for (int i = 0; i < offsets.Count; i++) if (offsets[i] == offset && lengths[i] == length) return i;
            offsets.Add(offset); lengths.Add(length); texts.Add("");
            return offsets.Count - 1;
        }
        // Since a TextLine always keeps the paragraph's line height, the only way to give a
        // reference definition line no height is the same fold the table delimiter row and the
        // setext underline use. A fold is only allowed under a line that can span it (the
        // element generators extend that line over the folded characters), so the visual line
        // list never walks into a collapsed line.
        readonly List<CollapsedLineSection> referenceSections = new List<CollapsedLineSection>();
        CollapsedLineSection SectionAt(DocumentLine line) {
            if (line == null) return null;
            int number = line.LineNumber;
            for (int i = referenceSections.Count - 1; i >= 0; i--) {
                CollapsedLineSection section = referenceSections[i];
                if (!section.IsCollapsed) { referenceSections.RemoveAt(i); continue; }
                if (section.Start.LineNumber <= number && number <= section.End.LineNumber) return section;
            }
            return null;
        }
        bool FoldedReference(DocumentLine line) { return SectionAt(line) != null; }
        // The fold that starts right below this line, so the line's own visual line has to
        // span its characters (MarkerGenerator does that at the line end).
        internal CollapsedLineSection ReferenceBelow(DocumentLine line) {
            if (line == null || line.NextLine == null) return null;
            foreach (CollapsedLineSection section in referenceSections)
                if (section.IsCollapsed && section.Start == line.NextLine) return section;
            return null;
        }
        // Only a line built from MarkdownGenerator may be extended: a table row, a folded
        // delimiter row or a setext heading whose underline is folded already spans the line
        // below it, so none of them can take another folded line.
        bool MergeHost(DocumentLine line) {
            LineInfo info = Info(line);
            if (info.Table || info.Delimiter || info.SetextUnderline > 0) return false;
            return !(info.Setext && HideDelimiter(line));
        }
        // The run bounds of the last fold decision, valid while the active (caret/selection)
        // state and the document are unchanged. Every visible line asks about its own run on
        // each VisualLinesChanged; without this a scroll over a long consecutive run walked
        // the whole run once per visible line.
        bool refRunValid;
        DocumentLine refRunStart, refRunEnd;
        int refRunCaret, refRunSelectionStart, refRunSelectionEnd;
        void InvalidateReferenceRun() { refRunValid = false; refRunStart = null; refRunEnd = null; }
        // Folds or unfolds one reference definition line, and the run it belongs to. Returns
        // true only when the fold state changed, so the caller redraws once.
        internal bool SyncReference(DocumentLine line) {
            if (line == null || line.IsDeleted) return false;
            LineInfo info = Info(line);
            if (!info.RefDefinition) return false;
            CollapsedLineSection section = SectionAt(line);
            if (!HideReference(info, line)) {
                if (section == null) return false;
                section.Uncollapse();
                return true;
            }
            DocumentLine start, end;
            ReferenceRun(line, out start, out end);
            DocumentLine above = start.PreviousLine;
            bool hide = above != null && !FoldedReference(above) && MergeHost(above);
            if (!hide) {
                if (section == null) return false;
                section.Uncollapse();
                return true;
            }
            if (section != null && section.IsCollapsed && section.Start == start && section.End == end) return false;
            if (section != null && section.IsCollapsed) section.Uncollapse();
            referenceSections.Add(View.CollapseLines(start, end));
            return true;
        }
        // The consecutive reference definition lines the caret is not on, up and down from
        // this line. Cached per active state; a document edit drops the cache.
        void ReferenceRun(DocumentLine line, out DocumentLine start, out DocumentLine end) {
            if (refRunValid && refRunStart != null && refRunEnd != null && !refRunStart.IsDeleted && !refRunEnd.IsDeleted &&
                refRunCaret == caretLine && refRunSelectionStart == selectionStart && refRunSelectionEnd == selectionEnd &&
                line.LineNumber >= refRunStart.LineNumber && line.LineNumber <= refRunEnd.LineNumber) {
                start = refRunStart; end = refRunEnd; return;
            }
            start = line;
            while (start.PreviousLine != null) {
                LineInfo previous = Info(start.PreviousLine);
                DiagReferenceScanLines++;
                if (!previous.RefDefinition || !HideReference(previous, start.PreviousLine)) break;
                start = start.PreviousLine;
            }
            end = line;
            while (end.NextLine != null) {
                LineInfo next = Info(end.NextLine);
                DiagReferenceScanLines++;
                if (!next.RefDefinition || !HideReference(next, end.NextLine)) break;
                end = end.NextLine;
            }
            refRunStart = start; refRunEnd = end; refRunValid = true;
            refRunCaret = caretLine; refRunSelectionStart = selectionStart; refRunSelectionEnd = selectionEnd;
        }
        void ClearReferenceSections() {
            foreach (CollapsedLineSection section in referenceSections) if (section.IsCollapsed) section.Uncollapse();
            referenceSections.Clear();
            InvalidateReferenceRun();
            ClearMathSections();
        }
        // Display math: the lines under a rendered block's opener are folded away like a table's
        // delimiter row (the opener's visual line spans them, see MarkerGenerator.FoldedBelow).
        // The fold is installed while the opener's visual line is built (StrutGenerator), released
        // whenever the caret or a selection touches the block, and before any edit that reaches it.
        readonly Dictionary<DocumentLine, CollapsedLineSection> mathSections = new Dictionary<DocumentLine, CollapsedLineSection>();
        internal void PrepareMath(DocumentLine opener, bool fold) {
            CollapsedLineSection section;
            LineInfo info = Info(opener);
            int last = opener.LineNumber + info.MathCount;
            if (mathSections.TryGetValue(opener, out section)) {
                if (fold && info.MathCount > 0 && section.IsCollapsed && section.Start == opener.NextLine && section.End.LineNumber == last) return;
                if (section.IsCollapsed) section.Uncollapse();
                mathSections.Remove(opener);
            }
            if (!fold || info.MathCount <= 0 || opener.NextLine == null || last > Document.LineCount) return;
            mathSections.Add(opener, View.CollapseLines(opener.NextLine, Document.GetLineByNumber(last)));
        }
        internal CollapsedLineSection MathBelow(DocumentLine line) {
            if (mathSections.Count == 0 || line == null || line.NextLine == null) return null;
            foreach (CollapsedLineSection section in mathSections.Values)
                if (section.IsCollapsed && section.Start == line.NextLine) return section;
            return null;
        }
        void ClearMathSections() {
            foreach (CollapsedLineSection section in mathSections.Values) if (section.IsCollapsed) section.Uncollapse();
            mathSections.Clear();
        }
        // Before an edit: every fold the edit touches (or sits next to) is released, so the height
        // tree never calls a changed line collapsed.
        void PrepareMathSections(DocumentLine first, DocumentLine last) {
            if (mathSections.Count == 0) return;
            int from = first.LineNumber, to = last.LineNumber;
            List<DocumentLine> release = null;
            foreach (KeyValuePair<DocumentLine, CollapsedLineSection> item in mathSections) {
                CollapsedLineSection section = item.Value;
                if (item.Key.IsDeleted || !section.IsCollapsed || section.Start == null || section.End == null) { if (release == null) release = new List<DocumentLine>(); release.Add(item.Key); continue; }
                int start = item.Key.LineNumber, end = section.End.LineNumber;
                if (to >= start - 1 && from <= end + 1) { if (release == null) release = new List<DocumentLine>(); release.Add(item.Key); }
            }
            if (release == null) return;
            foreach (DocumentLine opener in release) {
                CollapsedLineSection section = mathSections[opener];
                if (section.IsCollapsed) section.Uncollapse();
                mathSections.Remove(opener);
            }
        }
        // A collapse must never be installed while the visual line list is being built: a line
        // that this pass has not reached yet would then be constructed from a collapsed line.
        // Defer the sync to an idle turn and redraw once it has run.
        DispatcherOperation referenceSync;
        void QueueReferenceSync() {
            if (disposed || referenceSync != null) return;
            referenceSync = View.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                referenceSync = null;
                if (disposed || !View.VisualLinesValid) return;
                if (SyncVisibleReferences()) View.Redraw();
            }));
        }
        // The folded lines have no visual line of their own, so the visible ones around them
        // decide: each visible line, its next and its previous line are brought up to date.
        bool SyncVisibleReferences() {
            if (!View.VisualLinesValid) { QueueReferenceSync(); return false; }
            bool changed = false;
            foreach (VisualLine line in visible) {
                if (line.IsDisposed || line.FirstDocumentLine.IsDeleted) continue;
                DocumentLine at = line.FirstDocumentLine;
                if (SyncReference(at)) changed = true;
                if (SyncReference(at.NextLine)) changed = true;
                if (SyncReference(at.PreviousLine)) changed = true;
            }
            return changed;
        }
        // The resolve step for the image placeholder prefix. The delegate itself cannot be
        // compared (the host passes a new method-group instance every time), so the label it
        // produces is what Update compares: a UI-language switch is the only time it changes.
        static string ImageLabelOf(Settings settings) {
            if (settings == null) return "[이미지: ";
            Func<string,string> translate = settings.Translate;
            return translate == null ? "[이미지: " : translate("[이미지: ");
        }
        // Host settings change without reattaching; only the derived state is rebuilt.
        internal void Update(Settings next) {
            if (next == null) throw new ArgumentNullException("next");
            if (disposed) return;
            string nextImageLabel = ImageLabelOf(next);
            // The host calls this after every layout/mode change; identical settings must not
            // drop table widths or line caches (that would flash tables on each mode switch).
            if (next.FontSize == Options.FontSize && next.Family == Options.Family && next.LinePercent == Options.LinePercent &&
                next.Dark == Options.Dark && next.TextColor == Options.TextColor && next.WordWrap == Options.WordWrap &&
                string.Equals(next.BaseDirectory, Options.BaseDirectory, StringComparison.Ordinal) &&
                string.Equals(nextImageLabel, imageLabel, StringComparison.Ordinal)) {
                bool reveal = next.ReadOnly != Options.ReadOnly || next.HideMarkers != markersWereHidden;
                bool markersTurnedOn = next.HideMarkers && !markersWereHidden;
                markersWereHidden = next.HideMarkers;
                Options = next;
                ApplyHideMarkersInteraction(markersTurnedOn);
                if (reveal) { ClearReferenceSections(); View.Redraw(); }
                QueueCaretGuard();
                return;
            }
            bool font = next.Family != glyphFamily || next.FontSize != glyphSize;
            bool baseDirectory = !string.Equals(next.BaseDirectory, imageBase, StringComparison.Ordinal);
            bool markersSwitched = next.HideMarkers && !markersWereHidden;
            markersWereHidden = next.HideMarkers;
            Options = next;
            imageLabel = nextImageLabel;
            ApplyHideMarkersInteraction(markersSwitched);
            QueueCaretGuard();
            Colors = new Palette(next.Dark, next.TextColor);
            CellDefaults=new TextRunProperties[] {new CellProperties(next,Colors,false),new CellProperties(next,Colors,true)};
            Array.Clear(numberMetrics,0,numberMetrics.Length);
            if (font) {
                glyphs = new GlyphMeasure(new FontFamily(next.Family), next.FontSize);
                glyphFamily = next.Family;
                glyphSize = next.FontSize;
                NumberWidth = MeasureNumberWidth(next);
            }
            if (tableWork != null) { tableWork.Abort(); tableWork = null; }
            tableIdle.Stop();
            tables.Clear();
            lines.Clear();previousRowLine=null;previousRow=null;
            ClearReferenceSections();
            if (baseDirectory) {
                images.Dispose();
                images = new ImageCache(this);
                imageBase = next.BaseDirectory;
            }
            View.Redraw();
        }
        void ReadActive(out int caret, out int start, out int end) {
            caret = Editor.TextArea.Caret.Line;
            start = end = 0;
            if (!Editor.TextArea.Selection.IsEmpty) {
                ISegment segment = Editor.TextArea.Selection.SurroundingSegment;
                start = Document.GetLineByOffset(Math.Min(segment.Offset, Document.TextLength)).LineNumber;
                end = Document.GetLineByOffset(Math.Min(segment.EndOffset, Document.TextLength)).LineNumber;
            }
        }
        static bool Active(int number, int caret, int start, int end) {
            return number == caret || (HasSelectionLines(start, end) && number >= start && number <= end);
        }
        // selectionStart/selectionEnd are 1-based LINE numbers filled by ReadActive; 0 means the
        // selection is empty. A selection that starts at the document's first byte is line 1, so
        // the check names "there is a selection" instead of a value a reader takes for an offset.
        static bool HasSelectionLines(int start, int end) {
            return start >= 1 && end >= start;
        }
        void ActiveChanged(object sender, EventArgs e) {
            if (disposed || Editor.Document != Document) return;
            if (frozen) {
                // A missed release must not leave the frozen reveal set behind. Only a
                // real press unlocks here; BeginPress from tests has no pressed button.
                if (pressFromMouse && Mouse.LeftButton == MouseButtonState.Released) ScheduleRelease();
                return;
            }
            ApplyActiveChange();
            // Same as the layout path: only an open composition has a native point to move.
            if (compositionLive || Editor.ImeBusy) QueueComposition();
        }
        // Applies the reveal-set change exactly once. Scroll compensation keeps the
        // anchor line at the same viewport position when a reveal above it changes height.
        void ApplyActiveChange() {
            if (disposed || scrolling || Editor.Document != Document) return;
            // A caret/selection moved anywhere drops the closing-edge space: the next character may
            // only merge that space back in when it is typed exactly where the space left the
            // caret. (The typing that writes the space itself moves the caret before the state is
            // armed, so this never cancels a fresh one.)
            if (closeSpacePending && (!Editor.TextArea.Selection.IsEmpty || Editor.TextArea.Caret.Offset != closeSpaceCaret)) {
                closeSpacePending = false;
                // The toolbar read the caret before this ran (FormatAt checks the offset itself);
                // a selection that kept the caret still needs the lit style taken back.
                if (!Editor.TextArea.Selection.IsEmpty) Editor.NotifyToolsChanged();
            }
            // The merged word's continuation mark follows the same rule: a caret that leaves the
            // spot it names ends the word's inside-the-pair run.
            if (closeWordCaret >= 0 && (!Editor.TextArea.Selection.IsEmpty || Editor.TextArea.Caret.Offset != closeWordCaret)) closeWordCaret = -1;
            SnapHiddenCaret();
            // A selection change can move the moving endpoint into a row/escape without a
            // separate caret guard pass; the table rule also sees the just-made selection (H7).
            NormalizeTableCaret();
            SnapEscapedPipeCaret();
            int nextCaret, nextStart, nextEnd;
            ReadActive(out nextCaret, out nextStart, out nextEnd);
            // The direction a later fence-line snap moves towards. SnapHiddenCaret above already
            // ran with the previous value, so Up and Down step over the band on the side they came from.
            lastActiveLine = nextCaret;
            // An inline formula under the caret shows its source (no-reveal mode): a caret that moves
            // onto or off a formula on the same line has to rebuild that line.
            int nextTouch = MathTouchAt(nextCaret);
            bool touchChanged = nextTouch != mathTouch;
            int oldTouchLine = mathTouchLine;
            mathTouch = nextTouch;
            mathTouchLine = nextTouch < 0 ? 0 : nextCaret;
            if (nextCaret == caretLine && nextStart == selectionStart && nextEnd == selectionEnd && !touchChanged) return;
            int oldCaretLine = caretLine, oldStart = selectionStart, oldEnd = selectionEnd;
            caretLine = nextCaret;
            selectionStart = nextStart;
            selectionEnd = nextEnd;
            // A caret moved through the API/keyboard can target a currently collapsed delimiter.
            DocumentLine caretDocumentLine=Document.GetLineByNumber(nextCaret);
            if(Raw(caretDocumentLine)&&caretDocumentLine.PreviousLine!=null)PrepareDelimiter(caretDocumentLine.PreviousLine,false);
            // The same for a caret that lands inside a folded display formula (find, go to, undo).
            LineInfo caretMath=Info(caretDocumentLine);
            if((caretMath.MathCount>0||caretMath.MathRemaining>0)&&Raw(caretDocumentLine)){
                DocumentLine mathOpener=caretMath.MathCount>0?caretDocumentLine:caretMath.MathOpener;
                if(mathOpener!=null&&!mathOpener.IsDeleted)PrepareMath(mathOpener,false);
            }
            // Publish a deferred grid only after its active row has been left. Quiet
            // typing is not permission to rewrap the row underneath an IME window.
            if (nextCaret!=oldCaretLine) {
                foreach (TableLayout table in tables) if (table.PendingRemeasure) { tableIdle.Stop(); tableIdle.Start(); break; }
            }
            DocumentLine anchor = AnchorLine(nextCaret);
            int anchorNumber = anchor == null ? 0 : anchor.LineNumber;
            bool above = false;
            // A folded reference definition line has no visual line of its own, so the state
            // is synced through the visible lines around it; a change re-lays out the viewport.
            if (SyncVisibleReferences()) View.Redraw();
            List<VisualLine> redraw = null;
            // Iterate the viewport, never the selected document range.
            foreach (VisualLine line in visible) {
                if (line.IsDisposed || line.FirstDocumentLine.IsDeleted) continue;
                int number = line.FirstDocumentLine.LineNumber;
                bool changed=Active(number,oldCaretLine,oldStart,oldEnd)!=Active(number,nextCaret,nextStart,nextEnd);
                LineInfo first=Info(line.FirstDocumentLine);
                if (!changed && touchChanged && (number==oldTouchLine || number==mathTouchLine)) changed=true;
                if (!changed && (first.MathCount>0 || first.MathRemaining>0 || first.MathOne)) {
                    // A display formula is raw as a whole: every line of the block flips together.
                    int blockFirst, blockLast;
                    MathRange(first,line.FirstDocumentLine,out blockFirst,out blockLast);
                    changed=BlockActive(blockFirst,blockLast,oldCaretLine,oldStart,oldEnd)!=BlockActive(blockFirst,blockLast,nextCaret,nextStart,nextEnd);
                }
                if(first.Header||first.Setext){
                    changed|=Active(number+1,oldCaretLine,oldStart,oldEnd)!=Active(number+1,nextCaret,nextStart,nextEnd);
                    if(changed)PrepareDelimiter(line.FirstDocumentLine,HideDelimiter(line.FirstDocumentLine));
                }
                if(changed && first.MathCount>0)PrepareMath(line.FirstDocumentLine,!Raw(line.FirstDocumentLine));
                if(!changed)continue;
                if (redraw == null) redraw = new List<VisualLine>();
                redraw.Add(line);
                if (anchorNumber > 0 && number < anchorNumber) above = true;
            }
            if (redraw == null) return;
            double before = 0;
            if (above) before = View.GetVisualTopByDocumentLine(anchorNumber) - View.VerticalOffset;
            var changedLines=new List<DocumentLine>();
            foreach (VisualLine line in redraw) { changedLines.Add(line.FirstDocumentLine); View.Redraw(line); }
            // Resolve reveal-dependent wrapping before a caret/IME query can observe
            // the old height of a preceding line. Work stays within the changed viewport.
            foreach (DocumentLine line in changedLines) View.GetOrConstructVisualLine(line);
            if (!above) return;
            View.EnsureVisualLines();
            double after = View.GetVisualTopByDocumentLine(anchorNumber) - View.VerticalOffset;
            if (Math.Abs(after - before) <= 0.5) return;
            scrolling = true;
            try { Editor.ScrollToVerticalOffset(View.VerticalOffset + (after - before)); }
            finally { scrolling = false; }
        }
        DocumentLine AnchorLine(int caret) {
            DocumentLine first = null;
            foreach (VisualLine line in visible) {
                if (line.IsDisposed || line.FirstDocumentLine.IsDeleted) continue;
                if (line.FirstDocumentLine.LineNumber == caret) return line.FirstDocumentLine;
                if (first == null) first = line.FirstDocumentLine;
            }
            return first;
        }
        // The press is intercepted before AvalonEdit moves the caret, so the whole drag
        // hit-tests the pre-press layout instead of chasing reveal-induced layout changes.
        void TextAreaPreviewMouseDown(object sender, MouseButtonEventArgs e) {
            if (disposed || e.Handled) return;
            hoverOffset=-1;HideLinkTip();SetLinkCursor(false);
            if (TryCheckbox(e) || TryLink(e)) { e.Handled = true; return; }
            if (e.ClickCount >= 2 && TryMultiClick(e.ClickCount)) {
                if (e.ClickCount == 2) {
                    // The second press of a double click starts a word drag on the word it
                    // just selected. The reveal stays frozen until the release, so the
                    // whole drag hit-tests one layout.
                    BeginPress();
                    BeginWordPressDrag(Editor.SelectionStart, Editor.SelectionStart + Editor.SelectionLength);
                }
                e.Handled = true; return;
            }
            BeginPress();
            if (e.ClickCount == 1) {
                CacheClick(e);
                int anchor=Editor.CaretOffset==Editor.SelectionStart ? Editor.SelectionStart+Editor.SelectionLength : Editor.SelectionStart;
                if ((Keyboard.Modifiers & ~ModifierKeys.Shift)==0 && PlaceTableCaret(e.GetPosition(View),(Keyboard.Modifiers & ModifierKeys.Shift)!=0)) {
                    // The caret is placed above. The click stays consumed even when the
                    // drag cannot start, so AvalonEdit does not hit-test the padding again.
                    BeginTablePressDrag((Keyboard.Modifiers & ModifierKeys.Shift)!=0 ? anchor : Editor.CaretOffset);
                    e.Handled=true;
                }
            }
        }
        int tableDragAnchor=-1;
        // True while the drag came from a real left press. The autoscroll timer then
        // stops as soon as the button is up or the capture is gone; without it a missed
        // release would scroll and extend the selection until the next click. The
        // mouse-less probe seam BeginTableDrag leaves this off.
        bool tableDragPointerGuard;
        DispatcherTimer dragScrollTimer;Point dragScrollPoint;
        // Word-mode drag: after a double click selects a word, moving with the button held
        // extends the selection to the word under the pointer. The word is read through
        // the visible projection, so hidden markup never joins one, and a word inside a
        // table cell is clamped to that cell.
        bool wordDrag;int wordDragAnchorStart,wordDragAnchorEnd;bool wordDragPointerGuard;
        DispatcherTimer wordScrollTimer;Point wordScrollPoint;
        // Cell boxes are the renderer's boundaries, not the nearest caret stop in a
        // combined inter-cell padding run. All coordinates here are TextView-local.
        static RowLayout DrawnRow(VisualLine line) {
            foreach (VisualLineElement element in line.Elements) {
                StrutElement strut=element as StrutElement;if (strut!=null) return strut.Layout;
            }
            return null;
        }
        internal Rect TableCellBox(VisualLine line,int column) {
            RowLayout row=DrawnRow(line);
            if (row==null || column<0 || column>=row.Texts.Length || row.Texts[column]==null) return Rect.Empty;
            return new Rect(row.Boundaries[column]-View.HorizontalOffset,line.VisualTop-View.VerticalOffset,row.Widths[column],line.Height);
        }
        // H11: the horizontal span of the cell that owns the caret, in TextView-local pixels.
        // False on an ordinary line, a delimiter row, or a row the formatter re-wrapped (the
        // same cases the drawn table caret declines), so the caller keeps the stock point.
        internal bool CaretCellHorizontal(out double left,out double right) {
            left=0;right=0;
            if (disposed || !View.VisualLinesValid || Editor.Document != Document) return false;
            var caret=Editor.TextArea.Caret;
            if (caret.Line<1 || caret.Line>Document.LineCount) return false;
            DocumentLine line=Document.GetLineByNumber(caret.Line);
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0 || info.Row==null) return false;
            VisualLine visual=View.GetOrConstructVisualLine(line);
            if (visual==null) return false;
            TableRowElement element=null;
            foreach (VisualLineElement candidate in visual.Elements) { element=candidate as TableRowElement;if (element!=null) break; }
            if (element==null || !TableRowElement.RowAligned(visual,info.Row)) return false;
            int cell,textIndex;
            if (!TableCellForOffset(info,element,caret.Offset-line.Offset,out cell,out textIndex)) return false;
            Rect box=TableCellBox(visual,cell);
            if (box.IsEmpty || box.Width<=0) return false;
            left=box.Left;right=box.Right;
            return true;
        }
        // Probe oracle, independent of the caret's own visual column and of the Reverse map:
        // from a LOGICAL position (cell index, text index inside that cell, and which side of a
        // soft-wrap boundary) this picks that cell's own slice and measures x with that slice's
        // TextLine character hit. A composition point must land on this x and inside the
        // vertical span of the same TextLine.
        internal bool CellGeometry(DocumentLine line,int cell,int textIndex,bool atWrapEnd,out double x,out double rowTop,out double rowBottom) {
            x=Double.NaN;rowTop=Double.NaN;rowBottom=Double.NaN;
            if (disposed || line==null || line.IsDeleted) return false;
            LineInfo info=Info(line);
            if (!info.Table || info.Cells==null || cell<0 || cell>=info.Cells.Count || info.Row==null) return false;
            RowLayout row=info.Row;
            if (row.Texts==null || cell>=row.Texts.Length || row.Texts[cell]==null) return false;
            List<CellSlice> slices=row.Slices[cell];
            if (slices==null || slices.Count==0) return false;
            // The slice that owns the text index. At a wrap boundary the caller says whether the
            // position belongs to the ending slice or the starting one.
            int slice=-1;
            for (int r=0;r<slices.Count;r++) {
                int start=slices[r].Start, inside=slices[r].Start+slices[r].Length;
                if (textIndex>=start && textIndex<inside) { slice=r;break; }
                if (textIndex==inside) {
                    bool boundary=r+1<slices.Count && slices[r+1].Start==inside;
                    if (!boundary) { slice=r;break; }
                    slice=atWrapEnd ? r : r+1;
                    break;
                }
            }
            if (slice<0) slice=atWrapEnd ? slices.Count-1 : slices.Count-1;
            CellSlice part=slices[slice];
            VisualLine visual=View.GetOrConstructVisualLine(line);
            if (visual==null) return false;
            TableRowElement element=null;
            foreach (VisualLineElement candidate in visual.Elements) { element=candidate as TableRowElement;if (element!=null) break; }
            if (element==null) return false;
            if (!TableRowElement.RowAligned(visual,row)) return false;
            int column=element.SliceStartColumn(cell,slice);
            if (column<0) return false;
            TextLine own=visual.GetTextLine(column);
            int within=Math.Max(0,Math.Min(part.Length,textIndex-part.Start));
            x=visual.GetTextLineVisualXPosition(own,column+within)-View.HorizontalOffset;
            rowTop=visual.GetTextLineVisualYPosition(own,VisualYPosition.LineTop)-View.VerticalOffset;
            rowBottom=visual.GetTextLineVisualYPosition(own,VisualYPosition.LineBottom)-View.VerticalOffset;
            return true;
        }
        // Probe oracle: the caret-stop round trip on the element's own maps. Every canonical
        // caret stop (a column whose Map entry is the Reverse answer for its offset) must come
        // back to its own offset, so no stop is lost to another physical row of the row-major
        // projection. Every cell's start and end must be a canonical stop: those are the
        // positions the caret contract promises (empty cells included, where start == end).
        internal bool CaretRoundTrip(DocumentLine line,out string detail) {
            detail=null;
            if (disposed || line==null || line.IsDeleted) return false;
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0) return false;
            if (Raw(line)) return true;
            VisualLine visual=View.GetOrConstructVisualLine(line);
            if (visual==null) return false;
            TableRowElement element=null;
            foreach (VisualLineElement candidate in visual.Elements) { element=candidate as TableRowElement;if (element!=null) break; }
            if (element==null) return false;
            for (int column=0;column<element.VisualLength;column++) {
                int offset=element.GetRelativeOffset(column);
                if (offset<0 || offset>info.Text.Length) { detail="row="+line.LineNumber+" column "+column+" has no offset";return false; }
                if (element.StartColumn(offset)!=column) continue;
                int back=element.GetRelativeOffset(column);
                if (back!=offset) { detail="row="+line.LineNumber+" stop offset="+offset+" column="+column+" came back as "+back;return false; }
            }
            foreach (Cell cell in info.Cells) {
                foreach (int endpoint in new[]{cell.Start,cell.End}) {
                    int column=element.StartColumn(endpoint);
                    if (column<0 || column>=element.VisualLength) { detail="row="+line.LineNumber+" cell endpoint="+endpoint+" has no stop";return false; }
                }
            }
            return true;
        }
        internal bool PlaceTableCaret(Point point,bool extend) {
            int offset;
            if (!TableOffsetAtPoint(point,out offset)) return false;
            int anchor=extend ? Editor.CaretOffset==Editor.SelectionStart ? Editor.SelectionStart+Editor.SelectionLength : Editor.SelectionStart : offset;
            Editor.TextArea.Caret.Offset=offset;
            Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,anchor,offset);
            Editor.TextArea.Focus();
            // The press is consumed here, so SelectionMouseHandler's own reveal never runs; a row
            // whose bottom edge sat below the viewport left the caret off screen. Reveal it the
            // way a stock click does.
            Rect caretRect=Editor.TextArea.Caret.CalculateCaretRectangle();
            if (!caretRect.IsEmpty) { caretRect.Inflate(5,5); View.MakeVisible(caretRect); }
            return true;
        }
        // The source offset a press at this TextView-local point means inside a drawn table row, or
        // false off a table row. Shared by the click above and the IME commit-click path
        // (SourceEditor.TableOffsetAt), so both resolve the cell padding the same way.
        internal bool TableOffsetAtPoint(Point point,out int offset) {
            offset=-1;
            if (disposed || Options.Busy) return false;
            View.EnsureVisualLines();
            Point documentPoint=point+View.ScrollOffset;
            VisualLine line=View.GetVisualLineFromVisualTop(documentPoint.Y);
            if (line==null || documentPoint.Y<line.VisualTop || documentPoint.Y>=line.VisualTop+line.Height) return false;
            LineInfo info=Info(line.FirstDocumentLine);RowLayout row=DrawnRow(line);
            if (row==null || info.Cells==null || info.Cells.Count==0 || row.Boundaries==null || row.Boundaries.Length<info.Cells.Count+1) return false;
            if (documentPoint.X<row.Boundaries[0]) return false;
            // The row re-wrapped: its columns no longer name text lines, so leave the click to
            // the stock hit-test instead of placing the caret in a neighbour cell.
            if (!TableRowElement.RowAligned(line,row)) return false;
            int cell=info.Cells.Count-1;
            for (int c=0;c<info.Cells.Count;c++) if (documentPoint.X<row.Boundaries[c+1]) { cell=c;break; }
            // Right of the final column still belongs to that row's final cell.
            Cell range=info.Cells[cell];TableRowElement projection=null;
            foreach (VisualLineElement element in line.Elements) { projection=element as TableRowElement;if (projection!=null) break; }
            if (projection==null) return false;
            // The cell is chosen from x against the column boxes, and x is then clamped into
            // that cell's own box: a click in the padding between two columns can never
            // resolve into the neighbour.
            double x=Math.Min(Math.Max(documentPoint.X,row.Boundaries[cell]),Math.Max(row.Boundaries[cell],row.Boundaries[cell+1]-0.5));
            Point cellPoint=new Point(x,documentPoint.Y);
            int relative=documentPoint.X>=row.Boundaries[info.Cells.Count] ? range.End : projection.HitCell(line,row,cell,cellPoint);
            relative=Math.Max(range.Start,Math.Min(range.End,relative));
            offset=line.FirstDocumentLine.Offset+relative;
            return true;
        }
        void TableCaretDrag(object sender,MouseEventArgs e) {
            if (tableDragAnchor<0 || e.LeftButton!=MouseButtonState.Pressed) return;
            TableDragStep(e.GetPosition(View));
            e.Handled=true;
        }
        // One drag step. Outside the text viewport the capture keeps the pointer from
        // reaching AvalonEdit's own selection autoscroll, so the selection would stop at
        // the visible edge. Keep a timer scrolling while the pointer stays outside, and
        // extend to the position at the clamped edge point.
        internal void TableDragStep(Point point) {
            if (disposed || tableDragAnchor<0) return;
            // The press's capture is the only release channel. When the button or the capture is
            // already gone the drag is over: end it before the first extend, so a press whose
            // capture failed cannot reveal/scroll the view once and leave an anchor behind.
            if (tableDragPointerGuard && (Mouse.LeftButton!=MouseButtonState.Pressed || !Editor.TextArea.IsMouseCaptured)) { EndTableDrag(); return; }
            if (point.Y<0 || point.Y>View.ActualHeight) {
                dragScrollPoint=point;
                StartDragScroll();
                ExtendTableDrag(EdgePoint(point));
            } else {
                StopDragScroll();
                ExtendTableDrag(point);
            }
        }
        // The press half of a real table drag. Capture is what delivers the release back
        // to this text area; if it fails, a started drag would never end (no MouseUp, no
        // LostMouseCapture) and a started autoscroll would keep running, so stay out of
        // drag mode and only keep the caret placed by the caller.
        internal bool BeginTablePressDrag(int anchor) {
            if (!Editor.TextArea.CaptureMouse()) return false;
            tableDragAnchor=anchor;tableDragPointerGuard=true;return true;
        }
        internal bool TableDragActive { get { return tableDragAnchor>=0; } }
        internal bool WordDragActive { get { return wordDrag; } }
        // Probe seam: the drag half of a table press, without a real mouse.
        internal void BeginTableDrag(int anchor) { tableDragAnchor=anchor;tableDragPointerGuard=false; }
        // Probe seam: a pointer-guarded drag without a real capture, so the autoscroll
        // stop conditions can be exercised on a machine with no button held.
        internal void BeginTableDragWithPointerGuard(int anchor) { tableDragAnchor=anchor;tableDragPointerGuard=true; }
        internal void EndTableDrag() {
            tableDragAnchor=-1;StopDragScroll();tableDragPointerGuard=false;
            if (Editor.TextArea.IsMouseCaptured) Editor.TextArea.ReleaseMouseCapture();
        }
        void ExtendTableDrag(Point point) {
            int anchor=tableDragAnchor;if (anchor<0) return;
            if (!PlaceTableCaret(point,false)) {
                var hit=View.GetPosition(point+View.ScrollOffset);
                if (!hit.HasValue) return;
                Editor.TextArea.Caret.Offset=Document.GetOffset(hit.Value.Location);
            }
            Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,anchor,Editor.CaretOffset);
        }
        Point EdgePoint(Point point) {
            double x=point.X,y=point.Y;
            if (x<1) x=1; else if (x>View.ActualWidth-1) x=View.ActualWidth-1;
            if (y<1) y=1; else if (y>View.ActualHeight-1) y=View.ActualHeight-1;
            return new Point(x,y);
        }
        void StartDragScroll() {
            if (dragScrollTimer==null) {
                dragScrollTimer=new DispatcherTimer(DispatcherPriority.Input,View.Dispatcher);
                dragScrollTimer.Interval=TimeSpan.FromMilliseconds(50);
                dragScrollTimer.Tick+=DragScrollTick;
            }
            if (!dragScrollTimer.IsEnabled) dragScrollTimer.Start();
        }
        void StopDragScroll() { if (dragScrollTimer!=null) dragScrollTimer.Stop(); }
        void DragScrollTick(object sender,EventArgs e) {
            if (disposed || tableDragAnchor<0) { StopDragScroll(); return; }
            // A real press stops the autoscroll once the button is up or the capture is
            // gone; otherwise a release this text area never heard would keep scrolling
            // and selecting until the next click.
            if (tableDragPointerGuard && (Mouse.LeftButton!=MouseButtonState.Pressed || !Editor.TextArea.IsMouseCaptured)) { EndTableDrag(); return; }
            ScrollForDrag();
            ExtendTableDrag(EdgePoint(dragScrollPoint));
        }
        void ScrollForDrag() { ScrollForDrag(dragScrollPoint); }
        void ScrollForDrag(Point point) {
            double height=View.ActualHeight;
            double outside=point.Y<0 ? point.Y : point.Y-height;
            double line=Math.Max(1,View.DefaultLineHeight);
            // Proportional to how far outside the pointer is, capped so a long drag does
            // not jump more than a few lines per tick.
            double step=Math.Min(Math.Max(line*.5,Math.Abs(outside)*.5),line*4);
            Editor.ScrollToVerticalOffset(View.VerticalOffset+(outside<0 ? -step : step));
        }
        // The press half of a real word drag. Capture is what delivers the release back
        // to this text area; without it a started drag would never end.
        internal bool BeginWordPressDrag(int start,int end) {
            if (!Editor.TextArea.CaptureMouse()) return false;
            wordDrag=true;wordDragAnchorStart=start;wordDragAnchorEnd=end;wordDragPointerGuard=true;
            return true;
        }
        // Probe seam: the drag half without a real mouse or capture. It applies the
        // anchor selection the second press of a double click leaves behind.
        internal void BeginWordDragForProbe(int start,int end) {
            wordDrag=true;wordDragAnchorStart=start;wordDragAnchorEnd=end;wordDragPointerGuard=false;
            Editor.TextArea.Caret.Offset=end;
            Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,start,end);
        }
        void WordCaretDrag(object sender,MouseEventArgs e) {
            if (!wordDrag || e.LeftButton != MouseButtonState.Pressed) return;
            WordDragStep(e.GetPosition(View));
            e.Handled=true;
        }
        // One drag step. Outside the text viewport the capture keeps the pointer from
        // reaching AvalonEdit's own selection autoscroll, so keep a timer scrolling while
        // the pointer stays outside and extend to the clamped edge point, as table drags do.
        internal void WordDragStep(Point point) {
            if (disposed || !wordDrag) return;
            if (Editor.Document != Document) { EndWordDrag(); return; }
            if (point.Y<0 || point.Y>View.ActualHeight) {
                wordScrollPoint=point;
                StartWordScroll();
                ExtendWordDrag(EdgePoint(point));
            } else {
                StopWordScroll();
                ExtendWordDrag(point);
            }
        }
        internal void EndWordDrag() {
            wordDrag=false;wordDragPointerGuard=false;
            StopWordScroll();
            if (Editor.TextArea.IsMouseCaptured) Editor.TextArea.ReleaseMouseCapture();
        }
        void ExtendWordDrag(Point point) {
            int start,end;
            if (!WordAtPoint(point,out start,out end)) return;
            if (start>end) { int swap=start;start=end;end=swap; }
            int from=Math.Min(start,wordDragAnchorStart);
            int to=Math.Max(end,wordDragAnchorEnd);
            if (from<0) from=0;
            if (to>Document.TextLength) to=Document.TextLength;
            if (from>=to) return;
            // The caret sits on the moving end, so typing/replacing continues from the
            // same end AvalonEdit's own word drag would leave it at.
            Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,from,to);
            Editor.TextArea.Caret.Offset=start<wordDragAnchorStart ? start : to;
        }
        void StartWordScroll() {
            if (wordScrollTimer==null) {
                wordScrollTimer=new DispatcherTimer(DispatcherPriority.Input,View.Dispatcher);
                wordScrollTimer.Interval=TimeSpan.FromMilliseconds(50);
                wordScrollTimer.Tick+=WordScrollTick;
            }
            if (!wordScrollTimer.IsEnabled) wordScrollTimer.Start();
        }
        void StopWordScroll() { if (wordScrollTimer!=null&&wordScrollTimer.IsEnabled) wordScrollTimer.Stop(); }
        void WordScrollTick(object sender,EventArgs e) {
            if (disposed || !wordDrag) { StopWordScroll(); return; }
            // A real press stops the autoscroll once the button is up or the capture is
            // gone; otherwise a release this text area never heard would keep scrolling.
            if (wordDragPointerGuard && (Mouse.LeftButton!=MouseButtonState.Pressed || !Editor.TextArea.IsMouseCaptured)) { EndWordDrag(); return; }
            if (wordScrollPoint.Y>=0 && wordScrollPoint.Y<=View.ActualHeight) { StopWordScroll(); return; }
            ScrollForDrag(wordScrollPoint);
            ExtendWordDrag(EdgePoint(wordScrollPoint));
        }
        // The word under a view point, read through the visible projection: hidden markup
        // has no visual column, so a word never contains a marker. A table row is one
        // combined element whose caret walk crosses cells, so its word comes from the
        // cell's own visible text and is clamped to the cell.
        internal bool WordAtPoint(Point point,out int start,out int end) {
            start=end=0;
            if (disposed) return false;
            View.EnsureVisualLines();
            Point documentPoint=point+View.ScrollOffset;
            if (documentPoint.Y<0) documentPoint.Y=0;
            if (documentPoint.Y>View.ActualHeight) documentPoint.Y=View.ActualHeight;
            VisualLine line=View.GetVisualLineFromVisualTop(documentPoint.Y);
            if (line==null) return false;
            TableRowElement projection=null;
            foreach (VisualLineElement element in line.Elements) { projection=element as TableRowElement; if (projection!=null) break; }
            if (projection!=null) {
                LineInfo info=Info(line.FirstDocumentLine);
                RowLayout row=DrawnRow(line);
                // A wrapped cell's second physical row has no strut of its own; the row
                // layout cached on the line still describes the same cells.
                if (row==null) row=info.Row;
                if (row!=null && info.Cells!=null && info.Cells.Count>0 && CellWordAt(line,info,row,projection,documentPoint,out start,out end))
                    return true;
            }
            int column=line.GetVisualColumn(documentPoint,false);
            LineInfo markerInfo=Info(line.FirstDocumentLine);
            int baseOffset=line.FirstDocumentLine.Offset;
            int lineEnd=line.LastDocumentLine.EndOffset;
            // Stage 3a: a hidden marker owns exactly one zero-width visual column, so a hit
            // that resolves to that column would make the marker itself the "word" (an
            // invisible selection). Step to the neighbouring columns instead, right first.
            for (int attempt=0;attempt<3;attempt++) {
                int wordStart=line.GetNextCaretPosition(column+1,LogicalDirection.Backward,CaretPositioningMode.WordStartOrSymbol,false);
                if (wordStart<0) wordStart=0;
                int wordEnd=line.GetNextCaretPosition(wordStart,LogicalDirection.Forward,CaretPositioningMode.WordBorderOrSymbol,false);
                if (wordEnd<0) wordEnd=line.VisualLength;
                if (wordStart>wordEnd) { int swap=wordStart;wordStart=wordEnd;wordEnd=swap; }
                start=baseOffset+line.GetRelativeOffset(wordStart);
                end=baseOffset+line.GetRelativeOffset(wordEnd);
                if (start>lineEnd) start=lineEnd;
                if (end>lineEnd) end=lineEnd;
                if (!Options.HideMarkers || !MarkerOnly(markerInfo,start-baseOffset,end-baseOffset)) return true;
                column = attempt==0 ? column+1 : column-1;
                if (column<0) column=0;
                if (column>line.VisualLength) break;
            }
            return false;
        }
        // True when the range lies wholly inside hidden markup: one replacement, or the line's
        // leading run (several prefixes such as "> > " side by side).
        static bool MarkerOnly(LineInfo info,int start,int end) {
            if (end<=start) return false;
            if (info.LeadEnd>0 && start>=0 && end<=info.LeadEnd) return true;
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Length<=0) continue;
                if (start>=replacement.Start && end<=replacement.Start+replacement.Length) return true;
            }
            return false;
        }
        // A table row's word: the cell's visible text decides the boundary, then the
        // cell's own source map turns it back into document offsets. Never past the cell.
        bool CellWordAt(VisualLine line,LineInfo info,RowLayout row,TableRowElement projection,Point documentPoint,out int start,out int end) {
            start=end=0;
            if (documentPoint.X<row.Boundaries[0]) return false;
            // The drawn columns are the grid's (a data row's extra cells are cut); a row shorter
            // than the grid keeps its last real cell for the hit.
            int columns=row.Texts.Length;
            int cell=Math.Min(info.Cells.Count,columns)-1;
            for (int c=0;c<columns&&c<info.Cells.Count;c++) if (documentPoint.X<row.Boundaries[c+1]) { cell=c;break; }
            Cell range=info.Cells[cell];
            int relative=documentPoint.X>=row.Boundaries[columns] ? range.End : projection.HitCell(line,row,cell,documentPoint);
            relative=Math.Max(range.Start,Math.Min(range.End,relative));
            int baseOffset=line.FirstDocumentLine.Offset;
            CellText text=row.Texts[cell];
            if (text==null || text.Text.Length==0) { start=end=baseOffset+range.Start;return true; }
            // First visible character at or after the hit offset: the caret sits before it.
            // The source map is non-decreasing, so binary search keeps a long cell O(log n).
            int lo=0,hi=text.Text.Length;
            while (lo<hi) { int mid=(lo+hi)/2; if (text.Map[mid]<relative) lo=mid+1;else hi=mid; }
            int at=lo;
            int from=at,to=at;
            char left=at>0 ? text.Text[at-1] : '\0';
            char right=at<text.Text.Length ? text.Text[at] : '\0';
            if (WordBoundary.IsWordChar(left)||WordBoundary.IsWordChar(right)) {
                if (WordBoundary.IsWordChar(left)) from=at-1;
                while (from>0 && WordBoundary.IsWordChar(text.Text[from-1])) from--;
                while (to<text.Text.Length && WordBoundary.IsWordChar(text.Text[to])) to++;
            }
            start=baseOffset+text.Map[from];
            end=baseOffset+text.Map[to];
            if (start<baseOffset+range.Start) start=baseOffset+range.Start;
            if (end<start) end=start;
            if (end>baseOffset+range.End) end=baseOffset+range.End;
            return true;
        }
        // #6: the reveal follows the release at once. A double/triple click then lands on
        // the revealed layout, so the word/line under the first click is measured on the
        // formatted layout at that press and selected directly by the later clicks.
        int clickWordStart = -1, clickWordEnd, clickLineStart, clickLineEnd, clickLength, clickTicks;
        void CacheClick(MouseButtonEventArgs e) {
            clickWordStart = -1;
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            Point point = e.GetPosition(View);
            VisualLine line = View.GetVisualLineFromVisualTop(point.Y + View.ScrollOffset.Y);
            if (line == null) return;
            int start, end;
            if (!WordAtPoint(point, out start, out end)) return;
            clickWordStart = start;
            clickWordEnd = end;
            clickLineStart = line.FirstDocumentLine.Offset;
            clickLineEnd = line.LastDocumentLine.EndOffset + line.LastDocumentLine.DelimiterLength;
            clickLength = Document.TextLength;
            clickTicks = Environment.TickCount;
        }
        bool TryMultiClick(int count) {
            if (clickWordStart < 0 || Keyboard.Modifiers != ModifierKeys.None) return false;
            int limit = 2 * System.Windows.Forms.SystemInformation.DoubleClickTime;
            if (Document.TextLength != clickLength || unchecked(Environment.TickCount - clickTicks) > limit) { clickWordStart = -1; return false; }
            int start = count == 2 ? clickWordStart : clickLineStart;
            int end = Math.Min(Document.TextLength, count == 2 ? clickWordEnd : clickLineEnd);
            if (start > end) return false;
            Editor.TextArea.Caret.Offset = end;
            Editor.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, start, end);
            clickTicks = Environment.TickCount;
            if (count >= 3) clickWordStart = -1;
            return true;
        }
        // #22: an endpoint dragged to the left edge of a line whose leading markup is hidden
        // (`## `, `> `, `**`) maps after that markup; once the line reveals, the markup sat
        // outside the selection. Only the two endpoint lines are inspected.
        internal void SnapHiddenPrefixes() {
            var selection = Editor.TextArea.Selection;
            // Rectangle (Alt+drag) selections keep their segments untouched.
            if (selection.IsEmpty || selection.Segments == null || selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            int start = Editor.SelectionStart, end = start + Editor.SelectionLength;
            bool caretAtEnd = Editor.CaretOffset == end;
            bool multiline = Document.GetLineByOffset(start).LineNumber != Document.GetLineByOffset(end).LineNumber;
            int newStart = HiddenPrefixStart(start, multiline), newEnd = HiddenPrefixStart(end, multiline);
            if (newStart == start && newEnd == end) return;
            int anchor = caretAtEnd ? newStart : newEnd, active = caretAtEnd ? newEnd : newStart;
            Editor.TextArea.Caret.Offset = active;
            Editor.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, anchor, active);
        }
        // A whole-line (multi-line) selection includes the invisible prefix: a drag endpoint
        // that maps to the left edge of a "# " line must keep line.Offset, so deleting the
        // selected lines cannot leave a bare "#" behind. A single-line selection keeps the
        // old snap to the visible text.
        int HiddenPrefixStart(int offset, bool multiline) {
            DocumentLine line = Document.GetLineByOffset(offset);
            if (offset == line.Offset || Raw(line)) return offset;
            VisualLine visual = View.GetOrConstructVisualLine(line);
            if (visual == null || visual.TextLines.Count == 0) return offset;
            int column = visual.GetVisualColumn(offset - visual.FirstDocumentLine.Offset);
            if (visual.GetTextLine(column) != visual.TextLines[0]) return offset;
            double x = visual.GetVisualPosition(column, VisualYPosition.LineTop).X;
            double left = visual.GetVisualPosition(0, VisualYPosition.LineTop).X;
            if (x - left >= 0.5) return offset;
            // Stage 3a: in the no-reveal mode the visible line starts after the hidden prefix,
            // so a left-edge endpoint snaps to the text, never to the line's first column.
            if (multiline) return line.Offset;
            int lead = Options.HideMarkers ? Info(line).LeadEnd : 0;
            return lead > 0 ? line.Offset + lead : line.Offset;
        }
        void TextAreaPreviewMouseUp(object sender, MouseButtonEventArgs e) {
            if (wordDrag) EndWordDrag();
            if (tableDragAnchor>=0) { tableDragAnchor=-1;tableDragPointerGuard=false;StopDragScroll(); if (Editor.TextArea.IsMouseCaptured) Editor.TextArea.ReleaseMouseCapture(); }
            if (disposed || !frozen) return;
            ScheduleRelease();
        }
        void TextAreaLostMouseCapture(object sender, MouseEventArgs e) {
            tableDragAnchor=-1;tableDragPointerGuard=false;StopDragScroll();
            wordDrag=false;wordDragPointerGuard=false;StopWordScroll();
            if (disposed || !frozen) return;
            ScheduleRelease();
        }
        // The reveal waits one double-click interval after release: the second click of a
        // double/triple click must hit-test the same (formatted) layout as the first. Key
        // input reveals at once, and a new press keeps the layout frozen.
        void ScheduleRelease() {
            pressFromMouse = false;
            if (releaseTimer == null) {
                releaseTimer = new DispatcherTimer(DispatcherPriority.Input, View.Dispatcher);
                releaseTimer.Tick += delegate { releaseTimer.Stop(); if (!disposed && frozen) SnapHiddenPrefixes(); ResumePress(); };
            }
            // Runs right after AvalonEdit's own MouseUp selection handling (#6).
            releaseTimer.Interval = TimeSpan.FromMilliseconds(1);
            releaseTimer.Stop();
            releaseTimer.Start();
        }
        void HeaderLineEnd(object sender,ExecutedRoutedEventArgs e) {
            bool extend=e.Command==EditingCommands.SelectToLineEnd;
            bool home=e.Command==EditingCommands.MoveToLineStart||e.Command==EditingCommands.SelectToLineStart;
            if (disposed || e.Handled || !extend && !home && e.Command!=EditingCommands.MoveToLineEnd) return;
            var caret=Editor.TextArea.Caret;DocumentLine line=Document.GetLineByNumber(caret.Line);
            LineInfo info=Info(line);
            if (info.Cells==null || info.Cells.Count==0 || !info.Table && !info.Header) return;
            if (info.Delimiter) return;
            TableRowElement row=RowElement(View.GetOrConstructVisualLine(line));
            // Home/End stay inside the caret's own cell (never after the row's trailing '|'),
            // which is what AvalonEdit would otherwise use: the hidden delimiter's end, or the
            // row's last column stop, both of which sit outside the cell's text.
            int relative=caret.Offset-line.Offset;
            int lastCellEnd=info.Cells[info.Cells.Count-1].End;
            bool afterText=relative>lastCellEnd;
            int cell=TableRowElement.CellAt(info,relative);
            if (cell<0||afterText) {
                // The caret sits outside every cell's text: a revealed pipe, the padding around
                // it, or the last row's line end. Home comes back to the row's first cell and
                // End to its last one, in document order; never AvalonEdit's own line end.
                cell=home ? 0 : info.Cells.Count-1;
            }
            Cell range=info.Cells[cell];
            int target=line.Offset+(home ? range.Start : range.End);
            // H7: the slot past the row's trailing pipe is not a caret stop, also on the last
            // row. End at the last cell stays at the cell's end; Enter there leaves the table
            // (MarkdownTableCommands.EnterOutsideTable reads that same position).
            int anchor=extend ? caret.Offset==Editor.SelectionStart ? Editor.SelectionStart+Editor.SelectionLength : Editor.SelectionStart : target;
            caret.Offset=target;
            Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,anchor,target);
            SafeBringCaretToView(Editor.TextArea);e.Handled=true;
        }
        // Ctrl+Home on a document whose first line is a rendered table row put the caret at
        // offset 0, in front of the hidden leading pipe; typing there wrote outside the table
        // ("s| a | b |"). That offset is the row's start, which belongs to the first cell, so the
        // command lands on the cell's text. The offset itself stays reachable for the commands
        // that name it on purpose (Enter above the table matches on the header's column 0).
        void DocumentStartHome(object sender,ExecutedRoutedEventArgs e) {
            if (disposed || e.Handled || e.Parameter!=null) return;
            if (e.Command!=EditingCommands.MoveToDocumentStart) return;
            if (Document.LineCount<1) return;
            DocumentLine line=Document.GetLineByNumber(1);
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0) return;
            int target=line.Offset+info.Cells[0].Start;
            Editor.TextArea.Caret.Offset=target;
            Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,target,target);
            e.Handled=true;
        }
        internal TableRowElement RowElement(VisualLine line) {
            if (line==null || line.IsDisposed) return null;
            foreach (VisualLineElement element in line.Elements) {
                TableRowElement row=element as TableRowElement;
                if (row!=null) return row;
            }
            return null;
        }
        // Left/Right and Up/Down on a rendered table row. The visual columns are row-major, so
        // AvalonEdit's own movement walks into a neighbour's physical row; here every move is
        // expressed as a document offset inside the caret's cell, and only the cell boundary
        // hops to the adjacent cell. Up/Down stay on the same cell's own slice at the same x.
        void TableCaretNavigation(object sender,ExecutedRoutedEventArgs e) {
            if (disposed || e.Handled || e.Parameter!=null) return;
            ICommand command=e.Command;
            bool left=command==EditingCommands.MoveLeftByCharacter||command==EditingCommands.SelectLeftByCharacter;
            bool right=command==EditingCommands.MoveRightByCharacter||command==EditingCommands.SelectRightByCharacter;
            bool wordLeft=command==EditingCommands.MoveLeftByWord||command==EditingCommands.SelectLeftByWord;
            bool wordRight=command==EditingCommands.MoveRightByWord||command==EditingCommands.SelectRightByWord;
            bool up=command==EditingCommands.MoveUpByLine||command==EditingCommands.SelectUpByLine;
            bool down=command==EditingCommands.MoveDownByLine||command==EditingCommands.SelectDownByLine;
            if (!left && !right && !wordLeft && !wordRight && !up && !down) return;
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            // A plain arrow on a Tab-selected cell collapses to that cell's edge. The row's own
            // stops cannot do it: NextStop past the cell text was the next cell's start, so Right
            // left the cell and the next Enter/typing edited the neighbour (real input: Tab
            // selects cell m, Right, Enter -> the <br> landed in the next cell). Other lines keep
            // the stock clear-and-step, so this only runs where the caret's line is a table row.
            // Only a selection that covers exactly one cell's text is the Tab result: any other
            // selection (a drag, Shift+arrow, a selected run crossing the pipes) keeps
            // AvalonEdit's clear-and-step-once, word moves included.
            bool plainLeft=command==EditingCommands.MoveLeftByCharacter;
            bool plainRight=command==EditingCommands.MoveRightByCharacter;
            if ((plainLeft||plainRight) && !Editor.TextArea.Selection.IsEmpty &&
                Editor.TextArea.Caret.Line>=1 && Editor.TextArea.Caret.Line<=Document.LineCount) {
                DocumentLine selectedLine=Document.GetLineByNumber(Editor.TextArea.Caret.Line);
                LineInfo selectedInfo=Info(selectedLine);
                if (selectedInfo.Table && !selectedInfo.Delimiter && selectedInfo.Cells!=null && selectedInfo.Cells.Count>0) {
                    int selectedStart=Editor.SelectionStart,selectedLength=Editor.SelectionLength;
                    for (int c=0;c<selectedInfo.Cells.Count;c++) {
                        Cell candidate=selectedInfo.Cells[c];
                        if (selectedStart==selectedLine.Offset+candidate.Start && selectedLength==candidate.End-candidate.Start && selectedLength>0) {
                            int edge=plainRight ? selectedStart+selectedLength : selectedStart;
                            ExtendOrMoveTable(Editor.TextArea.Caret,edge,command);
                            e.Handled=true;
                            return;
                        }
                    }
                    // Any other selection keeps AvalonEdit's clear-and-step-once rule below, but
                    // the step is this row's own previous/next caret stop: a raw +-1 can land
                    // inside a surrogate pair, a "\|" unit or a hidden marker, and clamping it to
                    // the line made Left at the row start a no-op. ExtendOrMoveTable clears the
                    // selection once the cell-aware stop is known.
                }
            }
            var caret=Editor.TextArea.Caret;
            if (caret.Line<1 || caret.Line>Document.LineCount) return;
            DocumentLine line=Document.GetLineByNumber(caret.Line);
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0) return;
            VisualLine visual=View.GetOrConstructVisualLine(line);
            TableRowElement row=RowElement(visual);
            if (row==null || row.Info!=info) return;
            int relative=TableRowElement.Normalize(info,caret.Offset-line.Offset);
            if (relative!=caret.Offset-line.Offset) { caret.Offset=line.Offset+relative;relative=caret.Offset-line.Offset; }
            bool back=left||wordLeft||up;
            int cell=TableRowElement.CellAt(info,relative);
            if (cell<0) {
                // The caret sits on a revealed pipe or in the padding outside every cell's text
                // (an undo, a drag or a click can leave it there). Never fall through to
                // AvalonEdit's own movement: it steps the row-major visual columns and
                // hit-tests the row by x, so a wrapped row walks into another cell's physical
                // row. Move in document order instead - into the cell on the side the key
                // moves towards, then inside that cell.
                cell=GapCell(info.Cells,relative,back);
                relative=Math.Max(info.Cells[cell].Start,Math.Min(info.Cells[cell].End,relative));
            } else if (relative>info.Cells[cell].End) {
                // The only position past a cell's own text is the table's LAST row at its line
                // end (the spot the Enter rule uses there): a step starts from that cell's end,
                // never from the pipe after it (Left used to jump over the whole cell).
                relative=info.Cells[cell].End;
            }
            Cell range=info.Cells[cell];
            int target=relative;
            if (left||right||wordLeft||wordRight) {
                int step;
                if (wordLeft) step=row.PrevWordStop(relative);
                else if (wordRight) step=row.NextWordStop(relative);
                else if (left) step=row.PrevStop(relative);
                else step=row.NextStop(relative);
                if (step>=range.Start && step<=range.End) target=step;
                else if (right||wordRight) {
                    // The cell boundary hops to the next cell's start; the row's last cell end
                    // keeps the caret before the trailing '|'.
                    if (cell+1<info.Cells.Count) target=info.Cells[cell+1].Start;
                    else target=range.End;
                } else {
                    if (cell>0) target=info.Cells[cell-1].End;
                    else target=range.Start;
                }
            } else {
                List<CellSlice> slices=row.Row.Slices!=null && cell<row.Row.Slices.Length ? row.Row.Slices[cell] : null;
                if (slices==null || slices.Count==0) return;
                int index=row.TextIndexOf(cell,relative);
                int slice=index<0 ? (up ? 0 : slices.Count-1) : row.SliceOf(cell,index);
                double desiredX=Double.NaN;
                if (index>=0 && slice<slices.Count) desiredX=row.CellX(visual,cell,slice,index);
                int next=up ? slice-1 : slice+1;
                if (next<0 || next>=slices.Count) {
                    // Leave the visual line from the first/last slice only; the caret goes to
                    // the previous/next line's own table cell at the same x.
                    int lineNumber=up ? line.LineNumber-1 : line.LineNumber+1;
                    if (lineNumber<1 || lineNumber>Document.LineCount) return;
                    DocumentLine neighbour=Document.GetLineByNumber(lineNumber);
                    LineInfo neighbourInfo=Info(neighbour);
                    // The |---| line belongs to the table but is not a row: step over it to the
                    // row on the other side (the header above it, the first body row below it).
                    // Returning here let AvalonEdit move by visual line, which lands inside the
                    // revealed row's own next physical row instead of on the neighbouring row.
                    if (neighbourInfo.Delimiter) {
                        neighbour=up ? neighbour.PreviousLine : neighbour.NextLine;
                        if (neighbour==null) return;
                        neighbourInfo=Info(neighbour);
                    }
                    if (!neighbourInfo.Table || neighbourInfo.Delimiter || neighbourInfo.Cells==null || neighbourInfo.Cells.Count==0) return;
                    VisualLine neighbourVisual=View.GetOrConstructVisualLine(neighbour);
                    TableRowElement neighbourRow=RowElement(neighbourVisual);
                    if (neighbourRow==null) return;
                    int neighbourCell=Math.Min(cell,neighbourInfo.Cells.Count-1);
                    RowLayout neighbourLayout=neighbourRow.Row;
                    double x=Double.IsNaN(desiredX) ? 0 : desiredX;
                    // Same x, measured against the neighbour cell's box (columns differ by width).
                    x=x-row.Row.Boundaries[cell]+neighbourLayout.Boundaries[neighbourCell];
                    int neighbourSlice=0;
                    if (neighbourLayout.Slices[neighbourCell]!=null && neighbourLayout.Slices[neighbourCell].Count>0)
                        neighbourSlice=up ? neighbourLayout.Slices[neighbourCell].Count-1 : 0;
                    int neighbourIndex=neighbourRow.CellIndexAt(neighbourVisual,neighbourCell,neighbourSlice,x);
                    // The cell text position maps through its own map, so a slice/line index
                    // never becomes an offset in the neighbouring column.
                    CellText text=neighbourRow.Row.Texts[neighbourCell];
                    target=neighbourInfo.Cells[neighbourCell].Start;
                    if (text!=null) target=text.Map[Math.Max(0,Math.Min(text.Map.Length-1,neighbourIndex))];
                    target=Math.Max(neighbourInfo.Cells[neighbourCell].Start,Math.Min(neighbourInfo.Cells[neighbourCell].End,target));
                    ExtendOrMoveTable(caret,neighbour.Offset+target,command);
                    e.Handled=true;return;
                }
                CellSlice part=slices[next];
                int within=Math.Max(0,Math.Min(part.Length,index<0 ? 0 : index-part.Start));
                int newIndex=part.Start+within;
                if (!Double.IsNaN(desiredX)) newIndex=row.CellIndexAt(visual,cell,next,desiredX);
                CellText own=row.Row.Texts[cell];
                if (own==null) return;
                target=own.Map[Math.Max(0,Math.Min(own.Map.Length-1,newIndex))];
                target=Math.Max(range.Start,Math.Min(range.End,target));
            }
            ExtendOrMoveTable(caret,line.Offset+target,command);
            e.Handled=true;
        }
        // The cell a caret outside every cell's text belongs to when a key moves back (the cell
        // that ends before it) or forward (the cell that starts after it), both in document
        // order. A position before the first cell and after the last fall back to the edge cell.
        static int GapCell(List<Cell> cells,int relative,bool back) {
            if (back) {
                int best=-1;
                for (int c=0;c<cells.Count;c++) if (cells[c].End<=relative) best=c;
                return best<0 ? 0 : best;
            }
            for (int c=0;c<cells.Count;c++) if (cells[c].Start>=relative) return c;
            return cells.Count-1;
        }
        void ExtendOrMoveTable(ICSharpCode.AvalonEdit.Editing.Caret caret,int offset,ICommand command) {
            offset=Math.Max(0,Math.Min(offset,Document.TextLength));
            bool extend=command==EditingCommands.SelectLeftByCharacter||command==EditingCommands.SelectRightByCharacter||
                command==EditingCommands.SelectLeftByWord||command==EditingCommands.SelectRightByWord||
                command==EditingCommands.SelectUpByLine||command==EditingCommands.SelectDownByLine;
            ICSharpCode.AvalonEdit.TextViewPosition previous=caret.Position;
            bool emptySelection=Editor.TextArea.Selection.IsEmpty;
            caret.Offset=offset;
            if (extend) {
                // Same contract AvalonEdit's own Shift+arrow uses: the old caret position is the
                // fixed end, the new one becomes the moving endpoint.
                Editor.TextArea.Selection=Editor.TextArea.Selection.StartSelectionOrSetEndpoint(previous,caret.Position);
            } else if (!emptySelection) {
                Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,offset,offset);
            }
            SafeBringCaretToView(Editor.TextArea);
        }
        // C1b-overlap-2, the mirror: Left across a hidden opener/closer of the no-reveal view. An
        // inline run draws zero width, so the engine's step from the run's end column to its start
        // column moves no pixels and the press looked dead. When that step lands that way (the
        // stop is the start column of the run the caret stood at the end of), the key takes the
        // engine's step again from the run's start: that is the stop a caret already there
        // reaches, so one press still crosses one visible character. Touching runs pass whole
        // (each round follows the same zero-width rule), and a line with no earlier stop keeps the
        // engine's own step. Shift+Left extends from the same positions, so a selected range
        // follows the caret. The live view's caret line is source, so only the no-reveal view
        // takes part. The work is this line's own caret stops and elements, never a document scan.
        void HiddenRunLeft(object sender,ExecutedRoutedEventArgs e) {
            if (disposed || e.Handled || e.Parameter!=null) return;
            if (!Options.HideMarkers || Options.Busy || Editor.Document!=Document) return;
            bool extend=e.Command==EditingCommands.SelectLeftByCharacter;
            if (!extend && e.Command!=EditingCommands.MoveLeftByCharacter) return;
            var area=Editor.TextArea;
            if (area.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            var caret=area.Caret;
            if (caret.Line<1 || caret.Line>Document.LineCount) return;
            DocumentLine line=Document.GetLineByNumber(caret.Line);
            LineInfo info=Info(line);
            int relative=caret.Offset-line.Offset;
            // Only a caret standing on a hidden run's end can spend a press on its start; a plain
            // line answers from its (usually empty) marker list without touching the visual line.
            bool atRunEnd=false;
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind!=ReplacementKind.Hidden || marker.Length<=0) continue;
                if (marker.Start+marker.Length==relative) { atRunEnd=true; break; }
            }
            if (!atRunEnd) return;
            VisualLine visual=View.GetOrConstructVisualLine(line);
            if (visual==null || visual.IsDisposed) return;
            bool virtualSpace=area.Selection.EnableVirtualSpace;
            int column;
            try { column=caret.VisualColumn; }
            catch (ArgumentOutOfRangeException) { return; }
            catch (InvalidOperationException) { return; }
            int target=-1;
            bool dead=false;
            for (int round=0;round<8;round++) {
                int stop=visual.GetNextCaretPosition(column,LogicalDirection.Backward,CaretPositioningMode.Normal,virtualSpace);
                if (stop<0) break;
                target=Document.GetOffset(visual.GetTextViewPosition(stop).Location);
                // A run's start column draws the same spot as the end column the caret stood on,
                // so that step spent the press; continue from the run's start and let the next
                // round cross the visible character. Anything else is the visible stop.
                if (!EndsHiddenRunAt(visual,stop,column)) break;
                dead=true;
                column=stop;
            }
            // Every other step is AvalonEdit's own, including the cross-line one below this
            // line's stops: only a key that would have spent a press on a hidden run is handled.
            if (!dead || target<0 || target>=caret.Offset) return;
            ICSharpCode.AvalonEdit.TextViewPosition before=caret.Position;
            bool emptySelection=area.Selection.IsEmpty;
            caret.Offset=target;
            if (extend) area.Selection=area.Selection.StartSelectionOrSetEndpoint(before,caret.Position);
            else if (!emptySelection) area.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(area,target,target);
            SafeBringCaretToView(area);
            e.Handled=true;
        }
        // True when the visual column names the start column of a hidden run whose end column is
        // the column the caret stood on: the run draws zero width, so those two columns are one
        // drawn spot.
        static bool EndsHiddenRunAt(VisualLine visual,int startColumn,int endColumn) {
            if (visual==null || startColumn<0 || endColumn<=startColumn) return false;
            foreach (VisualLineElement element in visual.Elements) {
                if (!(element is InvisibleElement)) continue;
                if (element.VisualColumn!=startColumn) continue;
                if (element.VisualColumn+element.VisualLength==endColumn) return true;
            }
            return false;
        }
        // Every entry path into a rendered table row ends in a cell: End, a click right of the
        // last cell, Right at the row's last stop, an undo restoring the caret, raw -> live.
        // Only the input caret is normalized here, so a selection endpoint that spans rows keeps
        // the offsets the user created.
        bool tableCaretGuarded;
        // An edit can leave the caret at a row slot without a caret event of its own (a paste
        // before the caret, an undo, a command). One coalesced Input pass re-applies the rules.
        DispatcherOperation caretGuardWork;
        void QueueCaretGuard() {
            if (caretGuardWork != null || disposed) return;
            caretGuardWork = Editor.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(delegate {
                caretGuardWork = null;
                if (disposed || Editor.Document != Document) return;
                // Busy is only the file-open lock. Every unlock path clears it and calls LayoutMode
                // (FileOpening Prepare/Complete/Cancel) -> UpdateLivePreview -> Update, which calls
                // QueueCaretGuard again with Busy already false. Re-posting here instead kept a
                // ContextIdle callback alive for the whole open, spinning a core and starving
                // ApplicationIdle.
                if (Options.Busy) return;
                // An edit or an attach can leave the caret on a hidden marker/fence line without a
                // caret event of its own; the idle pass applies the same rules a caret move runs.
                SnapHiddenCaret();
                NormalizeTableCaret();
                SnapEscapedPipeCaret();
            }));
        }
        // Stage 3a: with Settings.HideMarkers the caret never rests inside a hidden marker.
        // Arrow keys, Home/End and clicks already stop on element borders; an offset set
        // directly (undo, search, a command) is pulled to the nearest visible boundary.
        bool hiddenCaretGuarded;
        void SnapHiddenCaret() {
            if (disposed || hiddenCaretGuarded || !Options.HideMarkers || Options.Busy) return;
            if (!Editor.TextArea.Selection.IsEmpty) return;
            var caret=Editor.TextArea.Caret;
            if (caret.Line<1 || caret.Line>Document.LineCount) return;
            DocumentLine line=Document.GetLineByNumber(caret.Line);
            LineInfo info=Info(line);
            // A fence line draws nothing but keeps the height of a text line, so a click in that
            // blank band or an arrow key landing there names an offset inside the fence's own
            // source. Typing or Backspace at that offset rewrites the fence and can end the block
            // (a closing fence that stops closing turns the rest of the document into code), so
            // the caret always moves to the neighbouring line on the side it came from. An
            // unclosed opening fence is the line being typed and keeps the caret (EditableFence).
            if (info.FenceLine && EditableFence(line)) return;
            if (info.FenceLine && !Raw(line)) {
                int target=FenceCaretTarget(line,lastActiveLine<=line.LineNumber);
                if (target==caret.Offset) return;
                hiddenCaretGuarded=true;
                try {
                    caret.Offset=target;
                    RevalidateCaretColumn(Editor.TextArea);
                } finally { hiddenCaretGuarded=false; }
                return;
            }
            int relative=caret.Offset-line.Offset;
            int snap=HiddenSnap(info,relative);
            if (snap<0) return;
            hiddenCaretGuarded=true;
            try {
                caret.Offset=line.Offset+snap;
                // The move above runs inside a caret notification (ApplyActiveChange); leave a
                // validated column behind for the caller that may still be measuring (task A).
                RevalidateCaretColumn(Editor.TextArea);
            } finally { hiddenCaretGuarded=false; }
        }
        // 11-01 user: a click on a hidden fence row started a mouse selection at the fence and the
        // caret was then moved off it (SnapHiddenCaret), so the whole fence row came out selected,
        // as if dragged. The press is taken here: the caret goes straight to the block's first
        // line (opening fence) or last line (closing fence) and no selection starts.
        void FenceRowMouseDown(object sender, MouseButtonEventArgs e) {
            if (disposed || !Options.HideMarkers || Options.Busy || e.ChangedButton != MouseButton.Left) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
            ICSharpCode.AvalonEdit.TextViewPosition? position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
            if (position == null) return;
            int number = position.Value.Line;
            if (number < 1 || number > Document.LineCount) return;
            DocumentLine line = Document.GetLineByNumber(number);
            if (Raw(line) || !Info(line).FenceLine || EditableFence(line)) return;
            DocumentLine next = line.NextLine;
            bool opener = next != null && Info(next).Code && !Info(next).FenceLine;
            int target = FenceCaretTarget(line, opener);
            e.Handled = true;
            Editor.Focus();
            Editor.TextArea.ClearSelection();
            Editor.TextArea.Caret.Offset = target;
            Editor.TextArea.Caret.BringCaretToView();
        }
        // Offset a caret on a hidden fence line moves to: the nearest line outside every fence
        // on the preferred side (forward when the caret came from above or stood still, backward
        // when it came from below), falling back to the other side at the document's edges. Code
        // body lines are ordinary caret stops; only fence marker lines are skipped, because the
        // line right after one block may be the opening fence of the next.
        int FenceCaretTarget(DocumentLine line,bool forward) {
            if (forward) {
                DocumentLine next=line.NextLine;
                while (next!=null && Info(next).FenceLine) next=next.NextLine;
                if (next!=null) return next.Offset;
            }
            DocumentLine previous=line.PreviousLine;
            while (previous!=null && Info(previous).FenceLine) previous=previous.PreviousLine;
            if (previous!=null) return previous.EndOffset;
            if (!forward) {
                DocumentLine next=line.NextLine;
                while (next!=null && Info(next).FenceLine) next=next.NextLine;
                if (next!=null) return next.Offset;
            }
            return line.Offset;
        }
        // -1 when the offset is already a visible boundary. A leading prefix only opens forward
        // (its own start is invisible), an inline marker takes the nearer of its two ends. A table
        // row's pipe/padding runs are drawn by the row projection, which gives each cell's own
        // padding a caret stop inside that cell; snapping out of them (to the neighbour's start or
        // past the pipe) is what pulled the caret back to the cell's end after a typed space, so
        // the next word landed in front of it.
        static int HiddenSnap(LineInfo info,int relative) {
            if (info==null || relative<0 || relative>info.Text.Length) return -1;
            if (info.LeadEnd>0 && relative<info.LeadEnd) return info.LeadEnd;
            bool table=info.Table && info.Cells!=null && info.Cells.Count>0;
            // A padding-only cell's own stop is one space inside its closing pipe
            // (MarkdownTableCommands.EmptyCellStop). It lies inside the column run that hides the
            // pipe and both cells' padding, so the marker snap would pull it to the cell end and
            // the next character landed before the pipe ("|  x|") instead of between the spaces.
            if (table && PaddingCellStop(info,relative+1)==relative) return -1;
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Length<=0) continue;
                if (relative<=replacement.Start || relative>=replacement.Start+replacement.Length) continue;
                if (table && !InsideTableCell(info,replacement) && InCellPadding(info,relative)) continue;
                int start=replacement.Start,end=replacement.Start+replacement.Length;
                return relative-start<=end-relative ? start : end;
            }
            return -1;
        }
        // True when the whole run lies inside one cell's text: only a cell's own markup takes the
        // caret back to a visible character. The row's pipes and the padding around them are the
        // row's own stops, and a run between two cells belongs to neither cell.
        static bool InsideTableCell(LineInfo info,Replacement replacement) {
            int end=replacement.Start+replacement.Length;
            foreach (Cell cell in info.Cells) if (replacement.Start>=cell.Start && end<=cell.End) return true;
            return false;
        }
        // True when the offset is exactly the end of one of the row's cells: typing there is
        // text appended to the cell, not markup continuing a closing run (H10).
        static bool AtCellEnd(LineInfo info,int relative) {
            if (info.Cells==null) return false;
            foreach (Cell cell in info.Cells) if (relative==cell.End) return true;
            return false;
        }
        // True when the offset sits in a cell's own trailing padding: after that cell's text,
        // before its following pipe, with nothing but spaces/tabs between. That run is the cell's
        // word gap (a space typed at the cell's end), so the caret stays; a pipe or the padding
        // on the next cell's side still snaps to a visible character. Keeping the caret out of
        // the other pipe/padding positions matters: their columns are fillers, not caret stops,
        // and validation could walk a folded header's caret into its collapsed delimiter row.
        static bool InCellPadding(LineInfo info,int relative) {
            foreach (Cell cell in info.Cells) {
                if (relative<=cell.End) continue;
                int p=cell.End;
                while (p<info.Text.Length && (info.Text[p]==' ' || info.Text[p]=='\t')) p++;
                if (p==cell.End) continue;
                if (p<info.Text.Length && info.Text[p]=='|') { if (relative<p) return true; continue; }
                // The last cell's padding runs to the line's end (the row has no closing pipe).
                if (p==info.Text.Length) return true;
            }
            return false;
        }
        // The caret stop a padding-only cell that ends at `end` owns: one space in from the pipe
        // that closes it, the renderer's copy of MarkdownTableCommands.EmptyCellStop. -1 when no
        // padding-only cell ends there, and for a cell narrower than two padding characters (a
        // "| |" or "||" cell keeps its end, exactly like the command's stop). A cell a word delete
        // emptied (Ctrl+Backspace on the last cell's word) is padding-only too, so the caret the
        // delete leaves at the cell's end takes this stop instead of staying in front of the pipe.
        static int PaddingCellStop(LineInfo info,int end) {
            if (info.Cells==null) return -1;
            string text=info.Text;
            foreach (Cell cell in info.Cells) {
                if (cell.Start!=cell.End || cell.End!=end) continue;
                int p=cell.End-1;
                while (p>=0 && (text[p]==' ' || text[p]=='\t')) p--;
                if (p<0 || text[p]!='|') continue;
                if (cell.End-p-1<2) continue;
                return cell.End-1;
            }
            return -1;
        }
        void NormalizeTableCaret() {
            if (disposed || tableCaretGuarded || Options.Busy || Editor.Document != Document) return;
            var caret=Editor.TextArea.Caret;
            if (caret.Line<1 || caret.Line>Document.LineCount) return;
            DocumentLine line=Document.GetLineByNumber(caret.Line);
            LineInfo info=Info(line);
            int offset=caret.Offset;
            int normalized=offset;
            if (info.Table && !info.Delimiter && info.Cells!=null && info.Cells.Count>0) {
                int relative=offset-line.Offset;
                // H7/H8b: the row's slots outside its cells are never caret stops, also on the
                // last row and for a selection endpoint. A boundary Enter is the table command's
                // rule, which reads the last cell's own end.
                // A whole-table selection whose endpoint is this row's own line end keeps that
                // endpoint: clamping it into the last cell dropped the trailing pipe, so Delete,
                // Cut and typing read the block as a partial cell selection. Only that exact
                // shape pays for the table scan; ordinary keystrokes end before it.
                bool wholeTableEnd=relative==info.Text.Length&&Editor.SelectionLength>0&&
                    MarkdownTableCommands.SelectionCoversWholeTables(Document,Editor.SelectionStart,Editor.SelectionLength);
                if (!wholeTableEnd) normalized=line.Offset+ClampTableRowOffset(info,relative);
            }
            normalized=SnapEscapedPipeOffset(normalized);
            if (normalized==offset) return;
            MoveCaretAndSelection(normalized);
        }
        // H7/H8b: a caret in a table row names a position inside one of its cells. At or before
        // the leading pipe it belongs to the first cell's start; past the row's trailing pipe it
        // belongs to the last cell's end. The space between the leading pipe and the first cell's
        // text is that cell's own leading padding and stays reachable, the same way its trailing
        // padding does: a delete that empties a cell leaves the caret there, and clamping it one
        // offset past the deletion position made the next keystroke land elsewhere (the long
        // typing probe's bad inputs). A typed pipe there is still cell text (TablePipeTyping).
        static int ClampTableRowOffset(LineInfo info,int relative) {
            List<Cell> cells=info.Cells;
            if (cells==null || cells.Count==0) return relative;
            if (relative<0) return 0;
            if (relative>info.Text.Length) relative=info.Text.Length;
            int indent=0;
            while (indent<info.Text.Length && (info.Text[indent]==' ' || info.Text[indent]=='\t')) indent++;
            bool indentStop=indent>0 && relative<=indent;
            // `indent` is the row's leading pipe: only an offset in front of it (or on it) is
            // outside the first cell. Offsets after it, before the cell's text, are its padding.
            if (!indentStop && relative<=indent) return cells[0].Start;
            Cell last=cells[cells.Count-1];
            if (relative>last.End) {
                int pipe=-1;
                for (int p=info.Text.Length-1;p>=last.End;p--) if (info.Text[p]=='|') { pipe=p;break; }
                if (pipe>=0 && relative>pipe) relative=last.End;
            }
            // A padding-only cell's own end is the pipe that closes it, and a caret there types
            // before the pipe ("|  x|"). That offset (a click on the empty cell, an undo, the
            // row's line end) belongs to the cell's own stop one space in
            // (MarkdownTableCommands.EmptyCellStop), which is where "| x |" is typed. A cell with
            // text and a cell narrower than two padding characters keep their end.
            int paddingStop=PaddingCellStop(info,relative);
            if (paddingStop>=0) return paddingStop;
            return relative;
        }
        // H2: "\|" is one caret unit. A caret between the two characters moves after them on
        // every caret move, click and selection change; Backspace/Delete remove both in one step
        // (see TablePipeDelete). Only an odd run of backslashes escapes the pipe.
        static int ClampEscapedPipe(string text,int relative) {
            if (text==null || relative<=0 || relative>=text.Length) return relative;
            if (text[relative-1]!='\\' || text[relative]!='|') return relative;
            int count=0;
            for (int p=relative-1;p>=0 && text[p]=='\\';p--) count++;
            return (count&1)==1 ? relative+1 : relative;
        }
        int SnapEscapedPipeOffset(int offset) {
            if (offset<0 || offset>Document.TextLength) return offset;
            DocumentLine line=Document.GetLineByOffset(offset);
            LineInfo info=Info(line);
            if (info.Code) return offset;
            int relative=offset-line.Offset;
            int snapped=ClampEscapedPipe(info.Text,relative);
            return snapped==relative ? offset : line.Offset+snapped;
        }
        // Moves the caret and, when a selection exists, its moving endpoint. The other endpoint
        // stays the anchor for the next Shift+arrow, the same contract AvalonEdit's own moves use.
        void MoveCaretAndSelection(int offset) {
            if (Editor.Document != Document) return;
            offset=Math.Max(0,Math.Min(offset,Document.TextLength));
            var area=Editor.TextArea;
            tableCaretGuarded=true;
            try {
                if (area.Selection.IsEmpty) area.Caret.Offset=offset;
                else {
                    int start=Editor.SelectionStart, end=start+Editor.SelectionLength;
                    int caret=area.Caret.Offset;
                    int anchor=caret<=start ? end : start;
                    area.Caret.Offset=offset;
                    area.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(area,anchor,offset);
                }
                // AvalonEdit's CalcCaretRectangle keeps reading position.VisualColumn after the
                // PositionChanged it raised from the middle of its own column revalidation
                // (vendor Editing/Caret.cs:369 -> :380); the move above leaves that column at -1
                // and GetTextLine throws on it (Rendering/VisualLine.cs:332). Validate here, while
                // the guard still suppresses a second move, so the interrupted caller sees a real one.
                RevalidateCaretColumn(area);
            } finally { tableCaretGuarded=false; }
        }
        // AvalonEdit validates the caret column by searching the visual line's caret stops and
        // re-assigning Position from inside that search. Our PositionChanged listeners can move
        // the caret from there; their move writes VisualColumn = -1 and invalidates it, but the
        // already-running RevalidateVisualColumn then returns with visualColumnValid = true and
        // CalcCaretRectangle passes the -1 to GetTextLine, which throws
        // ArgumentOutOfRangeException("visualColumn"). Reading Caret.VisualColumn validates the
        // column when it is marked invalid, so a caller that just moved the caret inside a
        // notification leaves a usable value behind. A nested listener can move it again; ask
        // twice, then give up quietly rather than turn a caret into a crash.
        static void RevalidateCaretColumn(ICSharpCode.AvalonEdit.Editing.TextArea area) {
            if (area==null || area.Document==null || area.Caret==null) return;
            for (int attempt=0;attempt<2;attempt++) {
                int column;
                try { column=area.Caret.VisualColumn; }
                catch (ArgumentOutOfRangeException) { column=-1; }
                catch (InvalidOperationException) { return; }
                if (column>=0) return;
                // RevalidateVisualColumn marks the column valid before it does the work
                // (vendor Editing/Caret.cs:321), so a nested move can leave a stored -1 that the
                // getter hands back unchanged. A column below -1 is clamped back to -1 by
                // ValidatePosition yet differs from the stored value, so the setter invalidates
                // the column without moving the caret and the next getter recomputes it.
                try { area.Caret.Position=new ICSharpCode.AvalonEdit.TextViewPosition(area.Caret.Line,area.Caret.Column,-2); }
                catch (ArgumentOutOfRangeException error) { if (error.ParamName!="visualColumn") throw; return; }
                catch (InvalidOperationException) { return; }
            }
            try { if (area.Caret.VisualColumn>=0) return; }
            catch (ArgumentOutOfRangeException) { }
            catch (InvalidOperationException) { }
        }
        // BringCaretToView measures the caret rectangle, so it hits the same negative column when
        // an earlier attempt threw after our listener had already run. Force a fresh validation
        // and retry once: the failed attempt already ran the listener that moved the caret to its
        // normalized spot, so the second pass starts from settled state. Any other failure keeps
        // its own meaning; a repeated visualColumn failure leaves the caret where it is instead
        // of taking the window down.
        internal static void SafeBringCaretToView(ICSharpCode.AvalonEdit.Editing.TextArea area) {
            if (area==null || area.Document==null) return;
            try { area.Caret.BringCaretToView(); return; }
            catch (ArgumentOutOfRangeException error) { if (error.ParamName!="visualColumn") throw; }
            catch (InvalidOperationException) { return; }
            RevalidateCaretColumn(area);
            try { area.Caret.BringCaretToView(); }
            catch (ArgumentOutOfRangeException error) { if (error.ParamName!="visualColumn") throw; }
            catch (InvalidOperationException) { }
        }
        // True when the row ends its table: the next line is neither another row (no '|') nor
        // the |---| line, which belongs to the table but is not a row (so the header is not
        // last).
        internal bool LastTableRow(DocumentLine line) {
            DocumentLine next=line.NextLine;
            return next==null||(!HasTablePipe(Document.GetText(next))&&!IsDelimiterLine(next));
        }
        bool IsDelimiterLine(DocumentLine line) {
            LineInfo info;
            if (lines.TryGetValue(line,out info)) return info.Delimiter;
            return IsDelimiter(Document.GetText(line));
        }
        void CaretGuardChanged() {
            if (disposed || frozen) return;
            NormalizeTableCaret();
            SnapEscapedPipeCaret();
        }
        void SnapEscapedPipeCaret() {
            if (disposed || tableCaretGuarded || Options.Busy || Editor.Document != Document) return;
            var caret=Editor.TextArea.Caret;
            if (caret.Line<1 || caret.Line>Document.LineCount) return;
            int offset=caret.Offset;
            int snapped=SnapEscapedPipeOffset(offset);
            if (snapped==offset) return;
            // The move itself runs through MoveCaretAndSelection, which leaves a validated
            // visual column behind (the caller that raised the caret event may still measure).
            MoveCaretAndSelection(snapped);
        }
        // The IME commit seam asks this before inserting: an offset that sits outside every
        // cell of its rendered row is pulled back into the nearest one, so a syllable can never
        // be written after the row's trailing '|'.
        int ClampCommitOffset(int offset) {
            if (disposed || Options.Busy) return offset;
            if (offset<0 || offset>Document.TextLength) return Math.Max(0,Math.Min(offset,Document.TextLength));
            DocumentLine line=Document.GetLineByOffset(offset);
            LineInfo info=Info(line);
            // The |---| row is structure, not a cell row: a result committed onto it (the caret
            // can sit there after the raw view, and a click commits at that point) would rewrite
            // the delimiter and end the table. It goes into the row below's first cell instead,
            // or the header's last cell when no data row follows, like a caret-only paste.
            if (info.Delimiter) return DelimiterCommitOffset(line);
            if (!info.Table || info.Cells==null || info.Cells.Count==0) return offset;
            if (Raw(line)) return offset;
            int relative=offset-line.Offset;
            // A caret before the row's own leading pipe belongs to the first cell, so a committed
            // syllable can never land in front of the table. The padding AFTER the pipe is the
            // first cell itself (an empty cell's stop is its following pipe), so it stays.
            if (relative<=RowPipeIndex(Document,line)) return line.Offset+info.Cells[0].Start;
            return line.Offset+TableRowElement.Normalize(info,relative);
        }
        int DelimiterCommitOffset(DocumentLine row) {
            DocumentLine next=row.NextLine;
            if (next!=null) {
                LineInfo nextInfo=Info(next);
                if (nextInfo.Table && !nextInfo.Delimiter && nextInfo.Cells!=null && nextInfo.Cells.Count>0)
                    return next.Offset+nextInfo.Cells[0].Start;
            }
            DocumentLine header=row.PreviousLine;
            if (header!=null) {
                LineInfo headerInfo=Info(header);
                if (headerInfo.Cells!=null && headerInfo.Cells.Count>0)
                    return header.Offset+headerInfo.Cells[headerInfo.Cells.Count-1].End;
            }
            return row.Offset;
        }
        // Index of the row's leading pipe (the first non-blank character; a table row starts
        // with it). A caret at or before it is in front of the table, not in the first cell.
        static int RowPipeIndex(TextDocument document,DocumentLine line) {
            int p=0;
            while (p<line.Length) { char c=document.GetCharAt(line.Offset+p); if (c!=' ' && c!='\t') break; p++; }
            return p;
        }
        // The cell that owns a row offset for typed cell text: its own text, either end, and the
        // padding between its text and the neighbouring separator pipes on either side. A
        // separator pipe itself belongs to neither cell (the caller has already made sure the
        // caret is inside a rendered cell). CellAt alone answers -1 for padding, which made a
        // "|" typed on a cell's padding a literal column.
        static int TypingCellAt(LineInfo info,int relative) {
            List<Cell> cells=info.Cells;
            if (cells==null || cells.Count==0) return -1;
            var pipes=new List<int>();
            TablePipes(info.Text,pipes);
            // Cells and separator pipes are both in document order, so one forward index names
            // each cell's following pipe; the previous pipe is the one before it.
            int pipeIndex=0;
            for (int c=0;c<cells.Count;c++) {
                while (pipeIndex<pipes.Count && pipes[pipeIndex]<cells[c].End) pipeIndex++;
                int nextPipe=pipeIndex<pipes.Count ? pipes[pipeIndex] : info.Text.Length;
                int previousPipe=pipeIndex>0 && pipes[pipeIndex-1]<cells[c].Start ? pipes[pipeIndex-1] : -1;
                if (relative>=cells[c].Start && relative<=cells[c].End) return c;
                if (previousPipe>=0 && relative>previousPipe && relative<cells[c].Start) return c;
                if (relative>cells[c].End && relative<nextPipe) return c;
                // The last cell's padding runs to the line's end when the row has no closing pipe.
                if (c==cells.Count-1 && nextPipe==info.Text.Length && relative==info.Text.Length) return c;
            }
            return -1;
        }
        // A selection that spans more than one cell of a table row must not delete a '|'
        // delimiter: only each cell's selected interior text goes, in one undo group, and the
        // caret collapses into the first selected cell. A selection inside one cell keeps
        // AvalonEdit's normal path. The row the caret is on is revealed (Raw) but is the row
        // users select in, so the reveal state must not disable this; the real source view
        // detaches the session entirely and keeps AvalonEdit's behaviour.
        internal bool MultiCellSelection(out int start,out int end,out string text) {
            return MultiCellSelection(out start,out end,out text,false);
        }
        // throughRowNewline: a Backspace/Delete/Paste selection that runs from inside a row
        // through the row's own line break ends at the next line's start. That tail includes the
        // row's trailing pipe, so the ordinary replacement would destroy the table; the row's
        // cell text is what the key means, exactly like a selection inside one row.
        internal bool MultiCellSelection(out int start,out int end,out string text,bool throughRowNewline) {
            start=end=-1;text=null;
            if (disposed || Options.Busy) return false;
            ISegment segment=Editor.TextArea.Selection.SurroundingSegment;
            if (segment==null || segment.Length<=0) return false;
            int first=Document.GetLineByOffset(Math.Min(segment.Offset,Document.TextLength)).LineNumber;
            int last=Document.GetLineByOffset(Math.Min(segment.EndOffset,Document.TextLength)).LineNumber;
            if (first!=last) {
                if (!throughRowNewline) return false;
                DocumentLine row=Document.GetLineByNumber(first);
                DocumentLine endLine=Document.GetLineByNumber(last);
                if (segment.EndOffset!=endLine.Offset) return false;
                LineInfo rowInfo=Info(row);
                if (!rowInfo.Table || rowInfo.Delimiter || rowInfo.Cells==null || rowInfo.Cells.Count==0) return false;
                // Everything between the row and the endpoint's line must be blank: the
                // selection then covers the row's own tail and line breaks, not another cell
                // or paragraph's text.
                for (int number=first+1;number<last;number++) if (Document.GetLineByNumber(number).Length>0) return false;
                int from=Math.Max(rowInfo.Cells[0].Start,segment.Offset-row.Offset);
                int to=rowInfo.Cells[rowInfo.Cells.Count-1].End;
                if (to<=from) {
                    // A selection entirely in the row's padding/pipe/line end has no cell text
                    // to clear; the key is swallowed so the structure survives (H3).
                    start=row.Offset+Math.Max(0,Math.Min(segment.Offset-row.Offset,row.Length));
                    end=start;text=string.Empty;
                    return true;
                }
                start=row.Offset+from;end=row.Offset+to;text=Document.GetText(start,end-start);
                return true;
            }
            DocumentLine line=Document.GetLineByNumber(first);
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0) return false;
            int a=Math.Max(info.Cells[0].Start,segment.Offset-line.Offset);
            int b=Math.Min(info.Cells[info.Cells.Count-1].End,segment.EndOffset-line.Offset);
            if (b<=a) {
                // No cell text in the selection (padding, a separator, the row's line end): the
                // structure is kept and the caller places its input at the nearest cell edge.
                start=line.Offset+Math.Max(0,Math.Min(segment.Offset-line.Offset,line.Length));
                end=start;text=string.Empty;
                return true;
            }
            int firstCell=TableRowElement.CellAt(info,a);
            int lastCell=TableRowElement.CellAt(info,b);
            if (firstCell<0) firstCell=0;
            if (lastCell<0) lastCell=info.Cells.Count-1;
            if (firstCell==lastCell && b<=info.Cells[firstCell].End && a>=info.Cells[firstCell].Start) return false;
            start=line.Offset+a;end=line.Offset+b;text=Document.GetText(start,end-start);
            return true;
        }
        // Clears each selected cell's interior text and leaves the caret in the first one.
        // Returns the offset the follows the edit insert (or the caret for a pure delete).
        internal int ReplaceMultiCell(string insert) {
            return ReplaceMultiCell(insert,false);
        }
        internal int ReplaceMultiCell(string insert,bool throughRowNewline) {
            int start,end;string selected;
            if (!MultiCellSelection(out start,out end,out selected,throughRowNewline)) return -1;
            DocumentLine line=Document.GetLineByOffset(start);
            LineInfo info=Info(line);
            int baseOffset=line.Offset;
            var ranges=new List<Cell>();
            for (int c=0;c<info.Cells.Count;c++) {
                int a=Math.Max(info.Cells[c].Start,start-baseOffset);
                int b=Math.Min(info.Cells[c].End,end-baseOffset);
                if (a<b) ranges.Add(new Cell { Start=baseOffset+a, End=baseOffset+b });
            }
            if (ranges.Count==0) return -1;
            int caret=ranges[0].Start;
            Document.BeginUpdate();
            try {
                for (int r=ranges.Count-1;r>=0;r--) Document.Remove(ranges[r].Start,ranges[r].End-ranges[r].Start);
                if (insert!=null && insert.Length>0) { Document.Insert(caret,insert);caret+=insert.Length; }
                // The history group records the selection the update ended with, so the final caret
                // and the collapsed selection go in before EndUpdate: the entry's TypedEnd then
                // points at the caret and the next typed character joins the same step.
                Editor.TextArea.Caret.Offset=caret;
                if (Editor.TextArea.Selection!=null && !Editor.TextArea.Selection.IsEmpty)
                    Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,caret,caret);
            } finally { Document.EndUpdate(); }
            return caret;
        }
        void MultiCellEntering(object sender,TextCompositionEventArgs e) {
            if (disposed || e.Handled || e.Text==null || e.Text.Length==0) return;
            if (Editor.Document != Document) return;
            // The delimiter row is structure in basic mode and live mode alike: a selection that
            // starts or ends on it must not be replaced by typed text, or the table loses the row
            // (and with it its columns). The caret-line check below only covers the caret itself;
            // the selection endpoint can sit on the row while the caret is on another line.
            if (SelectionTouchesDelimiter()) { Editor.EndTypingRun(); e.Handled=true; return; }
            // Basic view: a hidden fence line is the same kind of structure. Typing over a
            // selection that only covers one fence of a block would leave the partner behind, so
            // the input is kept out unless the selection holds the block's both fences.
            if (SelectionBreaksFence()) { Editor.EndTypingRun(); e.Handled=true; return; }
            if (e.Text=="\r" || e.Text=="\n" || e.Text=="\r\n") return;
            // A selection that covers whole tables is ordinary text: the typed character replaces
            // the whole block through the editor's own path, pipes included.
            ISegment whole=Editor.TextArea.Selection.SurroundingSegment;
            if (whole!=null && whole.Length>0 && MarkdownTableCommands.SelectionCoversWholeTables(Document,whole.Offset,whole.Length)) return;
            // The delimiter row is structure: text typed there (a pipe included) would stop the
            // table. Alignment and columns come from the table tool row, so the input is kept out.
            // A handled input raises no TextEntered, so the typing-run marker is cleared here as
            // well: the swallowed key must not leave the next edit marked as typing.
            DocumentLine typingLine=Document.GetLineByOffset(Math.Min(Editor.CaretOffset,Document.TextLength));
            LineInfo typingInfo=Info(typingLine);
            if (typingInfo.Table && typingInfo.Delimiter) { Editor.EndTypingRun(); e.Handled=true; return; }
            string typed=CellTypedText(e.Text);
            int blockStart,blockEnd;string blockText;
            if (MultiCellSelectionBlock(out blockStart,out blockEnd,out blockText)) {
                int caret=ReplaceMultiCellBlock(typed,blockStart,blockEnd);
                if (caret<0) caret=InsertTypedAtBlockEdge(typed,blockStart);
                if (caret>=0) e.Handled=true;
                return;
            }
            int start,end;string selected;
            if (!MultiCellSelection(out start,out end,out selected)) return;
            int placed=ReplaceMultiCell(typed);
            if (placed<0) placed=InsertCellTextAtSelection(typed,start,start+ (end>start ? end-start : 0));
            if (placed<0) return;
            e.Handled=true;
        }
        // A block selection with no cell text (the row tail/delimiter start) is collapsed to the
        // nearest cell edge and the typed text goes there; no separator or newline is removed.
        int InsertTypedAtBlockEdge(string typed,int offset) {
            DocumentLine line=Document.GetLineByOffset(Math.Min(offset,Document.TextLength));
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0 || typed==null) return -1;
            int relative=offset-line.Offset;
            int cell=TableRowElement.CellAt(info,relative);
            if (cell<0) cell=0;
            int at=line.Offset+Math.Max(info.Cells[cell].Start,Math.Min(info.Cells[cell].End,relative));
            tableCaretGuarded=true;
            try {
                Editor.Select(at,0);
                Document.Insert(at,typed);
            } finally { tableCaretGuarded=false; }
            int caret=at+typed.Length;
            Editor.Select(caret,0);
            return caret;
        }
        // A selection in a row's padding or on a separator has no cell text to replace; the
        // typed character still belongs to a cell, so it lands at the nearest cell edge. The
        // selected range itself is left alone when it holds no cell text: removing it would
        // take the separator pipe (and the padding around it) with the keystroke and change the
        // column count, so typing over " |  " must keep every pipe.
        int InsertCellTextAtSelection(string typed,int selectionStart,int selectionEnd) {
            DocumentLine line=Document.GetLineByOffset(Math.Min(selectionStart,Document.TextLength));
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0) return -1;
            if (typed==null) return -1;
            int relative=selectionStart-line.Offset;
            int cell=TableRowElement.CellAt(info,relative);
            if (cell<0) cell=0;
            int at=Math.Max(info.Cells[cell].Start,Math.Min(info.Cells[cell].End,relative));
            int start=selectionStart,end=selectionEnd;
            int length=Math.Max(0,Math.Min(end,Document.TextLength)-start);
            bool hasText=false;
            int relEnd=end-line.Offset;
            for (int c=0;c<info.Cells.Count && !hasText;c++)
                hasText=Math.Max(info.Cells[c].Start,relative)<Math.Min(info.Cells[c].End,relEnd);
            int target=line.Offset+at;
            Document.BeginUpdate();
            try {
                if (length>0 && hasText) Document.Remove(start,length);
                if (hasText && target>start) target=Math.Max(start,target-length);
                Document.Insert(target,typed);
            } finally { Document.EndUpdate(); }
            int caret=target+typed.Length;
            Editor.TextArea.Caret.Offset=caret;
            if (Editor.TextArea.Selection!=null && !Editor.TextArea.Selection.IsEmpty)
                Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,caret,caret);
            return caret;
        }
        // Typed text that replaces a table selection is cell text: an unescaped "|" would add a
        // column, so it is written as "\|" exactly like a paste (MarkdownTableCommands.CellInsertText).
        static string CellTypedText(string value) {
            if (value==null || value.IndexOf('|')<0) return value;
            var escaped=new StringBuilder(value.Length+8);
            for (int i=0;i<value.Length;i++) {
                char c=value[i];
                if (c=='|' && (i==0 || value[i-1]!='\\')) escaped.Append('\\');
                escaped.Append(c);
            }
            return escaped.ToString();
        }
        // A selection that spans more than one line but stays inside one table block: every line
        // from the first to the last is a row (or the delimiter row) of the same table. Its text
        // is replaced cell by cell, so the "|" separators and the block shape survive.
        bool MultiCellSelectionBlock(out int start,out int end,out string text) {
            start=end=-1;text=null;
            if (disposed || Options.Busy || Editor.Document != Document) return false;
            ISegment segment=Editor.TextArea.Selection.SurroundingSegment;
            if (segment==null || segment.Length<=0) return false;
            // A selection that covers whole tables is not a cell block: its lines go through the
            // editor's own replacement.
            if (MarkdownTableCommands.SelectionCoversWholeTables(Document,segment.Offset,segment.Length)) return false;
            DocumentLine first=Document.GetLineByOffset(Math.Min(segment.Offset,Document.TextLength));
            DocumentLine last=Document.GetLineByOffset(Math.Min(segment.EndOffset,Document.TextLength));
            if (first==last) return false;
            LineInfo firstInfo=Info(first),lastInfo=Info(last);
            if (!firstInfo.Table || firstInfo.Delimiter || firstInfo.Cells==null || firstInfo.Cells.Count==0) return false;
            if (!lastInfo.Table || lastInfo.TableStart==null || firstInfo.TableStart==null) return false;
            // The last line may be the delimiter row only when the selection stops at its start
            // (the endpoint is there; no delimiter text is selected).
            if (lastInfo.Delimiter) { if (segment.EndOffset!=last.Offset) return false; }
            else if (lastInfo.Cells==null || lastInfo.Cells.Count==0) return false;
            if (firstInfo.TableStart.Offset!=lastInfo.TableStart.Offset) return false;
            for (DocumentLine line=first;line!=null;line=line.NextLine) {
                LineInfo info=Info(line);
                if (!info.Table || info.TableStart==null || info.TableStart.Offset!=firstInfo.TableStart.Offset) return false;
                if (line==last) break;
            }
            start=segment.Offset;end=segment.EndOffset;text=Document.GetText(start,end-start);
            return true;
        }
        // Clears every selected cell's text across the block and inserts at the first cell.
        int ReplaceMultiCellBlock(string insert,int start,int end) {
            // Whole tables are ordinary text (the caller's own path replaces them).
            if (MarkdownTableCommands.SelectionCoversWholeTables(Document,start,end-start)) return -1;
            DocumentLine first=Document.GetLineByOffset(Math.Min(start,Document.TextLength));
            DocumentLine last=Document.GetLineByOffset(Math.Min(end,Document.TextLength));
            var ranges=new List<Cell>();
            for (DocumentLine line=first;line!=null;line=line.NextLine) {
                LineInfo info=Info(line);
                if (!info.Delimiter && info.Table && info.Cells!=null) {
                    int from=line==first ? Math.Max(0,start-line.Offset) : 0;
                    int to=line==last ? Math.Max(0,end-line.Offset) : line.Length;
                    for (int c=0;c<info.Cells.Count;c++) {
                        int a=Math.Max(info.Cells[c].Start,from), b=Math.Min(info.Cells[c].End,to);
                        if (a<b) ranges.Add(new Cell { Start=line.Offset+a, End=line.Offset+b });
                    }
                }
                if (line==last) break;
            }
            if (ranges.Count==0) return -1;
            int caret=ranges[0].Start;
            Document.BeginUpdate();
            try {
                for (int r=ranges.Count-1;r>=0;r--) Document.Remove(ranges[r].Start,ranges[r].End-ranges[r].Start);
                if (insert!=null && insert.Length>0) { Document.Insert(caret,insert);caret+=insert.Length; }
                // Same as ReplaceMultiCell: the final caret belongs inside the update, or the group
                // records the old selection start as its end and the next typed character cannot
                // join this step.
                Editor.TextArea.Caret.Offset=caret;
                if (Editor.TextArea.Selection!=null && !Editor.TextArea.Selection.IsEmpty)
                    Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,caret,caret);
            } finally { Document.EndUpdate(); }
            return caret;
        }
        string NormalizeInputNewline(string value) {
            if (value=="\n"||value=="\r"||value=="\r\n") return DocumentNewline();
            return value;
        }
        string DocumentNewline() {
            for (int n=1;n<=Math.Min(Document.LineCount,16);n++) {
                DocumentLine line=Document.GetLineByNumber(n);
                if (line.DelimiterLength>0) return Document.GetText(line.EndOffset,line.DelimiterLength);
            }
            return Environment.NewLine;
        }
        // True when the first or last line the selection touches is a table's delimiter row. The
        // row is structure (its alignment and the table's column count come from it), so an edit
        // that removes or replaces that selection would end the table. Basic mode keeps the row
        // folded; live mode reveals it on the caret/selection line, and the guard applies there.
        internal bool SelectionTouchesDelimiter() {
            if (disposed || Editor.Document != Document) return false;
            ISegment segment=Editor.TextArea.Selection.SurroundingSegment;
            if (segment==null || segment.Length<=0) return false;
            int start=Math.Max(0,Math.Min(segment.Offset,Document.TextLength));
            int end=Math.Max(start,Math.Min(segment.EndOffset,Document.TextLength));
            // A selection that covers whole tables is ordinary text (SelectionCoversWholeTables):
            // its lines go as one block, the delimiter row included, so this guard must not keep
            // them. A selection that only cuts into a table still keeps the row (rule d).
            if (MarkdownTableCommands.SelectionCoversWholeTables(Document,start,end-start)) return false;
            DocumentLine first=Document.GetLineByOffset(start);
            DocumentLine last=Document.GetLineByOffset(Math.Max(start,end-1));
            // The row may only go together with its header (header + delimiter lines, Ctrl+A, the
            // whole table). Guarded: any cut into the row, the row's text alone, and a selection
            // that ends at the row's column 0 (it takes the header's line break and merges them).
            DocumentLine endLine=Document.GetLineByOffset(end);
            DocumentLine delimiter=null;
            if (Info(first).Delimiter) delimiter=first;
            else if (Info(last).Delimiter) delimiter=last;
            else if (end==endLine.Offset && !Object.ReferenceEquals(endLine,last) && Info(endLine).Delimiter) delimiter=endLine;
            if (delimiter==null) return false;
            DocumentLine header=delimiter.PreviousLine;
            return header==null || start>header.Offset || end<delimiter.EndOffset;
        }
        // Basic view: a fence marker line is structure the view draws as nothing. A selection
        // may replace it only when it takes the whole block - both fences of that block inside
        // its text. Anything else would leave one fence behind: removing the opening fence turns
        // the partner into literal text at the block's end, and removing the closing fence alone
        // makes the still-open block swallow the rest of the document. A whole-document selection
        // is ordinary text (Ctrl+A), exactly like the whole-table rule.
        internal bool SelectionBreaksFence() {
            if (disposed || Options.Busy || !Options.HideMarkers || Editor.Document != Document) return false;
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return false;
            ISegment segment=Editor.TextArea.Selection.SurroundingSegment;
            if (segment==null || segment.Length<=0) return false;
            int start=Math.Max(0,Math.Min(segment.Offset,Document.TextLength));
            int end=Math.Max(start,Math.Min(segment.EndOffset,Document.TextLength));
            if (start==0 && end>=Document.TextLength) return false;
            DocumentLine first=Document.GetLineByOffset(start);
            DocumentLine last=Document.GetLineByOffset(Math.Max(start,end-1));
            // Walking the selection's own lines is O(that range); every fence is a line the walk
            // sees, and a fence pair never nests, so one pass answers both fences of each block.
            bool openerInside=false;
            for (DocumentLine line=first;line!=null;line=line.NextLine) {
                LineInfo info=Info(line);
                // The line's text, not its line break: the selection has to hold the marker
                // characters themselves, and a selection that ends at a line's start is not on it.
                bool touched=end>line.Offset && start<line.EndOffset;
                if (touched && info.FenceLine) {
                    bool whole=line.Offset>=start && line.EndOffset<=end;
                    if (info.Before.Fence=='\0') {
                        // Opening fence: its partner is the next fence line, later in this walk.
                        if (!whole) return true;
                        openerInside=true;
                    } else {
                        // Closing fence: only a block whose opener was selected whole counts.
                        if (!openerInside || !whole) return true;
                        openerInside=false;
                    }
                }
                if (line==last) break;
            }
            // An opening fence at the selection's end has no partner inside it.
            return openerInside;
        }
        // A table selection that is not a cell block: the header row plus its delimiter row, both
        // whole. The host's cut rule clears the selected cells and keeps every line; Delete reached
        // through the editor's own command (a probe, a command invocation) must answer the same way
        // instead of handing the whole run to a raw text replacement.
        bool ClearTableSelectionCells() {
            ISegment segment=Editor.TextArea.Selection.SurroundingSegment;
            if (segment==null || segment.Length<=0) return false;
            int start=Math.Max(0,Math.Min(segment.Offset,Document.TextLength));
            int length=Math.Max(0,Math.Min(segment.Length,Document.TextLength-start));
            if (length<=0) return false;
            DocumentLine line=Document.GetLineByOffset(start);
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0) return false;
            if (!MarkdownTableCommands.SpansCellBoundary(Document,start,length,info.Cells)) return false;
            CommandResult clear=MarkdownTableCommands.ClearCellSelection(Document,start,length,info.Cells);
            if (clear==null || clear.Edits.Count==0) return false;
            tableCaretGuarded=true;
            Document.BeginUpdate();
            try {
                foreach (SourceEdit edit in clear.Edits) Document.Replace(edit.Offset,edit.Length,edit.Text);
                int caret=Math.Max(0,Math.Min(clear.SelectionStart,Document.TextLength));
                Editor.TextArea.Caret.Offset=caret;
                if (Editor.TextArea.Selection!=null && !Editor.TextArea.Selection.IsEmpty)
                    Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,caret,caret);
            } finally {
                Document.EndUpdate();
                tableCaretGuarded=false;
            }
            RevalidateCaretColumn(Editor.TextArea);
            return true;
        }
        void MultiCellDelete(object sender,ExecutedRoutedEventArgs e) {
            if (disposed || e.Handled) return;
            ICommand command=e.Command;
            if (command!=EditingCommands.Backspace && command!=EditingCommands.Delete && command!=ApplicationCommands.Delete && command!=ApplicationCommands.Paste) return;
            // Basic view: a hidden fence line is structure (the typing rule above). Only a
            // selection that takes the whole block may remove its fences, so a lone fence line
            // is never deleted while its partner stays behind.
            if (!Editor.TextArea.Selection.IsEmpty && SelectionBreaksFence()) { e.Handled=true; return; }
            // A selection that covers whole tables is ordinary text: Delete, Backspace and Paste
            // replace those lines through the editor's own path, so the cell-clearing and
            // delimiter rules below must not intercept it.
            ISegment whole=Editor.TextArea.Selection.SurroundingSegment;
            if (whole!=null && whole.Length>0 && MarkdownTableCommands.SelectionCoversWholeTables(Document,whole.Offset,whole.Length)) return;
            // The delimiter row is structure: a selection that starts or ends on it must not be
            // removed (Delete/Backspace) or replaced (Paste). The key is swallowed, so the
            // header keeps its delimiter row and the table keeps its columns.
            if (SelectionTouchesDelimiter()) { e.Handled=true; return; }
            int start,end;string selected;
            bool rowNewline=command!=ApplicationCommands.Paste;
            if (!MultiCellSelection(out start,out end,out selected,rowNewline)) {
                // A selection that spans several rows of one table keeps its cell structure too
                // (Backspace/Delete clear the selected cell text, never a separator pipe).
                if (command!=ApplicationCommands.Paste) {
                    int blockStart,blockEnd;string blockText;
                    if (MultiCellSelectionBlock(out blockStart,out blockEnd,out blockText) && ReplaceMultiCellBlock(null,blockStart,blockEnd)>=0) { e.Handled=true; return; }
                    // Header plus delimiter selected whole: neither rule above takes it (the
                    // block rule needs the selection to stop at the delimiter's start), yet the
                    // lines are a table selection. Clearing the cells is what the host's cut
                    // rule does, so Delete answers the same way here.
                    if (ClearTableSelectionCells()) e.Handled=true;
                }
                return;
            }
            string insert=null;
            if (command==ApplicationCommands.Paste) {
                if (!ClipboardRetry.TryGetText(out insert)) insert=null;
                if (insert==null) return;
                insert=NormalizeInputNewline(insert.Replace("\r\n","\n").Replace('\r','\n'));
            }
            ReplaceMultiCell(insert,rowNewline);
            e.Handled=true;
        }
        // With Settings.HideMarkers, unselected inline emphasis/code markers are transparent
        // to Backspace/Delete: the visible character on that side goes instead. Structural
        // prefixes, links, <br> and images retain their whole-unit deletion. A selection that
        // partly covers a marker is widened here, so no half marker is ever left behind.
        void HiddenMarkerDelete(object sender, ExecutedRoutedEventArgs e) {
            if (disposed || e.Handled || Options.Busy || Editor.Document != Document) return;
            ICommand command = e.Command;
            bool back = command == EditingCommands.Backspace;
            bool forward = command == EditingCommands.Delete || command == ApplicationCommands.Delete;
            // A cut removes the same selection a Backspace/Delete would, so the empty-pair rule
            // below covers it too; the copied payload is what the widened selection holds.
            bool cut = command == ApplicationCommands.Cut;
            // Ctrl+Backspace / Ctrl+Delete ask the visual line for a word boundary, so their range
            // starts or ends on a zero-width marker run the caret rules above never look at. They
            // take the same route as the plain keys: the structural walls first, then whole pairs.
            bool wordBack = command == EditingCommands.DeletePreviousWord;
            bool wordForward = command == EditingCommands.DeleteNextWord;
            if (!back && !forward && !cut && !wordBack && !wordForward) return;
            bool backKey = back || wordBack;
            bool forwardKey = forward || wordForward;
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            // Basic view: a hidden fence line is structure. A selection that covers only one
            // fence of a block must not go (and a cut must not copy it): the partner would stay
            // behind and the block would no longer be a block. Both fences inside is ordinary text.
            if (!Editor.TextArea.Selection.IsEmpty && SelectionBreaksFence()) { e.Handled = true; return; }
            // 11-04: Backspace on the only, empty line of a code block takes the block away.
            if (back && Editor.TextArea.Selection.IsEmpty && EmptyFenceBlockBackspace()) { e.Handled = true; return; }
            // H3, both view modes: a text delete inside a table row never leaves the caret's own
            // cell. DeleteInCell/BackspaceInCell answer the cell ends, the separator pipe and the
            // padding run; this check covers every position they leave to the editor (a marker's
            // inner edge, the padding after a typed space, a cell that starts with hidden markup)
            // by asking for the exact range the key would remove.
            if (Editor.TextArea.Selection.IsEmpty && (backKey || forwardKey) && TableCellWall(backKey, wordBack || wordForward)) { e.Handled = true; return; }
            // The delimiter row is structure in both view modes. A folded delimiter is drawn as
            // part of its header and a text delete on that hidden line turns into AvalonEdit's
            // collapsed-section step (one Backspace removed the whole row in the probe); live
            // mode reveals the caret line, where a delete joins the row to its neighbour or eats
            // one of its dashes. Both remove the table's delimiter (or a column), and typed text
            // there is already kept out (MultiCellEntering), so the key does nothing here too.
            if (Editor.TextArea.Selection.IsEmpty && (backKey || forwardKey)) {
                DocumentLine caretLine = Document.GetLineByOffset(Math.Min(Editor.CaretOffset, Document.TextLength));
                LineInfo caretInfo = Info(caretLine);
                if (caretInfo.Delimiter && caretLine.PreviousLine != null) { e.Handled = true; return; }
                // A Backspace at the start of the line directly under a folded delimiter row: the
                // join asks AvalonEdit to step over a line its height tree has collapsed, which
                // removes the folded row (and with it the table's delimiter). That line break is
                // structure; the key does nothing here.
                if (backKey && caretLine.PreviousLine != null && Editor.TextArea.Caret.Offset == caretLine.Offset) {
                    DocumentLine previous = caretLine.PreviousLine;
                    LineInfo previousInfo = Info(previous);
                    if (previousInfo.Delimiter && previous.PreviousLine != null && HideDelimiter(previous.PreviousLine)) { e.Handled = true; return; }
                }
                // The mirror case: Delete at the end of a header row joins the folded delimiter row
                // into it, so the table loses its delimiter. That line break is structure too.
                if (forwardKey && caretLine.NextLine != null && Editor.TextArea.Caret.Offset == caretLine.EndOffset) {
                    LineInfo nextInfo = Info(caretLine.NextLine);
                    if (nextInfo.Delimiter && HideDelimiter(caretLine)) { e.Handled = true; return; }
                }
            }
            // H2/H3: a table row's pipes are structure. An escaped "\|" is one unit in both
            // modes, and an unescaped cell separator is never removed by a text delete.
            if (Editor.TextArea.Selection.IsEmpty && !cut && (backKey || forwardKey) && TablePipeDelete(backKey)) { e.Handled = true; return; }
            // A selection that starts or ends on the delimiter row is structure too: a cut would
            // take the row's text with it and leave the header without its delimiter.
            if (!Editor.TextArea.Selection.IsEmpty && SelectionTouchesDelimiter()) { e.Handled = true; return; }
            // A fence line is structure like the delimiter row (task F2): a delete that joins a
            // hidden fence line with a neighbour or shortens its marker run changes the range the
            // block covers. The key is swallowed; a fence the view shows as source keeps its key.
            if (Editor.TextArea.Selection.IsEmpty && !cut && FenceStructureDelete(backKey, forwardKey)) { e.Handled = true; return; }
            // J4: a selection that covers an inline pair's whole visible text takes both hidden
            // markers with it, so a cut of "링크" from "**링크**" never leaves "****". The pair is
            // one unit in both modes (project rule: live and basic behave the same). A selection
            // that cuts only one side of a pair is widened to the whole pair by the same rule the
            // typing and paste paths use (WidenHiddenPairs), so no half marker can be deleted or
            // copied out as literal text.
            if (!Editor.TextArea.Selection.IsEmpty) { WidenHiddenSelection(); WidenEmptyPair(); WidenHiddenPairs(); return; }
            if (!Options.HideMarkers) return;
            if (cut) return;
            if (wordBack || wordForward) { if (WordMarkerDelete(wordBack)) e.Handled = true; return; }
            if (HiddenTextDelete(back) || HiddenUnitDelete(back)) e.Handled = true;
        }
        // True when this delete would rewrite a hidden fence line: joining it with a neighbour or
        // removing one of its marker characters. A fence line is structure like a delimiter row,
        // and the no-reveal mode keeps it invisible, so the key must not end the block silently
        // (an opening fence that gains a neighbour line keeps that text as its info string; a
        // closing fence that stops closing turns the rest of the document into code). A blank
        // neighbour may still be joined: the fence keeps its own text and its tail.
        bool FenceStructureDelete(bool back,bool forward) {
            if (Editor.Document != Document) return false;
            int offset=Math.Max(0,Math.Min(Editor.CaretOffset,Document.TextLength));
            DocumentLine line=Document.GetLineByOffset(offset);
            LineInfo info=Info(line);
            if (back && offset==line.Offset) {
                DocumentLine previous=line.PreviousLine;
                if (previous==null) return false;
                LineInfo previousInfo=Info(previous);
                bool currentFence=info.FenceLine, previousFence=previousInfo.FenceLine;
                if (!currentFence && !previousFence) return false;
                bool currentHidden=currentFence && !Raw(line);
                bool previousHidden=previousFence && !Raw(previous);
                if (!currentHidden && !previousHidden) return false;
                // Two fence markers merged by the join become one longer run: the empty block
                // between them disappears and the new run opens a different block.
                if (currentFence && previousFence) return true;
                string joined=currentFence ? previousInfo.Text : info.Text;
                return FenceOtherVisible(joined);
            }
            if (forward && offset==line.EndOffset) {
                DocumentLine next=line.NextLine;
                if (next==null) return false;
                LineInfo nextInfo=Info(next);
                bool currentFence=info.FenceLine, nextFence=nextInfo.FenceLine;
                if (!currentFence && !nextFence) return false;
                bool currentHidden=currentFence && !Raw(line);
                bool nextHidden=nextFence && !Raw(next);
                if (!currentHidden && !nextHidden) return false;
                if (currentFence && nextFence) return true;
                string joined=currentFence ? nextInfo.Text : info.Text;
                return FenceOtherVisible(joined);
            }
            // Inside the fence's own marker run a delete shortens the run below three characters
            // (or shortens a longer closing run below the opener's length).
            if (info.FenceLine && !Raw(line)) {
                string text=info.Text;
                int column=offset-line.Offset;
                int runStart=0;
                while (runStart<text.Length && (text[runStart]==' ' || text[runStart]=='\t')) runStart++;
                if (runStart<text.Length) {
                    char marker=text[runStart];
                    int runEnd=runStart;
                    while (runEnd<text.Length && text[runEnd]==marker) runEnd++;
                    if (back && column>runStart && column<=runEnd) return true;
                    if (forward && column>=runStart && column<runEnd) return true;
                }
            }
            return false;
        }
        // 11-04: the empty block "```", "", "```" (what Enter after "```" or the code button writes) has
        // its caret on the blank body line. Backspace there used to join that line into the hidden
        // opening fence, which left two adjacent fences with no caret stop and swallowed the next
        // typed text. 11-05 (the block is now written by Enter after "```python"): Backspace in the
        // empty block takes back what that Enter wrote, the body line and the closing fence, in
        // one undo step; the caret goes to the end of the opening line ("```python"), which is then
        // an unclosed fence the basic view shows as source (EditableFence), so a further Backspace
        // deletes its characters one by one.
        bool EmptyFenceBlockBackspace() {
            int offset=Math.Max(0,Math.Min(Editor.CaretOffset,Document.TextLength));
            DocumentLine line=Document.GetLineByOffset(offset);
            if (offset!=line.Offset || line.Length!=0) return false;
            DocumentLine previous=line.PreviousLine, next=line.NextLine;
            if (previous==null || next==null) return false;
            LineInfo opener=Info(previous), closer=Info(next);
            if (!opener.FenceLine || opener.Before.Fence!='\0') return false;
            if (!closer.FenceLine || closer.Before.Fence=='\0') return false;
            int start=previous.EndOffset;
            Document.Remove(start,next.EndOffset-start);
            Editor.Select(start,0);
            return true;
        }
        static bool FenceOtherVisible(string text) {
            if (text==null) return false;
            for (int i=0;i<text.Length;i++) if (text[i]!=' ' && text[i]!='\t') return true;
            return false;
        }
        // H3: Backspace/Delete at a table row's separator pipe does nothing, so a burst of keys
        // cannot delete the neighbour cell's delimiter and change the column count. H2: the two
        // characters of an escaped "\|" are one unit and go together in one undo step.
        bool TablePipeDelete(bool back) {
            if (Editor.Document != Document) return false;
            int offset=Editor.CaretOffset;
            if (offset<0 || offset>Document.TextLength) return false;
            DocumentLine line=Document.GetLineByOffset(offset);
            LineInfo info=Info(line);
            // A fenced code line keeps the literal text: "\|" is two ordinary characters there,
            // exactly like HiddenTextDelete's code guard.
            if (info.Code) return false;
            string text=info.Text;
            int relative=offset-line.Offset;
            int unit=EscapedPipeUnit(text,relative,back);
            if (unit>=0) {
                Document.BeginUpdate();
                try {
                    Document.Remove(line.Offset+unit,2);
                    Editor.TextArea.Caret.Offset=line.Offset+unit;
                } finally { Document.EndUpdate(); }
                return true;
            }
            if (!info.Table || info.Delimiter) return false;
            // The row's own line break is structure too: Backspace at the row's start or
            // Delete at its end would join a row to its neighbour and drop the block's line.
            // There is no documented row merge (the table tool row removes a row whole), so
            // the key is swallowed. A row whose cells parse to none ("|   " after a burst)
            // still owns its pipe, so the separator rule below does not need a cell count.
            if (back) { if (relative==0) return line.PreviousLine!=null; }
            else if (relative==text.Length) return line.NextLine!=null;
            int next=TextUtilities.GetNextCaretPosition(Document,offset,back ? LogicalDirection.Backward : LogicalDirection.Forward,CaretPositioningMode.Normal);
            if (next<0 || Math.Abs(next-offset)!=1) return false;
            int pipe=Math.Min(offset,next)-line.Offset;
            if (pipe<0 || pipe>=text.Length || text[pipe]!='|') return false;
            // Only a pipe the renderer reads as a column separator is structure; a pipe inside
            // an escaped pair or a code span is cell text and may be deleted.
            var pipes=new List<int>();
            TablePipes(text,pipes);
            return pipes.Contains(pipe);
        }
        // The cell wall of H3 for the editor's own Delete/Backspace: the key must only remove text
        // inside the caret's own cell. DeleteInCell/BackspaceInCell answer the cell ends and the
        // pipe/padding run; a position they leave to the editor (a marker's inner edge, the
        // padding after a typed space, a cell whose text starts with hidden markup) is checked
        // here with the very step the command runs, so no key crosses a separator in either mode.
        bool TableCellWall(bool back,bool word) {
            if (Editor.Document != Document) return false;
            int offset=Editor.CaretOffset;
            if (offset<0 || offset>Document.TextLength) return false;
            DocumentLine line=Document.GetLineByOffset(offset);
            LineInfo info=Info(line);
            if (info.Code || info.Delimiter || info.SetextUnderline>0 || !info.Table || info.Cells==null || info.Cells.Count==0) return false;
            int cell=WallCellAt(info.Cells,offset-line.Offset);
            if (cell<0) {
                // The row's leading run (its indentation, the leading pipe and the first cell's
                // padding). Backspace there is the structural indent/row-start rule; Delete would
                // step into the first cell's padding or over its pipe, so it is a wall.
                if (back) return false;
                cell=0;
            }
            int endOffset;
            try { endOffset=word ? WordStepOffset(back,line) : WallStepOffset(back,line); }
            catch (Exception) { return false; }
            if (endOffset<0) return false;
            int start=Math.Min(offset,endOffset),finish=Math.Max(offset,endOffset);
            if (finish<=start) return false;
            Cell span=info.Cells[cell];
            if (start>=line.Offset+span.Start && finish<=line.Offset+span.End) return false;
            // A space typed after the cell text lies past the trimmed end: removing only that
            // whitespace never reaches the closing pipe, so it stays the cell's own edit.
            if (start>=line.Offset+span.End && WhitespaceOnly(start,finish)) return false;
            // Backspace at the cell's drawn start (a cell whose text opens with hidden markup)
            // is the structured rule's move to the previous cell's end, never a delete.
            if (back && Options.HideMarkers && start<line.Offset+span.Start && LeadingHiddenRun(info,span,offset-line.Offset)) {
                Editor.TextArea.Caret.Offset=cell>0 ? line.Offset+info.Cells[cell-1].End : line.Offset+span.Start;
                // This key may run from a caret notification or right before a measurement; a
                // moved caret must leave a usable visual column behind.
                RevalidateCaretColumn(Editor.TextArea);
            }
            return true;
        }
        bool WhitespaceOnly(int start,int finish) {
            if (start<0 || finish>Document.TextLength) return false;
            for (int i=start;i<finish;i++) { char c=Document.GetCharAt(i); if (c!=' ' && c!='\t') return false; }
            return true;
        }
        // The offset the editor's own Delete (CharRight) or Backspace takes from the caret, read
        // with the same public visual-line steps CaretNavigationCommandHandler uses (that class is
        // internal to the AvalonEdit build, so its method is not callable here). -1 when the step
        // has no caret position at all.
        int WallStepOffset(bool back,DocumentLine line) {
            var caret=Editor.TextArea.Caret;
            ICSharpCode.AvalonEdit.TextViewPosition position=caret.Position;
            VisualLine visual=View.GetOrConstructVisualLine(line);
            bool virtualSpace=back && Editor.Options!=null && Editor.Options.EnableVirtualSpace;
            CaretPositioningMode mode=back ? CaretPositioningMode.EveryCodepoint : CaretPositioningMode.Normal;
            int pos=visual.GetNextCaretPosition(position.VisualColumn,back ? LogicalDirection.Backward : LogicalDirection.Forward,mode,virtualSpace);
            if (pos>=0) return Document.GetOffset(visual.GetTextViewPosition(pos).Location);
            if (back) {
                DocumentLine previous=visual.FirstDocumentLine.PreviousLine;
                if (previous==null) return 0;
                VisualLine previousLine=View.GetOrConstructVisualLine(previous);
                pos=previousLine.GetNextCaretPosition(previousLine.VisualLength+1,LogicalDirection.Backward,mode,virtualSpace);
                if (pos<0) return -1;
                return Document.GetOffset(previousLine.GetTextViewPosition(pos).Location);
            }
            DocumentLine next=visual.LastDocumentLine.NextLine;
            if (next==null) return Document.TextLength;
            VisualLine nextLine=View.GetOrConstructVisualLine(next);
            pos=nextLine.GetNextCaretPosition(-1,LogicalDirection.Forward,mode,virtualSpace);
            if (pos<0) return -1;
            return Document.GetOffset(nextLine.GetTextViewPosition(pos).Location);
        }
        // The range EditingCommands.DeletePreviousWord/DeleteNextWord take, read with the same
        // visual-line step CaretNavigationCommandHandler uses (WordStart, both directions). The
        // hidden runs are caret stops, so the vendor range can start or end on one; the caller
        // widens it to whole pairs. -1 when the step has no caret position at all.
        int WordStepOffset(bool back,DocumentLine line) {
            var caret=Editor.TextArea.Caret;
            ICSharpCode.AvalonEdit.TextViewPosition position=caret.Position;
            VisualLine visual=View.GetOrConstructVisualLine(line);
            bool virtualSpace=back && Editor.Options!=null && Editor.Options.EnableVirtualSpace;
            CaretPositioningMode mode=CaretPositioningMode.WordStart;
            int pos=visual.GetNextCaretPosition(position.VisualColumn,back ? LogicalDirection.Backward : LogicalDirection.Forward,mode,virtualSpace);
            if (pos>=0) return Document.GetOffset(visual.GetTextViewPosition(pos).Location);
            if (back) {
                DocumentLine previous=visual.FirstDocumentLine.PreviousLine;
                if (previous==null) return 0;
                VisualLine previousLine=View.GetOrConstructVisualLine(previous);
                pos=previousLine.GetNextCaretPosition(previousLine.VisualLength+1,LogicalDirection.Backward,mode,virtualSpace);
                if (pos<0) return -1;
                return Document.GetOffset(previousLine.GetTextViewPosition(pos).Location);
            }
            DocumentLine next=visual.LastDocumentLine.NextLine;
            if (next==null) return Document.TextLength;
            VisualLine nextLine=View.GetOrConstructVisualLine(next);
            pos=nextLine.GetNextCaretPosition(-1,LogicalDirection.Forward,mode,virtualSpace);
            if (pos<0) return -1;
            return Document.GetOffset(nextLine.GetTextViewPosition(pos).Location);
        }
        // True when every character from the cell's first source character up to the caret is the
        // cell's own hidden markup, so the caret is drawn at the cell's visible start.
        static bool LeadingHiddenRun(LineInfo info,Cell cell,int relative) {
            if (relative<=cell.Start) return true;
            int at=cell.Start;
            while (at<relative) {
                int next=-1;
                foreach (Replacement marker in info.Replacements) {
                    if (marker.Length<=0) continue;
                    if (marker.Kind!=ReplacementKind.Hidden && marker.Kind!=ReplacementKind.Spacer && marker.Kind!=ReplacementKind.Glyph) continue;
                    if (at<marker.Start || at>=marker.Start+marker.Length) continue;
                    next=marker.Start+marker.Length;
                    break;
                }
                if (next<0) return false;
                at=next;
            }
            return true;
        }
        // The cell a row offset belongs to for the wall: a cell's text and the padding between its
        // text and the following pipe are that cell's, so the gap after cell c answers c. The run
        // in front of the first cell's text answers -1 (the row's own leading run, whose rules
        // stay with the structured keys).
        static int WallCellAt(List<Cell> cells,int relative) {
            if (cells==null || cells.Count==0 || relative<cells[0].Start) return -1;
            int cell=0;
            for (int c=0;c<cells.Count;c++) {
                if (relative<cells[c].Start) break;
                cell=c;
            }
            return cell;
        }
        // The [start, start+2) span of the escaped-pipe unit adjacent to or containing the
        // caret, or -1. Only an odd run of backslashes escapes the pipe.
        static int EscapedPipeUnit(string text,int relative,bool back) {
            int unit=-1;
            if (relative>=1 && relative<text.Length && text[relative-1]=='\\' && text[relative]=='|') unit=relative-1;
            else if (back) {
                if (relative>=2 && text[relative-1]=='|' && text[relative-2]=='\\') unit=relative-2;
            } else {
                if (relative+1<text.Length && text[relative]=='\\' && text[relative+1]=='|') unit=relative;
            }
            if (unit<0) return -1;
            int count=0;
            for (int p=unit;p>=0 && text[p]=='\\';p--) count++;
            return (count&1)==1 ? unit : -1;
        }
        // Inline emphasis/code markers have no visible width: both source edges name the same
        // visible insertion point. Delete a neighbouring grapheme, never the hidden delimiter.
        // Prefixes, images, links and <br> keep their structural/unit deletion rules below.
        bool HiddenTextDelete(bool back) {
            int offset=Editor.CaretOffset;
            if (offset<0 || offset>Document.TextLength) return false;
            DocumentLine line=Document.GetLineByOffset(offset);
            LineInfo info=Info(line);
            if (info.Code || info.Delimiter || info.SetextUnderline>0) return false;
            int relative=offset-line.Offset, point=relative;
            // An empty pair ("<span ...></span>" from the typing-size command) has no visible
            // character on either side; its own unit rule removes both tags, so decline here
            // instead of deleting one half and leaving the other as raw text.
            if (EmptyPairAt(info, relative)) return false;
            var markers=new List<Replacement>();bool adjacent=false;
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind!=ReplacementKind.Hidden || marker.Length<=0) continue;
                if (PartnerMarker(info,marker,true)<0 && PartnerMarker(info,marker,false)<0) continue;
                markers.Add(marker);
                if (relative>=marker.Start && relative<=marker.Start+marker.Length) adjacent=true;
            }
            if (!adjacent) return false;
            // Skip a contiguous run in source order, including an offset left inside it by an
            // API/mouse path. The opposite edge of the run is the same on-screen position.
            if (back) {
                for (int i=markers.Count-1;i>=0;i--) {
                    Replacement marker=markers[i];
                    if (marker.Start<point && marker.Start+marker.Length>=point) point=marker.Start;
                }
            } else {
                foreach (Replacement marker in markers)
                    if (marker.Start<=point && marker.Start+marker.Length>point) point=marker.Start+marker.Length;
            }
            int at=line.Offset+point;
            int next;
            // TextUtilities treats CR and LF as separate codepoints; a line boundary is one
            // edit, just like AvalonEdit's ordinary Delete/Backspace path.
            if (back && point==0) next=line.PreviousLine==null ? -1 : line.PreviousLine.EndOffset;
            else if (!back && point==line.Length) next=line.DelimiterLength==0 ? -1 : line.EndOffset+line.DelimiterLength;
            else next=TextUtilities.GetNextCaretPosition(Document,at,back ? LogicalDirection.Backward : LogicalDirection.Forward,CaretPositioningMode.Normal);
            if (next<0) return true;
            int start=Math.Min(at,next),end=Math.Max(at,next);
            // A table row never loses a column separator or its cell padding to a text
            // delete: its own command path is responsible for that structure. Anywhere else
            // the range may touch a structural neighbour (a list bullet, a heading mark); the
            // key must still reach the normal path, so decline instead of swallowing it, and
            // HiddenUnitDelete or AvalonEdit's own command decides from the untouched caret.
            foreach (Replacement marker in info.Replacements) {
                if (marker.Length<=0) continue;
                if (start>=line.Offset+marker.Start+marker.Length || end<=line.Offset+marker.Start) continue;
                // The range crosses hidden markup. A table row must keep its separators and
                // cell padding, so it stays handled (nothing is deleted); everywhere else the
                // caret was not on a plain visible character after all, so give the key back.
                return info.Table;
            }
            // The whitespace this delete would leave against the pair's own marker moves outside
            // the marker in the same update, so the pair keeps parsing (see the helper).
            if (HiddenWhitespaceDelete(start,end,back,line,info)) return true;
            Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,start,end);
            WidenEmptyPair();
            // One document update, like AvalonEdit's normal selection deletion, so the last
            // character also removes its empty pair and Undo restores both together.
            Editor.TextArea.Selection.ReplaceSelectionWithText("");
            SafeBringCaretToView(Editor.TextArea);
            return true;
        }
        // Ctrl+Backspace / Ctrl+Delete over hidden markers. The visual-line word range the vendor
        // command would take starts or ends on a zero-width run when the word touches a pair, so
        // deleting it as-is removes one half of the pair and the no-reveal view draws the other
        // half as literal text. The same pair rule the selection and paste paths use runs here:
        // every pair the range cuts (or spends its whole range on) goes with it, in one document
        // update so one undo takes the change back. A table cell is the one exemption to the
        // row-level skip in WidenMarkerPairs: the cell wall already kept the range inside it, so
        // the cell's own inline pair is not row structure.
        bool WordMarkerDelete(bool back) {
            int offset=Editor.CaretOffset;
            if (offset<0||offset>Document.TextLength) return false;
            DocumentLine line=Document.GetLineByOffset(offset);
            LineInfo info=Info(line);
            if (info.Code || info.Delimiter || info.SetextUnderline>0) return false;
            int step;
            try { step=WordStepOffset(back,line); }
            catch (Exception) { return false; }
            if (step<0 || step==offset) return false;
            int start=Math.Min(offset,step), end=Math.Max(offset,step);
            // An endpoint left inside a marker by an undo or an API-set offset takes that marker's
            // own edge first, exactly like the selection rules, so no half marker is deleted.
            start=Math.Min(start,WidenStart(start));
            end=Math.Max(end,WidenEnd(end));
            int from,to;
            if (info.Table) {
                if (WidenMarkerPairs(start,end,true,out from,out to)) { start=from; end=to; }
            } else if (WidenMarkerPairs(start,end,out from,out to)) { start=from; end=to; }
            if (start<0) start=0;
            if (end>Document.TextLength) end=Document.TextLength;
            if (end<=start) return false;
            Editor.TextArea.Selection=ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea,start,end);
            // A word that is the pair's whole visible text takes the pair like the last character
            // the plain Delete removes does.
            WidenEmptyPair();
            Editor.TextArea.Selection.ReplaceSelectionWithText("");
            SafeBringCaretToView(Editor.TextArea);
            return true;
        }
        // A delete that would leave the pair's own edge whitespace against a closing (or opening)
        // marker turns the pair into literal text: CommonMark needs a non-space character inside a
        // closer and right after an opener. The whitespace moves outside the stacked marker run in
        // the SAME document update, so the pair keeps parsing and one undo still takes the whole
        // change back. Returns true when it applied the delete itself.
        bool HiddenWhitespaceDelete(int start,int end,bool back,DocumentLine line,LineInfo info) {
            string text=info.Text;
            int relStart=start-line.Offset,relEnd=end-line.Offset;
            if (relStart<0||relEnd>text.Length||relStart>=relEnd) return false;
            if (back) {
                int runStart,runEnd;
                // The run the caret skipped to; it begins at the deleted grapheme.
                if (!HiddenCloseRun(info,relEnd,out runStart,out runEnd)||runStart!=relEnd) return false;
                // A tag pair takes whitespace inside it; only emphasis, strike and code break.
                Replacement closer=MarkerStarting(info,relEnd);
                if (closer.Length<=0||IsTagMarker(info,closer)) return false;
                int opener=PartnerMarker(info,closer,false);
                if (opener<0) return false;
                int chunkEnd=ClosePairEnd(info,relEnd,runEnd);
                if (chunkEnd<=relEnd) return false;
                int contentStart=info.Replacements[opener].Start+info.Replacements[opener].Length;
                int spacesStart=relStart;
                while (spacesStart>contentStart&&char.IsWhiteSpace(text[spacesStart-1])) spacesStart--;
                if (spacesStart==relStart) return false;
                string run=text.Substring(relEnd,chunkEnd-relEnd),spaces=text.Substring(spacesStart,relStart-spacesStart);
                int at=line.Offset+spacesStart;
                Document.BeginUpdate();
                try { Document.Replace(at,chunkEnd-spacesStart,run+spaces); }
                finally { Document.EndUpdate(); }
                // The caret sits at the visible end, after the whitespace that moved out.
                Editor.TextArea.Caret.Offset=Math.Min(Document.TextLength,at+run.Length+spaces.Length);
                SafeBringCaretToView(Editor.TextArea);
                return true;
            }
            int openStart,openEnd;
            if (!HiddenOpenRun(info,relStart,out openStart,out openEnd)||openEnd!=relStart) return false;
            Replacement openMarker=MarkerEnding(info,relStart);
            if (openMarker.Length<=0||IsTagMarker(info,openMarker)) return false;
            int closeIndex=PartnerMarker(info,openMarker,true);
            if (closeIndex<0) return false;
            int chunkStart=OpenPairStart(info,openStart,relStart);
            if (chunkStart>=relStart) return false;
            int contentEnd=info.Replacements[closeIndex].Start;
            int spacesEnd=relEnd;
            while (spacesEnd<contentEnd&&char.IsWhiteSpace(text[spacesEnd])) spacesEnd++;
            if (spacesEnd==relEnd) return false;
            string markers=text.Substring(chunkStart,relStart-chunkStart),leading=text.Substring(relEnd,spacesEnd-relEnd);
            int begin=line.Offset+chunkStart;
            Document.BeginUpdate();
            try { Document.Replace(begin,spacesEnd-chunkStart,leading+markers); }
            finally { Document.EndUpdate(); }
            // The caret stays on the visible start of the remaining text, now after the moved space.
            Editor.TextArea.Caret.Offset=Math.Min(Document.TextLength,begin+leading.Length+markers.Length);
            SafeBringCaretToView(Editor.TextArea);
            return true;
        }
        // A delete, backspace or cut that removes the whole text of an emphasis, strike or code
        // pair would leave the two hidden markers behind ("****"), and the no-reveal view then
        // draws them as text. The pair goes with its own text: the selection grows over both
        // markers before the same command runs, so the whole removal stays one undo step. A
        // typed replacement never comes here, so typing over the text keeps the pair.
        void WidenEmptyPair() {
            ISegment segment = Editor.TextArea.Selection.SurroundingSegment;
            if (segment == null || segment.Length <= 0) return;
            DocumentLine line = Document.GetLineByOffset(segment.Offset);
            if (segment.EndOffset > line.EndOffset) return;
            LineInfo info = Info(line);
            if (info.Code || info.Delimiter || info.SetextUnderline > 0) return;
            // Nested pairs (***가***, **~~가~~**, a span around **text**) leave the next pair
            // around the already widened selection, so keep widening until nothing encloses it.
            for (int pass = 0; pass < 8; pass++) if (!WidenOneEmptyPair(info, line)) break;
            if (!info.Table) WidenOnlyVisibleText(info, line);
        }
        // One pair around the whole selection: its text is styled and a hidden marker sits on
        // both edges (equal lengths for an emphasis pair, a matching tag pair for span/font).
        bool WidenOneEmptyPair(LineInfo info, DocumentLine line) {
            ISegment segment = Editor.TextArea.Selection.SurroundingSegment;
            if (segment == null || segment.Length <= 0) return false;
            int from, to;
            if (!EmptyPairAround(info, line, segment.Offset, segment.EndOffset, out from, out to)) return false;
            Editor.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, from, to);
            Editor.TextArea.Caret.Offset = from;
            return true;
        }
        // [from,to) is one pair's hidden markers plus its whole styled text. Shared by the caret
        // selection rules and the drag source range, which has no live selection to read.
        bool EmptyPairAround(LineInfo info, DocumentLine line, int start, int end, out int from, out int to) {
            from = start; to = end;
            if (end > line.EndOffset) return false;
            int innerStart = start - line.Offset, innerEnd = end - line.Offset;
            if (innerEnd <= innerStart) return false;
            if (info.Table) {
                bool inCell=false;
                if (info.Cells!=null) foreach (Cell cell in info.Cells)
                    if (innerStart>=cell.Start && innerEnd<=cell.End) { inCell=true;break; }
                if (!inCell) return false;
            }
            bool styled = false;
            foreach (StyledPart part in info.Styles) {
                if (part.Start != innerStart || part.Length != innerEnd - innerStart) continue;
                styled = true;
                break;
            }
            if (!styled) return false;
            Replacement? open = null, close = null;
            foreach (Replacement candidate in info.Replacements) {
                if (candidate.Kind != ReplacementKind.Hidden || candidate.Length <= 0) continue;
                if (candidate.Start + candidate.Length == innerStart) open = candidate;
                if (candidate.Start == innerEnd) close = candidate;
            }
            if (!open.HasValue || !close.HasValue) return false;
            bool pair = open.Value.Length <= 4 && open.Value.Length == close.Value.Length;
            if (!pair) {
                // <span ...>text</span>, <font ...>text</font>: the closing tag names the same
                // element, and the pair may have different lengths.
                pair = TagPair(info.Text, open.Value, close.Value);
            }
            if (!pair) return false;
            from = line.Offset + open.Value.Start;
            to = line.Offset + close.Value.Start + close.Value.Length;
            if (from >= start || to <= end) return false;
            return true;
        }
        // When the selection is the only visible text of the line, the inline markup on that
        // line belongs to it. Overlapping/nested pairs (***가***) are framed by no single pair,
        // so taking the markers too is what keeps "******" and empty span tags off the screen.
        void WidenOnlyVisibleText(LineInfo info, DocumentLine line) {
            ISegment segment = Editor.TextArea.Selection.SurroundingSegment;
            if (segment == null || segment.Length <= 0) return;
            int from, to;
            if (!OnlyVisibleTextRange(info, line, segment.Offset, segment.EndOffset, out from, out to)) return;
            Editor.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, from, to);
            Editor.TextArea.Caret.Offset = from;
        }
        // True with [from,to) grown over every non-prefix hidden marker on the line when
        // [start,end) is exactly the line's visible text. Shared with the drag source range.
        bool OnlyVisibleTextRange(LineInfo info, DocumentLine line, int start, int end, out int from, out int to) {
            from = start; to = end;
            if (end > line.EndOffset) return false;
            int innerStart = start - line.Offset, innerEnd = end - line.Offset;
            if (innerEnd <= innerStart) return false;
            for (int p = 0; p < info.Text.Length; p++) {
                if (p >= innerStart && p < innerEnd) continue;
                if (InsideReplacement(info, p)) continue;
                return false;
            }
            int wideStart = innerStart, wideEnd = innerEnd;
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Length <= 0 || replacement.Kind != ReplacementKind.Hidden || replacement.Prefix) continue;
                if (replacement.Start < wideStart) wideStart = replacement.Start;
                if (replacement.Start + replacement.Length > wideEnd) wideEnd = replacement.Start + replacement.Length;
            }
            if (wideStart == innerStart && wideEnd == innerEnd) return false;
            from = line.Offset + wideStart; to = line.Offset + wideEnd;
            return true;
        }
        static bool InsideReplacement(LineInfo info, int relative) {
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Length <= 0) continue;
                if (relative >= replacement.Start && relative < replacement.Start + replacement.Length) return true;
            }
            return false;
        }
        // "<span ...>" immediately followed (through the selection) by its own "</span>".
        static bool TagPair(string text, Replacement open, Replacement close) {
            if (open.Start < 0 || open.Start + 1 >= text.Length || text[open.Start] != '<') return false;
            if (open.Start + 1 < text.Length && text[open.Start + 1] == '/') return false;
            if (close.Start < 0 || close.Start + 2 >= text.Length) return false;
            if (text[close.Start] != '<' || text[close.Start + 1] != '/') return false;
            int name = open.Start + 1;
            int nameEnd = name;
            while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] == '-')) nameEnd++;
            int length = nameEnd - name;
            if (length <= 0) return false;
            if (close.Start + 2 + length >= text.Length) return false;
            return String.Compare(text, name, text, close.Start + 2, length, StringComparison.OrdinalIgnoreCase) == 0;
        }
        // Stage 3c: typing or pasting over a selection that only partly covers a marker widens
        // the selection first, so the marker is replaced whole instead of leaving half of it.
        void HiddenSelectionEntering(object sender, TextCompositionEventArgs e) {
            if (disposed || e.Handled || !Options.HideMarkers || Options.Busy) return;
            if (e.Text == null || e.Text.Length == 0) return;
            if (Editor.TextArea.Selection.IsEmpty) return;
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            WidenHiddenSelection();
            WidenHiddenPairs();
        }
        // The one spot where "before the closing **" and "after it" draw the same: an empty
        // selection on the edge of a hidden closing run. A non-blank character typed there takes
        // the style the toolbar shows at that spot (11-05 user decision, replacing the 11-01 rule
        // that wrote it after a bold closer): "**굵게**" then "x" writes "**굵게x**", and a 20 px
        // span or colour tag around the bold keeps its size and colour. A SPACE is different: a
        // space typed at or past an already closed bold closer stays outside and plain
        // ("**light**" + " and" is "**light** and", 11-01), while a space typed inside the pair
        // (the run being typed) waits at the closing edge for the next character to take it back.
        void HiddenCloseTypingEntering(object sender, TextCompositionEventArgs e) {
            if (disposed || e.Handled || Options.Busy || !CloseSpaceAllowed()) return;
            if (e.Text == null || e.Text.Length == 0) return;
            // A newline is the Enter rule's business (LiveMarkdownCommands.InlineEnter moves the
            // pair outside first); writing it inside the pair here would cut the pair in two.
            if (e.Text == "\r" || e.Text == "\n" || e.Text == "\r\n") return;
            if (!Editor.TextArea.Selection.IsEmpty) return;
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            int offset = Editor.TextArea.Caret.Offset;
            if (offset < 0 || offset > Document.TextLength) return;
            // One input after a closing-edge merge still belongs to that word (closeWordCaret).
            // Read it once and consume it: the character lands inside the pair, and any later
            // character is already there, so only this one input needs the mark.
            int wordCaret = closeWordCaret;
            closeWordCaret = -1;
            bool continuingWord = wordCaret >= 0 && offset == wordCaret &&
                Object.ReferenceEquals(Document, closeWordDocument) &&
                Object.ReferenceEquals(Document.Version, closeWordVersion);
            DocumentLine line = Document.GetLineByOffset(offset);
            LineInfo info = Info(line);
            int relative = offset - line.Offset;
            bool blank = BlankText(e.Text);
            // An arm from an earlier input that no TextEntered consumed is stale.
            closeSpaceArm = false;
            // H10: a table cell's own end is its boundary. Typing there (a click at the cell's
            // right edge lands on it) belongs after the cell's text, not inside a closing run
            // that happens to end at the same source offset; the cell, not the pair, owns the spot.
            if (info.Table && info.Cells != null && AtCellEnd(info, relative)) return;
            int start, end;
            if (HiddenCloseRun(info, relative, out start, out end)) {
                // 11-05 user decision: a non-blank character typed anywhere on a closing run's edges
                // takes the style the toolbar shows there. FormatAt/LiveDrawnSize read the whole
                // stacked run as the pair's inside (HiddenCloseInside), so typing goes to the same
                // spot (TypingInsideTarget): `**굵게**|` + X writes `**굵게X**`, with the enclosing
                // size/colour tag kept, in the no-reveal view and on the revealed caret line alike.
                // Inline code is the one style the toolbar does not light at the outer edge
                // (LiveInlineCodeAt): on a revealed line the character stays after its closer.
                if (!blank) {
                    // On a revealed line the inner edge is already the spot, and a caret strictly
                    // inside a marker is the user editing the marker itself.
                    if (!Options.HideMarkers && (relative <= start || InsideCloserMarker(info, relative))) return;
                    Editor.TextArea.Caret.Offset = line.Offset + TypingInsideTarget(info, start, end, relative, !Options.HideMarkers);
                    return;
                }
                // Whitespace on a closing edge stays outside the emphasis pair: a space just
                // inside the closer ("**ab **") is not right-flanking, so the parser draws both
                // markers as text. It may stay inside an enclosing tag, which takes it.
                int target = ClosePairEnd(info, start, end);
                // 11-01 user decision (bold), 11-05 extended to italic, strike, bold+italic and the
                // size/colour tags: a space typed right after a CLOSED run (the caret at or past the
                // run's own closer) is plain, and so is the word after it, so the word-by-word
                // continuation (which would carry both back inside the closer) is not armed there;
                // the toolbar shows the style off after that space. The space stays where the caret
                // is, so after a closed tag it is outside the tag too. A caret before the closer
                // (inside the pair, the run being typed) keeps the continuation.
                // A space typed right after a closing-edge merge (continuingWord: the caret sits on the
                // merged run's outer edge, where the no-reveal view stops) is still part of the style
                // being typed, exactly like the raw line where the caret stays inside the pair.
                int closedAt = ClosedRunTypingEnd(info, start, end);
                bool plainAfterClosed = !continuingWord && closedAt >= 0 && relative >= closedAt;
                if (plainAfterClosed) target = relative;
                if (blank && target > start && !plainAfterClosed && !Options.ReadOnly && !Editor.IsReadOnly) {
                    // The space will land after the emphasis closers; remember that run and the
                    // spot so the next non-space character can take both back inside (see
                    // HiddenCloseSpaceEntered/HiddenCloseSpaceEntering). The 11-01 rule above
                    // only decides where a non-blank input at the closed edge lands; the space's
                    // own word-by-word continuation keeps its existing behaviour.
                    closeSpaceArm = true;
                    closeSpaceArmStart = line.Offset + start;
                    closeSpaceArmEnd = line.Offset + target;
                    closeSpaceArmMarkers = info.Text.Substring(start, target - start);
                }
                Editor.TextArea.Caret.Offset = line.Offset + target;
                return;
            }
            // A space typed strictly between the characters of a hidden opening run splits its
            // markers ("* *굵게**"), which turns the pair literal in every view mode. The run is
            // one unit here exactly as Enter's H9 rule treats it, so the space goes to the run's
            // outer edge. Only the run's own interior is moved; a space at its outer edge follows
            // the hidden-line rule below.
            if (blank) {
                Replacement opener = HiddenOpenerMarkerAt(info, relative);
                if (opener.Length > 0) { Editor.TextArea.Caret.Offset = line.Offset + opener.Start; return; }
            }
            // A space typed right after an opening emphasis marker goes in front of it for the
            // same reason ("** ab**" is literal; " **ab**" is still a pair); a tag opener keeps
            // the space inside it. Only on a line where the markers are hidden: on a revealed
            // (raw) line the opener is real text, so the space goes at the caret (the same reason
            // SnapHiddenCaret and HiddenSelectionEntering skip raw lines). Real bug: live, line
            // `**<span style="font-size:18px">x</span>`, caret offset 2, Space landed at 31.
            if (blank && !Raw(line) && HiddenOpenRun(info, relative, out start, out end))
                Editor.TextArea.Caret.Offset = line.Offset + OpenPairStart(info, start, end);
        }
        // The space typed on a closing edge is now in the document, just after the emphasis
        // closers (an enclosing tag is still behind it). Pending is only armed when the caret
        // moved by exactly that space and the run it left is still there, so a handled input or
        // an unexpected insertion never arms it.
        void HiddenCloseSpaceEntered(object sender, TextCompositionEventArgs e) {
            if (!closeSpaceArm) return;
            closeSpaceArm = false;
            if (disposed || !CloseSpaceAllowed() || Options.Busy) return;
            if (e.Handled || e.Text == null || e.Text.Length == 0 || !BlankText(e.Text)) return;
            int start = closeSpaceArmStart, end = closeSpaceArmEnd;
            int caret = Editor.TextArea.Caret.Offset;
            if (start < 0 || end < start || end > Document.TextLength || caret != end + e.Text.Length) return;
            if (Document.GetText(start, end - start) != closeSpaceArmMarkers) return;
            if (Document.GetText(end, e.Text.Length) != e.Text) return;
            closeSpacePending = true;
            closeSpaceStart = start; closeSpaceEnd = end; closeSpaceCaret = caret;
            closeSpaceMarkers = closeSpaceArmMarkers; closeSpaceSpaces = e.Text;
            closeSpaceArmMarkers = null;
            // The next character goes back inside the pair: the toolbar shows the style lit now.
            Editor.NotifyToolsChanged();
        }
        // The next input after that space. More whitespace joins the run the move will take; the
        // first non-space character takes the whole run back inside the pair with itself after it,
        // in ONE document update (one undo step): "**alpha** " + "b" becomes "**alpha b**", and
        // "<span ...>**alpha** </span>" + "b" keeps both the bold and the size. A newline, a
        // moved caret or any other edit drops the state instead.
        void HiddenCloseSpaceEntering(object sender, TextCompositionEventArgs e) {
            if (!closeSpacePending) return;
            if (disposed || !CloseSpaceAllowed() || Options.Busy) { closeSpacePending = false; return; }
            if (e.Handled) { closeSpacePending = false; return; }
            if (e.Text == null || e.Text.Length == 0) return;
            if (!Editor.TextArea.Selection.IsEmpty) { closeSpacePending = false; return; }
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) { closeSpacePending = false; return; }
            if (Editor.TextArea.Caret.Offset != closeSpaceCaret) { closeSpacePending = false; return; }
            int at = Editor.TextArea.Caret.Offset;
            // Whitespace here only extends the pending gap (DocumentChanged does that). It must not
            // open an update: an empty RunUpdate still raises UpdateStarted/UpdateFinished, whose
            // typing-run bookkeeping would be spent on the space about to land, and that space
            // would be recorded as an edit outside the typing run.
            if (BlankText(e.Text)) return;
            // Everything InsertCloseSpaceCommit checks, before the update is opened for the same
            // reason: a refused input must not leave an empty update behind.
            if (!CloseSpaceCommitReady(at, e.Text)) { closeSpacePending = false; return; }
            // Keep the moved space, inserted text AND final caret in the same history group.
            // On the revealed line the markers are real characters, so the caret belongs inside
            // the pair (right after the text); Basic draws the two edges on one spot and keeps
            // the existing outer-edge stop.
            int placed = Options.HideMarkers ? at + e.Text.Length : closeSpaceStart + closeSpaceSpaces.Length + e.Text.Length;
            using (Document.RunUpdate()) {
                if (!InsertCloseSpaceCommit(at, e.Text)) return;
                Editor.TextArea.Caret.Offset = placed;
            }
            Editor.EndTypingRun();
            SafeBringCaretToView(Editor.TextArea);
            e.Handled = true;
            // A handled TextEntering has no TextEntered, but SourceEditor.ImeResultEntering has
            // already taken this text out of any counted IME result before this handler ran.
        }
        // The closing-edge space rule is not Basic-only: Info() parses a revealed caret line the
        // same way, so the space typed right before a closing run also lands outside it on the
        // Live view's raw line. Only an edit surface takes part (a read-only body never reveals).
        bool CloseSpaceAllowed() {
            return Options.HideMarkers || (!Options.ReadOnly && !Editor.IsReadOnly);
        }
        // The state InsertCloseSpaceCommit needs, read without side effects: the pending run is
        // still the text at the caret and the input can be written there.
        bool CloseSpaceCommitReady(int at, string text) {
            if (disposed || !CloseSpaceAllowed() || Options.Busy || Editor.IsReadOnly || Editor.Document != Document ||
                at != closeSpaceCaret || String.IsNullOrEmpty(text) || text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0) return false;
            return PendingCloseSpaceIntact();
        }
        // The pending marker run and the whitespace already moved past it are still the exact
        // text between the remembered offsets and the caret.
        bool PendingCloseSpaceIntact() {
            if (closeSpaceStart < 0 || closeSpaceEnd < closeSpaceStart || closeSpaceCaret < closeSpaceEnd ||
                closeSpaceCaret > Document.TextLength) return false;
            return Document.GetText(closeSpaceStart, closeSpaceEnd - closeSpaceStart) == closeSpaceMarkers &&
                Document.GetText(closeSpaceEnd, closeSpaceCaret - closeSpaceEnd) == closeSpaceSpaces;
        }
        // B1: a style toggle clicked while the closing-edge space is still pending means "this
        // word ends here", not "open a second pair". The toggle edits nothing: the pending state
        // ends, the space stays outside the run and the next character lands after it. Only a
        // toggle for one of the run's own closing markers is taken (an italic toggle does not
        // consume a bold run, whose "**" is a single marker of its own).
        internal bool EndPendingCloseStyle(string marker) {
            if (!closeSpacePending || String.IsNullOrEmpty(marker)) return false;
            if (disposed || Options.Busy || Editor.IsReadOnly || Editor.Document != Document) return false;
            if (!Editor.TextArea.Selection.IsEmpty) return false;
            if (Editor.TextArea.Caret.Offset != closeSpaceCaret) return false;
            if (!PendingCloseSpaceIntact()) return false;
            DocumentLine line = Document.GetLineByOffset(closeSpaceStart);
            int lineOffset = line.Offset;
            LineInfo info = Info(line);
            bool owns = false;
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Kind != ReplacementKind.Hidden || replacement.Length <= 0 || replacement.Prefix) continue;
                int start = lineOffset + replacement.Start, end = start + replacement.Length;
                if (start < closeSpaceStart || end > closeSpaceEnd) continue;
                if (!HiddenCloser(info, replacement)) continue;
                if (!String.Equals(Document.GetText(start, replacement.Length), marker, StringComparison.Ordinal)) continue;
                owns = true;
                break;
            }
            if (!owns) return false;
            closeSpacePending = false;
            closeSpaceArm = false;
            return true;
        }
        // Shared by normal TextEntering and SourceEditor's redirected/late commit insertion.
        // It reads just the pending marker run on the edited line and never moves another caret.
        bool InsertCloseSpaceCommit(int at, string text) {
            if (!closeSpacePending) return InsertCommitInsideRun(at, text);
            if (!CloseSpaceCommitReady(at, text)) {
                closeSpacePending = false;
                return false;
            }
            // Whitespace alone extends the pending gap through DocumentChanged.
            if (BlankText(text)) return false;
            int start = closeSpaceStart;
            string replacement = closeSpaceSpaces + text + closeSpaceMarkers;
            closeSpacePending = false;
            using (Document.RunUpdate()) Document.Replace(start, at - start, replacement);
            // The moved markers put the no-reveal caret on the run's outer edge; the next
            // character of the same word belongs inside the pair (HiddenCloseTypingEntering reads
            // this mark before the 11-01 plain rule).
            if (Options.HideMarkers) {
                closeWordCaret = start + replacement.Length;
                closeWordDocument = Document;
                closeWordVersion = Document.Version;
            }
            return true;
        }
        // 11-05: an IME result that bypasses TextEntering (a click/late commit written at the
        // composition point) follows the same rule as a typed character: at a closing run's edge,
        // where the toolbar shows the style lit, the text is written inside the pair. False leaves
        // the plain insertion at `at` to the caller, which also shifts its caret past the text.
        bool InsertCommitInsideRun(int at, string text) {
            if (disposed || !CloseSpaceAllowed() || Options.Busy || Editor.IsReadOnly || Editor.Document != Document) return false;
            if (String.IsNullOrEmpty(text) || BlankText(text) || text.IndexOf('\r') >= 0 || text.IndexOf('\n') >= 0) return false;
            if (at < 0 || at > Document.TextLength) return false;
            DocumentLine line = Document.GetLineByOffset(at);
            LineInfo info = Info(line);
            int relative = at - line.Offset;
            if (info.Table && info.Cells != null && AtCellEnd(info, relative)) return false;
            int start, end;
            if (!HiddenCloseRun(info, relative, out start, out end)) return false;
            if (!Options.HideMarkers && (relative <= start || InsideCloserMarker(info, relative))) return false;
            int target = TypingInsideTarget(info, start, end, relative, !Options.HideMarkers);
            if (target >= relative) return false;
            Document.Insert(line.Offset + target, text);
            return true;
        }
        static bool BlankText(string text) {
            if (String.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++) if (!char.IsWhiteSpace(text[i])) return false;
            return true;
        }
        // Whitespace that can be part of a word gap: a newline ends the line, so it ends the
        // pending closing-edge state instead of extending it.
        static bool SpaceOnly(string text) {
            if (String.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++) {
                char c = text[i];
                if (c == '\r' || c == '\n' || !char.IsWhiteSpace(c)) return false;
            }
            return true;
        }
        // A character typed in front of the row's leading pipe would be written outside the
        // table and drop the row out of it. That position belongs to the first cell, so the
        // caret steps into that cell before the input lands; the padding after the pipe is the
        // first cell itself (an empty cell's own stop is its following pipe), so it stays.
        void TableRowEntering(object sender,TextCompositionEventArgs e) {
            if (disposed || e.Handled || Options.Busy) return;
            if (e.Text==null || e.Text.Length==0) return;
            if (!Editor.TextArea.Selection.IsEmpty) return;
            var caret=Editor.TextArea.Caret;
            if (caret.Line<1 || caret.Line>Document.LineCount) return;
            DocumentLine line=Document.GetLineByNumber(caret.Line);
            LineInfo info=Info(line);
            if (!info.Table || info.Delimiter || info.Cells==null || info.Cells.Count==0) return;
            if (caret.Offset-line.Offset>RowPipeIndex(Document,line)) return;
            caret.Offset=line.Offset+info.Cells[0].Start;
        }
        // H1: a "|" typed inside a table row's cell is cell text, not a new column, so it is
        // written as "\|" in one undo step; the caret stays after it. Inside a closed `code`
        // span it stays raw instead (TablePipes skips the span and ParseInline shows its text
        // literally), exactly the paste rule MarkdownTableCommands.CellInsertText applies, so
        // typing and paste agree. Outside a row the key is the editor's own input. A selection
        // that stays inside one cell is replaced whole.
        void TablePipeTyping(object sender,TextCompositionEventArgs e) {
            if (disposed || e.Handled || Options.Busy || Editor.IsReadOnly || Editor.Document != Document) return;
            if (e.Text!="|") return;
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            int start,length;
            if (Editor.TextArea.Selection.IsEmpty) { start=Editor.CaretOffset;length=0; }
            else {
                ISegment segment=Editor.TextArea.Selection.SurroundingSegment;
                if (segment==null || segment.Length<=0) return;
                start=segment.Offset;length=segment.Length;
                int firstLine=Document.GetLineByOffset(start).LineNumber;
                int lastLine=Document.GetLineByOffset(Math.Min(segment.EndOffset,Document.TextLength)).LineNumber;
                if (firstLine!=lastLine) return;
            }
            if (start<0 || start>Document.TextLength) return;
            DocumentLine line=Document.GetLineByOffset(start);
            LineInfo info=Info(line);
            // The delimiter row is structure, not cell text: an inserted pipe would end the
            // table. Column edits come from the table tool row, so the key is swallowed.
            if (info.Table && info.Delimiter) { e.Handled=true; return; }
            if (!info.Table || info.Cells==null || info.Cells.Count==0) return;
            int relative=start-line.Offset;
            // A caret in the padding around a cell (after a separator pipe or before one) still
            // writes that cell's text: the space typed at a cell's end, or the offset a delete
            // that emptied the cell left, is the cell's own. Without this a "|" typed there
            // became a new column.
            int cell=TypingCellAt(info,relative);
            if (cell<0) return;
            if (length>0 && start+length-line.Offset>info.Cells[cell].End) return;
            string insert=MarkdownTableCommands.CellInsertText(Document,start,length,"|",info.Cells);
            Document.BeginUpdate();
            try {
                Document.Replace(start,length,insert);
            } finally { Document.EndUpdate(); }
            Editor.Select(start+insert.Length,0);
            e.Handled=true;
        }
        // fix13: a paragraph written directly under a table row is read back as one more row by
        // other Markdown parsers (the live view draws it as a paragraph), and text written directly
        // above a header row joins the table's own block. The empty line between the table and the
        // user's text is the separator both sides need, so the first text typed on it keeps the
        // line: one newline goes in with the text, in one document update. The update opens here
        // and the typed text lands inside it, then TextEntered closes it, so one undo takes the
        // newline and the text back together. Cells, non-empty lines, code, a selection and a
        // busy or read-only surface keep the editor's own input.
        void TableGapEntering(object sender,TextCompositionEventArgs e) {
            if (disposed || e.Handled) return;
            if (gapUpdate) CloseTableGap();
            if (Options.Busy || Options.ReadOnly || Editor.IsReadOnly) return;
            if (e.Text==null || e.Text.Length==0) return;
            if (e.Text=="\r" || e.Text=="\n" || e.Text=="\r\n") return;
            if (!Editor.TextArea.Selection.IsEmpty) return;
            int offset=Editor.TextArea.Caret.Offset;
            string newline;
            int mode=TableGapMode(offset,out newline);
            if (mode==0) return;
            gapUpdate=true;gapNewline=newline;gapAbove=mode==2;gapDocument=Document;
            Document.BeginUpdate();
            // This update spans the input event and is normally closed by TextEntered. TextEntered
            // is not raised when a TextEntering handler registered after this one consumes the
            // input, nor when the editor's own insert throws; an update left open keeps
            // UpdateStarted/UpdateFinished from ever pairing again, so DocumentHistory stops
            // recording entries for the rest of the session (the monkey's undo oracle then cannot
            // reach the segment start). The posted close is the guarantee: it runs after this
            // input event finishes and is a no-op when TextEntered already closed the update.
            Editor.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(CloseTableGap));
            try {
                // Below a row the newline opens the line the text goes on; the editor's empty
                // selection is a pair of fixed offsets, so it is re-made after the new line.
                if (mode==1) { Document.Insert(offset,newline); Editor.Select(offset+newline.Length,0); }
            } catch (Exception) {
                CloseTableGap();
                throw;
            }
        }
        // Above a header row the typed text is already in the document, so the newline goes after
        // it and the caret stays on the text. Either way the update from TableGapEntering ends here.
        void TableGapEntered(object sender,TextCompositionEventArgs e) {
            if (!gapUpdate) return;
            bool above=gapAbove;
            try {
                if (above && e.Text!=null && e.Text.Length>0) {
                    int at=Editor.TextArea.Caret.Offset;
                    if (at>=0 && at<=Document.TextLength) { Document.Insert(at,gapNewline);Editor.Select(at,0); }
                }
            } finally { CloseTableGap(); }
        }
        void CloseTableGap() {
            if (!gapUpdate) return;
            gapUpdate=false;gapAbove=false;gapNewline=null;
            TextDocument document=gapDocument;gapDocument=null;
            if (document!=null) document.EndUpdate();
        }
        // 0: an ordinary line, no separator to keep. 1: the empty line under a table row, newline
        // BEFORE the text. 2: the empty line above a header row, newline AFTER the text. A line
        // between two tables answers 2, which leaves one blank line on each side.
        internal int TableGapMode(int offset,out string newline) {
            newline=null;
            if (disposed || offset<0 || offset>Document.TextLength) return 0;
            DocumentLine line=Document.GetLineByOffset(offset);
            if (line.Length!=0 || offset!=line.Offset) return 0;
            if (Info(line).Code) return 0;
            DocumentLine next=line.NextLine;
            bool above=next!=null && Info(next).Header;
            if (!above) {
                DocumentLine previous=line.PreviousLine;
                if (previous==null || !Info(previous).Table) return 0;
            }
            newline=LineNewline(line);
            return above ? 2 : 1;
        }
        // The empty line's own delimiter, else the nearest earlier one; the same rule the
        // structural and table commands use, so the new line matches the document.
        string LineNewline(DocumentLine line) {
            if (line.DelimiterLength>0) return Document.GetText(line.EndOffset,line.DelimiterLength);
            DocumentLine previous=line.PreviousLine;
            if (previous!=null && previous.DelimiterLength>0) return Document.GetText(previous.EndOffset,previous.DelimiterLength);
            return "\n";
        }
        // Bug A: a typed character on the blank line directly above a dash underline ("---") turned
        // "text" + "---" into a setext heading, so the typed line took the heading scale and the
        // rule disappeared. Word and Notion keep the rule, so an empty line is written between the
        // text and the underline, in the same update as the typed text (one undo step). Only a
        // dash underline takes part: an "=" line is no thematic break, and a blank line under one
        // has always read as a heading. The cheap guards come first - a typing keystroke on an
        // ordinary line stops at the next line's first character and never copies a line.
        void SetextRuleEntering(object sender,TextCompositionEventArgs e) {
            if (disposed || e.Handled) return;
            if (setextGapUpdate) CloseSetextRule();
            if (Options.Busy || Options.ReadOnly || Editor.IsReadOnly) return;
            if (Editor.Document!=Document) return;
            if (e.Text==null || e.Text.Length==0) return;
            if (e.Text.IndexOf('\r')>=0 || e.Text.IndexOf('\n')>=0) return;
            if (BlankText(e.Text)) return;
            if (!Editor.TextArea.Selection.IsEmpty) return;
            int offset=Editor.TextArea.Caret.Offset;
            if (offset<0 || offset>Document.TextLength) return;
            DocumentLine line=Document.GetLineByOffset(offset);
            DocumentLine next=line.NextLine;
            if (next==null || next.Length>ParseLimit || !SetextUnderlineStart(Document,next)) return;
            if (!SetextBlankLine(Document,line)) return;
            if (SetextUnderlineLevel(ShortText(Document,next))!=2) return;
            // Inside a fenced block "---" is code, not an underline.
            if (Info(line).Code) return;
            setextGapUpdate=true;setextGapNewline=LineNewline(line);setextGapDocument=Document;
            Document.BeginUpdate();
            // The same guarantee the table gap makes: a TextEntering handler after this one can
            // consume the input (no TextEntered), and an update left open stops the history from
            // recording anything for the rest of the session.
            Editor.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(CloseSetextRule));
        }
        // The typed text is in the document and the caret is after it: put the empty line at the
        // end of that line, so the text stays a paragraph and the underline below stays a rule.
        // The shape is read once more here: a redirected input (a late IME commit) can land its
        // text somewhere else, and the line must really have become text for this rule.
        void SetextRuleEntered(object sender,TextCompositionEventArgs e) {
            if (!setextGapUpdate) return;
            try {
                if (!Object.ReferenceEquals(Document,setextGapDocument)) return;
                int at=Math.Max(0,Math.Min(Editor.TextArea.Caret.Offset,Document.TextLength));
                DocumentLine line=Document.GetLineByOffset(at);
                DocumentLine next=line.NextLine;
                if (next==null || next.Length>ParseLimit || !SetextUnderlineStart(Document,next)) return;
                if (SetextUnderlineLevel(ShortText(Document,next))!=2) return;
                if (SetextBlankLine(Document,line)) return;
                // 11-01 user: an empty line pushed in under the typed text kept the rule one line
                // away, and every new line typed there pushed it again. A rule of three or more
                // dashes is rewritten with "*" instead ("***" draws the same rule and can never
                // underline a heading), so the text sits right above it with no extra line.
                string rule=Document.GetText(next.Offset,next.Length);
                int dashes=0;
                for (int i=0;i<rule.Length;i++) if (rule[i]=='-') dashes++;
                if (dashes>=3) Document.Replace(next.Offset,next.Length,rule.Replace('-','*'));
                else Document.Insert(line.EndOffset,setextGapNewline);
                Editor.Select(at,0);
            } finally { CloseSetextRule(); }
        }
        void CloseSetextRule() {
            if (!setextGapUpdate) return;
            setextGapUpdate=false;setextGapNewline=null;
            TextDocument document=setextGapDocument;setextGapDocument=null;
            if (document!=null) document.EndUpdate();
        }
        // 11-04 user (video): "```" typed on an empty line turned the line into a code block, but
        // the opening fence is a hidden line in the basic view, so the caret guard
        // (FenceCaretTarget) moved the caret to the end of the line above and the next text or
        // picture landed there. 11-05 user decision: typing the marker writes nothing extra (the
        // info string "python" is typed on that line, which stays shown as source: EditableFence),
        // and Enter at the end of an opening fence with no closing fence below writes the empty
        // body line and the closing fence (same marker, run and indent) in one update, caret on
        // the body line, in both views. One undo gives back the "```python" line with the caret at
        // its end. The window's own Enter rules (lists, tables) run on the TextArea's
        // PreviewKeyDown; this rule is on the editor itself, which the tunnelling key reaches first.
        void FenceEnterKeyDown(object sender,KeyEventArgs e) {
            if (disposed || e.Handled || e.Key!=Key.Enter || Keyboard.Modifiers!=ModifierKeys.None) return;
            if (!Object.ReferenceEquals(e.OriginalSource,Editor.TextArea)) return;
            if (FenceEnter()) e.Handled=true;
        }
        // The newline text path (an IME's Enter, a newline typed by a probe) takes the same rule;
        // Shift+Enter stays the plain line break.
        void FenceEnterEntering(object sender,TextCompositionEventArgs e) {
            if (disposed || e.Handled) return;
            if (e.Text!="\n" && e.Text!="\r" && e.Text!="\r\n") return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift)!=0) return;
            if (FenceEnter()) e.Handled=true;
        }
        bool FenceEnter() {
            if (Options.Busy || Options.ReadOnly || Editor.IsReadOnly || Editor.ImeBusy) return false;
            if (Editor.Document!=Document || !Editor.TextArea.Selection.IsEmpty) return false;
            int offset=Editor.TextArea.Caret.Offset;
            if (offset<0 || offset>Document.TextLength) return false;
            DocumentLine line=Document.GetLineByOffset(offset);
            if (offset!=line.EndOffset || line.Length>ParseLimit) return false;
            string text=Document.GetText(line.Offset,line.Length);
            int lead=0;
            while (lead<text.Length && lead<4 && text[lead]==' ') lead++;
            if (lead>3 || lead>=text.Length) return false;
            char marker=text[lead];
            if (marker!='`' && marker!='~') return false;
            int end=lead;
            while (end<text.Length && text[end]==marker) end++;
            int run=end-lead;
            if (run<3) return false;
            // A backtick fence's info string may not hold a backtick (CommonMark): "```a`b" is text.
            if (marker=='`' && text.IndexOf('`',end)>=0) return false;
            // Inside a fenced block the line is code or that block's closing fence.
            LineInfo info=Info(line);
            if (!info.FenceLine || info.Before.Fence!='\0') return false;
            if (FenceCloserBelow(line,marker,run)!=0) return false;
            string newline=LineNewline(line);
            Editor.EndTypingRun();
            Document.BeginUpdate();
            try {
                Document.Insert(line.EndOffset,newline+newline+text.Substring(0,lead)+new string(marker,run));
                Editor.Select(offset+newline.Length,0);
            } finally { Document.EndUpdate(); }
            Editor.TextArea.Caret.BringCaretToView();
            return true;
        }
        // 1 when a closing fence for this run follows within the bounded scan, 0 when the block
        // runs open to the document's end, -1 when the scan stopped before the end. Read only on
        // Enter at an opening fence and for the caret's own fence line (EditableFence, cached per
        // document version), reading the first characters of each following line.
        int FenceCloserBelow(DocumentLine line,char marker,int run) {
            DocumentLine next=line.NextLine;
            int scanned=0;
            while (next!=null) {
                if (++scanned>400) return -1;
                char fence;int length;bool tailBlank;
                ReadFence(Document,next,out fence,out length,out tailBlank);
                if (fence==marker && length>=run && tailBlank) return 1;
                next=next.NextLine;
            }
            return 0;
        }
        // An opening fence that no line below closes is the line being typed ("```" and then its
        // info string, before Enter writes the block). It is not structure yet: the basic view
        // shows it as source while the caret is on it (Raw) and every hidden-fence caret rule
        // leaves the caret on it, so typed or pasted text goes on that line, never on the line
        // above. A scan that stops before the document's end counts as open here (showing one
        // fence line as source is harmless; hiding the line being typed is the bug).
        DocumentLine editableFenceLine;
        object editableFenceVersion;
        bool editableFenceAnswer;
        internal bool EditableFence(DocumentLine line) {
            if (line==null || line.IsDeleted || Options.ReadOnly || Editor.IsReadOnly) return false;
            object version=Document.Version;
            if (Object.ReferenceEquals(line,editableFenceLine) && version!=null && Object.ReferenceEquals(version,editableFenceVersion)) return editableFenceAnswer;
            bool answer=false;
            LineInfo info=Info(line);
            if (info.FenceLine && info.Before.Fence=='\0') {
                char fence;int length;bool tailBlank;
                ReadFence(Document,line,out fence,out length,out tailBlank);
                answer=fence!='\0' && FenceCloserBelow(line,fence,length)!=1;
            }
            editableFenceLine=line;editableFenceVersion=version;editableFenceAnswer=answer;
            return answer;
        }
        // The no-reveal mode draws a fence line as nothing but keeps the height of a text line,
        // so a click in that blank band can name an offset inside the fence's own source.
        // SnapHiddenCaret normally moves the caret out first; this is the last guard for an
        // offset set while a press froze the reveal set or by a path without caret
        // notifications. The input is redirected to the neighbouring line the caret came from,
        // and swallowed when the document holds nothing but the fence.
        void HiddenFenceEntering(object sender,TextCompositionEventArgs e) {
            if (disposed || e.Handled || !Options.HideMarkers || Options.Busy || Editor.IsReadOnly) return;
            if (e.Text==null || e.Text.Length==0) return;
            if (!Editor.TextArea.Selection.IsEmpty) return;
            int offset=Math.Max(0,Math.Min(Editor.CaretOffset,Document.TextLength));
            DocumentLine line=Document.GetLineByOffset(offset);
            LineInfo info=Info(line);
            if (!info.FenceLine || Raw(line) || EditableFence(line)) return;
            int target=FenceCaretTarget(line,lastActiveLine<=line.LineNumber);
            if (target==Editor.CaretOffset) { e.Handled=true; return; }
            Editor.TextArea.Caret.Offset=target;
            RevalidateCaretColumn(Editor.TextArea);
        }
        void HiddenPasteWiden(object sender, ExecutedRoutedEventArgs e) {
            if (disposed || e.Handled || !Options.HideMarkers || Options.Busy) return;
            if (e.Command != ApplicationCommands.Paste) return;
            // A paste over a selection that holds only one fence line of a block is swallowed:
            // replacing it would leave the partner fence behind (the typing and delete rules).
            if (SelectionBreaksFence()) { e.Handled = true; return; }
            PrepareHiddenPaste();
        }
        // Stage 3d: a selection follows the single-line rule however many lines it spans, and a
        // caret that sits inside a marker (an offset set by undo, search or a command) is pulled
        // to a boundary first, so a paste can never split a marker. Also the host's paste seam:
        // the window's Ctrl+V (LiveMarkdownCommands.LivePaste) replaces SelectedText directly and
        // never runs ApplicationCommands.Paste, so it must call this before its insertion.
        internal void PrepareHiddenPaste() {
            if (disposed || !Options.HideMarkers || Options.Busy) return;
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return;
            if (Editor.TextArea.Selection.IsEmpty) { SnapHiddenPasteCaret(); return; }
            WidenHiddenSelection();
            WidenHiddenPairs();
        }
        // The paste-side caret snap: text pasted at an offset strictly inside a hidden marker
        // would land between its characters, so the caret moves to the nearest visible edge.
        void SnapHiddenPasteCaret() {
            int offset = Editor.TextArea.Caret.Offset;
            int snapped = SnapInsertOffset(offset);
            if (snapped == offset) return;
            Editor.TextArea.Caret.Offset = snapped;
            // Same rule as the other caret moves: a caller that measures the caret right after
            // this seam must not see the move's invalidated visual column.
            RevalidateCaretColumn(Editor.TextArea);
        }
        // The offset an insertion at `offset` must use instead: inside a hidden marker it moves to
        // the marker's edge, on a closing run's outer edge to its inside, and on a hidden fence
        // line to the neighbouring line. A drop point on a line's left margin is line.Offset, so
        // this is also what keeps an internal drag from landing in front of a list/heading prefix.
        // Ctrl+V reads it through SnapHiddenPasteCaret; the drag move reads it for its drop point.
        internal int SnapInsertOffset(int offset) {
            if (disposed || !Options.HideMarkers) return offset;
            if (offset < 0 || offset > Document.TextLength) return offset;
            DocumentLine line = Document.GetLineByOffset(offset);
            LineInfo info = Info(line);
            // A hidden fence line draws as one invisible run, so the marker snap below would
            // leave the caret at its start or end - still on the fence. A paste there rewrites
            // the fence line, so it goes to the neighbouring line instead (task F2).
            if (info.FenceLine && EditableFence(line)) return offset;
            if (info.FenceLine && !Raw(line)) return FenceCaretTarget(line, lastActiveLine <= line.LineNumber);
            // A paste on the outer edge of a closing run lands inside the pair, so the pasted
            // run keeps the bold or the span size. Typed text is different on a bold pair's own
            // outer edge since 11-01 (HiddenCloseTypingEntering leaves it outside); the paste
            // rule was not part of that decision and stays as it is.
            int inside = HiddenCloseInside(info, offset - line.Offset);
            if (inside >= 0) return line.Offset + inside;
            int snap = HiddenSnap(info, offset - line.Offset);
            return snap < 0 ? offset : line.Offset + snap;
        }
        // Rectangle (Alt+drag) selections have one segment per line, so the endpoint widening
        // below cannot keep every marker whole while they are hidden. Stage 3d turns rectangle
        // selection off while markers are hidden; a rectangle made before the switch becomes an
        // ordinary selection whose two ends then take the same widening rule typing and paste use.
        // `markersTurnedOn` is the update that enabled the no-reveal mode: only that transition has
        // a caret or a rectangle left over from the revealed view.
        void ApplyHideMarkersInteraction(bool markersTurnedOn) {
            Editor.Options.EnableRectangularSelection = !Options.HideMarkers;
            if (!markersTurnedOn || !Options.HideMarkers) return;
            var selection = Editor.TextArea.Selection;
            if (selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) {
                ISegment segment = selection.SurroundingSegment;
                int start = segment == null ? Editor.TextArea.Caret.Offset : segment.Offset;
                int end = segment == null ? start : segment.EndOffset;
                if (end < start) { int swap = start; start = end; end = swap; }
                Editor.TextArea.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, start, end);
                // The rectangle's edges are visual columns measured on the revealed layout, so the
                // plain span they become starts and ends inside the marker runs the surface now
                // hides (an Alt+drag endpoint inside the second `*` of `**`). An endpoint inside a
                // hidden marker would be half a marker in the selection the next paste or delete
                // replaces, so both ends widen out to their markers now; the pairs the span merely
                // cuts stay whole until input lands in them, like any other selection.
                WidenHiddenSelection();
            }
            // Switching to the no-reveal mode does not move the caret or change any offset, so
            // no caret notification runs the marker rules. A caret the live view left inside a
            // now-hidden marker would split the pair with the next typed character or paste, so
            // it is pulled out once here, before the next input. A selection is left as the user
            // made it; the input-time rules (HiddenSelectionEntering/HiddenPasteWiden) widen its
            // endpoints when the replacement actually lands.
            if (Editor.TextArea.Selection.IsEmpty) SnapHiddenCaret();
        }
        // Widens a selection endpoint that sits inside a marker out to that marker's edge: the
        // start to the marker's start, the end to its end. A leading prefix is one marker run, so
        // an endpoint inside it takes the whole prefix instead of half of it.
        void WidenHiddenSelection() {
            ISegment segment = Editor.TextArea.Selection.SurroundingSegment;
            if (segment == null || segment.Length <= 0) return;
            int start = segment.Offset, end = segment.EndOffset;
            // An endpoint inside a marker only ever widens outward: the start to the marker's
            // own start, the end to its end. The clamps keep that contract even for a rule that
            // answers inward (the selection can then never shrink or collapse here).
            int newStart = Math.Min(start, WidenStart(start)), newEnd = Math.Max(end, WidenEnd(end));
            if (newStart == start && newEnd == end) return;
            if (newStart < 0) newStart = 0;
            if (newEnd > Document.TextLength) newEnd = Document.TextLength;
            if (newEnd <= newStart) return;
            bool caretAtStart = Editor.CaretOffset <= start;
            // SimpleSelection remembers its first position as the anchor for shift-extension.
            // A caret that sat at the start keeps the anchor at the far end, so the selection
            // is created from that end; otherwise the next Shift+arrow grows from the wrong side.
            Editor.TextArea.Selection = caretAtStart
                ? ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, newEnd, newStart)
                : ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, newStart, newEnd);
            Editor.TextArea.Caret.Offset = caretAtStart ? newStart : newEnd;
            RevalidateCaretColumn(Editor.TextArea);
        }
        // The second half of the input-time rule: every inline pair the selection cuts through is
        // taken whole. A selection that covers one marker but not its partner would otherwise
        // leave the partner as literal text on screen, or pair it with an unrelated later marker.
        // Runs after WidenHiddenSelection, so an endpoint inside a marker already reached its edge.
        // Only the no-reveal mode hides the markers this protects; in the live view the selected
        // lines draw their own source and a selection over half a marker is visible text.
        void WidenHiddenPairs() {
            if (!Options.HideMarkers) return;
            ISegment segment = Editor.TextArea.Selection.SurroundingSegment;
            if (segment == null || segment.Length <= 0) return;
            int start = segment.Offset, end = segment.EndOffset;
            int newStart, newEnd;
            if (!WidenMarkerPairs(start, end, out newStart, out newEnd)) return;
            if (newStart < 0) newStart = 0;
            if (newEnd > Document.TextLength) newEnd = Document.TextLength;
            if (newEnd <= newStart) return;
            bool caretAtStart = Editor.CaretOffset <= start;
            Editor.TextArea.Selection = caretAtStart
                ? ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, newEnd, newStart)
                : ICSharpCode.AvalonEdit.Editing.Selection.Create(Editor.TextArea, newStart, newEnd);
            Editor.TextArea.Caret.Offset = caretAtStart ? newStart : newEnd;
            RevalidateCaretColumn(Editor.TextArea);
        }
        // The drag source half of the paste rule: an in-document move takes the text the reader
        // sees, so a selection that cuts a hidden pair, or that is a pair's whole visible text,
        // carries the markers with it (no "****" left behind, no half marker inserted elsewhere).
        // The range has no live selection, so the caret rules above cannot be read; the range
        // forms are shared with them. Only the no-reveal mode needs this.
        internal bool WidenMoveRange(int start,int length,out int from,out int to) {
            from=start; to=start+length;
            if (disposed || !Options.HideMarkers || Options.Busy) return false;
            if (Document==null || start<0 || length<=0 || start+length>Document.TextLength) return false;
            if (Editor.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection) return false;
            bool grew=false;
            int a,b;
            if (WidenMarkerPairs(from,to,out a,out b)) { from=a; to=b; grew=true; }
            DocumentLine firstLine=Document.GetLineByOffset(from);
            DocumentLine lastLine=Document.GetLineByOffset(Math.Min(to,Document.TextLength));
            if (to>from && to==lastLine.Offset && lastLine.PreviousLine!=null) lastLine=lastLine.PreviousLine;
            if (Object.ReferenceEquals(firstLine,lastLine)) {
                LineInfo info=Info(firstLine);
                for (int pass=0;pass<8;pass++) {
                    if (!EmptyPairAround(info,firstLine,from,to,out a,out b)) break;
                    from=a; to=b; grew=true;
                }
                if (!info.Table && OnlyVisibleTextRange(info,firstLine,from,to,out a,out b)) { from=a; to=b; grew=true; }
            }
            return grew;
        }
        int WidenStart(int offset) {
            DocumentLine line = Document.GetLineByOffset(offset);
            LineInfo info = Info(line);
            int relative = offset - line.Offset;
            // Table rows: the row projection owns pipes and padding, so the table paste rules decide.
            if (info.Table || info.Delimiter) return offset;
            // The start endpoint widens to the outer edge only. An endpoint inside the invisible
            // leading run therefore takes the whole prefix (line.Offset): pulling it forward to
            // LeadEnd would shrink the selection and could leave half of the run behind.
            if (info.LeadEnd > 0 && relative > 0 && relative < info.LeadEnd) return line.Offset;
            Replacement? marker = MarkerAt(info, relative);
            return marker.HasValue ? line.Offset + marker.Value.Start : offset;
        }
        int WidenEnd(int offset) {
            DocumentLine line = Document.GetLineByOffset(offset);
            LineInfo info = Info(line);
            int relative = offset - line.Offset;
            // Table rows: the row projection owns pipes and padding, so the table paste rules decide.
            if (info.Table || info.Delimiter) return offset;
            // A multi-line selection can end inside the last line's hidden prefix (an endpoint
            // restored by undo or set by a command); widening forward drops the whole prefix
            // instead of leaving a half marker behind.
            if (info.LeadEnd > 0 && relative > 0 && relative < info.LeadEnd) return line.Offset + info.LeadEnd;
            Replacement? marker = MarkerAt(info, relative);
            return marker.HasValue ? line.Offset + marker.Value.Start + marker.Value.Length : offset;
        }
        // The replacement range that strictly contains the offset, or null.
        static Replacement? MarkerAt(LineInfo info, int relative) {
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Length <= 0) continue;
                if (relative > replacement.Start && relative < replacement.Start + replacement.Length) return replacement;
            }
            return null;
        }
        // Grows [start,end) until every inline emphasis/code/tag pair it cuts is inside it whole.
        // A marker on its own is only half of a pair: replacing it without its partner leaves the
        // partner behind as literal text. The loop runs to a fixed depth because widening over one
        // pair can reach a nested or an enclosing one; it stops as soon as a pass changes nothing.
        // Lines that carry their own structure (a code line, a table row, a delimiter) are left to
        // their own rules; the word-delete rule asks for the cell's own pair through allowTable.
        bool WidenMarkerPairs(int start,int end,out int from,out int to) {
            return WidenMarkerPairs(start,end,false,out from,out to);
        }
        bool WidenMarkerPairs(int start,int end,bool allowTable,out int from,out int to) {
            from=start; to=end;
            bool grew=false;
            if (Document==null || start<0 || end<=start || end>Document.TextLength) return false;
            // A pair lives on one line and the selection is contiguous, so only the first and
            // last touched lines can hold a pair it cuts: every pair wholly between them is
            // selected whole, and every pair beyond them is untouched. Typing over a Ctrl+A
            // selection therefore costs two line parses, never a document walk.
            DocumentLine firstLine=Document.GetLineByOffset(from);
            DocumentLine lastLine=Document.GetLineByOffset(Math.Min(to,Document.TextLength));
            // A selection ending at a line's column 0 does not touch that line.
            if (to>from && to==lastLine.Offset && lastLine.PreviousLine!=null) lastLine=lastLine.PreviousLine;
            for (int pass=0;pass<8;pass++) {
                bool passGrew=WidenPairsOn(Info(firstLine),firstLine,allowTable,ref from,ref to);
                if (!Object.ReferenceEquals(lastLine,firstLine)) passGrew|=WidenPairsOn(Info(lastLine),lastLine,allowTable,ref from,ref to);
                if (!passGrew) break;
                grew=true;
            }
            return grew;
        }
        // One boundary line: every inline pair the range cuts through is taken whole.
        bool WidenPairsOn(LineInfo info,DocumentLine line,bool allowTable,ref int from,ref int to) {
            if (info.Code || info.Delimiter || info.SetextUnderline>0) return false;
            if (info.Table && !allowTable) return false;
            bool grew=false;
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind!=ReplacementKind.Hidden || marker.Length<=0 || marker.Prefix) continue;
                int markerStart=line.Offset+marker.Start, markerEnd=markerStart+marker.Length;
                if (from>=markerEnd || to<=markerStart) continue;
                int pair=PartnerMarkerAny(info,marker);
                if (pair<0) continue;
                Replacement other=info.Replacements[pair];
                int pairStart=line.Offset+other.Start, pairEnd=pairStart+other.Length;
                // Both markers selected: the pair goes as a whole, nothing to repair.
                if (from<=pairStart && to>=pairEnd && from<=markerStart && to>=markerEnd) continue;
                int nextFrom=Math.Min(from,Math.Min(markerStart,pairStart));
                int nextTo=Math.Max(to,Math.Max(markerEnd,pairEnd));
                if (nextFrom<from || nextTo>to) { from=nextFrom;to=nextTo;grew=true; }
            }
            return grew;
        }
        // The other marker of any inline pair, from either side (an opener finds its closer and a
        // closer its opener). Tags pair through TagPartner's own side check, so trying both
        // directions answers for every marker kind PartnerMarker knows.
        static int PartnerMarkerAny(LineInfo info,Replacement marker) {
            int pair=PartnerMarker(info,marker,true);
            return pair>=0 ? pair : PartnerMarker(info,marker,false);
        }
        // The caret on the far side of the opening inline marker ("- **|item**") names the same
        // drawn position as the end of the line's structural prefix, so a Backspace there is the
        // prefix's own key: the list marker, quote level or task box goes and the text keeps its
        // bold, exactly like the plain "- item" line. Every offset from the prefix's end to the
        // first visible character (a run of hidden inline markers, so more than one on
        // "# ***title***") belongs to that one visual spot.
        internal static int LeadRelative(LineInfo info, int relative) {
            int lead = info.LeadEnd;
            if (lead <= 0 || relative <= lead) return relative;
            int end = lead;
            while (true) {
                int grown = end;
                foreach (Replacement replacement in info.Replacements) {
                    if (replacement.Length <= 0 || replacement.Prefix || replacement.Kind != ReplacementKind.Hidden) continue;
                    if (replacement.Start > end || replacement.Start + replacement.Length <= end) continue;
                    grown = replacement.Start + replacement.Length;
                    break;
                }
                if (grown == end) break;
                end = grown;
            }
            return relative <= end ? lead : relative;
        }
        bool HiddenUnitDelete(bool back) {
            int offset = Editor.CaretOffset;
            if (offset < 0 || offset > Document.TextLength) return false;
            DocumentLine line = Document.GetLineByOffset(offset);
            LineInfo info = Info(line);
            if (info.Table || info.Code || info.Delimiter || info.SetextUnderline > 0) return false;
            int relative = offset - line.Offset;
            // Only Backspace: a forward Delete at the same spot removes the first visible
            // character and never the marker this line kept.
            if (back) relative = LeadRelative(info, relative);
            int at = -1;
            for (int i = 0; i < info.Replacements.Count; i++) {
                Replacement candidate = info.Replacements[i];
                if (candidate.Length <= 0) continue;
                if (back ? candidate.Start + candidate.Length == relative : candidate.Start == relative) {
                    at = i;
                    if (!back) break;
                }
            }
            if (at < 0) return false;
            Replacement marker = info.Replacements[at];
            var ranges = new List<Cell>();
            if (marker.Start < info.LeadEnd) ranges.Add(new Cell { Start=0, End=info.LeadEnd });
            else {
                LinkPart link = LinkAround(info, marker);
                if (link != null) AddLinkRanges(info, link, ranges);
                else {
                    ranges.Add(new Cell { Start=marker.Start, End=marker.Start + marker.Length });
                    // A tag's pair is found from either side; for emphasis and code the key's
                    // direction names the side the partner must be on, exactly as before.
                    int pair = marker.Start < info.Text.Length && info.Text[marker.Start] == '<'
                        ? TagPairPartner(info, marker)
                        : PartnerMarker(info, marker, back);
                    if (pair >= 0) ranges.Add(new Cell { Start=info.Replacements[pair].Start, End=info.Replacements[pair].Start + info.Replacements[pair].Length });
                }
            }
            if (ranges.Count == 0) return false;
            ApplyUnitDelete(line, ranges);
            return true;
        }
        static LinkPart LinkAround(LineInfo info, Replacement marker) {
            if (info.Links == null) return null;
            foreach (LinkPart link in info.Links) {
                if (marker.Start >= link.Start && marker.Start + marker.Length <= link.Start + link.Length) return link;
            }
            return null;
        }
        // A link keeps its label: only the hidden parts ("[" and "](url)") go. A footnote
        // reference is not user text, so the whole "[^id]" goes.
        static void AddLinkRanges(LineInfo info, LinkPart link, List<Cell> ranges) {
            if (link.Url != null && link.Url.StartsWith("#^", StringComparison.Ordinal)) {
                ranges.Add(new Cell { Start=link.Start, End=link.Start + link.Length });
                return;
            }
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Length <= 0 || replacement.Kind != ReplacementKind.Hidden) continue;
                if (replacement.Start < link.Start || replacement.Start + replacement.Length > link.Start + link.Length) continue;
                ranges.Add(new Cell { Start=replacement.Start, End=replacement.Start + replacement.Length });
            }
        }
        // The other marker of an emphasis, code or tag pair. The styled part between them is
        // what identifies the partner, so "**bold**" never pairs with a neighbouring marker.
        static int PartnerMarker(LineInfo info, Replacement marker, bool back) {
            if (marker.Kind != ReplacementKind.Hidden || marker.Length <= 0) return -1;
            // A tag pair may have different lengths ("<span style=...>" vs "</span>"), so it has
            // its own depth-counted match; every other marker keeps the styled-part rule below.
            bool tag = marker.Start >= 0 && marker.Start < info.Text.Length && info.Text[marker.Start] == '<';
            if (tag) return TagPartner(info, marker, back);
            if (marker.Length > 4) return -1;
            int end = marker.Start + marker.Length;
            for (int i = 0; i < info.Styles.Count; i++) {
                StyledPart part = info.Styles[i];
                bool styled = (part.Style & (Style.Bold | Style.Italic | Style.Strike | Style.Code)) != 0 ||
                    part.Color != null || part.Size > 0 || part.SizeEm > 0;
                if (!styled) continue;
                if (back) {
                    if (part.Start != end) continue;
                    int close = part.Start + part.Length;
                    for (int k = 0; k < info.Replacements.Count; k++) {
                        Replacement candidate = info.Replacements[k];
                        if (candidate.Kind != ReplacementKind.Hidden || candidate.Start != close) continue;
                        if (candidate.Length != marker.Length) continue;
                        if (tag && !(candidate.Start + 1 < info.Text.Length && info.Text[candidate.Start + 1] == '/')) continue;
                        return k;
                    }
                } else {
                    if (part.Start + part.Length != marker.Start) continue;
                    for (int k = 0; k < info.Replacements.Count; k++) {
                        Replacement candidate = info.Replacements[k];
                        if (candidate.Kind != ReplacementKind.Hidden) continue;
                        if (candidate.Start + candidate.Length != part.Start || candidate.Length != marker.Length) continue;
                        if (tag && !(candidate.Start + 1 < info.Text.Length && info.Text[candidate.Start + 1] == '/')) continue;
                        return k;
                    }
                }
            }
            return -1;
        }
        // Host-facing pair lookup: `back` asks for the opener of a closing run, otherwise the
        // closer of an opening run. Null when the run is not the marker of an inline pair.
        internal static Replacement? PairOf(LineInfo info, Replacement marker, bool back) {
            int index = PartnerMarker(info, marker, back);
            return index >= 0 && index < info.Replacements.Count ? (Replacement?)info.Replacements[index] : null;
        }
        // The other tag of a hidden <span ...>/<font ...> pair on the same line. Depth counting
        // over the line's hidden tag runs keeps "<span>a<span>b</span>c</span>" paired with its
        // own element, the same rule FindTagClose uses while parsing. `back` has the meaning the
        // emphasis path gives it: the marker is an opening tag and the answer is its closing tag,
        // or (back off) the marker is a closing tag and the answer is its opener.
        static int TagPartner(LineInfo info, Replacement marker, bool back) {
            string text = info.Text;
            if (marker.Start < 0 || marker.Start + 1 >= text.Length || text[marker.Start] != '<') return -1;
            bool closing = text[marker.Start + 1] == '/';
            if (closing == back) return -1;   // back asks about an opening tag, not a closing one
            string name = TagNameAt(text, marker.Start + (closing ? 2 : 1));
            if (name == null) return -1;
            // The run must be the tag itself, so text that only starts like one never pairs.
            if (closing) {
                if (marker.Start + 2 + name.Length >= text.Length || text[marker.Start + 2 + name.Length] != '>') return -1;
                if (marker.Length != name.Length + 3) return -1;
            } else {
                if (marker.Start + 1 + name.Length >= text.Length) return -1;
                char next = text[marker.Start + 1 + name.Length];
                if (next != ' ' && next != '\t') return -1;
                if (text[marker.Start + marker.Length - 1] != '>') return -1;
            }
            int depth = 0;
            // An opening tag looks forward for its closer; a closing tag looks back for its opener.
            int step = back ? 1 : -1;
            for (int i = back ? 0 : info.Replacements.Count - 1; i >= 0 && i < info.Replacements.Count; i += step) {
                Replacement candidate = info.Replacements[i];
                if (candidate.Kind != ReplacementKind.Hidden || candidate.Length <= 0) continue;
                if (back ? candidate.Start <= marker.Start : candidate.Start >= marker.Start) continue;
                if (candidate.Start + 1 >= text.Length || text[candidate.Start] != '<') continue;
                bool candidateClosing = text[candidate.Start + 1] == '/';
                string candidateName = TagNameAt(text, candidate.Start + (candidateClosing ? 2 : 1));
                if (candidateName == null || String.Compare(candidateName, name, StringComparison.OrdinalIgnoreCase) != 0) continue;
                if (candidateClosing) {
                    if (candidate.Start + 2 + candidateName.Length >= text.Length || text[candidate.Start + 2 + candidateName.Length] != '>') continue;
                    if (candidate.Length != candidateName.Length + 3) continue;
                } else {
                    if (candidate.Start + 1 + candidateName.Length >= text.Length) continue;
                    char after = text[candidate.Start + 1 + candidateName.Length];
                    if (after != ' ' && after != '\t') continue;
                    if (text[candidate.Start + candidate.Length - 1] != '>') continue;
                }
                if (back) {
                    // Forward: a nested opener deepens, and the first closer at depth 0 answers.
                    if (!candidateClosing) { depth++; continue; }
                    if (depth == 0) return i;
                    depth--;
                } else {
                    // Backward: a nested closer deepens, and the first opener at depth 0 answers.
                    if (candidateClosing) { depth++; continue; }
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return -1;
        }
        // The other tag of a pair from either side: the key's direction names the edge the caret
        // is on, not the side the partner must be on ("<span ...>|</span>" exists on both).
        static int TagPairPartner(LineInfo info, Replacement marker) {
            int pair = PartnerMarker(info, marker, true);
            return pair >= 0 ? pair : PartnerMarker(info, marker, false);
        }
        // The tag name after "<" or "</": letters, digits and '-', the same scan FindTagClose
        // compares with; null when the run does not start with one.
        static string TagNameAt(string text, int start) {
            int end = start;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '-')) end++;
            return end > start ? text.Substring(start, end - start) : null;
        }
        // The caret between an empty pair's two markers, or just after its closing marker: the
        // pair frames no text, so HiddenUnitDelete's whole-unit rule is the deletion the key means.
        // Only the line's own parse is read; a non-empty pair always has a visible character on
        // the side the key comes from.
        static bool EmptyPairAt(LineInfo info, int relative) {
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind != ReplacementKind.Hidden || marker.Length <= 0 || marker.Prefix) continue;
                if (marker.Start + marker.Length != relative) continue;
                // (a) The marker is an opener whose closer starts here: the caret is inside.
                int pair = PartnerMarker(info, marker, true);
                if (pair >= 0 && info.Replacements[pair].Start == relative) return true;
                // (b) The marker is a closer whose opener ends here: the caret is just after.
                pair = PartnerMarker(info, marker, false);
                if (pair >= 0 && info.Replacements[pair].Start + info.Replacements[pair].Length == marker.Start) return true;
            }
            return false;
        }
        // The inside edge of the closing run whose outer edge is this offset, or -1. An inline
        // pair's closing markers draw zero width, so every edge of the stacked run names one
        // visual position. Paste (SnapInsertOffset) and the toolbar/size reads (FormatAt,
        // LiveDrawnSize) take the whole spot as the pair's inside, so a paste there keeps the
        // bold and the size, and a non-blank typed character goes to the same spot
        // (HiddenCloseTypingEntering/TypingInsideTarget), so the toolbar and typing agree.
        internal static int HiddenCloseInside(LineInfo info, int relative) {
            int start, end;
            return HiddenCloseRun(info, relative, out start, out end) ? start : -1;
        }
        // The contiguous run of stacked hidden closers around `relative`: `start` is the inner
        // edge of the innermost one, `end` the outer edge past the last stacked closer. A caret
        // anywhere in the run (an edge of any marker or an offset inside one) names that one
        // drawn position.
        internal static bool HiddenCloseRun(LineInfo info, int relative, out int start, out int end) {
            start = -1; end = -1;
            if (info == null || relative <= 0 || relative > info.Text.Length) return false;
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind != ReplacementKind.Hidden || marker.Length <= 0 || marker.Prefix) continue;
                int markerEnd = marker.Start + marker.Length;
                if (marker.Start > relative || markerEnd < relative) continue;
                if (!HiddenCloser(info, marker)) continue;
                if (start < 0 || marker.Start < start) start = marker.Start;
                if (markerEnd > end) end = markerEnd;
            }
            if (start < 0) return false;
            for (int pass = 0; pass < 8 && end < info.Text.Length; pass++) {
                int next = -1;
                foreach (Replacement marker in info.Replacements) {
                    if (marker.Start != end || !HiddenCloser(info, marker)) continue;
                    if (marker.Start + marker.Length > next) next = marker.Start + marker.Length;
                }
                if (next <= end) break;
                end = next;
            }
            for (int pass = 0; pass < 8; pass++) {
                int next = -1;
                foreach (Replacement marker in info.Replacements) {
                    if (!HiddenCloser(info, marker) || marker.Start + marker.Length != start) continue;
                    if (next < 0 || marker.Start < next) next = marker.Start;
                }
                if (next < 0 || next >= start) break;
                start = next;
            }
            return true;
        }
        // The offset just past the last style closer of the caret's closing run: the emphasis
        // closers ("*", "**", "***", "_", "__", "~~") when the run holds any, else the end of its
        // tag closers (size span, colour font), or -1 when it holds neither (inline code alone
        // keeps its older answer). A caret at or past that offset has left the styled run: a
        // SPACE typed there is plain (11-01 bold, 11-05 every inline style, plainAfterClosed in
        // HiddenCloseTypingEntering); a caret before it is inside the pair, the run being typed.
        static int ClosedRunTypingEnd(LineInfo info, int start, int end) {
            if (info == null || start < 0 || end <= start || end > info.Text.Length) return -1;
            int emphasis = -1, tag = -1;
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind != ReplacementKind.Hidden || marker.Length <= 0 || marker.Prefix) continue;
                int markerEnd = marker.Start + marker.Length;
                if (marker.Start < start || markerEnd > end) continue;
                if (!HiddenCloser(info, marker)) continue;
                char first = info.Text[marker.Start];
                if (first == '`') continue;
                if (first == '<') { if (markerEnd > tag) tag = markerEnd; }
                else if (markerEnd > emphasis) emphasis = markerEnd;
            }
            return emphasis >= 0 ? emphasis : tag;
        }
        // Where a non-blank character typed on the closing run [start, end] goes: the pair's
        // inside, the same spot FormatAt reads for the toolbar, so the lit buttons and the typed
        // text can never disagree. Inline code is the exception the toolbar already makes (it is
        // not lit on the outer edge, LiveInlineCodeAt): on a revealed line (`stepOverCode`) its
        // closers at the run's inner end are stepped over, so the character stays after the code
        // as before (R7). With markers hidden it keeps its older inside answer (probes
        // "basic overlap typing at the drawn end code"); that code mismatch is reported, not changed.
        static int TypingInsideTarget(LineInfo info, int start, int end, int relative, bool stepOverCode) {
            int at = start;
            if (!stepOverCode || relative != end) return at;
            while (at < end) {
                Replacement marker = MarkerStarting(info, at);
                if (marker.Length <= 0 || info.Text[marker.Start] != '`') break;
                at = marker.Start + marker.Length;
            }
            return at;
        }
        // True when `relative` lies strictly inside one closing marker's characters (between the two
        // stars of "**"): a revealed line's caret there is editing the marker itself.
        static bool InsideCloserMarker(LineInfo info, int relative) {
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind != ReplacementKind.Hidden || marker.Length <= 1 || marker.Prefix) continue;
                if (relative <= marker.Start || relative >= marker.Start + marker.Length) continue;
                if (HiddenCloser(info, marker)) return true;
            }
            return false;
        }
        // The contiguous run of stacked hidden openers around `relative`. Typing a space on any
        // edge of it would put the whitespace just inside an opening marker, where the parser
        // stops pairing ("** ab**" is literal, " **ab**" is a pair).
        internal static bool HiddenOpenRun(LineInfo info, int relative, out int start, out int end) {
            start = -1; end = -1;
            if (info == null || relative < 0 || relative > info.Text.Length) return false;
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind != ReplacementKind.Hidden || marker.Length <= 0 || marker.Prefix) continue;
                int markerEnd = marker.Start + marker.Length;
                if (marker.Start > relative || markerEnd < relative) continue;
                if (!HiddenOpener(info, marker)) continue;
                if (start < 0 || marker.Start < start) start = marker.Start;
                if (markerEnd > end) end = markerEnd;
            }
            if (start < 0) return false;
            for (int pass = 0; pass < 8 && start > 0; pass++) {
                int next = -1;
                foreach (Replacement marker in info.Replacements) {
                    if (!HiddenOpener(info, marker) || marker.Start + marker.Length != start) continue;
                    if (next < 0 || marker.Start < next) next = marker.Start;
                }
                if (next < 0 || next >= start) break;
                start = next;
            }
            for (int pass = 0; pass < 8 && end < info.Text.Length; pass++) {
                int next = -1;
                foreach (Replacement marker in info.Replacements) {
                    if (marker.Start != end || !HiddenOpener(info, marker)) continue;
                    if (marker.Start + marker.Length > next) next = marker.Start + marker.Length;
                }
                if (next <= end) break;
                end = next;
            }
            return true;
        }
        // The hidden opener whose marker characters strictly contain `relative` (neither edge): a
        // typed space there would split the run. A tag's own tag text is not a marker run the user
        // can split - the tag is hidden wholesale and its characters are structure - so tags are
        // excluded. Stacked runs answer their innermost opener, the same point Enter's edge rule
        // keeps the break on.
        static Replacement HiddenOpenerMarkerAt(LineInfo info, int relative) {
            if (info == null || relative <= 0 || relative >= info.Text.Length) return new Replacement();
            foreach (Replacement marker in info.Replacements) {
                if (marker.Kind != ReplacementKind.Hidden || marker.Length <= 0 || marker.Prefix) continue;
                if (relative <= marker.Start || relative >= marker.Start + marker.Length) continue;
                if (!HiddenOpener(info, marker) || IsTagMarker(info, marker)) continue;
                return marker;
            }
            return new Replacement();
        }
        // The outer edge of the stacked emphasis/strike/code closers at the inner end of a close
        // run, stopping before an enclosing tag: a tag takes whitespace inside it, so the space
        // may stay there. Returns `start` when the innermost closer is a tag (nothing can break).
        static int ClosePairEnd(LineInfo info, int start, int end) {
            int at = start;
            while (at < end) {
                Replacement marker = MarkerStarting(info, at);
                if (marker.Length <= 0 || IsTagMarker(info, marker)) break;
                at = marker.Start + marker.Length;
            }
            return at;
        }
        // The inner edge of the stacked emphasis/strike/code openers, measured the same way.
        static int OpenPairStart(LineInfo info, int start, int end) {
            int at = end;
            while (at > start) {
                Replacement marker = MarkerEnding(info, at);
                if (marker.Length <= 0 || IsTagMarker(info, marker)) break;
                at = marker.Start;
            }
            return at;
        }
        static bool IsTagMarker(LineInfo info, Replacement marker) {
            return marker.Start >= 0 && marker.Start < info.Text.Length && info.Text[marker.Start] == '<';
        }
        // A hidden marker that closes an emphasis, code or tag pair.
        static bool HiddenCloser(LineInfo info, Replacement marker) {
            return marker.Kind == ReplacementKind.Hidden && marker.Length > 0 && !marker.Prefix && PartnerMarker(info, marker, false) >= 0;
        }
        // A hidden marker that opens one.
        static bool HiddenOpener(LineInfo info, Replacement marker) {
            return marker.Kind == ReplacementKind.Hidden && marker.Length > 0 && !marker.Prefix && PartnerMarker(info, marker, true) >= 0;
        }
        // The closer that starts exactly at `relative`, else the default replacement.
        static Replacement MarkerStarting(LineInfo info, int relative) {
            foreach (Replacement marker in info.Replacements)
                if (marker.Start == relative && HiddenCloser(info, marker)) return marker;
            return new Replacement();
        }
        // The innermost opener that ends exactly at `relative`, else the default replacement.
        static Replacement MarkerEnding(LineInfo info, int relative) {
            Replacement found = new Replacement();
            foreach (Replacement marker in info.Replacements) {
                if (marker.Start + marker.Length != relative || !HiddenOpener(info, marker)) continue;
                if (found.Length <= 0 || marker.Start > found.Start) found = marker;
            }
            return found;
        }
        // One undo group for the whole marker (DocumentHistory records an update as one entry).
        void ApplyUnitDelete(DocumentLine line, List<Cell> ranges) {
            ranges.Sort(delegate(Cell a, Cell b) { return a.Start.CompareTo(b.Start); });
            var merged = new List<Cell>();
            foreach (Cell range in ranges) {
                if (range.End <= range.Start) continue;
                if (merged.Count > 0 && range.Start <= merged[merged.Count - 1].End) {
                    Cell last = merged[merged.Count - 1];
                    if (range.End > last.End) { last.End = range.End; merged[merged.Count - 1] = last; }
                    continue;
                }
                merged.Add(range);
            }
            if (merged.Count == 0) return;
            int lineOffset = line.Offset;
            int caret = lineOffset + merged[0].Start;
            Document.BeginUpdate();
            try {
                for (int i = merged.Count - 1; i >= 0; i--) Document.Remove(lineOffset + merged[i].Start, merged[i].End - merged[i].Start);
            } finally { Document.EndUpdate(); }
            int limit = Document.GetLineByOffset(Math.Min(caret, Document.TextLength)).EndOffset;
            Editor.TextArea.Caret.Offset = Math.Max(0, Math.Min(caret, limit));
        }
        void TextAreaKeyInput(object sender, EventArgs e) {
            if (disposed) return;
            if (frozen) {
                if (Mouse.LeftButton == MouseButtonState.Pressed) return;
                if (releaseTimer != null) releaseTimer.Stop();
                ResumePress();
            }
            // Deletions and structural keys change the edited line's height too; the same
            // one coalesced settle keeps that frame's geometry final (no-op when valid).
            FinishInputLayout();
        }
        void ResumePress() {
            if (disposed || !frozen) return;
            frozen = false;
            pressFromMouse = false;
            ApplyActiveChange();
        }
        internal void BeginPress() {
            if (disposed) return;
            if (releaseTimer != null) releaseTimer.Stop();
            frozen = true;
            pressFromMouse = Mouse.LeftButton == MouseButtonState.Pressed;
        }
        internal void EndPress() {
            if (disposed || !frozen) return;
            frozen = false;
            pressFromMouse = false;
            ApplyActiveChange();
        }
        CheckBox CheckboxAt(MouseButtonEventArgs e) {
            DependencyObject node = (e.OriginalSource != null ? e.OriginalSource : e.Source) as DependencyObject;
            while (node != null && node != View) {
                CheckBox box = node as CheckBox;
                if (box != null) return box;
                node = ParentOf(node);
            }
            return null;
        }
        bool TryCheckbox(MouseButtonEventArgs e) {
            CheckBox box = CheckboxAt(e);
            if (box == null) return false;
            TaskTag tag = box.Tag as TaskTag;
            if (tag != null && !tag.Line.IsDeleted) ToggleTask(tag.Line, tag.Column);
            return true;
        }
        static DependencyObject ParentOf(DependencyObject node) {
            try { return VisualTreeHelper.GetParent(node); }
            catch (InvalidOperationException) { return null; }
        }
        // #19: read mode follows links on a plain click; edit mode needs Ctrl+click.
        // Answers whether this press would open a link and where it is (no side effects), so the
        // press handler and the click-commit caret placement agree on what the press belongs to.
        bool LinkPressAt(MouseButtonEventArgs e, out int offset) {
            offset = -1;
            bool readOnly = Options.ReadOnly || Editor.IsReadOnly;
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!readOnly && !ctrl) return false;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return false;
            if (LinkActivated == null) return false;
            ICSharpCode.AvalonEdit.TextViewPosition? hit = View.GetPositionFloor(e.GetPosition(View) + View.ScrollOffset);
            if (!hit.HasValue) return false;
            offset = Document.GetOffset(hit.Value.Location);
            return LinkAt(offset) != null;
        }
        bool TryLink(MouseButtonEventArgs e) {
            int offset;
            if (!LinkPressAt(e, out offset)) return false;
            return ActivateLinkAt(offset, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
        // The press belongs to a task box or a link, which the preview consumes without moving the
        // caret. It is asked from SourceEditor's press handler before a click-commit places the
        // caret, because that handler runs first and would otherwise reveal the line (shifting the
        // layout) under this hit test.
        internal bool PressClaimed(MouseButtonEventArgs e) {
            if (disposed || e.Handled) return false;
            if (CheckboxAt(e) != null) return true;
            int offset;
            return LinkPressAt(e, out offset);
        }
        // Shared by the mouse hit test and --link-probe so both follow the same policy.
        internal bool ActivateLinkAt(int offset, bool ctrl) {
            if (disposed) return false;
            bool readOnly = Options.ReadOnly || Editor.IsReadOnly;
            if (!readOnly && !ctrl) return false;
            Action<string> handler = LinkActivated;
            if (handler == null) return false;
            string url = LinkAt(offset);
            if (url == null) return false;
            // A definition marker jumps back to the first reference (click-time scan only).
            if (url.StartsWith("#ref:", StringComparison.Ordinal)) {
                SelectFootnoteReference(url.Substring(5));
                return true;
            }
            handler(url);
            return true;
        }
        void SelectFootnoteReference(string id) {
            if (String.IsNullOrEmpty(id)) return;
            string needle = "[^" + id + "]";
            for (int number = 1; number <= Document.LineCount; number++) {
                DocumentLine line = Document.GetLineByNumber(number);
                if (Info(line).Code) continue;
                string text = Document.GetText(line);
                int at = text.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0) continue;
                if (at + needle.Length < text.Length && text[at + needle.Length] == ':') continue;
                Editor.Select(line.Offset + at, needle.Length);
                SafeBringCaretToView(Editor.TextArea);
                return;
            }
        }
        void ViewSizeChanged(object sender, SizeChangedEventArgs e) {
            if (disposed || !e.WidthChanged) return;
            // A narrow paragraph budget belongs to the old viewport. Keeping it when the
            // window widens pins the table to that old width until some unrelated redraw.
            lastRowParagraphWidth=0;
            foreach (VisualLine line in visible) if (!line.IsDisposed) View.Redraw(line);
        }
        // A folded reference run is one height-tree section over its definition lines, and the
        // host line above spans it. An edit that touches the run, that host or the line after
        // it can move or delete a member, so the fold is released before the document changes;
        // otherwise the height tree can still call the changed line collapsed and the view
        // throws "Trying to build visual line from collapsed line".
        void PrepareReferences(DocumentLine first, DocumentLine last) {
            InvalidateReferenceRun();
            if (referenceSections.Count == 0) return;
            int from = first.LineNumber, to = last.LineNumber;
            for (int i = referenceSections.Count - 1; i >= 0; i--) {
                CollapsedLineSection section = referenceSections[i];
                if (!section.IsCollapsed || section.Start == null || section.End == null) { referenceSections.RemoveAt(i); continue; }
                int start = section.Start.LineNumber, end = section.End.LineNumber;
                if (to >= start - 1 && from <= end + 1) { section.Uncollapse(); referenceSections.RemoveAt(i); }
            }
        }
        void DocumentChanging(object sender, DocumentChangeEventArgs e) {
            if (disposed) return;
            DocumentLine first = Document.GetLineByOffset(e.Offset);
            DocumentLine last = Document.GetLineByOffset(e.Offset + e.RemovalLength);
            PrepareReferences(first, last);
            PrepareMathSections(first, last);
            // Only sections touching this edit need release. A newline elsewhere must not
            // walk every table the reader has visited; the height tree tracks shifted lines.
            if(first.PreviousLine!=null)PrepareDelimiter(first.PreviousLine,false);
            for(DocumentLine changed=first;changed!=null;changed=changed.NextLine){PrepareDelimiter(changed,false);if(changed==last)break;}
            // AvalonEdit's Changed handlers run before ours. Selection/caret validation
            // can construct a visual line from those handlers, so dropping the old parse
            // in DocumentChanged is too late: its old DocumentLength can consume a pipe,
            // a newline, or the next row. Invalidate before the document is mutated.
            previousRowLine=null;previousRow=null;
            if (first==last) {
                LineInfo cached;
                if (lines.TryGetValue(first,out cached) && cached.Row!=null) { previousRowLine=first;previousRow=cached.Row; }
                lines.Remove(first);
            } else {
                var remove=new List<DocumentLine>();
                foreach (DocumentLine cached in lines.Keys)
                    if (cached.LineNumber>=first.LineNumber && cached.LineNumber<=last.LineNumber) remove.Add(cached);
                foreach (DocumentLine cached in remove) lines.Remove(cached);
            }
            // Capture the pre-change block state after an in-line edit when it is already
            // cached (no scan here): DocumentChanged can then prove that the rest of the
            // viewport still parses identically and skip its redraw. Unknown state falls
            // back to the conservative redraw below.
            changeStateKnown = false;
            changeOldLineCount = Document.LineCount;
            if (first.LineNumber == last.LineNumber && blocks.IsKnown(last.LineNumber)) {
                changeLineNumber = last.LineNumber;
                changeState = blocks.After(last.LineNumber);
                changeStateKnown = true;
            }
            DocumentLine firstTable = blocks.KnownTableStart(first.LineNumber) ?? blocks.KnownTableStart(first.LineNumber - 1);
            DocumentLine lastTable = blocks.KnownTableStart(last.LineNumber) ?? blocks.KnownTableStart(last.LineNumber - 1);
            resetCaretColumn = firstTable != null || lastTable != null;
            foreach (TableLayout table in tables) {
                // Widths depend on this table's sampled rows, not every earlier table.
                // Known block states also catch edited visible rows beyond the sample cap.
                if (table.Start.IsDeleted || table.Start == firstTable || table.Start == lastTable ||
                    (!table.SampleEnd.IsDeleted && first.LineNumber<=table.SampleEnd.LineNumber && last.LineNumber>=table.Start.LineNumber-1) ||
                    (table.Start.LineNumber >= first.LineNumber - 1 && table.Start.LineNumber <= last.LineNumber + 1)) table.Dirty = true;
            }
        }
        void DocumentRebuilt() {
            ClearDelimiterSections();
            ClearReferenceSections();
            if (tableWork != null) { tableWork.Abort(); tableWork = null; }
            // A whole-text replace may move the caret without a line-number change event.
            if (!frozen) ReadActive(out caretLine, out selectionStart, out selectionEnd);
            tableIdle.Stop();
            tables.Clear();
            lines.Clear();previousRowLine=null;previousRow=null;
            visible = new VisualLine[0];
        }
        bool resetCaretColumn;
        void DocumentChanged(object sender, DocumentChangeEventArgs e) {
            if (disposed) return;
            // Any edit (typing, undo, paste) moves the text under the pointer; a link tooltip
            // must not keep describing the old offset.
            hoverOffset=-1;HideLinkTip();SetLinkCursor(false);
            // The closing-edge space's state: only a plain whitespace insertion at its exact end
            // extends it (the second space of a gap, typed there); every other edit ends it.
            if (closeSpacePending) {
                string inserted = e.RemovalLength == 0 && e.Offset == closeSpaceCaret && e.InsertionLength > 0
                    ? e.InsertedText.Text : null;
                if (inserted != null && SpaceOnly(inserted)) { closeSpaceCaret += inserted.Length; closeSpaceSpaces += inserted; }
                else closeSpacePending = false;
            }
            // AvalonEdit keeps the caret's OLD visual column across an edit ("keep visual
            // column"). In a table row the edit re-projects the row (a wrapped cell can unwrap),
            // so that column may still map to the same offset yet be a non-canonical filler
            // stop; revalidation then moved the caret to a neighbouring offset (Backspace
            // left it one offset too far right). Drop the stale column: it is recomputed from
            // the offset via the canonical stop.
            bool resetColumn=resetCaretColumn;
            resetCaretColumn=false;
            int number = Document.GetLineByOffset(Math.Min(e.Offset, Document.TextLength)).LineNumber;
            int lastNumber = Document.GetLineByOffset(Math.Min(e.Offset + e.InsertionLength, Document.TextLength)).LineNumber;
            // Table invalidation policy: an edit never restarts the bounded background
            // scan. The edited row wraps synchronously in the published grid; exact
            // widths are recomputed after the caret leaves that table, and visible
            // rows are redrawn only when the published widths or alignment changed.
            bool remeasure = false;
            // URI image data and font advances do not depend on document offsets;
            // only document-derived table samples/row associations are invalidated.
            for (LinkedListNode<TableLayout> node = tables.First; node != null; ) {
                LinkedListNode<TableLayout> next = node.Next;
                TableLayout table = node.Value;
                if (table.Start.IsDeleted) tables.Remove(node);
                else if (table.Dirty) {
                    table.Dirty = false;
                    table.PendingRemeasure = true;
                    remeasure = true;
                }
                node = next;
            }
            if (remeasure) {
                tableIdle.Stop();
                tableIdle.Start();
            }
            blocks.Invalidate(number - 1);
            blocks.Invalidate(number);
            DocumentLine edited=Document.GetLineByNumber(number),previous=edited.PreviousLine;LineInfo preceding;
            HashSet<DocumentLine> mathDirty=MathInvalidate(edited);
            // Only the two next-line lookaheads (a table header's delimiter row and a setext
            // underline) can change on the preceding line. Without its own pipe or heading the
            // header flag is false either way, and an underline needs "=" / "-" first, so
            // ordinary typing skips both scans.
            bool editedUnderline=SetextUnderlineStart(Document,edited)&&SetextUnderlineLevel(ShortText(Document,edited))>0;
            if (previous!=null && lines.TryGetValue(previous,out preceding) && !preceding.Code && !preceding.Table &&
                (preceding.Header || preceding.Setext || editedUnderline || HasTablePipe(preceding.Text))) {
                bool header=HasTablePipe(preceding.Text)&&NextIsDelimiter(Document,previous);
                bool setext=!preceding.Table&&editedUnderline&&SetextText(preceding.Text,blocks.Before(previous.LineNumber).Setext);
                if (preceding.Header!=header || preceding.Setext!=setext) {
                    lines.Remove(previous);View.Redraw(previous.Offset,previous.Length);
                }
            }
            if (!frozen) ReadActive(out caretLine, out selectionStart, out selectionEnd);
            bool structural = true;
            if (changeStateKnown && number == changeLineNumber && lastNumber == changeLineNumber &&
                Document.LineCount == changeOldLineCount)
                structural = !blocks.After(lastNumber).Equals(changeState);
            if (structural) {
                DiagStructural++;
                // A structural edit propagates only until the incoming state converges.
                // Keep unaffected visual lines (and their table rows) instead of formatting
                // the whole viewport after a list marker becomes ordinary text.
                var keep=new HashSet<DocumentLine>();
                foreach (VisualLine line in visible) {
                    if (line.IsDisposed || line.FirstDocumentLine.IsDeleted) continue;
                    DocumentLine first=line.FirstDocumentLine;
                    bool changed=line.LastDocumentLine.LineNumber>=number && first.LineNumber<=lastNumber;
                    if (!changed && mathDirty!=null && mathDirty.Contains(first)) changed=true;
                    LineInfo cached;
                    if (!changed && line.LastDocumentLine.LineNumber>=number-1) {
                        if (!lines.TryGetValue(first,out cached)) changed=true;
                        else if (first.LineNumber<number) {
                            // Only the two next-line lookaheads (table header, setext underline)
                            // depend on the edited line. A line inside an existing table block
                            // is never a header (ParseLine's !before.Table), so its cache must
                            // not be redrawn as one when a delimiter-looking row follows.
                            bool header=!cached.Before.Table&&HasTablePipe(cached.Text)&&NextIsDelimiter(Document,first);
                            bool setext=!cached.Table&&editedUnderline&&SetextText(cached.Text,blocks.Before(first.LineNumber).Setext);
                            changed=!cached.Code&&(cached.Header!=header||cached.Setext!=setext);
                        } else {
                            BlockState before=blocks.Before(first.LineNumber), old=cached.Before;
                            // The setext bit only renders differently on a line that is itself
                            // an underline ("===" / "---"); other lines keep their parse so a
                            // structural edit above does not redraw the whole viewport.
                            bool underline=SetextUnderlineLevel(cached.Text)>0;
                            changed=before.Fence!=old.Fence || before.FenceLength!=old.FenceLength ||
                                before.Math!=old.Math || before.MathStart!=old.MathStart ||
                                before.Table!=old.Table || before.TableStart!=old.TableStart ||
                                cached.Table && (before.TableColumns!=old.TableColumns || !before.Equals(old)) ||
                                underline && before.Setext!=old.Setext;
                            // An ordinary/blank line does not consume list context. For an
                            // item compare its result, not the incoming sibling's number.
                            if (!changed && cached.List!=null) {
                                ListState list=blocks.After(first.LineNumber).List;
                                changed=list==null || cached.List.Depth!=list.Depth || cached.List.Number!=list.Number;
                                if (!changed) cached.List=list;
                            }
                            if (!changed) cached.Before=before;
                        }
                    }
                    if (changed) { lines.Remove(first); View.Redraw(line); }
                    else keep.Add(first);
                }
                var remove=new List<DocumentLine>();
                foreach (DocumentLine line in lines.Keys) if (!keep.Contains(line)) remove.Add(line);
                foreach (DocumentLine line in remove) lines.Remove(line);
                // A column command is a structural edit: its delimiter row is processed while
                // the header still has the old count (so it does not read as a delimiter yet),
                // and the header edit then only changes the number of cells. The row cache keeps
                // the old grid until the idle re-sample, which the caret inside the table
                // defers; publish the new count from the final parse here.
                PublishTableCount(number, lastNumber);
            } else {
                // Changing already removed the old parse. A native Changed handler may
                // have built the new row in the meantime; keep that parse and visual paired.
                // Removing the preceding row unconditionally orphaned its live projection
                // whenever text was entered on the line immediately after a table.
                MeasureChangedLines(number, lastNumber);
                if (mathDirty != null) {
                    foreach (VisualLine line in visible)
                        if (!line.IsDisposed && !line.FirstDocumentLine.IsDeleted && mathDirty.Contains(line.FirstDocumentLine)) View.Redraw(line);
                }
                // An undo applies a column command's parts top-down, so the last edit can land
                // on a body row: no header/delimiter measurement republishes the grid there.
                RepublishEditedTableGrid(number);
            }
            if (resetColumn) {
                var caret=Editor.TextArea.Caret;
                caret.Position=new ICSharpCode.AvalonEdit.TextViewPosition(caret.Location,-1);
            }
            if (!frozen && !Options.Busy) QueueCaretGuard();
        }
        void EditorDocumentChanged(object sender, EventArgs e) { Dispose(); }
        void VisualLinesChanged(object sender, EventArgs e) {
            if (disposed || !View.VisualLinesValid) return;
            visible = new VisualLine[View.VisualLines.Count];
            var keep = new HashSet<DocumentLine>();
            for (int i = 0; i < visible.Length; i++) {
                visible[i] = View.VisualLines[i];
                keep.Add(visible[i].FirstDocumentLine);
            }
            var remove = new List<DocumentLine>();
            foreach (DocumentLine line in lines.Keys) if (!keep.Contains(line)) remove.Add(line);
            foreach (DocumentLine line in remove) lines.Remove(line);
            images.RefreshVisible();
            if (SyncVisibleReferences()) View.Redraw();
            // A layout only moves a native composition point while the IME owns one. Queuing it
            // outside a composition ran one DataBind pass plus a Rendering subscription for every
            // line that was re-laid out, which is once per typed character.
            if (compositionLive || Editor.ImeBusy) QueueComposition();
            // A command/paste can place the caret before the layout that exposes its row (H7).
            QueueCaretGuard();
        }

        internal LineInfo Info(DocumentLine line) {
            LineInfo info;
            if (lines.TryGetValue(line, out info)) return info;
            // Every parse reads line.Offset/line.Length straight out of the line object, and
            // ParseLine/ShortText do not clamp that range: a line that is no longer the
            // document's line at its own number would read past the text (monkey seed 1 step
            // 209: GetText(6, 2) on a 7-character document). AvalonEdit's
            // LineManager.Rebuild reuses the old first DocumentLine for the new first line and
            // detaches the other old lines while marking only the second one IsDeleted, so a
            // reference kept across a whole-document replace still reports its old
            // Offset/Length. Parse the document's current line at the same number instead: that
            // is the line the view draws, and its offsets are the ones ShortText must read.
            line = CurrentDocumentLine(line);
            if (line == null) return new LineInfo { Text = string.Empty };
            if (lines.TryGetValue(line, out info)) return info;
            long parseStart = System.Diagnostics.Stopwatch.GetTimestamp();
            info = ParseLine(Document, line, blocks.Before(line.LineNumber), NumberWidth, this);
            if (line==previousRowLine) info.Row=previousRow;
            DiagParses++; DiagParseTicks += System.Diagnostics.Stopwatch.GetTimestamp() - parseStart;
            lines.Add(line, info);
            return info;
        }
        // The line as the document has it now, named by its line number. A DocumentLine that is
        // not in the current line tree (detached by a whole-document replace) keeps its old
        // Offset/Length and IsDeleted == false, so the line number is the only reliable key; the
        // line at that number is the one this view draws. Null when the line cannot be named at
        // all (deleted, or a number past the document).
        DocumentLine CurrentDocumentLine(DocumentLine line) {
            if (line == null) return null;
            int number;
            try {
                if (line.IsDeleted) return null;
                number = line.LineNumber;
            } catch (InvalidOperationException) { return null; }
            if (number < 1 || number > Document.LineCount) return null;
            DocumentLine current = Document.GetLineByNumber(number);
            if (Object.ReferenceEquals(current, line)) return line;
            return current;
        }

        internal RowLayout LayoutRow(LineInfo info, bool raw) {
            TableLayout table = null;
            for (LinkedListNode<TableLayout> node = tables.First; node != null; node = node.Next) {
                if (node.Value.Start != info.TableStart) continue;
                table = node.Value;
                tables.Remove(node);
                tables.AddFirst(node);
                break;
            }
            if (table == null) {
                table = new TableLayout(info.TableStart);
                table.Scale=Options.FontScale;
                tables.AddFirst(table);
                while (tables.Count > 16) tables.RemoveLast();
                // Seed at most one background slice before projecting visible rows.
                // Otherwise every newly encountered table first wraps into 40px columns
                // and is immediately formatted a second time after width publication.
                int rows=0, chars=0;
                while (table.Next!=null && rows<8 && chars<8192) MeasureTableLine(table,ref rows,ref chars);
                table.Widths=(double[])table.Work.Clone(); table.Minima=(double[])table.WorkMinima.Clone(); table.Align=(int[])table.WorkAlign.Clone();
                ScheduleTables();
            }
            // The header row is the grid: a structural edit that lands on it (the last edit of
            // a column command) publishes the new column count here. A data row never grows it.
            if (info.Header && info.Cells != null) table.Count=info.Cells.Count;
            int columns=RowColumns(info,table,raw);
            RowLayout cached=info.Row;
            double indent=Math.Max(0,info.TableIndent);
            if (cached!=null && Object.ReferenceEquals(cached.Source,info.Text) && cached.Table==table && cached.Version==table.Version && cached.Raw==raw &&
                cached.ViewWidth==View.ActualWidth && cached.Indent==indent && String.Equals(cached.MathKey,info.MathTouchKey,StringComparison.Ordinal) &&
                cached.BaseWidth==Math.Max(1,RowWidth()-indent) && cached.Widths.Length==columns) {
                if (Object.ReferenceEquals(cached,previousRow)) { previousRow=null;previousRowLine=null; }
                return cached;
            }
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            var row=new RowLayout(info, table, this, raw);
            // The cache belongs to the parsed line, not to one caller: the marker generator,
            // the table projection, caret/hit-test stops and height queries all read info.Row
            // within the same key. Storing the build here means the first caller builds the
            // row and every later one reuses it, instead of relying on StrutGenerator having
            // run first. Unchanged rows keep their valid cached row and build nothing.
            info.Row=row;
            // previousRow only carries the pre-edit row into the one rebuild that edit
            // causes. Drop the reference once a layout has consumed it, so a later re-parse
            // of the same line (scrolled away, resized) cannot re-attach stale slices.
            if (Object.ReferenceEquals(cached,previousRow)) { previousRow=null;previousRowLine=null; }
            DiagRows++; DiagRowTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
            return row;
        }
        // The width a table row may occupy. The formatter hands every line a paragraph width
        // (availableSize.Width) that the surface's page insets make narrower than the view's
        // ActualWidth; a row sized from the view alone is then wrapped by WPF onto a second
        // visual line, which is what drew a cell fragment at the first column. The remembered
        // paragraph width narrows the budget; until one is known the view width is used.
        internal double RowWidth() {
            double width=View.ActualWidth;
            double paragraph=lastRowParagraphWidth;
            if (paragraph>0 && !Double.IsNaN(paragraph) && !Double.IsInfinity(paragraph) && paragraph<width) width=paragraph;
            return Math.Max(40,width);
        }
        // A row built before the paragraph width was known (a table at the top of a freshly
        // attached view) asks for one redraw once the width arrives. Only a paragraph narrower
        // than the row's own budget can overflow, so only that direction schedules work, which
        // cannot loop: the redraw rebuilds the row at the width now known.
        internal void NoteParagraphWidth(double width,RowLayout row) {
            if (width<=0 || Double.IsNaN(width) || Double.IsInfinity(width)) return;
            lastRowParagraphWidth=width;
            if (row==null || width>=row.Indent+row.BaseWidth-.5) return;
            if (widthRedraw!=null) return;
            widthRedraw=View.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate {
                widthRedraw=null;
                if (disposed) return;
                foreach (TableLayout table in tables) RedrawTable(table);
            }));
        }
        // The TextView formats every line with TextOptions.GetTextFormattingMode(View); in the app
        // that is Display, whose whole-pixel glyph advances run a few percent wider than Ideal.
        // The session can attach while the editor is still parentless, before the view inherits
        // the window's mode, so the formatter it measured cells with then disagrees with the one
        // that draws the row: the row comes out wider than its paragraph and WPF wraps the tail
        // onto a second visual line (the fragment users saw in the first column). Every cell
        // measurement re-checks the view's mode and switches with it.
        internal void EnsureFormatter() {
            TextFormattingMode mode=TextOptions.GetTextFormattingMode(View);
            if (mode==formatterMode) return;
            formatterMode=mode;
            if (Formatter!=null) Formatter.Dispose();
            Formatter=TextFormatter.Create(mode);
            fastNumbers=mode==TextFormattingMode.Ideal && !CultureInfo.CurrentCulture.TextInfo.IsRightToLeft;
            numberMetrics[0]=new CellSlice();numberMetrics[1]=new CellSlice();
            glyphs.Clear();
            // Cell shapes and content metrics hold advances of the old formatter, and every row
            // built from them is stale. Drop both so the next layout shapes at the new advances;
            // the columns themselves are re-measured by the ordinary idle re-sample.
            foreach (TableLayout table in tables) {
                table.Shapes.Clear();table.ShapeCharacters=0;
                table.Metrics.Clear();table.MetricCharacters=0;
                table.Version++;
                if (!table.PendingRemeasure) { table.PendingRemeasure=true; tableIdle.Stop(); tableIdle.Start(); }
            }
            // This can run inside a visual line's construction (a row being measured), where a
            // redraw would dispose the line that is still being built; defer it.
            if (disposed || formatterRedraw!=null) return;
            formatterRedraw=View.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate {
                formatterRedraw=null;
                if (!disposed) View.Redraw();
            }));
        }
        void ScheduleTables() {
            if (disposed || tableWork != null) return;
            bool pending = false;
            foreach (TableLayout table in tables) if (table.Next != null && !table.PendingRemeasure) { pending = true; break; }
            if (!pending) return;
            tableWork = View.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(MeasureTables));
        }
        // Debounced idle re-measure: a table edited by the user is re-sampled from its
        // start only after TableIdleMilliseconds of quiet, so deletion can shrink
        // columns without any full-table scan on the typing path.
        void TableIdle(object sender, EventArgs e) {
            tableIdle.Stop();
            if (disposed) return;
            bool any = false;
            for (LinkedListNode<TableLayout> node = tables.First; node != null; ) {
                LinkedListNode<TableLayout> next = node.Next;
                TableLayout table = node.Value;
                if (table.Start.IsDeleted) tables.Remove(node);
                else if (table.PendingRemeasure && !EditingTable(table)) {
                    table.Restart();
                    any = true;
                }
                node = next;
            }
            if (any) ScheduleTables();
        }
        bool EditingTable(TableLayout table) {
            if (Options.ReadOnly || Editor.IsReadOnly) return false;
            DocumentLine line=Document.GetLineByNumber(Editor.TextArea.Caret.Line);
            return Info(line).TableStart==table.Start;
        }
        void MeasureTables() {
            tableWork = null;
            if (disposed) return;
            // No 2,000-row scan on an input/caret callback. Yield between small slices;
            // retain the previous estimate while an edited table is being resampled.
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            int rows = 0, chars = 0;
            foreach (TableLayout table in tables) {
                if (table.PendingRemeasure) continue;
                while (table.Next != null && rows < 8 && chars < 8192) MeasureTableLine(table,ref rows,ref chars);
                if (rows >= 8 || chars >= 8192) break;
            }
            DiagTableTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
            ScheduleTables();
        }
        void MeasureTableLine(TableLayout table, ref int rows, ref int chars) {
            DocumentLine line=table.Next;
            if (line.IsDeleted || table.Start.IsDeleted) { table.Next=null; return; }
            if (table.Rows>=2000 || table.Characters+line.Length>300000 || line.Length>ParseLimit) { CompleteTable(table); return; }
            table.SampleEnd=line;
            string text=ShortText(Document,line);
            if (!HasTablePipe(text)) { CompleteTable(table); return; }
            List<Cell> cells=SplitCells(text);
            if (table.Rows==1 && IsDelimiter(text)) {
                for (int c=0;c<cells.Count && c<TableLayout.Columns;c++) {
                    Cell cell=cells[c];
                    bool left=cell.Start<cell.End && text[cell.Start]==':', right=cell.Start<cell.End && text[cell.End-1]==':';
                    table.WorkAlign[c]=right ? left ? 2 : 1 : 0;
                }
            } else {
                var info=new LineInfo {Text=text,Cells=cells,Header=table.Rows==0,Table=true};
                ParseInline(info,0,this); SortReplacements(info);
                MeasureCells(info,table,table.Work,table.WorkMinima);
                // GFM: the header row's cells are the grid; a data row with extra cells is cut
                // to them by RowColumns, so its own count must not widen the table.
                if (table.Rows==0) table.Count=cells.Count;
            }
            rows++; chars+=text.Length; table.Rows++; table.Characters+=text.Length; table.Next=line.NextLine;
            if (table.Next==null || table.Rows>=2000 || table.Characters>=300000) CompleteTable(table);
        }
        void CompleteTable(TableLayout table) {
            table.Next = null;
            MergeHotRows(table);
            // b52: grow-only while the caret is still in this table. A sample taken before
            // the last keys landed would otherwise publish the narrower width under the
            // caret and wrap the text again.
            if (EditingTable(table))
                for (int c = 0; c < TableLayout.Columns; c++)
                    if (table.Work[c] < table.Grow[c]) table.Work[c] = table.Grow[c];
            // Cell shapes cache the line splits of one width keyed by cell text. The width
            // key already rejects stale hits, but a column count change makes the whole
            // set stale for the new grid, so drop it instead of carrying old slices. The
            // grow-only widths are indexed by column too, so they go with it: after a column
            // was inserted or removed the stored width would hold the neighbour's column.
            if (table.PublishedCount != table.Count) { table.Shapes.Clear(); table.ShapeCharacters = 0; Array.Clear(table.Grow, 0, table.Grow.Length); }
            table.PublishedCount = table.Count;
            bool changed = false;
            for (int c = 0; c < TableLayout.Columns; c++) {
                if (table.Widths[c] != table.Work[c] || table.Minima[c] != table.WorkMinima[c] || table.Align[c] != table.WorkAlign[c]) { changed = true; break; }
            }
            table.Widths = table.Work;
            table.Minima = table.WorkMinima;
            table.Align = table.WorkAlign;
            if (changed) { table.Version++; RedrawTable(table); }
        }
        // The bounded sample may stop before a row the user edited. Its measured metrics
        // are merged into the fresh sample so a later publish neither drops the growth nor
        // restores a width the user just deleted. Rows the scan covered are skipped (the
        // scan read their current text); deleted and no-longer-table rows are dropped.
        void MergeHotRows(TableLayout table) {
            if (table.HotRows.Count == 0) return;
            List<DocumentLine> stale = null;
            foreach (DocumentLine line in table.HotRows) {
                if (line.IsDeleted) { if (stale == null) stale = new List<DocumentLine>(); stale.Add(line); continue; }
                if (table.SampleEnd != null && line.LineNumber <= table.SampleEnd.LineNumber) continue;
                LineInfo info = Info(line);
                if (!info.Table || info.Delimiter || info.TableStart != table.Start || info.Cells == null || info.Cells.Count == 0) {
                    if (stale==null) stale=new List<DocumentLine>();stale.Add(line);continue;
                }
                MeasureCells(info,table,table.Work,table.WorkMinima);
                // A merged row is a data row beyond the sample; only the header names the grid.
            }
            if (stale != null) foreach (DocumentLine line in stale) table.HotRows.Remove(line);
        }
        void RedrawTable(TableLayout table) {
            // Raw rows keep the source pipes but still use the same column targets,
            // so they must follow width changes too.
            foreach (VisualLine line in visible) {
                if (line.IsDisposed || line.FirstDocumentLine.IsDeleted) continue;
                if (Info(line.FirstDocumentLine).TableStart == table.Start) View.Redraw(line);
            }
        }
        TableLayout FindTable(DocumentLine start) {
            if (start == null) return null;
            for (LinkedListNode<TableLayout> node = tables.First; node != null; node = node.Next)
                if (node.Value.Start == start) return node.Value;
            return null;
        }
        // Incremental edit path: only the edited rows are re-measured. Column growth and
        // row content stay unpublished until the idle re-sample, so a keystroke inside a
        // cell re-lays out that row alone; delimiter alignment stays immediate. Bounded
        // like MeasureTables.
        // A structural edit does not run the row measurement below, so the published column
        // count is synced here from the edited header/delimiter line. Both lines carry the
        // table's own column count (NextIsDelimiter makes the header's equal the delimiter's),
        // and a version bump redraws the rows so their cached layouts take the new count.
        void PublishTableCount(int firstNumber,int lastNumber) {
            int limit=Math.Min(lastNumber,firstNumber+7);
            for (int number=firstNumber;number<=limit;number++) {
                if (number>Document.LineCount) break;
                DocumentLine line=Document.GetLineByNumber(number);
                if (line.IsDeleted) continue;
                LineInfo info=Info(line);
                if (!info.Table || info.TableStart==null || info.Cells==null || info.Cells.Count==0) continue;
                if (!info.Delimiter && !info.Header) continue;
                TableLayout table=FindTable(info.TableStart);
                if (table==null || table.Start.IsDeleted || table.Count==info.Cells.Count) continue;
                if (table.PublishedCount!=info.Cells.Count) { table.Shapes.Clear(); table.ShapeCharacters = 0; Array.Clear(table.Grow, 0, table.Grow.Length); }
                table.Count=info.Cells.Count;
                table.PublishedCount=info.Cells.Count;
                table.Version++;
                RedrawTable(table);
            }
        }
        // A body-row edit republishes the grid from the table's edge row (the delimiter row,
        // which owns the column count with the header). Undoing a column command applies its
        // parts top-down, so the undo ends on a body row after the header and delimiter were
        // restored; without this the published grid kept the old count and drew one empty extra
        // column. Ordinary body edits find the counts equal and do nothing.
        void RepublishEditedTableGrid(int number) {
            if (number<1 || number>Document.LineCount) return;
            LineInfo info=Info(Document.GetLineByNumber(number));
            if (!info.Table || info.TableStart==null) return;
            DocumentLine edge=info.Delimiter ? Document.GetLineByNumber(number) : info.TableStart.NextLine;
            if (edge==null || edge.IsDeleted) return;
            LineInfo edgeInfo=Info(edge);
            if (!edgeInfo.Header && !edgeInfo.Delimiter) return;
            // A delimiter row's Info carries no Cells (ParseLine folds it away), so split its
            // own source like MeasureRow does; the header row's Cells are already there.
            List<Cell> edgeCells=edgeInfo.Cells!=null ? edgeInfo.Cells : SplitCells(edgeInfo.Text);
            if (edgeCells.Count==0) return;
            TableLayout table=FindTable(info.TableStart);
            if (table==null || table.Start.IsDeleted || table.Count==edgeCells.Count) return;
            if (table.PublishedCount!=edgeCells.Count) { table.Shapes.Clear(); table.ShapeCharacters = 0; Array.Clear(table.Grow, 0, table.Grow.Length); }
            table.Count=edgeCells.Count;
            table.PublishedCount=edgeCells.Count;
            table.Version++;
            RedrawTable(table);
        }
        void MeasureChangedLines(int firstNumber, int lastNumber) {
            int limit = Math.Min(lastNumber, firstNumber + 7);
            int chars = 0;
            for (int number = firstNumber; number <= limit && chars < 8192; number++) {
                DocumentLine line = Document.GetLineByNumber(number);
                if (line.IsDeleted) continue;
                chars += line.Length;
                MeasureRow(line);
            }
        }
        void MeasureRow(DocumentLine line) {
            LineInfo info = Info(line);
            if (!info.Table || info.TableStart == null) return;
            TableLayout table = FindTable(info.TableStart);
            if (table == null || table.Start.IsDeleted) return;
            List<Cell> cells = info.Cells != null ? info.Cells : SplitCells(info.Text);
            bool changed = false;
            if (info.Delimiter) {
                for (int c = 0; c < cells.Count && c < TableLayout.Columns; c++) {
                    Cell cell = cells[c];
                    bool left = cell.Start < cell.End && info.Text[cell.Start] == ':';
                    bool right = cell.Start < cell.End && info.Text[cell.End - 1] == ':';
                    int align = right ? left ? 2 : 1 : 0;
                    if (table.Align[c] == align) continue;
                    table.Align[c] = align;
                    changed = true;
                }
                // The |---| row declares the table's column count (GFM pads/truncates every
                // row to it), so a column command or its undo/redo - each of which edits this
                // line - publishes the new grid at once. The idle re-sample is deferred while
                // the caret stays in the table, so the old, wider grid otherwise kept drawing
                // one empty extra column until the caret left and the table redrew.
                if (cells.Count > 0 && table.Count != cells.Count) {
                    if (table.PublishedCount != cells.Count) { table.Shapes.Clear(); table.ShapeCharacters = 0; Array.Clear(table.Grow, 0, table.Grow.Length); }
                    table.Count = cells.Count;
                    table.PublishedCount = cells.Count;
                    changed = true;
                }
            } else {
                // The published grid is the visual contract: a growing cell wraps inside
                // its column. The idle re-sample publishes only after leaving this table;
                // a pause must not move an active row or the composition window.
                // The background sample is deferred, so FormatMinMaxParagraphWidth on every
                // key cannot change any visible result. It shaped the growing cell a second
                // time and retained all earlier prefixes in Metrics. Defer that work entirely.
                // A header row's cell count IS the delimiter row's (NextIsDelimiter compares
                // them), so a column count change that lands on the header - the last edit of
                // a column command, applied after the delimiter row was transiently not yet
                // a table - publishes the new grid here, not only on the delimiter row.
                if (info.Header && table.Count != cells.Count) {
                    if (table.PublishedCount != cells.Count) { table.Shapes.Clear(); table.ShapeCharacters = 0; Array.Clear(table.Grow, 0, table.Grow.Length); }
                    table.Count = cells.Count;
                    table.PublishedCount = cells.Count;
                    changed = true;
                }
                RecordHotRow(table,line);
                // b52: a cell the caret is typing into must not wrap in the middle of a
                // syllable only to unwrap when the column is re-fitted. The column grows
                // to hold the typed line (composition included) before that wrap can be
                // formatted; growth is grow-only until the caret leaves the table, which
                // is where the idle re-sample may shrink it again. TableWidths bounds the
                // table's total, so an unbroken cell still never pushes the row past the
                // paragraph: the slack comes from the other columns, not from the typed one.
                changed |= GrowTypingColumn(info,table,line,cells);
            }
            if (changed) { table.Version++; RedrawTable(table); }
        }
        // Only the caret's own cell is measured: the other rows of that column keep wrapping
        // in the published width, exactly as before. The column records the typed line's own
        // one-line width; TableWidths bounds the table's total, so the row still fits the
        // paragraph without capping this column (which used to shrink the table while the
        // caret was inside it and let it jump when the caret left).
        bool GrowTypingColumn(LineInfo info,TableLayout table,DocumentLine line,List<Cell> cells) {
            if (Options.ReadOnly || Editor.IsReadOnly) return false;
            int relative=Editor.CaretOffset-line.Offset;
            if (relative<0 || relative>line.Length) return false;
            int column=-1;
            for (int c=0;c<cells.Count && c<TableLayout.Columns;c++)
                if (relative>=cells[c].Start && relative<=cells[c].End) { column=c;break; }
            if (column<0) {
                // A caret in a cell's own padding, in the gap before a pipe, or past the last pipe
                // still edits a cell: a paste clamps into the cell the same way (ClampIntoCell in
                // MarkdownTableCommands). Without this a paste there left the column at its old
                // width until the caret left the table and the idle re-sample republished.
                for (int c=0;c<cells.Count&&c<TableLayout.Columns;c++) {
                    if (relative>cells[c].End) continue;
                    column=c;break;
                }
                if (column<0) column=Math.Min(cells.Count,TableLayout.Columns)-1;
            }
            if (column<0) return false;
            Cell cell=cells[column];
            if (cell.End<=cell.Start) return false;
            // A column that was inserted or removed while the caret stayed here renumbers every
            // stored width; they would then hold a neighbour's column, so drop them first.
            if (table.GrowColumns!=cells.Count) { Array.Clear(table.Grow,0,table.Grow.Length); table.GrowColumns=cells.Count; }
            // Widths (and Work) hold the cell's TEXT width; TableWidths adds the padding and
            // bounds the table's total, so this column is not capped here.
            double text=glyphs.Measure(info.Text,cell.Start,cell.End,info.Header);
            if (text<=0) return false;
            // A composition string is not in the document yet: reserve one more syllable so
            // the column is already wide enough when the IME draws its interim glyphs.
            double allowance=Editor.ImeBusy ? Options.FontSize*1.2 : 0;
            // +2: FormatCell wraps at Width-Padding*2-1, so the text needs one more pixel
            // than its measured advance to stay on one line (rounding included).
            double need=text+allowance+2;
            if (need>table.Grow[column]) table.Grow[column]=need;
            double before=table.Widths[column];
            // The grown width is remembered even when the current grid cannot use it, so a
            // wider view pays it without another key.
            if (table.Grow[column]<=before+.5) return false;
            table.Widths[column]=table.Grow[column];
            double published;
            double gained=AllocatedWidthGain(table,cells,column,info.TableIndent,before,out published);
            // Publish when the new allocation keeps the typed line on one line (it fits there
            // and not at the published width), or when it widens the column by at least half a
            // character. A column that shares the view's slack with others creeps a couple of
            // pixels per key; publishing that redrew every visible row (two extra row builds
            // on every key) for a boundary that moved less than a character and could not
            // stop any wrap. `published` is the column width the grid uses now; these widths
            // include the cell padding, the stored Widths do not.
            double textWidth=Math.Max(0,published-CellPadding*2-1);
            double grownWidth=Math.Max(0,published+gained-CellPadding*2-1);
            if (gained<Math.Max(1,Options.FontSize*.5) && !(text>textWidth && text<=grownWidth)) {
                table.Widths[column]=before;
                return false;
            }
            return true;
        }
        // How much the allocation TableWidths makes would widen the typed column if the grown
        // width were published, and the width the grid uses now. Grow keeps the text width
        // when nothing is published, so the next key compares from the published value.
        double AllocatedWidthGain(TableLayout table,List<Cell> cells,int column,double indent,double before,out double published) {
            // The grid is the header's; a data row's extra cells own no column.
            int count=table.Count>0 ? table.Count : cells.Count;
            if (column<0 || column>=count) { published=before; return 0; }
            double viewWidth=RowWidth()-Math.Max(0,indent);
            double grown=table.Widths[column];
            double withGrown=TableWidths(table.Widths,table.Minima,count,viewWidth,Options.WordWrap,Options.FontScale)[column];
            table.Widths[column]=before;
            double withOld=TableWidths(table.Widths,table.Minima,count,viewWidth,Options.WordWrap,Options.FontScale)[column];
            table.Widths[column]=grown;
            published=withOld;
            return withGrown-withOld;
        }
        // Keep the latest metrics of an edited row for a re-sample that may stop before it.
        static void RecordHotRow(TableLayout table, DocumentLine line) {
            if (table.HotRows.Count>=64 && !table.HotRows.Contains(line)) {
                DocumentLine oldest=null;
                foreach (DocumentLine cached in table.HotRows) { oldest=cached;break; }
                if (oldest!=null) table.HotRows.Remove(oldest);
            }
            table.HotRows.Add(line);
        }
        internal static bool PlainCell(LineInfo info, Cell cell) {
            foreach (StyledPart style in info.Styles) if (style.Start<cell.End && style.Start+style.Length>cell.Start) return false;
            foreach (Replacement item in info.Replacements) if (item.Start<cell.End && item.Start+item.Length>cell.Start) return false;
            return true;
        }
        internal List<CellSlice> FormatCell(LineInfo info, Cell cell, CellText text, TableLayout table, double width, bool cacheShape, List<CellSlice> previousSlices) {
            EnsureFormatter();
            bool plain=PlainCell(info,cell), digits=plain && text.Text.Length>0;
            for (int p=0;digits && p<text.Text.Length;p++) digits=text.Text[p]>='0' && text.Text[p]<='9';
            var paragraph=new CellParagraph(text.Default,Options.FontSize*Options.LinePercent/100);
            double advance;
            if (digits && fastNumbers && glyphs.TryDigits(text.Text,0,text.Text.Length,info.Header,out advance)) {
                if (advance<=width) {
                    int index=info.Header ? 1 : 0;
                    if (numberMetrics[index].Height==0) using (TextLine line=Formatter.FormatLine(text,0,Math.Max(width,advance+1),paragraph,null))
                        numberMetrics[index]=new CellSlice {Height=line.Height,Baseline=line.Baseline};
                    CellSlice slice=numberMetrics[index]; slice.Length=text.Text.Length; slice.Width=advance;
                    return new List<CellSlice> {slice};
                }
            }
            // An active row owns its current slices already. Caching every longer prefix
            // retains O(keys squared) characters until the budget fills, with no reuse.
            string key=cacheShape && plain && !digits ? (info.Header ? "H" : "B")+text.Text : null;
            CellShape cached;
            if (key!=null && table.Shapes.TryGetValue(key,out cached) &&
                (cached.Width==width || cached.Slices.Count==1 && cached.Slices[0].Width<=width)) return cached.Slices;
            var slices=new List<CellSlice>();int position=0;
            if (previousSlices!=null && previousSlices.Count>0) {
                for (int p=0;p<previousSlices.Count-1;p++) slices.Add(previousSlices[p]);
                position=previousSlices[previousSlices.Count-1].Start;
            }
            // A cell <br> is a slice boundary, never a run inside a row: every physical row
            // is one TextLine of the projected row, so an in-row break would push the
            // neighbouring cells' text onto another line.
            int breakAt=text.NextBreak(position);
            text.FormatEnd=breakAt<0 ? text.Text.Length : breakAt;
            bool first=true;
            try {
                while (true) {
                    if (position<text.FormatEnd || (first && breakAt<0)) {
                        // FormatLine requires a TextRunCache. A cell with <br> segments
                        // changes FormatEnd between segments, so the per-cell cache could
                        // return a run that crosses the hidden break; one fresh (and
                        // therefore empty) cache per segment avoids both, and the cache is
                        // still reused across the lines of a single segment.
                        TextRunCache cache=text.Breaks==null ? text.RunCache : new TextRunCache();
                        do {
                            using (TextLine line=Formatter.FormatLine(text,position,width,paragraph,null,cache)) {
                                int length=Math.Min(text.FormatEnd-position,line.Length);
                                // One unbreakable run (a long token) is formatted wider than the
                                // cell: WPF keeps the word whole, and the row paragraph then wraps
                                // the rest onto a second visual line at the paragraph's left edge
                                // (the fragment users saw in the first column). Break the run at
                                // the character that still fits, so every physical row of a cell
                                // stays inside its own column.
                                if (line.WidthIncludingTrailingWhitespace>width+.5 && length>1) {
                                    int fit=FittingCharacters(line,width,position,length);
                                    if (fit<length) {
                                        int saved=text.FormatEnd;
                                        text.FormatEnd=position+fit;
                                        try {
                                            using (TextLine part=Formatter.FormatLine(text,position,width,paragraph,null,new TextRunCache())) {
                                                int take=Math.Max(1,Math.Min(fit,part.Length));
                                                slices.Add(new CellSlice {Start=position,Length=take,Width=part.WidthIncludingTrailingWhitespace,Height=part.Height,Baseline=part.Baseline});
                                                position+=take;
                                            }
                                        } finally { text.FormatEnd=saved; }
                                        continue;
                                    }
                                }
                                slices.Add(new CellSlice {Start=position,Length=length,Width=line.WidthIncludingTrailingWhitespace,Height=line.Height,Baseline=line.Baseline});
                                position+=Math.Max(1,length);
                            }
                        } while (position<text.FormatEnd);
                    } else {
                        // An empty segment (a leading, doubled or trailing <br>) still
                        // needs its own physical row and caret stop. Without this the
                        // offset after a final <br> had no stop at all.
                        double height=paragraph.LineHeight;
                        slices.Add(new CellSlice {Start=position,Length=0,Width=0,Height=height,Baseline=height*.8});
                    }
                    first=false;
                    if (breakAt<0) break;
                    position=breakAt+1;
                    breakAt=text.NextBreak(position);
                    text.FormatEnd=breakAt<0 ? text.Text.Length : breakAt;
                }
            } finally { text.FormatEnd=text.Text.Length; }
            if (text.HasMath) FitFormulas(text,slices);
            if (key!=null && table.Shapes.Count<512 && table.ShapeCharacters+key.Length<=32768) {
                if (!table.Shapes.ContainsKey(key)) table.ShapeCharacters+=key.Length;
                table.Shapes[key]=new CellShape {Width=width,Slices=slices};
            }
            return slices;
        }
        // The cell lines are formatted with the paragraph's fixed line height, which does not grow for an
        // object taller than it. A line that holds a formula takes the formula's ascent and descent, so the
        // row (its fillers carry these metrics) keeps the cell's vertical padding around a tall formula.
        static void FitFormulas(CellText text, List<CellSlice> slices) {
            for (int s=0;s<slices.Count;s++) {
                CellSlice slice=slices[s];
                double ascent=slice.Baseline, descent=Math.Max(0,slice.Height-slice.Baseline);
                for (int p=slice.Start;p<slice.Start+slice.Length && p<text.Text.Length;p++) {
                    if (!text.IsMath(p)) continue;
                    MathVisual visual=text.MathVisuals[text.MathSlots[p]];
                    if (visual.Ascent>ascent) ascent=visual.Ascent;
                    if (visual.Descent>descent) descent=visual.Descent;
                }
                if (ascent+descent<=slice.Height+.01 && ascent<=slice.Baseline+.01) continue;
                slice.Baseline=ascent; slice.Height=ascent+descent;
                slices[s]=slice;
            }
        }
        // Characters of an over-wide formatted line that still end inside the cell width. The
        // text line's character indices are paragraph-absolute (the same space as `start`).
        static int FittingCharacters(TextLine line,double width,int start,int length) {
            CharacterHit hit=line.GetCharacterHitFromDistance(Math.Max(0,width));
            int at=hit.FirstCharacterIndex+hit.TrailingLength-start;
            if (at<1) at=1;
            if (at>=length) at=length-1;
            return at;
        }
        // Publishing measurement for the bounded background sample. The typing path never
        // measures a row (MeasureRow records its identity instead), so every typed prefix
        // cannot accumulate as its own metric entry.
        bool MeasureCells(LineInfo info, TableLayout table, double[] preferred, double[] minima) {
            EnsureFormatter();
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            DiagMeasureCells+=Math.Min(info.Cells.Count,TableLayout.Columns);
            bool changed=false;
            for (int c=0;c<info.Cells.Count && c<TableLayout.Columns;c++) {
                Cell cell=info.Cells[c];
                bool plain=PlainCell(info,cell), digits=cell.End>cell.Start;
                for (int p=cell.Start;digits && p<cell.End;p++) digits=info.Text[p]>='0' && info.Text[p]<='9';
                CellMetric metric;
                double advance;
                if (plain && digits && fastNumbers && glyphs.TryDigits(info.Text,cell.Start,cell.End,info.Header,out advance)) {
                    // Decimal runs have no shaping/word-break context. Do not let the
                    // thousands of unique row IDs evict repeated prose measurements.
                    metric=new CellMetric();
                    metric.Preferred=metric.Minimum=Math.Ceiling(advance+1);
                } else {
                    string key=plain ? (info.Header ? "H" : "B")+info.Text.Substring(cell.Start,cell.End-cell.Start) : null;
                    if (key==null || !table.Metrics.TryGetValue(key,out metric)) {
                        var text=new CellText(info,cell,this,false,0,null);
                        MinMaxParagraphWidth measured=Formatter.FormatMinMaxParagraphWidth(text,0,new CellParagraph(text.Default,0));
                        metric=new CellMetric { Preferred=Math.Ceiling(measured.MaxWidth+1), Minimum=Math.Ceiling(measured.MinWidth+1) };
                        if (key!=null && table.Metrics.Count<512 && table.MetricCharacters+key.Length<=32768) {
                            table.Metrics.Add(key,metric); table.MetricCharacters+=key.Length;
                        }
                    }
                }
                if (metric.Preferred>preferred[c]) { preferred[c]=metric.Preferred; changed=true; }
                if (metric.Minimum>minima[c]) { minima[c]=metric.Minimum; changed=true; }
            }
            DiagMeasureTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
            return changed;
        }
        internal UIElement ImageObject(Replacement replacement) {
            return images.Object(replacement.Source, replacement.Label);
        }
        // A table cell draws its image itself, so it asks for the pixels and the capped size
        // instead of a UIElement; the same cache, loader and path resolution do the work.
        internal BitmapSource CellBitmap(string source,out double width,out double height) {
            return images.CellBitmap(source,out width,out height);
        }
        // The drawn formula for a table cell's text run (its size and colour), or null when the formula
        // does not parse: the cell then keeps the source. WpfMath is only touched through MathRender.
        internal MathVisual CellMath(string latex, TextRunProperties properties) {
            if (properties==null || !MathRender.Check(latex)) return null;
            MathVisual visual=MathRender.Get(latex,false,properties.FontRenderingEmSize,properties.ForegroundBrush,Options.Family);
            return visual.Error || visual.Drawing==null || visual.Width<=0 || visual.Height<=0 ? null : visual;
        }
        internal bool UsesImage(Uri uri) {
            foreach (VisualLine line in visible) {
                if (line.IsDisposed || line.FirstDocumentLine.IsDeleted) continue;
                LineInfo info = Info(line.FirstDocumentLine);
                foreach (Replacement replacement in info.Replacements) {
                    if (replacement.Kind == ReplacementKind.Image && images.SameUri(replacement.Source, uri)) return true;
                }
            }
            return false;
        }
        internal void RedrawImages() {
            if (disposed) return;
            foreach (VisualLine line in visible) {
                if (line.IsDisposed || line.FirstDocumentLine.IsDeleted) continue;
                LineInfo info = Info(line.FirstDocumentLine);
                foreach (Replacement replacement in info.Replacements) {
                    if (replacement.Kind != ReplacementKind.Image) continue;
                    // A cell's image position is built together with the row, so the row cache
                    // has to go for the redraw to see the bitmap that has just decoded.
                    if (info.Table) {
                        info.Row = null;
                        TableLayout table = FindTable(info.TableStart);
                        if (table != null && !table.PendingRemeasure) {
                            // The columns were measured from the "![alt](src)" source text
                            // while the bitmap was still loading. Re-sample this table so the
                            // picture gets its natural width (up to the column cap); the idle
                            // path keeps an actively edited row from moving under the caret.
                            table.PendingRemeasure = true;
                            tableIdle.Stop();
                            tableIdle.Start();
                        }
                    }
                    View.Redraw(line);
                    break;
                }
            }
        }
        internal void ToggleTask(DocumentLine line, int column) {
            // #14: like the rich view, reading mode still ticks checkboxes (a normal, undoable edit).
            if (disposed || Options.Busy || Editor.Document != Document || line.IsDeleted || column + 2 >= line.Length) return;
            int offset = line.Offset + column;
            if (Document.GetCharAt(offset) != '[' || Document.GetCharAt(offset + 2) != ']') return;
            char value = Document.GetCharAt(offset + 1);
            if (value != ' ' && value != 'x' && value != 'X') return;
            Document.Replace(offset + 1, 1, value == ' ' ? "x" : " ");
        }
        // Also used by the Ctrl+click interception; returns null outside stored links.
        internal string LinkAt(int offset) {
            if (disposed || offset < 0 || offset > Document.TextLength) return null;
            DocumentLine line = Document.GetLineByOffset(offset);
            LineInfo info = Info(line);
            if (info.Links == null) return null;
            foreach (LinkPart link in info.Links) {
                int start = line.Offset + link.Start;
                if (offset >= start && offset < start + link.Length) return link.Url;
            }
            return null;
        }
        // A rendered link shows where it goes: the document's own Ctrl+click wording in edit
        // mode and plain click in read mode, then the target. A footnote reference shows its
        // definition text and a reference link its "[id]: url" target, so the tooltip never
        // shows the internal "ref:docs" form. Runs on pointer entry only (UpdateLinkHover).
        internal string LinkTipAt(int offset) {
            if (disposed || offset < 0 || offset > Document.TextLength) return null;
            DocumentLine line = Document.GetLineByOffset(offset);
            // A revealed caret/selection line draws Markdown source, not a rendered link.
            if (Raw(line)) return null;
            string url = LinkAt(offset);
            if (url == null) return null;
            bool readOnly = Options.ReadOnly || Editor.IsReadOnly;
            string text = (readOnly ? "클릭하여 열기" : "Ctrl+클릭하여 열기") + " · " + LinkTipTarget(url);
            Func<string,string> translate = Options.Translate;
            return translate == null ? text : translate(text);
        }
        string LinkTipTarget(string url) {
            if (url.StartsWith("#^",StringComparison.Ordinal)) {
                string note = LinkTipDefinition(url.Substring(2),true);
                return note == null ? url : note;
            }
            // The definition marker jumps back to the first reference of that footnote.
            if (url.StartsWith("#ref:",StringComparison.Ordinal)) return "[^"+url.Substring(5)+"]";
            if (url.StartsWith("ref:",StringComparison.Ordinal)) {
                string target = LinkTipDefinition(url.Substring(4),false);
                return target == null ? url : target;
            }
            return url;
        }
        // The host resolves these on click with the same first-definition scan. A definition is
        // looked up once per pointer entry into a footnote/reference link, never per move and
        // never for an ordinary inline link.
        string LinkTipDefinition(string id,bool note) {
            if (String.IsNullOrEmpty(id)) return null;
            string marker = note ? "[^"+id+"]:" : "["+id+"]:";
            TextDocument document = Document;
            for (int number=1;number<=document.LineCount;number++) {
                DocumentLine line = document.GetLineByNumber(number);
                if (line.Length<marker.Length) continue;
                // A definition line starts (after up to three spaces) with '['. The cheap check
                // keeps a big document's walk to one char per line; the string is only built
                // for lines that can be the definition.
                int at = line.Offset, limit = Math.Min(3,line.Length);
                for (int skip=0;skip<limit;skip++) { char c = document.GetCharAt(at); if (c!=' '&&c!='\t') break; at++; }
                if (at-line.Offset>=line.Length || document.GetCharAt(at)!='[') continue;
                string text = document.GetText(line).TrimStart();
                if (!text.StartsWith(marker,StringComparison.OrdinalIgnoreCase)) continue;
                // A definition-looking line inside a fenced block is code, not a definition.
                if (Info(line).Code) continue;
                string rest = text.Substring(marker.Length).Trim();
                if (!note) {
                    int space = rest.IndexOfAny(new char[] {' ','\t'});
                    if (space>=0) rest = rest.Substring(0,space);
                    if (rest.Length>1 && rest[0]=='<' && rest[rest.Length-1]=='>') rest = rest.Substring(1,rest.Length-2);
                } else if (rest.Length>240) rest = rest.Substring(0,240)+"…";
                return rest.Length==0 ? null : rest;
            }
            return null;
        }
        // A shortcut reference link "[label]" is a link only when the document defines that label.
        // The parse stays O(line): the id set is cached by document version, a small document's
        // scan is queued at once, a large one only after the typing pauses.
        internal bool ReferenceLabelDefined(string label) {
            HashSet<string> ids = ReferenceIdsForParse();
            return ids != null && ids.Contains(label);
        }
        HashSet<string> ReferenceIdsForParse() {
            if (referenceIds != null && Object.ReferenceEquals(referenceIdsVersion, Document.Version)) return referenceIds;
            // Never walk the document inside the parse: the set is built by a dispatcher
            // operation after this frame (a small document at once, a large one after typing
            // settles) and the visible lines are reparsed when it lands, so a keystroke only
            // ever costs its own line.
            if (referenceScan == null) {
                if (Document.TextLength <= PromptReferenceScanLimit) QueueReferenceScan();
                else StartReferenceIdle();
            }
            return referenceIds;
        }
        void StartReferenceIdle() {
            if (referenceIdle == null) {
                referenceIdle = new DispatcherTimer(DispatcherPriority.Background, View.Dispatcher);
                referenceIdle.Interval = TimeSpan.FromMilliseconds(250);
                referenceIdle.Tick += ReferenceIdle;
            }
            referenceIdle.Stop();
            referenceIdle.Start();
        }
        void ReferenceIdle(object sender, EventArgs e) {
            referenceIdle.Stop();
            if (disposed) return;
            if (referenceIds != null && Object.ReferenceEquals(referenceIdsVersion, Document.Version)) return;
            QueueReferenceScan();
        }
        void QueueReferenceScan() {
            if (referenceScan != null || disposed) return;
            referenceScan = View.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate {
                referenceScan = null;
                if (disposed) return;
                bool changed;
                ScanReferenceDefinitions(out changed);
                if (changed) RefreshReferenceLines();
            }));
        }
        // Replaces the id set with the document's definitions and says whether it changed.
        // A definition is the same "[id]: ..." shape the line parser draws muted; footnote
        // definitions and lines inside a fence are not link targets. A document past
        // ShortcutReferenceLimit keeps an empty set: a whole-document walk on a million-char
        // log is not paid for links that kind of file does not use.
        HashSet<string> ScanReferenceDefinitions(out bool changed) {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object version = Document.Version;
            if (Document.TextLength <= ShortcutReferenceLimit) {
                char fence = '\0'; int fenceLength = 0;
                for (int number = 1; number <= Document.LineCount; number++) {
                    DocumentLine line = Document.GetLineByNumber(number);
                    char marker; int length; bool tailBlank;
                    ReadFence(Document, line, out marker, out length, out tailBlank);
                    if (fence != '\0') {
                        if (marker == fence && length >= fenceLength && tailBlank) fence = '\0';
                        continue;
                    }
                    if (length >= 3) { fence = marker; fenceLength = length; continue; }
                    if (line.Length < 4 || line.Length > ParseLimit) continue;
                    string text = Document.GetText(line);
                    int p = 0, columns = 0;
                    while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { columns += text[p] == '\t' ? 4 : 1; p++; }
                    if (columns > 3 || p >= text.Length || text[p] != '[') continue;
                    bool note; int idStart, idEnd, colon, bodyStart;
                    if (!DefinitionLine(text, p, out note, out idStart, out idEnd, out colon, out bodyStart)) continue;
                    if (note) continue;
                    // The print and TXT renderers need a destination ("[id]:" alone is not a
                    // definition), so a shortcut link to it stays literal in every view.
                    int destination = bodyStart;
                    while (destination < text.Length && (text[destination] == ' ' || text[destination] == '\t')) destination++;
                    if (destination >= text.Length) continue;
                    string id = ReferenceLabel(text.Substring(idStart, idEnd - idStart));
                    if (id.Length > 0) ids.Add(id);
                }
            }
            changed = referenceIds == null || !referenceIds.SetEquals(ids);
            referenceIds = ids;
            referenceIdsVersion = version;
            return ids;
        }
        // The visible lines were parsed against the older set; reparse them so a shortcut link
        // the new definitions cover appears (and one a deleted definition covered goes away).
        void RefreshReferenceLines() {
            if (disposed) return;
            lines.Clear(); previousRowLine = null; previousRow = null;
            View.Redraw();
        }
        internal bool LinkTipShown { get { return linkTip!=null && linkTip.IsOpen; } }
        internal string LinkTipText { get { return LinkTipShown ? linkTipText : null; } }
        // Shared by the view's mouse move and --link-probe. Returns true while the tooltip is
        // open; an unchanged offset with the same modifier/read state does no lookup.
        internal bool UpdateLinkHover(int offset,bool ctrl,bool readOnly) {
            if (disposed) return false;
            if (offset==hoverOffset && ctrl==hoverCtrl && readOnly==hoverReadOnly) return LinkTipShown;
            hoverOffset = offset; hoverCtrl = ctrl; hoverReadOnly = readOnly; LinkHoverChecks++;
            string text = offset<0 ? null : LinkTipAt(offset);
            if (text==null) { HideLinkTip(); SetLinkCursor(false); return false; }
            ShowLinkTip(text);
            SetLinkCursor(readOnly || ctrl);
            return true;
        }
        void ShowLinkTip(string text) {
            if (linkTip==null) {
                linkTip=new ToolTip { Placement=System.Windows.Controls.Primitives.PlacementMode.Mouse, PlacementTarget=View, StaysOpen=true, HasDropShadow=false, HorizontalOffset=14, VerticalOffset=18, MaxWidth=560 };
                linkTip.Content=new TextBlock { TextWrapping=TextWrapping.Wrap, MaxWidth=520 };
            }
            // A new link updates the open tooltip in place; the popup is not re-created, so the
            // pointer stays over the view and no hover stop/start flicker appears.
            TextBlock content = linkTip.Content as TextBlock;
            if (content!=null && content.Text==text && linkTip.IsOpen) return;
            linkTipText = text;
            if (content!=null) content.Text = text; else linkTip.Content = text;
            linkTip.IsOpen = true;
        }
        internal void HideLinkTip() {
            if (linkTip!=null && linkTip.IsOpen) linkTip.IsOpen = false;
        }
        // The hand cursor only where a click would follow the link: always in read mode, and in
        // edit mode only while Ctrl is held. Restores whatever local cursor value the view had.
        void SetLinkCursor(bool hand) {
            if (hand) {
                if (linkCursorSet) return;
                oldLinkCursor = View.ReadLocalValue(FrameworkElement.CursorProperty);
                linkCursorSet = true;
                View.Cursor = Cursors.Hand;
            } else if (linkCursorSet) {
                linkCursorSet = false;
                if (oldLinkCursor==DependencyProperty.UnsetValue) View.ClearValue(FrameworkElement.CursorProperty);
                else View.Cursor = (Cursor)oldLinkCursor;
            }
        }
        void LinkMouseMove(object sender,MouseEventArgs e) {
            if (disposed || Editor.Document!=Document) return;
            if (e.LeftButton!=MouseButtonState.Released || e.RightButton!=MouseButtonState.Released) {
                hoverOffset=-1; HideLinkTip(); SetLinkCursor(false); return;
            }
            ICSharpCode.AvalonEdit.TextViewPosition? hit = View.GetPositionFloor(e.GetPosition(View)+View.ScrollOffset);
            int offset = hit.HasValue ? Document.GetOffset(hit.Value.Location) : -1;
            UpdateLinkHover(offset,(Keyboard.Modifiers&ModifierKeys.Control)!=0,Options.ReadOnly||Editor.IsReadOnly);
        }
        void LinkMouseLeave(object sender,MouseEventArgs e) {
            hoverOffset=-1; HideLinkTip(); SetLinkCursor(false);
        }
        // Ctrl pressed or released without moving: the hand cursor follows the modifier even
        // when the pointer stays on the same offset (the deferred call reads the settled state).
        void LinkModifierChanged(object sender,KeyEventArgs e) {
            if (e.Key!=Key.LeftCtrl && e.Key!=Key.RightCtrl) return;
            if (hoverOffset<0) return;
            View.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate {
                if (disposed || hoverOffset<0) return;
                bool ctrl = (Keyboard.Modifiers&ModifierKeys.Control)!=0;
                if (ctrl==hoverCtrl) return;
                hoverCtrl = ctrl;
                SetLinkCursor(hoverReadOnly || ctrl);
            }));
        }
        // Fenced code (including the fence lines) at an offset, from the block cache.
        internal bool InCode(int offset) {
            if (disposed || offset < 0 || offset > Document.TextLength) return false;
            return Info(Document.GetLineByOffset(offset)).Code;
        }
        // Toolbar state at the caret from the cached line parse (no extra scan).
        internal void FormatAt(int offset, out bool bold, out bool italic, out bool strike, out bool code, out int heading) {
            bold = italic = strike = code = false;
            heading = 0;
            if (disposed || offset < 0 || offset > Document.TextLength) return;
            DocumentLine line = Document.GetLineByOffset(offset);
            LineInfo info = Info(line);
            heading = info.Heading;
            int relative = offset - line.Offset;
            // At the outer edge of a hidden closing run the caret draws on the pair's inside, so
            // the toolbar reports the format the next character would take.
            int inside = HiddenCloseInside(info, relative);
            if (inside >= 0) relative = inside;
            // The closing-edge space is pending (rule C): the next character goes back inside the
            // pair, so the toolbar shows the run's style lit after the space (O(markers)).
            else if (closeSpacePending && offset == closeSpaceCaret && closeSpaceStart >= line.Offset && closeSpaceStart <= line.Offset + info.Text.Length && PendingCloseSpaceIntact())
                relative = closeSpaceStart - line.Offset;
            foreach (StyledPart part in info.Styles) {
                if (relative < part.Start || relative > part.Start + part.Length) continue;
                bold |= (part.Style & Style.Bold) != 0;
                italic |= (part.Style & Style.Italic) != 0;
                strike |= (part.Style & Style.Strike) != 0;
                code |= (part.Style & Style.Code) != 0;
            }
        }

        public void Dispose() {
            if (disposed) return;
            disposed = true;
            ClearDelimiterSections();
            ClearReferenceSections();
            Document.Changing -= DocumentChanging;
            Document.Changed -= DocumentChanged;
            View.SizeChanged -= ViewSizeChanged;
            if (tableWork != null) { tableWork.Abort(); tableWork = null; }
            if (widthRedraw != null) { widthRedraw.Abort(); widthRedraw = null; }
            if (formatterRedraw != null) { formatterRedraw.Abort(); formatterRedraw = null; }
            if (caretGuardWork != null) { caretGuardWork.Abort(); caretGuardWork = null; }
            if (tableIdle != null) { tableIdle.Stop(); tableIdle.Tick -= TableIdle; }
            if (referenceIdle != null) { referenceIdle.Stop(); referenceIdle.Tick -= ReferenceIdle; }
            if (referenceScan != null) { referenceScan.Abort(); referenceScan = null; }
            tables.Clear();
            glyphs.Clear();
            Formatter.Dispose();
            images.Dispose();
            Editor.TextArea.Caret.PositionChanged -= ActiveChanged;
            Editor.TextArea.SelectionChanged -= ActiveChanged;
            View.PreviewMouseMove -= LinkMouseMove;
            View.MouseLeave -= LinkMouseLeave;
            Editor.PreviewKeyUp -= LinkModifierChanged;
            HideLinkTip();
            linkTip=null;
            SetLinkCursor(false);
            Editor.TextArea.PreviewMouseLeftButtonDown -= TextAreaPreviewMouseDown;
            Editor.TextArea.PreviewMouseLeftButtonUp -= TextAreaPreviewMouseUp;
            Editor.TextArea.PreviewMouseMove -= TableCaretDrag;
            Editor.TextArea.PreviewMouseMove -= WordCaretDrag;
            if (tableDragAnchor>=0) { tableDragAnchor=-1;tableDragPointerGuard=false; if (Editor.TextArea.IsMouseCaptured) Editor.TextArea.ReleaseMouseCapture(); }
            if (wordDrag) { EndWordDrag(); }
            if (wordScrollTimer != null) { wordScrollTimer.Stop(); wordScrollTimer.Tick -= WordScrollTick; wordScrollTimer=null; }
            if (dragScrollTimer != null) { dragScrollTimer.Stop(); dragScrollTimer.Tick -= DragScrollTick; dragScrollTimer=null; }
            Editor.TextArea.LostMouseCapture -= TextAreaLostMouseCapture;
            Editor.TextArea.PreviewKeyDown -= TextAreaKeyInput;
            Editor.TextArea.PreviewTextInput -= TextAreaKeyInput;
            CommandManager.RemovePreviewExecutedHandler(Editor.TextArea,HeaderLineEnd);
            CommandManager.RemovePreviewExecutedHandler(Editor.TextArea,TableCaretNavigation);
            CommandManager.RemovePreviewExecutedHandler(Editor.TextArea,HiddenRunLeft);
            CommandManager.RemovePreviewExecutedHandler(Editor.TextArea,DocumentStartHome);
            CommandManager.RemovePreviewExecutedHandler(Editor.TextArea,MultiCellDelete);
            CommandManager.RemovePreviewExecutedHandler(Editor.TextArea,HiddenMarkerDelete);
            CommandManager.RemovePreviewExecutedHandler(Editor.TextArea,HiddenPasteWiden);
            Editor.TextArea.TextEntering -= MultiCellEntering;
            Editor.TextArea.TextEntering -= HiddenSelectionEntering;
            Editor.TextArea.TextEntering -= HiddenCloseTypingEntering;
            Editor.TextArea.TextEntering -= HiddenCloseSpaceEntering;
            Editor.TextArea.TextEntering -= TableRowEntering;
            Editor.TextArea.TextEntering -= TablePipeTyping;
            Editor.TextArea.TextEntering -= TableGapEntering;
            Editor.TextArea.TextEntering -= HiddenFenceEntering;
            Editor.TextArea.PreviewMouseLeftButtonDown -= FenceRowMouseDown;
            Editor.TextArea.TextEntering -= SetextRuleEntering;
            Editor.TextArea.TextEntering -= FenceEnterEntering;
            Editor.PreviewKeyDown -= FenceEnterKeyDown;
            Editor.TextArea.TextEntered -= TableGapEntered;
            Editor.TextArea.TextEntered -= HiddenCloseSpaceEntered;
            Editor.TextArea.TextEntered -= SetextRuleEntered;
            CloseSetextRule();
            editableFenceLine = null; editableFenceVersion = null;
            closeSpaceArm = closeSpacePending = false;
            closeWordCaret = -1;closeWordDocument = null;closeWordVersion = null;
            CloseTableGap();
            Editor.CaretGuardChangedForTable = null;
            Editor.CellClamp = null;
            Editor.CommitTextInserter = null;
            Editor.PressHandledElsewhere = null;
            Editor.TableOffsetAt = null;
            DetachTableCaret();
            if (releaseTimer != null) releaseTimer.Stop();
            if (referenceSync != null) { referenceSync.Abort(); referenceSync = null; }
            InvalidateReferenceRun();
            frozen = false;
            Editor.DocumentChanged -= EditorDocumentChanged;
            View.VisualLinesChanged -= VisualLinesChanged;
            Editor.TextArea.TextEntered -= InputCompleted;
            TextCompositionManager.RemovePreviewTextInputStartHandler(Editor.TextArea,CompositionUpdated);
            TextCompositionManager.RemovePreviewTextInputUpdateHandler(Editor.TextArea,CompositionUpdated);
            if (compositionFrame) { CompositionTarget.Rendering-=CompositionRendering;compositionFrame=false; }
            if (compositionReapply!=null) { compositionReapply.Abort();compositionReapply=null; }
            View.LayoutUpdated -= ViewLayoutUpdated;
            View.ScrollOffsetChanged -= ViewScrollOffsetChanged;
            Editor.Loaded -= CompositionLoaded;
            Editor.Unloaded -= CompositionUnloaded;
            PresentationSource.RemoveSourceChangedHandler(Editor, CompositionSourceChanged);
            UnhookComposition();
            if (compositionWork!=null) { compositionWork.Abort(); compositionWork=null; }
            if (layoutWork!=null) { layoutWork.Abort(); layoutWork=null; }
            blocks.Rebuilt -= DocumentRebuilt;
            blocks.Dispose();
            View.ElementGenerators.Remove(markers);
            View.ElementGenerators.Remove(lazyQuotes);
            View.ElementGenerators.Remove(struts);
            View.ElementGenerators.Remove(fill);
            View.LineTransformers.Remove(transformer);
            View.LineTransformers.Remove(codeColors);
            View.BackgroundRenderers.Remove(renderer);
            selection.Dispose();
            Editor.Options.EnableRectangularSelection = oldRectangularSelection;
            View.LineHeight = oldHeight;
            lines.Clear();previousRowLine=null;previousRow=null;
            visible = new VisualLine[0];
            attachedSessions.Remove(this);
            LiveHandlerSets--;
        }
    }

    static LineInfo ParseLine(TextDocument document, DocumentLine line, BlockState before, double numberWidth, Session session) {
        var info = new LineInfo { Text=ShortText(document, line), Before=before };
        // Display math. A line inside a block is formula source: folded to nothing while the
        // block is rendered, plain source text while the caret or a selection is in it.
        if (before.Math > 0) {
            info.MathRemaining = before.Math;
            info.MathOpener = before.MathStart;
            info.Hide(0, line.Length);
            return info;
        }
        int mathCount = MathOpen(document, line, before);
        if (mathCount > 0) {
            info.MathCount = mathCount;
            info.MathOpener = line;
            info.MathLatex = MathBlockSource(document, line, mathCount);
            info.Replacements.Add(new Replacement { Start=0, Length=line.Length, Kind=ReplacementKind.Math, Level=1, Source=info.MathLatex });
            return info;
        }
        string mathOne;
        if (before.Fence == '\0' && before.Quote == 0 && line.Length > 4 && line.Length <= ParseLimit && MathRender.OneLineDisplay(info.Text, out mathOne)) {
            info.MathOne = true;
            MathRender.Seen = true;
            info.MathLatex = mathOne;
            info.Replacements.Add(new Replacement { Start=0, Length=line.Length, Kind=ReplacementKind.Math, Level=1, Source=mathOne });
            return info;
        }
        char fence;
        int fenceLength;
        bool tailBlank;
        ReadFence(document, line, out fence, out fenceLength, out tailBlank);
        info.FenceLine = before.Fence == '\0' ? fenceLength >= 3 : fence == before.Fence && fenceLength >= before.FenceLength && tailBlank;
        info.Code = before.Fence != '\0' || info.FenceLine;
        if (info.Code) {
            if (info.FenceLine) info.Hide(0, line.Length);
            return info;
        }
        // Oversized lines remain editable native text; block state still propagates.
        if (line.Length > ParseLimit) return info;
        string text = info.Text;
        int p = 0, columns = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { columns += text[p] == '\t' ? 4 : 1; p++; }
        int content = p, outerLevel = Math.Min(8, columns / 2);
        while (content < text.Length && text[content] == '>' && info.QuoteLevels < 6) {
            int end = content + 1;
            if (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
            info.Replacements.Add(new Replacement { Start=content, Length=end - content, Kind=ReplacementKind.Spacer, Width=0.9, Prefix=true, Scaled=true });
            info.QuoteLevels++;
            content = end;
        }
        info.Quote = info.QuoteLevels > 0;
        // CommonMark lazy continuation: a line with no marker of its own directly after a quote
        // paragraph line stays in the quote (the block cache made the same call). The line has
        // nothing to hide; only the muted text and the bar follow the quote state.
        if (!info.Quote && LazyQuoteLine(text, before, document, line)) {
            info.Quote = true; info.LazyQuote = true;
            info.LazyLevels = Math.Max(1, Math.Min(6, before.Quote));
        }
        int indentStart = content;
        // A rule's own indentation starts at the container's content: the line's leading spaces,
        // or the spaces after the last quote marker (the markers and their spacer are not
        // indentation for the break). Four or more columns make it paragraph text (IsRule).
        int ruleIndent = info.QuoteLevels > 0 ? 0 : columns;
        while (content < text.Length && (text[content] == ' ' || text[content] == '\t')) {
            int width = text[content] == '\t' ? 4 : 1;
            columns += width;
            ruleIndent += width;
            content++;
        }
        info.ListLevel = Math.Min(8, columns / 2);
        // CommonMark and the basic view (MarkdownPlainText.AtxHeading): at most three leading
        // whitespace characters set an ATX heading off; four or more make the line ordinary
        // paragraph text. A quote's own markers are not indentation, so the run after the last
        // ">" is what counts there, and a list item's heading is judged after its marker below.
        // The count is characters (a tab counts one), exactly what the basic renderer reads, so
        // both views draw the same line the same way.
        int headingIndent = info.QuoteLevels > 0 ? content - indentStart : p;
        int h = content;
        while (h < text.Length && text[h] == '#' && h - content < 7) h++;
        if (h > content && h - content <= 6 && headingIndent <= 3 && (h == text.Length || text[h] == ' ' || text[h] == '\t')) {
            info.Heading = h - content;
            if (h < text.Length) h++;
            info.HidePrefix(content, h - content);
            content = h;
            // The closing "#" run only counts when a space sets it off ("## 제목 ##" shows
            // 제목); a "#" glued to the text ("### C#") is content and stays visible.
            int closing = AtxClosingStart(text, content);
            if (closing >= 0) info.Hide(closing, text.Length - closing);
        }
        // The delimiter row belongs to the header directly above it, exactly like NextIsDelimiter
        // reads it (HideDelimiter folds header.NextLine). A later row that happens to look like
        // one ("|  -  |" typed into a data row) is an ordinary body row: GFM only accepts the
        // delimiter as the second line, and treating it as structure folded the user's row away
        // and left it without cells.
        info.Delimiter = IsDelimiter(text) && before.Table && before.TableStart != null && line.PreviousLine == before.TableStart;
        // A header row cannot start inside a table block: GFM ends a table at a blank or
        // non-table line, so a pipe row followed by a delimiter-looking row inside the block
        // is an ordinary data row. Treating it as a header folded the row below it away
        // (StrutGenerator collapses a header's next line) while that line's own Delimiter
        // stayed false, so one line disappeared and the visible row took header styling.
        info.Header = !before.Table && HasTablePipe(text) && NextIsDelimiter(document, line);
        info.Table = before.Table && HasTablePipe(text) || info.Header;
        info.TableStart = info.Table ? before.Table ? before.TableStart : line : null;
        if (info.Table) {
            // Every row inherits the header's list context, never its own leading spaces.
            // Incidental spaces on a top-level table do not create a list indentation. A table
            // that follows a blank line is a block of its own: the block cache ends the list
            // there (ListGap), and the print and TXT renderers clear their list state at it, so
            // this line may not draw a list indentation the other two do not have.
            int tableColumns=before.Table ? before.TableColumns : columns;
            ListState parent=before.List;
            if (parent!=null && !before.ListGap && tableColumns>parent.Indent)
                info.TableIndent=ListContentWidth(parent,numberWidth,session.Options.FontScale);
        }
        if (info.Delimiter) {
            info.Replacements.Clear();
            info.Hide(0, text.Length);
            return info;
        }
        if (info.Table) {
            info.Heading = 0;
            info.Quote = false;
            info.Replacements.Clear();
            info.Cells = SplitCells(text);
            ParseInline(info, 0, session);
            AddTableStops(info);
            SortReplacements(info);
            return info;
        }
        // Setext underline ("=====" under a paragraph line). The basic view tests it before
        // the rule (MDNote.cs:51), so its line is never drawn as a thematic break; a "---"
        // with no paragraph directly above stays a rule because Before().Setext is false.
        // The line is folded onto the heading above (PrepareDelimiter) and only its own
        // caret reveals the raw underline.
        int setextUnderline = before.Setext && info.QuoteLevels == 0 ? SetextUnderlineLevel(text) : 0;
        if (setextUnderline > 0) {
            info.SetextUnderline = setextUnderline;
            info.Hide(0, text.Length);
            return info;
        }
        // The paragraph line above an underline takes the heading scale; the underline's
        // own line carries no text style. The basic view uses the same 2x / 1.6x sizes.
        if (info.Heading == 0 && info.QuoteLevels == 0 && SetextText(text, before.Setext)) {
            int setext = NextSetextLevel(document, line);
            if (setext > 0) { info.Setext = true; info.Heading = setext; }
        }
        if (IsRule(text, content, ruleIndent)) {
            info.Rule = true;
            info.Hide(content, text.Length - content);
            return info;
        }
        // Definition lines: "[^id]: text" shows the id as a superscript number followed by
        // muted text; "[id]: url" uses the same muted definition style.
        int definitionNoteStart, definitionNoteEnd, definitionColon, definitionBody;
        bool definitionNote;
        if (columns <= 3 && info.QuoteLevels == 0 && DefinitionLine(text, content, out definitionNote, out definitionNoteStart, out definitionNoteEnd, out definitionColon, out definitionBody)) {
            if (definitionNote) {
                info.Hide(content, 2);
                info.Hide(definitionNoteEnd, 1);
                info.Hide(definitionColon, 1);
                info.AddStyle(definitionNoteStart, definitionNoteEnd - definitionNoteStart, Style.Link | Style.Superscript, null);
                info.AddStyle(definitionBody, text.Length - definitionBody, Style.Muted | Style.Small, null);
                if (info.Links == null) info.Links = new List<LinkPart>();
                info.Links.Add(new LinkPart { Start=content, Length=definitionBody - content, Url="#ref:" + text.Substring(definitionNoteStart, definitionNoteEnd - definitionNoteStart) });
                ParseInline(info, definitionBody, session);
            } else {
                info.RefDefinition = true;
                info.AddStyle(content, text.Length - content, Style.Muted | Style.Small, null);
            }
            SortReplacements(info);
            return info;
        }
        ListPrefix prefix;
        // A blockquote's content can hold a list ("> - item", "> 1. item", "> - [ ] item"): the
        // marker is looked for after the quote prefix, and the quote markers keep their own
        // spacers, so the bar, the bullet and the item's indentation all render. Without a quote
        // the scan starts at the line start and counts the item's own indentation.
        int listScan = info.QuoteLevels > 0 ? indentStart : 0;
        if (ReadList(text, listScan, out prefix)) {
            info.List = NextList(text, listScan, line, before.List);
            info.ListLevel = info.List.Depth;
            info.ListStart = prefix.Start; info.ListEnd = prefix.End; info.ListBody = prefix.Body;
            double listScale=session.Options.FontScale;
            if (prefix.Start > listScan) info.Replacements.Add(new Replacement { Start=listScan, Length=prefix.Start - listScan, Kind=ReplacementKind.Spacer, Width=Math.Max(0,ListContentWidth(info.List,numberWidth,listScale)-ListSlotWidth(info.List,numberWidth,listScale)), Prefix=true });
            if (prefix.Task) {
                info.Replacements.Add(new Replacement { Start=prefix.Start, Length=prefix.End - prefix.Start, Kind=ReplacementKind.Spacer, Width=(ListMarkerSlot-TaskBoxWidth)*listScale, Prefix=true });
                info.Replacements.Add(new Replacement { Start=prefix.End, Length=prefix.Body - prefix.End, Kind=ReplacementKind.Task, Prefix=true });
            } else info.Replacements.Add(new Replacement { Start=prefix.Start, Length=prefix.End - prefix.Start,
                Kind=prefix.Ordered ? ReplacementKind.Number : ReplacementKind.Bullet,
                Label=prefix.Ordered ? info.List.Number.ToString(CultureInfo.InvariantCulture) + "." : "•", Prefix=true });
            content = prefix.Body;
            // A list item whose body starts with a blockquote marker ("- > text", also after a
            // task checkbox) is a quote inside the item. Hide the marker after the list prefix and
            // let the line keep the quote state, so the same muted text and bar render as a
            // top-level quote; the bar starts at the item's text x (ListBody), not after the ">".
            // The list prefix stops are disjoint, and the heading check below runs on the text
            // after the quote markers, matching the parser's listquote body.
            int listQuote = content;
            while (listQuote < text.Length && text[listQuote] == '>' && info.QuoteLevels < 6) {
                int end = listQuote + 1;
                if (end < text.Length && (text[end] == ' ' || text[end] == '\t')) end++;
                info.Replacements.Add(new Replacement { Start=listQuote, Length=end - listQuote, Kind=ReplacementKind.Spacer, Width=0.9, Prefix=true, Scaled=true });
                info.QuoteLevels++;
                listQuote = end;
            }
            if (info.QuoteLevels > 0) { info.Quote = true; content = listQuote; }
            // A list item whose body starts with an ATX heading ("- ### title") renders the heading,
            // not the literal hashes. The marker is hidden like a top-level heading; the list prefix
            // replacements are disjoint, so both survive SortReplacements.
            int listHeading = content;
            while (listHeading < text.Length && text[listHeading] == '#' && listHeading - content < 7) listHeading++;
            if (listHeading > content && listHeading - content <= 6 && (listHeading == text.Length || text[listHeading] == ' ' || text[listHeading] == '\t')) {
                    info.Heading = listHeading - content;
                if (listHeading < text.Length) listHeading++;
                info.HidePrefix(content, listHeading - content);
                content = listHeading;
                int listClosing = AtxClosingStart(text, content);
                if (listClosing >= 0) info.Hide(listClosing, text.Length - listClosing);
            }
        }
        ParseInline(info, content, session);
        SortReplacements(info);
        return info;
    }

    static void SortReplacements(LineInfo info) {
        info.Replacements.Sort(delegate(Replacement a, Replacement b) { return a.Start.CompareTo(b.Start); });
        // Only disjoint source ranges may be consumed by a generator.
        int last = -1;
        for (int i = 0; i < info.Replacements.Count; ) {
            Replacement replacement = info.Replacements[i];
            if (replacement.Start < last) info.Replacements.RemoveAt(i);
            else { last = replacement.Start + replacement.Length; i++; }
        }
        info.LeadEnd = LeadEndOf(info);
    }

    // End of the contiguous replacement run that starts at the line's first character. A run
    // that covers the whole line (a rule, a table delimiter, a setext underline) answers 0:
    // that line has no visible text, so its own stops stay where they are.
    static int LeadEndOf(LineInfo info) {
        // A table row's leading pipes belong to the row projection, which supplies its own
        // caret stops; treating them as a hidden line prefix pushed the caret out of an empty
        // first cell and past the row's first pipe (Stage 3d).
        if (info.Table) return 0;
        int end = 0;
        for (int i = 0; i < info.Replacements.Count; i++) {
            Replacement replacement = info.Replacements[i];
            if (!replacement.Prefix || replacement.Length <= 0) continue;
            if (replacement.Start > end) break;
            if (replacement.Start + replacement.Length > end) end = replacement.Start + replacement.Length;
        }
        return end < info.Text.Length ? end : 0;
    }

    // A thematic break: at most three leading whitespace columns (a tab counts four, CommonMark's
    // indentation), then three or more "-", "*" or "_" markers with only spaces between them, so
    // "___" and "_ _ _" break a line like "***" while "    ---" is paragraph text. indent is the
    // whitespace the container's content starts with (after a quote's markers on a quoted line).
    // A setext underline is judged first and only accepts "=" / "-", so "---" under a paragraph
    // stays a heading.
    static bool IsRule(string text, int start, int indent) {
        if (indent > 3) return false;
        char marker = '\0';
        int count = 0;
        for (int i = start; i < text.Length; i++) {
            char c = text[i];
            if (c == ' ' || c == '\t') continue;
            if (marker == '\0') marker = c;
            if ((c != '-' && c != '*' && c != '_') || c != marker) return false;
            count++;
        }
        return count >= 3;
    }

    // Setext underline: up to three leading whitespace columns, then one or more "=" (H1)
    // or two or more "-" (H2) and nothing but trailing whitespace, exactly the print view's
    // ^[ \t]{0,3}(=+|-{2,})[ \t]*$ (MDNote.cs ParseCore). Returns 1/2, or 0 when the line is
    // not an underline. A single "-" is the start of a list item being typed, not an underline:
    // with the one-dash rule a "-\n" typed under a paragraph drew that paragraph as a setext H2
    // (user video 16:49). The thematic break still needs three markers (IsRule).
    static int SetextUnderlineLevel(string text) {
        int p = 0;
        while (p < text.Length && p < 4 && (text[p] == ' ' || text[p] == '\t')) p++;
        if (p > 3 || p >= text.Length) return 0;
        char marker = text[p];
        if (marker != '=' && marker != '-') return 0;
        int count = 0;
        for (; p < text.Length && text[p] == marker; p++) count++;
        if (marker == '-' && count < 2) return 0;
        for (; p < text.Length; p++) if (text[p] != ' ' && text[p] != '\t') return 0;
        return marker == '=' ? 1 : 2;
    }

    // Keystroke-cost guard for the one-line lookahead: only a line whose first non-blank
    // character is "=" or "-" can be an underline, so ordinary typing never copies the line.
    static bool SetextUnderlineStart(TextDocument document, DocumentLine line) {
        int p = 0;
        while (p < line.Length && p < 4) {
            char c = document.GetCharAt(line.Offset + p);
            if (c != ' ' && c != '\t') break;
            p++;
        }
        if (p > 3 || p >= line.Length) return false;
        char marker = document.GetCharAt(line.Offset + p);
        return marker == '=' || marker == '-';
    }

    static int NextSetextLevel(TextDocument document, DocumentLine line) {
        DocumentLine next = line.NextLine;
        if (next == null || next.Length > ParseLimit) return 0;
        if (!SetextUnderlineStart(document, next)) return 0;
        return SetextUnderlineLevel(ShortText(document, next));
    }

    // The line carries no paragraph text yet: empty, or only the whitespace a blank line may hold
    // (at most three columns, so it is not indented code). Reads at most four characters: the
    // first non-blank one, or the fourth column, ends the scan.
    static bool SetextBlankLine(TextDocument document, DocumentLine line) {
        int p = 0, columns = 0;
        while (p < line.Length) {
            char c = document.GetCharAt(line.Offset + p);
            if (c != ' ' && c != '\t') return false;
            columns += c == '\t' ? 4 : 1;
            if (columns > 3) return false;
            p++;
        }
        return true;
    }

    static bool AtxStart(string text, int p) {
        int h = p;
        while (h < text.Length && text[h] == '#' && h - p < 7) h++;
        return h > p && h - p <= 6 && (h == text.Length || text[h] == ' ' || text[h] == '\t');
    }

    // The closing "#" run of an ATX heading, CommonMark's rule: hashes at the end of the line
    // set off by a space or tab, or filling the whole content ("## ###" is an empty heading).
    // A "#" glued to the text ("### C#") is content and has no closing run. Returns the start
    // of the run to hide, with the whitespace that sets it off, or -1.
    static int AtxClosingStart(string text, int bodyStart) {
        int end = text.Length;
        while (end > bodyStart && (text[end - 1] == ' ' || text[end - 1] == '\t')) end--;
        int hashes = end;
        while (hashes > bodyStart && text[hashes - 1] == '#') hashes--;
        if (hashes == end) return -1;                 // no trailing hashes
        if (hashes == bodyStart) return bodyStart;    // the content is only the closing run
        if (text[hashes - 1] != ' ' && text[hashes - 1] != '\t') return -1;
        while (hashes > bodyStart && (text[hashes - 1] == ' ' || text[hashes - 1] == '\t')) hashes--;
        return hashes;
    }

    // A line that a following underline turns into a setext heading: ordinary paragraph
    // text in the basic view's setext position (MDNote.cs:51 tests it before the rule).
    // Blank lines, quotes, ATX headings, tables, definitions, list items and thematic
    // breaks are never paragraph text, and neither is a "===" line that already serves as
    // the previous line's underline (so "a\n===\n---" keeps the "---" a rule), matching
    // CommonMark. The caller resolves tables first; this only judges the line's own shape.
    static bool SetextText(string text, bool underlinedBefore) {
        if (text.Length == 0) return false;
        if (underlinedBefore && SetextUnderlineLevel(text) > 0) return false;
        int p = 0, columns = 0;
        while (p < text.Length && (text[p] == ' ' || text[p] == '\t')) { columns += text[p] == '\t' ? 4 : 1; p++; }
        if (p >= text.Length) return false;
        if (text[p] == '>') return false;
        if (IsRule(text, p, columns)) return false;
        if (AtxStart(text, p)) return false;
        ListPrefix prefix;
        if (ReadList(text, out prefix)) return false;
        if (columns <= 3) {
            bool note; int idStart, idEnd, colon, body;
            if (DefinitionLine(text, p, out note, out idStart, out idEnd, out colon, out body)) return false;
        }
        return true;
    }

    // "[id]: url" or "[^id]: text" at the line start (after indentation). The caller has
    // already rejected quotes, code fences, tables and lines indented four or more columns.
    static bool DefinitionLine(string text, int start, out bool note, out int idStart, out int idEnd, out int colon, out int bodyStart) {
        note = false; idStart = idEnd = colon = bodyStart = 0;
        if (start >= text.Length || text[start] != '[') return false;
        int p = start + 1;
        if (p < text.Length && text[p] == '^') { note = true; p++; }
        int first = p;
        while (p < text.Length && p - first < 128 && text[p] != ']' && text[p] != '[') p++;
        if (p >= text.Length || text[p] != ']' || p == first) return false;
        idStart = first; idEnd = p;
        p++;
        if (p >= text.Length || text[p] != ':') return false;
        colon = p; p++;
        bodyStart = p;
        return true;
    }

    static bool Matches(string text, int p, string token) {
        return p >= 0 && p + token.Length <= text.Length && string.CompareOrdinal(text, p, token, 0, token.Length) == 0;
    }
    static int Find(string text, string token, int start, ref int budget) {
        for (int p = start; p + token.Length <= text.Length && budget > 0; p++, budget--) {
            if (text[p] == '\\') { p++; budget--; continue; }
            if (Matches(text, p, token)) return p;
        }
        return -1;
    }
    // The "]" that closes the link label opening at `open`, with bracket depth so a badge
    // ("[![alt](img.png)](url)") names the OUTER bracket: the inner image's "](" must not be
    // taken as this link's target. Nested images are skipped whole (label plus "(target)"),
    // nested "[" increases the depth, escapes walk as their own units. The image target ends
    // at ImageEnd, the same scan ParseInline uses: the first ")" is inside a nested pair
    // ("img(1).png") or a quoted title, not the end of the target. A "]' inside a code span
    // is still a label end - the label is matched before inline code exists (the same rule the
    // plain "] search used), so a backtick is an ordinary character here. Budget is the parser's
    // shared scan budget.
    static int LinkLabelEnd(string text, int open, ref int budget) {
        int depth = 1, p = open + 1;
        while (p < text.Length && budget > 0) {
            budget--;
            char c = text[p];
            if (c == '\\') { p += Math.Min(2, text.Length - p); continue; }
            if (c == '!' && p + 1 < text.Length && text[p + 1] == '[') {
                int imageLabel = Find(text, "]", p + 2, ref budget);
                if (imageLabel >= 0 && Matches(text, imageLabel, "](")) {
                    int imageClose = ImageEnd(text, imageLabel + 2, ref budget);
                    if (imageClose >= 0) { p = imageClose + 1; continue; }
                }
                p++;
                continue;
            }
            if (c == '[') { depth++; p++; continue; }
            if (c == ']') { depth--; if (depth == 0) return p; }
            p++;
        }
        return -1;
    }

    static int FindCodeEnd(string text, int start, int length, ref int budget) {
        int p = start;
        while (p < text.Length && budget > 0) {
            if (text[p] != '`') { p++; budget--; continue; }
            int first = p;
            while (p < text.Length && text[p] == '`' && budget > 0) { p++; budget--; }
            if (p - first == length) return first;
        }
        return -1;
    }
    // "<br>", "<br/>" and "<br />" (case-insensitive) end a line on a non-caret line.
    // Only the tag itself is inspected, so this stays O(1) per '<'.
    static int LineBreakEnd(string text, int start) {
        int p = start + 1;
        if (p + 1 >= text.Length || (text[p] != 'b' && text[p] != 'B') || (text[p + 1] != 'r' && text[p + 1] != 'R')) return -1;
        p += 2;
        while (p < text.Length && text[p] == ' ') p++;
        if (p < text.Length && text[p] == '/') p++;
        while (p < text.Length && text[p] == ' ') p++;
        if (p >= text.Length || text[p] != '>' || p - start > 8) return -1;
        return p + 1;
    }

    // HTML character references come from the TXT renderer's table (WebUtility's HTML 4 names
    // plus &apos; and numeric forms), so the screen, the print view and MD-to-TXT decode the same
    // names. Anything else stays as typed.
    static string DecodeEntity(string entity) {
        return MarkdownPlainText.DecodeEntity(entity);
    }
    static void ParseInline(LineInfo info, int start, Session session) {
        string text = info.Text;
        int budget = text.Length * 4;
        int[] opens = { -1, -1, -1, -1, -1 };
        string[] tokens = { "**", "__", "*", "_", "~~" };
        Style[] styles = { Style.Bold, Style.Bold, Style.Italic, Style.Italic, Style.Strike };
        // A link label owns its own inline markup (GFM parses emphasis, code and tags inside
        // "[...]"), so the scan walks the label's text and then skips the hidden "](url)" tail
        // whole: a marker inside the URL must never pair with the line. The opening state is
        // saved around the label so an unclosed marker inside it cannot pair past the link.
        int linkResume = -1, linkSkip = -1;
        int[] linkOpens = null;
        for (int i = start; i < text.Length && info.Styles.Count < MaxSpans && budget > 0; ) {
            if (linkResume >= 0 && i >= linkResume) {
                if (linkOpens != null) { Array.Copy(linkOpens, opens, opens.Length); linkOpens = null; }
                i = linkSkip; linkResume = -1; linkSkip = -1;
                continue;
            }
            budget--;
            char c = text[i];
            // #20: escapes display as the character they stand for (`\*` → *, `&#45;` → -),
            // as in any CommonMark viewer. Text pasted or converted by the rich path uses both.
            if (c == '\\') {
                // A backslash at the line's end is a hard line break: the character itself is
                // markup and hides, like an escaped punctuation mark (the line renderer already
                // turns the break itself into the line's own newline).
                if (i + 1 >= text.Length) info.Hide(i, 1);
                else if (text[i + 1] < 128 && char.IsPunctuation(text[i + 1]) || "$+<=>^`|~".IndexOf(text[i + 1]) >= 0) info.Hide(i, 1);
                i += Math.Min(2, text.Length - i);
                continue;
            }
            if (c == '&') {
                int semicolon = text.IndexOf(';', i + 1, Math.Min(10, text.Length - i - 1));
                string decoded = semicolon > i + 1 ? DecodeEntity(text.Substring(i, semicolon + 1 - i)) : null;
                if (decoded != null) {
                    info.Replacements.Add(new Replacement { Start=i, Length=semicolon + 1 - i, Kind=ReplacementKind.Glyph, Label=decoded });
                    i = semicolon + 1;
                    continue;
                }
            }
            if (c == '`') {
                int run = 1;
                while (i + run < text.Length && text[i + run] == '`') run++;
                int close = FindCodeEnd(text, i + run, run, ref budget);
                // A code span that starts inside a link label but closes past the label's "]" is
                // not this label's markup: the label walk continues at "]"(linkResume) and would
                // re-walk the hidden "](url)" tail if the jump went past it. Leave the backtick
                // as literal text and let the walk reach the label end.
                if (close >= 0 && !(linkResume >= 0 && close >= linkResume)) {
                    info.Hide(i, run);
                    info.Hide(close, run);
                    // CommonMark strips one space from each end when a code span both starts and
                    // ends with one and is not all spaces, exactly the basic view's UnpadCode:
                    // "` code `" shows "code".
                    if (close - i - run >= 2 && text[i + run] == ' ' && text[close - 1] == ' ' && !OnlySpaces(text, i + run, close)) {
                        info.Hide(i + run, 1);
                        info.Hide(close - 1, 1);
                    }
                    info.AddStyle(i + run, close - i - run, Style.Code, null);
                    i = close + run;
                    continue;
                }
                i += run;
                continue;
            }
            if (c == '$') {
                // Inline math "$x$" (MathRender.InlineEnd holds the rules). The span is skipped whole, so
                // emphasis markers inside a formula ("x_i * y_j") never pair with the text around it.
                // "$$" is never inline. A table row draws it in the cell (CellText / CellMathRun), but a span that
                // holds a column pipe belongs to no cell, so it stays source; "\|" is the cell's escape for a pipe.
                if (i + 1 < text.Length && text[i + 1] == '$') { i += 2; continue; }
                int mathClose = MathRender.InlineEnd(text, i, ref budget);
                if (mathClose > i + 1 && info.Table && HasCellPipe(text, i + 1, mathClose)) mathClose = -1;
                if (mathClose > i + 1 && !(linkResume >= 0 && mathClose >= linkResume)) {
                    string mathSource = text.Substring(i + 1, mathClose - i - 1);
                    if (info.Table && mathSource.IndexOf("\\|", StringComparison.Ordinal) >= 0) mathSource = mathSource.Replace("\\|", "|");
                    info.Replacements.Add(new Replacement { Start=i, Length=mathClose + 1 - i, Kind=ReplacementKind.Math, Source=mathSource });
                    info.MathInline = true;
                    MathRender.Seen = true;
                    i = mathClose + 1;
                    continue;
                }
                i++;
                continue;
            }
            if (c == '!' && Matches(text, i, "![")) {
                // The image's "](" must follow its own label: a later "](" (the next link on
                // the line) is not this picture's target.
                int labelEnd = Find(text, "]", i + 2, ref budget);
                int middle = labelEnd >= 0 && Matches(text, labelEnd, "](") ? labelEnd : -1;
                int close = middle < 0 ? -1 : ImageEnd(text, middle + 2, ref budget);
                if (close >= 0) {
                    // A table row gets the same replacement: CellText turns it into one image
                    // object inside the cell (the projection has no element for a cell), and
                    // AddTableStops drops it when the span crosses a column separator.
                    info.Replacements.Add(new Replacement {
                        Start=i, Length=close + 1 - i, Kind=ReplacementKind.Image,
                        Label=text.Substring(i + 2, middle - i - 2), Source=ImageSource(text.Substring(middle + 2, close - middle - 2))
                    });
                    i = close + 1;
                    continue;
                }
            }
            if (c == '[' && i + 2 < text.Length && text[i + 1] == '^' && linkResume < 0) {
                // Footnote reference [^id]; a definition "[^id]:" stays plain text.
                int note = Find(text, "]", i + 2, ref budget);
                if (note > i + 2 && (note + 1 >= text.Length || text[note + 1] != ':')) {
                    info.Hide(i, 2);
                    info.Hide(note, 1);
                    info.AddStyle(i + 2, note - i - 2, Style.Link | Style.Superscript, null);
                    if (info.Links == null) info.Links = new List<LinkPart>();
                    info.Links.Add(new LinkPart { Start=i, Length=note + 1 - i, Url="#^" + text.Substring(i + 2, note - i - 2) });
                    i = note + 1;
                    continue;
                }
            }
            if (c == '[' && (i == 0 || text[i - 1] != '!') && linkResume < 0) {
                // Reference link [text][id] (or [text][]): the label stays, "][id]" is hidden.
                int labelEnd = LinkLabelEnd(text, i, ref budget);
                if (labelEnd > i + 1 && labelEnd + 1 < text.Length && text[labelEnd + 1] == '[') {
                    int idEnd = Find(text, "]", labelEnd + 2, ref budget);
                    if (idEnd >= 0) {
                        info.Hide(i, 1);
                        info.Hide(labelEnd, idEnd + 1 - labelEnd);
                        info.AddStyle(i + 1, labelEnd - i - 1, Style.Link, null);
                        // The host resolves "ref:<id>" against the "[id]: url" definition on click.
                        string id = text.Substring(labelEnd + 2, idEnd - labelEnd - 2).Trim();
                        if (id.Length == 0) id = text.Substring(i + 1, labelEnd - i - 1);
                        if (info.Links == null) info.Links = new List<LinkPart>();
                        info.Links.Add(new LinkPart { Start=i, Length=idEnd + 1 - i, Url="ref:" + id });
                        // The label keeps its own inline markup; "][id]" is skipped whole.
                        linkOpens = (int[])opens.Clone();
                        linkResume = labelEnd; linkSkip = idEnd + 1;
                        i++;
                        continue;
                    }
                }
                // The inline link's "](" must follow this label directly. Searching for a later
                // "](" made a bracketed label swallow the image next to it: "[tip] ![icon](x)"
                // turned into one link over the image's "](" and the emphasis around the label
                // was never paired (its markers stayed visible).
                int middle = labelEnd > i + 1 && Matches(text, labelEnd, "](") ? labelEnd : -1;
                // The target ends at its matching ")", not the first one: a destination with a
                // balanced pair ("..._(동음이의)") and a quoted title hold ")" characters of their
                // own (the same scan an image target uses).
                int close = middle < 0 ? -1 : ImageEnd(text, middle + 2, ref budget);
                if (close >= 0) {
                    info.Hide(i, 1);
                    info.Hide(middle, close + 1 - middle);
                    info.AddStyle(i + 1, middle - i - 1, Style.Link, null);
                    if (info.Links == null) info.Links = new List<LinkPart>();
                    // The optional title ("url \"title\"", "url 'title'", "<url>") is not part of
                    // the destination, so Ctrl+click opens the URL alone.
                    info.Links.Add(new LinkPart { Start=i, Length=close + 1 - i, Url=ImageSource(text.Substring(middle + 2, close - middle - 2)) });
                    // GFM parses the label's inline content ("[**bold**](url)" is bold link
                    // text). Walk the label here; the hidden "](url)" tail is skipped whole at
                    // linkResume, and the saved opening state keeps an unclosed marker inside
                    // the label from pairing with text after the link.
                    linkOpens = (int[])opens.Clone();
                    linkResume = middle; linkSkip = close + 1;
                    i++;
                    continue;
                }
                // A shortcut reference link "[label]" is a link when the document defines that
                // label ("[label]: url"): GitHub renders the label, not the brackets. The id set
                // is the session's cached definition scan; before its first scan the text stays
                // literal, exactly like an undefined id.
                if (labelEnd > i + 1 &&
                    (labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(' && text[labelEnd + 1] != '[')) {
                    string shortcut = ReferenceLabel(text.Substring(i + 1, labelEnd - i - 1));
                    if (shortcut.Length > 0 && session != null && session.ReferenceLabelDefined(shortcut)) {
                        info.Hide(i, 1);
                        info.Hide(labelEnd, 1);
                        info.AddStyle(i + 1, labelEnd - i - 1, Style.Link, null);
                        if (info.Links == null) info.Links = new List<LinkPart>();
                        info.Links.Add(new LinkPart { Start=i, Length=labelEnd + 1 - i, Url="ref:" + shortcut });
                        // The label keeps its own inline markup, like every other link label.
                        linkOpens = (int[])opens.Clone();
                        linkResume = labelEnd; linkSkip = labelEnd + 1;
                        i++;
                        continue;
                    }
                }
            }
            if (c == '<') {
                int lineBreak = LineBreakEnd(text, i);
                if (lineBreak > i) {
                    // The caret line keeps the raw tag; every other line breaks, like the
                    // hidden markup around it. Bounded to the tag, never a scan.
                    info.Replacements.Add(new Replacement { Start=i, Length=lineBreak - i, Kind=ReplacementKind.LineBreak });
                    i = lineBreak;
                    continue;
                }
                // An HTML comment that closes on this same line is markup, not text (GitHub
                // hides it). A comment spanning lines is a block this line parser does not
                // track, so it stays literal like every other raw tag.
                if (Matches(text, i, "<!--")) {
                    int comment = Find(text, "-->", i + 4, ref budget);
                    if (comment >= 0) { info.Hide(i, comment + 3 - i); i = comment + 3; continue; }
                }
                // CommonMark angle autolink (<https://…>, <mailto:…>, <a@b.c>): the brackets are
                // markup, and the target opens like a written link.
                int autolink = AutolinkEnd(text, i);
                if (autolink > i) {
                    string address = text.Substring(i + 1, autolink - i - 2);
                    // An email autolink opens as a mail link (CommonMark's own destination).
                    if (address.IndexOf(':') < 0) address = "mailto:" + address;
                    info.Hide(i, 1);
                    info.Hide(autolink - 1, 1);
                    info.AddStyle(i + 1, autolink - i - 2, Style.Link, null);
                    if (info.Links == null) info.Links = new List<LinkPart>();
                    info.Links.Add(new LinkPart { Start=i, Length=autolink - i, Url=address });
                    i = autolink;
                    continue;
                }
            }
            if (c == '<' && (MatchesTag(text, i, "span") || MatchesTag(text, i, "font"))) {
                // <span style="color:..."> and <font color="..."> share one path: the basic
                // view reads colour and font-size from both (MDNote.cs:83,89-95) and hides
                // the tags. A <font> with nothing readable still hides, like the basic Span.
                bool font = char.ToLowerInvariant(text[i + 1]) == 'f';
                string name = font ? "font" : "span";
                int tagEnd = Find(text, ">", i + 5, ref budget);
                int close = tagEnd < 0 ? -1 : FindTagClose(text, tagEnd + 1, name, ref budget);
                if (close >= 0 && tagEnd - i <= 512) {
                    string tag = text.Substring(i, tagEnd + 1 - i);
                    Brush color = SpanColor(tag);
                    double em;
                    double size = SpanSize(tag, out em);
                    if (font || color != null || size > 0 || em > 0) {
                        info.Hide(i, tagEnd + 1 - i);
                        info.Hide(close, name.Length + 3);
                        info.AddStyle(tagEnd + 1, close - tagEnd - 1, Style.None, color, size, em);
                        // The content keeps its own inline markup: "**", "*", "~~", backticks and
                        // links inside the tag parse here, and their styles combine with the
                        // tag's (Bold+Size, Code+Size, Color+Bold). The closing tag is already
                        // hidden, so the scan simply walks over it and finds nothing to pair.
                        i = tagEnd + 1;
                        continue;
                    }
                }
            }
            int kind = -1;
            for (int t = 0; t < tokens.Length; t++) if (Matches(text, i, tokens[t])) { kind = t; break; }
            if (kind < 0) { i++; continue; }
            int length = tokens[kind].Length;
            if (c == '_' && i > 0 && i + length < text.Length && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + length])) { i += length; continue; }
            if (opens[kind] < 0) {
                if (i + length < text.Length && !char.IsWhiteSpace(text[i + length])) opens[kind] = i;
            } else if (i > opens[kind] + length && !char.IsWhiteSpace(text[i - 1])) {
                int open = opens[kind];
                info.Hide(open, length);
                info.Hide(i, length);
                info.AddStyle(open + length, i - open - length, styles[kind], null);
                opens[kind] = -1;
            }
            i += length;
        }
    }

    // A reference label as the print and TXT renderers match it: the raw text between the
    // brackets, compared case-insensitively (no escape or whitespace processing, the same rule
    // TryDefinition and the click-time definition lookup read).
    static string ReferenceLabel(string value) {
        return value;
    }

    static int ImageEnd(string text, int start, ref int budget) {
        int depth = 0;
        bool angle = false;
        char quote = '\0';
        for (int p = start; p < text.Length && budget > 0; p++, budget--) {
            char c = text[p];
            if (c == '\\') { p++; continue; }
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c == '<') angle = true;
            else if (c == '>') angle = false;
            else if (!angle && (c == '"' || c == '\'') && p > start && char.IsWhiteSpace(text[p - 1])) quote = c;
            else if (!angle && c == '(') depth++;
            else if (!angle && c == ')') { if (depth == 0) return p; depth--; }
        }
        return -1;
    }
    static string ImageSource(string value) {
        value = value.Trim();
        if (value.Length > 1 && value[0] == '<') {
            int end = value.IndexOf('>');
            if (end > 0) return value.Substring(1, end - 1);
        }
        // Ignore the optional Markdown title, but preserve spaces in local paths.
        int title = value.IndexOf(" \"", StringComparison.Ordinal);
        if (title < 0) title = value.IndexOf(" '", StringComparison.Ordinal);
        // A parenthesized title ("url (title)") is a title too, not part of the destination:
        // the basic view's TargetParts reads the destination as the first whitespace-delimited
        // token, so "[t](url (title))" opens "url". The run must end the value and be set off
        // by whitespace, the same way the quoted forms are.
        if (title < 0 && value.Length > 1 && value[value.Length - 1] == ')') {
            int depth = 0;
            for (int p = value.Length - 1; p >= 0; p--) {
                if (value[p] == ')') depth++;
                else if (value[p] == '(') {
                    depth--;
                    if (depth == 0) {
                        if (p > 0 && (value[p - 1] == ' ' || value[p - 1] == '\t')) title = p;
                        break;
                    }
                }
            }
        }
        return title < 0 ? value : value.Substring(0, title).TrimEnd();
    }
    // True when the range holds nothing but ASCII spaces: the basic view's UnpadCode test for
    // an inline code span that is only padding.
    static bool OnlySpaces(string text, int start, int end) {
        for (int p = start; p < end; p++) if (text[p] != ' ') return false;
        return true;
    }
    // A CommonMark angle autolink starting at "<": a scheme target ("<https://…>",
    // "<mailto:…>") or an email ("<a@b.c>") closing on the same line. Returns the offset after
    // the ">", or -1. Bounded to the candidate, never a line scan.
    static int AutolinkEnd(string text, int start) {
        int p = start + 1, limit = Math.Min(text.Length, start + 2048);
        while (p < limit && text[p] != '>') {
            char c = text[p];
            if (c == ' ' || c == '\t' || c == '<') return -1;
            p++;
        }
        if (p >= limit || text[p] != '>') return -1;
        string target = text.Substring(start + 1, p - start - 1);
        return IsAutolinkTarget(target) ? p + 1 : -1;
    }
    // The CommonMark shape: a 2-32 character URI scheme followed by ":" and something, or a
    // plain email address. Anything else is an ordinary raw tag and stays literal.
    static bool IsAutolinkTarget(string value) {
        int colon = value.IndexOf(':');
        if (colon > 0) {
            if (colon < 2 || colon > 32 || colon + 1 >= value.Length) return false;
            for (int k = 0; k < colon; k++) {
                char c = value[k];
                bool alpha = c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z';
                if (k == 0 ? !alpha : !(alpha || c >= '0' && c <= '9' || c == '+' || c == '.' || c == '-')) return false;
            }
            return true;
        }
        int at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at + 1 >= value.Length) return false;
        return value.IndexOf('.', at + 1) > at + 1;
    }

    // "<span" / "<font", followed by whitespace like the basic view's token regex
    // (MDNote.cs:20, IgnoreCase); the name comparison is case-insensitive there and here.
    static bool MatchesTag(string text, int p, string name) {
        if (p < 0 || p + name.Length + 1 >= text.Length || text[p] != '<') return false;
        for (int k = 0; k < name.Length; k++)
            if (char.ToLowerInvariant(text[p + 1 + k]) != name[k]) return false;
        char next = text[p + 1 + name.Length];
        return next == ' ' || next == '\t';
    }

    // The closing "</name>" of an inline tag (case-insensitive), honouring backslash escapes
    // exactly like the shared Find(). Same-name tags nest ("<span>a<span>b</span>c</span>"),
    // so depth counting here keeps each opener with its own closer; the first close only
    // belongs to the outer tag when no inner one opened in between.
    static int FindTagClose(string text, int start, string name, ref int budget) {
        int stop = text.Length - name.Length - 3;
        int depth = 1;
        for (int p = start; p <= stop && budget > 0; p++, budget--) {
            if (text[p] == '\\') { p++; budget--; continue; }
            if (text[p] != '<') continue;
            if (text[p + 1] == '/') {
                int k = 0;
                while (k < name.Length && char.ToLowerInvariant(text[p + 2 + k]) == name[k]) k++;
                if (k == name.Length && text[p + 2 + k] == '>') { depth--; if (depth == 0) return p; }
            } else {
                int k = 0;
                while (k < name.Length && char.ToLowerInvariant(text[p + 1 + k]) == name[k]) k++;
                if (k == name.Length && (text[p + 1 + name.Length] == ' ' || text[p + 1 + name.Length] == '\t')) depth++;
            }
        }
        return -1;
    }

    static Brush SpanColor(string tag) {
        int p = tag.IndexOf("color", StringComparison.OrdinalIgnoreCase);
        if (p < 0) return null;
        p += 5;
        while (p < tag.Length && char.IsWhiteSpace(tag[p])) p++;
        // CSS "color: red" (span style) and the HTML "color=\"red\"" attribute (font).
        if (p >= tag.Length || (tag[p] != ':' && tag[p] != '=')) return null;
        p++;
        while (p < tag.Length && char.IsWhiteSpace(tag[p])) p++;
        if (p < tag.Length && (tag[p] == '"' || tag[p] == '\'')) p++;
        int end = p;
        while (end < tag.Length && (char.IsLetterOrDigit(tag[end]) || tag[end] == '#')) end++;
        if (end == p || end - p > 64) return null;
        try { return BrushOf(tag.Substring(p, end - p)); }
        catch (FormatException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    // Font size from a span/font tag: "font-size:Npx", "Npt" (pt scaled by 96/72), "Nem"
    // and "N%" (both returned as a relative factor in `em`, like the basic view's
    // MDNote.cs:93-94), clamped to 6..96 px. Returns 0 when no usable size is present.
    static double SpanSize(string tag, out double em) {
        em = 0;
        int p = tag.IndexOf("font-size", StringComparison.OrdinalIgnoreCase);
        if (p < 0) return 0;
        p += 9;
        while (p < tag.Length && char.IsWhiteSpace(tag[p])) p++;
        if (p >= tag.Length || tag[p] != ':') return 0;
        p++;
        while (p < tag.Length && char.IsWhiteSpace(tag[p])) p++;
        int start = p;
        while (p < tag.Length && (char.IsDigit(tag[p]) || tag[p] == '.')) p++;
        if (p == start || p - start > 8) return 0;
        double value;
        if (!double.TryParse(tag.Substring(start, p - start), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)) return 0;
        char unit = p < tag.Length ? char.ToLowerInvariant(tag[p]) : ' ';
        char kind = p + 1 < tag.Length ? char.ToLowerInvariant(tag[p + 1]) : ' ';
        if (unit == '%') { em = value / 100; return 0; }
        if (unit == 'e' && kind == 'm') { em = value; return 0; }
        double size = unit == 'p' && kind == 't' ? value * 96.0 / 72.0 : value;
        if (size < 6) size = 6;
        if (size > 96) size = 96;
        return size;
    }

    // With word wrap the widest visible row defines the horizontal extent. When a short
    // line is the widest (e.g. a heading block at the top), every keystroke changed the
    // extent and cost 2-3 extra measure/render passes. A trailing zero-length filler takes
    // the remaining paragraph width so the extent stays at the view width.
    internal sealed class FillGenerator : VisualLineElementGenerator {
        readonly Session session;
        internal FillGenerator(Session session) { this.session = session; }
        public override int GetFirstInterestedOffset(int startOffset) {
            if (!session.Options.WordWrap) return -1;
            int end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
            return startOffset <= end ? end : -1;
        }
        public override VisualLineElement ConstructElement(int offset) { return new FillElement(); }
    }
    // The filler replaces AvalonEdit's implicit line-end stop (which sat after the filler,
    // at the right edge): a non-empty line ends at the filler's start; an empty line has
    // only the strut's column-0 stop.
    internal sealed class FillElement : VisualLineElement {
        internal FillElement() : base(1, 0) { }
        public override bool IsWhitespace(int visualColumn) { return true; }
        public override bool HandlesLineBorders { get { return true; } }
        public override int GetNextCaretPosition(int column, LogicalDirection direction, CaretPositioningMode mode) {
            if (RelativeTextOffset == 0) return -1;
            if (direction == LogicalDirection.Forward) return column < VisualColumn ? VisualColumn : -1;
            return column > VisualColumn ? VisualColumn : -1;
        }
        public override TextRun CreateTextRun(int column, ITextRunConstructionContext context) { return new FillRun(TextRunProperties); }
    }
    internal sealed class FillRun : InvisibleRun {
        internal FillRun(TextRunProperties properties) : base(properties, 0) { }
        public override bool HasFixedSize { get { return false; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            // A 2px margin keeps rounding from pushing the filler onto a new row.
            return new TextEmbeddedObjectMetrics(Math.Max(0, Math.Min(4000, remainingParagraphWidth - 2)), 0, 0);
        }
    }

    internal sealed class StrutGenerator : VisualLineElementGenerator {
        readonly Session session;
        internal StrutGenerator(Session session) { this.session = session; }
        public override int GetFirstInterestedOffset(int startOffset) {
            int offset = CurrentContext.VisualLine.FirstDocumentLine.Offset;
            return startOffset <= offset ? offset : -1;
        }
        public override VisualLineElement ConstructElement(int offset) {
            DiagLineBuilds++;
            DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
            LineInfo info = session.Info(line);
            session.PrepareDelimiter(line,(info.Header||info.Setext)&&session.HideDelimiter(line));
            double scale = HeadingScale[info.Heading];
            double height = Math.Max(session.Options.FontSize * scale, session.Options.FontSize * scale * session.Options.LinePercent / 100);
            // Raw table rows keep their source pipes; the same layout still supplies the
            // column stops that keep the caret row aligned with formatted neighbours.
            bool raw = session.Raw(line);
            if (info.Table) info.MathTouchKey = session.MathTouchKeyOf(line, info);
            info.Row = info.Table && !info.Delimiter && info.Cells != null && info.Cells.Count > 0 ? session.LayoutRow(info, raw) : null;
            // A display formula folds the lines under its opener while it is drawn, and unfolds them when it is source.
            if (info.MathCount > 0) session.PrepareMath(line, !raw);
            // A line drawn as nothing at all: a table delimiter row, a setext underline, or a
            // reference definition line while the caret is elsewhere (no-reveal mode).
            bool hiddenLine = (info.Delimiter || info.SetextUnderline>0) && !raw;
            if (!hiddenLine && info.RefDefinition) hiddenLine = session.HideReference(info, line);
            // The lines under a rendered display formula carry no height of their own.
            if (!hiddenLine && info.MathRemaining > 0 && !raw) hiddenLine = true;
            if (info.Row!=null || hiddenLine) height=0;
            // A list row keeps its fixed 4 px: scaling it with the font let small fonts at tight
            // line spacing pack rows below the gutter label height (settings probe, 8 px / 50%).
            else if (info.List!=null) height+=4;
            return new StrutElement(height, info.Row, line.Length == 0, session, LazyQuoteGenerator.Applies(session, line, info));
        }
    }

    // A lazily continued quote line has no ">" of its own, so no marker spacer pushes its text
    // right of the quote bar ("게" sat under the bar). This zero-text element is the indent the
    // quote's own lines get from their markers: one 0.9 em spacer per quote level. It sits right
    // after the strut, and the strut names the column after it as the line's start stop, so a
    // caret at offset 0 draws where the first glyph is. A revealed (raw) line shows its source
    // as it is and gets nothing.
    internal sealed class LazyQuoteGenerator : VisualLineElementGenerator {
        readonly Session session;
        internal LazyQuoteGenerator(Session session) { this.session = session; }
        internal static bool Applies(Session session, DocumentLine line, LineInfo info) {
            return info.LazyQuote && info.LazyLevels > 0 && !info.Code && !info.Table && !info.Delimiter &&
                info.SetextUnderline == 0 && !info.RefDefinition && info.MathCount == 0 && info.MathRemaining == 0 &&
                line.Length > 0 && !session.Raw(line);
        }
        public override int GetFirstInterestedOffset(int startOffset) {
            DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
            if (startOffset > line.Offset) return -1;
            return Applies(session, line, session.Info(line)) ? line.Offset : -1;
        }
        public override VisualLineElement ConstructElement(int offset) {
            LineInfo info = session.Info(CurrentContext.VisualLine.FirstDocumentLine);
            return new MarkerElement(0, session.Options.FontSize * 0.9 * info.LazyLevels, null, false, false, true);
        }
    }

    internal sealed class MarkerGenerator : VisualLineElementGenerator {
        struct RawStop {
            internal int Offset;
            internal int Column;
            internal Replacement Image, Prefix;
            internal bool ListPrefix;
        }
        readonly Session session;
        readonly List<RawStop> rawStops = new List<RawStop>();
        DocumentLine line;
        // The visual line these elements are built for; a hidden run's backward word step asks
        // it for the stop before the run (see InvisibleElement.GetNextCaretPosition).
        VisualLine visualLine;
        LineInfo info;
        int index;
        bool raw;
        internal MarkerGenerator(Session session) { this.session = session; }
        public override void StartGeneration(ITextRunConstructionContext context) {
            base.StartGeneration(context);
            visualLine = context.VisualLine;
            line = context.VisualLine.FirstDocumentLine;
            info = session.Info(line);
            index = 0;
            raw = session.Raw(line);
            if (info.Table && !info.Delimiter && info.Cells!=null && info.Cells.Count>0) return;
            if (!raw) return;
            // Raw lines hide no markup, but the image behind an image markup and the
            // column stops of a table row still take part in the layout.
            rawStops.Clear();
            foreach (Replacement replacement in info.Replacements) {
                if (replacement.Kind == ReplacementKind.Image)
                    rawStops.Add(new RawStop { Offset=line.Offset + replacement.Start + replacement.Length, Image=replacement });
            }
            if (info.Table && !info.Delimiter && info.Cells != null) {
                for (int c = 1; c < info.Cells.Count; c++)
                    rawStops.Add(new RawStop { Offset=line.Offset + info.Cells[c - 1].End, Column=c });
            }
            if (info.List != null) foreach (Replacement replacement in info.Replacements) {
                // Only the item's own prefix: the bullet/number, its indentation and the checkbox
                // slot. A quote marker ("- > text", or the "> " of a quoted item) keeps its own
                // spacer here, because on a raw line the '>' is source text, not a list marker.
                if (replacement.Start < info.ListBody && !replacement.Scaled)
                    rawStops.Add(new RawStop { Offset=line.Offset + replacement.Start, Prefix=replacement, ListPrefix=true });
            }
            rawStops.Sort(delegate(RawStop a, RawStop b) { return a.Offset.CompareTo(b.Offset); });
            int kept = 0;
            for (int i = 0; i < rawStops.Count; i++) {
                if (kept > 0 && rawStops[kept - 1].Offset == rawStops[i].Offset) continue;
                rawStops[kept++] = rawStops[i];
            }
            if (kept < rawStops.Count) rawStops.RemoveRange(kept, rawStops.Count - kept);
        }
        // True when this line's own visual line has to span a line collapsed directly below it: a
        // table header over its folded delimiter row, a paragraph over its folded setext underline,
        // or the host line above a folded reference run. Every path below must offer line.EndOffset
        // for these, because ConstructElement turns that offset into the element covering the
        // collapsed line. A header row whose cells failed to parse (e.g. a line holding only
        // " |") is drawn as plain text, and this stop is the only thing that keeps its folded
        // delimiter row inside the visual line instead of letting the walk start a build on it.
        bool FoldedBelow() {
            return (info.Header && session.HideDelimiter(line)) || FoldedSetext() || session.ReferenceBelow(line) != null || session.MathBelow(line) != null;
        }
        public override int GetFirstInterestedOffset(int startOffset) {
            if (info.Table && !info.Delimiter && info.Cells!=null && info.Cells.Count>0) {
                if(startOffset<=line.Offset)return line.Offset;
                return FoldedBelow()&&startOffset<=line.EndOffset ? line.EndOffset : -1;
            }
            // A reference definition line that the no-reveal mode folds away is one element
            // over the whole line, so none of its markup positions produce a stop.
            if (!raw && info.RefDefinition && session.HideReference(info, line))
                return startOffset <= line.Offset ? line.Offset : -1;
            if (raw) {
                // Zero-document-length elements consume no text, so the engine asks again
                // at startOffset + 1; drop stops behind it instead of repeating forever.
                while (index < rawStops.Count && rawStops[index].Offset < startOffset) index++;
                int stop = index < rawStops.Count ? rawStops[index].Offset : -1;
                if ((stop < 0 || stop > line.EndOffset) && startOffset <= line.EndOffset && FoldedBelow())
                    return line.EndOffset;
                return stop;
            }
            while (index < info.Replacements.Count && line.Offset + info.Replacements[index].Start < startOffset) index++;
            int next = index < info.Replacements.Count ? line.Offset + info.Replacements[index].Start : -1;
            // A folded setext underline joins the heading line (see ConstructElement): the
            // heading's visual line must span the collapsed line or the caret there has no
            // visual column. A folded reference definition line joins the line above it for
            // the same reason, and a folded delimiter row joins its header (also when the
            // header row is drawn from its source or as plain text): the visual line walk must
            // never reach a collapsed line.
            if ((next < 0 || next > line.EndOffset) && startOffset <= line.EndOffset && FoldedBelow())
                return line.EndOffset;
            return next;
        }
        bool FoldedSetext() { return info.Setext && session.HideDelimiter(line); }
        public override VisualLineElement ConstructElement(int offset) {
            // The delimiter's source stays intact. Its height-tree row is collapsed and its
            // characters join the header visual line, so no default-font-sized empty band remains.
            if(offset==line.EndOffset&&info.Header&&session.HideDelimiter(line))return new InvisibleElement(line.DelimiterLength+line.NextLine.Length,0);
            // A folded setext underline joins its heading line the same way, so the heading's
            // visual line covers the collapsed line's characters (caret column stays valid).
            if(offset==line.EndOffset&&FoldedSetext())return new InvisibleElement(line.DelimiterLength+line.NextLine.Length,0);
            // A folded run of reference definition lines joins this line the same way; the
            // element covers every character up to the end of the folded run.
            if(offset==line.EndOffset) {
                CollapsedLineSection reference=session.ReferenceBelow(line);
                if(reference!=null)return new InvisibleElement(reference.End.EndOffset-line.EndOffset,0);
                // The folded lines under a rendered display formula join its opener line the same way.
                CollapsedLineSection math=session.MathBelow(line);
                if(math!=null)return new InvisibleElement(math.End.EndOffset-line.EndOffset,0);
            }
            // A reference definition line with the caret elsewhere: nothing is drawn and the
            // line keeps the zero height its strut reported. The caret line is never here.
            if(!raw && info.RefDefinition && session.HideReference(info, line))return new InvisibleElement(line.Length,0);
            if (info.Row!=null) return new TableRowElement(info,info.Row);
            if (raw) {
                RawStop stop = rawStops[index++];
                if (stop.ListPrefix) {
                    Replacement prefix = stop.Prefix;
                    if (prefix.Kind == ReplacementKind.Spacer) return new MarkerElement(prefix.Length, prefix.Width, null, false);
                    string label = info.Text.Substring(prefix.Start, prefix.Length).Trim();
                    bool number = prefix.Kind == ReplacementKind.Number;
                    double prefixScale=session.Options.FontScale;
                    return new MarkerElement(prefix.Length, number ? NumberMarkerWidth(label, session.NumberWidth, prefixScale) : (prefix.Kind == ReplacementKind.Task ? TaskBoxWidth : ListMarkerSlot) * prefixScale, label, false, number);
                }
                if (stop.Column > 0) {
                    RowLayout row = info.Row;
                    return row == null ? null : (VisualLineElement)new RawSpacerElement(row, stop.Column);
                }
                return new RawImageElement(session.ImageObject(stop.Image));
            }
            Replacement replacement = info.Replacements[index++];
            // Stage 3a: on a line whose leading markup is hidden, the leading run has no caret
            // stop of its own. Only the no-reveal mode takes part; the raw reveal keeps every
            // marker stop it has today.
            bool lead = session.Options.HideMarkers && replacement.Start < info.LeadEnd;
            if (replacement.Kind == ReplacementKind.Hidden) return new InvisibleElement(replacement.Length, 0, lead, visualLine);
            if (replacement.Kind == ReplacementKind.LineBreak) return new BreakElement(replacement.Length);
            if (replacement.Kind == ReplacementKind.Spacer) return new MarkerElement(replacement.Length, replacement.Scaled ? replacement.Width * session.Options.FontSize : replacement.Width, null, false, false, lead);
            if (replacement.Kind == ReplacementKind.Bullet) return new MarkerElement(replacement.Length, ListMarkerSlot * session.Options.FontScale, "•", false, false, lead);
            if (replacement.Kind == ReplacementKind.Number) return new MarkerElement(replacement.Length, NumberMarkerWidth(replacement.Label, session.NumberWidth, session.Options.FontScale), replacement.Label, false, true, lead);
            if (replacement.Kind == ReplacementKind.Column) return new ColumnElement(replacement.Length, info.Row, replacement.Column);
            if (replacement.Kind == ReplacementKind.Image) {
                UIElement picture=session.ImageObject(replacement);
                var image=picture as Image;
                // A picture that fits the text band is an icon and is centred on the ink band;
                // a larger one keeps the stock inline object (its top at the row's top).
                return image==null ? (VisualLineElement)new InlineObjectElement(replacement.Length,picture)
                    : new InlineImageElement(replacement.Length,new ImageHost(image));
            }
            if (replacement.Kind == ReplacementKind.Math) {
                // The source stays text while the caret touches an inline formula (no-reveal mode)
                // and when it does not parse; otherwise the formula is one object with one caret stop.
                if (session.MathTouched(line, replacement) || !MathRender.Check(replacement.Source)) return null;
                return new MathElement(replacement.Length, replacement.Source, replacement.Level == 1, session.Options.Family, session.MathAvailable);
            }
            if (replacement.Kind == ReplacementKind.Glyph) return new FormattedTextElement(replacement.Label, replacement.Length);
            DocumentLine taskLine = line;
            int column = replacement.Start;
            double boxScale=session.Options.FontScale;
            var box = new CheckBox {
                IsChecked=info.Text[column + 1] != ' ', Focusable=false, IsTabStop=false,
                Foreground=session.Colors.Foreground, Background=session.Colors.Background,
                VerticalAlignment=VerticalAlignment.Center, Width=16*boxScale, Height=16*boxScale,
                Margin=new Thickness(0, 0, 6*boxScale, 0), Cursor=Cursors.Hand
            };
            box.Tag = new Session.TaskTag { Line=taskLine, Column=column };
            // The host reports zero height, so it keeps the box on the row without making the
            // line taller, and it centres the box on the row's text band itself.
            var host = new TaskBoxHost(boxScale);
            host.Child = box;
            return new TaskBoxElement(replacement.Length, host, lead);
        }
        public override void FinishGeneration() {
            line = null;
            info = null;
            base.FinishGeneration();
        }
    }

    internal sealed class InvisibleElement : VisualLineElement {
        readonly double height;
        readonly bool prefix;
        // The visual line that built this run, when it came from a line's marker list.
        readonly VisualLine line;
        internal InvisibleElement(int documentLength, double height) : this(documentLength, height, false, null) { }
        internal InvisibleElement(int documentLength, double height, bool prefix) : this(documentLength, height, prefix, null) { }
        internal InvisibleElement(int documentLength, double height, bool prefix, VisualLine line) : base(1, documentLength) { this.height = height; this.prefix = prefix; this.line = line; }
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
            return new InvisibleRun(TextRunProperties, height);
        }
        public override bool IsWhitespace(int visualColumn) { return true; }
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) {
            if (DocumentLength == 0) return -1;
            // Backward, a hidden run is not a word boundary: its one drawn column is the same spot
            // as the visible text beside it, so a word step that stopped at its start looked like
            // nothing happened (Ctrl+Left just outside a closing run needed a second press). The
            // visible run's own word start is the stop, and the pair rules widen deletions. Forward
            // the stop is kept: it is the boundary between the text before the run and the text
            // after it.
            // Answering -1 alone made the engine ask the text before the run from the caret's
            // column, one past the run's start, so that search stopped on the run's edge - the
            // spot the press was meant to pass. Ask the line from this run's own start column
            // instead: the engine then only looks at elements before the run and the text gives
            // the word start the reader sees. A leading prefix has no visible text before it on
            // the line, so it keeps -1 and the step moves to the previous line.
            if (direction == LogicalDirection.Backward && (mode == CaretPositioningMode.WordStart || mode == CaretPositioningMode.WordStartOrSymbol)) {
                if (prefix || line == null || line.IsDisposed) return -1;
                return line.GetNextCaretPosition(VisualColumn, direction, mode, false);
            }
            // A hidden run draws zero width, so its two visual columns name one drawn spot. A
            // character step that stopped on the run's own edge therefore looked like nothing
            // happened and the next press did the work: Right from the text before an opener
            // stopped between the marker and the next character (the run's end column), Left
            // from before that character stopped between the marker and the text before it
            // (the run's start column). Continue the line search outside the run instead, so one
            // press crosses one visible character (a hidden opener before it and a hidden closer
            // after it included) and typing at the landing spot stays inside the pair. The word
            // moves above keep their own branch; asking the same visual line keeps this
            // O(current line).
            // Forward only: the backward search from a run's end is what the host's caret snapping
            // uses to land between an emphasis closer and a tag closer (InputFormatProbe "typing
            // just past the emphasis closer", SyntaxComboProbe span cases), so skipping there moved
            // typed text one character early. Left across an opener still stops once (known).
            if (mode == CaretPositioningMode.Normal && line != null && !line.IsDisposed) {
                if (direction == LogicalDirection.Forward && visualColumn == VisualColumn) {
                    // A run at the line end has no stop after it: keep the engine's own stop on its
                    // end then, or the caret could not leave a closer on the document's last line.
                    int next = line.GetNextCaretPosition(VisualColumn + VisualLength, LogicalDirection.Forward, mode, false);
                    if (next >= 0) return next;
                }
            }
            int stop = base.GetNextCaretPosition(visualColumn, direction, mode);
            // Stage 3a: a hidden line prefix has no stop of its own, so Home and Left put the
            // caret on the first visible character and typing never lands before the markup.
            if (prefix && stop == VisualColumn) return -1;
            return stop;
        }
    }
    // <br> on a non-caret line: the tag's columns collapse into one line-break column, so the
    // rest of the paragraph continues on the next text line. The document keeps the source.
    internal sealed class BreakElement : VisualLineElement {
        internal BreakElement(int documentLength) : base(1, documentLength) { }
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
            return new TextEndOfLine(1);
        }
    }
    internal class InvisibleRun : TextEmbeddedObject {
        readonly TextRunProperties properties;
        readonly double height;
        internal InvisibleRun(TextRunProperties properties, double height) { this.properties = properties; this.height = height; }
        public override int Length { get { return 1; } }
        public override TextRunProperties Properties { get { return properties; } }
        public override CharacterBufferReference CharacterBufferReference { get { return new CharacterBufferReference(); } }
        public override LineBreakCondition BreakBefore { get { return LineBreakCondition.BreakRestrained; } }
        public override LineBreakCondition BreakAfter { get { return LineBreakCondition.BreakRestrained; } }
        public override bool HasFixedSize { get { return true; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            return new TextEmbeddedObjectMetrics(0, height, height * 0.8);
        }
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) { return Rect.Empty; }
        public override void Draw(DrawingContext drawingContext, Point origin, bool rightToLeft, bool sideways) { }
    }
    // GetIndentationVisualColumn starts at this strut, not at the first source
    // character. All leading list/quote objects (including the checkbox) must
    // report whitespace; BuildVisualLine keeps the native < half-view-width guard.
    // Line start: outside table rows the strut replaces AvalonEdit's implicit line-start stop;
    // on an empty line it owns the single caret stop at column 0 and maps offset 0 there. Before,
    // offset 0 mapped to column 1, which on an empty line is no stop, so the caret snapped
    // past the filler to the right edge and the first Backspace only moved it back.
    internal sealed class StrutElement : VisualLineElement {
        readonly double height;
        readonly RowLayout row;
        readonly bool empty;
        readonly Session session;
        readonly bool lazyLead;
        internal StrutElement(double height, RowLayout row, bool empty, Session session) : this(height, row, empty, session, false) { }
        internal StrutElement(double height, RowLayout row, bool empty, Session session, bool lazyLead) : base(1, 0) { this.height = height; this.row = row; this.empty = empty; this.session = session; this.lazyLead = lazyLead; }
        internal RowLayout Layout { get { return row; } }
        public override bool IsWhitespace(int visualColumn) { return true; }
        public override bool HandlesLineBorders { get { return row == null; } }
        // Non-empty lines keep only the text's own start stop (column 1); a second stop at
        // column 0 for the same offset made Right/Backspace at the line start a no-op.
        // A lazy quote line's indent element (LazyQuoteGenerator) sits in column 1, so its text starts one column later.
        public override int GetVisualColumn(int relativeTextOffset) {
            if (lazyLead && row==null && relativeTextOffset<=RelativeTextOffset) return VisualColumn+VisualLength+1;
            if (row!=null && relativeTextOffset<=RelativeTextOffset) return VisualColumn+row.FirstColumn;
            return row == null && empty && relativeTextOffset <= RelativeTextOffset ? VisualColumn : base.GetVisualColumn(relativeTextOffset);
        }
        public override int GetNextCaretPosition(int column, LogicalDirection direction, CaretPositioningMode mode) {
            if (row != null || !empty) return -1;
            if (direction == LogicalDirection.Forward) return column < VisualColumn ? VisualColumn : -1;
            return column > VisualColumn ? VisualColumn : -1;
        }
        public override TextRun CreateTextRun(int column, ITextRunConstructionContext context) { return new StrutRun(TextRunProperties, height, row, session); }
    }
    internal sealed class StrutRun : InvisibleRun {
        readonly double height;
        readonly RowLayout row;
        readonly Session session;
        internal StrutRun(TextRunProperties properties, double height, RowLayout row, Session session) : base(properties, height) { this.height = height; this.row = row; this.session = session; }
        public override bool HasFixedSize { get { return false; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            // The first object receives precisely BuildVisualLine's paragraph width
            // (32000 in NoWrap). Never approximate it with TextView.ActualWidth.
            if (row != null) row.ParagraphWidth = remainingParagraphWidth;
            session.NoteParagraphWidth(remainingParagraphWidth, row);
            double width = 0;
            // TableProjection owns all cell padding, including the first column.
            if (row != null) width=0;
            return new TextEmbeddedObjectMetrics(width, height, height * 0.8);
        }
    }
    // Raw lines keep the image source text visible; the image object follows it without
    // consuming document text, so caret mapping and the source stay untouched.
    internal sealed class RawImageElement : InlineObjectElement {
        internal RawImageElement(UIElement element) : base(0, element) { }
        public override bool IsWhitespace(int visualColumn) { return true; }
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) { return -1; }
    }
    internal sealed class RawSpacerElement : VisualLineElement {
        readonly RowLayout row;
        readonly int tableColumn;
        internal RawSpacerElement(RowLayout row, int tableColumn) : base(1, 0) { this.row = row; this.tableColumn = tableColumn; }
        public override bool IsWhitespace(int visualColumn) { return true; }
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) { return -1; }
        public override TextRun CreateTextRun(int start, ITextRunConstructionContext context) { return new RawSpacerRun(TextRunProperties, row, tableColumn); }
    }
    internal sealed class RawSpacerRun : InvisibleRun {
        readonly RowLayout row;
        readonly int tableColumn;
        internal RawSpacerRun(TextRunProperties properties, RowLayout row, int tableColumn) : base(properties, 0) { this.row = row; this.tableColumn = tableColumn; }
        public override bool HasFixedSize { get { return false; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            if (row == null || tableColumn >= row.Targets.Length) return new TextEmbeddedObjectMetrics(0, 0, 0);
            // The visible pipe/padding text already advanced the cursor; pad the rest.
            double current = row.ParagraphWidth - remainingParagraphWidth;
            double separator = tableColumn < row.SeparatorWidths.Length ? row.SeparatorWidths[tableColumn] : 0;
            double width = Math.Max(row.Minimum, row.Targets[tableColumn] - current - separator);
            return new TextEmbeddedObjectMetrics(width, 0, 0);
        }
    }
    internal sealed class ColumnElement : VisualLineElement {
        readonly RowLayout row;
        readonly int column;
        internal ColumnElement(int length, RowLayout row, int column) : base(1, length) { this.row = row; this.column = column; }
        public override TextRun CreateTextRun(int start, ITextRunConstructionContext context) { return new ColumnRun(TextRunProperties, row, column); }
    }
    internal sealed class ColumnRun : InvisibleRun {
        readonly RowLayout row;
        readonly int column;
        internal ColumnRun(TextRunProperties properties, RowLayout row, int column) : base(properties, 0) { this.row = row; this.column = column; }
        public override bool HasFixedSize { get { return false; } }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            if (row == null || column >= row.Targets.Length) return base.Format(remainingParagraphWidth);
            double current = row.ParagraphWidth - remainingParagraphWidth;
            double width = Math.Max(row.Padding, row.Targets[column] - current);
            // No accumulated cursor: repeated Format calls yield the same metrics.
            // Boundaries are immutable and shared by every row of this table.
            return new TextEmbeddedObjectMetrics(width, 0, 0);
        }
    }
    // The task checkbox on the live surface. TextView.Arrange places every InlineObjectRun
    // element with its top at the TextLine's top and never applies the run's baseline
    // (vendor/Rendering/TextView.cs, ArrangeOverride), so the old 16px box, whose metrics
    // ended at the text baseline, stood entirely above the text. This host reports the same
    // 22px advance (16px box + the 6px gap) at zero height, so the row keeps its own height,
    // and it puts the checkbox on the row's text band when the row is arranged.
    internal sealed class TaskBoxElement : InlineObjectElement {
        readonly TaskBoxHost host;
        readonly bool prefix;
        internal TaskBoxElement(int length, TaskBoxHost element) : this(length, element, false) { }
        internal TaskBoxElement(int length, TaskBoxHost element, bool prefix) : base(length, element) { host = element; this.prefix = prefix; }
        public override bool IsWhitespace(int visualColumn) { return true; }
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) {
            int stop = base.GetNextCaretPosition(visualColumn, direction, mode);
            // Stage 3a: the box is part of a hidden leading run, so it keeps only its end stop.
            if (prefix && stop == VisualColumn) return -1;
            return stop;
        }
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
            if (context == null) throw new ArgumentNullException("context");
            return new TaskBoxRun(1, startVisualColumn, TextRunProperties, host);
        }
    }
    // Custom run: it remembers its visual column so the host can pick the TextLine that holds
    // it (a wrapped line has several) and read that line's baseline.
    internal sealed class TaskBoxRun : InlineObjectRun {
        internal readonly int Column;
        internal TaskBoxRun(int length, int column, TextRunProperties properties, TaskBoxHost element)
            : base(length, properties, element) { Column = column; element.Bind(this); }
    }
    internal sealed class TaskBoxHost : Decorator {
        internal const double BaseBoxSize = 16;
        // 11-01: the gap fills the shared list slot, so a task item's text starts where a bullet's does.
        internal const double BaseGap = TaskBoxWidth - BaseBoxSize;
        // The box and its gap follow the font size (1 at the default 16 px): a zoomed task
        // item would otherwise keep a 16 px box while its text grows or shrinks around it.
        internal readonly double BoxSize, Gap;
        internal TaskBoxHost() : this(1) { }
        internal TaskBoxHost(double scale) { if (scale<=0) scale=1; BoxSize=BaseBoxSize*scale; Gap=BaseGap*scale; }
        internal TaskBoxRun Run;
        TextLine measured;
        double offset;
        // A new run (a rebuilt line or a re-wrapped row) can name another row or column, so
        // the cached offset no longer applies.
        internal void Bind(TaskBoxRun run) { Run = run; measured = null; offset = 0; }
        protected override Size MeasureOverride(Size availableSize) {
            // The child keeps its own scaled footprint (the box plus its gap); only the height
            // this host reports to the line is zeroed.
            if (Child != null) Child.Measure(new Size(BoxSize + Gap, BoxSize));
            return new Size(BoxSize + Gap, 0);
        }
        protected override Size ArrangeOverride(Size finalSize) {
            // TextView.Arrange puts this element's top exactly at the row's top, so the offset
            // is the distance from the row top to the vertical centre of the text band. The
            // full slot keeps the box's own margin; the box size and its gap are font-scaled.
            if (Child != null) Child.Arrange(new Rect(0, Offset(), BoxSize + Gap, BoxSize));
            return new Size(BoxSize + Gap, 0);
        }
        // Centre of the band the glyphs occupy: baseline - ascent .. baseline + descent. This
        // is where the basic view's InlineUIContainer (BaselineAlignment.Center) puts the box.
        // The baseline is the row's own, so the strut's 0.8 share, a larger run on the line and
        // the line-spacing option stay consistent. Ascent and descent come from the typeface
        // and size of the item text that follows the box, never from a fixed pixel value.
        double Offset() {
            VisualLine line = Run == null ? null : Run.VisualLine;
            if (line == null || line.IsDisposed || line.TextLines == null) { measured = null; return 0; }
            // The TextLine that holds the box: the first whose columns reach the box's own
            // column (the rule VisualLine.GetTextLine uses, written out so a stale column
            // cannot throw while the row is being arranged).
            TextLine textLine = null;
            int start = 0;
            foreach (TextLine candidate in line.TextLines) {
                textLine = candidate;
                if (start + candidate.Length > Run.Column) break;
                start += candidate.Length;
            }
            // One TextLine instance per formatting pass: the same reference means the same
            // baseline, so only a rebuilt line recomputes the offset.
            if (Object.ReferenceEquals(textLine, measured)) return offset;
            measured = textLine;
            offset = 0;
            if (textLine == null) return 0;
            TextRunProperties properties = BandProperties(textLine, Run.Column - start);
            if (properties == null) properties = Run.Properties;
            if (properties == null) return 0;
            var metric = LiveCompositionGeometry.Metric(properties.Typeface, properties.FontRenderingEmSize);
            double descent = metric.Height - metric.Baseline;
            offset = textLine.Baseline - (metric.Baseline - descent) / 2 - BoxSize / 2;
            return offset;
        }
        // The first text run the box precedes: the item's own text at its own size and face.
        TextRunProperties BandProperties(TextLine textLine, int target) {
            TextRunProperties first = null;
            int at = 0;
            foreach (var span in textLine.GetTextRunSpans()) {
                TextRun run = span.Value;
                TextRunProperties properties = run == null ? null : run.Properties;
                if (properties != null && run is TextCharacters) {
                    if (first == null) first = properties;
                    if (at + span.Length > target) return properties;
                }
                at += span.Length;
            }
            return first;
        }
    }
    // An inline picture that fits inside the text band is an icon: the host reports zero height
    // (so the line keeps its own height and the line spacing never changes) and arranges the
    // picture on the band's vertical centre, because TextView.Arrange puts every inline object's
    // top at the text line's top. A larger picture keeps the stock InlineObjectElement geometry,
    // where the element's bottom stands on the baseline.
    internal sealed class InlineImageElement : InlineObjectElement {
        readonly ImageHost host;
        internal ImageHost Host { get { return host; } }
        internal InlineImageElement(int length, ImageHost element) : base(length, element) { host = element; }
        public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context) {
            if (context == null) throw new ArgumentNullException("context");
            return new InlineImageRun(1, startVisualColumn, TextRunProperties, host);
        }
    }
    internal sealed class InlineImageRun : InlineObjectRun {
        internal readonly int Column;
        internal InlineImageRun(int length, int column, TextRunProperties properties, ImageHost element)
            : base(length, properties, element) { Column = column; element.Bind(this); }
    }
    internal sealed class ImageHost : Decorator {
        internal InlineImageRun Run;
        TextLine measured;
        double offset;
        bool centred;
        internal ImageHost(UIElement child) { Child = child; }
        internal double ProbeOffset { get { return offset; } }
        internal bool ProbeCentred { get { return centred; } }
        internal double ProbeHeight { get { return Child == null ? 0 : Child.DesiredSize.Height; } }
        internal void Bind(InlineImageRun run) { Run = run; measured = null; offset = 0; centred = false; }
        protected override Size MeasureOverride(Size availableSize) {
            if (Child == null) return new Size(0, 0);
            Child.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
            Size size = Child.DesiredSize;
            // Only a decoded picture is centred; the muted "[이미지: alt]" placeholder is text
            // and keeps its normal line box.
            centred = Child is Image && size.Width > 0 && size.Height > 0 && size.Height <= BandHeight();
            return new Size(size.Width, centred ? 0 : size.Height);
        }
        protected override Size ArrangeOverride(Size finalSize) {
            if (Child == null) return new Size(0, 0);
            Size size = Child.DesiredSize;
            if (centred) Child.Arrange(new Rect(0, Offset(size.Height), size.Width, size.Height));
            else Child.Arrange(new Rect(0, 0, size.Width, Math.Max(size.Height, finalSize.Height)));
            return new Size(size.Width, centred ? 0 : Math.Max(size.Height, finalSize.Height));
        }
        double BandHeight() {
            TextRunProperties properties = Run == null ? null : Run.Properties;
            if (properties == null) return 0;
            return LiveCompositionGeometry.Metric(properties.Typeface, properties.FontRenderingEmSize).Height;
        }
        // Same formula as TaskBoxHost: the centre of the ink band (baseline - ascent .. baseline
        // + descent) minus half the picture's height, as a distance from the text line's top.
        double Offset(double height) {
            VisualLine line = Run == null ? null : Run.VisualLine;
            if (line == null || line.IsDisposed || line.TextLines == null) { measured = null; return 0; }
            TextLine textLine = null;
            int start = 0;
            foreach (TextLine candidate in line.TextLines) {
                textLine = candidate;
                if (start + candidate.Length > Run.Column) break;
                start += candidate.Length;
            }
            // One TextLine instance per formatting pass: the same reference means the same
            // baseline, so only a rebuilt line recomputes the offset.
            if (Object.ReferenceEquals(textLine, measured)) return offset;
            measured = textLine;
            offset = 0;
            if (textLine == null) return 0;
            TextRunProperties properties = BandProperties(textLine, Run.Column - start);
            if (properties == null) properties = Run.Properties;
            if (properties == null) return 0;
            var metric = LiveCompositionGeometry.Metric(properties.Typeface, properties.FontRenderingEmSize);
            double descent = metric.Height - metric.Baseline;
            offset = textLine.Baseline - (metric.Baseline - descent) / 2 - height / 2;
            return offset;
        }
        // The first text run the picture precedes: its own text at its own size and face.
        TextRunProperties BandProperties(TextLine textLine, int target) {
            TextRunProperties first = null;
            int at = 0;
            foreach (var span in textLine.GetTextRunSpans()) {
                TextRun run = span.Value;
                TextRunProperties properties = run == null ? null : run.Properties;
                if (properties != null && run is TextCharacters) {
                    if (first == null) first = properties;
                    if (at + span.Length > target) return properties;
                }
                at += span.Length;
            }
            return first;
        }
    }
    internal sealed class MarkerElement : VisualLineElement {
        readonly double width;
        readonly string label;
        readonly bool right, number, prefix;
        internal MarkerElement(int length, double width, string label, bool right) : this(length, width, label, right, false, false) { }
        internal MarkerElement(int length, double width, string label, bool right, bool number) : this(length, width, label, right, number, false) { }
        internal MarkerElement(int length, double width, string label, bool right, bool number, bool prefix) : base(1, length) { this.width = width; this.label = label; this.right = right; this.number = number; this.prefix = prefix; }
        internal string Label { get { return label; } }
        public override bool IsWhitespace(int visualColumn) { return true; }
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) {
            int stop = base.GetNextCaretPosition(visualColumn, direction, mode);
            // Stage 3a: same contract as InvisibleElement's hidden prefix.
            if (prefix && stop == VisualColumn) return -1;
            return stop;
        }
        public override TextRun CreateTextRun(int column, ITextRunConstructionContext context) { return new MarkerRun(TextRunProperties, width, label, right, number); }
    }
    internal sealed class MarkerRun : InvisibleRun {
        readonly FormattedText text;
        readonly double width;
        readonly bool right;
        internal MarkerRun(TextRunProperties properties, double width, string label, bool right, bool number) : base(properties, 0) {
            this.right = right;
            this.width = width;
            if (label == null) return;
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            bool disc=label=="•";
            text = new FormattedText(disc ? "\u009f" : label, properties.CultureInfo, FlowDirection.LeftToRight,
                disc ? new Typeface("Wingdings") : properties.Typeface, properties.FontRenderingEmSize, properties.ForegroundBrush);
            DiagMarkers++; DiagMarkerTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
            // Keep FlowDocument's .75em marker/body gap, but fit the ink inside
            // TextView's clip. The shared 1..999 slot also applies on revealed lines;
            // wider labels grow locally rather than spilling into the gutter. The disc does
            // not grow: its slot is font-scaled (18 px at the default 16 px), and slot, ink
            // and gap scale together, so a disc that fits at 16 px fits at every zoom. Growing
            // it moved list text 3 px at 100% (livelayout/renderfix list x checks).
            if (number) this.width = Math.Max(width, Math.Ceiling(text.WidthIncludingTrailingWhitespace + properties.FontRenderingEmSize * (MarkerLeadEm + MarkerGapEm)));
        }
        public override TextEmbeddedObjectMetrics Format(double remainingParagraphWidth) {
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            var metrics=new TextEmbeddedObjectMetrics(width, text == null ? 0 : text.Height, text == null ? 0 : text.Baseline);
            DiagMarkerTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
            return metrics;
        }
        public override Rect ComputeBoundingBox(bool rightToLeft, bool sideways) {
            return text == null ? Rect.Empty : new Rect(0, -text.Baseline, width, text.Height);
        }
        public override void Draw(DrawingContext context, Point origin, bool rightToLeft, bool sideways) {
            if (text == null) return;
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            double x = right ? width - text.WidthIncludingTrailingWhitespace - Properties.FontRenderingEmSize * MarkerGapEm : Properties.FontRenderingEmSize * MarkerLeadEm;
            context.DrawText(text, new Point(origin.X + x, origin.Y - text.Baseline));
            DiagMarkerTicks+=System.Diagnostics.Stopwatch.GetTimestamp()-started;
        }
    }

    internal sealed class MarkdownTransformer : DocumentColorizingTransformer {
        readonly Session session;
        internal MarkdownTransformer(Session session) { this.session = session; }
        protected override void ColorizeLine(DocumentLine line) {
            LineInfo info = session.Info(line);
            if (line.Length == 0 || info.Row!=null) return;
            if ((info.Delimiter || info.SetextUnderline > 0) && !session.Raw(line)) {
                ChangeLinePart(line.Offset,line.EndOffset,delegate(VisualLineElement element) { element.TextRunProperties.SetFontRenderingEmSize(.1); });
                return;
            }
            if (info.Heading > 0 || info.Quote || info.Code || info.Header) {
                // A heading inside a list keeps its bullet at body size; only the text after the
                // list prefix gets the heading typeface and size. A quote inside a list likewise
                // mutes only the item text, never the bullet or the number.
                int styleStart = info.List != null && (info.Heading > 0 || info.Quote) ? info.ListBody : 0;
                ChangeLinePart(line.Offset + styleStart, line.EndOffset, delegate(VisualLineElement element) {
                    VisualLineElementTextRunProperties p = element.TextRunProperties;
                    Typeface old = p.Typeface;
                    p.SetTypeface(new Typeface(info.Code ? CodeFamily : old.FontFamily, old.Style,
                        info.Heading > 0 || info.Header ? FontWeights.Bold : old.Weight, old.Stretch));
                    if (info.Heading > 0) p.SetFontRenderingEmSize(session.Options.FontSize * HeadingScale[info.Heading]);
                    if (info.Quote) p.SetForegroundBrush(session.Colors.QuoteText);
                });
            }
            foreach (StyledPart part in info.Styles) {
                StyledPart captured = part;
                ChangeLinePart(line.Offset + part.Start, line.Offset + part.Start + part.Length, delegate(VisualLineElement element) {
                    VisualLineElementTextRunProperties p = element.TextRunProperties;
                    Typeface old = p.Typeface;
                    Style style = captured.Style;
                    p.SetTypeface(new Typeface((style & Style.Code) != 0 ? CodeFamily : old.FontFamily,
                        (style & Style.Italic) != 0 ? FontStyles.Italic : old.Style,
                        (style & Style.Bold) != 0 ? FontWeights.Bold : old.Weight, old.Stretch));
                    if ((style & Style.Strike) != 0) p.SetTextDecorations(TextDecorations.Strikethrough);
                    if ((style & Style.Link) != 0) p.SetForegroundBrush(session.Colors.Link);
                    if ((style & Style.Muted) != 0) p.SetForegroundBrush(session.Colors.Muted);
                    if ((style & Style.Code) != 0) element.BackgroundBrush = session.Colors.InlineCode;
                    if (captured.Color != null) p.SetForegroundBrush(captured.Color);
                    if (captured.Size > 0) p.SetFontRenderingEmSize(captured.Size);
                    else if (captured.SizeEm > 0) {
                        double em = session.Options.FontSize * captured.SizeEm;
                        if (em < 6) em = 6;
                        if (em > 120) em = 120;
                        p.SetFontRenderingEmSize(em);
                    }
                    if ((style & Style.Small) != 0) p.SetFontRenderingEmSize(session.Options.FontSize * 0.9);
                    if ((style & Style.Superscript) != 0) {
                        p.SetFontRenderingEmSize(session.Options.FontSize * 0.75);
                        p.SetBaselineAlignment(BaselineAlignment.Superscript);
                    }
                });
            }
        }
    }

    internal sealed class BlockRenderer : IBackgroundRenderer {
        readonly Session session;
        Palette colors;
        Pen rule, quote, tableRule;
        internal BlockRenderer(Session session) {
            this.session = session;
        }
        // Pens follow the current palette; Update() swaps the Colors instance.
        void EnsurePens() {
            if (ReferenceEquals(colors, session.Colors)) return;
            colors = session.Colors;
            rule = new Pen(colors.Rule, 1);
            rule.Freeze();
            quote = new Pen(colors.QuoteBar, 3);
            quote.Freeze();
            tableRule=new Pen(BrushOf(session.Options.Dark ? "#707C8E" : "#ADB8C6"),.5); tableRule.Freeze();
        }
        public KnownLayer Layer { get { return KnownLayer.Background; } }
        public void Draw(TextView textView, DrawingContext drawingContext) {
            if (!textView.VisualLinesValid) return;
            DiagDraws++;
            EnsurePens();
            double width = Math.Max(0, textView.ActualWidth);
            double headerEnd = -1, headerLeft = 0;
            foreach (VisualLine line in textView.VisualLines) {
                LineInfo info = session.Info(line.FirstDocumentLine);
                double y = line.VisualTop - textView.VerticalOffset;
                if (info.Code) drawingContext.DrawRectangle(session.Colors.Code, null, new Rect(0, y, width, line.Height));
                bool raw = session.Raw(line.FirstDocumentLine);
                if (info.Quote && !raw) {
                    int levels = 0;
                    foreach (VisualLineElement element in line.Elements) {
                        int offset = element.RelativeTextOffset;
                        if (!(element is MarkerElement) || offset >= info.Text.Length || info.Text[offset] != '>') continue;
                        double x = line.GetVisualPosition(element.VisualColumn, VisualYPosition.LineTop).X - textView.HorizontalOffset + (info.List != null && offset >= info.ListBody ? 0 : session.Options.FontSize * 0.23);
                        drawingContext.DrawLine(quote, new Point(x, y), new Point(x, y + line.Height));
                        if (++levels >= info.QuoteLevels) break;
                    }
                    // A lazily continued quote line has no ">" of its own, so the marker walk
                    // above finds nothing. Its bars come from the quote state, one per level at the
                    // inset the quote's own lines use; the line's text is indented by the same
                    // spacers (LazyQuoteGenerator), so it starts right of the last bar.
                    if (levels == 0 && info.LazyQuote) {
                        int bars = LazyQuoteGenerator.Applies(session, line.FirstDocumentLine, info) ? info.LazyLevels : 1;
                        for (int bar = 0; bar < bars; bar++) {
                            double x = session.Options.FontSize * (0.9 * bar + 0.23) - textView.HorizontalOffset;
                            drawingContext.DrawLine(quote, new Point(x, y), new Point(x, y + line.Height));
                        }
                    }
                }
                StrutElement strut = line.Elements.Count == 0 ? null : line.Elements[0] as StrutElement;
                RowLayout row = strut == null ? null : strut.Layout;
                if (info.Table && !info.Delimiter && row != null) {
                    // A table under a list item starts at the item's body x: the boundaries carry
                    // the indent, so the shade and the rules use the same left edge.
                    double end = row.Boundaries[row.Boundaries.Length - 1], left=row.Indent-textView.HorizontalOffset;
                    Brush shade = info.Header ? session.Colors.TableHead : session.Colors.TableCell;
                    if (shade != null) drawingContext.DrawRectangle(shade, null, new Rect(left, y, Math.Max(0, end-row.Indent), line.Height));
                    for (int c = 0; c < row.Boundaries.Length; c++) {
                        double x = row.Boundaries[c] - textView.HorizontalOffset;
                        drawingContext.DrawLine(tableRule, new Point(x, y), new Point(x, y + line.Height));
                    }
                    drawingContext.DrawLine(tableRule,new Point(left,y),new Point(left+end-row.Indent,y));
                    drawingContext.DrawLine(tableRule,new Point(left,y+line.Height),new Point(left+end-row.Indent,y+line.Height));
                    if (info.Header) { headerLeft = left; headerEnd = left + end - row.Indent; }
                }
                // #17: the header rule is drawn once, through the delimiter row and only as wide
                // as the table (it used to be a short header underline plus a full-width rule).
                if (info.Delimiter && !raw) {
                    if (headerEnd > headerLeft && session.Colors.TableCell != null) drawingContext.DrawRectangle(session.Colors.TableCell, null, new Rect(headerLeft, y, headerEnd - headerLeft, line.Height));
                    if (headerEnd > headerLeft) drawingContext.DrawLine(rule, new Point(headerLeft, y + line.Height / 2), new Point(headerEnd, y + line.Height / 2));
                    headerEnd = -1;
                    continue;
                }
                if (info.Rule && !raw) {
                    double middle = y + line.Height / 2;
                    drawingContext.DrawLine(rule, new Point(8, middle), new Point(Math.Max(8, width - 8), middle));
                }
            }
        }
    }
}
}
