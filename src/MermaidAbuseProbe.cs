using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {public partial class MainWindow {
internal static int VerifyMermaidAbuse(string outDir){return MermaidAbuseProbe.Run(outDir);}
// The abuse side of the ```mermaid editor feature (MermaidRender.cs + the MarkdownLivePreview hooks), in the
// basic and the live view. It runs at the end of MermaidLiveProbe (--mermaid-live-probe), which fails with it.
// Nothing here invents a product rule: the checks hold the feature to its own (a block shows its picture only
// while the caret is outside it) and demand that the editor never throws, never draws one block twice, never
// leaves a stale picture or fold, and never renders more than the document holds. Each case builds its own
// MainWindow, so Basic/Live, zoom and theme are the app's real entry points. Documents are generated here; no
// user file and no clipboard is touched. Report <folder>\mermaid-abuse-probe.txt, -PASS.txt only when every
// check held.
internal static class MermaidAbuseProbe {
    static bool all=true;
    static readonly StringBuilder report=new StringBuilder();
    static string folder;
    // The whole probe's time bound: mermaid-live-probe runs it at its own end after checking its own 90 s, so
    // this part owns the rest of the 150 s (every wait returns at once once the bound passes). Raised from
    // 55000 to 80000 with the m11 and m12 cases added: the older cases alone ran about 55 s here. 11-15: the
    // test VM is slower and ran out at 80 s (m6-m10 never waited), so 125000, still inside the 150 s check.
    const int BudgetMilliseconds=125000;
    static Stopwatch clock;

    static void Check(string name,bool ok,string detail){
        report.Append(ok?"PASS ":"FAIL ").Append(name);
        if(detail!=null)report.Append(": ").Append(detail);
        report.Append('\n');
        if(!ok)all=false;
        Trace((ok?"PASS ":"FAIL ")+name);
    }
    static void Note(string text){report.Append("INFO ").Append(text).Append('\n');Trace("INFO "+text);}
    // Written line by line so a run that hangs still shows the last step it reached.
    static void Trace(string text){
        try{if(folder!=null)File.AppendAllText(Path.Combine(folder,"mermaid-abuse-trace.txt"),(clock==null?0:clock.ElapsedMilliseconds)+" "+text+"\n",new UTF8Encoding(false));}catch(Exception){}
    }
    static string N(double value){return value.ToString("0.##",CultureInfo.InvariantCulture);}
    static string Mode(bool live){return live?"live":"basic";}
    static string Clip(string value){
        if(value==null)return "<null>";
        if(value.Length>60)value=value.Substring(0,60)+"...";
        return value.Replace("\r","\\r").Replace("\n","\\n");
    }

    // ------------------------------------------------ window, pumping, inspection
    // The pump MermaidUndoProbe uses: ApplicationIdle lets the session's Background idle timer run.
    static void Pump(MainWindow window){
        LongLineBench.Idle();
        if(window==null||window.source==null)return;
        window.UpdateLayout();
        window.source.TextArea.TextView.EnsureVisualLines();
    }
    static bool WaitFor(MainWindow window,Func<bool> condition,int milliseconds){
        Stopwatch watch=Stopwatch.StartNew();
        while(true){
            // A case that never settles must not eat the whole run: past the probe's own deadline every wait
            // returns at once and the remaining checks report what they see.
            if(clock!=null&&clock.ElapsedMilliseconds>=BudgetMilliseconds)return false;
            Pump(window);
            if(condition())return true;
            if(watch.ElapsedMilliseconds>=milliseconds)return false;
            Thread.Sleep(10);
        }
    }
    static void PumpFor(MainWindow window,int milliseconds){
        WaitFor(window,delegate { return false; },milliseconds);
    }
    static MainWindow Open(string text,bool live){
        bool old=ProbeDefaults.Live;ProbeDefaults.Live=live;
        try{
            MainWindow window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=800};
            window.Show();window.Left=-20000;window.UpdateLayout();
            window.SetText(text);window.saved=text;window.recoveredDocument=false;
            window.SetMode(true);window.SetLiveMode(live);
            LongLineBench.Idle();window.UpdateLayout();
            // SetText clears the history itself; anything left here came from setup, not from a user edit.
            if(window.History.UndoDepth!=0)window.History.Clear();
            return window;
        }finally{ProbeDefaults.Live=old;}
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{window.FlushDocumentChanges();window.saved=window.text;window.recoveredDocument=false;window.CaptureTab();foreach(var document in window.documents){document.Saved=document.Text;document.Recovered=false;}window.Close();}catch(Exception){}
    }
    static TextDocument Doc(MainWindow window){return window.source.Document;}
    // The caret is placed with a real column so it never rests on a fence row: in the basic view a caret on a
    // hidden fence row is snapped to the neighbouring line (SnapHiddenCaret).
    static void Caret(MainWindow window,int line,int column){
        TextDocument document=Doc(window);
        if(line<1)line=1;
        if(line>document.LineCount)line=document.LineCount;
        DocumentLine target=document.GetLineByNumber(line);
        if(column<1)column=1;
        if(column>target.Length+1)column=target.Length+1;
        window.source.TextArea.Caret.Line=line;
        window.source.TextArea.Caret.Column=column;
        Pump(window);
    }
    static void CaretEnd(MainWindow window,int line){Caret(window,line,Doc(window).GetLineByNumber(line).Length+1);}
    static MermaidElement Diagram(MainWindow window,int number){
        VisualLine visual=window.source.TextArea.TextView.GetVisualLine(number);
        if(visual==null)return null;
        foreach(VisualLineElement element in visual.Elements){MermaidElement diagram=element as MermaidElement;if(diagram!=null)return diagram;}
        return null;
    }
    static bool Shown(MainWindow window,int number){return Diagram(window,number)!=null;}
    // The opener's visual line spans the folded lines under it up to `last`.
    static bool Folded(MainWindow window,int opener,int last){
        VisualLine visual=window.source.TextArea.TextView.GetVisualLine(opener);
        return visual!=null&&visual.FirstDocumentLine.LineNumber==opener&&visual.LastDocumentLine.LineNumber==last;
    }
    // The line has a visual line of its own (it is not folded under an opener).
    static bool OwnLine(MainWindow window,int number){
        VisualLine visual=window.source.TextArea.TextView.GetVisualLine(number);
        return visual!=null&&visual.FirstDocumentLine.LineNumber==number;
    }
    static bool LineShown(MainWindow window,int number){return window.source.TextArea.TextView.GetVisualLine(number)!=null;}
    static int Records(MainWindow window){return window.livePreview==null?0:window.livePreview.MermaidBlockCount;}
    static int Folds(MainWindow window){return window.livePreview==null?0:window.livePreview.MermaidFoldCount;}
    static string LineText(MainWindow window,int number){
        TextDocument document=Doc(window);
        if(number<1||number>document.LineCount)return "<no line "+number+">";
        return document.GetText(document.GetLineByNumber(number));
    }
    static string EngineState(){
        return "unavailable="+MermaidEngine.Unavailable+" startup="+(MermaidEngine.StartupError??"-")+" last="+(MermaidRender.LastError??"-")+
            " pending="+MermaidRender.PendingCount+" requests="+MermaidRender.DiagRequests+" renders="+MermaidEngine.DiagRenders;
    }
    static void ScrollTo(MainWindow window,int line){
        TextView view=window.source.TextArea.TextView;
        double top=view.GetVisualTopByDocumentLine(line)-40;
        window.source.ScrollToVerticalOffset(Math.Max(0,top));
        Pump(window);
    }

    // ------------------------------------------------ fences, read by the block cache's own rule
    // FenceRead mirrors MarkdownLivePreview.ReadFence: up to three leading spaces, three or more ` or ~, and
    // the closer must carry the same marker, at least the opener's length and nothing but blanks after it. A
    // blockquote or list marker before the backticks makes it no fence at all.
    sealed class Fence { internal int Opener, Closer; }
    static bool FenceRead(string text,out char marker,out int length,out bool tailBlank){
        marker='\0';length=0;tailBlank=false;
        int p=0;
        while(p<text.Length&&p<4&&text[p]==' ')p++;
        if(p>3||p>=text.Length)return false;
        char first=text[p];
        if(first!='`'&&first!='~')return false;
        int start=p;
        while(p<text.Length&&text[p]==first)p++;
        if(p-start<3)return false;
        marker=first;length=p-start;
        tailBlank=true;
        while(p<text.Length){
            char t=text[p++];
            if(t!=' '&&t!='\t'){tailBlank=false;break;}
        }
        return true;
    }
    // Every line the session would treat as a mermaid opener: not inside another fence, the block cache's
    // fence rule, MermaidRender.IsMermaidFence and the 64-character limit ParseLine applies before it sets
    // LineInfo.Mermaid.
    static List<int> MermaidOpeners(TextDocument document){
        var openers=new List<int>();
        int open=0;
        char marker='\0';
        int length=0;
        for(int number=1;number<=document.LineCount;number++){
            string text=document.GetText(document.GetLineByNumber(number));
            char seen;
            int seenLength;
            bool blank;
            bool fence=FenceRead(text,out seen,out seenLength,out blank);
            if(open==0){
                if(!fence)continue;
                if(MermaidRender.IsMermaidFence(text)&&text.Length<=64)openers.Add(number);
                open=number;marker=seen;length=seenLength;
                continue;
            }
            if(!fence||seen!=marker||seenLength<length||!blank)continue;
            open=0;
        }
        return openers;
    }
    // The pairs MermaidScan can draw: the opener is a mermaid fence (not inside another fence), the closer is
    // within MermaidRender.BlockLines lines of it, and there is at least one non-blank source line between them
    // (MermaidScan gives up on `count < 2` and on blank source, so those blocks stay code).
    static List<Fence> MermaidFences(TextDocument document){
        var list=new List<Fence>();
        int open=0;
        char marker='\0';
        int length=0;
        for(int number=1;number<=document.LineCount;number++){
            string text=document.GetText(document.GetLineByNumber(number));
            char seen;
            int seenLength;
            bool blank;
            bool fence=FenceRead(text,out seen,out seenLength,out blank);
            if(open==0){
                if(fence){open=number;marker=seen;length=seenLength;}
                continue;
            }
            if(!fence||seen!=marker||seenLength<length||!blank)continue;
            string openerText=document.GetText(document.GetLineByNumber(open));
            if(MermaidRender.IsMermaidFence(openerText)&&openerText.Length<=64&&number-open<=MermaidRender.BlockLines&&number-open>=2){
                DocumentLine openLine=document.GetLineByNumber(open),closeLine=document.GetLineByNumber(number);
                int start=openLine.Offset+openLine.TotalLength;
                if(document.GetText(start,closeLine.Offset-start).Trim().Length>0)
                    list.Add(new Fence {Opener=open,Closer=number});
            }
            open=0;
        }
        return list;
    }
    // A line outside every fence and not the document's last line, so a caret there never sits on a closer
    // that the document-end stop could keep. 0 when no such line exists.
    static int OutsideLine(TextDocument document,List<Fence> fences){
        for(int number=1;number<document.LineCount;number++){
            bool inside=false;
            foreach(Fence fence in fences)if(number>=fence.Opener&&number<=fence.Closer){inside=true;break;}
            if(!inside)return number;
        }
        return 0;
    }
    // The caret rule in one place: a block shows its picture only while the caret and the selection are
    // outside it. MermaidActive also keeps the picture for the closer of a block that ends the document at the
    // document-end stop; every caret here is placed outside, so the plain range test is what must hold.
    static bool CaretOutside(MainWindow window,int first,int last){
        int line=window.source.TextArea.Caret.Line;
        if(line>=first&&line<=last)return false;
        if(window.source.TextArea.Selection.IsEmpty)return true;
        ISegment segment=window.source.TextArea.Selection.SurroundingSegment;
        if(segment==null)return true;
        TextDocument document=Doc(window);
        int start=document.GetLineByOffset(Math.Min(segment.Offset,document.TextLength)).LineNumber;
        int end=document.GetLineByOffset(Math.Min(segment.EndOffset,document.TextLength)).LineNumber;
        return !(start<=last&&end>=first);
    }
    // The cache key MermaidKeyOf builds for this block (source + the session's font suffix), or null when the
    // block has no source. Read only: nothing is requested here.
    static string BlockKey(MainWindow window,int opener,int closer){
        MarkdownLivePreview.Session session=window.livePreview;
        if(session==null||closer<=opener)return null;
        TextDocument document=Doc(window);
        DocumentLine open=document.GetLineByNumber(opener),close=document.GetLineByNumber(closer);
        int start=open.Offset+open.TotalLength,length=close.Offset-start;
        if(length<=0||length>MermaidRender.SourceLimit)return null;
        string text=document.GetText(start,length);
        if(text.IndexOf('\r')>=0)text=text.Replace("\r\n","\n").Replace('\r','\n');
        text=text.TrimEnd('\n',' ','\t');
        if(text.Trim().Length==0)return null;
        return text+MermaidRender.Suffix(session.Options.Family,session.Options.FontSize,session.PixelsPerDip());
    }
    static int State(MainWindow window,int opener,int closer){
        string key=BlockKey(window,opener,closer);
        return key==null?MermaidRender.Failed:MermaidRender.State(key);
    }
    // The block's source as MermaidScan reads it (the key without its font suffix), or null.
    static string BlockSource(MainWindow window,int opener,int closer){
        string key=BlockKey(window,opener,closer);
        if(key==null)return null;
        MarkdownLivePreview.Session session=window.livePreview;
        string suffix=MermaidRender.Suffix(session.Options.Family,session.Options.FontSize,session.PixelsPerDip());
        return key.Substring(0,key.Length-suffix.Length);
    }
    static string StateName(int state){
        return state==MermaidRender.Ready?"ready":state==MermaidRender.Failed?"failed":state==MermaidRender.Pending?"pending":"unknown";
    }
    static string Hash(string text){
        if(text==null)return "-";
        uint hash=2166136261;
        foreach(char c in text){hash^=c;hash=unchecked(hash*16777619);}
        return hash.ToString("x8");
    }
    // The engine's own answer for a source, asked directly (no cache, no session): the ground truth for
    // "this block cannot be drawn". null = it renders.
    static string DirectError(MainWindow window,string source){
        if(source==null)return "no source";
        MarkdownLivePreview.Session session=window.livePreview;
        MermaidResult result=MermaidEngine.RenderSync(source,session.Options.Family,session.Options.FontSize,session.PixelsPerDip(),4000);
        if(result==null)return "no result";
        if(result.Error!=null)return result.Error;
        return String.IsNullOrEmpty(result.Svg)?"empty svg":null;
    }
    // A picture on a laid-out line that is not a mermaid opener is stale. Only visible lines can be read, so
    // callers scroll the block into view first.
    static int StalePictures(MainWindow window){
        TextView view=window.source.TextArea.TextView;
        if(!view.VisualLinesValid)return 0;
        int stale=0;
        foreach(VisualLine line in view.VisualLines){
            if(line.IsDisposed)continue;
            int number=line.FirstDocumentLine.LineNumber;
            if(Diagram(window,number)==null)continue;
            if(!MermaidRender.IsMermaidFence(LineText(window,number)))stale++;
        }
        return stale;
    }

    // ------------------------------------------------ documents
    // A small diagram whose label names `tag`, so a picture of one block is never taken for another's.
    static string Small(string tag,string shape){
        return "```mermaid\n"+shape+"\n  A["+tag+"] --> B[끝]\n```\n";
    }
    static void AppendBlock(StringBuilder builder,int index){
        builder.Append("문단 ").Append(index).Append(": mermaid 블록 아래 설명 글.\n\n");
        builder.Append(Small("블록"+index,"flowchart LR"));
        builder.Append('\n');
    }
    // Each block is 7 lines: paragraph, blank, opener, two source lines, closer, blank.
    static string ManyBlocksText(int count){
        var builder=new StringBuilder();
        for(int n=1;n<=count;n++)AppendBlock(builder,n);
        builder.Append("마지막 문단\n");
        return builder.ToString();
    }

    // ------------------------------------------------ m1 a long scroll through 40 blocks
    static void ScrollMany(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(ManyBlocksText(40),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=40){Check("m1 "+mode+": forty mermaid blocks are read from the document",false,"fences="+fences.Count);return;}
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            long renders=MermaidEngine.DiagRenders;
            bool firstPicture=WaitFor(window,delegate { return Shown(window,fences[0].Opener); },4000);
            if(!firstPicture){Check("m1 "+mode+": the first block is drawn before the scroll",false,EngineState());return;}
            PumpFor(window,150);
            // Down in 20 steps, each bounded; then back up without a wait (every picture is cached by then).
            // A broken state cannot eat the budget: the walk itself stops after 5 s per mode.
            int stepTimeouts=0;
            Stopwatch walk=Stopwatch.StartNew();
            for(int step=1;step<=20&&walk.ElapsedMilliseconds<5000;step++){
                double maximum=Math.Max(0,window.source.ExtentHeight-window.source.ViewportHeight);
                window.source.ScrollToVerticalOffset(maximum*step/20.0);
                Pump(window);
                if(!WaitFor(window,delegate { return MermaidRender.PendingCount==0&&VisiblePictures(window,fences); },450))stepTimeouts++;
            }
            for(int step=19;step>=0;step--){
                double maximum=Math.Max(0,window.source.ExtentHeight-window.source.ViewportHeight);
                window.source.ScrollToVerticalOffset(maximum*step/20.0);
                Pump(window);
            }
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            WaitFor(window,delegate { return MermaidRender.PendingCount==0; },5000);
            PumpFor(window,250);
            // Afterwards, every block is visited: the ones already laid out are read where they are, the rest
            // are scrolled into view and waited for by state. A block whose picture never arrived is counted
            // and reported rather than hidden in the total.
            int missed;
            int[] counts=MeasureBlocks(window,fences,out missed);
            int visible=counts[0],both=counts[1],foldedNoPicture=counts[2],pictureNotFolded=counts[3],pictures=counts[4];
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            int stale=StalePictures(window);
            Check("m1 "+mode+": the scroll draws no block twice and leaves no stale picture",both==0&&stale==0,
                "blocks="+fences.Count+" visible="+visible+" pictures="+pictures+" both="+both+" stale="+stale+" records="+Records(window)+" "+EngineState());
            Check("m1 "+mode+": every block is a picture or its own code, never a fold under nothing",
                foldedNoPicture==0&&pictureNotFolded==0,
                "visible="+visible+" foldedNoPicture="+foldedNoPicture+" pictureNotFolded="+pictureNotFolded+" pictures="+pictures+" stepTimeouts="+stepTimeouts);
            Check("m1 "+mode+": all forty blocks kept a record and a fold after the walk",
                visible==40&&Records(window)==40&&Folds(window)==40&&pictures==40,
                "laidOut="+visible+"/40 records="+Records(window)+"/40 folds="+Folds(window)+"/40 pictures="+pictures+
                " missed="+missed+" walk="+walk.ElapsedMilliseconds+" ms "+EngineState());
            Check("m1 "+mode+": the engine rendered each block once at most (the cache works)",MermaidEngine.DiagRenders-renders<=40,
                "renders +"+(MermaidEngine.DiagRenders-renders)+" for 40 blocks, picture hits "+MermaidRender.DiagPictureHits);
            Note("m1 "+mode+": renders +"+(MermaidEngine.DiagRenders-renders)+", requests "+MermaidRender.DiagRequests+", lines "+Doc(window).LineCount+", step timeouts "+stepTimeouts);
        }catch(Exception ex){
            Check("m1 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }
    // Every mermaid block whose lines are laid out now is a picture.
    static bool VisiblePictures(MainWindow window,List<Fence> fences){
        foreach(Fence fence in fences){
            if(!LineShown(window,fence.Opener))continue;
            if(!Shown(window,fence.Opener))return false;
        }
        return true;
    }
    // Waits until the block is drawn, or its source is known to fail (the failure is remembered, so no
    // later pass changes the block); a picture still being requested keeps it waiting. False = deadline.
    static bool WaitDrawn(MainWindow window,Fence fence,int milliseconds){
        return WaitFor(window,delegate {
            if(Shown(window,fence.Opener))return true;
            return State(window,fence.Opener,fence.Closer)==MermaidRender.Failed;
        },milliseconds);
    }
    // One pass over the blocks of m1: scroll an opener into view, wait for its picture, then read the four
    // states the checks count. Returns visible, both, foldedNoPicture, pictureNotFolded, pictures; missed
    // counts the blocks whose picture never came inside their wait (reported, not hidden in the total).
    // Most blocks are cached by the walk, so the waits cost nothing; the pass still stops waiting after
    // 12 s so one broken block cannot eat the probe's own budget.
    static int[] MeasureBlocks(MainWindow window,List<Fence> fences,out int missed){
        int visible=0,both=0,foldedNoPicture=0,pictureNotFolded=0,pictures=0;
        missed=0;
        Stopwatch pass=Stopwatch.StartNew();
        foreach(Fence fence in fences){
            if(!LineShown(window,fence.Opener))ScrollTo(window,fence.Opener);
            // A block scrolled into view asks the engine on the idle pass: the wait has to cover the request
            // (300 ms), the render and the fold. The fixed 250 ms this used to give each block ended the
            // pass with the last picture still pending (pictures=39, pending=1).
            if(!Shown(window,fence.Opener)&&pass.ElapsedMilliseconds<12000&&clock.ElapsedMilliseconds<BudgetMilliseconds-8000&&!WaitDrawn(window,fence,2500))missed++;
            PumpFor(window,30);
            if(!LineShown(window,fence.Opener))continue;
            visible++;
            bool shown=Shown(window,fence.Opener);
            bool folded=Folded(window,fence.Opener,fence.Closer);
            if(shown)pictures++;
            // A picture and its first source line laid out on its own at once is the double draw.
            if(shown&&OwnLine(window,fence.Opener+1))both++;
            if(folded&&!shown)foldedNoPicture++;
            if(shown&&!folded)pictureNotFolded++;
        }
        int[] counts={visible,both,foldedNoPicture,pictureNotFolded,pictures};
        return counts;
    }

    // ------------------------------------------------ m2 300 random edits
    static void EditStorm(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            var builder=new StringBuilder();
            for(int n=1;n<=5;n++)AppendBlock(builder,n);
            for(int n=1;n<=40;n++)builder.Append("설명 문단 ").Append(n).Append(": 글자를 고치는 곳.\n");
            window=Open(builder.ToString(),live);
            var fences=MermaidFences(Doc(window));
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            bool first=WaitFor(window,delegate { return fences.Count>0&&Shown(window,fences[0].Opener)&&MermaidRender.PendingCount==0; },6000);
            if(!first){Check("m2 "+mode+": a block is drawn before the storm",false,EngineState());return;}
            PumpFor(window,200);
            var random=new Random(12345);
            int edits=0,skipped=0,inBlock=0,onFence=0,textEdits=0;
            // The storm has to leave blocks to judge: if it ever took every fence out, the pass over the
            // document below would count zero blocks and the check would be vacuous. Steps that would touch
            // a block are aimed at a text line instead unless more than half of the original blocks stand.
            int baseline=fences.Count;
            for(int step=0;step<300;step++){
                TextDocument document=Doc(window);
                int length=document.TextLength;
                if(length<40){skipped++;continue;}
                int action=random.Next(100);
                int offset=random.Next(length);
                string insert=null;
                int remove=0;
                // The storm keeps at least half of the original blocks standing, so its pass over the
                // document below always has blocks to judge. Only a text edit can change the pairing.
                List<Fence> now=MermaidFences(document);
                bool keepBlocks=now.Count*2>=baseline;
                if(!keepBlocks)action=action<20?99:action;
                if(action<5){
                    // A fence-shaped edit on a source row just inside a block: one character typed or removed
                    // there, never on an opener or a closer, so the pairing survives and the block is
                    // rescanned with a different source.
                    Fence pick=now[random.Next(now.Count)];
                    int line=random.Next(pick.Opener+1,pick.Closer);
                    DocumentLine target=document.GetLineByNumber(line);
                    if(random.Next(2)==0){offset=target.EndOffset;insert="`";}
                    else{
                        if(target.Length<1){skipped++;continue;}
                        offset=target.EndOffset-1;remove=1;
                    }
                    onFence++;
                }else if(action<15){
                    // Inside a block: one to five characters at a random offset of a source row.
                    Fence pick=now[random.Next(now.Count)];
                    int line=random.Next(pick.Opener+1,pick.Closer);
                    DocumentLine target=document.GetLineByNumber(line);
                    offset=target.Offset+random.Next(target.Length+1);
                    insert=new string((char)('a'+random.Next(26)),random.Next(1,6));
                    inBlock++;
                }else if(action<20){
                    // A newline just outside every block: the line structure changes, the fences do not.
                    int free=OutsideLine(document,now);
                    if(free>0)offset=document.GetLineByNumber(free).EndOffset;
                    insert="\n";
                    textEdits++;
                }else{
                    // Text edits stay inside one line outside every block (an edit that ran past its end
                    // could reach a fence row and change the pairing): the blocks are what the storm leaves
                    // judgeable in a state of its own.
                    int free=OutsideLine(document,now);
                    if(free>0){
                        DocumentLine target=document.GetLineByNumber(free);
                        offset=target.Offset+random.Next(0,target.Length+1);
                        int room=target.Offset+target.Length-offset;
                        if(action<60)insert=new string((char)('a'+random.Next(26)),Math.Min(random.Next(1,6),room+1));
                        else remove=Math.Min(random.Next(1,6),room);
                    }
                    textEdits++;
                }
                if(remove==0&&insert==null){skipped++;continue;}
                if(offset<0||offset+remove>length){skipped++;continue;}
                if(remove>0)document.Remove(offset,remove);
                if(insert!=null)document.Insert(offset,insert);
                edits++;
                // The caret moves every few edits, so a picture the storm never had to reconsider is not the
                // only kind it meets.
                if(step%3==0){
                    TextDocument moved=Doc(window);
                    int line=random.Next(1,moved.LineCount+1);
                    DocumentLine target=moved.GetLineByNumber(line);
                    window.source.TextArea.Caret.Line=line;
                    window.source.TextArea.Caret.Column=random.Next(0,target.Length)+1;
                    Pump(window);
                }else if(step%25==0){
                    Pump(window);
                }
            }
            PumpFor(window,1000);
            WaitFor(window,delegate { return MermaidRender.PendingCount==0; },10000);
            PumpFor(window,300);
            TextDocument final=Doc(window);
            fences=MermaidFences(final);
            // Random characters inside a source row usually break the diagram, so a storm can leave every
            // block with a failed source and nothing to judge the drawing rule on. One surviving block gets
            // its source back in one Replace (an edit like any other) so the pass below has a block that
            // must be drawn; the rest are judged as the storm left them. The Replace can move the closer's
            // line, so the pair list is read again before the wait.
            if(fences.Count>0){
                Fence anchor=fences[0];
                DocumentLine anchorOpen=final.GetLineByNumber(anchor.Opener),anchorClose=final.GetLineByNumber(anchor.Closer);
                int anchorStart=anchorOpen.Offset+anchorOpen.TotalLength,anchorLength=anchorClose.Offset-anchorStart;
                // The label names the mode: the caches are shared, so the live pass would otherwise be served
                // the basic pass's picture without asking the engine.
                string good="flowchart LR\n  A[회복 "+mode+"] --> B[끝]\n";
                if(anchorLength>=0&&!String.Equals(final.GetText(anchorStart,anchorLength),good,StringComparison.Ordinal)){
                    final.Replace(anchorStart,anchorLength,good);
                    Pump(window);
                    final=Doc(window);
                    fences=MermaidFences(final);
                    if(fences.Count>0){
                        Fence restored=fences[0];
                        WaitFor(window,delegate { return State(window,restored.Opener,restored.Closer)==MermaidRender.Ready; },8000);
                        WaitFor(window,delegate { return MermaidRender.PendingCount==0; },5000);
                        PumpFor(window,300);
                        final=Doc(window);
                        fences=MermaidFences(final);
                    }
                }
            }
            outside=OutsideLine(final,fences);
            if(outside>0)Caret(window,outside,1);
            PumpFor(window,250);
            int judged=0,wrong=0,outsideReady=0,pictured=0,notReady=0,failedShown=0,stale=0,notVisible=0;
            Stopwatch judging=Stopwatch.StartNew();
            var doubt=new List<Fence>();
            foreach(Fence fence in fences){
                if(!LineShown(window,fence.Opener))ScrollTo(window,fence.Opener);
                if(!LineShown(window,fence.Opener)){notVisible++;continue;}
                // A block the storm's scroll brought into view may still be waiting for its render: wait for
                // its picture instead of reading a snapshot 90 ms into the request, but only for the first
                // few seconds of the pass so a broken block cannot eat the probe's budget.
                if(!Shown(window,fence.Opener)&&judging.ElapsedMilliseconds<6000&&clock.ElapsedMilliseconds<BudgetMilliseconds-6000)WaitDrawn(window,fence,2000);
                PumpFor(window,60);
                stale+=StalePictures(window);
                judged++;
                bool shown=Shown(window,fence.Opener);
                bool folded=Folded(window,fence.Opener,fence.Closer);
                if(!CaretOutside(window,fence.Opener,fence.Closer)){
                    // The caret rule's inside half: the source shows and no picture is drawn.
                    if(shown||folded)wrong++;
                    continue;
                }
                int state=State(window,fence.Opener,fence.Closer);
                if(state!=MermaidRender.Ready){
                    // A pending source keeps the previous picture on purpose; a failed one must drop it.
                    notReady++;
                    doubt.Add(fence);
                    if(state==MermaidRender.Failed&&shown)failedShown++;
                    continue;
                }
                outsideReady++;
                if(shown&&folded)pictured++;
                else wrong++;
            }
            // The blocks outside the caret with no cached picture: each one's state and the engine's own
            // answer for its source. A source the engine draws when asked directly must not stay text.
            int lost=0,transient=0;
            bool waiting=false;
            foreach(Fence fence in doubt){
                int state=State(window,fence.Opener,fence.Closer);
                if(state==MermaidRender.Unknown||state==MermaidRender.Pending)waiting=true;
            }
            // Idle only: no edit, no caret move, no scroll. A failed source never changes, so the wait is
            // only for a block still unknown or pending.
            if(waiting)WaitFor(window,delegate {
                foreach(Fence fence in doubt){
                    int state=State(window,fence.Opener,fence.Closer);
                    if(state==MermaidRender.Unknown||state==MermaidRender.Pending)return false;
                }
                return true;
            },3000);
            foreach(Fence fence in doubt){
                int state=State(window,fence.Opener,fence.Closer);
                string source=BlockSource(window,fence.Opener,fence.Closer);
                string direct=DirectError(window,source);
                string opener=LineText(window,fence.Opener);
                if(state==MermaidRender.Unknown||state==MermaidRender.Pending)lost++;
                // Only the diagram's own parse error counts as a refusal: an engine that is gone or out of
                // time says nothing about the source.
                else if(direct==null||direct=="timeout"||direct=="time limit"||direct=="unavailable"||direct=="reentrant"||direct=="no result"||direct=="no source")transient++;
                Note("m2 "+mode+": not-ready block at line "+fence.Opener+" opener=\""+Clip(opener)+"\" lines="+(fence.Closer-fence.Opener+1)+
                    " hash="+Hash(source)+" mermaidFence="+MermaidRender.IsMermaidFence(opener)+" key="+(BlockKey(window,fence.Opener,fence.Closer)!=null)+
                    " state="+StateName(state)+" idleWaited="+waiting+" shown="+Shown(window,fence.Opener)+
                    " direct="+(direct==null?"renders":"error \""+Clip(direct)+"\"")+" source=\""+Clip(source)+"\"");
            }
            if(lost>0){
                // The view-change path: scroll away and back, then a caret move outside every block.
                window.source.ScrollToVerticalOffset(window.source.ExtentHeight);
                PumpFor(window,400);
                window.source.ScrollToVerticalOffset(0);
                PumpFor(window,1500);
                foreach(Fence fence in doubt)Note("m2 "+mode+": after a scroll away and back, line "+fence.Opener+" state="+StateName(State(window,fence.Opener,fence.Closer)));
                Caret(window,outside>0?outside:1,2);
                PumpFor(window,1500);
                foreach(Fence fence in doubt)Note("m2 "+mode+": after a caret move, line "+fence.Opener+" state="+StateName(State(window,fence.Opener,fence.Closer)));
            }
            // The blocks left as text are exactly the ones the engine refuses: none was never asked, none is
            // still waiting after the idle time, and none carries a remembered failure for a source that draws.
            Check("m2 "+mode+": a block left as text after the storm has a source the engine refuses",lost==0&&transient==0,
                "notReady="+doubt.Count+" neverAskedOrPending="+lost+" failedButDrawableOrNoAnswer="+transient+" "+EngineState());
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            int openers=MermaidOpeners(final).Count;
            // A record per opener is the shape; a record over an opener that left the document is the bug.
            Check("m2 "+mode+": 300 edits leave the session settled and the document whole",
                MermaidRender.PendingCount==0&&final.LineCount>0&&Records(window)<=openers,
                "edits="+edits+" skipped="+skipped+" lines="+final.LineCount+" records="+Records(window)+
                " openers="+openers+" pending="+MermaidRender.PendingCount);
            Check("m2 "+mode+": every block's picture follows the caret rule and the cache",wrong==0&&failedShown==0,
                "judged="+judged+" notVisible="+notVisible+" wrong="+wrong+" failedButShown="+failedShown+" notReady="+notReady+
                " pictures="+pictured+" folds="+Folds(window)+" caretLine="+window.source.TextArea.Caret.Line);
            // A ready block outside the caret is drawn and folded. The storm keeps blocks standing, so a
            // pass over zero blocks (or over none whose source renders) is a vacuous result and says so
            // rather than passing: the counts are in the note either way.
            Check("m2 "+mode+": a picture is drawn for a folded block and for nothing else",
                outsideReady>0&&pictured==outsideReady&&notVisible==0&&Folds(window)>=pictured,
                "outsideReady="+outsideReady+" pictured="+pictured+" folds="+Folds(window)+" notVisible="+notVisible+
                " judged="+judged+" blocks="+fences.Count+" records="+Records(window));
            Check("m2 "+mode+": no picture is left for a block that is no longer a mermaid fence",stale==0,
                "stale="+stale+" fences="+fences.Count);
            // The caret rule's inside half, on a block whose picture is cached.
            Fence probe=null;
            foreach(Fence fence in fences){
                if(State(window,fence.Opener,fence.Closer)==MermaidRender.Ready){probe=fence;break;}
            }
            if(probe==null)Note("m2 "+mode+": no block has a cached picture after the storm; the inside half is not judged here");
            else{
                if(!LineShown(window,probe.Opener))ScrollTo(window,probe.Opener);
                CaretEnd(window,probe.Opener+1);
                PumpFor(window,200);
                bool inside=!Shown(window,probe.Opener)&&OwnLine(window,probe.Opener+1);
                int again=OutsideLine(final,fences);
                if(again>0)Caret(window,again,1);
                PumpFor(window,200);
                bool back=Shown(window,probe.Opener)&&Folded(window,probe.Opener,probe.Closer);
                Check("m2 "+mode+": the caret inside shows the source, outside the picture again",inside&&back,
                    "inside="+inside+" outsideAgain="+back+" opener="+probe.Opener+" "+EngineState());
            }
            // The other half of the doubt: a block the storm broke is asked again once its source is
            // mended. Every refused block gets a source of its own (new to the caches in either mode) in one
            // Replace with the caret outside, then nothing but idle time: each must be drawn and folded.
            if(doubt.Count>0){
                int repairOutside=OutsideLine(final,fences);
                if(repairOutside>0)Caret(window,repairOutside,1);
                // The scroll comes before the edits, so nothing but the edits and idle time follows it.
                ScrollTo(window,doubt[0].Opener);
                long asked=MermaidRender.DiagRequests;
                for(int i=doubt.Count-1;i>=0;i--){
                    Fence fence=doubt[i];
                    DocumentLine open=final.GetLineByNumber(fence.Opener),close=final.GetLineByNumber(fence.Closer);
                    int start=open.Offset+open.TotalLength;
                    final.Replace(start,close.Offset-start,"flowchart LR\n  A[고침 "+mode+" "+i+"] --> B[끝]\n");
                }
                final=Doc(window);
                fences=MermaidFences(final);
                List<Fence> mended=fences;
                // Idle only (no scroll, no caret move): every block gets its picture cached (all five have a
                // record from the passes above). The cache is read by key, so this needs no layout of its own.
                int idleInView=0,idleReady=0;
                bool idle=WaitFor(window,delegate {
                    idleInView=0;idleReady=0;
                    foreach(Fence fence in mended){
                        idleInView++;
                        if(State(window,fence.Opener,fence.Closer)==MermaidRender.Ready)idleReady++;
                    }
                    return idleInView>0&&idleReady==idleInView&&MermaidRender.PendingCount==0;
                },3000);
                // Only laid-out lines can be read, so each block is scrolled to and then only waited on: the
                // 300 ms idle count, the render and the fold. The picture on the line must be this source's
                // own, not a kept older one.
                int mendedShown=0,mendedReady=0,mendedSeen=0,mendedOwn=0;
                string states="";
                Stopwatch mending=Stopwatch.StartNew();
                foreach(Fence each in mended){
                    Fence fence=each;
                    if(!LineShown(window,fence.Opener))ScrollTo(window,fence.Opener);
                    if(!LineShown(window,fence.Opener))continue;
                    mendedSeen++;
                    if(mending.ElapsedMilliseconds<6000)WaitFor(window,delegate { return Shown(window,fence.Opener)&&Folded(window,fence.Opener,fence.Closer); },3000);
                    int state=State(window,fence.Opener,fence.Closer);
                    if(state==MermaidRender.Ready)mendedReady++;
                    bool shown=Shown(window,fence.Opener)&&Folded(window,fence.Opener,fence.Closer);
                    if(shown)mendedShown++;
                    MermaidElement element=Diagram(window,fence.Opener);
                    bool own=element!=null&&element.Picture!=null&&String.Equals(element.Picture.Key,BlockKey(window,fence.Opener,fence.Closer),StringComparison.Ordinal);
                    if(own)mendedOwn++;
                    states+=" "+fence.Opener+":"+StateName(state)+(shown?":drawn":":text")+(own?"":":not its own picture");
                }
                bool settled=WaitFor(window,delegate { return MermaidRender.PendingCount==0; },2000);
                Check("m2 "+mode+": every block mended after the storm is drawn in idle time",
                    idle&&settled&&mended.Count>=doubt.Count&&mendedReady==mended.Count&&mendedSeen==mended.Count&&mendedShown==mended.Count&&mendedOwn==mended.Count&&
                    MermaidRender.DiagRequests-asked>=doubt.Count,
                    "mended="+doubt.Count+" blocks="+mended.Count+" ready="+mendedReady+" visible="+mendedSeen+" drawn="+mendedShown+" ownPicture="+mendedOwn+
                    " idleOnly="+idle+" ("+idleReady+" of "+idleInView+" cached)"+" settled="+settled+" requests +"+(MermaidRender.DiagRequests-asked)+" ms="+mending.ElapsedMilliseconds+" states"+states+" "+EngineState());
                window.source.ScrollToVerticalOffset(0);
                Pump(window);
            }
            Note("m2 "+mode+": "+edits+" edits ("+skipped+" skipped, "+inBlock+" in a block, "+onFence+" on a source row, "+textEdits+" on text), "+
                fences.Count+" blocks, "+judged+" judged, "+outsideReady+" ready and pictured, "+notReady+" not ready, records "+Records(window));
        }catch(Exception ex){
            Check("m2 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }
    // ------------------------------------------------ m3 one Remove of a whole folded block
    static void DeleteBlock(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            string text=ManyBlocksText(3);
            window=Open(text,live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=3){Check("m3 "+mode+": three blocks are read from the document",false,"fences="+fences.Count);return;}
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            bool first=WaitFor(window,delegate {
                return Shown(window,fences[0].Opener)&&Shown(window,fences[1].Opener)&&Shown(window,fences[2].Opener)&&MermaidRender.PendingCount==0;
            },6000);
            if(!first){Check("m3 "+mode+": the three blocks are drawn first",false,EngineState());return;}
            PumpFor(window,200);
            TextDocument document=Doc(window);
            int opener=fences[1].Opener,closer=fences[1].Closer;
            int start=document.GetLineByNumber(opener).Offset;
            int end=document.GetLineByNumber(closer).EndOffset;
            long renders=MermaidEngine.DiagRenders;
            document.Remove(start,end-start);
            Pump(window);
            PumpFor(window,600);
            string expected=text.Remove(start,end-start);
            bool sameText=String.Equals(Doc(window).Text,expected,StringComparison.Ordinal);
            // The caret is on line 1, well above the deleted range: the fold must be gone with the Remove, not
            // by the idle pass.
            Check("m3 "+mode+": one Remove of the whole block leaves exactly the expected text",sameText,
                "textEqual="+sameText+" lines="+Doc(window).LineCount+" chars="+Doc(window).TextLength+"/"+expected.Length);
            Check("m3 "+mode+": the deleted block leaves no picture, record or fold",
                Records(window)==2&&Folds(window)==2&&StalePictures(window)==0,
                "records="+Records(window)+" folds="+Folds(window)+" stale="+StalePictures(window)+" "+EngineState());
            // The app's own undo (window.Undo), never the document's stack. The caret is put back on line 1 as
            // a reader would have left it: an undo that restores a caret inside the block would show source.
            window.Undo();
            Pump(window);
            Caret(window,1,1);
            bool back=WaitFor(window,delegate { return Shown(window,opener)&&Folded(window,opener,closer)&&MermaidRender.PendingCount==0; },5000);
            Check("m3 "+mode+": undo brings the picture back from the cache",
                back&&MermaidEngine.DiagRenders==renders&&String.Equals(Doc(window).Text,text,StringComparison.Ordinal),
                "back="+back+" renders +"+(MermaidEngine.DiagRenders-renders)+" textEqual="+String.Equals(Doc(window).Text,text,StringComparison.Ordinal)+
                " records="+Records(window)+" "+EngineState());
        }catch(Exception ex){
            Check("m3 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m4 a whole block inserted in one Insert
    static void PasteBlock(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(ManyBlocksText(2),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=2){Check("m4 "+mode+": two blocks are read from the document",false,"fences="+fences.Count);return;}
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            bool first=WaitFor(window,delegate { return Shown(window,fences[0].Opener)&&Shown(window,fences[1].Opener)&&MermaidRender.PendingCount==0; },6000);
            if(!first){Check("m4 "+mode+": the existing blocks are drawn first",false,EngineState());return;}
            PumpFor(window,200);
            string pasted=Small("붙임","flowchart TD")+"\n";
            // One Insert of the whole text, the way a paste of a copied diagram arrives. The caret goes to the
            // document's end, outside every block.
            Doc(window).Insert(Doc(window).GetLineByNumber(1).Offset,pasted);
            Pump(window);
            CaretEnd(window,Doc(window).LineCount);
            var after=MermaidFences(Doc(window));
            bool settled=WaitFor(window,delegate {
                if(after.Count!=3)return false;
                return Shown(window,after[0].Opener)&&Shown(window,after[1].Opener)&&Shown(window,after[2].Opener)&&MermaidRender.PendingCount==0;
            },6000);
            PumpFor(window,200);
            after=MermaidFences(Doc(window));
            bool allPictured=after.Count==3;
            if(allPictured)
                foreach(Fence fence in after)allPictured=allPictured&&Shown(window,fence.Opener)&&Folded(window,fence.Opener,fence.Closer);
            Check("m4 "+mode+": a block inserted in one Insert above a picture is drawn too",
                settled&&allPictured&&Records(window)==3,
                "fences="+after.Count+" pictured="+allPictured+" records="+Records(window)+" folds="+Folds(window)+" "+EngineState());
        }catch(Exception ex){
            Check("m4 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m5 odd fences
    // One document per case. A picture is a bonus, not a promise, so the result is INFO; FAIL only for an
    // exception, or a picture where four spaces make indented code.
    static bool OddFence(string mode,string name,string document,int openerLine,bool fourSpace){
        MainWindow window=null;
        try{
            window=Open(document,mode=="live");
            int outside=OutsideLine(Doc(window),MermaidFences(Doc(window)));
            Caret(window,outside>0?outside:Doc(window).LineCount,1);
            // The caret is outside every block (a case whose only line outside is the closer of an unclosed
            // fence has it on that closer; MermaidActive keeps such a block a picture at the document end).
            // A fresh window has nothing pending yet, so "pending==0" alone is not a wait: the record is
            // built and the request sent by the first layout plus the 300 ms idle pass (PrepareMermaid
            // scans at once and MermaidRefreshPicture kicks the timer). Give that pass its time, then wait
            // the request out.
            PumpFor(window,450);
            bool recorded=Records(window)>0;
            bool settled=WaitFor(window,delegate { return MermaidRender.PendingCount==0; },8000);
            PumpFor(window,400);
            bool isFence=openerLine>=1&&openerLine<=Doc(window).LineCount&&MermaidRender.IsMermaidFence(LineText(window,openerLine));
            bool shown=openerLine>=1&&openerLine<=Doc(window).LineCount&&Shown(window,openerLine);
            Note("m5 "+mode+" "+name+": picture="+shown+" openerFence="+isFence+" records="+Records(window)+
                " folds="+Folds(window)+" recorded="+recorded+" settled="+settled+
                " opener=\""+Clip(LineText(window,openerLine))+"\"");
            if(fourSpace)
                Check("m5 "+mode+" "+name+": a four-space fence is indented code and draws no picture",!shown&&!isFence,
                    "picture="+shown+" openerFence="+isFence+" opener=\""+Clip(LineText(window,openerLine))+"\" "+EngineState());
            return true;
        }catch(Exception ex){
            Check("m5 "+mode+" "+name+": no exception",false,ex.ToString());
            return false;
        }finally{CloseWindow(window);}
    }
    // Eight cases. The seven that must simply not throw have their picture read back per case, never demanded;
    // the eighth (four spaces = indented code) must draw nothing. Line numbers are written out for each fixture
    // so a case whose lines moved cannot misreport.
    static void OddFences(bool live){
        string mode=Mode(live);
        int failed=0;
        // A fence longer than three closes only on a fence of its own length, so the inner ``` row is source.
        if(!OddFence(mode,"four backticks","앞\n\n````mermaid\nflowchart TD\n  A[넷] --> B[둘]\n```\n  B --> C[끝]\n````\n\n뒤\n",3,false))failed++;
        if(!OddFence(mode,"tilde fence","앞\n\n~~~mermaid\nflowchart TD\n  A[물결] --> B[둘]\n~~~\n\n뒤\n",3,false))failed++;
        if(!OddFence(mode,"trailing spaces","앞\n\n```mermaid   \nflowchart TD\n  A[공백] --> B[둘]\n```\n\n뒤\n",3,false))failed++;
        if(!OddFence(mode,"title word","앞\n\n```mermaid title\nflowchart TD\n  A[제목] --> B[둘]\n```\n\n뒤\n",3,false))failed++;
        if(!OddFence(mode,"three-space indent","앞\n\n   ```mermaid\nflowchart TD\n  A[셋] --> B[둘]\n   ```\n\n뒤\n",3,false))failed++;
        if(!OddFence(mode,"blockquote fence","앞\n\n> ```mermaid\n> flowchart TD\n>   A[인용] --> B[둘]\n> ```\n\n뒤\n",3,false))failed++;
        if(!OddFence(mode,"list item fence","앞\n\n- ```mermaid\n  flowchart TD\n    A[목록] --> B[둘]\n  ```\n\n뒤\n",3,false))failed++;
        // The one case the fence rule fixes: four spaces is indented code, so nothing may be drawn there.
        if(!OddFence(mode,"four-space indent","앞\n\n    ```mermaid\n    flowchart TD\n      A[넷] --> B[둘]\n    ```\n\n뒤\n",3,true))failed++;
        Check("m5 "+mode+": eight odd fences drew no exception",failed==0,"failed cases="+failed);
    }

    // ------------------------------------------------ m6 mode and theme switches
    static void ModeThemeSwitch(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(ManyBlocksText(2),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=2){Check("m6 "+mode+": two blocks are read from the document",false,"fences="+fences.Count);return;}
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            bool first=WaitFor(window,delegate {
                return Shown(window,fences[0].Opener)&&Shown(window,fences[1].Opener)&&MermaidRender.PendingCount==0;
            },6000);
            if(!first){Check("m6 "+mode+": both blocks are drawn first",false,EngineState());return;}
            PumpFor(window,300);
            long renders=MermaidEngine.DiagRenders,converts=MermaidRender.DiagConverts;
            for(int step=0;step<20;step++){
                window.SetLiveMode(step%2==0?!live:live);
                Pump(window);
                window.SetDark(step%4<2);
                Pump(window);
            }
            // Leave the window in the mode and theme this case started from.
            window.SetLiveMode(live);
            window.SetDark(false);
            Pump(window);
            bool both=WaitFor(window,delegate {
                return Shown(window,fences[0].Opener)&&Shown(window,fences[1].Opener)&&MermaidRender.PendingCount==0;
            },8000);
            PumpFor(window,300);
            Check("m6 "+mode+": 20 mode and theme switches keep both pictures",both,
                "b1="+Shown(window,fences[0].Opener)+" b2="+Shown(window,fences[1].Opener)+" records="+Records(window)+" folds="+Folds(window)+" "+EngineState());
            Check("m6 "+mode+": no engine render after the first pass (the SVG cache serves the theme)",MermaidEngine.DiagRenders==renders,
                "renders +"+(MermaidEngine.DiagRenders-renders)+" converts +"+(MermaidRender.DiagConverts-converts));
        }catch(Exception ex){
            Check("m6 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m7 a font-size (zoom) change
    static void ZoomCase(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(ManyBlocksText(1),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=1){Check("m7 "+mode+": a block is read from the document",false,"fences="+fences.Count);return;}
            Fence fence=fences[0];
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            bool first=WaitFor(window,delegate { return Shown(window,fence.Opener)&&MermaidRender.PendingCount==0; },6000);
            MermaidElement before=Diagram(window,fence.Opener);
            if(!first||before==null){Check("m7 "+mode+": the block is drawn first",false,EngineState());return;}
            PumpFor(window,200);
            string keyBefore=before.Picture.Key;
            double heightBefore=before.Picture.Height;
            long renders=MermaidEngine.DiagRenders,requests=MermaidRender.DiagRequests;
            for(int step=0;step<5;step++){window.Zoom(2.0);Pump(window);}
            bool rendered=WaitFor(window,delegate {
                MermaidElement now=Diagram(window,fence.Opener);
                return MermaidRender.PendingCount==0&&now!=null&&now.Picture.Key!=null&&!String.Equals(now.Picture.Key,keyBefore,StringComparison.Ordinal);
            },8000);
            PumpFor(window,300);
            MermaidElement after=Diagram(window,fence.Opener);
            // The engine renders once per key, and the whole probe shares one SVG cache: when the other mode
            // has already rendered this source at this size, the new key is a cache hit and no engine render
            // follows. That is the cache working, not a stale picture, so the check is the new key and its
            // ready SVG; a second render for one key would be the fault, and DiagRequests never repeats a key.
            bool ready=after!=null&&State(window,fence.Opener,fence.Closer)==MermaidRender.Ready;
            Check("m7 "+mode+": a font-size change makes a new picture",rendered&&ready&&MermaidEngine.DiagRenders-renders<=1,
                "newKey="+rendered+" ready="+ready+" renders +"+(MermaidEngine.DiagRenders-renders)+" requests +"+(MermaidRender.DiagRequests-requests)+
                " oldKey=\""+Clip(keyBefore)+"\" key=\""+Clip(after==null?null:after.Picture.Key)+"\"");
            // The engine draws at the new font size, so the picture's own height changes: that is the zoom taking
            // effect. The block stays drawn and folded over its two source lines.
            Check("m7 "+mode+": the picture stays, folded, and is redrawn at the new size",
                after!=null&&Shown(window,fence.Opener)&&Folded(window,fence.Opener,fence.Closer)&&Math.Abs(after.Picture.Height-heightBefore)>0.5,
                "height="+N(heightBefore)+" -> "+N(after==null?0:after.Picture.Height)+" picture="+Shown(window,fence.Opener)+
                " folded="+Folded(window,fence.Opener,fence.Closer)+" "+EngineState());
        }catch(Exception ex){
            Check("m7 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m8 a wide diagram in a narrow view
    static string WideDiagram(){
        var builder=new StringBuilder("앞 문단\n\n```mermaid\nflowchart LR\n");
        for(int n=1;n<=20;n++)builder.Append("  A").Append(n).Append("[노드").Append(n).Append("] --> B").Append(n).Append("[연결").Append(n).Append("]\n");
        for(int n=1;n<20;n++)builder.Append("  B").Append(n).Append(" --> A").Append(n+1).Append('\n');
        builder.Append("```\n\n뒤 문단\n");
        return builder.ToString();
    }
    static void Wide(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(WideDiagram(),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=1){Check("m8 "+mode+": a block is read from the document",false,"fences="+fences.Count);return;}
            Fence fence=fences[0];
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            bool first=WaitFor(window,delegate { return Shown(window,fence.Opener)&&MermaidRender.PendingCount==0; },10000);
            MermaidElement wide=Diagram(window,fence.Opener);
            if(!first||wide==null){Check("m8 "+mode+": the wide picture is drawn first",false,EngineState());return;}
            double viewWidth=window.source.TextArea.TextView.ActualWidth;
            double drawn=wide.Picture.Width*wide.Scale;
            Check("m8 "+mode+": a wide picture is scaled into the view",drawn<=viewWidth+1&&wide.Scale<=1,
                "picture="+N(wide.Picture.Width)+" scale="+N(wide.Scale)+" drawn="+N(drawn)+" view="+N(viewWidth));
            double scaleBefore=wide.Scale;
            // A 40% narrower window: the paragraph width shrinks, so Format must scale the picture again.
            window.Width=window.Width*0.6;
            Pump(window);
            window.UpdateLayout();
            Pump(window);
            bool rescaled=WaitFor(window,delegate {
                MermaidElement now=Diagram(window,fence.Opener);
                return now!=null&&now.Scale<scaleBefore&&now.Picture.Width*now.Scale<=window.source.TextArea.TextView.ActualWidth+1;
            },5000);
            MermaidElement narrow=Diagram(window,fence.Opener);
            Check("m8 "+mode+": narrowing the window rescales the picture and keeps it folded",
                rescaled&&narrow!=null&&Shown(window,fence.Opener)&&Folded(window,fence.Opener,fence.Closer),
                "scale="+N(scaleBefore)+" -> "+N(narrow==null?0:narrow.Scale)+" drawn="+N(narrow==null?0:narrow.Picture.Width*narrow.Scale)+
                " view="+N(window.source.TextArea.TextView.ActualWidth)+" "+EngineState());
        }catch(Exception ex){
            Check("m8 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m9 a source the engine refuses
    static void Refusal(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            // 60 short lines whose labels are long: past MermaidRender.SourceLimit (20000) characters but well
            // inside MermaidRender.BlockLines (400), so the block is read and its source is refused as too large.
            var builder=new StringBuilder("앞 문단\n\n```mermaid\nflowchart TD\n");
            var label=new StringBuilder();
            while(label.Length<400)label.Append("아주긴원문");
            for(int n=1;n<=60;n++)builder.Append("  N").Append(n).Append('[').Append(label).Append("] --> M").Append(n).Append('\n');
            builder.Append("```\n\n입력 줄\n");
            window=Open(builder.ToString(),live);
            long requests=MermaidRender.DiagRequests,renders=MermaidEngine.DiagRenders;
            var openers=MermaidOpeners(Doc(window));
            int opener=openers.Count>0?openers[0]:0;
            int outside=OutsideLine(Doc(window),MermaidFences(Doc(window)));
            Caret(window,outside>0?outside:1,1);
            WaitFor(window,delegate { return MermaidRender.PendingCount==0; },3000);
            PumpFor(window,400);
            bool code=opener>0&&!Shown(window,opener)&&OwnLine(window,opener+1);
            Check("m9 "+mode+": a 25000-character block stays a code block",code,
                "picture="+(opener>0&&Shown(window,opener))+" ownSource="+(opener>0&&OwnLine(window,opener+1))+" opener="+opener+
                " requests +"+(MermaidRender.DiagRequests-requests)+" last="+(MermaidRender.LastError??"-"));
            // Typing elsewhere must not stall behind the refused block.
            int line=Doc(window).LineCount;
            CaretEnd(window,line);
            Stopwatch watch=Stopwatch.StartNew();
            for(int i=0;i<50;i++){
                window.source.TextArea.PerformTextInput("q");
                window.source.UpdateLayout();
            }
            watch.Stop();
            double perKey=watch.Elapsed.TotalMilliseconds/50.0;
            bool typed=LineText(window,line).IndexOf("qqqqq",StringComparison.Ordinal)>=0;
            // A refused block that made typing elsewhere wait for it would be far past any single keystroke here;
            // the measured value is in the INFO line either way.
            Check("m9 "+mode+": typing 50 characters elsewhere stays responsive",typed&&perKey<300.0,
                N(perKey)+" ms per keystroke, typed="+typed+" renders +"+(MermaidEngine.DiagRenders-renders)+" "+EngineState());
            Note("m9 "+mode+": "+N(perKey)+" ms per keystroke beside a refused 25000-character block");
        }catch(Exception ex){
            Check("m9 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m10 typing inside a 300-line block
    static void BigTyping(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            var builder=new StringBuilder("앞 문단\n\n```mermaid\nflowchart TD\n");
            for(int n=1;n<=300;n++)builder.Append("  N").Append(n).Append("[노드").Append(n).Append("] --> N").Append(n+1).Append("[다음]\n");
            builder.Append("```\n\n뒤 문단\n");
            window=Open(builder.ToString(),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=1){Check("m10 "+mode+": a 300-line block is read from the document",false,"fences="+fences.Count);return;}
            Fence fence=fences[0];
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            // A fresh window has nothing pending yet, so "pending==0" would return before the block is even
            // asked for. Wait for the block's own first render instead (a failed source counts: it is asked
            // once and remembered).
            WaitFor(window,delegate {
                int state=State(window,fence.Opener,fence.Closer);
                return state==MermaidRender.Ready||state==MermaidRender.Failed;
            },15000);
            WaitFor(window,delegate { return MermaidRender.PendingCount==0; },5000);
            PumpFor(window,300);
            // The caret goes into the block first, and the baseline is taken after it. The caret is set on
            // the editor directly (Caret() would pump the dispatcher to idle, which is exactly the pass this
            // check must not let run before the baseline); a layout pass only.
            DocumentLine typingLine=Doc(window).GetLineByNumber(fence.Opener+150);
            window.source.TextArea.Caret.Line=typingLine.LineNumber;
            window.source.TextArea.Caret.Column=typingLine.Length+1;
            window.UpdateLayout();
            window.source.TextArea.TextView.EnsureVisualLines();
            long requests=MermaidRender.DiagRequests,renders=MermaidEngine.DiagRenders;
            Stopwatch watch=Stopwatch.StartNew();
            for(int i=0;i<100;i++){
                window.source.TextArea.PerformTextInput("q");
                window.source.UpdateLayout();
            }
            watch.Stop();
            double perKey=watch.Elapsed.TotalMilliseconds/100.0;
            // Read before any pump: a dispatcher turn would let the 300 ms idle pass run. Nothing in the loop
            // pumps one (PerformTextInput and UpdateLayout are synchronous), so a request here would be one
            // the keystroke path itself made, which is the fault this check names.
            long during=MermaidRender.DiagRequests,rendersDuring=MermaidEngine.DiagRenders;
            Check("m10 "+mode+": 100 keystrokes inside a big block send no request",during==requests&&rendersDuring==renders,
                "requests +"+(during-requests)+" renders +"+(rendersDuring-renders)+" "+EngineState());
            Note("m10 "+mode+": "+N(perKey)+" ms per keystroke in the block (typing + layout, 100 characters)");
            // The idle pass 300 ms after the last edit may request the edited source once, while the caret is
            // still inside: MermaidSync asks for any block whose key the cache does not know and does not ask
            // where the caret is (MermaidRender.cs:574-578). That is the intended pre-render, so at most one
            // request is expected here and it is counted rather than held against the product.
            PumpFor(window,900);
            long afterIdle=MermaidRender.DiagRequests;
            Note("m10 "+mode+": the idle pass with the caret still inside: requests +"+(afterIdle-during)+
                ", renders +"+(MermaidEngine.DiagRenders-rendersDuring)+" (the source may be rendered while the caret shows it)");
            // Leaving the block: the block comes back from its picture (or its code view when the edited
            // source failed) and asks for at most one more source.
            CaretEnd(window,Doc(window).LineCount);
            bool after=WaitFor(window,delegate { return MermaidRender.PendingCount==0; },10000);
            PumpFor(window,300);
            long late=MermaidRender.DiagRequests-afterIdle;
            Check("m10 "+mode+": the idle pass and the leave ask for the edited source at most once each",
                after&&afterIdle-during<=1&&late<=1,
                "settled="+after+" idle requests +"+(afterIdle-during)+" leave requests +"+late+
                " picture="+Shown(window,fence.Opener)+" last="+(MermaidRender.LastError??"-"));
        }catch(Exception ex){
            Check("m10 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m11 a block first seen from its middle
    // A viewport that jumps straight into the middle of a block never lays out its opener, so the block
    // stays unknown and its source stays code (MermaidSeeTop in MermaidRender.cs). The document puts the
    // block far below the first screen; one direct scroll then lands in the middle of its source, with no
    // layout at the opener in between. A picture taller than the block keeps the folded state measurable.
    // The lead-in is 100 paragraphs (about five viewports) so the block sits past the idle pre-render
    // pass's reach (two viewports below the view, MermaidPreRenderNow): the case must judge the jump
    // path, not a pass that already prepared the block.
    static string MidBlockText(string tag){
        var builder=new StringBuilder();
        for(int n=1;n<=100;n++)builder.Append("앞 문단 ").Append(n).Append(": 첫 화면 아래의 설명 글.\n\n");
        builder.Append("```mermaid\nflowchart TD\n");
        for(int n=1;n<=34;n++)builder.Append("  N").Append(n).Append("[단계").Append(n).Append(' ').Append(tag).Append("] --> N").Append(n+1).Append("[다음").Append(n).Append("]\n");
        builder.Append("```\n\n");
        for(int n=1;n<=20;n++)builder.Append("뒤 문단 ").Append(n).Append(": 블록 아래의 설명 글.\n\n");
        return builder.ToString();
    }
    static void MidBlock(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(MidBlockText(mode),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=1){Check("m11 "+mode+": a mermaid block is read from the document",false,"fences="+fences.Count);return;}
            Fence fence=fences[0];
            Caret(window,1,1);
            PumpFor(window,150);
            // The case is about a block nobody has laid out yet: if the open screen already reached its
            // opener the scroll below would judge the ordinary path, and the check would be vacuous.
            if(Records(window)!=0||LineShown(window,fence.Opener)){
                Check("m11 "+mode+": a block first seen from its middle is drawn",false,
                    "the opener was laid out at open, so the case is not judged: records="+Records(window)+
                    " openerShown="+LineShown(window,fence.Opener)+" opener="+fence.Opener+" lines="+Doc(window).LineCount);
                return;
            }
            TextView view=window.source.TextArea.TextView;
            int middle=fence.Opener+(fence.Closer-fence.Opener)/2;
            // The target is re-read after each attempt: the first measure replaces the default heights of
            // the lines it builds, which moves the middle line's own top. Each attempt stays inside the
            // block (or above it, never with the opener on screen), so the opener is never laid out.
            int topLine=0;
            bool inside=false;
            for(int attempt=0;attempt<4&&!inside;attempt++){
                double top=view.GetVisualTopByDocumentLine(middle);
                window.source.ScrollToVerticalOffset(Math.Max(0,top));
                Pump(window);
                if(LineShown(window,fence.Opener))break;
                topLine=view.GetDocumentLineByVisualTop(view.VerticalOffset).LineNumber;
                inside=topLine>fence.Opener&&topLine<fence.Closer;
            }
            if(!inside){
                Check("m11 "+mode+": a block first seen from its middle is drawn",false,
                    "the scroll did not land inside the block: topLine="+topLine+" block="+fence.Opener+"-"+fence.Closer+
                    " openerShown="+LineShown(window,fence.Opener)+" offset="+N(view.VerticalOffset));
                return;
            }
            // The walk and the request are made by the product; the wait covers the 300 ms idle count, the
            // engine render and the fold (the same 300 ms + render the other cases wait out).
            long mark=clock.ElapsedMilliseconds;
            bool known=WaitFor(window,delegate {
                return Records(window)==1&&MermaidRender.PendingCount==0&&
                    State(window,fence.Opener,fence.Closer)==MermaidRender.Ready&&Folds(window)==1;
            },3000);
            long waited=clock.ElapsedMilliseconds-mark;
            // Snapshot at the verdict, before the scroll below lays the opener out: that scroll is the
            // ordinary path (it would draw even a block the fix missed), so the catch is `known` here.
            int recordsThen=Records(window),foldsThen=Folds(window);
            int stateThen=State(window,fence.Opener,fence.Closer);
            // Then the opener is brought into view: the picture must be on its row, still folded.
            ScrollTo(window,fence.Opener);
            bool drawn=WaitFor(window,delegate {return Shown(window,fence.Opener)&&Folded(window,fence.Opener,fence.Closer);},800);
            Check("m11 "+mode+": a block first seen from its middle is drawn",known&&drawn,
                "block="+fence.Opener+"-"+fence.Closer+" topLine="+topLine+" records@wait="+recordsThen+
                " folds@wait="+foldsThen+" state@wait="+StateName(stateThen)+
                " known="+known+" drawn="+drawn+" ownMiddle="+OwnLine(window,middle)+
                " waited="+waited+" ms "+EngineState());
        }catch(Exception ex){
            Check("m11 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m12 a press on a tall picture
    // A picture taller than the viewport is clicked. The caret goes into the block's first source line;
    // the unfold that follows must keep the opener where it was on screen. Before the fix the reveal ran
    // while the block was still folded, where the opener's row is the whole picture, so the view scrolled
    // as if the caret sat at the picture's bottom and the source line ended above the viewport.
    static string TallDiagramText(string tag){
        var builder=new StringBuilder("머리 문단: 그림 위의 설명 글.\n\n둘째 문단: 클릭할 그림의 바로 위.\n\n");
        builder.Append("```mermaid\nflowchart TD\n");
        for(int n=1;n<=18;n++)
            builder.Append("  N").Append(n).Append("[단계").Append(n).Append(' ').Append(tag).Append("] --> N").Append(n+1).Append("[다음").Append(n).Append("]\n");
        builder.Append("```\n\n");
        for(int n=1;n<=8;n++)builder.Append("아래 문단 ").Append(n).Append(": 그림 아래의 설명 글.\n\n");
        return builder.ToString();
    }
    static void ClickJump(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(TallDiagramText(mode),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=1){Check("m12 "+mode+": one tall mermaid block is read from the document",false,"fences="+fences.Count);return;}
            Fence fence=fences[0];
            int outside=OutsideLine(Doc(window),fences);
            Caret(window,outside>0?outside:1,1);
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            bool drawn=WaitFor(window,delegate { return Shown(window,fence.Opener)&&MermaidRender.PendingCount==0; },5000);
            MermaidElement diagram=Diagram(window,fence.Opener);
            if(!drawn||diagram==null){Check("m12 "+mode+": the tall picture is drawn first",false,"drawn="+drawn+" "+EngineState());return;}
            PumpFor(window,150);
            TextView view=window.source.TextArea.TextView;
            VisualLine row=view.GetVisualLine(fence.Opener);
            if(row==null||row.IsDisposed){Check("m12 "+mode+": the picture row is laid out",false,"row="+(row==null)+" "+EngineState());return;}
            if(window.livePreview==null){Check("m12 "+mode+": the session is attached",false,"session=null");return;}
            double viewport=window.source.ViewportHeight;
            if(viewport<=0)viewport=view.ActualHeight;
            double height=diagram.Picture.Height*diagram.Scale,width=diagram.Picture.Width*diagram.Scale;
            TextLine line=row.GetTextLine(diagram.VisualColumn);
            double slotLeft=row.GetTextLineVisualXPosition(line,diagram.VisualColumn);
            double left=slotLeft+Math.Max(0,(diagram.Slot-width)/2);
            double openerTop=view.GetVisualTopByDocumentLine(fence.Opener);
            double openerBefore=openerTop-view.VerticalOffset;
            // A press inside the picture and inside the viewport, where a reader clicks a tall one.
            double documentY=openerTop+Math.Max(24,Math.Min(height/2,viewport/3));
            Point press=new Point(left+width/2-view.HorizontalOffset,documentY-view.VerticalOffset);
            bool onView=press.Y>=0&&press.Y<viewport&&press.X>=0&&press.X<view.ActualWidth;
            // The press path the editor runs: the reveal is frozen for the press (TextAreaPreviewMouseDown),
            // the product's press places the caret, and the release applies the reveal.
            long mark=clock.ElapsedMilliseconds;
            bool pressed=false;
            window.livePreview.BeginPress();
            try{pressed=window.livePreview.MermaidPress(press);}
            finally{window.livePreview.EndPress();}
            bool unfolded=WaitFor(window,delegate { return !Shown(window,fence.Opener)&&OwnLine(window,fence.Opener+1); },1500);
            PumpFor(window,120);
            int caretLine=window.source.TextArea.Caret.Line;
            bool caretOnFirst=caretLine==fence.Opener+1;
            double caretTop=caretLine>=1&&caretLine<=Doc(window).LineCount?view.GetVisualTopByDocumentLine(caretLine)-view.VerticalOffset:double.NaN;
            VisualLine caretRow=caretLine>=1&&LineShown(window,caretLine)?view.GetVisualLine(caretLine):null;
            bool caretInView=caretRow!=null&&!caretRow.IsDisposed&&caretTop>=-0.5&&caretTop+caretRow.Height<=viewport+0.5;
            double openerAfter=view.GetVisualTopByDocumentLine(fence.Opener)-view.VerticalOffset;
            double lineHeight=view.DefaultLineHeight;
            bool anchored=Math.Abs(openerAfter-openerBefore)<=(lineHeight>0?lineHeight:1);
            bool tall=height>viewport;
            // A picture no taller than the viewport would make the case vacuous, so it is named as a
            // failed precondition rather than passing on a small diagram.
            Check("m12 "+mode+": clicking a tall picture keeps its first source line in view",
                tall&&onView&&pressed&&unfolded&&caretOnFirst&&caretInView&&anchored,
                "picture="+N(height)+" viewport="+N(viewport)+" openerY="+N(openerBefore)+"->"+N(openerAfter)+
                " lineHeight="+N(lineHeight)+" caret="+caretLine+" want="+(fence.Opener+1)+" caretY="+N(caretTop)+
                " unfolded="+unfolded+" anchored="+anchored+" caretInView="+caretInView+" tall="+tall+
                " pressed="+pressed+" onView="+onView+" ms="+(clock.ElapsedMilliseconds-mark)+" "+EngineState());
            Note("m12 "+mode+": press=("+N(press.X)+","+N(press.Y)+") block="+fence.Opener+"-"+fence.Closer+" caret="+caretLine);
        }catch(Exception ex){
            Check("m12 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // ------------------------------------------------ m13 idle pre-render below the view
    // A block whose opener was never laid out stays source until the reader scrolls it in: the 300 ms
    // idle count and the render only start then (11-15 user: "scrolling down takes 1-2 s before the code
    // block is a picture"). The idle pre-render pass (MermaidPreRenderNow in MermaidRender.cs) prepares
    // the openers up to two viewports below the view, so the scroll draws them from the cache at once.
    // The document holds six blocks about 1.5 viewports apart; the open screen shows only the first. The
    // pass-off window is the negative control: without the pass the same block stays unanswered.
    static string PreRenderText(int blocks,int filler,string tag){
        var builder=new StringBuilder("머리 문단: 화면 맨 위의 설명 글.\n\n");
        for(int n=1;n<=blocks;n++){
            builder.Append("```mermaid\nflowchart LR\n  A[").Append(tag).Append(n).Append("] --> B[끝]\n```\n\n");
            if(n<blocks)
                for(int line=0;line<filler;line++)
                    builder.Append("사이 글 ").Append(n).Append('-').Append(line).Append(": 다음 블록까지의 설명 글.\n");
        }
        return builder.ToString();
    }
    // Every block's opener top, so the range the pass covers is read from the view and not from a line
    // count the case assumes. near = below the view bottom and inside the two-viewport range the pass
    // walks; far = past three viewports below the view, which the pass must not touch.
    static void SplitRange(MainWindow window,List<Fence> fences,double viewport,List<Fence> near,List<Fence> far){
        TextView view=window.source.TextArea.TextView;
        double top=view.VerticalOffset,bottom=top+viewport;
        foreach(Fence fence in fences){
            double openerTop=view.GetVisualTopByDocumentLine(fence.Opener);
            if(openerTop<=bottom)continue;
            if(openerTop<=bottom+2*viewport)near.Add(fence);
            else if(openerTop>bottom+3*viewport)far.Add(fence);
        }
    }
    static void PreRender(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(PreRenderText(6,46,"미리"+mode),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=6){Check("m13 "+mode+": six mermaid blocks are read from the document",false,"fences="+fences.Count);return;}
            Caret(window,1,1);
            TextView view=window.source.TextArea.TextView;
            double viewport=window.source.ViewportHeight;
            if(viewport<=0)viewport=view.ActualHeight;
            if(viewport<=0){Check("m13 "+mode+": the window has a viewport",false,"viewport="+N(viewport));return;}
            // The open screen shows the first block only: the ones this case judges were never laid out,
            // so nothing but the pass can have prepared them.
            PumpFor(window,350);
            var near=new List<Fence>();
            var far=new List<Fence>();
            SplitRange(window,fences,viewport,near,far);
            Fence nearest=null;
            double nearestTop=double.MaxValue,bottom=view.VerticalOffset+viewport;
            foreach(Fence fence in near){
                double openerTop=view.GetVisualTopByDocumentLine(fence.Opener);
                if(openerTop<nearestTop){nearestTop=openerTop;nearest=fence;}
            }
            bool laidOut=near.Count>0&&nearest!=null&&LineShown(window,nearest.Opener);
            bool scrollShows=nearest!=null&&nearestTop-bottom<viewport;
            if(near.Count==0||far.Count==0||laidOut||!scrollShows){
                Check("m13 "+mode+": the fixture puts blocks inside and past the pre-render range",false,
                    "near="+near.Count+" far="+far.Count+" nearestShown="+laidOut+" nearestY="+N(nearestTop-bottom)+
                    " viewport="+N(viewport)+" lines="+Doc(window).LineCount);
                return;
            }
            // Idle time only: no scroll, no caret move. The pass and the renders it starts must make every
            // block in the near range Ready on their own (the cache, not a layout, is what is judged). The
            // counters are cumulative; the states are the verdict.
            Stopwatch watch=Stopwatch.StartNew();
            bool ready=WaitFor(window,delegate {
                foreach(Fence fence in near)if(State(window,fence.Opener,fence.Closer)!=MermaidRender.Ready)return false;
                return MermaidRender.PendingCount==0;
            },2500);
            string states="";
            bool farUntouched=true;
            foreach(Fence fence in near)states+=" near"+fence.Opener+":"+StateName(State(window,fence.Opener,fence.Closer));
            foreach(Fence fence in far){
                int state=State(window,fence.Opener,fence.Closer);
                states+=" far"+fence.Opener+":"+StateName(state);
                if(state!=MermaidRender.Unknown)farUntouched=false;
            }
            Check("m13 "+mode+": blocks just below the view are asked for in idle time, blocks far below are not",
                ready&&farUntouched,
                "near="+near.Count+" far="+far.Count+" ready="+ready+" farUntouched="+farUntouched+
                " took="+watch.ElapsedMilliseconds+" ms prePasses="+MermaidRender.DiagPrePasses+
                " linesWalked="+MermaidRender.DiagPreLines+" blocksPrepared="+MermaidRender.DiagPreBlocks+states+" "+EngineState());
            // The scroll a reader makes, through the probe's own one-pump path. The block's picture is
            // already cached, so it must be on its row and no engine render may follow: a block whose
            // opener was never prepared would instead request and render here, which is the 1-2 s the
            // user reported. The pump is one dispatcher frame, the same "no wait" the case demands.
            long renders=MermaidEngine.DiagRenders;
            ScrollTo(window,nearest.Opener);
            bool atOnce=Shown(window,nearest.Opener)&&Folded(window,nearest.Opener,nearest.Closer);
            Check("m13 "+mode+": scrolled to the block it is drawn in one pump, with no render",atOnce&&MermaidEngine.DiagRenders==renders,
                "opener="+nearest.Opener+" shown="+Shown(window,nearest.Opener)+" folded="+Folded(window,nearest.Opener,nearest.Closer)+
                " renders +"+(MermaidEngine.DiagRenders-renders)+" state="+StateName(State(window,nearest.Opener,nearest.Closer))+" "+EngineState());
            PumpFor(window,300);
        }catch(Exception ex){
            Check("m13 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
        // Negative control: the same fixture with the pass off. Nothing else changes, so the near block
        // staying Unknown (and its opener a code block after the scroll) is the pass's own work measured.
        MainWindow control=null;
        try{
            MermaidRender.PreRenderOff=true;
            control=Open(PreRenderText(6,46,"대조"+mode),live);
            var fences=MermaidFences(Doc(control));
            if(fences.Count!=6){Check("m13 "+mode+" control: six mermaid blocks are read from the document",false,"fences="+fences.Count);return;}
            Caret(control,1,1);
            TextView view=control.source.TextArea.TextView;
            double viewport=control.source.ViewportHeight;
            if(viewport<=0)viewport=view.ActualHeight;
            PumpFor(control,350);
            var near=new List<Fence>();
            var far=new List<Fence>();
            SplitRange(control,fences,viewport,near,far);
            Fence nearest=null;
            double nearestTop=double.MaxValue,bottom=view.VerticalOffset+viewport;
            foreach(Fence fence in near){
                double openerTop=view.GetVisualTopByDocumentLine(fence.Opener);
                if(openerTop<nearestTop){nearestTop=openerTop;nearest=fence;}
            }
            if(nearest==null||near.Count==0){Check("m13 "+mode+" control: the fixture puts a block inside the range",false,"near="+near.Count);return;}
            long preBefore=MermaidRender.DiagPreBlocks,requests=MermaidRender.DiagRequests;
            PumpFor(control,900);
            int before=State(control,nearest.Opener,nearest.Closer);
            bool passOff=MermaidRender.DiagPreBlocks==preBefore&&!LineShown(control,nearest.Opener);
            // The same one-pump scroll as the case above. With no pass the opener was never asked for, so
            // here it is laid out as its own code, the request starts only now and the picture arrives a
            // render later: the 1-2 s the user saw, and the exact difference the pass makes.
            ScrollTo(control,nearest.Opener);
            bool notAtOnce=!Shown(control,nearest.Opener)&&LineShown(control,nearest.Opener);
            bool late=WaitFor(control,delegate { return Shown(control,nearest.Opener); },3000);
            Check("m13 "+mode+" control: with the pass off the same block is code after the scroll and needs a render",
                passOff&&before==MermaidRender.Unknown&&notAtOnce&&late&&MermaidRender.DiagRequests>requests,
                "opener="+nearest.Opener+" stateBefore="+StateName(before)+" passOff="+passOff+
                " shownAtOnce="+Shown(control,nearest.Opener)+" openerShown="+LineShown(control,nearest.Opener)+
                " late="+late+" requests +"+(MermaidRender.DiagRequests-requests)+" "+EngineState());
        }catch(Exception ex){
            Check("m13 "+mode+" control: no exception",false,ex.ToString());
        }finally{
            MermaidRender.PreRenderOff=false;
            CloseWindow(control);
        }
    }

    // ------------------------------------------------ m14 a caret move that refolds a block
    // The bug (user video 11-15, focus mode on and off): the caret sat in an unfolded block A's source,
    // a press took it out to a line below A, A folded back into its tall picture, and every line below A
    // moved down by the height difference: the pressed line jumped away from the mouse. The same happens
    // when the press opens block B below while A refolds. MermaidPress / MermaidPressSettle anchored only
    // the opener of the block being entered. The fix (MermaidRender.cs) anchors the caret's own line as
    // the old layout draws it, before the first flip, and restores that screen height after the layout:
    // MermaidPress is its press case, not a separate rule.
    // The user's shape (11-15 video): a short source whose picture is tall. The chain is written several
    // nodes per line, so the source lays out in about one line height while the folded picture is taller
    // than a viewport third: the refold is exactly what moves the text below it.
    static void AnchorChain(StringBuilder builder,char node,string label,string tag){
        builder.Append("```mermaid\nflowchart TD\n");
        for(int n=1;n<=18;n++){
            builder.Append("  ").Append(node).Append(n).Append('[').Append(label).Append(n).Append(' ').Append(tag).Append(']');
            if(n<18)builder.Append(" --> ").Append(node).Append(n+1);
            builder.Append(n%6==0||n==18?"\n":"; ");
        }
        builder.Append("```\n\n");
    }
    static string AnchorText(string tag){
        var builder=new StringBuilder("머리 문단: 첫 그림 위의 설명 글.\n\n");
        AnchorChain(builder,'A',"단계",tag);
        for(int n=1;n<=3;n++)builder.Append("사이 문단 ").Append(n).Append(": 첫 그림 아래의 설명 글.\n\n");
        AnchorChain(builder,'B',"구간",tag);
        for(int n=1;n<=6;n++)builder.Append("아래 문단 ").Append(n).Append(": 둘째 그림 아래의 설명 글.\n\n");
        return builder.ToString();
    }
    // A press inside a drawn picture, in view coordinates (m12 builds it the same way), the row's
    // document top, and whether the point is inside the view's rectangle. nearTop picks the picture's
    // upper part, where a row that starts low in the view still has its click point on screen.
    static bool PicturePress(MainWindow window,Fence fence,double viewport,bool nearTop,out Point press,out double top){
        press=new Point(0,0);top=0;
        TextView view=window.source.TextArea.TextView;
        VisualLine row=view.GetVisualLine(fence.Opener);
        MermaidElement diagram=Diagram(window,fence.Opener);
        if(row==null||row.IsDisposed||diagram==null||diagram.Picture==null)return false;
        double height=diagram.Picture.Height*diagram.Scale,width=diagram.Picture.Width*diagram.Scale;
        TextLine line=row.GetTextLine(diagram.VisualColumn);
        double slotLeft=row.GetTextLineVisualXPosition(line,diagram.VisualColumn);
        double left=slotLeft+Math.Max(0,(diagram.Slot-width)/2);
        top=view.GetVisualTopByDocumentLine(fence.Opener);
        double inside=nearTop?Math.Max(24,Math.Min(height/4,viewport/8)):Math.Max(24,Math.Min(height/2,viewport/3));
        double documentY=top+inside;
        press=new Point(left+width/2-view.HorizontalOffset,documentY-view.VerticalOffset);
        return press.Y>=0&&press.Y<viewport&&press.X>=0&&press.X<view.ActualWidth;
    }
    // The product's press path (the freeze TextAreaPreviewMouseDown makes, the caret the press asks for,
    // the release that applies the reveal and flips the folds): m12 uses it for a picture, and this is
    // the same path for a line that is not a picture, where the editor itself places the caret.
    static void TextPress(MainWindow window,int line,int column){
        TextDocument document=Doc(window);
        if(line<1)line=1;
        if(line>document.LineCount)line=document.LineCount;
        DocumentLine target=document.GetLineByNumber(line);
        int offset=target.Offset+(column<1?0:Math.Min(column,target.Length));
        window.livePreview.BeginPress();
        try{window.source.TextArea.ClearSelection();window.source.TextArea.Caret.Offset=offset;}
        finally{window.livePreview.EndPress();}
    }
    static bool PressPicture(MainWindow window,Point press){
        bool pressed;
        window.livePreview.BeginPress();
        try{pressed=window.livePreview.MermaidPress(press);}
        finally{window.livePreview.EndPress();}
        return pressed;
    }
    // The row is laid out, spans its whole line height and sits inside the viewport.
    static bool RowInView(MainWindow window,int line,double viewport,out double top){
        top=double.NaN;
        TextView view=window.source.TextArea.TextView;
        VisualLine row=view.GetVisualLine(line);
        if(row==null||row.IsDisposed||row.FirstDocumentLine.LineNumber!=line)return false;
        top=view.GetVisualTopByDocumentLine(line)-view.VerticalOffset;
        return top>=-0.5&&top+row.Height<=viewport+0.5;
    }
    // The screen y of a line's top in the layout, whether or not the line has a row of its own: the
    // height tree positions every line, and a line hidden in a fold reads as the fold's top. The layout
    // is forced first so a press's flip is always measured after it was laid out.
    static double ScreenTop(MainWindow window,int line){
        TextView view=window.source.TextArea.TextView;
        if(line<1||line>Doc(window).LineCount)return double.NaN;
        view.EnsureVisualLines();
        return view.GetVisualTopByDocumentLine(line)-view.VerticalOffset;
    }
    // Case 1: the caret is in A's source, the press lands on a paragraph below A. A refolds and the
    // pressed line must stay where it was, within one line height; the caret line must be in view.
    static void AnchorPressBelow(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(AnchorText(mode),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=2){Check("m14 "+mode+": two tall mermaid blocks are read from the document",false,"fences="+fences.Count);return;}
            Fence a=fences[0],b=fences[1];
            TextView view=window.source.TextArea.TextView;
            Caret(window,1,1);
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            double viewport=window.source.ViewportHeight;
            if(viewport<=0)viewport=view.ActualHeight;
            if(!WaitFor(window,delegate { return Shown(window,a.Opener)&&MermaidRender.PendingCount==0; },5000)){
                Check("m14 "+mode+": the first picture is drawn first",false,EngineState());return;
            }
            PumpFor(window,150);
            // The caret enters A through the product's press (the m12 path). A unfolds, and the caret
            // sits on its first source line: that is the state the user's video starts from.
            long mark=clock.ElapsedMilliseconds;
            Point press;
            double pictureTop;
            if(!PicturePress(window,a,viewport,false,out press,out pictureTop)){
                Check("m14 "+mode+": the first picture is on screen for the press",false,
                    "pictureY="+N(pictureTop-view.VerticalOffset)+" viewport="+N(viewport)+" "+EngineState());return;
            }
            bool pressed=PressPicture(window,press);
            WaitFor(window,delegate { return !Shown(window,a.Opener)&&OwnLine(window,a.Opener+1); },1500);
            PumpFor(window,150);
            bool unfolded=pressed&&!Shown(window,a.Opener)&&OwnLine(window,a.Opener+1)&&window.source.TextArea.Caret.Line==a.Opener+1;
            // A paragraph below A, on screen with room around it: the line the user clicks next.
            int clicked=0;
            double before=double.NaN;
            for(int number=a.Closer+1;number<b.Opener;number++){
                string text=LineText(window,number);
                if(text.Trim().Length==0)continue;
                double top=ScreenTop(window,number);
                if(double.IsNaN(top)||top<40||top>viewport-80)continue;
                clicked=number;before=top;break;
            }
            double lineHeight=view.DefaultLineHeight;
            if(!unfolded||clicked==0){
                Check("m14 "+mode+": the caret reaches the first block's source before the judged press",false,
                    "pressed="+pressed+" unfolded="+unfolded+" caret="+window.source.TextArea.Caret.Line+" want="+(a.Opener+1)+
                    " clicked="+clicked+" before="+N(before)+" block="+a.Opener+"-"+a.Closer+" "+EngineState());
                return;
            }
            TextPress(window,clicked,2);
            bool refolded=WaitFor(window,delegate { return Shown(window,a.Opener)&&Folded(window,a.Opener,a.Closer); },1500);
            PumpFor(window,150);
            double after=ScreenTop(window,clicked);
            bool inView=RowInView(window,clicked,viewport,out after);
            double moved=double.IsNaN(after)||double.IsNaN(before)?double.MaxValue:Math.Abs(after-before);
            int caretLine=window.source.TextArea.Caret.Line;
            Check("m14 "+mode+": a press below an unfolded block keeps the pressed line on screen",
                refolded&&clicked==caretLine&&inView&&moved<=(lineHeight>0?lineHeight:1),
                "block="+a.Opener+"-"+a.Closer+" clicked="+clicked+" caret="+caretLine+" y="+N(before)+"->"+N(after)+
                " moved="+N(moved)+" lineHeight="+N(lineHeight)+" refolded="+refolded+" inView="+inView+
                " ms="+(clock.ElapsedMilliseconds-mark)+" "+EngineState());
            Note("m14 "+mode+": press below A at y="+N(before)+", caret "+caretLine+", "+(clock.ElapsedMilliseconds-mark)+" ms");
        }catch(Exception ex){
            Check("m14 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }
    // Case 2: the caret is in A's source and the press lands on B's picture below. A refolds and B
    // unfolds in one move; B's first source line must stay within one line height of the picture top
    // the user clicked.
    static void AnchorPressPicture(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            window=Open(AnchorText(mode),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=2){Check("m14 "+mode+": two tall mermaid blocks are read from the document",false,"fences="+fences.Count);return;}
            Fence a=fences[0],b=fences[1];
            TextView view=window.source.TextArea.TextView;
            Caret(window,1,1);
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            double viewport=window.source.ViewportHeight;
            if(viewport<=0)viewport=view.ActualHeight;
            if(!WaitFor(window,delegate { return Shown(window,a.Opener)&&MermaidRender.PendingCount==0; },5000)){
                Check("m14 "+mode+": the first picture is drawn first",false,EngineState());return;
            }
            PumpFor(window,150);
            long mark=clock.ElapsedMilliseconds;
            Point press;
            double pictureTop;
            if(!PicturePress(window,a,viewport,false,out press,out pictureTop)){
                Check("m14 "+mode+": the first picture is on screen for the press",false,"pictureY="+N(pictureTop-view.VerticalOffset)+" "+EngineState());return;
            }
            bool pressed=PressPicture(window,press);
            bool unfolded=WaitFor(window,delegate { return !Shown(window,a.Opener)&&OwnLine(window,a.Opener+1); },1500)?true:pressed&&!Shown(window,a.Opener);
            PumpFor(window,200);
            // B is below and still drawn while the caret is in A. Its picture row must be on screen for
            // the second press; the case needs both pictures to exist for the refold to be measurable.
            bool bDrawn=WaitFor(window,delegate { return Shown(window,b.Opener)&&Folded(window,b.Opener,b.Closer); },2500);
            double bTop=0;
            Point bPress=new Point(0,0);
            bool bOnScreen=bDrawn&&PicturePress(window,b,viewport,true,out bPress,out bTop);
            if(!unfolded||!bOnScreen){
                Check("m14 "+mode+": the fixture keeps the first block open and the second picture on screen",false,
                    "pressed="+pressed+" unfolded="+unfolded+" bDrawn="+bDrawn+" bOnScreen="+bOnScreen+
                    " bY="+N(bTop-view.VerticalOffset)+" viewport="+N(viewport)+" block="+b.Opener+"-"+b.Closer+" "+EngineState());
                return;
            }
            double bPictureY=ScreenTop(window,b.Opener);
            bool second=PressPicture(window,bPress);
            bool flipped=WaitFor(window,delegate { return Shown(window,a.Opener)&&!Shown(window,b.Opener)&&OwnLine(window,b.Opener+1); },1500);
            PumpFor(window,150);
            // The judged line is the opener (B's fence line): the press took it out of the fold, and it
            // must come back at the picture top the user clicked. The ordinary text line height is the
            // tolerance, never the folded row's own (picture) height, which would accept anything.
            double lineHeight=view.DefaultLineHeight;
            double openerY=ScreenTop(window,b.Opener);
            double pictureGap=double.IsNaN(openerY)||double.IsNaN(bPictureY)?double.MaxValue:Math.Abs(openerY-bPictureY);
            double firstSourceY=ScreenTop(window,b.Opener+1);
            int caretLine=window.source.TextArea.Caret.Line;
            bool inView=RowInView(window,b.Opener+1,viewport,out firstSourceY);
            bool aFolded=Shown(window,a.Opener)&&Folded(window,a.Opener,a.Closer);
            bool bOpen=!Shown(window,b.Opener)&&OwnLine(window,b.Opener+1);
            Check("m14 "+mode+": a press on the next picture keeps the opener at the picture top",
                second&&flipped&&aFolded&&bOpen&&caretLine==b.Opener+1&&inView&&pictureGap<=(lineHeight>0?lineHeight:1),
                "A="+a.Opener+"-"+a.Closer+" folded="+aFolded+" B="+b.Opener+"-"+b.Closer+" open="+bOpen+" flipped="+flipped+
                " pictureY="+N(bPictureY)+" openerY="+N(openerY)+" gap="+N(pictureGap)+" sourceY="+N(firstSourceY)+
                " lineHeight="+N(lineHeight)+" caret="+caretLine+" want="+(b.Opener+1)+" inView="+inView+
                " ms="+(clock.ElapsedMilliseconds-mark)+" "+EngineState());
            Note("m14 "+mode+": press on B's picture at y="+N(bPictureY)+", opener "+N(openerY)+", source "+N(firstSourceY)+", caret "+caretLine+", "+(clock.ElapsedMilliseconds-mark)+" ms");
        }catch(Exception ex){
            Check("m14 "+mode+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }
    // The negative control for case 2: with the caret anchor left out, the same press on B's picture
    // moves the opener away from the picture top by more than one text line.
    static void AnchorControlPicture(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            MermaidRender.CaretAnchorOff=true;
            window=Open(AnchorText(mode),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=2){Check("m14 "+mode+" control: two tall mermaid blocks are read from the document",false,"fences="+fences.Count);return;}
            Fence a=fences[0],b=fences[1];
            TextView view=window.source.TextArea.TextView;
            Caret(window,1,1);
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            double viewport=window.source.ViewportHeight;
            if(viewport<=0)viewport=view.ActualHeight;
            if(!WaitFor(window,delegate { return Shown(window,a.Opener)&&MermaidRender.PendingCount==0; },5000)){
                Check("m14 "+mode+" control: the first picture is drawn first",false,EngineState());return;
            }
            PumpFor(window,150);
            // The caret goes into A's source directly (the brief's starting state): with the anchor off
            // the view is not corrected, so a press path for the first step would leave the second
            // picture elsewhere and make this control judge the wrong press.
            Caret(window,a.Opener+1,2);
            bool opened=WaitFor(window,delegate { return !Shown(window,a.Opener)&&OwnLine(window,a.Opener+1)&&OwnLine(window,b.Opener); },2000);
            PumpFor(window,200);
            bool bDrawn=opened&&WaitFor(window,delegate { return Shown(window,b.Opener)&&Folded(window,b.Opener,b.Closer); },2500);
            double bTop=0;
            Point bPress=new Point(0,0);
            bool bOnScreen=bDrawn&&PicturePress(window,b,viewport,true,out bPress,out bTop);
            if(!bOnScreen){
                Check("m14 "+mode+" control: the second picture is on screen for the press",false,
                    "opened="+opened+" bDrawn="+bDrawn+" bY="+N(bTop-view.VerticalOffset)+" "+EngineState());return;
            }
            double lineHeight=view.DefaultLineHeight;
            double bPictureY=ScreenTop(window,b.Opener);
            bool second=PressPicture(window,bPress);
            // The folds themselves are read without requiring the moved opener's row on screen: with no
            // anchor the click's line can end up far below the viewport, which is the very jump judged.
            bool settled=WaitFor(window,delegate { return !Shown(window,b.Opener)&&MermaidRender.PendingCount==0; },1500);
            PumpFor(window,150);
            double openerY=ScreenTop(window,b.Opener);
            double gap=double.IsNaN(openerY)||double.IsNaN(bPictureY)?double.MaxValue:Math.Abs(openerY-bPictureY);
            Check("m14 "+mode+" control: without the anchor the same picture press moves the opener",
                second&&settled&&gap>(lineHeight>0?lineHeight:1),
                "pictureY="+N(bPictureY)+" openerY="+N(openerY)+" gap="+N(gap)+" lineHeight="+N(lineHeight)+
                " opened="+opened+" second="+second+" settled="+settled+" caret="+window.source.TextArea.Caret.Line+" "+EngineState());
        }catch(Exception ex){
            Check("m14 "+mode+" control: no exception",false,ex.ToString());
        }finally{
            MermaidRender.CaretAnchorOff=false;
            CloseWindow(window);
        }
    }
    // The negative control: case 1 again with the caret anchor left out (MermaidRender.CaretAnchorOff),
    // where the pressed line must jump by the folded picture's height difference.
    static void AnchorControl(bool live){
        string mode=Mode(live);
        MainWindow window=null;
        try{
            MermaidRender.CaretAnchorOff=true;
            window=Open(AnchorText(mode),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=2){Check("m14 "+mode+" control: two tall mermaid blocks are read from the document",false,"fences="+fences.Count);return;}
            Fence a=fences[0],b=fences[1];
            TextView view=window.source.TextArea.TextView;
            Caret(window,1,1);
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            double viewport=window.source.ViewportHeight;
            if(viewport<=0)viewport=view.ActualHeight;
            if(!WaitFor(window,delegate { return Shown(window,a.Opener)&&MermaidRender.PendingCount==0; },5000)){
                Check("m14 "+mode+" control: the first picture is drawn first",false,EngineState());return;
            }
            PumpFor(window,150);
            Point press;
            double pictureTop;
            if(!PicturePress(window,a,viewport,false,out press,out pictureTop)){
                Check("m14 "+mode+" control: the first picture is on screen for the press",false,"pictureY="+N(pictureTop-view.VerticalOffset)+" "+EngineState());return;
            }
            bool pressed=PressPicture(window,press);
            WaitFor(window,delegate { return !Shown(window,a.Opener)&&OwnLine(window,a.Opener+1); },1500);
            PumpFor(window,150);
            int clicked=0;
            double before=double.NaN;
            for(int number=a.Closer+1;number<b.Opener;number++){
                string text=LineText(window,number);
                if(text.Trim().Length==0)continue;
                double top=ScreenTop(window,number);
                if(double.IsNaN(top)||top<40||top>viewport-80)continue;
                clicked=number;before=top;break;
            }
            if(!pressed||clicked==0||!OwnLine(window,a.Opener+1)){
                Check("m14 "+mode+" control: the fixture reaches the judged press",false,
                    "pressed="+pressed+" clicked="+clicked+" own="+OwnLine(window,a.Opener+1)+" "+EngineState());return;
            }
            TextPress(window,clicked,2);
            bool refolded=WaitFor(window,delegate { return Shown(window,a.Opener)&&Folded(window,a.Opener,a.Closer); },1500);
            PumpFor(window,150);
            double after=ScreenTop(window,clicked);
            double lineHeight=view.DefaultLineHeight;
            double moved=double.IsNaN(after)||double.IsNaN(before)?double.MaxValue:Math.Abs(after-before);
            Check("m14 "+mode+" control: without the anchor the same press moves the pressed line",
                refolded&&moved>(lineHeight>0?lineHeight:1),
                "clicked="+clicked+" y="+N(before)+"->"+N(after)+" moved="+N(moved)+" lineHeight="+N(lineHeight)+
                " refolded="+refolded+" "+EngineState());
        }catch(Exception ex){
            Check("m14 "+mode+" control: no exception",false,ex.ToString());
        }finally{
            MermaidRender.CaretAnchorOff=false;
            CloseWindow(window);
        }
    }
    static void AnchorJump(bool live){
        string mode=Mode(live);
        Stopwatch caseClock=Stopwatch.StartNew();
        AnchorPressBelow(live);
        AnchorPressPicture(live);
        // The controls cost another window each: skipped when the probe's own bound is close.
        if(clock.ElapsedMilliseconds<BudgetMilliseconds-24000){
            AnchorControl(live);
            AnchorControlPicture(live);
        }
        Note("m14 "+mode+": "+(clock.ElapsedMilliseconds>0?caseClock.ElapsedMilliseconds:0)+" ms for the case");
    }

    // ------------------------------------------------ m15 a press on a picture that starts above the view
    // The bug (user video 11-15 21:06, live view): the viewport started inside a tall picture (its top
    // about 400 px above the view), a press on the lower part opened the source, and the source was
    // entirely above the viewport; the view then jumped up and down again. Then a press on a heading near
    // the top of the view refolded the block, whose source was off screen above, and the view jumped to the
    // document top. Case a: the opener ends at the top of the viewport and the scroll settles once.
    // Case b: the pressed heading stays where it was and the scroll settles once.
    static string OffscreenText(string tag){
        var builder=new StringBuilder("머리 문단: 첫 그림 위의 설명 글.\n\n");
        AnchorChain(builder,'A',"단계",tag);
        for(int n=1;n<=3;n++)builder.Append("사이 문단 ").Append(n).Append(": 첫 그림 아래의 설명 글.\n\n");
        builder.Append("## 가운데 제목 ").Append(tag).Append("\n\n");
        AnchorChain(builder,'B',"구간",tag);
        for(int n=1;n<=6;n++)builder.Append("아래 문단 ").Append(n).Append(": 둘째 그림 아래의 설명 글.\n\n");
        return builder.ToString();
    }
    // True when the scroll offset stays put for the next ~600 ms (after a 250 ms settle of the move itself).
    static bool ScrollSteady(MainWindow window,out string detail){
        TextView view=window.source.TextArea.TextView;
        PumpFor(window,250);
        double first=view.VerticalOffset,worst=0;
        for(int i=0;i<4;i++){
            PumpFor(window,150);
            worst=Math.Max(worst,Math.Abs(view.VerticalOffset-first));
        }
        detail="offset="+N(first)+" drift="+N(worst);
        return worst<=0.5;
    }
    static void OffscreenCase(bool live,bool control){
        string mode=Mode(live),tag="m15 "+mode+(control?" control":"");
        MainWindow window=null;
        try{
            MermaidRender.CaretAnchorOff=control;
            window=Open(OffscreenText(mode),live);
            var fences=MermaidFences(Doc(window));
            if(fences.Count!=2){Check(tag+": two tall mermaid blocks are read from the document",false,"fences="+fences.Count);return;}
            Fence a=fences[0],b=fences[1];
            TextView view=window.source.TextArea.TextView;
            Caret(window,1,1);
            window.source.ScrollToVerticalOffset(0);
            Pump(window);
            double viewport=window.source.ViewportHeight;
            if(viewport<=0)viewport=view.ActualHeight;
            if(!WaitFor(window,delegate { return Shown(window,a.Opener)&&MermaidRender.PendingCount==0; },5000)){
                Check(tag+": the first picture is drawn first",false,EngineState());return;
            }
            PumpFor(window,150);
            double lineHeight=view.DefaultLineHeight;
            if(lineHeight<=0)lineHeight=1;
            VisualLine row=view.GetVisualLine(a.Opener);
            MermaidElement diagram=Diagram(window,a.Opener);
            if(row==null||row.IsDisposed||diagram==null||diagram.Picture==null){Check(tag+": the first picture has a row",false,EngineState());return;}
            double rowHeight=row.Height;
            if(rowHeight<lineHeight*12){
                Check(tag+": the fixture's picture is tall enough",false,"row="+N(rowHeight)+" viewport="+N(viewport)+" lineHeight="+N(lineHeight));return;
            }
            // The picture's top at -(height/2): only its lower half is in view.
            double pictureTop=view.GetVisualTopByDocumentLine(a.Opener);
            window.source.ScrollToVerticalOffset(pictureTop+rowHeight/2);
            Pump(window);
            PumpFor(window,150);
            double startY=ScreenTop(window,a.Opener);
            if(Math.Abs(startY+rowHeight/2)>lineHeight){
                Check(tag+": the fixture scrolls the picture half above the view",false,"top="+N(startY)+" want="+N(-rowHeight/2)+" row="+N(rowHeight));return;
            }
            // The press: x on the picture, y in the visible lower part (view coordinates, as m12 builds it).
            double width=diagram.Picture.Width*diagram.Scale;
            TextLine textLine=row.GetTextLine(diagram.VisualColumn);
            double slotLeft=row.GetTextLineVisualXPosition(textLine,diagram.VisualColumn);
            double left=slotLeft+Math.Max(0,(diagram.Slot-width)/2);
            double pressY=Math.Min(startY+rowHeight-20,viewport/2);
            Point press=new Point(left+width/2-view.HorizontalOffset,pressY);
            long mark=clock.ElapsedMilliseconds;
            bool pressed=PressPicture(window,press);
            bool unfolded=WaitFor(window,delegate { return !Shown(window,a.Opener)&&OwnLine(window,a.Opener+1); },1500);
            string steadyA;
            bool steady=ScrollSteady(window,out steadyA);
            double openerY=ScreenTop(window,a.Opener);
            int caretLine=window.source.TextArea.Caret.Line;
            double caretTop;
            bool caretIn=RowInView(window,caretLine,viewport,out caretTop);
            bool openerOk=openerY>=-0.5&&openerY<=lineHeight+0.5;
            string detailA="pictureY="+N(startY)+" press="+N(pressY)+" row="+N(rowHeight)+" opener="+N(openerY)+" caret="+caretLine+" want="+(a.Opener+1)+
                " caretY="+N(caretTop)+" caretIn="+caretIn+" lineHeight="+N(lineHeight)+" pressed="+pressed+" unfolded="+unfolded+" "+steadyA+
                " ms="+(clock.ElapsedMilliseconds-mark)+" "+EngineState();
            if(control){
                // Without the anchor the opened source stays where the folded picture's top was: above the view.
                Check(tag+": without the anchor the opened source ends above the view or the view moves",
                    pressed&&unfolded&&(!openerOk||!steady||!caretIn),detailA);
                return;
            }
            Check(tag+"a: a press on a picture that starts above the view opens the source at the top of the view",
                pressed&&unfolded&&caretLine==a.Opener+1&&openerOk&&caretIn&&steady,detailA);
            // Case b: scroll the next heading to y=80 (A's source is above the view then) and press it.
            int heading=0;
            for(int number=a.Closer+1;number<b.Opener;number++)if(LineText(window,number).StartsWith("## ")){heading=number;break;}
            if(heading==0){Check(tag+"b: the heading is found",false,"block="+a.Opener+"-"+a.Closer);return;}
            double headingNow=ScreenTop(window,heading);
            window.source.ScrollToVerticalOffset(view.VerticalOffset+headingNow-80);
            Pump(window);
            PumpFor(window,150);
            double closerY=ScreenTop(window,a.Closer);
            double before=ScreenTop(window,heading);
            if(!(closerY<0)||Math.Abs(before-80)>lineHeight){
                Check(tag+"b: the fixture keeps A's source above the view and the heading near the top",false,
                    "closerY="+N(closerY)+" heading="+heading+" y="+N(before)+" "+EngineState());return;
            }
            mark=clock.ElapsedMilliseconds;
            TextPress(window,heading,2);
            // A's opener row is off screen (no visual line to ask): the fold count and the height tree say it.
            bool refolded=WaitFor(window,delegate { return Folds(window)==2&&ScreenTop(window,a.Closer+1)-ScreenTop(window,a.Opener)>=rowHeight*0.8; },1500);
            string steadyB;
            bool steadyAfter=ScrollSteady(window,out steadyB);
            double headingTop;
            bool headingIn=RowInView(window,heading,viewport,out headingTop);
            double moved=double.IsNaN(headingTop)?double.MaxValue:Math.Abs(headingTop-before);
            int caretB=window.source.TextArea.Caret.Line;
            Check(tag+"b: a press on a heading below a block whose source is above the view keeps the heading where it was",
                refolded&&caretB==heading&&headingIn&&moved<=lineHeight&&steadyAfter,
                "heading="+heading+" caret="+caretB+" y="+N(before)+"->"+N(headingTop)+" moved="+N(moved)+" lineHeight="+N(lineHeight)+
                " refolded="+refolded+" inView="+headingIn+" "+steadyB+" ms="+(clock.ElapsedMilliseconds-mark)+" "+EngineState());
        }catch(Exception ex){
            Check(tag+": no exception",false,ex.ToString());
        }finally{
            MermaidRender.CaretAnchorOff=false;
            CloseWindow(window);
        }
    }
    static void AnchorOffscreen(bool live){
        Stopwatch caseClock=Stopwatch.StartNew();
        OffscreenCase(live,false);
        // The control costs another window: skipped when the probe's own bound is close.
        if(clock.ElapsedMilliseconds<BudgetMilliseconds-24000)OffscreenCase(live,true);
        Note("m15 "+Mode(live)+": "+caseClock.ElapsedMilliseconds+" ms for the case");
    }

    // ------------------------------------------------ entry
    internal static int Run(string outDir){
        folder=Path.GetFullPath(outDir);
        Directory.CreateDirectory(folder);
        string pass=Path.Combine(folder,"mermaid-abuse-probe-PASS.txt"),fail=Path.Combine(folder,"mermaid-abuse-probe-FAIL.txt");
        try{File.Delete(pass);File.Delete(fail);File.Delete(Path.Combine(folder,"mermaid-abuse-trace.txt"));}catch(IOException){}
        all=true;report.Length=0;
        report.Append("# MermaidAbuseProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o",CultureInfo.InvariantCulture)).Append('\n');
        int exit=0;
        Stopwatch total=Stopwatch.StartNew();
        clock=total;
        bool oldSelf=Program.IsSelfTest;
        try{
            // The window stays off screen and the preference file is neither read nor written.
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            // m1 first: it is the widest document and warms the picture cache every later case reuses.
            Trace("start m1");ScrollMany(false);ScrollMany(true);
            Trace("start m2");EditStorm(false);EditStorm(true);
            Trace("start m3");DeleteBlock(false);DeleteBlock(true);
            Trace("start m4");PasteBlock(false);PasteBlock(true);
            Trace("start m5");OddFences(false);OddFences(true);
            Trace("start m6");ModeThemeSwitch(false);ModeThemeSwitch(true);
            Trace("start m7");ZoomCase(false);ZoomCase(true);
            Trace("start m8");Wide(false);Wide(true);
            Trace("start m9");Refusal(false);Refusal(true);
            Trace("start m10");BigTyping(false);BigTyping(true);
            Trace("start m11");MidBlock(false);MidBlock(true);
            Trace("start m12");ClickJump(false);ClickJump(true);
            Trace("start m13");PreRender(false);PreRender(true);
            Trace("start m14");AnchorJump(false);AnchorJump(true);
            Trace("start m15");AnchorOffscreen(false);AnchorOffscreen(true);
        }catch(Exception ex){
            exit=1;all=false;report.Append("FAIL probe exception: ").Append(ex).Append('\n');
        }finally{Program.IsSelfTest=oldSelf;clock=null;}
        Note("probe time "+total.ElapsedMilliseconds+" ms");
        Check("z whole probe under 150 s",total.ElapsedMilliseconds<150000,total.ElapsedMilliseconds+" ms");
        report.Append("\n").Append(all?"RESULT PASS":"RESULT FAIL").Append('\n');
        try{
            File.WriteAllText(Path.Combine(folder,"mermaid-abuse-probe.txt"),report.ToString(),new UTF8Encoding(false));
            File.WriteAllText(all&&exit==0?pass:fail,report.ToString(),new UTF8Encoding(false));
        }catch(Exception){return 2;}
        return all&&exit==0?0:1;
    }
}
}}
