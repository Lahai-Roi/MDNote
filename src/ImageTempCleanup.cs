using System;using System.IO;using System.Text;using System.Collections.Generic;using System.Diagnostics;using System.Runtime.InteropServices;using System.Threading.Tasks;using System.Windows;using System.Windows.Threading;
namespace MDNote {
/* Automatic cleanup of the shared temp picture folder (user, 2026-11-04): a picture pasted or dropped
   into a document is first written to Documents\MDNote\images (MainWindow.PastedImageTempFolder) and
   moves beside the document on save, so every picture the text no longer links stays there for ever.
   Once per launch, ~20 seconds after the first window restored the session and the crash drafts, a
   picture directly in that folder is moved to the Recycle Bin when all of these hold:
     - it is older than 7 days (newer of creation and last write time);
     - no open tab of any window names it (the live editor text, not the last saved text);
     - no crash draft or session draft on disk names it (a postponed draft may still be restored);
     - no other MDNote process is running (its unsaved pastes may not be in a draft yet).
   Never File.Delete: the Recycle Bin keeps a wrong guess recoverable. Subfolders, non-picture files
   and anything over 64 MB (the bin may delete such a file for good) are left alone. A read that
   fails on any draft abandons the whole run, because a skipped draft could hide a link. Nothing
   here shows UI or throws, and a self-test, a probe with the folder override and RecoveryStore
   disabled never run it. */
static class ImageTempCleanup {
    const int StaleDays=7,DelaySeconds=20;
    const long ReadCap=50L*1024*1024,ItemCap=64L*1024*1024;
    static readonly string[] pictureExtensions=new string[]{".png",".jpg",".jpeg",".gif",".bmp",".webp",".tif",".tiff",".ico",".svg",".jfif"};
    static bool scheduled;
    // Called once from the first window's Loaded handler, after the session and the crash prompt.
    internal static void ScheduleOnce(){
        try{
            if(scheduled)return;
            if(!RecoveryStore.Enabled||Program.IsSelfTest||MainWindow.ImageTempFolderOverride!=null)return;
            scheduled=true;
            var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(DelaySeconds)};
            timer.Tick+=delegate{timer.Stop();RunOnUiThread();};
            timer.Start();
        }catch(Exception){}
    }
    // UI thread: only the open text is collected here; every file touch happens on a pool thread.
    static void RunOnUiThread(){
        try{
            if(OtherInstanceRunning())return;
            string folder=MainWindow.TempFolderForCleanup();
            if(String.IsNullOrEmpty(folder))return;
            var openTexts=new List<string>();
            var application=Application.Current;
            if(application==null)return;
            foreach(Window window in application.Windows){
                var editor=window as MainWindow;
                if(editor!=null)editor.AddOpenDocumentTexts(openTexts);
            }
            Task.Run(delegate{try{Sweep(folder,openTexts);}catch(Exception){}});
        }catch(Exception){}
    }
    // A failed process query counts as "another one is running": the cleanup is the optional side.
    static bool OtherInstanceRunning(){
        try{
            using(Process me=Process.GetCurrentProcess()){
                Process[] all=Process.GetProcessesByName(me.ProcessName);
                bool others=all.Length>1;
                foreach(Process one in all)one.Dispose();
                return others;
            }
        }catch(Exception){return true;}
    }
    static bool IsPicture(string name){
        string extension=Path.GetExtension(name);
        if(String.IsNullOrEmpty(extension))return false;
        foreach(string known in pictureExtensions)if(String.Equals(extension,known,StringComparison.OrdinalIgnoreCase))return true;
        return false;
    }
    // The spellings a link can carry for one file name: raw, the drop's own escaping
    // (MarkdownCommands.EscapeImageReference), spaces as %20, and the full percent-escape.
    static string[] NameVariants(string name){
        var result=new List<string>();
        result.Add(name);
        string[] others=new string[]{
            name.Replace("%","%25").Replace("#","%23").Replace("?","%3F").Replace("(","%28").Replace(")","%29").Replace(" ","%20"),
            name.Replace(" ","%20"),
            Uri.EscapeDataString(name)};
        foreach(string other in others)if(!result.Contains(other))result.Add(other);
        return result.ToArray();
    }
    // Marks every candidate whose name appears in the text; returns how many are still unmarked.
    static int MarkReferenced(string haystack,List<string[]> variants,bool[] keep){
        int open=0;
        if(String.IsNullOrEmpty(haystack)){
            for(int i=0;i<keep.Length;i++)if(!keep[i])open++;
            return open;
        }
        for(int i=0;i<keep.Length;i++){
            if(keep[i])continue;
            foreach(string variant in variants[i]){
                if(haystack.IndexOf(variant,StringComparison.OrdinalIgnoreCase)>=0){keep[i]=true;break;}
            }
            if(!keep[i])open++;
        }
        return open;
    }
    // Pool thread. openTexts is private to this call now (the UI thread built it and let go).
    static void Sweep(string folder,List<string> openTexts){
        if(!Directory.Exists(folder))return;
        string full=Path.GetFullPath(folder);
        if(full.TrimEnd('\\').Length<4)return;
        DateTime cutoff=DateTime.UtcNow-TimeSpan.FromDays(StaleDays);
        var candidates=new List<FileInfo>();
        var variants=new List<string[]>();
        foreach(string file in Directory.GetFiles(full)){
            try{
                var info=new FileInfo(file);
                if(!IsPicture(info.Name))continue;
                if((info.Attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)continue;
                if(info.Length>ItemCap)continue;
                DateTime newest=info.CreationTimeUtc>info.LastWriteTimeUtc?info.CreationTimeUtc:info.LastWriteTimeUtc;
                if(newest>=cutoff)continue;
                candidates.Add(info);
                variants.Add(NameVariants(info.Name));
            }catch(Exception){}
        }
        if(candidates.Count==0)return;
        var keep=new bool[candidates.Count];
        foreach(string openText in openTexts)if(MarkReferenced(openText,variants,keep)==0)return;
        /* Crash drafts and session drafts share one folder. A draft is UTF-16 after a header of
           arbitrary byte length, so it is decoded at both byte alignments. */
        string root=RecoveryStore.Root;
        if(Directory.Exists(root)){
            foreach(string draft in Directory.GetFiles(root,"*.draft",SearchOption.AllDirectories)){
                byte[] bytes=null;
                try{
                    using(var stream=new FileStream(draft,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
                        if(stream.Length>ReadCap)return;
                        bytes=new byte[(int)stream.Length];
                        int got=0;
                        while(got<bytes.Length){int count=stream.Read(bytes,got,bytes.Length-got);if(count<=0)break;got+=count;}
                        if(got<bytes.Length)Array.Resize(ref bytes,got);
                    }
                }catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}catch(Exception){return;}
                if(MarkReferenced(Encoding.Unicode.GetString(bytes),variants,keep)==0)return;
                if(bytes.Length>1&&MarkReferenced(Encoding.Unicode.GetString(bytes,1,bytes.Length-1),variants,keep)==0)return;
            }
        }
        var doomed=new List<string>();
        for(int i=0;i<candidates.Count;i++)if(!keep[i])doomed.Add(candidates[i].FullName);
        if(doomed.Count>0)ToRecycleBin(doomed);
    }
    /* One file per SHFileOperation call: a locked file or a bad path fails only itself, not the rest.
       A path of 260+ characters is skipped (the shell's old-style path limit; the bin could not take it).
       SHFileOperation takes a double-null-terminated list; the marshaller adds the last terminator. */
    static void ToRecycleBin(List<string> files){
        foreach(string file in files){
            try{
                if(file==null||file.Length>=260)continue;
                RecycleBatch(file+"\0");
            }catch(Exception){}
        }
    }
    const uint FO_DELETE=3;
    // FOF_SILENT 0x4 | FOF_NOCONFIRMATION 0x10 | FOF_ALLOWUNDO 0x40 | FOF_NOERRORUI 0x400 | FOF_WANTNUKEWARNING 0x4000
    // (the last one: a drive without a Recycle Bin asks instead of deleting for good)
    const ushort DeleteFlags=0x4454;
    static void RecycleBatch(string list){
        if(IntPtr.Size==4){var op=new ShFileOp32 {wFunc=FO_DELETE,pFrom=list,fFlags=DeleteFlags};SHFileOperation32(ref op);}
        else{var op=new ShFileOp64 {wFunc=FO_DELETE,pFrom=list,fFlags=DeleteFlags};SHFileOperation64(ref op);}
    }
    // The 32-bit struct is byte-packed, the 64-bit one uses the default layout.
    [StructLayout(LayoutKind.Sequential,Pack=1,CharSet=CharSet.Unicode)]
    struct ShFileOp32 {public IntPtr hwnd;public uint wFunc;public string pFrom;public string pTo;public ushort fFlags;public int fAnyOperationsAborted;public IntPtr hNameMappings;public string lpszProgressTitle;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct ShFileOp64 {public IntPtr hwnd;public uint wFunc;public string pFrom;public string pTo;public ushort fFlags;public int fAnyOperationsAborted;public IntPtr hNameMappings;public string lpszProgressTitle;}
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,EntryPoint="SHFileOperationW")]
    static extern int SHFileOperation32(ref ShFileOp32 operation);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,EntryPoint="SHFileOperationW")]
    static extern int SHFileOperation64(ref ShFileOp64 operation);
}
/* MainWindow side of the cleanup: DocumentTab is private to the window, so the tab texts are read here. */
public partial class MainWindow {
    internal static string TempFolderForCleanup(){return PastedImageTempFolder();}
    // The text this window's tabs hold right now: the visible editor's buffer, and for every other tab its editor buffer and its stored text.
    // No catch here on purpose: a tab that cannot be read abandons the whole run (RunOnUiThread), since a missed tab could hide a link.
    internal void AddOpenDocumentTexts(List<string> into){
        if(source!=null)AddCleanupText(into,source.Document.Text);
        AddCleanupText(into,text);
        foreach(var document in documents){
            if(Object.ReferenceEquals(document,activeDocument))continue;
            if(document.Source!=null)AddCleanupText(into,document.Source.Document.Text);
            AddCleanupText(into,document.Text);
        }
    }
    static void AddCleanupText(List<string> into,string value){
        if(String.IsNullOrEmpty(value))return;
        foreach(string known in into)if(Object.ReferenceEquals(known,value)||String.Equals(known,value,StringComparison.Ordinal))return;
        into.Add(value);
    }
}
}
