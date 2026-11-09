using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Xml;
namespace MDNote {
internal sealed class ViewPreferences {
    internal string WindowPlacement="";
    internal string Family="Malgun Gothic";
    internal double Size=16;
    internal double LinePercent=150;
    internal double CustomLinePercent=175;
    internal Color? Color;
    internal bool Dark;internal bool NewDocumentsText,AlwaysEditing;
    internal bool OpenInTabs=true;internal bool NumberedList;internal int HeadingLevel=2;
    // Automatic word wrap is a view preference like the font and line spacing; a file without the
    // attribute (every earlier build) keeps the wrapped default.
    internal bool WordWrap=true;
    // 2.4.0 memory study (user 2026-11-09): drawing on the CPU is the default because it saves 32-60 MB
    // with the same typing and scrolling speed; 보기 > GPU 가속 사용 turns hardware drawing back on.
    // Absent (every earlier settings file) reads as false.
    internal bool GpuAcceleration;
    // Stage 2 of the live-as-basic audit: no caret-line markup reveal (the Basic view).
    // The field default stays off for probe fixtures; Load starts a missing or unreadable
    // settings file in the Basic view, and a valid file's attribute is authoritative.
    internal bool HideMarkers;
    /* 이전 세션 계속하기 (user, 2026-11-03): closing while the document has unsaved changes skips
       the save prompt and keeps the text for the next start instead. Off by default, so a build
       without the setting behaves exactly as every earlier one. */
    internal bool ContinueSession;
    // 2.2.0 보기 > 집중 모드 / 타자기 모드 (FocusMode.cs). FocusMode: 0 off, 1 paragraph, 2 line; saved as
    // focusMode="paragraph"/"line" (absent = off); an old file's focusMode="true" reads as paragraph.
    internal int FocusMode;
    internal bool TypewriterMode;
    // 보기 > 제목 단계 표시 (the level mark left of every heading, MarkdownLivePreview HeadingMark). Style: 0 slash bars
    // (default), 1 "#" symbols, 2 off. Saved as headingMarks="hash"/"off" (absent = slash bars; an earlier
    // build's headingMarks="false" reads as off). HeadingMarkLast is the style a click on the row itself turns
    // back on with (0 or 1), saved as headingLast="hash" (absent = slash bars, or the saved style when that is
    // "hash"); choosing off never changes it.
    internal int HeadingMarkStyle;
    internal int HeadingMarkLast;
    internal static string HeadingMarkName(int style){return style==2?"off":style==1?"hash":"";}
    internal static int HeadingMarkStyleOf(string value){return value=="off"||value=="false"?2:value=="hash"?1:0;}
    internal static int HeadingMarkLastOf(string value,int style){return value=="hash"?1:value=="slash"?0:style==1?1:0;}
    // The level a click on the 집중 모드 row itself turns on with (user 2026-11-06): 1 paragraph, 2 line;
    // saved as focusLast="line" while it is 줄 (absent = paragraph), so a file without it keeps 문단. A
    // file that has a saved focusMode but no focusLast (an earlier 2.2.0 build) starts at that level.
    internal int FocusLastLevel=1;
    internal static string FocusModeName(int level){return level==2?"line":level==1?"paragraph":"";}
    internal static int FocusModeLevel(string value){return value=="line"?2:(value=="paragraph"||value=="true")?1:0;}
    internal static int FocusLastLevelOf(string value,int focusMode){
        if(value=="paragraph")return 1;
        if(value=="line")return 2;
        if(String.IsNullOrEmpty(value)&&(focusMode==1||focusMode==2))return focusMode;
        return 1;
    }
    // 2.2.0 update check (UpdateCheck.cs): AutoUpdateChoice is the user's own Help-menu choice, written as
    // autoUpdateCheck="true"/"false" once they toggled it (null = never chose; then the installer's value decides, and
    // without one the check is off); updateCheckedDate is the last automatic attempt's local day (yyyy-MM-dd);
    // skippedUpdateVersion is the release the user asked not to be told about again.
    internal bool? AutoUpdateChoice;internal string UpdateCheckedDate="",SkippedUpdateVersion="";
    internal static bool? ParseChoice(string value){if(value=="true")return true;if(value=="false")return false;return null;}
    internal static string ValidDate(string value){DateTime parsed;return value!=null&&DateTime.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out parsed)?value:"";}
    internal static string ValidVersion(string value){return value!=null&&System.Text.RegularExpressions.Regex.IsMatch(value,"^\\d{1,5}\\.\\d{1,5}\\.\\d{1,5}$")?value:"";}
    internal string[] FunctionKeys=MainWindow.DefaultFunctionKeys();
    internal string Language="ko";
    // 최근 파일 사용 안 함 (2026-11-07, a forum reader asked how to clear the history): the File menu's
    // recent list stops recording while this is on, and turning it on clears the list, so a settings
    // file written with recentOff="true" carries no Recent elements. Absent = off (recent files used).
    internal bool RecentOff;
    internal List<string> Recent=new List<string>();
    internal string Serialize(){var doc=new XmlDocument();var root=doc.CreateElement("MDNote");doc.AppendChild(root);root.SetAttribute("newDocumentsText",NewDocumentsText?"true":"false");root.SetAttribute("alwaysEditing",AlwaysEditing?"true":"false");if(HideMarkers)root.SetAttribute("liveHideMarkers","true");else root.SetAttribute("liveMode","true");root.SetAttribute("windowPlacement",WindowPlacement);root.SetAttribute("language",Language);root.SetAttribute("headingLevel",HeadingLevel.ToString(CultureInfo.InvariantCulture));root.SetAttribute("numberedList",NumberedList?"true":"false");root.SetAttribute("openInTabs",OpenInTabs?"true":"false");root.SetAttribute("wordWrap",WordWrap?"true":"false");root.SetAttribute("gpuAcceleration",GpuAcceleration?"true":"false");if(ContinueSession)root.SetAttribute("continueSession","true");if(RecentOff)root.SetAttribute("recentOff","true");if(FocusMode==1||FocusMode==2)root.SetAttribute("focusMode",FocusModeName(FocusMode));if(FocusLastLevel==2)root.SetAttribute("focusLast",FocusModeName(FocusLastLevel));if(TypewriterMode)root.SetAttribute("typewriterMode","true");if(HeadingMarkStyle==1||HeadingMarkStyle==2)root.SetAttribute("headingMarks",HeadingMarkName(HeadingMarkStyle));if(HeadingMarkLast==1)root.SetAttribute("headingLast","hash");if(AutoUpdateChoice.HasValue)root.SetAttribute("autoUpdateCheck",AutoUpdateChoice.Value?"true":"false");if(UpdateCheckedDate.Length>0)root.SetAttribute("updateCheckedDate",UpdateCheckedDate);if(SkippedUpdateVersion.Length>0)root.SetAttribute("skippedUpdateVersion",SkippedUpdateVersion);root.SetAttribute("font",Family);root.SetAttribute("size",Size.ToString("R",CultureInfo.InvariantCulture));root.SetAttribute("color",Color.HasValue?Color.Value.ToString():"");root.SetAttribute("dark",Dark?"true":"false");root.SetAttribute("linePercent",LinePercent.ToString("R",CultureInfo.InvariantCulture));root.SetAttribute("customLinePercent",CustomLinePercent.ToString("R",CultureInfo.InvariantCulture));for(int i=0;i<12;i++){var key=doc.CreateElement("FunctionKey");key.SetAttribute("key",(i+1).ToString());key.SetAttribute("command",FunctionKeys[i]);root.AppendChild(key);}/* Ten very long paths once pushed the file past the reader's limit and reset every setting on the
   next start; a path too long for the stored list is left out instead of growing the file. */
if(!RecentOff)foreach(string path in Recent){if(path==null||path.Length>520)continue;var item=doc.CreateElement("Recent");item.InnerText=path;root.AppendChild(item);}return doc.OuterXml;}
    // A baseline must not share its mutable members with the window's live fields (the shortcut
    // dialog writes FunctionKeys in place), or the merge would see no change.
    internal ViewPreferences Copy(){return new ViewPreferences {WindowPlacement=WindowPlacement,Family=Family,Size=Size,LinePercent=LinePercent,CustomLinePercent=CustomLinePercent,Color=Color,Dark=Dark,NewDocumentsText=NewDocumentsText,AlwaysEditing=AlwaysEditing,HideMarkers=HideMarkers,ContinueSession=ContinueSession,FocusMode=FocusMode,FocusLastLevel=FocusLastLevel,TypewriterMode=TypewriterMode,HeadingMarkStyle=HeadingMarkStyle,HeadingMarkLast=HeadingMarkLast,AutoUpdateChoice=AutoUpdateChoice,UpdateCheckedDate=UpdateCheckedDate,SkippedUpdateVersion=SkippedUpdateVersion,OpenInTabs=OpenInTabs,NumberedList=NumberedList,HeadingLevel=HeadingLevel,WordWrap=WordWrap,FunctionKeys=(string[])FunctionKeys.Clone(),Language=Language,RecentOff=RecentOff,Recent=new List<string>(Recent),GpuAcceleration=GpuAcceleration};}
        // First-run font follows the UI language (or Windows' display language when none is chosen): Malgun Gothic
    // drew Japanese and Chinese text with Korean glyph shapes. All of these ship with Windows 10/11.
    // A hand-edited or damaged settings file must not carry a family string WPF refuses to parse into
    // the MainWindow constructor (the editor, live view and outline all build a FontFamily from it).
    static bool UsableFamily(string family){try{new System.Windows.Media.FontFamily(family);return true;}catch(Exception){return false;}}
    internal static string DefaultFamily(string language){string tag=String.IsNullOrEmpty(language)?System.Globalization.CultureInfo.CurrentUICulture.Name:language;tag=(tag??"").ToLowerInvariant();if(tag.StartsWith("ko"))return "Malgun Gothic";if(tag.StartsWith("ja"))return "Yu Gothic UI";if(tag=="zh-tw"||tag=="zh-hk"||tag=="zh-mo"||tag.StartsWith("zh-hant"))return "Microsoft JhengHei UI";if(tag.StartsWith("zh"))return "Microsoft YaHei UI";return "Segoe UI";}
    // Windows display language mapped to the five UI languages; anything else is English (11-01 user).
    internal static string SystemLanguage(){
        string tag=(System.Globalization.CultureInfo.CurrentUICulture.Name??"").ToLowerInvariant();
        if(tag.StartsWith("ko"))return "ko";if(tag.StartsWith("ja"))return "ja";
        if(tag=="zh-tw"||tag=="zh-hk"||tag=="zh-mo"||tag.StartsWith("zh-hant"))return "zh-TW";if(tag.StartsWith("zh"))return "zh-CN";
        return "en";
    }
    // install-info.ini [MDNote] Language=ko|en|ja|zh-CN|zh-TW, written by setup; null when absent or unknown.
    internal static string InstallerLanguage(){
        try {string ini=System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"install-info.ini");if(!File.Exists(ini))return null;
            foreach(string raw in File.ReadAllLines(ini)){string line=raw.Trim();if(!line.StartsWith("Language=",StringComparison.OrdinalIgnoreCase))continue;string value=line.Substring(9).Trim();
                foreach(string known in new[]{"ko","en","ja","zh-CN","zh-TW"})if(String.Equals(known,value,StringComparison.OrdinalIgnoreCase))return known;return null;}
        }catch(IOException){}catch(UnauthorizedAccessException){}
        return null;
    }
    internal static ViewPreferences Load(string path){bool unreadable;return Load(path,out unreadable);}
    /* unreadable: the file exists but is not a settings file this build can parse. The caller keeps
       it as a .bak instead of writing defaults over it.
       ioFailure: the file could not be read this time (another window or instance was replacing it).
       That is not a broken file, so the caller may retry and must not overwrite it with defaults. */
    internal static ViewPreferences Load(string path,out bool unreadable){bool ioFailure;return Load(path,out unreadable,out ioFailure);}
    internal static ViewPreferences Load(string path,out bool unreadable,out bool ioFailure){
        unreadable=false;ioFailure=false;
        /* First run and an unreadable file start in the Basic view (markers hidden); a valid file's
           liveMode attribute (written only for the Live view) overrides this below, so older files without it open in Basic. Keeping the override on `result` rather than
           the field default leaves `new ViewPreferences()` off for the probe fixtures. */
        /* 11-01 user: "항상 수정 기능 활성화" starts on; only a saved alwaysEditing="false" turns it off. */
        var result=new ViewPreferences();result.HideMarkers=true;result.AlwaysEditing=true;
        /* 11-01 user: the setup wizard's first page picks the language; the first run (no settings
           file yet) starts in that language and its font. Setup writes it to install-info.ini next to
           the exe; a portable copy has no such file and keeps Korean. */
        if(!File.Exists(path)){string chosen=InstallerLanguage();result.Language=chosen??SystemLanguage();result.Family=DefaultFamily(result.Language);return result;}
        bool loaded=false;
        try {var doc=new XmlDocument {XmlResolver=null};using(var reader=XmlReader.Create(path,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536}))doc.Load(reader);loaded=true;var root=doc.DocumentElement;if(root==null||root.Name!="MDNote"){unreadable=true;return result;}
            /* One bad field must not cost every other setting: font, size, dark and colour each fall
               back to their own default here, and the rest of the file still loads (bughunt-ui 12). */
            string family=root.GetAttribute("font"),color=root.GetAttribute("color");if(family.Trim().Length==0||family.Length>256||!UsableFamily(family))family=DefaultFamily(root.GetAttribute("language"));
            double size;if(!Double.TryParse(root.GetAttribute("size"),NumberStyles.Float,CultureInfo.InvariantCulture,out size)||Double.IsNaN(size)||size<8||size>48)size=16;
            bool dark;if(!Boolean.TryParse(root.GetAttribute("dark"),out dark))dark=false;
            double line; if(!Double.TryParse(root.GetAttribute("linePercent"),NumberStyles.Float,CultureInfo.InvariantCulture,out line)||Double.IsNaN(line)||line<50||line>400)line=150;
            double customLine;if(!Double.TryParse(root.GetAttribute("customLinePercent"),NumberStyles.Float,CultureInfo.InvariantCulture,out customLine)||Double.IsNaN(customLine)||customLine<50||customLine>400)customLine=175;
            bool recentOff=root.GetAttribute("recentOff")=="true";var recent=new List<string>();if(!recentOff)foreach(XmlNode item in root.SelectNodes("Recent")){if(recent.Count==10)break;if(item.InnerText.Length<=520&&System.IO.Path.IsPathRooted(item.InnerText))recent.Add(item.InnerText);}
            var keys=MainWindow.DefaultFunctionKeys();foreach(XmlNode node in root.SelectNodes("FunctionKey")){int index;var key=node as XmlElement;if(key!=null&&Int32.TryParse(key.GetAttribute("key"),out index)&&index>=1&&index<=12&&MainWindow.ValidShortcut(key.GetAttribute("command")))keys[index-1]=key.GetAttribute("command");}
            int headingLevel;if(!Int32.TryParse(root.GetAttribute("headingLevel"),out headingLevel)||headingLevel<1||headingLevel>6)headingLevel=2;/* An unparsable colour is one bad field, not a broken file: it falls back to the no-custom-colour default while every other attribute still loads. */ Color? selected=null;if(color.Length>0){try{selected=(Color)ColorConverter.ConvertFromString(color);}catch(FormatException){selected=null;}catch(NotSupportedException){selected=null;}catch(ArgumentException){selected=null;}}int focusLevel=FocusModeLevel(root.GetAttribute("focusMode"));return new ViewPreferences {WindowPlacement=root.GetAttribute("windowPlacement"),NewDocumentsText=root.GetAttribute("newDocumentsText")=="true",AlwaysEditing=root.GetAttribute("alwaysEditing")!="false",HideMarkers=root.GetAttribute("liveMode")!="true",ContinueSession=root.GetAttribute("continueSession")=="true",FocusMode=focusLevel,FocusLastLevel=FocusLastLevelOf(root.GetAttribute("focusLast"),focusLevel),TypewriterMode=root.GetAttribute("typewriterMode")=="true",HeadingMarkStyle=HeadingMarkStyleOf(root.GetAttribute("headingMarks")),HeadingMarkLast=HeadingMarkLastOf(root.GetAttribute("headingLast"),HeadingMarkStyleOf(root.GetAttribute("headingMarks"))),AutoUpdateChoice=ParseChoice(root.GetAttribute("autoUpdateCheck")),UpdateCheckedDate=ValidDate(root.GetAttribute("updateCheckedDate")),SkippedUpdateVersion=ValidVersion(root.GetAttribute("skippedUpdateVersion")),FunctionKeys=keys,HeadingLevel=headingLevel,NumberedList=root.GetAttribute("numberedList")=="true",RecentOff=recentOff,Recent=recent,Family=family,Size=size,Color=selected,Dark=dark,LinePercent=line,CustomLinePercent=customLine,OpenInTabs=root.GetAttribute("openInTabs")!="false",WordWrap=root.GetAttribute("wordWrap")!="false",GpuAcceleration=root.GetAttribute("gpuAcceleration")=="true",Language=root.GetAttribute("language")==""?"ko":root.GetAttribute("language")};
        }/* Only a failure to read the document itself is marked for the .bak: a file that is merely
           locked this once (another instance) or one bad attribute value is left in place and read
           as defaults again the way it always was. */
        catch(Exception ex){
            if(!loaded&&(ex is XmlException||ex is FormatException||ex is ArgumentException||ex is NotSupportedException)){unreadable=true;return result;}
            if(ex is IOException||ex is UnauthorizedAccessException){ioFailure=true;return result;}
            if(ex is FormatException||ex is ArgumentException||ex is NotSupportedException)return result;
            throw;
        }
    }
    /* 2.4.0 memory study (user 2026-11-09): CPU drawing is the default (measured 32-60 MB less, same
       typing and scrolling speed); 보기 > GPU 가속 사용 turns hardware drawing back on. This runs before
       the first window, so only the one attribute is read. MDNOTE_SOFTWARE_RENDER overrides it for
       benches and probes (1 = software, 0 = hardware); a hidden probe run - IsSelfTest is not set yet
       here, and every probe/bench entry point is a "--" flag - keeps hardware unless the variable is
       set, so existing probes and benches measure what they measured before. */
    internal static System.Windows.Interop.RenderMode StartupRenderMode(string[] args){
        string forced=Environment.GetEnvironmentVariable("MDNOTE_SOFTWARE_RENDER");
        if(forced=="1")return System.Windows.Interop.RenderMode.SoftwareOnly;
        if(forced=="0")return System.Windows.Interop.RenderMode.Default;
        if(args!=null&&args.Length>0&&args[0]!=null&&args[0].StartsWith("--",StringComparison.Ordinal))return System.Windows.Interop.RenderMode.Default;
        return LoadGpuAcceleration(MainWindow.PreferencesPath)?System.Windows.Interop.RenderMode.Default:System.Windows.Interop.RenderMode.SoftwareOnly;
    }
    /* A read that lands while another window or instance replaces the file is retried once; any other
       failure answers false (CPU drawing), the safe default and what a file without the attribute means. */
    internal static bool LoadGpuAcceleration(string path){
        for(int attempt=0;attempt<2;attempt++){
            try{
                if(!File.Exists(path))return false;
                var doc=new XmlDocument {XmlResolver=null};
                using(var reader=XmlReader.Create(path,new XmlReaderSettings {DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=65536}))doc.Load(reader);
                var root=doc.DocumentElement;
                return root!=null&&root.Name=="MDNote"&&root.GetAttribute("gpuAcceleration")=="true";
            }catch(IOException){if(attempt>0)return false;System.Threading.Thread.Sleep(10);}
            catch(Exception){return false;}
        }
        return false;
    }
    /* Several windows share one settings file. A save writes only the fields this window changed
       since it last read or wrote the file; everything else comes from the file as it is now, so a
       second window's older snapshot can no longer roll back the first window's language, theme,
       size or recent list. The single-window result is unchanged: its file and its fields agree. */
    internal static ViewPreferences MergePreferences(ViewPreferences mine,ViewPreferences baseline,ViewPreferences disk){
        if(disk==null||baseline==null)return mine;
        if(!String.Equals(mine.WindowPlacement,baseline.WindowPlacement,StringComparison.Ordinal))disk.WindowPlacement=mine.WindowPlacement;
        if(!String.Equals(mine.Family,baseline.Family,StringComparison.Ordinal))disk.Family=mine.Family;
        if(mine.Size!=baseline.Size)disk.Size=mine.Size;
        if(mine.LinePercent!=baseline.LinePercent)disk.LinePercent=mine.LinePercent;
        if(mine.CustomLinePercent!=baseline.CustomLinePercent)disk.CustomLinePercent=mine.CustomLinePercent;
        if(mine.Color!=baseline.Color)disk.Color=mine.Color;
        if(mine.Dark!=baseline.Dark)disk.Dark=mine.Dark;
        if(mine.NewDocumentsText!=baseline.NewDocumentsText)disk.NewDocumentsText=mine.NewDocumentsText;
        if(mine.AlwaysEditing!=baseline.AlwaysEditing)disk.AlwaysEditing=mine.AlwaysEditing;
        if(mine.HideMarkers!=baseline.HideMarkers)disk.HideMarkers=mine.HideMarkers;
        if(mine.ContinueSession!=baseline.ContinueSession)disk.ContinueSession=mine.ContinueSession;
        if(mine.FocusMode!=baseline.FocusMode)disk.FocusMode=mine.FocusMode;
        if(mine.FocusLastLevel!=baseline.FocusLastLevel)disk.FocusLastLevel=mine.FocusLastLevel;
        if(mine.TypewriterMode!=baseline.TypewriterMode)disk.TypewriterMode=mine.TypewriterMode;
        if(mine.HeadingMarkStyle!=baseline.HeadingMarkStyle)disk.HeadingMarkStyle=mine.HeadingMarkStyle;
        if(mine.HeadingMarkLast!=baseline.HeadingMarkLast)disk.HeadingMarkLast=mine.HeadingMarkLast;
        if(mine.AutoUpdateChoice!=baseline.AutoUpdateChoice)disk.AutoUpdateChoice=mine.AutoUpdateChoice;
        if(!String.Equals(mine.UpdateCheckedDate,baseline.UpdateCheckedDate,StringComparison.Ordinal))disk.UpdateCheckedDate=mine.UpdateCheckedDate;
        if(!String.Equals(mine.SkippedUpdateVersion,baseline.SkippedUpdateVersion,StringComparison.Ordinal))disk.SkippedUpdateVersion=mine.SkippedUpdateVersion;
        if(mine.OpenInTabs!=baseline.OpenInTabs)disk.OpenInTabs=mine.OpenInTabs;
        if(mine.NumberedList!=baseline.NumberedList)disk.NumberedList=mine.NumberedList;
        if(mine.WordWrap!=baseline.WordWrap)disk.WordWrap=mine.WordWrap;
        if(mine.GpuAcceleration!=baseline.GpuAcceleration)disk.GpuAcceleration=mine.GpuAcceleration;
        if(mine.HeadingLevel!=baseline.HeadingLevel)disk.HeadingLevel=mine.HeadingLevel;
        if(!String.Equals(mine.Language,baseline.Language,StringComparison.Ordinal))disk.Language=mine.Language;
        if(!SameFunctionKeys(mine.FunctionKeys,baseline.FunctionKeys))disk.FunctionKeys=mine.FunctionKeys;
        if(mine.RecentOff!=baseline.RecentOff)disk.RecentOff=mine.RecentOff;
        /* A window that names nothing it used to have cleared the list (최근 파일 기록 삭제, or the
           switch that turns recording off): the union below must not restore those names from the
           file or from another window's snapshot. While the switch is on the file keeps none at all
           (user 2026-11-07). */
        if(mine.Recent.Count==0&&baseline.Recent.Count>0)disk.Recent.Clear();
        else disk.Recent=MergeRecents(mine.Recent,baseline.Recent,disk.Recent);
        if(disk.RecentOff)disk.Recent.Clear();
        return disk;
    }
    static bool SameFunctionKeys(string[] left,string[] right){
        if(left==null||right==null)return left==right;
        if(left.Length!=right.Length)return false;
        for(int i=0;i<left.Length;i++)if(!String.Equals(left[i],right[i],StringComparison.Ordinal))return false;
        return true;
    }
    /* Newest first: what this window opened or brought forward since it last read the file, then the
       file's own order (another window's latest files), then the older entries this window knew. */
    static List<string> MergeRecents(List<string> mine,List<string> baseline,List<string> disk){
        var merged=new List<string>();
        if(mine!=null)for(int i=0;i<mine.Count;i++){string path=mine[i];int before=IndexRecent(baseline,path);if(before<0||i<before)AddRecentPath(merged,path);}
        if(disk!=null)foreach(string path in disk)AddRecentPath(merged,path);
        /* An entry this window already knew that the file no longer has was cleared or pushed out by
           another window (최근 파일 기록 삭제 there): it is not brought back (Argon review 11-07). */
        if(mine!=null)foreach(string path in mine){if(IndexRecent(baseline,path)>=0&&IndexRecent(disk,path)<0)continue;AddRecentPath(merged,path);}
        return merged;
    }
    static int IndexRecent(List<string> paths,string path){if(paths==null)return -1;for(int i=0;i<paths.Count;i++)if(String.Equals(paths[i],path,StringComparison.OrdinalIgnoreCase))return i;return -1;}
    static void AddRecentPath(List<string> paths,string path){if(String.IsNullOrEmpty(path)||paths.Count>=10)return;if(IndexRecent(paths,path)>=0)return;paths.Add(path);}
    internal static void Save(string path,string xml){
        Directory.CreateDirectory(Path.GetDirectoryName(path));string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,xml,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
public partial class MainWindow {
    string lastPreferences;
    // What this window last read from or wrote to the settings file. SavePreferences compares the
    // window's fields with it and writes only the ones that changed (a merge with the file on disk).
    ViewPreferences savedPreferences;
    // A settings file that exists but cannot be parsed is kept as a .bak; when it cannot even be
    // set aside, nothing is written over it (defaults must not erase the user's settings).
    bool preferencesUnreadable;
    // liveHideMarkers: the Basic view (no caret-line markup reveal). Static because it is a
    // preference shared by every window; the 보기 > 라이브 모드 item flips it and
    // UpdateLiveModeMenu keeps every window's check mark in step.
    static bool hideMarkers;
    // 보기 > GPU 가속 사용 (2.4.0 memory study, user 2026-11-09): a process-wide setting, static like
    // hideMarkers. ViewPreferences.StartupRenderMode reads it from the file before the first window.
    static bool gpuAcceleration;
    // 보기 > 제목 단계 표시: static like hideMarkers (one preference for every window and tab).
    // headingMarkStyle: 0 slash bars, 1 "#" symbols, 2 off; headingMarkLast: the style a row click turns on with.
    static int headingMarkStyle,headingMarkLast;
    internal static string PreferencesPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MDNote","settings.xml");}}
    /* Several windows - and a second MDNote instance - share one settings file. The atomic replace
       keeps the file whole; this mutex keeps one window's read-merge-write from interleaving with
       another's, which used to let an older snapshot roll back a change written a moment before it
       (bughunt4 item 11). A timeout still lets the save go ahead: the replace is atomic, and losing
       a settings change to a stuck lock would be worse than the documented last-writer rule. */
    static System.Threading.Mutex preferencesFileMutex;
    static System.Threading.Mutex PreferencesFileMutex(){
        if(preferencesFileMutex==null){
            /* A lock that cannot be created must never break saving or start-up: the file replace
               below is atomic on its own and the mutex only narrows the interleaving window. */
            try{preferencesFileMutex=new System.Threading.Mutex(false,"Local\\MDNote-settings");}
            catch(Exception){return null;}
        }
        return preferencesFileMutex;
    }
    static bool LockPreferencesFile(){
        var mutex=PreferencesFileMutex();if(mutex==null)return false;
        /* A settings write takes a few milliseconds; the wait only has to cover another window's
           save, so a stuck lock must not freeze this window's UI for long. */
        try{return mutex.WaitOne(500);}
        catch(System.Threading.AbandonedMutexException){return true;}
        catch(UnauthorizedAccessException){return false;}
    }
    static void UnlockPreferencesFile(bool held){
        /* ReleaseMutex throws when this thread does not own the mutex; a timed-out wait is not
           ownership, and `held` is exactly that answer. */
        var mutex=preferencesFileMutex;if(!held||mutex==null)return;
        try{mutex.ReleaseMutex();}catch(ApplicationException){}
    }
    void LoadPreferences(){if(Program.IsSelfTest)return;bool unreadable=false;bool existed=File.Exists(PreferencesPath);ViewPreferences value=null;/* A read that lands while another window or instance replaces the file used to start this window in defaults, and its close then erased the shared window placement (bughunt4 item 12): the read runs under the same lock and retries a momentary lock. */bool held=LockPreferencesFile();try{for(int attempt=0;attempt<3&&value==null;attempt++){if(!File.Exists(PreferencesPath)){/* First run: Load gives the setup language and its font (a bare ViewPreferences was always Korean). */value=ViewPreferences.Load(PreferencesPath);break;}bool ioFailure;value=ViewPreferences.Load(PreferencesPath,out unreadable,out ioFailure);if(ioFailure){value=null;if(attempt<2)System.Threading.Thread.Sleep(10);}}if(value==null){value=new ViewPreferences();value.HideMarkers=true;value.AlwaysEditing=true;}}finally{UnlockPreferencesFile(held);}if(unreadable){try{string backup=PreferencesPath+".bak";if(File.Exists(backup))File.Delete(backup);File.Move(PreferencesPath,backup);}catch(IOException){preferencesUnreadable=true;}catch(UnauthorizedAccessException){preferencesUnreadable=true;}}recentFiles.AddRange(value.Recent);recentOff=value.RecentOff;language=value.Language;/* 11-01: the setup language (install-info.ini) decides a first run; the old HKLM DefaultLanguage is only a fallback when it is actually set, and the font follows the language it picks (an empty key used to force "ko" and Malgun Gothic). */if(!existed&&ViewPreferences.InstallerLanguage()==null){try{using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey("Software\\MDNote")){string fixedLanguage=key==null?null:key.GetValue("DefaultLanguage") as string;if(!String.IsNullOrEmpty(fixedLanguage)){language=fixedLanguage;value.Family=ViewPreferences.DefaultFamily(fixedLanguage);}}}catch(Exception){/* locked-down machines can refuse HKLM in more ways than SecurityException */}}rememberedWindow=value.WindowPlacement;newDocumentsText=value.NewDocumentsText;alwaysEditing=value.AlwaysEditing;/* Old liveMarkdown attributes are ignored; hideMarkers is the saved Basic/Live view mode. */hideMarkers=value.HideMarkers;functionKeys=value.FunctionKeys;lastHeadingLevel=value.HeadingLevel;numberedList=value.NumberedList;openInTabs=value.OpenInTabs;family=value.Family;fontSize=value.Size;customColor=value.Color;dark=value.Dark;linePercent=value.LinePercent;customLinePercent=value.CustomLinePercent;wordWrap=value.WordWrap;gpuAcceleration=value.GpuAcceleration;continueSession=value.ContinueSession;focusMode=value.FocusMode;focusLastLevel=value.FocusLastLevel;typewriterMode=value.TypewriterMode;headingMarkStyle=value.HeadingMarkStyle;headingMarkLast=value.HeadingMarkLast;autoUpdateChoice=value.AutoUpdateChoice;updateCheckedDate=value.UpdateCheckedDate;skippedUpdateVersion=value.SkippedUpdateVersion;savedPreferences=value.Copy();lastPreferences=value.Serialize();/* The wrap menu is built after this load; show its loaded check and save the next click. */Loaded+=delegate{if(wrapToggle!=null){wrapToggle.IsChecked=wordWrap;wrapToggle.Click+=delegate{SavePreferences();};}/* the saved 집중/타자기 modes attach to the first surface once it is in the tree (FocusMode.cs) */if(focusMode!=0||typewriterMode)SyncFocusModes();};}
    // This window's own view of the shared settings, the left side of the merge.
    ViewPreferences WindowPreferences(){return new ViewPreferences {WindowPlacement=rememberedWindow,NewDocumentsText=newDocumentsText,AlwaysEditing=alwaysEditing,HideMarkers=hideMarkers,ContinueSession=continueSession,FocusMode=focusMode,FocusLastLevel=focusLastLevel,TypewriterMode=typewriterMode,HeadingMarkStyle=headingMarkStyle,HeadingMarkLast=headingMarkLast,AutoUpdateChoice=autoUpdateChoice,UpdateCheckedDate=updateCheckedDate,SkippedUpdateVersion=skippedUpdateVersion,FunctionKeys=(string[])functionKeys.Clone(),HeadingLevel=lastHeadingLevel,NumberedList=numberedList,Recent=new List<string>(recentFiles),RecentOff=recentOff,Language=language,OpenInTabs=openInTabs,Family=family,Size=fontSize,Color=customColor,Dark=dark,LinePercent=linePercent,CustomLinePercent=customLinePercent,WordWrap=wordWrap,GpuAcceleration=gpuAcceleration};}
    // True when a field this window owns changed since it last read or wrote the file. Placement is
    // saved separately at close (SaveWindowPlacement) and does not count here.
    bool PreferencesDirty(){if(savedPreferences==null)return true;ViewPreferences mine=WindowPreferences();mine.WindowPlacement=savedPreferences.WindowPlacement;return mine.Serialize()!=savedPreferences.Serialize();}
    // The file as the last window left it, or null when it is missing or cannot be read this time.
    // The plain read comes first so a momentary lock (a read failure inside Load would look like a
    // defaults file) never turns into a write of defaults over the real settings.
    ViewPreferences ReadPreferencesFromDisk(){
        if(!File.Exists(PreferencesPath))return null;
        try{if(File.ReadAllText(PreferencesPath).Length==0)return null;}catch(IOException){return null;}catch(UnauthorizedAccessException){return null;}
        /* A load that fell back because the file was locked this once must answer null (nothing is
           written over it), not a defaults copy the callers would merge and save back. */
        bool unreadable,ioFailure;var disk=ViewPreferences.Load(PreferencesPath,out unreadable,out ioFailure);
        return unreadable||ioFailure?null:disk;
    }
    void SavePreferences(){
        if(Program.IsSelfTest||preferencesUnreadable)return;
        bool held=LockPreferencesFile();string broadcastLanguage=null;
        try{
            ViewPreferences mine=WindowPreferences(),disk=ReadPreferencesFromDisk();/* A file this build cannot parse or read is never written over. */if(disk==null&&File.Exists(PreferencesPath))return;
            ViewPreferences merged=disk==null?mine:ViewPreferences.MergePreferences(mine,savedPreferences,disk);string xml=merged.Serialize();if(xml==lastPreferences)return;
            string previousLanguage=savedPreferences==null?null:savedPreferences.Language;
            try{ViewPreferences.Save(PreferencesPath,xml);lastPreferences=xml;savedPreferences=mine.Copy();AdoptRecent(merged);if(previousLanguage!=null&&!String.Equals(merged.Language,previousLanguage,StringComparison.Ordinal))broadcastLanguage=merged.Language;}catch(IOException){}catch(UnauthorizedAccessException){}
        }finally{UnlockPreferencesFile(held);}
        /* Every window shares one language setting, but the switch used to re-label only the window
           it was made in until the next start (bughunt4 item 8). The save that switch performs is
           the signal: the language it wrote is applied to the other windows here. Localization.cs
           owns the menu click and is add-strings-only this round, so the broadcast hangs off the
           save instead. A window opened later reads the saved value as before. */
        if(broadcastLanguage!=null)ApplyLanguageToOtherWindows(broadcastLanguage);
    }
    void ApplyLanguageToOtherWindows(string code){
        var app=System.Windows.Application.Current;if(app==null)return;
        foreach(System.Windows.Window candidate in app.Windows){
            var window=candidate as MainWindow;if(window==null||Object.ReferenceEquals(window,this))continue;
            if(String.Equals(window.language,code,StringComparison.Ordinal))continue;
            window.language=code;window.ApplyLanguage();
        }
    }
    /* Closing changes only this window's placement: the other values in the file belong to whichever
       window changed them last, and this window's older copy must not replace them. A change that
       could not be written earlier is still saved in full. */
    void SaveWindowPlacement(){
        if(Program.IsSelfTest||preferencesUnreadable)return;
        if(PreferencesDirty()){SavePreferences();return;}
        bool defaultsOnly=false;bool held=LockPreferencesFile();
        try{
            ViewPreferences disk=ReadPreferencesFromDisk();
            if(disk==null)defaultsOnly=!File.Exists(PreferencesPath);
            else{
                /* A window whose settings could not be read when it opened has no placement of its
                   own (rememberedWindow is empty); writing that value would erase the position every
                   window shares (bughunt4 item 12). The reverse - clearing a placement - has no UI,
                   so an empty disk value is the only time an empty memory may be written. */
                if(rememberedWindow.Length>0||disk.WindowPlacement.Length==0)disk.WindowPlacement=rememberedWindow;
                string xml=disk.Serialize();if(xml==lastPreferences)return;
                try{ViewPreferences.Save(PreferencesPath,xml);lastPreferences=xml;savedPreferences=WindowPreferences().Copy();}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }finally{UnlockPreferencesFile(held);}
        if(defaultsOnly)SavePreferences();
    }
}
}
