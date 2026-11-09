using System;using System.Text;using System.Windows;using System.Windows.Controls;using System.Windows.Documents;
namespace MDNote {public partial class MainWindow {
bool? textMode;static bool newDocumentsText,alwaysEditing;MenuItem textModeMenu,newTextMenu,alwaysEditMenu;Button quickText;FrameworkElement quickTextLabel;FrameworkElement quickTextIcon;bool quickTextCompact;
// TXT->MD escapes every character that would read back as markup as an entity, and a backslash
// becomes &#92; as well: a raw "\" would eat the character after it (`\*` lost its backslash on
// the way back to TXT). The backslash replace must follow the "&" replace, otherwise the entity it
// writes is escaped again. The live preview and the print parser decode the entity after their
// escape scan (MarkdownLivePreview.ParseInline, Markdown.Inline), so both draw the characters TXT had.
// 11-01 user: TXT->MD wrote "[사진 1]" back as "&#91;사진 1&#93;". Escaping is needed only where the text would read as markup: a line
// whose plain conversion already gives the same text stays as typed, and only the others are escaped. If the whole result then reads
// back differently (a line that turns the one above into a heading, a fence...), every line is escaped as before.
static string LiteralMarkdown(string value){return MinimalLiteral(value,FullLiteralMarkdown);}
static string MinimalLiteral(string value,Func<string,string> full){
    string all=full(value);if(value.Length>256*1024)return all;
    try{
        string[] lines=value.Split('\n');var result=new StringBuilder(value.Length+16);
        for(int i=0;i<lines.Length;i++){if(i>0)result.Append('\n');string line=lines[i];string body=line.TrimEnd('\r');
            bool plainLine=body.Length==0||Canonical(MarkdownPlainText.Convert(body))==Canonical(MarkdownPlainText.Convert(full(body)));
            result.Append(plainLine?line:full(line));}
        string minimal=result.ToString();
        return Canonical(MarkdownPlainText.Convert(minimal))==Canonical(MarkdownPlainText.Convert(all))?minimal:all;
    }catch(Exception){return all;}
}
static string FullLiteralMarkdown(string value){string escaped=value.Replace("&","&amp;").Replace("#","&#35;").Replace("<","&lt;").Replace(">","&gt;").Replace("*","&#42;").Replace("_","&#95;").Replace("~","&#126;").Replace("[","&#91;").Replace("]","&#93;").Replace("`","&#96;").Replace("-","&#45;").Replace("+","&#43;").Replace("=","&#61;").Replace("\\","&#92;");escaped=System.Text.RegularExpressions.Regex.Replace(escaped,@"(?m)^[ \t]+(?=\r?$)",delegate(System.Text.RegularExpressions.Match m){return m.Value.Replace(" ","&#32;").Replace("\t","&#9;");});return System.Text.RegularExpressions.Regex.Replace(escaped,@"(?m)^(\s*\d+)([.)])(?=\s|$)",delegate(System.Text.RegularExpressions.Match m){return m.Groups[1].Value+(m.Groups[2].Value=="."?"&#46;":"&#41;");});}
static string ReadableText(FlowDocument document){var result=new StringBuilder();PlainBlocks(document.Blocks,result);if(result.Length>0)result.Length--;return Canonical(result.ToString());}
static void PlainBlocks(BlockCollection blocks,StringBuilder result){PlainBlocks(blocks,result,0);}
static void PlainBlocks(BlockCollection blocks,StringBuilder result,int depth){foreach(Block block in blocks){if(MarkdownWriter.IsAutoTableGap(block))continue;var paragraph=block as Paragraph;if(paragraph!=null){result.Append(ParagraphBody(paragraph.Inlines)).Append('\n');}else if(block is Section)PlainBlocks(((Section)block).Blocks,result,depth);else if(block is System.Windows.Documents.List){var list=(System.Windows.Documents.List)block;int number=list.StartIndex;foreach(ListItem item in list.ListItems){var content=new StringBuilder();PlainBlocks(item.Blocks,content,depth+1);string task=item.Tag as string;bool isTask=MarkdownWriter.TaskItem(item);string marker=isTask?(task=="task:x"?"☑ ":"☐ "):list.MarkerStyle==TextMarkerStyle.Decimal?(number+". "):list.MarkerStyle==TextMarkerStyle.None?"":"- ";result.Append(new string(' ',depth*2)).Append(marker).Append(content);number++;}}else if(block is Table){foreach(TableRowGroup group in ((Table)block).RowGroups)foreach(TableRow row in group.Rows){bool first=true;foreach(TableCell cell in row.Cells){if(!first)result.Append('\t');first=false;var content=new StringBuilder();PlainBlocks(cell.Blocks,content,depth);result.Append(content.ToString().TrimEnd('\n').Replace("\n"," "));}result.Append('\n');}}}}

/* 2026-11-09 (user): the TXT toggle moved from the top toolbar to the status bar's right end. The
   command, the Tag=Active state and the shortcut/menu entry are the old ones; only the home and the
   chrome changed. It is docked Right, so the status text growing or shrinking never moves it, and
   the wide face is the bare "TXT" label (a file type, never translated). The chrome is the 목차
   button's FormatViewButton: 22 px high, 1 px ButtonLine outline, 6 px radius, the shared
   hover/pressed scales and the app's ActiveFill/ActiveInk pair when TXT mode is on. */
void BuildTextQuickButton(){
    var row=statusBar==null?null:statusBar.Child as DockPanel;if(row==null)return;
    /* 11-09 (user): the bare "TXT" left the box half empty; the T glyph now leads like the 목차/원문 glyphs, and the label is "TXT" beside a page-with-lines glyph (user: "T .txt" read oddly) */
    var txtGlyph=Ui.CenteredInkIcon("txtDoc",16,0.5);txtGlyph.Margin=new Thickness(0,0,4,0);txtGlyph.VerticalAlignment=VerticalAlignment.Center;
    var txtRow=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};txtRow.Children.Add(txtGlyph);txtRow.Children.Add(new TextBlock {Text="TXT",VerticalAlignment=VerticalAlignment.Center,RenderTransform=new System.Windows.Media.TranslateTransform(0,1)});/* capitals without descenders read high beside the Hangul labels: 1 px down (user 11-09) */txtRow.RenderTransform=new System.Windows.Media.TranslateTransform(-0.5,0);/* user 11-09: a half pixel left */quickTextLabel=txtRow;
    quickText=new Button {Content=quickTextLabel,Focusable=false,UseLayoutRounding=true,SnapsToDevicePixels=true,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right};
    quickText.Style=(Style)Resources["FormatViewButton"];quickText.Margin=new Thickness(0);
    quickText.ToolTip=TextToggleTip();
    System.Windows.Automation.AutomationProperties.SetName(quickText,T("TXT 모드"));
    quickText.Click+=delegate{ToggleTextMode();};
    DockPanel.SetDock(quickText,Dock.Right);
    /* before the status text, the LastChildFill child, so the button keeps the bar's right edge */
    row.Children.Insert(Math.Max(0,row.Children.Count-1),quickText);
    /* the top row is the host 목차 reads, so this toggle crosses 520 px at the same moment; the bar
       itself would be narrower while the 목차 panel is open */
    statusBar.SizeChanged+=delegate{UpdateTextModeButtonLayout();};
    UpdateTextModeButtonLayout();UpdateTextModeMenu();PolishToolbar();
}
/* Same compact rule as Ui.CompactToggleLabel: below CompactToggleHostWidth the label gives way to
   the existing T glyph alone in the 22 px box the collapsed 원문/목차 toggles keep. */
void UpdateTextModeButtonLayout(){
    if(quickText==null)return;
    FrameworkElement host=topBarHost!=null&&topBarHost.ActualWidth>0?topBarHost:statusBar;
    double width=host==null?0:host.ActualWidth;
    /* before the first layout (or while minimised) there is no width to judge: keep the current face */
    if(width<=0)return;
    bool compact=width<Ui.CompactToggleHostWidth;
    if(compact==quickTextCompact)return;
    quickTextCompact=compact;
    if(compact){
        if(quickTextIcon==null){quickTextIcon=Ui.CenteredInkIcon("txtT",14,0.5);quickTextIcon.HorizontalAlignment=HorizontalAlignment.Center;/* whole pixels only (a half-pixel shift smeared the strokes, user 11-09): centred across, 1 px low for the heavy bar */}/* ink-sized like the collapsed 원문 braces, so the T sits in the box centre (the scaled 24 px "type" slot leaned top-left) */
        quickText.Padding=new Thickness(0);quickText.MinWidth=Ui.CompactToggleBox;quickText.Content=quickTextIcon;
    }else{
        quickText.ClearValue(Control.PaddingProperty);quickText.ClearValue(FrameworkElement.MinWidthProperty);quickText.Content=quickTextLabel;SyncViewButtonWidths();
    }
}
void ToggleTextMode(){if(StoreGuardLive()){source.RunAfterComposition(ToggleTextModeNow);return;}ToggleTextModeNow();}
// Conversion keeps the same source surface and records one mode-aware undo step.
void ToggleTextModeNow(){if(documentOperationBusy)return;bool beforeEditing=editing,beforePreview=preview.IsChecked==true;if(!editing)SetMode(true);FinishComposition();FlushDocumentChanges();if(source.Document.TextLength>=256*1024){QueueModeConversion();return;}bool beforeMode=PlainText;string before=text;int caret=beforeMode?Canonical(before.Substring(0,source.CaretIndex)).Length:LivePlainCaret(before,source.CaretIndex,T("[이미지: "));string after=beforeMode?LiteralMarkdown(before):MarkdownPlainText.Convert(before,T("[이미지: "));CommitTextMode(beforeMode,before,after,caret,beforeEditing,beforePreview);}

// A format-changing save converts the tab first and writes the file second. When the conversion
// runs for a save, CommitTextMode holds its one-step mode entry instead of recording it: a failed
// or cancelled write then puts the exact pre-conversion text, mode, reading/raw view, caret and
// selection back without touching the undo/redo stacks the user already had (History.Record would
// clear redo).
bool saveConversionDeferred,saveConversionHeld,saveConversionBeforeMode;string saveConversionBefore,saveConversionAfter;int saveConversionCaret,saveConversionSelectionStart,saveConversionSelectionLength;DocumentTab saveConversionDocument;bool saveConversionBeforeEditing,saveConversionBeforePreview;
void CommitTextMode(bool beforeMode,string before,string after,int caret,bool beforeEditing,bool beforePreview){
    BeginViewTransition();CancelEditorRefresh();
    if(saveConversionDeferred){
        saveConversionHeld=true;saveConversionBeforeMode=beforeMode;saveConversionBefore=before;saveConversionAfter=after;
        saveConversionCaret=source.CaretIndex;saveConversionSelectionStart=source.SelectionStart;saveConversionSelectionLength=source.SelectionLength;saveConversionDocument=activeDocument;
        saveConversionBeforeEditing=beforeEditing;saveConversionBeforePreview=beforePreview;
    }else History.Record(before,after,beforeMode,!beforeMode);
    textMode=!beforeMode;text=after;loading=true;preview.IsChecked=false;loading=false;Render();LayoutMode();if(PlainText)source.CaretIndex=ActualSourceOffset(after,Math.Min(caret,Canonical(after).Length));else PlaceLiveLiteralCaret(before,caret);source.Focus();PositionCaretInView();Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{PositionCaretInView();EndViewTransition();}));UpdateTextModeMenu();CaptureTab();}
