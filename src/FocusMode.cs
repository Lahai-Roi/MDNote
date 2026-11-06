using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
namespace MDNote {
/* 2.2.0 (user decision 2026-11-05): View > 집중 모드 / 타자기 모드. Both are off by default, saved in
   the settings file (focusMode / typewriterMode), shared by every window and every tab, and work the
   same in the live and the basic view.
   집중 모드: the caret's block (FocusBlocks.Find) is drawn normally; every other visual line gets a
   translucent page-coloured rectangle in a layer just below the caret layer, which is above the text
   and the live preview's inline pictures, so the caret stays on top. The line-number gutter is outside
   TextView and stays undimmed. The block is recomputed only when the caret changes line or an edit
   changes the caret line's kind (one GetText of that line per keystroke); the layer is repainted on
   VisualLinesChanged/ScrollOffsetChanged, which AvalonEdit raises for its own layers anyway.
   타자기 모드: after an edit or a navigation key the caret's visual row is scrolled to the middle of the
   editor, once per change at Loaded priority (after AvalonEdit's BringCaretToView and MDNote's
   caret-row checks, which then find the caret on screen and do nothing). A mouse click, a wheel turn
   or a scroll-bar drag never scrolls. The space that lets the last line reach the middle is a larger
   TextView bottom margin: ApplySourceInset (Editor.cs) already slides a 24 px end margin in over the
   last 24 px of scrolling; while the mode is on the same ramp continues up to half the viewport. */
public partial class MainWindow {
    // focusMode: 0 off, 1 paragraph (the caret's FocusBlocks block), 2 line (the caret's source line).
    int focusMode;
    // The level a click on the 집중 모드 row itself turns on with (user 2026-11-06): the last level chosen
    // in the submenu, 문단 until the user picks 줄. Off never changes it, so the choice survives a restart.
    int focusLastLevel=1;
    bool typewriterMode;
    MenuItem focusToggle, typewriterToggle;
    readonly MenuItem[] focusChoices=new MenuItem[3];
    sealed class FocusChoice {internal readonly int Level;internal FocusChoice(int level){Level=level;}}
    static readonly string[] FocusChoiceNames={"끄기","문단","줄"};
    static readonly string[] FocusChoiceKeys={"집중 모드: 끄기","집중 모드: 문단","집중 모드: 줄"};
    // 보기 > 집중 모드 > 끄기 / 문단 / 줄. The parent shows the check mark while 문단 or 줄 is selected, so
    // the state is visible from the 보기 menu itself. The three Korean words are also other labels' keys
    // ("줄" is the status bar's " Ln"), so these items are translated here and TranslateTree skips them.
    void BuildFocusMenu(MenuItem view){
        focusToggle=new MenuItem {Header="집중 모드",IsChecked=focusMode!=0};
        foreach(int level in new[]{1,2,0}){/* user 11-05: 문단, 줄, 끄기 in that order */
            int chosen=level;var item=new MenuItem {Header=FocusChoiceNames[level],IsCheckable=true,IsChecked=focusMode==level,Tag=new FocusChoice(level)};
            item.Click+=delegate{SetFocusLevel(chosen);};
            focusChoices[level]=item;focusToggle.Items.Add(item);
        }
        /* user 2026-11-06: a click on the 집중 모드 row itself turns focus mode on (the last level) or off
           without going into the submenu. The walk up from the click decides whether it landed on this
           row's header: a click inside the submenu is already claimed by its own MenuItem, so only the
           row itself reaches us. Keyboard behaviour (Enter / Right open the submenu) is untouched. */
        focusToggle.PreviewMouseLeftButtonUp+=delegate(object sender,MouseButtonEventArgs e){
            if(!Object.ReferenceEquals(ClickedMenuItem(e.OriginalSource as DependencyObject),focusToggle))return;
            e.Handled=true;ToggleFocusFromHeader();
            MenuItem top=TopMenuItem(focusToggle);if(top!=null)top.IsSubmenuOpen=false;
        };
        view.Items.Add(focusToggle);
        typewriterToggle=new MenuItem {Header="타자기 모드",IsCheckable=true,IsChecked=typewriterMode};typewriterToggle.Click+=delegate{SetTypewriterMode(typewriterToggle.IsChecked);};view.Items.Add(typewriterToggle);
    }
    string FocusChoiceText(int level){return language=="ko"?FocusChoiceNames[level]:T(FocusChoiceKeys[level]);}
    // Called first by TranslateTree: true when the element is one of the three choices.
    bool TranslateFocusChoice(DependencyObject element){
        var item=element as MenuItem;if(item==null)return false;
        if(TranslateHeadingStyleChoice(element))return true;/* 제목 단계 표시's three styles (ReadingLayout.cs) */
        var choice=item.Tag as FocusChoice;if(choice==null)return false;
        item.Header=FocusChoiceText(choice.Level);return true;
    }
    // The surface the two modes were last applied to; a tab switch puts another SourceEditor in
    // `source`, and the first layout pass after it applies the modes there (FocusLayoutUpdated).
    SourceEditor focusEditor;
    bool focusLayoutHooked;
    void SetFocusLevel(int level){SetFocusModes(level,typewriterMode,true);}
    void SetTypewriterMode(bool value){SetFocusModes(focusMode,value,true);}
    // user 2026-11-06: the 집중 모드 row toggles. While off it comes back at the level last chosen.
    void ToggleFocusFromHeader(){SetFocusLevel(focusMode!=0?0:focusLastLevel);}
    /* The MenuItem a click landed in. A header's text and check glyph are template elements with no
       logical parent, so the visual tree is walked first and the logical one is the fallback (from a
       template element it is often the only link out of the menu's popup). */
    static MenuItem ClickedMenuItem(DependencyObject node){
        while(node!=null){
            var item=node as MenuItem;if(item!=null)return item;
            DependencyObject parent=null;
            try{
                var visual=node as Visual;parent=visual==null?null:VisualTreeHelper.GetParent(visual);
                if(parent==null&&(node is FrameworkElement||node is FrameworkContentElement))parent=LogicalTreeHelper.GetParent(node);
            }catch(InvalidOperationException){parent=null;}
            node=parent;
        }
        return null;
    }
    // The top-level menu item (보기) that holds this row; the row itself when nothing above is a MenuItem.
    static MenuItem TopMenuItem(MenuItem item){
        MenuItem top=item;DependencyObject parent=item.Parent;
        while(parent!=null){
            var menu=parent as MenuItem;
            if(menu!=null){top=menu;parent=menu.Parent;continue;}
            var element=parent as FrameworkElement;parent=element==null?null:element.Parent;
        }
        return top;
    }
    void SetFocusModes(int focus,bool typewriter,bool save){
        if(focus<0||focus>2)focus=0;
        bool changed=focusMode!=focus||typewriterMode!=typewriter;
        focusMode=focus;typewriterMode=typewriter;if(focus!=0)focusLastLevel=focus;
        if(focusToggle!=null&&focusToggle.IsChecked!=(focus!=0))focusToggle.IsChecked=focus!=0;
        // Radio-style: the clicked choice toggles itself first, so every choice is set from the level.
        for(int i=0;i<focusChoices.Length;i++)if(focusChoices[i]!=null&&focusChoices[i].IsChecked!=(i==focus))focusChoices[i].IsChecked=i==focus;
        if(typewriterToggle!=null&&typewriterToggle.IsChecked!=typewriter)typewriterToggle.IsChecked=typewriter;
        SyncFocusModes();
        if(!save||!changed)return;
        SavePreferences();
        // One setting for every window, like the theme: the other windows follow without saving again.
        var app=Application.Current;if(app==null)return;
        foreach(Window candidate in app.Windows){var window=candidate as MainWindow;if(window!=null&&!Object.ReferenceEquals(window,this))window.SetFocusModes(focus,typewriter,false);}
    }
    void SyncFocusModes(){
        if(!focusLayoutHooked&&(focusMode!=0||typewriterMode)){focusLayoutHooked=true;LayoutUpdated+=FocusLayoutUpdated;}
        var editor=source;focusEditor=editor;if(editor==null)return;
        FocusSurface surface=FocusSurface.Of(editor);
        if(surface==null){if(focusMode==0&&!typewriterMode)return;surface=FocusSurface.Create(editor,this);}
        surface.Sync(focusMode,typewriterMode);
    }
    // A reference compare per layout pass; the work happens only when the shown surface changed.
    void FocusLayoutUpdated(object sender,EventArgs e){if(!Object.ReferenceEquals(source,focusEditor))SyncFocusModes();}
    // The fence state the live session already keeps (any distance from the fence), or null when no
    // session is attached to this surface (plain text, a detached view): FocusBlocks then counts
    // fences inside its own 200-line window.
    Func<int,int> FocusCodeState(SourceEditor editor){
        var session=livePreview;
        if(session==null||session.IsDisposed||!Object.ReferenceEquals(session.Editor,editor)||!Object.ReferenceEquals(session.Document,editor.Document))return null;
        TextDocument document=editor.Document;
        return delegate(int number){var info=session.Info(document.GetLineByNumber(number));return info==null?0:info.FenceLine?2:info.Code?1:0;};
    }
    // Probe access (FocusModeProbe.cs).
    internal SourceEditor FocusProbeEditor{get{return source;}}
    internal bool FocusModeOn{get{return focusMode!=0;}}
    internal int FocusModeLevel{get{return focusMode;}}
    internal bool TypewriterModeOn{get{return typewriterMode;}}
    internal void FocusProbeSet(bool focus,bool typewriter,bool save){SetFocusModes(focus?1:0,typewriter,save);}
    internal void FocusProbeSetLevel(int focus,bool typewriter,bool save){SetFocusModes(focus,typewriter,save);}
    internal void FocusProbeToggleHeader(){ToggleFocusFromHeader();}
    internal int FocusProbeLastLevel{get{return focusLastLevel;}}
    internal MenuItem FocusProbeParent{get{return focusToggle;}}
    internal MenuItem[] FocusProbeChoices{get{return focusChoices;}}
    internal string FocusProbeChoiceText(int level){return FocusChoiceText(level);}
    // The saved value, as another window or a restart would read it.
    internal void FocusProbeApplyPreferences(ViewPreferences value){SetFocusModes(value.FocusMode,value.TypewriterMode,false);}
    internal static FocusSurface FocusSurfaceOf(SourceEditor editor){return FocusSurface.Of(editor);}

