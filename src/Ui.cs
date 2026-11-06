using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Shapes;

namespace MDNote {
internal static class Ui {
    internal const string ChevronGeometry="M 2.5,3.3 L 6,8.3 L 9.5,3.3";
    internal static ResourceDictionary Styles() {
        string xaml=@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <!-- b51: every thumb radius is exactly half its painted thickness (4 on the 8 px bar, 5.5 on the 11 px hover bar, 5 on the 10 px horizontal hover bar), so both ends of the capsule are truly round. A radius larger than half the thickness is clipped by the border box, which made one end read flattened (user: one rounded end looks clipped). The bar is also thicker (axis 6 -> 8 px) and longer (min 24 -> 28 px) so it stops collapsing to a dot on a very long line. -->
 <ControlTemplate x:Key='ScrollThumb' TargetType='Thumb'><Grid Background='Transparent'><Border x:Name='T' Width='8' HorizontalAlignment='Right' CornerRadius='4' Margin='0,0,3,0' Background='{DynamicResource ScrollThumb}'/></Grid><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='T' Property='Background' Value='{DynamicResource ScrollThumbHover}'/></Trigger><Trigger Property='IsDragging' Value='True'><Setter TargetName='T' Property='Background' Value='{DynamicResource ScrollThumbHover}'/><Setter TargetName='T' Property='Width' Value='11'/><Setter TargetName='T' Property='CornerRadius' Value='5.5'/></Trigger><DataTrigger Binding='{Binding IsMouseOver,RelativeSource={RelativeSource AncestorType=ScrollBar}}' Value='True'><Setter TargetName='T' Property='Width' Value='11'/><Setter TargetName='T' Property='CornerRadius' Value='5.5'/></DataTrigger></ControlTemplate.Triggers></ControlTemplate>
 <ControlTemplate x:Key='ScrollThumbHorizontal' TargetType='Thumb'><Border x:Name='T' CornerRadius='4' Margin='1' Background='{DynamicResource ScrollThumb}'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='T' Property='Background' Value='{DynamicResource ScrollThumbHover}'/></Trigger><Trigger Property='IsDragging' Value='True'><Setter TargetName='T' Property='Background' Value='{DynamicResource ScrollThumbHover}'/><Setter TargetName='T' Property='Margin' Value='0'/><Setter TargetName='T' Property='CornerRadius' Value='5'/></Trigger><DataTrigger Binding='{Binding IsMouseOver,RelativeSource={RelativeSource AncestorType=ScrollBar}}' Value='True'><Setter TargetName='T' Property='Margin' Value='0'/><Setter TargetName='T' Property='CornerRadius' Value='5'/></DataTrigger></ControlTemplate.Triggers></ControlTemplate>
 <ControlTemplate x:Key='ScrollPage' TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate>
 <Style TargetType='ScrollBar'>
  <Setter Property='Background' Value='Transparent'/>
  <Style.Triggers>
   <Trigger Property='Orientation' Value='Vertical'>
    <Setter Property='Width' Value='14'/><Setter Property='MinWidth' Value='14'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Grid Background='Transparent'><Track x:Name='PART_Track' IsDirectionReversed='True'><Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False' Template='{StaticResource ScrollPage}'/></Track.DecreaseRepeatButton><Track.Thumb><Thumb MinHeight='28' Template='{StaticResource ScrollThumb}'/></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False' Template='{StaticResource ScrollPage}'/></Track.IncreaseRepeatButton></Track></Grid></ControlTemplate></Setter.Value></Setter>
   </Trigger>
   <Trigger Property='Orientation' Value='Horizontal'>
    <Setter Property='Height' Value='10'/><Setter Property='MinHeight' Value='10'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Grid Background='Transparent'><Track x:Name='PART_Track'><Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageLeftCommand' Focusable='False' Template='{StaticResource ScrollPage}'/></Track.DecreaseRepeatButton><Track.Thumb><Thumb MinWidth='28' Template='{StaticResource ScrollThumbHorizontal}'/></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageRightCommand' Focusable='False' Template='{StaticResource ScrollPage}'/></Track.IncreaseRepeatButton></Track></Grid></ControlTemplate></Setter.Value></Setter>
   </Trigger>
  </Style.Triggers>
 </Style>
 <!-- b51: overlay strip for the command row and format ribbon. It stays invisible at rest and shows only while the row is hovered, so a narrow window no longer draws a stray grey line under the buttons. -->
 <ControlTemplate x:Key='ScrollThumbSlim' TargetType='Thumb'><Border x:Name='T' CornerRadius='2' Background='{DynamicResource ScrollThumb}'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='T' Property='Background' Value='{DynamicResource ScrollThumbHover}'/></Trigger><Trigger Property='IsDragging' Value='True'><Setter TargetName='T' Property='Background' Value='{DynamicResource ScrollThumbHover}'/></Trigger></ControlTemplate.Triggers></ControlTemplate>
 <Style x:Key='SlimBar' TargetType='ScrollBar'>
  <Setter Property='Background' Value='Transparent'/><Setter Property='Height' Value='4'/><Setter Property='MinHeight' Value='4'/><Setter Property='Opacity' Value='0'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'><Grid Background='Transparent'><Track x:Name='PART_Track'><Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageLeftCommand' Focusable='False' Template='{StaticResource ScrollPage}'/></Track.DecreaseRepeatButton><Track.Thumb><Thumb MinWidth='24' Template='{StaticResource ScrollThumbSlim}'/></Track.Thumb><Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageRightCommand' Focusable='False' Template='{StaticResource ScrollPage}'/></Track.IncreaseRepeatButton></Track></Grid></ControlTemplate></Setter.Value></Setter>
  <!-- b51: the reveal (0 at rest, 1 while the row is hovered or a thumb is dragged) is driven from SlimHorizontal; only the thickness change lives here. Opacity (not Visibility) keeps the strip laid out, so the pointer can still reach it when it is shown. -->
  <Style.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter Property='Height' Value='6'/><Setter Property='MinHeight' Value='6'/></Trigger></Style.Triggers>
 </Style>
 <Style TargetType='TabControl'>
  <Setter Property='Background' Value='{DynamicResource Ribbon}'/><Setter Property='BorderBrush' Value='{DynamicResource Line}'/>
  <Setter Property='ItemsPanel'><Setter.Value><ItemsPanelTemplate><StackPanel Orientation='Horizontal'/></ItemsPanelTemplate></Setter.Value></Setter>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TabControl'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='0,0,0,1' Padding='6,0'><ScrollViewer HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Disabled' Focusable='False'><ItemsPresenter/></ScrollViewer></Border></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='TabItem'>
  <Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Background' Value='{DynamicResource Ribbon}'/><Setter Property='BorderBrush' Value='{DynamicResource Line}'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TabItem'>
   <Border x:Name='TabSurface' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='4' Padding='7,2' Margin='0,3,3,3'><ContentPresenter ContentSource='Header' VerticalAlignment='Center'/></Border>
   <ControlTemplate.Triggers><Trigger Property='IsSelected' Value='True'><Setter TargetName='TabSurface' Property='Background' Value='{DynamicResource ActiveFill}'/><Setter TargetName='TabSurface' Property='BorderBrush' Value='{DynamicResource ButtonLine}'/></Trigger><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='TabSurface' Property='Background' Value='{DynamicResource Hover}'/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='MenuItem'><Setter Property='Foreground' Value='#202020'/></Style>
 <Style TargetType='Button'>
  <Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Background' Value='Transparent'/>
  <Setter Property='BorderBrush' Value='Transparent'/><Setter Property='BorderThickness' Value='1'/>
  <Setter Property='Padding' Value='9,3'/><Setter Property='Margin' Value='2,0'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='VerticalContentAlignment' Value='Center'/><Setter Property='HorizontalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>
   <Border x:Name='Surface' RenderTransformOrigin='0.5,0.5' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6' Padding='{TemplateBinding Padding}'>
    <ContentPresenter x:Name='ButtonContent' RenderTransformOrigin='0.5,0.5' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='True'/>
   </Border>
   <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger>
    <Trigger Property='Tag' Value='Active'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource ActiveFill}'/><Setter Property='Foreground' Value='{DynamicResource ActiveInk}'/></Trigger>
    <MultiTrigger><MultiTrigger.Conditions><Condition Property='IsMouseOver' Value='True'/><Condition Property='Tag' Value='Active'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='{DynamicResource ActiveHover}'/></MultiTrigger>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ButtonContent' Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='0.96' ScaleY='0.96'/></Setter.Value></Setter></Trigger>
    <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Pressed}'/><Setter TargetName='ButtonContent' Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='0.92' ScaleY='0.92'/></Setter.Value></Setter></Trigger>
    <!-- Primary is a semantic default, not a persistent hover. Focus changes only the border. -->
    <Trigger Property='IsDefault' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Accent}'/><Setter TargetName='Surface' Property='BorderBrush' Value='{DynamicResource Accent}'/><Setter Property='Foreground' Value='White'/></Trigger>
    <Trigger Property='IsDefaulted' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Accent}'/><Setter TargetName='Surface' Property='BorderBrush' Value='{DynamicResource Accent}'/><Setter Property='Foreground' Value='White'/></Trigger>
    <MultiTrigger><MultiTrigger.Conditions><Condition Property='IsDefault' Value='True'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='{DynamicResource PrimaryHover}'/></MultiTrigger>
    <MultiTrigger><MultiTrigger.Conditions><Condition Property='IsDefaulted' Value='True'/><Condition Property='IsMouseOver' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='{DynamicResource PrimaryHover}'/></MultiTrigger>
    <MultiTrigger><MultiTrigger.Conditions><Condition Property='IsDefault' Value='True'/><Condition Property='IsPressed' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='{DynamicResource PrimaryPressed}'/></MultiTrigger>
    <MultiTrigger><MultiTrigger.Conditions><Condition Property='IsDefaulted' Value='True'/><Condition Property='IsPressed' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='{DynamicResource PrimaryPressed}'/></MultiTrigger>
    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <!-- All split controls share one hover surface; Tag is semantic, never an active fill. -->
 <Style x:Key='ToolbarSplit' TargetType='ContentControl'>
  <Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Height' Value='28'/><Setter Property='Padding' Value='__SPLITPAD__,0,0,0'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ContentControl'><Border x:Name='Surface' Background='Transparent' CornerRadius='6' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style x:Key='ToolbarSplitPart' TargetType='Button' BasedOn='{StaticResource {x:Type Button}}'>
  <Setter Property='BorderThickness' Value='0'/><Setter Property='Padding' Value='0'/><Setter Property='Margin' Value='0'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'><Border x:Name='Surface' Background='Transparent' CornerRadius='5' Padding='{TemplateBinding Padding}'><ContentPresenter x:Name='ButtonContent' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='True'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Pressed}'/></Trigger><Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Pressed}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='TextBox'>
  <Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Background' Value='{DynamicResource Paper}'/><Setter Property='BorderBrush' Value='{DynamicResource Line}'/><Setter Property='BorderThickness' Value='1'/><Setter Property='Padding' Value='7,0'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='TextBox'>
   <Border x:Name='Frame' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='6'><ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/></Border>
   <ControlTemplate.Triggers><Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Frame' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Frame' Property='Opacity' Value='0.5'/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ComboBox'>
  <Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Background' Value='{DynamicResource Paper}'/><Setter Property='BorderBrush' Value='{DynamicResource Line}'/>
  <Setter Property='Padding' Value='8,3'/><Setter Property='MinHeight' Value='26'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'>
   <Grid>
    <ToggleButton x:Name='Toggle' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' Focusable='False' IsChecked='{Binding IsDropDownOpen,RelativeSource={RelativeSource TemplatedParent},Mode=TwoWay}' ClickMode='Press'>
     <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='6'><Path Data='__CHEVRON__' Stroke='{DynamicResource Ink}' StrokeThickness='__CHEVRONSTROKE__' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Width='12' Height='12' RenderTransformOrigin='0.5,0.5' RenderTransform='__CHEVRONSCALE__,0,0,__CHEVRONSCALE__,0,0' HorizontalAlignment='Right' Margin='0,0,2,0' VerticalAlignment='Center'/></Border></ControlTemplate></ToggleButton.Template>
    </ToggleButton>
    <ContentPresenter x:Name='SelectedValue' Content='{TemplateBinding SelectionBoxItem}' ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}' Margin='8,0,14,0' VerticalAlignment='Center' IsHitTestVisible='False'/>
    <!-- b52 (user 2026-10-28): the size word sat ~2 px above the other toolbar labels and its right inset left a 7 px hole before the
         chevron. One TextBox keeps the number and the unit on one baseline (no per-run offsets); padding 1,0,1,3 drops the old 5 px
         bottom lift to 3, which centres the ink band level with 본문 / 목록. The 14 px right margin clears the 12 px chevron and the
         left margin is 2 px because the text is right-aligned, so the spare width sits on the left and is not worth keeping. -->
    <TextBox x:Name='PART_EditableTextBox' Visibility='Collapsed' Margin='0,0,14,0' Padding='0,0,0,3' VerticalContentAlignment='Center' TextAlignment='Right' Background='Transparent' BorderThickness='0' Foreground='{TemplateBinding Foreground}' IsReadOnly='{TemplateBinding IsReadOnly}'/>
    <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
     <Border Background='{DynamicResource Paper}' BorderBrush='{DynamicResource Line}' BorderThickness='1' CornerRadius='5' MinWidth='{Binding ActualWidth,RelativeSource={RelativeSource TemplatedParent}}' MaxHeight='320' Padding='4'>
      <ScrollViewer CanContentScroll='True'><ItemsPresenter/></ScrollViewer>
     </Border>
    </Popup>
   </Grid>
   <ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Toggle' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Toggle' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsEditable' Value='True'><Setter TargetName='SelectedValue' Property='Visibility' Value='Collapsed'/><Setter TargetName='PART_EditableTextBox' Property='Visibility' Value='Visible'/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ComboBoxItem'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Padding' Value='8,5'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'><Border x:Name='Row' Padding='{TemplateBinding Padding}' CornerRadius='3'><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter TargetName='Row' Property='Background' Value='{DynamicResource AccentSoft}'/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType='CheckBox'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='VerticalContentAlignment' Value='Center'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
   <Grid Background='Transparent'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
    <Border x:Name='Box' Width='15' Height='15' CornerRadius='3' BorderThickness='1.5' BorderBrush='{DynamicResource CheckLine}' Background='Transparent' VerticalAlignment='Center'><Path x:Name='Mark' Data='M2.4,6.1 L4.9,8.6 L9.6,3.4' Stroke='{DynamicResource CheckMark}' StrokeThickness='1.9' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Visibility='Collapsed'/></Border>
    <ContentPresenter x:Name='Label' Grid.Column='1' Margin='6,0,0,0' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='True'/>
   </Grid>
   <ControlTemplate.Triggers>
    <Trigger Property='IsChecked' Value='True'><Setter TargetName='Box' Property='Background' Value='{DynamicResource CheckOn}'/><Setter TargetName='Box' Property='BorderBrush' Value='{DynamicResource CheckOn}'/><Setter TargetName='Mark' Property='Visibility' Value='Visible'/></Trigger>
    <Trigger Property='HasContent' Value='False'><Setter TargetName='Label' Property='Visibility' Value='Collapsed'/></Trigger>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <!-- Quiet outlined toggle (source edit): same chrome as the top bar toggles; the Markdown mark lives in the template so Content stays the plain label. -->
 <Style x:Key='FormatViewToggle' TargetType='CheckBox'><Setter Property='FontFamily' Value='Segoe UI, Malgun Gothic'/><Setter Property='FontSize' Value='13'/><Setter Property='FontWeight' Value='Normal'/>
  <Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Height' Value='22'/><Setter Property='MinHeight' Value='22'/><Setter Property='MinWidth' Value='__SQUARE__'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
   <Border x:Name='Surface' RenderTransformOrigin='0.5,0.5' Background='Transparent' BorderBrush='{DynamicResource ButtonLine}' BorderThickness='1' CornerRadius='6' Padding='8,0,9,0'><StackPanel x:Name='ToggleContent' HorizontalAlignment='Center' Margin='0,0,0,1' RenderTransformOrigin='0.5,0.5' Orientation='Horizontal' VerticalAlignment='Center'><ContentControl x:Name='SourceGlyph' Content='{DynamicResource SourceToggleGlyph}' Margin='0,0,4,0' VerticalAlignment='Center' Focusable='False'/><ContentPresenter VerticalAlignment='Center' RecognizesAccessKey='True'/></StackPanel></Border>
   <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger>
    <Trigger Property='IsChecked' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource ActiveFill}'/><Setter Property='Foreground' Value='{DynamicResource ActiveInk}'/></Trigger>
    <MultiTrigger><MultiTrigger.Conditions><Condition Property='IsMouseOver' Value='True'/><Condition Property='IsChecked' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='{DynamicResource ActiveHover}'/></MultiTrigger>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ToggleContent' Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='0.96' ScaleY='0.96'/></Setter.Value></Setter></Trigger>
    <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Pressed}'/><Setter TargetName='ToggleContent' Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='0.92' ScaleY='0.92'/></Setter.Value></Setter></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <!-- __SQUARE__x__SQUARE__ borderless icon toggle (find bar Aa): hover fill, ActiveFill/ActiveInk when on. -->
 <Style x:Key='SquareToggle' TargetType='CheckBox'><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Width' Value='__SQUARE__'/><Setter Property='Height' Value='__SQUARE__'/><Setter Property='Cursor' Value='Hand'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
   <Border x:Name='Surface' RenderTransformOrigin='0.5,0.5' Background='Transparent' CornerRadius='6'><ContentPresenter x:Name='ToggleContent' RenderTransformOrigin='0.5,0.5' VerticalAlignment='Center' HorizontalAlignment='Center'/></Border>
   <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger>
    <Trigger Property='IsChecked' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource ActiveFill}'/><Setter Property='Foreground' Value='{DynamicResource ActiveInk}'/></Trigger>
    <MultiTrigger><MultiTrigger.Conditions><Condition Property='IsMouseOver' Value='True'/><Condition Property='IsChecked' Value='True'/></MultiTrigger.Conditions><Setter TargetName='Surface' Property='Background' Value='{DynamicResource ActiveHover}'/></MultiTrigger>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='ToggleContent' Property='RenderTransform'><Setter.Value><ScaleTransform ScaleX='0.96' ScaleY='0.96'/></Setter.Value></Setter></Trigger>
    <Trigger Property='IsPressed' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Pressed}'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/></Trigger>
   </ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='ToolTip'><Setter Property='Background' Value='{DynamicResource Paper}'/><Setter Property='Foreground' Value='{DynamicResource Ink}'/><Setter Property='Padding' Value='9,6'/></Style>
