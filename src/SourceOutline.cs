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
    void CancelSourceOutline(){if(sourceOutlineCancellation!=null){sourceOutlineCancellation.Cancel();sourceOutlineCancellation=null;}sourceOutlineDocument=null;sourceOutlineVersion=null;sourceOutlineStyle=null;}
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
        if(outer&&OutlineAtx(text,length,afterQuote,out level,out titleStart))return;
        int body;
        if(OutlineList(text,length,quotes>0?q:0,out body)){
            int itemQuote=body,inner=0;
            while(itemQuote<length&&text[itemQuote]=='>'&&inner<6){itemQuote++;inner++;if(itemQuote<length&&(text[itemQuote]==' '||text[itemQuote]=='\t'))itemQuote++;}
            if(OutlineAtx(text,length,itemQuote,out level,out titleStart))return;
        }
        level=0;titleStart=0;
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
            var template=new HierarchicalDataTemplate(typeof(SourceOutlineNode)) {ItemsSource=new Binding("Children")};var label=new FrameworkElementFactory(typeof(TextBlock));label.SetBinding(TextBlock.TextProperty,new Binding("Label"));label.SetBinding(ToolTipService.ToolTipProperty,new Binding("Title"));label.SetBinding(System.Windows.Automation.AutomationProperties.NameProperty,new Binding("Label"));label.SetValue(TextBlock.FontWeightProperty,FontWeights.Normal);label.SetValue(TextBlock.FontSizeProperty,Math.Max(12,Math.Min(16,fontSize)));label.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);label.SetValue(FrameworkElement.WidthProperty,Math.Max(45,outline.Width-55));label.SetValue(FrameworkElement.MarginProperty,new Thickness(4,3,4,3));label.SetResourceReference(TextBlock.ForegroundProperty,"Ink");template.VisualTree=label;
            var style=new Style(typeof(TreeViewItem),(Style)Resources[typeof(TreeViewItem)]);/* a recycling container must take its fold from the node, not from a constant: the setters below run again for every heading a rebuild binds */style.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,new Binding("Expanded"){Mode=BindingMode.TwoWay}));style.Setters.Add(new Setter(VirtualizingStackPanel.IsVirtualizingProperty,true));style.Setters.Add(new Setter(VirtualizingStackPanel.VirtualizationModeProperty,VirtualizationMode.Recycling));
            var state=CaptureOutlineState();
            ClearOutlineItems();outline.ItemTemplate=template;outline.ItemContainerStyle=style;displayedOutlineDocument=sourceOutlineDocument;displayedOutlineVersion=sourceOutlineVersion;outline.ItemsSource=nodes;ApplyOutlineState(state);published=true;
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
    TreeViewItem CreateSourceOutlineItem(SourceOutlineNode node,TextDocument document,ITextSourceVersion version){
        var item=OutlineItem(node.Title,node.Level);
        item.Tag=new OutlineJump {Document=document,Version=version,Offset=node.Offset,Key=OutlineKey(node.Level,node.Title)};
        item.Selected+=delegate(object sender,RoutedEventArgs args){if(args.OriginalSource!=sender||outlineRestoring||outlinePressedItem!=null)return;JumpOutlineItem(item);args.Handled=true;};
        foreach(var child in node.Children)item.Items.Add(CreateSourceOutlineItem(child,document,version));return item;
    }
    void RefreshSmallSourceOutline(){
        var snapshot=source.Document.CreateSnapshot();var version=snapshot.Version;
        foreach(var node in ScanSourceOutline(snapshot,CancellationToken.None))outline.Items.Add(CreateSourceOutlineItem(node,source.Document,version));
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
