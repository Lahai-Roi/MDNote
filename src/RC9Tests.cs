using System;using System.IO;using System.Text;using System.Windows;using System.Windows.Controls;using System.Windows.Documents;
namespace MDNote {public partial class MainWindow {
internal void VerifyRecoveryBacklog(RecoveryRecord record){
    string id=Guid.NewGuid().ToString("N");var jobs=new System.Collections.Generic.List<System.Threading.Tasks.Task>();var gate=RecoveryStore.TestWriter;
    gate.Wait();try{
        for(int i=0;i<100;i++)jobs.Add(RecoveryStore.WriteAsync(id,record,new ICSharpCode.AvalonEdit.Document.StringTextSource("generated revision "+i),null));
        Check(System.Threading.Tasks.Task.WhenAll(jobs.GetRange(0,99)).Wait(5000),"Superseded recovery snapshots remained queued behind a blocked writer");Check(!jobs[99].IsCompleted,"Latest recovery bypassed writer serialization");
    }finally{gate.Release();}
    Check(System.Threading.Tasks.Task.WhenAll(jobs).Wait(10000)&&RecoveryStore.LastError==null,"Coalesced recovery writes failed");RecoveryStore.ReleaseTestOwner();var records=RecoveryStore.List();bool found=false;foreach(var saved in records)if(RecoveryStore.RecordId(saved.File)==id){Check(RecoveryStore.Read(saved.File).Text=="generated revision 99","Older queued recovery replaced the newest snapshot");found=true;}Check(found,"Latest coalesced recovery was missing");RecoveryStore.Remove(id);
    gate.Wait();try{var pending=RecoveryStore.WriteAsync(id,record,null,"discarded");RecoveryStore.Remove(id);Check(pending.Wait(5000),"Discarded recovery retained a blocked pending write");}finally{gate.Release();}
    RecoveryStore.ReleaseTestOwner();foreach(var saved in RecoveryStore.List())Check(RecoveryStore.RecordId(saved.File)!=id,"Canceled pending recovery resurrected discarded data");
}
internal void VerifyCompoundHistory(){
    VerifyLargeHistoryAndMismatch();
    string original="abcDEFghiJKL",changed=">abc12ghitail";var history=new DocumentHistory();var parts=new System.Collections.Generic.List<DocumentHistory.Edit> {new DocumentHistory.Edit {At=9,Removed="JKL",Inserted="tail"},new DocumentHistory.Edit {At=0,Removed="",Inserted=">"},new DocumentHistory.Edit {At=3,Removed="DEF",Inserted="12"}};history.RecordChanges(parts,6,5);parts[0].Inserted="caller changed its input";
    for(int i=0;i<20;i++){Check(history.Undo(changed)==original&&!history.CanUndo&&history.CanRedo,"Compound string undo changed content or split the group");Check(history.Redo(original)==changed,"Compound string redo changed offsets");}
    var editor=new SourceEditor {Text=changed};int updates=0;editor.Document.UpdateFinished+=delegate{updates++;};Check(history.ApplySource(false,editor)&&editor.Text==original&&editor.CaretIndex==6&&updates==1,"Compound source undo lost content/caret or emitted multiple transactions");Check(history.ApplySource(true,editor)&&editor.Text==changed&&editor.CaretIndex==5&&updates==2,"Compound source redo changed offsets or caret");
    editor.Text="unrelated";bool rejected=false;try{history.ApplySource(false,editor);}catch(InvalidOperationException){rejected=true;}Check(rejected&&editor.Text=="unrelated"&&history.CanUndo&&!history.CanRedo,"Mismatched compound undo partially changed source or history");
    history=new DocumentHistory();rejected=false;try{history.RecordChanges(new[]{new DocumentHistory.Edit {At=0,Removed="abc",Inserted="x"},new DocumentHistory.Edit {At=2,Removed="c",Inserted="y"}});}catch(ArgumentException){rejected=true;}Check(rejected&&!history.CanUndo,"Overlapping compound transaction entered history");
    history.RecordChanges(new[]{new DocumentHistory.Edit {At=0,Removed="a",Inserted="a"}});Check(!history.CanUndo,"Compound no-op created an undo entry");
}
internal void VerifyLargeHistoryAndMismatch(){
    var original=new StringBuilder();var changed=new StringBuilder();var parts=new System.Collections.Generic.List<DocumentHistory.Edit>();
    for(int i=0;i<10000;i++){string inserted="#"+(i%97)+"😀";parts.Add(new DocumentHistory.Edit {At=original.Length+2,Removed="--",Inserted=inserted});original.Append("ab--cd\n");changed.Append("ab").Append(inserted).Append("cd\n");}
    var history=new DocumentHistory();history.RecordChanges(parts);Check(history.Undo(changed.ToString())==original.ToString()&&history.Redo(original.ToString())==changed.ToString(),"Large compound undo/redo lost source spans");
    history=new DocumentHistory();history.RecordDelta(1,"b","B");bool rejected=false;try{history.Undo("aXc");}catch(InvalidOperationException){rejected=true;}Check(rejected&&history.CanUndo&&!history.CanRedo,"Mismatched single history moved its undo stack");var editor=new SourceEditor {Text="aXc"};rejected=false;try{history.ApplySource(false,editor);}catch(InvalidOperationException){rejected=true;}Check(rejected&&editor.Text=="aXc"&&history.CanUndo,"Mismatched source undo silently overwrote external text");Check(history.Undo("aBc")=="abc","Rejected source undo damaged a valid history entry");
    rejected=false;try{history.Redo("aYc");}catch(InvalidOperationException){rejected=true;}Check(rejected&&history.CanRedo&&history.Redo("abc")=="aBc","Mismatched redo damaged source or history");
}
internal void VerifyLazySourceHistory(){
    textMode=true;path="";SetMode(true);SetText("first\r\nsecond\r\nlast 😀");saved=text;source.AppendText(" first edit");source.AppendText(" second edit");Undo();Check(sourceSnapshotDirty&&source.Document.GetText(source.Document.TextLength-11,11)==" first edit","Source undo eagerly joined text or lost an edit");Redo();Check(sourceSnapshotDirty,"Source redo eagerly joined text");Undo();Undo();Check(!sourceSnapshotDirty&&!HasUnsavedChanges&&documentText==saved,"Source undo did not restore the exact saved state");Redo();Check(sourceSnapshotDirty&&HasUnsavedChanges,"Source redo did not restore unsaved state");textMode=false;SetText("");saved=text;
}
internal async System.Threading.Tasks.Task VerifyBackgroundOutline(){
    VerifyOutlineStreaming();
    string sample="# First\r\n````text\r\n# hidden\r\n```\r\n# still hidden\r\n~~~\r\n# hidden again\r\n````\r\n## Child\r# Last";
    var nodes=ScanSourceOutline(new ICSharpCode.AvalonEdit.Document.StringTextSource(sample),System.Threading.CancellationToken.None);Check(nodes.Count==2&&nodes[0].Children.Count==1&&nodes[0].Children[0].Offset==sample.IndexOf("## Child",StringComparison.Ordinal)&&nodes[1].Offset==sample.IndexOf("# Last",StringComparison.Ordinal),"Outline fence length/type or CRLF offset differs");
    sample=new string('x',8191)+"\r\n# boundary\r\n";nodes=ScanSourceOutline(new ICSharpCode.AvalonEdit.Document.StringTextSource(sample),System.Threading.CancellationToken.None);Check(nodes.Count==1&&nodes[0].Offset==8193,"Outline chunk split changed CRLF position");
    var window=new MainWindow();try{
        window.Show();window.textMode=false;window.SetMode(true);window.preview.IsChecked=true;var builder=new StringBuilder();for(int i=0;i<6000;i++)builder.Append("# Generated heading ").Append(i).Append("\r\n");string original=builder.ToString();window.SetText(original);window.saved=original;window.outline.Visibility=Visibility.Visible;window.RefreshOutline();Check(window.sourceOutlineTask!=null,"Large outline used synchronous scan");await window.sourceOutlineTask;Check(window.LastSourceOutlineError==null,"Background outline failed: "+window.LastSourceOutlineError);nodes=window.outline.ItemsSource as System.Collections.Generic.List<SourceOutlineNode>;Check(nodes!=null&&nodes.Count==6000,"Virtual outline omitted headings");window.UpdateLayout();Check(window.outline.ItemContainerGenerator.ContainerFromIndex(5999)==null,"Outline eagerly realized all 6000 heading controls");
        var selected=nodes[100];window.source.Document.Insert(0,"prefix\r\n");Check(window.sourceSnapshotDirty,"Outline eagerly materialized edited source");window.outline.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(null,selected,TreeView.SelectedItemChangedEvent));Check(window.source.CaretIndex==selected.Offset+8,"Visible older outline used a pending snapshot's offsets");await window.sourceOutlineTask;Check(window.LastSourceOutlineError==null&&window.sourceSnapshotDirty,"Outline refresh failed or joined full source");
        window.outline.Visibility=Visibility.Collapsed;Check(window.outline.ItemsSource==null&&window.displayedOutlineDocument==null,"Hidden outline retained document data");window.preview.IsChecked=false;Check(window.outline.ItemTemplate==null,"Source outline template leaked into rich mode");
    }finally{window.CaptureTab();foreach(var state in window.documents)state.Saved=state.Text;window.saved=window.text;window.Close();}
}
internal void VerifyOutlineStreaming(){
    var generated=new StringBuilder();var expected=new System.Collections.Generic.List<SourceOutlineNode>();char fence='\0';int fenceLength=0,offset=0;
    var random=new Random(199);string[] tails={"", " "," title ##  ","x", " info`bad", " \t", "# title", "😀 title"};
    // A heading whose visible text is only whitespace ("### \t") is not an outline item: the
    // scanner skips a title that trims to nothing, so the reference grammar requires a
    // non-whitespace title too.
    for(int i=0;i<3000;i++){
        string line=new string(' ',random.Next(6))+new string("#`~x"[random.Next(4)],random.Next(1,10))+tails[random.Next(tails.Length)];
        var marker=System.Text.RegularExpressions.Regex.Match(line,@"^\s{0,3}(`{3,}|~{3,})(.*)$");
        if(fence!='\0'){if(marker.Success&&marker.Groups[1].Value[0]==fence&&marker.Groups[1].Value.Length>=fenceLength&&String.IsNullOrWhiteSpace(marker.Groups[2].Value))fence='\0';}
        else if(marker.Success&&!(marker.Groups[1].Value[0]=='`'&&marker.Groups[2].Value.Contains("`"))){fence=marker.Groups[1].Value[0];fenceLength=marker.Groups[1].Value.Length;}
        else{var heading=System.Text.RegularExpressions.Regex.Match(line,@"^\s{0,3}(#{1,6})\s+(\S.*?)\s*#*\s*$");if(heading.Success)expected.Add(new SourceOutlineNode {Title=heading.Groups[2].Value,Level=heading.Groups[1].Value.Length,Offset=offset});}
        string newline=i%3==0?"\r\n":i%3==1?"\r":"\n";generated.Append(line).Append(newline);offset+=line.Length+newline.Length;
    }
    var roots=ScanSourceOutline(new ICSharpCode.AvalonEdit.Document.StringTextSource(generated.ToString()),System.Threading.CancellationToken.None);var actual=new System.Collections.Generic.List<SourceOutlineNode>();Action<SourceOutlineNode> flatten=null;flatten=delegate(SourceOutlineNode node){actual.Add(node);foreach(var child in node.Children)flatten(child);};foreach(var root in roots)flatten(root);
    Check(actual.Count==expected.Count,"Streaming outline changed heading recognition");for(int i=0;i<actual.Count;i++)Check(actual[i].Title==expected[i].Title&&actual[i].Level==expected[i].Level&&actual[i].Offset==expected[i].Offset,"Streaming outline differs from original grammar at "+i);
    string longLine=new string('x',2000000),sample=longLine+"\r\n```"+longLine+"`\n# visible\n~~~"+longLine+"\n# hidden\n~~~\n# final";
    roots=ScanSourceOutline(new ICSharpCode.AvalonEdit.Document.StringTextSource(sample),System.Threading.CancellationToken.None);Check(roots.Count==2&&roots[0].Title=="visible"&&roots[1].Title=="final"&&roots[0].Offset==sample.IndexOf("# visible",StringComparison.Ordinal),"Long nonheading/fence info lost outline state or offsets");
}
internal void VerifyBackgroundOutlineSync(){var prior=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));try{var task=VerifyBackgroundOutline();var watch=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&watch.ElapsedMilliseconds<15000){Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}Check(task.IsCompleted,"Background outline test timed out");task.GetAwaiter().GetResult();}finally{System.Threading.SynchronizationContext.SetSynchronizationContext(prior);}}
internal void VerifyAdaptiveGutter(){
    foreach(double dpi in new[]{1.0,1.25,1.5,2.0}){double previous=0;foreach(int lines in new[]{1,9,10,99,100,99999,659292,999999999,Int32.MaxValue}){double width=textGutter.WidthForLines(lines,dpi);var label=new System.Windows.Media.FormattedText(lines.ToString(System.Globalization.CultureInfo.InvariantCulture),System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new System.Windows.Media.Typeface("Consolas"),11,System.Windows.Media.Brushes.Black,dpi);Check(width>=previous&&width>=label.Width+9&&width<100,"Gutter clipped line digits or grew excessively at "+lines);previous=width;}}
    path="";textMode=true;SetMode(true);SetText("single");LayoutMode();UpdateLayout();double narrow=textGutter.Width,shown=textGutter.Visibility==Visibility.Visible?narrow:0;/* #24: two fixed widths - stable through 99,999 lines, wide from 100,000 */source.Document.Insert(0,new string('\n',1000));UpdateLayout();Check(textGutter.Width==narrow&&source.Margin.Left==shown,"Gutter width shifted inside the ordinary line range");source.Document.Remove(0,1000);source.Document.Insert(0,new string('\n',100000));UpdateLayout();Check(textGutter.Width>narrow&&source.Margin.Left==(textGutter.Visibility==Visibility.Visible?textGutter.Width:0),"Gutter did not widen past 99,999 lines");source.Document.Remove(0,100000);UpdateLayout();Check(textGutter.Width==narrow&&source.Margin.Left==shown,"Gutter did not shrink after line deletion");for(int digits=1;digits<=10;digits++){double dpiWidth=textGutter.WidthForLines(digits==10?Int32.MaxValue:(int)Math.Pow(10,digits-1),1.0);Check(dpiWidth==textGutter.WidthForLines(digits<=5?1:Int32.MaxValue,1.0),"Gutter width is not one of the two fixed widths at "+digits+" digits");}textMode=false;SetText("");LayoutMode();UpdateLayout();Check(textGutter.Visibility==Visibility.Visible&&source.Margin.Left==textGutter.Width,"Markdown edit keeps the line-number column");saved="";CaptureTab();
}
internal void VerifyDeepHtmlPaste(){var table=new StringBuilder("<table>");for(int i=0;i<6000;i++)table.Append("<tbody>");table.Append("<tr><td>cell</td></tr>");for(int i=0;i<6000;i++)table.Append("</tbody>");table.Append("</table>");Check(ClipboardMarkdown(table.ToString())=="| cell |\n| --- |","Deep HTML table lost content or overflowed stack");var spans=new StringBuilder();for(int i=0;i<6000;i++)spans.Append("<span>");spans.Append("content");for(int i=0;i<6000;i++)spans.Append("</span>");Check(ClipboardMarkdown(spans.ToString())=="content","Deep inline HTML fallback lost text");}
internal void VerifyClipboardByteOffsets(){string header="Version:1.0\r\nStartHTML:{0}\r\nEndHTML:{1}\r\nStartFragment:{2}\r\nEndFragment:{3}\r\n",prefix="<html><body>한글 before",fragment="<p>복사 **literal** <strong>bold</strong>😀</p>",suffix="after</body></html>";string empty=String.Format(header,"0000000000","0000000000","0000000000","0000000000");int htmlStart=Encoding.UTF8.GetByteCount(empty),start=htmlStart+Encoding.UTF8.GetByteCount(prefix),end=start+Encoding.UTF8.GetByteCount(fragment),htmlEnd=end+Encoding.UTF8.GetByteCount(suffix);string payload=String.Format(header,htmlStart.ToString("D10"),htmlEnd.ToString("D10"),start.ToString("D10"),end.ToString("D10"))+prefix+fragment+suffix;Check(ClipboardFragment(payload)==fragment,"CF_HTML UTF8 fragment byte offsets were treated as character offsets");string result=ClipboardMarkdown(payload);Check(!result.Contains("Version:")&&!result.Contains("before")&&result.Contains("**bold**")&&result.Contains("😀"),"CF_HTML metadata leaked into pasted text");Check(ClipboardFragment("<p>before</p><!--StartFragment-->선택<!--EndFragment--><p>after</p>")=="선택","Clipboard fragment comments failed");Check(ClipboardMarkdown("<div style='font-weight:700'><p>before<!--StartFragment-->selected<!--EndFragment-->after</p></div>")=="**selected**","Clipboard selection lost ancestor bold style");Check(ClipboardMarkdown("<p><em>before<!--StartFragment-->selected<!--EndFragment-->after</em></p>")=="*selected*","Clipboard selection lost ancestor italic style");}
internal void VerifyHtmlBlankParagraphs(){path="";textMode=false;SetMode(true);string[] htmls={"<p>first</p><p><br></p><p><br></p><p>last</p>","<p><br></p><p>first</p><p><br></p>","<div>first</div><div></div><div>last</div>","<p>first<br>second</p>"};string[] expected={"first\n\n\nlast","\nfirst\n","first\n\nlast","first\nsecond"};for(int i=0;i<htmls.Length;i++){Check(ClipboardMarkdown(htmls[i])==expected[i],"HTML conversion dropped blank paragraphs "+i);SetText("");var data=new DataObject();data.SetData(DataFormats.Html,htmls[i]);PasteData(data,false);FlushDocumentChanges();Check(text==expected[i],"HTML insertion changed blank paragraph count "+i);string serialized=text;SetText(serialized);Check(text==expected[i],"HTML blank paragraph reload drift "+i);}}
internal void VerifyAsyncModeConversion(){var prior=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));try{var task=VerifyAsyncModeConversionCore();var watch=System.Diagnostics.Stopwatch.StartNew();while(!task.IsCompleted&&watch.ElapsedMilliseconds<20000){Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}Check(task.IsCompleted,"Async conversion timed out");task.GetAwaiter().GetResult();}finally{CancelFileOpen();path="";textMode=false;SetText("");saved="";CaptureTab();System.Threading.SynchronizationContext.SetSynchronizationContext(prior);}}
async System.Threading.Tasks.Task VerifyAsyncModeConversionCore(){path="";textMode=true;SetMode(true);var generated=new StringBuilder();for(int i=0;i<100;i++)generated.Append("**literal** <span> ").Append(new string('x',2900)).Append("\r\n");string value=generated.ToString();documentNewline="\r\n";SetText(value);LayoutMode();source.CaretIndex=source.Document.GetLineByNumber(70).Offset+12;int caret=source.CaretIndex;ToggleTextMode();for(int i=0;i<100&&!documentOperationBusy&&fileOpenRunning;i++)await System.Threading.Tasks.Task.Delay(2);CancelFileOpen();while(fileOpenRunning)await System.Threading.Tasks.Task.Delay(2);Check(PlainText&&text==value&&source.CaretIndex==caret,"Canceled conversion changed document or caret");ToggleTextMode();while(fileOpenRunning)await System.Threading.Tasks.Task.Delay(2);if(LastFileOpenError!=null)throw LastFileOpenError;Check(!PlainText&&Canonical(text)==Canonical(LiteralMarkdown(value)),"Async TXT-to-MD conversion lost literal text");ToggleTextMode();while(fileOpenRunning)await System.Threading.Tasks.Task.Delay(2);if(LastFileOpenError!=null)throw LastFileOpenError;Check(PlainText&&Canonical(text)==Canonical(value),"Async MD-to-TXT conversion changed content");var location=source.Document.GetLocation(source.CaretIndex);Check(location.Line==70&&location.Column==13,"Async mode roundtrip moved logical caret position");}
internal void VerifyLargeSourceCaretSpacing(){double previous=linePercent;try{path="";textMode=true;SetMode(true);var generated=new StringBuilder();for(int i=0;i<3500;i++)generated.Append("Generated line ").Append(i).Append("\n");SetText(generated.ToString());LayoutMode();source.CaretIndex=source.Document.GetLineByNumber(3100).Offset+3;source.ScrollToLine(3099);UpdateLayout();foreach(int spacing in new[]{50,400,100,200}){SetLinePercent(spacing);UpdateLayout();var rectangle=source.GetRectFromCharacterIndex(source.CaretIndex);Check(!rectangle.IsEmpty&&Math.Abs(rectangle.Top-source.ViewportHeight/3)<fontSize*spacing/100+4,"Source line spacing lost distant caret position");}Check(source.Document.GetLineByOffset(source.CaretIndex).LineNumber==3100,"Line spacing moved caret to a different logical line");}finally{SetLinePercent(previous);path="";textMode=false;SetText("");saved="";CaptureTab();}}
internal void VerifyAtomicSaveFailures(string scratch){string root=Path.Combine(scratch,"atomic-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);string file=Path.Combine(root,"document.txt");try{File.WriteAllText(file,"BASE",new UTF8Encoding(false));bool rejected=false;using(var held=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){try{WriteDocumentAtomic(file,"NEW",StrictEncoding(0));}catch(IOException){rejected=true;}catch(UnauthorizedAccessException){rejected=true;}}Check(rejected&&File.ReadAllText(file)=="BASE"&&Directory.GetFiles(root).Length==1,"Failed atomic replace changed original or leaked temp");AtomicWriteObserver=delegate(string phase){if(phase=="durable-temp")throw new IOException("Simulated failure before commit");};rejected=false;try{WriteDocumentAtomic(file,"NEW",StrictEncoding(0));}catch(IOException){rejected=true;}finally{AtomicWriteObserver=null;}Check(rejected&&File.ReadAllText(file)=="BASE"&&Directory.GetFiles(root).Length==1,"Pre-commit failure lost original");WriteDocumentAtomic(file,"한글😀\r\n",StrictEncoding(1));Check(File.ReadAllText(file)=="한글😀\r\n"&&Directory.GetFiles(root).Length==1,"Durable atomic save changed content");}finally{AtomicWriteObserver=null;foreach(string remaining in Directory.GetFiles(root))File.Delete(remaining);Directory.Delete(root);}}
internal void VerifyHistoryBranchBudget(){var history=new DocumentHistory();string block=new string('x',1500000);history.RecordDelta(0,"","a");history.RecordDelta(1,"",block);history.RecordDelta(1,"",block);string value="a"+block+block;value=history.Undo(value);history.RecordDelta(1,"",block);value=value.Insert(1,block);value=history.Undo(value);Check(history.CanUndo&&history.CanRedo,"History branching unexpectedly dropped retained edits");value=history.Undo(value);Check(value=="a"&&history.CanUndo,"Abandoned redo bytes incorrectly exhausted history budget");Check(source.Document.UndoStack.SizeLimit==0,"Source retained a duplicate native undo history");}
internal void VerifyReferenceFenceBoundaries(){foreach(string value in new[]{"````text\n```\n[id]: https://hidden.invalid/\n````\n[id]: https://visible.invalid/\n[label][id]","~~~text\n```\n[id]: https://hidden.invalid/\n~~~\n[id]: https://visible.invalid/\n[label][id]","```inline`code```\n[id]: https://visible.invalid/\n[label][id]","````text\n```\n[id]: https://hidden.invalid/\n````\n[label][id]"}){var document=Markdown.Parse(value,16,family,System.Windows.Media.Brushes.Black,false,"");var paragraph=(Paragraph)document.Blocks.LastBlock;string address=null;foreach(Inline inline in paragraph.Inlines){var link=inline as Hyperlink;if(link!=null)address=link.Tag as string;}Check(address==(value.Contains("visible.invalid")?"https://visible.invalid/":null),"Reference scanner accepted an unmatched fence or code definition");}}
internal void VerifyParallelSearch(){var random=new Random(9127);var data=new StringBuilder();for(int i=0;i<1100000;i++)data.Append("abAB가나"[random.Next(6)]);string value=data.ToString();foreach(string term in new[]{"a","aa","aba","AB","가나","missing"})foreach(var comparison in new[]{StringComparison.Ordinal,StringComparison.OrdinalIgnoreCase}){var expected=new System.Collections.Generic.List<int>();int at=0;while(at<=value.Length-term.Length){int found=value.IndexOf(term,at,comparison);if(found<0)break;expected.Add(found);at=found+term.Length;}var actual=ParallelTextMatches(value,term,comparison,System.Threading.CancellationToken.None);Check(actual.Count==expected.Count,"Parallel search count mismatch");for(int i=0;i<actual.Count;i++)Check(actual[i]==expected[i],"Parallel boundary/overlap search mismatch");}using(var canceled=new System.Threading.CancellationTokenSource()){canceled.Cancel();bool rejected=false;try{ParallelTextMatches(value,"a",StringComparison.Ordinal,canceled.Token);}catch(OperationCanceledException){rejected=true;}Check(rejected,"Parallel search ignored cancellation");}}
internal void VerifyRecovery(string scratch){var previousContext=System.Threading.SynchronizationContext.Current;System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));string root=Path.Combine(scratch,"recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);RecoveryStore.TestRoot=root;string id=Guid.NewGuid().ToString("N"),original=Path.Combine(root,"original.txt");File.WriteAllText(original,"SAVED");try{var record=new RecoveryRecord {Name="recovered.txt",OriginalPath=original,PlainText=true,CodePage=65001,Newline="\r\n"};VerifyRecoveryBacklog(record);string value="한글😀\0\r\ntrailing\n"+new string((char)0xD800,1);RecoveryStore.WriteAsync(id,record,new ICSharpCode.AvalonEdit.Document.StringTextSource(value),null).GetAwaiter().GetResult();Check(RecoveryStore.LastError==null&&RecoveryStore.List().Count==0,"Recovery included a live editor draft or failed writing");RecoveryStore.ReleaseTestOwner();var records=RecoveryStore.List();Check(records.Count==1&&RecoveryStore.Read(records[0].File).Text==value,"Recovery lost UTF16 code units or newline");Check(File.ReadAllText(original)=="SAVED","Recovery overwrote original file");var pending=RecoveryStore.WriteAsync(id,record,new ICSharpCode.AvalonEdit.Document.StringTextSource(new string('x',1000000)),null);RecoveryStore.Remove(id);pending.GetAwaiter().GetResult();RecoveryStore.ReleaseTestOwner();Check(RecoveryStore.List().Count==0,"An in-flight recovery write resurrected discarded data");value="Recovered unsaved 한글\r\n";RecoveryStore.WriteAsync(id,record,null,value).GetAwaiter().GetResult();RecoveryStore.ReleaseTestOwner();var recovered=RecoveryStore.List()[0];var restore=RestoreRecovery(recovered);var watch=System.Diagnostics.Stopwatch.StartNew();while(!restore.IsCompleted&&watch.ElapsedMilliseconds<10000){Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}restore.GetAwaiter().GetResult();Check(path==""&&text==value&&HasUnsavedChanges&&PlainText,"Restored document was clean or bound to original path");string copy=Path.Combine(root,"saved-copy.txt");Check(SaveToPath(copy,StrictEncoding(0),false)&&!HasUnsavedChanges&&File.ReadAllText(copy)==value&&File.ReadAllText(original)=="SAVED","Saving recovery changed original or kept dirty state");RecoveryStore.ReleaseTestOwner();Check(RecoveryStore.List().Count==0,"Saved recovery was offered again");string junk=Path.Combine(root,"unexpected.draft");File.WriteAllBytes(junk,new byte[]{1,2,3});Check(RecoveryStore.List().Count==0&&File.Exists(junk),"Malformed recovery file broke listing or was deleted");File.Delete(junk);junk=Path.Combine(root,"invalid-owner-"+id+".draft");File.WriteAllBytes(junk,new byte[]{1,2,3});RecoveryStore.DiscardGroup(id);Check(File.Exists(junk),"Discard touched a foreign recovery filename");File.Delete(junk);junk=Path.Combine(root,Guid.NewGuid().ToString("N")+"-"+id+".draft");File.WriteAllBytes(junk,new byte[]{1,2,3});Check(RecoveryStore.List().Count==0&&File.Exists(junk),"Truncated recovery was listed or removed");File.Delete(junk);RecoveryStore.WriteAsync(id,record,null,"").GetAwaiter().GetResult();RecoveryStore.ReleaseTestOwner();restore=RestoreRecovery(RecoveryStore.List()[0]);while(!restore.IsCompleted){Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{}));System.Threading.Thread.Sleep(1);}restore.GetAwaiter().GetResult();Check(text==""&&HasUnsavedChanges,"Empty recovered deletion was treated as saved");Check(ApplyEditExit(SaveChoice.Discard)&&!HasUnsavedChanges,"Discarding empty recovery failed");}finally{RecoveryStore.ReleaseTestOwner();RecoveryStore.TestRoot=null;recoveredDocument=false;path="";saved="";SetText("");foreach(var state in documents){state.Saved=state.Text;state.Recovered=false;}CaptureTab();foreach(string file in Directory.GetFiles(root))File.Delete(file);Directory.Delete(root);System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);}}
internal void VerifyExplicitEncoding(string scratch){foreach(int code in new[]{65001,1200,1201,12000,12001,949,932,936,950,1252}){string sample=code==949?"한글":code==932?"日本語":code==936?"你好":code==950?"繁體":code==1252?"café":"한글😀";foreach(bool bom in new[]{false,true}){Encoding original=code==65001?(Encoding)new UTF8Encoding(bom,true):code==1200?new UnicodeEncoding(false,bom,true):code==1201?new UnicodeEncoding(true,bom,true):code==12000?new UTF32Encoding(false,bom,true):code==12001?new UTF32Encoding(true,bom,true):Encoding.GetEncoding(code,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);string file=Path.Combine(scratch,"open-encoding-"+code+"-"+bom+".txt");try{File.WriteAllText(file,sample,original);Encoding detected;string stamp;Check(ReadTextFileEncodingVersion(file,code,out detected,out stamp)==sample,"Explicit encoding decoded wrong text");byte[] actual=detected.GetBytes(sample),prefix=detected.GetPreamble(),expected=File.ReadAllBytes(file);Check(expected.Length==actual.Length+prefix.Length&&stamp==FileVersion.Of(expected),"Explicit encoding changed BOM or stamp");for(int i=0;i<expected.Length;i++)Check(expected[i]==(i<prefix.Length?prefix[i]:actual[i-prefix.Length]),"Explicit encoding re-encode differs");if(bom&&code==1200){bool rejected=false;try{ReadTextFileEncodingVersion(file,65001,out detected,out stamp);}catch(InvalidDataException){rejected=true;}Check(rejected,"Explicit encoding ignored a conflicting BOM");}}finally{if(File.Exists(file))File.Delete(file);}}}}
// A rich undo/redo records into the source mirror and leaves a render batch queued at Background
// priority (the Ctrl+Z path deliberately skips the synchronous flush). FlushDocumentChanges then
// returns early whenever an IME composition is in progress, and SwitchTab used to swap the text
// while that batch still pointed at the old viewer/source: the text setter threw
// "History surface changed before flush". Switching must settle the batch on its own surface
// first, and the search cache must not keep the closed document alive.
// A tab switch during a pending edit must settle the buffer on its own tab, and the search
// cache must not keep the closed document alive.
internal void VerifyTabSurfaceFlush(){
    var window=new MainWindow();try{
        window.Show();window.textMode=false;window.SetMode(true);
        string original="alpha beta\n\ngamma";window.SetText(original);window.saved=window.text;
        var first=window.activeDocument;
        window.CreateTab();window.SetText("second tab");window.saved=window.text;
        var second=window.activeDocument;
        window.SwitchTab(first);
        window.source.Document.Insert(0,"typed ");window.FlushDocumentChanges();
        string edited=window.text;Check(edited!=original,"Tab flush probe did not edit the document");
        window.RestoreEditorHistory(false);
        Check(window.source.Document.Text==original,"Undo did not reach the source buffer");
        window.imeComposing=true;
        try{window.SwitchTab(second);}finally{window.imeComposing=false;}
        Check(Object.ReferenceEquals(window.activeDocument,second)&&window.text=="second tab","Tab switch during a composition lost the target document");
        Check(first.Text==original,"Switched-away tab kept the pre-undo text: "+first.Text.Replace("\n","/"));
        window.SwitchTab(first);
        Check(window.text==original,"Returning to the tab lost the undone text");
        window.Redo();Check(window.text==edited,"Redo after the tab switch lost the edit");
        window.Undo();Check(window.text==original,"Undo after redo did not return to the original text");
        window.saved=window.text;window.sourceSnapshotDirty=false;
        window.ShowSearch();window.query.Text="gamma";window.RefreshSearch();
        Check(window.searchCacheMap!=null&&Object.ReferenceEquals(window.searchCacheSource,window.source.Document),"Search did not cache the current document");
        window.query.Text="";/* an empty query returns early and leaves the populated cache in place */
        window.saved=window.text;foreach(var state in window.documents)state.Saved=state.Text;
        window.CloseTab(window.activeDocument);
        Check(window.searchCacheMap==null&&window.searchCacheMatches==null&&window.searchCacheSource==null&&window.searchCacheVersion==null,"Closing a searched tab kept its search cache alive");
    }finally{window.imeComposing=false;if(window.searchHost.Visibility==Visibility.Visible)window.CloseSearch();window.CaptureTab();foreach(var state in window.documents)state.Saved=state.Text;window.saved=window.text;window.Close();}
}
internal void VerifyFieldShortcutIsolation(){ShowSearch();try{UpdateLayout();sizes.ApplyTemplate();var sizeBox=sizes.Template==null?null:sizes.Template.FindName("PART_EditableTextBox",sizes) as TextBox;Check(sizeBox!=null,"Font size field template missing");
    foreach(var key in new[]{System.Windows.Input.Key.V,System.Windows.Input.Key.Z,System.Windows.Input.Key.Y,System.Windows.Input.Key.B,System.Windows.Input.Key.I,System.Windows.Input.Key.K}){Check(FieldOwnsKey(query,key)&&FieldOwnsKey(replacement,key)&&FieldOwnsKey(sizeBox,key),"Field shortcut was routed to the document: "+key);Check(!FieldOwnsKey(source.TextArea,key),"Document lost its shortcut: "+key);}
    foreach(var key in new[]{System.Windows.Input.Key.S,System.Windows.Input.Key.F,System.Windows.Input.Key.H,System.Windows.Input.Key.N,System.Windows.Input.Key.W})Check(!FieldOwnsKey(query,key),"Window command blocked in a field: "+key);}finally{CloseSearch();}}
// Every change inside one TextDocument update must be exactly one undo step, whatever its count,
// order or overlap, and only a single-change update may still merge with a typing run.
internal void VerifyDocumentHistoryGroups(){
    foreach(int count in new[]{3,5,20}){
        for(int pass=0;pass<2;pass++){
            string original=HistoryGroupText(12);
            var host=new HistoryProbeHost(original);var document=host.Editor.Document;
            host.Editor.Select(5,3);
            int afterStart=0,afterLength=0;
            document.BeginUpdate();
            try{
                if(pass==0){
                    int at=document.TextLength/2,lastAt=at,lastLength=0;
                    for(int i=0;i<count;i++){
                        int changeAt,remove;string insert;
                        if(i==0){changeAt=at;remove=2;insert="<"+i+"-abc>";}
                        else if(i%3==1){changeAt=lastAt+lastLength;remove=0;insert="|"+i;}
                        else if(i%3==2){changeAt=lastAt+Math.Min(lastLength-1,i);remove=1;insert="%"+i;}
                        else{changeAt=Math.Max(lastAt,lastAt+lastLength-1);remove=Math.Min(document.TextLength-changeAt,lastLength+2);insert="~"+i;}
                        document.Replace(changeAt,remove,insert);lastAt=changeAt;lastLength=insert.Length;
                    }
                }else{
                    int baseAt=Math.Max(0,document.TextLength-3);
                    for(int i=0;i<count;i++){int changeAt=Math.Max(0,baseAt-i*4);document.Replace(changeAt,Math.Min(2,document.TextLength-changeAt),"["+i+"]");}
                }
                afterStart=Math.Min(5,document.TextLength);afterLength=Math.Min(2,Math.Max(0,document.TextLength-afterStart));
                host.Editor.Select(afterStart,afterLength);
            }finally{document.EndUpdate();}
            string changed=document.Text;
            Check(changed!=original,"History group probe made no change ("+count+","+pass+")");
            Check(host.History.CanUndo,"Update with "+count+" changes recorded no undo entry (pass "+pass+")");
            bool undone=false;try{undone=host.Undo();}catch(DocumentHistory.MismatchException){Check(false,"Update with "+count+" changes produced a mismatched undo entry (pass "+pass+")");}
            Check(undone&&document.Text==original,"One undo did not restore the text before a "+count+"-change update (pass "+pass+")");
            Check(!host.History.CanUndo,"Update with "+count+" changes split into several undo steps (pass "+pass+")");
            Check(host.Editor.SelectionStart==5&&host.Editor.SelectionLength==3,"Undo did not restore the selection the update started with ("+count+","+pass+")");
            bool redone=false;try{redone=host.Redo();}catch(DocumentHistory.MismatchException){Check(false,"Redo of a "+count+"-change update raised MismatchException (pass "+pass+")");}
            Check(redone&&document.Text==changed,"One redo did not reapply a "+count+"-change update (pass "+pass+")");
            Check(host.Editor.SelectionStart==afterStart&&host.Editor.SelectionLength==afterLength,"Redo did not restore the selection the update ended with ("+count+","+pass+")");
        }
    }
    {
        string original=HistoryGroupText(3);
        var host=new HistoryProbeHost(original);var document=host.Editor.Document;
        int at=Math.Min(4,document.TextLength);
        // Typed characters: the host arms the marker for each key, so the three inserts build one
        // typing run and the undo below takes them back together.
        host.Editor.SimulateTypedKey();document.Insert(at,"a");
        host.Editor.SimulateTypedKey();document.Insert(at+1,"b");
        host.Editor.SimulateTypedKey();document.Insert(at+2,"c");
        host.Editor.Select(at+3,0);
        Check(host.History.CanUndo&&!host.History.CanRedo,"Typing probe did not produce one typing entry");
        string typed=document.Text;
        document.BeginUpdate();
        try{
            // U2: the first change is a 1-char insert contiguous with the typing run. This update is
            // not typing (no typed marker), so it must be its own step and leave the run whole; the
            // provisional entry it pushes must be taken back without extending that run.
            document.Insert(at+3,"d");document.Insert(at+4,"e");
            host.Editor.Select(at+1,3);
        }finally{document.EndUpdate();}
        string updated=document.Text;
        Check(updated==typed.Insert(at+3,"de"),"Two-change typing update applied the wrong text");
        bool undone=false;try{undone=host.Undo();}catch(DocumentHistory.MismatchException){Check(false,"Two-change update merged into typing produced a mismatched undo entry");}
        Check(undone&&document.Text==typed,"Undo of a two-change update did not leave the earlier typing alone");
        Check(host.History.CanUndo,"Undo of a two-change update consumed the typing entry");
        Check(host.Editor.SelectionStart==at+3&&host.Editor.SelectionLength==0,"Undo of a two-change update restored the wrong selection");
        host.Undo();
        Check(document.Text==original&&!host.History.CanUndo,"The preceding typing run no longer undoes as one step");
        host.Redo();Check(document.Text==typed,"Redo of the typing run failed");
        host.Redo();Check(document.Text==updated&&host.Editor.SelectionStart==at+1&&host.Editor.SelectionLength==3,"Redo of the two-change update lost its text or selection");
    }
    {
        string original=HistoryGroupText(4);
        var host=new HistoryProbeHost(original);var document=host.Editor.Document;
        int at=original.IndexOf('\n')+1;host.Editor.Select(at,0);
        document.BeginUpdate();
        try{document.Insert(at,"\n");document.Insert(at+1,"    ");}finally{document.EndUpdate();}
        Check(document.Text==original.Insert(at,"\n    "),"Newline plus indentation applied the wrong text");
        Check(host.History.CanUndo,"Newline plus indentation recorded no entry");
        host.Undo();
        Check(document.Text==original&&!host.History.CanUndo,"Newline plus 4 spaces indentation split into two undo steps");
    }
    {
        string original="aa  lineone\nbb    linetwo\ncc   linethree\ndd  linefour\n";
        var host=new HistoryProbeHost(original);var document=host.Editor.Document;
        int line2=original.IndexOf("bb",StringComparison.Ordinal),line3=original.IndexOf("cc",StringComparison.Ordinal);
        // Two Backspace presses build one delete run that ends exactly at the next line's indent.
        document.Remove(line2+5,1);document.Remove(line2+4,1);
        string afterBackspace=document.Text;
        int start2=afterBackspace.IndexOf("bb",StringComparison.Ordinal)+2,start3=afterBackspace.IndexOf("cc",StringComparison.Ordinal);
        Check(start2==line2+2&&start3==line3-2,"Shift+Tab probe positions moved");
        document.BeginUpdate();
        try{document.Remove(start2,2);document.Remove(start3-2,2);}finally{document.EndUpdate();}
        string afterShiftTab=afterBackspace.Remove(start2,2).Remove(start3-2,2);
        Check(document.Text==afterShiftTab,"Shift+Tab probe applied the wrong text");
        Check(host.History.CanUndo,"Shift+Tab update recorded no entry");
        bool undone=false;try{undone=host.Undo();}catch(DocumentHistory.MismatchException){Check(false,"Shift+Tab after a Backspace run produced a mismatched undo entry");}
        Check(undone&&document.Text==afterBackspace,"Undo of a Shift+Tab update did not restore the text before it");
        Check(host.History.CanUndo,"Undo of a Shift+Tab update consumed the earlier Backspace run");
        host.Undo();
        Check(document.Text==original&&!host.History.CanUndo,"The Backspace run no longer undoes as one step after a Shift+Tab update");
        host.Redo();host.Redo();
        Check(document.Text==afterShiftTab,"Redo of the Backspace run plus Shift+Tab update failed");
    }
    {
        // H1: the first change of a multi-change update is pushed at once and clears the redo stack;
        // an update that ends where it started must put the redo stack back, not lose it.
        var host=new HistoryProbeHost("abcdef");var document=host.Editor.Document;
        document.Insert(0,"X");document.Insert(0,"Y");
        string before=document.Text;
        bool undone=host.Undo();
        Check(undone&&document.Text=="Xabcdef"&&host.History.CanRedo,"No-op redo probe setup failed");
        string afterUndo=document.Text;
        document.BeginUpdate();
        try{document.Insert(0,"AB");document.Remove(0,2);}finally{document.EndUpdate();}
        Check(document.Text==afterUndo,"A net no-op update changed the text");
        Check(host.History.CanRedo,"A net no-op update dropped the redo stack");
        bool redone=false;try{redone=host.Redo();}catch(DocumentHistory.MismatchException){Check(false,"Redo after a net no-op update raised MismatchException");}
        Check(redone&&document.Text==before,"Redo after a net no-op update lost its text");
        host.Undo();host.Undo();
        Check(document.Text=="abcdef"&&!host.History.CanUndo&&host.History.CanRedo,"The undo stack changed around a net no-op update");
    }
    {
        // The provisional push also trims the oldest entries at the cap; the no-op update must put
        // those back too. Nine 500k inserts fill the 8 MB cap (the first insert is already gone),
        // then a 1M-char insert taken back in the no-op update pushes over the cap again.
        var host=new HistoryProbeHost("seed");var document=host.Editor.Document;
        var chunk=new string('q',500000);
        for(int i=0;i<9;i++)document.Insert(document.TextLength,chunk);
        bool undone=host.Undo();
        string afterUndo=document.Text;
        Check(undone&&afterUndo!="seed"&&host.History.CanRedo,"Byte cap probe setup failed");
        var big=new string('w',1000000);
        document.BeginUpdate();
        try{document.Insert(0,big);document.Remove(0,big.Length);}finally{document.EndUpdate();}
        Check(document.Text==afterUndo,"The byte cap no-op update changed the text");
        int steps=0;while(host.History.CanUndo&&steps<16){host.Undo();steps++;}
        Check(document.Text=="seed"+chunk,"A net no-op update lost the oldest entry the provisional push trimmed");
    }
    {
        var seedText=new StringBuilder();var seedRandom=new Random(7717);
        for(int i=0;i<48;i++){seedText.Append("fuzz ").Append(i).Append(": ");for(int c=0;c<30;c++)seedText.Append("abcdefghij"[seedRandom.Next(10)]);seedText.Append('\n');}
        var host=new HistoryProbeHost(seedText.ToString());var document=host.Editor.Document;
        var snapshots=new System.Collections.Generic.List<string>();snapshots.Add(document.Text);
        var random=new Random(20261026);
        for(int update=0;update<300;update++){
            document.BeginUpdate();
            try{
                int changes=1+random.Next(6);
                for(int c=0;c<changes;c++){
                    int length=document.TextLength;
                    if(length<2){document.Insert(0,"pad");continue;}
                    int changeAt=random.Next(length+1);
                    int remove=random.Next(Math.Min(12,length-changeAt)+1);
                    int insertLength=1+random.Next(6);
                    var builder=new StringBuilder(insertLength+3);
                    for(int k=0;k<insertLength;k++)builder.Append("abcdefghij"[random.Next(10)]);
                    // Keep a lone insert longer than the 1-2 char typing merge so every changed
                    // update is its own entry and the snapshots line up with the undo stack.
                    if(remove==0&&builder.Length<=2)builder.Append("xyz");
                    document.Replace(changeAt,remove,builder.ToString());
                }
            }finally{document.EndUpdate();}
            string now=document.Text;
            if(now!=snapshots[snapshots.Count-1])snapshots.Add(now);
        }
        Check(snapshots.Count>250,"Fuzz history probe produced too few distinct states: "+snapshots.Count);
        int index=snapshots.Count-1;
        while(host.History.CanUndo&&index>0){bool step=false;try{step=host.Undo();}catch(DocumentHistory.MismatchException){Check(false,"Fuzz undo raised MismatchException at state "+index);}Check(step&&document.Text==snapshots[index-1],"Fuzz undo produced the wrong text at state "+index);index--;}
        Check(index==0&&document.Text==snapshots[0]&&!host.History.CanUndo,"Fuzz undo-all did not reach the original text");
        while(host.History.CanRedo&&index+1<snapshots.Count){bool step=false;try{step=host.Redo();}catch(DocumentHistory.MismatchException){Check(false,"Fuzz redo raised MismatchException at state "+index);}index++;Check(step&&document.Text==snapshots[index],"Fuzz redo produced the wrong text at state "+index);}
        Check(index==snapshots.Count-1&&document.Text==snapshots[snapshots.Count-1]&&!host.History.CanRedo,"Fuzz redo-all did not reach the final text");
    }
    {
        // Typing over a selection that spans two table cells rewrites every selected cell in one
        // update (remove, remove, then the typed key). That replace is its own undo step, and a
        // Backspace right after it starts a new step instead of taking the key back out of it.
        string original="| aa | bb |\n| --- | --- |\n| cc | dd |\n";
        var host=new HistoryProbeHost(original);var document=host.Editor.Document;
        host.Editor.Select(2,5);
        host.Editor.SimulateTypedKey();
        document.BeginUpdate();
        try{document.Remove(7,2);document.Remove(2,2);document.Insert(2,"x");host.Editor.Select(3,0);}
        finally{document.EndUpdate();}
        string replaced=document.Text;
        Check(replaced=="| x |  |\n| --- | --- |\n| cc | dd |\n","Multi-cell table replace probe applied the wrong text");
        Check(host.History.CanUndo,"Multi-cell table replace recorded no undo entry");
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Remove(2,1);host.Editor.Select(2,0);}
        Check(document.Text=="|  |  |\n| --- | --- |\n| cc | dd |\n","Backspace after a multi-cell replace removed the wrong text");
        bool undone=host.Undo();
        Check(undone&&document.Text==replaced&&host.History.CanUndo,"Backspace after a multi-cell replace merged into the replace step");
        host.Undo();
        Check(document.Text==original&&!host.History.CanUndo,"The multi-cell table replace was not its own undo step");
    }
    {
        // Typing after that replace starts a run of its own; the Backspace after that typing is a
        // step of its own too (the deleted character comes back first), never part of the replace.
        string original="| aa | bb |\n| --- | --- |\n| cc | dd |\n";
        var host=new HistoryProbeHost(original);var document=host.Editor.Document;
        host.Editor.Select(2,5);
        host.Editor.SimulateTypedKey();
        document.BeginUpdate();
        try{document.Remove(7,2);document.Remove(2,2);document.Insert(2,"x");host.Editor.Select(3,0);}
        finally{document.EndUpdate();}
        string replaced=document.Text;
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(3,"y");host.Editor.Select(4,0);}
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(4,"z");host.Editor.Select(5,0);}
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Remove(4,1);host.Editor.Select(4,0);}
        Check(document.Text==replaced.Insert(3,"y"),"Typing and Backspace after a multi-cell replace changed the wrong text");
        bool undone=host.Undo();
        Check(undone&&document.Text==replaced.Insert(3,"yz")&&host.History.CanUndo,"Undo after Backspace did not bring the deleted character back");
        host.Undo();
        Check(document.Text==replaced&&host.History.CanUndo,"The typing after the replace was not its own undo step");
        host.Undo();
        Check(document.Text==original&&!host.History.CanUndo,"Typing after a multi-cell replace did not start its own step");
    }
    {
        // A paste is its own step: a Backspace right after it starts a new step instead of taking
        // text back out of the paste, and the typing run before the paste stays separate too.
        var host=new HistoryProbeHost("");var document=host.Editor.Document;
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(0,"ab");host.Editor.Select(2,0);}
        host.Editor.BreakTypingRun();
        using(document.RunUpdate()){document.Insert(2,"hello");host.Editor.Select(7,0);}
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Remove(6,1);host.Editor.Select(6,0);}
        Check(document.Text=="abhell","Paste then Backspace probe changed the wrong text");
        bool undone=host.Undo();
        Check(undone&&document.Text=="abhello"&&host.History.CanUndo,"Backspace after a paste merged into the paste step");
        host.Undo();
        Check(document.Text=="ab"&&host.History.CanUndo,"The paste was not its own undo step");
        host.Undo();
        Check(document.Text==""&&!host.History.CanUndo,"The typing run before the paste was not its own step");
    }
    {
        // An IME commit is typing, but the Backspace right after it is the next step: the first undo
        // brings the committed syllable back and the next one takes the commit away, like Notepad.
        var host=new HistoryProbeHost("");var document=host.Editor.Document;
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(0,"한");host.Editor.Select(1,0);}
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Remove(0,1);host.Editor.Select(0,0);}
        Check(document.Text=="","IME commit then Backspace probe changed the wrong text");
        Check(host.History.CanUndo,"Backspace after an IME commit left no undo step");
        bool undone=host.Undo();
        Check(undone&&document.Text=="한"&&host.History.CanUndo,"Undo after Backspace did not restore the committed syllable");
        bool undone2=host.Undo();
        Check(undone2&&document.Text==""&&!host.History.CanUndo,"The IME commit was not its own undo step");
    }
    {
        // Delete ends the typing run: the text typed after it is a new step, and the run before it
        // stays whole.
        var host=new HistoryProbeHost("");var document=host.Editor.Document;
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(0,"ab");host.Editor.Select(2,0);}
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(2,"c");host.Editor.Select(3,0);}
        host.Editor.BreakTypingRun();
        using(document.RunUpdate()){document.Remove(0,1);host.Editor.Select(0,0);}
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(0,"x");host.Editor.Select(1,0);}
        Check(document.Text=="xbc","Delete then typing probe changed the wrong text");
        bool undone=host.Undo();
        Check(undone&&document.Text=="bc"&&host.History.CanUndo,"Typing after Delete merged into the Delete step");
        host.Undo();
        Check(document.Text=="abc"&&host.History.CanUndo,"The typing run before Delete was not its own step");
        host.Undo();
        Check(document.Text==""&&!host.History.CanUndo,"The first typing run was not its own step");
    }
    {
        // A Delete that changed nothing still ends the run: the next typed character is a step of
        // its own.
        var host=new HistoryProbeHost("");var document=host.Editor.Document;
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(0,"ab");host.Editor.Select(2,0);}
        host.Editor.BreakTypingRun();
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(2,"c");host.Editor.Select(3,0);}
        Check(document.Text=="abc","Delete that changed nothing probe changed the wrong text");
        bool undone=host.Undo();
        Check(undone&&document.Text=="ab"&&host.History.CanUndo,"A Delete that changed nothing did not end the typing step");
        host.Undo();
        Check(document.Text==""&&!host.History.CanUndo,"The typing run before the no-op Delete was lost");
    }
    {
        // Typing and then Backspacing it is two steps, the way Windows Notepad and Word behave: the
        // first undo brings the deleted characters back, the next one takes the typing back. The
        // Backspaces among themselves still group into one deletion entry.
        var host=new HistoryProbeHost("");var document=host.Editor.Document;
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(0,"abcdef");host.Editor.Select(6,0);}
        for(int i=0;i<6;i++){
            host.Editor.SimulateTypedKey();
            using(document.RunUpdate()){document.Remove(document.TextLength-1,1);host.Editor.Select(document.TextLength,0);}
        }
        Check(document.Text==""&&host.History.CanUndo,"Typing and Backspacing it left no undo step");
        bool undone=host.Undo();
        Check(undone&&document.Text=="abcdef"&&host.History.CanUndo,"Undo after Backspace did not restore the deleted text");
        bool undone2=host.Undo();
        Check(undone2&&document.Text==""&&!host.History.CanUndo,"The typing run was not its own undo step after Backspace");
        bool redone=host.Redo();
        Check(redone&&document.Text=="abcdef"&&host.History.CanRedo,"Redo did not put the typing back");
        bool redone2=host.Redo();
        Check(redone2&&document.Text==""&&!host.History.CanRedo,"Redo did not take the deleted text back out");
    }
    {
        // The monkey's oracle shape: typing runs that were typed and Backspaced away between real
        // edits. Every undo step must change the text, undo-all must reach the start text and
        // redo-all the last one. A run and the Backspace that removes it are two steps now, and the
        // real edit after them a third.
        string start=HistoryGroupText(2);
        var host=new HistoryProbeHost(start);var document=host.Editor.Document;
        var states=new System.Collections.Generic.List<string>();states.Add(document.Text);int expected=0;
        for(int run=0;run<3;run++){
            // A caret move starts the taken-back run as its own step (the monkey's caret actions).
            host.Editor.BreakTypingRun();
            host.Editor.SimulateTypedKey();
            using(document.RunUpdate()){document.Insert(0,"q");host.Editor.Select(1,0);}
            host.Editor.SimulateTypedKey();
            using(document.RunUpdate()){document.Remove(0,1);host.Editor.Select(0,0);}
            Check(String.Equals(document.Text,states[run],StringComparison.Ordinal),"A taken-back typing run changed the text (run "+run+")");
            expected+=2;
            host.Editor.BreakTypingRun();
            host.Editor.Select(document.TextLength,0);
            host.Editor.SimulateTypedKey();
            using(document.RunUpdate()){document.Insert(document.TextLength,"R");host.Editor.Select(document.TextLength,0);}
            Check(host.History.CanUndo,"A real edit after a taken-back run recorded no entry (run "+run+")");
            expected++;
            states.Add(document.Text);
        }
        string previous=document.Text;
        int steps=0;
        while(host.History.CanUndo&&steps<64){
            bool step=host.Undo();steps++;
            Check(step&&!String.Equals(document.Text,previous,StringComparison.Ordinal),"An undo step changed nothing after "+steps+" step(s)");
            previous=document.Text;
        }
        Check(steps==expected&&String.Equals(document.Text,start,StringComparison.Ordinal)&&!host.History.CanUndo,"Undo-all did not reach the start text after taken-back runs");
        steps=0;
        while(host.History.CanRedo&&steps<64){
            bool step=host.Redo();steps++;
            Check(step&&!String.Equals(document.Text,previous,StringComparison.Ordinal),"A redo step changed nothing after "+steps+" step(s)");
            previous=document.Text;
        }
        Check(steps==expected&&String.Equals(document.Text,states[states.Count-1],StringComparison.Ordinal)&&!host.History.CanRedo,"Redo-all did not replay the edits after taken-back runs");
    }
    {
        // A host rearranging its own run's text with one typed key is folded into the run. When
        // that key puts the run's original text back, the entry is a no-op and must be dropped.
        var host=new HistoryProbeHost("xy");var document=host.Editor.Document;
        host.Editor.Select(0,2);
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Replace(0,2,"ab");host.Editor.Select(2,0);}
        Check(document.Text=="ab"&&host.History.CanUndo,"Typed replacement probe recorded no entry");
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Replace(0,2,"xy");host.Editor.Select(2,0);}
        Check(document.Text=="xy","Typed replacement probe changed the wrong text");
        Check(!host.History.CanUndo,"A typed replacement that restored the original text left a no-op undo step");
    }
    {
        // The same cancel as a two-change typed update: the first change folds into the run and
        // the group then splices the run's own text back (FoldTypedGroup), leaving a no-op entry.
        var host=new HistoryProbeHost("xy");var document=host.Editor.Document;
        host.Editor.Select(0,2);
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Replace(0,2,"ab");host.Editor.Select(2,0);}
        Check(document.Text=="ab"&&host.History.CanUndo,"Folded replacement probe recorded no entry");
        host.Editor.SimulateTypedKey();
        document.BeginUpdate();
        try{document.Replace(0,2,"zz");document.Replace(0,2,"xy");host.Editor.Select(2,0);}finally{document.EndUpdate();}
        Check(document.Text=="xy","Folded typed replacement probe changed the wrong text");
        Check(!host.History.CanUndo,"A folded typed replacement that restored the original text left a no-op undo step");
    }
    {
        // A Backspace that deletes one character of the run is its own step on both sides: undo puts
        // the character back, the next undo removes the typing, and redo replays both in order.
        var host=new HistoryProbeHost("");var document=host.Editor.Document;
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Insert(0,"abc");host.Editor.Select(3,0);}
        host.Editor.SimulateTypedKey();
        using(document.RunUpdate()){document.Remove(2,1);host.Editor.Select(2,0);}
        Check(document.Text=="ab"&&host.History.CanUndo,"Partial Backspace probe recorded no entry");
        bool undone=host.Undo();
        Check(undone&&document.Text=="abc"&&host.History.CanUndo,"Undo after Backspace did not restore the deleted character");
        bool undone2=host.Undo();
        Check(undone2&&document.Text==""&&!host.History.CanUndo,"Undo of the typing run did not restore its original text");
        bool redone=host.Redo();
        Check(redone&&document.Text=="abc"&&host.History.CanRedo,"Redo did not put the typing back");
        bool redone2=host.Redo();
        Check(redone2&&document.Text=="ab"&&!host.History.CanRedo,"Redo did not take the deleted character back out");
    }
}
// Owns the editor/history pair the grouping probe drives: ContentDelta records deltas exactly the
// way MainWindow does, and the loading flag keeps ApplySource's own document update out of history.
sealed class HistoryProbeHost {
    internal readonly DocumentHistory History=new DocumentHistory();
    internal readonly SourceEditor Editor;
    internal bool Loading;
    internal HistoryProbeHost(string text){
        Editor=new SourceEditor {Text=text};var target=History;var host=this;
        Editor.ContentDelta+=delegate(object sender,ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs change){if(!host.Loading)target.RecordDelta(change.Offset,change.RemovedText.Text,change.InsertedText.Text);};
    }
    internal bool Undo(){Loading=true;try{return History.ApplySource(false,Editor);}finally{Loading=false;}}
    internal bool Redo(){Loading=true;try{return History.ApplySource(true,Editor);}finally{Loading=false;}}
}
static string HistoryGroupText(int lines){
    var builder=new StringBuilder();
    for(int i=0;i<lines;i++)builder.Append("line ").Append(i).Append(": alpha beta gamma delta\n");
    return builder.ToString();
}
internal void VerifyRC9(string scratch){
VerifyFieldShortcutIsolation();VerifyCompoundHistory();VerifyDocumentHistoryGroups();VerifyLazySourceHistory();VerifyBackgroundOutlineSync();VerifyAdaptiveGutter();VerifyDeepHtmlPaste();VerifyClipboardByteOffsets();VerifyLargeSourceCaretSpacing();VerifyAtomicSaveFailures(scratch);VerifyHistoryBranchBudget();VerifyReferenceFenceBoundaries();VerifyParallelSearch();VerifyExplicitEncoding(scratch);
string prefix=Path.Combine(scratch,"rc9-"+Guid.NewGuid().ToString("N"));string file=prefix+".txt",copy=prefix+"-copy.txt";
try{
File.WriteAllText(file,"BASE",new UTF8Encoding(false));LoadFileCore(file);source.AppendText(" LOCAL");File.WriteAllText(file,"ELSE",new UTF8Encoding(false));bool conflict=false;try{SaveToPath(file,StrictEncoding(0),false);}catch(SaveConflictException){conflict=true;}Check(conflict&&File.ReadAllText(file)=="ELSE"&&text=="BASE LOCAL"&&saved=="BASE","External edit conflict overwrote disk or lost unsaved edits");Check(SaveToPath(copy,StrictEncoding(0),false)&&File.ReadAllText(copy)=="BASE LOCAL","Conflict Save As failed");
LoadFileCore(file);source.AppendText(" FIRST");var second=new MainWindow();try{second.LoadFileCore(file);second.source.AppendText(" SECOND");SaveToPath(file,StrictEncoding(0),false);conflict=false;try{second.SaveToPath(file,StrictEncoding(0),false);}catch(SaveConflictException){conflict=true;}Check(conflict&&File.ReadAllText(file)=="ELSE FIRST"&&second.text=="ELSE SECOND","Two windows overwrote each other");}finally{second.saved=second.text;second.CaptureTab();second.Close();}
LoadFileCore(file);source.AppendText(" retained");File.Delete(file);conflict=false;try{SaveToPath(file,StrictEncoding(0),false);}catch(SaveConflictException){conflict=true;}Check(conflict&&!File.Exists(file)&&text.Contains("retained"),"Deleted file conflict failed");
VerifyRecovery(scratch);File.WriteAllText(prefix+"-PASS.txt","PASS: external edit/deletion/two-window conflict and Save As; history grouping; outline; gutter; recovery; encoding; parallel search.");
}finally{path="";documentFingerprint=documentFingerprintPath=null;textMode=false;SetText("");saved=text;if(File.Exists(file))File.Delete(file);if(File.Exists(copy))File.Delete(copy);}
}
}}


