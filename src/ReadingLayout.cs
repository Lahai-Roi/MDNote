using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
namespace MDNote {
public partial class MainWindow {
    double linePercent=150;
    double customLinePercent=175;
    readonly Dictionary<int,MenuItem> linePresets=new Dictionary<int,MenuItem>();
    MenuItem customLine,liveModeMenu;
    void BuildLineMenu(MenuItem view){
        var menu=new MenuItem {Header="줄 간격"};view.Items.Add(menu);
        foreach(int value in new[]{100,125,150,200}){int percent=value;var item=new MenuItem {Header=percent+"%",IsCheckable=true};item.Click+=delegate{SetLinePercent(percent);};linePresets.Add(percent,item);menu.Items.Add(item);}
        menu.Items.Add(new Separator());customLine=new MenuItem {IsCheckable=true};customLine.Click+=delegate{string value=Input(T("줄 간격 (%)"),customLinePercent.ToString("0.##",CultureInfo.CurrentCulture));double percent;if(value!=null){if(TryLinePercent(value,out percent))SetCustomLinePercent(percent);else MessageBox.Show(this,T("50~400% 사이의 숫자를 입력해 주세요."),T("줄 간격"));}UpdateLineMenu();};menu.Items.Add(customLine);UpdateLineMenu();
    }
    /* Basic mode keeps every line formatted; Live view reveals the caret and selection lines. Both use
       the same editor and buffer. 11-01 user: the item sits with the other display toggles (다크 모드 /
       집중 모드 / 타자기 모드), not under 줄 간격, and keeps the name the user knows ("라이브 보기").
       The panel toggles (목차 / 제목 단계 표시 / 자동 줄바꿈) form their own group above (user, 2026-11-09). */
    void BuildLiveViewItem(MenuItem view){
        liveModeMenu=new MenuItem {Header="Markdown 라이브 미리보기",IsCheckable=true,IsChecked=!hideMarkers,ToolTip="커서가 있는 줄만 원문으로 보여 줍니다."};
        liveModeMenu.Click+=delegate{SetLiveMode(liveModeMenu.IsChecked);};
        view.Items.Add(liveModeMenu);
        view.SubmenuOpened+=delegate{UpdateLiveModeMenu();UpdateHeadingMarksMenu();};
    }
    /* user 2026-11-09: 보기 > 제목 단계 표시 sits in the panel group with 목차 and 자동 줄바꿈, and is built
       like the 집중 모드 row (FocusMode.cs BuildFocusMenu): the row itself toggles (on with the last style,
       or off) and its submenu picks 슬래시 막대 / # 기호 / 끄기. The parent shows the check mark while a style
       is on. The three choices carry a HeadingStyleChoice tag and are translated by TranslateFocusChoice
       ("끄기" is also other labels' key territory), so TranslateTree skips them. */
    MenuItem headingMarksMenu;
    readonly MenuItem[] headingMarkChoices=new MenuItem[3];
    sealed class HeadingStyleChoice {internal readonly int Style;internal HeadingStyleChoice(int style){Style=style;}}
    static readonly string[] HeadingStyleNames={"슬래시 막대","# 기호","끄기"};
    static readonly string[] HeadingStyleKeys={"슬래시 막대","# 기호","집중 모드: 끄기"};
    void BuildHeadingMarksMenu(MenuItem view){
        headingMarksMenu=new MenuItem {Header="제목 단계 표시",IsChecked=headingMarkStyle!=2};
        for(int style=0;style<3;style++){
            int chosen=style;var item=new MenuItem {Header=HeadingStyleNames[style],IsCheckable=true,IsChecked=headingMarkStyle==style,Tag=new HeadingStyleChoice(style)};
            item.Click+=delegate{SetHeadingMarkStyle(chosen);};
            headingMarkChoices[style]=item;headingMarksMenu.Items.Add(item);
        }
        headingMarksMenu.PreviewMouseLeftButtonUp+=delegate(object sender,System.Windows.Input.MouseButtonEventArgs e){
            if(!Object.ReferenceEquals(ClickedMenuItem(e.OriginalSource as DependencyObject),headingMarksMenu))return;
            e.Handled=true;ToggleHeadingMarksFromHeader();
            MenuItem top=TopMenuItem(headingMarksMenu);if(top!=null)top.IsSubmenuOpen=false;
        };
        view.Items.Add(headingMarksMenu);
    }
    string HeadingStyleText(int style){return language=="ko"?HeadingStyleNames[style]:T(HeadingStyleKeys[style]);}
    // Called by TranslateFocusChoice: true when the element is one of the three style choices.
    bool TranslateHeadingStyleChoice(DependencyObject element){
        var item=element as MenuItem;if(item==null)return false;
        var choice=item.Tag as HeadingStyleChoice;if(choice==null)return false;
        item.Header=HeadingStyleText(choice.Style);return true;
    }
    internal static bool TryLinePercent(string value,out double percent){string number=(value??"").Trim().TrimEnd('%').Trim();bool parsed=Double.TryParse(number,NumberStyles.Float,CultureInfo.CurrentCulture,out percent)||Double.TryParse(number,NumberStyles.Float,CultureInfo.InvariantCulture,out percent);return parsed&&!Double.IsNaN(percent)&&!Double.IsInfinity(percent)&&percent>=50&&percent<=400;}
    void UpdateLineMenu(){foreach(var pair in linePresets)pair.Value.IsChecked=Math.Abs(pair.Key-linePercent)<.001;if(customLine!=null){customLine.Header=T("사용자 지정")+" ("+customLinePercent.ToString("0.##",CultureInfo.CurrentCulture)+"%)";customLine.IsChecked=!linePresets.ContainsKey((int)linePercent)||Math.Abs(linePercent-(int)linePercent)>.001;}}
    void SetCustomLinePercent(double value){if(Double.IsNaN(value)||Double.IsInfinity(value)||value<50||value>400)return;customLinePercent=value;SetLinePercent(value);}
    // Line spacing re-lays the whole view, the same view-level write the base font and the theme
    // queue for: under a live composition the value waits for the store release. A second press in
    // the same composition replaces the wanted value instead of queueing again.
    bool linePercentQueued;double linePercentWanted;
    void SetLinePercent(double value){
        if(Double.IsNaN(value)||Double.IsInfinity(value)||value<50||value>400)return;
        if(StoreGuardLive()){linePercentWanted=value;if(linePercentQueued)return;linePercentQueued=true;source.RunAfterComposition(delegate{linePercentQueued=false;SetLinePercent(linePercentWanted);});return;}
        SetLinePercentNow(value);
    }
    /* b56: line spacing re-lays the whole view. It used to jump to the caret on every change (the
       caret is put a third of the viewport down), so a reader who had scrolled the mouse away from
       the caret lost the place. The base-font path keeps the reading anchor; this follows it and
       re-centres only when the caret's own line was on screen, which keeps the RC9 distant-caret
       rule (the caret line must stay visible when spacing changes under it). */
    void SetLinePercentNow(double value){linePercent=value;UpdateLineMenu();bool keepCaret=CaretLineOnScreen();var anchor=keepCaret?null:CaptureReadingAnchor();ApplySourceLines();UpdateLivePreview();SavePreferences();if(keepCaret)PositionCaretInView();else RestoreReadingAnchor(anchor);QueueSettingsHorizontalClamp();}
    void ApplySourceLines(){if(!LivePreviewAttached)source.TextArea.TextView.LineHeight=fontSize*linePercent/100;TextBlock.SetLineHeight(source,fontSize*linePercent/100);TextBlock.SetLineStackingStrategy(source,LineStackingStrategy.BlockLineHeight);}
}
}
