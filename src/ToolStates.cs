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

    readonly TreeView outline=new TreeView {Width=220,Visibility=Visibility.Collapsed};
    /* 2026-11-09 (user): the panel is a host column now - a two-tab text header (목차 | 파일) above
       the tree. The host width is the old outline width (the grip drags the same number), and the
       tree keeps outline.Visibility as the panel's open/closed switch so the toolbar toggle, the
       View menu, F10, probes and SourceOutline's IsVisibleChanged hook keep their meaning. While the
       파일 tab is chosen the tree stays visible underneath an opaque file surface; switching back to
       목차 only reuses it, so no tab switch ever costs an outline rebuild. */
    readonly Border outlineHost=new Border {Width=0,ClipToBounds=true};
    readonly Grid outlineBody=new Grid();
    TextBlock outlineTabText,filesTabText;
    bool outlinePanelOpen,outlineFilesTab;
    Button outlineButton;MenuItem outlineMenuToggle;
    readonly System.Windows.Controls.Primitives.Thumb outlineGrip=new System.Windows.Controls.Primitives.Thumb {Width=5,Cursor=System.Windows.Input.Cursors.SizeWE,Visibility=Visibility.Collapsed};
    string outlineSignature;double outlineDragWidth;Style outlineRowStyle;
    void BuildOutline(DockPanel top){
        outlineButton=CommandIcon("find","아웃라인 열기/닫기",delegate{SetOutlinePanel(outline.Visibility!=Visibility.Visible);});
        /* 2026-11-09 (user): the outline was an unlabelled 22 px square at the far right, so nobody
           found it. It now wears the same quiet outlined chrome as the 원문 toggle, icon then the
           목차 label (translated through Localization), and the shared overflow pass may shrink it to
           the icon alone on a very narrow window — never into the "»" list. */
        outlineButton.Style=(Style)Resources["FormatViewButton"];
        outlineButton.ClearValue(FrameworkElement.WidthProperty);outlineButton.ClearValue(FrameworkElement.MinWidthProperty);outlineButton.ClearValue(FrameworkElement.HeightProperty);outlineButton.ClearValue(FrameworkElement.MinHeightProperty);outlineButton.ClearValue(Control.PaddingProperty);
        var outlineGlyph=Ui.OutlineIcon();outlineGlyph.Margin=new Thickness(0,0,5,0);outlineGlyph.Tag=outlineGlyph.Margin;/* Ui.CompactToggleLabel restores this on the wide layout. VM pixels (11-09): 16 + 5 = the braces' 17 + 4, labels start on the same column and the two marks share a centre *//* the 원문 glyph's own 4 px gap */outlineButton.UseLayoutRounding=true;outlineButton.SnapsToDevicePixels=true;/* 11-09: the centred 22 px button sat on a half pixel in the top bar and its 1 px outline blurred over two rows *//* 2026-11-09 (user): the old -2 px nudge is gone; VerticalAlignment=Center puts the glyph's centre on the 목차 label's centre, the same alignment the 원문 glyph uses */outlineGlyph.VerticalAlignment=VerticalAlignment.Center;
        var outlineRow=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};outlineRow.Children.Add(outlineGlyph);outlineRow.Children.Add(new TextBlock {Text="목차",VerticalAlignment=VerticalAlignment.Center});
        outlineButton.Content=outlineRow;outlineButton.Margin=new Thickness(1,0,1,0);outlineButton.VerticalAlignment=VerticalAlignment.Center;outlineButton.HorizontalAlignment=HorizontalAlignment.Right;DockPanel.SetDock(outlineButton,Dock.Right);top.Children.Insert(0,outlineButton);
        outline.SetResourceReference(Control.BackgroundProperty,"Ribbon");outline.SetResourceReference(Control.ForegroundProperty,"Ink");outline.SetResourceReference(Control.BorderBrushProperty,"Line");outline.BorderThickness=new Thickness(0);outline.HorizontalAlignment=HorizontalAlignment.Left;
        /* The header is two equal text tabs over one Line rule; the selected one carries the body
           ink and the SemiBold weight, the other the Muted ink. Hover is a flat Hover fill (no pill,
           no underline, no segmented control - user decision). */
        var tabHeader=new Grid();tabHeader.ColumnDefinitions.Add(new ColumnDefinition());tabHeader.ColumnDefinitions.Add(new ColumnDefinition());
        var outlineTab=PanelTabButton("목차",false);var filesTab=PanelTabButton("파일",true);Grid.SetColumn(filesTab,1);tabHeader.Children.Add(outlineTab);tabHeader.Children.Add(filesTab);
        var tabFrame=new Border {Child=tabHeader,BorderThickness=new Thickness(0,0,0,1)};tabFrame.SetResourceReference(Border.BorderBrushProperty,"Line");
        /* The host stays in the tree at width 0 while the panel is closed (see the IsVisibleChanged
           hook below), so outline.Visibility alone still answers "is the panel open". */
        var hostGrid=new Grid();hostGrid.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});hostGrid.RowDefinitions.Add(new RowDefinition {Height=new GridLength(1,GridUnitType.Star)});hostGrid.Children.Add(tabFrame);Grid.SetRow(outlineBody,1);outlineBody.Children.Add(outline);hostGrid.Children.Add(outlineBody);
        outlineHost.Child=hostGrid;outlineHost.SetResourceReference(Border.BackgroundProperty,"Ribbon");outlineHost.SetResourceReference(Border.BorderBrushProperty,"Line");outlineHost.BorderThickness=new Thickness(1,0,0,0);DockPanel.SetDock(outlineHost,Dock.Right);root.Children.Add(outlineHost);UpdateOutlineTabChrome();
        outlineGrip.Template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Thumb'><Border Background='{TemplateBinding Background}'/></ControlTemplate>");outlineGrip.SetResourceReference(Control.BackgroundProperty,"Line");DockPanel.SetDock(outlineGrip,Dock.Right);root.Children.Add(outlineGrip);outline.IsVisibleChanged+=delegate{bool shown=outline.Visibility==Visibility.Visible;outlinePanelOpen=shown;outlineHost.Width=shown?PanelWidth():0;outlineGrip.Visibility=outline.Visibility;/* a closed panel collapses the file surface too, so a zero-width host still measures nothing but the header */if(folderPanel!=null)folderPanel.Visibility=shown&&outlineFilesTab?Visibility.Visible:Visibility.Collapsed;/* the panel can also be opened/closed by a probe or a bench; keep the toggle and the View menu honest */Active(outlineButton,shown);outlineButton.ToolTip=OutlineToggleTip();if(outlineMenuToggle!=null)outlineMenuToggle.IsChecked=shown;System.Windows.Input.KeyboardNavigation.SetTabNavigation(outlineHost,shown?System.Windows.Input.KeyboardNavigationMode.Continue:System.Windows.Input.KeyboardNavigationMode.None);if(!shown&&outlineHost.IsKeyboardFocusWithin&&source!=null&&IsActive)source.Focus();};outline.SizeChanged+=delegate{if(outline.Visibility==Visibility.Visible&&outlineHost.Width>0&&!Double.IsNaN(outline.Width)&&Math.Abs(outlineHost.Width-outline.Width)>0.1)outlineHost.Width=PanelWidth();};outlineGrip.DragStarted+=delegate{outlineDragWidth=outline.Width;};outlineGrip.DragDelta+=delegate(object sender,System.Windows.Controls.Primitives.DragDeltaEventArgs args){if(outlineDragWidth<=0)outlineDragWidth=outline.Width;outlineDragWidth=Math.Max(180,Math.Min(Math.Max(180,ActualWidth-340),outlineDragWidth-args.HorizontalChange));outlineGrip.RenderTransform=new TranslateTransform(outline.Width-outlineDragWidth,0);};outlineGrip.DragCompleted+=delegate{if(outlineDragWidth>0){outline.Width=outlineDragWidth;if(outlinePanelOpen)outlineHost.Width=outlineDragWidth;}outlineGrip.RenderTransform=Transform.Identity;outlineDragWidth=0;outlineSignature=null;RefreshOutline();};/* Long headings are never trimmed now: the panel scrolls sideways, the Typora reading of a title
           wider than the column (user, 2026-11-09). Vertical scrolling and its restore are unchanged. */
        ScrollViewer.SetHorizontalScrollBarVisibility(outline,ScrollBarVisibility.Auto);outline.Padding=new Thickness(5,7,5,7);
        /* A tab switch changes the file the 파일 tab follows. The strip's own handler runs first and
           the posted pass reads the settled active document, so a programmatic switch never sees a
           half-switched path. */
        documentTabs.SelectionChanged+=delegate{if(switchingTabs)return;if(outlinePanelOpen&&outlineFilesTab)Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(FolderTabPathChanged));};
    }
    /* One path for the toolbar toggle, the View > 목차 menu item and F10: the panel, the button's
       active fill and the menu check always move together. */
    void SetOutlinePanel(bool show){
        outlinePanelOpen=show;
        outlineHost.Width=show?PanelWidth():0;
        outline.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        if(show){
            /* The panel reopens on the tab the reader used last. */
            if(outlineFilesTab){EnsureFolderTabContent();if(folderPanel!=null)folderPanel.Visibility=Visibility.Visible;FolderTabPathChanged();SelectCurrentFolderFile();}
            else RefreshOutline();
        }else if(folderPanel!=null)folderPanel.Visibility=Visibility.Collapsed;
        Active(outlineButton,show);
        outlineButton.ToolTip=OutlineToggleTip();
        if(outlineMenuToggle!=null)outlineMenuToggle.IsChecked=show;
    }
    double PanelWidth(){double width=outline.Width;return Double.IsNaN(width)||width<=0?220:width;}
    /* The header's two text tabs share the width evenly. The selected one is body ink and SemiBold;
       the other is Muted and Normal. Hover paints the flat Hover fill; there is no underline, pill or
       segmented chrome (user decision, 2026-11-09). */
    Button PanelTabButton(string text,bool files){
        var label=new TextBlock {Text=T(text),FontFamily=new FontFamily(family),FontSize=Math.Max(12,Math.Min(14,fontSize)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,5,0,5)};
        label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        if(files)filesTabText=label;else outlineTabText=label;
        var button=new Button {Content=label,Height=30,Padding=new Thickness(0),Margin=new Thickness(0),BorderThickness=new Thickness(0),Background=Brushes.Transparent,HorizontalContentAlignment=HorizontalAlignment.Stretch,VerticalContentAlignment=VerticalAlignment.Center};
        button.Template=PanelTabTemplate();button.Click+=delegate{ShowOutlineTab(files);};System.Windows.Automation.AutomationProperties.SetName(button,T(text));return button;
    }
    static ControlTemplate panelTabTemplate;
    static ControlTemplate PanelTabTemplate(){
        if(panelTabTemplate==null){string xaml=@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'><Border x:Name='Surface' Background='Transparent'><ContentPresenter HorizontalAlignment='Stretch' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Pressed}'/></Trigger></ControlTemplate.Triggers></ControlTemplate>";panelTabTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);}
        return panelTabTemplate;
    }
    void UpdateOutlineTabChrome(){StyleOutlineTab(outlineTabText,!outlineFilesTab);StyleOutlineTab(filesTabText,outlineFilesTab);}
    static void StyleOutlineTab(TextBlock label,bool selected){
        if(label==null)return;
        if(selected){label.SetResourceReference(TextBlock.ForegroundProperty,"Ink");label.FontWeight=FontWeights.SemiBold;}
        else{label.SetResourceReference(TextBlock.ForegroundProperty,"Muted");label.FontWeight=FontWeights.Normal;}
    }
    void SyncOutlineTabFont(){
        if(outlineTabText==null)return;double size=Math.Max(12,Math.Min(14,fontSize));
        if(outlineTabText.FontFamily.Source!=family){var tabFont=new FontFamily(family);outlineTabText.FontFamily=tabFont;if(filesTabText!=null)filesTabText.FontFamily=tabFont;}
        if(outlineTabText.FontSize!=size){outlineTabText.FontSize=size;if(filesTabText!=null)filesTabText.FontSize=size;}
    }
    /* Clicking a tab only changes what the open panel shows; the panel itself (and the tree that
       answers "is it open") stays exactly where it was. */
    void ShowOutlineTab(bool files){
        outlineFilesTab=files;UpdateOutlineTabChrome();SyncOutlineTabFont();
        if(files){EnsureFolderTabContent();if(folderPanel!=null)folderPanel.Visibility=outlinePanelOpen?Visibility.Visible:Visibility.Collapsed;FolderTabPathChanged();SelectCurrentFolderFile();}
        else{if(folderPanel!=null)folderPanel.Visibility=Visibility.Collapsed;if(outlinePanelOpen)RefreshOutline();}
    }
    /* The row is the heading itself: n slanted bars (the editor's HeadingMark language) then the
       title, level 1 in SemiBold. The "H<level>" badge and the level 1-2 tint are gone - the bars
       carry the level, so the panel stays as quiet as a Typora outline. The 10 px per-level indent,
       the small fold chevron and the hover/selection fill come from the TreeViewItem template. */
    TreeViewItem OutlineItem(string title,int level){
        var row=new StackPanel {Orientation=Orientation.Horizontal};var mark=new OutlineLevelMark {Level=Math.Max(1,Math.Min(6,level)),Margin=new Thickness(0,0,7,0),VerticalAlignment=VerticalAlignment.Center};mark.SetResourceReference(OutlineLevelMark.InkProperty,"Muted");row.Children.Add(mark);
        var label=new TextBlock {Text=title,FontFamily=new FontFamily(family),FontSize=Math.Max(12,Math.Min(16,fontSize)),FontWeight=level==1?FontWeights.SemiBold:FontWeights.Normal,VerticalAlignment=VerticalAlignment.Center};label.SetResourceReference(TextBlock.ForegroundProperty,"Ink");row.Children.Add(label);
        var item=new TreeViewItem {Header=row,ToolTip=title,IsExpanded=true,Padding=new Thickness(0),Style=OutlineRowStyle()};System.Windows.Automation.AutomationProperties.SetName(item,"H"+level+" "+title);return item;
    }
    /* One item style for both outline paths (the small tree's TreeViewItems and the large tree's
       recycled containers). The old indent was the theme template's 19 px expander column; this
       template makes it 10 px and replaces the theme expander with a small Muted chevron that turns
       down when the item opens. A leaf hides its chevron but keeps the column, so titles at one
       level line up. Hover and selection use the app's Hover/ActiveFill pair because the theme's
       system-highlight fill does not follow the dark/light palette. */
    Style OutlineRowStyle(){if(outlineRowStyle==null)outlineRowStyle=CreateOutlineRowStyle();return outlineRowStyle;}
    static Style CreateOutlineRowStyle(){
        string xaml=@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TreeViewItem'>
 <Grid>
  <Grid.ColumnDefinitions><ColumnDefinition Width='10'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
  <Grid.RowDefinitions><RowDefinition Height='Auto'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>
  <ToggleButton x:Name='Expander' Width='10' Height='16' ClickMode='Press' Focusable='False' HorizontalAlignment='Left' VerticalAlignment='Center' IsChecked='{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}'>
   <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'>
    <Border Background='Transparent'><Path x:Name='Chevron' Data='M 3,4 L 7,8 L 3,12' Stroke='{DynamicResource Muted}' StrokeThickness='1.2' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' HorizontalAlignment='Center' VerticalAlignment='Center' RenderTransformOrigin='0.5,0.5'/></Border>
    <ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Chevron' Property='RenderTransform'><Setter.Value><RotateTransform Angle='90'/></Setter.Value></Setter></Trigger></ControlTemplate.Triggers>
   </ControlTemplate></ToggleButton.Template>
  </ToggleButton>
  <Border x:Name='Row' Grid.Column='1' Margin='0,2' Padding='4,3' CornerRadius='4' Background='Transparent'><ContentPresenter x:Name='PART_Header' ContentSource='Header' HorizontalAlignment='Stretch' VerticalAlignment='Center'/></Border>
  <ItemsPresenter x:Name='ItemsHost' Grid.Row='1' Grid.Column='1'/>
 </Grid>
 <ControlTemplate.Triggers>
  <Trigger Property='HasItems' Value='False'><Setter TargetName='Expander' Property='Visibility' Value='Hidden'/></Trigger>
  <Trigger Property='IsExpanded' Value='False'><Setter TargetName='ItemsHost' Property='Visibility' Value='Collapsed'/></Trigger>
  <Trigger SourceName='Row' Property='IsMouseOver' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource Hover}'/></Trigger>
  <Trigger Property='IsSelected' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource ActiveFill}'/></Trigger>
  <MultiTrigger><MultiTrigger.Conditions><Condition Property='IsSelected' Value='True'/><Condition SourceName='Row' Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Row' Property='Background' Value='{DynamicResource ActiveHover}'/></MultiTrigger>
 </ControlTemplate.Triggers>
