// Render fix probe: inline icon centring, table cell wrapping, lists inside quotes, tables inside
// lists and the formatter's text mode (offscreen, generated fixtures only; no window input).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.TextFormatting;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using MDNote;

static class RenderFixProbe {
    static StringBuilder report;
    static bool all;
    static int cases;
    internal static int Run(string outDir) {
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string pass=Path.Combine(outDir,"renderfix-PASS.txt");
        string fail=Path.Combine(outDir,"renderfix-FAIL.txt");
        if (File.Exists(pass)) File.Delete(pass);
        if (File.Exists(fail)) File.Delete(fail);
        report=new StringBuilder("Render fix probe: icon centring, cell wrapping, quoted lists, indented tables, text mode.\n");
        all=true;cases=0;
        bool oldSelfTest=Program.IsSelfTest;Application app=null;
        ShutdownMode oldShutdown=ShutdownMode.OnExplicitShutdown;
        try {
            Program.IsSelfTest=true;
            if (Application.Current==null) new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            app=Application.Current;oldShutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            WriteProbePng(Path.Combine(outDir,"rf-icon.png"),17,17);
            try {RunIcon(outDir);} catch (Exception ex) {Check("icon exception",false,ex.ToString());}
            try {RunTableWrap(outDir);} catch (Exception ex) {Check("cell-wrap exception",false,ex.ToString());}
            try {RunQuoteLists(outDir);} catch (Exception ex) {Check("quote-list exception",false,ex.ToString());}
            try {RunIndentedTable(outDir);} catch (Exception ex) {Check("indented-table exception",false,ex.ToString());}
            try {RunLabelAndIcon(outDir);} catch (Exception ex) {Check("label-icon exception",false,ex.ToString());}
            try {RunDisplayMode(outDir);} catch (Exception ex) {Check("display-mode exception",false,ex.ToString());}
            try {RunSourceFace(outDir);} catch (Exception ex) {Check("source-face exception",false,ex.ToString());}
            try {RunReviewTables(outDir);} catch (Exception ex) {Check("review-table exception",false,ex.ToString());}
            try {RunCellEdit(outDir);} catch (Exception ex) {Check("cell-edit exception",false,ex.ToString());}
            try {RunNestedBullet(outDir);} catch (Exception ex) {Check("nested-bullet exception",false,ex.ToString());}
            try {RunFoldedDelimiter(outDir);} catch (Exception ex) {Check("folded-delimiter exception",false,ex.ToString());}
            try {RunCollapsedAfterEnter(outDir);} catch (Exception ex) {Check("collapsed-enter exception",false,ex.ToString());}
            try {RunSwallowedFold(outDir);} catch (Exception ex) {Check("swallowed-fold exception",false,ex.ToString());}
        } catch (Exception ex) {all=false;report.Append("EXCEPTION ").Append(ex).Append('\n');}
        finally {Program.IsSelfTest=oldSelfTest;if (app!=null) app.ShutdownMode=oldShutdown;}
        report.Append("Cases: ").Append(cases).Append("\nResult: ").Append(all ? "PASS" : "FAIL").Append('\n');
        try {
            File.WriteAllText(Path.Combine(outDir,"renderfix-report.txt"),report.ToString(),new UTF8Encoding(false));
            File.WriteAllText(all ? pass : fail,report.ToString(),new UTF8Encoding(false));
        } catch (IOException) {return 1;}
        return all ? 0 : 1;
    }
    static void Check(string name,bool ok,string detail) {
        cases++;all&=ok;
        report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(' ').Append(detail).Append('\n');
    }

