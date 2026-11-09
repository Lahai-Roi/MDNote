using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MDNote {
public partial class MainWindow {
    // --toolbar-shot <dir>: renders the command rows without ever showing a window (no HWND, no focus change).
    internal static int RunToolbarShot(string outDir){return ToolbarShotRunner.Run(outDir);}
    // b39 contact sheets (windowless): closed heading captions + open list items, and every toolbar icon at 2x with its name.
    internal FrameworkElement HeadingCapsStrip(){
        var root=new StackPanel {Margin=new Thickness(12)};var row=new StackPanel {Orientation=Orientation.Horizontal};
        for(int level=0;level<=6;level++){var cell=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,18,0)};var caption=HeadingCaptionBlock(level);cell.Children.Add(caption);cell.Children.Add(Ui.Chevron());row.Children.Add(cell);}
        root.Children.Add(row);
        var menu=new Menu {Margin=new Thickness(0,14,0,0),HorizontalAlignment=HorizontalAlignment.Left,MinWidth=150,ItemsPanel=new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel)))};menu.Items.Add(new MenuItem {Header=HeadingText(T("본문"),0,HeadingBodyMenuSize,0)});menu.Items.Add(new Separator());
        for(int i=0;i<headingButtons.Count;i++)menu.Items.Add(new MenuItem {Header=HeadingText(HeadingName(i+1),i+1,HeadingMenuSizes[i],0)});
        root.Children.Add(menu);
        Ui.ToolbarText(root);var holder=new ContentControl {Content=root,FontFamily=Ui.LabelFamily,FontSize=Ui.LabelSize};holder.SetResourceReference(Control.ForegroundProperty,"Ink");return holder;
    }
    internal FrameworkElement ToolbarCaptionMeasure(int level){return HeadingCaptionBlock(level);}
    internal TextBox[] ToolbarSearchFields(){return new TextBox[]{query,replacement};}
    internal CheckBox ToolbarSourceToggle(){return preview;}
    internal FrameworkElement[] ToolbarHeadingParts(){return new FrameworkElement[]{headingButton,headingChevron,headingHover,headingSlot,sizes};}
    internal FrameworkElement[][] ToolbarSplitParts(){return new[]{new FrameworkElement[]{headingHover,headingButton,headingChevron},new FrameworkElement[]{saveHover,saveMain,saveChevron},new FrameworkElement[]{colorHover,colorMain,colorArrow},new FrameworkElement[]{listHover,listButton,listChevron}};}
    internal ComboBox ToolbarStartupSize(bool live){textMode=false;SetText("font number");SetMode(true);ApplyLanguage();return sizes;}
    internal void ToolbarHeadingLevel(int level){if(level>0)lastHeadingLevel=level;liveHeadingLevel=level;for(int i=0;i<headingButtons.Count;i++)headingButtons[i].IsChecked=i+1==level;headingButton.Tag=level>0?"Active":null;UpdateHeadingCaption();}
    internal void ToolbarSizeCaption(string value){bool previous=updatingTools;updatingTools=true;try{sizes.Text=value;}finally{updatingTools=previous;}}
    internal void ToolbarShotState(){preview.IsChecked=true;OpenSearch(true);ToolbarHeadingLevel(1);boldButton.Tag="Active";ToolbarSizeCaption("32 px");}
    internal DependencyObject[] ToolbarTextRoots(){return new DependencyObject[]{root.Children[0],documentTabs,ribbonHost,tableToolsHost,searchHost,statusBar,saveMenu,headingMenu,colorPopup.Child};}
    internal void ToolbarSearchPrefillCheck(){
        SetText("selected sample");SetMode(true);source.Select(0,8);query.Text="";OpenSearch(false);
        if(query.Text!="")throw new InvalidOperationException("Search must not pre-fill from the selection");
        source.Select(0,0);query.Text="kept query";OpenSearch(true);if(query.Text!="kept query")throw new InvalidOperationException("Opening search replaced an unselected query");
        SetText("");query.Clear();replacement.Clear();
    }
    internal FrameworkElement IconSheet(bool darkMode){
        if(darkMode)SetDark(true);
        var wrap=new WrapPanel {Width=620,Margin=new Thickness(8)};
        foreach(string key in new string[]{"new","open","save","find","replace","dark","menu","undo","redo","bold","italic","strike","highlight","color","check","quote","code","rule","link","image","table","alignL","alignC","alignR","rowAdd","rowRemove","colAdd","colRemove","tableDelete","edit","up","down","close","source","markdown","paragraph","print"}){
            var cell=new StackPanel {Width=44,Margin=new Thickness(0,0,0,6)};
            var guide=new Grid {Width=28,Height=28,HorizontalAlignment=HorizontalAlignment.Center};
            var box=new System.Windows.Shapes.Rectangle {Width=20,Height=20,Stroke=new SolidColorBrush(Color.FromArgb(70,128,128,128)),StrokeThickness=.5,StrokeDashArray=new DoubleCollection(new double[]{2,2})};guide.Children.Add(box);
            var button=new Button {Width=28,Height=28,Padding=new Thickness(0),Focusable=false};
            if(key=="color"){var swatch=new Border {Background=Brushes.Red};button.Content=Ui.ColorGlyph(swatch);}else button.Content=Ui.GridIcon(key);
            guide.Children.Add(button);cell.Children.Add(guide);
            cell.Children.Add(new TextBlock {Text=key,FontSize=7,HorizontalAlignment=HorizontalAlignment.Center,Opacity=.8});
            wrap.Children.Add(cell);
        }
        var holder=new ContentControl {Content=wrap,FontFamily=FontFamily};holder.SetResourceReference(Control.ForegroundProperty,"Ink");return holder;
    }
    /* b52: the two candidate "원문" page+braces marks beside the icons they sit next to, at 1x (the toolbar's real size) and 4x.
       Same recipe as IconSheet: a 28 px cell, real Buttons (the icon strokes bind colour to the nearest ButtonBase), 2x capture. */
    internal FrameworkElement SourceBraceSheet(bool darkMode){
        if(darkMode)SetDark(true);
        var root=new StackPanel {Margin=new Thickness(10)};
        string[] keys={"sourceBraceA","sourceBraceB","sourceCode","image","table"};
        foreach(double zoom in new double[]{1,4}){
            var row=new StackPanel {Orientation=Orientation.Horizontal};
            foreach(string key in keys){
                var cell=new StackPanel {Margin=new Thickness(0,0,10,0),Width=28*zoom+6};
                var button=new Button {Width=28,Height=28,Padding=new Thickness(0),Focusable=false,HorizontalAlignment=HorizontalAlignment.Left};
                button.SetResourceReference(Control.ForegroundProperty,"Ink");
                button.Content=Ui.CenteredInkIcon(key,16,1);
                button.LayoutTransform=new ScaleTransform(zoom,zoom);
                var slot=new Grid {Height=28*zoom,HorizontalAlignment=HorizontalAlignment.Left};slot.Children.Add(button);
                cell.Children.Add(slot);
                cell.Children.Add(new TextBlock {Text=key+" "+zoom+"x",FontSize=7,Opacity=.8});
                row.Children.Add(cell);
            }
            root.Children.Add(row);
            root.Children.Add(new Border {Height=6});
        }
        var holder=new ContentControl {Content=root,FontFamily=Ui.LabelFamily};holder.SetResourceReference(Control.ForegroundProperty,"Ink");return holder;
    }
    internal FrameworkElement ToolbarShotPrepare(bool darkMode){
        SetDark(darkMode);
        SetMode(true);
        tableToolsHost.Visibility=Visibility.Visible;foreach(var action in tableActions)action.Visibility=Visibility.Visible;if(tableAlignDivider!=null)tableAlignDivider.Visibility=Visibility.Visible;
        OpenSearch(true);
        var content=(FrameworkElement)Content;Content=null;return content;
    }
    internal Border ToolbarRibbonHost(){return ribbonHost;}
    internal ScrollViewer ToolbarRibbonScroll(){return ribbonScroll;}
    internal FrameworkElement ToolbarRibbonPreview(){return ribbonPreviewHost;}
    internal FrameworkElement ToolbarTopHost(){return topBarHost;}
    internal Button ToolbarRibbonOverflow(){return ribbonOverflow;}
    internal Button ToolbarTopOverflow(){return topOverflow;}
    internal void ToolbarLoadSample(string path){LoadFileCore(path);}
    internal bool ToolbarEditing(){return editing;}
    /* b55: the list dropdown's slot is caption-independent; it was tuned on 목록. A probe switch lets the
       shot carry the caption in every language and report what the label got against what its glyphs need. */
    internal void ToolbarLanguage(string code){language=code;ApplyLanguage();}
    /* --toolbar-shot panel shots: the runner is outside this partial class, so these keep the same
       narrow internal surface as ToolbarLoadSample above. SetOutlinePanel and ShowOutlineTab are the
       exact paths the toolbar toggle, the View menu, F10 and the header's two tabs use; the field
       readers let the runner check the tree it just drove (the folder rows arrive from the pool). */
    internal void PanelShotOpen(string path){LoadFileCore(path);SetMode(false);}
    internal void PanelShotTheme(bool dark){SetDark(dark);}
    internal void PanelShotOutline(bool show){SetOutlinePanel(show);}
    internal void PanelShotFilesTab(){ShowOutlineTab(true);}
    /* --toolbar-shot format sample: the runner sits outside this partial class, so edit mode, the
       Live/Basic switch, the caret and the quiet close get the same narrow internal surface as the
       panel shots above. The caret clamps to the document so a caller offset can never throw. */
    internal void FormatShotEdit(){SetMode(true);}
    internal int FormatShotLength(){return source==null||source.Document==null?0:source.Document.TextLength;}
    // Assigning CaretIndex does not scroll, so a caret placed after an earlier shot's scroll kept
    // that shot's viewport (the line-1 picture showed the document end and its YAML rows read as
    // missing). Each shot now scrolls to its own caret: line 1 goes to the top of the document,
    // any other caret is brought just inside the viewport (SafeBringCaretToView).
    internal void FormatShotCaret(int offset){
        if(source==null||source.Document==null)return;
        int length=source.Document.TextLength;
        offset=offset<0?0:(offset>length?length:offset);
        source.CaretIndex=offset;
        if(offset==0)source.ScrollTo(1,0);
        else MarkdownLivePreview.Session.SafeBringCaretToView(source.TextArea);
    }
    internal void FormatShotLive(bool live){SetLiveMode(live);}
    internal void FormatShotClose(){saved=text;recoveredDocument=false;Close();}
    internal int PanelShotOutlineCount(){return outline==null?0:outline.Items.Count;}
    internal string PanelShotOutlineTitles(){var titles=new List<string>();if(outline!=null)PanelShotOutlineTitles(outline.Items,titles);return String.Join(" | ",titles.ToArray());}
    static void PanelShotOutlineTitles(ItemCollection items,List<string> titles){
        foreach(object entry in items){var item=entry as TreeViewItem;if(item==null)continue;string name=System.Windows.Automation.AutomationProperties.GetName(item);if(!String.IsNullOrEmpty(name))titles.Add(name);PanelShotOutlineTitles(item.Items,titles);}
    }
    internal string PanelShotPanelState(){return "outline="+(outline==null?"null":outline.Visibility.ToString())+" filesTab="+outlineFilesTab+" open="+outlinePanelOpen+" folderPanel="+(folderPanel==null?"null":folderPanel.Visibility.ToString());}
    internal TreeViewItem PanelShotFolderItem(string name){
        if(folderTree==null)return null;
        foreach(object child in folderTree.Items){var item=child as TreeViewItem;if(item==null)continue;var entry=item.Tag as FolderTreeEntry;if(entry!=null&&entry.IsFolder&&String.Equals(entry.Name,name,StringComparison.Ordinal))return item;}
        return null;
    }
    internal string[] PanelShotFolderNames(){
        if(folderTree==null)return new string[0];
        var names=new List<string>();
        foreach(object child in folderTree.Items){var item=child as TreeViewItem;if(item==null)continue;var entry=item.Tag as FolderTreeEntry;if(entry!=null)names.Add(entry.Name);}
        return names.ToArray();
    }
    internal void PanelShotExpand(TreeViewItem item){if(item!=null)item.IsExpanded=true;}
    internal bool PanelShotFolderChildren(TreeViewItem item){
        if(item==null)return false;
        var entry=item.Tag as FolderTreeEntry;
        if(entry==null||!entry.Loaded)return false;
        foreach(object child in item.Items){var node=child as TreeViewItem;if(node!=null&&node.Tag is FolderTreeEntry)return true;}
        return false;
    }
    internal string ToolbarCaretFormat(int line,int column){return CaretPositionText(line,column);}
    internal string ToolbarStatusHead(){var value=status==null?"":status.Text;int at=value.IndexOf("   ·",StringComparison.Ordinal);return at<0?value:value.Substring(0,at);}
    internal Control[] ToolbarCaptionControls(){return new Control[]{editButton,listButton,taskButton,quoteButton,blockButton,ruleButton,preview,headingButton,sizes};}
    internal Menu ToolbarMenu(){return topMenu;}
    internal string ToolbarBodyFonts(){return source.FontFamily.Source+"|"+family+"|"+fontSize;}
    internal TextBlock ToolbarListCaptionLabel(){var caption=listButton.Content as StackPanel;return caption==null?null:caption.Children[1] as TextBlock;}
    internal double ToolbarListCaptionWidth(){var caption=listButton.Content as FrameworkElement;return caption==null?0:caption.Width;}
    internal double ToolbarListOuterWidth(){return listButton.ActualWidth;}
    internal bool ToolbarListButtonInTree(Visual host){return listButton.IsDescendantOf(host);}
    internal string ToolbarListCaptionState(){
        var caption=listButton.Content as FrameworkElement;var label=ToolbarListCaptionLabel();if(caption==null||label==null)return "missing";
        caption.UpdateLayout();
        string chain="";
        DependencyObject node=label;int guard=0;
        while(node!=null&&guard++<12){var element=node as FrameworkElement;var ui=node as UIElement;chain+=" "+node.GetType().Name+"(w="+(element==null?0:element.ActualWidth).ToString("0.##")+(ui!=null&&(ui.Clip!=null||ui.ClipToBounds)?" CLIP":"")+")";node=VisualTreeHelper.GetParent(node);}
        var face=new Typeface(label.FontFamily,label.FontStyle,label.FontWeight,label.FontStretch);
        var formatted=new FormattedText(label.Text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,label.FontSize,Brushes.Black,null,TextFormattingMode.Display,1.0);
        Rect ink=formatted.BuildGeometry(new Point()).Bounds;
        return "text=["+label.Text+"] font="+label.FontFamily.Source+"/"+label.FontSize+" captionWidth="+caption.Width+" caption="+caption.ActualWidth+" labelDesired="+label.DesiredSize.Width.ToString("0.##")+" labelActual="+label.ActualWidth.ToString("0.##")+" displayAdvance="+formatted.WidthIncludingTrailingWhitespace.ToString("0.##")+" ink="+ink.Left.ToString("0.##")+".."+ink.Right.ToString("0.##")+" ancestors:"+chain;
    }
}