    /* Per-editor state: every tab has its own SourceEditor, so the layer, the block and the scroll
       bookkeeping live with the editor (an attached property), and the window only says which modes
       are on. Event handlers stay attached for the editor's lifetime and return at once while the
       mode they serve is off. */
    internal sealed class FocusSurface {
        static readonly DependencyProperty SurfaceProperty=DependencyProperty.RegisterAttached("MDNoteFocusSurface",typeof(FocusSurface),typeof(FocusSurface),new PropertyMetadata(null));
        internal static FocusSurface Of(SourceEditor editor){return editor==null?null:editor.GetValue(SurfaceProperty) as FocusSurface;}
        internal static FocusSurface Create(SourceEditor editor,MainWindow owner){var surface=new FocusSurface(editor,owner);editor.SetValue(SurfaceProperty,surface);return surface;}
        internal const double DimOpacity=0.6;
        readonly SourceEditor editor;readonly MainWindow owner;readonly TextView view;
        FocusDimLayer layer;TextDocument document;
        bool focusOn,typeOn;int level;
        // The kept (undimmed) lines: the caret's block, widened to the selection's lines.
        int blockStart,blockEnd,selectionStart,selectionEnd;
        int caretLine;int caretKind=-1;
        DispatcherOperation blockWork,centreWork;
        // Typewriter: an edit or a navigation key arms one centring; the row it centred last is kept
        // so a change that leaves the caret on the same row (an IME composition update, a typed
        // character) does not scroll again.
        bool centrePending;int centrePasses;double centredRowTop=Double.NaN;int centredLine;
        bool marginHooked,applyingMargin;
        DependencyPropertyDescriptor marginDescriptor;
        // Counters read by the probe.
        internal int BlockComputes,Centres,Redraws;
        internal int BlockStart{get{return blockStart;}}
        internal int BlockEnd{get{return blockEnd;}}
        internal int Level{get{return level;}}
        internal bool LayerAttached{get{return layer!=null&&view.Layers.IndexOf(layer)>=0;}}
        internal int DimmedRectangles{get{return layer==null?0:layer.LastRectangles;}}
        FocusSurface(SourceEditor editor,MainWindow owner){
            this.editor=editor;this.owner=owner;view=editor.TextArea.TextView;
            editor.TextArea.Caret.PositionChanged+=CaretChanged;
            editor.TextArea.SelectionChanged+=SelectionChanged;
            editor.DocumentChanged+=EditorDocumentChanged;HookDocument();
            view.VisualLinesChanged+=ViewChanged;view.ScrollOffsetChanged+=ViewChanged;
            editor.TextArea.PreviewKeyDown+=KeyPressed;
            editor.PreviewMouseDown+=MouseUsed;editor.PreviewMouseWheel+=MouseUsed;
            editor.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler(ScrollChanged));
            editor.Unloaded+=delegate{UnhookMargin();};
            editor.Loaded+=EditorLoaded;
            editor.TextArea.SizeChanged+=AreaSizeChanged;
        }
        // The editor area changed size (a window resize, a side panel opening): the middle moved, and
        // the caret row stays at its old distance from the top until the next key. Recompute the bottom
        // space for the new height and centre once. Our own bottom-space margin is on TextView, inside
        // the text area, so it never changes this size; applyingMargin also covers a synchronous echo.
        // The centring itself is the usual queued one, so a drag that raises many size changes costs
        // one scroll per layout pass and the scroll it makes changes no size (no loop).
        void AreaSizeChanged(object sender,SizeChangedEventArgs e){
            if(!typeOn||applyingMargin)return;
            if(Math.Abs(e.NewSize.Height-e.PreviousSize.Height)<=0.5&&Math.Abs(e.NewSize.Width-e.PreviousSize.Width)<=0.5)return;
            ApplyBottomSpace();ArmCentre();
        }
        void ArmCentre(){centredRowTop=Double.NaN;centrePending=true;centrePasses=0;QueueCentre();}
        // A programmatic caret jump (a "#heading" link): centre the target row like an edit would. It
        // is posted at Loaded so that the mouse press that started the jump has finished routing:
        // MouseUsed (a plain click never scrolls) cancels any centring armed before it, and the order
        // between it and the link handler is not guaranteed.
        internal void CentreAfterJump(){
            if(!typeOn)return;
            editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(delegate{if(typeOn)ArmCentre();}));
        }
        // Replace All edits the whole document but promises the reader's view stays put (Search.cs
        // RestoreReplaceView): the centring that edit armed is dropped; the next key centres again.
        internal void KeepViewAfterBulkEdit(){if(!typeOn)return;CancelCentre();centredRowTop=Double.NaN;}
        // The editor entered the tree: a tab switch back, or the first show after a restore/open. Its
        // own Loaded handler (ApplySourceInset, registered first) has just reset the end margin to the
        // plain 24 px, which would clamp the offset and leave the last rows at the bottom edge; give
        // the half-view margin back and centre the caret row once, as a document change does.
        void EditorLoaded(object sender,RoutedEventArgs e){
            if(!typeOn)return;
            HookMargin();ApplyBottomSpace();
            ArmCentre();
        }
        internal void Sync(int focus,bool typewriter){
            bool focusWas=focusOn,typeWas=typeOn;int levelWas=level;focusOn=focus!=0;level=focus;typeOn=typewriter;
            if(focusOn){
                if(layer==null)layer=new FocusDimLayer(this);
                if(view.Layers.IndexOf(layer)<0)view.InsertLayer(layer,KnownLayer.Caret,LayerInsertionPosition.Below);
                if(!focusWas||levelWas!=level){caretLine=0;QueueBlock();}
                layer.InvalidateVisual();
            }else if(layer!=null){
                int index=view.Layers.IndexOf(layer);if(index>=0)view.Layers.RemoveAt(index);
                blockStart=blockEnd=0;selectionStart=selectionEnd=0;
            }
            if(typewriter){
                if(editor.IsLoaded)HookMargin();
                ApplyBottomSpace();
                if(!typeWas){centredRowTop=Double.NaN;centrePending=true;QueueCentre();}
            }else{
                UnhookMargin();CancelCentre();
                // Back to the plain 24 px end margin; the scroll offset is clamped by the next arrange.
                if(typeWas)ApplySourceInset(editor);
            }
        }
        void EditorDocumentChanged(object sender,EventArgs e){HookDocument();caretLine=0;centredRowTop=Double.NaN;if(focusOn)QueueBlock();}
        void HookDocument(){
            if(document!=null)document.Changed-=DocumentChanged;
            document=editor.Document;if(document!=null)document.Changed+=DocumentChanged;
        }
        static readonly char[] LineBreaks={'\r','\n'};
        void DocumentChanged(object sender,DocumentChangeEventArgs e){
            if(focusOn){
                bool lines=(e.InsertionLength>0&&e.InsertedText.IndexOfAny(LineBreaks,0,e.InsertionLength)>=0)||(e.RemovalLength>0&&e.RemovedText.IndexOfAny(LineBreaks,0,e.RemovalLength)>=0);
                if(lines)QueueBlock();
                else{
                    // O(edited line): the block can only change when this line's kind changed
                    // (a "# " typed at its start, a line that became blank, a fence, a "|").
                    DocumentLine line=document.GetLineByOffset(Math.Min(e.Offset,document.TextLength));
                    if(line.LineNumber!=caretLine||(level==1&&FocusBlocks.Kind(document.GetText(line))!=caretKind))QueueBlock();
                }
            }
            // A load or a tab render replaces the text with the window's loading flag set; that is
            // not the user's edit and must not move the reading position.
            if(typeOn&&!owner.loading){centrePending=true;QueueCentre();}
        }
        void CaretChanged(object sender,EventArgs e){if(focusOn&&editor.TextArea.Caret.Line!=caretLine)QueueBlock();}
        void SelectionChanged(object sender,EventArgs e){
            if(!focusOn||layer==null)return;
            int start,end;SelectionLines(out start,out end);
            if(start!=selectionStart||end!=selectionEnd){selectionStart=start;selectionEnd=end;Redraw();}
        }
        void SelectionLines(out int start,out int end){
            start=end=0;var selection=editor.TextArea.Selection;if(selection==null||selection.IsEmpty||document==null)return;
            ISegment span=selection.SurroundingSegment;if(span==null)return;
            int from=Math.Max(0,Math.Min(span.Offset,document.TextLength)),to=Math.Max(from,Math.Min(span.EndOffset,document.TextLength));
            start=document.GetLineByOffset(from).LineNumber;end=document.GetLineByOffset(to).LineNumber;
        }
        void ViewChanged(object sender,EventArgs e){if(focusOn&&layer!=null)layer.InvalidateVisual();}
        void Redraw(){if(layer!=null){Redraws++;layer.InvalidateVisual();}}
        void QueueBlock(){
            if(blockWork!=null)return;
            // Render priority: after the edit and the live session's own change handling, before the frame.
            blockWork=editor.Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(ComputeBlock));
        }
        void ComputeBlock(){
            blockWork=null;if(!focusOn||document==null)return;
            BlockComputes++;
            int line=Math.Max(1,Math.Min(document.LineCount,editor.TextArea.Caret.Line)),start,end;
            // Line level: only the caret's source line (all of its wrapped rows); no block search at all.
            if(level==2){start=end=line;caretKind=0;}
            else{FocusBlocks.Find(document,line,owner.PlainText?null:owner.FocusCodeState(editor),owner.PlainText,out start,out end);caretKind=FocusBlocks.Kind(document.GetText(document.GetLineByNumber(line)));}
            caretLine=line;
            int selStart,selEnd;SelectionLines(out selStart,out selEnd);
            if(start!=blockStart||end!=blockEnd||selStart!=selectionStart||selEnd!=selectionEnd){blockStart=start;blockEnd=end;selectionStart=selStart;selectionEnd=selEnd;Redraw();}
        }
        // The kept range in line numbers; both 0 before the first computation (nothing is dimmed then).
        internal bool Keeps(int first,int last){
            if(blockStart<=0)return true;
            if(last>=blockStart&&first<=blockEnd)return true;
            return selectionStart>0&&last>=selectionStart&&first<=selectionEnd;
        }
        internal bool FocusOn{get{return focusOn;}}
        internal TextView View{get{return view;}}
        internal SourceEditor Editor{get{return editor;}}
        internal Color PageColor(){
            var brush=editor.Background as SolidColorBrush;if(brush!=null&&brush.Color.A==255)return brush.Color;
            var paper=owner.Background as SolidColorBrush;return paper!=null?paper.Color:(owner.dark?Color.FromRgb(0x27,0x27,0x27):Colors.White);
        }

        // ---- 타자기 모드 ----
        void KeyPressed(object sender,KeyEventArgs e){
            if(!typeOn)return;
            Key key=e.Key==Key.System?e.SystemKey:e.Key;
            // Modifiers alone move nothing; ImeProcessed keys are composition updates (the commit
            // that follows changes the document and arms the centring there).
            switch(key){case Key.LeftCtrl:case Key.RightCtrl:case Key.LeftShift:case Key.RightShift:case Key.LeftAlt:case Key.RightAlt:case Key.LWin:case Key.RWin:case Key.ImeProcessed:case Key.None:return;}
            centrePending=true;QueueCentre();
        }
        // The mouse never scrolls; the row it leaves the caret on is centred by the next key or edit.
        void MouseUsed(object sender,EventArgs e){if(!typeOn)return;CancelCentre();centredRowTop=Double.NaN;}
        void QueueCentre(){
            if(centreWork!=null)return;
            // Loaded: after the key's own caret move, AvalonEdit's BringCaretToView and the layout
            // of the edited line, so the row is measured where it really is.
            centreWork=editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(Centre));
        }
        void CancelCentre(){if(centreWork!=null){centreWork.Abort();centreWork=null;}centrePending=false;centrePasses=0;}
        void Centre(){
            centreWork=null;if(!typeOn||!centrePending)return;
            if(document==null||!editor.IsLoaded||editor.ViewportHeight<=0||editor.TextArea.ActualHeight<=0){CancelCentre();return;}
            var caret=editor.TextArea.Caret;int line=caret.Line;bool verify=centrePasses>0;
            // An IME composition rewrites the same line; it never scrolls while the caret stays there.
            if(!verify&&line==centredLine&&!Double.IsNaN(centredRowTop)&&(editor.ImeBusy||editor.ImeCompositionActive())){CancelCentre();return;}
            double top=view.GetVisualPosition(caret.Position,VisualYPosition.LineTop).Y,bottom=view.GetVisualPosition(caret.Position,VisualYPosition.LineBottom).Y;
            // Same row as the last centring: typing along a line costs this one measurement.
            if(!verify&&line==centredLine&&!Double.IsNaN(centredRowTop)&&Math.Abs(top-centredRowTop)<0.5){CancelCentre();return;}
            centredRowTop=top;centredLine=line;
            double target=CentreOffset((top+bottom)/2);
            if(Math.Abs(target-editor.VerticalOffset)>1){
                EnsureBottomSpace(target);editor.ScrollToVerticalOffset(target);Centres++;
                // Lines scrolled into view are measured by the next layout and may move the row by
                // their real heights; one or two checks after that layout settle it.
                if(centrePasses<2){centrePasses++;QueueCentre();return;}
            }
            CancelCentre();
        }
        // The offset that puts a row centre (document coordinates) at the middle of the text area.
        // The top inset (Editor.cs SourceInset: 24 px shrinking to 0 over the first 24 px of
        // scrolling) is above TextView inside the text area, so a target under 24 solves for it.
        internal double CentreOffset(double rowCentre){
            double half=editor.TextArea.ActualHeight/2,target=rowCentre-half;
            if(target<24)target=(rowCentre+24-half)/2;
            return Math.Max(0,target);
        }
        double BottomLimit(){return Math.Max(24,editor.TextArea.ActualHeight/2);}
        /* The end margin while the mode is on. `remaining` is ApplySourceInset's own measure (space
           left to scroll before the plain end); it does not depend on the bottom margin, because the
           viewport shrinks by exactly what the margin grows. ApplySourceInset's 24 px ramp is kept as
           is; past the end the margin equals the distance scrolled beyond it (up to half the
           viewport), so the document's last line always sits on the margin and nothing is clipped. */
        double BottomFor(double remaining){return Math.Max(Math.Max(0,Math.Min(24,24-remaining)),Math.Min(BottomLimit(),-remaining));}
        /* ViewportHeight + Margin.Bottom is the text area's height minus the top inset, but only
           while both come from the same layout: inside ApplySourceInset's margin write the margin is
           new and the viewport is still the old one. The text area's height and the inset for the
           offset (SourceInset's rule) give the same sum without that skew. */
        double Remaining(double offset){return editor.ExtentHeight-(editor.TextArea.ActualHeight-Math.Max(0,24-offset))-offset;}
        void ApplyBottomSpace(){
            if(!typeOn||applyingMargin||!editor.IsLoaded)return;
            double remaining=Remaining(editor.VerticalOffset);
            // A document that fits the viewport keeps ApplySourceInset's margins.
            if(editor.VerticalOffset<=0&&remaining+editor.VerticalOffset<=0)return;
            SetBottom(BottomFor(remaining));
        }
        void EnsureBottomSpace(double target){
            double bottom=BottomFor(Remaining(target));if(bottom>view.Margin.Bottom+0.1)SetBottom(bottom);
        }
        void SetBottom(double bottom){
            Thickness margin=view.Margin;if(Math.Abs(margin.Bottom-bottom)<=0.1)return;
            applyingMargin=true;try{view.Margin=new Thickness(margin.Left,margin.Top,margin.Right,bottom);}finally{applyingMargin=false;}
        }
        void ScrollChanged(object sender,ScrollChangedEventArgs e){if(typeOn&&Object.ReferenceEquals(sender,editor))ApplyBottomSpace();}
        // ApplySourceInset also runs outside ScrollChanged (its Loaded re-check); a margin it writes
        // is corrected in the same call, before any frame shows the shorter margin.
        void MarginChanged(object sender,EventArgs e){if(typeOn&&!applyingMargin)ApplyBottomSpace();}
        void HookMargin(){
            if(marginHooked)return;
            if(marginDescriptor==null)marginDescriptor=DependencyPropertyDescriptor.FromProperty(FrameworkElement.MarginProperty,typeof(FrameworkElement));
            if(marginDescriptor==null)return;
            marginDescriptor.AddValueChanged(view,MarginChanged);marginHooked=true;
        }
        // The descriptor keeps a strong reference to the view; an editor that leaves the tree (a tab
        // switch or close) drops it, and Sync hooks it again when the tab is shown.
        void UnhookMargin(){if(!marginHooked)return;marginDescriptor.RemoveValueChanged(view,MarginChanged);marginHooked=false;}
    }

    // Drawn between the text (with the live preview's inline pictures) and the caret layer. It never
    // takes the mouse, so clicks and drags reach the text exactly as without it.
    internal sealed class FocusDimLayer:UIElement {
        readonly FocusSurface owner;Color brushColor;SolidColorBrush brush;
        internal int LastRectangles;
        internal FocusDimLayer(FocusSurface owner){this.owner=owner;IsHitTestVisible=false;Focusable=false;}
        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters){return null;}
        protected override GeometryHitTestResult HitTestCore(GeometryHitTestParameters hitTestParameters){return null;}
        protected override void OnRender(DrawingContext drawingContext){
            LastRectangles=0;TextView view=owner.View;
            if(!owner.FocusOn||view==null||!view.VisualLinesValid)return;
            Color page=owner.PageColor();
            if(brush==null||page!=brushColor){brushColor=page;brush=new SolidColorBrush(Color.FromArgb((byte)Math.Round(255*FocusSurface.DimOpacity),page.R,page.G,page.B));brush.Freeze();}
            // Cover the editor's side padding too (list markers are drawn left of the text), but not
            // the line-number gutter, which is a separate element left of the editor.
            double left=0,right=view.ActualWidth;
            SourceEditor editor=owner.Editor;
            if(editor!=null&&editor.IsAncestorOf(view)){Point origin=view.TranslatePoint(new Point(0,0),editor);left=-Math.Max(0,origin.X);right=view.ActualWidth+editor.Padding.Right;}
            double scroll=view.ScrollOffset.Y,runTop=0,runBottom=0;bool open=false;
            foreach(VisualLine line in view.VisualLines){
                if(line.FirstDocumentLine.IsDeleted||line.LastDocumentLine.IsDeleted)continue;
                double top=line.VisualTop-scroll,bottom=top+line.Height;
                if(owner.Keeps(line.FirstDocumentLine.LineNumber,line.LastDocumentLine.LineNumber)){if(open){Fill(drawingContext,left,right,runTop,runBottom);open=false;}continue;}
                if(open&&top<=runBottom+0.5){runBottom=bottom;continue;}
                if(open)Fill(drawingContext,left,right,runTop,runBottom);
                runTop=top;runBottom=bottom;open=true;
            }
            if(open)Fill(drawingContext,left,right,runTop,runBottom);
        }
        void Fill(DrawingContext drawingContext,double left,double right,double top,double bottom){
            if(bottom<=top||right<=left)return;
            drawingContext.DrawRectangle(brush,null,new Rect(left,top,right-left,bottom-top));LastRectangles++;
        }
    }
}

