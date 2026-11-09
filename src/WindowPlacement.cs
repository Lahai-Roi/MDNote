using System;using System.Globalization;using System.Windows;using System.Windows.Interop;using System.Windows.Threading;using System.Runtime.InteropServices;using System.Collections.Generic;
namespace MDNote {public partial class MainWindow {
static string rememberedWindow="";WindowState lastRestorableState=WindowState.Normal;static WeakReference lastMovedWindow;bool placementReady,closeApproved,placementClosed;static bool testDeferredClose;Rect requestedPlacement,reservedPlacement;bool placementReserved;
// A saved maximized state is applied once the window exists (SourceInitialized): maximizing before
// the window is shown made WPF pick the primary monitor instead of the saved one.
bool restoreMaximized;
// Runs in the MainWindow constructor: a machine with no primary screen or a zero work area (RDP,
// monitors switching) must open at the default spot rather than fail to start.
void RestoreWindowPlacement(){try{RestoreWindowPlacementCore();}catch(Exception){restoreMaximized=false;WindowStartupLocation=WindowStartupLocation.CenterScreen;}}
void RestoreWindowPlacementCore(){if(Program.IsSelfTest||String.IsNullOrEmpty(rememberedWindow))return;string[] parts=rememberedWindow.Split('|');double left,top,width,height;if(parts.Length!=5||!Double.TryParse(parts[0],NumberStyles.Float,CultureInfo.InvariantCulture,out left)||!Double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out top)||!Double.TryParse(parts[2],NumberStyles.Float,CultureInfo.InvariantCulture,out width)||!Double.TryParse(parts[3],NumberStyles.Float,CultureInfo.InvariantCulture,out height)||Double.IsNaN(left)||Double.IsInfinity(left)||Double.IsNaN(top)||Double.IsInfinity(top)||Double.IsNaN(width)||Double.IsNaN(height)||width<200||width>16000||height<150||height>16000)return;double scale=ScreenScale();var screen=MonitorArea.WorkFromRect((int)(left/scale),(int)(top/scale),(int)(width/scale),(int)(height/scale));var work=new Rect(screen.Left*scale,screen.Top*scale,screen.Width*scale,screen.Height*scale);Width=Math.Max(MinWidth,Math.Min(width,work.Width));Height=Math.Max(MinHeight,Math.Min(height,work.Height));Left=Math.Max(work.Left,Math.Min(left,work.Right-Width));Top=Math.Max(work.Top,Math.Min(top,work.Bottom-Height));WindowStartupLocation=WindowStartupLocation.Manual;restoreMaximized=parts[4]=="max";lastRestorableState=restoreMaximized?WindowState.Maximized:WindowState;}
void RememberWindowPlacement(){if(Program.IsSelfTest)return;WindowState effective=WindowState==WindowState.Minimized?lastRestorableState:WindowState;Rect bounds=WindowState!=WindowState.Normal?RestoreBounds:new Rect(Left,Top,ActualWidth,ActualHeight);if(bounds.IsEmpty||Double.IsNaN(bounds.Left))return;rememberedWindow=String.Join("|",new[]{bounds.Left.ToString("R",CultureInfo.InvariantCulture),bounds.Top.ToString("R",CultureInfo.InvariantCulture),bounds.Width.ToString("R",CultureInfo.InvariantCulture),bounds.Height.ToString("R",CultureInfo.InvariantCulture),effective==WindowState.Maximized?"max":"normal"});SaveWindowPlacement();}

static bool FinitePlacement(double value){return !Double.IsNaN(value)&&!Double.IsInfinity(value);}
internal static bool UsablePlacement(Rect bounds){return !bounds.IsEmpty&&FinitePlacement(bounds.Left)&&FinitePlacement(bounds.Top)&&FinitePlacement(bounds.Width)&&FinitePlacement(bounds.Height)&&bounds.Width>0&&bounds.Height>0&&bounds.Left>-30000&&bounds.Top>-30000;}
internal static bool TryActualPlacement(MainWindow window,out Rect bounds){bounds=Rect.Empty;if(window==null||window.WindowState!=WindowState.Normal||!window.IsLoaded||!(window.ActualWidth>0)||!(window.ActualHeight>0))return false;var actual=new Rect(window.Left,window.Top,window.ActualWidth,window.ActualHeight);if(!UsablePlacement(actual))return false;bounds=actual;return true;}
internal static bool TryPlacementBounds(MainWindow window,MainWindow self,out Rect bounds){bounds=Rect.Empty;if(window==null||window==self||window.placementClosed)return false;if(TryActualPlacement(window,out bounds))return true;if(window.WindowState==WindowState.Minimized||window.WindowState==WindowState.Maximized){Rect restore=window.RestoreBounds;if(UsablePlacement(restore)){bounds=restore;return true;}}if(window.placementReserved&&UsablePlacement(window.reservedPlacement)){bounds=window.reservedPlacement;return true;}if(UsablePlacement(window.requestedPlacement)){bounds=window.requestedPlacement;return true;}bounds=Rect.Empty;return false;}
internal static Rect CenteredPlacement(Rect work,Size size){double width=Math.Max(0,Math.Min(size.Width,work.Width)),height=Math.Max(0,Math.Min(size.Height,work.Height));return new Rect(work.Left+(work.Width-width)/2,work.Top+(work.Height-height)/2,width,height);}
Rect ReadRequestedPlacement(){double width=FinitePlacement(Width)&&Width>0?Width:(FinitePlacement(ActualWidth)&&ActualWidth>0?ActualWidth:MinWidth),height=FinitePlacement(Height)&&Height>0?Height:(FinitePlacement(ActualHeight)&&ActualHeight>0?ActualHeight:MinHeight);if(FinitePlacement(Left)&&FinitePlacement(Top))return new Rect(Left,Top,width,height);Rect work=SystemParameters.WorkArea;if(!(work.Width>0)||!(work.Height>0))work=new Rect(0,0,SystemParameters.PrimaryScreenWidth,SystemParameters.PrimaryScreenHeight);return CenteredPlacement(work,new Size(width,height));}
void ReservePlacement(Rect bounds){if(!UsablePlacement(bounds))return;reservedPlacement=bounds;placementReserved=true;}
internal bool RefreshReservedPlacement(){Rect bounds;if(!TryActualPlacement(this,out bounds))return false;ReservePlacement(bounds);return true;}
internal MainWindow FindPlacementAnchor(){if(lastMovedWindow!=null){var target=lastMovedWindow.Target as MainWindow;Rect latest;if(target!=null&&TryPlacementBounds(target,this,out latest))return target;}MainWindow anchor=null;if(Application.Current!=null)foreach(Window candidate in Application.Current.Windows){var other=candidate as MainWindow;Rect bounds;if(other==null||!TryPlacementBounds(other,this,out bounds))continue;if(other==DocumentActivation.ActiveWindow)return other;if(anchor==null)anchor=other;}return anchor;}
internal void ClaimPlacementAnchor(){var latest=lastMovedWindow==null?null:lastMovedWindow.Target as MainWindow;if(latest==null||latest==this||latest.placementClosed){lastMovedWindow=new WeakReference(this);return;}Rect bounds;if(!TryPlacementBounds(latest,this,out bounds))lastMovedWindow=new WeakReference(this);}

[DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr hwnd);
[DllImport("user32.dll")]static extern bool BringWindowToTop(IntPtr hwnd);[DllImport("dwmapi.dll")]static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
[DllImport("user32.dll")]internal static extern bool AllowSetForegroundWindow(int process);

internal void EnsureVisibleForeground(){if(WindowState==WindowState.Minimized){/* b50: skip the DWM restore animation so a close prompt from the taskbar appears at once */IntPtr restoring=new WindowInteropHelper(this).Handle;int flag=1;if(restoring!=IntPtr.Zero)DwmSetWindowAttribute(restoring,3,ref flag,4);WindowState=lastRestorableState==WindowState.Maximized?WindowState.Maximized:WindowState.Normal;flag=0;if(restoring!=IntPtr.Zero)DwmSetWindowAttribute(restoring,3,ref flag,4);}if(Program.IsSelfTest)return;if(!IsVisible)Show();Activate();IntPtr handle=new WindowInteropHelper(this).Handle;if(handle!=IntPtr.Zero){BringWindowToTop(handle);SetForegroundWindow(handle);}}
void ApplyTitleTheme(){IntPtr handle=new WindowInteropHelper(this).Handle;if(handle==IntPtr.Zero)return;try{int enabled=dark?1:0;if(DwmSetWindowAttribute(handle,20,ref enabled,4)!=0)DwmSetWindowAttribute(handle,19,ref enabled,4);int caption=dark?0x202020:-1;DwmSetWindowAttribute(handle,35,ref caption,4);int ink=dark?0xE8E8E8:-1;DwmSetWindowAttribute(handle,36,ref ink,4);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}}
// WPF units per screen pixel; 1 when the primary screen is missing or reports an empty work area.
static double ScreenScale(){double pixels=MonitorArea.PrimaryWorkHeight();double scale=pixels>0?SystemParameters.WorkArea.Height/pixels:1;return scale>0&&!Double.IsInfinity(scale)?scale:1;}
static Rect ScreenWorkArea(MonitorArea.PixelRect screen){double scale=ScreenScale();return new Rect(screen.Left*scale,screen.Top*scale,screen.Width*scale,screen.Height*scale);}
static Rect WorkAreaFor(Rect bounds){try{double scale=ScreenScale();return ScreenWorkArea(MonitorArea.WorkFromRect((int)(bounds.Left/scale),(int)(bounds.Top/scale),(int)(bounds.Width/scale),(int)(bounds.Height/scale)));}catch(Exception){return SystemParameters.WorkArea;}}
// A maximized or minimized window reports its normal (restore) rectangle in Left/Top, and a window
// that has not been placed yet has NaN there: neither names the monitor the window is drawn on.
// The handle does, so an owned dialog or a cascade started from such a window follows that monitor.
static Rect WorkAreaFor(Window window,Rect bounds){
    if(window!=null&&(window.WindowState!=WindowState.Normal||!FinitePlacement(window.Left)||!FinitePlacement(window.Top))){
        IntPtr handle=new WindowInteropHelper(window).Handle;
        if(handle!=IntPtr.Zero)return ScreenWorkArea(MonitorArea.WorkFromWindow(handle));
    }
    return WorkAreaFor(bounds);
}
// The requested rectangle fitted inside the work area of the monitor it belongs to, so the title
// bar and menu of the first window stay on screen (a 1080x800 default on a 1366x768@125% desktop was
// centered above the top edge).
internal static Rect ClampPlacement(Rect requested,Rect work){
    if(!(work.Width>0)||!(work.Height>0))return requested;
    double width=Math.Max(0,Math.Min(requested.Width,work.Width)),height=Math.Max(0,Math.Min(requested.Height,work.Height));
    double left=Math.Max(work.Left,Math.Min(requested.Left,work.Right-width)),top=Math.Max(work.Top,Math.Min(requested.Top,work.Bottom-height));
    return new Rect(left,top,width,height);
}
internal static Rect CascadeBounds(Rect previous,Size size,Rect work){double width=Math.Min(size.Width,work.Width),height=Math.Min(size.Height,work.Height);double left=Math.Max(work.Left,Math.Min(previous.Left+28,work.Right-width));double top=previous.Top+28;if(top+height>work.Bottom)top=work.Top;top=Math.Max(work.Top,top);return new Rect(left,top,width,height);}
internal bool ReserveInitialPlacement(bool cascade){requestedPlacement=ReadRequestedPlacement();bool cascaded=false;if(cascade){MainWindow previous=FindPlacementAnchor();if(previous!=null){Rect old;if(TryPlacementBounds(previous,this,out old)){Rect next=CascadeBounds(old,new Size(requestedPlacement.Width,requestedPlacement.Height),WorkAreaFor(previous,old));WindowState=WindowState.Normal;lastRestorableState=WindowState.Normal;WindowStartupLocation=WindowStartupLocation.Manual;Left=next.Left;Top=next.Top;Width=next.Width;Height=next.Height;requestedPlacement=next;cascaded=true;}}}/* The first window (or one with no visible anchor) applies the clamped request itself instead of leaving it to WindowStartupLocation.CenterScreen, which does not fit an oversized default. */if(!cascaded&&!Program.IsSelfTest){Rect target=ClampPlacement(requestedPlacement,WorkAreaFor(requestedPlacement));if(UsablePlacement(target)){WindowStartupLocation=WindowStartupLocation.Manual;Left=target.Left;Top=target.Top;Width=Math.Max(MinWidth,target.Width);Height=Math.Max(MinHeight,target.Height);requestedPlacement=target;}}ReservePlacement(ReadRequestedPlacement());lastMovedWindow=new WeakReference(this);return cascaded;}
internal bool ReserveMovedPlacement(Rect bounds){if(WindowState!=WindowState.Normal||!UsablePlacement(bounds))return false;ReservePlacement(bounds);lastMovedWindow=new WeakReference(this);return true;}
void SetupWindowPlacement(){/* The session group exists from construction, so its order is the order the windows were opened. */SetupSessionGroup();ReserveInitialPlacement(!Program.IsSelfTest);Closed+=delegate{placementClosed=true;placementReserved=false;if(lastMovedWindow!=null&&lastMovedWindow.Target==this)lastMovedWindow=null;};SourceInitialized+=delegate{/* The HWND exists here with the clamped rectangle, so maximizing now picks its monitor. */if(restoreMaximized&&WindowState==WindowState.Normal)WindowState=WindowState.Maximized;restoreMaximized=false;ApplyTitleTheme();var handle=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);if(handle!=null)handle.AddHook(WindowMessage);};ContentRendered+=delegate{placementReady=true;RefreshReservedPlacement();ClaimPlacementAnchor();};LocationChanged+=delegate{if(placementReady){Rect moved;if(TryActualPlacement(this,out moved))ReserveMovedPlacement(moved);}};}
IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){/* Windows shutdown: after WM_QUERYENDSESSION the system keeps posting WM_CLOSE (observed: thousands, 3 ms apart, and no WM_ENDSESSION). The deferred close below would never run (its timer restarted on every message) and the process is killed, so the close goes straight to WPF: Closing flushes the drafts again (a no-op once flushed) and the window really closes. */if(sessionEnding)return IntPtr.Zero;if(!closeApproved&&(!Program.IsSelfTest||testDeferredClose)&&(message==0x10||message==0x112&&(wParam.ToInt64()&0xFFF0)==0xF060)){QueueWindowClose(this);handled=true;}return IntPtr.Zero;}
// b50: HwndSource calls hooks newest first, so this one (registered after SaveCloseMessage) sees a close
// from a minimized window before SaveCloseMessage can restore it. A minimized owner must stay minimized
// until the save prompt has been answered, so the close is deferred here (and an open prompt is raised).
internal void SetupMinimizedClosePriority(){SourceInitialized+=delegate{var hwnd=HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);if(hwnd!=null)hwnd.AddHook(MinimizedCloseMessage);};}
IntPtr MinimizedCloseMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled){
    if(closeApproved||sessionEnding||WindowState!=WindowState.Minimized||Program.IsSelfTest&&!testDeferredClose)return IntPtr.Zero;
    if(!(message==0x10||message==0x112&&(wParam.ToInt64()&0xFFF0)==0xF060))return IntPtr.Zero;
    closeTraceHook=System.Diagnostics.Stopwatch.GetTimestamp();
    // Always queue this window: when another window's prompt is already open the request used to be
    // dropped (only the open dialog was raised) and this close was swallowed. The running batch, or
    // the batch started once that prompt is answered, picks the request up in turn.
    if(!closeRequests.Contains(this))closeRequests.Enqueue(this);
    var dialog=saveConfirmationDialog;
    // No 160 ms batching wait here: the user is looking at the taskbar and expects the prompt at once.
    // Background priority still lets close messages already queued for other windows join this batch.
    if(dialog!=null){ForegroundSaveWindow(dialog);if(!closingBatch)ScheduleCloseRetry();}else if(!closingBatch)Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(ProcessWindowCloses));
    handled=true;return IntPtr.Zero;
}
// Timestamps of the last minimized close (hook, ShowDialog, first render), read by --userfix-probe.
internal static long closeTraceHook,closeTraceProcess,closeTraceShow,closeTraceRendered;
static readonly Queue<MainWindow> closeRequests=new Queue<MainWindow>();static DispatcherTimer closeTimer;static bool closingBatch;
void OnWindowClosing(object sender,System.ComponentModel.CancelEventArgs args){if(closeApproved){RememberWindowPlacement();return;}if(Program.IsSelfTest&&!testDeferredClose){if(fileOpenRunning)CancelFileOpen();if(!ConfirmTabs())args.Cancel=true;return;}/* WPF ignores Cancel while the application shuts down, so drafts are flushed synchronously instead of queued. */if(sessionEnding||Application.Current!=null&&Application.Current.Dispatcher.HasShutdownStarted){FlushForSessionEnd();RememberWindowPlacement();return;}args.Cancel=true;QueueWindowClose(this);}
static void QueueWindowClose(MainWindow window){if(!closeRequests.Contains(window))closeRequests.Enqueue(window);if(closingBatch)return;
    // A prompt opened outside a close batch (a tab close, for example) may be up: starting the
    // batch now would stack a second prompt on it, or close this window while the user answers
    // it. Raise the prompt and keep the request for the retry its finally starts (K3).
    if(saveConfirmationDialog!=null){ForegroundSaveWindow(saveConfirmationDialog);ScheduleCloseRetry();return;}
    if(closeTimer==null){closeTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(160)};closeTimer.Tick+=delegate{closeTimer.Stop();ProcessWindowCloses();};}/* The wait starts at the first request and is not restarted by the next one: a close message that repeats faster than the interval (Windows shutdown posts WM_CLOSE every few ms) would otherwise postpone the batch forever. */if(!closeTimer.IsEnabled)closeTimer.Start();}
