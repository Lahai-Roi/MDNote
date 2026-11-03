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
    // Stage 2 of the live-as-basic audit: no caret-line markup reveal (the Basic view).
    // The field default stays off for probe fixtures; Load starts a missing or unreadable
    // settings file in the Basic view, and a valid file's attribute is authoritative.
    internal bool HideMarkers;
    /* 이전 세션 계속하기 (user, 2026-11-03): closing while the document has unsaved changes skips
       the save prompt and keeps the text for the next start instead. Off by default, so a build
       without the setting behaves exactly as every earlier one. */
    internal bool ContinueSession;
    internal string[] FunctionKeys=MainWindow.DefaultFunctionKeys();
    internal string Language="ko";
    internal List<string> Recent=new List<string>();
    internal string Serialize(){var doc=new XmlDocument();var root=doc.CreateElement("MDNote");doc.AppendChild(root);root.SetAttribute("newDocumentsText",NewDocumentsText?"true":"false");root.SetAttribute("alwaysEditing",AlwaysEditing?"true":"false");if(HideMarkers)root.SetAttribute("liveHideMarkers","true");else root.SetAttribute("liveMode","true");root.SetAttribute("windowPlacement",WindowPlacement);root.SetAttribute("language",Language);root.SetAttribute("headingLevel",HeadingLevel.ToString(CultureInfo.InvariantCulture));root.SetAttribute("numberedList",NumberedList?"true":"false");root.SetAttribute("openInTabs",OpenInTabs?"true":"false");root.SetAttribute("wordWrap",WordWrap?"true":"false");if(ContinueSession)root.SetAttribute("continueSession","true");root.SetAttribute("font",Family);root.SetAttribute("size",Size.ToString("R",CultureInfo.InvariantCulture));root.SetAttribute("color",Color.HasValue?Color.Value.ToString():"");root.SetAttribute("dark",Dark?"true":"false");root.SetAttribute("linePercent",LinePercent.ToString("R",CultureInfo.InvariantCulture));root.SetAttribute("customLinePercent",CustomLinePercent.ToString("R",CultureInfo.InvariantCulture));for(int i=0;i<12;i++){var key=doc.CreateElement("FunctionKey");key.SetAttribute("key",(i+1).ToString());key.SetAttribute("command",FunctionKeys[i]);root.AppendChild(key);}/* Ten very long paths once pushed the file past the reader's limit and reset every setting on the
   next start; a path too long for the stored list is left out instead of growing the file. */
foreach(string path in Recent){if(path==null||path.Length>520)continue;var item=doc.CreateElement("Recent");item.InnerText=path;root.AppendChild(item);}return doc.OuterXml;}
    // A baseline must not share its mutable members with the window's live fields (the shortcut
    // dialog writes FunctionKeys in place), or the merge would see no change.
    internal ViewPreferences Copy(){return new ViewPreferences {WindowPlacement=WindowPlacement,Family=Family,Size=Size,LinePercent=LinePercent,CustomLinePercent=CustomLinePercent,Color=Color,Dark=Dark,NewDocumentsText=NewDocumentsText,AlwaysEditing=AlwaysEditing,HideMarkers=HideMarkers,ContinueSession=ContinueSession,OpenInTabs=OpenInTabs,NumberedList=NumberedList,HeadingLevel=HeadingLevel,WordWrap=WordWrap,FunctionKeys=(string[])FunctionKeys.Clone(),Language=Language,Recent=new List<string>(Recent)};}
        // First-run font follows the UI language (or Windows' display language when none is chosen): Malgun Gothic
    // drew Japanese and Chinese text with Korean glyph shapes. All of these ship with Windows 10/11.
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
            string family=root.GetAttribute("font"),color=root.GetAttribute("color");if(family.Length==0||family.Length>256)family=DefaultFamily(root.GetAttribute("language"));
            double size;if(!Double.TryParse(root.GetAttribute("size"),NumberStyles.Float,CultureInfo.InvariantCulture,out size)||Double.IsNaN(size)||size<8||size>48)size=16;
            bool dark;if(!Boolean.TryParse(root.GetAttribute("dark"),out dark))dark=false;
            double line; if(!Double.TryParse(root.GetAttribute("linePercent"),NumberStyles.Float,CultureInfo.InvariantCulture,out line)||Double.IsNaN(line)||line<50||line>400)line=150;
            double customLine;if(!Double.TryParse(root.GetAttribute("customLinePercent"),NumberStyles.Float,CultureInfo.InvariantCulture,out customLine)||Double.IsNaN(customLine)||customLine<50||customLine>400)customLine=175;
            var recent=new List<string>();foreach(XmlNode item in root.SelectNodes("Recent")){if(recent.Count==10)break;if(item.InnerText.Length<=520&&System.IO.Path.IsPathRooted(item.InnerText))recent.Add(item.InnerText);}
            var keys=MainWindow.DefaultFunctionKeys();foreach(XmlNode node in root.SelectNodes("FunctionKey")){int index;var key=node as XmlElement;if(key!=null&&Int32.TryParse(key.GetAttribute("key"),out index)&&index>=1&&index<=12&&MainWindow.ValidShortcut(key.GetAttribute("command")))keys[index-1]=key.GetAttribute("command");}
            int headingLevel;if(!Int32.TryParse(root.GetAttribute("headingLevel"),out headingLevel)||headingLevel<1||headingLevel>6)headingLevel=2;/* An unparsable colour is one bad field, not a broken file: it falls back to the no-custom-colour default while every other attribute still loads. */ Color? selected=null;if(color.Length>0){try{selected=(Color)ColorConverter.ConvertFromString(color);}catch(FormatException){selected=null;}catch(NotSupportedException){selected=null;}catch(ArgumentException){selected=null;}}return new ViewPreferences {WindowPlacement=root.GetAttribute("windowPlacement"),NewDocumentsText=root.GetAttribute("newDocumentsText")=="true",AlwaysEditing=root.GetAttribute("alwaysEditing")!="false",HideMarkers=root.GetAttribute("liveMode")!="true",ContinueSession=root.GetAttribute("continueSession")=="true",FunctionKeys=keys,HeadingLevel=headingLevel,NumberedList=root.GetAttribute("numberedList")=="true",Recent=recent,Family=family,Size=size,Color=selected,Dark=dark,LinePercent=line,CustomLinePercent=customLine,OpenInTabs=root.GetAttribute("openInTabs")!="false",WordWrap=root.GetAttribute("wordWrap")!="false",Language=root.GetAttribute("language")==""?"ko":root.GetAttribute("language")};
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
        if(mine.OpenInTabs!=baseline.OpenInTabs)disk.OpenInTabs=mine.OpenInTabs;
        if(mine.NumberedList!=baseline.NumberedList)disk.NumberedList=mine.NumberedList;
        if(mine.WordWrap!=baseline.WordWrap)disk.WordWrap=mine.WordWrap;
        if(mine.HeadingLevel!=baseline.HeadingLevel)disk.HeadingLevel=mine.HeadingLevel;
        if(!String.Equals(mine.Language,baseline.Language,StringComparison.Ordinal))disk.Language=mine.Language;
        if(!SameFunctionKeys(mine.FunctionKeys,baseline.FunctionKeys))disk.FunctionKeys=mine.FunctionKeys;
        disk.Recent=MergeRecents(mine.Recent,baseline.Recent,disk.Recent);
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
        if(mine!=null)foreach(string path in mine)AddRecentPath(merged,path);
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
    static string PreferencesPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MDNote","settings.xml");}}
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
    void LoadPreferences(){if(Program.IsSelfTest)return;bool unreadable=false;bool existed=File.Exists(PreferencesPath);ViewPreferences value=null;/* A read that lands while another window or instance replaces the file used to start this window in defaults, and its close then erased the shared window placement (bughunt4 item 12): the read runs under the same lock and retries a momentary lock. */bool held=LockPreferencesFile();try{for(int attempt=0;attempt<3&&value==null;attempt++){if(!File.Exists(PreferencesPath)){/* First run: Load gives the setup language and its font (a bare ViewPreferences was always Korean). */value=ViewPreferences.Load(PreferencesPath);break;}bool ioFailure;value=ViewPreferences.Load(PreferencesPath,out unreadable,out ioFailure);if(ioFailure){value=null;if(attempt<2)System.Threading.Thread.Sleep(10);}}if(value==null){value=new ViewPreferences();value.HideMarkers=true;value.AlwaysEditing=true;}}finally{UnlockPreferencesFile(held);}if(unreadable){try{string backup=PreferencesPath+".bak";if(File.Exists(backup))File.Delete(backup);File.Move(PreferencesPath,backup);}catch(IOException){preferencesUnreadable=true;}catch(UnauthorizedAccessException){preferencesUnreadable=true;}}recentFiles.AddRange(value.Recent);language=value.Language;/* 11-01: the setup language (install-info.ini) decides a first run; the old HKLM DefaultLanguage is only a fallback when it is actually set, and the font follows the language it picks (an empty key used to force "ko" and Malgun Gothic). */if(!existed&&ViewPreferences.InstallerLanguage()==null){try{using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey("Software\\MDNote")){string fixedLanguage=key==null?null:key.GetValue("DefaultLanguage") as string;if(!String.IsNullOrEmpty(fixedLanguage)){language=fixedLanguage;value.Family=ViewPreferences.DefaultFamily(fixedLanguage);}}}catch(System.Security.SecurityException){}}rememberedWindow=value.WindowPlacement;newDocumentsText=value.NewDocumentsText;alwaysEditing=value.AlwaysEditing;/* Old liveMarkdown attributes are ignored; hideMarkers is the saved Basic/Live view mode. */hideMarkers=value.HideMarkers;functionKeys=value.FunctionKeys;lastHeadingLevel=value.HeadingLevel;numberedList=value.NumberedList;openInTabs=value.OpenInTabs;family=value.Family;fontSize=value.Size;customColor=value.Color;dark=value.Dark;linePercent=value.LinePercent;customLinePercent=value.CustomLinePercent;wordWrap=value.WordWrap;continueSession=value.ContinueSession;savedPreferences=value.Copy();lastPreferences=value.Serialize();/* The wrap menu is built after this load; show its loaded check and save the next click. */Loaded+=delegate{if(wrapToggle!=null){wrapToggle.IsChecked=wordWrap;wrapToggle.Click+=delegate{SavePreferences();};}};}
    // This window's own view of the shared settings, the left side of the merge.
    ViewPreferences WindowPreferences(){return new ViewPreferences {WindowPlacement=rememberedWindow,NewDocumentsText=newDocumentsText,AlwaysEditing=alwaysEditing,HideMarkers=hideMarkers,ContinueSession=continueSession,FunctionKeys=(string[])functionKeys.Clone(),HeadingLevel=lastHeadingLevel,NumberedList=numberedList,Recent=new List<string>(recentFiles),Language=language,OpenInTabs=openInTabs,Family=family,Size=fontSize,Color=customColor,Dark=dark,LinePercent=linePercent,CustomLinePercent=customLinePercent,WordWrap=wordWrap};}
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
            try{ViewPreferences.Save(PreferencesPath,xml);lastPreferences=xml;savedPreferences=mine.Copy();if(previousLanguage!=null&&!String.Equals(merged.Language,previousLanguage,StringComparison.Ordinal))broadcastLanguage=merged.Language;}catch(IOException){}catch(UnauthorizedAccessException){}
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