/* The caret's block for 집중 모드. Reads at most Window lines above and below the caret line (each
   line once, only as far as the block reaches), never the whole document.
   - a blank line is its own block; a heading (ATX, or a setext paragraph with its underline) and a
     thematic break are their own blocks;
   - a fenced code block is one block: the live session's fence state when it is attached (exact at
     any distance), otherwise fences counted inside the window;
   - a table (a run of "|" lines with a delimiter row) and a quote run (">" lines and their lazy
     continuation lines) are one block each;
   - a list item is its own block together with the following lines indented deeper than its marker
     (continuations and child items); a continuation line belongs to the nearest item above it;
   - anything else is the run of plain non-blank lines around the caret. */
internal static class FocusBlocks {
    internal const int Window=200;
    internal const int Blank=0,Plain=1,Heading=2,Rule=3,Quote=4,ListItem=5,EqualsLine=6,DashLine=7,Fence=8;
    // A line's signature: kind in the low 4 bits, leading indent (tabs to 4) in the next 10, and a
    // bit for a "|" anywhere. FocusSurface compares it per keystroke to skip a recomputation.
    internal static int Kind(string text){
        int i=0,indent=0;
        while(i<text.Length&&(text[i]==' '||text[i]=='\t')){indent+=text[i]=='\t'?4-(indent%4):1;i++;}
        if(i>=text.Length)return Blank;
        bool pipe=text.IndexOf('|')>=0;int kind=Plain;char c=text[i];
        if(indent<=3){
            if(c=='#'){int n=0;while(i+n<text.Length&&text[i+n]=='#')n++;if(n<=6&&(i+n==text.Length||text[i+n]==' '||text[i+n]=='\t'))kind=Heading;}
            else if(c=='>')kind=Quote;
            else if((c=='`'||c=='~')&&RunLength(text,i,c)>=3)kind=Fence;
            else if(c=='='&&OnlyRun(text,i,'='))kind=EqualsLine;
            else if(c=='-'&&OnlyRun(text,i,'-')&&RunLength(text,i,'-')>=2)kind=DashLine;
            else if((c=='-'||c=='*'||c=='_')&&IsRule(text,i,c))kind=Rule;
        }
        if(kind==Plain&&IsListMarker(text,i))kind=ListItem;
        return kind|(Math.Min(indent,1023)<<4)|(pipe?1<<14:0);
    }
    internal static int KindOf(int signature){return signature&15;}
    internal static int IndentOf(int signature){return (signature>>4)&1023;}
    internal static bool HasPipe(int signature){return (signature&(1<<14))!=0;}
    static int RunLength(string text,int at,char c){int n=0;while(at+n<text.Length&&text[at+n]==c)n++;return n;}
    // The run of c from `at`, then only spaces to the end.
    static bool OnlyRun(string text,int at,char c){int i=at+RunLength(text,at,c);while(i<text.Length&&(text[i]==' '||text[i]=='\t'))i++;return i==text.Length;}
    static bool IsRule(string text,int at,char c){int count=0;for(int i=at;i<text.Length;i++){if(text[i]==c)count++;else if(text[i]!=' '&&text[i]!='\t')return false;}return count>=3;}
    static bool IsListMarker(string text,int at){
        char c=text[at];
        if(c=='-'||c=='*'||c=='+')return at+1==text.Length||text[at+1]==' '||text[at+1]=='\t';
        int i=at;while(i<text.Length&&i-at<9&&text[i]>='0'&&text[i]<='9')i++;
        if(i==at||i>=text.Length||(text[i]!='.'&&text[i]!=')'))return false;
        return i+1==text.Length||text[i+1]==' '||text[i+1]=='\t';
    }
    internal static bool IsDelimiterRow(string text){
        bool dash=false,bar=false;
        foreach(char c in text){if(c=='-')dash=true;else if(c=='|')bar=true;else if(c!=':'&&c!=' '&&c!='\t')return false;}
        return dash&&bar;
    }
    // codeState(lineNumber): 0 outside a fence, 1 inside, 2 a fence line; null = count raw fences.
    internal static void Find(TextDocument document,int line,Func<int,int> codeState,bool plainText,out int start,out int end){
        var scan=new Scan(document,line,codeState);start=end=line;
        int kind=KindOf(scan.Sig(line));
        // A blank line inside a fence is code, so the fence test comes before the blank one.
        if(!plainText&&scan.Code(line)>0){while(start>scan.Lo&&scan.Code(start-1)>0)start--;while(end<scan.Hi&&scan.Code(end+1)>0)end++;return;}
        if(kind==Blank)return;
        if(plainText){while(start>scan.Lo&&!scan.Blank(start-1))start--;while(end<scan.Hi&&!scan.Blank(end+1))end++;return;}
        if(kind==Heading||kind==Rule)return;
        if(kind==DashLine){
            // A setext underline belongs to the paragraph above it; without one it is a rule.
            if(start>scan.Lo&&scan.ParagraphLine(start-1)){start--;while(start>scan.Lo&&scan.ParagraphLine(start-1))start--;}
            return;
        }
        if(HasPipe(scan.Sig(line))&&kind==Plain){
            int top=line,bottom=line;
            while(top>scan.Lo&&scan.TableLine(top-1))top--;
            while(bottom<scan.Hi&&scan.TableLine(bottom+1))bottom++;
            for(int n=top;n<=bottom;n++)if(IsDelimiterRow(scan.Text(n))){start=top;end=bottom;return;}
        }
        if(kind==Quote||kind==Plain||kind==EqualsLine){
            int up=line;while(up>scan.Lo&&scan.QuoteOrLazy(up-1))up--;
            for(int n=up;n<=line;n++)if(KindOf(scan.Sig(n))==Quote){
                start=n;while(end<scan.Hi&&scan.QuoteOrLazy(end+1))end++;return;
            }
        }
        int item=-1;
        if(kind==ListItem)item=line;
        else if(IndentOf(scan.Sig(line))>0){
            // The nearest item above with a shallower marker, and every line between deeper than it.
            int limit=IndentOf(scan.Sig(line));
            for(int n=line-1;n>=scan.Lo&&!scan.Blank(n);n--){
                int sig=scan.Sig(n),indent=IndentOf(sig);
                if(KindOf(sig)==ListItem&&indent<limit){item=n;break;}
                if(KindOf(sig)==Heading&&scan.Code(n)==0)break;
                limit=Math.Min(limit,indent);if(limit==0)break;
            }
        }
        if(item>0){
            int depth=IndentOf(scan.Sig(item));start=item;end=item;
            while(end<scan.Hi&&!scan.Blank(end+1)&&IndentOf(scan.Sig(end+1))>depth)end++;
            if(end<line)end=line;
            return;
        }
        while(start>scan.Lo&&scan.ParagraphLine(start-1))start--;
        // An "===" line is the underline that ends the paragraph above it.
        if(kind==EqualsLine&&start<line)return;
        while(end<scan.Hi&&scan.ParagraphLine(end+1))end++;
        if(end<scan.Hi&&scan.Code(end+1)==0){int next=KindOf(scan.Sig(end+1));if(next==EqualsLine||next==DashLine)end++;}
    }
    // The lines Find reads, each fetched once.
    sealed class Scan {
        readonly TextDocument document;readonly Func<int,int> codeState;
        internal readonly int Lo,Hi;
        readonly string[] texts;readonly int[] sigs,codes;
        int rawFilled;bool rawInFence;
        internal Scan(TextDocument document,int line,Func<int,int> codeState){
            this.document=document;this.codeState=codeState;
            Lo=Math.Max(1,line-Window);Hi=Math.Min(document.LineCount,line+Window);
            int count=Hi-Lo+1;texts=new string[count];sigs=new int[count];codes=new int[count];
            for(int i=0;i<count;i++){sigs[i]=-1;codes[i]=-1;}
            rawFilled=Lo-1;
        }
        internal string Text(int n){int i=n-Lo;if(texts[i]==null)texts[i]=document.GetText(document.GetLineByNumber(n));return texts[i];}
        internal int Sig(int n){int i=n-Lo;if(sigs[i]<0)sigs[i]=Kind(Text(n));return sigs[i];}
        internal bool Blank(int n){return KindOf(Sig(n))==FocusBlocks.Blank;}
        internal int Code(int n){
            int i=n-Lo;if(codes[i]>=0)return codes[i];
            if(codeState!=null){codes[i]=codeState(n);return codes[i];}
            // Raw count from the top of the window: the window is assumed to start outside a fence.
            while(rawFilled<n){
                rawFilled++;int at=rawFilled-Lo;
                if(KindOf(Sig(rawFilled))==Fence){codes[at]=2;rawInFence=!rawInFence;}
                else codes[at]=rawInFence?1:0;
            }
            return codes[i];
        }
        internal bool ParagraphLine(int n){return KindOf(Sig(n))==Plain&&Code(n)==0;}
        internal bool TableLine(int n){int sig=Sig(n);return KindOf(sig)==Plain&&HasPipe(sig)&&Code(n)==0;}
        internal bool QuoteOrLazy(int n){int kind=KindOf(Sig(n));return (kind==Quote||kind==Plain||kind==EqualsLine)&&Code(n)==0;}
    }
}
}
