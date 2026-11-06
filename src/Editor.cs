using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms=System.Windows.Forms;

namespace MDNote {
public partial class MainWindow : Window {
    internal enum SaveChoice {Save,Discard,Cancel}
    readonly DockPanel root=new DockPanel();
    readonly Grid area=new Grid();
    SourceEditor source=new SourceEditor();
    /* A status line that does not fit ends in "…" instead of being clipped (bughunt-ui 27). */
    readonly TextBlock status=new TextBlock {TextTrimming=TextTrimming.CharacterEllipsis};
    readonly Button editButton=new Button();
    readonly ContextMenu saveMenu=new ContextMenu();
    readonly Button boldButton=new Button(),italicButton=new Button(),strikeButton=new Button();
    readonly WrapPanel formatBar=new WrapPanel();
    readonly Border ribbonHost=new Border();
    readonly StackPanel formatting=new StackPanel {Orientation=Orientation.Horizontal};
    readonly CheckBox preview=new CheckBox {Content="원문 편집",IsChecked=false,Focusable=false,VerticalAlignment=VerticalAlignment.Center};
    /* D1: the format ribbon scrolls sideways at narrow widths (the same overflow the top command row
       uses). While it does, the right-docked 원문 toggle used to float over the row's clipped last
       button, so the button read as covered by it. The toggle now leads the row whenever the row
       overflows and stays at the right edge when everything fits; the overflow itself is unchanged. */
    ScrollViewer ribbonScroll;Border ribbonPreviewHost;int ribbonDockKey=int.MinValue;
    readonly ComboBox sizes=new ComboBox {IsEditable=true,IsTextSearchEnabled=false,StaysOpenOnEdit=true,Width=76,Margin=new Thickness(2,0,0,0),VerticalAlignment=VerticalAlignment.Center};/* b52: the right-aligned size text leaves its spare width on the left, so the box starts 2 px from its neighbour instead of 4 */
    readonly TextBox query=new TextBox {Width=220,Margin=new Thickness(8,4,6,4)};
    readonly StackPanel search=new StackPanel {Orientation=Orientation.Horizontal,Visibility=Visibility.Collapsed};
    string path="",saved="",sizeText="";
    bool editing,loading,dark,wordWrap=true,updatingTools;
    /* 이전 세션 계속하기 (user, 2026-11-03): the File-menu option and its check, and the pictures a
       paste wrote for each tab, so the first save of an untitled document can move them next to the
       file it is being saved as (Recovery.cs / PasteSupport.cs own the two behaviours). The table's
       keys are weak: a closed tab is collected with its list. */
    bool continueSession;MenuItem continueSessionToggle;
    readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentTab,List<string>> pastedImages=new System.Runtime.CompilerServices.ConditionalWeakTable<DocumentTab,List<string>>();
    double fontSize=16;
    string family=ViewPreferences.DefaultFamily(null);
    Color? customColor;
    bool PlainText {get{return textMode??String.Equals(System.IO.Path.GetExtension(path),".txt",StringComparison.OrdinalIgnoreCase);}}
    // The AvalonEdit source is the only editing surface; CodeMode stays as the probe-visible
    // name for it (read-only otherwise).
    bool CodeMode {get{return true;}}

