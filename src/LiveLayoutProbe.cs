using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Text;
using System.Collections;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
public partial class MainWindow {
    internal static int VerifyLiveLayout(string directory) { return LiveLayoutProbeRunner.Run(directory); }
}
// T7: generated data, actual FlowDocument reference and AvalonEdit glyph positions.
internal static class LiveLayoutProbeRunner {
    internal const string Fixture = "Table and list layout fixture\n\n- Bullet level one\n  - Bullet level two\n    - Bullet level three\n- Bullet level one again\n\n1. Number item 1\n2. Number item 2\n3. Number item 3\n4. Number item 4\n5. Number item 5\n6. Number item 6\n7. Number item 7\n8. Number item 8\n9. Number item 9\n10. Number item 10\n11. Number item 11\n12. Number item 12\n\n- [ ] Task pending\n- [x] Task complete\n\n| Key | Kind | Description |\n| --- | --- | --- |\n| A | one | This last column contains a long sentence that must wrap inside its own cell without overlapping the adjacent column or the table border. |\n| B | two | A short value. |\n| C | three | **Bold words** and `code_words` and <span style=\"font-size:22px\">larger text</span> stay in the cell. |\n\nEnd fixture\n";
    sealed class ReferenceItem {
        internal string Text, Marker;
        internal double X, MarkerX, MarkerRight, Y;
    }
    sealed class Ink {
        internal string Text;
        internal double X, Right, Y, Size;
        internal string Family;
    }
    // The same test can attach the untouched b0 assembly without changing its source
    // or entry points. AvalonEdit's shared public surface is the only static dependency.
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static Assembly Product { get { string path=Environment.GetEnvironmentVariable("MDNOTE_LAYOUT_BASELINE"); return String.IsNullOrEmpty(path) ? typeof(MainWindow).Assembly : Assembly.LoadFile(Path.GetFullPath(path)); } }
    static FieldInfo FieldOrFail(object value,string name) { var field=value.GetType().GetField(name,Members); if(field==null)throw new MissingMemberException("probe: "+value.GetType().Name+" has no field '"+name+"'"); return field; }
    static object Field(object value,string name) { return FieldOrFail(value,name).GetValue(value); }
    static void SetField(object value,string name,object target) { FieldOrFail(value,name).SetValue(value,target); }
    static object GetProperty(object value,string name) { var property=value.GetType().GetProperty(name,Members); if(property==null)throw new MissingMemberException("probe: "+value.GetType().Name+" has no property '"+name+"'"); return property.GetValue(value,null); }
    sealed class ProbeCell { internal int Start, End; }
    sealed class ProbeRow { internal double[] Boundaries; }
    sealed class ProbeInfo { internal string Text; internal bool Table, Delimiter; internal List<ProbeCell> Cells; internal ProbeRow Row; }
    sealed class ProbeSession : IDisposable {
        readonly object session;
        internal ProbeSession(TextEditor editor,bool wrap) {
            Type product=editor.GetType().Assembly.GetType("MDNote.MarkdownLivePreview"), settingsType=product.GetNestedType("Settings",Members);
            object settings=Activator.CreateInstance(settingsType,true);
            foreach (var pair in new Dictionary<string,object> {{"FontSize",16.0},{"Family","Malgun Gothic"},{"LinePercent",150.0},{"WordWrap",wrap}}) settingsType.GetField(pair.Key,Members).SetValue(settings,pair.Value);
            session=product.GetMethod("Attach",Members).Invoke(null,new object[] {editor,settings});
        }
        internal ProbeSession(object existing) {session=existing;}
        internal ProbeInfo Info(DocumentLine line) {
            object value=session.GetType().GetMethod("Info",Members).Invoke(session,new object[] {line}), row=Field(value,"Row");
            var cells=new List<ProbeCell>(); var source=Field(value,"Cells") as IEnumerable;
            if(source!=null)foreach(object cell in source)cells.Add(new ProbeCell {Start=(int)Field(cell,"Start"),End=(int)Field(cell,"End")});
            return new ProbeInfo {Text=(string)Field(value,"Text"),Table=(bool)Field(value,"Table"),Delimiter=(bool)Field(value,"Delimiter"),Cells=cells,
                Row=row==null ? null : new ProbeRow {Boundaries=(double[])Field(row,"Boundaries")}};
        }
        internal bool Raw(DocumentLine line) { return (bool)session.GetType().GetMethod("Raw",Members).Invoke(session,new object[] {line}); }
        public void Dispose() { ((IDisposable)session).Dispose(); }
    }
    static TextEditor Editor(bool wrap) {
        var editor=(TextEditor)Activator.CreateInstance(Product.GetType("MDNote.SourceEditor"),true);
        editor.FontFamily=new FontFamily("Malgun Gothic"); editor.FontSize=16; editor.WordWrap=wrap;
        editor.Foreground=Brushes.Black; editor.Background=Brushes.White; editor.BorderThickness=new Thickness(0); editor.Padding=new Thickness(0);
        editor.TextArea.TextView.LineHeight=double.NaN; return editor;
    }
    static readonly StringBuilder report = new StringBuilder();
    static int passes, failures;
    internal static int Run(string directory) {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        report.Length = 0; passes = failures = 0;
        if (Application.Current == null) new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        try {
            if (Environment.GetEnvironmentVariable("MDNOTE_LAYOUT_TYPING_ONLY")=="1") { MeasureTyping(directory); return 0; }
            File.WriteAllText(Path.Combine(directory, "layout-fixture.md"), Fixture, new UTF8Encoding(false));
            foreach (double width in new double[] { 420, 900 }) foreach (bool wrap in new bool[] { true, false }) {
                Compare(directory, width, wrap);
            }
            CheckCacheContract();
            CheckStructuralEdits();
            CheckTableShrink(directory);
            CheckSetextHeadings();
            CheckDashUnderText();
            CheckFontTags();
            CheckCellShadeUniform();
            CheckCellShadeMatchesLine();
            CheckKbdOverHighlight();
            CheckCellKbdNoWrap();
            CheckCellKbdWrapLayout();
            CheckCellKbdCases();
            CheckTableLongTokenPasteStability();
            CheckLargeTableTypingBeforeSampleCompletes();
            CheckTableTypedTokenContinuity();
        } catch (Exception ex) { Check("exception", false, ex.ToString()); }
        report.Append("TOTAL PASS=").Append(passes).Append(" FAIL=").Append(failures).Append('\n');
        File.WriteAllText(Path.Combine(directory, "livelayout-probe.txt"), report.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "T7-livelayout-" + (failures == 0 ? "PASS" : "FAIL") + ".txt"), report.ToString(), new UTF8Encoding(false));
        Application.Current.Shutdown(); return failures == 0 ? 0 : 1;
    }
    static void Compare(string directory, double width, bool wrap) {
        string key = "w" + width + "-" + (wrap ? "wrap" : "nowrap");
        var rich = new RichTextBox { Padding=new Thickness(0), BorderThickness=new Thickness(0), IsReadOnly=true, HorizontalScrollBarVisibility=wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto };
        rich.Document = Markdown.Parse(Fixture, 16, "Malgun Gothic", Brushes.Black, false, "");
        rich.Document.PagePadding = new Thickness(0); rich.Document.LineHeight = 24; rich.Document.LineStackingStrategy=LineStackingStrategy.MaxHeight;
        SetLines(rich.Document.Blocks);
        if (!wrap) rich.Document.PageWidth=1000;
        var editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=wrap, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editor.TextArea.TextView.LineHeight = double.NaN; editor.Text=Fixture;
        var session = MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=wrap });
        // The editor's own basic view (no caret-line reveal) of the same fixture: the list checks measure
        // the live view against it, not against the print-style FlowDocument above.
        var basicEditor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=wrap, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        basicEditor.TextArea.TextView.LineHeight = double.NaN; basicEditor.Text=Fixture;
        var basicSession = MarkdownLivePreview.Attach(basicEditor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=wrap, HideMarkers=true });
        Window richWindow = Host(rich, width), liveWindow = Host(editor, width), basicWindow = Host(basicEditor, width);
        try {
            richWindow.Show(); liveWindow.Show(); basicWindow.Show(); Pump(editor);
            basicEditor.TextArea.Caret.Line = 1; Pump(basicEditor);
            var refs = new List<ReferenceItem>(); Collect(rich.Document.Blocks, refs);
            var basicMeasures = new Dictionary<string,ListMeasure>();
            foreach (ReferenceItem item in refs) basicMeasures[item.Text] = MeasureList(basicEditor, basicSession, item.Text);
            var inks = new List<Ink>(); CollectInk(rich, Transform.Identity, inks);
            double bodyX=((Paragraph)rich.Document.Blocks.FirstBlock).ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward).X;
            foreach (ReferenceItem item in refs) {
                Paragraph paragraph = FindParagraph(rich.Document.Blocks, item.Text);
                Run body=FirstRun(paragraph.Inlines);
                Rect r = (body==null ? paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward) : body.ContentStart).GetCharacterRect(LogicalDirection.Forward);
                item.X=r.X-bodyX; item.Y=r.Y;
                Ink closest=null;
                foreach (Ink ink in inks) {
                    if (ink.Y < r.Top - 1 || ink.Y > r.Bottom + 1 || ink.Right > r.X + 1) continue;
                    if (!DiscMarker(ink.Text) && !NumberMarker(ink.Text)) { if(item.Text=="Bullet level one")report.Append("BULLET CANDIDATE ").Append(ink.Text).Append(" x=").Append(ink.X).Append(" size=").Append(ink.Size).Append(" family=").Append(ink.Family).Append('\n'); continue; }
                    if (closest == null || ink.Right > closest.Right) closest=ink;
                }
                if (closest != null) { item.Marker=closest.Text; item.MarkerX=closest.X-bodyX; item.MarkerRight=closest.Right-bodyX; }
                report.Append("REF ").Append(key).Append(' ').Append(item.Text).Append(" textX=").Append(N(item.X)).Append(" marker=").Append(item.Marker).Append(" x/right=").Append(N(item.MarkerX)).Append('/').Append(N(item.MarkerRight)).Append('\n');
            }
            for (int active = 0; active < 2; active++) {
                editor.TextArea.Caret.Line = active == 0 ? 1 : 8; Pump(editor);
                string label=key + (active == 0 ? " formatted" : " caret1");
                CheckLists(label, editor, session, refs, basicMeasures);
                CheckTables(label, editor, session);
                if (active == 1) Save(liveWindow, Path.Combine(directory, key + "-caret1.png"));
            }
            editor.TextArea.Caret.Line=26; Pump(editor);
            CheckTables(key + " caret-cell", editor, session);
            // Interior source offsets must round-trip, not just the cell endpoints.
            var visual=editor.TextArea.TextView.GetVisualLine(26);
            bool mapped=visual != null;
            if (visual != null) {
                var line=editor.Document.GetLineByNumber(26);
                for (int p=0; p<=line.Length; p++) if (visual.GetRelativeOffset(visual.GetVisualColumn(p)) != p) { mapped=false; report.Append("MAP ").Append(key).Append(" source=").Append(p).Append(" visual=").Append(visual.GetVisualColumn(p)).Append(" back=").Append(visual.GetRelativeOffset(visual.GetVisualColumn(p))).Append(" raw=").Append(session.Raw(line)).Append('\n'); break; }
            }
            Check(key + " editable raw cell mapping", mapped, "all source offsets round-trip");
            Save(richWindow, Path.Combine(directory, key + "-basic.png"));
        } finally { session.Dispose(); basicSession.Dispose(); richWindow.Close(); liveWindow.Close(); basicWindow.Close(); }
    }
    static void CheckCacheContract() {
        const string fixture="Cache fixture\n\n| key | value |\n| --- | --- |\n| 1 | repeated cell |\n| 22 | repeated cell |\n\nunrelated body\n\n| other | value |\n| --- | --- |\n| 999 | independent |\n\nend";
        var editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
        editor.Text=fixture;
        var session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
        Window window=Host(editor,500);
        try {
            window.Show(); Pump(editor);
            DocumentLine first=editor.Document.GetLineByNumber(5), other=editor.Document.GetLineByNumber(12);
            var initial=session.Info(first).Row; var independent=session.Info(other).Row;
            Check("cache tables measured once", initial!=null && independent!=null && initial.Table.Next==null && independent.Table.Next==null,"two complete independent tables");
            double initialWidth=initial.Boundaries[initial.Boundaries.Length-1];
            MarkdownLivePreview.DiagMeasureCells=0;
            for(int i=0;i<5;i++) { editor.TextArea.TextView.Redraw(); Pump(editor); }
            Check("cache redraw reuses row", Object.ReferenceEquals(initial,session.Info(first).Row),"same source, widths, viewport and reveal key");
            Check("cache redraw performs no width measurement", MarkdownLivePreview.DiagMeasureCells==0,"cells="+MarkdownLivePreview.DiagMeasureCells);
            DocumentLine outside=editor.Document.GetLineByNumber(8); editor.Document.Insert(outside.EndOffset,"x"); Pump(editor);
            Check("cache unrelated edit retains both tables", !initial.Table.PendingRemeasure && !independent.Table.PendingRemeasure &&
                Object.ReferenceEquals(initial,session.Info(first).Row) && Object.ReferenceEquals(independent,session.Info(other).Row),"no table invalidation");
            var info=session.Info(first); int insert=first.Offset+info.Cells[1].End;
            editor.Document.Insert(insert," much wider content that grows this column");
            Check("cache edit invalidates only touched table", initial.Table.PendingRemeasure && !independent.Table.PendingRemeasure,"first pending, second clean");
            Pump(editor); var grown=session.Info(first).Row;
            // Typing keeps the published grid (the row re-wraps inside its column); the
            // debounced idle re-sample publishes the wider column once the user pauses.
            Check("cache edit defers column growth", grown!=null && Math.Abs(grown.Boundaries[grown.Boundaries.Length-1]-initialWidth)<.01 && !Object.ReferenceEquals(initial,grown),"initial="+N(initialWidth)+" grown="+(grown==null?"missing":N(grown.Boundaries[grown.Boundaries.Length-1])));
            Check("cache other table survives table edit", Object.ReferenceEquals(independent,session.Info(other).Row),"row object retained");
            typeof(MarkdownLivePreview.Session).GetMethod("TableIdle",Members).Invoke(session,new object[] {null,EventArgs.Empty}); Pump(editor);
            var widened=session.Info(first).Row;
            Check("cache idle remeasure widens shared columns", widened!=null && widened.Boundaries[widened.Boundaries.Length-1]>initialWidth && !Object.ReferenceEquals(initial,widened),"initial="+N(initialWidth)+" widened="+(widened==null?"missing":N(widened.Boundaries[widened.Boundaries.Length-1])));
            editor.Document.Remove(insert," much wider content that grows this column".Length);
            typeof(MarkdownLivePreview.Session).GetMethod("TableIdle",Members).Invoke(session,new object[] {null,EventArgs.Empty}); Pump(editor);
            var restored=session.Info(first).Row;
            Check("cache idle remeasure shrinks after deletion", Math.Abs(restored.Boundaries[restored.Boundaries.Length-1]-initialWidth)<.01,"restored="+N(restored.Boundaries[restored.Boundaries.Length-1]));
            bool numeric=true;
            foreach(DocumentLine source in new DocumentLine[] {first,editor.Document.GetLineByNumber(6),other}) {
                var row=session.Info(source).Row;
                using(TextLine line=session.Formatter.FormatLine(row.Texts[0],0,32000,new MarkdownLivePreview.CellParagraph(row.Texts[0].Default,24),null))
                    numeric &= Math.Abs(line.WidthIncludingTrailingWhitespace-row.Slices[0][0].Width)<.05;
            }
            Check("cache decimal advances match native formatter",numeric,"regular decimal runs, tolerance .05px");
            editor.TextArea.Caret.Line=5; Pump(editor); var raw=session.Info(first).Row;
            Check("cache reveal key changes row", raw.Raw && !Object.ReferenceEquals(restored,raw),"formatted to source mapping");
            var visual=editor.TextArea.TextView.GetVisualLine(5); bool mapped=visual!=null;
            for(int p=0;mapped && p<=first.Length;p++) mapped=visual.GetRelativeOffset(visual.GetVisualColumn(p))==p;
            Check("cache raw row maps every source offset",mapped,"including pipes and padding");
            window.Width=900; Pump(editor);
            Check("cache resize updates row key",session.Info(first).Row.ViewWidth==editor.TextArea.TextView.ActualWidth && !Object.ReferenceEquals(raw,session.Info(first).Row),"new viewport width");
            editor.Text=fixture; Pump(editor);
            Check("cache replace drops old table objects",!Object.ReferenceEquals(initial.Table,session.Info(editor.Document.GetLineByNumber(5)).Row.Table),"whole-buffer replacement");
        } finally {session.Dispose();window.Close();}
    }
    static void CheckStructuralEdits() {
        var editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
        var fresh=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
        var session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {ReadOnly=true});
        var reference=MarkdownLivePreview.Attach(fresh,new MarkdownLivePreview.Settings {ReadOnly=true});
        Window window=Host(editor,500), oracle=Host(fresh,500);
        try {
            window.Show(); oracle.Show();
            editor.Text="intro\n\n1. parent\n  7. child\n  7. sibling\n9. parent two\n- bullet\n  - nested\n- sibling\n\nend"; Pump(editor);
            EditAndCompare("ordered parent removal",editor,session,fresh,reference,3,0,3,"");
            EditAndCompare("ordered parent restore",editor,session,fresh,reference,3,0,0,"1. ");
            EditAndCompare("ordered start number",editor,session,fresh,reference,3,0,1,"8");
            EditAndCompare("nested indent change",editor,session,fresh,reference,4,0,2,"    ");
            EditAndCompare("nested indent restore",editor,session,fresh,reference,4,0,4,"  ");
            EditAndCompare("split list item",editor,session,fresh,reference,3,3,0,"\n");
            EditAndCompare("join list item",editor,session,fresh,reference,3,3,1,"");
            editor.Text="intro\n\n- first\n- sibling\n  - child\n- tail\n\n| h | v |\n| --- | --- |\n| 1 | cell |\n\nend"; Pump(editor);
            DocumentLine sibling=editor.Document.GetLineByNumber(4), row=editor.Document.GetLineByNumber(10);
            var retained=session.Info(sibling); var retainedRow=session.Info(row).Row;
            EditAndCompare("unordered root removal",editor,session,fresh,reference,3,0,2,"");
            Check("structural unchanged sibling info retained",Object.ReferenceEquals(retained,session.Info(sibling)),"root identity changes, rendered depth/number do not");
            Check("structural independent table row retained",Object.ReferenceEquals(retainedRow,session.Info(row).Row),"list marker edit does not touch table geometry");
            session.BeginPress();
            EditAndCompare("frozen reveal structural edit",editor,session,fresh,reference,4,0,2,"");
            session.EndPress();
            editor.Text="intro\n\n| h | v |\n| --- | --- |\n| 1 | cell |\n\nend"; Pump(editor);
            EditAndCompare("table delimiter invalidated",editor,session,fresh,reference,4,2,1,"x");
            EditAndCompare("table delimiter restored",editor,session,fresh,reference,4,2,1,"-");
            EditAndCompare("table row insertion",editor,session,fresh,reference,5,0,0,"| side | more |\n");
            EditAndCompare("table row deletion",editor,session,fresh,reference,5,0,16,"");
            editor.Text="intro\n\n```text\n- fenced\n```\n1. one\n2. two\n\nend"; Pump(editor);
            EditAndCompare("closing fence removal",editor,session,fresh,reference,5,0,3,"");
            EditAndCompare("closing fence restore",editor,session,fresh,reference,5,0,0,"```");
            EditAndCompare("opening fence removal",editor,session,fresh,reference,3,0,3,"");
            EditAndCompare("opening fence restore",editor,session,fresh,reference,3,0,0,"```");
        } finally {session.Dispose(); reference.Dispose(); window.Close(); oracle.Close();}
    }
    // A table edited down to fewer columns must publish the smaller grid: the idle
    // re-measure restarts the sample, so the column count (and the per-column widths
    // it indexes) cannot keep the old, wider layout. Real MainWindow live session.
    static void CheckTableShrink(string directory) {
        const string fixture="Table shrink fixture\n\n| H1 | H2 | H3 |\n|---|---|---|\n\nTail text.\n";
        string file=Path.Combine(directory,"table-shrink.md");File.WriteAllText(file,fixture,new UTF8Encoding(false));
        bool oldDefault=ProbeDefaults.Live;
        MainWindow window=null;
        try {
            ProbeDefaults.Live=true;
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;
            window.LoadFile(file);LongLineBench.Idle();
            window.SetMode(true);
            var editor=(TextEditor)Field(window,"source");
            var session=(MarkdownLivePreview.Session)Field(window,"livePreview");
            if(editor==null||session==null) { Check("table shrink live session",false,"source or live preview missing"); return; }
            editor.TextArea.Caret.Line=1;Pump(editor);Settle(editor);
            string original=editor.Document.Text;
            DocumentLine header=editor.Document.GetLineByNumber(3);
            var initial=session.Info(header);
            VisualLine initialVisual=editor.TextArea.TextView.GetVisualLine(3);
            string initialGlyphs=initialVisual==null?"":GlyphText(initialVisual);
            Check("table shrink initial three columns",initial.Table&&initial.Row!=null&&initial.Row.Table.Count==3&&initial.Row.Boundaries.Length==4&&initial.Row.Texts.Length==3,
                initial.Row==null?"row missing":initial.Row.Table.Count+" columns, boundaries="+initial.Row.Boundaries.Length);
            Check("table shrink formatted row hides pipes",initialGlyphs.IndexOf('|')<0,"glyphs="+initialGlyphs);
            editor.Document.Replace(header.Offset,header.Length,"| H1 | H2 |");
            DocumentLine delimiter=editor.Document.GetLineByNumber(4);
            editor.Document.Replace(delimiter.Offset,delimiter.Length,"|---|---|");
            Settle(editor);
            var shrunk=session.Info(editor.Document.GetLineByNumber(3));
            VisualLine shrunkVisual=editor.TextArea.TextView.GetVisualLine(3);
            string shrinkDetail=shrunk.Row==null?"row missing":shrunk.Row.Table.Count+" columns, boundaries="+shrunk.Row.Boundaries.Length+", texts="+shrunk.Row.Texts.Length;
            Check("table shrink two columns after header and delimiter edit",shrunk.Table&&shrunk.Row!=null&&shrunk.Row.Table.Count==2&&shrunk.Row.Texts.Length==2&&shrunk.Row.Boundaries.Length==3,shrinkDetail);
            bool inside=shrunkVisual!=null&&shrunk.Row!=null;
            if(inside) {
                double end=shrunk.Row.Boundaries[shrunk.Row.Boundaries.Length-1];
                foreach(TextLine textLine in shrunkVisual.TextLines) foreach(IndexedGlyphRun run in textLine.GetIndexedGlyphRuns()) {
                    Rect ink=run.GlyphRun.ComputeInkBoundingBox();
                    inside&=run.GlyphRun.BaselineOrigin.X+ink.Right<=end+.5;
                }
            }
            Check("table shrink no ink in a third column",inside,"all glyph runs inside the two-column table");
            delimiter=editor.Document.GetLineByNumber(4);
            editor.Document.Replace(delimiter.Offset,delimiter.Length,"|--x|");
            Settle(editor);
            var plainHeader=session.Info(editor.Document.GetLineByNumber(3));
            var plainDelimiter=session.Info(editor.Document.GetLineByNumber(4));
            VisualLine plainVisual=editor.TextArea.TextView.GetVisualLine(3);
            string plainGlyphs=plainVisual==null?"":GlyphText(plainVisual);
            Check("table shrink malformed delimiter renders paragraphs",!plainHeader.Table&&plainHeader.Row==null&&!plainDelimiter.Table&&plainDelimiter.Row==null&&plainGlyphs.IndexOf('|')>=0,
                "table="+plainHeader.Table+", row="+(plainHeader.Row!=null)+", delimiterTable="+plainDelimiter.Table+", glyphs="+plainGlyphs);
            MethodInfo undo=typeof(MainWindow).GetMethod("Undo",Members);
            if(undo==null)throw new MissingMemberException("probe: MainWindow has no method 'Undo'");
            undo.Invoke(window,null);undo.Invoke(window,null);undo.Invoke(window,null);
            Settle(editor);
            var restored=session.Info(editor.Document.GetLineByNumber(3));
            Check("table shrink undo restores three columns",original==editor.Document.Text&&restored.Table&&restored.Row!=null&&restored.Row.Table.Count==3&&restored.Row.Boundaries.Length==4&&restored.Cells.Count==3,
                "text restored="+(original==editor.Document.Text)+", columns="+(restored.Row==null?0:restored.Row.Table.Count));
        } finally {
            if(window!=null) { SetField(window,"saved",GetProperty(window,"text")); window.Close(); }
            ProbeDefaults.Live=oldDefault;
        }
    }
    // Setext headings (a "===" / "---" line under a paragraph) must render like the basic
    // view's heading paragraph: the same 2x / 1.6x font size, the same bold weight and the
    // same text x, with the underline folded until the caret enters it. A "---" after a
    // blank line stays a rule and a table delimiter row keeps its own fold.
    static void CheckSetextHeadings() {
        const string fixture="Title\n===\n\nSubtitle\n---\n\nBody text\n\nRule above\n\n---\n\n| A | B |\n| --- | --- |\n| one | two |\n";
        var rich=new RichTextBox { Padding=new Thickness(0), BorderThickness=new Thickness(0), IsReadOnly=true, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        rich.Document=Markdown.Parse(fixture,16,"Malgun Gothic",Brushes.Black,false,"");
        rich.Document.PagePadding=new Thickness(0);
        var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixture;
        var session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
        Window basicWindow=Host(rich,700), liveWindow=Host(editor,700);
        try {
            basicWindow.Show(); liveWindow.Show(); Pump(editor);
            editor.TextArea.Caret.Line=7; Pump(editor);
            double bodyX=((Paragraph)rich.Document.Blocks.FirstBlock).ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward).X;
            var view=editor.TextArea.TextView;
            string[] texts={"Title","Subtitle","Body text"}; int[] numbers={1,4,7};
            for (int i=0;i<texts.Length;i++) {
                Paragraph paragraph=FindParagraph(rich.Document.Blocks,texts[i]);
                Run run=paragraph==null ? null : FirstRun(paragraph.Inlines);
                VisualLine visual=view.GetVisualLine(numbers[i]);
                if (run==null||visual==null) { Check("setext "+texts[i]+" geometry available",false,"paragraph="+(paragraph!=null)+" visual="+(visual!=null)); continue; }
                TextRunProperties properties=FirstRunProperties(visual.TextLines);
                double basicX=run.ContentStart.GetCharacterRect(LogicalDirection.Forward).X-bodyX;
                double liveX=visual.GetVisualPosition(visual.GetVisualColumn(0),VisualYPosition.LineTop).X-view.HorizontalOffset;
                Check("setext "+texts[i]+" size and weight match basic",
                    properties!=null&&Math.Abs(properties.FontRenderingEmSize-run.FontSize)<.01&&properties.Typeface.Weight==run.FontWeight,
                    "live="+(properties==null?0:properties.FontRenderingEmSize)+" basic="+run.FontSize+" weight="+(properties==null?"-":properties.Typeface.Weight.ToString())+"/"+run.FontWeight);
                // The print path draws no heading level marks; the editor starts a heading's text right of its mark (Session.HeadingShift).
                var lineInfo=session.Info(editor.Document.GetLineByNumber(numbers[i]));
                double markShift=session.Options.HeadingMarks&&lineInfo.Heading>0 ? session.HeadingShift(lineInfo.Heading) : 0;
                Check("setext "+texts[i]+" text x matches basic plus the heading mark",Math.Abs(liveX-(basicX+markShift))<=2,"live="+N(liveX)+" basic="+N(basicX)+" mark="+N(markShift));
            }
            var title=session.Info(editor.Document.GetLineByNumber(1));
            var underline=session.Info(editor.Document.GetLineByNumber(2));
            var rule=session.Info(editor.Document.GetLineByNumber(11));
            var delimiter=session.Info(editor.Document.GetLineByNumber(14));
            bool basicRule=false; foreach(Block block in rich.Document.Blocks) if(block is BlockUIContainer) basicRule=true;
            Check("setext underline folds at rest",title.Setext&&title.Heading==1&&underline.SetextUnderline==1&&!OwnVisualLine(view,2),
                "setext="+title.Setext+" heading="+title.Heading+" underline="+underline.SetextUnderline);
            Check("setext --- after a blank stays a rule",rule.Rule&&rule.SetextUnderline==0&&basicRule&&delimiter.Delimiter,
                "rule="+rule.Rule+" underline="+rule.SetextUnderline+" basicRule="+basicRule+" delimiter="+delimiter.Delimiter);
            editor.TextArea.Caret.Line=2; Pump(editor);
            Check("setext underline reveals on caret",OwnVisualLine(view,2)&&GlyphText(view.GetVisualLine(2)).Contains("==="),
                "glyphs="+(view.GetVisualLine(2)==null?"none":GlyphText(view.GetVisualLine(2))));
            editor.TextArea.Caret.Line=7; Pump(editor);
            Check("setext underline folds again",!OwnVisualLine(view,2),"caret back on the body line");
        } finally { foreach(Window window in new Window[] {basicWindow,liveWindow}) if(window!=null) window.Close(); }
    }

    // Issue #4: drive TextEntering/TextEntered, not Document.Insert. Both views use the same
    // parser, and the two-dash exemption is temporary: the saved/loaded Markdown stays setext.
    static void CheckDashUnderText() {
        foreach (bool basic in new bool[] {true,false}) foreach (string newline in new string[] {"\n","\r\n"}) {
            string key="dash-under-text "+(basic?"basic ":"live ")+(newline=="\n"?"LF ":"CRLF ");
            var editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            var session=MarkdownLivePreview.Attach(editor,DashSettings(basic));
            Window window=Host(editor,700);
            try {
                window.Show();
                // a: the second dash, not the third, is the first possible setext promotion.
                // Text input "\n" is rewritten to the editor's own newline, so the fixture holds it.
                DashFixture(editor,"Title"+newline);
                for (int i=1;i<=2;i++) {
                    editor.TextArea.PerformTextInput("-");
                    Check(key+"a immediate paragraph after dash "+i,DashParagraph(editor,session,1),editor.Text);
                    Pump(editor);
                    Check(key+"a paragraph after dash "+i,DashParagraph(editor,session,1),editor.Text);
                }
                Check(key+"a two dashes kept",editor.Text=="Title"+newline+"--",editor.Text);
                editor.Select(editor.Document.TextLength-1,0);Pump(editor);
                Check(key+"a same-line caret keeps exemption",DashParagraph(editor,session,1),editor.Text);
                editor.Select(0,0);Pump(editor);
                Check(key+"a leaving line restores setext",session.Info(editor.Document.GetLineByNumber(1)).Heading==2,editor.Text);

                // b: no intermediate heading on any input; rewriting the third is one undo step.
                DashFixture(editor,"Title"+newline);
                bool headingSeen=false;
                EventHandler<DocumentChangeEventArgs> observe=delegate {
                    if (!DashParagraph(editor,session,1)) headingSeen=true;
                };
                editor.Document.Changed+=observe;
                try {
                    for (int i=1;i<=3;i++) {
                        editor.TextArea.PerformTextInput("-");Pump(editor);
                        Check(key+"b paragraph after dash "+i,DashParagraph(editor,session,1),editor.Text);
                    }
                } finally { editor.Document.Changed-=observe; }
                var rule=session.Info(editor.Document.GetLineByNumber(2));
                Check(key+"b three dashes become rule",editor.Text=="Title"+newline+"***"&&rule.Rule&&rule.SetextUnderline==0&&!headingSeen,
                    editor.Text+" rule="+rule.Rule+" intermediateHeading="+headingSeen);
                headingSeen=false;editor.Document.Changed+=observe;
                try {
                    // MDNote's undo is DocumentHistory.ApplySource (MainWindow), not AvalonEdit's
                    // stack: replay what it writes back, with its Applying flag. Real undo: VM.
                    DashHistory(editor,2,"--");
                    Check(key+"b undo immediately rearms paragraph",editor.Text=="Title"+newline+"--"&&DashParagraph(editor,session,1),editor.Text);
                    Pump(editor);
                    Check(key+"b one undo returns two dashes without heading",editor.Text=="Title"+newline+"--"&&DashParagraph(editor,session,1),editor.Text);
                    DashHistory(editor,2,"***");Pump(editor);
                    Check(key+"b redo restores rule",editor.Text=="Title"+newline+"***"&&DashParagraph(editor,session,1)&&session.Info(editor.Document.GetLineByNumber(2)).Rule,editor.Text);
                    Check(key+"b undo redo never exposes intermediate heading",!headingSeen,"intermediateHeading="+headingSeen);
                } finally { editor.Document.Changed-=observe; }

                // c: no dispatcher/layout pass between these keys; the fourth/fifth must be stars.
                DashFixture(editor,"Title"+newline);
                DashKeys(editor,"-----");Pump(editor);
                Check(key+"c five fast dashes",editor.Text=="Title"+newline+"*****"&&DashParagraph(editor,session,1)&&session.Info(editor.Document.GetLineByNumber(2)).Rule,editor.Text);
                editor.Select(editor.Document.GetLineByNumber(2).Offset+2,0);
                DashKeys(editor,"-");Pump(editor);
                Check(key+"c dash inside produced rule is a star",editor.Text=="Title"+newline+"******",editor.Text);
                DashFixture(editor,"Title"+newline);
                DashKeys(editor,"--- W");Pump(editor);
                Check(key+"phrase restores dashes with text",editor.Text=="Title"+newline+"--- W"&&DashParagraph(editor,session,1),editor.Text);
                DashFixture(editor,"Title"+newline);
                DashKeys(editor,"-----  W");Pump(editor);
                Check(key+"phrase keeps extended run and spaces",editor.Text=="Title"+newline+"-----  W",editor.Text);
                DashFixture(editor,"Title"+newline);
                DashKeys(editor,"--- ");Pump(editor);
                editor.Select(0,0);editor.Select(editor.Document.TextLength,0);
                DashKeys(editor,"W");Pump(editor);
                Check(key+"phrase caret departure drops tracking",editor.Text=="Title"+newline+"*** W",editor.Text);

                // d: no closer exists when the first two closing dashes are entered.
                DashFixture(editor,"---"+newline+"key: v"+newline);
                DashKeys(editor,"---");Pump(editor);
                Check(key+"d YAML closer stays dashes",editor.Text=="---"+newline+"key: v"+newline+"---"&&session.IsFrontMatter(3)&&session.FrontMatterEnd==3,editor.Text);
                // e/f/g/h: table delimiter, list, loaded file and the original opposite-order fix.
                DashFixture(editor,"a | b"+newline);
                // Only the dash-only prefix of a hand-typed delimiter is ours; once "|" makes the
                // pair a table, the table input rules own the rest of the row.
                DashKeys(editor,"---");Pump(editor);
                Check(key+"e table delimiter stays dashes",editor.Text=="a | b"+newline+"---",editor.Text);
                DashFixture(editor,"- a"+newline);
                DashKeys(editor,"---");Pump(editor);
                Check(key+"f list stays dashes",editor.Text=="- a"+newline+"---",editor.Text);
                DashFixture(editor,"- a"+newline);
                foreach (char c in "  ---") {
                    editor.TextArea.PerformTextInput(c.ToString());Pump(editor);
                    Check(key+"container item marker never becomes heading after "+editor.Document.TextLength,DashParagraph(editor,session,1),editor.Text);
                }
                // SetextText rejects the list marker line itself; indentation must not invent
                // an item heading which this parser would never make in the first place.
                Check(key+"container item marker follows parser",editor.Text=="- a"+newline+"  ---",editor.Text);
                DashFixture(editor,"- a"+newline+"  body"+newline);
                foreach (char c in "  ---") {
                    editor.TextArea.PerformTextInput(c.ToString());Pump(editor);
                    Check(key+"container item paragraph never becomes heading after "+editor.Document.TextLength,DashParagraph(editor,session,2),editor.Text);
                }
                Check(key+"container item paragraph becomes rule",editor.Text=="- a"+newline+"  body"+newline+"  ***"&&session.Info(editor.Document.GetLineByNumber(3)).Rule,editor.Text);
                DashFixture(editor,"Title"+newline+"---");
                var loaded=session.Info(editor.Document.GetLineByNumber(1));
                Check(key+"g loaded dashes remain setext",loaded.Setext&&loaded.Heading==2&&editor.Text=="Title"+newline+"---",editor.Text);
                DashFixture(editor,newline+"---",0);
                DashKeys(editor,"Title");Pump(editor);
                Check(key+"h text above existing rule",editor.Text=="Title"+newline+"***"&&DashParagraph(editor,session,1)&&session.Info(editor.Document.GetLineByNumber(2)).Rule,editor.Text);

                // Context exclusions use the parser's own shapes, including an already-used
                // equals underline (the before.Setext argument matters for that last case).
                string[] prefixes={"```"+newline+"body"+newline,"```math"+newline+"x"+newline,
                    "$$"+newline+"x"+newline,newline,"> quote"+newline,"# heading"+newline,
                    "***"+newline,"[id]: target"+newline,"Title"+newline+"==="+newline,
                    "---"+newline+"key: v"+newline};
                string[] suffixes={"","",newline+"$$","","","","","","",newline+"---"};
                string[] names={"fenced code","fenced math","display math","blank above","quote above","ATX above",
                    "rule above","definition above","equals underline above","inside YAML"};
                for (int i=0;i<prefixes.Length;i++) {
                    DashFixture(editor,prefixes[i]+suffixes[i],prefixes[i].Length);
                    DashKeys(editor,"---");Pump(editor);
                    Check(key+"guard "+names[i],editor.Text==prefixes[i]+"---"+suffixes[i],editor.Text);
                }
                DashFixture(editor,"Title"+newline);
                DashKeys(editor,"===");Pump(editor);
                Check(key+"guard equals input untouched",editor.Text=="Title"+newline+"==="&&session.Info(editor.Document.GetLineByNumber(1)).Heading==1,editor.Text);
                DashFixture(editor,"Title"+newline);
                editor.TextArea.PerformTextInput("---");Pump(editor);
                Check(key+"guard multi-character input untouched",editor.Text=="Title"+newline+"---"&&session.Info(editor.Document.GetLineByNumber(1)).Setext,editor.Text);
                // The basic view folds a loaded underline (its selection snaps to the hidden run),
                // so the raw selection case is the live view's.
                if (!basic) {
                    DashFixture(editor,"Title"+newline+"---");
                    editor.Select(editor.Document.TextLength-1,1);DashKeys(editor,"-");Pump(editor);
                    Check(key+"guard selected replacement untouched",editor.Text=="Title"+newline+"---",editor.Text);
                }
                DashFixture(editor,"Title"+newline);
                session.Options.Busy=true;
                try { DashKeys(editor,"---"); } finally { session.Options.Busy=false; }
                Pump(editor);
                Check(key+"guard busy untouched",editor.Text=="Title"+newline+"---",editor.Text);
                DashFixture(editor,"Title"+newline);
                session.Options.ReadOnly=true;
                try { DashKeys(editor,"---"); } finally { session.Options.ReadOnly=false; }
                Pump(editor);
                Check(key+"guard read-only session untouched",editor.Text=="Title"+newline+"---",editor.Text);

                // Spaces/tabs within the run and its original caret offset survive the rewrite.
                DashFixture(editor,"Title"+newline);
                DashKeys(editor,"  ---");Pump(editor);
                Check(key+"spaces and caret preserved",editor.Text=="Title"+newline+"  ***"&&editor.CaretOffset==editor.Document.TextLength,editor.Text+" caret="+editor.CaretOffset);
                DashFixture(editor,"Title"+newline+"    --");
                DashKeys(editor,"-");Pump(editor);
                Check(key+"guard four leading spaces",editor.Text=="Title"+newline+"    ---",editor.Text);

                DashFixture(editor,"Title"+newline);DashKeys(editor,"--");Pump(editor);
                DashKeys(editor," ");Pump(editor);
                Check(key+"other edit drops exemption",session.Info(editor.Document.GetLineByNumber(1)).Setext,editor.Text);
                DashFixture(editor,"Title"+newline);DashKeys(editor,"--");Pump(editor);
                session.Update(DashSettings(!basic));Pump(editor);
                Check(key+"mode switch drops exemption",session.Info(editor.Document.GetLineByNumber(1)).Setext,editor.Text);
                session.Update(DashSettings(basic));
                DashFixture(editor,"Title"+newline);DashKeys(editor,"--");Pump(editor);
                var oldLine=editor.Document.GetLineByNumber(2);
                session.Dispose();
                Check(key+"dispose drops exemption",!session.DashTyping(oldLine),editor.Text);
                session=MarkdownLivePreview.Attach(editor,DashSettings(basic));Pump(editor);
                Check(key+"reattach reads actual setext",session.Info(editor.Document.GetLineByNumber(1)).Setext,editor.Text);
                DashFixture(editor,"Title"+newline);DashKeys(editor,"--");Pump(editor);
                oldLine=editor.Document.GetLineByNumber(2);
                editor.Document=new TextDocument("Other"+newline+"---");
                Check(key+"document swap disposes old exemption",session.IsDisposed&&!session.DashTyping(oldLine),"disposed="+session.IsDisposed);
            } finally { session.Dispose();window.Close(); }
        }
    }
    static MarkdownLivePreview.Settings DashSettings(bool basic) {
        return new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=basic};
    }
    static void DashFixture(SourceEditor editor,string text) { DashFixture(editor,text,text.Length); }
    static void DashFixture(SourceEditor editor,string text,int offset) {
        editor.Text=text;editor.Select(offset,0);editor.Document.UndoStack.ClearAll();Pump(editor);
    }
    static void DashHistory(SourceEditor editor,int number,string text) {
        var line=editor.Document.GetLineByNumber(number);
        DocumentHistory.Applying=true;
        try { editor.Document.Replace(line.Offset,line.Length,text); } finally { DocumentHistory.Applying=false; }
    }
    static void DashKeys(SourceEditor editor,string keys) {
        for (int i=0;i<keys.Length;i++) editor.TextArea.PerformTextInput(keys[i].ToString());
    }
    static bool DashParagraph(SourceEditor editor,MarkdownLivePreview.Session session,int number) {
        var info=session.Info(editor.Document.GetLineByNumber(number));
        return info.Heading==0&&!info.Setext;
    }

    // <font color=...> and <span style=...> share the basic view's rule (MDNote.cs:83,89-95):
    // hidden tags, colour and CSS font size applied, surrounding text at the same x.
    static void CheckFontTags() {
        const string fixture="plain <font color=\"#C03030\">red words</font> tail\n\n<font face=\"Arial\">face text</font> and <font color=\"blue\" style=\"font-size:22px\">blue big</font> end\n\nlast\n";
        var rich=new RichTextBox { Padding=new Thickness(0), BorderThickness=new Thickness(0), IsReadOnly=true, HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        rich.Document=Markdown.Parse(fixture,16,"Malgun Gothic",Brushes.Black,false,"");
        rich.Document.PagePadding=new Thickness(0);
        var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixture;
        var session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
        Window basicWindow=Host(rich,700), liveWindow=Host(editor,700);
        try {
            basicWindow.Show(); liveWindow.Show(); editor.TextArea.Caret.Line=5; Pump(editor);
            double bodyX=((Paragraph)rich.Document.Blocks.FirstBlock).ContentStart.GetInsertionPosition(LogicalDirection.Forward).GetCharacterRect(LogicalDirection.Forward).X;
            var view=editor.TextArea.TextView;
            Paragraph first=FindParagraph(rich.Document.Blocks,"plain red words tail");
            Paragraph second=FindParagraph(rich.Document.Blocks,"face text and blue big end");
            Run basicRed=first==null ? null : RunWithText(null,first.Inlines,"red words");
            Run basicTail=first==null ? null : RunWithText(null,first.Inlines,"tail");
            Run basicFace=second==null ? null : RunWithText(null,second.Inlines,"face text");
            Run basicBlue=second==null ? null : RunWithText(null,second.Inlines,"blue big");
            VisualLine firstVisual=view.GetVisualLine(1), secondVisual=view.GetVisualLine(3);
            if (basicRed==null||basicTail==null||basicFace==null||basicBlue==null||firstVisual==null||secondVisual==null) {
                Check("font tag geometry available",false,"runs="+(basicRed!=null)+","+(basicTail!=null)+","+(basicFace!=null)+","+(basicBlue!=null)+" visuals="+(firstVisual!=null)+","+(secondVisual!=null));
                return;
            }
            string firstText=editor.Document.GetText(editor.Document.GetLineByNumber(1));
            string secondText=editor.Document.GetText(editor.Document.GetLineByNumber(3));
            int redAt=firstText.IndexOf("red words",StringComparison.Ordinal), tailAt=firstText.IndexOf(" tail",StringComparison.Ordinal)+1;
            int faceAt=secondText.IndexOf("face text",StringComparison.Ordinal), blueAt=secondText.IndexOf("blue big",StringComparison.Ordinal);
            Check("font tags hide and keep the basic colour",
                SameBrush(VisualElement(firstVisual,redAt),(SolidColorBrush)basicRed.Foreground)&&
                SameBrush(VisualElement(secondVisual,blueAt),(SolidColorBrush)basicBlue.Foreground),
                "red="+BrushText(BrushOf(VisualElement(firstVisual,redAt)))+" basic="+BrushText(basicRed.Foreground)+", blue="+BrushText(BrushOf(VisualElement(secondVisual,blueAt)))+" basic="+BrushText(basicBlue.Foreground));
            Check("font tags keep the basic size",
                Math.Abs(ElementSize(secondVisual,blueAt)-basicBlue.FontSize)<.01&&Math.Abs(ElementSize(secondVisual,faceAt)-basicFace.FontSize)<.01,
                "blue="+N(ElementSize(secondVisual,blueAt))+"/"+N(basicBlue.FontSize)+" face="+N(ElementSize(secondVisual,faceAt))+"/"+N(basicFace.FontSize));
            // The basic run starts at the space before "tail"; compare the 't' on both sides.
            double basicTailX=basicTail.ContentStart.GetPositionAtOffset(1).GetCharacterRect(LogicalDirection.Forward).X-bodyX;
            double liveTailX=firstVisual.GetVisualPosition(firstVisual.GetVisualColumn(tailAt),VisualYPosition.LineTop).X-view.HorizontalOffset;
            Check("font tags keep the basic x after the tag",Math.Abs(liveTailX-basicTailX)<=2,
                "live="+N(liveTailX)+" basic="+N(basicTailX));
            Check("font tags leave no tag glyphs",!GlyphText(firstVisual).Contains("<font")&&GlyphText(firstVisual).Contains("red words"),
                "glyphs="+GlyphText(firstVisual));
        } finally { foreach(Window window in new Window[] {basicWindow,liveWindow}) if(window!=null) window.Close(); }
    }
    static void CheckCellShadeUniform() {
        const string fixture="| h1 | h2 |\n| --- | --- |\n| a`가나다`b | ==한글abc== |\n";
        var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixture;
        var session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
        Window window=Host(editor,700);
        try {
            window.Show(); editor.TextArea.Caret.Line=1; Pump(editor);
            var rowLine=editor.Document.GetLineByNumber(3);
            var row=session.Info(rowLine).Row;
            bool ok=row!=null && row.Texts!=null && row.Texts.Length>=2 && row.Texts[0]!=null && row.Texts[1]!=null;
            int codeRuns=0, highlightRuns=0;
            bool codeNullBg=true, highlightNullBg=true;
            if (ok) {
                var codeCell=row.Texts[0];
                int codePos=codeCell.Text.IndexOf("가나다",StringComparison.Ordinal);
                if (codePos<0) ok=false;
                else {
                    int codeEnd=codePos+3;
                    for (int p=0;p<codeCell.Text.Length;) {
                        TextRun run=codeCell.GetTextRun(p);
                        int len=Math.Max(1,run.Length);
                        if (p<codeEnd && p+len>codePos) codeRuns++;
                        p+=len;
                    }
                    for (int p=codePos;p<codeEnd;p++) if (codeCell.Properties[p]==null || codeCell.Properties[p].BackgroundBrush!=null) codeNullBg=false;
                }
                var highlightCell=row.Texts[1];
                int hiPos=highlightCell.Text.IndexOf("한글abc",StringComparison.Ordinal);
                if (hiPos<0) ok=false;
                else {
                    int hiEnd=hiPos+5;
                    for (int p=0;p<highlightCell.Text.Length;) {
                        TextRun run=highlightCell.GetTextRun(p);
                        int len=Math.Max(1,run.Length);
                        if (p<hiEnd && p+len>hiPos) highlightRuns++;
                        p+=len;
                    }
                    for (int p=hiPos;p<hiEnd;p++) if (highlightCell.Properties[p]==null || highlightCell.Properties[p].BackgroundBrush!=null) highlightNullBg=false;
                }
            }
            Check("cell-shade-uniform", ok && codeRuns==1 && highlightRuns==1 && codeNullBg && highlightNullBg,
                "ok="+ok+" codeRuns="+codeRuns+" highlightRuns="+highlightRuns+" codeNullBg="+codeNullBg+" highlightNullBg="+highlightNullBg);

            bool codePixels=false, highlightPixels=false;
            string codeDetail="skipped", highlightDetail="skipped";
            if (ok) {
                var bitmap=Render(window);
                int bmpW=bitmap.PixelWidth, bmpH=bitmap.PixelHeight, stride=bmpW*4;
                byte[] pixels=new byte[bmpH*stride];
                bitmap.CopyPixels(pixels, stride, 0);
                var view=editor.TextArea.TextView;
                var visual=view.GetVisualLine(3);
                Point origin=view.TranslatePoint(new Point(0,0), editor);
                codePixels=CheckCellSpanPixels(pixels, stride, bmpW, bmpH, view, visual, origin, row.Texts[0], "가나다", session.Colors.InlineCode, true, out codeDetail);
                highlightPixels=CheckCellSpanPixels(pixels, stride, bmpW, bmpH, view, visual, origin, row.Texts[1], "한글abc", session.Colors.Highlight, true, out highlightDetail);
            }
            Check("cell-shade-pixels", codePixels && highlightPixels, "code=["+codeDetail+"] highlight=["+highlightDetail+"]");
        } finally { session.Dispose(); window.Close(); }
    }
    static bool CheckCellSpanPixels(byte[] pixels, int stride, int bmpW, int bmpH, TextView view, VisualLine visual, Point origin,
        MarkdownLivePreview.CellText cellText, string needle, Brush brush, bool rounded, out string detail) {
        detail="missing";
        if (visual==null || cellText==null || !(brush is SolidColorBrush)) { detail="no visual or brush"; return false; }
        int pos=cellText.Text.IndexOf(needle, StringComparison.Ordinal);
        if (pos<0) { detail="missing needle"; return false; }
        int endPos=pos+needle.Length;
        MarkdownLivePreview.TableRowElement rowElement=null;
        foreach (VisualLineElement element in visual.Elements) {
            rowElement=element as MarkdownLivePreview.TableRowElement;
            if (rowElement!=null) break;
        }
        if (rowElement==null) { detail="no row element"; return false; }
        MarkdownLivePreview.TableToken foundToken=null;
        IList<MarkdownLivePreview.TableToken> tokens=rowElement.Tokens;
        for (int t=0;t<tokens.Count;t++) {
            var candidate=tokens[t];
            if (Object.ReferenceEquals(candidate.Text, cellText) && pos>=candidate.TextStart && endPos<=candidate.TextStart+candidate.Length) {
                foundToken=candidate; break;
            }
        }
        if (foundToken==null) { detail="no token"; return false; }
        int startCol=rowElement.VisualColumn+foundToken.Start+(pos-foundToken.TextStart);
        int endCol=startCol+needle.Length;
        TextLine textLine=visual.GetTextLine(startCol);
        double left=visual.GetTextLineVisualXPosition(textLine, startCol)-view.HorizontalOffset;
        double right=visual.GetTextLineVisualXPosition(textLine, endCol)-view.HorizontalOffset;
        double top=visual.GetTextLineVisualYPosition(textLine, VisualYPosition.LineTop)-view.VerticalOffset;
        double bottom=visual.GetTextLineVisualYPosition(textLine, VisualYPosition.LineBottom)-view.VerticalOffset;
        Color c=((SolidColorBrush)brush).Color;
        byte expR=(byte)((c.R*c.A+255*(255-c.A))/255);
        byte expG=(byte)((c.G*c.A+255*(255-c.A))/255);
        byte expB=(byte)((c.B*c.A+255*(255-c.A))/255);
        int x0=Math.Max(0, (int)Math.Floor(origin.X+left)-1);
        int x1=Math.Min(bmpW-1, (int)Math.Ceiling(origin.X+right)+1);
        int y0=Math.Max(0, (int)Math.Floor(origin.Y+top)-1);
        int y1=Math.Min(bmpH-1, (int)Math.Ceiling(origin.Y+bottom)+1);
        int spanCols=x1-x0+1;
        if (spanCols<=0 || y1<y0) { detail="empty bounds"; return false; }
        int[] colTop=new int[spanCols];
        int[] colBottom=new int[spanCols];
        int firstCol=-1, lastCol=-1;
        for (int i=0;i<spanCols;i++) {
            int x=x0+i, cMin=-1, cMax=-1;
            for (int y=y0;y<=y1;y++) {
                int idx=y*stride+x*4;
                if (Math.Abs(pixels[idx+2]-expR)<=4 && Math.Abs(pixels[idx+1]-expG)<=4 && Math.Abs(pixels[idx]-expB)<=4) {
                    if (cMin<0) cMin=y;
                    cMax=y;
                }
            }
            colTop[i]=cMin; colBottom[i]=cMax;
            if (cMin>=0) { if (firstCol<0) firstCol=i; lastCol=i; }
        }
        if (firstCol<0 || lastCol-firstCol+1<Math.Max(4, (int)Math.Floor(right-left)-3)) {
            detail="coverage first="+firstCol+" last="+lastCol+" width="+N(right-left);
            return false;
        }
        int gap=0, maxGap=0;
        for (int i=firstCol;i<=lastCol;i++) {
            if (colTop[i]<0) { gap++; if (gap>maxGap) maxGap=gap; }
            else gap=0;
        }
        int inset=rounded ? 3 : 0;
        int checkStart=firstCol+inset, checkEnd=lastCol-inset;
        if (checkEnd<checkStart) { checkStart=firstCol; checkEnd=lastCol; }
        int minTop=int.MaxValue, maxTop=-1, minBot=int.MaxValue, maxBot=-1;
        for (int i=checkStart;i<=checkEnd;i++) {
            if (colTop[i]<0) continue;
            if (colTop[i]<minTop) minTop=colTop[i];
            if (colTop[i]>maxTop) maxTop=colTop[i];
            if (colBottom[i]<minBot) minBot=colBottom[i];
            if (colBottom[i]>maxBot) maxBot=colBottom[i];
        }
        bool uniform=maxTop>=0 && maxTop-minTop<=2 && maxBot-minBot<=2;
        detail="cols="+(lastCol-firstCol+1)+" gap="+maxGap+" top="+minTop+".."+maxTop+" bot="+minBot+".."+maxBot;
        return maxGap<=1 && uniform;
    }
    static void CheckCellShadeMatchesLine() {
        // No <kbd> here: a key may now wrap on its own line, which moved the measured space to a
        // wrap end (unshaded). Band height is the point; kbd-over-highlight covers the key.
        const string fixture="| 키 |\n| --- |\n| ==Press the key now== |\n\n==Press the key now==\n";
        var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixture;
        var session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
        Window window=Host(editor,136);
        try {
            window.Show(); editor.TextArea.Caret.Line=editor.Document.LineCount; Pump(editor);
            var view=editor.TextArea.TextView;
            var cellVisual=view.GetVisualLine(3);
            for (int step=0;step<24 && (cellVisual==null || cellVisual.TextLines.Count!=2);step++) {
                window.Width+=(cellVisual!=null && cellVisual.TextLines.Count>2) ? 4 : -4;
                Pump(editor);
                cellVisual=view.GetVisualLine(3);
            }
            var ordVisual=view.GetVisualLine(5);
            var cellInfo=session.Info(editor.Document.GetLineByNumber(3));
            var ordInfo=session.Info(editor.Document.GetLineByNumber(5));
            bool ok=cellVisual!=null && cellVisual.TextLines.Count>=2 && ordVisual!=null && cellInfo.Row!=null && cellInfo.Row.Texts!=null && cellInfo.Row.Texts.Length>0 &&
                session.Colors.Highlight is SolidColorBrush;
            string detail="missing";
            if (ok) {
                var bitmap=Render(window);
                int bmpW=bitmap.PixelWidth, bmpH=bitmap.PixelHeight, stride=bmpW*4;
                byte[] pixels=new byte[bmpH*stride];
                bitmap.CopyPixels(pixels, stride, 0);
                Point origin=view.TranslatePoint(new Point(0,0), editor);
                Color hc=((SolidColorBrush)session.Colors.Highlight).Color;
                byte hiR=(byte)((hc.R*hc.A+255*(255-hc.A))/255), hiG=(byte)((hc.G*hc.A+255*(255-hc.A))/255), hiB=(byte)((hc.B*hc.A+255*(255-hc.A))/255);

                int ordSpaceCol=ordVisual.GetVisualColumn(2+5), ordSpaceEnd=ordVisual.GetVisualColumn(2+6);
                TextLine ordTl=ordVisual.GetTextLine(ordSpaceCol);
                double ordX=origin.X+(ordVisual.GetTextLineVisualXPosition(ordTl,ordSpaceCol)+ordVisual.GetTextLineVisualXPosition(ordTl,ordSpaceEnd))*0.5-view.HorizontalOffset;
                double ordLineTop=origin.Y+ordVisual.GetTextLineVisualYPosition(ordTl,VisualYPosition.LineTop)-view.VerticalOffset;
                double ordLineBot=origin.Y+ordVisual.GetTextLineVisualYPosition(ordTl,VisualYPosition.LineBottom)-view.VerticalOffset;
                double ordTextTop=origin.Y+ordVisual.GetTextLineVisualYPosition(ordTl,VisualYPosition.TextTop)-view.VerticalOffset;
                int ordTop, ordBot;
                ColumnHighlightBounds(pixels, stride, bmpW, bmpH, (int)Math.Round(ordX), (int)Math.Floor(ordLineTop)-2, (int)Math.Ceiling(ordLineBot)+2, hiR, hiG, hiB, out ordTop, out ordBot);

                MarkdownLivePreview.TableRowElement rowElement=null;
                foreach (VisualLineElement elem in cellVisual.Elements) { rowElement=elem as MarkdownLivePreview.TableRowElement; if (rowElement!=null) break; }
                MarkdownLivePreview.TableToken token0=null, token1=null;
                var cellText=cellInfo.Row.Texts[0];
                if (rowElement!=null) {
                    IList<MarkdownLivePreview.TableToken> tokens=rowElement.Tokens;
                    for (int t=0;t<tokens.Count;t++) {
                        var candidate=tokens[t];
                        if (!Object.ReferenceEquals(candidate.Text, cellText)) continue;
                        if (token0==null) token0=candidate; else if (token1==null) token1=candidate;
                    }
                }
                int spaceIdx=cellText.Text.IndexOf(' '), nowIdx=cellText.Text.IndexOf("now", StringComparison.Ordinal);
                if (token0!=null && token1!=null && spaceIdx>=token0.TextStart && spaceIdx<token0.TextStart+token0.Length && nowIdx>=token1.TextStart && nowIdx+3<=token1.TextStart+token1.Length) {
                    int cCol0=rowElement.VisualColumn+token0.Start+(spaceIdx-token0.TextStart), cCol1=cCol0+1;
                    TextLine cellTl0=cellVisual.GetTextLine(cCol0);
                    double cellX=origin.X+(cellVisual.GetTextLineVisualXPosition(cellTl0,cCol0)+cellVisual.GetTextLineVisualXPosition(cellTl0,cCol1))*0.5-view.HorizontalOffset;
                    double cellLineTop0=origin.Y+cellVisual.GetTextLineVisualYPosition(cellTl0,VisualYPosition.LineTop)-view.VerticalOffset;
                    double cellLineBot0=origin.Y+cellVisual.GetTextLineVisualYPosition(cellTl0,VisualYPosition.LineBottom)-view.VerticalOffset;
                    double cellTextTop0=origin.Y+cellVisual.GetTextLineVisualYPosition(cellTl0,VisualYPosition.TextTop)-view.VerticalOffset;
                    double cellTextBot0=origin.Y+cellVisual.GetTextLineVisualYPosition(cellTl0,VisualYPosition.TextBottom)-view.VerticalOffset;
                    int cellTop, cellBot;
                    ColumnHighlightBounds(pixels, stride, bmpW, bmpH, (int)Math.Round(cellX), (int)Math.Floor(cellLineTop0)-2, (int)Math.Ceiling(cellLineBot0)+2, hiR, hiG, hiB, out cellTop, out cellBot);

                    int wCol0=rowElement.VisualColumn+token1.Start+(nowIdx-token1.TextStart), wCol1=wCol0+3;
                    TextLine cellTl1=cellVisual.GetTextLine(wCol0);
                    double nowLeft=origin.X+cellVisual.GetTextLineVisualXPosition(cellTl1,wCol0)-view.HorizontalOffset;
                    double nowRight=origin.X+cellVisual.GetTextLineVisualXPosition(cellTl1,wCol1)-view.HorizontalOffset;
                    double cellLineBot1=origin.Y+cellVisual.GetTextLineVisualYPosition(cellTl1,VisualYPosition.LineBottom)-view.VerticalOffset;
                    int nx0=Math.Max(0, (int)Math.Ceiling(nowLeft)+3), nx1=Math.Min(bmpW-1, (int)Math.Floor(nowRight)-3);
                    int wrapBot=-1, gapRows=0, wrapTopHit=-1;
                    if (nx1>=nx0 && cellTop>=0 && cellBot>=cellTop) {
                        int yMax=Math.Min(bmpH-1, (int)Math.Ceiling(cellLineBot1)+2);
                        for (int y=cellTop;y<=yMax;y++) {
                            bool hit=false;
                            for (int x=nx0;x<=nx1;x++) {
                                int idx=y*stride+x*4;
                                if (Math.Abs(pixels[idx+2]-hiR)<=4 && Math.Abs(pixels[idx+1]-hiG)<=4 && Math.Abs(pixels[idx]-hiB)<=4) { hit=true; break; }
                            }
                            if (hit) {
                                if (wrapTopHit<0) wrapTopHit=y;
                                else if (wrapBot>=0 && y>wrapBot+1) gapRows+=(y-wrapBot-1);
                                wrapBot=y;
                            }
                        }
                        if (wrapTopHit<0 || wrapTopHit>cellTop+1) gapRows++;
                    }
                    // The two wrapped lines merge into one outline whose inner corner at the end of
                    // "now" is filleted (BackgroundGeometryBuilder, as on a wrapped ordinary line), so
                    // the first line's own height is not measurable next to it. Measured instead:
                    // the merged height over "now" = two ordinary bands, and the band's top offset
                    // from the text top = the ordinary line's (same band around the same text).
                    int ordH=ordTop>=0 ? ordBot-ordTop+1 : 0;
                    int cellH=cellTop>=0 ? cellBot-cellTop+1 : 0;
                    int mergedH=wrapTopHit>=0 && wrapBot>=wrapTopHit ? wrapBot-wrapTopHit+1 : 0;
                    double ordOffset=ordTop-ordTextTop, cellOffset=cellTop-cellTextTop0;
                    ok=ordH>0 && cellH>0 && mergedH>0 && Math.Abs(mergedH-2*ordH)<=1 && Math.Abs(cellOffset-ordOffset)<=1 &&
                        gapRows==0 && cellTop<=Math.Ceiling(cellTextTop0) && cellBot>=Math.Floor(cellTextBot0);
                    detail="ordH="+ordH+"("+N(ordOffset)+") cellH="+cellH+"("+N(cellOffset)+") mergedH="+mergedH+" gaps="+gapRows;
                } else {
                    ok=false; detail="tokens="+(token0!=null)+","+(token1!=null)+" lines="+cellVisual.TextLines.Count;
                }
            }
            Check("cell-shade-matches-line", ok, detail);
        } finally { session.Dispose(); window.Close(); }
    }
    static void ColumnHighlightBounds(byte[] pixels, int stride, int bmpW, int bmpH, int x, int y0, int y1, byte hiR, byte hiG, byte hiB, out int top, out int bot) {
        top=-1; bot=-1;
        if (x<0 || x>=bmpW) return;
        y0=Math.Max(0, y0); y1=Math.Min(bmpH-1, y1);
        for (int y=y0;y<=y1;y++) {
            int idx=y*stride+x*4;
            if (Math.Abs(pixels[idx+2]-hiR)<=4 && Math.Abs(pixels[idx+1]-hiG)<=4 && Math.Abs(pixels[idx]-hiB)<=4) {
                if (top<0) top=y;
                bot=y;
            }
        }
    }
    static void CheckKbdOverHighlight() {
        const string fixture="==Press <kbd>Ctrl</kbd> now==\n\n| h1 | h2 |\n| --- | --- |\n| ==Press <kbd>Ctrl</kbd> now== | plain |\n\nend\n";
        var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixture;
        var session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
        Window window=Host(editor,700);
        try {
            window.Show(); editor.TextArea.Caret.Line=7; Pump(editor);
            var bitmap=Render(window);
            int bmpW=bitmap.PixelWidth, bmpH=bitmap.PixelHeight, stride=bmpW*4;
            byte[] pixels=new byte[bmpH*stride];
            bitmap.CopyPixels(pixels, stride, 0);
            var view=editor.TextArea.TextView;
            Point origin=view.TranslatePoint(new Point(0,0), editor);

            string lineDetail="skipped", cellDetail="skipped";
            bool lineOk=false, cellOk=false;

            var lineVisual=view.GetVisualLine(1);
            var lineInfo=session.Info(editor.Document.GetLineByNumber(1));
            MarkdownLivePreview.StyledPart lineKey=default(MarkdownLivePreview.StyledPart);
            bool hasLineKey=false;
            foreach (var part in lineInfo.Styles) {
                if ((part.Style & MarkdownLivePreview.Style.Keyboard)!=0 && part.Length>0) { lineKey=part; hasLineKey=true; break; }
            }
            if (lineVisual!=null && hasLineKey) {
                int startCol=lineVisual.GetVisualColumn(lineKey.Start);
                int endCol=lineVisual.GetVisualColumn(lineKey.Start+lineKey.Length);
                double leadPad=0;
                bool padded=false;
                foreach (VisualLineElement candidate in lineVisual.Elements) {
                    var hidden=candidate as MarkdownLivePreview.InvisibleElement;
                    if (hidden==null || hidden.PadEm<=0) continue;
                    if (hidden.RelativeTextOffset+hidden.DocumentLength==lineKey.Start || hidden.RelativeTextOffset==lineKey.Start+lineKey.Length) { padded=true; break; }
                }
                if (padded) {
                    foreach (VisualLineElement elem in lineVisual.Elements) {
                        if (elem.DocumentLength>0 && elem.RelativeTextOffset<=lineKey.Start && elem.RelativeTextOffset+elem.DocumentLength>lineKey.Start) {
                            leadPad=elem.TextRunProperties.FontRenderingEmSize*0.35; break;
                        }
                    }
                }
                TextLine tl=lineVisual.GetTextLine(startCol);
                double x1=lineVisual.GetTextLineVisualXPosition(tl, startCol)-view.HorizontalOffset;
                double x2=lineVisual.GetTextLineVisualXPosition(tl, endCol)-view.HorizontalOffset;
                double tTop=lineVisual.GetTextLineVisualYPosition(tl, VisualYPosition.TextTop)-view.VerticalOffset;
                double tBot=lineVisual.GetTextLineVisualYPosition(tl, VisualYPosition.TextBottom)-view.VerticalOffset;
                lineOk=CheckKbdBoxAndLeftHighlight(pixels, stride, bmpW, bmpH, origin.X+x1, origin.X+x2, origin.X+x1-leadPad-0.5, origin.Y+tTop, origin.Y+tBot,
                    session.Colors.InlineCode, session.Colors.Highlight, out lineDetail);
            }

            var cellVisual=view.GetVisualLine(5);
            var cellInfo=session.Info(editor.Document.GetLineByNumber(5));
            MarkdownLivePreview.StyledPart cellKey=default(MarkdownLivePreview.StyledPart);
            bool hasCellKey=false;
            foreach (var part in cellInfo.Styles) {
                if ((part.Style & MarkdownLivePreview.Style.Keyboard)!=0 && part.Length>0) { cellKey=part; hasCellKey=true; break; }
            }
            MarkdownLivePreview.TableRowElement rowElement=null;
            if (cellVisual!=null) foreach (VisualLineElement elem in cellVisual.Elements) {
                rowElement=elem as MarkdownLivePreview.TableRowElement;
                if (rowElement!=null) break;
            }
            if (cellVisual!=null && hasCellKey && rowElement!=null && cellInfo.Row!=null && cellInfo.Row.Texts!=null && cellInfo.Row.Texts.Length>0 && cellInfo.Row.Texts[0]!=null) {
                var cellText=cellInfo.Row.Texts[0];
                int pos=cellText.Text.IndexOf("Ctrl", StringComparison.Ordinal);
                MarkdownLivePreview.TableToken foundToken=null;
                if (pos>=0) {
                    IList<MarkdownLivePreview.TableToken> tokens=rowElement.Tokens;
                    for (int t=0;t<tokens.Count;t++) {
                        var candidate=tokens[t];
                        if (Object.ReferenceEquals(candidate.Text, cellText) && pos>=candidate.TextStart && pos+4<=candidate.TextStart+candidate.Length) {
                            foundToken=candidate; break;
                        }
                    }
                }
                if (foundToken!=null) {
                    int startCol=rowElement.VisualColumn+foundToken.Start+(pos-foundToken.TextStart);
                    int endCol=startCol+4;
                    double leadPad=rowElement.KeyboardPad(cellKey.Start);
                    TextLine tl=cellVisual.GetTextLine(startCol);
                    double x1=cellVisual.GetTextLineVisualXPosition(tl, startCol)-view.HorizontalOffset;
                    double x2=cellVisual.GetTextLineVisualXPosition(tl, endCol)-view.HorizontalOffset;
                    double tTop=cellVisual.GetTextLineVisualYPosition(tl, VisualYPosition.TextTop)-view.VerticalOffset;
                    double tBot=cellVisual.GetTextLineVisualYPosition(tl, VisualYPosition.TextBottom)-view.VerticalOffset;
                    cellOk=CheckKbdBoxAndLeftHighlight(pixels, stride, bmpW, bmpH, origin.X+x1, origin.X+x2, origin.X+x1-leadPad-0.5, origin.Y+tTop, origin.Y+tBot,
                        session.Colors.InlineCode, session.Colors.Highlight, out cellDetail);
                }
            }
            Check("kbd-over-highlight", lineOk && cellOk, "line=["+lineDetail+"] cell=["+cellDetail+"]");
        } finally { session.Dispose(); window.Close(); }
    }
    static void CheckCellKbdNoWrap() {
        const string fixture="| 제목1 | 제목2 |\n|---|---|\n|  ==Press <kbd>Ctrl</kbd> now==|  |\n\n==Press <kbd>Ctrl</kbd> now==\n";
        bool ok=true;
        var details=new StringBuilder();
        foreach (TextFormattingMode mode in new TextFormattingMode[] { TextFormattingMode.Ideal, TextFormattingMode.Display }) {
            var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
            editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixture;
            var session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true});
            Window window=Host(editor,900);
            TextOptions.SetTextFormattingMode(window, mode);
            try {
                // The caret sits on the blank line 4: on line 5 it would show that line's source
                // and the ordinary width would be the raw tags' (220 px instead of ~120).
                window.Show(); editor.TextArea.Caret.Line=4; Pump(editor);
                var view=editor.TextArea.TextView;
                var cellVisual=view.GetVisualLine(3);
                var ordVisual=view.GetVisualLine(5);
                var cellInfo=session.Info(editor.Document.GetLineByNumber(3));
                var row=cellInfo.Row;
                double ordWidth=0;
                if (ordVisual!=null && ordVisual.TextLines!=null && ordVisual.TextLines.Count>0) {
                    TextLine tl=ordVisual.TextLines[0];
                    int col0=ordVisual.GetVisualColumn(2);
                    int col1=ordVisual.GetVisualColumn(27);
                    ordWidth=ordVisual.GetTextLineVisualXPosition(tl,col1)-ordVisual.GetTextLineVisualXPosition(tl,col0);
                }
                int slices=row!=null && row.Slices!=null && row.Slices.Length>0 && row.Slices[0]!=null ? row.Slices[0].Count : 0;
                int visualLines=cellVisual!=null && cellVisual.TextLines!=null ? cellVisual.TextLines.Count : 0;
                double colTextWidth=row!=null && row.Widths!=null && row.Widths.Length>0 ? row.Widths[0]-row.Padding*2 : 0;
                bool pass=slices==1 && visualLines==1 && ordWidth>0 && colTextWidth>=ordWidth-1;
                ok &= pass;
                if (details.Length>0) details.Append("; ");
                details.Append(mode.ToString()).Append(":slices=").Append(slices).Append(",lines=").Append(visualLines)
                    .Append(",colText=").Append(N(colTextWidth)).Append(",ord=").Append(N(ordWidth));
            } finally { session.Dispose(); window.Close(); }
        }
        Check("cell-kbd-no-wrap", ok, details.ToString());
    }
    static void CheckCellKbdWrapLayout() {
        const string snippet="==Press <kbd>Ctrl</kbd> now==";
        var eight=new StringBuilder();
        for (int i=0;i<8;i++) eight.Append(snippet);
        string eightText=eight.ToString();
        string directFixture="| 제목1 | 제목2 |\n|---|---|\n| "+eightText+" | "+eightText+" |\n\nend\n";
        string seedFixture="| 제목1 | 제목2 |\n|---|---|\n| "+snippet+" | "+snippet+" |\n\nend\n";

        string directDetail="missing", typingMidDetail="missing", typingEndDetail="missing";
        bool directOk=false, typingMidOk=false, typingEndOk=false;

        var editor1=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editor1.TextArea.TextView.LineHeight=double.NaN; editor1.Text=directFixture;
        var session1=MarkdownLivePreview.Attach(editor1,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=true});
        Window win1=Host(editor1,860);
        TextOptions.SetTextFormattingMode(win1, TextFormattingMode.Display);
        try {
            win1.Show();
            DocumentLine line3=editor1.Document.GetLineByNumber(3);
            editor1.TextArea.Caret.Offset=line3.Offset+session1.Info(line3).Cells[0].End;
            Pump(editor1);
            directOk=CheckCellKbdWrapState(win1, editor1, session1, out directDetail);
        } finally { session1.Dispose(); win1.Close(); }

        var editor2=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editor2.TextArea.TextView.LineHeight=double.NaN; editor2.Text=seedFixture;
        var session2=MarkdownLivePreview.Attach(editor2,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=true});
        Window win2=Host(editor2,860);
        TextOptions.SetTextFormattingMode(win2, TextFormattingMode.Display);
        try {
            win2.Show(); Pump(editor2);
            // Simulate f002 -> f004 (paste 7 more copies into cell 2), then f005 -> f007 (paste into cell 1).
            for (int i=0;i<7;i++) {
                DocumentLine l3=editor2.Document.GetLineByNumber(3);
                int at=l3.Offset+session2.Info(l3).Cells[1].End;
                editor2.TextArea.Caret.Offset=at;
                editor2.Document.Insert(at, snippet);
                Pump(editor2);
            }
            for (int i=0;i<7;i++) {
                DocumentLine l3=editor2.Document.GetLineByNumber(3);
                int at=l3.Offset+session2.Info(l3).Cells[0].End;
                editor2.TextArea.Caret.Offset=at;
                editor2.Document.Insert(at, snippet);
                Pump(editor2);
                if (i==2) typingMidOk=CheckCellKbdWrapState(win2, editor2, session2, out typingMidDetail);
            }
            typingEndOk=CheckCellKbdWrapState(win2, editor2, session2, out typingEndDetail);
        } finally { session2.Dispose(); win2.Close(); }

        Check("cell-kbd-wrap-layout", directOk && typingMidOk && typingEndOk,
            "direct=["+directDetail+"] mid=["+typingMidDetail+"] end=["+typingEndDetail+"]");
    }
    static bool IsProbeCjkBreakable(char c) {
        return (c>=0x1100 && c<=0x11FF) || (c>=0x2E80 && c<=0x9FFF) ||
            (c>=0xAC00 && c<=0xD7AF) || (c>=0xF900 && c<=0xFAFF) || (c>=0xFF00 && c<=0xFFEF);
    }
    // Computes the cell's longest unbreakable token width directly via TextFormatter (never reading
    // TableLayout.Minima) so probes do not validate the table layout against itself.
    static double IndependentCellMinimum(MarkdownLivePreview.Session session, MarkdownLivePreview.CellText cellText) {
        if (cellText==null || cellText.Text.Length==0) return 0;
        double minW=0;
        var noWrap=new MarkdownLivePreview.CellParagraph(cellText.Default, 24, TextWrapping.NoWrap);
        int segStart=0, savedEnd=cellText.FormatEnd;
        try {
            while (segStart<=cellText.Text.Length) {
                int breakAt=cellText.NextBreak(segStart);
                int segEnd=breakAt<0 ? cellText.Text.Length : breakAt;
                if (segEnd>segStart) {
                    cellText.FormatEnd=segEnd;
                    using (TextLine full=session.Formatter.FormatLine(cellText, segStart, 32000, noWrap, null, new TextRunCache())) {
                        int p=segStart;
                        while (p<segEnd) {
                            if (cellText.IsPad(p) && !cellText.IsClosePad(p)) {
                                int ks, ke;
                                if (cellText.FindKeyRange(p+1, out ks, out ke) && ks==p) {
                                    int tokEnd=ke+1;
                                    while (tokEnd<segEnd && !char.IsWhiteSpace(cellText.Text[tokEnd]) && !cellText.IsPad(tokEnd) && !IsProbeCjkBreakable(cellText.Text[tokEnd])) tokEnd++;
                                    double kw=full.GetDistanceFromCharacterHit(new CharacterHit(tokEnd, 0))-full.GetDistanceFromCharacterHit(new CharacterHit(p, 0));
                                    if (kw>minW) minW=kw;
                                    p=tokEnd;
                                    continue;
                                }
                            }
                            char ch=cellText.Text[p];
                            if (char.IsWhiteSpace(ch)) { p++; continue; }
                            if (IsProbeCjkBreakable(ch)) {
                                double cw=full.GetDistanceFromCharacterHit(new CharacterHit(p+1, 0))-full.GetDistanceFromCharacterHit(new CharacterHit(p, 0));
                                if (cw>minW) minW=cw;
                                p++;
                                continue;
                            }
                            int start=p;
                            while (p<segEnd && !char.IsWhiteSpace(cellText.Text[p]) && !IsProbeCjkBreakable(cellText.Text[p]) &&
                                   !(cellText.IsPad(p) && !cellText.IsClosePad(p))) p++;
                            double ww=full.GetDistanceFromCharacterHit(new CharacterHit(p, 0))-full.GetDistanceFromCharacterHit(new CharacterHit(start, 0));
                            if (ww>minW) minW=ww;
                        }
                    }
                }
                if (breakAt<0) break;
                segStart=breakAt+1;
            }
        } finally { cellText.FormatEnd=savedEnd; }
        return minW;
    }
    static bool CheckCellKbdWrapState(Window window, SourceEditor editor, MarkdownLivePreview.Session session, out string detail) {
        return CheckTableRowInvariants(window, editor, session, 3, 2, true, null, out detail);
    }
    static bool CheckTableRowInvariants(Window window, SourceEditor editor, MarkdownLivePreview.Session session,
        int lineNumber, int expectedCols, bool checkMinWidths, int[] plainPixelColumns, out string detail) {
        detail="missing";
        var view=editor.TextArea.TextView;
        var visual=view.GetVisualLine(lineNumber);
        var info=session.Info(editor.Document.GetLineByNumber(lineNumber));
        var row=info.Row;
        if (visual==null || row==null || row.Slices==null || row.Slices.Length<expectedCols || row.Texts==null || row.Texts.Length<expectedCols) return false;
        if (!MarkdownLivePreview.TableRowElement.RowAligned(visual, row)) { detail="unaligned visual="+visual.TextLines.Count+" phys="+row.PhysicalRows; return false; }

        MarkdownLivePreview.TableRowElement rowElement=null;
        foreach (VisualLineElement elem in visual.Elements) {
            rowElement=elem as MarkdownLivePreview.TableRowElement;
            if (rowElement!=null) break;
        }
        if (rowElement==null) { detail="no row element"; return false; }

        // 1. No <kbd> key split across slices/TextLines when the key itself fits on one line,
        // and no continuation slice holds only pads or whitespace.
        int keySplits=0, blankSlices=0;
        for (int c=0;c<expectedCols;c++) {
            var cellText=row.Texts[c];
            var slices=row.Slices[c];
            if (cellText==null || slices==null) continue;
            double colTextMax=Math.Max(1, row.Widths[c]-row.Padding*2-1);
            int savedEnd=cellText.FormatEnd;
            try {
                for (int s=0;s<slices.Count;s++) {
                    var slice=slices[s];
                    if (slice.Length>0 && (s>0 || slices.Count>1)) {
                        bool hasVisible=false;
                        for (int k=slice.Start;k<slice.Start+slice.Length && k<cellText.Text.Length;k++) {
                            if (!cellText.IsPad(k) && !char.IsWhiteSpace(cellText.Text[k])) { hasVisible=true; break; }
                        }
                        if (!hasVisible) blankSlices++;
                    }
                    if (s+1<slices.Count) {
                        int ks, ke;
                        if (cellText.FindKeyRange(slice.Start+slice.Length, out ks, out ke)) {
                            int segStart=ks;
                            while (segStart>0 && !cellText.IsBreak(segStart-1)) segStart--;
                            int segBreak=cellText.NextBreak(ks);
                            int segEnd=segBreak<0 ? cellText.Text.Length : segBreak;
                            cellText.FormatEnd=segEnd;
                            double keyW=0;
                            using (TextLine full=session.Formatter.FormatLine(cellText, segStart, 32000,
                                new MarkdownLivePreview.CellParagraph(cellText.Default, 24, TextWrapping.NoWrap), null, new TextRunCache())) {
                                keyW=full.GetDistanceFromCharacterHit(new CharacterHit(ke+1, 0))-full.GetDistanceFromCharacterHit(new CharacterHit(ks, 0));
                            }
                            if (keyW<=colTextMax) keySplits++;
                        }
                    }
                }
            } finally { cellText.FormatEnd=savedEnd; }
        }

        // 2. Column text widths >= min(independently measured longest unbreakable width, what the view can pay).
        bool minOk=true;
        double worstCol=0, worstMin=0;
        double[] indMins=new double[expectedCols];
        for (int c=0;c<expectedCols;c++) indMins[c]=IndependentCellMinimum(session, row.Texts[c]);
        double viewAvail=Math.Max(40, row.BaseWidth-4);
        for (int c=0;c<expectedCols;c++) {
            double otherMin=0;
            for (int o=0;o<expectedCols;o++) if (o!=c) otherMin+=Math.Max(row.Padding*2+1, indMins[o]+row.Padding*2);
            double viewCap=Math.Max(1, viewAvail-otherMin-row.Padding*2-1);
            double expectedMin=Math.Min(indMins[c], viewCap);
            double colText=row.Widths[c]-row.Padding*2;
            if (checkMinWidths && colText<expectedMin-1) { minOk=false; worstCol=colText; worstMin=expectedMin; break; }
            if (c==1 || (checkMinWidths && expectedMin>worstMin)) { worstCol=colText; worstMin=expectedMin; }
        }

        // 3. Every glyph run and shade char stays inside its own column boundaries, and no
        // key pad/token box starts left of its own cell boundary.
        int overflowRuns=0, badKeyBounds=0;
        IList<MarkdownLivePreview.TableToken> tokens=rowElement.Tokens;
        for (int t=0;t<tokens.Count;t++) {
            var token=tokens[t];
            if (token.Text==null || token.Length<=0) continue;
            int cellIdx=-1;
            for (int c=0;c<expectedCols;c++) { if (Object.ReferenceEquals(token.Text, row.Texts[c])) { cellIdx=c; break; } }
            if (cellIdx<0) continue;
            int c0=rowElement.VisualColumn+token.Start, c1=c0+token.Length;
            TextLine tl=visual.GetTextLine(c0);
            if (tl==null) continue;
            double x0=visual.GetTextLineVisualXPosition(tl, c0);
            double x1=visual.GetTextLineVisualXPosition(tl, c1);
            if (x0<row.Boundaries[cellIdx]-0.5 || x1>row.Boundaries[cellIdx+1]+0.5) overflowRuns++;
            IList<TextBounds> bounds=tl.GetTextBounds(c0, token.Length);
            if (bounds!=null) {
                for (int b=0;b<bounds.Count;b++) {
                    TextBounds tb=bounds[b];
                    if (tb.TextRunBounds!=null && tb.TextRunBounds.Count>0) {
                        for (int r=0;r<tb.TextRunBounds.Count;r++) {
                            Rect rect=tb.TextRunBounds[r].Rectangle;
                            if (rect.Width<=0) continue;
                            if (rect.Left<row.Boundaries[cellIdx]-0.5 || rect.Right>row.Boundaries[cellIdx+1]+0.5) overflowRuns++;
                        }
                    } else if (tb.Rectangle.Width>0) {
                        Rect rect=tb.Rectangle;
                        if (rect.Left<row.Boundaries[cellIdx]-0.5 || rect.Right>row.Boundaries[cellIdx+1]+0.5) overflowRuns++;
                    }
                }
            }
            for (int p=token.TextStart;p<token.TextStart+token.Length;p++) {
                if (token.Text.IsPad(p) && !token.Text.IsClosePad(p)) {
                    int col=rowElement.VisualColumn+token.Start+(p-token.TextStart);
                    double padLeft=visual.GetTextLineVisualXPosition(tl, col)-0.5;
                    if (padLeft<row.Boundaries[cellIdx]-1.0) badKeyBounds++;
                }
            }
        }

        // 4. Rendered pixels in every state: no key-box (InlineCode) or highlight pixels below any
        // column's last physical row, in plain columns, or outside table boundaries.
        int bleedPixels=0;
        bool brushesOk=session.Colors.InlineCode is SolidColorBrush && session.Colors.Highlight is SolidColorBrush;
        if (!brushesOk) {
            bleedPixels=-1;
        } else {
            var bitmap=Render(window);
            int bmpW=bitmap.PixelWidth, bmpH=bitmap.PixelHeight, stride=bmpW*4;
            byte[] pixels=new byte[bmpH*stride];
            bitmap.CopyPixels(pixels, stride, 0);
            Point origin=view.TranslatePoint(new Point(0,0), editor);
            Color bc=((SolidColorBrush)session.Colors.InlineCode).Color;
            byte boxR=(byte)((bc.R*bc.A+255*(255-bc.A))/255), boxG=(byte)((bc.G*bc.A+255*(255-bc.A))/255), boxB=(byte)((bc.B*bc.A+255*(255-bc.A))/255);
            Color hc=((SolidColorBrush)session.Colors.Highlight).Color;
            byte hiR=(byte)((hc.R*hc.A+255*(255-hc.A))/255), hiG=(byte)((hc.G*hc.A+255*(255-hc.A))/255), hiB=(byte)((hc.B*hc.A+255*(255-hc.A))/255);

            double rowTopY=origin.Y+visual.GetTextLineVisualYPosition(visual.TextLines[0], VisualYPosition.LineTop)-view.VerticalOffset;
            double rowBotY=origin.Y+visual.GetTextLineVisualYPosition(visual.TextLines[visual.TextLines.Count-1], VisualYPosition.LineBottom)-view.VerticalOffset;
            int ry0=Math.Max(0, (int)Math.Ceiling(rowTopY)+1), ry1=Math.Min(bmpH-1, (int)Math.Floor(rowBotY)-1);

            for (int c=0;c<expectedCols;c++) {
                int x0=Math.Max(0, (int)Math.Ceiling(origin.X+row.Boundaries[c]-view.HorizontalOffset)+2);
                int x1=Math.Min(bmpW-1, (int)Math.Floor(origin.X+row.Boundaries[c+1]-view.HorizontalOffset)-2);
                if (x1<x0) continue;
                bool checkFull=plainPixelColumns!=null && Array.IndexOf(plainPixelColumns, c)>=0;
                int startLine=checkFull ? 0 : row.Slices[c].Count;
                if (startLine<visual.TextLines.Count) {
                    double yTop=origin.Y+visual.GetTextLineVisualYPosition(visual.TextLines[startLine], VisualYPosition.LineTop)-view.VerticalOffset;
                    int y0=Math.Max(0, (int)Math.Ceiling(yTop)+1);
                    bleedPixels+=CountShadeOrBoxPixels(pixels, stride, x0, x1, y0, ry1, boxR, boxG, boxB, hiR, hiG, hiB);
                }
            }
            int rightX0=Math.Max(0, (int)Math.Ceiling(origin.X+row.Boundaries[expectedCols]-view.HorizontalOffset)+2);
            if (rightX0<bmpW) bleedPixels+=CountShadeOrBoxPixels(pixels, stride, rightX0, bmpW-1, ry0, ry1, boxR, boxG, boxB, hiR, hiG, hiB);
            int leftX0=Math.Max(0, (int)Math.Ceiling(origin.X-view.HorizontalOffset));
            int leftX1=Math.Min(bmpW-1, (int)Math.Floor(origin.X+row.Boundaries[0]-view.HorizontalOffset)-3);
            if (leftX1>=leftX0) bleedPixels+=CountShadeOrBoxPixels(pixels, stride, leftX0, leftX1, ry0, ry1, boxR, boxG, boxB, hiR, hiG, hiB);
        }

        detail="splits="+keySplits+" blank="+blankSlices+" min="+N(worstCol)+"/"+N(worstMin)+
            " ovf="+overflowRuns+" keyBounds="+badKeyBounds+" bleed="+bleedPixels;
        return keySplits==0 && blankSlices==0 && minOk && overflowRuns==0 && badKeyBounds==0 && bleedPixels==0;
    }
    static int CountShadeOrBoxPixels(byte[] pixels, int stride, int x0, int x1, int y0, int y1,
        byte boxR, byte boxG, byte boxB, byte hiR, byte hiG, byte hiB) {
        int count=0;
        for (int y=y0;y<=y1;y++) {
            for (int x=x0;x<=x1;x++) {
                int idx=y*stride+x*4;
                byte r=pixels[idx+2], g=pixels[idx+1], b=pixels[idx];
                if ((Math.Abs(r-boxR)<=4 && Math.Abs(g-boxG)<=4 && Math.Abs(b-boxB)<=4) ||
                    (Math.Abs(r-hiR)<=4 && Math.Abs(g-hiG)<=4 && Math.Abs(b-hiB)<=4)) count++;
            }
        }
        return count;
    }
    static void CheckCellKbdCases() {
        var details=new StringBuilder();
        bool allOk=true;

        // 1. Oversized <kbd> key + 2. Space & Hangul inside <kbd> + 3. <kbd> at cell start +
        // 4. Adjacent keys + 5. Key followed by <br> + 7. 3-column table.
        string[] names=new string[] { "oversized", "space-hangul", "cell-start", "adjacent", "kbd-br", "three-col" };
        string[] fixtures=new string[] {
            "| col1 | col2 |\n|---|---|\n| ==<kbd>SuperLongOversizedKeyboardShortcutKeyNameThatCannotFit</kbd> end== | plain |\n\nend\n",
            "| col1 | col2 |\n|---|---|\n| ==before <kbd>Page Up</kbd> <kbd>한/영</kbd> after== | plain |\n\nend\n",
            "| col1 | col2 |\n|---|---|\n| <kbd>Ctrl</kbd> ==middle <kbd>Shift</kbd> <kbd>Alt</kbd>== | plain |\n\nend\n",
            "| col1 | col2 |\n|---|---|\n| ==<kbd>Ctrl</kbd>+<kbd>C</kbd> <kbd>Alt</kbd><kbd>Tab</kbd>== | plain |\n\nend\n",
            "| col1 | col2 |\n|---|---|\n| ==<kbd>Enter</kbd><br>next line<br><kbd>Esc</kbd>== | plain |\n\nend\n",
            "| col1 | col2 | col3 |\n|---|---|---|\n| ==<kbd>Ctrl</kbd>+<kbd>Shift</kbd> wrap== | ==<kbd>한/영</kbd><br>two== | plain |\n\nend\n"
        };
        double[] widths=new double[] { 420, 320, 320, 260, 320, 420 };
        int[] cols=new int[] { 2, 2, 2, 2, 2, 3 };
        bool[] checkMin=new bool[] { false, true, true, true, true, true };
        int[][] plainCols=new int[][] { new int[] { 1 }, new int[] { 1 }, new int[] { 1 }, new int[] { 1 }, new int[] { 1 }, new int[] { 2 } };
        for (int i=0;i<fixtures.Length;i++) {
            var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
            editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixtures[i];
            var session=MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=true });
            Window win=Host(editor, widths[i]);
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            try {
                win.Show(); editor.TextArea.Caret.Line=editor.Document.LineCount; Pump(editor);
                string d;
                bool ok=CheckTableRowInvariants(win, editor, session, 3, cols[i], checkMin[i], plainCols[i], out d);
                if (i==4 && session.Info(editor.Document.GetLineByNumber(3)).Row!=null &&
                    session.Info(editor.Document.GetLineByNumber(3)).Row.Slices[0].Count<3) { ok=false; d+=" slices<3"; }
                if (!ok) allOk=false;
                details.Append(names[i]).Append(ok ? "=PASS(" : "=FAIL(").Append(d).Append(") ");
            } finally { session.Dispose(); win.Close(); }
        }

        // 6. Key followed by "." at a wrap stays with the key instead of orphan "." row.
        {
            const string dotFixture="| col1 | col2 |\n|---|---|\n| ==alpha beta <kbd>Shift</kbd>. gamma== | plain |\n\nend\n";
            var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
            editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=dotFixture;
            var session=MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=true });
            Window win=Host(editor, 340);
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            try {
                win.Show(); editor.TextArea.Caret.Line=editor.Document.LineCount; Pump(editor);
                var row=session.Info(editor.Document.GetLineByNumber(3)).Row;
                for (int step=0;step<24 && (row==null || row.Slices[0].Count<2);step++) {
                    win.Width-=6; Pump(editor);
                    row=session.Info(editor.Document.GetLineByNumber(3)).Row;
                }
                string d;
                bool ok=CheckTableRowInvariants(win, editor, session, 3, 2, true, new int[] { 1 }, out d);
                bool dotOrphan=false;
                if (row!=null && row.Slices[0].Count>=2) {
                    var ct=row.Texts[0];
                    for (int s=1;s<row.Slices[0].Count;s++) {
                        int st=row.Slices[0][s].Start;
                        if (st<ct.Text.Length && ct.Text[st]=='.') { dotOrphan=true; break; }
                    }
                } else { ok=false; d+=" noWrap"; }
                if (dotOrphan) { ok=false; d+=" dotOrphan"; }
                if (!ok) allOk=false;
                details.Append("dot-wrap").Append(ok ? "=PASS(" : "=FAIL(").Append(d).Append(") ");
            } finally { session.Dispose(); win.Close(); }
        }

        // 8. Item E undo: inserting a long key raises typed Minima and squeezes neighbour; Undo while
        // caret remains inside the table restores Minima immediately (while b52 keeps Widths/Grow);
        // moving the caret out lets TableIdle restore neighbour width within 1 px.
        {
            const string undoFixture="| col1 | col2 |\n|---|---|\n| short | ==Press <kbd>Ctrl</kbd> now== ==Press <kbd>Ctrl</kbd> now== ==Press <kbd>Ctrl</kbd> now== |\n\nend\n";
            var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
            editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=undoFixture;
            // SourceEditor disables AvalonEdit's UndoStack (SizeLimit=0) in favor of MainWindow DocumentHistory;
            // re-enable UndoStack on this standalone editor so editor.Undo() actually reverts the insert.
            editor.Document.UndoStack.SizeLimit=10;
            var session=MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=true });
            Window win=Host(editor, 420);
            TextOptions.SetTextFormattingMode(win, TextFormattingMode.Display);
            try {
                win.Show();
                DocumentLine l3=editor.Document.GetLineByNumber(3);
                int at=l3.Offset+session.Info(l3).Cells[0].End;
                editor.TextArea.Caret.Offset=at;
                Pump(editor);
                var rowBefore=session.Info(editor.Document.GetLineByNumber(3)).Row;
                var table=rowBefore!=null ? rowBefore.Table : null;
                double col2Before=rowBefore!=null ? rowBefore.Widths[1] : 0;
                double min0Before=table!=null ? table.Minima[0] : 0;
                double min1Before=table!=null ? table.Minima[1] : 0;
                editor.Document.Insert(at, " <kbd>VeryLongKeyboardTokenNameToSqueezeNeighbour</kbd>");
                Pump(editor);
                var rowSqueezed=session.Info(editor.Document.GetLineByNumber(3)).Row;
                double col2Squeezed=rowSqueezed!=null ? rowSqueezed.Widths[1] : 0;
                double min0Squeezed=table!=null ? table.Minima[0] : 0;
                editor.Undo();
                Pump(editor);
                bool textRestored=editor.Document.Text==undoFixture;
                bool caretInRow=editor.TextArea.Caret.Line==3;
                double min0AfterUndo=table!=null ? table.Minima[0] : 0;
                double min1AfterUndo=table!=null ? table.Minima[1] : 0;
                string dUndo;
                bool invUndoOk=CheckTableRowInvariants(win, editor, session, 3, 2, true, null, out dUndo);

                editor.TextArea.Caret.Line=editor.Document.LineCount;
                Pump(editor);
                Settle(editor);
                var rowSettled=session.Info(editor.Document.GetLineByNumber(3)).Row;
                double col2Settled=rowSettled!=null ? rowSettled.Widths[1] : 0;
                string dSettled;
                bool invSettledOk=CheckTableRowInvariants(win, editor, session, 3, 2, true, null, out dSettled);

                bool undoOk=textRestored && caretInRow && table!=null && rowBefore!=null &&
                    min0Squeezed>min0Before+50 && col2Squeezed<col2Before-5 &&
                    Math.Abs(min0AfterUndo-min0Before)<=1.0 && Math.Abs(min1AfterUndo-min1Before)<=1.0 &&
                    Math.Abs(col2Settled-col2Before)<=1.0 && invUndoOk && invSettledOk;
                if (!undoOk) allOk=false;
                details.Append("undo-min=").Append(undoOk ? "PASS(" : "FAIL(")
                    .Append("txt=").Append(textRestored).Append(" caret=").Append(caretInRow)
                    .Append(" min0=").Append(N(min0Before)).Append("->").Append(N(min0Squeezed)).Append("->").Append(N(min0AfterUndo))
                    .Append(" min1=").Append(N(min1Before)).Append("->").Append(N(min1AfterUndo))
                    .Append(" w=").Append(N(col2Before)).Append("->").Append(N(col2Squeezed)).Append("->").Append(N(col2Settled))
                    .Append(" invUndo=[").Append(dUndo).Append("] invSettle=[").Append(dSettled).Append("])");
            } finally { session.Dispose(); win.Close(); }
        }

        Check("cell-kbd-cases", allOk, details.ToString());
    }
    // Independent plain-text measurement for the Bug A / Bug B probes: WPF FormattedText only,
    // never MarkdownLivePreview's own width or metric methods, so the table layout is never
    // validated against itself.
    static double IndependentPlainTextWidth(string text, bool bold, TextFormattingMode mode) {
        if (String.IsNullOrEmpty(text)) return 0;
        var ft=new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Malgun Gothic"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            16.0, Brushes.Black, null, mode);
        return ft.WidthIncludingTrailingWhitespace;
    }
    static double IndependentWidestWordWidth(string cellPlainText, bool bold, TextFormattingMode mode) {
        if (String.IsNullOrEmpty(cellPlainText)) return 0;
        double widest=0; int wordStart=-1;
        for (int p=0;p<=cellPlainText.Length;p++) {
            bool gap=p==cellPlainText.Length || char.IsWhiteSpace(cellPlainText[p]);
            if (!gap) { if (wordStart<0) wordStart=p; continue; }
            if (wordStart<0) continue;
            double width=IndependentPlainTextWidth(cellPlainText.Substring(wordStart,p-wordStart), bold, mode);
            if (width>widest) widest=width;
            wordStart=-1;
        }
        return widest;
    }
    // Bug A: pasting a URL about twice the window width into the first column must publish one
    // stable column grid. On the old code the other columns collapsed to one syllable while
    // typing and leaving the caret jumped every column by more than 200 px; here the caret-in-cell
    // widths and the idle-settled widths must agree within 1 px and keep every other column's
    // independently measured widest word.
    static void CheckTableLongTokenPasteStability() {
        const double winWidth=600.0;
        const TextFormattingMode mode=TextFormattingMode.Display;
        string urlPlain="https://example.com/";
        while (IndependentPlainTextWidth(urlPlain,false,mode)<winWidth*2.0) urlPlain+="abcdefghij";
        string urlHyphen="https://docs.example.com/guide-part-one/";
        while (IndependentPlainTextWidth(urlHyphen,false,mode)<winWidth*2.0) urlHyphen+="guide-part-two-";
        string[] urls=new string[] { urlPlain, urlHyphen };
        string[] urlNames=new string[] { "plain", "hyphen" };
        string[] fixtures=new string[] {
            "| 항목 | 설명 |\n|---|---|\n| init | 한국어 단어테스트 문장입니다 |\n\nend\n",
            "| 항목 | 영문 | 설명 |\n|---|---|---|\n| init | Supercalifragilisticexpialidocious summary | 한국어 단어테스트 문장입니다 |\n\nend\n"
        };
        int[] cols=new int[] { 2, 3 };
        string[][] headers=new string[][] {
            new string[] { "항목", "설명" },
            new string[] { "항목", "영문", "설명" }
        };
        string[][] dataCells=new string[][] {
            new string[] { "init", "한국어 단어테스트 문장입니다" },
            new string[] { "init", "Supercalifragilisticexpialidocious summary", "한국어 단어테스트 문장입니다" }
        };
        bool[] fixtureOk=new bool[] { true, true };
        var details=new StringBuilder[] { new StringBuilder(), new StringBuilder() };
        for (int f=0;f<fixtures.Length;f++) for (int u=0;u<urls.Length;u++) for (int hideIndex=0;hideIndex<2;hideIndex++) {
            bool hide=hideIndex==0;
            int count=cols[f];
            string url=urls[u];
            string label=urlNames[u]+"/"+(hide ? "hide" : "live");
            bool ok=true;
            double typingLast=0, settledLast=0, worstJump=0, worstWordGap=double.MaxValue;
            var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
            editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixtures[f];
            var session=MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=hide });
            Window win=Host(editor, winWidth);
            TextOptions.SetTextFormattingMode(win, mode);
            try {
                win.Show(); Pump(editor);
                DocumentLine line3=editor.Document.GetLineByNumber(3);
                int at=line3.Offset+session.Info(line3).Cells[0].End;
                editor.TextArea.Caret.Offset=at;
                editor.Document.Insert(at, " "+url);
                editor.TextArea.Caret.Offset=at+1+url.Length;
                Pump(editor);
                if (editor.TextArea.Caret.Line!=3) { ok=false; label+=" caret="+editor.TextArea.Caret.Line; }
                var rowTyping=session.Info(editor.Document.GetLineByNumber(3)).Row;
                if (rowTyping==null) { ok=false; label+=" noTypingRow"; }
                else {
                    if (rowTyping.Boundaries[count]>editor.TextArea.TextView.ActualWidth+1) {
                        ok=false; label+=" wide="+N(rowTyping.Boundaries[count])+"/"+N(editor.TextArea.TextView.ActualWidth);
                    }
                    double[] typingW=new double[count];
                    for (int c=0;c<count;c++) typingW[c]=rowTyping.Boundaries[c+1]-rowTyping.Boundaries[c];
                    typingLast=typingW[0];
                    editor.TextArea.Caret.Line=editor.Document.LineCount; Pump(editor);
                    typeof(MarkdownLivePreview.Session).GetMethod("TableIdle",Members).Invoke(session,new object[] {null,EventArgs.Empty}); Pump(editor);
                    var rowSettled=session.Info(editor.Document.GetLineByNumber(3)).Row;
                    if (rowSettled==null) { ok=false; label+=" noSettledRow"; }
                    else {
                        double[] settledW=new double[count];
                        for (int c=0;c<count;c++) settledW[c]=rowSettled.Boundaries[c+1]-rowSettled.Boundaries[c];
                        settledLast=settledW[0];
                        for (int c=0;c<count;c++) {
                            double jump=Math.Abs(typingW[c]-settledW[c]);
                            if (jump>worstJump) worstJump=jump;
                            if (jump>1.0) ok=false;
                        }
                        for (int c=1;c<count;c++) {
                            double widestWord=Math.Max(IndependentWidestWordWidth(headers[f][c],true,mode), IndependentWidestWordWidth(dataCells[f][c],false,mode));
                            // A word is owed its whole width unless that takes another column under its floor
                            // (TableWidths keeps every floor first, then pays wider minima from the rest); every
                            // column is owed its floor.
                            // Beside the unbroken URL (u==0, wider than the view) a word wider than its floor shares the
                            // rest above the floors with it at one level (the typed column never narrows), so only the
                            // floor is owed there (documented limit in HANDOFF).
                            double floorW=MarkdownLivePreview.ColumnFloor(editor.TextArea.TextView.ActualWidth,count);
                            widestWord=Math.Min(widestWord,u==0 ? floorW-rowTyping.Padding*2 : (editor.TextArea.TextView.ActualWidth-4)-(count-1)*floorW-rowTyping.Padding*2);
                            if (settledW[c]<floorW-1 || typingW[c]<floorW-1) { ok=false; label+=" floor"+c+"="+N(settledW[c]); }
                            double typingGap=(typingW[c]-rowTyping.Padding*2)-(widestWord-1.0);
                            double settledGap=(settledW[c]-rowSettled.Padding*2)-(widestWord-1.0);
                            if (typingGap<worstWordGap) worstWordGap=typingGap;
                            if (settledGap<worstWordGap) worstWordGap=settledGap;
                            if (typingGap<0 || settledGap<0) ok=false;
                        }
                    }
                }
                double urlWidth=IndependentPlainTextWidth(url,false,mode);
                if (urlWidth<winWidth*1.95) ok=false;
                label+=" url="+N(urlWidth)+" w0="+N(typingLast)+"/"+N(settledLast)+" jump="+N(worstJump)+" wordGap="+N(worstWordGap);
            } finally { session.Dispose(); win.Close(); }
            fixtureOk[f]&=ok;
            details[f].Append(label).Append(ok ? "=PASS " : "=FAIL ");
        }
        Check("table-long-url-2col",fixtureOk[0],details[0].ToString());
        Check("table-long-url-3col",fixtureOk[1],details[1].ToString());
    }
    // Bug B: a 48-row table is measured in 7 slices of 8 rows. Typing in a cell before the sample
    // finishes must never publish a narrower column, and the in-flight sample completing while the
    // caret is still in the typed cell must keep the typing-raised minimum. On the old code
    // CompleteTable dropped Minima[0] and cleared Grow, narrowing column 0 by more than 80 px.
    static void CheckLargeTableTypingBeforeSampleCompletes() {
        var fixture=new StringBuilder();
        fixture.Append("| Col1 | Col2 |\n|---|---|\n");
        for (int i=0;i<48;i++) fixture.Append("| init | This second column holds a long sentence so it wants plenty of width across the table view. |\n");
        fixture.Append("\nend\n");
        string text=fixture.ToString();
        bool monotonicOk=true, completeOk=true;
        var monotonicDetail=new StringBuilder(); var completeDetail=new StringBuilder();

        var editorA=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editorA.TextArea.TextView.LineHeight=double.NaN; editorA.Text=text;
        var sessionA=MarkdownLivePreview.Attach(editorA, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=true });
        Window winA=Host(editorA, 540);
        TextOptions.SetTextFormattingMode(winA, TextFormattingMode.Display);
        try {
            winA.Show(); editorA.UpdateLayout(); editorA.TextArea.TextView.EnsureVisualLines();
            DocumentLine line3=editorA.Document.GetLineByNumber(3);
            var info=sessionA.Info(line3);
            object table=info.Row!=null ? info.Row.Table : null;
            bool unfinished=table!=null && Field(table,"Next")!=null;
            if (!unfinished) { monotonicOk=false; monotonicDetail.Append("next=null "); }
            editorA.TextArea.Caret.Offset=line3.Offset+info.Cells[0].End;
            editorA.UpdateLayout(); editorA.TextArea.TextView.EnsureVisualLines();
            var rowStart=sessionA.Info(editorA.Document.GetLineByNumber(3)).Row;
            double startWidth=rowStart!=null ? rowStart.Boundaries[1]-rowStart.Boundaries[0] : 0;
            double prevWidth=startWidth;
            const string typed=" alpha beta gamma delta Supercalifragilisticexpialidocious";
            bool neverNarrower=true;
            for (int i=0;i<typed.Length;i++) {
                editorA.TextArea.PerformTextInput(typed[i].ToString());
                editorA.UpdateLayout(); editorA.TextArea.TextView.EnsureVisualLines();
                if ((i+1)%10==0) {
                    var frame=new DispatcherFrame();
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue=false; }));
                    Dispatcher.PushFrame(frame);
                }
                var rowNow=sessionA.Info(editorA.Document.GetLineByNumber(3)).Row;
                double widthNow=rowNow!=null ? rowNow.Boundaries[1]-rowNow.Boundaries[0] : 0;
                if (widthNow<prevWidth-0.5) neverNarrower=false;
                prevWidth=widthNow;
            }
            if (!neverNarrower || prevWidth<=startWidth+50) monotonicOk=false;
            monotonicDetail.Append("next=").Append(unfinished).Append(" start=").Append(N(startWidth)).Append(" final=").Append(N(prevWidth));
        } finally { sessionA.Dispose(); winA.Close(); }

        var editorB=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
        editorB.TextArea.TextView.LineHeight=double.NaN; editorB.Text=text;
        var sessionB=MarkdownLivePreview.Attach(editorB, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=true });
        Window winB=Host(editorB, 540);
        TextOptions.SetTextFormattingMode(winB, TextFormattingMode.Display);
        try {
            winB.Show(); editorB.UpdateLayout(); editorB.TextArea.TextView.EnsureVisualLines();
            DocumentLine line3=editorB.Document.GetLineByNumber(3);
            var info=sessionB.Info(line3);
            object table=info.Row!=null ? info.Row.Table : null;
            bool unfinished=table!=null && Field(table,"Next")!=null;
            editorB.TextArea.Caret.Offset=line3.Offset+info.Cells[0].End;
            editorB.Document.Insert(editorB.TextArea.Caret.Offset, " alpha beta gamma Supercalifragilisticexpialidocious");
            editorB.UpdateLayout(); editorB.TextArea.TextView.EnsureVisualLines();
            var rowBefore=sessionB.Info(editorB.Document.GetLineByNumber(3)).Row;
            double widthBefore=rowBefore!=null ? rowBefore.Boundaries[1]-rowBefore.Boundaries[0] : 0;
            double wordWidth=IndependentPlainTextWidth("Supercalifragilisticexpialidocious",false,TextFormattingMode.Display);
            bool ok=unfinished && wordWidth>200 && widthBefore>240;
            if (table==null) { ok=false; completeDetail.Append("table=null "); }
            else {
                SetField(table,"PendingRemeasure",false);
                typeof(MarkdownLivePreview.Session).GetMethod("ScheduleTables",Members).Invoke(sessionB,null);
                Pump(editorB);
                bool sampleFinished=Field(table,"Next")==null;
                var rowSample=sessionB.Info(editorB.Document.GetLineByNumber(3)).Row;
                double widthSample=rowSample!=null ? rowSample.Boundaries[1]-rowSample.Boundaries[0] : 0;
                bool caretInRow=editorB.TextArea.Caret.Line==3;
                editorB.TextArea.PerformTextInput("z"); Pump(editorB);
                var rowKey=sessionB.Info(editorB.Document.GetLineByNumber(3)).Row;
                double widthKey=rowKey!=null ? rowKey.Boundaries[1]-rowKey.Boundaries[0] : 0;
                if (!sampleFinished || !caretInRow || widthSample<widthBefore-0.5 || widthKey<widthSample-0.5) ok=false;
                completeDetail.Append("next=").Append(unfinished).Append(" word=").Append(N(wordWidth)).Append(" w=").Append(N(widthBefore))
                    .Append("->").Append(N(widthSample)).Append("->").Append(N(widthKey)).Append(" caret=").Append(caretInRow).Append(" done=").Append(sampleFinished);
            }
            completeOk=ok;
        } finally { sessionB.Dispose(); winB.Close(); }

        Check("large-table-typing-monotonic",monotonicOk,monotonicDetail.ToString());
        Check("large-table-sample-complete-while-typing",completeOk,completeDetail.ToString());
    }
    // Column widths of row 3 (Boundaries differences), or null.
    static double[] RowThreeWidths(SourceEditor editor,MarkdownLivePreview.Session session,int count) {
        var row=session.Info(editor.Document.GetLineByNumber(3)).Row;
        if (row==null || row.Boundaries==null || row.Boundaries.Length<=count) return null;
        var widths=new double[count];
        for (int c=0;c<count;c++) widths[c]=row.Boundaries[c+1]-row.Boundaries[c];
        return widths;
    }
    // Opus review 11-22, the cases the paste probes miss. (a) One key that takes a typed word's minimum
    // over its column floor must not reshuffle the OTHER columns of a 3-column table: they may only give
    // what the typed column takes (sum of their moves <= its move + 1.5 px). (b) A token typed one key at
    // a time past what the view can pay (not pasted) must look the same before and after the caret leaves.
    static void CheckTableTypedTokenContinuity() {
        const TextFormattingMode mode=TextFormattingMode.Display;
        // (a)
        {
            const string fixture="| 항목 | 설명 | 비고 |\n|---|---|---|\n| a | 한국어 단어테스트 문장입니다 한국어 단어테스트 문장입니다 한국어 단어테스트 문장입니다 한국어 | 짧은 한국어 문장 테스트입니다 짧은 문장 |\n\nend\n";
            bool ok=true;double worstExcess=0,startW0=0,endW0=0;int keys=0;string why="";
            var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
            editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixture;
            var session=MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=true });
            Window win=Host(editor, 600);
            TextOptions.SetTextFormattingMode(win, mode);
            try {
                win.Show(); Pump(editor);
                DocumentLine line3=editor.Document.GetLineByNumber(3);
                editor.TextArea.Caret.Offset=line3.Offset+session.Info(line3).Cells[0].End;
                editor.TextArea.PerformTextInput(" "); Pump(editor);
                double[] prev=RowThreeWidths(editor,session,3);
                if (prev==null) { ok=false; why=" noRow"; }
                else {
                    startW0=prev[0];
                    const string word="Supercalifragilisticexpialidocious";
                    for (int i=0;i<word.Length && ok;i++) {
                        editor.TextArea.PerformTextInput(word[i].ToString());
                        editor.UpdateLayout(); editor.TextArea.TextView.EnsureVisualLines();
                        double[] now=RowThreeWidths(editor,session,3);
                        if (now==null) { ok=false; why=" noRow@"+i; break; }
                        double excess=Math.Abs(now[1]-prev[1])+Math.Abs(now[2]-prev[2])-Math.Abs(now[0]-prev[0]);
                        if (excess>worstExcess) { worstExcess=excess; why=" at"+i+"="+N(prev[0])+"/"+N(prev[1])+"/"+N(prev[2])+"->"+N(now[0])+"/"+N(now[1])+"/"+N(now[2]); }
                        prev=now;keys++;
                    }
                    endW0=prev[0];
                    if (worstExcess>1.5) ok=false;
                    if (IndependentPlainTextWidth(word,false,mode)<200) { ok=false; why+=" shortWord"; }
                    double[] typing=prev;
                    editor.TextArea.Caret.Line=editor.Document.LineCount; Pump(editor);
                    typeof(MarkdownLivePreview.Session).GetMethod("TableIdle",Members).Invoke(session,new object[] {null,EventArgs.Empty}); Pump(editor);
                    double[] settled=RowThreeWidths(editor,session,3);
                    if (settled==null) { ok=false; why+=" noSettled"; }
                    // Half a character: GrowTypingColumn does not publish a growth smaller than that (it
                    // would redraw every row per key for a boundary that stops no wrap), the idle sample does.
                    else for (int c=0;c<3;c++) if (Math.Abs(settled[c]-typing[c])>8.0) { ok=false; why+=" leave"+c+"="+N(typing[c])+"/"+N(settled[c]); }
                }
            } finally { session.Dispose(); win.Close(); }
            Check("table-word-cross-floor-3col",ok,"keys="+keys+" w0="+N(startW0)+"->"+N(endW0)+" worstExcess="+N(worstExcess)+why);
        }
        // (b)
        string[] fixtures=new string[] {
            "| 항목 | 설명 |\n|---|---|\n| init | 한국어 단어테스트 문장입니다 |\n\nend\n",
            "| 항목 | 영문 | 설명 |\n|---|---|---|\n| init | Supercalifragilisticexpialidocious summary | 한국어 단어테스트 문장입니다 |\n\nend\n"
        };
        string[][] headers=new string[][] { new string[] { "항목", "설명" }, new string[] { "항목", "영문", "설명" } };
        string[][] dataCells=new string[][] { new string[] { "init", "한국어 단어테스트 문장입니다" }, new string[] { "init", "Supercalifragilisticexpialidocious summary", "한국어 단어테스트 문장입니다" } };
        var detail=new StringBuilder();bool allOk=true;
        for (int f=0;f<fixtures.Length;f++) {
            int count=f==0 ? 2 : 3;bool ok=true;string why="";double worstJump=0,worstGap=double.MaxValue;int keys=0;
            var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
            editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixtures[f];
            var session=MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=true });
            Window win=Host(editor, 600);
            TextOptions.SetTextFormattingMode(win, mode);
            try {
                win.Show(); Pump(editor);
                DocumentLine line3=editor.Document.GetLineByNumber(3);
                editor.TextArea.Caret.Offset=line3.Offset+session.Info(line3).Cells[0].End;
                editor.TextArea.PerformTextInput(" ");
                editor.UpdateLayout(); editor.TextArea.TextView.EnsureVisualLines();
                var token=new StringBuilder();double[] last=RowThreeWidths(editor,session,count);double worstExcess=0,worstNarrow=0;
                while (IndependentPlainTextWidth(token.ToString(),false,mode)<1200 && keys<400) {
                    string key=((char)('a'+keys%10)).ToString();
                    editor.TextArea.PerformTextInput(key);token.Append(key);keys++;
                    editor.UpdateLayout(); editor.TextArea.TextView.EnsureVisualLines();
                    // Per key (Neon review 11-22): the typed column never narrows, the others only give
                    // what it takes; the all-or-nothing payment snapped both by ~260 px on one key.
                    double[] now=RowThreeWidths(editor,session,count);
                    if (now!=null && last!=null) {
                        double others=0;for (int c=1;c<count;c++) others+=Math.Abs(now[c]-last[c]);
                        double excess=others-Math.Abs(now[0]-last[0]);
                        if (excess>worstExcess) worstExcess=excess;
                        if (last[0]-now[0]>worstNarrow) worstNarrow=last[0]-now[0];
                    }
                    last=now;
                }
                if (worstExcess>1.5 || worstNarrow>0.5) ok=false;
                why+=" excess="+N(worstExcess)+" narrow="+N(worstNarrow);
                Pump(editor);
                if (editor.TextArea.Caret.Line!=3) { ok=false; why+=" caret="+editor.TextArea.Caret.Line; }
                double[] typing=RowThreeWidths(editor,session,count);
                editor.TextArea.Caret.Line=editor.Document.LineCount; Pump(editor);
                typeof(MarkdownLivePreview.Session).GetMethod("TableIdle",Members).Invoke(session,new object[] {null,EventArgs.Empty}); Pump(editor);
                double[] settled=RowThreeWidths(editor,session,count);
                var rowSettled=session.Info(editor.Document.GetLineByNumber(3)).Row;
                if (typing==null || settled==null || rowSettled==null) { ok=false; why+=" noRow"; }
                else {
                    for (int c=0;c<count;c++) { double jump=Math.Abs(typing[c]-settled[c]); if (jump>worstJump) worstJump=jump; if (jump>1.0) ok=false; }
                    for (int c=1;c<count;c++) {
                        double widest=Math.Max(IndependentWidestWordWidth(headers[f][c],true,mode),IndependentWidestWordWidth(dataCells[f][c],false,mode));
                        double floorW=MarkdownLivePreview.ColumnFloor(editor.TextArea.TextView.ActualWidth,count);
                        widest=Math.Min(widest,floorW-rowSettled.Padding*2); /* the typed token is unbroken and wider than the view: only the floor is owed beside it */
                        if (settled[c]<floorW-1) { ok=false; why+=" floor"+c+"="+N(settled[c]); }
                        double gap=(settled[c]-rowSettled.Padding*2)-(widest-1.0);
                        if (gap<worstGap) worstGap=gap;
                        if (gap<0) ok=false;
                    }
                    if (rowSettled.Boundaries[count]>editor.TextArea.TextView.ActualWidth+1) { ok=false; why+=" wide="+N(rowSettled.Boundaries[count]); }
                }
            } finally { session.Dispose(); win.Close(); }
            allOk&=ok;
            detail.Append(count).Append("col keys=").Append(keys).Append(" jump=").Append(N(worstJump)).Append(" wordGap=").Append(N(worstGap)).Append(why).Append(ok ? "=PASS " : "=FAIL ");
        }
        Check("table-long-token-typed",allOk,detail.ToString());
        // (c) Neon review 11-22: a short first column ("1") beside an over-long URL and Korean prose. The
        // short column's floor was reserved at its content width, so the Korean column ended 54 px under its floor.
        {
            const string fixture="| # | 주소 | 설명 |\n|---|---|---|\n| 1 | x | 한국어 단어테스트 문장입니다 |\n\nend\n";
            string url="https://example.com/";
            while (IndependentPlainTextWidth(url,false,mode)<1200) url+="abcdefghij";
            bool ok=true;string why="";
            var editor=new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true, Foreground=Brushes.Black, Background=Brushes.White, BorderThickness=new Thickness(0), Padding=new Thickness(0) };
            editor.TextArea.TextView.LineHeight=double.NaN; editor.Text=fixture;
            var session=MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings { FontSize=16, Family="Malgun Gothic", LinePercent=150, WordWrap=true, HideMarkers=true });
            Window win=Host(editor, 600);
            TextOptions.SetTextFormattingMode(win, mode);
            try {
                win.Show(); Pump(editor);
                DocumentLine line3=editor.Document.GetLineByNumber(3);
                int at=line3.Offset+session.Info(line3).Cells[1].End;
                editor.TextArea.Caret.Offset=at;
                editor.Document.Insert(at,url);
                editor.TextArea.Caret.Offset=at+url.Length;
                Pump(editor);
                double[] typing=RowThreeWidths(editor,session,3);
                editor.TextArea.Caret.Line=editor.Document.LineCount; Pump(editor);
                typeof(MarkdownLivePreview.Session).GetMethod("TableIdle",Members).Invoke(session,new object[] {null,EventArgs.Empty}); Pump(editor);
                double[] settled=RowThreeWidths(editor,session,3);
                double floorW=MarkdownLivePreview.ColumnFloor(editor.TextArea.TextView.ActualWidth,3);
                if (typing==null || settled==null) { ok=false; why=" noRow"; }
                else {
                    for (int c=0;c<3;c++) {
                        if (Math.Abs(typing[c]-settled[c])>1.0) { ok=false; why+=" leave"+c; }
                        if (settled[c]<floorW-1) { ok=false; why+=" floor"+c; }
                    }
                    why+=" w="+N(settled[0])+"/"+N(settled[1])+"/"+N(settled[2])+" floor="+N(floorW);
                }
            } finally { session.Dispose(); win.Close(); }
            Check("table-short-column-beside-url",ok,why);
        }
    }
    static bool CheckKbdBoxAndLeftHighlight(byte[] pixels, int stride, int bmpW, int bmpH,
        double keyLeft, double keyRight, double boxLeft, double bandTop, double bandBottom,
        Brush boxBrush, Brush highlightBrush, out string detail) {
        detail="invalid";
        if (!(boxBrush is SolidColorBrush) || !(highlightBrush is SolidColorBrush)) { detail="no brush"; return false; }
        Color bc=((SolidColorBrush)boxBrush).Color;
        byte boxR=(byte)((bc.R*bc.A+255*(255-bc.A))/255), boxG=(byte)((bc.G*bc.A+255*(255-bc.A))/255), boxB=(byte)((bc.B*bc.A+255*(255-bc.A))/255);
        Color hc=((SolidColorBrush)highlightBrush).Color;
        byte hiR=(byte)((hc.R*hc.A+255*(255-hc.A))/255), hiG=(byte)((hc.G*hc.A+255*(255-hc.A))/255), hiB=(byte)((hc.B*hc.A+255*(255-hc.A))/255);
        int midY=(int)Math.Round((bandTop+bandBottom)/2.0);
        int my0=Math.Max(0, midY-2), my1=Math.Min(bmpH-1, midY+2);
        int kx0=Math.Max(0, (int)Math.Ceiling(keyLeft)), kx1=Math.Min(bmpW-1, (int)Math.Floor(keyRight)-1);
        int boxPixels=0;
        for (int x=kx0;x<=kx1;x++) {
            for (int y=my0;y<=my1;y++) {
                int idx=y*stride+x*4;
                if (Math.Abs(pixels[idx+2]-boxR)<=4 && Math.Abs(pixels[idx+1]-boxG)<=4 && Math.Abs(pixels[idx]-boxB)<=4) boxPixels++;
            }
        }
        int lx0=Math.Max(0, (int)Math.Floor(boxLeft)-4), lx1=Math.Min(bmpW-1, (int)Math.Floor(boxLeft)-2);
        int leftHighlightPixels=0;
        for (int x=lx0;x<=lx1;x++) {
            for (int y=my0;y<=my1;y++) {
                int idx=y*stride+x*4;
                if (Math.Abs(pixels[idx+2]-hiR)<=4 && Math.Abs(pixels[idx+1]-hiG)<=4 && Math.Abs(pixels[idx]-hiB)<=4) leftHighlightPixels++;
            }
        }
        detail="boxFill="+boxPixels+" leftHi="+leftHighlightPixels;
        return boxPixels>0 && leftHighlightPixels>0;
    }
    static VisualLineElement VisualElement(VisualLine visual,int relativeOffset) {
        if (visual==null) return null;
        foreach (VisualLineElement element in visual.Elements)
            if (element.RelativeTextOffset==relativeOffset&&element.TextRunProperties!=null) return element;
        return null;
    }
    static Brush BrushOf(VisualLineElement element) { return element==null ? null : element.TextRunProperties.ForegroundBrush; }
    static double ElementSize(VisualLine visual,int relativeOffset) {
        VisualLineElement element=VisualElement(visual,relativeOffset);
        return element==null ? 0 : element.TextRunProperties.FontRenderingEmSize;
    }
    static string BrushText(Brush brush) { var solid=brush as SolidColorBrush; return solid==null ? "none" : solid.Color.ToString(); }
    static bool SameBrush(VisualLineElement element, SolidColorBrush expected) {
        Brush brush=BrushOf(element);
        return brush is SolidColorBrush && expected!=null && ((SolidColorBrush)brush).Color==expected.Color;
    }
    static Run RunWithText(Run hint, InlineCollection inlines,string needle) {
        if (hint!=null&&hint.Text.IndexOf(needle,StringComparison.Ordinal)>=0) return hint;
        foreach (Inline inline in inlines) {
            var run=inline as Run;
            if (run!=null&&run.Text.IndexOf(needle,StringComparison.Ordinal)>=0) return run;
            var span=inline as Span;
            if (span!=null) { Run found=RunWithText(null,span.Inlines,needle); if (found!=null) return found; }
        }
        return null;
    }
    static TextRunProperties FirstRunProperties(IList<TextLine> lines) {
        if (lines==null) return null;
        foreach (TextLine line in lines) foreach (var span in line.GetTextRunSpans()) {
            var run=span.Value as TextCharacters;
            if (run!=null&&run.Properties!=null) return run.Properties;
        }
        return null;
    }
    static bool OwnVisualLine(TextView view,int lineNumber) {
        VisualLine line=view.GetVisualLine(lineNumber);
        return line!=null&&line.FirstDocumentLine.LineNumber==lineNumber;
    }
    // The width re-sample waits for a 500ms dispatcher timer. Sleeping blocks the UI thread;
    // Pump then lets the timer, the background re-measure and the redraw run.
    static void Settle(TextEditor editor) {
        for(int i=0;i<4;i++) { System.Threading.Thread.Sleep(180); Pump(editor); }
    }
    static string GlyphText(VisualLine visual) {
        var inks=new List<Ink>();
        foreach(TextLine line in visual.TextLines) foreach(IndexedGlyphRun run in line.GetIndexedGlyphRuns()) AddInk(run.GlyphRun,Transform.Identity,inks);
        var text=new StringBuilder();foreach(Ink ink in inks)text.Append(ink.Text);return text.ToString();
    }
    static void EditAndCompare(string name,SourceEditor editor,MarkdownLivePreview.Session session,SourceEditor fresh,MarkdownLivePreview.Session reference,int number,int column,int remove,string insert) {
        DocumentLine changed=editor.Document.GetLineByNumber(number);
        editor.Document.Replace(changed.Offset+column,remove,insert); Pump(editor);
        // Width shrink/new structural table rows intentionally settle on the quiet tick.
        typeof(MarkdownLivePreview.Session).GetMethod("TableIdle",Members).Invoke(session,new object[] {null,EventArgs.Empty}); Pump(editor);
        fresh.Text=editor.Text; Pump(fresh);
        bool equal=editor.Document.LineCount==fresh.Document.LineCount; string detail="incremental vs fresh parse and native layout";
        for(int n=1;equal && n<=editor.Document.LineCount;n++) {
            DocumentLine line=editor.Document.GetLineByNumber(n), other=fresh.Document.GetLineByNumber(n);
            var a=session.Info(line); var b=reference.Info(other);
            equal=InfoKey(a)==InfoKey(b);
            VisualLine va=editor.TextArea.TextView.GetVisualLine(n), vb=fresh.TextArea.TextView.GetVisualLine(n);
            if(equal && va!=null && vb!=null) {
                equal=Math.Abs(va.Height-vb.Height)<.05;
                if(!equal)detail+=" heights="+va.Height+"/"+vb.Height;
                for(int p=0;equal && p<=line.Length;p++) {
                    Point pa=va.GetVisualPosition(va.GetVisualColumn(p),VisualYPosition.LineTop), pb=vb.GetVisualPosition(vb.GetVisualColumn(p),VisualYPosition.LineTop);
                    equal=Math.Abs(pa.X-pb.X)<.05 && Math.Abs(pa.Y-va.VisualTop-pb.Y+vb.VisualTop)<.05;
                    if(!equal)detail+=" offset="+p+" x="+pa.X+"/"+pb.X+" y="+(pa.Y-va.VisualTop)+"/"+(pb.Y-vb.VisualTop);
                }
            } else if(equal) equal=va==null && vb==null;
            if(!equal) detail+="; line="+n+" incremental="+InfoKey(a)+" fresh="+InfoKey(b);
        }
        Check("structural "+name,equal,detail);
    }
    static string InfoKey(MarkdownLivePreview.LineInfo info) {
        var key=new StringBuilder();
        key.Append(info.Text).Append('/').Append(info.Heading).Append('/').Append(info.QuoteLevels).Append('/').Append(info.ListLevel)
            .Append('/').Append(info.Code).Append('/').Append(info.FenceLine).Append('/').Append(info.Table).Append('/').Append(info.Header)
            .Append('/').Append(info.Delimiter).Append('/').Append(info.Rule).Append('/').Append(info.TableStart==null ? 0 : info.TableStart.LineNumber)
            .Append('/').Append(info.Setext).Append('/').Append(info.SetextUnderline);
        if(info.List!=null)key.Append("/list:").Append(info.List.Depth).Append(':').Append(info.List.Number).Append(':').Append(info.List.Ordered).Append(':').Append(info.List.Task);
        foreach(var part in info.Replacements)key.Append("/replace:").Append(part.Start).Append(':').Append(part.Length).Append(':').Append(part.Kind).Append(':').Append(part.Width).Append(':').Append(part.Label);
        foreach(var part in info.Styles)key.Append("/style:").Append(part.Start).Append(':').Append(part.Length).Append(':').Append(part.Style).Append(':').Append(part.Size);
        return key.ToString();
    }
    static long Counter(Type product,string name) { FieldInfo field=product.GetField(name,Members); return field==null ? 0 : (long)field.GetValue(null); }
    static void MeasureTyping(string directory) {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        Assembly assembly=Product; Type host=assembly.GetType("MDNote.MainWindow");
        assembly.GetType("MDNote.Program").GetField("IsSelfTest",Members).SetValue(null,true);
        // ProbeDefaults is its own type, not a MainWindow field; the baseline assembly has its own copy.
        Type defaults=assembly.GetType("MDNote.ProbeDefaults");
        FieldInfo live=defaults==null?null:defaults.GetField("Live",Members);
        if(live!=null)live.SetValue(null,true);
        var window=(Window)Activator.CreateInstance(host); window.ShowInTaskbar=false; window.ShowActivated=false; window.Width=1000; window.Height=800;
        string file=Path.Combine(directory,"typing-generated.md"); File.WriteAllText(file,LivePreviewBench.GenerateMarkdown(12000000,3001),new UTF8Encoding(false));
        TextEditor editor=null; ProbeSession session=null;
        Type product=assembly.GetType("MDNote.MarkdownLivePreview");
        string[] names={"DiagParses","DiagParseTicks","DiagMarkers","DiagMarkerTicks","DiagMeasureCells","DiagRows","DiagBlockScans","DiagBlockTicks","DiagStructural","DiagDraws","DiagLineBuilds"};
        var output=new StringBuilder("sample\tms\tbuilds\tmeasures\tmeasure_ms\tline\ttable\tlist\tsync_ms\tbuild_ms\textent_width\textent_height\tbuild_lines"); foreach(string name in names)output.Append('\t').Append(name); output.Append('\n');
        var samples=new List<double>(); int builds=0; long building=0,buildTicks=0;
        EventHandler<VisualLineConstructionStartEventArgs> onBuild=delegate {builds++;building=Stopwatch.GetTimestamp();};
        EventHandler onBuilt=delegate {if(building!=0){buildTicks+=Stopwatch.GetTimestamp()-building;building=0;}};
        try {
            window.Show(); window.Left=-20000; LongLineBench.Idle();
            host.GetMethod("LoadFileCore",Members).Invoke(window,new object[] {file}); LongLineBench.Idle();
            if(!(bool)Field(window,"editing"))host.GetMethod("SetMode",Members).Invoke(window,new object[] {true});
            host.GetMethod("ToggleSearch",Members).Invoke(window,new object[] {false}); ((TextBox)Field(window,"query")).Text="생성";
            editor=(TextEditor)Field(window,"source"); session=new ProbeSession(Field(window,"livePreview")); Pump(editor);
            editor.CaretOffset=editor.Document.GetLineByOffset(editor.Document.TextLength/2).Offset;
            editor.TextArea.Caret.BringCaretToView(); Pump(editor);
            for(int i=0;i<10;i++)LongLineBench.Time(delegate {editor.TextArea.PerformTextInput("가");},window,DispatcherPriority.ContextIdle);
            editor.TextArea.TextView.VisualLineConstructionStarting+=onBuild;
            editor.TextArea.TextView.VisualLinesChanged+=onBuilt;
            for(int i=0;i<300;i++) {
                var before=new long[names.Length]; for(int n=0;n<names.Length;n++)before[n]=Counter(product,names[n]);
                FieldInfo calls=editor.GetType().GetField("MeasureCalls",Members), milliseconds=editor.GetType().GetField("MeasureMilliseconds",Members);
                int oldCalls=(int)calls.GetValue(editor), oldBuilds=builds; double oldMs=(double)milliseconds.GetValue(editor);
                var oldLines=new List<VisualLine>(editor.TextArea.TextView.VisualLines);
                string text=i%40==39 ? "\n" : "가"; long oldTicks=buildTicks,syncTicks=0;
                double ms=LongLineBench.Time(delegate {long started=Stopwatch.GetTimestamp();editor.TextArea.PerformTextInput(text);syncTicks=Stopwatch.GetTimestamp()-started;},window,DispatcherPriority.ContextIdle); samples.Add(ms);
                var line=editor.Document.GetLineByNumber(editor.TextArea.Caret.Line); var info=session.Info(line);
                string prefix=editor.Document.GetText(line.Offset,Math.Min(8,line.Length));
                output.Append(i).Append('\t').Append(ms.ToString("F4",CultureInfo.InvariantCulture)).Append('\t').Append(builds-oldBuilds).Append('\t')
                    .Append((int)calls.GetValue(editor)-oldCalls).Append('\t').Append(((double)milliseconds.GetValue(editor)-oldMs).ToString("F4",CultureInfo.InvariantCulture))
                    .Append('\t').Append(line.LineNumber).Append('\t').Append(info.Table).Append('\t').Append(prefix.TrimStart().StartsWith("- "))
                    .Append('\t').Append((syncTicks*1000.0/Stopwatch.Frequency).ToString("F4",CultureInfo.InvariantCulture))
                    .Append('\t').Append(((buildTicks-oldTicks)*1000.0/Stopwatch.Frequency).ToString("F4",CultureInfo.InvariantCulture))
                    .Append('\t').Append(editor.ExtentWidth.ToString("F2",CultureInfo.InvariantCulture)).Append('\t').Append(editor.ExtentHeight.ToString("F2",CultureInfo.InvariantCulture));
                output.Append('\t'); foreach(VisualLine built in editor.TextArea.TextView.VisualLines)if(!oldLines.Contains(built)) {
                    DocumentLine source=built.FirstDocumentLine;
                    object parsed=Field(window,"livePreview").GetType().GetMethod("Info",Members).Invoke(Field(window,"livePreview"),new object[] {source});
                    FieldInfo listField=parsed.GetType().GetField("List",Members); object list=listField==null ? null : listField.GetValue(parsed);
                    output.Append(source.LineNumber).Append(':').Append(editor.Document.GetText(source.Offset,Math.Min(source.Length,12))).Append(':').Append(list==null ? "plain" : "depth"+Field(list,"Depth")).Append(',');
                }
                for(int n=0;n<names.Length;n++)output.Append('\t').Append(Counter(product,names[n])-before[n]); output.Append('\n');
            }
            samples.Sort(); output.Append("p50=").Append(LongLineBench.Percentile(samples,.5).ToString("F4",CultureInfo.InvariantCulture)).Append(" p95=").Append(LongLineBench.Percentile(samples,.95).ToString("F4",CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllText(Path.Combine(directory,"typing-counters.tsv"),output.ToString(),new UTF8Encoding(false));
        } catch(Exception ex) {File.WriteAllText(Path.Combine(directory,"typing-error.txt"),ex.ToString()); throw;}
        finally {
            if(editor!=null){editor.TextArea.TextView.VisualLineConstructionStarting-=onBuild;editor.TextArea.TextView.VisualLinesChanged-=onBuilt;}
            host.GetField("saved",Members).SetValue(window,host.GetProperty("text",Members).GetValue(window,null)); window.Close();
            if(live!=null)live.SetValue(null,false); File.Delete(file); Application.Current.Shutdown();
        }
    }
    // One list item's text x and marker ink as the editor draws it. Embedded list markers are
    // drawings rather than native glyph runs, so each MarkerElement's actual CreateTextRun result is
    // inspected and drawn through the public TextRun API.
    sealed class ListMeasure { internal bool Searched, Found; internal double X; internal Ink Marker; }
    static ListMeasure MeasureList(SourceEditor editor, MarkdownLivePreview.Session session, string itemText) {
        var result=new ListMeasure();
        int number=FindLine(editor, itemText); if (number < 0) return result;
        result.Searched=true;
        var source=editor.Document.GetLineByNumber(number); var info=session.Info(source);
        VisualLine visual=editor.TextArea.TextView.GetVisualLine(number);
        int start=info.Text.IndexOf(itemText, StringComparison.Ordinal);
        if (visual == null || start < 0) return result;
        result.Found=true;
        result.X=visual.GetVisualPosition(visual.GetVisualColumn(start), VisualYPosition.LineTop).X;
        var inks=new List<Ink>();
        foreach (TextLine line in visual.TextLines) foreach (IndexedGlyphRun run in line.GetIndexedGlyphRuns()) AddInk(run.GlyphRun, Transform.Identity, inks);
        foreach (VisualLineElement element in visual.Elements) {
            if (element.GetType().Name != "MarkerElement") continue;
            TextEmbeddedObject obj=element.CreateTextRun(element.VisualColumn, null) as TextEmbeddedObject;
            if (obj == null) continue;
            obj.Format(1000); var drawing=new DrawingVisual();
            using (DrawingContext dc=drawing.RenderOpen()) obj.Draw(dc, new Point(visual.GetVisualPosition(element.VisualColumn, VisualYPosition.LineTop).X, 0), false, false);
            CollectInk(drawing, Transform.Identity, inks);
        }
        foreach (Ink ink in inks) if ((NumberMarker(ink.Text)||DiscMarker(ink.Text)||ink.Text=="◦"||ink.Text=="▪") && ink.Right<=result.X+1) result.Marker=ink;
        return result;
    }
    // 11-01 user decision (MarkdownLivePreview.ListMarkerSlot=32): every marker takes one 32 px slot in
    // the editor, basic and live alike (they share one engine). The rich FlowDocument built by
    // Markdown.Parse (print style) keeps an 18 px bullet slot, so its REF numbers are only reported.
    // The list checks compare the live view (markers revealed on the caret line) with the same build's
    // basic editor view (Settings.HideMarkers, markers hidden), measured the same way.
    static void CheckLists(string key, SourceEditor editor, MarkdownLivePreview.Session session, List<ReferenceItem> refs, Dictionary<string,ListMeasure> basic) {
        bool text=true, markers=true, discs=true, numbers=true; double right=-1;
        foreach (ReferenceItem item in refs) {
            ListMeasure live=MeasureList(editor, session, item.Text); if (!live.Searched) continue;
            ListMeasure reference; basic.TryGetValue(item.Text, out reference);
            if (!live.Found || reference == null || !reference.Found) { text=markers=false; continue; }
            double x=live.X; Ink marker=live.Marker; Ink basicMarker=reference.Marker;
            text &= Math.Abs(x-reference.X)<=2;
            if (item.Text.StartsWith("Bullet", StringComparison.Ordinal)) discs &= marker != null && DiscMarker(marker.Text);
            if (!item.Text.StartsWith("Task", StringComparison.Ordinal) && basicMarker == null) markers=false;
            if (basicMarker != null) {
                markers &= marker != null && Math.Abs(marker.X-basicMarker.X)<=2;
                if (item.Text.StartsWith("Number", StringComparison.Ordinal)) {
                    // The editor's own numbers: every live number starts at one left x (11-01 user: every
                    // marker starts at the same left x of its slot, MarkdownLivePreview.MarkerLeadEm).
                    if (marker==null) numbers=false;
                    else { if (right>=0) numbers &= Math.Abs(right-marker.X)<=1; right=marker.X; }
                }
            }
            report.Append("LIVE ").Append(key).Append(' ').Append(item.Text).Append(" x=").Append(N(x)).Append(" marker=").Append(marker==null ? "missing" : marker.Text+"@"+N(marker.X)+"/"+N(marker.Right))
                .Append(" | basic x=").Append(N(reference.X)).Append(" marker=").Append(basicMarker==null ? "missing" : basicMarker.Text+"@"+N(basicMarker.X)+"/"+N(basicMarker.Right)).Append('\n');
        }
        Check(key + " list text x vs basic", text, "live vs basic editor view, tolerance 2px");
        Check(key + " list marker x vs basic", markers, "live vs basic editor view, tolerance 2px");
        Check(key + " same disc at every depth", discs, "three levels");
        Check(key + " shared number left edge", numbers && right>=0, "1..12 and caret on item 1");
    }
    static void CheckTables(string key, SourceEditor editor, MarkdownLivePreview.Session session) {
        bool bounded=true, shared=true, last=true, fits=true; double[] boundaries=null; int checkedRuns=0, rows=0;
        for (int number=24; number<=28; number++) {
            var source=editor.Document.GetLineByNumber(number); var info=session.Info(source);
            if (!info.Table || info.Delimiter) continue;
            VisualLine visual=editor.TextArea.TextView.GetVisualLine(number);
            if (visual==null || info.Row==null) { bounded=false; continue; }
            double[] current=info.Row.Boundaries;
            if (boundaries==null) boundaries=(double[])current.Clone();
            else if (boundaries.Length!=current.Length) shared=false;
            else for (int c=0;c<current.Length;c++) shared &= Math.Abs(current[c]-boundaries[c])<=.5;
            last &= current[current.Length-1]-current[current.Length-2]>=40;
            // This fixture's independent longest-word + padding budget is below 420px.
            fits &= current[current.Length-1]<=editor.TextArea.TextView.ActualWidth+1;
            foreach (TextLine line in visual.TextLines) foreach (IndexedGlyphRun run in line.GetIndexedGlyphRuns()) {
                int offset=visual.GetRelativeOffset(run.TextSourceCharacterIndex);
                int cell=-1; for (int c=0;c<info.Cells.Count;c++) if (offset>=info.Cells[c].Start && offset<info.Cells[c].End) { cell=c; break; }
                if (cell<0) continue;
                Rect ink=run.GlyphRun.ComputeInkBoundingBox(); double edge=run.GlyphRun.BaselineOrigin.X+ink.Right;
                bounded &= edge<=current[cell+1]+.5; checkedRuns++;
                if (edge>current[cell+1]+.5) report.Append("OVERFLOW ").Append(key).Append(" row=").Append(number).Append(" cell=").Append(cell).Append(" edge/boundary=").Append(N(edge)).Append('/').Append(N(current[cell+1])).Append('\n');
            }
            rows++;
        }
        Check(key+" cell glyph bounds", bounded&&checkedRuns>0&&rows==4, "rows="+rows+" glyph runs="+checkedRuns);
        Check(key+" shared table boundaries", shared&&rows==4, boundaries==null ? "missing" : String.Join(",", Array.ConvertAll(boundaries, delegate(double v){return N(v);} )));
        Check(key+" last column >=40px", last&&rows==4, "all data rows and header");
        Check(key+" feasible table fits view", fits&&rows==4, "view="+N(editor.TextArea.TextView.ActualWidth));
    }
    static Run FirstRun(InlineCollection inlines) { foreach(Inline inline in inlines) { var run=inline as Run; if(run!=null&&!Object.Equals(run.Tag,"marker"))return run; var span=inline as Span; if(span!=null) { run=FirstRun(span.Inlines); if(run!=null)return run; } } return null; }
    static bool DiscMarker(string text) { return text=="•"||text=="●"||text==""||text=="·"||text=="\u009f"; }
    static bool NumberMarker(string text) {
        if (String.IsNullOrEmpty(text) || !text.Trim().EndsWith(".", StringComparison.Ordinal)) return false;
        int n; return Int32.TryParse(text.Trim().TrimEnd('.'), out n);
    }
    static Window Host(UIElement content, double width) { return new Window { Left=-20000, Top=0, Width=width, Height=1700, WindowStyle=WindowStyle.None, ShowInTaskbar=false, ShowActivated=false, Content=content, Background=Brushes.White }; }
    static void Pump(TextEditor editor) { for (int i=0;i<3;i++) { var frame=new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate{frame.Continue=false;})); Dispatcher.PushFrame(frame); editor.UpdateLayout(); editor.TextArea.TextView.EnsureVisualLines(); } }
    static void SetLines(BlockCollection blocks) { foreach (Block b in blocks) { var p=b as Paragraph; if(p!=null) { p.LineHeight=24; p.LineStackingStrategy=LineStackingStrategy.MaxHeight; } var list=b as System.Windows.Documents.List; if(list!=null) foreach(ListItem item in list.ListItems) SetLines(item.Blocks); var table=b as Table; if(table!=null) foreach(TableRowGroup g in table.RowGroups) foreach(TableRow r in g.Rows) foreach(TableCell c in r.Cells) SetLines(c.Blocks); } }
    static void Collect(BlockCollection blocks, List<ReferenceItem> items) { foreach(Block b in blocks) { var p=b as Paragraph; if(p!=null) { string text=Body(p); if(text.StartsWith("Bullet")||text.StartsWith("Number")||text.StartsWith("Task")) items.Add(new ReferenceItem {Text=text}); } var list=b as System.Windows.Documents.List; if(list!=null) foreach(ListItem item in list.ListItems) Collect(item.Blocks,items); } }
    static Paragraph FindParagraph(BlockCollection blocks, string text) { foreach(Block b in blocks) { var p=b as Paragraph; if(p!=null&&Body(p)==text)return p; var list=b as System.Windows.Documents.List; if(list!=null) foreach(ListItem item in list.ListItems) { var found=FindParagraph(item.Blocks,text); if(found!=null)return found; } } return null; }
    // WPF puts the native list marker ("•\t", "1.\t") into the paragraph's text range even though
    // it is not an inline; the body checks below compare against the source text without it.
    static string Body(Paragraph paragraph) { string value=System.Text.RegularExpressions.Regex.Replace(new TextRange(paragraph.ContentStart,paragraph.ContentEnd).Text,"^(?:•|\\d+\\.)\\t","").Trim(); foreach(string prefix in new string[] {"Bullet", "Number", "Task"}) { int at=value.IndexOf(prefix,StringComparison.Ordinal); if(at>=0)return value.Substring(at); } return value; }
    static int FindLine(SourceEditor editor, string text) { for(int n=1;n<=editor.Document.LineCount;n++) if(editor.Document.GetText(editor.Document.GetLineByNumber(n)).Contains(text)) return n; return -1; }
    static void CollectInk(Visual visual, Transform parent, List<Ink> inks) {
        GeneralTransform relative=VisualTreeHelper.GetTransform(visual); Vector offset=VisualTreeHelper.GetOffset(visual);
        var transforms=new TransformGroup(); transforms.Children.Add(relative as Transform ?? Transform.Identity); transforms.Children.Add(new TranslateTransform(offset.X,offset.Y)); transforms.Children.Add(parent);
        DrawingGroup drawing=VisualTreeHelper.GetDrawing(visual); if(drawing!=null) DrawInk(drawing,transforms,inks);
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(visual);i++) { var child=VisualTreeHelper.GetChild(visual,i) as Visual; if(child!=null)CollectInk(child,transforms,inks); }
    }
    static void DrawInk(Drawing drawing, Transform parent, List<Ink> inks) { var glyph=drawing as GlyphRunDrawing; if(glyph!=null) { AddInk(glyph.GlyphRun,parent,inks); return; } var group=drawing as DrawingGroup; if(group==null)return; var transform=new TransformGroup(); transform.Children.Add(group.Transform??Transform.Identity); transform.Children.Add(parent); foreach(Drawing child in group.Children)DrawInk(child,transform,inks); }
    static void AddInk(GlyphRun glyph, Transform transform, List<Ink> inks) {
        string text=""; if(glyph.Characters!=null)text=new string(new List<char>(glyph.Characters).ToArray());
        else foreach(ushort id in glyph.GlyphIndices) { char found='?'; foreach(var pair in glyph.GlyphTypeface.CharacterToGlyphMap) if(pair.Value==id&&(pair.Key<128||pair.Key==0x2022||pair.Key==0x25CF||pair.Key==0x25CB||pair.Key==0x25E6||pair.Key==0x25AA)) {found=(char)pair.Key;break;} text+=found; }
        double advance=0; foreach(double w in glyph.AdvanceWidths)advance+=w;
        Point origin=transform.Transform(glyph.BaselineOrigin), right=transform.Transform(new Point(glyph.BaselineOrigin.X+advance,glyph.BaselineOrigin.Y));
        inks.Add(new Ink {Text=text,X=origin.X,Right=right.X,Y=origin.Y,Size=glyph.FontRenderingEmSize,Family=String.Join(",",glyph.GlyphTypeface.FamilyNames.Values)});
    }
    static RenderTargetBitmap Render(Window window) { window.UpdateLayout(); var content=window.Content as FrameworkElement; var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(content); return bitmap; }
    static void Save(Window window,string path) { var bitmap=Render(window); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var stream=File.Create(path))encoder.Save(stream); }
    static void Check(string name,bool ok,string detail) { if(ok)passes++;else failures++; report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n'); }
    static string N(double value) { return value.ToString("0.##",CultureInfo.InvariantCulture); }
}
}
