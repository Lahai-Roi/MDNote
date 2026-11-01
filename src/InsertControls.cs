using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
namespace MDNote {public partial class MainWindow {
    ContextMenu headingMenu,tablePickerMenu;int lastHeadingLevel=2;
    MenuItem listBulletMenuItem,listNumberMenuItem;
    System.Windows.Threading.DispatcherTimer tablePickerCloseTimer;bool tablePickerHoverOpened;DateTime? tablePickerOutsideSince;
    internal Func<Point> tablePickerPointerPosition;
    void CancelTablePickerClose(){if(tablePickerCloseTimer!=null)tablePickerCloseTimer.Stop();tablePickerOutsideSince=null;}
    static bool TablePickerContains(FrameworkElement element,Point point){if(element==null||!element.IsVisible||element.ActualWidth<=0||element.ActualHeight<=0)return false;try{return new Rect(element.PointToScreen(new Point()),element.PointToScreen(new Point(element.ActualWidth,element.ActualHeight))).Contains(point);}catch(InvalidOperationException){return false;}}
    void PollTablePicker(DateTime now){var menu=tablePickerMenu;if(menu==null||!menu.IsOpen||!tablePickerHoverOpened){CancelTablePickerClose();return;}var cursor=System.Windows.Forms.Cursor.Position;Point point=tablePickerPointerPosition==null?new Point(cursor.X,cursor.Y):tablePickerPointerPosition();/* closes as soon as the pointer is outside the button, the grid and the gap between them (user, 2026-10-24) */if(TablePickerContains(tableButton,point)||TablePickerContains(menu,point)||TablePickerBridge(tableButton,menu,point)){tablePickerOutsideSince=null;return;}menu.IsOpen=false;}
    static bool TablePickerBridge(FrameworkElement button,FrameworkElement menu,Point point){if(button==null||menu==null||!button.IsVisible||!menu.IsVisible)return false;try{Point a=button.PointToScreen(new Point()),b=button.PointToScreen(new Point(button.ActualWidth,button.ActualHeight)),c=menu.PointToScreen(new Point()),d=menu.PointToScreen(new Point(menu.ActualWidth,menu.ActualHeight));double top=Math.Min(b.Y,d.Y),bottom=Math.Max(a.Y,c.Y);if(bottom<top){double swap=top;top=bottom;bottom=swap;}var band=new Rect(new Point(Math.Min(a.X,c.X),top-1),new Point(Math.Max(b.X,d.X),bottom+1));return band.Contains(point);}catch(InvalidOperationException){return false;}}
    void QueueTablePickerClose(){if(!tablePickerHoverOpened)return;if(tablePickerCloseTimer==null){tablePickerCloseTimer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(40)};tablePickerCloseTimer.Tick+=delegate{PollTablePicker(DateTime.UtcNow);};}tablePickerCloseTimer.Start();}
    /* X29: Esc must win over the button's hover-open. Closing a menu hands the mouse back, WPF re-enters
       the button under the pointer and MouseEnter opened the grid again, so the first Escape looked dead
       (user, 2026-10-30). A close by Escape waits for the pointer to leave the button once. */
    bool tablePickerEscClosed;
    /* b57: hover-open state. The screen point the pointer had when the button appeared under it, and
       whether a real move has been seen since; see TableHoverMoved. */
    Point tableHoverScreen;bool tableHoverArmed;
    /* X29: every menu paints the app's own surfaces in dark mode. The system menu templates keep their
       light theme brushes (see Ui.DarkMenuTemplate) and a bar item moved into the "»" list keeps the light
       Ink of the toolbar, which read as grey on the light system popup. Entries are restyled on open and
       put back when the theme is light, so light keeps the system chrome. */
    sealed class MenuEntrySkin{internal Style Style;}
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement,MenuEntrySkin> menuEntrySkins=new System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement,MenuEntrySkin>();
    bool menuBarSkinApplied;bool menuBarSkinDark;
    static MainWindow(){
        ScrollThumbFloor.Install();
        EventManager.RegisterClassHandler(typeof(ContextMenu),ContextMenu.OpenedEvent,new RoutedEventHandler(AnyContextMenuOpened));
        EventManager.RegisterClassHandler(typeof(Menu),UIElement.PreviewMouseDownEvent,new System.Windows.Input.MouseButtonEventHandler(AnyMenuPointer));
        EventManager.RegisterClassHandler(typeof(Menu),UIElement.GotKeyboardFocusEvent,new System.Windows.Input.KeyboardFocusChangedEventHandler(AnyMenuFocus));
        EventManager.RegisterClassHandler(typeof(MenuItem),MenuItem.SubmenuOpenedEvent,new RoutedEventHandler(AnySubmenuOpened));
    }
    static void AnyContextMenuOpened(object sender,RoutedEventArgs args){
        var menu=sender as ContextMenu;if(menu==null)return;
        MainWindow owner=MenuOwner(menu);
        if(owner!=null)owner.ApplyMenuSkin(menu);
    }
    static void AnyMenuPointer(object sender,System.Windows.Input.MouseButtonEventArgs args){
        var menu=sender as Menu;if(menu==null)return;
        MainWindow owner=Window.GetWindow(menu) as MainWindow;
        if(owner!=null)owner.ApplyMenuBarSkin(menu);
    }
    static void AnyMenuFocus(object sender,System.Windows.Input.KeyboardFocusChangedEventArgs args){
        var menu=sender as Menu;if(menu==null)return;
        MainWindow owner=Window.GetWindow(menu) as MainWindow;
        if(owner!=null)owner.ApplyMenuBarSkin(menu);
    }
    /* Backstop for bar submenus opened by hover navigation, where no mouse-down reaches the Menu. */
    static void AnySubmenuOpened(object sender,RoutedEventArgs args){
        var item=sender as MenuItem;if(item==null)return;
        var menu=item.Parent as Menu;if(menu==null)return;
        MainWindow owner=Window.GetWindow(menu) as MainWindow;
        if(owner!=null)owner.ApplyMenuBarSkin(menu);
    }
    static MainWindow MenuOwner(ContextMenu menu){
        var target=menu==null?null:menu.PlacementTarget;
        var owner=target==null?null:Window.GetWindow(target) as MainWindow;
        if(owner==null&&System.Windows.Input.Keyboard.FocusedElement is DependencyObject)owner=Window.GetWindow((DependencyObject)System.Windows.Input.Keyboard.FocusedElement) as MainWindow;
        if(owner==null&&Application.Current!=null)foreach(Window candidate in Application.Current.Windows){var editor=candidate as MainWindow;if(editor!=null&&editor.IsActive){owner=editor;break;}}
        return owner;
    }
    void ApplyMenuSkin(ContextMenu menu){
        if(menu==null)return;
        if(dark){
            menu.Template=Ui.DarkMenuTemplate();
            menu.Background=Ui.MenuSurface;menu.BorderBrush=Ui.MenuLine;menu.BorderThickness=new Thickness(1);menu.Padding=new Thickness(3);
            menu.Foreground=Ui.MenuInk;menu.HasDropShadow=false;
        }else if(Object.ReferenceEquals(menu.Template,Ui.DarkMenuTemplate())){
            menu.ClearValue(Control.TemplateProperty);menu.ClearValue(Control.BackgroundProperty);menu.ClearValue(Control.BorderBrushProperty);
            menu.ClearValue(Control.BorderThicknessProperty);menu.ClearValue(Control.PaddingProperty);menu.ClearValue(Control.ForegroundProperty);menu.ClearValue(ContextMenu.HasDropShadowProperty);
        }
        ThemeMenuEntries(menu.Items);
    }
    /* The bar item replaces the window's ItemContainerStyle while dark (stored and put back for light);
       its submenu entries and their own submenus follow. */
    void ApplyMenuBarSkin(Menu menu){
        if(menu==null)return;
        ThemeMenuEntries(menu.Items);
        menuBarSkinApplied=true;menuBarSkinDark=dark;
    }
    void RefreshMenuBarSkin(){
        if(topMenu==null)return;
        if(menuBarSkinApplied&&menuBarSkinDark==dark)return;
        ApplyMenuBarSkin(topMenu);
        /* b56: bar items parked in the "»" list are children of that ContextMenu, so the pass above
           never reaches them. Restyle them too, otherwise a theme switch leaves them with the other
           theme's entry skin until the list is opened again. */
        if(topOverflowMenu!=null)ThemeMenuEntries(topOverflowMenu.Items);
    }
    void ThemeMenuEntries(ItemCollection items){
        if(items==null)return;
        foreach(object entry in items){
            var item=entry as MenuItem;
            if(item!=null){ThemeMenuEntry(item);ThemeMenuEntries(item.Items);continue;}
            var separator=entry as Separator;
            if(separator!=null)ThemeMenuEntry(separator);
        }
    }
    void ThemeMenuEntry(FrameworkElement entry){ThemeMenuEntry(entry,false);}
    /* forceTop: an item that belongs to the bar but is parked in the "»" list at this moment. Its parent
       is that ContextMenu, so the shape test below would paint it as a submenu row; putting it back on
       the bar has to restore the bar shape (InsertTopMenuItem). */
    void ThemeMenuEntry(FrameworkElement entry,bool forceTop){
        if(entry==null)return;
        MenuEntrySkin saved;
        if(dark){
            if(!menuEntrySkins.TryGetValue(entry,out saved)){saved=new MenuEntrySkin {Style=entry.Style};menuEntrySkins.Add(entry,saved);}
            entry.Style=forceTop?Ui.DarkTopMenuItemStyle():MenuEntryDarkStyle(entry);
        }else if(menuEntrySkins.TryGetValue(entry,out saved)){
            entry.Style=saved.Style;menuEntrySkins.Remove(entry);
        }
    }
    static Style MenuEntryDarkStyle(FrameworkElement entry){
        if(entry is Separator)return Ui.DarkSeparatorStyle();
        var item=entry as MenuItem;
        bool top=item!=null&&(item.Parent is Menu||ItemsControl.ItemsControlFromItemContainer(item) is Menu);
        return top?Ui.DarkTopMenuItemStyle():Ui.DarkSubMenuItemStyle();
    }
    readonly Button taskButton=new Button(),blockButton=new Button(),tableButton=new Button(),imageButton=new Button(),ruleButton=new Button();bool numberedList;
    string listCaptionIcon;/* b55: the glyph kind the list caption row currently shows, so a language update can reuse it */
    static StackPanel IconButtonLabel(FrameworkElement icon,string label){var row=new StackPanel {Orientation=Orientation.Horizontal};icon.Margin=new Thickness(0,0,1,0);icon.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(icon);row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});return row;}
    static StackPanel ButtonLabel(string glyph,string label){var row=new StackPanel {Orientation=Orientation.Horizontal};row.Children.Add(new TextBlock {Text=glyph,Margin=new Thickness(0,0,3,0),VerticalAlignment=VerticalAlignment.Center});row.Children.Add(new TextBlock {Text=label,VerticalAlignment=VerticalAlignment.Center});return row;}
    /* 2026-10-28 user: the menu opens under the whole split button, not under the arrow; the target is the split pair
       (its left edge is the button's visible left edge). The table picker's target is its own square button, so it is
       unchanged by the shared rule. */
    /* b57: the popups below take the keyboard focus, so an IME composition still open at that moment must be
       put into the document first, the way the menu bar does it (CommitCompositionForMenu). */
    void ShowDropdown(FrameworkElement target,ContextMenu menu){CommitCompositionForMenu("toolbar dropdown");Ui.ToolbarText(menu,FontFamily);ApplyMenuSkin(menu);Ui.Dropdown(target,menu);if(menu!=headingMenu)TranslateTree(menu);menu.IsOpen=true;}
    const double HeadingBodyMenuSize=13.5;
    static readonly double[] HeadingMenuSizes={19,17,15.5,14.5,13.5,12.5};
    static FontWeight HeadingWordWeight(int level){return level==1||level==2?FontWeights.Bold:level>=5?FontWeights.Light:FontWeights.Normal;}
    static readonly FontFamily HeadingDigitFamily=new FontFamily("Segoe UI");/* 11-01: Segoe UI Black/Semibold/Semilight/Light are installed as their own family names; asking "Segoe UI" for those weights drew Bold or Regular (measured), so each level names its face. */static readonly string[] HeadingDigitFaces={"Segoe UI Black","Segoe UI","Segoe UI Semibold","Segoe UI","Segoe UI Semilight","Segoe UI Light"};static readonly FontWeight[] HeadingDigitWeights={FontWeights.Black,FontWeights.Bold,FontWeights.SemiBold,FontWeights.Normal,FontWeights.Normal,FontWeights.Normal};static FontFamily HeadingDigitFamilyFor(int level){return level>=1&&level<=6?new FontFamily(HeadingDigitFaces[level-1]+", Segoe UI"):HeadingDigitFamily;}/* 11-01: the word has only three real faces (Bold, Regular, Malgun Gothic Semilight), so the Segoe UI digit carries the step inside each pair: every neighbouring level differs in the word or in a clearly different digit weight. */static FontWeight HeadingNumberWeight(int level){return level>=1&&level<=6?HeadingDigitWeights[level-1]:FontWeights.Normal;}static readonly FontFamily HeadingLightWordFamily=new FontFamily("Malgun Gothic Semilight, Segoe UI Semilight");
    TextBlock HeadingText(string text,int level,double size,double width){
        var caption=new TextBlock {Tag="HeadingText",FontFamily=FontFamily,FontSize=size,FontWeight=FontWeights.Normal,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        TextOptions.SetTextFormattingMode(caption,TextFormattingMode.Display);TextOptions.SetTextRenderingMode(caption,TextRenderingMode.ClearType);
        System.Windows.Automation.AutomationProperties.SetName(caption,text);
        if(level==0)caption.Inlines.Add(new Run(text){FontWeight=FontWeights.Normal});
        else{var word=new Run(text.Substring(0,text.Length-1)){FontWeight=HeadingWordWeight(level)};if(level>=5){word.FontFamily=HeadingLightWordFamily;word.FontWeight=FontWeights.Normal;}caption.Inlines.Add(word);/* 11-01: Malgun Gothic has only Semilight/Regular/Bold, so SemiBold (3) drew as Regular like 4; the level digit uses Segoe UI, whose six weights keep every level visibly different. */caption.Inlines.Add(new Run(text.Substring(text.Length-1)){FontWeight=HeadingNumberWeight(level),FontFamily=HeadingDigitFamilyFor(level)});}
        if(width>0)caption.Width=width;
        var formatted=HeadingFormatted(text,level,size);Rect ink=formatted.BuildGeometry(new Point()).Bounds;if(!ink.IsEmpty)caption.RenderTransform=new TranslateTransform(0,formatted.Height/2-Ui.ChromeInkConverter.InkCentre(formatted,"heading|"+FontFamily.Source+"|"+level+"|"+size+"|"+text));
        return caption;
    }
    FormattedText HeadingFormatted(string text,int level,double size){var face=new Typeface(FontFamily,FontStyles.Normal,HeadingWordWeight(level),FontStretches.Normal);var formatted=new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,size,Brushes.Black,null,TextFormattingMode.Display,1.0);if(level>0)formatted.SetFontWeight(HeadingNumberWeight(level),text.Length-1,1);return formatted;}
    double HeadingTextWidth(string text,int level,double size){return HeadingFormatted(text,level,size).WidthIncludingTrailingWhitespace;}
    void BuildParagraphControls(StackPanel styleGroup,StackPanel listGroup,StackPanel blockGroup){
        headingMenu=new ContextMenu();SetupButton(headingButton,"제목",delegate{int level=lastHeadingLevel;SetHeading(level);});headingButton.ToolTip="마지막 선택 제목 적용";var headingArrow=headingChevron=new Button {Focusable=false,ToolTip="제목 단계 선택"};headingHover=Ui.SplitButton(headingButton,headingArrow,28);headingHover.Margin=new Thickness(0);headingArrow.Click+=delegate{ShowDropdown(headingHover,headingMenu);};headingSlot=new Grid();headingSlot.Children.Add(headingHover);styleGroup.Children.Add(headingSlot);UpdateHeadingCaption();
        headingBodyItem=new MenuItem {Header=HeadingText(T("본문"),0,HeadingBodyMenuSize,0),IsCheckable=false};headingBodyItem.Click+=delegate{SetHeading(0);};headingMenu.Items.Add(headingBodyItem);headingMenu.Items.Add(new Separator());for(int i=1;i<=6;i++){int level=i;var item=new MenuItem {Header=HeadingText(HeadingName(i),i,HeadingMenuSizes[i-1],0),IsCheckable=false};item.Click+=delegate{lastHeadingLevel=level;UpdateHeadingCaption();SavePreferences();SetHeading(level);};headingButtons.Add(item);headingMenu.Items.Add(item);}
        SetupButton(listButton,"• 목록",ApplyLastList);listButton.Width=Double.NaN;listButton.ToolTip="마지막 선택 목록 적용";var arrow=listChevron=new Button {Focusable=false,ToolTip="목록 종류 선택"};var choices=new ContextMenu();foreach(bool number in new bool[]{false,true}){bool selectedNumber=number;var option=new MenuItem();if(number)listNumberMenuItem=option;else listBulletMenuItem=option;option.Click+=delegate{numberedList=selectedNumber;UpdateListCaption();SavePreferences();ApplyLastList();};choices.Items.Add(option);}/* Headers come from UpdateListCaption: a "· " / "1. " prefix plus the translated list name, so the dropdown reads as a list sample. */arrow.Click+=delegate{ShowDropdown(listHover,choices);};listHover=Ui.SplitButton(listButton,arrow,28);/* user 2026-10-28: same gap to the next icon as the other dropdowns */listHover.Margin=new Thickness(0,0,7,0);listGroup.Children.Add(listHover);UpdateListCaption();
        SetupButton(taskButton,"체크",ToggleTaskList);taskButton.Content=Ui.GridIcon("check");taskButton.ToolTip="체크리스트 삽입";listGroup.Children.Add(taskButton);/* user 2026-10-28: label buttons get the same visual gap as icon buttons */listGroup.Children.Add(new Border {Width=8});
        SetupButton(quoteButton,"인용",Quote);if(!translations.ContainsKey("인용"))translations["인용"]=new[]{"Quote","引用","引用","引用"};quoteButton.Content=Ui.GridIcon("quote");quoteButton.ToolTip="인용문 켜기/끄기";listGroup.Children.Add(quoteButton);SetupButton(blockButton,"코드",ToggleCodeBlock);blockButton.Content=Ui.GridIcon("code");blockButton.ToolTip="코드 블록 삽입";blockGroup.Children.Add(blockButton);blockGroup.Children.Add(new Border {Width=8});SetupButton(ruleButton,"구분",delegate{InsertMarkdownBlock("---");});ruleButton.Content=Ui.GridIcon("rule");ruleButton.ToolTip="구분선 삽입";blockGroup.Children.Add(ruleButton);
        headingButton.FontSize=Ui.LabelSize;listButton.FontSize=Ui.LabelSize;foreach(StackPanel panel in new StackPanel[]{listGroup,blockGroup})foreach(UIElement child in panel.Children){formatControls.Add(child);var button=child as Button;if(button!=null){button.FontSize=Ui.LabelSize;button.Padding=new Thickness(5,1,5,1);button.Margin=new Thickness(Ui.ButtonGap,0,Ui.ButtonGap,0);}}
    }
    // b38 (Docs wording): the closed dropdown reads "본문" or "제목 N" for the caret paragraph (the last chosen level while the live view cannot tell); the list shows the same names.
    MenuItem headingBodyItem;
    int liveHeadingLevel;
    int HeadingCaptionLevel(){return Object.Equals(headingButton.Tag,"Active")?Math.Max(0,liveHeadingLevel):0;}
    string HeadingName(int level){return T("제목")+" "+level;}
    // b39: the three dropdowns (heading, size, list) hug their widest caption + 2 px and keep that width; captions are centred; the widest sets the chevron gap.
    double CaptionWidth(Control owner,FontWeight weight,params string[] texts){double widest=0;var face=new Typeface(owner.FontFamily,owner.FontStyle,weight,owner.FontStretch);foreach(string text in texts){var measured=new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,Ui.LabelSize,Brushes.Black,1.0);widest=Math.Max(widest,measured.WidthIncludingTrailingWhitespace);}return Math.Ceiling(widest)+2;}
    const double HeadingCaptionSize=13;
    static readonly System.Collections.Generic.Dictionary<string,Rect> headingCaptionInk=new System.Collections.Generic.Dictionary<string,Rect>();
    Rect HeadingCaptionInk(string text,int level){string key=FontFamily.Source+":"+level+":"+text;Rect ink;if(!headingCaptionInk.TryGetValue(key,out ink)){ink=HeadingFormatted(text,level,HeadingCaptionSize).BuildGeometry(new Point()).Bounds;headingCaptionInk[key]=ink;}return ink;}
    double HeadingCaptionWidth(){double widest=HeadingCaptionInk(T("본문"),0).Width;for(int i=1;i<=6;i++)widest=Math.Max(widest,HeadingCaptionInk(HeadingName(i),i).Width);return Math.Ceiling(widest)+2;}
    FrameworkElement HeadingCaptionBlock(int level){string text=level<1?T("본문"):HeadingName(level);level=Math.Max(0,level);var caption=HeadingText(text,level,HeadingCaptionSize,0);Rect ink=HeadingCaptionInk(text,level);/* cached font side bearings: the shared hover surface pads the ink, not the advance box */if(!ink.IsEmpty)caption.Margin=new Thickness(-ink.Left,0,ink.Right-HeadingTextWidth(text,level,HeadingCaptionSize),0);return caption;}
    /* b45: one 4 px padded hover surface; both hit targets remain independent, centred together in a stable slot. */
    Grid headingSlot;ContentControl headingHover,listHover,saveHover,colorHover;Button headingChevron,listChevron,saveMain,saveChevron;
    void SizeHeadingSlot(){if(headingSlot!=null)headingSlot.Width=HeadingCaptionWidth()+headingButton.Padding.Left+headingButton.Padding.Right+Ui.SplitArrowWidth+5;} /* b50: slot = widest caption + main padding + arrow segment + 1px divider + 4px hover-surface left padding; constant across levels */
    void UpdateHeadingCaption(){headingButton.Content=HeadingCaptionBlock(HeadingCaptionLevel());SizeHeadingSlot();if(headingBodyItem!=null)headingBodyItem.Header=HeadingText(T("본문"),0,HeadingBodyMenuSize,0);for(int i=0;i<headingButtons.Count;i++)headingButtons[i].Header=HeadingText(HeadingName(i+1),i+1,HeadingMenuSizes[i],0);/* X29: Theme() ends here, so the menu bar takes the new chrome without waiting for the first click */RefreshMenuBarSkin();}
    /* b55: the caption slot is measured from the translated caption with the same Display metrics the label is
       painted with. The b39/b52 rule measured the Ideal advance and then took 5 px back, which is 2-3 px short of
       the painted ink: English "List" lost the right side of its t, and 목록/列表 lost the last stroke's edge. The
       button keeps its old outer width - the caption takes the extra pixels out of the split button's right padding
       (4 px today), never out of the row - so the confirmed Korean geometry and every control after it do not move.
       A language or theme change updates the existing label: rebuilding the row would drop the realisation of a
       windowless surface (the toolbar-shot host never runs a render pass), and the glyph kind only changes here. */
    void UpdateListCaption(){
        string text=T("목록");
        var face=new Typeface(FontFamily,FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
        var ideal=new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,Ui.LabelSize,Brushes.Black,1.0);
        var display=new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,Ui.LabelSize,Brushes.Black,null,TextFormattingMode.Display,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double icon=20*Ui.IconScale+Ui.RibbonIconLabelGap;
        double slot=Math.Ceiling(ideal.WidthIncludingTrailingWhitespace)-3;/* the confirmed slot: the b39 caption measure less its 5 px trim */
        double extra=Math.Max(0,Math.Ceiling(icon+display.WidthIncludingTrailingWhitespace)-(icon+slot));
        string iconName=numberedList?"listNumber":"listBullet";
        var caption=listButton.Content as StackPanel;
        var label=caption==null||caption.Children.Count<2?null:caption.Children[1] as TextBlock;
        if(label==null||listCaptionIcon!=iconName){
            caption=IconLabelRow(iconName,text);/* Both icons occupy the same fixed grid, so switching kinds never moves the split arrow. b52: the icon slot and the label gap follow the ribbon's shared values, not a private pair. */
            label=(TextBlock)caption.Children[1];listCaptionIcon=iconName;listButton.Content=caption;
        } else label.Text=text;
        caption.Width=icon+slot+extra;
        listButton.Padding=new Thickness(0,0,Math.Max(0,4-extra),0);/* panel + this padding = icon + slot + 4, the old outer width */
        /* b58: the screen-reader name follows the UI language, exactly like the visible label this
           caption is rebuilt with in ApplyLanguage. */
        System.Windows.Automation.AutomationProperties.SetName(caption,T(numberedList?"번호 목록":"글머리 기호 목록"));
        /* The two dropdown entries carry the kind as a sample prefix; translating them through
           TranslateTree would replace the whole header and drop it (see the skip in TranslateTree). */
        if(listBulletMenuItem!=null)listBulletMenuItem.Header="· "+T("글머리 기호 목록");
        if(listNumberMenuItem!=null)listNumberMenuItem.Header="1. "+T("번호 목록");
    }
    void ApplyLastList(){LiveList(numberedList?ListKind.Number:ListKind.Bullet);}
    void ToggleListStyle(bool numbered){LiveList(numbered?ListKind.Number:ListKind.Bullet);}
    void BuildImageControl(StackPanel group){SetupButton(imageButton,"이미지",InsertImage);imageButton.Content=Ui.GridIcon("image");imageButton.ToolTip="이미지 삽입 (Ctrl+Shift+I)";group.Children.Add(imageButton);group.Children.Add(new Border {Width=3});}
    // Bar rules shared by every toolbar: 28 px tall buttons, icon-only 28x28 with 1 px side margins, text buttons padded 6, split halves touching.
    void CompactRibbon(DependencyObject element){foreach(object child in LogicalTreeHelper.GetChildren(element)){var ui=child as DependencyObject;if(ui==null)continue;var owner=LogicalTreeHelper.GetParent(ui) as FrameworkElement;bool direct=owner!=null&&Object.Equals(owner.Tag,"GroupBody");var button=child as Button;if(button!=null)StyleBarButton(button,direct);else if(direct&&(child is StackPanel||child is ComboBox))((FrameworkElement)child).Margin=new Thickness(Ui.ButtonGap,0,Ui.ButtonGap,0);CompactRibbon(ui);}}
    static void StyleBarButton(Button button,bool direct){if(button.Style!=null&&Object.ReferenceEquals(button.Style,button.TryFindResource("ToolbarSplitPart")))return;button.Height=Ui.SquareSize;button.MinHeight=Ui.SquareSize;button.VerticalAlignment=VerticalAlignment.Center;button.Margin=direct?new Thickness(Ui.ButtonGap,0,Ui.ButtonGap,0):new Thickness(0);if(Ui.IsGrid(button.Content)){var keep=button.Margin;Ui.NarrowRibbonSquare(button);button.Margin=keep;return;}if(!Double.IsNaN(button.Width)&&button.Width<=14){button.Width=14;button.MinWidth=0;button.Padding=new Thickness(0);return;}var text=button.Content as string;var icon=button.Content as FrameworkElement;bool glyph=(text!=null&&text.Length<=2)||(icon!=null&&!(icon is StackPanel)&&!(icon is TextBlock));if(glyph){button.Width=direct?Ui.RibbonIconWidth:24;button.MinWidth=0;button.Padding=new Thickness(direct?4:2);if(icon!=null&&!(icon is Viewbox)&&(icon.Width>20||icon.Height>20)){button.Content=null;button.Content=Ui.FitToolbarIcon(icon);}return;}button.Width=Double.NaN;button.MinWidth=direct?Ui.RibbonIconWidth:0;button.Padding=direct?new Thickness(Ui.RibbonLabelPadding,0,Ui.RibbonLabelPadding,0):new Thickness(6,0,2,0);}
    /* User 2026-10-29: the last two buttons (9 내어쓰기, 10 들여쓰기) were a 20 px bar-with-arrow drawing on their own grid, so they read as
       a different set next to the table glyphs. They now take the row-action glyph (table base + ← / → in the badge slot) and go through
       the same StyleBarButton path as their neighbours; tooltip, size, handler and order are set in BuildTableTools and stay untouched. */
    void CompactTableToolbar(){for(int i=0;i<tableActions.Count;i++){var button=tableActions[i];if(i==9||i==10){button.Content=Ui.TableMoveIcon(i==10);StyleBarButton(button,true);continue;}var label=button.Content as StackPanel;if(label!=null&&label.Children.Count>0){var icon=label.Children[0] as FrameworkElement;if(icon!=null&&!Ui.IsGrid(icon)){label.Children.RemoveAt(0);var fitted=Ui.FitToolbarIcon(icon);fitted.Margin=new Thickness(0,0,4,0);label.Children.Insert(0,fitted);}}else{var icon=button.Content as FrameworkElement;if(icon!=null&&!(icon is Viewbox)&&!Ui.IsGrid(icon)){button.Content=null;button.Content=Ui.FitToolbarIcon(icon);}}StyleBarButton(button,true);}var row=tableToolsHost.Child as Panel;if(row!=null)for(int i=0;i<row.Children.Count;i++){var divider=row.Children[i] as Border;if(divider==null||!(divider.Width<=1.5)||i==0||i==row.Children.Count-1)continue;divider.Width=1;divider.Height=16;divider.VerticalAlignment=VerticalAlignment.Center;divider.Margin=new Thickness(Ui.DividerGap,0,Ui.DividerGap,0);Ui.BalanceDivider(divider,(FrameworkElement)row.Children[i-1],(FrameworkElement)row.Children[i+1],row);}tableToolsHost.Padding=new Thickness(7,4,7,4);Ui.NormalizeBarIcons(tableToolsHost);}
    void InsertMarkdownBlock(string markdown){InsertMarkdownBlockNow(markdown);}
    void InsertMarkdownBlockNow(string markdown){if(markdown=="---")LiveRule();}
    void InsertTable(int columns,int rows){if(columns<1||columns>50||rows<1||rows>200)return;LiveTable(columns,rows);}
    // #18: the table button opens the size grid (also on hover); "사용자 지정 표…" opens the number dialog.
    void BuildTableControl(StackPanel group){SetupButton(tableButton,"표",OpenTablePicker);tableButton.Content=Ui.GridIcon("table");tableButton.ToolTip="표 삽입";tableButton.Focusable=true;tableButton.PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Down||e.Key==System.Windows.Input.Key.F4){OpenTablePicker();e.Handled=true;}};/* b56: Esc closes the size grid and puts the caret back in the body (user, 2026-10-30); the grid keeps no keyboard focus of its own, so the window has to answer when focus stayed in the editor. */PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Escape&&tablePickerMenu!=null&&tablePickerMenu.IsOpen){CloseTablePicker(true);e.Handled=true;}};tableButton.MouseEnter+=delegate(object sender,System.Windows.Input.MouseEventArgs e){TableHoverEntered(TablePointerScreen(e));};tableButton.MouseMove+=delegate(object sender,System.Windows.Input.MouseEventArgs e){if(tableHoverArmed)TableHoverMoved(TablePointerScreen(e));};tableButton.MouseLeave+=delegate{tableHoverArmed=false;tablePickerEscClosed=false;};Closed+=delegate{CancelTablePickerClose();if(tablePickerMenu!=null)tablePickerMenu.IsOpen=false;};group.Children.Add(tableButton);}
    /* b57: the formatting row is Collapsed in read mode, so Ctrl+E (or a tab switch) can put the 표
       button under a pointer that never moved. MouseEnter fires for that layout change alone, and the
       grid then took Tab/Space/Enter and the IME away from the body (user, 2026-10-31: typing went into
       the menu and Enter opened the custom table dialog). The hover open now waits for a real move: a
       MouseMove whose screen point differs from the point the pointer had when the button appeared.
       A click and Alt+Down still open at once, and a deliberate hover (any movement) is unchanged.
       X29: after an Escape the pointer usually still rests on the button; tablePickerEscClosed keeps
       the first move from reopening until the pointer has left once. */
    Point TablePointerScreen(System.Windows.Input.MouseEventArgs e){
        try{return tableButton.PointToScreen(e.GetPosition(tableButton));}catch(InvalidOperationException){return e.GetPosition(tableButton);}
    }
    void TableHoverEntered(Point screen){tableHoverArmed=!tablePickerEscClosed;tableHoverScreen=screen;}
    bool TableHoverMoved(Point screen){
        if(!tableHoverArmed||tablePickerEscClosed)return false;
        if(Math.Abs(screen.X-tableHoverScreen.X)<2&&Math.Abs(screen.Y-tableHoverScreen.Y)<2)return false;
        /* A live composition owns the keyboard: a menu opened now takes the syllable into its own focus
           instead of the document. Stay armed so the next move retries once the commit has landed. */
        if(source!=null&&(source.ImeBusy||source.ImeCompositionActive()))return false;
        tableHoverArmed=false;
        if(tablePickerMenu==null||!tablePickerMenu.IsOpen){OpenTablePicker();tablePickerHoverOpened=true;QueueTablePickerClose();}
        return true;
    }
    void OpenTablePicker(){tablePickerEscClosed=false;tablePickerHoverOpened=false;CancelTablePickerClose();if(!LiveFormat)return;if(tablePickerMenu!=null&&tablePickerMenu.IsOpen)return;/* b57: the click and Down/F4 paths open a popup that takes the keyboard focus; commit a live composition first. The hover open keeps its own rule (TableHoverMoved refuses while composing). */CommitCompositionForMenu("table grid");ShowTablePicker();}
    void ShowTablePicker(){CancelTablePickerClose();var menu=tablePickerMenu=new ContextMenu();ThemePickerMenu(menu);menu.Closed+=delegate{if(!Object.ReferenceEquals(tablePickerMenu,menu))return;tablePickerHoverOpened=false;CancelTablePickerClose();};menu.PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Escape){CloseTablePicker(true);e.Handled=true;}};var host=new StackPanel {Margin=new Thickness(6)};var label=new TextBlock {Text=T("표 크기 선택"),Margin=new Thickness(0,0,0,5)};if(dark)label.SetResourceReference(TextBlock.ForegroundProperty,"Ink");host.Children.Add(label);var cells=new System.Windows.Controls.Primitives.UniformGrid {Rows=8,Columns=10};host.Children.Add(cells);var borders=new Border[8,10];for(int row=0;row<8;row++)for(int column=0;column<10;column++){int rr=row,cc=column;var cell=new Border {Width=19,Height=19,Margin=new Thickness(1),BorderThickness=new Thickness(1),BorderBrush=Ui.Brush(dark?"#6B7686":"#9AA7B9"),Background=Brushes.Transparent};borders[row,column]=cell;cells.Children.Add(cell);cell.MouseEnter+=delegate{label.Text=TableSizeLabel(cc+1,"열")+" × "+TableSizeLabel(rr+1,"행");for(int y=0;y<8;y++)for(int x=0;x<10;x++)borders[y,x].Background=y<=rr&&x<=cc?(Brush)Resources["ActiveFill"]:Brushes.Transparent;};cell.MouseLeftButtonDown+=delegate{menu.IsOpen=false;InsertTable(cc+1,rr+1);};}var gridItem=new MenuItem {Header=host,StaysOpenOnClick=true};if(dark)gridItem.SetResourceReference(Control.ForegroundProperty,"Ink");menu.Items.Add(gridItem);menu.Items.Add(new Separator());var custom=new MenuItem {Header=T("사용자 지정 표…")};if(dark)custom.SetResourceReference(Control.ForegroundProperty,"Ink");menu.Items.Add(custom);custom.Click+=delegate{menu.IsOpen=false;ShowCustomTable();};FrameworkElement anchor=tablePickerAnchor==null?tableButton:tablePickerAnchor;tablePickerAnchor=null;ShowDropdown(anchor,menu);}
    /* b56: the size label reads "1 Columns × 2 Rows" in English because both counters are stored as
       plurals (열 = Columns, 행 = Rows). English takes the singular for one; the other languages keep
       exactly the wording they have (the keys themselves are unchanged). */
    string TableSizeLabel(int count,string key){
        string word=T(key);
        if(language=="en"&&count==1&&word.Length>1&&word.EndsWith("s",StringComparison.Ordinal))word=word.Substring(0,word.Length-1);
        return count+" "+word;
    }
    // b38: Markdown-specific commands show icon + 4 px + short label (28 px high, 6 px side padding, borderless); the other buttons stay icon-only.
    internal static StackPanel IconLabelRow(string icon,string label){var row=new StackPanel {Orientation=Orientation.Horizontal};var glyph=Ui.GridIcon(icon);glyph.Margin=new Thickness(0,0,Ui.RibbonIconLabelGap,0);glyph.VerticalAlignment=VerticalAlignment.Center;row.Children.Add(glyph);row.Children.Add(new TextBlock {Text=label,FontSize=Ui.LabelSize,VerticalAlignment=VerticalAlignment.Center});return row;}
    static void LabelButton(Button button,string icon,string label){button.Content=null;button.Content=IconLabelRow(icon,label);button.Width=Double.NaN;StyleBarButton(button,true);}
    // Runs once at the end of the constructor (from BuildTextQuickButton), after every bar is built and compacted.
    void PolishToolbar(){
        ToolTipService.SetInitialShowDelay(this,250);ToolTipService.SetBetweenShowDelay(this,0);/* b41: tooltips after 250 ms, inherited by every toolbar, search and table button */
        LabelButton(taskButton,"check","체크");LabelButton(quoteButton,"quote","인용");LabelButton(ruleButton,"rule","구분");LabelButton(blockButton,"code","코드");codeButton.Content=null;codeButton.Content=Ui.GridIcon("highlight");codeButton.Width=Double.NaN;StyleBarButton(codeButton,true);BindShortcutTip(codeButton,"inline","인라인 코드",null);
        var tagWatch=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(FrameworkElement.TagProperty,typeof(Button));tagWatch.AddValueChanged(headingButton,delegate{UpdateHeadingCaption();});
        var checkWatch=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(MenuItem.IsCheckedProperty,typeof(MenuItem));
        for(int i=0;i<headingButtons.Count;i++){checkWatch.AddValueChanged(headingButtons[i],delegate{UpdateHeadingCaption();});BindShortcutTip(headingButtons[i],"h"+(i+1),"제목 "+(i+1),null);}
        if(headingBodyItem!=null)BindShortcutTip(headingBodyItem,"paragraph","본문",null);
        headingButton.BorderThickness=new Thickness(0);headingButton.Padding=new Thickness(0,0,4,0);headingButton.Margin=new Thickness(0);headingChevron.BorderThickness=new Thickness(0);headingChevron.Padding=new Thickness(0);headingChevron.Margin=new Thickness(0);listButton.Padding=new Thickness(0,0,4,0);UpdateHeadingCaption();UpdateListCaption();RefreshSizeBox();
        var sizeTexts=new System.Collections.Generic.List<string>();foreach(object entry in sizes.Items)sizeTexts.Add(Convert.ToString(entry));sizes.Width=CaptionWidth(sizes,sizes.FontWeight,sizeTexts.ToArray())+20;/* b52: text box margins 2+14, padding 1+1, plus the 14 px arrow segment */
        if(colorMain!=null){colorMain.Width=17;colorMain.MinWidth=17;var colorInk=colorMain.Content as FrameworkElement;if(colorInk!=null)colorInk.Margin=new Thickness(-0.5,0,0,0);}/* A widens downward and the chevron upward, so the same pixel gap reads wider: the A ink moves 3 px toward the arrow inside an unchanged 20 px hit area *//* 2026-10-28 user: A-to-arrow gap now matches 본문/목록 (~5 px) *//* b41: A-to-chevron ink gap equals the heading dropdown caption-to-chevron gap (8.8 px); b51 leaves the main half at 24 and narrows the arrow segment instead */
        preview.Content="원문";
        /* 2026-10-28 user: the 저장 menu opens under the whole 저장 ▾ button like the other three. Editor.cs owns the
           save split and its menu, so the shared rule is applied here, in the pass that runs after every bar exists
           (the arrows are already laid out, and the rule reads nothing but the pair). */
        if(saveHover!=null&&saveMenu!=null)Ui.Dropdown(saveHover,saveMenu);
        Ui.ToolbarText(topMenu);Ui.ToolbarText(documentTabs);Ui.ToolbarText(ribbonHost);Ui.ToolbarText(tableToolsHost);Ui.ToolbarText(searchHost);Ui.ToolbarText(statusBar);Ui.ToolbarText(saveMenu);Ui.ToolbarText(headingMenu);if(colorPopup.Child!=null)Ui.ToolbarText(colorPopup.Child);
        var previewHost=preview.Parent as Border;if(previewHost!=null)previewHost.Padding=new Thickness(0,4,Ui.RightEdge,4);/* border edge = hover-box edge of the outline and close buttons */
        InstallToolbarOverflow();
        HookEditImageCommand();
    }
    /* bughunt4 P3-2: the Edit menu's "이미지 삽입…" entry stayed enabled in TXT mode and in read
       mode, and the command then did nothing at all (LiveImage needs the Markdown editing
       surface). The item is built in Editor.cs, so its state is refreshed here, in the menu
       pipeline this file already owns: the edit submenu's own open is the last moment before its
       entries are painted, and the answer is a pure property of the document mode. */
    MenuItem editImageMenuItem;
    void HookEditImageCommand(){
        if(topMenu==null||editImageMenuItem!=null)return;
        for(int i=0;i<topMenu.Items.Count;i++){
            var parentItem=topMenu.Items[i] as MenuItem;
            if(parentItem==null)continue;
            for(int c=0;c<parentItem.Items.Count;c++){
                var item=parentItem.Items[c] as MenuItem;
                if(item==null||item.InputGestureText!="Ctrl+Shift+I")continue;
                editImageMenuItem=item;
                parentItem.SubmenuOpened+=delegate{RefreshEditImageCommand();};
                return;
            }
        }
    }
    void RefreshEditImageCommand(){
        if(editImageMenuItem!=null)editImageMenuItem.IsEnabled=!PlainText&&editing;
    }
    /* b56: measured overflow for the two toolbar rows. A narrow window clipped the right end of both rows
       (체크·인용·코드·구분·링크·이미지·표 and the 언어 / 도움말 / ≡ menus). The rows only scroll through the thin
       hover strip, so those commands were unreachable. Each row now keeps the commands that fit and moves
       the rest into a "»" drop-down at its right end with the same icon, caption, tooltip, enabled and active
       state, and the same command (top-row menus move as whole items, so their submenus stay live). The rows
       are measured on SizeChanged and on a language switch only - never on a render or a caret callback - and
       at 1050 px and wider every element keeps its exact position. */
    sealed class OverflowCommand{
        internal FrameworkElement Holder;
        internal Button Click;
        internal FrameworkElement State;
        internal string IconKey;
        internal Func<FrameworkElement> Icon;
        internal Func<object[]> SubItems;
        internal Action Action;
        internal string CaptionKey;
    }
    sealed class OverflowUnit{
        internal FrameworkElement Element;
        internal bool Divider;
        internal OverflowCommand Command;
        internal MenuItem Menu;
        internal int MenuIndex;
        internal int Group;
        internal double Width;
    }
    readonly List<OverflowUnit> ribbonOverflowUnits=new List<OverflowUnit>();
    readonly List<OverflowUnit> topOverflowUnits=new List<OverflowUnit>();
    readonly List<Border> ribbonOverflowGroups=new List<Border>();
    readonly Dictionary<object,OverflowCommand> overflowCommands=new Dictionary<object,OverflowCommand>();
    Button ribbonOverflow,topOverflow;
    ContextMenu ribbonOverflowMenu,topOverflowMenu;
    FrameworkElement topBarHost;
    DockPanel topBarPanel;
    ScrollViewer topBarScroll;
    bool toolbarOverflowQueued,toolbarOverflowClosed;
    int ribbonOverflowVisibleCount,topOverflowVisibleCount;
    /* b57 probe surface: one per real overflow pass, so a pass that posts itself again shows up. */
    internal int overflowPassCount;
    FrameworkElement tablePickerAnchor;
    /* b56: submenus opened from the "»" list must unfold inside the window. The template places them right or
       bottom and only the screen edge stops them, so a 450 px window painted 파일/편집 past its right edge. */
    /* b56: weak markers: the ribbon » list builds fresh submenu items on every re-measure (resize or language
       switch), so a strong list would keep every discarded item and popup alive. */
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MenuItem,object> overflowSubmenuHooked=new System.Runtime.CompilerServices.ConditionalWeakTable<MenuItem,object>();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Popup,OverflowSubmenuState> overflowSubmenuStates=new System.Runtime.CompilerServices.ConditionalWeakTable<Popup,OverflowSubmenuState>();
    sealed class OverflowSubmenuState{internal double BaseOffset;internal double BaseVertical;internal MenuItem Item;internal bool DropQueued;}
    void InstallToolbarOverflow(){
        if(ribbonOverflow!=null)return;
        ribbonOverflow=OverflowButton(false);topOverflow=OverflowButton(true);
        ribbonOverflowMenu=new ContextMenu();topOverflowMenu=new ContextMenu();
        ribbonOverflow.Click+=delegate{OpenOverflowMenu(ribbonOverflow,ribbonOverflowMenu);};
        topOverflow.Click+=delegate{OpenOverflowMenu(topOverflow,topOverflowMenu);};
        ribbonOverflowMenu.Opened+=delegate{RefreshOverflowStates(ribbonOverflowMenu);};
        topOverflowMenu.Opened+=delegate{RefreshOverflowStates(topOverflowMenu);};
        var layout=ribbonHost.Child as DockPanel;
        if(layout!=null){DockPanel.SetDock(ribbonOverflow,Dock.Right);layout.Children.Insert(1,ribbonOverflow);}
        topBarPanel=topMenu.Parent as DockPanel;
        if(topBarPanel!=null){DockPanel.SetDock(topOverflow,Dock.Right);topBarPanel.Children.Insert(outlineButton==null?0:1,topOverflow);}
        topBarHost=root.Children.Count>0?root.Children[0] as FrameworkElement:null;
        var topBorder=topBarHost as Border;
        var topGrid=topBorder==null?null:topBorder.Child as Panel;
        if(topGrid!=null&&topGrid.Children.Count>0)topBarScroll=topGrid.Children[0] as ScrollViewer;
        RegisterOverflowCommands();
        ribbonHost.SizeChanged+=delegate{ScheduleToolbarOverflow();};
        if(topBarHost!=null)topBarHost.SizeChanged+=delegate{ScheduleToolbarOverflow();};
        Ui.SetChromeTextChanged(ribbonHost,new Action(ScheduleToolbarOverflow));
        if(topBarHost!=null)Ui.SetChromeTextChanged(topBarHost,new Action(ScheduleToolbarOverflow));
        Closed+=delegate{toolbarOverflowClosed=true;};
        PreviewKeyDown+=OverflowKeyboardKey;
        ScheduleToolbarOverflow();
    }
    /* b56: the » button is Focusable=false (clicking it must not take the keyboard out of the body), so
       nothing on the keyboard could reach the commands that moved into it - 언어, 도움말, About and the
       shortcut dialog had no other path in a narrow window. Alt+Down (the platform's menu gesture) opens
       whichever » is painted; the ContextMenu then owns arrows, access keys and Escape by itself.
       Mouse clicks are untouched: opening from the button never focuses it.
       b58: a 360-450 DIP window paints both » rows. The top row's list opens first and the next press
       opens the ribbon's, alternating, so the formatting commands in the ribbon » are keyboard
       reachable as well; a row that holds the keyboard wins over the alternation. The flag is "the
       next press opens the ribbon »". */
    bool overflowKeyboardNextRibbon;
    void OverflowKeyboardKey(object sender,System.Windows.Input.KeyEventArgs e){
        if(e.Handled||ribbonOverflow==null)return;
        /* the size box is a ComboBox, and Alt+Down is its own "open the list" gesture; Alt+Shift / Ctrl+Alt
           + Down stay AvalonEdit's rectangular-selection keys */
        if(sizes!=null&&(sizes.IsKeyboardFocusWithin||sizes.IsDropDownOpen))return;
        System.Windows.Input.ModifierKeys modifiers=System.Windows.Input.Keyboard.Modifiers;
        bool alt=e.Key==System.Windows.Input.Key.System||(modifiers&System.Windows.Input.ModifierKeys.Alt)!=0;
        if(!alt||(modifiers&(System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Shift))!=0)return;
        System.Windows.Input.Key key=e.Key==System.Windows.Input.Key.System?e.SystemKey:e.Key;
        if(key!=System.Windows.Input.Key.Down)return;
        Button button=OverflowKeyboardTarget();
        if(button==null)return;
        e.Handled=true;
        /* b58: the list is a popup that takes the keyboard, so a live IME syllable commits first. */
        CommitCompositionForKeyboardCommand();
        overflowKeyboardNextRibbon=!Object.ReferenceEquals(button,ribbonOverflow);
        if(Object.ReferenceEquals(button,ribbonOverflow))OpenOverflowMenu(button,ribbonOverflowMenu);
        else OpenOverflowMenu(button,topOverflowMenu);
    }
    Button OverflowKeyboardTarget(){
        Button top=topOverflow!=null&&topOverflow.Visibility==Visibility.Visible&&topOverflow.ActualWidth>0?topOverflow:null;
        Button ribbon=ribbonOverflow!=null&&ribbonOverflow.Visibility==Visibility.Visible&&ribbonOverflow.ActualWidth>0?ribbonOverflow:null;
        if(top!=null&&ribbon!=null){
            if(topBarHost!=null&&topBarHost.IsKeyboardFocusWithin)return top;
            if(ribbonHost!=null&&ribbonHost.IsKeyboardFocusWithin)return ribbon;
            return overflowKeyboardNextRibbon?ribbon:top;
        }
        return top!=null?top:ribbon;
    }
    Button OverflowButton(bool top){
        var button=new Button {Content=Ui.GridIcon("more"),Focusable=false,ToolTip=T("도구 더 보기"),VerticalAlignment=VerticalAlignment.Center};
        if(top){button.Width=button.MinWidth=22;button.Height=button.MinHeight=22;button.Margin=new Thickness(1,0,1,0);}
        else{button.Width=button.MinWidth=Ui.RibbonIconWidth;button.Height=button.MinHeight=Ui.SquareSize;button.Margin=new Thickness(0);}
        button.Padding=new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(button,T("도구 더 보기"));
        return button;
    }
    void RegisterOverflowCommands(){
        RegisterOverflow(undoButton,"undo",null,undoButton,undoButton,null,null,null);
        RegisterOverflow(redoButton,"redo",null,redoButton,redoButton,null,null,null);
        RegisterOverflow(headingSlot,"paragraph",null,headingButton,headingButton,HeadingOverflowItems,null,null);
        RegisterOverflow(sizes,"type",null,null,sizes,SizesOverflowItems,null,"크기");
        RegisterOverflow(boldButton,"bold",null,boldButton,boldButton,null,null,null);
        RegisterOverflow(italicButton,"italic",null,italicButton,italicButton,null,null,null);
        RegisterOverflow(strikeButton,"strike",null,strikeButton,strikeButton,null,null,null);
        RegisterOverflow(codeButton,"highlight",null,codeButton,codeButton,null,null,null);
        RegisterOverflow(colorHover,null,OverflowColorIcon,colorMain,colorMain,ColorOverflowItems,null,"글자색");
        RegisterOverflow(listHover,null,OverflowListIcon,listButton,listButton,ListOverflowItems,null,null);
        RegisterOverflow(taskButton,"check",null,taskButton,taskButton,null,null,null);
        RegisterOverflow(quoteButton,"quote",null,quoteButton,quoteButton,null,null,null);
        RegisterOverflow(blockButton,"code",null,blockButton,blockButton,null,null,null);
        RegisterOverflow(ruleButton,"rule",null,ruleButton,ruleButton,null,null,null);
        RegisterOverflow(linkButton,"link",null,linkButton,linkButton,null,null,null);
        RegisterOverflow(imageButton,"image",null,imageButton,imageButton,null,null,null);
        RegisterOverflow(tableButton,"table",null,tableButton,tableButton,null,OpenTableFromOverflow,null);
        var commands=quickFind.Parent as Panel;
        if(commands!=null)foreach(UIElement child in commands.Children){
            var button=child as Button;if(button==null)continue;
            string tip=OriginalKey(Convert.ToString(button.ToolTip));
            if(tip=="새 문서 만들기 (Ctrl+N)")RegisterOverflow(button,"new",null,button,button,null,null,null);
            else if(tip=="파일 열기 (Ctrl+O)")RegisterOverflow(button,"open",null,button,button,null,null,null);
        }
        if(saveHover!=null&&saveMain!=null)RegisterOverflow(saveHover,"save",null,saveMain,saveMain,null,null,null);
        RegisterOverflow(quickFind,"find",null,quickFind,quickFind,null,null,null);
        RegisterOverflow(quickReplace,"replace",null,quickReplace,quickReplace,null,null,null);
        RegisterOverflow(editButton,"edit",null,editButton,editButton,null,null,null);
        if(quickText!=null)RegisterOverflow(quickText,"type",null,quickText,quickText,null,null,null);
        RegisterOverflow(quickDark,"dark",null,quickDark,quickDark,null,null,null);
        if(outlineButton!=null)RegisterOverflow(outlineButton,"menu",null,outlineButton,outlineButton,null,null,null);
    }
    OverflowCommand RegisterOverflow(FrameworkElement holder,string iconKey,Func<FrameworkElement> icon,Button click,FrameworkElement state,Func<object[]> subItems,Action action,string captionKey){
        var command=new OverflowCommand {Holder=holder,IconKey=iconKey,Icon=icon,Click=click,State=state==null?(FrameworkElement)click:state,SubItems=subItems,Action=action,CaptionKey=captionKey};
        if(holder!=null)overflowCommands[holder]=command;
        return command;
    }
    FrameworkElement OverflowColorIcon(){
        var swatch=new Border {Background=colorSwatch.Background};
        return Ui.ColorGlyph(swatch);
    }
    FrameworkElement OverflowListIcon(){return Ui.GridIcon(numberedList?"listNumber":"listBullet");}
    void OpenTableFromOverflow(){tablePickerAnchor=ribbonOverflow;OpenTablePicker();}
    void ScheduleToolbarOverflow(){
        if(toolbarOverflowClosed||toolbarOverflowQueued)return;
        toolbarOverflowQueued=true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{toolbarOverflowQueued=false;UpdateToolbarOverflow();}));
    }
    internal void ToolbarRefreshOverflow(){toolbarOverflowQueued=false;UpdateToolbarOverflow();}
    void UpdateToolbarOverflow(){
        if(toolbarOverflowClosed||ribbonOverflow==null)return;
        overflowPassCount++;
        if(ribbonOverflowUnits.Count==0)BuildRibbonOverflowUnits();
        /* the top row only grows a 언어 menu during the language setup, so the unit table is built on the
           first pass that has a real width - never on a pass that could run inside the constructor */
        if(topOverflowUnits.Count==0&&topBarHost!=null&&topBarHost.ActualWidth>0)BuildTopOverflowUnits();
        UpdateRibbonOverflow();
        UpdateTopOverflow();
        RefreshMenuBarSkin();
    }
    void BuildRibbonOverflowUnits(){
        int group=-1;
        foreach(object entry in formatBar.Children){
            var frame=entry as Border;if(frame==null)continue;
            var body=frame.Child as StackPanel;
            if(body==null){ribbonOverflowUnits.Add(new OverflowUnit {Element=frame,Divider=true,Group=-1});continue;}
            group++;ribbonOverflowGroups.Add(frame);
            foreach(UIElement child in body.Children){
                var element=child as FrameworkElement;if(element==null)continue;
                ribbonOverflowUnits.Add(new OverflowUnit {Element=element,Command=CommandFor(element),Group=group});
            }
        }
    }
    void BuildTopOverflowUnits(){
        var commands=quickFind.Parent as Panel;
        if(commands!=null)foreach(UIElement child in commands.Children){
            var element=child as FrameworkElement;if(element==null)continue;
            var line=element as Border;
            bool divider=line!=null&&line.Width<=1.5&&line.ActualHeight<=40;
            topOverflowUnits.Add(new OverflowUnit {Element=element,Command=CommandFor(element),Divider=divider,Group=0});
        }
        if(topMenu!=null)for(int i=0;i<topMenu.Items.Count;i++){
            var item=topMenu.Items[i] as MenuItem;if(item==null)continue;
            topOverflowUnits.Add(new OverflowUnit {Element=item,Menu=item,MenuIndex=i,Group=1});
        }
        if(outlineButton!=null)topOverflowUnits.Add(new OverflowUnit {Element=outlineButton,Command=CommandFor(outlineButton),Group=2});
    }
    /* A unit added after this table was written still becomes a menu entry (icon-less but reachable), so a
       later toolbar button can never be lost to a row that stays hidden. */
    OverflowCommand CommandFor(FrameworkElement element){
        OverflowCommand command;
        if(element!=null&&overflowCommands.TryGetValue(element,out command))return command;
        var button=element as Button;
        var combo=element as ComboBox;
        if(button==null&&combo==null&&!(element is ContentControl))return null;
        command=new OverflowCommand {Holder=element,Click=button,State=element};
        overflowCommands[element]=command;
        return command;
    }
    static void ShowOverflowUnits(List<OverflowUnit> units){foreach(var unit in units)if(unit.Menu==null)unit.Element.Visibility=Visibility.Visible;}
    static void MeasureOverflowUnits(List<OverflowUnit> units){foreach(var unit in units){var element=unit.Element;element.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));unit.Width=element.DesiredSize.Width;}/* DesiredSize already carries the element's own margin */}
    static double SumOverflowWidth(List<OverflowUnit> units){double sum=0;foreach(var unit in units)sum+=unit.Width;return sum;}
    static int FitOverflowUnits(List<OverflowUnit> units,double budget){
        double sum=0;int count=0;
        for(int i=0;i<units.Count;i++){if(sum+units[i].Width>budget+0.01)break;sum+=units[i].Width;count++;}
        while(count>0&&units[count-1].Divider)count--;
        return count;
    }
    /* b57: the » button's right margin is the cosmetic gap left after the last painted unit, and it also
       narrows the row's viewport. A pass that measured with the previous pass's margin therefore hid units
       the current width still fits, the next pass left an even wider gap, and a slow resize collapsed the
       row into » step by step. Every pass starts from the unshifted row; PlaceOverflowNearLastUnit derives
       the gap again at its end, so one pass per width is enough. */
    static void ResetOverflowOffset(Button button){if(button==null)return;Thickness margin=button.Margin;if(margin.Right!=0)button.Margin=new Thickness(margin.Left,margin.Top,0,margin.Bottom);}
    void UpdateRibbonOverflow(){
        if(ribbonHost.Visibility!=Visibility.Visible||ribbonHost.ActualWidth<=0){ResetOverflowOffset(ribbonOverflow);ribbonOverflow.Visibility=Visibility.Collapsed;return;}
        ResetOverflowOffset(ribbonOverflow);
        ShowOverflowUnits(ribbonOverflowUnits);
        for(int i=0;i<ribbonOverflowGroups.Count;i++)ribbonOverflowGroups[i].Visibility=Visibility.Visible;
        ribbonOverflow.Visibility=Visibility.Collapsed;
        double preview=0;
        if(ribbonPreviewHost.Visibility==Visibility.Visible){ribbonPreviewHost.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));preview=ribbonPreviewHost.DesiredSize.Width;}
        double margins=formatBar.Margin.Left+formatBar.Margin.Right;
        MeasureOverflowUnits(ribbonOverflowUnits);
        double width=ribbonHost.ActualWidth,total=margins+preview+SumOverflowWidth(ribbonOverflowUnits);
        int visible=ribbonOverflowUnits.Count;
        if(total>width+0.01){visible=OverflowRibbonTo(visible,width-preview-margins);}
        else if(width-total<2){ribbonScroll.UpdateLayout();if(ribbonScroll.ScrollableWidth>0.5)visible=OverflowRibbonTo(visible,width-preview-margins);}
        ribbonOverflowVisibleCount=visible;
        BuildRibbonOverflowMenu(visible);
        PlaceOverflowNearLastUnit(ribbonOverflow,ribbonHost,ribbonOverflowUnits,visible);
    }
    int OverflowRibbonTo(int visible,double budget){
        ribbonOverflow.Visibility=Visibility.Visible;
        visible=FitOverflowUnits(ribbonOverflowUnits,budget-ribbonOverflow.Width);
        ApplyRibbonOverflowVisibility(visible);
        ribbonScroll.UpdateLayout();
        int guard=0;
        while(visible>0&&ribbonScroll.ScrollableWidth>0.5&&guard++<ribbonOverflowUnits.Count+2){visible--;ApplyRibbonOverflowVisibility(visible);ribbonScroll.UpdateLayout();}
        if(ribbonScroll.ScrollableWidth>0.5){visible=0;ApplyRibbonOverflowVisibility(0);ribbonScroll.UpdateLayout();}
        return visible;
    }
    void ApplyRibbonOverflowVisibility(int visibleCount){
        for(int i=0;i<ribbonOverflowUnits.Count;i++)ribbonOverflowUnits[i].Element.Visibility=i<visibleCount?Visibility.Visible:Visibility.Collapsed;
        for(int group=0;group<ribbonOverflowGroups.Count;group++){
            bool any=false;
            for(int i=0;i<visibleCount&&!any;i++)if(ribbonOverflowUnits[i].Group==group)any=true;
            ribbonOverflowGroups[group].Visibility=any?Visibility.Visible:Visibility.Collapsed;
        }
    }
    void UpdateTopOverflow(){
        if(topOverflow==null)return;
        if(topBarHost==null||topBarHost.ActualWidth<=0){ResetOverflowOffset(topOverflow);topOverflow.Visibility=Visibility.Collapsed;return;}
        ResetOverflowOffset(topOverflow);
        RestoreTopOverflowMenus();
        ShowOverflowUnits(topOverflowUnits);
        topOverflow.Visibility=Visibility.Collapsed;
        double margins=topBarPanel==null?0:topBarPanel.Margin.Left+topBarPanel.Margin.Right;
        MeasureOverflowUnits(topOverflowUnits);
        double width=topBarHost.ActualWidth,total=margins+SumOverflowWidth(topOverflowUnits);
        int visible=topOverflowUnits.Count;
        if(total>width+0.01){visible=OverflowTopTo(visible,width,margins);}
        else if(width-total<2&&topBarScroll!=null){topBarScroll.UpdateLayout();if(topBarScroll.ScrollableWidth>0.5)visible=OverflowTopTo(visible,width,margins);}
        topOverflowVisibleCount=visible;
        BuildTopOverflowMenu(visible);
        PlaceOverflowNearLastUnit(topOverflow,topBarHost,topOverflowUnits,visible);
    }
    /* The rows stay left-packed, so the right-docked » button left a wide empty strip in front of
       it once units moved into its menu (user, 2026-10-29). A right-docked child moves left by its
       right margin, so the measured distance from the last painted unit is written there: the fill
       region takes the freed space and the 원문 toggle keeps the formatting row's right edge. The
       caller resets the margin to 0 before measuring (ResetOverflowOffset), so the distance below is
       the real gap from the last painted unit and one pass per width is enough. */
    void PlaceOverflowNearLastUnit(Button button,FrameworkElement host,List<OverflowUnit> units,int visible){
        if(button==null)return;
        double margin=0;
        int at=Math.Min(visible,units.Count)-1;
        while(at>=0&&units[at].Divider)at--;   /* a dangling divider is not a command to follow */
        if(host!=null&&at>=0&&button.Visibility==Visibility.Visible){
            var last=units[at].Element;
            if(last!=null&&last.Visibility==Visibility.Visible&&last.ActualWidth>0&&button.ActualWidth>0){
                try{
                    Rect box=button.TransformToAncestor(host).TransformBounds(new Rect(0,0,button.ActualWidth,button.ActualHeight));
                    Rect edge=last.TransformToAncestor(host).TransformBounds(new Rect(0,0,last.ActualWidth,last.ActualHeight));
                    margin=box.Left+button.Margin.Right-(edge.Right+last.Margin.Right+OverflowUnitGap);
                }catch(InvalidOperationException){margin=0;}catch(ArgumentException){margin=0;}
            }
        }
        if(margin<0.5)margin=0;
        Thickness current=button.Margin;
        if(Math.Abs(current.Right-margin)>0.5)button.Margin=new Thickness(current.Left,current.Top,margin,current.Bottom);
    }
    const double OverflowUnitGap=2;
    int OverflowTopTo(int visible,double width,double margins){
        topOverflow.Visibility=Visibility.Visible;
        double outline=0;int outlineIndex=-1;
        for(int i=0;i<topOverflowUnits.Count;i++)if(Object.ReferenceEquals(topOverflowUnits[i].Element,outlineButton)){outline=topOverflowUnits[i].Width;outlineIndex=i;}
        double budget=width-margins-topOverflow.Width;
        visible=FitOverflowUnits(topOverflowUnits,budget-outline);
        if(outlineIndex>=0&&visible<=outlineIndex)visible=FitOverflowUnits(topOverflowUnits,budget);
        ApplyTopOverflowVisibility(visible);
        if(topBarScroll!=null){
            topBarScroll.UpdateLayout();
            int guard=0;
            while(visible>0&&topBarScroll.ScrollableWidth>0.5&&guard++<topOverflowUnits.Count+2){visible--;ApplyTopOverflowVisibility(visible);topBarScroll.UpdateLayout();}
            if(topBarScroll.ScrollableWidth>0.5){visible=0;ApplyTopOverflowVisibility(0);topBarScroll.UpdateLayout();}
        }
        return visible;
    }
    void ApplyTopOverflowVisibility(int visibleCount){
        for(int i=0;i<topOverflowUnits.Count;i++){
            var unit=topOverflowUnits[i];bool visible=i<visibleCount;
            if(unit.Menu!=null){
                bool inMenu=topMenu.Items.IndexOf(unit.Menu)>=0;
                if(visible){if(!inMenu)InsertTopMenuItem(unit);}
                else if(inMenu)topMenu.Items.Remove(unit.Menu);
            }else unit.Element.Visibility=visible?Visibility.Visible:Visibility.Collapsed;
        }
    }
    void RestoreTopOverflowMenus(){
        for(int i=0;i<topOverflowUnits.Count;i++){
            var unit=topOverflowUnits[i];if(unit.Menu==null)continue;
            if(topOverflowMenu!=null&&topOverflowMenu.Items.IndexOf(unit.Menu)>=0)topOverflowMenu.Items.Remove(unit.Menu);
            if(topMenu.Items.IndexOf(unit.Menu)<0)InsertTopMenuItem(unit);
        }
    }
    void InsertTopMenuItem(OverflowUnit unit){
        int at=unit.MenuIndex;if(at>topMenu.Items.Count)at=topMenu.Items.Count;if(at<0)at=0;
        topMenu.Items.Insert(at,unit.Menu);
        /* b56: while it sat in the "»" list the bar item wore the submenu skin (ApplyMenuSkin themes the
           popup's entries). A wider window puts it back before RefreshMenuBarSkin, which returns early
           whenever the theme flag already matches, so the item stayed a submenu row on a light bar.
           Re-apply the bar shape here, on the move itself, whatever the flag says. */
        ThemeMenuEntry(unit.Menu,true);
    }
    void BuildRibbonOverflowMenu(int visibleCount){
        var menu=ribbonOverflowMenu;if(menu==null)return;
        menu.Items.Clear();
        int lastGroup=-2;
        for(int i=visibleCount;i<ribbonOverflowUnits.Count;i++){
            var unit=ribbonOverflowUnits[i];
            if(unit.Divider||unit.Command==null)continue;
            if(lastGroup!=-2&&unit.Group!=lastGroup)menu.Items.Add(new Separator());
            menu.Items.Add(OverflowMenuItem(unit.Command));
            lastGroup=unit.Group;
        }
        Ui.ToolbarText(menu,FontFamily);TranslateTree(menu);
    }
    void BuildTopOverflowMenu(int visibleCount){
        var menu=topOverflowMenu;if(menu==null)return;
        menu.Items.Clear();
        int lastKind=-1;
        for(int i=visibleCount;i<topOverflowUnits.Count;i++){
            var unit=topOverflowUnits[i];
            if(unit.Divider)continue;
            if(unit.Menu==null&&unit.Command==null)continue;
            int kind=unit.Menu==null?0:1;
            if(lastKind>=0&&kind!=lastKind)menu.Items.Add(new Separator());
            if(unit.Menu!=null){menu.Items.Add(unit.Menu);HookOverflowSubmenu(unit.Menu);}
            else menu.Items.Add(OverflowMenuItem(unit.Command));
            lastKind=kind;
        }
        Ui.ToolbarText(menu,FontFamily);TranslateTree(menu);
    }
    void OpenOverflowMenu(Button button,ContextMenu menu){
        /* b57: the » list is a ContextMenu and takes the keyboard focus, so the Alt+Down path (and any
           programmatic open) must commit a live IME composition before the popup appears; a mouse click
           has already asked for it in the window's PreviewMouseDown. */
        CommitCompositionForMenu("toolbar overflow");
        if(Object.ReferenceEquals(button,ribbonOverflow))BuildRibbonOverflowMenu(ribbonOverflowVisibleCount);
        else BuildTopOverflowMenu(topOverflowVisibleCount);
        RefreshOverflowStates(menu);
        // The list hangs from the button's right edge and grows leftwards over the row it came
        // from, so it opens completely inside the window (user, 2026-10-29: it was left-aligned on
        // the button and ran past the window's right edge).
        menu.PlacementTarget=button;menu.Placement=PlacementMode.Custom;
        ApplyMenuSkin(menu);/* X29: theme before the popup is realised so the list never paints light first */
        menu.HorizontalOffset=0;menu.VerticalOffset=0;menu.PlacementRectangle=Rect.Empty;
        FrameworkElement anchor=button;
        menu.CustomPopupPlacementCallback=delegate(Size popup,Size target,Point offset){return OverflowPlacements(anchor,ClientWidth(),AnchorLeft(anchor),popup,target);};
        menu.IsOpen=true;
    }
    /* Right-aligned popup for a » menu: the popup's right edge meets the button's right edge, and
       the menu is clamped into the window so a button near the right end cannot push it outside.
       anchorLeft<0 (no realised window, the windowless toolbar shot) or windowWidth<=0 skips the
       clamp and keeps the plain right alignment. */
    CustomPopupPlacement[] OverflowPlacements(FrameworkElement anchor,double windowWidth,double anchorLeft,Size popup,Size target){
        double x=target.Width-popup.Width;
        if(windowWidth>0&&anchorLeft>=0){
            double minX=-anchorLeft,maxX=windowWidth-anchorLeft-popup.Width;
            if(x>maxX)x=maxX;if(x<minX)x=minX;
        }
        return new CustomPopupPlacement[]{new CustomPopupPlacement(new Point(x,target.Height),PopupPrimaryAxis.Horizontal),new CustomPopupPlacement(new Point(x,-popup.Height),PopupPrimaryAxis.Horizontal)};
    }
    // The window's content width in the same space the popup offset is measured in.
    double ClientWidth(){
        var content=Content as FrameworkElement;
        if(content!=null&&content.ActualWidth>0)return content.ActualWidth;
        return ActualWidth;
    }
    // The anchor's left edge from the window's content origin; -1 when it cannot be resolved.
    double AnchorLeft(FrameworkElement anchor){
        if(anchor==null)return -1;
        try{return anchor.TransformToAncestor(this).Transform(new Point(0,0)).X;}
        catch(InvalidOperationException){return -1;}catch(ArgumentException){return -1;}
    }
    /* Probe surface: the placement the real popup callback would return for a synthetic menu. */
    internal CustomPopupPlacement[] ToolbarOverflowPlacement(bool ribbon,double windowWidth,double anchorLeft,Size popup){
        var button=ribbon?ribbonOverflow:topOverflow;
        if(button==null)return new CustomPopupPlacement[0];
        return OverflowPlacements(button,windowWidth,anchorLeft,popup,new Size(button.ActualWidth,button.ActualHeight));
    }
    /* Probe surface: the unit whose button the » button follows (a dangling divider does not count). */
    internal FrameworkElement ToolbarOverflowLastVisible(bool ribbon){
        var units=ribbon?ribbonOverflowUnits:topOverflowUnits;
        int count=ribbon?ribbonOverflowVisibleCount:topOverflowVisibleCount;
        if(units.Count==0||count<=0)return null;
        int at=Math.Min(count,units.Count)-1;
        while(at>=0&&units[at].Divider)at--;
        return at<0?null:units[at].Element;
    }
    MenuItem OverflowMenuItem(OverflowCommand command){
        var item=new MenuItem();
        string caption=OverflowCaption(command);
        item.Header=new TextBlock {Text=caption,VerticalAlignment=VerticalAlignment.Center};
        item.ToolTip=OverflowTooltip(command,caption);
        item.Tag=command;
        item.IsChecked=OverflowChecked(command.State);
        item.IsEnabled=command.State==null||command.State.IsEnabled;
        System.Windows.Automation.AutomationProperties.SetName(item,caption);
        if(command.SubItems!=null){
            object[] subItems=command.SubItems();
            if(subItems!=null)foreach(object sub in subItems)item.Items.Add(sub);
        }else item.Click+=delegate{InvokeOverflowCommand(command);};
        FrameworkElement icon=command.Icon!=null?command.Icon():(command.IconKey==null?null:Ui.GridIcon(command.IconKey));
        if(icon!=null)item.Icon=OverflowIconHost(icon);
        if(command.SubItems!=null)HookOverflowSubmenu(item);
        return item;
    }
    /* The cloned glyph keeps its ink binding to the nearest ButtonBase, so it is hosted in a chrome-less,
       hit-test-invisible button that follows the menu item's Foreground (the menu is light in both themes). */
    FrameworkElement OverflowIconHost(FrameworkElement icon){
        var host=new Button {Content=icon,MinWidth=17,MinHeight=17,Padding=new Thickness(0),Margin=new Thickness(0),ClipToBounds=false,Focusable=false,IsHitTestVisible=false,IsTabStop=false,Background=Brushes.Transparent,BorderThickness=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};
        host.SetBinding(Control.ForegroundProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(MenuItem),1)});
        return host;
    }
    string OverflowCaption(OverflowCommand command){
        if(command.CaptionKey!=null){string text=T(command.CaptionKey);if(!String.IsNullOrEmpty(text))return text;}
        if(command.Click!=null){
            var row=command.Click.Content as StackPanel;
            if(row!=null)foreach(UIElement child in row.Children){var block=child as TextBlock;if(block!=null&&!String.IsNullOrEmpty(block.Text))return block.Text;}
            var single=command.Click.Content as TextBlock;
            if(single!=null&&!String.IsNullOrEmpty(single.Text))return single.Text;
        }
        string tip=OverflowSourceTip(command);
        return tip==null?"":tip;
    }
    string OverflowSourceTip(OverflowCommand command){
        string tip=command.Click==null?null:Convert.ToString(command.Click.ToolTip);
        if(String.IsNullOrEmpty(tip)&&command.Holder!=null)tip=Convert.ToString(command.Holder.ToolTip);
        if(String.IsNullOrEmpty(tip)&&command.State!=null)tip=Convert.ToString(command.State.ToolTip);
        return tip;
    }
    string OverflowTooltip(OverflowCommand command,string caption){
        string tip=OverflowSourceTip(command);
        return String.IsNullOrEmpty(tip)?caption:tip;
    }
    bool OverflowChecked(FrameworkElement state){
        var toggle=state as ToggleButton;
        if(toggle!=null)return toggle.IsChecked==true;
        var control=state as Control;
        return control!=null&&Object.Equals(control.Tag,"Active");
    }
    void RefreshOverflowStates(ContextMenu menu){
        if(menu==null)return;
        foreach(object entry in menu.Items){
            var item=entry as MenuItem;if(item==null)continue;
            var command=item.Tag as OverflowCommand;if(command==null||command.State==null)continue;
            item.IsEnabled=command.State.IsEnabled;
            item.IsChecked=OverflowChecked(command.State);
        }
    }
    void InvokeOverflowCommand(OverflowCommand command){
        if(command.Action!=null){command.Action();return;}
        if(command.Click!=null&&command.Click.IsEnabled)command.Click.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
    object[] HeadingOverflowItems(){
        var items=new List<object>();
        var apply=new MenuItem {Header=T("마지막 선택 제목 적용")};apply.Click+=delegate{SetHeading(lastHeadingLevel);};items.Add(apply);
        items.Add(new Separator());
        var body=new MenuItem {Header=T("본문")};body.Click+=delegate{SetHeading(0);};items.Add(body);
        for(int i=1;i<=6;i++){
            int level=i;var item=new MenuItem {Header=T("제목")+" "+level};
            item.Click+=delegate{lastHeadingLevel=level;UpdateHeadingCaption();SavePreferences();SetHeading(level);};
            items.Add(item);
        }
        return items.ToArray();
    }
    object[] ListOverflowItems(){
        var items=new List<object>();
        var apply=new MenuItem {Header=T("마지막 선택 목록 적용")};apply.Click+=delegate{ApplyLastList();};items.Add(apply);
        items.Add(new Separator());
        var bullet=new MenuItem {Header="· "+T("글머리 기호 목록")};bullet.Click+=delegate{numberedList=false;UpdateListCaption();SavePreferences();ApplyLastList();};items.Add(bullet);
        var number=new MenuItem {Header="1. "+T("번호 목록")};number.Click+=delegate{numberedList=true;UpdateListCaption();SavePreferences();ApplyLastList();};items.Add(number);
        return items.ToArray();
    }
    object[] SizesOverflowItems(){
        var items=new List<object>();
        foreach(object entry in sizes.Items){
            string text=Convert.ToString(entry);double value;
            if(!Double.TryParse(text.Replace(" px",""),NumberStyles.Float,CultureInfo.InvariantCulture,out value))continue;
            string choice=text;var item=new MenuItem {Header=choice};
            item.Click+=delegate{ApplySizeChoice(choice);};
            items.Add(item);
        }
        return items.ToArray();
    }
    object[] ColorOverflowItems(){
        var items=new List<object>();
        var apply=new MenuItem {Header=T("마지막 글자색 적용")};apply.Click+=delegate{if(lastColorNone)ClearTextColor();else SetTextColor(lastTextColor);};items.Add(apply);
        items.Add(new Separator());
        var none=new MenuItem {Header=T("색 없음 (기본색)")};none.Click+=delegate{ChooseNoColor();CloseOverflowMenus();};items.Add(none);
        var colors=new UniformGrid {Columns=8,Margin=new Thickness(6)};
        foreach(string value in TextColorValues){
            Color selected=(Color)ColorConverter.ConvertFromString(value);Color picked=selected;
            var cell=new Button {Width=25,Height=25,Padding=new Thickness(3),Margin=new Thickness(0),Focusable=false,ToolTip=value,Content=new Border {Width=17,Height=17,Background=new SolidColorBrush(selected),BorderBrush=Brushes.LightGray,BorderThickness=new Thickness(.5)}};
            cell.Click+=delegate{ChooseTextColor(picked);CloseOverflowMenus();};
            colors.Children.Add(cell);
        }
        items.Add(new MenuItem {Header=colors,StaysOpenOnClick=true});
        var custom=new MenuItem {Header=T("다른 색…")};custom.Click+=delegate{CloseOverflowMenus();ChooseCustomTextColor();};items.Add(custom);
        return items.ToArray();
    }
    void CloseOverflowMenus(){
        if(ribbonOverflowMenu!=null)ribbonOverflowMenu.IsOpen=false;
        if(topOverflowMenu!=null)topOverflowMenu.IsOpen=false;
    }
    /* b56: the table size grid is the one popup that paints a wide panel, so it follows the app's own popup
       surface (Paper / Line / Ink) instead of the system menu chrome, which stays light grey in dark mode
       (user, 2026-10-30). One template for both themes keeps light and dark the same shape. */
    void ThemePickerMenu(ContextMenu menu){
        if(!dark)return;/* light keeps the system menu chrome every dropdown shares */
        menu.Template=PickerSurfaceTemplate();
        menu.Background=(Brush)Resources["Paper"];menu.BorderBrush=(Brush)Resources["Line"];menu.BorderThickness=new Thickness(1);menu.Padding=new Thickness(4);
        menu.Foreground=(Brush)Resources["Ink"];menu.HasDropShadow=false;
    }
    static ControlTemplate pickerSurfaceTemplate;
    static ControlTemplate PickerSurfaceTemplate(){
        if(pickerSurfaceTemplate==null){
            string xaml="<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContextMenu'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='5' Padding='{TemplateBinding Padding}'><ItemsPresenter KeyboardNavigation.DirectionalNavigation='Cycle' KeyboardNavigation.TabNavigation='Cycle'/></Border></ControlTemplate>";
            pickerSurfaceTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }
        return pickerSurfaceTemplate;
    }
    void CloseTablePicker(bool focusBody){
        if(tablePickerMenu!=null)tablePickerMenu.IsOpen=false;
        tablePickerHoverOpened=false;CancelTablePickerClose();
        if(focusBody){tablePickerEscClosed=true;if(source!=null)source.Focus();}
    }
    /* b56: a top-row menu moved into the "»" list keeps its template-driven submenu placement (Right /
       Bottom), which only the screen edge constrains; in a 450 px window 파일/편집 unfolded past the window's
       right edge (user, 2026-10-30). Once the submenu popup is open it is shifted left by exactly the
       overhang, so a submenu that already fits keeps the placement the template chose. */
    /* b57: a bar item that was parked in the "»" list is the same MenuItem when a wider window puts it back
       on the bar, and the handlers installed by HookOverflowSubmenu stay on it. The clamp and the below-item
       drop only make sense for the cascading placement inside the » ContextMenu; on the bar the template's
       own placement must stand, and the offsets recorded while parked must not follow the item back. The two
       overflow collections are the truth: Build*OverflowMenu adds an entry when it is parked, and
       RestoreTopOverflowMenus takes it out when it returns to the bar. */
    bool InOverflowList(MenuItem item){
        if(item==null)return false;
        if(ribbonOverflowMenu!=null&&ribbonOverflowMenu.Items.IndexOf(item)>=0)return true;
        return topOverflowMenu!=null&&topOverflowMenu.Items.IndexOf(item)>=0;
    }
    void HookOverflowSubmenu(MenuItem item){
        if(item==null)return;
        object marked;
        if(overflowSubmenuHooked.TryGetValue(item,out marked))return;
        overflowSubmenuHooked.Add(item,item);
        item.SubmenuOpened+=delegate{
            var popup=OverflowSubmenuPopup(item);
            if(popup==null)return;
            OverflowSubmenuState state;
            bool known=overflowSubmenuStates.TryGetValue(popup,out state);
            if(!InOverflowList(item)){
                /* back on the bar (the popup outlives the move): put the template's own offsets back */
                if(known){popup.HorizontalOffset=state.BaseOffset;popup.VerticalOffset=state.BaseVertical;}
                return;
            }
            if(!known){
                state=new OverflowSubmenuState {BaseOffset=popup.HorizontalOffset,BaseVertical=popup.VerticalOffset,Item=item};
                overflowSubmenuStates.Add(popup,state);
                popup.Opened+=delegate{ClampOverflowSubmenu(popup,state);};
            }
        };
    }
    static Popup OverflowSubmenuPopup(MenuItem item){
        try{return item.Template==null?null:item.Template.FindName("PART_Popup",item) as Popup;}catch(InvalidOperationException){return null;}
    }
    void ClampOverflowSubmenu(Popup popup,OverflowSubmenuState state){
        if(popup==null||state==null||!popup.IsOpen)return;
        if(!InOverflowList(state.Item))return;
        try{
            var child=popup.Child as FrameworkElement;if(child==null||child.ActualWidth<=0)return;
            /* 2.0 (user, 11-01): the submenu opens to the right of its » entry like any cascading menu, even past
               the window's right edge; only the screen edge flips it (the template's own placement). The b56
               shift into the window is gone, and the base offset is put back so no earlier shift lingers. */
            popup.HorizontalOffset=state.BaseOffset;
            /* X29: a submenu wider than the space beside its item is pushed back into the window and then
               covers the very item that was clicked; the click's release landed on its first entry and ran
               "새로 만들기" (user, 2026-10-30). The drop is queued after the horizontal pass because only
               once the popup has really moved can its rectangle be compared with the item's. */
            QueueOverflowSubmenuDrop(popup,state);
        }catch(InvalidOperationException){}catch(ArgumentException){}
    }
    void QueueOverflowSubmenuDrop(Popup popup,OverflowSubmenuState state){
        if(state.DropQueued)return;
        state.DropQueued=true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{
            state.DropQueued=false;
            DropOverflowSubmenuBelowItem(popup,state);
        }));
    }
    void DropOverflowSubmenuBelowItem(Popup popup,OverflowSubmenuState state){
        if(popup==null||state==null||!popup.IsOpen||state.Item==null)return;
        if(!InOverflowList(state.Item))return;
        try{
            var child=popup.Child as FrameworkElement;if(child==null||child.ActualWidth<=0)return;
            Rect box=new Rect(child.PointToScreen(new Point(0,0)),child.PointToScreen(new Point(child.ActualWidth,child.ActualHeight)));
            MenuItem item=state.Item;
            Rect itemBox=new Rect(item.PointToScreen(new Point(0,0)),item.PointToScreen(new Point(item.ActualWidth,item.ActualHeight)));
            if(!box.IntersectsWith(itemBox))return;
            Rect windowBox=new Rect(PointToScreen(new Point(0,0)),PointToScreen(new Point(ActualWidth,ActualHeight)));
            double vertical=itemBox.Bottom+2-box.Top;
            if(box.Top+vertical+box.Height>windowBox.Bottom-2)vertical=itemBox.Top-2-box.Bottom;
            if(box.Top+vertical<windowBox.Top+2)vertical=windowBox.Top+2-box.Top;
            var presentation=PresentationSource.FromVisual(this);
            if(presentation!=null&&presentation.CompositionTarget!=null)vertical=presentation.CompositionTarget.TransformFromDevice.Transform(new Point(0,vertical)).Y;
            popup.VerticalOffset=state.BaseVertical+vertical;
        }catch(InvalidOperationException){}catch(ArgumentException){}
    }
    /* ---- probe surface (ToolbarShot reads the overflow without a window or a popup) ---- */
    internal FrameworkElement[] ToolbarOverflowSources(bool ribbon){
        var sources=new List<FrameworkElement>();
        if(ribbon){foreach(var unit in ribbonOverflowUnits)if(unit.Command!=null&&unit.Command.Holder!=null)sources.Add(unit.Command.Holder);}
        else{foreach(var unit in topOverflowUnits)if((unit.Command!=null&&unit.Command.Holder!=null)||unit.Menu!=null)sources.Add(unit.Element);}
        return sources.ToArray();
    }
    internal bool ToolbarOverflowShown(bool ribbon){
        var button=ribbon?ribbonOverflow:topOverflow;
        return button!=null&&button.Visibility==Visibility.Visible&&button.ActualWidth>0;
    }
    internal bool ToolbarOverflowMenuHas(bool ribbon,FrameworkElement source){
        var menu=ribbon?ribbonOverflowMenu:topOverflowMenu;if(menu==null||source==null)return false;
        foreach(object entry in menu.Items){var item=entry as MenuItem;if(item==null)continue;var command=item.Tag as OverflowCommand;if(command!=null&&Object.ReferenceEquals(command.Holder,source))return true;if(Object.ReferenceEquals(item,source))return true;}
        return false;
    }
    internal string[] ToolbarOverflowCaptions(bool ribbon){
        var menu=ribbon?ribbonOverflowMenu:topOverflowMenu;var captions=new List<string>();
        if(menu==null)return captions.ToArray();
        foreach(object entry in menu.Items){
            var item=entry as MenuItem;if(item==null)continue;
            var block=item.Header as TextBlock;
            if(block!=null){captions.Add(block.Text);continue;}
            var row=item.Header as StackPanel;
            if(row!=null){foreach(UIElement child in row.Children){var text=child as TextBlock;if(text!=null){captions.Add(text.Text);break;}}continue;}
            var header=item.Header as string;
            if(header!=null)captions.Add(header.Replace("_",""));
        }
        return captions.ToArray();
    }
    internal string ToolbarOverflowLayout(bool ribbon){
        var units=ribbon?ribbonOverflowUnits:topOverflowUnits;var text=new StringBuilder();
        text.Append("k=").Append(ribbon?ribbonOverflowVisibleCount:topOverflowVisibleCount).Append(" host=").Append(ribbon?ribbonHost.ActualWidth:topBarHost==null?0:topBarHost.ActualWidth);
        foreach(var unit in units)text.Append(" [").Append(unit.Divider?"|":unit.Menu!=null?"M":unit.Command==null?"-":"c").Append(" ").Append(unit.Width.ToString("0.0",CultureInfo.InvariantCulture)).Append("]");
        return text.ToString();
    }
}}