async System.Threading.Tasks.Task ConvertTextModeAsync(System.Threading.CancellationToken cancellation){
    bool beforeEditing=editing,beforePreview=preview.IsChecked==true;
    if(!editing)SetMode(true);FinishComposition();FlushDocumentChanges();var state=activeDocument;var editor=source;var document=source.Document;string signature=ViewSignature;bool beforeMode=PlainText;var version=document.Version;int rawCaret=source.CaretIndex;
    var snapshot=document.CreateSnapshot();
    /* The busy guard goes up before the first await: reading a large buffer takes long enough for
       typing, a second toggle or an image drop to run on a document that is being converted. The
       drain unlocks it when the operation ends, cancel included. */
    string before,after;int caret;
    try{
        EnsureFileOpenOverlay();fileOpenLabel.Text=T("형식 변환 중…");
        string imageLabel=T("[이미지: ");
        before=await System.Threading.Tasks.Task.Run(delegate{return snapshot.Text;},cancellation);
        caret=await System.Threading.Tasks.Task.Run(delegate{return beforeMode?Canonical(before.Substring(0,Math.Min(rawCaret,before.Length))).Length:LivePlainCaret(before,rawCaret,imageLabel);},cancellation);
        after=await System.Threading.Tasks.Task.Run(delegate{return beforeMode?LiteralMarkdown(before):MarkdownPlainText.Convert(before,imageLabel);},cancellation);
        cancellation.ThrowIfCancellationRequested();
    }catch(Exception){
        /* A cancelled or failed conversion must not leave the automatic edit-mode switch behind. */
        if(ConversionViewCurrent(state,editor,document))RestoreTextModeView(beforeEditing,beforePreview);
        throw;
    }
    if(fileOpenClosed||state!=activeDocument||signature!=ViewSignature||!Object.ReferenceEquals(editor,source)||!Object.ReferenceEquals(document,source.Document)||!Object.ReferenceEquals(version,source.Document.Version)){if(ConversionViewCurrent(state,editor,document))RestoreTextModeView(beforeEditing,beforePreview);ReportAbortedTextConversion();return;}
    CommitTextMode(beforeMode,before,after,caret,beforeEditing,beforePreview);
}
// A conversion switches the tab to edit mode and clears the raw view for its render; a rollback
// or a conversion that never committed puts the reading/raw state back with the text. The state
// was captured before the automatic SetMode, so the pre-conversion view is what comes back.
void RestoreTextModeView(bool beforeEditing,bool beforePreview){
    if(editing!=beforeEditing)SetMode(beforeEditing);
    loading=true;preview.IsChecked=beforePreview;loading=false;
    LayoutMode();UpdateTitle();
}
bool ConversionViewCurrent(DocumentTab state,SourceEditor editor,object document){return !fileOpenClosed&&Object.ReferenceEquals(state,activeDocument)&&Object.ReferenceEquals(editor,source)&&Object.ReferenceEquals(document,source.Document);}