</ControlTemplate>";
        var style=new Style(typeof(TreeViewItem));style.Setters.Add(new Setter(Control.TemplateProperty,(ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml)));return style;
    }
    void RefreshOutline(){
        if(outline.Visibility!=Visibility.Visible)return;SyncOutlineTabFont();sourceOutlineDirty=false;if(outline.FontFamily.Source!=family)outline.FontFamily=new FontFamily(family);if(PlainText){CancelSourceOutline();ClearOutlineItems();return;}
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
            if(outlineMenuToggle!=null){outlineMenuToggle.IsChecked=false;outlineMenuToggle.IsEnabled=false;}
        }else{
            if(outlineSuspendedByTextMode){outlineSuspendedByTextMode=false;SetOutlinePanel(true);}
            if(outlineMenuToggle!=null){outlineMenuToggle.IsEnabled=true;outlineMenuToggle.IsChecked=outline.Visibility==Visibility.Visible;}
        }
        outlineButton.ToolTip=OutlineToggleTip();
        outlineButton.IsEnabled=!PlainText;
    }
    static IEnumerable<Paragraph> EnumerateParagraphs(BlockCollection blocks){foreach(Block block in blocks){var paragraph=block as Paragraph;if(paragraph!=null)yield return paragraph;else if(block is Section){foreach(var child in EnumerateParagraphs(((Section)block).Blocks))yield return child;}else if(block is System.Windows.Documents.List){foreach(ListItem item in ((System.Windows.Documents.List)block).ListItems)foreach(var child in EnumerateParagraphs(item.Blocks))yield return child;}}}
    void RefreshSourceOutline(){RefreshSmallSourceOutline();}
    static BlockCollection ParentBlocks(Block block){var parent=block.Parent;if(parent is FlowDocument)return ((FlowDocument)parent).Blocks;if(parent is Section)return ((Section)parent).Blocks;if(parent is ListItem)return ((ListItem)parent).Blocks;if(parent is TableCell)return ((TableCell)parent).Blocks;return null;}
    void SetHeading(int level){LiveHeading(level);}
    void ClearTextColor(){LiveColor(null);}

    /* 2026-11-09 (user): the four toggle buttons say what pressing them will do, so their tooltips
       follow the state they are in (edit mode, the 원문 view, the 목차 panel, TXT mode). Each state
       update assigns the string through T(); ApplyLanguage re-assigns it after the translation pass.
       BindShortcutTip (ShortcutSettings.cs) rewrites the tooltip from a fixed caption on every
       ToolTipOpening and it is registered before this pass, so the handlers here run last and keep
       the state wording (with the outline key read from the current F-key assignment). */
    string EditToggleTip(){return T(editing?"수정 모드 끄기: 읽기 모드로 전환합니다. (Ctrl+E)":"수정 모드 켜기: 본문을 편집할 수 있습니다. (Ctrl+E)");}
    string SourceToggleTip(){return T(preview.IsChecked==true?"원문 보기 끄기: 서식 기호를 숨깁니다.":"원문 보기 켜기: 서식 기호(#, ** 등)를 표시합니다.");}
    string OutlineToggleTip(){string shortcut=FunctionKeyText("outline");return T(outline.Visibility==Visibility.Visible?"목차 닫기: 제목 목록을 숨깁니다.":"목차 열기: 제목 목록을 표시합니다.")+(shortcut.Length==0?"":" ("+shortcut+")");}
    string TextToggleTip(){return T(PlainText?"TXT 모드 끄기: .md 파일 형식으로 전환합니다.":"TXT 모드 켜기: .txt 파일 형식으로 전환합니다.");}
    bool stateTooltipsReady;
    void SetupStateTooltips(){
        if(stateTooltipsReady)return;stateTooltipsReady=true;
        editButton.ToolTipOpening+=delegate{editButton.ToolTip=EditToggleTip();};
        preview.ToolTipOpening+=delegate{preview.ToolTip=SourceToggleTip();};
        if(outlineButton!=null)outlineButton.ToolTipOpening+=delegate{outlineButton.ToolTip=OutlineToggleTip();};
        /* the 원문 toggle is collapsed in read mode (measures 0): widths are agreed again when it shows */
        preview.IsVisibleChanged+=delegate{SyncViewButtonWidths();};
    }
    /* 11-09 (user): 목차, 원문 and the status-bar TXT button keep one width in every UI language: the widest
       label decides and the other two take it as MinWidth. The collapsed 22 px squares are already equal, so
       the pass waits for the wide state. Called after a language switch and after each compact/wide change. */
    bool viewWidthSyncQueued;
    void SyncViewButtonWidths(){
        if(viewWidthSyncQueued)return;viewWidthSyncQueued=true;
        Dispatcher.BeginInvoke(new Action(delegate{
            viewWidthSyncQueued=false;
            Control[] buttons={preview,outlineButton,quickText};
            foreach(var b in buttons)if(b==null||b.Padding.Left==0&&b.MinWidth==Ui.CompactToggleBox)return;
            double widest=0;
            foreach(var b in buttons){b.ClearValue(FrameworkElement.MinWidthProperty);b.Measure(new Size(double.PositiveInfinity,double.PositiveInfinity));widest=Math.Max(widest,b.DesiredSize.Width);}
            widest=Math.Ceiling(widest);
            foreach(var b in buttons)b.MinWidth=widest;
        }),System.Windows.Threading.DispatcherPriority.Loaded);
    }
    /* The outline's heading level mark: the panel's version of the editor's HeadingMark. Level n is
       n parallelograms at a 4 px pitch, leaning right by 18 degrees, drawn at half opacity in the
       panel's "Muted" ink (a dynamic resource, so a theme switch repaints without a rebuild). The
       mark grows with the level like the one left of a heading in the document, and stays compact -
       at 100% DPI H6 is about 27 px wide. */
    public sealed class OutlineLevelMark : FrameworkElement {
        internal static readonly DependencyProperty LevelProperty=DependencyProperty.Register("Level",typeof(int),typeof(OutlineLevelMark),new FrameworkPropertyMetadata(0,FrameworkPropertyMetadataOptions.AffectsMeasure|FrameworkPropertyMetadataOptions.AffectsRender));
        internal static readonly DependencyProperty InkProperty=DependencyProperty.Register("Ink",typeof(Brush),typeof(OutlineLevelMark),new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
        const double Pitch=4.0,BarWidth=2.5,BarHeight=10.0,BoxHeight=12.0,SlantDegrees=18.0;
        internal int Level {get{return (int)GetValue(LevelProperty);}set{SetValue(LevelProperty,value);}}
        internal Brush Ink {get{return (Brush)GetValue(InkProperty);}set{SetValue(InkProperty,value);}}
        // One geometry for all of the level's bars: a lean each bar's top edge shares with the editor's mark.
        static double Lean(){return BarHeight*Math.Tan(SlantDegrees*Math.PI/180.0);}
        protected override Size MeasureOverride(Size availableSize){
            int level=Math.Max(0,Math.Min(6,Level));
            return new Size(level<=0?0:level*Pitch+Lean(),BoxHeight);
        }
        protected override void OnRender(DrawingContext drawingContext){
            int level=Math.Max(0,Math.Min(6,Level));Brush ink=Ink;if(level<=0||ink==null)return;
            double lean=Lean(),top=(BoxHeight-BarHeight)/2.0,bottom=top+BarHeight;
            var geometry=new StreamGeometry();
            using(StreamGeometryContext context=geometry.Open()){
                for(int i=0;i<level;i++){double x=i*Pitch;context.BeginFigure(new Point(x,bottom),true,true);context.LineTo(new Point(x+BarWidth,bottom),true,false);context.LineTo(new Point(x+BarWidth+lean,top),true,false);context.LineTo(new Point(x+lean,top),true,false);}
            }
            geometry.Freeze();
            drawingContext.PushOpacity(0.5);drawingContext.DrawGeometry(ink,null,geometry);drawingContext.Pop();
        }
    }
}
}
