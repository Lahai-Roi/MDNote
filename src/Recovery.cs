using System;using System.IO;using System.Text;using System.Collections.Generic;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Input;using System.Windows.Threading;using ICSharpCode.AvalonEdit.Document;
namespace MDNote {public partial class MainWindow {
readonly DispatcherTimer recoveryTimer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(2)};static bool recoveryOfferShown;bool recoveredDocument;/* One session restore per process: the first window of a launch restores every stored window group, and a window it opens for a later group restores only its own. */static bool sessionRestoreAttempted;static string[] pendingLaunchFiles;List<RecoveryRecord> pendingSessionGroup;/* This window's session group, written on close. The order is the window's creation order, so a restore puts the windows back in the order they were opened. */string sessionGroup;int sessionOrder;string sessionEndGroup;static int sessionWindowSerial;
void SetupRecovery(MenuItem file){var option=new MenuItem {Header=T("복구할 문서…")};option.Click+=async delegate{await OfferRecovery(false);};int recentAt=recentMenu==null?-1:file.Items.IndexOf(recentMenu);file.Items.Insert(recentAt>=0?recentAt+1:Math.Min(3,file.Items.Count),option);recoveryTimer.Tick+=delegate{CaptureRecoveryDrafts();};Loaded+=async delegate{if(RecoveryStore.Enabled)recoveryTimer.Start();/* A window opened for another session group restores that group and never starts a second session restore or a second crash prompt. */if(pendingSessionGroup!=null){var pending=pendingSessionGroup;pendingSessionGroup=null;RestoreSessionGroup(pending);return;}if(Program.IsSelfTest||recoveryOfferShown)return;recoveryOfferShown=true;/* A launch that was given documents opens them once the session is back: the argument file becomes a tab of the first restored window, or selects its already restored tab. */bool launchesDocuments=pendingLaunchFiles!=null;if(!launchesDocuments)await Task.Delay(300);await Task.Run(delegate{/* Only a provably empty new-document draft expires; a postponed draft with unsaved text keeps being offered at every start (RecoveryStore.PurgeOlderThan). */RecoveryStore.PurgeOlderThan(TimeSpan.FromDays(7));});/* 이전 세션 계속하기: the drafts a close left are restored instead of the crash prompt, so the two never offer the same text; a session draft is skipped by List anyway, and restoring it here leaves the prompt with the real crash drafts only. */await RestoreSession();await OfferRecovery(true);/* Shared temp picture folder: old unreferenced pictures go to the Recycle Bin once per launch, late (ImageTempCleanup). */ImageTempCleanup.ScheduleOnce();};/* Session end kept its drafts on purpose: deleting them here would undo the synchronous flush. */Closed+=delegate{recoveryTimer.Stop();if(sessionEndFlushed)return;foreach(var document in documents)ForgetRecovery(document);};SourceInitialized+=delegate{var endSource=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);if(endSource!=null)endSource.AddHook(SessionEndMessage);};}
// Set while SessionEnding (or Dispatcher shutdown) flushes drafts; WPF ignores Closing's Cancel then.
static bool sessionEnding;bool sessionEndFlushed;
void ForgetRecovery(DocumentTab state){if(state==null||!RecoveryStore.Enabled)return;try{RecoveryStore.Remove(state.RecoveryId);RecoveryStore.DiscardGroup(state.RecoveryId);}catch(IOException){}catch(UnauthorizedAccessException){}state.RecoveryVersion=null;/* The pictures pasted into an untitled document follow the text: once the document is saved the links were rewritten to its own folder (PasteSupport.MovePastedImagesToDocument), and a tab that is never restored needs no list. */ForgetPastedImages(state);}
/* 이전 세션 계속하기 (user, 2026-11-03, label matched to Windows 11 Notepad): the File-menu option
   that closes the window without the save prompt. The switch itself is a preference, so every
   window follows it, and the menu's own check is updated here rather than translated. */
