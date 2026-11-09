using System;
using System.IO;
namespace MDNote {
// Markdown and TXT share one AvalonEdit buffer. The raw-source toggle only detaches the
// preview; Basic/Live view modes control caret-line markup without changing engines.
public partial class MainWindow {
    MarkdownLivePreview.Session livePreview;
    int SurfaceLength {get{return source==null||source.Document==null?0:source.Document.TextLength;}}
    bool LivePreviewWanted {get{return !PlainText&&!(editing&&preview.IsChecked==true);}}
    bool LivePreviewAttached {get{return livePreview!=null&&!livePreview.IsDisposed&&source!=null&&livePreview.Editor==source&&livePreview.Document==source.Document;}}
    // The path relative image and link references resolve against. A recovered draft has path=""
    // until its first save, but its references were written next to the file it came from, so they
    // resolve against that file's folder - the same path the save dialog defaults to.
    string DocumentBasePath(){
        if(path.Length==0&&recoveredDocument&&activeDocument!=null){
            string original=activeDocument.RecoveryOriginalPath;
            if(!String.IsNullOrEmpty(original))return original;
        }
        return path;
    }
    MarkdownLivePreview.Settings LiveSettings(){
        string directory=null;
        string basePath=DocumentBasePath();
        if(!String.IsNullOrEmpty(basePath)){try{directory=Path.GetDirectoryName(Path.GetFullPath(basePath));}catch(ArgumentException){}catch(NotSupportedException){}catch(PathTooLongException){}}
        return new MarkdownLivePreview.Settings {FontSize=fontSize,Family=family,LinePercent=linePercent,Dark=dark,TextColor=customColor,WordWrap=wordWrap,BaseDirectory=directory,ReadOnly=!editing||documentOperationBusy,Busy=documentOperationBusy,HideMarkers=hideMarkers||MarkdownLivePreview.Settings.HideMarkersFromEnvironment(),HeadingMarks=headingMarkStyle!=2,HeadingHash=headingMarkStyle==1,Translate=T,Notice=ShowStatusNotice,
            // The in-document [TOC] reads the outline's published heading list; a parsed TOC
            // line asks for a scan (debounced, and skipped while the list is current).
            Toc=tocData,TocRequested=delegate{if(source!=null)EnsureTocHeadingsSoon();}};
    }
    // The raw source view (원문 편집 toggle, preview detached) is source text, not prose: ASCII
    // symbols take the Latin code face while Hangul keeps the chosen document family. The live
    // surface keeps the document face and reveals only the caret line through the session's
    // transformer, so rendered prose is unchanged. Editor.Render sets the document face on every
    // rebuild; this runs after it (UpdateLivePreview is its last step) and on every mode toggle.
    void ApplySourceFace(){
        if(source==null)return;
        string wanted=!PlainText&&editing&&preview.IsChecked==true?MarkdownLivePreview.SourceFace(family).Source:family;
        if(String.Equals(source.FontFamily.Source,wanted,StringComparison.Ordinal))return;
        source.FontFamily=new System.Windows.Media.FontFamily(wanted);
    }
    // Called after layout, render, theme, font, wrap and line-spacing changes; cheap when nothing changed.
    void UpdateLivePreview(){
        if(source==null)return;
        // A tab switch leaves the previous document's heading list behind; drop it so a closed
        // document's text is not kept alive by the TOC cache (a publish for this tab fills it).
        if(tocData!=null&&source.Document!=null&&!Object.ReferenceEquals(tocData.Document,source.Document))tocData=null;
        ApplySourceFace();
        // A tab switch replaces `source`; a whole-document swap disposes the session itself.
        if(livePreview!=null&&!LivePreviewAttached)DetachLivePreview(false);
        if(!LivePreviewWanted){if(livePreview!=null)DetachLivePreview(true);UpdateLongLines(PlainText);RefreshSearchForView();return;}
        if(livePreview==null){
            // A TXT long-line session can outlive the switch to the live preview; the
            // preview owns the line height and inserts its own generators first.
            bool previous=LongLinesAttached;
            livePreview=MarkdownLivePreview.Attach(source,LiveSettings());livePreview.LinkActivated+=OpenLiveLink;livePreview.TocJump+=LiveTocJump;livePreview.Edited+=LivePreviewEdited;HookLiveCodeMenu(source.TextArea);
            UpdateLongLines(true);
            if(previous){var session=longLines as LongLineWindow.Session;if(session!=null){session.PreviewActivated();session.BringToFront();}}
        }else{livePreview.Update(LiveSettings());UpdateLongLines(true);}
        RefreshSearchForView();
    }
    /* A save that binds the document to a file (the first save of an untitled document, or a Save
       As into another folder) changes the folder relative references resolve against, and the live
       session keeps its own copy of that folder (Session.imageBase). Hand the new one over here:
       Update sees the base change, drops the image cache and redraws the visible lines, which is
       what makes the pictures a first save just moved render in the same turn instead of after a
       reopen. A view with no session attached is left alone (its next attach reads the path
       itself), so a raw-source or TXT view pays nothing. */
    void RefreshLiveDocumentPath(){
        if(livePreview==null||livePreview.IsDisposed||!LivePreviewAttached)return;
        livePreview.Update(LiveSettings());
    }
    // A [TOC] row click: the heading goes to the caret exactly like an outline row (JumpLiveAnchor
    // also centres it in typewriter mode), so the two ways of jumping behave the same.
    void LiveTocJump(int offset){
        if(source==null||source.Document==null)return;
        JumpLiveAnchor(offset);
    }
    /* A document edit while the live session holds a [TOC]: refresh the heading list the box
       draws. The outline's own cheap rule (OutlineMayChange) decides whether this edit can change
       a heading at all, so typing inside a paragraph costs a line read, not a document scan. */
    void LivePreviewEdited(ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs change){
        if(tocData==null||source==null||source.Document==null)return;
        if(livePreview==null||!livePreview.TocSeen)return;
        if(OutlineMayChange(source.Document,change))EnsureTocHeadingsSoon();
    }
    // Ctrl+click targets: "#^id" jumps to the footnote definition, "ref:id" opens the URL of the
    // "[id]: url" definition (case-insensitive), "#heading" jumps to that heading (the basic
    // view's rule, Workspace.cs:153) and any other "#" target stays inside the document the way
    // Markdown.OpenLink already drops it. A click is the only thing that searches, and a large
    // document is searched on a snapshot off the UI thread (see the Resolve methods below).
    void OpenLiveLink(string url){
        // A hand-written link can carry the rich view's own footnote URL; it means the same as "#^id".
        if(url.StartsWith("#mdnote-footnote-",StringComparison.Ordinal))url="#^"+Uri.UnescapeDataString(url.Substring(17));
        if(url.StartsWith("#^",StringComparison.Ordinal)){OpenLiveFootnote(url.Substring(2));return;}
        if(url.StartsWith("ref:",StringComparison.Ordinal)){OpenLiveReference(url.Substring(4));return;}
        if(url.StartsWith("#",StringComparison.Ordinal)){OpenLiveAnchor(url);return;}
        Markdown.OpenLink(url,path,T);
    }
    // The footnote a "#^id" link names: the caret goes to the definition line. The definition is
    // looked up on the click's snapshot (ResolveLiveDefinition), and the line is re-read before the
    // jump so an edit that landed while a large document was being searched cannot send the caret
    // to a line that is no longer the definition.
    void OpenLiveFootnote(string id){
        string marker="[^"+id+"]:";
        ResolveLiveDefinition(marker,delegate(int offset){
            if(offset<0||source==null||source.Document==null)return;
            var document=source.Document;
            if(offset>document.TextLength)return;
            var line=document.GetLineByOffset(offset);
            if(!document.GetText(line).TrimStart().StartsWith(marker,StringComparison.OrdinalIgnoreCase))return;
            JumpLiveAnchor(line.Offset);
        });
    }
    // The URL of the "[id]: url" definition a "ref:id" link names (case-insensitive). The body is
    // read only after the line is confirmed to still carry the marker, so the stale answer a
    // background search can return never opens another line's URL.
    void OpenLiveReference(string id){
        string marker="["+id+"]:";
        ResolveLiveDefinition(marker,delegate(int offset){
            if(offset<0||source==null||source.Document==null)return;
            var document=source.Document;
            if(offset>document.TextLength)return;
            var line=document.GetLineByOffset(offset);
            string head=document.GetText(line).TrimStart();
            if(!head.StartsWith(marker,StringComparison.OrdinalIgnoreCase))return;
            string rest=head.Substring(marker.Length).Trim();
            int space=rest.IndexOfAny(new[]{' ','\t'});string target=space<0?rest:rest.Substring(0,space);
            if(target.StartsWith("<",StringComparison.Ordinal)&&target.EndsWith(">",StringComparison.Ordinal))target=target.Substring(1,target.Length-2);
            if(target.Length>0)Markdown.OpenLink(target,path,T);
        });
    }
    // The definition line for the clicked link: searched on the spot for a small document, on a
    // snapshot off the UI thread for a large one. completed runs with the offset only when a
    // definition was found, and a cancelled search calls nothing.
    void ResolveLiveDefinition(string marker,Action<int> completed){
        var document=source==null?null:source.Document;
        if(document==null)return;
        if(document.TextLength<=LiveLinkSyncLength&&document.LineCount<=LiveLinkSyncLines){
            int offset=FindDefinitionOffset(document,LivePreviewAttached?livePreview:null,marker);
            if(offset>=0&&completed!=null)completed(offset);
            return;
        }
        StartLiveDefinitionSearch(document,marker,completed);
    }
    // #anchor: activating an in-document link moves the caret to the first heading whose slug
    // matches and scrolls it into view; a target that matches nothing moves nothing, like the
    // basic view's NavigateDocumentLink returning without a jump. The heading text is the line's
    // readable text, so markers, links, entities and inline code match the heading the live view
    // draws. Setext, list and quote headings are targets too: the paragraph above an underline is
    // compared for the usual "제목" + "====" shape, and a list or quote line is parsed by the view
    // before its level counts (UI bug hunt item 7). A click never walks the document with a parse
    // per line: the outline panel's list answers first when it has one, and the fallback picks
    // candidate lines by their raw text (ScanAnchorOffsets) so only a line that can be the target
    // is parsed at all. A document past LiveLinkSyncLength (or LiveLinkSyncLines) is scanned on a
    // snapshot off the UI thread and the caret moves when the answer is back; typing pays nothing.
    void OpenLiveAnchor(string url){
        if(source==null||!LivePreviewAttached)return;
        string target=AnchorRule.Target(url);
        int listed=TryOutlineAnchor(target);
        if(listed>=0){JumpLiveAnchor(listed);return;}
        var document=source.Document;
        if(document.TextLength>LiveLinkSyncLength||document.LineCount>LiveLinkSyncLines){StartLiveAnchorSearch(document,target);return;}
        int offset=FindLiveAnchorOffset(document,livePreview,target);
        if(offset>=0)JumpLiveAnchor(offset);
    }
    // The offset the click jumps to, or -1 when no heading matches. The scan reads a snapshot and
    // compares the same label the jump compares; the view's parse only confirms a line whose label
    // already matched, so a quoted line or list item that is not a heading is never parsed (P2-6).
    internal static int FindLiveAnchorOffset(ICSharpCode.AvalonEdit.Document.TextDocument document,MarkdownLivePreview.Session session,string target){
        if(document==null||target==null)return -1;
        System.Collections.Generic.List<int> found=ScanAnchorOffsets(document.CreateSnapshot(),target,System.Threading.CancellationToken.None);
        foreach(int offset in found){
            var line=document.GetLineByOffset(Math.Max(0,Math.Min(offset,document.TextLength)));
            if(LiveAnchorAt(document,session,line,target))return line.Offset;
        }
        return -1;
    }
    // The heading the open outline panel lists for this anchor, or -1. The panel's scan and the
    // click's read the same line grammar, so a current list answers without any document scan; it
    // is used only while it was built from this very version (a list older than the last edit
    // could be missing the heading the click should take first), and the line is read once more
    // before the jump. A miss falls through to the snapshot scan.
    int TryOutlineAnchor(string target){
        if(outline==null||outline.ItemsSource==null||source==null||source.Document==null||displayedOutlineDocument!=source.Document||displayedOutlineVersion==null)return -1;
        if(!Object.ReferenceEquals(displayedOutlineVersion,source.Document.Version))return -1;
        foreach(object entry in outline.Items){var node=entry as SourceOutlineNode;if(node==null)continue;int found=OutlineAnchorNode(node,target);if(found>=0)return found;}
        return -1;
    }
    int OutlineAnchorNode(SourceOutlineNode node,string target){
        if(node==null)return -1;
        if(node.Title!=null&&String.Equals(target,AnchorRule.Slug(node.Title),StringComparison.Ordinal)){
            int offset=displayedOutlineVersion.MoveOffsetTo(source.Document.Version,node.Offset);
            offset=Math.Max(0,Math.Min(offset,source.Document.TextLength));
            var line=source.Document.GetLineByOffset(offset);
            if(LiveAnchorAt(source.Document,livePreview,line,target))return line.Offset;
        }
        foreach(var child in node.Children){int found=OutlineAnchorNode(child,target);if(found>=0)return found;}
        return -1;
    }
    // Whether the line is the heading the target names, read exactly as the click always read it:
    // the view's parse says the line is a heading, then its label slugs to the target.
    static bool LiveAnchorAt(ICSharpCode.AvalonEdit.Document.TextDocument document,MarkdownLivePreview.Session session,ICSharpCode.AvalonEdit.Document.DocumentLine line,string target){
        string text=LiveHeadingText(document,session,line);
        return text!=null&&String.Equals(target,AnchorRule.Slug(OutlineLabel(text)),StringComparison.Ordinal);
    }
    void JumpLiveAnchor(int offset){JumpLiveAnchor(source,offset);}
    internal static void JumpLiveAnchor(SourceEditor editor,int offset){
        if(editor==null||editor.Document==null)return;
        offset=Math.Max(0,Math.Min(offset,editor.Document.TextLength));
        editor.Select(offset,0);MarkdownLivePreview.Session.SafeBringCaretToView(editor.TextArea);editor.Focus();
        // Typewriter mode: the jumped-to heading goes to the middle, not the bottom edge (FocusMode.cs).
        var surface=FocusSurfaceOf(editor);if(surface!=null)surface.CentreAfterJump();
    }
    // The line's readable heading text, or null when the live view does not draw it as one. The
    // text is the ATX title without its outer markers (a list or quote prefix included) or the
    // whole paragraph line a setext underline promoted. The session must own the document: its
    // parse is what decides, and the block cache it reads is keyed by that document's lines.
    static string LiveHeadingText(ICSharpCode.AvalonEdit.Document.TextDocument document,MarkdownLivePreview.Session session,ICSharpCode.AvalonEdit.Document.DocumentLine line){
        if(document==null||session==null||line==null||!Object.ReferenceEquals(document,session.Document))return null;
        var info=session.Info(line);
        if(info.Code||info.Heading==0)return null;
        if(line.Length>OutlineParseLimit)return null;
        int length=Math.Min(line.Length,OutlineHead);char[] buffer=new char[length];
        document.GetText(line.Offset,length).CopyTo(0,buffer,0,length);
        if(info.Setext)return OutlineParagraphRaw(buffer,length);
        int level,titleStart;
        OutlineClassify(buffer,length,out level,out titleStart);
        if(level==0)return OutlineParagraphRaw(buffer,length);
        return OutlineAtxRaw(buffer,length,titleStart);
    }
    /* A definition is looked up on click only. Lines the live view draws as code are skipped: a
       "[^1]:" or "[id]: url" inside a fence is not a definition there, so the click used to jump
       into the fence and a reference link could open the fence's URL (UI bug hunt item 5). The
       candidates are picked by their text (at most three spaces, then "["), so a line that cannot
       hold a definition is never parsed. */
    ICSharpCode.AvalonEdit.Document.DocumentLine FindDefinition(string marker){
        if(source==null||source.Document==null)return null;
        int offset=FindDefinitionOffset(source.Document,LivePreviewAttached?livePreview:null,marker);
        return offset<0?null:source.Document.GetLineByOffset(offset);
    }
    static int FindDefinitionOffset(ICSharpCode.AvalonEdit.Document.TextDocument document,MarkdownLivePreview.Session session,string marker){
        if(document==null||marker==null||marker.Length==0)return -1;
        System.Collections.Generic.List<int> found=ScanDefinitionOffsets(document.CreateSnapshot(),marker,System.Threading.CancellationToken.None);
        foreach(int offset in found){
            var line=document.GetLineByOffset(Math.Max(0,Math.Min(offset,document.TextLength)));
            if(session!=null&&Object.ReferenceEquals(document,session.Document)&&session.Info(line).Code)continue;
            if(!LiveDefinitionHead(document,line,marker))continue;
            return line.Offset;
        }
        return -1;
    }
    // The line still carries the marker after at most three spaces: the shape the snapshot scan
    // accepted, read again from the live line so a stale offset cannot pick another line.
    static bool LiveDefinitionHead(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line,string marker){
        if(line==null||marker==null||line.Length<marker.Length)return false;
        int at=line.Offset,end=line.EndOffset;
        while(at<end&&(document.GetCharAt(at)==' '||document.GetCharAt(at)=='\t')){at++;if(at-line.Offset>3)return false;}
        if(end-at<marker.Length)return false;
        return String.Equals(document.GetText(at,marker.Length),marker,StringComparison.OrdinalIgnoreCase);
    }
    /* Offsets of the lines the "#anchor" rule calls a heading and whose label slugs to the target,
       in document order. The scan reads a snapshot one chunk at a time and keeps the outline's line
       grammar - fenced blocks, an ATX heading after any outer quote/list prefix, and the paragraph
       a setext underline promotes - so a candidate is found by its raw text alone, without a parse
       per line, and the answer matches the click on the live parse. A line over OutlineParseLimit
       is skipped the way the click skips it. */
    static System.Collections.Generic.List<int> ScanAnchorOffsets(ICSharpCode.AvalonEdit.Document.ITextSource snapshot,string target,System.Threading.CancellationToken cancellation){
        var found=new System.Collections.Generic.List<int>();
        int offset=0;char fence='\0';int fenceLength=0;
        bool pending=false;int pendingOffset=0;char[] pendingText=null;int pendingLength=0;
        using(var reader=snapshot.CreateReader()){
            var line=new OutlineLine();var chunk=new char[8192];int count;bool previousCR=false;
            Action consume=delegate{
                cancellation.ThrowIfCancellationRequested();
                bool fenceLine=line.Fence;char symbol=line.Marker;int length=line.MarkerLength;bool white=line.TailWhite,backtick=line.HasBacktick;
                int lineLength=line.Length,headLength=line.HeadLength;char[] head=line.Head;line.Reset();
                if(fence!='\0'){if(fenceLine&&symbol==fence&&length>=fenceLength&&white)fence='\0';pending=false;return;}
                if(fenceLine&&!(symbol=='`'&&backtick)){fence=symbol;fenceLength=length;pending=false;return;}
                if(lineLength>OutlineParseLimit){pending=false;return;}
                int level,titleStart;
                OutlineClassify(head,headLength,out level,out titleStart);
                if(level>0){
                    string raw=OutlineAtxRaw(head,headLength,titleStart);
                    if(raw!=null&&String.Equals(target,AnchorRule.Slug(OutlineLabel(raw)),StringComparison.Ordinal))found.Add(offset);
                    pending=false;return;
                }
                if(OutlineUnderline(head,headLength)>0){
                    if(pending&&pendingText!=null){
                        string promoted=OutlineParagraphRaw(pendingText,pendingLength);
                        if(promoted!=null&&String.Equals(target,AnchorRule.Slug(OutlineLabel(promoted)),StringComparison.Ordinal))found.Add(pendingOffset);
                    }
                    pending=false;return;
                }
                if(OutlineParagraph(head,headLength)){pending=true;pendingOffset=offset;pendingText=head;pendingLength=headLength;}
                else pending=false;
            };
            int position=0;while((count=reader.Read(chunk,0,chunk.Length))>0){cancellation.ThrowIfCancellationRequested();for(int i=0;i<count;i++,position++){
                char c=chunk[i];if(previousCR){previousCR=false;if(c=='\n'){offset=position+1;continue;}}
                if(c=='\r'||c=='\n'){consume();offset=position+1;previousCR=c=='\r';}else line.Feed(c,fence=='\0');
            }}consume();
        }
        return found;
    }
    /* Offsets of the lines that can hold the definition the marker names: at most three leading
       spaces, then a "[" line whose head equals the marker (case-insensitive, the way the reference
       link rule compares it). Fence lines are tracked so a "[^id]:" inside a code block is not a
       definition, the same call the view's parse makes; the caller parses the found line to be sure. */
    static System.Collections.Generic.List<int> ScanDefinitionOffsets(ICSharpCode.AvalonEdit.Document.ITextSource snapshot,string marker,System.Threading.CancellationToken cancellation){
        var found=new System.Collections.Generic.List<int>();
        if(marker==null||marker.Length==0)return found;
        int offset=0;char fence='\0';int fenceLength=0;
        using(var reader=snapshot.CreateReader()){
            var line=new OutlineLine();var chunk=new char[8192];int count;bool previousCR=false;
            Action consume=delegate{
                cancellation.ThrowIfCancellationRequested();
                bool fenceLine=line.Fence;char symbol=line.Marker;int length=line.MarkerLength;bool white=line.TailWhite,backtick=line.HasBacktick;
                int headLength=line.HeadLength;char[] head=line.Head;line.Reset();
                if(fence!='\0'){if(fenceLine&&symbol==fence&&length>=fenceLength&&white)fence='\0';return;}
                if(fenceLine&&!(symbol=='`'&&backtick)){fence=symbol;fenceLength=length;return;}
                int at=0;
                while(at<headLength&&at<4&&(head[at]==' '||head[at]=='\t'))at++;
                if(at>3||at>=headLength||head[at]!='[')return;
                if(MarkerAt(head,at,headLength,marker))found.Add(offset);
            };
            int position=0;while((count=reader.Read(chunk,0,chunk.Length))>0){cancellation.ThrowIfCancellationRequested();for(int i=0;i<count;i++,position++){
                char c=chunk[i];if(previousCR){previousCR=false;if(c=='\n'){offset=position+1;continue;}}
                if(c=='\r'||c=='\n'){consume();offset=position+1;previousCR=c=='\r';}else line.Feed(c,fence=='\0');
            }}consume();
        }
        return found;
    }
    // The marker at the line head, compared the way StartsWith(marker, OrdinalIgnoreCase) compares
    // the line's own text.
    static bool MarkerAt(char[] text,int start,int length,string marker){
        if(marker==null||start<0||start+marker.Length>length)return false;
        for(int i=0;i<marker.Length;i++){char a=text[start+i],b=marker[i];if(a==b)continue;if(Char.ToUpperInvariant(a)!=Char.ToUpperInvariant(b))return false;}
        return true;
    }
    /* A link search on a large document reads a snapshot off the UI thread, so the window keeps
       drawing and only the found line is parsed, on the UI thread, when the answer is back. One
       search runs at a time: a second click cancels the first, and the newest search owns the wait
       cursor so an older continuation cannot clear the newer one's. */
    const int LiveLinkSyncLength=2*1024*1024;
    // A document of very many short lines (a generated heading or definition per line) would make
    // the on-the-spot scan pay one label per line, so its line count sends it to the snapshot too.
    const int LiveLinkSyncLines=20000;
    int liveLinkSearch;
    System.Threading.CancellationTokenSource liveLinkSearchPending;
    bool liveLinkWaiting;
    System.Windows.Input.Cursor liveLinkPreviousCursor;
    internal Exception LastLiveLinkError;
    void CancelLiveLinkSearch(){
        var pending=liveLinkSearchPending;liveLinkSearchPending=null;
        if(pending!=null){try{pending.Cancel();}catch(ObjectDisposedException){}}
    }
    void BeginLiveLinkWait(){
        if(liveLinkWaiting)return;
        liveLinkPreviousCursor=System.Windows.Input.Mouse.OverrideCursor;
        System.Windows.Input.Mouse.OverrideCursor=System.Windows.Input.Cursors.Wait;
        liveLinkWaiting=true;
    }
    void EndLiveLinkWait(){
        if(!liveLinkWaiting)return;
        liveLinkWaiting=false;
        System.Windows.Input.Mouse.OverrideCursor=liveLinkPreviousCursor;
        liveLinkPreviousCursor=null;
    }
    void StartLiveAnchorSearch(ICSharpCode.AvalonEdit.Document.TextDocument document,string target){
        CancelLiveLinkSearch();
        var pending=new System.Threading.CancellationTokenSource();liveLinkSearchPending=pending;int search=++liveLinkSearch;
        BeginLiveLinkWait();
        var snapshot=document.CreateSnapshot();
        ResolveLiveAnchorAsync(search,pending,document,snapshot,target);
    }
    async void ResolveLiveAnchorAsync(int search,System.Threading.CancellationTokenSource pending,ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.ITextSource snapshot,string target){
        System.Collections.Generic.List<int> found=null;
        try{found=await System.Threading.Tasks.Task.Run(delegate{return ScanAnchorOffsets(snapshot,target,pending.Token);},pending.Token);}
        catch(OperationCanceledException){}catch(Exception error){LastLiveLinkError=error;}
        if(search==liveLinkSearch){liveLinkSearchPending=null;EndLiveLinkWait();}
        if(found==null||search!=liveLinkSearch||pending.IsCancellationRequested)return;
        if(source==null||fileOpenClosed||!Object.ReferenceEquals(source.Document,document)||!LivePreviewAttached)return;
        try{
            foreach(int offset in found){
                int at=Math.Max(0,Math.Min(snapshot.Version.MoveOffsetTo(document.Version,offset),document.TextLength));
                var line=document.GetLineByOffset(at);
                if(LiveAnchorAt(document,livePreview,line,target)){JumpLiveAnchor(line.Offset);return;}
            }
        }catch(Exception error){LastLiveLinkError=error;}
    }
    void StartLiveDefinitionSearch(ICSharpCode.AvalonEdit.Document.TextDocument document,string marker,Action<int> completed){
        CancelLiveLinkSearch();
        var pending=new System.Threading.CancellationTokenSource();liveLinkSearchPending=pending;int search=++liveLinkSearch;
        BeginLiveLinkWait();
        var snapshot=document.CreateSnapshot();
        ResolveLiveDefinitionAsync(search,pending,document,snapshot,marker,completed);
    }
    async void ResolveLiveDefinitionAsync(int search,System.Threading.CancellationTokenSource pending,ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.ITextSource snapshot,string marker,Action<int> completed){
        System.Collections.Generic.List<int> found=null;
        try{found=await System.Threading.Tasks.Task.Run(delegate{return ScanDefinitionOffsets(snapshot,marker,pending.Token);},pending.Token);}
        catch(OperationCanceledException){}catch(Exception error){LastLiveLinkError=error;}
        if(search==liveLinkSearch){liveLinkSearchPending=null;EndLiveLinkWait();}
        if(found==null||search!=liveLinkSearch||pending.IsCancellationRequested||completed==null)return;
        if(source==null||fileOpenClosed||!Object.ReferenceEquals(source.Document,document))return;
        try{
            var session=LivePreviewAttached?livePreview:null;
            foreach(int offset in found){
                int at=Math.Max(0,Math.Min(snapshot.Version.MoveOffsetTo(document.Version,offset),document.TextLength));
                var line=document.GetLineByOffset(at);
                if(session!=null&&session.Info(line).Code)continue;
                if(!LiveDefinitionHead(document,line,marker))continue;
                completed(line.Offset);return;
            }
        }catch(Exception error){LastLiveLinkError=error;}
    }
    // P9: lines over 16K chars keep only a bounded real-text window (LongLineWindow).
    // The source surface owns the window: it is attached with the live preview and
    // also in TXT mode, which has no preview. Raw-edit Markdown (preview toggled off)
    // stays detached; the basic view has no source surface at all.
    IDisposable longLines;
    bool LongLinesAttached {
        get{
            var session=longLines as LongLineWindow.Session;
            return session!=null&&session.IsAttached&&Object.ReferenceEquals(session.Editor,source);
        }
    }
    // One owner for attach/detach: a tab switch replaces `source`, so a session that
    // no longer belongs to the current editor (or is unwanted) is disposed here.
    void UpdateLongLines(bool wanted){
        if(longLines!=null&&(!wanted||!LongLinesAttached)){longLines.Dispose();longLines=null;}
        if(wanted&&longLines==null&&source!=null)longLines=LongLineWindow.Attach(source);
    }
    void DetachLivePreview(bool restoreLines){
        if(longLines!=null){longLines.Dispose();longLines=null;}
        var session=livePreview;livePreview=null;
        SourceEditor owner=session.Editor;UnhookLiveCodeMenu(owner);session.Dispose();
        if(restoreLines&&owner==source)ApplySourceLines();
    }
    // Source-surface outline refresh restarts a whole-document scan, so skip it for edits that
    // cannot change headings. A change is judged on the lines it can reach: the edited line and the
    // line below it (a setext underline); when a break was typed, also the line two below (an
    // underline the break pushed down); when a break was removed, the two halves the join made. The
    // heading marker is read the way the scan reads it, so a heading behind a list or quote prefix
    // ("- ## x", "> # x") counts while "#tag" and a bullet line are ordinary text. Every read is one
    // line, never the document.
    bool sourceOutlineDirty=true;
    bool OutlineMayChange(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs change){
        if(OutlineSensitive(change.InsertedText)||OutlineSensitive(change.RemovedText))return true;
        if(OutlineMultilineChange(change))return true;
        bool breaks=HasLineBreak(change.InsertedText)||HasLineBreak(change.RemovedText);
        int at=Math.Min(change.Offset,document.TextLength);
        var line=document.GetLineByOffset(at);
        if(OutlineLineShape(document,line)!=0||OutlineSetextPair(document,line))return true;
        if(!breaks)return false;
        /* A break typed at the end of a setext text line pushes its underline one line down, and a
           break above a fence line moves which lines are code: the heading above is gone even though
           this line's own next line is the empty one now. */
        var next=line.NextLine;
        if(change.InsertionLength>0&&next!=null&&next.NextLine!=null&&OutlineStructuralLine(document,next.NextLine)&&OutlineParagraphLine(document,line))return true;
        /* A removed break leaves the old second line as this line's tail and the old first line as
           its head, so a marker or underline either one owned is no longer a line of its own. */
        return change.InsertionLength==0&&OutlineJoinedLine(document,line,at);
    }
    // Characters that can change a line's block shape by themselves: a heading, list or quote
    // marker, a setext underline or a fence. A line break is not one of them; a break is answered
    // by re-reading the lines it moved (above).
    static bool OutlineSensitive(ICSharpCode.AvalonEdit.Document.ITextSource text){
        if(text.TextLength>256)return true;
        for(int i=0;i<text.TextLength;i++){char c=text.GetCharAt(i);if(c=='#'||c=='`'||c=='~'||c=='='||c=='-'||c=='>')return true;}
        return false;
    }
    // A paste or a multi-line deletion can carry a heading anywhere inside its text, so it is not
    // narrowed to the lines around the change. A typed break (one or two characters) still is.
    static bool OutlineMultilineChange(ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs change){
        return change.InsertedText.TextLength>2&&HasLineBreak(change.InsertedText)||change.RemovedText.TextLength>2&&HasLineBreak(change.RemovedText);
    }
    /* One line, read the way the scan reads it: its first OutlineHead characters, and never a line
       longer than OutlineParseLimit (the scan skips those). The buffer is reused; every caller runs
       on the UI thread inside the document-change callback. */
    char[] outlineChangeHead=new char[OutlineHead];
    char[] OutlineLineHead(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line,out int length){
        length=0;
        if(line==null||line.Length==0||line.Length>OutlineParseLimit)return null;
        length=Math.Min(line.Length,OutlineHead);
        document.GetText(line.Offset,length).CopyTo(0,outlineChangeHead,0,length);
        return outlineChangeHead;
    }
    /* What a line's head is, read the same way the scan reads it. 0: ordinary text. 1: a heading
       (ATX, also behind a list or quote prefix). 2: a setext underline, or a line that is only
       whitespace and one "=" or "-" run and may have just stopped being one - a space typed inside
       "====" takes the heading off the line above it and inserts no marker character. 3: a fence
       line, whose run decides which later lines are code. */
    static int OutlineHeadShape(char[] head,int length){
        int level,titleStart;
        OutlineClassify(head,length,out level,out titleStart);
        if(level>0)return 1;
        if(OutlineUnderline(head,length)>0)return 2;
        if(OutlineFenceStart(head,length))return 3;
        bool equals=false,dash=false;
        for(int i=0;i<length;i++){char c=head[i];if(c==' '||c=='\t')continue;if(c=='=')equals=true;else if(c=='-')dash=true;else return 0;if(equals&&dash)return 0;}
        return equals||dash?2:0;
    }
    // A line that looks like a code fence: up to three leading spaces, then three or more "`" or "~".
    static bool OutlineFenceStart(char[] head,int length){
        int p=0;
        while(p<length&&p<4&&(head[p]==' '||head[p]=='\t'))p++;
        if(p>3||p>=length)return false;
        char marker=head[p];
        if(marker!='`'&&marker!='~')return false;
        int count=0;
        while(p<length&&head[p]==marker){p++;count++;}
        return count>=3;
    }
    int OutlineLineShape(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line){
        int length;char[] head=OutlineLineHead(document,line,out length);
        return head==null?0:OutlineHeadShape(head,length);
    }
    bool OutlineStructuralLine(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line){
        int length;char[] head=OutlineLineHead(document,line,out length);
        return head!=null&&OutlineHeadShape(head,length)!=0;
    }
    bool OutlineParagraphLine(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line){
        int length;char[] head=OutlineLineHead(document,line,out length);
        return head!=null&&OutlineParagraph(head,length);
    }
    // A paragraph line over an underline is a heading in the view and in the outline: editing the
    // paragraph's own text changes the heading's label.
    bool OutlineSetextPair(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line){
        if(line==null)return false;
        var next=line.NextLine;
        if(next==null)return false;
        int length;char[] head=OutlineLineHead(document,next,out length);
        if(head==null||OutlineUnderline(head,length)==0)return false;
        head=OutlineLineHead(document,line,out length);
        return head!=null&&OutlineParagraph(head,length);
    }
    // A removed break joins two lines; either half could have been a heading or an underline, and
    // the old second half could have been the text of a setext heading whose underline still follows.
    bool OutlineJoinedLine(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line,int at){
        if(line==null||at<=line.Offset||at>=line.EndOffset)return false;
        int length=Math.Min(at-line.Offset,OutlineHead);
        document.GetText(line.Offset,length).CopyTo(0,outlineChangeHead,0,length);
        if(OutlineHeadShape(outlineChangeHead,length)!=0)return true;
        length=Math.Min(line.EndOffset-at,OutlineHead);
        document.GetText(at,length).CopyTo(0,outlineChangeHead,0,length);
        if(OutlineHeadShape(outlineChangeHead,length)!=0)return true;
        if(!OutlineParagraph(outlineChangeHead,length))return false;
        int nextLength;char[] next=OutlineLineHead(document,line.NextLine,out nextLength);
        return next!=null&&OutlineUnderline(next,nextLength)>0;
    }
    // MD↔TXT on the live surface (P3): no FlowDocument. The caret maps to the start of the
    // corresponding line (approximation; exact in-line mapping is still open). imageLabel is the
    // same "[이미지: " prefix the conversion used, so an image before the caret keeps its length.
    static int LivePlainCaret(string markdown,int rawOffset,string imageLabel=null){
        rawOffset=Math.Max(0,Math.Min(rawOffset,markdown.Length));
        int lineStart=rawOffset==0?0:markdown.LastIndexOf('\n',rawOffset-1)+1;
        // Convert drops the final newline of its output; the caret line starts after it.
        int plainLineStart=lineStart==0?0:Canonical(MarkdownPlainText.Convert(markdown.Substring(0,lineStart),imageLabel)).Length+1;
        if(rawOffset==lineStart)return plainLineStart;
        // Within the line: the converted length of the text up to the caret (markup before it removed).
        return Math.Max(plainLineStart,Canonical(MarkdownPlainText.Convert(markdown.Substring(0,rawOffset),imageLabel)).Length);
    }
    void PlaceLiveLiteralCaret(string plain,int caret){
        string canonical=Canonical(plain);caret=Math.Max(0,Math.Min(caret,canonical.Length));
        int line=0,lineStart=0;for(int i=0;i<caret;i++)if(canonical[i]=='\n'){line++;lineStart=i+1;}
        var document=source.Document;var target=document.GetLineByNumber(Math.Min(document.LineCount,line+1));
        // Within the line: the escaped length of the plain text before the caret.
        int lineEnd=canonical.IndexOf('\n',lineStart);if(lineEnd<0)lineEnd=canonical.Length;
        // A line LiteralMarkdown kept as typed maps one to one; an escaped one by its escaped prefix.
        bool kept=document.GetText(target.Offset,target.Length)==canonical.Substring(lineStart,lineEnd-lineStart);
        int column=caret>lineStart?(kept?caret-lineStart:FullLiteralMarkdown(canonical.Substring(lineStart,caret-lineStart)).Length):0;
        source.CaretIndex=target.Offset+Math.Min(column,target.Length);
    }
    // Alt/F10 (and Alt+letter) enter the menu bar from the editing surface. This tunnelling
    // window handler still runs while the TextArea has keyboard focus, before the menu takes it.
    void MenuKeyCommitComposition(object sender,System.Windows.Input.KeyEventArgs e){
        bool activation=e.Key==System.Windows.Input.Key.F10||(e.Key==System.Windows.Input.Key.System&&(System.Windows.Input.Keyboard.Modifiers&System.Windows.Input.ModifierKeys.Alt)!=0&&(System.Windows.Input.Keyboard.Modifiers&System.Windows.Input.ModifierKeys.Control)==0);
        if(!activation)return;
        if(ImeTrace.Enabled)ImeTrace.Write("menu key activation key="+e.Key+" systemKey="+(e.Key==System.Windows.Input.Key.System?e.SystemKey.ToString():"-")+" focus="+FocusName());
        CommitCompositionForMenu("key");
    }
    // Commit while the TextArea still owns focus, before the menu cancels the IMM composition.
    void CommitCompositionForMenu(string reason){
        if(!IsActive||reason=="menu hover")return;
        if(MenuOwnsFocus()){
            if(ImeTrace.Enabled)ImeTrace.Write("menu commit skipped menu focus reason="+reason+" focus="+FocusName());
            return;
        }
        if(source==null||source.Visibility!=System.Windows.Visibility.Visible)return;
        source.RefreshCompositionState("menu "+reason);
        if(!source.ImeBusy&&!source.ImeCompositionActive())return;
        if(ImeTrace.Enabled)ImeTrace.Write("menu commit source reason="+reason+" "+source.CompositionState()+" focus="+FocusName());
        source.BeginCompositionCommit();
    }
    internal static string FocusName(){
        var focused=System.Windows.Input.Keyboard.FocusedElement;
        return focused==null?"null":focused.GetType().Name;
    }
    // N23: keep the reading position across a view change (zoom, theme, line spacing, base font).
    // The anchor is the buffer offset of a character (the caret when it is on screen, otherwise the
    // first visible character) and its distance from the top of the viewport, so the rebuilt view
    // can put the same character in the same place. A line or block start would drift by the
    // wrapped rows above it. The offset is the TextDocument's own: every restore is guarded to the
    // tab and view transition it was captured in and lands in the same document the render keeps
    // (mode switches and loads start a new transition), so no canonical (LF) copy of the text is
    // needed to keep newline styles apart.
    sealed class ReadingAnchor {internal int Line=1;internal int Offset;internal double Top;}
    ReadingAnchor CaptureReadingAnchor(){
        var anchor=new ReadingAnchor();
        bool caretVisible=false;
        var caretRect=source.GetRectFromCharacterIndex(source.CaretIndex);
        if(!caretRect.IsEmpty&&caretRect.Bottom>0&&caretRect.Top<source.ViewportHeight){
            anchor.Offset=source.CaretIndex;
            anchor.Line=source.Document.GetLineByOffset(source.CaretIndex).LineNumber;anchor.Top=caretRect.Top;caretVisible=true;
        }
        if(!caretVisible){
            int offset=FirstVisibleSourceOffset();
            var rect=source.GetRectFromCharacterIndex(offset);
            anchor.Offset=offset;
            anchor.Line=source.Document.GetLineByOffset(offset).LineNumber;anchor.Top=rect.IsEmpty?0:rect.Top;
        }
        return anchor;
    }
    // The character at the top-left of the viewport, not the line that contains it: a scrolled
    // wrapped line must restore the same character.
    int FirstVisibleSourceOffset(){
        var view=source.TextArea.TextView;if(view==null)return 0;
        view.EnsureVisualLines();
        var position=view.GetPositionFloor(new System.Windows.Point(view.ScrollOffset.X,view.ScrollOffset.Y));
        if(position.HasValue)return Math.Max(0,Math.Min(source.Document.TextLength,source.Document.GetOffset(position.Value.Location)));
        int line=source.GetFirstVisibleLineIndex();
        return line<0?0:source.GetCharacterIndexFromLineIndex(line);
    }
    int TextLineAt(int offset){return TextLineAt(Canonical(documentText),offset);}
    static int TextLineAt(string value,int offset){
        int line=1,end=Math.Max(0,Math.Min(offset,value.Length));
        for(int i=0;i<end;i++)if(value[i]=='\n')line++;
        return line;
    }
    // The deferred callbacks capture the document, the surface and the transition generation they
    // belong to. A tab switch, another toggle or a document swap in between must not scroll the
    // wrong document, and a stale callback must not end a newer transition's mask.
    void RestoreReadingAnchor(ReadingAnchor anchor,bool firstLayoutOnly=false){
        if(anchor==null)return;
        DocumentTab document=activeDocument;int version=viewTransitionVersion;
        Action apply=delegate{
            if(fileOpenClosed||version!=viewTransitionVersion||!Object.ReferenceEquals(activeDocument,document))return;
            var targetDocument=source.Document;if(targetDocument==null||targetDocument.LineCount==0)return;
            // The offset is this document's own: the guards above reject a tab switch or a newer
            // view transition, and Render rewrites this same TextDocument, so no canonical (LF)
            // conversion and no copy of the text is needed. The line scroll measures the wrapped
            // rows above the position, the offset below targets the character.
            int offset=Math.Max(0,Math.Min(anchor.Offset,targetDocument.TextLength));
            int number=targetDocument.GetLineByOffset(offset).LineNumber;
            UpdateLayout();source.ScrollToLine(number-1);UpdateLayout();
            var view=source.TextArea.TextView;view.EnsureVisualLines();
            // Measure in document space, not from the editor rect: right after a re-render
            // the source is still at offset 0, where ApplySourceInset pads the text view by 24px.
            // That transient inset would enter the delta and leave the character 24px too high
            // once the scroll and the disappearing inset settle. The settled top inset is
            // max(0,24-offset), so solve for the offset that puts the character at anchor.Top in
            // that geometry; inside the first 24px the content moves twice as fast (the inset
            // shrinks while the content scrolls), hence the halved target there.
            double top=view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(targetDocument.GetLocation(offset)),ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop).Y+source.Padding.Top;
            double target=top-anchor.Top;
            if(target<24){double inset=(top+24-anchor.Top)/2;target=inset<0?0:inset;}
            source.ScrollToVerticalOffset(Math.Max(0,target));
        };
        apply();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{apply();}));
        // This generation still owns the transition? Then end it even when the document guard
        // inside apply() rejects the scroll: a tab switch can remove the mask without ending the
        // flag, and leaving it set would swallow every later BringIntoView. A newer transition
        // bumped the version, so it must keep its own mask.
        Dispatcher.BeginInvoke(firstLayoutOnly?System.Windows.Threading.DispatcherPriority.Loaded:System.Windows.Threading.DispatcherPriority.ContextIdle,new Action(delegate{if(version==viewTransitionVersion){apply();EndViewTransition(firstLayoutOnly);}}));
    }
    // 보기 > 라이브 모드. Basic (live=false) keeps HideMarkers on, so every line is drawn
    // formatted; live shows the caret and selection lines as source. hideMarkers is static, so
    // one switch reaches every window: each open window's live preview is refreshed with the new
    // setting and every View menu's check mark is rewritten. The item toggles itself before its
    // Click fires, so `live` is already the state the user asked for.
    void SetLiveMode(bool live){
        if(ImeTrace.Enabled)ImeTrace.Write("SetLiveMode live="+live+" hideMarkers="+hideMarkers+" focus="+FocusName());
        if(source!=null&&(source.ImeBusy||source.ImeCompositionActive()))FinishComposition();
        hideMarkers=!live;
        var app=System.Windows.Application.Current;bool self=false;
        if(app!=null)foreach(System.Windows.Window candidate in app.Windows){
            var window=candidate as MainWindow;if(window==null)continue;
            if(Object.ReferenceEquals(window,this))self=true;
            window.UpdateLivePreview();
        }
        if(!self)UpdateLivePreview();
        UpdateLiveModeMenu();SavePreferences();
    }
    // One preference is shared by every window, including windows opened after the switch (they
    // read hideMarkers while building the item). A SubmenuOpened refresh on each View menu also
    // covers a window that was built before this one.
    void UpdateLiveModeMenu(){
        bool live=!hideMarkers;bool self=false;
        var app=System.Windows.Application.Current;
        if(app!=null)foreach(System.Windows.Window candidate in app.Windows){
            var window=candidate as MainWindow;if(window==null)continue;
            if(Object.ReferenceEquals(window,this))self=true;
            if(window.liveModeMenu!=null)window.liveModeMenu.IsChecked=live;
        }
        if(!self&&liveModeMenu!=null)liveModeMenu.IsChecked=live;
    }
    // 보기 > 제목 단계 표시 (slash bars / # symbols / off). headingMarkStyle is static (one preference for
    // every window and tab): each open window's session gets the new Settings.HeadingMarks/HeadingHash and
    // redraws its visible lines (Update rebuilds the visual lines, so the heading indent and the marks change
    // together); a tab that is not showing picks it up when it is switched to. A style choice toggles itself
    // before its Click fires, so the menu is rewritten from the style afterwards. Off never changes the
    // remembered style, which the row itself turns back on with.
    void SetHeadingMarkStyle(int style){
        if(style<0||style>2)style=0;
        if(source!=null&&(source.ImeBusy||source.ImeCompositionActive()))FinishComposition();
        headingMarkStyle=style;if(style!=2)headingMarkLast=style;
        var app=System.Windows.Application.Current;bool self=false;
        if(app!=null)foreach(System.Windows.Window candidate in app.Windows){
            var window=candidate as MainWindow;if(window==null)continue;
            if(Object.ReferenceEquals(window,this))self=true;
            window.UpdateLivePreview();
        }
        if(!self)UpdateLivePreview();
        UpdateHeadingMarksMenu();SavePreferences();
    }
    // The 제목 단계 표시 row itself: on it turns off, off it comes back at the last style.
    void ToggleHeadingMarksFromHeader(){SetHeadingMarkStyle(headingMarkStyle!=2?2:headingMarkLast);}
    void UpdateHeadingMarksMenu(){
        bool self=false;
        var app=System.Windows.Application.Current;
        if(app!=null)foreach(System.Windows.Window candidate in app.Windows){
            var window=candidate as MainWindow;if(window==null)continue;
            if(Object.ReferenceEquals(window,this))self=true;
            window.ShowHeadingMarksMenu();
        }
        if(!self)ShowHeadingMarksMenu();
    }
    void ShowHeadingMarksMenu(){
        if(headingMarksMenu!=null&&headingMarksMenu.IsChecked!=(headingMarkStyle!=2))headingMarksMenu.IsChecked=headingMarkStyle!=2;
        for(int i=0;i<headingMarkChoices.Length;i++)if(headingMarkChoices[i]!=null&&headingMarkChoices[i].IsChecked!=(i==headingMarkStyle))headingMarkChoices[i].IsChecked=i==headingMarkStyle;
    }

    static string OutlineText(System.Windows.DependencyObject node){
        var block=node as System.Windows.Controls.TextBlock;if(block!=null)return block.Text+" ";
        var item=node as System.Windows.Controls.TreeViewItem;if(item!=null&&item.Header is System.Windows.DependencyObject)return OutlineText((System.Windows.DependencyObject)item.Header);
        var text=new System.Text.StringBuilder();
        foreach(object child in System.Windows.LogicalTreeHelper.GetChildren(node)){var element=child as System.Windows.DependencyObject;if(element!=null&&!(element is System.Windows.Controls.TreeViewItem))text.Append(OutlineText(element));}
        return text.ToString();
    }
    // Installed menu handlers, not only the pure heading transformer. Invoked by live-host-probe,
    // never by toolbar-shot (that renderer must not open a window or popup).
    static void VerifyLiveToolbarRegression(MainWindow window,Action<string,bool,string> check,string loadFile){
        string[] samples={"body","# title","first\nsecond","head\nlast","- ## item","> - ## item"};
        string[] expected={"### body","### title","### first\n### second","head\n### last","### item","> ### item"};
        string[] names={"body line","heading line","selection","end of document","list heading","quoted list heading"};
        for(int i=0;i<samples.Length;i++){
            window.SetText(samples[i]);window.SetMode(true);LongLineBench.Idle();
            int at=i==3?samples[i].Length:i==2?0:Math.Min(2,samples[i].Length);window.source.Select(at,i==2?samples[i].Length:0);
            window.headingButtons[2].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));LongLineBench.Idle();
            bool applied=window.source.Document.Text==expected[i];
            check("live heading menu click on "+names[i],applied,"source/expected match="+applied);
            window.Undo();LongLineBench.Idle();
            check("live heading menu undo on "+names[i],window.source.Document.Text==samples[i],"one step="+(window.source.Document.Text==samples[i]));
            window.Redo();LongLineBench.Idle();check("live heading menu redo on "+names[i],window.source.Document.Text==expected[i],"restored="+(window.source.Document.Text==expected[i]));
        }
        window.SetText("# title");window.source.Select(3,0);
        window.headingButtons[0].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));LongLineBench.Idle();
        check("live heading menu same level toggles off",window.source.Document.Text=="title","same-level click");window.Undo();LongLineBench.Idle();
        string before=window.source.Document.Text;bool undo=window.History.CanUndo,redo=window.History.CanRedo;int start=window.source.SelectionStart,length=window.source.SelectionLength;
        window.RunLiveCommand(delegate(ICSharpCode.AvalonEdit.Document.TextDocument d,int s,int l){var invalid=new CommandResult();invalid.Add(2,3,"bad");invalid.Add(1,3,"overlap");return invalid;});
        check("invalid toolbar command is atomic",window.source.Document.Text==before&&window.History.CanUndo==undo&&window.History.CanRedo==redo&&window.source.SelectionStart==start&&window.source.SelectionLength==length,"buffer/selection/history unchanged");
        window.RunLiveCommand(delegate(ICSharpCode.AvalonEdit.Document.TextDocument d,int s,int l){throw new ArgumentException("probe command failure");});
        check("toolbar exception does not escape",window.source.Document.Text==before,"source unchanged");
        bool failOnce=true;EventHandler<ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs> failure=delegate{if(failOnce){failOnce=false;throw new ArgumentException("probe apply failure");}};
        window.source.Document.Changed+=failure;
        try{window.RunLiveCommand(delegate(ICSharpCode.AvalonEdit.Document.TextDocument d,int s,int l){var edits=new CommandResult();edits.Add(d.TextLength-1,1,"X");edits.Add(0,1,"Y");return edits;});}
        finally{window.source.Document.Changed-=failure;}
        check("partially applied toolbar command rolls back",window.source.Document.Text==before&&window.History.CanUndo==undo&&window.History.CanRedo==redo,"buffer/history unchanged");
        foreach(bool live in new[]{false,true,false,true}){
            window.SetLiveMode(live);LongLineBench.Idle();VerifyToolbarSizeInk(window,check,"after mode switch "+live);
            window.LoadFileCore(loadFile);window.SetMode(true);LongLineBench.Idle();VerifyToolbarSizeInk(window,check,"after file load "+live);
            window.SetText("font number");window.SetMode(true);LongLineBench.Idle();
            foreach(bool darkMode in new[]{false,true}){
                window.SetDark(darkMode);window.ApplyLanguage();LongLineBench.Idle();window.sizes.ApplyTemplate();
                var field=window.sizes.Template.FindName("PART_EditableTextBox",window.sizes) as System.Windows.Controls.TextBox;
                check("size number survives language/theme "+live+"/"+darkMode,field!=null&&field.Visibility==System.Windows.Visibility.Visible&&field.Text==window.sizes.Text&&field.Text.Contains("px")&&field.ActualWidth>20,"number="+window.sizes.Text);
                VerifyToolbarSizeInk(window,check,"theme/language "+live+"/"+darkMode);
            }
            /* Only the source surface exists now: a Basic/Live view switch must leave it pickable. */
            window.source.Select(0,4);
            window.sizes.IsDropDownOpen=true;LongLineBench.Idle();bool opened=window.sizes.IsDropDownOpen;window.sizes.SelectedItem="24 px";window.sizes.IsDropDownOpen=false;LongLineBench.Idle();
            bool picked=window.source.Document.Text.Contains("font-size:24px");
            check("size dropdown opens and applies "+live,opened&&picked,"opened="+opened+", applied="+picked);
            window.sizes.Text="28";window.FontSizeKeyDown(window.sizes,new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window),0,System.Windows.Input.Key.Enter){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent});LongLineBench.Idle();
            check("size keyboard entry applies "+live,window.source.Document.Text.Contains("font-size:28px"),"Enter=28");
            window.FlushDocumentChanges();string unchanged=window.text;window.sizes.Text="99";window.FontSizeKeyDown(window.sizes,new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window),0,System.Windows.Input.Key.Escape){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent});LongLineBench.Idle();
            check("size Escape leaves document unchanged "+live,window.text==unchanged,"unchanged="+(window.text==unchanged));
        }
        window.SetDark(false);
        var fixture=new System.Text.StringBuilder();for(int i=0;i<50;i++)fixture.Append("body ").Append(i).Append('\n');fixture.Append("| a | b |\n|---|---|\n| x | y |\n\nafter table\n");for(int i=0;i<50;i++)fixture.Append("tail ").Append(i).Append('\n');
        window.SetText(fixture.ToString());LongLineBench.Idle();var tableLine=window.source.Document.GetLineByNumber(53);var after=window.source.Document.GetLineByNumber(55);
        window.source.Select(tableLine.Offset+2,0);window.source.ScrollToLine(49);LongLineBench.Idle();window.UpdateTableTools();LongLineBench.Idle();
        check("table toolbar appears in table",window.tableToolsHost.Visibility==System.Windows.Visibility.Visible,"shown="+window.tableToolsHost.Visibility);
        window.source.Select(after.Offset+2,0);window.source.ScrollToVerticalOffset(window.source.VerticalOffset+100);LongLineBench.Idle();
        check("table toolbar leaves table once",window.tableToolsHost.Visibility==System.Windows.Visibility.Collapsed,"hidden="+window.tableToolsHost.Visibility);
        int toggles=0;var descriptor=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.UIElement.VisibilityProperty,typeof(System.Windows.Controls.Border));EventHandler changed=delegate{toggles++;};descriptor.AddValueChanged(window.tableToolsHost,changed);
        try{
            window.source.Select(tableLine.Offset+2,0);LongLineBench.Idle();window.source.Select(after.Offset+2,0);LongLineBench.Idle();
            double offset=window.source.VerticalOffset;for(int i=0;i<8;i++){window.UpdateTools();window.UpdateLayout();LongLineBench.Idle();}
            check("table toolbar has no layout feedback",toggles==2&&window.tableToolsHost.Visibility==System.Windows.Visibility.Collapsed&&Math.Abs(window.source.VerticalOffset-offset)<1,"visibility changes="+toggles+", scroll drift="+Math.Abs(window.source.VerticalOffset-offset));
        }finally{descriptor.RemoveValueChanged(window.tableToolsHost,changed);}
    }
    static void VerifyToolbarSizeInk(MainWindow window,Action<string,bool,string> check,string step){
        window.UpdateLayout();var combo=window.sizes;combo.ApplyTemplate();var field=combo.Template.FindName("PART_EditableTextBox",combo) as System.Windows.Controls.TextBox;int count=0;
        if(field!=null&&field.ActualWidth>0&&field.ActualHeight>0){
            int width=(int)Math.Ceiling(combo.ActualWidth),height=(int)Math.Ceiling(combo.ActualHeight);var visual=new System.Windows.Media.DrawingVisual();
            using(var drawing=visual.RenderOpen()){drawing.DrawRectangle((System.Windows.Media.Brush)window.Resources["Ribbon"],null,new System.Windows.Rect(0,0,width,height));drawing.DrawRectangle(new System.Windows.Media.VisualBrush(combo),null,new System.Windows.Rect(0,0,width,height));}
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(visual);var pixels=new byte[width*height*4];bitmap.CopyPixels(pixels,width*4,0);
            var rect=field.TransformToAncestor(combo).TransformBounds(new System.Windows.Rect(0,0,field.ActualWidth,field.ActualHeight));
            for(int y=Math.Max(1,(int)Math.Ceiling(rect.Top)+2);y<Math.Min(height-1,(int)Math.Floor(rect.Bottom)-2);y++)for(int x=Math.Max(1,(int)Math.Ceiling(rect.Left)+2);x<Math.Min(width-1,(int)Math.Floor(rect.Right)-2);x++){int at=(y*width+x)*4;if(window.dark?pixels[at]>140&&pixels[at+1]>140&&pixels[at+2]>140:pixels[at]<160&&pixels[at+1]<160&&pixels[at+2]<160)count++;}
        }
        double number;bool numeric=Double.TryParse((combo.Text??"").Replace("px","").Trim(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out number);
        check("size displayed ink "+step,numeric&&field!=null&&field.IsVisible&&field.Text==combo.Text&&count>=10,"text=["+combo.Text+"], contrasting text pixels="+count+", dark="+window.dark);
    }
    static void WaitToolbarRegression(int milliseconds){
        var frame=new System.Windows.Threading.DispatcherFrame();var timer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);LongLineBench.Idle();
    }
    // 2.3.1 random format-abuse find: a character typed on a stacked hidden closing run (`*기울임***`, the
    // italic closer then the outer bold closer) landed between the two closers and broke the pairs. It must go to
    // the run's inner edge (inside every pair, FormatAt's answer): every pair survives and the character is drawn
    // with the style the toolbar showed at the caret before the key. Basic view (markers hidden), caret at line end.
    // VM real-input find (11-07): Backspace on the space between "_ab_" and "c" (no marker next to the caret)
    // left "_ab_c", an underscore pair against a word character that no longer parses. Basic view rewrites the
    // pair with "*" in the same update; Delete from the other side and plain text without "_" stay ordinary.
    static void VerifyUnderscoreSpaceDelete(MainWindow window,Action<string,bool,string> check){
        bool oldHide=hideMarkers;
        try{
            hideMarkers=true;window.UpdateLivePreview();LongLineBench.Idle();
            string[] fixtures={"_ab_ c","x _ab_ c","a b"};
            string[] expected={"*ab*c","x *ab*c","ab"};
            bool[] backs={true,false,true};
            for(int i=0;i<fixtures.Length;i++){
                window.SetText(fixtures[i]);window.SetMode(true);LongLineBench.Idle();
                int space=fixtures[i].LastIndexOf(' ');
                window.source.Select(backs[i]?space+1:space,0);LongLineBench.Idle();
                var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,backs[i]?System.Windows.Input.Key.Back:System.Windows.Input.Key.Delete){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
                window.source.TextArea.RaiseEvent(args);
                if(!args.Handled){var down=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,backs[i]?System.Windows.Input.Key.Back:System.Windows.Input.Key.Delete){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent};window.source.TextArea.RaiseEvent(down);}
                LongLineBench.Idle();
                string text=window.source.Document.Text;
                window.Undo();LongLineBench.Idle();
                bool restored=window.source.Document.Text==fixtures[i];
                check("underscore pair after a space delete: "+fixtures[i]+(backs[i]?" Backspace":" Delete"),text==expected[i]&&restored,"got=["+text+"] want=["+expected[i]+"] undo="+restored);
            }
        }finally{hideMarkers=oldHide;window.UpdateLivePreview();LongLineBench.Idle();}
    }
    // VM real-input note (11-07): Enter inside bold, Ctrl+Z, Enter again left the second line plain. The second
    // Enter must split the pair exactly like the first, in both views.
    static void VerifyEnterUndoEnter(MainWindow window,Action<string,bool,string> check){
        bool oldHide=hideMarkers;
        try{
            for(int round=0;round<4;round++){
                bool hide=round%2==0;
                hideMarkers=hide;window.UpdateLivePreview();LongLineBench.Idle();
                // Rounds 2-3 follow the VM run: a longer document and a first Enter one character earlier.
                string fixture=round<2?"a **굵게글자** b":"a **굵게글자** b\n\na **굵Z자** b\n\n- 목록 **굵게** 끝\n";
                window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();
                int at=fixture.IndexOf("글",StringComparison.Ordinal);
                int firstAt=round<2?at:fixture.IndexOf("게",StringComparison.Ordinal);
                Action enter=delegate{
                    var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,System.Windows.Input.Key.Enter){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
                    window.source.TextArea.RaiseEvent(args);
                    if(!args.Handled){var down=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,System.Windows.Input.Key.Enter){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent};window.source.TextArea.RaiseEvent(down);}
                    LongLineBench.Idle();
                };
                window.source.Select(firstAt,0);LongLineBench.Idle();
                enter();
                string first=window.source.Document.Text;
                window.Undo();LongLineBench.Idle();
                string undone=window.source.Document.Text;int caret=window.source.CaretOffset;
                window.source.Select(at,0);LongLineBench.Idle();
                enter();
                string second=window.source.Document.Text;
                string view=(hide?"basic":"live")+(round<2?"":" long doc, first Enter elsewhere");
                string wantSecond=fixture.Substring(0,at)+"**\n**"+fixture.Substring(at);
                // Drawn weight of "글" on the second line, from the visual line on screen (not rebuilt here), on and off the caret line.
                var doc=window.source.Document;var second2=doc.LineCount>=2?doc.GetLineByNumber(2):null;
                string onCaret=second2==null?"?":DrawnWeight(window,second2,second2.Offset+2);
                window.source.Select(1,0);LongLineBench.Idle();
                string offCaret=second2==null?"?":DrawnWeight(window,second2,second2.Offset+2);
                string parsed=second2==null?"?":StackedDrawnFlags(window.livePreview.Info(second2),2);
                check(view+" Enter, Undo, Enter draws the second line bold",offCaret=="Bold"&&parsed.StartsWith("B",StringComparison.Ordinal)&&(hide?onCaret=="Bold":true),
                    "onCaret="+onCaret+" offCaret="+offCaret+" parsed="+parsed);
                check(view+" Enter, Undo, Enter splits the pair again",undone==fixture&&second==wantSecond&&(round>=2||first==wantSecond),
                    "first=["+first.Replace("\n","|")+"] undone=["+undone+"] caretAfterUndo="+caret+"/"+at+" second=["+second.Replace("\n","|")+"]");
            }
        }finally{hideMarkers=oldHide;window.UpdateLivePreview();LongLineBench.Idle();}
    }
    // VM real-input find (11-07): live view Ctrl+Backspace at "a **굵게글자|** b" left "a **** b". A word delete
    // that takes a pair's whole text takes the pair in both views; a word that is only part of it keeps the pair.
    static void VerifyWordDeleteEmptiesPair(MainWindow window,Action<string,bool,string> check){
        bool oldHide=hideMarkers;
        try{
            foreach(bool hide in new[]{true,false}){
                hideMarkers=hide;window.UpdateLivePreview();LongLineBench.Idle();
                string view=hide?"basic":"live";
                string[] fixtures={"a **굵게글자** b","a **굵게 글자** b","a `코드` b","a **글자 굵게** b"};
                string[] anchors={"** b","** b","` b","글자"};
                string[] expected={"a  b","a **굵게**  b","a  b","a **굵게** b"};
                bool[] forward={false,false,false,true};
                for(int i=0;i<fixtures.Length;i++){
                    window.SetText(fixtures[i]);window.SetMode(true);LongLineBench.Idle();
                    window.source.Select(forward[i]?fixtures[i].IndexOf(anchors[i],StringComparison.Ordinal):fixtures[i].LastIndexOf(anchors[i],StringComparison.Ordinal),0);LongLineBench.Idle();
                    (forward[i]?System.Windows.Documents.EditingCommands.DeleteNextWord:System.Windows.Documents.EditingCommands.DeletePreviousWord).Execute(null,window.source.TextArea);LongLineBench.Idle();
                    string text=window.source.Document.Text;
                    window.Undo();LongLineBench.Idle();
                    bool restored=window.source.Document.Text==fixtures[i];
                    check(view+" Ctrl+Backspace word delete: "+fixtures[i],text==expected[i]&&restored,"got=["+text+"] want=["+expected[i]+"] undo="+restored);
                }
            }
        }finally{hideMarkers=oldHide;window.UpdateLivePreview();LongLineBench.Idle();}
    }
    // Code-review finds (2.3.1, not yet reproduced): selection edits that cut part of a pair's text must keep the
    // pair balanced and never leave a blank just inside a closer / after an opener. A-D: Delete/Backspace/Cut/typing
    // a space over a selection; E: Ctrl+Backspace over nested pairs; F: paste right after a closing backtick in a paragraph.
    static void SelectionPrepare(MainWindow window,string fixture,int start,int length){
        window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();window.source.Select(start,length);LongLineBench.Idle();
    }
    static string selectionKeyTrace="";
    static void SelectionKey(MainWindow window,System.Windows.Input.Key key){
        var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,key){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
        window.source.TextArea.RaiseEvent(args);
        selectionKeyTrace="handled="+args.Handled+" selectionAfterPreview="+window.source.SelectionStart+"+"+window.source.SelectionLength+" text=["+window.source.Document.Text+"]";
        // A synthetic KeyDown does not run the editor's delete command (the session's PreviewExecuted rules), so the
        // command the key is bound to is executed directly, as the other probes do (FormatAbuseProbe.DeleteOnce).
        if(!args.Handled)(key==System.Windows.Input.Key.Back?System.Windows.Documents.EditingCommands.Backspace:System.Windows.Documents.EditingCommands.Delete).Execute(null,window.source.TextArea);
        LongLineBench.Idle();
    }
    static void SelectionType(MainWindow window,string text){
        window.source.TextArea.PerformTextInput(text);LongLineBench.Idle();
    }
    static bool SelectionBalanced(string text){
        foreach(string marker in new[]{"**","~~","`"}){int count=0,at=0;while((at=text.IndexOf(marker,at,StringComparison.Ordinal))>=0){count++;at+=marker.Length;}if(count%2!=0)return false;}
        return true;
    }
    static void VerifySelectionMarkerEdits(MainWindow window,Action<string,bool,string> check){
        bool oldHide=hideMarkers;
        try{
            foreach(bool hide in new[]{true,false}){
                hideMarkers=hide;window.UpdateLivePreview();LongLineBench.Idle();
                string view=hide?"basic":"live";
                const string bold="**a b**";
                string got;
                // Live view keeps the caret line as raw source: Delete/Backspace/Cut and typing over a selection are plain
                // source edits there (format-matrix rule), so only the basic view is expected to keep the pair parsing.
                string wantLast=hide?"**a** ":"**a **",wantFirst=hide?" **b**":"** b**";
                // A: delete the last word of a pair's text; the blank moves outside the closer.
                SelectionPrepare(window,bold,bold.IndexOf('b'),1);SelectionKey(window,System.Windows.Input.Key.Delete);got=window.source.Document.Text;
                check(view+" selection Delete of the last word: "+bold,got==wantLast,"got=["+got+"] want=["+wantLast+"] key: "+selectionKeyTrace);
                SelectionPrepare(window,bold,bold.IndexOf('b'),1);SelectionKey(window,System.Windows.Input.Key.Back);got=window.source.Document.Text;
                check(view+" selection Backspace of the last word: "+bold,got==wantLast,"got=["+got+"] want=["+wantLast+"] key: "+selectionKeyTrace);
                // Cut shares this rule (HiddenMarkerDelete) but writes the system clipboard, so this host probe never runs it.
                // B: delete the first word; the blank moves outside the opener.
                SelectionPrepare(window,bold,bold.IndexOf('a'),1);SelectionKey(window,System.Windows.Input.Key.Delete);got=window.source.Document.Text;
                check(view+" selection Delete of the first word: "+bold,got==wantFirst,"got=["+got+"] want=["+wantFirst+"] key: "+selectionKeyTrace);
                // C: type a space over the last character ("**ab**": b is offset 3).
                string wantC=hide?"**a** ":"**a **",wantCc=hide?"**a** c":"**a c**";int wantCaret=hide?6:4;
                SelectionPrepare(window,"**ab**",3,1);SelectionType(window," ");got=window.source.Document.Text;int caret=window.source.CaretOffset;
                check(view+" space typed over the last character: **ab**",got==wantC&&caret==wantCaret,"got=["+got+"] caret="+caret+" want=["+wantC+"] caret="+wantCaret);
                SelectionType(window,"c");got=window.source.Document.Text;
                check(view+" letter after that space: **ab**",got==wantCc,"got=["+got+"] want=["+wantCc+"]");
                // D: the selection includes the closer.
                string wantD=hide?"**a** ":"**a ",wantDc=hide?"**a** c":"**a c";int wantDCaret=hide?6:4;
                SelectionPrepare(window,"**ab**",3,3);SelectionType(window," ");got=window.source.Document.Text;caret=window.source.CaretOffset;
                check(view+" space typed over the last character and the closer: **ab**",got==wantD&&caret==wantDCaret,"got=["+got+"] caret="+caret+" want=["+wantD+"] caret="+wantDCaret);
                SelectionType(window,"c");got=window.source.Document.Text;
                check(view+" letter after that space (closer selected): **ab**",got==wantDc,"got=["+got+"] want=["+wantDc+"]");
                // E: Ctrl+Backspace at the end of nested pairs. Rule: no literal marker, pairs balanced; expected text derived from the rule.
                const string nested="**a ~~b c~~**";
                SelectionPrepare(window,nested,nested.Length,0);
                System.Windows.Documents.EditingCommands.DeletePreviousWord.Execute(null,window.source.TextArea);LongLineBench.Idle();
                got=window.source.Document.Text;
                check(view+" Ctrl+Backspace through nested pairs: "+nested,got=="**a ~~b~~** "&&SelectionBalanced(got),"got=["+got+"] balanced="+SelectionBalanced(got)+" want=[**a ~~b~~** ]");
                // E2: the caret between the stacked closers (basic view: a click past the line end lands there, VM 11-07 rk11).
                SelectionPrepare(window,nested,nested.Length-2,0);
                System.Windows.Documents.EditingCommands.DeletePreviousWord.Execute(null,window.source.TextArea);LongLineBench.Idle();
                got=window.source.Document.Text;
                check(view+" Ctrl+Backspace between stacked closers: "+nested,got=="**a ~~b~~** ","got=["+got+"] want=[**a ~~b~~** ]");
                // F: paste right after a closing backtick of a paragraph lands outside the code span.
                SelectionPrepare(window,"`a`",3,0);
                var data=new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText,"X");
                MarkdownClipboard.Set(data,"X");window.PasteData(data,false);window.FlushDocumentChanges();LongLineBench.Idle();
                got=window.source.Document.Text;
                check(view+" paste after a closing backtick in a paragraph",got=="`a`X","got=["+got+"] want=[`a`X]");
            }
        }finally{hideMarkers=oldHide;window.UpdateLivePreview();LongLineBench.Idle();}
    }
    // Code-review finds on table edges (2.3.1, not yet reproduced): 1 Enter in a cell's code span next to a backtick of the
    // content, 2 Enter / multi-line paste over a selection inside a cell's code span, 3 Delete over a selection that runs
    // from a whole table into a paragraph's pair, 4 (log only) plain paste over a selection from a paragraph into a row.
    // Rule for 1-2: both pieces stay real code spans (the drawn code text is exactly the two halves, no "<br>" drawn inside).
    static void TableEnterKey(MainWindow window){
        var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,System.Windows.Input.Key.Enter){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
        window.source.TextArea.RaiseEvent(args);
        if(!args.Handled){var down=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,System.Windows.Input.Key.Enter){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent};window.source.TextArea.RaiseEvent(down);}
        window.FlushDocumentChanges();LongLineBench.Idle();
    }
    // The text the row draws with the code style, one string per code span (hidden marker and padding characters left out).
    static string DrawnCodeRuns(MainWindow window,int lineNumber){
        var line=window.source.Document.GetLineByNumber(lineNumber);
        string row=window.source.Document.GetText(line);
        var info=window.livePreview==null?null:window.livePreview.Info(line);
        if(info==null)return "(no parse)";
        var runs=new System.Text.StringBuilder();var current=new System.Text.StringBuilder();bool open=false;
        for(int i=0;i<row.Length;i++){
            bool hidden=false;
            foreach(var run in info.Replacements)if(run.Kind==MarkdownLivePreview.ReplacementKind.Hidden&&run.Length>0&&i>=run.Start&&i<run.Start+run.Length){hidden=true;break;}
            bool code=StackedDrawnFlags(info,i)[3]=='C';
            if(code){if(!hidden){current.Append(row[i]);open=true;}}
            else if(open){runs.Append('[').Append(current).Append(']');current.Length=0;open=false;}
        }
        if(open)runs.Append('[').Append(current).Append(']');
        return runs.ToString();
    }
    static void TableCodeCase(MainWindow window,Action<string,bool,string> check,string name,string row,string anchor,int anchorShift,int selectLength,string pasted,string wantRuns){
        const string head="| h | i |\n| --- | --- |\n";
        string fixture=head+row+"\n";
        window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();
        window.source.Select(head.Length+row.IndexOf(anchor,StringComparison.Ordinal)+anchorShift,selectLength);LongLineBench.Idle();
        if(pasted==null)TableEnterKey(window);
        else{
            var data=new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText,pasted);
            MarkdownClipboard.Set(data,pasted);window.PasteData(data,false);window.FlushDocumentChanges();LongLineBench.Idle();
        }
        string text=window.source.Document.Text;
        var line=window.source.Document.GetLineByNumber(3);
        string got=window.source.Document.GetText(line);
        string runs=DrawnCodeRuns(window,3);
        int breaks=0,at=0;while((at=got.IndexOf("<br>",at,StringComparison.Ordinal))>=0){breaks++;at+=4;}
        bool cellsKept=window.source.Document.LineCount==fixture.Split('\n').Length&&got.StartsWith("| ",StringComparison.Ordinal)&&got.EndsWith(" | e |",StringComparison.Ordinal);
        check(name,runs==wantRuns&&breaks==1&&cellsKept,"row=["+got+"] drawnCode="+runs+" want="+wantRuns+" breaks="+breaks+" cellsKept="+cellsKept);
    }
    static void VerifyTableEdgeEdits(MainWindow window,Action<string,bool,string> check){
        bool oldHide=hideMarkers;
        try{
            foreach(bool hide in new[]{true,false}){
                hideMarkers=hide;window.UpdateLivePreview();LongLineBench.Idle();
                string view=hide?"basic":"live";
                // 1: Enter next to a backtick that belongs to the span's content ("``a`b``": content a`b).
                TableCodeCase(window,check,view+" cell Enter before an inner backtick keeps both code spans","| ``a`b`` | e |","a`b",1,0,null,"[a][`b]");
                TableCodeCase(window,check,view+" cell Enter after an inner backtick keeps both code spans","| ``a`b`` | e |","a`b",2,0,null,"[a`][b]");
                TableCodeCase(window,check,view+" cell Enter in the middle of a plain code span (control)","| `ab` | e |","ab",1,0,null,"[a][b]");
                // 2: a selection inside the span is replaced by the break, the span splits around it.
                TableCodeCase(window,check,view+" cell Enter over a selection inside a code span","| `a b c` | e |","b",0,1,null,"[a ][ c]");
                TableCodeCase(window,check,view+" cell multi-line paste into a code span","| `ab` | e |","ab",1,0,"x\ny","[ax][yb]");
                TableCodeCase(window,check,view+" cell multi-line paste over a selection inside a code span","| `a b c` | e |","b",0,1,"x\ny","[a x][y c]");
                // 3: Delete over whole table + the start of a paragraph's pair; the pair keeps balanced markers (basic view).
                // The live view edits the caret line as source, so it is the plain raw delete there.
                bool balancedTail=true;
                {
                    const string tail="| a | b |\n| --- | --- |\n| c | d |\n\n**a b**";
                    int cut=tail.IndexOf("b**",StringComparison.Ordinal);
                    SelectionPrepare(window,tail,0,cut);SelectionKey(window,System.Windows.Input.Key.Delete);
                    string got=window.source.Document.Text;
                    balancedTail=SelectionBalanced(got);
                    bool ok=hide?(balancedTail&&got.Trim()=="**b**"):got==tail.Substring(cut);
                    check(view+" Delete from a table into a paragraph pair keeps the pair balanced",ok,"got=["+got+"] balanced="+balancedTail+" key: "+selectionKeyTrace);
                    SelectionPrepare(window,tail,0,cut);SelectionKey(window,System.Windows.Input.Key.Back);
                    got=window.source.Document.Text;
                    ok=hide?(SelectionBalanced(got)&&got.Trim()=="**b**"):got==tail.Substring(cut);
                    check(view+" Backspace from a table into a paragraph pair keeps the pair balanced",ok,"got=["+got+"] key: "+selectionKeyTrace);
                    // The mirror: from inside a paragraph's pair through the end of a table.
                    const string front="**a b**\n\n| a | b |\n| --- | --- |\n| c | d |";
                    int from=front.IndexOf("b**",StringComparison.Ordinal);
                    SelectionPrepare(window,front,from,front.Length-from);SelectionKey(window,System.Windows.Input.Key.Delete);
                    got=window.source.Document.Text;
                    // Exact: a parity count alone would also pass "a****".
                    ok=hide?got=="**a** ":got==front.Substring(0,from);
                    check(view+" Delete from a paragraph pair through a table end keeps the pair balanced",ok,"got=["+got+"] key: "+selectionKeyTrace);
                }
                // 4 (log only): plain paste over a selection from a paragraph into a table row; what the row becomes is reported.
                {
                    const string mixed="para text\n\n| a | b |\n| --- | --- |\n| c | d |\n";
                    window.SetText(mixed);window.SetMode(true);LongLineBench.Idle();
                    int to=mixed.IndexOf("| c",StringComparison.Ordinal)+3;
                    window.source.Select(2,to-2);LongLineBench.Idle();
                    var data=new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText,"X");
                    MarkdownClipboard.Set(data,"X");window.PasteData(data,false);window.FlushDocumentChanges();LongLineBench.Idle();
                    check(view+" plain paste over a paragraph-to-row selection (log only)",true,"text=["+window.source.Document.Text.Replace("\n","\\n")+"]");
                }
            }
        }finally{hideMarkers=oldHide;window.UpdateLivePreview();LongLineBench.Idle();}
    }
    static void VerifyStackedCloserTyping(MainWindow window,Action<string,bool,string> check){
        bool oldHide=hideMarkers;
        try{
            hideMarkers=true;window.UpdateLivePreview();LongLineBench.Idle();
            string[] lines={"> 번****굵은 붙여넣기** 와 *기울임***","> 번****굵은 붙여넣기** 와 *기울임***","**a *b***","**a *b***","> 번****굵은 붙여넣기** 와 *기울임***","> 번****굵은 붙여넣기** 와 *기울임***"};int[] back={0,0,0,0,2,2};
            string[] typed={"`","a","c","`","`","a"};
            string[] names={"abuse line backtick","abuse line letter","stacked closer letter","stacked closer backtick","abuse line backtick after paste","abuse line letter after paste"};
            for(int i=0;i<lines.Length;i++){
                window.SetText(lines[i]);window.SetMode(true);LongLineBench.Idle();
                var session=window.livePreview;
                window.source.CaretIndex=window.source.Document.TextLength-back[i];LongLineBench.Idle();
                var lineBefore=window.source.Document.GetLineByNumber(1);
                var before=session.Info(lineBefore);
                int relative=window.source.CaretIndex-lineBefore.Offset;
                int inside=MarkdownLivePreview.Session.HiddenCloseInside(before,relative+back[i]);
                bool fb,fi,fs,fc;int fh;session.FormatAt(window.source.CaretIndex,out fb,out fi,out fs,out fc,out fh);string flags=(fb?"B":"-")+(fi?"I":"-")+(fs?"S":"-")+(fc?"C":"-");
                int emphasisBefore=StackedEmphasisCount(before),hiddenBefore=StackedHiddenLength(before);
                string source=window.source.Document.Text;
                window.source.TextArea.PerformTextInput(typed[i]);LongLineBench.Idle();
                string result=window.source.Document.Text;
                int at=0;while(at<source.Length&&at<result.Length&&source[at]==result[at])at++;
                bool oneChar=result.Length==source.Length+1&&result.Remove(at,1)==source;
                var after=window.livePreview.Info(window.source.Document.GetLineByNumber(1));
                int emphasisAfter=StackedEmphasisCount(after),hiddenAfter=StackedHiddenLength(after);
                string drawn=oneChar?StackedDrawnFlags(after,at):"?";
                check("stacked closer typing keeps every pair: "+names[i],oneChar&&(inside<0||at==inside)&&emphasisAfter==emphasisBefore&&hiddenAfter==hiddenBefore,"result="+result+", spot="+at+", inside="+inside+", emphasis="+emphasisBefore+"/"+emphasisAfter+", hidden="+hiddenBefore+"/"+hiddenAfter+", markers="+StackedMarkerList(before));
                check("stacked closer typing takes the toolbar style: "+names[i],oneChar&&drawn==flags,"toolbar="+flags+", drawn="+drawn);
                window.Undo();LongLineBench.Idle();
            }
        }finally{hideMarkers=oldHide;window.UpdateLivePreview();LongLineBench.Idle();}
    }
    static int StackedEmphasisCount(MarkdownLivePreview.LineInfo info){
        int count=0;foreach(var span in MarkdownInlineSpans.Build(info,info.Text))if(span.Kind==MarkdownInlineSpans.SpanKind.Emphasis)count++;
        return count;
    }
    static string StackedMarkerList(MarkdownLivePreview.LineInfo info){
        var list=new System.Text.StringBuilder();foreach(var run in info.Replacements)if(run.Kind==MarkdownLivePreview.ReplacementKind.Hidden&&!run.Prefix&&run.Length>0)list.Append(run.Start).Append('+').Append(run.Length).Append(' ');
        return list.ToString();
    }
    static int StackedHiddenLength(MarkdownLivePreview.LineInfo info){
        int total=0;foreach(var run in info.Replacements)if(run.Kind==MarkdownLivePreview.ReplacementKind.Hidden&&!run.Prefix&&run.Length>0)total+=run.Length;
        return total;
    }
    // Drawn bold/italic/strike/code of one source character, in the toolbar's B-I-S-C notation.
    // The font weight the visual line currently on screen uses at one document offset ("none" when the line is
    // not built, "?" when no element covers the offset). Reads the existing visual line, so a stale one shows.
    static string DrawnWeight(MainWindow window,ICSharpCode.AvalonEdit.Document.DocumentLine line,int offset){
        var view=window.source.TextArea.TextView;
        var visual=view.GetVisualLine(line.LineNumber);
        if(visual==null)return "none";
        int relative=offset-visual.FirstDocumentLine.Offset;
        foreach(var element in visual.Elements){
            if(relative<element.RelativeTextOffset||relative>=element.RelativeTextOffset+element.DocumentLength)continue;
            if(element.TextRunProperties==null||element.DocumentLength==0)continue;
            return element.TextRunProperties.Typeface.Weight==System.Windows.FontWeights.Bold?"Bold":element.TextRunProperties.Typeface.Weight.ToString();
        }
        return "?";
    }
    static string StackedDrawnFlags(MarkdownLivePreview.LineInfo info,int relative){
        bool bold=false,italic=false,strike=false,code=false;
        foreach(var part in info.Styles){
            if(relative<part.Start||relative+1>part.Start+part.Length)continue;
            bold|=(part.Style&MarkdownLivePreview.Style.Bold)!=0;italic|=(part.Style&MarkdownLivePreview.Style.Italic)!=0;
            strike|=(part.Style&MarkdownLivePreview.Style.Strike)!=0;code|=(part.Style&MarkdownLivePreview.Style.Code)!=0;
        }
        return (bold?"B":"-")+(italic?"I":"-")+(strike?"S":"-")+(code?"C":"-");
    }
    // 2.3.1 random format-abuse find: a formatted paste that carries the same emphasis as the pair around the
    // caret ("**번글자**" + Markdown "**굵은 붙여넣기**") wrote markers that collided with the pair's own, so the
    // parser paired the stars wrongly. The pasted text now drops the markers of the kinds that already enclose it
    // (LiveMarkdownCommands.StripEnclosingStyles) and keeps its other styles. Generated text only; the paste goes
    // through window.PasteData with MDNote's own Markdown format, the system clipboard is never touched.
    static void VerifyPasteInsideSameStyle(MainWindow window,Action<string,bool,string> check){
        bool oldHide=hideMarkers;
        const string table="| h | i |\n| --- | --- |\n| **번글자** | 끝 |\n";
        try{
            foreach(bool hide in new[]{true,false}){
                hideMarkers=hide;window.UpdateLivePreview();LongLineBench.Idle();
                string view=hide?"basic":"live";
                PasteSameStyleCase(window,check,view+" bold into bold","**번글자**","번",false,"**굵은 붙여넣기**","**번굵은 붙여넣기글자**",1,new[]{"번","굵","글"},new[]{"B---","B---","B---"});
                PasteSameStyleCase(window,check,view+" bold with italic inside into bold","**번글자**","번",false,"**a *b* c**","**번a *b* c글자**",2,new[]{"a","b","글"},new[]{"B---","BI--","B---"});
                PasteSameStyleCase(window,check,view+" italic into bold stays","**번글자**","번",false,"*x*","**번*x*글자**",2,new[]{"x","글"},new[]{"BI--","B---"});
                PasteSameStyleCase(window,check,view+" bold into plain text unchanged","번글자","번",false,"**굵은 붙여넣기**","번**굵은 붙여넣기**글자",1,new[]{"번","굵","글"},new[]{"----","B---","----"});
                PasteSameStyleCase(window,check,view+" bold into bold table cell",table,"번",false,"**굵은 붙여넣기**","| **번굵은 붙여넣기글자** | 끝 |",1,new[]{"번","굵","글"},new[]{"B---","B---","B---"});
                PasteSameStyleCase(window,check,view+" bold over a selection inside bold","**번글자**","글",true,"**굵**","**번굵자**",1,new[]{"번","굵","자"},new[]{"B---","B---","B---"});
                PasteSameStyleCase(window,check,view+" strike into strike","~~번글자~~","번",false,"~~굵~~","~~번굵글자~~",1,new[]{"번","굵","글"},new[]{"--S-","--S-","--S-"});
                PasteSameStyleCase(window,check,view+" italic into italic","*번글자*","번",false,"*x*","*번x글자*",1,new[]{"번","x","글"},new[]{"-I--","-I--","-I--"});
                PasteSameStyleCase(window,check,view+" bold into italic stays","*번글자*","번",false,"**x**","*번**x**글자*",2,new[]{"번","x","글"},new[]{"-I--","BI--","-I--"});
                PasteSameStyleCase(window,check,view+" literal star in the paste untouched","**번글자**","번",false,"a * b","**번a * b글자**",1,new[]{"a","글"},new[]{"B---","B---"});
            }
        }finally{hideMarkers=oldHide;window.UpdateLivePreview();LongLineBench.Idle();}
        // Review fix: the other pairs must survive at the shifted offsets ("_" cannot close inside "b_c", so "*b_c*" stays the italic).
        string kept=StripPastedPairs("**_a** *b_c*",true,false,false);
        check("strip keeps the other pair at its shifted place",kept=="_a *b_c*","got=["+kept+"]");
        string repaired=StripPastedPairs("**a** b*",true,false,false);
        check("strip with a literal star left over",repaired=="a b*","got=["+repaired+"]");
        string plain=StripPastedPairs("**x** *y*",true,false,false);
        check("strip drops the same-kind pair and keeps the other",plain=="x *y*","got=["+plain+"]");
    }
    // A pasted text that is one complete span ("**`a`**", bold around inline code) at a table cell's text end is not a
    // character typed next to a closer (rule H10 stays for typing): it is inserted as-is where the caret is, after the
    // cell's own closer, and draws as its own bold+code span, in both views. Before the fix the basic view snapped the
    // caret into a closing "**" and StripEnclosingStyles dropped the paste's own markers ("| **x`a`** |").
    static void VerifyCellSpanPaste(MainWindow window,Action<string,bool,string> check){
        bool oldHide=hideMarkers;
        try{
            foreach(bool hide in new[]{true,false}){
                hideMarkers=hide;window.UpdateLivePreview();LongLineBench.Idle();
                string view=hide?"basic":"live";
                PasteSameStyleCase(window,check,view+" bold code into a cell after bold","| h | i |\n| --- | --- |\n| **x** | 끝 |\n","**x**",false,"**`a`**","| **x****`a`** | 끝 |",2,new[]{"x","a"},new[]{"B---","B--C"});
                PasteSameStyleCase(window,check,view+" bold code into a cell after code","| h | i |\n| --- | --- |\n| `x` | 끝 |\n","`x`",false,"**`a`**","| `x`**`a`** | 끝 |",1,new[]{"x","a"},new[]{"---C","B--C"});
                PasteSameStyleCase(window,check,view+" bold code into a cell after plain text","| h | i |\n| --- | --- |\n| x | 끝 |\n","x",false,"**`a`**","| x**`a`** | 끝 |",1,new[]{"x","a"},new[]{"----","B--C"});
            }
        }finally{hideMarkers=oldHide;window.UpdateLivePreview();LongLineBench.Idle();}
    }
    // One case: the caret goes right after `anchor` (or the anchor is selected), `pasted` arrives as MDNote Markdown,
    // and the line must read `expected`, with `pairs` emphasis pairs, each probed character drawn with its flags,
    // and one Undo must restore the fixture.
    static void PasteSameStyleCase(MainWindow window,Action<string,bool,string> check,string name,string fixture,string anchor,bool select,string pasted,string expected,int pairs,string[] probes,string[] flags){
        window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();
        int index=fixture.IndexOf(anchor,StringComparison.Ordinal);
        if(select)window.source.Select(index,anchor.Length);else window.source.Select(index+anchor.Length,0);
        LongLineBench.Idle();
        int lineNumber=window.source.Document.GetLineByOffset(index).LineNumber;
        var data=new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText,pasted);
        MarkdownClipboard.Set(data,pasted);
        window.PasteData(data,false);window.FlushDocumentChanges();LongLineBench.Idle();
        var line=window.source.Document.GetLineByNumber(lineNumber);
        string row=window.source.Document.GetText(line);
        var info=window.livePreview==null?null:window.livePreview.Info(line);
        int found=info==null?-1:StackedEmphasisCount(info);
        var drawn=new System.Text.StringBuilder();bool flagsOk=info!=null;
        for(int i=0;i<probes.Length;i++){
            int at=row.IndexOf(probes[i],StringComparison.Ordinal);
            string got=info==null||at<0?"?":StackedDrawnFlags(info,at);
            drawn.Append(probes[i]).Append('=').Append(got).Append(' ');
            if(got!=flags[i])flagsOk=false;
        }
        check("paste inside same style "+name,row==expected&&found==pairs&&flagsOk,"row=["+row+"] want=["+expected+"], pairs="+found+"/"+pairs+", drawn "+drawn);
        window.Undo();LongLineBench.Idle();
        check("paste inside same style undoes in one step "+name,window.source.Document.Text==fixture,"text=["+window.source.Document.Text.Replace("\n","\\n")+"]");
    }
    static void VerifyTableToolbarExitRegression(MainWindow window,Action<string,bool,string> check){
        const string fixture="before table\n\n| a | b |\n|---|---|\n| x | y |\n\nafter table\n\ntail\n";
        foreach(string input in new[]{"caret API","keyboard","click hit-test"}){
            window.SetText(fixture);window.SetMode(true);LongLineBench.Idle();
            var line=window.source.Document.GetLineByNumber(5);window.source.Select(line.Offset+2,0);window.source.ScrollToHome();
            window.UpdateTools();WaitToolbarRegression(100);bool started=window.tableToolsHost.Visibility==System.Windows.Visibility.Visible;
            int changes=0;var descriptor=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(System.Windows.UIElement.VisibilityProperty,typeof(System.Windows.Controls.Border));EventHandler changed=delegate{changes++;};descriptor.AddValueChanged(window.tableToolsHost,changed);
            try{
                if(input=="keyboard"){
                    for(int i=0;i<6&&window.LiveTableCaret;i++){
                        var key=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,System.Windows.Input.Key.Down){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent};window.source.TextArea.RaiseEvent(key);
                    }
                }else{
                    int offset=window.source.Document.GetLineByNumber(7).Offset+2;
                    if(input=="click hit-test"){window.UpdateLayout();var rect=window.source.GetRectFromCharacterIndex(offset);offset=window.source.GetCharacterIndexFromPoint(new System.Windows.Point(rect.Left+1,rect.Top+rect.Height/2),true);}
                    if(offset>=0)window.source.Select(offset,0);
                }
                // Hit-test case uses the real point mapping, not the hardware pointer; MouseUp
                // exercises the installed deferred-visibility release handler without moving it.
                if(input=="click hit-test")window.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.PreviewMouseUpEvent});
                window.UpdateTools();WaitToolbarRegression(500);double offsetAfter=window.source.VerticalOffset;window.UpdateTools();window.UpdateLayout();WaitToolbarRegression(100);
                double drift=Math.Abs(window.source.VerticalOffset-offsetAfter);
                check("table toolbar exit once "+input,started&&changes==1&&window.tableToolsHost.Visibility==System.Windows.Visibility.Collapsed&&drift<1,"started="+started+", visibility changes="+changes+", final="+window.tableToolsHost.Visibility+", post-settle drift="+drift);
            }finally{descriptor.RemoveValueChanged(window.tableToolsHost,changed);}
        }
    }
    // --live-host-probe <outDir>: MainWindow integration with the switch on (generated text only).
    internal static int VerifyLiveHost(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        if(System.Windows.Application.Current==null)new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
        var report=new System.Text.StringBuilder("# LiveHostProbe\n\n");bool all=true;
        Action<string,bool,string> check=delegate(string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n');};
        string file=Path.Combine(outDir,"live-host.md");
        File.WriteAllText(file,"# 제목\n\n본문 **굵게** 문단.\n\n- 항목\n\n| a | b |\n|---|---|\n| 1 | 2 |\n",new System.Text.UTF8Encoding(false));
        MainWindow window=null;
        try{
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};window.Show();window.Left=-20000;
            window.LoadFileCore(file);LongLineBench.Idle();
            check("open uses source surface",window.CodeMode&&window.source.Visibility==System.Windows.Visibility.Visible,"code="+window.CodeMode);
            Program.Snapshot(window,Path.Combine(outDir,"live-host-open.png"));
            check("preview attached",window.LivePreviewAttached&&double.IsNaN(window.source.TextArea.TextView.LineHeight),"attached="+window.LivePreviewAttached);
            check("read-only state follows editing",window.livePreview!=null&&window.livePreview.Options.ReadOnly==(!window.editing),"editing="+window.editing+", readOnly="+(window.livePreview==null?"-":window.livePreview.Options.ReadOnly.ToString()));
            if(window.editing)window.SetMode(false);LongLineBench.Idle();
            check("status shows reading mode",window.status.Text.StartsWith(window.T("읽기"),StringComparison.Ordinal),"status="+window.status.Text.Split(' ')[0]);
            window.SetMode(true);LongLineBench.Idle();
            check("status shows body editing",window.status.Text.StartsWith(window.T("수정"),StringComparison.Ordinal),"status="+window.status.Text.Split(' ')[0]);
            var session=window.livePreview;
            check("edit mode keeps session",session!=null&&window.LivePreviewAttached&&!session.Options.ReadOnly,"readOnly="+(session==null?"-":session.Options.ReadOnly.ToString()));
            window.source.CaretIndex=window.source.Document.GetLineByNumber(3).EndOffset;window.source.TextArea.PerformTextInput("추가");LongLineBench.Idle();
            check("text authority is the buffer",window.text.Contains("문단.추가")&&window.HasUnsavedChanges,"dirty="+window.HasUnsavedChanges);
            window.Undo();LongLineBench.Idle();
            check("undo through app history",!window.text.Contains("추가")&&window.text.Contains("문단."),"len="+window.text.Length);
            window.outline.Visibility=System.Windows.Visibility.Visible;window.sourceOutlineDirty=true;window.RefreshOutline();LongLineBench.Idle();
            var firstItem=window.outline.Items.Count==0?null:window.outline.Items[0] as System.Windows.Controls.TreeViewItem;
            check("outline lists heading",firstItem!=null&&OutlineText(firstItem).Contains("제목"),"items="+window.outline.Items.Count);
            window.source.CaretIndex=window.source.Document.GetLineByNumber(3).EndOffset;window.source.TextArea.PerformTextInput("본");LongLineBench.Idle();
            var afterBody=window.outline.Items.Count==0?null:window.outline.Items[0];
            check("body typing keeps outline",object.ReferenceEquals(firstItem,afterBody),"same="+object.ReferenceEquals(firstItem,afterBody));
            window.source.CaretIndex=window.source.Document.GetLineByNumber(1).EndOffset;window.source.TextArea.PerformTextInput("X");LongLineBench.Idle();
            var renamed=window.outline.Items.Count==0?null:window.outline.Items[0] as System.Windows.Controls.TreeViewItem;
            check("heading typing refreshes outline",renamed!=null&&OutlineText(renamed).Contains("제목X"),"text="+(renamed==null?"-":OutlineText(renamed)));
            window.Undo();window.Undo();window.outline.Visibility=System.Windows.Visibility.Collapsed;LongLineBench.Idle();
            string original=window.text;
            int enabled=0;foreach(System.Windows.UIElement control in window.formatControls)if(control.IsEnabled)enabled++;
            check("format buttons enabled",window.LiveFormat&&window.boldButton.IsEnabled&&window.quoteButton.IsEnabled,"live="+window.LiveFormat+", enabled "+enabled+"/"+window.formatControls.Count+", bold="+window.boldButton.IsEnabled);
            int word=window.source.Document.GetLineByNumber(3).Offset+window.source.Document.GetText(window.source.Document.GetLineByNumber(3)).IndexOf("문단",StringComparison.Ordinal);
            window.source.Select(word,2);window.ToggleFormat("bold");LongLineBench.Idle();
            window.source.Select(word+3,0);window.UpdateTools();
            check("toolbar shows bold at caret",Object.Equals(window.boldButton.Tag,"Active")&&window.italicButton.Tag==null,"bold="+window.boldButton.Tag+", italic="+window.italicButton.Tag);
            check("bold edits the source",window.text.Contains("**문단**."),"line="+window.source.Document.GetText(window.source.Document.GetLineByNumber(3)));
            window.source.Select(word,0);window.SetHeading(3);LongLineBench.Idle();
            check("heading edits the source",window.source.Document.GetText(window.source.Document.GetLineByNumber(3)).StartsWith("### ",StringComparison.Ordinal),"line="+window.source.Document.GetText(window.source.Document.GetLineByNumber(3)));
            window.Undo();LongLineBench.Idle();window.Undo();LongLineBench.Idle();
            check("each command is one undo step",window.text==original,"len="+window.text.Length+"/"+original.Length);
            // Rule button round-trip through the same routed Click used by the toolbar; never run by --toolbar-shot.
            window.source.Select(window.source.Document.GetLineByNumber(3).EndOffset,0);
            window.ruleButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));LongLineBench.Idle();window.UpdateTools();
            string withRule=window.text;int ruleLines=window.source.Document.LineCount;
            // InsertRule ends the caret on the new empty line under the rule, so the button is idle there.
            var ruleCaretLine=window.source.Document.GetLineByOffset(window.source.CaretIndex);
            check("rule inserts and leaves the caret below",withRule!=original&&ruleCaretLine.Length==0&&ruleCaretLine.PreviousLine!=null&&LiveRuleLine(window.source.Document,ruleCaretLine.PreviousLine)&&window.ruleButton.Tag==null,"active="+window.ruleButton.Tag+", line="+ruleCaretLine.LineNumber);
            // The removal path needs the caret on the rule line: run the same command the Up key binds.
            System.Windows.Documents.EditingCommands.MoveUpByLine.Execute(null,window.source.TextArea);LongLineBench.Idle();window.UpdateTools();
            var ruleLine=window.source.Document.GetLineByOffset(window.source.CaretIndex);
            bool backOnRule=LiveRuleLine(window.source.Document,ruleLine)&&Object.Equals(window.ruleButton.Tag,"Active");
            window.ruleButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));LongLineBench.Idle();window.UpdateTools();
            check("rule button removes the caret rule",backOnRule&&window.source.Document.LineCount<ruleLines&&!LiveRuleLine(window.source.Document,window.source.Document.GetLineByOffset(window.source.CaretIndex))&&window.ruleButton.Tag==null,"lines="+window.source.Document.LineCount+"/"+ruleLines+", onRule="+backOnRule);
            window.Undo();LongLineBench.Idle();
            check("rule removal undoes in one step",window.text==withRule,"restored="+(window.text==withRule));
            window.Undo();LongLineBench.Idle();
            check("rule insertion undoes in one step",window.text==original,"restored="+(window.text==original));
            // A thematic break may be indented at most three spaces (CommonMark); four make the
            // line paragraph text, and the raw grammar and the live parse agree on that.
            foreach(string sample in new[]{"---","  * * *  ","   _ _ _ _","    _ _ _ _","- -","    ---","--*","--- text"}){
                var ruleDocument=new ICSharpCode.AvalonEdit.Document.TextDocument(sample);bool ruleExpected=sample=="---"||sample=="  * * *  "||sample=="   _ _ _ _";
                check("rule line grammar "+sample,LiveRuleLine(ruleDocument,ruleDocument.GetLineByNumber(1))==ruleExpected,"expected="+ruleExpected);
            }
            string markdownBefore=window.text;
            var bodyLine=window.source.Document.GetLineByNumber(3);
            window.source.CaretIndex=bodyLine.Offset+window.source.Document.GetText(bodyLine).IndexOf("문단",StringComparison.Ordinal);
            window.ToggleTextMode();LongLineBench.Idle();
            string plainNow=window.source.Document.Text;int plainCaret=window.source.CaretIndex;
            check("MD to TXT keeps the caret inside the line",plainCaret+2<=plainNow.Length&&plainNow.Substring(plainCaret,2)=="문단","at="+plainCaret+", text="+(plainCaret+6<=plainNow.Length?plainNow.Substring(plainCaret,6):"-"));
            check("MD to TXT uses the string converter",window.PlainText&&window.text==MarkdownPlainText.Convert(markdownBefore)&&window.livePreview==null,"plain="+window.PlainText+", len="+window.text.Length);
            string plainText=window.text;
            var plainLine=window.source.Document.GetLineByNumber(3);
            window.source.CaretIndex=plainLine.Offset+window.source.Document.GetText(plainLine).IndexOf("문단",StringComparison.Ordinal);
            window.ToggleTextMode();LongLineBench.Idle();
            var mdLine=window.source.Document.GetLineByNumber(3);
            string mdAfter=window.source.Document.GetText(mdLine);int mdColumn=window.source.CaretIndex-mdLine.Offset;
            check("TXT to MD keeps the caret inside the line",mdColumn+2<=mdAfter.Length&&mdAfter.Substring(mdColumn,2)=="문단","column="+mdColumn+", line="+mdAfter);
            check("TXT to MD stays on the live surface",!window.PlainText&&window.LivePreviewAttached&&window.text==LiteralMarkdown(plainText),"attached="+window.LivePreviewAttached);
            window.Undo();LongLineBench.Idle();window.Undo();LongLineBench.Idle();
            check("mode changes undo back to the original",!window.PlainText&&window.text==markdownBefore&&window.LivePreviewAttached,"plain="+window.PlainText+", same="+(window.text==markdownBefore));
            string beforeTyping=window.text;
            window.source.CaretIndex=window.source.Document.GetLineByNumber(3).EndOffset;
            foreach(string ch in new[]{"가","나","다"," ","라"}){window.source.TextArea.PerformTextInput(ch);LongLineBench.Idle();}
            bool typedRun=window.text.Contains("문단.가나다 라");
            // Notepad's rule (user decision, 2026-10-31): the whole typed run, spaces included, is
            // one undo step.
            window.Undo();LongLineBench.Idle();
            check("typing run undoes in one step",typedRun&&window.text==beforeTyping,"typed="+typedRun+", restored="+(window.text==beforeTyping));
            window.source.CaretIndex=window.source.Document.GetLineByNumber(3).EndOffset;
            Action<System.Windows.Input.Key> hit=delegate(System.Windows.Input.Key key){
                var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,key){RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent};
                window.source.TextArea.RaiseEvent(args);LongLineBench.Idle();
            };
            hit(System.Windows.Input.Key.Back);hit(System.Windows.Input.Key.Back);hit(System.Windows.Input.Key.Back);
            bool deleted=window.text.Length==beforeTyping.Length-3;
            window.Undo();LongLineBench.Idle();
            check("Backspace run undoes in one step",deleted&&window.text==beforeTyping,"deleted="+deleted+", restored="+(window.text==beforeTyping));
            string beforeKeys=window.text;
            var listLine=window.source.Document.GetLineByNumber(5);
            window.source.CaretIndex=listLine.EndOffset;
            Action<System.Windows.Input.Key> press=delegate(System.Windows.Input.Key key){
                var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,System.Windows.PresentationSource.FromVisual(window.source),0,key){RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
                window.source.TextArea.RaiseEvent(args);LongLineBench.Idle();
            };
            press(System.Windows.Input.Key.Enter);
            string next=window.source.Document.GetText(window.source.Document.GetLineByNumber(6));
            check("Enter continues the list",next=="- "&&window.source.TextArea.Caret.Line==6,"line6='"+next+"'");
            press(System.Windows.Input.Key.Tab);
            next=window.source.Document.GetText(window.source.Document.GetLineByNumber(6));
            check("Tab indents the list item",next=="  - ","line6='"+next+"'");
            press(System.Windows.Input.Key.Back);
            next=window.source.Document.GetText(window.source.Document.GetLineByNumber(6));
            check("Backspace removes the marker first",next=="  ","line6='"+next+"'");
            press(System.Windows.Input.Key.Back);
            next=window.source.Document.GetText(window.source.Document.GetLineByNumber(6));
            check("second Backspace removes the indentation",next=="","line6='"+next+"'");
            for(int i=0;i<4;i++){window.Undo();LongLineBench.Idle();}
            check("structural keys undo step by step",window.text==beforeKeys,"same="+(window.text==beforeKeys));
            // An IME-committed Enter arrives as text input, not as Key.Enter.
            window.source.CaretIndex=window.source.Document.GetLineByNumber(5).EndOffset;
            window.source.TextArea.PerformTextInput("\n");LongLineBench.Idle();
            next=window.source.Document.GetText(window.source.Document.GetLineByNumber(6));
            check("newline text input continues the list",next=="- ","line6='"+next+"'");
            window.Undo();LongLineBench.Idle();
            check("newline text input undoes in one step",window.text==beforeKeys,"same="+(window.text==beforeKeys));
            // Copy handler called directly so the user's clipboard is never touched.
            var line3=window.source.Document.GetLineByNumber(3);
            window.source.Select(line3.Offset,line3.Length);
            var copied=new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText,window.source.SelectedText);
            window.SourceCopying(window.source.TextArea,new System.Windows.DataObjectCopyingEventArgs(copied,false));
            string rtf=copied.GetData(System.Windows.DataFormats.Rtf) as string;
            check("copy keeps Markdown text and adds rendered RTF",(copied.GetData(System.Windows.DataFormats.UnicodeText) as string).Contains("**굵게**")&&rtf!=null&&rtf.StartsWith("{\\rtf",StringComparison.Ordinal)&&!rtf.Contains("**"),"rtf="+(rtf==null?"null":rtf.Length.ToString()));
            window.source.Select(line3.EndOffset,0);
            string beforePaste=window.text;
            window.source.CaretIndex=window.source.Document.GetLineByNumber(3).EndOffset;
            window.PasteData(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText,"*그대로*"),false);LongLineBench.Idle();
            check("plain paste is inserted verbatim",window.text.Contains("문단.*그대로*"),"line="+window.source.Document.GetText(window.source.Document.GetLineByNumber(3)));
            var rich=new System.Windows.DataObject();
            string fragment="<html><body><!--StartFragment--><b>굵은</b> 글<!--EndFragment--></body></html>";
            string header="Version:0.9\r\nStartHTML:0000000000\r\nEndHTML:0000000000\r\nStartFragment:0000000000\r\nEndFragment:0000000000\r\n";
            int startHtml=header.Length,startFragment=startHtml+fragment.IndexOf("<!--StartFragment-->",StringComparison.Ordinal)+20,endFragment=startHtml+fragment.IndexOf("<!--EndFragment-->",StringComparison.Ordinal);
            string cfHtml=header.Replace("StartHTML:0000000000","StartHTML:"+startHtml.ToString("D10")).Replace("EndHTML:0000000000","EndHTML:"+(startHtml+fragment.Length).ToString("D10")).Replace("StartFragment:0000000000","StartFragment:"+startFragment.ToString("D10")).Replace("EndFragment:0000000000","EndFragment:"+endFragment.ToString("D10"))+fragment;
            rich.SetData(System.Windows.DataFormats.Html,cfHtml);rich.SetData(System.Windows.DataFormats.UnicodeText,"굵은 글");
            window.PasteData(rich,false);LongLineBench.Idle();
            check("HTML paste becomes Markdown",window.text.Contains("**굵은**"),"line="+window.source.Document.GetText(window.source.Document.GetLineByNumber(3)));
            window.Undo();LongLineBench.Idle();window.Undo();LongLineBench.Idle();
            check("pastes undo",window.text==beforePaste,"same="+(window.text==beforePaste));
            session=window.livePreview;
            window.SetDark(true);LongLineBench.Idle();Program.Snapshot(window,Path.Combine(outDir,"live-host-dark.png"));
            check("theme change updates without reattach",object.ReferenceEquals(session,window.livePreview)&&window.livePreview.Options.Dark,"same="+object.ReferenceEquals(session,window.livePreview));
            window.loading=true;window.preview.IsChecked=true;window.loading=false;window.SwitchSurface();LongLineBench.Idle();
            check("raw edit toggle detaches",window.livePreview==null&&!double.IsNaN(window.source.TextArea.TextView.LineHeight)&&window.CodeMode,"lineHeight="+window.source.TextArea.TextView.LineHeight);
            window.loading=true;window.preview.IsChecked=false;window.loading=false;window.SwitchSurface();LongLineBench.Idle();
            check("raw edit toggle reattaches",window.LivePreviewAttached,"attached="+window.LivePreviewAttached);
            window.source.TextArea.PerformTextInput("끝");LongLineBench.Idle();
            Program.Snapshot(window,Path.Combine(outDir,"live-host.png"));
            for(int i=0;i<10;i++){System.Threading.Thread.Sleep(50);LongLineBench.Idle();}
            Program.Snapshot(window,Path.Combine(outDir,"live-host-settled.png"));
            window.source.Document.Insert(window.source.Document.TextLength,"\n본문 각주[^n]와 [문서][Docs].\n\n[^n]: 각주 정의\n[docs]: https://example.invalid/d\n");LongLineBench.Idle();
            var noteLine=window.source.Document.GetLineByNumber(window.source.Document.LineCount-4);
            string noteUrl=window.livePreview==null?null:window.livePreview.LinkAt(noteLine.Offset+window.source.Document.GetText(noteLine).IndexOf("[^n]",StringComparison.Ordinal)+1);
            string refUrl=window.livePreview==null?null:window.livePreview.LinkAt(noteLine.Offset+window.source.Document.GetText(noteLine).IndexOf("[문서]",StringComparison.Ordinal)+1);
            if(noteUrl!=null)window.OpenLiveLink(noteUrl);LongLineBench.Idle();
            var definition=window.FindDefinition("[docs]:");
            check("footnote and reference links resolve",noteUrl=="#^n"&&refUrl=="ref:Docs"&&window.source.Document.GetText(window.source.Document.GetLineByOffset(window.source.CaretIndex)).StartsWith("[^n]:",StringComparison.Ordinal)&&definition!=null,"note="+noteUrl+", ref="+refUrl+", definition="+(definition!=null));
            VerifyLiveToolbarRegression(window,check,file);
            VerifyTableToolbarExitRegression(window,check);
            VerifyStackedCloserTyping(window,check);
            VerifyPasteInsideSameStyle(window,check);
            VerifyCellSpanPaste(window,check);
            VerifyUnderscoreSpaceDelete(window,check);
            VerifyEnterUndoEnter(window,check);
            VerifyWordDeleteEmptiesPair(window,check);
            VerifySelectionMarkerEdits(window,check);
            VerifyTableEdgeEdits(window,check);
            // Scrollbar look: a long document in dark, then light.
            var lines=new System.Text.StringBuilder();for(int i=0;i<120;i++)lines.Append("\n스크롤 확인 줄 ").Append(i);
            window.source.Document.Insert(window.source.Document.TextLength,lines.ToString());window.source.ScrollToVerticalOffset(300);LongLineBench.Idle();
            Program.Snapshot(window,Path.Combine(outDir,"live-host-scroll-dark.png"));
            window.SetDark(false);LongLineBench.Idle();
            Program.Snapshot(window,Path.Combine(outDir,"live-host-scroll-light.png"));
        }catch(Exception ex){all=false;report.Append("EXCEPTION ").Append(ex).Append('\n');}
        finally{if(window!=null){window.saved=window.text;window.Close();}try{File.Delete(file);}catch(IOException){}}
        report.Append(all?"\nResult: PASS\n":"\nResult: FAIL\n");
        File.WriteAllText(Path.Combine(outDir,"live-host-probe.txt"),report.ToString(),new System.Text.UTF8Encoding(false));
        if(all)File.WriteAllText(Path.Combine(outDir,"live-host-probe-PASS.txt"),"PASS\n");
        return all?0:1;
    }
}
// The basic view's in-document "#heading" rule, shared by the live jump and --commands-probe.
// Workspace.cs:153 (NavigateDocumentLink): the hash is dropped and the rest is unescaped and
// lower-cased; a heading's text is trimmed, lower-cased, stripped of every character outside
// letters, digits, '_', ' ' and '-', and then its spaces become '-'. The comparison is exact,
// so duplicates keep the first heading and a missing anchor moves nothing.
internal static class AnchorRule {
    static readonly System.Text.RegularExpressions.Regex Invalid=new System.Text.RegularExpressions.Regex(@"[^\p{L}\p{N}_ -]",System.Text.RegularExpressions.RegexOptions.Compiled);
    internal static string Target(string url){return Uri.UnescapeDataString(url.Substring(1)).ToLowerInvariant();}
    internal static string Slug(string title){return title==null?"":Invalid.Replace(title.Trim().ToLowerInvariant(),"").Replace(' ','-');}
}
}