// The save conversion wrote its file: record the one-step mode entry now, exactly where an
// ordinary toggle records it, and forget the rollback snapshot.
void FinishSaveConversion(DocumentTab state){
    if(!saveConversionHeld||state!=saveConversionDocument)return;
    saveConversionHeld=false;bool beforeMode=saveConversionBeforeMode;string before=saveConversionBefore,after=saveConversionAfter;
    saveConversionBefore=null;saveConversionAfter=null;saveConversionDocument=null;
    /* The tab's own history, not the active tab's: a switch is not expected between the conversion
       and its write, but the entry belongs to the tab the conversion was committed to. */
    state.History.Record(before,after,beforeMode,!beforeMode);
    /* The save wrote before this entry existed, so the undo button's state from that pass did not
       see it; the ordinary toggle records before its Render and refreshes it there. */
    if(state==activeDocument)UpdateUndoRedoTools();
}
// The save conversion did not write its file (encoder refusal, conflict, lock, cancel): put the
// tab back exactly as it was before the conversion. No entry was recorded for the conversion, so
// the undo and redo stacks are still the ones the user had. The error itself is reported by the
// caller that already reports save failures; this method only restores state.
void RollbackSaveConversion(DocumentTab state){
    if(!saveConversionHeld||state!=saveConversionDocument)return;
    saveConversionHeld=false;bool beforeMode=saveConversionBeforeMode;string before=saveConversionBefore,after=saveConversionAfter;
    int caret=saveConversionCaret,start=saveConversionSelectionStart,length=saveConversionSelectionLength;
    bool beforeEditing=saveConversionBeforeEditing,beforePreview=saveConversionBeforePreview;
    saveConversionBefore=null;saveConversionAfter=null;saveConversionDocument=null;
    /* The conversion and the write run in one dispatcher turn, so this is the active tab. A text
       that no longer matches the conversion means another owner already changed the buffer (no
       history entry exists for the conversion) and must not be overwritten. */
    if(state==null||state!=activeDocument||!String.Equals(text,after,StringComparison.Ordinal))return;
    BeginViewTransition();text=before;textMode=beforeMode;RestoreTextModeView(beforeEditing,beforePreview);Render();LayoutMode();
    int total=source.Document.TextLength;if(start<0)start=0;if(start>total)start=total;if(length<0||length>total-start)length=total-start;
    source.Select(start,length);if(caret<0)caret=0;if(caret>total)caret=total;source.CaretIndex=caret;
    source.Focus();PositionCaretInView();Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{PositionCaretInView();EndViewTransition();}));UpdateTextModeMenu();CaptureTab();
}
// A save conversion that failed before its commit has no rollback snapshot, but the automatic
// edit-mode switch still happened: the caller passes the view it captured before the toggle.
void RollbackSaveConversionView(bool beforeEditing,bool beforePreview){
    bool held=saveConversionHeld;
    RollbackSaveConversion(activeDocument);
    if(!held)RestoreTextModeView(beforeEditing,beforePreview);
}

