using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {public partial class MainWindow {
internal static int VerifyMermaidUndo(string outDir){return MermaidUndoProbe.Run(outDir);}
// --mermaid-undo-probe <folder>: a ```mermaid block survives the app's own undo/redo (Ctrl+Z / Ctrl+Y,
// i.e. MainWindow.Undo/Redo -> RestoreEditorHistory; AvalonEdit's own UndoStack is off, SizeLimit=0),
// in the basic and the live view. Each case edits the document the way a user's one action would, waits
// for the session to settle, then presses the app's undo and checks the text AND the drawn state.
// Documents are generated here; no user file and no clipboard is used. Report
// <folder>\mermaid-undo-probe.txt, -PASS.txt only when every case held.
internal static class MermaidUndoProbe {
    // Lines: 1 text, 2 blank, 3 opener, 4-5 source, 6 closer, 7 blank, 8 text.
    const string Fixture="제목 문단\n\n```mermaid\ngraph TD\n  A --> B\n```\n\n끝 문단\n";
    const int OpenerLine=3,CloserLine=6,OutsideLine=8;
    static bool all=true;
    static readonly StringBuilder report=new StringBuilder();

    static void Check(string name,bool ok,string detail){
        report.Append(ok?"PASS ":"FAIL ").Append(name);
        if(detail!=null)report.Append(": ").Append(detail);
        report.Append('\n');
        if(!ok)all=false;
    }
    static void Note(string text){report.Append("INFO ").Append(text).Append('\n');}

    internal static int Run(string outDir){
        string folder=Path.GetFullPath(outDir);
        Directory.CreateDirectory(folder);
        string pass=Path.Combine(folder,"mermaid-undo-probe-PASS.txt"),fail=Path.Combine(folder,"mermaid-undo-probe-FAIL.txt");
        try{File.Delete(pass);File.Delete(fail);}catch(IOException){}
        all=true;report.Length=0;
        report.Append("# MermaidUndoProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o",CultureInfo.InvariantCulture)).Append('\n');
        int exit=0;
        Stopwatch total=Stopwatch.StartNew();
        bool oldSelf=Program.IsSelfTest;
        try{
            // Self-test: the window stays off-screen and the preference file is neither read nor written.
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            CaseU1(false);CaseU1(true);
            CaseU2(false);CaseU2(true);
            CaseU3(false);CaseU3(true);
            CaseU4(false);CaseU4(true);
            CaseU5(false);CaseU5(true);
        }catch(Exception ex){
            exit=1;all=false;report.Append("FAIL probe exception: ").Append(ex).Append('\n');
        }finally{Program.IsSelfTest=oldSelf;}
        Note("probe time "+total.ElapsedMilliseconds+" ms");
        Check("z whole probe under 150 s",total.ElapsedMilliseconds<150000,total.ElapsedMilliseconds+" ms");
        report.Append("\n").Append(all?"RESULT PASS":"RESULT FAIL").Append('\n');
        try{
            File.WriteAllText(Path.Combine(folder,"mermaid-undo-probe.txt"),report.ToString(),new UTF8Encoding(false));
            File.WriteAllText(all&&exit==0?pass:fail,report.ToString(),new UTF8Encoding(false));
        }catch(Exception){return 2;}
        return all&&exit==0?0:1;
    }

    // ------------------------------------------------ window, pumping, inspection
    // The same wait MermaidLiveProbe uses: ApplicationIdle lets the session's Background idle timer run.
    static void Pump(MainWindow window){
        LongLineBench.Idle();
        if(window==null||window.source==null)return;
        window.UpdateLayout();
        window.source.TextArea.TextView.EnsureVisualLines();
    }
    static bool WaitFor(MainWindow window,Func<bool> condition,int milliseconds){
        Stopwatch watch=Stopwatch.StartNew();
        while(true){
            Pump(window);
            if(condition())return true;
            if(watch.ElapsedMilliseconds>=milliseconds)return false;
            Thread.Sleep(10);
        }
    }
    static MainWindow Open(bool live){
        bool old=ProbeDefaults.Live;ProbeDefaults.Live=live;
        try{
            MainWindow window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;window.UpdateLayout();
            window.SetText(Fixture);window.saved=Fixture;window.recoveredDocument=false;
            window.SetMode(true);window.SetLiveMode(live);LongLineBench.Idle();window.UpdateLayout();
            // SetText clears the history itself; anything left here came from setup, not from a user edit.
            if(window.History.UndoDepth!=0){Note((live?"live":"basic")+": "+window.History.UndoDepth+" setup history entries cleared");window.History.Clear();}
            return window;
        }finally{ProbeDefaults.Live=old;}
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{window.FlushDocumentChanges();window.saved=window.text;window.recoveredDocument=false;window.CaptureTab();foreach(var document in window.documents){document.Saved=document.Text;document.Recovered=false;}window.Close();}catch(Exception){}
    }
    static void MoveCaret(MainWindow window,int line,int column){
        TextDocument document=window.source.Document;
        if(line<1)line=1;
        if(line>document.LineCount)line=document.LineCount;
        DocumentLine target=document.GetLineByNumber(line);
        if(column<1)column=1;
        if(column>target.Length+1)column=target.Length+1;
        window.source.TextArea.Caret.Line=line;
        window.source.TextArea.Caret.Column=column;
        Pump(window);
    }
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
    static string LineText(MainWindow window,int number){
        TextDocument document=window.source.Document;
        if(number<1||number>document.LineCount)return "<no line "+number+">";
        return document.GetText(document.GetLineByNumber(number));
    }
    static string DocumentText(MainWindow window){return window.source.Document.Text;}
    static bool TextIs(MainWindow window,string expected){return String.Equals(DocumentText(window),expected,StringComparison.Ordinal);}
    static int Records(MainWindow window){return window.livePreview==null?0:window.livePreview.MermaidBlockCount;}
    static int Folds(MainWindow window){return window.livePreview==null?0:window.livePreview.MermaidFoldCount;}
    static string PictureKey(MainWindow window,int number){
        MermaidElement diagram=Diagram(window,number);
        return diagram==null||diagram.Picture==null||diagram.Picture.Key==null?"<none>":diagram.Picture.Key;
    }
    static string Clip(string value){
        if(value==null)return "<null>";
        if(value.Length>70)value=value.Substring(0,70)+"...";
        return value.Replace("\r","\\r").Replace("\n","\\n");
    }
    static string EngineState(){
        return "unavailable="+MermaidEngine.Unavailable+" startup="+(MermaidEngine.StartupError??"-")+" last="+(MermaidRender.LastError??"-")+
            " pending="+MermaidRender.PendingCount+" requests="+MermaidRender.DiagRequests+" renders="+MermaidEngine.DiagRenders;
    }
    // A FAIL detail always says what was observed: text equal? drawn? folded?
    static string State(MainWindow window,string expected){
        return "textEqual="+TextIs(window,expected)+" drawn="+Shown(window,OpenerLine)+" folded="+Folded(window,OpenerLine,CloserLine)+
            " line3=\""+Clip(LineText(window,OpenerLine))+"\" records="+Records(window)+" folds="+Folds(window)+" pending="+MermaidRender.PendingCount;
    }
    static bool DrawFirst(MainWindow window,string tag,out long milliseconds){
        Stopwatch watch=Stopwatch.StartNew();
        MoveCaret(window,OutsideLine,1);
        bool drawn=WaitFor(window,delegate { return Shown(window,OpenerLine)&&Folded(window,OpenerLine,CloserLine)&&MermaidRender.PendingCount==0; },8000);
        milliseconds=watch.ElapsedMilliseconds;
        Check(tag+": the block is drawn before the edit",drawn,
            "draw "+milliseconds+" ms, attached="+window.LivePreviewAttached+" "+State(window,Fixture)+" "+EngineState());
        return drawn;
    }
    static bool DrawnAgain(MainWindow window,string expected,int milliseconds){
        return WaitFor(window,delegate { return TextIs(window,expected)&&Shown(window,OpenerLine)&&Folded(window,OpenerLine,CloserLine)&&MermaidRender.PendingCount==0; },milliseconds);
    }
    static string Mode(bool live){return live?"live":"basic";}

    // u1: the opener's info word edited and taken back. The text and the drawn picture come back.
    static void CaseU1(bool live){
        string tag="u1 "+Mode(live);
        MainWindow window=null;
        try{
            window=Open(live);
            long drawMs;
            if(!DrawFirst(window,tag,out drawMs))return;
            string original=DocumentText(window);
            string edited=original.Replace("```mermaid","```mermai");
            long depth=window.History.UndoDepth;
            Stopwatch edit=Stopwatch.StartNew();
            DocumentLine opener=window.source.Document.GetLineByNumber(OpenerLine);
            window.source.Document.Remove(opener.EndOffset-1,1);
            Pump(window);
            bool coded=WaitFor(window,delegate { return !Shown(window,OpenerLine)&&OwnLine(window,OpenerLine+1); },3000);
            edit.Stop();
            Check(tag+": one edit on the opener turns the picture into code",
                coded&&TextIs(window,edited)&&window.History.UndoDepth==depth+1,
                "textEdited="+TextIs(window,edited)+" drawn="+Shown(window,OpenerLine)+" own4="+OwnLine(window,OpenerLine+1)+
                " depth="+window.History.UndoDepth+" (was "+depth+") line3=\""+Clip(LineText(window,OpenerLine))+"\"");
            MoveCaret(window,OutsideLine,1);
            Stopwatch undo=Stopwatch.StartNew();
            window.Undo();
            MoveCaret(window,OutsideLine,1);
            bool back=DrawnAgain(window,original,5000);
            undo.Stop();
            Check(tag+": undo restores the text and the drawn picture",back,State(window,original)+" undo+redraw "+undo.ElapsedMilliseconds+" ms");
            Note(tag+": draw "+drawMs+" ms, edit "+edit.ElapsedMilliseconds+" ms, undo+redraw "+undo.ElapsedMilliseconds+" ms");
        }catch(Exception ex){
            Check(tag+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // u2: the same edit, undo, then redo (plain code again) and undo again (picture back).
    static void CaseU2(bool live){
        string tag="u2 "+Mode(live);
        MainWindow window=null;
        try{
            window=Open(live);
            long drawMs;
            if(!DrawFirst(window,tag,out drawMs))return;
            string original=DocumentText(window);
            string edited=original.Replace("```mermaid","```mermai");
            DocumentLine opener=window.source.Document.GetLineByNumber(OpenerLine);
            window.source.Document.Remove(opener.EndOffset-1,1);
            Pump(window);
            bool coded=WaitFor(window,delegate { return !Shown(window,OpenerLine)&&OwnLine(window,OpenerLine+1); },3000);
            MoveCaret(window,OutsideLine,1);
            window.Undo();
            MoveCaret(window,OutsideLine,1);
            bool back=DrawnAgain(window,original,5000);
            Check(tag+": the first undo draws the picture again",coded&&back,State(window,original)+" "+EngineState());
            bool canRedo=window.History.CanRedo;
            Stopwatch redo=Stopwatch.StartNew();
            window.Redo();
            MoveCaret(window,OutsideLine,1);
            bool recoded=WaitFor(window,delegate {
                return TextIs(window,edited)&&!Shown(window,OpenerLine)&&OwnLine(window,OpenerLine+1)&&MermaidRender.PendingCount==0;
            },5000);
            redo.Stop();
            Check(tag+": redo turns the picture back into code",canRedo&&recoded,
                "canRedo="+canRedo+" textEdited="+TextIs(window,edited)+" line3=\""+Clip(LineText(window,OpenerLine))+"\" drawn="+Shown(window,OpenerLine)+
                " own4="+OwnLine(window,OpenerLine+1)+" records="+Records(window)+" redo "+redo.ElapsedMilliseconds+" ms");
            Stopwatch undo=Stopwatch.StartNew();
            window.Undo();
            MoveCaret(window,OutsideLine,1);
            bool again=DrawnAgain(window,original,5000);
            undo.Stop();
            Check(tag+": the second undo brings the picture back",again,State(window,original)+" undo+redraw "+undo.ElapsedMilliseconds+" ms");
            Note(tag+": draw "+drawMs+" ms, redo "+redo.ElapsedMilliseconds+" ms, undo+redraw "+undo.ElapsedMilliseconds+" ms");
        }catch(Exception ex){
            Check(tag+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // u3: the diagram source edited (B -> C) and taken back: the original picture (its own cache key), not
    // a stale picture of the edited source.
    static void CaseU3(bool live){
        string tag="u3 "+Mode(live);
        MainWindow window=null;
        try{
            window=Open(live);
            long drawMs;
            if(!DrawFirst(window,tag,out drawMs))return;
            string original=DocumentText(window);
            string originalKey=PictureKey(window,OpenerLine);
            if(String.Equals(originalKey,"<none>",StringComparison.Ordinal)){Check(tag+": the first picture has a key",false,"key=<none> "+EngineState());return;}
            TextDocument document=window.source.Document;
            DocumentLine sourceLine=document.GetLineByNumber(OpenerLine+2);
            int at=sourceLine.EndOffset-1;
            string before=document.GetText(at,1);
            if(!String.Equals(before,"B",StringComparison.Ordinal)){
                Check(tag+": the source line ends with B",false,"line5=\""+Clip(LineText(window,OpenerLine+2))+"\" char=\""+before+"\" at="+at);
                return;
            }
            Stopwatch edit=Stopwatch.StartNew();
            document.Replace(at,1,"C");
            Pump(window);
            MoveCaret(window,OutsideLine,1);
            bool redrawn=WaitFor(window,delegate {
                MermaidElement now=Diagram(window,OpenerLine);
                return MermaidRender.PendingCount==0&&now!=null&&now.Picture.Key!=null&&
                    now.Picture.Key.IndexOf("A --> C",StringComparison.Ordinal)>=0&&Folded(window,OpenerLine,CloserLine);
            },8000);
            edit.Stop();
            Check(tag+": the edited source is drawn",redrawn,
                "line5=\""+Clip(LineText(window,OpenerLine+2))+"\" key=\""+Clip(PictureKey(window,OpenerLine))+"\" "+EngineState());
            Stopwatch undo=Stopwatch.StartNew();
            window.Undo();
            MoveCaret(window,OutsideLine,1);
            bool back=WaitFor(window,delegate {
                MermaidElement now=Diagram(window,OpenerLine);
                return TextIs(window,original)&&now!=null&&now.Picture.Key!=null&&
                    String.Equals(now.Picture.Key,originalKey,StringComparison.Ordinal)&&MermaidRender.PendingCount==0;
            },8000);
            undo.Stop();
            Check(tag+": undo restores the source and its own picture",back,
                "textEqual="+TextIs(window,original)+" drawn="+Shown(window,OpenerLine)+" keyMatches="+String.Equals(PictureKey(window,OpenerLine),originalKey,StringComparison.Ordinal)+
                " key=\""+Clip(PictureKey(window,OpenerLine))+"\" line5=\""+Clip(LineText(window,OpenerLine+2))+"\" undo+redraw "+undo.ElapsedMilliseconds+" ms");
            Note(tag+": draw "+drawMs+" ms, edit+redraw "+edit.ElapsedMilliseconds+" ms, undo+redraw "+undo.ElapsedMilliseconds+" ms");
        }catch(Exception ex){
            Check(tag+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // u4: the whole block deleted (one selection delete) and taken back: the block and its picture return,
    // and the caret the undo restored is inside the document.
    static void CaseU4(bool live){
        string tag="u4 "+Mode(live);
        MainWindow window=null;
        try{
            window=Open(live);
            long drawMs;
            if(!DrawFirst(window,tag,out drawMs))return;
            string original=DocumentText(window);
            MoveCaret(window,OutsideLine,1);
            TextDocument document=window.source.Document;
            int start=document.GetLineByNumber(OpenerLine).Offset;
            int end=document.GetLineByNumber(CloserLine).EndOffset;
            Stopwatch edit=Stopwatch.StartNew();
            document.Remove(start,end-start);
            Pump(window);
            bool gone=WaitFor(window,delegate {
                return DocumentText(window).IndexOf("mermaid",StringComparison.Ordinal)<0&&Records(window)==0&&Folds(window)==0;
            },3000);
            edit.Stop();
            Check(tag+": deleting the block removes it and its picture",gone,
                "chars="+DocumentText(window).Length+" records="+Records(window)+" folds="+Folds(window)+" lines="+document.LineCount);
            Stopwatch undo=Stopwatch.StartNew();
            window.Undo();
            int restoredCaret=window.source.CaretIndex;
            int restoredLength=window.source.Document.TextLength;
            bool sane=restoredCaret>=0&&restoredCaret<=restoredLength;
            MoveCaret(window,OutsideLine,1);
            bool back=DrawnAgain(window,original,5000);
            undo.Stop();
            Check(tag+": undo brings the block and its picture back",back&&sane,
                State(window,original)+" restoredCaret="+restoredCaret+"/"+restoredLength+" sane="+sane+" undo+redraw "+undo.ElapsedMilliseconds+" ms");
            Note(tag+": draw "+drawMs+" ms, delete "+edit.ElapsedMilliseconds+" ms, undo+redraw "+undo.ElapsedMilliseconds+" ms");
        }catch(Exception ex){
            Check(tag+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }

    // u5: ``` typed on the blank line above a drawn block opens a fence that swallows it; the undo gives the
    // picture back.
    static void CaseU5(bool live){
        string tag="u5 "+Mode(live);
        MainWindow window=null;
        try{
            window=Open(live);
            long drawMs;
            if(!DrawFirst(window,tag,out drawMs))return;
            string original=DocumentText(window);
            MoveCaret(window,2,1);
            Stopwatch edit=Stopwatch.StartNew();
            window.source.TextArea.PerformTextInput("```");
            Pump(window);
            MoveCaret(window,OutsideLine,1);
            bool swallowed=WaitFor(window,delegate {
                return !Shown(window,OpenerLine)&&Folds(window)==0&&String.Equals(LineText(window,2),"```",StringComparison.Ordinal);
            },3000);
            edit.Stop();
            Check(tag+": a fence typed above swallows the picture",swallowed&&window.History.CanUndo,
                "line2=\""+Clip(LineText(window,2))+"\" drawn="+Shown(window,OpenerLine)+" folds="+Folds(window)+" records="+Records(window)+
                " canUndo="+window.History.CanUndo+" depth="+window.History.UndoDepth);
            MoveCaret(window,OutsideLine,1);
            Stopwatch undo=Stopwatch.StartNew();
            window.Undo();
            MoveCaret(window,OutsideLine,1);
            bool back=DrawnAgain(window,original,5000);
            undo.Stop();
            Check(tag+": undo brings the picture back",back,State(window,original)+" undo+redraw "+undo.ElapsedMilliseconds+" ms");
            Note(tag+": draw "+drawMs+" ms, type "+edit.ElapsedMilliseconds+" ms, undo+redraw "+undo.ElapsedMilliseconds+" ms");
        }catch(Exception ex){
            Check(tag+": no exception",false,ex.ToString());
        }finally{CloseWindow(window);}
    }
}
}}
