using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using System.Windows.Documents;
namespace MDNote {public partial class MainWindow {
internal static int VerifyUndoWalk(string outDir){return UndoWalkProbe.Run(outDir);}
// --undo-walk-probe <dir>: how long one Ctrl+Z / Ctrl+Y costs on a small (~1300 character) document
// with a few hundred history entries, in live mode and in basic mode. Clipboard-free (no paste, no
// cut): typing, Enter, table insert, heading/list/format commands, delete, Korean/Chinese text.
// Each step is timed twice: (a) the Undo()/Redo() call alone, (b) the call plus the wait for the
// dispatcher's ApplicationIdle (what the history stress probe's PumpLight waits for). The PASS/FAIL
// verdict is p95 of (b) < 50 ms in both modes. Generated text only.
internal static class UndoWalkProbe {
    const int EditCount=300;
    const double LimitMs=50.0;
    static StringBuilder report;
    sealed class Step {public double Call,Idle,Read;public string What;}
    static readonly string[] Latin=new string[]{"alpha","be","gamma ","d","epsilon","zeta ","x","theta","io","kappa "};
    static readonly string[] Cjk=new string[]{"한글","입력","中文","漢字","テスト","가나다","문장 "};
    internal static int Run(string outputDirectory){
        string outRoot=Path.GetFullPath(outputDirectory);Directory.CreateDirectory(outRoot);
        string passFile=Path.Combine(outRoot,"undo-walk-PASS.txt"),failFile=Path.Combine(outRoot,"undo-walk-FAIL.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        report=new StringBuilder("# undo-walk-probe\n");
        report.Append("# (a) = Undo()/Redo() call alone. (b) = call + wait for Dispatcher ApplicationIdle (the stress probe's Pump). (c) = reading window.text afterwards (what the stress probe's Capture does, extra).\n");
        report.Append("# PASS when p95 of (b) < ").Append(LimitMs.ToString("0",CultureInfo.InvariantCulture)).Append(" ms in both modes.\n");
        bool all=true;
        bool oldLive=ProbeDefaults.Live,oldSelf=Program.IsSelfTest;
        try{
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            all&=RunMode(true,"live");
            all&=RunMode(false,"basic");
            // Informational, not in the verdict: the same live walk with WPF forced to software rendering (what a VM without a GPU does).
            System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
            try{RunMode(true,"live, software rendering (informational)");}
            finally{System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.Default;}
        }catch(Exception ex){all=false;report.Append("EXCEPTION ").Append(OneLine(ex.ToString())).Append('\n');}
        finally{ProbeDefaults.Live=oldLive;Program.IsSelfTest=oldSelf;}
        report.Append("\nResult: ").Append(all?"PASS":"FAIL").Append('\n');
        try{
            File.WriteAllText(Path.Combine(outRoot,"undo-walk-probe.txt"),report.ToString(),new UTF8Encoding(false));
            File.WriteAllText(all?passFile:failFile,report.ToString(),new UTF8Encoding(false));
        }catch(Exception){return 2;}
        return all?0:1;
    }
    static bool RunMode(bool live,string name){
        MainWindow window=null;bool ok=true;
        try{
            window=CreateWindow(live);
            string fixture="# Undo walk\n\nFirst paragraph with alpha and beta words.\n\n- alpha item\n- beta item\n\n> quoted alpha line\n\n| head | note |\n| --- | --- |\n| one | alpha |\n| two | beta |\n\nClosing alpha paragraph.\n";
            window.SetText(fixture);window.saved=fixture;window.recoveredDocument=false;
            LongLineBench.Idle();window.SetMode(true);window.SetLiveMode(live);LongLineBench.Idle();
            report.Append("\n## mode ").Append(name).Append('\n');
            report.Append("- surface: code=").Append(window.CodeMode).Append(", liveFormat=").Append(window.LiveFormat).Append(", plain=").Append(window.PlainText).Append(", editing=").Append(window.editing).Append(", hideMarkers=").Append(hideMarkers).Append(", previewAttached=").Append(window.LivePreviewAttached).Append('\n');
            var random=new Random(live?11:23);
            var edits=new StringBuilder();int applied=0;
            for(int i=0;i<EditCount;i++){
                try{if(Edit(window,random,i,edits))applied++;}catch(Exception ex){report.Append("- edit ").Append(i).Append(" threw ").Append(OneLine(ex.GetType().Name+": "+ex.Message)).Append('\n');}
                LongLineBench.Idle();
            }
            string built=window.text;
            report.Append("- built: edits tried=").Append(EditCount).Append(", doc chars=").Append(built.Length).Append(", can undo=").Append(window.History.CanUndo).Append('\n');
            var undo=new List<Step>();var redo=new List<Step>();
            WalkTimed(window,false,undo);
            string bottom=window.text;
            WalkTimed(window,true,redo);
            bool returned=String.Equals(window.text,built,StringComparison.Ordinal);
            report.Append("- walk: undo steps=").Append(undo.Count).Append(", redo steps=").Append(redo.Count).Append(", returned to built text=").Append(returned).Append(", bottom chars=").Append(bottom.Length).Append('\n');
            if(!returned||undo.Count!=redo.Count||undo.Count==0){ok=false;report.Append("- FAIL walk did not round-trip\n");}
            ok&=Summarize("undo",undo);
            ok&=Summarize("redo",redo);
            WalkPieces(window,false);WalkPieces(window,true);
            WalkProfiled(window,false);
            WalkProfiled(window,true);
        }catch(Exception ex){ok=false;report.Append("EXCEPTION in ").Append(name).Append(' ').Append(OneLine(ex.ToString())).Append('\n');}
        finally{CloseWindow(window);}
        return ok;
    }
    // Dispatcher operations that ran during the walk (hooks): count and total ms per call site, so the
    // idle wait in (b) can be attributed. Priority is part of the key.
    static readonly Dictionary<string,double[]> OpTotals=new Dictionary<string,double[]>();
    static long opStart;static string opKey;
    static string OpKey(System.Windows.Threading.DispatcherOperation operation){
        string name="?";
        try{
            var type=typeof(System.Windows.Threading.DispatcherOperation);
            object value=null;
            var property=type.GetProperty("Method",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public);
            if(property!=null)value=property.GetValue(operation,null);
            if(value==null){var field=type.GetField("_method",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);if(field!=null)value=field.GetValue(operation);}
            Delegate callback=value as Delegate;
            if(callback!=null)name=(callback.Method.DeclaringType==null?"":callback.Method.DeclaringType.Name)+"."+callback.Method.Name;
            else if(value!=null)name=value.GetType().Name;
        }catch(Exception){}
        return operation.Priority+" "+name;
    }
    static void OpStarted(object sender,System.Windows.Threading.DispatcherHookEventArgs e){opKey=OpKey(e.Operation);opStart=Stopwatch.GetTimestamp();}
    static void OpCompleted(object sender,System.Windows.Threading.DispatcherHookEventArgs e){
        double ms=(Stopwatch.GetTimestamp()-opStart)*1000.0/Stopwatch.Frequency;
        string key=opKey==null?OpKey(e.Operation):opKey;
        double[] total;if(!OpTotals.TryGetValue(key,out total)){total=new double[2];OpTotals[key]=total;}
        total[0]++;total[1]+=ms;opKey=null;
    }
    static void ReportOps(string label){
        var keys=new List<string>(OpTotals.Keys);
        keys.Sort(delegate(string x,string y){return OpTotals[y][1].CompareTo(OpTotals[x][1]);});
        report.Append("- dispatcher operations during ").Append(label).Append(" walk (total ms, count), top 12:\n");
        for(int i=0;i<Math.Min(12,keys.Count);i++)report.Append("    ").Append(F(OpTotals[keys[i]][1])).Append(" ms, ").Append((int)OpTotals[keys[i]][0]).Append("x  ").Append(keys[i]).Append('\n');
        OpTotals.Clear();
    }
    // Diagnostic: the same work as MainWindow.RestoreHistoryCore on the source surface, split into pieces and timed one by one.
    static readonly string[] PieceNames=new string[]{"finish+break","flush","apply (Document.Replace)","saved-compare+title+outline","focus","queue-caret-visible","UpdateUndoRedoTools","UpdateTableCommandAvailability","RefreshSizeBox","livePreview.Info","FormatAt","LiveInlineCodeAt","UpdateTableTools"};
    static void WalkPieces(MainWindow window,bool redo){
        int count=PieceNames.Length;var all=new double[count];var slow=new double[count];int steps=0,slowSteps=0;var ts=new long[count+1];
        double toMs=1000.0/Stopwatch.Frequency;int guard=0;
        while((redo?window.History.CanRedo:window.History.CanUndo)&&guard++<2000){
            SourceEditor source=window.source;
            ts[0]=Stopwatch.GetTimestamp();
            window.FinishComposition();source.BreakTypingRun();
            ts[1]=Stopwatch.GetTimestamp();
            window.FlushDocumentChanges();
            ts[2]=Stopwatch.GetTimestamp();
            bool applied;window.loading=true;try{applied=window.History.ApplySource(redo,source);}finally{window.loading=false;}
            ts[3]=Stopwatch.GetTimestamp();
            if(applied){if(window.SourceMatchesSaved())window.text=window.saved;else window.sourceSnapshotDirty=true;window.UpdateTitle();window.RefreshOutline();}
            ts[4]=Stopwatch.GetTimestamp();
            source.Focus();
            ts[5]=Stopwatch.GetTimestamp();
            window.QueueCaretRowVisible();
            ts[6]=Stopwatch.GetTimestamp();
            window.UpdateUndoRedoTools();
            ts[7]=Stopwatch.GetTimestamp();
            window.UpdateTableCommandAvailability();
            ts[8]=Stopwatch.GetTimestamp();
            window.RefreshSizeBox();
            ts[9]=Stopwatch.GetTimestamp();
            var ruleLine=source.Document.GetLineByOffset(source.CaretIndex);
            if(window.livePreview!=null&&window.LivePreviewAttached){
                window.livePreview.Info(ruleLine);
                ts[10]=Stopwatch.GetTimestamp();
                bool b,it,st,cd;int hd;window.livePreview.FormatAt(source.CaretIndex,out b,out it,out st,out cd,out hd);
                ts[11]=Stopwatch.GetTimestamp();
                window.LiveInlineCodeAt(source.CaretIndex);
            }else{ts[10]=ts[9];ts[11]=ts[9];}
            ts[12]=Stopwatch.GetTimestamp();
            window.UpdateTableTools();
            ts[13]=Stopwatch.GetTimestamp();
            steps++;bool isSlow=(ts[13]-ts[6])*toMs>5.0;if(isSlow)slowSteps++;
            // piece 6 (update-tools) ends at ts[7]; the arrays are indexed by piece
            for(int i=0;i<count;i++){double ms=(ts[i+1]-ts[i])*toMs;all[i]+=ms;if(isSlow)slow[i]+=ms;}
            LongLineBench.Idle();
        }
        report.Append("- pieces of one ").Append(redo?"redo":"undo").Append(" step, mean ms over ").Append(steps).Append(" steps (and over the ").Append(slowSteps).Append(" steps where the tool update took over 5 ms):\n");
        for(int i=0;i<count;i++)report.Append("    ").Append(PieceNames[i]).Append(": ").Append(F(all[i]/Math.Max(1,steps))).Append(" / ").Append(slowSteps==0?"-":F(slow[i]/slowSteps)).Append('\n');
    }
    // A second walk with the dispatcher hooks on (the hooks slow the walk, so its steps are not in the statistics).
    static void WalkProfiled(MainWindow window,bool redo){
        var hooks=System.Windows.Threading.Dispatcher.CurrentDispatcher.Hooks;
        hooks.OperationStarted+=OpStarted;hooks.OperationCompleted+=OpCompleted;
        try{WalkTimed(window,redo,new List<Step>());}
        finally{hooks.OperationStarted-=OpStarted;hooks.OperationCompleted-=OpCompleted;}
        ReportOps(redo?"redo":"undo");
    }
    static void WalkTimed(MainWindow window,bool redo,List<Step> steps){
        long frequency=Stopwatch.Frequency;int guard=0;
        while((redo?window.History.CanRedo:window.History.CanUndo)&&guard++<2000){
            string before=window.text;
            long t0=Stopwatch.GetTimestamp();
            if(redo)window.Redo();else window.Undo();
            long t1=Stopwatch.GetTimestamp();
            LongLineBench.Idle();
            long t2=Stopwatch.GetTimestamp();
            string after=window.text;
            long t3=Stopwatch.GetTimestamp();
            var step=new Step();
            step.Call=(t1-t0)*1000.0/frequency;step.Idle=(t2-t0)*1000.0/frequency;step.Read=(t3-t2)*1000.0/frequency;
            step.What=Describe(before,after);
            steps.Add(step);
        }
    }
    static string Describe(string before,string after){
        int limit=Math.Min(before.Length,after.Length),prefix=0;
        while(prefix<limit&&before[prefix]==after[prefix])prefix++;
        int suffix=0;
        while(suffix<limit-prefix&&before[before.Length-1-suffix]==after[after.Length-1-suffix])suffix++;
        string removed=before.Substring(prefix,before.Length-prefix-suffix),added=after.Substring(prefix,after.Length-prefix-suffix);
        return "at "+prefix+": removed "+removed.Length+" \""+Clip(removed)+"\", inserted "+added.Length+" \""+Clip(added)+"\"";
    }
    static string Clip(string value){
        if(value.Length>40)value=value.Substring(0,40)+"...";
        return OneLine(value);
    }
    static bool Summarize(string label,List<Step> steps){
        if(steps.Count==0)return false;
        double[] a=new double[steps.Count],b=new double[steps.Count],c=new double[steps.Count];
        for(int i=0;i<steps.Count;i++){a[i]=steps[i].Call;b[i]=steps[i].Idle;c[i]=steps[i].Read;}
        Array.Sort(a);Array.Sort(b);Array.Sort(c);
        report.Append("- ").Append(label).Append(" (a) call alone ms: ").Append(Stats(a)).Append('\n');
        report.Append("- ").Append(label).Append(" (b) call + idle ms: ").Append(Stats(b)).Append('\n');
        report.Append("- ").Append(label).Append(" (c) text read ms: ").Append(Stats(c)).Append('\n');
        var order=new List<Step>(steps);
        order.Sort(delegate(Step x,Step y){return y.Idle.CompareTo(x.Idle);});
        for(int i=0;i<Math.Min(5,order.Count);i++){
            Step s=order[i];
            report.Append("  slowest ").Append(label).Append(' ').Append(i+1).Append(": step ").Append(steps.IndexOf(s)+1).Append(", (a) ").Append(F(s.Call)).Append(" / (b) ").Append(F(s.Idle)).Append(" ms, ").Append(s.What).Append('\n');
        }
        double p95=Percentile(b,0.95);
        bool ok=p95<LimitMs;
        report.Append("- ").Append(label).Append(" p95 of (b)=").Append(F(p95)).Append(" ms -> ").Append(ok?"ok":"TOO SLOW").Append('\n');
        return ok;
    }
    static string Stats(double[] sorted){
        return "min "+F(sorted[0])+", median "+F(Percentile(sorted,0.5))+", p95 "+F(Percentile(sorted,0.95))+", max "+F(sorted[sorted.Length-1]);
    }
    static double Percentile(double[] sorted,double fraction){
        int index=(int)Math.Ceiling(fraction*sorted.Length)-1;
        if(index<0)index=0;if(index>=sorted.Length)index=sorted.Length-1;
        return sorted[index];
    }
    static string F(double value){return value.ToString("0.0",CultureInfo.InvariantCulture);}
    // ------------------------------------------------ edits (no clipboard)
    static bool Edit(MainWindow window,Random random,int index,StringBuilder log){
        SourceEditor source=window.source;
        int length=source.Document.TextLength;
        // Past ~1300 characters mostly delete, so the document stays near the size of the stress run.
        int phase=length>1400?(index%3==0?8:7):index%10;
        if(random.Next(10)<7)PositionCaret(source,random);
        switch(phase){
            case 0:case 1:source.TextArea.PerformTextInput(Latin[random.Next(Latin.Length)]);return true;
            case 2:source.TextArea.PerformTextInput(Cjk[random.Next(Cjk.Length)]);return true;
            case 3:Enter(window,source);return true;
            case 4:
                if(window.LiveFormat){window.InsertTable(2+random.Next(3),1+random.Next(2));return true;}
                source.TextArea.PerformTextInput("| a | b |");return true;
            case 5:window.SetHeading(1+random.Next(6));return true;
            case 6:
                switch(random.Next(4)){
                    case 0:window.ToggleListStyle(false);break;
                    case 1:window.ToggleListStyle(true);break;
                    case 2:window.ToggleFormat("bold");break;
                    default:window.ToggleFormat("italic");break;
                }
                return true;
            case 7:source.TextArea.PerformTextInput(Latin[random.Next(Latin.Length)]);return true;
            default:Delete(source);return true;
        }
    }
    static void Enter(MainWindow window,SourceEditor source){
        if(window.LiveFormat){
            var command=window.SourceEnter();
            if(command==null)source.TextArea.PerformTextInput("\n");
            else window.ApplyCommandResult(command,-1,0);
        }else source.TextArea.PerformTextInput("\n");
    }
    static void Delete(SourceEditor source){
        PresentationSource presentation=PresentationSource.FromVisual(source);
        if(presentation!=null){
            var args=new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,Key.Delete) {RoutedEvent=Keyboard.PreviewKeyDownEvent};
            source.TextArea.RaiseEvent(args);
            if(args.Handled)return;
        }
        EditingCommands.Delete.Execute(null,source.TextArea);
    }
    static void PositionCaret(SourceEditor source,Random random){
        int length=source.Document.TextLength;
        if(length<=0){source.Select(0,0);return;}
        int roll=random.Next(10);
        if(roll<2){source.Select(length,0);return;}
        int at=random.Next(length);
        if(roll<8){source.Select(at,0);return;}
        source.Select(at,1+random.Next(Math.Min(12,length-at)));
    }
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
    static string OneLine(string value){return value==null?"<null>":value.Replace("\r\n","\\n").Replace("\r","\\r").Replace("\n","\\n");}
}
}}
