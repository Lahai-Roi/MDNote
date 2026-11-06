using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace MDNote {
/* 2.2.0: tells the user when another program changed or removed an open file. The window remembers the
   file's last-write time and length (a DiskStamp) right after it loaded or saved the file; the active tab's
   file is compared with it when the window is activated and when a tab becomes active. Nothing runs on
   keystrokes, and nothing is watched in the background. */
sealed class DiskStamp {
    internal string FullPath,Fingerprint;internal bool Exists;internal DateTime Utc;internal long Length;
    internal static DiskStamp Read(string full,string fingerprint){
        var stamp=new DiskStamp {FullPath=full,Fingerprint=fingerprint};
        try{var info=new FileInfo(full);if(info.Exists){stamp.Exists=true;stamp.Utc=info.LastWriteTimeUtc;stamp.Length=info.Length;}}
        catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(NotSupportedException){}
        return stamp;
    }
    internal bool SameFile(DiskStamp other){return other!=null&&Exists==other.Exists&&Utc==other.Utc&&Length==other.Length;}
}
public partial class MainWindow {
    /* The stamp of the active document's file (a tab keeps its own in DocumentTab.Stamp). Null for an untitled document. */
    DiskStamp documentStamp;
    /* A stamp read just before a file was read from disk; ApplyLoadedFile takes it so a change made while the
       file was being read and rendered is still seen. */
    DiskStamp pendingLoadStamp;
    bool externalChecking,externalCheckQueued;
    /* Probe seam: (kind, full path) -> true when the first button was chosen. Kinds: "changed", "changed-dirty",
       "removed", "overwrite". While a self-test runs, the window only checks when a probe set this. */
    internal static Func<string,string,bool> ExternalChangeDialog;
    /* The disk version a "취소" at save time already answered for (EvaluateExternalChange skips it). */
    DiskStamp externalAskedStamp;
    static bool SameFullPath(string a,string b){
        if(a==null||b==null)return false;
        try{return String.Equals(System.IO.Path.GetFullPath(a),System.IO.Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);}catch(Exception){return false;}
    }
    /* ApplyLoadedFile: the file was just read, so its stamp is the file's version. */
    void TakeLoadedFileStamp(string full){
        DiskStamp pending=pendingLoadStamp;pendingLoadStamp=null;
        if(pending!=null&&String.Equals(pending.FullPath,full,StringComparison.OrdinalIgnoreCase)){pending.Fingerprint=documentFingerprint;documentStamp=pending;return;}
        documentStamp=DiskStamp.Read(full,documentFingerprint);
    }
    /* CaptureTab: a save sets a new documentFingerprint (the hash of the bytes it wrote) right before the
       capture, so a fingerprint the stamp does not know yet means "MDNote just wrote this file". An
       identical-content save keeps the fingerprint but touches the file time; the check below sees that
       the bytes are still ours and only refreshes the stamp. O(1) when nothing changed. */
    void SyncDocumentStamp(){
        if(documentFingerprint==null||documentFingerprintPath==null)return;
        DiskStamp known=documentStamp;
        if(known!=null&&String.Equals(known.Fingerprint,documentFingerprint,StringComparison.Ordinal)&&String.Equals(known.FullPath,documentFingerprintPath,StringComparison.OrdinalIgnoreCase))return;
        documentStamp=DiskStamp.Read(documentFingerprintPath,documentFingerprint);
    }
    /* Window activation (Editor.cs) and tab switches call this. The work is posted: the activation that a
       closing dialog causes, or one inside a save dialog's nested loop, must not run a check in the middle of
       the operation that raised it. */
    void CheckExternalChanges(){ScheduleExternalCheck();}
    void ScheduleExternalCheck(){
        if(externalCheckQueued)return;
        if(Program.IsSelfTest&&ExternalChangeDialog==null)return;
        externalCheckQueued=true;
        try{Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(delegate{externalCheckQueued=false;RunExternalCheck();}));}
        catch(Exception){externalCheckQueued=false;}
    }
    /* One File.Exists-sized look at the active tab's file; the dialogs and the reload only happen on a change. */
    internal void RunExternalCheck(){
        if(externalChecking)return;
        if(Program.IsSelfTest&&ExternalChangeDialog==null)return;
        if(!Program.IsSelfTest&&(!IsActive||WindowState==WindowState.Minimized))return;
        if(activeDocument==null||documentOperationBusy||fileOpenRunning||saveInProgress||placementClosed||closeApproved||saveConfirmationDialog!=null)return;
        DiskStamp stamp=documentStamp;
        if(stamp==null||path==null||path.Length==0||!SameFullPath(stamp.FullPath,path))return;
        /* A live IME composition would receive the dialog's keys: wait for its commit, then look again. */
        SourceEditor owner=source;
        if(owner!=null&&owner.Visibility==Visibility.Visible&&owner.ImeBusy){owner.RunAfterComposition(delegate{RunExternalCheck();});return;}
        externalChecking=true;
        try{EvaluateExternalChange(stamp);}
        catch(Exception ex){System.Diagnostics.Trace.TraceError("MDNote file check: {0}: {1}",ex.GetType().Name,ex.Message);}
        finally{externalChecking=false;}
    }
    void EvaluateExternalChange(DiskStamp stamp){
        string full=stamp.FullPath;
        DiskStamp now=DiskStamp.Read(full,null);
        if(!now.Exists){
            if(!stamp.Exists)return;/* already noticed: once per removal */
            /* Remember the removal first, so the activation the box causes finds nothing new. The old
               version's hash is dropped: the save that follows recreates the file instead of meeting a
               conflict, and the document counts as unsaved so closing it asks to save. */
            now.Fingerprint=null;documentStamp=now;documentFingerprint=null;
            recoveredDocument=true;CaptureTab();UpdateTitle();
            AskExternalChange("removed",full);
            return;
        }
        if(now.SameFile(stamp))return;
        /* The time or size moved. If the bytes are still the version this window loaded or saved (a sync tool
           or an identical save touched the file), only the stamp is refreshed. */
        if(stamp.Exists&&documentFingerprint!=null&&documentFingerprintPath!=null&&SameFullPath(documentFingerprintPath,full)){
            string disk;
            try{disk=FileVersion.Read(full);}catch(IOException){return;}catch(UnauthorizedAccessException){return;}
            if(String.Equals(disk,documentFingerprint,StringComparison.Ordinal)){now.Fingerprint=documentFingerprint;documentStamp=now;CaptureTab();return;}
        }
        /* The save-time question was just answered "취소" for this very disk version: the activation that box causes
           must not ask about the same change again (VM check 2026-11-05). A later save still asks. */
        DiskStamp asked=externalAskedStamp;
        if(asked!=null&&SameFullPath(asked.FullPath,full)&&now.SameFile(asked))return;
        FlushDocumentChanges();
        bool dirty=HasUnsavedChanges;
        bool reload=AskExternalChange(dirty?"changed-dirty":"changed",full);
        if(reload&&ReloadFromDisk(full))return;
        if(reload)return;/* the file could not be read just now: the old stamp stays, the next activation asks again */
        AdoptDiskVersion(now,full);
    }
    /* "그대로 두기" / "내 변경 내용 유지": the file as it is now becomes this document's base, so the same change is not
       asked again and a later save replaces it knowingly (the save-time conflict check compares this hash). */
    void AdoptDiskVersion(DiskStamp now,string full){
        string disk=null;
        try{disk=FileVersion.Read(full);}catch(IOException){}catch(UnauthorizedAccessException){}
        if(disk!=null&&disk!="missing"){documentFingerprint=disk;documentFingerprintPath=full;}
        now.Fingerprint=documentFingerprint;documentStamp=now;CaptureTab();
    }
    /* Reads the file again into this same tab the way a file open does (same encoding detection and newline
       rule, ApplyLoadedFile), then puts the caret back on its line and column (clamped) and the view near its
       old top. The edit mode the tab was in is kept. Returns false, changing nothing, when the read fails. */
    bool ReloadFromDisk(string full){
        int line=1,column=0;double top=0;
        SourceEditor editor=source;
        if(editor!=null&&editor.Document!=null){
            var document=editor.Document;int caret=Math.Max(0,Math.Min(editor.CaretIndex,document.TextLength));
            var current=document.GetLineByOffset(caret);line=current.LineNumber;column=caret-current.Offset;top=editor.VerticalOffset;
        }
        DiskStamp before=DiskStamp.Read(full,null);
        string value;Encoding encoding;string fingerprint;byte[] originalBytes;
        try{value=ReadTextFileVersion(full,out encoding,out fingerprint,out originalBytes);}
        catch(Exception ex){System.Diagnostics.Trace.TraceError("MDNote reload: {0}: {1}",ex.GetType().Name,ex.Message);return false;}
        string newline=DetectNewline(value);string buffer=NormalizeLineEndings(value,newline);if(buffer==value)originalBytes=null;
        bool previousAlways=alwaysEditing;
        pendingLoadStamp=before;
        try{alwaysEditing=editing;ApplyLoadedFile(full,value,encoding,fingerprint,originalBytes,buffer,newline);}
        finally{alwaysEditing=previousAlways;pendingLoadStamp=null;}
        ForgetRecovery(activeDocument);
        if(source!=null&&source.Document!=null){
            var document=source.Document;int target=Math.Max(1,Math.Min(line,document.LineCount));
            var placed=document.GetLineByNumber(target);int offset=placed.Offset+Math.Max(0,Math.Min(column,placed.Length));
            RestoreMovedTabView(offset,top);
        }
        CaptureTab();UpdateTitle();
        return true;
    }
    /* Save time (see the report): true when the write may go on. A file whose time or size differs from the
       stamp, and whose bytes are not this window's own version, was changed by another program while MDNote
       stayed in front: ask "덮어쓰기" / "취소". Only the document's own file is asked about; a Save As to another
       path, an untitled document or a file that is gone never asks. "덮어쓰기" makes the disk version the base,
       so the save-time conflict check (SaveSafety.cs) that follows lets this one write through. */
    internal bool ConfirmOverwriteExternalChange(string target){
        // Self-tests without the probe hook keep the older conflict path (SaveSafety.cs); a real dialog would block them.
        if(Program.IsSelfTest&&ExternalChangeDialog==null)return true;
        string full;try{full=System.IO.Path.GetFullPath(target);}catch(Exception){return true;}
        DiskStamp stamp=documentStamp;
        if(stamp==null||!stamp.Exists||!SameFullPath(stamp.FullPath,full))return true;
        DiskStamp now=DiskStamp.Read(full,null);
        if(!now.Exists||now.SameFile(stamp))return true;
        string disk=null;
        try{disk=FileVersion.Read(full);}catch(IOException){}catch(UnauthorizedAccessException){}
        if(disk!=null&&documentFingerprint!=null&&SameFullPath(documentFingerprintPath,full)&&String.Equals(disk,documentFingerprint,StringComparison.Ordinal)){now.Fingerprint=documentFingerprint;documentStamp=now;return true;}
        if(!AskExternalChange("overwrite",full)){externalAskedStamp=now;return false;}
        AdoptDiskVersion(now,full);
        return true;
    }
    /* The dialog's own wording per kind. Returns true for the first button. */
    bool AskExternalChange(string kind,string full){
        Func<string,string,bool> hook=ExternalChangeDialog;
        if(hook!=null)return hook(kind,full);
        string message,first,second=null;bool firstIsDefault=true;
        switch(kind){
            case "changed":message=String.Format(T("다른 프로그램에서 이 파일이 바뀌었습니다.\n{0}\n\n다시 불러올까요?"),full);first=T("다시 불러오기");second=T("그대로 두기");break;
            case "changed-dirty":message=String.Format(T("다른 프로그램에서 이 파일이 바뀌었습니다.\n{0}\n\nMDNote에서 수정한 내용은 아직 저장하지 않았습니다. 다시 불러오면 수정한 내용이 사라집니다."),full);first=T("다시 불러오기");second=T("내 변경 내용 유지");firstIsDefault=false;break;
            case "overwrite":message=String.Format(T("저장하려는 파일이 다른 프로그램에서 바뀌었습니다.\n{0}\n\n덮어쓸까요?"),full);first=T("덮어쓰기");second=T("취소");firstIsDefault=false;break;
            default:message=String.Format(T("이 파일이 없어졌습니다(지워졌거나 옮겨졌습니다).\n{0}\n\n지금 내용은 그대로 남아 있고, 저장하면 파일을 다시 만듭니다."),full);first=T("확인");break;
        }
        return ShowExternalChangeDialog(message,first,second,firstIsDefault);
    }
    /* Same look as the update dialog (UpdateCheck.cs): a small owned window with the About dialog's theme brushes.
       The answer that discards or overwrites is never the default button, and Esc / the close box choose the
       second button. */
    bool ShowExternalChangeDialog(string message,string firstLabel,string secondLabel,bool firstIsDefault){
        bool first=false;
        var dialog=new Window {Owner=this,Title="MDNote",Width=470,SizeToContent=SizeToContent.Height,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,Background=(System.Windows.Media.Brush)Resources["Paper"],Foreground=(System.Windows.Media.Brush)Resources["Ink"],FontFamily=FontFamily,FontSize=13};
        dialog.Resources.MergedDictionaries.Add(Resources);
        var panel=new StackPanel {Margin=new Thickness(24)};dialog.Content=panel;
        panel.Children.Add(new TextBlock {Text=message,TextWrapping=TextWrapping.Wrap,LineHeight=21,Margin=new Thickness(0,0,0,18)});
        var buttons=new WrapPanel {HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(buttons);
        var primary=new Button {Content=firstLabel,IsDefault=firstIsDefault||secondLabel==null,IsCancel=secondLabel==null,MinWidth=84,Margin=new Thickness(0,0,8,0)};
        primary.Click+=delegate{first=true;dialog.Close();};buttons.Children.Add(primary);
        if(secondLabel!=null){var other=new Button {Content=secondLabel,IsCancel=true,IsDefault=!firstIsDefault,MinWidth=84};other.Click+=delegate{dialog.Close();};buttons.Children.Add(other);}
        dialog.ShowDialog();
        return first;
    }
}
}