void SetContinueSession(bool value){
    continueSession=value;if(continueSessionToggle!=null)continueSessionToggle.IsChecked=value;
    /* Every open window follows the switch at once, not only after a restart: the option decides
       how each window closes, and the menu is per window. */
    var application=Application.Current;
    if(application!=null)foreach(Window window in application.Windows){
        var other=window as MainWindow;
        if(other==null||Object.ReferenceEquals(other,this))continue;
        other.continueSession=value;
        if(other.continueSessionToggle!=null)other.continueSessionToggle.IsChecked=value;
    }
    SavePreferences();
}
/* This window's whole tab set is written as one session group. A tab with unsaved text keeps its
   text, path (empty for an untitled document), encoding, mode, caret and the file version it was
   editing; a tab without changes keeps only its path (Clean), and the restore reopens it from disk
   the way File > Open would. The tab order is the draft index and the tab that was on screen is
   flagged, so both come back. Drafts of this same group from an earlier capture are dropped first,
   so a capture never leaves two copies behind. Returns false when a write failed, and the caller
   then keeps today's save prompt instead of closing silently. */
bool CaptureSessionForClose(){
    if(!RecoveryStore.Enabled)return false;
    SetupSessionGroup();
    try{RecoveryStore.DropOwnGroup(sessionGroup);}catch(Exception){return false;}
    try{FlushDocumentChanges();}catch(Exception){}
    var replaced=new List<DocumentTab>();
    for(int i=0;i<documents.Count;i++){
        var state=documents[i];
        try{
            bool active=Object.ReferenceEquals(state,activeDocument);
            bool dirty=active?HasUnsavedChanges:(state.Recovered||state.Text!=state.Saved);
            string file=active?path:state.Path;
            Encoding encoding=active?documentEncoding:state.Encoding;
            string newline=active?documentNewline:state.Newline;
            string body=dirty?(active?text:state.Text):"";
            int caret=active?(source==null?0:source.CaretIndex):(state.Source==null?0:state.Source.CaretIndex);
            string fingerprint=active?documentFingerprint:state.Fingerprint;
            var record=new RecoveryRecord {Name=file==""?(state.RecoveryName??((active?PlainText:state.TextMode==true)?T("문서.txt"):T("문서.md"))):Path.GetFileName(file),
                OriginalPath=file,PlainText=active?PlainText:state.TextMode==true,CodePage=encoding.CodePage,Bom=encoding.GetPreamble().Length>0,
                Newline=newline,Caret=caret,Fingerprint=fingerprint==null?"":fingerprint,Clean=!dirty,Active=active};
            /* A dirty background tab writes its captured copy; the visible dirty tab writes straight
               from its buffer. A clean tab has no text to write beyond the header naming its path. */
            bool written=RecoveryStore.WriteSession(sessionGroup,sessionOrder,i,record,active&&dirty?(source==null?null:source.Document.CreateSnapshot()):null,body);
            if(!written){RecoveryStore.DropOwnGroup(sessionGroup);return false;}
            /* The session draft now holds this tab's unsaved text, so the crash draft the timer may
               have written for the same tab is dropped: otherwise the crash prompt would offer the
               text a second time after the session restore. */
            /* Removed only after every tab of the group is written (below): a later tab's failed write drops the group, and the crash draft of an earlier tab must still be there then. */
            if(state.RecoveryVersion!=null)replaced.Add(state);
        }catch(Exception){RecoveryStore.DropOwnGroup(sessionGroup);return false;}
    }
    foreach(var state in replaced){try{RecoveryStore.Remove(state.RecoveryId);}catch(Exception){}state.RecoveryVersion=null;}
    return true;
}
/* The drafts are written per window, so the group id and its order exist before the first close. */
void SetupSessionGroup(){
    if(sessionGroup!=null)return;
    sessionGroup=Guid.NewGuid().ToString("N").Substring(0,12);
    sessionOrder=System.Threading.Interlocked.Increment(ref sessionWindowSerial);
}
/* The first window of a launch restores every stored window group: the earliest group fills this
   window (its blank tab is the first tab) and each other group becomes its own window. A dirty tab
   is loaded as an ordinary document and then marked modified, so the title keeps its * and the
   first save writes the text over its own file; a file that changed on disk since the close still
   meets the fingerprint it closed with, so the first save asks instead of overwriting it in
   silence. A clean tab is reopened from the path it stored. Each draft is deleted after it is read,
   so a second start no longer offers it. */
