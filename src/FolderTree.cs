using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace MDNote {
/* 파일 tab (2026-11-09, user): a lazy folder tree beside the outline. The helpers in FolderTree own
   the directory read, the natural sort, the icons, the folder picker and the localized strings; the
   MainWindow methods here only turn that data into TreeViewItems and route clicks to the existing
   open path (LoadFile, which switches to a tab that already holds the file).

   Cost rule: nothing here touches the file system and no TreeView exists until the 파일 tab is first
   shown (EnsureFolderTabContent is called from ShowOutlineTab). There is no FileSystemWatcher: a
   folder is read only when it is expanded, once, and the refresh button re-reads what is expanded. */
public partial class MainWindow {
    Border folderPanel;
    TreeView folderTree;
    TextBlock folderName;
    string folderRootPath="",folderFollowPath,folderMenuPath;
    bool folderManual;
    /* Every re-root bumps this; a background read from an older root is dropped when it lands. */
    int folderGeneration;
    // Folder paths the refresh must reopen once their rows are rebuilt.
    HashSet<string> folderExpandOnLoad;
    FontFamily folderItemFont;
    // The language the tree's user text (folder/file names) was last painted in.
    string folderLanguage;

    void EnsureFolderTabContent(){
        if(folderPanel!=null)return;
        var panel=new DockPanel {LastChildFill=true};
        var nameRow=new Grid {Height=28,Margin=new Thickness(6,4,6,2)};
        nameRow.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        nameRow.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
        nameRow.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        var folderGlyph=FolderTree.Icon(FolderTreeIcon.Folder);folderGlyph.Margin=new Thickness(0,0,5,0);folderGlyph.VerticalAlignment=VerticalAlignment.Center;nameRow.Children.Add(folderGlyph);
        folderName=new TextBlock {FontSize=FolderTree.RowFontSize(fontSize),TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,4,0)};
        folderName.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Grid.SetColumn(folderName,1);nameRow.Children.Add(folderName);
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(buttons,2);nameRow.Children.Add(buttons);
        buttons.Children.Add(FolderIconButton(FolderTreeIcon.Refresh,"새로 고침",delegate{RefreshFolderTab();}));
        buttons.Children.Add(FolderIconButton(FolderTreeIcon.Folder,"다른 폴더 열기",delegate{RunAfterCompositionOrNow(OpenFolderPicker);}));
        DockPanel.SetDock(nameRow,Dock.Top);panel.Children.Add(nameRow);
        folderTree=new TreeView {BorderThickness=new Thickness(0),Padding=new Thickness(5,2,5,7)};
        folderTree.SetResourceReference(Control.BackgroundProperty,"Ribbon");folderTree.SetResourceReference(Control.ForegroundProperty,"Ink");
        ScrollViewer.SetHorizontalScrollBarVisibility(folderTree,ScrollBarVisibility.Disabled);
        folderTree.PreviewMouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs args){FolderTreeClicked(args);};
        folderTree.PreviewKeyDown+=delegate(object sender,KeyEventArgs args){FolderTreeKey(args);};
        folderTree.ContextMenuOpening+=delegate(object sender,ContextMenuEventArgs args){FolderTreeMenuOpening(args);};
        var menu=new ContextMenu();
        var reveal=new MenuItem {Header=T("탐색기에서 보기")};reveal.Click+=delegate{FolderTreeReveal(folderMenuPath);};menu.Items.Add(reveal);
        var copy=new MenuItem {Header=T("경로 복사")};copy.Click+=delegate{FolderTreeCopyPath(folderMenuPath);};menu.Items.Add(copy);
        /* The menu is not in the window's logical tree, so a later language switch is picked up here
           instead of by Localization's tree pass. */
        menu.Opened+=delegate{reveal.Header=T("탐색기에서 보기");copy.Header=T("경로 복사");};
        folderTree.ContextMenu=menu;
        panel.Children.Add(folderTree);
        folderPanel=new Border {Child=panel,Visibility=Visibility.Collapsed};
        folderPanel.SetResourceReference(Border.BackgroundProperty,"Ribbon");
        outlineBody.Children.Add(folderPanel);
        folderLanguage=language;
    }
    Button FolderIconButton(FolderTreeIcon kind,string tip,Action action){
        var button=new Button {Content=FolderTree.Icon(kind),Width=22,Height=22,MinWidth=22,MinHeight=22,Padding=new Thickness(0),Margin=new Thickness(2,0,0,0),ToolTip=T(tip)};
        button.Template=FolderTree.IconButtonTemplate();button.Click+=delegate{action();};
        System.Windows.Automation.AutomationProperties.SetName(button,T(tip));
        return button;
    }
    /* The tree follows the current file's folder unless the reader picked a folder by hand; a manual
       root survives tab switches and file opens until the next folder pick. */
    void FolderTabPathChanged(){
        FolderTabLanguageSync();
        if(folderTree==null||!outlinePanelOpen||!outlineFilesTab)return;
        string current=path;
        if(Object.Equals(current,folderFollowPath))return;
        folderFollowPath=current;
        if(folderManual){SelectCurrentFolderFile();return;}
        /* An unsaved tab has no file of its own: the most recent file wins (the recent list, or -
           when 최근 파일 사용 안 함 is on and that list stays empty - any file already open). */
        string reference=current;
        if(reference.Length==0)foreach(var state in documents){string candidate=state==activeDocument?path:state.Path;if(!String.IsNullOrEmpty(candidate)){reference=candidate;break;}}
        string root=FolderTree.FolderFor(reference,recentFiles);
        if(String.IsNullOrEmpty(root))return;
        if(String.Equals(root,folderRootPath,StringComparison.OrdinalIgnoreCase)){SelectCurrentFolderFile();return;}
        SetFolderRoot(root,false);
    }
    /* Localization's tree pass treats every TextBlock as UI text; folder and file names are user
       data (a folder literally named "Files" must not read "파일" in another language). After a
       language switch the labels are painted back from their entries; UpdateTitle runs this once
       per switch through FolderTabPathChanged. */
    void FolderTabLanguageSync(){
        if(folderTree==null||Object.Equals(folderLanguage,language))return;
        folderLanguage=language;
        ResetFolderLabels(folderTree.Items);
        if(folderName!=null&&!String.IsNullOrEmpty(folderRootPath))folderName.Text=FolderTree.DisplayName(folderRootPath);
    }
    static void ResetFolderLabels(ItemCollection items){
        foreach(object child in items){
            var item=child as TreeViewItem;if(item==null)continue;
            var entry=item.Tag as FolderTreeEntry;
            if(entry!=null){
                var row=item.Header as Grid;
                if(row!=null&&row.Children.Count>1){var label=row.Children[1] as TextBlock;if(label!=null)label.Text=entry.Name;}
            }
            ResetFolderLabels(item.Items);
        }
    }
    void SetFolderRoot(string folder,bool keepExpanded){
        if(folderTree==null||String.IsNullOrEmpty(folder))return;
        string full;
        try{full=System.IO.Path.GetFullPath(folder);}catch(ArgumentException){return;}catch(NotSupportedException){return;}catch(System.IO.PathTooLongException){return;}catch(System.Security.SecurityException){return;}
        if(!Directory.Exists(full))return;
        folderGeneration++;
        folderRootPath=full;
        if(folderName!=null){folderName.Text=FolderTree.DisplayName(full);folderName.ToolTip=full;folderName.FontSize=FolderTree.RowFontSize(fontSize);}
        folderExpandOnLoad=keepExpanded?FolderTree.ExpandedPaths(folderTree.Items):null;
        int generation=folderGeneration;
        folderTree.Items.Clear();folderTree.Items.Add(FolderPlaceholder());
        Task.Run<FolderTreeReadResult>(delegate{return FolderTree.ReadFolder(full);}).ContinueWith(delegate(Task<FolderTreeReadResult> task){
            FolderTreeReadResult result;
            try{result=task.Result;}catch(Exception){result=new FolderTreeReadResult();}
            try{Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{ApplyFolderRoot(generation,result);}));}catch(InvalidOperationException){}
        });
    }
    void ApplyFolderRoot(int generation,FolderTreeReadResult result){
        if(generation!=folderGeneration||folderTree==null)return;
        folderTree.Items.Clear();
        FolderTreeFill(folderTree.Items,result);
        ApplyFolderExpansion(folderTree.Items);
        SelectCurrentFolderFile();
    }
    /* After a refresh the folders that were open are opened again as their rows come back; each
       re-expansion loads that folder itself, so a deep open path rebuilds level by level. */
    void ApplyFolderExpansion(ItemCollection items){
        if(folderExpandOnLoad==null||folderExpandOnLoad.Count==0)return;
        foreach(object child in items){
            var node=child as TreeViewItem;if(node==null)continue;
            var folder=node.Tag as FolderTreeEntry;
            if(folder!=null&&folder.IsFolder&&folderExpandOnLoad.Contains(folder.FullPath))node.IsExpanded=true;
        }
    }
    void FolderTreeFill(ItemCollection target,FolderTreeReadResult result){
        foreach(FolderTreeEntry entry in result.Entries)target.Add(FolderItem(entry));
        /* The cap is per folder: one Muted last row says the listing was cut, never a truncated
           tree the reader mistakes for the whole folder. */
        if(result.Capped)target.Add(FolderMoreItem());
    }
    TreeViewItem FolderItem(FolderTreeEntry entry){
        /* A star column gives the label the row's spare width, so TextTrimming really ellipsizes a
           long name instead of letting the horizontal StackPanel overflow at its measured width. */
        var row=new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition {Width=GridLength.Auto});
        row.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(1,GridUnitType.Star)});
        FolderTreeIcon kind=entry.IsFolder?FolderTreeIcon.Folder:(FolderTree.IsMarkdown(entry.Name)?FolderTreeIcon.Markdown:FolderTreeIcon.Text);
        var icon=FolderTree.Icon(kind);icon.Margin=new Thickness(0,0,5,0);icon.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(icon);
        var label=new TextBlock {Text=entry.Name,FontFamily=FolderItemFont(),FontSize=FolderTree.RowFontSize(fontSize),VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
        label.SetResourceReference(TextBlock.ForegroundProperty,"Ink");Grid.SetColumn(label,1);row.Children.Add(label);
        var item=new TreeViewItem {Header=row,ToolTip=entry.FullPath,Style=OutlineRowStyle(),Padding=new Thickness(0)};
        item.Tag=entry;
        if(entry.IsFolder){
            item.Items.Add(FolderPlaceholder());
            item.Expanded+=delegate{LoadFolderChildren(item,entry);};
        }
        return item;
    }
    FontFamily FolderItemFont(){if(folderItemFont==null||folderItemFont.Source!=family)folderItemFont=new FontFamily(family);return folderItemFont;}
    TreeViewItem FolderPlaceholder(){
        var text=new TextBlock {Text="…",FontSize=12,VerticalAlignment=VerticalAlignment.Center};text.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        return new TreeViewItem {Header=text,Style=OutlineRowStyle(),Padding=new Thickness(0),Focusable=false,IsHitTestVisible=false};
    }
    TreeViewItem FolderMoreItem(){
        var text=new TextBlock {Text=T("더 있음"),FontSize=12,VerticalAlignment=VerticalAlignment.Center};text.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
        return new TreeViewItem {Header=text,Style=OutlineRowStyle(),Padding=new Thickness(0),Focusable=false,IsHitTestVisible=false};
    }
    /* One directory read per expanded folder, on the worker pool; the rows land on the UI thread. */
    void LoadFolderChildren(TreeViewItem item,FolderTreeEntry entry){
        if(item==null||entry==null||!entry.IsFolder||entry.Loading||entry.Loaded)return;
        entry.Loading=true;
        int generation=folderGeneration;
        string folder=entry.FullPath;
        Task.Run<FolderTreeReadResult>(delegate{return FolderTree.ReadFolder(folder);}).ContinueWith(delegate(Task<FolderTreeReadResult> task){
            FolderTreeReadResult result;
            try{result=task.Result;}catch(Exception){result=new FolderTreeReadResult();}
            try{Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{ApplyFolderChildren(item,entry,generation,result);}));}catch(InvalidOperationException){}
        });
    }
    void ApplyFolderChildren(TreeViewItem item,FolderTreeEntry entry,int generation,FolderTreeReadResult result){
        if(generation!=folderGeneration||item==null)return;
        entry.Loading=false;entry.Loaded=true;
        item.Items.Clear();FolderTreeFill(item.Items,result);
        ApplyFolderExpansion(item.Items);
        SelectCurrentFolderFile();
    }
    /* The refresh button re-reads the folders the reader has open (their paths are collected before
       the rebuild and reopened as their rows come back). */
    void RefreshFolderTab(){
        if(folderTree==null||String.IsNullOrEmpty(folderRootPath))return;
        SetFolderRoot(folderRootPath,true);
    }
    void OpenFolderPicker(){
        string picked=FolderPicker.Pick(this,folderRootPath,T("폴더 열기"));
        if(String.IsNullOrEmpty(picked))return;
        string full;
        try{full=System.IO.Path.GetFullPath(picked);}catch(ArgumentException){return;}catch(NotSupportedException){return;}catch(System.IO.PathTooLongException){return;}catch(System.Security.SecurityException){return;}
        if(!Directory.Exists(full))return;
        folderManual=true;SetFolderRoot(full,false);
    }
    /* A click on a file opens it exactly like File > Open (LoadFile switches to a tab that already
       holds it); a double click on a folder toggles it like its chevron. */
    void FolderTreeClicked(MouseButtonEventArgs args){
        if(args.ChangedButton!=System.Windows.Input.MouseButton.Left)return;
        var item=FolderTreeRowUnder(args.OriginalSource as DependencyObject);
        if(item==null||folderTree==null)return;
        var entry=item.Tag as FolderTreeEntry;
        if(entry==null)return;
        if(entry.IsFolder){
            if(args.ClickCount>1&&!FolderTreeOnExpander(args.OriginalSource as DependencyObject,item)){item.IsExpanded=!item.IsExpanded;args.Handled=true;}
            return;
        }
        if(args.ClickCount>1){args.Handled=true;return;}
        args.Handled=true;item.IsSelected=true;LoadFile(entry.FullPath);
    }
    void FolderTreeKey(KeyEventArgs args){
        if(args.Key!=Key.Enter||folderTree==null)return;
        var item=folderTree.SelectedItem as TreeViewItem;
        if(item==null)return;
        var entry=item.Tag as FolderTreeEntry;
        if(entry==null)return;
        args.Handled=true;
        if(entry.IsFolder)item.IsExpanded=!item.IsExpanded;
        else LoadFile(entry.FullPath);
    }
    /* One menu for the whole tree: Explorer's /select and the path; nothing that can rename or
       delete a file lives in this panel. */
    void FolderTreeMenuOpening(ContextMenuEventArgs args){
        var item=FolderTreeRowUnder(args.OriginalSource as DependencyObject);
        if(item==null&&folderTree!=null)item=folderTree.SelectedItem as TreeViewItem;
        var entry=item==null?null:item.Tag as FolderTreeEntry;
        folderMenuPath=entry==null?null:entry.FullPath;
        if(folderMenuPath==null)args.Handled=true;
    }
    void FolderTreeReveal(string target){
        if(String.IsNullOrEmpty(target))return;
        try{System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe","/select,\""+target+"\""){UseShellExecute=true});}catch(Exception){}
    }
    void FolderTreeCopyPath(string target){
        if(String.IsNullOrEmpty(target))return;
        try{System.Windows.Clipboard.SetText(target);}catch(Exception){}
    }
    /* The current file wears the tree's selected look only along an already expanded branch (the
       brief: highlight it when it is visible), so opening the tab never scans the disk to find it. */
    void SelectCurrentFolderFile(){
        if(folderTree==null)return;
        string current=path;
        if(String.IsNullOrEmpty(current))return;
        var item=FindVisibleFolderItem(folderTree.Items,current);
        if(item!=null)item.IsSelected=true;
    }
    static TreeViewItem FindVisibleFolderItem(ItemCollection items,string full){
        foreach(object entry in items){
            var item=entry as TreeViewItem;if(item==null)continue;
            var folder=item.Tag as FolderTreeEntry;if(folder==null)continue;
            if(!folder.IsFolder&&String.Equals(folder.FullPath,full,StringComparison.OrdinalIgnoreCase))return item;
            if(folder.IsFolder&&item.IsExpanded){var found=FindVisibleFolderItem(item.Items,full);if(found!=null)return found;}
        }
        return null;
    }
    /* The row under the pointer, and whether the press landed on a fold chevron (the small ToggleButton
       of the shared row template). Local copies so this tab does not lean on SourceOutline's helpers. */
    static TreeViewItem FolderTreeRowUnder(DependencyObject element){
        while(element!=null){
            var item=element as TreeViewItem;
            if(item!=null)return item;
            if(element is Visual){element=VisualTreeHelper.GetParent(element);continue;}
            element=LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
    static bool FolderTreeOnExpander(DependencyObject element,TreeViewItem item){
        while(element!=null&&element!=item){
            if(element is System.Windows.Controls.Primitives.ToggleButton)return true;
            var visual=element as Visual;
            element=visual==null?null:VisualTreeHelper.GetParent(visual);
        }
        return false;
    }
}

internal enum FolderTreeIcon {Folder,Markdown,Text,Refresh}

internal sealed class FolderTreeEntry {
    internal string FullPath="";
    internal string Name="";
    internal bool IsFolder;
    /* UI-thread only: a folder is read once, on its first expansion. */
    internal bool Loaded;
    internal bool Loading;
}

internal sealed class FolderTreeReadResult {
    internal List<FolderTreeEntry> Entries=new List<FolderTreeEntry>();
    internal bool Capped;
}

/* Everything that is not UI wiring for the 파일 tab. No instance state: two windows share it safely. */
internal static class FolderTree {
    /* One row text size for the panel (the folder name row, the tree's file names and the heading
       list): the two tabs read alike and a zoomed document never inflates the narrow panel (user,
       2026-11-09). */
    internal static double RowFontSize(double baseSize){return Math.Max(12,Math.Min(14,baseSize));}
    internal const int MaxEntries=2000;
    internal const uint FosPickFolders=0x20,FosForceFileSystem=0x40,FosPathMustExist=0x800;
    internal const uint SigdnFileSystemPath=0x80058000;
    static readonly FolderNameComparer NameComparer=FolderNameComparer.Instance;

    internal static bool IsMarkdown(string name){
        string extension=System.IO.Path.GetExtension(name);
        return String.Equals(extension,".md",StringComparison.OrdinalIgnoreCase)||String.Equals(extension,".markdown",StringComparison.OrdinalIgnoreCase);
    }
    internal static bool IsShownFile(string name){
        return IsMarkdown(name)||String.Equals(System.IO.Path.GetExtension(name),".txt",StringComparison.OrdinalIgnoreCase);
    }
    /* One EnumerateFileSystemInfos pass, hidden/system entries skipped, at most MaxEntries rows with
       one extra seen row proving the listing was cut. A folder that holds none of the extensions is
       still listed: checking that at any depth would cost time on every expansion. */
    internal static FolderTreeReadResult ReadFolder(string folder){
        var result=new FolderTreeReadResult();
        var folders=new List<FolderTreeEntry>();
        var files=new List<FolderTreeEntry>();
        try{
            var info=new DirectoryInfo(folder);
            int shown=0;
            foreach(FileSystemInfo child in info.EnumerateFileSystemInfos()){
                FileAttributes attributes;
                try{attributes=child.Attributes;}catch(IOException){continue;}catch(UnauthorizedAccessException){continue;}
                if((attributes&(FileAttributes.Hidden|FileAttributes.System))!=0)continue;
                bool isFolder=(attributes&FileAttributes.Directory)!=0;
                if(!isFolder&&!IsShownFile(child.Name))continue;
                if(shown>=MaxEntries){result.Capped=true;break;}
                var entry=new FolderTreeEntry {FullPath=child.FullName,Name=child.Name,IsFolder=isFolder};
                if(isFolder)folders.Add(entry);else files.Add(entry);
                shown++;
            }
        }catch(UnauthorizedAccessException){}catch(IOException){}catch(System.Security.SecurityException){}catch(ArgumentException){}catch(NotSupportedException){}
        Comparison<FolderTreeEntry> byName=delegate(FolderTreeEntry left,FolderTreeEntry right){return NameComparer.Compare(left.Name,right.Name);};
        folders.Sort(byName);files.Sort(byName);
        result.Entries.AddRange(folders);result.Entries.AddRange(files);
        return result;
    }
    internal static string DisplayName(string folder){
        try{var info=new DirectoryInfo(folder);string name=info.Name;return String.IsNullOrEmpty(name)?folder:name;}catch(ArgumentException){return folder;}catch(IOException){return folder;}catch(System.Security.SecurityException){return folder;}
    }
    /* Current tab's file folder; when that tab is unsaved, the most recent file's folder; then
       Documents. */
    internal static string FolderFor(string file,List<string> recent){
        string folder=ExistingFolder(file);
        if(folder!=null)return folder;
        if(recent!=null)foreach(string entry in recent){folder=ExistingFolder(entry);if(folder!=null)return folder;}
        string documents=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if(!String.IsNullOrEmpty(documents)&&Directory.Exists(documents))return documents;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
    static string ExistingFolder(string file){
        if(String.IsNullOrEmpty(file))return null;
        try{
            if(!File.Exists(file))return null;
            string folder=System.IO.Path.GetDirectoryName(file);
            return folder!=null&&Directory.Exists(folder)?folder:null;
        }catch(ArgumentException){return null;}catch(NotSupportedException){return null;}catch(IOException){return null;}catch(System.Security.SecurityException){return null;}
    }
    /* Folders the reader has open, read before a refresh rebuilds the tree. */
    internal static HashSet<string> ExpandedPaths(ItemCollection items){
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddExpandedPaths(items,paths);
        return paths;
    }
    static void AddExpandedPaths(ItemCollection items,HashSet<string> paths){
        if(items==null)return;
        foreach(object entry in items){
            var item=entry as TreeViewItem;if(item==null)continue;
            var folder=item.Tag as FolderTreeEntry;
            if(folder==null||!folder.IsFolder)continue;
            if(!item.IsExpanded)continue;
            paths.Add(folder.FullPath);
            AddExpandedPaths(item.Items,paths);
        }
    }
    /* 14 px folder / markdown / text / refresh marks, one round stroke in the panel's Muted ink
       (a DynamicResource, so a theme switch repaints them without a rebuild). The shared Ui.GridIcon
       set has no folder or file key (it has a 20 px "open" folder and a Markdown badge), so the
       file tab draws its own smaller family here. */
    internal static FrameworkElement Icon(FolderTreeIcon kind){
        string data;
        switch(kind){
            case FolderTreeIcon.Markdown:data="M3 1.6 H8.1 L10.9 4.4 V12.4 H3 Z M8.1 1.6 V4.4 H10.9 M4.6 10.4 V6.8 L5.9 8.4 L7.2 6.8 V10.4";break;
            case FolderTreeIcon.Text:data="M3 1.6 H8.1 L10.9 4.4 V12.4 H3 Z M8.1 1.6 V4.4 H10.9 M4.8 6.4 H9.1 M4.8 8.4 H9.1 M4.8 10.4 H7.6";break;
            case FolderTreeIcon.Refresh:data="M11.7 5.4 A4.6 4.6 0 1 0 12.2 9.3 M11.7 2.3 V5.5 H8.5";break;
            default:data="M1.5 3.5 H5.2 L6.8 5.3 H12.5 V11.4 H1.5 Z";break;
        }
        var shape=new System.Windows.Shapes.Path {Data=Geometry.Parse(data),StrokeThickness=1.1,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
        shape.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Muted");
        var canvas=new Canvas {Width=14,Height=14,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        canvas.Children.Add(shape);
        return canvas;
    }
    static ControlTemplate iconButtonTemplate;
    internal static ControlTemplate IconButtonTemplate(){
        if(iconButtonTemplate==null){string xaml=@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'><Border x:Name='Surface' Background='Transparent' CornerRadius='4'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Pressed}'/></Trigger></ControlTemplate.Triggers></ControlTemplate>";iconButtonTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);}
        return iconButtonTemplate;
    }
}

/* Natural order (digit runs compare as numbers) that ignores case; the app's UI is ordinal, so no
   culture rules are mixed in. */
internal sealed class FolderNameComparer : IComparer<string> {
    internal static readonly FolderNameComparer Instance=new FolderNameComparer();
    public int Compare(string left,string right){
        if(Object.ReferenceEquals(left,right))return 0;
        if(left==null)return right==null?0:-1;
        if(right==null)return 1;
        int i=0,j=0;
        while(i<left.Length&&j<right.Length){
            char a=left[i],b=right[j];
            if(Char.IsDigit(a)&&Char.IsDigit(b)){
                int startA=i,startB=j;
                while(i<left.Length&&Char.IsDigit(left[i]))i++;
                while(j<right.Length&&Char.IsDigit(right[j]))j++;
                int endA=i,endB=j;
                while(startA<endA-1&&left[startA]=='0')startA++;
                while(startB<endB-1&&right[startB]=='0')startB++;
                int lengthA=endA-startA,lengthB=endB-startB;
                if(lengthA!=lengthB)return lengthA<lengthB?-1:1;
                for(int k=0;k<lengthA;k++){char ca=left[startA+k],cb=right[startB+k];if(ca!=cb)return ca<cb?-1:1;}
            }else{
                char ca=Char.ToUpperInvariant(a),cb=Char.ToUpperInvariant(b);
                if(ca!=cb)return ca<cb?-1:1;
                i++;j++;
            }
        }
        int restA=left.Length-i,restB=right.Length-j;
        if(restA!=restB)return restA<restB?-1:1;
        return StringComparer.OrdinalIgnoreCase.Compare(left,right);
    }
}

/* Vista IFileOpenDialog with FOS_PICKFOLDERS, so the file tab never loads System.Windows.Forms (the
   memory goal of this release). The dialog is modal on this window and returns null on cancel. */
internal static class FolderPicker {
    internal static string Pick(Window owner,string start,string title){
        IFileOpenDialog dialog=null;
        try{
            dialog=(IFileOpenDialog)new FolderOpenDialogCoClass();
            uint options=0;
            try{dialog.GetOptions(out options);}catch(Exception){options=0;}
            dialog.SetOptions(options|FolderTree.FosPickFolders|FolderTree.FosForceFileSystem|FolderTree.FosPathMustExist);
            try{dialog.SetTitle(title);}catch(Exception){}
            if(!String.IsNullOrEmpty(start)){
                IShellItem folder=CreateItem(start);
                if(folder!=null){try{dialog.SetFolder(folder);}catch(Exception){}try{Marshal.ReleaseComObject(folder);}catch(Exception){}}
            }
            IntPtr handle=IntPtr.Zero;
            if(owner!=null)handle=new System.Windows.Interop.WindowInteropHelper(owner).Handle;
            if(dialog.Show(handle)<0)return null;
            IShellItem result;
            if(dialog.GetResult(out result)<0||result==null)return null;
            string selected=ShellPath(result);
            try{Marshal.ReleaseComObject(result);}catch(Exception){}
            return selected;
        }catch(Exception){return null;}
        finally{if(dialog!=null)try{Marshal.ReleaseComObject(dialog);}catch(Exception){}}
    }
    static IShellItem CreateItem(string folder){
        try{
            Guid iid=ShellItemIid;
            IShellItem item;
            int hr=SHCreateItemFromParsingName(folder,IntPtr.Zero,ref iid,out item);
            return hr<0?null:item;
        }catch(Exception){return null;}
    }
    static string ShellPath(IShellItem item){
        if(item==null)return null;
        IntPtr pointer;
        if(item.GetDisplayName(FolderTree.SigdnFileSystemPath,out pointer)!=0||pointer==IntPtr.Zero)return null;
        try{return Marshal.PtrToStringUni(pointer);}finally{Marshal.FreeCoTaskMem(pointer);}
    }
    static readonly Guid ShellItemIid=new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string pszPath,IntPtr pbc,ref Guid riid,[MarshalAs(UnmanagedType.Interface)] out IShellItem ppsi);
}

[ComImport,Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
internal class FolderOpenDialogCoClass { }

[ComImport,Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem {
    void BindToHandler(IntPtr pbc,ref Guid bhid,ref Guid riid,out IntPtr ppv);
    void GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName,out IntPtr ppszName);
    void GetAttributes(uint sfgaoMask,out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi,uint hint,out int piOrder);
}

[ComImport,Guid("d57c7288-d4ad-4768-be02-9d969532d960"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOpenDialog {
    [PreserveSig] int Show(IntPtr parent);
    void SetFileTypes(uint cFileTypes,IntPtr rgFilterSpec);
    void SetFileTypeIndex(uint iFileType);
    void GetFileTypeIndex(out uint piFileType);
    void Advise(IntPtr pfde,out uint pdwCookie);
    void Unadvise(uint dwCookie);
    [PreserveSig] int SetOptions(uint fos);
    [PreserveSig] int GetOptions(out uint pfos);
    void SetDefaultFolder(IShellItem psi);
    void SetFolder(IShellItem psi);
    void GetFolder(out IShellItem ppsi);
    void GetCurrentSelection(out IShellItem ppsi);
    void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetFileName(out IntPtr pszName);
    void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
    void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
    void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
    [PreserveSig] int GetResult(out IShellItem ppsi);
    void AddPlace([MarshalAs(UnmanagedType.LPWStr)] string pszName,IShellItem psi,int fdap);
    void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
    void Close(int hr);
    void SetClientGuid(ref Guid guid);
    void ClearClientData();
    void SetFilter(IntPtr pFilter);
    void GetResults(out IntPtr ppenum);
    void GetSelectedItems(out IntPtr ppsai);
}
}