// A close requested while an unrelated save prompt is open (not part of a batch) waits for it:
// starting the batch now would stack a second modal prompt on the first.
static DispatcherTimer closeRetryTimer;
static void ScheduleCloseRetry(){
    // No give-up: an open prompt only pauses the retry, and the prompt's finally calls
    // ResumeDeferredCloses, so answering it cannot leave a queued close stranded (K3).
    if(closeRetryTimer==null){closeRetryTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};closeRetryTimer.Tick+=delegate{if(saveConfirmationDialog!=null)return;closeRetryTimer.Stop();if(closingBatch||closeRequests.Count==0)return;ProcessWindowCloses();};}
    closeRetryTimer.Stop();closeRetryTimer.Start();
}
// K1/K2/K3: a deferred close is retried from a short Background timer, never from a nested frame.
// It is not started while a save prompt is open; that prompt's finally starts it again.
static DispatcherTimer closeDeferTimer;
static void ResumeDeferredCloses(){
    if(closingBatch||saveConfirmationDialog!=null||closeRequests.Count==0)return;
    if(closeDeferTimer==null){closeDeferTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(25)};closeDeferTimer.Tick+=delegate{closeDeferTimer.Stop();if(closingBatch||saveConfirmationDialog!=null||closeRequests.Count==0)return;ProcessWindowCloses();};}
    closeDeferTimer.Stop();closeDeferTimer.Start();
}
static async void ProcessWindowCloses(){
    // A prompt started outside a close batch must be answered first; ResumeDeferredCloses retries
    // the queued closes once it is gone, so a batch never runs underneath it.
    if(closingBatch||saveConfirmationDialog!=null)return;closeTraceProcess=System.Diagnostics.Stopwatch.GetTimestamp();if(closeTimer!=null)closeTimer.Stop();if(closeDeferTimer!=null)closeDeferTimer.Stop();closingBatch=true;
    // The batch is fixed at its start: a close that arrives while a prompt is open belongs to the next
    // batch, so cancelling this one must not drop it (K3). Deferred windows are queued again, marked.
    var batch=new List<MainWindow>(closeRequests);closeRequests.Clear();
    try{
        foreach(var window in batch){
            if(window.placementClosed||!window.IsLoaded)continue;
            if(window.PrepareCloseDeferral()){window.closeDeferred=true;if(!closeRequests.Contains(window))closeRequests.Enqueue(window);continue;}
            window.ResetCloseDeferral();window.FinishComposition();window.FlushDocumentChanges();
            /* The save may be a large format-changing conversion running under the file-open
               overlay; await it so the window cannot close (or write) underneath the conversion. */
            bool confirmed;if(window.WindowState==WindowState.Minimized)confirmed=await window.ConfirmTabsMinimized();else confirmed=await window.ConfirmTabsAsync();
            if(window.placementClosed||!window.IsLoaded)continue;
            if(!confirmed){DropDeferredRequests();break;}
            window.closeApproved=true;window.Close();
        }
    }finally{closingBatch=false;}
    ResumeDeferredCloses();
}
static void DropDeferredRequests(){if(closeRequests.Count==0)return;var kept=new List<MainWindow>();while(closeRequests.Count>0){var window=closeRequests.Dequeue();if(window.closeDeferred)window.ResetCloseDeferral();else kept.Add(window);}foreach(var window in kept)closeRequests.Enqueue(window);}
// An open in flight leaves documentOperationBusy set, so Save returns false with no message; the prompt
// would vanish without saving. A cancelled read finishes on the thread pool and an IME commit arrives as
// a later input message, so the close waits for either: cancel/commit, keep the window queued and let
// ResumeDeferredCloses run the batch again. A format-changing save is the opposite case: it must finish,
// so the close waits for it instead of cancelling. Nothing here pumps the dispatcher (K1, K2).
bool closeDeferred;long closeOpenSince,closeComposeSince,closeSettleSince;bool closeCompositionRequested;bool closeOpenCancelled;System.Threading.CancellationTokenSource closeCancelledOpen;
static double CloseWaitMilliseconds(long since){return (System.Diagnostics.Stopwatch.GetTimestamp()-since)*1000.0/System.Diagnostics.Stopwatch.Frequency;}
bool PrepareCloseDeferral(){
    long now=System.Diagnostics.Stopwatch.GetTimestamp();
    if(saveInProgress){
        // The conversion/write cannot be cancelled without losing the user's save, so let it finish
        // (the overlay covers the document) and have the retry timer run the batch again afterwards.
        closeOpenSince=0;closeOpenCancelled=false;closeCancelledOpen=null;
        return true;
    }
    if(fileOpenRunning){
        // Cancel once per open (a new open that starts during the wait is cancelled again) so Save is
        // never refused by documentOperationBusy, then wait for the cancellation to really finish.
        if(closeOpenSince==0)closeOpenSince=now;
        if(!closeOpenCancelled||(fileOpenCancellation!=null&&!Object.ReferenceEquals(closeCancelledOpen,fileOpenCancellation))){closeOpenCancelled=true;closeCancelledOpen=fileOpenCancellation;CancelFileOpen();}
        if(CloseWaitMilliseconds(closeOpenSince)<2000)return true;
    }
    closeOpenSince=0;closeOpenCancelled=false;closeCancelledOpen=null;
    bool composing=imeComposing||source!=null&&(source.ImeBusy||source.ImeCompositionActive());
    if(composing||closeCompositionRequested){
        if(closeComposeSince==0)closeComposeSince=now;
        double waited=CloseWaitMilliseconds(closeComposeSince);
        if(composing){closeSettleSince=0;if(waited<500){if(!closeCompositionRequested){closeCompositionRequested=true;FinishComposition();}return true;}}
        else{
            // FinishComposition only requests the commit; give the commit at least one more retry
            // turn to land. Measured from the first tick that saw composing end: the 25 ms retry
            // itself already made closeComposeSince (when composing started) look settled, so the
            // old test never held and the close ran at once.
            if(closeSettleSince==0)closeSettleSince=now;
            if(CloseWaitMilliseconds(closeSettleSince)<25)return true;
        }
        closeCompositionRequested=false;closeComposeSince=0;closeSettleSince=0;
    }
    return false;
}
void ResetCloseDeferral(){closeDeferred=false;closeOpenSince=0;closeComposeSince=0;closeSettleSince=0;closeCompositionRequested=false;closeOpenCancelled=false;closeCancelledOpen=null;}
}
/* 2026-11-09: screen metrics through user32 only. System.Windows.Forms.Screen and System.Drawing.Rectangle
   were loaded by the startup placement code and stayed in memory; these calls return the same device-pixel
   work-area rectangles Screen did (ScreenWorkArea scales them to DIPs exactly as before). */
