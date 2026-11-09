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
using System.Windows.Input;
using System.Windows.Threading;

namespace MDNote {
// --window-abuse-probe <dir>: seeded window/tab/menu/shortcut sequences on real offscreen windows,
// in both the Basic view (no caret-line markup reveal) and the Live view. Every view-level command
// (theme, zoom, language, raw view, overflow, find) must leave document text alone; a tab switch
// must keep each tab's text, caret, mode and undo history; closing a tab must never take another
// tab's text; the dirty flag and its title/caption markers must agree with the buffer; menu and
// F-key commands must reach the same action as their toolbar buttons; and a non-ko UI must not
// leave Korean text in menu headers, tooltips or tab captions.
//
// Dialogs: the save prompt is answered through the app's own queuedSaveChoices seam, never by a
// real modal wait. The WPF dialogs the commands themselves open (about, shortcuts, link text,
// custom table) are closed by a probe timer so their open/close path is exercised; native file /
// print / font / colour dialogs are not invoked. The clipboard, the user's settings, the recovery
// store and the desktop are never touched: fixtures are generated under the result folder only.
// Report: <dir>\window-abuse-probe.txt, window-abuse-probe-PASS.txt / window-abuse-probe-FAIL.txt.
internal static class WindowAbuseProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const long DefaultBudgetMilliseconds=260000;
    const long StepLimitMilliseconds=8000;
    const int DefaultSequenceSteps=36;
    const int MaxFailures=240;

    internal sealed class FailureInfo {
        internal int Seed;
        internal string Mode, Scenario, Step, Invariant, Expected, Actual, Steps;
    }
    internal sealed class TabSnapshot {
        internal object Tab;
        internal string Buffer, StoredText, Saved, Path;
        internal bool? TextMode;
        internal bool Editing, Recovered, Active, Dirty;
        internal int Caret;
    }
    internal sealed class WinSnapshot {
        internal string Buffer, Title, Language, ActiveCaption;
        internal int Caret, SelectionStart, SelectionLength, DocCount, TabIndex;
        internal bool Dirty, Editing, Dark, WordWrap, PlainText, PreviewChecked, SearchOpen, ReplaceOpen, OutlineVisible;
        internal double FontSize, LinePercent;
        internal bool CustomColor;
        internal List<string> TabTexts=new List<string>();
        internal List<bool> TabDirty=new List<bool>();
        internal string Flag(string name, bool value) { return name+"="+value.ToString(); }
    }
    internal sealed class ModalResult {
        internal bool Saw, Closed;
        internal string Title, Error;
    }

    static string outDir, fixturesDir, savesDir;
    static string fixtureA, fixtureB, fixtureC;
    static readonly StringBuilder reportBody=new StringBuilder();
    static readonly List<FailureInfo> failures=new List<FailureInfo>();
    static readonly Dictionary<string,int> opCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,int> checkCounts=new Dictionary<string,int>();
    static readonly Dictionary<string,int> failCounts=new Dictionary<string,int>();
    static readonly List<string> skipped=new List<string>();
    static readonly List<string> notes=new List<string>();
    static readonly List<string> stepNames=new List<string>();
    static readonly List<string> dispatcherNotes=new List<string>();
    static readonly List<MainWindow> tracked=new List<MainWindow>();
    static readonly Dictionary<string,FieldInfo> windowFields=new Dictionary<string,FieldInfo>();
    static readonly Dictionary<string,PropertyInfo> windowProperties=new Dictionary<string,PropertyInfo>();
    static readonly Dictionary<string,MethodInfo> windowMethods=new Dictionary<string,MethodInfo>();
    static readonly object reportGate=new object();
    static Stopwatch watch;
    static bool all, finished, budgetReached;
    static int dispatcherErrors, currentSeed, stepsRun, checksRun;
    static string currentMode="live", currentScenario="", currentStep="", lastDispatcherNote="";
    static long scenarioDeadline;
    static long budgetMilliseconds=DefaultBudgetMilliseconds;
    static long stepLimitMilliseconds=StepLimitMilliseconds;
    static int sequenceSteps=DefaultSequenceSteps;
    static int sequenceSeedFirst=1, sequenceSeedLast=2;
    static bool skipSequence;
    static string languageFilter;

    internal static int Run(string folder) {
        outDir=Path.GetFullPath(folder);
        Directory.CreateDirectory(outDir);
        string passFile=Path.Combine(outDir,"window-abuse-probe-PASS.txt");
        string failFile=Path.Combine(outDir,"window-abuse-probe-FAIL.txt");
        RemoveFile(passFile);
        RemoveFile(failFile);
        ReadOptions();
        failures.Clear(); opCounts.Clear(); checkCounts.Clear(); failCounts.Clear(); skipped.Clear();
        notes.Clear(); dispatcherNotes.Clear(); tracked.Clear();
        reportBody.Length=0; all=true; finished=false; budgetReached=false;
        dispatcherErrors=0; stepsRun=0; checksRun=0;
        watch=new Stopwatch(); watch.Start();
        bool oldSelf=Program.IsSelfTest;
        bool oldHide=false;
        try { oldHide=StaticField<bool>("hideMarkers"); } catch (Exception) { oldHide=false; }
        Dispatcher dispatcher=null;
        DispatcherUnhandledExceptionEventHandler handler=null;
        Exception fatal=null;
        try {
            Program.IsSelfTest=true;
            if (Application.Current==null) new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            dispatcher=Application.Current.Dispatcher;
            SetSynchronizationContext();
            handler=delegate(object sender,DispatcherUnhandledExceptionEventArgs args) { NoteDispatcher(args); };
            dispatcher.UnhandledException+=handler;
            Application.Current.DispatcherUnhandledException+=handler;
            StartWatchdog(budgetMilliseconds+20000);
            MakeFixtures();
            if (!skipSequence) {
                for (int seed=sequenceSeedFirst;seed<=sequenceSeedLast;seed++) {
                    int localSeed=seed;
                    RunScenario("sequence-basic-"+localSeed.ToString(CultureInfo.InvariantCulture),90000,delegate { RunSequence(localSeed,false); });
                    RunScenario("sequence-live-"+localSeed.ToString(CultureInfo.InvariantCulture),90000,delegate { RunSequence(localSeed,true); });
                }
            }
            RunScenario("views",70000,delegate { ScenarioViews(); });
            RunScenario("menus",90000,delegate { ScenarioMenus(); });
            RunScenario("tabs",90000,delegate { ScenarioTabs(); });
            RunScenario("shortcuts",120000,delegate { ScenarioShortcuts(); });
            RunScenario("overflow",90000,delegate { ScenarioOverflow(); });
            RunScenario("windows",90000,delegate { ScenarioWindows(); });
        } catch (Exception ex) {
            fatal=ex;
            all=false;
        } finally {
            try { finished=true; } catch (Exception) { }
            try { if (dispatcher!=null&&handler!=null) dispatcher.UnhandledException-=handler; } catch (Exception) { }
            try { if (Application.Current!=null&&handler!=null) Application.Current.DispatcherUnhandledException-=handler; } catch (Exception) { }
            try { CloseAllWindows(); } catch (Exception) { }
            try { if (StaticField<object>("hideMarkers")!=null) SetStaticField("hideMarkers",oldHide); } catch (Exception) { }
            Program.IsSelfTest=oldSelf;
            watch.Stop();
        }
        string text=ComposeReport(fatal);
        try {
            File.WriteAllText(Path.Combine(outDir,"window-abuse-probe.txt"),text,new UTF8Encoding(false));
            if (all&&!budgetReached) File.WriteAllText(passFile,"PASS\n",new UTF8Encoding(false));
            else File.WriteAllText(failFile,text,new UTF8Encoding(false));
        } catch (IOException) {
            return 1;
        }
        return all&&!budgetReached?0:1;
    }

    static void SetSynchronizationContext() {
        try {
            if (SynchronizationContext.Current==null)
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        } catch (Exception) { }
    }

