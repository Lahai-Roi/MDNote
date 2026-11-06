using System;using System.IO;using System.Text;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using System.Windows;using System.Windows.Controls;using System.Windows.Documents;using System.Windows.Media;using System.Windows.Threading;
namespace MDNote {public partial class MainWindow {
sealed class FileOperation {internal string Name;internal int? CodePage;internal bool ConvertMode;internal DocumentTab Document;
/* A file already read by the window that hands it to a new one ("새 탭으로 보기" off). The new
   window applies this instead of reading again, and a failed read never creates a window at all. */
internal OpenedFile Prepared;
/* A format-changing Save As reuses the conversion operation but keeps the overlay and the busy
   guard until the file is written. Completion reports the conversion result to the awaiting save. */
internal bool SaveMode,TargetPlain,Started;internal string SaveTarget;internal Encoding SaveEncoding;internal System.Threading.Tasks.TaskCompletionSource<bool> Completion;}
// Bench-only observer: timestamps are taken at the actual phase boundary, including worker read.
internal Action<string> FileOpenMeasurement;
// Bench-only progress observer: called only when the bar takes a new value, with the indeterminate
// state the bar had on entry. Null in normal runs, so the open cost is one field read.
internal Action<double,bool> BenchFileOpenProgressObserver;
void MeasureFileOpen(string phase){var observer=FileOpenMeasurement;if(observer!=null)observer(phase);if(Dispatcher.CheckAccess())AdvanceFileOpenProgress(phase);}
bool applyingLoadedFile;
void PreparingFileOpenScreen(){fileOpenLabel.Text=T("화면 준비 중…");}
// One monotonic determinate bar up to the first layout: parse 0-70%, then fixed shares for the
// render phases. Worker-thread read/normalize measurements reach SetFileOpenProgress through the
// dispatcher check. The layout drain then switches to the indeterminate look (StartFileOpenScreenProgress).
bool fileOpenUnlocked;
void SetFileOpenProgress(double percent){if(!Dispatcher.CheckAccess())return;if(fileOpenProgress==null)return;if(percent<0)percent=0;if(percent>100)percent=100;Action<double,bool> benchProgress=BenchFileOpenProgressObserver;bool benchIndeterminate=benchProgress!=null&&fileOpenProgress.IsIndeterminate;if(fileOpenProgress.IsIndeterminate)SetFileOpenIndeterminate(false);if(fileOpenProgress.Minimum!=0)fileOpenProgress.Minimum=0;if(fileOpenProgress.Maximum!=100)fileOpenProgress.Maximum=100;if(percent<fileOpenProgress.Value)return;if(Math.Abs(fileOpenProgress.Value-percent)<0.01)return;fileOpenProgress.Value=percent;if(benchProgress!=null)benchProgress(percent,benchIndeterminate);}
void ResetFileOpenProgress(){if(!Dispatcher.CheckAccess())return;if(fileOpenProgress==null)return;SetFileOpenIndeterminate(false);fileOpenProgress.Minimum=0;fileOpenProgress.Maximum=100;fileOpenProgress.Value=0;}
void AdvanceFileOpenProgress(string phase){
    double percent;
    if(String.Equals(phase,"read-done",StringComparison.Ordinal))percent=2;
    else if(String.Equals(phase,"normalize-done",StringComparison.Ordinal))percent=5;
    else if(String.Equals(phase,"parse-done",StringComparison.Ordinal))percent=70;
    else if(String.Equals(phase,"create-tab-done",StringComparison.Ordinal))percent=71;
    else if(String.Equals(phase,"apply-loaded-done",StringComparison.Ordinal))percent=73;
    else if(String.Equals(phase,"render-source-done",StringComparison.Ordinal))percent=78;
    else if(String.Equals(phase,"render-block-layout-done",StringComparison.Ordinal))percent=84;
    /* Everything up to the first layout stays at or below 90%. The drain that absorbs the remaining
       background formatting has no reliable percentage, so StartFileOpenScreenProgress turns the bar
       indeterminate (see its comment); unlock sets 100. */
    else if(String.Equals(phase,"render-baseline-done",StringComparison.Ordinal))percent=87;
    else if(String.Equals(phase,"render-attach-done",StringComparison.Ordinal))percent=89;
    else if(String.Equals(phase,"render-done",StringComparison.Ordinal))percent=89;
    else if(String.Equals(phase,"open-refresh-done",StringComparison.Ordinal))percent=90;
    else if(String.Equals(phase,"first-layout-done",StringComparison.Ordinal))percent=90;
    else if(String.Equals(phase,"overlay-hidden",StringComparison.Ordinal))percent=100;
    else return;
    SetFileOpenProgress(percent);
}
bool fileOpenLayoutReady;
// The interactive state is applied and laid out while the progress bar still covers the window.
// Doing it after unlock (as the old unlock did) changed read-only/toolbar/visibility state and ran
// document-wide layout queries once the document was already visible, which re-laid out or blocked
// the 4M-character body for seconds after it appeared.
void PrepareFileOpenLayout(){if(fileOpenLayoutReady)return;fileOpenLayoutReady=true;long probe=BenchProbeStart();documentOperationBusy=false;LayoutMode();BenchProbeEnd("unlock-layout-mode",probe);probe=BenchProbeStart();UpdateTools();BenchProbeEnd("unlock-tools",probe);probe=BenchProbeStart();UpdateLayout();BenchProbeEnd("unlock-first-layout",probe);}
// Unlock once, at the first layout when possible; the drain finally repeats it after cancel or failure.
// The final layout runs before the overlay disappears, so whatever invalidating work Prepare left
// behind is absorbed while the progress bar still covers the document.
void CompleteFileOpenUnlock(FileOperation operation){if(fileOpenUnlocked)return;fileOpenUnlocked=true;StopFileOpenScreenProgress();SetFileOpenProgress(100);/* 11-01: a .txt opened in the read view left no keyboard focus, so the first Ctrl+E (and every shortcut) went nowhere until a click; give the surface the focus when nothing holds it. */Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,new Action(delegate{if(IsActive&&System.Windows.Input.Keyboard.FocusedElement==null)source.Focus();}));bool owns=OwnsOpenView(operation);long probe=BenchProbeStart();if(!fileOpenLayoutReady){documentOperationBusy=false;/* the visible tab clears its busy read-only state even when the open's tab was switched away */LayoutMode();if(owns)UpdateTools();}if(owns)UpdateLayout();BenchProbeEnd("unlock-final-layout",probe);probe=BenchProbeStart();HideFileOpenOverlay();BenchProbeEnd("unlock-overlay",probe);MeasureFileOpen("editing-unlocked");}
// The open may only touch the viewer while its own tab is the active one: a tab switch (or Ctrl+N)
// during the open leaves another tab's RichTextBox in `viewer`, and its surface must not be released
// in place of the held one. A file open that returned before binding a tab (already-open file, new
// window, cancel during parse) owns nothing; only the mode conversion, which works on activeDocument
// and never binds a tab, keeps the current viewer. Probe calls pass no operation.
bool OwnsOpenView(FileOperation operation){return operation==null||operation.ConvertMode||(operation.Document!=null&&Object.ReferenceEquals(activeDocument,operation.Document));}
// WPF finishes FlowDocument text layout in Background-priority turns after the first measure:
// background pagination computes up to 30 ms of break records per turn, but one page that holds a
// giant paragraph is uninterruptible and can hold the thread for ~0.7 s. The table pass and our own
// deferred posts ride the same priority. For a large body that queue used to drain after unlock and
// freeze the window for seconds; here it is drained while the progress bar still covers the document
// and input is still guarded (documentOperationBusy). A fixed time cap released the document while
// that stream was still running, so the drain ends on quiet turns instead: the body is unlocked only
// after a continuous 1 s window in which no Background turn cost 50 ms or more (a gap between two
// giant pages must not end it). The 15 s cap is a safety net, not the normal exit. Column widths stay
// deferred (Surface.DeferFits) until every table is measured and then apply in one batch, because
// fitting tables per background slice invalidated the whole document on every turn.
Task DrainFileOpenBackgroundWorkAsync(FileOperation operation){return Task.FromResult(0);}
// The drain has no reliable percentage (WPF formats in uneven Background turns), so the bar turns
// indeterminate. It keeps the determinate bar's own look: same 5 px height, track brush and corner
// radius, with a 30% segment in the same fill brush sliding left->right. The default Aero
// indeterminate template has a different colour/track, so the template below replaces both states.
// The motion is driven by the render clock and Render outranks the Background continuation posted by
// the drain's yield, so it keeps animating between formatting turns.
const double FileOpenBarFallbackWidth=340.0;
static ControlTemplate FileOpenProgressTemplate(){
    var root=new FrameworkElementFactory(typeof(Grid));root.SetValue(UIElement.ClipToBoundsProperty,true);
    var track=new FrameworkElementFactory(typeof(Border));track.Name="PART_Track";track.SetValue(Border.CornerRadiusProperty,new CornerRadius(2));track.SetResourceReference(Border.BackgroundProperty,"Line");
    var indicator=new FrameworkElementFactory(typeof(Border));indicator.Name="PART_Indicator";indicator.SetValue(Border.HorizontalAlignmentProperty,HorizontalAlignment.Left);indicator.SetValue(Border.CornerRadiusProperty,new CornerRadius(2));indicator.SetResourceReference(Border.BackgroundProperty,"Accent");
    var segment=new FrameworkElementFactory(typeof(Border));segment.Name="IndeterminateSegment";segment.SetValue(Border.HorizontalAlignmentProperty,HorizontalAlignment.Left);segment.SetValue(Border.CornerRadiusProperty,new CornerRadius(2));segment.SetValue(UIElement.VisibilityProperty,Visibility.Collapsed);segment.SetResourceReference(Border.BackgroundProperty,"Accent");
    root.AppendChild(track);root.AppendChild(indicator);root.AppendChild(segment);
    return new ControlTemplate(typeof(ProgressBar)){VisualTree=root};
}
bool fileOpenIndeterminateUpdating;
// The transform the indeterminate animation was actually started on. Stopping used to look the
// template part up again, and a lookup that answers with a different Border (or with null) leaves
// the animation running on the old one: a Forever DoubleAnimation keeps MediaContext's render loop
// armed, and on a 4M-character body one render costs ~500 ms, so the window then spends most of
// every 550 ms repainting a viewport the user never touched. Measured on the generated 4M body
// (--file-bench, basic view, wrap on): animation left running -> longest 562 ms and 26 gaps over
// 100 ms after unlock; animation stopped -> longest 47 ms, 0 gaps, which is b52's number.
TranslateTransform fileOpenBarShift;
void SetFileOpenIndeterminate(bool value){
    if(fileOpenProgress==null||fileOpenIndeterminateUpdating)return;
    fileOpenIndeterminateUpdating=true;
    try{
        fileOpenProgress.IsIndeterminate=value;
        /* ApplyTemplate returns false once the template is already applied, so the lookup must not
           be skipped on that result: a "false" has to reach the stop below, or the animation and the
           stand-in window keep running while the in-window bar is determinate again (and the next
           open cannot start its own stand-in). */
        ControlTemplate template=fileOpenProgress.Template;
        if(template!=null)fileOpenProgress.ApplyTemplate();
        var indicator=template==null?null:template.FindName("PART_Indicator",fileOpenProgress) as UIElement;
        var segment=template==null?null:template.FindName("IndeterminateSegment",fileOpenProgress) as Border;
        if(value){if(indicator!=null)indicator.Visibility=Visibility.Collapsed;StartFileOpenBarAnimation(segment);}
        else{StopFileOpenBarAnimation(segment);if(indicator!=null)indicator.Visibility=Visibility.Visible;}
    }finally{fileOpenIndeterminateUpdating=false;}
}void StartFileOpenBarAnimation(Border segment){
    if(segment==null)return;
    double width=fileOpenProgress.ActualWidth;if(!(width>0))width=FileOpenBarFallbackWidth;
    double length=Math.Max(24.0,width*0.3);
    segment.Width=length;segment.Visibility=Visibility.Visible;
    // A Transform is not a FrameworkElement, so the template cannot build it; each segment gets its own here.
    var shift=segment.RenderTransform as TranslateTransform;if(shift==null){shift=new TranslateTransform();segment.RenderTransform=shift;}
    // A second start on a different Border must not leave the first one animating forever.
    if(fileOpenBarShift!=null&&!Object.ReferenceEquals(fileOpenBarShift,shift))fileOpenBarShift.BeginAnimation(TranslateTransform.XProperty,null);
    fileOpenBarShift=shift;
    /* No animation in this window (b53 regression): a Forever clock keeps MediaContext rendering every
       frame, each render of a 4M-character viewport costs ~0.5 s, and the drain then never sees a quiet
       turn and runs to its 15 s cap (b52 47 ms -> 562 ms longest stall). Even a Collapsed segment's clock
       ticks. Motion comes only from the stand-in window; in self-test the segment stays still. */
    shift.BeginAnimation(TranslateTransform.XProperty,null);shift.X=0;
    /* A render-clock animation here would be driven by this thread's render pass. During
       the layout drain a single WPF formatting turn can hold the thread for seconds, so the bar is
       frozen on screen exactly when the user is most likely to think the app died. The stand-in
       window below draws the same segment from its own dispatcher thread, so motion does not depend
       on this thread reaching a frame. It is skipped in self-test mode, where benches and probes
       measure this window's own frame timing. */
    if(!Program.IsSelfTest||FileOpenBarStandInSelfTest){StartFileOpenBarStand(segment);/* the stand-in draws the whole bar (track + segment), so this one must not draw a second copy on top of it */segment.Visibility=Visibility.Collapsed;}
}
void StopFileOpenBarAnimation(Border segment){
    /* The stand-in is a separate window; it is stopped even when the template part was not found,
       so "leave the indeterminate state" always takes the stand-in down with it. The animation is
       stopped on the transform it was started on, not on whatever this lookup returned: a Forever
       animation that keeps running holds MediaContext's render loop open, and every render of a
       4M-character viewport costs ~0.5 s (see fileOpenBarShift). */
    var shift=segment==null?null:segment.RenderTransform as TranslateTransform;
    if(shift!=null)shift.BeginAnimation(TranslateTransform.XProperty,null);
    if(fileOpenBarShift!=null&&!Object.ReferenceEquals(fileOpenBarShift,shift))fileOpenBarShift.BeginAnimation(TranslateTransform.XProperty,null);
    fileOpenBarShift=null;
    if(segment!=null)segment.Visibility=Visibility.Collapsed;
    StopFileOpenBarStand();
}
void StartFileOpenScreenProgress(){if(fileOpenLabel!=null)fileOpenLabel.Text=T("화면 구성 중…");SetFileOpenIndeterminate(true);}
void StopFileOpenScreenProgress(){SetFileOpenIndeterminate(false);}
readonly Queue<FileOperation> fileOpenQueue=new Queue<FileOperation>();bool documentOperationBusy;CancellationTokenSource fileOpenCancellation;bool fileOpenRunning,fileOpenClosed;internal Exception LastFileOpenError;Border fileOpenOverlay;TextBlock fileOpenLabel;ProgressBar fileOpenProgress;DispatcherTimer fileOpenOverlayDelay;
void EnsureFileOpenOverlay(){if(fileOpenOverlay==null){var panel=new StackPanel {Width=340,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};fileOpenLabel=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)};panel.Children.Add(fileOpenLabel);fileOpenProgress=new ProgressBar {Height=5,IsIndeterminate=false,Minimum=0,Maximum=100,Value=0,Margin=new Thickness(0,0,0,16),Template=FileOpenProgressTemplate()};fileOpenProgress.SizeChanged+=delegate{if(fileOpenProgress!=null&&fileOpenProgress.IsIndeterminate)SetFileOpenIndeterminate(true);};panel.Children.Add(fileOpenProgress);var cancel=new Button {Content=T("취소"),HorizontalAlignment=HorizontalAlignment.Right,MinWidth=70};cancel.Click+=delegate{CancelFileOpen();};panel.Children.Add(cancel);fileOpenOverlay=new Border {Child=panel};Panel.SetZIndex(fileOpenOverlay,200);}fileOpenOverlay.SetResourceReference(Border.BackgroundProperty,"Paper");fileOpenLabel.SetResourceReference(TextBlock.ForegroundProperty,"Ink");if(!area.Children.Contains(fileOpenOverlay))area.Children.Add(fileOpenOverlay);ResetFileOpenProgress();/* short opens finish before the overlay appears; only slow ones show progress and Cancel */fileOpenOverlay.Visibility=Visibility.Collapsed;if(fileOpenOverlayDelay==null){fileOpenOverlayDelay=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(400)};fileOpenOverlayDelay.Tick+=delegate{fileOpenOverlayDelay.Stop();if(documentOperationBusy&&fileOpenOverlay!=null)fileOpenOverlay.Visibility=Visibility.Visible;};}fileOpenOverlayDelay.Stop();fileOpenOverlayDelay.Start();documentOperationBusy=true;LayoutMode();}
void HideFileOpenOverlay(){StopFileOpenScreenProgress();/* the stand-in is a separate window on its own thread: stop it here too, so a value that never reached StopFileOpenBarAnimation (no template part, guard flag) cannot leave it drawing */StopFileOpenBarStand();if(fileOpenOverlayDelay!=null)fileOpenOverlayDelay.Stop();if(fileOpenOverlay!=null)fileOpenOverlay.Visibility=Visibility.Collapsed;MeasureFileOpen("overlay-hidden");}
/* Complete queued conversions that a save is waiting on as cancelled: Clear alone left the save's
   task pending forever when its operation had not started yet. */
