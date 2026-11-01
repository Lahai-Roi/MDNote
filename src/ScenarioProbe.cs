// Everyday-scenario probe: emoji/surrogates, external paste formats, external file changes, read-only files, crash recovery, unicode paths, line endings, single-instance activation, unsaved-close prompts (offscreen).
// Every fixture is generated inside the folder the entry point is given; no user file, setting or
// recovery draft is read or written. Windows are offscreen; no SendInput is used.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// --scenario-probe <dir>
internal static class ScenarioProbe {
    internal static int Run(string folder){return MainWindow.RunScenarioProbe(folder);}
}

// The suite lives inside MainWindow so it can drive the window's own private entry points
// (Save, AskSave, ConfirmTabs, RestoreRecovery, the document tabs) the way the UI does, without
// reflection or a second copy of any command.
public partial class MainWindow {
    internal static int RunScenarioProbe(string folder){return ScenarioSuite.Run(folder);}

    static class ScenarioSuite {
        static StringBuilder report;
        static bool all;
        static string outDir;
        static string currentScenario;
        static long scenarioDeadline;
        static bool finished;
        static int dispatcherErrors;
        static string lastDispatcherError=null;
        static readonly HashSet<string> dispatcherSeen=new HashSet<string>();
        static DispatcherUnhandledExceptionEventHandler dispatcherHandler;
        static readonly object reportGate=new object();
        // Longest total run; the hidden-desktop runner kills the process at 15 minutes, so stop
        // earlier with a report that names the scenario that hung.
        const int WatchdogMilliseconds=9*60*1000;

        internal static int Run(string folder){
            outDir=Path.GetFullPath(folder);
            Directory.CreateDirectory(outDir);
            string reportPath=Path.Combine(outDir,"scenario-report.txt");
            string passPath=Path.Combine(outDir,"scenario-PASS.txt");
            string failPath=Path.Combine(outDir,"scenario-FAIL.txt");
            RemoveFile(passPath);RemoveFile(failPath);
            report=new StringBuilder();
            all=true;finished=false;
            report.Append("# ScenarioProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o",CultureInfo.InvariantCulture)).Append('\n');
            Append("- windows: offscreen MainWindow (IsSelfTest), live engine, generated fixtures only\n");
            Append("- files open in 읽기 mode (alwaysEditing defaults false); every editing scenario presses 수정 시작 through SetMode(true), the same toggle Ctrl+E runs\n");
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            // The real app runs under Application.Run, so every await resumes on the dispatcher.
            // This probe never runs the message loop; without the same context Recovery.RestoreRecovery
            // would resume on the thread pool and touch WPF objects from the wrong thread.
            SynchronizationContext previousContext=SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            // A dispatcher callback that throws would otherwise end the process (0xc000041d) and
            // take the whole report with it. Record it against the running scenario and let the
            // scenario's own checks decide; the product defect stays visible as a FAIL line.
            Dispatcher currentDispatcher=Dispatcher.CurrentDispatcher;
            dispatcherHandler=delegate(object sender,DispatcherUnhandledExceptionEventArgs args){
                string text=args.Exception==null?"(no exception object)":args.Exception.ToString();
                args.Handled=true;
                // WPF surfaces one exception through the dispatcher and the application event,
                // and repeats it when the same close path runs twice; count each shape once.
                string key=ExceptionKey(text);
                if(!dispatcherSeen.Add(key))return;
                dispatcherErrors++;
                lastDispatcherError=text;
                all=false;
                Append("FAIL dispatcher exception in "+currentScenario+": "+OneLine(text)+"\n");
            };
            currentDispatcher.UnhandledException+=dispatcherHandler;
            if(Application.Current!=null)Application.Current.DispatcherUnhandledException+=dispatcherHandler;
            StartWatchdog(WatchdogMilliseconds);
            bool oldHide=hideMarkers;bool oldLive=ProbeDefaults.Live;
            bool oldSelf=Program.IsSelfTest;
            SaveTargetPicker=null;ConflictAnswer=null;ConflictPrompts=0;ConflictRetrySawBusy=false;AtomicWriteObserver=null;
            try{
                Scenario("E1",240000,ScenarioE1);
                Scenario("E2",240000,ScenarioE2);
                Scenario("E3",180000,ScenarioE3);
                Scenario("E4",120000,ScenarioE4);
                Scenario("E5",180000,ScenarioE5);
                Scenario("E6",180000,ScenarioE6);
                Scenario("E7",240000,ScenarioE7);
                Scenario("E10",180000,ScenarioE10);
                Scenario("E11",120000,ScenarioE11);
            }catch(Exception error){
                all=false;lock(reportGate)report.Append("FAIL suite exception: ").Append(OneLine(error.ToString())).Append('\n');
            }finally{
                hideMarkers=oldHide;ProbeDefaults.Live=oldLive;Program.IsSelfTest=oldSelf;
                SaveTargetPicker=null;ConflictAnswer=null;AtomicWriteObserver=null;
                try{RecoveryStore.ReleaseTestOwner();}catch(Exception){}
                CloseAllWindows();
                try{Dispatcher.CurrentDispatcher.UnhandledException-=dispatcherHandler;}catch(Exception){}
                try{if(Application.Current!=null)Application.Current.DispatcherUnhandledException-=dispatcherHandler;}catch(Exception){}
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            lock(reportGate){
                report.Append('\n').Append(all?"RESULT PASS\n":"RESULT FAIL\n");
                File.WriteAllText(reportPath,report.ToString(),new UTF8Encoding(false));
                if(all)File.WriteAllText(passPath,"scenario-probe PASS\n"+DateTime.Now.ToString("o",CultureInfo.InvariantCulture)+"\n",new UTF8Encoding(false));
                else File.WriteAllText(failPath,report.ToString(),new UTF8Encoding(false));
                finished=true;
            }
            return all?0:1;
        }

