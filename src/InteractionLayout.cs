using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace MDNote {public partial class MainWindow {
    void SetupInteractionLayout(){Resources[SystemParameters.VerticalScrollBarWidthKey]=17.0;Resources[SystemParameters.HorizontalScrollBarHeightKey]=14.0;/* 2.4.0: appinn #30 "scroll bar too thin" */Closed+=delegate{fileOpenClosed=true;CancelFileOpen();searchWindowClosed=true;CancelParallelSearch();CancelEditorRefresh();foreach(var state in documents)ReleaseTabSurfaces(state);textGutter.Editor=null;};}
    // Ctrl+wheel scrolls the source by four lines, independent of the preview's own scrolling.
    void FastScroll(object sender,MouseWheelEventArgs args){
        if((Keyboard.Modifiers&ModifierKeys.Control)!=0||args.Delta==0)return;
        if(source==null||source.Visibility!=Visibility.Visible)return;
        double pixels=fontSize*linePercent/100*4*args.Delta/120.0;
        source.ScrollToVerticalOffset(Math.Max(0,source.VerticalOffset-pixels));
        args.Handled=true;
    }
    // Wrap rewrites the source's wrapping and its scroll bar policy, the same view-level write as
    // the base font and the theme: under a live composition the wanted value is applied from the
    // store release. A second press in the same composition replaces the value.
    MenuItem wrapToggle;
    bool wrapQueued,wrapWanted;
    void SetWrapNow(bool value){
        if(StoreGuardLive()){wrapWanted=value;if(wrapQueued)return;wrapQueued=true;source.RunAfterComposition(delegate{wrapQueued=false;SetWrapNow(wrapWanted);});return;}
        ApplyWrapNow(value);
    }
    /* Wrap rewrites every line's row count, the same whole-view re-layout line spacing makes, so it
       keeps the place the same way (ReadingLayout.SetLinePercentNow): a caret the reader had scrolled
       away from stays away and the first visible character is put back at the same height; a visible
       caret's line is re-centred so the caret cannot be left outside the viewport. Turning wrap off
       also needs the horizontal correction, or a caret at the end of a long line stays off-screen. */
    void ApplyWrapNow(bool value){wordWrap=value;bool keepCaret=CaretLineOnScreen();var anchor=keepCaret?null:CaptureReadingAnchor();ApplyWrap();UpdateLivePreview();if(wrapToggle!=null)wrapToggle.IsChecked=value;if(keepCaret)PositionCaretInView();else RestoreReadingAnchor(anchor);QueueSettingsHorizontalClamp();}
}
// Word boundaries shared by the live surface: letters, digits and the underscore form
// a word; spaces and punctuation separate them. This is the same identifier class the
// AvalonEdit text view uses for its own double-click word selection.
internal static class WordBoundary {
    internal static bool IsWordChar(char value){return char.IsLetter(value)||char.IsDigit(value)||value=='_';}
}
}