static class ToolbarShotRunner {
    const int ShotHeight=300;
    sealed class Item {internal string Kind,Label,Chrome;internal Rect Box,Ink;internal bool Bordered;}

    internal static int Run(string outDir){
        Directory.CreateDirectory(outDir);
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var report=new StringBuilder();
        try{
        foreach(int width in new int[]{1050,1400}){
            foreach(bool dark in new bool[]{false,true}){
                var window=new MainWindow();
                var content=window.ToolbarShotPrepare(dark);
                var host=new Border {Child=content,Width=width,Height=ShotHeight};
                host.Resources.MergedDictionaries.Add(window.Resources);
                host.SetResourceReference(Border.BackgroundProperty,"Paper");
                Settle(host,width);
                foreach(var chrome in window.ToolbarTextRoots())ValidateText(chrome);
                string name="toolbar-"+width+(dark?"-dark":"-light");
                Save(host,width,Path.Combine(outDir,name+".png"));
                Describe(host,report,name,dark?null:Render(host,width,ShotHeight));
                report.Append("theme");foreach(string key in new[]{"Paper","Canvas","Ribbon","Line","Hover","ActiveFill","ActiveHover","Pressed","SearchInk","SearchMuted"})report.Append(" ").Append(key).Append("=").Append(window.Resources[key]);report.AppendLine();
                /* before any template is swapped: a ContentControl re-templated windowlessly does not re-create its
                   content child, and the list split's row would leave the visual tree */
                if(width==1050&&!dark){MeasureListCaptions(window,host,report,dark,outDir,name);ShootEditRead(window,host,report,outDir);}
                MeasureSourceToggle(window,host,report,dark);MeasureSearch(window,host,width,report,dark,outDir,name);
                MeasureStartupSize(window,host,report,dark,outDir,name);
                MeasureHeadingDropdown(window,host,report,dark,outDir,name,true);
                MeasureSplits(window,host,report,dark,outDir,name);
                MeasureSplitSegments(window,host,report,dark,outDir,name);
                ValidateTheme(window.Resources,dark);
                if(width==1050&&!dark){var captionFaces=new List<string>();for(int level=0;level<=6;level++){string face=MeasureCaption(window,level,report);if(face.Length>0){if(captionFaces.IndexOf(face)>=0)throw new InvalidOperationException("Heading captions share a face: "+face);captionFaces.Add(face);}}report.AppendLine("caption faces: "+captionFaces.Count+" distinct level faces = "+String.Join(" / ",captionFaces.ToArray()));window.ToolbarSearchPrefillCheck();report.AppendLine("search: no selection pre-fill, existing query kept PASS");}
                if(width==1050)MeasureDialogButtons(window,report,dark,outDir);
                /* the narrow check needs the row in the host: it used to run after host.Child=null, so every
                   control was "detached" and the overlap test never looked at a single rectangle (b56) */
                if(width==1050&&!dark)MeasureNarrowRibbon(window,host,report,outDir);
                host.Child=null;
            }
        }
        // Capture the actual product only. The old segmented mock-up reparented live dividers
        // and left their LayoutUpdated handlers referring to nodes outside the original ancestor.
        {/* heading-caps.png: closed captions (本文, 제목 1..6) and the open list items, rendered offscreen */
            var window=new MainWindow();var strip=window.HeadingCapsStrip();var host=new Border {Child=strip,Width=620,Height=300};host.Resources.MergedDictionaries.Add(window.Resources);host.SetResourceReference(Border.BackgroundProperty,"Paper");
            host.LayoutTransform=new ScaleTransform(2,2);Settle(host,1240,600);Save(host,1240,600,Path.Combine(outDir,"heading-caps.png"));host.Child=null;
        }
        {/* icons-sheet.png: every toolbar icon on the 28 px cell / 20 px live area, 2x, light on top and dark below */
            var parts=new List<BitmapSource>();
            foreach(bool dark in new bool[]{false,true}){var window=new MainWindow();var sheet=window.IconSheet(dark);var host=new Border {Child=sheet,Width=640,Height=300};host.Resources.MergedDictionaries.Add(window.Resources);host.SetResourceReference(Border.BackgroundProperty,"Paper");host.LayoutTransform=new ScaleTransform(2,2);Settle(host,1280,600);var bitmap=new RenderTargetBitmap(1280,600,96,96,PixelFormats.Pbgra32);bitmap.Render(host);parts.Add(bitmap);host.Child=null;}
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawImage(parts[0],new Rect(0,0,1280,600));dc.DrawImage(parts[1],new Rect(0,600,1280,600));}
            var all=new RenderTargetBitmap(1280,1200,96,96,PixelFormats.Pbgra32);all.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(all));using(var stream=File.Create(Path.Combine(outDir,"icons-sheet.png")))encoder.Save(stream);
        }
        {/* source-braces.png: the two candidate 원문 marks at 1x and 4x next to the icons they sit beside, light above dark (b52) */}
        {
            var parts=new List<BitmapSource>();
            foreach(bool dark in new bool[]{false,true}){var window=new MainWindow();var sheet=window.SourceBraceSheet(dark);var host=new Border {Child=sheet,Width=640,Height=300};host.Resources.MergedDictionaries.Add(window.Resources);host.SetResourceReference(Border.BackgroundProperty,"Paper");host.LayoutTransform=new ScaleTransform(2,2);Settle(host,1280,600);var bitmap=new RenderTargetBitmap(1280,600,96,96,PixelFormats.Pbgra32);bitmap.Render(host);parts.Add(bitmap);host.Child=null;}
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen()){dc.DrawImage(parts[0],new Rect(0,0,1280,600));dc.DrawImage(parts[1],new Rect(0,600,1280,600));}
            var all=new RenderTargetBitmap(1280,1200,96,96,PixelFormats.Pbgra32);all.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(all));using(var stream=File.Create(Path.Combine(outDir,"source-braces.png")))encoder.Save(stream);
        }
        MeasureLiveOverflowWindow(report,outDir);
        MeasureOutlinePanelShots(report,outDir);
        MeasureFormatSampleShots(report,outDir);
        File.WriteAllText(Path.Combine(outDir,"toolbar-shot.txt"),report.ToString(),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir,"toolbar-shot-PASS.txt"),"PASS",new UTF8Encoding(false));
        return 0;
        }catch(Exception error){report.AppendLine(error.ToString());throw;}finally{File.WriteAllText(Path.Combine(outDir,"toolbar-shot.txt"),report.ToString(),new UTF8Encoding(false));var alignment=new StringBuilder();foreach(string line in report.ToString().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries))if(line.StartsWith("alignment ",StringComparison.Ordinal))alignment.AppendLine(line);File.WriteAllText(Path.Combine(outDir,"caption-alignment.txt"),alignment.ToString(),new UTF8Encoding(false));}
    }

    // Design mock-ups only (not product code): "segment" wraps each divider-separated run in a soft rounded group; "tonal" gives every bar button a soft fill.
    static void Variant(FrameworkElement root,string variant){
        var dividers=new List<Border>();FindDividers(root,dividers);
        var panels=new List<Panel>();foreach(var d in dividers){var panel=VisualTreeHelper.GetParent(d) as Panel;if(panel!=null&&!panels.Contains(panel))panels.Add(panel);}
        if(variant=="segment"){
            foreach(var panel in panels){
                var children=new List<UIElement>();foreach(UIElement child in panel.Children)children.Add(child);panel.Children.Clear();
                StackPanel run=null;
                foreach(var child in children){
                    var line=child as Border;if(line!=null&&line.Width<=1.5){run=null;line.Visibility=Visibility.Collapsed;panel.Children.Add(line);continue;}
                    if(run==null){run=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};var pill=new Border {Child=run,CornerRadius=new CornerRadius(8),Padding=new Thickness(2),Margin=new Thickness(3,0,3,0),VerticalAlignment=VerticalAlignment.Center};pill.Background=new SolidColorBrush(Color.FromArgb(18,0,0,0));panel.Children.Add(pill);}
                    run.Children.Add(child);
                }
            }
            return;
        }
        var buttons=new List<ButtonBase>();FindButtons(root,buttons);
        foreach(var button in buttons){button.Background=new SolidColorBrush(Color.FromArgb(18,0,0,0));var m=button.Margin;button.Margin=new Thickness(Math.Max(m.Left,2),m.Top,Math.Max(m.Right,2),m.Bottom);}
        foreach(var d in dividers)d.Visibility=Visibility.Collapsed;
    }
    static void FindDividers(DependencyObject node,List<Border> result){for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);var line=child as Border;if(line!=null&&line.Width<=1.5&&line.Height>=8&&line.Height<=20){result.Add(line);continue;}FindDividers(child,result);}}
    static void FindButtons(DependencyObject node,List<ButtonBase> result){for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);if(child is ComboBox||child is ScrollBar||child is TextBox)continue;var button=child as ButtonBase;if(button!=null&&!(button is CheckBox&&((CheckBox)button).Style==null)){var b=child as FrameworkElement;if(b.ActualHeight>=20&&b.ActualHeight<=30)result.Add(button);continue;}FindButtons(child,result);}}

    static void Settle(FrameworkElement host,int width){Settle(host,width,ShotHeight);}
    static void Settle(FrameworkElement host,int width,int height){
        for(int i=0;i<8;i++){
            host.Measure(new Size(width,height));host.Arrange(new Rect(0,0,width,height));host.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));
        }
    }

    static void Save(FrameworkElement host,int width,string path){Save(host,width,ShotHeight,path);}
    static BitmapSource Render(FrameworkElement host,int width,int height){var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(host);return bitmap;}
    /* vertical ink extent (top,bottom row of pixels darker than the light paper) inside a region of a light-mode shot */
    static string InkRows(BitmapSource bitmap,Rect region){int x0=(int)Math.Floor(region.Left),x1=(int)Math.Ceiling(region.Right),y0=(int)Math.Floor(region.Top),y1=(int)Math.Ceiling(region.Bottom);int w=x1-x0,h=y1-y0;if(w<=0||h<=0)return "n/a";var px=new byte[w*h*4];bitmap.CopyPixels(new Int32Rect(x0,y0,w,h),px,w*4,0);int top=-1,bottom=-1;for(int y=0;y<h;y++)for(int x=0;x<w;x++){int i=(y*w+x)*4;if(px[i+2]<170&&px[i+1]<170&&px[i]<170){if(top<0)top=y;bottom=y;}}if(top<0)return "none";return "ink y="+F(y0+top)+".."+F(y0+bottom+1)+" centre="+F(y0+(top+bottom+1)/2.0);}
    static void Save(FrameworkElement host,int width,int height,string path){
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(host);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(path))encoder.Save(stream);
    }

    static Color ThemeColor(ResourceDictionary resources,string key){return ((SolidColorBrush)resources[key]).Color;}
    static void ValidateTheme(ResourceDictionary resources,bool dark){
        Color ribbon=ThemeColor(resources,"Ribbon"),hover=ThemeColor(resources,"Hover"),pressed=ThemeColor(resources,"Pressed"),active=ThemeColor(resources,"ActiveFill");
        int s=dark?-1:1;/* light: hover/pressed darken the ribbon; dark: they lift above it */
        if(s*(ribbon.R-hover.R)<14||s*(ribbon.G-hover.G)<14||s*(ribbon.B-hover.B)<14||s*(hover.R-pressed.R)<8||s*(hover.G-pressed.G)<8||s*(hover.B-pressed.B)<8||s*(hover.R-active.R)<8||s*(hover.G-active.G)<8||s*(hover.B-active.B)<8)throw new InvalidOperationException("Toolbar hover/pressed contrast failed");
        if(!dark)return;Color body=ThemeColor(resources,"Paper"),canvas=ThemeColor(resources,"Canvas"),line=ThemeColor(resources,"Line");
        if(body.R!=body.G||body.G!=body.B||canvas!=body||ribbon.R!=ribbon.G||ribbon.G!=ribbon.B||line.R!=line.G||line.G!=line.B||ribbon.R-body.R<12||Math.Abs(line.R-body.R)<10||Math.Abs(line.R-ribbon.R)<10)throw new InvalidOperationException("Dark toolbar/body contrast failed");
    }
    static void ValidateText(DependencyObject node){ValidateText(node,Ui.LabelFamily);}
    static void ValidateText(DependencyObject node,FontFamily expected){
        if(node==null)return;var block=node as TextBlock;var control=node as Control;var element=node as FrameworkElement;bool heading=element!=null&&(Object.Equals(element.Tag,"HeadingText")||Object.Equals(element.Tag,Ui.KeepFontTag));
        if(TextOptions.GetTextFormattingMode(node)!=TextFormattingMode.Display||TextOptions.GetTextRenderingMode(node)!=TextRenderingMode.ClearType)throw new InvalidOperationException("Chrome text is not Display/ClearType: "+node.GetType().Name);
        if(!heading&&(block!=null||control!=null)&&(System.Windows.Documents.TextElement.GetFontFamily(node).Source!=expected.Source||Math.Abs(System.Windows.Documents.TextElement.GetFontSize(node)-13)>.01||System.Windows.Documents.TextElement.GetFontWeight(node)!=FontWeights.Normal))throw new InvalidOperationException("Chrome label font mismatch: "+node.GetType().Name);
        if(heading)return;foreach(object child in LogicalTreeHelper.GetChildren(node)){var nested=child as DependencyObject;if(nested!=null)ValidateText(nested,expected);}
    }
    static Rect PixelInk(BitmapSource bitmap,Rect region,bool dark){return PixelInk(bitmap,region,dark,null);}
    static Rect PixelInk(BitmapSource bitmap,Rect region,bool dark,Color? foreground){
        int x0=Math.Max(0,(int)Math.Ceiling(region.Left)),x1=Math.Min(bitmap.PixelWidth,(int)Math.Floor(region.Right)),y0=Math.Max(0,(int)Math.Ceiling(region.Top)),y1=Math.Min(bitmap.PixelHeight,(int)Math.Floor(region.Bottom));int w=x1-x0,h=y1-y0;if(w<=0||h<=0)return Rect.Empty;
        var pixels=new byte[w*h*4];bitmap.CopyPixels(new Int32Rect(x0,y0,w,h),pixels,w*4,0);int left=w,right=-1,top=h,bottom=-1;
        for(int y=0;y<h;y++)for(int x=0;x<w;x++){int at=(y*w+x)*4;bool ink=dark?pixels[at]>100&&pixels[at+1]>100&&pixels[at+2]>100:pixels[at]<200&&pixels[at+1]<200&&pixels[at+2]<200;if(foreground.HasValue){Color color=foreground.Value;double paper=dark?39:255,contrast=paper-(color.R+color.G+color.B)/3.0;ink=(paper-(pixels[at]+pixels[at+1]+pixels[at+2])/3.0)/contrast>=.5;}if(ink){left=Math.Min(left,x);right=Math.Max(right,x);top=Math.Min(top,y);bottom=Math.Max(bottom,y);}}
        return right<left?Rect.Empty:new Rect(x0+left,y0+top,right-left+1,bottom-top+1);
    }
    /* 11-01 approved design (InsertControls.cs: HeadingWordWeight / HeadingDigitFaces / HeadingDigitWeights): the
       word has only three Malgun faces - Bold for 1-2, Regular for 3-4, and the Malgun Gothic Semilight family at
       Normal for 5-6 - and the trailing digit carries the step inside each pair with a named Segoe UI face
       (Black, Bold, Semibold, then Semilight/Light families at Normal). The old expectation here (Light 300 words,
       350/300 digit weights, one hardcoded "Malgun Gothic"/"Segoe UI" lookup for every level) predates that design,
       which is why it threw on both 2.0 and the current build while the caption itself looked right. */
    static readonly int[] CaptionWordWeights={400,700,700,400,400,400,400};
    static readonly int[] CaptionDigitWeights={400,900,700,600,400,400,400};
    static readonly string[] CaptionDigitFaces={"","Segoe UI Black","Segoe UI","Segoe UI Semibold","Segoe UI","Segoe UI Semilight","Segoe UI Light"};
    static readonly string[] CaptionLightWordFaces={"","","","","","Malgun Gothic Semilight","Malgun Gothic Semilight"};
    static string FirstFamily(FontFamily family){string source=family==null?"":family.Source;int comma=source.IndexOf(',');return (comma<0?source:source.Substring(0,comma)).Trim();}
    static string MeasureCaption(MainWindow window,int level,StringBuilder report){
        var caption=(TextBlock)window.ToolbarCaptionMeasure(level);caption.HorizontalAlignment=HorizontalAlignment.Left;var holder=new ContentControl {Content=caption,HorizontalContentAlignment=HorizontalAlignment.Left,VerticalContentAlignment=VerticalAlignment.Center,FontFamily=Ui.LabelFamily,FontSize=Ui.LabelSize,Foreground=(Brush)window.Resources["Ink"]};
        var host=new Border {Child=holder,Width=80,Height=32,Background=Brushes.White,LayoutTransform=new ScaleTransform(4,4)};Settle(host,320,128);
        Rect pixels=PixelInk(Render(host,320,128),new Rect(0,0,320,128),false);double centre=(pixels.Left+pixels.Width/2)/4,offset=centre-(BoundsIn(caption,host).Left+(caption.ActualWidth+caption.Margin.Right-caption.Margin.Left)/2);string captionName=System.Windows.Automation.AutomationProperties.GetName(caption);
        var runs=new List<System.Windows.Documents.Run>();foreach(var inline in caption.Inlines){var run=inline as System.Windows.Documents.Run;if(run==null)throw new InvalidOperationException("Heading caption is not real text");runs.Add(run);}
        if(runs.Count!=(level==0?1:2)||caption.FontFamily.Source!=Ui.LabelFamily.Source||caption.FontSize!=13)throw new InvalidOperationException("Heading caption font/run count");
        int wordWeight=runs[0].FontWeight.ToOpenTypeWeight();string wordFace=FirstFamily(runs[0].FontFamily);
        if(wordWeight!=CaptionWordWeights[level])throw new InvalidOperationException("Heading caption word weight: "+captionName+" wanted "+CaptionWordWeights[level]+" got "+wordWeight);
        if(CaptionLightWordFaces[level].Length>0&&!String.Equals(wordFace,CaptionLightWordFaces[level],StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Heading caption light word face: "+captionName+" wanted "+CaptionLightWordFaces[level]+" got "+wordFace);
        string signature="word="+wordFace+"/"+wordWeight;
        if(level>0){int digitWeight=runs[1].FontWeight.ToOpenTypeWeight();string digitFace=FirstFamily(runs[1].FontFamily);
            if(digitWeight!=CaptionDigitWeights[level]||!String.Equals(digitFace,CaptionDigitFaces[level],StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Heading caption digit face: "+captionName+" wanted "+CaptionDigitFaces[level]+"/"+CaptionDigitWeights[level]+" got "+digitFace+"/"+digitWeight);
            signature+=" digit="+digitFace+"/"+digitWeight;}
        string faces="";for(int i=0;i<runs.Count;i++){var face=new Typeface(runs[i].FontFamily,FontStyles.Normal,runs[i].FontWeight,FontStretches.Normal);GlyphTypeface glyph;if(!face.TryGetGlyphTypeface(out glyph)||glyph.StyleSimulations!=StyleSimulations.None)throw new InvalidOperationException("Synthetic heading face: "+captionName+" "+runs[i].FontWeight);faces+=" "+FirstFamily(runs[i].FontFamily)+"/"+runs[i].FontWeight.ToOpenTypeWeight()+"(glyph "+glyph.Weight.ToOpenTypeWeight()+")";}
        report.AppendLine("caption "+captionName+" width="+F(caption.ActualWidth)+" pixel4x inkWidth="+F(pixels.Width/4)+" centreX="+F(centre)+" centredInkOffset="+F(offset)+" faces="+faces);host.Child=null;
        if(Math.Abs(offset)>.5)throw new InvalidOperationException("Heading caption ink not centred: "+captionName+" "+F(offset));return signature;
    }
    static T Descendant<T>(DependencyObject node) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){var child=VisualTreeHelper.GetChild(node,i);var hit=child as T;if(hit!=null)return hit;hit=Descendant<T>(child);if(hit!=null)return hit;}return null;}
    static void MeasureSourceToggle(MainWindow window,FrameworkElement host,StringBuilder report,bool dark){
        var toggle=window.ToolbarSourceToggle();var icon=(FrameworkElement)toggle.Template.FindName("SourceGlyph",toggle);var content=(FrameworkElement)toggle.Template.FindName("ToggleContent",toggle);var text=Descendant<TextBlock>(content);if(icon==null||text==null)throw new InvalidOperationException("Source toggle text/icon missing");
        var slot=Descendant<Canvas>(icon);var canvas=slot==null?null:Descendant<Canvas>(slot);var path=canvas==null?null:Descendant<System.Windows.Shapes.Path>(canvas);if(canvas==null||!Ui.IsGrid(canvas)||path==null||Descendant<TextBlock>(icon)!=null)throw new InvalidOperationException("Source toggle must use the drawn braces icon, not a font glyph");
        var bitmap=Render(host,(int)host.ActualWidth,ShotHeight);Rect mark=PixelInk(bitmap,BoundsIn(icon,host),dark),word=PixelInk(bitmap,BoundsIn(text,host),dark);double scale=canvas.LayoutTransform.Value.M11,lineWeight=path.StrokeThickness*scale,expectedLine=Ui.IconLine*Ui.IconScale*(dark?1.2:1),gap=word.Left-mark.Right;report.AppendLine("source toggle height="+F(toggle.ActualHeight)+" markdownInk="+R(mark)+" 원문Ink="+R(word)+" markdownInkHeight="+F(mark.Height)+" 원문HangulInkHeight="+F(word.Height)+" hangulDelta="+F(mark.Height-word.Height)+" gap="+F(gap)+" stroke="+lineWeight.ToString("0.000",CultureInfo.InvariantCulture)+" rightInset="+F(host.ActualWidth-BoundsIn(toggle,host).Right));
        if(mark.IsEmpty||word.IsEmpty||Math.Abs(toggle.ActualHeight-22)>.01||mark.Height<10||mark.Height>16.5||gap<3||gap>6||Math.Abs(lineWeight-expectedLine)>.01||Math.Abs(host.ActualWidth-BoundsIn(toggle,host).Right-Ui.RightEdge)>.5)throw new InvalidOperationException("Source toggle size/alignment mismatch");
    }
    static string R(Rect box){return box.IsEmpty?"empty":"["+F(box.Left)+","+F(box.Top)+".."+F(box.Right)+","+F(box.Bottom)+"]";}
    static Rect ElementPixels(BitmapSource bitmap,FrameworkElement element,Visual host,bool dark){Rect region=BoundsIn(element,host);region.Inflate(.75,.75);return PixelInk(bitmap,region,dark);}
    static ControlTemplate ProbeTemplate(Control control){var template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(System.Windows.Markup.XamlWriter.Save(control.Template));foreach(TriggerBase entry in template.Triggers){var trigger=entry as Trigger;if(trigger!=null)trigger.Property=ProbeProperty(trigger.Property);var multi=entry as MultiTrigger;if(multi!=null)foreach(Condition condition in multi.Conditions)condition.Property=ProbeProperty(condition.Property);}return template;}
    static void MeasureStartupSize(MainWindow window,FrameworkElement host,StringBuilder report,bool dark,string outDir,string name){
        foreach(bool live in new[]{true}){
            var size=window.ToolbarStartupSize(live);Settle(host,(int)host.ActualWidth);var field=size.Template.FindName("PART_EditableTextBox",size) as TextBox;
            Rect ink=field==null?Rect.Empty:ElementPixels(Render(host,(int)host.ActualWidth,ShotHeight),field,host,dark);
            report.AppendLine("startup size "+name+" "+(live?"live":"basic")+" combo=["+size.Text+"] field=["+(field==null?"missing":field.Text)+"] ink="+R(ink));
            double number;if(field==null||field.Visibility!=Visibility.Visible||field.Text!=size.Text||!Double.TryParse(size.Text.Replace("px",""),NumberStyles.Float,CultureInfo.InvariantCulture,out number)||number<6||ink.IsEmpty||field.ActualWidth<20||BrushColor(field.Foreground)!=ThemeColor(window.Resources,"Ink"))throw new InvalidOperationException("Startup font-size number missing (before forced shot caption)");
            Save(host,(int)host.ActualWidth,Path.Combine(outDir,name+"-size-"+(live?"live":"basic")+".png"));
        }
    }
    static void MeasureSplits(MainWindow window,FrameworkElement host,StringBuilder report,bool dark,string outDir,string name){
        var splits=window.ToolbarSplitParts();string[] names={"heading","save","color","list"};var originals=new ControlTemplate[splits.Length];var tags=new object[splits.Length][];int width=(int)host.ActualWidth;
        try{
            for(int i=0;i<splits.Length;i++){
                var pair=(ContentControl)splits[i][0];var main=(Button)splits[i][1];var arrow=(Button)splits[i][2];originals[i]=pair.Template;tags[i]=new[]{main.Tag,arrow.Tag};main.Tag=arrow.Tag="Active";pair.Template=ProbeTemplate(pair);Settle(host,width);
                var surface=(Border)pair.Template.FindName("Surface",pair);var a=(Border)main.Template.FindName("Surface",main);var b=(Border)arrow.Template.FindName("Surface",arrow);
                Rect box=BoundsIn(surface,host),first=BoundsIn(main,host),last=BoundsIn(arrow,host);
                report.AppendLine("split "+name+" "+names[i]+" box="+R(box)+" padding="+surface.Padding+" radius="+surface.CornerRadius+" targets="+F(first.Width)+"/"+F(last.Width));
                if(!Object.ReferenceEquals(pair.Style,window.Resources["ToolbarSplit"])||!Object.ReferenceEquals(main.Style,window.Resources["ToolbarSplitPart"])||!Object.ReferenceEquals(arrow.Style,main.Style)||surface.CornerRadius!=new CornerRadius(6)||surface.Padding!=new Thickness(Ui.SplitSurfacePadding,0,0,0)||!box.Contains(first)||!box.Contains(last)||BrushColor(surface.Background).A!=0||BrushColor(a.Background).A!=0||BrushColor(b.Background).A!=0)throw new InvalidOperationException("Split shared style/active fill mismatch: "+names[i]);
                foreach(var target in new[]{main,arrow}){DependencyObject ancestor=target;while(ancestor!=null&&!Object.ReferenceEquals(ancestor,pair))ancestor=VisualTreeHelper.GetParent(ancestor);if(ancestor==null)throw new InvalidOperationException("Split target outside hover ancestor");pair.SetValue(ProbeHoverProperty,true);Settle(host,width);if(BrushColor(((Border)pair.Template.FindName("Surface",pair)).Background)!=ThemeColor(window.Resources,"Hover"))throw new InvalidOperationException("Split hover missing: "+names[i]);pair.SetValue(ProbeHoverProperty,false);}
                pair.SetValue(ProbeHoverProperty,true);
            }
            Settle(host,width);Save(host,width,Path.Combine(outDir,name+"-split-hover.png"));report.AppendLine("split hover: all four real templates, caption + arrow ancestry, Active tag without active fill PASS (input DP substitution; no HWND)");
        }finally{for(int i=0;i<splits.Length;i++)if(originals[i]!=null){var pair=(ContentControl)splits[i][0];pair.ClearValue(ProbeHoverProperty);pair.Template=originals[i];((Button)splits[i][1]).Tag=tags[i][0];((Button)splits[i][2]).Tag=tags[i][1];}Settle(host,width);}
    }
    // Segment-level split states: real part/pair templates with input DPs substituted; the divider's real MouseEnter/MouseLeave handlers are fired by RaiseEvent on the pair.
    static void SplitMouse(ContentControl pair,bool inside){pair.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=inside?System.Windows.Input.Mouse.MouseEnterEvent:System.Windows.Input.Mouse.MouseLeaveEvent});}
    static void MeasureSplitSegments(MainWindow window,FrameworkElement host,StringBuilder report,bool dark,string outDir,string name){
        var splits=window.ToolbarSplitParts();string[] names={"heading","list"};int[] index={0,3};int width=(int)host.ActualWidth;Color hoverColor=ThemeColor(window.Resources,"Hover"),pressedColor=ThemeColor(window.Resources,"Pressed");
        for(int n=0;n<names.Length;n++){
            var pair=(ContentControl)splits[index[n]][0];var main=(Button)splits[index[n]][1];var arrow=(Button)splits[index[n]][2];var row=(StackPanel)pair.Content;var divider=(Border)row.Children[1];
            var pairOriginal=pair.Template;var mainOriginal=main.Template;var arrowOriginal=arrow.Template;
            try{
                pair.Template=ProbeTemplate(pair);main.Template=ProbeTemplate(main);arrow.Template=ProbeTemplate(arrow);Settle(host,width);
                string[] states={"arrow-hover","main-hover","arrow-pressed"};
                foreach(string state in states){
                    if(n==1&&state!="arrow-hover")continue;
                    foreach(Button part in new[]{main,arrow}){part.SetValue(ProbeHoverProperty,false);part.SetValue(ProbePressedProperty,false);}pair.SetValue(ProbeHoverProperty,false);SplitMouse(pair,false);
                    Button target=state=="main-hover"?main:arrow;pair.SetValue(ProbeHoverProperty,true);SplitMouse(pair,true);target.SetValue(ProbeHoverProperty,true);if(state=="arrow-pressed")target.SetValue(ProbePressedProperty,true);Settle(host,width);
                    Color pairFill=BrushColor(((Border)pair.Template.FindName("Surface",pair)).Background),mainFill=BrushColor(((Border)main.Template.FindName("Surface",main)).Background),arrowFill=BrushColor(((Border)arrow.Template.FindName("Surface",arrow)).Background);
                    report.AppendLine("split segment "+name+" "+names[n]+" "+state+" pairFill="+pairFill+" mainFill="+mainFill+" arrowFill="+arrowFill+" dividerOpacity="+F(divider.Opacity)+" arrowBox="+R(BoundsIn(arrow,host))+" mainBox="+R(BoundsIn(main,host)));
                    if(pairFill!=hoverColor)throw new InvalidOperationException("Split segment: shared hover missing: "+names[n]+" "+state);
                    if(state=="main-hover"){if(arrowFill.A!=0||mainFill!=pressedColor)throw new InvalidOperationException("Split segment: main hover must not fill the arrow: "+names[n]);}
                    else{if(arrowFill.A==0||arrowFill==mainFill||arrowFill!=pressedColor||mainFill.A!=0||Math.Abs(divider.Opacity-1)>.001)throw new InvalidOperationException("Split segment: arrow-only fill/divider missing: "+names[n]+" "+state);}
                    if(n==0||state=="arrow-hover")Save(host,width,Path.Combine(outDir,name+"-"+names[n]+"-"+state+".png"));
                }
                SplitMouse(pair,false);Settle(host,width);if(Math.Abs(divider.Opacity)>.001)throw new InvalidOperationException("Split divider stays visible after mouse leave: "+names[n]);
                report.AppendLine("split segments "+name+" "+names[n]+": arrow-only fill, main-only (arrow transparent), arrow pressed, divider PASS (arrow Pressed="+pressedColor+", pair Hover="+hoverColor+")");
            }finally{
                foreach(Button part in new[]{main,arrow}){part.ClearValue(ProbeHoverProperty);part.ClearValue(ProbePressedProperty);}pair.ClearValue(ProbeHoverProperty);SplitMouse(pair,false);
                pair.Template=pairOriginal;main.Template=mainOriginal;arrow.Template=arrowOriginal;Settle(host,width);
            }
        }
    }
    static void MeasureHeadingDropdown(MainWindow window,FrameworkElement host,StringBuilder report,bool dark,string outDir,string name,bool states){
        int width=(int)host.ActualWidth;var parts=window.ToolbarHeadingParts();var captionButton=(Button)parts[0];var arrow=(Button)parts[1];var pair=(ContentControl)parts[2];var slot=parts[3];var size=(ComboBox)parts[4];window.ToolbarSizeCaption("16 px");
        for(int level=0;level<=6;level++){
            window.ToolbarHeadingLevel(level);Settle(host,width);var bitmap=Render(host,width,ShotHeight);var caption=(FrameworkElement)captionButton.Content;var field=(TextBox)size.Template.FindName("PART_EditableTextBox",size);var sizeArrow=Descendant<System.Windows.Shapes.Path>(size);
            Rect word=ElementPixels(bitmap,caption,host,dark),chevron=ElementPixels(bitmap,(FrameworkElement)arrow.Content,host,dark),sizeWord=ElementPixels(bitmap,field,host,dark),sizeChevron=ElementPixels(bitmap,sizeArrow,host,dark),hover=BoundsIn((Border)pair.Template.FindName("Surface",pair),host),slotBox=BoundsIn(slot,host);
            double gap=chevron.Left-word.Right,sizeGap=sizeChevron.Left-sizeWord.Right,left=word.Left-hover.Left,right=hover.Right-chevron.Right,centre=hover.Left+hover.Width/2-slotBox.Left-slotBox.Width/2;
            var captionSurface=(Border)captionButton.Template.FindName("Surface",captionButton);var arrowSurface=(Border)arrow.Template.FindName("Surface",arrow);var pairSurface=(Border)pair.Template.FindName("Surface",pair);
            report.AppendLine("heading "+name+" level="+level+" tag="+captionButton.Tag+" captionInk="+R(word)+" chevronInk="+R(chevron)+" size16Ink="+R(sizeWord)+" sizeChevronInk="+R(sizeChevron)+" gap="+F(gap)+" sizeGap="+F(sizeGap)+" hoverBox="+R(hover)+" innerPadding="+F(left)+","+F(right)+" slotWidth="+F(slotBox.Width)+" pairCentreOffset="+F(centre)+" fills="+BrushColor(captionSurface.Background)+","+BrushColor(arrowSurface.Background)+","+BrushColor(pairSurface.Background));
            // b50: the heading arrow is its own SplitArrowWidth segment, so its gap no longer matches the size combo's.
            // b51: the segment is now chevron ink + an equal pad on both sides, so the right margin is just that pad.
            // b52: the shared hover surface carries SplitSurfacePadding on the left, and the arrow segment narrowed to SplitArrowWidth.
            if(word.IsEmpty||chevron.IsEmpty||sizeWord.IsEmpty||sizeChevron.IsEmpty||gap<2||Math.Abs(left-Ui.SplitSurfacePadding)>1||Math.Abs(right-(Ui.SplitArrowWidth-chevron.Width)/2)>1.5||Math.Abs(centre)>.5||BrushColor(captionSurface.Background).A!=0||BrushColor(arrowSurface.Background).A!=0||BrushColor(pairSurface.Background).A!=0||BrushColor(captionButton.Foreground)!=ThemeColor(window.Resources,"Ink"))throw new InvalidOperationException("Heading split spacing/active fill mismatch: level "+level);
            if(!hover.Contains(BoundsIn(captionButton,host))||!hover.Contains(BoundsIn(arrow,host)))throw new InvalidOperationException("Heading hover box does not cover both targets");
            Describe(host,new StringBuilder(),name,null);
        }
        if(!states)return;
        window.ToolbarShotState();Settle(host,width);Save(host,width,Path.Combine(outDir,name+"-states.png"));Describe(host,report,name+"-states",dark?null:Render(host,width,ShotHeight));report.AppendLine("states: heading=제목 1 Tag=Active, bold=Active, size=32 px, source=checked, replace=Active");
        var original=pair.Template;pair.Template=ProbeTemplate(pair);
        foreach(Button target in new Button[]{captionButton,arrow}){
            pair.SetValue(ProbeHoverProperty,false);Settle(host,width);DependencyObject ancestor=target;while(ancestor!=null&&!Object.ReferenceEquals(ancestor,pair))ancestor=VisualTreeHelper.GetParent(ancestor);if(ancestor==null)throw new InvalidOperationException("Heading hit target outside shared hover surface");
            pair.SetValue(ProbeHoverProperty,true);Settle(host,width);var surface=(Border)pair.Template.FindName("Surface",pair);Color fill=BrushColor(surface.Background);report.AppendLine("heading hover "+(dark?"dark":"light")+" input="+(target==captionButton?"caption":"chevron")+" sharedBox="+R(BoundsIn(surface,host))+" fill="+fill+" (real template IsMouseOver trigger, input DP substituted; no mouse/HWND)");if(fill!=ThemeColor(window.Resources,"Hover"))throw new InvalidOperationException("Heading shared hover missing");
        }
        Save(host,width,Path.Combine(outDir,name+"-heading-hover.png"));pair.ClearValue(ProbeHoverProperty);pair.Template=original;Settle(host,width);
    }
    /* b55: the five-language check for the list dropdown, the status bar's mode word and the caret pattern, run on
       the shot host (no window, like the rest of this probe). The caption updates in place, so the language switch
       needs no render pass. The report carries the old outer width the button must keep, the slot it got, the
       advance its glyphs need, the pixels painted, the status head and the caret text; a caption wider than its
       slot, clipped ink or a moved button fails the shot. */
    static void MeasureListCaptions(MainWindow window,FrameworkElement host,StringBuilder report,bool dark,string outDir,string name){
        int width=(int)host.ActualWidth;var failures=new List<string>();string bodyFonts=window.ToolbarBodyFonts();
        foreach(string code in new[]{"ko","en","ja","zh-CN","zh-TW"}){
            try{
                window.ToolbarLanguage(code);window.ToolbarHeadingLevel(1);window.ToolbarSizeCaption("32 px");Settle(host,width);
                if(window.ToolbarBodyFonts()!=bodyFonts)throw new InvalidOperationException("Language changed the document/editor font");
                foreach(var chrome in window.ToolbarTextRoots())ValidateText(chrome,Ui.ChromeFamily(code));
                MeasureAlignment(window,host,report,code,true);
                Describe(host,report,"positions "+code,null);
                var label=window.ToolbarListCaptionLabel();
                if(label==null)throw new InvalidOperationException("List caption label missing");
                var face=new Typeface(label.FontFamily,label.FontStyle,label.FontWeight,label.FontStretch);
                var formatted=new FormattedText(label.Text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,label.FontSize,Brushes.Black,null,TextFormattingMode.Display,1.0);
                var ideal=new FormattedText(label.Text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,Ui.LabelSize,Brushes.Black,1.0);
                double advance=formatted.WidthIncludingTrailingWhitespace;Rect need=formatted.BuildGeometry(new Point()).Bounds;
                double icon=20*Ui.IconScale+Ui.RibbonIconLabelGap;double slot=Math.Ceiling(ideal.WidthIncludingTrailingWhitespace)-3;double oldOuter=icon+slot+Math.Max(0,Math.Ceiling(icon+advance)-(icon+slot))+4;/* 2.2.0: the button grows by the text's extra width, the 4 px arrow gap stays */
                Rect box=label.IsDescendantOf(host)?BoundsIn(label,host):Rect.Empty;
                Rect ink=box.IsEmpty?Rect.Empty:PixelInk(Render(host,width,ShotHeight),new Rect(box.Left,box.Top,box.Width+4,box.Height),dark);
                report.AppendLine("list caption "+code+" inTree="+label.IsDescendantOf(host)+"/"+window.ToolbarListButtonInTree(host)+" text=["+label.Text+"] caret=["+window.ToolbarCaretFormat(8,4)+"] status=["+window.ToolbarStatusHead()+"] slot="+window.ToolbarListCaptionWidth().ToString("0.##")+" need="+(icon+advance).ToString("0.##")+" advance="+advance.ToString("0.##")+" ink="+need.Left.ToString("0.##")+".."+need.Right.ToString("0.##")+" labelBox="+R(box)+" paintedInk="+R(ink)+" outer="+window.ToolbarListOuterWidth().ToString("0.##")+"/"+oldOuter.ToString("0.##")+" "+window.ToolbarListCaptionState());
                if(box.IsEmpty)failures.Add(code+": caption not realised");
                if(window.ToolbarListCaptionWidth()<icon+advance-.5)failures.Add(code+": caption slot "+window.ToolbarListCaptionWidth().ToString("0.##")+" narrower than "+(icon+advance).ToString("0.##"));
                if(!box.IsEmpty&&(ink.IsEmpty||ink.Right<box.Left+need.Right-1.5))failures.Add(code+": caption ink clipped ("+R(ink)+" vs need "+(box.Left+need.Right).ToString("0.##")+")");
                if(Math.Abs(window.ToolbarListOuterWidth()-oldOuter)>.01)failures.Add(code+": list button width moved from "+oldOuter.ToString("0.##"));
                Save(host,width,Path.Combine(outDir,name+"-list-"+code+".png"));
            }catch(Exception error){report.AppendLine("list caption "+code+" ERROR "+error.ToString());failures.Add(code+": "+error.Message);}
        }
        window.ToolbarLanguage("ko");Settle(host,width);
        if(failures.Count>0)throw new InvalidOperationException("List caption check: "+String.Join(" | ",failures.ToArray()));
    }
    /* guide images: the read-state (not editing) top bar in every language, light theme; the 수정 button box is reported so the guide crop can be cut from it */
    static void ShootEditRead(MainWindow window,FrameworkElement host,StringBuilder report,string outDir){
        int width=(int)host.ActualWidth;
        try{
            window.SetMode(false);
            foreach(string code in new[]{"ko","en","ja","zh-CN","zh-TW"}){
                window.ToolbarLanguage(code);Settle(host,width);
                var button=window.ToolbarCaptionControls()[0];Rect box=BoundsIn(button,host);
                report.AppendLine("edit-read "+code+" box="+R(box));
                Save(host,width,Path.Combine(outDir,"toolbar-1050-light-edit-read-"+code+".png"));
            }
        }finally{window.ToolbarLanguage("ko");window.SetMode(true);Settle(host,width);}
    }
    static double CentreY(Rect rect){return rect.Top+rect.Height/2;}
    static void MeasureAlignment(MainWindow window,FrameworkElement host,StringBuilder report,string code,bool assert){
        var bitmap=Render(host,(int)host.ActualWidth,ShotHeight);double max=0;string[] names={"Editing","List","To-do","Quote","Code","Divider","Source","Heading","Size"};var controls=window.ToolbarCaptionControls();
        for(int i=0;i<controls.Length;i++){
            var control=controls[i];var text=Descendant<TextBlock>(control);FrameworkElement icon=null;
            var button=control as Button;var row=button==null?null:button.Content as StackPanel;
            if(row!=null&&row.Children.Count>1){icon=row.Children[0] as FrameworkElement;text=row.Children[1] as TextBlock;}
            if(control is CheckBox)icon=(FrameworkElement)control.Template.FindName("SourceGlyph",control);
            if(i==7)icon=(FrameworkElement)((Button)window.ToolbarHeadingParts()[1]).Content;
            var combo=control as ComboBox;FrameworkElement caption=text;
            if(combo!=null){caption=(TextBox)combo.Template.FindName("PART_EditableTextBox",combo);icon=Descendant<System.Windows.Shapes.Path>(combo);}
            if(caption==null||icon==null)throw new InvalidOperationException("Caption metric missing: "+names[i]);
            Rect region=BoundsIn(caption,host);if(combo!=null){region=new Rect(region.Left,BoundsIn(combo,host).Top+2,region.Width,combo.ActualHeight-4);}
            Rect word=PixelInk(bitmap,region,false),mark=PixelInk(bitmap,BoundsIn(icon,host),false);
            if(word.IsEmpty||mark.IsEmpty)throw new InvalidOperationException("Caption metric empty: "+names[i]);
            double delta=CentreY(word)-CentreY(mark);max=Math.Max(max,Math.Abs(delta));
            report.AppendLine("alignment "+code+" "+names[i]+" text="+R(word)+" icon="+R(mark)+" diff="+F(delta)+" transform="+caption.RenderTransform+" height="+F(caption.ActualHeight)+" family="+System.Windows.Documents.TextElement.GetFontFamily(caption));
        }
        foreach(MenuItem item in window.ToolbarMenu().Items){var header=HeaderPresenter(item,item.Header);if(header==null)throw new InvalidOperationException("Menu header metric missing");Rect word=PixelInk(bitmap,BoundsIn(header,host),false);if(word.IsEmpty)throw new InvalidOperationException("Menu ink missing");double delta=CentreY(word)-CentreY(BoundsIn(item,host));max=Math.Max(max,Math.Abs(delta));report.AppendLine("alignment "+code+" menu "+item.Header+" text="+R(word)+" rowCentre="+F(CentreY(BoundsIn(item,host)))+" diff="+F(delta));}
        report.AppendLine("alignment "+code+" MAX="+F(max)+" dpi=96 documentFonts="+window.ToolbarBodyFonts());if(assert&&max>.5)throw new InvalidOperationException("Caption ink alignment: "+code+" max="+F(max));
    }
    static void MeasureSearch(MainWindow window,FrameworkElement host,int width,StringBuilder report,bool dark,string outDir,string name){
        var fields=window.ToolbarSearchFields();var placeholders=new Rect[fields.Length];var origins=new double[fields.Length];var bitmap=Render(host,width,ShotHeight);
        for(int i=0;i<fields.Length;i++){var grid=(Grid)fields[i].Parent;var hint=(TextBlock)grid.Children[1];Rect box=BoundsIn(fields[i],host);placeholders[i]=PixelInk(bitmap,new Rect(box.Left+3,box.Top+4,100,box.Height-8),dark,ThemeColor(window.Resources,"SearchMuted"));origins[i]=BoundsIn(hint,host).Left;fields[i].Text=hint.Text;fields[i].Select(0,0);}
        Settle(host,width);bitmap=Render(host,width,ShotHeight);Save(host,width,Path.Combine(outDir,name+"-typed.png"));
        for(int i=0;i<fields.Length;i++){Rect box=BoundsIn(fields[i],host),typed=PixelInk(bitmap,new Rect(box.Left+3,box.Top+4,100,box.Height-8),dark,ThemeColor(window.Resources,"SearchInk"));double caret=fields[i].TransformToAncestor(host).Transform(new Point(fields[i].GetRectFromCharacterIndex(0).Left,0)).X,delta=typed.Left-placeholders[i].Left;report.AppendLine("search "+(i==0?"find":"replace")+" sample=["+fields[i].Text+"] placeholderLeft="+F(placeholders[i].Left)+" typedLeft="+F(typed.Left)+" inkDelta="+F(delta)+" placeholderOrigin="+F(origins[i])+" typedOrigin="+F(caret));if(placeholders[i].IsEmpty||typed.IsEmpty||Math.Abs(delta)>.5||Math.Abs(caret-origins[i])>.5)throw new InvalidOperationException("Search placeholder/typed x mismatch");fields[i].Clear();}
        Settle(host,width);
    }
    static readonly DependencyProperty ProbeHoverProperty=DependencyProperty.RegisterAttached("ProbeHover",typeof(bool),typeof(ToolbarShotRunner),new PropertyMetadata(false));
    static readonly DependencyProperty ProbePressedProperty=DependencyProperty.RegisterAttached("ProbePressed",typeof(bool),typeof(ToolbarShotRunner),new PropertyMetadata(false));
    static readonly DependencyProperty ProbeFocusProperty=DependencyProperty.RegisterAttached("ProbeFocus",typeof(bool),typeof(ToolbarShotRunner),new PropertyMetadata(false));
    static readonly DependencyProperty ProbeDefaultedProperty=DependencyProperty.RegisterAttached("ProbeDefaulted",typeof(bool),typeof(ToolbarShotRunner),new PropertyMetadata(false));
    static DependencyProperty ProbeProperty(DependencyProperty property){if(property==UIElement.IsMouseOverProperty)return ProbeHoverProperty;if(property==ButtonBase.IsPressedProperty)return ProbePressedProperty;if(property==UIElement.IsKeyboardFocusedProperty)return ProbeFocusProperty;if(property==Button.IsDefaultedProperty)return ProbeDefaultedProperty;return property;}
    static Color BrushColor(Brush brush){var solid=brush as SolidColorBrush;return solid==null?Colors.Transparent:solid.Color;}
    // Clone only the input DPs in the real template, so hover/press/focus can be tested without a mouse or HWND.
    static void MeasureDialogButtons(MainWindow window,StringBuilder report,bool dark,string outDir){
        var style=(Style)window.Resources[typeof(Button)];var seed=new Button {Style=style};seed.ApplyTemplate();var template=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(System.Windows.Markup.XamlWriter.Save(seed.Template));
        foreach(TriggerBase entry in template.Triggers){var trigger=entry as Trigger;if(trigger!=null)trigger.Property=ProbeProperty(trigger.Property);var multi=entry as MultiTrigger;if(multi!=null)foreach(Condition condition in multi.Conditions)condition.Property=ProbeProperty(condition.Property);}
        var panel=new StackPanel {Margin=new Thickness(10)};var host=new Border {Child=panel,Width=520,Height=320};host.Resources.MergedDictionaries.Add(window.Resources);host.SetResourceReference(Border.BackgroundProperty,"Ribbon");Ui.ToolbarText(panel);
        string[] states={"rest","other-hover","other-pressed","default-hover","default-pressed","other-focus","default-focus","defaulted-only"};
        foreach(string state in states){
            var row=new StackPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(0,2,0,2)};panel.Children.Add(row);row.Children.Add(new TextBlock {Text=state,Width=130,VerticalAlignment=VerticalAlignment.Center,Foreground=(Brush)window.Resources["Ink"]});
            var other=new Button {Style=style,Template=template,Content="기본 보기로 열기",Width=160,Height=32};var primary=new Button {Style=style,Template=template,Content="live로 열기",Width=160,Height=32,IsDefault=state!="defaulted-only",Tag="Active"};row.Children.Add(other);row.Children.Add(primary);
            if(state=="other-hover"||state=="other-pressed")other.SetValue(ProbeHoverProperty,true);if(state=="other-pressed")other.SetValue(ProbePressedProperty,true);if(state=="default-hover"||state=="default-pressed")primary.SetValue(ProbeHoverProperty,true);if(state=="default-pressed")primary.SetValue(ProbePressedProperty,true);if(state=="other-focus")other.SetValue(ProbeFocusProperty,true);if(state=="default-focus")primary.SetValue(ProbeFocusProperty,true);if(state=="defaulted-only")primary.SetValue(ProbeDefaultedProperty,true);
            Settle(host,520,320);var a=(Border)other.Template.FindName("Surface",other);var b=(Border)primary.Template.FindName("Surface",primary);Color primaryColor=BrushColor(b.Background),otherColor=BrushColor(a.Background);string expected=state=="default-hover"?"PrimaryHover":state=="default-pressed"?"PrimaryPressed":"Accent";
            report.AppendLine("dialog "+(dark?"dark":"light")+" "+state+" primary="+primaryColor+" other="+otherColor+" text="+BrushColor(primary.Foreground)+" border="+BrushColor(b.BorderBrush));
            if(primaryColor!=ThemeColor(window.Resources,expected)||otherColor!=(state=="other-hover"?ThemeColor(window.Resources,"Hover"):state=="other-pressed"?ThemeColor(window.Resources,"Pressed"):Colors.Transparent)||BrushColor(primary.Foreground)!=Colors.White||BrushColor(b.BorderBrush)!=ThemeColor(window.Resources,"Accent"))throw new InvalidOperationException("Default button trigger mismatch: "+state);
            if(state=="other-focus"&&BrushColor(a.BorderBrush)!=ThemeColor(window.Resources,"Accent"))throw new InvalidOperationException("Keyboard focus border missing");
        }
        Save(host,520,320,Path.Combine(outDir,"dialog-buttons-"+(dark?"dark":"light")+".png"));host.Child=null;
    }
    /* b56: a 450 px window is the narrow case the user hit. Both toolbar rows must keep every command:
       either painted fully inside the window or listed in that row's "»" overflow menu (top-row menus move
       as whole items, so their submenus stay live). Five languages, because a longer caption can be the one
       that no longer fits. At 1050 px neither overflow button may appear and every command stays painted
       inside the row, so the confirmed layout there does not move. */
    static void MeasureNarrowRibbon(MainWindow window,FrameworkElement host,StringBuilder report,string outDir){
        var failures=new List<string>();
        host.Width=450;
        try{
            foreach(string code in new[]{"ko","en","ja","zh-CN","zh-TW"}){
                try{
                    window.ToolbarLanguage(code);
                    Settle(host,450,ShotHeight);
                    window.ToolbarRefreshOverflow();
                    Settle(host,450,ShotHeight);
                    MeasureOverflowRow(window,host,true,code,report,failures);
                    MeasureOverflowRow(window,host,false,code,report,failures);
                    Save(host,450,Path.Combine(outDir,"toolbar-450-"+code+".png"));
                }catch(Exception error){failures.Add(code+": "+error.GetType().Name+" "+error.Message+" @@ "+error.StackTrace);}
            }
            window.ToolbarLanguage("ko");
            /* the app's minimum window (MinWidth 360) leaves ~344 px of client area: the same rule must hold there */
            foreach(int narrow in new int[]{360,380}){
                host.Width=narrow;
                try{
                    foreach(string code in new[]{"ko","en"}){
                        try{
                            window.ToolbarLanguage(code);
                            Settle(host,narrow,ShotHeight);
                            window.ToolbarRefreshOverflow();
                            Settle(host,narrow,ShotHeight);
                            MeasureOverflowRow(window,host,true,code,report,failures);
                            MeasureOverflowRow(window,host,false,code,report,failures);
                            Save(host,narrow,Path.Combine(outDir,"toolbar-"+narrow+"-"+code+".png"));
                        }catch(Exception error){failures.Add(narrow+" "+code+": "+error.GetType().Name+" "+error.Message+" @@ "+error.StackTrace);}
                    }
                }finally{host.Width=1050;window.ToolbarLanguage("ko");}
            }
            host.Width=1050;
            foreach(string code in new[]{"ko","en"}){
                try{
                    window.ToolbarLanguage(code);
                    Settle(host,1050,ShotHeight);
                    window.ToolbarRefreshOverflow();
                    Settle(host,1050,ShotHeight);
                    if(window.ToolbarOverflowShown(true))failures.Add("1050 "+code+": ribbon overflow button shown");
                    if(window.ToolbarOverflowShown(false))failures.Add("1050 "+code+": top overflow button shown");
                    MeasureWideRow(window,host,true,code,report,failures);
                    MeasureWideRow(window,host,false,code,report,failures);
                }catch(Exception error){failures.Add("1050 "+code+": "+error.GetType().Name+" "+error.Message);}
            }
        }finally{host.Width=1050;window.ToolbarLanguage("ko");}
        if(failures.Count>0)throw new InvalidOperationException("Toolbar overflow: "+String.Join(" | ",failures.ToArray()));
        report.AppendLine("overflow 450 px: five languages, every command painted inside the window or listed in its row's » menu, no overlap, both » buttons visible PASS");
        report.AppendLine("overflow 1050 px: no overflow button, every command painted inside the row PASS");
    }
    static void MeasureOverflowRow(MainWindow window,FrameworkElement host,bool ribbon,string code,StringBuilder report,List<string> failures){
        string row=ribbon?"ribbon":"top";
        if(!window.ToolbarOverflowShown(ribbon)){failures.Add(code+": "+row+" » button not visible");return;}
        var button=ribbon?window.ToolbarRibbonOverflow():window.ToolbarTopOverflow();
        Rect overflowBox=BoundsIn(button,host);
        report.AppendLine("overflow "+code+" "+row+" » box="+R(overflowBox)+" menu=["+String.Join(" | ",window.ToolbarOverflowCaptions(ribbon))+"]");
        if(overflowBox.IsEmpty||overflowBox.Left<-.5||overflowBox.Right>host.ActualWidth+.5||overflowBox.Top<0)failures.Add(code+": "+row+" » outside the window "+R(overflowBox));
        /* b56: » follows the last painted command of its row (normal button spacing) instead of the
           row's right end, and its menu hangs from the button's right edge inside the window. */
        var lastUnit=window.ToolbarOverflowLastVisible(ribbon);
        if(lastUnit!=null){
            Rect lastBox=BoundsIn(lastUnit,host);double gap=overflowBox.Left-lastBox.Right;
            report.AppendLine("overflow "+code+" "+row+" » follows ["+ControlName(lastUnit)+"] lastRight="+F(lastBox.Right)+" »left="+F(overflowBox.Left)+" gap="+F(gap));
            if(gap<-.5||gap>12)failures.Add(code+": "+row+" » does not follow the last visible command (gap="+F(gap)+")");
        }
        var placements=window.ToolbarOverflowPlacement(ribbon,host.ActualWidth,overflowBox.Left,new Size(240,320));
        if(placements.Length>0){
            double popupLeft=overflowBox.Left+placements[0].Point.X,popupRight=popupLeft+240;
            report.AppendLine("overflow "+code+" "+row+" » popup x="+F(popupLeft)+".."+F(popupRight)+" buttonRight="+F(overflowBox.Right)+" window=0.."+F(host.ActualWidth));
            if(popupRight>host.ActualWidth+.5||popupLeft<-.5)failures.Add(code+": "+row+" » popup leaves the window "+R(new Rect(popupLeft,0,240,1)));
            /* right edge on the button, or clamped to a window edge when 240 px does not fit there */
            bool aligned=Math.Abs(popupRight-overflowBox.Right)<=.5;
            bool clamped=popupRight>=host.ActualWidth-.5||popupLeft<=.5;
            if(!aligned&&!clamped)failures.Add(code+": "+row+" » popup is not right-aligned to its button and not clamped ("+F(popupRight)+" vs "+F(overflowBox.Right)+")");
        }
        int visible=0,listed=0;
        foreach(var source in window.ToolbarOverflowSources(ribbon)){
            if(ShownInRow(source,window,ribbon,host)){
                visible++;
                Rect box=BoundsIn(source,host);
                if(box.Right>host.ActualWidth+.5||box.Left<-.5)failures.Add(code+": "+row+" command clipped "+ControlName(source)+" "+R(box));
            }else{
                if(!window.ToolbarOverflowMenuHas(ribbon,source))failures.Add(code+": "+row+" command lost "+ControlName(source));
                else listed++;
            }
        }
        report.AppendLine("overflow "+code+" "+row+" visible="+visible+" inMenu="+listed);
        report.AppendLine("layout "+code+" "+row+" "+window.ToolbarOverflowLayout(ribbon));
        var controls=new List<FrameworkElement>();
        CollectRowControls(ribbon?window.ToolbarRibbonHost():window.ToolbarTopHost(),controls);
        for(int i=0;i<controls.Count;i++){
            Rect a=BoundsIn(controls[i],host);
            if(a.Right>host.ActualWidth+.5||a.Left<-.5)failures.Add(code+": "+row+" control clipped "+ControlName(controls[i])+" "+R(a));
            for(int j=i+1;j<controls.Count;j++){
                Rect b=BoundsIn(controls[j],host);
                if(Math.Min(a.Right,b.Right)-Math.Max(a.Left,b.Left)>0.5&&Math.Min(a.Bottom,b.Bottom)-Math.Max(a.Top,b.Top)>0.5)failures.Add(code+": "+row+" "+ControlName(controls[i])+" overlaps "+ControlName(controls[j]));
            }
        }
    }
    static void MeasureWideRow(MainWindow window,FrameworkElement host,bool ribbon,string code,StringBuilder report,List<string> failures){
        string row=ribbon?"ribbon":"top";
        foreach(var source in window.ToolbarOverflowSources(ribbon)){
            if(!ShownInRow(source,window,ribbon,host)){failures.Add("1050 "+code+": "+row+" command hidden "+ControlName(source));continue;}
            Rect box=BoundsIn(source,host);
            if(box.Right>host.ActualWidth+.5||box.Left<-.5)failures.Add("1050 "+code+": "+row+" command clipped "+ControlName(source)+" "+R(box));
        }
        report.AppendLine("overflow 1050 "+code+" "+row+" all commands painted inside the row");
    }
    /* b56: the windowless shots prove the geometry; this one runs the real window path (Show, then resize) so
       the SizeChanged-driven overflow is exercised the way the app hits it, and the visible row is snapshotted. */
    static void MeasureLiveOverflowWindow(StringBuilder report,string outDir){
        string sample=Path.Combine(outDir,"live-overflow.md");File.WriteAllText(sample,"# 제목\n\n본문 한 줄\n",new UTF8Encoding(false));
        LiveOverflowScenario(report,outDir,"live-a",380,true,sample);
        LiveOverflowScenario(report,outDir,"live-b",380,false,sample);
    }
    /* A: the size is set before Show; B: Show, then resize, then load a document - the order the ui-shot
       snapshots use and the order a real user hits by dragging the window edge. */
    static void LiveOverflowScenario(StringBuilder report,string outDir,string name,int width,bool sizeBeforeShow,string sample){
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000};
        try{
            if(sizeBeforeShow){window.Width=width;window.Height=330;}
            window.Show();
            if(!sizeBeforeShow){window.Width=width;window.Height=330;}
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(delegate{}));
            if(!sizeBeforeShow){window.ToolbarLoadSample(sample);window.SetMode(true);}
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(delegate{}));
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));
            var ribbon=window.ToolbarRibbonOverflow();var top=window.ToolbarTopOverflow();
            report.AppendLine(name+" content="+F(window.Content==null?0:((FrameworkElement)window.Content).ActualWidth)+" ribbon host="+F(window.ToolbarRibbonHost().ActualWidth)+" preview="+F(window.ToolbarRibbonPreview().ActualWidth)+" scrollable="+F(window.ToolbarRibbonScroll().ScrollableWidth)+" »="+ribbon.Visibility+" top »="+top.Visibility+" mode="+(window.ToolbarEditing()?"edit":"read"));
            if(ribbon.Visibility!=Visibility.Visible||top.Visibility!=Visibility.Visible)throw new InvalidOperationException(name+": live window at "+width+" px lost an overflow button");
            Program.Snapshot(window,Path.Combine(outDir,name+".png"));
            window.Width=1050;window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(delegate{}));
            window.UpdateLayout();
            report.AppendLine(name+" 1050 ribbon »="+ribbon.Visibility+" scrollable="+F(window.ToolbarRibbonScroll().ScrollableWidth)+" top »="+top.Visibility);
            if(ribbon.Visibility==Visibility.Visible||top.Visibility==Visibility.Visible)throw new InvalidOperationException(name+": live window at 1050 px shows an overflow button");
            report.AppendLine(name+" live window 380/1050 px overflow PASS");
        }finally{window.Close();}
    }
    /* --toolbar-shot: the outline panel and the 파일 tab on a real (offscreen) 1050x700 window.
       The fixture folder is the panel's own data on disk: 목차 reads the headings of 회의 기록.md below,
       and the 파일 tab's root is that file's folder, so the two PNGs exercise the product paths the
       F10 toggle and the header's two tabs use. */
    static void MeasureOutlinePanelShots(StringBuilder report,string outDir){
        string fixture=Path.Combine(outDir,"panel-fixture","노트");
        Directory.CreateDirectory(Path.Combine(fixture,"자료"));
        string note=Path.Combine(fixture,"회의 기록.md");
        File.WriteAllText(note,"# 회의 기록\n\n## 결정한 것\n\n- 다음 주 배포\n\n### 아주 긴 제목이 패널 폭보다 길어서 가로 스크롤이 생기는지 보는 제목\n\n본문 한 줄\n\n## 남은 일\n\n- 검토하기\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture,"지난 회의.md"),"# 지난 회의\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture,"메모.txt"),"메모\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture,"자료","할 일.md"),"# 할 일\n",new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(fixture,"사진.png"),"",new UTF8Encoding(false));
        foreach(bool dark in new bool[]{false,true}){
            string name=dark?"dark":"light";
            var window=PanelShotWindow(dark);
            try{
                window.PanelShotOpen(note);
                PumpWindow(window);
                window.PanelShotOutline(true);
                PumpWindow(window);
                int items=window.PanelShotOutlineCount();
                report.AppendLine("panel outline "+name+" "+window.PanelShotPanelState()+" items="+items+" titles="+window.PanelShotOutlineTitles());
                if(items==0)throw new InvalidOperationException("Panel outline shot: no headings in the tree");
                Program.Snapshot(window,Path.Combine(outDir,"panel-outline-"+name+".png"));
                window.PanelShotFilesTab();
                bool folderFound=WaitFor(window,delegate{return window.PanelShotFolderItem("자료")!=null;},3000);
                string[] rootNames=window.PanelShotFolderNames();
                report.AppendLine("panel files "+name+" "+window.PanelShotPanelState()+" root=["+String.Join(" | ",rootNames)+"] 자료="+folderFound);
                if(!folderFound)throw new InvalidOperationException("Panel files shot: 자료 did not appear within 3 s");
                if(Array.IndexOf(rootNames,"사진.png")>=0)throw new InvalidOperationException("Panel files shot: the ignored png is listed");
                var folder=window.PanelShotFolderItem("자료");
                window.PanelShotExpand(folder);
                bool children=WaitFor(window,delegate{return window.PanelShotFolderChildren(folder);},3000);
                PumpWindow(window);
                report.AppendLine("panel files "+name+" 자료 children="+children);
                if(!children)throw new InvalidOperationException("Panel files shot: 자료 children did not load within 3 s");
                Program.Snapshot(window,Path.Combine(outDir,"panel-files-"+name+".png"));
            }finally{window.Close();}
        }
    }
    /* One offscreen 1050x700 window in the requested theme and Korean captions, laid out before the
       first shot (Show is what realises the panel and the tree the shots capture). Closing it needs
       no prompt: the probe only opens a file and reads the panel, so nothing is unsaved. */
    static MainWindow PanelShotWindow(bool dark){
        return PanelShotWindow(dark,700);
    }
    static MainWindow PanelShotWindow(bool dark,int height){
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000,Width=1050,Height=height};
        window.Show();
        window.ToolbarLanguage("ko");
        window.PanelShotTheme(dark);
        PumpWindow(window);
        return window;
    }
    static void PumpWindow(MainWindow window){
        window.Dispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));
        window.Dispatcher.Invoke(DispatcherPriority.Loaded,new Action(delegate{}));
        window.Dispatcher.Invoke(DispatcherPriority.ContextIdle,new Action(delegate{}));
        window.UpdateLayout();
    }
    /* The folder read lands on the dispatcher from the worker pool, so the wait pumps the dispatcher
       and lets those posted passes run between steps; the caller's 3 s cap bounds the wait. */
    static bool WaitFor(MainWindow window,Func<bool> condition,int timeoutMs){
        DateTime deadline=DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while(true){
            window.Dispatcher.Invoke(DispatcherPriority.Background,new Action(delegate{}));
            window.UpdateLayout();
            if(condition())return true;
            if(DateTime.UtcNow>=deadline)return false;
            System.Threading.Thread.Sleep(25);
        }
    }
    /* --toolbar-shot format sample: every 2.4 inline/form addition in one document (highlight, sup/sub,
       kbd, u, mark, emoji, table, details, fence, front matter). A real 1050x800 light window in Korean
       opens it the way the panel shots do; edit mode puts the caret at the document end (the last row,
       which live draws as source) and the same caret is shot in Live (SetLiveMode(true)) and Basic
       (SetLiveMode(false)). The caret then goes to line 1 and back to the end so the last two PNGs
       catch a stale first-line cache. Offscreen, no hover (the probe has no mouse), and closing is
       quiet because only the caret moved. */
    static void MeasureFormatSampleShots(StringBuilder report,string outDir){
        string folder=Path.Combine(outDir,"format-fixture");
        Directory.CreateDirectory(folder);
        string samplePath=Path.Combine(folder,"sample.md");
        string sample=@"---
title: 2.4 서식 확인
tags: [mdnote, test]
---
<p align=""center""><b>MDNote 2.4</b> 서식 견본</p>

# 새 서식

이건 ==형광펜==이고, x<sup>2</sup>와 H<sub>2</sub>O, <kbd>Ctrl</kbd>+<kbd>S</kbd>, <u>밑줄</u>, <mark>표시</mark>입니다.

:tada: 이모지 :+1: 와 <kbd>A</kbd> <kbd>B</kbd> <kbd>C</kbd>

| 키 | 설명 |
| --- | --- |
| <kbd>S</kbd> | 저장 |

<details>
<summary>펼친 상자 제목</summary>
상자 안 줄
</details>

```js
function hello() { return 1; }
```

끝 줄
";
        /* the source file is LF, but keep the fixture LF even if this file is ever saved on a CRLF editor */
        File.WriteAllText(samplePath,sample.Replace("\r\n","\n"),new UTF8Encoding(false));
        var window=PanelShotWindow(false,800);
        try{
            window.PanelShotOpen(samplePath);
            PumpWindow(window);
            window.FormatShotEdit();
            int length=window.FormatShotLength();
            if(length<=0)throw new InvalidOperationException("Format shot: the sample did not load");
            window.FormatShotCaret(length);
            window.FormatShotLive(true);PumpWindow(window);
            Program.Snapshot(window,Path.Combine(outDir,"format-live.png"));
            window.FormatShotLive(false);PumpWindow(window);
            Program.Snapshot(window,Path.Combine(outDir,"format-basic.png"));
            window.FormatShotLive(true);window.FormatShotCaret(0);PumpWindow(window);
            Program.Snapshot(window,Path.Combine(outDir,"format-live-caret1.png"));
            window.FormatShotCaret(window.FormatShotLength());PumpWindow(window);
            Program.Snapshot(window,Path.Combine(outDir,"format-live-back.png"));
            /* caret away from the front matter with line 1 still on screen: both --- rows must be hidden */
            window.FormatShotCaret(0);PumpWindow(window);window.FormatShotCaret(sample.IndexOf("# 새 서식",StringComparison.Ordinal));PumpWindow(window);
            Program.Snapshot(window,Path.Combine(outDir,"format-live-mid.png"));
            report.AppendLine("format shots: sample.md "+sample.Length+" chars, carets end/end/1/end/mid, view scrolled to each caret, 1050x800 light ko, live/basic live/basic live/back PASS (no hover)");
        }finally{window.FormatShotClose();}
    }
    static bool ShownInRow(FrameworkElement source,MainWindow window,bool ribbon,FrameworkElement host){
        var item=source as MenuItem;
        if(item!=null)return window.ToolbarMenu().Items.Contains(item)&&source.IsDescendantOf(host)&&source.Visibility==Visibility.Visible;
        return source.Visibility==Visibility.Visible&&source.IsDescendantOf(host)&&source.ActualWidth>0;
    }
    static string ControlName(FrameworkElement control){var label=System.Windows.Automation.AutomationProperties.GetName(control);if(!String.IsNullOrEmpty(label))return label;var tip=Convert.ToString(control.ToolTip);if(!String.IsNullOrEmpty(tip))return tip;var text=control as TextBox;if(text!=null)return "textbox ["+text.Text+"]";return control.GetType().Name+"#"+control.GetHashCode().ToString("x");}
    static void CollectRowControls(DependencyObject node,List<FrameworkElement> controls){
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){
            var child=VisualTreeHelper.GetChild(node,i) as FrameworkElement;if(child==null)continue;
            if(child is ScrollBar||child is ContextMenu)continue;
            if(child.Visibility!=Visibility.Visible||child.ActualWidth<=0||child.ActualHeight<=0)continue;
            var menuItem=child as MenuItem;
            if(menuItem!=null){if(menuItem.Parent is Menu)controls.Add(menuItem);continue;}
            if(child is ComboBox){controls.Add(child);continue;}
            if(child is ButtonBase){controls.Add(child);continue;}
            CollectRowControls(child,controls);
        }
    }
    static string F(double value){return value.ToString("0.0",CultureInfo.InvariantCulture);}
    static bool Inside<T>(DependencyObject element,DependencyObject stop) where T:DependencyObject{
        var parent=VisualTreeHelper.GetParent(element);
        while(parent!=null&&parent!=stop){if(parent is T)return true;parent=VisualTreeHelper.GetParent(parent);}
        return false;
    }

    static ContentPresenter FirstPresenter(DependencyObject element){
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++){
            var child=VisualTreeHelper.GetChild(element,i);var presenter=child as ContentPresenter;
            if(presenter!=null)return presenter;
            var nested=FirstPresenter(child);if(nested!=null)return nested;
        }
        return null;
    }

    static ContentPresenter HeaderPresenter(DependencyObject element,object header){
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++){
            var child=VisualTreeHelper.GetChild(element,i);var presenter=child as ContentPresenter;
            if(presenter!=null&&presenter.ActualWidth>0&&Object.Equals(presenter.Content,header))return presenter;
            var nested=HeaderPresenter(child,header);if(nested!=null)return nested;
        }
        return null;
    }

    static Rect BoundsIn(FrameworkElement element,Visual host){
        return element.TransformToAncestor(host).TransformBounds(new Rect(0,0,element.ActualWidth,element.ActualHeight));
    }

    static Rect InkIn(Visual element,Visual host){
        var caption=element as TextBlock;if(caption==null)caption=Descendant<TextBlock>(element);if(caption!=null&&Object.Equals(caption.Tag,"HeadingText")){/* TextBlock descendant bounds include advance whitespace; the cropped caption is bounded by its real glyphs. */var formatted=new FormattedText(System.Windows.Automation.AutomationProperties.GetName(caption),CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(caption.FontFamily,caption.FontStyle,caption.FontWeight,caption.FontStretch),caption.FontSize,Brushes.Black,null,TextFormattingMode.Display,1.0);int at=0;foreach(var inline in caption.Inlines){var run=inline as System.Windows.Documents.Run;if(run==null)continue;formatted.SetFontWeight(run.FontWeight,at,run.Text.Length);at+=run.Text.Length;}return caption.TransformToAncestor(host).TransformBounds(formatted.BuildGeometry(new Point()).Bounds);}
        Rect local=VisualTreeHelper.GetDescendantBounds(element);
        var own=VisualTreeHelper.GetContentBounds(element);if(!own.IsEmpty)local.Union(own);
        if(local.IsEmpty)return Rect.Empty;
        return element.TransformToAncestor(host).TransformBounds(local);
    }

    static bool Visible(FrameworkElement element){return element.Visibility==Visibility.Visible&&element.ActualWidth>0&&element.ActualHeight>0;}

    static void Collect(DependencyObject node,FrameworkElement host,List<Item> items){
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++){
            var child=VisualTreeHelper.GetChild(node,i) as FrameworkElement;if(child==null)continue;
            if(child.Visibility!=Visibility.Visible)continue;
            var menuItem=child as MenuItem;
            if(menuItem!=null&&menuItem.Parent is Menu&&Visible(menuItem)){
                var header=HeaderPresenter(menuItem,menuItem.Header);
                items.Add(new Item {Kind="menu",Label=Convert.ToString(menuItem.Header),Box=BoundsIn(menuItem,host),Ink=header==null?Rect.Empty:InkIn(header,host)});
                continue;
            }
            var combo=child as ComboBox;
            if(combo!=null&&Visible(combo)){items.Add(new Item {Kind="combo",Label=combo.Text,Box=BoundsIn(combo,host),Ink=Ui.ComboInk(combo,host),Bordered=false});continue;}
            var textBox=child as TextBox;
            if(textBox!=null&&Visible(textBox)&&!Inside<ComboBox>(textBox,host)){items.Add(new Item {Kind="textbox",Label="",Box=BoundsIn(textBox,host),Ink=BoundsIn(textBox,host),Bordered=true});continue;}
            var button=child as ButtonBase;
            if(button!=null&&Visible(button)&&!Inside<ScrollBar>(button,host)&&!Inside<ComboBox>(button,host)){
                var presenter=FirstPresenter(button);
                var control=(Control)button;var brush=control.BorderBrush as SolidColorBrush;
                bool bordered=control.BorderThickness.Left>0&&brush!=null&&brush.Color.A>0;
                string label=Convert.ToString(button.ToolTip);if(String.IsNullOrEmpty(label)&&button.Content is string)label=(string)button.Content;
                var surface=control.Template==null?null:control.Template.FindName("Surface",control) as Border;string chrome="";if(surface!=null){Rect bounds=BoundsIn(surface,host);chrome=" surface="+F(bounds.Width)+"x"+F(bounds.Height)+" y="+F(bounds.Top)+".."+F(bounds.Bottom)+" border="+surface.BorderThickness+" radius="+surface.CornerRadius;}
                items.Add(new Item {Kind=button is CheckBox?"check":button is ToggleButton?"toggle":"button",Label=label,Box=BoundsIn(button,host),Ink=presenter==null?Rect.Empty:InkIn(presenter,host),Bordered=bordered,Chrome=chrome});
                continue;
            }
            var border=child as Border;
            if(border!=null&&Visible(border)&&border.ActualWidth<=1.5&&border.ActualHeight>=8&&border.ActualHeight<=40&&border.Background!=null){
                var box=BoundsIn(border,host);items.Add(new Item {Kind="divider",Label="| m="+F(border.Margin.Left)+","+F(border.Margin.Right)+" parent="+VisualTreeHelper.GetParent(border).GetType().Name,Box=box,Ink=box});continue;
            }
            if(border!=null&&Visible(border)&&border.BorderThickness.Right>=1&&border.BorderThickness.Left==0&&border.Child is StackPanel){
                var box=BoundsIn(border,host);items.Add(new Item {Kind="divider",Label="group|",Box=new Rect(box.Right-1,box.Top,1,box.Height),Ink=new Rect(box.Right-1,box.Top,1,box.Height)});
            }
            var block=child as TextBlock;
            if(block!=null&&Visible(block)&&!Inside<ButtonBase>(block,host)&&!Inside<MenuItem>(block,host)&&!Inside<ComboBox>(block,host)&&!Inside<TextBox>(block,host)&&!String.IsNullOrWhiteSpace(block.Text)){
                items.Add(new Item {Kind="label",Label=block.Text,Box=BoundsIn(block,host),Ink=InkIn(block,host)});continue;
            }
            Collect(child,host,items);
        }
    }

    static void Describe(FrameworkElement host,StringBuilder report,string name,BitmapSource bitmap){
        var items=new List<Item>();Collect(host,host,items);
        items.Sort(delegate(Item a,Item b){int row=Math.Round(a.Box.Top+a.Box.Height/2).CompareTo(Math.Round(b.Box.Top+b.Box.Height/2));if(Math.Abs((a.Box.Top+a.Box.Height/2)-(b.Box.Top+b.Box.Height/2))>8)return row;return a.Box.Left.CompareTo(b.Box.Left);});
        report.AppendLine("## "+name);
        Item previous=null;double rowY=-100;
        foreach(var item in items){
            if(item.Box.Top<215&&(item.Box.Left<-.5||item.Box.Right>host.ActualWidth+.5))throw new InvalidOperationException("Toolbar control clipped: "+item.Label);
            if(item.Kind=="button"&&!item.Ink.IsEmpty&&(item.Ink.Left<item.Box.Left-.5||item.Ink.Right>item.Box.Right+.5||item.Ink.Top<item.Box.Top-.5||item.Ink.Bottom>item.Box.Bottom+.5))throw new InvalidOperationException("Toolbar ink clipped: "+item.Label);
            double centre=item.Box.Top+item.Box.Height/2;
            if(Math.Abs(centre-rowY)>8){report.AppendLine("-- row y="+F(centre));rowY=centre;previous=null;}
            Rect visual=item.Bordered||item.Ink.IsEmpty?item.Box:item.Ink;
            string gap="";
            if(previous!=null){Rect before=previous.Bordered||previous.Ink.IsEmpty?previous.Box:previous.Ink;gap=" gap="+F(visual.Left-before.Right);}
            report.AppendLine(item.Kind+" ["+item.Label+"] box="+F(item.Box.Left)+".."+F(item.Box.Right)+" y="+F(item.Box.Top)+".."+F(item.Box.Bottom)+" visual="+F(visual.Left)+".."+F(visual.Right)+(item.Bordered?" bordered":"")+(item.Ink.IsEmpty?"":" ink="+F(item.Ink.Width)+"x"+F(item.Ink.Height)+"@"+F(item.Ink.Left)+".."+F(item.Ink.Right))+gap+item.Chrome);
            if(bitmap!=null&&!item.Ink.IsEmpty){if(item.Label=="마지막 선택 제목 적용")report.AppendLine("   pixel "+InkRows(bitmap,new Rect(item.Ink.Left,item.Box.Top+2,item.Ink.Width,item.Box.Height-4)));else if(item.Label=="체크리스트 삽입"||item.Label=="인용문 켜기/끄기")report.AppendLine("   pixel label "+InkRows(bitmap,new Rect(item.Ink.Left+21,item.Box.Top+2,item.Ink.Width-21,item.Box.Height-4)));else if(item.Label=="마지막 선택 목록 적용")report.AppendLine("   pixel label "+InkRows(bitmap,new Rect(item.Ink.Left+12,item.Box.Top+2,item.Ink.Width-12,item.Box.Height-4)));}
            previous=item;
        }
        report.AppendLine();
    }
}
}