async Task RestoreSession(){
    if(sessionRestoreAttempted)return;
    sessionRestoreAttempted=true;
    if(Program.IsSelfTest||fileOpenClosed)return;
    /* Only a window that starts blank is the launch window; a link window or a window that already
       holds a document keeps its own tabs. */
    if(path!=""||text!=""||saved!=""||recoveredDocument){OpenLaunchFiles(false);return;}
    var records=await Task.Run(delegate{
        var list=RecoveryStore.ReadSessions();
        /* One read off the UI thread per tab: a dirty draft's text, or the file a clean tab names.
           A file deleted since the close sets LoadFailed and its tab is skipped instead of opening
           an empty document under that name. */
        foreach(var record in list){
            try{
                if(record.Clean){
                    if(String.IsNullOrEmpty(record.OriginalPath))continue;
                    string full=Path.GetFullPath(record.OriginalPath);
                    if(!File.Exists(full)||Directory.Exists(full)){record.LoadFailed=true;continue;}
                    record.LoadedText=ReadTextFileVersion(full,out record.LoadEncoding,out record.LoadFingerprint,out record.LoadBytes);
                }else{
                    record.LoadedText=RecoveryStore.Read(record.File).Text;
                }
            }catch(Exception){record.LoadFailed=true;}
        }
        return list;
    });
    if(fileOpenClosed)return;
    /* Text the user typed while the drafts were being read turns this into an ordinary launch:
       restoring over it would throw the first keystrokes away, so the session stays for a later
       launch. */
    if(path!=""||text!=""||saved!=""||recoveredDocument){OpenLaunchFiles(false);return;}
    var groups=GroupSessions(records);
    if(groups.Count==0){OpenLaunchFiles(false);return;}
    try{RestoreSessionGroup(groups[0]);}catch(Exception){}
    OpenLaunchFiles(true);
    for(int i=1;i<groups.Count&&!fileOpenClosed;i++){
        /* A group that cannot open a window keeps its drafts for the next launch instead of taking
           the restored ones down with it. */
        try{var window=new MainWindow();window.pendingSessionGroup=groups[i];window.Show();}catch(Exception){}
    }
}
/* Records grouped by the window that wrote them: groups in window creation order, records in tab
   order inside each group. The group string alone identifies a window (the order only sorts). */
static List<List<RecoveryRecord>> GroupSessions(List<RecoveryRecord> records){
    var groups=new List<List<RecoveryRecord>>();
    foreach(var record in records){
        List<RecoveryRecord> found=null;
        foreach(var group in groups)if(String.Equals(group[0].SessionGroup,record.SessionGroup,StringComparison.Ordinal)){found=group;break;}
        if(found==null){found=new List<RecoveryRecord>();groups.Add(found);}
        found.Add(record);
    }
    foreach(var group in groups)group.Sort(delegate(RecoveryRecord a,RecoveryRecord b){int order=a.SessionOrder.CompareTo(b.SessionOrder);return order!=0?order:a.SessionIndex.CompareTo(b.SessionIndex);});
    groups.Sort(delegate(List<RecoveryRecord> a,List<RecoveryRecord> b){int order=a[0].SessionOrder.CompareTo(b[0].SessionOrder);return order!=0?order:String.CompareOrdinal(a[0].SessionGroup,b[0].SessionGroup);});
    return groups;
}
/* One stored window: its tabs in their original order, with the tab that was on screen selected at
   the end. A clean record reopens its file; a dirty one restores its unsaved text and keeps the
   modified mark. A draft is deleted only after its tab was restored: a read that failed, or a tab
   that could not be built, leaves the draft on disk for the next launch instead of throwing away
   the only copy of text that never reached a file. Each record is used once per run and the crash
   prompt never lists a session id, so a draft kept here is not restored a second time now. */
