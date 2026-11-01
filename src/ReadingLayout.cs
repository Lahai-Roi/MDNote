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
       the same editor and buffer. 11-01 user: the item sits with the other display toggles (under
       자동 줄바꿈), not under 줄 간격, and keeps the name the user knows ("라이브 보기"). */
    void BuildLiveViewItem(MenuItem view){
        liveModeMenu=new MenuItem {Header="Markdown 라이브 미리보기",IsCheckable=true,IsChecked=!hideMarkers,ToolTip="커서가 있는 줄만 원문으로 보여 줍니다."};
        liveModeMenu.Click+=delegate{SetLiveMode(liveModeMenu.IsChecked);};
        view.Items.Add(liveModeMenu);view.SubmenuOpened+=delegate{UpdateLiveModeMenu();};
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