void CancelFileOpen(){while(fileOpenQueue.Count>0){var pending=fileOpenQueue.Dequeue();if(pending.Completion!=null)pending.Completion.TrySetResult(false);}if(fileOpenCancellation!=null)fileOpenCancellation.Cancel();fileOpenLayoutReady=false;HideFileOpenOverlay();documentOperationBusy=false;LayoutMode();}
void QueueFileOpen(string name){EnqueueFileOperation(new FileOperation {Name=name});}
void QueueModeConversion(){EnqueueFileOperation(new FileOperation {ConvertMode=true});}
// A format-changing Save As uses the conversion queue: the conversion, the background drain, the
// file write and the unlock all run inside one queued operation, so the overlay covers the whole
// save and a later open cannot start before this one has written and released the document.
FileOperation QueueModeConversionForSaveAsync(bool targetPlain,string target,Encoding encoding){
    /* RunContinuationsAsynchronously: the awaiting save (SaveToPathAsync -> SaveAsync) resumes on
       this dispatcher, and its conflict retry reads documentOperationBusy. Inline continuations
       would run that retry before the drain's finally released the document (the completion is
       set after CompleteFileOpenUnlock, so both halves of the fix agree). */
    var operation=new FileOperation {ConvertMode=true,SaveMode=true,TargetPlain=targetPlain,SaveTarget=target,SaveEncoding=encoding,Document=activeDocument,Completion=new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)};
    if(fileOpenClosed)operation.Completion.TrySetResult(false);else EnqueueFileOperation(operation);
    return operation;
}
void EnqueueFileOperation(FileOperation operation){if(fileOpenClosed)return;if(!operation.ConvertMode)MeasureFileOpen("request");fileOpenQueue.Enqueue(operation);if(fileOpenRunning)return;fileOpenRunning=true;Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(async delegate{await DrainFileOpens();}));}
async Task DrainFileOpens(){
    try{
        while(fileOpenQueue.Count>0&&!fileOpenClosed){
            var operation=fileOpenQueue.Dequeue();fileOpenUnlocked=false;fileOpenLayoutReady=false;ResetFileOpenProgress();
            using(var cancellation=new CancellationTokenSource()){
                fileOpenCancellation=cancellation;
                /* A save's waiter resumes on this dispatcher (SaveToPathAsync awaits Completion) and
                   its SaveAsync conflict retry checks documentOperationBusy. Completing a save inside
                   the try posts that continuation before the finally unlocks the document, so the
                   retry can run while the operation is still busy and return false with no message.
                   The result is therefore held and posted after CompleteFileOpenUnlock below; the
                   source is also created with RunContinuationsAsynchronously, so a waiter never runs
                   inline on the turn that completes it. */
                bool complete=false,completeResult=false;Exception completeError=null;
                try{
                    LastFileOpenError=null;
                    /* A save conversion that finds the tab already in its target mode (a queued mode
                       toggle ran first) has nothing to convert. A tab switch after the request owns
                       no conversion: without this the write below could target another tab's text. */
                    bool owned=!operation.SaveMode||Object.ReferenceEquals(operation.Document,activeDocument);
                    if(operation.SaveMode&&!owned){complete=true;completeResult=false;}
                    else if(operation.SaveMode&&operation.TargetPlain==PlainText){/* only the write below */}
                    else if(operation.ConvertMode){
                        operation.Started=true;
                        /* A save conversion holds its mode entry until the file write succeeds; a
                           user toggle records it at once. */
                        saveConversionDeferred=operation.SaveMode;
                        try{await ConvertTextModeAsync(cancellation.Token);}
                        finally{saveConversionDeferred=false;}
                    }
                    else await OpenPreparedFileAsync(operation.Name,operation.CodePage,cancellation.Token,operation);
                    if(operation.SaveMode&&owned){
                        /* Write before the unlock so the overlay and the busy guard cover the file
                           write too, and so a queued operation cannot start in between. The tab must
                           still be the one the save started on (a switch during the conversion must
                           not write another document to this path). */
                        bool ready=operation.TargetPlain==PlainText&&Object.ReferenceEquals(operation.Document,activeDocument);
                        if(ready){
                            /* A writer that reports false without throwing must roll back like a
                               thrown failure: a held mode entry is never recorded as saved. */
                            bool wrote=WriteConvertedToPath(operation.SaveTarget,operation.SaveEncoding,true);
                            if(wrote)FinishSaveConversion(operation.Document);else RollbackSaveConversion(operation.Document);
                            ready=wrote;
                        }else{
                            /* A view or document change interrupted the conversion (the commit and
                               this check run in one turn, so a committed conversion is always
                               ready) and TextMode.ReportAbortedTextConversion already showed its
                               own box; the save's follow-up notice would be a second modal for one
                               Ctrl+S. */
                            RollbackSaveConversion(operation.Document);
                            if(operation.Started)LastSaveReported=true;
                        }
                        complete=true;completeResult=ready;
                    }else if(operation.Completion!=null&&!operation.SaveMode)operation.Completion.TrySetResult(operation.TargetPlain==PlainText);
                }catch(OperationCanceledException){
                    /* A cancel after the conversion committed still has to put the tab back; before
                       the commit there is nothing to roll back. */
                    if(operation.SaveMode){try{RollbackSaveConversion(operation.Document);}catch(Exception){/* the cancellation is the result; never hang the waiting save */}}
                    if(operation.Completion!=null){if(operation.SaveMode){complete=true;completeResult=false;}else operation.Completion.TrySetResult(false);}
                }
                catch(Exception error){
                    /* The write failed (encoder refusal, conflict, lock, cancellation of the lease):
                       the tab must not stay converted while path/saved still name the old file. */
                    if(operation.SaveMode){try{RollbackSaveConversion(operation.Document);}catch(Exception){/* keep the original save error */}}
                    LastFileOpenError=error;
                    if(operation.Completion!=null){if(operation.SaveMode){complete=true;completeError=error;}else operation.Completion.TrySetException(error);}
                    else if(!Program.IsSelfTest&&!fileOpenClosed){EnsureVisibleForeground();MessageBox.Show(this,T("파일을 열 수 없습니다.\n")+error.Message,"MDNote");}
                }finally{
                    fileOpenCancellation=null;                    /* A save that never started (already in the target mode) has no overlay to
                       release; every other operation unlocks here. */
                    if(!operation.SaveMode||operation.Started)CompleteFileOpenUnlock(operation);
                    if(complete&&operation.Completion!=null){if(completeError!=null)operation.Completion.TrySetException(completeError);else operation.Completion.TrySetResult(completeResult);}
                }
            }
        }
    }finally{fileOpenRunning=false;}
}
bool ActivateOpenFile(string full){foreach(var state in documents)if(String.Equals(state==activeDocument?path:state.Path,full,StringComparison.OrdinalIgnoreCase)){switchingTabs=true;documentTabs.SelectedItem=state.Item;switchingTabs=false;SwitchTab(state);return true;}return false;}
sealed class OpenedFile {internal string Text,Buffer,Newline,Fingerprint;internal Encoding Encoding;internal byte[] Bytes;/* The file's time and length read just before its bytes (FileChangeWatch.cs). */internal DiskStamp Stamp;}
/* `operation` is optional so probe/test callers that invoke the open directly keep their signature; without it the table-fit hold falls back to the current viewer. */
async Task OpenPreparedFileAsync(string name,int? codePage,CancellationToken cancellation,FileOperation operation=null){/* Direct callers (probes) skip the drain's per-operation reset; without it a second open keeps the first open's unlock/busy state. */fileOpenUnlocked=false;fileOpenLayoutReady=false;/* Every return below (an already-open file found during the read, a handover to a new window, a cancel) must release the overlay and the busy guard even when the caller is not DrainFileOpens; the unlock is idempotent. */try{await OpenPreparedFileCoreAsync(name,codePage,cancellation,operation);}finally{CompleteFileOpenUnlock(operation);}}
async Task OpenPreparedFileCoreAsync(string name,int? codePage,CancellationToken cancellation,FileOperation operation){string full=Path.GetFullPath(name);/* A folder dropped on the window used to reach the text reader and answer with its access-denied exception. Say it is a folder before the read and before the overlay. */if(Directory.Exists(full))throw new IOException(T("폴더는 열 수 없습니다. MDNote는 파일만 열 수 있습니다."));/* One file is one tab/window, the explicit-encoding route included: an already-open file switches to the tab or window that holds it instead of being read a second time (user rule A26, UI bug hunt 4 item 2). */if(ActivateOpenFile(full))return;if(ActivateFileInOtherWindow(full))return;/* The new-window decision waits for a successful read: a blank window created before it would stay as an empty "새 문서" when the read fails (a CP949 file opened as UTF-8). */EnsureFileOpenOverlay();fileOpenLabel.Text=T("파일을 읽는 중…")+"\n"+Path.GetFileName(full);LastFileOpenError=null;
OpenedFile loaded=operation==null?null:operation.Prepared;if(loaded==null)loaded=await Task.Run(delegate{cancellation.ThrowIfCancellationRequested();var result=new OpenedFile();result.Stamp=DiskStamp.Read(full,null);result.Text=codePage.HasValue?ReadTextFileEncodingVersion(full,codePage.Value,out result.Encoding,out result.Fingerprint,out result.Bytes):ReadTextFileVersion(full,out result.Encoding,out result.Fingerprint,out result.Bytes);MeasureFileOpen("read-done");cancellation.ThrowIfCancellationRequested();result.Newline=DetectNewline(result.Text);result.Buffer=NormalizeLineEndings(result.Text,result.Newline);if(result.Buffer==result.Text)result.Bytes=null;MeasureFileOpen("normalize-done");cancellation.ThrowIfCancellationRequested();return result;},cancellation);cancellation.ThrowIfCancellationRequested();if(fileOpenClosed)return;MeasureFileOpen("parse-done");PreparingFileOpenScreen();SetFileOpenProgress(72);/* Paint the preparation label and the parse share before the remaining synchronous UI work. */await Dispatcher.InvokeAsync(new Action(delegate{}),DispatcherPriority.Background);cancellation.ThrowIfCancellationRequested();if(fileOpenClosed||ActivateOpenFile(full)||ActivateFileInOtherWindow(full))return;bool blank=path==""&&text==""&&saved==""&&!recoveredDocument;if(!blank){/* The document state can change while the read awaited: a toggle of the new-window option during the open, or a first character typed into this blank tab, can make this window the wrong home now, and a tab created here would sit under a strip the option hides. The window and the already-read text are handed over only here, after the read succeeded. */if(!openInTabs){OpenFileInNewWindow(full,codePage,loaded);return;}CreateTab();}/* SwitchTab replaces area.Children; keep the busy overlay above the new surfaces. */if(fileOpenOverlay!=null&&!area.Children.Contains(fileOpenOverlay))area.Children.Add(fileOpenOverlay);MeasureFileOpen("create-tab-done");/* Bind the operation to the tab that receives the document, so a later tab switch is detectable. */if(operation!=null)operation.Document=activeDocument;pendingLoadStamp=loaded.Stamp;try{ApplyLoadedFile(full,loaded.Text,loaded.Encoding,loaded.Fingerprint,loaded.Bytes,loaded.Buffer,loaded.Newline);}finally{pendingLoadStamp=null;}if(operation!=null)operation.Prepared=null;/* the new window owns the text now */MeasureFileOpen("apply-loaded-done");RememberRecent(full);CaptureTab();RefreshTabCaption();MeasureFileOpen("open-refresh-done");/* Render already refreshed the outline for this document. */await Dispatcher.InvokeAsync(new Action(delegate{}),DispatcherPriority.Background);/* Keep the overlay/input guard through the first render, not just through parsing. */await Dispatcher.InvokeAsync(new Action(delegate{if(fileOpenClosed)return;UpdateLayout();MeasureFileOpen("first-layout-done");}),DispatcherPriority.Loaded);await DrainFileOpenBackgroundWorkAsync(operation);await Dispatcher.InvokeAsync(new Action(delegate{if(fileOpenClosed)return;if(OwnsOpenView(operation))PrepareFileOpenLayout();CompleteFileOpenUnlock(operation);}),DispatcherPriority.Loaded);}
/* One place moves a file open to a new window: the entry check and the re-check after the awaited read have to agree, or a tab lands in a window whose strip the option hides. The new window carries the same operation shape the entry path always used. */
void OpenFileInNewWindow(string full,int? codePage,OpenedFile prepared=null){var other=new MainWindow();other.SetOpenInTabs(false);other.Show();other.EnsureVisibleForeground();other.EnqueueFileOperation(new FileOperation {Name=full,CodePage=codePage,Prepared=prepared});}
void ApplyLoadedFile(string name,string value,Encoding encoding,string fingerprint,byte[] originalBytes,string normalizedBuffer=null,string newline=null){recoveredDocument=false;documentEncoding=encoding;documentFingerprint=fingerprint;documentFingerprintPath=Path.GetFullPath(name);TakeLoadedFileStamp(documentFingerprintPath);documentNewline=newline??DetectNewline(value);string buffer=normalizedBuffer??NormalizeLineEndings(value,documentNewline);documentOriginalBytes=normalizedBuffer!=null?originalBytes:buffer==value?null:originalBytes;textMode=String.Equals(Path.GetExtension(name),".txt",StringComparison.OrdinalIgnoreCase);path=Path.GetFullPath(name);saved=buffer;loading=true;preview.IsChecked=false;loading=false;bool previous=applyingLoadedFile;applyingLoadedFile=true;/* 11-01: a .txt opened with editing=true while the screen showed the read view, so the first Ctrl+E only turned editing off (a no-op on screen); it now opens like .md (VM trace 16:06). */bool targetEditing=alwaysEditing;try{loading=true;editing=targetEditing;loading=false;SetText(buffer);SetMode(targetEditing);source.ScrollToHome();}finally{applyingLoadedFile=previous;}RefreshSearch();}
/* ---- Stand-in window for the indeterminate progress bar --------------------------------
   The bar lives in this window and is animated by a render-clock DoubleAnimation, which only
   paints when this window's UI thread reaches a render pass. The drain that follows the first
   layout runs WPF's own pagination at Background priority; a single table or one giant paragraph
   can hold the thread for 0.5-8 s, and the bar then looks frozen (user report on the 4M body:
   "화면 구성 중" with a bar that does not move). A dispatcher timer cannot help, it is the same
   thread. So the same 5 px track and 30% accent segment are drawn by a borderless, click-through
   window on its own STA thread, positioned over the in-window bar. It stops as soon as the
   in-window bar leaves the indeterminate state, so nothing extra is on screen at any other time.
   Look is unchanged: same height, colours, corner radius, easing and 1.4 s pass. */
