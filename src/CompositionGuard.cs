using System;
using System.Windows;
namespace MDNote {public partial class MainWindow {
    // True while a composition of the source may still own the text store: an IME composition in
    // flight, or a result whose store release has not been posted yet. Every document/selection
    // write that a command makes is deferred while this is true (SourceEditor.RunAfterComposition).
    bool RichCompositionLive{
        get{
            if(imeComposing)return true;
            if(source!=null&&source.Visibility==Visibility.Visible&&source.ImeBusy)return true;
            return false;
        }
    }
    // The store itself, ignoring the post-commit grace window.
    bool CompositionOwnsStore(){return imeComposing||NativeCompositionOwnsStore();}
    // The store query without the flags this file owns. The source is the only composing surface.
    bool NativeCompositionOwnsStore(){
        if(source==null||source.Visibility!=Visibility.Visible)return false;
        return source.ImeBusy||source.ImeCompositionActive();
    }
}}
