using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
namespace MDNote {
// --filechange-probe <outDir>: generated files only, in a temp folder under outDir. Drives the
// "another program changed / removed the open file" check (FileChangeWatch.cs) through the same
// seam the dialog uses (MainWindow.ExternalChangeDialog), so no real dialog opens.
static class FileChangeProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static readonly List<string> asked=new List<string>();
    static bool nextAnswer;
    static int tick;
    static int cases,passed,failed;
    static StringBuilder report;
    internal static int Run(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string passFile=Path.Combine(outDir,"filechange-probe-PASS.txt"),failFile=Path.Combine(outDir,"filechange-probe-FAIL.txt"),resultFile=Path.Combine(outDir,"filechange-probe.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        report=new StringBuilder();cases=0;passed=0;failed=0;traceFile=Path.Combine(outDir,"filechange-probe.trace.txt");if(File.Exists(traceFile))File.Delete(traceFile);
        string root=Path.Combine(outDir,"filechange-"+Guid.NewGuid().ToString("N"));
        try{
            Directory.CreateDirectory(root);
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            RunCase("a-reload",delegate{CaseReload(root);});
            RunCase("b-keep",delegate{CaseKeep(root);});
            RunCase("c-dirty-keep",delegate{CaseDirtyKeep(root);});
            RunCase("d-dirty-reload",delegate{CaseDirtyReload(root);});
            RunCase("e-removed",delegate{CaseRemoved(root);});
            RunCase("f-save-overwrite",delegate{CaseSaveOverwrite(root);});
            RunCase("g-own-save",delegate{CaseOwnSave(root);});
            RunCase("h-save-as",delegate{CaseSaveAs(root);});
            RunCase("i-untitled",delegate{CaseUntitled();});
            RunCase("j-touch-only",delegate{CaseTouchOnly(root);});
        }catch(Exception ex){Check("probe setup",false,ex.ToString());}
        finally{
            MainWindow.ExternalChangeDialog=null;
            try{if(Directory.Exists(root)){foreach(string file in Directory.GetFiles(root))File.Delete(file);Directory.Delete(root);}}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        report.Append("Cases: ").Append(cases).Append(" PASS: ").Append(passed).Append(" FAIL: ").Append(failed).Append('\n');
        File.WriteAllText(resultFile,report.ToString(),new UTF8Encoding(false));
        File.WriteAllText(failed==0&&cases>0?passFile:failFile,report.ToString(),new UTF8Encoding(false));
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
    // Written as it goes, so a run that stops partway still shows where.
    static string traceFile;
    static void Trace(string line){if(traceFile==null)return;try{File.AppendAllText(traceFile,line+"\n",new UTF8Encoding(false));}catch(IOException){}}
    // ---- helpers
    static void Pump(){LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
    static T Field<T>(MainWindow window,string name){
        FieldInfo field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");
        return (T)field.GetValue(window);
    }
    static object Call(MainWindow window,string name,params object[] args){
        MethodInfo method=typeof(MainWindow).GetMethod(name,Members);
        if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");
        return method.Invoke(window,args);
    }
    static SourceEditor Editor(MainWindow window){return Field<SourceEditor>(window,"source");}
    static string Text(MainWindow window){return Editor(window).Document.Text;}
    static bool Dirty(MainWindow window){
        Call(window,"FlushDocumentChanges");
        return (bool)typeof(MainWindow).GetProperty("HasUnsavedChanges",Members).GetValue(window,null);
    }
    static MainWindow NewWindow(){
        var window=new MainWindow {ShowInTaskbar=false,Width=900,Height=700};
        window.Show();window.Left=-20000;Pump();
        DispatcherTimer timer=Field<DispatcherTimer>(window,"recoveryTimer");if(timer!=null)timer.Stop();
        return window;
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{
            Call(window,"FlushDocumentChanges");
            typeof(MainWindow).GetField("text",Members).SetValue(window,Text(window));
            typeof(MainWindow).GetField("saved",Members).SetValue(window,Text(window));
            typeof(MainWindow).GetField("recoveredDocument",Members).SetValue(window,false);
            typeof(MainWindow).GetField("sourceSnapshotDirty",Members).SetValue(window,false);
            var tabs=(System.Collections.IList)typeof(MainWindow).GetField("documents",Members).GetValue(window);
            foreach(object tab in tabs){tab.GetType().GetField("Saved",Members).SetValue(tab,tab.GetType().GetField("Text",Members).GetValue(tab));tab.GetType().GetField("Recovered",Members).SetValue(tab,false);}
            window.Close();
        }catch(Exception){}
    }
    static MainWindow OpenFile(string file){
        MainWindow window=NewWindow();
        Call(window,"LoadFileCore",file);Call(window,"CaptureTab");Pump();
        /* The test run starts files in the read view; an edit needs the edit view (the reload keeps whichever view the tab was in). */
        window.SetMode(true);Pump();
        return window;
    }
    // Another program writes: new text, and a last-write time that is surely different from every earlier one.
    static void ExternalWrite(string file,string text){
        File.WriteAllText(file,text,new UTF8Encoding(false));
        tick++;File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddSeconds(3*tick));
    }
    static string FileText(string file){return File.ReadAllText(file,new UTF8Encoding(false));}
    static void TypeAtEnd(MainWindow window,string text){
        SourceEditor editor=Editor(window);editor.Select(editor.Document.TextLength,0);editor.TextArea.PerformTextInput(text);Pump();
    }
    static string Ask(Func<bool> action,bool answer,out bool result){
        asked.Clear();nextAnswer=answer;
        MainWindow.ExternalChangeDialog=delegate(string kind,string full){asked.Add(kind);return nextAnswer;};
        try{result=action();}finally{MainWindow.ExternalChangeDialog=null;}
        string joined=String.Join(",",asked.ToArray());
        Pump();
        return joined;
    }
    // One RunExternalCheck with the dialog answered; returns the kinds that were asked.
    static string RunCheck(MainWindow window,bool answer){
        bool ignored;
        return Ask(delegate{window.RunExternalCheck();return true;},answer,out ignored);
    }
    static int CaretLine(MainWindow window){SourceEditor editor=Editor(window);return editor.Document.GetLineByOffset(Math.Min(editor.CaretIndex,editor.Document.TextLength)).LineNumber;}
    const string Original="line1\nline2\nline3\nline4\n";
    const string Changed="alpha\nbeta\ngamma\ndelta\nepsilon\n";
    // ---- cases
    // (a) external write, not dirty, answer "다시 불러오기": new text, not dirty, caret line kept.
    static void CaseReload(string root){
        string file=Path.Combine(root,"a.md");File.WriteAllText(file,Original,new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            SourceEditor editor=Editor(window);int offset=editor.Document.GetLineByNumber(3).Offset+2;editor.Select(offset,0);Pump();
            ExternalWrite(file,Changed);
            string kinds=RunCheck(window,true);
            Check("a asks once (changed)",kinds=="changed",kinds);
            Check("a text is the new file",Text(window)==Changed,"text="+Text(window).Replace("\n","\\n"));
            Check("a not dirty after reload",!Dirty(window),"dirty="+Dirty(window));
            Check("a caret line kept",CaretLine(window)==3,"line="+CaretLine(window));
            Check("a second check is silent",RunCheck(window,true)=="","second check asked nothing");
        }finally{CloseWindow(window);}
    }
    // (b) external write, answer "그대로 두기": not asked again on the next check.
    static void CaseKeep(string root){
        string file=Path.Combine(root,"b.md");File.WriteAllText(file,Original,new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            ExternalWrite(file,Changed);
            string kinds=RunCheck(window,false);
            Check("b asks once (changed)",kinds=="changed",kinds);
            Check("b text unchanged",Text(window)==Original,"text="+Text(window).Replace("\n","\\n"));
            string again=RunCheck(window,false);
            Check("b not asked again",again=="","second check asked: ["+again+"]");
            Check("b still clean",!Dirty(window),"dirty="+Dirty(window));
        }finally{CloseWindow(window);}
    }
    // (c) dirty + external write + keep: text unchanged and still dirty.
    static void CaseDirtyKeep(string root){
        string file=Path.Combine(root,"c.md");File.WriteAllText(file,Original,new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            TypeAtEnd(window,"MINE");string mine=Text(window);
            Check("c edit made the document dirty",Dirty(window)&&mine!=Original,"dirty="+Dirty(window));
            ExternalWrite(file,Changed);
            string kinds=RunCheck(window,false);
            Check("c asks the dirty question",kinds=="changed-dirty",kinds);
            Check("c text unchanged",Text(window)==mine,"text="+Text(window).Replace("\n","\\n"));
            Check("c still dirty",Dirty(window),"dirty="+Dirty(window));
            Check("c not asked again",RunCheck(window,false)=="","second check");
        }finally{CloseWindow(window);}
    }
    // (d) dirty + reload: new text, not dirty.
    static void CaseDirtyReload(string root){
        string file=Path.Combine(root,"d.md");File.WriteAllText(file,Original,new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            TypeAtEnd(window,"MINE");
            ExternalWrite(file,Changed);
            string kinds=RunCheck(window,true);
            Check("d asks the dirty question",kinds=="changed-dirty",kinds);
            Check("d text is the new file",Text(window)==Changed,"text="+Text(window).Replace("\n","\\n"));
            Check("d not dirty after reload",!Dirty(window),"dirty="+Dirty(window));
        }finally{CloseWindow(window);}
    }
    // (e) removal: one notice, the document becomes dirty, a second check is silent; the file coming
    // back is a change; saving after the removal recreates the file without a conflict.
    static void CaseRemoved(string root){
        string file=Path.Combine(root,"e.md");File.WriteAllText(file,Original,new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            Check("e clean before removal",!Dirty(window),"dirty="+Dirty(window));
            File.Delete(file);
            string kinds=RunCheck(window,true);
            Check("e one removal notice",kinds=="removed",kinds);
            Check("e dirty after removal",Dirty(window),"dirty="+Dirty(window));
            Check("e text kept",Text(window)==Original,"text="+Text(window).Replace("\n","\\n"));
            Check("e second check silent",RunCheck(window,true)=="","second check");
            ExternalWrite(file,Changed);
            kinds=RunCheck(window,false);
            Check("e file back is a change (dirty question)",kinds=="changed-dirty",kinds);
            Check("e still dirty and text kept",Dirty(window)&&Text(window)==Original,"dirty="+Dirty(window));
            // removed again, then save: the file is recreated, no conflict, no further notice
            File.Delete(file);
            kinds=RunCheck(window,true);
            Check("e removal notice again",kinds=="removed",kinds);
            bool wrote=(bool)Call(window,"SaveToPath",file,Field<Encoding>(window,"documentEncoding"),false);
            Check("e save recreates the file",wrote&&File.Exists(file)&&FileText(file)==Original,"wrote="+wrote+" exists="+File.Exists(file));
            Check("e clean after the save",!Dirty(window),"dirty="+Dirty(window));
            Check("e no question after own save",RunCheck(window,true)=="","after save");
        }finally{CloseWindow(window);}
    }
    // (f) save after a write the window never saw: overwrite question; cancel keeps the disk file,
    // overwrite lets the save write. (The window's SaveAsync calls ConfirmOverwriteExternalChange just
    // before the write; the probe calls it at the same point, then the real SaveToPath.)
    static void CaseSaveOverwrite(string root){
        string file=Path.Combine(root,"f.md");File.WriteAllText(file,Original,new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            TypeAtEnd(window,"MINE");string mine=Text(window);
            ExternalWrite(file,Changed);
            bool proceed;string kinds=Ask(delegate{return window.ConfirmOverwriteExternalChange(file);},false,out proceed);
            Check("f cancel: asked overwrite",kinds=="overwrite",kinds);
            Check("f cancel: save must not go on",!proceed,"proceed="+proceed);
            Check("f cancel: disk file kept",FileText(file)==Changed,"disk="+FileText(file).Replace("\n","\\n"));
            Check("f cancel: text kept and dirty",Text(window)==mine&&Dirty(window),"dirty="+Dirty(window));
            // VM check 2026-11-05: the activation right after that 취소 must not ask about the same change again.
            kinds=RunCheck(window,false);
            Check("f cancel: no second question on activation",kinds=="","asked="+kinds);
            kinds=Ask(delegate{return window.ConfirmOverwriteExternalChange(file);},true,out proceed);
            Check("f overwrite: asked again, may go on",kinds=="overwrite"&&proceed,kinds+" proceed="+proceed);
            bool wrote=(bool)Call(window,"SaveToPath",file,Field<Encoding>(window,"documentEncoding"),false);
            Check("f overwrite: the save writes the window's text",wrote&&FileText(file)==mine,"disk="+FileText(file).Replace("\n","\\n"));
            Check("f overwrite: clean afterwards",!Dirty(window),"dirty="+Dirty(window));
            kinds=Ask(delegate{return window.ConfirmOverwriteExternalChange(file);},false,out proceed);
            Check("f no question after the save",kinds==""&&proceed,"asked="+kinds);
        }finally{CloseWindow(window);}
    }
    // (g) MDNote's own saves never trigger a question afterwards, neither on activation nor at the next save.
    static void CaseOwnSave(string root){
        string file=Path.Combine(root,"g.md");File.WriteAllText(file,Original,new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            TypeAtEnd(window,"ONE");
            bool wrote=(bool)Call(window,"SaveToPath",file,Field<Encoding>(window,"documentEncoding"),false);
            Check("g own save wrote",wrote&&FileText(file)==Text(window),"wrote="+wrote);
            Check("g no question after own save",RunCheck(window,true)=="","check after save");
            TypeAtEnd(window,"TWO");
            bool proceed;string kinds=Ask(delegate{return window.ConfirmOverwriteExternalChange(file);},false,out proceed);
            Check("g next save point asks nothing",kinds==""&&proceed,"asked="+kinds);
            wrote=(bool)Call(window,"SaveToPath",file,Field<Encoding>(window,"documentEncoding"),false);
            Check("g second own save wrote",wrote&&FileText(file)==Text(window),"wrote="+wrote);
            Check("g no question after second save",RunCheck(window,true)=="","check after second save");
            // a save with the same bytes only touches the file time: still silent
            wrote=(bool)Call(window,"SaveToPath",file,Field<Encoding>(window,"documentEncoding"),false);
            Check("g identical save is silent",wrote&&RunCheck(window,true)=="","identical save");
        }finally{CloseWindow(window);}
    }
    // (h) Save As to another path never asks; the stamp follows the new path.
    static void CaseSaveAs(string root){
        string file=Path.Combine(root,"h.md"),other=Path.Combine(root,"h-other.md"),target=Path.Combine(root,"h-new.md");
        File.WriteAllText(file,Original,new UTF8Encoding(false));File.WriteAllText(other,"someone else's file\n",new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            TypeAtEnd(window,"ABC");
            ExternalWrite(other,"someone else's file, changed\n");
            bool proceed;string kinds=Ask(delegate{return window.ConfirmOverwriteExternalChange(other);},false,out proceed);
            Check("h Save As to another existing path asks nothing",kinds==""&&proceed,"asked="+kinds);
            bool wrote=(bool)Call(window,"SaveToPath",target,Field<Encoding>(window,"documentEncoding"),false);
            Check("h Save As wrote the new path",wrote&&FileText(target)==Text(window),"wrote="+wrote);
            Check("h no question after Save As",RunCheck(window,true)=="","check after Save As");
            ExternalWrite(file,"the old file changed\n");
            Check("h the old path is no longer watched",RunCheck(window,true)=="","old path changed");
            ExternalWrite(target,Changed);
            string changed=RunCheck(window,false);
            Check("h the new path is watched",changed=="changed"||changed=="changed-dirty",changed);
        }finally{CloseWindow(window);}
    }
    // (i) an untitled document has no file to check.
    static void CaseUntitled(){
        MainWindow window=NewWindow();
        try{
            TypeAtEnd(window,"draft text");
            Check("i untitled: no check",RunCheck(window,true)=="","asked nothing");
            Check("i untitled: text kept",Text(window)=="draft text","text="+Text(window));
        }finally{CloseWindow(window);}
    }
    // (j) a touched file whose bytes are still the loaded ones is not a change.
    static void CaseTouchOnly(string root){
        string file=Path.Combine(root,"j.md");File.WriteAllText(file,Original,new UTF8Encoding(false));
        MainWindow window=OpenFile(file);
        try{
            tick++;File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddSeconds(3*tick));
            Check("j touch only is silent",RunCheck(window,true)=="","touched");
            ExternalWrite(file,Changed);
            string kinds=RunCheck(window,true);
            Check("j a later real change still asks",kinds=="changed",kinds);
        }finally{CloseWindow(window);}
    }
}
}