Window fileOpenBarStand;
System.Threading.Thread fileOpenBarStandThread;
System.Windows.Threading.Dispatcher fileOpenBarStandDispatcher;
volatile bool fileOpenBarStandWanted;
/* Whether the stand-in may be on screen: the main window is foreground and not minimized. Read by
   the stand-in thread only (plain bool), written by the UI thread from the window's own events. */
volatile bool fileOpenBarStandShow=true;
/* Start/Stop/thread cleanup all take this lock: the old code read the thread field twice (a null
   write from the thread's finally could turn the second read into an NRE) and a Stop followed by
   a quick Start could leave an exiting thread owning the "alive" field, so the window closed and
   the next open had no bar at all. */
readonly object fileOpenBarStandSync=new object();
bool fileOpenBarStandEventsHooked;
System.Windows.Media.Color fileOpenBarAccent=System.Windows.Media.Color.FromRgb(37,99,235);
System.Windows.Media.Color fileOpenBarTrack=System.Windows.Media.Color.FromRgb(199,206,217);
double fileOpenBarStandWidth=FileOpenBarFallbackWidth;
void StartFileOpenBarStand(Border segment){
    if(fileOpenProgress==null)return;
    HookFileOpenBarStandEvents();
    fileOpenBarStandWanted=true;fileOpenBarStandShow=IsActive&&WindowState!=WindowState.Minimized;
    // The colours come from the same resources the template uses, so a theme switch is followed.
    var accent=(SolidColorBrush)TryFindResource("Accent");if(accent!=null)fileOpenBarAccent=accent.Color;
    var track=(SolidColorBrush)TryFindResource("Line");if(track!=null)fileOpenBarTrack=track.Color;
    double width=fileOpenProgress.ActualWidth>0?fileOpenProgress.ActualWidth:FileOpenBarFallbackWidth;
    fileOpenBarStandWidth=width;
    UpdateFileOpenBarStandBounds();
    // A previous stand-in may still be winding down. It only closes its window after it has
    // decided to exit under the lock, so a thread found here keeps serving the new request.
    lock(fileOpenBarStandSync){
        if(fileOpenBarStandThread!=null&&fileOpenBarStandThread.IsAlive)return;
        var thread=new System.Threading.Thread(FileOpenBarStandMain);
        thread.IsBackground=true;
        thread.Name="MDNote file-open bar";
        try{thread.SetApartmentState(System.Threading.ApartmentState.STA);}catch(PlatformNotSupportedException){}
        fileOpenBarStandThread=thread;
        thread.Start();
    }
}
// The stand-in must not stay over another application after Alt+Tab, and it must never take a
// click: the window is created non-activating and click-through (see FileOpenBarStandMain) and it
// hides itself when this window is no longer the foreground window.
void HookFileOpenBarStandEvents(){
    if(fileOpenBarStandEventsHooked)return;
    fileOpenBarStandEventsHooked=true;
    Activated+=delegate{UpdateFileOpenBarStandForeground(true);};
    Deactivated+=delegate{UpdateFileOpenBarStandForeground(false);};
    StateChanged+=delegate{UpdateFileOpenBarStandForeground(IsActive);};
    Closed+=delegate{StopFileOpenBarStand();};
}
void UpdateFileOpenBarStandForeground(bool foreground){
    fileOpenBarStandShow=foreground&&WindowState!=WindowState.Minimized;
}
// Screen rect of the in-window bar, in device-independent pixels. Recomputed by the UI thread
// while the drain runs (the overlay is centred, so a resize moves it) and read by the stand-in
// thread only as plain ints.
volatile int fileOpenBarStandX,fileOpenBarStandY,fileOpenBarStandWidthDip;
// True once a real rect has been read. Until then the stand-in stays hidden instead of appearing at
// (0,0): the first indeterminate turn can arrive before the in-window bar has been laid out.
volatile bool fileOpenBarStandHasBounds;
void UpdateFileOpenBarStandBounds(){
    if(!fileOpenBarStandWanted||fileOpenProgress==null||!fileOpenProgress.IsVisible)return;
    try{
        var origin=fileOpenProgress.PointToScreen(new Point(0,0));
        var source=PresentationSource.FromVisual(fileOpenProgress);
        if(source==null||source.CompositionTarget==null)return;
        var toDevice=source.CompositionTarget.TransformToDevice;
        if(!(toDevice.M11>0)||!(toDevice.M22>0))return;
        fileOpenBarStandX=(int)Math.Round(origin.X/toDevice.M11);
        fileOpenBarStandY=(int)Math.Round(origin.Y/toDevice.M22);
        fileOpenBarStandWidthDip=(int)Math.Round(fileOpenProgress.ActualWidth);
        if(fileOpenBarStandWidthDip>0)fileOpenBarStandHasBounds=true;
    }catch(InvalidOperationException){}
}
void StopFileOpenBarStand(){
    /* Only flags are set here. A posted Close used to run on the stand-in thread after a quick Start had
       already asked for the bar again: the thread stayed alive, its window had been closed, and the next
       open had no bar at all (and the loop never re-showed it). The loop below notices the flag within one
       40 ms turn, exits, and closes the window in its finally. */
    fileOpenBarStandWanted=false;fileOpenBarStandShow=false;
}
void CloseFileOpenBarStand(){
    var window=fileOpenBarStand;fileOpenBarStand=null;
    if(window!=null){try{window.Hide();window.Close();}catch(InvalidOperationException){}}
}
/* Screen-space helpers. The stand-in keeps Topmost so it can sit over the in-window bar, but it is
   hidden the moment this window stops being the foreground window (UpdateFileOpenBarStandForeground),
   so Alt+Tab does not leave a bar floating over another application, and its extended style keeps it
   out of the taskbar, out of activation (WS_EX_NOACTIVATE) and out of the hit test
   (WS_EX_TRANSPARENT): the old Topmost window could take the click meant for the application under it. */
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern int GetWindowLong(IntPtr hwnd,int index);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern int SetWindowLong(IntPtr hwnd,int index,int value);
const int GwlExStyle=-20;
const int WsExTransparent=0x20,WsExToolWindow=0x80,WsExNoActivate=0x08000000;
void MakeFileOpenStandPassive(IntPtr handle){
    if(handle==IntPtr.Zero)return;
    int style=GetWindowLong(handle,GwlExStyle);
    int wanted=style|WsExTransparent|WsExToolWindow|WsExNoActivate;
    if(wanted!=style)SetWindowLong(handle,GwlExStyle,wanted);
    FileOpenBarStandLastStyle=GetWindowLong(handle,GwlExStyle);
}
/* Probe seams: the stand-in is normally skipped in self-test, so --userfix-probe turns
   FileOpenBarStandInSelfTest on and reads these to check the start/stop handshake and the window's
   style bits (what makes it click-through and non-activating). */