void RestoreSessionGroup(List<RecoveryRecord> group){
    if(group==null||group.Count==0)return;
    var tabs=new List<DocumentTab>();DocumentTab activeTab=null;
    foreach(RecoveryRecord record in group){
        if(record.LoadFailed)continue;
        bool restored=false;
        try{
            /* CreateTab captures the tab that is active now, so the new record's encoding and
               fingerprint must not be in the window's fields yet: they would be captured onto the
               previous tab. The blank tab this window starts with takes the first record. */
            bool blank=path==""&&text==""&&saved==""&&!recoveredDocument;
            if(record.Clean){
                if(!blank)CreateTab();
                if(String.IsNullOrEmpty(record.OriginalPath)){
                    textMode=record.PlainText;SetMode(true);CaptureTab();
                }else{
                    string full=Path.GetFullPath(record.OriginalPath);
                    string body=record.LoadedText;string newline=DetectNewline(body);string buffer=NormalizeLineEndings(body,newline);
                    byte[] bytes=record.LoadBytes;if(buffer==body)bytes=null;
                    ApplyLoadedFile(full,body,record.LoadEncoding,record.LoadFingerprint,bytes,buffer,newline);
                    if(source!=null&&source.Document!=null)source.Select(Math.Max(0,Math.Min(record.Caret,source.Document.TextLength)),0);
                    CaptureTab();
                }
            }else{
                string body=record.LoadedText;
                if(body==null)continue;   /* the text did not arrive: keep the draft for the next launch */
                if(!blank)CreateTab();
                Encoding encoding=RecoveryEncoding(record);
                string full=SessionFullPath(record.OriginalPath);
                documentEncoding=encoding;documentNewline=record.Newline=="\r\n"||record.Newline=="\r"?record.Newline:"\n";
                documentFingerprint=String.IsNullOrEmpty(record.Fingerprint)?null:record.Fingerprint;
                documentFingerprintPath=full;
                documentOriginalBytes=null;preview.IsChecked=false;textMode=record.PlainText;
                path=full??"";
                recoveredDocument=true;/* the restored text is unsaved text, and the title keeps its mark */
                SetText(NormalizeLineEndings(body,documentNewline));
                /* The save point is the restored text itself, not an empty document: the edit-exit
                   prompt's 버리기 puts `saved` back into the buffer, and an empty save point would
                   throw the restored text away. The document is still modified through
                   recoveredDocument, so the title keeps its * and the first save writes it out. */
                saved=text;
                SetMode(true);
                /* The caret comes back only when it still fits the text that was restored. */
                if(source!=null&&source.Document!=null)source.Select(Math.Max(0,Math.Min(record.Caret,source.Document.TextLength)),0);
                CaptureTab();
            }
            tabs.Add(activeDocument);
            if(record.Active)activeTab=activeDocument;
            restored=true;
        }catch(Exception){}
        if(restored)DiscardSession(record);
    }
    if(tabs.Count>0){
        DocumentTab target=activeTab;
        if(target==null||!tabs.Contains(target))target=tabs[0];
        if(!Object.ReferenceEquals(target,activeDocument)){switchingTabs=true;documentTabs.SelectedItem=target.Item;switchingTabs=false;SwitchTab(target);}
        /* A session draft is never offered by the crash-recovery prompt (RecoveryStore.List skips
           these ids), so nothing restores it a second time. */
        UpdateTitle();RefreshTabCaption();RefreshOutline();
        if(source!=null&&source.Document!=null)MarkdownLivePreview.Session.SafeBringCaretToView(source.TextArea);
    }
}
/* The documents a launch was given (Explorer double-click): opened after the session is back. When
   a session group was restored into this window the argument always becomes a tab here - even under
   "새 탭으로 보기" off, whose new-window rule would otherwise split the restored window - or selects
   the restored tab that already holds the file. The option is put back after the queued opens, so
   it still governs the next open. Without a restored group the argument follows the option exactly
   as any other open does. */
void OpenLaunchFiles(bool restoredSession){
    string[] files=pendingLaunchFiles;
    pendingLaunchFiles=null;
    if(files==null||files.Length==0)return;
    if(!restoredSession||openInTabs){foreach(string file in files)LoadFile(file);return;}
    openInTabs=true;
    int remaining=files.Length;
    MainWindow target=this;
    foreach(string file in files){
        var operation=new FileOperation {Name=file,Completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)};
        EnqueueFileOperation(operation);
        operation.Completion.Task.ContinueWith(delegate(System.Threading.Tasks.Task<bool> task){
            target.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{remaining--;if(remaining<=0)target.openInTabs=false;}));
        },TaskScheduler.Default);
    }
}
/* Called by the launcher once the first window exists: a document argument must not replace the
   blank tab the restored session needs. */
internal static void PrepareLaunchFiles(string[] files){
    if(files!=null&&files.Length>0)pendingLaunchFiles=files;
}
void DiscardSession(RecoveryRecord record){
    if(record==null||record.File==null)return;
    RecoveryStore.DiscardSession(record.File);
}
/* A stored original path may no longer be a legal path (a removed drive letter, a name Windows now
   refuses). Reading it on the UI thread must not cost the tab its text, so an unusable one is
   dropped and the tab comes back untitled instead of failing the whole restore. */