    // Reduced runs for debugging: MDNOTE_WINDOW_ABUSE_STEPS, _SEED, _MODE, _BUDGET, _SKIP_SEQUENCE,
    // _LANGUAGE.
    static void ReadOptions() {
        budgetMilliseconds=DefaultBudgetMilliseconds;
        stepLimitMilliseconds=StepLimitMilliseconds;
        sequenceSteps=DefaultSequenceSteps;
        sequenceSeedFirst=1; sequenceSeedLast=2;
        skipSequence=false; languageFilter=null;
        string value=Environment.GetEnvironmentVariable("MDNOTE_WINDOW_ABUSE_STEPS");
        int number;
        if (!String.IsNullOrEmpty(value)&&Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)&&number>0) sequenceSteps=number;
        value=Environment.GetEnvironmentVariable("MDNOTE_WINDOW_ABUSE_SEED");
        if (!String.IsNullOrEmpty(value)&&Int32.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out number)&&number>0) { sequenceSeedFirst=number; sequenceSeedLast=number; }
        value=Environment.GetEnvironmentVariable("MDNOTE_WINDOW_ABUSE_BUDGET");
        long budget;
        if (!String.IsNullOrEmpty(value)&&Int64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out budget)&&budget>0) budgetMilliseconds=budget;
        value=Environment.GetEnvironmentVariable("MDNOTE_WINDOW_ABUSE_STEP_LIMIT");
        if (!String.IsNullOrEmpty(value)&&Int64.TryParse(value,NumberStyles.Integer,CultureInfo.InvariantCulture,out budget)&&budget>0) stepLimitMilliseconds=budget;
        value=Environment.GetEnvironmentVariable("MDNOTE_WINDOW_ABUSE_MODE");
        if (!String.IsNullOrEmpty(value)) currentMode=String.Equals(value,"basic",StringComparison.OrdinalIgnoreCase)?"basic":"live";
        if (Environment.GetEnvironmentVariable("MDNOTE_WINDOW_ABUSE_SKIP_SEQUENCE")=="1") skipSequence=true;
        value=Environment.GetEnvironmentVariable("MDNOTE_WINDOW_ABUSE_LANGUAGE");
        if (!String.IsNullOrEmpty(value)) languageFilter=value;
    }

    static void MakeFixtures() {
        fixturesDir=Path.Combine(outDir,"fixtures");
        savesDir=Path.Combine(outDir,"saves");
        Directory.CreateDirectory(fixturesDir);
        Directory.CreateDirectory(savesDir);
        fixtureA=WriteFixture("alpha.md","# Alpha heading\n\nAlpha body with **bold** and `code`.\n\n- one\n- two\n");
        fixtureB=WriteFixture("beta.txt","Plain text line one.\nPlain text line two.\n");
        fixtureC=WriteFixture("gamma.md","## Gamma\n\nGamma paragraph with a [link](https://example.invalid/).\n\n| A | B |\n| --- | --- |\n| C | D |\n");
    }
    static string WriteFixture(string name,string body) {
        string path=Path.Combine(fixturesDir,name);
        File.WriteAllText(path,body,new UTF8Encoding(false));
        return path;
    }
    static void RemoveFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { } }

    static void StartWatchdog(long milliseconds) {
        long start=Environment.TickCount;
        var thread=new Thread(delegate() {
            while (true) {
                Thread.Sleep(400);
                if (finished) return;
                if (unchecked(Environment.TickCount-start)>milliseconds) {
                    lock (reportGate) {
                        if (finished) return;
                        finished=true;
                        try {
                            reportBody.Append("\n## Watchdog\n- the probe did not finish within ").Append(milliseconds/1000)
                                .Append(" s; it was killed at scenario ").Append(currentScenario).Append(", step ").Append(currentStep).Append('\n');
                            File.WriteAllText(Path.Combine(outDir,"window-abuse-probe.txt"),ComposeReport(null),new UTF8Encoding(false));
                            File.WriteAllText(Path.Combine(outDir,"window-abuse-probe-FAIL.txt"),"watchdog timeout\n",new UTF8Encoding(false));
                        } catch (Exception) { }
                    }
                    Environment.Exit(2);
                }
            }
        });
        thread.IsBackground=true;
        thread.Name="window-abuse-watchdog";
        thread.Start();
    }

    static void RunScenario(string name,long budget,Action body) {
        currentScenario=name;
        currentStep="";
        stepNames.Clear();
        scenarioDeadline=watch.ElapsedMilliseconds+budget;
        long start=watch.ElapsedMilliseconds;
        try {
            body();
        } catch (Exception ex) {
            all=false;
            AppendReport("\nFAIL scenario "+name+" exception: "+OneLine(ex.ToString())+"\n");
        } finally {
            AppendReport("\n- scenario "+name+": "+(watch.ElapsedMilliseconds-start)+" ms, "+stepsRun+" steps so far\n");
        }
    }
    static void AppendReport(string text) { lock (reportGate) reportBody.Append(text); }

    static void Note(string text) { notes.Add(OneLine(text)); }

    // ---------------------------------------------------------------- result checks
    static bool Fail(string invariant,string step,string expected,string actual) {
        all=false;
        Bump(failCounts,invariant);
        if (failures.Count<MaxFailures) {
            var failure=new FailureInfo();
            failure.Seed=currentSeed;
            failure.Mode=currentMode;
            failure.Scenario=currentScenario;
            failure.Step=String.IsNullOrEmpty(step)?currentStep:step;
            failure.Invariant=invariant;
            failure.Expected=OneLine(expected);
            failure.Actual=OneLine(actual);
            failure.Steps=RecentSteps();
            failures.Add(failure);
        }
        return false;
    }
    static bool Check(string invariant,string step,bool ok,string expected,string actual) {
        checksRun++;
        Bump(checkCounts,invariant);
        if (ok) return true;
        return Fail(invariant,step,expected,actual);
    }
    static string RecentSteps() {
        int count=stepNames.Count;
        if (count==0) return "(none)";
        int first=Math.Max(0,count-24);
        var text=new StringBuilder();
        for (int i=first;i<count;i++) {
            if (text.Length>0) text.Append(" | ");
            text.Append(stepNames[i]);
        }
        return text.ToString();
    }
    static void Bump(Dictionary<string,int> map,string key) {
        int value;
        if (!map.TryGetValue(key,out value)) value=0;
        map[key]=value+1;
    }
    static void CheckBudget(string step) {
        if (watch.ElapsedMilliseconds>budgetMilliseconds) {
            budgetReached=true;
            all=false;
            throw new TimeoutException("probe budget reached at "+currentScenario+"/"+step);
        }
        if (watch.ElapsedMilliseconds>scenarioDeadline) {
            all=false;
            throw new TimeoutException("scenario "+currentScenario+" exceeded its budget at "+step);
        }
    }

    // Every action runs through here: an exception and a dispatcher exception are both a failure,
    // and a step that takes longer than the limit is recorded as a possible hang.
    static bool RunStep(string name,Action action) {
        currentStep=name;
        stepNames.Add(name);
        Bump(opCounts,name);
        stepsRun++;
        long start=watch.ElapsedMilliseconds;
        int dispatcherBefore=dispatcherErrors;
        Exception error=null;
        try {
            action();
        } catch (Exception ex) {
            error=ex;
        }
        try {
            Pump();
        } catch (Exception ex) {
            if (error==null) error=ex;
        }
        double elapsed=watch.ElapsedMilliseconds-start;
        bool ok=true;
        if (error!=null) ok=Check("step:no-exception",name,false,"no exception",ExceptionText(error))&&ok;
        if (dispatcherErrors>dispatcherBefore) ok=Check("step:no-dispatcher-exception",name,false,"no dispatcher exception",lastDispatcherNote)&&ok;
        if (elapsed>stepLimitMilliseconds) ok=Check("step:bounded",name,false,"step under "+stepLimitMilliseconds+" ms",elapsed.ToString(CultureInfo.InvariantCulture)+" ms")&&ok;
        CheckBudget(name);
        return ok;
    }

    // ---------------------------------------------------------------- dispatcher and report plumbing
    static void NoteDispatcher(DispatcherUnhandledExceptionEventArgs args) {
        dispatcherErrors++;
        string note=args.Exception==null?"unknown":args.Exception.GetType().Name+": "+OneLine(args.Exception.Message);
        if (args.Exception!=null&&args.Exception.StackTrace!=null) {
            string[] frames=args.Exception.StackTrace.Replace("\r","").Split('\n');
            note+=" @ "+OneLine(String.Join(" < ",frames,0,Math.Min(4,frames.Length)));
        }
        note=currentScenario+"/"+currentStep+": "+note;
        lastDispatcherNote=note;
        if (dispatcherNotes.Count<40) dispatcherNotes.Add(note);
        args.Handled=true;
    }

    static void Pump() {
        LongLineBench.Idle();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate { }));
        LongLineBench.Idle();
    }
    static void PumpMs(int milliseconds) {
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.ApplicationIdle);
        timer.Interval=TimeSpan.FromMilliseconds(milliseconds);
        timer.Tick+=delegate { timer.Stop(); frame.Continue=false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Pump();
    }

    // ---------------------------------------------------------------- reflection
    static FieldInfo RequireWindowField(string name) {
        FieldInfo field;
        if (windowFields.TryGetValue(name,out field)) return field;
        field=typeof(MainWindow).GetField(name,Members);
        if (field==null) throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");
        windowFields[name]=field;
        return field;
    }
    static PropertyInfo RequireWindowProperty(string name) {
        PropertyInfo property;
        if (windowProperties.TryGetValue(name,out property)) return property;
        property=typeof(MainWindow).GetProperty(name,Members);
        if (property==null) throw new MissingMemberException("probe: MainWindow has no property '"+name+"'");
        windowProperties[name]=property;
        return property;
    }
    static MethodInfo RequireWindowMethod(string name) {
        MethodInfo method;
        if (windowMethods.TryGetValue(name,out method)) return method;
        method=typeof(MainWindow).GetMethod(name,Members);
        if (method==null) throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");
        windowMethods[name]=method;
        return method;
    }
    // Overloads (Render(), Save(bool), ...) share a name, so the argument count picks the method.
    static MethodInfo RequireWindowMethod(string name,int argumentCount) {
        string key=name+"/"+argumentCount.ToString(CultureInfo.InvariantCulture);
        MethodInfo method;
        if (windowMethods.TryGetValue(key,out method)) return method;
        MethodInfo[] methods=typeof(MainWindow).GetMethods(Members);
        method=null;
        for (int i=0;i<methods.Length;i++) {
            if (!String.Equals(methods[i].Name,name,StringComparison.Ordinal)) continue;
            if (methods[i].IsGenericMethodDefinition) continue;
            if (methods[i].GetParameters().Length!=argumentCount) continue;
            method=methods[i];
            break;
        }
        if (method==null) throw new MissingMemberException("probe: MainWindow has no method '"+name+"' with "+argumentCount+" arguments");
        windowMethods[key]=method;
        return method;
    }
    static T Field<T>(MainWindow window,string name) { return (T)RequireWindowField(name).GetValue(window); }
    static bool FieldBool(MainWindow window,string name) { return (bool)RequireWindowField(name).GetValue(window); }
    static object FieldObject(MainWindow window,string name) { return RequireWindowField(name).GetValue(window); }
    static void SetField(MainWindow window,string name,object value) { RequireWindowField(name).SetValue(window,value); }
    static T StaticField<T>(string name) { return (T)RequireWindowField(name).GetValue(null); }
    static void SetStaticField(string name,object value) { RequireWindowField(name).SetValue(null,value); }
    static T Prop<T>(MainWindow window,string name) { return (T)RequireWindowProperty(name).GetValue(window,null); }
    static object PropObject(MainWindow window,string name) { return RequireWindowProperty(name).GetValue(window,null); }
    static object Call(MainWindow window,string name,params object[] args) {
        return RequireWindowMethod(name,args.Length).Invoke(window,args);
    }
    static object TabField(object tab,string name) {
        FieldInfo field=tab.GetType().GetField(name,Members);
        if (field==null) throw new MissingMemberException("probe: DocumentTab has no field '"+name+"'");
        return field.GetValue(tab);
    }
    static void SetTabField(object tab,string name,object value) {
        FieldInfo field=tab.GetType().GetField(name,Members);
        if (field==null) throw new MissingMemberException("probe: DocumentTab has no field '"+name+"'");
        field.SetValue(tab,value);
    }
    static string TabString(object tab,string name) {
        object value=TabField(tab,name);
        return value==null?"":(string)value;
    }

    // ---------------------------------------------------------------- window and tab helpers
    static MainWindow NewWindow(double width,double height) {
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Width=width,Height=height,Left=-20000,Top=-20000};
        tracked.Add(window);
        window.Show();
        window.UpdateLayout();
        Pump();
        return window;
    }
    static void MarkSaved(MainWindow window) {
        if (window==null) return;
        try {
            Call(window,"CaptureTab");
            Flush(window);
            string text=Prop<string>(window,"text");
            IList documents=Documents(window);
            for (int i=0;i<documents.Count;i++) {
                object tab=documents[i];
                SetTabField(tab,"Text",text);
                SetTabField(tab,"Saved",text);
                SetTabField(tab,"Recovered",false);
            }
            SetField(window,"saved",text);
            SetField(window,"recoveredDocument",false);
        } catch (Exception) { }
    }
    static void CloseQuiet(MainWindow window) {
        if (window==null) return;
        try {
            MarkSaved(window);
            if (window.IsLoaded) window.Close();
        } catch (Exception) { }
    }
    static void CloseAllWindows() {
        if (Application.Current==null) return;
        var open=new List<Window>();
        foreach (Window candidate in Application.Current.Windows) open.Add(candidate);
        for (int pass=0;pass<2;pass++) {
            for (int i=0;i<open.Count;i++) {
                Window window=open[i];
                try {
                    if (!window.IsLoaded) continue;
                    if (pass==0&&window is MainWindow) MarkSaved((MainWindow)window);
                    if (pass==0&&!(window is MainWindow)) { window.Close(); continue; }
                    if (pass==1) window.Close();
                } catch (Exception) { }
            }
        }
    }
    static void Flush(MainWindow window) { try { Call(window,"FlushDocumentChanges"); } catch (Exception) { } }
    static SourceEditor Source(MainWindow window) { return Field<SourceEditor>(window,"source"); }
    static string Buffer(MainWindow window) {
        SourceEditor source=Source(window);
        return source==null||source.Document==null?"":source.Document.Text;
    }
    static IList Documents(MainWindow window) { return (IList)FieldObject(window,"documents"); }
    static object ActiveDocument(MainWindow window) { return FieldObject(window,"activeDocument"); }
    static TabControl Tabs(MainWindow window) { return Field<TabControl>(window,"documentTabs"); }
    static Menu TopMenu(MainWindow window) { return Field<Menu>(window,"topMenu"); }
    static Button ButtonField(MainWindow window,string name) { return (Button)FieldObject(window,name); }
    static MenuItem MenuItemField(MainWindow window,string name) { return (MenuItem)FieldObject(window,name); }
    static bool IsVisible(UIElement element) { return element!=null&&element.Visibility==Visibility.Visible; }
    static object TabOf(TabItem item) { return item==null?null:item.Tag; }
    static string TabBufferFor(MainWindow window,object tab) {
        if (tab==null) return "";
        if (Object.ReferenceEquals(tab,ActiveDocument(window))) return Buffer(window);
        SourceEditor source=(SourceEditor)TabField(tab,"Source");
        if (source!=null&&source.Document!=null) return source.Document.Text;
        return TabString(tab,"Text");
    }
    static string TabCaption(object tab) {
        if (tab==null) return "";
        TabItem item=(TabItem)TabField(tab,"Item");
        if (item==null) return "";
        var header=item.Header as StackPanel;
        if (header==null||header.Children.Count==0) return "";
        var block=header.Children[0] as TextBlock;
        return block==null?"":block.Text;
    }
    static void SelectTab(MainWindow window,int index) {
        TabControl tabs=Tabs(window);
        if (tabs==null||index<0||index>=tabs.Items.Count) return;
        tabs.SelectedIndex=index;
    }
    static void Type(MainWindow window,string text) { Source(window).TextArea.PerformTextInput(text); }
    static void Select(MainWindow window,int start,int length) { Source(window).Select(start,length); }
    static void PlaceCaret(MainWindow window,int offset) { Source(window).Select(Math.Max(0,Math.Min(offset,Buffer(window).Length)),0); }
    static void Press(FrameworkElement control) {
        var button=control as System.Windows.Controls.Primitives.ButtonBase;
        if (button!=null) { button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); return; }
        var item=control as MenuItem;
        if (item!=null) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
    static void ClickItem(MenuItem item) {
        if (item==null) return;
        if (item.IsCheckable) item.IsChecked=!(item.IsChecked==true);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }
    static bool RaiseKey(MainWindow window,Key key) {
        PresentationSource presentation=PresentationSource.FromVisual(window);
        if (presentation==null) return false;
        var args=new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,key) {RoutedEvent=Keyboard.PreviewKeyDownEvent};
        window.RaiseEvent(args);
        return args.Handled;
    }
    static void SaveChoices(MainWindow window,int count,MainWindow.SaveChoice choice) {
        var queue=(Queue<MainWindow.SaveChoice>)FieldObject(window,"queuedSaveChoices");
        for (int i=0;i<count;i++) queue.Enqueue(choice);
    }
    static int SaveChoiceCount(MainWindow window) {
        return ((Queue<MainWindow.SaveChoice>)FieldObject(window,"queuedSaveChoices")).Count;
    }
    static void ClearSaveChoices(MainWindow window) {
        ((Queue<MainWindow.SaveChoice>)FieldObject(window,"queuedSaveChoices")).Clear();
    }
    static string ExceptionText(Exception error) {
        if (error==null) return "";
        Exception actual=error;
        while (actual is TargetInvocationException&&actual.InnerException!=null) actual=actual.InnerException;
        string text=actual.GetType().Name+": "+OneLine(actual.Message);
        if (actual.StackTrace!=null) {
            string[] frames=actual.StackTrace.Replace("\r","").Split('\n');
            text+=" @ "+OneLine(String.Join(" < ",frames,0,Math.Min(4,frames.Length)));
        }
        return text;
    }
    static string OneLine(string value) {
        if (value==null) return "<null>";
        string text=value.Replace("\r\n","\\n").Replace("\r","\\n").Replace("\n","\\n").Replace("\t","\\t");
        return text.Length<=700?text:text.Substring(0,700)+"...";
    }
    static string Diff(string expected,string actual) {
        if (expected==null) expected="<null>";
        if (actual==null) actual="<null>";
        int at=0,limit=Math.Min(expected.Length,actual.Length);
        while (at<limit&&expected[at]==actual[at]) at++;
        return "firstDiff="+at.ToString(CultureInfo.InvariantCulture)+", expected=["+Context(expected,at)+"], actual=["+Context(actual,at)+"]";
    }
    static string Context(string value,int at) {
        int start=Math.Max(0,at-24),end=Math.Min(value.Length,at+24);
        return OneLine(value.Substring(start,end-start));
    }

    // ---------------------------------------------------------------- snapshots and invariants
    static List<TabSnapshot> CaptureTabs(MainWindow window) {
        var list=new List<TabSnapshot>();
        try {
            IList documents=Documents(window);
            object active=ActiveDocument(window);
            for (int i=0;i<documents.Count;i++) {
                object tab=documents[i];
                var snapshot=new TabSnapshot();
                snapshot.Tab=tab;
                snapshot.Active=Object.ReferenceEquals(tab,active);
                snapshot.Buffer=TabBufferFor(window,tab);
                snapshot.StoredText=TabString(tab,"Text");
                snapshot.Saved=TabString(tab,"Saved");
                snapshot.Path=TabString(tab,"Path");
                snapshot.TextMode=(bool?)TabField(tab,"TextMode");
                snapshot.Editing=(bool)TabField(tab,"Editing");
                snapshot.Recovered=(bool)TabField(tab,"Recovered");
                snapshot.Caret=snapshot.Active?Source(window).CaretIndex:-1;
                snapshot.Dirty=TabDirty(window,tab);
                list.Add(snapshot);
            }
        } catch (Exception ex) {
            Fail("probe:snapshot",currentStep,"tabs readable",ExceptionText(ex));
        }
        return list;
    }
    static bool TabDirty(MainWindow window,object tab) {
        if (Object.ReferenceEquals(tab,ActiveDocument(window))) return Prop<bool>(window,"HasUnsavedChanges");
        return (bool)TabField(tab,"Recovered")||!String.Equals(TabString(tab,"Text"),TabString(tab,"Saved"),StringComparison.Ordinal);
    }
    static WinSnapshot Snap(MainWindow window) {
        var snap=new WinSnapshot();
        SourceEditor source=Source(window);
        snap.Buffer=source==null||source.Document==null?"":source.Document.Text;
        snap.Caret=source==null?-1:source.CaretIndex;
        snap.SelectionStart=source==null?-1:source.SelectionStart;
        snap.SelectionLength=source==null?-1:source.SelectionLength;
        snap.Dirty=Prop<bool>(window,"HasUnsavedChanges");
        snap.Editing=FieldBool(window,"editing");
        snap.Dark=FieldBool(window,"dark");
        snap.WordWrap=FieldBool(window,"wordWrap");
        snap.PlainText=Prop<bool>(window,"PlainText");
        var preview=Field<CheckBox>(window,"preview");
        snap.PreviewChecked=preview!=null&&preview.IsChecked==true;
        var searchHost=Field<Border>(window,"searchHost");
        snap.SearchOpen=searchHost!=null&&searchHost.Visibility==Visibility.Visible;
        var replaceRow=Field<StackPanel>(window,"replaceRow");
        snap.ReplaceOpen=replaceRow!=null&&replaceRow.Visibility==Visibility.Visible;
        var outline=Field<TreeView>(window,"outline");
        snap.OutlineVisible=outline!=null&&outline.Visibility==Visibility.Visible;
        snap.FontSize=(double)FieldObject(window,"fontSize");
        snap.LinePercent=(double)FieldObject(window,"linePercent");
        snap.CustomColor=((System.Windows.Media.Color?)FieldObject(window,"customColor")).HasValue;
        snap.Language=(string)FieldObject(window,"language");
        snap.Title=window.Title;
        IList documents=Documents(window);
        snap.DocCount=documents.Count;
        TabControl tabs=Tabs(window);
        snap.TabIndex=tabs==null?-1:tabs.SelectedIndex;
        for (int i=0;i<documents.Count;i++) {
            object tab=documents[i];
            snap.TabTexts.Add(TabBufferFor(window,tab));
            snap.TabDirty.Add(TabDirty(window,tab));
        }
        snap.ActiveCaption=TabCaption(ActiveDocument(window));
        return snap;
    }
    static TabSnapshot FindTab(List<TabSnapshot> list,object tab) {
        for (int i=0;i<list.Count;i++) if (Object.ReferenceEquals(list[i].Tab,tab)) return list[i];
        return null;
    }
    static bool SameTabSet(List<TabSnapshot> before,List<TabSnapshot> after) {
        if (before.Count!=after.Count) return false;
        for (int i=0;i<before.Count;i++) if (FindTab(after,before[i].Tab)==null) return false;
        return true;
    }
    static string DescribeTabs(List<TabSnapshot> tabs) {
        var text=new StringBuilder();
        for (int i=0;i<tabs.Count;i++) {
            if (i>0) text.Append(" | ");
            text.Append('#').Append(i).Append(tabs[i].Active?"*":" ").Append(" len=").Append(tabs[i].Buffer.Length)
                .Append(" dirty=").Append(tabs[i].Dirty).Append(" text=[").Append(OneLine(tabs[i].Buffer)).Append(']');
        }
        return text.ToString();
    }
    static void CompareViewSnapshots(List<TabSnapshot> before,List<TabSnapshot> after,string step) {
        Check("tabs:count",step,SameTabSet(before,after),"the same tabs",DescribeTabs(after));
        if (!SameTabSet(before,after)) return;
        for (int i=0;i<before.Count;i++) {
            TabSnapshot old=before[i];
            TabSnapshot now=FindTab(after,old.Tab);
            Check("view:text",step,String.Equals(old.Buffer,now.Buffer),"tab #"+i.ToString(CultureInfo.InvariantCulture)+" buffer unchanged",
                Diff(old.Buffer,now.Buffer));
            Check("view:dirty",step,old.Dirty==now.Dirty,"tab #"+i.ToString(CultureInfo.InvariantCulture)+" dirty flag unchanged",
                "before="+old.Dirty+", after="+now.Dirty);
            if (old.Active&&now.Active) {
                Check("view:caret",step,old.Caret==now.Caret,"active caret offset unchanged",
                    "before="+old.Caret+", after="+now.Caret+", bufferLen="+now.Buffer.Length);
            }
        }
    }
    // View-level commands may not change text or the tab set. Caret is checked only for commands where
    // the app documents its keep (theme/zoom/language/raw view); callers pass checkCaret=false for the
    // operations that may legitimately move it (find opening focuses the query box, for example).
    static bool ViewStep(MainWindow window,string name,Action action,bool checkCaret) {
        List<TabSnapshot> before=CaptureTabs(window);
        bool ok=RunStep(name,action);
        Flush(window);
        List<TabSnapshot> after=CaptureTabs(window);
        if (checkCaret) CompareViewSnapshots(before,after,name);
        else {
            Check("tabs:count",name,SameTabSet(before,after),"the same tabs",DescribeTabs(after));
            if (SameTabSet(before,after)) {
                for (int i=0;i<before.Count;i++) {
                    TabSnapshot now=FindTab(after,before[i].Tab);
                    Check("view:text",name,String.Equals(before[i].Buffer,now.Buffer),"tab #"+i.ToString(CultureInfo.InvariantCulture)+" buffer unchanged",
                        Diff(before[i].Buffer,now.Buffer));
                    Check("view:dirty",name,before[i].Dirty==now.Dirty,"tab #"+i.ToString(CultureInfo.InvariantCulture)+" dirty flag unchanged",
                        "before="+before[i].Dirty+", after="+now.Dirty);
                }
            }
        }
        return ok;
    }
    static void CheckMirror(MainWindow window,string step) {
        Flush(window);
        string buffer=Buffer(window);
        string mirror=Prop<string>(window,"text");
        Check("buffer:mirror",step,String.Equals(buffer,mirror,StringComparison.Ordinal),"text mirror equals the buffer",Diff(buffer,mirror));
        string saved=(string)FieldObject(window,"saved");
        bool recovered=FieldBool(window,"recoveredDocument");
        bool dirty=Prop<bool>(window,"HasUnsavedChanges");
        bool want=recovered||!String.Equals(buffer,saved,StringComparison.Ordinal);
        Check("dirty:buffer",step,dirty==want,"dirty flag follows the buffer vs saved","dirty="+dirty+", want="+want+
            ", bufferLen="+buffer.Length+", savedLen="+saved.Length+", recovered="+recovered);
    }
    static void CheckDirtyMarkers(MainWindow window,string step) {
        bool dirty=Prop<bool>(window,"HasUnsavedChanges");
        Check("dirty:title",step,window.Title.StartsWith("* ",StringComparison.Ordinal)==dirty,"title marker follows dirty",
            "dirty="+dirty+", title=["+OneLine(window.Title)+"]");
        string caption=TabCaption(ActiveDocument(window));
        Check("dirty:caption",step,caption.StartsWith("• ",StringComparison.Ordinal)==dirty,"tab caption marker follows dirty",
            "dirty="+dirty+", caption=["+OneLine(caption)+"]");
    }
    static void CheckTabIntegrity(MainWindow window,string step) {
        try {
            IList documents=Documents(window);
            TabControl tabs=Tabs(window);
            object active=ActiveDocument(window);
            bool ok=documents.Count>=1&&active!=null&&tabs!=null&&tabs.Items.Count==documents.Count;
            string detail="documents="+documents.Count+", items="+(tabs==null?-1:tabs.Items.Count)+", active="+(active==null?"null":"yes");
            if (ok) {
                bool selected=Object.ReferenceEquals(TabOf(tabs.SelectedItem as TabItem),active);
                ok=selected;
                detail+=", selected="+selected;
                for (int i=0;i<documents.Count;i++) {
                    object tag=TabOf(tabs.Items[i] as TabItem);
                    if (!Object.ReferenceEquals(tag,documents[i])) { ok=false; detail+=", tab "+i+" tag mismatch"; break; }
                }
            }
            Check("tabs:integrity",step,ok,"one active document with one tab per document",detail);
            bool shouldShow=documents.Count>1||FieldBool(window,"openInTabs");
            bool shown=tabs!=null&&tabs.Visibility==Visibility.Visible;
            Check("tabs:visibility",step,shown==shouldShow,"tab strip visible exactly when tabs exist","documents="+documents.Count+
                ", openInTabs="+FieldBool(window,"openInTabs")+", shown="+shown);
            for (int i=0;i<documents.Count;i++) {
                object tab=documents[i];
                if (Object.ReferenceEquals(tab,active)) {
                    SourceEditor activeSource=(SourceEditor)TabField(tab,"Source");
                    if (activeSource!=null) Check("tabs:active-source",step,Object.ReferenceEquals(activeSource,Source(window)),
                        "the active tab's editor is the window editor","different editor for tab "+i);
                    continue;
                }
                SourceEditor source=(SourceEditor)TabField(tab,"Source");
                string buffer=source!=null&&source.Document!=null?source.Document.Text:TabString(tab,"Text");
                Check("tabs:buffer",step,String.Equals(buffer,TabString(tab,"Text"),StringComparison.Ordinal),
                    "inactive tab #"+i.ToString(CultureInfo.InvariantCulture)+" buffer equals its captured text",Diff(TabString(tab,"Text"),buffer));
            }
        } catch (Exception ex) {
            Fail("tabs:integrity",step,"tab structures readable",ExceptionText(ex));
        }
    }
    static bool ContainsTab(MainWindow window,object tab) {
        IList documents=Documents(window);
        for (int i=0;i<documents.Count;i++) if (Object.ReferenceEquals(documents[i],tab)) return true;
        return false;
    }
    static Button TabCloseButton(object tab) {
        TabItem item=(TabItem)TabField(tab,"Item");
        var header=item==null?null:item.Header as StackPanel;
        if (header==null||header.Children.Count<2) return null;
        return header.Children[1] as Button;
    }
    static void ClickTabClose(MainWindow window,object tab) {
        Button close=TabCloseButton(tab);
        if (close==null) throw new InvalidOperationException("probe: tab close button is missing");
        close.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
    // The strip's ScrollViewer lives in the TabControl template, so it is found in the visual tree.
    static ScrollViewer InnerScrollViewer(DependencyObject root) {
        if (root==null) return null;
        var scroll=root as ScrollViewer;
        if (scroll!=null) return scroll;
        int count=System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i=0;i<count;i++) {
            var found=InnerScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root,i));
            if (found!=null) return found;
        }
        return null;
    }
    // A tab switch replaces the focused editor; the active one (or its TextArea, which
    // SourceEditor.OnGotKeyboardFocus forwards to) must own the keyboard afterwards.
    static bool SourceHasKeyboardFocus(MainWindow window) {
        SourceEditor source=Source(window);
        if (source==null||!source.IsKeyboardFocusWithin) return false;
        IInputElement focused=Keyboard.FocusedElement;
        if (focused==null) return source.TextArea.IsKeyboardFocusWithin;
        return Object.ReferenceEquals(focused,source)||Object.ReferenceEquals(focused,source.TextArea)||source.TextArea.IsKeyboardFocusWithin;
    }
    static string FocusState(MainWindow window) {
        IInputElement focused=Keyboard.FocusedElement;
        SourceEditor source=Source(window);
        return "focused="+(focused==null?"null":focused.GetType().Name)
            +", sourceFocus="+(source!=null&&source.IsKeyboardFocusWithin)
            +", windowActive="+window.IsActive;
    }
    // Focus assertions need a real keyboard focus: an offscreen window whose activation the desktop
    // refuses can leave this run without one, so a focus step first proves the editor can take it and
    // records a note (instead of failing the product check) when it cannot.
    static bool focusChecks;
    static bool FocusChecksAvailable(MainWindow window) {
        if (window==null) return false;
        window.Activate(); Pump();
        Source(window).Focus(); Keyboard.Focus(Source(window).TextArea); Pump();
        focusChecks=SourceHasKeyboardFocus(window);
        if (!focusChecks) Note("focus checks skipped in this run: "+FocusState(window));
        return focusChecks;
    }
    static void ResetBaseline(MainWindow window,string text,int caret) {
        // One document only; extra tabs are closed through the app's own CloseTab (Discard) path.
        for (int guard=0;guard<10&&Documents(window).Count>1;guard++) {
            object extra=Documents(window)[Documents(window).Count-1];
            SaveChoices(window,1,MainWindow.SaveChoice.Discard);
            try { Call(window,"CloseTab",new object[] {extra}); } catch (Exception) { }
            ClearSaveChoices(window);
            Pump();
        }
        if (FieldBool(window,"dark")) Call(window,"SetDark",new object[] {false});
        if (!FieldBool(window,"wordWrap")) Call(window,"SetWrap",new object[] {true});
        string family=(string)FieldObject(window,"family");
        if (Math.Abs((double)FieldObject(window,"fontSize")-16)>0.01) Call(window,"SetBaseFont",new object[] {family,16.0});
        var preview=Field<CheckBox>(window,"preview");
        if (preview!=null&&preview.IsChecked==true) { preview.IsChecked=false; Pump(); }
        var searchHost=Field<Border>(window,"searchHost");
        if (searchHost!=null&&searchHost.Visibility==Visibility.Visible) Call(window,"CloseSearch");
        var outline=Field<TreeView>(window,"outline");
        if (outline!=null&&outline.Visibility==Visibility.Visible) {
            Button outlineButton=ButtonField(window,"outlineButton");
            if (outlineButton!=null) Press(outlineButton);
        }
        if (!String.Equals((string)FieldObject(window,"language"),"ko",StringComparison.Ordinal)) {
            SetField(window,"language","ko");
            Call(window,"ApplyLanguage");
        }
        SetField(window,"path","");
        SetField(window,"textMode",null);
        SetField(window,"customColor",null);
        SetField(window,"numberedList",false);
        SetField(window,"lastHeadingLevel",2);
        SetField(window,"recoveredDocument",false);
        Call(window,"SetText",new object[] {text});
        Call(window,"SetMode",new object[] {true});
        SetField(window,"saved",text);
        Call(window,"UpdateTitle");
        Call(window,"FlushDocumentChanges");
        Source(window).Select(Math.Max(0,Math.Min(caret,Buffer(window).Length)),0);
        Pump();
        CheckMirror(window,"reset");
    }
    static string Outcome(MainWindow window) {
        var text=new StringBuilder();
        SourceEditor source=Source(window);
        text.Append("text=").Append(Buffer(window)).Append('\u0001');
        text.Append("caret=").Append(source==null?-1:source.CaretIndex);
        text.Append(",sel=").Append(source==null?-1:source.SelectionStart).Append(':').Append(source==null?-1:source.SelectionLength);
        text.Append(",editing=").Append(FieldBool(window,"editing"));
        text.Append(",dark=").Append(FieldBool(window,"dark"));
        text.Append(",wrap=").Append(FieldBool(window,"wordWrap"));
        text.Append(",plain=").Append(Prop<bool>(window,"PlainText"));
        text.Append(",preview=").Append(Field<CheckBox>(window,"preview").IsChecked==true);
        text.Append(",outline=").Append(Field<TreeView>(window,"outline").Visibility==Visibility.Visible);
        text.Append(",search=").Append(Field<Border>(window,"searchHost").Visibility==Visibility.Visible);
        text.Append(",replace=").Append(Field<StackPanel>(window,"replaceRow").Visibility==Visibility.Visible);
        text.Append(",font=").Append(((double)FieldObject(window,"fontSize")).ToString("R",CultureInfo.InvariantCulture));
        text.Append(",custom=").Append(((System.Windows.Media.Color?)FieldObject(window,"customColor")).HasValue);
        text.Append(",docs=").Append(Documents(window).Count).Append('@').Append(Tabs(window).SelectedIndex);
        text.Append(",bold=").Append(ButtonField(window,"boldButton").Tag);
        text.Append(",italic=").Append(ButtonField(window,"italicButton").Tag);
        text.Append(",strike=").Append(ButtonField(window,"strikeButton").Tag);
        text.Append(",code=").Append(ButtonField(window,"codeButton").Tag);
        text.Append(",heading=").Append(ButtonField(window,"headingButton").Tag);
        return text.ToString();
    }

    // ---------------------------------------------------------------- seeded sequences
    static void RunSequence(int seed,bool live) {
        currentSeed=seed;
        currentMode=live?"live":"basic";
        MainWindow window=null;
        try {
            window=NewWindow(900,720);
            Call(window,"SetLiveMode",new object[] {live});
            Call(window,"SetText",new object[] {"Seed "+seed.ToString(CultureInfo.InvariantCulture)+" first line\n\n- one\n- two\n\nBody text.\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            Pump();
            Call(window,"CreateTab");
            Call(window,"SetText",new object[] {"Second tab body\n\nMore text.\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            Pump();
            SelectTab(window,0);
            Pump();
            CheckTabIntegrity(window,"sequence:setup");
            var random=new Random(seed*104729+(live?7919:17));
            for (int step=1;step<=sequenceSteps;step++) {
                CheckBudget("sequence step "+step.ToString(CultureInfo.InvariantCulture));
                string name;
                bool viewOnly;
                Action action=ChooseSequenceAction(window,random,out name,out viewOnly);
                List<TabSnapshot> before=CaptureTabs(window);
                bool ok=RunStep("seq:"+name,action);
                Flush(window);
                CheckTabIntegrity(window,"seq:"+name);
                if (viewOnly&&ok) {
                    CompareViewSnapshots(before,CaptureTabs(window),"seq:"+name);
                } else {
                    CheckMirror(window,"seq:"+name);
                    CheckDirtyMarkers(window,"seq:"+name);
                }
            }
        } catch (Exception ex) {
            if (ex is TimeoutException) {
                all=false;
                AppendReport("\nFAIL sequence seed "+seed+" stopped: "+OneLine(ex.Message)+"\n");
            } else {
                Fail("sequence:exception","sequence","sequence completes",ExceptionText(ex));
            }
        } finally {
            CloseQuiet(window);
        }
    }
    static string SequenceText(Random random) {
        switch (random.Next(8)) {
            case 0: return "sentence ";
            case 1: return "가나다 ";
            case 2: return "**bold** ";
            case 3: return "- item ";
            case 4: return "`code` ";
            case 5: return "line\n";
            case 6: return "| a | b |";
            default: return "日本語 ";
        }
    }
    static Action ChooseSequenceAction(MainWindow window,Random random,out string name,out bool viewOnly) {
        viewOnly=false;
        int roll=random.Next(100);
        if (roll<10) {
            string value=SequenceText(random);
            name="type";
            return delegate {
                if (!FieldBool(window,"editing")) Call(window,"SetMode",new object[] {true});
                Type(window,value);
            };
        }
        if (roll<16) {
            name="new-tab";
            int index=random.Next(3);
            return delegate {
                Call(window,"CreateTab");
                Call(window,"SetText",new object[] {"New tab "+index.ToString(CultureInfo.InvariantCulture)+"\n\n- item\n"});
                Call(window,"SetMode",new object[] {true});
                Pump();
            };
        }
        if (roll<22) {
            name="open-file";
            string file=random.Next(2)==0?fixtureA:fixtureB;
            return delegate { Call(window,"LoadFile",new object[] {file}); Pump(); };
        }
        if (roll<25) {
            name="new-document";
            return delegate {
                Call(window,"NewFile");
                Call(window,"SetText",new object[] {"Fresh document\n"});
                Call(window,"SetMode",new object[] {true});
                Pump();
            };
        }
        if (roll<35) {
            int direction=random.Next(2)==0?1:-1;
            name=direction>0?"next-tab":"previous-tab";
            viewOnly=true;
            return delegate { Call(window,"CycleTab",new object[] {direction}); Pump(); };
        }
        if (roll<42) {
            name="switch-tab";
            viewOnly=true;
            int index=random.Next(Math.Max(1,Documents(window).Count));
            return delegate { SelectTab(window,index); Pump(); };
        }
        if (roll<48) {
            name="close-cancel";
            return delegate {
                if (!FieldBool(window,"editing")) Call(window,"SetMode",new object[] {true});
                Type(window," pending");
                Pump();
                object active=ActiveDocument(window);
                int count=Documents(window).Count;
                string before=Buffer(window);
                SaveChoices(window,1,MainWindow.SaveChoice.Cancel);
                try { Call(window,"CloseTab",new object[] {active}); } finally { Pump(); }
                if (SaveChoiceCount(window)>0) ClearSaveChoices(window);
                Check("tabs:close-cancel","seq:close-cancel",Documents(window).Count==count&&ContainsTab(window,active)&&
                    String.Equals(Buffer(window),before,StringComparison.Ordinal),"Cancel keeps the tab and its text",
                    "documents="+Documents(window).Count+" (was "+count+"), active kept="+ContainsTab(window,active)+", text=["+OneLine(Buffer(window))+"]");
            };
        }
        if (roll<54) {
            name="close-discard";
            return delegate {
                if (!FieldBool(window,"editing")) Call(window,"SetMode",new object[] {true});
                Type(window," discarded");
                Pump();
                List<TabSnapshot> before=CaptureTabs(window);
                object active=ActiveDocument(window);
                SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                try { Call(window,"CloseTab",new object[] {active}); } finally { Pump(); }
                if (SaveChoiceCount(window)>0) ClearSaveChoices(window);
                bool gone=!ContainsTab(window,active);
                Check("tabs:close-keeps-others","seq:close-discard",gone,"Discard closes the active tab","active still present");
                for (int i=0;i<before.Count;i++) {
                    if (Object.ReferenceEquals(before[i].Tab,active)) continue;
                    if (!ContainsTab(window,before[i].Tab)) continue;
                    string now=TabBufferFor(window,before[i].Tab);
                    Check("tabs:close-keeps-others","seq:close-discard",String.Equals(before[i].Buffer,now,StringComparison.Ordinal),
                        "another tab's text survives the close",Diff(before[i].Buffer,now));
                }
            };
        }
        if (roll<62) {
            name="dark";
            viewOnly=true;
            return delegate { Call(window,"SetDark",new object[] {!FieldBool(window,"dark")}); };
        }
        if (roll<68) {
            int choice=random.Next(3);
            name=choice==0?"zoom-reset":choice==1?"zoom-in":"zoom-out";
            viewOnly=true;
            if (choice==0) return delegate { Call(window,"SetBaseFont",new object[] {(string)FieldObject(window,"family"),16.0}); };
            double delta=choice==1?2:-2;
            return delegate { Call(window,"Zoom",new object[] {delta}); };
        }
        if (roll<73) {
            string[] codes=new string[] {"en","ja","zh-CN","zh-TW","ko"};
            string code=codes[random.Next(codes.Length)];
            name="language-"+code;
            viewOnly=true;
            return delegate {
                SetField(window,"language",code);
                Call(window,"ApplyLanguage");
                Pump();
            };
        }
        if (roll<78) {
            name="raw-view";
            viewOnly=true;
            return delegate {
                var preview=Field<CheckBox>(window,"preview");
                preview.IsChecked=!(preview.IsChecked==true);
                Pump();
            };
        }
        if (roll<82) {
            if (!FieldBool(window,"editing")) {
                name="read-mode-off";
                viewOnly=true;
                return delegate { Call(window,"SetMode",new object[] {true}); };
            }
            if (Prop<bool>(window,"HasUnsavedChanges")) {
                name="dark";
                viewOnly=true;
                return delegate { Call(window,"SetDark",new object[] {!FieldBool(window,"dark")}); };
            }
            name="read-mode-on";
            viewOnly=true;
            return delegate { Call(window,"SetMode",new object[] {false}); };
        }
        if (roll<86) {
            name="find-toggle";
            viewOnly=true;
            var searchHost=Field<Border>(window,"searchHost");
            bool open=searchHost.Visibility==Visibility.Visible;
            return delegate {
                if (open) Call(window,"CloseSearch");
                else Call(window,"ToggleSearch",new object[] {false});
                Pump();
            };
        }
        if (roll<89) {
            name="wrap";
            viewOnly=true;
            return delegate { Call(window,"SetWrap",new object[] {!FieldBool(window,"wordWrap")}); };
        }
        if (roll<93) {
            bool narrow=random.Next(2)==0;
            name=narrow?"resize-narrow":"resize-wide";
            viewOnly=true;
            return delegate {
                window.Width=narrow?430:1000;
                window.UpdateLayout();
                Pump();
            };
        }
        if (roll<96) {
            name="save";
            bool plain=Prop<bool>(window,"PlainText");
            string target=Path.Combine(savesDir,"seq-"+currentSeed.ToString(CultureInfo.InvariantCulture)+"-"+roll.ToString(CultureInfo.InvariantCulture)+"-"+
                random.Next(1000).ToString(CultureInfo.InvariantCulture)+(plain?".txt":".md"));
            return delegate {
                RemoveFile(target);
                SetField(window,"path",target);
                MainWindow.SaveTargetPicker=delegate(string name2) { return target; };
                object saved=null;
                try { saved=Call(window,"Save",new object[] {false}); }
                finally { MainWindow.SaveTargetPicker=null; }
                Pump();
                bool written=File.Exists(target);
                bool cleaned=!Prop<bool>(window,"HasUnsavedChanges");
                Check("save:file","seq:save",written&&cleaned,"Save writes the file and clears dirty",
                    "written="+written+", dirty="+Prop<bool>(window,"HasUnsavedChanges")+", saved="+Convert.ToString(saved));
            };
        }
        if (roll<98) {
            name="select-all";
            return delegate { Source(window).SelectAll(); Pump(); };
        }
        name="undo";
        return delegate { Call(window,"Undo"); Pump(); };
    }

    // ---------------------------------------------------------------- view-level scenario
    static void ScenarioViews() {
        bool[] modes=new bool[] {false,true};
        for (int mode=0;mode<modes.Length;mode++) {
            currentMode=modes[mode]?"live":"basic";
            MainWindow window=null;
            try {
                window=NewWindow(1100,760);
                Call(window,"SetLiveMode",new object[] {modes[mode]});
                Call(window,"SetText",new object[] {"View fixture line one\n\nA paragraph with **bold** text.\n\n- item\n"});
                Call(window,"SetMode",new object[] {true});
                SetField(window,"saved",Buffer(window));
                Pump();
                Call(window,"CreateTab");
                Call(window,"SetText",new object[] {"Second document for the language check\n"});
                Call(window,"SetMode",new object[] {true});
                SetField(window,"saved",Buffer(window));
                Pump();
                SelectTab(window,0);
                Pump();
                CheckTabIntegrity(window,"view:setup");

                ViewStep(window,"view:dark-on",delegate { Call(window,"SetDark",new object[] {true}); },true);
                ViewStep(window,"view:dark-off",delegate { Call(window,"SetDark",new object[] {false}); },true);
                ViewStep(window,"view:zoom-in",delegate { Call(window,"Zoom",new object[] {2.0}); },true);
                ViewStep(window,"view:zoom-out",delegate { Call(window,"Zoom",new object[] {-2.0}); },true);
                ViewStep(window,"view:zoom-reset",delegate { Call(window,"SetBaseFont",new object[] {"Malgun Gothic",16.0}); },true);
                ViewStep(window,"view:wrap-off",delegate { Call(window,"SetWrap",new object[] {false}); },false);
                ViewStep(window,"view:wrap-on",delegate { Call(window,"SetWrap",new object[] {true}); },false);
                ViewStep(window,"view:live-mode",delegate {
                    ClickItem(MenuItemField(window,"liveModeMenu"));
                    Pump();
                },true);
                bool wantLive=MenuCheck(window,"liveModeMenu");
                Check("view:live-mode","view:live-mode",(!StaticField<bool>("hideMarkers"))==wantLive,
                    "the live-mode menu switches the shared view mode",
                    "checked="+wantLive+", hideMarkers="+StaticField<bool>("hideMarkers"));
                ViewStep(window,"view:live-mode-back",delegate {
                    ClickItem(MenuItemField(window,"liveModeMenu"));
                    Pump();
                },true);
                Check("view:live-mode","view:live-mode-back",(!StaticField<bool>("hideMarkers"))==MenuCheck(window,"liveModeMenu"),
                    "the live-mode menu switches back","checked="+MenuCheck(window,"liveModeMenu")+", hideMarkers="+StaticField<bool>("hideMarkers"));
                ViewStep(window,"view:raw-on",delegate { Field<CheckBox>(window,"preview").IsChecked=true; Pump(); },true);
                ViewStep(window,"view:raw-off",delegate { Field<CheckBox>(window,"preview").IsChecked=false; Pump(); },true);
                ViewStep(window,"view:read-mode",delegate { Call(window,"SetMode",new object[] {false}); },true);
                ViewStep(window,"view:edit-mode",delegate { Call(window,"SetMode",new object[] {true}); },true);
                ViewStep(window,"view:find-open",delegate { Call(window,"ToggleSearch",new object[] {false}); },false);
                ViewStep(window,"view:replace-open",delegate { Call(window,"ToggleSearch",new object[] {true}); },false);
                ViewStep(window,"view:find-close",delegate { Call(window,"CloseSearch"); },false);
                ViewStep(window,"view:narrow",delegate { window.Width=430; window.UpdateLayout(); Pump(); },false);
                ViewStep(window,"view:wide",delegate { window.Width=1100; window.UpdateLayout(); Pump(); },false);

                // View commands must not add an undo entry: after one typed character, a single Undo
                // still has to remove that character.
                ViewHistoryStep(window,"history:dark",delegate { Call(window,"SetDark",new object[] {true}); });
                ViewHistoryStep(window,"history:zoom",delegate { Call(window,"Zoom",new object[] {2.0}); });
                ViewHistoryStep(window,"history:raw",delegate { Field<CheckBox>(window,"preview").IsChecked=true; Pump(); });
                ViewHistoryStep(window,"history:find",delegate { Call(window,"ToggleSearch",new object[] {false}); });
                ViewHistoryStep(window,"history:wrap",delegate { Call(window,"SetWrap",new object[] {false}); });
                ViewHistoryStep(window,"history:language",delegate {
                    SetField(window,"language","en");
                    Call(window,"ApplyLanguage");
                    Pump();
                });

                // Language: sample menu headers, tooltips and tab captions. Both tabs exist, so an
                // inactive tab's caption is sampled too.
                string[] languages=new string[] {"en","ja","zh-CN","zh-TW"};
                for (int i=0;i<languages.Length;i++) {
                    string code=languages[i];
                    if (languageFilter!=null&&!String.Equals(languageFilter,code,StringComparison.Ordinal)) continue;
                    ViewStep(window,"view:language-"+code,delegate {
                        SetField(window,"language",code);
                        Call(window,"ApplyLanguage");
                        Pump();
                    },false);
                    CheckNoKorean(window,code,"view:language-"+code);
                    ViewStep(window,"view:language-back-"+code,delegate {
                        SetField(window,"language","ko");
                        Call(window,"ApplyLanguage");
                        Pump();
                    },false);
                }

                // Font size box: type a size and commit it through the same method Enter runs.
                RunStep("view:fontbox",delegate {
                    var sizes=Field<ComboBox>(window,"sizes");
                    sizes.Text="20 px";
                    Call(window,"CommitFontSize",new object[] {true});
                    Pump();
                });
                RunStep("view:fontbox-reset",delegate {
                    var sizes=Field<ComboBox>(window,"sizes");
                    sizes.Text="16 px";
                    Call(window,"CommitFontSize",new object[] {true});
                    Pump();
                });
                Check("view:fontbox-no-throw","view:fontbox",true,"the size box commits without an exception","ok");

                // Open and close every top-level menu (the menu bar itself, not the popups' items).
                RunStep("view:menu-open-close",delegate {
                    Menu menu=TopMenu(window);
                    for (int i=0;i<menu.Items.Count;i++) {
                        var item=menu.Items[i] as MenuItem;
                        if (item==null||item.Items.Count==0) continue;
                        item.IsSubmenuOpen=true;
                        Pump();
                        item.IsSubmenuOpen=false;
                        Pump();
                    }
                });
                CheckTabIntegrity(window,"view:end");
            } catch (Exception ex) {
                Fail("view:scenario","view","the view scenario completes",ExceptionText(ex));
            } finally {
                CloseQuiet(window);
            }
        }
    }
    static void ViewHistoryStep(MainWindow window,string name,Action action) {
        ResetBaseline(window,"history baseline line\n",21);
        Type(window,"Z");
        Pump();
        string edited=Buffer(window);
        Check("view:history-setup",name,edited.IndexOf("Z",StringComparison.Ordinal)>=0,"typed character present",Diff("Z inserted",edited));
        RunStep(name,action);
        Flush(window);
        Check("view:text",name,String.Equals(edited,Buffer(window),StringComparison.Ordinal),"view command keeps the text",Diff(edited,Buffer(window)));
        Call(window,"Undo");
        Pump();
        Flush(window);
        string undone=Buffer(window);
        Check("view:no-undo-entry",name,String.Equals("history baseline line\n",undone,StringComparison.Ordinal),
            "one Undo removes only the typed character (the view command added no entry)",Diff("history baseline line\n",undone));
        Call(window,"Redo");
        Pump();
        Flush(window);
        // Reopening the raw view closes it again so the next baseline starts from the same surface.
        var preview=Field<CheckBox>(window,"preview");
        if (preview.IsChecked==true) { preview.IsChecked=false; Pump(); }
        var searchHost=Field<Border>(window,"searchHost");
        if (searchHost.Visibility==Visibility.Visible) Call(window,"CloseSearch");
    }

    // ---------------------------------------------------------------- language leftovers
    static readonly HashSet<string> languageSeen=new HashSet<string>();
    static bool HasHangul(string value) {
        if (String.IsNullOrEmpty(value)) return false;
        for (int i=0;i<value.Length;i++) {
            char c=value[i];
            if ((c>='가'&&c<='힣')||(c>='ᄀ'&&c<='ᇿ')||(c>='㄰'&&c<='㆏')) return true;
        }
        return false;
    }
    static string HeaderText(object header) {
        if (header==null) return null;
        string text=header as string;
        if (text!=null) return text;
        var block=header as TextBlock;
        if (block!=null) return block.Text;
        var panel=header as Panel;
        if (panel!=null) {
            for (int i=0;i<panel.Children.Count;i++) {
                string nested=HeaderText(panel.Children[i]);
                if (!String.IsNullOrEmpty(nested)) return nested;
            }
        }
        var content=header as ContentControl;
        if (content!=null) return HeaderText(content.Content);
        return null;
    }
    static HashSet<object> LanguageItemSet(MainWindow window) {
        var set=new HashSet<object>();
        try {
            object items=FieldObject(window,"languageItems");
            var list=items as IList;
            if (list!=null) for (int i=0;i<list.Count;i++) set.Add(list[i]);
        } catch (Exception) { }
        return set;
    }
    static void CollectMenuItems(ItemCollection items,string path,int depth,List<string> output,HashSet<object> skip) {
        if (items==null||depth>3) return;
        int index=0;
        foreach (object entry in items) {
            var item=entry as MenuItem;
            if (item==null) { index++; continue; }
            string header=HeaderText(item.Header);
            bool ignored=skip.Contains(item);
            if (!ignored&&!String.IsNullOrEmpty(header)) {
                output.Add("menu:"+path+"["+index.ToString(CultureInfo.InvariantCulture)+"] = "+header.Replace("_",""));
                string tip=item.ToolTip as string;
                if (!String.IsNullOrEmpty(tip)) output.Add("menu-tip:"+path+"["+index.ToString(CultureInfo.InvariantCulture)+"] = "+tip);
                string automationName=System.Windows.Automation.AutomationProperties.GetName(item);
                if (!String.IsNullOrEmpty(automationName)) output.Add("menu-name:"+path+"["+index.ToString(CultureInfo.InvariantCulture)+"] = "+automationName);
            }
            if (item.Items.Count>0) {
                string name=String.IsNullOrEmpty(header)?"?":header.Replace("_","");
                CollectMenuItems(item.Items,path+">"+name,depth+1,output,skip);
            }
            index++;
        }
    }
    static void CollectContextMenu(ContextMenu menu,string path,List<string> output,HashSet<object> skip) {
        if (menu==null) return;
        CollectMenuItems(menu.Items,path,0,output,skip);
    }
    static void AddControlTip(MainWindow window,string fieldName,string label,List<string> output) {
        try {
            var control=FieldObject(window,fieldName) as FrameworkElement;
            if (control==null) return;
            string tip=control.ToolTip as string;
            if (!String.IsNullOrEmpty(tip)) output.Add("tooltip:"+label+" = "+tip);
            /* b58 bug hunt 3: the screen-reader name is a second string the same button carries; a
               Korean literal there left an English UI reading Korean to Narrator. */
            string name=System.Windows.Automation.AutomationProperties.GetName(control);
            if (!String.IsNullOrEmpty(name)) output.Add("name:"+label+" = "+name);
        } catch (Exception) { }
    }
    static void CheckNoKorean(MainWindow window,string language,string step) {
        if (String.Equals(language,"ko",StringComparison.Ordinal)) return;
        checksRun++;
        Bump(checkCounts,"language:korean-leftover");
        var strings=new List<string>();
        try {
            HashSet<object> skip=LanguageItemSet(window);
            Menu menu=TopMenu(window);
            if (menu!=null) CollectMenuItems(menu.Items,"top",0,strings,skip);
            CollectContextMenu(FieldObject(window,"saveMenu") as ContextMenu,"save",strings,skip);
            CollectContextMenu(FieldObject(window,"headingMenu") as ContextMenu,"heading",strings,skip);
            CollectContextMenu(FieldObject(window,"ribbonOverflowMenu") as ContextMenu,"overflow-ribbon",strings,skip);
            CollectContextMenu(FieldObject(window,"topOverflowMenu") as ContextMenu,"overflow-top",strings,skip);
            AddControlTip(window,"boldButton","bold",strings);
            AddControlTip(window,"italicButton","italic",strings);
            AddControlTip(window,"strikeButton","strike",strings);
            AddControlTip(window,"quoteButton","quote",strings);
            AddControlTip(window,"linkButton","link",strings);
            AddControlTip(window,"codeButton","inline",strings);
            AddControlTip(window,"blockButton","block",strings);
            AddControlTip(window,"imageButton","image",strings);
            AddControlTip(window,"tableButton","table",strings);
            AddControlTip(window,"outlineButton","outline",strings);
            AddControlTip(window,"editButton","edit",strings);
            AddControlTip(window,"quickDark","dark",strings);
            AddControlTip(window,"quickFind","find",strings);
            AddControlTip(window,"quickReplace","replace",strings);
            AddControlTip(window,"quickText","txt",strings);
            AddControlTip(window,"saveMain","save",strings);
            AddControlTip(window,"undoButton","undo",strings);
            AddControlTip(window,"redoButton","redo",strings);
            AddControlTip(window,"headingButton","heading",strings);
            AddControlTip(window,"listButton","list",strings);
            AddControlTip(window,"taskButton","task",strings);
            AddControlTip(window,"ruleButton","rule",strings);
            AddControlTip(window,"sizes","sizes",strings);
            AddControlTip(window,"preview","preview",strings);
            AddControlTip(window,"wrapToggle","wrap",strings);
            AddControlTip(window,"darkToggle","dark-menu",strings);
            AddControlTip(window,"textModeMenu","txt-menu",strings);
            AddControlTip(window,"alwaysEditMenu","always-edit",strings);
            AddControlTip(window,"newTextMenu","new-text",strings);
            IList documents=Documents(window);
            for (int i=0;i<documents.Count;i++) strings.Add("tab-caption["+i.ToString(CultureInfo.InvariantCulture)+"] = "+TabCaption(documents[i]));
            var status=Field<TextBlock>(window,"status");
            if (status!=null) strings.Add("status = "+status.Text);
        } catch (Exception ex) {
            Fail("language:korean-leftover",step,"UI strings readable",ExceptionText(ex));
            return;
        }
        int fresh=0;
        for (int i=0;i<strings.Count;i++) {
            string value=strings[i];
            if (!HasHangul(value)) continue;
            string key=language+"|"+value;
            if (!languageSeen.Add(key)) continue;
            fresh++;
            if (fresh<=40) Fail("language:korean-leftover",step,"no Hangul in a "+language+" UI string",key);
        }
        if (fresh==0) Check("language:korean-leftover-ok",step,true,"no Korean leftovers","none");
    }

    // ---------------------------------------------------------------- menu scenario
    static int fileCounter;
    static string SaveTarget(string label) {
        fileCounter++;
        return Path.Combine(savesDir,label+"-"+fileCounter.ToString(CultureInfo.InvariantCulture)+".md");
    }
    static bool WaitForFile(string path,int milliseconds) {
        long start=watch.ElapsedMilliseconds;
        while (!File.Exists(path)&&watch.ElapsedMilliseconds-start<milliseconds) PumpMs(15);
        return File.Exists(path);
    }
    static string ReadUtf8(string path) {
        return File.ReadAllText(path,new UTF8Encoding(false));
    }
    static MenuItem TopItem(MainWindow window,string header) {
        Menu menu=TopMenu(window);
        if (menu==null) return null;
        foreach (object entry in menu.Items) {
            var item=entry as MenuItem;
            if (item!=null&&String.Equals(HeaderText(item.Header),header,StringComparison.Ordinal)) return item;
        }
        return null;
    }
    static MenuItem ChildItem(MenuItem parent,string header) {
        if (parent==null) return null;
        foreach (object entry in parent.Items) {
            var item=entry as MenuItem;
            if (item!=null&&String.Equals(HeaderText(item.Header),header,StringComparison.Ordinal)) return item;
        }
        return null;
    }
    static MenuItem ChildItemPrefix(MenuItem parent,string prefix) {
        if (parent==null) return null;
        foreach (object entry in parent.Items) {
            var item=entry as MenuItem;
            string header=item==null?null:HeaderText(item.Header);
            if (header!=null&&header.StartsWith(prefix,StringComparison.Ordinal)) return item;
        }
        return null;
    }
    static ModalResult RunModal(MainWindow owner,Action open,int waitMilliseconds) {
        var result=new ModalResult();
        DispatcherTimer timer=null;
        timer=new DispatcherTimer(DispatcherPriority.Background);
        timer.Interval=TimeSpan.FromMilliseconds(40);
        timer.Tick+=delegate {
            try {
                if (Application.Current==null) return;
                foreach (Window candidate in Application.Current.Windows) {
                    if (Object.ReferenceEquals(candidate,owner)||candidate is MainWindow) continue;
                    if (!candidate.IsLoaded||!candidate.IsVisible) continue;
                    if (!result.Saw) {
                        result.Saw=true;
                        result.Title=candidate.Title;
                    }
                    try { candidate.Close(); result.Closed=true; } catch (Exception) { }
                }
                if (result.Closed) timer.Stop();
            } catch (Exception ex) {
                result.Error=ExceptionText(ex);
                timer.Stop();
            }
        };
        timer.Start();
        try {
            open();
        } catch (Exception ex) {
            result.Error=ExceptionText(ex);
        } finally {
            timer.Stop();
        }
        if (result.Saw&&!result.Closed) {
            for (int i=0;i<10&&!result.Closed;i++) PumpMs(30);
        }
        PumpMs(waitMilliseconds);
        return result;
    }
    static void ParityStep(MainWindow window,string name,Action setup,Action first,Action second) {
        ResetBaseline(window,"Parity baseline line\n",21);
        if (setup!=null) { setup(); Pump(); }
        RunStep(name+":menu",first);
        Flush(window);
        string firstOutcome=Outcome(window);
        ResetBaseline(window,"Parity baseline line\n",21);
        if (setup!=null) { setup(); Pump(); }
        RunStep(name+":toolbar",second);
        Flush(window);
        string secondOutcome=Outcome(window);
        Check("menu:parity",name,String.Equals(firstOutcome,secondOutcome,StringComparison.Ordinal),
            "the menu command and the toolbar command reach the same state",Diff(firstOutcome,secondOutcome));
    }
    static void ScenarioMenus() {
        currentMode="live";
        MainWindow window=null;
        bool oldAlways=false,oldNewText=false;
        string[] oldKeys=null;
        try { oldAlways=StaticField<bool>("alwaysEditing"); } catch (Exception) { }
        try { oldNewText=StaticField<bool>("newDocumentsText"); } catch (Exception) { }
        try { oldKeys=(string[])((string[])StaticField<string[]>("functionKeys")).Clone(); } catch (Exception) { }
        try {
            window=NewWindow(1200,820);
            Call(window,"SetLiveMode",new object[] {true});
            Call(window,"SetText",new object[] {"Menu fixture line one\n\nsecond paragraph\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            Pump();
            MenuItem fileMenu=TopItem(window,"파일(_F)");
            MenuItem editMenu=TopItem(window,"편집(_E)");
            MenuItem viewMenu=TopItem(window,"보기(_V)");
            MenuItem helpMenu=TopItem(window,"도움말(_H)");
            Check("menu:structure","menu:structure",fileMenu!=null&&editMenu!=null&&viewMenu!=null&&helpMenu!=null,
                "the four top menus exist","file="+(fileMenu!=null)+", edit="+(editMenu!=null)+", view="+(viewMenu!=null)+", help="+(helpMenu!=null));
            if (fileMenu==null||editMenu==null||viewMenu==null||helpMenu==null) return;

            // File > New: the menu item and the toolbar new button both add one tab.
            ParityStep(window,"menu:new",null,
                delegate { Press(ChildItem(fileMenu,"새로 만들기")); Pump(); },
                delegate { Press(ToolbarButtonByTip(window,"새 문서 만들기 (Ctrl+N)")); Pump(); });
            ResetBaseline(window,"Menu fixture line one\n\nsecond paragraph\n",0);
            Check("menu:new","menu:new",Documents(window).Count==1,"the baseline reset removed the extra tab",
                "documents="+Documents(window).Count);

            // File > Save writes the active buffer and clears dirty.
            string target=SaveTarget("menu-save");
            RemoveFile(target);
            RunStep("menu:save",delegate {
                SetField(window,"path",target);
                PlaceCaret(window,Buffer(window).Length);
                Type(window," menu saved");
                Pump();
                Press(ChildItem(fileMenu,"저장"));
                PumpMs(60);
            });
            bool savedWritten=WaitForFile(target,600);
            Check("save:file","menu:save",savedWritten&&String.Equals(ReadUtf8(target),Buffer(window),StringComparison.Ordinal),
                "Save writes the buffer", "exists="+savedWritten+", file=["+ (savedWritten?OneLine(ReadUtf8(target)):"-") +"], buffer=["+OneLine(Buffer(window))+"]");
            Check("save:dirty","menu:save",!Prop<bool>(window,"HasUnsavedChanges"),"Save clears the dirty flag",
                "dirty="+Prop<bool>(window,"HasUnsavedChanges"));
            CheckDirtyMarkers(window,"menu:save");

            // File > Save As answers through SaveTargetPicker (the app's own seam, no native dialog).
            string saveAsTarget=SaveTarget("menu-save-as");
            RemoveFile(saveAsTarget);
            RunStep("menu:save-as",delegate {
                Type(window," save as");
                Pump();
                MainWindow.SaveTargetPicker=delegate(string name) { return saveAsTarget; };
                try { Press(ChildItem(fileMenu,"다른 이름으로 저장…")); PumpMs(80); }
                finally { MainWindow.SaveTargetPicker=null; }
            });
            bool saveAsWritten=WaitForFile(saveAsTarget,800);
            Check("save:file","menu:save-as",saveAsWritten&&String.Equals(ReadUtf8(saveAsTarget),Buffer(window),StringComparison.Ordinal),
                "Save As writes the buffer to the picked path", "exists="+saveAsWritten+", file=["+(saveAsWritten?OneLine(ReadUtf8(saveAsTarget)):"-")+"], buffer=["+OneLine(Buffer(window))+"]");
            Check("save:dirty","menu:save-as",!Prop<bool>(window,"HasUnsavedChanges"),"Save As clears the dirty flag",
                "dirty="+Prop<bool>(window,"HasUnsavedChanges"));

            // Edit > Undo / Redo against the toolbar buttons.
            ParityStep(window,"menu:undo",delegate { Type(window,"X"); Pump(); },
                delegate { Press(ChildItem(editMenu,"실행 취소")); Pump(); },
                delegate { Press(ButtonField(window,"undoButton")); Pump(); });
            ResetBaseline(window,"Parity baseline line\n",21);
            Type(window,"X"); Pump();
            Call(window,"Undo"); Pump();
            ParityStep(window,"menu:redo",delegate { Type(window,"X"); Call(window,"Undo"); Pump(); },
                delegate { Press(ChildItem(editMenu,"다시 실행")); Pump(); },
                delegate { Press(ButtonField(window,"redoButton")); Pump(); });

            // Edit > Select all.
            ResetBaseline(window,"Select all fixture\n",0);
            RunStep("menu:select-all",delegate { Press(ChildItem(editMenu,"모두 선택")); Pump(); });
            Check("menu:select-all","menu:select-all",Source(window).SelectionLength==Buffer(window).Length,
                "Select all covers the document","selection="+Source(window).SelectionLength+", length="+Buffer(window).Length);

            // Edit > Find / Replace against the toolbar buttons.
            ParityStep(window,"menu:find",null,
                delegate { Press(ChildItem(editMenu,"찾기…")); Pump(); },
                delegate { Press(ButtonField(window,"quickFind")); Pump(); });
            ParityStep(window,"menu:replace",null,
                delegate { Press(ChildItem(editMenu,"바꾸기…")); Pump(); },
                delegate { Press(ButtonField(window,"quickReplace")); Pump(); });
            RunStep("menu:find-close",delegate { Call(window,"CloseSearch"); });
            Check("menu:find-closed","menu:find-close",Field<Border>(window,"searchHost").Visibility!=Visibility.Visible,
                "find bar closes","visible="+Field<Border>(window,"searchHost").Visibility);

            // Edit > TXT mode toggles the document mode and the check mark.
            ResetBaseline(window,"TXT menu fixture\n",0);
            RunStep("menu:txt-on",delegate { Press(ChildItem(editMenu,"TXT 모드")); Pump(); });
            Check("menu:state","menu:txt-on",Prop<bool>(window,"PlainText"),"TXT mode turns on","plain="+Prop<bool>(window,"PlainText"));
            Check("menu:state","menu:txt-on",MenuCheck(window,"textModeMenu"),"the TXT menu check mark follows","checked="+MenuCheck(window,"textModeMenu"));
            RunStep("menu:txt-off",delegate { Press(ChildItem(editMenu,"TXT 모드")); Pump(); });
            Check("menu:state","menu:txt-off",!Prop<bool>(window,"PlainText"),"TXT mode turns off","plain="+Prop<bool>(window,"PlainText"));

            // Edit > Always editing / new documents in TXT: static options round-trip.
            RunStep("menu:always-editing",delegate { ClickItem(ChildItem(editMenu,"항상 수정 기능 활성화")); Pump(); });
            Check("menu:state","menu:always-editing",StaticField<bool>("alwaysEditing")!=oldAlways,"alwaysEditing flips",
                "value="+StaticField<bool>("alwaysEditing"));
            RunStep("menu:always-editing-back",delegate { ClickItem(ChildItem(editMenu,"항상 수정 기능 활성화")); Pump(); });
            Check("menu:state","menu:always-editing-back",StaticField<bool>("alwaysEditing")==oldAlways,"alwaysEditing restored",
                "value="+StaticField<bool>("alwaysEditing"));
            RunStep("menu:new-text",delegate { ClickItem(ChildItem(editMenu,"새 문서는 항상 TXT 모드 활성화")); Pump(); });
            Check("menu:state","menu:new-text",StaticField<bool>("newDocumentsText")!=oldNewText,"newDocumentsText flips",
                "value="+StaticField<bool>("newDocumentsText"));
            RunStep("menu:new-text-back",delegate { ClickItem(ChildItem(editMenu,"새 문서는 항상 TXT 모드 활성화")); Pump(); });
            Check("menu:state","menu:new-text-back",StaticField<bool>("newDocumentsText")==oldNewText,"newDocumentsText restored",
                "value="+StaticField<bool>("newDocumentsText"));

            // View > zoom items against the same handler the Ctrl shortcuts reach.
            ResetBaseline(window,"Zoom menu fixture\n",0);
            ParityStep(window,"menu:zoom-in",null,
                delegate { Press(ChildItem(viewMenu,"글자 확대")); Pump(); },
                delegate { Call(window,"Zoom",new object[] {2.0}); Pump(); });
            ParityStep(window,"menu:zoom-out",null,
                delegate { Press(ChildItem(viewMenu,"글자 축소")); Pump(); },
                delegate { Call(window,"Zoom",new object[] {-2.0}); Pump(); });
            ParityStep(window,"menu:zoom-reset",delegate { Call(window,"SetBaseFont",new object[] {"Malgun Gothic",22.0}); Pump(); },
                delegate { Press(ChildItem(viewMenu,"기본 글자 크기")); Pump(); },
                delegate { Call(window,"SetBaseFont",new object[] {"Malgun Gothic",16.0}); Pump(); });

            // View > dark, wrap, line spacing presets and text colour reset.
            ResetBaseline(window,"View menu fixture\n",0);
            ViewStep(window,"menu:dark",delegate { ClickItem(MenuItemField(window,"darkToggle")); Pump(); },true);
            Check("menu:state","menu:dark",FieldBool(window,"dark")==MenuCheck(window,"darkToggle"),"the dark menu check follows",
                "dark="+FieldBool(window,"dark")+", checked="+MenuCheck(window,"darkToggle"));
            ViewStep(window,"menu:wrap",delegate { ClickItem(MenuItemField(window,"wrapToggle")); Pump(); },false);
            Check("menu:state","menu:wrap",FieldBool(window,"wordWrap")==MenuCheck(window,"wrapToggle"),"the wrap menu check follows",
                "wrap="+FieldBool(window,"wordWrap")+", checked="+MenuCheck(window,"wrapToggle"));
            MenuItem lineMenu=ChildItem(viewMenu,"줄 간격");
            MenuItem preset=ChildItem(lineMenu,"125%");
            ViewStep(window,"menu:line-125",delegate { Press(preset); Pump(); },false);
            Check("menu:line","menu:line-125",Math.Abs((double)FieldObject(window,"linePercent")-125)<0.001,"the 125% preset applies",
                "linePercent="+Convert.ToString(FieldObject(window,"linePercent"),CultureInfo.InvariantCulture));
            SetField(window,"customColor",System.Windows.Media.Colors.Red);
            Call(window,"RenderPreservingView");
            Pump();
            ViewStep(window,"menu:color-reset",delegate { Press(ChildItem(viewMenu,"기본 글자색 초기화")); Pump(); },false);
            Check("menu:color","menu:color-reset",!((System.Windows.Media.Color?)FieldObject(window,"customColor")).HasValue,
                "the text colour reset clears the custom colour","customColor="+Convert.ToString(FieldObject(window,"customColor")));

            // View > New files in tabs: unchecking moves the other document to a window, checking restores the strip.
            object movedTab=null;
            string movedText=null;
            RunStep("menu:tabs-off-setup",delegate {
                Call(window,"CreateTab");
                Call(window,"SetText",new object[] {"Moved to another window\n"});
                Call(window,"SetMode",new object[] {true});
                SetField(window,"saved",Buffer(window));
                Pump();
                movedTab=ActiveDocument(window);
                movedText=Buffer(window);
                SelectTab(window,0);
                Pump();
            });
            HashSet<MainWindow> beforeWindows=MainWindowSet();
            RunStep("menu:tabs-off",delegate { ClickItem(ChildItem(viewMenu,"새 탭으로 보기")); Pump(); });
            Check("menu:tabs-off","menu:tabs-off",!FieldBool(window,"openInTabs")&&Documents(window).Count==1&&
                Tabs(window).Visibility!=Visibility.Visible,"unchecking opens the other document in its own window",
                "openInTabs="+FieldBool(window,"openInTabs")+", documents="+Documents(window).Count+", strip="+Tabs(window).Visibility);
            var movedWindows=NewWindowsSince(beforeWindows);
            MainWindow moved=movedWindows.Count==1?movedWindows[0]:null;
            Check("menu:tabs-off","menu:tabs-off",moved!=null&&movedTab!=null&&!ContainsTab(window,movedTab)&&
                String.Equals(Buffer(moved),movedText,StringComparison.Ordinal),"the moved document keeps its text in the new window",
                "windows="+movedWindows.Count+", text=["+(moved==null?"-":OneLine(Buffer(moved)))+"]");
            CloseQuiet(moved);
            RunStep("menu:tabs-on",delegate { ClickItem(ChildItem(viewMenu,"새 탭으로 보기")); Pump(); });
            Check("menu:tabs-on","menu:tabs-on",FieldBool(window,"openInTabs")&&Tabs(window).Visibility==Visibility.Visible,
                "checking the option shows the tab strip again","openInTabs="+FieldBool(window,"openInTabs")+", strip="+Tabs(window).Visibility);

            // Language submenu: a real click switches the UI, and the file menu is translated.
            MenuItem languageMenu=TopItem(window,"언어");
            Check("menu:language","menu:language",languageMenu!=null,"the language menu exists","missing");
            if (languageMenu!=null) {
                MenuItem english=LanguageChild(languageMenu,"en");
                RunStep("menu:language-en",delegate { ClickItem(english); Pump(); });
                Check("menu:language","menu:language-en",String.Equals((string)FieldObject(window,"language"),"en",StringComparison.Ordinal),
                    "the English item selects en","language="+Convert.ToString(FieldObject(window,"language")));
                Check("menu:language","menu:language-en",MenuHeaderAnywhere(window,"File(_F)"),
                    "the File menu header is translated","headers=["+TopHeaders(window)+"], fileMenu=["+Convert.ToString(HeaderText(fileMenu.Header))+"]");
                CheckNoKorean(window,"en","menu:language-en");
                MenuItem korean=LanguageChild(languageMenu,"ko");
                RunStep("menu:language-ko",delegate { ClickItem(korean); Pump(); });
                Check("menu:language","menu:language-ko",String.Equals((string)FieldObject(window,"language"),"ko",StringComparison.Ordinal),
                    "the Korean item selects ko","language="+Convert.ToString(FieldObject(window,"language")));
            }

            // Help > About and the shortcut window open and close (their own WPF dialogs).
            ResetBaseline(window,"About fixture\n",0);
            ModalResult about=RunModal(window,delegate { Press(ChildItem(helpMenu,"MDNote 정보")); },120);
            Check("dialog:about","menu:about",about.Saw&&about.Closed,"the About dialog opens and closes",
                "saw="+about.Saw+", closed="+about.Closed+", title=["+OneLine(about.Title)+"], error="+OneLine(about.Error));
            ModalResult shortcutDialog=RunModal(window,delegate { Press(TopItem(window,"단축키")); },120);
            Check("dialog:shortcuts","menu:shortcuts",shortcutDialog.Saw&&shortcutDialog.Closed,"the shortcut settings window opens and closes",
                "saw="+shortcutDialog.Saw+", closed="+shortcutDialog.Closed+", title=["+OneLine(shortcutDialog.Title)+"], error="+OneLine(shortcutDialog.Error));
            MenuItem customLine=ChildItem(lineMenu,"사용자 지정 (175%)");
            if (customLine==null) customLine=ChildItemPrefix(lineMenu,"사용자 지정");
            if (customLine!=null) {
                ModalResult lineDialog=RunModal(window,delegate { Press(customLine); },120);
                Check("dialog:line-spacing","menu:line-custom",lineDialog.Saw&&lineDialog.Closed,"the custom line spacing prompt opens and closes",
                    "saw="+lineDialog.Saw+", closed="+lineDialog.Closed+", error="+OneLine(lineDialog.Error));
            }

            // Help > usage documents only when their files are next to the executable; a missing file
            // would raise a real message box, which a headless probe must not do.
            string baseDir=AppDomain.CurrentDomain.BaseDirectory;
            // Same order as MainWindow.HelpFile: the help\ folder first, the exe folder for an older layout.
            string helpDir=Path.Combine(baseDir,"help");
            string usageKo=Path.Combine(helpDir,"MDNote 사용법.md");
            if (!File.Exists(usageKo)) usageKo=Path.Combine(baseDir,"MDNote 사용법.md");
            string exampleKo=Path.Combine(helpDir,"마크다운 작성 예제.md");
            if (!File.Exists(exampleKo)) exampleKo=Path.Combine(baseDir,"마크다운 작성 예제.md");
            if (File.Exists(usageKo)) {
                int before=Documents(window).Count;
                RunStep("menu:help-usage",delegate { Press(ChildItem(helpMenu,"MDNote 사용법")); Pump(); });
                Check("menu:help-usage","menu:help-usage",Documents(window).Count==before+1,"the usage document opens in a tab",
                    "documents="+Documents(window).Count+" (was "+before+")");
                if (Documents(window).Count>before) { object extra=Documents(window)[Documents(window).Count-1]; SelectTab(window,Documents(window).Count-1); SaveChoices(window,1,MainWindow.SaveChoice.Discard); Call(window,"CloseTab",new object[] {extra}); ClearSaveChoices(window); Pump(); }
            } else skipped.Add("도움말 > MDNote 사용법 (guide file not next to the exe)");
            if (File.Exists(exampleKo)) {
                int before=Documents(window).Count;
                RunStep("menu:help-example",delegate { Press(ChildItem(helpMenu,"MD파일 작성방법")); Pump(); });
                Check("menu:help-example","menu:help-example",Documents(window).Count==before+1,"the writing guide opens in a tab",
                    "documents="+Documents(window).Count+" (was "+before+")");
                if (Documents(window).Count>before) { object extra=Documents(window)[Documents(window).Count-1]; SelectTab(window,Documents(window).Count-1); SaveChoices(window,1,MainWindow.SaveChoice.Discard); Call(window,"CloseTab",new object[] {extra}); ClearSaveChoices(window); Pump(); }
            } else skipped.Add("도움말 > MD파일 작성방법 (guide file not next to the exe)");

            // Native dialogs: their menu items are present but cannot be driven on a headless desktop.
            skipped.Add("파일 > 열기… / 인코딩을 지정하여 열기… / 인쇄… (native dialogs)");
            skipped.Add("파일 > 복구할 문서… (MessageBox when no drafts; recovery store disabled in self-test)");
            skipped.Add("편집 > 복사 / 잘라내기 / 붙여넣기 / 서식 없이 붙여넣기 (system clipboard is shared with the desktop)");
            skipped.Add("편집 > 이미지 삽입… (native file dialog)");
            skipped.Add("보기 > 글꼴 및 크기… / 기본 글자색… (native dialogs)");

            // File > Exit: Cancel keeps the window, Discard closes it, answered through queuedSaveChoices.
            ResetBaseline(window,"Exit fixture\n",0);
            PlaceCaret(window,Buffer(window).Length);
            Type(window," unsaved exit"); Pump();
            SaveChoices(window,1,MainWindow.SaveChoice.Cancel);
            RunStep("menu:exit-cancel",delegate { Press(ChildItem(fileMenu,"끝내기")); PumpMs(60); });
            if (SaveChoiceCount(window)>0) ClearSaveChoices(window);
            Check("window:exit-cancel","menu:exit-cancel",window.IsLoaded&&String.Equals(Buffer(window),"Exit fixture\n unsaved exit",StringComparison.Ordinal),
                "Cancel on exit keeps the window and its unsaved text","loaded="+window.IsLoaded+", text=["+OneLine(Buffer(window))+"]");
            SaveChoices(window,1,MainWindow.SaveChoice.Discard);
            RunStep("menu:exit-discard",delegate { Press(ChildItem(fileMenu,"끝내기")); PumpMs(80); });
            if (SaveChoiceCount(window)>0) ClearSaveChoices(window);
            Check("window:exit-discard","menu:exit-discard",!window.IsLoaded,"Discard on exit closes the window","loaded="+window.IsLoaded);
        } catch (Exception ex) {
            Fail("menu:scenario","menu","the menu scenario completes",ExceptionText(ex));
        } finally {
            try { SetStaticField("alwaysEditing",oldAlways); } catch (Exception) { }
            try { SetStaticField("newDocumentsText",oldNewText); } catch (Exception) { }
            if (oldKeys!=null) { try { SetStaticField("functionKeys",oldKeys); } catch (Exception) { } }
            CloseQuiet(window);
        }
    }
    static bool MenuCheck(MainWindow window,string fieldName) {
        try { return ((MenuItem)FieldObject(window,fieldName)).IsChecked==true; } catch (Exception) { return false; }
    }
    static Button ToolbarButtonByTip(MainWindow window,string tip) {
        Button quickFind=ButtonField(window,"quickFind");
        var panel=quickFind==null?null:quickFind.Parent as Panel;
        if (panel==null) return null;
        foreach (UIElement child in panel.Children) {
            var button=child as Button;
            if (button==null) continue;
            if (String.Equals(Convert.ToString(button.ToolTip),tip,StringComparison.Ordinal)) return button;
        }
        return null;
    }
    static MenuItem LanguageChild(MenuItem parent,string code) {
        if (parent==null) return null;
        foreach (object entry in parent.Items) {
            var item=entry as MenuItem;
            if (item!=null&&String.Equals(Convert.ToString(item.Tag),code,StringComparison.Ordinal)) return item;
        }
        return null;
    }
    static bool ItemHasHeader(MenuItem item,string header) {
        if (item==null) return false;
        if (String.Equals(HeaderText(item.Header),header,StringComparison.Ordinal)) return true;
        foreach (object entry in item.Items) {
            var child=entry as MenuItem;
            if (child!=null&&ItemHasHeader(child,header)) return true;
        }
        return false;
    }
    static bool MenuHeaderAnywhere(MainWindow window,string header) {
        Menu menu=TopMenu(window);
        if (menu!=null) {
            foreach (object entry in menu.Items) {
                var item=entry as MenuItem;
                if (item!=null&&ItemHasHeader(item,header)) return true;
            }
        }
        ContextMenu top=OverflowMenu(window,false);
        if (top!=null) {
            foreach (object entry in top.Items) {
                var item=entry as MenuItem;
                if (item!=null&&ItemHasHeader(item,header)) return true;
            }
        }
        return false;
    }
    static string TopHeaders(MainWindow window) {
        var text=new StringBuilder();
        Menu menu=TopMenu(window);
        if (menu!=null) {
            foreach (object entry in menu.Items) {
                var item=entry as MenuItem;
                if (item==null) continue;
                if (text.Length>0) text.Append(" / ");
                text.Append(HeaderText(item.Header));
            }
        }
        ContextMenu top=OverflowMenu(window,false);
        if (top!=null&&top.Items.Count>0) {
            text.Append(" || overflow:");
            foreach (object entry in top.Items) {
                var item=entry as MenuItem;
                if (item==null) continue;
                text.Append(" / ").Append(HeaderText(item.Header));
            }
        }
        return text.ToString();
    }
    static HashSet<MainWindow> MainWindowSet() {
        var set=new HashSet<MainWindow>();
        if (Application.Current==null) return set;
        foreach (Window candidate in Application.Current.Windows) {
            var editor=candidate as MainWindow;
            if (editor!=null) set.Add(editor);
        }
        return set;
    }
    static List<MainWindow> NewWindowsSince(HashSet<MainWindow> before) {
        var list=new List<MainWindow>();
        if (Application.Current==null) return list;
        foreach (Window candidate in Application.Current.Windows) {
            var editor=candidate as MainWindow;
            if (editor!=null&&!before.Contains(editor)) list.Add(editor);
        }
        return list;
    }
    // The Explorer/second-instance entry point: DocumentActivation.DispatchFiles, invoked with the
    // same static ActiveWindow field MainWindow.Activated writes. Static-private members are reached
    // by reflection, as the other probes reach private MainWindow members.
    static void SetActivationWindow(MainWindow window) {
        Type activation=typeof(MainWindow).Assembly.GetType("MDNote.DocumentActivation");
        if (activation==null) throw new MissingMemberException("probe: DocumentActivation type");
        FieldInfo field=activation.GetField("ActiveWindow",Members);
        if (field==null) throw new MissingMemberException("probe: DocumentActivation.ActiveWindow");
        field.SetValue(null,window);
    }
    static void DispatchFiles(string[] files) {
        Type activation=typeof(MainWindow).Assembly.GetType("MDNote.DocumentActivation");
        if (activation==null) throw new MissingMemberException("probe: DocumentActivation type");
        MethodInfo method=activation.GetMethod("DispatchFiles",BindingFlags.Static|Members);
        if (method==null) throw new MissingMemberException("probe: DocumentActivation.DispatchFiles");
        try { method.Invoke(null,new object[] {Application.Current,files}); }
        catch (TargetInvocationException error) { if (error.InnerException!=null) throw error.InnerException; throw; }
    }
    static bool TabSelectedFor(MainWindow window,string file) {
        object active=ActiveDocument(window);
        if (active==null) return false;
        var item=(TabItem)TabField(active,"Item");
        return item!=null&&Object.ReferenceEquals(Tabs(window).SelectedItem,item)&&
            String.Equals(TabString(active,"Path"),Path.GetFullPath(file),StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- tab scenario
    static void ScenarioTabs() {
        currentMode="live";
        MainWindow window=null;
        try {
            window=NewWindow(1100,780);
            Call(window,"SetLiveMode",new object[] {true});
            Call(window,"SetText",new object[] {"A first line\nA second line\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            PlaceCaret(window,3);
            Pump();
            object tabA=ActiveDocument(window);

            Call(window,"CreateTab");
            Call(window,"SetText",new object[] {"B plain line\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            PlaceCaret(window,2);
            Pump();
            object tabB=ActiveDocument(window);

            Call(window,"CreateTab");
            Call(window,"SetText",new object[] {"C **bold** and plain\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            PlaceCaret(window,5);
            Pump();
            object tabC=ActiveDocument(window);

            Call(window,"CreateTab");
            Call(window,"SetText",new object[] {"D plain and **bold**\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            PlaceCaret(window,4);
            Pump();
            object tabD=ActiveDocument(window);

            Call(window,"CreateTab");
            Call(window,"SetText",new object[] {"# Heading line\nBody line\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            PlaceCaret(window,4);
            Pump();
            object tabE=ActiveDocument(window);

            Call(window,"CreateTab");
            Call(window,"SetText",new object[] {"F text mode body\n"});
            SetField(window,"textMode",true);
            Call(window,"Render");
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            Pump();
            object tabF=ActiveDocument(window);
            CheckTabIntegrity(window,"tabs:setup");

            // Many tabs overflow the 31 px strip: the tab that becomes selected past the right edge
            // must be scrolled into view (the bar itself stays hidden; the wheel is the pointer path).
            RunStep("tabs:strip-scroll",delegate {
                window.Width=380; Pump();
                /* Leave the last tab first: selecting the already-active tab changes nothing and no
                   switch happens, so the reveal would not be exercised. */
                SelectTab(window,0); Pump();
                SelectTab(window,Documents(window).Count-1); Pump();
                ScrollViewer strip=InnerScrollViewer(Tabs(window));
                bool overflow=strip!=null&&strip.ScrollableWidth>0.5;
                bool revealed=overflow&&strip.HorizontalOffset>0.5;
                Check("tabs:strip","tabs:strip-scroll",revealed,"the strip scrolls the selected tab into view",
                    "scrollable="+(strip==null?"null":strip.ScrollableWidth.ToString(CultureInfo.InvariantCulture))
                    +", offset="+(strip==null?"null":strip.HorizontalOffset.ToString(CultureInfo.InvariantCulture)));
                /* The wheel over the strip is the pointer path for the hidden bar. */
                SelectTab(window,0); Pump();
                if (strip!=null&&strip.ScrollableWidth>0.5) {
                    double before=strip.HorizontalOffset;
                    var wheel=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120) {RoutedEvent=Mouse.PreviewMouseWheelEvent};
                    Tabs(window).RaiseEvent(wheel); Pump();
                    Check("tabs:strip","tabs:strip-scroll",wheel.Handled&&strip.HorizontalOffset>before,
                        "the wheel over the strip scrolls it sideways",
                        "handled="+wheel.Handled+", offset="+strip.HorizontalOffset.ToString(CultureInfo.InvariantCulture)+" was "+before.ToString(CultureInfo.InvariantCulture));
                }
                window.Width=1100; Pump();
            });

            // Text, caret and mode survive a plain tab switch.
            RunStep("tabs:switch-a",delegate {
                SelectTab(window,0); Pump();
                Check("tabs:buffer","tabs:switch-a",Object.ReferenceEquals(ActiveDocument(window),tabA)&&String.Equals(Buffer(window),"A first line\nA second line\n",StringComparison.Ordinal),
                    "tab A text is restored","text=["+OneLine(Buffer(window))+"]");
                Check("tabs:caret","tabs:switch-a",Source(window).CaretIndex==3,"tab A caret is kept","caret="+Source(window).CaretIndex);
                Check("tabs:mode","tabs:switch-a",!Prop<bool>(window,"PlainText"),"tab A is Markdown","plain="+Prop<bool>(window,"PlainText"));
            });
            RunStep("tabs:switch-b",delegate {
                SelectTab(window,1); Pump();
                Check("tabs:buffer","tabs:switch-b",Object.ReferenceEquals(ActiveDocument(window),tabB)&&String.Equals(Buffer(window),"B plain line\n",StringComparison.Ordinal),
                    "tab B text is restored","text=["+OneLine(Buffer(window))+"]");
                Check("tabs:caret","tabs:switch-b",Source(window).CaretIndex==2,"tab B caret is kept","caret="+Source(window).CaretIndex);
            });
            RunStep("tabs:switch-f",delegate {
                SelectTab(window,5); Pump();
                Check("tabs:buffer","tabs:switch-f",Object.ReferenceEquals(ActiveDocument(window),tabF)&&String.Equals(Buffer(window),"F text mode body\n",StringComparison.Ordinal),
                    "tab F text is restored","text=["+OneLine(Buffer(window))+"]");
                Check("tabs:mode","tabs:switch-f",Prop<bool>(window,"PlainText"),"tab F keeps TXT mode","plain="+Prop<bool>(window,"PlainText"));
                Check("menu:state","tabs:switch-f",ButtonField(window,"quickText").Tag!=null,"the TXT status-bar button shows TXT mode","tag="+ButtonField(window,"quickText").Tag);
            });

            // Toolbar B / heading state must follow the caret of the tab that becomes active.
            RunStep("tabs:toolbar-bold",delegate {
                SelectTab(window,2); Pump();
                Call(window,"UpdateTools"); Pump();
                string ownerBold=Convert.ToString(ButtonField(window,"boldButton").Tag);
                Check("toolbar:caret-context","tabs:toolbar-bold",String.Equals(ownerBold,"Active",StringComparison.Ordinal),
                    "the bold button lights inside bold text","tag="+ownerBold);
                SelectTab(window,3); Pump();
                string stale=Convert.ToString(ButtonField(window,"boldButton").Tag);
                Call(window,"UpdateTools"); Pump();
                string oracle=Convert.ToString(ButtonField(window,"boldButton").Tag);
                Check("tabs:toolbar-state","tabs:toolbar-bold",String.Equals(stale,oracle,StringComparison.Ordinal),
                    "the bold button follows the new tab's caret without another refresh","shown="+stale+", correct="+oracle);
                SelectTab(window,2); Pump();
                string staleBack=Convert.ToString(ButtonField(window,"boldButton").Tag);
                Call(window,"UpdateTools"); Pump();
                string oracleBack=Convert.ToString(ButtonField(window,"boldButton").Tag);
                Check("tabs:toolbar-state","tabs:toolbar-bold",String.Equals(staleBack,oracleBack,StringComparison.Ordinal),
                    "returning to the bold tab restores the bold button","shown="+staleBack+", correct="+oracleBack);
            });
            RunStep("tabs:toolbar-heading",delegate {
                SelectTab(window,4); Pump();
                Call(window,"UpdateTools"); Pump();
                string owner=Convert.ToString(ButtonField(window,"headingButton").Tag);
                Check("toolbar:caret-context","tabs:toolbar-heading",String.Equals(owner,"Active",StringComparison.Ordinal),
                    "the heading button lights on a heading line","tag="+owner);
                SelectTab(window,3); Pump();
                string stale=Convert.ToString(ButtonField(window,"headingButton").Tag);
                Call(window,"UpdateTools"); Pump();
                string oracle=Convert.ToString(ButtonField(window,"headingButton").Tag);
                Check("tabs:toolbar-state","tabs:toolbar-heading",String.Equals(stale,oracle,StringComparison.Ordinal),
                    "the heading button follows the new tab's caret without another refresh","shown="+stale+", correct="+oracle);
            });

            // Caret survives an ordinary switch, and a view change in between must not lose it.
            RunStep("tabs:caret-switch",delegate {
                SelectTab(window,0); Pump(); PlaceCaret(window,3);
                SelectTab(window,1); Pump(); PlaceCaret(window,2);
                SelectTab(window,0); Pump();
                Check("tabs:caret","tabs:caret-switch",Source(window).CaretIndex==3,"the caret survives a tab switch","caret="+Source(window).CaretIndex);
                Call(window,"Zoom",new object[] {2.0}); Pump();
                SelectTab(window,1); Pump();
                SelectTab(window,0); Pump();
                Check("tabs:caret","tabs:caret-switch",Source(window).CaretIndex==3,
                    "the caret survives a tab switch after a view change","caret="+Source(window).CaretIndex);
            });
            RunStep("tabs:zoom-reset",delegate { Call(window,"SetBaseFont",new object[] {"Malgun Gothic",16.0}); Pump(); });

            // A slow open keeps its overlay (label, progress bar, 취소) over the body. Switching tabs
            // used to clear area.Children and take the overlay with it, so neither the progress nor
            // the cancel button could be reached for the rest of the load; the tab that was picked
            // stayed read-only through documentOperationBusy. The overlay must be put back above the
            // tab shown now, and the finish path must clear busy and the read-only state.
            RunStep("tabs:open-overlay-switch",delegate {
                SelectTab(window,0); Pump();
                Call(window,"EnsureFileOpenOverlay"); Pump();
                var overlay=Field<Border>(window,"fileOpenOverlay");
                var area=Field<Grid>(window,"area");
                bool built=overlay!=null;
                if (overlay!=null) overlay.Visibility=Visibility.Visible;
                bool cancel=false;
                var panel=overlay==null?null:overlay.Child as StackPanel;
                if (panel!=null) foreach (UIElement child in panel.Children) if (child is Button) cancel=true;
                bool before=built&&area.Children.Contains(overlay)&&overlay.Visibility==Visibility.Visible&&FieldBool(window,"documentOperationBusy");
                SelectTab(window,1); Pump();
                bool kept=built&&area.Children.Contains(overlay)&&overlay.Visibility==Visibility.Visible;
                Check("open:overlay","tabs:open-overlay-switch",before&&kept&&cancel,
                    "the progress overlay and its cancel button stay with the tab switch",
                    "built="+built+", inArea="+(built&&area.Children.Contains(overlay))+", visible="+(built?overlay.Visibility.ToString():"-")+", cancel="+cancel);
                Call(window,"CompleteFileOpenUnlock",new object[] {null}); Pump();
                bool unlocked=!FieldBool(window,"documentOperationBusy")&&!Source(window).IsReadOnly&&overlay!=null&&overlay.Visibility!=Visibility.Visible;
                Check("open:unlock","tabs:open-overlay-switch",unlocked,
                    "the shown tab is editable after the open finishes",
                    "busy="+FieldBool(window,"documentOperationBusy")+", readOnly="+Source(window).IsReadOnly+", overlay="+(overlay==null?"null":overlay.Visibility.ToString()));
                SelectTab(window,0); Pump();
            });

            // A view change made in one tab re-renders the others when they come back; the caret and
            // selection of the tab that comes back must survive that re-render instead of resetting
            // to offset 0 (AvalonEdit's Text setter).
            RunStep("tabs:view-preserve",delegate {
                SelectTab(window,1); Pump();
                Source(window).Select(1,3); Pump();
                int caret=Source(window).CaretIndex,start=Source(window).SelectionStart,length=Source(window).SelectionLength;
                SelectTab(window,0); Pump();
                Call(window,"Zoom",new object[] {2.0}); Pump();
                SelectTab(window,1); Pump();
                Check("tabs:view","tabs:view-preserve",
                    Source(window).CaretIndex==caret&&Source(window).SelectionStart==start&&Source(window).SelectionLength==length,
                    "the other tab keeps its caret and selection across a re-render",
                    "caret="+Source(window).CaretIndex+" (want "+caret+"), selection="+Source(window).SelectionStart+","+Source(window).SelectionLength+" (want "+start+","+length+")");
                Call(window,"SetBaseFont",new object[] {"Malgun Gothic",16.0}); Pump();
            });

            // A tab switch replaces the editor the keyboard focus was in: the new editor must own
            // the keyboard or the next keystroke goes nowhere. A new tab and a close take it too.
            RunStep("tabs:focus-switch",delegate {
                if (!FocusChecksAvailable(window)) return;
                SelectTab(window,0); Pump();
                SelectTab(window,1); Pump();
                Check("tabs:focus","tabs:focus-switch",SourceHasKeyboardFocus(window),
                    "the switched-to editor owns the keyboard focus",FocusState(window));
                SelectTab(window,0); Pump();
                Check("tabs:focus","tabs:focus-switch",SourceHasKeyboardFocus(window),
                    "switching back leaves the keyboard focus in the editor",FocusState(window));
            });
            RunStep("tabs:focus-new",delegate {
                if (!FocusChecksAvailable(window)) return;
                Call(window,"CreateTab"); Pump();
                Check("tabs:focus","tabs:focus-new",SourceHasKeyboardFocus(window),
                    "a new tab focuses its editor",FocusState(window));
                object created=ActiveDocument(window);
                if (Prop<bool>(window,"HasUnsavedChanges")) SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                ClickTabClose(window,created); Pump();
                ClearSaveChoices(window);
                Check("tabs:focus","tabs:focus-new",SourceHasKeyboardFocus(window),
                    "closing the new tab returns the focus to its neighbour",FocusState(window));
            });

            // Undo history is per tab: undo in A cannot take B's edit and vice versa.
            RunStep("tabs:undo-a",delegate {
                SelectTab(window,0); Pump(); PlaceCaret(window,Buffer(window).Length);
                Type(window,"A1"); Pump();
                SelectTab(window,1); Pump(); PlaceCaret(window,Buffer(window).Length);
                Type(window,"B1"); Pump();
                string bText=Buffer(window);
                SelectTab(window,0); Pump();
                Call(window,"Undo"); Pump();
                Check("tabs:undo","tabs:undo-a",String.Equals(Buffer(window),"A first line\nA second line\n",StringComparison.Ordinal),
                    "undo in tab A removes only A's edit",Diff("A first line\nA second line\n",Buffer(window)));
                SelectTab(window,1); Pump();
                Check("tabs:undo","tabs:undo-a",String.Equals(Buffer(window),bText,StringComparison.Ordinal),
                    "tab B keeps its edit after an undo in A",Diff(bText,Buffer(window)));
                Call(window,"Undo"); Pump();
                Check("tabs:undo","tabs:undo-a",String.Equals(Buffer(window),"B plain line\n",StringComparison.Ordinal),
                    "undo in tab B removes only B's edit",Diff("B plain line\n",Buffer(window)));
                SelectTab(window,0); Pump();
                Check("tabs:undo","tabs:undo-a",String.Equals(Buffer(window),"A first line\nA second line\n",StringComparison.Ordinal),
                    "tab A stays undone",Diff("A first line\nA second line\n",Buffer(window)));
            });

            // Find bar open while switching tabs: no text change, the bar stays open.
            RunStep("tabs:find-open",delegate {
                window.Activate(); Pump();
                Call(window,"ToggleSearch",new object[] {true}); Pump();
                Check("tabs:find","tabs:find-open",Field<Border>(window,"searchHost").Visibility==Visibility.Visible,"find bar opens",
                    "visible="+Field<Border>(window,"searchHost").Visibility);
                /* The new tab focus must not take the focus away from a search that spans tabs. */
                SelectTab(window,1); Pump();
                if (focusChecks) Check("tabs:focus","tabs:find-open",Field<TextBox>(window,"query").IsKeyboardFocusWithin,
                    "a tab switch keeps the focus in the find field",
                    "findFocus="+Field<TextBox>(window,"query").IsKeyboardFocusWithin+", editorFocus="+Source(window).IsKeyboardFocusWithin);
                SelectTab(window,0); Pump();
            });
            for (int i=0;i<6;i++) {
                int index=i;
                ViewStep(window,"tabs:find-switch-"+index.ToString(CultureInfo.InvariantCulture),delegate { SelectTab(window,index); Pump(); },false);
                Check("tabs:find","tabs:find-switch-"+index.ToString(CultureInfo.InvariantCulture),
                    Field<Border>(window,"searchHost").Visibility==Visibility.Visible,"the find bar stays open across a tab switch",
                    "visible="+Field<Border>(window,"searchHost").Visibility);
            }
            RunStep("tabs:find-close",delegate { Call(window,"CloseSearch"); });

            // Language switch with an inactive tab whose caption was created in Korean.
            RunStep("tabs:language-en",delegate {
                SetField(window,"language","en");
                Call(window,"ApplyLanguage");
                Pump();
            });
            CheckNoKorean(window,"en","tabs:language-en");
            RunStep("tabs:language-ko",delegate {
                SetField(window,"language","ko");
                Call(window,"ApplyLanguage");
                Pump();
            });

            // Dirty flag after save, undo and redo in one tab.
            RunStep("tabs:save",delegate {
                SelectTab(window,0); Pump();
                PlaceCaret(window,Buffer(window).Length);
                Type(window,"A2"); Pump();
                CheckDirtyMarkers(window,"tabs:save");
                string target=SaveTarget("tab-save");
                RemoveFile(target);
                SetField(window,"path",target);
                Call(window,"Save",new object[] {false});
                PumpMs(40);
                bool written=WaitForFile(target,600);
                Check("save:file","tabs:save",written&&String.Equals(ReadUtf8(target),Buffer(window),StringComparison.Ordinal),
                    "the tab saves its buffer to disk","exists="+written+", buffer=["+OneLine(Buffer(window))+"]");
                CheckDirtyMarkers(window,"tabs:save");
                Call(window,"Undo"); Pump();
                CheckDirtyMarkers(window,"tabs:undo-after-save");
                Call(window,"Redo"); Pump();
                CheckDirtyMarkers(window,"tabs:redo-after-save");
            });

            // Closing a clean background tab from its own close button keeps every other tab.
            RunStep("tabs:close-background",delegate {
                List<TabSnapshot> before=CaptureTabs(window);
                object closing=tabB;
                object keep=ActiveDocument(window);
                int count=Documents(window).Count;
                ClickTabClose(window,closing); Pump();
                Check("tabs:close-neighbour","tabs:close-background",Object.ReferenceEquals(ActiveDocument(window),keep),
                    "closing a background tab keeps the tab the user was working in",
                    "active changed (tabs="+Tabs(window).SelectedIndex+", documents="+Documents(window).Count+")");
                if (focusChecks) Check("tabs:focus","tabs:close-background",SourceHasKeyboardFocus(window),
                    "closing a background tab returns the focus to the editor",FocusState(window));
                Check("tabs:close-keeps-others","tabs:close-background",!ContainsTab(window,closing),"the background tab closes","still present");
                Check("tabs:close-keeps-others","tabs:close-background",Documents(window).Count==count-1,"exactly one tab closes",
                    "documents="+Documents(window).Count+" (was "+count+")");
                for (int i=0;i<before.Count;i++) {
                    if (Object.ReferenceEquals(before[i].Tab,closing)) continue;
                    if (!ContainsTab(window,before[i].Tab)) continue;
                    string now=TabBufferFor(window,before[i].Tab);
                    Check("tabs:close-keeps-others","tabs:close-background",String.Equals(before[i].Buffer,now,StringComparison.Ordinal),
                        "another tab's text survives the close",Diff(before[i].Buffer,now));
                }
            });

            // Cancel and Discard on a dirty tab closed from its own close button.
            RunStep("tabs:close-cancel",delegate {
                SelectTab(window,0); Pump();
                PlaceCaret(window,Buffer(window).Length);
                Type(window," cancel-close"); Pump();
                object active=ActiveDocument(window);
                int count=Documents(window).Count;
                string text=Buffer(window);
                SaveChoices(window,1,MainWindow.SaveChoice.Cancel);
                ClickTabClose(window,active); Pump();
                if (SaveChoiceCount(window)>0) ClearSaveChoices(window);
                Check("tabs:close-cancel","tabs:close-cancel",Documents(window).Count==count&&ContainsTab(window,active)&&
                    String.Equals(Buffer(window),text,StringComparison.Ordinal),"Cancel keeps the tab and its text",
                    "documents="+Documents(window).Count+", text=["+OneLine(Buffer(window))+"]");
                List<TabSnapshot> before=CaptureTabs(window);
                SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                ClickTabClose(window,active); Pump();
                if (SaveChoiceCount(window)>0) ClearSaveChoices(window);
                Check("tabs:close-discard","tabs:close-cancel",!ContainsTab(window,active),"Discard closes the dirty tab","still present");
                for (int i=0;i<before.Count;i++) {
                    if (Object.ReferenceEquals(before[i].Tab,active)) continue;
                    if (!ContainsTab(window,before[i].Tab)) continue;
                    string now=TabBufferFor(window,before[i].Tab);
                    Check("tabs:close-keeps-others","tabs:close-cancel",String.Equals(before[i].Buffer,now,StringComparison.Ordinal),
                        "another tab's text survives the discard close",Diff(before[i].Buffer,now));
                }
            });

            // Closing the active tab activates the tab that took its index, not the last tab.
            RunStep("tabs:close-neighbour",delegate {
                if (Documents(window).Count<3) return;
                int index=1;
                SelectTab(window,index); Pump();
                object closing=ActiveDocument(window);
                object expected=Documents(window)[index+1];
                if (Prop<bool>(window,"HasUnsavedChanges")) SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                ClickTabClose(window,closing); Pump();
                ClearSaveChoices(window);
                Check("tabs:close-neighbour","tabs:close-neighbour",!ContainsTab(window,closing)&&Object.ReferenceEquals(ActiveDocument(window),expected),
                    "closing the active tab activates the tab that took its index",
                    "active index="+Tabs(window).SelectedIndex+", documents="+Documents(window).Count+", expected kept="+ContainsTab(window,expected));
            });

            // Closing the last tab leaves a fresh empty document.
            RunStep("tabs:close-last",delegate {
                for (int guard=0;guard<10&&Documents(window).Count>1;guard++) {
                    object active=ActiveDocument(window);
                    if (Prop<bool>(window,"HasUnsavedChanges")) SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                    ClickTabClose(window,active); Pump();
                    ClearSaveChoices(window);
                }
                object last=ActiveDocument(window);
                int countBefore=Documents(window).Count;
                if (Prop<bool>(window,"HasUnsavedChanges")) SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                ClickTabClose(window,last); Pump();
                ClearSaveChoices(window);
                Check("tabs:close-last","tabs:close-last",Documents(window).Count==1&&!ContainsTab(window,last)&&
                    String.Equals(Buffer(window),"",StringComparison.Ordinal),"closing the last tab creates a fresh empty document",
                    "documents="+Documents(window).Count+", len="+Buffer(window).Length+", oldKept="+ContainsTab(window,last)+", before="+countBefore);
                if (focusChecks) Check("tabs:focus","tabs:close-last",SourceHasKeyboardFocus(window),
                    "the fresh document after the last close focuses its editor",FocusState(window));
            });

            // Closing the active tab must release the find highlight adorner that tab owns; the
            // close path used to skip the capture for an already-active tab and the overlay stayed
            // on the window's AdornerLayer after its editor left the tree.
            RunStep("tabs:close-highlight",delegate {
                Call(window,"SetText",new object[] {"alpha beta\n"});
                Call(window,"SetMode",new object[] {true});
                SetField(window,"saved",Buffer(window));
                window.Activate(); Pump();
                Call(window,"ToggleSearch",new object[] {true}); Pump();
                Field<TextBox>(window,"query").Text="alpha"; Pump();
                object highlight=FieldObject(window,"sourceHighlight");
                bool attached=highlight!=null&&System.Windows.Media.VisualTreeHelper.GetParent((System.Windows.Media.Visual)highlight)!=null;
                Check("tabs:highlight","tabs:close-highlight",attached,"the find highlight is live before the close",
                    "highlight="+(highlight==null?"null":"attached="+attached));
                object active=ActiveDocument(window);
                if (Prop<bool>(window,"HasUnsavedChanges")) SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                ClickTabClose(window,active); Pump();
                ClearSaveChoices(window);
                bool released=highlight!=null&&System.Windows.Media.VisualTreeHelper.GetParent((System.Windows.Media.Visual)highlight)==null;
                Check("tabs:highlight","tabs:close-highlight",released,"closing the active tab releases its find highlight adorner",
                    "highlight="+(highlight==null?"null":"still attached"));
                Call(window,"CloseSearch"); Pump();
            });
            // A live composition must be committed into the tab that owns it before a tab change
            // detaches that editor: AvalonEdit cancels the IMM composition from the focus loss that
            // follows, and the tab paths used to read MainWindow.imeComposing, which no product path
            // ever sets, so the syllable was lost. A headless run has no IMM composition string, so
            // the counted result stands in: the change must ask for the commit while the old editor
            // still owns the tab, and the result delivered afterwards must stay in that tab and
            // never reach the new one.
            RunStep("tabs:ime-setup",delegate {
                Call(window,"SetText",new object[] {"IME first\n"});
                Call(window,"SetMode",new object[] {true});
                SetField(window,"saved",Buffer(window));
                Call(window,"CreateTab");
                Call(window,"SetText",new object[] {"IME second\n"});
                Call(window,"SetMode",new object[] {true});
                SetField(window,"saved",Buffer(window));
                Pump();
                SelectTab(window,0); Pump();
                Check("tabs:ime","tabs:ime-setup",
                    Documents(window).Count==2&&String.Equals(Buffer(window),"IME first\n",StringComparison.Ordinal),
                    "two clean tabs for the composition checks",
                    "documents="+Documents(window).Count+", text=["+OneLine(Buffer(window))+"]");
            });
            RunStep("tabs:ime-switch",delegate {
                object first=ActiveDocument(window);
                PlaceCaret(window,Buffer(window).Length);
                SourceEditor editor=Source(window);
                int requests=editor.CompositionRequests,caret=editor.CaretIndex;
                try {
                    editor.SimulateImeComposition();
                    editor.SimulateImeCommit(1);
                    Call(window,"CycleTab",new object[] {1}); Pump();
                    bool asked=editor.CompositionRequests>requests&&editor.CompositionRequestCaret==caret;
                    bool other=String.Equals(Buffer(window),"IME second\n",StringComparison.Ordinal);
                    /* The counted result the commit asked for lands in the editor that owned the tab
                       (that surface keeps its own document now that it is out of the tree). */
                    editor.Document.Insert(editor.CaretIndex,"한"); Pump();
                    bool kept=String.Equals(TabBufferFor(window,first),"IME first\n한",StringComparison.Ordinal);
                    string detail="asked="+asked+" (requests "+requests+"->"+editor.CompositionRequests
                        +", caret "+editor.CompositionRequestCaret+" want "+caret+"), second=["+OneLine(Buffer(window))+"]"
                        +", first=["+OneLine(TabBufferFor(window,first))+"]";
                    /* Return to the tab and let the window read its own editor again, as it does for
                       any committed typing, so the captured text carries the syllable too. */
                    Call(window,"SwitchTab",new object[] {first}); Pump();
                    SetField(window,"sourceSnapshotDirty",true);
                    Call(window,"CaptureTab");
                    bool stored=String.Equals(Buffer(window),"IME first\n한",StringComparison.Ordinal);
                    Check("tabs:ime","tabs:ime-switch",asked&&other&&kept&&stored,
                        "Ctrl+Tab commits the composing syllable into the tab that owned it",detail);
                } finally { editor.ResetCompositionSimulation(); }
            });
            RunStep("tabs:ime-close",delegate {
                SelectTab(window,0); Pump();
                object active=ActiveDocument(window);
                PlaceCaret(window,Buffer(window).Length);
                SourceEditor editor=Source(window);
                int requests=editor.CompositionRequests,caret=editor.CaretIndex;
                try {
                    editor.SimulateImeComposition();
                    editor.SimulateImeCommit(1);
                    SaveChoices(window,1,MainWindow.SaveChoice.Cancel);
                    ClickTabClose(window,active); Pump();
                    if (SaveChoiceCount(window)>0) ClearSaveChoices(window);
                    bool asked=editor.CompositionRequests>requests&&editor.CompositionRequestCaret==caret;
                    Type(window,"글"); Pump();
                    bool kept=ContainsTab(window,active)&&String.Equals(Buffer(window),"IME first\n한글",StringComparison.Ordinal);
                    Check("tabs:ime","tabs:ime-close",asked&&kept,
                        "Cancel keeps the tab and the committed syllable after a close",
                        "asked="+asked+" (requests "+requests+"->"+editor.CompositionRequests+"), present="+ContainsTab(window,active)
                        +", text=["+OneLine(Buffer(window))+"]");
                } finally { editor.ResetCompositionSimulation(); }
            });
            RunStep("tabs:ime-new",delegate {
                object first=ActiveDocument(window);
                PlaceCaret(window,Buffer(window).Length);
                SourceEditor editor=Source(window);
                int requests=editor.CompositionRequests,caret=editor.CaretIndex;
                object created=null;
                try {
                    editor.SimulateImeComposition();
                    editor.SimulateImeCommit(1);
                    Call(window,"CreateTab"); Pump();
                    created=ActiveDocument(window);
                    bool asked=editor.CompositionRequests>requests&&editor.CompositionRequestCaret==caret;
                    bool fresh=!Object.ReferenceEquals(created,first)&&String.Equals(Buffer(window),"",StringComparison.Ordinal);
                    editor.Document.Insert(editor.CaretIndex,"다"); Pump();
                    bool kept=String.Equals(TabBufferFor(window,first),"IME first\n한글다",StringComparison.Ordinal);
                    bool other=String.Equals(Buffer(window),"",StringComparison.Ordinal);
                    /* Back to the first tab (it keeps the syllable) and close the empty new one. */
                    Call(window,"SwitchTab",new object[] {first}); Pump();
                    SetField(window,"sourceSnapshotDirty",true);
                    Call(window,"CaptureTab");
                    ClickTabClose(window,created); Pump();
                    ClearSaveChoices(window);
                    bool closed=!ContainsTab(window,created)&&String.Equals(Buffer(window),"IME first\n한글다",StringComparison.Ordinal);
                    Check("tabs:ime","tabs:ime-new",asked&&fresh&&kept&&other&&closed,
                        "a new tab commits the composing syllable into the tab that owned it",
                        "asked="+asked+" (requests "+requests+"->"+editor.CompositionRequests+"), fresh="+fresh
                        +", kept=["+OneLine(TabBufferFor(window,first))+"]");
                } finally { editor.ResetCompositionSimulation(); }
            });
            CheckTabIntegrity(window,"tabs:end");
        } catch (Exception ex) {
            Fail("tabs:scenario","tabs","the tab scenario completes",ExceptionText(ex));
        } finally {
            CloseQuiet(window);
        }
    }

    // ---------------------------------------------------------------- shortcut scenario
    static bool TagActive(FrameworkElement control) {
        return control!=null&&String.Equals(Convert.ToString(control.Tag),"Active",StringComparison.Ordinal);
    }
    static void ShortcutParity(MainWindow window,string id,string fixture,int caret,Action counterpart,bool expectTextChange) {
        ResetBaseline(window,fixture,caret);
        RunStep("shortcut:"+id+":fkey",delegate { Call(window,"ExecuteFunction",new object[] {id}); Pump(); });
        Flush(window);
        string fkey=Outcome(window);
        string fkeyText=Buffer(window);
        if (expectTextChange) {
            Check("shortcut:effect",id,!String.Equals(fkeyText,fixture,StringComparison.Ordinal),
                "the F-key command changes the document as its toolbar action does","text=["+OneLine(fkeyText)+"]");
        }
        ResetBaseline(window,fixture,caret);
        RunStep("shortcut:"+id+":toolbar",counterpart);
        Flush(window);
        string toolbar=Outcome(window);
        Check("shortcut:parity",id,String.Equals(fkey,toolbar,StringComparison.Ordinal),
            "the F-key command and its toolbar action reach the same state",Diff(fkey,toolbar));
    }
    static void TableShortcutParity(MainWindow window,string id,int action) {
        string table="| A | B |\n| --- | --- |\n| C | D |\n";
        int caret=table.IndexOf("| C",StringComparison.Ordinal)+2;
        ResetBaseline(window,table,caret);
        RunStep("shortcut:"+id+":fkey",delegate { Call(window,"ExecuteFunction",new object[] {id}); Pump(); });
        Flush(window);
        string fkey=Outcome(window);
        Check("shortcut:effect",id,!String.Equals(Buffer(window),table,StringComparison.Ordinal),
            "the table shortcut changes the table","text=["+OneLine(Buffer(window))+"]");
        ResetBaseline(window,table,caret);
        var actions=FieldObject(window,"tableActions") as IList;
        var button=actions==null||action>=actions.Count?null:actions[action] as Button;
        RunStep("shortcut:"+id+":toolbar",delegate { Press(button); Pump(); });
        Flush(window);
        string toolbar=Outcome(window);
        Check("shortcut:parity",id,String.Equals(fkey,toolbar,StringComparison.Ordinal),
            "the table shortcut and its toolbar button reach the same table",Diff(fkey,toolbar));
    }
    static void TabShortcutParity(MainWindow window,string id,int direction) {
        for (int pass=0;pass<2;pass++) {
            ResetBaseline(window,"Tab shortcut first\n",0);
            Call(window,"CreateTab");
            Call(window,"SetText",new object[] {"Tab shortcut second\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            Pump();
            SelectTab(window,0);
            Pump();
            string label=pass==0?"shortcut:"+id+":fkey":"shortcut:"+id+":toolbar";
            if (pass==0) RunStep(label,delegate { Call(window,"ExecuteFunction",new object[] {id}); Pump(); });
            else RunStep(label,delegate { Call(window,"CycleTab",new object[] {direction}); Pump(); });
            Flush(window);
            string outcome=Outcome(window);
            if (pass==0) {
                fkeyOutcome=outcome;
                Check("shortcut:effect",id,Tabs(window).SelectedIndex==(direction>0?1:1),"the tab shortcut moves to the other tab",
                    "index="+Tabs(window).SelectedIndex);
            } else {
                Check("shortcut:parity",id,String.Equals(fkeyOutcome,outcome,StringComparison.Ordinal),
                    "the tab shortcut and CycleTab reach the same tab",Diff(fkeyOutcome,outcome));
            }
        }
    }
    static string fkeyOutcome;
    static void ScenarioShortcuts() {
        currentMode="live";
        MainWindow window=null;
        string[] oldKeys=null;
        try { oldKeys=(string[])((string[])StaticField<string[]>("functionKeys")).Clone(); } catch (Exception) { }
        try {
            window=NewWindow(1100,780);
            Call(window,"SetLiveMode",new object[] {true});
            Call(window,"SetText",new object[] {"Shortcut fixture\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            Pump();

            // Real F1..F12 events through the window's own PreviewKeyDown handler.
            string[] dispatchIds=new string[] {"h1","h2","h3","h4","h5","h6","quote","rule","dark","wrap","zoomIn","zoomOut"};
            for (int i=0;i<dispatchIds.Length;i++) {
                string id=dispatchIds[i];
                ResetBaseline(window,"Function key baseline\n",0);
                string[] keys=new string[12];
                for (int j=0;j<keys.Length;j++) keys[j]="none";
                keys[i]=id;
                SetStaticField("functionKeys",keys);
                bool darkBefore=FieldBool(window,"dark");
                bool wrapBefore=FieldBool(window,"wordWrap");
                double fontBefore=(double)FieldObject(window,"fontSize");
                string textBefore=Buffer(window);
                bool handled=false;
                RunStep("shortcut:F"+(i+1).ToString(CultureInfo.InvariantCulture)+"="+id,delegate { handled=RaiseKey(window,Key.F1+i); });
                bool reached=false;
                string detail;
                if (id=="h1"||id=="h2"||id=="h3"||id=="h4"||id=="h5"||id=="h6") {
                    reached=Buffer(window).StartsWith("#",StringComparison.Ordinal)&&!String.Equals(Buffer(window),textBefore,StringComparison.Ordinal);
                    detail="text=["+OneLine(Buffer(window))+"]";
                } else if (id=="quote") {
                    reached=Buffer(window).StartsWith(">",StringComparison.Ordinal);
                    detail="text=["+OneLine(Buffer(window))+"]";
                } else if (id=="rule") {
                    reached=Buffer(window).IndexOf("---",StringComparison.Ordinal)>=0&&!String.Equals(Buffer(window),textBefore,StringComparison.Ordinal);
                    detail="text=["+OneLine(Buffer(window))+"]";
                } else if (id=="dark") {
                    reached=FieldBool(window,"dark")!=darkBefore;
                    detail="dark="+FieldBool(window,"dark");
                } else if (id=="wrap") {
                    reached=FieldBool(window,"wordWrap")!=wrapBefore;
                    detail="wrap="+FieldBool(window,"wordWrap");
                } else {
                    reached=Math.Abs((double)FieldObject(window,"fontSize")-fontBefore)>0.01;
                    detail="fontSize="+Convert.ToString(FieldObject(window,"fontSize"),CultureInfo.InvariantCulture);
                }
                Check("shortcut:F-key","F"+(i+1).ToString(CultureInfo.InvariantCulture)+"="+id,handled,
                    "the window handles the function key","handled="+handled);
                Check("shortcut:dispatch","F"+(i+1).ToString(CultureInfo.InvariantCulture)+"="+id,reached,
                    "the assigned F key runs its command",detail);
            }

            // An unassigned F key must leave the event unhandled, so the IME (for example the
            // Japanese IME's F6-F10 conversion outside composition) still receives it.
            string[] noneKeys=new string[12];
            for (int j=0;j<noneKeys.Length;j++) noneKeys[j]="none";
            SetStaticField("functionKeys",noneKeys);
            bool noneHandled=true;
            RunStep("shortcut:F7=none",delegate { noneHandled=RaiseKey(window,Key.F7); });
            Check("shortcut:unassigned","F7=none",!noneHandled,
                "an unassigned F key leaves the event unhandled","handled="+noneHandled);

            if (oldKeys!=null) SetStaticField("functionKeys",oldKeys);

            // Every ShortcutSettings id, through ExecuteFunction (the F-key dispatch target) and its
            // toolbar/menu counterpart.
            string[] ids=(string[])StaticField<string[]>("ShortcutIds");
            for (int i=0;i<ids.Length;i++) {
                string id=ids[i];
                if (id=="none") {
                    ResetBaseline(window,"None shortcut\n",0);
                    RunStep("shortcut:none",delegate { Call(window,"ExecuteFunction",new object[] {"none"}); Pump(); });
                    Check("shortcut:noop","none",String.Equals(Buffer(window),"None shortcut\n",StringComparison.Ordinal),
                        "the unassigned shortcut changes nothing","text=["+OneLine(Buffer(window))+"]");
                } else if (id=="edit") {
                    ShortcutParity(window,id,"Edit mode fixture\n",0,delegate { Press(ButtonField(window,"editButton")); Pump(); },false);
                } else if (id=="source") {
                    ShortcutParity(window,id,"Source mode fixture\n",0,delegate {
                        var preview=Field<CheckBox>(window,"preview");
                        preview.IsChecked=!(preview.IsChecked==true);
                        Pump();
                    },false);
                } else if (id=="dark") {
                    ShortcutParity(window,id,"Dark fixture\n",0,delegate { Press(ButtonField(window,"quickDark")); Pump(); },false);
                } else if (id=="wrap") {
                    ShortcutParity(window,id,"Wrap fixture\n",0,delegate { Call(window,"SetWrap",new object[] {!FieldBool(window,"wordWrap")}); Pump(); },false);
                } else if (id=="outline") {
                    ShortcutParity(window,id,"Outline fixture\n",0,delegate { Press(ButtonField(window,"outlineButton")); Pump(); },false);
                } else if (id=="zoomIn"||id=="zoomOut"||id=="zoomReset") {
                    double delta=id=="zoomIn"?2:id=="zoomOut"?-2:0;
                    if (delta==0) ShortcutParity(window,id,"Zoom reset fixture\n",0,delegate { Call(window,"SetBaseFont",new object[] {(string)FieldObject(window,"family"),16.0}); Pump(); },false);
                    else ShortcutParity(window,id,"Zoom fixture\n",0,delegate { Call(window,"Zoom",new object[] {delta}); Pump(); },false);
                } else if (id=="previousTab"||id=="nextTab") {
                    TabShortcutParity(window,id,id=="nextTab"?1:-1);
                } else if (id=="heading") {
                    ShortcutParity(window,id,"plain paragraph\n",3,delegate { Press(ButtonField(window,"headingButton")); Pump(); },true);
                } else if (id=="paragraph") {
                    ShortcutParity(window,id,"# heading line\n",3,delegate { Press(MenuItemField(window,"headingBodyItem")); Pump(); },true);
                } else if (id=="h1"||id=="h2"||id=="h3"||id=="h4"||id=="h5"||id=="h6") {
                    int level=id[1]-'1';
                    var headingButtons=FieldObject(window,"headingButtons") as IList;
                    MenuItem target=headingButtons==null?null:headingButtons[level] as MenuItem;
                    ShortcutParity(window,id,"heading shortcut line\n",3,delegate { Press(target); Pump(); },true);
                } else if (id=="list") {
                    ShortcutParity(window,id,"list shortcut line\n",3,delegate { Press(ButtonField(window,"listButton")); Pump(); },true);
                } else if (id=="bullet"||id=="number") {
                    MenuItem target=MenuItemField(window,id=="bullet"?"listBulletMenuItem":"listNumberMenuItem");
                    ShortcutParity(window,id,"list kind line\n",3,delegate { Press(target); Pump(); },true);
                } else if (id=="task") {
                    ShortcutParity(window,id,"task shortcut line\n",3,delegate { Press(ButtonField(window,"taskButton")); Pump(); },true);
                } else if (id=="check") {
                    ShortcutParity(window,id,"- [ ] task line\n",7,delegate { Call(window,"ToggleCurrentCheck"); Pump(); },true);
                } else if (id=="quote") {
                    ShortcutParity(window,id,"quote shortcut line\n",3,delegate { Press(ButtonField(window,"quoteButton")); Pump(); },true);
                } else if (id=="rule") {
                    ShortcutParity(window,id,"rule shortcut line\n",3,delegate { Press(ButtonField(window,"ruleButton")); Pump(); },true);
                } else if (id=="inline") {
                    ShortcutParity(window,id,"inline shortcut word\n",3,delegate { Press(ButtonField(window,"codeButton")); Pump(); },true);
                } else if (id=="block") {
                    ShortcutParity(window,id,"block shortcut line\n",3,delegate { Press(ButtonField(window,"blockButton")); Pump(); },true);
                } else if (id=="strike") {
                    ShortcutParity(window,id,"strike shortcut word\n",3,delegate { Press(ButtonField(window,"strikeButton")); Pump(); },true);
                } else if (id=="clearColor") {
                    ResetBaseline(window,"Colour fixture line\n",0);
                    SetField(window,"customColor",System.Windows.Media.Colors.Red);
                    Call(window,"RenderPreservingView"); Pump();
                    RunStep("shortcut:clearColor:fkey",delegate { Call(window,"ExecuteFunction",new object[] {id}); Pump(); });
                    Flush(window);
                    string fkey=Outcome(window);
                    ResetBaseline(window,"Colour fixture line\n",0);
                    SetField(window,"customColor",System.Windows.Media.Colors.Red);
                    Call(window,"RenderPreservingView"); Pump();
                    RunStep("shortcut:clearColor:toolbar",delegate { Call(window,"ClearTextColor"); Pump(); });
                    Flush(window);
                    Check("shortcut:parity",id,String.Equals(fkey,Outcome(window),StringComparison.Ordinal),
                        "the colour reset shortcut and the toolbar reset reach the same state",Diff(fkey,Outcome(window)));
                } else if (id=="rowAdd"||id=="rowRemove"||id=="columnAdd"||id=="columnRemove"||id=="tableDelete"||id=="tableParagraph") {
                    int action=id=="rowAdd"?0:id=="rowRemove"?1:id=="columnAdd"?2:id=="columnRemove"?3:id=="tableDelete"?4:5;
                    TableShortcutParity(window,id,action);
                } else if (id=="color") {
                    ResetBaseline(window,"Colour popup fixture\n",0);
                    RunStep("shortcut:color",delegate { Call(window,"ExecuteFunction",new object[] {"color"}); Pump(); });
                    var popup=(System.Windows.Controls.Primitives.Popup)FieldObject(window,"colorPopup");
                    Check("shortcut:effect","color",popup!=null&&popup.IsOpen,"the colour shortcut opens the palette","open="+(popup!=null&&popup.IsOpen));
                    if (popup!=null) popup.IsOpen=false;
                    Check("shortcut:effect","color",String.Equals(Buffer(window),"Colour popup fixture\n",StringComparison.Ordinal),
                        "the colour palette does not change the text","text=["+OneLine(Buffer(window))+"]");
                } else if (id=="size") {
                    ResetBaseline(window,"Size box fixture\n",0);
                    RunStep("shortcut:size",delegate { Call(window,"ExecuteFunction",new object[] {"size"}); Pump(); });
                    var sizes=Field<ComboBox>(window,"sizes");
                    Check("shortcut:effect","size",sizes.IsDropDownOpen,"the size shortcut opens the size list","open="+sizes.IsDropDownOpen);
                    sizes.IsDropDownOpen=false;
                    Check("shortcut:effect","size",String.Equals(Buffer(window),"Size box fixture\n",StringComparison.Ordinal),
                        "the size list does not change the text","text=["+OneLine(Buffer(window))+"]");
                } else if (id=="link") {
                    ResetBaseline(window,"Link shortcut fixture\n",0);
                    ModalResult first=RunModal(window,delegate { Call(window,"ExecuteFunction",new object[] {"link"}); },100);
                    string firstText=Buffer(window);
                    ResetBaseline(window,"Link shortcut fixture\n",0);
                    ModalResult second=RunModal(window,delegate { Press(ButtonField(window,"linkButton")); },100);
                    Check("shortcut:parity",id,first.Saw&&second.Saw&&first.Closed&&second.Closed&&
                        String.Equals(firstText,Buffer(window),StringComparison.Ordinal),
                        "the link shortcut opens the same prompt and leaves the text alone",
                        "fkeySaw="+first.Saw+", toolbarSaw="+second.Saw+", text=["+OneLine(Buffer(window))+"]");
                } else if (id=="table") {
                    ResetBaseline(window,"Table shortcut fixture\n",0);
                    ModalResult custom=RunModal(window,delegate { Call(window,"ExecuteFunction",new object[] {"table"}); },100);
                    Check("shortcut:effect",id,custom.Saw,"the table shortcut opens its size prompt","saw="+custom.Saw);
                    Check("shortcut:effect",id,String.Equals(Buffer(window),"Table shortcut fixture\n",StringComparison.Ordinal),
                        "the table shortcut does not change the text when cancelled","text=["+OneLine(Buffer(window))+"]");
                    ResetBaseline(window,"Table shortcut fixture\n",0);
                    RunStep("shortcut:table:toolbar",delegate { Press(ButtonField(window,"tableButton")); Pump(); });
                    var picker=(ContextMenu)FieldObject(window,"tablePickerMenu");
                    Check("shortcut:effect",id,picker!=null&&picker.IsOpen,"the toolbar table button opens the size picker",
                        "open="+(picker!=null&&picker.IsOpen));
                    if (picker!=null) picker.IsOpen=false;
                } else if (id=="image") {
                    skipped.Add("단축키 image: native OpenFileDialog (not driven headless)");
                } else {
                    skipped.Add("shortcut id not exercised: "+id);
                }
            }

            // Ctrl shortcuts: Keyboard.Modifiers cannot be synthesized headlessly, so each one is
            // driven through the exact handler target its switch case calls.
            ResetBaseline(window,"Ctrl helpers fixture\n",0);
            RunStep("ctrl:select-all",delegate { Source(window).SelectAll(); Pump(); });
            Check("ctrl:select-all","ctrl:select-all",Source(window).SelectionLength==Buffer(window).Length,
                "Ctrl+A selects the document","selection="+Source(window).SelectionLength+", length="+Buffer(window).Length);
            ResetBaseline(window,"Ctrl undo fixture\n",0);
            RunStep("ctrl:undo-redo",delegate {
                PlaceCaret(window,Buffer(window).Length);
                Type(window,"XY"); Pump();
                Call(window,"Undo"); Pump();
                Check("ctrl:undo","ctrl:undo-redo",String.Equals(Buffer(window),"Ctrl undo fixture\n",StringComparison.Ordinal),
                    "Ctrl+Z removes the last edit",Diff("Ctrl undo fixture\n",Buffer(window)));
                Call(window,"Redo"); Pump();
                Check("ctrl:redo","ctrl:undo-redo",String.Equals(Buffer(window),"Ctrl undo fixture\nXY",StringComparison.Ordinal),
                    "Ctrl+Y restores it",Diff("Ctrl undo fixture\nXY",Buffer(window)));
            });
            ResetBaseline(window,"Ctrl new fixture\n",0);
            RunStep("ctrl:new-document",delegate {
                int before=Documents(window).Count;
                Call(window,"NewFile"); Pump();
                Check("ctrl:new","ctrl:new-document",Documents(window).Count==before+1,"Ctrl+N creates a new tab",
                    "documents="+Documents(window).Count+" (was "+before+")");
                object extra=ActiveDocument(window);
                SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                Call(window,"CloseTab",new object[] {extra});
                ClearSaveChoices(window); Pump();
            });
            ResetBaseline(window,"Ctrl tab first\n",0);
            RunStep("ctrl:next-tab-setup",delegate {
                Call(window,"CreateTab"); Call(window,"SetText",new object[] {"Ctrl tab second\n"}); Call(window,"SetMode",new object[] {true});
                SetField(window,"saved",Buffer(window)); Pump(); SelectTab(window,0); Pump();
                Call(window,"CycleTab",new object[] {1}); Pump();
                Check("ctrl:next-tab","ctrl:next-tab-setup",Tabs(window).SelectedIndex==1,"Ctrl+Tab moves to the next tab","index="+Tabs(window).SelectedIndex);
                Call(window,"CycleTab",new object[] {-1}); Pump();
                Check("ctrl:previous-tab","ctrl:next-tab-setup",Tabs(window).SelectedIndex==0,"Ctrl+Shift+Tab moves to the previous tab","index="+Tabs(window).SelectedIndex);
                object extra=Documents(window)[Documents(window).Count-1];
                SelectTab(window,1); SaveChoices(window,1,MainWindow.SaveChoice.Discard); Call(window,"CloseTab",new object[] {extra}); ClearSaveChoices(window); Pump();
            });
            ResetBaseline(window,"Ctrl close fixture\n",0);
            RunStep("ctrl:close-tab",delegate {
                int before=Documents(window).Count;
                Type(window," close me"); Pump();
                object active=ActiveDocument(window);
                SaveChoices(window,1,MainWindow.SaveChoice.Discard);
                Call(window,"CloseTab",new object[] {active});
                ClearSaveChoices(window); Pump();
                Check("ctrl:close-tab","ctrl:close-tab",Documents(window).Count==before-1||Documents(window).Count==1,
                    "Ctrl+W closes the active tab (or leaves a fresh one)","documents="+Documents(window).Count+" (was "+before+")");
            });
            ResetBaseline(window,"Ctrl edit fixture\n",0);
            RunStep("ctrl:toggle-edit",delegate {
                Call(window,"Toggle"); Pump();
                Check("ctrl:edit","ctrl:toggle-edit",!FieldBool(window,"editing"),"Ctrl+E leaves editing mode","editing="+FieldBool(window,"editing"));
                Call(window,"Toggle"); Pump();
                Check("ctrl:edit","ctrl:toggle-edit",FieldBool(window,"editing"),"Ctrl+E enters editing mode","editing="+FieldBool(window,"editing"));
            });
            RunStep("ctrl:find",delegate { Call(window,"ToggleSearch",new object[] {false}); Pump(); });
            Check("ctrl:find","ctrl:find",Field<Border>(window,"searchHost").Visibility==Visibility.Visible,"Ctrl+F opens find",
                "visible="+Field<Border>(window,"searchHost").Visibility);
            RunStep("ctrl:replace",delegate { Call(window,"ToggleSearch",new object[] {true}); Pump(); });
            Check("ctrl:replace","ctrl:replace",Field<StackPanel>(window,"replaceRow").Visibility==Visibility.Visible,"Ctrl+H opens replace",
                "visible="+Field<StackPanel>(window,"replaceRow").Visibility);
            RunStep("ctrl:find-close",delegate { Call(window,"CloseSearch"); });
            ResetBaseline(window,"Ctrl save fixture\n",0);
            RunStep("ctrl:save",delegate {
                PlaceCaret(window,Buffer(window).Length);
                Type(window," saved"); Pump();
                string target=SaveTarget("ctrl-save");
                RemoveFile(target);
                SetField(window,"path",target);
                Call(window,"Save",new object[] {false});
                PumpMs(40);
                bool written=WaitForFile(target,600);
                Check("ctrl:save","ctrl:save",written&&String.Equals(ReadUtf8(target),Buffer(window),StringComparison.Ordinal),
                    "Ctrl+S writes the buffer","exists="+written+", buffer=["+OneLine(Buffer(window))+"]");
            });
            ResetBaseline(window,"Ctrl zoom fixture\n",0);
            RunStep("ctrl:zoom",delegate {
                Call(window,"Zoom",new object[] {2.0}); Pump();
                Check("ctrl:zoom","ctrl:zoom",Math.Abs((double)FieldObject(window,"fontSize")-18)<0.01,"Ctrl++ enlarges",
                    "fontSize="+Convert.ToString(FieldObject(window,"fontSize"),CultureInfo.InvariantCulture));
                Call(window,"Zoom",new object[] {-2.0}); Pump();
                Call(window,"SetBaseFont",new object[] {(string)FieldObject(window,"family"),16.0}); Pump();
                Check("ctrl:zoom","ctrl:zoom",Math.Abs((double)FieldObject(window,"fontSize")-16)<0.01,"Ctrl+0 resets the size",
                    "fontSize="+Convert.ToString(FieldObject(window,"fontSize"),CultureInfo.InvariantCulture));
            });
            ResetBaseline(window,"bold word\n",0);
            RunStep("ctrl:bold",delegate {
                Source(window).Select(0,4);
                Call(window,"ToggleFormat",new object[] {"bold"}); Pump();
                Check("ctrl:bold","ctrl:bold",Buffer(window).IndexOf("**bold**",StringComparison.Ordinal)>=0,
                    "Ctrl+B writes the bold pair","text=["+OneLine(Buffer(window))+"]");
            });
            ResetBaseline(window,"italic word\n",0);
            RunStep("ctrl:italic",delegate {
                Source(window).Select(0,6);
                Call(window,"ToggleFormat",new object[] {"italic"}); Pump();
                Check("ctrl:italic","ctrl:italic",Buffer(window).IndexOf("*italic*",StringComparison.Ordinal)>=0,
                    "Ctrl+I writes the italic pair","text=["+OneLine(Buffer(window))+"]");
            });
            skipped.Add("Ctrl+O / Ctrl+P / Ctrl+K / Ctrl+Shift+I (native dialogs or link prompt outside this probe)");
            skipped.Add("Ctrl+C / Ctrl+X / Ctrl+V / Ctrl+Shift+V (system clipboard is shared with the desktop)");
        } catch (Exception ex) {
            Fail("shortcut:scenario","shortcut","the shortcut scenario completes",ExceptionText(ex));
        } finally {
            if (oldKeys!=null) { try { SetStaticField("functionKeys",oldKeys); } catch (Exception) { } }
            CloseQuiet(window);
        }
    }

    // ---------------------------------------------------------------- overflow scenario
    static ContextMenu OverflowMenu(MainWindow window,bool ribbon) {
        return (ContextMenu)FieldObject(window,ribbon?"ribbonOverflowMenu":"topOverflowMenu");
    }
    static Button OverflowBtn(MainWindow window,bool ribbon) {
        return (Button)FieldObject(window,ribbon?"ribbonOverflow":"topOverflow");
    }
    static MenuItem OverflowItem(MainWindow window,ContextMenu menu,string holderField) {
        if (menu==null) return null;
        object holder;
        try { holder=FieldObject(window,holderField); } catch (Exception) { return null; }
        foreach (object entry in menu.Items) {
            var item=entry as MenuItem;
            if (item==null||item.Tag==null) continue;
            FieldInfo field=item.Tag.GetType().GetField("Holder",Members);
            if (field==null) continue;
            if (Object.ReferenceEquals(field.GetValue(item.Tag),holder)) return item;
        }
        return null;
    }
    static MenuItem FindLanguageMenu(MenuItem parent,int depth) {
        if (parent==null||depth>4) return null;
        foreach (object entry in parent.Items) {
            var item=entry as MenuItem;
            if (item==null) continue;
            foreach (object childEntry in item.Items) {
                var child=childEntry as MenuItem;
                if (child!=null&&String.Equals(Convert.ToString(child.Tag),"en",StringComparison.Ordinal)) return item;
            }
            MenuItem nested=FindLanguageMenu(item,depth+1);
            if (nested!=null) return nested;
        }
        return null;
    }
    static MenuItem LanguageMenu(MainWindow window) {
        Menu menu=TopMenu(window);
        if (menu!=null) {
            foreach (object entry in menu.Items) {
                var item=entry as MenuItem;
                MenuItem found=FindLanguageMenu(item,0);
                if (found!=null) return found;
            }
        }
        ContextMenu top=OverflowMenu(window,false);
        if (top!=null) {
            foreach (object entry in top.Items) {
                var item=entry as MenuItem;
                if (item==null) continue;
                if (HasLanguageChild(item)) return item;
                MenuItem found=FindLanguageMenu(item,0);
                if (found!=null) return found;
            }
        }
        // The language items know their own parent even after the menu was moved between hosts.
        try {
            var items=FieldObject(window,"languageItems") as IList;
            if (items!=null&&items.Count>0) {
                var first=items[0] as MenuItem;
                if (first!=null&&first.Parent is MenuItem) return (MenuItem)first.Parent;
            }
        } catch (Exception) { }
        return null;
    }
    static bool HasLanguageChild(MenuItem item) {
        if (item==null) return false;
        foreach (object entry in item.Items) {
            var child=entry as MenuItem;
            if (child==null) continue;
            string tag=Convert.ToString(child.Tag);
            if (tag=="en"||tag=="ja"||tag=="zh-CN"||tag=="zh-TW"||tag=="ko") return true;
        }
        return false;
    }
    static bool ContainsMenuItem(Menu menu,MenuItem item) {
        if (menu==null||item==null) return false;
        foreach (object entry in menu.Items) if (Object.ReferenceEquals(entry,item)) return true;
        return false;
    }
    static void ScenarioOverflow() {
        currentMode="live";
        MainWindow window=null;
        try {
            window=NewWindow(420,700);
            Call(window,"SetLiveMode",new object[] {true});
            Call(window,"SetText",new object[] {"Overflow fixture **bold** word\n"});
            Call(window,"SetMode",new object[] {true});
            SetField(window,"saved",Buffer(window));
            Pump();
            window.UpdateLayout();
            Pump();

            RunStep("overflow:measure",delegate { Call(window,"UpdateToolbarOverflow"); Pump(); });
            Check("overflow:shown","overflow:measure",window.ToolbarOverflowShown(true)&&window.ToolbarOverflowShown(false),
                "both toolbars show the overflow button at 420 px","ribbon="+window.ToolbarOverflowShown(true)+", top="+window.ToolbarOverflowShown(false));
            Check("overflow:shown","overflow:measure",window.ToolbarOverflowSources(true).Length>0&&window.ToolbarOverflowSources(false).Length>0,
                "the overflow menus hold the commands that did not fit","ribbonCount="+window.ToolbarOverflowSources(true).Length+", topCount="+window.ToolbarOverflowSources(false).Length);
            var ribbonSources=window.ToolbarOverflowSources(true);
            var topSources=window.ToolbarOverflowSources(false);
            Check("overflow:menu","overflow:measure",ribbonSources.Length>0&&
                window.ToolbarOverflowMenuHas(true,ribbonSources[ribbonSources.Length-1]),
                "the ribbon overflow lists its last measured command","sources="+ribbonSources.Length);
            /* 2026-11-09: the 목차 toggle is never folded into "»" (InsertControls.BuildTopOverflowMenu),
               so the last unit that can move into the list is the one before it. */
            FrameworkElement lastFoldableTop=null;
            for(int i=topSources.Length-1;i>=0&&lastFoldableTop==null;i--)if(!Object.ReferenceEquals(topSources[i],ButtonField(window,"outlineButton")))lastFoldableTop=topSources[i];
            Check("overflow:menu","overflow:measure",lastFoldableTop!=null&&
                window.ToolbarOverflowMenuHas(false,lastFoldableTop),
                "the top overflow lists its last foldable command","sources="+topSources.Length);

            // Bold through the overflow clone and through the toolbar button.
            ResetBaseline(window,"word\n",0);
            Source(window).Select(0,4);
            Press(ButtonField(window,"boldButton")); Pump();
            string toolbarBold=Buffer(window);
            ResetBaseline(window,"word\n",0);
            Source(window).Select(0,4); Pump();
            MenuItem boldOverflow=OverflowItem(window,OverflowMenu(window,true),"boldButton");
            if (boldOverflow!=null) {
                boldOverflow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check("overflow:parity","overflow:bold",String.Equals(toolbarBold,Buffer(window),StringComparison.Ordinal),
                    "the overflow bold command matches the toolbar bold button",Diff(toolbarBold,Buffer(window)));
            } else {
                Note("bold stays visible at 420 px, so the bold overflow clone is not exercised; its toolbar path is covered by the shortcut parity tests");
            }

            // Heading submenu entry.
            ResetBaseline(window,"plain heading line\n",3);
            Press(ButtonField(window,"headingButton")); Pump();
            string toolbarHeading=Buffer(window);
            ResetBaseline(window,"plain heading line\n",3); Pump();
            MenuItem headingOverflow=OverflowItem(window,OverflowMenu(window,true),"headingSlot");
            MenuItem headingLevel=headingOverflow==null?null:ChildItem(headingOverflow,"제목 2");
            if (headingLevel!=null) {
                headingLevel.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check("overflow:parity","overflow:heading",String.Equals(toolbarHeading,Buffer(window),StringComparison.Ordinal),
                    "the overflow heading command matches the toolbar heading button",Diff(toolbarHeading,Buffer(window)));
            } else Note("the heading dropdown stays visible at 420 px, so its overflow submenu is not exercised");

            // List submenu entry.
            ResetBaseline(window,"plain list line\n",3);
            Press(MenuItemField(window,"listBulletMenuItem")); Pump();
            string toolbarList=Buffer(window);
            ResetBaseline(window,"plain list line\n",3); Pump();
            MenuItem listOverflow=OverflowItem(window,OverflowMenu(window,true),"listHover");
            MenuItem bullet=ParentChildByPrefix(listOverflow,"· ");
            if (bullet!=null) {
                bullet.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check("overflow:parity","overflow:list",String.Equals(toolbarList,Buffer(window),StringComparison.Ordinal),
                    "the overflow list command matches the list menu",Diff(toolbarList,Buffer(window)));
            } else Note("the list dropdown stays visible at 420 px, so its overflow submenu is not exercised");

            // Task button through the overflow.
            ResetBaseline(window,"plain task line\n",3);
            Press(ButtonField(window,"taskButton")); Pump();
            string toolbarTask=Buffer(window);
            ResetBaseline(window,"plain task line\n",3); Pump();
            MenuItem taskOverflow=OverflowItem(window,OverflowMenu(window,true),"taskButton");
            if (taskOverflow!=null) {
                taskOverflow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check("overflow:parity","overflow:task",String.Equals(toolbarTask,Buffer(window),StringComparison.Ordinal),
                    "the overflow task command matches the toolbar task button",Diff(toolbarTask,Buffer(window)));
            } else Note("the task button stays visible at 420 px, so its overflow clone is not exercised");

            // Top-row dark and edit clones.
            ResetBaseline(window,"overflow dark fixture\n",0);
            Press(ButtonField(window,"quickDark")); Pump();
            bool toolbarDark=FieldBool(window,"dark");
            ResetBaseline(window,"overflow dark fixture\n",0); Pump();
            MenuItem darkOverflow=OverflowItem(window,OverflowMenu(window,false),"quickDark");
            if (darkOverflow!=null) {
                darkOverflow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check("overflow:parity","overflow:dark",FieldBool(window,"dark")==toolbarDark,
                    "the top overflow dark command matches the toolbar toggle","dark="+FieldBool(window,"dark")+", toolbar="+toolbarDark);
            } else Note("the dark toggle stays visible at 420 px, so its top overflow clone is not exercised");
            ResetBaseline(window,"overflow edit fixture\n",0);
            Press(ButtonField(window,"editButton")); Pump();
            bool toolbarEditing=FieldBool(window,"editing");
            ResetBaseline(window,"overflow edit fixture\n",0); Pump();
            MenuItem editOverflow=OverflowItem(window,OverflowMenu(window,false),"editButton");
            if (editOverflow!=null) {
                editOverflow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check("overflow:parity","overflow:edit",FieldBool(window,"editing")==toolbarEditing,
                    "the top overflow edit command matches the toolbar toggle","editing="+FieldBool(window,"editing")+", toolbar="+toolbarEditing);
            } else Note("the edit toggle stays visible at 420 px, so its top overflow clone is not exercised");

            // The language menu moved into the overflow must still be live.
            MenuItem languageMenu=LanguageMenu(window);
            Check("overflow:menu","overflow:language",languageMenu!=null,
                "the language menu is reachable from the overflow","missing; headers=["+TopHeaders(window)+"]");
            if (languageMenu!=null) {
                Check("overflow:menu","overflow:language",window.ToolbarOverflowMenuHas(false,languageMenu)&&!ContainsMenuItem(TopMenu(window),languageMenu),
                    "the language menu is moved out of the narrow menu bar into the top overflow","moved="+window.ToolbarOverflowMenuHas(false,languageMenu));
                MenuItem english=LanguageChild(languageMenu,"en");
                RunStep("overflow:language-en",delegate { ClickItem(english); Pump(); });
                Check("overflow:parity","overflow:language-en",String.Equals((string)FieldObject(window,"language"),"en",StringComparison.Ordinal),
                    "a language item inside the overflow still switches the UI","language="+Convert.ToString(FieldObject(window,"language")));
                CheckNoKorean(window,"en","overflow:language-en");
                MenuItem korean=LanguageChild(languageMenu,"ko");
                RunStep("overflow:language-ko",delegate { ClickItem(korean); Pump(); });
            }

            // Open both overflow popups, then change theme and language while they are open (the
            // b56 close path), and close them.
            RunStep("overflow:open",delegate {
                Button top=OverflowBtn(window,false);
                Button ribbon=OverflowBtn(window,true);
                if (top!=null) { top.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); }
                if (ribbon!=null) { ribbon.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(); }
            });
            RunStep("overflow:theme-open",delegate { Call(window,"SetDark",new object[] {true}); Pump(); });
            RunStep("overflow:language-open",delegate {
                SetField(window,"language","en");
                Call(window,"ApplyLanguage");
                Pump();
            });
            RunStep("overflow:close",delegate {
                ContextMenu top=OverflowMenu(window,false);
                ContextMenu ribbon=OverflowMenu(window,true);
                if (top!=null) top.IsOpen=false;
                if (ribbon!=null) ribbon.IsOpen=false;
                Call(window,"SetDark",new object[] {false});
                SetField(window,"language","ko");
                Call(window,"ApplyLanguage");
                Pump();
            });

            // The size command's submenu in the ribbon overflow.
            ResetBaseline(window,"overflow size fixture\n",0);
            RunStep("overflow:size",delegate {
                MenuItem sizeOverflow=OverflowItem(window,OverflowMenu(window,true),"sizes");
                MenuItem entry=sizeOverflow==null?null:ChildItem(sizeOverflow,"20 px");
                if (entry==null) {
                    Note("the size box stays visible at 420 px, so its overflow submenu is not exercised");
                    return;
                }
                entry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
                Check("overflow:size","overflow:size",true,"the size entry applies without an exception","ok");
            });
            ResetBaseline(window,"overflow size fixture\n",0);

            // Widen: the overflow disappears and every moved menu returns to the menu bar.
            RunStep("overflow:wide",delegate { window.Width=1200; window.UpdateLayout(); Pump(); });
            Check("overflow:restored","overflow:wide",!window.ToolbarOverflowShown(true)&&!window.ToolbarOverflowShown(false),
                "the overflow buttons disappear when the window is wide again","ribbon="+window.ToolbarOverflowShown(true)+", top="+window.ToolbarOverflowShown(false));
            MenuItem returnedLanguage=LanguageMenu(window);
            Check("overflow:restored","overflow:wide",returnedLanguage!=null&&ContainsMenuItem(TopMenu(window),returnedLanguage)&&
                !window.ToolbarOverflowMenuHas(false,returnedLanguage),
                "the language menu returns to the menu bar","returned="+(returnedLanguage!=null)+", headers=["+TopHeaders(window)+"]");
            ContextMenu topMenuOverflow=OverflowMenu(window,false);
            Check("overflow:restored","overflow:wide",topMenuOverflow!=null&&topMenuOverflow.Items.Count==0,
                "no moved menu is duplicated in the top overflow","items="+(topMenuOverflow==null?-1:topMenuOverflow.Items.Count));
            Check("overflow:restored","overflow:wide",ButtonField(window,"boldButton").Visibility==Visibility.Visible&&
                ButtonField(window,"taskButton").Visibility==Visibility.Visible,"the ribbon commands are visible again",
                "bold="+ButtonField(window,"boldButton").Visibility+", task="+ButtonField(window,"taskButton").Visibility);
        } catch (Exception ex) {
            Fail("overflow:scenario","overflow","the overflow scenario completes",ExceptionText(ex));
        } finally {
            CloseQuiet(window);
        }
    }
    static MenuItem ParentChildByPrefix(MenuItem parent,string prefix) {
        if (parent==null) return null;
        foreach (object entry in parent.Items) {
            var item=entry as MenuItem;
            string header=item==null?null:HeaderText(item.Header);
            if (header!=null&&header.StartsWith(prefix,StringComparison.Ordinal)) return item;
        }
        return null;
    }

    // ---------------------------------------------------------------- window scenario
    static void ScenarioWindows() {
        currentMode="live";
        MainWindow first=null,second=null,closer=null;
        try {
            first=NewWindow(1000,720);
            Call(first,"SetLiveMode",new object[] {true});
            Call(first,"SetText",new object[] {"Window A document\n"});
            Call(first,"SetMode",new object[] {true});
            SetField(first,"saved",Buffer(first));
            Pump();

            RunStep("windows:tabs-off",delegate { Call(first,"SetOpenInTabs",new object[] {false}); Pump(); });
            Check("window:tabs-off","windows:tabs-off",Tabs(first).Visibility!=Visibility.Visible,"tabs off hides the strip",
                "strip="+Tabs(first).Visibility);

            HashSet<MainWindow> before=MainWindowSet();
            RunStep("windows:new-window",delegate { Call(first,"NewFile"); Pump(); });
            var created=NewWindowsSince(before);
            second=created.Count==1?created[0]:null;
            if (second!=null) tracked.Add(second);
            Check("window:new","windows:new-window",second!=null&&String.Equals(Buffer(second),"",StringComparison.Ordinal),
                "Ctrl+N with tabs off opens a new empty window","windows="+created.Count+", text=["+(second==null?"-":OneLine(Buffer(second)))+"]");
            Check("window:new","windows:new-window",String.Equals(Buffer(first),"Window A document\n",StringComparison.Ordinal),
                "the first window keeps its text","text=["+OneLine(Buffer(first))+"]");
            if (second!=null) {
                RunStep("windows:type-second",delegate { Call(second,"SetMode",new object[] {true}); Call(second,"SetText",new object[] {"Window B document\n"}); Pump(); });
                Check("window:new","windows:type-second",String.Equals(Buffer(first),"Window A document\n",StringComparison.Ordinal),
                    "typing in the new window leaves the first alone","first=["+OneLine(Buffer(first))+"]");
                RunStep("windows:close-second",delegate { CloseQuiet(second); Pump(); });
                Check("window:new","windows:close-second",!second.IsLoaded,"the second window closes","loaded="+second.IsLoaded);
                second=null;
            }

            // A file opened with tabs off while the window is not blank becomes another window.
            before=MainWindowSet();
            RunStep("windows:open-file",delegate { Call(first,"LoadFile",new object[] {fixtureA}); Pump(); });
            created=NewWindowsSince(before);
            second=created.Count==1?created[0]:null;
            if (second!=null) tracked.Add(second);
            Check("window:open","windows:open-file",second!=null&&Buffer(second).IndexOf("Alpha heading",StringComparison.Ordinal)>=0&&
                String.Equals(Buffer(first),"Window A document\n",StringComparison.Ordinal),
                "opening a file with tabs off opens it in a new window and leaves the first alone",
                "windows="+created.Count+", newText=["+(second==null?"-":OneLine(Buffer(second)))+"]");
            if (second!=null) { CloseQuiet(second); second=null; }

            // Turning tabs back on, then off, moves the other tab into its own window unchanged.
            RunStep("windows:tabs-on",delegate { Call(first,"SetOpenInTabs",new object[] {true}); Pump(); });
            RunStep("windows:tab-setup",delegate {
                Call(first,"CreateTab");
                Call(first,"SetText",new object[] {"Moved tab text\n"});
                Call(first,"SetMode",new object[] {true});
                SetField(first,"saved",Buffer(first));
                Pump();
                SelectTab(first,0);
                Pump();
            });
            object movedTab=Documents(first).Count>1?Documents(first)[1]:null;
            string movedText=movedTab==null?"":TabString(movedTab,"Text");
            before=MainWindowSet();
            RunStep("windows:uncheck-tabs",delegate { Call(first,"SetOpenInTabs",new object[] {false}); Pump(); });
            created=NewWindowsSince(before);
            MainWindow moved=created.Count==1?created[0]:null;
            if (moved!=null) tracked.Add(moved);
            Check("window:move-tab","windows:uncheck-tabs",moved!=null&&Documents(first).Count==1&&
                String.Equals(Buffer(moved),movedText,StringComparison.Ordinal)&&Tabs(first).Visibility!=Visibility.Visible,
                "unchecking tabs moves the other document to a window with its text",
                "windows="+created.Count+", documents="+Documents(first).Count+", movedText=["+(moved==null?"-":OneLine(Buffer(moved)))+"]");
            if (moved!=null) { CloseQuiet(moved); }

            // Closing a window with two dirty tabs: Cancel keeps both, Discard closes the window.
            closer=NewWindow(1000,720);
            Call(closer,"SetLiveMode",new object[] {true});
            Call(closer,"SetText",new object[] {"Close C1\n"});
            Call(closer,"SetMode",new object[] {true});
            SetField(closer,"saved",Buffer(closer));
            Call(closer,"CreateTab");
            Call(closer,"SetText",new object[] {"Close C2\n"});
            Call(closer,"SetMode",new object[] {true});
            SetField(closer,"saved",Buffer(closer));
            Pump();
            SelectTab(closer,0); Pump();
            PlaceCaret(closer,Buffer(closer).Length); Type(closer," dirty1"); Pump();
            SelectTab(closer,1); Pump();
            PlaceCaret(closer,Buffer(closer).Length); Type(closer," dirty2"); Pump();
            SaveChoices(closer,1,MainWindow.SaveChoice.Cancel);
            RunStep("windows:close-cancel",delegate { closer.Close(); PumpMs(60); });
            if (SaveChoiceCount(closer)>0) ClearSaveChoices(closer);
            Check("window:close-cancel","windows:close-cancel",closer.IsLoaded&&Documents(closer).Count==2,
                "Cancel on a two-tab window close keeps the window and both tabs",
                "loaded="+closer.IsLoaded+", documents="+Documents(closer).Count);
            SaveChoices(closer,2,MainWindow.SaveChoice.Discard);
            RunStep("windows:close-discard",delegate { closer.Close(); PumpMs(80); });
            if (SaveChoiceCount(closer)>0) ClearSaveChoices(closer);
            Check("window:close-discard","windows:close-discard",!closer.IsLoaded,"Discard on a two-tab window close closes it",
                "loaded="+closer.IsLoaded);
            closer=null;

            // Explorer / a second instance forwards a path to the running app. A file another window
            // already holds must come forward in that window with its tab selected; the receiving
            // window must not open a second copy of it (two windows editing one file).
            RunStep("windows:forward-existing",delegate {
                MainWindow receiving=NewWindow(900,700);
                MainWindow holder=NewWindow(900,700);
                SetActivationWindow(receiving);
                try {
                    Call(receiving,"SetText",new object[] {"Receiving window document\n"});
                    Call(receiving,"SetMode",new object[] {true});
                    SetField(receiving,"saved",Buffer(receiving));
                    Call(holder,"LoadFile",new object[] {fixtureA});
                    Call(holder,"CreateTab");
                    Call(holder,"SetText",new object[] {"Holder other tab\n"});
                    Call(holder,"SetMode",new object[] {true});
                    SetField(holder,"saved",Buffer(holder));
                    Pump();
                    SelectTab(holder,1); Pump();
                    int receivingTabs=Documents(receiving).Count;
                    /* A window activation raised by Show/Pump may have rewritten the static field
                       since the windows were created, so name the receiving window last. */
                    SetActivationWindow(receiving);
                    DispatchFiles(new string[] {fixtureA});
                    Pump();
                    Check("window:forward-existing","windows:forward-existing",
                        Documents(receiving).Count==receivingTabs&&Field<string>(receiving,"path").Length==0&&
                        Documents(holder).Count==2&&TabSelectedFor(holder,fixtureA),
                        "the window that already holds the file brings its tab forward and no copy opens elsewhere",
                        "receivingTabs="+Documents(receiving).Count+"/"+receivingTabs+", receivingPath=["+Field<string>(receiving,"path")+"]"+
                        ", holderTabs="+Documents(holder).Count+", holderActive=["+TabString(ActiveDocument(holder),"Path")+"]");
                } finally {
                    SetActivationWindow(null);
                    CloseQuiet(holder);
                    CloseQuiet(receiving);
                }
            });

            // File > Open, a recent entry and a drop land in LoadFile, which used to look only at
            // its own window. A file another window already holds must come forward there (the
            // same rule DispatchFiles follows); the caller must open no second copy.
            RunStep("windows:open-elsewhere",delegate {
                MainWindow receiver=NewWindow(900,700);
                MainWindow holder=NewWindow(900,700);
                try {
                    Call(receiver,"SetText",new object[] {"Receiver window document\n"});
                    Call(receiver,"SetMode",new object[] {true});
                    SetField(receiver,"saved",Buffer(receiver));
                    Call(holder,"LoadFile",new object[] {fixtureA});
                    Call(holder,"CreateTab");
                    Call(holder,"SetText",new object[] {"Holder other tab\n"});
                    Call(holder,"SetMode",new object[] {true});
                    SetField(holder,"saved",Buffer(holder));
                    Pump();
                    int receiverTabs=Documents(receiver).Count;
                    bool found=Object.ReferenceEquals(MainWindow.WindowWithFile(Application.Current,fixtureA),holder);
                    bool activated=receiver.ActivateFileInOtherWindow(fixtureA);
                    Pump();
                    Check("window:open-elsewhere","windows:open-elsewhere",
                        found&&activated&&Documents(receiver).Count==receiverTabs&&Field<string>(receiver,"path").Length==0&&
                        Documents(holder).Count==2&&TabSelectedFor(holder,fixtureA),
                        "the window that already holds the file is found and its tab comes forward; the caller opens no copy",
                        "found="+found+", activated="+activated+", receiverTabs="+Documents(receiver).Count+"/"+receiverTabs+
                        ", receiverPath=["+Field<string>(receiver,"path")+"], holderTabs="+Documents(holder).Count+
                        ", holderActive=["+TabString(ActiveDocument(holder),"Path")+"]");
                } finally {
                    CloseQuiet(holder);
                    CloseQuiet(receiver);
                }
            });

            // A window that is busy opening/saving refuses an image drop with the busy notice, not the
            // "images only in MD documents" answer the TXT path gives (a Markdown tab can be busy too).
            RunStep("windows:busy-drop",delegate {
                SelectTab(first,0); Pump();
                string notice=null;
                SetField(first,"documentOperationBusy",true);
                MainWindow.ImageDropNotice=delegate(string text) { notice=text; };
                try { Call(first,"InsertImageFilesAtCaret",new object[] {new string[] {"generated-image.png"}}); }
                finally { MainWindow.ImageDropNotice=null; SetField(first,"documentOperationBusy",false); Pump(); }
                Check("drop:busy","windows:busy-drop",notice!=null&&notice.IndexOf("작업이 끝난 뒤",StringComparison.Ordinal)>=0,
                    "a busy document answers an image drop with the busy notice",
                    "notice=["+notice+"]");
            });

            Check("window:new","windows:end",String.Equals(Buffer(first),"Window A document\n",StringComparison.Ordinal),
                "the other window is unaffected by the close","text=["+OneLine(Buffer(first))+"]");

            // Leaving edit mode with Discard must return the buffer to the saved text (the prompt says
            // 버리기를 누르면 마지막 저장 상태로 돌아갑니다). A mirror-only reset is a dirty-flag lie.
            MainWindow editor=NewWindow(900,700);
            tracked.Add(editor);
            try {
                Call(editor,"SetLiveMode",new object[] {true});
                Call(editor,"SetText",new object[] {"Edit exit baseline\n"});
                Call(editor,"SetMode",new object[] {true});
                SetField(editor,"saved",Buffer(editor));
                Pump();
                RunStep("windows:edit-exit-discard",delegate {
                    PlaceCaret(editor,Buffer(editor).Length);
                    Type(editor,"XYZ");
                    Pump();
                    Check("dirty:buffer","windows:edit-exit-discard",Prop<bool>(editor,"HasUnsavedChanges"),
                        "the typed text is dirty before the prompt","dirty="+Prop<bool>(editor,"HasUnsavedChanges"));
                    SaveChoices(editor,1,MainWindow.SaveChoice.Discard);
                    Press(ButtonField(editor,"editButton"));
                    Pump();
                    if (SaveChoiceCount(editor)>0) ClearSaveChoices(editor);
                    Check("tabs:edit-exit","windows:edit-exit-discard",!FieldBool(editor,"editing"),
                        "Discard leaves edit mode","editing="+FieldBool(editor,"editing"));
                    Check("dirty:buffer","windows:edit-exit-discard",String.Equals(Buffer(editor),"Edit exit baseline\n",StringComparison.Ordinal),
                        "Discard returns the buffer to the last saved text",Diff("Edit exit baseline\n",Buffer(editor)));
                    CheckMirror(editor,"windows:edit-exit-discard");
                });
            } finally {
                CloseQuiet(editor);
            }

            // Close answered with Save through SaveTargetPicker (the prompt's own seam, no dialog).
            MainWindow saver=NewWindow(900,700);
            tracked.Add(saver);
            Call(saver,"SetLiveMode",new object[] {true});
            Call(saver,"SetText",new object[] {"Save on close\n"});
            Call(saver,"SetMode",new object[] {true});
            SetField(saver,"saved",Buffer(saver));
            PlaceCaret(saver,Buffer(saver).Length);
            Type(saver," changed"); Pump();
            string savedText=Buffer(saver);
            string saveTarget=SaveTarget("window-close-save");
            RemoveFile(saveTarget);
            MainWindow.SaveTargetPicker=delegate(string name) { return saveTarget; };
            try {
                SaveChoices(saver,1,MainWindow.SaveChoice.Save);
                RunStep("windows:close-save",delegate { saver.Close(); PumpMs(120); });
                if (SaveChoiceCount(saver)>0) ClearSaveChoices(saver);
            } finally {
                MainWindow.SaveTargetPicker=null;
            }
            bool saveWritten=WaitForFile(saveTarget,800);
            Check("window:close-save","windows:close-save",!saver.IsLoaded&&saveWritten&&
                String.Equals(ReadUtf8(saveTarget),savedText,StringComparison.Ordinal),
                "Save on close writes the buffer and closes the window",
                "loaded="+saver.IsLoaded+", exists="+saveWritten+", file=["+(saveWritten?OneLine(ReadUtf8(saveTarget)):"-")+"]");
            if (saver.IsLoaded) CloseQuiet(saver);
        } catch (Exception ex) {
            Fail("window:scenario","windows","the window scenario completes",ExceptionText(ex));
        } finally {
            CloseQuiet(second);
            CloseQuiet(closer);
            CloseQuiet(first);
        }
    }

    // ---------------------------------------------------------------- report
    static string ComposeReport(Exception fatal) {
        var text=new StringBuilder();
        text.Append("# WindowAbuseProbe\n\n");
        text.Append("- timestamp: ").Append(DateTime.Now.ToString("o",CultureInfo.InvariantCulture)).Append('\n');
        text.Append("- seeds: ").Append(sequenceSeedFirst).Append("..").Append(sequenceSeedLast).Append(", ")
            .Append(sequenceSteps).Append(" steps per sequence, both views\n");
        text.Append("- modes: basic (HideMarkers true) and live (HideMarkers false)\n");
        text.Append("- fixtures: generated under ").Append(fixturesDir).Append("; no user file, setting, recovery draft or clipboard is read\n");
        text.Append("- windows: offscreen MainWindow instances; no SendInput; no window is raised on the user's desktop\n");
        text.Append("- dialogs: save prompts answered through the app's queuedSaveChoices seam; the app's own WPF dialogs\n")
            .Append("  (about, shortcut settings, link text, custom table/line spacing) are closed by a probe timer;\n")
            .Append("  native file/print/font/colour dialogs are not opened\n");
        text.Append("- runtime: ").Append(watch==null?0:watch.ElapsedMilliseconds).Append(" ms")
            .Append(budgetReached?" (budget reached; run incomplete)":"").Append('\n');
        if (fatal!=null) text.Append("\n## Fatal\n- ").Append(OneLine(fatal.ToString())).Append('\n');
        if (dispatcherNotes.Count>0) {
            text.Append("\n## Dispatcher exceptions\n");
            for (int i=0;i<dispatcherNotes.Count;i++) text.Append("- ").Append(dispatcherNotes[i]).Append('\n');
        }
        text.Append("\n## Operations\n");
        AppendCounts(text,opCounts);
        text.Append("\n## Invariant checks\n");
        AppendCheckCounts(text);
        text.Append("\n## Failures\n");
        if (failures.Count==0) {
            text.Append("- none\n");
        } else {
            var order=new List<string>();
            var groups=new Dictionary<string,List<FailureInfo>>();
            for (int i=0;i<failures.Count;i++) {
                FailureInfo failure=failures[i];
                string key=failure.Invariant+"\u0001"+failure.Expected+"\u0001"+failure.Actual;
                List<FailureInfo> list;
                if (!groups.TryGetValue(key,out list)) { list=new List<FailureInfo>(); groups[key]=list; order.Add(key); }
                list.Add(failure);
            }
            int number=0;
            for (int i=0;i<order.Count;i++) {
                List<FailureInfo> list=groups[order[i]];
                FailureInfo first=list[0];
                number++;
                text.Append("\n### ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(". ").Append(first.Invariant)
                    .Append(" — ").Append(first.Scenario).Append('/').Append(first.Step)
                    .Append(" (seed ").Append(first.Seed).Append(", ").Append(first.Mode).Append(")\n");
                text.Append("- occurrences: ").Append(list.Count).Append('\n');
                text.Append("- expected: ").Append(first.Expected).Append('\n');
                text.Append("- actual: ").Append(first.Actual).Append('\n');
                text.Append("- steps: ").Append(first.Steps).Append('\n');
                if (list.Count>1) {
                    var where=new StringBuilder();
                    for (int j=0;j<Math.Min(6,list.Count);j++) {
                        if (where.Length>0) where.Append(" | ");
                        where.Append(list[j].Scenario).Append('/').Append(list[j].Step);
                    }
                    text.Append("- also at: ").Append(where).Append('\n');
                }
            }
        }
        text.Append("\n## Skipped commands\n");
        if (skipped.Count==0) text.Append("- none\n");
        else for (int i=0;i<skipped.Count;i++) text.Append("- ").Append(skipped[i]).Append('\n');
        text.Append("\n## Notes\n");
        text.Append("- Ctrl chords cannot be synthesized headlessly: Keyboard.Modifiers reads the real key state, so each\n")
            .Append("  Ctrl shortcut was driven through the exact method its switch case calls; F1..F12 went through the\n")
            .Append("  real window PreviewKeyDown handler.\n");
        text.Append("- A view command must not change document text or add an undo entry; the probe types one character\n")
            .Append("  and checks that a single Undo still removes it after each theme/zoom/language/raw/find/wrap change.\n");
        text.Append("- Tab captions, menu headers and tooltips are sampled for Hangul in en/ja/zh-CN/zh-TW; language-menu\n")
            .Append("  entries themselves are excluded because they name each language in its own script.\n");
        text.Append("- Probe corrections made during development (no invariant was relaxed): window state is read from the\n")
            .Append("  fields the app declares (editing/dark/wordWrap/openInTabs/recoveredDocument), not properties with the\n")
            .Append("  same name; overflow parity only runs for commands the measured overflow actually holds (at 420 px the\n")
            .Append("  bold/heading/list/size/dark/edit commands can stay in the bar, and their toolbar path is still covered\n")
            .Append("  by the menu/shortcut parity tests); the new-document parity removes its extra tab through the app's own\n")
            .Append("  CloseTab before the next measurement.\n");
        for (int i=0;i<notes.Count;i++) text.Append("- ").Append(notes[i]).Append('\n');
        text.Append("\nExecuted checks: ").Append(checksRun).Append(", failures: ").Append(failures.Count)
            .Append(", steps: ").Append(stepsRun).Append('\n');
        text.Append("Result: ").Append(all&&!budgetReached&&fatal==null?"PASS":"FAIL").Append('\n');
        text.Append("Runtime: ").Append(watch==null?0:watch.ElapsedMilliseconds).Append(" ms\n");
        text.Append(reportBody.ToString());
        return text.ToString();
    }
    static void AppendCounts(StringBuilder text,Dictionary<string,int> map) {
        if (map.Count==0) { text.Append("- (none)\n"); return; }
        var keys=new List<string>(map.Keys);
        keys.Sort(StringComparer.Ordinal);
        for (int i=0;i<keys.Count;i++) text.Append("- ").Append(keys[i]).Append(": ").Append(map[keys[i]]).Append('\n');
    }
    static void AppendCheckCounts(StringBuilder text) {
        var keys=new List<string>(checkCounts.Keys);
        keys.Sort(StringComparer.Ordinal);
        if (keys.Count==0) { text.Append("- (none)\n"); return; }
        for (int i=0;i<keys.Count;i++) {
            string key=keys[i];
            int failed;
            if (!failCounts.TryGetValue(key,out failed)) failed=0;
            text.Append("- ").Append(key).Append(": ").Append(checkCounts[key]).Append(" checks, ").Append(failed).Append(" failed\n");
        }
    }

    // <helpers-end>
}
}