internal static bool FileOpenBarStandInSelfTest;
internal static int FileOpenBarStandLastStyle;
internal static bool FileOpenBarStandPlacedBeforeShow;
internal static int FileOpenBarStandShowCount;
internal bool HasFileOpenBarStandWindow(){
    lock(fileOpenBarStandSync)return fileOpenBarStand!=null;
}
internal bool HasFileOpenBarStandThread(){
    lock(fileOpenBarStandSync)return fileOpenBarStandThread!=null&&fileOpenBarStandThread.IsAlive;
}
void FileOpenBarStandMain(){
    var dispatcher=System.Windows.Threading.Dispatcher.CurrentDispatcher;
    lock(fileOpenBarStandSync)fileOpenBarStandDispatcher=dispatcher;
    Window window=null;
    try{
        double width=fileOpenBarStandWidthDip>0?fileOpenBarStandWidthDip:FileOpenBarFallbackWidth;
        window=new Window { WindowStyle=WindowStyle.None, ResizeMode=ResizeMode.NoResize, ShowInTaskbar=false, ShowActivated=false, Topmost=true, Focusable=false, AllowsTransparency=true, Background=Brushes.Transparent, IsHitTestVisible=false, Width=width, Height=5 };
        var grid=new Grid { ClipToBounds=true };
        grid.Children.Add(new Border { CornerRadius=new CornerRadius(2), Background=new SolidColorBrush(fileOpenBarTrack) });
        var segment=new Border { CornerRadius=new CornerRadius(2), Background=new SolidColorBrush(fileOpenBarAccent), HorizontalAlignment=HorizontalAlignment.Left, Width=Math.Max(24.0,width*0.3) };
        var shift=new TranslateTransform();segment.RenderTransform=shift;
        grid.Children.Add(segment);
        window.Content=grid;
        var animation=new System.Windows.Media.Animation.DoubleAnimation(-segment.Width,width,TimeSpan.FromSeconds(1.4));
        animation.RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever;
        animation.EasingFunction=new System.Windows.Media.Animation.CubicEase {EasingMode=System.Windows.Media.Animation.EasingMode.EaseInOut};
        shift.BeginAnimation(TranslateTransform.XProperty,animation);
        lock(fileOpenBarStandSync)fileOpenBarStand=window;
        // Placed before the first Show so the window never appears at (0,0) for a frame.
        double placedWidth=width,placedLeft=fileOpenBarStandX,placedTop=fileOpenBarStandY;
        window.Width=placedWidth;window.Left=placedLeft;window.Top=placedTop;
        // The HWND exists only after Show or EnsureHandle; creating it here lets the passive styles
        // be in place for the very first Show (no activatable frame, no hit test, no taskbar entry).
        var interop=new System.Windows.Interop.WindowInteropHelper(window);
        MakeFileOpenStandPassive(interop.EnsureHandle());
        bool shown=false;
        // Keep the stand-in over the real bar: the overlay is centred, so a resize, a tab strip
        // appearing or a window move all change where it belongs. The window's own properties are
        // read back only through locals: Window.Width/Left are dispatcher-affine and this loop runs
        // on the stand-in thread.
        while(true){
            while(fileOpenBarStandWanted){
                int targetWidth=fileOpenBarStandWidthDip,targetX=fileOpenBarStandX,targetY=fileOpenBarStandY;
                if(targetWidth>0&&Math.Abs(placedWidth-targetWidth)>0.5){window.Width=targetWidth;placedWidth=targetWidth;}
                if(Math.Abs(placedLeft-targetX)>0.5){window.Left=targetX;placedLeft=targetX;}
                if(Math.Abs(placedTop-targetY)>0.5){window.Top=targetY;placedTop=targetY;}
                bool wantShow=fileOpenBarStandShow&&fileOpenBarStandHasBounds;
                if(wantShow&&!shown){
                    /* Show must see the placement already applied: the window is created at WPF's default
                       position (0,0) and painting it there first is the top-left flash the review named. */
                    FileOpenBarStandPlacedBeforeShow=!Double.IsNaN(window.Left)&&window.Left==placedLeft&&!Double.IsNaN(window.Top)&&window.Top==placedTop;
                    FileOpenBarStandShowCount++;window.Show();shown=true;
                }
                else if(!wantShow&&shown){window.Hide();shown=false;} /* the in-window bar keeps drawing; only the copy over another app is removed */
                dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.SystemIdle,new Action(delegate{}));
                System.Threading.Thread.Sleep(40);
            }
            /* Deciding to exit and clearing the field happen under the same lock Start takes, so a
               Stop followed by a quick Start either keeps this window serving or sees no thread and
               starts a fresh one; it can no longer own a thread whose window is already closed. */
            lock(fileOpenBarStandSync){
                if(fileOpenBarStandWanted)continue;
                if(Object.ReferenceEquals(fileOpenBarStandThread,System.Threading.Thread.CurrentThread))fileOpenBarStandThread=null;
                break;
            }
        }
        // Let the last layout and paint land before the window goes away, so the hand-over back to
        // the in-window bar (which is made visible again by StopFileOpenBarAnimation) is not a flicker.
        dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.SystemIdle,new Action(delegate{}));
    }catch(Exception){/* a stand-in that cannot be created must never break an open */}
    finally{
        try{CloseFileOpenBarStand();}catch(InvalidOperationException){}
        lock(fileOpenBarStandSync){
            if(Object.ReferenceEquals(fileOpenBarStandDispatcher,dispatcher))fileOpenBarStandDispatcher=null;
            if(Object.ReferenceEquals(fileOpenBarStandThread,System.Threading.Thread.CurrentThread))fileOpenBarStandThread=null;
        }
        /* This thread owns a dispatcher; without the shutdown the thread and its window's WPF
           resources stay alive after the last open. */
        try{dispatcher.InvokeShutdown();}catch(InvalidOperationException){}
    }
}
}}
