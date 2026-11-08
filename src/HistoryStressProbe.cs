using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
namespace MDNote {
// --history-stress-probe <dir>: one long seeded editing session (1000+ user actions) on the real
// window, then a history-cap case, the paste-then-typing case, the CR-only MD->TXT caret and the
// one-typing-run case (Korean/Japanese commits, Backspace, paste and focus boundaries). Every
// action is followed by a history oracle: the text at every
// undo point must equal the snapshot recorded when that point was created, redo must replay the
// later snapshots exactly, one command must be one undo step, the dirty flag must follow the last
// save, undo must keep the caret inside the document, and the undo stack must stay under its cap.
// The mix is typing (Latin and Korean), IME commits, Enter/Backspace/Delete runs, toolbar and table
// commands, paste, cut, undo/redo, basic/live and raw-view switches, TXT round trips, save+reload,
// a second tab, and find/replace-all. Fixtures and files are generated here; no user file is read.
// Report: <dir>\history-stress-probe.txt; -PASS.txt only when every invariant held.
internal static class HistoryStressProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int UndoCap=512;
    const long ByteCap=8L*1024*1024;
    const int DefaultSeed=1,DefaultLastSeed=2,DefaultSteps=1200;
    const long DefaultBudgetMilliseconds=280000L;
    const int KindNormal=0,KindUndo=1,KindRedo=2,KindTab=3,KindReload=4,KindSkipped=5;