static string SessionFullPath(string path){
    if(String.IsNullOrEmpty(path))return null;
    try{return Path.GetFullPath(path);}catch(Exception){return null;}
}
/* The close paths (X, 파일 > 끝내기, Alt+F4, Windows shutdown) all reach this decision: this window's
   tabs are written as its own session group and the close then skips the prompt. Every window has
   its own group, so closing one never waits for another and never overwrites it. A tab close and a
   self-test close call Confirm outside a close batch: those keep the prompt (the option turns off a
   window close, not the question a single tab asks). False keeps today's behaviour, which is every
   answer when the option is off or a draft could not be written. */
bool TryCloseWithSession(){
    if(!continueSession||!RecoveryStore.Enabled)return false;
    bool shutdown=sessionEnding||(Application.Current!=null&&Application.Current.Dispatcher.HasShutdownStarted);
    if(!closingBatch&&!shutdown)return false;
    if(!CaptureSessionForClose())return false;
    if(shutdown)sessionEndGroup=sessionGroup;
    return true;
}
void CaptureRecoveryDrafts(){if(!RecoveryStore.Enabled||fileOpenClosed||imeComposing)return;FlushDocumentChanges();foreach(var state in documents)CaptureRecoveryDraft(state,false);}
// No dialogs and no async work here: logoff/restart ends the process before a queued write would run.
/* b50: at session end an unfinished IME syllable may be lost, but never the rest: write every tab even while composing, and isolate failures per tab. */void FlushRecoveryDraftsForSessionEnd(){if(!RecoveryStore.Enabled)return;try{FlushDocumentChanges();}catch(Exception){}foreach(var state in new List<DocumentTab>(documents)){try{CaptureRecoveryDraft(state,true);}catch(Exception){}}}
void FlushForSessionEnd(){if(sessionEndFlushed)return;sessionEndFlushed=true;if(fileOpenRunning)CancelFileOpen();FinishComposition();/* 이전 세션 계속하기 answers a Windows shutdown (logoff/restart) as well: the session draft is the synchronous write the close needs, and the crash drafts below would otherwise only be offered by the prompt. */if(TryCloseWithSession())return;FlushRecoveryDraftsForSessionEnd();}
internal static void FlushAllForSessionEnd(){sessionEnding=true;var current=Application.Current;if(current==null)return;var editors=new List<MainWindow>();foreach(Window window in current.Windows){var editor=window as MainWindow;if(editor!=null)editors.Add(editor);}foreach(var editor in editors){try{editor.FlushForSessionEnd();}catch(Exception){}}}
/* A logoff/restart the system cancelled leaves sessionEnding and sessionEndFlushed set, and every
   later window close would skip the save prompt and keep the drafts. Activation clears them (the
   app is still running), but never while the dispatcher is really shutting down. */
