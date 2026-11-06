using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// --ime-abuse-probe <folder>: seeded IME composition sequences on the live source surface (Basic
// and Live view). Every composition starts at a random document place (paragraph, bold run edge,
// list/quote/heading, table cell including an empty one and the escaped pipe, code block,
// document start/end, sometimes with a selection), is updated several times the way Korean
// syllables, Japanese romaji readings and Chinese pinyin strings grow, and is then ended one of
// the ways a real IME can end: Enter, Space-then-commit, a click on another line or cell, a
// Tab/arrow/Home/End key, Esc (cancel), a view switch, a toolbar command (bold/list/table) and a
// focus loss. The composition state is driven through the same synthetic IMM seams ImeProbe uses
// (the STARTCOMPOSITION session, WM_IME_COMPOSITION state, TextInput for the result text and
// ENDCOMPOSITION), because a headless run has no IME. The native candidate window, the glyph IMM
// draws and the reading->candidate conversion itself are therefore not observable here; the state
// machine, the insertion point, the caret, the single-copy rule, the table shape and the history
// are.
// Invariants after every sequence: no exception; the committed text appears exactly once at the
// composition point (or at the pressed point's documented side for a click); a cancel leaves the
// text alone; the caret follows the app's rule (after the committed text, or at the click target);
// table rows keep their cell counts; no command leaves an empty "****" pair; continuous typing
// (every IME commit included) is one undo step and undo restores the pre-composition text;
// no composition is left open (ImeBusy false).
// Fixtures are generated here from the seed; no user file is read.
// Report: <folder>\ime-abuse-probe.txt; ime-abuse-probe-PASS.txt only when every sequence kept
// every invariant.
internal static class ImeAbuseProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int FirstSeedDefault=1,LastSeedDefault=5,StepsPerSeedDefault=20;
    const long DefaultBudgetMilliseconds=280000;
    const int WM_IME_ENDCOMPOSITION=0x010E;
    const int GCS_COMPSTR=0x0008,GCS_RESULTSTR=0x0800;

    internal sealed class FailureInfo {
        internal int Seed,Step;
        internal string Mode,Action,Language,Invariant,Detail,Before,After,Steps;
    }
    internal sealed class StepContext {
        internal MainWindow Window;
        internal SourceEditor Editor;
        internal MethodInfo Note,Message;
        internal IntPtr Handle;
        internal string Ending,Language,Label,Place,Before,After,Committed;
        internal int Point,SelectionStart,SelectionLength;
        internal bool Whole;
        // The expected result of typing the committed clauses at this point, measured through the
        // app's own TextInput path before the composition starts (one entry per clause prefix).
        internal string[] RefTexts;
        internal int[] RefCarets;
    }

    static readonly Func<bool> KoreanLayout=delegate{return false;};
    static readonly Func<bool> NonKoreanLayout=delegate{return true;};

    static string outRoot;
    static int currentSeed,currentStep,sequenceFailures;
    static string currentMode,currentAction;
    static bool dispatcherFailed;
    static readonly List<string> dispatcherNotes=new List<string>();
    static readonly List<string> stepNames=new List<string>();
    static readonly List<string> skipNotes=new List<string>();
    static readonly Dictionary<string,int> opCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,int> checkCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,int> failCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,int> skipCounts=new Dictionary<string,int>();
    static readonly List<FailureInfo> failures=new List<FailureInfo>();
    static bool all;
    static Stopwatch watch;
    static long budgetMilliseconds;
    static int firstSeed=FirstSeedDefault,lastSeed=LastSeedDefault,stepsPerSeed=StepsPerSeedDefault;
    static int modeFilter=-1;   // -1 = both, 0 = basic only, 1 = live only
    static bool budgetReached;

    internal static int Run(string dir) {
        if(Environment.GetEnvironmentVariable("MDNOTE_IME_ABUSE_DIAG")=="1")return RunDiagnostics(dir);
        string folder=Path.GetFullPath(dir);
        Directory.CreateDirectory(folder);
        outRoot=folder;
        string passFile=Path.Combine(folder,"ime-abuse-probe-PASS.txt");
        string failFile=Path.Combine(folder,"ime-abuse-probe-FAIL.txt");
        if(File.Exists(passFile))File.Delete(passFile);
        if(File.Exists(failFile))File.Delete(failFile);
        ReadOptions();
        all=true;
        budgetReached=false;
        watch=new Stopwatch();
        watch.Start();
        var runs=new StringBuilder();
        bool oldSelf=Program.IsSelfTest;
        bool oldHide=false;
        bool oldLive=ProbeDefaults.Live;
        Dispatcher dispatcher=null;
        DispatcherUnhandledExceptionEventHandler handler=null;
        try{
            Program.IsSelfTest=true;
            oldHide=Static<bool>("hideMarkers");
            ProbeDefaults.Live=true;
            if(Application.Current==null)new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            dispatcher=Application.Current.Dispatcher;
            handler=delegate(object sender,DispatcherUnhandledExceptionEventArgs args){NoteDispatcher(args);};
            dispatcher.UnhandledException+=handler;
            Application.Current.DispatcherUnhandledException+=handler;
            for(int seed=firstSeed;seed<=lastSeed;seed++){
                for(int mode=0;mode<2;mode++){
                    if(modeFilter>=0&&modeFilter!=mode)continue;
                    if(watch.ElapsedMilliseconds>budgetMilliseconds){budgetReached=true;all=false;break;}
                    if(!RunSequence(seed,mode==1,runs))all=false;
                }
                if(budgetReached)break;
            }
        }catch(Exception ex){
            all=false;
            runs.Append("\nFATAL: ").Append(OneLine(ex.ToString())).Append('\n');
        }finally{
            try{if(dispatcher!=null&&handler!=null)dispatcher.UnhandledException-=handler;}catch(Exception){}
            try{if(Application.Current!=null&&handler!=null)Application.Current.DispatcherUnhandledException-=handler;}catch(Exception){}
            try{Static("hideMarkers",oldHide);}catch(Exception){}
            ProbeDefaults.Live=oldLive;
            Program.IsSelfTest=oldSelf;
            watch.Stop();
        }
        string text=ComposeReport(runs);
        try{
            File.WriteAllText(Path.Combine(folder,"ime-abuse-probe.txt"),text,new UTF8Encoding(false));
            if(all&&!budgetReached)File.WriteAllText(passFile,"PASS\n",new UTF8Encoding(false));
            else File.WriteAllText(failFile,text,new UTF8Encoding(false));
        }catch(IOException){
            return 1;
        }
        return all&&!budgetReached?0:1;
    }

    // A reduced run replays one seed/mode; the default (no variables) is the full suite.
    //   MDNOTE_IME_ABUSE_SEED=3 MDNOTE_IME_ABUSE_MODE=live MDNOTE_IME_ABUSE_STEPS=10
    //   MDNOTE_IME_ABUSE_BUDGET=600000  MDNOTE_IME_ABUSE_KIND=click
    static void ReadOptions() {
        firstSeed=FirstSeedDefault;lastSeed=LastSeedDefault;stepsPerSeed=StepsPerSeedDefault;
        budgetMilliseconds=DefaultBudgetMilliseconds;modeFilter=-1;endingFilter=null;
        string value=Environment.GetEnvironmentVariable("MDNOTE_IME_ABUSE_SEED");
        int number;
        if(!String.IsNullOrEmpty(value)&&Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)&&number>0){firstSeed=number;lastSeed=number;}
        value=Environment.GetEnvironmentVariable("MDNOTE_IME_ABUSE_STEPS");
        if(!String.IsNullOrEmpty(value)&&Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)&&number>0)stepsPerSeed=number;
        value=Environment.GetEnvironmentVariable("MDNOTE_IME_ABUSE_BUDGET");
        if(!String.IsNullOrEmpty(value)&&Int64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out budgetMilliseconds)&&budgetMilliseconds<=0)budgetMilliseconds=DefaultBudgetMilliseconds;
        value=Environment.GetEnvironmentVariable("MDNOTE_IME_ABUSE_MODE");
        if(!String.IsNullOrEmpty(value)){
            if(String.Equals(value,"basic",StringComparison.OrdinalIgnoreCase))modeFilter=0;
            else if(String.Equals(value,"live",StringComparison.OrdinalIgnoreCase))modeFilter=1;
        }
        value=Environment.GetEnvironmentVariable("MDNOTE_IME_ABUSE_KIND");
        if(!String.IsNullOrEmpty(value))endingFilter=value;
    }
    static string endingFilter;

    static bool RunSequence(int seed,bool liveMode,StringBuilder reportBuilder) {
        currentSeed=seed;
        currentMode=liveMode?"live":"basic";
        currentStep=0;
        currentAction="open";
        sequenceFailures=0;
        stepNames.Clear();
        dispatcherFailed=false;
        MainWindow window=null;
        string surface="(not opened)";
        int steps=0;
        var timer=Stopwatch.StartNew();
        try{
            window=CreateWindow();
            window.SetText(Fixture(seed));
            window.SetMode(true);
            Pump();
            Invoke(window,"SetLiveMode",liveMode);
            Pump();
            window.UpdateLayout();
            Invoke(window,"FlushDocumentChanges");
            bool code=Prop<bool>(window,"CodeMode"),editing=Field<bool>(window,"editing"),attached=Prop<bool>(window,"LivePreviewAttached");
            bool hide=Static<bool>("hideMarkers");
            surface="code="+code+", editing="+editing+", attached="+attached+", live="+(!hide)+", hide="+hide;
            if(!code||!editing||!attached||hide!=!liveMode){
                FailOpen("open","the live edit surface did not attach: "+surface);
            }else{
                var editor=Field<SourceEditor>(window,"source");
                var random=new Random(seed*104729+(liveMode?7919:17));
                string[] endings=BuildEndingPlan(random);
                string[] languages=BuildLanguagePlan(random);
                for(int step=1;step<=stepsPerSeed;step++){
                    if(watch.ElapsedMilliseconds>budgetMilliseconds){budgetReached=true;all=false;break;}
                    if(editor.Document==null)break;
                    steps++;
                    RunStep(window,editor,random,endings[step-1],languages[step-1],step);
                }
            }
            Invoke(window,"FlushDocumentChanges");
        }catch(Exception ex){
            FailOpen("open","seed exception: "+OneLine(ex.ToString()));
        }finally{
            CloseWindow(window);
            timer.Stop();
        }
        reportBuilder.Append("\n## seed ").Append(seed).Append(' ').Append(liveMode?"live":"basic").Append('\n');
        reportBuilder.Append("- fixture: generated (newline ").Append(seed%2==0?"CRLF":"LF").Append(")\n");
        reportBuilder.Append("- surface: ").Append(surface).Append('\n');
        reportBuilder.Append("- steps: ").Append(steps).Append(" of ").Append(stepsPerSeed).Append('\n');
        reportBuilder.Append("- run time: ").Append(timer.ElapsedMilliseconds).Append(" ms\n");
        reportBuilder.Append("- failures: ").Append(sequenceFailures).Append('\n');
        return sequenceFailures==0&&!budgetReached;
    }

    static void RunStep(MainWindow window,SourceEditor editor,Random random,string ending,string language,int step) {
        currentStep=step;
        dispatcherFailed=false;
        StepContext c=new StepContext();
        c.Window=window;
        c.Editor=editor;
        c.Message=typeof(SourceEditor).GetMethod("ImeProc",Members);
        c.Note=typeof(SourceEditor).GetMethod("NoteImeComposition",Members);
        if(c.Message==null||c.Note==null){Fail(c,"seam","SourceEditor.ImeProc/NoteImeComposition not found");return;}
        c.Handle=WindowHandle(editor);
        // A composition a previous step left open was already reported; clear the probe-only state
        // so this sequence continues on a defined surface.
        editor.ResetCompositionSimulation();
        c.Ending=ending;
        c.Language=language;
        c.Label=ending+":"+language;
        currentAction=c.Label;
        stepNames.Add(step.ToString(CultureInfo.InvariantCulture)+" "+c.Label);
        if(stepNames.Count>12)stepNames.RemoveAt(0);
        Bump(opCounts,c.Label);
        if(endingFilter!=null&&!String.Equals(endingFilter,ending,StringComparison.Ordinal))return;
        editor.NonKoreanImeLayout=language=="ko"?KoreanLayout:NonKoreanLayout;
        c.Whole=language!="ko"&&random.Next(100)<50;

        int at,selStart=0,selLen=0;
        string kind;
        bool selection=AllowsSelection(ending)&&random.Next(100)<15;
        if(!PickAnchor(editor,random,out at,out kind)){Skipped("anchor","no composition place found");return;}
        if(selection&&!PickSelection(editor,random,out selStart,out selLen))selection=false;
        PlaceAt(editor,at);
        if(selection){editor.Select(selStart,selLen);Pump();}
        int length=editor.Document.TextLength;
        if(editor.SelectionStart<0||editor.SelectionStart+editor.SelectionLength>length){editor.Select(length,0);Pump();selection=false;}
        c.SelectionStart=editor.SelectionStart;
        c.SelectionLength=editor.SelectionLength;
        c.Point=editor.CaretIndex;
        c.Before=DocText(editor);
        c.Place=kind+(selection?" + selection":"");
        currentMode=Static<bool>("hideMarkers")?"basic":"live";
        ClearTypingArm(window,editor,c.Point);
        Bump(opCounts,selection?"selection:"+language:"no-selection:"+language);

        int wanted=ending=="enter"||ending=="space"?1+random.Next(3):ending=="esc"?0:1;
        string[] clauses=Clauses(random,language,wanted);
        // An ordinary commit lands through the app's TextInput path, so its expected result is
        // measured by typing the same clauses before the composition opens: an undo while a
        // composition owns the store is skipped by the app's own guard (RestoreEditorHistory ->
        // CompositionOwnsStore), and a reference typed inside it could not be taken back.
        if(NeedsReference(ending)&&clauses.Length>0){
            bool built;
            if(ending=="mode"){
                // The typing rules follow the view, and this case flips it while composing:
                // measure in the mode the commit lands in, then flip back for the case itself.
                bool before=Static<bool>("hideMarkers");
                Invoke(window,"SetLiveMode",before);
                Pump();
                window.UpdateLayout();
                built=BuildReferences(c,clauses);
                Invoke(window,"SetLiveMode",!before);
                Pump();
                window.UpdateLayout();
            }else{
                built=BuildReferences(c,clauses);
            }
            if(!built){
                ending="esc";
                clauses=new string[0];
                c.Ending=ending;
                c.Label=ending+":"+language;
                currentAction=c.Label;
            }
        }
        // The references were measured from the placed selection; the composition starts there.
        // A caret guard can snap the re-placed caret (a hidden line prefix, a table row slot), so
        // read the settled selection back before the composition opens.
        editor.Select(c.SelectionStart,c.SelectionLength);
        Pump();
        c.SelectionStart=editor.SelectionStart;
        c.SelectionLength=editor.SelectionLength;
        c.Point=editor.CaretIndex;
        c.Before=DocText(editor);

        editor.SimulateImeComposition();
        int[] lengths=UpdateLengths(random,language);
        for(int i=0;i<lengths.Length;i++){
            c.Note.Invoke(editor,new object[]{(long)GCS_COMPSTR,null,lengths[i]});
            if(i==0||i==lengths.Length-1)Pump();
        }
        bool sameSource=DocText(editor)==c.Before;
        bool sameCaret=editor.CaretIndex==c.Point;
        bool sameSelection=editor.SelectionStart==c.SelectionStart&&editor.SelectionLength==c.SelectionLength;
        Check(c,"composition updates leave the source and caret alone",sameSource&&sameCaret&&sameSelection&&editor.ImeBusy,
            "text changed="+(!sameSource)+", caret="+editor.CaretIndex+"/"+c.Point+", selection="+editor.SelectionStart+"+"+editor.SelectionLength+"/"+c.SelectionStart+"+"+c.SelectionLength+", "+editor.CompositionState());
        Check(c,"composition updates guard the store",(bool)Invoke(window,"StoreGuardLive"),"store guard false while composing");
        try{
            switch(ending){
                case "enter":CaseCommit(c,clauses,"enter");break;
                case "space":CaseCommit(c,clauses,"space");break;
                case "click":CaseClick(c,clauses,random);break;
                case "nav":CaseNav(c,clauses,random);break;
                case "tab":CaseTab(c,clauses,random);break;
                case "esc":CaseCancel(c);break;
                case "mode":CaseMode(c,clauses);break;
                case "bold":CaseCommand(c,clauses,"bold");break;
                case "list":CaseCommand(c,clauses,"list");break;
                case "table":CaseCommand(c,clauses,"table");break;
                default:CaseFocus(c,clauses);break;
            }
        }catch(Exception ex){
            Fail(c,"exception",Describe(ex));
        }finally{
            editor.NonKoreanImeLayout=KoreanLayout;
        }
        if(dispatcherFailed){
            Fail(c,"dispatcher",LastDispatcherNote());
            dispatcherFailed=false;
        }
    }
    // ---------------------------------------------------------------- window and fixture
    static MainWindow CreateWindow() {
        var window=new MainWindow{ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Width=900,Height=700};
        window.Show();
        window.Left=-20000;
        return window;
    }
    static void CloseWindow(MainWindow window) {
        if(window==null)return;
        try{
            Invoke(window,"FlushDocumentChanges");
            SetField(window,"documentOperationBusy",false);
            SetField(window,"saved",Prop<string>(window,"text"));
            SetField(window,"recoveredDocument",false);
            window.Close();
        }catch(Exception){}
    }
    // Every surface a composition can start on: a paragraph, the bold run's inner/outer edges, a
    // list item, a quote, a heading, a table cell (header, body, empty, escaped pipe) and a code
    // block; the document start and end are always offered. Offsets are recomputed from the live
    // document each step, so a sequence keeps working after commands rewrote lines.
    static string Fixture(int seed) {
        string nl=seed%2==0?"\r\n":"\n";
        var text=new StringBuilder();
        Line(text,nl,"# 제목 줄");
        Line(text,nl,"");
        Line(text,nl,"문단 첫 줄입니다.");
        Line(text,nl,"");
        Line(text,nl,"**굵게 문장** 뒤 문장");
        Line(text,nl,"");
        Line(text,nl,"- 목록 항목 하나");
        Line(text,nl,"- 목록 항목 둘");
        Line(text,nl,"");
        Line(text,nl,"> 인용 문장입니다");
        Line(text,nl,"");
        Line(text,nl,"```cs");
        Line(text,nl,"int value = 1;");
        Line(text,nl,"```");
        Line(text,nl,"");
        Line(text,nl,"| 머리 | 둘째 | 셋째 |");
        Line(text,nl,"|---|---|---|");
        Line(text,nl,"| 칸글 |  | 끝 \\| 값 |");
        Line(text,nl,"| 둘째 줄 | 값 | 마지막 |");
        Line(text,nl,"");
        Line(text,nl,"마지막 줄");
        return text.ToString();
    }
    static void Line(StringBuilder text,string nl,string value) {
        text.Append(value).Append(nl);
    }

    // ---------------------------------------------------------------- places
    static bool PickAnchor(SourceEditor editor,Random random,out int offset,out string kind) {
        var document=editor.Document;
        var offsets=new List<int>();
        var kinds=new List<string>();
        int length=document.TextLength;
        AddAnchor(offsets,kinds,document,0,"document start");
        AddAnchor(offsets,kinds,document,length,"document end");
        int limit=Math.Min(document.LineCount,80);
        for(int number=1;number<=limit;number++){
            DocumentLine line=document.GetLineByNumber(number);
            string text=document.GetText(line);
            int start=line.Offset;
            if(text.Length==0)continue;
            // The delimiter row is structure, not cell text: the preview keeps typed input out of
            // it (MarkdownLivePreview.MultiCellEntering/TablePipeTyping) and the rendered table
            // gives it no caret stop. A composition there is not a place the app can put a caret.
            if(text.StartsWith("|",StringComparison.Ordinal)&&IsDelimiterLine(text))continue;
            AddAnchor(offsets,kinds,document,start,"line start");
            AddAnchor(offsets,kinds,document,line.EndOffset,"line end");
            if(text.StartsWith("#",StringComparison.Ordinal)){
                AddAnchor(offsets,kinds,document,start+2,"heading text start");
                if(line.Length>4)AddAnchor(offsets,kinds,document,start+4,"heading middle");
            }
            int pair=text.IndexOf("**",StringComparison.Ordinal);
            if(pair>=0){
                AddAnchor(offsets,kinds,document,start+pair,"before bold opener");
                AddAnchor(offsets,kinds,document,start+pair+2,"inside bold opener");
                AddAnchor(offsets,kinds,document,start+pair+3,"after bold opener");
                int close=text.IndexOf("**",pair+2,StringComparison.Ordinal);
                if(close>0){
                    AddAnchor(offsets,kinds,document,start+close,"before bold closer");
                    AddAnchor(offsets,kinds,document,start+close+2,"after bold closer");
                }
            }
            if(text.StartsWith("- ",StringComparison.Ordinal)||text.StartsWith("* ",StringComparison.Ordinal))AddAnchor(offsets,kinds,document,start+2,"list text start");
            if(text.StartsWith("> ",StringComparison.Ordinal))AddAnchor(offsets,kinds,document,start+2,"quote text start");
            bool fence=text.StartsWith("```",StringComparison.Ordinal)||text.StartsWith("~~~",StringComparison.Ordinal);
            if(fence)AddAnchor(offsets,kinds,document,line.EndOffset,"fence line end");
            else if(text.StartsWith("|",StringComparison.Ordinal)&&!IsDelimiterLine(text))AddCellAnchors(offsets,kinds,document,line,text);
        }
        if(offsets.Count==0){offset=-1;kind="none";return false;}
        int pick=random.Next(offsets.Count);
        offset=offsets[pick];
        kind=kinds[pick];
        return true;
    }
    static void AddAnchor(List<int> offsets,List<string> kinds,TextDocument document,int at,string name) {
        if(at<0||at>document.TextLength)return;
        // A caret between CR and LF is not a place the app or AvalonEdit moves a caret to.
        if(at>0&&at<document.TextLength&&document.GetCharAt(at-1)=='\r'&&document.GetCharAt(at)=='\n')return;
        offsets.Add(at);
        kinds.Add(name);
    }
    static void AddCellAnchors(List<int> offsets,List<string> kinds,TextDocument document,DocumentLine line,string text) {
        List<int> pipes=ScanPipes(text);
        int segmentStart=0;
        for(int i=0;i<=pipes.Count;i++){
            int segmentEnd=i<pipes.Count?pipes[i]:text.Length;
            int a=segmentStart,b=segmentEnd;
            while(a<b&&(text[a]==' '||text[a]=='\t'))a++;
            while(b>a&&(text[b-1]==' '||text[b-1]=='\t'))b--;
            bool interior=i>0&&i<pipes.Count;
            if(a!=b||interior){
                AddAnchor(offsets,kinds,document,line.Offset+a,"table cell start");
                if(b-a>=2)AddAnchor(offsets,kinds,document,line.Offset+(a+b)/2,"table cell middle");
                AddAnchor(offsets,kinds,document,line.Offset+b,"table cell end");
            }
            segmentStart=segmentEnd+1;
        }
    }
    // A caret move that leaves and returns makes the next update its own history entry (the app's
    // own rule: a caret the user moved since the last input starts a new undo step). Without it a
    // commit that landed on the last typing run's exact end would legitimately merge with it, and
    // the undo oracle could not tell how many steps the sequence itself wrote.
    static void PlaceAt(SourceEditor editor,int at) {
        int length=editor.Document.TextLength;
        at=Math.Max(0,Math.Min(at,length));
        int away=at>0?at-1:Math.Min(1,length);
        if(away!=at&&away>=0&&away<=length){
            editor.Select(away,0);
            Pump();
        }
        editor.Select(at,0);
        Pump();
    }
    static bool AllowsSelection(string ending) {
        return ending=="enter"||ending=="space";
    }
    // Endings whose result text lands through AvalonEdit's ordinary TextInput path (a plain IME
    // commit): their expected text is measured by typing. The click and focus-loss endings are
    // redirected to the remembered composition point by the app itself, and a cancel writes
    // nothing, so those do not use the reference.
    static bool NeedsReference(string ending) {
        return ending=="enter"||ending=="space"||ending=="nav"||ending=="tab"||
            ending=="mode"||ending=="bold"||ending=="list"||ending=="table";
    }
    // A caret-only style toggle on an earlier step can still be armed when the composition point
    // happens to be exactly its offset (a real caret move away drops it: LiveMarkdownCommands.
    // TypingStyleCaretMoved). Force that move so the reference typing and the commit see the same
    // unarmed state; the armed-pair behaviour itself is covered by ImeProbe's pending-style case.
    static void ClearTypingArm(MainWindow window,SourceEditor editor,int point) {
        FieldInfo open=typeof(MainWindow).GetField("typingOpen",Members);
        if(open==null||open.GetValue(window)==null)return;
        int length=editor.Document.TextLength;
        if(length==0)return;
        FieldInfo atField=typeof(MainWindow).GetField("typingAt",Members);
        int typingAt=atField==null?-1:(int)atField.GetValue(window);
        int[] candidates={point-1,point+1,0,length};
        int away=-1;
        for(int i=0;i<candidates.Length;i++){
            int candidate=candidates[i];
            if(candidate<0||candidate>length)continue;
            if(candidate==point||candidate==typingAt)continue;
            away=candidate;
            break;
        }
        if(away<0)return;
        editor.Select(away,0);
        Pump();
        editor.Select(point,0);
        Pump();
    }
    static bool PickSelection(SourceEditor editor,Random random,out int start,out int length) {
        start=0;length=0;
        var document=editor.Document;
        int lineCount=document.LineCount;
        for(int attempt=0;attempt<12;attempt++){
            DocumentLine line=document.GetLineByNumber(1+random.Next(lineCount));
            string text=document.GetText(line);
            if(line.Length<4)continue;
            if(text.IndexOf('|')>=0)continue;
            if(text.StartsWith("```",StringComparison.Ordinal))continue;
            int from=random.Next(Math.Max(1,line.Length-3));
            int max=Math.Min(3,line.Length-from);
            if(max<=0)continue;
            start=line.Offset+from;
            length=1+random.Next(max);
            return true;
        }
        return false;
    }
    static int PickClickTarget(SourceEditor editor,Random random) {
        var document=editor.Document;
        int length=document.TextLength;
        if(document.LineCount<=1)return length;
        DocumentLine line=document.GetLineByNumber(1+random.Next(document.LineCount));
        string text=document.GetText(line);
        if(text.StartsWith("|",StringComparison.Ordinal)&&!IsDelimiterLine(text)){
            List<int> pipes=ScanPipes(text);
            if(pipes.Count>0){
                int index=random.Next(pipes.Count);
                int a=index==0?0:pipes[index-1]+1;
                int b=pipes[index];
                while(a<b&&(text[a]==' '||text[a]=='\t'))a++;
                while(b>a&&(text[b-1]==' '||text[b-1]=='\t'))b--;
                return line.Offset+(a==b?a:b);
            }
        }
        return line.Offset+random.Next(line.Length+1);
    }

    // ---------------------------------------------------------------- composition plans
    static string[] BuildEndingPlan(Random random) {
        string[] required={"enter","space","click","nav","tab","esc","mode","bold","list","table","focus"};
        var list=new List<string>();
        for(int i=0;i<required.Length&&list.Count<stepsPerSeed;i++)list.Add(required[i]);
        while(list.Count<stepsPerSeed)list.Add(PickEnding(random));
        Shuffle(list,random);
        return list.ToArray();
    }
    static string[] BuildLanguagePlan(Random random) {
        var list=new List<string>();
        for(int i=0;i<3;i++){list.Add("ko");list.Add("ja");list.Add("zh");}
        while(list.Count<stepsPerSeed)list.Add(PickLanguage(random));
        if(list.Count>stepsPerSeed)list.RemoveRange(stepsPerSeed,list.Count-stepsPerSeed);
        Shuffle(list,random);
        return list.ToArray();
    }
    static void Shuffle(List<string> list,Random random) {
        for(int i=list.Count-1;i>0;i--){
            int j=random.Next(i+1);
            string value=list[i];list[i]=list[j];list[j]=value;
        }
    }
    static string PickEnding(Random random) {
        int roll=random.Next(100);
        if(roll<22)return "enter";
        if(roll<38)return "space";
        if(roll<56)return "click";
        if(roll<68)return "nav";
        if(roll<75)return "tab";
        if(roll<85)return "esc";
        if(roll<89)return "mode";
        if(roll<93)return "bold";
        if(roll<96)return "list";
        if(roll<98)return "table";
        return "focus";
    }
    static string PickLanguage(Random random) {
        int roll=random.Next(100);
        if(roll<40)return "ko";
        if(roll<75)return "ja";
        return "zh";
    }
    // The composition string lengths an IME reports while the reading grows: Korean composes one
    // syllable (jamo runs), Japanese romaji readings grow over a long phrase, Pinyin strings grow
    // in Latin letters. The app only reads the length from this message; the glyph is IMM's.
    static int[] UpdateLengths(Random random,string language) {
        int count=3+random.Next(4);
        var lengths=new int[count];
        int value=1;
        for(int i=0;i<count;i++){
            if(language=="ko")value=(i%2==0)?1:2;
            else if(language=="ja")value=Math.Min(14,1+(i+1)*(1+random.Next(2)));
            else value=Math.Min(9,1+(i+1)*(1+random.Next(2)));
            lengths[i]=value;
        }
        return lengths;
    }
    static string[] Clauses(Random random,string language,int wanted) {
        if(wanted<=0)return new string[0];
        var list=new List<string>();
        if(language=="ko"){
            string[] pool={"가","나","다","한","글","값","문","장","기","호"};
            for(int i=0;i<wanted;i++)list.Add(pool[random.Next(pool.Length)]+pool[random.Next(pool.Length)]);
            // A Korean IME can also hand a whole word over in one result.
            if(wanted==1&&random.Next(100)<40)list[0]=list[0]+pool[random.Next(pool.Length)];
        }else if(language=="ja"){
            string[] phrases={"日本語","日本語を勉強","てすと","漢字","変換","かな","これは二十文字を超える長い日本語の文章です"};
            for(int i=0;i<wanted;i++)list.Add(phrases[random.Next(phrases.Length)]);
        }else{
            string[] phrases={"中文","汉语","输入法","明","，","。"};
            for(int i=0;i<wanted;i++)list.Add(phrases[random.Next(phrases.Length)]);
        }
        return list.ToArray();
    }
    static string Join(string[] values) {
        if(values==null)return "";
        var text=new StringBuilder();
        for(int i=0;i<values.Length;i++)text.Append(values[i]);
        return text.ToString();
    }

    // ---------------------------------------------------------------- endings
    // The result text arrives the way WM_CHAR delivers it: one TextInput per character for a
    // Korean syllable stream, or the whole clause for a Japanese/Chinese result. Either way the
    // counted result is consumed on TextEntering (SourceEditor.ImeResultEntering).
    // reference: the expected document was measured by typing the same clauses through the same
    // TextInput path (BuildReferences), so an intermediate clause must match that prefix.
    static string CommitResult(StepContext c,string[] clauses,bool pumpBetween,bool reference) {
        var all=new StringBuilder();
        for(int i=0;i<clauses.Length;i++){
            string clause=clauses[i];
            bool last=i+1>=clauses.Length;
            int remaining=last?0:Math.Max(1,clauses[i+1].Length);
            long flags=last?(long)GCS_RESULTSTR:(long)(GCS_RESULTSTR|GCS_COMPSTR);
            c.Note.Invoke(c.Editor,new object[]{flags,clause,remaining});
            Deliver(c,clause);
            if(pumpBetween)Pump();
            all.Append(clause);
            c.Committed=all.ToString();
            if(!last&&reference){
                string expected=c.RefTexts[i];
                bool ok=String.Equals(DocText(c.Editor),expected,StringComparison.Ordinal)&&c.Editor.ImeBusy;
                Check(c,"partial commit lands at the composition point and keeps composing",ok,
                    "committed=["+OneLine(c.Committed)+"], "+Diff(expected,DocText(c.Editor))+", "+c.Editor.CompositionState());
            }
        }
        End(c);
        if(pumpBetween)Pump();
        return all.ToString();
    }
    static void Deliver(StepContext c,string text) {
        if(String.IsNullOrEmpty(text))return;
        if(c.Whole){c.Editor.TextArea.PerformTextInput(text);return;}
        for(int i=0;i<text.Length;i++)c.Editor.TextArea.PerformTextInput(text.Substring(i,1));
    }
    static void End(StepContext c) {
        c.Message.Invoke(c.Editor,new object[]{c.Handle,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
    }
    // An ordinary commit reaches the document through AvalonEdit's TextInput path, so the app's
    // typing rules decide where it lands: the hidden line prefix is skipped, the outer edge of a
    // hidden closing run takes the text inside the pair, a table row keeps text in its cells, the
    // delimiter row keeps input out, and an empty line under a table keeps its separator. The
    // expected value is therefore measured, not assumed: type the same clauses through the same
    // TextInput path on the same surface, record the text and caret, then undo back. The
    // redirected click/late paths are different by design (CompositionCommitEntering/
    // AcceptLateImeCommit insert at the remembered composition point) and keep the raw expectation.
    static bool BuildReferences(StepContext c,string[] clauses) {
        if(clauses.Length==0)return true;
        c.RefTexts=new string[clauses.Length];
        c.RefCarets=new int[clauses.Length];
        for(int k=1;k<=clauses.Length;k++){
            var prefix=new string[k];
            for(int i=0;i<k;i++)prefix[i]=clauses[i];
            string text;int caret;string problem;
            if(!TypeReference(c,prefix,out text,out caret,out problem)){
                Skipped("reference","typing the expected text did not undo to the pre-composition state ("+problem+")");
                return false;
            }
            c.RefTexts[k-1]=text;
            c.RefCarets[k-1]=caret;
        }
        return true;
    }
    static bool TypeReference(StepContext c,string[] clauses,out string text,out int caret,out string problem) {
        text=null;caret=-1;problem=null;
        int start=c.SelectionStart,length=c.SelectionLength;
        for(int attempt=0;attempt<2;attempt++){
            // Measure from the state the commit will insert from. A caret guard can settle a
            // re-placed caret onto a hidden marker's visible boundary; the settled selection is
            // the one both insertions start from.
            c.Editor.Select(start,length);
            Pump();
            start=c.Editor.SelectionStart;
            length=c.Editor.SelectionLength;
            for(int i=0;i<clauses.Length;i++){
                Deliver(c,clauses[i]);
                Pump();
            }
            text=DocText(c.Editor);
            caret=c.Editor.CaretIndex;
            int steps=0;
            while(steps<3&&!String.Equals(DocText(c.Editor),c.Before,StringComparison.Ordinal)){
                Invoke(c.Window,"RestoreEditorHistory",false);
                steps++;
            }
            bool restored=String.Equals(DocText(c.Editor),c.Before,StringComparison.Ordinal);
            // Undo puts back the selection the reference started with; re-place it so the
            // composition starts from the same state.
            c.Editor.Select(start,length);
            Pump();
            int settled=c.Editor.SelectionStart,settledLength=c.Editor.SelectionLength;
            if(restored&&settled==start&&settledLength==length){
                // The app's caret guard may settle the requested offset onto a visible boundary
                // (a hidden line prefix, a table row's own cell stop). That settled stop is where
                // both the reference and the commit start, so keep it in the context.
                c.SelectionStart=start;
                c.SelectionLength=length;
                return true;
            }
            if(!restored){
                problem="undo did not restore the text in "+steps+" step(s): "+Diff(c.Before,DocText(c.Editor));
                return false;
            }
            // The line was parsed for the first time between the two placements, so the guard now
            // settles the same offset differently. Measure again from the settled boundary; it is
            // a fixed point on the second pass.
            start=settled;
            length=settledLength;
        }
        problem="the settled selection moved on every pass";
        return false;
    }
    // Enter and Space both end the composition through the IME's result; the key itself is
    // consumed by the IME on a real session, so no newline or space of its own may appear.
    static void CaseCommit(StepContext c,string[] clauses,string how) {
        if(clauses.Length==0){CaseCancel(c);return;}
        string insert=CommitResult(c,clauses,true,true);
        CheckCommitted(c,c.RefTexts[clauses.Length-1],c.RefCarets[clauses.Length-1],insert);
        CheckHistory(c,clauses.Length);
    }
    // J4a shape: the window's tunnelling press asks for the commit at the pre-click caret, the
    // press then moves the caret, and the counted result must still land at the composition point.
    static void CaseClick(StepContext c,string[] clauses,Random random) {
        string clause=clauses.Length>0?clauses[0]:"가";
        c.Window.mouseDownCompositionCommitted=false;
        c.Window.mouseDownCommitCaret=-1;
        RaiseMouseDown(c.Editor);
        Check(c,"click press asks for the commit at the pre-click caret",
            c.Window.mouseDownCompositionCommitted&&c.Window.mouseDownCommitCaret==c.Point,
            "committed="+c.Window.mouseDownCompositionCommitted+", commit caret="+c.Window.mouseDownCommitCaret+", point="+c.Point+", requests="+c.Editor.CompositionRequests);
        int clicked=PickClickTarget(c.Editor,random);
        c.Editor.Select(Math.Max(0,Math.Min(clicked,c.Editor.Document.TextLength)),0);
        Pump();
        clicked=c.Editor.CaretIndex;
        // The redirected commit writes at the remembered composition point through the app's own
        // cell clamp (MarkdownLivePreview.ClampCommitOffset): an offset outside every cell of its
        // row is pulled back into the nearest one, so a syllable never lands after the trailing
        // '|'. The clamp depends on the caret line at insertion time, so ask it here, after the
        // press moved the caret and before the result enters.
        int at=c.Point;
        if(c.Editor.CellClamp!=null)at=c.Editor.CellClamp(at);
        at=Math.Max(0,Math.Min(at,c.Before.Length));
        c.Note.Invoke(c.Editor,new object[]{(long)GCS_RESULTSTR,clause,0});
        Deliver(c,clause);
        End(c);
        Pump();
        // 11-05 rule: a result at the outer edge of a closed bold/italic/strike run lands inside the pair on this route too
        // (seed 2 live step 4 click:ja), so the write point is the same InsidePoint the plain commit expects.
        int landed=InsidePoint(c.Before,at,clause);
        int caret=clicked>=landed?clicked+clause.Length:clicked;
        CheckCommitted(c,c.Before.Insert(landed,clause),caret,clause);
        CheckHistory(c,1);
    }
    // A key the IME does not consume (an arrow, Home/End) commits first and then acts; only the
    // commit may change the text, and the navigation's own effect is caret-only.
    static void CaseNav(StepContext c,string[] clauses,Random random) {
        string clause=clauses.Length>0?clauses[0]:"가";
        string insert=CommitResult(c,new string[]{clause},true,true);
        CheckCommitted(c,c.RefTexts[0],c.RefCarets[0],insert);
        string afterCommit=DocText(c.Editor);
        NavigationCommand(random.Next(6)).Execute(null,c.Editor.TextArea);
        Pump();
        Check(c,"navigation after the commit leaves the text alone",DocText(c.Editor)==afterCommit,Diff(afterCommit,DocText(c.Editor)));
        Check(c,"no composition left open after navigation",SourceIdle(c.Window,c.Editor),c.Editor.CompositionState());
        CheckHistory(c,1);
    }
    // Tab goes through the app's own Tab path (a cell move, an indent or a literal tab).
    static void CaseTab(StepContext c,string[] clauses,Random random) {
        string clause=clauses.Length>0?clauses[0]:"가";
        string insert=CommitResult(c,new string[]{clause},true,true);
        CheckCommitted(c,c.RefTexts[0],c.RefCarets[0],insert);
        bool ran=c.Window.SourceTabPathProbe(false);
        Pump();
        if(!ran){
            Skipped("tab","the Tab path declined at this place (no cell move or indent rule applies; the commit itself was checked)");
        }else{
            Check(c,"tab after the commit keeps the committed text once",CountDelta(c.Before,DocText(c.Editor),insert)==1,
                "count before="+Count(c.Before,insert)+", after="+Count(DocText(c.Editor),insert)+", "+Diff(c.RefTexts[0],DocText(c.Editor)));
            Check(c,"table rows keep their cell counts",SameTableShapeAfterTab(c.RefTexts[0],DocText(c.Editor)),TableShapeDiff(c.RefTexts[0],DocText(c.Editor)));
        }
        Check(c,"no composition left open after tab",SourceIdle(c.Window,c.Editor),c.Editor.CompositionState());
        CheckHistory(c,2);
    }
    // Esc empties the composition string; nothing may be written and the caret stays.
    static void CaseCancel(StepContext c) {
        c.Note.Invoke(c.Editor,new object[]{(long)GCS_COMPSTR,null,0});
        End(c);
        Pump();
        string text=DocText(c.Editor);
        c.After=text;
        Check(c,"cancel leaves the source unchanged",text==c.Before,Diff(c.Before,text));
        Check(c,"cancel leaves the caret at the composition point",c.Editor.CaretIndex==c.Point,"caret="+c.Editor.CaretIndex+", point="+c.Point);
        Check(c,"cancel leaves the selection alone",c.Editor.SelectionStart==c.SelectionStart&&c.Editor.SelectionLength==c.SelectionLength,
            "selection="+c.Editor.SelectionStart+"+"+c.Editor.SelectionLength+"/"+c.SelectionStart+"+"+c.SelectionLength);
        Check(c,"no composition left open after cancel",SourceIdle(c.Window,c.Editor),c.Editor.CompositionState());
    }
    // A view switch while composing: the app's SetLiveMode runs FinishComposition first, so the
    // result that arrives afterwards must still land at the composition point.
    static void CaseMode(StepContext c,string[] clauses) {
        bool beforeHide=Static<bool>("hideMarkers");
        Invoke(c.Window,"SetLiveMode",beforeHide);
        Pump();
        c.Window.UpdateLayout();
        bool afterHide=Static<bool>("hideMarkers");
        currentMode=afterHide?"basic":"live";
        Check(c,"view switch flips the mode",afterHide==!beforeHide,"hideMarkers before="+beforeHide+", after="+afterHide);
        // The expected text was measured in this mode from this selection; re-place it so the
        // commit starts from the same caret the reference typed from.
        c.Editor.Select(c.SelectionStart,c.SelectionLength);
        Pump();
        string insert=CommitResult(c,clauses,true,true);
        CheckCommitted(c,c.RefTexts[clauses.Length-1],c.RefCarets[clauses.Length-1],insert);
        CheckHistory(c,clauses.Length);
    }
    // A toolbar command during the composition: the app claims it and runs it only after the
    // commit has landed (SourceEditor.RunAfterComposition), so the source right after END must hold
    // the commit alone, and the command then edits the committed text rather than a stale one.
    static void CaseCommand(StepContext c,string[] clauses,string kind) {
        string clause=clauses.Length>0?clauses[0]:"가";
        if(kind=="bold")Invoke(c.Window,"ToggleFormat","bold");
        else if(kind=="list")Invoke(c.Window,"ToggleListStyle",false);
        else Invoke(c.Window,"InsertTable",2,2);
        Pump();
        string insert=CommitResult(c,new string[]{clause},false,true);
        string text=DocText(c.Editor);
        c.After=text;
        string expected=c.RefTexts[0];
        Check(c,"a command during the composition does not write before the commit",text==expected,
            "expected length="+expected.Length+", "+Diff(expected,text));
        Check(c,"no composition left open",SourceIdle(c.Window,c.Editor),c.Editor.CompositionState());
        Pump();
        text=DocText(c.Editor);
        c.After=text;
        Check(c,"the parked command keeps the committed text once",CountDelta(c.Before,text,insert)==1,
            "count before="+Count(c.Before,insert)+", after="+Count(text,insert)+", "+Diff(expected,text));
        Check(c,"no empty emphasis pair",text.IndexOf("****",StringComparison.Ordinal)<0,"an empty marker pair is in the source");
        if(kind!="table")Check(c,"table rows keep their cell counts",SameTableShape(expected,text),TableShapeDiff(expected,text));
        Check(c,"no composition left open after the command",SourceIdle(c.Window,c.Editor),c.Editor.CompositionState());
        CheckHistory(c,2);
    }
    // The menu/focus path: BeginCompositionCommit remembers the caret, focus leaves, and the
    // counted result is adopted late at that remembered caret (SourceEditor.AcceptLateImeCommit).
    static void CaseFocus(StepContext c,string[] clauses) {
        string clause=clauses.Length>0?clauses[0]:"가";
        c.Editor.BeginCompositionCommit();
        Keyboard.ClearFocus();
        Pump();
        bool away=!c.Editor.TextArea.IsKeyboardFocusWithin;
        c.Note.Invoke(c.Editor,new object[]{(long)GCS_RESULTSTR,clause,0});
        bool adopted=false;
        if(away)adopted=c.Editor.AcceptLateImeCommit(clause);
        else c.Editor.TextArea.PerformTextInput(clause);
        End(c);
        Pump();
        if(away)Check(c,"focus loss adopts the result at the remembered caret",adopted,"AcceptLateImeCommit declined the counted result");
        else Skipped("focus","keyboard focus could not be cleared in this run");
        CheckCommitted(c,ExpectedText(c,clause),ExpectedCaret(c,clause),clause);
        try{c.Editor.TextArea.Focus();Pump();}catch(Exception){}
        CheckHistory(c,1);
    }
    static void RaiseMouseDown(SourceEditor editor) {
        var down=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left);
        down.RoutedEvent=UIElement.PreviewMouseDownEvent;
        down.Source=editor.TextArea.TextView;
        editor.TextArea.TextView.RaiseEvent(down);
    }
    static RoutedUICommand NavigationCommand(int index) {
        switch(index){
            case 0:return EditingCommands.MoveLeftByCharacter;
            case 1:return EditingCommands.MoveRightByCharacter;
            case 2:return EditingCommands.MoveUpByLine;
            case 3:return EditingCommands.MoveDownByLine;
            case 4:return EditingCommands.MoveToLineStart;
            default:return EditingCommands.MoveToLineEnd;
        }
    }
    // ---------------------------------------------------------------- typing-rule diagnostic
    // MDNOTE_IME_ABUSE_DIAG=1: a deterministic matrix of the app's own typing rules at the places
    // the fuzz places compositions (hidden line prefix, hidden closing run edges, table cells and
    // the gap between them, the delimiter row, ordinary lines). It types the same character
    // through the normal TextInput path and through a synthetic IME result, so the probe's
    // expected values can be derived from observed app behaviour instead of assumptions.
    // Report: <folder>\ime-abuse-probe-diag.txt (not part of the pass/fail run).
    internal static int RunDiagnostics(string dir) {
        string folder=Path.GetFullPath(dir);
        Directory.CreateDirectory(folder);
        var report=new StringBuilder();
        report.Append("# ImeAbuseProbe diagnostics\n\n");
        bool oldSelf=Program.IsSelfTest;
        bool oldLive=ProbeDefaults.Live;
        bool oldHide=false;
        MainWindow window=null;
        bool ok=true;
        try{
            Program.IsSelfTest=true;
            oldHide=Static<bool>("hideMarkers");
            ProbeDefaults.Live=true;
            if(Application.Current==null)new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            window=CreateWindow();
            var editor=Field<SourceEditor>(window,"source");
            MethodInfo note=typeof(SourceEditor).GetMethod("NoteImeComposition",Members);
            MethodInfo message=typeof(SourceEditor).GetMethod("ImeProc",Members);
            IntPtr handle=WindowHandle(editor);
            string[] names={"doc-start","heading+2","heading-end","after-bold-close","before-bold-close","inside-bold-close","after-bold-open","inside-bold-open","para-start","para-end","cell-end","empty-cell","cell-gap","delimiter-end","doc-end"};
            foreach(bool live in new[]{false,true}){
                window.SetText(Fixture(1));
                window.SetMode(true);
                Pump();
                Invoke(window,"SetLiveMode",live);
                Pump();
                window.UpdateLayout();
                report.Append("\n## ").Append(live?"live":"basic").Append(" (hideMarkers=").Append(Static<bool>("hideMarkers")).Append(")\n");
                for(int i=0;i<names.Length;i++){
                    window.SetText(Fixture(1));
                    window.SetMode(true);
                    Pump();
                    string problem;
                    int at=DiagnosticOffset(editor,names[i],out problem);
                    if(at<0){report.Append("- ").Append(names[i]).Append(": ").Append(problem).Append('\n');continue;}
                    string before=DocText(editor);
                    PlaceAt(editor,at);
                    int placed=editor.CaretIndex;
                    editor.TextArea.PerformTextInput("@");
                    Pump();
                    string typed=DocText(editor);
                    int typedAt=InsertedAt(before,typed);
                    int typedCaret=editor.CaretIndex;
                    Invoke(window,"RestoreEditorHistory",false);
                    Pump();
                    string undone=DocText(editor);
                    int undoneCaret=editor.CaretIndex;
                    window.SetText(Fixture(1));
                    window.SetMode(true);
                    Pump();
                    PlaceAt(editor,at);
                    int placed2=editor.CaretIndex;
                    editor.SimulateImeComposition();
                    note.Invoke(editor,new object[]{(long)GCS_RESULTSTR,"@",0});
                    editor.TextArea.PerformTextInput("@");
                    message.Invoke(editor,new object[]{handle,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
                    Pump();
                    string ime=DocText(editor);
                    int imeAt=InsertedAt(before,ime);
                    int imeCaret=editor.CaretIndex;
                    report.Append("- ").Append(names[i]).Append(": raw=").Append(at)
                        .Append(" placed=").Append(placed).Append('/').Append(placed2)
                        .Append(" typedAt=").Append(typedAt).Append(" typedCaret=").Append(typedCaret)
                        .Append(" undoText=").Append(undone==before?"same":"diff").Append(" undoAt=").Append(undoneCaret)
                        .Append(" imeAt=").Append(imeAt).Append(" imeCaret=").Append(imeCaret)
                        .Append(" imeIdle=").Append(SourceIdle(window,editor)?"true":"false")
                        .Append('\n');
                    if(typedAt!=imeAt||typedCaret!=imeCaret)report.Append("  ! typed and IME paths differ\n");
                }
            }
        }catch(Exception ex){
            ok=false;
            report.Append("\nFATAL: ").Append(OneLine(ex.ToString())).Append('\n');
        }finally{
            CloseWindow(window);
            try{Static("hideMarkers",oldHide);}catch(Exception){}
            ProbeDefaults.Live=oldLive;
            Program.IsSelfTest=oldSelf;
        }
        try{
            File.WriteAllText(Path.Combine(folder,"ime-abuse-probe-diag.txt"),report.ToString(),new UTF8Encoding(false));
            if(ok)File.WriteAllText(Path.Combine(folder,"ime-abuse-probe-diag-PASS.txt"),"PASS\n",new UTF8Encoding(false));
        }catch(IOException){
            return 1;
        }
        return ok?0:1;
    }
    static int InsertedAt(string before,string after) {
        if(after==null||before==null||after.Length!=before.Length+1)return -1;
        int at=after.IndexOf('@');
        if(at<0)return -1;
        return String.Equals(after.Remove(at,1),before,StringComparison.Ordinal)?at:-1;
    }
    // The place the case names, read from the fixture's own lines (searched, so the offsets are
    // the live document's, not constants).
    static int DiagnosticOffset(SourceEditor editor,string name,out string problem) {
        problem=null;
        var document=editor.Document;
        int length=document.TextLength;
        if(name=="doc-start")return 0;
        if(name=="doc-end")return length;
        for(int number=1;number<=document.LineCount;number++){
            DocumentLine line=document.GetLineByNumber(number);
            string text=document.GetText(line);
            int start=line.Offset;
            if(name=="heading+2"&&text.StartsWith("# ",StringComparison.Ordinal))return start+2;
            if(name=="heading-end"&&text.StartsWith("# ",StringComparison.Ordinal))return line.EndOffset;
            if(name=="para-start"&&text=="문단 첫 줄입니다.")return start;
            if(name=="para-end"&&text=="문단 첫 줄입니다.")return line.EndOffset;
            if(name=="after-bold-close"){
                int close=text.LastIndexOf("**",StringComparison.Ordinal);
                if(close>0)return start+close+2;
            }
            if(name=="before-bold-close"){
                int open=text.IndexOf("**",StringComparison.Ordinal);
                int close=open>=0?text.IndexOf("**",open+2,StringComparison.Ordinal):-1;
                if(close>0)return start+close;
            }
            if(name=="inside-bold-close"){
                int open=text.IndexOf("**",StringComparison.Ordinal);
                int close=open>=0?text.IndexOf("**",open+2,StringComparison.Ordinal):-1;
                if(close>0)return start+close+1;
            }
            if(name=="after-bold-open"&&text.IndexOf("**",StringComparison.Ordinal)>=0)return start+text.IndexOf("**",StringComparison.Ordinal)+2;
            if(name=="inside-bold-open"&&text.IndexOf("**",StringComparison.Ordinal)>=0)return start+text.IndexOf("**",StringComparison.Ordinal)+1;
            if(!text.StartsWith("|",StringComparison.Ordinal)||IsDelimiterLine(text))continue;
            List<int> pipes=ScanPipes(text);
            if(name=="cell-end"&&pipes.Count>=2)return start+pipes[0];
            if(name=="empty-cell"&&pipes.Count>=3)return start+pipes[1]+1;
            if(name=="cell-gap"&&pipes.Count>=3)return start+pipes[1];
            if(name=="delimiter-end"&&IsDelimiterLine(text))return line.EndOffset;
        }
        // The delimiter row is rejected by the loop's table filter above; search it directly.
        if(name=="delimiter-end"){
            for(int number=1;number<=document.LineCount;number++){
                DocumentLine line=document.GetLineByNumber(number);
                if(IsDelimiterLine(document.GetText(line)))return line.EndOffset;
            }
        }
        problem="fixture place not found";
        return -1;
    }
    // ---------------------------------------------------------------- invariants
    static string ExpectedText(StepContext c,string insert) {
        if(c==null||c.Before==null)return insert;
        if(c.SelectionLength>0){
            int start=Math.Max(0,Math.Min(c.SelectionStart,c.Before.Length));
            int length=Math.Min(c.SelectionLength,c.Before.Length-start);
            return c.Before.Remove(start,length).Insert(start,insert);
        }
        int at=Math.Max(0,Math.Min(c.Point,c.Before.Length));
        return c.Before.Insert(InsidePoint(c.Before,at,insert),insert);
    }
    // 11-05 rule (HANDOFF.md:115, MarkdownLivePreview.InsertCommitInsideRun): a result written at the
    // outer edge of a closed inline style's closing run (bold, italic, strikethrough) lands inside
    // the pair, exactly like a typed character. The caret expectation is landed point+length
    // (ExpectedCaret): the caret stays inside the pair. This reads only the line: the run
    // of marker characters ending at `at`, with its mirrored opener earlier on the same line,
    // content between, and no whitespace against either marker. Table rows and fenced lines keep
    // the plain point (a cell end is declined by the product; a fence has no styles). A stack the
    // line cannot mirror (a closer that ends several differently-opened pairs) stays plain here.
    static int InsidePoint(string before,int at,string insert) {
        if(String.IsNullOrEmpty(insert)||String.IsNullOrWhiteSpace(insert)||insert.IndexOf('\r')>=0||insert.IndexOf('\n')>=0)return at;
        if(at<=0||at>before.Length)return at;
        int lineStart=before.LastIndexOf('\n',at-1)+1;
        int lineEnd=before.IndexOf('\n',at);
        if(lineEnd<0)lineEnd=before.Length;
        string text=before.Substring(lineStart,lineEnd-lineStart).TrimEnd('\r');
        string trimmed=text.TrimStart();
        if(trimmed.StartsWith("|",StringComparison.Ordinal)||trimmed.StartsWith("```",StringComparison.Ordinal)||trimmed.StartsWith("~~~",StringComparison.Ordinal))return at;
        int fences=0,scan=0;
        while(scan<lineStart){
            int next=before.IndexOf('\n',scan);
            if(next<0||next>=lineStart)break;
            string above=before.Substring(scan,next-scan).TrimStart();
            if(above.StartsWith("```",StringComparison.Ordinal)||above.StartsWith("~~~",StringComparison.Ordinal))fences++;
            scan=next+1;
        }
        if((fences&1)==1)return at;
        int run=0;
        while(at-run-1>=lineStart&&run<6&&IsStyleMarker(before[at-run-1]))run++;
        if(run==0||run>=6)return at;
        if(at<before.Length&&IsStyleMarker(before[at]))return at;
        string closers=before.Substring(at-run,run);
        if(closers=="~")return at;
        char[] mirror=closers.ToCharArray();
        Array.Reverse(mirror);
        string opener=new string(mirror);
        string head=before.Substring(lineStart,at-run-lineStart);
        int found=head.LastIndexOf(opener,StringComparison.Ordinal);
        if(found<0||head.Length-(found+run)<1)return at;
        if(char.IsWhiteSpace(head[found+run])||char.IsWhiteSpace(head[head.Length-1]))return at;
        return at-run;
    }
    static bool IsStyleMarker(char ch) {
        return ch=='*'||ch=='_'||ch=='~';
    }
    // 11-06: a result written inside the closing run keeps the caret inside the pair too
    // (Point - run + length), so the next syllable continues the style.
    static int ExpectedCaret(StepContext c,string insert) {
        int insertLength=insert==null?0:insert.Length;
        if(c.SelectionLength>0)return c.SelectionStart+insertLength;
        int at=c.Before==null?c.Point:Math.Max(0,Math.Min(c.Point,c.Before.Length));
        return (c.Before==null?at:InsidePoint(c.Before,at,insert))+insertLength;
    }
    static bool CheckCommitted(StepContext c,string expectedText,int expectedCaret,string insert) {
        string text=DocText(c.Editor);
        c.After=text;
        bool textOk=String.Equals(text,expectedText,StringComparison.Ordinal);
        Check(c,"commit writes the text once at the composition point",textOk,
            "insert=["+OneLine(insert)+"], expected length="+expectedText.Length+", "+Diff(expectedText,text));
        Check(c,"commit caret",c.Editor.CaretIndex==expectedCaret,
            "caret="+c.Editor.CaretIndex+", expected="+expectedCaret+", point="+c.Point+", length="+text.Length);
        int before=Count(c.Before,insert),after=Count(text,insert);
        Check(c,"commit text appears exactly once",after==before+1,
            "count before="+before+", after="+after+", insert=["+OneLine(insert)+"]");
        Check(c,"no composition left open",SourceIdle(c.Window,c.Editor),c.Editor.CompositionState());
        Check(c,"table rows keep their cell counts",SameTableShape(expectedText,text),TableShapeDiff(expectedText,text));
        return textOk;
    }
    // The app's rule: continuous typing is one undo step, and every IME commit (Korean, Japanese,
    // Chinese) is part of it, so N clauses need at most N presses and usually one; undo must
    // restore the pre-composition text exactly and redo must bring the whole sequence's text back.
    static void CheckHistory(StepContext c,int maxSteps) {
        string target=c.Before;
        string end=DocText(c.Editor);
        int steps=0;
        string first=null;
        while(steps<maxSteps&&!String.Equals(DocText(c.Editor),target,StringComparison.Ordinal)){
            Invoke(c.Window,"RestoreEditorHistory",false);
            steps++;
            if(steps==1)first=DocText(c.Editor);
        }
        bool restored=String.Equals(DocText(c.Editor),target,StringComparison.Ordinal);
        Check(c,"undo restores the pre-composition text",restored,
            "steps="+steps+" of "+maxSteps+", "+Diff(target,DocText(c.Editor))+(first==null?"":"; first=["+OneLine(first)+"]"));
        if(!restored||steps==0)return;
        for(int i=0;i<steps;i++)Invoke(c.Window,"RestoreEditorHistory",true);
        string redone=DocText(c.Editor);
        Check(c,"redo restores the committed text",String.Equals(redone,end,StringComparison.Ordinal),"steps="+steps+", "+Diff(end,redone));
    }
    static bool SourceIdle(MainWindow window,SourceEditor editor) {
        return !editor.ImeBusy&&!(bool)Invoke(window,"CompositionOwnsStore")&&!(bool)Invoke(window,"StoreGuardLive");
    }
    static bool Check(StepContext c,string name,bool ok,string detail) {
        Bump(checkCounts,name);
        if(ok)return true;
        return Fail(c,name,detail);
    }
    static bool Fail(StepContext c,string invariant,string detail) {
        Bump(failCounts,invariant);
        all=false;
        sequenceFailures++;
        var failure=new FailureInfo();
        failure.Seed=currentSeed;
        failure.Step=currentStep;
        failure.Mode=CurrentModeName();
        failure.Action=c==null?currentAction:c.Label;
        failure.Language=c==null?"-":c.Language;
        failure.Invariant=invariant;
        failure.Detail=OneLine(detail);
        failure.Before=c==null?"(none)":Excerpt(c.Before,c.Point);
        failure.After=c==null?"(none)":Excerpt(DocText(c.Editor),c.Point);
        failure.Steps=StepListText();
        failures.Add(failure);
        return false;
    }
    static void FailOpen(string invariant,string detail) {
        Bump(failCounts,invariant);
        all=false;
        sequenceFailures++;
        var failure=new FailureInfo();
        failure.Seed=currentSeed;
        failure.Step=currentStep;
        failure.Mode=CurrentModeName();
        failure.Action=currentAction;
        failure.Language="-";
        failure.Invariant=invariant;
        failure.Detail=OneLine(detail);
        failure.Before="(none)";
        failure.After="(none)";
        failure.Steps=StepListText();
        failures.Add(failure);
    }
    static void Skipped(string name,string detail) {
        Bump(skipCounts,name);
        if(skipNotes.Count>=30)return;
        skipNotes.Add("seed "+currentSeed+" "+CurrentModeName()+" step "+currentStep+" "+currentAction+": "+name+" ("+OneLine(detail)+")");
    }
    static string CurrentModeName() {
        try{return Static<bool>("hideMarkers")?"basic":"live";}catch(Exception){return currentMode;}
    }

    // ---------------------------------------------------------------- document model
    // Independent of the preview parser: a table row is a line that starts with a pipe (after its
    // indent) and is not a delimiter row; its cells follow the same escaped pipe / code span rule
    // the table commands use. The code-fence state is deliberately NOT part of this shape: a
    // committed character can break a fence line's leading "```" and a later "```" then reads as
    // an opener, which would hide every row below it from a fence-aware scan. A fence decides
    // rendering, not whether a row's cells changed; the probe's committed text contains no pipe,
    // so a pipe-leading line can only stop being a row if text was written in front of its pipe,
    // which is a real change the shape must show.
    static string TableShape(string text) {
        var shape=new StringBuilder();
        List<string> lines=Lines(text);
        for(int i=0;i<lines.Count;i++){
            string line=lines[i];
            string trimmed=line.TrimStart(' ','\t');
            if(!trimmed.StartsWith("|",StringComparison.Ordinal))continue;
            if(IsDelimiterLine(line))continue;
            if(shape.Length>0)shape.Append(',');
            shape.Append(CellCount(line).ToString(CultureInfo.InvariantCulture));
        }
        return shape.ToString();
    }
    static bool SameTableShape(string before,string after) {
        return String.Equals(TableShape(before),TableShape(after),StringComparison.Ordinal);
    }
    // Tab in the last cell of a table's last row appends one empty row there
    // (MarkdownStructuralInput.TableCellMove -> MarkdownTableCommands.AddRowAtEnd), so after a Tab
    // step the shape may carry exactly one extra row, right after the row it follows at the end of
    // its table. Every old row keeps its cell count in order, and the added row must repeat the
    // cell count of the row above it; a changed row or column, or two added rows, still fails.
    static bool SameTableShapeAfterTab(string before,string after) {
        string was=TableShape(before),now=TableShape(after);
        if(String.Equals(was,now,StringComparison.Ordinal))return true;
        if(was.Length==0||now.Length==0)return false;
        string[] oldRows=was.Split(',');
        string[] newRows=now.Split(',');
        if(newRows.Length!=oldRows.Length+1)return false;
        for(int at=1;at<newRows.Length;at++){
            bool same=true;
            for(int i=0;i<oldRows.Length;i++){
                int j=i<at?i:i+1;
                if(!String.Equals(oldRows[i],newRows[j],StringComparison.Ordinal)){same=false;break;}
            }
            if(same&&String.Equals(newRows[at],newRows[at-1],StringComparison.Ordinal))return true;
        }
        return false;
    }
    static string TableShapeDiff(string before,string after) {
        return "before=["+TableShape(before)+"], after=["+TableShape(after)+"]";
    }
    static bool IsDelimiterLine(string text) {
        bool seenPipe=false,colonAfter=false;
        int dashes=0,cells=0;
        for(int i=0;i<=text.Length;i++){
            char c=i==text.Length?'|':text[i];
            if(c==' '||c=='\t')continue;
            if(c=='|'){
                if(i<text.Length)seenPipe=true;
                if(dashes>0)cells++;
                else if(colonAfter)return false;
                dashes=0;colonAfter=false;
            }else if(c=='-'&&!colonAfter){
                dashes++;
            }else if(c==':'){
                if(colonAfter)return false;
                if(dashes>0)colonAfter=true;
            }else return false;
        }
        return seenPipe&&cells>0;
    }
    static int CellCount(string text) {
        List<int> pipes=ScanPipes(text);
        int count=0,segmentStart=0;
        for(int i=0;i<=pipes.Count;i++){
            int segmentEnd=i<pipes.Count?pipes[i]:text.Length;
            int a=segmentStart,b=segmentEnd;
            while(a<b&&(text[a]==' '||text[a]=='\t'))a++;
            while(b>a&&(text[b-1]==' '||text[b-1]=='\t'))b--;
            bool interior=i>0&&i<pipes.Count;
            if(a!=b||interior)count++;
            segmentStart=segmentEnd+1;
        }
        return count;
    }
    static List<int> ScanPipes(string text) {
        var pipes=new List<int>();
        int at=0;
        while(at<text.Length){
            char c=text[at];
            if(c=='\\'){at+=2;continue;}
            if(c=='`'){
                int run=1;
                while(at+run<text.Length&&text[at+run]=='`')run++;
                int close=FindBackticks(text,at+run,run);
                at=close>=0?close+run:at+run;
                continue;
            }
            if(c=='|')pipes.Add(at);
            at++;
        }
        return pipes;
    }
    static int FindBackticks(string text,int from,int length) {
        int at=from;
        while(at<text.Length){
            if(text[at]!='`'){at++;continue;}
            int run=1;
            while(at+run<text.Length&&text[at+run]=='`')run++;
            if(run==length)return at;
            at+=run;
        }
        return -1;
    }
    static List<string> Lines(string text) {
        var lines=new List<string>();
        if(text==null)return lines;
        int at=0;
        while(true){
            int start=at,end=at;
            while(end<text.Length&&text[end]!='\n'&&text[end]!='\r')end++;
            lines.Add(text.Substring(start,end-start));
            if(end>=text.Length)break;
            at=text[end]=='\r'&&end+1<text.Length&&text[end+1]=='\n'?end+2:end+1;
            if(at>text.Length)break;
        }
        return lines;
    }
    static int Count(string text,string value) {
        if(String.IsNullOrEmpty(text)||String.IsNullOrEmpty(value))return 0;
        int count=0,at=0;
        while(true){
            at=text.IndexOf(value,at,StringComparison.Ordinal);
            if(at<0)return count;
            count++;
            at+=value.Length;
        }
    }
    static int CountDelta(string before,string after,string value) {
        return Count(after,value)-Count(before,value);
    }

    // ---------------------------------------------------------------- report
    static string ComposeReport(StringBuilder runs) {
        var text=new StringBuilder();
        text.Append("# ImeAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o",CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- seeds: ").Append(firstSeed).Append("..").Append(lastSeed).Append(", ").Append(stepsPerSeed)
            .Append(" compositions per sequence, 2 modes\n");
        text.Append("- modes: basic (HideMarkers true) and live (markers shown); a view-switch ending flips the mode mid-sequence\n");
        text.Append("- fixtures: generated from the seed only; no user file is read\n");
        text.Append("- surface: offscreen MainWindow, live editor, the app's own command, key, tab and mouse-down paths\n");
        text.Append("- seams: SourceEditor.SimulateImeComposition / NoteImeComposition / ImeProc ENDCOMPOSITION and TextArea.PerformTextInput; the same synthetic IMM paths ImeProbe drives\n");
        text.Append("- not observable here: the native candidate window, the glyph IMM draws, the reading->candidate conversion\n");
        if(endingFilter!=null)text.Append("- ending filter: ").Append(endingFilter).Append('\n');
        text.Append("- runtime: ").Append(watch==null?0:watch.ElapsedMilliseconds).Append(" ms")
            .Append(budgetReached?" (budget reached; run incomplete)":"").Append('\n');
        if(dispatcherNotes.Count>0){
            text.Append("\n## Dispatcher exceptions\n");
            foreach(string note in dispatcherNotes)text.Append("- ").Append(note).Append('\n');
        }
        text.Append("\n## Operations\n");
        AppendCounts(text,opCounts);
        text.Append("\n## Invariant checks\n");
        AppendCounts(text,checkCounts);
        text.Append("\n## Invariant failures\n");
        AppendCounts(text,failCounts);
        text.Append("\n## Skipped steps\n");
        AppendCounts(text,skipCounts);
        for(int i=0;i<skipNotes.Count;i++)text.Append("- ").Append(skipNotes[i]).Append('\n');
        text.Append(runs.ToString());
        text.Append("\n## Failures\n");
        if(failures.Count==0){
            text.Append("- none\n");
        }else{
            int limit=Math.Min(failures.Count,80);
            for(int i=0;i<limit;i++){
                FailureInfo failure=failures[i];
                text.Append("\n### seed ").Append(failure.Seed).Append(' ').Append(failure.Mode)
                    .Append(", step ").Append(failure.Step).Append(", ").Append(failure.Action).Append('\n');
                text.Append("- invariant: ").Append(failure.Invariant).Append('\n');
                text.Append("- detail: ").Append(failure.Detail).Append('\n');
                text.Append("- before:\n").Append(failure.Before).Append('\n');
                text.Append("- after:\n").Append(failure.After).Append('\n');
                text.Append("- steps: ").Append(failure.Steps).Append('\n');
            }
            if(failures.Count>limit)text.Append("\n- ").Append(failures.Count-limit).Append(" more failures (see the counts above)\n");
        }
        text.Append("\nResult: ").Append(all&&!budgetReached?"PASS":"FAIL").Append('\n');
        text.Append("Runtime: ").Append(watch==null?0:watch.ElapsedMilliseconds).Append(" ms\n");
        return text.ToString();
    }
    static void AppendCounts(StringBuilder text,Dictionary<string,int> map) {
        if(map.Count==0){text.Append("- (none)\n");return;}
        var keys=new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        for(int i=0;i<keys.Count;i++)text.Append("- ").Append(keys[i]).Append(": ").Append(map[keys[i]]).Append('\n');
    }
    static string StepListText() {
        if(stepNames.Count==0)return "(none)";
        return String.Join(" | ",stepNames.ToArray());
    }
    static string Excerpt(string text,int offset) {
        if(text==null)return "(none)";
        List<string> lines=Lines(text);
        int at=Math.Max(0,Math.Min(offset,text.Length));
        int index=0,seen=0;
        for(int i=0;i<lines.Count;i++){
            if(seen<=at)index=i;
            seen+=lines[i].Length+1;
        }
        int first=Math.Max(0,index-2),last=Math.Min(lines.Count-1,index+2);
        var builder=new StringBuilder();
        for(int i=first;i<=last;i++)builder.Append(i+1).Append("| ").Append(OneLine(lines[i])).Append('\n');
        string value=builder.ToString();
        if(value.Length>1200)value=value.Substring(0,1200)+"...";
        return value;
    }
    static string Diff(string want,string got) {
        if(want==null)want="<null>";
        if(got==null)got="<null>";
        int at=0,limit=Math.Min(want.Length,got.Length);
        while(at<limit&&want[at]==got[at])at++;
        if(at==limit&&want.Length==got.Length)return "equal";
        return "first difference at "+at+", expected length="+want.Length+", actual length="+got.Length+
            ", expected around=["+Context(want,at)+"], actual around=["+Context(got,at)+"]";
    }
    static string Context(string value,int at) {
        if(value==null)return "<null>";
        int start=Math.Max(0,at-18),end=Math.Min(value.Length,at+18);
        return OneLine(value.Substring(start,end-start));
    }
    static string OneLine(string value) {
        if(value==null)return "<null>";
        string text=value.Replace("\r\n","\\n").Replace("\r","\\n").Replace("\n","\\n");
        return text.Length<=600?text:text.Substring(0,600)+"...";
    }
    static string Describe(Exception error) {
        Exception inner=error;
        while(inner is TargetInvocationException&&inner.InnerException!=null)inner=inner.InnerException;
        return OneLine(inner.GetType().Name+": "+inner.Message+" @ "+inner.StackTrace);
    }
    static void Bump(Dictionary<string,int> map,string key) {
        int value;
        if(!map.TryGetValue(key,out value))value=0;
        map[key]=value+1;
    }
    static void NoteDispatcher(DispatcherUnhandledExceptionEventArgs args) {
        dispatcherFailed=true;
        args.Handled=true;
        if(dispatcherNotes.Count>=20)return;
        string note=args.Exception==null?"unknown":args.Exception.GetType().Name+": "+OneLine(args.Exception.Message);
        if(args.Exception!=null&&args.Exception.StackTrace!=null){
            string[] frames=args.Exception.StackTrace.Replace("\r","").Split('\n');
            note+=" @ "+OneLine(String.Join(" < ",frames,0,Math.Min(4,frames.Length)));
        }
        dispatcherNotes.Add("seed "+currentSeed+" step "+currentStep+" "+currentAction+": "+note);
    }
    static string LastDispatcherNote() {
        return dispatcherNotes.Count==0?"unhandled dispatcher exception":dispatcherNotes[dispatcherNotes.Count-1];
    }

    // ---------------------------------------------------------------- reflection and pump
    static void Pump() {
        LongLineBench.Idle();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));
        LongLineBench.Idle();
    }
    static string DocText(SourceEditor editor) {
        if(editor==null||editor.Document==null)return "";
        return editor.Document.Text;
    }
    static IntPtr WindowHandle(SourceEditor editor) {
        var source=PresentationSource.FromVisual(editor.TextArea) as System.Windows.Interop.HwndSource;
        return source==null?IntPtr.Zero:source.Handle;
    }
    static FieldInfo RequireField(string name) {
        FieldInfo field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");
        return field;
    }
    static T Field<T>(MainWindow window,string name) {
        return (T)RequireField(name).GetValue(window);
    }
    static void SetField(MainWindow window,string name,object value) {
        RequireField(name).SetValue(window,value);
    }
    static T Prop<T>(MainWindow window,string name) {
        PropertyInfo property=typeof(MainWindow).GetProperty(name,Members);
        if(property==null)throw new MissingMemberException("probe: MainWindow has no property '"+name+"'");
        return (T)property.GetValue(window,null);
    }
    static object Invoke(MainWindow window,string name,params object[] args) {
        MethodInfo method=typeof(MainWindow).GetMethod(name,Members);
        if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");
        return method.Invoke(window,args);
    }
    static T Static<T>(string name) {
        return (T)RequireField(name).GetValue(null);
    }
    static void Static(string name,object value) {
        RequireField(name).SetValue(null,value);
    }
}}

