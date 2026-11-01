using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
namespace MDNote {public partial class MainWindow {
    readonly Border tableToolsHost=new Border {Visibility=Visibility.Collapsed};
    readonly List<Button> tableActions=new List<Button>();
    Border tableAlignDivider;
    void BuildTableTools(){SetupTableLevelKeys();AddHandler(Mouse.PreviewMouseDownEvent,new MouseButtonEventHandler(TableToolsPointerPressed),true);AddHandler(Mouse.MouseUpEvent,new MouseButtonEventHandler(TableToolsPointerReleased),true);AddHandler(Mouse.LostMouseCaptureEvent,new MouseEventHandler(TableToolsCaptureLost),true);Closed+=delegate{if(tableToolsWork!=null){tableToolsWork.Abort();tableToolsWork=null;}tableToolsPressed=false;UnhookTableLevelEdits();};/* b56: a horizontal StackPanel clipped the right end of the row below ~430 px (MinWidth is 360), so
   정렬/행/열/표 삭제/내어쓰기 buttons had no hit target at all. A WrapPanel keeps the exact one-row
   geometry while everything fits and wraps the rest onto a second line instead of clipping it. */var row=new WrapPanel {Orientation=Orientation.Horizontal};tableToolsHost.Child=row;tableToolsHost.Padding=new Thickness(8,2,8,2);tableToolsHost.BorderThickness=new Thickness(0,0,0,1);tableToolsHost.SetResourceReference(Border.BackgroundProperty,"Ribbon");tableToolsHost.SetResourceReference(Border.BorderBrushProperty,"Line");DockPanel.SetDock(tableToolsHost,Dock.Top);root.Children.Insert(root.Children.IndexOf(ribbonHost)+1,tableToolsHost);tableToolsHost.SizeChanged+=delegate{QueueTableDividerFit();};string[] labels={"행 추가","행 제거","열 추가","열 제거","표 삭제","아래 문단 추가","왼쪽 정렬","가운데 정렬","오른쪽 정렬","표 내어쓰기","표 들여쓰기"};var buttons=new Button[labels.Length];for(int i=0;i<labels.Length;i++){int action=i;var button=new Button();SetupButton(button,labels[i],delegate{EditTable(action);});string[] keys={"rowAdd","rowRemove","colAdd","colRemove","tableDelete","paragraph","alignL","alignC","alignR","outdent","indent"};button.Content=i==5?IconLabelRow(keys[i],"아래 문단"):i>=9?TableLevelIcon(i==10):Ui.GridIcon(keys[i]);button.ToolTip=labels[i];System.Windows.Automation.AutomationProperties.SetName(button,labels[i]);button.Height=Ui.SquareSize;button.MinHeight=Ui.SquareSize;button.Padding=new Thickness(6,1,6,1);button.Margin=new Thickness(2,0,2,0);buttons[i]=button;tableActions.Add(button);}tableAlignDivider=TableToolDivider();row.Children.Add(buttons[6]);row.Children.Add(buttons[7]);row.Children.Add(buttons[8]);row.Children.Add(tableAlignDivider);row.Children.Add(buttons[0]);row.Children.Add(buttons[1]);row.Children.Add(buttons[2]);row.Children.Add(buttons[3]);row.Children.Add(TableToolDivider());row.Children.Add(buttons[4]);row.Children.Add(buttons[5]);row.Children.Add(TableToolDivider());row.Children.Add(buttons[9]);row.Children.Add(buttons[10]);
// The outdent/indent pair is disabled when the move is unavailable (no list item above the table,
// a table with no indent). WPF ignores a tooltip on a disabled control unless ShowOnDisabled is
// set, and the reason is exactly what the user needs to read then.
ToolTipService.SetShowOnDisabled(buttons[9],true);ToolTipService.SetShowOnDisabled(buttons[10],true);
}
    static Border TableToolDivider(){var divider=new Border {Width=1,Height=16,Margin=new Thickness(8,0,8,0)};divider.SetResourceReference(Border.BackgroundProperty,"Line");return divider;}
    /* b57: the row wraps below its one-line width. Ui.BalanceDivider assumes the divider and both of its
       neighbours share a line; a divider that started a wrapped line measured a ~300 px gap and pushed
       the remaining buttons onto extra lines (seen at 360 px). The dividers are decoration, so they are
       hidden while the row does not fit on one line and come back with their neutral gap when it does. */
    bool tableDividerFitQueued;
    void QueueTableDividerFit(){
        if(tableDividerFitQueued)return;tableDividerFitQueued=true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{tableDividerFitQueued=false;UpdateTableDividerFit();}));
    }
    void UpdateTableDividerFit(){
        var row=tableToolsHost.Child as Panel;
        if(row==null||row.Children.Count==0)return;
        double hostWidth=tableToolsHost.ActualWidth;
        if(hostWidth<=0&&root!=null)hostWidth=root.ActualWidth;
        if(hostWidth<=0)return;
        double available=hostWidth-tableToolsHost.Padding.Left-tableToolsHost.Padding.Right-tableToolsHost.BorderThickness.Left-tableToolsHost.BorderThickness.Right;
        if(available<=0)return;
        double sum=0;bool any=false;
        foreach(UIElement child in row.Children){
            var divider=child as Border;
            if(divider!=null&&divider.Width<=1.5){sum+=divider.Width+divider.Margin.Left+divider.Margin.Right;any=true;continue;}
            var element=child as FrameworkElement;if(element==null)continue;
            element.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            sum+=element.DesiredSize.Width;
        }
        if(!any)return;
        bool fit=sum<=available+0.5;
        foreach(UIElement child in row.Children){
            var divider=child as Border;
            if(divider==null||divider.Width>1.5)continue;
            if(fit){
                if(divider.Visibility!=Visibility.Visible){divider.Margin=new Thickness(Ui.DividerGap,0,Ui.DividerGap,0);divider.Visibility=Visibility.Visible;}
            }else if(divider.Visibility!=Visibility.Collapsed)divider.Visibility=Visibility.Collapsed;
        }
    }
    // Outdent/indent icon: a small table block with an arrow pointing away from (outdent) or
    // towards (indent) the left margin, at the same 20x20 optical size as the other table icons.
    static FrameworkElement TableLevelIcon(bool indent){
        var icon=new Canvas {Width=20,Height=20};
        string box=indent?"M 8,4 H 18 M 8,10 H 18 M 8,16 H 18 M 8,3 V 17":"M 6,4 H 18 M 6,10 H 18 M 6,16 H 18 M 6,3 V 17";
        string arrow=indent?"M 1,10 H 5 M 3,8 L 5,10 L 3,12":"M 5,10 H 1 M 3,8 L 1,10 L 3,12";
        var lines=new System.Windows.Shapes.Path {Data=Geometry.Parse(box+" "+arrow),StrokeThickness=1.3,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
        lines.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Ink");
        icon.Children.Add(lines);
        return icon;
    }
    // #18/#17: in live mode the same bar edits the Markdown table source (MarkdownTableCommands).
// The caret-line check reads the preview's line cache, O(1).
bool LiveTableCaret {get{if(!LiveFormat||!LivePreviewAttached)return false;return livePreview.Info(source.Document.GetLineByOffset(source.CaretIndex)).Table;}}
System.Windows.Threading.DispatcherOperation tableToolsWork;bool tableToolsChanging;
// The tool row is a bar above the editor, so showing or hiding it moves the text under the
// pointer. AvalonEdit hit-tests that same screen point again when the button comes up
// (SelectionMouseHandler.textArea_MouseLeftButtonUp), so a change made while a press is in
// flight put the caret on another line: a click on the paragraph under a table typed into the
// table's last cell, and a click into a cell typed into the row above. The row now waits for
// the release, then applies once with the scroll compensation below. Keyboard caret moves have
// no pointer under them and stay immediate.
bool tableToolsPressed;int tableToolsPressTick;
void UpdateTableTools(){
    if(tableToolsChanging)return;
    HookTableLevelEdits();
    bool live=LiveTableCaret,shown=live;
    // One extent read per pass: the alignment buttons and the level buttons read the same table,
    // and FindTable walks every row of it, so the pass computes it once and hands it to both.
    int first=0,last=0;bool table=false;
    if(shown)table=LiveTableExtent(out first,out last);
    int alignment=shown?LiveColumnAlignment(table,first,last):-1;
    for(int i=6;i<9&&i<tableActions.Count;i++)tableActions[i].Tag=i-6==alignment?"Active":null;
    UpdateTableLevelTools(shown,table,first,last);
    if(tableToolsWork!=null||tableToolsHost.Visibility==(shown?Visibility.Visible:Visibility.Collapsed))return;
    if(TableToolsPointerHeld())return;   // TableToolsPointerReleased / TableToolsCaptureLost retries
    // Selection and caret notifications are separate; one coalesced pass is enough.
    tableToolsWork=Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,new Action(delegate{
        tableToolsWork=null;if(fileOpenClosed||TableToolsPointerHeld())return;
        ApplyTableToolsVisibility();
    }));
}
void TableToolsPointerPressed(object sender,MouseButtonEventArgs e){
    if(e.ChangedButton!=MouseButton.Left&&e.ChangedButton!=MouseButton.Right)return;
    tableToolsPressed=true;tableToolsPressTick=Environment.TickCount;
}
void TableToolsPointerReleased(object sender,MouseButtonEventArgs e){
    if(e.ChangedButton!=MouseButton.Left&&e.ChangedButton!=MouseButton.Right)return;
    tableToolsPressed=false;
    // Bubbling, so the editor's own release handler has already hit-tested the pointer
    // (SelectionMouseHandler.textArea_MouseLeftButtonUp) and released its capture. Applying
    // from a preview handler could move the text before that hit-test.
    UpdateTableTools();
}
void TableToolsCaptureLost(object sender,MouseEventArgs e){
    tableToolsPressed=false;
    UpdateTableTools();
}
bool TableToolsPointerHeld(){
    // A release that went to another window without capture must not freeze the row for good:
    // after a second with no button physically down the press is over.
    if(tableToolsPressed&&Mouse.LeftButton==MouseButtonState.Released&&Mouse.RightButton==MouseButtonState.Released&&unchecked(Environment.TickCount-tableToolsPressTick)>1000)tableToolsPressed=false;
    return tableToolsPressed||Mouse.LeftButton==MouseButtonState.Pressed||Mouse.RightButton==MouseButtonState.Pressed||EditorMouseCaptured();
}
bool EditorMouseCaptured(){return source!=null&&source.TextArea.IsMouseCaptured;}
void ApplyTableToolsVisibility(){
    if(tableToolsChanging)return;
    bool shown=LiveTableCaret;Visibility wanted=shown?Visibility.Visible:Visibility.Collapsed;
    if(tableToolsHost.Visibility==wanted)return;
    tableToolsChanging=true;
    try{
        FrameworkElement surface=source;
        double top=surface.TranslatePoint(new Point(),root).Y,scroll=source.VerticalOffset;
        tableToolsHost.Visibility=wanted;
        if(tableAlignDivider!=null)tableAlignDivider.Visibility=wanted;
        for(int i=6;i<tableActions.Count;i++)tableActions[i].Visibility=wanted;
        /* the divider fit decides before the arrange pass, so the row's final height (one line or two) is
           what the scroll compensation below measures */
        if(shown)UpdateTableDividerFit();
        // Arrange first: TranslatePoint reads the last arrange, so without this pass delta is
        // always 0 and the compensation below never runs (measured: the text moved the full row
        // height under a fixed point).
        UpdateLayout();
        double delta=surface.TranslatePoint(new Point(),root).Y-top;
        if(Math.Abs(delta)>.5)source.ScrollToVerticalOffset(Math.Max(0,scroll+delta));
        // The OS caret and the IME composition window follow Caret.Show, not a scroll alone, so
        // a moved editor would leave them drawn where the caret used to be.
        if(source!=null)source.TextArea.Caret.Show();
    }finally{tableToolsChanging=false;}
}
// Live: the delimiter row is the truth. The table's extent is cached by document version and
// caret line, so a caret move inside the table re-reads only the caret line (CellIndexAt); the
// delimiter alignments are cached for the same version and extent. UpdateTableTools reads the
// extent once through LiveTableExtent and passes it here and to the level buttons.
object liveExtentVersion,liveAlignVersion;int liveExtentFirst,liveExtentLast,liveExtentLine;bool liveExtentTable;int liveTableFirst,liveTableLast;int[] liveAlignColumns;
bool LiveTableExtent(out int first,out int last){
    var document=source.Document;object version=document.Version;int line=document.GetLineByOffset(source.CaretIndex).LineNumber;
    if(Object.ReferenceEquals(version,liveExtentVersion)&&(line==liveExtentLine||(liveExtentTable&&line>=liveExtentFirst&&line<=liveExtentLast))){first=liveExtentFirst;last=liveExtentLast;return liveExtentTable;}
    bool found=MarkdownTableCommands.FindTable(document,source.CaretIndex,out first,out last);
    liveExtentVersion=version;liveExtentLine=line;liveExtentTable=found;
    if(found){liveExtentFirst=first;liveExtentLast=last;}else liveExtentFirst=liveExtentLast=0;
    return found;
}
int LiveColumnAlignment(bool table,int first,int last){var document=source.Document;int column=MarkdownTableCommands.CellIndexAt(document,source.CaretIndex);object version=document.Version;if(table){if(!Object.ReferenceEquals(version,liveAlignVersion)||first!=liveTableFirst||last!=liveTableLast||liveAlignColumns==null){liveAlignColumns=DelimiterAlignments(document.GetText(document.GetLineByNumber(first+1)));liveAlignVersion=version;liveTableFirst=first;liveTableLast=last;}return column<liveAlignColumns.Length?liveAlignColumns[column]:0;}liveAlignVersion=version;liveAlignColumns=null;liveTableFirst=liveTableLast=0;return 0;}
internal static int DelimiterAlignment(string line,int column){int[] values=DelimiterAlignments(line);return column>=0&&column<values.Length?values[column]:0;}
static int[] DelimiterAlignments(string line){if(line==null)return new int[0];string value=line.Trim();if(value.StartsWith("|",StringComparison.Ordinal))value=value.Substring(1);if(value.EndsWith("|",StringComparison.Ordinal))value=value.Substring(0,value.Length-1);string[] cells=value.Split('|');var result=new int[cells.Length];for(int i=0;i<cells.Length;i++){string cell=cells[i].Trim();result[i]=cell.StartsWith(":",StringComparison.Ordinal)&&cell.EndsWith(":",StringComparison.Ordinal)?1:cell.EndsWith(":",StringComparison.Ordinal)?2:0;}return result;}
void LiveEditTable(int action){RunLiveCommand(delegate(ICSharpCode.AvalonEdit.Document.TextDocument d,int s,int l){switch(action){case 0:return MarkdownTableCommands.AddRowBelow(d,s);case 1:return MarkdownTableCommands.RemoveRow(d,s);case 2:return MarkdownTableCommands.AddColumnRight(d,s);case 3:return MarkdownTableCommands.RemoveColumn(d,s);case 4:return MarkdownTableCommands.DeleteTable(d,s);case 5:return MarkdownTableCommands.ParagraphAfterGap(d,s);default:return MarkdownTableCommands.AlignColumn(d,s,action-6);}});UpdateTableTools();}
// Actions 9/10: move the whole table one list level out or in. A table inside a list item is
// written as its lines indented by the item's continuation indent, so outdent takes one indent
// unit off every line and indent writes the item's own content indent (MarkdownTableCommands.
// IndentTable), never a bare four-space block. Tab/Shift+Tab inside a table stay cell navigation,
// so the move is reached from the two buttons on the table tool row and from Ctrl+[ / Ctrl+
// (StructuralInput.TableLevelKey).
internal bool TableLevel(bool indent){
    if(!LiveTableCaret)return false;
    bool moved=RunLiveCommand(delegate(ICSharpCode.AvalonEdit.Document.TextDocument d,int s,int l){
        return indent?MarkdownTableCommands.IndentTable(d,s,l):MarkdownTableCommands.OutdentTable(d,s,l);
    });
    UpdateTableTools();
    return moved;
}
// The two buttons' enabled state and tooltip, for the live view. The live view asks the same
// pure functions the command runs, so a disabled button and a refused command never disagree.
// The tooltip explains the reason so the buttons are never silently dead (user report: they did
// nothing outside a list).
// This runs on every pipeline flush, i.e. on every keystroke. TableLevelState reads the table's
// header (its indent), the blank line under the table and the list lines above it - its three
// scans (ListLineAbove, OwningListItem, ParentListLine) reach 600 lines up - and never the rows
// between, so the answer is kept until an edit can reach one of those lines or the table's
// extent or header text changes. Typing inside a middle cell then costs no line above the table
// at all; Enter adds a row and Backspace joins lines, which change the extent (or drop the break
// at the table's bottom edge) and recompute the answer.
const int TableLevelScanLines=600;
ICSharpCode.AvalonEdit.Document.TextDocument tableLevelDocument;int tableLevelFirst,tableLevelLast;string tableLevelFirstText;bool tableLevelValid;bool tableLevelEdits;int tableLevelEditLow,tableLevelEditHigh;bool tableLevelEditBreak;bool liveLevelIndent,liveLevelOutdent;/* the cached reasons are the untranslated keys: T() runs on every update, so a language switch shows on the next refresh instead of keeping the old language's sentence */string liveLevelIndentReason,liveLevelOutdentReason;
// The cached answer is only as good as the record of what changed, so the editor's change seam
// (SourceEditor.ContentDelta, raised after every document change) is hooked once per source.
SourceEditor tableLevelSource;
void HookTableLevelEdits(){if(Object.ReferenceEquals(tableLevelSource,source))return;if(tableLevelSource!=null)tableLevelSource.ContentDelta-=TableLevelDocumentChanged;tableLevelSource=source;if(tableLevelSource!=null)tableLevelSource.ContentDelta+=TableLevelDocumentChanged;}
void UnhookTableLevelEdits(){if(tableLevelSource!=null)tableLevelSource.ContentDelta-=TableLevelDocumentChanged;tableLevelSource=null;}
// The changed span as the new document's lines: the line the change lands on and, for an
// insertion, the line its last character lands on. A removal reports its join line, which is
// where a merged row or the blank line under the table ends up.
void TableLevelDocumentChanged(object sender,ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs change){
    var document=source==null?null:source.Document;
    if(document==null||change==null)return;
    int length=document.TextLength;int offset=change.Offset<0?0:change.Offset>length?length:change.Offset;
    int low=document.GetLineByOffset(offset).LineNumber,high=low;
    if(change.InsertionLength>0){int end=offset+change.InsertionLength;if(end<offset||end>length)end=length;high=document.GetLineByOffset(end).LineNumber;}
    if(!tableLevelEdits){tableLevelEdits=true;tableLevelEditLow=low;tableLevelEditHigh=high;}else{if(low<tableLevelEditLow)tableLevelEditLow=low;if(high>tableLevelEditHigh)tableLevelEditHigh=high;}
    if(!tableLevelEditBreak&&(HasLineBreak(change.InsertedText)||HasLineBreak(change.RemovedText)))tableLevelEditBreak=true;
}
// Reuse the cached answer unless the table, its header text or one of the lines the move reads
// changed: any edit on the lines above the header (the list scans), on the blank line under the
// table (a typed one can take the continuation line over), or a removed line break that merged
// the bottom edge. An edit between the header and the last row cannot reach any of them.
bool TableLevelCacheHit(int first,int last,string firstText){
    if(!tableLevelValid||!Object.ReferenceEquals(tableLevelDocument,source.Document))return false;
    if(first!=tableLevelFirst||last!=tableLevelLast||firstText!=tableLevelFirstText)return false;
    if(!tableLevelEdits)return true;
    int from=first-TableLevelScanLines;if(from<1)from=1;
    if(tableLevelEditLow<first&&tableLevelEditHigh>=from)return false;
    if(tableLevelEditLow<=last+1&&tableLevelEditHigh>=last+1)return false;
    return !(tableLevelEditBreak&&tableLevelEditLow<=last&&tableLevelEditHigh>=last);
}
void ComputeTableLevelState(ICSharpCode.AvalonEdit.Document.TextDocument document,int first,int last,string firstText){
    int offset=Math.Max(0,Math.Min(source.CaretIndex,document.TextLength));
    string indentReason,outdentReason;
    MarkdownTableCommands.TableLevelState(document,offset,out liveLevelIndent,out liveLevelOutdent,out indentReason,out outdentReason);
    liveLevelIndentReason=indentReason;liveLevelOutdentReason=outdentReason;
    tableLevelDocument=document;tableLevelFirst=first;tableLevelLast=last;tableLevelFirstText=firstText;tableLevelValid=true;tableLevelEdits=false;
}
void UpdateTableLevelTools(bool shown,bool table,int first,int last){
    if(tableActions.Count<11)return;
    bool indentAvailable=false,outdentAvailable=false;string indentTip=null,outdentTip=null;
    if(shown&&table){
        var document=source.Document;
        string firstText=document.GetText(document.GetLineByNumber(first));
        if(!TableLevelCacheHit(first,last,firstText))ComputeTableLevelState(document,first,last,firstText);
        indentAvailable=liveLevelIndent;outdentAvailable=liveLevelOutdent;
        indentTip=liveLevelIndentReason;outdentTip=liveLevelOutdentReason;
    }
    Button outdent=tableActions[9],indent=tableActions[10];
    // The tooltip is rewritten on every refresh, so it goes through T() here: the old code put the
    // Korean literal (or the old language's cached sentence) back on the next caret move.
    indent.IsEnabled=indentAvailable;indent.ToolTip=T(indentTip??"표 들여쓰기");
    outdent.IsEnabled=outdentAvailable;outdent.ToolTip=T(outdentTip??"표 내어쓰기");
}
// The table tool row's commands. The live surface edits the Markdown source; actions 9/10 move
// the table one list level (TableLevel).
void EditTable(int action){if(action==9||action==10){TableLevel(action==10);return;}LiveEditTable(action);}
}}
