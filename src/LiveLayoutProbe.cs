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
            CheckFontTags();
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
        Window richWindow = Host(rich, width), liveWindow = Host(editor, width);
        try {
            richWindow.Show(); liveWindow.Show(); Pump(editor);
            var refs = new List<ReferenceItem>(); Collect(rich.Document.Blocks, refs);
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
                CheckLists(label, editor, session, refs);
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
        } finally { session.Dispose(); richWindow.Close(); liveWindow.Close(); }
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
                Check("setext "+texts[i]+" text x matches basic",Math.Abs(liveX-basicX)<=2,"live="+N(liveX)+" basic="+N(basicX));
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
    static void CheckLists(string key, SourceEditor editor, MarkdownLivePreview.Session session, List<ReferenceItem> refs) {
        bool text=true, markers=true, discs=true, numbers=true; double right=-1;
        foreach (ReferenceItem item in refs) {
            int number=FindLine(editor, item.Text); if (number < 0) continue;
            var source=editor.Document.GetLineByNumber(number); var info=session.Info(source);
            VisualLine visual=editor.TextArea.TextView.GetVisualLine(number);
            int start=info.Text.IndexOf(item.Text, StringComparison.Ordinal);
            if (visual == null || start < 0) { text=markers=false; continue; }
            double x=visual.GetVisualPosition(visual.GetVisualColumn(start), VisualYPosition.LineTop).X;
            double inset=0;
            text &= Math.Abs(x-item.X-inset)<=2;
            var inks=new List<Ink>();
            foreach (TextLine line in visual.TextLines) foreach (IndexedGlyphRun run in line.GetIndexedGlyphRuns()) AddInk(run.GlyphRun, Transform.Identity, inks);
            // Embedded list markers are drawings rather than native glyph runs. Inspect
            // their actual CreateTextRun result and draw it through the public TextRun API.
            foreach (VisualLineElement element in visual.Elements) {
                if (element.GetType().Name != "MarkerElement") continue;
                TextEmbeddedObject obj=element.CreateTextRun(element.VisualColumn, null) as TextEmbeddedObject;
                if (obj == null) continue;
                obj.Format(1000); var drawing=new DrawingVisual();
                using (DrawingContext dc=drawing.RenderOpen()) obj.Draw(dc, new Point(visual.GetVisualPosition(element.VisualColumn, VisualYPosition.LineTop).X, 0), false, false);
                CollectInk(drawing, Transform.Identity, inks);
            }
            Ink marker=null;
            foreach (Ink ink in inks) if ((NumberMarker(ink.Text)||DiscMarker(ink.Text)||ink.Text=="◦"||ink.Text=="▪") && ink.Right<=x+1) marker=ink;
            if (item.Text.StartsWith("Bullet", StringComparison.Ordinal)) discs &= marker != null && DiscMarker(marker.Text);
            if (!item.Text.StartsWith("Task", StringComparison.Ordinal) && item.Marker == null) markers=false;
            if (item.Marker != null) {
                markers &= marker != null && Math.Abs(marker.X-item.MarkerX-inset)<=2;
                if (item.Text.StartsWith("Bullet", StringComparison.Ordinal)) discs &= marker != null && DiscMarker(marker.Text);
                if (item.Text.StartsWith("Number", StringComparison.Ordinal)) {
                    if (marker==null) numbers=false;
                    else { if (right>=0) numbers &= Math.Abs(right-marker.Right)<=1; right=marker.Right; }
                }
            }
            report.Append("LIVE ").Append(key).Append(' ').Append(item.Text).Append(" x=").Append(N(x)).Append(" marker=").Append(marker==null ? "missing" : marker.Text+"@"+N(marker.X)+"/"+N(marker.Right)).Append('\n');
        }
        Check(key + " list text x vs basic", text, "tolerance 2px; no inset compensation");
        Check(key + " list marker x vs basic", markers, "tolerance 2px; no inset compensation");
        Check(key + " same disc at every depth", discs, "three levels");
        Check(key + " shared number right edge", numbers && right>=0, "1..12 and caret on item 1");
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
    static void Save(Window window,string path) { window.UpdateLayout(); var content=window.Content as FrameworkElement; var bitmap=new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),(int)Math.Ceiling(content.ActualHeight),96,96,PixelFormats.Pbgra32); bitmap.Render(content); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var stream=File.Create(path))encoder.Save(stream); }
    static void Check(string name,bool ok,string detail) { if(ok)passes++;else failures++; report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n'); }
    static string N(double value) { return value.ToString("0.##",CultureInfo.InvariantCulture); }
}
}