        // ---------------------------------------------------------------- scaffolding
        static void Scenario(string id,int budget,Action body){
            currentScenario=id;
            scenarioDeadline=Environment.TickCount+budget;
            lock(reportGate)report.Append("\n## ").Append(id).Append('\n');
            try{body();}
            catch(Exception error){
                all=false;
                lock(reportGate)report.Append("FAIL ").Append(id).Append(" exception: ").Append(OneLine(error.ToString())).Append('\n');
            }
            // Keep a report on disk after every scenario: a hard crash in a later one still
            // leaves the evidence of everything that ran before it.
            try{
                lock(reportGate)File.WriteAllText(Path.Combine(outDir,"scenario-report.txt"),report.ToString(),new UTF8Encoding(false));
            }catch(Exception){}
        }
        // Between steps of one scenario: a dispatcher call that never returns would otherwise sit
        // until the process watchdog; this names the step that ran out of its budget.
        static void Step(string name){
            Progress(name);
            if(unchecked(Environment.TickCount-scenarioDeadline)>0)
                throw new TimeoutException("scenario "+currentScenario+" exceeded its budget at "+name);
        }
        // The run folder keeps a plain step log, so a hang is diagnosable without the report.
        static void Progress(string text){
            try{
                File.AppendAllText(Path.Combine(outDir,"scenario-progress.txt"),
                    DateTime.Now.ToString("HH:mm:ss.fff",CultureInfo.InvariantCulture)+" "+currentScenario+" "+text+"\r\n",
                    new UTF8Encoding(false));
            }catch(Exception){}
        }
        static void Verify(string name,bool ok,string detail){
            all&=ok;
            lock(reportGate){
                report.Append(ok?"PASS ":"FAIL ").Append(name);
                if(!String.IsNullOrEmpty(detail))report.Append(": ").Append(OneLine(detail));
                report.Append('\n');
            }
        }
        static void Note(string text){
            lock(reportGate)report.Append("note ").Append(OneLine(text)).Append('\n');
        }
        static void Append(string text){lock(reportGate)report.Append(text);}
        static string OneLine(string value){
            if(value==null)return "";
            return value.Replace('\r',' ').Replace('\n','|');
        }
        // Message plus the first stack frame: enough to tell two different callback crashes
        // apart without treating one exception surfacing twice as two findings.
        static string ExceptionKey(string value){
            if(value==null)return "";
            string[] lines=value.Replace("\r","").Split('\n');
            var key=new StringBuilder();
            int taken=0;
            foreach(string line in lines){
                string trimmed=line.Trim();
                if(trimmed.Length==0)continue;
                if(taken>0)key.Append(" / ");
                key.Append(trimmed);
                taken++;
                if(taken>=2)break;
            }
            string text=key.ToString();
            return text.Length>300?text.Substring(0,300):text;
        }
        static void RemoveFile(string path){try{if(File.Exists(path))File.Delete(path);}catch(Exception){}}
        static void StartWatchdog(int milliseconds){
            long start=Environment.TickCount;
            var thread=new Thread(delegate(){
                while(true){
                    Thread.Sleep(500);
                    if(finished)return;
                    if(unchecked(Environment.TickCount-start)>milliseconds){
                        lock(reportGate){
                            if(finished)return;
                            finished=true;
                            try{
                                report.Append("FAIL watchdog: the probe did not finish within ")
                                    .Append((milliseconds/1000).ToString(CultureInfo.InvariantCulture))
                                    .Append(" s; last scenario ").Append(currentScenario).Append('\n');
                                File.WriteAllText(Path.Combine(outDir,"scenario-report.txt"),report.ToString(),new UTF8Encoding(false));
                                File.WriteAllText(Path.Combine(outDir,"scenario-FAIL.txt"),"watchdog timeout\n",new UTF8Encoding(false));
                            }catch(Exception){}
                        }
                        Environment.Exit(2);
                    }
                }
            });
            thread.IsBackground=true;thread.Name="scenario-watchdog";thread.Start();
        }
        static void CloseAllWindows(){
            if(Application.Current==null)return;
            var open=new List<Window>();
            foreach(Window candidate in Application.Current.Windows)open.Add(candidate);
            foreach(Window candidate in open){
                try{
                    var editor=candidate as MainWindow;
                    if(editor!=null)MarkSaved(editor);
                    candidate.Close();
                }catch(Exception){}
            }
        }
        static void Pump(){
            LongLineBench.Idle();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));
            LongLineBench.Idle();
        }
        static void Pump(int milliseconds){
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer(DispatcherPriority.ApplicationIdle){Interval=TimeSpan.FromMilliseconds(milliseconds)};
            timer.Tick+=delegate{timer.Stop();frame.Continue=false;};
            timer.Start();
            Dispatcher.PushFrame(frame);
            Pump();
        }
        // Every window the probe creates is thrown away without a prompt.
        static void MarkSaved(MainWindow window){
            if(window==null||window.documents==null)return;
            try{
                window.CaptureTab();
                foreach(DocumentTab state in window.documents){state.Saved=state.Text;state.Recovered=false;}
                window.saved=window.text;
                window.recoveredDocument=false;
            }catch(Exception){}
        }
        static void CloseQuiet(MainWindow window){
            if(window==null)return;
            try{MarkSaved(window);window.Close();}
            catch(Exception error){
                // A close must not throw; a silent catch here once hid a product crash.
                all=false;
                Append("FAIL window close in "+currentScenario+" raised "+error.GetType().Name+": "+OneLine(error.Message)+"\n");
            }
        }
        static MainWindow NewWindow(){
            var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=1000,Height=720};
            window.Show();
            window.Left=-20000;window.Top=-20000;
            window.UpdateLayout();
            Pump();
            return window;
        }
        static SourceEditor Source(MainWindow window){return window.source;}
        static string TextOf(MainWindow window){return window.text;}
        static void Type(MainWindow window,string value){window.source.TextArea.PerformTextInput(value);}
        static void LoadText(MainWindow window,string body){
            window.SetText(body);window.SetMode(true);Pump();
        }
        // A file opened with LoadFile starts in 읽기 mode unless the "항상 편집" preference is on
        // (alwaysEditing defaults false), exactly like a user pressing 수정 시작 (Ctrl+E). Every
        // scenario that edits a loaded file goes through that same public toggle.
        static void EnterEdit(MainWindow window){
            if(!window.editing){window.SetMode(true);Pump(150);}
        }
        // The app's own key split: TextArea PreviewKeyDown (list/table rules) first, then the
        // vendor command the real key reaches when no app handler takes it.
        static bool RaisePreviewKey(MainWindow window,Key key){
            SourceEditor source=Source(window);
            if(source==null)return false;
            PresentationSource presentation=PresentationSource.FromVisual(source);
            if(presentation==null)return false;
            var args=new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
            source.TextArea.RaiseEvent(args);
            return args.Handled;
        }
        static void BackspaceKey(MainWindow window){
            if(!RaisePreviewKey(window,Key.Back))EditingCommands.Backspace.Execute(null,Source(window).TextArea);
            Pump(40);
        }
        static void DeleteKey(MainWindow window){
            if(!RaisePreviewKey(window,Key.Delete))EditingCommands.Delete.Execute(null,Source(window).TextArea);
            Pump(40);
        }
        static void MoveLeft(MainWindow window){EditingCommands.MoveLeftByCharacter.Execute(null,Source(window).TextArea);}
        static void MoveRight(MainWindow window){EditingCommands.MoveRightByCharacter.Execute(null,Source(window).TextArea);}
        static void MoveLineEnd(MainWindow window){EditingCommands.MoveToLineEnd.Execute(null,Source(window).TextArea);}
        static void MoveDocumentEnd(MainWindow window){EditingCommands.MoveToDocumentEnd.Execute(null,Source(window).TextArea);}
        // First UTF-16 unit that is half of a surrogate pair on its own.
        static int LoneSurrogateIndex(string text){
            if(text==null)return -1;
            for(int i=0;i<text.Length;i++){
                if(char.IsHighSurrogate(text[i])){
                    if(i+1>=text.Length||!char.IsLowSurrogate(text[i+1]))return i;
                    i++;
                }else if(char.IsLowSurrogate(text[i])){
                    return i;
                }
            }
            return -1;
        }
        static bool MidSurrogate(string text,int offset){
            return text!=null&&offset>0&&offset<text.Length&&char.IsHighSurrogate(text[offset-1])&&char.IsLowSurrogate(text[offset]);
        }
        static int IndexOf(string text,string piece){
            return text==null||piece==null?-1:text.IndexOf(piece,StringComparison.Ordinal);
        }
        static string Show(string value){
            if(value==null)return "<null>";
            var text=new StringBuilder("\"");
            foreach(char c in value){
                if(c=='\r')text.Append("\\r");
                else if(c=='\n')text.Append("\\n");
                else if(c=='\t')text.Append("\\t");
                else if(char.IsSurrogate(c)||c<32||c>126)text.Append("\\u").Append(((int)c).ToString("X4",CultureInfo.InvariantCulture));
                else text.Append(c);
            }
            return text.Append('"').ToString();
        }
        static string N(double value){return value.ToString("0.##",CultureInfo.InvariantCulture);}
        static string ByteHash(byte[] bytes){
            if(bytes==null)return "<null>";
            using(var hash=System.Security.Cryptography.SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(bytes));
        }

        // ---------------------------------------------------------------- E1 surrogates / combining
        // Values are typed through the editor's own text input on an offscreen window, in the Basic
        // view (markers hidden) and the Live view (caret line raw), in a paragraph, inside **bold**
        // and inside a table cell. The document is the authority: caret stops must never split a
        // surrogate pair, Backspace/Delete must never leave half of one, find must locate the value
        // and save/reopen must round-trip byte-exactly as UTF-8.
        static readonly string[] E1Values=new string[]{
            "😀",                                     /* grinning face, U+1F600 */
            "👍🏽",                         /* thumbs up + skin tone, U+1F44D U+1F3FD */
            "👨‍👩‍👧", /* ZWJ family */
            "é",                                          /* e + combining acute, U+0301 */
            "𠀀"                                      /* U+20000 */
        };
        static readonly string[] E1Names=new string[]{"U+1F600","U+1F44D+U+1F3FD","ZWJ-family","e+U+0301","U+20000"};

        static void ScenarioE1(){
            string folder=Path.Combine(outDir,"e1");Directory.CreateDirectory(folder);
            string[] documents=new string[]{
                "앞 문단\n\n여기에 입력: \n\n뒤 문단\n",
                "앞 **굵게** 뒤\n",
                "| 항목 | 값 |\n| --- | --- |\n| 가 | 나 |\n"
            };
            string[] contexts=new string[]{"paragraph","bold","table-cell"};
            int[] carets=new int[]{
                IndexOf(documents[0],"입력: ")+4,
                IndexOf(documents[1],"굵게")+1,
                IndexOf(documents[2],"가")+1
            };
            MainWindow window=null;
            bool tableLeak=false;
            try{
                window=NewWindow();
                window.SetOpenInTabs(true);
                foreach(bool basic in new bool[]{true,false}){
                    string mode=basic?"basic":"live";
                    window.SetLiveMode(!basic);
                    Pump(150);
                    for(int c=0;c<documents.Length;c++){
                        Step("E1 "+mode+" "+contexts[c]);
                        for(int v=0;v<E1Values.Length;v++){
                            string value=E1Values[v],name=E1Names[v];
                            LoadText(window,documents[c]);
                            int at=carets[c];
                            window.source.Select(at,0);Pump(40);
                            Type(window,value);Pump(120);
                            string typedText=window.source.Document.Text;
                            bool typed=IndexOf(typedText,value)>=0;
                            int loneAfterType=LoneSurrogateIndex(typedText);
                            int afterType=window.source.CaretIndex;
                            MoveLeft(window);Pump(25);
                            int leftOffset=window.source.CaretIndex;
                            bool midLeft=MidSurrogate(typedText,leftOffset);
                            MoveRight(window);Pump(25);
                            int rightOffset=window.source.CaretIndex;
                            bool midRight=MidSurrogate(typedText,rightOffset);
                            bool movedBack=leftOffset<afterType&&rightOffset==afterType;
                            int walkSplits=0,walkStops=0;
                            window.source.Select(typedText.Length,0);Pump(25);
                            int previous=typedText.Length;
                            for(int guard=0;guard<typedText.Length+8;guard++){
                                MoveLeft(window);
                                int pos=window.source.CaretIndex;
                                if(pos>=previous)break;
                                walkStops++;
                                if(MidSurrogate(typedText,pos))walkSplits++;
                                previous=pos;
                                if(pos==0)break;
                            }
                            Verify("E1 ["+mode+"/"+contexts[c]+"/"+name+"] typed, caret moves whole code points",
                                typed&&loneAfterType<0&&!midLeft&&!midRight&&movedBack&&walkSplits==0,
                                "typed="+typed+" lone="+loneAfterType+" left="+leftOffset+" midLeft="+midLeft+" right="+rightOffset+" midRight="+midRight+
                                " afterType="+afterType+" walkStops="+walkStops+" splits="+walkSplits+" text="+Show(typedText));

                            // Backspace with the caret after the value.
                            LoadText(window,documents[c]);
                            window.source.Select(carets[c],0);Pump(40);
                            Type(window,value);Pump(100);
                            string inserted=window.source.Document.Text;
                            int insertAt=IndexOf(inserted,value);
                            int endAt=insertAt<0?-1:insertAt+value.Length;
                            int lengthBefore=inserted.Length;
                            if(endAt>=0)window.source.Select(endAt,0);
                            Pump(25);
                            BackspaceKey(window);
                            string afterBack=window.source.Document.Text;
                            int backLone=LoneSurrogateIndex(afterBack);
                            bool backRemoved=afterBack.Length<lengthBefore&&afterBack.Length>=lengthBefore-value.Length;

                            // Delete with the caret before the value.
                            LoadText(window,documents[c]);
                            window.source.Select(carets[c],0);Pump(40);
                            Type(window,value);Pump(100);
                            inserted=window.source.Document.Text;
                            insertAt=IndexOf(inserted,value);
                            lengthBefore=inserted.Length;
                            if(insertAt>=0)window.source.Select(insertAt,0);
                            Pump(25);
                            DeleteKey(window);
                            string afterDelete=window.source.Document.Text;
                            int deleteLone=LoneSurrogateIndex(afterDelete);
                            bool deleteRemoved=afterDelete.Length<lengthBefore&&afterDelete.Length>=lengthBefore-value.Length;
                            Verify("E1 ["+mode+"/"+contexts[c]+"/"+name+"] Backspace/Delete never leave a lone surrogate",
                                insertAt>=0&&backRemoved&&deleteRemoved&&backLone<0&&deleteLone<0,
                                "insertAt="+insertAt+" backRemoved="+backRemoved+" backLone="+backLone+" deleteRemoved="+deleteRemoved+" deleteLone="+deleteLone+
                                " back="+Show(afterBack)+" delete="+Show(afterDelete));
                            if(contexts[c]=="table-cell"&&(backLone>=0||deleteLone>=0))tableLeak=true;
                        }
                    }
                }

                Step("E1 find");
                Note("E1 table rows: MarkdownLivePreview.TableRowElement.GetNextCaretPosition (MarkdownLivePreview.cs:1774) walks source offsets one UTF-16 unit at a time and answers a caret stop for every offset in Normal/EveryCodepoint mode; TextUtilities.GetNextCaretPosition, which every non-table line uses, refuses a stop between a surrogate pair (TextUtilities.cs:331) and does not stop before a combining mark (TextUtilities.cs:361)");
                if(tableLeak){
                    // Severity check: a lone surrogate cannot be encoded as strict UTF-8, so the
                    // document also becomes unsavable. Recorded as a note; the FAIL above is the bug.
                    MainWindow leak=null;
                    try{
                        leak=NewWindow();
                        LoadText(leak,documents[2]);
                        leak.source.Select(carets[2],0);Pump(40);
                        Type(leak,E1Values[0]);Pump(100);
                        int at=IndexOf(leak.source.Document.Text,E1Values[0]);
                        leak.source.Select(at+E1Values[0].Length,0);Pump(30);
                        BackspaceKey(leak);
                        string broken=leak.source.Document.Text;
                        int lone=LoneSurrogateIndex(broken);
                        string saveFile=Path.Combine(folder,"table-split.md");
                        string pick=saveFile;
                        string saveResult;
                        SaveTargetPicker=delegate(string name){return pick;};
                        try{saveResult="saved="+leak.Save(true);}
                        catch(Exception error){saveResult=error.GetType().Name;}
                        finally{SaveTargetPicker=null;}
                        Note("E1 [table/split] after one Backspace in a cell the buffer holds a lone surrogate at "+lone+" and saving reports "+saveResult);
                    }finally{CloseQuiet(leak);}
                }
                string allText="앞 "+E1Values[0]+" "+E1Values[1]+" "+E1Values[2]+" "+E1Values[3]+" "+E1Values[4]+" 뒤\n";
                foreach(bool basic in new bool[]{true,false}){
                    string mode=basic?"basic":"live";
                    window.SetLiveMode(!basic);
                    LoadText(window,allText);
                    window.ShowSearch();Pump(400);
                    for(int v=0;v<E1Values.Length;v++){
                        Step("E1 find "+mode+" "+E1Names[v]);
                        window.query.Text=E1Values[v];
                        window.query.CaretIndex=E1Values[v].Length;
                        Pump(450);
                        window.source.Select(0,0);
                        Pump(120);
                        window.Find();Pump(450);
                        bool found=window.source.SelectedText==E1Values[v]&&window.source.SelectionLength==E1Values[v].Length;
                        Verify("E1 [find/"+mode+"/"+E1Names[v]+"] finds the sequence",found,
                            "selected="+Show(window.source.SelectedText)+" length="+window.source.SelectionLength+" caret="+window.source.CaretIndex);
                    }
                    window.query.Text="";
                    window.CloseSearch();Pump(250);
                }

                Step("E1 save/reopen");
                window.SetLiveMode(false);
                LoadText(window,allText);
                string file=Path.Combine(folder,"emoji-roundtrip.md");
                string picked=file;
                bool saved=false;
                SaveTargetPicker=delegate(string name){return picked;};
                try{saved=window.Save(true);}finally{SaveTargetPicker=null;}
                Pump(250);
                byte[] first=File.Exists(file)?File.ReadAllBytes(file):null;
                string decoded=first==null?null:new UTF8Encoding(false,true).GetString(first);
                bool bom=first!=null&&first.Length>=3&&first[0]==0xEF&&first[1]==0xBB&&first[2]==0xBF;
                Verify("E1 save writes the sequences as UTF-8 without BOM",
                    saved&&first!=null&&!bom&&decoded==allText&&LoneSurrogateIndex(decoded)<0,
                    "saved="+saved+" bytes="+(first==null?-1:first.Length)+" bom="+bom+" equal="+(decoded==allText)+" lone="+LoneSurrogateIndex(decoded));

                MainWindow reopened=null;
                try{
                    reopened=NewWindow();
                    reopened.LoadFile(file);Pump(400);
                    string reopenedText=reopened.source.Document.Text;
                    reopened.saved=reopened.text;
                    bool again=reopened.Save(false);Pump(250);
                    byte[] second=File.Exists(file)?File.ReadAllBytes(file):null;
                    Verify("E1 reopen round-trips byte-exactly and saves the same bytes",
                        reopenedText==allText&&again&&second!=null&&ByteHash(second)==ByteHash(first),
                        "textEqual="+(reopenedText==allText)+" saved="+again+" hash="+ByteHash(second)+" first="+ByteHash(first));
                }finally{CloseQuiet(reopened);reopened=null;}
            }finally{CloseQuiet(window);}
        }

        // ---------------------------------------------------------------- E2 paste
        // Real clipboard data is put on the clipboard inside the probe and the app's own paste
        // commands run (menu/Ctrl+V path and the Ctrl+Shift+V plain path). The clipboard text the
        // machine had before the run is restored afterwards.
        static void SetClipboard(IDataObject data){
            for(int attempt=0;attempt<8;attempt++){
                try{Clipboard.SetDataObject(data,true);return;}
                catch(System.Runtime.InteropServices.ExternalException){Thread.Sleep(80);}
            }
            throw new IOException("the clipboard stayed locked");
        }
        static void SetClipboardImage(BitmapSource image){
            for(int attempt=0;attempt<8;attempt++){
                try{Clipboard.SetImage(image);return;}
                catch(System.Runtime.InteropServices.ExternalException){Thread.Sleep(80);}
            }
            throw new IOException("the clipboard stayed locked");
        }
        // CF_HTML: fixed-width decimal offsets in UTF-8 bytes, the format the spec defines.
        static string CfHtml(string body){
            string prefix="<html><body><!--StartFragment-->";
            string suffix="<!--EndFragment--></body></html>";
            const string template="Version:0.9\r\nStartHTML:{0}\r\nEndHTML:{1}\r\nStartFragment:{2}\r\nEndFragment:{3}\r\n";
            string zero=string.Format(CultureInfo.InvariantCulture,template,"0000000000","0000000000","0000000000","0000000000");
            int baseLength=Encoding.UTF8.GetByteCount(zero);
            string html=prefix+body+suffix;
            int startHtml=baseLength;
            int startFragment=startHtml+Encoding.UTF8.GetByteCount(prefix);
            int endFragment=startFragment+Encoding.UTF8.GetByteCount(body);
            int endHtml=startHtml+Encoding.UTF8.GetByteCount(html);
            return string.Format(CultureInfo.InvariantCulture,template,
                startHtml.ToString("D10",CultureInfo.InvariantCulture),
                endHtml.ToString("D10",CultureInfo.InvariantCulture),
                startFragment.ToString("D10",CultureInfo.InvariantCulture),
                endFragment.ToString("D10",CultureInfo.InvariantCulture))+html;
        }
        static void PasteCommand(MainWindow window,bool plain){
            window.PasteClipboard(plain);
            Pump(250);
        }
        static string Doc(MainWindow window){return window.source.Document.Text;}
        static bool UndoRestores(MainWindow window,string before){
            window.Undo();Pump(200);
            return Doc(window)==before;
        }
        static void ScenarioE2(){
            string folder=Path.Combine(outDir,"e2");Directory.CreateDirectory(folder);
            MainWindow window=null;
            string previousText=null;
            try{
                try{previousText=Clipboard.GetText();}catch(Exception){previousText=null;}
                window=NewWindow();
                window.SetLiveMode(false);
                Pump(150);
                string before=Doc(window);

                // a) CF_HTML with a heading, a list, a table, a link, bold and italic.
                Step("E2 html");
                string body="<h2>제목</h2><ul><li>하나</li><li>둘</li></ul>"+
                    "<table><tr><th>A</th><th>B</th></tr><tr><td>1</td><td>2</td></tr></table>"+
                    "<p><a href=\"https://example.invalid/a\">링크</a> <b>굵게</b> <i>기울임</i></p>";
                string plainText="제목\n하나\n둘\nA\tB\n링크 굵게 기울임\n";
                var htmlData=new DataObject();
                htmlData.SetData(DataFormats.Html,CfHtml(body));
                htmlData.SetData(DataFormats.UnicodeText,plainText);
                SetClipboard(htmlData);
                Exception htmlError=null;
                LoadText(window,"");
                try{PasteCommand(window,false);}catch(Exception error){htmlError=error;}
                string htmlResult=Doc(window);
                bool heading=htmlResult.IndexOf("## 제목",StringComparison.Ordinal)>=0;
                bool list=htmlResult.IndexOf("- 하나",StringComparison.Ordinal)>=0&&htmlResult.IndexOf("- 둘",StringComparison.Ordinal)>=0;
                bool table=htmlResult.IndexOf("| A | B |",StringComparison.Ordinal)>=0&&
                    htmlResult.IndexOf("| --- | --- |",StringComparison.Ordinal)>=0&&
                    htmlResult.IndexOf("| 1 | 2 |",StringComparison.Ordinal)>=0;
                bool link=htmlResult.IndexOf("[링크](https://example.invalid/a)",StringComparison.Ordinal)>=0;
                bool bold=htmlResult.IndexOf("**굵게**",StringComparison.Ordinal)>=0;
                bool italic=htmlResult.IndexOf("*기울임*",StringComparison.Ordinal)>=0;
                Verify("E2 [html] paste converts heading, list, table, link, bold and italic",
                    htmlError==null&&heading&&list&&table&&link&&bold&&italic,
                    "error="+OneLine(htmlError==null?"":htmlError.Message)+" heading="+heading+" list="+list+" table="+table+" link="+link+" bold="+bold+" italic="+italic+" text="+Show(htmlResult));
                Verify("E2 [html] one paste is one undo step",htmlError==null&&UndoRestores(window,before),Show(Doc(window)));

                LoadText(window,"");
                try{PasteCommand(window,true);}catch(Exception error){Note("E2 plain paste exception: "+OneLine(error.Message));}
                Verify("E2 [html] plain paste inserts the text representation verbatim",
                    Doc(window)==plainText,"text="+Show(Doc(window)));
                UndoRestores(window,before);

                // b) RTF.
                Step("E2 rtf");
                string rtf=@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fnil Arial;}}\viewkind4\uc1\pard\f0\fs20 plain \b bold\b0  and \i italic\i0 .\par}";
                var rtfData=new DataObject();
                rtfData.SetData(DataFormats.Rtf,rtf);
                rtfData.SetData(DataFormats.UnicodeText,"plain bold and italic .");
                SetClipboard(rtfData);
                Exception rtfError=null;
                LoadText(window,"");
                try{PasteCommand(window,false);}catch(Exception error){rtfError=error;}
                string rtfResult=Doc(window);
                Verify("E2 [rtf] paste keeps bold and italic as Markdown",
                    rtfError==null&&rtfResult.IndexOf("**bold**",StringComparison.Ordinal)>=0&&rtfResult.IndexOf("*italic*",StringComparison.Ordinal)>=0,
                    "error="+OneLine(rtfError==null?"":rtfError.Message)+" text="+Show(rtfResult));
                Verify("E2 [rtf] one paste is one undo step",rtfError==null&&UndoRestores(window,before),Show(Doc(window)));

                LoadText(window,"");
                PasteCommand(window,true);
                Verify("E2 [rtf] plain paste inserts the text representation verbatim",Doc(window)=="plain bold and italic .","text="+Show(Doc(window)));
                UndoRestores(window,before);

                // c) Plain text only.
                Step("E2 plain text");
                string plainBody="줄 하나\n줄 둘\n";
                var textData=new DataObject();
                textData.SetData(DataFormats.UnicodeText,plainBody);
                SetClipboard(textData);
                LoadText(window,"");
                Exception textError=null;
                try{PasteCommand(window,false);}catch(Exception error){textError=error;}
                Verify("E2 [text] plain text pastes without an exception and keeps its lines",
                    textError==null&&Doc(window)=="줄 하나\n줄 둘\n",
                    "error="+OneLine(textError==null?"":textError.Message)+" text="+Show(Doc(window)));
                Verify("E2 [text] one paste is one undo step",textError==null&&UndoRestores(window,before),Show(Doc(window)));

                // d) Tab-separated rows: the app has no TSV-to-table command, so the honest check is
                // that the tabs survive as text and the document stays valid.
                Step("E2 tsv");
                string tsv="이름\t값\n하나\t1\n둘\t2\n";
                var tsvData=new DataObject();
                tsvData.SetData(DataFormats.UnicodeText,tsv);
                SetClipboard(tsvData);
                LoadText(window,"");
                Exception tsvError=null;
                try{PasteCommand(window,false);}catch(Exception error){tsvError=error;}
                string tsvResult=Doc(window);
                Verify("E2 [tsv] tab-separated text keeps every tab and line",
                    tsvError==null&&tsvResult==tsv&&CountOf(tsvResult,'\t')==CountOf(tsv,'\t'),
                    "error="+OneLine(tsvError==null?"":tsvError.Message)+" tabs="+CountOf(tsvResult,'\t')+" text="+Show(tsvResult));
                Note("E2 [tsv] the app does not convert tab-separated rows into a Markdown table (no TSV command in the source); the text is inserted verbatim");
                Verify("E2 [tsv] one paste is one undo step",tsvError==null&&UndoRestores(window,before),Show(Doc(window)));

                // e) Bitmap. The app's image entry points are the menu (Ctrl+Shift+I) and an image
                // drop; a clipboard bitmap has no Markdown conversion, so nothing may be inserted
                // silently inside the text and nothing may throw.
                Step("E2 bitmap");
                byte[] pixels=new byte[4*8*8];
                for(int i=0;i<pixels.Length;i+=4){pixels[i]=40;pixels[i+1]=120;pixels[i+2]=200;pixels[i+3]=255;}
                var bitmap=BitmapSource.Create(8,8,96,96,PixelFormats.Bgra32,null,pixels,4*8);
                bitmap.Freeze();
                SetClipboardImage(bitmap);
                LoadText(window,"앞\n");
                string beforeBitmap=Doc(window);
                Exception bitmapError=null;
                try{PasteCommand(window,false);}catch(Exception error){bitmapError=error;}
                string bitmapResult=Doc(window);
                bool imageInserted=bitmapResult.IndexOf("![",StringComparison.Ordinal)>=0||bitmapResult.IndexOf("](data:",StringComparison.Ordinal)>=0;
                Verify("E2 [bitmap] paste does not throw and leaves the text intact",
                    bitmapError==null&&(bitmapResult==beforeBitmap||imageInserted),
                    "error="+OneLine(bitmapError==null?"":bitmapError.Message)+" unchanged="+(bitmapResult==beforeBitmap)+" image="+imageInserted+" text="+Show(bitmapResult));
                if(bitmapResult==beforeBitmap)Note("E2 [bitmap] a clipboard bitmap is ignored by the paste command (image insert is only the menu command / image drop); this is a limitation, not a crash");
                UndoRestores(window,before);
            }finally{
                try{
                    if(String.IsNullOrEmpty(previousText))Clipboard.Clear();else Clipboard.SetText(previousText);
                }catch(Exception){}
                CloseQuiet(window);
            }
        }
        static int CountOf(string text,char value){
            if(text==null)return -1;
            int count=0;
            foreach(char c in text)if(c==value)count++;
            return count;
        }

        // ---------------------------------------------------------------- E3 external change
        // The app keeps the SHA-256 of the bytes it loaded and compares it at save time
        // (SaveSafety.cs:11 CheckSaveConflict, called from DocumentEncoding.WriteConvertedToPath).
        // There is no file watcher and no activation-time check: an external edit is only noticed
        // when the user saves. Each case drives that save path with the self-test seams
        // (ConflictAnswer / SaveTargetPicker) so the production prompt never blocks a hidden run.
        static void ScenarioE3(){
            string folder=Path.Combine(outDir,"e3");Directory.CreateDirectory(folder);
            MainWindow window=null;
            try{
                string file=Path.Combine(folder,"external.md");
                File.WriteAllText(file,"원본 한 줄\n",new UTF8Encoding(false));
                window=NewWindow();
                window.LoadFile(file);Pump(400);
                EnterEdit(window);
                window.source.Select(window.source.Document.TextLength,0);
                Type(window,"내 편집");Pump(150);
                string bufferBefore=Doc(window);

                Step("E3 conflict");
                File.WriteAllText(file,"외부 수정 한 줄\n",new UTF8Encoding(false));
                ConflictPrompts=0;ConflictAnswer=null;SaveTargetPicker=null;
                bool thrown=false;string thrownType="";
                try{window.Save(false);}
                catch(SaveConflictException){thrown=true;thrownType="SaveConflictException";}
                catch(Exception error){thrown=true;thrownType=error.GetType().Name;}
                string onDisk=File.ReadAllText(file,new UTF8Encoding(false));
                Verify("E3 [external edit] save refuses to overwrite the other program's file",
                    thrown&&onDisk=="외부 수정 한 줄\n"&&Doc(window)==bufferBefore&&
                    Doc(window).IndexOf("내 편집",StringComparison.Ordinal)>=0,
                    "thrown="+thrown+" type="+thrownType+" disk="+Show(onDisk)+" buffer="+Show(Doc(window)));

                Step("E3 decline");
                ConflictPrompts=0;ConflictAnswer=MessageBoxResult.No;
                bool declinedSave=true;Exception declineError=null;
                try{declinedSave=window.Save(false);}catch(Exception error){declineError=error;}
                Verify("E3 [external edit] the conflict path asks and can keep both versions",
                    declineError==null&&!declinedSave&&ConflictPrompts==1&&
                    File.ReadAllText(file,new UTF8Encoding(false))=="외부 수정 한 줄\n"&&Doc(window)==bufferBefore,
                    "error="+OneLine(declineError==null?"":declineError.Message)+" saved="+declinedSave+" prompts="+ConflictPrompts+
                    " disk="+Show(File.ReadAllText(file,new UTF8Encoding(false)))+" buffer="+Show(Doc(window)));

                Step("E3 save as");
                string copy=Path.Combine(folder,"conflict-copy.md");
                string pickedCopy=copy;
                ConflictPrompts=0;ConflictAnswer=MessageBoxResult.Yes;SaveTargetPicker=delegate(string name){return pickedCopy;};
                bool retried=false;Exception retryError=null;
                try{retried=window.Save(false);}catch(Exception error){retryError=error;}
                finally{SaveTargetPicker=null;ConflictAnswer=null;}
                Verify("E3 [external edit] answering 'save as' writes the buffer to a new file",
                    retryError==null&&retried&&File.Exists(copy)&&
                    File.ReadAllText(copy,new UTF8Encoding(false))==bufferBefore&&
                    File.ReadAllText(file,new UTF8Encoding(false))=="외부 수정 한 줄\n",
                    "error="+OneLine(retryError==null?"":retryError.Message)+" saved="+retried+" copy="+Show(File.Exists(copy)?File.ReadAllText(copy):null)+
                    " disk="+Show(File.ReadAllText(file,new UTF8Encoding(false)))+" sawBusy="+ConflictRetrySawBusy);

                Step("E3 deleted");
                string deleted=Path.Combine(folder,"deleted.md");
                File.WriteAllText(deleted,"지울 파일\n",new UTF8Encoding(false));
                MainWindow deletion=NewWindow();
                try{
                    deletion.LoadFile(deleted);Pump(350);
                    EnterEdit(deletion);
                    deletion.source.Select(deletion.source.Document.TextLength,0);
                    Type(deletion,"편집");Pump(150);
                    string edited=Doc(deletion);
                    File.Delete(deleted);
                    ConflictPrompts=0;ConflictAnswer=MessageBoxResult.No;SaveTargetPicker=null;
                    bool recreated=true;Exception deleteError=null;
                    try{recreated=deletion.Save(false);}catch(Exception error){deleteError=error;}
                    Verify("E3 [deleted on disk] save asks instead of silently recreating the file",
                        deleteError==null&&!recreated&&ConflictPrompts==1&&!File.Exists(deleted)&&Doc(deletion)==edited&&
                        edited.IndexOf("편집",StringComparison.Ordinal)>=0,
                        "error="+OneLine(deleteError==null?"":deleteError.Message)+" saved="+recreated+" prompts="+ConflictPrompts+
                        " exists="+File.Exists(deleted)+" buffer="+Show(Doc(deletion)));
                    string rescued=Path.Combine(folder,"deleted-copy.md");
                    string pickedRescue=rescued;
                    ConflictPrompts=0;ConflictAnswer=MessageBoxResult.Yes;SaveTargetPicker=delegate(string name){return pickedRescue;};
                    bool rescuedSave=false;Exception rescueError=null;
                    try{rescuedSave=deletion.Save(false);}catch(Exception error){rescueError=error;}
                    finally{SaveTargetPicker=null;ConflictAnswer=null;}
                    Verify("E3 [deleted on disk] a new name keeps the edited text",
                        rescueError==null&&rescuedSave&&File.Exists(rescued)&&File.ReadAllText(rescued,new UTF8Encoding(false))==edited,
                        "error="+OneLine(rescueError==null?"":rescueError.Message)+" saved="+rescuedSave+" text="+Show(File.Exists(rescued)?File.ReadAllText(rescued):null));
                }finally{CloseQuiet(deletion);}

                Step("E3 control");
                string control=Path.Combine(folder,"control.md");
                File.WriteAllText(control,"변하지 않은 파일\n",new UTF8Encoding(false));
                MainWindow plain=NewWindow();
                try{
                    plain.LoadFile(control);Pump(300);
                    EnterEdit(plain);
                    plain.source.Select(plain.source.Document.TextLength,0);
                    Type(plain,"추가");Pump(120);
                    string expected=Doc(plain);
                    ConflictPrompts=0;ConflictAnswer=null;SaveTargetPicker=null;
                    bool ok=false;Exception controlError=null;
                    try{ok=plain.Save(false);}catch(Exception error){controlError=error;}
                    Verify("E3 [control] an untouched fingerprint saves in place",
                        controlError==null&&ok&&File.ReadAllText(control,new UTF8Encoding(false))==expected&&ConflictPrompts==0&&
                        expected.IndexOf("추가",StringComparison.Ordinal)>=0,
                        "error="+OneLine(controlError==null?"":controlError.Message)+" saved="+ok+" prompts="+ConflictPrompts+" disk="+Show(File.ReadAllText(control)));
                }finally{CloseQuiet(plain);}
                Note("E3 activation: there is no activation-time or file-watcher check in the product (only CheckSaveConflict at save), so an external edit is surfaced when the user saves, not when the window is re-activated");
            }finally{CloseQuiet(window);}
        }

        // ---------------------------------------------------------------- E4 read-only file
        // A read-only target cannot be replaced; the write fails and the buffer keeps the edit.
        // The production path shows "저장할 수 없습니다" (Editor.cs SaveAsync catch); the self-test
        // seam rethrows instead of opening a modal box, so the probe asserts that visible failure.
        static void ScenarioE4(){
            string folder=Path.Combine(outDir,"e4");Directory.CreateDirectory(folder);
            string file=Path.Combine(folder,"readonly.md");
            File.WriteAllText(file,"읽기 전용 원본\n",new UTF8Encoding(false));
            File.SetAttributes(file,FileAttributes.ReadOnly);
            MainWindow window=null;
            try{
                window=NewWindow();
                window.LoadFile(file);Pump(400);
                EnterEdit(window);
                window.source.Select(window.source.Document.TextLength,0);
                Type(window," 추가 편집");Pump(150);
                string edited=Doc(window);
                Step("E4 save");
                bool thrown=false;string thrownType="";
                ConflictAnswer=null;SaveTargetPicker=null;
                try{window.Save(false);}
                catch(UnauthorizedAccessException){thrown=true;thrownType="UnauthorizedAccessException";}
                catch(IOException){thrown=true;thrownType="IOException";}
                catch(Exception error){thrown=true;thrownType=error.GetType().Name;}
                string disk=File.ReadAllText(file,new UTF8Encoding(false));
                Verify("E4 [read-only] save fails loudly, the file is unchanged and the edit stays in the editor",
                    thrown&&(thrownType=="UnauthorizedAccessException"||thrownType=="IOException")&&
                    disk=="읽기 전용 원본\n"&&Doc(window)==edited&&
                    edited.IndexOf("추가 편집",StringComparison.Ordinal)>=0,
                    "thrown="+thrown+" type="+thrownType+" disk="+Show(disk)+" buffer="+Show(Doc(window)));

                Step("E4 save as");
                string copy=Path.Combine(folder,"readonly-copy.md");
                string pickedCopy=copy;
                bool savedAs=false;Exception copyError=null;
                SaveTargetPicker=delegate(string name){return pickedCopy;};
                try{savedAs=window.Save(true);}catch(Exception error){copyError=error;}
                finally{SaveTargetPicker=null;}
                Verify("E4 [read-only] Save As keeps the edited text in a writable file",
                    copyError==null&&savedAs&&File.Exists(copy)&&File.ReadAllText(copy,new UTF8Encoding(false))==edited,
                    "error="+OneLine(copyError==null?"":copyError.Message)+" saved="+savedAs+" text="+Show(File.Exists(copy)?File.ReadAllText(copy):null));

                Step("E4 unlocked");
                File.SetAttributes(file,FileAttributes.Normal);
                MainWindow unlocked=NewWindow();
                try{
                    unlocked.LoadFile(file);Pump(300);
                    EnterEdit(unlocked);
                    unlocked.source.Select(unlocked.source.Document.TextLength,0);
                    Type(unlocked," 다시");Pump(120);
                    string expected=Doc(unlocked);
                    bool ok=false;Exception unlockError=null;
                    ConflictAnswer=null;
                    try{ok=unlocked.Save(false);}catch(Exception error){unlockError=error;}
                    Verify("E4 [read-only] after the attribute is cleared the in-place save works",
                        unlockError==null&&ok&&File.ReadAllText(file,new UTF8Encoding(false))==expected&&
                        expected.IndexOf("다시",StringComparison.Ordinal)>=0,
                        "error="+OneLine(unlockError==null?"":unlockError.Message)+" saved="+ok+" disk="+Show(File.ReadAllText(file)));
                }finally{CloseQuiet(unlocked);}
                Note("E4 a folder without write permission was skipped: denying the current user's ACL can only be restored by changing permissions, which a probe must not do; the read-only file covers the same failure path");
                Note("E4 the user-visible \"저장할 수 없습니다\" box exists in the product (Editor.cs SaveAsync catch) but a hidden run must not open it; the self-test seam rethrows instead");
            }finally{
                try{if(File.Exists(file))File.SetAttributes(file,FileAttributes.Normal);}catch(Exception){}
                CloseQuiet(window);
            }
        }

        // ---------------------------------------------------------------- E5 recovery
        // RecoveryStore.TestRoot is the seam the recovery probe already uses; without it the store
        // is disabled in a self-test run. An unsaved document is written through the same
        // synchronous path the logoff/restart code uses (no Close, so nothing deletes the draft),
        // the owner lock is released to stand in for the dead process, and the restart path
        // (Recovery.RestoreRecovery) must put the text back into a new tab.
        static void ScenarioE5(){
            string folder=Path.Combine(outDir,"e5");Directory.CreateDirectory(folder);
            string root=Path.Combine(folder,"recovery-root");Directory.CreateDirectory(root);
            string previousRoot=RecoveryStore.TestRoot;
            MainWindow writer=null,restored=null;
            try{
                Step("E5 draft");
                RecoveryStore.TestRoot=root;
                writer=NewWindow();
                writer.recoveryTimer.Stop();
                writer.path="";
                writer.SetText("복구 대상 문단입니다.\n두 번째 줄 😀\n");
                writer.SetMode(true);
                writer.source.Select(writer.source.Document.TextLength,0);
                Type(writer,"마지막 입력");Pump(150);
                string expected=Doc(writer);
                writer.FlushRecoveryDraftsForSessionEnd();

                Step("E5 list");
                RecoveryStore.ReleaseTestOwner();
                var records=RecoveryStore.List();
                RecoveryRecord found=null;
                foreach(RecoveryRecord record in records){
                    if(record.OriginalPath==""&&record.Characters==expected.Length)found=record;
                }
                Verify("E5 [recovery] an abruptly ended unsaved document leaves a readable draft",
                    found!=null&&records.Count>0,
                    "records="+records.Count+" found="+(found!=null)+" chars="+(found==null?-1:found.Characters)+" expected="+expected.Length);
                string stored=null;
                if(found!=null)stored=RecoveryStore.Read(found.File).Text;
                Verify("E5 [recovery] the draft holds the exact unsaved buffer",stored==expected,"stored="+Show(stored)+" expected="+Show(expected));

                Step("E5 restore");
                if(found!=null){
                    restored=NewWindow();
                    restored.recoveryTimer.Stop();
                    System.Threading.Tasks.Task task=restored.RestoreRecovery(found);
                    for(int i=0;i<240&&!task.IsCompleted;i++)Pump(50);
                    Verify("E5 [recovery] the restart path restores the text into a tab",
                        task.IsCompleted&&!task.IsFaulted&&Doc(restored)==expected,
                        "completed="+task.IsCompleted+" faulted="+task.IsFaulted+" text="+Show(Doc(restored))+" error="+OneLine(task.IsFaulted?task.Exception.GetBaseException().Message:""));
                    bool unsaved=restored.HasUnsavedChanges;
                    string id=restored.activeDocument==null?null:restored.activeDocument.RecoveryId;
                    Verify("E5 [recovery] the restored tab is still unsaved and keeps its draft identity",
                        unsaved&&found!=null&&id==RecoveryStore.RecordId(found.File),
                        "unsaved="+unsaved+" id="+id+" recovered="+restored.recoveredDocument);
                }
                Note("E5 the draft is written synchronously only because a restart kills the process before a queued write runs (RecoveryStore.WriteSync); the 2 s timer is stopped here so the check is deterministic");
            }finally{
                CloseQuiet(restored);
                CloseQuiet(writer);
                try{RecoveryStore.ReleaseTestOwner();}catch(Exception){}
                RecoveryStore.TestRoot=previousRoot;
                try{
                    if(Directory.Exists(root)){
                        foreach(string file in Directory.GetFiles(root))File.Delete(file);
                        Directory.Delete(root);
                    }
                }catch(Exception){}
            }
        }

        // ---------------------------------------------------------------- E6 unicode paths
        // Space + Hangul + Japanese + Chinese (simplified and traditional) + emoji in folder and
        // file names, through open, in-place save, Save As and the recent list (which is the path
        // the user actually reopens from).
        static void ScenarioE6(){
            string folder=Path.Combine(outDir,"e6");Directory.CreateDirectory(folder);
            string korean=Path.Combine(folder,"테스트 폴더 😀");Directory.CreateDirectory(korean);
            string japanese=Path.Combine(folder,"日本語 폴더");Directory.CreateDirectory(japanese);
            string traditional=Path.Combine(folder,"繁體 中文 폴더");Directory.CreateDirectory(traditional);
            string file=Path.Combine(korean,"文件 名.md");
            string body="# 유니코드 경로\n\n본문 😀\n";
            File.WriteAllText(file,body,new UTF8Encoding(false));
            string second=Path.Combine(japanese,"文章 名前.md");
            File.WriteAllText(second,"둘째 문서\n",new UTF8Encoding(false));
            MainWindow window=null,reopened=null;
            try{
                Step("E6 open");
                window=NewWindow();
                window.LoadFile(file);Pump(400);
                string loaded=Doc(window);
                Verify("E6 [open] a path with spaces, Hangul, Japanese, Chinese and an emoji opens",
                    loaded==body&&String.Equals(window.path,Path.GetFullPath(file),StringComparison.OrdinalIgnoreCase),
                    "path="+Show(window.path)+" textEqual="+(loaded==body)+" exists="+File.Exists(file));

                Step("E6 second tab");
                window.LoadFile(second);Pump(400);
                EnterEdit(window);
                Verify("E6 [open] a second unicode path opens as another tab",
                    window.OpenDocumentCount==2&&Doc(window)=="둘째 문서\n"&&String.Equals(window.path,Path.GetFullPath(second),StringComparison.OrdinalIgnoreCase),
                    "documents="+window.OpenDocumentCount+" path="+Show(window.path)+" text="+Show(Doc(window)));

                Step("E6 save");
                window.source.Select(window.source.Document.TextLength,0);
                Type(window,"추가 😀");Pump(150);
                string edited=Doc(window);
                bool saved=false;Exception saveError=null;
                try{saved=window.Save(false);}catch(Exception error){saveError=error;}
                Verify("E6 [save] in-place save writes the unicode path",
                    saveError==null&&saved&&File.Exists(second)&&File.ReadAllText(second,new UTF8Encoding(false))==edited&&
                    edited.IndexOf("추가 😀",StringComparison.Ordinal)>=0,
                    "error="+OneLine(saveError==null?"":saveError.Message)+" saved="+saved+" disk="+Show(File.Exists(second)?File.ReadAllText(second):null));

                Step("E6 save as");
                string target=Path.Combine(traditional,"새 文件 😀.md");
                string pickedTarget=target;
                bool savedAs=false;Exception asError=null;
                SaveTargetPicker=delegate(string name){return pickedTarget;};
                try{savedAs=window.Save(true);}catch(Exception error){asError=error;}
                finally{SaveTargetPicker=null;}
                Verify("E6 [save as] a unicode target path is written and becomes the document path",
                    asError==null&&savedAs&&File.Exists(target)&&
                    File.ReadAllText(target,new UTF8Encoding(false))==edited&&
                    String.Equals(window.path,Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase),
                    "error="+OneLine(asError==null?"":asError.Message)+" saved="+savedAs+" path="+Show(window.path)+
                    " disk="+Show(File.Exists(target)?File.ReadAllText(target):null));

                Step("E6 recent");
                string fullTarget=Path.GetFullPath(target);
                bool recentHas=false;
                foreach(string entry in window.recentFiles)if(String.Equals(entry,fullTarget,StringComparison.OrdinalIgnoreCase))recentHas=true;
                MenuItem recentItem=null;
                if(window.recentMenu!=null){
                    foreach(object entry in window.recentMenu.Items){
                        var item=entry as MenuItem;
                        if(item!=null&&String.Equals(item.ToolTip as string,fullTarget,StringComparison.OrdinalIgnoreCase))recentItem=item;
                    }
                }
                Verify("E6 [recent] the unicode path is listed in 최근 파일",recentHas&&recentItem!=null,
                    "inList="+recentHas+" item="+(recentItem!=null)+" count="+window.recentFiles.Count);

                if(recentItem!=null){
                    // The menu item belongs to this window and its handler is LoadFile, so the click
                    // is raised on the same window. Switch to the first document first, otherwise
                    // LoadFile would find the target already active and prove nothing.
                    window.LoadFile(file);Pump(400);
                    bool onOther=!String.Equals(window.path,fullTarget,StringComparison.OrdinalIgnoreCase);
                    recentItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    Pump(500);
                    Verify("E6 [recent] clicking the recent entry switches to the unicode document",
                        onOther&&String.Equals(window.path,fullTarget,StringComparison.OrdinalIgnoreCase)&&Doc(window)==edited,
                        "onOther="+onOther+" path="+Show(window.path)+" text="+Show(Doc(window))+" expected="+Show(edited));
                }
                Note("E6 the recent list is in-memory in a self-test run (SavePreferences returns before writing), so the user's real preferences file is never touched");
            }finally{
                CloseQuiet(reopened);
                CloseQuiet(window);
            }
        }

        // ---------------------------------------------------------------- E7 line endings
        // The app's rule (Workspace.DetectNewline): a loaded file's newline is chosen by counts
        // (CRLF when CRLF outnumbers LF-only endings, LF when those outnumber CRLF, CR only when
        // the text has no LF at all); the buffer is normalized to it and every save writes that
        // one newline. A file whose bytes are still
        // exactly the loaded ones is written back from the original bytes, so an untouched mixed
        // file keeps its bytes; once a line is edited the save normalizes the whole file.
        static void ScenarioE7(){
            string folder=Path.Combine(outDir,"e7");Directory.CreateDirectory(folder);
            MainWindow window=null;
            try{
                window=NewWindow();
                LineEndingCase(window,folder,"crlf.md","a\r\nb\r\nc\r\n","\r\n");
                LineEndingCase(window,folder,"lf.md","a\nb\nc\n","\n");
                LineEndingCase(window,folder,"cr.md","a\rb\rc\r","\r");
                LineEndingCase(window,folder,"mixed.md","a\r\nb\nc\r\n","\r\n");

                Step("E7 untouched mixed");
                string untouched=Path.Combine(folder,"mixed-untouched.md");
                byte[] original=Encoding.UTF8.GetBytes("a\r\nb\nc\r\n");
                File.WriteAllBytes(untouched,original);
                window.LoadFile(untouched);Pump(350);
                bool savedUntouched=false;Exception untouchedError=null;
                try{savedUntouched=window.Save(false);}catch(Exception error){untouchedError=error;}
                Pump(120);
                byte[] after=File.Exists(untouched)?File.ReadAllBytes(untouched):null;
                Verify("E7 [mixed, no edit] an untouched file is written back byte-for-byte",
                    untouchedError==null&&savedUntouched&&after!=null&&ByteHash(after)==ByteHash(original),
                    "error="+OneLine(untouchedError==null?"":untouchedError.Message)+" saved="+savedUntouched+" same="+(after!=null&&ByteHash(after)==ByteHash(original)));

                Step("E7 tabs");
                string tabs=Path.Combine(folder,"tabs.md");
                string tabsBody="탭\t구분\t값\n둘째\t줄\n";
                File.WriteAllText(tabs,tabsBody,new UTF8Encoding(false));
                window.LoadFile(tabs);Pump(350);
                EnterEdit(window);
                var tabLine=window.source.Document.GetLineByNumber(2);
                window.source.Select(tabLine.EndOffset,0);Pump(40);
                Type(window,"\t끝");Pump(120);
                string expectedTabs=Doc(window);
                bool tabSaved=false;Exception tabError=null;
                try{tabSaved=window.Save(false);}catch(Exception error){tabError=error;}
                string tabDisk=File.Exists(tabs)?File.ReadAllText(tabs,new UTF8Encoding(false)):null;
                Verify("E7 [tabs] tab characters are preserved through open, edit and save",
                    tabError==null&&tabSaved&&tabDisk==expectedTabs&&
                    CountOf(tabDisk,'\t')==CountOf(tabsBody,'\t')+1,
                    "error="+OneLine(tabError==null?"":tabError.Message)+" tabs="+CountOf(tabDisk,'\t')+" disk="+Show(tabDisk));

                Step("E7 long line open");
                string longFile=Path.Combine(folder,"longline.md");
                var builder=new StringBuilder(50000+16);
                builder.Append('a',50000).Append("\r\n끝\r\n");
                File.WriteAllText(longFile,builder.ToString(),new UTF8Encoding(false));
                int openedErrors=dispatcherErrors;
                var openWatch=Stopwatch.StartNew();
                window.LoadFile(longFile);Pump(400);
                openWatch.Stop();
                Step("E7 long line edit");
                EnterEdit(window);
                int firstLineLength=window.source.Document.GetLineByNumber(1).Length;
                int lineEnd=window.source.Document.GetLineByNumber(1).EndOffset;
                Step("E7 long line End");
                window.source.Select(0,0);
                // End follows the visible (wrapped) line: it must move off the start and stay
                // inside the line, and it must answer quickly on a 50,000-char line.
                var endWatch=Stopwatch.StartNew();
                MoveLineEnd(window);Pump(60);
                endWatch.Stop();
                int endCaret=window.source.CaretIndex;
                bool reachedEnd=endCaret>0&&endCaret<=lineEnd;
                Step("E7 long line caret end");
                window.source.Select(lineEnd,0);Pump(60);
                int caretAtEnd=window.source.CaretIndex;
                Step("E7 long line type");
                var inputTimes=new List<double>();
                var echoTimes=new List<double>();
                for(int i=0;i<10;i++){
                    var inputWatch=Stopwatch.StartNew();
                    Type(window,"x");
                    inputWatch.Stop();
                    inputTimes.Add(inputWatch.Elapsed.TotalMilliseconds);
                    var echoWatch=Stopwatch.StartNew();
                    Pump();
                    echoWatch.Stop();
                    echoTimes.Add(echoWatch.Elapsed.TotalMilliseconds);
                }
                double inputMax=0,inputSum=0,echoMax=0,echoSum=0;
                foreach(double value in inputTimes){inputSum+=value;if(value>inputMax)inputMax=value;}
                foreach(double value in echoTimes){echoSum+=value;if(value>echoMax)echoMax=value;}
                string firstLine=window.source.Document.GetText(window.source.Document.GetLineByNumber(1));
                bool typedAtEnd=firstLine.Length==50010&&firstLine.EndsWith("xxxxxxxxxx",StringComparison.Ordinal);
                Verify("E7 [long line] a 50,000-char line opens and End answers without hanging",
                    firstLineLength==50000&&reachedEnd&&endWatch.Elapsed.TotalMilliseconds<200&&dispatcherErrors==openedErrors,
                    "openMs="+N(openWatch.Elapsed.TotalMilliseconds)+" line1="+firstLineLength+" endCaret="+endCaret+
                    " lineEnd="+lineEnd+" endMs="+N(endWatch.Elapsed.TotalMilliseconds)+" dispatcherErrors="+(dispatcherErrors-openedErrors)+
                    (dispatcherErrors==openedErrors?"":" last="+OneLine(lastDispatcherError)));
                Verify("E7 [long line] typing at the end of the line stays under 200 ms per key",
                    inputMax<200&&echoMax<200&&typedAtEnd&&caretAtEnd==lineEnd,
                    "input max="+N(inputMax)+" avg="+N(inputSum/10)+" echo max="+N(echoMax)+" avg="+N(echoSum/10)+
                    " firstLine="+firstLine.Length+" typedAtEnd="+typedAtEnd+" caretAtEnd="+caretAtEnd+" lineEnd="+lineEnd);
                Note("E7 long-line timings are 10 keys on a hidden desktop: per-key input max "+N(inputMax)+" ms, input+idle max "+N(echoMax)+" ms; one run only");
                Note("E7 the rule the checks above enforce: mixed newlines are normalized to the detected one on the first edited save ("+Delimiters(File.ReadAllText(Path.Combine(folder,"mixed.md"),Encoding.UTF8))+"); pure files keep theirs");
                Note("E7 closing a window whose caret sits on a >16,384-char line: MainWindow.ReleaseTabSurfaces (Workspace.cs:66) replaces the editor's TextDocument; the caret move inside TextArea.OnDocumentChanged reaches LongLineWindow.Session.CaretChanged (LongLineWindow.cs:634) before Session.TrackDocument (LongLineWindow.cs:452) sees the swap, so Rebuild lays out a DocumentLine of the old document and AvalonEdit throws InvalidOperationException 'Line belongs to wrong document'");
            }finally{
                int closeErrors=dispatcherErrors;
                string closeThrow=null;
                try{MarkSaved(window);window.Close();}
                catch(Exception error){closeThrow=error.GetType().Name+": "+error.Message;}
                Verify("E7 [close] closing a window that holds a 50,000-char line raises no unhandled dispatcher exception",
                    dispatcherErrors==closeErrors&&closeThrow==null,
                    "new="+(dispatcherErrors-closeErrors)+" throw="+(closeThrow==null?"none":closeThrow)+
                    (dispatcherErrors==closeErrors?"":" last="+OneLine(lastDispatcherError)));
            }
        }
        static void LineEndingCase(MainWindow window,string folder,string name,string body,string expectedNewline){
            Step("E7 "+name);
            string file=Path.Combine(folder,name);
            File.WriteAllText(file,body,new UTF8Encoding(false));
            window.LoadFile(file);Pump(350);
            EnterEdit(window);
            string rule=window.documentNewline;
            var line=window.source.Document.GetLineByNumber(2);
            window.source.Select(line.EndOffset,0);Pump(40);
            Type(window,"X");Pump(120);
            bool saved=false;Exception error=null;
            try{saved=window.Save(false);}catch(Exception exception){error=exception;}
            Pump(150);
            string disk=File.Exists(file)?File.ReadAllText(file,new UTF8Encoding(false)):null;
            bool consistent=OnlyDelimiter(disk,expectedNewline);
            bool editedLine=disk!=null&&disk.Replace("\r\n","\n").Replace("\r","\n").IndexOf("bX",StringComparison.Ordinal)>=0;
            Verify("E7 ["+name+"] one newline kind is written consistently and the edit survives",
                error==null&&saved&&rule==expectedNewline&&consistent&&editedLine,
                "error="+OneLine(error==null?"":error.Message)+" saved="+saved+" rule="+Show(rule)+" expected="+Show(expectedNewline)+
                " delimiters="+Delimiters(disk)+" text="+Show(disk));
        }
        static string Delimiters(string text){
            int crlf=0,lf=0,cr=0;
            if(text!=null){
                for(int i=0;i<text.Length;i++){
                    if(text[i]=='\r'){
                        if(i+1<text.Length&&text[i+1]=='\n'){crlf++;i++;}
                        else cr++;
                    }else if(text[i]=='\n')lf++;
                }
            }
            return "crlf="+crlf+",lf="+lf+",cr="+cr;
        }
        static bool OnlyDelimiter(string text,string newline){
            int crlf=0,lf=0,cr=0;
            if(text!=null){
                for(int i=0;i<text.Length;i++){
                    if(text[i]=='\r'){
                        if(i+1<text.Length&&text[i+1]=='\n'){crlf++;i++;}
                        else cr++;
                    }else if(text[i]=='\n')lf++;
                }
            }
            if(newline=="\r\n")return lf==0&&cr==0&&crlf>0;
            if(newline=="\n")return cr==0&&crlf==0&&lf>0;
            if(newline=="\r")return crlf==0&&lf==0&&cr>0;
            return false;
        }

        // ---------------------------------------------------------------- E10 single instance
        // The Explorer double-click entry is DocumentActivation.Run: a second process loses the
        // named mutex, forwards its paths over the pipe and exits; the first window receives them
        // through the same DispatchFiles the real listener calls. The activation test entries are
        // that exact client/server code with a private scope, so a real user instance is neither
        // needed nor disturbed. Child processes run on the same hidden desktop.
        static void ScenarioE10(){
            string folder=Path.Combine(outDir,"e10");Directory.CreateDirectory(folder);
            string first=Path.Combine(folder,"first.md"),second=Path.Combine(folder,"second.md");
            File.WriteAllText(first,"첫 파일\n",new UTF8Encoding(false));
            File.WriteAllText(second,"둘째 파일\n",new UTF8Encoding(false));
            string exe=Process.GetCurrentProcess().MainModule.FileName;
            string scope="scenario-"+Guid.NewGuid().ToString("N");
            Process server=null,client=null;
            try{
                Step("E10 server");
                var serverInfo=new ProcessStartInfo(exe,"--activation-server "+scope+" \""+folder+"\" tabs");
                serverInfo.UseShellExecute=false;
                serverInfo.WorkingDirectory=Path.GetDirectoryName(exe);
                server=Process.Start(serverInfo);
                string ready=Path.Combine(folder,"ready.txt");
                string result=Path.Combine(folder,"result.txt");
                bool isReady=WaitForFile(ready,30000);
                Verify("E10 [activation] the first instance starts, loads the first document and listens",
                    isReady&&server!=null&&!server.HasExited,
                    "ready="+isReady+" server="+(server!=null)+" exited="+(server==null||server.HasExited)+" exe="+Show(exe));

                Step("E10 client");
                var clientInfo=new ProcessStartInfo(exe,"--activation-client "+scope+" \""+second+"\"");
                clientInfo.UseShellExecute=false;
                clientInfo.WorkingDirectory=Path.GetDirectoryName(exe);
                client=Process.Start(clientInfo);
                bool clientExited=client!=null&&client.WaitForExit(20000);
                int clientCode=clientExited?client.ExitCode:-1;
                bool got=WaitForFile(result,30000);
                string text=got?File.ReadAllText(result,Encoding.UTF8):null;
                bool serverExited=server!=null&&server.WaitForExit(15000);
                bool oneWindow=text!=null&&text.IndexOf("Windows=1",StringComparison.Ordinal)>=0;
                bool twoTabs=text!=null&&text.IndexOf("Tabs=2",StringComparison.Ordinal)>=0;
                Verify("E10 [activation] a second open forwards into the running window and no second instance stays",
                    clientExited&&clientCode==0&&got&&oneWindow&&twoTabs&&serverExited,
                    "clientExit="+clientExited+" code="+clientCode+" result="+Show(text)+" serverExited="+serverExited+
                    " windowsOne="+oneWindow+" tabsTwo="+twoTabs);
                Note("E10 the real Explorer path is DocumentActivation.Run(args); the test entries run the same mutex/pipe/forward/DispatchFiles code with a private scope, so this checks the mechanism, not the shell registration");
            }finally{
                try{if(client!=null&&!client.HasExited)client.Kill();}catch(Exception){}
                try{if(server!=null&&!server.HasExited)server.Kill();}catch(Exception){}
            }
        }
        static bool WaitForFile(string path,int milliseconds){
            long start=Environment.TickCount;
            while(unchecked(Environment.TickCount-start)<milliseconds){
                if(File.Exists(path)){
                    // The writer may still be flushing; a readable non-empty file is enough.
                    try{if(new FileInfo(path).Length>0)return true;}catch(IOException){}
                }
                Thread.Sleep(100);
            }
            return File.Exists(path);
        }

        // ---------------------------------------------------------------- E11 unsaved close
        // Three dirty tabs, answered through the queuedSaveChoices seam (the prompt itself never
        // opens on a hidden desktop): Save writes the first to a temp file, Discard drops the
        // second, Cancel keeps the window open with every tab still there.
        static void ScenarioE11(){
            string folder=Path.Combine(outDir,"e11");Directory.CreateDirectory(folder);
            string fileA=Path.Combine(folder,"tab-a.md");
            string fileB=Path.Combine(folder,"tab-b.md");
            string fileC=Path.Combine(folder,"tab-c.md");
            RemoveFile(fileA);
            File.WriteAllText(fileB,"B original\n",new UTF8Encoding(false));
            File.WriteAllText(fileC,"C original\n",new UTF8Encoding(false));
            MainWindow window=null;
            try{
                window=NewWindow();
                // tab 1: a new, unsaved document
                window.path="";window.saved="";window.textMode=false;window.documentNewline="\n";
                window.documentFingerprint=null;window.documentFingerprintPath=null;window.recoveredDocument=false;
                window.SetText("A pending");window.SetMode(true);Pump(60);
                window.CreateTab();
                // tab 2: dirty against an existing file
                window.path=fileB;window.saved="B original\n";window.textMode=false;window.documentNewline="\n";
                window.documentFingerprint=null;window.documentFingerprintPath=null;window.recoveredDocument=false;
                window.SetText("B pending");window.SetMode(true);Pump(60);
                window.CreateTab();
                // tab 3: dirty against an existing file
                window.path=fileC;window.saved="C original\n";window.textMode=false;window.documentNewline="\n";
                window.documentFingerprint=null;window.documentFingerprintPath=null;window.recoveredDocument=false;
                window.SetText("C pending");window.SetMode(true);Pump(60);
                window.CaptureTab();
                Step("E11 close");
                int before=window.documents.Count;
                window.queuedSaveChoices.Enqueue(SaveChoice.Save);
                window.queuedSaveChoices.Enqueue(SaveChoice.Discard);
                window.queuedSaveChoices.Enqueue(SaveChoice.Cancel);
                string pickedA=fileA;
                SaveTargetPicker=delegate(string name){return pickedA;};
                Exception closeError=null;
                try{window.Close();}catch(Exception error){closeError=error;}
                finally{SaveTargetPicker=null;}
                Pump(250);
                bool stayed=window.IsLoaded;
                bool answered=window.queuedSaveChoices.Count==0;
                bool aSaved=File.Exists(fileA)&&File.ReadAllText(fileA,new UTF8Encoding(false))=="A pending";
                bool bUntouched=File.ReadAllText(fileB,new UTF8Encoding(false))=="B original\n";
                bool cUntouched=File.ReadAllText(fileC,new UTF8Encoding(false))=="C original\n";
                string cText=window.documents.Count>2?window.documents[2].Text:"<missing>";
                Verify("E11 [close] Save/Discard/Cancel are asked per dirty tab and Cancel keeps the window",
                    closeError==null&&answered&&before==3&&stayed&&aSaved&&bUntouched&&cUntouched&&
                    window.documents.Count==3&&cText=="C pending",
                    "error="+OneLine(closeError==null?"":closeError.Message)+" answered="+answered+" documents="+window.documents.Count+
                    " stayed="+stayed+" aSaved="+aSaved+" bUntouched="+bUntouched+" cUntouched="+cUntouched+" cText="+Show(cText));

                Step("E11 cleanup");
                if(window.IsLoaded){
                    for(int i=0;i<3;i++)window.queuedSaveChoices.Enqueue(SaveChoice.Discard);
                    window.Close();Pump(200);
                }
                Verify("E11 [cleanup] discarding the remaining tabs closes the window",!window.IsLoaded,"loaded="+window.IsLoaded);
            }finally{CloseQuiet(window);}
        }

        // SCENARIO-APPEND
    }
}
}
