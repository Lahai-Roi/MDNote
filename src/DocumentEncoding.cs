using System;using System.IO;using System.Text;using System.Windows;using System.Windows.Controls;
namespace MDNote {public partial class MainWindow {
static bool FormatMismatch(string filename,bool textMode){string extension=Path.GetExtension(filename).ToLowerInvariant();return (extension==".txt"&&!textMode)||((extension==".md"||extension==".markdown")&&textMode);}
// 2.2.0: "ANSI" is the legacy code page of this Windows, as Notepad reads it: CP949 on Korean, GBK
// on Simplified Chinese, Big5 on Traditional Chinese, Shift-JIS on Japanese, Windows-1252 on English
// and Western European, 1251 on Russian, and so on (before 2.2.0 it was CP949 everywhere). With the
// "Beta: UTF-8 worldwide" option the system page is 65001, which the UTF-8 attempt already covered,
// so the UI language's legacy page is used instead.
internal static int AnsiCodePage(){int page;try{page=Encoding.Default.CodePage;if(page==65001||page<=0)page=System.Globalization.CultureInfo.CurrentUICulture.TextInfo.ANSICodePage;}catch(Exception){page=1252;}return page==65001||page<=0?1252:page;}
static Encoding StrictEncoding(int index){switch(index){case 1:return new UTF8Encoding(true,true);case 2:return new UnicodeEncoding(false,true,true);case 4:return new UnicodeEncoding(true,true,true);case 3:return Encoding.GetEncoding(AnsiCodePage(),EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);default:return new UTF8Encoding(false,true);}}
// The Save As combo's "keep" entry names the document's own encoding. It is spelled with the same
// words the Open-with-encoding menu uses, so a Shift-JIS/GBK/Big5/CP1252/UTF-32 document shows a
// name the user already saw there instead of an index the fixed entries cannot express.
static string EncodingCaption(Encoding encoding){
    switch(encoding.CodePage){
        case 65001:return encoding.GetPreamble().Length>0?"UTF-8 (BOM)":"UTF-8";
        case 1200:return "UTF-16 LE";
        case 1201:return "UTF-16 BE";
        case 12000:return "UTF-32 LE";
        case 12001:return "UTF-32 BE";
        case 949:return "ANSI (CP949)";
        case 932:return "Shift JIS";
        case 936:return "GBK";
        case 950:return "Big5";
        case 1252:return "Windows-1252";
        default:return encoding.WebName.ToUpperInvariant()+" (CP"+encoding.CodePage+")";
    }
}
// The path the Save As dialog starts with: the document's own full path so the dialog opens in its
// folder, the same folder with the converted extension for a format-changing save, and a recovered
// draft's original file while the tab's own path field stays "" (RC9Tests).
static string SaveAsDefaultPath(string documentPath,string recoveredOriginal,bool mismatch,bool plain){
    string original=documentPath;
    if(original==null||original.Length==0)original=recoveredOriginal;
    if(original==null||original.Length==0)return null;
    if(!mismatch)return original;
    /* A recovery record can hold a bare file name (no directory) and a root path has none either.
       The bare name converts in place; a root has no file name to convert, so let SaveAsync pick
       its own default instead of letting Path.Combine throw on a null directory. */
    string directory=Path.GetDirectoryName(original);
    if(directory==null)return null;
    string stem=Path.GetFileNameWithoutExtension(original);
    if(directory.Length==0)return stem.Length==0?null:stem+(plain?".txt":".md");
    return Path.Combine(directory,stem+(plain?".txt":".md"));
}

// StreamReader BOM autodetection replaces a strict decoder with a permissive one.
// Choose the encoding ourselves so malformed BOM documents cannot be silently saved lossy.
static string ReadTextFile(string name,out Encoding encoding){string version;return ReadTextFileVersion(name,out encoding,out version);}
static string ReadTextFileVersion(string name,out Encoding encoding,out string version){byte[] bytes;return ReadTextFileVersion(name,out encoding,out version,out bytes);}
static string ReadTextFileVersion(string name,out Encoding encoding,out string version,out byte[] bytes){bytes=File.ReadAllBytes(name);version=FileVersion.Of(bytes);int offset=0;bool bom=true;if(bytes.Length>=4&&bytes[0]==0xFF&&bytes[1]==0xFE&&bytes[2]==0&&bytes[3]==0){encoding=new UTF32Encoding(false,true,true);offset=4;}else if(bytes.Length>=4&&bytes[0]==0&&bytes[1]==0&&bytes[2]==0xFE&&bytes[3]==0xFF){encoding=new UTF32Encoding(true,true,true);offset=4;}else if(bytes.Length>=3&&bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF){encoding=StrictEncoding(1);offset=3;}else if(bytes.Length>=2&&bytes[0]==0xFF&&bytes[1]==0xFE){encoding=StrictEncoding(2);offset=2;}else if(bytes.Length>=2&&bytes[0]==0xFE&&bytes[1]==0xFF){encoding=StrictEncoding(4);offset=2;}else{encoding=StrictEncoding(0);bom=false;}try{return encoding.GetString(bytes,offset,bytes.Length-offset);}catch(DecoderFallbackException){if(bom)throw;encoding=StrictEncoding(3);return encoding.GetString(bytes);}}
// Saving changes bytes, not the editing buffer or its history. `saved` is the exact buffer
// snapshot at the save point, not the normalized/converted file text: the O(1) dirty checks,
// tab state and recovery checks all compare in that same representation. Disk conflicts use
// documentFingerprint instead. Keeping this snapshot also leaves history offsets byte-exact.
// The rich source mirror is untouched as well, so the next rich edit can still take the delta
// path; AcceptSerializedEdit verifies that delta against the mirror before it applies one.
// Save As to the other format is the exception: the tab ends in the file's mode (RC7), so the
// mode switch runs first through the explicit toggle's one-step conversion and the file is then
// written from the converted buffer. Ordinary saves never switch mode or convert the buffer.
// Every save normalizes the file to documentNewline (output only); the buffer and its history
// keep their newlines.
bool saveInProgress;
// Above ToggleTextMode's deferral threshold a format-changing Save As must not duplicate the
// conversion synchronously: it goes through the queued async conversion (and its overlay) and the
// file is written only after that conversion finished.
bool FormatChangeNeedsAsync(string target,bool changeFormat){
    if(!changeFormat)return false;
    string extension=Path.GetExtension(target).ToLowerInvariant();
    if(extension!=".txt"&&extension!=".md"&&extension!=".markdown")return false;
    bool targetPlain=extension==".txt";
    if(targetPlain==PlainText)return false;
    int length=documentText.Length;if(source.Document.TextLength>length)length=source.Document.TextLength;
    return length>=256*1024;
}
/* Probes and RC9Tests call SaveToPath / SaveToPathAsync by name through reflection with their three
   arguments, so their arity stays; the path a conflict prompt approved travels through the Core
   variants below as a parameter of that one retry save (never as window state). */
bool SaveToPath(string target,Encoding encoding,bool changeFormat){return SaveToPathCore(target,encoding,changeFormat,null);}
bool SaveToPathCore(string target,Encoding encoding,bool changeFormat,string approvedPath){
    FlushDocumentChanges();
    if(!FormatChangeNeedsAsync(target,changeFormat))return SaveToPathNow(target,encoding,changeFormat,approvedPath);
    var previous=System.Threading.SynchronizationContext.Current;bool set=Dispatcher.CheckAccess()&&previous==null;
    if(set)System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
    try{return WaitForSaveTask(SaveToPathAsyncCore(target,encoding,changeFormat,approvedPath));}
    finally{if(set)System.Threading.SynchronizationContext.SetSynchronizationContext(previous);}
}
System.Threading.Tasks.Task<bool> SaveToPathAsync(string target,Encoding encoding,bool changeFormat){return SaveToPathAsyncCore(target,encoding,changeFormat,null);}
async System.Threading.Tasks.Task<bool> SaveToPathAsyncCore(string target,Encoding encoding,bool changeFormat,string approvedPath){
    FlushDocumentChanges();
    if(!FormatChangeNeedsAsync(target,changeFormat))return SaveToPathNow(target,encoding,changeFormat,approvedPath);
    if(saveInProgress)return false;
    saveInProgress=true;
    try{
        bool targetPlain=String.Equals(Path.GetExtension(target),".txt",StringComparison.OrdinalIgnoreCase);
        /* The queued operation performs the write inside the open queue (FileOpening.cs), which has no
           approved path to hand over. An approved retry that still needs this queue therefore cannot
           skip its conflict check; SaveAsync stops it there instead of prompting for the same file again. */
        FileOperation operation=QueueModeConversionForSaveAsync(targetPlain,target,encoding);
        return await operation.Completion.Task;
    }finally{saveInProgress=false;}
}
bool SaveToPathNow(string target,Encoding encoding,bool changeFormat,string approvedPath=null){
    string extension=Path.GetExtension(target).ToLowerInvariant();bool switched=false;
    if(changeFormat&&(extension==".txt"||extension==".md"||extension==".markdown")){
        bool targetPlain=extension==".txt";
        if(targetPlain!=PlainText){
            /* The conversion is committed before the write can fail, so its mode entry is held
               back and the tab is put back as it was when the write does not happen. The explicit
               toggle's one-step conversion runs synchronously here: ToggleTextMode defers to
               RunAfterComposition while an IME store still owns the text, and the save then saw
               the old mode and returned false without saving or converting anything. */
            saveConversionDeferred=true;
            bool beforeEditing=editing,beforePreview=preview.IsChecked==true;
            try{FinishComposition();ToggleTextModeNow();}
            catch{RollbackSaveConversionView(beforeEditing,beforePreview);throw;}
            finally{saveConversionDeferred=false;}
            switched=targetPlain==PlainText;
            if(!switched){RollbackSaveConversionView(beforeEditing,beforePreview);return false;}
            bool saved;
            try{saved=WriteConvertedToPath(target,encoding,switched,approvedPath);}
            catch{RollbackSaveConversionView(beforeEditing,beforePreview);throw;}
            if(!saved){RollbackSaveConversionView(beforeEditing,beforePreview);return false;}
            FinishSaveConversion(activeDocument);
            return saved;
        }
    }
    return WriteConvertedToPath(target,encoding,switched,approvedPath);
}
/* True when a path is held by a tab other than the active document, or by another window. The
   active document's own file is the ordinary save target and is excluded by the caller (UI bug
   hunt 4 item 2). */
bool SaveTargetHeldElsewhere(string full){
    foreach(var document in documents){
        if(Object.ReferenceEquals(document,activeDocument))continue;
        string held=document.Path;
        if(held==null||held.Length==0)continue;
        string other;
        try{other=Path.GetFullPath(held);}catch(Exception){continue;}
        if(String.Equals(other,full,StringComparison.OrdinalIgnoreCase))return true;
    }
    MainWindow owner=WindowWithFile(Application.Current,full);
    return owner!=null&&!Object.ReferenceEquals(owner,this);
}
bool WriteConvertedToPath(string target,Encoding encoding,bool switched,string approvedPath=null){
    bool plain=PlainText;string original=text;
    /* Newlines are normalized for the file only, exactly like an ordinary save; the buffer (and the
       history offsets recorded for it) keeps the newlines the conversion produced. */
    string output=NormalizeLineEndings(original,documentNewline);
    byte[] originalBytes=!switched&&original==saved&&documentOriginalBytes!=null&&encoding.CodePage==documentEncoding.CodePage&&encoding.GetPreamble().Length==documentEncoding.GetPreamble().Length?documentOriginalBytes:null;
    encoding.GetByteCount(output);
    string fullTarget=Path.GetFullPath(target);
    /* A Save As onto a file another tab or window already holds would leave two editors on one
       path, each later refusing the other's save as a conflict (UI bug hunt 4 item 2). Saving the
       document's own file is not a duplicate. */
    bool ownPath=false;
    if(path!=null&&path.Length>0){try{ownPath=String.Equals(Path.GetFullPath(path),fullTarget,StringComparison.OrdinalIgnoreCase);}catch(Exception){}}
    if(!ownPath&&SaveTargetHeldElsewhere(fullTarget))throw new SaveTargetInUseException();
    using(var lease=new SaveLease(target)){
        /* The conflict retry's own write: answering "yes" and then keeping the same file name
           approves replacing the version the prompt named, so this write must not ask again (item 3).
           The approval is that retry save's parameter, so a second save in the same window still
           meets the external edit instead of inheriting the approval. */
        CheckSaveConflict(fullTarget,approvedPath);
        /* The exact bytes this save writes: the unchanged-file shortcut's original bytes, or the
           encoding's preamble plus the encoded output (the same bytes the writer produces). */
        byte[] content=originalBytes;
        if(content==null){byte[] preamble=encoding.GetPreamble();byte[] body=encoding.GetBytes(output);if(preamble.Length==0)content=body;else{content=new byte[preamble.Length+body.Length];Array.Copy(preamble,0,content,0,preamble.Length);Array.Copy(body,0,content,preamble.Length,body.Length);}}
        WriteDocumentAtomic(target,output,encoding,content);
        /* The fingerprint is the hash of what this save wrote, not a re-read of the file: a write
           landing between our commit and that read would otherwise be adopted as this window's own
           version and the next save would overwrite it without a prompt (review item 2). */
        documentFingerprint=FileVersion.Of(content);documentFingerprintPath=fullTarget;
    }
    // The save point is the buffer as it stands after the mode switch. Undoing the conversion
    // entry (Ctrl+Z) returns the previous mode and text, which is then dirty against this file.
    documentOriginalBytes=originalBytes;path=target;saved=original;textMode=plain;
    recoveredDocument=false;ForgetRecovery(activeDocument);documentEncoding=encoding;
    RememberRecent(target);CaptureTab();UpdateTitle();return true;
}
// Callers that cannot await (the close prompt's synchronous Confirm, tab close, tests) still get
// the asynchronous conversion: start it on the dispatcher and pump until it finishes. Only the UI
// thread can pump; another thread just waits for the dispatcher work.
bool WaitForSaveTask(System.Threading.Tasks.Task<bool> task){
    if(task.IsCompleted)return task.GetAwaiter().GetResult();
    if(!Dispatcher.CheckAccess())return task.GetAwaiter().GetResult();
    var frame=new System.Windows.Threading.DispatcherFrame();
    var timer=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background);timer.Interval=TimeSpan.FromMilliseconds(10);
    long started=System.Diagnostics.Stopwatch.GetTimestamp();
    timer.Tick+=delegate{if(task.IsCompleted||(System.Diagnostics.Stopwatch.GetTimestamp()-started)*1000.0/System.Diagnostics.Stopwatch.Frequency>=300000.0){timer.Stop();frame.Continue=false;}};
    timer.Start();
    try{System.Windows.Threading.Dispatcher.PushFrame(frame);}finally{timer.Stop();}
    if(!task.IsCompleted){CancelFileOpen();return false;}
    return task.GetAwaiter().GetResult();
}
// The probe/test entry points run before Application.Run installs the dispatcher's
// SynchronizationContext; without one the conversion's awaits would resume on the thread pool.
System.Threading.Tasks.Task<bool> SaveTaskOnDispatcher(bool saveAs){return SaveOnDispatcher(saveAs,null);}
/* The conflict retry, carrying the path its prompt approved (one prompt per save). Only this call
   passes it; a probe can call it by name to start an approved save without the prompt. */
System.Threading.Tasks.Task<bool> RetrySaveOnDispatcher(string approvedPath){return SaveOnDispatcher(true,approvedPath);}
System.Threading.Tasks.Task<bool> SaveOnDispatcher(bool saveAs,string approvedPath){
    var previous=System.Threading.SynchronizationContext.Current;bool set=Dispatcher.CheckAccess()&&previous==null;
    if(set)System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher));
    try{return SaveAsync(saveAs,approvedPath);}
    finally{if(set)System.Threading.SynchronizationContext.SetSynchronizationContext(previous);}
}
}}
