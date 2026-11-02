using System;using System.Windows;using System.Windows.Controls;using System.Windows.Media;using ICSharpCode.AvalonEdit;using ICSharpCode.AvalonEdit.Document;using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {
// Compatibility surface keeps editor commands independent of the text engine.
// AvalonEdit lays out only visible lines and maintains its line tree incrementally.
internal sealed class SourceEditor:TextEditor {
internal double MeasureMilliseconds;internal int MeasureCalls;protected override Size MeasureOverride(Size available){var watch=System.Diagnostics.Stopwatch.StartNew();try{return base.MeasureOverride(available);}finally{MeasureCalls++;MeasureMilliseconds+=watch.Elapsed.TotalMilliseconds;}}
internal event EventHandler SelectionChanged;internal event EventHandler<DocumentChangeEventArgs> ContentDelta;TextDocument trackedDocument;void TrackDocument(){if(trackedDocument!=null){trackedDocument.Changed-=DocumentDelta;trackedDocument.Changing-=DocumentChanging;trackedDocument.UpdateStarted-=DocumentUpdateStarted;trackedDocument.UpdateFinished-=DocumentUpdateFinished;}trackedDocument=Document;if(trackedDocument!=null){trackedDocument.UndoStack.SizeLimit=0;trackedDocument.Changed+=DocumentDelta;trackedDocument.Changing+=DocumentChanging;trackedDocument.UpdateStarted+=DocumentUpdateStarted;trackedDocument.UpdateFinished+=DocumentUpdateFinished;}lastSelectionStart=-2;lastSelectionLength=lastCaret=-1;typingCaret=-1;typingBroken=false;typedKeyArmed=false;typing=false;}void DocumentDelta(object sender,DocumentChangeEventArgs args){if(ContentDelta!=null)ContentDelta(this,args);}
// The app history groups one user action into one undo step. TextDocument.RunUpdate is exactly
// that action (a selection replace, a newline plus its indentation, a structural edit), so hand
// the update to DocumentHistory with the caret/selection it started and ended with. AvalonEdit's
// own undo stack, which groups the same way, is disabled here (SizeLimit=0).
void DocumentChanging(object sender,DocumentChangeEventArgs args){DocumentHistory.BeforeDocumentChange((TextDocument)sender,args.Offset,args.RemovalLength,args.InsertionLength);}
// The history entry is decided while `typing`/`typedKeyArmed` still describes the update: a caret
// the user moved (or a focus loss) since the last input starts a new undo step, even when it came
// back to the same spot.
void DocumentUpdateStarted(object sender,EventArgs e){DocumentHistory.BeginEditGroup((TextDocument)sender,SelectionStart,SelectionLength,typing||typedKeyArmed,typingBroken,typedBackspaceArmed);}
void DocumentUpdateFinished(object sender,EventArgs e){DocumentHistory.EndEditGroup(SelectionStart,SelectionLength);typingCaret=SelectionStart;typingBroken=false;typedKeyArmed=false;typedBackspaceArmed=false;typing=false;}
// Typing merges into one undo step until the caret moves, Delete, a paste/cut/drop, a command or
// a focus loss; a run whose first character replaced a selection keeps merging after it (a whole
// TextInput, IME commit included). A newline typed into the document starts a new entry.
bool typing;void TypingEntering(object sender,System.Windows.Input.TextCompositionEventArgs e){typing=true;}void TypingEntered(object sender,System.Windows.Input.TextCompositionEventArgs e){typing=false;}
// The caret the last update left. Any caret change outside an input (an arrow, a click, Home/End)
// ends the typing run, so the next input is its own undo step even when it lands on the same
// offset again. `typing` spans TextEntering..UpdateFinished, so the caret moves the input itself
// makes are never counted here (a real-IME click commit commits, then places the caret).
int typingCaret=-1;bool typingBroken;
// Paths that insert without raising TextEntering (a Backspace command, a late IME commit) arm
// this marker for the update they are about to open. UpdateFinished clears it, and a posted
// callback clears it when no update followed (a Backspace the preview swallowed).
bool typedKeyArmed;
// The same arm for the Backspace command, which the history reads to keep the key's replacement
// (a hidden marker run re-spaced around a closing-edge space) from folding into the typing run it
// stands after. It joins the run only by taking back the run's own characters.
bool typedBackspaceArmed;
// The layout seam is kept for the probes that describe the IME they simulate. The product rule
// no longer differs by language: every IME commit is typing.
internal Func<bool> NonKoreanImeLayout;
void MarkImeCommitText(){ArmTypedInput();}
// A handler that consumes the input (the newline normalizer) sets Handled after TypingEntering ran,
// so TextEntered never fires; clear the run marker or the next non-text edit is recorded as typing.
internal void EndTypingRun(){typing=false;typedKeyArmed=false;typedBackspaceArmed=false;}
// A key or command that ends the run even when it changes nothing (Delete, Undo/Redo).
internal void BreakTypingRun(){typing=false;typedKeyArmed=false;typedBackspaceArmed=false;typingBroken=true;}
// The typed marker for an update that has no TextEntering of its own. The posted clear covers a
// command the preview swallowed before it opened an update; a real update clears it in
// UpdateFinished.
void ArmTypedInput(){typedKeyArmed=true;Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,new Action(delegate{typedKeyArmed=false;}));}
// A Backspace is typing like ArmTypedInput says, but its marker move is a separate history entry;
// the same posted clear covers a key the preview swallowed without opening an update.
void ArmTypedBackspace(){ArmTypedInput();typedBackspaceArmed=true;Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,new Action(delegate{typedBackspaceArmed=false;}));}
// A Backspace is part of the typing step it continues; Delete, paste and cut end the step. The
// command is seen here even when the surface handles it (a table cell or a hidden marker), and
// before any of its document changes run.
void TypingCommandExecuting(object sender,System.Windows.Input.ExecutedRoutedEventArgs e){
    if(e.Command==System.Windows.Documents.EditingCommands.Backspace){ArmTypedBackspace();return;}
    if(e.Command==System.Windows.Documents.EditingCommands.Delete||e.Command==System.Windows.Input.ApplicationCommands.Delete||e.Command==System.Windows.Input.ApplicationCommands.Cut||e.Command==System.Windows.Input.ApplicationCommands.Paste)BreakTypingRun();
}
// Keyboard focus leaving the editor (another control, a menu, another window) ends the step, the
// same break a caret move makes. Focus moving inside the editor (TextArea <-> TextEditor) keeps
// IsKeyboardFocusWithin true and is not a leave.
void TypingFocusLost(object sender,System.Windows.Input.KeyboardFocusChangedEventArgs e){
    if(IsKeyboardFocusWithin)return;
    StopPressWatch();DisarmButtonCaret();
    BreakTypingRun();
}
// The routed TextInput event ends after TextArea.OnTextInput ran PerformTextInput. A handler that
// consumed the input (the live preview keeps text out of a delimiter row) raises no TextEntered,
// so this is where the run marker is cleared; handledEventsToo covers exactly that case.
void TypingInputEnded(object sender,System.Windows.Input.TextCompositionEventArgs e){typing=false;}
// A drag that starts in this editor moves text: AvalonEdit records the drop insert while the
// drag runs and removes the source selection after it returns, i.e. two document changes. The
// start marker (and the token it puts on the drag data) lets the drop handler perform both edits
// as one action instead. The token keeps a later unrelated or other-window drop from being read
// as this drag.
internal const string DragTokenFormat="MDNoteDragSourceToken";
internal int DragSourceStart=-1,DragSourceLength;internal TextDocument DragSourceDocument;internal string DragSourceToken;int dragSourceTick;
internal void NoteDragSource(){DragSourceStart=SelectionStart;DragSourceLength=SelectionLength;DragSourceDocument=Document;DragSourceToken=Guid.NewGuid().ToString("N");dragSourceTick=Environment.TickCount;}
internal void ClearDragSource(){DragSourceStart=-1;DragSourceLength=0;DragSourceDocument=null;DragSourceToken=null;}
internal bool DragSourceActive(){return DragSourceStart>=0&&DragSourceLength>0&&Object.ReferenceEquals(DragSourceDocument,Document)&&unchecked(Environment.TickCount-dragSourceTick)<60000&&DragSourceStart+DragSourceLength<=Document.TextLength;}
void DragCopying(object sender,System.Windows.DataObjectCopyingEventArgs e){
    if(!e.IsDragDrop){ClearDragSource();return;}
    NoteDragSource();
    try{e.DataObject.SetData(DragTokenFormat,DragSourceToken);}catch(Exception){}
}
// Select() moves the selection and the caret, and both raise an event; the window reacts once
// per logical change, not twice for the same state.
int lastSelectionStart=-2,lastSelectionLength=-1,lastCaret=-1;
void NotifySelectionChanged(){int start=SelectionStart,length=SelectionLength,caret=CaretIndex;if(start==lastSelectionStart&&length==lastSelectionLength&&caret==lastCaret)return;lastSelectionStart=start;lastSelectionLength=length;lastCaret=caret;if(!typing&&typingCaret>=0&&caret!=typingCaret)typingBroken=true;if(SelectionChanged!=null)SelectionChanged(this,EventArgs.Empty);}
internal SourceEditor(){DocumentChanged+=delegate{TrackDocument();};TrackDocument();Options.EnableHyperlinks=false;Options.EnableEmailHyperlinks=false;Options.AllowScrollBelowDocument=false;Options.ConvertTabsToSpaces=false;TextArea.SelectionChanged+=delegate{NotifySelectionChanged();};TextArea.Caret.PositionChanged+=delegate{NotifySelectionChanged();ScheduleCompositionFont();CaretPositionChangedForCommit(null,EventArgs.Empty);};TextArea.TextView.VisualLinesChanged+=delegate{ScheduleCompositionFont();};TextArea.TextEntering+=ImeResultEntering;TextArea.TextEntering+=TypingEntering;TextArea.TextEntered+=TypingEntered;TextArea.TextEntering+=CompositionCommitEntering;TextArea.AddHandler(System.Windows.Input.TextCompositionManager.TextInputEvent,new System.Windows.Input.TextCompositionEventHandler(TypingInputEnded),true);System.Windows.Input.CommandManager.AddPreviewExecutedHandler(TextArea,TypingCommandExecuting);TextArea.PreviewLostKeyboardFocus+=ImePreviewLostKeyboardFocus;TextArea.LostKeyboardFocus+=TypingFocusLost;TextArea.LostKeyboardFocus+=delegate{Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{RefreshCompositionState("lost focus");}));};System.Windows.DataObject.AddCopyingHandler(TextArea,DragCopying);TextArea.GotKeyboardFocus+=delegate{HookIme();if(imeSource!=null)QueueCompositionFont(imeSource.Handle);ScheduleCompositionFont();};TextArea.PreviewMouseDown+=ImeMouseDown;/* A surface that leaves the tree (tab switch or close) must keep no window-wide mouse handler and no dispatcher timer: both hold this editor, and the window outlives its tabs. The tab is reused, so Loaded re-hooks the IME and re-arms the composition timeout. */Loaded+=delegate{HookIme();if(imeParked!=null)ArmImeTimeout();};Unloaded+=delegate{StopPressWatch();DisarmButtonCaret();if(imeTimer!=null)imeTimer.Stop();UnhookIme();};PresentationSource.AddSourceChangedHandler(this,ImeSourceChanged);if(ImeTrace.Enabled)TraceCompositionEvents();}
// MDNOTE_IME_TRACE: this surface composes in the IMM32 window (WPF's input method is suspended),
// so WPF raises no TextInputStart/Update and TextInput only when a commit reaches the TextArea.
// Attach on the focused element itself, again for already-handled events, and log the
// AvalonEdit TextEntering/TextEntered pair to prove which handler the commit really reaches.
void TraceCompositionEvents(){
    TraceRoutedComposition(TextArea,"source TextInput",System.Windows.Input.TextCompositionManager.PreviewTextInputEvent);
    TraceRoutedComposition(TextArea,"source TextInputStart",System.Windows.Input.TextCompositionManager.PreviewTextInputStartEvent);
    TraceRoutedComposition(TextArea,"source TextInputUpdate",System.Windows.Input.TextCompositionManager.PreviewTextInputUpdateEvent);
    TextArea.TextEntering+=delegate(object sender,System.Windows.Input.TextCompositionEventArgs args){ImeTrace.Write("source TextEntering text="+TraceText(args.Text)+" handled="+args.Handled);};
    TextArea.TextEntered+=delegate(object sender,System.Windows.Input.TextCompositionEventArgs args){ImeTrace.Write("source TextEntered text="+TraceText(args.Text));};
}
// handledEventsToo=true: AvalonEdit and WPF items handle composition events before non-preview
// handlers see them, so a plain handler subscription could hide the commit completely.
static void TraceRoutedComposition(System.Windows.UIElement target,string label,System.Windows.RoutedEvent routedEvent){
    target.AddHandler(routedEvent,new System.Windows.Input.TextCompositionEventHandler(delegate(object sender,System.Windows.Input.TextCompositionEventArgs args){ImeTrace.Write(label+" text="+TraceText(args.Text)+" handled="+args.Handled);}),true);
}
internal static string TraceText(string value){return value==null?"<null>(0)":"["+value+"]("+value.Length+")";}
internal bool AcceptsReturn {get;set;}internal bool AcceptsTab {get;set;}
[System.Runtime.InteropServices.DllImport("imm32.dll")]static extern IntPtr ImmGetContext(IntPtr hWnd);[System.Runtime.InteropServices.DllImport("imm32.dll")]static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);[System.Runtime.InteropServices.DllImport("imm32.dll")]static extern bool ImmReleaseContext(IntPtr hWnd,IntPtr hIMC);[System.Runtime.InteropServices.DllImport("imm32.dll")]static extern bool ImmNotifyIME(IntPtr hIMC,int action,int index,int value);
// AvalonEdit composes in the IMM32 window, so pending syllables are not in the document yet.
// Commit them before save, mode switches and commands read the text. CPS_COMPLETE alone is not
// enough for the TSF-backed Korean IME (it accepts the notification but sends no result), so
// CommitCompositionText moves the composition string into the document first.
internal void CompleteComposition(){RequestCompositionCommit(false,"complete");}
internal void CompleteCompositionForViewSwitch(){RequestCompositionCommit(true,"view switch");}
void RequestCompositionCommit(bool forceWindowCommit,string reason){CompositionRequests++;CompositionRequestCaret=CaretOffset;if(!forceWindowCommit&&!TextArea.IsKeyboardFocusWithin)return;if(CommitCompositionText(reason))return;CompleteWindowComposition(TextArea);}
// View switches restore the editing focus after the menu closes, then commit the window's
// IMM context even if WPF's focus/composition flags have not caught up. Also used for rich TSF.
internal static void CompleteWindowComposition(Visual owner){
    var window=PresentationSource.FromVisual(owner) as System.Windows.Interop.HwndSource;
    if(window==null){if(ImeTrace.Enabled)ImeTrace.Write("ImmGetContext skipped: no window for "+owner.GetType().Name);return;}
    IntPtr context=ImmGetContext(window.Handle);
    if(ImeTrace.Enabled)ImeTrace.Write("ImmGetContext hwnd=0x"+window.Handle.ToInt64().ToString("X")+" hIMC=0x"+context.ToInt64().ToString("X")+" owner="+owner.GetType().Name);
    if(context==IntPtr.Zero)return;
    try{
        bool notified=ImmNotifyIME(context,0x15,1,0);
        if(ImeTrace.Enabled)ImeTrace.Write("ImmNotifyIME CPS_COMPLETE hIMC=0x"+context.ToInt64().ToString("X")+" result="+notified);
    }finally{ImmReleaseContext(window.Handle,context);}
}
// Observable truth from the real run: CPS_COMPLETE returns true and no result text ever arrives,
// so the syllable is still in the window's composition string when the menu opens. Take that
// string out of the same IMM context the IME writes to, cancel the composition so it cannot be
// delivered twice, and enter it through the TextArea's own text input handler
// (TextArea.PerformTextInput -> TextEntering/TextEntered, vendor TextArea.cs:890 and :903), which
// is the path a normal commit uses. Must run while the TextArea still owns keyboard focus:
// AvalonEdit's ImeSupport cancels the composition and releases the context from
// OnLostKeyboardFocus (vendor ImeSupport.cs:69-74). The native deactivation hook and the
// PreviewLostKeyboardFocus fallback must capture it before that bubbling focus-loss handler.
// CPS_CANCEL can synchronously send IME/focus messages; a nested request must not capture the
// same syllable or fall through to CPS_COMPLETE while the outer request still owns it.
bool imeCapturing;
internal bool CommitCompositionText(string reason){
    if(imeCapturing)return true;
    imeCapturing=true;
    try{return CaptureCompositionText(reason);}finally{imeCapturing=false;}
}
bool CaptureCompositionText(string reason){
    if(imeCommitPending>0){if(ImeTrace.Enabled)ImeTrace.Write("commit capture skipped reason="+reason+" pending="+imeCommitPending);return false;}
    if(Document==null)return false;
    if(!TextArea.IsKeyboardFocusWithin){if(ImeTrace.Enabled)ImeTrace.Write("commit capture skipped reason="+reason+" no focus");return false;}
    var window=PresentationSource.FromVisual(TextArea) as System.Windows.Interop.HwndSource;
    if(window==null){if(ImeTrace.Enabled)ImeTrace.Write("commit capture skipped reason="+reason+" no window");return false;}
    IntPtr context=ImmGetContext(window.Handle);
    if(context==IntPtr.Zero){if(ImeTrace.Enabled)ImeTrace.Write("commit capture skipped reason="+reason+" no context focus="+MainWindow.FocusName());return false;}
    int bytes;string text=null;bool cancelled=false;int after;int index=0x0008;
    try{
        bytes=ImmGetCompositionStringW(context,index,IntPtr.Zero,0);
        // Some IMEs keep only the reading string; a Korean syllable is the same text in both.
        if(bytes<=0){index=0x0001;bytes=ImmGetCompositionStringW(context,index,IntPtr.Zero,0);}
        if(bytes>0){
            IntPtr buffer=System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
            try{int read=ImmGetCompositionStringW(context,index,buffer,bytes);if(read>0)text=System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer,read/2);}
            finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);}
        }
        // Some IMM implementations count a terminating NUL in the length; never insert it.
        if(text!=null&&text.Length>0&&text[text.Length-1]=='\0')text=text.Substring(0,text.Length-1);
        // The window hook, the menu hook and the deferred retry all reach this routine for the
        // same composition. If the IME ignored CPS_CANCEL and still reports the text we already
        // inserted, do not insert it a second time (a new composition sends WM_IME_STARTCOMPOSITION,
        // which clears the marker).
        bool duplicate=!String.IsNullOrEmpty(text)&&!imeComposing&&String.Equals(text,imeCapturedText,StringComparison.Ordinal)&&unchecked(Environment.TickCount-imeCapturedTick)<5000;
        if(duplicate){
            ImmNotifyIME(context,0x15,0x4,0);
            imeComposing=false;imeCommitPending=0;imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();
            if(ImeTrace.Enabled)ImeTrace.Write("commit capture duplicate reason="+reason+" text="+TraceText(text));
            return true;
        }
        // Cancel only what was captured; without text the IME may still deliver a normal result.
        if(!String.IsNullOrEmpty(text))cancelled=ImmNotifyIME(context,0x15,0x4,0);
        after=ImmGetCompositionStringW(context,0x0008,IntPtr.Zero,0);
        if(ImeTrace.Enabled)ImeTrace.Write("commit capture reason="+reason+" hIMC=0x"+context.ToInt64().ToString("X")+" kind="+(index==0x0008?"compstr":"readstr")+" bytes="+bytes+" text="+TraceText(text)+" cancel="+cancelled+" after="+after+" focus="+MainWindow.FocusName());
        if(ImeTrace.Enabled&&String.IsNullOrEmpty(text))DefaultImeWindowProbe(reason,bytes);
    }finally{ImmReleaseContext(window.Handle,context);}
    if(String.IsNullOrEmpty(text))return false;
    imeComposing=false;imeCommitPending=0;imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();
    // The captured composition string is a whole IME clause too (the real MS-IME click commit
    // takes this path). PerformTextInput raises TextEntering, so the history records it as typing
    // like any other committed text. The text becomes the duplicate marker only once it really
    // reached the document: a handler that refused the input (a structure guard) must still let
    // the IME's own result arrive once.
    int before=Document.TextLength;
    var version=Document.Version;
    TextArea.PerformTextInput(NormalizeCommittedNewlines(text));
    if(Object.ReferenceEquals(Document.Version,version)){imeCapturedText=null;}
    else{imeCapturedText=text;imeCapturedTick=Environment.TickCount;imeCapturedConsumed=0;}
    if(ImeTrace.Enabled)ImeTrace.Write("commit insert reason="+reason+" text="+TraceText(text)+" len="+before+"->"+Document.TextLength+" caret="+CaretOffset);
    return true;
}
// Trace only: shows whether the composition lives in the HwndSource's associated context or in
// the thread's default IME window context, which decides if CPS_COMPLETE ever had a chance.
void DefaultImeWindowProbe(string reason,int hwndBytes){
    IntPtr defaultWnd=ImmGetDefaultIMEWnd(IntPtr.Zero);
    IntPtr defaultContext=defaultWnd==IntPtr.Zero?IntPtr.Zero:ImmGetContext(defaultWnd);
    int defaultBytes=-1;
    try{if(defaultContext!=IntPtr.Zero)defaultBytes=ImmGetCompositionStringW(defaultContext,0x0008,IntPtr.Zero,0);}
    finally{if(defaultContext!=IntPtr.Zero)ImmReleaseContext(defaultWnd,defaultContext);}
    ImeTrace.Write("commit capture probe reason="+reason+" hwndBytes="+hwndBytes+" defaultWnd=0x"+defaultWnd.ToInt64().ToString("X")+" defaultHIMC=0x"+defaultContext.ToInt64().ToString("X")+" defaultBytes="+defaultBytes);
}
// The IME may have produced the result string after all, and WPF then raises TextInput on
// whoever owns keyboard focus at that moment (the menu item). The TextArea's own hook never sees
// it, so adopt it at the caret the composition belonged to. Only a counted pending result and a
// lost TextArea focus qualify; key/menu text with no IME result is never touched.
internal bool AcceptLateImeCommit(string text){
    if(imeCommitPending<=0||String.IsNullOrEmpty(text)||TextArea.IsKeyboardFocusWithin)return false;
    if(imeCommitTarget<0||!Object.ReferenceEquals(Document,imeCommitTargetDocument))return false;
    int at=Math.Max(0,Math.Min(imeCommitTarget,Document.TextLength)),caret=CaretOffset;
    // The same cell rule the redirect path uses: a syllable never lands outside the row's cells
    // (after raw -> live the remembered point can sit on a rendered table row).
    if(CellClamp!=null)at=CellClamp(at);
    at=Math.Max(0,Math.Min(at,Document.TextLength));
    string adopted=NormalizeCommittedNewlines(text);
    RestoreCommitTypingState();
    MarkImeCommitText();
    using(Document.RunUpdate()){InsertCommittedText(at,adopted);CaretOffset=caret>=at?caret+adopted.Length:caret;}
    imeCommitPending=Math.Max(0,imeCommitPending-text.Length);
    if(imeCommitPending<=0){imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();/* The counted result is in the document: a command parked behind it runs now, exactly as the other commit paths release it (the 500 ms timer is only the fallback). */if(!ImeBusy)RunParked();}
    if(ImeTrace.Enabled)ImeTrace.Write("late commit adopted text="+TraceText(text)+" at="+at+" pending="+imeCommitPending+" focus="+MainWindow.FocusName());
    return true;
}
// The IME result text is inserted verbatim by the late/click commit paths: they must land at
// the caret the composition belonged to, so they cannot go through PerformTextInput. A result
// that carries a line break must still use the document's own newline. A raw "\r" or "\n"
// recorded into a CRLF document leaves a mixed-line-ending edit in the history and in the rich
// source mirror; a save only normalizes the bytes on disk now (DocumentEncoding.SaveToPath
// leaves the buffer, its history and the mirror untouched), so the wrong newline would stay in
// every later undo/redo of that run. Reads only the first lines, never O(document) on an input
// path.
internal string NormalizeCommittedNewlines(string value){
    if(String.IsNullOrEmpty(value)||(value.IndexOf('\n')<0&&value.IndexOf('\r')<0))return value;
    string newline=DocumentNewline();
    return value.Replace("\r\n","\n").Replace('\r','\n').Replace("\n",newline);
}
internal string DocumentNewline(){
    var document=Document;
    if(document!=null){
        int limit=Math.Min(document.LineCount,16);
        for(int lineNumber=1;lineNumber<=limit;lineNumber++){var line=document.GetLineByNumber(lineNumber);if(line.DelimiterLength>0)return document.GetText(line.EndOffset,line.DelimiterLength);}
    }
    return Environment.NewLine;
}
// WM_IME_ENDCOMPOSITION does not reach this hook after AvalonEdit cancels the composition in
// OnLostKeyboardFocus, so imeComposing would stay true and ImeBusy would block later commands.
// Clear the latch only when the context no longer holds a composition and no result is pending.
// A result that never arrives (menu focus consumed the WM_CHAR) expires on the same 500 ms bound
// RunAfterComposition uses, so one lost commit cannot park commands forever.
internal void RefreshCompositionState(string reason){
    if(imeCommitPending>0&&unchecked(Environment.TickCount-imeCommitPendingTick)>500){
        // The result never arrives (menu focus consumed the WM_CHAR): the same 500 ms bound the
        // timer applies releases it here, so undo and save are not skipped forever by ImeBusy.
        imeCommitPending=0;imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();
        if(ImeTrace.Enabled)ImeTrace.Write("composing cleared reason="+reason+" pending expired");
        if(!ImeBusy)RunParked(); /* a command parked behind the lost result runs like the timer's release */
    }
    if(!imeComposing||imeCommitPending>0)return;
    if(ImeCompositionActive())return;
    imeComposing=false;
    if(ImeTrace.Enabled)ImeTrace.Write("composing cleared reason="+reason+" active=False pending=0");
}
// The counted result of a commit requested elsewhere never arrived (the menu consumed its
// WM_CHAR). Undo, save and every store-gated command would be skipped forever behind that count;
// release it on the same 500 ms bound the composition timer uses, without touching a composition
// that is still alive (ClearEndedComposition and the timer own that latch).
internal void ExpireLostCommitResult(string reason){
    if(imeCommitPending<=0||unchecked(Environment.TickCount-imeCommitPendingTick)<=500)return;
    imeCommitPending=0;imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();
    if(ImeTrace.Enabled)ImeTrace.Write("pending result expired reason="+reason);
    if(!ImeBusy)RunParked(); /* a command parked behind the lost result runs like the timer's release */
}
// The Korean IME sends WM_IME_ENDCOMPOSITION before the final result, and a result message can
// leave no composition string in the context. Once its counted text has been consumed, such a
// latch is stale: an empty IMM composition string means the IME owns nothing, so undo must not be
// parked behind it. A result whose characters were all delivered proves the composition is over
// even when no END arrived. A composition that never sent END (between START and its first
// update) is left alone: an edit then would run under the IME's store.
internal void ClearEndedComposition(){
    if(!imeComposing||imeCommitPending>0)return;
    if(ImeCompositionActive())return;
    if(!imeCompositionEnded&&!imeResultDelivered)return;
    imeComposing=false;
    if(ImeTrace.Enabled)ImeTrace.Write("composing cleared reason=ended composition active=False pending=0");
}
// The deferred switch gives up after its timeout because the IME can keep reporting a
// composition it will never deliver. Clear every latch so commands, saves and runs are not
// parked behind flags for a composition that is already gone.
internal void AbandonComposition(string reason){
    if(ImeTrace.Enabled)ImeTrace.Write("composition abandoned reason="+reason+" "+CompositionState());
    StopPressWatch();DisarmButtonCaret();
    imeComposing=false;imeCompositionEnded=false;imeCommitPending=0;imeResultDelivered=false;imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();
}
internal string CompositionState(){return "busy="+ImeBusy+" composing="+imeComposing+" pending="+imeCommitPending+" active="+ImeCompositionActive();}
// True while IMM holds an in-progress composition string. Covers input that never sent
// WM_IME_STARTCOMPOSITION to our hook (TSF paths), which the imeComposing flag would miss.
internal bool ImeCompositionActive(){return WindowCompositionActive(TextArea);}
internal static bool WindowCompositionActive(Visual owner){var window=PresentationSource.FromVisual(owner) as System.Windows.Interop.HwndSource;if(window==null)return false;IntPtr context=ImmGetContext(window.Handle);if(context==IntPtr.Zero)return false;try{return ImmGetCompositionStringW(context,0x0008,IntPtr.Zero,0)>0;}finally{ImmReleaseContext(window.Handle,context);}}
// IME guard. CPS_COMPLETE only requests the commit: the result string comes back later as
// WM_CHAR -> TextInput and replaces whatever is selected then. A command that selects text
// (live Bold wraps the caret word and selects it) must wait until the committed characters
// are in the document, or the late syllable overwrites its selection.
[System.Runtime.InteropServices.DllImport("imm32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern int ImmGetCompositionStringW(IntPtr hIMC,int index,IntPtr buffer,int length);
[System.Runtime.InteropServices.DllImport("imm32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern bool ImmSetCompositionFontW(IntPtr hIMC,ref ImeFont font);
[System.Runtime.InteropServices.DllImport("imm32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern bool ImmGetCompositionFontW(IntPtr hIMC,out ImeFont font);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,CharSet=System.Runtime.InteropServices.CharSet.Unicode)]struct ImeFont{public int lfHeight,lfWidth,lfEscapement,lfOrientation,lfWeight;public byte lfItalic,lfUnderline,lfStrikeOut,lfCharSet,lfOutPrecision,lfClipPrecision,lfQuality,lfPitchAndFamily;[System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr,SizeConst=32)]public string lfFaceName;}
bool imeComposing,imeCompositionEnded;int imeCommitPending;int imeCommitPendingTick;string imeCapturedText;int imeCapturedTick;
// How much of imeCapturedText the IME has already reported a second time after ignoring
// CPS_CANCEL; the result delivery path swallows exactly those characters (no composition is
// active then, and WM_IME_STARTCOMPOSITION clears the marker for a genuinely new text).
int imeCapturedConsumed;Action imeParked;TextDocument imeParkedDocument;System.Windows.Threading.DispatcherTimer imeTimer;System.Windows.Interop.HwndSource imeSource;bool imeFontScheduled;
// True once every character of a counted result is in the document. A commit that never sent
// WM_IME_ENDCOMPOSITION (some IMEs only send the result) still left imeComposing armed, so
// ClearEndedComposition's END condition also accepts this proof that the composition is over.
bool imeResultDelivered;
// The click-commit caret move (PlacePressCaret) sets the placing flag so that move does not drop
// the commit's composition point.
bool imeClickPlacing;
// Set by the live preview: true for a press it will consume without moving the caret (a task
// box, a link), so the click-commit placement leaves the caret alone for that press.
internal Func<System.Windows.Input.MouseButtonEventArgs,bool> PressHandledElsewhere;
// Set by the live preview: the offset a TextView-local point means inside a drawn table row (the
// same cell-box rule as its own click), false off a table. The stock hit-test reads a cell's blank
// padding as the next cell's start.
internal delegate bool PointOffset(Point point,out int offset);
internal PointOffset TableOffsetAt;
internal bool ImeBusy {get{return imeComposing||imeCommitPending>0;}}
int imeCommitTarget=-1,imeCommitTargetTick;TextDocument imeCommitTargetDocument;
// The typing run the current commit request was made in. Keyboard focus can leave while the
// counted result is still on its way (a menu, the find box, a dialog-opening command's own commit
// request): the focus-loss break must not cost that result its place in the run, so the paths that
// write a requested commit restore the state BeginCompositionCommit saw.
bool imeCommitTypingRemembered,imeCommitTypingBroken;
void RememberCommitTypingState(){imeCommitTypingRemembered=true;imeCommitTypingBroken=typingBroken;}
void RestoreCommitTypingState(){if(!imeCommitTypingRemembered)return;imeCommitTypingRemembered=false;typingBroken=imeCommitTypingBroken;}
void ForgetCommitTypingState(){imeCommitTypingRemembered=false;}
// One composition session: the insertion point is an anchor, so it follows document changes
// (including the composition's own earlier syllables) but never a caret move the user made.
// Two anchors keep the range the composition owns; the id makes a deferred callback from an
// older session a no-op.
int imeSessionId;TextDocument imeSessionDocument;TextAnchor imeSessionStart,imeSessionEnd;bool imeSessionOpen,imeCommitInserting;
internal int ImeSessionId{get{return imeSessionId;}}
// Set by the live preview: pulls an offset inside the caret's own table cell, so a commit
// interrupted by a click cannot land after the row's trailing '|'.
internal Func<int,int> CellClamp;
// The preview's pending closing-edge space must also follow commits which bypass TextEntering.
// True means the preview inserted the complete text; false leaves the plain insertion here.
internal Func<int,string,bool> CommitTextInserter;
void InsertCommittedText(int at,string text){if(CommitTextInserter==null||!CommitTextInserter(at,text))Document.Insert(at,text);}
// Set by the live preview: every caret move re-checks that a rendered table row keeps the
// caret inside a cell (undo/redo restores an offset the row no longer owns).
internal Action CaretGuardChangedForTable;
void BeginImeSession(){
    ClearImeSession();
    if(Document==null)return;
    int at=Math.Max(0,Math.Min(CaretOffset,Document.TextLength));
    imeSessionStart=Document.CreateAnchor(at);imeSessionStart.MovementType=AnchorMovementType.BeforeInsertion;imeSessionStart.SurviveDeletion=true;
    imeSessionEnd=Document.CreateAnchor(at);imeSessionEnd.MovementType=AnchorMovementType.AfterInsertion;imeSessionEnd.SurviveDeletion=true;
    imeSessionDocument=Document;imeSessionOpen=true;imeSessionId++;
}
void ClearImeSession(){
    imeSessionOpen=false;imeSessionStart=null;imeSessionEnd=null;imeSessionDocument=null;
}
internal void CancelCompositionSession(){imeSessionId++;ClearImeSession();}
// The insertion point this composition owns. The end anchor moves after text inserted there
// (again, after that), which is exactly where the next syllable belongs. False when the
// session is gone: document swapped, anchors deleted, or the composition was cancelled.
bool ImeSessionPoint(out int at){
    at=-1;
    if(!imeSessionOpen||imeSessionStart==null||imeSessionEnd==null)return false;
    if(!Object.ReferenceEquals(Document,imeSessionDocument)){ClearImeSession();return false;}
    if(imeSessionStart.IsDeleted||imeSessionEnd.IsDeleted){ClearImeSession();return false;}
    at=Math.Max(0,Math.Min(imeSessionEnd.Offset,Document.TextLength));
    return true;
}
// Probe seams: an offscreen run has no IME, so the composition state is simulated and every
// commit request records the caret it saw (the N8 ordering check reads these).
internal int CompositionRequests;internal int CompositionRequestCaret=-1;
internal void SimulateImeComposition(){imeComposing=true;imeCompositionEnded=false;BeginImeSession();}
internal void ResetCompositionSimulation(){imeComposing=false;imeCompositionEnded=false;imeCommitPending=0;imeResultDelivered=false;imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();CancelCompositionSession();}
// Probe seam: the result path of a store that ignored CPS_CANCEL reports the text the capture
// already inserted once more. The capture marker is the state CaptureCompositionText leaves.
internal void SimulateCapturedImeResult(string text){imeCapturedText=text;imeCapturedTick=Environment.TickCount;imeCapturedConsumed=0;imeComposing=false;}
// Probe seam: the typed-input marker a command-shaped edit arms (Backspace, a late commit), so a
// probe can record a typed update that has no TextEntering event of its own.
internal void SimulateTypedKey(){ArmTypedInput();}
// Runs now when nothing is composing; otherwise commits and runs once the result text is in
// the document (or the composition was cancelled). Abandoned if the document is swapped.
internal void RunAfterComposition(Action action){if(!ImeBusy){action();return;}var previous=imeParked;imeParked=previous==null?action:(Action)delegate{previous();action();};imeParkedDocument=Document;if(imeComposing)CompleteComposition();ArmImeTimeout();if(!ImeBusy)RunParked();}
// The 500 ms bound that releases a composition which never delivers its result, and lets the
// commands parked behind it run. Called for a new parked action and again from Loaded when a
// reused tab comes back into the tree (Unloaded stops the timer).
void ArmImeTimeout(){
    if(imeTimer==null){imeTimer=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input,Dispatcher){Interval=TimeSpan.FromMilliseconds(500)};imeTimer.Tick+=delegate{imeComposing=false;imeCommitPending=0;imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();RunParked();};}
    imeTimer.Stop();imeTimer.Start();
}
void RunParked(){if(imeTimer!=null)imeTimer.Stop();var action=imeParked;imeParked=null;if(action==null||!Object.ReferenceEquals(Document,imeParkedDocument))return;Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,new Action(delegate{if(Object.ReferenceEquals(Document,imeParkedDocument))action();}));}
// Test seam for the probe: pretend a commit of `length` characters is on its way.
internal void SimulateImeCommit(int length){imeComposing=false;imeCommitPending=length;imeCommitPendingTick=Environment.TickCount;imeResultDelivered=false;}
// The counted result characters are taken when the text enters, before any TextEntering handler
// can set Handled. A handled input raises no TextEntered, and the pending style wraps the first
// committed character that way; counting only on TextEntered left that character in the counted
// result, so ImeBusy stayed true and every undo/redo press was skipped by the store guard
// (real Japanese MS-IME: Ctrl+B, commit, arrow, commit, then no undo). The text this event
// carries is inserted by AvalonEdit right after this handler returns, before the posted
// RunParked callback can run, so a parked command still sees the completed input.
// The text is typing: TypingEntering runs in the same event, so every IME result (Korean, Japanese,
// Chinese) joins the continuous typing step like a plain character.
void ImeResultEntering(object sender,System.Windows.Input.TextCompositionEventArgs e){
    if(imeCommitPending<=0||e.Text==null||e.Text.Length==0)return;
    // A store that ignored CPS_CANCEL reports the text the capture already inserted once more.
    // Swallow exactly that report (same text, still fresh, no composition active) so the syllable
    // cannot land twice. A new composition clears the marker at WM_IME_STARTCOMPOSITION, so
    // typing the same syllable again is not touched; the report may arrive in pieces, so the
    // comparison walks the captured text from where the previous piece ended.
    if(!imeComposing&&imeCapturedText!=null&&unchecked(Environment.TickCount-imeCapturedTick)<5000&&imeCapturedConsumed+e.Text.Length<=imeCapturedText.Length&&String.CompareOrdinal(imeCapturedText,imeCapturedConsumed,e.Text,0,e.Text.Length)==0){
        imeCapturedConsumed+=e.Text.Length;
        if(imeCapturedConsumed>=imeCapturedText.Length)imeCapturedConsumed=0;
        imeCommitPending=Math.Max(0,imeCommitPending-e.Text.Length);
        imeResultDelivered=imeCommitPending<=0;
        e.Handled=true;
        if(ImeTrace.Enabled)ImeTrace.Write("duplicate result dropped text="+TraceText(e.Text)+" pending="+imeCommitPending);
        if(!ImeBusy)RunParked();
        return;
    }
    imeCommitPending=Math.Max(0,imeCommitPending-e.Text.Length);
    if(imeCommitPending>0)return;
    imeResultDelivered=true;
    if(!ImeBusy)RunParked();
}
// HwndSource calls the most recently added hook first, and AvalonEdit removes and re-adds its own
// hook on every keyboard focus. Ours is registered once before the first focus, so it stays older
// than AvalonEdit's hook and runs after it, overwriting the oversized font that hook just set.
void ImeSourceChanged(object sender,SourceChangedEventArgs e){HookIme();}
void HookIme(){if(!imeFocusHooked){imeFocusHooked=true;TextArea.GotKeyboardFocus+=delegate{ScheduleImeUi();};TextArea.LostKeyboardFocus+=delegate{SetImeInline(null);ScheduleImeUi();};TextArea.PreviewKeyDown+=ImeHanjaKeyDown;}var window=PresentationSource.FromVisual(TextArea) as System.Windows.Interop.HwndSource;if(Object.ReferenceEquals(window,imeSource))return;if(imeSource!=null){try{imeSource.RemoveHook(ImeProc);}catch(Exception){}}imeSource=window;if(window!=null)window.AddHook(ImeProc);}
void UnhookIme(){SetImeInline(null);if(imeSource==null)return;try{imeSource.RemoveHook(ImeProc);}catch(Exception){}imeSource=null;}
// TextEditor.OnGotKeyboardFocus forwards focus to the TextArea, but Keyboard.Focus only reaches an
// element that is connected to a presentation source: the template (which hosts the TextArea) is
// first applied when the editor is measured. A source surface that stayed Collapsed in the basic
// view is measured only after the basic->live switch, so the forward is lost and the editor keeps
// focus; the IME then has no context for the next syllable (r7 unusual-9: focus stayed on
// SourceEditor after the switch and the next 가 went nowhere). Retry after the switch's first layout.
protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e){
    base.OnGotKeyboardFocus(e);
    if(e.NewFocus!=this||TextArea.IsKeyboardFocusWithin)return;
    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{if(IsKeyboardFocusWithin&&!TextArea.IsKeyboardFocusWithin)TextArea.Focus();}));
}
// A normal WPF focus transfer tunnels PreviewLostKeyboardFocus before TextArea's override
// cancels/releases IMM in LostKeyboardFocus. Do not cancel the focus transfer or refocus here.
// Native window deactivation has its own earlier hook: forced focus clearing need not raise
// the cancellable preview event at all.
void ImePreviewLostKeyboardFocus(object sender,System.Windows.Input.KeyboardFocusChangedEventArgs e){
    if(e.OldFocus!=TextArea||!TextArea.IsKeyboardFocusWithin)return;
    if(ImeTrace.Enabled)ImeTrace.Write("source PreviewLostKeyboardFocus new="+(e.NewFocus==null?"null":e.NewFocus.GetType().Name)+" handled="+e.Handled+" "+CompositionState());
    if(Program.IsSelfTest||(!ImeBusy&&!ImeCompositionActive()))return;
    CommitCompositionText("preview lost keyboard focus");
}
// A press while composing must commit at the pre-click caret; the click itself stays unhandled.
// A press WPF delivers is AvalonEdit's to move the caret with, so a press record made by the
// composition poll must not compete with it (the Korean IME passes the click through).
void ImeMouseDown(object sender,System.Windows.Input.MouseButtonEventArgs e){
    if(e.ChangedButton!=System.Windows.Input.MouseButton.Left)return;
    imeLastWpfPressTick=Environment.TickCount;
    DisarmButtonCaret();
    if(imeComposing)CompleteComposition();
}
// The document offset under a point in the text view, resolved through the visual column the
// caret would use (hidden markup and wrapped rows included). False outside the visible text body.
bool OffsetAtPoint(Point point,out int offset){
    offset=0;
    var view=TextArea.TextView;var document=Document;
    if(document==null||view==null)return false;
    if(point.X<0||point.Y<0||point.X>view.ActualWidth||point.Y>=view.ActualHeight)return false;
    // A press in the empty area under the last line still belongs to the document's end; the
    // stock handler clamps the same way (SelectionMouseHandler.GetOffsetFromMousePosition).
    // TextView.GetPosition answers null there, so the click-commit caret stayed at the composition
    // end until a second click (real Japanese MS-IME, 2026-10-30: only the last line was affected).
    Point local=point+view.ScrollOffset;
    double height=view.DocumentHeight;
    if(height>0&&local.Y>=height)local.Y=Math.Max(0,height-0.001);
    TextViewPosition? hit=view.GetPosition(local);
    if(!hit.HasValue)hit=view.GetPositionFloor(local);
    if(!hit.HasValue)return false;
    DocumentLine line=document.GetLineByNumber(Math.Max(1,Math.Min(hit.Value.Line,document.LineCount)));
    VisualLine visual=view.GetOrConstructVisualLine(line);
    if(visual==null)return false;
    offset=visual.FirstDocumentLine.Offset+visual.GetRelativeOffset(Math.Max(0,hit.Value.VisualColumn));
    offset=Math.Max(0,Math.Min(offset,document.TextLength));
    if(ImeTrace.Enabled)ImeTrace.Write("ime caret place y="+local.Y+"/"+height+" line="+hit.Value.Line+" column="+hit.Value.VisualColumn+" offset="+offset+" old="+CaretOffset);
    return true;
}
// A real IMM32/TSF IME consumes the left press that commits a composition: WPF never raises
// PreviewMouseDown for it (trace 2026-10-30). The IME result is no trigger either: a converted
// clause can be committed with the result arriving after the button is up, and some IMEs report it
// as a plain WM_IME_CHAR with no counted RESULTSTR (real Japanese MS-IME and ChangJie, 2026-11-01:
// the syllable landed at the composition point and the caret never moved). The physical press is
// the only reliable signal, so a 15 ms poll runs while a composition is live, records the clicked
// offset from the layout that still shows the pre-commit text, and places the caret there once the
// IME has released the store and the committed text has landed. A key typed in between places the
// caret first, so its input is inserted at the clicked place instead of the old composition point.
// A press WPF does deliver (the Korean IME passes it through) cancels the record: AvalonEdit moves
// the caret for that one by itself.
// Armed only for a click that is really on this editor: our window active, text area focused, no
// modifier, and the window under the pointer our own HwndSource (not the IME candidate window, a
// menu popup or another app). Known limit: a press with Shift/Ctrl/Alt held is skipped (shift-click
// selection is not emulated).
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern short GetAsyncKeyState(int key);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct ImeScreenPoint{public int X;public int Y;}
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool GetCursorPos(out ImeScreenPoint point);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr WindowFromPoint(ImeScreenPoint point);
bool imeBtnPending;int imeBtnArmTick,imeBtnRaw,imeBtnLength,imeBtnSessionId;bool imeBtnShift;TextAnchor imeBtnOld,imeBtnTarget;TextDocument imeBtnDocument;
// The poll runs while a composition lives. imePressDown is the previous sample; the low bit of
// GetAsyncKeyState catches a press that went down and up between two polls.
bool imePressWatching,imePressDown;int imeBtnStableLength=-1,imeBtnStableTick;int imeLastWpfPressTick=-100000;
// A composition that keeps a stale latch must not keep the poll alive forever: the watch start
// moves with every composition message, and a minute without one stops it.
int imePressWatchStart;
// When the IME last reported the composition over (END, or an empty composition string). Some IMEs
// send END before the result characters: the click placement waits past this point so the result
// cannot land at the clicked place.
int imeCompositionFreeTick=-100000;
System.Windows.Threading.DispatcherTimer imeBtnTimer;System.Windows.Window imeBtnWindow;
// A key right after the click must not lose the click: place the caret now, and the input lands at
// the clicked place. The record stays while the IME still owns the store (the key belongs to it).
void ImeBtnKeyDown(object sender,System.Windows.Input.KeyEventArgs e){if(imeBtnPending)TryPlacePressCaret(true);}
// WPF raises this before TranslateMessage hands the key to the IME, so the form is at the caret when the Hanja / symbol list opens.
void ImeHanjaKeyDown(object sender,System.Windows.Input.KeyEventArgs e){
var key=e.Key==System.Windows.Input.Key.ImeProcessed?e.ImeProcessedKey:e.Key;
if(key==System.Windows.Input.Key.HanjaMode&&imeComposing&&KoreanLayout()){imeHanjaAtCaret=true;RestoreParkedForm();if(ImeTrace.Enabled)ImeTrace.Write("ime hanja key");}}
void ImeBtnMouseDown(object sender,System.Windows.Input.MouseButtonEventArgs e){imeLastWpfPressTick=Environment.TickCount;DisarmButtonCaret();}
// A mouse press WPF itself received means the stock handlers own the caret again: the record is
// over. The composition poll is separate and stops when the composition does.
void DisarmButtonCaret(){
    imeBtnPending=false;imeBtnOld=null;imeBtnTarget=null;imeBtnDocument=null;imeBtnStableTick=0;
    TextArea.PreviewKeyDown-=ImeBtnKeyDown;
    if(imeBtnWindow!=null){imeBtnWindow.PreviewMouseDown-=ImeBtnMouseDown;imeBtnWindow=null;}
    if(!imePressWatching&&imeBtnTimer!=null)imeBtnTimer.Stop();
}
void StartPressWatch(){
    if(Program.IsSelfTest)return;
    if(imePressWatching&&imeBtnTimer!=null)return;
    imePressWatching=true;
    imePressWatchStart=Environment.TickCount;
    // A press that began before the composition is not a click on it.
    imePressDown=(GetAsyncKeyState(0x01)&0x8000)!=0;imePressQueued=false;
    if(ImeTrace.Enabled)ImeTrace.Write("ime press watch start down="+imePressDown);
    EnsurePressTimer();
}
void StopPressWatch(){if(ImeTrace.Enabled&&imePressWatching)ImeTrace.Write("ime press watch stop pending="+imeBtnPending);imePressWatching=false;if(!imeBtnPending&&imeBtnTimer!=null)imeBtnTimer.Stop();}
void EnsurePressTimer(){
    if(imeBtnTimer==null){imeBtnTimer=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input,Dispatcher){Interval=TimeSpan.FromMilliseconds(15)};imeBtnTimer.Tick+=delegate{PressWatchTick();};}
    imeBtnTimer.Stop();imeBtnTimer.Start();
}
// The result message of a click commit is a second signal for the same press, and the poll above
// usually armed first. Still requires the left button down: a result that arrives with the button
// up is not a click commit (or that press was already placed).
bool imePressQueued;
void NoteMouseCommit(){
    if(imeBtnPending)return;
    short state=GetAsyncKeyState(0x01);
    /* GetAsyncKeyState clears its one-shot "pressed since the last call" bit, which the poll needs
       for a click whose commit result arrives after the button is up; keep the observation. */
    if((state&0x0001)!=0)imePressQueued=true;
    if((state&0x8000)==0)return;
    NotePressCaret();
}
// Resolve the clicked offset from the physical press. The layout here still shows the text before
// the commit, which is what the armed offset and the inserted-length shift are computed against.
void NotePressCaret(){
    // A headless probe has no real pointer, and its own IME cases drive the message seams: the
    // press record stays off so a real mouse button cannot leak into a probe run.
    if(Program.IsSelfTest)return;
    try{
        // WPF's own press for this click is AvalonEdit's: it moved (or moves) the caret itself.
        if(unchecked(Environment.TickCount-imeLastWpfPressTick)<200)return;
        var window=System.Windows.Window.GetWindow(this);
        if(window==null||!window.IsActive||!TextArea.IsKeyboardFocusWithin)return;
        if((System.Windows.Input.Keyboard.Modifiers&(System.Windows.Input.ModifierKeys.Shift|System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Alt))!=0)return;
        var view=TextArea.TextView;var document=Document;
        if(view==null||document==null||imeSource==null||PresentationSource.FromVisual(view)==null)return;
        ImeScreenPoint cursor;
        if(!GetCursorPos(out cursor))return;
        if(WindowFromPoint(cursor)!=imeSource.Handle)return;
        Point local=view.PointFromScreen(new Point(cursor.X,cursor.Y));
        int offset;
        if(!PressOffsetAt(local,out offset))return;
        ArmPressCaret(offset,document);
    }catch(Exception){DisarmButtonCaret();}
}
bool PressOffsetAt(Point local,out int offset){
    offset=0;
    var view=TextArea.TextView;var document=Document;
    if(view==null||document==null)return false;
    if(local.X<0||local.Y<0||local.X>view.ActualWidth||local.Y>=view.ActualHeight)return false;
    // The layout still shows the state before the commit: ask the live preview now whether the
    // press belongs to a task box or a link (it keeps the caret where it was).
    if(PressHandledElsewhere!=null){
        var args=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left);
        args.RoutedEvent=UIElement.PreviewMouseDownEvent;args.Source=TextArea;
        if(PressHandledElsewhere(args))return false;
    }
    // 10-31 real input: a press in the blank part of a table cell resolved to the next cell's
    // start through the stock hit-test; the table rule comes first. A malformed/stale table
    // layout must fall back to the stock hit-test instead of failing the input.
    bool placed=false;
    if(TableOffsetAt!=null){
        try{placed=TableOffsetAt(local,out offset);}
        catch(Exception){placed=false;}
    }
    if(!placed){if(!OffsetAtPoint(local,out offset))return false;}
    return true;
}
void ArmPressCaret(int rawOffset,TextDocument document){
    // The range the composition owns (the caret point when its session is already gone). A click
    // inside it, or exactly at its edge, belongs after the committed text.
    int start=Math.Max(0,Math.Min(CaretOffset,document.TextLength)),end=start;
    if(imeSessionOpen&&imeSessionStart!=null&&imeSessionEnd!=null&&Object.ReferenceEquals(document,imeSessionDocument)&&!imeSessionStart.IsDeleted&&!imeSessionEnd.IsDeleted){start=imeSessionStart.Offset;end=imeSessionEnd.Offset;}
    if(rawOffset>=start&&rawOffset<=end)rawOffset=end;
    // AfterInsertion: text inserted at the target's own offset (the end of the composition range)
    // lands before it, so the target ends up after the committed text; text inserted before it
    // shifts it right and keeps it on the same character.
    imeBtnTarget=document.CreateAnchor(rawOffset);imeBtnTarget.MovementType=AnchorMovementType.AfterInsertion;
    imeBtnOld=document.CreateAnchor(Math.Max(0,Math.Min(CaretOffset,document.TextLength)));imeBtnOld.MovementType=AnchorMovementType.AfterInsertion;
    imeBtnDocument=document;imeBtnArmTick=Environment.TickCount;imeBtnPending=true;
    // c9 real input: a target after the composition point landed one commit length too far with
    // the anchor value. The product value is the raw offset plus the document's net length change
    // since arming, applied only to a target at/after the composition point; the anchor is trace only.
    imeBtnRaw=rawOffset;imeBtnLength=document.TextLength;imeBtnShift=rawOffset>=end;imeBtnSessionId=imeSessionId;
    imeBtnStableLength=imeBtnLength;imeBtnStableTick=0;
    if(ImeTrace.Enabled)ImeTrace.Write("ime press armed raw="+rawOffset+" point="+end+" start="+start+" len="+imeBtnLength+" caret="+CaretOffset);
    TextArea.PreviewKeyDown-=ImeBtnKeyDown;TextArea.PreviewKeyDown+=ImeBtnKeyDown;
    var window=System.Windows.Window.GetWindow(this);
    if(imeBtnWindow!=null&&!Object.ReferenceEquals(imeBtnWindow,window)){imeBtnWindow.PreviewMouseDown-=ImeBtnMouseDown;imeBtnWindow=null;}
    if(window!=null){imeBtnWindow=window;window.PreviewMouseDown-=ImeBtnMouseDown;window.PreviewMouseDown+=ImeBtnMouseDown;}
    EnsurePressTimer();
}
void PressWatchTick(){
    int nowTick=Environment.TickCount;
    if(imePressWatching&&unchecked(nowTick-imePressWatchStart)>60000)StopPressWatch();
    if(!imePressWatching&&!imeBtnPending){if(imeBtnTimer!=null)imeBtnTimer.Stop();return;}
    if(imePressWatching)SampleImePress();
    if(imeBtnPending){
        // The cap passed while the commit is still unfinished: give up without moving.
        if(unchecked(nowTick-imeBtnArmTick)>800){
            if(ImeTrace.Enabled)ImeTrace.Write("ime press caret gave up raw="+imeBtnRaw+" composing="+imeComposing+" pending="+imeCommitPending+" session="+imeSessionId+"/"+imeBtnSessionId+" "+CompositionState());
            DisarmButtonCaret();return;
        }
        TryPlacePressCaret(false);
    }
    // The composition is over and no click is waiting: the poll is done. A pending press keeps the
    // timer alive until its placement decides.
    if(imePressWatching&&!imeBtnPending&&!imeComposing&&imeCommitPending<=0)StopPressWatch();
}
void SampleImePress(){
    short state=GetAsyncKeyState(0x01);
    bool down=(state&0x8000)!=0;
    // The low bit reports a press that went down and up between two polls.
    bool edge=(down||(state&0x0001)!=0||imePressQueued)&&!imePressDown;imePressQueued=false;
    imePressDown=down;
    if(!edge||imeBtnPending)return;
    NotePressCaret();
}
// True when the composition that was live at the press no longer owns the store and no counted
// result is still on its way. A new composition (the user typed immediately, or the IME moved on)
// has its own session id: the arm-time one is over, so the caret may move for it. Moving during
// that new composition is what keeps a key typed right after the click from landing at the old
// place: its preedit follows the caret and its result is written where the caret is then.
bool PressStoreFree(int now){
    if(imeCommitPending>0)return false;
    if(imeSessionOpen&&imeSessionId==imeBtnSessionId){
        // IMM holds the composition string until the IME has really committed it. A composition
        // this session still sees is live however long it lasts (the IME may keep it open after a
        // click on its own window): moving the caret under it would carry the commit to the
        // clicked place. A stale report is cleared by RefreshCompositionState, and the 800 ms cap
        // in PressWatchTick still gives the click up (11-01 review; the old 400 ms escape moved
        // the caret under a live composition).
        if(ImeCompositionActive()||imeComposing)return false;
    }
    return true;
}
void TryPlacePressCaret(bool immediate){
    if(!imeBtnPending)return;
    int now=Environment.TickCount;
    if(!PressStoreFree(now))return;
    if(!immediate){
        int length=Document==null?-1:Document.TextLength;
        // Nothing has landed since the press yet: the result may still be on its way (posted
        // behind this tick, and an END may even precede it), so give it a moment instead of
        // moving the caret under it.
        if(length==imeBtnLength){
            int sinceEnd=unchecked(now-imeCompositionFreeTick);
            if(unchecked(now-imeBtnArmTick)<60||sinceEnd<100)return;
        }
        // A multi-character result arrives as several inputs (WM_IME_CHAR): place only after the
        // text stopped changing for a few ticks, so no committed syllable lands after the move.
        if(length!=imeBtnStableLength){imeBtnStableLength=length;imeBtnStableTick=now;return;}
        if(imeBtnStableTick==0){imeBtnStableTick=now;return;}
        if(unchecked(now-imeBtnStableTick)<40)return;
    }
    PlacePressCaret();
}
void PlacePressCaret(){
    var old=imeBtnOld;var target=imeBtnTarget;var document=imeBtnDocument;int raw=imeBtnRaw,armLength=imeBtnLength;bool shift=imeBtnShift;
    DisarmButtonCaret();
    try{
        if(!TextArea.IsKeyboardFocusWithin)return;
        if(old==null||target==null||old.IsDeleted||target.IsDeleted||!Object.ReferenceEquals(Document,document))return;
        // Moved already (a commit that did not sit at the caret, or the user): never move twice.
        if(SelectionLength!=0||CaretOffset!=old.Offset)return;
        int anchorOffset=Math.Max(0,Math.Min(target.Offset,Document.TextLength));
        int delta=Document.TextLength-armLength;
        int offset=Math.Max(0,Math.Min(shift?raw+delta:raw,Document.TextLength));
        if(ImeTrace.Enabled)ImeTrace.Write("ime press caret raw="+raw+" delta="+delta+" shift="+shift+" anchor="+anchorOffset+" chosen="+offset+" caret="+CaretOffset);
        if(offset==CaretOffset)return;
        if(ImeTrace.Enabled)ImeTrace.Write("ime press caret placed at offset "+offset);
        imeClickPlacing=true;
        try{
            Select(offset,0);
            /* The selection change runs the live table's caret guard, which can pull the caret into a cell: scroll to where the caret really is, not to the requested offset. */
            int placed=CaretOffset;var location=Document.GetLocation(placed);
            ScrollTo(location.Line,location.Column);MarkdownLivePreview.Session.SafeBringCaretToView(TextArea);
        }finally{imeClickPlacing=false;}
    }catch(Exception){}
}
// N8 fallback: when the composition string cannot be read, CPS_COMPLETE only asks for the commit;
// the result arrives later as TextInput, after the click has already moved the caret. Remember the
// caret the syllable belongs to, so the matching input can be inserted there while the caret the
// user clicked keeps its place (shifted when it lies after it).
internal void BeginCompositionCommit(){CompositionRequests++;CompositionRequestCaret=CaretOffset;if(imeCommitTarget<0)RememberCommitTypingState();imeCommitTarget=CaretOffset;imeCommitTargetTick=Environment.TickCount;imeCommitTargetDocument=Document;if(CommitCompositionText("mouse down"))return;if(TextArea.IsKeyboardFocusWithin)CompleteWindowComposition(TextArea);}
void CompositionCommitEntering(object sender,System.Windows.Input.TextCompositionEventArgs e){
// Only a commit that was requested elsewhere (the press/menu path) is redirected; ordinary
// typing keeps AvalonEdit's own selection/indent path.
if(imeCommitTarget<0||e.Text==null||e.Text.Length==0)return;
if(!Object.ReferenceEquals(Document,imeCommitTargetDocument)||(!ImeBusy&&unchecked(Environment.TickCount-imeCommitTargetTick)>250)){imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();return;}
// The commit goes where the composition point is now. The session's end anchor records that
// point and follows text inserted there, so a re-layout or a composition update that moved the
// caret cannot redirect the syllable; the remembered target is only the fallback for a commit
// whose composition session is already gone.
int at;
if(!ImeSessionPoint(out at))at=imeCommitTarget;
if(CellClamp!=null)at=CellClamp(at);
at=Math.Max(0,Math.Min(at,Document.TextLength));
int caret=CaretOffset;
string committed=NormalizeCommittedNewlines(e.Text);int length=committed.Length;
if(ImeTrace.Enabled)ImeTrace.Write("commit enter at="+at+" caret="+caret+" text="+TraceText(committed)+" session="+imeSessionId);
RestoreCommitTypingState();
MarkImeCommitText();
e.Handled=true;
imeCommitInserting=true;
try{using(Document.RunUpdate()){InsertCommittedText(at,committed);CaretOffset=caret>=at?caret+length:caret;}}
finally{imeCommitInserting=false;}
imeCommitTarget=at+length;imeCommitTargetTick=Environment.TickCount;imeCommitTargetDocument=Document;
// ImeResultEntering already took this text out of the counted result when the input entered
// (a handled TextEntering raises no TextEntered); only the target bookkeeping is left here.
if(imeCommitPending<=0){imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();CancelCompositionSession();}
}
// A caret move while nothing is composing is the user's own: a target remembered for a
// composition that never delivered its result must not claim the next input.
void CaretPositionChangedForCommit(object sender,EventArgs e){
if(imeCommitInserting==true)return;
if(ImeBusy)return;
// Only a free caret is normalized; during a composition the IME owns the point and the
// preview must not move it underneath the interim glyphs.
if(CaretGuardChangedForTable!=null)CaretGuardChangedForTable();
// The caret move that belongs to the commit press itself (PlacePressCaret) must not drop the
// composition point the result is written to; any later user move still does.
if(!imeClickPlacing){
if(imeCommitTarget>=0){imeCommitTarget=-1;imeCommitTargetDocument=null;ForgetCommitTypingState();}
if(imeSessionOpen)CancelCompositionSession();
}
}
IntPtr ImeProc(IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled){
if(ImeTrace.Enabled){if(msg==0x010D||msg==0x010E||msg==0x010F)ImeTrace.Write("ime msg=0x"+msg.ToString("X")+" lParam=0x"+lParam.ToInt64().ToString("X")+" composing="+imeComposing+" pending="+imeCommitPending);else if(msg==0x0286)ImeTrace.Write("ime WM_IME_CHAR char="+(char)(wParam.ToInt64()&0xFFFF)+" composing="+imeComposing);}
// The hook is window-wide: a composition owned by another control (the find box) must not latch this editor (same rule as MarkdownLivePreview.CompositionMessage); one that started here is still finished here.
if((msg==0x010D||msg==0x010F)&&!imeComposing&&!TextArea.IsKeyboardFocusWithin)return IntPtr.Zero;
if(msg==0x010D||msg==0x010F)imeLastCompositionTick=Environment.TickCount;
// A form parked while the Korean layout was active outlives a switch to ja/zh, whose IMEs then open at the screen's top-left: bring it back to the caret first.
if(msg==0x010D&&TextArea.IsKeyboardFocused&&!KoreanLayout())UnparkImeForm(hwnd);
// Korean: the form is parked only while a composition runs (see IMN_SETCOMPOSITIONWINDOW below), from its first message on.
if(msg==0x010D&&TextArea.IsKeyboardFocused&&KoreanLayout()){WatchImeBoxes();imeHanjaAtCaret=false;ParkImeForm(hwnd);}
// Typing over a selection: the selected text goes as the composition starts (as in Notepad), not when the first syllable lands.
if(msg==0x010D&&TextArea.IsKeyboardFocused&&!TextArea.Selection.IsEmpty&&CjkLayout())TextArea.Selection.ReplaceSelectionWithText(String.Empty);
if(msg==0x010D){imeComposing=true;imeCompositionEnded=false;imeResultDelivered=false;imeCapturedText=null;imeCapturedConsumed=0;ForgetCommitTypingState();BeginImeSession();StartPressWatch();ApplyCompositionFont(hwnd);QueueCompositionFont(hwnd);}
else if(msg==0x010F){imePressWatchStart=Environment.TickCount;long flags=lParam.ToInt64();string result=null;int compositionLength=-1;if((flags&0x0800)!=0||(flags&0x0008)!=0){string compositionText=null;IntPtr context=ImmGetContext(hwnd);if(context!=IntPtr.Zero){try{if((flags&0x0800)!=0)result=ReadImeString(context,0x0800);compositionText=ReadImeString(context,0x0008);compositionLength=NormalizedImeLength(compositionText);imeAttrs=null;imeCursor=-1;if(compositionLength>0&&!KoreanLayout()&&CjkLayout()){imeAttrs=ReadImeAttr(context);imeCursor=ImmGetCompositionStringW(context,0x0080,IntPtr.Zero,0);}}finally{ImmReleaseContext(hwnd,context);}}
// MDNote: Korean IME composing syllable is drawn inline by us (the OS composition window is hidden, see ApplyImeUi).
if(compositionLength>=0)SetImeInline(compositionLength>0&&CjkLayout()?compositionText.TrimEnd('\0'):null,imeAttrs,imeCursor);}NoteImeComposition(flags,result,compositionLength);if((flags&0x0800)!=0&&result!=null)NoteMouseCommit();ApplyCompositionFont(hwnd);QueueCompositionFont(hwnd);}
else if(msg==0x010E){SetImeInline(null);imeComposing=false;imeCompositionEnded=true;imeCompositionFreeTick=Environment.TickCount;if(imeCommitPending==0){CancelCompositionSession();if(imeParked!=null)RunParked();}
// The Hanja key ends the composition and then opens the Hanja / symbol list from the form: it must be back at the caret by then.
if(TextArea.IsKeyboardFocused&&KoreanLayout())UnparkImeForm(hwnd);}
else if(msg==0x0281&&wParam!=IntPtr.Zero){QueueCompositionFont(hwnd);
// MDNote: while this editor has focus under a Korean layout, tell the IME not to show its own composition window (we draw it inline).
long contextFlags=lParam.ToInt64();if((contextFlags&0x80000000L)!=0&&TextArea.IsKeyboardFocused&&CjkLayout()){handled=true;return DefWindowProcW(hwnd,msg,wParam,new IntPtr(unchecked((int)(contextFlags&0x7FFFFFFFL))));}}
else if(msg==0x0051)ScheduleImeUi();
else if(msg==0x0282){long notify=wParam.ToInt64()&0xFFFF;if(notify==0x000A||notify==0x000B)ScheduleCompositionFont();
// MDNote: AvalonEdit's Caret calls ImeSupport.UpdateCompositionWindow on every caret show/move, putting the form at the caret, and the
// Korean MS-IME then draws its own GDI copy of the syllable over our inline run (thinner or raised at small sizes). IMN_SETCOMPOSITIONWINDOW
// is sent synchronously by that call: park the form off-screen and swallow this notification so the IME UI only sees the parked one.
// Korean only (MS Pinyin and the Japanese IME place their candidate lists from this form), and only while composing and before the
// Hanja key (ImeBtnKeyDown): its Hanja / symbol lists open from the composition box, which must then be at the caret.
if(notify==0x000B&&imeComposing&&TextArea.IsKeyboardFocused&&KoreanLayout()&&!imeHanjaAtCaret&&ParkImeForm(hwnd)){handled=true;return IntPtr.Zero;}}
// WM_IME_REQUEST / IMR_QUERYCHARPOSITION: the new Korean MS-IME opens its Hanja and symbol lists after the syllable is committed and places
// them from this answer; AvalonEdit never answered, so they opened at the screen's top-left (0,0).
if(msg==0x0288){long request=wParam.ToInt64();if(ImeTrace.Enabled)ImeTrace.Write("ime request 0x"+request.ToString("X")+" composing="+imeComposing+" focused="+TextArea.IsKeyboardFocused);
// The Hanja key never reaches WPF (the IME eats it); the only sign is a lone IMR_QUERYCHARPOSITION, asked while the syllable is still
// composing but long after the last WM_IME_COMPOSITION (each of those is followed by its own pair of requests within a few ms).
if(request==0x0006&&imeComposing&&!imeHanjaAtCaret&&TextArea.IsKeyboardFocused&&KoreanLayout()&&unchecked(Environment.TickCount-imeLastCompositionTick)>100){imeHanjaAtCaret=true;RestoreParkedForm();if(ImeTrace.Enabled)ImeTrace.Write("ime hanja (lone request)");}
if(request==0x0006&&lParam!=IntPtr.Zero&&TextArea.IsKeyboardFocused&&KoreanLayout()&&AnswerCharPosition(hwnd,lParam)){handled=true;return new IntPtr(1);}}
return IntPtr.Zero;}
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct ImeCharPosition{public int dwSize;public int dwCharPos;public ImeScreenPoint pt;public int cLineHeight;public ImeRect rcDocument;}
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool ClientToScreen(IntPtr hwnd,ref ImeScreenPoint point);
// The caret's top-left in screen pixels with the line height, and the editor as the document rectangle. The caret line is mid-format
// right after a commit, so the caret form saved while composing stands in for it then (same position: the syllable just committed).
bool AnswerCharPosition(IntPtr hwnd,IntPtr lParam){
    var info=(ImeCharPosition)System.Runtime.InteropServices.Marshal.PtrToStructure(lParam,typeof(ImeCharPosition));
    if(info.dwSize<System.Runtime.InteropServices.Marshal.SizeOf(typeof(ImeCharPosition)))return false;
    var source=System.Windows.Interop.HwndSource.FromHwnd(hwnd);if(source==null||source.RootVisual==null||source.CompositionTarget==null)return false;
    var toRoot=TransformToAncestor(source.RootVisual);var toDevice=source.CompositionTarget.TransformToDevice;
    ImeScreenPoint at=new ImeScreenPoint();int height;Rect caret=GetRectFromCharacterIndex(CaretOffset);bool saved=caret.IsEmpty;
    if(!saved){Point top=toDevice.Transform(toRoot.Transform(caret.TopLeft));Point bottom=toDevice.Transform(toRoot.Transform(caret.BottomLeft));at.X=(int)top.X;at.Y=(int)top.Y;height=Math.Max(1,(int)Math.Round(bottom.Y-top.Y));}
    else if(imeCaretFormSaved){at=imeCaretForm.ptCurrentPos;height=Math.Max(1,(int)Math.Round(TextArea.TextView.DefaultLineHeight*toDevice.M22));}
    else return false;
    ImeScreenPoint docTop=new ImeScreenPoint(),docBottom=new ImeScreenPoint();
    Point a=toDevice.Transform(toRoot.Transform(new Point(0,0))),b=toDevice.Transform(toRoot.Transform(new Point(ActualWidth,ActualHeight)));
    docTop.X=(int)a.X;docTop.Y=(int)a.Y;docBottom.X=(int)b.X;docBottom.Y=(int)b.Y;
    ClientToScreen(hwnd,ref at);ClientToScreen(hwnd,ref docTop);ClientToScreen(hwnd,ref docBottom);
    info.pt=at;info.cLineHeight=height;info.rcDocument.Left=docTop.X;info.rcDocument.Top=docTop.Y;info.rcDocument.Right=docBottom.X;info.rcDocument.Bottom=docBottom.Y;
    System.Runtime.InteropServices.Marshal.StructureToPtr(info,lParam,false);
    if(ImeTrace.Enabled)ImeTrace.Write("ime charposition pos="+info.dwCharPos+" at="+at.X+","+at.Y+" height="+height+" saved="+saved);
    return true;
}
// State half of WM_IME_COMPOSITION; the probe supplies the native result text and composition
// string the IME would report, so message ordering can be exercised without an installed IME.
// A result-only message (GCS_RESULTSTR, no composition string left in the context) ends the
// composition. The Korean IME sends END before the final result, so re-arming the latch here left
// imeComposing true after the text landed and every later undo was skipped by the guard.
// The counted length comes from the result string itself, never from a byte count: an IME may
// report the terminating NUL as part of GCS_RESULTSTR, and a count one larger than the
// characters it delivers left one unit pending forever, which also parks undo behind ImeBusy.
void NoteImeComposition(long flags,string resultText,int compositionLength){
    int resultLength=NormalizedImeLength(resultText);
    if(resultLength>0){imeCommitPending+=resultLength;imeCommitPendingTick=Environment.TickCount;imeResultDelivered=false;}
    if(compositionLength>0){imeComposing=true;imeCompositionEnded=false;imeResultDelivered=false;if(!imeSessionOpen)BeginImeSession();StartPressWatch();return;}
    if(compositionLength==0){
        imeComposing=false;imeCompositionEnded=true;imeCompositionFreeTick=Environment.TickCount;
        if(imeCommitPending==0){CancelCompositionSession();if(imeParked!=null)RunParked();}
        return;
    }
    // No context to read: keep the conservative latch. An END already seen marks this as a result
    // hand-off, which ClearEndedComposition releases before a command once the text has landed.
    imeComposing=true;if(!imeSessionOpen)BeginImeSession();
}
// The characters an IMM string reports, without the terminating NUL some IMEs include in the
// reported byte count. Null and an empty string both count as no characters.
static int NormalizedImeLength(string text){
    if(String.IsNullOrEmpty(text))return 0;
    int length=text.Length;
    while(length>0&&text[length-1]=='\0')length--;
    return length;
}
// GCS_COMPSTR/GCS_RESULTSTR of an IMM context as text. Null when the context holds no string.
string ReadImeString(IntPtr context,int index){
    int bytes=ImmGetCompositionStringW(context,index,IntPtr.Zero,0);
    if(bytes<=0)return null;
    IntPtr buffer=System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
    try{
        int read=ImmGetCompositionStringW(context,index,buffer,bytes);
        return read>0?System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer,read/2):null;
    }finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);}
}
void QueueCompositionFont(IntPtr hwnd){Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,new Action(delegate{ApplyCompositionFont(hwnd);}));}
// AvalonEdit also resets the composition font outside WM_IME_COMPOSITION: ImeSupport.CreateContext
// defers to Caret.ShowInternal, which calls UpdateCompositionWindow from a DispatcherPriority.Normal
// BeginInvoke on every caret move, visual line change and focus (Caret.cs:496). Our Send-priority
// re-apply runs before that Normal callback, so the caret paths re-apply at Background, which is
// the first priority below Normal, and only when the IME font actually differs (the apply itself
// can make the IME trivially notify, and the WM_IME_NOTIFY branch above feeds this same queue).
void ScheduleCompositionFont(){if(imeFontScheduled)return;imeFontScheduled=true;Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{imeFontScheduled=false;var window=imeSource;if(window!=null)EnsureCompositionFont(window.Handle);}));}
void EnsureCompositionFont(IntPtr hwnd){if(!TextArea.IsKeyboardFocused)return;var font=CompositionFont();IntPtr context=ImmGetContext(hwnd);if(context==IntPtr.Zero)return;try{ImeFont current;if(ImmGetCompositionFontW(context,out current)&&current.lfHeight==font.lfHeight&&current.lfWeight==font.lfWeight&&current.lfQuality==font.lfQuality&&String.Equals(current.lfFaceName,font.lfFaceName))return;ImmSetCompositionFontW(context,ref font);}finally{ImmReleaseContext(hwnd,context);}}
// AvalonEdit sizes the IME font from the caret's line box (line spacing included), so the
// composing syllable looked larger than the text. Use the em size of the text at the caret.
void ApplyCompositionFont(IntPtr hwnd){if(!TextArea.IsKeyboardFocused)return;var font=CompositionFont();IntPtr context=ImmGetContext(hwnd);if(context==IntPtr.Zero)return;try{ImmSetCompositionFontW(context,ref font);}finally{ImmReleaseContext(hwnd,context);}}
ImeFont CompositionFont(){double size=FontSize;Typeface face=new Typeface(FontFamily,FontStyle,FontWeight,FontStretch);var view=TextArea.TextView;var line=view.VisualLinesValid?view.GetVisualLine(TextArea.Caret.Line):null;if(line!=null){int column=line.GetVisualColumn(Math.Max(0,TextArea.Caret.Offset-line.FirstDocumentLine.Offset));VisualLineElement best=null;foreach(var element in line.Elements){if(element.DocumentLength==0)continue;if(best==null||element.VisualColumn<column)best=element;else break;}if(best!=null&&best.TextRunProperties!=null){size=best.TextRunProperties.FontRenderingEmSize;face=best.TextRunProperties.Typeface;}}var dpi=VisualTreeHelper.GetDpi(this);string name=face.FontFamily.Source;int comma=name.IndexOf(',');if(comma>0)name=name.Substring(0,comma).Trim();return new ImeFont{lfHeight=-(int)Math.Round(size*dpi.DpiScaleY),lfWeight=face.Weight.ToOpenTypeWeight(),lfItalic=(byte)(face.Style==FontStyles.Normal?0:1),lfCharSet=1,lfQuality=4,lfFaceName=name.Length>31?name.Substring(0,31):name};}
// MDNote: Korean IME composing syllable, drawn inline instead of by the OS composition window.
// The OS window paints the syllable on a white box that covers the character right of the caret,
// in GDI colors. AvalonEdit never puts the composition string into the document, so we show it
// as a visual-only zero-document-length element at the caret (underlined, editor font/color,
// pushes the following text right). The committed result still arrives through WM_IME_CHAR ->
// TextInput as before, so undo grouping, dirty state and the composition guards are unchanged.
// ja/zh keep the OS window (clause highlighting and conversion UI stay native).
ImeInlineGenerator imeInline;bool imeFocusHooked;
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr GetKeyboardLayout(uint thread);
[System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="DefWindowProcW")]static extern IntPtr DefWindowProcW(IntPtr hWnd,int msg,IntPtr wParam,IntPtr lParam);
static bool KoreanLayout(){return (GetKeyboardLayout(0).ToInt64()&0x3FF)==0x12;}
// ko (0x12), ja (0x11), zh (0x04): the OS composition window is hidden and the string is drawn inline.
static bool CjkLayout(){long lang=GetKeyboardLayout(0).ToInt64()&0x3FF;return lang==0x12||lang==0x11||lang==0x04;}
byte[] imeAttrs;int imeCursor=-1;bool imeCandidateQueued;
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct ImeRect{public int Left;public int Top;public int Right;public int Bottom;}
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct ImeCandForm{public int dwIndex;public int dwStyle;public ImeScreenPoint ptCurrentPos;public ImeRect rcArea;}
[System.Runtime.InteropServices.DllImport("imm32.dll")]static extern bool ImmSetCandidateWindow(IntPtr hIMC,ref ImeCandForm form);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct ImeCompForm{public int dwStyle;public ImeScreenPoint ptCurrentPos;public ImeRect rcArea;}
[System.Runtime.InteropServices.DllImport("imm32.dll")]static extern bool ImmSetCompositionWindow(IntPtr hIMC,ref ImeCompForm form);
[System.Runtime.InteropServices.DllImport("imm32.dll")]static extern bool ImmGetCompositionWindow(IntPtr hIMC,out ImeCompForm form);
// The last on-screen form before it was parked: AvalonEdit's caret placement, given back when the composition ends.
static ImeCompForm imeCaretForm;static bool imeCaretFormSaved;
// True when the form was somewhere else and has just been moved off-screen (CFS_FORCE_POSITION at -32000); false when it already was, or no context.
static bool ParkImeForm(IntPtr hwnd){IntPtr context=ImmGetContext(hwnd);if(context==IntPtr.Zero)return false;try{ImeCompForm form;bool read=ImmGetCompositionWindow(context,out form);if(read&&form.dwStyle==0x0020&&form.ptCurrentPos.X==-32000)return false;if(read&&form.ptCurrentPos.X!=-32000){imeCaretForm=form;imeCaretFormSaved=true;}imeParkedWindow=hwnd;form=new ImeCompForm();form.dwStyle=0x0020;form.ptCurrentPos.X=-32000;form.ptCurrentPos.Y=-32000;form.rcArea.Left=-32000;form.rcArea.Top=-32000;form.rcArea.Right=-31999;form.rcArea.Bottom=-31999;ImmSetCompositionWindow(context,ref form);if(ImeTrace.Enabled)ImeTrace.Write("ime form parked");return true;}finally{ImmReleaseContext(hwnd,context);}}
// The Korean MS-IME also ignores ISC_SHOWUICOMPOSITIONWINDOW and clamps the parked form to the monitor's corner, where its composition box
// (a CiceroUIWndFrame top-level window of this thread, about one syllable in size) then shows. A small CiceroUIWndFrame exactly at a monitor
// corner is that box and nothing else (no IME window belongs there), so it is made fully transparent (layered, alpha 0) while it stays there
// and restored when the IME moves it on-screen elsewhere. A WinEvent hook on this thread sees every show and move as it happens.
delegate bool ThreadWindowProc(IntPtr hwnd,IntPtr lParam);
delegate void WinEventProc(IntPtr hook,int eventType,IntPtr hwnd,int idObject,int idChild,int thread,int time);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool EnumThreadWindows(int threadId,ThreadWindowProc proc,IntPtr lParam);
[System.Runtime.InteropServices.DllImport("kernel32.dll")]static extern int GetCurrentThreadId();
[System.Runtime.InteropServices.DllImport("kernel32.dll")]static extern int GetCurrentProcessId();
[System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern int GetClassNameW(IntPtr hwnd,System.Text.StringBuilder name,int max);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr hwnd,out ImeRect rect);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr SetWinEventHook(int eventMin,int eventMax,IntPtr module,WinEventProc proc,int processId,int threadId,int flags);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern int GetWindowLongW(IntPtr hwnd,int index);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern int SetWindowLongW(IntPtr hwnd,int index,int value);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool SetLayeredWindowAttributes(IntPtr hwnd,int key,byte alpha,int flags);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int cx,int cy,int flags);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr MonitorFromWindow(IntPtr hwnd,int flags);
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool GetMonitorInfoW(IntPtr monitor,ref ImeMonitorInfo info);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]struct ImeMonitorInfo{public int cbSize;public ImeRect rcMonitor;public ImeRect rcWork;public int dwFlags;}
[System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool IsWindow(IntPtr hwnd);
static WinEventProc imeBoxEvents;
// Boxes made transparent here; the value holds the extended-style bits we added (WS_EX_LAYERED, WS_EX_TRANSPARENT), removed again on restore.
static readonly System.Collections.Generic.Dictionary<IntPtr,int> imeBoxesHidden=new System.Collections.Generic.Dictionary<IntPtr,int>();
// The IME opens its Hanja / symbol lists next to its composition box, so the Hanja key gives the form back at the caret (RestoreParkedForm)
// for the rest of that composition; the box stays transparent there. Before that the form at the caret brings back the IME's own copy of the syllable.
bool imeHanjaAtCaret;int imeLastCompositionTick;
static IntPtr imeParkedWindow;
static void RestoreParkedForm(){
    if(imeParkedWindow==IntPtr.Zero||!imeCaretFormSaved)return;
    IntPtr context=ImmGetContext(imeParkedWindow);if(context==IntPtr.Zero)return;
    try{ImeCompForm form;if(!ImmGetCompositionWindow(context,out form)||form.ptCurrentPos.X!=-32000)return;form=imeCaretForm;ImmSetCompositionWindow(context,ref form);if(ImeTrace.Enabled)ImeTrace.Write("ime form restored at="+form.ptCurrentPos.X+","+form.ptCurrentPos.Y);}
    finally{ImmReleaseContext(imeParkedWindow,context);}
}
static void WatchImeBoxes(){
    if(imeBoxEvents==null){
        imeBoxEvents=OnImeBoxEvent;int pid=GetCurrentProcessId(),tid=GetCurrentThreadId();
        // EVENT_OBJECT_DESTROY..EVENT_OBJECT_SHOW and EVENT_OBJECT_LOCATIONCHANGE, out of context (delivered through this thread's message loop).
        SetWinEventHook(0x8001,0x8002,IntPtr.Zero,imeBoxEvents,pid,tid,0);SetWinEventHook(0x800B,0x800B,IntPtr.Zero,imeBoxEvents,pid,tid,0);
    }
    // A box left at the corner by an earlier composition goes transparent before the IME shows it again.
    EnumThreadWindows(GetCurrentThreadId(),delegate(IntPtr window,IntPtr unused){CheckImeBox(window);return true;},IntPtr.Zero);
}
static void OnImeBoxEvent(IntPtr hook,int eventType,IntPtr hwnd,int idObject,int idChild,int thread,int time){
    if(hwnd==IntPtr.Zero||idObject!=0||idChild!=0)return; /* OBJID_WINDOW / CHILDID_SELF only: caret moves arrive as OBJID_CARET */
    if(eventType==0x8001){imeBoxesHidden.Remove(hwnd);return;}
    CheckImeBox(hwnd);
}
static void CheckImeBox(IntPtr hwnd){
    var name=new System.Text.StringBuilder(64);if(GetClassNameW(hwnd,name,name.Capacity)==0)return;
    ImeRect r;if(!GetWindowRect(hwnd,out r))return;
    int width=r.Right-r.Left,height=r.Bottom-r.Top;bool small=width>0&&height>0&&width<=320&&height<=120;bool hidden=imeBoxesHidden.ContainsKey(hwnd);
    string cls=name.ToString();
    if((cls!="CiceroUIWndFrame"||!small)&&!hidden)return;
    // Once seen at a corner the box stays transparent wherever the IME moves it next: between compositions the form follows the caret,
    // and the next composition may show the box there before its first message parks the form again. A window grown into a list is given back.
    bool box=small&&(hidden||AtMonitorCorner(hwnd,r));
    if(box){
        int ex=GetWindowLongW(hwnd,-20); /* GWL_EXSTYLE */
        const int want=0x80000|0x20; /* WS_EX_LAYERED | WS_EX_TRANSPARENT: invisible and click-through while it sits at the caret */
        if(!hidden)imeBoxesHidden[hwnd]=want&~ex;
        if((ex&want)!=want)SetWindowLongW(hwnd,-20,ex|want);
        bool clear=SetLayeredWindowAttributes(hwnd,0,0,2); /* LWA_ALPHA */
        // A window the IME draws with UpdateLayeredWindow refuses LWA_ALPHA: put it off-screen instead (no clamping for SetWindowPos).
        if(!clear){SetWindowLongW(hwnd,-20,(ex|want)&~imeBoxesHidden[hwnd]);imeBoxesHidden.Remove(hwnd);if(AtMonitorCorner(hwnd,r))SetWindowPos(hwnd,IntPtr.Zero,-32000,-32000,0,0,0x0015);} /* SWP_NOSIZE|SWP_NOZORDER|SWP_NOACTIVATE */
        if(ImeTrace.Enabled&&!hidden)ImeTrace.Write("ime box hidden rect="+r.Left+","+r.Top+","+r.Right+","+r.Bottom+" exstyle=0x"+ex.ToString("X")+" alpha="+clear);
    }else if(hidden&&width>0&&height>0){ /* grown past a box: a list now; a box shrunk to nothing while hidden stays tracked */
        int added=imeBoxesHidden[hwnd];imeBoxesHidden.Remove(hwnd);SetLayeredWindowAttributes(hwnd,0,255,2);
        if(added!=0)SetWindowLongW(hwnd,-20,GetWindowLongW(hwnd,-20)&~added);
        if(ImeTrace.Enabled)ImeTrace.Write("ime box restored rect="+r.Left+","+r.Top+","+r.Right+","+r.Bottom);
    }
}
static bool AtMonitorCorner(IntPtr hwnd,ImeRect r){
    IntPtr monitor=MonitorFromWindow(hwnd,2);if(monitor==IntPtr.Zero)return false; /* MONITOR_DEFAULTTONEAREST */
    var info=new ImeMonitorInfo();info.cbSize=System.Runtime.InteropServices.Marshal.SizeOf(typeof(ImeMonitorInfo));
    if(!GetMonitorInfoW(monitor,ref info))return false;
    return AtCorner(r,info.rcMonitor)||AtCorner(r,info.rcWork);
}
static bool AtCorner(ImeRect r,ImeRect area){return Math.Abs(r.Left-area.Left)<=1&&Math.Abs(r.Top-area.Top)<=1;}
// Same placement as AvalonEdit's ImeNativeWrapper.SetCompositionWindow (caret top-left in device pixels of the window), only when parked.
void UnparkImeForm(IntPtr hwnd){
    var source=System.Windows.Interop.HwndSource.FromHwnd(hwnd);if(source==null||source.RootVisual==null||source.CompositionTarget==null)return;
    IntPtr context=ImmGetContext(hwnd);if(context==IntPtr.Zero)return;
    try{
        ImeCompForm form;if(!ImmGetCompositionWindow(context,out form)||form.ptCurrentPos.X!=-32000)return;
        // At WM_IME_ENDCOMPOSITION the caret line is being re-formatted (the inline run just went), so there is no caret rectangle yet:
        // the caret form saved when it was parked is used then (the Hanja key opens its lists right after this message).
        Rect caret=GetRectFromCharacterIndex(CaretOffset);bool saved=caret.IsEmpty;
        if(saved){if(!imeCaretFormSaved){if(ImeTrace.Enabled)ImeTrace.Write("ime form unpark skipped: no caret rect, nothing saved");return;}form=imeCaretForm;}
        else{
            var toRoot=TransformToAncestor(source.RootVisual);var toDevice=source.CompositionTarget.TransformToDevice;
            Point at=toDevice.Transform(toRoot.Transform(caret.TopLeft));Rect area=new Rect(toDevice.Transform(toRoot.Transform(new Point(0,0))),toDevice.Transform(toRoot.Transform(new Point(ActualWidth,ActualHeight))));
            form=new ImeCompForm();form.dwStyle=0x0020;form.ptCurrentPos.X=(int)at.X;form.ptCurrentPos.Y=(int)at.Y;
            form.rcArea.Left=(int)area.Left;form.rcArea.Top=(int)area.Top;form.rcArea.Right=(int)area.Right;form.rcArea.Bottom=(int)area.Bottom;
        }
        ImmSetCompositionWindow(context,ref form);if(ImeTrace.Enabled)ImeTrace.Write("ime form unparked at="+form.ptCurrentPos.X+","+form.ptCurrentPos.Y+" saved="+saved);
    }finally{ImmReleaseContext(hwnd,context);}
}
// GCS_COMPATTR: one attribute byte per composition character (0 input, 1 target converted, 2 converted, 3 target not converted, 4 error).
byte[] ReadImeAttr(IntPtr context){
    int bytes=ImmGetCompositionStringW(context,0x0010,IntPtr.Zero,0);
    if(bytes<=0)return null;
    IntPtr buffer=System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
    try{
        int read=ImmGetCompositionStringW(context,0x0010,buffer,bytes);
        if(read<=0)return null;
        byte[] attrs=new byte[read];System.Runtime.InteropServices.Marshal.Copy(buffer,attrs,0,read);return attrs;
    }finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);}
}
static bool SameAttrs(byte[] a,byte[] b){
    if(Object.ReferenceEquals(a,b))return true;
    if(a==null||b==null||a.Length!=b.Length)return false;
    for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;
    return true;
}
void SetImeInline(string text,byte[] attrs,int cursor){
    if(String.IsNullOrEmpty(text)){if(imeInline==null||imeInline.Text==null)return;imeInline.Text=null;imeInline.Attrs=null;RedrawImeInline();return;}
    if(imeInline==null)imeInline=new ImeInlineGenerator(this);
    // First in the list: a generator that builds a non-empty element at the caret offset (live preview markers) would otherwise stop the pass before ours.
    var generators=TextArea.TextView.ElementGenerators;if(generators.IndexOf(imeInline)!=0){generators.Remove(imeInline);generators.Insert(0,imeInline);}
    if(String.Equals(imeInline.Text,text)&&SameAttrs(imeInline.Attrs,attrs)){CaretAfterImeInline();return;}
    imeInline.Text=text;imeInline.Attrs=attrs;imeInline.Cursor=cursor;RedrawImeInline();CaretAfterImeInline();
    if(!imeCandidateQueued){imeCandidateQueued=true;Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(PlaceImeCandidate));}
}
void SetImeInline(string text){SetImeInline(text,null,-1);}
// The OS composition window is hidden, so the candidate window is anchored by hand: below the line, at the
// target clause start inside the inline string (the composition start when nothing is a target), with the
// inline string excluded so the list never covers it. Runs after the redraw, using the width the generator measured.
void PlaceImeCandidate(){
    imeCandidateQueued=false;
    var window=imeSource;var gen=imeInline;
    if(window==null||gen==null||gen.Text==null||!TextArea.IsKeyboardFocused)return;
    var root=window.RootVisual as UIElement;var view=TextArea.TextView;var document=Document;
    if(root==null||document==null||window.CompositionTarget==null)return;
    try{
        var pos=new TextViewPosition(document.GetLocation(Math.Max(0,Math.Min(CaretOffset,document.TextLength))));
        Point top=view.GetVisualPosition(pos,VisualYPosition.LineTop)-view.ScrollOffset;
        Point bottom=view.GetVisualPosition(pos,VisualYPosition.LineBottom)-view.ScrollOffset;
        var toDevice=window.CompositionTarget.TransformToDevice;
        Point anchor=toDevice.Transform(view.TranslatePoint(new Point(top.X+gen.TargetX,bottom.Y),root));
        Point left=toDevice.Transform(view.TranslatePoint(new Point(top.X,top.Y),root));
        Point right=toDevice.Transform(view.TranslatePoint(new Point(top.X+gen.Width,bottom.Y),root));
        ImeCandForm form=new ImeCandForm();
        form.dwIndex=0;form.dwStyle=0x0080; /* CFS_EXCLUDE */
        form.ptCurrentPos.X=(int)anchor.X;form.ptCurrentPos.Y=(int)anchor.Y;
        form.rcArea.Left=(int)left.X;form.rcArea.Top=(int)left.Y;form.rcArea.Right=(int)Math.Max(right.X,left.X+1);form.rcArea.Bottom=(int)right.Y;
        IntPtr context=ImmGetContext(window.Handle);if(context==IntPtr.Zero)return;
        try{ImmSetCandidateWindow(context,ref form);}finally{ImmReleaseContext(window.Handle,context);}
    }catch(Exception){}
}
// 11-01 user: the caret blinked left of the syllable being composed. The inline string has no document length, so the visual
// columns before and after it are the same offset and both are valid caret stops: the caret takes the one after it, as in
// Notepad. Only the visual column changes; every caret handler compares offsets, so none of them sees a move.
// Other caret work (live preview snapping, a rebuilt visual line) puts the caret back on the stop before the string, so the
// stop is re-taken whenever the caret moves or the lines are rebuilt while a composition is drawn.
bool imeCaretHooked,imeCaretPlacing;
void CaretAfterImeInline(){
    var gen=imeInline;var document=Document;if(gen==null||gen.Text==null||document==null||!TextArea.IsKeyboardFocused||imeCaretPlacing)return;
    imeCaretPlacing=true;try{CaretAfterImeInlineCore(gen,document);}finally{imeCaretPlacing=false;}
}
void CaretAfterImeInlineCore(ImeInlineGenerator gen,TextDocument document){
    if(!imeCaretHooked){imeCaretHooked=true;
        TextArea.Caret.PositionChanged+=delegate{if(imeInline!=null&&imeInline.Text!=null)CaretAfterImeInline();};
        TextArea.TextView.VisualLinesChanged+=delegate{if(imeInline!=null&&imeInline.Text!=null)Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render,new Action(CaretAfterImeInline));};}
    try{
        var caret=TextArea.Caret;var line=TextArea.TextView.GetOrConstructVisualLine(document.GetLineByNumber(caret.Line));
        foreach(var element in line.Elements){if(!(element is ImeInlineElement))continue;int after=element.VisualColumn+element.VisualLength;
            if(caret.VisualColumn!=after){caret.Position=new TextViewPosition(caret.Line,caret.Column,after);if(ImeTrace.Enabled)ImeTrace.Write("ime caret after vc="+after+" now="+caret.VisualColumn);}break;}
    }catch(Exception ex){if(ImeTrace.Enabled)ImeTrace.Write("ime caret after failed "+ex.GetType().Name+": "+ex.Message);}
}
void RedrawImeInline(){
    var document=Document;if(document==null)return;
    DocumentLine line=document.GetLineByOffset(Math.Max(0,Math.Min(CaretOffset,document.TextLength)));
    TextArea.TextView.Redraw(line);
}
// Hides (Korean layout, editor focused) or restores the IME's own composition window by re-sending
// WM_IME_SETCONTEXT to the default window procedure with ISC_SHOWUICOMPOSITIONWINDOW cleared or set.
void ApplyImeUi(){
    var window=imeSource;if(window==null)return;
    bool hide=TextArea.IsKeyboardFocused&&CjkLayout();
    DefWindowProcW(window.Handle,0x0281,new IntPtr(1),new IntPtr(unchecked(hide?(int)0x4000000F:(int)0xC000000F)));
    if(!hide)SetImeInline(null);
}
void ScheduleImeUi(){Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(ApplyImeUi));}
internal Brush CaretBrush {get{return TextArea.Caret.CaretBrush;}set{TextArea.Caret.CaretBrush=value;}}
internal int CaretIndex {get{return CaretOffset;}set{CaretOffset=Math.Max(0,Math.Min(value,Document.TextLength));}}
internal TextWrapping TextWrapping {get{return WordWrap?TextWrapping.Wrap:TextWrapping.NoWrap;}set{WordWrap=value!=TextWrapping.NoWrap;}}






