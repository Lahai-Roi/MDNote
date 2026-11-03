using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
namespace MDNote {
public partial class MainWindow {
    sealed class DocumentTab {internal DocumentHistory History=new DocumentHistory();internal string RecoveryId=Guid.NewGuid().ToString("N"),RecoveryName,RecoveryOriginalPath;internal object RecoveryVersion;internal bool Recovered;internal string Path="",Text="",Saved="",Fingerprint,FingerprintPath;internal bool? TextMode;internal bool Editing,Preview=false;internal Encoding Encoding=new UTF8Encoding(false,true);internal string Newline="\n";internal TabItem Item;internal SourceEditor Source;internal byte[] OriginalBytes;internal EventHandler SourceLayout;internal string ViewSignature;internal SearchHighlight SourceHighlight;
        // The tab's dirty state as it stood when CaptureTab last wrote Text/Saved, and the caption
        // last painted for it. Both let the every-keystroke caption pass stay O(1) per tab: a
        // background tab cannot be edited (only the active document is), so its cached dirty flag
        // stays right until it becomes active again, and a caption that reads the same is not
        // repainted at all.
        internal bool Dirty;internal string Caption;
        // The tooltip last put on the tab (the whole path): a file name alone cannot tell two
        // same-named files in different folders apart (bughunt4 item 9).
        internal string CaptionTip;
        }
    readonly TabControl documentTabs=new TabControl {Height=31,BorderThickness=new Thickness(0),Padding=new Thickness(0)};
    readonly List<DocumentTab> documents=new List<DocumentTab>();
    Encoding documentEncoding=new UTF8Encoding(false,true);string documentNewline="\n";
    // Only mixed-newline loads need a second, byte-exact representation for a clean save.
    byte[] documentOriginalBytes;
    bool surfacesReady;
    string ViewSignature {get{return fontSize+"|"+family+"|"+dark+"|"+customColor+"|"+linePercent;}}
    MenuItem openTabsOption;
    DocumentTab activeDocument;bool switchingTabs,openInTabs=true;
    internal bool ContainsFile(string file){string full=System.IO.Path.GetFullPath(file);foreach(var document in documents)if(String.Equals(document==activeDocument?path:document.Path,full,StringComparison.OrdinalIgnoreCase))return true;return String.Equals(path,full,StringComparison.OrdinalIgnoreCase);}
    /* A path forwarded by Explorer or a second instance belongs to the window that already holds
       it: opening it here as well left two windows editing one file, so the later save was refused
       as a conflict. A window whose close was approved (or that already closed) must not receive a
       file it is about to drop. */
    internal bool CanTakeForwardedFile {get{return !placementClosed&&!closeApproved;}}
    internal static MainWindow WindowWithFile(Application app,string file){
        if(app==null)return null;
        string full;
        try{full=System.IO.Path.GetFullPath(file);}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}catch(System.IO.PathTooLongException){return null;}catch(System.Security.SecurityException){return null;}
        foreach(Window candidate in app.Windows){var editor=candidate as MainWindow;if(editor==null||!editor.CanTakeForwardedFile)continue;if(editor.ContainsFile(full))return editor;}
        return null;
    }
    internal bool ActivateOpenedFile(string file){
        string full;
        try{full=System.IO.Path.GetFullPath(file);}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(System.IO.PathTooLongException){return false;}catch(System.Security.SecurityException){return false;}
        if(!ActivateOpenFile(full))return false;
        EnsureVisibleForeground();
        return true;
    }
    /* File > Open, a recent entry and a drop land in LoadFile, which only looked at this window.
       A file another window already holds belongs to that window (DispatchFiles follows the same
       rule); opening it here as well left two windows editing one document until the later save
       hit the conflict prompt. The window that holds it brings its tab forward. */
    internal bool ActivateFileInOtherWindow(string file){
        /* Only an open whose window is already on screen routes to another window: a new window
           that is still being prepared (a link's window) loads into itself as before. */
        if(!IsLoaded)return false;
        MainWindow owner=WindowWithFile(Application.Current,file);
        if(owner==null||Object.ReferenceEquals(owner,this))return false;
        return owner.ActivateOpenedFile(file);
    }
    internal int OpenDocumentCount {get{return documents.Count;}}
    internal void ConfigureActivationTest(bool tabs){if(!Program.IsSelfTest)throw new InvalidOperationException();openInTabs=tabs;documentTabs.Visibility=tabs?Visibility.Visible:Visibility.Collapsed;}
    void BuildTabs(MenuItem view){
        DockPanel.SetDock(documentTabs,Dock.Top);root.Children.Insert(1,documentTabs);documentTabs.SetResourceReference(Control.BackgroundProperty,"Ribbon");
        documentTabs.SelectionChanged+=delegate{if(!switchingTabs&&documentTabs.SelectedItem!=null)SwitchTab((DocumentTab)((TabItem)documentTabs.SelectedItem).Tag);};
        /* Many tabs used to be clipped with no way to reach the hidden ones: the strip's bar is
           hidden, nothing scrolled on the wheel and a selected tab was never brought into view.
           The wheel over the strip scrolls it sideways and SwitchTab reveals the selected tab; the
           bar stays hidden so the 31 px strip keeps its look. */
        documentTabs.PreviewMouseWheel+=delegate(object sender,System.Windows.Input.MouseWheelEventArgs args){
            var scroll=ScrollViewerIn(documentTabs);if(scroll==null||scroll.ScrollableWidth<=0.5)return;
            scroll.ScrollToHorizontalOffset(Math.Max(0,Math.Min(scroll.ScrollableWidth,scroll.HorizontalOffset-args.Delta)));
            args.Handled=true;
        };
        var option=openTabsOption=new MenuItem {Header="새 탭으로 보기",IsCheckable=true,IsChecked=openInTabs};option.Click+=delegate{SetOpenInTabs(option.IsChecked);};view.Items.Insert(0,option);view.Items.Insert(1,new Separator());documentTabs.Visibility=openInTabs?Visibility.Visible:Visibility.Collapsed;CreateTab();
    }
    void SetOpenInTabs(bool value){
        openInTabs=value;if(openTabsOption!=null)openTabsOption.IsChecked=value;SavePreferences();documentTabs.Visibility=value?Visibility.Visible:Visibility.Collapsed;if(value||documents.Count<2)return;CaptureTab();var otherDocuments=new List<DocumentTab>(documents);otherDocuments.Remove(activeDocument);switchingTabs=true;try{foreach(var state in otherDocuments){
                /* The new window re-renders the document, which puts its caret back on offset 0: read the
                   tab's own caret and scroll here, before its surface is released (bughunt4 item 10). */
                int movedCaret=0;double movedTop=0;bool keepView=state.Source!=null&&state.Source.Document!=null;
                if(keepView){movedCaret=Math.Max(0,Math.Min(state.Source.CaretIndex,state.Source.Document.TextLength));movedTop=state.Source.VerticalOffset;}
                var window=new MainWindow();window.SetOpenInTabs(false);window.textMode=state.TextMode;window.path=state.Path;window.text=state.Text;window.saved=state.Saved;window.recoveredDocument=state.Recovered;window.documentFingerprint=state.Fingerprint;window.documentFingerprintPath=state.FingerprintPath;window.documentEncoding=state.Encoding;window.documentNewline=state.Newline;window.documentOriginalBytes=state.OriginalBytes;window.editing=state.Editing;window.loading=true;window.preview.IsChecked=state.Preview;window.loading=false;window.activeDocument.History=state.History;window.activeDocument.RecoveryId=state.RecoveryId;window.activeDocument.RecoveryName=state.RecoveryName;window.activeDocument.RecoveryOriginalPath=state.RecoveryOriginalPath;window.activeDocument.RecoveryVersion=state.RecoveryVersion;window.Render();if(keepView)window.RestoreMovedTabView(movedCaret,movedTop);window.ribbonHost.Visibility=window.editing?Visibility.Visible:Visibility.Collapsed;window.LayoutMode();window.UpdateEditCaption();window.CaptureTab();window.Show();ReleaseTabSurfaces(state);documents.Remove(state);documentTabs.Items.Remove(state.Item);}}finally{switchingTabs=false;}Activate();
    }
    /* Restores the caret and the top of the view a tab had before it was moved into this window.
       The scroll can only stick once the window has been laid out (before that the extent is empty
       and the offset is clamped to 0), so it is applied again from Loaded - and only while the
       caret still sits where it was put, so a click in the meantime is not undone. */
    void RestoreMovedTabView(int caret,double top){
        if(source==null||source.Document==null)return;
        ApplyMovedTabView(caret,top);
        var document=activeDocument;int placed=source.CaretIndex;
        /* The window is shown after this call, so the scroll is repeated once the extent exists
           (Loaded) and once more after the first render/drain passes (Background). */
        RestoreMovedTabViewLater(document,placed,caret,top,System.Windows.Threading.DispatcherPriority.Loaded);
        RestoreMovedTabViewLater(document,placed,caret,top,System.Windows.Threading.DispatcherPriority.Background);
    }
    void RestoreMovedTabViewLater(DocumentTab document,int placed,int caret,double top,System.Windows.Threading.DispatcherPriority priority){
        Dispatcher.BeginInvoke(priority,new Action(delegate{
            if(!Object.ReferenceEquals(document,activeDocument)||source==null||source.Document==null)return;
            if(source.CaretIndex!=placed)return;
            ApplyMovedTabView(caret,top);
        }));
    }
    void ApplyMovedTabView(int caret,double top){
        int offset=Math.Max(0,Math.Min(caret,source.Document.TextLength));
        source.Select(offset,0);
        source.ScrollToVerticalOffset(Math.Max(0,top));
    }
    static string NormalizeLineEndings(string value,string newline){
        /* An LF document with no CR is already normal: return the same instance (no 2 x size copy on open,
           and callers' buffer==value checks stay O(1)). */
        if(newline=="\n"&&value.IndexOf('\r')<0)return value;
        return value.Replace("\r\n","\n").Replace("\r","\n").Replace("\n",newline);
    }
    /* One newline rule for every open. Line endings are counted, not probed by "contains": a single
       stray CR in an LF or CRLF document (an old-Mac paste, a terminal progress line) used to
       select CR for the whole document and the next save rewrote every line ending as CR. CRLF
       wins a tie, and CR is chosen only when the text has no LF at all. */
    static string DetectNewline(string value){
        if(value.IndexOf('\r')<0)return "\n";
        int crlf=0,lf=0,cr=0;
        for(int i=0;i<value.Length;i++){
            char c=value[i];
            if(c=='\r'){if(i+1<value.Length&&value[i+1]=='\n'){crlf++;i++;}else cr++;}
            else if(c=='\n')lf++;
        }
        if(crlf==0&&lf==0)return cr>0?"\r":"\n";
        return crlf>=lf?"\r\n":"\n";
    }
    internal static Action<string> AtomicWriteObserver=null;
    static void WriteDocumentAtomic(string target,string value,Encoding encoding,byte[] originalBytes=null){string full=System.IO.Path.GetFullPath(target);string temp=System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full),".mdnote-"+Guid.NewGuid().ToString("N")+".tmp");try{using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,FileOptions.SequentialScan)){if(originalBytes!=null){stream.Write(originalBytes,0,originalBytes.Length);stream.Flush(true);}else using(var output=new StreamWriter(stream,encoding,16384,true)){output.Write(value);output.Flush();stream.Flush(true);}}if(Program.IsSelfTest&&AtomicWriteObserver!=null)AtomicWriteObserver("durable-temp");if(File.Exists(full))File.Replace(temp,full,null);else File.Move(temp,full);if(Program.IsSelfTest&&AtomicWriteObserver!=null)AtomicWriteObserver("committed");}finally{try{if(File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}}
    void CaptureTab(){if(activeDocument==null)return;if(surfacesReady){activeDocument.SourceHighlight=sourceHighlight;activeDocument.Source=source;activeDocument.ViewSignature=ViewSignature;}activeDocument.Recovered=recoveredDocument;activeDocument.Fingerprint=documentFingerprint;activeDocument.FingerprintPath=documentFingerprintPath;activeDocument.Encoding=documentEncoding;activeDocument.Newline=documentNewline;activeDocument.OriginalBytes=documentOriginalBytes;activeDocument.TextMode=textMode;activeDocument.Path=path;activeDocument.Text=text;activeDocument.Saved=saved;activeDocument.Dirty=HasUnsavedChanges;activeDocument.Editing=editing;activeDocument.Preview=preview.IsChecked==true;}
    void RefreshTabCaption(){foreach(var state in documents)RefreshTabCaption(state);}
    /* A language switch re-captions every tab, not only the active one: a background tab keeps its
       captured path/text, while the active tab's live fields are the truth until CaptureTab.
       UpdateTitle runs from the source's TextChanged on every keystroke, so the background tabs must
       not compare their captured Text and Saved here: that comparison walks the common prefix of two
       strings, and a large document whose lengths still match would make every keystroke O(chars).
       The dirty flag captured with the text (DocumentTab.Dirty) answers the same question in O(1),
       and is exact because only the active document can be edited. The caption is only assigned when
       it actually changed, so a keystroke does not invalidate every tab header's measure. */
    void RefreshTabCaption(DocumentTab state){if(state==null)return;var header=state.Item.Header as StackPanel;if(header==null)return;bool active=Object.ReferenceEquals(state,activeDocument);bool dirty=active?HasUnsavedChanges:state.Dirty;string target=active?path:state.Path;string name=target==""?(state.RecoveryName??T("새 문서")):target;string caption=(dirty?"• ":"")+(target==""?name:System.IO.Path.GetFileName(target));
        /* The caption keeps the file name only; hovering the tab names the whole path, so two files
           of the same name from different folders can be told apart (bughunt4 item 9). */
        if(!String.Equals(name,state.CaptionTip,StringComparison.Ordinal)){state.CaptionTip=name;state.Item.ToolTip=name;((TextBlock)header.Children[0]).ToolTip=name;}
        if(String.Equals(caption,state.Caption,StringComparison.Ordinal))return;state.Caption=caption;((TextBlock)header.Children[0]).Text=caption;}
    void CreateTab(){
        /* The new tab detaches the editor that may still be composing (the Ctrl+N/Ctrl+T path). */
        CommitCompositionBeforeViewChange();
        CaptureTab();var state=new DocumentTab {Editing=true,TextMode=newDocumentsText?(bool?)true:null};var item=new TabItem {Tag=state};state.Item=item;var header=new StackPanel {Orientation=Orientation.Horizontal};header.Children.Add(new TextBlock {Text=T("새 문서"),VerticalAlignment=VerticalAlignment.Center,MaxWidth=180,TextTrimming=TextTrimming.CharacterEllipsis});var close=new Button {Content="×",Padding=new Thickness(3,0,3,0),MinHeight=18,Margin=new Thickness(9,0,0,0),Focusable=false,ToolTip=T("탭 닫기 (Ctrl+W)")};close.Click+=delegate{CloseTab(state);};header.Children.Add(close);item.Header=header;documents.Add(state);/* A window that ends up holding a second document always shows the strip. "새 탭으로 보기" decides whether an open makes a new window or a new tab; it never decides that a tab the user cannot see or click is acceptable (the help document opened that way). */if(documents.Count>1&&documentTabs.Visibility!=Visibility.Visible)documentTabs.Visibility=Visibility.Visible;switchingTabs=true;documentTabs.Items.Add(item);documentTabs.SelectedItem=item;switchingTabs=false;SwitchTab(state);
    }
    // SearchCacheReset drops the map/matches/selection but keeps the document references, and the
    // cache outlived a tab switch or close until the next search (a multi-megabyte FlowDocument /
    // TextDocument stayed reachable). Search.cs is owned elsewhere in this round, so the three
    // references are dropped here, next to the reset that should own them.
    void DropSearchCache(){SearchCacheReset();searchCacheSource=null;searchCacheVersion=null;}
    void SwitchTab(DocumentTab state){
        /* The previous editor leaves the tree below and AvalonEdit cancels its IME composition from
           the resulting focus loss: finish the composition before anything captures or detaches the
           tab, the way every other surface change does (SwitchSurface/SetMode/SetLiveMode). */
        CommitCompositionBeforeViewChange();
        if(state==activeDocument)return;
        FlushDocumentChanges();
        CancelEditorRefresh();CaptureTab();CancelParallelSearch();DropSearchCache();activeDocument=state;loading=true;
        /* A tab that keeps its own editor already holds its text (only the active document is
           edited), so the render below must not write it again: AvalonEdit's Text setter puts the
           caret back on offset 0 and drops the selection, which lost the reading position of every
           tab whenever a view change in another tab made its stored signature stale. */
        bool reused=surfacesReady&&state.Source!=null;sourceHighlight=state.SourceHighlight;bool cached=reused&&state.ViewSignature==ViewSignature;
        if(surfacesReady){area.Children.Clear();if(state.Source!=null){source=state.Source;area.Children.Add(source);}else{source=new SourceEditor();SetupSurfaces();SetupTableKeys(source);}}
        /* A file open or conversion keeps its overlay (label, progress bar, 취소) in `area` while the
           window is busy. The clear above took it out with the previous surface, which left the
           progress and the only cancel button invisible for the rest of the load; put it back above
           the tab that is shown now. */
        if(fileOpenOverlay!=null&&documentOperationBusy&&!area.Children.Contains(fileOpenOverlay))area.Children.Add(fileOpenOverlay);
        recoveredDocument=state.Recovered;documentFingerprint=state.Fingerprint;documentFingerprintPath=state.FingerprintPath;documentEncoding=state.Encoding;documentNewline=state.Newline;documentOriginalBytes=state.OriginalBytes;path=state.Path;textMode=state.TextMode;text=state.Text;saved=state.Saved;editing=state.Editing;preview.IsChecked=state.Preview;loading=false;
        Theme();if(!cached)RenderSurface(!reused);else{ApplyWrap();UpdateTitle();}ribbonHost.Visibility=editing?Visibility.Visible:Visibility.Collapsed;editButton.Tag=editing?"Active":null;UpdateEditCaption();LayoutMode();/* LayoutMode attaches the new tab's live session; a tool refresh before that returns early while detached and leaves the previous tab's B/heading state lit. */UpdateTools();RefreshOutline();
        /* b53 #12: the search cache listener is bound to a viewer/source instance (Search.cs). This swap put a
           different pair in the fields, so the listener has to move with them or the count stops following edits. */
        SyncSearchInvalidation();
        /* The strip scrolls sideways now (BuildTabs), so the tab that just became active has to be
           revealed even when it sits past the right edge. A tab added in this very frame has no
           layout yet, so its first request can be empty: ask once more after the layout settles,
           and only while it is still the active tab. */
        if(state.Item!=null){
            state.Item.BringIntoView();
            if(state.Item.ActualWidth<=0){var pending=state;System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{if(Object.ReferenceEquals(pending,activeDocument)&&pending.Item!=null)pending.Item.BringIntoView();}));}
        }
        FocusActiveSource();
    }
    /* A tab switch takes the editor the keyboard focus was in out of the tree, so the surface that
       becomes active takes the focus: typing right after a tab click, Ctrl+Tab, Ctrl+W, a new tab or
       the tab close button otherwise went nowhere until the body was clicked (user, 2026-10-31).
       The find/replace fields and the size box keep the focus when they own it, so a search that
       spans tabs keeps working. */
    void FocusActiveSource(){
        if(source==null)return;
        if(query.IsKeyboardFocusWithin||replacement.IsKeyboardFocusWithin||sizes.IsKeyboardFocusWithin)return;
        source.Focus();
    }
    /* The strip's ScrollViewer lives in the TabControl template (Ui.Styles), so it is found by
       walking the visual tree rather than a template part name. */
    static ScrollViewer ScrollViewerIn(DependencyObject root){
        var scroll=root as ScrollViewer;if(scroll!=null)return scroll;
        int count=root==null?-1:System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for(int i=0;i<count;i++){var found=ScrollViewerIn(System.Windows.Media.VisualTreeHelper.GetChild(root,i));if(found!=null)return found;}
        return null;
    }
    void CycleTab(int direction){/* Ctrl+Tab/Ctrl+Shift+Tab: the target tab replaces this surface below. */CommitCompositionBeforeViewChange();if(documents.Count<2)return;int index=documents.IndexOf(activeDocument);index=(index+direction+documents.Count)%documents.Count;switchingTabs=true;documentTabs.SelectedItem=documents[index].Item;switchingTabs=false;SwitchTab(documents[index]);}
    static void ReleaseHighlight(SearchHighlight highlight){if(highlight==null)return;var layer=System.Windows.Documents.AdornerLayer.GetAdornerLayer(highlight.AdornedElement);if(layer!=null){var adorners=layer.GetAdorners(highlight.AdornedElement);if(adorners!=null&&Array.IndexOf(adorners,highlight)>=0)layer.Remove(highlight);}highlight.Clear();}
    void ReleaseTabSurfaces(DocumentTab state){
        var oldSource=state.Source??(state==activeDocument?source:null);
        /* A closed tab's editor stayed in StructuralInput's table-key set after its surface left
           the tree. Drop its Ctrl+[ / Ctrl+] hook and the set entry before it is detached. */
        if(oldSource!=null)ForgetTableKeySource(oldSource);
        if(oldSource!=null&&state.SourceLayout!=null)oldSource.LayoutUpdated-=state.SourceLayout;
        ReleaseHighlight(state.SourceHighlight);
        bool previous=loading;loading=true;try{if(oldSource!=null)oldSource.Document=new ICSharpCode.AvalonEdit.Document.TextDocument();}finally{loading=previous;}
        state.Source=null;state.SourceLayout=null;state.SourceHighlight=null;state.Text=state.Saved="";state.History=new DocumentHistory();state.OriginalBytes=null;state.RecoveryVersion=null;
        /* The closed document's FlowDocument/TextDocument must not stay reachable through the search cache. */
        DropSearchCache();
    }
    void CloseTab(DocumentTab state){
        /* Ctrl+W and the tab's × unlock this editor: a live composition belongs in the document
           first, and the save prompt below must read the committed text. */
        CommitCompositionBeforeViewChange();
        var previous=activeDocument;switchingTabs=true;documentTabs.SelectedItem=state.Item;switchingTabs=false;SwitchTab(state);if(!Confirm()){switchingTabs=true;documentTabs.SelectedItem=previous.Item;switchingTabs=false;SwitchTab(previous);/* SwitchTab returns at once for the tab that is still active, so the Cancel path must put the keyboard back in the body itself. */FocusActiveSource();return;}
        /* SwitchTab returns at once when the tab was already active, so the live surface and its
           find-highlight adorner were never written to the tab. Without this capture the adorner
           stayed on the window's AdornerLayer after its editor left the tree, and ReleaseTabSurfaces
           below had nothing of the active tab's own to release (user, 2026-10-31). */
        CaptureTab();
        ForgetRecovery(state);CancelEditorRefresh();
        int index=documents.IndexOf(state);ReleaseTabSurfaces(state);switchingTabs=true;documents.Remove(state);documentTabs.Items.Remove(state.Item);activeDocument=null;switchingTabs=false;
        if(documents.Count==0){path="";text="";saved="";CreateTab();}
        else{
            /* Closing a background tab returns to the tab the user was working in; closing the
               active tab activates the tab that took its index (its next neighbour), or the
               previous one when it was last. The old code always jumped to the last tab. */
            DocumentTab next;
            if(previous!=null&&!Object.ReferenceEquals(previous,state)&&documents.Contains(previous))next=previous;
            else next=documents[Math.Min(Math.Max(index,0),documents.Count-1)];
            switchingTabs=true;documentTabs.SelectedItem=next.Item;switchingTabs=false;SwitchTab(next);
        }
        /* Back to one document with the option off: the strip returns to the single-document state instead of staying up from the 2+ rule. */if(!openInTabs&&documents.Count<2&&documentTabs.Visibility!=Visibility.Collapsed)documentTabs.Visibility=Visibility.Collapsed;
    }
    bool ConfirmTabs(){CaptureTab();var previous=activeDocument;try{foreach(var state in new List<DocumentTab>(documents)){/* b50: iterate a copy, a forwarded open can add a tab while the prompt runs. b49: only dirty tabs need a prompt; switching (and re-rendering) clean or already active tabs delayed the close prompt */bool current=Object.ReferenceEquals(state,activeDocument);if(!current&&state.Text==state.Saved&&!state.Recovered)continue;if(!current){switchingTabs=true;documentTabs.SelectedItem=state.Item;switchingTabs=false;SwitchTab(state);}if(!Confirm())return false;}return true;}finally{/* Cancel (or a failed save) must not leave another tab showing; same restore as ConfirmTabsMinimized. */if(previous!=null&&documents.Contains(previous)&&!Object.ReferenceEquals(activeDocument,previous)){switchingTabs=true;documentTabs.SelectedItem=previous.Item;switchingTabs=false;SwitchTab(previous);}}}
    readonly List<string> recentFiles=new List<string>();MenuItem recentMenu;
    void BuildRecent(MenuItem file){recentMenu=new MenuItem {Header="최근 파일"};file.Items.Insert(2,recentMenu);RefreshRecent();}
    void RefreshRecent(){if(recentMenu==null)return;recentMenu.Items.Clear();foreach(string entry in recentFiles){string name=entry;var item=new MenuItem {Header=new TextBlock {Text=System.IO.Path.GetFileName(name)},ToolTip=name};item.Click+=delegate{LoadFile(name);};recentMenu.Items.Add(item);}recentMenu.IsEnabled=recentFiles.Count>0;}
    void RememberRecent(string file){recentFiles.RemoveAll(delegate(string item){return String.Equals(item,file,StringComparison.OrdinalIgnoreCase);});recentFiles.Insert(0,file);if(recentFiles.Count>10)recentFiles.RemoveRange(10,recentFiles.Count-10);RefreshRecent();SavePreferences();}
    void InsertImage(){LiveImage();}
    // Explorer drop seen by the window (the source consumes its own drops in SourcePreviewDrop).
    // A TXT document answers with the notice; an all-image drop on a Markdown document is the
    // document's own insert at the caret, read mode included, so the window never hands a PNG to
    // the text decoder. A mixed drop (an image plus a .md/.txt) keeps the open-as-document answer
    // for every file and returns false.
    internal bool AcceptImageDrop(string[] files,Point? viewerPoint){
        try{
            if(!MarkdownCommands.AllImageFiles(files))return false;
            if(PlainText){ShowImageTxtNotice();return true;}
            // Names that are gone insert nothing (the source surface drops them the same way), but
            // the image-only drop is still consumed: LoadFile must never read a PNG as text.
            string[] present=MarkdownCommands.ExistingImageFiles(files);
            if(present.Length==0)return true;
            return InsertImageFilesAtCaret(present);
        }catch(Exception ex){
            System.Diagnostics.Trace.TraceError("MDNote image drop: {0}: {1}",ex.GetType().Name,ex.Message);return false;
        }
    }
    // The source surface inserts an Explorer image drop at the pointer (AcceptSourceImageDrop); a
    // drop the text view never sees (status bar, tab strip, ribbon, margins) lands at the caret
    // instead. Read mode turns editing on through the same Ctrl+E entry point, and the whole drop
    // is one history entry (MarkdownCommands.InsertImages).
    bool InsertImageFilesAtCaret(string[] files){
        /* An open or save holds the document busy and read-only. The TXT notice used to answer the
           drop here even on a Markdown document; say what is really wrong instead. */
        if(documentOperationBusy){ShowImageBusyNotice();return true;}
        if(source==null||source.Visibility!=Visibility.Visible){ShowImageTxtNotice();return true;}
        if(!editing){
            // The Ctrl+E entry point, not SetMode: it flushes the pipeline the same way the menu
            // does. A busy document stays read-only, so nothing can be inserted there.
            Toggle();
            if(!editing){ShowImageTxtNotice();return true;}
        }
        if(documentOperationBusy){ShowImageBusyNotice();return true;}
        /* A picture outside the document's own picture folder is staged in the shared temp folder
           first (PasteSupport.cs ImageFilesForInsert); the save moves it into <docname>\. */
        files=ImageFilesForInsert(files);
        var references=new List<string>();
        var alts=new List<string>();
        foreach(string file in files){references.Add(ImageLinkReference(file));alts.Add(System.IO.Path.GetFileNameWithoutExtension(file));}
        var owner=source;var document=owner.Document;
        try{
            owner.RunAfterComposition(delegate{
                if(!Object.ReferenceEquals(owner,source)||!Object.ReferenceEquals(document,source.Document))return;
                try{
                    FlushDocumentChanges();
                    int offset=Math.Max(0,Math.Min(source.CaretIndex,source.Document.TextLength));
                    CommandResult result=MarkdownCommands.InsertImages(source.Document,offset,references,alts);
                    if(result!=null){ApplyCommandResult(result);source.Focus();}
                }catch(Exception ex){ReportCommandFailure(ex);}
            });
        }catch(Exception ex){ReportCommandFailure(ex);return false;}
        return true;
    }
    // Probes swap in an observer so the notice never opens a modal box in a headless run.
    internal static Action<string> ImageDropNotice=null;
    void ShowImageTxtNotice(){
        string message=T("이미지는 MD 문서에서만 넣을 수 있습니다.");
        var observer=ImageDropNotice;
        if(observer!=null){try{observer(message);}catch(Exception){}return;}
        try{MessageBox.Show(this,message,"MDNote");}catch(Exception){}
    }
    /* The same observer seam as ShowImageTxtNotice, so a probe can tell the two answers apart. */
    void ShowImageBusyNotice(){
        string message=T("지금은 이미지를 넣을 수 없습니다. 파일을 여는/저장하는 작업이 끝난 뒤 다시 시도해 주세요.");
        var observer=ImageDropNotice;
        if(observer!=null){try{observer(message);}catch(Exception){}return;}
        try{MessageBox.Show(this,message,"MDNote");}catch(Exception){}
    }
    void ShowAbout(){var dialog=new Window {Owner=this,Title=T("MDNote 정보"),Width=430,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,Background=(System.Windows.Media.Brush)Resources["Paper"],Foreground=(System.Windows.Media.Brush)Resources["Ink"],FontFamily=FontFamily,FontSize=13};dialog.Resources.MergedDictionaries.Add(Resources);var panel=new StackPanel {Margin=new Thickness(24)};dialog.Content=panel;panel.Children.Add(new TextBlock {Text="MDNote",FontSize=24,FontWeight=FontWeights.SemiBold});panel.Children.Add(new TextBlock {Text=((System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(MainWindow).Assembly,typeof(System.Reflection.AssemblyInformationalVersionAttribute))).InformationalVersion,Margin=new Thickness(0,3,0,12)});panel.Children.Add(new TextBlock {Text=T("가벼운 Markdown / TXT 편집기"),Margin=new Thickness(0,0,0,18)});panel.Children.Add(new TextBlock {Text="Copyright (c) 2026 Lahai-Roi(shs7034)\nGNU General Public License v3.0",Margin=new Thickness(0,0,0,6)});/* 2.0: project page link */var repoLink=new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("github.com/Lahai-Roi/MDNote")) {Foreground=(System.Windows.Media.Brush)Resources["Accent"]};repoLink.Click+=delegate{try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Lahai-Roi/MDNote") {UseShellExecute=true});}catch(Exception){}};var repoText=new TextBlock {Margin=new Thickness(0,0,0,18)};repoText.Inlines.Add(repoLink);panel.Children.Add(repoText);var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(buttons);var license=new Button {Content=T("라이선스 보기")};license.Click+=delegate{ShowLicense(dialog);};buttons.Children.Add(license);var usage=new Button {Content=T("MDNote 사용법")};usage.Click+=delegate{dialog.Close();OpenAppUsage();};buttons.Children.Add(usage);var okay=new Button {Content=T("확인"),IsDefault=true,IsCancel=true,MinWidth=64};okay.Click+=delegate{dialog.Close();};buttons.Children.Add(okay);dialog.ShowDialog();}
    void ShowLicense(Window owner){string file=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"LICENSE");if(!File.Exists(file))return;var dialog=new Window {Owner=owner,Title="GNU GPL v3 — MDNote",Width=700,Height=520,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=(System.Windows.Media.Brush)Resources["Paper"],Foreground=(System.Windows.Media.Brush)Resources["Ink"]};/* b56: Esc closes the license text like the link and custom-table prompts (bughunt-ui 27). */dialog.PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Escape){dialog.Close();e.Handled=true;}};var box=new TextBox {Text=File.ReadAllText(file),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(20),Background=dialog.Background,Foreground=dialog.Foreground,BorderThickness=new Thickness(0)};dialog.Content=box;dialog.ShowDialog();}
    void OpenAppUsage(){string name=language=="ko"?"MDNote 사용법.md":"MDNote-guide."+language+".md";LoadFile(HelpFile(name));}
    void OpenUsage(){string name=language=="ko"?"마크다운 작성 예제.md":"example."+language+".md";LoadFile(HelpFile(name));}
    /* 2.0: the help documents and their pictures live in a help folder next to the exe; a copy without it keeps them beside the exe. */
    static string HelpFile(string name){string folder=AppDomain.CurrentDomain.BaseDirectory;string inHelp=System.IO.Path.Combine(System.IO.Path.Combine(folder,"help"),name);return System.IO.File.Exists(inHelp)?inHelp:System.IO.Path.Combine(folder,name);}
    public void LoadFile(string name){if(!Program.IsSelfTest){if(ActivateFileInOtherWindow(name))return;QueueFileOpen(name);return;}LoadFileSynchronously(name);}
    void LoadFileSynchronously(string name){
        /* A dropped or opened file becomes a tab (or replaces this blank one) below: finish a live
           composition while its editor still owns this tab, before CaptureTab/LoadFileCore run. */
        CommitCompositionBeforeViewChange();
        string full;try{full=System.IO.Path.GetFullPath(name);}catch(Exception ex){MessageBox.Show(this,ex.Message,"MDNote");return;}
        if(!File.Exists(full)){MessageBox.Show(this,T("파일을 찾을 수 없습니다.\n")+full,"MDNote");return;}
        foreach(var state in documents)if(String.Equals(state==activeDocument?path:state.Path,full,StringComparison.OrdinalIgnoreCase)){switchingTabs=true;documentTabs.SelectedItem=state.Item;switchingTabs=false;SwitchTab(state);return;}
        bool blank=path==""&&text==""&&saved==""&&!recoveredDocument;
        if(!blank&&!openInTabs){var other=new MainWindow();other.SetOpenInTabs(false);other.LoadFile(full);other.Show();other.EnsureVisibleForeground();return;}
        if(!blank)CreateTab();LoadFileCore(full);if(String.Equals(path,full,StringComparison.OrdinalIgnoreCase))RememberRecent(full);CaptureTab();RefreshTabCaption();RefreshOutline();
    }
}
}