</ResourceDictionary>";
        var resources=(ResourceDictionary)XamlReader.Parse(xaml.Replace("__CHEVRON__",ChevronGeometry).Replace("__CHEVRONSCALE__",SplitChevronScale.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("__CHEVRONSTROKE__",SplitChevronStroke.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("__SQUARE__",SquareSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Replace("__SPLITPAD__",SplitSurfacePadding.ToString(System.Globalization.CultureInfo.InvariantCulture)));resources["ChromeFont"]=LabelFamily;((Style)resources[typeof(TextBox)]).Setters.Add(new Setter(UIElement.RenderTransformProperty,CaptionBinding(true)));resources["SourceToggleGlyph"]=CenteredInkIcon("sourceBraceA",16,0.5);/* b52 (user 2026-10-28): page + large { } — the Markdown logo was rejected; sourceBraceB is the spare variant */return resources;
    }
    // Frozen: a mutable brush defeats WPF's formatted-run sharing, so a document built from these
    // (code background, table borders, quote bars, link ink, code tokens) re-formats whole
    // paragraphs on every layout pass. Nothing in the product animates or mutates a brush in
    // place (Theme/SetDark assign new ones), so freezing is invisible. Profile values only.
    internal static SolidColorBrush Brush(string hex) {var brush=(SolidColorBrush)new BrushConverter().ConvertFromString(hex);brush.Freeze();return brush;}
    internal static FrameworkElement HistoryIcon(bool redo){var canvas=new Canvas {Width=20,Height=20};var path=new Path {Data=Geometry.Parse("M6 5 L2.5 8.5 L6 12 M3 8.5 H11 Q17 8.5 17 14 V16"),StrokeThickness=1.8,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};path.SetBinding(Shape.StrokeProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Button),1)});canvas.Children.Add(path);if(redo){canvas.RenderTransform=new ScaleTransform(-1,1);canvas.RenderTransformOrigin=new Point(.5,.5);}return new Viewbox {Width=20,Height=20,Child=canvas};}
    // ---- One icon set: 20 px grid, 1.5 px round stroke, ~16 px live area (Material Symbols Outlined at 20 px), ink = the button's Foreground. ----
    internal const string GridTag="G20";
    internal const double LabelSize=13;
    /* b49: one 28x28 box for every square icon button (format ribbon, table row, find bar); the SquareToggle style and the style helpers below all read it */
    internal const double SquareSize=28;
    /* b51: the 28 px click box stays, but the 1 px margin each side is gone — 2 px off every button gap (user: the full ribbon is too wide). */
    internal const double ButtonGap=0;
    /* b52 (user 2026-10-28): the format ribbon is squeezed to the width of the top menu row. Icon-only ribbon buttons keep the
       28 px height but lose 4 px of width (24 px box = the floor the user set), icon+label buttons lose 3 px of side padding and
       1 px of icon-to-label gap, and the ribbon's rules sit 4 px from their neighbours instead of 6. Icon ink, button height and
       the top bar are untouched. */
    internal const double RibbonIconWidth=24;
    /* b52: icon+label ribbon buttons carry their own 2 px inside gap instead of padding, so the ink starts one px sooner on each
       side; their hit box is still the icon slot + gap + label (>= 40 px wide), well over the 24 px floor. */
    internal const double RibbonLabelPadding=0;
    /* b52: the ribbon's group rules keep the top bar's 6 px sides, so the ink-to-ink distance across a group boundary (13 px, rule
       included) stays larger than the widest inside-a-group gap (10.3 px between 체크 and 인용) — the user's grouping floor. */
    internal const double RibbonDividerGap=6;
    /* b52: the split button's shared hover surface keeps only its left pad — the caption half already carries its own side padding,
       so the old 4 px left inset (added to a caption that also ends 4 px before its edge) doubled up. */
    internal const double SplitSurfacePadding=2;
    /* b52: one shared icon-to-label gap in the ribbon (체크·인용·코드·구분·목록), so every row shares one rhythm. */
    internal const double RibbonIconLabelGap=3;/* user 2026-10-28: 1 px read as glued icon+label */
    internal static readonly FontFamily LabelFamily=new FontFamily("Segoe UI, Malgun Gothic");
    internal static FontFamily ChromeFamily(string language){switch(language){case "en":return new FontFamily("Segoe UI, Malgun Gothic");case "ja":return new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI, Malgun Gothic");case "zh-CN":return new FontFamily("Microsoft YaHei UI, Segoe UI, Malgun Gothic");case "zh-TW":return new FontFamily("Microsoft JhengHei UI, Segoe UI, Malgun Gothic");default:return LabelFamily;}}
    // Scope text metrics to chrome only: never inherit these settings through the document host.
    internal const string KeepFontTag="KeepFont";/* glyph TextBlocks (find bar Aa) that ToolbarText must not reset to LabelSize/Normal */
    internal static void ToolbarText(DependencyObject root){var element=root as FrameworkElement;ToolbarText(root,element==null?LabelFamily:element.TryFindResource("ChromeFont") as FontFamily??LabelFamily);}
    /* b56: a toolbar row that measures its own overflow has to re-measure when a language switch rewrites
       every caption in place, and ApplyLanguage formats the chrome roots through ToolbarText. The hook
       lives on the row element itself (a static event would keep every closed window alive). */
    internal static readonly DependencyProperty ChromeTextChangedProperty=DependencyProperty.RegisterAttached("ChromeTextChanged",typeof(Action),typeof(Ui),new PropertyMetadata(null));
    internal static void SetChromeTextChanged(DependencyObject element,Action callback){element.SetValue(ChromeTextChangedProperty,callback);}
    internal static void ToolbarText(DependencyObject root,FontFamily font){
        var scope=root as FrameworkElement;if(scope!=null&&!scope.Resources.Contains(typeof(TextBlock))){var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(UIElement.RenderTransformProperty,CaptionBinding(false)));scope.Resources[typeof(TextBlock)]=style;}
        ToolbarTextCore(root,font);
        var hook=scope==null?null:scope.GetValue(ChromeTextChangedProperty) as Action;if(hook!=null)hook();
    }
    static void ToolbarTextCore(DependencyObject root,FontFamily font){
        TextOptions.SetTextFormattingMode(root,TextFormattingMode.Display);TextOptions.SetTextRenderingMode(root,TextRenderingMode.ClearType);RenderOptions.SetClearTypeHint(root,ClearTypeHint.Enabled);
        var menu=root as MenuItem;if(menu!=null&&menu.Parent is Menu&&menu.Header is string){var header=new FrameworkElementFactory(typeof(AccessText));header.SetBinding(AccessText.TextProperty,new Binding());header.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);header.SetBinding(UIElement.RenderTransformProperty,CaptionBinding(false));menu.HeaderTemplate=new DataTemplate {VisualTree=header};}
        var block=root as TextBlock;if(block!=null)BindingOperations.SetBinding(block,UIElement.RenderTransformProperty,CaptionBinding(false));
        System.Windows.Documents.TextElement.SetFontFamily(root,font);
        System.Windows.Documents.TextElement.SetFontSize(root,LabelSize);System.Windows.Documents.TextElement.SetFontWeight(root,FontWeights.Normal);
        foreach(object child in LogicalTreeHelper.GetChildren(root)){var node=child as DependencyObject;if(node==null)continue;var nested=node as FrameworkElement;if(nested!=null&&(Object.Equals(nested.Tag,"HeadingText")||Object.Equals(nested.Tag,KeepFontTag)))continue;ToolbarTextCore(node,font);}
    }
    internal static MultiBinding CaptionBinding(bool field){
        var binding=new MultiBinding {Converter=new ChromeInkConverter(),ConverterParameter=field?"field":null};
        binding.Bindings.Add(new Binding {RelativeSource=new RelativeSource(RelativeSourceMode.Self)});
        foreach(string property in new[]{"ActualHeight","Text","FontFamily","FontSize","FontWeight","FontStyle","FontStretch"})binding.Bindings.Add(new Binding(property){RelativeSource=new RelativeSource(RelativeSourceMode.Self)});
        return binding;
    }
    internal static bool IsGrid(object content){var element=content as FrameworkElement;return element!=null&&Object.Equals(element.Tag,GridTag);}
    static Binding InkBinding(){return new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(ButtonBase),1)};}
    static Path GridStroke(string data,double thickness,string resource){
        Geometry geometry;if(data.StartsWith("@24:",StringComparison.Ordinal)){var group=new GeometryGroup();group.Children.Add(Geometry.Parse(data.Substring(4)));group.Transform=new ScaleTransform(20/24.0,20/24.0);geometry=group;}else geometry=Geometry.Parse(data);
        var path=new Path {Data=geometry,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};Weight(path,thickness);inkPaths.Add(new WeakReference(path));
        if(resource==null)path.SetBinding(Shape.StrokeProperty,InkBinding());else path.SetResourceReference(Shape.StrokeProperty,resource);return path;
    }
    /* b42: light lines on a dark background read too faint, so dark mode draws every icon line 20% heavier. Each icon path keeps its light-mode width in Tag. */
    static readonly System.Collections.Generic.List<WeakReference> inkPaths=new System.Collections.Generic.List<WeakReference>();static double inkWeight=1;static bool inkDark;
    static void Weight(Path path,double width){path.Tag=width;path.StrokeThickness=width*inkWeight;}
    /* b57: the multiplier used to be one process-wide weight, so toggling dark in one window
       restroked every icon in the other windows. A pass now leaves the paths that hang in another
       Window alone, and each window remembers the theme it last painted (a repeated pass with the
       same theme costs nothing). Editor's Activated handler re-applies this window's pass, which
       also picks up icons it built after another window's pass; paths not in a Window yet (being
       built) still take the current pass. */
    internal static readonly DependencyProperty IconThemeAppliedProperty=DependencyProperty.RegisterAttached("IconThemeApplied",typeof(bool),typeof(Ui),new PropertyMetadata(false));
    /* Bumped whenever the global ink weight flips. A window whose last pass is from an older epoch
       may hold icons built under the other weight (dark -> light -> dark in another window), so it
       repaints even though its theme and the current weight agree again. */
    static int inkEpoch;
    internal static readonly DependencyProperty IconThemeEpochProperty=DependencyProperty.RegisterAttached("IconThemeEpoch",typeof(int),typeof(Ui),new PropertyMetadata(0));
    static Window IconWindow(DependencyObject node){
        while(node!=null){
            var window=node as Window;if(window!=null)return window;
            DependencyObject parent=null;
            try{
                var visual=node as Visual;parent=visual==null?null:VisualTreeHelper.GetParent(visual);
                /* the visual root above a Window is not a FrameworkElement, so only ask the logical
                   tree for elements it accepts */
                if(parent==null&&(node is FrameworkElement||node is FrameworkContentElement))parent=LogicalTreeHelper.GetParent(node);
            }catch(InvalidOperationException){/* a disposed/detached visual has no host to protect */return null;}
            node=parent;
        }
        return null;
    }
    internal static void SetIconTheme(bool dark,Window owner){
        bool applied=owner!=null&&(bool)owner.GetValue(IconThemeAppliedProperty);
        if(owner!=null&&applied==dark&&inkDark==dark&&(int)owner.GetValue(IconThemeEpochProperty)==inkEpoch)return;
        if(inkDark!=dark)inkEpoch++;
        inkWeight=dark?1.2:1;inkDark=dark;if(owner!=null){owner.SetValue(IconThemeAppliedProperty,dark);owner.SetValue(IconThemeEpochProperty,inkEpoch);}
        for(int i=inkPaths.Count-1;i>=0;i--){
            var path=inkPaths[i].Target as Path;if(path==null){inkPaths.RemoveAt(i);continue;}
            if(owner!=null){var host=IconWindow(path);if(host!=null&&!Object.ReferenceEquals(host,owner))continue;}
            if(path.Tag is double)path.StrokeThickness=(double)path.Tag*inkWeight;
        }
    }
    internal static void ReapplyIconTheme(bool dark,Window owner){SetIconTheme(dark,owner);}
    /* b41: icons are drawn on the 20 grid and shown at 17 px with a ~1.06 px line, the RC8 size the user picked (matches the 파일~도움말 menu text). */
    internal const double IconScale=0.85,IconLine=1.25;
    static Canvas GridCanvas(params UIElement[] parts){var canvas=new Canvas {Width=20,Height=20,Tag=GridTag,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,LayoutTransform=new ScaleTransform(IconScale,IconScale)};foreach(var part in parts)canvas.Children.Add(part);return canvas;}
    static Path GridFill(Geometry geometry){var path=new Path {Data=geometry};path.SetBinding(Shape.FillProperty,InkBinding());return path;}
    // Letter glyph centred on the 20 px grid (cap height ~12 px at 17 px), so B / I / S match the outline icons in size.
    internal static FrameworkElement GridGlyph(string text,string face,FontStyle style,FontWeight weight,double size,double centerY,bool strike){
        var typeface=new Typeface(new FontFamily(face),style,weight,FontStretches.Normal);var formatted=new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,typeface,size,Brushes.Black,1.0);
        var geometry=formatted.BuildGeometry(new Point(0,0));Rect bounds=geometry.Bounds;geometry.Transform=new TranslateTransform(10-bounds.Left-bounds.Width/2,centerY-bounds.Top-bounds.Height/2);
        var canvas=GridCanvas(GridFill(geometry));if(strike)canvas.Children.Add(GridStroke("M3.5 10 H16.5",IconLine,null));return canvas;
    }
    // Text colour "A" with its colour bar (the swatch Border is owned by the caller and recoloured on every pick).
    /* b51: the A used to sit on centerY 8 while B / I / S sit on 10, so the letter read ~2 px high (user). The A now shares their ink band, and the bar hangs just under it. */
    internal static FrameworkElement ColorGlyph(Border swatch){
        /* 2026-10-28 user: a font A (any weight) read heavier than the outline neighbours and narrow, and sat on the colour bar. Drawn as an outline A on the same stroke as the other icons, 11 wide x 10.5 high, ending 3 units above the bar */var glyph=GridCanvas(GridStroke("M6.25 15 L11.5 3.5 L16.75 15 M8.3 10.5 H14.7",1.4,null));/* 10.5 wide (9 read narrow next to the highlighter), a slightly heavier stroke than the outline set so it holds its own next to B, shifted 1.5 right toward the arrow */swatch.Width=11.5;swatch.Height=2.3;swatch.CornerRadius=new CornerRadius(.5);Canvas.SetLeft(swatch,5.75);Canvas.SetTop(swatch,17);glyph.Children.Add(swatch);return glyph;
    }
    /* b40: icons are drawn by hand on a 24 grid (Lucide keylines, ISC licence) and scaled to the 20 px canvas without auto-fitting, so each shape keeps its optical size:
       squares 18 (table, image, check), circles/diagonals a little larger, letters smaller (14) because a glyph reads bigger than an outline of the same height. */
    static string GridData(string key){switch(key){
        /* b41: top row is the RC8 set; b42 grows new/open/save optically (open was a short landscape shape and read much smaller) and drops the save slot line that smudged at 15 px */
        case "new":return "M3.5 2 H12 L16.5 6.5 V18 H3.5 Z M12 2 V6.5 H16.5";
        case "open":return "M2 17 V3.5 H7.5 L9.5 5.5 H16.5 V8 M2 17 H15.5 L18.5 8 H5 Z";
        case "save":return "M2.5 2.5 H13.5 L17.5 6.5 V17.5 H2.5 Z M6 2.5 V7 H12.5 V2.5 M5.5 17.5 V11.5 H14.5 V17.5";
        /* TXT mode: a type "T" */
        case "type":return "@24:M5 7.5 V5 H19 V7.5 M12 5 V19 M9 19 H15";
        /* numbered list: a "1" and a "2" beside three lines */
        case "listNumber":return "@24:M3.5 5.5 L5.5 4 V10.5 M3.5 14.5 C3.5 13 7.5 12.5 7.5 15 C7.5 16.5 3.5 18 3.5 20 H7.5 M11 6.5 H21 M11 12 H21 M11 17.5 H21";
        case "find":return "M13.5 8 A5.5 5.5 0 1 1 2.5 8 A5.5 5.5 0 1 1 13.5 8 M12 12 L18 18";
        case "replace":return "M2.5 6 H17 L13 2 M17 6 V9 M17.5 14 H3 L7 18 M3 14 V11";
        case "edit":return "M4 13.5 L12.5 5 L16 8.5 L7.5 17 L3 18 Z M11.5 6 L15 9.5 M14 3.5 L15 2.5 Q16 1.5 17 2.5 L18 3.5 Q19 4.5 18 5.5 L17 6.5";
        case "dark":return "M17.45 10.84 A7.5 7.5 0 1 1 9.16 2.55 A5.1 5.1 0 0 0 17.45 10.84 Z";/* 2026-10-28 user: the filled dark-mode moon read fat; a tighter inner arc thins the crescent *//* crescent = circle r7.5 minus circle r6 offset to the top right (classic moon proportions) *//* fuller crescent than RC8 (user: too thin) */
        case "close":return "@24:M17.5 6.5 L6.5 17.5 M6.5 6.5 L17.5 17.5";
        case "print":return "M6 6.5 V2.5 H14 V6.5 M5 15 H2.5 V8 Q2.5 6.5 4 6.5 H16 Q17.5 6.5 17.5 8 V15 H15 M5.5 12 H14.5 V18 H5.5 Z M14.5 9 H15.5";
        case "undo":return "@24:M9 14 L4 9 L9 4 M4 9 H14.5 A5.5 5.5 0 0 1 20 14.5 A5.5 5.5 0 0 1 14.5 20 H11";
        case "redo":return "@24:M15 14 L20 9 L15 4 M20 9 H9.5 A5.5 5.5 0 0 0 4 14.5 A5.5 5.5 0 0 0 9.5 20 H13";
        case "check":return "@24:M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M7.5 12 L10.5 15 L16.5 9";
        case "quote":return "@24:M4 5 V19 M8.5 6 H20 M8.5 12 H20 M8.5 18 H16";/* quote bar with all three lines to its right: |≡ */
        /* a paragraph line above and below, and the markdown "- - -" between them */
        case "rule":return "@24:M3 5 H16 M3 12 H6 M10.5 12 H13.5 M18 12 H21 M3 19 H16";
        case "code":return "@24:M18 16 L22 12 L18 8 M6 8 L2 12 L6 16 M14.5 4 L9.5 20";
        case "link":return "@24:M10 13 A5 5 0 0 0 17.54 13.54 L20.54 10.54 A5 5 0 0 0 13.47 3.47 L11.75 5.18 M14 11 A5 5 0 0 0 6.46 10.46 L3.46 13.46 A5 5 0 0 0 10.53 20.53 L12.24 18.82";
        case "inline":return "M5.5 4 H14.5 Q17 4 17 6.5 V13.5 Q17 16 14.5 16 H5.5 Q3 16 3 13.5 V6.5 Q3 4 5.5 4 Z M7.25 7.5 L8.75 10.5 M11.25 7.5 L12.75 10.5";
        case "highlight":return "@24:M15 3.6 L20.4 9 L11.4 18 L6 12.6 Z M6.3 12.9 L4.5 19.2 L11.1 17.7 M13.8 20.4 H20.7";
        case "image":return "@24:M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M11 9 A2 2 0 1 1 7 9 A2 2 0 1 1 11 9 Z M21 15 L17.914 11.914 A2 2 0 0 0 15.086 11.914 L6 21";
        case "table":return "@24:M5 3 H19 A2 2 0 0 1 21 5 V19 A2 2 0 0 1 19 21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M3 9 H21 M3 15 H21 M9 3 V21 M15 3 V21";
        case "menu":return "@24:M4 12 H20 M4 6 H20 M4 18 H20";
        case "alignL":return "@24:M21 6 H3 M15 12 H3 M17 18 H3";
        case "alignC":return "@24:M21 6 H3 M17 12 H7 M19 18 H5";
        case "alignR":return "@24:M21 6 H3 M21 12 H9 M21 18 H7";
        case "up":return "@24:M5 12 L12 5 L19 12 M12 19 V5";
        case "down":return "@24:M12 5 V19 M19 12 L12 19 L5 12";
        case "source":return "@24:M8 3 H7 A2 2 0 0 0 5 5 V10 A2 2 0 0 1 3 12 A2 2 0 0 1 5 14 V19 A2 2 0 0 0 7 21 H8 M16 21 H17 A2 2 0 0 0 19 19 V14 A2 2 0 0 1 21 12 A2 2 0 0 1 19 10 V5 A2 2 0 0 0 17 3 H16";
        case "sourceMark":return "M1.6 2 H10.4 A1 1 0 0 1 11.4 3 V9 A1 1 0 0 1 10.4 10 H1.6 A1 1 0 0 1 0.6 9 V3 A1 1 0 0 1 1.6 2 Z M2.7 8 V4 L4.35 5.9 L6 4 V8 M8.6 4 V7.8 M7.3 6.5 L8.6 7.9 L9.9 6.5";/* the old Markdown-logo source toggle (user rejected it, 2026-10-28): kept only so a saved preference cannot resolve to a missing glyph */
        /* b52 (user 2026-10-28): the source toggle is a page with a large { } inside, not the Markdown logo (rejected). Drawn on the
           same 12 unit box as the other source marks (InkSizedIcon scales by 12), so the painted line lands at ~1.06 px like its
           neighbours. The ink spans 8.2 units vertically, which paints ~12 px — the height of the 원문 label beside it. "{" hooks
           right at both ends and spikes left at the middle, "}" mirrors it, and the facing hook ends keep ~1.8 units (2.2 px) apart
           so the pair reads as { } and never merges into one blob. */
        // Box 11.4 units, as large as the image/table icons' frames; corner radius 1.8 (outer ink edge ~2.9 px, user asked a touch rounder) is concentric with the
        // 원문 button: its 5 px inner border radius minus the ~2.6 px top/bottom gap (Apple-style nesting). Braces 1.08x about the centre with a 1.3-unit middle point (user: a little smaller, point sticks out more), clear of the frame on all sides.
        case "sourceBraceA":return "M2.57 1.5 H1.57 C1.07 1.5 0.57 2 0.57 3 V4.5 C0.57 5.2 0.07 5.7 -0.43 6 C0.07 6.3 0.57 6.8 0.57 7.5 V9 C0.57 10 1.07 10.5 1.57 10.5 H2.57 M9.35 1.5 H10.35 C10.85 1.5 11.35 2 11.35 3 V4.5 C11.35 5.2 11.85 5.7 12.35 6 C11.85 6.3 11.35 6.8 11.35 7.5 V9 C11.35 10 10.85 10.5 10.35 10.5 H9.35 M3.12 6 A0.3 0.3 0 1 0 3.72 6 A0.3 0.3 0 1 0 3.12 6 M5.52 6 A0.3 0.3 0 1 0 6.12 6 A0.3 0.3 0 1 0 5.52 6 M7.92 6 A0.3 0.3 0 1 0 8.52 6 A0.3 0.3 0 1 0 7.92 6";/* 2026-10-28 user-supplied SVG ({ ... }); braces pulled in 0.15 each side, dots 0.18 left (optical centre, tuned by the user) *//* 2026-10-28 user-supplied SVG ({ ... }); braces 0.3 further out each side and dots a touch smaller so the dots clear the braces *//* 2026-10-28 user-supplied SVG ({ ... }), braces 1.2 further apart so three solid dots sit 2.4 units (~3 px) apart without merging *//* dots: tiny stroked rings read as solid ~2 px dots *//* 2026-10-28 user-supplied SVG: soft-cornered { ... } braces with three dots *//* 2026-10-28 user: frame dropped (it suggested something unwanted); { ... } braces with three dots like a code/placeholder mark */
        case "sourceBraceB":return "M4.2 4.7 A1.8 1.8 0 0 1 7.8 4.7 V7.3 A1.8 1.8 0 0 1 4.2 7.3 Z M6.4 2.95 V2.2 C6.4 1.7 6.7 1.5 7.2 1.5 H8.6 C9.6 1.5 10.4 2.3 10.4 3.3 V4.6 C10.4 5.3 10.9 5.8 11.6 6 C10.9 6.2 10.4 6.7 10.4 7.4 V8.7 C10.4 9.7 9.6 10.5 8.6 10.5 H6.6 M5.6 9.05 V9.8 C5.6 10.3 5.3 10.5 4.8 10.5 H3.4 C2.4 10.5 1.6 9.7 1.6 8.7 V7.4 C1.6 6.7 1.1 6.2 0.4 6 C1.1 5.8 1.6 5.3 1.6 4.6 V3.3 C1.6 2.3 2.4 1.5 3.4 1.5 H5.4";/* 2026-10-28 user reference: braces joined through a central capsule ({0}) */
        case "sourceDoc":return "M2.2 1 H9.8 A1 1 0 0 1 10.8 2 V10 A1 1 0 0 1 9.8 11 H2.2 A1 1 0 0 1 1.2 10 V2 A1 1 0 0 1 2.2 1 Z M5.2 2.8 H4.7 A0.9 0.9 0 0 0 3.8 3.7 V5.3 A0.7 0.7 0 0 1 3.1 6 A0.7 0.7 0 0 1 3.8 6.7 V8.3 A0.9 0.9 0 0 0 4.7 9.2 H5.2 M6.8 2.8 H7.3 A0.9 0.9 0 0 1 8.2 3.7 V5.3 A0.7 0.7 0 0 0 8.9 6 A0.7 0.7 0 0 0 8.2 6.7 V8.3 A0.9 0.9 0 0 1 7.3 9.2 H6.8";/* source toggle (2026-10-27): plain page + one clear brace pair, no fold, few strokes */
        case "sourcePage":return "M8.4 1.2 H3.6 A1.2 1.2 0 0 0 2.4 2.4 V12 A1.2 1.2 0 0 0 3.6 13.2 H10.8 A1.2 1.2 0 0 0 12 12 V4.8 Z M8.4 1.2 V4.8 H12 M6 6 H5.6 A0.8 0.8 0 0 0 4.8 6.8 V8.2 A0.8 0.8 0 0 1 4 9 A0.8 0.8 0 0 1 4.8 9.8 V11.2 A0.8 0.8 0 0 0 5.6 12 H6 M8.4 6 H8.8 A0.8 0.8 0 0 1 9.6 6.8 V8.2 A0.8 0.8 0 0 0 10.4 9 A0.8 0.8 0 0 0 9.6 9.8 V11.2 A0.8 0.8 0 0 1 8.8 12 H8.4";/* (unused page+braces drawing, kept for reference) */
        /* b50: the source toggle icon is a </> pair on a 12 unit tall box (InkSizedIcon scales by 12), mirror-symmetric about x=8, centred on y=6.6 */
        case "sourceCode":return "M4.8 3 L1.2 6.6 L4.8 10.2 M11.2 3 L14.8 6.6 L11.2 10.2 M9.2 0.6 L6.8 12.6";
        case "markdown":return "M3.5 4 H16.5 A1.5 1.5 0 0 1 18 5.5 V14.5 A1.5 1.5 0 0 1 16.5 16 H3.5 A1.5 1.5 0 0 1 2 14.5 V5.5 A1.5 1.5 0 0 1 3.5 4 Z M5 13 V7 L7.5 10 L10 7 V13 M14 7 V13 M12 11 L14 13 L16 11";
        case "paragraph":return "@24:M5 3 H19 A2 2 0 0 1 21 5 V10 H3 V5 A2 2 0 0 1 5 3 Z M9 3 V10 M15 3 V10 M3 15.5 H21 M3 20.5 H14";
        case "chevron":return "M7.2 8 L10 12 L12.8 8";
        /* b56: the toolbar overflow mark: the double chevron "»" that opens the commands a narrow row cannot show */
        case "more":return "M3.5 3.5 L9 10 L3.5 16.5 M9.5 3.5 L15 10 L9.5 16.5";
    }return null;}
    /* Every table action is one family: a shared table base plus one 7 x 7 mark in the badge slot. The row base is the header half
       of the grid (badge slot underneath, centred on 12,18), the column base its left half (badge slot beside it, on 18,12).
       The bases are constants so no action can drift into a slightly different grid. */
    internal const string RowTableBase="@24:M5 3 H19 A2 2 0 0 1 21 5 V11 H3 V5 A2 2 0 0 1 5 3 Z M3 7 H21 M12 3 V11";
    internal const string ColumnTableBase="@24:M5 3 H11 V21 H5 A2 2 0 0 1 3 19 V5 A2 2 0 0 1 5 3 Z M3 12 H11 M7 3 V21";
    // The column base mirrored onto the right half, leaving the left slot (centred on 6,12) for the indent arrow.
    internal const string MoveTableBase="@24:M19 3 H13 V21 H19 A2 2 0 0 0 21 19 V5 A2 2 0 0 0 19 3 Z M13 12 H21 M17 3 V21";
    // Table row/column actions: a small ink table plus a coloured mark drawn clearly (green +, red -, red x).
    static string[] GridTableData(string key){switch(key){
        case "rowAdd":return new string[]{RowTableBase,"@24:M12 14.5 V21.5 M8.5 18 H15.5","TableAddMark"};
        case "rowRemove":return new string[]{RowTableBase,"@24:M8.5 18 H15.5","TableRemoveMark"};
        case "colAdd":return new string[]{ColumnTableBase,"@24:M14.5 12 H21.5 M18 8.5 V15.5","TableAddMark"};
        case "colRemove":return new string[]{ColumnTableBase,"@24:M14.5 12 H21.5","TableRemoveMark"};
        /* b49: same table footprint and same 7 unit mark as rowAdd / rowRemove (the x stays at the bottom-right), so it no longer reads smaller than its neighbours */
        case "tableDelete":return new string[]{RowTableBase,"@24:M14.5 14.5 L21.5 21.5 M21.5 14.5 L14.5 21.5","TableRemoveMark"};
        /* User 2026-10-29: an indent arrow reads only on the LEFT of what it moves (a corner badge did not), so the table half sits
           on the right and the ← / → spans the same 7 x 7 box in the left slot (shaft on y = 12 from x 2.5 to 9.5). Null resource =
           normal ink, since moving a table out or in is neither an add nor a delete. */
        case "tableOutdent":return new string[]{MoveTableBase,"@24:M9.5 12 H2.5 M6 8.5 L2.5 12 L6 15.5",null};
        case "tableIndent":return new string[]{MoveTableBase,"@24:M2.5 12 H9.5 M6 8.5 L9.5 12 L6 15.5",null};
    }return null;}
    /* The table tool row's 내어쓰기 / 들여쓰기 buttons (actions 9 and 10) draw this, so they sit in the same family as the row and
       column actions beside them: same base, same badge slot, same stroke and caps, normal ink instead of the green/red marks. */
    internal static FrameworkElement TableMoveIcon(bool indent){return GridIcon(indent?"tableIndent":"tableOutdent");}
    internal static FrameworkElement GridIcon(string key){
        /* b41: B / I / S are real type (the user wants "bold" to look bold); same font size and ink centre so none sits higher than its neighbours.
           b49: the italic I was Normal and read thin next to B, so it uses the real Georgia Bold Italic face (>= SemiBold; Georgia has no 600 face to pick).
           b50: the user read Bold Italic as "bold italic", so I is back to the regular Georgia Italic face. */
        if(key=="bold")return GridGlyph("B","Segoe UI",FontStyles.Normal,FontWeights.Bold,19,10,false);
        if(key=="italic")return GridGlyph("I","Georgia",FontStyles.Italic,FontWeights.Normal,19.5,10,false);
        if(key=="strike")return GridGlyph("S","Segoe UI",FontStyles.Normal,FontWeights.Normal,19,10,true);
        string[] pair=GridTableData(key);if(pair!=null)return GridCanvas(GridStroke(pair[0],IconLine,null),GridStroke(pair[1],1.75,pair[2]));
        if(key=="listBullet")return GridCanvas(GridStroke("@24:M9.5 6 H20.5 M9.5 12 H20.5 M9.5 18 H20.5",IconLine,null),GridStroke("@24:M4.5 6 H4.51 M4.5 12 H4.51 M4.5 18 H4.51",2.6,null));/* round-capped zero-length strokes = dots */
        /* 2.2.0: the find bar's regex toggle drew ".*" as type and the user could not read it (a hairline dot, a small high star), so it is the same ".*" drawn big: a heavy dot on the baseline and a six-spoke star at cap height */
        if(key=="regex")return GridCanvas(GridStroke("@24:M7 19.8 H7.01",3.8,null),GridStroke("@24:M15.99 5.43 V16.18 M11.34 8.11 L20.64 13.49 M11.34 13.49 L20.64 8.11",IconLine,null));
        string data=GridData(key);if(data==null){key="chevron";data=GridData("chevron");}
        var line=GridStroke(data,IconLine,null);if(key=="open")line.SetResourceReference(Shape.FillProperty,"OpenIconFill");if(key=="dark")line.SetResourceReference(Shape.FillProperty,"MoonIconFill");return GridCanvas(line);
    }
    /* b38: every outline icon is fitted by its INK: the longer side of the stroked outline equals FitTarget (16 px; denser or round shapes 15), centred on the 20 px grid. The close x keeps its own size (CloseInk). */
    internal const double CloseInk=9;
    internal const double RightEdge=8;
    internal static double FitTarget(string key){switch(key){case "find":case "replace":case "up":case "down":case "table":case "image":case "check":case "save":case "highlight":case "inline":return 15;}return 16;}
    static Path FittedStroke(string data,string key){
        var path=GridStroke(data,1.5,null);if(key=="close"||key=="chevron")return path;
        double angle=key=="link"?-45:0;var group=new GeometryGroup();group.Children.Add(path.Data);group.Transform=new RotateTransform(angle,10,10);Rect box=group.Bounds;double extent=System.Math.Max(box.Width,box.Height);if(box.IsEmpty||extent<=0)return path;
        double scale=(FitTarget(key)-1.5)/extent,cx=box.X+box.Width/2,cy=box.Y+box.Height/2;var fit=new TransformGroup();fit.Children.Add(new RotateTransform(angle,10,10));fit.Children.Add(new ScaleTransform(scale,scale,cx,cy));fit.Children.Add(new TranslateTransform(10-cx,10-cy));group.Transform=fit;path.Data=group;return path;
    }
    /* b41: the top command row sits next to the 파일~도움말 menu text, so it draws at 15 px with the same ~1.06 px line (the find bar uses GridIcon's 17 px scale instead) */
    internal const double TopIconScale=0.75;
    internal static FrameworkElement Icon(string name){if(name=="down")return Chevron();var icon=GridIcon(name) as Canvas;if(icon==null)return GridIcon(name);icon.LayoutTransform=new ScaleTransform(TopIconScale,TopIconScale);foreach(UIElement part in icon.Children){var path=part as Path;if(path!=null&&path.Tag is double)Weight(path,(double)path.Tag*IconScale/TopIconScale);}return icon;}
    internal const double ToggleIconScale=0.65;
    /* b41: the icon inside a 22 px bordered toggle is drawn smaller (13 px layout) than the borderless neighbours, since the border adds visual size; stroke compensated like Icon */
    internal static FrameworkElement ToggleIcon(string name){var icon=GridIcon(name) as Canvas;if(icon==null)return GridIcon(name);icon.LayoutTransform=new ScaleTransform(ToggleIconScale,ToggleIconScale);foreach(UIElement part in icon.Children){var path=part as Path;if(path!=null&&path.Tag is double)Weight(path,(double)path.Tag*IconScale/ToggleIconScale);}return icon;}
    // A smaller layout grid keeps the same painted line weight as the other toolbar icons.
    internal static FrameworkElement SizedIcon(string name,double scale){var icon=GridIcon(name) as Canvas;if(icon==null)return GridIcon(name);icon.LayoutTransform=new ScaleTransform(scale,scale);foreach(UIElement part in icon.Children){var path=part as Path;if(path!=null&&path.Tag is double)Weight(path,(double)path.Tag*IconScale/scale);}return icon;}
    // Size/crop the layout slot to the stroked ink, so the label margin is an ink-to-ink gap.
    internal static FrameworkElement InkSizedIcon(string name,double height){
        double scale=(height-IconLine*IconScale)/12;var icon=(Canvas)SizedIcon(name,scale);
        Rect bounds=Rect.Empty;foreach(UIElement part in icon.Children){var path=part as Path;if(path!=null)bounds.Union(path.Data.GetRenderBounds(new Pen(Brushes.Black,IconLine*IconScale/scale){LineJoin=PenLineJoin.Round,StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round}));}
        var slot=new Canvas {Width=bounds.Width*scale,Height=height,VerticalAlignment=VerticalAlignment.Center};Canvas.SetLeft(icon,-bounds.Left*scale);Canvas.SetTop(icon,-bounds.Top*scale);slot.Children.Add(icon);return slot;
    }
    /* 2.0.1 (user, 11-02): the {···} mark in the » list goes through the same 20 px grid and 0.85 scale as its neighbours. Its two upright
       strokes (x 0.57 / 11.35) sit on the 표 frame's uprights (grid x 2.5 / 17.5), so they land on the pixels exactly like the rest of the
       column; at CenteredInkIcon's own scale they fell on pixel centres and read crisper and thinner than every other icon. */
    internal static FrameworkElement MenuSourceIcon(){
        var geometry=Geometry.Parse(GridData("sourceBraceA")).Clone();double fit=15/10.78;
        var place=new TransformGroup();place.Children.Add(new ScaleTransform(fit,fit));place.Children.Add(new TranslateTransform(2.5-0.57*fit,10-6*fit));geometry.Transform=place;
        var line=GridStroke("M0 0",IconLine,null);line.Data=geometry;return GridCanvas(line);
    }
    internal static FrameworkElement CenteredInkIcon(string name,double height,double nudge){
        var slot=(Canvas)InkSizedIcon(name,height);var icon=(Canvas)slot.Children[0];
        double scale=(height-IconLine*IconScale)/12;Rect bounds=Rect.Empty;foreach(UIElement part in icon.Children){var path=part as Path;if(path!=null)bounds.Union(path.Data.GetRenderBounds(new Pen(Brushes.Black,IconLine*IconScale/scale){LineJoin=PenLineJoin.Round,StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round}));}
        Canvas.SetTop(icon,-bounds.Top*scale+(height-bounds.Height*scale)/2+nudge);return slot;/* short ink (e.g. a wide frame) centred in the slot, nudge in px for optical alignment */
    }
    internal static ContentControl SplitButton(Button main,Button arrow,double height){
        foreach(var part in new[]{main,arrow}){part.SetResourceReference(FrameworkElement.StyleProperty,"ToolbarSplitPart");part.BorderThickness=new Thickness(0);part.Margin=new Thickness(0);part.Height=part.MinHeight=height;part.MinWidth=0;}
        main.Padding=new Thickness(0);arrow.Content=TightChevron();arrow.Padding=new Thickness(0);arrow.HorizontalContentAlignment=HorizontalAlignment.Center;arrow.VerticalContentAlignment=VerticalAlignment.Center;
        /* b50: the arrow is its own full-height SplitArrowWidth segment. InsertControls used to reset the heading arrow to 8.4px after this ran, which hid the b49 widening; callers must not assign its Width.
           b51: the segment is exactly chevron ink + an even pad either side (22 -> 18), so the empty space around the arrow shrinks and it reads attached to its main button. */
        arrow.Width=SplitArrowWidth;arrow.MinWidth=SplitArrowWidth;
        var divider=new Border {Width=1,Height=System.Math.Max(10,height-10),VerticalAlignment=VerticalAlignment.Center,Opacity=0,IsHitTestVisible=false};divider.SetResourceReference(Border.BackgroundProperty,"Line");
        var row=new StackPanel {Orientation=Orientation.Horizontal};row.Children.Add(main);row.Children.Add(divider);row.Children.Add(arrow);
        var pair=new ContentControl {Content=row,Height=height,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(ButtonGap,0,ButtonGap,0),Tag="ToolbarSplit"};pair.SetResourceReference(FrameworkElement.StyleProperty,"ToolbarSplit");
        pair.MouseEnter+=delegate{divider.Opacity=1;};pair.MouseLeave+=delegate{divider.Opacity=0;};return pair;
    }
    /* b51: width of every split button's arrow segment; 22 -> 18. TightChevron paints ~11 px, so 18 leaves an even ~3.5 px pad
       either side of the arrow instead of ~5.6 (user: too much empty space left/right of the arrow).
       b52: 18 -> 14 (user, 2026-10-28: the arrow halves read almost square next to their main button). The painted chevron is
       unchanged (TightChevron still draws 11.6 px), so the segment is now the chevron plus ~1.2 px either side. */
    internal const double SplitArrowWidth=12;/* 2026-10-28: narrower 7-unit chevron, keep ~even pad */
    /* 2026-10-28 user: the four toolbar dropdowns (저장 · 본문 · 목록 · 글자색) must open flush under their whole split
       button — the visible left edge of both halves — not under the arrow segment.
       One rule for all four: place the popup window's top-left at the target's bottom-left corner in the target's own
       coordinate space (the Custom callback receives that space; the returned point is where the popup's top-left goes).
       Custom placement is used instead of Bottom because Bottom aligns the right edge instead of the left when
       SystemParameters.MenuDropAlignment (Windows "menus drop to the right") is on, and Custom does not consult it.
       No pixel nudge: the default popup chrome only insets its drop shadow on the right and bottom
       (SystemDropShadowChrome Margin 0,0,5,5 while SystemParameters.DropShadow is on), so the window edge is already the
       painted edge of the menu, in logical units, at any DPI. Vertical placement is the same as the old Bottom one. */
    static CustomPopupPlacement[] DropdownPoint(Size popup,Size target,Point offset){/* below-left first; above-left only when the menu would not fit below, which is what Bottom did before */return new CustomPopupPlacement[]{new CustomPopupPlacement(new Point(0,target.Height),PopupPrimaryAxis.Horizontal),new CustomPopupPlacement(new Point(0,-popup.Height),PopupPrimaryAxis.Horizontal)};}
    internal static void Dropdown(FrameworkElement target,ContextMenu menu){menu.PlacementTarget=target;menu.Placement=PlacementMode.Custom;menu.HorizontalOffset=0;menu.VerticalOffset=0;menu.PlacementRectangle=Rect.Empty;menu.CustomPopupPlacementCallback=DropdownPoint;}
    internal static void Dropdown(FrameworkElement target,Popup popup){popup.PlacementTarget=target;popup.Placement=PlacementMode.Custom;popup.HorizontalOffset=0;popup.VerticalOffset=0;popup.PlacementRectangle=Rect.Empty;popup.CustomPopupPlacementCallback=DropdownPoint;}
    /* X29: dark menu skin. The system menu templates paint their popup surface, row highlight, check
       mark and separator from static theme brushes, so swapping SystemColors cannot reach them
       (measured 2026-10-30: the popup stayed #F5F5F5, an item stayed #FFFFFF, with MenuBrushKey
       overridden). The dark skin therefore supplies its own menu and item templates on the app's own
       popup surfaces (Paper / Line / Ink / Hover); light mode keeps the system chrome untouched.
       The colours mirror the night[] palette of Editor.Theme() because the theme is switched there. */
    internal static readonly Brush MenuSurface=Brush("#272727"),MenuLine=Brush("#515151"),MenuInk=Brush("#E8E8E8");
    static ControlTemplate darkMenuTemplate,darkTopMenuItemTemplate,darkSubMenuItemTemplate;
    static Style darkSubMenuItemStyle,darkTopMenuItemStyle,darkSeparatorStyle;
    const string MenuPopupXaml="<Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsSubmenuOpen}' Placement='Right' HorizontalOffset='-4' VerticalOffset='-3' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>"
        +"<Border x:Name='SubmenuBorder' Background='#272727' BorderBrush='#515151' BorderThickness='1' CornerRadius='5' Padding='3'>"
        +"<ScrollViewer x:Name='SubmenuScrollViewer' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Focusable='False'>"
        +"<StackPanel IsItemsHost='True' Grid.IsSharedSizeScope='True' KeyboardNavigation.DirectionalNavigation='Cycle'/></ScrollViewer></Border></Popup>";
    const string MenuXamlHead="<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='MenuItem'>";
    internal static ControlTemplate DarkMenuTemplate(){
        if(darkMenuTemplate==null){
            string xaml="<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ContextMenu'>"
                +"<Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='5' Padding='{TemplateBinding Padding}'>"
                +"<ScrollViewer x:Name='ContextMenuScrollViewer' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled' Focusable='False'>"
                +"<StackPanel IsItemsHost='True' Grid.IsSharedSizeScope='True' KeyboardNavigation.DirectionalNavigation='Cycle'/></ScrollViewer></Border></ControlTemplate>";
            darkMenuTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }
        return darkMenuTemplate;
    }
    internal static ControlTemplate DarkSubMenuItemTemplate(){
        if(darkSubMenuItemTemplate==null){
            string xaml=MenuXamlHead
                +"<Grid><Border x:Name='ItemSurface' Background='{TemplateBinding Background}' CornerRadius='4'/>"
                /* Metrics follow the system Aero2 row (measured 2026-10-30): 22 px row, icon 22 | gap 13 | text | gap 30 | shortcut | arrow 20,
                   icon/text/shortcut columns shared across the rows of one menu. */
                +"<Grid Margin='0,2,0,2'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto' MinWidth='22' SharedSizeGroup='MenuItemIconColumnGroup'/><ColumnDefinition Width='13'/><ColumnDefinition Width='Auto' SharedSizeGroup='MenuTextColumnGroup'/><ColumnDefinition Width='30'/><ColumnDefinition Width='Auto' SharedSizeGroup='MenuItemIGTColumnGroup'/><ColumnDefinition Width='20'/></Grid.ColumnDefinitions>"
                +"<Grid Grid.Column='0' Width='18' Height='18' HorizontalAlignment='Center' VerticalAlignment='Center'>"
                +"<ContentPresenter x:Name='ItemIcon' ContentSource='Icon' HorizontalAlignment='Center' VerticalAlignment='Center'/>"
                +"<Path x:Name='ItemCheck' Data='M 2,7 L 6,11 L 14,3' Stroke='#90BFFF' StrokeThickness='1.7' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Visibility='Collapsed' HorizontalAlignment='Center' VerticalAlignment='Center'/></Grid>"
                +"<ContentPresenter x:Name='HeaderHost' Grid.Column='2' ContentSource='Header' RecognizesAccessKey='True' VerticalAlignment='Center' HorizontalAlignment='Stretch'/>"
                +"<TextBlock x:Name='InputGestureText' Grid.Column='4' Text='{TemplateBinding InputGestureText}' Foreground='#BDBDBD' VerticalAlignment='Center'/>"
                +"<Path x:Name='Arrow' Grid.Column='5' Data='M 0,0 L 4,4 L 0,8' Stroke='#E8E8E8' StrokeThickness='1.3' Visibility='Collapsed' HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,6,0'/></Grid>"
                +MenuPopupXaml+"</Grid>"
                +"<ControlTemplate.Triggers>"
                +"<Trigger Property='IsHighlighted' Value='True'><Setter TargetName='ItemSurface' Property='Background' Value='#474747'/></Trigger>"
                +"<Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='ItemSurface' Property='Background' Value='#555555'/></Trigger>"
                +"<Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='#8A8A8A'/><Setter TargetName='InputGestureText' Property='Foreground' Value='#8A8A8A'/><Setter TargetName='Arrow' Property='Stroke' Value='#8A8A8A'/></Trigger>"
                +"<Trigger Property='IsChecked' Value='True'><Setter TargetName='ItemCheck' Property='Visibility' Value='Visible'/><Setter TargetName='ItemIcon' Property='Visibility' Value='Collapsed'/></Trigger>"
                +"<Trigger Property='Role' Value='SubmenuHeader'><Setter TargetName='Arrow' Property='Visibility' Value='Visible'/></Trigger>"
                +"</ControlTemplate.Triggers></ControlTemplate>";
            darkSubMenuItemTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }
        return darkSubMenuItemTemplate;
    }
    internal static ControlTemplate DarkTopMenuItemTemplate(){
        if(darkTopMenuItemTemplate==null){
            string xaml=MenuXamlHead
                +"<Grid><Border x:Name='ItemSurface' Background='{TemplateBinding Background}' CornerRadius='6'/>"
                /* Same box as the system bar item: 1 px frame, optional icon column (3 px margin), text margin = the item's Padding (per item). */
                +"<Grid Margin='1'><Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>"
                +"<ContentPresenter x:Name='ItemIcon' ContentSource='Icon' Margin='3' VerticalAlignment='Center'/>"
                +"<ContentPresenter x:Name='HeaderHost' Grid.Column='1' ContentSource='Header' RecognizesAccessKey='True' Margin='{TemplateBinding Padding}' VerticalAlignment='Center' HorizontalAlignment='Center'/></Grid>"
                +MenuPopupXaml+"</Grid>"
                +"<ControlTemplate.Triggers>"
                +"<Trigger Property='Icon' Value='{x:Null}'><Setter TargetName='ItemIcon' Property='Visibility' Value='Collapsed'/></Trigger>"
                +"<Trigger Property='IsHighlighted' Value='True'><Setter TargetName='ItemSurface' Property='Background' Value='#474747'/></Trigger>"
                +"<Trigger Property='IsSubmenuOpen' Value='True'><Setter TargetName='ItemSurface' Property='Background' Value='#555555'/></Trigger>"
                +"<Trigger Property='IsEnabled' Value='False'><Setter Property='Foreground' Value='#8A8A8A'/></Trigger>"
                /* The shared popup above is the submenu one (Right, -4/-3) because a bar item can be moved into the "»" list and then has Role=SubmenuHeader. A real bar header keeps the system template's rule: it draws below its item, with the light template's zero offsets. */
                +"<Trigger Property='Role' Value='TopLevelHeader'><Setter TargetName='PART_Popup' Property='Placement' Value='Bottom'/><Setter TargetName='PART_Popup' Property='HorizontalOffset' Value='0'/><Setter TargetName='PART_Popup' Property='VerticalOffset' Value='0'/></Trigger>"
                +"</ControlTemplate.Triggers></ControlTemplate>";
            darkTopMenuItemTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
        }
        return darkTopMenuItemTemplate;
    }
    /* The bar item keeps the bar's own metrics (28 px row, 8 px side padding in the template) because it
       replaces the window's ItemContainerStyle while dark mode is on. */
    internal static Style DarkTopMenuItemStyle(){
        if(darkTopMenuItemStyle==null){
            var style=new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty,MenuInk));
            style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty,28.0));
            style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,0,8,0)));/* the bar's own item padding (Editor.cs menuStyle) */
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.TemplateProperty,DarkTopMenuItemTemplate()));
            darkTopMenuItemStyle=style;
        }
        return darkTopMenuItemStyle;
    }
    internal static Style DarkSubMenuItemStyle(){
        if(darkSubMenuItemStyle==null){
            var style=new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty,MenuInk));
            style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));
            style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.TemplateProperty,DarkSubMenuItemTemplate()));
            darkSubMenuItemStyle=style;
        }
        return darkSubMenuItemStyle;
    }
    internal static Style DarkSeparatorStyle(){
        if(darkSeparatorStyle==null){
            var style=new Style(typeof(Separator));
            var template=new ControlTemplate(typeof(Separator));var border=new FrameworkElementFactory(typeof(Border));
            border.SetValue(FrameworkElement.HeightProperty,1.0);border.SetValue(FrameworkElement.MarginProperty,new Thickness(8,4,8,4));border.SetValue(Border.BackgroundProperty,MenuLine);border.SetValue(UIElement.SnapsToDevicePixelsProperty,true);
            template.VisualTree=border;
            style.Setters.Add(new Setter(Control.TemplateProperty,template));
            darkSeparatorStyle=style;
        }
        return darkSeparatorStyle;
    }
    // Every action button is the same 28x28 square, borderless at rest (hover/active fills come from the Button style).
    internal static void Square(Button button){button.Width=SquareSize;button.Height=SquareSize;button.MinWidth=SquareSize;button.MinHeight=SquareSize;button.Padding=new Thickness(0);button.Margin=new Thickness(ButtonGap,0,ButtonGap,0);button.HorizontalContentAlignment=HorizontalAlignment.Center;button.VerticalContentAlignment=VerticalAlignment.Center;button.VerticalAlignment=VerticalAlignment.Center;}
    /* b52: format-ribbon icon buttons keep the 28 px height but a 24 px wide box (the user's 24 px hit-area floor), so the row
       loses 4 px per icon button. The 20 px icon slot still fits with 2 px either side. */
    internal static void NarrowRibbonSquare(Button button){Square(button);button.Width=button.MinWidth=RibbonIconWidth;}
    // Top commands retain their 30 px pitch, but the painted surface matches the 22 px toggles.
    internal static void TopSquare(Button button){Square(button);button.Width=button.MinWidth=22;button.Height=button.MinHeight=22;button.Margin=new Thickness(1,0,7,0);}
    // Toggles share one chrome: height 22, thin ButtonLine outline, one label size; ActiveFill/ActiveInk come from the Button style.
    internal static void ToggleChrome(Button button,bool square){Square(button);button.FontSize=LabelSize;button.FontWeight=FontWeights.Normal;button.BorderThickness=new Thickness(1);button.SetResourceReference(Control.BorderBrushProperty,"ButtonLine");button.Focusable=false;button.Height=22;button.MinHeight=22;/* b41: RC8 geometry, 22 px box, border 1, padding 2 */if(square){button.Width=22;button.MinWidth=22;button.Padding=new Thickness(2);button.Margin=new Thickness(3,0,3,0);}else{button.Width=Double.NaN;button.MinWidth=SquareSize;button.Padding=new Thickness(7,0,10,0);}}
    internal static FrameworkElement QuoteIcon(){var icon=new Grid {Width=16,Height=16};var path=new Path {Data=Geometry.Parse("M 2,2 V 14 M 6.5,4.5 H 14 M 6.5,8 H 14 M 6.5,11.5 H 11"),StrokeThickness=1.5,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};path.SetResourceReference(Shape.StrokeProperty,"Ink");icon.Children.Add(path);return icon;}
    internal static FrameworkElement CodeIcon(){var icon=new Grid {Width=16,Height=16};var path=new Path {Data=Geometry.Parse("M 5,3.5 L 1,8 L 5,12.5 M 11,3.5 L 15,8 L 11,12.5 M 9.3,2.5 L 6.7,13.5"),StrokeThickness=1.5,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};path.SetResourceReference(Shape.StrokeProperty,"Ink");icon.Children.Add(path);return icon;}
    internal static FrameworkElement CloseIcon(){var path=new Path {Data=Geometry.Parse("M 0.75,0.75 L 11.25,11.25 M 11.25,0.75 L 0.75,11.25"),Width=12,Height=12,Margin=new Thickness(0,0,-1.2,0),StrokeThickness=1.5,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center};path.SetBinding(Shape.StrokeProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Button),1)});return path;}
    internal static FrameworkElement StrikeIcon(){var icon=new Grid {Width=18,Height=18};icon.Children.Add(new TextBlock {Text="S",FontFamily=new FontFamily("Segoe UI"),FontSize=14,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});var line=new Border {Height=1,Margin=new Thickness(1,0,1,0),VerticalAlignment=VerticalAlignment.Center};line.SetResourceReference(Border.BackgroundProperty,"Ink");icon.Children.Add(line);return icon;}
    internal static FrameworkElement CheckboxIcon(){var icon=new Grid {Width=15,Height=15};var path=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 2,2 L 13,2 L 13,13 L 2,13 Z M 4,7 L 6.5,10 L 11,5"),StrokeThickness=1.25,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Fill=Brushes.Transparent};path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Ink");icon.Children.Add(path);return icon;}
    internal static FrameworkElement RuleIcon(){var icon=new Grid {Width=18,Height=18};var path=new Path {Data=Geometry.Parse("M 2,9 L 16,9"),StrokeThickness=1.5};path.SetResourceReference(Shape.StrokeProperty,"Ink");icon.Children.Add(path);return icon;}
    internal static FrameworkElement OutlineIcon(){var icon=new Grid {Width=18,Height=18};var path=new Path {Data=Geometry.Parse("M 3,5 L 15,5 M 3,9 L 15,9 M 3,13 L 15,13"),StrokeThickness=1.6,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round};path.SetResourceReference(Shape.StrokeProperty,"Ink");icon.Children.Add(path);return icon;}
    internal static FrameworkElement ImageIcon(){var icon=new Grid {Width=18,Height=18};var path=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 1,2 L 17,2 L 17,16 L 1,16 Z M 2,14 L 7,8 L 11,12 L 14,9 L 17,13 M 12,5 A 1,1 0 1 0 14,5 A 1,1 0 1 0 12,5"),StrokeThickness=1.3};path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Ink");icon.Children.Add(path);return icon;}
    internal static Image GlyphImage(string glyph,int weight,bool dark,int width){var brush=Ui.Brush(dark?"#E7EBF2":"#263242");var text=new FormattedText(glyph,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(new FontFamily("Segoe UI"),FontStyles.Normal,FontWeight.FromOpenTypeWeight(weight),FontStretches.Normal),14,brush,1);var geometry=text.BuildGeometry(new Point());Rect bounds=geometry.Bounds;geometry.Transform=new TranslateTransform((width-bounds.Width)/2-bounds.Left,(18-bounds.Height)/2-bounds.Top);var group=new DrawingGroup();group.Children.Add(new GeometryDrawing(Brushes.Transparent,null,new RectangleGeometry(new Rect(0,0,width,18))));group.Children.Add(new GeometryDrawing(brush,weight>=800?new Pen(brush,(weight-700)/500.0):null,geometry));var source=new DrawingImage(group);source.Freeze();var image=new Image {Source=source,Width=width,Height=18,Tag=glyph};System.Windows.Automation.AutomationProperties.SetName(image,glyph);return image;}
    internal static Image HeadingIcon(int level,bool dark){int[] weights={600,600,600,600,600,600};return GlyphImage("H"+level,weights[System.Math.Max(0,System.Math.Min(5,level-1))],dark,20);}
    internal static Image ListIcon(bool number,bool dark){if(number){return GlyphImage("1",600,dark,14);}var group=new DrawingGroup();group.Children.Add(new GeometryDrawing(Brushes.Transparent,null,new RectangleGeometry(new Rect(0,0,14,18))));group.Children.Add(new GeometryDrawing(Brush(dark?"#E7EBF2":"#263242"),null,new EllipseGeometry(new Point(7,9),2.4,2.4)));var source=new DrawingImage(group);source.Freeze();return new Image {Source=source,Width=14,Height=18,Tag="bullet"};}
    // A real normal-width font avoids the hand-drawn 4.5:10 tall/narrow capitals.
    internal static FrameworkElement TxtIcon(){/* b42: the TXT toggle is a type "T" icon drawn like the dark-mode moon next to it */return ToggleIcon("type");}
    // One icon language for every bar: all outline paths merged and fitted into the same 16 px box, one 1.5 px stroke, the button's own foreground.
    internal const double BarIconSize=16;
    internal static void NormalizeBarIcons(DependencyObject root){foreach(object child in LogicalTreeHelper.GetChildren(root)){var node=child as DependencyObject;if(node==null)continue;var button=child as Button;if(button!=null){if(!Double.IsNaN(button.Width)&&button.Width<=14)continue;var icon=button.Content as FrameworkElement;var label=icon as StackPanel;if(label!=null&&label.Children.Count>0){var lead=label.Children[0] as FrameworkElement;var normal=lead==null?null:NormalizeIcon(lead);if(normal!=null){normal.Margin=new Thickness(0,0,4,0);label.Children.RemoveAt(0);label.Children.Insert(0,normal);}}else if(icon!=null&&!(icon is TextBlock)){var normal=NormalizeIcon(icon);if(normal!=null){button.Content=null;button.Content=normal;}}continue;}NormalizeBarIcons(node);}}
    internal static FrameworkElement NormalizeIcon(FrameworkElement icon){if(IsGrid(icon))return icon;var strokes=new GeometryGroup();var fills=new GeometryGroup();if(!CollectIconGeometry(icon,new Vector(),strokes,fills))return null;if(strokes.Children.Count>0&&fills.Children.Count>0)return null;bool fill=fills.Children.Count>0;var group=fill?fills:strokes;if(group.Children.Count==0)return null;var path=new Path {Data=group,Stretch=Stretch.Uniform,Width=BarIconSize,Height=BarIconSize,StrokeThickness=1.5,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,SnapsToDevicePixels=false};var ink=new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Button),1)};if(fill){path.Height=BarIconSize/2;path.SetBinding(Shape.FillProperty,ink);}else path.SetBinding(Shape.StrokeProperty,ink);return path;}
    static bool CollectIconGeometry(DependencyObject node,Vector offset,GeometryGroup strokes,GeometryGroup fills){var element=node as FrameworkElement;if(element!=null&&element.RenderTransform!=null&&!element.RenderTransform.Value.IsIdentity)return false;if(element is TextBlock||element is Image||element is Control)return false;var viewbox=node as Viewbox;if(viewbox!=null)return viewbox.Child==null||CollectIconGeometry(viewbox.Child,offset,strokes,fills);var path=node as Path;if(path!=null){if(path.Data==null)return true;var geometry=path.Data.Clone();if(offset.X!=0||offset.Y!=0){var moved=new GeometryGroup();moved.Children.Add(geometry);moved.Transform=new TranslateTransform(offset.X,offset.Y);geometry=moved;}bool hasStroke=path.ReadLocalValue(Shape.StrokeProperty)!=DependencyProperty.UnsetValue,hasFill=path.ReadLocalValue(Shape.FillProperty)!=DependencyProperty.UnsetValue&&!(path.Fill is SolidColorBrush&&((SolidColorBrush)path.Fill).Color.A==0);bool filled=hasFill&&!hasStroke;if(filled)fills.Children.Add(geometry);else strokes.Children.Add(geometry);return true;}var shape=node as Shape;if(shape!=null)return false;var panel=node as Panel;if(panel!=null){foreach(UIElement child in panel.Children){var item=child as FrameworkElement;if(item==null)continue;if(item.Visibility!=Visibility.Visible)continue;double left=panel is Canvas?Canvas.GetLeft(item):0,top=panel is Canvas?Canvas.GetTop(item):0;if(Double.IsNaN(left))left=0;if(Double.IsNaN(top))top=0;if(!CollectIconGeometry(item,offset+new Vector(left+item.Margin.Left,top+item.Margin.Top),strokes,fills))return false;}return true;}var decorator=node as Decorator;if(decorator!=null)return decorator.Child==null||CollectIconGeometry(decorator.Child,offset,strokes,fills);return false;}
    internal static FrameworkElement FitToolbarIcon(FrameworkElement icon){icon.Margin=new Thickness(0);return new Viewbox {Width=20,Height=20,Child=icon,Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};}
    // Reserve only the existing chevron's painted width; no change to its geometry, scale or stroke.
    /* b50: split arrows draw the chevron 25% larger (scale .75 -> .9375) with the stroke reduced to keep the same painted line weight.
       b51: another 13% (-> 1.0625) puts the drawn chevron at ~11 px wide inside the 18 px arrow segment, so the empty space either side
       of the arrow shrinks and it reads attached to its main button (user: too much empty space left/right of the arrow).
       stroke*scale stays 0.9375, so the painted line weight is unchanged. */
    internal const double SplitChevronScale=1.0625;
    internal const double SplitChevronStroke=0.9375/SplitChevronScale;
    internal static FrameworkElement TightChevron(){var box=new Grid {Width=(10+SplitChevronStroke)*SplitChevronScale,Height=15,VerticalAlignment=VerticalAlignment.Center};var icon=Chevron(SplitChevronScale,SplitChevronStroke);icon.HorizontalAlignment=HorizontalAlignment.Center;box.Children.Add(icon);return box;}
    // One chevron for every format-ribbon dropdown: same geometry, size, stroke and colour.
    internal static FrameworkElement Chevron(){return Chevron(SplitChevronScale,SplitChevronStroke);}
    internal static FrameworkElement Chevron(double scale,double stroke){var box=new Canvas {Width=12,Height=12,RenderTransformOrigin=new Point(0.5,0.5),RenderTransform=new ScaleTransform(scale,scale)};var path=new Path {Data=Geometry.Parse(ChevronGeometry),StrokeThickness=stroke,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};path.SetResourceReference(Shape.StrokeProperty,"Ink");box.Children.Add(path);return box;}
    // The four top-right command icons share one 22x22 button and a 16x16 glyph box.
    internal static void ApplyQuickCommandStyle(Button button,FrameworkElement glyph){button.Width=SquareSize;button.Height=SquareSize;button.MinWidth=SquareSize;button.MinHeight=SquareSize;button.Padding=new Thickness(4);button.Margin=new Thickness(1,0,1,0);button.Focusable=false;button.HorizontalContentAlignment=HorizontalAlignment.Center;button.VerticalContentAlignment=VerticalAlignment.Center;if(glyph!=null){glyph.Width=20;glyph.Height=20;glyph.HorizontalAlignment=HorizontalAlignment.Center;glyph.VerticalAlignment=VerticalAlignment.Center;}}
    // #12: command row and format ribbon scroll sideways on a thin overlay strip. The bar stays invisible
    // until the row is hovered, then a 4px (6px hovered) thumb overlays the bottom edge instead of taking a
    // layout row; the mouse wheel scrolls the row horizontally while it overflows.
    // b51: "invisible" means Opacity 0 and IsHitTestVisible false, not Collapsed — a narrow window used to show
    // a stray grey line under the buttons. The strip comes back and takes the pointer only while the row is
    // hovered (or a drag is in flight), so scrolling still works but nothing is painted at rest.
    internal static ScrollViewer SlimHorizontal(FrameworkElement content,Panel host){
        var scroll=new ScrollViewer {Content=content,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=Brushes.Transparent,Focusable=false};
        var bar=new ScrollBar {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Bottom,HorizontalAlignment=HorizontalAlignment.Stretch,Margin=new Thickness(6,0,6,0),Visibility=Visibility.Collapsed,Focusable=false,Opacity=0,IsHitTestVisible=false};
        bar.SetResourceReference(FrameworkElement.StyleProperty,"SlimBar");
        host.Children.Add(scroll);host.Children.Add(bar);
        bool syncing=false,rowHover=false,thumbHeld=false;
        Action reveal=delegate{
            bool over=scroll.ScrollableWidth>0.5;
            bool show=over&&(rowHover||thumbHeld);
            bar.Visibility=over?Visibility.Visible:Visibility.Collapsed;
            bar.Opacity=show?1:0;
            /* a hidden overlay must not eat clicks meant for the row underneath; it takes the pointer only while shown */
            bar.IsHitTestVisible=show;
        };        Action sync=delegate{
            if(!(scroll.ScrollableWidth>0.5)){reveal();return;}
            syncing=true;
            try{bar.Minimum=0;bar.Maximum=scroll.ScrollableWidth;bar.ViewportSize=scroll.ViewportWidth;bar.LargeChange=System.Math.Max(1,scroll.ViewportWidth);bar.SmallChange=16;bar.Value=scroll.HorizontalOffset;}finally{syncing=false;}
            reveal();
        };
        scroll.ScrollChanged+=delegate(object sender,ScrollChangedEventArgs e){sync();};
        scroll.Loaded+=delegate(object sender,RoutedEventArgs e){sync();};
        bar.ValueChanged+=delegate(object sender,RoutedPropertyChangedEventArgs<double> e){if(!syncing)scroll.ScrollToHorizontalOffset(bar.Value);};
        scroll.PreviewMouseWheel+=delegate(object sender,MouseWheelEventArgs e){if((Keyboard.Modifiers&ModifierKeys.Control)!=0||scroll.ScrollableWidth<=0.5)return;scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset-e.Delta);e.Handled=true;};
        /* Hover is tracked on the host, not on the scrolling row: the strip is a sibling overlay laid
           over the row's bottom edge, so entering it raised MouseLeave on the row, which hid the strip,
           which raised MouseEnter on the row again - a flicker that also lost the click that was meant
           for the thumb. The host contains both, so its enter/leave is stable while the pointer is
           anywhere in the row or on the strip. */
        host.MouseEnter+=delegate{rowHover=true;reveal();};
        host.MouseLeave+=delegate{rowHover=false;reveal();};
        /* a thumb drag that leaves the row keeps the strip painted until the drag ends */
        host.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler(delegate(object sender,DragStartedEventArgs e){thumbHeld=true;reveal();}),true);
        host.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler(delegate(object sender,DragCompletedEventArgs e){thumbHeld=false;reveal();}),true);
        return scroll;
    }
    internal static FrameworkElement ParagraphAfterTableIcon(){var icon=new Grid {Width=22,Height=18};var shape=new Path {Data=Geometry.Parse("M 1,1 H 17 V 7 H 1 Z M 6,1 V 7 M 12,1 V 7 M 3,11 V 16 H 10 M 7,13 L 10,16 L 7,18 M 14,13 H 21 M 14,17 H 21"),StrokeThickness=1.3,StrokeLineJoin=PenLineJoin.Round};shape.SetResourceReference(Shape.StrokeProperty,"Ink");icon.Children.Add(shape);return icon;}
    internal static FrameworkElement TableActionIcon(int action){var icon=new Grid {Width=22,Height=18};string shape=action<2?"M 1,2 H 17 V 9 H 1 Z M 6.3,2 V 9 M 11.6,2 V 9":action<4?"M 1,1 H 9 V 17 H 1 Z M 1,6.3 H 9 M 1,11.6 H 9":"M 1,1 H 17 V 17 H 1 Z M 1,6 H 17 M 1,12 H 17 M 6,1 V 17 M 12,1 V 17";var grid=new Path {Data=Geometry.Parse(shape),StrokeThickness=1.2};grid.SetResourceReference(Shape.StrokeProperty,"Ink");icon.Children.Add(grid);string mark=action==4?"M 5,5 L 14,14 M 14,5 L 5,14":action==0||action==2?"M 12,13 H 21 M 16.5,8.5 V 17.5":"M 12,13 H 21";var badge=new Path {Data=Geometry.Parse(mark),StrokeThickness=2.3,StrokeStartLineCap=PenLineCap.Square,StrokeEndLineCap=PenLineCap.Square};badge.SetResourceReference(Shape.StrokeProperty,action==0||action==2?"TableAddMark":"TableRemoveMark");icon.Children.Add(badge);return icon;}
    internal static FrameworkElement TableIcon(){var icon=new Grid {Width=18,Height=18};var path=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 1,2 L 17,2 L 17,16 L 1,16 Z M 1,7 L 17,7 M 1,11 L 17,11 M 6,2 L 6,16 M 11,2 L 11,16"),StrokeThickness=1.3};path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Ink");icon.Children.Add(path);return icon;}
    internal static FrameworkElement LinkIcon(){var icon=new Grid {Width=18,Height=18};var chain=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 7,6 L 10,3 C 13,-0.5 18,4 14.5,7.5 L 11,11 C 9,13 6,13 5,11 M 11,11 L 8,14 C 4.5,17.5 0,13 3.5,9.5 L 7,6 C 9,4 12,4 13,6 M 6,11 L 12,5"),StrokeThickness=1.3,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};chain.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Ink");icon.Children.Add(chain);return icon;}
    internal static FrameworkElement HighlighterIcon(){var icon=new Grid {Width=18,Height=18};var body=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 4,2 L 14,2 L 13,9 L 5,9 Z M 5,9 L 7,12 L 11,12 L 13,9 M 7,12 L 7,15 L 11,13 L 11,12"),StrokeThickness=1.35,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Fill=Brushes.Transparent};body.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty,"Ink");icon.Children.Add(body);var nib=new System.Windows.Shapes.Path {Data=Geometry.Parse("M 7,12 L 7,15 L 11,13 L 11,12 Z")};nib.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,"Ink");icon.Children.Add(nib);return icon;}
    // One divider rule for every bar: 1x16 line, 6 px from what the eye sees on each side (ink of borderless content, the border of outlined controls). b51: 8 -> 6, the ribbon was too wide.
    internal const double DividerGap=6;
    internal static Border Divider(){return Divider(DividerGap);}
    internal static Border Divider(double gap){var line=new Border {Width=1,Height=16,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(gap,0,gap,0)};line.SetResourceReference(Border.BackgroundProperty,"Line");return line;}
    internal static void BalanceDivider(Border divider,FrameworkElement before,FrameworkElement after,FrameworkElement ancestor){BalanceDivider(divider,before,after,ancestor,DividerGap);}
    internal static void BalanceDivider(Border divider,FrameworkElement before,FrameworkElement after,FrameworkElement ancestor,double gap){double previousBefore=-1,previousAfter=-1,previousAncestor=-1,previousStart=double.NaN;divider.LayoutUpdated+=delegate{if(!Shown(divider)||!Shown(before)||!Shown(after))return;/* neighbours can move without changing width when an earlier divider adjusts, so the start position is part of the key */double start;try{start=before.TransformToAncestor(ancestor).Transform(new Point()).X;}catch(InvalidOperationException){return;}catch(ArgumentException){return;}if(previousBefore==before.ActualWidth&&previousAfter==after.ActualWidth&&previousAncestor==ancestor.ActualWidth&&previousStart==start)return;Rect left=VisibleEdges(before,ancestor,false),right=VisibleEdges(after,ancestor,true);if(left.IsEmpty||right.IsEmpty)return;previousBefore=before.ActualWidth;previousAfter=after.ActualWidth;previousAncestor=ancestor.ActualWidth;previousStart=start;double x;try{x=divider.TransformToAncestor(ancestor).Transform(new Point()).X;}catch(InvalidOperationException){return;}catch(ArgumentException){return;}/* equal gaps on both sides, never below the floor: a side that cannot shrink sets the gap for both */double baseLeft=(x-left.Right)-divider.Margin.Left,baseRight=(right.Left-x-divider.ActualWidth)-divider.Margin.Right,target=System.Math.Max(gap,System.Math.Max(baseLeft,baseRight));double marginLeft=target-baseLeft,marginRight=target-baseRight;if(System.Math.Abs(marginLeft-divider.Margin.Left)>.4||System.Math.Abs(marginRight-divider.Margin.Right)>.4){divider.Margin=new Thickness(marginLeft,divider.Margin.Top,marginRight,divider.Margin.Bottom);previousBefore=-1;}};}
    static bool Shown(FrameworkElement element){return element.Visibility==Visibility.Visible&&element.ActualWidth>0&&element.ActualHeight>0;}
    /* The edge the eye sees: a panel's first/last visible child, an outlined control's border, a menu item's header text, else the ink of the content. */
    /* b56: a neighbour can leave this ancestor between layout passes - the narrow top row moves a whole menu
       item into the "»" popup, and TransformToAncestor throws InvalidOperationException for it. That pass
       reports "no edge" so the caller skips it; the exception used to close the app (dark, 450 px, click "»"). */
    internal static Rect VisibleEdges(FrameworkElement element,Visual ancestor,bool leading){var panel=element as Panel;if(panel!=null){int count=panel.Children.Count;for(int k=0;k<count;k++){var child=panel.Children[leading?k:count-1-k] as FrameworkElement;if(child==null||!Shown(child))continue;var rect=VisibleEdges(child,ancestor,leading);if(!rect.IsEmpty)return rect;}return Rect.Empty;}/* a control whose template is not applied yet has no ink to measure: report nothing so the caller retries instead of caching the box */if(element is Control&&VisualTreeHelper.GetChildrenCount(element)==0)return Rect.Empty;Rect box;try{box=element.TransformToAncestor(ancestor).TransformBounds(new Rect(0,0,element.ActualWidth,element.ActualHeight));}catch(InvalidOperationException){return Rect.Empty;}catch(ArgumentException){return Rect.Empty;}var comboBox=element as ComboBox;if(comboBox!=null)return ComboInk(comboBox,ancestor);var control=element as Control;if(control!=null&&!(element is MenuItem)){var brush=control.BorderBrush as SolidColorBrush;if(control.BorderThickness.Left>0&&brush!=null&&brush.Color.A>0)return box;}var menuItem=element as MenuItem;Visual source=menuItem!=null?(Visual)FindHeaderPresenter(menuItem,menuItem.Header):FindPresenter(element);if(source==null)source=element;Rect ink=VisualTreeHelper.GetDescendantBounds(source);if(ink.IsEmpty)return Rect.Empty;return source.TransformToAncestor(ancestor).TransformBounds(ink);}
    /* A borderless combo reads as "text + chevron": its visible edges are the right-aligned text and the chevron, not its 64 px box. */
    internal static Rect ComboInk(ComboBox combo,Visual ancestor){var chevron=FindDescendant<Path>(combo);if(chevron==null)return Rect.Empty;Rect arrow=chevron.TransformToAncestor(ancestor).TransformBounds(VisualTreeHelper.GetDescendantBounds(chevron));Rect box=combo.TransformToAncestor(ancestor).TransformBounds(new Rect(0,0,combo.ActualWidth,combo.ActualHeight));double left=arrow.Left;var edit=FindDescendant<TextBox>(combo);if(edit!=null&&edit.Visibility==Visibility.Visible&&edit.ActualWidth>0&&!String.IsNullOrEmpty(combo.Text)){Rect field=edit.TransformToAncestor(ancestor).TransformBounds(new Rect(0,0,edit.ActualWidth,edit.ActualHeight));var text=new FormattedText(combo.Text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface(combo.FontFamily,combo.FontStyle,combo.FontWeight,combo.FontStretch),combo.FontSize,Brushes.Black,1.0);left=System.Math.Min(left,field.Right-edit.Padding.Right-text.WidthIncludingTrailingWhitespace);}return new Rect(left,box.Top,System.Math.Max(0,arrow.Right-left),box.Height);}
    static T FindDescendant<T>(DependencyObject element) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++){var child=VisualTreeHelper.GetChild(element,i);var hit=child as T;if(hit!=null)return hit;var nested=FindDescendant<T>(child);if(nested!=null)return nested;}return null;}
    static void BalanceGroup(Border border,StackPanel body){if(!border.IsVisible||border.ActualWidth<=0||body.Children.Count==0)return;Rect bounds=Rect.Empty;foreach(UIElement child in body.Children){var element=child as FrameworkElement;if(element==null||!element.IsVisible)continue;FrameworkElement visible=element;if(element is Button){var presenter=FindPresenter(element);if(presenter!=null)visible=presenter;}if(visible.ActualWidth<=0)continue;var position=visible.TransformToAncestor(border).Transform(new Point());bounds.Union(new Rect(position,new Size(visible.ActualWidth,visible.ActualHeight)));}if(bounds.IsEmpty)return;double left=System.Math.Max(0,border.Padding.Left+1-bounds.Left),right=System.Math.Max(0,border.Padding.Right+1-(border.ActualWidth-bounds.Right));if(System.Math.Abs(left-border.Padding.Left)>.6||System.Math.Abs(right-border.Padding.Right)>.6)border.Padding=new Thickness(left,2,right,2);}
    static ContentPresenter FindHeaderPresenter(DependencyObject element,object header){for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++){var child=VisualTreeHelper.GetChild(element,i);var presenter=child as ContentPresenter;if(presenter!=null&&presenter.ActualWidth>0&&Object.Equals(presenter.Content,header))return presenter;var nested=FindHeaderPresenter(child,header);if(nested!=null)return nested;}return null;}
    static ContentPresenter FindPresenter(DependencyObject element){for(int i=0;i<VisualTreeHelper.GetChildrenCount(element);i++){var child=VisualTreeHelper.GetChild(element,i);var presenter=child as ContentPresenter;if(presenter!=null)return presenter;var nested=FindPresenter(child);if(nested!=null)return nested;}return null;}
    // Ribbon groups carry no border of their own; a shared divider sits between consecutive groups.
    internal static Border Group(WrapPanel ribbon,string title,out StackPanel body) {
        return Group(ribbon,title,DividerGap,out body);
    }
    /* b52: the format ribbon passes its own (narrower) rule gap; the top bar keeps the shared 6 px. */
    internal static Border Group(WrapPanel ribbon,string title,double gap,out StackPanel body) {
        body=new StackPanel {Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Height=28,Tag="GroupBody"};
        StackPanel previous=null;if(ribbon.Children.Count>0){var last=ribbon.Children[ribbon.Children.Count-1] as Border;if(last!=null)previous=last.Child as StackPanel;}
        if(previous!=null){var line=Divider(gap);ribbon.Children.Add(line);BalanceDivider(line,previous,body,ribbon,gap);}
        var border=new Border {Child=body,Padding=new Thickness(0,4,0,4)};ribbon.Children.Add(border);return border;
    }