internal new void ScrollToLine(int line){base.ScrollToLine(line+1);}
internal int GetLineIndexFromCharacterIndex(int index){return Document.GetLineByOffset(Math.Max(0,Math.Min(index,Document.TextLength))).LineNumber-1;}
internal int GetCharacterIndexFromLineIndex(int line){return Document.GetLineByNumber(Math.Max(1,Math.Min(line+1,Document.LineCount))).Offset;}
internal int GetFirstVisibleLineIndex(){var view=TextArea.TextView;view.EnsureVisualLines();return view.VisualLines.Count==0?0:view.VisualLines[0].FirstDocumentLine.LineNumber-1;}
internal int GetCharacterIndexFromPoint(Point point,bool snap){var view=TextArea.TextView;view.EnsureVisualLines();Point local=TranslatePoint(point,view);var pos=view.GetPositionFloor(local+view.ScrollOffset);return pos.HasValue?Document.GetOffset(pos.Value.Location):-1;}
internal Rect GetRectFromCharacterIndex(int index){return GetRectFromCharacterIndex(index,false);}
internal Rect GetRectFromCharacterIndex(int index,bool trailing){var view=TextArea.TextView;if(!view.VisualLinesValid)return Rect.Empty;index=Math.Max(0,Math.Min(index,Document.TextLength));if(trailing&&index<Document.TextLength)index++;var loc=Document.GetLocation(index);var line=view.GetVisualLine(loc.Line);if(line==null)return Rect.Empty;var pos=new TextViewPosition(loc);Point top=view.GetVisualPosition(pos,VisualYPosition.LineTop)-view.ScrollOffset;Point bottom=view.GetVisualPosition(pos,VisualYPosition.LineBottom)-view.ScrollOffset;top=view.TranslatePoint(top,this);bottom=view.TranslatePoint(bottom,this);return new Rect(top.X,top.Y,0,Math.Max(1,bottom.Y-top.Y));}
}
// MDNote: visual-only inline IME composition text (see SourceEditor.SetImeInline).
internal sealed class ImeInlineGenerator:VisualLineElementGenerator{
    readonly SourceEditor owner;internal string Text;internal byte[] Attrs;internal int Cursor=-1;
    // Measured when the element is built (device-independent units): whole string, and the start of the target clause.
    internal double Width;internal double TargetX;
    internal ImeInlineGenerator(SourceEditor owner){this.owner=owner;}
    public override int GetFirstInterestedOffset(int startOffset){
        if(String.IsNullOrEmpty(Text))return -1;
        var line=CurrentContext.VisualLine;int caret=owner.CaretOffset;
        if(caret<line.FirstDocumentLine.Offset||caret>line.LastDocumentLine.EndOffset)return -1;
        return caret>=startOffset?caret:-1;
    }
    public override VisualLineElement ConstructElement(int offset){
        var context=CurrentContext;string text=Text;byte[] attrs=Attrs;
        var props=context.GlobalTextRunProperties;
        var formatter=System.Windows.Media.TextFormatting.TextFormatter.Create(System.Windows.Media.TextOptions.GetTextFormattingMode(context.TextView));
        var line=formatter.FormatLine(new ImeInlineSource(text,attrs,props),0,32000,new ImeInlineParagraph(props),null);
        Width=line.WidthIncludingTrailingWhitespace;
        int target=-1;if(attrs!=null)for(int i=0;i<attrs.Length&&i<text.Length;i++)if(attrs[i]==1||attrs[i]==3){target=i;break;}
        TargetX=target<=0?0:line.GetDistanceFromCharacterHit(new System.Windows.Media.TextFormatting.CharacterHit(target,0));
        return new ImeInlineElement(line);
    }
}
// Run properties of the inline composition string: the editor's own font and colour, plus the clause styling.
internal sealed class ImeInlineProps:System.Windows.Media.TextFormatting.TextRunProperties{
    readonly System.Windows.Media.TextFormatting.TextRunProperties baseProps;readonly TextDecorationCollection decorations;readonly Brush background;
    internal ImeInlineProps(System.Windows.Media.TextFormatting.TextRunProperties baseProps,bool target){
        this.baseProps=baseProps;
        Brush foreground=baseProps.ForegroundBrush??Brushes.Black;
        decorations=new TextDecorationCollection();
        decorations.Add(new TextDecoration(TextDecorationLocation.Underline,new Pen(foreground,target?2:1),0,TextDecorationUnit.FontRecommended,TextDecorationUnit.Pixel));
        decorations.Freeze();
        if(target){var c=SystemColors.HighlightColor;var b=new SolidColorBrush(Color.FromArgb(70,c.R,c.G,c.B));b.Freeze();background=b;}
    }
    public override Typeface Typeface{get{return baseProps.Typeface;}}
    public override double FontRenderingEmSize{get{return baseProps.FontRenderingEmSize;}}
    public override double FontHintingEmSize{get{return baseProps.FontHintingEmSize;}}
    public override TextDecorationCollection TextDecorations{get{return decorations;}}
    public override Brush ForegroundBrush{get{return baseProps.ForegroundBrush;}}
    public override Brush BackgroundBrush{get{return background;}}
    public override System.Globalization.CultureInfo CultureInfo{get{return baseProps.CultureInfo;}}
    public override TextEffectCollection TextEffects{get{return baseProps.TextEffects;}}
}
internal sealed class ImeInlineSource:System.Windows.Media.TextFormatting.TextSource{
    readonly string text;readonly byte[] attrs;readonly System.Windows.Media.TextFormatting.TextRunProperties normal,target;
    internal ImeInlineSource(string text,byte[] attrs,System.Windows.Media.TextFormatting.TextRunProperties baseProps){this.text=text;this.attrs=attrs;normal=new ImeInlineProps(baseProps,false);target=new ImeInlineProps(baseProps,true);}
    bool IsTarget(int i){if(attrs==null||i>=attrs.Length)return false;return attrs[i]==1||attrs[i]==3;}
    public override System.Windows.Media.TextFormatting.TextRun GetTextRun(int index){
        if(index>=text.Length)return new System.Windows.Media.TextFormatting.TextEndOfParagraph(1);
        bool t=IsTarget(index);int end=index+1;while(end<text.Length&&IsTarget(end)==t)end++;
        return new System.Windows.Media.TextFormatting.TextCharacters(text,index,end-index,t?target:normal);
    }
    public override System.Windows.Media.TextFormatting.TextSpan<System.Windows.Media.TextFormatting.CultureSpecificCharacterBufferRange> GetPrecedingText(int limit){throw new NotImplementedException();}
    public override int GetTextEffectCharacterIndexFromTextSourceCharacterIndex(int index){throw new NotImplementedException();}
}
internal sealed class ImeInlineParagraph:System.Windows.Media.TextFormatting.TextParagraphProperties{
    readonly System.Windows.Media.TextFormatting.TextRunProperties props;
    internal ImeInlineParagraph(System.Windows.Media.TextFormatting.TextRunProperties props){this.props=props;}
    public override FlowDirection FlowDirection{get{return FlowDirection.LeftToRight;}}
    public override TextAlignment TextAlignment{get{return TextAlignment.Left;}}
    public override double LineHeight{get{return double.NaN;}}
    public override bool FirstLineInParagraph{get{return false;}}
    public override System.Windows.Media.TextFormatting.TextRunProperties DefaultTextRunProperties{get{return props;}}
    public override TextWrapping TextWrapping{get{return TextWrapping.NoWrap;}}
    public override System.Windows.Media.TextFormatting.TextMarkerProperties TextMarkerProperties{get{return null;}}
    public override double Indent{get{return 0;}}
}
internal sealed class ImeInlineElement:FormattedTextElement{
    internal ImeInlineElement(System.Windows.Media.TextFormatting.TextLine line):base(line,0){}
    // No caret stop inside the provisional text; the caret stays where the document says it is.
    public override int GetNextCaretPosition(int visualColumn,System.Windows.Documents.LogicalDirection direction,CaretPositioningMode mode){return -1;}
}
}
