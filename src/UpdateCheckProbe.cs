using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace MDNote {
// --update-probe <outDir>: the update check (UpdateCheck.cs) without a network, a browser or a real click.
// The release answer, the new-version dialog, the notice box, the browser and the clock are probe seams, so
// the daily rule, the strip above the status bar, the strip's skip-this-version link, the Help item and the settings
// keys are driven exactly as the app runs them. One case opens the real dialog and presses its controls.
static class UpdateCheckProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static int cases,passed,failed;
    static StringBuilder report;
    static string traceFile;
    // seam state
    static int jsonCalls,offerCalls,noticeCalls;
    static Func<string> jsonFactory;
    static string lastOfferVersion,lastOfferUrl,lastNotice;
    static bool nextOpen;
    static readonly List<string> opened=new List<string>();
    static int? installerValue;
    static readonly DateTime Noon=new DateTime(2026,11,5,12,0,0);

    internal static int Run(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string passFile=Path.Combine(outDir,"update-probe-PASS.txt"),failFile=Path.Combine(outDir,"update-probe-FAIL.txt"),resultFile=Path.Combine(outDir,"update-probe.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        report=new StringBuilder();cases=0;passed=0;failed=0;traceFile=Path.Combine(outDir,"update-probe.trace.txt");if(File.Exists(traceFile))File.Delete(traceFile);
        MainWindow w1=null,w2=null;
        bool? oldAuto=MainWindow.autoUpdateChoice;string oldDate=MainWindow.updateCheckedDate,oldSkip=MainWindow.skippedUpdateVersion;
        try{
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            RunCase("a-versions",CaseVersions);
            RunCase("b-release-json",CaseReleaseJson);
            RunCase("c-daily-rule",CaseDailyRule);
            RunCase("d-preferences",delegate{CasePreferences(outDir);});
            SetSeams();
            // windows are made before the probe drives the check: a self-test never starts it by itself
            UpdateChecker.ProbeDrive=false;
            jsonFactory=delegate{return Json(Newer());};jsonCalls=0;
            w1=NewWindow();
            Pump();
            Check("e0 self-test window does not start a check",jsonCalls==0&&UpdateChecker.Completed==0,"calls="+jsonCalls+" completed="+UpdateChecker.Completed);
            MenuItem firstToggle=FindToggle(w1);
            Check("e0 Help item starts unchecked (no choice, no installer value)",firstToggle!=null&&!firstToggle.IsChecked&&!MainWindow.autoUpdateChoice.HasValue,"");
            UpdateChecker.ProbeDrive=true;
            MainWindow window=w1;
            RunCase("e1-same-day",delegate{CaseSameDay(window);});
            RunCase("e2-new-version-strip",delegate{CaseStrip(window);});
            RunCase("e3-details-and-skip",delegate{CaseDetailsAndSkip(window);});
            RunCase("e4-skipped-and-newer",delegate{CaseSkipped(window);});
            RunCase("e5-manual-shows-skipped",delegate{CaseManual(window);});
            RunCase("e6-setting-off-failure-uptodate",delegate{CaseOffFailureUpToDate(window);});
            RunCase("f-installer-value",delegate{CaseInstallerValue(window);});
            UpdateChecker.ProbeDrive=false;w2=NewWindow();Pump();UpdateChecker.ProbeDrive=true;
            MainWindow second=w2;
            RunCase("e7-first-window-only",delegate{CaseFirstWindowOnly(window,second);});
            RunCase("e8-real-dialog",delegate{CaseRealDialog(window);});
            RunCase("e9-help-menu",delegate{CaseHelpMenu(window);});
            RunShots(outDir,window);
        }catch(Exception ex){Check("probe setup",false,ex.ToString());}
        finally{
            UpdateChecker.ProbeDrive=false;UpdateChecker.DialogShownHook=null;UpdateChecker.ReleaseJsonHook=null;UpdateChecker.OfferHook=null;UpdateChecker.NoticeHook=null;UpdateChecker.OpenPageHook=null;UpdateChecker.NowHook=null;UpdateChecker.InstallerValueHook=null;
            MainWindow.autoUpdateChoice=oldAuto;MainWindow.updateCheckedDate=oldDate;MainWindow.skippedUpdateVersion=oldSkip;
        }
        report.Append("Cases: ").Append(cases).Append(" PASS: ").Append(passed).Append(" FAIL: ").Append(failed).Append('\n');
        File.WriteAllText(resultFile,report.ToString(),new UTF8Encoding(false));
        File.WriteAllText(failed==0&&cases>0?passFile:failFile,report.ToString(),new UTF8Encoding(false));
        CloseClean(w2);CloseClean(w1);
        return failed==0&&cases>0?0:1;
    }
    static void RunCase(string name,Action body){
        Trace("CASE "+name);
        try{body();}catch(Exception ex){Exception inner=ex is TargetInvocationException&&ex.InnerException!=null?ex.InnerException:ex;Check(name+" no exception",false,inner.GetType().Name+": "+inner.Message+"\n"+inner.StackTrace);}
    }
    static void Check(string name,bool ok,string detail){
        cases++;if(ok)passed++;else failed++;
        report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n');
        Trace((ok?"PASS ":"FAIL ")+name+": "+detail);
    }
    static void Trace(string line){if(traceFile==null)return;try{File.AppendAllText(traceFile,line+"\n",new UTF8Encoding(false));}catch(IOException){}}

    // ---- helpers
    // The cases typed into the first window: mark every tab saved so closing it opens no save question.
    static void CloseClean(MainWindow window){
        if(window==null)return;
        try{
            typeof(MainWindow).GetMethod("FlushDocumentChanges",Members).Invoke(window,null);
            SourceEditor editor=Field<SourceEditor>(window,"source");
            typeof(MainWindow).GetField("text",Members).SetValue(window,editor.Document.Text);
            typeof(MainWindow).GetField("saved",Members).SetValue(window,editor.Document.Text);
            typeof(MainWindow).GetField("recoveredDocument",Members).SetValue(window,false);
            typeof(MainWindow).GetField("sourceSnapshotDirty",Members).SetValue(window,false);
            var tabs=(System.Collections.IList)typeof(MainWindow).GetField("documents",Members).GetValue(window);
            foreach(object tab in tabs){tab.GetType().GetField("Saved",Members).SetValue(tab,tab.GetType().GetField("Text",Members).GetValue(tab));tab.GetType().GetField("Recovered",Members).SetValue(tab,false);}
            window.Close();
        }catch(Exception){}
    }
    static void Pump(){LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
    static bool WaitDone(int target){
        var clock=Stopwatch.StartNew();
        while(UpdateChecker.Completed<target&&clock.ElapsedMilliseconds<8000){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));System.Threading.Thread.Sleep(5);}
        Pump();
        return UpdateChecker.Completed>=target;
    }
    static MainWindow NewWindow(){
        var window=new MainWindow {ShowInTaskbar=false,Width=900,Height=700};
        window.Show();window.Left=-20000;Pump();
        FieldInfo timer=typeof(MainWindow).GetField("recoveryTimer",Members);
        if(timer!=null){var value=timer.GetValue(window) as DispatcherTimer;if(value!=null)value.Stop();}
        return window;
    }
    static T Field<T>(MainWindow window,string name){
        FieldInfo field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");
        return (T)field.GetValue(window);
    }
    static Version Cur(){return UpdateChecker.CurrentVersion();}
    static Version Newer(){Version c=Cur();return new Version(c.Major,c.Minor,c.Build+1);}
    static string Json(Version version){return "{\"url\":\"x\",\"tag_name\":\"v"+version+"\",\"name\":\"MDNote "+version+"\",\"html_url\":\"https://github.com/Lahai-Roi/MDNote/releases/tag/v"+version+"\",\"assets\":[]}";}
    static void SetSeams(){
        UpdateChecker.NowHook=delegate{return Noon;};
        UpdateChecker.InstallerValueHook=delegate{return installerValue;};
        UpdateChecker.ReleaseJsonHook=delegate{jsonCalls++;return jsonFactory();};
        UpdateChecker.OfferHook=delegate(string version,string url){offerCalls++;lastOfferVersion=version;lastOfferUrl=url;return nextOpen;};
        UpdateChecker.NoticeHook=delegate(string message){noticeCalls++;lastNotice=message;};
        UpdateChecker.OpenPageHook=delegate(string url){opened.Add(url);};
    }
    static string Yesterday(){return Noon.AddDays(-1).ToString("yyyy-MM-dd");}
    static string Today(){return Noon.ToString("yyyy-MM-dd");}
    // One automatic check as the app arms it (the first window's Loaded); returns when it has finished.
    static bool AutoRun(MainWindow window,string last,bool? enabled,string skipped,Func<string> factory){
        UpdateChecker.ResetForProbe();
        MainWindow.updateCheckedDate=last;MainWindow.autoUpdateChoice=enabled;MainWindow.skippedUpdateVersion=skipped;
        jsonCalls=0;offerCalls=0;noticeCalls=0;jsonFactory=factory;nextOpen=false;opened.Clear();
        window.HideUpdateStrip();
        int target=UpdateChecker.Completed+1;
        UpdateChecker.ScheduleAuto(window);
        return WaitDone(target);
    }
    static int Caret(MainWindow window){return Field<SourceEditor>(window,"source").CaretIndex;}
    static object Focused(){return Keyboard.FocusedElement;}

    // ---- pure cases
    static void CaseVersions(){
        Check("a 2.2.0 < 2.2.1",UpdateChecker.ParseTag("2.2.1").CompareTo(UpdateChecker.ParseTag("2.2.0"))>0,"");
        Check("a v prefix and 2.10.0 > 2.9.0",UpdateChecker.ParseTag("v2.10.0").CompareTo(UpdateChecker.ParseTag("2.9.0"))>0,"");
        Check("a equal",UpdateChecker.ParseTag("v2.2.0").CompareTo(UpdateChecker.ParseTag("2.2.0"))==0,"");
        Check("a two numbers = x.y.0",UpdateChecker.ParseTag("2.3").Equals(new Version(2,3,0)),"");
        Check("a suffix ignored",UpdateChecker.ParseTag("2.2.1-beta").Equals(new Version(2,2,1)),"");
        Check("a garbage is null",UpdateChecker.ParseTag("latest")==null&&UpdateChecker.ParseTag("")==null&&UpdateChecker.ParseTag(null)==null&&UpdateChecker.ParseTag("vv1")==null,"");
        Check("a skip rule: equal skipped",UpdateChecker.IsSkipped("2.2.1",new Version(2,2,1)),"");
        Check("a skip rule: older than skipped",UpdateChecker.IsSkipped("2.3.0",new Version(2,2,5)),"");
        Check("a skip rule: newer than skipped is announced",!UpdateChecker.IsSkipped("2.2.1",new Version(2,2,2)),"");
        Check("a skip rule: empty or garbage skips nothing",!UpdateChecker.IsSkipped("",new Version(9,9,9))&&!UpdateChecker.IsSkipped("zz",new Version(9,9,9)),"");
        Check("a release target keeps this project's link",UpdateChecker.ReleaseTarget("https://github.com/Lahai-Roi/MDNote/releases/tag/v2.2.1")=="https://github.com/Lahai-Roi/MDNote/releases/tag/v2.2.1","");
        Check("a release target falls back for a foreign link",UpdateChecker.ReleaseTarget("https://evil.example/x")=="https://github.com/Lahai-Roi/MDNote/releases/latest"&&UpdateChecker.ReleaseTarget(null)=="https://github.com/Lahai-Roi/MDNote/releases/latest","");
    }
    static void CaseReleaseJson(){
        Version v;string url;
        bool ok=UpdateChecker.ParseRelease(Json(new Version(2,2,1)),out v,out url);
        Check("b good answer",ok&&v.Equals(new Version(2,2,1))&&url=="https://github.com/Lahai-Roi/MDNote/releases/tag/v2.2.1",ok+" "+v+" "+url);
        ok=UpdateChecker.ParseRelease("{\"message\":\"Not Found\"}",out v,out url);
        Check("b no tag",!ok&&v==null,"");
        ok=UpdateChecker.ParseRelease("{\"tag_name\":\"nightly\"}",out v,out url);
        Check("b unparsable tag",!ok,"");
        ok=UpdateChecker.ParseRelease(null,out v,out url);
        Check("b null answer",!ok,"");
    }
    static void CaseDailyRule(){
        Check("c same date: no check",!UpdateChecker.AutoDue(true,"2026-11-05","2026-11-05"),"");
        Check("c yesterday: check",UpdateChecker.AutoDue(true,"2026-11-04","2026-11-05"),"");
        Check("c never checked: check",UpdateChecker.AutoDue(true,"","2026-11-05"),"");
        Check("c setting off: no check",!UpdateChecker.AutoDue(false,"2026-11-04","2026-11-05")&&!UpdateChecker.AutoDue(false,"","2026-11-05"),"");
        UpdateChecker.NowHook=delegate{return Noon;};
        Check("c today string is the local yyyy-MM-dd",UpdateChecker.Today()=="2026-11-05",UpdateChecker.Today());
    }
    static void CasePreferences(string outDir){
        var plain=new ViewPreferences();
        string xml=plain.Serialize();
        Check("d default writes none of the three keys",xml.IndexOf("autoUpdateCheck")<0&&xml.IndexOf("updateCheckedDate")<0&&xml.IndexOf("skippedUpdateVersion")<0,xml);
        Check("d default: no stored choice",!plain.AutoUpdateChoice.HasValue&&plain.UpdateCheckedDate==""&&plain.SkippedUpdateVersion=="","");
        var set=new ViewPreferences {AutoUpdateChoice=false,UpdateCheckedDate="2026-11-05",SkippedUpdateVersion="2.2.1"};
        string file=Path.Combine(outDir,"update-prefs.xml");
        File.WriteAllText(file,set.Serialize(),new UTF8Encoding(false));
        var back=ViewPreferences.Load(file);
        Check("d round trip",back.AutoUpdateChoice==false&&back.UpdateCheckedDate=="2026-11-05"&&back.SkippedUpdateVersion=="2.2.1",back.AutoUpdateChoice+" "+back.UpdateCheckedDate+" "+back.SkippedUpdateVersion);
        File.WriteAllText(file,new ViewPreferences().Serialize().Replace("<MDNote ","<MDNote autoUpdateCheck=\"maybe\" updateCheckedDate=\"yesterday\" skippedUpdateVersion=\"two\" "),new UTF8Encoding(false));
        back=ViewPreferences.Load(file);
        Check("d bad values fall back (no choice, no date, no skip)",!back.AutoUpdateChoice.HasValue&&back.UpdateCheckedDate==""&&back.SkippedUpdateVersion=="",back.AutoUpdateChoice+" ["+back.UpdateCheckedDate+"] ["+back.SkippedUpdateVersion+"]");
        File.WriteAllText(file,"<MDNote windowPlacement=\"\" language=\"ko\"/>",new UTF8Encoding(false));
        back=ViewPreferences.Load(file);
        Check("d older file without the keys = no stored choice",!back.AutoUpdateChoice.HasValue,"");
        var baseline=new ViewPreferences();var mine=baseline.Copy();mine.UpdateCheckedDate="2026-11-05";mine.SkippedUpdateVersion="2.2.1";mine.AutoUpdateChoice=true;
        var disk=new ViewPreferences();
        var merged=ViewPreferences.MergePreferences(mine,baseline,disk);
        Check("d merge writes the changed keys",merged.AutoUpdateChoice==true&&merged.UpdateCheckedDate=="2026-11-05"&&merged.SkippedUpdateVersion=="2.2.1","");
        var untouched=ViewPreferences.MergePreferences(baseline.Copy(),baseline,new ViewPreferences {UpdateCheckedDate="2026-11-04",SkippedUpdateVersion="2.2.0"});
        Check("d merge keeps another window's values",untouched.UpdateCheckedDate=="2026-11-04"&&untouched.SkippedUpdateVersion=="2.2.0","");
        Check("d copy keeps the keys",set.Copy().SkippedUpdateVersion=="2.2.1"&&set.Copy().AutoUpdateChoice==false&&set.Copy().UpdateCheckedDate=="2026-11-05","");
        try{File.Delete(file);}catch(IOException){}
    }

    // ---- window cases
    static void CaseSameDay(MainWindow window){
        bool done=AutoRun(window,Today(),true,"",delegate{return Json(Newer());});
        Check("e1 finished",done,"");
        Check("e1 same date: no request",jsonCalls==0,"calls="+jsonCalls);
        Check("e1 no strip",!window.UpdateStripVisible,"");
        Check("e1 date unchanged",MainWindow.updateCheckedDate==Today(),MainWindow.updateCheckedDate);
    }
    static void CaseStrip(MainWindow window){
        SourceEditor editor=Field<SourceEditor>(window,"source");
        editor.Document.Text="line one\nline two\nline three\n";editor.Select(11,0);editor.Focus();Pump();
        int caretBefore=editor.CaretIndex;object focusBefore=Focused();
        bool done=AutoRun(window,Yesterday(),true,"",delegate{return Json(Newer());});
        Check("e2 finished",done,"");
        Check("e2 one request",jsonCalls==1,"calls="+jsonCalls);
        Check("e2 date recorded as today",MainWindow.updateCheckedDate==Today(),MainWindow.updateCheckedDate);
        Check("e2 strip is visible with the version",window.UpdateStripVisible&&window.UpdateStripVersion==Newer().ToString(),window.UpdateStripVersion);
        string text=window.UpdateStripText;
        Check("e2 strip text names the version and has the details link",text!=null&&text.IndexOf(Newer().ToString())>=0&&text.IndexOf("자세히")>=0,text);
        Check("e2 strip text has only the details link, the skip text is a box",window.UpdateStripLinkCount==1&&text!=null&&text.IndexOf("이 버전은 다시 알리지 않음")<0&&window.UpdateStripSkipText=="이 버전은 다시 알리지 않음",text+" links="+window.UpdateStripLinkCount+" box="+window.UpdateStripSkipText);
        Check("e2 skip box starts unticked",!window.UpdateStripSkipChecked,"");
        Check("e2 no dialog was opened",offerCalls==0&&noticeCalls==0,"offer="+offerCalls+" notice="+noticeCalls);
        window.UpdateLayout();Pump();
        Check("e2 caret unchanged",editor.CaretIndex==caretBefore,caretBefore+" -> "+editor.CaretIndex);
        Check("e2 focus unchanged",Object.ReferenceEquals(Focused(),focusBefore),"before="+(focusBefore==null?"null":focusBefore.GetType().Name)+" after="+(Focused()==null?"null":Focused().GetType().Name));
        FrameworkElement strip=Field<FrameworkElement>(window,"updateStrip"),bar=Field<FrameworkElement>(window,"statusBar");
        double stripTop=strip.TranslatePoint(new Point(0,0),window).Y,barTop=bar.TranslatePoint(new Point(0,0),window).Y;
        Check("e2 strip sits directly above the status bar",strip.ActualHeight>0&&Math.Abs(stripTop+strip.ActualHeight-barTop)<1.5,"strip top="+stripTop+" h="+strip.ActualHeight+" bar top="+barTop);
        FrameworkElement[] ownText=new FrameworkElement[]{strip};
        Check("e2 strip uses a theme brush",((Border)strip).Background!=null,"");
        window.ClickUpdateStripClose();window.UpdateLayout();Pump();
        Check("e2 close hides the strip",!window.UpdateStripVisible,"");
        Check("e2 close stores nothing",MainWindow.skippedUpdateVersion=="",MainWindow.skippedUpdateVersion);
        Check("e2 caret and focus unchanged after close",editor.CaretIndex==caretBefore&&Object.ReferenceEquals(Focused(),focusBefore),"");
        Check("e2 ownText",ownText.Length==1,"");
    }
    static void CaseDetailsAndSkip(MainWindow window){
        AutoRun(window,Yesterday(),true,"",delegate{return Json(Newer());});
        Check("e3 strip shown",window.UpdateStripVisible,"");
        SourceEditor editor=Field<SourceEditor>(window,"source");
        editor.Select(5,0);editor.Focus();Pump();
        int caretBefore=editor.CaretIndex;object focusBefore=Focused();
        offerCalls=0;nextOpen=true;
        window.ClickUpdateStripDetails();Pump();
        Check("e3 details opens the dialog once",offerCalls==1,"offer="+offerCalls);
        Check("e3 dialog gets the version and the release link",lastOfferVersion==Newer().ToString()&&lastOfferUrl=="https://github.com/Lahai-Roi/MDNote/releases/tag/v"+Newer(),lastOfferVersion+" "+lastOfferUrl);
        Check("e3 release page button opens the link",opened.Count==1&&opened[0]==lastOfferUrl,String.Join(",",opened.ToArray()));
        Check("e3 details: strip stays, nothing stored",window.UpdateStripVisible&&MainWindow.skippedUpdateVersion=="",MainWindow.skippedUpdateVersion);
        nextOpen=false;opened.Clear();offerCalls=0;
        window.ClickUpdateStripDetails();Pump();
        Check("e3 dialog's close button opens nothing, stores nothing",opened.Count==0&&MainWindow.skippedUpdateVersion==""&&window.UpdateStripVisible,"");
        // the strip's own tick box: stores at once, the strip stays
        offerCalls=0;
        var skipBox=Field<CheckBox>(window,"updateStripSkipBox");
        Check("e3 skip box: not focusable, no tab stop",!skipBox.Focusable&&!skipBox.IsTabStop,"");
        window.ClickUpdateStripSkip();window.UpdateLayout();Pump();
        Check("e3 tick: this version stored, box ticked",MainWindow.skippedUpdateVersion==Newer().ToString()&&window.UpdateStripSkipChecked,MainWindow.skippedUpdateVersion);
        Check("e3 tick: strip stays visible, no dialog",window.UpdateStripVisible&&offerCalls==0,"offer="+offerCalls);
        Check("e3 tick: caret and focus unchanged",editor.CaretIndex==caretBefore&&Object.ReferenceEquals(Focused(),focusBefore),caretBefore+" -> "+editor.CaretIndex);
        window.ClickUpdateStripSkip();window.UpdateLayout();Pump();
        Check("e3 untick: stored version cleared, box unticked, strip stays",MainWindow.skippedUpdateVersion==""&&!window.UpdateStripSkipChecked&&window.UpdateStripVisible,MainWindow.skippedUpdateVersion);
        // tick, then the close button: the strip is hidden and the version stays skipped
        window.ClickUpdateStripSkip();Pump();
        window.ClickUpdateStripClose();window.UpdateLayout();Pump();
        Check("e3 tick then close: strip hidden, version still stored",!window.UpdateStripVisible&&MainWindow.skippedUpdateVersion==Newer().ToString(),MainWindow.skippedUpdateVersion);
        Check("e3 tick then close: caret and focus unchanged",editor.CaretIndex==caretBefore&&Object.ReferenceEquals(Focused(),focusBefore),caretBefore+" -> "+editor.CaretIndex);
        // the next automatic check is silent for that version, a newer release shows the strip again
        string stored=MainWindow.skippedUpdateVersion;
        AutoRun(window,Yesterday(),true,stored,delegate{return Json(Newer());});
        Check("e3 after skip: next automatic check is silent",jsonCalls==1&&!window.UpdateStripVisible&&offerCalls==0,"calls="+jsonCalls);
        var later=new Version(Newer().Major,Newer().Minor,Newer().Build+1);
        AutoRun(window,Yesterday(),true,stored,delegate{return Json(later);});
        Check("e3 after skip: a newer release shows the strip again, box unticked",window.UpdateStripVisible&&window.UpdateStripVersion==later.ToString()&&!window.UpdateStripSkipChecked,window.UpdateStripVersion);
        window.HideUpdateStrip();
    }
    static void CaseSkipped(MainWindow window){
        // the skipped version is the latest release: the check runs, shows nothing
        bool done=AutoRun(window,Yesterday(),true,Newer().ToString(),delegate{return Json(Newer());});
        Check("e4 finished",done,"");
        Check("e4 skipped version: request made, no strip, no dialog",jsonCalls==1&&!window.UpdateStripVisible&&offerCalls==0&&noticeCalls==0,"calls="+jsonCalls+" strip="+window.UpdateStripVisible);
        Check("e4 date still recorded",MainWindow.updateCheckedDate==Today(),"");
        // a release older than the skipped one (a withdrawn release) stays quiet too
        AutoRun(window,Yesterday(),true,new Version(Newer().Major,Newer().Minor,Newer().Build+5).ToString(),delegate{return Json(Newer());});
        Check("e4 older than skipped: no strip",!window.UpdateStripVisible,"");
        // a release newer than the skipped one is announced again
        AutoRun(window,Yesterday(),true,Cur().ToString(),delegate{return Json(Newer());});
        Check("e4 newer than skipped: strip shown",window.UpdateStripVisible&&window.UpdateStripVersion==Newer().ToString(),window.UpdateStripVersion);
        Check("e4 newer than skipped: box unticked",!window.UpdateStripSkipChecked,"");
        Check("e4 skipped value untouched by the check",MainWindow.skippedUpdateVersion==Cur().ToString(),MainWindow.skippedUpdateVersion);
        window.HideUpdateStrip();
    }
    static void CaseManual(MainWindow window){
        UpdateChecker.ResetForProbe();
        MainWindow.skippedUpdateVersion=Newer().ToString();MainWindow.autoUpdateCheck=false;MainWindow.updateCheckedDate=Today();
        jsonCalls=0;offerCalls=0;noticeCalls=0;jsonFactory=delegate{return Json(Newer());};nextOpen=false;opened.Clear();
        int target=UpdateChecker.Completed+1;
        UpdateChecker.Run(window);WaitDone(target);
        Check("e5 manual check asks even with the automatic check off",jsonCalls==1,"calls="+jsonCalls);
        Check("e5 manual check shows the dialog for the skipped version",offerCalls==1&&lastOfferVersion==Newer().ToString(),"offer="+offerCalls);
        Check("e5 the manual dialog stores nothing and clears nothing",MainWindow.skippedUpdateVersion==Newer().ToString(),MainWindow.skippedUpdateVersion);
        Check("e5 manual check shows no strip",!window.UpdateStripVisible,"");
        Check("e5 manual check leaves the date alone",MainWindow.updateCheckedDate==Today(),"");
        // the same dialog for an up-to-date answer is a notice, not the offer
        offerCalls=0;noticeCalls=0;jsonFactory=delegate{return Json(Cur());};
        target=UpdateChecker.Completed+1;UpdateChecker.Run(window);WaitDone(target);
        Check("e5 up to date: notice, no offer",noticeCalls==1&&offerCalls==0&&!String.IsNullOrEmpty(lastNotice),"notice="+noticeCalls+" offer="+offerCalls);
        noticeCalls=0;jsonFactory=delegate{throw new InvalidOperationException("offline");};
        target=UpdateChecker.Completed+1;UpdateChecker.Run(window);WaitDone(target);
        Check("e5 failure: notice",noticeCalls==1&&offerCalls==0,"notice="+noticeCalls);
    }
    static void CaseOffFailureUpToDate(MainWindow window){
        bool done=AutoRun(window,Yesterday(),false,"",delegate{return Json(Newer());});
        Check("e6 setting off finished",done,"");
        Check("e6 setting off: no request, no strip, date untouched",jsonCalls==0&&!window.UpdateStripVisible&&MainWindow.updateCheckedDate==Yesterday(),"calls="+jsonCalls+" date="+MainWindow.updateCheckedDate);
        done=AutoRun(window,Yesterday(),true,"",delegate{throw new InvalidOperationException("offline");});
        Check("e6 failure finished",done,"");
        Check("e6 failure: attempted, date recorded",jsonCalls==1&&MainWindow.updateCheckedDate==Today(),"calls="+jsonCalls+" date="+MainWindow.updateCheckedDate);
        Check("e6 failure: nothing shown",!window.UpdateStripVisible&&offerCalls==0&&noticeCalls==0,"");
        // the same day again: no second attempt
        done=AutoRun(window,MainWindow.updateCheckedDate,true,"",delegate{throw new InvalidOperationException("offline");});
        Check("e6 failed day is not retried",jsonCalls==0,"calls="+jsonCalls);
        AutoRun(window,Yesterday(),true,"",delegate{return "{\"message\":\"rate limit\"}";});
        Check("e6 answer without a tag: silent",!window.UpdateStripVisible&&offerCalls==0&&noticeCalls==0&&MainWindow.updateCheckedDate==Today(),"");
        AutoRun(window,Yesterday(),true,"",delegate{return Json(Cur());});
        Check("e6 up to date: silent",jsonCalls==1&&!window.UpdateStripVisible&&offerCalls==0&&noticeCalls==0,"");
        AutoRun(window,Yesterday(),true,"",delegate{return Json(new Version(Cur().Major,Cur().Minor,Math.Max(0,Cur().Build-1)));});
        Check("e6 older release: silent",!window.UpdateStripVisible&&offerCalls==0&&noticeCalls==0,"");
    }
    // The window's menu bar is a local of its constructor: find it in the logical tree.
    static System.Collections.IEnumerable MenuBarItems(DependencyObject parent){
        foreach(object child in LogicalTreeHelper.GetChildren(parent)){
            var menu=child as Menu;if(menu!=null)return menu.Items;
            var element=child as DependencyObject;
            if(element!=null){var found=MenuBarItems(element);if(found!=null)return found;}
        }
        return null;
    }
    static MenuItem FindToggle(MainWindow window){
        foreach(object item in MenuBarItems(window)){
            var top=item as MenuItem;if(top==null||top.Header==null||top.Header.ToString().IndexOf("_H")<0)continue;
            foreach(object sub in top.Items){var entry=sub as MenuItem;if(entry!=null&&entry.Header!=null&&entry.Header.ToString()=="업데이트 자동 확인")return entry;}
        }
        return null;
    }
    // The setting is off unless the user turned it on, or the installer ticked it and the user never chose.
    static void CaseInstallerValue(MainWindow window){
        MainWindow.autoUpdateChoice=null;
        installerValue=1;Check("f no choice + installer 1 = on",MainWindow.autoUpdateCheck,"");
        installerValue=0;Check("f no choice + installer 0 = off",!MainWindow.autoUpdateCheck,"");
        installerValue=null;Check("f no choice + no installer value = off",!MainWindow.autoUpdateCheck,"");
        installerValue=7;Check("f no choice + odd installer value = off",!MainWindow.autoUpdateCheck,"");
        MainWindow.autoUpdateChoice=false;installerValue=1;Check("f choice off + installer 1 = off",!MainWindow.autoUpdateCheck,"");
        MainWindow.autoUpdateChoice=true;installerValue=0;Check("f choice on + installer 0 = on",MainWindow.autoUpdateCheck,"");
        installerValue=null;Check("f choice on + no installer value = on",MainWindow.autoUpdateCheck,"");
        // behaviour: the check follows the same rule
        installerValue=1;
        bool done=AutoRun(window,Yesterday(),null,"",delegate{return Json(Newer());});
        Check("f no choice + installer 1: the daily check runs and shows the strip",done&&jsonCalls==1&&window.UpdateStripVisible,"calls="+jsonCalls);
        installerValue=null;
        AutoRun(window,Yesterday(),null,"",delegate{return Json(Newer());});
        Check("f no choice + no installer value: no request",jsonCalls==0&&!window.UpdateStripVisible&&MainWindow.updateCheckedDate==Yesterday(),"calls="+jsonCalls);
        installerValue=1;
        AutoRun(window,Yesterday(),false,"",delegate{return Json(Newer());});
        Check("f choice off beats installer 1: no request",jsonCalls==0&&!window.UpdateStripVisible,"calls="+jsonCalls);
        window.HideUpdateStrip();
        // a stored choice: both values are written and read back
        var on=new ViewPreferences {AutoUpdateChoice=true};var off=new ViewPreferences {AutoUpdateChoice=false};
        Check("f on is stored too",on.Serialize().IndexOf("autoUpdateCheck=\"true\"")>=0&&off.Serialize().IndexOf("autoUpdateCheck=\"false\"")>=0,"");
        installerValue=null;
    }
    static void CaseFirstWindowOnly(MainWindow first,MainWindow second){
        UpdateChecker.ResetForProbe();
        MainWindow.updateCheckedDate=Yesterday();MainWindow.autoUpdateCheck=true;MainWindow.skippedUpdateVersion="";
        jsonCalls=0;offerCalls=0;jsonFactory=delegate{return Json(Newer());};first.HideUpdateStrip();second.HideUpdateStrip();
        int target=UpdateChecker.Completed+1;
        UpdateChecker.ScheduleAuto(first);UpdateChecker.ScheduleAuto(second);
        WaitDone(target);Pump();Pump();
        Check("e7 two windows, one request",jsonCalls==1,"calls="+jsonCalls);
        Check("e7 one finished check",UpdateChecker.Completed==target,"completed="+UpdateChecker.Completed+" target="+target);
        Check("e7 strip shown once (first window)",first.UpdateStripVisible&&!second.UpdateStripVisible,"first="+first.UpdateStripVisible+" second="+second.UpdateStripVisible);
        // the skip box in one window moves the box in every window that shows the same version; the strips stay
        first.ShowUpdateStrip(Cur(),Newer(),null);second.ShowUpdateStrip(Cur(),Newer(),null);
        first.ClickUpdateStripSkip();Pump();
        Check("e7 tick in one window ticks the other, stores the version, both strips stay",first.UpdateStripSkipChecked&&second.UpdateStripSkipChecked&&first.UpdateStripVisible&&second.UpdateStripVisible&&MainWindow.skippedUpdateVersion==Newer().ToString(),"first="+first.UpdateStripSkipChecked+" second="+second.UpdateStripSkipChecked+" skipped="+MainWindow.skippedUpdateVersion);
        second.ClickUpdateStripSkip();Pump();
        Check("e7 untick in the other window unticks the first and clears the version",!first.UpdateStripSkipChecked&&!second.UpdateStripSkipChecked&&MainWindow.skippedUpdateVersion=="","first="+first.UpdateStripSkipChecked+" second="+second.UpdateStripSkipChecked+" skipped="+MainWindow.skippedUpdateVersion);
        MainWindow.skippedUpdateVersion="";
        first.HideUpdateStrip();second.HideUpdateStrip();
    }
    // The real dialog: the link and the two buttons are there, no check box; the first button opens the page and stores nothing.
    static string dialogReport;static bool dialogSeen;
    static void CaseRealDialog(MainWindow window){
        UpdateChecker.OfferHook=null;
        dialogReport="";dialogSeen=false;opened.Clear();
        MainWindow.skippedUpdateVersion="";jsonFactory=delegate{return Json(Newer());};jsonCalls=0;
        var poll=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(80)};int ticks=0;
        poll.Tick+=delegate{
            ticks++;
            Window dialog=null;
            foreach(Window candidate in Application.Current.Windows){if(!(candidate is MainWindow)&&Object.ReferenceEquals(candidate.Owner,window)&&candidate.IsVisible){dialog=candidate;break;}}
            if(dialog==null){if(ticks>150){poll.Stop();dialogReport="no dialog appeared";}return;}
            poll.Stop();dialogSeen=true;
            var panel=dialog.Content as StackPanel;
            System.Windows.Documents.Hyperlink link=null;CheckBox box=null;var buttons=new List<Button>();
            if(panel!=null)foreach(object child in panel.Children){
                var block=child as TextBlock;
                if(block!=null)foreach(var inline in block.Inlines){var found=inline as System.Windows.Documents.Hyperlink;if(found!=null)link=found;}
                var check=child as CheckBox;if(check!=null)box=check;
                var wrap=child as WrapPanel;if(wrap!=null)foreach(object item in wrap.Children){var button=item as Button;if(button!=null)buttons.Add(button);}
            }
            dialogReport="link="+(link!=null)+" box="+(box!=null)+" buttons="+buttons.Count;
            if(link!=null)dialogReport+=" linkText="+new System.Windows.Documents.TextRange(link.ContentStart,link.ContentEnd).Text;
            if(box==null&&buttons.Count==2&&link!=null){
                dialogReport+=" first="+buttons[0].Content+" second="+buttons[1].Content+" firstDefault="+buttons[0].IsDefault+" secondCancel="+buttons[1].IsCancel;
                buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }else dialog.Close();
        };
        poll.Start();
        int target=UpdateChecker.Completed+1;
        UpdateChecker.Run(window);WaitDone(target);
        SetSeams();
        Check("e8 the dialog opened",dialogSeen,dialogReport);
        Check("e8 dialog has link, two buttons and no check box",dialogReport.IndexOf("link=True")>=0&&dialogReport.IndexOf("box=False")>=0&&dialogReport.IndexOf("buttons=2")>=0,dialogReport);
        Check("e8 link text is the release page",dialogReport.IndexOf("linkText=https://github.com/Lahai-Roi/MDNote/releases/tag/v"+Newer())>=0,dialogReport);
        Check("e8 first button is the default release button, second cancels",dialogReport.IndexOf("firstDefault=True")>=0&&dialogReport.IndexOf("secondCancel=True")>=0,dialogReport);
        Check("e8 the dialog stored nothing",MainWindow.skippedUpdateVersion=="",MainWindow.skippedUpdateVersion);
        Check("e8 first button opened the release page",opened.Count==1&&opened[0]=="https://github.com/Lahai-Roi/MDNote/releases/tag/v"+Newer(),String.Join(",",opened.ToArray()));
        Check("e8 the window has the keyboard back after the dialog",Field<SourceEditor>(window,"source")!=null,"");
    }
    static void CaseHelpMenu(MainWindow window){
        MenuItem help=null;
        foreach(object item in MenuBarItems(window)){var top=item as MenuItem;if(top!=null&&top.Header!=null&&top.Header.ToString().IndexOf("_H")>=0)help=top;}
        Check("e9 help menu found",help!=null,"");
        if(help==null)return;
        var order=new List<string>();
        foreach(object item in help.Items){var entry=item as MenuItem;if(entry!=null)order.Add(entry.Header.ToString());else order.Add("-");}
        Check("e9 order: update check, then the automatic item, one separator, About last",order.Count>=5&&order[order.Count-1]=="MDNote 정보"&&order[order.Count-2]=="-"&&order[order.Count-3]=="업데이트 자동 확인"&&order[order.Count-4]=="업데이트 확인",String.Join(" | ",order.ToArray()));
        bool noDouble=true;for(int i=1;i<order.Count;i++)if(order[i]=="-"&&order[i-1]=="-")noDouble=false;
        Check("e9 no double separator, none at the ends",noDouble&&order[0]!="-"&&order[order.Count-1]!="-",String.Join(" | ",order.ToArray()));
        MenuItem toggle=null;foreach(object item in help.Items){var entry=item as MenuItem;if(entry!=null&&entry.Header.ToString()=="업데이트 자동 확인")toggle=entry;}
        Check("e9 toggle is checkable and follows the setting",toggle!=null&&toggle.IsCheckable,"");
        if(toggle==null)return;
        MainWindow.autoUpdateCheck=true;toggle.IsChecked=true;
        toggle.IsChecked=false;toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("e9 clicking it off stores an explicit off",!MainWindow.autoUpdateCheck&&MainWindow.autoUpdateChoice==false,"");
        toggle.IsChecked=true;toggle.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check("e9 clicking it on stores an explicit on",MainWindow.autoUpdateCheck&&MainWindow.autoUpdateChoice==true,"");
    }

    // ---- PNGs of the real UI (a fake release 2.3.0 through the release-JSON seam). They add no Check: a failed
    // picture is a "shot note" line in the report and never changes PASS/FAIL.
    static void Note(string line){report.Append("shot note: ").Append(line).Append('\n');Trace("shot note: "+line);}
    static void SetDarkMode(MainWindow window,bool dark){
        typeof(MainWindow).GetMethod("SetDark",Members).Invoke(window,new object[]{dark});
        window.UpdateLayout();Pump();Pump();
    }
    // The client area of an element on the paper colour, 1x.
    static BitmapSource Photograph(FrameworkElement element,Brush back,int minHeight){return Photograph(element,back,minHeight,0);}
    // pad = the element's own outer margin (the dialog's panel has 24), drawn as background around it.
    static BitmapSource Photograph(FrameworkElement element,Brush back,int minHeight,int pad){
        int width=Math.Max(1,(int)Math.Ceiling(element.ActualWidth)+2*pad),height=Math.Max(minHeight,(int)Math.Ceiling(element.ActualHeight)+2*pad);
        var visual=new DrawingVisual();
        using(DrawingContext context=visual.RenderOpen()){
            if(back!=null)context.DrawRectangle(back,null,new Rect(0,0,width,height));
            context.DrawRectangle(new VisualBrush(element),null,new Rect(pad,pad,element.ActualWidth,element.ActualHeight));
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        return bitmap;
    }
    static void SavePng(BitmapSource bitmap,string path){
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(path))encoder.Save(stream);
        report.Append("shot ").Append(Path.GetFileName(path)).Append(' ').Append(bitmap.PixelWidth).Append('x').Append(bitmap.PixelHeight).Append('\n');
    }
    static Brush PaperOf(MainWindow window){return window.Resources["Paper"] as Brush;}
    // ---- the strip's x button: size against the tab's close button, hover trigger, equal gaps left and right of it
    static object MemberOf(object target,string name){
        Type type=target.GetType();
        FieldInfo field=type.GetField(name,Members);if(field!=null)return field.GetValue(target);
        PropertyInfo property=type.GetProperty(name,Members);return property==null?null:property.GetValue(target,null);
    }
    static Size TabCloseSize(MainWindow window){
        try{
            var tabs=(System.Collections.IList)typeof(MainWindow).GetField("documents",Members).GetValue(window);
            var item=MemberOf(tabs[0],"Item") as TabItem;
            var header=item==null?null:item.Header as StackPanel;
            var tabClose=header==null||header.Children.Count<2?null:header.Children[1] as Button;
            if(tabClose==null)return new Size(0,0);
            tabClose.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));
            Thickness margin=tabClose.Margin;/* DesiredSize includes the tab button's 9 px left margin; the strip's x is compared by its own box */
            return new Size(tabClose.DesiredSize.Width-margin.Left-margin.Right,tabClose.DesiredSize.Height-margin.Top-margin.Bottom);
        }catch(Exception ex){Note("tab close size: "+ex.GetType().Name+": "+ex.Message);return new Size(0,0);}
    }
    static bool Inked(byte[] px,int stride,int x,int y,int bgB,int bgG,int bgR){
        int at=y*stride+x*4;
        return Math.Abs(px[at]-bgB)+Math.Abs(px[at+1]-bgG)+Math.Abs(px[at+2]-bgR)>30;
    }
    // Centre of the inked pixels inside the box (edge coordinates, so a 1 px wide mark at column 5 has centre 5.5); null when nothing is inked.
    static double[] InkCentre(byte[] px,int stride,int left,int right,int top,int bottom,int bgB,int bgG,int bgR){
        int minX=int.MaxValue,maxX=-1,minY=int.MaxValue,maxY=-1;
        for(int y=top;y<bottom;y++)for(int x=left;x<right;x++)if(Inked(px,stride,x,y,bgB,bgG,bgR)){if(x<minX)minX=x;if(x>maxX)maxX=x;if(y<minY)minY=y;if(y>maxY)maxY=y;}
        return maxX<0?null:new double[]{(minX+maxX+1)/2.0,(minY+maxY+1)/2.0,maxX-minX+1,maxY-minY+1};
    }
    static bool ColumnInked(byte[] px,int stride,int x,int h,int bgB,int bgG,int bgR){
        for(int y=2;y<h-1;y++)if(Inked(px,stride,x,y,bgB,bgG,bgR))return true;
        return false;
    }
    // 4x nearest-neighbour crop of the strip's right end; the spans (x0,x1 in source pixels) are marked by a bar along the bottom.
    static BitmapSource ZoomRight(BitmapSource shot,int fromX,int[] redSpan,int[] greenSpan){
        int w=shot.PixelWidth,h=shot.PixelHeight,cw=w-fromX,scale=4;
        int stride=w*4;byte[] src=new byte[stride*h];shot.CopyPixels(src,stride,0);
        int zw=cw*scale,zh=h*scale,zs=zw*4;byte[] dst=new byte[zs*zh];
        for(int y=0;y<zh;y++)for(int x=0;x<zw;x++){
            int from=(y/scale)*stride+(fromX+x/scale)*4,to=y*zs+x*4;
            dst[to]=src[from];dst[to+1]=src[from+1];dst[to+2]=src[from+2];dst[to+3]=src[from+3];
        }
        for(int span=0;span<2;span++){
            int[] range=span==0?redSpan:greenSpan;if(range==null)continue;
            for(int y=zh-6;y<zh-2;y++)for(int x=(range[0]-fromX)*scale;x<(range[1]-fromX)*scale&&x<zw;x++){
                if(x<0)continue;int to=y*zs+x*4;
                dst[to]=span==0?(byte)40:(byte)40;dst[to+1]=span==0?(byte)40:(byte)170;dst[to+2]=span==0?(byte)230:(byte)40;dst[to+3]=255;
            }
        }
        return BitmapSource.Create(zw,zh,96,96,PixelFormats.Pbgra32,null,dst,zs);
    }
    static void StripCloseChecks(MainWindow window,bool dark,string outDir){
        string tag=dark?"dark":"light";
        var strip=Field<FrameworkElement>(window,"updateStrip") as Border;
        var row=strip==null?null:strip.Child as DockPanel;
        Button close=row==null||row.Children.Count<2?null:row.Children[0] as Button;
        CheckBox skip=row==null||row.Children.Count<2?null:row.Children[1] as CheckBox;
        if(close==null||skip==null){Check("g close button found ("+tag+")",false,"");return;}
        window.UpdateLayout();Pump();
        Size tab=TabCloseSize(window);
        Check("g x button is not smaller than the tab's close button ("+tag+")",tab.Width>0&&close.ActualWidth>=tab.Width&&close.ActualHeight>=tab.Height,"strip x "+close.ActualWidth+"x"+close.ActualHeight+", tab x "+tab.Width+"x"+tab.Height);
        Check("g x button stays unfocusable ("+tag+")",!close.Focusable,"");
        Check("g strip grew by at most 2 px (was 29) ("+tag+")",strip.ActualHeight<=31.01,"height "+strip.ActualHeight);
        Check("g x is vertically centred in the strip ("+tag+")",Math.Abs(close.TranslatePoint(new Point(0,close.ActualHeight/2),strip).Y-(strip.ActualHeight+1)/2)<=1,"centre "+close.TranslatePoint(new Point(0,close.ActualHeight/2),strip).Y+" of "+strip.ActualHeight);
        bool templateHover=false,styleHover=false;
        if(close.Template!=null)foreach(TriggerBase baseTrigger in close.Template.Triggers){var t=baseTrigger as Trigger;if(t==null||t.Property!=UIElement.IsMouseOverProperty)continue;foreach(Setter s in t.Setters)if(s.TargetName=="Surface"&&s.Property==Border.BackgroundProperty)templateHover=true;}
        if(close.Style!=null)foreach(TriggerBase baseTrigger in close.Style.Triggers){var t=baseTrigger as Trigger;if(t!=null&&t.Property==UIElement.IsMouseOverProperty)styleHover=true;}
        Check("g x button has a hover fill in its template and a hover ink in its style ("+tag+")",templateHover&&styleHover,"template="+templateHover+" style="+styleHover);
        BitmapSource shot=Photograph(strip,PaperOf(window),1);
        int w=shot.PixelWidth,h=shot.PixelHeight,stride=w*4;byte[] px=new byte[stride*h];shot.CopyPixels(px,stride,0);
        int bgAt=(h/2)*stride+(w-2)*4,bgB=px[bgAt],bgG=px[bgAt+1],bgR=px[bgAt+2];
        int skipLeft=(int)Math.Round(skip.TranslatePoint(new Point(0,0),strip).X),closeLeft=(int)Math.Round(close.TranslatePoint(new Point(0,0),strip).X);
        int closeRight=closeLeft+(int)Math.Round(close.ActualWidth),skipRight=skipLeft+(int)Math.Round(skip.ActualWidth);
        int textInk=-1;for(int x=closeLeft-1;x>=skipLeft;x--)if(ColumnInked(px,stride,x,h,bgB,bgG,bgR)){textInk=x;break;}
        int inkL=-1,inkR=-1;for(int x=closeLeft;x<closeRight&&x<w;x++)if(ColumnInked(px,stride,x,h,bgB,bgG,bgR)){if(inkL<0)inkL=x;inkR=x;}
        int closeTop=(int)Math.Round(close.TranslatePoint(new Point(0,0),strip).Y),closeBottom=closeTop+(int)Math.Round(close.ActualHeight);
        double boxCx=(closeLeft+closeRight)/2.0,boxCy=(closeTop+closeBottom)/2.0;
        double[] ink=InkCentre(px,stride,closeLeft,closeRight,closeTop,closeBottom,bgB,bgG,bgR);
        Check("g visible x is centred in its box within 0.5 px ("+tag+")",ink!=null&&Math.Abs(ink[0]-boxCx)<=0.5&&Math.Abs(ink[1]-boxCy)<=0.5,ink==null?"no ink":"box centre "+boxCx+","+boxCy+" ink centre "+ink[0]+","+ink[1]+" ink "+ink[2]+"x"+ink[3]);
        int boxLeft=closeLeft-skipRight,boxRight=w-closeRight;
        int inkGapLeft=inkL-textInk-1,inkGapRight=w-1-inkR;
        report.Append("g gaps (").Append(tag).Append("): strip ").Append(w).Append('x').Append(h).Append(", x button ").Append(closeLeft).Append("..").Append(closeRight).Append(" (").Append(close.ActualWidth).Append('x').Append(close.ActualHeight).Append("), text ink right ").Append(textInk).Append(", x ink ").Append(inkL).Append("..").Append(inkR).Append(" (").Append(inkR-inkL+1).Append(" wide), box gap left/right ").Append(boxLeft).Append('/').Append(boxRight).Append(", ink gap left/right ").Append(inkGapLeft).Append('/').Append(inkGapRight).Append('\n');
        Check("g space left and right of the x button box is equal within 1 px ("+tag+")",inkL>=0&&textInk>=0&&Math.Abs(boxLeft-boxRight)<=1,"left "+boxLeft+", right "+boxRight);
        Check("g space left and right of the visible x is equal within 1 px ("+tag+")",inkL>=0&&textInk>=0&&Math.Abs(inkGapLeft-inkGapRight)<=1,"left "+inkGapLeft+", right "+inkGapRight);
        int from=Math.Max(0,skipRight-70);
        int[] red=new int[]{textInk+1,inkL},green=new int[]{inkR+1,w};
        SavePng(ZoomRight(shot,from,null,null),Path.Combine(outDir,"update-strip-close-zoom-"+tag+".png"));
        SavePng(ZoomRight(shot,from,red,green),Path.Combine(outDir,"update-strip-close-gaps-"+tag+".png"));
        // hover: the pointer cannot be put on the button here, so the two visuals the hover triggers set are applied by hand
        var surface=close.Template==null?null:close.Template.FindName("Surface",close) as Border;
        var content=close.Template==null?null:close.Template.FindName("ButtonContent",close) as ContentPresenter;
        if(surface==null||content==null){Note("hover shot: template parts missing");return;}
        Brush oldFill=surface.Background;Transform oldTransform=content.RenderTransform;
        try{
            surface.Background=window.Resources["Hover"] as Brush;content.RenderTransform=new ScaleTransform(.96,.96);close.Foreground=window.Resources["Ink"] as Brush;
            window.UpdateLayout();Pump();
            BitmapSource hoverShot=Photograph(strip,PaperOf(window),1);
            byte[] hp=new byte[stride*h];hoverShot.CopyPixels(hp,stride,0);
            int hAt=((closeTop+closeBottom)/2)*stride+(closeLeft+2)*4;
            double[] hink=InkCentre(hp,stride,closeLeft+3,closeRight-3,closeTop+3,closeBottom-3,hp[hAt],hp[hAt+1],hp[hAt+2]);
            Check("g visible x is centred in its hover box within 0.5 px ("+tag+")",hink!=null&&Math.Abs(hink[0]-boxCx)<=0.5&&Math.Abs(hink[1]-boxCy)<=0.5,hink==null?"no ink":"box centre "+boxCx+","+boxCy+" ink centre "+hink[0]+","+hink[1]);
            SavePng(ZoomRight(hoverShot,from,null,null),Path.Combine(outDir,"update-strip-close-zoom-hover-"+tag+".png"));
        }finally{surface.Background=oldFill;content.RenderTransform=oldTransform;close.ClearValue(Control.ForegroundProperty);window.UpdateLayout();Pump();}
    }
    static void RunShots(string outDir,MainWindow window){
        var fake=new Version(2,3,0);
        try{
            SetSeams();UpdateChecker.OfferHook=null;
            SourceEditor editor=Field<SourceEditor>(window,"source");
            editor.Document.Text="# 업데이트 알림 예시\n\n짧은 예시 문서입니다. 새 버전이 나오면 아래 상태 줄 위에 알림이 뜹니다.\n\n- 첫째 항목\n- 둘째 항목\n\n**굵은 글씨**와 `코드`도 있습니다.\n";
            editor.Select(0,0);Pump();
            foreach(bool dark in new[]{false,true}){
                SetDarkMode(window,dark);
                AutoRun(window,Yesterday(),true,"",delegate{return Json(fake);});
                window.UpdateLayout();Pump();Pump();
                var root=window.Content as FrameworkElement;
                if(root==null||!window.UpdateStripVisible){Note("strip not visible ("+(dark?"dark":"light")+")");continue;}
                report.Append("strip text (ko, ").Append(dark?"dark":"light").Append("): ").Append(window.UpdateStripText).Append(" | box: ").Append(window.UpdateStripSkipText).Append('\n');
                if(dark){window.ClickUpdateStripSkip();window.UpdateLayout();Pump();}/* the dark shot shows the ticked box */
                SavePng(Photograph(root,PaperOf(window),1),Path.Combine(outDir,"update-strip-"+(dark?"dark":"light")+".png"));
                try{StripCloseChecks(window,dark,outDir);}catch(Exception ex){Check("g close checks run ("+(dark?"dark":"light")+")",false,ex.GetType().Name+": "+ex.Message+"\n"+ex.StackTrace);}
                window.HideUpdateStrip();window.UpdateLayout();Pump();
            }
            foreach(bool dark in new[]{false,true}){
                SetDarkMode(window,dark);
                ShotDialog(window,fake,Path.Combine(outDir,"update-dialog-"+(dark?"dark":"light")+".png"));
            }
            SetDarkMode(window,false);
            try{ShotHelpMenu(window,Path.Combine(outDir,"update-help-menu-light.png"));}catch(Exception ex){Note("help menu: "+ex.GetType().Name+": "+ex.Message);}
            window.ToolbarLanguage("en");Pump();Pump();
            ShotDialog(window,fake,Path.Combine(outDir,"update-dialog-en-light.png"));
            AutoRun(window,Yesterday(),true,"",delegate{return Json(fake);});
            window.UpdateLayout();Pump();Pump();
            var enRoot=window.Content as FrameworkElement;
            if(enRoot==null||!window.UpdateStripVisible)Note("strip not visible (en)");
            else{
                report.Append("strip text (en): ").Append(window.UpdateStripText).Append('\n');
                SavePng(Photograph(enRoot,PaperOf(window),1),Path.Combine(outDir,"update-strip-en-light.png"));
                window.HideUpdateStrip();window.UpdateLayout();Pump();
            }
        }catch(Exception ex){Note("shots: "+ex.GetType().Name+": "+ex.Message+"\n"+ex.StackTrace);}
        finally{
            UpdateChecker.DialogShownHook=null;
            try{window.ToolbarLanguage("ko");SetDarkMode(window,false);window.HideUpdateStrip();}catch(Exception){}
            SetSeams();
        }
    }
    static void ShotDialog(MainWindow window,Version fake,string path){
        bool shot=false;
        UpdateChecker.DialogShownHook=delegate(Window dialog){
            try{
                dialog.UpdateLayout();
                var content=dialog.Content as FrameworkElement;
                if(content!=null){SavePng(Photograph(content,dialog.Background,1,(int)Math.Round(content.Margin.Left)),path);shot=true;}
            }catch(Exception ex){Note("dialog photo: "+ex.GetType().Name+": "+ex.Message);}
            finally{dialog.Close();}
        };
        MainWindow.skippedUpdateVersion="";jsonFactory=delegate{return Json(fake);};jsonCalls=0;opened.Clear();
        UpdateChecker.ResetForProbe();
        int target=UpdateChecker.Completed+1;
        UpdateChecker.Run(window);WaitDone(target);
        UpdateChecker.DialogShownHook=null;
        if(!shot)Note("dialog not photographed: "+Path.GetFileName(path));
    }
    static Menu MenuBarOf(DependencyObject parent){
        foreach(object child in LogicalTreeHelper.GetChildren(parent)){
            var menu=child as Menu;if(menu!=null)return menu;
            var element=child as DependencyObject;
            if(element!=null){var found=MenuBarOf(element);if(found!=null)return found;}
        }
        return null;
    }
    static Popup PopupOf(DependencyObject parent){
        int count=VisualTreeHelper.GetChildrenCount(parent);
        for(int i=0;i<count;i++){
            DependencyObject child=VisualTreeHelper.GetChild(parent,i);
            var popup=child as Popup;if(popup!=null)return popup;
            var found=PopupOf(child);if(found!=null)return found;
        }
        return null;
    }
    // An open MenuItem's list lives in its own popup window, which RenderTargetBitmap on the main window does not
    // include: the picture is the window with the popup's content drawn at the Help header's place.
    static void ShotHelpMenu(MainWindow window,string path){
        Menu bar=MenuBarOf(window);MenuItem help=null;
        if(bar!=null)foreach(object item in bar.Items){var top=item as MenuItem;if(top!=null&&top.Header!=null&&top.Header.ToString().IndexOf("_H")>=0)help=top;}
        var root=window.Content as FrameworkElement;
        if(help==null||root==null){Note("help menu: Help item not found");return;}
        try{
            help.IsSubmenuOpen=true;window.UpdateLayout();Pump();Pump();
            Popup popup=help.Template==null?null:help.Template.FindName("PART_Popup",help) as Popup;
            if(popup==null)popup=PopupOf(help);
            var child=popup==null?null:popup.Child as FrameworkElement;
            if(child==null){Note("help menu: no popup content to photograph (popup="+(popup!=null)+")");return;}
            if(!popup.IsOpen){
                // the menu closes at once on this unfocused hidden window: lay the popup's content out by hand
                child.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
                child.Arrange(new Rect(0,0,child.DesiredSize.Width,child.DesiredSize.Height));
                Note("help menu: popup was not open; its content was measured and arranged by hand");
            }
            child.UpdateLayout();Pump();
            if(child.ActualWidth<1||child.ActualHeight<1){Note("help menu: popup content has no size");return;}
            Point at=help.TranslatePoint(new Point(0,help.ActualHeight),root);
            int width=(int)Math.Ceiling(root.ActualWidth),height=Math.Min((int)Math.Ceiling(root.ActualHeight),(int)Math.Ceiling(at.Y+child.ActualHeight+24));
            var visual=new DrawingVisual();
            using(DrawingContext context=visual.RenderOpen()){
                context.DrawRectangle(PaperOf(window),null,new Rect(0,0,root.ActualWidth,root.ActualHeight));
                context.DrawRectangle(new VisualBrush(root),null,new Rect(0,0,root.ActualWidth,root.ActualHeight));
                context.DrawRectangle(new VisualBrush(child),null,new Rect(at.X,at.Y,child.ActualWidth,child.ActualHeight));
            }
            var bitmap=new RenderTargetBitmap(Math.Max(1,width),Math.Max(1,height),96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
            SavePng(bitmap,path);
        }finally{help.IsSubmenuOpen=false;window.UpdateLayout();Pump();}
    }
}
}
