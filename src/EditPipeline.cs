using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Documents;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
namespace MDNote {public partial class MainWindow {
    bool sourceSnapshotDirty;bool HasUnsavedChanges {get{return recoveredDocument||sourceSnapshotDirty||text!=saved;}}
    string documentText="";bool sourceMirrorOutdated,pipelineBusy,imeComposing;DispatcherOperation refreshOperation;
    string text {get{if(sourceSnapshotDirty){documentText=source.Text;sourceSnapshotDirty=false;}return documentText;}set{documentText=value;sourceSnapshotDirty=false;sourceMirrorOutdated=true;}}
    // Posted refresh: the background pass updates tools, outline and the table row after an edit
    // burst. The document text itself is read lazily by the `text` getter.
    void QueueEditorRefresh(bool content,bool preserveBlockCache=false){if(loading||pipelineBusy)return;if(refreshOperation==null||refreshOperation.Status!=DispatcherOperationStatus.Pending)refreshOperation=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{refreshOperation=null;if(MenuOwnsFocus()){if(ImeTrace.Enabled)ImeTrace.Write("editor refresh deferred menu focus focus="+FocusName());QueueRefreshAfterMenu();return;}if(!RichCompositionLive)FlushDocumentChanges();}));}
    internal Action<string,double> PipelineMeasurement=null;
    void MeasurePipeline(string stage,ref long timestamp){if(PipelineMeasurement==null)return;long now=System.Diagnostics.Stopwatch.GetTimestamp();PipelineMeasurement(stage,(now-timestamp)*1000.0/System.Diagnostics.Stopwatch.Frequency);timestamp=now;}
    void FlushDocumentChanges(){if(loading||pipelineBusy||RichCompositionLive)return;pipelineBusy=true;try{UpdateTools();if(sourceOutlineDirty)RefreshOutline();UpdateTableTools();}finally{pipelineBusy=false;}}
    // The refresh flag belongs to the current document; a document swap cancels the posted pass.
    void CancelEditorRefresh(){if(refreshOperation!=null&&refreshOperation.Status==DispatcherOperationStatus.Pending)refreshOperation.Abort();refreshOperation=null;}
    void RestoreEditorHistory(bool redo){
        if(documentOperationBusy)return;
        FinishComposition();
        // FinishComposition only asks for the commit; a store that still owns the text (or a
        // composition already restarted) must not be edited by an undo patch. Skip this press.
        if(CompositionOwnsStore())return;
        /* Undo and Redo are commands: they end the typing step even when the caret comes back to
           the same offset, so typing after them starts a new entry. */
        if(source!=null)source.BreakTypingRun();
        RestoreHistory(redo);UpdateTools();
    }
    void CancelPendingRichHistory(){}
    // Complete the source's IME composition before a command edits the document.
    void FinishComposition(){FinishCompositionCore(false);}
    void FinishCompositionCore(bool forceWindowCommit){
        if(ImeTrace.Enabled)ImeTrace.Write("FinishCompositionCore force="+forceWindowCommit+" source="+(source==null?"-":source.CompositionState())+" focus="+FocusName());
        if(source!=null){
            if(forceWindowCommit)source.CompleteCompositionForViewSwitch();else source.CompleteComposition();
            // A menu-requested commit whose result never comes back (its WM_CHAR landed on the menu)
            // leaves the counted result pending, and CompositionOwnsStore then skips every undo and
            // save press. Age that count out on this path too, exactly as the composition timer
            // does; the composition latch itself stays with ClearEndedComposition below, so a live
            // composition that still owns the store is not released early.
            source.ExpireLostCommitResult("finish");
            // END may precede the final result (Korean IME); once that result is in, an empty IMM
            // composition string must not keep the undo/history guard parked forever.
            source.ClearEndedComposition();
        }
        imeComposing=false;
        FlushDocumentChanges();
    }
    // A tab change captures or detaches the editor the composition lives in, and AvalonEdit
    // cancels the IMM composition from the focus loss that follows: the syllable has to be in
    // the document before SwitchTab/CycleTab/CreateTab/CloseTab (or a file that becomes a tab)
    // swaps the surfaces. This is the same guard and commit call every other surface change uses
    // (SwitchSurface/SetMode/SetLiveMode). The tab paths used to read MainWindow.imeComposing,
    // which no product path ever sets, so nothing was committed; the source's own state is the
    // truth. imeComposing stays in the guard so a probe that sets it keeps the old behaviour.
    void CommitCompositionBeforeViewChange(){
        if(imeComposing||(source!=null&&(source.ImeBusy||source.ImeCompositionActive())))FinishComposition();
    }
    // StateChanged/Deactivated are too late for AvalonEdit: WPF may already have raised
    // LostKeyboardFocus, whose TextArea override cancels IMM and releases the context.
    // Add a public HwndSource hook after source creation, before WPF processes the native
    // focus/activation message. Never mark it handled: Windows/WPF must still lose focus.
    System.Windows.Interop.HwndSource compositionDeactivationSource;
    bool committingDeactivation;
    // WPF menus and dropdown popups own keyboard focus while they open and stay open. The basic
    // view's TSF composition completes on that focus change, which marks the document dirty at
    // the exact moment the popup is settling. Reserialising and mirroring into the hidden source
    // from a posted callback then left the 보기 popup unable to take the first item click
    // (real-IME runs, 2026-10-26). Command paths still flush synchronously; only the background
    // refresh waits for the menu to close.
    bool MenuOwnsFocus(){
        if(topMenu!=null&&topMenu.IsKeyboardFocusWithin)return true;
        if(System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.MenuItem)return true;
        if(topMenu!=null)foreach(object entry in topMenu.Items){var item=entry as System.Windows.Controls.MenuItem;if(item!=null&&item.IsSubmenuOpen)return true;}
        return false;
    }
    bool menuRefreshDeferred;System.Windows.Threading.DispatcherTimer menuRefreshTimer;
    void QueueRefreshAfterMenu(){
        menuRefreshDeferred=true;
        if(menuRefreshTimer==null){
            menuRefreshTimer=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromMilliseconds(40)};
            menuRefreshTimer.Tick+=delegate{
                if(!menuRefreshDeferred){menuRefreshTimer.Stop();return;}
                if(MenuOwnsFocus())return;
                menuRefreshTimer.Stop();menuRefreshDeferred=false;
                if(!RichCompositionLive)FlushDocumentChanges();
            };
            Closed+=delegate{if(menuRefreshTimer!=null)menuRefreshTimer.Stop();menuRefreshDeferred=false;};
        }
        menuRefreshTimer.Start();
    }
    void SetupCompositionDeactivation(){
        SourceInitialized+=delegate{
            compositionDeactivationSource=System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if(compositionDeactivationSource!=null)compositionDeactivationSource.AddHook(CompositionDeactivationMessage);
        };
        Closed+=delegate{
            var owner=compositionDeactivationSource;compositionDeactivationSource=null;
            if(owner!=null){try{owner.RemoveHook(CompositionDeactivationMessage);}catch(ObjectDisposedException){}}
        };
    }
    IntPtr CompositionDeactivationMessage(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled){
        bool leaving=(msg==0x0006&&(wParam.ToInt64()&0xFFFF)==0)|| // WM_ACTIVATE / WA_INACTIVE
            (msg==0x001C&&wParam==IntPtr.Zero)|| // WM_ACTIVATEAPP / false
            (msg==0x0112&&(wParam.ToInt64()&0xFFF0)==0xF020)|| // WM_SYSCOMMAND / SC_MINIMIZE
            msg==0x0008; // WM_KILLFOCUS (ShowWindow(SW_MINIMIZE) need not send SC_MINIMIZE)
        if(leaving){
            // A menu/popup of this window is a separate HWND, so the main window can receive
            // WM_KILLFOCUS while the popup takes native focus (MenuOwnsFocus covers the WPF-level
            // change). Neither is an application deactivation, and committing the composition
            // there touches the IME while the popup is opening: a forced CPS_COMPLETE at menu
            // time made the 보기 popup miss the item click (real-IME run, 2026-10-26).
            if(msg==0x0008&&SameProcessWindow(wParam)){
                if(ImeTrace.Enabled)ImeTrace.Write("window deactivation skipped own window msg=0x8 focus="+FocusName());
                return IntPtr.Zero;
            }
            if(msg==0x0008&&MenuOwnsFocus()){
                if(ImeTrace.Enabled)ImeTrace.Write("window deactivation skipped menu focus msg=0x8 focus="+FocusName());
                return IntPtr.Zero;
            }
            if(ImeTrace.Enabled)ImeTrace.Write("window deactivation msg=0x"+msg.ToString("X")+" focus="+FocusName());
            CommitCompositionBeforeDeactivation();
        }
        return IntPtr.Zero;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    static readonly uint OwnProcessId=(uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    static bool SameProcessWindow(IntPtr hwnd){if(hwnd==IntPtr.Zero)return false;uint process;GetWindowThreadProcessId(hwnd,out process);return process==OwnProcessId;}
    // The native hook protects both stores while the editing surface still owns focus. Keep
    // the old window events as a fallback, and avoid reacting to another control's IME or to
    // probes that deliberately simulate composition without a real IMM/TSF context.
    void CommitCompositionBeforeDeactivation(){
        if(Program.IsSelfTest||loading||committingDeactivation)return;
        committingDeactivation=true;
        try{
            if(source==null||source.Visibility!=Visibility.Visible||!source.TextArea.IsKeyboardFocusWithin)return;
            if(source.ImeBusy||source.ImeCompositionActive())source.CommitCompositionText("window deactivation");
        }finally{committingDeactivation=false;}
    }
}}