// The line box includes ascent/descent room which is not painted. Move only the rendered caption,
// leaving its advance, measure, hit target and horizontal position unchanged. Bindings re-evaluate
// on caption/font/size changes, not on editor caret or keystroke callbacks.
internal sealed class ChromeInkConverter : IMultiValueConverter {
    static readonly System.Collections.Generic.Dictionary<string,double> centres=new System.Collections.Generic.Dictionary<string,double>();
    internal static double InkCentre(FormattedText formatted,string key){
        double centre;if(centres.TryGetValue(key,out centre))return centre;
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())dc.DrawText(formatted,new Point(4,4));
        int width=Math.Max(1,(int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace)+8),height=Math.Max(1,(int)Math.Ceiling(formatted.Height)+8);
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var pixels=new byte[width*height*4];bitmap.CopyPixels(pixels,width*4,0);int top=height,bottom=-1;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(pixels[(y*width+x)*4+3]>=64){top=Math.Min(top,y);bottom=Math.Max(bottom,y);}
        Rect ink=formatted.BuildGeometry(new Point()).Bounds;centre=bottom<top?ink.Top+ink.Height/2:(top+bottom+1)/2.0-4;
        if(centres.Count>=256)centres.Clear();centres[key]=centre;return centre;
    }
    public object Convert(object[] values,Type targetType,object parameter,System.Globalization.CultureInfo culture){
        if(values.Length<2||!(values[1] is double))return Transform.Identity;
        var element=values[0] as FrameworkElement;var block=element as TextBlock;var field=element as TextBox;var access=element as AccessText;
        if(block==null&&access==null&&(field==null||!(field.TemplatedParent is ComboBox)))return Transform.Identity;
        if(block!=null){if(Object.Equals(block.Tag,Ui.KeepFontTag)||VisualTreeHelper.GetParent(block) is AccessText)return Transform.Identity;DependencyObject owner=block;while(owner!=null&&!(owner is ButtonBase)&&!(owner is MenuItem))owner=VisualTreeHelper.GetParent(owner);if(owner==null)return Transform.Identity;}
        string text=block!=null?block.Text:access!=null?(access.Text==null?null:access.Text.Replace("_","")):(field==null?null:field.Text);if(String.IsNullOrEmpty(text))return Transform.Identity;
        FontFamily family=System.Windows.Documents.TextElement.GetFontFamily(element);double size=System.Windows.Documents.TextElement.GetFontSize(element);if(family==null||size<=0)return Transform.Identity;
        var face=new Typeface(family,System.Windows.Documents.TextElement.GetFontStyle(element),System.Windows.Documents.TextElement.GetFontWeight(element),System.Windows.Documents.TextElement.GetFontStretch(element));
        var formatted=new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,size,Brushes.Black,null,TextFormattingMode.Display,1.0);
        string key=family.Source+"|"+size+"|"+face.Style+"|"+face.Weight+"|"+face.Stretch+"|"+text;
        if(block!=null){int at=0;foreach(var inline in block.Inlines){var run=inline as System.Windows.Documents.Run;if(run==null)continue;if(at+run.Text.Length<=text.Length)formatted.SetFontWeight(run.FontWeight,at,run.Text.Length);key+="|"+run.Text.Length+":"+run.FontWeight;at+=run.Text.Length;}}
        double height=field==null?(double)values[1]:formatted.Height;
        double offset=height/2-InkCentre(formatted,key);if(field!=null)offset+=(field.Padding.Bottom-field.Padding.Top)/2;
        return new TranslateTransform(0,offset);
    }
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,System.Globalization.CultureInfo culture){throw new NotSupportedException();}
}
}
/* 2.0 (user, 11-01): in a very long document the scroll bar thumb was cut to a half circle. WPF's Track
   sizes the thumb from ViewportSize and only floors it at half a system button (~8 px), so the Thumb's
   own MinHeight 28 / MinWidth 24-28 made it overflow its slot and the layout clip cut it. The Track's
   ViewportSize is raised just enough that the thumb gets its MinHeight/MinWidth; Value, Maximum and the
   ScrollBar's own ViewportSize (page up/down) are untouched, so positions and dragging stay the same. */
