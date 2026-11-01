using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
namespace MDNote {public partial class MainWindow {
internal static int VerifyHistoryCap(string outDir){return HistoryCapProbe.Run(outDir);}
// --history-cap-probe <dir>: undo-history caps in DocumentHistory (512 entries or 8 MB of UTF-16
// text per document, oldest evicted first, including the provisional-entry bookkeeping). Self-test
// only, generated text only. Cases 1/4 run on the live source surface and the basic rich surface;
// case 2 on DocumentHistory and a plain-text source window (a 12 MB FlowDocument is not practical
// to drive); case 3 on the source surface as well, because an EditGroup only exists inside a
// TextDocument update (basic rich edits never open one).
internal static class HistoryCapProbe {
    const int UndoCap=512;
    const long ByteCap=8L*1024*1024;
    const int ChunkLines=4096,ChunkLineChars=256,ChunkChars=ChunkLines*ChunkLineChars;
    static readonly string[] BigChunks=new string[12];
    static StringBuilder report;static bool all;static int cases,passed,failed;static string outRoot;
    internal static int Run(string outputDirectory){
        outRoot=Path.GetFullPath(outputDirectory);Directory.CreateDirectory(outRoot);
        report=new StringBuilder("# history-cap-probe\n");
        report.Append("# DocumentHistory caps: 512 undo entries or 8 MB of UTF-16 text per document, oldest evicted first.\n");
        report.Append("# live = live Markdown source editing; rich = basic rich editing; txt = plain-text source; history = DocumentHistory directly.\n");
        all=true;cases=passed=failed=0;
        string passFile=Path.Combine(outRoot,"history-cap-PASS.txt"),failFile=Path.Combine(outRoot,"history-cap-FAIL.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        bool oldLive=ProbeDefaults.Live,oldSelf=Program.IsSelfTest;
        var watch=new System.Diagnostics.Stopwatch();watch.Start();
        // A fault on a dispatcher turn or a worker thread leaves no report file at all; log it next
        // to the progress file so a crash names itself instead of just ending the process.
        System.Windows.Threading.DispatcherUnhandledExceptionEventHandler dispatcherFault=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args){LogFault("dispatcher",args.Exception);};
        UnhandledExceptionEventHandler domainFault=delegate(object sender,UnhandledExceptionEventArgs args){LogFault("domain",args.ExceptionObject as Exception);};
        try{
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException+=dispatcherFault;
            AppDomain.CurrentDomain.UnhandledException+=domainFault;
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            for(int i=0;i<BigChunks.Length;i++)BigChunks[i]=ByteChunk(i+1);
            RunHistoryProvisional();
            RunHistoryByteCap();
            RunWindowCountCap();
            RunWindowByteCap();
            RunWindowProvisional();
            RunWindowMultiChange();
        }catch(Exception ex){all=false;report.Append("EXCEPTION ").Append(OneLine(ex.ToString())).Append('\n');}
        finally{
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException-=dispatcherFault;AppDomain.CurrentDomain.UnhandledException-=domainFault;
            ProbeDefaults.Live=oldLive;Program.IsSelfTest=oldSelf;
        }
        watch.Stop();
        report.Append("\nCases: ").Append(cases).Append("  PASS: ").Append(passed).Append("  FAIL: ").Append(failed).Append('\n');
        report.Append("Runtime: ").Append(watch.ElapsedMilliseconds).Append(" ms\n");
        report.Append("Result: ").Append(all?"PASS":"FAIL").Append('\n');
        try{File.WriteAllText(all?passFile:failFile,report.ToString(),new UTF8Encoding(false));}
        catch(Exception){return 2;}
        return all?0:1;
    }
    // ------------------------------------------------ case 1/4: live source surface, 600 edits
    static void RunWindowCountCap(){
        MainWindow window=null;var states=new List<string>();
        try{
            window=CreateWindow(true);
            window.SetText("");LongLineBench.Idle();window.SetMode(true);LongLineBench.Idle();
            Case("live.cap.surface",window.CodeMode&&true&&window.editing,"code="+window.CodeMode+", live="+true+", editing="+window.editing);
            var document=window.source.Document;states.Add(document.Text);
            for(int i=1;i<=600;i++){
                int at=(i&1)==1?0:document.TextLength;
                document.Insert(at,Token(i));
                LongLineBench.Idle();
                states.Add(document.Text);
            }
            Case("live.cap.fill",states.Count==601&&EveryStepChanged(states)&&document.TextLength==states[0].Length+600*6,"chars="+document.TextLength);
            int undo=UndoAll(window);string undone=window.text;
            Case("live.cap.undo-count",undo==UndoCap,"undoSteps="+undo+", cap="+UndoCap);
            Case("live.cap.undo-oldest",undone==states[88],undone==states[88]?"text equals the state after edit 88":"differs from the state after edit 88, "+Diff(states[88],undone));
            Case("live.cap.undo-flags",!window.History.CanUndo&&window.History.CanRedo,Flags(window.History));
            int redo=RedoAll(window);
            Case("live.cap.redo-count",redo==undo&&redo==UndoCap,"redoSteps="+redo+", undoSteps="+undo);
            Case("live.cap.redo-final",window.text==states[600],window.text==states[600]?"text equals the final text":"differs from the final text, "+Diff(states[600],window.text));
            Case("live.cap.redo-flags",window.History.CanUndo&&!window.History.CanRedo,Flags(window.History));
            // Case 4: at the cap, undo 10, then a new edit clears redo and keeps the oldest state.
            for(int i=0;i<10;i++)window.Undo();
            LongLineBench.Idle();
            string before=window.text,added=Token(9001);
            document.Insert(0,added);LongLineBench.Idle();
            string changed=window.text;
            Case("live.after-cap.new-edit",changed==added+before&&!window.History.CanRedo&&window.History.CanUndo,"textChanged="+(changed==added+before)+", "+Flags(window.History));
            undo=UndoAll(window);
            Case("live.after-cap.undo-count",undo==UndoCap-10+1,"undoSteps="+undo+", expected="+(UndoCap-10+1));
            Case("live.after-cap.undo-oldest",window.text==states[88],window.text==states[88]?"oldest kept state unchanged":"differs from the state after edit 88, "+Diff(states[88],window.text));
            redo=RedoAll(window);
            Case("live.after-cap.redo-count",redo==undo,"redoSteps="+redo+", undoSteps="+undo);
            Case("live.after-cap.redo-final",window.text==changed,window.text==changed?"new final text restored":"differs from the new final text, "+Diff(changed,window.text));
        }catch(Exception ex){Case("live.cap.exception",false,OneLine(ex.GetType().Name+": "+ex.Message));}
        finally{CloseWindow(window);}
    }
    // ------------------------------------------------ case 1/4: basic rich surface, 600 edits
    // ------------------------------------------------ case 2: bytes cap, 12 x 1 MB inserts
    static void RunHistoryByteCap(){
        try{
            var history=new DocumentHistory();string text="";
            for(int i=0;i<BigChunks.Length;i++){history.RecordDelta(text.Length,"",BigChunks[i]);text+=BigChunks[i];}
            string final=text;
            Case("history.bytes.setup",history.CanUndo&&!history.CanRedo,"chars="+text.Length+", chunks="+BigChunks.Length);
            int undo=UndoAll(history,ref text);
            string expected8=BigText(8);
            Case("history.bytes.undo-count",undo==4,"undoSteps="+undo+", capBytes="+ByteCap);
            Case("history.bytes.undo-oldest",String.Equals(text,expected8,StringComparison.Ordinal)&&text.IndexOf(ChunkHeader(9),StringComparison.Ordinal)<0,text.Length==8*ChunkChars?"chunks 1-8 kept":"chars="+text.Length);
            Case("history.bytes.undo-flags",!history.CanUndo&&history.CanRedo,Flags(history));
            int redo=RedoAll(history,ref text);
            Case("history.bytes.redo-count",redo==undo&&redo==4,"redoSteps="+redo+", undoSteps="+undo);
            Case("history.bytes.redo-final",String.Equals(text,final,StringComparison.Ordinal),"chars="+text.Length);
            Case("history.bytes.redo-flags",history.CanUndo&&!history.CanRedo,Flags(history));
        }catch(Exception ex){Case("history.bytes.exception",false,OneLine(ex.GetType().Name+": "+ex.Message));}
    }
    static void RunWindowByteCap(){
        MainWindow window=null;
        try{
            window=CreateWindow(false);
            window.textMode=true;window.SetText("");LongLineBench.Idle();window.SetMode(true);LongLineBench.Idle();
            Case("txt.bytes.surface",window.CodeMode&&window.PlainText&&window.editing,"code="+window.CodeMode+", plain="+window.PlainText+", editing="+window.editing);
            var document=window.source.Document;
            for(int i=0;i<BigChunks.Length;i++){document.Insert(document.TextLength,BigChunks[i]);LongLineBench.Idle();}
            Case("txt.bytes.fill",document.TextLength==BigChunks.Length*ChunkChars,"chars="+document.TextLength);
            string full=window.text,expected8=BigText(8);
            int undo=UndoAll(window);string text8=window.text;
            Case("txt.bytes.undo-count",undo==4,"undoSteps="+undo+", capBytes="+ByteCap);
            Case("txt.bytes.undo-oldest",String.Equals(text8,expected8,StringComparison.Ordinal)&&text8.IndexOf(ChunkHeader(9),StringComparison.Ordinal)<0,text8.Length==8*ChunkChars?"chunks 1-8 kept":"chars="+text8.Length);
            Case("txt.bytes.undo-flags",!window.History.CanUndo&&window.History.CanRedo,Flags(window.History));
            int redo=RedoAll(window);string text12=window.text;
            Case("txt.bytes.redo-count",redo==undo&&redo==4,"redoSteps="+redo+", undoSteps="+undo);
            Case("txt.bytes.redo-final",String.Equals(text12,full,StringComparison.Ordinal),"chars="+text12.Length);
            Case("txt.bytes.redo-flags",window.History.CanUndo&&!window.History.CanRedo,Flags(window.History));
        }catch(Exception ex){Case("txt.bytes.exception",false,OneLine(ex.GetType().Name+": "+ex.Message));}
        finally{CloseWindow(window);}
    }
    // ------------------------------------------------ case 3: eviction during an open EditGroup
    static void RunHistoryProvisional(){
        var history=new DocumentHistory();var document=new TextDocument();var states=new List<string>();string value="";
        try{
            states.Add(document.Text);
            for(int i=1;i<=UndoCap;i++){
                int at=(i&1)==1?0:document.TextLength;
                history.RecordDelta(at,"",Token(i));
                document.Insert(at,Token(i));
                states.Add(document.Text);
            }
            Case("history.provisional.fill",history.CanUndo&&!history.CanRedo&&document.TextLength>0,"chars="+document.TextLength);
            string first=Token(5001),second=Token(5002);
            try{
                DocumentHistory.BeginEditGroup(document,0,0,false);
                DocumentHistory.BeforeDocumentChange(document,0,0,first.Length);
                document.Insert(0,first);history.RecordDelta(0,"",first);
                int at=document.TextLength;
                DocumentHistory.BeforeDocumentChange(document,at,0,second.Length);
                document.Insert(at,second);history.RecordDelta(at,"",second);
            }finally{DocumentHistory.EndEditGroup(0,0);}
            states.Add(document.Text);value=document.Text;
            int undo=UndoAll(history,ref value);
            Case("history.provisional.commit.undo-count",undo==UndoCap,"undoSteps="+undo);
            Case("history.provisional.commit.undo-oldest",value==states[1],value==states[1]?"state after edit 1 kept":"differs, "+Diff(states[1],value));
            Case("history.provisional.commit.undo-flags",!history.CanUndo&&history.CanRedo,Flags(history));
            int redo=RedoAll(history,ref value);
            Case("history.provisional.commit.redo-count",redo==undo&&redo==UndoCap,"redoSteps="+redo);
            Case("history.provisional.commit.redo-final",value==states[UndoCap+1],value==states[UndoCap+1]?"committed text restored":"differs, "+Diff(states[UndoCap+1],value));
            Case("history.provisional.commit.redo-flags",history.CanUndo&&!history.CanRedo,Flags(history));
            // The restored eviction must not leak into the byte count: one more edit still keeps 512.
            string extra=Token(5003);
            history.RecordDelta(document.TextLength,"",extra);document.Insert(document.TextLength,extra);
            states.Add(document.Text);value=document.Text;
            undo=UndoAll(history,ref value);
            Case("history.provisional.restored.undo-count",undo==UndoCap,"undoSteps="+undo);
            Case("history.provisional.restored.undo-oldest",value==states[2],value==states[2]?"oldest state advanced by one":"differs, "+Diff(states[2],value));
            redo=RedoAll(history,ref value);
            Case("history.provisional.restored.redo-final",value==states[UndoCap+2],value==states[UndoCap+2]?"extra edit restored":"differs, "+Diff(states[UndoCap+2],value));
            // Abandon: the first change of the update is taken back, so DropProvisional must restore it.
            string temporary=Token(5004);
            try{
                DocumentHistory.BeginEditGroup(document,0,0,false);
                DocumentHistory.BeforeDocumentChange(document,0,0,temporary.Length);
                document.Insert(0,temporary);history.RecordDelta(0,"",temporary);
                DocumentHistory.BeforeDocumentChange(document,0,temporary.Length,0);
                document.Remove(0,temporary.Length);history.RecordDelta(0,temporary,"");
            }finally{DocumentHistory.EndEditGroup(0,0);}
            Case("history.provisional.abandon.text",document.Text==states[UndoCap+2],document.Text==states[UndoCap+2]?"document text unchanged":"differs, "+Diff(states[UndoCap+2],document.Text));
            value=document.Text;
            undo=UndoAll(history,ref value);
            Case("history.provisional.abandon.undo-count",undo==UndoCap,"undoSteps="+undo);
            Case("history.provisional.abandon.undo-oldest",value==states[2],value==states[2]?"oldest state unchanged":"differs, "+Diff(states[2],value));
            redo=RedoAll(history,ref value);
            Case("history.provisional.abandon.redo-final",value==states[UndoCap+2],value==states[UndoCap+2]?"text restored after redo":"differs, "+Diff(states[UndoCap+2],value));
        }catch(Exception ex){Case("history.provisional.exception",false,OneLine(ex.GetType().Name+": "+ex.Message));}
        finally{try{DocumentHistory.EndEditGroup(0,0);}catch(Exception){}}
    }
    static void RunWindowProvisional(){
        MainWindow window=null;var states=new List<string>();
        try{
            window=CreateWindow(true);
            window.SetText("");LongLineBench.Idle();window.SetMode(true);LongLineBench.Idle();
            var document=window.source.Document;states.Add(document.Text);
            for(int i=1;i<=520;i++){
                int at=(i&1)==1?0:document.TextLength;
                document.Insert(at,Token(i));
                LongLineBench.Idle();
                states.Add(document.Text);
            }
            Case("live.provisional.fill",states.Count==521&&window.History.CanUndo&&window.text.Length==states[520].Length,"chars="+states[520].Length);
            string first=Token(7001),second=Token(7002);
            using(document.RunUpdate()){
                document.Insert(0,first);
                document.Insert(document.TextLength,second);
            }
            LongLineBench.Idle();
            states.Add(document.Text);
            int undo=UndoAll(window);
            Case("live.provisional.commit.undo-count",undo==UndoCap,"undoSteps="+undo);
            Case("live.provisional.commit.undo-oldest",window.text==states[9],window.text==states[9]?"state after edit 9 kept":"differs, "+Diff(states[9],window.text));
            Case("live.provisional.commit.undo-flags",!window.History.CanUndo&&window.History.CanRedo,Flags(window.History));
            int redo=RedoAll(window);
            Case("live.provisional.commit.redo-count",redo==undo&&redo==UndoCap,"redoSteps="+redo);
            Case("live.provisional.commit.redo-final",window.text==states[521],window.text==states[521]?"committed text restored":"differs, "+Diff(states[521],window.text));
            Case("live.provisional.commit.redo-flags",window.History.CanUndo&&!window.History.CanRedo,Flags(window.History));
            string temporary=Token(7003);
            using(document.RunUpdate()){
                document.Insert(0,temporary);
                document.Remove(0,temporary.Length);
            }
            LongLineBench.Idle();
            Case("live.provisional.abandon.text",document.Text==states[521],document.Text==states[521]?"document text unchanged":"differs, "+Diff(states[521],document.Text));
            undo=UndoAll(window);
            Case("live.provisional.abandon.undo-count",undo==UndoCap,"undoSteps="+undo);
            Case("live.provisional.abandon.undo-oldest",window.text==states[9],window.text==states[9]?"oldest state unchanged":"differs, "+Diff(states[9],window.text));
            redo=RedoAll(window);
            Case("live.provisional.abandon.redo-final",window.text==states[521],window.text==states[521]?"text restored after redo":"differs, "+Diff(states[521],window.text));
        }catch(Exception ex){Case("live.provisional.exception",false,OneLine(ex.GetType().Name+": "+ex.Message));}
        finally{CloseWindow(window);}
    }
    // ------------------------------------------------ case 5: many changes inside one update
    // One update that applies thousands of changes makes the EditGroup track the union span: the
    // first change is measured in DocumentDelta, every later one in Document.Changing, and before
    // this fix each of those concatenated the whole span copy again (O(changes x span)). The two
    // shapes that reach it are measured here on a 2 MB generated body: a replace-all of a token
    // with 20,000 matches applied right-to-left inside one update (the transaction shape that hits
    // the prepend branch), the same replace-all through the editor's own command, and a Tab indent
    // of 5,000 selected lines (the editor's own update, one change per line). Each must finish in
    // under 500 ms and one undo must restore the exact text.
    const int MultiChangeLines=40000,MultiChangeLineChars=50,MultiChangeMatchCount=20000,MultiChangeIndentLines=5000;
    const string MultiChangeToken="T0K",MultiChangeReplacement="TOKENVAL";
    const long MultiChangeMilliseconds=500L;
    static void RunWindowMultiChange(){
        // Replace-all on a large body starts the parallel search, whose async continuation needs a
        // DispatcherSynchronizationContext to come back to this thread. The app always has one; the
        // probe entry point does not (same setup as VerifyViewportSourceSync).
        var previous=System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try{
            RunMultiChangeSurface(true,"live");
            RunMultiChangeSurface(false,"txt");
        }finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previous);}
    }
    static void RunMultiChangeSurface(bool live,string label){
        MainWindow window=null;
        try{
            window=CreateWindow(live);
            if(!live){window.textMode=true;}
            window.SetText("");LongLineBench.Idle();window.SetMode(true);LongLineBench.Idle();
            Case(label+".multi.surface",window.CodeMode&&window.editing&&(!live||true),"code="+window.CodeMode+", live="+true+", editing="+window.editing);
            string body=MultiChangeBody();
            window.SetText(body);window.saved=body;window.History.Clear();LongLineBench.Idle();
            var document=window.source.Document;
            Case(label+".multi.fill",document.TextLength==body.Length&&body.Length>=2000000,"chars="+document.TextLength+", matches="+CountMultiChangeMatches(body));
            // (a) replace-all through the editor command: the source surface replaces the whole
            // selection once, so this is the shape a user actually runs.
            window.query.Text=MultiChangeToken;window.replacement.Text=MultiChangeReplacement;window.ShowReplace();
            long started=System.Diagnostics.Stopwatch.GetTimestamp();
            window.ReplaceAll();
            long replaceAll=ElapsedMilliseconds(started);
            LongLineBench.Idle();
            string afterCommand=window.text;
            Case(label+".multi.replace-all.fast",replaceAll<MultiChangeMilliseconds,"ms="+replaceAll+", limit="+MultiChangeMilliseconds);
            Case(label+".multi.replace-all.text",CountOccurrences(afterCommand,MultiChangeReplacement)==MultiChangeMatchCount&&afterCommand.IndexOf(MultiChangeToken,StringComparison.Ordinal)<0,"matches="+CountOccurrences(afterCommand,MultiChangeReplacement)+", chars="+afterCommand.Length);
            int steps=UndoAll(window);
            Case(label+".multi.replace-all.undo",steps>=1&&window.text==body,"undoSteps="+steps+", exact="+(window.text==body)+(window.text==body?"":", "+Diff(body,window.text)));
            window.SetText(body);window.saved=body;window.History.Clear();LongLineBench.Idle();
            document=window.source.Document;
            // (b) the transaction shape: every match replaced inside one document update, in
            // descending order, so the group's span grows to the left on each change.
            var matches=MultiChangeMatches(document,0,document.TextLength);
            Case(label+".multi.transaction.fill",matches.Count==MultiChangeMatchCount,"matches="+matches.Count);
            started=System.Diagnostics.Stopwatch.GetTimestamp();
            document.BeginUpdate();
            try{for(int i=matches.Count-1;i>=0;i--)document.Replace(matches[i],MultiChangeToken.Length,MultiChangeReplacement);}
            finally{document.EndUpdate();}
            long transaction=ElapsedMilliseconds(started);
            LongLineBench.Idle();
            string afterTransaction=window.text;
            Case(label+".multi.transaction.fast",transaction<MultiChangeMilliseconds,"ms="+transaction+", changes="+matches.Count+", limit="+MultiChangeMilliseconds);
            Case(label+".multi.transaction.text",afterTransaction.Replace(MultiChangeReplacement,MultiChangeToken)==body&&CountOccurrences(afterTransaction,MultiChangeToken)==0,"chars="+afterTransaction.Length+", grew="+(afterTransaction.Length-body.Length));
            steps=UndoAll(window);
            Case(label+".multi.transaction.undo",steps==1&&window.text==body,"undoSteps="+steps+", exact="+(window.text==body)+(window.text==body?"":", "+Diff(body,window.text)));
            // (b2) The same union-span bookkeeping with the concatenation this fix removed, on the
            // same body and the same change order, against the shipped list-and-join version. Both
            // run at 2,000 and 4,000 changes: doubling the changes doubles the span too, so a
            // quadratic path roughly quadruples while the segmented one stays flat.
            var descending=DescendingOffsets(matches);
            long legacy2k=LegacySpanMilliseconds(body,descending,LegacySampleChanges),segmented2k=SegmentedSpanMilliseconds(body,descending,LegacySampleChanges);
            long legacy4k=LegacySpanMilliseconds(body,descending,LegacySampleChanges*LegacySampleDoubles),segmented4k=SegmentedSpanMilliseconds(body,descending,LegacySampleChanges*LegacySampleDoubles);
            long legacyFull=LegacySpanMilliseconds(body,descending,matches.Count),segmentedFull=SegmentedSpanMilliseconds(body,descending,matches.Count);
            Case(label+".multi.transaction.legacy",legacyFull>=0,"oldMs2k="+legacy2k+", oldMs4k="+legacy4k+", oldMsAll="+legacyFull+", newMs2k="+segmented2k+", newMs4k="+segmented4k+", newMsAll="+segmentedFull);
            window.SetText(body);window.saved=body;window.History.Clear();LongLineBench.Idle();
            document=window.source.Document;
            // (c) Tab indent of the first 5,000 lines through the editor's own Tab command: one
            // document update, one insert per line, all recorded as a single history entry. The
            // selection reaches the end of the last line: a selection that stops at the start of a
            // line leaves that line out (AvalonEdit, EditingCommandHandler.OnTab).
            DocumentLine firstLine=document.GetLineByNumber(1);DocumentLine lastLine=document.GetLineByNumber(MultiChangeIndentLines);
            window.source.Select(firstLine.Offset,lastLine.EndOffset-firstLine.Offset);
            started=System.Diagnostics.Stopwatch.GetTimestamp();
            System.Windows.Documents.EditingCommands.TabForward.Execute(null,window.source.TextArea);
            long indent=ElapsedMilliseconds(started);
            LongLineBench.Idle();
            string afterIndent=window.text;
            int indented=CountLinesStartingWith(afterIndent,'\t',MultiChangeIndentLines);
            Case(label+".multi.indent.fast",indent<MultiChangeMilliseconds,"ms="+indent+", lines="+MultiChangeIndentLines+", limit="+MultiChangeMilliseconds);
            Case(label+".multi.indent.text",indented==MultiChangeIndentLines&&afterIndent.Length==body.Length+MultiChangeIndentLines,"indented="+indented+", grew="+(afterIndent.Length-body.Length));
            steps=UndoAll(window);
            Case(label+".multi.indent.undo",steps==1&&window.text==body,"undoSteps="+steps+", exact="+(window.text==body)+(window.text==body?"":", "+Diff(body,window.text)));
            // (c2) The indent's change order (one insert per line, top to bottom) against the old
            // per-change concatenation: this shape appends, which copied the same whole span.
            var indents=IndentOffsets(body,MultiChangeIndentLines);
            long indentLegacy=LegacySpanMilliseconds(body,indents,indents.Count),indentSegmented=SegmentedSpanMilliseconds(body,indents,indents.Count);
            Case(label+".multi.indent.legacy",indentLegacy>=0,"oldMs="+indentLegacy+", newMs="+indentSegmented+", changes="+indents.Count);
        }catch(Exception ex){Case(label+".multi.exception",false,OneLine(ex.GetType().Name+": "+ex.Message));}
        finally{CloseWindow(window);}
    }
    // 2 MB in 40,000 lines of 50 characters; every other line carries the token once (20,000).
    static string MultiChangeBody(){
        var builder=new StringBuilder(MultiChangeLines*(MultiChangeLineChars+1));
        for(int i=0;i<MultiChangeLines;i++){
            string index=i.ToString("00000",CultureInfo.InvariantCulture);
            string head="row"+index+" "+((i&1)==0?MultiChangeToken:"tok")+" ";
            builder.Append(head);
            builder.Append('f',MultiChangeLineChars-head.Length);
            builder.Append('\n');
        }
        return builder.ToString();
    }
    static List<int> MultiChangeMatches(ITextSource source,int from,int until){
        var result=new List<int>();int at=from,limit=Math.Min(until,source.TextLength);
        while(at<limit){
            int found=source.IndexOf(MultiChangeToken,at,limit-at,StringComparison.Ordinal);
            if(found<0)break;
            result.Add(found);at=found+MultiChangeToken.Length;
        }
        return result;
    }
    static int CountMultiChangeMatches(string value){return CountOccurrences(value,MultiChangeToken);}
    static int CountOccurrences(string value,string needle){
        int count=0,at=0;while(at<=value.Length-needle.Length){int found=value.IndexOf(needle,at,StringComparison.Ordinal);if(found<0)break;count++;at=found+needle.Length;}return count;
    }
    static int CountLinesStartingWith(string value,char first,int limit){
        int count=0,at=0,line=0;
        while(at<value.Length&&line<limit){if(value[at]==first)count++;int next=value.IndexOf('\n',at);if(next<0)break;at=next+1;line++;}
        return count;
    }
    // The two group bookkeeping algorithms over one change sequence, so the fix is measured against
    // the code it replaced rather than against a guess. `offsets` is the order the update applies
    // them; both implementations only do the string work the group does, so the numbers are the
    // bookkeeping cost and not the document's own edit cost. The shipped version is EndEditGroup's
    // piece list and its single join; the old one concatenated the whole span on every change.
    const int LegacySampleChanges=2000,LegacySampleDoubles=2;
    static long LegacySpanMilliseconds(string body,List<int> offsets,int count){
        if(count<=0||offsets.Count==0)return -1;
        if(count>offsets.Count)count=offsets.Count;
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        int first=offsets[0];
        string original=body.Substring(first,MultiChangeToken.Length);int spanStart=first,spanEnd=first+MultiChangeToken.Length;
        for(int change=1;change<count;change++){
            int at=offsets[change];
            if(at+MultiChangeToken.Length<=spanStart){
                // Left of the span: the gap is prepended, which copied the whole span every time.
                original=body.Substring(at,spanStart-at)+original;
                spanStart=at;spanEnd=spanEnd+MultiChangeReplacement.Length-MultiChangeToken.Length;
            }else if(at>spanEnd){
                // Right of the span: appended, the other branch of the same quadratic shape.
                original=original+body.Substring(spanEnd,at-spanEnd);
                spanEnd=at+MultiChangeReplacement.Length;
            }
        }
        GC.KeepAlive(original);
        return ElapsedMilliseconds(started);
    }
    // The same sequence with the shipped bookkeeping (gap pieces joined once at the end).
    static long SegmentedSpanMilliseconds(string body,List<int> offsets,int count){
        if(count<=0||offsets.Count==0)return -1;
        if(count>offsets.Count)count=offsets.Count;
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        int first=offsets[0];
        string original=body.Substring(first,MultiChangeToken.Length);int spanStart=first,spanEnd=first+MultiChangeToken.Length;
        var front=new List<string>();var back=new List<string>();
        for(int change=1;change<count;change++){
            int at=offsets[change];
            if(at+MultiChangeToken.Length<=spanStart){front.Add(body.Substring(at,spanStart-at));spanStart=at;spanEnd=spanEnd+MultiChangeReplacement.Length-MultiChangeToken.Length;}
            else if(at>spanEnd){back.Add(body.Substring(spanEnd,at-spanEnd));spanEnd=at+MultiChangeReplacement.Length;}
        }
        int length=original.Length;foreach(string piece in front)length+=piece.Length;foreach(string piece in back)length+=piece.Length;
        var builder=new StringBuilder(length);
        for(int i=front.Count-1;i>=0;i--)builder.Append(front[i]);
        builder.Append(original);
        foreach(string piece in back)builder.Append(piece);
        GC.KeepAlive(builder.ToString());
        return ElapsedMilliseconds(started);
    }
    // The transaction's own order: every match replaced from the last to the first.
    static List<int> DescendingOffsets(List<int> matches){var offsets=new List<int>(matches.Count);for(int i=matches.Count-1;i>=0;i--)offsets.Add(matches[i]);return offsets;}
    // The Tab indent's order: one insert per line from the first line to the last.
    static List<int> IndentOffsets(string body,int lines){
        var offsets=new List<int>(lines+1);int at=0;
        for(int line=0;line<lines&&at<body.Length;line++){offsets.Add(at);int next=body.IndexOf('\n',at);if(next<0)break;at=next+1;}
        return offsets;
    }
    static long ElapsedMilliseconds(long started){return (System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000L/System.Diagnostics.Stopwatch.Frequency;}
    // ------------------------------------------------ helpers
    static MainWindow CreateWindow(bool live){
        bool old=ProbeDefaults.Live;ProbeDefaults.Live=live;
        try{
            var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;window.SetMode(true);LongLineBench.Idle();window.UpdateLayout();
            return window;
        }finally{ProbeDefaults.Live=old;}
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{
            window.FlushDocumentChanges();window.saved=window.text;window.recoveredDocument=false;window.CaptureTab();
            foreach(var document in window.documents){document.Saved=document.Text;document.Recovered=false;}
            window.Close();
        }catch(Exception){}
    }
    static int UndoAll(MainWindow window){
        int steps=0;while(window.History.CanUndo&&steps<UndoCap*4){window.Undo();steps++;if((steps&127)==0)LongLineBench.Idle();}
        LongLineBench.Idle();return steps;
    }
    static int RedoAll(MainWindow window){
        int steps=0;while(window.History.CanRedo&&steps<UndoCap*4){window.Redo();steps++;if((steps&127)==0)LongLineBench.Idle();}
        LongLineBench.Idle();return steps;
    }
    static int UndoAll(DocumentHistory history,ref string value){
        int steps=0;while(history.CanUndo&&steps<UndoCap*4){value=history.Undo(value);steps++;}return steps;
    }
    static int RedoAll(DocumentHistory history,ref string value){
        int steps=0;while(history.CanRedo&&steps<UndoCap*4){value=history.Redo(value);steps++;}return steps;
    }
    static void LogFault(string where,Exception error){
        try{File.WriteAllText(Path.Combine(outRoot,"history-cap-crash.txt"),where+"\n"+(error==null?"<null>":error.ToString())+"\n\n"+report,new UTF8Encoding(false));}catch(Exception){}
    }
    static void Case(string name,bool ok,string detail){
        cases++;if(ok)passed++;else failed++;all&=ok;
        report.Append(ok?"PASS ":"FAIL ").Append(cases).Append(". ").Append(name);
        if(!String.IsNullOrEmpty(detail))report.Append(": ").Append(OneLine(detail));
        report.Append('\n');
        // A crash (an unhandled exception on a dispatcher turn, a stack overflow) leaves no report
        // at all; writing after each case keeps the completed part and the failing case's name.
        try{File.WriteAllText(Path.Combine(outRoot,"history-cap-progress.txt"),report.ToString(),new UTF8Encoding(false));}catch(Exception){}
    }
    static string Flags(DocumentHistory history){return "canUndo="+history.CanUndo+", canRedo="+history.CanRedo;}
    static bool EveryStepChanged(List<string> states){
        for(int i=1;i<states.Count;i++)if(states[i]==states[i-1])return false;
        return true;
    }
    static string OneLine(string value){return value==null?"<null>":value.Replace("\r\n","\\n").Replace("\r","\\r").Replace("\n","\\n");}
    static string Diff(string want,string got){
        if(want==null)want="<null>";if(got==null)got="<null>";
        int at=0,limit=Math.Min(want.Length,got.Length);
        while(at<limit&&want[at]==got[at])at++;
        return "firstDiff="+at+", want="+Context(want,at)+", got="+Context(got,at);
    }
    static string Context(string value,int at){
        int start=Math.Max(0,at-24),end=Math.Min(value.Length,at+24);
        return "\""+OneLine(value.Substring(start,end-start))+"\"";
    }
    // A token longer than the 2-character typing merge, without Markdown meaning.
    static string Token(int index){return "z"+index.ToString("0000",CultureInfo.InvariantCulture)+";";}
    // 1 MB (1,048,576 UTF-16 chars) in 4096 lines, so the source surface never holds one long line.
    static string ByteChunk(int index){
        string line=ChunkHeader(index).PadRight(ChunkLineChars-1,(char)('a'+(index-1)%26))+"\n";
        var builder=new StringBuilder(ChunkChars);
        for(int i=0;i<ChunkLines;i++)builder.Append(line);
        return builder.ToString();
    }
    static string ChunkHeader(int index){return "CHUNK"+index.ToString("000",CultureInfo.InvariantCulture)+":";}
    static string BigText(int count){
        var builder=new StringBuilder(ChunkChars*count);
        for(int i=0;i<count;i++)builder.Append(BigChunks[i]);
        return builder.ToString();
    }
}}}