/* WM_ENDSESSION with wParam FALSE: the shutdown was cancelled (another application refused WM_QUERYENDSESSION, or the user backed out), so what it set is undone. A window activation is not such a signal: a real shutdown activates the application right after WM_QUERYENDSESSION, and the old reset-on-Activated dropped the session draft that had just been written (2026-11-03: restart lost the text). */
IntPtr SessionEndMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){
    if(message==0x16&&(wParam.ToInt64()&0xFFFFFFFFL)==0)ResetSessionEndState();
    return IntPtr.Zero;
}
internal static void ResetSessionEndState(){
    var current=Application.Current;if(current==null||current.Dispatcher.HasShutdownStarted)return;
    sessionEnding=false;
    /* A shutdown the system cancelled never closed the window: the group written for it describes a
       close that did not happen, so only that window's group is dropped (a window closed earlier
       in the same process keeps its own group) and the next real close writes the current text. */
    foreach(Window window in current.Windows){
        var editor=window as MainWindow;if(editor==null||!editor.sessionEndFlushed)continue;
        string group=editor.sessionEndGroup;editor.sessionEndGroup=null;
        if(group!=null)try{RecoveryStore.DropOwnGroup(group);}catch(Exception){}
        editor.sessionEndFlushed=false;
    }
}
void CaptureRecoveryDraft(DocumentTab state,bool synchronous){bool active=state==activeDocument;bool dirty=active?HasUnsavedChanges:state.Recovered||state.Text!=state.Saved;if(!dirty){if(state.RecoveryVersion!=null)ForgetRecovery(state);return;}bool code=true;SourceEditor editor=active?source:state.Source;bool mirror=active&&sourceSnapshotDirty;ITextSource snapshot=(code||mirror)&&editor!=null?editor.Document.CreateSnapshot():null;string value=snapshot==null?(active?text:state.Text):null;object version=snapshot!=null?(object)editor.Document.Version:value;/* A pending async write may not reach the disk before the process ends, so session end writes again even when the version is unchanged. */if(!synchronous&&Object.ReferenceEquals(state.RecoveryVersion,version)&&RecoveryStore.LastError==null)return;string original=active?path:state.Path;Encoding encoding=active?documentEncoding:state.Encoding;var record=new RecoveryRecord {Name=original==""?(state.RecoveryName??((active?PlainText:state.TextMode==true)?T("문서.txt"):T("문서.md"))):Path.GetFileName(original),OriginalPath=original,PlainText=active?PlainText:state.TextMode==true,CodePage=encoding.CodePage,Bom=encoding.GetPreamble().Length>0,Newline=active?documentNewline:state.Newline};state.RecoveryVersion=version;if(synchronous)RecoveryStore.WriteSync(state.RecoveryId,record,snapshot,value);else RecoveryStore.WriteAsync(state.RecoveryId,record,snapshot,value);}
async Task OfferRecovery(bool startup){if(fileOpenClosed)return;var records=await Task.Run(delegate{return RecoveryStore.List();});if(fileOpenClosed)return;records.RemoveAll(delegate(RecoveryRecord record){return RecoveryDocumentIsOpen(RecoveryStore.RecordId(record.File));});if(records.Count==0){if(!startup)MessageBox.Show(this,T("복구할 문서가 없습니다."),"MDNote");return;}EnsureVisibleForeground();var dialog=new Window {Title=T("문서 복구"),Owner=this,Width=560,Height=380,MinWidth=420,MinHeight=260,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background,Foreground=Foreground,ShowInTaskbar=false};dialog.Resources.MergedDictionaries.Add(Resources);var panel=new DockPanel {Margin=new Thickness(20)};dialog.Content=panel;var heading=new TextBlock {Text=T("저장하지 못한 문서가 있습니다. 복구하면 새 탭으로 열립니다."),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)};DockPanel.SetDock(heading,Dock.Top);panel.Children.Add(heading);var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,14,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);var rows=new StackPanel();panel.Children.Add(new ScrollViewer {Content=rows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});var choices=new List<CheckBox>();foreach(var record in records){var choice=new CheckBox {IsChecked=true,Content=record.Name+"  ·  "+record.Characters.ToString("N0")+T("자")+"  ·  "+record.Updated.ToLocalTime().ToString("MM-dd HH:mm"),Margin=new Thickness(0,6,0,6)};if(!String.IsNullOrEmpty(record.OriginalPath)){choice.ToolTip=record.OriginalPath;/* Off until the mouse really moves (see the PreviewMouseMove below): a dialog opened under a resting pointer would pop this tooltip's small window in front of itself. */ToolTipService.SetIsEnabled(choice,false);}choices.Add(choice);rows.Children.Add(choice);}bool restore=false,discard=false,accepted=false;Action accept=delegate{if(accepted)return;accepted=true;restore=true;dialog.DialogResult=true;};var later=new Button {Content=T("나중에"),IsCancel=true,MinWidth=70};later.Click+=delegate{dialog.DialogResult=false;};buttons.Children.Add(later);var remove=new Button {Content=T("선택 복구본 삭제"),Margin=new Thickness(6,0,0,0)};remove.Click+=delegate{if(MessageBox.Show(dialog,T("선택한 복구본을 삭제할까요? 원본 파일은 유지되며 복구본 삭제는 되돌릴 수 없습니다."),"MDNote",MessageBoxButton.YesNo,MessageBoxImage.Warning)==MessageBoxResult.Yes){discard=true;dialog.DialogResult=true;}};buttons.Children.Add(remove);var open=new Button {Content=T("선택 복구"),IsDefault=true,MinWidth=80,Margin=new Thickness(6,0,0,0)};open.Click+=delegate{accept();};buttons.Children.Add(open);
/* The list takes the keyboard: the first draft row is focused on open, so Space toggles that draft
   and Tab walks the rows and then the buttons. A CheckBox is a ButtonBase, and ButtonBase turns
   Enter into a click on itself (KeyboardNavigation.AcceptsReturn), so Enter on a row would toggle
   it and never reach the IsDefault button; this handler accepts the whole list instead and stays
   out of the way of Enter on a real Button (나중에, 선택 복구본 삭제, 선택 복구). */