// A view/document change invalidates a queued conversion before it commits or writes a file.
void ReportAbortedTextConversion(){if(!saveInProgress||Program.IsSelfTest)return;EnsureVisibleForeground();MessageBox.Show(this,T("저장할 수 없습니다.\n")+T("변환 중에 보기나 문서가 바뀌어 저장하지 못했습니다. 다시 저장해 주세요."),"MDNote");}

Border textBadge,statusBar;
void UpdateTextModeMenu(){if(textModeMenu!=null)textModeMenu.IsChecked=PlainText;if(quickText!=null){quickText.Tag=PlainText?"Active":null;quickText.ToolTip=TextToggleTip();}if(textBadge!=null)textBadge.Visibility=PlainText?Visibility.Visible:Visibility.Collapsed;if(statusBar!=null){if(PlainText)statusBar.SetResourceReference(Border.BackgroundProperty,"AccentSoft");else statusBar.ClearValue(Border.BackgroundProperty);}}
void BuildTextModeMenu(MenuItem menu){alwaysEditMenu=new MenuItem {Header="항상 수정 기능 활성화",IsCheckable=true,IsChecked=alwaysEditing};alwaysEditMenu.Click+=delegate{alwaysEditing=alwaysEditMenu.IsChecked;SavePreferences();};menu.Items.Insert(1,alwaysEditMenu);textModeMenu=new MenuItem {Header="TXT 모드",IsCheckable=true};textModeMenu.Click+=delegate{ToggleTextMode();};menu.Items.Insert(2,textModeMenu);newTextMenu=new MenuItem {Header="새 문서는 항상 TXT 모드 활성화",IsCheckable=true,IsChecked=newDocumentsText};newTextMenu.Click+=delegate{newDocumentsText=newTextMenu.IsChecked;SavePreferences();};menu.Items.Insert(3,newTextMenu);}
}}