internal static class ScrollThumbFloor {
    static bool installed;
    internal static void Install(){
        if(installed)return;installed=true;
        /* SizeChanged, not Loaded: the Loaded broadcast skips elements that have no instance handler, so a
           Loaded class handler never ran for the editor's bars. SizeChanged is raised on the bar itself when
           it first gets a size (and on resizes, where the check below returns at once). */
        EventManager.RegisterClassHandler(typeof(ScrollBar),FrameworkElement.SizeChangedEvent,new SizeChangedEventHandler(BarSized));
    }
    static void BarSized(object sender,SizeChangedEventArgs e){
        var bar=sender as ScrollBar;if(bar==null)return;
        bar.ApplyTemplate();var track=bar.Track;
        if(track==null||BindingOperations.GetMultiBinding(track,Track.ViewportSizeProperty)!=null)return;
        bool vertical=bar.Orientation==Orientation.Vertical;
        var binding=new MultiBinding();binding.Converter=new FloorConverter(track,vertical);
        binding.Bindings.Add(new Binding("ViewportSize") {Source=bar});
        binding.Bindings.Add(new Binding("Minimum") {Source=bar});
        binding.Bindings.Add(new Binding("Maximum") {Source=bar});
        binding.Bindings.Add(new Binding(vertical?"ActualHeight":"ActualWidth") {Source=track});
        track.SetBinding(Track.ViewportSizeProperty,binding);
    }
    sealed class FloorConverter : IMultiValueConverter {
        readonly Track track;readonly bool vertical;
        internal FloorConverter(Track track,bool vertical){this.track=track;this.vertical=vertical;}
        public object Convert(object[] values,Type targetType,object parameter,System.Globalization.CultureInfo culture){
            if(values==null||values.Length<4||!(values[0] is double))return double.NaN;
            double viewport=(double)values[0];
            if(!(values[1] is double)||!(values[2] is double)||!(values[3] is double))return viewport;
            double range=(double)values[2]-(double)values[1],length=(double)values[3];
            if(double.IsNaN(viewport)||viewport<0||range<=0||length<=0)return viewport;
            var thumb=track.Thumb;if(thumb==null)return viewport;
            double floor=vertical?thumb.MinHeight:thumb.MinWidth;
            if(floor<=0||length<=floor*1.5)return viewport;
            /* thumb = length*v/(range+v) >= floor  <=>  v >= floor*range/(length-floor) */
            double need=floor*range/(length-floor);
            return viewport<need?need:viewport;
        }
        public object[] ConvertBack(object value,Type[] targetTypes,object parameter,System.Globalization.CultureInfo culture){throw new NotSupportedException();}
    }
}
}
