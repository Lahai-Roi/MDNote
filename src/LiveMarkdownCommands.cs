using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
namespace MDNote {
// P4 host: with the liveMarkdown switch on, toolbar/shortcut formatting edits the Markdown
// source through MarkdownCommands (pure functions) instead of the rich FlowDocument.
// One command = one compound history entry; the editor's own undo stack stays disabled.
public partial class MainWindow {
    bool LiveFormat {get{return CodeMode&&!PlainText&&editing&&!documentOperationBusy&&source!=null&&source.Visibility==System.Windows.Visibility.Visible;}}
    bool RunLiveCommand(Func<TextDocument,int,int,CommandResult> command){
        if(!LiveFormat)return false;
        // The command reads the selection only after a pending IME commit has landed (#7).
        var owner=source;var document=owner.Document;
        try{owner.RunAfterComposition(delegate{
            if(!LiveFormat||!Object.ReferenceEquals(owner,source)||!Object.ReferenceEquals(document,source.Document))return;
            try{
                FlushDocumentChanges();
                CommandResult result=command(document,source.SelectionStart,source.SelectionLength);
                if(result!=null)ApplyCommandResult(result);
                source.Focus();
            }catch(Exception ex){ReportCommandFailure(ex);}
        });}catch(Exception ex){ReportCommandFailure(ex);}
        return true;
    }
    static void ReportCommandFailure(Exception error){System.Diagnostics.Trace.TraceError("MDNote command rejected: {0}: {1}",error.GetType().Name,error.Message);}
    // A caret-only style toggle (Ctrl+B and friends, the colour and size pickers) does not write
    // an empty marker pair: the style is armed for the text typed next and the pair is written
    // with that first input. A caret move, any applied command or a second toggle of the same
    // style drops the arm, so no raw "****" (or empty span) is ever left in the source and the
    // toggle itself records no history entry. Armed styles stack (outermost first), so a size
    // picked before Bold still wraps the first typed text together:
    // <span style="font-size:24px">**가**</span>, the way Word applies both to the typing point.
    sealed class ArmedPair {
        internal string Open;
        internal string Close;
        internal string Marker;
    }
    List<ArmedPair> typingArmed;
    string typingOpen,typingClose;
    int typingAt=-1;
    TextDocument typingDocument;
    ICSharpCode.AvalonEdit.Document.ITextSourceVersion typingVersion;
    ICSharpCode.AvalonEdit.Editing.TextArea typingCaretArea;
    EventHandler typingCaretHandler,typingTextHandler;
    // True while the armed styles still belong to the caret: same surface, same document, text
    // unchanged since the toggle, and a collapsed caret that has not moved. One move is the armed
    // code pair's own: the live view's closing-edge rule steps a caret right after a closed
    // highlight to the run's inner edge (the same drawn spot), and the arm survives it
    // (ClosingEdgeArmSpot).
    bool TypingStyleArmed{
        get{
            if(typingOpen==null||!LiveFormat||source==null||typingDocument==null)return false;
            if(!Object.ReferenceEquals(source.Document,typingDocument))return false;
            if(!Object.ReferenceEquals(source.Document.Version,typingVersion))return false;
            if(source.SelectionLength!=0)return false;
            return source.SelectionStart==typingAt||ClosingEdgeArmSpot();
        }
    }
    // 11-09: with inline code armed on a closed highlight's outer edge ("==테스트==|"), the live
    // view's closing-edge rule (HiddenCloseTypingEntering) moves the caret to the run's inner edge
    // before the first character lands. That move must not spend the arm: the pair is written at
    // the arming spot, where WrapTyped's exclusive rule splits the highlight and writes the code
    // pair outside it ("==테스트==" + "`X`"). Bold, italic, strike and the span styles keep the
    // older answer (the typed text takes the run, the arm ends on the move).
    bool ClosingEdgeArmSpot(){
        if(typingOpen==null||typingArmed==null||source==null||typingDocument==null)return false;
        if(!Object.ReferenceEquals(source.Document,typingDocument))return false;
        if(!Object.ReferenceEquals(source.Document.Version,typingVersion))return false;
        if(source.SelectionLength!=0||source.CaretIndex==typingAt)return false;
        bool codeArmed=false;
        for(int i=0;i<typingArmed.Count;i++)if(typingArmed[i].Marker=="`"){codeArmed=true;break;}
        if(!codeArmed||!LivePreviewAttached||livePreview==null)return false;
        // The moved spot must be the inner edge the preview's rule names for this caret (the
        // innermost closer of the stacked run at typingAt), and that run must be a closed
        // highlight the exclusive replacement actually splits.
        var document=source.Document;
        DocumentLine line=document.GetLineByOffset(typingAt);
        int start,end;
        if(!MarkdownLivePreview.Session.HiddenCloseRun(livePreview.Info(line),typingAt-line.Offset,out start,out end))return false;
        if(source.CaretIndex!=line.Offset+start)return false;
        return MarkdownCommands.CaretAfterClosedHighlight(document,typingAt);
    }
    void ClearTypingStyle(){typingArmed=null;typingOpen=null;typingClose=null;typingAt=-1;typingDocument=null;typingVersion=null;UnhookTypingStyleCaret();if(source!=null)source.TextArea.Caret.Refresh();}
    // The style property a caret-armed span pair writes ("font-size", "color"), null for a marker
    // pair. A size and a colour each replace the armed one of their own kind instead of stacking.
    static string ArmedSpanProperty(string open){
        const string prefix="<span style=\"";
        if(open==null||!open.StartsWith(prefix,StringComparison.Ordinal))return null;
        int colon=open.IndexOf(':',prefix.Length);
        if(colon<0)return null;
        string name=open.Substring(prefix.Length,colon-prefix.Length).Trim();
        if(name.Equals("font-size",StringComparison.OrdinalIgnoreCase))return "font-size";
        if(name.Equals("color",StringComparison.OrdinalIgnoreCase))return "color";
        return null;
    }
    // The value one armed span pair carries, null when it is a marker pair or writes another property.
    static string ArmedSpanValue(string open,string property){
        const string prefix="<span style=\"";
        if(open==null||!open.StartsWith(prefix,StringComparison.Ordinal))return null;
        int colon=open.IndexOf(':',prefix.Length);
        if(colon<0)return null;
        if(!String.Equals(open.Substring(prefix.Length,colon-prefix.Length).Trim(),property,StringComparison.OrdinalIgnoreCase))return null;
        int end=colon+1;
        while(end<open.Length&&open[end]!=';'&&open[end]!='"'&&open[end]!='\'')end++;
        return open.Substring(colon+1,end-colon-1).Trim();
    }
    static ArmedPair NewArmedPair(CommandResult result){return new ArmedPair {Open=result.TypingOpen,Close=result.TypingClose,Marker=result.TypingMarker};}
    // The opening runs in list order and the closing runs in reverse, so the pairs nest.
    string CombinedTypingOpen(){
        string value="";
        for(int i=0;i<typingArmed.Count;i++)value+=typingArmed[i].Open;
        return value;
    }
    string CombinedTypingClose(){
        string value="";
        for(int i=typingArmed.Count-1;i>=0;i--)value+=typingArmed[i].Close;
        return value;
    }
    // Arming a pair again switches that one style off before any input. A size or a colour
    // replaces the armed one of its own kind (two sizes never stack); any other style is added
    // inside the armed ones.
    void ArmTypingStyle(CommandResult result){
        if(result==null||result.TypingOpen==null)return;
        if(!TypingStyleArmed)ClearTypingStyle();
        if(typingArmed==null)typingArmed=new List<ArmedPair>();
        string property=ArmedSpanProperty(result.TypingOpen);
        // 11-09 user: highlight and inline code replace one another; the later choice wins.
        string opposite=result.TypingMarker=="=="?"`":result.TypingMarker=="`"?"==":null;
        if(opposite!=null)typingArmed.RemoveAll(delegate(ArmedPair pair){return pair.Marker==opposite;});
        int index=-1;
        for(int i=0;i<typingArmed.Count;i++){
            ArmedPair pair=typingArmed[i];
            if(property!=null){if(String.Equals(ArmedSpanProperty(pair.Open),property,StringComparison.Ordinal)){index=i;break;}}
            else if(String.Equals(pair.Open,result.TypingOpen,StringComparison.Ordinal)&&String.Equals(pair.Close,result.TypingClose,StringComparison.Ordinal)){index=i;break;}
        }
        if(index<0)typingArmed.Add(NewArmedPair(result));
        else if(property!=null)typingArmed[index]=NewArmedPair(result);
        else typingArmed.RemoveAt(index);
        if(typingArmed.Count==0){ClearTypingStyle();return;}
        typingOpen=CombinedTypingOpen();typingClose=CombinedTypingClose();
        typingAt=Math.Max(0,Math.Min(result.SelectionStart,source.Document.TextLength));
        typingDocument=source.Document;typingVersion=source.Document.Version;
        HookTypingStyleCaret();
        source.PendingImeStyle=PendingImeStyleNow;
        // 11-08 user: a size picked before typing left the caret at the old height until the first syllable composed.
        source.TextArea.Caret.PendingEmSize=PendingTypingSize;source.TextArea.Caret.Refresh();
    }
    // 11-08 user: a syllable composed right after B / I / S / highlight / code / colour was drawn plain until it committed (the pair is written with the commit).
    // Every armed style shows on the composition string together (size and bold both), read from
    // the same pairs the commit writes; null when nothing is armed.
    ImePendingStyle PendingImeStyleNow(){
        if(!TypingStyleArmed||typingArmed==null)return null;
        var style=new ImePendingStyle();
        for(int i=0;i<typingArmed.Count;i++){
            ArmedPair pair=typingArmed[i];
            if(pair.Marker=="**")style.Bold=true;
            else if(pair.Marker=="*")style.Italic=true;
            else if(pair.Marker=="~~")style.Strike=true;
            else if(pair.Marker=="==")style.Highlight=true;
            else if(pair.Marker=="`")style.Code=true;
            string color=ArmedSpanValue(pair.Open,"color");
            if(color!=null&&style.Color==null){
                try{var brush=new BrushConverter().ConvertFromString(color) as Brush;if(brush!=null){brush.Freeze();style.Color=brush;}}catch(FormatException){}catch(NotSupportedException){}
            }
        }
        style.Size=PendingTypingSize();
        if(style.Code){style.CodeFamily=MarkdownLivePreview.InlineCodeFamily;if(LivePreviewAttached)style.CodeBackground=livePreview.Colors.InlineCode;}
        if(style.Highlight&&LivePreviewAttached)style.HighlightBackground=livePreview.Colors.Highlight;
        if(!style.Bold&&!style.Italic&&!style.Strike&&!style.Highlight&&!style.Code&&style.Color==null&&style.Size<=0)return null;
        return style;
    }
    // A caret that leaves the armed spot (a click, Home/End, an arrow, a selection) cancels the
    // style at once, like Word; the offset alone cannot tell that the caret came back, so the
    // arm listens for the move itself.
    void HookTypingStyleCaret(){
        if(typingCaretHandler!=null||source==null)return;
        typingCaretArea=source.TextArea;
        typingCaretHandler=new EventHandler(TypingStyleCaretMoved);
        typingCaretArea.Caret.PositionChanged+=typingCaretHandler;
        // An edit that leaves the caret where it was (Delete at the end of a word, undo, a paste
        // elsewhere) also ends the arm: the version check in TypingStyleArmed already says so, but
        // nothing asked the toolbar to drop its lit button.
        typingTextHandler=new EventHandler(TypingStyleTextChanged);
        source.TextChanged+=typingTextHandler;
    }
    void UnhookTypingStyleCaret(){
        if(typingCaretHandler==null)return;
        if(typingCaretArea!=null)typingCaretArea.Caret.PositionChanged-=typingCaretHandler;
        if(typingTextHandler!=null&&source!=null)source.TextChanged-=typingTextHandler;
        typingCaretArea=null;typingCaretHandler=null;typingTextHandler=null;
    }
    void TypingStyleCaretMoved(object sender,EventArgs e){
        if(typingOpen==null){UnhookTypingStyleCaret();return;}
        if(source==null||source.SelectionLength!=0||source.CaretIndex!=typingAt){
            // The closing-edge step the armed code pair survives is not a real caret move (see
            // ClosingEdgeArmSpot); every other move ends the arm.
            if(source!=null&&source.SelectionLength==0&&ClosingEdgeArmSpot())return;
            ClearTypingStyle();UpdateTools();
        }
    }
    void TypingStyleTextChanged(object sender,EventArgs e){
        if(typingOpen==null){UnhookTypingStyleCaret();return;}
        if(!TypingStyleArmed){ClearTypingStyle();if(LiveFormat)UpdateTools();}
    }
    // The size a pending span style will write, 0 when no armed pair carries one. Read from the
    // armed opening tags, so the size box shows the pending value instead of the caret's size.
    double PendingTypingSize(){
        if(!TypingStyleArmed||typingArmed==null)return 0;
        for(int i=0;i<typingArmed.Count;i++){
            string size=ArmedSpanValue(typingArmed[i].Open,"font-size");
            if(size==null)continue;
            int end=0;
            while(end<size.Length&&(Char.IsDigit(size[end])||size[end]=='.'))end++;
            double value;
            if(end>0&&Double.TryParse(size.Substring(0,end),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out value))return value;
        }
        return 0;
    }
    // Whitespace cannot sit inside an emphasis pair (the markers would stay literal) and a
    // newline is the line's own structural command, so such input ends the armed style instead
    // of being wrapped in it.
    static bool EndsTypingStyle(string text){return String.IsNullOrEmpty(text)||Char.IsWhiteSpace(text[0]);}
    // The armed pairs written around the first input, applied as the same command path every
    // toolbar command takes: one history entry holding the markers and the typed text together.
    bool WrapTypingStyle(string text){
        string exclusive=null;
        if(typingArmed!=null)foreach(ArmedPair pair in typingArmed)if(pair.Marker=="=="||pair.Marker=="`")exclusive=pair.Marker;
        CommandResult wrapped=MarkdownCommands.WrapTyped(source.Document,typingAt,typingOpen,typingClose,text,exclusive);
        ClearTypingStyle();
        if(wrapped==null)return false;
        // The pair is recorded as typing, not as a command: the rest of the run is typed before the
        // closing markers, and the typing coalescer grows this same entry for those characters, so
        // one Ctrl+Z takes the whole run (`**abc**`), exactly like typing without a pending style.
        applyingTypedRun=true;
        try{ApplyCommandResult(wrapped,-1,0);}finally{applyingTypedRun=false;}
        source.EndTypingRun();
        return true;
    }
    // Commands may remove a prefix and insert its replacement at the SAME pre-edit offset
    // (SetHeading and list conversion). History requires one non-overlapping replacement there.
    static List<DocumentHistory.Edit> PrepareCommandParts(TextDocument document,CommandResult result){
        var parts=new List<DocumentHistory.Edit>();long total=document.TextLength;
        foreach(var edit in result.Edits){
            if(edit.Offset<0||edit.Length<0||edit.Offset>document.TextLength-edit.Length)throw new ArgumentException("Command edit outside document");
            string inserted=edit.Text??"";DocumentHistory.Edit previous=parts.Count==0?null:parts[parts.Count-1];
            if(previous!=null&&edit.Offset>previous.At)throw new ArgumentException("Command edits are not descending");
            if(previous!=null&&edit.Offset==previous.At){
                if(edit.Length!=0)throw new ArgumentException("Command replacements overlap");
                previous.Inserted=inserted+previous.Inserted;
            }else{
                if(previous!=null&&(long)edit.Offset+edit.Length>previous.At)throw new ArgumentException("Command replacements overlap");
                parts.Add(new DocumentHistory.Edit {At=edit.Offset,Removed=document.GetText(edit.Offset,edit.Length),Inserted=inserted});
            }
            total+=(long)inserted.Length-edit.Length;
            if(total<0||total>Int32.MaxValue)throw new ArgumentException("Command exceeds document limit");
        }
        parts.RemoveAll(delegate(DocumentHistory.Edit part){return part.Removed==part.Inserted;});
        // Exercise the exact history contract BEFORE touching the buffer (also preserves redo on rejection).
        new DocumentHistory().RecordChanges(parts);
        return parts;
    }
    // Set only by WrapTypingStyle, around the one apply that is the run's first typed input.
    bool applyingTypedRun;
    void ApplyCommandResult(CommandResult result,int beforeSelectionStart=-1,int beforeSelectionLength=0){
        bool typedRun=applyingTypedRun;applyingTypedRun=false;
        if(result==null)return;
        // A caret-only toggle arms the typing style: there is nothing to write yet.
        if(result.TypingOpen!=null){ArmTypingStyle(result);UpdateTools();return;}
        // Any applied command ends a style that was only armed for the text typed next.
        if(typingOpen!=null)ClearTypingStyle();
        var document=source.Document;int beforeCaret=source.CaretIndex,beforeStart=source.SelectionStart,beforeLength=source.SelectionLength;
        var snapshot=document.CreateSnapshot();bool wasLoading=loading,changed=false;
        try{
            var parts=PrepareCommandParts(document,result);
            /* typedRun: the pending style pair is the run's first typed input. The document update
               records itself through the normal typing path (typed entry, caret included), so the
               following characters of the run can merge into it. The caret goes inside the pair
               before the update closes: the entry then records the spot the next character belongs
               to (TypedEnd), and undo/redo put the caret back into the pair instead of after the
               closing markers. */
            if(!typedRun)loading=true;
            int start,length;
            if(parts.Count>0){
                changed=true;document.BeginUpdate();try{
                    foreach(var part in parts)document.Replace(part.At,part.Removed.Length,part.Inserted);
                    start=Math.Max(0,Math.Min(result.SelectionStart,document.TextLength));
                    length=Math.Max(0,Math.Min(result.SelectionLength,document.TextLength-start));
                    if(typedRun)source.Select(start,length);
                }finally{document.EndUpdate();}
            }else{
                start=Math.Max(0,Math.Min(result.SelectionStart,document.TextLength));
                length=Math.Max(0,Math.Min(result.SelectionLength,document.TextLength-start));
            }
            if(!typedRun)source.Select(start,length);
            // An action that supplies its own selections (a drag move) restores and reapplies them
            // with the text; commands keep the older caret-only restore.
            if(!typedRun)History.RecordChanges(parts,beforeCaret,source.CaretIndex,beforeSelectionStart,beforeSelectionLength,beforeSelectionStart>=0?source.SelectionStart:-1,source.SelectionLength);
            if(parts.Count>0){sourceSnapshotDirty=true;sourceOutlineDirty=true;}
        }catch(Exception ex){
            // Snapshots share the immutable text tree, O(1) on the normal path. Only a failed
            // application materializes the old text; never retain a partially applied command.
            loading=true;
            try{if(changed)document.Text=snapshot.Text;source.Select(beforeStart,beforeLength);source.CaretIndex=beforeCaret;}catch(Exception restore){ReportCommandFailure(restore);}
            ReportCommandFailure(ex);return;
        }finally{loading=wasLoading;}
        UpdateTitle();RefreshOutline();UpdateTools();
    }
    // P6: rich clipboard formats become Markdown source; plain text and anything pasted
    // inside fenced code is inserted verbatim (no escaping). One paste = one history entry.
    bool LivePaste(System.Windows.IDataObject data,bool plain,string value){
        if(!LiveFormat)return false;
        /* Basic view: a hidden fence line is structure. A paste over a selection that holds only one fence of a block is swallowed, or the partner fence would be left behind (the typing, Delete and Cut rule). */
        if(LivePreviewAttached&&livePreview!=null&&livePreview.SelectionBreaksFence())return true;
        /* A paste at a caret inside a table cell belongs at that caret. The hidden-marker paste snap
           pulls a caret on a closing bold, span or backtick run's outer edge back inside the pair so pasted text
           keeps the style, but a cell is the input unit: H10 gives typed text the same exemption
           (MarkdownLivePreview.HiddenCloseTypingEntering) and the cell command below reads the
           selection before it inserts. When the snap moved a caret that is a valid position in the
           cell's own text, put it back so the paste lands on the spot the user's caret was on. */
        int pasteStart=source.SelectionStart,pasteLength=source.SelectionLength;
        /* Ctrl+V does not go through ApplicationCommands.Paste, so the hidden-marker step runs here: an endpoint inside a marker moves to its edge. The pairs the selection cuts are not widened any more (2.3.1 D1); the markers it cut off are written back with the paste (RebalancedReplace). */
        if(LivePreviewAttached&&livePreview!=null)livePreview.PrepareHiddenPasteSelection();
        /* 2.3.1: a caret on a closing run's outer edge pastes like the paragraph does, inside the pair (the snap above), cell or not: a text put between two stacked closers broke "_" pairs and drew bold-italic after the pair. Any other snapped caret that is a valid cell position still goes back. */
        bool snappedInCell=pasteLength==0&&source.SelectionLength==0&&source.SelectionStart!=pasteStart&&CaretInCellText(source.Document,pasteStart);
        if(snappedInCell&&!PasteOnCloserEdge(pasteStart))source.Select(pasteStart,0);
        /* A paragraph follows the same closing-backtick exception (HANDOFF: after the closing backtick is outside code): the snap moved the caret into the span, so it goes back. */
        else if(!snappedInCell&&pasteLength==0&&source.SelectionLength==0&&source.SelectionStart!=pasteStart&&PasteAfterClosingBacktick(pasteStart))source.Select(pasteStart,0);
        string markdown=plain?null:MarkdownClipboard.Get(data);
        if(markdown!=null)markdown=NormalizeLineEndings(markdown,documentNewline);
        bool code=LivePreviewAttached&&livePreview.InCode(source.SelectionStart);
        // An inline code span is code too: the P6 rule "anything pasted inside code is verbatim"
        // covers it, or a formatted paste turns into "co**굵게**de" and the span draws the stars
        // (code has no inline markup). LiveInlineCodeAt reads the same per-line cache, O(line).
        if(!code&&LivePreviewAttached)code=LiveInlineCodeAt(source.SelectionStart);
        if(markdown==null&&!plain&&!code&&data.GetDataPresent(System.Windows.DataFormats.Html)){
            string html=data.GetData(System.Windows.DataFormats.Html) as string;
            if(!String.IsNullOrEmpty(html)&&html.Length<8*1024*1024){softPasteLiteral=true;try{markdown=RelaxPasteBrackets(ClipboardMarkdown(html));}finally{softPasteLiteral=false;}}
        }
        if(markdown==null&&!plain&&!code&&data.GetDataPresent(System.Windows.DataFormats.Rtf)){
            string rtf=data.GetData(System.Windows.DataFormats.Rtf) as string;
            if(!String.IsNullOrEmpty(rtf)&&rtf.Length<8*1024*1024){
                try{
                    var imported=new System.Windows.Documents.FlowDocument {FontSize=fontSize,FontFamily=new FontFamily(family)};
                    using(var stream=new MemoryStream(System.Text.Encoding.ASCII.GetBytes(NormalizeRtfUnicode(rtf))))new System.Windows.Documents.TextRange(imported.ContentStart,imported.ContentEnd).Load(stream,System.Windows.DataFormats.Rtf);
                    NormalizePasteDocument(imported);markdown=MarkdownClipboard.NormalizeExternal(MarkdownWriter.Write(imported));
                }catch(ArgumentException){}catch(System.Windows.Markup.XamlParseException){}catch(IOException){}catch(FormatException){/* malformed RTF from another program (FileFormatException) */}
            }
        }
        if(markdown==null&&!plain&&!code)markdown=MarkdownClipboard.NormalizeExternal(value);
        // #18: plain text keeps its paragraph blank lines; only the line ending is normalized.
        // Every path lands on the document's own newline: the clipboard's plain text (a Ctrl+V of
        // a Notepad copy used to write CRLF into an LF document) and the HTML/RTF conversions,
        // which join their lines with "\n" of their own.
        // 11-01 user: 서식 없이 붙여넣기 wrote the copied Markdown source back, so its quote, list
        // and bold were drawn again. A plain paste writes the visible text: MDNote's own copy is
        // rendered to text first, and every character that would start markup is escaped, so the
        // pasted text shows exactly as copied without any style. Code keeps the verbatim rule.
        if(markdown==null&&plain&&!code&&value!=null){
            string own=MarkdownClipboard.Get(data);
            string visible=own!=null?MarkdownPlainText.Convert(MarkdownClipboard.CanonicalNewlines(own)):value;
            markdown=NormalizeLineEndings(SoftLiteral(visible),documentNewline);
        }
        else if(markdown==null)markdown=value==null?null:NormalizeLineEndings(value,documentNewline);
        else markdown=NormalizeLineEndings(markdown,documentNewline);
        if(markdown==null)return true;
        /* A pasted text that is one complete span of its own ("**`a`**", "`a`") is not a character typed next to a closer: in a cell it goes where the caret was (the cell text's end after the closer), as its own span, like the "`a`X" exemption above. The snap into the pair would make StripEnclosingStyles drop its markers and merge it into the old pair. Plain text and partial markup keep the snap. */
        if(snappedInCell&&!code&&source.SelectionLength==0&&source.SelectionStart!=pasteStart&&PasteIsOwnSpan(markdown))source.Select(pasteStart,0);
        // A selection that covers whole tables is ordinary text: the paste replaces the whole block
        // below (pipes included) instead of landing in its first cell.
        bool wholeTables=LivePreviewAttached&&source.SelectionLength>0&&MarkdownTableCommands.SelectionCoversWholeTables(source.Document,source.SelectionStart,source.SelectionLength);
        // The |---| line is structure, not a cell row: typing on it is already swallowed, so a
        // paste must not append text there either (it ends the table: the header loses its
        // delimiter row). With the caret on the folded delimiter - where the drawn position is
        // the header row's end - the paste lands in the header's last cell instead.
        if(LivePreviewAttached&&source.SelectionLength==0){
            var caretLine=source.Document.GetLineByOffset(Math.Max(0,Math.Min(source.SelectionStart,source.Document.TextLength)));
            var caretInfo=livePreview.Info(caretLine);
            if(caretInfo!=null&&caretInfo.Delimiter){
                var header=caretLine.PreviousLine;
                var headerInfo=header==null?null:livePreview.Info(header);
                if(headerInfo==null||headerInfo.Cells==null||headerInfo.Cells.Count==0)return true;
                source.Select(header.Offset+headerInfo.Cells[headerInfo.Cells.Count-1].End,0);
            }
        }
        // A selection that starts or ends on the delimiter row is structure too: replacing it
        // would rewrite or remove the row and end the table, so the paste is swallowed. The
        // caret-only case above moves into the header's last cell instead of refusing the paste.
        if(LivePreviewAttached&&!wholeTables&&SelectionTouchesDelimiter())return true;
        /* 2.3.1: a formatted paste lands inside the caret line's emphasis pairs; the markers of the kinds that already enclose it would collide with the pair's own, so they are dropped from the pasted text (both views, point and selection, table cells too). */
        if(!plain&&!code)markdown=StripEnclosingStyles(markdown,source.SelectionStart);
        /* 11-09 user: a multi-line paste inside an inline pair (==, `, <span>) split the pair - the break landed between its markers and the
           tail drew raw "</span>==". An inline pair never crosses a line, so the block is inserted as its visible text and every line break
           is written as the enclosing pairs' closers + newline + openers, the same split Enter writes (MarkdownInlineSpans.Split). */
        if(LivePreviewAttached&&livePreview!=null)markdown=SpanBalancedPaste(markdown,source.SelectionStart,source.SelectionLength,plain,code);
        // Stage 4: pasting into a table cell follows the basic view - the pasted lines become the
        // cell's paragraphs, saved as "<br>", and a cell-spanning selection keeps the "|"s.
        if(LivePreviewAttached&&!wholeTables){
            CommandResult cell=MarkdownTableCommands.PasteInCell(source.Document,source.SelectionStart,source.SelectionLength,markdown,PasteRowCells(source.Document,source.SelectionStart));
            if(cell!=null){ApplyCommandResult(RebalancedCellPaste(cell));source.Focus();return true;}
            // fix13: a paste on the blank separator line under a table row (or above a header row)
            // would join the table the way typed text would, so it writes the same newline; the
            // paste and the newline are one command, hence one undo step.
            CommandResult gap=PasteTableGap(markdown);
            if(gap!=null){ApplyCommandResult(gap);source.Focus();return true;}
        }
        // P3: a multi-line paste into a list item or a quote continues the caret line's own prefix
        // on the lines after the first, exactly what Enter on that line writes (the list/quote
        // continuation rule). Code stays literal, and the table rules above already answered.
        if(!code&&(markdown.IndexOf('\n')>=0||markdown.IndexOf('\r')>=0)){
            string prefix=PasteContinuationPrefix(source.Document,source.SelectionStart);
            if(prefix!=null)markdown=ContinuePastedLines(markdown,prefix,documentNewline);
        }
        // A paste whose first line carries a cell pipe on the line directly under a table would
        // make that line one more row of the table (the renderer reads every piped line after a
        // row as a row), so the paste starts on its own line instead and the paragraph keeps
        // ending the table. A cell paste answered above; this is the line past the last row.
        if(!code){
            CommandResult belowTable=PasteBelowTable(markdown);
            if(belowTable!=null){ApplyCommandResult(belowTable);source.Focus();return true;}
        }
        /* 2.3.1 D1: a paste over a selection that cut a hidden marker writes the markers back next to the pasted text, in the same command result (one undo step). */
        /* The history entry keeps the selection the paste replaced, so one Ctrl+Z puts the text and the selection back like the plain replacement below. */
        int balancedSelectionStart=source.SelectionStart,balancedSelectionLength=source.SelectionLength;
        CommandResult balanced=RebalancedReplace(markdown);
        if(balanced!=null){ApplyCommandResult(balanced,balancedSelectionStart,balancedSelectionLength);source.Focus();return true;}
        int at=source.SelectionStart;source.SelectedText=markdown;source.Select(at+markdown.Length,0);source.Focus();
        return true;
    }
    // The same rule as the toolbar's "inner same style of a link label is removed": a paste that lands
    // inside bold, italic, strike or highlight pairs of the caret line (the caret, or the start of a selection) does
    // not repeat those kinds, because "**번**굵게**글자**" closes the outer pair early and pairs the stars
    // wrongly. Only the markers MarkdownLivePreview.ParseInlineText pairs in the pasted FIRST line are
    // dropped; its other styles and literal stars stay. A later pasted line cannot join the caret line's
    // pairs (a pair never crosses a line), so lines after the first are left alone. O(caret line + first
    // pasted line), from the line cache.
    string StripEnclosingStyles(string markdown,int at){
        if(String.IsNullOrEmpty(markdown)||!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return markdown;
        var document=source.Document;
        if(at<0||at>document.TextLength)return markdown;
        int stop=markdown.IndexOfAny(new char[]{'\r','\n'});
        string head=stop<0?markdown:markdown.Substring(0,stop);
        if(head.IndexOfAny(new char[]{'*','_','~','='})<0)return markdown;
        var line=document.GetLineByOffset(at);
        var info=livePreview.Info(line);
        if(info==null||info.Text==null||info.Text.Length!=line.Length||info.Code||info.FenceLine||info.Delimiter||info.SetextUnderline>0)return markdown;
        bool selected=source.SelectionLength>0;
        /* A selection that leaves a table row is not one cell's text: the cell rules own that paste. */
        if(selected&&info.Table&&source.SelectionStart+source.SelectionLength>line.EndOffset)return markdown;
        int relative=at-line.Offset;
        bool bold=false,italic=false,strike=false,highlight=false;
        foreach(MarkdownInlineSpans.Span span in MarkdownInlineSpans.Build(info,info.Text)){
            if(span.Kind!=MarkdownInlineSpans.SpanKind.Emphasis)continue;
            /* Inside the pair: after its opener and before its closer; a selection that starts on the closer replaces the closer's side, not the pair's text. */
            if(relative<span.OpenStart+span.OpenLen||relative>span.CloseStart||(selected&&relative==span.CloseStart))continue;
            if(span.OpenText=="**"||span.OpenText=="__")bold=true;
            else if(span.OpenText=="*"||span.OpenText=="_")italic=true;
            else if(span.OpenText=="~~")strike=true;
            else if(span.OpenText=="==")highlight=true;
        }
        if(!bold&&!italic&&!strike&&!highlight)return markdown;
        string stripped=StripPastedPairs(head,bold,italic,strike,highlight);
        return String.Equals(stripped,head,StringComparison.Ordinal)?markdown:stripped+markdown.Substring(head.Length);
    }
    // 11-09 user (bug B): a multi-line paste that lands inside an inline pair's visible text keeps the pair on every line. The rule:
    //   1. only a paste whose insertion point lies inside a pair the caret line's own parse found (==, `, **, ~~, <span>, links) is touched;
    //   2. one-line pastes keep today's behaviour (their markup stays; StripEnclosingStyles already drops a repeated enclosing kind);
    //   3. a multi-line block is written as its visible text - the markdown is reduced to what the reader would see, the same result the
    //      rich view's paste gives - and every character that could start a pair is escaped, so the clipboard's own markers can never pair
    //      with the document's (the pasted "==테스트입력" stays literal text instead of joining the enclosing highlight);
    //   4. each line break is written as the enclosing pairs' closers + newline + openers, exactly the split Enter writes, so every line
    //      of the block sits inside the same pair set and the document's own markers stay balanced on each line.
    // Inline code is literal already (P6): only the backtick split is written there. Returns the text unchanged when the rule does not apply.
    string SpanBalancedPaste(string markdown,int at,int length,bool plain,bool code){
        if(String.IsNullOrEmpty(markdown)||!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return markdown;
        string newline=String.IsNullOrEmpty(documentNewline)?"\n":documentNewline;
        if(markdown.IndexOf('\n')<0&&markdown.IndexOf('\r')<0)return markdown;
        var document=source.Document;
        if(at<0||at>document.TextLength||length<0||at+length>document.TextLength)return markdown;
        /* A selection that leaves one line keeps the whole-pair rule the selection paste already has. */
        if(length>0&&document.GetLineByOffset(at)!=document.GetLineByOffset(Math.Min(document.TextLength,at+length)))return markdown;
        /* A paste on the line directly under a table can be moved to a line of its own (PasteBelowTable) after this rule ran; the
           enclosers written here would then land on the new line. That case keeps the ordinary paste. */
        int firstRow,lastRow;
        if(MarkdownTableCommands.LineAfterTable(document,at,out firstRow,out lastRow))return markdown;
        var line=document.GetLineByOffset(at);
        var info=livePreview.Info(line);
        if(info==null||info.Text==null||info.Text.Length!=line.Length||info.Code||info.Table||info.Delimiter||info.SetextUnderline>0||info.FenceLine)return markdown;
        int relative=at-line.Offset;
        var closeParts=new List<MarkdownInlineSpans.Span>();
        var openParts=new List<MarkdownInlineSpans.Span>();
        foreach(MarkdownInlineSpans.Span span in MarkdownInlineSpans.Build(info,info.Text)){
            if(span.Kind==MarkdownInlineSpans.SpanKind.Atomic||span.CloseLen<=0)continue;
            /* Only the pairs the user reported (==, inline code, <span>): bold/italic/strike keep their existing multi-line paste, which
               paste-abuse pins line for line (a pasted table inside ** kept all its rows). */
            string opener=span.OpenText??"";
            if(opener!="=="&&!opener.StartsWith("`",StringComparison.Ordinal)&&!opener.StartsWith("<span",StringComparison.OrdinalIgnoreCase))continue;
            /* Inside the visible content: the opener's inner edge and the caret before the closer both count. */
            if(span.OpenStart+span.OpenLen>relative||relative>span.CloseStart)continue;
            closeParts.Add(span);openParts.Add(span);
        }
        if(openParts.Count==0)return markdown;
        closeParts.Sort(delegate(MarkdownInlineSpans.Span a,MarkdownInlineSpans.Span b){return a.CloseStart.CompareTo(b.CloseStart);});
        openParts.Sort(delegate(MarkdownInlineSpans.Span a,MarkdownInlineSpans.Span b){return a.OpenStart.CompareTo(b.OpenStart);});
        var closing=new System.Text.StringBuilder();var opening=new System.Text.StringBuilder();
        for(int i=0;i<closeParts.Count;i++)closing.Append(closeParts[i].CloseText);
        for(int i=0;i<openParts.Count;i++)opening.Append(openParts[i].OpenText);
        bool trailing=markdown.Length>0&&(markdown[markdown.Length-1]=='\n'||markdown[markdown.Length-1]=='\r');
        string text;
        if(code)text=markdown;   /* inline code is literal (P6): its backticks cannot be escaped, only the split is written */
        else if(plain)text=EscapeEquals(markdown);   /* the plain path already wrote the visible text (SoftLiteral) */
        else text=SpanPasteLiteral(DropPastedTags(MarkdownPlainText.Convert(markdown,T("[이미지: "))));
        /* The converter drops the block's final newline; a pasted block that ended on one keeps its line break. */
        if(trailing&&text.Length>0&&text[text.Length-1]!='\n'&&text[text.Length-1]!='\r')text+="\n";
        /* One closing set at the end of every line, one opening set at the start of the next non-blank line: a blank line stays outside
           the pair, and a block that ends on one reopens it for the rest of the caret line (the document's own closer is still there). */
        String[] lines=text.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
        var built=new System.Text.StringBuilder(text.Length+(closing.Length+opening.Length+newline.Length)*2);
        bool open=true;
        for(int i=0;i<lines.Length;i++){
            if(!open&&lines[i].Length>0){built.Append(opening);open=true;}
            built.Append(lines[i]);
            if(i+1>=lines.Length)continue;
            if(open){built.Append(closing);open=false;}
            built.Append(newline);
        }
        if(!open)built.Append(opening);
        return built.ToString();
    }
    // The pasted block's visible text with every character that could start a pair escaped: the live preview draws "\*" as "*", so the text
    // reads as copied and only the enclosing pair remains markup. "=" joins the escape set (the rich path never escapes it): an unpaired
    // "==" in a pasted line would otherwise pair with the closers and openers written around the line break.
    static string SpanPasteLiteral(string value){
        string escaped=FullSoftLiteral(value);
        return EscapeEquals(escaped);
    }
    // A partial copy can start or end inside a tag ("테스트</span>" then the next copied line): the reader keeps an unmatched tag as
    // literal text (Markdown and MarkdownPlainText both do), and inside the span that would draw the raw "</span>" the user saw. A tag is
    // formatting, never the reader's text, so the tag runs the converted block still carries are dropped here. A "<" that is not a tag
    // (a comparison, a lone bracket) is left alone.
    static string DropPastedTags(string value){
        if(String.IsNullOrEmpty(value)||value.IndexOf('<')<0)return value;
        var built=new System.Text.StringBuilder(value.Length);
        int at=0,p=0;
        while(p<value.Length){
            if(value[p]=='<'){
                InlineHtml.Tag tag=InlineHtml.Read(value,p);
                if(tag!=null&&tag.End>p){built.Append(value,at,p-at);p=tag.End;at=p;continue;}
            }
            p++;
        }
        built.Append(value,at,value.Length-at);
        return built.ToString();
    }
    // Escapes every "=" (already escaped ones keep their backslash) for the multi-line-in-a-span paste.
    static string EscapeEquals(string value){
        if(value==null||value.IndexOf('=')<0)return value;
        var built=new System.Text.StringBuilder(value.Length+8);
        for(int i=0;i<value.Length;i++){char c=value[i];if(c=='='&&(i==0||value[i-1]!='\\'))built.Append('\\');built.Append(c);}
        return built.ToString();
    }
    // One pasted line without the markers of the pairs it parses as, for the kinds asked. The result must
    // still parse as the other pairs (same count); if removing the markers re-pairs the rest, the line is
    // returned unchanged.
    static string StripPastedPairs(string pasted,bool bold,bool italic,bool strike){
        return StripPastedPairs(pasted,bold,italic,strike,false);
    }
    static string StripPastedPairs(string pasted,bool bold,bool italic,bool strike,bool highlight){
        MarkdownLivePreview.LineInfo parsed=MarkdownLivePreview.ParseInlineText(pasted,false);
        var drop=new bool[pasted.Length];
        var kept=new List<MarkdownInlineSpans.Span>();
        int removed=0;
        foreach(MarkdownInlineSpans.Span span in MarkdownInlineSpans.Build(parsed,pasted)){
            if(span.Kind!=MarkdownInlineSpans.SpanKind.Emphasis)continue;
            string open=span.OpenText;
            bool same=(bold&&(open=="**"||open=="__"))||(italic&&(open=="*"||open=="_"))||(strike&&open=="~~")||(highlight&&open=="==");
            if(!same||span.OpenStart<0||span.CloseStart<0||span.OpenStart+span.OpenLen>pasted.Length||span.CloseStart+span.CloseLen>pasted.Length){kept.Add(span);continue;}
            for(int i=0;i<span.OpenLen;i++)drop[span.OpenStart+i]=true;
            for(int i=0;i<span.CloseLen;i++)drop[span.CloseStart+i]=true;
            removed++;
        }
        if(removed==0)return pasted;
        var result=new System.Text.StringBuilder(pasted.Length);
        var shift=new int[pasted.Length+1];
        for(int i=0;i<pasted.Length;i++){shift[i]=result.Length;if(!drop[i])result.Append(pasted[i]);}
        shift[pasted.Length]=result.Length;
        string text=result.ToString();
        /* The other pairs must survive as the same pairs (same markers at the shifted offsets), not just as many: "**_a** *b_c*" without the bold re-pairs "_a *b_" and loses "*b_c*". */
        var after=new List<MarkdownInlineSpans.Span>();
        foreach(MarkdownInlineSpans.Span span in MarkdownInlineSpans.Build(MarkdownLivePreview.ParseInlineText(text,false),text))if(span.Kind==MarkdownInlineSpans.SpanKind.Emphasis)after.Add(span);
        if(after.Count!=kept.Count)return pasted;
        foreach(MarkdownInlineSpans.Span span in kept){
            if(span.OpenStart<0||span.CloseStart<0||span.OpenStart>pasted.Length||span.CloseStart>pasted.Length)return pasted;
            bool found=false;
            foreach(MarkdownInlineSpans.Span now in after)if(now.OpenText==span.OpenText&&now.OpenStart==shift[span.OpenStart]&&now.CloseStart==shift[span.CloseStart]){found=true;break;}
            if(!found)return pasted;
        }
        return text;
    }
    // True when the pasted text is one line that is a complete span of its own: its first characters are an
    // opening marker and the parser pairs it with the closing marker that ends the text ("**`a`**", "*x*",
    // "`a`"). Text with unpaired markers, plain text and several lines answer false. O(pasted line).
    static bool PasteIsOwnSpan(string markdown){
        if(String.IsNullOrEmpty(markdown)||markdown.Length>4096||markdown.IndexOfAny(new char[]{'\r','\n'})>=0)return false;
        char first=markdown[0];
        if(first!='*'&&first!='_'&&first!='~'&&first!='='&&first!='`')return false;
        MarkdownLivePreview.LineInfo parsed=MarkdownLivePreview.ParseInlineText(markdown,false);
        foreach(MarkdownInlineSpans.Span span in MarkdownInlineSpans.Build(parsed,markdown)){
            if(span.Kind!=MarkdownInlineSpans.SpanKind.Emphasis&&span.Kind!=MarkdownInlineSpans.SpanKind.Code)continue;
            if(span.OpenStart==0&&span.CloseStart>0&&span.CloseStart+span.CloseLen==markdown.Length)return true;
        }
        return false;
    }
    // A paste at a caret as one command result, only when it would leave an underscore pair against
    // a word character (MarkdownLivePreview.PlanInsertRepair rewrites that pair with "*"). Null otherwise.
    CommandResult RepairedInsert(int at,string markdown){
        if(!LivePreviewAttached||livePreview==null||String.IsNullOrEmpty(markdown))return null;
        int editStart,editLength,caret;string editText;
        if(!livePreview.PlanInsertRepair(at,markdown,out editStart,out editLength,out editText,out caret))return null;
        CommandResult result=new CommandResult();
        if(editLength>0||editText.Length>0)result.Add(editStart,editLength,editText);
        result.SelectionStart=caret;
        result.SelectionLength=0;
        return result;
    }
    // True when the offset lies right after a hidden closing backtick run (an inline code span's end).
    bool PasteAfterClosingBacktick(int offset){
        if(!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return false;
        var document=source.Document;
        offset=Math.Max(0,Math.Min(offset,document.TextLength));
        var line=document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        if(info==null||info.Text==null)return false;
        int inside=MarkdownLivePreview.Session.HiddenCloseInside(info,offset-line.Offset);
        return inside>=0&&inside<info.Text.Length&&info.Text[inside]=='`';
    }
    // True when the offset lies on a hidden closing run (the spot SnapInsertOffset moves inside the pair).
    bool PasteOnCloserEdge(int offset){
        if(!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return false;
        var document=source.Document;
        offset=Math.Max(0,Math.Min(offset,document.TextLength));
        var line=document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        if(info==null||info.Text==null)return false;
        int inside=MarkdownLivePreview.Session.HiddenCloseInside(info,offset-line.Offset);
        if(inside<0)return false;
        /* A closing backtick run is the user's own "code ends here": text pasted after it stays outside the span (TableCaretProbe cell paste, "`a`X"). */
        return !(inside<info.Text.Length&&info.Text[inside]=='`');
    }
    // The paste as one command result: the replaced selection, the restored markers and the caret
    // after the pasted text (MarkdownLivePreview.PlanSelectionReplace). Null when there is no
    // selection or the rule does not apply (live view, code, table selection across cells), so the
    // ordinary replacement runs.
    CommandResult RebalancedReplace(string markdown){
        if(!LivePreviewAttached||livePreview==null||source==null||markdown==null)return null;
        if(source.SelectionLength<=0)return RepairedInsert(source.SelectionStart,markdown);
        int editStart,editLength,caret;string editText;
        int selectionStart=source.SelectionStart;
        if(!livePreview.PlanSelectionReplace(selectionStart,selectionStart+source.SelectionLength,markdown,out editStart,out editLength,out editText,out caret))return null;
        CommandResult result=new CommandResult();
        if(editLength>0||editText.Length>0)result.Add(editStart,editLength,editText);
        result.SelectionStart=caret;
        result.SelectionLength=0;
        return result;
    }
    // A paste inside one table cell is the cell command's single replacement of the selection;
    // the markers that selection cut off are written back through the same plan (the result keeps
    // the cell command's own text, so pipes and <br> are already escaped). Any other shape of cell
    // result (a selection across cells, several edits) is applied as the cell command made it.
    CommandResult RebalancedCellPaste(CommandResult cell){
        if(cell==null||!LivePreviewAttached||livePreview==null||source==null)return cell;
        if(cell.Edits.Count!=1)return cell;
        SourceEdit edit=cell.Edits[0];
        if(source.SelectionLength<=0){
            // A point paste in a cell: only an underscore pair the text would break changes the edit.
            if(edit.Length!=0||edit.Text==null)return cell;
            CommandResult point=RepairedInsert(edit.Offset,edit.Text);
            return point==null?cell:point;
        }
        if(edit.Offset!=source.SelectionStart||edit.Length!=source.SelectionLength||edit.Text==null)return cell;
        int editStart,editLength,caret;string editText;
        if(!livePreview.PlanSelectionReplace(edit.Offset,edit.Offset+edit.Length,edit.Text,out editStart,out editLength,out editText,out caret))return cell;
        CommandResult result=new CommandResult();
        if(editLength>0||editText.Length>0)result.Add(editStart,editLength,editText);
        result.SelectionStart=caret;
        result.SelectionLength=0;
        return result;
    }
    // The paste side of the line-under-a-table rule (TableGapMode keeps the blank separator for
    // typed text): text whose first line has a pipe written directly on the line under the last
    // row would be read back as one more row. A newline opens a line for the paste, so the
    // paragraph keeps its own text and the table keeps its rows. Null when the paste lands
    // anywhere else, or when its first line stays pipe-free.
    CommandResult PasteBelowTable(string markdown){
        if(!LivePreviewAttached||source==null||String.IsNullOrEmpty(markdown))return null;
        if(source.SelectionLength!=0)return null;
        TextDocument document=source.Document;
        if(document==null)return null;
        int at=source.SelectionStart;
        if(at<0||at>document.TextLength)return null;
        int firstLine,lastLine;
        if(!MarkdownTableCommands.LineAfterTable(document,at,out firstLine,out lastLine))return null;
        if(MarkdownTableCommands.FindTable(document,at,out firstLine,out lastLine))return null;
        int stop=markdown.IndexOfAny(new char[]{'\r','\n'});
        string head=stop<0?markdown:markdown.Substring(0,stop);
        if(!MarkdownTableCommands.HasTablePipe(head))return null;
        string newline=String.IsNullOrEmpty(documentNewline)?"\n":documentNewline;
        CommandResult result=new CommandResult();
        result.Add(at,0,newline+markdown);
        result.SelectionStart=at+newline.Length+markdown.Length;
        result.SelectionLength=0;
        return result;
    }
    // The continuation Enter would write after a line break at the caret, taken from the same
    // structural rule so paste and Enter never disagree ("- item" -> "- ", "> item" -> "> ",
    // "2. item" -> "3. ", a task item -> "- [ ] "). Null when the line continues nothing: plain
    // text, a heading, a code line, a caret at column zero or inside the marker itself.
    string PasteContinuationPrefix(TextDocument document,int caret){
        if(document==null)return null;
        caret=Math.Max(0,Math.Min(caret,document.TextLength));
        CommandResult next;
        try{next=MarkdownStructuralInput.Enter(document,caret,0);}
        catch(Exception){return null;}   /* a paste must never fail on the prefix probe */
        if(next==null)return null;
        foreach(SourceEdit edit in next.Edits){
            if(edit.Offset!=caret||edit.Length!=0||String.IsNullOrEmpty(edit.Text))continue;
            int at=0;
            while(at<edit.Text.Length&&(edit.Text[at]=='\r'||edit.Text[at]=='\n'))at++;
            if(at==0)continue;   /* a replacement (the empty-quote exit), not a line break */
            string prefix=edit.Text.Substring(at);
            return prefix.Length==0?null:prefix;
        }
        return null;
    }
    // Prefixes the pasted text's lines after the first with `prefix`, unless the line already
    // carries it (a copied list keeps its own bullets). Blank lines and the paste's own trailing
    // line break get none, so no dangling empty item is written.
    static string ContinuePastedLines(string markdown,string prefix,string newline){
        if(String.IsNullOrEmpty(markdown)||String.IsNullOrEmpty(prefix))return markdown;
        string breakText=String.IsNullOrEmpty(newline)?"\n":newline;
        if(markdown.IndexOf(breakText,StringComparison.Ordinal)<0)return markdown;
        string trimmedPrefix=prefix.TrimStart();
        var built=new System.Text.StringBuilder(markdown.Length+prefix.Length*4);
        int at=0;
        while(at<markdown.Length){
            int stop=markdown.IndexOf(breakText,at,StringComparison.Ordinal);
            int end=stop<0?markdown.Length:stop;
            string segment=markdown.Substring(at,end-at);
            if(at>0&&segment.Length>0&&!segment.StartsWith(prefix,StringComparison.Ordinal)&&!segment.StartsWith(trimmedPrefix,StringComparison.Ordinal))built.Append(prefix);
            built.Append(segment);
            if(stop<0)break;
            built.Append(breakText);
            at=stop+breakText.Length;
        }
        return built.ToString();
    }
    // The table-gap rule's paste side (the typing side lives in MarkdownLivePreview.TableGapMode).
    // Null on every other line, so ordinary pastes keep their own path.
    CommandResult PasteTableGap(string markdown){
        if(!LivePreviewAttached||source==null||String.IsNullOrEmpty(markdown))return null;
        if(source.SelectionLength!=0)return null;
        string newline;
        int mode=livePreview.TableGapMode(source.SelectionStart,out newline);
        if(mode==0)return null;
        int at=source.SelectionStart;
        CommandResult result=new CommandResult();
        if(mode==2){result.Add(at,0,markdown+newline);result.SelectionStart=at+markdown.Length;}
        else{result.Add(at,0,newline+markdown);result.SelectionStart=at+newline.Length+markdown.Length;}
        result.SelectionLength=0;
        return result;
    }
    // Ctrl+Shift+C (#21): copy the selection as readable text, in read and edit mode alike.
    // Handled at window level; before, only the live edit-mode key path knew the shortcut.
    internal bool CopyReadable(){
        if(System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.TextBox)return false;
        if(source==null)return true;
        string text=source.SelectedText;
        if(text.Length>0&&!PlainText)text=MarkdownPlainText.Convert(text,T("[이미지: "));
        if(String.IsNullOrEmpty(text))return true;
        if(ClipboardRetry.TrySetText(text))return true;
        ShowStatusNotice(T("클립보드를 열 수 없습니다. 잠시 후 다시 복사하세요."));
        /* clipboard stayed locked: report failure so Ctrl+Shift+C falls through instead of claiming a copy */
        return false;
    }
    // P5: Enter/Tab/Shift+Tab/Backspace on list, quote and code lines. During IME composition
    // keys arrive as Key.ImeProcessed and are never handled here.
    void SourceStructuralKeys(object sender,System.Windows.Input.KeyEventArgs e){
        if(e.Handled||!LiveFormat||source==null||!Object.ReferenceEquals(sender,source.TextArea))return;
        var modifiers=System.Windows.Input.Keyboard.Modifiers;CommandResult result=null;
        bool back=e.Key==System.Windows.Input.Key.Back&&(modifiers==System.Windows.Input.ModifierKeys.None||modifiers==System.Windows.Input.ModifierKeys.Shift);
        bool delete=e.Key==System.Windows.Input.Key.Delete&&modifiers==System.Windows.Input.ModifierKeys.None;
        if(e.Key==System.Windows.Input.Key.Enter&&modifiers==System.Windows.Input.ModifierKeys.None)result=SourceEnter();
        else if(e.Key==System.Windows.Input.Key.Tab&&(modifiers==System.Windows.Input.ModifierKeys.None||modifiers==System.Windows.Input.ModifierKeys.Shift))result=SourceTab(modifiers==System.Windows.Input.ModifierKeys.Shift);
        else if(back&&source.SelectionLength==0)result=SourceBackspace();
        else if(delete&&source.SelectionLength==0)result=SourceDelete();
        else if((back||delete)&&source.SelectionLength>0)result=SourceCellSelectionDelete();
        if(result==null)return;
        e.Handled=true;
        if(result.Edits.Count>0)ApplyCommandResult(result);else source.Select(result.SelectionStart,result.SelectionLength);
    }
    // Enter: at a table boundary the basic view's "Enter makes a paragraph, never a row" rule
    // (MarkdownTableCommands.EnterOutsideTable); everywhere else the P5 list/quote continuation.
    // The table rule runs first because a table row must not continue as a list, and it returns
    // null on every other line, which hands the caret line back to the list rule. It is a
    // formatted-view rule like the basic view's, so the 원문 편집 toggle (preview detached) keeps
    // today's plain newline there.
    CommandResult SourceEnter(){
        return SourceEnterPath(true);
    }
    // The editor's newline text path (Shift+Enter, an IME Enter). It keeps the cell paragraph
    // break instead of the key's table boundary rule.
    CommandResult SourceEnterText(){
        return SourceEnterPath(false);
    }
    // keyEnter: the Enter key path (the table boundary rule). The newline text path passes false.
    CommandResult SourceEnterPath(bool keyEnter){
        // Basic view: a hidden fence line is structure. Enter over a selection that holds only
        // one fence of a block would replace it with a newline (and the line's continuation),
        // leaving the partner behind, so the key keeps the selection instead.
        if(source.SelectionLength>0&&LivePreviewAttached&&livePreview!=null&&livePreview.SelectionBreaksFence())return KeepSelection();
        if(LivePreviewAttached){
            // The delimiter row is structure: it carries the table's alignment and its own column
            // count. Typed text there is already kept out (MarkdownLivePreview.MultiCellEntering);
            // the Enter key over a selection that lies inside that row must not replace it with a
            // newline either, which would leave the header without its delimiter and end the table.
            CommandResult delimiter=DelimiterRowSelectionEnter();
            if(delimiter!=null)return delimiter;
            CommandResult table=MarkdownTableCommands.EnterOutsideTable(source.Document,source.SelectionStart,source.SelectionLength,keyEnter);
            if(table!=null)return table;
            // Stage 4: Enter/Shift+Enter inside a cell is the basic view's paragraph break, saved
            // as "<br>", never a new source line that would split the table row.
            // The <br> lands at the position the inline pair map settled (a caret on a marker's
            // inner edge leaves the pair first); without a line parse EnterInCell keeps H9.
            CommandResult cell=MarkdownTableCommands.EnterInCell(source.Document,source.SelectionStart,source.SelectionLength,SourceRowCells(source.SelectionStart),InlineEnterCellColumn());
            if(cell!=null)return cell;
            // Stage 6: Enter inside an inline pair's visible text closes and reopens every pair
            // that encloses the caret, so both lines keep the whole style set. Basic and live
            // mode share the rule: with the markers hidden the two edges of a marker draw on one
            // spot, and with them shown the split still keeps each pair whole instead of cutting
            // one in two. Literal marker text stays raw (InsideMarkerRun).
            if(source.SelectionLength==0&&!source.ImeBusy&&livePreview.Options!=null){
                CommandResult inline=InlineEnter(keyEnter);
                if(inline!=null)return inline;
            }
        }
        // Only the Enter key ends an empty list item; Shift+Enter and the IME's newline keep the
        // old continuation.
        return MarkdownStructuralInput.Enter(source.Document,source.SelectionStart,source.SelectionLength,keyEnter);
    }
    // A non-empty selection that lies inside one delimiter row: the key is swallowed (a no-op
    // result keeps the selection), so the row that keeps the table a table stays. A selection
    // reaching onto other lines is an ordinary multi-line replacement and takes the usual path.
    CommandResult DelimiterRowSelectionEnter(){
        if(source.SelectionLength<=0)return null;
        var document=source.Document;
        int start=Math.Max(0,Math.Min(source.SelectionStart,document.TextLength));
        int end=Math.Min(start+source.SelectionLength,document.TextLength);
        if(end<=start)return null;
        var first=document.GetLineByOffset(start);
        var last=document.GetLineByOffset(end-1);
        if(first.LineNumber!=last.LineNumber)return null;
        var info=livePreview==null?null:livePreview.Info(first);
        if(info==null||!info.Delimiter)return null;
        CommandResult result=new CommandResult();
        result.SelectionStart=source.SelectionStart;
        result.SelectionLength=source.SelectionLength;
        return result;
    }
    // Enter inside an inline pair. An empty selection on a marker's inner edge first steps to
    // that pair's outer side ("**ab|**" becomes "**ab**|") before the ordinary Enter runs; a
    // caret strictly inside the pair's visible text splits it, writing the closing markers, the
    // newline, the line's own continuation ("- ", "> ") and the same opening markers as one
    // command, so neither line shows raw markup. Null everywhere else, which hands the line
    // back to the ordinary rule.
    // The pairs come from MarkdownInlineSpans (links and tags pair like emphasis, so a link's
    // label splits into two links with the same address) and the settled caret is the break's
    // position as is: the parser already told where the pairs are, so H9's raw marker-run snap
    // does not run again (markerEdge false). A caret the parser did not place (plain text)
    // returns null and keeps H9 in the ordinary rule.
    CommandResult InlineEnter(bool keyEnter){
        var document=source.Document;
        int offset=Math.Max(0,Math.Min(source.CaretIndex,document.TextLength));
        var line=document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        if(info==null||info.Code||info.Table||info.Delimiter||info.SetextUnderline>0)return null;
        /* a line cache one edit behind (StripEnclosingStyles and PasteRowCells check the same) leaves Enter to the plain newline */
        if(info.Text==null||info.Text.Length!=line.Length)return null;
        int relative=offset-line.Offset;
        // With the markers drawn (live mode) the caret can sit between the characters of a marker
        // run; that spot is literal markup the user sees, so Enter there stays the plain newline
        // instead of writing closing and opening markers into the middle of a run.
        if(livePreview.Options!=null&&!livePreview.Options.HideMarkers&&InsideMarkerRun(info,relative))return null;
        var spans=MarkdownInlineSpans.Build(info,info.Text);
        int settled=MarkdownInlineSpans.StepOut(spans,relative);
        bool moved=settled!=relative;
        if(moved){
            offset=line.Offset+settled;
            relative=settled;
        }
        string closing,opening;
        // Markdown emphasis cannot open/close against whitespace. Split reports the adjacent
        // spaces so they stay outside the split pairs, rather than leaving "**ab **" or
        // "** cd**" as visible markers.
        int left,right;
        bool splitPairs=MarkdownInlineSpans.Split(spans,info.Text,relative,out left,out right,out closing,out opening);
        if(!splitPairs){
            // No pair encloses the settled caret. Away from every marker it is plain text and the
            // ordinary Enter owns it; on a marker the parser placed (or moved to), the break goes
            // exactly there, with the line's own continuation.
            if(!moved&&!MarkdownInlineSpans.TouchesMarker(spans,relative))return null;
            closing="";opening="";left=relative;right=relative;
            while(right<info.Text.Length&&(info.Text[right]==' '||info.Text[right]=='\t'))right++;
        }
        /* The new line starts without the blanks that followed the break, as every Enter on the source surface writes it (AvalonEdit's indentation strategy, ImeProbe J4c "**ab**| cd" -> "**ab**\ncd"). */
        string leftSpace=info.Text.Substring(left,relative-left),rightSpace="";
        int editOffset=line.Offset+left,editLength=right-left;
        // The list/quote continuation comes from the same rule the ordinary Enter uses, so a
        // split inside "- **ab|cd**" still opens the next line with "- ".
        CommandResult inherited=MarkdownStructuralInput.Enter(document,offset,0,keyEnter,false);
        if(!splitPairs&&inherited!=null){
            // Nothing to write around the break: take the structural result as it is when it is
            // not the plain "newline + continuation" insertion at the caret (an empty-item exit).
            bool insertion=false;
            for(int i=0;i<inherited.Edits.Count;i++)if(inherited.Edits[i].Offset==offset&&inherited.Edits[i].Length==0)insertion=true;
            if(!insertion)return inherited;
        }
        if(inherited!=null){
            for(int i=0;i<inherited.Edits.Count;i++){
                SourceEdit edit=inherited.Edits[i];
                if(edit.Offset!=offset||edit.Length!=0)continue;
                string inserted=closing+leftSpace+(edit.Text==null?"":edit.Text)+rightSpace+opening;
                inherited.Edits[i]=new SourceEdit(editOffset,editLength,inserted);
                inherited.SelectionStart=editOffset+inserted.Length;
                inherited.SelectionLength=0;
                inherited.SortEdits();
                return inherited;
            }
        }
        var result=new CommandResult();
        string newline=InlineEnterNewline(document,line);
        string split=closing+leftSpace+newline+rightSpace+opening;
        result.Add(editOffset,editLength,split);
        result.SelectionStart=editOffset+split.Length;
        result.SelectionLength=0;
        result.SortEdits();
        return result;
    }
    // The settled Enter column (relative to the line) for a table cell's "<br>", or -1 when the
    // preview has no say: no table row parse, a selection, an IME composition, or a caret that
    // plain text owns (EnterInCell then keeps H9's marker-run snap). Same rule as InlineEnter: a
    // caret on a pair's inner edge steps to the outside, and a caret the parser placed on a
    // marker stays; live mode's caret inside a drawn marker run is literal and stays H9's.
    int InlineEnterCellColumn(){
        if(!LivePreviewAttached||livePreview==null||source==null||source.SelectionLength>0||source.ImeBusy)return -1;
        var document=source.Document;
        int offset=Math.Max(0,Math.Min(source.CaretIndex,document.TextLength));
        var line=document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        if(info==null||!info.Table||info.Delimiter||info.Code||info.Text==null)return -1;
        int relative=offset-line.Offset;
        if(livePreview.Options!=null&&!livePreview.Options.HideMarkers&&InsideMarkerRun(info,relative))return -1;
        var spans=MarkdownInlineSpans.Build(info,info.Text);
        int settled=MarkdownInlineSpans.StepOut(spans,relative);
        if(settled!=relative||MarkdownInlineSpans.TouchesMarker(spans,relative))return settled;
        return -1;
    }
    // True when the caret lies strictly between the characters of one hidden marker run (an
    // emphasis marker, a tag, ...). Prefix runs are skipped: they are never split, and no pair
    // encloses a caret inside them.
    static bool InsideMarkerRun(MarkdownLivePreview.LineInfo info,int relative){
        foreach(MarkdownLivePreview.Replacement marker in info.Replacements){
            if(marker.Kind!=MarkdownLivePreview.ReplacementKind.Hidden||marker.Length<=0||marker.Prefix)continue;
            if(marker.Start<relative&&relative<marker.Start+marker.Length)return true;
        }
        return false;
    }
    // The line's own line ending: its delimiter, else the nearest earlier one; the same rule
    // MarkdownStructuralInput uses for its continuation, so a split keeps the document's CRLF.
    static string InlineEnterNewline(TextDocument document,DocumentLine line){
        for(var current=line;current!=null;current=current.PreviousLine){
            if(current.DelimiterLength>0)return document.GetText(current.EndOffset,current.DelimiterLength);
        }
        return "\n";
    }
    // The caret line's cells as the live preview parsed them. Info() is the per-line cache, so
    // this stays O(line) on a keystroke; the delimiter row is not a cell row and a line outside a
    // table answers null, which leaves the ordinary editing keys untouched.
    List<MarkdownLivePreview.Cell> SourceRowCells(int offset){
        if(!LivePreviewAttached||source==null)return null;
        var document=source.Document;
        offset=Math.Max(0,Math.Min(offset,document.TextLength));
        var line=document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        if(info==null||!info.Table||info.Delimiter||info.Cells==null||info.Cells.Count==0)return null;
        return info.Cells;
    }
    // The caret line's cells as an edit command must see them. The cached parse is the fast path
    // when it still describes the line; an entry left from the text before the last keystroke
    // would hand the command the cell end from before that character (a paste then lands one
    // spot left of the caret). The line is compared once, and only a stale row pays for a fresh
    // split; a paste is a one-shot command, so this stays O(line) and never touches a keystroke.
    List<MarkdownLivePreview.Cell> PasteRowCells(TextDocument document,int offset){
        if(document==null||!LivePreviewAttached||livePreview==null)return null;
        offset=Math.Max(0,Math.Min(offset,document.TextLength));
        var line=document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        if(info==null||!info.Table||info.Delimiter)return null;
        string text=document.GetText(line.Offset,line.Length);
        if(info.Cells!=null&&info.Cells.Count>0&&String.Equals(info.Text,text,StringComparison.Ordinal))return info.Cells;
        // A line that is no longer a pipe row at all answers null, exactly like SourceRowCells.
        return MarkdownTableCommands.CellsAt(document,offset);
    }
    // True when the offset is a valid position in a table cell's own text, read from the line as
    // it is now: a stale Info() entry would judge the text before the last keystroke. A caret
    // strictly inside a hidden marker is not one of these: pasted text there would split the
    // pair, so that caret stays with the session's snap.
    bool CaretInCellText(TextDocument document,int offset){
        if(document==null||offset<0||offset>document.TextLength)return false;
        var line=document.GetLineByOffset(offset);
        int relative=offset-line.Offset;
        var cells=MarkdownTableCommands.CellsAt(document,offset);
        if(cells==null||cells.Count==0)return false;
        // A cell's own padding counts as that cell, so the caret on an empty cell's stop (or in a
        // typed space at a cell's end) is a cell position the paste must keep.
        if(MarkdownTableCommands.CellIndexAtPadding(document.GetText(line.Offset,line.Length),cells,relative)<0)return false;
        if(LivePreviewAttached&&livePreview!=null){
            var info=livePreview.Info(line);
            if(info!=null&&info.Replacements!=null){
                foreach(MarkdownLivePreview.Replacement marker in info.Replacements){
                    if(marker.Kind!=MarkdownLivePreview.ReplacementKind.Hidden||marker.Length<=0)continue;
                    if(marker.Start<relative&&relative<marker.Start+marker.Length)return false;
                }
            }
        }
        return true;
    }
    // True when the selection's first or last touched line is a table delimiter row (the live
    // preview owns the parse, O(line) on two lines). Basic mode keeps that row folded and live
    // mode reveals it on the selection line; in both the row is structure, so a delete, cut or
    // paste over the selection must not rewrite or remove it.
    bool SelectionTouchesDelimiter(){
        return LivePreviewAttached&&livePreview!=null&&source!=null&&source.SelectionLength>0&&livePreview.SelectionTouchesDelimiter();
    }
    // True while the caret line is a table delimiter row. Typed text there is already kept out
    // (MarkdownLivePreview.MultiCellEntering); the same reason makes the delete keys write
    // nothing, because a delete joins the row with its neighbour or removes one of its dashes.
    bool DelimiterLineAtCaret(){
        if(!LivePreviewAttached||source==null)return false;
        var document=source.Document;
        var line=document.GetLineByOffset(Math.Max(0,Math.Min(source.CaretIndex,document.TextLength)));
        var info=livePreview.Info(line);
        return info!=null&&info.Delimiter;
    }
    // Tab/Shift+Tab are cell navigation (MarkdownStructuralInput.TableTab). A cell move must land
    // inside a cell's text: a row's trailing pipe leaves one phantom slot past it (the line end),
    // and a caret there makes the next character add a column. The basic view's TableCellTab
    // declines at the table's end instead of offering that slot, so the move is swallowed there
    // (no row change; the caret stays where it was). The one edit is Tab in the last cell of the
    // table's last row: the structural rule appends an empty row there
    // (MarkdownTableCommands.AddRowAtEnd), and a result with edits is applied by the host below.
    CommandResult SourceTab(bool shift){
        // Basic view: Tab over a selection that holds only one fence line of a block would
        // replace the selected text with a tab and leave the partner fence behind.
        if(source.SelectionLength>0&&LivePreviewAttached&&livePreview!=null&&livePreview.SelectionBreaksFence())return KeepSelection();
        CommandResult result=MarkdownStructuralInput.Tab(source.Document,source.SelectionStart,source.SelectionLength,shift);
        if(result==null||result.Edits.Count>0||!LivePreviewAttached)return result;
        int offset=Math.Max(0,Math.Min(result.SelectionStart,source.Document.TextLength));
        var document=source.Document;
        var line=document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        // The |---| line is not a row in either view, but the raw TableTab rule walks through its
        // pipes as if it were one. The basic view's TableCellTab steps over it to the row on the
        // other side, so Tab continues down and Shift+Tab up from that row instead.
        if(info!=null&&info.Delimiter){
            var neighbour=shift?line.PreviousLine:line.NextLine;
            if(neighbour==null)return StayTab();
            var neighbourInfo=livePreview.Info(neighbour);
            if(neighbourInfo==null||!neighbourInfo.Table||neighbourInfo.Cells==null||neighbourInfo.Cells.Count==0)return StayTab();
            int cell=shift?neighbourInfo.Cells.Count-1:0;
            CommandResult moved=new CommandResult();
            moved.SelectionStart=neighbour.Offset+neighbourInfo.Cells[cell].Start;
            moved.SelectionLength=Math.Max(0,neighbourInfo.Cells[cell].End-neighbourInfo.Cells[cell].Start);
            return moved;
        }
        if(info==null||!info.Table)return result;
        var cells=info.Cells;
        int relative=offset-line.Offset;
        if(cells!=null&&cells.Count>0){
            // A cell's own padding is that cell (MarkdownTableCommands.CellIndexAtPadding): the
            // stop inside an empty cell, and a space typed after a cell's text, must not read as
            // "past the row's last cell" - that sent Tab on to the next row instead of leaving the
            // cell the move had just selected.
            if(MarkdownTableCommands.CellIndexAtPadding(document.GetText(line.Offset,line.Length),cells,relative)>=0)return result;
            // A backward move onto a row's trailing-pipe slot lands on that row's last real cell.
            if(shift)return CellTarget(line,cells[cells.Count-1]);
            // A forward move past the row's last cell continues on the next row, skipping |---|.
            for(var next=line.NextLine;next!=null;next=next.NextLine){
                var nextInfo=livePreview.Info(next);
                if(nextInfo==null||!nextInfo.Table)break;
                if(!nextInfo.Delimiter&&nextInfo.Cells!=null&&nextInfo.Cells.Count>0)return CellTarget(next,nextInfo.Cells[0]);
            }
        }
        return StayTab();
    }
    CommandResult CellTarget(ICSharpCode.AvalonEdit.Document.DocumentLine line,MarkdownLivePreview.Cell cell){
        CommandResult moved=new CommandResult();
        int length=Math.Max(0,cell.End-cell.Start);
        int start=line.Offset+cell.Start;
        // A padding-only cell keeps its own caret stop (MarkdownTableCommands.EmptyCellStop), so
        // the character typed after this Tab lands between the cell's two spaces instead of in
        // front of its closing pipe.
        if(length==0)start=line.Offset+MarkdownTableCommands.EmptyCellStopBefore(source.Document.GetText(line.Offset,line.Length),cell.End);
        moved.SelectionStart=start;
        moved.SelectionLength=length;
        return moved;
    }
    // The swallowed Tab: no edits and the caret where it is. CaretIndex (not SelectionStart) keeps
    // a cell selected by the previous Tab from collapsing to the cell's start.
    CommandResult StayTab(){
        CommandResult stay=new CommandResult();
        stay.SelectionStart=source.CaretIndex;
        stay.SelectionLength=0;
        return stay;
    }
    // Probe seam: the same Tab path the key handler runs, without a keyboard (a probe cannot set
    // Keyboard.Modifiers, so Shift+Tab is unreachable through the key event).
    internal bool SourceTabPathProbe(bool shift){
        if(!LiveFormat||source==null)return false;
        CommandResult result=SourceTab(shift);
        if(result==null)return false;
        if(result.Edits.Count>0)ApplyCommandResult(result);else source.Select(result.SelectionStart,result.SelectionLength);
        source.Focus();
        return true;
    }
    // Backspace in a cell never merges cells or deletes a "|" (Stage 4); inside the cell's text
    // the rule answers null and the ordinary character deletion runs.
    CommandResult SourceBackspace(){
        // The delimiter row is structure: basic mode folds it and live mode reveals the caret
        // line, but a text delete in either would join it with the header and end the table.
        if(DelimiterLineAtCaret())return StayTab();
        CommandResult cell=null;
        if(LivePreviewAttached){
            cell=MarkdownTableCommands.BackspaceInCell(source.Document,source.CaretIndex,SourceRowCells(source.CaretIndex));
            // A paragraph under a table keeps the blank separator and the last row: the key only
            // steps into the table's last cell (the cell rule above answers on a row itself).
            if(cell==null)cell=MarkdownTableCommands.BackspaceBeforeTable(source.Document,source.CaretIndex);
        }
        if(cell!=null)return cell;
        // The caret on the far side of an opening inline marker ("- **|item**") draws on the
        // item's text start, the same spot the structural prefix's end names: the marker rule
        // runs from there (a bullet goes, the text keeps its bold) instead of the inline pair's
        // own deletion. A line whose prefix has no rule of its own (a heading) declines here and
        // the hidden prefix deletion in the command path takes the same position.
        int lead=HiddenLeadStart();
        if(lead>=0)return MarkdownStructuralInput.Backspace(source.Document,lead);
        CommandResult structural=MarkdownStructuralInput.Backspace(source.Document,source.CaretIndex);
        if(structural!=null)return structural;
        // Backspace at the start of a line joins it to the ordered item above: the items after it are renumbered.
        return MarkdownStructuralInput.JoinOrdered(source.Document,source.CaretIndex,true);
    }
    // Source offset of the caret line's structural prefix end when the caret sits past it inside
    // the hidden inline markers that start right there; -1 anywhere else. Only while the markers
    // are drawn as nothing: with them shown they are real characters the caret can sit between,
    // and Backspace has to take the character it sits after.
    int HiddenLeadStart(){
        if(!LivePreviewAttached||source==null)return -1;
        if(livePreview.Options==null||!livePreview.Options.HideMarkers)return -1;
        int offset=Math.Max(0,Math.Min(source.CaretIndex,source.Document.TextLength));
        var line=source.Document.GetLineByOffset(offset);
        var info=livePreview.Info(line);
        if(info==null||info.Table||info.Code||info.Delimiter||info.SetextUnderline>0)return -1;
        int relative=offset-line.Offset;
        int lead=MarkdownLivePreview.Session.LeadRelative(info,relative);
        return lead==relative?-1:line.Offset+lead;
    }
    // Delete at a cell's end is a wall: it does nothing (MarkdownTableCommands.DeleteInCell)
    // instead of removing a delimiter or joining the row with the next line.
    CommandResult SourceDelete(){
        if(!LivePreviewAttached)return null;
        // The delimiter row is structure in both view modes: Delete would join it with the next
        // row or eat one of its dashes, so the key writes nothing.
        if(DelimiterLineAtCaret())return StayTab();
        // A fence line is structure too: Delete would join it with the next line or shorten its
        // marker run. The pair is one unit (11-06 user: the closing ``` must be deletable):
        // breaking either fence removes both fence lines and keeps the block's text, and a fence
        // still being typed (no closer) answers null so the ordinary character delete runs.
        if(MarkdownStructuralInput.DeleteAtFenceBoundary(source.Document,source.CaretIndex)){
            CommandResult fence=MarkdownCommands.DeleteFenceBlock(source.Document,source.CaretIndex);
            if(fence!=null)return fence;
        }
        CommandResult cell=MarkdownTableCommands.DeleteInCell(source.Document,source.CaretIndex,SourceRowCells(source.CaretIndex));
        if(cell!=null)return cell;
        // A line whose next non-blank line is a table header never merges the header row; the
        // caret steps to the first cell instead.
        CommandResult beforeTable=MarkdownTableCommands.DeleteBeforeTable(source.Document,source.CaretIndex);
        if(beforeTable!=null)return beforeTable;
        // Delete at the end of an ordered item joins the next line into it: the items after it are renumbered.
        return MarkdownStructuralInput.JoinOrdered(source.Document,source.CaretIndex,false);
    }
    // Backspace/Delete over a selection that reaches across a cell boundary: only the selected
    // text inside the cells goes, so the row keeps its "|" and its shape. A selection inside one
    // cell is text only and keeps the editor's own deletion.
    // True when the line at the offset is a table row or a delimiter row (the caret-line parse).
    bool SourceEdgeInTable(int offset){
        if(!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return true;
        var document=source.Document;
        var info=livePreview.Info(document.GetLineByOffset(Math.Max(0,Math.Min(offset,document.TextLength))));
        return info==null||info.Table||info.Delimiter;
    }
    CommandResult SourceCellSelectionDelete(){
        if(!LivePreviewAttached)return null;
        // A selection that covers whole tables is ordinary text: Delete/Backspace removes those
        // lines, so neither the cell rules nor the delimiter guard may keep them. The removal runs
        // here, not through the editor's command: MarkdownLivePreview's press resume normalizes
        // the selection before that command and would shorten a selection endpoint that sits at a
        // row's line end into the last cell, which turns the whole-table rule into a cell clear.
        CommandResult whole=MarkdownTableCommands.DeleteWholeTableSelection(source.Document,source.SelectionStart,source.SelectionLength);
        if(whole!=null){
            /* SelectionCoversWholeTables also answers true when neither end lies in a table (a plain selection inside a paragraph). That is not a table delete: the editor's own command runs the hidden-marker rules (HiddenMarkerDelete / RebalanceSelection in the basic view), so a selection with no table at either end goes there instead of being removed here as plain text ("**a b**" minus "b" left "**a **"). */
            if(!SourceEdgeInTable(source.SelectionStart)&&!SourceEdgeInTable(source.SelectionStart+source.SelectionLength))return null;
            // A selection that also touches a fence line takes both fence lines of that block with
            // it (MarkdownCommands.RemoveSelectionWithFences, run by the editor's own delete path in
            // both views); answering null here hands the key to that path instead of deleting the
            // tables and leaving the partner fence behind.
            if(MarkdownCommands.RemoveSelectionWithFences(source.Document,source.SelectionStart,source.SelectionLength)!=null)return null;
            CommandResult edge=TableEdgeRebalance();
            return edge!=null?edge:whole;
        }
        // A selection whose first or last line is the delimiter row is structure (basic mode
        // folds it, live mode reveals it): the row keeps its text and its line break, and the
        // selection stays so the user can see which edit was refused.
        if(SelectionTouchesDelimiter())return KeepSelection();
        int offset=source.SelectionStart;
        var cells=SourceRowCells(offset);
        if(cells==null||!MarkdownTableCommands.SpansCellBoundary(source.Document,offset,source.SelectionLength,cells))return null;
        return MarkdownTableCommands.ClearCellSelection(source.Document,offset,source.SelectionLength,cells);
    }
    // A whole-table selection that ends (or starts) inside a paragraph: the raw removal would cut a
    // pair of that paragraph in half ("**a b**" minus "**a " left "b**"). The paragraph's part goes
    // through the rebalance the editor uses for any selection delete (PlanSelectionReplace, basic view
    // only; the live view edits that line as source), and the table lines go with it in one edit.
    // Null when the rule does not apply, which keeps the raw removal.
    CommandResult TableEdgeRebalance(){
        if(!LivePreviewAttached||livePreview==null||source==null)return null;
        var document=source.Document;
        int start=source.SelectionStart,end=start+source.SelectionLength;
        bool startIn=SourceEdgeInTable(start),endIn=SourceEdgeInTable(end);
        int editStart,editLength,caret;string editText;
        CommandResult result=new CommandResult();
        if(startIn&&!endIn){
            var last=document.GetLineByOffset(end);
            if(end<=last.Offset)return null;
            if(!livePreview.PlanSelectionReplace(last.Offset,end,string.Empty,out editStart,out editLength,out editText,out caret))return null;
            if(editStart<last.Offset||editStart+editLength>last.EndOffset)return null;
            result.Add(start,editStart+editLength-start,document.GetText(last.Offset,editStart-last.Offset)+editText);
            result.SelectionStart=start+(caret-last.Offset);
        }else if(!startIn&&endIn){
            var first=document.GetLineByOffset(start);
            if(start>=first.EndOffset)return null;
            if(!livePreview.PlanSelectionReplace(start,first.EndOffset,string.Empty,out editStart,out editLength,out editText,out caret))return null;
            int kept=editStart+editLength;
            if(editStart<first.Offset||kept>first.EndOffset)return null;
            result.Add(editStart,end-editStart,editText+document.GetText(kept,first.EndOffset-kept));
            result.SelectionStart=caret;
        }else return null;
        result.SelectionLength=0;
        return result;
    }
    // A no-edit result that keeps the current selection (the host reselects it instead of
    // handing the key to the editor).
    CommandResult KeepSelection(){
        CommandResult kept=new CommandResult();
        kept.SelectionStart=source.SelectionStart;
        kept.SelectionLength=source.SelectionLength;
        return kept;
    }
    // P6 copy: text stays the Markdown source; rich targets get RTF rendered from it instead of
    // AvalonEdit's HTML of the raw markup. Bounded to 256K chars of selection.
    void SourceSettingData(object sender,System.Windows.DataObjectSettingDataEventArgs e){
        if(LivePreviewAttached&&e.Format==System.Windows.DataFormats.Html)e.CancelCommand();
    }
    void SourceCopying(object sender,System.Windows.DataObjectCopyingEventArgs e){
        if(e.IsDragDrop)return;
        // AvalonEdit's CutCopyWholeLine path raises Copying with an empty selection; copy the
        // same line it does so the private format also carries the list/task/quote prefix.
        int start=source.SelectionStart,length=source.SelectionLength;
        if(length==0&&source.Options.CutCopyWholeLine&&source.Document!=null&&source.Document.TextLength>0){
            DocumentLine line=source.Document.GetLineByOffset(Math.Min(source.CaretIndex,source.Document.TextLength));
            start=line.Offset;length=line.TotalLength;
        }
        string markdown=MarkdownClipboard.LiveSelection(source.Document,start,length,LivePreviewAttached&&!PlainText&&!livePreview.InCode(start));
        if(String.IsNullOrEmpty(markdown)||markdown.Length>256*1024)return;
        markdown=MarkdownClipboard.CanonicalNewlines(markdown);
        MarkdownClipboard.Set(e.DataObject,markdown);
        // The source paste command applies UnicodeText directly (Editor.cs PastingHandler),
        // so it must carry the same extended source as the private format.
        string unicode=markdown.Replace("\n","\r\n");
        e.DataObject.SetData(System.Windows.DataFormats.UnicodeText,unicode);
        e.DataObject.SetData(System.Windows.DataFormats.Text,unicode);
        if(!LivePreviewAttached)return;
        try{
            var document=Markdown.Parse(markdown,fontSize,family,Brushes.Black,false,DocumentBasePath(),T("[이미지: "));
            using(var stream=new MemoryStream()){
                new System.Windows.Documents.TextRange(document.ContentStart,document.ContentEnd).Save(stream,System.Windows.DataFormats.Rtf);
                e.DataObject.SetData(System.Windows.DataFormats.Rtf,System.Text.Encoding.ASCII.GetString(stream.ToArray()));
            }
        }catch(ArgumentException){}catch(IOException){}catch(InvalidOperationException){}
    }
    // An IME may commit a composition and deliver Enter as text input ("\r") instead of a key
    // event; route that newline through the same structural Enter (table boundary first). The same
    // hook carries the automatic list conversion, which the basic view does for the committed
    // space: a handler that consumes the input stops TextEntered from running, so the typing run
    // marker is cleared here the way SourceNewlineEntering does it.
    // The autoformat is the basic view's formatted-view rule, so it never runs with the 원문 편집
    // toggle on (the preview is detached and the source is plain text then); the newline path keeps
    // its old behaviour there.
    void SourceTextEntering(object sender,System.Windows.Input.TextCompositionEventArgs e){
        if(e.Handled||!LiveFormat||source==null)return;
        // The first input after a caret-only style toggle carries the armed pair with it.
        if(TypingStyleArmed){
            if(!EndsTypingStyle(e.Text)){if(WrapTypingStyle(e.Text)){e.Handled=true;return;}}
            else ClearTypingStyle();
        }
        if(e.Text==" "){
            if(source.ImeBusy||!LivePreviewAttached)return;
            CommandResult marker=MarkdownStructuralInput.AutoFormatListMarker(source.Document,source.SelectionStart,source.SelectionLength);
            if(marker==null)return;
            e.Handled=true;
            source.EndTypingRun();
            ApplyCommandResult(marker);
            return;
        }
        if(e.Text!="\r"&&e.Text!="\n"&&e.Text!="\r\n")return;
        CommandResult result=SourceEnterText();
        if(result==null)return;
        e.Handled=true;
        if(result.Edits.Count>0)ApplyCommandResult(result);else source.Select(result.SelectionStart,result.SelectionLength);
    }
    // External drops with rich formats go through the same conversion as paste. Plain text from
    // another window is inserted by AvalonEdit itself, which would split a table row, so a drop on
    // a table row takes the cell paste path instead. Internal drags never carry HTML/RTF here
    // (HTML is cancelled in SettingData, RTF is not added for drags).
    // The dragged data lives in the source program: a source that hangs or closes mid-drop fails the
    // OLE read with a COMException, which drops nothing instead of closing MDNote.
    void SourcePreviewDrop(object sender,System.Windows.DragEventArgs e){
        if(source!=null)source.NoteInactiveImeFocus();/* a drop from Explorer leaves this window in the background (SourceEditor) */try{SourcePreviewDropCore(sender,e);}catch(System.Runtime.InteropServices.ExternalException){e.Effects=System.Windows.DragDropEffects.None;e.Handled=true;}catch(System.Runtime.InteropServices.InvalidComObjectException){e.Effects=System.Windows.DragDropEffects.None;e.Handled=true;}
    }
    void SourcePreviewDropCore(object sender,System.Windows.DragEventArgs e){
        if(e.Handled||source==null||!Object.ReferenceEquals(sender,source.TextArea))return;
        // Files from Explorer: an all-image drop is the document's own insert and stops here, so the
        // vendor Drop never sees it; anything else continues to the window's open-as-document path.
        System.Windows.IDataObject dropped=e.Data;
        if(dropped!=null&&dropped.GetDataPresent(System.Windows.DataFormats.FileDrop)){
            var droppedFiles=dropped.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if(droppedFiles!=null&&droppedFiles.Length>0&&AcceptSourceImageDrop(droppedFiles,e.GetPosition(source.TextArea.TextView))){
                e.Effects=System.Windows.DragDropEffects.Copy;e.Handled=true;return;
            }
        }
        int moveAt;
        if(InternalMoveTarget(e,out moveAt)){
            // A drop inside the dragged block, or exactly at its end, moves nothing. Consume it here so
            // AvalonEdit cannot insert at that offset and then delete the source as two changes, and
            // record no history entry for a move that keeps the text (K4). Basic view: the block is
            // the one MoveSelectionTo reads (whole hidden pairs included), and the drop point is the
            // same snapped offset, so a drop that visually names the block's own markers is covered
            // by this refusal instead of reaching AvalonEdit's insert-then-delete.
            int dragStart=source.DragSourceStart,dragLength=source.DragSourceLength;
            if(LivePreviewAttached&&livePreview!=null){
                int widenedStart,widenedEnd;
                if(livePreview.WidenMoveRange(dragStart,dragLength,out widenedStart,out widenedEnd)){dragStart=widenedStart;dragLength=widenedEnd-widenedStart;}
                moveAt=livePreview.SnapInsertOffset(moveAt);
            }
            if(moveAt>=dragStart&&moveAt<=dragStart+dragLength){source.ClearDragSource();e.Effects=System.Windows.DragDropEffects.None;e.Handled=true;return;}
            if(MoveSelectionTo(moveAt)){e.Effects=System.Windows.DragDropEffects.Copy;e.Handled=true;return;}
            // A move the command could not apply (a rectangle selection, a failed edit) must not
            // fall back to AvalonEdit's own insert on a table row: that would write the raw text
            // into the row the cell rule protects.
            if(DropOnTableRow(moveAt)){source.ClearDragSource();e.Effects=System.Windows.DragDropEffects.None;e.Handled=true;return;}
        }
        if(!LiveFormat||!LivePreviewAttached)return;
        var data=e.Data;
        if(data==null)return;
        bool rich=data.GetDataPresent(System.Windows.DataFormats.Html)||data.GetDataPresent(System.Windows.DataFormats.Rtf);
        bool textPresent=data.GetDataPresent(System.Windows.DataFormats.UnicodeText)||data.GetDataPresent(System.Windows.DataFormats.Text);
        if(!rich&&!textPresent)return;
        var view=source.TextArea.TextView;
        var hit=view.GetPositionFloor(e.GetPosition(view)+view.ScrollOffset);
        if(!hit.HasValue)return;
        int at=source.Document.GetOffset(hit.Value.Location);
        // A plain drop anywhere else keeps the vendor's own insert; only a table row needs the
        // cell rule here (newline -> "<br>", "|" -> "\|", exactly like Ctrl+V).
        if(!rich&&!DropOnTableRow(at))return;
        string text=data.GetDataPresent(System.Windows.DataFormats.UnicodeText)?data.GetData(System.Windows.DataFormats.UnicodeText) as string:textPresent?data.GetData(System.Windows.DataFormats.Text) as string:null;
        if(text==null&&!rich)return;
        source.Select(at,0);
        LivePaste(data,rich?false:true,text);
        e.Effects=System.Windows.DragDropEffects.Copy;e.Handled=true;
    }
    // True when the drop point lands on a table row (the delimiter row included: it is structure,
    // and LivePaste moves a paste there into the header's last cell). O(line) from the line cache.
    bool DropOnTableRow(int at){
        if(!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return false;
        var document=source.Document;
        at=Math.Max(0,Math.Min(at,document.TextLength));
        var info=livePreview.Info(document.GetLineByOffset(at));
        return info!=null&&info.Table;
    }
    // A drag that starts and ends in this editor is a move. AvalonEdit inserts at the drop point
    // and removes the source selection after the drag returns, i.e. two document changes and, with
    // its own undo stack disabled, two app history entries. Do both here as one action and answer
    // Copy so the vendor handler leaves the source text alone.
    bool InternalMoveTarget(System.Windows.DragEventArgs e,out int at){
        at=-1;
        if(source==null||!CodeMode||!editing||documentOperationBusy)return false;
        if(!source.DragSourceActive())return false;
        System.Windows.IDataObject data=e.Data;
        if(data==null||!data.GetDataPresent(System.Windows.DataFormats.UnicodeText))return false;
        if((e.AllowedEffects&System.Windows.DragDropEffects.Move)!=System.Windows.DragDropEffects.Move)return false;
        if((e.KeyStates&System.Windows.DragDropKeyStates.ControlKey)!=0)return false;
        // Only the drag this editor started carries the token, so another window's drag or a later
        // drop is never mistaken for a move of the marker's range.
        string token=data.GetData(SourceEditor.DragTokenFormat) as string;
        if(String.IsNullOrEmpty(token)||!String.Equals(token,source.DragSourceToken,StringComparison.Ordinal))return false;
        if(!DragTextMatches(data))return false;
        var view=source.TextArea.TextView;
        var hit=view.GetPositionFloor(e.GetPosition(view)+view.ScrollOffset);
        if(!hit.HasValue){
            /* The blank band below the last line (or above the first) has no caret position.
               Handing the drop back to AvalonEdit would record its insert and the source removal
               as two changes, i.e. two app history entries; clamp the drop to the document end
               (start, above the first line) so the one-entry move below takes it. */
            at=e.GetPosition(view).Y<0?0:source.Document.TextLength;
            return true;
        }
        at=source.Document.GetOffset(hit.Value.Location);
        return true;
    }
    // The offered text must be the dragged selection itself (the vendor normalizes newlines), so a
    // stale start marker can never turn an unrelated drop into a move.
    bool DragTextMatches(System.Windows.IDataObject data){
        string offered=data.GetData(System.Windows.DataFormats.UnicodeText) as string;
        if(offered==null)return false;
        return Canonical(offered)==Canonical(source.Document.GetText(source.DragSourceStart,source.DragSourceLength));
    }
    // One compound entry: undo restores the source selection, redo selects the moved block. A move
    // that lands on a table row takes the cell paste rule (newline -> "<br>", "|" -> "\|") so the
    // row keeps its shape; a source selection across cells is cleared with the same cell rule (the
    // separators stay), and the source clear and the insert stay one entry.
    internal bool MoveSelectionTo(int at){
        if(source==null||!CodeMode||!editing||documentOperationBusy)return false;
        if(source.TextArea.Selection is ICSharpCode.AvalonEdit.Editing.RectangleSelection)return false;   /* row-by-row move stays with AvalonEdit */
        if(!source.DragSourceActive())return false;
        /* Basic view: a dragged selection that holds only one fence of a code block would leave
           its partner behind as literal text. The move is refused like Delete/Cut and the drop is
           consumed, so AvalonEdit's own insert-then-delete cannot take the fence. */
        if(source.SelectionLength>0&&LivePreviewAttached&&livePreview!=null&&livePreview.SelectionBreaksFence()){source.ClearDragSource();return true;}
        var document=source.Document;
        int start=source.DragSourceStart,length=source.DragSourceLength;
        if(start<0||length<=0||start+length>document.TextLength)return false;
        if(at<0||at>document.TextLength)return false;
        /* Basic view: the move takes what the reader sees, so the paste path's marker rules run
           first. A selection that cuts a hidden pair, or that is a pair's whole visible text,
           takes the markers with it (no "****" left in the source, no half marker carried to the
           drop), and the drop point follows the same snap a paste does, so a drop on a line's
           left margin lands after its hidden list/heading prefix instead of in front of it. */
        if(LivePreviewAttached&&livePreview!=null){
            int widenedStart,widenedEnd;
            if(livePreview.WidenMoveRange(start,length,out widenedStart,out widenedEnd)){start=widenedStart;length=widenedEnd-widenedStart;}
            at=livePreview.SnapInsertOffset(at);
            if(at<0||at>document.TextLength)return false;
        }
        if(at>=start&&at<=start+length)return false;   /* dropping inside the selection or at either edge moves nothing */
        string dragged=document.GetText(start,length);
        int drop=at;
        SourceEdit insert=new SourceEdit(at,0,dragged);
        List<MarkdownLivePreview.Cell> cells;
        if(TableDropCells(at,out drop,out cells)){
            // No landing cell (a malformed row): the drop changes nothing, but it is consumed here
            // so AvalonEdit cannot write raw text onto the structure row.
            if(cells==null||cells.Count==0){source.ClearDragSource();return true;}
            CommandResult cell=MarkdownTableCommands.PasteInCell(document,drop,0,dragged,cells);
            if(cell==null||cell.Edits.Count!=1){source.ClearDragSource();return true;}
            insert=cell.Edits[0];
            // Clamping the landing into its cell can put it back inside the dragged block: nothing
            // moves, and the row keeps its shape.
            if(insert.Offset>=start&&insert.Offset<=start+length){source.ClearDragSource();return true;}
        }
        // The source side follows the same table rule the delete keys use: a selection that
        // reaches across cell boundaries clears each selected cell's own text, so a moved row or
        // multi-cell run never takes a "|" or a line with it. A whole table selected is ordinary
        // text (the cell rule declines it) and keeps the raw removal. All edits go in one
        // CommandResult, so the move stays one undo step.
        var result=new CommandResult();
        int removedBeforeInsert=0;
        CommandResult cleared=null;
        if(LivePreviewAttached&&livePreview!=null){
            var sourceCells=SourceRowCells(start);
            if(sourceCells!=null&&sourceCells.Count>0&&MarkdownTableCommands.SpansCellBoundary(document,start,length,sourceCells)){
                // The cell rule never removes a line break, and its table scan stops at the
                // table's last line: a selection that ends with the row's newline is trimmed to
                // the last touched line's text so the rule still applies to it.
                int clearLength=length;
                DocumentLine lastTouched=document.GetLineByOffset(Math.Max(start,start+length-1));
                if(lastTouched.EndOffset<start+length)clearLength=Math.Max(0,lastTouched.EndOffset-start);
                if(clearLength>0)cleared=MarkdownTableCommands.ClearCellSelection(document,start,clearLength,sourceCells);
            }
        }
        if(cleared!=null&&cleared.Edits.Count>0){
            foreach(SourceEdit edit in cleared.Edits){
                result.Add(edit.Offset,edit.Length,edit.Text);
                if(edit.Offset<insert.Offset)removedBeforeInsert+=edit.Length;
            }
        }else{
            result.Add(start,length,string.Empty);
            if(start<insert.Offset)removedBeforeInsert=length;
        }
        result.Add(insert.Offset,insert.Length,insert.Text);
        result.SortEdits();
        int target=insert.Offset-removedBeforeInsert;
        result.SelectionStart=target;
        result.SelectionLength=insert.Text.Length;
        source.ClearDragSource();
        ApplyCommandResult(result,start,length);
        // A failed command rolled the document back; leave the drop to AvalonEdit then, instead of
        // reporting a move that did not happen. The range is checked first: a rolled-back document
        // can be shorter than the moved block, and reading it would run past the text.
        var check=source.Document;
        if(target<0||target>check.TextLength||insert.Text.Length>check.TextLength-target)return false;
        return source.SelectionStart==target&&source.SelectionLength==insert.Text.Length&&check.GetText(target,insert.Text.Length)==insert.Text;
    }
    // The cells of the table row a drop lands on, so the move can write through PasteInCell. The
    // delimiter row is structure: drop is moved onto the header row's last cell, exactly where a
    // paste on that row goes. False when the drop is on any other line. True with cells null when
    // the row has no landing cell (the drop is then swallowed).
    bool TableDropCells(int at,out int drop,out List<MarkdownLivePreview.Cell> cells){
        drop=at;cells=null;
        if(!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return false;
        var document=source.Document;
        at=Math.Max(0,Math.Min(at,document.TextLength));
        var line=document.GetLineByOffset(at);
        var info=livePreview.Info(line);
        if(info==null||!info.Table)return false;
        if(!info.Delimiter){cells=info.Cells;return true;}
        var header=line.PreviousLine;
        var headerInfo=header==null?null:livePreview.Info(header);
        if(headerInfo==null||!headerInfo.Table||headerInfo.Cells==null||headerInfo.Cells.Count==0)return true;
        drop=header.Offset+headerInfo.Cells[headerInfo.Cells.Count-1].End;
        cells=headerInfo.Cells;
        return true;
    }
    bool LiveUpdateTools(){
        // Runs before the live-view check so a mode switch that leaves live editing still opens
        // the table gate again (R2); the gate is idempotent and owns only the state it wrote.
        UpdateTableCommandAvailability();
        if(!LiveFormat)return false;
        if(updatingTools)return true;
        RefreshSizeBox();
        // The live parse decides whether the caret line is drawn as a rule (prefixes and the
        // setext-underline case included); the raw grammar only answers in the detached 원문 view.
        var ruleLine=source.Document.GetLineByOffset(source.CaretIndex);
        var ruleInfo=LivePreviewAttached&&livePreview!=null?livePreview.Info(ruleLine):null;
        Active(ruleButton,ruleInfo!=null?ruleInfo.Rule:LiveRuleLine(source.Document,ruleLine));
        // The 원문 toggle attaches or detaches the preview, so the table tool row's "the caret is
        // on a table" answer changes with it; it is refreshed here too, not only on the attached
        // path below (SwitchSurface runs UpdateTools after LayoutMode for the same reason).
        if(!LivePreviewAttached){UpdateTableTools();return true;}
        // 11-01 user: the block buttons light up for the caret line like B/I/S do (list, task,
        // quote, code block), read from the caret line's cached parse (O(line)).
        bool listed=ruleInfo!=null&&ruleInfo.ListEnd>ruleInfo.ListStart;
        bool tasked=listed&&ruleInfo.List!=null&&ruleInfo.List.Task;
        Active(listButton,listed&&!tasked);
        Active(taskButton,tasked);
        Active(quoteButton,ruleInfo!=null&&(ruleInfo.Quote||ruleInfo.QuoteLevels>0));
        Active(blockButton,ruleInfo!=null&&(ruleInfo.Code||ruleInfo.FenceLine));
        bool bold,italic,strike,code,highlight;int heading;
        livePreview.FormatAt(source.CaretIndex,out bold,out italic,out strike,out code,out highlight,out heading);
        // FormatAt answers the format the next character would take, so at the outer edge of a
        // hidden closing run it reports the pair's inside. Inline code has no such inheritance:
        // the button is lit only where the caret is really inside the code run (user, 2026-10-29).
        code=LiveInlineCodeAt(source.CaretIndex);
        if(source.SelectionLength>0){
            // A drag's caret can be on either side of the hidden == (or on the next line).
            // Read the selected visible text, not that insertion point. One line only: pairs
            // never cross a newline, and a caret/selection callback must stay O(edited line).
            int start=source.SelectionStart,end=start+source.SelectionLength;
            var line=source.Document.GetLineByOffset(start);
            var selected=livePreview.Info(line);
            highlight=end<=line.EndOffset&&MarkdownCommands.SelectionHasInlineStyle(selected,start-line.Offset,end-line.Offset,MarkdownLivePreview.Style.Highlight);
            code=end<=line.EndOffset&&MarkdownCommands.SelectionHasInlineStyle(selected,start-line.Offset,end-line.Offset,MarkdownLivePreview.Style.Code);
        }
        if(code)highlight=false;
        // Every armed style is shown as active at the caret: the next typed text is written in all
        // of them (the size box reads the armed size through PendingTypingSize in RefreshSizeBox).
        if(TypingStyleArmed&&typingArmed!=null){
            for(int i=0;i<typingArmed.Count;i++){
                string armedMarker=typingArmed[i].Marker;
                if(armedMarker=="**")bold=true;
                else if(armedMarker=="*")italic=true;
                else if(armedMarker=="~~")strike=true;
                else if(armedMarker=="=="){highlight=true;code=false;}
                else if(armedMarker=="`"){code=true;highlight=false;}
            }
        }
        boldButton.Tag=bold?"Active":null;italicButton.Tag=italic?"Active":null;strikeButton.Tag=strike?"Active":null;
        markButton.Tag=highlight?"Active":null;
        codeButton.Tag=code?"Active":null;headingButton.Tag=heading>0?"Active":null;if(liveHeadingLevel!=heading){liveHeadingLevel=heading;UpdateHeadingCaption();}/* b39: caption follows the caret line level (cached per line, O(line)) */
        UpdateTableTools();
        return true;
    }
    // The block buttons that would rewrite a table row read disabled while the caret is on one
    // (the command side refuses too, which is what the F-key path reaches). The 코드 block button
    // stays enabled: inside a cell it writes inline code instead of a fence.
    bool CaretOnTableLine(){
        if(!LivePreviewAttached||source==null||source.Document==null)return false;
        int at=Math.Max(0,Math.Min(source.SelectionStart,source.Document.TextLength));
        var info=livePreview.Info(source.Document.GetLineByOffset(at));
        return info!=null&&info.Table;
    }
    // R2: the table rule is a second gate, not the whole answer. It only ever turns these five
    // buttons off while the caret is on a table row in the live view; when the caret (or the view)
    // leaves the table it puts them back to the state their own rule wants (UpdateCodeAvailability:
    // enabled while the live view can edit) instead of leaving them disabled or forcing a button
    // on that another rule disabled.
    bool tableBlockGated;
    Button[] tableBlockButtons;
    Button[] TableBlockButtons(){
        // 11-01 user: the code block button is off inside a table too, like the other block buttons.
        if(tableBlockButtons==null)tableBlockButtons=new Button[]{headingButton,listButton,taskButton,quoteButton,ruleButton,blockButton};
        return tableBlockButtons;
    }
    void UpdateTableCommandAvailability(){
        Button[] buttons=TableBlockButtons();
        // The live preview owns the table rows; the detached 원문 view edits plain source, where
        // a table row is not a special surface any more and the block commands must work again.
        bool blocked=LiveFormat&&LivePreviewAttached&&source!=null&&source.Document!=null&&CaretOnTableLine();
        if(blocked){
            // Re-assert on every pass: another rule (UpdateCodeAvailability on a mode switch)
            // may have enabled the buttons while the gate was already engaged.
            tableBlockGated=true;
            for(int i=0;i<buttons.Length;i++)if(buttons[i]!=null)buttons[i].IsEnabled=false;
            return;
        }
        if(!tableBlockGated)return;
        tableBlockGated=false;
        bool editable=LiveFormat&&source!=null&&source.Document!=null;
        for(int i=0;i<buttons.Length;i++)if(buttons[i]!=null)buttons[i].IsEnabled=editable;
    }
    // Exact inline-code membership at an offset, from the same per-line cache FormatAt reads.
    // The caret one step past a hidden closing run is outside the run even though it draws on the
    // pair's inside; the inner edges and every offset inside the markers are inside it.
    bool LiveInlineCodeAt(int offset){
        if(!LivePreviewAttached||source==null||source.Document==null)return false;
        var document=source.Document;
        offset=Math.Max(0,Math.Min(offset,document.TextLength));
        var info=livePreview.Info(document.GetLineByOffset(offset));
        if(info==null)return false;
        int relative=offset-document.GetLineByOffset(offset).Offset;
        int start,end;
        bool closing=MarkdownLivePreview.Session.HiddenCloseRun(info,relative,out start,out end);
        if(closing&&relative==end)return false;
        int at=closing?start:relative;
        foreach(MarkdownLivePreview.StyledPart part in info.Styles){
            if((part.Style&MarkdownLivePreview.Style.Code)==0)continue;
            if(at>=part.Start&&at<=part.Start+part.Length)return true;
        }
        return false;
    }
    // B3: block commands never rewrite a table row. The live preview's own parse decides where
    // it is attached (O(line), cached); the raw table rule answers in the detached 원문 view.
    // The caret and both ends of a selection count, so a command never touches a row it only
    // borders. Inline commands keep working inside a cell and do not consult this.
    bool CommandTouchesTable(TextDocument document,int selectionStart,int selectionLength){
        if(document==null)return false;
        int start=Math.Max(0,Math.Min(selectionStart,document.TextLength));
        int end=Math.Max(start,Math.Min(selectionStart+Math.Max(0,selectionLength),document.TextLength));
        if(LineIsTable(document,start))return true;
        return end>start&&LineIsTable(document,end-1);
    }
    bool LineIsTable(TextDocument document,int offset){
        if(LivePreviewAttached&&source!=null&&Object.ReferenceEquals(document,source.Document)){
            var info=livePreview.Info(document.GetLineByOffset(offset));
            if(info!=null&&info.Table)return true;
        }
        int first,last;
        return MarkdownTableCommands.FindTable(document,offset,out first,out last);
    }
    // R1: a selection that starts above a table and ends below it must not put a quote marker or
    // a list marker on the table's rows. Every selected line is classified once (O(selection), at
    // command time only); each run of non-table lines takes the command on its own, and the runs'
    // edits are merged into one command so the host still records one undo step. A selection whose
    // every line is a table row changes nothing.
    CommandResult CommandOutsideTable(TextDocument document,int selectionStart,int selectionLength,Func<TextDocument,int,int,CommandResult> command){
        if(document==null||command==null)return null;
        int start=Math.Max(0,Math.Min(selectionStart,document.TextLength));
        int end=Math.Max(start,Math.Min(selectionStart+Math.Max(0,selectionLength),document.TextLength));
        DocumentLine first=document.GetLineByOffset(start);
        DocumentLine last=document.GetLineByOffset(end>start?end-1:start);
        CommandResult merged=new CommandResult();
        int runStart=-1,runEnd=-1;
        for(int number=first.LineNumber;number<=last.LineNumber;number++){
            DocumentLine line=document.GetLineByNumber(number);
            if(LineIsTable(document,line.Offset)){
                if(runStart>=0){AppendCommandRun(document,command,runStart,runEnd,merged);runStart=-1;}
                continue;
            }
            if(runStart<0)runStart=line.Offset;
            runEnd=line.Offset+line.Length;
        }
        if(runStart>=0)AppendCommandRun(document,command,runStart,runEnd,merged);
        if(merged.Edits.Count==0)return null;
        merged.SortEdits();
        FinishCommandSelection(merged,selectionStart,selectionStart+Math.Max(0,selectionLength));
        return merged;
    }
    static void AppendCommandRun(TextDocument document,Func<TextDocument,int,int,CommandResult> command,int runStart,int runEnd,CommandResult merged){
        CommandResult part=command(document,runStart,runEnd-runStart);
        if(part==null)return;
        for(int i=0;i<part.Edits.Count;i++)merged.Edits.Add(part.Edits[i]);
    }
    // The block commands' own selection mapping (MarkdownCommands.FinishSelection), applied to the
    // merged edit list so the whole multi-run command keeps one selection.
    static void FinishCommandSelection(CommandResult result,int originalStart,int originalEnd){
        bool caret=originalStart==originalEnd;
        int start=MapCommandPosition(result.Edits,originalStart,true);
        int end=MapCommandPosition(result.Edits,originalEnd,caret);
        if(end<start)end=start;
        result.SelectionStart=start;
        result.SelectionLength=end-start;
        result.SortEdits();
    }
    static int MapCommandPosition(List<SourceEdit> edits,int position,bool includeInsertionAtPosition){
        int delta=0;
        for(int i=0;i<edits.Count;i++){
            SourceEdit edit=edits[i];
            if(edit.Offset+edit.Length<=position){
                if(edit.Length>0||includeInsertionAtPosition||edit.Offset+edit.Length<position)delta+=edit.Text.Length-edit.Length;
            }else if(edit.Offset>=position)break;
            else return edit.Offset+delta;
        }
        return position+delta;
    }
    // B3: inside a cell the 코드 button writes inline code, never a fence. An empty caret arms
    // the backtick pair for the text typed next (no empty "``" is written into the cell); a
    // selection wholly inside one cell becomes a code span. Wider selections and the delimiter
    // row stay untouched, so the row is never cut.
    CommandResult InlineCodeInCell(TextDocument document,int selectionStart,int selectionLength){
        List<MarkdownLivePreview.Cell> cells=SourceRowCells(selectionStart);
        if(cells==null||cells.Count==0)return null;
        DocumentLine line=document.GetLineByOffset(selectionStart);
        int from=selectionStart-line.Offset;
        if(selectionLength==0){
            foreach(MarkdownLivePreview.Cell cell in cells)if(from>=cell.Start&&from<=cell.End){
                // The same caret-only arming every other style toggle uses (WrapTyped writes the
                // pair around the first input), so an abandoned click leaves the row unchanged.
                return new CommandResult {TypingOpen="`",TypingClose="`",TypingMarker="`",SelectionStart=selectionStart,SelectionLength=0};
            }
            return null;
        }
        int end=selectionStart+selectionLength;
        if(document.GetLineByOffset(Math.Max(selectionStart,end-1)).LineNumber!=line.LineNumber)return null;
        int to=end-line.Offset;
        foreach(MarkdownLivePreview.Cell cell in cells)if(from>=cell.Start&&to<=cell.End)
            return MarkdownCommands.ToggleInline(document,selectionStart,selectionLength,"`");
        return null;
    }
    bool LiveScript(bool superscript){return RunLiveCommand(delegate(TextDocument d,int s,int l){return MarkdownCommands.ToggleScript(d,s,l,superscript);});}
    bool LiveInline(string kind){
        string marker=kind=="bold"?"**":kind=="italic"?"*":kind=="strike"?"~~":kind=="highlight"?"==":"`";
        if(!LiveFormat||source==null)return false;
        // R8: the pending closing-edge space is the live surface's own state and the caret it
        // names must be the settled one, so the check runs after a pending IME commit has landed,
        // exactly like the command itself. While a composition is pending the toggle is claimed
        // and runs when the commit ends (returning false here would let the rich path edit the
        // document under the composition).
        bool parked=source.ImeBusy;
        bool handled=false;
        source.RunAfterComposition(delegate{
            if(!LiveFormat||source==null)return;
            // B1: with the closing-edge space still pending, the toggle for one of the run's own
            // styles ends that run at the word instead of opening a second pair. Nothing is edited
            // and no undo step is recorded; the toolbar drops the lit button (UpdateTools) and the
            // next character is written after the space, outside the pair.
            if(marker!="`"&&LivePreviewAttached&&livePreview.EndPendingCloseStyle(marker)){UpdateTools();handled=true;return;}
            handled=RunLiveCommand(delegate(TextDocument d,int s,int l){return MarkdownCommands.ToggleInline(d,s,l,marker);});
        });
        return parked||handled;
    }
    bool LiveHeading(int level){return RunLiveCommand(delegate(TextDocument d,int s,int l){
        return CommandOutsideTable(d,s,l,delegate(TextDocument inner,int innerStart,int innerLength){
            return MarkdownCommands.SetHeading(inner,innerStart,innerLength,level);});});}
    bool LiveList(ListKind kind){return RunLiveCommand(delegate(TextDocument d,int s,int l){
        return CommandOutsideTable(d,s,l,delegate(TextDocument inner,int innerStart,int innerLength){
            return MarkdownCommands.ToggleList(inner,innerStart,innerLength,kind);});});}
    bool LiveQuote(){return RunLiveCommand(delegate(TextDocument d,int s,int l){
        return CommandOutsideTable(d,s,l,delegate(TextDocument inner,int innerStart,int innerLength){
            return MarkdownCommands.ToggleQuote(inner,innerStart,innerLength);});});}
    bool LiveCheck(){return RunLiveCommand(delegate(TextDocument d,int s,int l){
        if(CommandTouchesTable(d,s,l))return null;
        return MarkdownCommands.ToggleTaskCheck(d,s);});}
    bool LiveCodeBlock(){return RunLiveCommand(delegate(TextDocument d,int s,int l){
        if(!CommandTouchesTable(d,s,l))return MarkdownCommands.InsertCodeBlock(d,s,l,"");
        return InlineCodeInCell(d,s,l);});}
    // Read exactly the caret line: up to three leading spaces, then at least three identical
    // markers. "-", "*" and "_" count, the same grammar the live parse draws (IsRule).
    static bool LiveRuleLine(TextDocument document,DocumentLine line){
        string value=document.GetText(line);int at=0;while(at<value.Length&&at<3&&value[at]==' ')at++;
        if(at==value.Length)return false;char marker=value[at];if(marker!='-'&&marker!='*'&&marker!='_')return false;
        int count=0;for(;at<value.Length;at++){char ch=value[at];if(ch==marker)count++;else if(ch!=' '&&ch!='\t')return false;}return count>=3;
    }
    static bool LiveBlankLine(TextDocument document,DocumentLine line){if(line==null)return false;for(int at=line.Offset;at<line.EndOffset;at++){char ch=document.GetCharAt(at);if(ch!=' '&&ch!='\t')return false;}return true;}
    bool LiveRule(){return RunLiveCommand(delegate(TextDocument d,int s,int l){
        if(CommandTouchesTable(d,s,l))return null;
        var line=d.GetLineByOffset(source.CaretIndex);
        // A fence line and every line inside a fenced block are literal code: a rule written there
        // draws as code text, and removing the line would cut the block open instead of ending a
        // rule (the plain insert refuses the same spot through MarkdownCommands.IsInsideCode).
        if(MarkdownStructuralInput.IsInsideFence(d,line))return null;
        // The toggle follows what the view draws (the live parse's own rule answer); the raw
        // grammar is the answer only while the preview is detached.
        var info=LivePreviewAttached&&livePreview!=null?livePreview.Info(line):null;
        bool on=info!=null?info.Rule:LiveRuleLine(d,line);
        if(!on)return MarkdownCommands.InsertRule(d,s+l);
        DocumentLine first=line,last=line;
        if(LiveBlankLine(d,line.PreviousLine))first=line.PreviousLine;else if(LiveBlankLine(d,line.NextLine))last=line.NextLine;
        int start=first.Offset,end=last.Offset+last.TotalLength;
        // At EOF a removed line owns the separator before it (including CRLF as a unit).
        if(last.DelimiterLength==0&&first.PreviousLine!=null)start=first.PreviousLine.EndOffset;
        var result=new CommandResult {SelectionStart=start,SelectionLength=0};result.Add(start,end-start,"");return result;
    });}
    // The app's table size counts the header row; MarkdownCommands counts body rows only. The
    // header placeholder follows the UI language: both InsertTable paths take the same word, so
    // a grid written next to an existing table reads the same as one written anywhere else.
    bool LiveTable(int columns,int rows){return RunLiveCommand(delegate(TextDocument d,int s,int l){
        // One row in the dialog is the header and its delimiter row only: what the command calls
        // rows is the body count, while the app's count includes the header.
        int body=Math.Max(0,rows-1);int at=Math.Min(s+l,d.TextLength);
        // A grid written inside a fenced block is literal code, not a table (the plain insert
        // refuses it itself; the safe path below would write the grid into the fence).
        if(MarkdownStructuralInput.IsInsideFence(d,d.GetLineByOffset(at)))return null;
        if(!NearTable(d,at))return MarkdownCommands.InsertTable(d,at,body,columns,T("제목"));
        return MarkdownTableCommands.InsertTableSafely(d,at,body,columns,T("제목"));});}
    // The same condition InsertTableSafely tests before writing its own gap-separated grid.
    bool NearTable(TextDocument d,int at){
        int first,last;
        if(MarkdownTableCommands.FindTable(d,at,out first,out last))return true;
        var line=d.GetLineByOffset(at);var above=line.PreviousLine;
        return above!=null&&MarkdownTableCommands.FindTable(d,above.Offset,out first,out last)&&last==above.LineNumber;
    }
    bool LiveColor(Color? color){
        string css=color.HasValue?String.Format("#{0:X2}{1:X2}{2:X2}",color.Value.R,color.Value.G,color.Value.B):null;
        return RunLiveCommand(delegate(TextDocument d,int s,int l){return MarkdownCommands.SetSpanColor(d,s,l,css);});
    }
    bool LiveLink(){
        if(!LiveFormat)return false;
        // The address dialog takes the keyboard away, and AvalonEdit cancels the IME composition
        // on that focus change (the same reason the Ctrl+K path commits first). The commit lands
        // in the document before the dialog opens; the dialog and the insert run after it.
        RunAfterCompositionOrNow(delegate{
            if(!LiveFormat)return;
            string url=Input(T("링크 주소"),"https://");
            if(String.IsNullOrWhiteSpace(url)){source.Focus();return;}
            RunLiveCommand(delegate(TextDocument d,int s,int l){
                // The link command declines inside a fence, structure and inline code; say so instead
                // of leaving the click unanswered (the notice sits next to the normal status and
                // clears on the next caret move or after a few seconds).
                CommandResult result=MarkdownCommands.InsertLink(d,s,l,url.Trim(),T("링크"));
                if(result==null)ShowStatusNotice(T("코드나 구조 안에는 링크를 넣을 수 없습니다"));
                return result;
            });
        });
        return true;
    }
    bool LiveImage(){
        if(!LiveFormat)return false;
        // The file dialog is another keyboard owner: commit the composition before it opens,
        // exactly like the Ctrl+Shift+I path, so the composing syllable is not lost.
        RunAfterCompositionOrNow(delegate{
            if(!LiveFormat)return;
            var dialog=new Microsoft.Win32.OpenFileDialog {Filter=MarkdownCommands.ImageFileFilter};
            if(dialog.ShowDialog(this)!=true){source.Focus();return;}
            InsertLiveImageFile(dialog.FileName);
        });
        return true;
    }
    // The image button's insert half without the dialog: the reference rule is the shared one, so
    // the button and an Explorer drop write the same paths in this view and in the basic view.
    internal bool InsertLiveImageFile(string file){
        if(!LiveFormat)return false;
        /* The image button's file dialog takes the same staging rule as a drop (PasteSupport.cs
           ImageFilesForInsert): a picture outside the document's picture folder is staged in the shared
           temp folder first and the save moves it into <docname>\. */
        file=ImageFileForInsert(file);
        string reference=ImageLinkReference(file);
        string alt=Path.GetFileNameWithoutExtension(file);
        return RunLiveCommand(delegate(TextDocument d,int s,int l){return MarkdownCommands.InsertImage(d,s+l,reference,alt);});
    }
    // Explorer drop on the source surface (live preview and 원문 편집). Images become one image
    // line per file at the hit position, applied as one history entry; a TXT document answers
    // with the notice instead, and a mixed drop is left to the window's open-as-document path.
    // A Markdown document always takes a drop of existing images, read mode included: the Ctrl+E
    // toggle turns editing on first and the images land at the point the user aimed at (taken
    // before that switch), so the window's open-as-document answer never hands a PNG to the text
    // decoder. A drop the editor cannot write answers with the notice instead of that path too.
    internal bool AcceptSourceImageDrop(string[] files,System.Windows.Point point){
        try{
            if(!MarkdownCommands.AllImageFiles(files))return false;
            // Only files that still exist on disk are inserted; a name that is gone would leave a
            // broken reference, so a drop of nothing but missing files answers like any non-image drop.
            string[] present=MarkdownCommands.ExistingImageFiles(files);
            if(present.Length==0)return false;
            if(PlainText){ShowImageTxtNotice();return true;}
            if(source==null||source.Visibility!=System.Windows.Visibility.Visible){ShowImageTxtNotice();return true;}
            // An open or save holds the document busy and read-only; say that instead of folding
            // the drop into the read-mode/TXT answers (the window's own path does the same).
            if(documentOperationBusy){ShowImageBusyNotice();return true;}
            /* A picture outside the document's folder is copied in only once the editor will take
               it (below), so a refused drop leaves no copy behind. */
            var view=source.TextArea.TextView;
            var hit=view.GetPositionFloor(point+view.ScrollOffset);
            // The empty space under the last line hits no text line of its own; a drop there lands
            // at the document end instead of falling through to the window's file answer.
            int at=hit.HasValue?source.Document.GetOffset(hit.Value.Location):source.Document.TextLength;
            if(!editing){
                // The Ctrl+E entry point, not SetMode: it flushes the pipeline the same way the
                // menu does. A busy document stays read-only, so nothing can be inserted there.
                Toggle();
                if(!editing){ShowImageTxtNotice();return true;}
            }
            if(documentOperationBusy){ShowImageBusyNotice();return true;}
            /* A picture outside the document's folder is copied in now (PasteSupport.cs
               ImageFilesForInsert), after the read-mode switch so no copy is left behind by a drop
               the editor then refuses; the link then names the copy beside the document. */
            present=ImageFilesForInsert(present);
            var references=new List<string>();
            var alts=new List<string>();
            foreach(string file in present){references.Add(ImageLinkReference(file));alts.Add(Path.GetFileNameWithoutExtension(file));}
            var owner=source;var document=owner.Document;
            try{
                owner.RunAfterComposition(delegate{
                    if(!Object.ReferenceEquals(owner,source)||!Object.ReferenceEquals(document,source.Document))return;
                    try{
                        FlushDocumentChanges();
                        int offset=Math.Max(0,Math.Min(at,source.Document.TextLength));
                        CommandResult result=MarkdownCommands.InsertImages(source.Document,offset,references,alts);
                        if(result!=null){ApplyCommandResult(result);source.Focus();}
                    }catch(Exception ex){ReportCommandFailure(ex);}
                });
            }catch(Exception ex){ReportCommandFailure(ex);return false;}
            return true;
        }catch(Exception ex){ReportCommandFailure(ex);return false;}
    }
    // Live fenced-code copy: the basic view hangs one "코드 복사" item on the code paragraph
    // (CodeHighlight.cs:14) and copies TextRange(...).Text.TrimEnd('\r','\n'), i.e. the lines
    // between the fences with trailing blank lines dropped. The live view draws the fences as
    // nothing, so the menu lives on the source TextArea and appears only for a right-click inside
    // a fence; everywhere else the surface keeps the menu it had (none). Probes capture the text
    // instead of writing the user's clipboard (the same rule as Markdown.LinkOpener).
    internal static Action<string> LiveCodeCopyHook;
    System.Windows.Controls.ContextMenu liveCodeMenu;
    internal string LiveCodeBodyAt(int offset){
        if(!LivePreviewAttached||source==null)return null;
        var document=source.Document;
        if(offset<0||offset>document.TextLength)return null;
        var line=document.GetLineByOffset(offset);
        if(!livePreview.Info(line).Code)return null;
        var open=LiveFenceOpen(document,line);
        if(open==null)return null;
        var close=LiveFenceClose(document,open);
        int start=open.EndOffset+open.DelimiterLength;
        int end=close==null?document.TextLength:close.Offset;
        if(end<start)return null;
        return document.GetText(start,end-start).TrimEnd('\r','\n');
    }
    internal System.Windows.Controls.ContextMenu LiveCodeMenuAt(int offset){
        string body=LiveCodeBodyAt(offset);
        if(body==null)return null;
        var menu=new System.Windows.Controls.ContextMenu();
        var copy=new System.Windows.Controls.MenuItem {Header=T("코드 복사")};
        copy.Click+=delegate{CopyLiveCode(body);};
        menu.Items.Add(copy);
        return menu;
    }
    static void CopyLiveCode(string text){
        if(String.IsNullOrEmpty(text))return;
        Action<string> hook=LiveCodeCopyHook;
        if(hook!=null){hook(text);return;}
        // The basic view copies without a guard; a clipboard another process holds must not throw
        // out of a menu click.
        ClipboardRetry.TrySetText(text);
    }
    // The opening fence of the block that holds this line. Walking up parses only lines that look
    // like a fence, and BlockState.Before tells an opener from a closer, so a "```" line inside a
    // "~~~" block never becomes the opener.
    ICSharpCode.AvalonEdit.Document.DocumentLine LiveFenceOpen(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line){
        for(var current=line;current!=null;current=current.PreviousLine){
            char marker;if(LiveFenceLength(document,current,out marker)==0)continue;
            var info=livePreview.Info(current);
            if(info.FenceLine&&info.Before.Fence=='\0')return current;
        }
        return null;
    }
    // The first fence line after the opener closes the block; a block without a closer runs to the
    // end of the document.
    ICSharpCode.AvalonEdit.Document.DocumentLine LiveFenceClose(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine open){
        for(var current=open.NextLine;current!=null;current=current.NextLine){
            char marker;if(LiveFenceLength(document,current,out marker)==0)continue;
            if(livePreview.Info(current).FenceLine)return current;
        }
        return null;
    }
    // The live parse's fence grammar (MarkdownLivePreview.ReadFence) read without building the
    // line: at most three leading spaces, then at least three '`' or '~'. 0 means not fence-like.
    static int LiveFenceLength(ICSharpCode.AvalonEdit.Document.TextDocument document,ICSharpCode.AvalonEdit.Document.DocumentLine line,out char marker){
        marker='\0';
        int at=line.Offset,end=line.EndOffset;
        while(at<end&&at-line.Offset<4&&document.GetCharAt(at)==' ')at++;
        if(at-line.Offset>3||at>=end)return 0;
        char c=document.GetCharAt(at);
        if(c!='`'&&c!='~')return 0;
        int begin=at;
        while(at<end&&document.GetCharAt(at)==c)at++;
        int length=at-begin;
        if(length<3)return 0;
        marker=c;
        return length;
    }
    // Set before WPF opens the menu (right-button down) and again when it is about to open, so the
    // item follows the pointer and the keyboard path cannot show a stale menu.
    void HookLiveCodeMenu(ICSharpCode.AvalonEdit.Editing.TextArea area){
        if(area==null)return;
        area.PreviewMouseRightButtonDown-=LiveCodeMenuPointer;
        area.PreviewMouseRightButtonDown+=LiveCodeMenuPointer;
        area.ContextMenuOpening-=LiveCodeMenuOpening;
        area.ContextMenuOpening+=LiveCodeMenuOpening;
        // The live attach is the one place this file is told about the surface, so the table cut
        // rule rides along with the code menu hooks (one preview-executed handler, removed again
        // on detach).
        HookLiveTableCut(area);
        /* ApplicationCommands.Paste in the basic view (menu, API) takes the same path as Ctrl+V: LivePaste, so the markers a selection cut off are restored and table cells paste alike. */
        if(livePreview!=null&&Object.ReferenceEquals(source==null?null:source.TextArea,area))livePreview.HostPaste=delegate{return PasteClipboard(false);};
    }
    void UnhookLiveCodeMenu(SourceEditor owner){
        if(owner==null||owner.TextArea==null)return;
        owner.TextArea.PreviewMouseRightButtonDown-=LiveCodeMenuPointer;
        owner.TextArea.ContextMenuOpening-=LiveCodeMenuOpening;
        UnhookLiveTableCut(owner);
        if(Object.ReferenceEquals(owner.TextArea.ContextMenu,liveCodeMenu))owner.TextArea.ContextMenu=null;
        liveCodeMenu=null;
    }
    // Stage 4 cut: a selection that reaches across cells keeps the "|" delimiters (the basic
    // view's WPF table cannot lose a cell on cut either). The copied payload is the same
    // MarkdownClipboard projection Ctrl+C writes; only the selected text inside the cells goes.
    // Probes capture the DataObject through LiveTableCutHook instead of the real clipboard.
    internal static Action<System.Windows.DataObject> LiveTableCutHook;
    System.Windows.Input.ExecutedRoutedEventHandler liveTableCutHandler;
    void HookLiveTableCut(ICSharpCode.AvalonEdit.Editing.TextArea area){
        if(area==null)return;
        if(liveTableCutHandler==null)liveTableCutHandler=new System.Windows.Input.ExecutedRoutedEventHandler(LiveTableCut);
        System.Windows.Input.CommandManager.RemovePreviewExecutedHandler(area,liveTableCutHandler);
        System.Windows.Input.CommandManager.AddPreviewExecutedHandler(area,liveTableCutHandler);
    }
    void UnhookLiveTableCut(SourceEditor owner){
        if(owner==null||owner.TextArea==null||liveTableCutHandler==null)return;
        System.Windows.Input.CommandManager.RemovePreviewExecutedHandler(owner.TextArea,liveTableCutHandler);
    }
    void LiveTableCut(object sender,System.Windows.Input.ExecutedRoutedEventArgs e){
        if(e.Handled||source==null||!Object.ReferenceEquals(sender,source.TextArea))return;
        if(e.Command!=System.Windows.Input.ApplicationCommands.Cut)return;
        if(!LiveFormat)return;
        int start=source.SelectionStart,length=source.SelectionLength;
        if(length<=0)return;
        // A selection that covers whole tables is an ordinary text cut: the editor's own cut
        // copies and removes those lines, pipes included.
        if(MarkdownTableCommands.SelectionCoversWholeTables(source.Document,start,length))return;
        // A selection that reaches the delimiter row is structure, the same rule Paste and the
        // delete keys follow: the cut must not clear the header cells around it, and nothing goes
        // to the clipboard when no text may be removed.
        if(SelectionTouchesDelimiter()){e.Handled=true;return;}
        var cells=SourceRowCells(start);
        if(cells==null||!MarkdownTableCommands.SpansCellBoundary(source.Document,start,length,cells))return;
        CommandResult clear=MarkdownTableCommands.ClearCellSelection(source.Document,start,length,cells);
        if(clear==null)return;
        e.Handled=true;
        var data=new System.Windows.DataObject();
        SourceCopying(source.TextArea,new System.Windows.DataObjectCopyingEventArgs(data,false));
        Action<System.Windows.DataObject> hook=LiveTableCutHook;
        if(hook!=null)hook(data);
        else{
            // A clipboard another process holds must not turn a cut into a silent delete.
            System.Windows.DataObject cut=data;
            if(!ClipboardRetry.Try(delegate{System.Windows.Clipboard.SetDataObject(cut,true);})){ShowStatusNotice(T("클립보드를 열 수 없습니다. 잠시 후 다시 복사하세요."));return;}
        }
        ApplyCommandResult(clear);
        source.Focus();
    }
    void LiveCodeMenuPointer(object sender,System.Windows.Input.MouseButtonEventArgs e){
        if(source==null||!Object.ReferenceEquals(sender,source.TextArea))return;
        PrepareLiveCodeMenu(-1);
    }
    void LiveCodeMenuOpening(object sender,System.Windows.Controls.ContextMenuEventArgs e){
        if(source==null||!Object.ReferenceEquals(sender,source.TextArea))return;
        PrepareLiveCodeMenu(e.CursorLeft<0?source.CaretIndex:-1);
    }
    // A negative offset asks for the pointer position. Only the menu this window installed is ever
    // cleared, so another owner's menu on the same TextArea is left alone.
    void PrepareLiveCodeMenu(int offset){
        var area=source==null?null:source.TextArea;
        if(area==null)return;
        System.Windows.Controls.ContextMenu menu=null;
        if(LivePreviewAttached&&source.Visibility==System.Windows.Visibility.Visible){
            int at=offset;
            if(at<0){try{at=source.GetCharacterIndexFromPoint(System.Windows.Input.Mouse.GetPosition(source),true);}catch(InvalidOperationException){at=-1;}}
            if(at>=0)menu=LiveCodeMenuAt(at);
        }
        if(menu==null){
            if(Object.ReferenceEquals(area.ContextMenu,liveCodeMenu)){area.ContextMenu=null;liveCodeMenu=null;}
            return;
        }
        liveCodeMenu=menu;area.ContextMenu=menu;
    }
}
}
