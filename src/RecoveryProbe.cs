using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
namespace MDNote {
public partial class MainWindow {
    internal static int VerifyRecoveryCleanup(string outDir){return RecoveryProbe.Run(outDir);}
}
// --recovery-probe <outDir>: generated drafts only. Points the store at a temporary root, ages
// drafts past seven days, and checks PurgeOlderThan never removes a draft that holds unsaved text
// or that names an original file (a postponed draft is offered again at every start); only a
// provably empty new-document draft may expire, and a live-owned draft is kept. The window half
// checks that an open which fails (an explicit encoding that cannot decode the file, or a missing
// file) leaves the document, its tabs and its undo history exactly as they were.
internal static class RecoveryProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    internal static int Run(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string passFile=Path.Combine(outDir,"recovery-probe-PASS.txt"),failFile=Path.Combine(outDir,"recovery-probe-FAIL.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        var report=new StringBuilder();bool all=true;
        Action<string,bool,string> check=delegate(string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n');};
        string root=Path.Combine(outDir,"recovery-"+Guid.NewGuid().ToString("N"));
        string oldRoot=RecoveryStore.TestRoot;
        try{
            Directory.CreateDirectory(root);
            RecoveryStore.TestRoot=root;
            var record=new RecoveryRecord {Name="generated.md",OriginalPath="",PlainText=false,CodePage=65001,Newline="\n"};
            string oldId=Guid.NewGuid().ToString("N"),recentId=Guid.NewGuid().ToString("N"),emptyId=Guid.NewGuid().ToString("N"),clearedId=Guid.NewGuid().ToString("N");
            WriteDraft(oldId,record,"generated old draft");
            WriteDraft(recentId,record,"generated recent draft");
            WriteDraft(emptyId,record,"");
            /* An empty draft that names an original file may be the user's unsaved "cleared the
               file" state, so age must not take it either. */
            var clearedRecord=new RecoveryRecord {Name="generated-original.md",OriginalPath=Path.Combine(outDir,"generated-cleared.md"),PlainText=false,CodePage=65001,Newline="\n"};
            WriteDraft(clearedId,clearedRecord,"");
            RecoveryStore.ReleaseTestOwner();
            string oldFile=DraftFile(oldId),recentFile=DraftFile(recentId),emptyFile=DraftFile(emptyId),clearedFile=DraftFile(clearedId);
            check("generated drafts written",File.Exists(oldFile)&&File.Exists(recentFile)&&File.Exists(emptyFile)&&File.Exists(clearedFile),"old="+File.Exists(oldFile)+", recent="+File.Exists(recentFile)+", emptyNew="+File.Exists(emptyFile)+", emptyOriginal="+File.Exists(clearedFile));
            File.SetLastWriteTimeUtc(oldFile,DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(recentFile,DateTime.UtcNow.AddDays(-1));
            File.SetLastWriteTimeUtc(emptyFile,DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(clearedFile,DateTime.UtcNow.AddDays(-8));
            int removed=RecoveryStore.PurgeOlderThan(TimeSpan.FromDays(7));
            /* Unsaved text never expires: a draft deferred with "나중에" is offered at every start
               until the user restores it or deletes it in the recovery window. Only a provably empty
               new-document draft (no text, no original file) is removed by age. */
            check("expires only the aged draft that is provably empty",removed==1&&File.Exists(oldFile)&&File.Exists(recentFile)&&!File.Exists(emptyFile)&&File.Exists(clearedFile),
                "removed="+removed+", oldText="+File.Exists(oldFile)+", recentText="+File.Exists(recentFile)+", emptyNew="+File.Exists(emptyFile)+", emptyOriginal="+File.Exists(clearedFile));
            bool offered=false;
            foreach(var item in RecoveryStore.List())if(RecoveryStore.RecordId(item.File)==oldId)offered=true;
            check("a postponed draft with unsaved text is still offered",offered,"a draft for "+oldId+" is listed after the purge");

            // An old draft whose owner editor still holds its lock must survive, even when empty.
            string liveId=Guid.NewGuid().ToString("N");
            WriteDraft(liveId,record,"");
            string liveFile=DraftFile(liveId);
            File.SetLastWriteTimeUtc(liveFile,DateTime.UtcNow.AddDays(-8));
            int kept=RecoveryStore.PurgeOlderThan(TimeSpan.FromDays(7));
            check("keeps a live-owned draft",kept==0&&File.Exists(liveFile),"removed="+kept+", live="+File.Exists(liveFile));
            int ignored=RecoveryStore.PurgeOlderThan(TimeSpan.Zero);
            check("non-positive age is ignored",ignored==0&&File.Exists(liveFile),"removed="+ignored);
            RecoveryStore.ReleaseTestOwner();
            int aged=RecoveryStore.PurgeOlderThan(TimeSpan.FromDays(7));
            check("released draft is purged",aged==1&&!File.Exists(liveFile)&&File.Exists(oldFile)&&File.Exists(clearedFile),"removed="+aged+", live="+File.Exists(liveFile)+", oldText="+File.Exists(oldFile)+", emptyOriginal="+File.Exists(clearedFile));
            VerifyRecoveredSaveAsStart(outDir,check);
            VerifyRestoredDraftStaysUnsaved(outDir,check);
            VerifySavedRestoredDraftIsGone(outDir,check);
            VerifyFailedOpenKeepsDocument(outDir,check);
        }catch(Exception ex){all=false;report.Append("EXCEPTION ").Append(ex).Append('\n');}
        finally{
            try{RecoveryStore.ReleaseTestOwner();}catch(Exception){}
            RecoveryStore.TestRoot=oldRoot;
            try{if(Directory.Exists(root)){foreach(string file in Directory.GetFiles(root))File.Delete(file);Directory.Delete(root);}}catch(IOException){}catch(UnauthorizedAccessException){}
        }
        File.WriteAllText(all?passFile:failFile,report.ToString(),new UTF8Encoding(false));
        return all?0:1;
    }
    // A restored draft keeps path="" (RC9Tests) and remembers its original file on the tab, so the
    // next Save As starts in that file's folder with its name instead of the last-used folder.
    static void VerifyRecoveredSaveAsStart(string outDir,Action<string,bool,string> check){
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        string id=Guid.NewGuid().ToString("N"),original=Path.Combine(outDir,"generated-original.md");
        var record=new RecoveryRecord {Name="generated-recovered.md",OriginalPath=original,PlainText=false,CodePage=65001,Newline="\n"};
        MainWindow window=null;
        try{
            WriteDraft(id,record,"recovered body");
            RecoveryStore.ReleaseTestOwner();
            RecoveryRecord listed=null;
            foreach(var item in RecoveryStore.List())if(RecoveryStore.RecordId(item.File)==id)listed=item;
            check("recovered draft is listed",listed!=null,listed!=null?"a draft for "+id:"no draft for "+id);
            if(listed==null)return;
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700,Left=-20000,Top=-20000};
            window.Show();Pump();
            /* The app restores a draft inside Application.Run (the startup prompt or the File >
               recover-documents menu item), which installs the dispatcher's SynchronizationContext,
               so RestoreRecovery's await resumes on the UI thread. The probe entry point runs before
               that context exists: without it the await resumed on the thread pool and
               CreateTab/CaptureTab read preview.IsChecked from the wrong thread (InvalidOperationException).
               ScenarioProbe and RC9Tests install the same context around this restart path. */
            var previousContext=System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try{
                var restore=(System.Threading.Tasks.Task)Call(window,"RestoreRecovery",listed);
                var watch=System.Diagnostics.Stopwatch.StartNew();
                while(!restore.IsCompleted&&watch.ElapsedMilliseconds<10000){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}
                restore.GetAwaiter().GetResult();
                object active=typeof(MainWindow).GetField("activeDocument",Members).GetValue(window);
                string kept=active==null?"":(string)active.GetType().GetField("RecoveryOriginalPath",Members).GetValue(active);
                string path=Field<string>(window,"path");
                bool recovered=Field<bool>(window,"recoveredDocument");
                string start=(string)Call(window,"SaveAsDefaultPath",path,kept,false,false);
                check("restored tab keeps path empty and its original file",
                    recovered&&path==""&&String.Equals(kept,original,StringComparison.OrdinalIgnoreCase)&&String.Equals(start,original,StringComparison.OrdinalIgnoreCase),
                    "expected path=[] original="+original+"; got path=["+path+"] recovered="+recovered+" original=["+kept+"] saveAs=["+start+"]");
            }finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}
        }finally{
            RecoveryStore.Remove(id);
            /* A restored tab counts as dirty by definition (recoveredDocument), so both that flag and
               saved are cleared before Close or the self-test close prompt would open a modal box. */
            if(window!=null){try{typeof(MainWindow).GetField("saved",Members).SetValue(window,SourceText(window));typeof(MainWindow).GetField("recoveredDocument",Members).SetValue(window,false);typeof(MainWindow).GetField("sourceSnapshotDirty",Members).SetValue(window,false);window.Close();}catch(Exception){}}
        }
    }
    /* A restored draft is unsaved until the user saves it or discards it at a prompt: the tab is
       dirty through its Recovered flag (an empty draft's text equals its empty save point, so the
       flag is the only thing that marks it), and that flag travels with the tab because CaptureTab
       copies it and SwitchTab reads it back. A review claimed an empty body, or a tab switch round
       trip, could make the tab clean and let ForgetRecovery delete the draft the user never saved.
       This pins every half of the answer: the state the restore leaves, the state after switching
       away and back, the draft still being on disk after a periodic capture, and the close prompt
       still being asked - with cancel keeping both the tab and its draft. */
    static void VerifyRestoredDraftStaysUnsaved(string outDir,Action<string,bool,string> check){
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        string[] bodies={"","recovered body"};
        foreach(string body in bodies){
            string tag=body.Length==0?"empty":"non-empty";
            string id=Guid.NewGuid().ToString("N");
            var record=new RecoveryRecord {Name="generated-restored-"+tag+".md",OriginalPath="",PlainText=false,CodePage=65001,Newline="\n"};
            MainWindow window=null;
            try{
                window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700,Left=-20000,Top=-20000};
                window.Show();Pump();
                /* The 2 s timer would run underneath the checks; the probe calls the same capture itself. */
                Field<DispatcherTimer>(window,"recoveryTimer").Stop();
                WriteDraft(id,record,body);
                RecoveryStore.ReleaseTestOwner();
                string draft=DraftFile(id);
                RecoveryRecord listed=null;
                foreach(var item in RecoveryStore.List())if(RecoveryStore.RecordId(item.File)==id)listed=item;
                check("restored "+tag+" draft is listed",listed!=null,listed!=null?"a draft for "+id:"no draft for "+id);
                if(listed==null)continue;
                var previousContext=System.Threading.SynchronizationContext.Current;
                System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                try{
                    var restore=(System.Threading.Tasks.Task)Call(window,"RestoreRecovery",listed);
                    var watch=System.Diagnostics.Stopwatch.StartNew();
                    while(!restore.IsCompleted&&watch.ElapsedMilliseconds<10000){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}
                    restore.GetAwaiter().GetResult();
                }finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}
                object restored=Field<object>(window,"activeDocument");
                check("the restored "+tag+" tab is unsaved right after the restore",
                    Unsaved(window)&&Field<bool>(window,"recoveredDocument")&&TabField<bool>(restored,"Recovered")&&TabField<string>(restored,"Text")==body&&TabField<string>(restored,"Saved")==""&&TabField<string>(restored,"RecoveryId")==id,
                    "dirty="+Unsaved(window)+", recovered="+TabField<bool>(restored,"Recovered")+", length="+TabField<string>(restored,"Text").Length+", savedLength="+TabField<string>(restored,"Saved").Length+", id="+TabField<string>(restored,"RecoveryId"));
                /* Reading a draft must not consume it: until the tab is saved (or the user
                   discards it) the file is the only second copy, so a crash before the next
                   periodic write still has something to offer on the restart. */
                check("restoring the "+tag+" draft leaves its file on disk until the tab is saved",File.Exists(draft),
                    "draft="+draft+" exists="+File.Exists(draft));
                /* The startup offer restores into a window that already holds a tab; copy that shape
                   and make the round trip the review named. */
                var documents=(System.Collections.IList)typeof(MainWindow).GetField("documents",Members).GetValue(window);
                object other=null;
                foreach(object tab in documents)if(!Object.ReferenceEquals(tab,restored))other=tab;
                if(other!=null){Call(window,"SwitchTab",other);Pump();Call(window,"SwitchTab",restored);Pump();}
                check("switching tabs and back keeps the restored "+tag+" tab unsaved",
                    other!=null&&Unsaved(window)&&Field<bool>(window,"recoveredDocument")&&TabField<bool>(restored,"Recovered")&&TabField<string>(restored,"Text")==body&&SourceText(window)==body,
                    "other="+(other!=null)+", dirty="+Unsaved(window)+", recovered="+TabField<bool>(restored,"Recovered")+", length="+TabField<string>(restored,"Text").Length+", source="+SourceText(window).Length);
                Call(window,"CaptureRecoveryDrafts");Pump();
                check("a periodic capture keeps the restored "+tag+" draft on disk",File.Exists(draft),
                    "draft="+draft+" exists="+File.Exists(draft)+", dirty="+Unsaved(window));
                /* Two answers are queued so an unexpected second prompt cannot open a modal box; the
                   count left proves exactly one tab was asked about. */
                var choices=(System.Collections.Generic.Queue<MainWindow.SaveChoice>)typeof(MainWindow).GetField("queuedSaveChoices",Members).GetValue(window);
                choices.Enqueue(MainWindow.SaveChoice.Cancel);choices.Enqueue(MainWindow.SaveChoice.Cancel);
                bool confirmed=(bool)Call(window,"ConfirmTabs");
                check("closing asks about the restored "+tag+" tab and cancel keeps its draft",
                    !confirmed&&choices.Count==1&&window.OpenDocumentCount==2&&File.Exists(draft),
                    "confirmed="+confirmed+", answersLeft="+choices.Count+", tabs="+window.OpenDocumentCount+", draft="+File.Exists(draft));
                /* The real close batch awaits ConfirmTabsAsync; with a cancel answer it completes
                   without a save, so the same prompt has to be asked there. */
                choices.Enqueue(MainWindow.SaveChoice.Cancel);
                bool confirmedAsync=false;
                var asyncContext=System.Threading.SynchronizationContext.Current;
                System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                try{
                    var confirm=(System.Threading.Tasks.Task<bool>)Call(window,"ConfirmTabsAsync");
                    var confirmWatch=System.Diagnostics.Stopwatch.StartNew();
                    while(!confirm.IsCompleted&&confirmWatch.ElapsedMilliseconds<10000){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}
                    if(!confirm.IsCompleted)throw new TimeoutException("probe: ConfirmTabsAsync did not finish");
                    confirmedAsync=confirm.GetAwaiter().GetResult();
                }finally{System.Threading.SynchronizationContext.SetSynchronizationContext(asyncContext);}
                check("the window close path asks about the restored "+tag+" tab and cancel keeps its draft",
                    !confirmedAsync&&choices.Count==1&&window.OpenDocumentCount==2&&File.Exists(draft),
                    "confirmed="+confirmedAsync+", answersLeft="+choices.Count+", tabs="+window.OpenDocumentCount+", draft="+File.Exists(draft));
            }finally{
                RecoveryStore.Remove(id);
                /* Clear the flag on the tab and in the window before Close or the self-test close
                   prompt would consume a queued answer and open a modal box. */
                if(window!=null){
                    try{
                        typeof(MainWindow).GetField("saved",Members).SetValue(window,SourceText(window));
                        typeof(MainWindow).GetField("recoveredDocument",Members).SetValue(window,false);
                        typeof(MainWindow).GetField("sourceSnapshotDirty",Members).SetValue(window,false);
                        var tabs=(System.Collections.IList)typeof(MainWindow).GetField("documents",Members).GetValue(window);
                        foreach(object tab in tabs){tab.GetType().GetField("Saved",Members).SetValue(tab,tab.GetType().GetField("Text",Members).GetValue(tab));tab.GetType().GetField("Recovered",Members).SetValue(tab,false);}
                        window.Close();
                    }catch(Exception){}
                }
            }
        }
    }
    /* The restart shape (G14): the draft on disk was written by the process that was killed, so its
       file name carries that process's owner id and the dead owner's .lock file is left beside it.
       Restoring it and saving the tab must end that draft for good: Remove only knows this process's
       owner id, so the killed owner's file can only go through DiscardGroup, and the store must stop
       listing it. RC9Tests.VerifyRecovery saves within one owner and never reaches that half. */
    static void VerifySavedRestoredDraftIsGone(string outDir,Action<string,bool,string> check){
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        string id=Guid.NewGuid().ToString("N");
        var record=new RecoveryRecord {Name="generated-crashed.md",OriginalPath="",PlainText=false,CodePage=65001,Newline="\n"};
        string body="unsaved line of a killed editor";
        string saved=Path.Combine(outDir,"generated-crashed-saved.md");
        MainWindow window=null;
        try{
            WriteDraft(id,record,body);
            /* Rename the generated draft under a foreign owner id and leave that owner's lock file,
               exactly as the killed process's Recovery folder looks on the next start. */
            string foreign=Guid.NewGuid().ToString("N");
            string crashed=Path.Combine(RecoveryStore.Root,foreign+"-"+id+".draft");
            string lockFile=Path.Combine(RecoveryStore.Root,foreign+".lock");
            File.Move(DraftFile(id),crashed);
            File.WriteAllBytes(lockFile,new byte[0]);
            RecoveryStore.ReleaseTestOwner();
            RecoveryRecord listed=null;
            foreach(var item in RecoveryStore.List())if(RecoveryStore.RecordId(item.File)==id)listed=item;
            check("a killed editor's draft is listed for recovery",listed!=null,listed!=null?"a draft for "+id:"no draft for "+id);
            if(listed==null)return;
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700,Left=-20000,Top=-20000};
            window.Show();Pump();
            Field<DispatcherTimer>(window,"recoveryTimer").Stop();
            var previousContext=System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try{
                var restore=(System.Threading.Tasks.Task)Call(window,"RestoreRecovery",listed);
                var watch=System.Diagnostics.Stopwatch.StartNew();
                while(!restore.IsCompleted&&watch.ElapsedMilliseconds<10000){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}
                restore.GetAwaiter().GetResult();
            }finally{System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}
            object restored=Field<object>(window,"activeDocument");
            check("the restored tab carries the killed editor's draft id",TabField<string>(restored,"RecoveryId")==id,"id="+TabField<string>(restored,"RecoveryId")+" expected="+id);
            if(File.Exists(saved))File.Delete(saved);
            bool wrote=(bool)Call(window,"SaveToPath",saved,new UTF8Encoding(false),false);
            check("saving the restored draft writes the new file",wrote&&File.Exists(saved)&&File.ReadAllText(saved)==body,
                "wrote="+wrote+", exists="+File.Exists(saved)+", length="+(File.Exists(saved)?File.ReadAllText(saved).Length:-1));
            check("saving the restored draft deletes the killed editor's draft file",!File.Exists(crashed),
                "draft="+crashed+" exists="+File.Exists(crashed));
            bool offered=Listed(id);
            check("a saved restored draft is no longer returned by List()",!offered,"listed="+offered);
            /* The 2 s capture runs after the save too: a saved tab must not put its draft back. */
            Call(window,"CaptureRecoveryDrafts");Pump();
            offered=Listed(id);
            check("a periodic capture after the save does not bring the draft back",!offered&&!File.Exists(crashed),
                "listed="+offered+", draft="+File.Exists(crashed));
        }finally{
            try{if(File.Exists(saved))File.Delete(saved);}catch(IOException){}catch(UnauthorizedAccessException){}
            RecoveryStore.Remove(id);
            /* Clear the flags the restored tab set or the self-test close prompt would open a box. */
            if(window!=null){try{
                typeof(MainWindow).GetField("saved",Members).SetValue(window,SourceText(window));
                typeof(MainWindow).GetField("recoveredDocument",Members).SetValue(window,false);
                typeof(MainWindow).GetField("sourceSnapshotDirty",Members).SetValue(window,false);
                var tabs=(System.Collections.IList)typeof(MainWindow).GetField("documents",Members).GetValue(window);
                foreach(object tab in tabs){tab.GetType().GetField("Saved",Members).SetValue(tab,tab.GetType().GetField("Text",Members).GetValue(tab));tab.GetType().GetField("Recovered",Members).SetValue(tab,false);}
                window.Close();
            }catch(Exception){}}
        }
    }
    static bool Listed(string id){
        foreach(var item in RecoveryStore.List())if(RecoveryStore.RecordId(item.File)==id)return true;
        return false;
    }
    // A failed open must be a message only: the tab that was open keeps its text, its path, its
    // tab count and its undo history. Both the explicit-encoding route (a CP949 file read as
    // strict UTF-8) and the ordinary auto-detect route (a missing file) are checked on the real
    // queued open path the menu, the recent list and a drop all use.
    static void VerifyFailedOpenKeepsDocument(string outDir,Action<string,bool,string> check){
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        MainWindow window=null;
        try{
            window=new MainWindow {ShowInTaskbar=false,Width=900,Height=700};
            window.Show();window.Left=-20000;window.Activate();
            window.SetText("first line\nsecond line");window.SetMode(true);Pump();
            var editor=Field<SourceEditor>(window,"source");
            editor.Select(editor.Document.TextLength,0);Pump();
            editor.TextArea.PerformTextInput("X");Pump();
            int tabsBefore=window.OpenDocumentCount;
            string beforeEdit="first line\nsecond line",edited=SourceText(window);
            string cp949=Path.Combine(outDir,"generated-cp949.txt");
            File.WriteAllBytes(cp949,Encoding.GetEncoding(949).GetBytes("한글 문서"));
            EnqueueEncodingOpen(window,cp949,65001);
            bool finished=WaitForOpenQueue(window,20000);
            Exception error=window.LastFileOpenError;
            bool failed=finished&&error!=null;
            check("explicit UTF-8 open of a generated CP949 file fails",failed,"finished="+finished+", error="+(error==null?"none":error.GetType().Name+": "+error.Message));
            check("failed encoding open keeps the document, its tab and its path",
                SourceText(window)==edited&&window.OpenDocumentCount==tabsBefore,
                "text=["+FlatText(SourceText(window))+"] tabs="+window.OpenDocumentCount+"/"+tabsBefore+", path=["+Field<string>(window,"path")+"]");
            Call(window,"Undo");Pump();
            check("failed encoding open keeps the undo history",SourceText(window)==beforeEdit,
                "text=["+FlatText(SourceText(window))+"] expected=["+FlatText(beforeEdit)+"]");
            Call(window,"Redo");Pump();
            EnqueueEncodingOpen(window,Path.Combine(outDir,"generated-missing-file.txt"),null);
            finished=WaitForOpenQueue(window,20000);
            error=window.LastFileOpenError;
            check("missing file through the ordinary open path fails",finished&&error!=null,"finished="+finished+", error="+(error==null?"none":error.GetType().Name));
            check("failed ordinary open keeps the document, its tab and its path",
                SourceText(window)==edited&&window.OpenDocumentCount==tabsBefore,
                "text=["+FlatText(SourceText(window))+"] tabs="+window.OpenDocumentCount+"/"+tabsBefore+", path=["+Field<string>(window,"path")+"]");
            // Single-window mode ("새 탭으로 보기" off): a file opened while this document is up
            // goes to a new window. That window must not stay as an empty "새 문서" when the read
            // fails: the original document is the only thing left, with the error message.
            Call(window,"SetOpenInTabs",false);Pump();
            int windowsBefore=CountMainWindows();
            EnqueueEncodingOpen(window,cp949,65001);
            WaitForOpenQueue(window,20000);
            WaitForAllOpenQueues(20000);Pump();
            check("failed open in single-window mode leaves no empty new window",
                CountMainWindows()==windowsBefore&&SourceText(window)==edited,
                "windows="+CountMainWindows()+"/"+windowsBefore+", text=["+FlatText(SourceText(window))+"]");
            // A successful open in the same mode still gets its own window with the file applied.
            string valid=Path.Combine(outDir,"generated-single-window.txt");
            File.WriteAllText(valid,"single window body",new UTF8Encoding(false));
            EnqueueEncodingOpen(window,valid,null);
            WaitForOpenQueue(window,20000);WaitForAllOpenQueues(20000);Pump();
            MainWindow opened=ExtraWindow(window);
            check("single-window mode still opens a valid file in its own window",
                opened!=null&&SourceText(opened)=="single window body"&&SourceText(window)==edited,
                "window="+(opened==null?"none":"found")+", text=["+(opened==null?"":FlatText(SourceText(opened)))+"]");
            CloseExtraWindows(window);
        }finally{
            if(window!=null){
                try{typeof(MainWindow).GetField("saved",Members).SetValue(window,SourceText(window));}catch(Exception){}
                window.Close();
            }
        }
    }
    static string SourceText(MainWindow window){return Field<SourceEditor>(window,"source").Document.Text;}
    static string FlatText(string text){return text==null?"null":text.Replace("\r","\\r").Replace("\n","\\n");}
    static void EnqueueEncodingOpen(MainWindow window,string path,int? codePage){
        Type operation=typeof(MainWindow).GetNestedType("FileOperation",Members);
        if(operation==null)throw new MissingMemberException("probe: MainWindow has no nested FileOperation");
        object item=Activator.CreateInstance(operation,true);
        operation.GetField("Name",Members).SetValue(item,path);
        operation.GetField("CodePage",Members).SetValue(item,codePage);
        MethodInfo enqueue=typeof(MainWindow).GetMethod("EnqueueFileOperation",Members);
        if(enqueue==null)throw new MissingMemberException("probe: MainWindow has no EnqueueFileOperation");
        enqueue.Invoke(window,new object[]{item});
    }
    static int CountMainWindows(){
        int count=0;
        if(Application.Current!=null)foreach(Window candidate in Application.Current.Windows)if(candidate is MainWindow)count++;
        return count;
    }
    static bool WaitForAllOpenQueues(int timeoutMilliseconds){
        var watch=System.Diagnostics.Stopwatch.StartNew();
        FieldInfo running=typeof(MainWindow).GetField("fileOpenRunning",Members);
        while(watch.ElapsedMilliseconds<timeoutMilliseconds){
            bool busy=false;
            if(Application.Current!=null)foreach(Window candidate in Application.Current.Windows){
                var editor=candidate as MainWindow;
                if(editor!=null&&running!=null&&Object.Equals(running.GetValue(editor),true))busy=true;
            }
            if(!busy)return true;
            Pump();
            System.Threading.Thread.Sleep(10);
        }
        return false;
    }
    static MainWindow ExtraWindow(MainWindow keep){
        if(Application.Current!=null)foreach(Window candidate in Application.Current.Windows){
            var editor=candidate as MainWindow;
            if(editor!=null&&!Object.ReferenceEquals(editor,keep))return editor;
        }
        return null;
    }
    static void CloseExtraWindows(MainWindow keep){
        var extra=new System.Collections.Generic.List<Window>();
        if(Application.Current!=null)foreach(Window candidate in Application.Current.Windows){
            var editor=candidate as MainWindow;
            if(editor!=null&&!Object.ReferenceEquals(editor,keep))extra.Add(candidate);
        }
        foreach(Window candidate in extra){
            try{typeof(MainWindow).GetField("saved",Members).SetValue(candidate,Field<string>((MainWindow)candidate,"text"));}catch(Exception){}
            try{candidate.Close();}catch(Exception){}
        }
    }
    static bool WaitForOpenQueue(MainWindow window,int timeoutMilliseconds){
        var watch=System.Diagnostics.Stopwatch.StartNew();
        FieldInfo running=typeof(MainWindow).GetField("fileOpenRunning",Members);
        while(watch.ElapsedMilliseconds<timeoutMilliseconds){
            LongLineBench.Idle();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));
            LongLineBench.Idle();
            if(running!=null&&!Object.Equals(running.GetValue(window),true))return true;
            System.Threading.Thread.Sleep(10);
        }
        return false;
    }
    static void Pump(){LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
    static T Field<T>(MainWindow window,string name){
        FieldInfo field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");
        return (T)field.GetValue(window);
    }
    static T Property<T>(MainWindow window,string name){
        PropertyInfo property=typeof(MainWindow).GetProperty(name,Members);
        if(property==null)throw new MissingMemberException("probe: MainWindow has no property '"+name+"'");
        return (T)property.GetValue(window,null);
    }
    static bool Unsaved(MainWindow window){return Property<bool>(window,"HasUnsavedChanges");}
    static T TabField<T>(object tab,string name){
        FieldInfo field=tab.GetType().GetField(name,Members);
        if(field==null)throw new MissingMemberException("probe: DocumentTab has no field '"+name+"'");
        return (T)field.GetValue(tab);
    }
    static object Call(MainWindow window,string name,params object[] args){
        MethodInfo method=typeof(MainWindow).GetMethod(name,Members);
        if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");
        return method.Invoke(window,args);
    }
    static void WriteDraft(string id,RecoveryRecord record,string text){
        RecoveryStore.WriteAsync(id,record,new StringTextSource(text),null).GetAwaiter().GetResult();
        if(RecoveryStore.LastError!=null)throw new InvalidOperationException("Generated draft write failed",RecoveryStore.LastError);
    }
    static string DraftFile(string id){
        string[] files=Directory.GetFiles(RecoveryStore.Root,"*-"+id+".draft");
        if(files.Length!=1)throw new InvalidOperationException("Generated draft file was not found");
        return files[0];
    }
}
}
