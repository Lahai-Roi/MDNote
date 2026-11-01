using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
namespace MDNote {
internal sealed class DocumentActivation:IDisposable {
    internal static MainWindow ActiveWindow;
    readonly string pipeName;readonly Mutex mutex;readonly bool primary;volatile bool stopping;NamedPipeServerStream server;
    internal DocumentActivation(string testScope){string identity=Environment.UserDomainName+"\\"+Environment.UserName+"|"+AppDomain.CurrentDomain.BaseDirectory.ToUpperInvariant()+"|"+testScope;using(var hash=SHA256.Create())pipeName="MDNote-"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(identity))).Replace("-","").Substring(0,32);mutex=new Mutex(true,"Local\\"+pipeName,out primary);}
    // A path the launching shell could not keep intact (an empty argument, "|" or "<" in the name,
    // a path past the file system limit) is skipped: Path.GetFullPath throws ArgumentException and
    // friends for it, and an uncaught throw here killed the second process before it reached the
    // first one, with no log (the old catch saw only IOException and TimeoutException). An argument
    // list that held nothing else has nothing to forward, so the launcher exits quietly instead of
    // asking the running window for a new document.
    internal bool Forward(string[] files){var paths=new System.Collections.Generic.List<string>();if(files!=null)foreach(string file in files){try{paths.Add(Path.GetFullPath(file));}catch(PathTooLongException){}catch(IOException){}catch(ArgumentException){}catch(NotSupportedException){}catch(System.Security.SecurityException){}}if(files!=null&&files.Length>0&&paths.Count==0)return true;try{using(var pipe=new NamedPipeClientStream(".",pipeName,PipeDirection.Out)){pipe.Connect(5000);using(var writer=new BinaryWriter(pipe,Encoding.UTF8)){writer.Write(paths.Count);foreach(string path in paths)writer.Write(path);writer.Flush();}}return true;}catch(IOException){return false;}catch(TimeoutException){return false;}}
    internal void Listen(Application app,Action<string[]> received){var thread=new Thread(delegate(){while(!stopping){try{using(var pipe=new NamedPipeServerStream(pipeName,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous)){server=pipe;pipe.WaitForConnection();var files=new System.Collections.Generic.List<string>();using(var reader=new BinaryReader(pipe,Encoding.UTF8)){int count=reader.ReadInt32();if(count<0||count>1000)continue;for(int i=0;i<count;i++){string file=reader.ReadString();if(file.Length>32767)throw new IOException("Path too long");files.Add(file);}}app.Dispatcher.BeginInvoke(new Action(delegate{received(files.ToArray());}));}}catch(IOException){}catch(ObjectDisposedException){if(stopping)return;}catch(System.Threading.ThreadAbortException){return;}}});thread.IsBackground=true;thread.Name="MDNote file activation";thread.Start();}
    internal static int Run(string[] args){InstallCrashLog(null);using(var activation=new DocumentActivation("")){if(!activation.primary){MainWindow.AllowSetForegroundWindow(-1);if(activation.Forward(args))return 0;MessageBox.Show(MainWindow.TranslatedText(MainWindow.SavedLanguage(),"MDNote 창으로 파일을 전달하지 못했습니다. 잠시 후 다시 열어 주세요."),"MDNote");return 1;}var app=new Application();InstallCrashLog(app);/* WPF ignores Closing's Cancel during shutdown, so logoff/restart writes recovery drafts here, before the process ends. */app.SessionEnding+=delegate(object sender,SessionEndingCancelEventArgs e){MainWindow.FlushAllForSessionEnd();};/* A cancelled session end returns activation to the app; clear the flush state there (see ResetSessionEndState). */app.Activated+=delegate{MainWindow.ResetSessionEndState();};activation.Listen(app,delegate(string[] files){DispatchFiles(app,files);});var window=new MainWindow();foreach(string file in args)window.LoadFile(file);app.Run(window);return 0;}}
    // System.Environment.FailFast (the text-store consistency check, stack overflow, an unwind
    // that cannot continue) never raises either handler, so a crash that only shows up in the
    // Windows event log leaves nothing behind. A plain exception does: log the stack, then let the
    // process terminate as before (e.Handled stays false, so nothing is swallowed). The file holds
    // an exception ToString only, never document text. Capped at 1 MB by dropping the oldest half.
    // The dispatcher handler is attached to the Application before Run, which is the only place a
    // dispatcher exception can be caught; AppDomain covers the rest and non-UI threads.
    // The AppDomain handler needs no Application, so Run installs it before the primary/secondary
    // split: a second instance that crashes while forwarding its arguments now leaves a log line
    // instead of dying silently. The dispatcher handler needs the Application (and can only be
    // attached before Run), so it keeps its own one-shot guard and the early call does not
    // consume it. Writing the log early is safe: AppendCrashLog swallows every failure, the file
    // lives under %LOCALAPPDATA%\MDNote, and the only other writer is the primary instance, whose
    // interleaved append or rotate also ends inside that try/catch.
    static int CrashLogInstalled,DispatcherCrashLogInstalled;
    internal static void InstallCrashLog(Application app){
        if(System.Threading.Interlocked.Exchange(ref CrashLogInstalled,1)==0)AppDomain.CurrentDomain.UnhandledException+=delegate(object sender,UnhandledExceptionEventArgs e){AppendCrashLog(e.ExceptionObject as Exception,false);};
        if(app!=null&&System.Threading.Interlocked.Exchange(ref DispatcherCrashLogInstalled,1)==0)app.DispatcherUnhandledException+=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e){AppendCrashLog(e.Exception,true);e.Handled=false;};
    }
    internal static string CrashLogPath{get{return Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MDNote"),"crash-log.txt");}}
    internal static void AppendCrashLog(Exception error,bool dispatcher){
        try{
            string path=CrashLogPath;string folder=Path.GetDirectoryName(path);if(!String.IsNullOrEmpty(folder))Directory.CreateDirectory(folder);
            var entry=new StringBuilder();
            entry.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss",System.Globalization.CultureInfo.InvariantCulture)).Append(dispatcher?"] dispatcher unhandled exception\r\n":"] unhandled exception\r\n");
            entry.Append(error==null?"(no Exception object: FailFast or a fatal runtime error)":error.ToString());
            entry.Append("\r\n\r\n");
            RotateCrashLog(path);
            File.AppendAllText(path,entry.ToString(),new UTF8Encoding(false));
        }catch(Exception){}/* a logging failure must never change how the crash ends */
    }
    // Keep the newest half when the file passes 1 MB, so the log grows by one crash at a time
    // instead of being deleted between two runs of the same bug.
    const int CrashLogLimit=1024*1024;
    static void RotateCrashLog(string path){
        try{
            var info=new FileInfo(path);if(!info.Exists||info.Length<CrashLogLimit)return;
            string existing=File.ReadAllText(path,Encoding.UTF8);
            int keep=existing.Length/2;
            int at=existing.IndexOf("\r\n\r\n",keep,StringComparison.Ordinal);
            if(at>=0)keep=at+4;
            File.WriteAllText(path,existing.Substring(Math.Min(keep,existing.Length)),new UTF8Encoding(false));
        }catch(Exception){}
    }
    static void DispatchFiles(Application app,string[] files){var target=ActiveWindow;if(target==null||!target.CanTakeForwardedFile){target=null;foreach(Window candidate in app.Windows){var editor=candidate as MainWindow;if(editor!=null&&editor.CanTakeForwardedFile){target=editor;break;}}}if(target==null||files.Length==0){target=new MainWindow();target.Show();}MainWindow foreground=target;foreach(string file in files){/* An Explorer double-click or a second instance hands the path here. A file another window already holds is brought forward there with its tab selected; opening it in this window as well left two windows editing one document until the later save hit the conflict prompt. */var owner=MainWindow.WindowWithFile(app,file);if(owner!=null&&owner.ActivateOpenedFile(file)){foreground=owner;continue;}target.LoadFile(file);foreground=target;}foreground.EnsureVisibleForeground();}
    internal static int ActivationTest(string[] args){Program.IsSelfTest=true;string scope=args[1];using(var activation=new DocumentActivation(scope)){if(args[0]=="--activation-client")return activation.Forward(new[]{args[2]})?0:1;if(!activation.primary)return 2;string folder=args[2];bool tabs=args[3]=="tabs";var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};var window=new MainWindow();window.ConfigureActivationTest(tabs);window.LoadFile(Path.Combine(folder,"first.md"));var deadline=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(15)};deadline.Tick+=delegate{File.WriteAllText(Path.Combine(folder,"result.txt"),"ERROR: timeout");app.Shutdown(3);};deadline.Start();activation.Listen(app,delegate(string[] files){DispatchFiles(app,files);app.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,new Action(delegate{int totalTabs=0;foreach(Window item in app.Windows){var editor=item as MainWindow;if(editor!=null)totalTabs+=editor.OpenDocumentCount;}File.WriteAllText(Path.Combine(folder,"result.txt"),"Windows="+app.Windows.Count+"\nTabs="+totalTabs);deadline.Stop();app.Shutdown();}));});window.Loaded+=delegate{File.WriteAllText(Path.Combine(folder,"ready.txt"),"ready");};return app.Run(window);}}
    public void Dispose(){stopping=true;var pipe=server;if(pipe!=null)pipe.Dispose();if(primary)mutex.ReleaseMutex();mutex.Dispose();}
}
}
