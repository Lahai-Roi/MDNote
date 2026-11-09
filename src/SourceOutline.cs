using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {public partial class MainWindow {
    sealed class SourceOutlineNode {
        public string Title {get;set;}
        public int Level {get;set;}
        public List<SourceOutlineNode> Children {get;set;}
        /* Whether the tree draws this heading's children. The large outline's item style binds
           IsExpanded to it two-way, so a fold the reader makes survives the rebuild an edit causes. */
        public bool Expanded {get;set;}
        public string Label {get{return "H"+Level+" "+Title;}}
        /* The tree draws level 1 in SemiBold; every other level reads as body text (the level bars
           already carry the level). Presentation only, like Label: the scanner never reads it. */
        public FontWeight Weight {get{return Level==1?FontWeights.SemiBold:FontWeights.Normal;}}
        internal int Offset;
    }
    CancellationTokenSource sourceOutlineCancellation;
    TextDocument sourceOutlineDocument;
    ITextSourceVersion sourceOutlineVersion;
    TextDocument displayedOutlineDocument;ITextSourceVersion displayedOutlineVersion;
    string sourceOutlineStyle;
    Task sourceOutlineTask;
    internal Exception LastSourceOutlineError;
    bool outlineRestoring;
    /* The row the current left press is on, from its down to the input that ends it. A selection
       change inside that window belongs to the same click and must not jump a second time. */
    TreeViewItem outlinePressedItem;
    // The heading list the outline last published, shared with the in-document [TOC] box. The
    // same scans the panel runs fill it; while the panel is closed a TOC document gets a
    // debounced background scan of the same kind, never a walk from the live parser.
    MarkdownLivePreview.TocData tocData;
    CancellationTokenSource tocHeadingsCancellation;
    DispatcherTimer tocHeadingsIdle;
    DispatcherOperation tocHeadingsQueued;
    System.Threading.Tasks.Task tocHeadingsTask;
    void CancelSourceOutline(){
        if(sourceOutlineCancellation!=null){sourceOutlineCancellation.Cancel();sourceOutlineCancellation=null;}
        // The TOC's heading scan follows the same lifecycle: a closed or hidden panel and a tab
        // switch stop a pending walk instead of publishing for a document nobody shows.
        if(tocHeadingsCancellation!=null){tocHeadingsCancellation.Cancel();tocHeadingsCancellation=null;}
        if(tocHeadingsIdle!=null)tocHeadingsIdle.Stop();
        sourceOutlineDocument=null;sourceOutlineVersion=null;sourceOutlineStyle=null;
    }
    // True while the published list was read from this document's current version, so a
    // heading edit or a tab switch is the only thing that starts another scan.
    bool TocHeadingsCurrent(){
        return source!=null&&source.Document!=null&&tocData!=null&&
            Object.ReferenceEquals(tocData.Document,source.Document)&&
            Object.ReferenceEquals(tocData.Version,source.Document.Version);
    }
    // A "[TOC]" line was parsed (Settings.TocRequested) or a heading edit landed in a document
    // that uses one: scan after the typing pauses. The list is the outline's own, so the box
    // and the panel always agree; an up-to-date list costs one compare. The first call is
    // posted: the [TOC] parse runs while the view builds a visual line, and Update (a redraw
    // when the list is already current) must not run inside that build.
    void EnsureTocHeadingsSoon(){
        if(source==null||source.Document==null||PlainText)return;
        if(tocHeadingsQueued!=null){
            if(tocHeadingsQueued.Status==DispatcherOperationStatus.Pending)return;
            tocHeadingsQueued=null;
        }
        tocHeadingsQueued=Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{
            tocHeadingsQueued=null;
            EnsureTocHeadingsNow();
        }));
    }
    void EnsureTocHeadingsNow(){
        if(source==null||source.Document==null||PlainText)return;
        if(TocHeadingsCurrent()){
            // The panel's scan already answered for this version: hand the list to a session
            // that has just parsed its first [TOC] line (its Toc may still be null or old).
            if(livePreview!=null&&LivePreviewAttached&&livePreview.TocSeen)livePreview.Update(LiveSettings());
            return;
        }
        if(tocHeadingsIdle==null){
            tocHeadingsIdle=new DispatcherTimer(DispatcherPriority.Background,Dispatcher);
            tocHeadingsIdle.Interval=TimeSpan.FromMilliseconds(160);
            tocHeadingsIdle.Tick+=TocHeadingsIdle;
        }
        tocHeadingsIdle.Stop();
        tocHeadingsIdle.Start();
    }
    void TocHeadingsIdle(object sender,EventArgs e){
        tocHeadingsIdle.Stop();
        if(source==null||source.Document==null||PlainText||TocHeadingsCurrent())return;
        if(tocHeadingsCancellation!=null){tocHeadingsCancellation.Cancel();tocHeadingsCancellation=null;}
        var pending=new CancellationTokenSource();
        tocHeadingsCancellation=pending;
        var document=source.Document;
        var version=document.Version;
        tocHeadingsTask=BuildTocHeadingsAsync(pending,document.CreateSnapshot(),document,version);
    }
    async Task BuildTocHeadingsAsync(CancellationTokenSource pending,ITextSource snapshot,TextDocument document,ITextSourceVersion version){
        try{
            await Task.Delay(40,pending.Token);
            var nodes=await Task.Run(delegate{return ScanSourceOutline(snapshot,pending.Token);},pending.Token);
            if(pending.IsCancellationRequested||tocHeadingsCancellation!=pending||source==null||PlainText||source.Document!=document)return;
            PublishOutlineHeadings(nodes,document,version);
        }catch(OperationCanceledException){
        }catch(Exception error){
            // The panel's error field is the one the probes read; a TOC scan must not throw out
            // of a dispatcher continuation.
            if(!pending.IsCancellationRequested&&tocHeadingsCancellation==pending)LastSourceOutlineError=error;
        }finally{
            if(tocHeadingsCancellation==pending)tocHeadingsCancellation=null;
            pending.Dispose();
        }
    }
    // Flattens the panel's heading tree into the TOC's rows (document order, cap 200: the box
    // is not meant to swallow a whole book's outline).
    static void CollectTocEntries(List<SourceOutlineNode> nodes,List<MarkdownLivePreview.TocEntry> entries){
        if(nodes==null)return;
        foreach(var node in nodes){
            if(entries.Count>=200)return;
            if(node.Title!=null&&node.Title.Length>0)
                // The heading's visible text: a ":name:" shortcode shows its character here the
                // way the heading itself draws it (the outline panel keeps the source form).
                entries.Add(new MarkdownLivePreview.TocEntry {Title=MarkdownPlainText.EmojiText(node.Title),Level=node.Level,Offset=node.Offset});
            CollectTocEntries(node.Children,entries);
        }
    }
    void PublishOutlineHeadings(List<SourceOutlineNode> nodes,TextDocument document,ITextSourceVersion version){
        var entries=new List<MarkdownLivePreview.TocEntry>();
        CollectTocEntries(nodes,entries);
        tocData=new MarkdownLivePreview.TocData(entries.ToArray(),version,document);
        // A session that has no [TOC] line pays nothing for a publish; the line's first parse
        // (EnsureTocHeadingsSoon) hands the current list over when it appears.
        if(livePreview!=null&&LivePreviewAttached&&livePreview.TocSeen)livePreview.Update(LiveSettings());
    }
    // The heading list for the print parser and the HTML export: the outline's own scan of a
    // text, with no live session involved (the export runs on a worker thread).
    internal static List<MarkdownLivePreview.TocEntry> ScanTocEntries(string text){
        var entries=new List<MarkdownLivePreview.TocEntry>();
        if(text==null)return entries;
        CollectTocEntries(ScanSourceOutline(new ICSharpCode.AvalonEdit.Document.StringTextSource(text),CancellationToken.None),entries);
        return entries;
    }
    void ClearOutlineItems(){outline.ItemsSource=null;outline.Items.Clear();outline.ItemTemplate=null;outline.ItemContainerStyle=null;displayedOutlineDocument=null;displayedOutlineVersion=null;}
    // Ordinary text keeps at most OutlineHead characters: enough for every outer marker the
    // grammar reads and for a heading's label, so a document with one very long line is never
    // copied whole. The fence run is still tracked character by character, and the two buffers
    // alternate so the paragraph a setext underline promotes is intact when the underline arrives
    // (that heading is one line of lookahead, like the renderer's Before().Setext).
    const int OutlineHead=4096;
    const int OutlineParseLimit=16384;
    sealed class OutlineLine {
        internal char[] Head=new char[OutlineHead];
        char[] spare=new char[OutlineHead];
        internal char Marker;internal int MarkerLength;internal bool TailWhite=true,HasBacktick;
        internal int Length,HeadLength;
        int state,indent;
        internal void Feed(char c,bool headings){
            Length++;if(HeadLength<OutlineHead)Head[HeadLength++]=c;
            if(state==4)return;
            if(state==0){if(c==' '||c=='\t'){if(++indent>3)state=4;return;}if(c=='`'||c=='~'||(c=='#'&&headings)){Marker=c;MarkerLength=1;state=1;}else state=4;return;}
            if(state==1){if(c==Marker){MarkerLength++;if(Marker=='#'&&MarkerLength>6)state=4;return;}state=Marker=='#'?2:3;if(state==2)return;}
            TailWhite&=c==' '||c=='\t';HasBacktick|=c=='`';
        }
        internal bool Fence {get{return (state==1||state==3)&&(Marker=='`'||Marker=='~')&&MarkerLength>=3;}}
        internal void Reset(){Marker='\0';MarkerLength=0;TailWhite=true;HasBacktick=false;state=0;indent=0;Length=0;HeadLength=0;var swap=Head;Head=spare;spare=swap;}
    }
    // The line's outer markers, read in MarkdownLivePreview.ParseLine's order: indentation,
    // blockquote markers, an ATX heading ("# title", "> # title"), then a list marker and the
    // quote markers and ATX heading inside the item ("- ## item", "> - ## item"). level>0 means
    // the live view draws the line as a heading; titleStart is where its visible text begins.
    // Indentation and a marker's separator are ASCII space/tab only, the same rule the renderer
    // and the structural keys read; a full-width space (U+3000) before "#" is paragraph text.
    static void OutlineClassify(char[] text,int length,out int level,out int titleStart){
        level=0;titleStart=0;
        int p=0,lead=0;
        while(p<length&&(text[p]==' '||text[p]=='\t')){p++;lead++;}
        if(p>=length)return;
        int q=p,quotes=0;
        while(q<length&&text[q]=='>'&&quotes<6){q++;quotes++;if(q<length&&(text[q]==' '||text[q]=='\t'))q++;}
        int afterQuote=q;
        while(afterQuote<length&&(text[afterQuote]==' '||text[afterQuote]=='\t'))afterQuote++;
        /* An ATX heading takes at most three leading whitespace characters. A quote's own markers
           are not indentation: what counts after them is the run the marker's single spacer does
           not consume (MarkdownLivePreview's headingIndent), so ">    # x" is a heading and
           ">     # x" is paragraph text, the same as the view draws them. */
        bool outer=quotes>0?afterQuote-q<=3:lead<=3;
        /* A one-line block heading ("<h2>제목</h2>", the live view's ParseHtmlSingleLine): the
           cheap shape only, so a line without "<hN>" pays three character compares. The raw
           title's closing "</hN>" is trimmed by OutlineAtxRaw. */
        if(outer&&OutlineHtmlAtx(text,length,afterQuote,out level,out titleStart))return;
        if(outer&&OutlineAtx(text,length,afterQuote,out level,out titleStart))return;
        int body;
        if(OutlineList(text,length,quotes>0?q:0,out body)){
            int itemQuote=body,inner=0;
            while(itemQuote<length&&text[itemQuote]=='>'&&inner<6){itemQuote++;inner++;if(itemQuote<length&&(text[itemQuote]==' '||text[itemQuote]=='\t'))itemQuote++;}
            if(OutlineAtx(text,length,itemQuote,out level,out titleStart))return;
        }
        level=0;titleStart=0;
    }
    // "<hN>" ... "</hN>" on one line. Attributes are left to the live view's full parser; the
    // outline's cheap check accepts the plain form only (the common one), and the closing run
    // must exist on the same line.
    static bool OutlineHtmlAtx(char[] text,int length,int at,out int level,out int titleStart){
        level=0;titleStart=at;
        if(at<0||at+4>=length||text[at]!='<')return false;
        if(text[at+1]!='h'&&text[at+1]!='H')return false;
        int digit=at+2;
        if(text[digit]<'1'||text[digit]>'6')return false;
        level=text[digit]-'0';
        int open=digit+1;
        if(open>=length)return false;
        if(text[open]!='>'){
            if(text[open]!=' '&&text[open]!='\t')return false;
            while(open<length&&text[open]!='>')open++;
            if(open>=length)return false;
        }
        titleStart=open+1;
        char close=(char)('0'+level);
        for(int q=titleStart;q+3<length;q++){
            if(text[q]!='<'||text[q+1]!='/'||(text[q+2]!='h'&&text[q+2]!='H')||text[q+3]!=close)continue;
            if(q+4<length&&text[q+4]=='>')return true;
        }
        level=0;titleStart=at;return false;
    }
    static bool OutlineAtx(char[] text,int length,int at,out int level,out int titleStart){
        level=0;titleStart=at;
        int h=at;
        while(h<length&&text[h]=='#'&&h-at<7)h++;
        int count=h-at;
        if(count<1||count>6)return false;
        /* The line's end counts, the same as the view's AtxStart: "###" alone is an empty heading
           there. No node is listed for it (its title is empty), but it must not fall through to
           the paragraph test, or a "---" below would promote it to a setext heading the view never
           draws. */
        if(h<length&&text[h]!=' '&&text[h]!='\t')return false;
        while(h<length&&(text[h]==' '||text[h]=='\t'))h++;
        level=count;titleStart=h;return true;
    }
    // MarkdownLivePreview.ReadList's marker test, plus the task box the item body can start with.
    static bool OutlineList(char[] text,int length,int from,out int body){
        body=from;int p=Math.Max(0,Math.Min(from,length));
        while(p<length&&(text[p]==' '||text[p]=='\t'))p++;
        if(p+1<length&&"-*+".IndexOf(text[p])>=0&&(text[p+1]==' '||text[p+1]=='\t'))p++;
        else{
            int start=p;
            while(p<length&&p-start<9&&text[p]>='0'&&text[p]<='9')p++;
            if(p==start||p+1>=length||(text[p]!='.'&&text[p]!=')')||(text[p+1]!=' '&&text[p+1]!='\t'))return false;
            p++;
        }
        while(p<length&&(text[p]==' '||text[p]=='\t'))p++;
        if(p+2<length&&text[p]=='['&&text[p+2]==']'&&" xX".IndexOf(text[p+1])>=0){p+=3;while(p<length&&(text[p]==' '||text[p]=='\t'))p++;}
        body=p;return true;
    }
    // MarkdownLivePreview.SetextUnderlineLevel: up to three leading whitespace characters, one or
    // more "=" (H1) or "-" (H2), then only whitespace. 0 when the line is not an underline.
    static int OutlineUnderline(char[] text,int length){
        int p=0;
        while(p<length&&p<4&&(text[p]==' '||text[p]=='\t'))p++;
        if(p>3||p>=length)return 0;
        char marker=text[p];
        if(marker!='='&&marker!='-')return 0;
        int run=p;
        while(p<length&&text[p]==marker)p++;
        // 11-01: one "-" under a paragraph is a list being typed, not an H2 (same rule as the live view).
        if(marker=='-'&&p-run<2)return 0;
        for(;p<length;p++)if(text[p]!=' '&&text[p]!='\t')return 0;
        return marker=='='?1:2;
    }
    // MarkdownLivePreview.IsRule: at most three leading whitespace columns (a tab counts four),
    // three or more "-", "*" or "_" and nothing else but spaces.
    static bool OutlineRule(char[] text,int length,int start,int indent){
        if(indent>3)return false;
        char marker='\0';int count=0;
        for(int i=start;i<length;i++){
            char c=text[i];if(c==' '||c=='\t')continue;
            if(marker=='\0')marker=c;
            if((c!='-'&&c!='*'&&c!='_')||c!=marker)return false;
            count++;
        }
        return count>=3;
    }
    // "[id]: url" or "[^id]: text" at the line start, MarkdownLivePreview.DefinitionLine's shape.
    static bool OutlineDefinition(char[] text,int length,int start){
        if(start>=length||text[start]!='[')return false;
        int p=start+1;
        if(p<length&&text[p]=='^')p++;
        int first=p;
        while(p<length&&p-first<128&&text[p]!=']'&&text[p]!='[')p++;
        if(p>=length||text[p]!=']'||p==first)return false;
        p++;
        return p<length&&text[p]==':';
    }
    // A line a following underline turns into a heading (MarkdownLivePreview.SetextText): plain
    // paragraph text, not a quote, rule, ATX heading, list item or definition. An indented line is
    // paragraph text too - the live view has no indented code block and promotes it the same way -
    // so only the rule and definition tests keep their own three-column limit.
    static bool OutlineParagraph(char[] text,int length){
        if(length==0)return false;
        int p=0,columns=0;
        while(p<length&&(text[p]==' '||text[p]=='\t')){columns+=text[p]=='\t'?4:1;p++;}
        if(p>=length)return false;
        if(text[p]=='>')return false;
        int level,start;
        if(OutlineHtmlAtx(text,length,p,out level,out start))return false;
        if(OutlineAtx(text,length,p,out level,out start))return false;
        if(OutlineRule(text,length,p,columns))return false;
        int body;
        if(OutlineList(text,length,0,out body))return false;
        if(columns<=3&&OutlineDefinition(text,length,p))return false;
        return true;
    }
    /* The heading's raw text, markers removed. An ATX title drops the line's trailing whitespace
       and a trailing "#" run that is a closing marker (a "#" that is part of the text, "C#", stays);
       the caller turns it into the visible text with MarkdownPlainText.Convert, so "**굵게** 제목"
       reads "굵게 제목" in the outline and slugs the same way in the #anchor jump. */
    static string OutlineAtxRaw(char[] text,int length,int start){
        int end=length;
        while(end>start&&Char.IsWhiteSpace(text[end-1]))end--;
        int hash=end;
        while(hash>start&&text[hash-1]=='#')hash--;
        if(hash<end&&(hash==start||Char.IsWhiteSpace(text[hash-1]))){end=hash;while(end>start&&Char.IsWhiteSpace(text[end-1]))end--;}
        /* An HTML heading's closer ("</h2>", with an optional space before its ">") is markup. */
        if(end>start&&text[end-1]=='>'){
            int lt=end-2;
            while(lt>start&&lt>end-8&&text[lt]!='<')lt--;
            if(lt>=start&&text[lt]=='<'){
                int p=lt+1;
                if(p<end&&text[p]=='/'&&p+1<end&&(text[p+1]=='h'||text[p+1]=='H')&&p+2<end&&text[p+2]>='1'&&text[p+2]<='6'){
                    int after=p+3;
                    while(after<end-1&&Char.IsWhiteSpace(text[after]))after++;
                    if(after==end-1)end=lt;
                }
            }
        }
        while(end>start&&Char.IsWhiteSpace(text[end-1]))end--;
        if(end<=start)return null;
        return new string(text,start,end-start);
    }
    static string OutlineParagraphRaw(char[] text,int length){
        int start=0;
        while(start<length&&Char.IsWhiteSpace(text[start]))start++;
        int end=length;
        while(end>start&&Char.IsWhiteSpace(text[end-1]))end--;
        if(end<=start)return null;
        return new string(text,start,end-start);
    }
    /* The visible text of a heading. MarkdownPlainText reads block structure, so a title that happens
       to start with a block marker ("1. 개요", "- 항목", "> 인용") would lose that marker; the "("
       prefix keeps the title one paragraph and is cut from the answer. Inline markers (bold, code,
       links) are removed as the reader sees them. */
    static string OutlineLabel(string raw){
        if(raw==null)return null;
        string plain=MarkdownPlainText.Convert("("+raw);
        return plain.Length>0&&plain[0]=='('?plain.Substring(1):plain;
    }
    static void AddOutlineNode(List<SourceOutlineNode> roots,SourceOutlineNode[] parents,int offset,string title,int level){
        var node=new SourceOutlineNode {Title=title,Level=level,Offset=offset,Children=new List<SourceOutlineNode>(),Expanded=true};SourceOutlineNode parent=null;
        for(int i=level-2;i>=0;i--)if(parents[i]!=null){parent=parents[i];break;}if(parent==null)roots.Add(node);else parent.Children.Add(node);parents[level-1]=node;for(int i=level;i<parents.Length;i++)parents[i]=null;
    }
    static List<SourceOutlineNode> ScanSourceOutline(ITextSource snapshot,CancellationToken cancellation){
        var roots=new List<SourceOutlineNode>();var parents=new SourceOutlineNode[6];int offset=0;char fence='\0';int fenceLength=0;
        bool pending=false;int pendingOffset=0;char[] pendingText=null;int pendingLength=0;
        // YAML front matter is not document structure: its "---"s are not rules/underlines and a
        // "#" line inside it is not a heading. The shared rule reads only line 1 for the common case.
        int frontMatterEnd=MarkdownLivePreview.FrontMatterEnd(snapshot);
        int lineNumber=1;
        using(var reader=snapshot.CreateReader()){
            var line=new OutlineLine();var chunk=new char[8192];int count;bool previousCR=false;
            Action consume=delegate{
                cancellation.ThrowIfCancellationRequested();
                int number=lineNumber++;
                if(number<=frontMatterEnd){line.Reset();pending=false;return;}
                bool fenceLine=line.Fence;char symbol=line.Marker;int length=line.MarkerLength;bool white=line.TailWhite,backtick=line.HasBacktick;
                int lineLength=line.Length,headLength=line.HeadLength;char[] head=line.Head;line.Reset();
                if(fence!='\0'){if(fenceLine&&symbol==fence&&length>=fenceLength&&white)fence='\0';pending=false;return;}
                if(fenceLine&&!(symbol=='`'&&backtick)){fence=symbol;fenceLength=length;pending=false;return;}
                if(lineLength>OutlineParseLimit){pending=false;return;}
                int level,titleStart;
                OutlineClassify(head,headLength,out level,out titleStart);
                if(level>0){
                    string title=OutlineLabel(OutlineAtxRaw(head,headLength,titleStart));
                    if(title!=null&&title.Length>0)AddOutlineNode(roots,parents,offset,title,level);
                    pending=false;return;
                }
                int underline=OutlineUnderline(head,headLength);
                if(underline>0){
                    if(pending&&pendingText!=null){
                        string promoted=OutlineLabel(OutlineParagraphRaw(pendingText,pendingLength));
                        if(promoted!=null&&promoted.Length>0)AddOutlineNode(roots,parents,pendingOffset,promoted,underline);
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
        return roots;
    }
    void SetupSourceOutline(){
        outline.Loaded+=delegate{HookOutlineSideways();};
        VirtualizingStackPanel.SetIsVirtualizing(outline,true);VirtualizingStackPanel.SetVirtualizationMode(outline,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(outline,true);
        outline.SelectedItemChanged+=delegate(object sender,RoutedPropertyChangedEventArgs<object> args){if(outlineRestoring||outlinePressedItem!=null)return;var node=args.NewValue as SourceOutlineNode;if(node==null||!CodeMode||displayedOutlineDocument!=source.Document||displayedOutlineVersion==null)return;
            SelectOutlineOffset(displayedOutlineVersion.MoveOffsetTo(source.Document.Version,node.Offset));
        };
        /* A press is the jump, whether or not the row was already selected: a click on the selected
           row raises no selection change, so re-clicking a heading did nothing (UI bug hunt item 8),
           and the row under the pointer is the heading the reader asked for anyway.
           ItemsControl.ContainerFromElement answers with the container the TreeView owns, which for
           a nested row is the top-level heading it hangs under: a press on "2. 가져갈 것" jumped to
           the document's first H1 whenever that root was the selected row (user video, 2026-11-01
           16:48). OutlineRowUnder stops at the row the press really landed on.
           While that press is current, a selection change it causes (the pressed row taking the
           selection, or a focus-driven selection of another row) stays out of the jump, so the click
           moves the caret exactly once, to the pressed heading. Keyboard selection in the tree still
           jumps through the Selected handlers below. */
        outline.PreviewMouseLeftButtonDown+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs args){
            if(source==null||args.ChangedButton!=System.Windows.Input.MouseButton.Left)return;
            var element=args.OriginalSource as System.Windows.DependencyObject;
            var item=OutlineRowUnder(element);
            if(item==null||OutlineOnExpander(element,item))return;
            outlinePressedItem=item;
            /* The jump waits for the input that is being handled now, so the tree finishes taking
               the selection and the caret then comes last: focusing the source inside the press
               would hand the keyboard back to the pressed row (a click that leaves the caret in
               place but the keyboard in the panel). The posted pass also owns the window's end. */
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,new Action(delegate{
                TreeViewItem pressed=outlinePressedItem;outlinePressedItem=null;JumpOutlineItem(pressed);
            }));
        };
        outline.IsVisibleChanged+=delegate{if(outline.Visibility!=Visibility.Visible){CancelSourceOutline();ClearOutlineItems();outlineSignature=null;}};Closed+=delegate{CancelSourceOutline();ClearOutlineItems();};
    }
    bool RefreshLargeSourceOutline(){
        if(source.Document.TextLength<256*1024&&source.Document.LineCount<2000){CancelSourceOutline();return false;}
        string style=ViewSignature+"|"+outline.Width;
        if(sourceOutlineDocument==source.Document&&Object.ReferenceEquals(sourceOutlineVersion,source.Document.Version)&&sourceOutlineStyle==style)return true;
        /* The rows on screen belong to the document the reader was in; a tab switch makes that a
           different document, and the new scan takes 120 ms plus the whole document before it can
           publish. The old tab's titles must not sit in the panel for that time (UI bug hunt P3-6),
           so a document change clears them now. A rebuild of the same document keeps its rows until
           the new ones replace them: the publish below reads that arrangement (fold, selection,
           scroll) back before it clears. */
        bool sameDocument=sourceOutlineDocument==source.Document;
        CancelSourceOutline();sourceOutlineDocument=source.Document;sourceOutlineVersion=source.Document.Version;sourceOutlineStyle=style;if(!sameDocument)ClearOutlineItems();sourceOutlineCancellation=new CancellationTokenSource();sourceOutlineTask=BuildSourceOutlineAsync(sourceOutlineCancellation,source.Document.CreateSnapshot());return true;
    }
    async Task BuildSourceOutlineAsync(CancellationTokenSource pending,ITextSource snapshot){
        bool published=false;
        try{
            LastSourceOutlineError=null;await Task.Delay(120,pending.Token);var nodes=await Task.Run(delegate{return ScanSourceOutline(snapshot,pending.Token);},pending.Token);
            if(pending.IsCancellationRequested||sourceOutlineCancellation!=pending||fileOpenClosed||!CodeMode||PlainText||outline.Visibility!=Visibility.Visible||sourceOutlineDocument!=source.Document||!Object.ReferenceEquals(sourceOutlineVersion,source.Document.Version))return;
            /* The large path's row matches the small path's: level bars, then the full title (no
               fixed width and no trimming - the tree scrolls sideways), level 1 SemiBold. */
            var template=new HierarchicalDataTemplate(typeof(SourceOutlineNode)) {ItemsSource=new Binding("Children")};
            var row=new FrameworkElementFactory(typeof(StackPanel));row.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
            var mark=new FrameworkElementFactory(typeof(OutlineLevelMark));mark.SetBinding(OutlineLevelMark.LevelProperty,new Binding("Level"));mark.SetValue(FrameworkElement.MarginProperty,new Thickness(0,0,7,0));mark.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);mark.SetResourceReference(OutlineLevelMark.InkProperty,"Muted");
            var label=new FrameworkElementFactory(typeof(TextBlock));label.SetBinding(TextBlock.TextProperty,new Binding("Title"));label.SetBinding(ToolTipService.ToolTipProperty,new Binding("Title"));label.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,new Binding("Label"));label.SetBinding(TextBlock.FontWeightProperty,new Binding("Weight"));label.SetValue(TextBlock.FontSizeProperty,FolderTree.RowFontSize(fontSize));label.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);label.SetResourceReference(TextBlock.ForegroundProperty,"Ink");row.AppendChild(mark);row.AppendChild(label);template.VisualTree=row;
            var style=new Style(typeof(TreeViewItem),OutlineRowStyle());/* a recycling container must take its fold from the node, not from a constant: the setters below run again for every heading a rebuild binds */style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,new Binding("Expanded"){Mode=BindingMode.TwoWay}));style.Setters.Add(new Setter(VirtualizingStackPanel.IsVirtualizingProperty,true));style.Setters.Add(new Setter(VirtualizingStackPanel.VirtualizationModeProperty,VirtualizationMode.Recycling));style.Setters.Add(new EventSetter(FrameworkElement.RequestBringIntoViewEvent,new RequestBringIntoViewEventHandler(OutlineRowBringIntoView)));
            var state=CaptureOutlineState();
            ClearOutlineItems();outline.ItemTemplate=template;outline.ItemContainerStyle=style;displayedOutlineDocument=sourceOutlineDocument;displayedOutlineVersion=sourceOutlineVersion;outline.ItemsSource=nodes;ApplyOutlineState(state);
            // The same scan answers the in-document [TOC]; its Element waits for this list.
            PublishOutlineHeadings(nodes,sourceOutlineDocument,sourceOutlineVersion);
            published=true;
        }catch(OperationCanceledException){}catch(Exception error){if(!pending.IsCancellationRequested&&sourceOutlineCancellation==pending)LastSourceOutlineError=error;}
        finally{if(sourceOutlineCancellation==pending){sourceOutlineCancellation=null;if(!published){sourceOutlineDocument=null;sourceOutlineVersion=null;sourceOutlineStyle=null;}}pending.Dispose();}
    }
    // Where a small outline item jumps: the offset it was made at, carried to the current document
    // version the way the large path maps its nodes (typing above a heading used to send the caret
    // to the heading's old position - UI bug hunt item 2).
    sealed class OutlineJump {internal TextDocument Document;internal ITextSourceVersion Version;internal int Offset;internal string Key;}
    // The row under the pointer: the first TreeViewItem on the way up from the element the event
    // named, so a nested header answers its own row and never the heading it hangs under.
    static TreeViewItem OutlineRowUnder(System.Windows.DependencyObject element){
        while(element!=null){
            var item=element as TreeViewItem;
            if(item!=null)return item;
            if(element is System.Windows.Media.Visual){element=System.Windows.Media.VisualTreeHelper.GetParent(element);continue;}
            element=LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
    static bool OutlineOnExpander(System.Windows.DependencyObject element,TreeViewItem item){
        while(element!=null&&element!=item){
            if(element is System.Windows.Controls.Primitives.ToggleButton)return true;
            var visual=element as System.Windows.Media.Visual;
            element=visual==null?null:System.Windows.Media.VisualTreeHelper.GetParent(visual);
        }
        return false;
    }
    void SelectOutlineOffset(int offset){
        if(source==null||source.Document==null)return;
        offset=Math.Max(0,Math.Min(offset,source.Document.TextLength));source.Focus();source.Select(offset,0);source.ScrollToLine(source.GetLineIndexFromCharacterIndex(offset));
        // Typewriter mode: the heading goes to the middle, as a "#heading" jump does (FocusMode.cs).
        var focusSurface=FocusSurfaceOf(source);if(focusSurface!=null)focusSurface.CentreAfterJump();
    }
    void JumpOutlineItem(TreeViewItem item){
        if(outlineRestoring||item==null||source==null||source.Document==null)return;
        var node=item.DataContext as SourceOutlineNode;
        if(node!=null){
            if(!CodeMode||displayedOutlineDocument!=source.Document||displayedOutlineVersion==null)return;
            SelectOutlineOffset(displayedOutlineVersion.MoveOffsetTo(source.Document.Version,node.Offset));return;
        }
        var jump=item.Tag as OutlineJump;
        if(jump==null||jump.Version==null||jump.Document!=source.Document)return;
        SelectOutlineOffset(jump.Version.MoveOffsetTo(source.Document.Version,jump.Offset));
    }
    /* The panel's row size, shared with the 파일 tab's file names (FolderTree.RowFontSize). The small
       path's label is built in ToolStates.OutlineItem with its own 12-16 scale, so the size is set
       here; weight and indent stay as that row made them (user, 2026-11-09). */
    void SetOutlineRowTextSize(TreeViewItem item){
        if(item==null)return;
        var row=item.Header as Panel;
        if(row==null)return;
        double size=FolderTree.RowFontSize(fontSize);
        foreach(UIElement child in row.Children){var label=child as TextBlock;if(label!=null){label.FontSize=size;return;}}
    }
    /* Sideways position of the heading list (user, 2026-11-09, three passes). The list keeps one wanted
       horizontal offset: the reader's own moves (the horizontal scroll bar under the mouse, Shift+wheel)
       set it, a click on a heading sets it to 0 so the row's start is always in view, and every other
       sideways move (bring-into-view of a long or deep row, the panel opening on the current heading,
       a selection that follows the caret) is put back to it. ScrollChanged runs inside the layout pass,
       so the put-back costs one more arrange and nothing shifted is drawn. Vertical scrolling is untouched. */
    bool outlineSidewaysHooked,outlineSidewaysByReader;
    double outlineSidewaysWanted;
    void OutlineRowBringIntoView(object sender,RequestBringIntoViewEventArgs args){HookOutlineSideways();}
    void HookOutlineSideways(){
        if(outlineSidewaysHooked)return;
        var scroller=ScrollViewerIn(outline);
        if(scroller==null)return;
        outlineSidewaysHooked=true;
        scroller.ApplyTemplate();
        var bar=scroller.Template==null?null:scroller.Template.FindName("PART_HorizontalScrollBar",scroller) as UIElement;
        scroller.PreviewMouseWheel+=delegate{
            if((Keyboard.Modifiers&ModifierKeys.Shift)==0)return;
            outlineSidewaysByReader=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate{outlineSidewaysByReader=false;}));
        };
        scroller.PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs args){
            if(bar!=null&&bar.IsMouseOver)return;
            var hit=args.OriginalSource as DependencyObject;
            while(hit!=null&&!(hit is TreeViewItem))hit=hit is Visual||hit is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(hit):LogicalTreeHelper.GetParent(hit);
            if(hit==null)return;
            outlineSidewaysWanted=0;
            if(scroller.HorizontalOffset!=0)scroller.ScrollToHorizontalOffset(0);
        };
        scroller.ScrollChanged+=delegate(object sender,ScrollChangedEventArgs args){
            if(args.HorizontalChange==0)return;
            if(outlineSidewaysByReader||bar!=null&&(bar.IsMouseOver||bar.IsMouseCaptureWithin)){outlineSidewaysWanted=args.HorizontalOffset;return;}
            /* an offset past a narrower list just clamps, so this cannot loop */
            if(Math.Abs(args.HorizontalOffset-outlineSidewaysWanted)>.01)scroller.ScrollToHorizontalOffset(outlineSidewaysWanted);
        };
    }
    TreeViewItem CreateSourceOutlineItem(SourceOutlineNode node,TextDocument document,ITextSourceVersion version){
        var item=OutlineItem(node.Title,node.Level);
        SetOutlineRowTextSize(item);
        item.RequestBringIntoView+=OutlineRowBringIntoView;
        item.Tag=new OutlineJump {Document=document,Version=version,Offset=node.Offset,Key=OutlineKey(node.Level,node.Title)};
        item.Selected+=delegate(object sender,RoutedEventArgs args){if(args.OriginalSource!=sender||outlineRestoring||outlinePressedItem!=null)return;JumpOutlineItem(item);args.Handled=true;};
        foreach(var child in node.Children)item.Items.Add(CreateSourceOutlineItem(child,document,version));return item;
    }
    void RefreshSmallSourceOutline(){
        var snapshot=source.Document.CreateSnapshot();var version=snapshot.Version;
        var nodes=ScanSourceOutline(snapshot,CancellationToken.None);
        foreach(var node in nodes)outline.Items.Add(CreateSourceOutlineItem(node,source.Document,version));
        PublishOutlineHeadings(nodes,source.Document,version);
    }
    /* What the reader arranged in the tree, so the rebuild an edit causes does not unfold every
       heading, drop the selection or put the list back at the top (UI bug hunt P2-4). A heading's
       key is its level and visible text: an offset would move whenever an earlier line is typed
       into, and two headings with the same text fold and select together, which is what a
       duplicate pair looks like to the reader anyway. */
    static string OutlineKey(int level,string title){return "H"+level+" "+title;}
    sealed class OutlineState {
        internal readonly HashSet<string> Folded=new HashSet<string>();
        internal string Selected;
        internal double Scroll;
    }
    OutlineState CaptureOutlineState(){
        var state=new OutlineState();
        var scroller=ScrollViewerIn(outline);
        state.Scroll=scroller==null?0:scroller.VerticalOffset;
        object selected=outline.SelectedItem;
        var node=selected as SourceOutlineNode;
        if(node!=null)state.Selected=OutlineKey(node.Level,node.Title);
        else{var item=selected as TreeViewItem;var jump=item==null?null:item.Tag as OutlineJump;if(jump!=null)state.Selected=jump.Key;}
        if(outline.ItemsSource!=null){
            foreach(object entry in outline.Items){var root=entry as SourceOutlineNode;if(root!=null)CaptureOutlineNode(root,state);}
        }else{
            foreach(object entry in outline.Items){var item=entry as TreeViewItem;if(item!=null)CaptureOutlineItem(item,state);}
        }
        return state;
    }
    static void CaptureOutlineNode(SourceOutlineNode node,OutlineState state){
        if(!node.Expanded&&node.Children.Count>0)state.Folded.Add(OutlineKey(node.Level,node.Title));
        foreach(var child in node.Children)CaptureOutlineNode(child,state);
    }
    static void CaptureOutlineItem(TreeViewItem item,OutlineState state){
        var jump=item.Tag as OutlineJump;
        if(jump!=null&&!item.IsExpanded&&item.Items.Count>0)state.Folded.Add(jump.Key);
        foreach(object entry in item.Items){var child=entry as TreeViewItem;if(child!=null)CaptureOutlineItem(child,state);}
    }
    void ApplyOutlineState(OutlineState state){
        if(state==null)return;
        if(outline.ItemsSource!=null){
            foreach(object entry in outline.Items){var node=entry as SourceOutlineNode;if(node!=null)ApplyOutlineNode(node,state);}
        }else{
            foreach(object entry in outline.Items){var item=entry as TreeViewItem;if(item!=null)ApplyOutlineItem(item,state);}
        }
        string selected=state.Selected;double scroll=state.Scroll;
        if(selected==null&&scroll<=0)return;
        /* The new rows, their containers and the scroll range exist only after this frame's layout,
           so the selection and the scroll go in one posted pass - selection first, so the
           BringIntoView it can cause is corrected by the offset put back after it. */
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{
            if(outline.Visibility!=Visibility.Visible)return;
            if(selected!=null)SelectOutlineKey(selected);
            if(scroll>0){var scroller=ScrollViewerIn(outline);if(scroller!=null)scroller.ScrollToVerticalOffset(scroll);}
        }));
    }
    static void ApplyOutlineNode(SourceOutlineNode node,OutlineState state){
        node.Expanded=!state.Folded.Contains(OutlineKey(node.Level,node.Title));
        foreach(var child in node.Children)ApplyOutlineNode(child,state);
    }
    static void ApplyOutlineItem(TreeViewItem item,OutlineState state){
        var jump=item.Tag as OutlineJump;
        if(jump!=null)item.IsExpanded=!state.Folded.Contains(jump.Key);
        foreach(object entry in item.Items){var child=entry as TreeViewItem;if(child!=null)ApplyOutlineItem(child,state);}
    }
    // Restoring the selection must not run the jump: the caret stays where the user left it while
    // editing. outlineRestoring silences the small path's Selected handler and the large path's
    // SelectedItemChanged one for that one assignment.
    void SelectOutlineKey(string key){
        TreeViewItem item=OutlineItemForKey(key);
        if(item==null)return;
        outlineRestoring=true;
        try{item.IsSelected=true;}finally{outlineRestoring=false;}
    }
    // Best effort: a virtualized tree realizes only the rows on screen, and a heading that never
    // realized has no item to select (its fold and the scroll still come back).
    TreeViewItem OutlineItemForKey(string key){
        foreach(object entry in outline.Items){
            var item=entry as TreeViewItem;
            if(item!=null){var found=OutlineItemWithKey(item,key);if(found!=null)return found;continue;}
            var node=entry as SourceOutlineNode;
            if(node==null)continue;
            var container=outline.ItemContainerGenerator.ContainerFromItem(node) as TreeViewItem;
            var match=OutlineNodeContainer(container,node,key);
            if(match!=null)return match;
        }
        return null;
    }
    static TreeViewItem OutlineItemWithKey(TreeViewItem item,string key){
        var jump=item.Tag as OutlineJump;
        if(jump!=null&&jump.Key==key)return item;
        foreach(object entry in item.Items){var child=entry as TreeViewItem;if(child==null)continue;var found=OutlineItemWithKey(child,key);if(found!=null)return found;}
        return null;
    }
    static TreeViewItem OutlineNodeContainer(TreeViewItem container,SourceOutlineNode node,string key){
        if(container==null)return null;
        if(OutlineKey(node.Level,node.Title)==key)return container;
        /* A folded node's children are not in the container tree, and the fold was just restored
           from the reader's state; expanding it here would fight what the user set. */
        if(!container.IsExpanded)return null;
        foreach(var child in node.Children){
            var next=container.ItemContainerGenerator.ContainerFromItem(child) as TreeViewItem;
            var found=OutlineNodeContainer(next,child,key);
            if(found!=null)return found;
        }
        return null;
    }
}}
