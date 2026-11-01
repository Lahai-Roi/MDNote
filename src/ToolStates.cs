using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
namespace MDNote {
public partial class MainWindow {
    Button undoButton,redoButton;
    // History owns both surfaces; Count-backed flags are the only work added to an edit callback.
    void UpdateUndoRedoTools(){bool allowed=!documentOperationBusy&&(editing||(!PlainText&&!CodeMode));if(undoButton!=null)undoButton.IsEnabled=allowed&&History.CanUndo;if(redoButton!=null)redoButton.IsEnabled=allowed&&History.CanRedo;}
    readonly List<MenuItem> headingButtons=new List<MenuItem>();
    readonly Button headingButton=new Button(),listButton=new Button(),quoteButton=new Button(),linkButton=new Button(),codeButton=new Button();
    static T Ancestor<T>(DependencyObject element) where T:DependencyObject {while(element!=null){var result=element as T;if(result!=null)return result;var content=element as FrameworkContentElement;element=content==null?LogicalTreeHelper.GetParent(element):content.Parent;}return null;}
    static void Active(Control button,bool active){button.Tag=active?"Active":null;}
    void UpdateStructuralTools(){LiveUpdateTools();}

    readonly TreeView outline=new TreeView {Width=220,Visibility=Visibility.Collapsed,BorderThickness=new Thickness(1,0,0,0)};
    Button outlineButton;
    readonly System.Windows.Controls.Primitives.Thumb outlineGrip=new System.Windows.Controls.Primitives.Thumb {Width=5,Cursor=System.Windows.Input.Cursors.SizeWE,Visibility=Visibility.Collapsed};
    string outlineSignature;double outlineDragWidth;
    void BuildOutline(DockPanel top){
        outlineButton=CommandIcon("find","아웃라인 열기/닫기",delegate{bool show=outline.Visibility!=Visibility.Visible;outline.Visibility=show?Visibility.Visible:Visibility.Collapsed;Active(outlineButton,show);RefreshOutline();});
        outlineButton.Content=Ui.OutlineIcon();outlineButton.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(outlineButton,Dock.Right);top.Children.Insert(0,outlineButton);
        outline.SetResourceReference(Control.BackgroundProperty,"Ribbon");outline.SetResourceReference(Control.ForegroundProperty,"Ink");outline.SetResourceReference(Control.BorderBrushProperty,"Line");DockPanel.SetDock(outline,Dock.Right);root.Children.Add(outline);outlineGrip.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Thumb'><Border Background='{TemplateBinding Background}'/></ControlTemplate>");outlineGrip.SetResourceReference(Control.BackgroundProperty,"Line");DockPanel.SetDock(outlineGrip,Dock.Right);root.Children.Add(outlineGrip);outline.IsVisibleChanged+=delegate{outlineGrip.Visibility=outline.Visibility;};outlineGrip.DragStarted+=delegate{outlineDragWidth=outline.Width;};outlineGrip.DragDelta+=delegate(object sender,System.Windows.Controls.Primitives.DragDeltaEventArgs args){if(outlineDragWidth<=0)outlineDragWidth=outline.Width;outlineDragWidth=Math.Max(180,Math.Min(Math.Max(180,ActualWidth-340),outlineDragWidth-args.HorizontalChange));outlineGrip.RenderTransform=new TranslateTransform(outline.Width-outlineDragWidth,0);};outlineGrip.DragCompleted+=delegate{if(outlineDragWidth>0)outline.Width=outlineDragWidth;outlineGrip.RenderTransform=Transform.Identity;outlineDragWidth=0;outlineSignature=null;RefreshOutline();};ScrollViewer.SetHorizontalScrollBarVisibility(outline,ScrollBarVisibility.Disabled);outline.Padding=new Thickness(5,7,5,7);
    }
    TreeViewItem OutlineItem(string title,int level){
        var row=new StackPanel {Orientation=Orientation.Horizontal};var badgeText=new TextBlock {Text="H"+level,FontSize=10,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};badgeText.Foreground=Ui.Brush(dark?"#DFE7F2":"#46556B");var badge=new Border {Child=badgeText,Width=23,Height=18,CornerRadius=new CornerRadius(3),Margin=new Thickness(0,0,6,0),Background=Ui.Brush(dark?"#536176":"#D6DFEB")};row.Children.Add(badge);
        var label=new TextBlock {Text=title,FontFamily=new FontFamily(family),FontSize=Math.Max(12,Math.Min(16,fontSize)),FontWeight=FontWeights.Normal,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,Width=Math.Max(45,outline.Width-72-(level-1)*19)};label.SetResourceReference(TextBlock.ForegroundProperty,"Ink");row.Children.Add(label);
        var surface=new Border {Child=row,CornerRadius=new CornerRadius(4),Padding=new Thickness(4,3,4,3),Margin=new Thickness(0,2,0,2),BorderThickness=new Thickness(0,0,0,level<=2?1:0),BorderBrush=Ui.Brush(dark?"#4F5C70":"#D2DAE5"),Background=level<=2?Ui.Brush(dark?(level==1?"#434D5D":"#3D4654"):(level==1?"#E0E6EF":"#E7ECF3")):Brushes.Transparent};var item=new TreeViewItem {Header=surface,ToolTip=title,IsExpanded=true,Padding=new Thickness(0)};System.Windows.Automation.AutomationProperties.SetName(item,"H"+level+" "+title);return item;
    }
    void RefreshOutline(){
        if(outline.Visibility!=Visibility.Visible)return;sourceOutlineDirty=false;if(outline.FontFamily.Source!=family)outline.FontFamily=new FontFamily(family);if(PlainText){CancelSourceOutline();ClearOutlineItems();return;}
        /* The rebuild keeps the reader's arrangement (fold, selection, scroll). The large path
           captures its own state when the background scan publishes, next to its own Clear. */
        /* Another tab's fold and selection keys belong to that tab: only the same document's arrangement carries over. */
        OutlineState state=ReferenceEquals(outlineStateDocument,source.Document)?CaptureOutlineState():null;outlineStateDocument=source.Document;
        if(RefreshLargeSourceOutline())return;ClearOutlineItems();outlineSignature=null;RefreshSourceOutline();ApplyOutlineState(state);
    }
    object outlineStateDocument;
    /* P2-1: a TXT document has no headings, so the panel goes away with the mode that can fill it.
       The button is disabled while TXT (there is nothing to list), and a panel that was open comes
       back open when the tab returns to Markdown. Closing it here also clears the items and cancels
       a pending scan (SourceOutline's IsVisibleChanged hook), so no empty frame is left behind. */
    bool outlineSuspendedByTextMode;
    void UpdateOutlineMode(){
        if(outlineButton==null)return;
        if(PlainText){
            if(outline.Visibility==Visibility.Visible)outlineSuspendedByTextMode=true;
            outline.Visibility=Visibility.Collapsed;Active(outlineButton,false);
        }else if(outlineSuspendedByTextMode){
            outlineSuspendedByTextMode=false;outline.Visibility=Visibility.Visible;Active(outlineButton,true);RefreshOutline();
        }
        outlineButton.IsEnabled=!PlainText;
    }
    static IEnumerable<Paragraph> EnumerateParagraphs(BlockCollection blocks){foreach(Block block in blocks){var paragraph=block as Paragraph;if(paragraph!=null)yield return paragraph;else if(block is Section){foreach(var child in EnumerateParagraphs(((Section)block).Blocks))yield return child;}else if(block is System.Windows.Documents.List){foreach(ListItem item in ((System.Windows.Documents.List)block).ListItems)foreach(var child in EnumerateParagraphs(item.Blocks))yield return child;}}}
    void RefreshSourceOutline(){RefreshSmallSourceOutline();}
    static BlockCollection ParentBlocks(Block block){var parent=block.Parent;if(parent is FlowDocument)return ((FlowDocument)parent).Blocks;if(parent is Section)return ((Section)parent).Blocks;if(parent is ListItem)return ((ListItem)parent).Blocks;if(parent is TableCell)return ((TableCell)parent).Blocks;return null;}
    void SetHeading(int level){LiveHeading(level);}
    void ClearTextColor(){LiveColor(null);}
}
}