internal static class MonitorArea{
    [StructLayout(LayoutKind.Sequential)]struct RECT{internal int Left;internal int Top;internal int Right;internal int Bottom;}
    [StructLayout(LayoutKind.Sequential)]struct MONITORINFO{internal int cbSize;internal RECT rcMonitor;internal RECT rcWork;internal int dwFlags;}
    [StructLayout(LayoutKind.Sequential)]internal struct ScreenPoint{internal int X;internal int Y;}
    // Device pixels; Width/Height are derived so callers keep the old Rectangle fields.
    internal struct PixelRect{internal int Left;internal int Top;internal int Width;internal int Height;}
    const uint MonitorDefaultToPrimary=1,MonitorDefaultToNearest=2;
    [DllImport("user32.dll")]static extern IntPtr MonitorFromRect(ref RECT rect,uint flags);
    [DllImport("user32.dll")]static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll")]static extern IntPtr MonitorFromPoint(ScreenPoint point,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetMonitorInfoW")]static extern bool GetMonitorInfoW(IntPtr monitor,ref MONITORINFO info);
    [DllImport("user32.dll")]static extern bool GetCursorPos(out ScreenPoint point);
    // Pointer position in screen device pixels; false while the input desktop is switched (UAC, lock, RDP).
    internal static bool TryGetCursor(out ScreenPoint point){return GetCursorPos(out point);}
    // Work-area height of the primary monitor in device pixels; 0 when it cannot be read (Screen returned null).
    internal static int PrimaryWorkHeight(){PixelRect work;return TryPrimaryWork(out work)?work.Height:0;}
    static bool TryPrimaryWork(out PixelRect work){ScreenPoint origin=new ScreenPoint();return TryWork(MonitorFromPoint(origin,MonitorDefaultToPrimary),out work);}
    // The monitor holding the largest part of the rectangle (Screen.FromRectangle's rule), else the nearest.
    internal static PixelRect WorkFromRect(int left,int top,int width,int height){
        RECT bounds=new RECT();bounds.Left=left;bounds.Top=top;bounds.Right=left+width;bounds.Bottom=top+height;
        PixelRect work;if(!TryWork(MonitorFromRect(ref bounds,MonitorDefaultToNearest),out work))throw new InvalidOperationException("GetMonitorInfo failed for the placement rectangle");
        return work;
    }
    internal static PixelRect WorkFromWindow(IntPtr window){
        PixelRect work;if(!TryWork(MonitorFromWindow(window,MonitorDefaultToNearest),out work))throw new InvalidOperationException("GetMonitorInfo failed for the window");
        return work;
    }
    static bool TryWork(IntPtr monitor,out PixelRect work){
        work=new PixelRect();if(monitor==IntPtr.Zero)return false;
        MONITORINFO info=new MONITORINFO();info.cbSize=Marshal.SizeOf(typeof(MONITORINFO));
        if(!GetMonitorInfoW(monitor,ref info))return false;
        work.Left=info.rcWork.Left;work.Top=info.rcWork.Top;work.Width=info.rcWork.Right-info.rcWork.Left;work.Height=info.rcWork.Bottom-info.rcWork.Top;return true;
    }
}
}