dialog.PreviewKeyDown+=delegate(object sender,KeyEventArgs e){/* Esc is 나중에 wherever the focus is (a row, a button, or a tooltip popup that took the keyboard). */if(e.Key==Key.Escape){e.Handled=true;if(!accepted){try{dialog.DialogResult=false;}catch(InvalidOperationException){}}return;}if(e.Key!=Key.Enter||Keyboard.FocusedElement is Button)return;e.Handled=true;accept();};
/* The dialog is centered on its owner, so the resting mouse pointer often sits over a row when it opens, and WPF then raises a mouse move and shows that row's path tooltip: a 116x30 top-level popup that the VM tool saw as the foreground MDNote window, in front of the dialog and holding Esc. Tooltips stay off until the pointer has moved a few pixels from where the first mouse event saw it. */
Point firstPointer=new Point(-1,-1);bool pointerSeen=false,tooltipsArmed=false;
dialog.PreviewMouseMove+=delegate(object sender,MouseEventArgs e){if(tooltipsArmed)return;Point now=e.GetPosition(dialog);if(!pointerSeen){pointerSeen=true;firstPointer=now;return;}if(Math.Abs(now.X-firstPointer.X)+Math.Abs(now.Y-firstPointer.Y)<4)return;tooltipsArmed=true;foreach(var item in choices)ToolTipService.SetIsEnabled(item,true);};
if(choices.Count>0)FocusManager.SetFocusedElement(dialog,choices[0]);
dialog.ContentRendered+=delegate{ForegroundSaveWindow(dialog);if(choices.Count>0)choices[0].Focus();else open.Focus();};
dialog.ShowDialog();for(int i=0;i<records.Count;i++){if(choices[i].IsChecked!=true)continue;try{if(discard)RecoveryStore.DiscardGroup(RecoveryStore.RecordId(records[i].File));else if(restore)await RestoreRecovery(records[i]);}catch(Exception error){if(!fileOpenClosed)MessageBox.Show(this,T("복구할 수 없습니다.\n")+error.Message,"MDNote");}}}
/* A draft whose document is already open (in this window or another one) is that document's own
   unsaved state; offering it a second time would open a second copy of the same text. The save
   path ends the draft id (WriteConvertedToPath -> ForgetRecovery), so a draft that was saved is
   already gone from the store and can never be offered again by this filter or any other. */
bool RecoveryDocumentIsOpen(string id){
    foreach(var document in documents)if(document.RecoveryId==id)return true;
    var application=Application.Current;if(application==null)return false;
    foreach(Window window in application.Windows){var other=window as MainWindow;if(other==null||Object.ReferenceEquals(other,this))continue;foreach(var document in other.documents)if(document.RecoveryId==id)return true;}
    return false;
}
static Encoding RecoveryEncoding(RecoveryRecord record){switch(record.CodePage){case 65001:return new UTF8Encoding(record.Bom,true);case 1200:return new UnicodeEncoding(false,record.Bom,true);case 1201:return new UnicodeEncoding(true,record.Bom,true);case 12000:return new UTF32Encoding(false,record.Bom,true);case 12001:return new UTF32Encoding(true,record.Bom,true);default:return Encoding.GetEncoding(record.CodePage,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);}}
async Task RestoreRecovery(RecoveryRecord record){var recovered=await Task.Run(delegate{return RecoveryStore.Read(record.File);});if(fileOpenClosed)return;/* A restored draft is a second document while this window still holds the one that survives the restart, and CreateTab shows the strip for 2+ documents even with "새 탭으로 보기" off, so a recovered tab is never invisible. */CreateTab();activeDocument.RecoveryId=RecoveryStore.RecordId(record.File);activeDocument.RecoveryName=recovered.Name;/* The tab's path stays "" (RC9Tests); the original file is kept on the tab so the next Save As starts in that file's folder. */activeDocument.RecoveryOriginalPath=recovered.OriginalPath??"";path="";textMode=recovered.PlainText;documentEncoding=RecoveryEncoding(recovered);documentNewline=recovered.Newline=="\r\n"||recovered.Newline=="\r"?recovered.Newline:"\n";documentFingerprint=documentFingerprintPath=null;preview.IsChecked=false;documentOriginalBytes=null;SetText(NormalizeLineEndings(recovered.Text,documentNewline));saved="";recoveredDocument=true;SetMode(true);CaptureTab();UpdateTitle();}
}}
