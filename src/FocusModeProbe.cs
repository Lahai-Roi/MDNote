using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {
// --focusmode-probe <dir>: 보기 > 집중 모드 / 타자기 모드 (FocusMode.cs).
// a) block bounds (FocusBlocks.Find without a session, and the window's live/basic surfaces);
// b) typing inside one line recomputes nothing; toggling adds/removes the dim layer;
// c) settings round trip; d) typewriter centring after Enter x40, at the last line, and no scroll
//    for a mouse-style caret move, in the live and the basic view; e) PNGs, light and dark.
static class FocusModeProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const string Fixture="# 제목\n\n첫 문단 첫 줄\n첫 문단 둘째 줄\n\n- 항목 하나\n  이어지는 줄\n  - 하위 항목\n- 항목 둘\n\n```\ncode a\n\ncode b\n```\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n> 인용 하나\n> 인용 둘\n게으른 줄\n\n끝 문단\n";
    // caret line -> expected block (start, end)
    static readonly int[,] Expected={{1,1,1},{2,2,2},{3,3,4},{4,3,4},{6,6,8},{7,6,8},{8,8,8},{9,9,9},{12,11,15},{13,11,15},{15,11,15},{17,17,19},{18,17,19},{21,21,23},{23,21,23},{24,24,24},{25,25,25}};
    static readonly string[] CaseNames={"heading","blank","paragraph first","paragraph second","list item with child","list continuation","child item","second item","fenced code","blank inside fence","closing fence","table header","table delimiter","quote","lazy quote line","blank before last","last paragraph"};
    static StringBuilder report;static bool all;static string progressPath;
    internal static int Run(string outDir){
        string dir;try{dir=Path.GetFullPath(outDir);}catch(Exception){dir=outDir;}
        Directory.CreateDirectory(dir);
        string passPath=Path.Combine(dir,"focusmode-PASS.txt"),failPath=Path.Combine(dir,"focusmode-FAIL.txt"),textPath=Path.Combine(dir,"focusmode-probe.txt");
        RemoveFile(passPath);RemoveFile(failPath);progressPath=Path.Combine(dir,"focusmode-progress.txt");RemoveFile(progressPath);
        report=new StringBuilder();all=true;
        report.Append("# FocusModeProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o",System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var previousContext=System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        bool oldHide=(bool)typeof(MainWindow).GetField("hideMarkers",Members).GetValue(null);
        MainWindow window=null,longWindow=null,plainWindow=null;
        try{
            Progress("blocks");
            var document=new TextDocument(Fixture);
            for(int i=0;i<Expected.GetLength(0);i++){
                int start,end;FocusBlocks.Find(document,Expected[i,0],null,false,out start,out end);
                Check("a raw "+CaseNames[i]+" (line "+Expected[i,0]+")",start==Expected[i,1]&&end==Expected[i,2],"block="+start+"-"+end+" expected="+Expected[i,1]+"-"+Expected[i,2]);
            }
            Prefs(dir);
            string file=Path.Combine(dir,"focusmode-fixture.md");File.WriteAllText(file,Fixture,new UTF8Encoding(false));
            window=OpenWindow(file);
            foreach(bool basic in new[]{false,true}){
                string mode=basic?"basic":"live";SetHideMarkers(window,basic);
                Progress("window-"+mode);
                window.FocusProbeSet(true,false,false);Pump();
                SourceEditor editor=window.FocusProbeEditor;var surface=MainWindow.FocusSurfaceOf(editor);
                Check("b "+mode+" focus surface attached",surface!=null&&surface.LayerAttached,"surface="+(surface!=null));
                if(surface==null)continue;
                for(int i=0;i<Expected.GetLength(0);i++){
                    MoveCaret(editor,Expected[i,0]);
                    // The basic view folds a closing fence away, and its caret guard moves a caret
                    // set there to the next line; the block must then be that line's block.
                    int at=editor.TextArea.Caret.Line,wantStart=Expected[i,1],wantEnd=Expected[i,2];
                    if(at!=Expected[i,0])FocusBlocks.Find(editor.Document,at,null,false,out wantStart,out wantEnd);
                    Check("a "+mode+" "+CaseNames[i]+" (line "+Expected[i,0]+")",surface.BlockStart==wantStart&&surface.BlockEnd==wantEnd,"block="+surface.BlockStart+"-"+surface.BlockEnd+" caretLine="+at+(at!=Expected[i,0]?" (caret moved by the view; expected "+wantStart+"-"+wantEnd+")":""));
                }
                MoveCaret(editor,3);editor.TextArea.Caret.Offset=editor.Document.GetLineByNumber(3).EndOffset;Pump();
                int computes=surface.BlockComputes;
                for(int i=0;i<20;i++){editor.TextArea.PerformTextInput(i%2==0?"가":"a");Pump();}
                Check("b "+mode+" typing 20 chars in one line recomputes nothing",surface.BlockComputes==computes&&surface.BlockStart==3&&surface.BlockEnd==4,"extra="+(surface.BlockComputes-computes)+" block="+surface.BlockStart+"-"+surface.BlockEnd);
                window.UpdateLayout();Pump();
                Check("b "+mode+" dims the other lines",surface.DimmedRectangles>0,"rectangles="+surface.DimmedRectangles);
                window.FocusProbeSet(false,false,false);window.UpdateLayout();Pump();
                Check("b "+mode+" off leaves no overlay",!surface.LayerAttached,"layer="+surface.LayerAttached);
                window.FocusProbeSet(true,false,false);window.UpdateLayout();Pump();
                Check("b "+mode+" on again redraws",surface.LayerAttached&&surface.DimmedRectangles>0,"layer="+surface.LayerAttached+" rectangles="+surface.DimmedRectangles);
                editor.Document.Text=Fixture;Pump();
            }
            // h) 끄기 / 문단 / 줄: menu structure (light and dark), texts, 줄 mode cases, old settings.
            Progress("menu");
            MenuStructure(window,false,dir);MenuStructure(window,true,dir);Call(window,"SetDark",false);Pump();window.UpdateLayout();Pump();
            MenuLanguages(window);
            HeaderCases(window);
            Progress("line-mode");
            LineCases(window,false,dir);LineCases(window,true,dir);
            OldSettings(window,dir);
            SetHideMarkers(window,false);window.FocusProbeSetLevel(2,false,false);window.FocusProbeEditor.Document.Text=Fixture;Pump();window.FocusProbeEditor.ScrollToVerticalOffset(0);MoveCaret(window.FocusProbeEditor,4);
            Shot(window,Path.Combine(dir,"focusmode-line-light.png"));
            Call(window,"SetDark",true);Pump();window.UpdateLayout();Pump();MoveCaret(window.FocusProbeEditor,19);
            Shot(window,Path.Combine(dir,"focusmode-line-dark.png"));
            Call(window,"SetDark",false);Pump();
            window.FocusProbeSetLevel(0,false,false);
            // e) the look: caret in the list item, light and dark.
            SetHideMarkers(window,false);window.FocusProbeSet(true,false,false);MoveCaret(window.FocusProbeEditor,6);
            Shot(window,Path.Combine(dir,"focusmode-light.png"));
            Call(window,"SetDark",true);Pump();window.UpdateLayout();Pump();MoveCaret(window.FocusProbeEditor,17);
            Shot(window,Path.Combine(dir,"focusmode-dark.png"));
            Call(window,"SetDark",false);Pump();
            window.FocusProbeSet(false,false,false);

            // d) typewriter.
            var body=new StringBuilder();for(int i=1;i<=150;i++)body.Append("문단 ").Append(i).Append(" 내용입니다. 타자기 모드 시험용 줄.\n\n");
            string longFile=Path.Combine(dir,"focusmode-long.md");File.WriteAllText(longFile,body.ToString(),new UTF8Encoding(false));
            longWindow=OpenWindow(longFile);
            foreach(bool basic in new[]{false,true}){
                string mode=basic?"basic":"live";SetHideMarkers(longWindow,basic);Progress("typewriter-"+mode);
                SourceEditor editor=longWindow.FocusProbeEditor;
                editor.TextArea.Caret.Offset=editor.Document.GetLineByNumber(101).EndOffset;Pump();
                longWindow.FocusProbeSet(false,true,false);Pump();Pump();
                var surface=MainWindow.FocusSurfaceOf(editor);
                for(int i=0;i<40;i++){editor.TextArea.PerformTextInput("\n");Pump();}
                Pump();Pump();
                double error=CentreError(editor);
                Check("d "+mode+" Enter x40 keeps the caret row centred",Math.Abs(error)<=2,"error="+error.ToString("0.0")+" offset="+editor.VerticalOffset.ToString("0.0")+" line="+editor.TextArea.Caret.Line+" centres="+(surface==null?-1:surface.Centres));
                int centres=surface==null?0:surface.Centres;double offset=editor.VerticalOffset;
                for(int i=0;i<20;i++){editor.TextArea.PerformTextInput("가");Pump();}
                Check("d "+mode+" typing along one row does not scroll",surface!=null&&surface.Centres==centres&&Math.Abs(editor.VerticalOffset-offset)<=0.5,"centres+="+(surface==null?-1:surface.Centres-centres)+" offsetChange="+(editor.VerticalOffset-offset).ToString("0.0"));
                // A caret set without the keyboard (what a click does) leaves the page where it is.
                offset=editor.VerticalOffset;editor.TextArea.Caret.Offset=editor.Document.GetLineByNumber(Math.Max(1,editor.TextArea.Caret.Line-4)).Offset;Pump();Pump();
                Check("d "+mode+" mouse-style caret move does not scroll",Math.Abs(editor.VerticalOffset-offset)<=0.5,"offsetChange="+(editor.VerticalOffset-offset).ToString("0.0"));
                editor.TextArea.Caret.Offset=editor.Document.TextLength;Pump();
                editor.TextArea.PerformTextInput("끝");Pump();Pump();Pump();
                error=CentreError(editor);
                Check("d "+mode+" the last line can be centred",Math.Abs(error)<=2,"error="+error.ToString("0.0")+" offset="+editor.VerticalOffset.ToString("0.0")+" extent="+editor.ExtentHeight.ToString("0.0")+" viewport="+editor.ViewportHeight.ToString("0.0")+" bottomMargin="+editor.TextArea.TextView.Margin.Bottom.ToString("0.0"));
                double lastRowBottom=RowBottomInArea(editor);
                Check("d "+mode+" the centred last row is not clipped",lastRowBottom<=editor.TextArea.ActualHeight-editor.TextArea.TextView.Margin.Bottom+0.5,"rowBottom="+lastRowBottom.ToString("0.0")+" viewBottom="+(editor.TextArea.ActualHeight-editor.TextArea.TextView.Margin.Bottom).ToString("0.0"));
                // A tab switch away and back: the editor leaves and re-enters the tree, ApplySourceInset's
                // Loaded handler resets the end margin, and the caret row must be in the middle third again.
                Call(longWindow,"CreateTab");longWindow.UpdateLayout();Pump();Pump();
                Call(longWindow,"CycleTab",1);longWindow.UpdateLayout();Pump();Pump();Pump();
                bool sameEditor=Object.ReferenceEquals(editor,longWindow.FocusProbeEditor);
                error=sameEditor?CentreError(editor):Double.NaN;
                Check("d "+mode+" tab switch away and back keeps the caret row in the middle third",sameEditor&&Math.Abs(error)<=editor.TextArea.ActualHeight/6,"sameEditor="+sameEditor+" error="+error.ToString("0.0")+" third="+(editor.TextArea.ActualHeight/6).ToString("0.0")+" line="+editor.TextArea.Caret.Line+"/"+editor.Document.LineCount+" offset="+editor.VerticalOffset.ToString("0.0")+" bottomMargin="+editor.TextArea.TextView.Margin.Bottom.ToString("0.0"));
                TypewriterResizeAndJump(longWindow,editor,mode);
                TypewriterOutlineAndFind(longWindow,editor,mode);
                longWindow.FocusProbeSet(false,false,false);Pump();Pump();
                Check("d "+mode+" off restores the plain end margin",editor.TextArea.TextView.Margin.Bottom<=24.1,"bottomMargin="+editor.TextArea.TextView.Margin.Bottom.ToString("0.0"));
                Progress("gutter-clip-"+mode);GutterClipAll(longWindow,editor,mode,dir,true);
                editor.Document.Text=body.ToString();Pump();
            }
            // g) the same in the plain-text view (a .txt file has no live or basic surface).
            Progress("gutter-clip-text");
            string plainFile=Path.Combine(dir,"focusmode-plain.txt");File.WriteAllText(plainFile,body.ToString(),new UTF8Encoding(false));
            plainWindow=OpenWindow(plainFile);
            GutterClipAll(plainWindow,plainWindow.FocusProbeEditor,"text",dir,true);
        }catch(Exception ex){Check("exception",false,ex.ToString());}
        finally{
            foreach(MainWindow open in new[]{window,longWindow,plainWindow})if(open!=null){try{Discard(open);open.Close();}catch(Exception ex){Check("close",false,ex.Message);}}
            typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,oldHide);
            System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        report.Append('\n').Append(all?"RESULT: PASS":"RESULT: FAIL").Append('\n');
        File.WriteAllText(textPath,report.ToString(),new UTF8Encoding(false));
        File.WriteAllText(all?passPath:failPath,report.ToString(),new UTF8Encoding(false));
        Progress("done");
        return all?0:1;
    }
    // Typewriter cases that follow the caret row after something other than a key: a window resize
    // (the middle moved) and a "#heading" link jump (a programmatic caret move), then a mouse-style
    // caret move that must still leave the page alone.
    static void TypewriterResizeAndJump(MainWindow window,SourceEditor editor,string mode){
        // a) the caret sits on the last line, centred; the window gets shorter, then taller than before.
        editor.TextArea.Caret.Offset=editor.Document.TextLength;editor.TextArea.PerformTextInput("끝");Pump();Pump();Pump();
        double height=window.Height,areaBefore=editor.TextArea.ActualHeight;
        window.Height=height-250;window.UpdateLayout();Pump();Pump();Pump();
        double areaSmall=editor.TextArea.ActualHeight,errorSmall=CentreError(editor);
        Check("d "+mode+" resize smaller keeps the caret row in the middle third",areaBefore-areaSmall>=100&&Math.Abs(errorSmall)<=areaSmall/6,"area="+areaBefore.ToString("0")+"->"+areaSmall.ToString("0")+" error="+errorSmall.ToString("0.0")+" third="+(areaSmall/6).ToString("0.0")+" offset="+editor.VerticalOffset.ToString("0.0"));
        window.Height=height+150;window.UpdateLayout();Pump();Pump();Pump();
        double areaLarge=editor.TextArea.ActualHeight,errorLarge=CentreError(editor);
        Check("d "+mode+" resize taller keeps the caret row in the middle third",areaLarge-areaSmall>=100&&Math.Abs(errorLarge)<=areaLarge/6,"area="+areaSmall.ToString("0")+"->"+areaLarge.ToString("0")+" error="+errorLarge.ToString("0.0")+" third="+(areaLarge/6).ToString("0.0")+" offset="+editor.VerticalOffset.ToString("0.0"));
        window.Height=height;window.UpdateLayout();Pump();Pump();Pump();
        // b) a heading far below the top: the "#heading" link jump (OpenLiveAnchor -> JumpLiveAnchor).
        var text=new StringBuilder();
        for(int i=1;i<=150;i++){if(i==120)text.Append("# 목표 제목\n\n");text.Append("문단 ").Append(i).Append(" 내용입니다. 타자기 모드 시험용 줄.\n\n");}
        editor.Document.Text=text.ToString();Pump();Pump();
        editor.TextArea.Caret.Offset=0;Pump();Pump();editor.ScrollToVerticalOffset(0);Pump();Pump();
        Call(window,"OpenLiveAnchor","#목표-제목");Pump();Pump();Pump();Pump();
        string lineText=editor.Document.GetText(editor.Document.GetLineByNumber(editor.TextArea.Caret.Line));
        double errorJump=CentreError(editor);
        Check("d "+mode+" #heading jump lands on the heading",lineText.StartsWith("# 목표 제목"),"line="+editor.TextArea.Caret.Line+" text="+lineText);
        Check("d "+mode+" #heading jump puts the heading row in the middle third",Math.Abs(errorJump)<=editor.TextArea.ActualHeight/6,"error="+errorJump.ToString("0.0")+" third="+(editor.TextArea.ActualHeight/6).ToString("0.0")+" offset="+editor.VerticalOffset.ToString("0.0")+" extent="+editor.ExtentHeight.ToString("0.0"));
        // c) a plain caret move (what a click does) after both still leaves the page where it is.
        double offset=editor.VerticalOffset;
        editor.TextArea.Caret.Offset=editor.Document.GetLineByNumber(Math.Max(1,editor.TextArea.Caret.Line-6)).Offset;Pump();Pump();Pump();
        Check("d "+mode+" mouse-style caret move after the jump does not scroll",Math.Abs(editor.VerticalOffset-offset)<=0.5,"offsetChange="+(editor.VerticalOffset-offset).ToString("0.0"));
    }
    // Two more programmatic jumps: the outline panel (SelectOutlineOffset) and Find next (SelectMatch)
    // centre the target row with typewriter mode on; with it off Find next centres nothing. The
    // document is the one TypewriterResizeAndJump left in the editor (150 paragraphs, a heading at 120).
    static void TypewriterOutlineAndFind(MainWindow window,SourceEditor editor,string mode){
        var surface=MainWindow.FocusSurfaceOf(editor);
        string full=editor.Document.Text;
        // a) outline jump to the heading far below.
        int heading=full.IndexOf("# 목표 제목",StringComparison.Ordinal);
        editor.TextArea.Caret.Offset=0;Pump();Pump();editor.ScrollToVerticalOffset(0);Pump();Pump();
        Call(window,"SelectOutlineOffset",heading);Pump();Pump();Pump();Pump();
        string lineText=editor.Document.GetText(editor.Document.GetLineByNumber(editor.TextArea.Caret.Line));
        double errorOutline=CentreError(editor);
        Check("d "+mode+" outline jump lands on the heading",lineText.StartsWith("# 목표 제목"),"line="+editor.TextArea.Caret.Line+" text="+lineText);
        Check("d "+mode+" outline jump puts the heading row in the middle third",Math.Abs(errorOutline)<=editor.TextArea.ActualHeight/6,"error="+errorOutline.ToString("0.0")+" third="+(editor.TextArea.ActualHeight/6).ToString("0.0")+" offset="+editor.VerticalOffset.ToString("0.0"));
        // b) Find next to a match far below.
        var query=(TextBox)typeof(MainWindow).GetField("query",Members).GetValue(window);
        Call(window,"ShowSearch");Pump();Pump();
        query.Text="문단 140 내용";Pump();Pump();Pump();
        int match=full.IndexOf("문단 140 내용",StringComparison.Ordinal);
        editor.Select(0,0);Pump();Pump();editor.ScrollToVerticalOffset(0);Pump();Pump();
        Call(window,"FindNext",false);Pump();Pump();Pump();Pump();
        double errorFind=CentreError(editor);
        Check("d "+mode+" find next selects the match far below",editor.SelectionStart==match&&editor.SelectionLength=="문단 140 내용".Length,"selection="+editor.SelectionStart+"+"+editor.SelectionLength+" expected="+match);
        Check("d "+mode+" find next puts the match row in the middle third",Math.Abs(errorFind)<=editor.TextArea.ActualHeight/6,"error="+errorFind.ToString("0.0")+" third="+(editor.TextArea.ActualHeight/6).ToString("0.0")+" offset="+editor.VerticalOffset.ToString("0.0"));
        // c) typewriter off: Find next to a match that is already on screen centres nothing.
        window.FocusProbeSet(false,false,false);Pump();Pump();
        int centres=surface==null?0:surface.Centres;
        query.Text="문단 3 내용";Pump();Pump();Pump();
        int near=full.IndexOf("문단 3 내용",StringComparison.Ordinal);
        editor.Select(0,0);Pump();Pump();editor.ScrollToVerticalOffset(0);Pump();Pump();
        double offsetBefore=editor.VerticalOffset;
        Call(window,"FindNext",false);Pump();Pump();Pump();Pump();
        Check("d "+mode+" typewriter off: find next selects the near match and centres nothing",editor.SelectionStart==near&&surface!=null&&surface.Centres==centres&&Math.Abs(editor.VerticalOffset-offsetBefore)<=0.5,"selection="+editor.SelectionStart+" expected="+near+" centres+="+(surface==null?-1:surface.Centres-centres)+" offsetChange="+(editor.VerticalOffset-offsetBefore).ToString("0.0"));
        Call(window,"CloseSearch");Pump();Pump();
        window.FocusProbeSet(false,true,false);Pump();Pump();
    }
    // The line-number gutter and the text are cut at the same rows. A half-scrolled line at the top (or
    // bottom) of the editor loses its text at the TextView's own edge (editor border, ApplySourceInset's
    // top margin, the end or typewriter margin); the numbers used to be drawn over the whole area, so a
    // number poked into the border band where its text was already gone. Read from the rendered window:
    // the rows between the area's edge and the TextView's edge must hold nothing in the gutter column
    // and nothing in the text column, while the rows just inside the edge do hold numbers.
    static void GutterClipAll(MainWindow window,SourceEditor editor,string mode,string dir,bool shots){
        foreach(bool typewriter in new[]{false,true})foreach(bool dark in new[]{false,true}){
            window.FocusProbeSet(false,typewriter,false);Call(window,"SetDark",dark);Pump();window.UpdateLayout();Pump();Pump();
            GutterClip(window,editor,"g "+mode+(typewriter?" typewriter":"")+(dark?" dark":" light"),shots&&!typewriter?Path.Combine(dir,"focusmode-clip-"+mode+(dark?"-dark":"-light")+".png"):null);
        }
        Call(window,"SetDark",false);window.FocusProbeSet(false,false,false);Pump();window.UpdateLayout();Pump();
    }
    static void GutterClip(MainWindow window,SourceEditor editor,string tag,string shotPath){
        var gutterField=typeof(MainWindow).GetField("textGutter",Members);
        var gutter=gutterField==null?null:gutterField.GetValue(window) as TextGutter;
        var root=window.Content as FrameworkElement;var view=editor.TextArea.TextView;
        if(gutter==null||root==null||view==null){Check(tag+" gutter and text view found",false,"gutter="+(gutter!=null));return;}
        double[] offsets={211.5,333.25,Double.NaN};
        for(int step=0;step<offsets.Length;step++){
            double target=Double.IsNaN(offsets[step])?Math.Max(0,editor.ExtentHeight-editor.ViewportHeight-7.5):offsets[step];
            editor.ScrollToVerticalOffset(target);window.UpdateLayout();Pump();Pump();
            if(shotPath!=null&&step==0)Shot(window,shotPath);
            int width=(int)Math.Ceiling(root.ActualWidth),height=(int)Math.Ceiling(root.ActualHeight);
            var bitmap=new RenderTargetBitmap(Math.Max(1,width),Math.Max(1,height),96,96,PixelFormats.Pbgra32);bitmap.Render(root);
            byte[] pixels=new byte[width*height*4];bitmap.CopyPixels(pixels,width*4,0);
            Point gutterOrigin=gutter.TranslatePoint(new Point(0,0),root);
            double areaTop=gutterOrigin.Y,areaBottom=gutterOrigin.Y+gutter.ActualHeight,gutterWidth=gutter.ActualWidth;
            double viewTop=view.TranslatePoint(new Point(0,0),root).Y,viewBottom=view.TranslatePoint(new Point(0,view.ActualHeight),root).Y;
            // one row of tolerance at the edge itself (anti-aliasing of a fractional edge)
            int topFrom=(int)Math.Ceiling(areaTop),topTo=(int)Math.Floor(viewTop)-1;
            int bottomFrom=(int)Math.Ceiling(viewBottom)+1,bottomTo=Math.Min(height,(int)Math.Floor(areaBottom));
            int gutterTop=BandInk(pixels,width,height,topFrom,topTo,2,(int)gutterWidth-3);
            int textTop=BandInk(pixels,width,height,topFrom,topTo,(int)gutterWidth+2,width-24);
            int gutterBottom=BandInk(pixels,width,height,bottomFrom,bottomTo,2,(int)gutterWidth-3);
            int textBottom=BandInk(pixels,width,height,bottomFrom,bottomTo,(int)gutterWidth+2,width-24);
            int inside=BandInk(pixels,width,height,(int)Math.Ceiling(viewTop),(int)Math.Ceiling(viewTop)+60,2,(int)gutterWidth-3);
            string detail="offset="+editor.VerticalOffset.ToString("0.0")+" areaTop="+areaTop.ToString("0.0")+" viewTop="+viewTop.ToString("0.0")+" viewBottom="+viewBottom.ToString("0.0")+" areaBottom="+areaBottom.ToString("0.0")+" topBand(gutter/text)="+gutterTop+"/"+textTop+" bottomBand(gutter/text)="+gutterBottom+"/"+textBottom+" numbersInside="+inside;
            Check(tag+" step"+step+" top band holds no number and no text",gutterTop==0&&textTop==0,detail);
            Check(tag+" step"+step+" bottom band holds no number and no text",gutterBottom==0&&textBottom==0,detail);
            Check(tag+" step"+step+" numbers are still drawn inside the text area",inside>0,detail);
        }
        editor.ScrollToVerticalOffset(0);Pump();
    }
    // Pixels in rows [from,to) and columns [left,right) that differ from the band's own first pixel.
    static int BandInk(byte[] pixels,int width,int height,int from,int to,int left,int right){
        from=Math.Max(0,from);to=Math.Min(height,to);left=Math.Max(0,left);right=Math.Min(width,right);
        if(to-from<=0||right-left<=0)return 0;
        int reference=(from*width+left)*4,count=0;
        for(int y=from;y<to;y++)for(int x=left;x<right;x++){
            int at=(y*width+x)*4;
            int diff=Math.Abs(pixels[at]-pixels[reference])+Math.Abs(pixels[at+1]-pixels[reference+1])+Math.Abs(pixels[at+2]-pixels[reference+2]);
            if(diff>30)count++;
        }
        return count;
    }
    // ---- 집중 모드 > 끄기 / 문단 / 줄 (user decision 2026-11-05) ----
    static readonly string[][] ChoiceNames={new[]{"끄기","문단","줄"},new[]{"Off","Paragraph","Line"},new[]{"オフ","段落","行"},new[]{"关闭","段落","行"},new[]{"關閉","段落","行"}};
    static readonly string[] ParentNames={"집중 모드","Focus mode","フォーカスモード","专注模式","專注模式"};
    static readonly string[] LanguageCodes={"ko","en","ja","zh-CN","zh-TW"};
    static void Invoke(MenuItem item){
        // MenuItem.OnClick is what a mouse click or Enter reaches: it toggles a checkable item and raises Click.
        var method=typeof(MenuItem).GetMethod("OnClick",BindingFlags.Instance|BindingFlags.NonPublic);
        if(method==null)throw new InvalidOperationException("no OnClick");
        method.Invoke(item,null);Pump();
    }
    static int CheckedCount(MenuItem[] items){int n=0;foreach(MenuItem item in items)if(item.IsChecked)n++;return n;}
    static FrameworkElement FindNamed(DependencyObject root,string name){
        if(root==null)return null;var element=root as FrameworkElement;if(element!=null&&element.Name==name)return element;
        int count=VisualTreeHelper.GetChildrenCount(root);
        for(int i=0;i<count;i++){var found=FindNamed(VisualTreeHelper.GetChild(root,i),name);if(found!=null)return found;}
        return null;
    }
    static void ShotVisual(FrameworkElement element,string path){
        int width=(int)Math.Ceiling(element.ActualWidth),height=(int)Math.Ceiling(element.ActualHeight);
        if(width<=0||height<=0){Check("shot "+Path.GetFileName(path),false,"empty "+width+"x"+height);return;}
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(element);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(path))encoder.Save(stream);
        report.Append("shot ").Append(Path.GetFileName(path)).Append(' ').Append(width).Append('x').Append(height).Append('\n');
    }
    // Menu structure in the current theme: a parent with three choices, exactly one checked, the parent
    // checked iff the level is not 끄기, the check glyph drawn on the parent, radio behaviour on click.
    static void MenuStructure(MainWindow window,bool dark,string dir){
        string tag="m "+(dark?"dark":"light");
        Call(window,"SetDark",dark);Pump();window.UpdateLayout();Pump();Pump();
        MenuItem parent=window.FocusProbeParent;MenuItem[] choices=window.FocusProbeChoices;
        Check(tag+" parent holds three choices",parent!=null&&parent.Items.Count==3&&choices[0]!=null&&choices[1]!=null&&choices[2]!=null,"items="+(parent==null?-1:parent.Items.Count));
        if(parent==null)return;
        Check(tag+" parent is a menu item with a submenu and a text header",parent.HasItems&&((string)parent.Header=="집중 모드"),"header="+parent.Header);
        for(int level=0;level<3;level++){
            window.FocusProbeSetLevel(level,false,false);Pump();
            Check(tag+" level "+level+": exactly one choice checked ("+level+") and parent checked iff not off",CheckedCount(choices)==1&&choices[level].IsChecked&&parent.IsChecked==(level!=0),"checked="+choices[0].IsChecked+"/"+choices[1].IsChecked+"/"+choices[2].IsChecked+" parent="+parent.IsChecked);
        }
        // Clicking behaves like radio buttons: the same choice twice stays selected.
        Invoke(choices[2]);Check(tag+" click 줄 selects it",window.FocusModeLevel==2&&choices[2].IsChecked&&CheckedCount(choices)==1&&parent.IsChecked,"level="+window.FocusModeLevel);
        Invoke(choices[2]);Check(tag+" click 줄 again keeps it selected",window.FocusModeLevel==2&&choices[2].IsChecked&&CheckedCount(choices)==1&&parent.IsChecked,"level="+window.FocusModeLevel+" checked="+choices[2].IsChecked);
        Invoke(choices[1]);Check(tag+" click 문단 moves the selection",window.FocusModeLevel==1&&choices[1].IsChecked&&!choices[2].IsChecked&&CheckedCount(choices)==1,"level="+window.FocusModeLevel);
        Invoke(choices[0]);Check(tag+" click 끄기 unchecks the parent",window.FocusModeLevel==0&&choices[0].IsChecked&&!parent.IsChecked&&CheckedCount(choices)==1,"level="+window.FocusModeLevel+" parent="+parent.IsChecked);
        Invoke(choices[0]);Check(tag+" click 끄기 again keeps it selected",window.FocusModeLevel==0&&choices[0].IsChecked&&!parent.IsChecked,"checked="+choices[0].IsChecked);
        // The check glyph on the rows. A real popup cannot be held open on the hidden desktop (the bar item
        // refuses IsSubmenuOpen without menu mode), so the rows' own templates are applied and read: the
        // glyph element (light: Aero2 "GlyphPanel", dark: "ItemCheck") must be Visible exactly while the
        // item is checked, on the parent row (Role SubmenuHeader) as well as on the choices.
        string glyphName=dark?"ItemCheck":"GlyphPanel";
        foreach(int level in new[]{2,1,0}){
            window.FocusProbeSetLevel(level,false,false);Pump();
            parent.ApplyTemplate();foreach(MenuItem choice in choices)choice.ApplyTemplate();
            FrameworkElement glyph=FindNamed(parent,glyphName);
            bool shown=glyph!=null&&glyph.Visibility==Visibility.Visible;
            Check(tag+" level "+level+": check glyph on the parent row is "+(level!=0?"drawn":"hidden")+" ("+glyphName+")",glyph!=null&&shown==(level!=0),"glyph="+(glyph!=null)+" visibility="+(glyph==null?"-":glyph.Visibility.ToString())+" parentRole="+parent.Role+" template="+(parent.Template==null?"null":parent.Template.GetType().Name));
            bool choicesOk=true;string states="";
            for(int c=0;c<3;c++){
                FrameworkElement g=FindNamed(choices[c],glyphName);bool on=g!=null&&g.Visibility==Visibility.Visible;
                states+=(g==null?"?":on?"1":"0");if(g==null||on!=(c==level))choicesOk=false;
            }
            Check(tag+" level "+level+": only the selected choice draws a check",choicesOk,"glyphs="+states+" choiceRole="+choices[0].Role);
            if(level==2){
                ShotItems(new MenuItem[]{parent},dark,Path.Combine(dir,"focusmode-menu-"+(dark?"dark":"light")+".png"));
                ShotItems(choices,dark,Path.Combine(dir,"focusmode-menusub-"+(dark?"dark":"light")+".png"));
            }
        }
        window.FocusProbeSetLevel(0,false,false);Pump();
    }
    // The rows laid out one above the other on the menu's own background colour (the rows are detached
    // from a popup, so each is measured, arranged at the origin and painted through a VisualBrush).
    static void ShotItems(MenuItem[] items,bool dark,string path){
        double width=230,height=0;
        foreach(MenuItem item in items){item.ApplyTemplate();item.Measure(new Size(width,Double.PositiveInfinity));width=Math.Max(width,item.DesiredSize.Width);}
        var heights=new double[items.Length];
        for(int i=0;i<items.Length;i++){items[i].Measure(new Size(width,Double.PositiveInfinity));heights[i]=Math.Max(22,items[i].DesiredSize.Height);height+=heights[i];}
        int w=(int)Math.Ceiling(width)+16,h=(int)Math.Ceiling(height)+16;
        var visual=new DrawingVisual();
        using(DrawingContext dc=visual.RenderOpen()){
            dc.DrawRectangle(new SolidColorBrush(dark?Color.FromRgb(0x2B,0x2B,0x2B):Color.FromRgb(0xF2,0xF2,0xF2)),null,new Rect(0,0,w,h));
            double y=8;
            for(int i=0;i<items.Length;i++){
                items[i].Arrange(new Rect(0,0,width,heights[i]));items[i].UpdateLayout();
                dc.PushTransform(new TranslateTransform(8,y));
                dc.DrawRectangle(new VisualBrush(items[i]){Stretch=Stretch.None,AlignmentX=AlignmentX.Left,AlignmentY=AlignmentY.Top},null,new Rect(0,0,width,heights[i]));
                dc.Pop();y+=heights[i];
            }
        }
        var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(path))encoder.Save(stream);
        report.Append("shot ").Append(Path.GetFileName(path)).Append(' ').Append(w).Append('x').Append(h).Append('\n');
    }
    static void MenuLanguages(MainWindow window){
        var field=typeof(MainWindow).GetField("language",Members);
        MenuItem parent=window.FocusProbeParent;MenuItem[] choices=window.FocusProbeChoices;
        for(int i=0;i<LanguageCodes.Length;i++){
            field.SetValue(window,LanguageCodes[i]);Call(window,"ApplyLanguage");Pump();
            bool ok=(string)parent.Header==ParentNames[i];
            for(int c=0;c<3;c++)ok&=(string)choices[c].Header==ChoiceNames[i][c];
            Check("n menu text in "+LanguageCodes[i],ok,"parent="+parent.Header+" choices="+choices[0].Header+"/"+choices[1].Header+"/"+choices[2].Header);
        }
        field.SetValue(window,"ko");Call(window,"ApplyLanguage");Pump();
        Check("n menu text back in Korean",(string)choices[0].Header=="끄기"&&(string)choices[1].Header=="문단"&&(string)choices[2].Header=="줄","choices="+choices[0].Header+"/"+choices[1].Header+"/"+choices[2].Header);
    }
    // i) 2026-11-06 user: a click on the "집중 모드" row itself toggles focus mode, turning it on at the
    // level last chosen in the submenu (문단 until 줄 is chosen). The accessor goes through the same path
    // as the real click (the Invoke cases above do the same); the probe runs as a self test, so the
    // settings file is never written. The initial level 1 then 0 fixes the last level the case starts on.
    static void HeaderCases(MainWindow window){
        window.FocusProbeSetLevel(1,false,false);window.FocusProbeSetLevel(0,false,false);Pump();
        window.FocusProbeToggleHeader();Pump();
        Check("i header toggle from off turns 문단 on",window.FocusModeLevel==1&&window.FocusProbeLastLevel==1&&CheckedCount(window.FocusProbeChoices)==1&&window.FocusProbeChoices[1].IsChecked&&window.FocusProbeParent.IsChecked,"level="+window.FocusModeLevel+" last="+window.FocusProbeLastLevel+" checked="+window.FocusProbeChoices[0].IsChecked+"/"+window.FocusProbeChoices[1].IsChecked+"/"+window.FocusProbeChoices[2].IsChecked+" parent="+window.FocusProbeParent.IsChecked);
        window.FocusProbeToggleHeader();Pump();
        Check("i header toggle while on turns focus mode off",window.FocusModeLevel==0&&window.FocusProbeLastLevel==1&&CheckedCount(window.FocusProbeChoices)==1&&window.FocusProbeChoices[0].IsChecked&&!window.FocusProbeParent.IsChecked,"level="+window.FocusModeLevel+" last="+window.FocusProbeLastLevel+" checked="+window.FocusProbeChoices[0].IsChecked+"/"+window.FocusProbeChoices[1].IsChecked+"/"+window.FocusProbeChoices[2].IsChecked+" parent="+window.FocusProbeParent.IsChecked);
        window.FocusProbeSetLevel(2,false,false);Pump();
        window.FocusProbeToggleHeader();Pump();
        Check("i header toggle keeps the chosen level (줄) while turning off",window.FocusModeLevel==0&&window.FocusProbeLastLevel==2&&window.FocusProbeChoices[0].IsChecked&&!window.FocusProbeParent.IsChecked,"level="+window.FocusModeLevel+" last="+window.FocusProbeLastLevel+" parent="+window.FocusProbeParent.IsChecked);
        window.FocusProbeToggleHeader();Pump();
        Check("i header toggle picks the last level (줄) up again",window.FocusModeLevel==2&&window.FocusProbeLastLevel==2&&CheckedCount(window.FocusProbeChoices)==1&&window.FocusProbeChoices[2].IsChecked&&window.FocusProbeParent.IsChecked,"level="+window.FocusModeLevel+" last="+window.FocusProbeLastLevel+" checked="+window.FocusProbeChoices[0].IsChecked+"/"+window.FocusProbeChoices[1].IsChecked+"/"+window.FocusProbeChoices[2].IsChecked+" parent="+window.FocusProbeParent.IsChecked);
        window.FocusProbeSetLevel(0,false,false);Pump();
    }
    // contrast (max-min brightness) of the rows between two y values of the rendered window
    static int RowSpread(byte[] pixels,int width,int height,double top,double bottom,int left,int right){
        int y0=Math.Max(0,(int)Math.Ceiling(top)+1),y1=Math.Min(height,(int)Math.Floor(bottom)-1);left=Math.Max(0,left);right=Math.Min(width,right);
        int min=255,max=0;
        for(int y=y0;y<y1;y++)for(int x=left;x<right;x++){int at=(y*width+x)*4;int luma=(pixels[at]+pixels[at+1]+pixels[at+2])/3;if(luma<min)min=luma;if(luma>max)max=luma;}
        return max>=min?max-min:0;
    }
    static byte[] Capture(MainWindow window,out int width,out int height){
        window.UpdateLayout();Pump();Pump();
        var root=(FrameworkElement)window.Content;width=(int)Math.Ceiling(root.ActualWidth);height=(int)Math.Ceiling(root.ActualHeight);
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        byte[] pixels=new byte[width*height*4];bitmap.CopyPixels(pixels,width*4,0);return pixels;
    }
    // The spread of one text row: the row that holds (line,column).
    static int SpreadOfRow(MainWindow window,SourceEditor editor,byte[] pixels,int width,int height,int line,int column){
        var view=editor.TextArea.TextView;var root=(FrameworkElement)window.Content;
        double top=view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(line,column),VisualYPosition.LineTop).Y-view.ScrollOffset.Y,bottom=view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(line,column),VisualYPosition.LineBottom).Y-view.ScrollOffset.Y;
        Point a=view.TranslatePoint(new Point(0,top),root),b=view.TranslatePoint(new Point(0,bottom),root);
        return RowSpread(pixels,width,height,a.Y,b.Y,(int)a.X,(int)(a.X+view.ActualWidth));
    }
    static string KeptList(MainWindow.FocusSurface surface,int count){var text=new StringBuilder();for(int n=1;n<=count;n++)if(surface.Keeps(n,n))text.Append(n).Append(' ');return text.ToString().Trim();}
    static void LineCases(MainWindow window,bool basic,string dir){
        string mode=basic?"basic":"live";
        SetHideMarkers(window,basic);SourceEditor editor=window.FocusProbeEditor;editor.Document.Text=Fixture;Pump();editor.ScrollToVerticalOffset(0);Pump();
        window.FocusProbeSetLevel(2,false,false);Pump();window.UpdateLayout();Pump();
        var surface=MainWindow.FocusSurfaceOf(editor);
        Check("l "+mode+" level 2 attached",surface!=null&&surface.LayerAttached&&surface.Level==2,"level="+(surface==null?-1:surface.Level));
        if(surface==null)return;
        int total=editor.Document.LineCount;
        int[] carets={3,4,7,8,12,13,14,18,19,22,23,25};
        string[] names={"paragraph line 1","paragraph line 2","list continuation","child item","code line","blank code line","code line 2","table delimiter","table row","quote line 2","lazy quote line","last paragraph"};
        for(int i=0;i<carets.Length;i++){
            MoveCaret(editor,carets[i]);int at=editor.TextArea.Caret.Line;
            string kept=KeptList(surface,total);
            Check("l "+mode+" "+names[i]+" (line "+carets[i]+"): only the caret line stays bright",kept==at.ToString()&&surface.BlockStart==at&&surface.BlockEnd==at,"caretLine="+at+" kept=["+kept+"] block="+surface.BlockStart+"-"+surface.BlockEnd);
        }
        // The same paragraph in 문단 mode keeps both lines, in 줄 mode only the caret's.
        MoveCaret(editor,3);window.FocusProbeSetLevel(1,false,false);Pump();Pump();
        string paragraphKept=KeptList(surface,total);
        window.FocusProbeSetLevel(2,false,false);Pump();Pump();
        string lineKept=KeptList(surface,total);
        Check("l "+mode+" switching 문단 -> 줄 narrows 3-4 to 3",paragraphKept=="3 4"&&lineKept=="3","paragraph=["+paragraphKept+"] line=["+lineKept+"]");
        // Pixels: line 3 is drawn at full contrast, line 4 (same paragraph) is dimmed in 줄 mode only.
        int width,height;byte[] pixels=Capture(window,out width,out height);
        int s3=SpreadOfRow(window,editor,pixels,width,height,3,1),s4=SpreadOfRow(window,editor,pixels,width,height,4,1);
        Check("l "+mode+" pixels: the caret line is clearly stronger than the next line of its paragraph (줄)",s3>=40&&s3*10>=s4*16,"spread line3="+s3+" line4="+s4);
        window.FocusProbeSetLevel(1,false,false);Pump();Pump();pixels=Capture(window,out width,out height);
        s3=SpreadOfRow(window,editor,pixels,width,height,3,1);s4=SpreadOfRow(window,editor,pixels,width,height,4,1);
        Check("l "+mode+" pixels: both paragraph lines are equally strong (문단)",s3>=40&&Math.Abs(s3-s4)<=Math.Max(12,s3/5),"spread line3="+s3+" line4="+s4);
        window.FocusProbeSetLevel(2,false,false);Pump();Pump();
        // Selection over two lines keeps both; a selection over a table keeps its lines.
        editor.Select(editor.Document.GetLineByNumber(3).Offset,editor.Document.GetLineByNumber(4).EndOffset-editor.Document.GetLineByNumber(3).Offset);Pump();Pump();
        string selKept=KeptList(surface,total);
        Check("l "+mode+" a selection over lines 3-4 keeps both lines and nothing else",selKept=="3 4","kept=["+selKept+"]");
        editor.Select(editor.Document.GetLineByNumber(17).Offset,editor.Document.GetLineByNumber(18).EndOffset-editor.Document.GetLineByNumber(17).Offset);Pump();Pump();
        selKept=KeptList(surface,total);
        Check("l "+mode+" a selection over table lines 17-18 keeps those two lines",selKept.Contains("17")&&selKept.Contains("18")&&!selKept.Contains("19")&&!selKept.Contains("3"),"kept=["+selKept+"]");
        editor.Select(0,0);MoveCaret(editor,3);
        // Typing inside the line recomputes nothing; Enter moves the bright line with the caret.
        editor.TextArea.Caret.Offset=editor.Document.GetLineByNumber(3).EndOffset;Pump();
        int computes=surface.BlockComputes;
        for(int i=0;i<10;i++){editor.TextArea.PerformTextInput(i%2==0?"가":"a");Pump();}
        Check("l "+mode+" typing 10 chars in the line recomputes nothing",surface.BlockComputes==computes&&KeptList(surface,editor.Document.LineCount)=="3","extra="+(surface.BlockComputes-computes)+" kept=["+KeptList(surface,editor.Document.LineCount)+"]");
        editor.TextArea.PerformTextInput("\n");Pump();Pump();
        Check("l "+mode+" Enter moves the bright line to the new line",KeptList(surface,editor.Document.LineCount)==editor.TextArea.Caret.Line.ToString()&&editor.TextArea.Caret.Line==4,"kept=["+KeptList(surface,editor.Document.LineCount)+"] caret="+editor.TextArea.Caret.Line);
        // Switching modes leaves nothing behind.
        editor.Document.Text=Fixture;Pump();MoveCaret(editor,3);total=editor.Document.LineCount;
        window.FocusProbeSetLevel(0,false,false);window.UpdateLayout();Pump();
        Check("l "+mode+" 끄기: no layer, nothing dimmed",!surface.LayerAttached&&surface.BlockStart==0&&KeptList(surface,total).Split(' ').Length==total,"layer="+surface.LayerAttached+" block="+surface.BlockStart+" kept="+KeptList(surface,total));
        window.FocusProbeSetLevel(2,false,false);window.UpdateLayout();Pump();Pump();
        Check("l "+mode+" 끄기 -> 줄 dims again with the caret line only",surface.LayerAttached&&surface.DimmedRectangles>0&&KeptList(surface,total)=="3","layer="+surface.LayerAttached+" rects="+surface.DimmedRectangles+" kept=["+KeptList(surface,total)+"]");
        window.FocusProbeSetLevel(1,false,false);window.UpdateLayout();Pump();Pump();
        Check("l "+mode+" 줄 -> 문단 widens to the block",KeptList(surface,total)=="3 4","kept=["+KeptList(surface,total)+"]");
        window.FocusProbeSetLevel(2,false,false);Pump();Pump();
        // A long line that wraps over several rows stays fully bright.
        var wrapped=new StringBuilder();for(int i=0;i<90;i++)wrapped.Append("가나다라 마바사 ");
        editor.Document.Text="짧은 첫 줄\n"+wrapped+"\n짧은 끝 줄\n";Pump();Pump();editor.ScrollToVerticalOffset(0);Pump();
        MoveCaret(editor,2);window.UpdateLayout();Pump();Pump();
        VisualLine visual=editor.TextArea.TextView.GetVisualLine(2);
        int rows=visual==null?0:visual.TextLines.Count;
        string wrapKept=KeptList(surface,3);
        Check("l "+mode+" the wrapped line spans several rows and stays one bright unit",rows>=3&&wrapKept=="2","rows="+rows+" kept=["+wrapKept+"]");
        pixels=Capture(window,out width,out height);
        int lineLength=editor.Document.GetLineByNumber(2).Length;
        int first=SpreadOfRow(window,editor,pixels,width,height,2,1),last=SpreadOfRow(window,editor,pixels,width,height,2,lineLength+1),dimmed=SpreadOfRow(window,editor,pixels,width,height,1,1);
        Check("l "+mode+" pixels: first and last row of the wrapped line are both bright, the lines around are dim",first>=40&&last>=40&&first*10>=dimmed*16&&last*10>=dimmed*16,"first="+first+" last="+last+" dimmed="+dimmed);
        editor.Document.Text=Fixture;Pump();
    }
    // An old settings file (focusMode="true") applied to a window comes up as 문단.
    static void OldSettings(MainWindow window,string dir){
        string path=Path.Combine(dir,"focusmode-old-settings.xml");
        File.WriteAllText(path,new ViewPreferences().Serialize().Replace("<MDNote ","<MDNote focusMode=\"true\" "),new UTF8Encoding(false));
        window.FocusProbeSetLevel(0,false,false);Pump();
        window.FocusProbeApplyPreferences(ViewPreferences.Load(path));Pump();
        Check("o old focusMode=\"true\" file applied to a window shows 문단",window.FocusModeLevel==1&&window.FocusProbeChoices[1].IsChecked&&window.FocusProbeParent.IsChecked,"level="+window.FocusModeLevel);
        RemoveFile(path);window.FocusProbeSetLevel(0,false,false);Pump();
    }
    static void Prefs(string dir){
        string path=Path.Combine(dir,"focusmode-settings.xml");
        var on=new ViewPreferences {FocusMode=1,TypewriterMode=true};ViewPreferences.Save(path,on.Serialize());
        var back=ViewPreferences.Load(path);
        Check("c settings round trip (paragraph)",back.FocusMode==1&&back.TypewriterMode,"focus="+back.FocusMode+" typewriter="+back.TypewriterMode);
        Check("c settings store a word, not true",on.Serialize().Contains("focusMode=\"paragraph\"")&&!on.Serialize().Contains("focusMode=\"true\""),on.Serialize());
        var line=new ViewPreferences {FocusMode=2};ViewPreferences.Save(path,line.Serialize());back=ViewPreferences.Load(path);
        Check("c settings round trip (line)",back.FocusMode==2&&line.Serialize().Contains("focusMode=\"line\""),"focus="+back.FocusMode);
        ViewPreferences.Save(path,new ViewPreferences().Serialize());back=ViewPreferences.Load(path);
        Check("c settings default off",back.FocusMode==0&&!back.TypewriterMode&&!new ViewPreferences().Serialize().Contains("focusMode"),"focus="+back.FocusMode+" typewriter="+back.TypewriterMode);
        // An old file (2.2.0 builds before the submenu) wrote focusMode="true".
        File.WriteAllText(path,new ViewPreferences().Serialize().Replace("<MDNote ","<MDNote focusMode=\"true\" "),new UTF8Encoding(false));
        back=ViewPreferences.Load(path);
        Check("c old focusMode=\"true\" loads as paragraph",back.FocusMode==1,"focus="+back.FocusMode);
        File.WriteAllText(path,new ViewPreferences().Serialize().Replace("<MDNote ","<MDNote focusMode=\"nonsense\" "),new UTF8Encoding(false));
        back=ViewPreferences.Load(path);
        Check("c unknown focusMode value loads as off",back.FocusMode==0,"focus="+back.FocusMode);
        var baseline=new ViewPreferences();var mine=baseline.Copy();mine.FocusMode=2;var disk=new ViewPreferences {TypewriterMode=true};
        var merged=ViewPreferences.MergePreferences(mine,baseline,disk);
        Check("c merge keeps another window's change",merged.FocusMode==2&&merged.TypewriterMode,"focus="+merged.FocusMode+" typewriter="+merged.TypewriterMode);
        Check("c copy carries both",mine.Copy().FocusMode==2&&disk.Copy().TypewriterMode,"");
        RemoveFile(path);
    }
    static void MoveCaret(SourceEditor editor,int line){editor.TextArea.Caret.Offset=editor.Document.GetLineByNumber(line).Offset;Pump();Pump();}
    // The caret row's centre in TextArea coordinates minus the TextArea's middle.
    static double CentreError(SourceEditor editor){
        var view=editor.TextArea.TextView;var caret=editor.TextArea.Caret;
        double top=view.GetVisualPosition(caret.Position,VisualYPosition.LineTop).Y,bottom=view.GetVisualPosition(caret.Position,VisualYPosition.LineBottom).Y;
        Point at=view.TranslatePoint(new Point(0,(top+bottom)/2-view.ScrollOffset.Y),editor.TextArea);
        return at.Y-editor.TextArea.ActualHeight/2;
    }
    static double RowBottomInArea(SourceEditor editor){
        var view=editor.TextArea.TextView;double bottom=view.GetVisualPosition(editor.TextArea.Caret.Position,VisualYPosition.LineBottom).Y;
        return view.TranslatePoint(new Point(0,bottom-view.ScrollOffset.Y),editor.TextArea).Y;
    }
    static void Shot(MainWindow window,string path){
        window.UpdateLayout();Pump();Pump();
        var root=window.Content as FrameworkElement;if(root==null){Check("shot "+Path.GetFileName(path),false,"no content");return;}
        int width=(int)Math.Ceiling(root.ActualWidth),height=(int)Math.Ceiling(root.ActualHeight);
        var bitmap=new RenderTargetBitmap(Math.Max(1,width),Math.Max(1,height),96,96,PixelFormats.Pbgra32);bitmap.Render(root);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(path))encoder.Save(stream);
        report.Append("shot ").Append(Path.GetFileName(path)).Append(' ').Append(width).Append('x').Append(height).Append('\n');
    }
    static MainWindow OpenWindow(string file){
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
        window.Show();window.Left=-20000;window.Top=-20000;
        Call(window,"LoadFileCore",file);window.SetMode(true);window.UpdateLayout();Pump();Pump();
        return window;
    }
    static void SetHideMarkers(MainWindow window,bool hidden){
        typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,hidden);Call(window,"UpdateLivePreview");window.UpdateLayout();Pump();
    }
    static void Discard(MainWindow window){
        var busy=typeof(MainWindow).GetField("documentOperationBusy",Members);if(busy!=null)busy.SetValue(window,false);
        var text=typeof(MainWindow).GetProperty("text",Members);var saved=typeof(MainWindow).GetField("saved",Members);
        if(text!=null&&saved!=null)saved.SetValue(window,text.GetValue(window,null));
        var recovered=typeof(MainWindow).GetField("recoveredDocument",Members);if(recovered!=null)recovered.SetValue(window,false);
    }
    static object Call(MainWindow window,string name,params object[] args){
        foreach(MethodInfo method in typeof(MainWindow).GetMethods(Members))if(method.Name==name&&method.GetParameters().Length==args.Length)return method.Invoke(window,args);
        throw new MissingMethodException("MainWindow",name);
    }
    static void Check(string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail==null?"":detail.Replace('\r',' ').Replace('\n',' ')).Append('\n');}
    static void Progress(string step){try{File.AppendAllText(progressPath,step+" "+DateTime.Now.ToString("HH:mm:ss.fff")+"\r\n");}catch(Exception){}}
    static void RemoveFile(string path){try{if(File.Exists(path))File.Delete(path);}catch(Exception){}}
    static void PumpFor(int milliseconds){var end=DateTime.UtcNow.AddMilliseconds(milliseconds);while(DateTime.UtcNow<end){Pump();System.Threading.Thread.Sleep(15);}}
    static void Pump(){
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{frame.Continue=false;}));
        Dispatcher.PushFrame(frame);
    }
}
}
