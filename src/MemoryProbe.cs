using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Text;
using System.Windows;
using System.Windows.Threading;
namespace MDNote {public partial class MainWindow {
internal static int VerifyMemory(string outDir){return MemoryProbe.Run(outDir);}
// --memory-probe <dir>: memory of one window holding a small document (11-09 appinn review: "90+ MB"),
// in the live and the basic view, measured after the window settled for a few seconds. Informational:
// always PASS, the numbers are the result. Generated text only.
// The same run also measures a second window, ten tabs, the loaded native modules and a compacting-GC
// pass, and MDNOTE_MEM_SOFTWARE=1 renders in software for comparison. Still informational: always PASS.
internal static class MemoryProbe {
    static StringBuilder report;static string progressFile;
    const string SmallFixture="# 제목\n\n본문 **굵게** 한 줄과 `코드`.\n\n- 목록\n- 항목\n\n> 인용\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n";
    internal static int Run(string outputDirectory){
        string outRoot=Path.GetFullPath(outputDirectory);Directory.CreateDirectory(outRoot);
        bool software=Environment.GetEnvironmentVariable("MDNOTE_MEM_SOFTWARE")=="1";
        report=new StringBuilder("# memory-probe (MB)\n");report.Append("render mode: ").Append(software?"software":"default").Append('\n');
        progressFile=Path.Combine(outRoot,"memory-progress.txt");
        bool oldLive=ProbeDefaults.Live,oldSelf=Program.IsSelfTest;
        try{
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            if(software)System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
            Sample("process start (before any window)");
            MeasureMode(true,"live");
            Step("native modules");SampleModules();
            MeasureMode(false,"basic");
            MeasureTwoWindows();
            MeasureTenTabs();
            report.Append("\n## loaded assemblies\n");
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies())report.Append("- ").Append(assembly.GetName().Name).Append('\n');
            AppendGcSummary();
        }catch(Exception ex){report.Append("EXCEPTION ").Append(ex.ToString().Replace("\r\n","\\n")).Append('\n');}
        finally{ProbeDefaults.Live=oldLive;Program.IsSelfTest=oldSelf;}
        try{
            File.WriteAllText(Path.Combine(outRoot,"memory-probe.txt"),report.ToString(),new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outRoot,"memory-PASS.txt"),report.ToString(),new UTF8Encoding(false));
        }catch(Exception){return 2;}
        return 0;
    }
    static void MeasureMode(bool live,string name){
        MainWindow window=null;
        bool old=ProbeDefaults.Live;ProbeDefaults.Live=live;
        try{
            Step(name+" create");window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;window.SetMode(true);LongLineBench.Idle();window.UpdateLayout();
        }finally{ProbeDefaults.Live=old;}
        string fixture=SmallFixture;
        window.SetText(fixture);window.saved=fixture;window.recoveredDocument=false;
        LongLineBench.Idle();window.SetMode(true);window.SetLiveMode(live);LongLineBench.Idle();
        Step(name+" text set");Wait(8000);Step(name+" waited");
        Sample(name+" window, small document, 8 s idle");
        FullGcSample(name+" after full GC");
        LohCompactSample(name+" after LOH compact");
        Step(name+" closing");CloseWindow(window);
    }
    // One live window with the same fixture as MeasureMode, for the two-window and ten-tab parts.
    static MainWindow CreateLiveWindow(){
        MainWindow window=null;bool old=ProbeDefaults.Live;ProbeDefaults.Live=true;
        try{
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;window.SetMode(true);LongLineBench.Idle();window.UpdateLayout();
        }finally{ProbeDefaults.Live=old;}
        window.SetText(SmallFixture);window.saved=SmallFixture;window.recoveredDocument=false;
        LongLineBench.Idle();window.SetMode(true);window.SetLiveMode(true);LongLineBench.Idle();
        return window;
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{window.FlushDocumentChanges();window.saved=window.text;window.recoveredDocument=false;window.CaptureTab();foreach(var document in window.documents){document.Saved=document.Text;document.Recovered=false;}window.Close();}catch(Exception){}
        LongLineBench.Idle();
    }
    // A second window in the same process: the first one is already warm, so the delta covers the
    // marginal cost of another window (and another live session), not the one-time WPF start-up.
    static void MeasureTwoWindows(){
        MainWindow first=null,second=null;
        try{
            Step("two windows create first");first=CreateLiveWindow();Wait(8000);Step("two windows first waited");
            Sample("two windows: first window, 8 s idle");
            long workingBefore,privateBefore,managedBefore;
            Usage(out workingBefore,out privateBefore,out managedBefore);
            Step("two windows create second");second=CreateLiveWindow();Wait(8000);Step("two windows second waited");
            Sample("two windows: second window added, 8 s idle");
            long workingAfter,privateAfter,managedAfter;
            Usage(out workingAfter,out privateAfter,out managedAfter);
            report.Append("- two windows: second window delta: working set=").Append(Mb(workingAfter-workingBefore))
                .Append(", private=").Append(Mb(privateAfter-privateBefore))
                .Append(", managed heap=").Append(Mb(managedAfter-managedBefore)).Append('\n');
            Step("two windows closing");CloseWindow(second);second=null;CloseWindow(first);first=null;
            FullGcSample("two windows: both closed, after full GC");
        }finally{CloseWindow(second);CloseWindow(first);}
    }
    // Ten tabs in one window, each holding the same small document: the delta over the first tab is
    // what an extra tab (its own editor, preview and history) costs while the window itself stays put.
    static void MeasureTenTabs(){
        MainWindow window=null;
        try{
            Step("ten tabs create");window=CreateLiveWindow();Wait(8000);Step("ten tabs first waited");
            Sample("ten tabs: 1 tab, 8 s idle");
            long workingBefore,privateBefore,managedBefore;
            Usage(out workingBefore,out privateBefore,out managedBefore);
            for(int i=2;i<=10;i++){window.CreateTab();window.SetText(SmallFixture);window.saved=window.text;window.recoveredDocument=false;LongLineBench.Idle();}
            Step("ten tabs added");Wait(8000);Step("ten tabs waited");
            Sample("ten tabs: 10 tabs, 8 s idle");
            long workingAfter,privateAfter,managedAfter;
            Usage(out workingAfter,out privateAfter,out managedAfter);
            report.Append("- ten tabs: nine extra tabs delta: working set=").Append(Mb(workingAfter-workingBefore))
                .Append(", private=").Append(Mb(privateAfter-privateBefore))
                .Append(", managed heap=").Append(Mb(managedAfter-managedBefore)).Append('\n');
            FullGcSample("ten tabs: after full GC");
            LohCompactSample("ten tabs: after LOH compact");
        }finally{Step("ten tabs closing");CloseWindow(window);}
    }
    // The render stack is process-wide and stays loaded after the window closes, so listing here still
    // shows what the first live window pulled in: GDI+, the JIT, the WPF/Direct3D stack and drivers.
    static void SampleModules(){
        try{
            var modules=new List<KeyValuePair<string,long>>();
            using(var process=Process.GetCurrentProcess()){
                foreach(ProcessModule module in process.Modules)modules.Add(new KeyValuePair<string,long>(module.ModuleName,module.ModuleMemorySize));
            }
            modules.Sort(delegate(KeyValuePair<string,long> a,KeyValuePair<string,long> b){return b.Value.CompareTo(a.Value);});
            report.Append("\n## native modules (top ").Append(Math.Min(40,modules.Count)).Append(" of ").Append(modules.Count).Append(", after the first window settled)\n");
            for(int i=0;i<modules.Count&&i<40;i++)report.Append("- ").Append(Path.GetFileName(modules[i].Key)).Append(": ").Append(Mb(modules[i].Value)).Append('\n');
            report.Append("\n## native module flags\n");
            ModuleFlag(modules,"gdiplus.dll","gdiplus.dll",true);
            ModuleFlag(modules,"clrjit.dll","clrjit.dll",true);
            ModuleFlag(modules,"d3d9.dll","d3d9.dll",true);
            ModuleFlag(modules,"driver *d3d*","d3d",false);
            ModuleFlag(modules,"driver *nvd*","nvd",false);
            ModuleFlag(modules,"driver *igd*","igd",false);
            ModuleFlag(modules,"driver *amd*","amd",false);
            ModuleFlag(modules,"driver *atio*","atio",false);
            ModuleFlag(modules,"wpfgfx_v0400.dll","wpfgfx_v0400.dll",true);
            ModuleFlag(modules,"System.Windows.Forms*.dll","System.Windows.Forms",false);
        }catch(Exception ex){report.Append("module list EXCEPTION ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');}
    }
    static void ModuleFlag(List<KeyValuePair<string,long>> modules,string label,string pattern,bool exact){
        var matches=new List<string>();
        foreach(var module in modules){string file=Path.GetFileName(module.Key);bool hit=exact?String.Equals(file,pattern,StringComparison.OrdinalIgnoreCase):file.IndexOf(pattern,StringComparison.OrdinalIgnoreCase)>=0;if(hit&&!matches.Contains(file))matches.Add(file);}
        report.Append("- ").Append(label).Append(": ");
        if(matches.Count==0)report.Append("not loaded");else for(int i=0;i<matches.Count;i++){if(i>0)report.Append(", ");report.Append(matches[i]);}
        report.Append('\n');
    }
    static void Usage(out long workingSet,out long privateBytes,out long managedHeap){
        using(var process=Process.GetCurrentProcess()){process.Refresh();workingSet=process.WorkingSet64;privateBytes=process.PrivateMemorySize64;}
        managedHeap=GC.GetTotalMemory(false);
    }
    static void FullGcSample(string label){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Wait(1000);Sample(label);}
    // Server GC does not support LOH compaction and the setter throws there: sample anyway, marked, so
    // the rest of the report still runs on a machine configured that way.
    static void LohCompactSample(string label){
        bool supported=true;try{GCSettings.LargeObjectHeapCompactionMode=GCLargeObjectHeapCompactionMode.CompactOnce;}catch(Exception){supported=false;}
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Wait(1000);Sample(supported?label:label+" (not supported under server GC)");
    }
    static void AppendGcSummary(){
        report.Append("\n## gc\n");
        report.Append("- collections: gen0=").Append(GC.CollectionCount(0)).Append(", gen1=").Append(GC.CollectionCount(1)).Append(", gen2=").Append(GC.CollectionCount(2)).Append('\n');
        report.Append("- server GC: ").Append(GCSettings.IsServerGC?"yes":"no").Append(", latency mode: ").Append(GCSettings.LatencyMode).Append('\n');
    }
    static void Step(string what){try{File.AppendAllText(progressFile,DateTime.Now.ToString("HH:mm:ss.fff")+" "+what+"\n");}catch(Exception){}}
    static void Wait(int milliseconds){
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=delegate{timer.Stop();frame.Continue=false;};
        timer.Start();Dispatcher.PushFrame(frame);
    }
    static void Sample(string label){
        using(var process=Process.GetCurrentProcess()){
            process.Refresh();
            report.Append("- ").Append(label).Append(": working set=").Append(Mb(process.WorkingSet64))
                .Append(", private=").Append(Mb(process.PrivateMemorySize64))
                .Append(", managed heap=").Append(Mb(GC.GetTotalMemory(false))).Append('\n');
        }
    }
    static string Mb(long bytes){return (bytes/1048576.0).ToString("0.0",CultureInfo.InvariantCulture);}
}
}}