    // Shared harness ----------------------------------------------------------------
    static SourceEditor editor;
    static Window window;
    static MarkdownLivePreview.Session session;
    static void Open(string text,double width,string baseDirectory) {
        editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
        editor.Text=text;
        window=new Window {Content=editor,Width=width,Height=900,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=0};
        window.Show();Pump();
        session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {
            FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,BaseDirectory=baseDirectory
        });
        // The caret must not sit on a fixture line: a revealed line draws its source.
        editor.Select(editor.Document.GetLineByNumber(editor.Document.LineCount).Offset,0);
        Pump();
    }
    static void Close() {
        if (session!=null) {session.Dispose();session=null;}
        if (window!=null) {window.Close();window=null;}
        editor=null;
    }
    static bool Drain(DispatcherPriority priority,int timeout) {
        bool reached=false;var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Send) {Interval=TimeSpan.FromMilliseconds(timeout)};
        EventHandler expired=delegate {timer.Stop();frame.Continue=false;};timer.Tick+=expired;timer.Start();
        DispatcherOperation marker=Dispatcher.CurrentDispatcher.BeginInvoke(priority,new Action(delegate {reached=true;frame.Continue=false;}));
        try {Dispatcher.PushFrame(frame);} finally {timer.Stop();timer.Tick-=expired;if (!reached) marker.Abort();}return reached;
    }
    static void Pump() {if (!Drain(DispatcherPriority.ApplicationIdle,2000)) throw new InvalidOperationException("Dispatcher did not become idle");}
    static string Number(double value) {return value.ToString("F2",CultureInfo.InvariantCulture);}
    static void WriteProbePng(string path,int pixelWidth,int pixelHeight) {
        var pixels=new byte[pixelWidth*pixelHeight*4];
        for (int i=0;i<pixels.Length;i+=4) {pixels[i]=40;pixels[i+1]=90;pixels[i+2]=200;pixels[i+3]=255;}
        BitmapSource bitmap=BitmapSource.Create(pixelWidth,pixelHeight,96,96,PixelFormats.Bgra32,null,pixels,pixelWidth*4);
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream=new FileStream(path,FileMode.Create,FileAccess.Write)) encoder.Save(stream);
    }
    static MarkdownLivePreview.TableRowElement RowElement(VisualLine visual) {
        if (visual==null) return null;
        foreach (VisualLineElement element in visual.Elements) {
            var row=element as MarkdownLivePreview.TableRowElement;
            if (row!=null) return row;
        }
        return null;
    }
    static string Glyphs(VisualLine visual) {
        var text=new StringBuilder();
        if (visual==null) return "";
        foreach (TextLine line in visual.TextLines) text.Append(LineGlyphs(line));
        return text.ToString();
    }
    static string LineGlyphs(TextLine line) {
        var text=new StringBuilder();
        if (line==null) return "";
        foreach (IndexedGlyphRun run in line.GetIndexedGlyphRuns())
            if (run.GlyphRun.Characters!=null) text.Append(new string(new List<char>(run.GlyphRun.Characters).ToArray()));
        return text.ToString();
    }
    static double GlyphAdvance(GlyphRun run) {
        double width=0;
        if (run!=null&&run.AdvanceWidths!=null) foreach (double value in run.AdvanceWidths) width+=value;
        return width;
    }

    // 1. An inline icon (17x17 in a 16 px text) is centred on the ink band and keeps the line's
    //    own height; before the fix TextView put its top at the text line's top.
    static void RunIcon(string dir) {
        const string text="아이콘 ![icon](rf-icon.png) 가운데\r\n\r\n아이콘 가운데\r\n\r\n끝\r\n";
        try {
            Open(text,900,dir);
            var view=session.View;
            VisualLine iconVisual=null;
            MarkdownLivePreview.ImageHost host=null;
            for (int attempt=0;attempt<80;attempt++) {
                iconVisual=view.GetVisualLine(1);
                var image=FindImageElement(iconVisual);
                host=image==null ? null : image.Host;
                var child=host==null ? null : host.Child as Image;
                if (child!=null && child.Source!=null) break;
                Pump();System.Threading.Thread.Sleep(10);
            }
            window.UpdateLayout();Pump();
            iconVisual=view.GetVisualLine(1);
            VisualLine plainVisual=view.GetVisualLine(3);
            var element=FindImageElement(iconVisual);
            host=element==null ? null : element.Host;
            if (element==null||host==null||host.Child==null) {Check("icon fixture",false,"no image host on line 1");return;}
            host.Child.UpdateLayout();
            TextLine textLine=iconVisual.GetTextLine(host.Run.Column);
            TextRunProperties properties=element.TextRunProperties;
            var metric=LiveCompositionGeometry.Metric(properties.Typeface,properties.FontRenderingEmSize);
            double baseline=iconVisual.GetTextLineVisualYPosition(textLine,VisualYPosition.Baseline)-view.VerticalOffset;
            double bandCentre=baseline-(metric.Baseline-(metric.Height-metric.Baseline))/2;
            Point top=host.Child.TransformToAncestor(view).Transform(new Point(0,0));
            double iconHeight=host.Child.RenderSize.Height;
            double iconCentre=top.Y+iconHeight/2;
            double delta=iconCentre-bandCentre;
            double lineTop=iconVisual.GetTextLineVisualYPosition(textLine,VisualYPosition.LineTop)-view.VerticalOffset;
            double lineBottom=iconVisual.GetTextLineVisualYPosition(textLine,VisualYPosition.LineBottom)-view.VerticalOffset;
            bool centred=host.ProbeCentred&&Math.Abs(host.DesiredSize.Height)<=.01;
            bool inside=top.Y>=lineTop-1.5&&top.Y+iconHeight<=lineBottom+1.5;
            double plainHeight=plainVisual==null ? -1 : plainVisual.Height;
            bool sameHeight=Math.Abs(iconVisual.Height-plainHeight)<=.01;
            Check("inline icon centred on the ink band, same line height",centred&&Math.Abs(delta)<=1&&sameHeight&&inside,
                "centred="+centred+", delta="+Number(delta)+", icon="+Number(top.Y)+".."+Number(top.Y+iconHeight)+" band="+Number(bandCentre)+
                ", line="+Number(iconVisual.Height)+"/"+Number(plainHeight)+", inside="+inside+", hostHeight="+Number(host.DesiredSize.Height));
        } finally {Close();}
    }
    static MarkdownLivePreview.InlineImageElement FindImageElement(VisualLine visual) {
        if (visual==null) return null;
        foreach (VisualLineElement candidate in visual.Elements) {
            var image=candidate as MarkdownLivePreview.InlineImageElement;
            if (image!=null) return image;
        }
        return null;
    }

    // 2. Narrow window, long Korean cells, six columns and a long unbreakable token: every row
    //    keeps one text line per physical row and every glyph stays inside its own cell.
    static void RunTableWrap(string dir) {
        string korean="| 항목 | 설명 | 비고 |\r\n| --- | --- | --- |\r\n"+
            "| 입력할 때의 동작 | 글자를 입력하면 바로 목록이 됩니다. 아래 행의 같은 칸으로 넘어갑니다. | 자동 줄바꿈이 적용됩니다. |\r\n"+
            "| 두 번째 행 | 표 칸의 글이 길면 자기 칸 안에서 줄이 바뀌어야 합니다. | 좁은 창에서도 같습니다. |\r\n\r\n끝\r\n";
        var wide=new StringBuilder();
        wide.Append("| 하나 | 둘 | 셋 | 넷 | 다섯 | 여섯 |\r\n| --- | --- | --- | --- | --- | --- |\r\n");
        wide.Append("| 첫째 칸의 긴 문장이 여기에서 줄바꿈됩니다 | 둘째 칸의 글자도 꽤 깁니다 | 넷째 칸입니다 | 다섯째 | 여섯째 칸의 마지막 글자 |\r\n\r\n끝\r\n");
        var token=new StringBuilder();
        token.Append("| a | b |\r\n| --- | --- |\r\n| ");
        for (int i=0;i<90;i++) token.Append('W');
        token.Append(" | b |\r\n\r\n끝\r\n");
        RunWrapCase("korean 3 columns 420",korean.ToString(),420,dir);
        RunWrapCase("six columns 420",wide.ToString(),420,dir);
        RunWrapCase("long unbreakable token 420",token.ToString(),420,dir);
    }
    static void RunWrapCase(string name,string text,double width,string dir) {
        try {
            // Start wide, then shrink to the test width: the first layout after a resize still
            // believes the old view width, which is the state that used to re-wrap a row.
            Open(text,900,dir);
            window.Width=width;window.UpdateLayout();Pump();
            var view=session.View;var document=editor.Document;
            int rows=0,wrapped=0;bool aligned=true,bounded=true,gaps=true;string detail="";
            for (int number=1;number<=document.LineCount;number++) {
                DocumentLine line=document.GetLineByNumber(number);
                MarkdownLivePreview.LineInfo info=session.Info(line);
                if (!info.Table||info.Delimiter||info.Cells==null||info.Cells.Count==0) continue;
                VisualLine visual=view.GetVisualLine(number);
                if (visual==null||info.Row==null) {aligned=false;detail="row "+number+" missing";continue;}
                rows++;
                if (visual.TextLines.Count!=info.Row.PhysicalRows) {
                    aligned=false;
                    detail="row "+number+" text lines="+visual.TextLines.Count+" physical rows="+info.Row.PhysicalRows;
                }
                if (info.Row.PhysicalRows>1) wrapped++;
                string glyphDetail;
                if (!GlyphBounds(visual,info,out glyphDetail)) {bounded=false;detail="row "+number+' '+glyphDetail;}
                string gapDetail;
                if (!BaselineGaps(visual,info,out gapDetail)) {gaps=false;detail="row "+number+' '+gapDetail;}
            }
            Check("table wrap "+name,rows>0&&aligned&&bounded&&gaps,
                "rows="+rows+", wrapped="+wrapped+", aligned="+aligned+", bounds="+bounded+", gaps="+gaps+"; "+detail);
            // The fixture must exercise the reported case: a cell that wraps inside its column.
            Check("table wrap "+name+" has a wrapped cell",wrapped>0,"wrapped rows="+wrapped);
        } finally {Close();}
    }
    static bool GlyphBounds(VisualLine visual,MarkdownLivePreview.LineInfo info,out string detail) {
        detail=null;int runs=0;
        MarkdownLivePreview.RowLayout row=info.Row;
        foreach (TextLine line in visual.TextLines) {
            foreach (IndexedGlyphRun glyph in line.GetIndexedGlyphRuns()) {
                int offset=visual.GetRelativeOffset(glyph.TextSourceCharacterIndex);
                int cell=-1;
                for (int c=0;c<info.Cells.Count;c++) if (offset>=info.Cells[c].Start&&offset<info.Cells[c].End) {cell=c;break;}
                if (cell<0) {detail="glyph at offset "+offset+" belongs to no cell";return false;}
                Rect ink=glyph.GlyphRun.ComputeInkBoundingBox();
                double edge=glyph.GlyphRun.BaselineOrigin.X+ink.Right;
                if (edge>row.Boundaries[cell+1]+.5) {
                    detail="cell "+cell+" glyph edge "+Number(edge)+" past "+Number(row.Boundaries[cell+1]);
                    return false;
                }
                runs++;
            }
        }
        if (runs==0) {detail="no glyph runs";return false;}
        return true;
    }
    // Wrapped lines of one cell keep the paragraph's own line spacing: every pair of consecutive
    // physical rows of that cell has the same baseline distance.
    static bool BaselineGaps(VisualLine visual,MarkdownLivePreview.LineInfo info,out string gapDetail) {
        gapDetail=null;
        int best=-1,bestCount=1;
        for (int c=0;c<info.Row.Slices.Length;c++) {
            List<MarkdownLivePreview.CellSlice> slices=info.Row.Slices[c];
            if (slices!=null&&slices.Count>bestCount) {bestCount=slices.Count;best=c;}
        }
        if (best<0||bestCount<3) return true;
        double expected=16*150/100.0;
        double previous=Double.NaN;
        for (int r=0;r<bestCount;r++) {
            if (r>=visual.TextLines.Count) {gapDetail="cell "+best+" row "+r+" has no text line";return false;}
            double baseline=visual.GetTextLineVisualYPosition(visual.TextLines[r],VisualYPosition.Baseline);
            if (!Double.IsNaN(previous)&&Math.Abs(baseline-previous-expected)>.6) {
                gapDetail="cell "+best+" row "+r+" baseline gap "+Number(baseline-previous)+" expected "+Number(expected);
                return false;
            }
            previous=baseline;
        }
        return true;
    }

    // 3. Lists inside a blockquote: the marker after "> " renders as the list's own bullet, number
    //    or checkbox, the quote bar keeps the line, and the visible text carries no marker.
    static void RunQuoteLists(string dir) {
        string text="> - 첫째\r\n> 1. 하나\r\n> 2. 둘\r\n> - [ ] 할 일\r\n> - **굵게**\r\n>   - 들여쓴 항목\r\n> **굵은 문단**\r\n> 보통 인용\r\n\r\n끝\r\n";
        string[] bodies=new string[] {"첫째","하나","둘","할 일","굵게","들여쓴 항목","굵은 문단","보통 인용"};
        try {
            Open(text,900,dir);
            var view=session.View;var document=editor.Document;
            int[] offsets;
            string[] visible=session.VisibleText(out offsets).Split('\n');
            bool lists=true,markers=true,numbers=true,visibleOk=true,bars=true,bold=true,depthOk=true;
            string detail="";
            for (int number=1;number<=8;number++) {
                DocumentLine line=document.GetLineByNumber(number);
                MarkdownLivePreview.LineInfo info=session.Info(line);
                VisualLine visual=view.GetVisualLine(number);
                bool listLine=number<=6;
                if (!info.Quote) {lists=false;detail="line "+number+" not a quote";continue;}
                if (listLine) {
                    if (info.List==null) {lists=false;detail="line "+number+" has no list";continue;}
                    if (number==4) {
                        bool box=false;
                        if (visual!=null) foreach (VisualLineElement element in visual.Elements)
                            if (element.GetType().Name=="TaskBoxElement") box=true;
                        if (!box) {markers=false;detail="line "+number+" has no checkbox";}
                    } else {
                        string expected=info.List.Ordered ? info.List.Number.ToString(CultureInfo.InvariantCulture)+"." : "•";
                        MarkdownLivePreview.MarkerElement marker=MarkerAt(visual,info.ListStart);
                        if (marker==null||marker.Label!=expected) {
                            markers=false;
                            detail="line "+number+" marker="+(marker==null?"missing":marker.Label+" @ "+marker.RelativeTextOffset)+" expected "+expected+" at "+info.ListStart;
                        }
                    }
                    if (info.List.Ordered&&info.List.Number!=(number==2?1:number==3?2:info.List.Number)) numbers=false;
                    if (number==6&&info.List.Depth!=1) {depthOk=false;detail="nested depth="+info.List.Depth;}
                } else {
                    // Lines 7 and 8 are quote paragraphs: no list, still a quote with its bar.
                    if (info.List!=null) {lists=false;detail="line "+number+" is unexpectedly a list";}
                }
                // The quote marker itself stays an element at the '>' so the bar renderer finds it.
                if (!QuoteMarkerElement(visual,info)) {bars=false;detail="line "+number+" quote marker missing";}
                string expectedBody=bodies[number-1];
                if (number-1>=visible.Length||visible[number-1]!=expectedBody) {
                    visibleOk=false;
                    detail="line "+number+" visible='"+(number-1<visible.Length?visible[number-1]:"-")+"' expected '"+expectedBody+"'";
                } else if (visible[number-1].IndexOf('-')>=0||visible[number-1].IndexOf('>')>=0) {
                    visibleOk=false;detail="line "+number+" visible carries a marker";
                }
            }
            Check("quote list lines are lists",lists,detail);
            Check("quote list bullet/number/checkbox markers",markers,detail);
            Check("quote list numbering 1,2",numbers,"first two ordered items");
            Check("quote list visible text has no markers",visibleOk,detail);
            Check("quote list quote bar marker",bars,detail);
            List<double> quoteXs=QuoteXs();
            bool straight=quoteXs.Count==8;
            foreach(double x in quoteXs)if(quoteXs.Count>0&&Math.Abs(x-quoteXs[0])>.01)straight=false;
            Check("review7 mixed quote paragraphs and lists share one bar x",straight,"bars="+quoteXs.Count);
            Check("quote list nested depth",depthOk,detail);
            Check("quote list bold survives",BoldSpan(session.Info(document.GetLineByNumber(7)),"굵은 문단")&&BoldSpan(session.Info(document.GetLineByNumber(5)),"굵게"),
                "bold inside a quoted item and a quoted paragraph");
        } finally {Close();}
    }
    static MarkdownLivePreview.MarkerElement MarkerAt(VisualLine visual,int relative) {
        if (visual==null) return null;
        foreach (VisualLineElement element in visual.Elements) {
            var marker=element as MarkdownLivePreview.MarkerElement;
            if (marker!=null&&marker.RelativeTextOffset==relative&&marker.Label!=null) return marker;
        }
        return null;
    }
    static bool QuoteMarkerElement(VisualLine visual,MarkdownLivePreview.LineInfo info) {
        if (visual==null) return false;
        foreach (VisualLineElement element in visual.Elements) {
            if (!(element is MarkdownLivePreview.MarkerElement)) continue;
            int offset=element.RelativeTextOffset;
            if (offset>=0&&offset<info.Text.Length&&info.Text[offset]=='>') return true;
        }
        return false;
    }
    static bool BoldSpan(MarkdownLivePreview.LineInfo info,string body) {
        int start=info.Text.IndexOf(body,StringComparison.Ordinal);
        if (start<0) return false;
        foreach (MarkdownLivePreview.StyledPart part in info.Styles)
            if ((part.Style&MarkdownLivePreview.Style.Bold)!=0&&part.Start<=start&&part.Start+part.Length>=start+body.Length) return true;
        return false;
    }

    // 4. A table under a list item starts at the item's body x, the boundaries stay shared by the
    //    renderer, hit-test and caret paths, and the list item after the table keeps its depth.
    static void RunIndentedTable(string dir) {
        string text="- 항목\r\n  | a | b |\r\n  | --- | --- |\r\n  | 1 | 2 |\r\n  - 다음\r\n\r\n끝\r\n";
        try {
            Open(text,900,dir);
            var view=session.View;var document=editor.Document;
            DocumentLine item=document.GetLineByNumber(1),header=document.GetLineByNumber(2),data=document.GetLineByNumber(4),after=document.GetLineByNumber(5);
            MarkdownLivePreview.LineInfo itemInfo=session.Info(item),headerInfo=session.Info(header),dataInfo=session.Info(data),afterInfo=session.Info(after);
            VisualLine itemVisual=view.GetVisualLine(1);
            double bodyX=itemVisual==null ? -1 : itemVisual.GetVisualPosition(itemVisual.GetVisualColumn(itemInfo.ListBody),VisualYPosition.LineTop).X;
            double left=headerInfo.Row==null ? -1 : headerInfo.Row.Boundaries[0];
            Check("indented table sits at the list body x",left>=0&&bodyX>0&&Math.Abs(left-bodyX)<=2,
                "table left="+Number(left)+", list body x="+Number(bodyX)+", indent="+Number(headerInfo.TableIndent));
            VisualLine dataVisual=view.GetVisualLine(4);
            MarkdownLivePreview.TableRowElement row=RowElement(dataVisual);
            bool shared=row!=null&&dataInfo.Row!=null&&Math.Abs(dataInfo.Row.Boundaries[0]-left)<=.01;
            Check("indented table rows share the indented boundaries",shared,
                "header="+Number(left)+", data="+(dataInfo.Row==null?"missing":Number(dataInfo.Row.Boundaries[0])));
            Check("list item after the table keeps its depth",afterInfo.List!=null&&afterInfo.List.Depth==1,
                "list="+(afterInfo.List!=null)+", depth="+(afterInfo.List==null?-1:afterInfo.List.Depth));
            bool cellsOk=false;bool caretInCell=false;string detail="";
            if (row!=null) {
                Rect box0=session.TableCellBox(dataVisual,0),box1=session.TableCellBox(dataVisual,1);
                cellsOk=!box0.IsEmpty&&!box1.IsEmpty&&Math.Abs(box1.Left-(box0.Left+dataInfo.Row.Widths[0]))<=.01&&box0.Left>=left-1;
                Point point=new Point(box1.Left+Math.Min(4,Math.Max(1,box1.Width/2)),box1.Top+Math.Min(6,Math.Max(1,box1.Height/2)));
                bool placed=session.PlaceTableCaret(point,false);
                int caret=editor.CaretOffset;
                MarkdownLivePreview.Cell target=dataInfo.Cells[1];
                caretInCell=placed&&caret>=data.Offset+target.Start&&caret<=data.Offset+target.End;
                detail="box0="+Number(box0.Left)+", box1="+Number(box1.Left)+", placed="+placed+", caret="+caret+" in "+target.Start+".."+target.End;
            }
            Check("indented table cell boxes follow the shared boundaries",cellsOk,detail);
            Check("indented table click/caret lands in the clicked cell",caretInCell,detail);
            // The same table at a narrow window: the indent comes out of the width budget, so the
            // rows still fit their paragraph and keep one text line per physical row.
            editor.Select(document.GetLineByNumber(document.LineCount).Offset,0);Pump();
            window.Width=420;window.UpdateLayout();Pump();
            int narrowRows=0;bool narrowAligned=true,narrowLeft=true;string narrowDetail="";
            for (int number=1;number<=document.LineCount;number++) {
                MarkdownLivePreview.LineInfo info=session.Info(document.GetLineByNumber(number));
                if (!info.Table||info.Delimiter||info.Row==null) continue;
                VisualLine visual=view.GetVisualLine(number);
                narrowRows++;
                // 11-01 user decision (MarkdownLivePreview.ListMarkerSlot=32): the table sits at the list
                // body x, which is the measured bodyX (32 for a top-level bullet), not a fixed 18 px slot.
                if (Math.Abs(info.Row.Boundaries[0]-bodyX)>1) {narrowLeft=false;narrowDetail="row "+number+" left="+Number(info.Row.Boundaries[0]);}
                if (visual==null||visual.TextLines.Count!=info.Row.PhysicalRows) {
                    narrowAligned=false;
                    narrowDetail="row "+number+" lines="+(visual==null?-1:visual.TextLines.Count)+" rows="+info.Row.PhysicalRows;
                }
            }
            Check("indented table fits the narrow view",narrowRows>0&&narrowAligned&&narrowLeft,
                "rows="+narrowRows+", aligned="+narrowAligned+", left="+narrowLeft+"; "+narrowDetail);
        } finally {Close();}
    }

    // 5. A bold bracketed label followed by an icon (the example document's tip lines): the link
    //    parser must not pair the label's '[' with the image's "](".
    static void RunLabelAndIcon(string dir) {
        string text="> **[작성 팁]** ![H2](rf-icon.png) 줄 앞에 글자\r\n\r\n끝\r\n";
        try {
            Open(text,900,dir);
            var view=session.View;var document=editor.Document;
            MarkdownLivePreview.LineInfo info=session.Info(document.GetLineByNumber(1));
            bool bold=BoldSpan(info,"[작성 팁]");
            bool picture=false;
            foreach (MarkdownLivePreview.Replacement item in info.Replacements)
                if (item.Kind==MarkdownLivePreview.ReplacementKind.Image) picture=true;
            int[] offsets;
            string[] visible=session.VisibleText(out offsets).Split('\n');
            string first=visible.Length>0 ? visible[0] : "-";
            bool clean=first.IndexOf("**",StringComparison.Ordinal)<0&&first.IndexOf("![",StringComparison.Ordinal)<0;
            Check("tip label keeps its bold and its icon",bold&&picture&&clean,
                "bold="+bold+", image="+picture+", visible=["+first+"]");
            string glyphs=Glyphs(view.GetVisualLine(1));
            Check("tip label line draws no markup",glyphs.Length>0&&glyphs.IndexOf("**",StringComparison.Ordinal)<0&&glyphs.IndexOf("![",StringComparison.Ordinal)<0,
                "glyphs=["+glyphs+"]");
        } finally {Close();}
    }

    // 7. A bulk edit into a table cell (what a paste does) grows the column in the same frame as
    //    typing the same text. The paste clamps its text into the cell even when the caret sits in
    //    the cell's own padding; the growth used to need the caret inside the cell's text range,
    //    so such a paste left the column narrow until the caret left the table and the idle
    //    re-sample ran.
    static void RunCellEdit(string dir) {
        const string sentence="표 칸에 긴 문장을 붙여넣으면 그 칸의 폭이 바로 넓어져야 하고 다음 단어도 이어서 보여야 합니다";
        const string text="| 항목 | 값 |\r\n| --- | --- |\r\n| x | y |\r\n\r\n끝\r\n";
        double typed=CellWidthAfterEdit(dir,text,sentence,0);
        double atCell=CellWidthAfterEdit(dir,text,sentence,1);
        double atPadding=CellWidthAfterEdit(dir,text,sentence,2);
        double atRowEnd=CellWidthAfterEdit(dir,text,sentence,3);
        Check("cell bulk edit grows the column like typing",
            typed>250&&Math.Abs(typed-atCell)<=.5&&Math.Abs(typed-atPadding)<=2&&Math.Abs(typed-atRowEnd)<=2,
            "typed="+Number(typed)+", insertAtCell="+Number(atCell)+", insertAtPadding="+Number(atPadding)+
            ", insertAtRowEnd="+Number(atRowEnd));
    }
    // 0: the editor's input path with the caret at the cell text's end (the reference); 1: a plain
    // document insert at that caret; 2: a paste-shaped insert with the caret in the cell's own
    // padding (the insert lands at the cell start, the caret stays in the padding); 3: the same
    // with the caret past the last pipe, where the paste clamps into the last cell.
    static double CellWidthAfterEdit(string dir,string text,string sentence,int mode) {
        try {
            Open(text,900,dir);
            var document=editor.Document;
            DocumentLine line=document.GetLineByNumber(3);
            MarkdownLivePreview.LineInfo info=session.Info(line);
            int column=mode==3?info.Cells.Count-1:0;
            int cell=info.Cells[column].Start;
            int caret,insert;
            if (mode==0) {caret=line.Offset+info.Cells[0].End;insert=caret;}
            else if (mode==1) {caret=line.Offset+cell+1;insert=caret;}
            else if (mode==2) {caret=line.Offset+cell-1;insert=line.Offset+cell;}
            else {caret=line.EndOffset;insert=line.Offset+info.Cells[column].End;}
            editor.Select(caret,0);Pump();
            if (mode==0) editor.TextArea.PerformTextInput(sentence);
            else document.Insert(insert,sentence);
            Pump();
            var row=session.Info(document.GetLineByNumber(3)).Row;
            return row==null||column>=row.Widths.Length?-1:row.Widths[column];
        } finally {Close();}
    }

    // 8. A bullet item nested under a numbered item. 11-01 user decision: every marker takes one
    //    ListMarkerSlot (32 px), so the bullet and number slots are equal and ListContentWidth gives
    //    the nested item the ordinary 18 px level step: its body starts 18 px right of the parent
    //    item's body x, and its marker slot ends at its own body x (slot left = body - 32). The old
    //    18 px bullet slot made the body align with the parent body; that case no longer exists.
    static void RunNestedBullet(string dir) {
        const string text="1. 첫째\r\n2. 부모 항목의 긴 본문\r\n   - 아이 항목 하나\r\n   - 아이 항목 둘\r\n3. 셋째\r\n\r\n끝\r\n";
        try {
            Open(text,900,dir);
            var document=editor.Document;
            var view=session.View;
            var parentInfo=session.Info(document.GetLineByNumber(2));
            var childInfo=session.Info(document.GetLineByNumber(3));
            VisualLine parentVisual=view.GetVisualLine(2),childVisual=view.GetVisualLine(3);
            double parentBody=BodyX(parentVisual,parentInfo),childBody=BodyX(childVisual,childInfo);
            double slotLeft=childVisual==null||childInfo.List==null?-1:
                childVisual.GetVisualPosition(childVisual.GetVisualColumn(childInfo.ListStart),VisualYPosition.LineTop).X;
            Check("nested bullet body steps one level (18 px) right of the numbered parent body",
                parentBody>0&&Math.Abs(childBody-(parentBody+18))<=1.5,
                "parentBody="+Number(parentBody)+", childBody="+Number(childBody)+", expected="+Number(parentBody+18)+", slotLeft="+Number(slotLeft));
            Check("nested bullet marker slot ends at its own body x",
                slotLeft>=0&&Math.Abs(slotLeft-(childBody-MarkdownLivePreview.ListMarkerSlot))<=1.5,
                "slotLeft="+Number(slotLeft)+", expected="+Number(childBody-MarkdownLivePreview.ListMarkerSlot));
        } finally {Close();}
    }
    static double BodyX(VisualLine visual,MarkdownLivePreview.LineInfo info) {
        return visual==null?0:visual.GetVisualPosition(visual.GetVisualColumn(info.ListBody),VisualYPosition.LineTop).X;
    }

    static List<double> QuoteXs() {
        var drawing=new DrawingGroup();
        using(DrawingContext context=drawing.Open())new MarkdownLivePreview.BlockRenderer(session).Draw(session.View,context);
        var result=new List<double>();
        foreach(Drawing child in drawing.Children) {
            var geometry=child as GeometryDrawing;
            if(geometry==null||geometry.Pen==null||Math.Abs(geometry.Pen.Thickness-3)>.01)continue;
            var line=geometry.Geometry as LineGeometry;
            if(line!=null)result.Add(line.StartPoint.X);
        }
        return result;
    }
    static void RunReviewTables(string dir) {
        try {
            Open("- > inside list\r\n\r\nend\r\n",900,dir);
            var info=session.Info(editor.Document.GetLineByNumber(1));
            var visual=session.View.GetVisualLine(1);
            double body=visual.GetVisualPosition(visual.GetVisualColumn(info.ListBody),VisualYPosition.LineTop).X;
            List<double> bars=QuoteXs();
            Check("review7 quote inside a list keeps the old bar x",bars.Count==1&&Math.Abs(bars[0]-body)<.01,
                "bar="+(bars.Count==0?"missing":Number(bars[0]))+", body="+Number(body));
        } finally {Close();}
        string[] prefixes={"","- item\r\n","1. item\r\n","- [ ] item\r\n","- outer\r\n  - inner\r\n"};
        foreach(string prefix in prefixes) {
            try {
                // Header, delimiter, data and a newly inserted-style row have different spaces.
                string indent=prefix.IndexOf("inner",StringComparison.Ordinal)>=0?"    ":"  ";
                Open(prefix+indent+"| a | b |\r\n| --- | --- |\r\n      | c | d |\r\n| new | row |\r\n\r\nend\r\n",900,dir);
                int header=prefix.Length==0?1:prefix.IndexOf("inner",StringComparison.Ordinal)>=0?3:2;
                double expected=0;
                if(prefix.Length>0) {
                    DocumentLine item=editor.Document.GetLineByNumber(header-1);
                    var itemInfo=session.Info(item);
                    var itemVisual=session.View.GetVisualLine(header-1);
                    expected=itemVisual.GetVisualPosition(itemVisual.GetVisualColumn(itemInfo.ListBody),VisualYPosition.LineTop).X;
                }
                bool common=true;
                for(int n=header;n<header+4;n++) {
                    var rowInfo=session.Info(editor.Document.GetLineByNumber(n));
                    if(!rowInfo.Table||Math.Abs(rowInfo.TableIndent-expected)>2)common=false;
                    if(!rowInfo.Delimiter&&(rowInfo.Row==null||Math.Abs(rowInfo.Row.Boundaries[0]-expected)>2))common=false;
                }
                Check("review8 header context aligns every table row",common,"prefix=["+prefix.Replace("\r\n"," / ")+"], expected="+Number(expected));
            } finally {Close();}
        }
        try {
            string text="| heading | second |\r\n| --- | --- |\r\n| "+new string('W',65)+" | "+new string('M',65)+" |\r\n\r\nend\r\n";
            Open(text,420,dir);
            var line=editor.Document.GetLineByNumber(3);
            for(int pass=0;pass<2;pass++) {
                window.Width=420;window.UpdateLayout();Pump();
                var narrow=session.Info(line).Row;
                double before=narrow.Boundaries[narrow.Boundaries.Length-1];
                window.Width=900;window.UpdateLayout();Pump();
                var wide=session.Info(line).Row;
                double after=wide.Boundaries[wide.Boundaries.Length-1];
                Check("review9 widening refits table columns "+pass,after>before+100&&Math.Abs(wide.BaseWidth-session.RowWidth())<.5,
                    "before="+Number(before)+", after="+Number(after)+", budget="+Number(wide.BaseWidth));
                long builds=MarkdownLivePreview.DiagLineBuilds;Pump();Pump();
                Check("review9 layout settles without a redraw loop "+pass,MarkdownLivePreview.DiagLineBuilds==builds,"extra builds="+(MarkdownLivePreview.DiagLineBuilds-builds));
            }
        } finally {Close();}
    }

    // 6. The session can attach while the editor is still parentless (Ideal text mode) and the
    //    view then inherits the window's Display mode, whose whole-pixel advances are wider. The
    //    cell measurement must follow the view's mode, or the row re-wraps.
    static void RunDisplayMode(string dir) {
        var text=new StringBuilder();
        text.Append("| first | second |\r\n| --- | --- |\r\n| ");
        for (int i=0;i<6;i++) text.Append("longer words that wrap inside one cell and keep another physical row ");
        text.Append("| x |\r\n\r\n끝\r\n");
        try {
            editor=new SourceEditor {FontFamily=new FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
            editor.Text=text.ToString();
            session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,BaseDirectory=dir});
            // The view picks the window's mode when the editor is parented, after the attach.
            TextOptions.SetTextFormattingMode(editor,TextFormattingMode.Display);
            window=new Window {Content=editor,Width=900,Height=900,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=0};
            window.Show();Pump();
            editor.Select(editor.Document.GetLineByNumber(editor.Document.LineCount).Offset,0);Pump();
            var view=session.View;var document=editor.Document;
            bool aligned=true,measured=true;string detail="";
            for (int number=1;number<=document.LineCount;number++) {
                DocumentLine line=document.GetLineByNumber(number);
                MarkdownLivePreview.LineInfo info=session.Info(line);
                if (!info.Table||info.Delimiter||info.Row==null||info.Cells==null||info.Cells.Count==0) continue;
                VisualLine visual=view.GetVisualLine(number);
                if (visual==null||visual.TextLines.Count!=info.Row.PhysicalRows) {
                    aligned=false;
                    detail="row "+number+" lines="+(visual==null?-1:visual.TextLines.Count)+" rows="+info.Row.PhysicalRows;
                    continue;
                }
                // The drawn advance of a cell's first slice must equal the width it was measured
                // at: that is what proves both sides use the same text mode.
                MarkdownLivePreview.CellText cell=info.Row.Texts[0];
                MarkdownLivePreview.CellSlice slice=info.Row.Slices[0][0];
                if (cell!=null&&slice.Length>1) {
                    foreach (TextLine textLine in visual.TextLines) {
                        foreach (IndexedGlyphRun glyph in textLine.GetIndexedGlyphRuns()) {
                            if (glyph.TextSourceCharacterIndex!=visual.GetVisualColumn(info.Cells[0].Start)) continue;
                            double drawn=GlyphAdvance(glyph.GlyphRun);
                            if (Math.Abs(drawn-slice.Width)>.75) {
                                measured=false;
                                detail="drawn "+Number(drawn)+" measured "+Number(slice.Width)+" mode="+TextOptions.GetTextFormattingMode(view);
                            }
                        }
                    }
                }
            }
            Check("cell measurement follows the view's text mode",aligned&&measured&&TextOptions.GetTextFormattingMode(view)==TextFormattingMode.Display,
                "aligned="+aligned+", measured="+measured+", view mode="+TextOptions.GetTextFormattingMode(view)+"; "+detail);
        } finally {Close();}
    }

    // 9. A folded delimiter row must always be inside the visual line of the header above it, or
    //    the visual-line walk starts a build on the collapsed row and TextView throws "Trying to
    //    build visual line from collapsed line" (monkey seed 5 step 448, seed 4 step 1020).
    //    A header whose cells do not parse (a line holding only " |") is drawn as plain text, so
    //    the table-row generator is bypassed on the plain, raw and row paths alike.
    internal static readonly string FoldedDelimiterFixture = " |\r\n|---|\r\n\r\nend\r\n";
    static void RunFoldedDelimiter(string dir) {
        var watch = new DispatcherWatch();
        string detail = "";
        try {
            Open(FoldedDelimiterFixture, 900, dir);
            var document = editor.Document;
            int[] carets = new int[] { 4, 1, 3, 2, 1 };
            bool ok = true;
            foreach (int number in carets) {
                editor.Select(document.GetLineByNumber(number).Offset, 0); Pump();
                bool valid = session.View.VisualLinesValid;
                bool covered = valid && CollapsesCovered();
                if (!valid || !covered) { ok = false; detail += " line=" + number + " valid=" + valid + " covered=" + covered; }
                if (watch.First != null) { ok = false; break; }
            }
            Pump();
            Check("a folded delimiter row stays inside its header's visual line",
                ok && watch.First == null && session.View.VisualLinesValid && CollapsesCovered(),
                "dispatcher=" + watch.Detail(600) + ", legal=" + ok + "; " + DescribeView("end") + detail);
        } finally {
            // A failed rebuild can leave a visual line "in construction"; disposing the view then
            // throws and would hide the case's own result. The next case builds a new editor.
            try { Close(); } catch (Exception) { }
            watch.Dispose();
        }
    }
    // The exact monkey reduction: Enter replaces a selection on a header row (that is at once the
    // session's folded line) and leaves a " |" header above the delimiter row.
    internal static readonly string CollapsedEnterFixture =
        " monk한포.f<br>너저버마한글 붙여넣기 값 4 <br>e*CKOV<br>초row* |\n" +
        "| --- | --- |\n" +
        "## |  다 | 라<br>Bo<HGGkhUbr>*더히버퍼투* |\n" +
        "### | <br>차수마| 가 | 나 |<br>| --- | --- |<br>| 다 | 라 | | 제목2 | 제목3 | 제목4 |\n" +
        "|---|---|---|---|\n" +
        "|  |  |  |  |\n" +
        "|  |  |  |  |\n" +
        "|  |  |  |  |";
    static void RunCollapsedAfterEnter(string dir) {
        var watch = new DispatcherWatch();
        try {
            Open(CollapsedEnterFixture, 900, dir);
            var document = editor.Document;
            editor.Select(154, 18); Pump();
            CommandResult command = EnterLikeTheApp(document);
            ApplyResult(document, command);
            Pump();
            bool valid = session.View.VisualLinesValid;
            bool covered = valid && CollapsesCovered();
            Pump();
            bool settled = session.View.VisualLinesValid && CollapsesCovered();
            string text = document.Text.Replace("\r\n", "\\n").Replace("\n", "\\n");
            Check("enter at a folded table row keeps the view consistent",
                watch.First == null && valid && covered && settled && editor.CaretOffset == 155 &&
                text.Contains("| \uB2E4 | \uB77C |\\n |\\n|---"),
                "dispatcher=" + watch.Detail(600) + ", valid=" + valid + ", covered=" + covered + ", settled=" + settled +
                ", caret=" + editor.CaretOffset + ", command=" + DescribeCommand(command) + "; " + DescribeView("after"));
        } finally {
            try { Close(); } catch (Exception) { }
            watch.Dispose();
        }
    }
    // 10. A header that becomes the folded delimiter row of the line above it used to keep its own fold:
    //     the line under it stayed collapsed with no visual line left to span it, and the next layout
    //     threw "Trying to build visual line from collapsed line" (monkey seed 4 step 1014). Here the
    //     first row has one cell ("|  |  "), so it is no header over the two-cell row below it; the
    //     row below is the header of the third line until a typed "x" gives the first row a second
    //     cell, which makes it the header and the second row its folded delimiter row.
    //     The repair counters must stay untouched: the second layers (visual line guard, layout
    //     recovery) would hide a hole in the first one.
    internal static readonly string SwallowedFoldFixture = "|  |  \n|---|---|\n|---|---|\n\nend\n";
    static void RunSwallowedFold(string dir) {
        var watch = new DispatcherWatch();
        try {
            Open(SwallowedFoldFixture, 900, dir);
            var document = editor.Document;
            object heightTree = HeightTreeOf(session);
            bool before = session.View.VisualLinesValid && CollapsedAt(heightTree, 3) && !CollapsedAt(heightTree, 2) && CollapsesCovered();
            long guards = MarkdownLivePreview.DiagFoldGuards, recoveries = MarkdownLivePreview.DiagFoldRecoveries;
            editor.Select(document.GetLineByNumber(1).EndOffset, 0); Pump();
            editor.TextArea.PerformTextInput("x"); Pump();
            if (document.GetLineByNumber(1).Length == 6) { document.Insert(document.GetLineByNumber(1).EndOffset, "x"); Pump(); }
            Pump();
            DocumentLine first = document.GetLineByNumber(1);
            string firstText = document.GetText(first.Offset, first.Length);
            bool valid = session.View.VisualLinesValid;
            heightTree = HeightTreeOf(session);
            bool covered = valid && CollapsesCovered();
            bool folded = CollapsedAt(heightTree, 2) && !CollapsedAt(heightTree, 3);
            Check("a header folded into the line above releases its own fold",
                watch.First == null && before && firstText == "|  |  x" && valid && covered && folded &&
                MarkdownLivePreview.DiagFoldGuards == guards && MarkdownLivePreview.DiagFoldRecoveries == recoveries,
                "dispatcher=" + watch.Detail(600) + ", before=" + before + ", first=[" + Escape(firstText) + "], valid=" + valid + ", covered=" + covered +
                ", folded=" + folded + ", guards=" + (MarkdownLivePreview.DiagFoldGuards - guards) + ", recoveries=" + (MarkdownLivePreview.DiagFoldRecoveries - recoveries) + "; " + DescribeView("after"));
        } finally {
            try { Close(); } catch (Exception) { }
            watch.Dispose();
        }
    }
    // Records the first unhandled dispatcher exception (and keeps it from ending the probe), so a
    // rebuild that throws inside a layout pass fails the case instead of crashing the run.
    sealed class DispatcherWatch {
        internal Exception First;
        string stack;
        readonly DispatcherUnhandledExceptionEventHandler handler;
        internal DispatcherWatch() {
            handler = delegate(object sender, DispatcherUnhandledExceptionEventArgs args) {
                if (First == null && args.Exception != null) { First = args.Exception; stack = args.Exception.ToString(); }
                args.Handled = true;
            };
            Dispatcher.CurrentDispatcher.UnhandledException += handler;
            if (Application.Current != null) Application.Current.DispatcherUnhandledException += handler;
        }
        internal string Detail(int limit) {
            if (First == null) return "none";
            string trace = stack == null ? "" : " @ " + Escape(stack.Length > limit ? stack.Substring(0, limit) : stack);
            return First.GetType().Name + ": " + First.Message + trace;
        }
        public void Dispose() {
            Dispatcher.CurrentDispatcher.UnhandledException -= handler;
            if (Application.Current != null) Application.Current.DispatcherUnhandledException -= handler;
        }
    }
    static CommandResult EnterLikeTheApp(TextDocument document) {
        int at = editor.SelectionStart, length = editor.SelectionLength;
        CommandResult result = MarkdownTableCommands.EnterOutsideTable(document, at, length);
        if (result == null) {
            DocumentLine line = document.GetLineByOffset(at);
            MarkdownLivePreview.LineInfo info = session.Info(line);
            List<MarkdownLivePreview.Cell> cells = info != null && info.Table && !info.Delimiter ? info.Cells : null;
            result = MarkdownTableCommands.EnterInCell(document, at, length, cells);
        }
        if (result == null) result = MarkdownStructuralInput.Enter(document, at, length);
        return result;
    }
    static string DescribeCommand(CommandResult result) {
        if (result == null) return "(null)";
        var text = new StringBuilder("sel=" + result.SelectionStart + "+" + result.SelectionLength + " edits=");
        foreach (SourceEdit edit in result.Edits)
            text.Append('[').Append(edit.Offset).Append(',').Append(edit.Length).Append(',').Append(edit.Text == null ? "null" : Escape(edit.Text)).Append(']');
        return text.ToString();
    }
    static string Escape(string value) {
        var text = new StringBuilder();
        foreach (char c in value) {
            if (c == '\n') text.Append("\\n");
            else if (c == '\r') text.Append("\\r");
            else if (c < 32) text.Append('?');
            else text.Append(c);
        }
        return text.ToString();
    }
    static void ApplyResult(TextDocument document, CommandResult result) {
        if (result == null) { editor.TextArea.PerformTextInput("\n"); return; }
        if (result.Edits.Count > 0) {
            document.BeginUpdate();
            try { foreach (SourceEdit edit in result.Edits) document.Replace(edit.Offset, edit.Length, edit.Text); }
            finally { document.EndUpdate(); }
        }
        int start = Math.Max(0, Math.Min(result.SelectionStart, document.TextLength));
        int length = Math.Max(0, Math.Min(result.SelectionLength, document.TextLength - start));
        editor.Select(start, length);
    }
    // 7. The raw source view: a rendered line and a revealed caret line both keep the document
    //    face, so the line does not change font or width as the caret moves. Only the detached
    //    raw view (원문 toggle, no live session) is source text: its ASCII symbols take the code
    //    face while Hangul stays in the document face (a Korean face draws '<' at 0.70em).
    static void RunSourceFace(string dir) {
        Open("<span style=\"color:red\">텍스트</span> a<b>c\n\n둘째 줄\n",720,dir);
        try {
            editor.UpdateLayout();Pump();
            string renderedAscii=GlyphFamily(1,'<'),renderedHangul=GlyphFamily(1,'텍');
            Check("rendered line keeps the document face",renderedAscii=="Malgun Gothic"&&renderedHangul=="Malgun Gothic",
                "ascii="+renderedAscii+", hangul="+renderedHangul);
            editor.Select(editor.Document.GetLineByNumber(1).EndOffset,0);
            editor.UpdateLayout();Pump();
            string rawLess=GlyphFamily(1,'<'),rawGreater=GlyphFamily(1,'>'),rawHangul=GlyphFamily(1,'텍');
            Check("revealed caret line keeps the document face for ASCII",rawLess=="Malgun Gothic"&&rawGreater=="Malgun Gothic",
                "less="+rawLess+", greater="+rawGreater);
            Check("revealed caret line keeps Hangul in the document face",rawHangul=="Malgun Gothic","hangul="+rawHangul);
            Check("source face keeps the Korean face for Hangul",MarkdownLivePreview.SourceFace("Malgun Gothic").Source=="Consolas, Malgun Gothic",
                "face="+MarkdownLivePreview.SourceFace("Malgun Gothic").Source);
            Check("a face without Hangul is left alone",MarkdownLivePreview.SourceFace("Segoe UI").Source=="Segoe UI",
                "face="+MarkdownLivePreview.SourceFace("Segoe UI").Source);
        } finally {Close();}
        OpenDetachedRaw("<span style=\"color:red\">텍스트</span> a<b>c\n\n둘째 줄\n",720);
        try {
            editor.UpdateLayout();Pump();
            string rawLess=GlyphFamily(1,'<'),rawGreater=GlyphFamily(1,'>'),rawHangul=GlyphFamily(1,'텍');
            Check("detached raw view draws ASCII with the code face",rawLess=="Consolas"&&rawGreater=="Consolas",
                "less="+rawLess+", greater="+rawGreater);
            Check("detached raw view keeps Hangul in the document face",rawHangul=="Malgun Gothic","hangul="+rawHangul);
        } finally {Close();}
    }
    // The detached raw view has no live session: the host gives the plain editor the composite
    // face (LiveMarkdownHost.ApplySourceFace), and the probe does the same here.
    static void OpenDetachedRaw(string text,double width) {
        editor=new SourceEditor {FontFamily=MarkdownLivePreview.SourceFace("Malgun Gothic"),FontSize=16,WordWrap=true};
        editor.Text=text;
        window=new Window {Content=editor,Width=width,Height=900,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=0};
        window.Show();Pump();
    }
    // The actual face a glyph was drawn with (font fallback resolved per character).
    static string GlyphFamily(int lineNumber,char target) {
        VisualLine visual=editor.TextArea.TextView.GetVisualLine(lineNumber);
        if (visual==null) return "(no visual line)";
        foreach (TextLine text in visual.TextLines)
            foreach (IndexedGlyphRun run in text.GetIndexedGlyphRuns()) {
                GlyphRun glyphs=run.GlyphRun;
                if (glyphs.Characters==null||glyphs.GlyphTypeface==null) continue;
                foreach (char c in glyphs.Characters) if (c==target) return PrimaryFamily(glyphs.GlyphTypeface);
            }
        return "(glyph not drawn)";
    }
    static string PrimaryFamily(GlyphTypeface face) {
        string name;
        if (face.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"),out name)) return name;
        foreach (KeyValuePair<CultureInfo,string> pair in face.FamilyNames) return pair.Value;
        return "(unnamed)";
    }
    static object HeightTreeOf(MarkdownLivePreview.Session live) {
        FieldInfo field = typeof(TextView).GetField("heightTree", BindingFlags.Instance | BindingFlags.NonPublic);
        return field == null || live == null ? null : field.GetValue(live.View);
    }
    static bool CollapsedAt(object heightTree, int number) {
        if (heightTree == null) return false;
        MethodInfo method = heightTree.GetType().GetMethod("GetIsCollapsed", BindingFlags.Instance | BindingFlags.Public);
        return method != null && (bool)method.Invoke(heightTree, new object[] { number });
    }
    static string DescribeView(string tag) {
        if (session == null) return tag + "=(no session)";
        var text = new StringBuilder(tag).Append("(caret=").Append(editor.CaretOffset).Append(")");
        object heightTree = HeightTreeOf(session);
        var document = editor.Document;
        bool valid = session.View.VisualLinesValid;
        if (!valid) text.Append(" visual-lines-invalid");
        for (int number = 1; number <= document.LineCount; number++) {
            if (!CollapsedAt(heightTree, number)) continue;
            DocumentLine line = document.GetLineByNumber(number);
            VisualLine first = null, last = null;
            if (valid) for (int index = 0; index < session.View.VisualLines.Count; index++) {
                VisualLine visual = session.View.VisualLines[index];
                if (visual.FirstDocumentLine.LineNumber <= number && number <= visual.LastDocumentLine.LineNumber) {
                    if (first == null) first = visual;
                    last = visual;
                }
            }
            text.Append(" [collapsed ").Append(number).Append(" len=").Append(line.Length)
                .Append(" visual=").Append(first == null ? (valid ? "none" : "invalid") : first.FirstDocumentLine.LineNumber + ".." + last.LastDocumentLine.LineNumber).Append(']');
        }
        return text.ToString();
    }
    // Every collapsed line must sit inside the span of the visual line built above it; a
    // collapsed line that is the first line of a visual line (or outside every span) is exactly
    // the state that throws in TextView.BuildVisualLine.
    static bool CollapsesCovered() {
        if (session == null || !session.View.VisualLinesValid) return false;
        object heightTree = HeightTreeOf(session);
        var document = editor.Document;
        for (int number = 1; number <= document.LineCount; number++) {
            if (!CollapsedAt(heightTree, number)) continue;
            bool covered = false;
            foreach (VisualLine visual in session.View.VisualLines) {
                if (visual.FirstDocumentLine.LineNumber < number && number <= visual.LastDocumentLine.LineNumber) { covered = true; break; }
            }
            if (!covered) return false;
        }
        return true;
    }
}