    public MainWindow(){
        Title="새 문서 — MDNote";Width=1080;Height=800;MinWidth=360;MinHeight=420;
        FontFamily=new FontFamily("Segoe UI, Malgun Gothic");FontSize=12;WindowStartupLocation=WindowStartupLocation.CenterScreen;if(Program.IsSelfTest){ShowInTaskbar=false;ShowActivated=false;WindowStartupLocation=WindowStartupLocation.Manual;Left=-20000;Top=-20000;}
        LoadPreferences();RestoreWindowPlacement();SetupWindowPlacement();Resources.MergedDictionaries.Add(Ui.Styles());Theme();
        // A Windows image without working imaging codecs opens without the window icon rather than not at all.
        try{using(var stream=typeof(MainWindow).Assembly.GetManifestResourceStream("MDNote.ico")){if(stream!=null){var decoder=new IconBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);BitmapFrame largest=null;foreach(var frame in decoder.Frames)if(largest==null||frame.PixelWidth>largest.PixelWidth)largest=frame;Icon=largest;}}}catch(Exception){}
        /* An activation is also when this window's icon stroke is re-applied: another window's theme
           pass leaves the icons here alone now (Ui.SetIconTheme), and icons this window built after
           that pass must still come back at this window's own weight. */
        Content=root;Activated+=delegate{DocumentActivation.ActiveWindow=this;UpdateUndoRedoTools();Ui.ReapplyIconTheme(dark,this);CheckExternalChanges();};Closed+=delegate{if(DocumentActivation.ActiveWindow==this)DocumentActivation.ActiveWindow=null;};
        /* The undo/redo buttons follow the document's history, never keyboard focus: a menu, the
           find field or another window taking focus used to leave them looking disabled until the
           next edit, so every focus change re-reads CanUndo/CanRedo (user, 2026-10-30). */
        IsKeyboardFocusWithinChanged+=delegate{if(!loading)UpdateUndoRedoTools();};
        var top=new DockPanel {LastChildFill=true,Margin=new Thickness(7,0,7,0)};
        var topGrid=new Grid();Ui.SlimHorizontal(top,topGrid);/* #12: narrow windows scroll the command row instead of clipping it (thin overlay bar) */var topHost=new Border {Child=topGrid,MinHeight=36,BorderThickness=new Thickness(0,0,0,1)};topHost.SetResourceReference(Border.BackgroundProperty,"Ribbon");topHost.SetResourceReference(Border.BorderBrushProperty,"Line");DockPanel.SetDock(topHost,Dock.Top);root.Children.Add(topHost);
        var commands=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(commands,Dock.Left);top.Children.Add(commands);
        commands.Children.Add(CommandIcon("new","새 문서 만들기 (Ctrl+N)",NewFile));commands.Children.Add(CommandIcon("open","파일 열기 (Ctrl+O)",delegate{RunAfterCompositionOrNow(OpenDialog);}));
        var saveIcon=saveMain=CommandIcon("save","저장 (Ctrl+S)",delegate{SaveFromUi(false);});
        var saveArrow=saveChevron=CommandIcon("down","다른 이름으로 저장 (Ctrl+Shift+S)",delegate{});
        var saveSplit=saveHover=Ui.SplitButton(saveIcon,saveArrow,22);saveIcon.Width=15+4;saveSplit.Margin=new Thickness(1,0,7,0);
        var saveAs=new MenuItem {Header="다른 이름으로 저장…",InputGestureText="Ctrl+Shift+S"};saveAs.Click+=delegate{SaveFromUi(true);};saveMenu.Items.Add(saveAs);
        saveArrow.Click+=delegate{saveMenu.Background=Ui.Brush("#F0F0F0");saveMenu.Foreground=Ui.Brush("#202020");saveMenu.IsOpen=true;};commands.Children.Add(saveSplit);
        /* order (user, 2026-10-24): new · open · save | find · replace | edit · TXT · dark | menus; each divider centred by BalanceDivider */var divider=Ui.Divider();commands.Children.Add(divider);quickFind=CommandIcon("find","찾기 열기/닫기 (Ctrl+F)",delegate{ToggleSearch(false);});quickReplace=CommandIcon("replace","바꾸기 열기/닫기 (Ctrl+H)",delegate{ToggleSearch(true);});commands.Children.Add(quickFind);commands.Children.Add(quickReplace);var toggleDivider=Ui.Divider();commands.Children.Add(toggleDivider);
        SetupCommandIcon(editButton,"edit","수정 시작 / 종료 · Ctrl+E",Toggle);commands.Children.Add(editButton);
        Ui.ToggleChrome(editButton,false);editButton.MinWidth=78;editButton.Padding=new Thickness(6,0,10,0);quickDark=CommandIcon("dark","다크 모드 켜기/끄기",delegate{SetDark(!dark);});commands.Children.Add(quickDark);Ui.ToggleChrome(quickDark,true);quickDark.Content=Ui.ToggleIcon("dark");var menuDivider=Ui.Divider();commands.Children.Add(menuDivider);
        var menu=topMenu=new Menu {Background=Brushes.Transparent,VerticalAlignment=VerticalAlignment.Center,Height=28,Padding=new Thickness(0),FontSize=13};menu.SetResourceReference(Menu.ForegroundProperty,"Ink");DockPanel.SetDock(menu,Dock.Left);var menuStyle=new Style(typeof(MenuItem),(Style)Resources[typeof(MenuItem)]);menuStyle.Setters.Add(new Setter(Control.ForegroundProperty,new System.Windows.DynamicResourceExtension("Ink")));var highlighted=new Trigger {Property=MenuItem.IsHighlightedProperty,Value=true};highlighted.Setters.Add(new Setter(Control.ForegroundProperty,new System.Windows.DynamicResourceExtension("Ink")));menuStyle.Triggers.Add(highlighted);menuStyle.Setters.Add(new Setter(HeightProperty,28.0));menuStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,0,8,0)));menuStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));menuStyle.Setters.Add(new Setter(Control.FontWeightProperty,FontWeights.Normal));menu.ItemContainerStyle=menuStyle;top.Children.Add(menu);
        // A left press on the menu bar is the earliest point where the menu can steal focus: the
        // source TextArea still owns the keyboard here, so the composing syllable can be committed
        // before the popup opens (SetLiveMarkdown keeps its deferred commit as the second safety).
        menu.PreviewMouseDown+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs args){
            if(ImeTrace.Enabled)ImeTrace.Write("menu PreviewMouseDown source="+(args.OriginalSource==null?"-":args.OriginalSource.GetType().Name)+" button="+args.ChangedButton+" focus="+FocusName());
            if(args.ChangedButton==System.Windows.Input.MouseButton.Left)CommitCompositionForMenu("menu mouse");
        };
        // Reaching the menu bar happens one hover before the click; ending a basic-view TSF
        // composition there keeps WPF from completing it during the focus change that opens the
        // popup (that completion made the first item click land on the document). Harmless when
        // nothing is composing: the handler returns after two cheap IMM checks.
        menu.PreviewMouseMove+=delegate(object sender,System.Windows.Input.MouseEventArgs args){CommitCompositionForMenu("menu hover");};
        var file=AddMenu(menu,"파일(_F)");var continueItem=continueSessionToggle=new MenuItem {Header="이전 세션 계속하기",IsCheckable=true,IsChecked=continueSession};continueItem.Click+=delegate{SetContinueSession(continueItem.IsChecked);};file.Items.Add(continueItem);file.Items.Add(new Separator());Item(file,"새로 만들기","Ctrl+N",NewFile);Item(file,"열기…","Ctrl+O",delegate{RunAfterCompositionOrNow(OpenDialog);});Item(file,T("저장"),"Ctrl+S",delegate{SaveFromUi(false);});Item(file,"다른 이름으로 저장…","Ctrl+Shift+S",delegate{SaveFromUi(true);});file.Items.Add(new Separator());Item(file,"인쇄…","Ctrl+P",Print);file.Items.Add(new Separator());Item(file,"끝내기","Alt+F4",Close);
        var edit=AddMenu(menu,"편집(_E)");Item(edit,"수정 시작/종료","Ctrl+E",Toggle);edit.Items.Add(new Separator());Item(edit,"실행 취소","Ctrl+Z",Undo);Item(edit,"다시 실행","Ctrl+Y",Redo);edit.Items.Add(new Separator());Item(edit,"모두 선택","Ctrl+A",delegate{source.SelectAll();});Item(edit,"복사","Ctrl+C",delegate{source.Copy();});Item(edit,"잘라내기","Ctrl+X",delegate{if(editing)source.Cut();});Item(edit,"붙여넣기","Ctrl+V",delegate{PasteClipboard(false);});Item(edit,"서식 없이 붙여넣기","Ctrl+Shift+V",delegate{PasteClipboard(true);});Item(edit,"이미지 삽입…","Ctrl+Shift+I",delegate{RunAfterCompositionOrNow(InsertImage);});edit.Items.Add(new Separator());Item(edit,"찾기…","Ctrl+F",delegate{ToggleSearch(false);});Item(edit,"바꾸기…","Ctrl+H",delegate{ToggleSearch(true);});
        var view=AddMenu(menu,"보기(_V)");var wrap=wrapToggle=new MenuItem {Header="자동 줄바꿈",IsCheckable=true,IsChecked=true};wrap.Click+=delegate{SetWrap(wrap.IsChecked);};view.Items.Add(wrap);view.Items.Add(new Separator());BuildLiveViewItem(view);view.Items.Add(new Separator());Item(view,"글자 확대","Ctrl++",delegate{Zoom(2);});Item(view,"글자 축소","Ctrl+-",delegate{Zoom(-2);});Item(view,"기본 글자 크기","Ctrl+0",delegate{SetBaseFont(family,16);});BuildLineMenu(view);view.Items.Add(new Separator());Item(view,"글꼴 및 크기…","",delegate{RunAfterCompositionOrNow(ChooseFont);});Item(view,"기본 글자색…","",delegate{RunAfterCompositionOrNow(ChooseColor);});Item(view,"기본 글자색 초기화","",delegate{customColor=null;RenderPreservingView();});view.Items.Add(new Separator());darkToggle=new MenuItem {Header="다크 모드",IsCheckable=true,IsChecked=dark};darkToggle.Click+=delegate{SetDark(darkToggle.IsChecked);};view.Items.Add(darkToggle);BuildFocusMenu(view);
        var help=AddMenu(menu,"도움말(_H)");Item(help,"MD파일 작성방법","",OpenUsage);Item(help,"MDNote 사용법","",OpenAppUsage);help.Items.Add(new Separator());Item(help,"업데이트 확인","",CheckForUpdates);autoUpdateToggle=new MenuItem {Header="업데이트 자동 확인",IsCheckable=true,IsChecked=autoUpdateCheck};autoUpdateToggle.Click+=delegate{SetAutoUpdateCheck(autoUpdateToggle.IsChecked);};help.Items.Add(autoUpdateToggle);help.Items.Add(new Separator());Item(help,"MDNote 정보","",ShowAbout);
        var searchCommands=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left};top.Children.Add(searchCommands);
        Ui.BalanceDivider(divider,saveArrow,quickFind,top);Ui.BalanceDivider(toggleDivider,quickReplace,editButton,top);Ui.BalanceDivider(menuDivider,quickDark,(FrameworkElement)menu.Items[0],top);Ui.NormalizeBarIcons(commands);BuildRibbon();BuildTabs(view);BuildRecent(file);BuildOpenEncodingMenu(file);BuildOutline(top);if(outlineButton!=null){outlineButton.Content=null;outlineButton.Content=Ui.GridIcon("menu");Ui.TopSquare(outlineButton);outlineButton.Margin=new Thickness(1,0,1,0);}
        var statusRow=new DockPanel {LastChildFill=true};textBadge=new Border {Child=new TextBlock {Text="TXT",FontSize=10.5,FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center},Padding=new Thickness(6,0,6,0),CornerRadius=new CornerRadius(3),Margin=new Thickness(0,0,10,0),Visibility=Visibility.Collapsed};textBadge.SetResourceReference(Border.BackgroundProperty,"Accent");((TextBlock)textBadge.Child).SetResourceReference(TextBlock.ForegroundProperty,"Paper");DockPanel.SetDock(textBadge,Dock.Left);statusRow.Children.Add(textBadge);statusRow.Children.Add(status);var bar=statusBar=new Border {Child=statusRow,Padding=new Thickness(14,4,14,4),BorderThickness=new Thickness(0,1,0,0)};bar.SetResourceReference(Border.BorderBrushProperty,"Line");DockPanel.SetDock(bar,Dock.Bottom);root.Children.Add(bar);
        BuildSearchBar();BuildTableTools();CompactTableToolbar();
        area.SetResourceReference(Grid.BackgroundProperty,"Canvas");root.Children.Add(new AdornerDecorator {Child=area});
        SetupSurfaces();surfacesReady=true;SetupInteractionLayout();SetupRecovery(file);SetupAutoUpdateCheck();
        CommandManager.AddPreviewExecutedHandler(this,delegate(object sender,ExecutedRoutedEventArgs args){if(source.IsKeyboardFocusWithin&&(args.Command==ApplicationCommands.Undo||args.Command==ApplicationCommands.Redo)){RestoreEditorHistory(args.Command==ApplicationCommands.Redo);args.Handled=true;}else if(source!=null&&(Object.ReferenceEquals(args.Source,source.TextArea)||source.IsKeyboardFocusWithin)&&(args.Command==EditingCommands.MoveDownByPage||args.Command==EditingCommands.MoveUpByPage)){/* the page handler reveals the caret from the height tree's estimates; verify once after the frame's layout (QueueCaretRowVisible). args.Source is the command's target, so a probe or a menu that executes it without keyboard focus is covered too. */QueueCaretRowVisible();}});CommandManager.AddPreviewCanExecuteHandler(this,delegate(object sender,CanExecuteRoutedEventArgs args){if(source.IsKeyboardFocusWithin&&(args.Command==ApplicationCommands.Undo||args.Command==ApplicationCommands.Redo)){args.CanExecute=editing&&(args.Command==ApplicationCommands.Undo?History.CanUndo:History.CanRedo);args.Handled=true;}});PreviewMouseDown+=PreviewCommitComposition;PreviewKeyDown+=Keys;PreviewKeyDown+=MenuKeyCommitComposition;PreviewMouseWheel+=delegate(object s,MouseWheelEventArgs e){if((Keyboard.Modifiers&ModifierKeys.Control)!=0){Zoom(e.Delta>0?1:-1);e.Handled=true;}};
        SetupCompositionDeactivation();StateChanged+=delegate{if(WindowState!=WindowState.Minimized)lastRestorableState=WindowState;else CommitCompositionBeforeDeactivation();};Deactivated+=delegate{CommitCompositionBeforeDeactivation();};SizeChanged+=delegate{ApplyPageInsets();};Closing+=OnWindowClosing;SetupSaveDialogActivation();SetupMinimizedClosePriority();AllowDrop=true;
        /* A drag source that hangs, closes or hands over a malformed HDROP fails these OLE reads (COMException and others): ignore that drag. */PreviewDragOver+=delegate(object s,DragEventArgs e){bool fileDrop;try{fileDrop=e.Data.GetDataPresent(DataFormats.FileDrop);}catch(Exception){fileDrop=false;}if(fileDrop){e.Effects=DragDropEffects.Copy;e.Handled=true;}};
        Drop+=delegate(object s,DragEventArgs e){
            if(source!=null)source.NoteInactiveImeFocus();/* see SourcePreviewDrop */
            string[] files;try{if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;files=e.Data.GetData(DataFormats.FileDrop) as string[];}catch(Exception){return;}
            if(files==null||files.Length==0)return;
            /* The source consumes image drops on the Markdown body itself (SourcePreviewDrop); a drop
               that reaches the window is the document's insert at the caret (AcceptImageDrop), except
               for the TXT notice. A mixed drop (an image plus a .md/.txt) keeps the open-as-document
               answer below, images included, so the documents still open. */
            e.Handled=true;
            try{if(AcceptImageDrop(files,null))return;}catch(Exception ex){System.Diagnostics.Trace.TraceError("MDNote drop: {0}: {1}",ex.GetType().Name,ex.Message);}
            /* Defensive: an all-image drop must never reach LoadFile. A PNG is not text, so the
               text decoder would answer a dropped image with its decode error box. */
            if(MarkdownCommands.AllImageFiles(files))return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{foreach(string dropped in files){/* A mixed drop opens only the documents: decoding a PNG as text shows an error box or a broken tab that could overwrite the picture. */if(MarkdownCommands.IsImageFile(dropped))continue;LoadFile(dropped);}/* the dropped document may be a new tab with its own editor, focused while this window is still in the background */if(source!=null)source.NoteInactiveImeFocus();}));
        };
        SetupSourceOutline();Theme();SetMode(true);SetupFunctionKeys();BuildTextModeMenu(edit);BuildTextQuickButton();BuildLanguages(menu,help);Ui.ToolbarText(topHost);if(ImeTrace.Enabled){AttachImeMenuTrace(menu,"menu");PreviewTextInput+=delegate(object traceSender,System.Windows.Input.TextCompositionEventArgs traceArgs){ImeTrace.Write("window TextInput text="+SourceEditor.TraceText(traceArgs.Text)+" handled="+traceArgs.Handled+" focus="+FocusName());};}
        // A menu takes keyboard focus before the IME's result arrives; WPF then raises TextInput on
        // the menu item and the TextArea never sees the syllable. Adopt that counted result at the
        // caret the composition belonged to (no effect on ordinary key/menu text).
        PreviewTextInput+=delegate(object lateSender,System.Windows.Input.TextCompositionEventArgs lateArgs){if(!lateArgs.Handled&&source!=null&&source.Visibility==Visibility.Visible&&source.AcceptLateImeCommit(lateArgs.Text))lateArgs.Handled=true;};
    }
    /* b59: the ScrollViewer's bars are laid out on its outermost rows, and the area fills exactly the
       space between the ribbon (a 1 px Line under it) and the status bar (a 1 px Line on top). At the
       ends of their tracks the thumbs therefore sat on those two lines - the vertical thumb's rounded
       top cap under the ribbon at the top of a document, the horizontal bar's bottom edge on the
       status line - and read as cut off (user screenshots, 2026-11-01). The editor's own border keeps
       this strip of the page, so both tracks and thumbs stay inside the editor area: the vertical
       track starts this far below the ribbon and ends this far above the status bar. */
    internal const double EditorScrollInset=4;
    void SetupSurfaces(){
        // One editing surface: the AvalonEdit source, with the live preview drawn over it.
        TextOptions.SetTextFormattingMode(source,TextFormattingMode.Display);TextOptions.SetTextRenderingMode(source,TextRenderingMode.Auto);RenderOptions.SetClearTypeHint(source,ClearTypeHint.Enabled);
        source.AcceptsReturn=true;source.AcceptsTab=true;source.FontFamily=new FontFamily("Consolas, Malgun Gothic");source.FontSize=15;source.BorderThickness=new Thickness(0,EditorScrollInset,0,EditorScrollInset);source.Padding=new Thickness(8,0,16,0);source.Loaded+=delegate(object sender,RoutedEventArgs args){ApplySourceInset((SourceEditor)sender);};source.TextArea.PreviewKeyDown+=SourceStructuralKeys;DataObject.AddCopyingHandler(source.TextArea,SourceCopying);DataObject.AddSettingDataHandler(source.TextArea,SourceSettingData);source.TextArea.PreviewDrop+=SourcePreviewDrop;source.TextArea.TextEntering+=SourceTextEntering;source.TextArea.TextEntering+=SourceNewlineEntering;source.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler(delegate(object sender,ScrollChangedEventArgs args){/* The inset margin is part of TextView's viewport, so the ScrollViewer clamps the offset and raises this event for its own clamp (VerticalChange == -ViewportHeightChange); recomputing the inset there ping-pongs forever. A real resize near the document end can carry that same signature, so the echo still skips the inset here but posts one re-check after the frame's layout (QueueSourceInsetRecheck); FollowDocumentEnd stays skipped for every echo. */var editor=(SourceEditor)sender;/* The inset is measured from TextView's own extent and viewport, and those only mean something once this frame's measure and arrange are valid; read earlier (during a layout, a document swap or a mode switch) they describe the previous frame, so the margin below is computed from stale numbers. That case gets the same single re-check the echo below gets (QueueSourceInsetRecheck), posted to run after the frame's layout. */if(!SourceInsetGeometryReady(editor)){insetRecheckSpent=false;QueueSourceInsetRecheck(editor);return;}if(args.VerticalChange!=0&&Math.Abs(args.VerticalChange+args.ViewportHeightChange)<=0.5&&Math.Abs(args.ViewportHeightChange)<=24.5){QueueSourceInsetRecheck(editor);return;}insetRecheckSpent=false;ApplySourceInset(editor);FollowDocumentEnd(editor,args);}));source.VerticalScrollBarVisibility=ScrollBarVisibility.Auto;
        area.Children.Add(source);/* a surface that comes back from Collapsed re-applies the current wrap setting (Editor.cs:SurfaceShown) */source.IsVisibleChanged+=delegate(object shownSender,System.Windows.DependencyPropertyChangedEventArgs shownArgs){if(shownArgs.NewValue is bool&&(bool)shownArgs.NewValue&&Object.ReferenceEquals(shownSender,source))SurfaceShown();};source.PreviewMouseWheel+=FastScroll;
        var sourceSurface=source;source.ContentDelta+=delegate(object sender,ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs change){/* a line break moves the caret's line through the height tree; verify the caret row once after the layout. Commands apply their edits with loading=true, so this check stays outside that guard (History below does not). */if(LineStructureChanged(change,sourceSurface.Document.TextLength))QueueCaretRowVisible();if(!loading&&CodeMode&&Object.ReferenceEquals(sourceSurface,source)){History.RecordDelta(change.Offset,change.RemovedText.Text,change.InsertedText.Text);sourceSnapshotDirty=true;if(!sourceOutlineDirty&&!PlainText)sourceOutlineDirty=OutlineMayChange(source.Document,change);}};source.TextChanged+=delegate{if(!loading&&CodeMode&&Object.ReferenceEquals(sourceSurface,source)){UpdateTitle();if(sourceOutlineDirty)RefreshOutline();}};source.SelectionChanged+=delegate{if(!loading){RefreshSearch();RefreshCaretStatus();if(LiveFormat)UpdateTools();}};EventHandler sourceLayout=delegate{if(Object.ReferenceEquals(sourceSurface,source)){/* a new tab or document must recompute the insets even when its gutter bucket matches the old one */if(!Object.ReferenceEquals(insetSurface,source)||!Object.ReferenceEquals(insetDocument,source.Document)){insetSurface=source;insetDocument=source.Document;insetBucket=-1;insetRecheckPending=false;insetRecheckSpent=false;}textGutter.InvalidateIfChanged();/* insets depend only on the gutter width class and visibility; recompute on change, not every layout pass */double dpiKey=VisualTreeHelper.GetDpi(this).PixelsPerDip;int bucket=(source.Document.LineCount>99999?2:0)+(textGutter.Visibility==Visibility.Visible?1:0);if(bucket!=insetBucket||dpiKey!=insetDpi){insetBucket=bucket;insetDpi=dpiKey;ApplyPageInsets();}}};source.LayoutUpdated+=sourceLayout;if(activeDocument!=null)activeDocument.SourceLayout=sourceLayout;source.DocumentChanged+=delegate{if(Object.ReferenceEquals(sourceSurface,source)){pendingViewSettings=null;if(!Object.ReferenceEquals(insetDocument,source.Document)){insetDocument=source.Document;insetBucket=-1;}}};
        DataObject.AddPastingHandler(source,delegate(object sender,DataObjectPastingEventArgs e){bool present=false;ClipboardRetry.Try(delegate{present=e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText);});if(present)e.FormatToApply=DataFormats.UnicodeText;else e.CancelCommand();});
    }
    // N8: a live IME syllable must commit at the pre-click caret. The window's tunnelling handler
    // runs before every child preview handler (live preview press handling, long line) and before
    // AvalonEdit moves the caret from its bubbling MouseDown, so the syllable cannot follow the
    // clicked place. The TextArea handler stays as a fallback; the click stays unhandled.
    internal bool mouseDownCompositionCommitted;internal int mouseDownCommitCaret=-1;
    void PreviewCommitComposition(object sender,MouseButtonEventArgs e){
        if(ImeTrace.Enabled)ImeTrace.Write("window PreviewMouseDown source="+(e.OriginalSource==null?"null":e.OriginalSource.GetType().Name)+" button="+e.ChangedButton+" handled="+e.Handled+" at="+e.GetPosition(this)+" focus="+FocusName()+" capture="+MenuCaptureName());
        if(e.ChangedButton!=MouseButton.Left||source.Visibility!=Visibility.Visible)return;
        bool busy=source.ImeBusy,active=source.ImeCompositionActive();
        if(ImeTrace.Enabled&&(busy||active))ImeTrace.Write("window preview commit busy="+busy+" active="+active+" caret="+source.CaretIndex+" focus="+FocusName());
        if(!busy&&!active)return;
        mouseDownCompositionCommitted=true;mouseDownCommitCaret=source.CaretIndex;
        source.BeginCompositionCommit();
    }
    // Enter on a plain line (SourceTextEntering above found no list/quote continuation) falls
    // through to AvalonEdit's TextArea.ReplaceSelectionWithNewLine. Its
    // TextUtilities.GetNewLineFromDocument returns Environment.NewLine when the document holds no
    // line delimiter at all (a one-line document, which is what Ctrl+A and retyping leaves), so an
    // LF document got a CRLF. Saving now normalizes only the bytes on disk and leaves the buffer,
    // its history and the rich source mirror as they are (DocumentEncoding.SaveToPath), so that
    // CRLF would stay in the buffer and in every undo/redo entry recorded for it as a newline that
    // differs from documentNewline. Insert the document's own newline (documentNewline) instead.
    // This runs after SourceTextEntering (subscribed just before it) so structural Enter is never
    // stolen.
    void SourceNewlineEntering(object sender,TextCompositionEventArgs e){
        if(e.Handled||source==null||!Object.ReferenceEquals(sender,source.TextArea))return;
        string value=e.Text;if(value!="\r"&&value!="\n"&&value!="\r\n")return;
        if(String.IsNullOrEmpty(documentNewline)||String.Equals(System.Environment.NewLine,documentNewline,StringComparison.Ordinal))return;
        var document=source.Document;if(document==null)return;
        var line=document.GetLineByOffset(Math.Min(Math.Max(0,source.CaretIndex),document.TextLength));
        if(line.DelimiterLength>0||line.PreviousLine!=null)return;
        e.Handled=true;
        using(document.RunUpdate()){
            source.TextArea.Selection.ReplaceSelectionWithText(documentNewline);
            var strategy=source.TextArea.IndentationStrategy;
            if(strategy!=null){
                var target=document.GetLineByOffset(Math.Min(source.CaretIndex,document.TextLength));
                var deletable=new List<ICSharpCode.AvalonEdit.Document.ISegment>(source.TextArea.ReadOnlySectionProvider.GetDeletableSegments(target));
                if(deletable.Count==1&&deletable[0].Offset==target.Offset&&deletable[0].Length==target.Length)strategy.IndentLine(document,target);
            }
        }
        source.EndTypingRun();
        MarkdownLivePreview.Session.SafeBringCaretToView(source.TextArea);
    }
    void BuildRibbon(){
        formatBar.Orientation=Orientation.Horizontal;var scrollHost=new Grid();ribbonScroll=Ui.SlimHorizontal(formatBar,scrollHost);var scrollHostWatch=scrollHost;scrollHostWatch.LayoutUpdated+=delegate{ApplyRibbonToggleDock();};var ribbonLayout=new DockPanel {LastChildFill=true};ribbonHost.Child=ribbonLayout;var previewHost=ribbonPreviewHost=new Border {Child=preview,Padding=new Thickness(0,4,14,4)};DockPanel.SetDock(previewHost,Dock.Right);ribbonLayout.Children.Add(previewHost);ribbonLayout.Children.Add(scrollHost);ribbonHost.BorderThickness=new Thickness(0,0,0,1);ribbonHost.SetResourceReference(Border.BackgroundProperty,"Ribbon");ribbonHost.SetResourceReference(Border.BorderBrushProperty,"Line");DockPanel.SetDock(ribbonHost,Dock.Top);root.Children.Add(ribbonHost);
        StackPanel group;Ui.Group(formatBar,"편집",Ui.RibbonDividerGap,out group);var undo=undoButton=ActionButton(group,"",Undo);undo.Content=Ui.GridIcon("undo");undo.ToolTip="실행 취소 (Ctrl+Z)";var redo=redoButton=ActionButton(group,"",Redo);redo.Content=Ui.GridIcon("redo");redo.ToolTip="다시 실행 (Ctrl+Y)";
        /* b39 order (Docs-like): style, inline, list, block, insert; the groups are created in bar order first, then filled */
        StackPanel styleGroup,textGroup,listGroup,blockGroup,insertGroup;var paragraphGroup=Ui.Group(formatBar,"문단",Ui.RibbonDividerGap,out styleGroup);paragraphGroup.Tag="CompactStart";Ui.Group(formatBar,"글자",Ui.RibbonDividerGap,out textGroup);Ui.Group(formatBar,"목록",Ui.RibbonDividerGap,out listGroup);Ui.Group(formatBar,"블록",Ui.RibbonDividerGap,out blockGroup);Ui.Group(formatBar,"삽입",Ui.RibbonDividerGap,out insertGroup);
        BuildParagraphControls(styleGroup,listGroup,blockGroup);
        SetupButton(boldButton,"B",delegate{ToggleFormat("bold");});boldButton.Content=Ui.GridIcon("bold");boldButton.ToolTip="굵게 켜기/끄기 (Ctrl+B)";textGroup.Children.Add(boldButton);
        SetupButton(italicButton,"A",delegate{ToggleFormat("italic");});italicButton.Content=Ui.GridIcon("italic");italicButton.ToolTip="기울임 켜기/끄기 (Ctrl+I)";textGroup.Children.Add(italicButton);
        SetupButton(strikeButton,"S",delegate{ToggleFormat("strike");});strikeButton.Content=Ui.GridIcon("strike");strikeButton.ToolTip=T("취소선 켜기/끄기");textGroup.Children.Add(strikeButton);
        SetupButton(codeButton,"〈 〉",delegate{ToggleFormat("code");});codeButton.Content=Ui.GridIcon("highlight");codeButton.ToolTip=T("인라인 코드 켜기/끄기");textGroup.Children.Add(codeButton);
        BuildColorControl(textGroup);/* #12: tighten the A-to-chevron gap so it matches the other three dropdowns */
        InputMethod.SetIsInputMethodEnabled(sizes,false);sizes.Items.Add("크기");foreach(int n in new int[]{12,14,16,18,20,24,28,32,40,48})sizes.Items.Add(n+" px");sizes.SelectedIndex=0;sizeText=sizes.Text;sizes.SelectionChanged+=delegate{if(updatingTools||committingSize||sizes.SelectedIndex<=0||!LiveFormat)return;string value=(string)sizes.SelectedItem;if(sizes.IsDropDownOpen){sizeChoice=value;return;}if(sizes.IsKeyboardFocusWithin)return;ApplySizeChoice(value);};sizes.PreviewKeyDown+=FontSizeKeyDown;sizes.AddHandler(UIElement.PreviewMouseDownEvent,new MouseButtonEventHandler(FontSizeItemMouseDown),true);sizes.DropDownOpened+=delegate{sizeChoice=null;cancelSizeChoice=false;};sizes.LostKeyboardFocus+=delegate{if(!committingSize&&!sizes.IsDropDownOpen&&sizeChoice==null&&(sizes.Text??"")!=sizeText)CommitFontSize(false);};System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.IsDropDownOpenProperty,typeof(ComboBox)).AddValueChanged(sizes,FontSizeDropStateChanged);Closed+=delegate{System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ComboBox.IsDropDownOpenProperty,typeof(ComboBox)).RemoveValueChanged(sizes,FontSizeDropStateChanged);};sizes.DropDownClosed+=delegate{RefreshSizeBox();};sizes.GotKeyboardFocus+=delegate(object sender,KeyboardFocusChangedEventArgs args){var box=args.OriginalSource as TextBox;if(box!=null)Dispatcher.BeginInvoke(new Action(delegate{box.SelectAll();}));};sizes.ToolTip="선택한 글자 크기 설정";styleGroup.Children.Add(sizes);
        sizes.Loaded+=delegate{RefreshSizeBox();};sizes.IsEnabledChanged+=delegate{RefreshSizeBox();};sizes.AddHandler(TextBox.TextChangedEvent,new TextChangedEventHandler(delegate{FitSizeCaption();}),true);sizes.SizeChanged+=delegate{FitSizeCaption();};
        foreach(UIElement child in styleGroup.Children)formatControls.Add(child);foreach(UIElement child in textGroup.Children)formatControls.Add(child);
        SetupButton(linkButton,"링크",delegate{RunAfterCompositionOrNow(InsertLink);});linkButton.Content=Ui.GridIcon("link");linkButton.ToolTip="링크(URL) 삽입";System.Windows.Automation.AutomationProperties.SetName(linkButton,T("링크(URL) 삽입"));insertGroup.Children.Add(linkButton);insertGroup.Children.Add(new Border {Width=3});foreach(var compact in new Button[]{quoteButton,linkButton,codeButton}){compact.Padding=new Thickness(5,1,5,1);compact.Margin=new Thickness(1,0,1,0);}linkButton.Width=Ui.SquareSize;codeButton.Width=Ui.SquareSize;linkButton.Padding=new Thickness(4,0,4,0);codeButton.Padding=new Thickness(4,0,4,0);BuildImageControl(insertGroup);BuildTableControl(insertGroup);foreach(UIElement child in insertGroup.Children)formatControls.Add(child);
        preview.Style=(Style)Resources["FormatViewToggle"];preview.Margin=new Thickness(0);preview.Checked+=delegate{SwitchSurface();};preview.Unchecked+=delegate{SwitchSurface();};preview.ToolTip=T("원문 편집 켜기/끄기");CompactRibbon(formatBar);Ui.NormalizeBarIcons(formatBar);formatBar.Margin=new Thickness(7,0,7,0);preview.MinHeight=22;preview.Height=22;preview.FontSize=Ui.LabelSize;sizes.FontSize=Ui.LabelSize;sizes.MinHeight=28;sizes.Height=28;sizes.Width=60;sizes.Margin=new Thickness(-4,0,1,0);/* 2026-10-28 user: 3 px closer to the heading arrow *//* reads like the other dropdowns ("H2 제목 ˅"): no box at rest */sizes.BorderBrush=Brushes.Transparent;sizes.Background=Brushes.Transparent;
    }
    readonly List<UIElement> formatControls=new List<UIElement>();
    void ApplyRibbonToggleDock(){
        var scroll=ribbonScroll;if(scroll==null||ribbonPreviewHost==null)return;
        int key=(int)scroll.ScrollableWidth*4096+(int)scroll.ActualWidth;
        if(key==ribbonDockKey)return;
        ribbonDockKey=key;
        /* 2.0.1 (user, 11-02): the Source toggle stays at the right end; a narrow row folds it into » (UpdateRibbonOverflow) instead of moving it left. */
        Dock dock=Dock.Right;
        if(DockPanel.GetDock(ribbonPreviewHost)!=dock)DockPanel.SetDock(ribbonPreviewHost,dock);
    }
    void SetupButton(Button b,string name,Action action){b.Content=name;b.ToolTip=name;b.MinHeight=26;b.Focusable=false;b.Click+=delegate{action();};}
    void SetupCommandIcon(Button button,string icon,string description,Action action){SetupButton(button,"",action);button.Content=Ui.Icon(icon);Ui.TopSquare(button);button.ToolTip=description;System.Windows.Automation.AutomationProperties.SetName(button,T(description));}
    Button CommandIcon(string icon,string description,Action action){var button=new Button();SetupCommandIcon(button,icon,description,action);return button;}
    Button ActionButton(Panel panel,string name,Action action){var b=new Button();SetupButton(b,name,action);panel.Children.Add(b);return b;}
    MenuItem AddMenu(Menu menu,string name){var m=new MenuItem {Header=name,Padding=new Thickness(5,0,5,0)};menu.Items.Add(m);return m;}
    void Item(MenuItem parent,string name,string shortcut,Action action){var m=new MenuItem {Header=name,InputGestureText=shortcut};m.Click+=delegate{action();};parent.Items.Add(m);}
    static string MenuCaptureName(){var captured=Mouse.Captured;return captured==null?"null":captured.GetType().Name;}
    // MDNOTE_IME_TRACE: record who owns keyboard focus when the menu tree opens or an item fires.
    void AttachImeMenuTrace(ItemsControl parent,string label){
        foreach(object entry in parent.Items){
            var item=entry as MenuItem;if(item==null)continue;
            string path=label+" ["+((item.Header as string)??"?")+"]";
            item.SubmenuOpened+=delegate{ImeTrace.Write("menu SubmenuOpened "+path+" focus="+FocusName()+" capture="+MenuCaptureName());};
            item.SubmenuClosed+=delegate{ImeTrace.Write("menu SubmenuClosed "+path+" focus="+FocusName()+" capture="+MenuCaptureName());};
            // UIA visibility and a completed SendInput call do not prove the click reached this
            // popup. Record both halves even when handled, including the hit position/capture,
            // before attributing a missing Click to composition or popup readiness.
            MouseButtonEventHandler mouseTrace=delegate(object sender,MouseButtonEventArgs args){
                Point at=args.GetPosition(item);
                ImeTrace.Write("menu item "+args.RoutedEvent.Name+" "+path+" button="+args.ChangedButton+" handled="+args.Handled+" source="+(args.OriginalSource==null?"null":args.OriginalSource.GetType().Name)+" at="+at+" size="+item.ActualWidth+","+item.ActualHeight+" open="+item.IsSubmenuOpen+" focus="+FocusName()+" capture="+MenuCaptureName());
            };
            item.AddHandler(Mouse.PreviewMouseDownEvent,mouseTrace,true);
            item.AddHandler(Mouse.PreviewMouseUpEvent,mouseTrace,true);
            item.AddHandler(MenuItem.ClickEvent,new RoutedEventHandler(delegate{ImeTrace.Write("menu Click "+path+" focus="+FocusName()+" capture="+MenuCaptureName());}),true);
            if(item.Items.Count>0)AttachImeMenuTrace(item,path);
        }
    }
    void SetDark(bool value){if(DeferViewSettings){QueueViewSettings(delegate(PendingViewSettings pending){pending.Dark=value;});return;}SetDarkNow(value);}
    void SetDarkNow(bool value){FinishComposition();if(dark==value)return;var bookmark=CaptureView();BeginViewTransition();dark=value;loading=true;try{Theme();ClearTransitionMask();}finally{loading=false;}LayoutMode();UpdateTools();RefreshOutline();SavePreferences();RestoreView(bookmark);}
    void Theme(){
        if(darkToggle!=null)darkToggle.IsChecked=dark;if(quickDark!=null)quickDark.Tag=dark?"Active":null;
        Resources["PrimaryHover"]=Ui.Brush(dark?"#619ADE":"#1D4ED8");Resources["PrimaryPressed"]=Ui.Brush(dark?"#427FC5":"#1E40AF");
        Resources["CommandStroke"]=1.25;Resources["OpenIconFill"]=dark?Brushes.Transparent:Ui.Brush("#FFF4D4");/* b42: no fill in dark (looked smudged); the moon is filled only in dark */Resources["MoonIconFill"]=dark?Ui.Brush("#E7EBF2"):Brushes.Transparent;Ui.SetIconTheme(dark,this);Resources["ActiveFill"]=Ui.Brush(dark?"#555555":"#CACCD0");Resources["ActiveHover"]=Ui.Brush(dark?"#5E5E5E":"#C0C2C6");Resources["Pressed"]=Ui.Brush(dark?"#505050":"#C4C6CA");/* b42: dark hover/active lift above the #383838 toolbar instead of sinking below it */Resources["SearchInk"]=Ui.Brush(dark?"#E6E6E6":"#263242");Resources["SearchMuted"]=Ui.Brush(dark?"#8F8F8F":"#6B7686");Resources["ActiveInk"]=Ui.Brush(dark?"#FFFFFF":"#263B55");Resources["ButtonLine"]=Ui.Brush(dark?"#717171":"#A2ACBA");Resources["SearchLine"]=Resources["ButtonLine"];Resources["ScrollThumb"]=Ui.Brush(dark?"#7A8290":"#8F99A6");Resources["ScrollThumbHover"]=Ui.Brush(dark?"#9AA3AE":"#6B7686");Resources["TableAddMark"]=Ui.Brush(dark?"#6CCB8F":"#1E8E3E");Resources["TableRemoveMark"]=Ui.Brush(dark?"#FF7B7B":"#D93025");Resources["CheckLine"]=Ui.Brush(dark?"#8A94A6":"#6B7686");Resources["CheckOn"]=Ui.Brush(dark?"#4C8DFF":"#2563EB");Resources["CheckMark"]=Brushes.White;string[] keys={"Ink","Muted","Paper","Ribbon","Canvas","Line","Hover","Accent","AccentSoft"};string[] light={"#263242","#4B586E","#FFFFFF","#ECEEF1","#FFFFFF","#C7CED9","#DADCE0","#2563EB","#E8F0FF"};string[] night={"#FFFFFF","#BDBDBD","#272727","#383838","#272727","#515151","#474747","#90BFFF","#344454"};for(int i=0;i<keys.Length;i++)Resources[keys[i]]=Ui.Brush((dark?night:light)[i]);Resources[SystemColors.MenuBrushKey]=Ui.Brush("#F0F0F0");Resources[SystemColors.MenuTextBrushKey]=Ui.Brush("#202020");Background=(Brush)Resources["Paper"];root.Background=Background;Foreground=(Brush)Resources["Ink"];source.CaretBrush=Foreground;source.Background=Background;source.Foreground=customColor.HasValue?FrozenBrush(customColor.Value):Foreground;status.Foreground=Foreground;ApplyTitleTheme();UpdateHeadingCaption();UpdateListCaption();
    }
    public void SetText(string value){viewTransition=false;viewTransitionVersion++;ClearTransitionMask();CancelEditorRefresh();History.Clear();text=value;Render();}
    // The source's ink is created frozen: an unfrozen brush defeats glyph-run sharing and makes a
    // long line re-format on every layout pass.
    internal static SolidColorBrush FrozenBrush(Color value){var brush=new SolidColorBrush(value);brush.Freeze();return brush;}
    void Render(){RenderSurface(true);}
    /* replaceText is false only for a tab that keeps its own editor (SwitchTab): that surface still
       holds its text, and writing it again would reset the caret to offset 0 and drop the selection
       (AvalonEdit's Text setter), losing the reading position of every tab whenever a view change in
       another tab made its stored signature stale (user, 2026-10-31). A new surface, a file load and
       a discarded edit still write the text; a length that disagrees with the mirror falls back to
       the write so the two can never drift apart. */
    void RenderSurface(bool replaceText){
        if(applyingLoadedFile)MeasureFileOpen("render-start");FlushDocumentChanges();loading=true;try{
            source.FontSize=fontSize;source.Foreground=customColor.HasValue?FrozenBrush(customColor.Value):Foreground;source.FontFamily=new FontFamily(family);ApplySourceLines();if(replaceText||source.Document==null||source.Document.TextLength!=text.Length)source.Text=text;sourceMirrorOutdated=false;if(applyingLoadedFile)MeasureFileOpen("render-source-done");
            ApplySourceLines();ApplyWrapSurfaceFlags();ApplyPageInsets();
        }finally{loading=false;}UpdateLivePreview();UpdateTitle();UpdateTools();RefreshOutline();SavePreferences();if(applyingLoadedFile)MeasureFileOpen("render-done");
    }
    void UpdateTitle(){UpdateUndoRedoTools();Title=(HasUnsavedChanges?"* ":"")+(path==""?(activeDocument!=null&&activeDocument.RecoveryName!=null?activeDocument.RecoveryName:T("새 문서")):System.IO.Path.GetFileName(path))+" — MDNote";UpdateStatusText();RefreshSearch();RefreshTabCaption();}
    /* The status line: mode, zoom, caret, length and encoding, with a transient notice (a replace
       run's count, a refused link) leading the line while one is showing. P2-3: the message used to
       replace the whole line, taking the caret position, zoom and encoding away until the next
       typing pass. It goes first because the readers that parse the replace count at the start of
       the line (FindAbuseProbe's accounting, LivePreviewProbe's count checks) read this text. */
    void UpdateStatusText(){
        status.Text=(statusNotice==null?"":statusNotice+"   ·   ")+(editing?(PlainText?T("TXT 편집"):!LivePreviewWanted?T("원문 편집"):T("수정")):T("읽기"))+"   ·   "+Math.Round(fontSize/16*100)+"%   ·   ["+CaretStatus()+"]   "+(sourceSnapshotDirty?source.Document.TextLength:text.Length).ToString("N0")+T("자")+"   ·   "+EncodingCaption(documentEncoding);
        if(historyResetNoticeTimer!=null&&historyResetNoticeTimer.IsEnabled)status.Text=T("실행 취소 기록이 문서와 맞지 않아 초기화했습니다");
    }
    /* One shared slot for the answers that belong next to the normal status. It clears on the next
       caret move (TextGutter's RefreshCaretStatus) or after four seconds, whichever comes first. */
    string statusNotice;
    System.Windows.Threading.DispatcherTimer statusNoticeTimer;
    void ShowStatusNotice(string message){
        if(String.IsNullOrEmpty(message))return;
        statusNotice=message;
        if(statusNoticeTimer==null){
            statusNoticeTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(4)};
            statusNoticeTimer.Tick+=delegate{statusNoticeTimer.Stop();statusNotice=null;UpdateStatusText();};
            Closed+=delegate{statusNoticeTimer.Stop();};
        }
        statusNoticeTimer.Stop();statusNoticeTimer.Start();
        UpdateTitle();
    }
    void ClearStatusNotice(){if(statusNotice==null)return;statusNotice=null;if(statusNoticeTimer!=null)statusNoticeTimer.Stop();UpdateStatusText();}
    void SwitchSurface(){if(loading)return;FinishComposition();FlushDocumentChanges();/* The raw toggle reflows every line (markers are drawn again), so the caret can leave the viewport while the scroll offset stays. Measure before the switch and scroll only if it was on screen. */bool keepCaret=CaretLineOnScreen();LayoutMode();UpdateTitle();/* The toggle attaches or detaches the preview, so the table tool row and the in-table button gate answer for the new surface at once (the tab switch does the same). */UpdateTools();if(keepCaret){KeepCaretLineVisible();QueueKeepCaretLineVisible();}}
    // A mode switch changes line heights (live headings, hidden markers) or the source height (the
    // ribbon shows/hides) after the view bookmark was taken, so a caret the user could see can end
    // up above or below the viewport. These helpers measure only the caret line and scroll the
    // minimum it needs; a caret the user had scrolled away from stays away. The document is never
    // scanned and nothing here runs on a typing path.
    bool CaretLineOnScreen(){
        double top,bottom;return CaretLineSpan(out top,out bottom)&&bottom>0&&top<source.ViewportHeight;
    }
    bool CaretLineSpan(out double top,out double bottom){
        top=0;bottom=0;
        if(source==null||source.Document==null||!IsLoaded||source.ViewportHeight<=0)return false;
        UpdateLayout();
        var view=source.TextArea.TextView;
        try{view.EnsureVisualLines();}catch(InvalidOperationException){return false;}catch(ICSharpCode.AvalonEdit.Rendering.VisualLinesInvalidException){return false;}
        if(!view.VisualLinesValid||source.ViewportHeight<=0)return false;
        try{
            int caret=Math.Max(0,Math.Min(source.CaretOffset,source.Document.TextLength));
            var line=source.Document.GetLineByOffset(caret);
            Rect union=Rect.Empty;int from=line.Offset,to=line.EndOffset;
            if(to-from>800){from=Math.Max(from,caret-60);to=Math.Min(to,caret+60);}
            // A character inside a hidden marker run has no rectangle of its own: union the line's
            // rects and fall back to its visual position when every character on it is folded away.
            for(int i=from;i<=to;i++){Rect rect=source.GetRectFromCharacterIndex(i);if(!rect.IsEmpty)union=union.IsEmpty?rect:Rect.Union(union,rect);}
            if(!union.IsEmpty){top=union.Top;bottom=union.Bottom;}
            else{var y=view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(line.LineNumber,1),ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop).Y-source.VerticalOffset;top=y;bottom=y+Math.Max(1,source.FontSize*1.3);}
            return true;
        }catch(InvalidOperationException){top=0;bottom=0;return false;}
        // EnsureVisualLines (above) can throw this when the view's visual lines are invalidated while
        // it builds them; GetRectFromCharacterIndex / GetVisualPosition reach the same state, so the
        // mode-switch click must not crash on it either.
        catch(ICSharpCode.AvalonEdit.Rendering.VisualLinesInvalidException){top=0;bottom=0;return false;}
    }
    void KeepCaretLineVisible(){
        double top,bottom;if(!CaretLineSpan(out top,out bottom))return;
        if(bottom<=0)source.ScrollToVerticalOffset(Math.Max(0,source.VerticalOffset+top));
        else if(top>=source.ViewportHeight)source.ScrollToVerticalOffset(source.VerticalOffset+bottom-source.ViewportHeight);
    }
    // The mode switch restores its view bookmark from a posted callback; this correction must run
    // after that restore, on the same document and surface it measured.
    void QueueKeepCaretLineVisible(){
        var editor=source;var surface=source==null?null:source.Document;var document=activeDocument;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{
            if(Object.ReferenceEquals(document,activeDocument)&&Object.ReferenceEquals(editor,source)&&source!=null&&Object.ReferenceEquals(surface,source.Document))KeepCaretLineVisible();
        }));
    }
    /* A page command or a line-structure edit (Enter, a joined line, a paste with line breaks) reveals
       the caret from the height tree's estimates. The measure that follows can move the caret's line
       (a >16K line's fold height, a split long line) and leave the caret outside the viewport: the
       probe saw PageDown end 114 px below the bottom edge, and Enter that split a 100K line scroll to
       the old fold's bottom (24K px away). Like the mode switch above, verify once at Background -
       after the frame's own layout - and let the editor reveal the caret. Only the caret's own row is
       measured, and the check is posted once per action, never from a scroll or viewport change. */
    System.Windows.Threading.DispatcherOperation caretRowCheck;
    void QueueCaretRowVisible(){
        var editor=source;var surface=editor==null?null:editor.Document;var document=activeDocument;
        if(editor==null||surface==null)return;
        if(caretRowCheck!=null&&caretRowCheck.Status==System.Windows.Threading.DispatcherOperationStatus.Pending)return;
        caretRowCheck=Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{
            caretRowCheck=null;
            if(loading||applyingLoadedFile)return;
            if(!Object.ReferenceEquals(document,activeDocument)||!Object.ReferenceEquals(editor,source)||surface==null||!Object.ReferenceEquals(surface,source.Document))return;
            if(!CaretRowOnScreenNow())MarkdownLivePreview.Session.SafeBringCaretToView(source.TextArea);
        }));
    }
    bool CaretRowOnScreenNow(){
        if(source==null||source.Document==null||!IsLoaded||source.ViewportHeight<=0)return true;
        var view=source.TextArea.TextView;
        try{view.EnsureVisualLines();}
        catch(InvalidOperationException){return true;}
        catch(ICSharpCode.AvalonEdit.Rendering.VisualLinesInvalidException){return true;}
        int caret=Math.Max(0,Math.Min(source.CaretOffset,source.Document.TextLength));
        ICSharpCode.AvalonEdit.Document.DocumentLine line=source.Document.GetLineByOffset(caret);
        ICSharpCode.AvalonEdit.Rendering.VisualLine visual=view.GetVisualLine(line.LineNumber);
        if(visual==null||visual.IsDisposed){try{visual=view.GetOrConstructVisualLine(line);}catch(InvalidOperationException){return true;}catch(ICSharpCode.AvalonEdit.Rendering.VisualLinesInvalidException){return true;}}
        if(visual==null||visual.IsDisposed)return true;
        int relative=caret-line.Offset;
        int column=Math.Max(0,Math.Min(visual.GetVisualColumn(Math.Min(relative,line.Length)),visual.VisualLength));
        double y=visual.GetVisualPosition(column,ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle).Y;
        double top=view.VerticalOffset,bottom=top+source.ViewportHeight;
        return y>=top-1&&y<=bottom+1;
    }
    // A change that adds or removes a line break can move the caret's line through the height tree.
    // The newline search runs on the change's ITextSource, so a long removal's rope is scanned
    // without being materialized as one string, and a removal of any length counts: deleting a
    // multi-line selection joined lines exactly like Enter split them (the old 8-character cap
    // ignored every selection delete and Clear). A whole-document replacement is answered without
    // reading its text at all - it rebuilt every line, and the check it posts stays the safety net
    // for a caret the caller re-selects without scrolling (discard at edit exit: Render, then
    // source.Select(caret,0), with no view restore of its own). That keeps opening or switching to
    // a large document from scanning its whole buffer here.
    static readonly char[] LineBreakChars=new char[]{'\r','\n'};
    internal static bool LineStructureChanged(ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs change,int documentLength){
        if(change.Offset==0&&change.RemovalLength>0&&change.InsertionLength>0&&change.InsertionLength==documentLength)return true;
        // Removing the whole document leaves no line whose visibility could be checked.
        if(change.InsertionLength==0&&documentLength==0)return false;
        return HasLineBreak(change.InsertedText)||HasLineBreak(change.RemovedText);
    }
    static bool HasLineBreak(ICSharpCode.AvalonEdit.Document.ITextSource text){
        return text!=null&&text.TextLength>0&&text.IndexOfAny(LineBreakChars,0,text.TextLength)>=0;
    }
    internal void SetMode(bool value){
        FinishComposition();FlushDocumentChanges();
        bool keepCaret=!applyingLoadedFile&&CaretLineOnScreen();
        var bookmark=applyingLoadedFile?null:CaptureView();if(!applyingLoadedFile)BeginViewTransition();
        editing=value;if(value){loading=true;preview.IsChecked=false;loading=false;}else{/* b58: Ctrl+E runs while the size dropdown or the colour palette can still be open; the ribbon folds away under them and the popup stayed over an empty strip until a click. */sizeChoice=null;sizes.IsDropDownOpen=false;colorPopup.IsOpen=false;}
        editButton.Tag=editing?"Active":null;UpdateEditCaption();editButton.ToolTip=T(editing?"수정 모드 끄기 (Ctrl+E)":"수정 모드 켜기 (Ctrl+E)");System.Windows.Automation.AutomationProperties.SetName(editButton,editing?T("수정 종료"):T("수정 시작"));
        LayoutMode();if(editing)source.Focus();UpdateTitle();
        if(!applyingLoadedFile){RestoreView(bookmark);if(keepCaret)QueueKeepCaretLineVisible();}else MeasureFileOpen("set-mode-done");
    }
    void LayoutMode(){UpdateTextModeMenu();UpdateGutter();UpdateOutlineMode();source.IsReadOnly=!editing||documentOperationBusy;preview.IsEnabled=!PlainText;source.Visibility=Visibility.Visible;foreach(UIElement c in formatControls)c.IsEnabled=LiveFormat;UpdateCodeAvailability();UpdateTableTools();ribbonHost.Visibility=editing&&!PlainText?Visibility.Visible:Visibility.Collapsed;UpdateLivePreview();ApplyPageInsets();/* a file open or a save's conversion sets documentOperationBusy here: the find bar's replace buttons follow that state at once */RefreshReplaceButtonState();UpdateUndoRedoTools();}
    async void Toggle(){if(documentOperationBusy)return;/* Switching modes re-renders the surface, the same view-level write a toolbar command makes: wait for a live composition's store to be released. */if(imeComposing||StoreGuardLive()){source.RunAfterComposition(delegate{Toggle();});return;}if(!editing){SetMode(true);if(!editing)return;}else if(!HasUnsavedChanges)SetMode(false);else await ApplyEditExitAsync(AskSave());}
    // 버리기 on the edit-exit prompt promises the last saved state, so the buffer itself has to go
    // back: the old path only rewound the `text` mirror, and the visible source kept the typed
    // text while the dirty flag it feeds cleared the * and • marks. The rewrite goes through
    // Render, the same whole-buffer path a file load uses, so source, live view and TXT all show
    // the restored text and the caret stays inside it. History is cleared rather than recording
    // the discard: SetText (file load, tab switch) clears it for the same wholesale replacement,
    // and an entry over the discarded span would let Ctrl+Z bring back exactly the text the
    // prompt just threw away.
    void DiscardEditExitChanges(){
        /* A session-restored tab's save point is the recovered text itself, and that text never
           reached its file: the discard drops this edit session's changes, not the recovered text,
           so the tab has to stay modified. Clearing the mark here dropped the * and closing then
           discarded the text with no prompt and no draft. A crash-restored tab (an empty save
           point) really does revert to it and keeps the old behaviour. */
        bool keepRecovered=recoveredDocument&&saved.Length>0;
        recoveredDocument=keepRecovered;
        int caret=source==null?0:source.CaretIndex;
        CancelEditorRefresh();
        History.Clear();
        text=saved;
        Render();
        if(source!=null){if(caret>source.Document.TextLength)caret=source.Document.TextLength;if(caret<0)caret=0;source.Select(caret,0);}
        if(!keepRecovered)ForgetRecovery(activeDocument);
    }
    internal bool ApplyEditExit(SaveChoice choice){if(!HasUnsavedChanges){SetMode(false);return true;}if(choice==SaveChoice.Cancel)return false;if(choice==SaveChoice.Save&&!Save(false))return false;if(choice==SaveChoice.Discard)DiscardEditExitChanges();SetMode(false);return true;}
    // The command path awaits the save so a large format-changing write finishes (and the overlay
    // closes) before the edit session ends; the synchronous entry point above stays for the tests.
    async System.Threading.Tasks.Task<bool> ApplyEditExitAsync(SaveChoice choice){if(!HasUnsavedChanges){SetMode(false);return true;}if(choice==SaveChoice.Cancel)return false;if(choice==SaveChoice.Save&&!(await SaveTaskOnDispatcher(false)))return false;if(choice==SaveChoice.Discard)DiscardEditExitChanges();SetMode(false);return true;}
    void Undo(){RestoreEditorHistory(false);UpdateTools();}
    void Redo(){RestoreEditorHistory(true);UpdateTools();}
    void ToggleFormat(string kind){if(LiveInline(kind))return;}
    bool committingSize,cancelSizeChoice;string sizeChoice;
    void FontSizeItemMouseDown(object sender,MouseButtonEventArgs args){if(!sizes.IsDropDownOpen||args.ChangedButton!=MouseButton.Left)return;var item=ItemsControl.ContainerFromElement(sizes,args.OriginalSource as DependencyObject) as ComboBoxItem;string choice=item==null?null:item.Content as string;if(choice!=null&&sizes.Items.IndexOf(choice)>0)sizeChoice=choice;}
    void FontSizeDropStateChanged(object sender,EventArgs args){if(sizes.IsDropDownOpen||sizeChoice==null||cancelSizeChoice)return;string choice=sizeChoice;sizeChoice=null;/* Popup.Closed waits for the close animation; the property change must apply before the next input. */ApplySizeChoice(choice);}
    void ApplySizeChoice(string choice){if(committingSize)return;committingSize=true;try{sizes.Text=choice;sizeText=choice;SetFontSize(Double.Parse(choice.Replace(" px",""),CultureInfo.InvariantCulture));}finally{committingSize=false;}}
    void FontSizeKeyDown(object sender,KeyEventArgs args){if(args.Key==Key.Enter){if(sizes.IsDropDownOpen)return;CommitFontSize(true);args.Handled=true;}else if(args.Key==Key.Escape){cancelSizeChoice=true;sizeChoice=null;committingSize=true;try{sizes.IsDropDownOpen=false;sizes.Text=sizeText;source.Focus();}finally{committingSize=false;}UpdateTools();args.Handled=true;}}
    void CommitFontSize(bool showError){if(updatingTools||committingSize||sizes.IsDropDownOpen||!LiveFormat)return;string entry=(sizes.Text??"").Replace("px","").Trim();double value;if((Double.TryParse(entry,NumberStyles.Float,CultureInfo.CurrentCulture,out value)||Double.TryParse(entry,NumberStyles.Float,CultureInfo.InvariantCulture,out value))&&!Double.IsNaN(value)&&!Double.IsInfinity(value)&&value>=6&&value<=120){committingSize=true;try{sizeText=sizes.Text;SetFontSize(value);if(showError)source.Focus();}finally{committingSize=false;}}else{sizeText=sizes.Text;UpdateTools();if(showError)MessageBox.Show(this,T("글자 크기는 6~120 사이의 숫자로 입력해 주세요."),"MDNote");}}
    void SetFontSize(double value){if(!LiveFormat)return;int pixels=(int)Math.Round(value);RunLiveCommand(delegate(ICSharpCode.AvalonEdit.Document.TextDocument d,int s,int l){return l==0?MarkdownCommands.SetTypingSize(d,s,pixels):MarkdownCommands.SetSpanSize(d,s,l,pixels);});}
    void SetTextColor(Color value){LiveColor(value);}
    void UpdateTools(){UpdateUndoRedoTools();LiveUpdateTools();}
    // Never overwrite an in-progress keyboard entry. The live surface has no rich selection
    // properties, so the number is what the preview draws at the caret (LiveDrawnSize below):
    // an explicit span size, then the draw's own scales. The basic view reports the character's
    // own size, which *is* the base size when the caret has no span and the heading size inside a
    // heading paragraph. Reading sizeText first (as this used to) showed whatever had been typed
    // last, so live kept "16 px" while the body was 8 px or 48 px, and a heading line kept the
    // body size while the paragraph style correctly read "제목1".
    void RefreshSizeBox(){
        if(updatingTools||committingSize||sizes.IsDropDownOpen||sizes.IsKeyboardFocusWithin)return;
        // A pending size style shows the size the next typed text will get, like the bold and
        // colour toggles show their own pending state (user, 2026-10-29: the box snapped back to
        // the caret's size immediately, so choosing a size looked like nothing happened).
        double pending=PendingTypingSize();
        double pixels=pending>0?pending:LiveDrawnSize();
        string size=pixels.ToString("0.##",CultureInfo.InvariantCulture)+" px";
        updatingTools=true;try{if(!sizes.Items.Contains(size))sizes.Items.Add(size);if(!Object.Equals(sizes.SelectedItem,size))sizes.SelectedItem=size;sizes.Text=size;sizeText=size;}finally{updatingTools=false;}
        FitSizeCaption();
    }
    /* The size box is sized for the menu's integer captions ("48 px"), but the live view can report
       a heading's scaled size ("20.8 px" at 16 px, H2), which is wider than that slot and used to be
       cut to "20.8 p". The value stays exact; only the caption's own type shrinks by the half-pixel
       step the spare width allows, so nothing else in the ribbon moves. */
    TextBox SizeCaptionField(){
        if(sizes.Template==null){if(sizes.IsLoaded)sizes.ApplyTemplate();if(sizes.Template==null)return null;}
        return sizes.Template.FindName("PART_EditableTextBox",sizes) as TextBox;
    }
    double MeasureSizeCaption(TextBox field,string text){
        if(String.IsNullOrEmpty(text))return 0;
        var face=new Typeface(field.FontFamily,field.FontStyle,field.FontWeight,field.FontStretch);
        var formatted=new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,Ui.LabelSize,Brushes.Black,null,TextFormattingMode.Display,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }
    /* Never wait for a width that may not come: the ribbon is collapsed in reading mode and the
       caption slot is 0 wide there. The Loaded/SizeChanged hooks run the fit again once the box is
       really laid out, so returning here is not losing the update. */
    void FitSizeCaption(){
        var field=SizeCaptionField();
        if(field==null||sizes.ActualWidth<=0)return;
        double available=field.ActualWidth-field.Padding.Left-field.Padding.Right;
        if(available<=1)return;
        double width=MeasureSizeCaption(field,sizes.Text??"");
        double target=Ui.LabelSize;
        if(width>available-1&&width>0)target=Math.Floor((Ui.LabelSize-0.25)*available/width*2)/2;
        if(target<9)target=9;
        if(target>Ui.LabelSize)target=Ui.LabelSize;
        if(Math.Abs(field.FontSize-target)>.01)field.FontSize=target;
    }
    // The size the live preview really draws at the caret, read from the caret line only: the
    // session caches one LineInfo per visible line, so this is O(line) and never re-parses the
    // body. MarkdownTransformer (MarkdownLivePreview.cs) starts every line at the base size, applies
    // the line's heading scale, and then lets each styled part covering the offset set a size; a later
    // part overrides an earlier one, and within one part the superscript mark wins over the muted
    // note scale, which wins over an explicit span size. This resolves in that same order, so a
    // heading reports the size actually drawn on it and a fenced code body stays at the base size,
    // which is what the preview draws there.
    double LiveDrawnSize(){
        if(!LivePreviewAttached||source==null)return fontSize;
        int offset=Math.Max(0,Math.Min(source.CaretIndex,source.Document.TextLength));
        var line=source.Document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        if(info==null)return fontSize;
        double baseSize=livePreview.Options==null||livePreview.Options.FontSize<=0?fontSize:livePreview.Options.FontSize;
        int relative=offset-line.Offset;
        // The caret on the outer edge of a hidden closing run draws on that run's inside (the
        // same rule typed text follows), so the box reports the size the next character gets.
        int inside=MarkdownLivePreview.Session.HiddenCloseInside(info,relative);
        if(inside>=0)relative=inside;
        // The heading scale is applied to the text after the list prefix, so a bullet keeps its own size.
        double drawn=info.Heading>0&&relative>=(info.List!=null?info.ListBody:0)?baseSize*Markdown.HeadingScale(info.Heading):baseSize;
        foreach(MarkdownLivePreview.StyledPart part in info.Styles){
            if(relative<part.Start||relative>part.Start+part.Length)continue;
            /* only parts that set a size matter here; bold, italic, strike, code and link restyle ink or typeface */
            if(part.Size>0)drawn=part.Size;
            if((part.Style&MarkdownLivePreview.Style.Small)!=0)drawn=baseSize*0.9;
            if((part.Style&MarkdownLivePreview.Style.Superscript)!=0)drawn=baseSize*0.75;
        }
        return drawn;
    }
    void Heading(){LiveHeading(2);}
    void Bullets(){ToggleListStyle(false);}
    void Quote(){LiveQuote();}
    void DocumentColor(){if(!LiveFormat)return;using(var d=new Forms.ColorDialog()){if(d.ShowDialog()==Forms.DialogResult.OK)SetTextColor(Color.FromRgb(d.Color.R,d.Color.G,d.Color.B));}}
    void InsertLink(){LiveLink();}
    string Input(string title,string initial){var dialog=new Window {Owner=this,Title=title,Width=400,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=(Brush)Resources["Paper"],Foreground=(Brush)Resources["Ink"]};/* b56: Esc closes the link/text prompt like the shortcut window (user, 2026-10-30); ShowDialog returns false and the caller puts the caret back in the body. */dialog.PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){dialog.Close();e.Handled=true;}};dialog.Resources.MergedDictionaries.Add(Resources);var panel=new StackPanel {Margin=new Thickness(20)};dialog.Content=panel;var box=new TextBox {Text=initial,Padding=new Thickness(8),Margin=new Thickness(0,0,0,16)};panel.Children.Add(box);var okay=new Button {Content=T("확인"),IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right};okay.Click+=delegate{dialog.DialogResult=true;};panel.Children.Add(okay);dialog.Loaded+=delegate{box.Focus();box.SelectAll();};TranslateTree(dialog);return dialog.ShowDialog()==true?box.Text:null;}
    void SetWrap(bool value){SetWrapNow(value);}
    void ApplyWrap(){ApplyWrapSurfaceFlags();}
    // The per-surface half of the wrap setting: the source's wrapping and scroll bar policy.
    void ApplyWrapSurfaceFlags(){
        source.TextWrapping=wordWrap?TextWrapping.Wrap:TextWrapping.NoWrap;
        source.HorizontalScrollBarVisibility=wordWrap?ScrollBarVisibility.Disabled:ScrollBarVisibility.Auto;
    }
    void SurfaceShown(){
        if(loading||documentOperationBusy||fileOpenClosed)return;
        ApplyWrapSurfaceFlags();
    }
    internal static double SourceInset(SourceEditor editor){return Math.Max(0,24-editor.VerticalOffset);}
    // #11/#26: margins scroll with the document (no fixed top/bottom band); list markers
    // are drawn left of the list margin, so the page keeps at least 14px of its own padding.
    int insetBucket=-1;double insetDpi;SourceEditor insetSurface;ICSharpCode.AvalonEdit.Document.TextDocument insetDocument;
    void ApplyPageInsets(){double side=ActualWidth>0&&ActualWidth<600?20:32;/* #24: the line-number column lives inside the left margin, so text keeps its window position when editing turns on/off */double numbers=textGutter.WidthForLines(source.Document.LineCount,VisualTreeHelper.GetDpi(this).PixelsPerDip),left=Math.Max(side,numbers+8);var inner=new Thickness(textGutter.Visibility==Visibility.Visible?left-numbers:left,0,side,0);if(source.Padding!=inner)source.Padding=inner;}
    static FrameworkElement SourceTextView(DependencyObject parent){for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child.GetType().Name=="TextBoxView")return child as FrameworkElement;var found=SourceTextView(child);if(found!=null)return found;}return null;}
    // Moving the render surface keeps the TextBox formatting cache intact during scrolling.
    // #11: a 24px end margin slides in over the last 24px of scrolling. It is computed from the
    // viewport without the bottom margin, so changing the margin cannot feed back into itself.
    static void ApplySourceInset(SourceEditor editor){var view=editor.TextArea.TextView;if(view==null)return;double inset=SourceInset(editor);/* #11: a document that fits the viewport (top of the scroll range, content no taller than the viewport) keeps no end margin and is never scrolled to the end */bool fits=editor.ExtentHeight<=editor.ViewportHeight+view.Margin.Bottom&&editor.VerticalOffset<=0;if(fits){if(Math.Abs(view.Margin.Top-inset)>.1||Math.Abs(view.Margin.Bottom)>.1)view.Margin=new Thickness(0,inset,0,0);return;}double remaining=editor.ExtentHeight-(editor.ViewportHeight+view.Margin.Bottom)-editor.VerticalOffset;double bottom=Math.Max(0,Math.Min(24,24-remaining));bool grewAtEnd=bottom>view.Margin.Bottom+.1&&remaining<=.5;if(Math.Abs(view.Margin.Top-inset)>.1||Math.Abs(view.Margin.Bottom-bottom)>.1)view.Margin=new Thickness(0,inset,0,bottom);if(grewAtEnd)editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{editor.ScrollToVerticalOffset(editor.ExtentHeight);}));}
    // TextView is only measurable at a point where its last measure and arrange belong to the
    // document on screen (a fresh document, a surface that just became visible, a zoom or wrap
    // change all invalidate them).
    static bool SourceInsetGeometryReady(SourceEditor editor){
        if(editor==null||editor.TextArea==null)return false;
        var view=editor.TextArea.TextView;
        return view!=null&&view.VisualLinesValid&&view.IsMeasureValid&&view.IsArrangeValid;
    }
    /* Finding 3: the inset echo is still skipped (it would ping-pong), but a real window resize near
       the document end shares its signature and would leave ApplySourceInset/FollowDocumentEnd never
       running. One re-check is posted at Loaded, after the frame's layout; it spends the flag, so an
       echo caused by its own margin change queues nothing more, and only a non-echo scroll change, an
       echo that found TextView's geometry not yet valid (the ScrollChanged handler above), or a new
       surface/document arms the next one. FollowDocumentEnd stays skipped for echoes. */
    bool insetRecheckPending,insetRecheckSpent;
    void QueueSourceInsetRecheck(SourceEditor editor){
        if(insetRecheckSpent||insetRecheckPending)return;
        insetRecheckPending=true;
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{
            insetRecheckPending=false;
            if(!Object.ReferenceEquals(editor,source))return;
            /* Still not laid out (a collapsed surface, a layout that was deferred again). Stop
               without spending the flag and leave the queueing to the next scroll change: a re-post
               from here would run at the same point again and would never end. */
            if(!SourceInsetGeometryReady(editor))return;
            insetRecheckSpent=true;
            ApplySourceInset(editor);
        }));
    }
    // Ctrl+End reveals the last line while its height is still the estimate; the measure that follows
    // stores the real, taller lines and the extent grows after that reveal, so nobody scrolls again and
    // the empty last line stays below the viewport. Only a view that was already at the end follows.
    static void FollowDocumentEnd(SourceEditor editor,ScrollChangedEventArgs args){
        if(args.ExtentHeightChange<=0)return; // extent growth only: the top inset (ApplySourceInset) makes the viewport oscillate by 3px, and reacting to that re-posted BringCaretToView forever
        double oldExtent=args.ExtentHeight-args.ExtentHeightChange,oldViewport=args.ViewportHeight-args.ViewportHeightChange;
        if(args.ViewportHeight<=0||oldViewport<=0||args.VerticalOffset+oldViewport<oldExtent-0.5)return;
        if(editor.Document==null||editor.TextArea.Caret.Line!=editor.Document.LineCount)return;
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{if(editor.Document!=null)MarkdownLivePreview.Session.SafeBringCaretToView(editor.TextArea);}));
    }
    // Ctrl +/- and Ctrl+wheel. Several presses inside one composition fold into a single deferred
    // delta (ViewContinuity.cs), because the first one renders a new document and a second queued
    // entry would be dropped as stale.
    void Zoom(double delta){if(DeferViewSettings){QueueViewSettings(delegate(PendingViewSettings pending){pending.Size=Math.Max(8,Math.Min(48,pending.Size+delta));pending.Font=true;});return;}SetBaseFontNow(family,fontSize+delta,false);}
    SaveChoice AskSave(){return AskSaveCore(null,false);}
    // b50: the prompt names the tab it asks about (the minimized close path asks tabs without switching).
    string DocumentPromptName(DocumentTab state){
        string file=state==null?path:state.Path,recovery=state==null?null:state.RecoveryName;
        return file==""?(recovery==null?T("새 문서"):recovery):System.IO.Path.GetFileName(file);
    }
    Window BuildSaveDialog(string documentName,Action<SaveChoice> choose,bool minimized){
        var dialog=new Window {Title=T("변경 내용 저장"),Width=480,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,FontFamily=FontFamily,FontSize=13,Background=(Brush)Resources["Paper"],Foreground=(Brush)Resources["Ink"]};
        if(minimized){dialog.ShowInTaskbar=true;dialog.WindowStartupLocation=WindowStartupLocation.Manual;}
        else{dialog.Owner=this;dialog.ShowInTaskbar=false;dialog.WindowStartupLocation=WindowStartupLocation.CenterOwner;}
        dialog.Resources.MergedDictionaries.Add(Resources);
        var stack=new StackPanel {Margin=new Thickness(24)};dialog.Content=stack;
        stack.Children.Add(new TextBlock {Text=T("변경 내용을 저장할까요?"),FontSize=19,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)});
        stack.Children.Add(new Border {Background=(Brush)Resources["Ribbon"],BorderBrush=(Brush)Resources["Line"],BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),Padding=new Thickness(12,9,12,9),Margin=new Thickness(0,0,0,12),Child=new TextBlock {Text=documentName,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,LineHeight=21}});
        stack.Children.Add(new TextBlock {Text=T("저장하지 않은 변경 내용이 있습니다."),TextWrapping=TextWrapping.Wrap,LineHeight=21,Margin=new Thickness(0,0,0,5)});
        stack.Children.Add(new TextBlock {Text=T("버리기를 누르면 마지막 저장 상태로 돌아갑니다."),TextWrapping=TextWrapping.Wrap,LineHeight=20,Foreground=(Brush)Resources["Muted"],FontSize=12});
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,-4,0)};stack.Children.Add(buttons);
        var cancel=new Button {Content=T("취소"),IsCancel=true};cancel.Click+=delegate{choose(SaveChoice.Cancel);dialog.Close();};buttons.Children.Add(cancel);
        var discard=new Button {Content=T("버리기")};discard.Click+=delegate{choose(SaveChoice.Discard);dialog.Close();};buttons.Children.Add(discard);
        var save=new Button {Content=T("저장"),IsDefault=true,Tag="Active"};save.Click+=delegate{choose(SaveChoice.Save);dialog.Close();};buttons.Children.Add(save);
        foreach(Button button in buttons.Children){button.Width=92;button.Height=30;button.Padding=new Thickness(8,2,8,2);button.Margin=new Thickness(4,0,4,0);button.SetResourceReference(Control.BorderBrushProperty,"ButtonLine");}
        TranslateTree(dialog);return dialog;
    }
    SaveChoice AskSaveCore(string documentName,bool minimized){
        if(!minimized)PrepareSaveOwner();
        if(Program.IsSelfTest&&queuedSaveChoices.Count>0)return queuedSaveChoices.Dequeue();SaveChoice choice=SaveChoice.Cancel;
        var dialog=BuildSaveDialog(documentName==null?DocumentPromptName(null):documentName,delegate(SaveChoice value){choice=value;},minimized);
        if(!minimized){dialog.Loaded+=delegate{dialog.Activate();SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(dialog).Handle);};ShowSaveConfirmation(dialog);return choice;}
        // b50: a minimized owner must not be restored before the prompt. An owned window of a minimized
        // owner stays hidden, so show it unowned and topmost on the monitor that holds the restored window.
        Rect restore=RestoreBounds;Rect work=UsablePlacement(restore)?WorkAreaFor(restore):SystemParameters.WorkArea;
        if(!(work.Width>0)||!(work.Height>0))work=new Rect(0,0,SystemParameters.PrimaryScreenWidth,SystemParameters.PrimaryScreenHeight);
        /* Measure the content before showing so the first frame is already centred (no move after Loaded).
           The 40px adds the caption bar and frame that SizeToContent puts around the content. */
        var content=(FrameworkElement)dialog.Content;content.Measure(new Size(480,double.PositiveInfinity));
        Rect start=CenteredPlacement(work,new Size(480,content.DesiredSize.Height+40));dialog.Left=start.Left;dialog.Top=start.Top;dialog.Topmost=true;
        dialog.Loaded+=delegate{
            /* Raise it once, then stop being topmost. */
            dialog.Activate();IntPtr handle=new System.Windows.Interop.WindowInteropHelper(dialog).Handle;if(handle!=IntPtr.Zero)SetForegroundWindow(handle);
            dialog.Topmost=false;
        };
        dialog.ContentRendered+=delegate{closeTraceRendered=System.Diagnostics.Stopwatch.GetTimestamp();};
        var previous=saveConfirmationDialog;saveConfirmationDialog=dialog;closeTraceShow=System.Diagnostics.Stopwatch.GetTimestamp();
        // K3: a close queued while this prompt was open is processed once the prompt is answered.
        try{dialog.ShowDialog();}finally{saveConfirmationDialog=previous;ResumeDeferredCloses();}
        return choice;
    }
    // b50: minimized window close asks each dirty tab in turn without restoring the window or switching
    // the visible tab. A tab is switched (invisibly) only when the user saves it, and the window is
    // restored only when that save needs a Save As dialog.
    async System.Threading.Tasks.Task<bool> ConfirmTabsMinimized(){
        if(TryCloseWithSession())return true;
        CaptureTab();var previous=activeDocument;
        try{
            foreach(var state in new List<DocumentTab>(documents)){
                bool current=Object.ReferenceEquals(state,activeDocument);
                bool dirty=current?HasUnsavedChanges:(state.Recovered||state.Text!=state.Saved);
                if(!dirty)continue;
                SaveChoice answer=AskSaveCore(DocumentPromptName(state),WindowState==WindowState.Minimized);
                if(answer==SaveChoice.Cancel)return false;
                if(answer==SaveChoice.Discard)continue;
                if(!current){switchingTabs=true;documentTabs.SelectedItem=state.Item;switchingTabs=false;SwitchTab(state);}
                if(WindowState==WindowState.Minimized&&(path==""||FormatMismatch(path,PlainText)))EnsureVisibleForeground();
                if(!(await SaveTaskOnDispatcher(false)))return false;
            }
            return true;
        }finally{
            if(previous!=null&&!Object.ReferenceEquals(activeDocument,previous)&&documents.Contains(previous)){switchingTabs=true;documentTabs.SelectedItem=previous.Item;switchingTabs=false;SwitchTab(previous);}
        }
    }
    bool Confirm(){if(!HasUnsavedChanges)return true;var answer=AskSave();if(answer==SaveChoice.Discard)return true;if(answer!=SaveChoice.Save)return false;if(Save(false))return true;/* A save refused while a file is opening or saving must not fail silently after the prompt closed: same box as SaveFromUi. */if(!Program.IsSelfTest&&!LastSaveCancelled&&!LastSaveReported){EnsureVisibleForeground();MessageBox.Show(this,LastSaveFailure??T("저장할 수 없습니다.\n"),"MDNote");}return false;}
    // Awaitable counterparts used by the close batch (WindowPlacement.ProcessWindowCloses). The
    // synchronous Confirm/ConfirmTabs in Workspace.cs stay for the self-test close path.
    async System.Threading.Tasks.Task<bool> ConfirmAsync(){if(!HasUnsavedChanges)return true;var answer=AskSave();if(answer==SaveChoice.Discard)return true;if(answer!=SaveChoice.Save)return false;if(await SaveTaskOnDispatcher(false))return true;if(!Program.IsSelfTest&&!LastSaveCancelled&&!LastSaveReported){EnsureVisibleForeground();MessageBox.Show(this,LastSaveFailure??T("저장할 수 없습니다.\n"),"MDNote");}return false;}
    async System.Threading.Tasks.Task<bool> ConfirmTabsAsync(){/* 이전 세션 계속하기: the option answers the close for the whole window at once, before any tab is switched, so the prompt never opens and the session's first document is the one that was on screen. */if(TryCloseWithSession())return true;CaptureTab();var previous=activeDocument;try{foreach(var state in new List<DocumentTab>(documents)){bool current=Object.ReferenceEquals(state,activeDocument);if(!current&&state.Text==state.Saved&&!state.Recovered)continue;if(!current){switchingTabs=true;documentTabs.SelectedItem=state.Item;switchingTabs=false;SwitchTab(state);}if(!(await ConfirmAsync()))return false;}return true;}finally{if(previous!=null&&documents.Contains(previous)&&!Object.ReferenceEquals(activeDocument,previous)){switchingTabs=true;documentTabs.SelectedItem=previous.Item;switchingTabs=false;SwitchTab(previous);}}}
    void NewFile(){if(fileOpenRunning)CancelFileOpen();if(!openInTabs){if(path==""&&text==""&&saved==""&&!recoveredDocument){SetMode(true);return;}var window=new MainWindow();window.SetOpenInTabs(false);window.Show();window.EnsureVisibleForeground();window.SetMode(true);return;}CreateTab();SetMode(true);}
    void OpenDialog(){var d=new Microsoft.Win32.OpenFileDialog {Multiselect=true,Filter=T("Markdown / 텍스트|*.md;*.markdown;*.txt|모든 파일|*.*")};if(d.ShowDialog(this)==true)foreach(string selected in d.FileNames)LoadFile(selected);}
    void LoadFileCore(string name){try{Encoding encoding;string fingerprint;byte[] originalBytes;string value=ReadTextFileVersion(name,out encoding,out fingerprint,out originalBytes);bool plain=String.Equals(System.IO.Path.GetExtension(name),".txt",StringComparison.OrdinalIgnoreCase);/* One normalization, one length: the size decisions below and ApplyLoadedFile's buffer (and its basic-view flag) must agree on a body just past the limit, where a file whose detected newline is CRLF is shorter on disk than in the buffer if it also holds bare LF breaks. */string newline=DetectNewline(value);string buffer=NormalizeLineEndings(value,newline);if(buffer==value)originalBytes=null;ApplyLoadedFile(name,value,encoding,fingerprint,originalBytes,buffer,newline);}catch(Exception ex){if(Program.IsSelfTest)throw;EnsureVisibleForeground();MessageBox.Show(this,T("파일을 열 수 없습니다.\n")+ex.Message,"MDNote");}}
    bool Save(bool saveAs){System.Threading.Tasks.Task<bool> task=SaveTaskOnDispatcher(saveAs);if(task.IsCompleted)return task.GetAwaiter().GetResult();return WaitForSaveTask(task);}
    // One implementation for both entry points: the synchronous Save above pumps when the save is
    // a large format-changing conversion; the menu, Ctrl+S and the close batch await this method.
    // Test seams for the save-conflict prompt, the same shape as queuedSaveChoices: a probe answers
    // the "save as another name?" question and supplies the name in place of the native dialog. Null
    // means the real prompt and dialog; a self-test with neither keeps the old behaviour (throw).
    internal static MessageBoxResult? ConflictAnswer;
    // Replaces the Save As dialog's answer (the default path goes in, the chosen path comes out), so
    // a probe can drive the Save As and its conflict retry without a native dialog.
    internal static Func<string,string> SaveTargetPicker;
    // Probe seam for the "already open in another tab/window" Save As refusal, the same shape as
    // ImageDropNotice: a headless run records the message instead of opening a modal box.
    internal static Action<string> SaveTargetInUseNotice;
    // Probe readout: the drain's operation state when the conflict handler decided to retry. The
    // retry starts in the continuation of the operation that raised the conflict, so a true here
    // means the retry began while the document operation had not released the window yet (which
    // makes the retry's own busy check refuse it silently).
    internal static bool ConflictRetrySawBusy;
    // Probe readout: how many times the conflict handler ran, so a probe can prove the conflict
    // path it means to test was actually taken.
    internal static int ConflictPrompts;
    // A save that did not write a file. SaveFromUi shows LastSaveFailure once; a Save As dialog the
    // user cancelled is not a failure (the close prompt treats false as "not saved" too), and an
    // error whose own box was already shown is not repeated.
    internal string LastSaveFailure;
    internal bool LastSaveCancelled,LastSaveReported;
    // A save that is holding its file dialog until a counted IME result lands. A second press
    // while that wait runs is answered as "not saved, no box": the first press owns the dialog.
    bool saveWaitingForIme;
    /* The path the conflict prompt's "yes" approved travels as the retry save's own parameter: a
       window field would stay set while that save waits for the IME, so a second Ctrl+S in the same
       window could overwrite an external edit without asking (one prompt per save, UI bug hunt 4
       item 3). Only the retry call below passes it. */
    async System.Threading.Tasks.Task<bool> SaveAsync(bool saveAs,string approvedPath=null){
        LastSaveFailure=null;LastSaveCancelled=false;LastSaveReported=false;
        if(documentOperationBusy||saveInProgress){LastSaveFailure=T("저장할 수 없습니다.\n");return false;}
        FinishComposition();string target=path;/* A recovered draft has path="" until its first save: the original file decides the default name and filter. */string modePath=path.Length==0&&recoveredDocument&&activeDocument!=null?activeDocument.RecoveryOriginalPath??"":path;bool mismatch=FormatMismatch(modePath,PlainText);bool choose=saveAs||target==""||mismatch;bool retry=false;string approved=approvedPath;
        /* The Save As / native dialog is a modal window: a counted IME result still on its way
           would land in its file name box instead of the body. Wait for the commit first, on the
           same path a parked command uses (its 500 ms bound is the fallback). */
        if(choose&&source!=null&&source.ImeBusy){
            if(saveWaitingForIme){LastSaveCancelled=true;return false;}
            saveWaitingForIme=true;
            DocumentTab saveTab=activeDocument;
            try{
                var ready=new System.Threading.Tasks.TaskCompletionSource<bool>();
                /* The composition timer's 500 ms release runs the parked completion; the extra
                   tick is the fallback for a callback RunParked drops after a document swap. */
                var fallback=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input,Dispatcher){Interval=TimeSpan.FromMilliseconds(700)};
                fallback.Tick+=delegate{fallback.Stop();ready.TrySetResult(true);};
                fallback.Start();
                source.RunAfterComposition(delegate{fallback.Stop();ready.TrySetResult(true);});
                await ready.Task;
            }finally{saveWaitingForIme=false;}
            /* The wait yields to the dispatcher: a tab switch during it must not make this save
               write another tab's text to the path that was chosen for this one. */
            if(!Object.ReferenceEquals(saveTab,activeDocument)){LastSaveCancelled=true;return false;}
        }
        /* The first save's picture move, when it ran: a save that does not write puts the pictures
           and their absolute links back (the failure paths below call this too). */
        PastedImageMoveState movedPictures=null;
        try{
            Encoding encoding=documentEncoding;
            bool exportHtml=false;
            if(choose){
                EnsureVisibleForeground();
                /* Save As starts in the document's own folder with the document's own file name (a
                   recovered draft uses the original file RecoveryStore kept while the tab's path
                   field stays "", RC9Tests). The dialog's first entry keeps the document's
                   encoding, so a Shift-JIS/GBK/Big5/CP1252/UTF-32 document is not rewritten as
                   UTF-8 in silence. */
                string recoveredOriginal=recoveredDocument&&activeDocument!=null?activeDocument.RecoveryOriginalPath:null;
                string name=SaveAsDefaultPath(path,recoveredOriginal,mismatch,PlainText);
                if(name==null)name=activeDocument!=null&&activeDocument.RecoveryName!=null?activeDocument.RecoveryName:PlainText?T("문서.txt"):T("문서.md");
                Func<string,string> picker=SaveTargetPicker;
                if(picker!=null){
                    string picked=picker(name);
                    if(picked==null){LastSaveCancelled=true;return false;}
                    target=picked;
                    exportHtml=IsHtmlFileName(picked);   // the probe seam has no file type list: a .html/.htm name stands for the HTML type
                }else{
                    var selected=NativeSaveDialog.Show(new System.Windows.Interop.WindowInteropHelper(this).Handle,name,PlainText,T("현재 인코딩 유지")+" ("+EncodingCaption(encoding)+")",T("인코딩:"),T("텍스트 (*.txt)"),T("모든 파일"),T("HTML 파일 (*.html)"));
                    if(selected==null){LastSaveCancelled=true;return false;}
                    target=selected.Path;
                    exportHtml=selected.Html;
                    if(!exportHtml&&selected.Encoding!=NativeSaveDialog.KeepEncoding)encoding=StrictEncoding(selected.Encoding);
                }
                /* 2.2.0: the HTML file type of this dialog is an export, not a save. The document keeps its
                   path, title, type, encoding, dirty mark, recent list, external-change stamp and recovery
                   state, and no overwrite question or picture move runs for the .html file; the save
                   reports "not saved, no box" so a close prompt does not treat it as saved. */
                if(exportHtml){
                    target=HtmlExportTarget(target);
                    await ExportHtmlToAsync(target);
                    LastSaveCancelled=true;
                    return false;
                }
            }
            /* Every save (user, 2026-11-03): the pictures the text links from the shared temp folder
               move into <docname>\ beside the target and their links become relative before the
               bytes are written, so the file on disk holds the relative form; a Save As of a saved
               document also copies the pictures it links from the old <docname>\ (PasteSupport.cs
               MovePastedImagesToDocument). Without staged or old-folder pictures it only parses. */
            // 2.2.0: another program changed this file while MDNote stayed in front (FileChangeWatch.cs).
            if(!ConfirmOverwriteExternalChange(target)){LastSaveCancelled=true;return false;}
            movedPictures=MovePastedImagesToDocument(target);
            bool saved=await SaveToPathAsyncCore(target,encoding,choose,approved);
            /* Nothing was written: the tab is still untitled and must not be left pointing at
               pictures that left the temp folder for a file that was never made. */
            if(!saved)RestorePastedImagesAfterFailedSave(movedPictures);
            /* The write binds the document to `target` (DocumentEncoding.WriteConvertedToPath sets
               path), so the folder relative image links resolve against has changed. The rewrite
               above ran while the tab was still untitled, and a live session resolves against the
               folder it was last given: without this hand-over the pictures the move just placed
               stayed placeholders until the view was rebuilt for another reason (a reopen). Only a
               save that could pick a path (the first save, a Save As) runs it; an ordinary Ctrl+S
               keeps its path, and Update returns at once when the folder did not actually change. */
            if(saved&&(choose||movedPictures!=null))RefreshLiveDocumentPath();
            if(!saved)LastSaveFailure=T("문서가 저장되지 않았습니다.")+Environment.NewLine+T("저장이 취소되었거나 다른 작업 때문에 중단되었습니다.");
            return saved;
        }catch(SaveTargetInUseException ex){
            RestorePastedImagesAfterFailedSave(movedPictures);
            /* The picked Save As target is another tab's or window's file: refuse before touching
               it (UI bug hunt 4 item 2). The observer seam keeps a hidden run off the modal box. */
            LastSaveReported=true;Action<string> notice=SaveTargetInUseNotice;
            if(notice!=null){try{notice(ex.Message);}catch(Exception){}return false;}
            if(Program.IsSelfTest)throw;
            EnsureVisibleForeground();MessageBox.Show(this,ex.Message,"MDNote");return false;
        }catch(SaveConflictException ex){RestorePastedImagesAfterFailedSave(movedPictures);if(Program.IsSelfTest&&!ConflictAnswer.HasValue)throw;ConflictPrompts++;MessageBoxResult answer=ConflictAnswer.HasValue?ConflictAnswer.Value:MessageBoxResult.No;if(!ConflictAnswer.HasValue){EnsureVisibleForeground();answer=MessageBox.Show(this,ex.Message+"\n\n"+T("다른 이름으로 저장하시겠습니까?"),"MDNote",MessageBoxButton.YesNo,MessageBoxImage.Warning);}if(answer==MessageBoxResult.Yes){
            /* A retry that already carried the approval for this exact file and met the conflict
               again is a write the open queue could not approve: a format-changing conversion of a
               large document writes inside DrainFileOpens, which has no approved path. Retrying it
               would prompt for the same file forever, so this save stops and SaveFromUi shows the
               reason once. */
            if(approvedPath!=null&&String.Equals(System.IO.Path.GetFullPath(target),approvedPath,StringComparison.OrdinalIgnoreCase)){LastSaveFailure=ex.Message;return false;}
            retry=true;approved=documentFingerprintPath;ConflictRetrySawBusy=documentOperationBusy||fileOpenRunning;}
            else{LastSaveReported=true;return false;}
        }catch(EncoderFallbackException){
            RestorePastedImagesAfterFailedSave(movedPictures);
            if(Program.IsSelfTest)throw;
            LastSaveReported=true;MessageBox.Show(this,T("선택한 인코딩으로 저장할 수 없는 문자가 있습니다. UTF-8로 저장해 주세요."),"MDNote");return false;
        }catch(Exception ex){
            RestorePastedImagesAfterFailedSave(movedPictures);
            if(Program.IsSelfTest)throw;
            LastSaveReported=true;MessageBox.Show(this,T("저장할 수 없습니다.\n")+ex.Message,"MDNote");return false;
        }if(retry)return await RetrySaveOnDispatcher(approved);return false;}
    // Menu/toolbar/keyboard entry point: the handler returns at once and the save continues; the
    // overlay and documentOperationBusy keep a second save from starting while it runs.
    async void SaveFromUi(bool saveAs){
        try{
            bool saved=await SaveTaskOnDispatcher(saveAs);
            /* A format-changing save that Ctrl+N or the overlay's 취소 cancelled returns false with
               no box of its own; the document keeps its dirty mark, so say that it was NOT saved. */
            if(!saved&&!Program.IsSelfTest&&!LastSaveCancelled&&!LastSaveReported){EnsureVisibleForeground();MessageBox.Show(this,LastSaveFailure??T("저장할 수 없습니다.\n"),"MDNote");}
        }catch(Exception error){
            if(!Program.IsSelfTest){EnsureVisibleForeground();MessageBox.Show(this,T("저장할 수 없습니다.\n")+error.Message,"MDNote");}
        }
    }
    void ChooseFont(){using(var d=new Forms.FontDialog()){try{d.Font=new System.Drawing.Font(family,(float)(fontSize*72/96));}catch(ArgumentException){/* GDI+ refuses a family without a Regular face: open the dialog on its default */}if(d.ShowDialog()==Forms.DialogResult.OK){SetBaseFont(d.Font.Name,d.Font.SizeInPoints*96/72);}}}
    void ChooseColor(){using(var d=new Forms.ColorDialog()){if(d.ShowDialog()==Forms.DialogResult.OK){customColor=Color.FromRgb(d.Color.R,d.Color.G,d.Color.B);RenderPreservingView();}}}
    /* A keyboard command that opens a dialog, a popup or another control takes the keyboard away from
       the body, and AvalonEdit cancels the IME composition from its LostKeyboardFocus handler before
       the new surface exists (vendor ImeSupport.cs). The mouse and menu paths commit first
       (PreviewCommitComposition, CommitCompositionForMenu); these key paths do the same while the
       body still owns the keyboard, so the composing syllable lands in the document instead of
       disappearing. Probe seams: a headless run has no real keyboard focus or modifier state, so the
       two fields below stand in (FunctionKeyFocus and TableKeyModifiersProbe are the same pattern). */
    internal static Func<bool> KeyboardCommandFocus=null;
    internal static ModifierKeys? ShortcutModifiersProbe=null;
    void CommitCompositionForKeyboardCommand(){
        if(source==null||source.Visibility!=Visibility.Visible)return;
        bool focused=KeyboardCommandFocus!=null?KeyboardCommandFocus():source.TextArea.IsKeyboardFocusWithin;
        if(!focused)return;
        source.RefreshCompositionState("keyboard command");
        if(!source.ImeBusy&&!source.ImeCompositionActive())return;
        source.BeginCompositionCommit();
    }
    // A dialog that opens while a commit result is still counted would receive that result in its
    // own text box: a modal window owns the TextInput route, so this window's late-commit handler
    // never sees it and the body loses the syllable. Hold the dialog until the commit is in the
    // document; RunAfterComposition commits now and bounds the wait at its 500 ms limit.
    void RunAfterCompositionOrNow(Action action){
        SourceEditor owner=source;
        if(owner!=null&&owner.Visibility==Visibility.Visible&&owner.ImeBusy){owner.RunAfterComposition(action);return;}
        action();
    }
    // Find/replace/size fields keep their own clipboard, undo and character shortcuts; window commands still apply.
    static bool FieldOwnsKey(IInputElement focused,Key key){return focused is TextBox&&(key==Key.V||key==Key.Z||key==Key.Y||key==Key.B||key==Key.I||key==Key.K);}
    void Keys(object sender,KeyEventArgs e){long probe=BenchProbeStart();try{/* An IME-owned key (VK_PROCESSKEY) belongs to the composition: it must never run a window shortcut or ask for a commit, or the word is committed mid-reading and the raw letter lands in front of the pending syllable. */if(e.Key==Key.ImeProcessed)return;ModifierKeys modifiers=ShortcutModifiersProbe.HasValue?ShortcutModifiersProbe.Value:Keyboard.Modifiers;bool ctrl=(modifiers&ModifierKeys.Control)!=0;bool shift=(modifiers&ModifierKeys.Shift)!=0;if(ctrl){if(FieldOwnsKey(Keyboard.FocusedElement,e.Key))return;switch(e.Key){case Key.Z:RestoreEditorHistory(shift);break;case Key.Y:RestoreEditorHistory(true);break;case Key.C:if(!shift||!CopyReadable())return;break;case Key.N:CommitCompositionForKeyboardCommand();NewFile();break;case Key.T:CommitCompositionForKeyboardCommand();NewFile();break;case Key.W:if(activeDocument!=null){CommitCompositionForKeyboardCommand();CloseTab(activeDocument);}break;case Key.Tab:CommitCompositionForKeyboardCommand();CycleTab(shift?-1:1);break;case Key.O:CommitCompositionForKeyboardCommand();RunAfterCompositionOrNow(OpenDialog);break;case Key.S:SaveFromUi(shift);break;case Key.P:Print();break;case Key.E:Toggle();break;case Key.F:CommitCompositionForKeyboardCommand();ToggleSearch(false);break;case Key.H:CommitCompositionForKeyboardCommand();ToggleSearch(true);break;case Key.V:PasteClipboard(shift);break;case Key.K:CommitCompositionForKeyboardCommand();RunAfterCompositionOrNow(InsertLink);break;case Key.B:ToggleFormat("bold");break;case Key.I:if(shift){CommitCompositionForKeyboardCommand();RunAfterCompositionOrNow(InsertImage);}else ToggleFormat("italic");break;case Key.Add:case Key.OemPlus:Zoom(2);break;case Key.Subtract:case Key.OemMinus:Zoom(-2);break;case Key.D0:case Key.NumPad0:SetBaseFont(family,16);break;default:return;}e.Handled=true;}else if(e.Key==Key.Escape&&search.Visibility==Visibility.Visible){CloseSearch();e.Handled=true;}}finally{BenchProbeEnd("key",probe);}}
}
}


