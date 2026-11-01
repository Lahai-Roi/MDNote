using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Forms=System.Windows.Forms;
namespace MDNote {
public partial class MainWindow {
    Button colorMain,colorArrow;
    Color lastTextColor=Colors.Red;
    bool lastColorNone;
    /* b58: an F-key can open the palette while the keyboard is still in the body. The swatches are
       Focusable=false and the popup never takes the keyboard, so the window's key handler walks this
       list (arrows/Home/End), Enter applies and Escape closes and gives the body back the keyboard.
       The mouse path never enters this mode and behaves exactly as before. */
    readonly List<Border> colorSwatchFaces=new List<Border>();
    bool colorKeyboardMode;int colorKeyboardIndex;
    readonly Border colorSwatch=new Border {Height=2,VerticalAlignment=VerticalAlignment.Bottom,Background=Brushes.Red};
    /* b56: one palette list for the text-colour popup and the toolbar overflow menu, so a narrow row can
       still reach every swatch (the popup is anchored to a button that may be hidden). */
    internal static readonly string[] TextColorValues={"#000000","#4B5563","#9CA3AF","#FFFFFF","#FF0000","#F97316","#FACC15","#22C55E","#14B8A6","#06B6D4","#3B82F6","#2563EB","#7C3AED","#C026D3","#EC4899","#92400E","#7F1D1D","#9A3412","#854D0E","#14532D","#134E4A","#164E63","#1E3A8A","#581C87","#FECACA","#FED7AA","#FEF08A","#BBF7D0","#99F6E4","#A5F3FC","#BFDBFE","#DDD6FE"};
    readonly Popup colorPopup=new Popup {StaysOpen=false,AllowsTransparency=true,Placement=PlacementMode.Bottom};
    void BuildColorControl(Panel group){
        var main=colorMain=new Button {Width=16,Focusable=false,ToolTip=T("마지막 글자색 적용")};
        main.Content=Ui.ColorGlyph(colorSwatch);main.Click+=delegate{if(lastColorNone)ClearTextColor();else SetTextColor(lastTextColor);};
        var arrow=colorArrow=new Button {Focusable=false,ToolTip=T("글자색 선택")};var split=colorHover=Ui.SplitButton(main,arrow,28);/* 2026-10-28 user: the palette opens under the whole A ▾ button, not under its arrow. */Ui.Dropdown(split,colorPopup);
        var panel=new StackPanel {Margin=new Thickness(8)};panel.Children.Add(new TextBlock {Text=T("글자색"),FontWeight=FontWeights.SemiBold,Margin=new Thickness(3,0,0,7)});
        var none=new Button {Content=T("색 없음 (기본색)"),HorizontalContentAlignment=HorizontalAlignment.Left,Focusable=false,Margin=new Thickness(0,0,0,5)};none.Click+=delegate{ChooseNoColor();};panel.Children.Add(none);
        var colors=new UniformGrid {Columns=8};panel.Children.Add(colors);
        foreach(string value in TextColorValues){Color selected=(Color)ColorConverter.ConvertFromString(value);var button=new Button {Width=25,Height=25,Padding=new Thickness(3),Margin=new Thickness(0),Focusable=false,ToolTip=value};var face=new Border {Width=17,Height=17,Background=new SolidColorBrush(selected),BorderBrush=Brushes.LightGray,BorderThickness=new Thickness(.5)};button.Content=face;button.Click+=delegate{ChooseTextColor(selected);};colors.Children.Add(button);colorSwatchFaces.Add(face);}
        var custom=new Button {Content=T("다른 색…"),Margin=new Thickness(0,7,0,0),HorizontalContentAlignment=HorizontalAlignment.Left,Focusable=false};custom.Click+=delegate{colorPopup.IsOpen=false;ChooseCustomTextColor();};panel.Children.Add(custom);
        var surface=new Border {Child=panel,CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1)};surface.Resources.MergedDictionaries.Add(Resources);surface.SetResourceReference(Border.BackgroundProperty,"Paper");surface.SetResourceReference(Border.BorderBrushProperty,"Line");surface.SetResourceReference(TextBlock.ForegroundProperty,"Ink");colorPopup.Child=surface;arrow.Click+=delegate{if(LiveFormat){if(!colorPopup.IsOpen)colorKeyboardMode=false;colorPopup.IsOpen=!colorPopup.IsOpen;}};colorPopup.Closed+=delegate{colorKeyboardMode=false;ClearColorKeyboardSelection();};group.Children.Add(split);
        /* b56: Esc closes the palette. The swatches are Focusable=false and the popup never takes the
           keyboard, so the window is the only place that sees the key. Registered here, in the ribbon
           pass, the handler runs before the find bar's (BuildSearchBar subscribes later): an open
           palette closes first and a find bar that is also open keeps the same Esc for itself. */
        PreviewKeyDown+=ColorPopupEscape;
    }
    void ColorPopupEscape(object sender,KeyEventArgs e){
        if(e.Handled||!colorPopup.IsOpen)return;
        Key key=SearchKey(e.Key,e.ImeProcessedKey);
        if(key==Key.Escape){bool keyboard=colorKeyboardMode;colorPopup.IsOpen=false;e.Handled=true;/* b58: a palette opened by an F-key gives the body the keyboard back (a find bar that is also open keeps its own Escape, as before). The mode flag is read before the close, because Popup raises Closed synchronously. */if(keyboard&&searchHost.Visibility!=Visibility.Visible&&source!=null)source.Focus();return;}
        /* b58: keys for a palette the keyboard opened. The body keeps the keyboard, so these are read
           here; a field that owns the keyboard (find/replace box, size box) keeps its own keys. */
        if(!colorKeyboardMode||FieldOwnsFunctionKey(Keyboard.FocusedElement))return;
        int count=colorSwatchFaces.Count;
        if(count==0)return;
        int next=colorKeyboardIndex;
        if(key==Key.Left)next--;else if(key==Key.Right)next++;else if(key==Key.Up)next-=8;else if(key==Key.Down)next+=8;else if(key==Key.Home)next=0;else if(key==Key.End)next=count-1;else if(key==Key.Enter){ApplyColorKeyboardChoice();e.Handled=true;return;}else return;
        next=Math.Max(0,Math.Min(count-1,next));
        if(next!=colorKeyboardIndex){colorKeyboardIndex=next;ShowColorKeyboardSelection();}
        e.Handled=true;
    }
    /* b58: an F-key opens the palette exactly where the arrow opened it before (under the whole A ▾
       button) and puts a selection on the last used swatch; a second press closes it like a toggle. */
    void OpenColorPaletteFromKeyboard(){
        if(!LiveFormat)return;
        if(colorPopup.IsOpen){colorPopup.IsOpen=false;return;}
        colorKeyboardMode=true;colorKeyboardIndex=TextColorIndex(lastTextColor);colorPopup.IsOpen=true;ShowColorKeyboardSelection();
    }
    int TextColorIndex(Color color){
        for(int i=0;i<TextColorValues.Length;i++)if((Color)ColorConverter.ConvertFromString(TextColorValues[i])==color)return i;
        return 0;
    }
    void ShowColorKeyboardSelection(){
        Brush ink=(Brush)Resources["Ink"];
        for(int i=0;i<colorSwatchFaces.Count;i++){bool selected=i==colorKeyboardIndex;colorSwatchFaces[i].BorderBrush=selected?ink:Brushes.LightGray;colorSwatchFaces[i].BorderThickness=new Thickness(selected?2:0.5);}
    }
    void ClearColorKeyboardSelection(){foreach(Border face in colorSwatchFaces){face.BorderBrush=Brushes.LightGray;face.BorderThickness=new Thickness(0.5);}}
    void ApplyColorKeyboardChoice(){
        if(colorKeyboardIndex<0||colorKeyboardIndex>=TextColorValues.Length)return;
        ChooseTextColor((Color)ColorConverter.ConvertFromString(TextColorValues[colorKeyboardIndex]));
        if(source!=null)source.Focus();
    }
    /* b56: black is a real colour. It used to double as the "no colour" value, so the first palette
       swatch (#000000) and a pure black in the custom dialog cleared the colour span instead, and dark
       mode painted that text in the default ink. Only "색 없음 (기본색)" clears now. */
    void ChooseTextColor(Color color){lastTextColor=color;lastColorNone=false;colorSwatch.Background=new SolidColorBrush(color);colorPopup.IsOpen=false;SetTextColor(color);}
    void ChooseNoColor(){lastColorNone=true;lastTextColor=Colors.Black;colorSwatch.Background=Brushes.Black;colorPopup.IsOpen=false;ClearTextColor();}
    /* b56: the picker is shared by the palette popup and the toolbar overflow menu; the caller closes whichever
       surface opened it before the modal dialog takes the mouse. */
    internal void ChooseCustomTextColor(){
        using(var dialog=new Forms.ColorDialog()){
            dialog.Color=System.Drawing.Color.FromArgb(lastTextColor.R,lastTextColor.G,lastTextColor.B);
            if(dialog.ShowDialog()==Forms.DialogResult.OK)ChooseTextColor(Color.FromRgb(dialog.Color.R,dialog.Color.G,dialog.Color.B));
        }
    }
    /* The toggle swaps its caption (수정 <-> 수정중). The 78 px box is the Korean width the user confirmed and
       every Korean/Japanese/Chinese caption fits it, but English "Editing" is one pixel wider, so the whole
       command row (and the menu after it) moved by that pixel when the mode changed. Measure both captions
       with this button's own chrome and keep the widest: 78 px stays for the CJK captions (their two
       captions both fit) and English gets the one extra pixel in read mode as well. */
    void UpdateEditCaption(){
        string caption=editing?T("수정중"):T("수정");double widest=0;
        editButton.MinWidth=0;
        foreach(string text in new[]{T("수정"),T("수정중")}){
            editButton.Content=EditCaptionRow(text);
            editButton.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));
            widest=Math.Max(widest,editButton.DesiredSize.Width);
        }
        editButton.Content=EditCaptionRow(caption);
        editButton.MinWidth=Math.Max(78,widest);
    }
    static StackPanel EditCaptionRow(string caption){
        var row=new StackPanel {Orientation=Orientation.Horizontal};
        row.Children.Add(Ui.GridIcon("edit"));
        /* ToolbarText formats the command row with Display metrics; the probe must use the same
           rounding or a caption can measure fractional here and integral when it is really drawn. */
        TextOptions.SetTextFormattingMode(row,TextFormattingMode.Display);
        row.Children.Add(new TextBlock {Text=caption,Margin=new Thickness(4,0,0,0),VerticalAlignment=VerticalAlignment.Center});
        return row;
    }
}
}
