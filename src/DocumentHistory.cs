using System;
using System.Collections.Generic;
using ICSharpCode.AvalonEdit.Document;
namespace MDNote {
internal sealed class DocumentHistory {
    internal sealed class MismatchException:InvalidOperationException {internal MismatchException(string message):base(message){MismatchCount++;LastMismatch=message;}}
    // Diagnostic counters for the mode-switch abuse probe (they change nothing in the product):
    // how many MismatchExceptions were built, the last message, and how many times the window
    // answered one with ResetMismatchedHistory (which clears both stacks).
    internal static int MismatchCount,ResetCount;
    internal static string LastMismatch;
    // One line describing the entry on top of the undo (or redo) stack, for probe failure reports.
    internal string DiagTop(bool redoStack){
        var stack=redoStack?redo:undo;if(stack.Count==0)return "empty";var edit=stack[stack.Count-1];
        string kind=edit.Parts!=null?"parts"+edit.Parts.Count:(edit.BeforeMode.HasValue||edit.AfterMode.HasValue?"mode":"delta");
        return "n="+stack.Count+" "+kind+" at="+edit.At+" rem="+(edit.Removed==null?-1:edit.Removed.Length)+" ins="+(edit.Inserted==null?-1:edit.Inserted.Length)
            +" bm="+(edit.BeforeMode.HasValue?(edit.BeforeMode.Value?"1":"0"):"-")+" am="+(edit.AfterMode.HasValue?(edit.AfterMode.Value?"1":"0"):"-")
            +(edit.Source?" src":"")+(edit.Typed?" typed":"")+(edit.Atomic?" atomic":"")+(edit.Backspace?" bksp":"");
    }
    // Before/AfterSel are the caret or selection the action started and ended with; an undo puts
    // both back. Source marks an entry recorded from the source editor, Typed one that came from
    // continuous text input (a plain character or any IME commit). The whole run is one undo step,
    // so it keeps merging after its first character replaced a selection; a caret move, Delete,
    // paste/cut/drop, a command, focus loss, a document switch or a table block replace starts a
    // new entry. A Backspace after typing is a step of its own, the way Windows Notepad and Word
    // behave: the characters it deleted come back on the next undo, and consecutive Backspaces
    // group among themselves. A replacement it makes (the host moving its hidden marker run back
    // out around a closing-edge space) is that key's own action too.
    // TypedEnd is where the run's next typed character belongs: right after the text typed last.
    // A pending-style pair writes its closing markers together with the first character, so that
    // point sits inside the entry's inserted text; the host selects it before the update closes,
    // so the group records it as the caret the run ended with.
    internal sealed class Edit {internal bool? BeforeMode,AfterMode;internal int At;internal string Removed,Inserted;internal List<Edit> Parts;internal int BeforeCaret=-1,AfterCaret=-1;internal int BeforeSelStart=-1,BeforeSelLength,AfterSelStart=-1,AfterSelLength,TypedEnd=-1;internal bool Source,Typed,Atomic,Backspace;internal long Bytes {get{if(Parts==null)return ((long)Removed.Length+Inserted.Length)*2L;long result=0;foreach(var part in Parts)result+=part.Bytes;return result;}}}
    readonly List<Edit> undo=new List<Edit>(),redo=new List<Edit>();long bytes;
    // One user action can change the document more than once inside TextDocument.RunUpdate: a
    // multi-line Tab indent, a selection replaced through several deletable segments, a newline
    // plus its indentation. AvalonEdit groups those in its own undo stack, which this app
    // disables, so the source editor reports the update here and all of its changes become one
    // entry. The group tracks the union of the changes as one span of the current text plus the
    // text that span held before the update; replacing the span with Original restores the exact
    // pre-update document. The first change is reported from DocumentDelta (its offsets are
    // already post-change); later ones are measured in Document.Changing, where the document
    // still holds the text the change replaces, so only the change and its gap are ever read.
    // The first change also pushes its own entry right away, exactly as a single-change update
    // always did: EndUpdate raises TextChanged before UpdateFinished, so the title and toolbar
    // already read CanUndo/CanRedo there. EndEditGroup replaces that provisional entry with one
    // spanning the whole update when more changes arrived.
    internal sealed class EditGroup {internal DocumentHistory Owner;internal ITextSource Document;internal int Depth;internal int BeforeStart=-1,BeforeLength,AfterStart=-1,AfterLength;internal bool Typed,Separate,Disjoint,MergedTyping,BackspaceKey;internal int Changes,RemovedChanges,InsertedChanges;internal int SpanStart,SpanEnd;internal string Original;internal List<string> OriginalFront,OriginalBack;internal Edit Provisional;internal int ProvisionalMerge,ProvisionalAt,ProvisionalIndex=-1,ProvisionalTypedEnd=-1;internal string ProvisionalRemoved,ProvisionalInserted;internal List<Edit> ProvisionalRedo,ProvisionalEvicted;}
    static EditGroup group;
    internal static void BeginEditGroup(ITextSource document,int beforeStart,int beforeLength,bool typed){BeginEditGroup(document,beforeStart,beforeLength,typed,false,false);}
    // Separate: the user moved the caret or the editor lost keyboard focus since the last input,
    // so this update must not fold into the run before it even when it lands on the same offset.
    internal static void BeginEditGroup(ITextSource document,int beforeStart,int beforeLength,bool typed,bool separate){BeginEditGroup(document,beforeStart,beforeLength,typed,separate,false);}
    // BackspaceKey: the update is the Backspace command's own edit. It never joins the typing run
    // (Coalesce starts a deletion entry of its own), and neither does the replacement it makes.
    // Folding that replacement made the entry it restored a no-op, EndEditGroup dropped the entry,
    // and one Ctrl+Z then skipped both the typed character and the Backspace (x8).
    internal static void BeginEditGroup(ITextSource document,int beforeStart,int beforeLength,bool typed,bool separate,bool backspaceKey){
        EditGroup current=group;
        if(current==null){current=new EditGroup();group=current;}
        // An update never spans documents: a group still open for another document belongs to an
        // update that never finished (its EndUpdate was lost, so UpdateStarted/UpdateFinished stop
        // pairing and EndEditGroup would return forever). Start over on this document instead of
        // folding its offsets into the stale span.
        if(current.Depth>0&&current.Document!=null&&!Object.ReferenceEquals(current.Document,document))current.Depth=0;
        if(current.Depth==0){current.Depth=0;current.Owner=null;current.Document=document;current.BeforeStart=beforeStart;current.BeforeLength=beforeLength;current.AfterStart=-1;current.AfterLength=0;current.Typed=typed;current.Separate=separate;current.BackspaceKey=backspaceKey;current.Disjoint=false;current.MergedTyping=false;current.Changes=0;current.RemovedChanges=0;current.InsertedChanges=0;current.SpanStart=0;current.SpanEnd=0;current.Original=null;current.OriginalFront=null;current.OriginalBack=null;current.Provisional=null;current.ProvisionalMerge=0;current.ProvisionalAt=0;current.ProvisionalIndex=-1;current.ProvisionalTypedEnd=-1;current.ProvisionalRemoved=null;current.ProvisionalInserted=null;current.ProvisionalRedo=null;current.ProvisionalEvicted=null;}
        current.Depth++;
    }
    // Runs inside TextDocument.Changing, before the change is applied. Any region the span did not
    // cover yet is still untouched by the update, so its current text is its original text: the
    // gap between the span and the new change can be read in O(gap) without scanning the document.
    // A change that overlaps the span only widens it (left/right gaps); the pre-group text it
    // replaces is already inside Original, and removed inserted text correctly is not.
    // Gap text is collected as pieces, not concatenated per change: a replace-all or a multi-line
    // Tab applies thousands of changes inside one update, and string concatenation made that
    // O(changes x span). A prepend extends the left edge, so its pieces are kept newest-first and
    // reversed when the text is joined once in EndEditGroup; an append extends the right edge and
    // keeps its order. Each piece is still read only from the gap it covers.
    static void NoteGroupGap(EditGroup current,string left,string right){
        if(!String.IsNullOrEmpty(left)){if(current.OriginalFront==null)current.OriginalFront=new List<string>();current.OriginalFront.Add(left);}
        if(!String.IsNullOrEmpty(right)){if(current.OriginalBack==null)current.OriginalBack=new List<string>();current.OriginalBack.Add(right);}
    }
    // The pre-update text of the group's span, joined once per update (EndEditGroup is the only
    // caller). The first change's removed text is already in Original, so the pieces go around it.
    static string GroupOriginal(EditGroup current){
        string middle=current.Original==null?"":current.Original;
        List<string> front=current.OriginalFront,back=current.OriginalBack;
        if(front==null&&back==null)return middle;
        int length=middle.Length;
        if(front!=null)foreach(string piece in front)length+=piece.Length;
        if(back!=null)foreach(string piece in back)length+=piece.Length;
        var builder=new System.Text.StringBuilder(length);
        if(front!=null)for(int i=front.Count-1;i>=0;i--)builder.Append(front[i]);
        builder.Append(middle);
        if(back!=null)foreach(string piece in back)builder.Append(piece);
        current.Original=builder.ToString();current.OriginalFront=null;current.OriginalBack=null;
        return current.Original;
    }
    internal static void BeforeDocumentChange(ITextSource document,int at,int removalLength,int insertedLength){
        EditGroup current=group;
        if(current==null||current.Depth<=0||current.Changes<=0||at<0||removalLength<0||insertedLength<0)return;
        if(!Object.ReferenceEquals(current.Document,document))return;
        if((long)at+(long)removalLength>(long)document.TextLength)return;
        // A block replace writes the typed key between the removed cells; a multi-range delete
        // (a hidden marker pair) only removes, and the seal below must not touch that key's run.
        // The counters describe that shape without reading any text: the seal needs two or more
        // removed cells and exactly the one written key.
        if(removalLength>0)current.RemovedChanges++;
        if(insertedLength>0)current.InsertedChanges++;
        int start=current.SpanStart,end=current.SpanEnd;
        if(at>=end){
            // An insert exactly at the end, or any change right of the span, is still original text.
            // Untouched text between the span and the change means the update rewrote separated
            // places (a table block's cells); EndEditGroup seals such a typed update below.
            if(at>end)current.Disjoint=true;
            NoteGroupGap(current,null,document.GetText(end,at+removalLength-end));
            current.SpanEnd=at+insertedLength;
        }else if(at+removalLength<=start){
            if(at+removalLength<start)current.Disjoint=true;
            NoteGroupGap(current,document.GetText(at,start-at),null);
            current.SpanStart=at;current.SpanEnd=end+insertedLength-removalLength;
        }else{
            NoteGroupGap(current,at<start?document.GetText(at,start-at):"",at+removalLength>end?document.GetText(end,at+removalLength-end):"");
            if(at<start)current.SpanStart=at;
            current.SpanEnd=end<=at+removalLength?at+insertedLength:end+insertedLength-removalLength;
        }
        current.Changes++;
    }
    internal static void EndEditGroup(int afterStart,int afterLength){
        EditGroup current=group;if(current==null)return;
        if(current.Depth>0)current.Depth--;
        if(current.Depth>0)return;
        group=null;
        DocumentHistory owner=current.Owner;
        if(owner==null||current.Changes<=0||current.Document==null)return;
        int at=current.SpanStart;long length=(long)current.SpanEnd-at;long total=current.Document.TextLength;
        if(at<0||length<0||at>total-length)return;
        string inserted=current.Document.GetText(at,(int)length),original=GroupOriginal(current);
        // A typed update that rewrote separated spans is a block replace (typing over a table
        // selection clears each selected cell and writes the key between the rest). It ends the
        // run: it never folds into the run before it, and Typed is cleared below so the next
        // character or Backspace starts a new step instead of growing its entry. Only that shape
        // is sealed: two or more removed cells (one change each) and exactly one written key. A
        // multi-range delete (a hidden marker pair) separates its spans too but inserts nothing;
        // an ordinary typed replacement of one span (a cell, padding, a marker run) removes once,
        // and a rectangle selection's input inserts once per line, so both stay typing entries.
        bool blockReplace=current.Typed&&current.Disjoint&&current.RemovedChanges>=2&&current.InsertedChanges==1&&original.Length>0&&inserted.Length>0;
        Edit entry;
        if(current.Changes==1){
            // One change is the whole update: the entry it pushed is final, and only this case may
            // still merge with a typing run. Nothing in the stack moves, so CanUndo/CanRedo stay
            // as the TextChanged already reported them. A typed removal (a Backspace) is not a
            // typing entry: the run stays whole, and the next character or Backspace must start or
            // grow a deletion step instead of joining it.
            entry=current.Provisional;
            if(entry==null||inserted==original){owner.DropProvisional(current);return;}
            // A Backspace's own replacement and an Enter (its text holds the newline, often followed
            // by indentation, so the run-ends-with-newline guard in Coalesce misses it) are not
            // typing entries either: the next character must start its own step (web Argon review).
            if(current.ProvisionalMerge==0)entry.Typed=current.Typed&&inserted.Length>0&&!current.BackspaceKey&&inserted.IndexOf('\n')<0&&inserted.IndexOf('\r')<0;
        }else{
            // The update ended where it started (an insert taken back, for example) and had no net
            // effect; otherwise all of its changes become one entry over their union span.
            owner.DropProvisional(current);
            if(inserted==original)return;
            // A typed update that only rewrote the run's own text (or inserted contiguous text at
            // its typed end) joins that run instead of starting a step of its own. A caret the user
            // moved since the last input keeps its own step, exactly as it does for one change.
            entry=current.Typed&&!current.Separate&&!blockReplace?owner.FoldTypedGroup(at,original,inserted,current.BackspaceKey):null;
            if(entry!=null)current.MergedTyping=true;
            else{
                // A typed update may touch the document several times (a table cell block, a
                // table's separator gap). A contiguous one is still one typing entry, so the
                // characters typed after it must be able to grow the same step; Atomic keeps
                // commands from merging. Clearing Typed seals the block replace, and a union with
                // no typed text (a Backspace's removal or marker move) stays out of the run too:
                // Coalesce's Atomic rule then keeps the next Backspace or character in a new
                // step, and typing after a Backspace starts a new entry of its own.
                entry=new Edit {At=at,Removed=original,Inserted=inserted,Atomic=true,Typed=current.Typed&&!blockReplace&&inserted.Length>0&&!current.BackspaceKey&&inserted.IndexOf('\n')<0&&inserted.IndexOf('\r')<0};
                owner.Add(entry);
            }
        }
        if(owner.undo.Count>0&&Object.ReferenceEquals(owner.undo[owner.undo.Count-1],entry)){
            // An entry whose original and inserted text are equal changes nothing: undoing it
            // would validate, replace the same text and still consume a step. A typed replacement
            // that puts the run's own original text back ends here; the run and this update cancel
            // out, so the entry goes. A Backspace no longer reaches this point: it deletes into a
            // step of its own, so its undo always changes the text. The monkey's undo oracle saw
            // three such replacement steps in a row and stopped there. Nothing else can be lost
            // with it: a merge needs an empty redo branch, and a single no-op change was taken
            // back above before its provisional entry could stay (DropProvisional, which also
            // restores the redo stack).
            if(entry.Parts==null&&!entry.BeforeMode.HasValue&&!entry.AfterMode.HasValue&&String.Equals(entry.Removed,entry.Inserted,StringComparison.Ordinal)){
                owner.bytes-=entry.Bytes;owner.undo.RemoveAt(owner.undo.Count-1);return;
            }
            entry.Source=true;
            // The first change of the action keeps the selection it started from; a typing run that
            // merged into an older entry keeps that entry's start.
            if(entry.BeforeSelStart<0){entry.BeforeSelStart=current.BeforeStart;entry.BeforeSelLength=current.BeforeLength;}
            entry.AfterSelStart=afterStart;entry.AfterSelLength=afterLength;
            // A run that starts here continues at the spot the host left the caret on before the
            // update closes: inside a pending style's markers. An entry that replaced a selection
            // records that spot too, so a typed character landing there joins the same step; a
            // host that left the caret outside the inserted text falls back to its end. A merge
            // that moved the run's own text (merge 5) and a folded multi-change group take the
            // host's spot the same way: its caret, not the shifted old typed end, is where the
            // next character is typed.
            if((current.ProvisionalMerge==0||current.ProvisionalMerge==5||current.MergedTyping)&&entry.Typed){
                int typedEnd=afterStart;
                if(typedEnd<entry.At||typedEnd>entry.At+entry.Inserted.Length)typedEnd=entry.At+entry.Inserted.Length;
                entry.TypedEnd=typedEnd;
            }
        }
    }
    // Takes back the entry the first change of an update pushed, undoing the typing merge when that
    // change folded into the previous entry, so a multi-change update can put its own entry there
    // instead (or leave none when the update ended where it started).
    void DropProvisional(EditGroup current){
        Edit provisional=current.Provisional;int merge=current.ProvisionalMerge;
        current.Provisional=null;current.ProvisionalMerge=0;
        if(provisional==null)return;
        if(merge==0){
            // Add() trims from the front and nothing else is pushed inside the same update, so the
            // entry is still the newest one.
            if(undo.Count>0&&Object.ReferenceEquals(undo[undo.Count-1],provisional)){
                bytes-=provisional.Bytes;undo.RemoveAt(undo.Count-1);
                // The update may have had no net change after all: Add() also dropped the redo
                // stack (and trimmed the oldest entries at the cap), so put both back.
                if(current.ProvisionalEvicted!=null){undo.InsertRange(0,current.ProvisionalEvicted);foreach(var lost in current.ProvisionalEvicted)bytes+=lost.Bytes;}
                if(current.ProvisionalRedo!=null){foreach(var lost in current.ProvisionalRedo)bytes+=lost.Bytes;redo.AddRange(current.ProvisionalRedo);}
            }
            current.ProvisionalRedo=null;current.ProvisionalEvicted=null;
            return;
        }
        string removed=current.ProvisionalRemoved==null?"":current.ProvisionalRemoved,inserted=current.ProvisionalInserted==null?"":current.ProvisionalInserted;
        if(merge==1){provisional.Inserted=provisional.Inserted.Substring(0,provisional.Inserted.Length-inserted.Length);provisional.TypedEnd=current.ProvisionalTypedEnd;bytes-=inserted.Length*2L;}
        else if(merge==2){provisional.Removed=provisional.Removed.Substring(removed.Length);provisional.At=current.ProvisionalAt+removed.Length;provisional.TypedEnd=current.ProvisionalTypedEnd;bytes-=removed.Length*2L;}
        else if(merge==4){
            // A typed character spliced into the middle of the run's own text: take it out of the
            // same spot again, so the entry is exactly what the document held before the update.
            int index=current.ProvisionalIndex;
            if(index<0||index+inserted.Length>provisional.Inserted.Length)index=provisional.Inserted.Length-inserted.Length;
            if(index<0)index=0;
            provisional.Inserted=provisional.Inserted.Substring(0,index)+provisional.Inserted.Substring(index+inserted.Length);
            provisional.TypedEnd=current.ProvisionalTypedEnd;bytes-=inserted.Length*2L;
        }
        else if(merge==5){
            // A typed replacement inside the run's own text: put the replaced text back where it
            // was removed from, so the entry is exactly what the document held before the update.
            int index=current.ProvisionalIndex;
            if(index<0||index+inserted.Length>provisional.Inserted.Length)index=provisional.Inserted.Length-inserted.Length;
            if(index<0)index=0;
            provisional.Inserted=provisional.Inserted.Substring(0,index)+removed+provisional.Inserted.Substring(index+inserted.Length);
            provisional.TypedEnd=current.ProvisionalTypedEnd;bytes+=(removed.Length-inserted.Length)*2L;
        }
        else{provisional.Removed=provisional.Removed.Substring(0,provisional.Removed.Length-removed.Length);bytes-=removed.Length*2L;}
    }
    internal bool CanUndo {get{return undo.Count>0;}}
    internal bool CanRedo {get{return redo.Count>0;}}
    // Stack sizes for the probes: a mode-only entry (text mode toggle) leaves the document text alone, so a step is only "stalled" when it also left these unchanged.
    internal int UndoDepth {get{return undo.Count;}}
    internal int RedoDepth {get{return redo.Count;}}
    // Reading mode on a Markdown body edits only through the live task boxes, so its undo and redo
    // may replay exactly one of those ticks and nothing else. A tick changes the single character
    // between the brackets of a task box between blank and x, and the '[' and ']' are still that
    // character's neighbours in the current text, so the check is O(1) and needs no marker on the
    // entry: typing, a command or a rich edit never has that shape. The entry keeps it when the
    // undo moves it to the other stack, so redo is decided the same way. TXT never asks.
    internal bool ReadingTick(bool redo,ITextSource document){
        var stack=redo?this.redo:undo;if(document==null||stack.Count==0)return false;var edit=stack[stack.Count-1];
        if(edit.Parts!=null||edit.BeforeMode.HasValue||edit.AfterMode.HasValue||edit.Removed==null||edit.Inserted==null)return false;
        bool tick=((edit.Removed=="x"||edit.Removed=="X")&&edit.Inserted==" ")||(edit.Removed==" "&&(edit.Inserted=="x"||edit.Inserted=="X"));
        if(!tick||edit.At<1||edit.At+1>=document.TextLength)return false;
        return document.GetCharAt(edit.At-1)=='['&&document.GetCharAt(edit.At+1)==']';
    }
    // A clear means the caller starts a fresh history, so a group left open here must not keep
    // Depth above zero: UpdateFinished only fires when the document's own update count returns to
    // zero, and a stale Depth made EndEditGroup return on every later update, which stopped the
    // history from recording anything at all. Dropping the group lets the next change record on
    // its own, exactly like a change outside an update.
    internal void Clear(){undo.Clear();redo.Clear();bytes=0;EditGroup current=group;if(current!=null&&Object.ReferenceEquals(current.Owner,this)){current.Owner=null;current.Depth=0;current.Document=null;current.Changes=0;current.RemovedChanges=0;current.InsertedChanges=0;current.MergedTyping=false;current.BackspaceKey=false;current.Original=null;current.OriginalFront=null;current.OriginalBack=null;current.Provisional=null;current.ProvisionalMerge=0;current.ProvisionalRedo=null;current.ProvisionalEvicted=null;if(Object.ReferenceEquals(group,current))group=null;}}
    internal bool? UndoMode {get{return CanUndo?undo[undo.Count-1].BeforeMode:null;}}
    internal bool? RedoMode {get{return CanRedo?redo[redo.Count-1].AfterMode:null;}}
    void ClearRedo(){foreach(var edit in redo)bytes-=edit.Bytes;redo.Clear();}
    void Add(Edit edit){Add(edit,null);}
    // A provisional push (the first change of an update) must not lose what Add removes: the
    // redo stack and the oldest entries trimmed at the cap are put back by DropProvisional when
    // the update turns out to hold no net change.
    void Add(Edit edit,EditGroup provisional){
        if(provisional!=null)provisional.ProvisionalRedo=redo.Count==0?null:new List<Edit>(redo);
        ClearRedo();undo.Add(edit);bytes+=edit.Bytes;
        while(undo.Count>1&&(undo.Count>512||bytes>8*1024*1024)){if(provisional!=null){if(provisional.ProvisionalEvicted==null)provisional.ProvisionalEvicted=new List<Edit>();provisional.ProvisionalEvicted.Add(undo[0]);}bytes-=undo[0].Bytes;undo.RemoveAt(0);}
    }
    internal void RecordChanges(IList<Edit> changes,int beforeCaret=-1,int afterCaret=-1,int beforeSelectionStart=-1,int beforeSelectionLength=0,int afterSelectionStart=-1,int afterSelectionLength=0){
        if(changes==null)throw new ArgumentNullException("changes");var parts=new List<Edit>();
        foreach(var part in changes){if(part==null||part.At<0||part.Removed==null||part.Inserted==null||part.Parts!=null||part.BeforeMode.HasValue||part.AfterMode.HasValue)throw new ArgumentException("Invalid compound history part");if(part.Removed!=part.Inserted)parts.Add(new Edit {At=part.At,Removed=part.Removed,Inserted=part.Inserted});}
        if(parts.Count==0)return;parts.Sort(delegate(Edit a,Edit b){return a.At.CompareTo(b.At);});
        for(int i=1;i<parts.Count;i++)if(parts[i].At==parts[i-1].At||(long)parts[i-1].At+parts[i-1].Removed.Length>parts[i].At)throw new ArgumentException("Compound history parts overlap");
        Add(new Edit {At=parts[0].At,Removed="",Inserted="",Parts=parts,BeforeCaret=beforeCaret,AfterCaret=afterCaret,BeforeSelStart=beforeSelectionStart,BeforeSelLength=beforeSelectionLength,AfterSelStart=afterSelectionStart,AfterSelLength=afterSelectionLength,Source=beforeSelectionStart>=0});
    }
    static string ApplyParts(string value,Edit edit,bool forward){
        if(edit.Parts==null){string expected=forward?edit.Removed:edit.Inserted;if(edit.At<0||edit.At>value.Length-expected.Length||String.CompareOrdinal(value,edit.At,expected,0,expected.Length)!=0)throw new MismatchException("History does not match its source");return value.Remove(edit.At,expected.Length).Insert(edit.At,forward?edit.Inserted:edit.Removed);}
        long shift=0;foreach(var part in edit.Parts){long at=part.At+(forward?0:shift);string expected=forward?part.Removed:part.Inserted;if(at<0||at>value.Length-expected.Length||String.CompareOrdinal(value,(int)at,expected,0,expected.Length)!=0)throw new MismatchException("Compound history does not match its source");shift+=part.Inserted.Length-part.Removed.Length;}
        long length=value.Length+(forward?shift:-shift);if(length<0||length>Int32.MaxValue)throw new InvalidOperationException("History result exceeds the text limit");
        var result=new System.Text.StringBuilder((int)length);int cursor=0;shift=0;
        foreach(var part in edit.Parts){int at=(int)(part.At+(forward?0:shift));string expected=forward?part.Removed:part.Inserted;result.Append(value,cursor,at-cursor).Append(forward?part.Inserted:part.Removed);cursor=at+expected.Length;shift+=part.Inserted.Length-part.Removed.Length;}
        result.Append(value,cursor,value.Length-cursor);return result.ToString();
    }
    internal void Record(string before,string after,bool? beforeMode=null,bool? afterMode=null){if(before==after&&beforeMode==afterMode)return;int prefix=0,limit=Math.Min(before.Length,after.Length);while(prefix<limit&&before[prefix]==after[prefix])prefix++;int suffix=0;while(suffix<limit-prefix&&before[before.Length-1-suffix]==after[after.Length-1-suffix])suffix++;var edit=new Edit {BeforeMode=beforeMode,AfterMode=afterMode,At=prefix,Removed=before.Substring(prefix,before.Length-prefix-suffix),Inserted=after.Substring(prefix,after.Length-prefix-suffix)};Add(edit);}
    internal void RecordDelta(int at,string removed,string inserted){
        if(at<0||removed==null||inserted==null)throw new ArgumentException("Invalid history delta");if(removed==inserted)return;
        EditGroup current=group;
        if(current!=null&&current.Depth>0){
            if(current.Owner==null)current.Owner=this;
            if(Object.ReferenceEquals(current.Owner,this)){
                // The first recorded change starts the span (its offsets are already post-change)
                // and is pushed at once, so the TextChanged that EndUpdate raises before
                // UpdateFinished already sees CanUndo. Later changes were folded into the span by
                // BeforeDocumentChange before they were applied; EndEditGroup replaces the pushed
                // entry with one over the whole span then.
                if(current.Changes<=0){
                    if(removed.Length>0)current.RemovedChanges++;
                    if(inserted.Length>0)current.InsertedChanges++;
                    current.Changes=1;current.SpanStart=at;current.SpanEnd=at+inserted.Length;current.Original=removed;
                    current.ProvisionalAt=at;current.ProvisionalRemoved=removed;current.ProvisionalInserted=inserted;
                    current.Provisional=RecordDeltaCore(at,removed,inserted,current,out current.ProvisionalMerge);
                }
                return;
            }
        }
        int merge;RecordDeltaCore(at,removed,inserted,null,out merge);
    }
    // Pushes one change now and reports how it merged into the newest entry: 0 for a new entry,
    // 1 for an appended insert, 2 for a prepended delete, 3 for an appended delete, 4 for an insert
    // spliced at the run's own typed end inside its pending-style pair, 5 for a replacement of text
    // the run itself inserted. A Backspace never merges into a typing run (see Coalesce).
    // EndEditGroup reverses that merge (DropProvisional) when the update turns out to hold more than
    // one change.
    Edit RecordDeltaCore(int at,string removed,string inserted,EditGroup provisional,out int merge){
        merge=0;int typedEnd=-1;
        bool typed=provisional!=null&&provisional.Typed;
        bool backspace=provisional!=null&&provisional.BackspaceKey;
        if(redo.Count==0&&undo.Count>0){
            Edit last=undo[undo.Count-1];
            if(provisional!=null)typedEnd=last.TypedEnd;
            if(provisional!=null&&!provisional.Separate)merge=Coalesce(last,at,removed,inserted,typed,backspace);
        }
        if(merge!=0){
            // The splice's/trim's own position inside the entry's inserted text, and the typed end
            // the merge moved, for DropProvisional.
            if(provisional!=null){if(merge==4||merge==5)provisional.ProvisionalIndex=at-undo[undo.Count-1].At;provisional.ProvisionalTypedEnd=typedEnd;}
            return undo[undo.Count-1];
        }
        // Backspace marks an entry the Backspace key started (a typed removal). Consecutive
        // Backspaces group with it; a cut or a command that deleted text stays its own step, exactly
        // as it did before the key stopped folding into typing runs.
        var edit=new Edit {At=at,Removed=removed,Inserted=inserted,Backspace=typed&&inserted.Length==0&&removed.Length>0};Add(edit,provisional);return edit;
    }
    // Typing coalescing: one continuous text input run is one undo step. A typed insert (an IME
    // clause included) appends to the run until the run holds 1024 characters (the cap below keeps
    // one key's copy cost bounded), and a run whose first character replaced a selection keeps
    // merging after it. The run never folds into a paste, a command or an entry recorded outside a
    // typing update (the incoming update must be typed too); nothing merges while redo is
    // available, and an update that holds several changes keeps no merge (its first change may
    // fold here and DropProvisional takes it back). An entry for a table block replace is Atomic
    // without Typed, so the first guard keeps the next Backspace or character out of that step. A
    // newline typed into the document still starts a new entry: Enter is a structural key. Delete
    // is an untyped update and never joins a typing run.
    // A Backspace after typing never touches the run: the characters it deleted must come back on
    // the next undo, the way Windows Notepad and Word do it, so the key starts a deletion entry of
    // its own and the typing after it becomes another step. Consecutive Backspaces then grow that
    // entry through the contiguous-deletion branch below, the same grouping Backspace over text
    // that was already there has always had; a typing entry is never grown by them.
    // A replacement a Backspace makes is the key's own action too: the host moves its hidden marker
    // run back out around a closing-edge space, which puts the previous entry's own original text
    // back; folding that made the entry a no-op, EndEditGroup dropped it, and one undo skipped both
    // keys (x8).
    // Return values: 0 none, 1 append insert, 2 prepend delete, 3 append delete, 4 insert inside
    // the run's typed end (a pending-style pair), 5 replace text the run itself inserted (a hidden
    // marker run rearranged by a typed character). A typed removal never returns a merge.
    int Coalesce(Edit last,int at,string removed,string inserted,bool typed,bool backspace){
        if(last.Parts!=null||(last.Atomic&&!last.Typed)||last.BeforeMode.HasValue||last.AfterMode.HasValue)return 0;
        if(typed&&last.Typed){
            int runStart=last.At,runEnd=last.At+last.Inserted.Length;
            // A run stops growing at 1024 characters: an append copies the run's whole inserted
            // text on every key, so the cap keeps one key's cost bounded instead of letting it grow
            // with the run. The next typed character starts a new step, so a very long input
            // undoes in chunks of at most 1024 instead of one huge entry.
            if(last.Inserted.Length>=1024)return 0;
            if(removed.Length==0&&inserted.Length>0&&inserted.IndexOf('\n')<0&&inserted.IndexOf('\r')<0){
                // Text typed on a new line is a new step: the run that ended with Enter does not
                // absorb it (the pending-style splice inside the run still can, below).
                bool endedWithNewline=last.Inserted.Length>0&&(last.Inserted[last.Inserted.Length-1]=='\n'||last.Inserted[last.Inserted.Length-1]=='\r');
                if(at==runEnd&&!endedWithNewline){bytes+=inserted.Length*2L;last.Inserted+=inserted;last.TypedEnd=at+inserted.Length;return 1;}
                // The pending-style pair writes its closing markers together with the first
                // character, so the rest of the run is typed before them. The host records that
                // spot as TypedEnd; only that spot absorbs input, so a caret the user moved into
                // the run starts its own entry. The run's first character counts too.
                if(last.TypedEnd>=0&&at==last.TypedEnd&&at>=runStart&&at<runEnd){
                    int index=at-runStart;bytes+=inserted.Length*2L;last.Inserted=last.Inserted.Insert(index,inserted);last.TypedEnd=at+inserted.Length;return 4;
                }
            }
            // A Backspace after typing is its own undo step, never the run's: the characters it
            // deleted must come back on the next undo (Windows Notepad and Word), so the run stays
            // whole here. The deletion starts an entry the next Backspace grows through the
            // contiguous branch below, and the typing after it starts a new entry because that
            // entry is not typed.
            if(inserted.Length==0&&removed.Length>0)return 0;
            // A typed character that rearranges the host's own hidden markers replaces text the run
            // itself inserted (the closing-edge space moving back inside the pair). A replacement
            // fits none of the merges above, so the key would become its own step; fold it into the
            // run while the whole replaced span is the run's own text. A Backspace's replacement
            // stays its own entry: the same kind of move by that key ("**ab c**" + Backspace ->
            // "**ab** ") restores the run's own original text, so folding would cancel the run's
            // entry and the drop in EndEditGroup would erase both keys from the history at once.
            // An Enter entry (its text holds the newline) never takes the next key's replacement:
            // Enter is a structural key, so a second Enter that rewrites what the first one wrote
            // (an empty list item's marker) is its own step (history-stress seed 1 step 306). The
            // key's own text is checked the same way: an update that writes a newline (Enter over a
            // selection inside the run) is that key, not typed run text, and starts its own entry.
            if(!backspace&&inserted.IndexOf('\n')<0&&inserted.IndexOf('\r')<0&&last.Inserted.IndexOf('\n')<0&&last.Inserted.IndexOf('\r')<0&&SpliceTypedRun(last,at,removed,inserted))return 5;
        }
        // Contiguous Backspace removals group into one entry. A typed Backspace joins only the
        // deletion entry a Backspace started, so a cut or a command that removed text keeps its own
        // step; the raw-document path (history probes, Shift+Tab) keeps its old rule and never grows
        // a Backspace entry. A typing entry is never grown here at all, so Delete still breaks the
        // run, and the typing after a deletion starts an entry of its own.
        bool deletionRun=typed?last.Backspace:!last.Backspace;
        if(!last.Typed&&deletionRun&&inserted.Length==0&&removed.Length>0&&removed.Length<=2&&last.Inserted.Length==0&&last.Removed.Length>0&&last.Removed.Length<256&&removed.IndexOf('\n')<0&&removed.IndexOf('\r')<0){
            if(at+removed.Length==last.At){bytes+=removed.Length*2L;last.Removed=removed+last.Removed;last.At=at;return 2;}
            if(at==last.At){bytes+=removed.Length*2L;last.Removed+=removed;return 3;}
        }
        return 0;
    }
    // Folds a replacement of the run's own text into the run (merge 5). The whole replaced span
    // must lie inside the text this run inserted and hold exactly that text: replacing anything
    // else (text from before the run, or text a command wrote) belongs to its own step, because
    // the run's entry could not put it back. TypedEnd keeps its meaning: the spot the next typed
    // character must land on, shifted with the text when the replacement covers it.
    bool SpliceTypedRun(Edit last,int at,string removed,string inserted){
        if(!last.Typed||removed.Length==0)return false;
        int runStart=last.At,runEnd=last.At+last.Inserted.Length;
        if(at<runStart||at+removed.Length>runEnd)return false;
        int index=at-runStart;
        if(String.CompareOrdinal(last.Inserted,index,removed,0,removed.Length)!=0)return false;
        bytes+=(inserted.Length-removed.Length)*2L;
        last.Inserted=last.Inserted.Substring(0,index)+inserted+last.Inserted.Substring(index+removed.Length);
        int typedEnd=last.TypedEnd;
        if(typedEnd>at){
            if(typedEnd>=at+removed.Length)typedEnd+=inserted.Length-removed.Length;
            else typedEnd=at+inserted.Length;
        }
        last.TypedEnd=typedEnd;
        return true;
    }
    // A typed update that changed the document more than once (a marker run written together with
    // its text) is still one key. A table block replace never reaches here: EndEditGroup keeps it
    // out. Its first change may have folded into the run, and DropProvisional has just taken that
    // back; the union entry is folded here instead of becoming a separate step when the whole span
    // is the run's own text (a replacement) or a contiguous insertion at the spot the run's next
    // typed character belongs to. Untyped updates, a redo history and a run at its 1024-character
    // cap keep their own entry, as they do for a single change; text after a newline the run ended
    // with starts a step (Enter is structural). A Backspace's own multi-change update is never
    // folded for the reason Coalesce gives: the key deletes into a step of its own, and the marker
    // move it makes is one action of that step.
    Edit FoldTypedGroup(int at,string original,string inserted,bool backspace){
        if(redo.Count>0||undo.Count==0||inserted.Length==0)return null;
        // A newline written by the update's own text is Enter: the editor's newline path writes
        // the line break and the new line's indentation as two changes of one typed update
        // (TextArea.ReplaceSelectionWithNewLine and its indentation strategy), so this union span
        // holds both and is that key. Enter is structural, so it starts an entry instead of growing
        // the run before it; the single-change form of the same rule is Coalesce's newline guard on
        // its append and splice merges. A run that ended with a newline is already kept out below;
        // an entry of Enter's own (its text holds the newline, here with the indentation after it)
        // is kept out here, which is why the second of two Enters was folded into the first.
        if(inserted.IndexOf('\n')>=0||inserted.IndexOf('\r')>=0)return null;
        Edit last=undo[undo.Count-1];
        if(last.Parts!=null||!last.Typed||last.BeforeMode.HasValue||last.AfterMode.HasValue)return null;
        if(last.Inserted.Length>=1024)return null;
        if(original.Length>0)return (!backspace&&SpliceTypedRun(last,at,original,inserted))?last:null;
        int runStart=last.At,runEnd=last.At+last.Inserted.Length;
        if(at<runStart||at>runEnd)return null;
        if(at==runEnd){
            if(last.Inserted.Length>0&&(last.Inserted[last.Inserted.Length-1]=='\n'||last.Inserted[last.Inserted.Length-1]=='\r'))return null;
            bytes+=inserted.Length*2L;last.Inserted+=inserted;last.TypedEnd=at+inserted.Length;return last;
        }
        // Inside the run only the typed end absorbs typed text: a caret the user moved into the
        // run starts its own entry even when the update inserted text there.
        if(last.TypedEnd<0||at!=last.TypedEnd)return null;
        bytes+=inserted.Length*2L;last.Inserted=last.Inserted.Insert(at-runStart,inserted);last.TypedEnd=at+inserted.Length;return last;
    }
    internal bool ApplySource(bool forward,SourceEditor editor){
        var from=forward?redo:undo;var to=forward?undo:redo;if(from.Count==0)return false;var edit=from[from.Count-1];if(edit.BeforeMode.HasValue||edit.AfterMode.HasValue)return false;
        if(edit.Parts==null){string expected=forward?edit.Removed:edit.Inserted;int length=expected.Length;string replacement=forward?edit.Inserted:edit.Removed;if(edit.At<0||edit.At>editor.Document.TextLength-length||editor.Document.GetText(edit.At,length)!=expected)throw new MismatchException("History does not match its source");editor.Document.Replace(edit.At,length,replacement);RestoreSourceSelection(editor,forward,edit,edit.At,edit.Removed.Length,edit.Inserted.Length);}
        else{
            long shift=0;foreach(var part in edit.Parts){long at=part.At+(forward?0:shift);string expected=forward?part.Removed:part.Inserted;if(at<0||at>editor.Document.TextLength-expected.Length||editor.Document.GetText((int)at,expected.Length)!=expected)throw new MismatchException("Compound history does not match its source");shift+=part.Inserted.Length-part.Removed.Length;}
            editor.Document.BeginUpdate();try{
                if(forward){for(int i=edit.Parts.Count-1;i>=0;i--){var part=edit.Parts[i];editor.Document.Replace(part.At,part.Removed.Length,part.Inserted);}}
                else foreach(var part in edit.Parts)editor.Document.Replace(part.At,part.Inserted.Length,part.Removed);
            }finally{editor.Document.EndUpdate();}
            RestoreSourceSelection(editor,forward,edit,edit.Parts[0].At,edit.Parts[0].Removed.Length,edit.Parts[0].Inserted.Length);
        }
        from.RemoveAt(from.Count-1);to.Add(edit);return true;
    }
    // An undo/redo puts back the selection the action started or ended with. Entries recorded
    // without one keep the older rule: the caret lands after the change, except that a replaced
    // range is selected again, like other editors.
    // The recorded selection is restored with Select() even when it is empty: setting CaretOffset
    // alone leaves a live selection in place (TextArea only collapses one whose caret moved outside,
    // and does that later on the dispatcher), so an undo would keep the selection of the edit.
    static void RestoreSourceSelection(SourceEditor editor,bool forward,Edit edit,int at,int removedLength,int insertedLength){
        int start=forward?edit.AfterSelStart:edit.BeforeSelStart,length=forward?edit.AfterSelLength:edit.BeforeSelLength;
        if(start>=0){int total=editor.Document.TextLength;if(start>total)start=total;if(length>total-start)length=total-start;editor.Select(start,length);return;}
        int caret=forward?edit.AfterCaret:edit.BeforeCaret;if(caret>=0){editor.CaretIndex=caret;return;}
        if(edit.Source&&edit.Parts==null&&removedLength>0&&insertedLength>0){editor.Select(at,forward?insertedLength:removedLength);return;}
        editor.CaretIndex=at+(forward?insertedLength:removedLength);
    }
    internal string Undo(string value){if(!CanUndo)return value;var edit=undo[undo.Count-1];string result=ApplyParts(value,edit,false);undo.RemoveAt(undo.Count-1);redo.Add(edit);return result;}
    internal string Redo(string value){if(!CanRedo)return value;var edit=redo[redo.Count-1];string result=ApplyParts(value,edit,true);redo.RemoveAt(redo.Count-1);undo.Add(edit);return result;}
}
public partial class MainWindow {
    readonly DocumentHistory fallbackHistory=new DocumentHistory();
    DocumentHistory History {get{return activeDocument==null?fallbackHistory:activeDocument.History;}}
    void AcceptEdit(string value){if(value!=saved)value=NormalizeLineEndings(value,documentNewline);History.Record(text,value);text=value;}
    bool SourceMatchesSaved(){if(source.Document.TextLength!=saved.Length)return false;var snapshot=source.Document.CreateSnapshot();for(int at=0;at<saved.Length;at+=8192){int count=Math.Min(8192,saved.Length-at);string chunk=snapshot.GetText(at,count);if(String.CompareOrdinal(chunk,0,saved,at,count)!=0)return false;}return true;}
    System.Windows.Threading.DispatcherTimer historyResetNoticeTimer;
    void ResetMismatchedHistory(){
        DocumentHistory.ResetCount++;
        History.Clear();
        if(historyResetNoticeTimer==null){historyResetNoticeTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(4)};historyResetNoticeTimer.Tick+=delegate{historyResetNoticeTimer.Stop();UpdateTitle();};Closed+=delegate{historyResetNoticeTimer.Stop();};}
        historyResetNoticeTimer.Stop();historyResetNoticeTimer.Start();UpdateTitle();
    }
    // Both source-delta and rich/mode history validate before touching the document. Only a
    // validation failure is recoverable here; unrelated UI exceptions must not be hidden.
    void RestoreHistory(bool redo){try{RestoreHistoryCore(redo);}catch(DocumentHistory.MismatchException){ResetMismatchedHistory();}}
    // Reading mode must not edit the body through undo, except the task ticks it makes itself
    // (ReadingTick); the read surface of a Markdown body is the source surface, so the old
    // "no undo while reading a source" rule would also block those ticks. TXT read mode and every
    // other reading-mode entry stay blocked.
    void RestoreHistoryCore(bool redo){if(documentOperationBusy)return;FlushDocumentChanges();if(redo?!History.CanRedo:!History.CanUndo)return;if(!editing&&(PlainText||!History.ReadingTick(redo,source.Document)))return;bool applied;loading=true;try{applied=History.ApplySource(redo,source);}finally{loading=false;}if(applied){if(SourceMatchesSaved())text=saved;else sourceSnapshotDirty=true;UpdateTitle();RefreshOutline();source.Focus();/* E1: the entry puts its selection back, but a caret outside the viewport stayed off-screen after the undo. The reveal waits for the frame's layout and only runs when the caret's row is really off screen (QueueCaretRowVisible), so an undo inside the view no longer scrolls a measured row from a stale height tree. */QueueCaretRowVisible();return;}int start=source.SelectionStart,length=source.SelectionLength;double scroll=source.VerticalOffset;var restoredMode=redo?History.RedoMode:History.UndoMode;text=redo?History.Redo(text):History.Undo(text);if(restoredMode.HasValue)textMode=restoredMode;Render();LayoutMode();source.Select(Math.Min(start,source.Document.TextLength),Math.Min(length,Math.Max(0,source.Document.TextLength-start)));source.ScrollToVerticalOffset(scroll);source.Focus();}
}
}
