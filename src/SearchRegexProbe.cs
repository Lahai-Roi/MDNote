using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace MDNote {
// --search-regex-probe <folder>: the Find/Replace regular-expression option (".*" beside "Aa") driven through
// the window's own search bar and entry points (ShowSearch/ShowReplace/FindNext/ReplaceOne/ReplaceAll/Undo),
// on both surfaces (live: markers shown, source text searched; basic: markers hidden, visible text searched).
// Documents are generated here; no user file is read. Report: <folder>\search-regex-probe.txt, plus
// search-regex-probe-PASS.txt only when every check held (search-regex-probe-FAIL.txt otherwise).
static class SearchRegexProbe {
    static readonly BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly List<string> lines=new List<string>();
    static bool all;
    static string mode="";
    static int checks;
    static bool dispatcherFailed;
    static string dispatcherNote="";

    internal static int Run(string outDir){
        string dir;
        try{dir=Path.GetFullPath(outDir);}catch(Exception){dir=outDir;}
        Directory.CreateDirectory(dir);
        string passFile=Path.Combine(dir,"search-regex-probe-PASS.txt"),failFile=Path.Combine(dir,"search-regex-probe-FAIL.txt");
        TryDelete(passFile);TryDelete(failFile);
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        all=true;checks=0;lines.Clear();dispatcherFailed=false;
        bool oldSelf=Program.IsSelfTest,oldLive=ProbeDefaults.Live,oldHide=false;
        DispatcherUnhandledExceptionEventHandler handler=null;
        var timer=Stopwatch.StartNew();
        try{
            Program.IsSelfTest=true;
            oldHide=(bool)RequireField("hideMarkers").GetValue(null);
            ProbeDefaults.Live=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            handler=delegate(object sender,DispatcherUnhandledExceptionEventArgs args){dispatcherFailed=true;args.Handled=true;if(dispatcherNote.Length==0&&args.Exception!=null)dispatcherNote=args.Exception.GetType().Name+": "+args.Exception.Message;};
            Application.Current.DispatcherUnhandledException+=handler;Application.Current.Dispatcher.UnhandledException+=handler;
            RunMode(true);
            RunMode(false);
        }catch(Exception ex){all=false;lines.Add("FATAL "+ex.GetType().Name+": "+ex.Message.Replace("\r","").Replace("\n","\\n")+" @ "+Flat(ex.StackTrace));}
        finally{
            try{if(handler!=null&&Application.Current!=null){Application.Current.DispatcherUnhandledException-=handler;Application.Current.Dispatcher.UnhandledException-=handler;}}catch(Exception){}
            try{RequireField("hideMarkers").SetValue(null,oldHide);}catch(Exception){}
            ProbeDefaults.Live=oldLive;Program.IsSelfTest=oldSelf;timer.Stop();
        }
        if(dispatcherFailed){all=false;lines.Add("FAIL dispatcher-exception "+dispatcherNote);}
        var text=new StringBuilder();
        text.Append("search-regex-probe: ").Append(all?"PASS":"FAIL").Append(", checks ").Append(checks).Append(", ").Append(timer.ElapsedMilliseconds).Append(" ms\n");
        foreach(string line in lines)text.Append(line).Append('\n');
        try{
            File.WriteAllText(Path.Combine(dir,"search-regex-probe.txt"),text.ToString(),new UTF8Encoding(false));
            if(all)File.WriteAllText(passFile,"PASS\n",new UTF8Encoding(false));else File.WriteAllText(failFile,text.ToString(),new UTF8Encoding(false));
        }catch(IOException){return 1;}
        return all?0:1;
    }

    // ---------------------------------------------------------------- one surface
    static void RunMode(bool live){
        mode=live?"live":"basic";
        MainWindow window=null;
        try{
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Width=900,Height=700};
            window.Show();window.Left=-20000;window.Top=-20000;window.UpdateLayout();Pump();
            window.SetText("seed");window.SetMode(true);Pump();
            Invoke(window,"SetLiveMode",live);Pump();window.UpdateLayout();
            Invoke(window,"FlushDocumentChanges");
            Check("surface-"+mode,(bool)RequireField("hideMarkers").GetValue(null)==!live&&(bool)RequireField("editing").GetValue(window),"hideMarkers="+RequireField("hideMarkers").GetValue(null)+" editing="+RequireField("editing").GetValue(window));
            RunCases(window);
            Invoke(window,"CloseSearch");Pump();
        }catch(Exception ex){Fail("exception-"+mode,ex.GetType().Name+": "+ex.Message.Replace("\r","").Replace("\n","\\n")+" @ "+Flat(ex.StackTrace));}
        finally{CloseWindow(window);}
    }
    static void RunCases(MainWindow window){
        // 1 simple regex find; the count and the selection are the regex match.
        Open(window,"Alpha beta\nbeta Gamma\nalpha alpha\nLine four",false);
        Query(window,"b.ta",true,true);
        Check("simple-count",Count(window)==2,"count="+Count(window)+" text="+CountText(window));
        Find(window);
        Check("simple-find",Selected(window)=="beta"&&Source(window).SelectionStart==6,"selected=["+Selected(window)+"] at "+Source(window).SelectionStart+" "+CountText(window));
        Find(window);
        Check("simple-find-second",Selected(window)=="beta"&&Source(window).SelectionStart==11&&CountText(window)=="2 / 2","selected=["+Selected(window)+"] at "+Source(window).SelectionStart+" "+CountText(window));
        // 2 IgnoreCase follows the case option.
        Query(window,"alpha",true,false);
        Check("ignorecase-on",Count(window)==3,"case off: count="+Count(window));
        Query(window,"alpha",true,true);
        Check("ignorecase-off",Count(window)==2,"case on: count="+Count(window));
        // 3 ^ and $ are per line.
        Query(window,"^\\w+",true,true);
        int starts=Count(window);
        Query(window,"\\w+$",true,true);
        int ends=Count(window);
        Source(window).Select(0,0);
        Find(window);
        Check("line-anchors",starts==4&&ends==4&&Selected(window)=="beta","^\\w+ count="+starts+", \\w+$ count="+ends+", first \\w+$ selected=["+Selected(window)+"]");
        // 4 a match spans two lines.
        Query(window,"beta\\nbeta",true,true);
        Find(window);
        Check("multi-line-match",Count(window)==1&&Selected(window)=="beta\nbeta","count="+Count(window)+" selected=["+Selected(window).Replace("\n","\\n")+"]");
        // 5 zero-length: find next steps through the lines and wraps; \b never loops.
        Open(window,"a\nb\nc",false);
        Source(window).Select(0,0);
        Query(window,"^",true,true);
        Check("zero-length-count",Count(window)==3,"count="+Count(window)+" "+CountText(window));
        var carets=new List<int>();
        for(int i=0;i<4;i++){Find(window);carets.Add(Source(window).CaretOffset);}
        Check("zero-length-advances",carets[0]==2&&carets[1]==4&&carets[2]==0&&carets[3]==2,"carets="+String.Join(",",carets.ConvertAll<string>(delegate(int value){return value.ToString();}).ToArray()));
        Check("zero-length-no-selection",Source(window).SelectionLength==0,"selection length="+Source(window).SelectionLength);
        Open(window,"ab cd",false);
        Query(window,"\\b",true,true);
        int word=Count(window);
        for(int i=0;i<7;i++)Find(window);
        Check("word-boundary-find",word==4,"count="+word+" "+CountText(window));
        // 6 replace all with $1/$2 swap, one undo step.
        string swapDocument="hello world\nfoo bar";
        Open(window,swapDocument,true);
        Query(window,"(\\w+) (\\w+)",true,true);Replacement(window,"$2 $1");
        Invoke(window,"ReplaceAll");Pump();Invoke(window,"FlushDocumentChanges");
        Check("replace-swap",DocText(window)=="world hello\nbar foo","doc=["+DocText(window).Replace("\n","\\n")+"]");
        Invoke(window,"Undo");Pump();Invoke(window,"FlushDocumentChanges");
        Check("replace-all-one-undo",DocText(window)==swapDocument,"after one undo doc=["+DocText(window).Replace("\n","\\n")+"]");
        // 7 ${name}, $$, $0.
        Open(window,"ab-x cd-x",true);
        Query(window,"(?<w>\\w+)-x",true,true);Replacement(window,"${w}!");
        Invoke(window,"ReplaceAll");Pump();Invoke(window,"FlushDocumentChanges");
        Check("replace-named-group",DocText(window)=="ab! cd!","doc=["+DocText(window)+"]");
        Open(window,"a b a",true);
        Query(window,"a",true,true);Replacement(window,"$$");
        Invoke(window,"ReplaceAll");Pump();Invoke(window,"FlushDocumentChanges");
        Check("replace-dollar-literal",DocText(window)=="$ b $","doc=["+DocText(window)+"]");
        Open(window,"a b a",true);
        Query(window,"b",true,true);Replacement(window,"[$0]");
        Invoke(window,"ReplaceAll");Pump();Invoke(window,"FlushDocumentChanges");
        Check("replace-whole-match",DocText(window)=="a [b] a","doc=["+DocText(window)+"]");
        // 8 replace one reads the substitution against the match it replaces.
        Open(window,"hello world",true);
        Query(window,"(\\w+) (\\w+)",true,true);Replacement(window,"$2 $1");
        Invoke(window,"ReplaceOne");Pump();Invoke(window,"FlushDocumentChanges");
        Check("replace-one-swap",DocText(window)=="world hello","doc=["+DocText(window)+"]");
        // 9 a replacement that lands in a table cell escapes its pipe, as plain text does.
        string table="| name | value |\n| --- | --- |\n| ab | cd |\n";
        Open(window,table,true);
        Query(window,"(a)(b)",true,true);Replacement(window,"$1|$2");
        Invoke(window,"ReplaceAll");Pump();Invoke(window,"FlushDocumentChanges");
        string after=DocText(window);
        Check("replace-table-cell-escapes-pipe",after.IndexOf("a\\|b",StringComparison.Ordinal)>=0&&after.IndexOf("| a\\|b | cd |",StringComparison.Ordinal)>=0,"doc=["+after.Replace("\n","\\n")+"]");
        // 10 an invalid pattern: nothing found, nothing selected, nothing thrown, and a valid one clears it.
        Open(window,"Alpha beta\nbeta Gamma",true);
        Source(window).Select(3,0);
        Query(window,"(",true,true);
        string invalidFlow="";
        try{Find(window);Invoke(window,"ReplaceAll");Invoke(window,"ReplaceOne");}catch(Exception ex){invalidFlow=ex.GetType().Name+": "+ex.Message;}
        Check("invalid-no-exception",invalidFlow.Length==0,invalidFlow);
        Check("invalid-no-result",Count(window)==0&&CountText(window)=="0 / 0"&&Source(window).SelectionLength==0&&DocText(window)=="Alpha beta\nbeta Gamma","count text="+CountText(window)+" selection="+Source(window).SelectionLength+" doc=["+DocText(window).Replace("\n","\\n")+"]");
        object tip=QueryBox(window).ToolTip;
        Check("invalid-tooltip",tip!=null&&tip.ToString().Length>0,"tooltip="+(tip==null?"<none>":tip.ToString()));
        Query(window,"b.ta",true,true);
        Check("valid-clears-invalid",Count(window)==2&&QueryBox(window).ToolTip==null,"count="+Count(window)+" tooltip="+(QueryBox(window).ToolTip==null?"<none>":QueryBox(window).ToolTip.ToString()));
        // 11 a catastrophic pattern comes back inside the 2 s timeout and finds nothing.
        Open(window,"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!",false);
        var watch=Stopwatch.StartNew();
        Query(window,"(a+)+$",true,true);
        Find(window);
        watch.Stop();
        bool timedOut=(bool)RequireField("searchRegexTimedOut").GetValue(window);
        Check("catastrophic-returns",watch.ElapsedMilliseconds<9000&&Count(window)==0&&Source(window).SelectionLength==0&&timedOut,"elapsed="+watch.ElapsedMilliseconds+" ms, count="+Count(window)+", timedOut="+timedOut);
        // 12 the option off: "." is only a dot; on, it is any character.
        Open(window,"abc a.c",false);
        Query(window,"a.c",false,true);
        int plain=Count(window);
        Find(window);
        bool plainAt=Source(window).SelectionStart==4&&Selected(window)=="a.c";
        Query(window,"a.c",true,true);
        int regex=Count(window);
        Query(window,"a.c",false,true);
        Check("option-off-literal",plain==1&&plainAt&&regex==2&&Count(window)==1,"off="+plain+" at "+Source(window).SelectionStart+" on="+regex+" off again="+Count(window));
        // 13 a CRLF document: $ stops before the CR and . never takes it.
        Open(window,"ab\r\ncd\r\n",false);
        bool hasCr=DocText(window).IndexOf('\r')>=0;
        Query(window,"b$",true,true);
        int dollar=Count(window);
        Query(window,"a.*$",true,true);
        Find(window);
        Check("crlf-anchors",!hasCr||(dollar==1&&Selected(window)=="ab"),"hasCr="+hasCr+" b$ count="+dollar+" a.*$ selected=["+Selected(window).Replace("\r","\\r").Replace("\n","\\n")+"]");
        // 14 a document past 32K: found off the UI thread, navigation and Replace All still work, one undo.
        var big=new StringBuilder();
        for(int i=0;i<4000;i++)big.Append("line ").Append(i).Append(" foo\n");
        string bigText=big.ToString();
        Open(window,bigText,true);
        Query(window,"fo+$",true,true);
        bool settled=WaitForCount(window);
        Check("large-count",settled&&Count(window)==4000,"settled="+settled+" "+CountText(window));
        Find(window);settled=WaitForCount(window);
        Check("large-find",Selected(window)=="foo"&&Source(window).SelectionStart==bigText.IndexOf("foo",StringComparison.Ordinal),"selected=["+Selected(window)+"] at "+Source(window).SelectionStart+" "+CountText(window));
        Replacement(window,"bar-$0");
        Invoke(window,"ReplaceAll");Pump();Invoke(window,"FlushDocumentChanges");
        string replaced=DocText(window);
        Check("large-replace-all",replaced.StartsWith("line 0 bar-foo\nline 1 bar-foo\n",StringComparison.Ordinal)&&replaced.Length==bigText.Length+4000*4,"len="+replaced.Length+" expected="+(bigText.Length+4000*4)+" start=["+Short(replaced).Replace("\n","\\n")+"]");
        Invoke(window,"Undo");Pump();Invoke(window,"FlushDocumentChanges");
        Check("large-replace-all-one-undo",DocText(window)==bigText,"after one undo len="+DocText(window).Length+" expected="+bigText.Length);
        Query(window,"",false,true);
    }

    // ---------------------------------------------------------------- the window's own search bar
    static void Open(MainWindow window,string document,bool replace){
        try{Invoke(window,"CloseSearch");}catch(Exception){}
        Pump();
        CheckBox regexBox=RegexBox(window);if(regexBox.IsChecked==true)regexBox.IsChecked=false;
        window.SetText(document);window.SetMode(true);Pump();window.UpdateLayout();
        Invoke(window,"FlushDocumentChanges");Pump();
        Source(window).Select(0,0);
        Invoke(window,replace?"ShowReplace":"ShowSearch");Pump();
        QueryBox(window).Text="";Replacement(window,"");
    }
    static void Query(MainWindow window,string pattern,bool regex,bool caseOn){
        CheckBox caseBox=Field<CheckBox>(window,"matchCase"),regexBox=RegexBox(window);
        if(caseBox.IsChecked!=caseOn)caseBox.IsChecked=caseOn;
        if(regexBox.IsChecked!=regex)regexBox.IsChecked=regex;
        TextBox box=QueryBox(window);
        if(box.Text==pattern)box.Text="\u0001";
        box.Text=pattern;box.CaretIndex=box.Text.Length;Pump();
    }
    static void Replacement(MainWindow window,string value){TextBox box=Field<TextBox>(window,"replacement");box.Text=value;box.CaretIndex=box.Text.Length;}
    static void Find(MainWindow window){Invoke(window,"FindNext",false);Pump();}
    static CheckBox RegexBox(MainWindow window){return Field<CheckBox>(window,"matchRegex");}
    static TextBox QueryBox(MainWindow window){return Field<TextBox>(window,"query");}
    static string CountText(MainWindow window){return Field<TextBlock>(window,"matchCount").Text;}
    static int Count(MainWindow window){
        string text=CountText(window);int at=text.IndexOf(" / ",StringComparison.Ordinal);int value;
        return at>=0&&Int32.TryParse(text.Substring(at+3),out value)?value:-1;
    }
    // The large search answers from a background scan: the count reads "searching" until it lands.
    static bool WaitForCount(MainWindow window){
        var watch=Stopwatch.StartNew();
        while(watch.ElapsedMilliseconds<15000){
            Pump(100);
            if(Count(window)>=0)return true;
        }
        return false;
    }
    static string Selected(MainWindow window){return Source(window).SelectedText;}
    static string DocText(MainWindow window){SourceEditor source=Source(window);return source==null||source.Document==null?"":source.Document.Text;}
    static SourceEditor Source(MainWindow window){return Field<SourceEditor>(window,"source");}
    static string Short(string value){return value.Length<=60?value:value.Substring(0,60)+"...";}
    static string Flat(string value){if(value==null)return "";string text=value.Replace("\r","").Replace("\n"," < ");return text.Length<=500?text:text.Substring(0,500)+"...";}

    // ---------------------------------------------------------------- report and reflection
    static void Check(string name,bool ok,string detail){
        checks++;
        if(!ok)all=false;
        lines.Add((ok?"PASS ":"FAIL ")+mode+" "+name+(ok&&detail.Length>60?"":": "+detail.Replace("\r","\\r").Replace("\n","\\n")));
    }
    static void Fail(string name,string detail){checks++;all=false;lines.Add("FAIL "+name+": "+detail);}
    static void Pump(){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));}
    static void Pump(int milliseconds){
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.ApplicationIdle){Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=delegate{timer.Stop();frame.Continue=false;};
        timer.Start();Dispatcher.PushFrame(frame);Pump();
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{
            Invoke(window,"FlushDocumentChanges");
            RequireField("documentOperationBusy").SetValue(window,false);
            RequireField("saved").SetValue(window,typeof(MainWindow).GetProperty("text",Members).GetValue(window,null));
            RequireField("recoveredDocument").SetValue(window,false);
            window.Close();
        }catch(Exception){}
    }
    static FieldInfo RequireField(string name){
        FieldInfo field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");
        return field;
    }
    static T Field<T>(MainWindow window,string name){return (T)RequireField(name).GetValue(window);}
    static object Invoke(MainWindow window,string name,params object[] args){
        MethodInfo method=typeof(MainWindow).GetMethod(name,Members);
        if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");
        return method.Invoke(window,args);
    }
    static void TryDelete(string path){try{if(File.Exists(path))File.Delete(path);}catch(Exception){}}
}
}
