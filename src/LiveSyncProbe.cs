using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;

namespace MDNote {
// Generated-data, offscreen regression for the per-window live switch (each window owns its
// surface; a new window starts from the last choice anywhere) and the reading-position restore
// (N23). Reflection keeps probe helpers out of MainWindow without exposing private editor state.
internal static class LiveSyncProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    internal static int VerifyLiveSync(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string passFile=Path.Combine(outDir,"livesync-probe-PASS.txt"),failFile=Path.Combine(outDir,"livesync-probe-FAIL.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        var report=new StringBuilder();bool all=true;
        Action<string,bool,string> check=delegate(string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n');};
        bool oldHide=ReadStatic<bool>("hideMarkers"),oldSelfTest=Program.IsSelfTest;
        MainWindow a=null,b=null,c=null,d=null,e=null,f=null,g=null,h=null,iTxt=null,j=null,k=null,tabs=null,tabsBasic=null,saveTxt=null,readLive=null,huge=null;Application application=null;ShutdownMode oldShutdown=ShutdownMode.OnExplicitShutdown;
        var detached=new System.Collections.Generic.List<MainWindow>();
        string fileA=Path.Combine(outDir,"livesync-generated-A.md"),fileB=Path.Combine(outDir,"livesync-generated-B.md"),fileC=Path.Combine(outDir,"livesync-generated-C.md"),fileD=Path.Combine(outDir,"livesync-generated-D.md"),fileE=Path.Combine(outDir,"livesync-generated-E.md");
        string fileLarge=Path.Combine(outDir,"livesync-large-310k.md"),fileMiddle=Path.Combine(outDir,"livesync-large-290k.md"),fileLargeTxt=Path.Combine(outDir,"livesync-large-400k.txt");
        string fileConvertTxt=Path.Combine(outDir,"livesync-convert-310k.txt"),fileSaveMd=Path.Combine(outDir,"livesync-saved-310k.md"),fileHuge=Path.Combine(outDir,"livesync-huge-3m.md");
        try{
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            application=Application.Current;oldShutdown=application.ShutdownMode;application.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            WriteStatic("hideMarkers",false);
            var generated=new StringBuilder("# Generated live sync document\n\n");
            for(int i=0;i<180;i++)generated.Append("Paragraph ").Append(i).Append(" with **bold** generated text for scrolling.\n\n");
            string document=generated.ToString();
            File.WriteAllText(fileA,document,new UTF8Encoding(false));File.WriteAllText(fileB,document,new UTF8Encoding(false));File.WriteAllText(fileC,document,new UTF8Encoding(false));
            a=OpenWindow(fileA);b=OpenWindow(fileB);Pump();
            check("one live engine",ShowsSource(a)&&ShowsSource(b)&&ReadProperty<bool>(a,"CodeMode")&&ReadProperty<bool>(b,"CodeMode"),"both windows use the source surface");
            check("live sessions attach",ReadProperty<bool>(a,"LivePreviewAttached")&&ReadProperty<bool>(b,"LivePreviewAttached"),"attached A="+ReadProperty<bool>(a,"LivePreviewAttached")+", B="+ReadProperty<bool>(b,"LivePreviewAttached"));
            check("live flags on",MenuChecked(a)==true&&MenuChecked(b)==true,"A="+MenuChecked(a)+", B="+MenuChecked(b));
            CheckWheel(a,true,check,"A live wheel");
            var source=ReadField<SourceEditor>(a,"source");

            // Basic/Live view modes change only the caret-line reveal; both keep the source surface.
            Invoke(a,"SetLiveMode",false);Pump();
            check("basic view keeps the source",ShowsSource(a)&&ReadStatic<bool>("hideMarkers")&&ReadProperty<bool>(a,"CodeMode"),
                "hideMarkers="+ReadStatic<bool>("hideMarkers")+", code="+ReadProperty<bool>(a,"CodeMode"));
            Invoke(a,"SetLiveMode",true);Pump();
            check("live view keeps the source",ShowsSource(a)&&!ReadStatic<bool>("hideMarkers")&&ReadProperty<bool>(a,"CodeMode"),
                "hideMarkers="+ReadStatic<bool>("hideMarkers")+", code="+ReadProperty<bool>(a,"CodeMode"));
            check("view modes preserve text",ReadProperty<string>(a,"text")==document&&ReadProperty<string>(b,"text")==document,"both generated buffers unchanged");

            // N9: the size box used to commit on every focus loss. In live mode that ran a size
            // command and source.Focus(), which closed the drop-down the moment it opened.
            var sizes=ReadField<ComboBox>(a,"sizes");
            source.Select(0,1);Pump();
            sizes.SelectedIndex=3;Pump();/* the list pick is the one intended command ("16 px") */
            string picked=sizes.Text;
            source.Select(source.Document.GetLineByNumber(3).Offset,3);Pump();
            string textBefore=source.Document.Text;
            Keyboard.Focus(sizes);Pump();
            sizes.IsDropDownOpen=true;Pump();
            bool dropDownOpened=sizes.IsDropDownOpen;
            source.CaretIndex=source.Document.GetLineByNumber(3).Offset+3;source.TextArea.Caret.BringCaretToView();a.UpdateLayout();Pump();
            check("size box keeps its drop-down",dropDownOpened&&sizes.IsDropDownOpen,"opened="+dropDownOpened+", after caret/layout="+sizes.IsDropDownOpen+", box focused="+sizes.IsKeyboardFocusWithin);
            sizes.IsDropDownOpen=false;Pump();
            // A focus loss with nothing typed must not commit; the synthetic event is the same
            // path the popup used to take (the old handler ran a size command and source.Focus()).
            var lost=new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice,Environment.TickCount,sizes,null);
            lost.RoutedEvent=Keyboard.LostKeyboardFocusEvent;
            sizes.RaiseEvent(lost);Pump();
            check("a size box focus loss without an edit leaves the text alone",source.Document.Text==textBefore&&sizes.Text==picked,"text unchanged="+(source.Document.Text==textBefore)+", box=\""+sizes.Text+"\", picked=\""+picked+"\"");

            // #11: a short document gets no 24px end margin (which used to add a scrollbar) and
            // must not be scrolled to the end.
            File.WriteAllText(fileD,"# Short\n\nOne line.\n",new UTF8Encoding(false));
            d=OpenWindow(fileD);Invoke(d,"SetLiveMode",true);Pump();
            var shortSource=ReadField<SourceEditor>(d,"source");
            d.UpdateLayout();Invoke(d,"ApplySourceInset",shortSource);d.UpdateLayout();Pump();Invoke(d,"ApplySourceInset",shortSource);
            var shortView=shortSource.TextArea.TextView;
            check("short live document has no end margin",Math.Abs(shortView.Margin.Bottom)<.1&&shortSource.VerticalOffset<=0,
                "bottom="+shortView.Margin.Bottom+", offset="+shortSource.VerticalOffset.ToString("0.0")+", extent="+shortSource.ExtentHeight.ToString("0.0")+", viewport="+shortSource.ViewportHeight.ToString("0.0"));

            // Finding 7: RestoreView's deferred callback belongs to the transition generation that
            // scheduled it. A newer transition started before it runs must keep its own mask; the
            // stale callback must not end it (the same guard as RestoreReadingAnchor).
            Pump();
            c=OpenWindow(fileC);Pump();
            Invoke(c,"SetMode",true);
            int scheduled=ReadField<int>(c,"viewTransitionVersion");
            Invoke(c,"BeginViewTransition");
            Pump();
            bool transitionOpen=ReadField<bool>(c,"viewTransition");
            int current=ReadField<int>(c,"viewTransitionVersion");
            check("stale RestoreView keeps the newer transition",transitionOpen&&current>scheduled,
                "open="+transitionOpen+", version="+current+" scheduled="+scheduled);
            Invoke(c,"EndViewTransition",false);Pump();

            // A generated 3,000,000-character Markdown body must open with no question window:
            // only the window itself is created, the live session attaches, and editing works.
            string hugeBody=LargeGeneratedBody(3000000);
            File.WriteAllText(fileHuge,hugeBody,new UTF8Encoding(false));
            int windowsBefore=0;foreach(Window candidate in Application.Current.Windows)if(candidate is MainWindow)windowsBefore++;
            huge=OpenWindow(fileHuge);Pump();
            int windowsAfter=0;foreach(Window candidate in Application.Current.Windows)if(candidate is MainWindow)windowsAfter++;
            check("3M open shows no question window",windowsAfter==windowsBefore+1,"main windows "+windowsBefore+" -> "+windowsAfter);
            check("3M open attaches the live session",ReadProperty<bool>(huge,"LivePreviewAttached")&&ReadProperty<bool>(huge,"CodeMode")&&ShowsSource(huge)&&ReadProperty<string>(huge,"text").Length>=3000000,
                "attached="+ReadProperty<bool>(huge,"LivePreviewAttached")+", code="+ReadProperty<bool>(huge,"CodeMode")+", length="+ReadProperty<string>(huge,"text").Length);
            var hugeSource=ReadField<SourceEditor>(huge,"source");
            int hugeLength=hugeSource.Document.TextLength;
            hugeSource.CaretIndex=hugeSource.Document.GetLineByNumber(2).EndOffset;
            hugeSource.TextArea.PerformTextInput("X");Pump();
            check("3M open is editable",hugeSource.Document.TextLength==hugeLength+1&&ReadProperty<bool>(huge,"HasUnsavedChanges"),
                "length "+hugeLength+" -> "+hugeSource.Document.TextLength+", dirty="+ReadProperty<bool>(huge,"HasUnsavedChanges"));

            // A large TXT converted to Markdown and saved as .md stays on the live surface and writes
            // the literal-markdown conversion; there is no size question left to ask.
            File.WriteAllText(fileConvertTxt,LargeGeneratedBody(310000),new UTF8Encoding(false));
            var previousContext=System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try{
                j=OpenWindow(fileConvertTxt);
                WaitFor((System.Threading.Tasks.Task)Invoke(j,"ConvertTextModeAsync",System.Threading.CancellationToken.None),60000);
                Invoke(j,"CancelFileOpen");
                string convertedText=ReadProperty<string>(j,"text");
                check("large txt conversion keeps the live surface",ReadProperty<bool>(j,"CodeMode")&&ShowsSource(j)&&!ReadProperty<bool>(j,"PlainText")&&convertedText.IndexOf("&#35; Generated large document",StringComparison.Ordinal)==0,
                    "code="+ReadProperty<bool>(j,"CodeMode")+", plain="+ReadProperty<bool>(j,"PlainText")+", literalPrefix="+(convertedText.Length>=2&&convertedText.StartsWith("&#35;",StringComparison.Ordinal)));
            }finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}

            saveTxt=OpenWindow(fileConvertTxt);
            if(File.Exists(fileSaveMd))File.Delete(fileSaveMd);
            string saveBuffer=ReadProperty<string>(saveTxt,"text");
            bool savedOk=false;
            var saveContext=System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try{
                var saveTask=(System.Threading.Tasks.Task<bool>)Invoke(saveTxt,"SaveToPathAsync",fileSaveMd,new UTF8Encoding(false),true);
                WaitFor(saveTask,60000);
                savedOk=saveTask.Result;
            }finally{System.Threading.SynchronizationContext.SetSynchronizationContext(saveContext);}
            string saveMdText=ReadProperty<string>(saveTxt,"text");
            string saveText=File.Exists(fileSaveMd)?File.ReadAllText(fileSaveMd):null;
            bool converted=saveText!=null&&saveText.IndexOf("&#35; Generated large document",StringComparison.Ordinal)==0;
            check("large txt save as md keeps the live surface",savedOk&&!ReadProperty<bool>(saveTxt,"PlainText")&&ReadProperty<bool>(saveTxt,"CodeMode")&&ShowsSource(saveTxt)&&saveMdText!=saveBuffer&&saveText==saveMdText&&converted,
                "saved="+savedOk+", plain="+ReadProperty<bool>(saveTxt,"PlainText")+", code="+ReadProperty<bool>(saveTxt,"CodeMode")+", bufferChanged="+(saveMdText!=saveBuffer)+", fileMatchesBuffer="+(saveText==saveMdText)+", converted="+converted);

            // Moving a tab into a window with "새 탭으로 보기" off still lands on the live surface.
            WriteStatic("hideMarkers",false);
            tabs=OpenWindow(fileA);
            Invoke(tabs,"CreateTab");Invoke(tabs,"LoadFileCore",fileD);Pump();
            var beforeMove=new System.Collections.Generic.List<Window>();foreach(Window candidate in Application.Current.Windows)beforeMove.Add(candidate);
            Invoke(tabs,"SetOpenInTabs",false);Pump();
            var moved=new System.Collections.Generic.List<MainWindow>();foreach(Window candidate in Application.Current.Windows){var window=candidate as MainWindow;if(window!=null&&!beforeMove.Contains(window)){moved.Add(window);detached.Add(window);}}
            bool movedLive=moved.Count==1;
            foreach(MainWindow window in moved)movedLive&=ReadProperty<bool>(window,"CodeMode")&&ShowsSource(window)&&ReadProperty<bool>(window,"LivePreviewAttached");
            check("moving a tab keeps the live surface",movedLive,"windows="+moved.Count);
        }catch(Exception ex){check("exception",false,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            foreach(var window in new[]{huge,readLive,iTxt,saveTxt,tabsBasic,tabs,k,j,h,g,f,e,d,c,b,a})if(window!=null){
                try{WriteField(window,"documentOperationBusy",false);WriteField(window,"saved",ReadProperty<string>(window,"text"));window.Close();}
                catch(Exception ex){check("close",false,ex.Message.Replace('\r',' ').Replace('\n',' '));}
            }
            foreach(var window in detached)if(window!=null){
                try{WriteField(window,"documentOperationBusy",false);WriteField(window,"saved",ReadProperty<string>(window,"text"));window.Close();}
                catch(Exception ex){check("close",false,ex.Message.Replace('\r',' ').Replace('\n',' '));}
            }
            WriteStatic("hideMarkers",oldHide);Program.IsSelfTest=oldSelfTest;
            if(application!=null)application.ShutdownMode=oldShutdown;
            foreach(string file in new[]{fileA,fileB,fileC,fileD,fileE,fileLarge,fileMiddle,fileLargeTxt,fileConvertTxt,fileSaveMd,fileHuge})try{if(File.Exists(file))File.Delete(file);}catch(IOException ex){check("cleanup",false,ex.Message.Replace('\r',' ').Replace('\n',' '));}
        }
        File.WriteAllText(all?passFile:failFile,report.ToString(),new UTF8Encoding(false));
        return all?0:1;
    }
    static MainWindow OpenWindow(string file){
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
        try{window.Show();window.Left=-20000;Invoke(window,"LoadFileCore",file);window.SetMode(true);return window;}
        catch{WriteField(window,"saved",ReadProperty<string>(window,"text"));window.Close();throw;}
    }
    // A window that keeps the loaded file in read mode: a large Markdown body then takes the live
    // read surface without asking.
    static MainWindow OpenReadWindow(string file){
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
        try{window.Show();window.Left=-20000;Invoke(window,"LoadFileCore",file);return window;}
        catch{WriteField(window,"saved",ReadProperty<string>(window,"text"));window.Close();throw;}
    }
    // A window that loads the file through the app's always-editing path (the Edit menu's
    // "항상 수정 기능 활성화"), which is the open that still asks the large-document question.
    static MainWindow OpenEditWindow(string file){
        bool previous=ReadStatic<bool>("alwaysEditing");
        MainWindow window=null;
        try{
            WriteStatic("alwaysEditing",true);
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;Invoke(window,"LoadFileCore",file);window.SetMode(true);return window;
        }
        catch{WriteField(window,"saved",ReadProperty<string>(window,"text"));window.Close();throw;}
        finally{WriteStatic("alwaysEditing",previous);}
    }
    // Generated Markdown of at least `length` characters for the large-document guard cases.
    static string LargeGeneratedBody(int length){
        var body=new StringBuilder("# Generated large document\n\n");int line=0;
        while(body.Length<length)body.Append("Line ").Append(line++).Append(" generated text for the large document guard.\n");
        return body.ToString();
    }
    // One paragraph that wraps over many visual rows, with following paragraphs so the caret can
    // sit high in the viewport; the caret is three quarters into the wrapped line.
    static string LongParagraph(out int caret){
        var words=new StringBuilder();
        for(int i=0;i<900;i++){if(i>0)words.Append(' ');words.Append("word").Append(i);}
        string paragraph=words.ToString();caret=paragraph.Length*3/4;
        var text=new StringBuilder(paragraph);
        for(int i=0;i<20;i++)text.Append("\n\n꼬리 문단 ").Append(i).Append(" generated text.");
        return text.ToString();
    }
    static bool ShowsSource(MainWindow window){return ReadField<SourceEditor>(window,"source").Visibility==Visibility.Visible;}
    // The 보기 > 라이브 모드 item holds the same state the removed per-window flag did.
    static bool? MenuChecked(MainWindow window){var menu=ReadField<MenuItem>(window,"liveModeMenu");return menu==null?null:(bool?)menu.IsChecked;}
    static void CheckWheel(MainWindow window,bool sourceShown,Action<string,bool,string> check,string name){
        var editor=ReadField<SourceEditor>(window,"source");
        editor.ScrollToVerticalOffset(0);Pump();
        double before=editor.VerticalOffset;
        // FastScroll is registered on the source surface, not on TextArea/TextView or
        // another child that cannot deliver a directly raised mouse event to it.
        var args=new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,-120) {RoutedEvent=Mouse.PreviewMouseWheelEvent};
        editor.RaiseEvent(args);Pump();
        double after=editor.VerticalOffset;
        check(name,args.Handled&&after>before+.1&&sourceShown,
            "offset="+before.ToString("0.0")+" -> "+after.ToString("0.0")+", handled="+args.Handled+", source shown="+sourceShown);
    }
    static void Pump(){LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
    // Queue-backed view changes outlive the menu handler. Wait for its finally block, not
    // ApplicationIdle/ContextIdle (full rich-document pagination can keep those waiting).
    static void WaitForDocumentOperation(MainWindow window,int milliseconds){
        Func<bool> complete=delegate{return !ReadField<bool>(window,"fileOpenRunning")&&!ReadField<bool>(window,"documentOperationBusy");};
        if(!complete()){
            var watch=System.Diagnostics.Stopwatch.StartNew();var frame=new DispatcherFrame();bool timedOut=false;
            var timer=new DispatcherTimer(DispatcherPriority.Send){Interval=TimeSpan.FromMilliseconds(10)};
            timer.Tick+=delegate{if(complete()||watch.ElapsedMilliseconds>=milliseconds){timedOut=!complete();timer.Stop();frame.Continue=false;}};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(timedOut)throw new TimeoutException("Queued view switch timed out after "+milliseconds+" ms");
        }
        var error=ReadField<Exception>(window,"LastFileOpenError");if(error!=null)throw new InvalidOperationException("Queued view switch failed",error);
    }
    // Pumps the dispatcher until an async probe step finishes (the same pattern as RC9Tests).
    static void WaitFor(System.Threading.Tasks.Task task,int milliseconds){
        var watch=System.Diagnostics.Stopwatch.StartNew();
        while(!task.IsCompleted&&watch.ElapsedMilliseconds<milliseconds){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}
        if(!task.IsCompleted)throw new TimeoutException("Probe task timed out after "+milliseconds+" ms");
        task.GetAwaiter().GetResult();
    }
    // A missing member is a probe fixture error, not a silent crash: name it in the FAIL line.
    static FieldInfo RequireField(string name){var field=typeof(MainWindow).GetField(name,Members);if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");return field;}
    static PropertyInfo RequireProperty(string name){var property=typeof(MainWindow).GetProperty(name,Members);if(property==null)throw new MissingMemberException("probe: MainWindow has no property '"+name+"'");return property;}
    static MethodInfo RequireMethod(string name){var method=typeof(MainWindow).GetMethod(name,Members);if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");return method;}
    static T ReadField<T>(MainWindow window,string name){return (T)RequireField(name).GetValue(window);}
    static void WriteField(MainWindow window,string name,object value){RequireField(name).SetValue(window,value);}
    static T ReadProperty<T>(MainWindow window,string name){return (T)RequireProperty(name).GetValue(window,null);}
    static object Invoke(MainWindow window,string name,params object[] args){return RequireMethod(name).Invoke(window,args);}
    static T ReadStatic<T>(string name){return (T)RequireField(name).GetValue(null);}
    static void WriteStatic(string name,object value){RequireField(name).SetValue(null,value);}
}
public partial class MainWindow {
    internal static int VerifyLiveSync(string outDir){return LiveSyncProbe.VerifyLiveSync(outDir);}
}
// b54: the live surface with hidden markers is now the only Markdown editing surface a normal
// run gets, but NoRevealProbe measures one MarkdownLivePreview session that the probe builds
// itself. This probe opens a real MainWindow on a generated document and runs the same rules
// through the window's own paths (the 원문 편집 escape hatch, the session's command hook, the
// table Enter rule, the table tool row's mouse schedule and the text getter a save reads). It
// sends no real keyboard or mouse input: keys and button events are raised on the TextArea and
// the window the same way TableCaretProbe raises them.
internal static class NoRevealWindowProbe {
    const BindingFlags WndMembers=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const string DocumentText=
        "# Generated heading\n"+
        "- generated item\n"+
        "plain **bold** and `code` text\n"+
        "\n"+
        "| alpha | beta |\n"+
        "| --- | --- |\n"+
        "| one | two |\n"+
        "\n"+
        "tail paragraph for the edit cycle\n";
    internal static int RunNoRevealWindowProbe(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string passFile=Path.Combine(outDir,"noreveal-window-PASS.txt"),failFile=Path.Combine(outDir,"noreveal-window-FAIL.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        var report=new StringBuilder();bool all=true;
        Action<string,bool,string> check=delegate(string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n');};
        bool oldHide=WndStatic<bool>("hideMarkers");
        MainWindow window=null;string file=Path.Combine(outDir,"noreveal-window-generated.md");
        try{
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            // LoadPreferences returns before it touches this on a self-test, so the probe sets the
            // static the normal run loads (markers hidden = the Basic view).
            WndStatic("hideMarkers",true);
            File.WriteAllText(file,DocumentText,new UTF8Encoding(false));
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;
            WndInvoke(window,"LoadFileCore",file);WndInvoke(window,"SetMode",true);WndPump();
            var source=WndField<SourceEditor>(window,"source");
            var preview=WndField<CheckBox>(window,"preview");
            string original=WndProperty<string>(window,"text");
            var session=WndField<MarkdownLivePreview.Session>(window,"livePreview");
            check("window opens live with hidden markers",
                WndProperty<bool>(window,"CodeMode")&&session!=null&&session.Options.HideMarkers&&!session.Options.ReadOnly&&original==DocumentText,
                "code="+WndProperty<bool>(window,"CodeMode")+", hideMarkers="+(session==null?"-":session.Options.HideMarkers.ToString())+", readOnly="+(session==null?"-":session.Options.ReadOnly.ToString())+", text matches="+(original==DocumentText));

            // (a) 원문 편집 is the escape hatch: on, the session is detached and the raw Markdown is
            // the whole surface; off, the hidden-marker view comes back with the same text.
            preview.IsChecked=true;WndPump();
            bool detached=WndField<MarkdownLivePreview.Session>(window,"livePreview")==null;
            check("원문 편집 shows the raw markdown",detached&&source.Text.IndexOf("**bold**",StringComparison.Ordinal)>=0&&WndProperty<string>(window,"text")==original,
                "session detached="+detached+", marker in source="+(source.Text.IndexOf("**bold**",StringComparison.Ordinal)>=0)+", text unchanged="+(WndProperty<string>(window,"text")==original));
            preview.IsChecked=false;WndPump();
            source=WndField<SourceEditor>(window,"source");session=WndField<MarkdownLivePreview.Session>(window,"livePreview");
            var boldLine=source.Document.GetLineByNumber(3);var boldInfo=session==null?null:session.Info(boldLine);
            int marker=source.Document.GetText(boldLine).IndexOf("**",StringComparison.Ordinal);
            bool hiddenPair=boldInfo!=null&&HiddenWindowRange(boldInfo,marker,2)&&!session.Raw(boldLine);
            check("원문 편집 off hides the markers again",session!=null&&session.Options.HideMarkers&&hiddenPair&&WndProperty<string>(window,"text")==original,
                "hideMarkers="+(session==null?"-":session.Options.HideMarkers.ToString())+", hidden pair="+hiddenPair+", text unchanged="+(WndProperty<string>(window,"text")==original));
            int seekTo=boldLine.Offset+marker+1;
            source.TextArea.Caret.Offset=seekTo;WndPump();
            int settled=source.TextArea.Caret.Offset;
            check("a direct caret seek cannot stop inside a hidden marker",settled!=seekTo&&(boldInfo==null||HiddenWindowMarkerAt(boldInfo,settled-boldLine.Offset)<0),
                "asked="+seekTo+", settled="+settled);

            // (e) a no-op edit cycle leaves the exact Markdown a save writes.
            int tailAt=source.Document.Text.IndexOf("tail paragraph",StringComparison.Ordinal)+6;
            source.Select(tailAt,0);source.TextArea.Caret.Offset=tailAt;WndPump();
            source.TextArea.PerformTextInput("Z");WndPump();
            EditingCommands.Backspace.Execute(null,source.TextArea);WndPump();
            WndInvoke(window,"FlushDocumentChanges");WndPump();
            string cycled=WndProperty<string>(window,"text");
            check("the text getter returns the exact original markdown",cycled==original&&!WndProperty<bool>(window,"HasUnsavedChanges"),
                "text matches="+(cycled==original)+", dirty="+WndProperty<bool>(window,"HasUnsavedChanges"));

            // (b) a hidden "**" pair is skipped by unselected character deletion.
            WndInvoke(window,"SetText","a **bold** b");WndPump();
            source=WndField<SourceEditor>(window,"source");
            source.Select(4,0);source.TextArea.Caret.Offset=4;WndPump();
            EditingCommands.Backspace.Execute(null,source.TextArea);WndPump();
            check("Backspace after an opening ** deletes visible space",source.Document.Text=="a**bold** b","text=["+source.Document.Text+"]");
            WndInvoke(window,"SetText","a **bold** b");WndPump();
            source=WndField<SourceEditor>(window,"source");
            source.Select(8,0);source.TextArea.Caret.Offset=8;WndPump();
            EditingCommands.Delete.Execute(null,source.TextArea);WndPump();
            check("Delete before a closing ** deletes visible space",source.Document.Text=="a **bold**b","text=["+source.Document.Text+"]");
            HiddenWindowBoundaryCases(window,check);
            HiddenLeadBackspaceCases(window,check);
            // With the markers shown they are real characters on the real key route too: the same
            // caret offset takes the character it sits after, never the line's marker.
            session=WndField<MarkdownLivePreview.Session>(window,"livePreview");
            var shownOptions=session==null?null:session.Options;
            if(shownOptions!=null){
                WndInvoke(window,"SetText","- **abc**");WndPump();
                shownOptions=WndField<MarkdownLivePreview.Session>(window,"livePreview").Options;
                shownOptions.HideMarkers=false;
                WndField<MarkdownLivePreview.Session>(window,"livePreview").Update(shownOptions);WndPump();
                source=WndField<SourceEditor>(window,"source");
                source.Select(4,0);source.CaretOffset=4;WndPump();
                PressWindowKey(source,Key.Back);
                string shownText=source.Document.Text;
                check("window backspace past a shown ** takes one character",shownText=="- *abc**",
                    "text=["+shownText+"]");
                shownOptions=WndField<MarkdownLivePreview.Session>(window,"livePreview").Options;
                shownOptions.HideMarkers=true;
                WndField<MarkdownLivePreview.Session>(window,"livePreview").Update(shownOptions);WndPump();
            }else check("window backspace past a shown ** takes one character",false,"no live session");

            // (b2) the whole inner text of a pair: deleting it takes the pair with it, and the
            // window's own one-step undo restores the exact Markdown the user typed.
            WndInvoke(window,"SetText","본문 **굵게** 글자");WndPump();
            source=WndField<SourceEditor>(window,"source");
            source.Select(5,2);source.TextArea.Caret.Offset=7;WndPump();
            EditingCommands.Backspace.Execute(null,source.TextArea);WndPump();
            string shell=source.Document.Text;
            WndInvoke(window,"Undo");WndPump();
            string shellUndone=source.Document.Text;
            check("whole-pair delete is one undo step in the window",shell=="본문  글자"&&shellUndone=="본문 **굵게** 글자",
                "after delete=["+shell+"], after undo=["+shellUndone+"]");

            // (c) Home/End never stop before or inside a hidden line prefix.
            WndInvoke(window,"SetText","# Heading line\n- item line\nplain tail\n");WndPump();
            source=WndField<SourceEditor>(window,"source");session=WndField<MarkdownLivePreview.Session>(window,"livePreview");
            for(int number=1;number<=2;number++){
                var line=source.Document.GetLineByNumber(number);var info=session.Info(line);
                source.Select(line.EndOffset,0);source.TextArea.Caret.Offset=line.EndOffset;WndPump();
                EditingCommands.MoveToLineStart.Execute(null,source.TextArea);WndPump();
                int home=source.TextArea.Caret.Offset;
                EditingCommands.MoveToLineEnd.Execute(null,source.TextArea);WndPump();
                int end=source.TextArea.Caret.Offset;
                check("Home/End never stop before the hidden prefix on line "+number,
                    info.LeadEnd>0&&home>=line.Offset+info.LeadEnd&&HiddenWindowMarkerAt(info,home-line.Offset)<0&&end>=line.Offset+info.LeadEnd&&HiddenWindowMarkerAt(info,end-line.Offset)<0,
                    "lead="+info.LeadEnd+", home="+(home-line.Offset)+", end="+(end-line.Offset)+", line=["+source.Document.GetText(line)+"]");
            }

            // (d) Enter inside a table cell writes one "<br>" and never a new source line.
            string fixture="before\n\n| a | b |\n| --- | --- |\n| c | d |\n\nafter\n";
            WndInvoke(window,"SetText",fixture);WndPump();
            source=WndField<SourceEditor>(window,"source");session=WndField<MarkdownLivePreview.Session>(window,"livePreview");
            var row=source.Document.GetLineByNumber(5);var rowInfo=session.Info(row);
            int cellStart=row.Offset+rowInfo.Cells[0].Start;
            source.Select(cellStart+1,0);WndPump();
            bool handled=PressWindowKey(source,Key.Enter);
            check("Enter in a table cell writes <br>",handled&&source.Document.Text==fixture.Replace("| c | d |","| c<br> | d |")&&source.Document.LineCount==8&&source.CaretIndex==cellStart+5,
                "handled="+handled+", text=["+FlatWindowText(source.Document.Text)+"], caret="+source.CaretIndex+"/"+(cellStart+5)+", lines="+source.Document.LineCount);

            TableGapTypingCases(window,check);

            // (f) Esc closes the find bar from the query field. The Korean IME spells its own keys
            // as Key.ImeProcessed with the real key in ImeProcessedKey, so a query typed in Korean
            // took the window's plain-Escape test off the path and the bar stayed open; both
            // spellings now close it, and the field route runs from the query box itself.
            WndInvoke(window,"ShowSearch");WndPump();
            var queryBox=WndField<TextBox>(window,"query");
            var bar=WndField<Border>(window,"searchHost");
            bool barOpen=queryBox!=null&&bar!=null&&bar.Visibility==Visibility.Visible;
            if(queryBox!=null){queryBox.Text="tail";WndPump();}
            bool imeMaps=MainWindow.SearchKey(Key.ImeProcessed,Key.Escape)==Key.Escape&&MainWindow.SearchKey(Key.Escape,Key.None)==Key.Escape;
            bool escHandled=false,barClosed=false;
            if(queryBox!=null){
                var esc=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                queryBox.RaiseEvent(esc);WndPump();
                escHandled=esc.Handled;barClosed=bar!=null&&bar.Visibility==Visibility.Collapsed;
            }
            check("Esc in the find box closes the bar",barOpen&&imeMaps&&escHandled&&barClosed,
                "open="+barOpen+", ime key mapping="+imeMaps+", handled="+escHandled+", closed="+barClosed);
            // The replace field stands on the same window route: the bar is open there and one Esc
            // from that field closes it too.
            WndInvoke(window,"ShowReplace");WndPump();
            var replaceBox=WndField<TextBox>(window,"replacement");
            bool replaceOpen=replaceBox!=null&&bar!=null&&bar.Visibility==Visibility.Visible;
            bool replaceHandled=false,replaceClosed=false;
            if(replaceBox!=null){
                var esc=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                replaceBox.RaiseEvent(esc);WndPump();
                replaceHandled=esc.Handled;replaceClosed=bar!=null&&bar.Visibility==Visibility.Collapsed;
            }
            check("Esc in the replace box closes the bar",replaceOpen&&replaceHandled&&replaceClosed,
                "open="+replaceOpen+", handled="+replaceHandled+", closed="+replaceClosed);

            TableToolbarMouseCases(window,check);
            WndInvoke(window,"SetLiveMode",true);WndPump();
        }catch(Exception ex){check("exception",false,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            if(window!=null){
                try{WndSet(window,"documentOperationBusy",false);WndSet(window,"saved",WndProperty<string>(window,"text"));WndSet(window,"recoveredDocument",false);window.Close();}
                catch(Exception ex){check("close",false,ex.Message.Replace('\r',' ').Replace('\n',' '));}
            }
            WndStatic("hideMarkers",oldHide);
            try{if(File.Exists(file))File.Delete(file);}catch(IOException ex){check("cleanup",false,ex.Message.Replace('\r',' ').Replace('\n',' '));}
        }
        File.WriteAllText(Path.Combine(outDir,"noreveal-window-probe.txt"),report.ToString(),new UTF8Encoding(false));
        if(all)File.WriteAllText(passFile,"PASS\n",new UTF8Encoding(false));else File.WriteAllText(failFile,report.ToString(),new UTF8Encoding(false));
        return all?0:1;
    }
    // fix9: the table tool row sits above the editor, so showing or hiding it moves the text
    // under the pointer. AvalonEdit hit-tests the pointer again when the button comes up
    // (SelectionMouseHandler.textArea_MouseLeftButtonUp), so a visibility change between press
    // and release put the caret on another line: a click on the paragraph under a table typed
    // into the table's last cell, and a click into a cell typed into the row above. A hidden
    // desktop cannot hold a real button, so the press is the remembered PreviewMouseDown with
    // the caret move AvalonEdit performs, and the release is the bubbling MouseUp.
    static void TableToolbarMouseCases(MainWindow window,Action<string,bool,string> check){
        string fixture="위 문단\n\n| 이름 | 값 | 메모 |\n|---|---|---|\n| 가 | 1 | 짧음 |\n| 나 | 2 |  |\n\n아래 문단\n\n그 다음 문단\n\n마지막 문단\n";
        WndInvoke(window,"SetText",fixture);WndPump();
        var source=WndField<SourceEditor>(window,"source");
        var host=WndField<Border>(window,"tableToolsHost");
        window.UpdateLayout();WndPump();
        var cellLine=source.Document.GetLineByNumber(5);var belowLine=source.Document.GetLineByNumber(8);
        int cellOffset=cellLine.Offset+source.Document.GetText(cellLine).IndexOf("가",StringComparison.Ordinal);
        int belowOffset=belowLine.Offset+source.Document.GetText(belowLine).IndexOf("단",StringComparison.Ordinal);
        // The user's starting state: the caret sits in a cell and the tool row is up.
        source.Select(cellOffset,0);WndPump();
        bool startsShown=host.Visibility==Visibility.Visible;
        // The screen point is what the pointer stays on: the editor moving under it is the bug.
        Point belowScreen=WndScreenAt(source,belowOffset);
        int pressHit=WndHitAtScreen(source,belowScreen);
        bool onParagraph=pressHit>=belowLine.Offset&&pressHit<=belowLine.EndOffset;
        check("probe point maps to the paragraph",onParagraph,"screen="+belowScreen+" hit="+pressHit+" line="+belowLine.Offset+".."+belowLine.EndOffset+", scroll="+source.TextArea.TextView.ScrollOffset);
        if(!onParagraph)return;
        double pressTop=WndSurfaceTop(source,window);
        var session=WndField<MarkdownLivePreview.Session>(window,"livePreview");
        WndPress(window,session);
        source.TextArea.Caret.Offset=pressHit;
        WndPump();
        bool heldShown=host.Visibility==Visibility.Visible;
        double heldTop=WndSurfaceTop(source,window);
        int releaseHit=WndHitAtScreen(source,belowScreen);
        // AvalonEdit's release hit-tests the same screen point again and takes that offset.
        if(releaseHit>=0)source.TextArea.Caret.Offset=releaseHit;
        WndRelease(window,session);
        bool collapsedAfter=host.Visibility==Visibility.Collapsed;
        source.TextArea.PerformTextInput("Z");WndPump();
        bool typedBelow=source.Document.GetText(source.Document.GetLineByNumber(8)).Contains("Z")&&
            !source.Document.GetText(source.Document.GetLineByNumber(5)).Contains("Z");
        check("click under a table types in that paragraph",
            startsShown&&onParagraph&&heldShown&&Math.Abs(heldTop-pressTop)<.5&&releaseHit==pressHit&&collapsedAfter&&typedBelow,
            "shown="+startsShown+", on paragraph="+onParagraph+", shown during press="+heldShown+
            ", editor top "+pressTop.ToString("0.0")+"->"+heldTop.ToString("0.0")+", hit "+pressHit+"/"+releaseHit+
            ", collapsed after release="+collapsedAfter+", typed below="+typedBelow+", line=["+FlatWindowText(source.Document.GetText(source.Document.GetLineByNumber(8)))+"]");

        // The other direction: from the paragraph under the table the tool row must not appear
        // while the button is down, and the click must stay in the cell it landed on.
        WndInvoke(window,"SetText",fixture);WndPump();
        source=WndField<SourceEditor>(window,"source");session=WndField<MarkdownLivePreview.Session>(window,"livePreview");
        window.UpdateLayout();WndPump();
        cellLine=source.Document.GetLineByNumber(5);belowLine=source.Document.GetLineByNumber(8);
        cellOffset=cellLine.Offset+source.Document.GetText(cellLine).IndexOf("가",StringComparison.Ordinal);
        belowOffset=belowLine.Offset+source.Document.GetText(belowLine).IndexOf("단",StringComparison.Ordinal);
        source.Select(belowOffset,0);WndPump();
        bool startsHidden=host.Visibility==Visibility.Collapsed;
        Point cellScreen=WndScreenAt(source,cellOffset);
        int cellHit=WndHitAtScreen(source,cellScreen);
        bool onCell=cellHit>=cellLine.Offset&&cellHit<=cellLine.EndOffset;
        if(!onCell){check("probe point maps to the cell",false,"screen="+cellScreen+" hit="+cellHit+" line="+cellLine.Offset+".."+cellLine.EndOffset);return;}
        double cellTop=WndSurfaceTop(source,window);
        WndPress(window,session);
        source.TextArea.Caret.Offset=cellHit;
        WndPump();
        bool hiddenDuringPress=host.Visibility==Visibility.Collapsed;
        double cellHeldTop=WndSurfaceTop(source,window);
        int cellReleaseHit=WndHitAtScreen(source,cellScreen);
        if(cellReleaseHit>=0)source.TextArea.Caret.Offset=cellReleaseHit;
        WndRelease(window,session);
        bool shownAfter=host.Visibility==Visibility.Visible;
        source.TextArea.PerformTextInput("Y");WndPump();
        bool typedInCell=source.Document.GetText(source.Document.GetLineByNumber(5)).Contains("Y")&&
            !source.Document.GetText(source.Document.GetLineByNumber(8)).Contains("Y");
        check("click into a table types in that cell",
            startsHidden&&onCell&&hiddenDuringPress&&Math.Abs(cellHeldTop-cellTop)<.5&&cellReleaseHit==cellHit&&shownAfter&&typedInCell,
            "hidden="+startsHidden+", on cell="+onCell+", hidden during press="+hiddenDuringPress+
            ", editor top "+cellTop.ToString("0.0")+"->"+cellHeldTop.ToString("0.0")+", hit "+cellHit+"/"+cellReleaseHit+
            ", shown after release="+shownAfter+", typed in cell="+typedInCell+", line=["+FlatWindowText(source.Document.GetText(source.Document.GetLineByNumber(5)))+"]");

        // A scrolled document: the row is a bar above the editor, so a visibility change must be
        // paid back with the scroll offset to keep the clicked line under the pointer.
        var longLines=new StringBuilder();
        for(int i=0;i<40;i++)longLines.Append("앞 줄 ").Append(i).Append("\n\n");
        longLines.Append("| a | b |\n| --- | --- |\n| c | d |\n\n");
        for(int i=0;i<40;i++)longLines.Append("뒤 줄 ").Append(i).Append("\n\n");
        WndInvoke(window,"SetText",longLines.ToString());WndPump();
        source=WndField<SourceEditor>(window,"source");window.UpdateLayout();WndPump();
        source.ScrollToLine(78);WndPump();
        var longRow=source.Document.GetLineByNumber(83);
        int longAnchor=source.Document.GetLineByNumber(88).Offset+2;
        source.Select(longRow.Offset+2,0);WndPump();
        window.UpdateLayout();WndPump();
        bool longShown=host.Visibility==Visibility.Visible;
        double anchorBefore=WndScreenAt(source,longAnchor).Y;
        source.Select(longAnchor,0);WndPump();
        double anchorAfter=WndScreenAt(source,longAnchor).Y;
        check("the tool row keeps scrolled text under the pointer",longShown&&host.Visibility==Visibility.Collapsed&&Math.Abs(anchorAfter-anchorBefore)<1.5,
            "shown before="+longShown+", collapsed="+(host.Visibility==Visibility.Collapsed)+", anchor "+anchorBefore.ToString("0.0")+"->"+anchorAfter.ToString("0.0")+", scroll="+source.VerticalOffset.ToString("0.0"));

        // Keyboard caret moves have no pointer under them: the row still changes at once.
        WndInvoke(window,"SetText",fixture);WndPump();
        source=WndField<SourceEditor>(window,"source");
        cellLine=source.Document.GetLineByNumber(5);belowLine=source.Document.GetLineByNumber(8);
        cellOffset=cellLine.Offset+source.Document.GetText(cellLine).IndexOf("가",StringComparison.Ordinal);
        belowOffset=belowLine.Offset+source.Document.GetText(belowLine).IndexOf("단",StringComparison.Ordinal);
        source.Select(cellOffset,0);WndPump();
        bool keyShown=host.Visibility==Visibility.Visible;
        source.Select(belowOffset,0);WndPump();
        bool keyHidden=host.Visibility==Visibility.Collapsed;
        check("keyboard caret keeps the immediate tool row",keyShown&&keyHidden,"in table="+keyShown+", below="+keyHidden);
    }
    // The screen point over a document offset, held fixed through a press the way a real pointer
    // is: the editor moving under it is what moves the release hit-test to another line.
    static Point WndScreenAt(SourceEditor source,int offset){
        var view=source.TextArea.TextView;
        view.EnsureVisualLines();
        var location=source.Document.GetLocation(offset);
        Point document=view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(location.Line,location.Column),ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle);
        return view.PointToScreen(new Point(document.X-view.ScrollOffset.X+2,document.Y-view.ScrollOffset.Y));
    }
    // GetOffsetFromMousePosition's own steps, from a screen point: back into the text view, plus
    // the scroll offset, resolved to a visual line and a document offset.
    static int WndHitAtScreen(SourceEditor source,Point screen){
        var view=source.TextArea.TextView;
        view.EnsureVisualLines();
        var position=view.GetPosition(view.PointFromScreen(screen)+view.ScrollOffset);
        return position.HasValue?source.Document.GetOffset(position.Value.Location):-1;
    }
    // The press and release halves of a click on the window: the window's own handlers see the
    // button state, and the live session freezes the reveal the way a real press does.
    static void WndPress(MainWindow window,MarkdownLivePreview.Session session){
        var source=WndField<SourceEditor>(window,"source");
        var down=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent};
        if(source!=null)source.TextArea.RaiseEvent(down);else window.RaiseEvent(down);
        WndCaptureMouse(window);
        if(session!=null)session.BeginPress();
        WndPump();
    }
    static void WndRelease(MainWindow window,MarkdownLivePreview.Session session){
        var source=WndField<SourceEditor>(window,"source");
        // The bubbling release, raised on the text area so it reaches the window the way a real
        // one does: after the editor's own release handler and its final hit-test.
        var up=new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.MouseUpEvent};
        if(source!=null)source.TextArea.RaiseEvent(up);else window.RaiseEvent(up);
        WndPump();
        if(session!=null)session.EndPress();WndPump();
    }
    // A real press leaves the editor holding the mouse. A hidden desktop refuses the capture, so
    // this only records what it could get; the window press flag carries the schedule.
    static void WndCaptureMouse(MainWindow window){
        var source=WndField<SourceEditor>(window,"source");
        if(source!=null)source.TextArea.CaptureMouse();
    }
    static double WndSurfaceTop(SourceEditor source,MainWindow window){
        source.UpdateLayout();
        return source.TranslatePoint(new Point(0,0),window).Y;
    }
    // Raise the real PreviewKeyDown -> KeyDown route, not just an editing command: the
    // window's structural Backspace handler runs first and must not consume a hidden pair.
    static void HiddenWindowBoundaryCases(MainWindow window,Action<string,bool,string> check){
        foreach(string marker in new[]{"**","~~","`","*"}){
            string text="본문 "+marker+"굵게"+marker+" 글자";
            for(int side=0;side<2;side++)for(int delta=0;delta<=marker.Length;delta++)foreach(bool back in new[]{true,false}){
                WndInvoke(window,"SetText",text);WndPump();
                var source=WndField<SourceEditor>(window,"source");
                int at=3+(side==0?0:marker.Length+2)+delta;
                source.Select(at,0);source.CaretOffset=at;WndPump();
                int settled=source.CaretOffset;
                PressWindowKey(source,back?Key.Back:Key.Delete);
                string actual=source.Document.Text;
                string expected=side==0 ? back ? "본문"+marker+"굵게"+marker+" 글자" : "본문 "+marker+"게"+marker+" 글자" :
                    back ? "본문 "+marker+"굵"+marker+" 글자" : "본문 "+marker+"굵게"+marker+"글자";
                WndInvoke(window,"Undo");WndPump();
                check("window hidden boundary "+marker+" "+(side==0?"open":"close")+"+"+delta+" "+(back?"Backspace":"Delete"),
                    actual==expected&&source.Document.Text==text,
                    "requested="+at+", settled="+settled+", text=["+actual+"], expected=["+expected+"], one undo="+(source.Document.Text==text));
            }
            foreach(bool back in new[]{true,false}){
                string single="x "+marker+"게"+marker+" y";
                WndInvoke(window,"SetText",single);WndPump();
                var source=WndField<SourceEditor>(window,"source");
                int at=back?3+marker.Length*2:2;
                source.Select(at,0);source.CaretOffset=at;WndPump();
                PressWindowKey(source,back?Key.Back:Key.Delete);
                string actual=source.Document.Text;
                WndInvoke(window,"Undo");WndPump();
                check("window last character beside "+marker+" "+(back?"Backspace":"Delete"),
                    actual=="x  y"&&source.Document.Text==single,"text=["+actual+"], one undo="+(source.Document.Text==single));
            }
            foreach(bool back in new[]{true,false}){
                string prefix="| head | h2 |\n| --- | --- |\n";
                string textInTable=prefix+"| "+marker+"게"+marker+" | z |\n";
                WndInvoke(window,"SetText",textInTable);WndPump();
                var source=WndField<SourceEditor>(window,"source");
                int at=prefix.Length+(back?3+marker.Length*2:2);
                source.Select(at,0);source.CaretOffset=at;WndPump();
                PressWindowKey(source,back?Key.Back:Key.Delete);
                string actual=source.Document.Text;
                WndInvoke(window,"Undo");WndPump();
                check("window table last character beside "+marker+" "+(back?"Backspace":"Delete"),
                    actual==prefix+"|  | z |\n"&&source.Document.Text==textInTable,
                    "text=["+FlatWindowText(actual)+"], one undo="+(source.Document.Text==textInTable));
            }
        }
        foreach(string newline in new[]{"\n","\r\n"})foreach(bool back in new[]{true,false}){
            string text=back?"앞"+newline+"**굵게**":"**굵게**"+newline+"뒤";
            string expected=back?"앞**굵게**":"**굵게**뒤";
            WndInvoke(window,"SetText",text);WndPump();
            var source=WndField<SourceEditor>(window,"source");int at=back?3+newline.Length:6;
            source.Select(at,0);source.CaretOffset=at;WndPump();
            PressWindowKey(source,back?Key.Back:Key.Delete);string actual=source.Document.Text;
            WndInvoke(window,"Undo");WndPump();
            check("window hidden pair joins a whole newline "+newline.Length+" "+(back?"Backspace":"Delete"),
                actual==expected&&source.Document.Text==text,
                "text=["+FlatWindowText(actual)+"], one undo="+(source.Document.Text==text));
        }
    }
    // b55: the caret at the text start of a line whose text opens with an inline pair is the plain
    // line's marker Backspace - the real key route (PreviewKeyDown structural handler first), at
    // both offsets that draw on that one spot: the structural prefix's end and the far side of the
    // opening marker. The text keeps its bold and the marker goes, in one undo step.
    static void HiddenLeadBackspaceCases(MainWindow window,Action<string,bool,string> check){
        string[] cases={
            "- **목록굵게** 항목","2","4","**목록굵게** 항목",
            "# **제목**","2","4","**제목**",
            "> **인용**","2","4","**인용**",
            "- [ ] **할 일**","6","8","**할 일**"
        };
        for(int i=0;i<cases.Length;i+=4){
            string text=cases[i],expected=cases[i+3];
            int leadEnd=Int32.Parse(cases[i+1]),afterMarker=Int32.Parse(cases[i+2]);
            foreach(int at in new[]{leadEnd,afterMarker}){
                WndInvoke(window,"SetText",text);WndPump();
                var source=WndField<SourceEditor>(window,"source");
                source.Select(at,0);source.CaretOffset=at;WndPump();
                int settled=source.CaretOffset;
                bool handled=PressWindowKey(source,Key.Back);
                string actual=source.Document.Text;
                WndInvoke(window,"Undo");WndPump();
                check("window backspace at the text start of "+text+" from "+at,
                    actual==expected&&source.Document.Text==text,
                    "settled="+settled+", handled="+handled+", text=["+FlatWindowText(actual)+"], expected=["+expected+"], one undo="+(source.Document.Text==text));
            }
        }
    }
    // fix13: the first text typed on the empty line directly under a table row (or directly above
    // a header row) writes the blank separator line in the same document update as the text, so
    // the table keeps its shape and one undo takes the newline and the text back together. The
    // window's own TextArea input path types, its own history undoes, and the IME commit runs the
    // editor's simulated composition the way ImeProbe does.
    static void TableGapTypingCases(MainWindow window,Action<string,bool,string> check){
        const string Below="before\n\n| h1 | h2 |\n| --- | --- |\n| c1 | c2 |\n\nafter\n";
        const string BelowTyped="before\n\n| h1 | h2 |\n| --- | --- |\n| c1 | c2 |\n\nk\nafter\n";
        const string Above="before\n\n\n| h1 | h2 |\n| --- | --- |\n| c1 | c2 |\ntail\n";
        const string AboveTyped="before\n\nk\n\n| h1 | h2 |\n| --- | --- |\n| c1 | c2 |\ntail\n";
        WndInvoke(window,"SetText",Below);WndPump();
        var source=WndField<SourceEditor>(window,"source");
        int blank=source.Document.GetLineByNumber(6).Offset;
        source.Select(blank,0);WndPump();
        source.TextArea.PerformTextInput("k");WndPump();
        check("typing under a table keeps one blank line",source.Document.Text==BelowTyped&&source.CaretIndex==blank+2,
            "text=["+FlatWindowText(source.Document.Text)+"] caret="+source.CaretIndex+"/"+(blank+2));
        WndInvoke(window,"Undo");WndPump();
        check("one undo takes back the newline and the typed text",source.Document.Text==Below,
            "text=["+FlatWindowText(source.Document.Text)+"]");
        WndInvoke(window,"SetText",Above);WndPump();
        source=WndField<SourceEditor>(window,"source");
        int aboveLine=source.Document.GetLineByNumber(3).Offset;
        source.Select(aboveLine,0);WndPump();
        source.TextArea.PerformTextInput("k");WndPump();
        check("typing above a table keeps one blank line",source.Document.Text==AboveTyped&&source.CaretIndex==aboveLine+1,
            "text=["+FlatWindowText(source.Document.Text)+"] caret="+source.CaretIndex+"/"+(aboveLine+1));
        WndInvoke(window,"Undo");WndPump();
        check("one undo above a table takes back the newline and the typed text",source.Document.Text==Above,
            "text=["+FlatWindowText(source.Document.Text)+"]");
        WndInvoke(window,"SetText",Below);WndPump();
        source=WndField<SourceEditor>(window,"source");
        blank=source.Document.GetLineByNumber(6).Offset;
        source.Select(blank,0);WndPump();
        source.SimulateImeCommit(1);source.TextArea.PerformTextInput("한");source.ResetCompositionSimulation();WndPump();
        check("an IME commit on that line keeps the blank line",source.Document.Text==BelowTyped.Replace("\nk\n","\n한\n")&&source.CaretIndex==blank+2&&!source.ImeBusy,
            "text=["+FlatWindowText(source.Document.Text)+"] caret="+source.CaretIndex+"/"+(blank+2)+" busy="+source.ImeBusy);
        // A paste on the same line is the same rule, in the paste's own one-step undo.
        WndInvoke(window,"SetText",Below);WndPump();
        source=WndField<SourceEditor>(window,"source");
        blank=source.Document.GetLineByNumber(6).Offset;
        source.Select(blank,0);WndPump();
        DataObject data=new DataObject();
        data.SetData(DataFormats.UnicodeText,"past");
        WndInvoke(window,"PasteData",data,true);WndPump();
        check("a paste under a table keeps one blank line",source.Document.Text==BelowTyped.Replace("\nk\n","\npast\n")&&source.CaretIndex==blank+5,
            "text=["+FlatWindowText(source.Document.Text)+"] caret="+source.CaretIndex+"/"+(blank+5));
        WndInvoke(window,"Undo");WndPump();
        check("one undo takes back the newline and the pasted text",source.Document.Text==Below,
            "text=["+FlatWindowText(source.Document.Text)+"]");
    }
    // A replacement range that covers [start,start+length): the "**" pair is hidden as one range.
    static bool HiddenWindowRange(MarkdownLivePreview.LineInfo info,int start,int length){
        foreach(MarkdownLivePreview.Replacement replacement in info.Replacements){
            if(replacement.Length<=0)continue;
            if(replacement.Start<=start&&replacement.Start+replacement.Length>=start+length)return true;
        }
        return false;
    }
    // Strictly inside a hidden marker, the same failure rule NoRevealProbe measures: the two
    // marker edges are legal caret stops, the characters between them are not.
    static int HiddenWindowMarkerAt(MarkdownLivePreview.LineInfo info,int relative){
        foreach(MarkdownLivePreview.Replacement replacement in info.Replacements){
            if(replacement.Length<=0)continue;
            if(relative>replacement.Start&&relative<replacement.Start+replacement.Length)return replacement.Start;
        }
        return -1;
    }
    // The same two-stage key raise TableCaretProbe uses: the structural handler sees PreviewKeyDown
    // first, then AvalonEdit's own KeyDown when that handler declined. No real input is sent.
    static bool PressWindowKey(SourceEditor source,Key key){
        var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(source),0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        source.TextArea.RaiseEvent(args);
        bool handled=args.Handled;WndPump();
        if(!handled){
            var down=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(source),0,key){RoutedEvent=Keyboard.KeyDownEvent};
            source.TextArea.RaiseEvent(down);WndPump();
        }
        return handled;
    }
    static string FlatWindowText(string text){return text==null?"null":text.Replace("\r","").Replace("\n","\\n");}
    static void WndPump(){LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
    static FieldInfo WndRequireField(string name){var field=typeof(MainWindow).GetField(name,WndMembers);if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");return field;}
    static PropertyInfo WndRequireProperty(string name){var property=typeof(MainWindow).GetProperty(name,WndMembers);if(property==null)throw new MissingMemberException("probe: MainWindow has no property '"+name+"'");return property;}
    static T WndField<T>(MainWindow window,string name){return (T)WndRequireField(name).GetValue(window);}
    static void WndSet(MainWindow window,string name,object value){WndRequireField(name).SetValue(window,value);}
    static T WndProperty<T>(MainWindow window,string name){return (T)WndRequireProperty(name).GetValue(window,null);}
    static object WndInvoke(MainWindow window,string name,params object[] args){var method=typeof(MainWindow).GetMethod(name,WndMembers);if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");return method.Invoke(window,args);}
    static T WndStatic<T>(string name){return (T)WndRequireField(name).GetValue(null);}
    static void WndStatic(string name,object value){WndRequireField(name).SetValue(null,value);}
}
}