    internal sealed class FailureInfo {
        internal string Invariant,Action,Detail,Expected,Actual,Steps;
        internal int Seed,Step,Count;
    }
    // One tab's exact timeline. States[i] is the text after undo entry i (States[0] is the oldest
    // state the stack can still reach); Modes[i] is the PlainText flag at that point; Position is
    // the number of entries currently applied (the undo stack's count). When the cap evicts the
    // oldest entry, the first state and mode go with it.
    internal sealed class StressModel {
        internal object Tab;
        internal readonly List<string> States=new List<string>();
        internal readonly List<bool> Modes=new List<bool>();
        internal int Position;
        internal bool Valid=true;
    }
    internal sealed class WindowState {
        internal string Text,Saved;
        internal bool Plain,Recovered,Dirty,SnapshotDirty;
        internal int UndoCount,RedoCount,Caret,SelectionStart,SelectionLength,DocumentLength;
        internal object Top;
        internal long Bytes;
    }
    internal sealed class StepResult {
        internal string Label="unknown";
        internal int Kind=KindNormal;
        internal WindowState Before,After;
        internal bool Saved,ImeWord;
        internal int PayloadLength;
        internal object OldTab;
        internal string Note,File;
        // The live preview keeps typed text out of a table's delimiter row (structure, not cell
        // text: MarkdownLivePreview.MultiCellEntering/TablePipeTyping). The step oracle accepts the
        // resulting no-change/no-entry type only when that documented rule applies.
        internal bool SwallowedDelimiterInput;
    }
    internal sealed class ReferenceComparer:IEqualityComparer<object> {
        internal static readonly ReferenceComparer Instance=new ReferenceComparer();
        public new bool Equals(object a,object b){return Object.ReferenceEquals(a,b);}
        public int GetHashCode(object value){return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);}
    }

    static string outRoot;
    static StringBuilder report;
    static bool all;
    static int checks,passed,failed;
    static readonly Dictionary<string,int> opCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,int> checkCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,int> failCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,FailureInfo> failures=new Dictionary<string,FailureInfo>();
    static readonly List<FailureInfo> failureList=new List<FailureInfo>();
    static int suppressedFailures;
    static readonly Dictionary<string,int> stepCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,int> replaceNotes=new Dictionary<string,int>();
    static int maxUndoEntries,maxRedoEntries,capHits;static long maxBytesSeen;
    static int currentSeed,currentStep;
    static string currentAction="start";
    static readonly List<string> stepNames=new List<string>();
    static readonly Dictionary<object,StressModel> models=new Dictionary<object,StressModel>(ReferenceComparer.Instance);
    static Stopwatch watch;
    static long budgetMilliseconds=DefaultBudgetMilliseconds;
    static bool budgetReached;
    static int firstSeed=DefaultSeed,lastSeed=DefaultLastSeed,stepsPerSeed=DefaultSteps;
    static int txtToggleIndex;
    static readonly int[] TxtToggleSteps=new int[]{230,270,720,780};
    // Per-step watchdog. The whole probe runs on the UI thread, so a product loop that never
    // yields cannot be interrupted from inside; a background thread watches the step clock and
    // the process's private bytes, writes the step, action, caret/line, memory and both a
    // managed stack of the UI thread and a CPU sample to history-stress-probe-HANG.txt (time)
    // or -MEM.txt (memory), and ends the process with a FAIL file and exit code 3 instead of
    // letting the caller's time cap hide where the run stopped.
    const int StepWatchdogMilliseconds=20000;
    const int UiStallMilliseconds=25000;
    // b56c6 froze the whole test VM for hours with no dump; a run that grows past this is not a
    // slow step any more. The fixture is a few KB, so a healthy process stays far below it.
    const long MemoryWatchdogBytes=1536L*1024L*1024L;
    const int DumpExitCode=3;
    static Thread uiThread,watchdogThread;
    static volatile bool watchdogStop,stepInFlight;
    static long stepStartTimestamp,uiBeatTimestamp;
    static volatile string watchdogPlace="start";
    static volatile string watchdogSnapshot="(none yet)";
    static volatile string watchdogDetail="";
    static MainWindow watchdogWindow;
    static int watchdogFired;
    // Frame/layout counters for the stall dump: an endless render loop shows up as a steady
    // hundreds-per-second rate of arrange passes and presented frames.
    static int layoutPasses,renderFrames;
    static EventHandler layoutCounter;
    static bool renderCounterHooked;
    // ScrollChanged stream while the stall runs: the trigger FollowDocumentEnd reads is in these
    // numbers (extent grew or viewport shrank), so the dump records the last event's values.
    static int scrollChanges;
    static double scrollExtent,scrollExtentChange,scrollViewport,scrollViewportChange,scrollOffset,scrollOffsetChange;
    static double scrollMarginTop,scrollMarginBottom;
    static ScrollChangedEventHandler scrollCounter;
    static readonly object scrollSync=new object();
    static readonly List<string> scrollLog=new List<string>();
    // UI-thread state sampler: a Normal-priority timer still runs inside the loop, so the dump
    // sees what the editor's scroll geometry and layout flags actually do between frames.
    static readonly object sampleSync=new object();
    static readonly List<string> stateSamples=new List<string>();
    static DispatcherTimer stateTimer;
    static int stateSequence;

    internal static int Run(string outputDirectory){
        outRoot=Path.GetFullPath(outputDirectory);Directory.CreateDirectory(outRoot);
        string passFile=Path.Combine(outRoot,"history-stress-probe-PASS.txt"),failFile=Path.Combine(outRoot,"history-stress-probe-FAIL.txt");
        string hangFile=Path.Combine(outRoot,"history-stress-probe-HANG.txt"),memoryFile=Path.Combine(outRoot,"history-stress-probe-MEM.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        if(File.Exists(hangFile))File.Delete(hangFile);if(File.Exists(memoryFile))File.Delete(memoryFile);
        ReadOptions();
        InitReflection();
        report=new StringBuilder("# history-stress-probe\n");
        report.Append("# Seed ").Append(firstSeed).Append("..").Append(lastSeed).Append(", ").Append(stepsPerSeed).Append(" actions per seed, budget ").Append(budgetMilliseconds).Append(" ms.\n");
        report.Append("# Every action is followed by: exact text at the current undo point, history entry diff, undo/redo of the");
        report.Append(" current point, dirty flag, caret bounds, entry count and byte cap. Walks at checkpoints and once at the end undo/redo the whole stack.\n");
        report.Append("# Dirty flag rule: HasUnsavedChanges is recovered || the edit latch || text != saved; an editing action that");
        report.Append(" reproduces the saved text may keep the latch (Notepad-like), every save/reload/tab/undo/redo answer is strict.\n");
        all=true;checks=passed=failed=0;budgetReached=false;
        watch=new Stopwatch();watch.Start();
        uiThread=Thread.CurrentThread;StartWatchdog();PostSpy.Install();
        bool oldSelf=Program.IsSelfTest;bool oldHide=false;bool haveHide=false;
        System.Windows.Threading.DispatcherUnhandledExceptionEventHandler dispatcherFault=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args){NoteDispatcher(args.Exception);};
        UnhandledExceptionEventHandler domainFault=delegate(object sender,UnhandledExceptionEventArgs args){NoteDispatcher(args.ExceptionObject as Exception);};
        try{
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException+=dispatcherFault;AppDomain.CurrentDomain.UnhandledException+=domainFault;
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            try{oldHide=Static<bool>("hideMarkers");haveHide=true;}catch(Exception){}
            for(int seed=firstSeed;seed<=lastSeed;seed++){
                if(watch.ElapsedMilliseconds>budgetMilliseconds){budgetReached=true;break;}
                RunSeed(seed);
            }
            if(!budgetReached){RunUndoRuleCase();RunImeCommitEdgeCase();RunHistoryCapCase();RunPasteUndoCase();RunTextModeCaretCase();}
        }catch(Exception ex){all=false;report.Append("\nFATAL: ").Append(OneLine(ex.ToString())).Append('\n');}
        finally{
            System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException-=dispatcherFault;AppDomain.CurrentDomain.UnhandledException-=domainFault;
            if(haveHide)try{SetStatic("hideMarkers",oldHide);}catch(Exception){}
            Program.IsSelfTest=oldSelf;watch.Stop();watchdogStop=true;
        }
        AppendTotals();
        string text=report.ToString();
        try{
            File.WriteAllText(Path.Combine(outRoot,"history-stress-probe.txt"),text,new UTF8Encoding(false));
            if(all&&!budgetReached)File.WriteAllText(passFile,"PASS\n",new UTF8Encoding(false));
            else File.WriteAllText(failFile,text,new UTF8Encoding(false));
        }catch(IOException){return 2;}
        return all&&!budgetReached?0:1;
    }

    // A reduced run: MDNOTE_HISTORY_STRESS_SEED / _LASTSEED / _STEPS / _BUDGET override the default.
    static void ReadOptions(){
        firstSeed=DefaultSeed;lastSeed=DefaultLastSeed;stepsPerSeed=DefaultSteps;budgetMilliseconds=DefaultBudgetMilliseconds;
        string value=Environment.GetEnvironmentVariable("MDNOTE_HISTORY_STRESS_SEED");int number;
        if(!String.IsNullOrEmpty(value)&&Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)&&number>0){firstSeed=number;lastSeed=number;}
        value=Environment.GetEnvironmentVariable("MDNOTE_HISTORY_STRESS_LASTSEED");
        if(!String.IsNullOrEmpty(value)&&Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)&&number>=firstSeed)lastSeed=number;
        value=Environment.GetEnvironmentVariable("MDNOTE_HISTORY_STRESS_STEPS");
        if(!String.IsNullOrEmpty(value)&&Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)&&number>0)stepsPerSeed=number;
        value=Environment.GetEnvironmentVariable("MDNOTE_HISTORY_STRESS_BUDGET");
        if(!String.IsNullOrEmpty(value)&&Int64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out budgetMilliseconds)&&budgetMilliseconds<=0)budgetMilliseconds=DefaultBudgetMilliseconds;
    }

    static void RunSeed(int seed){
        currentSeed=seed;currentStep=0;currentAction="open";stepNames.Clear();
        models.Clear();txtToggleIndex=0;ResetStats();
        long started=watch.ElapsedMilliseconds;
        MainWindow window=null;
        var random=new Random(seed*7919+17);
        bool seedOk=true;
        try{
            window=CreateWindow();watchdogWindow=window;HookCounters(window);
            string fixture=Fixture();
            window.SetText(fixture);SetField(window,"saved",fixture);SetField(window,"recoveredDocument",false);
            Pump();window.SetMode(true);Invoke(window,"SetLiveMode",true);Pump();
            Check("open.surface",Editing(window)&&Bool(window,"CodeMode")&&Bool(window,"LivePreviewAttached")&&!Bool(window,"PlainText"),"editing="+Editing(window)+", code="+Bool(window,"CodeMode")+", attached="+Bool(window,"LivePreviewAttached")+", plain="+Bool(window,"PlainText"));
            StressModel model=ModelFor(window);
            if(model==null||!model.Valid)seedOk=false;
            for(int step=1;step<=stepsPerSeed;step++){
                currentStep=step;
                if(watch.ElapsedMilliseconds>budgetMilliseconds){budgetReached=true;seedOk=false;break;}
                BeginStep(window);
                try{
                StepResult result=null;
                try{
                    result=ExecuteAction(window,random,step);
                    if(result==null)result=new StepResult {Kind=KindSkipped,Label="skip"};
                    currentAction=result.Label;
                    if(result.Kind==KindSkipped){stepNames.Add(result.Label);StepCount(result.Label);continue;}
                    if(result.Before==null)result.Before=Capture(window);
                    if(result.After==null)result.After=Capture(window);
                    if(result.Kind==KindTab)model=AfterTab(window,result);
                    else if(result.Kind==KindReload)model=AfterReload(window,result);
                    else if(result.Kind==KindUndo)AfterUndo(window,model,result);
                    else if(result.Kind==KindRedo)AfterRedo(window,model,result);
                    else AfterNormal(window,model,result);
                }catch(Exception ex){
                    all=false;seedOk=false;
                    Fail("action.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(action completes)",OneLine(ex.ToString()));
                    Invalidate(model);
                    TryRebuild(window,model);
                }
                stepNames.Add(result==null?"unknown":result.Label);
                if(result!=null)StepCount(result.Label);
                model=ModelFor(window);
                if(model!=null&&model.Valid){
                    if((step%25)==0)WalkAround(window,model,12);
                    if((step%125)==0)WalkAround(window,model,40);
                    if(step==620)FullWalk(window,model);
                }
                if((step%50)==0)WriteProgress();
                }finally{EndStep();}
            }
            if(!budgetReached){
                model=ModelFor(window);
                if(model!=null&&model.Valid)FullWalk(window,model);
                Check("seed.completed",currentStep>=stepsPerSeed,"steps="+(currentStep-1)+", expected="+stepsPerSeed);
            }
        }catch(Exception ex){
            all=false;seedOk=false;
            Fail("session.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(session completes)",OneLine(ex.ToString()));
        }finally{
            watchdogWindow=null;CloseWindow(window);
        }
        report.Append("\n## seed ").Append(seed).Append('\n');
        report.Append("- steps: ").Append(stepNames.Count).Append(" of ").Append(stepsPerSeed).Append('\n');
        report.Append("- run time: ").Append(watch.ElapsedMilliseconds-started).Append(" ms\n");
        report.Append("- tabs left open: ").Append(window==null?"-":window.OpenDocumentCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
        report.Append("- peak undo entries: ").Append(maxUndoEntries).Append(" (cap ").Append(UndoCap).Append(")\n");
        report.Append("- peak redo entries: ").Append(maxRedoEntries).Append('\n');
        report.Append("- peak history bytes: ").Append(maxBytesSeen).Append(" (cap ").Append(ByteCap).Append(")\n");
        report.Append("- steps with a full 512-entry stack: ").Append(capHits).Append('\n');
        report.Append("- ok: ").Append(seedOk&&!budgetReached?"yes":"no").Append('\n');
        if(budgetReached)report.Append("- note: budget reached; the run is incomplete\n");
    }

    // ---------------------------------------------------------------- counts and failures
    static void StepCount(string label){
        string key=label;
        int colon=key.IndexOf(':');
        if(colon>0)key=key.Substring(0,colon);
        Bump(opCounts,key);
    }
    static void Check(string name,bool ok,string detail){
        checks++;checkCounts[name]=CountOf(checkCounts,name)+1;
        if(ok)passed++;else{failed++;all=false;Fail(name,detail,"invariant holds",detail);}
    }
    static void CheckDiff(string name,string want,string got,string detail){
        checks++;checkCounts[name]=CountOf(checkCounts,name)+1;
        if(String.Equals(want,got,StringComparison.Ordinal)){passed++;return;}
        failed++;all=false;
        Fail(name,(detail==null?"":detail+"; ")+Diff(want,got),Excerpt(want),Excerpt(got));
    }
    static void Fail(string invariant,string detail,string expected,string actual){
        failCounts[invariant]=CountOf(failCounts,invariant)+1;
        string detailLine=OneLine(detail);string expectedLine=OneLine(expected);string actualLine=OneLine(actual);
        string key=invariant+"|"+Signature(detailLine)+"|"+Signature(expectedLine)+"|"+Signature(actualLine);
        FailureInfo existing;
        if(failures.TryGetValue(key,out existing)){existing.Count++;return;}
        if(failureList.Count>=80){suppressedFailures++;return;}
        var failure=new FailureInfo();
        failure.Invariant=invariant;failure.Detail=Truncate(detailLine,900);
        failure.Expected=Truncate(expectedLine,700);failure.Actual=Truncate(actualLine,700);
        failure.Seed=currentSeed;failure.Step=currentStep;failure.Action=currentAction;
        failure.Steps=LastSteps(40);failure.Count=1;
        failures[key]=failure;failureList.Add(failure);
    }
    static void NoteDispatcher(Exception error){
        all=false;
        Fail("dispatcher.exception",error==null?"<null>":OneLine(error.GetType().Name+": "+error.Message),"(no exception)","(dispatcher exception)");
    }
    static int CountOf(Dictionary<string,int> map,string key){int value;return map.TryGetValue(key,out value)?value:0;}
    static void Bump(Dictionary<string,int> map,string key){map[key]=CountOf(map,key)+1;}
    static string Signature(string value){
        if(value==null)return "<null>";
        string text=value.Replace("\r\n","\\n").Replace("\r","\\n").Replace("\n","\\n");
        if(text.Length>160)text=text.Substring(0,160);
        return text;
    }
    static void WriteProgress(){
        try{
            var text=new StringBuilder();
            text.Append("seed=").Append(currentSeed).Append(" step=").Append(currentStep).Append(" action=").Append(currentAction).Append('\n');
            text.Append("checks=").Append(checks).Append(" pass=").Append(passed).Append(" fail=").Append(failed).Append('\n');
            text.Append("runtime=").Append(watch==null?0:watch.ElapsedMilliseconds).Append(" ms\n");
            var keys=new List<string>(failCounts.Keys);keys.Sort(StringComparer.Ordinal);
            foreach(string key in keys)text.Append("fail ").Append(key).Append(": ").Append(failCounts[key]).Append('\n');
            File.WriteAllText(Path.Combine(outRoot,"history-stress-probe-progress.txt"),text.ToString(),new UTF8Encoding(false));
        }catch(Exception){}
    }
    static string LastSteps(int count){
        if(stepNames.Count==0)return "(none)";
        int first=Math.Max(0,stepNames.Count-count);
        var text=new StringBuilder();
        for(int i=first;i<stepNames.Count;i++){if(text.Length>0)text.Append(" | ");text.Append(i+1).Append('.').Append(stepNames[i]);}
        return text.ToString();
    }
    static void AppendTotals(){
        report.Append("\n## Operations\n");
        AppendCounts(report,opCounts);
        report.Append("\n## Invariant checks\n");
        AppendCounts(report,checkCounts);
        report.Append("\n## Undo steps per action\n");
        AppendCounts(report,stepCounts);
        report.Append("\n## Replace-all outcomes\n");
        AppendCounts(report,replaceNotes);
        report.Append("\n## Dispatcher post spy\n");
        try{report.Append(PostSpy.Report(6));}catch(Exception ex){report.Append("- report failed: ").Append(OneLine(ex.GetType().Name+": "+ex.Message)).Append('\n');}
        report.Append("\n## Invariant failures\n");
        AppendCounts(report,failCounts);
        report.Append("\n## Failures\n");
        if(failureList.Count==0){report.Append("- none\n");}
        else{
            foreach(FailureInfo failure in failureList){
                report.Append("\n### seed ").Append(failure.Seed).Append(", step ").Append(failure.Step).Append(' ').Append(failure.Action).Append('\n');
                report.Append("- invariant: ").Append(failure.Invariant).Append('\n');
                report.Append("- detail: ").Append(failure.Detail).Append('\n');
                report.Append("- expected: ").Append(failure.Expected).Append('\n');
                report.Append("- actual: ").Append(failure.Actual).Append('\n');
                report.Append("- steps: ").Append(failure.Steps).Append('\n');
                if(failure.Count>1)report.Append("- repeats: ").Append(failure.Count).Append('\n');
            }
        }
        if(suppressedFailures>0)report.Append("\n- further distinct failures suppressed: ").Append(suppressedFailures).Append('\n');
        report.Append("\nChecks: ").Append(checks).Append("  PASS: ").Append(passed).Append("  FAIL: ").Append(failed).Append('\n');
        report.Append("Result: ").Append(all&&!budgetReached?"PASS":"FAIL").Append('\n');
        report.Append("Runtime: ").Append(watch==null?0:watch.ElapsedMilliseconds).Append(" ms\n");
    }
    static void AppendCounts(StringBuilder text,Dictionary<string,int> map){
        if(map.Count==0){text.Append("- (none)\n");return;}
        var keys=new List<string>(map.Keys);keys.Sort(StringComparer.Ordinal);
        foreach(string key in keys)text.Append("- ").Append(key).Append(": ").Append(map[key]).Append('\n');
    }
    static string OneLine(string value){
        if(value==null)return "<null>";
        string text=value.Replace("\r\n","\\n").Replace("\r","\\n").Replace("\n","\\n");
        return Truncate(text,1200);
    }
    static string Truncate(string text,int limit){return text.Length<=limit?text:text.Substring(0,limit)+"...";}
    // A short expected/actual excerpt around the first difference.
    static string Excerpt(string value){
        if(value==null)return "<null>";
        string text=value.Replace("\r\n","\\n").Replace("\r","\\n").Replace("\n","\\n");
        if(text.Length<=260)return text;
        return text.Substring(0,260)+"...";
    }
    static string Diff(string want,string got){
        if(want==null)want="<null>";if(got==null)got="<null>";
        int at=0,limit=Math.Min(want.Length,got.Length);
        while(at<limit&&want[at]==got[at])at++;
        return "firstDiff="+at+", want=["+Context(want,at)+"], got=["+Context(got,at)+"]";
    }
    static string Context(string value,int at){
        int start=Math.Max(0,at-40),end=Math.Min(value.Length,at+40);
        return OneLine(value.Substring(start,end-start));
    }
    // ---------------------------------------------------------------- window and model access
    static MainWindow CreateWindow(){
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
        window.Show();window.Left=-20000;window.UpdateLayout();
        return window;
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{
            SetField(window,"documentOperationBusy",false);SetField(window,"imeComposing",false);
            Invoke(window,"FlushDocumentChanges");
            string value=Text(window);SetField(window,"saved",value);SetField(window,"recoveredDocument",false);
            IList documents=(IList)FieldValue(window,"documents");
            if(documents!=null)foreach(object tab in documents){SetMember(tab,"Saved",GetMember(tab,"Text"));SetMember(tab,"Recovered",false);}
            window.Close();
        }catch(Exception){}
    }
    static string Text(MainWindow window){return Prop<string>(window,"text");}
    static bool Plain(MainWindow window){return Prop<bool>(window,"PlainText");}
    static bool Editing(MainWindow window){return Field<bool>(window,"editing");}
    static bool Recovered(MainWindow window){return Field<bool>(window,"recoveredDocument");}
    static string SavedText(MainWindow window){return Field<string>(window,"saved");}
    static string DocumentNewline(MainWindow window){return Field<string>(window,"documentNewline");}
    static SourceEditor Source(MainWindow window){return Field<SourceEditor>(window,"source");}
    static DocumentHistory HistoryOf(MainWindow window){return Prop<DocumentHistory>(window,"History");}
    static bool Bool(MainWindow window,string name){return Prop<bool>(window,name);}
    static IList UndoList(DocumentHistory history){return (IList)undoField.GetValue(history);}
    static IList RedoList(DocumentHistory history){return (IList)redoField.GetValue(history);}
    static long HistoryBytes(DocumentHistory history){return (long)bytesField.GetValue(history);}

    static WindowState Capture(MainWindow window){
        Interlocked.Exchange(ref uiBeatTimestamp,Stopwatch.GetTimestamp());
        var state=new WindowState();
        // Read the dirty flag before the text getter: the getter clears sourceSnapshotDirty, and
        // the user sees the flag as it stood at the end of the action.
        state.Dirty=Bool(window,"HasUnsavedChanges");
        state.SnapshotDirty=Field<bool>(window,"sourceSnapshotDirty");
        state.Text=Text(window);state.Plain=Plain(window);state.Saved=SavedText(window);
        state.Recovered=Recovered(window);
        DocumentHistory history=HistoryOf(window);
        IList undo=UndoList(history),redo=RedoList(history);
        state.UndoCount=undo.Count;state.RedoCount=redo.Count;
        state.Top=undo.Count>0?undo[undo.Count-1]:null;state.Bytes=HistoryBytes(history);
        SourceEditor source=Source(window);
        if(source!=null){
            state.Caret=source.CaretIndex;state.SelectionStart=source.SelectionStart;state.SelectionLength=source.SelectionLength;
            state.DocumentLength=source.Document==null?-1:source.Document.TextLength;
        }
        return state;
    }
    static StressModel ModelFor(MainWindow window){
        object tab=FieldValue(window,"activeDocument");if(tab==null)return null;
        StressModel model;
        if(models.TryGetValue(tab,out model))return model;
        model=new StressModel {Tab=tab};models[tab]=model;
        WindowState state=Capture(window);
        model.States.Add(state.Text);model.Modes.Add(state.Plain);model.Position=state.UndoCount;
        if(state.UndoCount!=0||state.RedoCount!=0){model.Valid=false;TryRebuild(window,model);}
        return model;
    }
    static void Invalidate(StressModel model){if(model!=null)model.Valid=false;}
    static bool ModelCurrent(StressModel model,WindowState state){
        if(model==null||!model.Valid)return false;
        if(model.States.Count!=state.UndoCount+state.RedoCount+1)return false;
        if(model.Position!=state.UndoCount)return false;
        if(model.Modes.Count!=model.States.Count)return false;
        if(!String.Equals(model.States[model.Position],state.Text,StringComparison.Ordinal))return false;
        if(model.Modes[model.Position]!=state.Plain)return false;
        return true;
    }
    static string ModelSummary(StressModel model){
        if(model==null)return "<null>";
        int length=model.States.Count==0?-1:model.States[model.Position].Length;
        return "valid="+model.Valid+", pos="+model.Position+", states="+model.States.Count+", textLen="+length;
    }
    static string StateSummary(WindowState state){
        return "undo="+state.UndoCount+", redo="+state.RedoCount+", len="+state.Text.Length+", top="+(state.Top==null?"none":"entry");
    }

    // ---------------------------------------------------------------- after-action oracle
    static void ResetStats(){maxUndoEntries=0;maxRedoEntries=0;maxBytesSeen=0;capHits=0;}
    static void NoteStats(WindowState state){
        if(state.UndoCount>maxUndoEntries)maxUndoEntries=state.UndoCount;
        if(state.RedoCount>maxRedoEntries)maxRedoEntries=state.RedoCount;
        if(state.Bytes>maxBytesSeen)maxBytesSeen=state.Bytes;
        if(state.UndoCount==UndoCap)capHits++;
    }
    static void Common(string stage,MainWindow window,WindowState state,bool editLatchAllowed){
        NoteStats(state);
        Check("history.bounds.count",state.UndoCount<=UndoCap&&state.RedoCount<=UndoCap&&state.UndoCount+state.RedoCount<=UndoCap,
            "undo="+state.UndoCount+", redo="+state.RedoCount+", cap="+UndoCap);
        Check("history.bounds.bytes",state.Bytes>=0&&state.Bytes<=ByteCap,"bytes="+state.Bytes+", cap="+ByteCap);
        Check("caret.bounds",state.Caret>=0&&state.Caret<=state.DocumentLength&&state.SelectionStart>=0&&state.SelectionLength>=0&&(long)state.SelectionStart+state.SelectionLength<=state.DocumentLength,
            "caret="+state.Caret+", sel="+state.SelectionStart+"+"+state.SelectionLength+", document="+state.DocumentLength);
        /* HasUnsavedChanges (EditPipeline.cs) is the recovered flag || the edit latch ||
           text != saved. The latch is set by every ContentDelta and only save, load and an undo back
           to the saved text clear it, so an edit that happens to reproduce the saved text keeps the
           mark until then, exactly like Notepad's *. An editing action may therefore answer either
           way when the text already equals the saved one; the mark itself must be the latch, never a
           stale flag on its own. Every non-editing action (save, reload, tab, undo, redo, mode) must
           still answer strictly, which is what catches a save that failed to clear the mark. */
        bool sameAsSaved=String.Equals(state.Text,state.Saved,StringComparison.Ordinal);
        bool expectedDirty=state.Recovered||!sameAsSaved;
        string dirtyDetail="sourceSnapshotDirty="+state.SnapshotDirty+", textLen="+state.Text.Length+", savedLen="+(state.Saved==null?-1:state.Saved.Length);
        if(expectedDirty||!editLatchAllowed)CheckDiff("dirty.flag",expectedDirty?"dirty":"clean",state.Dirty?"dirty":"clean",dirtyDetail);
        else Check("dirty.flag",!state.Dirty||state.SnapshotDirty,dirtyDetail+", edit latch");
        Check("text.mirror",state.DocumentLength==state.Text.Length,"document="+state.DocumentLength+", text="+state.Text.Length);
        Check("recovery.clear",!state.Recovered,"recovered="+state.Recovered);
    }
    static void AfterNormal(MainWindow window,StressModel model,StepResult result){
        watchdogPlace="oracle:normal";
        WindowState before=result.Before,after=result.After;
        Common("normal",window,after,EditsText(ActionKind(result.Label)));
        if(model==null)model=ModelFor(window);
        if(model==null)return;
        if(model.Valid&&!ModelCurrent(model,before)){
            Fail("history.drift","the model does not describe the stack the action started from",ModelSummary(model),StateSummary(before));
            Invalidate(model);TryRebuild(window,model);return;
        }
        if(!model.Valid){TryRebuild(window,model);return;}
        string kind=ActionKind(result.Label);
        bool topChanged=!Object.ReferenceEquals(before.Top,after.Top);
        bool textChanged=!String.Equals(before.Text,after.Text,StringComparison.Ordinal);
        int observed=-1;
        Check("history.before.mirror",before.UndoCount==model.Position&&model.States.Count==before.UndoCount+before.RedoCount+1,"pos="+model.Position+", undo="+before.UndoCount+", redo="+before.RedoCount+", states="+model.States.Count);
        if(topChanged){
            // A typed key can also take the top entry away instead of adding one: a typed
            // replacement that restores the run's own original text leaves an entry with no net
            // change, and DocumentHistory drops it. The stack loses one entry, the text stays where
            // it was, and the model's last state (the canceled entry's text) must be removed with it.
            bool canceled=!textChanged&&before.RedoCount==0&&after.RedoCount==0&&after.UndoCount==before.UndoCount-1;
            if(canceled){
                if(model.Position<1||model.Position>=model.States.Count){
                    Fail("history.cancel.model","a canceled entry has no model state to remove",ModelSummary(model),StateSummary(after));
                    Invalidate(model);TryRebuild(window,model);return;
                }
                CheckDiff("history.cancel.text",model.States[model.Position-1],after.Text,"the canceled entry did not return to the text before it");
                model.States.RemoveAt(model.Position);model.Modes.RemoveAt(model.Position);
                model.Position=after.UndoCount;
                observed=0;
            }else{
                int expectedTotal=before.UndoCount+1,actualTotal=after.UndoCount+after.RedoCount,dropped=expectedTotal-actualTotal;
                if(dropped<0||dropped>1){
                    Fail("history.steps",kind+" changed the stack by an impossible number of entries","1 new entry (oldest may be evicted once)","undo "+before.UndoCount+"->"+after.UndoCount+", redo "+before.RedoCount+"->"+after.RedoCount);
                    Invalidate(model);TryRebuild(window,model);return;
                }
                string forward=null,error=null;
                try{forward=ForwardEntry(after.Top,before.Text);}catch(Exception ex){error=OneLine(ex.GetType().Name+": "+ex.Message);}
                CheckDiff("history.entry.diff",after.Text,forward,"the new entry does not rebuild the current text"+(error==null?"":" ("+error+")"));
                if(model.States.Count>model.Position+1){
                    int remove=model.States.Count-(model.Position+1);
                    model.States.RemoveRange(model.Position+1,remove);model.Modes.RemoveRange(model.Position+1,remove);
                }
                if(dropped==1){
                    model.States.RemoveAt(0);model.Modes.RemoveAt(0);
                    Check("history.cap.eviction",model.States.Count-1==actualTotal,"modelEntries="+(model.States.Count-1)+", stackEntries="+actualTotal);
                }
                bool expectedMode=ExpectedEntryMode(after.Top,before.Plain);
                Check("mode.entry",after.Plain==expectedMode,"plain="+after.Plain+", expected="+expectedMode);
                model.States.Add(after.Text);model.Modes.Add(after.Plain);model.Position=after.UndoCount;
                observed=1;
            }
        }else{
            if(after.UndoCount!=before.UndoCount||after.RedoCount!=before.RedoCount){
                Fail("history.stack.change",kind+" changed the stack without a new entry","undo/redo counts unchanged","undo "+before.UndoCount+"->"+after.UndoCount+", redo "+before.RedoCount+"->"+after.RedoCount);
                Invalidate(model);TryRebuild(window,model);return;
            }
            if(textChanged){
                if(model.Position<1||model.Position>=model.States.Count){
                    Fail("history.merge.model","text changed with no entry to merge into",ModelSummary(model),StateSummary(after));
                    Invalidate(model);TryRebuild(window,model);return;
                }
                string prior=model.States[model.Position-1],forward=null,error=null;
                try{forward=ForwardEntry(after.Top,prior);}catch(Exception ex){error=OneLine(ex.GetType().Name+": "+ex.Message);}
                CheckDiff("history.merge.diff",after.Text,forward,"the grown entry does not rebuild the current text"+(error==null?"":" ("+error+")"));
                model.States[model.Position]=after.Text;
            }
            observed=0;
        }
        Bump(stepCounts,kind+"="+observed);
        Check("history.stepcount",StepCountAllowed(kind,observed,textChanged,result.PayloadLength,result.ImeWord,result.SwallowedDelimiterInput),
            "kind="+kind+", steps="+observed+", textChanged="+textChanged+", payload="+result.PayloadLength+(result.SwallowedDelimiterInput?", delimiter-row input":""));
        if(kind=="mode"||kind=="raw"||kind=="save")Check("switch.text",!textChanged,"a view/save action changed the text: "+Diff(before.Text,after.Text));
        Check("mode.state",after.Plain==model.Modes[model.Position],"plain="+after.Plain+", model="+model.Modes[model.Position]);
        Check("model.stack",model.States.Count==after.UndoCount+after.RedoCount+1&&model.Modes.Count==model.States.Count&&model.Position==after.UndoCount,
            "states="+model.States.Count+", undo="+after.UndoCount+", redo="+after.RedoCount+", pos="+model.Position);
        if(kind=="save")Check("save.point",String.Equals(after.Saved,after.Text,StringComparison.Ordinal),"savedLen="+(after.Saved==null?-1:after.Saved.Length)+", textLen="+after.Text.Length);
    }
    static void AfterUndo(MainWindow window,StressModel model,StepResult result){
        watchdogPlace="oracle:undo";
        WindowState before=result.Before,after=result.After;
        Common("undo",window,after,false);
        if(model==null)model=ModelFor(window);
        if(model==null)return;
        if(model.Valid&&!ModelCurrent(model,before)){
            Fail("history.drift","the model does not describe the stack the undo started from",ModelSummary(model),StateSummary(before));
            Invalidate(model);TryRebuild(window,model);return;
        }
        if(!model.Valid){model.Position=after.UndoCount;TryRebuild(window,model);return;}
        if(after.UndoCount==before.UndoCount){
            if(before.UndoCount>0)Fail("undo.blocked","undo was available but changed nothing","undo count -1","undo="+after.UndoCount+", canUndo="+HistoryOf(window).CanUndo);
            return;
        }
        if(after.UndoCount!=before.UndoCount-1||after.RedoCount!=before.RedoCount+1){
            Fail("undo.stack","undo did not move one entry from the undo stack to redo","undo -1, redo +1","undo "+before.UndoCount+"->"+after.UndoCount+", redo "+before.RedoCount+"->"+after.RedoCount);
            Invalidate(model);TryRebuild(window,model);return;
        }
        int index=before.UndoCount-1;
        if(index<0||index>=model.States.Count){Fail("undo.model","undo point outside the model",ModelSummary(model),StateSummary(before));Invalidate(model);TryRebuild(window,model);return;}
        CheckDiff("undo.snapshot",model.States[index],after.Text,"undo entry "+(index+1)+" of "+before.UndoCount);
        Check("undo.mode",after.Plain==model.Modes[index],"plain="+after.Plain+", expected="+model.Modes[index]);
        model.Position=after.UndoCount;
        Check("model.stack",model.States.Count==after.UndoCount+after.RedoCount+1&&model.Position==after.UndoCount,"states="+model.States.Count+", undo="+after.UndoCount+", redo="+after.RedoCount+", pos="+model.Position);
    }
    static void AfterRedo(MainWindow window,StressModel model,StepResult result){
        watchdogPlace="oracle:redo";
        WindowState before=result.Before,after=result.After;
        Common("redo",window,after,false);
        if(model==null)model=ModelFor(window);
        if(model==null)return;
        if(model.Valid&&!ModelCurrent(model,before)){
            Fail("history.drift","the model does not describe the stack the redo started from",ModelSummary(model),StateSummary(before));
            Invalidate(model);TryRebuild(window,model);return;
        }
        if(!model.Valid){model.Position=after.UndoCount;TryRebuild(window,model);return;}
        if(after.UndoCount==before.UndoCount){
            if(before.RedoCount>0)Fail("redo.blocked","redo was available but changed nothing","undo count +1","undo="+after.UndoCount+", canRedo="+HistoryOf(window).CanRedo);
            return;
        }
        if(after.UndoCount!=before.UndoCount+1||after.RedoCount!=before.RedoCount-1){
            Fail("redo.stack","redo did not move one entry from redo to undo","undo +1, redo -1","undo "+before.UndoCount+"->"+after.UndoCount+", redo "+before.RedoCount+"->"+after.RedoCount);
            Invalidate(model);TryRebuild(window,model);return;
        }
        int index=after.UndoCount;
        if(index<0||index>=model.States.Count){Fail("redo.model","redo point outside the model",ModelSummary(model),StateSummary(after));Invalidate(model);TryRebuild(window,model);return;}
        CheckDiff("redo.snapshot",model.States[index],after.Text,"redo entry "+index+" of "+(model.States.Count-1));
        Check("redo.mode",after.Plain==model.Modes[index],"plain="+after.Plain+", expected="+model.Modes[index]);
        model.Position=after.UndoCount;
        Check("model.stack",model.States.Count==after.UndoCount+after.RedoCount+1&&model.Position==after.UndoCount,"states="+model.States.Count+", undo="+after.UndoCount+", redo="+after.RedoCount+", pos="+model.Position);
    }
    static StressModel AfterTab(MainWindow window,StepResult result){
        WindowState before=result.Before,after=result.After;
        object oldTab=result.OldTab;
        if(oldTab!=null){
            StressModel oldModel;
            if(models.TryGetValue(oldTab,out oldModel)&&oldModel.Valid){
                Check("tab.capture.text",String.Equals((string)GetMember(oldTab,"Text"),before.Text,StringComparison.Ordinal),"the tab lost the text it had when it was left");
                Check("tab.capture.saved",String.Equals((string)GetMember(oldTab,"Saved"),before.Saved,StringComparison.Ordinal),"the tab lost its save point");
            }
        }
        Common("tab",window,after,false);
        SourceEditor source=Source(window);
        if(source!=null&&source.Document!=null)CheckDiff("tab.mirror",after.Text,source.Document.Text,"the switched tab's source buffer differs from its text");
        StressModel model=ModelFor(window);
        if(model!=null&&model.Valid&&!ModelCurrent(model,after)){
            Fail("tab.state","the tab's history does not match its text","model describes the active tab",ModelSummary(model)+"; "+StateSummary(after));
            Invalidate(model);TryRebuild(window,model);
        }
        Check("tab.editing",Editing(window),"editing="+Editing(window));
        if(model!=null&&model.Valid)Check("model.stack",model.States.Count==after.UndoCount+after.RedoCount+1&&model.Position==after.UndoCount,"states="+model.States.Count+", undo="+after.UndoCount+", redo="+after.RedoCount+", pos="+model.Position);
        return model;
    }
    static StressModel AfterReload(MainWindow window,StepResult result){
        WindowState after=result.After;
        Common("reload",window,after,false);
        CheckDiff("reload.text",result.Note==null?"":result.Note,after.Text,"save+reload must land on the saved buffer");
        Check("reload.saved",String.Equals(after.Saved,after.Text,StringComparison.Ordinal),"saved and text differ after reload");
        DocumentHistory history=HistoryOf(window);
        Check("reload.history",!history.CanUndo&&!history.CanRedo,"reload must clear history explicitly; canUndo="+history.CanUndo+", canRedo="+history.CanRedo);
        StressModel model=ModelFor(window);
        if(model!=null){
            model.States.Clear();model.Modes.Clear();model.States.Add(after.Text);model.Modes.Add(after.Plain);
            model.Position=after.UndoCount;model.Valid=after.UndoCount==0&&after.RedoCount==0;
        }
        return model;
    }
    static string ActionKind(string label){
        if(label==null)return "";
        int colon=label.IndexOf(':');return colon<0?label:label.Substring(0,colon);
    }
    // The actions that write the document. The dirty check lets these keep the edit latch when the
    // text already equals the saved one (see Common); a save, reload, tab switch, undo or redo must
    // still answer the strict content test.
    static bool EditsText(string kind){
        return kind=="type"||kind=="ime"||kind=="back"||kind=="delete"||kind=="enter"||kind=="cmd"||
            kind=="table"||kind=="paste"||kind=="cut"||kind=="replace";
    }
    // One user action's expected undo entries. Typing and per-key edits may merge into the run they
    // continue (0) or start their own entry (1); commands, paste, cut, an Enter and replace-all are
    // one entry whenever they change text and none when they do not; mode-only switches and saves
    // add none; a TXT conversion is one mode-aware entry even when the text comes out identical.
    // A type on a table delimiter row is kept out by the live preview (MarkdownLivePreview.
    // MultiCellEntering) and therefore writes no entry and no text.
    static bool StepCountAllowed(string kind,int steps,bool textChanged,int payloadLength,bool wordIme,bool delimiterInput){
        if(steps<0)return false;
        // A delimiter-row type is kept out by the preview, so it changes no text and adds no entry.
        if(kind=="type"&&delimiterInput)return steps==0&&!textChanged;
        // Continuous typing is one undo step, so a type or IME commit may either start an entry or
        // merge into the run it continues (the word and clause rules are gone).
        if(kind=="type")return steps==0||steps==1;
        if(kind=="ime")return steps==0||steps==1;
        if(kind=="back"||kind=="delete")return steps==0||steps==1;
        if(kind=="enter"||kind=="cmd"||kind=="table"||kind=="paste"||kind=="cut"||kind=="replace")return textChanged?steps==1:steps==0;
        if(kind=="txt")return steps==1;
        if(kind=="mode"||kind=="raw"||kind=="save")return steps==0;
        return steps==0||steps==1;
    }
    static bool ExpectedEntryMode(object edit,bool beforeMode){
        object afterMode=editAfterModeField.GetValue(edit);return afterMode==null?beforeMode:(bool)afterMode;
    }
    static string ForwardEntry(object edit,string prior){
        if(edit==null||prior==null)return null;
        IList parts=(IList)editPartsField.GetValue(edit);
        if(parts!=null){
            // Forward, the parts are read against the pre-edit text: their offsets are original and
            // the builder slices that same text, exactly like DocumentHistory.ApplyParts(forward).
            int cursor=0;var result=new StringBuilder(prior.Length);
            foreach(object part in parts){
                int at=(int)editAtField.GetValue(part);
                string removed=(string)editRemovedField.GetValue(part),inserted=(string)editInsertedField.GetValue(part);
                if(at<cursor||at<0||at>prior.Length-removed.Length)return null;
                if(String.CompareOrdinal(prior,at,removed,0,removed.Length)!=0)return null;
                result.Append(prior,cursor,at-cursor);result.Append(inserted);
                cursor=at+removed.Length;
            }
            if(cursor<0||cursor>prior.Length)return null;
            result.Append(prior,cursor,prior.Length-cursor);
            return result.ToString();
        }
        int singleAt=(int)editAtField.GetValue(edit);
        string singleRemoved=(string)editRemovedField.GetValue(edit),singleInserted=(string)editInsertedField.GetValue(edit);
        if(singleAt<0||singleAt>prior.Length-singleRemoved.Length)return null;
        if(String.CompareOrdinal(prior,singleAt,singleRemoved,0,singleRemoved.Length)!=0)return null;
        return prior.Substring(0,singleAt)+singleInserted+prior.Substring(singleAt+singleRemoved.Length);
    }

    // ---------------------------------------------------------------- walks and rebuild
    static void UndoStep(MainWindow window,StressModel model,string where){
        string previous=currentAction;currentAction=where;watchdogPlace="walk:undo";
        // A full walk is hundreds of undo/redo steps inside one action: the hang watchdog times each step,
        // not the whole walk (a 266+178-step walk at ~45 ms a step on the slow VM passed 20 s with no hang).
        if(stepInFlight)Interlocked.Exchange(ref stepStartTimestamp,Stopwatch.GetTimestamp());
        try{
            var result=new StepResult {Label=where,Kind=KindUndo};
            result.Before=Capture(window);
            Invoke(window,"Undo");
            PumpLight();
            result.After=Capture(window);
            AfterUndo(window,model,result);
        }finally{currentAction=previous;}
    }
    static void RedoStep(MainWindow window,StressModel model,string where){
        string previous=currentAction;currentAction=where;watchdogPlace="walk:redo";
        if(stepInFlight)Interlocked.Exchange(ref stepStartTimestamp,Stopwatch.GetTimestamp());
        try{
            var result=new StepResult {Label=where,Kind=KindRedo};
            result.Before=Capture(window);
            Invoke(window,"Redo");
            PumpLight();
            result.After=Capture(window);
            AfterRedo(window,model,result);
        }finally{currentAction=previous;}
    }
    static void CheckBufferMirror(MainWindow window,string where){
        SourceEditor source=Source(window);
        if(source==null||source.Document==null)return;
        CheckDiff("text.mirror.content",Text(window),source.Document.Text,where+": the text buffer and the source document disagree");
    }
    static void WalkAround(MainWindow window,StressModel model,int steps){
        if(model==null||!model.Valid)return;
        CheckBufferMirror(window,"checkpoint");
        int down=Math.Min(steps,model.Position);
        for(int i=0;i<down&&model.Valid;i++)UndoStep(window,model,"walk");
        for(int i=0;i<down&&model.Valid;i++)RedoStep(window,model,"walk");
        if(model.Valid)CheckDiff("walk.return",model.States[model.Position],Text(window),"the walk did not return to its start");
    }
    static void FullWalk(MainWindow window,StressModel model){
        if(model==null||!model.Valid)return;
        CheckBufferMirror(window,"full walk");
        int start=model.Position;string startText=Text(window);int guard=0;
        while(model.Valid&&model.Position>0&&guard++<UndoCap*2){watchdogDetail="undo "+guard+" pos="+model.Position;UndoStep(window,model,"full-walk");}
        if(model.Valid)Check("walk.bottom",model.Position==0,"position after undoing every entry="+model.Position);
        guard=0;
        while(model.Valid&&model.Position<start&&guard++<UndoCap*2){watchdogDetail="redo "+guard+" pos="+model.Position+" of "+start;RedoStep(window,model,"full-walk");}
        if(model.Valid){
            Check("walk.top.position",model.Position==start,"position="+model.Position+", expected="+start);
            CheckDiff("walk.top.text",startText,Text(window),"the walk did not replay to its start");
        }
    }
    static bool TryRebuild(MainWindow window,StressModel model){
        if(window==null||model==null)return false;
        object tab=FieldValue(window,"activeDocument");
        if(!Object.ReferenceEquals(tab,model.Tab))return false;
        string previous=currentAction;currentAction="rebuild";
        try{
            DocumentHistory history=HistoryOf(window);
            string current=Text(window);bool plain=Plain(window);
            var texts=new List<string>();var modes=new List<bool>();
            int undos=0;
            while(history.CanUndo&&undos<UndoCap*2){Invoke(window,"Undo");PumpLight();texts.Add(Text(window));modes.Add(Plain(window));undos++;}
            string baseText=Text(window);bool basePlain=Plain(window);
            int redos=0;
            while(history.CanRedo&&redos<UndoCap*2){Invoke(window,"Redo");PumpLight();redos++;}
            if(undos!=redos||!String.Equals(Text(window),current,StringComparison.Ordinal)||Plain(window)!=plain){model.Valid=false;return false;}
            if(UndoList(history).Count!=undos||RedoList(history).Count!=0){model.Valid=false;return false;}
            var states=new List<string>();var stateModes=new List<bool>();
            states.Add(baseText);stateModes.Add(basePlain);
            if(undos>0){
                for(int i=undos-2;i>=0;i--){states.Add(texts[i]);stateModes.Add(modes[i]);}
                states.Add(current);stateModes.Add(plain);
            }
            if(states.Count!=undos+1){model.Valid=false;return false;}
            model.States.Clear();model.Modes.Clear();
            foreach(string value in states)model.States.Add(value);
            foreach(bool value in stateModes)model.Modes.Add(value);
            model.Position=undos;model.Valid=true;
            return true;
        }catch(Exception){model.Valid=false;return false;}
        finally{currentAction=previous;}
    }
    // ---------------------------------------------------------------- actions
    static StepResult ExecuteAction(MainWindow window,Random random,int step){
        currentAction="step "+step.ToString(CultureInfo.InvariantCulture);
        var result=new StepResult();
        result.Before=Capture(window);
        if(txtToggleIndex<TxtToggleSteps.Length&&step==TxtToggleSteps[txtToggleIndex]){txtToggleIndex++;return ExecuteTextMode(window,random,result);}
        // The tab switch and the save+reload clear or split the stack, so they stay outside the
        // depth-building window (300..620) and the mixed history there stays in one tab.
        if(step%95==0&&(step<300||step>620))return ExecuteTab(window,random,result);
        if(step%70==0&&(step<300||step>620))return ExecuteSaveReload(window,random,result);
        if(step%113==0)return ExecuteLive(window,random,result);
        if(step%87==0)return ExecuteRaw(window,random,result);
        if(step%57==0)return ExecuteReplace(window,random,result);
        // Deep phase: the whole session stops undoing for a while and every action is one that
        // normally writes an entry, so the stack reaches a few hundred mixed entries (the TXT round
        // trip and the tab switches stay in it) and the 620 walk undoes and replays all of them.
        if(step>=300&&step<=620){
            int phase=step%9;
            if(phase==0||phase==8)return ExecuteEnter(window,random,result);
            if(phase==1)return ExecutePaste(window,random,result);
            if(phase==2)return ExecuteCut(window,random,result);
            if(phase==3)return ExecuteTypeLong(window,random,result);
            if(phase==4)return ExecuteIme(window,random,result,true);
            if(phase==5){if(Prop<bool>(window,"LiveFormat"))return ExecuteTableInsert(window,random,result);return ExecutePaste(window,random,result);}
            if(phase==6)return ExecuteCommand(window,random,result);
            return ExecuteErase(window,random,result,false);
        }
        int roll=random.Next(1000);
        if(roll<155)return ExecuteType(window,random,result,false);
        if(roll<215)return ExecuteType(window,random,result,true);
        if(roll<285)return ExecuteIme(window,random,result,false);
        if(roll<325)return ExecuteIme(window,random,result,true);
        if(roll<405)return ExecuteEnter(window,random,result);
        if(roll<495)return ExecuteErase(window,random,result,true);
        if(roll<555)return ExecuteErase(window,random,result,false);
        if(roll<645)return ExecuteCommand(window,random,result);
        if(roll<715)return ExecuteTable(window,random,result);
        if(roll<765)return ExecutePaste(window,random,result);
        if(roll<805)return ExecuteCut(window,random,result);
        if(roll<865)return ExecuteUndo(window,random,result);
        if(roll<910)return ExecuteRedo(window,random,result);
        if(roll<925)return ExecuteLive(window,random,result);
        if(roll<940)return ExecuteRaw(window,random,result);
        if(roll<950)return ExecuteTextMode(window,random,result);
        if(roll<960)return ExecuteTab(window,random,result);
        if(roll<965)return ExecuteSave(window,random,result);
        if(roll<970)return ExecuteSaveReload(window,random,result);
        return ExecuteReplace(window,random,result);
    }
    static StepResult Done(MainWindow window,StepResult result){
        result.After=Capture(window);
        watchdogSnapshot=SnapshotLine(window);
        return result;
    }
    static StepResult ExecuteType(MainWindow window,Random random,StepResult result,bool korean){
        if(random.Next(10)>=6)PositionCaret(window,random);
        string payload=korean?RandomKorean(random,1+random.Next(3)):TypingPayload(random);
        result.Label="type:"+Short(payload);result.PayloadLength=payload.Length;
        // The delimiter row state is read before the input: the preview swallows the text and nothing
        // moves the caret, so both orders agree, but the oracle has to know which state it saw.
        result.SwallowedDelimiterInput=TypingIntoTableDelimiter(window);
        SourceEditor source=Source(window);
        source.TextArea.PerformTextInput(payload);
        /* A swallowed delimiter-row input raises no TextEntered, so the run marker stays set; the
           product clears it after the input too (SourceEditor.TypingInputEnded), and the probe
           performs the same clear after its programmatic input. */
        if(result.SwallowedDelimiterInput)source.EndTypingRun();
        return Done(window,result);
    }
    // True when the caret sits on a table delimiter row with the live preview attached: the preview
    // keeps typed text out of that row (MarkdownLivePreview.MultiCellEntering: "The delimiter row is
    // structure: text typed there (a pipe included) would stop the table ... the input is kept out").
    // The probe's history oracle accepts the swallowed input only for that state.
    static bool TypingIntoTableDelimiter(MainWindow window){
        try{
            if(!Prop<bool>(window,"LiveFormat")||!Bool(window,"LivePreviewAttached"))return false;
            SourceEditor source=Source(window);
            if(source==null||source.Document==null)return false;
            object session=ReadMember(window,"livePreview");
            if(session==null)return false;
            MethodInfo info=null;
            foreach(MethodInfo candidate in session.GetType().GetMethods(Members)){
                if(candidate.Name!="Info")continue;
                ParameterInfo[] parameters=candidate.GetParameters();
                if(parameters.Length==1&&parameters[0].ParameterType==typeof(DocumentLine)){info=candidate;break;}
            }
            if(info==null)return false;
            DocumentLine line=source.Document.GetLineByOffset(Math.Max(0,Math.Min(source.CaretIndex,source.Document.TextLength)));
            object value=info.Invoke(session,new object[]{line});
            return value!=null&&ReadBool(value,"Table")&&ReadBool(value,"Delimiter");
        }catch(Exception){return false;}
    }
    static readonly Func<bool> WordImeTrue=delegate{return true;};
    static readonly Func<bool> WordImeFalse=delegate{return false;};
    // A multi-character payload: it may merge into the typing run it continues.
    static StepResult ExecuteTypeLong(MainWindow window,Random random,StepResult result){
        if(random.Next(10)<4)PositionCaret(window,random);
        string payload=RandomLatin(random,3+random.Next(5));
        result.Label="type:"+Short(payload);result.PayloadLength=payload.Length;
        Source(window).TextArea.PerformTextInput(payload);
        return Done(window,result);
    }
    static StepResult ExecuteTableInsert(MainWindow window,Random random,StepResult result){
        if(random.Next(10)<5)PositionCaret(window,random);
        result.Label="table:insert";currentAction=result.Label;
        Invoke(window,"InsertTable",2+random.Next(3),1+random.Next(2));
        PumpLight();
        return Done(window,result);
    }
    static StepResult ExecuteIme(MainWindow window,Random random,StepResult result,bool word){
        if(random.Next(10)<5)PositionCaret(window,random);
        SourceEditor source=Source(window);
        string payload=word?RandomWordClause(random):RandomKorean(random,1);
        Func<bool> previous=source.NonKoreanImeLayout;
        source.NonKoreanImeLayout=word?WordImeTrue:WordImeFalse;
        try{
            source.SimulateImeComposition();
            source.SimulateImeCommit(payload.Length);
            source.TextArea.PerformTextInput(payload);
        }finally{
            source.ResetCompositionSimulation();
            source.NonKoreanImeLayout=previous;
        }
        result.Label="ime:"+(word?"word:":"kr:")+Short(payload);
        result.ImeWord=word;result.PayloadLength=payload.Length;
        return Done(window,result);
    }
    static StepResult ExecuteEnter(MainWindow window,Random random,StepResult result){
        if(random.Next(5)==0)PositionCaret(window,random);
        if(Prop<bool>(window,"LiveFormat")){
            object command=Invoke(window,"SourceEnter");
            if(command==null)Source(window).TextArea.PerformTextInput("\n");
            else Invoke(window,"ApplyCommandResult",command,-1,0);
        }else Source(window).TextArea.PerformTextInput("\n");
        result.Label="enter";
        return Done(window,result);
    }
    static StepResult ExecuteErase(MainWindow window,Random random,StepResult result,bool back){
        if(random.Next(5)==0)PositionCaret(window,random);
        if(back)BackspaceOnce(window);else DeleteOnce(window);
        result.Label=back?"back":"delete";
        return Done(window,result);
    }
    static readonly string[] CommandNames=new string[]{
        "bold","italic","strike","code","h0","h1","h2","h3","h4","h5","h6",
        "bullet","numbered","task","quote","codeblock","rule"
    };
    static StepResult ExecuteCommand(MainWindow window,Random random,StepResult result){
        if(random.Next(10)<7)PositionCaret(window,random);
        int index=random.Next(CommandNames.Length);
        result.Label="cmd:"+CommandNames[index];
        switch(index){
            case 0:Invoke(window,"ToggleFormat","bold");break;
            case 1:Invoke(window,"ToggleFormat","italic");break;
            case 2:Invoke(window,"ToggleFormat","strike");break;
            case 3:Invoke(window,"ToggleFormat","code");break;
            case 4:Invoke(window,"SetHeading",0);break;
            case 5:Invoke(window,"SetHeading",1);break;
            case 6:Invoke(window,"SetHeading",2);break;
            case 7:Invoke(window,"SetHeading",3);break;
            case 8:Invoke(window,"SetHeading",4);break;
            case 9:Invoke(window,"SetHeading",5);break;
            case 10:Invoke(window,"SetHeading",6);break;
            case 11:Invoke(window,"ToggleListStyle",false);break;
            case 12:Invoke(window,"ToggleListStyle",true);break;
            case 13:Invoke(window,"ToggleTaskList");break;
            case 14:Invoke(window,"Quote");break;
            case 15:Invoke(window,"ToggleCodeBlock");break;
            default:Invoke(window,"LiveRule");break;
        }
        PumpLight();
        return Done(window,result);
    }
    static readonly string[] TableNames=new string[]{
        "rowadd","rowremove","coladd","colremove","delete","paragraph",
        "align-left","align-center","align-right","outdent","indent","insert"
    };
    static StepResult ExecuteTable(MainWindow window,Random random,StepResult result){
        if(random.Next(2)==0)PlaceCaretInTable(window,random);else PositionCaret(window,random);
        int index=random.Next(TableNames.Length);
        result.Label="table:"+TableNames[index];
        if(index==11)Invoke(window,"InsertTable",2+random.Next(3),1+random.Next(3));
        else Invoke(window,"EditTable",index);
        PumpLight();
        return Done(window,result);
    }
    static readonly string[] PasteSnippets=new string[]{
        "**굵은 붙여넣기** 문장","- 목록 항목\n- 두 번째 항목\n","| 가 | 나 |\n| --- | --- |\n| 다 | 라 |\n",
        "`인라인 코드` 와 *기울임*","여러 줄\n붙여넣기\n문장입니다.\n","가 | 나 | 다",
        "| x | y | z |","<span style=\"font-size:18px\">붙인 글자</span>","## 붙인 제목","한글 붙여넣기 값 42"
    };
    static readonly bool[] PastePlain=new bool[]{false,false,false,false,true,false,false,false,false,true};
    static StepResult ExecutePaste(MainWindow window,Random random,StepResult result){
        if(random.Next(2)==0)PositionCaret(window,random);
        int index=random.Next(PasteSnippets.Length);
        string value=PasteSnippets[index];bool plain=PastePlain[index];
        result.Label="paste:"+index.ToString(CultureInfo.InvariantCulture);result.PayloadLength=value.Length;
        if(Prop<bool>(window,"LiveFormat"))Invoke(window,"LivePaste",new System.Windows.DataObject(),plain,value);
        else Source(window).SelectedText=value;
        PumpLight();
        return Done(window,result);
    }
    static StepResult ExecuteCut(MainWindow window,Random random,StepResult result){
        SourceEditor source=Source(window);int length=source.Document.TextLength;
        if(length<3){result.Label="cut:0";result.Kind=KindSkipped;return result;}
        // Half of the cuts go through the product's own table-cut command (LiveTableCut) when the
        // selection spans a cell boundary: that path rewrites the cells through ApplyCommandResult
        // instead of deleting the selected source. The probe hook takes the DataObject, so the
        // user's clipboard is never written; the guard below is the handler's own gate.
        int tableLength;
        if(random.Next(2)==0&&Prop<bool>(window,"LiveFormat")&&Bool(window,"LivePreviewAttached")&&TryTableCut(window,random,out tableLength)){
            result.Label="cut:table";result.PayloadLength=tableLength;
            return Done(window,result);
        }
        int at=random.Next(length-2);int room=Math.Min(40,length-at);int size=3+random.Next(room-2);
        source.Select(at,size);
        result.Label="cut:"+size.ToString(CultureInfo.InvariantCulture);result.PayloadLength=size;
        source.SelectedText="";
        return Done(window,result);
    }
    static bool TryTableCut(MainWindow window,Random random,out int length){
        length=0;
        SourceEditor source=Source(window);TextDocument document=source.Document;int lines=document.LineCount;
        if(lines<=0)return false;
        int startLine=random.Next(lines)+1;int limit=Math.Min(lines,3000);
        for(int k=0;k<limit;k++){
            int number=((startLine-1+k)%lines)+1;
            DocumentLine line=document.GetLineByNumber(number);
            string text=document.GetText(line);
            if(text.IndexOf('|')<0||LooksLikeTableDelimiter(text))continue;
            int pipe=text.IndexOf('|');
            if(pipe<1||pipe>=text.Length-1)continue;
            int start=line.Offset+Math.Max(0,pipe-1-random.Next(3));
            int end=line.Offset+Math.Min(text.Length,pipe+1+random.Next(3));
            if(end<=start)continue;
            source.Select(start,end-start);
            var cells=(List<MarkdownLivePreview.Cell>)Invoke(window,"SourceRowCells",start);
            if(cells==null||cells.Count==0)continue;
            if(!MarkdownTableCommands.SpansCellBoundary(document,start,end-start,cells))continue;
            if(MarkdownTableCommands.ClearCellSelection(document,start,end-start,cells)==null)continue;
            MainWindow.LiveTableCutHook=delegate(System.Windows.DataObject data){};
            try{ApplicationCommands.Cut.Execute(null,source.TextArea);}
            finally{MainWindow.LiveTableCutHook=null;}
            length=end-start;
            return true;
        }
        return false;
    }
    static StepResult ExecuteUndo(MainWindow window,Random random,StepResult result){
        result.Label="undo";result.Kind=KindUndo;
        Invoke(window,"Undo");PumpLight();
        return Done(window,result);
    }
    static StepResult ExecuteRedo(MainWindow window,Random random,StepResult result){
        result.Label="redo";result.Kind=KindRedo;
        Invoke(window,"Redo");PumpLight();
        return Done(window,result);
    }
    static StepResult ExecuteLive(MainWindow window,Random random,StepResult result){
        bool hide=Static<bool>("hideMarkers");bool live=hide;
        result.Label=live?"mode:live":"mode:basic";currentAction=result.Label;
        Invoke(window,"SetLiveMode",live);Pump();
        Check("mode.switch",Static<bool>("hideMarkers")==!live,"hideMarkers="+Static<bool>("hideMarkers")+", wanted live="+live);
        return Done(window,result);
    }
    static StepResult ExecuteRaw(MainWindow window,Random random,StepResult result){
        if(Plain(window)||!Editing(window)){result.Label="raw:skip";result.Kind=KindSkipped;return result;}
        CheckBox preview=Field<CheckBox>(window,"preview");bool target=!(preview.IsChecked==true);
        result.Label=target?"raw:on":"raw:off";currentAction=result.Label;
        SetField(window,"loading",true);preview.IsChecked=target;SetField(window,"loading",false);
        Invoke(window,"SwitchSurface");Pump();
        Check("raw.attach",Bool(window,"LivePreviewAttached")==!target,"attached="+Bool(window,"LivePreviewAttached")+", raw="+target);
        return Done(window,result);
    }
    static StepResult ExecuteTextMode(MainWindow window,Random random,StepResult result){
        if(Text(window).Length>=200000){result.Label="txt:skip";result.Kind=KindSkipped;result.Note="document too large for the synchronous conversion";return result;}
        bool before=Plain(window);
        result.Label=before?"txt:toMarkdown":"txt:toPlain";currentAction=result.Label;
        Invoke(window,"ToggleTextMode");Pump();
        Check("txt.mode",Plain(window)==!before,"plain="+Plain(window)+", expected="+(!before));
        return Done(window,result);
    }
    static StepResult ExecuteSave(MainWindow window,Random random,StepResult result){
        result.Label="save";currentAction=result.Label;result.Saved=SaveCurrent(window,result);PumpLight();
        return Done(window,result);
    }
    static StepResult ExecuteSaveReload(MainWindow window,Random random,StepResult result){
        if(Text(window).Length>=200000){result.Label="reload:skip";result.Kind=KindSkipped;return result;}
        result.Label="save+reload";currentAction=result.Label;result.Kind=KindReload;
        if(!SaveCurrent(window,result)){result.Kind=KindSkipped;return Done(window,result);}
        string expected=NormalizeNewlines(Text(window),DocumentNewline(window));
        try{Invoke(window,"LoadFileCore",result.File);}
        catch(Exception ex){
            Fail("reload.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(reload completes)",OneLine(ex.ToString()));
            result.Kind=KindSkipped;return Done(window,result);
        }
        if(!Editing(window))window.SetMode(true);
        Pump();
        result.Note=expected;
        return Done(window,result);
    }
    static StepResult ExecuteTab(MainWindow window,Random random,StepResult result){
        IList documents=(IList)FieldValue(window,"documents");
        object oldTab=FieldValue(window,"activeDocument");result.OldTab=oldTab;
        StressModel oldModel=ModelFor(window);
        if(oldModel!=null&&oldModel.Valid&&!ModelCurrent(oldModel,result.Before)){
            Fail("tab.before","the leaving tab's stack did not match its text",ModelSummary(oldModel),StateSummary(result.Before));
            Invalidate(oldModel);TryRebuild(window,oldModel);
        }
        if(documents==null||documents.Count<2){result.Label="tab:open";currentAction=result.Label;Invoke(window,"CreateTab");}
        else{result.Label="tab:switch";currentAction=result.Label;Invoke(window,"CycleTab",1);}
        Pump();
        object newTab=FieldValue(window,"activeDocument");
        Check("tab.switch",!Object.ReferenceEquals(newTab,oldTab),"the active tab did not change");
        result.Kind=KindTab;
        return Done(window,result);
    }
    static StepResult ExecuteReplace(MainWindow window,Random random,StepResult result){
        if(Text(window).Length==0){result.Label="replace:empty";result.Kind=KindSkipped;return result;}
        string query=random.Next(2)==0?"a":"e";
        // "zq" contains neither query letter, so any match changes the text; an identity replace
        // would hide whether the history path ran.
        string replacement="zq";
        result.Label="replace:"+query+"->"+replacement;currentAction=result.Label;
        string before=Text(window);
        int rawMatches=CountInsensitive(before,query);
        bool visibleSearch=Bool(window,"LivePreviewAttached")&&!Plain(window)&&(Static<bool>("hideMarkers")||!Editing(window)||Source(window).IsReadOnly);
        Invoke(window,"ShowReplace");
        Field<TextBox>(window,"query").Text=query;
        Field<TextBox>(window,"replacement").Text=replacement;
        Invoke(window,"ReplaceAll");
        SourceEditor source=Source(window);
        if(source!=null&&source.Document!=null)source.Select(Math.Min(source.SelectionStart,source.Document.TextLength),0);
        string after=Text(window);
        if(rawMatches==0)Bump(replaceNotes,"no-match in the source");
        else if(String.Equals(before,after,StringComparison.Ordinal)){
            Bump(replaceNotes,"source matches but no text change");
            if(!visibleSearch)Fail("replace.nochange","replace-all found source matches but changed nothing","the matches are replaced","query="+query+", rawMatches="+rawMatches);
        }else Bump(replaceNotes,"text changed");
        Invoke(window,"CloseSearch");
        PumpLight();
        return Done(window,result);
    }

    // ---------------------------------------------------------------- keys, caret, payloads
    static void BackspaceOnce(MainWindow window){
        if(RaisePreviewKey(window,Key.Back))return;
        SourceEditor source=Source(window);
        if(source!=null)EditingCommands.Backspace.Execute(null,source.TextArea);
    }
    static void DeleteOnce(MainWindow window){
        if(RaisePreviewKey(window,Key.Delete))return;
        SourceEditor source=Source(window);
        if(source!=null)EditingCommands.Delete.Execute(null,source.TextArea);
    }
    static bool RaisePreviewKey(MainWindow window,Key key){
        SourceEditor source=Source(window);if(source==null)return false;
        PresentationSource presentation=PresentationSource.FromVisual(source);
        if(presentation==null)return false;
        var args=new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,key) {RoutedEvent=Keyboard.PreviewKeyDownEvent};
        source.TextArea.RaiseEvent(args);
        return args.Handled;
    }
    static void PositionCaret(MainWindow window,Random random){
        SourceEditor source=Source(window);int length=source.Document.TextLength;
        if(length<=0){source.Select(0,0);return;}
        int roll=random.Next(10);
        if(roll<2){source.Select(length,0);return;}
        int at=random.Next(length);
        if(roll<6){source.Select(at,0);return;}
        if(roll<8){
            int size=1+random.Next(Math.Min(24,length-at));
            source.Select(at,size);return;
        }
        DocumentLine line=source.Document.GetLineByOffset(at);
        int size2=1+random.Next(Math.Min(40,length-line.Offset));
        source.Select(line.Offset,size2);
    }
    static bool PlaceCaretInTable(MainWindow window,Random random){
        SourceEditor source=Source(window);var document=source.Document;int lines=document.LineCount;
        if(lines<=0)return false;
        int start=random.Next(lines)+1;
        int limit=Math.Min(lines,3000);
        for(int k=0;k<limit;k++){
            int number=((start-1+k)%lines)+1;
            DocumentLine line=document.GetLineByNumber(number);
            string value=document.GetText(line);
            if(value.IndexOf('|')>=0&&!LooksLikeTableDelimiter(value)){
                int at=line.Offset+random.Next(Math.Max(1,value.Length));
                source.Select(Math.Min(at,document.TextLength),0);return true;
            }
        }
        return false;
    }
    static bool LooksLikeTableDelimiter(string value){
        string text=value.Trim();if(text.Length==0)return false;bool dash=false;
        for(int i=0;i<text.Length;i++){
            char c=text[i];if(c=='-'){dash=true;continue;}
            if(c=='|'||c==':'||c==' '||c=='\t')continue;
            return false;
        }
        return dash;
    }
    static readonly string KoreanSyllables="가나다라마바사아자차카타파하거너더러머버서어저처커터퍼허고노도로모보소오조초코토포호구누두루무부수우주추쿠투푸후기니디리미비시이지치키티피히값메모표행열";
    static readonly string LatinChars="abcdefghijklmnopqrstuvwxyz0123456789";
    static string RandomKorean(Random random,int count){
        var text=new StringBuilder(count);
        for(int i=0;i<count;i++)text.Append(KoreanSyllables[random.Next(KoreanSyllables.Length)]);
        return text.ToString();
    }
    static string RandomWordClause(Random random){
        string[] clauses=new string[]{"日本","中文","漢字","テスト"};
        return clauses[random.Next(clauses.Length)];
    }
    static string TypingPayload(Random random){
        switch(random.Next(12)){
            case 0:case 1:case 2:return RandomLatin(random,1+random.Next(6));
            case 3:case 4:case 5:return RandomKorean(random,1+random.Next(2));
            case 6:return " ";
            case 7:return "|";
            case 8:return "*";
            case 9:return "**";
            case 10:return RandomLatin(random,1+random.Next(2));
            default:return "~";
        }
    }
    static string RandomLatin(Random random,int count){
        var text=new StringBuilder(count);
        for(int i=0;i<count;i++)text.Append(LatinChars[random.Next(LatinChars.Length)]);
        return text.ToString();
    }
    static int CountInsensitive(string text,string needle){
        if(String.IsNullOrEmpty(text)||String.IsNullOrEmpty(needle))return 0;
        int count=0,at=0;
        while(at<=text.Length-needle.Length){int found=text.IndexOf(needle,at,StringComparison.OrdinalIgnoreCase);if(found<0)break;count++;at=found+needle.Length;}
        return count;
    }
    static string Short(string value){
        if(value==null)return "<null>";
        string text=value.Replace("\r\n","\\n").Replace("\r","\\n").Replace("\n","\\n");
        return text.Length<=18?text:text.Substring(0,18)+"...";
    }
    static string NormalizeNewlines(string value,string newline){
        if(newline==null||newline.Length==0)newline="\n";
        return value.Replace("\r\n","\n").Replace("\r","\n").Replace("\n",newline);
    }
    // ---------------------------------------------------------------- save and reload
    static string StressFile(MainWindow window){
        int index=TabIndex(window);
        string extension=Plain(window)?".txt":".md";
        return Path.Combine(outRoot,"stress-s"+currentSeed.ToString(CultureInfo.InvariantCulture)+"-t"+index.ToString(CultureInfo.InvariantCulture)+extension);
    }
    static int TabIndex(MainWindow window){
        IList documents=(IList)FieldValue(window,"documents");object active=FieldValue(window,"activeDocument");
        if(documents==null||active==null)return 0;
        int index=documents.IndexOf(active);return index<0?0:index;
    }
    static bool SaveCurrent(MainWindow window,StepResult result){
        string file=StressFile(window);
        result.File=file;
        bool ok;
        try{ok=(bool)Invoke(window,"SaveToPath",file,new UTF8Encoding(false),false);}
        catch(Exception ex){Fail("save.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(save succeeds)",OneLine(ex.ToString()));return false;}
        if(!ok){Fail("save.refused","SaveToPath returned false","true","false");return false;}
        result.Saved=true;
        string expected=NormalizeNewlines(Text(window),DocumentNewline(window));
        string got;
        try{got=new UTF8Encoding(false,true).GetString(File.ReadAllBytes(file));}
        catch(Exception ex){Fail("save.file",OneLine(ex.GetType().Name+": "+ex.Message),expected,"(unreadable file)");return false;}
        CheckDiff("save.file",expected,got,"the file must hold the normalized buffer");
        return true;
    }
    static string Fixture(){
        return "# History stress\n\nFirst paragraph with alpha and beta words.\n\n- alpha item\n- beta item\n\n> quoted alpha line\n\n| head | note |\n| --- | --- |\n| one | alpha |\n| two | beta |\n\nClosing alpha paragraph.\n";
    }
    // ---------------------------------------------------------------- history cap case
    // The main session is small enough that the entry cap may not be reached, so the cap is shown
    // here on the same surface the user edits: 600 single-change inserts, then the whole stack is
    // undone and redone and every step must land on the recorded state.
    static void RunHistoryCapCase(){
        currentSeed=0;currentStep=0;currentAction="history-cap";ResetStats();
        MainWindow window=null;
        try{
            window=CreateWindow();watchdogWindow=window;HookCounters(window);window.SetText("");SetField(window,"saved","");SetField(window,"recoveredDocument",false);
            Pump();window.SetMode(true);Pump();
            Check("cap.surface",Editing(window),"editing="+Editing(window));
            SourceEditor source=Source(window);TextDocument document=source.Document;
            var states=new List<string>();var builder=new StringBuilder();states.Add("");
            for(int i=1;i<=600;i++){
                string token=Token(i);document.Insert(document.TextLength,token);builder.Append(token);
                states.Add(builder.ToString());
                if((i%64)==0)PumpLight();
            }
            DocumentHistory history=HistoryOf(window);
            NoteStats(Capture(window));
            Check("cap.fill",states[600].Length==document.TextLength,"chars="+document.TextLength);
            Check("cap.entries",UndoList(history).Count==UndoCap,"undo="+UndoList(history).Count+", cap="+UndoCap);
            Check("cap.bytes",HistoryBytes(history)>=0&&HistoryBytes(history)<=ByteCap,"bytes="+HistoryBytes(history));
            int index=600;bool undoOk=true;
            for(int k=1;k<=UndoCap;k++){
                Invoke(window,"Undo");index--;
                if((k%64)==0)PumpLight();
                if(!String.Equals(Text(window),states[index],StringComparison.Ordinal)){
                    undoOk=false;CheckDiff("cap.undo.snapshot",states[index],Text(window),"undo "+k+"; expected the state after edit "+index);break;
                }
            }
            Check("cap.undo.walk",undoOk,"512 undo steps checked");
            Check("cap.undo.count",UndoList(history).Count==0&&RedoList(history).Count==UndoCap,"undo="+UndoList(history).Count+", redo="+RedoList(history).Count);
            Check("cap.undo.oldest",String.Equals(Text(window),states[600-UndoCap],StringComparison.Ordinal),"oldest kept state must be the state after edit "+(600-UndoCap));
            bool redoOk=true;
            for(int k=1;k<=UndoCap;k++){
                Invoke(window,"Redo");index++;
                if((k%64)==0)PumpLight();
                if(!String.Equals(Text(window),states[index],StringComparison.Ordinal)){
                    redoOk=false;CheckDiff("cap.redo.snapshot",states[index],Text(window),"redo "+k+"; expected the state after edit "+index);break;
                }
            }
            Check("cap.redo.walk",redoOk,"512 redo steps checked");
            Check("cap.redo.final",String.Equals(Text(window),states[600],StringComparison.Ordinal),"text after redo all");
            Check("cap.redo.count",UndoList(history).Count==UndoCap&&RedoList(history).Count==0,"undo="+UndoList(history).Count+", redo="+RedoList(history).Count);
            Check("cap.bytes.after",HistoryBytes(history)>=0&&HistoryBytes(history)<=ByteCap,"bytes="+HistoryBytes(history));
        }catch(Exception ex){
            Fail("cap.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(cap case completes)",OneLine(ex.ToString()));
        }finally{watchdogWindow=null;CloseWindow(window);}
        report.Append("\n## history cap case\n");
        report.Append("- peak undo entries: ").Append(maxUndoEntries).Append(" (cap ").Append(UndoCap).Append(")\n");
        report.Append("- peak history bytes: ").Append(maxBytesSeen).Append(" (cap ").Append(ByteCap).Append(")\n");
    }
    static string Token(int index){return "z"+index.ToString("0000",CultureInfo.InvariantCulture)+";";}

    // ---------------------------------------------------------------- paste then typing case
    // A paste is its own undo step and typing right after it starts a new one (Windows Notepad and
    // Word behave the same way): the caret never moves between the two, so the typing run must not
    // fold into the paste's entry. One undo then removes only the typed character, the next the
    // paste, and redo puts the paste back.
    static void RunPasteUndoCase(){
        currentSeed=0;currentStep=0;currentAction="paste-undo";ResetStats();
        MainWindow window=null;
        try{
            window=CreateWindow();watchdogWindow=window;HookCounters(window);
            window.SetText("abc");SetField(window,"saved","abc");SetField(window,"recoveredDocument",false);
            Pump();window.SetMode(true);Pump();
            SourceEditor source=Source(window);DocumentHistory history=HistoryOf(window);
            int baseCount=UndoList(history).Count;
            source.Select(source.Document.TextLength,0);
            if(Prop<bool>(window,"LiveFormat"))Invoke(window,"LivePaste",new System.Windows.DataObject(),true,"hello");
            else source.SelectedText="hello";
            PumpLight();
            int afterPaste=UndoList(history).Count;
            Check("paste-undo.paste-entry",afterPaste==baseCount+1,"undo="+afterPaste+", expected="+(baseCount+1)+" (the paste is one entry)");
            source.TextArea.PerformTextInput("!");
            PumpLight();
            int afterType=UndoList(history).Count;
            Check("paste-undo.type-entry",afterType==baseCount+2,"undo="+afterType+", expected="+(baseCount+2)+" (typing after a paste starts a new entry)");
            Invoke(window,"Undo");PumpLight();
            CheckDiff("paste-undo.undo1","abchello",Text(window),"one undo removes only the typed character");
            Invoke(window,"Undo");PumpLight();
            CheckDiff("paste-undo.undo2","abc",Text(window),"the second undo removes the paste");
            Invoke(window,"Redo");PumpLight();
            CheckDiff("paste-undo.redo","abchello",Text(window),"redo puts the paste back");
        }catch(Exception ex){
            Fail("paste-undo.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(case completes)",OneLine(ex.ToString()));
        }finally{watchdogWindow=null;CloseWindow(window);}
        report.Append("\n## paste then typing case\n");
        report.Append("- one paste, one typed character, then undo twice and redo once\n");
    }

    // ---------------------------------------------------------------- CR-only TXT caret case
    // A buffer whose newlines are all CR has no '\n' line start, so the shared MD->TXT rule takes
    // no line-start floor there and maps the caret through the converted prefix alone. That already
    // lands on the second line's first character (offset 4): Convert keeps a trailing newline (the
    // blank line emits one, then one is dropped), so the prefix up to the line start converts to
    // exactly that offset. The check pins it, and the round trip back to Markdown, so a future
    // change to the shared rule cannot shift a CR-only document by one.
    static void RunTextModeCaretCase(){
        currentSeed=0;currentStep=0;currentAction="txt-caret";ResetStats();
        MainWindow window=null;
        try{
            window=CreateWindow();watchdogWindow=window;HookCounters(window);
            window.SetText("one\rtwo\rthree");SetField(window,"saved","one\rtwo\rthree");SetField(window,"recoveredDocument",false);
            Pump();window.SetMode(true);Pump();
            SourceEditor source=Source(window);
            source.Select(4,0);
            Invoke(window,"ToggleTextMode");Pump();
            Check("txt-caret.plain",Plain(window),"plain="+Plain(window));
            CheckDiff("txt-caret.text","one\ntwo\nthree",NormalizeNewlines(Text(window),"\n"),"CR-only Markdown converted to TXT");
            Check("txt-caret.caret",source.CaretIndex==4,"caret="+source.CaretIndex+", expected=4");
            Invoke(window,"ToggleTextMode");Pump();
            Check("txt-caret.back",!Plain(window)&&source.CaretIndex==4,"plain="+Plain(window)+", caret="+source.CaretIndex);
        }catch(Exception ex){
            Fail("txt-caret.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(case completes)",OneLine(ex.ToString()));
        }finally{watchdogWindow=null;CloseWindow(window);}
        report.Append("\n## CR-only TXT caret case\n");
        report.Append("- MD->TXT and back with the caret on the second line's first character\n");
    }

    // ---------------------------------------------------------------- one typing run case
    // The product rule (Windows 11 Notepad and Word): continuous typing is ONE undo step, text
    // committed by any IME (Korean syllables, Japanese/Chinese conversions) included. A Backspace
    // after typing is a step of its own, so the characters it deleted come back on the next undo
    // (one Ctrl+Z after Backspacing shows the text again, exactly like Notepad). The step ends when
    // the caret is moved (arrows, mouse, Home/End, PageUp/Down), Delete is pressed, a paste/cut/drop
    // or any command runs, the editor loses keyboard focus, or the document is switched. Each case
    // starts from an empty document, so the undo-entry count the action produced is the number of
    // steps it wrote.
    static void RunUndoRuleCase(){
        currentSeed=0;currentStep=0;currentAction="undo-rule";ResetStats();
        MainWindow window=null;
        try{
            window=CreateWindow();watchdogWindow=window;HookCounters(window);
            window.SetText("");SetField(window,"saved","");SetField(window,"recoveredDocument",false);
            Pump();window.SetMode(true);Pump();
            SourceEditor source=Source(window);
            // Typing and the two commits are the first step; the Backspace that deletes 글 is the
            // second (its undo brings the syllable back) and the x typed after it the third.
            RunUndoRuleCase(window,source,"abc + 한글 + Backspace + x","abc한x",3,delegate{
                source.TextArea.PerformTextInput("abc");
                CommitImeText(source,"한",false);
                CommitImeText(source,"글",false);
                BackspaceOnce(window);
                source.TextArea.PerformTextInput("x");
            });
            RunUndoRuleCase(window,source,"type, Left arrow, type","ba",2,delegate{
                source.TextArea.PerformTextInput("a");
                EditingCommands.MoveLeftByCharacter.Execute(null,source.TextArea);
                source.TextArea.PerformTextInput("b");
            });
            RunUndoRuleCase(window,source,"type, paste, type","aPb",3,delegate{
                source.TextArea.PerformTextInput("a");
                bool pasted=false;
                if(Prop<bool>(window,"LiveFormat"))pasted=(bool)Invoke(window,"LivePaste",new System.Windows.DataObject(),true,"P");
                if(!pasted)source.SelectedText="P";
                PumpLight();
                source.TextArea.PerformTextInput("b");
            });
            if(!RunUndoRuleFocusCase(window,source))Check("undo-rule.focus skipped",true,"keyboard focus could not leave and return in this run");
            RunUndoRuleCase(window,source,"three Japanese conversion commits","日本語テスト",1,delegate{
                CommitImeText(source,"日本",true);
                CommitImeText(source,"語",true);
                CommitImeText(source,"テスト",true);
            });
            // A typed key whose handler changes the document twice in one update (the live preview
            // writes a marker run and the text together) is still one key: the first change folds
            // into the run and the union entry must fold there too, not start a new step.
            RunUndoRuleCase(window,source,"two typed changes in one update continue the run","abc",1,delegate{
                source.TextArea.PerformTextInput("a");
                TypedChanges(source,delegate{
                    int at=source.CaretOffset;
                    source.Document.Insert(at,"b");
                    source.Document.Insert(at+1,"c");
                    source.CaretOffset=at+2;
                });
            });
            // A typed replacement of the run's own text (a hidden marker run rearranged by the
            // same key: the closing-edge space moving back inside the pair) belongs to the run.
            RunUndoRuleCase(window,source,"typed replacement inside the run","zbc",1,delegate{
                source.TextArea.PerformTextInput("abc");
                TypedChanges(source,delegate{
                    source.Document.Replace(0,1,"z");
                    source.CaretOffset=1;
                });
            });
            // Two typed changes that together rewrite the head of the run (the union span, not the
            // first change alone) fold the same way.
            RunUndoRuleCase(window,source,"two typed changes replacing the run","zqc",1,delegate{
                source.TextArea.PerformTextInput("abc");
                TypedChanges(source,delegate{
                    source.Document.Replace(0,1,"z");
                    source.Document.Replace(1,1,"q");
                    source.CaretOffset=2;
                });
            });
            // The run stops growing at 1024 characters (one key copies the run's text), so 1500
            // characters typed without a break make exactly two steps: 1024 and 476.
            RunUndoRuleCase(window,source,"1500 characters typed continuously",new string('a',1500),2,delegate{
                // Every character is its own routed input. A light pump every 250 keys keeps the
                // watchdog's UI heartbeat fresh; it moves no caret and runs no command, so the
                // typing run continues across it.
                for(int i=0;i<1500;i++){source.TextArea.PerformTextInput("a");if((i%250)==249)PumpLight();}
            });
        }catch(Exception ex){
            Fail("undo-rule.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(case completes)",OneLine(ex.ToString()));
        }finally{watchdogWindow=null;CloseWindow(window);}
        report.Append("\n## one typing run case\n");
        report.Append("- abc+한글+Backspace+x (the Backspace is its own step); type/Left/type; type/paste/type; focus away and back; three Japanese commits; two typed changes in one update; typed replacement inside the run; 1500 characters typed in one run (two steps)\n");
    }
    static void RunUndoRuleCase(MainWindow window,SourceEditor source,string label,string expected,int steps,Action action){
        currentAction="undo-rule:"+label;
        window.SetText("");window.SetMode(true);source.Focus();Pump();
        DocumentHistory history=HistoryOf(window);
        int before=UndoList(history).Count;
        action();
        PumpLight();
        string text=Text(window);
        int produced=UndoList(history).Count-before;
        Check("undo-rule.text "+label,String.Equals(text,expected,StringComparison.Ordinal),"text=["+OneLine(text)+"], expected=["+OneLine(expected)+"]");
        Check("undo-rule.steps "+label,produced==steps,"steps="+produced+", expected="+steps+" (undo entries)");
        for(int i=0;i<steps&&HistoryOf(window).CanUndo;i++){Invoke(window,"Undo");PumpLight();}
        Check("undo-rule.undo "+label,Text(window)=="","after "+steps+" undo(s): ["+OneLine(Text(window))+"]");
        int redos=0;
        while(HistoryOf(window).CanRedo&&redos<steps+1){Invoke(window,"Redo");PumpLight();redos++;}
        Check("undo-rule.redo "+label,String.Equals(Text(window),expected,StringComparison.Ordinal),"after "+redos+" redo(s): ["+OneLine(Text(window))+"]");
        Pump();
    }
    // A focus loss is a step boundary: type, move keyboard focus to the find box (or nowhere) and
    // back, then type; the second typing must be its own entry even though the caret never moved.
    static bool RunUndoRuleFocusCase(MainWindow window,SourceEditor source){
        currentAction="undo-rule:focus";
        window.SetText("");window.SetMode(true);Pump();
        source.Focus();Keyboard.Focus(source.TextArea);Pump();
        if(!source.TextArea.IsKeyboardFocusWithin)return false;
        source.TextArea.PerformTextInput("a");PumpLight();
        int before=UndoList(HistoryOf(window)).Count;
        Invoke(window,"ShowSearch");Pump();
        TextBox query=Field<TextBox>(window,"query");
        bool away=query!=null&&query.Focus();
        Pump();
        away=away&&!source.TextArea.IsKeyboardFocusWithin;
        if(!away){Keyboard.ClearFocus();Pump();away=!source.TextArea.IsKeyboardFocusWithin;}
        if(!away){Invoke(window,"CloseSearch");Pump();return false;}
        source.Focus();Keyboard.Focus(source.TextArea);Pump();
        if(!source.TextArea.IsKeyboardFocusWithin){Invoke(window,"CloseSearch");Pump();return false;}
        source.TextArea.PerformTextInput("b");PumpLight();
        string text=Text(window);
        int steps=UndoList(HistoryOf(window)).Count-before;
        Check("undo-rule.focus.text",String.Equals(text,"ab",StringComparison.Ordinal),"text=["+OneLine(text)+"]");
        Check("undo-rule.focus.steps",steps==1,"new entries after the focus return="+steps+", expected=1");
        Invoke(window,"Undo");PumpLight();
        Check("undo-rule.focus.undo1",String.Equals(Text(window),"a",StringComparison.Ordinal),"after one undo: ["+OneLine(Text(window))+"]");
        Invoke(window,"Undo");PumpLight();
        Check("undo-rule.focus.undo2",Text(window)=="","after two undos: ["+OneLine(Text(window))+"]");
        Invoke(window,"CloseSearch");Pump();
        return true;
    }
    // The commit-result paths around undo (bughunt3 undo-ime 1/2/4). A commit requested while the
    // caret was still in a typing run belongs to that run even when keyboard focus leaves before
    // the result arrives; a lost result is aged out so undo is not skipped forever; a command
    // parked behind the composition is released the moment the late result lands; and a store
    // that ignored CPS_CANCEL cannot insert the captured text a second time.
    static void RunImeCommitEdgeCase(){
        currentSeed=0;currentStep=0;currentAction="ime-commit-edge";ResetStats();
        MainWindow window=null;
        try{
            window=CreateWindow();watchdogWindow=window;HookCounters(window);
            window.SetText("");SetField(window,"saved","");SetField(window,"recoveredDocument",false);
            Pump();window.SetMode(true);Pump();
            SourceEditor source=Source(window);
            source.Focus();Keyboard.Focus(source.TextArea);Pump();
            bool focused=source.TextArea.IsKeyboardFocusWithin;
            Check("ime-commit.focus",true,focused?"text area owns keyboard focus":"keyboard focus unavailable in this run");
            if(focused){
                // B: commit requested in the run, focus leaves, the late syllable still joins it.
                source.TextArea.PerformTextInput("ab");PumpLight();
                int before=UndoList(HistoryOf(window)).Count;
                source.SimulateImeComposition();
                source.BeginCompositionCommit();
                bool away=MoveFocusAway(window,source);
                if(!away)Check("ime-commit.late",true,"keyboard focus could not leave the body");
                else{
                    source.SimulateImeCommit(1);
                    bool adopted=source.AcceptLateImeCommit("가");
                    PumpLight();
                    Check("ime-commit.late-adopted",adopted,"AcceptLateImeCommit declined the counted result");
                    Check("ime-commit.late-text",String.Equals(Text(window),"ab가",StringComparison.Ordinal),"text=["+OneLine(Text(window))+"]");
                    int steps=UndoList(HistoryOf(window)).Count-before;
                    Check("ime-commit.late-run",steps==0,"new undo entries after the adopted syllable="+steps+", expected 0");
                    Invoke(window,"Undo");PumpLight();
                    Check("ime-commit.late-undo",Text(window)=="","after one undo: ["+OneLine(Text(window))+"]");
                    Invoke(window,"Redo");PumpLight();
                }
                Invoke(window,"CloseSearch");Pump();
                source.ResetCompositionSimulation();
                // E: the command parked behind the composition runs when the late result lands,
                // not 500 ms later when the composition timer happens to tick.
                window.SetText("");SetField(window,"saved","");window.SetMode(true);Pump();
                source.Focus();Keyboard.Focus(source.TextArea);Pump();
                source.TextArea.PerformTextInput("ab");PumpLight();
                bool ran=false;
                source.SimulateImeComposition();
                source.RunAfterComposition(delegate{ran=true;});
                source.BeginCompositionCommit();
                bool parkedAway=MoveFocusAway(window,source);
                if(!parkedAway)Check("ime-commit.parked",true,"keyboard focus could not leave the body");
                else{
                    source.SimulateImeCommit(1);
                    bool accepted=source.AcceptLateImeCommit("다");
                    // The timer is the fallback release; stopping it proves this path released the
                    // command itself. Pump then runs the posted callback only if it was posted.
                    StopCompositionTimer(source);
                    Pump();
                    Check("ime-commit.parked-adopted",accepted,"AcceptLateImeCommit declined the counted result");
                    Check("ime-commit.parked-release",ran,"the parked command did not run after the result landed");
                    Check("ime-commit.parked-text",String.Equals(Text(window),"ab다",StringComparison.Ordinal),"text=["+OneLine(Text(window))+"]");
                }
                Invoke(window,"CloseSearch");Pump();
                source.ResetCompositionSimulation();
                // G: the capture inserted the clause and asked the IME to cancel; a store that
                // ignored the cancel reports the same result again and must be dropped.
                window.SetText("");SetField(window,"saved","");window.SetMode(true);Pump();
                source.Focus();Keyboard.Focus(source.TextArea);Pump();
                source.TextArea.PerformTextInput("가");PumpLight();
                source.SimulateCapturedImeResult("가");
                source.SimulateImeCommit(1);
                source.TextArea.PerformTextInput("가");
                source.EndTypingRun();
                PumpLight();
                Check("ime-commit.duplicate.text",String.Equals(Text(window),"가",StringComparison.Ordinal),"text=["+OneLine(Text(window))+"]");
                int duplicateSteps=UndoList(HistoryOf(window)).Count;
                Check("ime-commit.duplicate.steps",duplicateSteps==1,"undo entries="+duplicateSteps+", expected 1");
                Invoke(window,"Undo");PumpLight();
                Check("ime-commit.duplicate.undo",Text(window)=="","after one undo: ["+OneLine(Text(window))+"]");
                source.ResetCompositionSimulation();
            }
        }catch(Exception ex){
            Fail("ime-commit.exception",OneLine(ex.GetType().Name+": "+ex.Message),"(case completes)",OneLine(ex.ToString()));
        }finally{watchdogWindow=null;CloseWindow(window);}
        report.Append("\n## ime commit edge case\n");
        report.Append("- late commit joins its run; parked command released by the late result; duplicate CPS_CANCEL result dropped\n");
    }
    // Moves keyboard focus off the body the way the find bar does; the probe skips the case when
    // this run has no keyboard focus at all.
    static bool MoveFocusAway(MainWindow window,SourceEditor source){
        Invoke(window,"ShowSearch");Pump();
        TextBox query=Field<TextBox>(window,"query");
        bool away=query!=null&&query.Focus();
        Pump();
        away=away&&!source.TextArea.IsKeyboardFocusWithin;
        if(!away){Keyboard.ClearFocus();Pump();away=!source.TextArea.IsKeyboardFocusWithin;}
        return away;
    }
    static void StopCompositionTimer(SourceEditor source){
        var field=typeof(SourceEditor).GetField("imeTimer",Members);
        var timer=field==null?null:field.GetValue(source) as DispatcherTimer;
        if(timer!=null)timer.Stop();
    }
    // One typed key whose handler edits the document inside its own update (the live preview
    // writes a marker run and the text together). typedKeyArmed is the marker the editor arms for
    // a command-shaped typed edit, so the group is recorded as typing exactly like real input.
    static void TypedChanges(SourceEditor source,Action change){
        source.SimulateTypedKey();
        using(source.Document.RunUpdate()){change();}
    }
    // An IME result: the counted composition text is delivered through the app's TextInput path,
    // one character at a time. wordUnit only describes the language to the app (the history rule
    // no longer differs by language); it keeps the probe seam realistic.
    static void CommitImeText(SourceEditor source,string text,bool wordUnit){
        Func<bool> previous=source.NonKoreanImeLayout;
        source.NonKoreanImeLayout=wordUnit?WordImeTrue:WordImeFalse;
        try{
            source.SimulateImeComposition();
            source.SimulateImeCommit(text.Length);
            for(int i=0;i<text.Length;i++)source.TextArea.PerformTextInput(text.Substring(i,1));
        }finally{
            source.ResetCompositionSimulation();
            source.NonKoreanImeLayout=previous;
        }
    }

    // ---------------------------------------------------------------- step watchdog
    static void BeginStep(MainWindow window){
        stepInFlight=true;
        Interlocked.Exchange(ref uiBeatTimestamp,Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref stepStartTimestamp,Stopwatch.GetTimestamp());
        watchdogSnapshot=SnapshotLine(window);
        watchdogPlace="action";watchdogDetail="";
    }
    static void EndStep(){
        Interlocked.Exchange(ref stepStartTimestamp,0);
        Interlocked.Exchange(ref uiBeatTimestamp,Stopwatch.GetTimestamp());
        stepInFlight=false;
    }
    static string SnapshotLine(MainWindow window){
        try{
            SourceEditor source=Source(window);
            if(source==null||source.Document==null)return "(no source)";
            TextDocument document=source.Document;int caret=source.CaretIndex;string line="";
            if(caret>=0&&caret<=document.TextLength){
                DocumentLine documentLine=document.GetLineByOffset(caret);
                line=document.GetText(documentLine);
                if(line.Length>160)line=line.Substring(0,160)+"...";
            }
            return "caret="+caret+", sel="+source.SelectionStart+"+"+source.SelectionLength+", docLen="+document.TextLength+", line=["+OneLine(line)+"]";
        }catch(Exception ex){return "(snapshot failed: "+OneLine(ex.GetType().Name+": "+ex.Message)+")";}
    }
    static void StartWatchdog(){
        watchdogStop=false;watchdogFired=0;
        Interlocked.Exchange(ref uiBeatTimestamp,Stopwatch.GetTimestamp());
        watchdogThread=new Thread(WatchdogLoop);
        watchdogThread.IsBackground=true;
        watchdogThread.Name="history-stress-watchdog";
        watchdogThread.Start();
    }
    static void WatchdogLoop(){
        int tick=0;
        while(!watchdogStop){
            Thread.Sleep(250);
            tick++;
            if((tick%8)==0)WriteWatchdogHeartbeat();
            long now=Stopwatch.GetTimestamp();
            long privateBytes=PrivateBytes();
            string reason=null;long elapsed=-1;bool memoryLimit=false;
            // Memory first: a fast allocation loop takes the VM down before the step clock fires.
            if(privateBytes>=MemoryWatchdogBytes){
                memoryLimit=true;
                reason="private bytes reached "+Megabytes(privateBytes)+" MB (limit "+Megabytes(MemoryWatchdogBytes)+" MB)";
            }
            if(reason==null&&stepInFlight){
                long started=Interlocked.Read(ref stepStartTimestamp);
                if(started!=0){
                    elapsed=(now-started)*1000L/Stopwatch.Frequency;
                    if(elapsed>=StepWatchdogMilliseconds)reason="one step stayed on the UI thread for "+elapsed+" ms";
                }
            }
            if(reason==null){
                long beat=Interlocked.Read(ref uiBeatTimestamp);
                long beatAge=beat==0?-1:(now-beat)*1000L/Stopwatch.Frequency;
                if(beatAge>=UiStallMilliseconds)reason="the UI thread moved no probe-level checkpoint for "+beatAge+" ms (step="+currentStep+", action="+currentAction+")";
            }
            if(reason==null)continue;
            if(Interlocked.CompareExchange(ref watchdogFired,1,0)!=0)continue;
            try{HangDump(reason,elapsed,memoryLimit,privateBytes);}catch(Exception){}
            // The dump is already on disk. Environment.Exit carries the code the run chain records;
            // if its managed shutdown blocks on the UI thread (the b56c6 scenario), a guard thread
            // kills the process anyway, so a stuck probe can never hold the VM for hours.
            StartExitGuard();
            Environment.Exit(DumpExitCode);
        }
    }
    // Proof of life while a step is stuck: if this file stops changing too, the watchdog itself
    // died (or the whole runtime froze), which is different evidence from a UI-thread loop.
    static void WriteWatchdogHeartbeat(){
        try{
            long started=Interlocked.Read(ref stepStartTimestamp);
            long elapsed=started==0?-1:(Stopwatch.GetTimestamp()-started)*1000L/Stopwatch.Frequency;
            long beat=Interlocked.Read(ref uiBeatTimestamp);
            long beatAge=beat==0?-1:(Stopwatch.GetTimestamp()-beat)*1000L/Stopwatch.Frequency;
            string text="watchdog alive, seed="+currentSeed+", step="+currentStep+", action="+currentAction+", inFlight="+stepInFlight+", stepElapsed="+elapsed+", beatAge="+beatAge+", privateMB="+Megabytes(PrivateBytes())+", place="+watchdogPlace+"\n";
            File.WriteAllText(Path.Combine(outRoot,"history-stress-probe-watchdog.txt"),text,new UTF8Encoding(false));
        }catch(Exception){}
    }
    static long PrivateBytes(){
        try{using(var process=Process.GetCurrentProcess())return process.PrivateMemorySize64;}catch(Exception){return -1;}
    }
    static string Megabytes(long bytes){
        return bytes<0?"?":(bytes/(1024L*1024L)).ToString(CultureInfo.InvariantCulture);
    }
    static string MemoryText(){
        try{
            using(var process=Process.GetCurrentProcess()){
                process.Refresh();
                return "private="+Megabytes(process.PrivateMemorySize64)+" MB, working set="+Megabytes(process.WorkingSet64)+" MB, peak working set="+Megabytes(process.PeakWorkingSet64)+" MB, virtual="+Megabytes(process.VirtualMemorySize64)+" MB";
            }
        }catch(Exception ex){return "(memory read failed: "+OneLine(ex.GetType().Name+": "+ex.Message)+")";}
    }
    // A second stop that does not depend on the managed shutdown path the dump's exit uses.
    static void StartExitGuard(){
        try{
            var guard=new Thread(delegate(){
                try{Thread.Sleep(4000);}catch(Exception){}
                try{using(var process=Process.GetCurrentProcess())process.Kill();}catch(Exception){}
            });
            guard.IsBackground=true;
            guard.Name="history-stress-exit-guard";
            guard.Start();
        }catch(Exception){}
    }
    // A step that never returns is the only state the main thread cannot report, so the dump is
    // written by the watchdog and ends the process: no more waiting for the caller's time cap.
    static void HangDump(string reason,long elapsed,bool memoryLimit,long privateBytes){
        var text=new StringBuilder();
        text.Append(memoryLimit?"# history-stress-probe MEM\n":"# history-stress-probe HANG\n");
        text.Append("# The run stopped here instead of hanging: ").Append(reason).Append(".\n");
        text.Append("seed=").Append(currentSeed).Append(" step=").Append(currentStep).Append(" action=").Append(currentAction).Append('\n');
        text.Append("elapsed in step=").Append(elapsed).Append(" ms\n");
        text.Append("private bytes at dump=").Append(Megabytes(privateBytes)).Append(" MB\n");
        text.Append("process memory: ").Append(MemoryText()).Append('\n');
        text.Append("last checkpoint=").Append(watchdogPlace).Append('\n');
        text.Append("last walk position=").Append(watchdogDetail).Append('\n');
        text.Append("snapshot at step start/last action=").Append(watchdogSnapshot).Append('\n');
        text.Append("recent actions: ").Append(LastSteps(40)).Append('\n');
        try{AppendFailureSummary(text);}catch(Exception){text.Append("(failure summary unavailable)\n");}
        AppendLoopEvidence(text);
        text.Append("\n## process CPU over 500 ms (one spinning core is about 500)\n");
        try{text.Append(CpuSampleMilliseconds().ToString("0",CultureInfo.InvariantCulture)).Append(" ms\n");}catch(Exception){text.Append("(cpu sample failed)\n");}
        string head=text.ToString();
        WriteDumpFiles(memoryLimit,head);
        // The stack walk is the only risky call (it stops the UI thread internally), so it runs on
        // its own thread with a bound: the head above is already on disk if it never returns.
        string stacks=CaptureStacks(8000);
        WriteDumpFiles(memoryLimit,head+stacks);
    }
    // Diagnostics that separate "blocked" from "a dispatcher operation keeps re-posting": the
    // live product flags at the stall, a two-second window of posted/started operations with
    // their sites, repeated samples of the UI thread's innermost frames, and the dispatcher
    // queue itself. All of it is read-only and bounded; the process is killed right after.
    static SourceEditor hookedSource;
    static void HookCounters(MainWindow window){
        SourceEditor source=Source(window);
        if(source!=null&&!Object.ReferenceEquals(source,hookedSource)){
            // A tab switch replaces MainWindow.source (Workspace.cs), and the cap case builds a
            // new window: the counters must follow the editor the stall runs on.
            if(hookedSource!=null&&layoutCounter!=null)hookedSource.LayoutUpdated-=layoutCounter;
            if(hookedSource!=null&&scrollCounter!=null){try{hookedSource.RemoveHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,scrollCounter);}catch(Exception){}}
            hookedSource=source;
            layoutCounter=delegate{Interlocked.Increment(ref layoutPasses);};
            source.LayoutUpdated+=layoutCounter;
            scrollCounter=null;
        }
        if(!renderCounterHooked){
            renderCounterHooked=true;
            System.Windows.Media.CompositionTarget.Rendering+=CountRenderFrame;
        }
        if(source!=null&&scrollCounter==null){
            scrollCounter=delegate(object sender,ScrollChangedEventArgs args){
                Interlocked.Increment(ref scrollChanges);
                scrollExtent=args.ExtentHeight;scrollExtentChange=args.ExtentHeightChange;
                scrollViewport=args.ViewportHeight;scrollViewportChange=args.ViewportHeightChange;
                scrollOffset=args.VerticalOffset;scrollOffsetChange=args.VerticalChange;
                try{
                    SourceEditor editor=sender as SourceEditor;
                    if(editor!=null&&editor.TextArea!=null&&editor.TextArea.TextView!=null){
                        scrollMarginTop=editor.TextArea.TextView.Margin.Top;
                        scrollMarginBottom=editor.TextArea.TextView.Margin.Bottom;
                    }
                }catch(Exception){}
                try{
                    lock(scrollSync){
                        if(scrollLog.Count>=8)scrollLog.RemoveAt(0);
                        scrollLog.Add("handled="+args.Handled+" extent="+args.ExtentHeight.ToString("0.##",CultureInfo.InvariantCulture)+" ch="+args.ExtentHeightChange.ToString("0.##",CultureInfo.InvariantCulture)+" viewport="+args.ViewportHeight.ToString("0.##",CultureInfo.InvariantCulture)+" ch="+args.ViewportHeightChange.ToString("0.##",CultureInfo.InvariantCulture)+" offset="+args.VerticalOffset.ToString("0.##",CultureInfo.InvariantCulture)+" ch="+args.VerticalChange.ToString("0.##",CultureInfo.InvariantCulture)+" margin="+scrollMarginTop.ToString("0.##",CultureInfo.InvariantCulture)+"/"+scrollMarginBottom.ToString("0.##",CultureInfo.InvariantCulture));
                    }
                }catch(Exception){}
            };
            // handledEventsToo: the ScrollViewer raises ScrollChanged from its LayoutUpdated pass and
            // the product handler may already have marked it handled; the counter must still see it.
            source.AddHandler(System.Windows.Controls.ScrollViewer.ScrollChangedEvent,scrollCounter,true);
        }
        if(stateTimer==null){
            stateTimer=new DispatcherTimer(DispatcherPriority.Normal);
            stateTimer.Interval=TimeSpan.FromMilliseconds(5);
            stateTimer.Tick+=SampleEditorState;
            stateTimer.Start();
        }
    }
    static void SampleEditorState(object sender,EventArgs e){
        try{
            MainWindow window=watchdogWindow;
            if(window==null)return;
            SourceEditor editor=Source(window);
            if(editor==null||editor.TextArea==null)return;
            var view=editor.TextArea.TextView;
            if(view==null)return;
            var caret=editor.TextArea.Caret;
            var selection=editor.TextArea.Selection;
            string line=(++stateSequence).ToString(CultureInfo.InvariantCulture)
                +" offset="+editor.VerticalOffset.ToString("0.##",CultureInfo.InvariantCulture)
                +" extent="+editor.ExtentHeight.ToString("0.##",CultureInfo.InvariantCulture)
                +" viewport="+editor.ViewportHeight.ToString("0.##",CultureInfo.InvariantCulture)
                +" margin="+view.Margin.Top.ToString("0.##",CultureInfo.InvariantCulture)+"/"+view.Margin.Bottom.ToString("0.##",CultureInfo.InvariantCulture)
                +" lines="+(view.VisualLinesValid?view.VisualLines.Count.ToString(CultureInfo.InvariantCulture):"-")
                +" measure="+view.IsMeasureValid+" arrange="+view.IsArrangeValid
                +" caret="+caret.Offset+" sel="+(selection.IsEmpty?"empty":(selection.SurroundingSegment.Offset+"+"+selection.SurroundingSegment.Length))
                +" doc="+(view.Document==null?-1:view.Document.TextLength);
            lock(sampleSync){
                if(stateSamples.Count>=240)stateSamples.RemoveAt(0);
                stateSamples.Add(line);
            }
        }catch(Exception ex){
            lock(sampleSync){
                if(stateSamples.Count>=240)stateSamples.RemoveAt(0);
                stateSamples.Add("(sample failed: "+ex.GetType().Name+": "+OneLine(ex.Message)+")");
            }
        }
    }
    static void CountRenderFrame(object sender,EventArgs e){Interlocked.Increment(ref renderFrames);}
    static void AppendLoopEvidence(StringBuilder text){
        text.Append("\n## layout and frame rate over 1 s\n");
        try{
            Interlocked.Exchange(ref layoutPasses,0);Interlocked.Exchange(ref renderFrames,0);Interlocked.Exchange(ref scrollChanges,0);
            Thread.Sleep(1000);
            text.Append("layout passes=").Append(Interlocked.CompareExchange(ref layoutPasses,0,0));
            text.Append(", rendered frames=").Append(Interlocked.CompareExchange(ref renderFrames,0,0));
            text.Append(", scroll changes=").Append(Interlocked.CompareExchange(ref scrollChanges,0,0)).Append('\n');
            text.Append("last scroll: extent=").Append(scrollExtent.ToString("0.##",CultureInfo.InvariantCulture));
            text.Append(" change=").Append(scrollExtentChange.ToString("0.##",CultureInfo.InvariantCulture));
            text.Append(", viewport=").Append(scrollViewport.ToString("0.##",CultureInfo.InvariantCulture));
            text.Append(" change=").Append(scrollViewportChange.ToString("0.##",CultureInfo.InvariantCulture));
            text.Append(", offset=").Append(scrollOffset.ToString("0.##",CultureInfo.InvariantCulture));
            text.Append(" change=").Append(scrollOffsetChange.ToString("0.##",CultureInfo.InvariantCulture));
            text.Append(", view margin top=").Append(scrollMarginTop.ToString("0.##",CultureInfo.InvariantCulture));
            text.Append(" bottom=").Append(scrollMarginBottom.ToString("0.##",CultureInfo.InvariantCulture)).Append('\n');
            lock(scrollSync){
                for(int i=0;i<scrollLog.Count;i++)text.Append("  scroll[").Append(i).Append("] ").Append(scrollLog[i]).Append('\n');
            }
            text.Append("\n## editor state samples (UI thread, Normal timer, 5 ms; last 30)\n");
            lock(sampleSync){
                int first=Math.Max(0,stateSamples.Count-30);
                for(int i=first;i<stateSamples.Count;i++)text.Append("  ").Append(stateSamples[i]).Append('\n');
            }
        }catch(Exception ex){text.Append("(rate sample failed: ").Append(OneLine(ex.GetType().Name+": "+ex.Message)).Append(")\n");}
        text.Append("\n## live state at dump time\n");
        try{text.Append(LiveState()).Append('\n');}catch(Exception ex){text.Append("(live state failed: ").Append(OneLine(ex.GetType().Name+": "+ex.Message)).Append(")\n");}
        text.Append("\n## dispatcher post spy\n");
        try{
            PostSpy.ClearRecent();
            Thread.Sleep(2000);
            text.Append(PostSpy.Report(14));
        }catch(Exception ex){text.Append("(post spy failed: ").Append(OneLine(ex.GetType().Name+": "+ex.Message)).Append(")\n");}
        text.Append("\n## UI thread top frames, 12 samples 100 ms apart\n");
        try{text.Append(SampleStackTops(12,100));}catch(Exception ex){text.Append("(stack sampling failed: ").Append(OneLine(ex.GetType().Name+": "+ex.Message)).Append(")\n");}
        text.Append("\n## dispatcher queue samples, 8 samples 100 ms apart\n");
        try{text.Append(SampleDispatcherQueue(8,100));}catch(Exception ex){text.Append("(queue sampling failed: ").Append(OneLine(ex.GetType().Name+": "+ex.Message)).Append(")\n");}
    }
    // The product state a self-re-posting operation would show: which coalesced work handles
    // are still posted, whether the session is Busy (the caret guard re-queues itself while it
    // is), the composition flags that decide whether the IME path keeps posting, and timers.
    static string LiveState(){
        var text=new StringBuilder();
        MainWindow window=watchdogWindow;
        if(window==null){text.Append("window=none");return text.ToString();}
        text.Append("documentOperationBusy=").Append(ReadBool(window,"documentOperationBusy"));
        text.Append(", editing=").Append(ReadBool(window,"editing"));
        text.Append(", plain=").Append(ReadBool(window,"PlainText"));
        text.Append(", attached=").Append(ReadBool(window,"LivePreviewAttached"));
        SourceEditor source=Field<SourceEditor>(window,"source");
        object session=ReadMember(window,"livePreview");
        text.Append(", session=").Append(session==null?"none":"yes");
        if(session!=null){
            text.Append("\n  disposed=").Append(MemberText(session,"disposed"));
            // Why LivePreviewAttached says no: the session's own three references are what the
            // host compares. A stale session keeps its render/layout hooks alive.
            object sessionEditor=ReadMember(session,"Editor");
            object sessionDocument=ReadMember(session,"Document");
            object sessionView=ReadMember(session,"View");
            object sourceView=null;
            object sourceArea=ReadMember(source,"TextArea");
            if(sourceArea!=null)sourceView=ReadMember(sourceArea,"TextView");
            object sourceDocument=ReadMember(source,"Document");
            text.Append(", editorIsSource=").Append(Object.ReferenceEquals(sessionEditor,source));
            text.Append(", documentIsSourceDocument=").Append(Object.ReferenceEquals(sessionDocument,sourceDocument));
            text.Append(", viewIsSourceView=").Append(Object.ReferenceEquals(sessionView,sourceView));
            text.Append(", sessionDocLen=").Append(sessionDocument==null?"null":MemberText(sessionDocument,"TextLength"));
            text.Append("\n  view: visualLinesValid=").Append(MemberText(sessionView,"VisualLinesValid"));
            text.Append(", measureValid=").Append(MemberText(sessionView,"IsMeasureValid"));
            text.Append(", arrangeValid=").Append(MemberText(sessionView,"IsArrangeValid"));
            object layer=ReadMember(session,"tableCaretLayer");
            object layers=ReadMember(sessionView,"Layers");
            bool layerListed=false;
            if(layer!=null&&layers!=null){try{layerListed=((IList)layers).IndexOf(layer)>=0;}catch(Exception){}}
            text.Append(", tableCaretLayerListed=").Append(layerListed);
            object options=ReadMember(session,"Options");
            text.Append(", Options.Busy=").Append(options==null?"(no options)":MemberText(options,"Busy"));
            text.Append(", frozen=").Append(MemberText(session,"frozen"));
            text.Append(", compositionLive=").Append(MemberText(session,"compositionLive"));
            text.Append(", hideMarkers=").Append(options==null?"?":MemberText(options,"HideMarkers"));
            text.Append("\n  caretGuardWork=").Append(WorkText(session,"caretGuardWork"));
            text.Append(", compositionWork=").Append(WorkText(session,"compositionWork"));
            text.Append(", compositionReappl=").Append(WorkText(session,"compositionReapply"));
            text.Append(", layoutWork=").Append(WorkText(session,"layoutWork"));
            text.Append(", tableWork=").Append(WorkText(session,"tableWork"));
            text.Append(", widthRedraw=").Append(WorkText(session,"widthRedraw"));
            text.Append(", formatterRedraw=").Append(WorkText(session,"formatterRedraw"));
            text.Append(", referenceSync=").Append(WorkText(session,"referenceSync"));
            text.Append("\n  tableCaretWork=").Append(WorkText(session,"tableCaretWork"));
            text.Append(", tableCaretReassert=").Append(WorkText(session,"tableCaretReassertWork"));
            text.Append(", tableCaretLayoutOnce=").Append(MemberText(session,"tableCaretLayoutOnce"));
            text.Append(", tableCaretRect=").Append(MemberText(session,"tableCaretRect"));
            text.Append(", tableIdle=").Append(TimerText(session,"tableIdle"));
            text.Append(", tablePending=");
            object tables=ReadMember(session,"tables");
            int pending=0;
            if(tables!=null){try{foreach(object table in (IEnumerable)tables){if(ReadBool(table,"PendingRemeasure"))pending++;}}catch(Exception){pending=-1;}}
            text.Append(pending);
        }
        if(source!=null){
            text.Append("\n  source: imeBusy=").Append(source.ImeBusy);
            text.Append(", composing=").Append(MemberText(source,"imeComposing"));
            text.Append(", fontScheduled=").Append(MemberText(source,"imeFontScheduled"));
            text.Append(", imeSource=").Append(ReadMember(source,"imeSource")==null?"null":"hwnd");
            text.Append(", focused=").Append(MemberText(source,"IsKeyboardFocusWithin"));
            text.Append(", readOnly=").Append(MemberText(source,"IsReadOnly"));
            try{
                text.Append(", caret=").Append(source.CaretIndex);
                text.Append(", sel=").Append(source.SelectionStart).Append('+').Append(source.SelectionLength);
                text.Append(", docLen=").Append(source.Document==null?-1:source.Document.TextLength);
            }catch(Exception ex){text.Append(", caret=(read failed: ").Append(ex.GetType().Name).Append(')');}
        }
        return text.ToString();
    }
    static object ReadMember(object target,string name){
        if(target==null)return null;
        try{
            FieldInfo field=target.GetType().GetField(name,Members);
            if(field!=null)return field.GetValue(target);
            PropertyInfo property=target.GetType().GetProperty(name,Members);
            if(property!=null)return property.GetValue(target,null);
        }catch(Exception){}
        return null;
    }
    static bool ReadBool(object target,string name){object value=ReadMember(target,name);return value is bool&&(bool)value;}
    static string MemberText(object target,string name){
        object value=ReadMember(target,name);
        if(value==null)return "null";
        try{return value.ToString();}catch(Exception){return "?";}
    }
    static string WorkText(object target,string name){
        object value=ReadMember(target,name);
        if(value==null)return "none";
        DispatcherOperation operation=value as DispatcherOperation;
        if(operation==null)return "set";
        try{return operation.Status.ToString()+"/"+operation.Priority.ToString();}catch(Exception){return "set";}
    }
    static string TimerText(object target,string name){
        object value=ReadMember(target,name);
        if(value==null)return "null";
        DispatcherTimer timer=value as DispatcherTimer;
        if(timer==null)return "set";
        try{return (timer.IsEnabled?"running/":"stopped/")+timer.Interval.TotalMilliseconds.ToString("0",CultureInfo.InvariantCulture);}catch(Exception){return "set";}
    }
    static string SampleStackTops(int samples,int intervalMilliseconds){
        var tally=new Dictionary<string,int>();
        int failed=0;
        for(int i=0;i<samples;i++){
            string top=null;bool suspended=false;
            try{
                if(uiThread!=null&&!Object.ReferenceEquals(uiThread,Thread.CurrentThread)){uiThread.Suspend();suspended=true;}
                var trace=new StackTrace(uiThread,false);
                StackFrame[] frames=trace.GetFrames();
                var built=new StringBuilder();
                int limit=frames==null?0:Math.Min(frames.Length,4);
                for(int f=0;f<limit;f++){
                    MethodBase method=frames[f].GetMethod();
                    if(method==null)continue;
                    if(built.Length>0)built.Append(" <- ");
                    string typeName=method.DeclaringType==null?"?":method.DeclaringType.FullName;
                    built.Append(typeName).Append('.').Append(method.Name);
                }
                top=built.Length==0?"(empty)" : built.ToString();
            }catch(Exception){failed++;}
            finally{if(suspended)try{uiThread.Resume();}catch(Exception){}}
            if(top!=null)Bump(tally,top);
            if(i+1<samples)Thread.Sleep(intervalMilliseconds);
        }
        var text=new StringBuilder();
        text.Append("samples=").Append(samples).Append(", failed=").Append(failed).Append('\n');
        AppendTally(text,tally);
        return text.ToString();
    }
    static string SampleDispatcherQueue(int samples,int intervalMilliseconds){
        var tally=new Dictionary<string,int>();
        int failed=0;
        for(int i=0;i<samples;i++){
            try{SampleQueueOnce(tally);}catch(Exception){failed++;}
            if(i+1<samples)Thread.Sleep(intervalMilliseconds);
        }
        var text=new StringBuilder();
        text.Append("samples=").Append(samples).Append(", failed=").Append(failed).Append('\n');
        AppendTally(text,tally);
        return text.ToString();
    }
    static void SampleQueueOnce(Dictionary<string,int> tally){
        Dispatcher dispatcher=Dispatcher.FromThread(uiThread);
        if(dispatcher==null)return;
        var visited=new HashSet<object>(ReferenceComparer.Instance);
        WalkQueue(dispatcher,0,visited,tally);
    }
    static void WalkQueue(object value,int depth,HashSet<object> visited,Dictionary<string,int> tally){
        if(value==null||depth>4||value is string)return;
        DispatcherOperation operation=value as DispatcherOperation;
        if(operation!=null){Bump(tally,PostSpy.KeyOf(operation));return;}
        if(!value.GetType().IsValueType){
            if(visited.Contains(value))return;
            visited.Add(value);
        }
        IEnumerable sequence=value as IEnumerable;
        if(sequence!=null){
            IEnumerator enumerator;
            try{enumerator=sequence.GetEnumerator();}catch(Exception){return;}
            while(true){
                bool more;
                try{more=enumerator.MoveNext();}catch(Exception){return;}
                if(!more)break;
                WalkQueue(enumerator.Current,depth+1,visited,tally);
            }
            return;
        }
        if(depth>=3)return;
        foreach(FieldInfo field in value.GetType().GetFields(Members)){
            string name=field.Name;
            if(name.IndexOf("queue",StringComparison.OrdinalIgnoreCase)<0&&field.FieldType.Name.IndexOf("Queue",StringComparison.OrdinalIgnoreCase)<0)continue;
            object item;
            try{item=field.GetValue(field.IsStatic?null:value);}catch(Exception){continue;}
            WalkQueue(item,depth+1,visited,tally);
        }
    }
    static void AppendTally(StringBuilder text,Dictionary<string,int> tally){
        if(tally.Count==0){text.Append("- (none)\n");return;}
        var keys=new List<string>(tally.Keys);
        keys.Sort(delegate(string a,string b){int by=tally[b].CompareTo(tally[a]);return by!=0?by:String.Compare(a,b,StringComparison.Ordinal);});
        int limit=Math.Min(keys.Count,20);
        for(int i=0;i<limit;i++)text.Append("- ").Append(tally[keys[i]]).Append("  ").Append(keys[i]).Append('\n');
    }
    static void WriteDumpFiles(bool memoryLimit,string body){
        try{
            File.WriteAllText(Path.Combine(outRoot,memoryLimit?"history-stress-probe-MEM.txt":"history-stress-probe-HANG.txt"),body,new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outRoot,"history-stress-probe-FAIL.txt"),body,new UTF8Encoding(false));
        }catch(Exception){}
    }
    static string CaptureStacks(int timeoutMilliseconds){
        var holder=new string[1];
        var worker=new Thread(delegate(){holder[0]=StackText();});
        worker.IsBackground=true;
        worker.Name="history-stress-stack";
        try{worker.Start();}catch(Exception ex){return "\n## UI thread managed stack\n(start failed: "+OneLine(ex.GetType().Name+": "+ex.Message)+")\n";}
        if(!worker.Join(timeoutMilliseconds))return "\n## UI thread managed stack\n(the stack capture did not return within "+timeoutMilliseconds+" ms)\n";
        return holder[0]==null?"\n## UI thread managed stack\n(no text)\n":holder[0];
    }
    static string StackText(){
        var text=new StringBuilder();
        text.Append("\n## UI thread managed stack, capture 1\n");
        AppendThreadStack(text,uiThread);
        Thread.Sleep(300);
        text.Append("\n## UI thread managed stack, capture 2 (300 ms later)\n");
        AppendThreadStack(text,uiThread);
        return text.ToString();
    }
    static double CpuSampleMilliseconds(){
        Process process=Process.GetCurrentProcess();
        TimeSpan first=process.TotalProcessorTime;Thread.Sleep(500);process.Refresh();
        return (process.TotalProcessorTime-first).TotalMilliseconds;
    }
    static void AppendFailureSummary(StringBuilder text){
        text.Append("checks=").Append(checks).Append(" pass=").Append(passed).Append(" fail=").Append(failed).Append('\n');
        foreach(FailureInfo failure in failureList){
            text.Append("- ").Append(failure.Invariant).Append(" @ seed ").Append(failure.Seed).Append(" step ").Append(failure.Step).Append(' ').Append(failure.Action).Append('\n');
            text.Append("  detail: ").Append(failure.Detail).Append('\n');
            text.Append("  expected: ").Append(failure.Expected).Append('\n');
            text.Append("  actual: ").Append(failure.Actual).Append('\n');
        }
    }
    static void AppendThreadStack(StringBuilder text,Thread thread){
        if(thread==null){text.Append("(no UI thread reference)\n");return;}
        bool suspended=false;
        try{
            // StackTrace(Thread) demands the target be suspended; the process is killed right after
            // the dump, so leaving it suspended would not matter if Resume itself failed.
            if(!Object.ReferenceEquals(thread,Thread.CurrentThread)){thread.Suspend();suspended=true;}
            var stackTrace=new StackTrace(thread,false);
            StackFrame[] frames=stackTrace.GetFrames();
            if(frames==null||frames.Length==0){text.Append("(the runtime returned an empty stack)\n");return;}
            int limit=Math.Min(frames.Length,80);
            for(int i=0;i<limit;i++){
                MethodBase method=frames[i].GetMethod();
                if(method==null){text.Append("  <native frame>\n");continue;}
                string typeName=method.DeclaringType==null?"?":method.DeclaringType.FullName;
                text.Append("  ").Append(typeName).Append('.').Append(method.Name).Append("  [").Append(frames[i].GetFileName()==null?"":frames[i].GetFileName()).Append(':').Append(frames[i].GetFileLineNumber()).Append("]\n");
            }
        }catch(Exception ex){text.Append("(stack capture failed: ").Append(OneLine(ex.GetType().Name+": "+ex.Message)).Append(")\n");}
        finally{if(suspended)try{thread.Resume();}catch(Exception){}}
    }

    // ---------------------------------------------------------------- dispatcher post spy
    // Diagnostic: counts every dispatcher operation the process posts and starts, keyed by the
    // priority and the method that will run, so the watchdog can name the operation that keeps
    // re-posting itself. Attached to WPF's internal DispatcherHooks; a failed install is only
    // recorded. The probe's own posts are counted like any other, which is the point.
    internal static class PostSpy {
        static readonly object sync=new object();
        static bool installed;
        static object hooks;
        static Delegate postedHandler,startedHandler;
        static string installNote="(not installed)";
        static readonly Dictionary<string,int> posted=new Dictionary<string,int>();
        static readonly Dictionary<string,int> started=new Dictionary<string,int>();
        static readonly Dictionary<string,int> recentPosted=new Dictionary<string,int>();
        static readonly Dictionary<string,int> recentStarted=new Dictionary<string,int>();
        static readonly Dictionary<string,string> sites=new Dictionary<string,string>();
        static readonly Dictionary<string,string> recentSites=new Dictionary<string,string>();
        static readonly Dictionary<string,int> siteCounters=new Dictionary<string,int>();
        static int postCount,startCount;
        internal static void Install(){
            if(installed)return;
            try{
                hooks=FindHooks();
                if(hooks==null){installNote="(no DispatcherHooks object reachable)";return;}
                postedHandler=Attach(hooks,"OperationPosted","OnPostedHook","OnPostedOperation");
                startedHandler=Attach(hooks,"OperationStarted","OnStartedHook","OnStartedOperation");
                installed=postedHandler!=null||startedHandler!=null;
                installNote="hooks="+hooks.GetType().FullName+", posted="+(postedHandler!=null)+", started="+(startedHandler!=null);
            }catch(Exception ex){installNote="(install failed: "+ex.GetType().Name+": "+ex.Message+")";}
        }
        internal static void ClearRecent(){
            lock(sync){recentPosted.Clear();recentStarted.Clear();}
        }
        internal static string Report(int top){
            var text=new StringBuilder();
            lock(sync){
                text.Append("install: ").Append(installNote).Append('\n');
                text.Append("totals: posted=").Append(postCount).Append(", started=").Append(startCount).Append('\n');
                text.Append("recent window: posted=").Append(Sum(recentPosted)).Append(", started=").Append(Sum(recentStarted)).Append('\n');
                text.Append("posted (recent window, highest first):\n");AppendTop(text,recentPosted,top);
                text.Append("started (recent window, highest first):\n");AppendTop(text,recentStarted,top);
                text.Append("posted (whole run, highest first):\n");AppendTop(text,posted,top);
                text.Append("\n### posting sites, refreshed every 200 posts (the loop's own callers)\n");
                var hot=new List<string>(recentPosted.Keys);
                hot.Sort(delegate(string a,string b){int by=recentPosted[b].CompareTo(recentPosted[a]);return by!=0?by:String.Compare(a,b,StringComparison.Ordinal);});
                int shown=0;
                foreach(string key in hot){
                    string site;
                    if(!recentSites.TryGetValue(key,out site))continue;
                    if(shown++>=14)break;
                    text.Append("- ").Append(key).Append(" = ").Append(site).Append('\n');
                }
                text.Append("\n### posting sites (first sample per key)\n");
                shown=0;
                foreach(KeyValuePair<string,string> pair in sites){
                    if(shown++>=40)break;
                    text.Append("- ").Append(pair.Key).Append(" = ").Append(pair.Value).Append('\n');
                }
            }
            return text.ToString();
        }
        static int Sum(Dictionary<string,int> tally){int total=0;foreach(int value in tally.Values)total+=value;return total;}
        static void AppendTop(StringBuilder text,Dictionary<string,int> tally,int top){
            var keys=new List<string>(tally.Keys);
            keys.Sort(delegate(string a,string b){int by=tally[b].CompareTo(tally[a]);return by!=0?by:String.Compare(a,b,StringComparison.Ordinal);});
            int limit=Math.Min(keys.Count,top);
            if(limit==0){text.Append("- (none)\n");return;}
            for(int i=0;i<limit;i++)text.Append("- ").Append(tally[keys[i]]).Append("  ").Append(keys[i]).Append('\n');
        }
        static object FindHooks(){
            Type type=typeof(Dispatcher);
            PropertyInfo property=type.GetProperty("Hooks",Members);
            if(property!=null){
                try{
                    MethodInfo getter=property.GetGetMethod(true);
                    object owner=getter!=null&&getter.IsStatic?null:Dispatcher.CurrentDispatcher;
                    object value=property.GetValue(owner,null);
                    if(value!=null)return value;
                }catch(Exception){}
            }
            foreach(FieldInfo field in type.GetFields(Members)){
                if(field.FieldType.Name!="DispatcherHooks")continue;
                try{object value=field.GetValue(field.IsStatic?null:Dispatcher.CurrentDispatcher);if(value!=null)return value;}catch(Exception){}
            }
            return null;
        }
        static Delegate Attach(object target,string eventName,string hookHandlerName,string operationHandlerName){
            try{
                MethodInfo hookHandler=typeof(PostSpy).GetMethod(hookHandlerName,BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
                MethodInfo operationHandler=typeof(PostSpy).GetMethod(operationHandlerName,BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
                foreach(EventInfo candidate in target.GetType().GetEvents(Members)){
                    if(candidate.Name!=eventName)continue;
                    Delegate handler=CreateHandler(candidate.EventHandlerType,hookHandler,operationHandler);
                    if(handler==null)continue;
                    candidate.AddEventHandler(target,handler);
                    return handler;
                }
                foreach(FieldInfo field in target.GetType().GetFields(Members)){
                    if(field.Name.IndexOf(eventName,StringComparison.OrdinalIgnoreCase)<0)continue;
                    if(!typeof(Delegate).IsAssignableFrom(field.FieldType))continue;
                    Delegate handler=CreateHandler(field.FieldType,hookHandler,operationHandler);
                    if(handler==null)continue;
                    field.SetValue(target,Delegate.Combine((Delegate)field.GetValue(target),handler));
                    return handler;
                }
            }catch(Exception){}
            return null;
        }
        // DispatcherHookEventHandler takes (object, DispatcherHookEventArgs); a probe-local variant
        // may take the DispatcherOperation directly. Try both so the spy does not depend on which
        // shape the installed WindowsBase carries.
        static Delegate CreateHandler(Type handlerType,MethodInfo hookHandler,MethodInfo operationHandler){
            if(hookHandler!=null){try{return Delegate.CreateDelegate(handlerType,hookHandler);}catch(Exception){}}
            if(operationHandler!=null){try{return Delegate.CreateDelegate(handlerType,operationHandler);}catch(Exception){}}
            return null;
        }
        static void OnPostedHook(object sender,DispatcherHookEventArgs e){OnPosted(e==null?null:e.Operation);}
        static void OnStartedHook(object sender,DispatcherHookEventArgs e){OnStarted(e==null?null:e.Operation);}
        static void OnPostedOperation(DispatcherOperation operation){OnPosted(operation);}
        static void OnStartedOperation(DispatcherOperation operation){OnStarted(operation);}
        static void OnPosted(DispatcherOperation operation){
            try{
                string key=KeyOf(operation);
                lock(sync){
                    postCount++;Bump(posted,key);Bump(recentPosted,key);
                    if(sites.Count<64&&!sites.ContainsKey(key))sites[key]=SiteStack();
                    int seen;
                    siteCounters.TryGetValue(key,out seen);
                    if((seen%200)==0)recentSites[key]=SiteStack();
                    siteCounters[key]=seen+1;
                }
            }catch(Exception){}
        }
        static void OnStarted(DispatcherOperation operation){
            try{
                string key=KeyOf(operation);
                lock(sync){startCount++;Bump(started,key);Bump(recentStarted,key);}
            }catch(Exception){}
        }
        internal static string KeyOf(DispatcherOperation operation){
            try{
                string priority="-";
                try{priority=operation.Priority.ToString();}catch(Exception){}
                Delegate method=DelegateOf(operation);
                if(method==null)return priority+" (delegate unreadable)";
                MethodInfo info=method.Method;
                string declaring=info.DeclaringType==null?"?":Label(info.DeclaringType);
                string target=method.Target==null?"":(" @ "+Label(method.Target.GetType()));
                return priority+" "+declaring+"."+info.Name+target;
            }catch(Exception ex){return "(key failed: "+ex.GetType().Name+")";}
        }
        static Delegate DelegateOf(DispatcherOperation operation){
            foreach(FieldInfo field in operation.GetType().GetFields(Members)){
                if(!typeof(Delegate).IsAssignableFrom(field.FieldType))continue;
                try{return field.GetValue(operation) as Delegate;}catch(Exception){return null;}
            }
            return null;
        }
        static string Label(Type type){
            if(type==null)return "?";
            string name=type.Name;
            if(name.Length>0&&name[0]=='<'){
                Type outer=type.DeclaringType;
                return (outer==null?"":Label(outer)+"+")+name;
            }
            return name;
        }
        static string SiteStack(){
            try{
                var trace=new StackTrace(2,false);
                var built=new StringBuilder();
                int limit=Math.Min(trace.FrameCount,14);
                for(int i=0;i<limit;i++){
                    MethodBase method=trace.GetFrame(i).GetMethod();
                    if(method==null)continue;
                    if(built.Length>0)built.Append(" <- ");
                    string declaring=method.DeclaringType==null?"?":Label(method.DeclaringType);
                    built.Append(declaring).Append('.').Append(method.Name);
                }
                return built.Length==0?"(empty)":built.ToString();
            }catch(Exception ex){return "(stack failed: "+ex.GetType().Name+")";}
        }
    }

    // ---------------------------------------------------------------- reflection and pumping
    static FieldInfo undoField,redoField,bytesField,editAtField,editRemovedField,editInsertedField,editPartsField,editAfterModeField;
    static readonly Dictionary<string,FieldInfo> windowFields=new Dictionary<string,FieldInfo>();
    static readonly Dictionary<string,PropertyInfo> windowProperties=new Dictionary<string,PropertyInfo>();
    static readonly Dictionary<string,MethodInfo> windowMethods=new Dictionary<string,MethodInfo>();
    static void InitReflection(){
        undoField=typeof(DocumentHistory).GetField("undo",Members);
        redoField=typeof(DocumentHistory).GetField("redo",Members);
        bytesField=typeof(DocumentHistory).GetField("bytes",Members);
        if(undoField==null||redoField==null||bytesField==null)throw new MissingMemberException("HistoryStressProbe: DocumentHistory stack fields changed");
        Type editType=undoField.FieldType.GetGenericArguments()[0];
        editAtField=editType.GetField("At",Members);editRemovedField=editType.GetField("Removed",Members);
        editInsertedField=editType.GetField("Inserted",Members);editPartsField=editType.GetField("Parts",Members);
        editAfterModeField=editType.GetField("AfterMode",Members);
        if(editAtField==null||editRemovedField==null||editInsertedField==null||editPartsField==null||editAfterModeField==null)
            throw new MissingMemberException("HistoryStressProbe: DocumentHistory.Edit fields changed");
    }
    static FieldInfo WindowField(string name){
        FieldInfo field;
        if(windowFields.TryGetValue(name,out field))return field;
        field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("HistoryStressProbe: MainWindow has no field '"+name+"'");
        windowFields[name]=field;return field;
    }
    static PropertyInfo WindowProperty(string name){
        PropertyInfo property;
        if(windowProperties.TryGetValue(name,out property))return property;
        property=typeof(MainWindow).GetProperty(name,Members);
        if(property==null)throw new MissingMemberException("HistoryStressProbe: MainWindow has no property '"+name+"'");
        windowProperties[name]=property;return property;
    }
    static MethodInfo WindowMethod(string name,int argumentCount){
        MethodInfo method;
        string key=name+"#"+argumentCount.ToString(CultureInfo.InvariantCulture);
        if(windowMethods.TryGetValue(key,out method))return method;
        MethodInfo[] candidates=typeof(MainWindow).GetMethods(Members);
        foreach(MethodInfo candidate in candidates){
            if(candidate.Name!=name)continue;
            ParameterInfo[] parameters=candidate.GetParameters();
            int required=0;foreach(ParameterInfo parameter in parameters)if(!parameter.IsOptional)required++;
            if(argumentCount<required||argumentCount>parameters.Length)continue;
            if(method!=null)throw new AmbiguousMatchException("HistoryStressProbe: MainWindow."+name+" has more than one matching overload");
            method=candidate;
        }
        if(method==null)throw new MissingMemberException("HistoryStressProbe: MainWindow has no method '"+name+"' for "+argumentCount+" arguments");
        windowMethods[key]=method;return method;
    }
    static object FieldValue(MainWindow window,string name){return WindowField(name).GetValue(window);}
    static T Field<T>(MainWindow window,string name){return (T)FieldValue(window,name);}
    static void SetField(MainWindow window,string name,object value){WindowField(name).SetValue(window,value);}
    static T Prop<T>(MainWindow window,string name){return (T)WindowProperty(name).GetValue(window,null);}
    static object Invoke(MainWindow window,string name,params object[] args){
        MethodInfo method=WindowMethod(name,args==null?0:args.Length);
        try{return method.Invoke(window,args);}
        catch(TargetInvocationException ex){if(ex.InnerException!=null)throw ex.InnerException;throw;}
    }
    static T Static<T>(string name){return (T)WindowField(name).GetValue(null);}
    static void SetStatic(string name,object value){WindowField(name).SetValue(null,value);}
    static object GetMember(object target,string name){
        if(target==null)return null;
        FieldInfo field=target.GetType().GetField(name,Members);
        if(field!=null)return field.GetValue(target);
        PropertyInfo property=target.GetType().GetProperty(name,Members);
        if(property!=null)return property.GetValue(target,null);
        throw new MissingMemberException("HistoryStressProbe: "+target.GetType().Name+" has no member '"+name+"'");
    }
    static void SetMember(object target,string name,object value){
        if(target==null)return;
        FieldInfo field=target.GetType().GetField(name,Members);
        if(field==null)throw new MissingMemberException("HistoryStressProbe: "+target.GetType().Name+" has no field '"+name+"'");
        field.SetValue(target,value);
    }
    static void PumpLight(){watchdogPlace="pump";Interlocked.Exchange(ref uiBeatTimestamp,Stopwatch.GetTimestamp());LongLineBench.Idle();}
    static void Pump(){watchdogPlace="pump";Interlocked.Exchange(ref uiBeatTimestamp,Stopwatch.GetTimestamp());LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
}
}
