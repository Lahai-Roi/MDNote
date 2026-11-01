using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace MDNote {public partial class MainWindow {
    bool viewTransition;int viewTransitionVersion;Canvas transitionMask;double transitionMaskTop;
    void ClearTransitionMask(){if(transitionMask!=null){transitionMask.Children.Clear();transitionMask.Visibility=Visibility.Collapsed;}}
    void BeginViewTransition(){if((!viewTransition||transitionMask==null||transitionMask.Visibility!=Visibility.Visible||Math.Abs(transitionMaskTop-source.VerticalOffset)>1)&&surfacesReady&&area.ActualWidth>0&&area.ActualHeight>0){ClearTransitionMask();double captureTop=source.VerticalOffset;viewTransition=true;int width=(int)Math.Ceiling(area.ActualWidth),height=(int)Math.Ceiling(area.ActualHeight);var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);var backdrop=new DrawingVisual();using(var drawing=backdrop.RenderOpen())drawing.DrawRectangle(Background,null,new Rect(0,0,width,height));bitmap.Render(backdrop);bitmap.Render(area);bitmap.Freeze();transitionMaskTop=captureTop;if(transitionMask==null){transitionMask=new Canvas {IsHitTestVisible=false,ClipToBounds=true};Panel.SetZIndex(transitionMask,100);area.Children.Add(transitionMask);}transitionMask.Children.Clear();transitionMask.Children.Add(new Image {Source=bitmap,Width=area.ActualWidth,Height=area.ActualHeight});transitionMask.Visibility=Visibility.Visible;}viewTransition=true;viewTransitionVersion++;}
    void EndViewTransition(bool firstLayoutOnly=false){int version=viewTransitionVersion;Dispatcher.BeginInvoke(firstLayoutOnly?DispatcherPriority.Loaded:DispatcherPriority.ContextIdle,new Action(delegate{if(version==viewTransitionVersion){viewTransition=false;ClearTransitionMask();}}));}
    sealed class ViewBookmark {internal double Top,Left;internal int SourceOffset;}
    ViewBookmark CaptureView(){var mark=new ViewBookmark {Top=source.VerticalOffset,Left=source.HorizontalOffset};if(source.LineCount>0){int line=source.GetFirstVisibleLineIndex();if(line>=0)mark.SourceOffset=source.GetCharacterIndexFromLineIndex(line);}return mark;}
    // The deferred restore captures the document and the transition generation: a tab switch or a
    // document swap must not scroll another view, and a stale callback must not end a newer mask.
    void RestoreView(ViewBookmark mark){UpdateLayout();DocumentTab document=activeDocument;int version=viewTransitionVersion;Action restore=delegate{if(fileOpenClosed||version!=viewTransitionVersion||!Object.ReferenceEquals(activeDocument,document))return;source.ScrollToVerticalOffset(mark.Top);source.ScrollToHorizontalOffset(mark.Left);};restore();Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{if(version==viewTransitionVersion){restore();EndViewTransition();}}));}
    // A view-level command (custom colour re-render, base font, zoom, theme) restyles the whole
    // surface, the same document/selection write a toolbar command makes: under a live IME
    // composition it waits for the store release (SourceEditor.RunAfterComposition).
    sealed class PendingViewSettings {
        internal SourceEditor Editor;internal ICSharpCode.AvalonEdit.Document.TextDocument Document;
        internal string Family;internal double Size;internal bool Dark,Font,Render,Queued;
    }
    PendingViewSettings pendingViewSettings;
    // One callback for this surface/document, not one full render per wheel tick or menu press.
    // Absolute font choices replace earlier choices; zoom deltas build on the pending size.
    void QueueViewSettings(Action<PendingViewSettings> change){
        var pending=pendingViewSettings;
        if(pending==null||pending.Editor!=source||pending.Document!=source.Document){
            pending=new PendingViewSettings {Editor=source,Document=source.Document,Family=family,Size=fontSize,Dark=dark};
            pendingViewSettings=pending;
        }
        change(pending);
        if(pending.Queued)return;
        pending.Queued=true;
        source.RunAfterComposition(delegate{
            if(!Object.ReferenceEquals(pendingViewSettings,pending))return;
            pendingViewSettings=null;
            if(fileOpenClosed||source!=pending.Editor||source.Document!=pending.Document)return;
            if(pending.Font||pending.Render){
                /* The deferred theme has to run the same layout pass SetDarkNow runs: Theme() alone
                   leaves the line numbers on the old theme's Muted brush (UpdateGutter is the only
                   place that reads it back). */
                if(dark!=pending.Dark){dark=pending.Dark;Theme();LayoutMode();}
                SetBaseFontNow(pending.Family,pending.Size,pending.Render);
            }else SetDarkNow(pending.Dark);
        });
    }
    bool DeferViewSettings {get{return StoreGuardLive()||pendingViewSettings!=null&&pendingViewSettings.Editor==source&&pendingViewSettings.Document==source.Document;}}
    void RenderPreservingView(){if(DeferViewSettings){QueueViewSettings(delegate(PendingViewSettings pending){pending.Render=true;});return;}RenderPreservingViewNow();}
    // Render() assigns source.Text, which moves the caret to 0 and drops the selection (AvalonEdit's
    // Text setter), so the view commands save the selection next to the caret before the render.
    struct ViewSelection{internal int Start,Length,Caret;}
    ViewSelection CaptureViewSelection(){var selection=new ViewSelection {Caret=source.CaretIndex};if(source.SelectionLength>0){selection.Start=source.SelectionStart;selection.Length=source.SelectionLength;}return selection;}
    void RestoreViewSelection(ViewSelection selection){
        var document=source.Document;int total=document==null?0:document.TextLength;
        int caret=Math.Max(0,Math.Min(selection.Caret,total));
        int start=Math.Max(0,Math.Min(selection.Start,total)),length=Math.Max(0,Math.Min(selection.Length,total-start));
        if(length>0){source.Select(start,length);source.CaretIndex=Math.Max(start,Math.Min(caret,start+length));}
        else source.CaretIndex=caret;
    }
    void RenderPreservingViewNow(){FinishComposition();FlushDocumentChanges();var anchor=CaptureReadingAnchor();var selection=CaptureViewSelection();BeginViewTransition();Render();RestoreViewSelection(selection);RestoreReadingAnchor(anchor);RefreshCaretStatus();}
    void SetBaseFont(string name,double size){if(DeferViewSettings){QueueViewSettings(delegate(PendingViewSettings pending){pending.Family=name;pending.Size=Math.Max(8,Math.Min(48,size));pending.Font=true;});return;}SetBaseFontNow(name,size,false);}
    // force re-renders even when the font did not change (a queued colour change); an unchanged
    // font otherwise returns before the whole-document Render below, so Ctrl+wheel at the 50%/300%
    // limit and Ctrl+0 at the current size stop re-writing every line.
    void SetBaseFontNow(string name,double size,bool force){double clamped=Math.Max(8,Math.Min(48,size));/* the surface check keeps a fresh window (source still at its construction size) rendering once */if(!force&&String.Equals(name,family,StringComparison.Ordinal)&&Math.Abs(clamped-fontSize)<.001&&(source==null||Math.Abs(source.FontSize-clamped)<.001))return;FinishComposition();FlushDocumentChanges();var anchor=CaptureReadingAnchor();var selection=CaptureViewSelection();BeginViewTransition();family=name;fontSize=clamped;Render();RestoreViewSelection(selection);RestoreReadingAnchor(anchor);QueueSettingsHorizontalClamp();RefreshSizeBox();RefreshCaretStatus();}
    // Font/layout changes can leave a sideways offset that is no longer needed. Do not
    // BringIntoView here: it can restore the old offset and change the reading anchor.
    void ClampSettingsHorizontal(){
        if(wordWrap)return;
        var view=source.TextArea.TextView;view.EnsureVisualLines();Rect rect=source.GetRectFromCharacterIndex(source.CaretIndex);if(rect.IsEmpty)return;
        double right=view.TranslatePoint(new Point(view.ActualWidth,0),source).X;
        source.ScrollToHorizontalOffset(Math.Max(0,rect.Right+source.HorizontalOffset-right+1));
    }
    void QueueSettingsHorizontalClamp(){
        if(wordWrap)return;var tab=activeDocument;var editor=source;int version=viewTransitionVersion;
        Action apply=delegate{if(fileOpenClosed||version!=viewTransitionVersion||!Object.ReferenceEquals(tab,activeDocument)||source!=editor)return;UpdateLayout();ClampSettingsHorizontal();};
        apply();Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,apply);
    }
    void PositionCaretInView(){UpdateLayout();MarkdownLivePreview.Session.SafeBringCaretToView(source.TextArea);UpdateLayout();var rect=source.GetRectFromCharacterIndex(source.CaretIndex);if(!rect.IsEmpty)source.ScrollToVerticalOffset(Math.Max(0,source.VerticalOffset+rect.Top-source.ViewportHeight/3));}
}}
