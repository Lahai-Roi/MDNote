using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
namespace MDNote {
/* Probe seam for the new-version dialog: (version, release link) -> true when the release page should be opened. */
internal delegate bool UpdateOfferDialog(string version,string url);
/* 2.1.x: the "업데이트 확인" help item. 2.2.0 adds the automatic check: once a day, on the first launch of the day,
   when the "업데이트 자동 확인" help item is on (default). These two are the only code in the app that touches the
   network. Nothing is downloaded or installed: a newer version only opens a dialog with the GitHub release link. */
public partial class MainWindow {
    void CheckForUpdates(){UpdateChecker.Run(this);}
    /* The result arrives from a dispatcher callback after the Help menu has closed. The menu bar can still hold the
       keyboard (it stays in its accent state and Enter would open File behind the dialog), so the editor takes the
       focus first, which ends the menu's keyboard mode; the dialog then owns the keyboard, and the editor gets it
       back when the dialog closes. */
    internal void UpdateDialogOpening(){try{FocusActiveSource();}catch(Exception){}}
    internal void UpdateDialogClosed(){try{if(IsLoaded)FocusActiveSource();}catch(Exception){}}
    /* Preferences shared by every window (Preferences.cs saves them): the automatic check is on unless the user turned
       it off; updateCheckedDate is the local day (yyyy-MM-dd) of the last automatic attempt; skippedUpdateVersion is
       the version whose announcement the user switched off (only a newer release is announced again). */
    internal static bool? autoUpdateChoice;
    /* Off unless switched on: the user's own choice (Help menu, stored for on and off) wins; with none stored the
       installer's "업데이트 자동 확인" tick decides (HKLM\Software\MDNote AutoUpdateCheck = 1). A portable or
       development build has no installer value and stays off. */
    internal static bool autoUpdateCheck{get{return autoUpdateChoice.HasValue?autoUpdateChoice.Value:UpdateChecker.InstallerAutoUpdate();}set{autoUpdateChoice=value;}}
    internal static string updateCheckedDate="",skippedUpdateVersion="";
    MenuItem autoUpdateToggle;
    void SetupAutoUpdateCheck(){Loaded+=delegate{UpdateChecker.ScheduleAuto(this);};}
    void SetAutoUpdateCheck(bool value){
        autoUpdateCheck=value;if(autoUpdateToggle!=null)autoUpdateToggle.IsChecked=value;
        var application=Application.Current;
        if(application!=null)foreach(Window window in application.Windows){
            var other=window as MainWindow;
            if(other!=null&&!Object.ReferenceEquals(other,this)&&other.autoUpdateToggle!=null)other.autoUpdateToggle.IsChecked=value;
        }
        SavePreferences();
    }
    internal void SaveUpdatePreferences(){SavePreferences();}
    /* The automatic check never opens a dialog (it would interrupt typing): a newer release shows this one-line strip
       directly above the status bar. The strip is a row of the window's DockPanel, so the editor shrinks by its
       height; nothing in it takes the keyboard focus (the caret stays where it was). The close button hides it for
       this session only. [자세히] opens the same dialog as the manual check. The "이 버전은 다시 알리지 않음" tick box (the only
       place that choice exists) sits at the right, next to the close button: ticking it stores the version and saves the
       preferences at once (the strip stays); unticking clears the stored version when it is this one. Every window that
       shows the strip follows the box. It never takes the keyboard focus. */
    Border updateStrip;TextBlock updateStripText;CheckBox updateStripSkipBox;string updateStripVersion;bool updateStripSyncing;
    static ControlTemplate updateSkipBoxTemplate;
    static ControlTemplate UpdateSkipBoxTemplate(){
        if(updateSkipBoxTemplate==null)updateSkipBoxTemplate=(ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='CheckBox'>"
            +"<StackPanel Orientation='Horizontal' Background='Transparent' SnapsToDevicePixels='True'>"
            +"<Border x:Name='Box' Width='14' Height='14' BorderThickness='1' CornerRadius='3' VerticalAlignment='Center' BorderBrush='{DynamicResource Muted}' Background='{DynamicResource Paper}'>"
            +"<Path x:Name='Mark' Data='M2,6 L4.8,8.8 L10,3' Stroke='{DynamicResource Accent}' StrokeThickness='1.7' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Visibility='Collapsed'/></Border>"
            +"<ContentPresenter Margin='6,0,0,0' VerticalAlignment='Center'/></StackPanel>"
            +"<ControlTemplate.Triggers>"
            +"<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='{DynamicResource Ink}'/></Trigger>"
            +"<Trigger Property='IsChecked' Value='True'><Setter TargetName='Mark' Property='Visibility' Value='Visible'/><Setter TargetName='Box' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger>"
            +"</ControlTemplate.Triggers></ControlTemplate>");
        return updateSkipBoxTemplate;
    }
    internal bool UpdateStripVisible{get{return updateStrip!=null&&updateStrip.Visibility==Visibility.Visible;}}
    internal string UpdateStripVersion{get{return UpdateStripVisible?updateStripVersion:null;}}
    internal string UpdateStripText{get{return UpdateStripVisible&&updateStripText!=null?TextOf(updateStripText):null;}}
    static string TextOf(TextBlock block){var builder=new StringBuilder();foreach(var inline in block.Inlines){var run=inline as System.Windows.Documents.Run;if(run!=null)builder.Append(run.Text);else{var link=inline as System.Windows.Documents.Hyperlink;if(link!=null)foreach(var part in link.Inlines){var linkRun=part as System.Windows.Documents.Run;if(linkRun!=null)builder.Append(linkRun.Text);}}}return builder.ToString();}
    /* The app's Button style (what the tab close button uses) with a muted x that turns to Ink while the pointer is over it. */
    Style UpdateStripCloseStyle(){
        var style=new Style(typeof(Button),TryFindResource(typeof(Button)) as Style);
        style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Muted")));
        var hover=new Trigger {Property=UIElement.IsMouseOverProperty,Value=true};
        hover.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Ink")));
        style.Triggers.Add(hover);
        return style;
    }
    void BuildUpdateStrip(){
        var text=new TextBlock {VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
        text.SetResourceReference(TextBlock.ForegroundProperty,"Ink");
        /* The x is the tab close button's look (app Button style: Hover fill, Pressed fill, radius 6) in a 26x24 box. The glyph is a drawn
           8x8 cross (user 11-05: 10x10 looked bigger than the text; a text "×" sits about 3 px low in its line), centred in the box by the content presenter, stroke bound to the
           button's Foreground so the hover ink applies. Its left margin (10) equals the strip's right padding (10), so the space
           left of the box (checkbox text to box) and right of it (box to strip edge) are equal and so are the gaps to the visible x. */
        var close=new Button {Focusable=false,Width=26,Height=24,Padding=new Thickness(0),Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center,Style=UpdateStripCloseStyle()};
        var cross=new System.Windows.Shapes.Path {Data=System.Windows.Media.Geometry.Parse("M0.65,0.65 L7.35,7.35 M7.35,0.65 L0.65,7.35"),Width=8,Height=8,Stretch=System.Windows.Media.Stretch.None,StrokeThickness=1.3,StrokeStartLineCap=System.Windows.Media.PenLineCap.Round,StrokeEndLineCap=System.Windows.Media.PenLineCap.Round,IsHitTestVisible=false};
        cross.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new System.Windows.Data.Binding("Foreground") {Source=close});
        close.Content=cross;
        close.Click+=delegate{HideUpdateStrip();};
        DockPanel.SetDock(close,Dock.Right);
        var skipBox=new CheckBox {Content=TranslateLabel("이 버전은 다시 알리지 않음"),Focusable=false,IsTabStop=false,Template=UpdateSkipBoxTemplate(),Margin=new Thickness(12,0,0,0),VerticalAlignment=VerticalAlignment.Center,Cursor=System.Windows.Input.Cursors.Hand};
        skipBox.SetResourceReference(Control.ForegroundProperty,"Ink");
        skipBox.Checked+=delegate{UpdateStripSkipChanged(true);};
        skipBox.Unchecked+=delegate{UpdateStripSkipChanged(false);};
        DockPanel.SetDock(skipBox,Dock.Right);
        var row=new DockPanel {LastChildFill=true};row.Children.Add(close);row.Children.Add(skipBox);row.Children.Add(text);
        var strip=new Border {Child=row,Padding=new Thickness(14,3,10,3),BorderThickness=new Thickness(0,1,0,0),Visibility=Visibility.Collapsed,Focusable=false};
        strip.SetResourceReference(Border.BackgroundProperty,"Ribbon");strip.SetResourceReference(Border.BorderBrushProperty,"Line");
        DockPanel.SetDock(strip,Dock.Bottom);
        int at=statusBar!=null?root.Children.IndexOf(statusBar):-1;
        if(at<0)return;
        root.Children.Insert(at+1,strip);
        updateStrip=strip;updateStripText=text;updateStripSkipBox=skipBox;
    }
    internal void ShowUpdateStrip(Version current,Version latest,string url){
        if(updateStrip==null)BuildUpdateStrip();
        if(updateStrip==null)return;
        updateStripVersion=latest.ToString();
        updateStripText.Inlines.Clear();
        updateStripText.Inlines.Add(new System.Windows.Documents.Run(String.Format(TranslateLabel("새 버전({0})이 나왔습니다."),latest)+"  "));
        var details=new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(TranslateLabel("자세히"))) {Focusable=false};
        details.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Accent");
        details.Click+=delegate{ClickUpdateStripDetails(current,latest,url);};
        updateStripText.Inlines.Add(details);
        updateStripSkipBox.Content=TranslateLabel("이 버전은 다시 알리지 않음");
        SetUpdateStripSkipBox(skippedUpdateVersion==updateStripVersion);
        updateStrip.Visibility=Visibility.Visible;
    }
    internal void HideUpdateStrip(){if(updateStrip!=null)updateStrip.Visibility=Visibility.Collapsed;}
    void SetUpdateStripSkipBox(bool on){
        if(updateStripSkipBox==null)return;
        updateStripSyncing=true;
        try{updateStripSkipBox.IsChecked=on;}finally{updateStripSyncing=false;}
    }
    /* The box was ticked or unticked: stores (or clears) the version at once and saves; the strip stays. */
    void UpdateStripSkipChanged(bool on){
        if(updateStripSyncing||updateStripVersion==null)return;
        string version=updateStripVersion;
        if(on)skippedUpdateVersion=version;
        else if(skippedUpdateVersion==version)skippedUpdateVersion="";
        try{SaveUpdatePreferences();}catch(Exception){}
        var application=Application.Current;
        if(application!=null)foreach(Window window in application.Windows){
            var other=window as MainWindow;
            if(other!=null&&!Object.ReferenceEquals(other,this)&&other.updateStripVersion==version)other.SetUpdateStripSkipBox(on);
        }
    }
    void ClickUpdateStripDetails(Version current,Version latest,string url){UpdateChecker.ShowDetails(this,current,latest,url);}
    /* Probe entry points: the same code the two controls run. */
    internal void ClickUpdateStripDetails(){ClickUpdateStripLink(0);}
    internal void ClickUpdateStripSkip(){if(updateStripSkipBox!=null)updateStripSkipBox.IsChecked=updateStripSkipBox.IsChecked!=true;}
    internal bool UpdateStripSkipChecked{get{return updateStripSkipBox!=null&&updateStripSkipBox.IsChecked==true;}}
    internal string UpdateStripSkipText{get{return updateStripSkipBox==null?null:updateStripSkipBox.Content as string;}}
    void ClickUpdateStripLink(int index){if(updateStripText==null)return;int seen=0;foreach(var inline in updateStripText.Inlines){var link=inline as System.Windows.Documents.Hyperlink;if(link!=null){if(seen==index){link.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent,link));return;}seen++;}}}
    internal int UpdateStripLinkCount{get{int count=0;if(updateStripText!=null)foreach(var inline in updateStripText.Inlines)if(inline is System.Windows.Documents.Hyperlink)count++;return count;}}
    internal void ClickUpdateStripClose(){HideUpdateStrip();}
}
static class UpdateChecker {
    const string RepoPrefix="https://github.com/Lahai-Roi/MDNote/";
    const string ReleasesPage="https://github.com/Lahai-Roi/MDNote/releases/latest";
    const string LatestApi="https://api.github.com/repos/Lahai-Roi/MDNote/releases/latest";
    /* Probe seams (the update probe never touches the network, a dialog or a browser). */
    internal static Func<string> ReleaseJsonHook;
    internal static UpdateOfferDialog OfferHook;
    internal static Action<string> NoticeHook,OpenPageHook;
    internal static Func<DateTime> NowHook;
    internal static bool ProbeDrive;
    /* Probe seam: receives the built dialog window once it is rendered, before the user could act (the probe photographs and closes it). */
    internal static Action<Window> DialogShownHook;
    /* Probe seam for the installer's value: null = the value is missing. */
    internal static Func<int?> InstallerValueHook;
    static bool installerRead;static bool installerOn;
    internal static bool InstallerAutoUpdate(){
        Func<int?> hook=InstallerValueHook;
        if(hook!=null){int? probed=hook();return probed.HasValue&&probed.Value==1;}
        if(!installerRead){
            installerRead=true;
            try{
                using(var baseKey=Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine,Microsoft.Win32.RegistryView.Registry64))
                using(var key=baseKey.OpenSubKey("Software\\MDNote")){
                    object value=key==null?null:key.GetValue("AutoUpdateCheck");
                    installerOn=value is int&&(int)value==1;
                }
            }catch(Exception){installerOn=false;}
        }
        return installerOn;
    }
    /* Counts finished checks (automatic or manual, shown or not); the probe pumps until it moves. */
    internal static int Completed;
    /* UI thread only: a second click while the first request is running does nothing. */
    static bool inFlight,autoScheduled;
    internal static void ResetForProbe(){inFlight=false;autoScheduled=false;}
    internal static Version CurrentVersion(){return Normalize(Assembly.GetExecutingAssembly().GetName().Version);}
    internal static void Run(MainWindow window){Start(window,false);}
    static void Start(MainWindow window,bool auto){
        if(inFlight){if(auto)Completed++;return;}
        inFlight=true;
        try{
            Version current=CurrentVersion();
            Task.Run(new Action(delegate{
                Version latest=null;string url=null;bool ok=false;
                try{ok=Fetch(out latest,out url);}catch(Exception){ok=false;}
                try{
                    window.Dispatcher.BeginInvoke(new Action(delegate{
                        try{Deliver(window,auto,ok,current,latest,url);}catch(Exception){}
                        finally{inFlight=false;Completed++;}
                    }));
                }catch(Exception){inFlight=false;Completed++;}
            }));
        }catch(Exception){inFlight=false;Completed++;}
    }
    /* The window that started the check may be gone by now: use another open one, or show nothing. */
    static MainWindow PickOwner(MainWindow window){
        MainWindow owner=UsableOwner(window);
        if(owner==null&&Application.Current!=null){
            foreach(Window other in Application.Current.Windows){
                MainWindow candidate=other as MainWindow;
                if(candidate!=null&&UsableOwner(candidate)!=null){owner=candidate;break;}
            }
        }
        return owner;
    }
    static void Deliver(MainWindow window,bool auto,bool ok,Version current,Version latest,string url){
        if(auto){
            /* Silent unless there is something new that the user has not switched off. */
            if(!ok||latest==null||latest.CompareTo(current)<=0)return;
            if(IsSkipped(MainWindow.skippedUpdateVersion,latest))return;
            /* No dialog: the strip above the status bar (MainWindow.ShowUpdateStrip). */
            MainWindow stripOwner=PickOwner(window);
            if(stripOwner!=null)stripOwner.ShowUpdateStrip(current,latest,url);
            return;
        }
        MainWindow owner=PickOwner(window);
        if(owner!=null)Present(owner,ok,current,latest,url);
    }
    /* The window itself when it is still open and shown (loaded, with a presentation source), else null: a closed window must never become an Owner. */
    static MainWindow UsableOwner(MainWindow window){
        try{
            if(window==null||!window.IsLoaded)return null;
            if(PresentationSource.FromVisual(window)==null)return null;
            return window;
        }catch(Exception){return null;}
    }

    /* ---- the daily automatic check ---- */
    internal static bool AutoDue(bool enabled,string lastDate,string today){return enabled&&!String.Equals(lastDate,today,StringComparison.Ordinal);}
    internal static string Today(){
        Func<DateTime> hook=NowHook;DateTime now=hook!=null?hook():DateTime.Now;
        return now.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
    }
    /* A release that is not newer than the version the user switched off is not announced by the automatic check. */
    internal static bool IsSkipped(string skipped,Version latest){
        Version known=ParseTag(skipped);
        return known!=null&&latest!=null&&latest.CompareTo(known)<=0;
    }
    /* Every window calls this when it has loaded; only the first one of the process arms the check. It runs once the
       dispatcher is idle (the result only ever shows the strip, so its timing is not visible), looks at the setting
       and the date, and asks GitHub on a background thread. Self-tests never run it. */
    internal static void ScheduleAuto(MainWindow window){
        if(Program.IsSelfTest&&!ProbeDrive)return;
        if(autoScheduled)return;
        autoScheduled=true;
        try{window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{RunAuto(window);}));}
        catch(Exception){autoScheduled=false;}
    }
    static void RunAuto(MainWindow window){
        try{
            string today=Today();
            if(!AutoDue(MainWindow.autoUpdateCheck,MainWindow.updateCheckedDate,today)){Completed++;return;}
            /* The attempt itself is recorded, success or not: a broken network is not retried at every launch of the same day. */
            MainWindow.updateCheckedDate=today;
            try{window.SaveUpdatePreferences();}catch(Exception){}
            Start(window,true);
        }catch(Exception){Completed++;}
    }
    /* [자세히] on the strip: the manual check's dialog (the strip stays; its own link switches the version off). */
    internal static void ShowDetails(MainWindow window,Version current,Version latest,string url){
        Offer(window,current,latest,url);
    }

    /* Background thread. Returns false (or throws) for anything but a clean answer with a parsable tag. */
    static bool Fetch(out Version latest,out string url){
        latest=null;url=null;
        Func<string> hook=ReleaseJsonHook;
        string text=hook!=null?hook():Download();
        return ParseRelease(text,out latest,out url);
    }
    static string Download(){
        ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
        var request=(HttpWebRequest)WebRequest.Create(LatestApi);
        request.Method="GET";request.UserAgent="MDNote";request.Accept="application/vnd.github+json";
        request.Timeout=10000;request.ReadWriteTimeout=10000;
        var body=new StringBuilder();
        using(var response=(HttpWebResponse)request.GetResponse())
        using(var stream=response.GetResponseStream())
        using(var reader=new StreamReader(stream,Encoding.UTF8)){
            char[] buffer=new char[4096];int count;
            /* The two fields sit near the top of the answer; a runaway body is cut off instead of read to the end. */
            while(body.Length<1000000&&(count=reader.Read(buffer,0,buffer.Length))>0)body.Append(buffer,0,count);
        }
        return body.ToString();
    }
    internal static bool ParseRelease(string text,out Version latest,out string url){
        latest=null;url=null;
        if(text==null)return false;
        Match tag=Regex.Match(text,"\"tag_name\"\\s*:\\s*\"([^\"\\\\]*)\"");
        if(!tag.Success)return false;
        latest=ParseTag(tag.Groups[1].Value);
        if(latest==null)return false;
        Match link=Regex.Match(text,"\"html_url\"\\s*:\\s*\"([^\"\\\\]*)\"");
        if(link.Success)url=link.Groups[1].Value;
        return true;
    }
    /* "v2.1.1" -> 2.1.1; "2.2" -> 2.2.0. Anything after the leading numbers (a "-beta" suffix) is ignored. */
    internal static Version ParseTag(string tag){
        if(tag==null)return null;
        tag=tag.Trim();
        if(tag.Length>0&&(tag[0]=='v'||tag[0]=='V'))tag=tag.Substring(1);
        Match number=Regex.Match(tag,"^(\\d+)(?:\\.(\\d+))?(?:\\.(\\d+))?(?:\\.\\d+)?");
        if(!number.Success)return null;
        int major,minor=0,build=0;
        if(!Int32.TryParse(number.Groups[1].Value,out major))return null;
        if(number.Groups[2].Success&&!Int32.TryParse(number.Groups[2].Value,out minor))return null;
        if(number.Groups[3].Success&&!Int32.TryParse(number.Groups[3].Value,out build))return null;
        return new Version(major,minor,build);
    }
    /* Major.Minor.Build only: the assembly's fourth number is never compared. */
    static Version Normalize(Version version){return new Version(version.Major,Math.Max(version.Minor,0),Math.Max(version.Build,0));}
    /* The manual check always answers, also for a version the user switched off. */
    static void Present(MainWindow window,bool ok,Version current,Version latest,string url){
        if(!ok||latest==null){Notice(window,window.TranslateLabel("업데이트를 확인하지 못했습니다.\n인터넷 연결을 확인한 뒤 다시 시도해 주세요."));return;}
        if(latest.CompareTo(current)<=0){Notice(window,String.Format(window.TranslateLabel("최신 버전입니다. ({0})"),current));return;}
        Offer(window,current,latest,url);
    }
    /* The new-version dialog (automatic and manual): the message, the release link, [릴리스 페이지 열기] [닫기]. It
       changes no preference; "이 버전은 다시 알리지 않음" lives on the strip only. */
    static void Offer(MainWindow window,Version current,Version latest,string url){
        string target=ReleaseTarget(url);bool open;
        UpdateOfferDialog hook=OfferHook;
        if(hook!=null)open=hook(latest.ToString(),target);
        else{
            string message=String.Format(window.TranslateLabel("새 버전이 있습니다: {0}\n현재 버전: {1}"),latest,current);
            open=Dialog(window,message,window.TranslateLabel("릴리스 페이지 열기"),window.TranslateLabel("닫기"),target);
        }
        if(open)OpenReleasePage(target);
    }
    /* Only a link inside this project's own GitHub page is ever opened; anything else falls back to the releases page. */
    internal static string ReleaseTarget(string url){
        return url!=null&&url.StartsWith(RepoPrefix,StringComparison.Ordinal)&&Uri.IsWellFormedUriString(url,UriKind.Absolute)?url:ReleasesPage;
    }
    static void OpenReleasePage(string url){
        string target=ReleaseTarget(url);
        Action<string> hook=OpenPageHook;
        if(hook!=null){hook(target);return;}
        try{Process.Start(new ProcessStartInfo(target){UseShellExecute=true});}catch(Exception){}
    }
    static void Notice(MainWindow window,string message){
        Action<string> hook=NoticeHook;
        if(hook!=null){hook(message);return;}
        Dialog(window,message,window.TranslateLabel("확인"),null,null);
    }
    /* A small owned window in the About dialog's look (same theme brushes, so dark mode follows). Returns true when
       the first button was pressed; with no second button the one button just closes it. A link (clickable, opens
       the release page) is shown only when given. */
    static bool Dialog(MainWindow window,string message,string firstLabel,string secondLabel,string linkUrl){
        bool first=false;
        var dialog=new Window {Owner=window,Title=window.TranslateLabel("업데이트 확인"),Width=430,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,Background=(System.Windows.Media.Brush)window.Resources["Paper"],Foreground=(System.Windows.Media.Brush)window.Resources["Ink"],FontFamily=window.FontFamily,FontSize=13};
        dialog.Resources.MergedDictionaries.Add(window.Resources);
        var panel=new StackPanel {Margin=new Thickness(24)};dialog.Content=panel;
        panel.Children.Add(new TextBlock {Text=message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,linkUrl!=null?10:18)});
        if(linkUrl!=null){
            var linkBlock=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16)};
            var link=new Hyperlink(new Run(linkUrl));
            link.SetResourceReference(TextElement.ForegroundProperty,"Accent");
            string opened=linkUrl;link.Click+=delegate{OpenReleasePage(opened);};
            linkBlock.Inlines.Add(link);panel.Children.Add(linkBlock);
        }
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(buttons);
        var primary=new Button {Content=firstLabel,IsDefault=true,IsCancel=secondLabel==null,MinWidth=64};
        primary.Click+=delegate{first=true;dialog.Close();};buttons.Children.Add(primary);
        if(secondLabel!=null){var other=new Button {Content=secondLabel,IsCancel=true,MinWidth=64,Margin=new Thickness(8,0,0,0)};other.Click+=delegate{dialog.Close();};buttons.Children.Add(other);}
        /* Take the window's keyboard focus explicitly: Enter/Esc must reach the dialog's buttons, not the menu bar behind it. */
        dialog.Loaded+=delegate{try{dialog.Activate();primary.Focus();System.Windows.Input.Keyboard.Focus(primary);}catch(Exception){}};
        Action<Window> shownHook=DialogShownHook;
        if(shownHook!=null)dialog.ContentRendered+=delegate{try{shownHook(dialog);}catch(Exception){}};
        window.UpdateDialogOpening();
        try{dialog.ShowDialog();}finally{window.UpdateDialogClosed();}
        return first;
    }
}
}
