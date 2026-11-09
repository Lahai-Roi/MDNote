using System;using System.IO;using System.Text;using System.Collections.Generic;using System.Text.RegularExpressions;using System.Windows;using System.Windows.Controls;using System.Windows.Documents;using System.Windows.Media.Imaging;using System.Globalization;
namespace MDNote {public partial class MainWindow {
static string NormalizeRtfUnicode(string value){var result=new StringBuilder();foreach(char character in value){if(character>127)result.Append("\\u").Append(unchecked((short)character)).Append('?');else result.Append(character);}return result.ToString();}
/* Clipboard pictures (user, 2026-11-03): a clipboard that holds an image and no text is a picture
   paste, in both views. A saved document keeps the picture in a folder named after itself beside
   the .md (C:\notes\메모.md -> C:\notes\메모\), an untitled one in Documents\MDNote\images, and the
   link is written by the drop's own rule (MarkdownCommands.ImageReference), so a name with a space,
   a parenthesis or a '%' renders. The pictures an untitled document pasted are remembered per tab;
   the first save moves the ones its text still links next to the file and rewrites their links
   (MovePastedImagesToDocument), which is why the list is cleared once a document has a path of its
   own. A document restored from a session or a crash draft has no list; its first save finds the
   pictures in the text itself. */
List<string> PastedImagesOf(DocumentTab state){
    if(state==null)return null;
    List<string> images;
    if(!pastedImages.TryGetValue(state,out images)){images=new List<string>();pastedImages.Add(state,images);}
    return images;
}
// A closed or saved tab drops its list; a list holding files that are still moving is dropped last.
void ForgetPastedImages(DocumentTab state){
    if(state==null)return;
    try{pastedImages.Remove(state);}catch(Exception){}
}
// Documents\MDNote\images for an untitled document: one shared folder, created on first use.
/* Probe seam: a run points the shared picture folder at its own scratch folder so it never touches
   the user's Documents\MDNote\images. Null in the product. */
internal static string ImageTempFolderOverride=null;
static string PastedImageTempFolder(){
    string forced=ImageTempFolderOverride;
    if(!String.IsNullOrEmpty(forced))return forced;
    string documents=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    if(String.IsNullOrEmpty(documents))documents=Path.GetTempPath();
    return Path.Combine(Path.Combine(documents,"MDNote"),"images");
}
// <folder>\<document name without extension> for a saved document.
static string PastedImageFolder(string documentPath){
    try{
        string folder=Path.GetDirectoryName(Path.GetFullPath(documentPath));
        string name=Path.GetFileNameWithoutExtension(documentPath);
        if(String.IsNullOrEmpty(folder)||String.IsNullOrEmpty(name))return null;
        return Path.Combine(folder,name);
    }catch(Exception){return null;}
}
// image-yyyyMMdd-HHmmss.png, then -2, -3 ... for a second picture in the same second.
static string UniqueImagePath(string folder,string prefix){
    string baseName=prefix+DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
    string path=Path.Combine(folder,baseName+".png");
    int number=1;
    while(File.Exists(path)){number++;path=Path.Combine(folder,baseName+"-"+number.ToString(CultureInfo.InvariantCulture)+".png");}
    return path;
}
static bool WritePng(BitmapSource image,string path){
    var encoder=new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(image));
    using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write))encoder.Save(stream);
    return true;
}
/* Image FILES inserted into a document (user, 2026-11-03): a picture that already lives in the
   document's own image folder (<docdir>\<docname>, subfolders included) is linked where it is, and
   a picture from anywhere else - even directly beside the .md or in another subfolder of its
   directory - is staged: copied into the shared Documents\MDNote\images folder (the one the
   clipboard picture uses) and linked by its absolute path, in a saved document too. Every save
   moves the staged pictures the text still links beside the file (MovePastedImagesToDocument). The same rule covers
   an Explorer drop, a Ctrl+V of copied files and the image button's file dialog, so all three write
   the link the drop always wrote. */
static bool ImageInsideFolder(string file,string folder){
    try{
        string full=Path.GetFullPath(file);
        string parent=Path.GetFullPath(folder);
        if(parent.Length==0||parent[parent.Length-1]!='\\')parent=parent+"\\";
        return full.Length>parent.Length&&full.StartsWith(parent,StringComparison.OrdinalIgnoreCase);
    }catch(Exception){return false;}
}
// True when the file's own folder is exactly `folder` (no subfolder).
static bool ImageDirectlyInFolder(string file,string folder){
    try{
        string parent=Path.GetDirectoryName(Path.GetFullPath(file));
        string wanted=Path.GetFullPath(folder).TrimEnd('\\');
        return !String.IsNullOrEmpty(parent)&&String.Equals(parent.TrimEnd('\\'),wanted,StringComparison.OrdinalIgnoreCase);
    }catch(Exception){return false;}
}
/* The link text an insert writes for a picture: a staged one (directly in the shared temp folder)
   keeps its absolute form even in a saved document, because the save finds and moves it by that
   form (PastedImageTempFile); every other picture follows the document's own path rule. */
string ImageLinkReference(string file){
    if(ImageDirectlyInFolder(file,PastedImageTempFolder()))return MarkdownCommands.ImageReference("",file);
    return MarkdownCommands.ImageReference(path,file);
}
// True when both files hold the same bytes: a second copy of one picture is the same picture.
static bool SameImageContent(string left,string right){
    try{
        var one=new FileInfo(left);var two=new FileInfo(right);
        if(one.Length!=two.Length)return false;
        using(var first=File.OpenRead(left))
        using(var second=File.OpenRead(right)){
            var bufferA=new byte[81920];var bufferB=new byte[81920];
            while(true){
                int readA=first.Read(bufferA,0,bufferA.Length);
                int readB=second.Read(bufferB,0,bufferB.Length);
                if(readA!=readB)return false;
                if(readA==0)return true;
                for(int i=0;i<readA;i++)if(bufferA[i]!=bufferB[i])return false;
            }
        }
    }catch(Exception){return false;}
}
/* The copy keeps the original file name; an identical file already under that name is reused, and a
   different picture with the same name takes -2, -3 ... before the extension. Null means the copy
   could not be made (the caller then links the original path). */
static string CopiedImageFile(string source,string folder){
    bool created;
    return CopiedImageFile(source,folder,out created);
}
// created is true only when this call wrote a new file (a reused identical file is not ours to undo).
static string CopiedImageFile(string source,string folder,out bool created){
    created=false;
    string name=Path.GetFileName(source);
    string stem=Path.GetFileNameWithoutExtension(name);
    string extension=Path.GetExtension(name);
    for(int number=1;number<=999;number++){
        string candidate=Path.Combine(folder,number==1?name:stem+"-"+number.ToString(CultureInfo.InvariantCulture)+extension);
        try{
            if(File.Exists(candidate)){
                if(SameImageContent(source,candidate))return candidate;
                continue;
            }
            File.Copy(source,candidate,false);
            created=true;
            return candidate;
        }catch(IOException){
            /* The name appeared between the check and the copy (another window copied the same
               picture): the next round reuses an identical file or asks for the next number. */
            if(!File.Exists(candidate))return null;
        }catch(UnauthorizedAccessException){return null;}
        catch(ArgumentException){return null;}
        catch(NotSupportedException){return null;}
    }
    return null;
}
/* One image file as the insert should link it. Only a picture already inside the document's own
   image folder (<docdir>\<docname>, subfolders included) or already staged keeps its place;
   everything else - a picture beside the .md, one in another subfolder of the document's
   directory, one on another drive - is copied into the shared temp folder. A
   copy that fails keeps the original path, so the insert still reaches the picture, and a TXT
   document is never touched (the callers answer it with the notice first). */
string ImageFileForInsert(string file){
    if(String.IsNullOrEmpty(file))return file;
    string full;
    try{full=Path.GetFullPath(file);}catch(Exception){return file;}
    if(!File.Exists(full))return full;
    string folder;
    try{
        /* Staging (user, 2026-11-03): saved or not, an inserted picture waits in the shared temp
           folder with an absolute link. The save moves the ones the text still links into <docname>\
           (MovePastedImagesToDocument), so a picture inserted and then deleted never lands there. */
        folder=PastedImageTempFolder();
        if(String.IsNullOrEmpty(folder))return full;
        if(path==""){
            /* A picture already in the shared folder needs no copy. */
            if(ImageInsideFolder(full,folder))return full;
        }else{
            /* Already staged (directly in the shared folder) or already inside the document's own
               picture folder: linked where it is. */
            if(ImageDirectlyInFolder(full,folder))return full;
            string own=PastedImageFolder(path);
            if(!String.IsNullOrEmpty(own)&&ImageInsideFolder(full,own))return full;
        }
        Directory.CreateDirectory(folder);
        string copied=CopiedImageFile(full,folder);
        return copied==null?full:copied;
    }catch(Exception error){
        System.Diagnostics.Trace.TraceError("MDNote image copy: {0}: {1}",error.GetType().Name,error.Message);
        return full;
    }
}
// Every file of an image insert, in the same order; null and an empty list pass through.
string[] ImageFilesForInsert(string[] files){
    if(files==null||files.Length==0)return files;
    if(PlainText)return files;
    var result=new string[files.Length];
    for(int i=0;i<files.Length;i++)result[i]=ImageFileForInsert(files[i]);
    return result;
}
/* A picture that could not be written is not a paste, and the half-written file must not stay:
   the WIC encoder, a bad image and a path the file system refuses all answer with exceptions
   other than IOException (review item 7). The caller then falls back to the clipboard's text. */
static void DiscardPastedImage(string file,Exception error){
    System.Diagnostics.Trace.TraceError("MDNote image paste: {0}: {1}",error.GetType().Name,error.Message);
    if(file==null)return;
    try{File.Delete(file);}catch(Exception){}
}
/* The clipboard formats Windows offers for a copied picture, read through ClipboardRetry so a
   program that holds the clipboard for a moment answers by waiting instead of failing. */
static BitmapSource ClipboardImage(IDataObject data){
    object value=null;
    bool ok=ClipboardRetry.Try(delegate{
        value=null;
        if(data.GetDataPresent("PNG"))value=data.GetData("PNG");
        if(value==null&&data.GetDataPresent(DataFormats.Bitmap))value=data.GetData(DataFormats.Bitmap);
        if(value==null&&data.GetDataPresent("DeviceIndependentBitmap"))value=data.GetData("DeviceIndependentBitmap");
    });
    if(!ok)return null;
    return BitmapSourceOf(value);
}
// PNG arrives as a stream, DataFormats.Bitmap as a BitmapSource or a drawing Bitmap, and the DIB
// as raw bytes; the decoder and the drawing-to-PNG detour cover every shape without failing the paste.
static BitmapSource BitmapSourceOf(object value){
    if(value==null)return null;
    var source=value as BitmapSource;
    if(source!=null)return source;
    var stream=value as Stream;
    if(stream!=null){
        try{var decoder=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);if(decoder.Frames.Count>0)return decoder.Frames[0];}catch(Exception){}
        try{stream.Position=0;}catch(Exception){}
        try{var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.EndInit();bitmap.Freeze();return bitmap;}catch(Exception){return null;}
    }
    var drawing=value as System.Drawing.Image;
    if(drawing!=null){
        /* The bitmap belongs to the clipboard data object, so it is not disposed here: the object
           may still hand it out again. Only the stream this method owns is closed. */
        using(var memory=new MemoryStream()){
            drawing.Save(memory,System.Drawing.Imaging.ImageFormat.Png);
            memory.Position=0;
            try{var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=memory;bitmap.EndInit();bitmap.Freeze();return bitmap;}catch(Exception){return null;}
        }
    }
    return null;
}
/* True means the paste was a picture and nothing else is inserted. False keeps today's text paste,
   which is also every TXT document's answer and every clipboard that still carries text. */bool PasteClipboardImage(IDataObject data){
    /* Explorer's own copy of picture files: the clipboard carries a file list, and some programs
       put the names in the text format as well, so the picture-file list is read before the text
       check - otherwise Ctrl+V would paste the file names as text. A TXT document keeps today's
       answer, so its check still comes first. */
    bool hasText=false;string text=null;
    bool ok=ClipboardRetry.Try(delegate{hasText=data.GetDataPresent(DataFormats.UnicodeText);if(hasText)text=data.GetData(DataFormats.UnicodeText) as string;});
    if(!ok)return false;
    /* A TXT document keeps today's answer: no picture is taken, and the clipboard's text (if any)
       is what the paste writes. Checked before the bitmap is read or decoded. */
    if(PlainText)return false;
    string[] imageFiles=ClipboardImageFileList(data);
    if(imageFiles!=null){
        if(documentOperationBusy){ShowImageBusyNotice();return true;}
        InsertImageFilesAtCaret(imageFiles);
        return true;
    }
    /* The text check: a copy from a web page carries both a picture and its alt text, and that
       paste must stay a text paste exactly as before. */
    if(hasText&&!String.IsNullOrEmpty(text))return false;
    BitmapSource image=ClipboardImage(data);
    if(image==null)return false;
    if(documentOperationBusy){ShowImageBusyNotice();return true;}
    /* Saved or not, the picture waits in the shared temp folder; the save moves it beside the file. */
    string folder=PastedImageTempFolder();
    if(String.IsNullOrEmpty(folder))return false;
    string file=null;
    try{
        Directory.CreateDirectory(folder);
        file=UniqueImagePath(folder,"image-");
        WritePng(image,file);
    }catch(IOException error){DiscardPastedImage(file,error);return false;}
    catch(UnauthorizedAccessException error){DiscardPastedImage(file,error);return false;}
    catch(Exception error){DiscardPastedImage(file,error);return false;}
    /* The temporary folder is shared by every untitled document, so a tab remembers the pictures
       it pasted; the first save moves the ones this document's text still links. */
    if(path==""){
        var images=PastedImagesOf(activeDocument);
        if(images!=null)images.Add(file);
    }
    /* The drop's insertion path: one command result, applied after a pending IME commit, so undo
       takes the picture out in one step and the live view draws it like any other image. The picture
       was taken, so the paste is answered either way: a caret the command declines (inside code or
       on structure) must not fall through and paste the clipboard's text over the document as well. */
    InsertImageFilesAtCaret(new string[]{file});
    return true;
}
/* Explorer's "복사" of picture files puts a file list on the clipboard and no bitmap, so Ctrl+V
   used to do nothing at all. A list of nothing but existing image files is that paste: every file
   takes the insert rule (an Explorer drop's own answer, ImageFilesForInsert), one link per file in
   file order and one undo step. A list holding a document as well is left to the text paste, the
   same answer a mixed drop gives. Null means this was not a picture-file paste. */
string[] ClipboardImageFileList(IDataObject data){
    string[] files=null;
    bool read=ClipboardRetry.Try(delegate{
        files=null;
        if(!data.GetDataPresent(DataFormats.FileDrop))return;
        files=data.GetData(DataFormats.FileDrop) as string[];
    });
    if(!read||files==null||files.Length==0)return null;
    if(!MarkdownCommands.AllImageFiles(files))return null;
    /* A list of names that are gone is not a picture paste: the clipboard's text (if any) is. */
    string[] present=MarkdownCommands.ExistingImageFiles(files);
    return present.Length==0?null:present;
}
/* One image token of the text whose target is a file directly inside the shared temp folder, and
   the span the rewrite replaces (the target itself, '<>' included when the paste wrote them). */
sealed class PastedImageLink {
    internal int Offset,Length;
    internal bool Angle;
    internal string File;
    /* False: File is a staged picture in the shared temp folder (a save moves it). True: File is a
       picture inside the OLD document's own picture folder (a Save As copies it). */
    internal bool Copy;
    internal PastedImageLink(int offset,int length,bool angle,string file,bool copy){Offset=offset;Length=length;Angle=angle;File=file;Copy=copy;}
}
/* The full path when the reference names an existing file inside oldFolder (<olddir>\<oldname>,
   subfolders included), resolved against oldDir, else null. Web and other scheme links ("http:",
   "data:", "mailto:"), drive-relative and root-relative targets, a directory and a missing file are
   not pictures of the old folder. Same escape rule as the paste writes (ImageReferenceText). */
static string OldFolderImage(string reference,string oldDir,string oldFolder){
    string value=reference??String.Empty;
    if(value.Length>=2&&value[0]=='<'&&value[value.Length-1]=='>')value=value.Substring(1,value.Length-2);
    if(value.IndexOf('<')>=0||value.IndexOf('>')>=0)return null;
    value=ImageReferenceText(value).Replace('/','\\');
    if(value.Length==0)return null;
    int colon=value.IndexOf(':');
    if(colon>=0&&colon!=1)return null;
    try{
        bool rooted=(value.Length>=3&&value[1]==':'&&value[2]=='\\')||(value.Length>=2&&value[0]=='\\'&&value[1]=='\\');
        if(!rooted&&(colon==1||value[0]=='\\'))return null;
        string full=rooted?Path.GetFullPath(value):Path.GetFullPath(Path.Combine(oldDir,value));
        if(!ImageInsideFolder(full,oldFolder))return null;
        if(!File.Exists(full))return null;
        return full;
    }catch(Exception){return null;}
}
/* Where the text of a line begins once any indent (spaces and tabs) and any '>' blockquote markers
   (each with one optional space) are skipped; end when nothing but blanks and markers is left (a
   blank line). Any indent counts for a fence, not only up to three spaces (a fenced block inside a
   list item is indented): reading a line as a fence can only skip an image move (the picture keeps
   its absolute link and still displays), while missing a fence would rewrite code. */
static int CodeLineContent(string body,int at,int end){
    int i=at;
    while(true){
        while(i<end&&(body[i]==' '||body[i]=='\t'))i++;
        if(i<end&&body[i]=='>'){i++;if(i<end&&body[i]==' ')i++;continue;}
        return i;
    }
}
/* A fence opener: three or more backticks or tildes; a backtick fence whose info string holds a
   backtick is an inline span ("```x```"), not a fence. */
static bool CodeFenceOpens(string body,int at,int end,out char marker,out int length){
    marker='\0';length=0;
    int i=CodeLineContent(body,at,end);
    if(i<0||i>=end)return false;
    char c=body[i];
    if(c!='`'&&c!='~')return false;
    int e=i;
    while(e<end&&body[e]==c)e++;
    if(e-i<3)return false;
    if(c=='`'){for(int k=e;k<end;k++)if(body[k]=='`')return false;}
    marker=c;length=e-i;
    return true;
}
/* A fence closer: the same character, at least as long, and nothing but blanks after it. */
static bool CodeFenceCloses(string body,int at,int end,char marker,int length){
    int i=CodeLineContent(body,at,end);
    if(i<0||i>=end||body[i]!=marker)return false;
    int e=i;
    while(e<end&&body[e]==marker)e++;
    if(e-i<length)return false;
    for(int k=e;k<end;k++)if(body[k]!=' '&&body[k]!='\t')return false;
    return true;
}
/* The inline code spans of one paragraph. runs holds its backtick runs as (position, length)
   pairs, already effective: MarkdownCodeRanges drops the first backtick of a run that an odd
   number of backslashes escapes, so an opener and a closer compare the same way. A run opens at
   its position and closes at the next run of exactly the same length. A run with no closer is
   literal. cache keeps, per length, the first run found after the last search, so a paragraph is
   read once for each distinct length. */
static void CodeSpanFlush(List<int> runs,List<int> ranges){
    int count=runs.Count/2;
    if(count>=2){
        var cache=new Dictionary<int,int>();
        int i=0;
        while(i<count){
            int pos=runs[i*2],length=runs[i*2+1];
            int found;
            if(!cache.TryGetValue(length,out found)||(found!=Int32.MaxValue&&found<=i)){
                found=Int32.MaxValue;
                for(int j=i+1;j<count;j++)if(runs[j*2+1]==length){found=j;break;}
                cache[length]=found;
            }
            if(found==Int32.MaxValue){i++;continue;}
            ranges.Add(pos);
            ranges.Add(runs[found*2]+runs[found*2+1]);
            i=found+1;
        }
    }
    runs.Clear();
}
/* The code of a Markdown body as sorted, non-overlapping [start,end) pairs in one flat list
   (start0,end0,start1,end1,...): fenced blocks (opening line start to closing line end, an
   unclosed fence to the end of the body) and inline code spans outside them (a span may cross a
   line break but not a blank line). Indented code is not code here: lists indent pictures. One
   linear pass; it runs once per save, never on a keystroke. */
static List<int> MarkdownCodeRanges(string body){
    var ranges=new List<int>();
    if(String.IsNullOrEmpty(body))return ranges;
    var runs=new List<int>();
    int n=body.Length,at=0;
    char fenceChar='\0';
    int fenceLength=0,fenceStart=0;
    while(at<n){
        int end=at;
        while(end<n&&body[end]!='\n'&&body[end]!='\r')end++;
        int next=end;
        if(next<n){
            if(body[next]=='\r'&&next+1<n&&body[next+1]=='\n')next+=2;
            else next++;
        }
        if(fenceChar!='\0'){
            if(CodeFenceCloses(body,at,end,fenceChar,fenceLength)){
                ranges.Add(fenceStart);
                ranges.Add(end);
                fenceChar='\0';
            }
            at=next;
            continue;
        }
        char marker;
        int length;
        if(CodeFenceOpens(body,at,end,out marker,out length)){
            CodeSpanFlush(runs,ranges);
            fenceChar=marker;fenceLength=length;fenceStart=at;
            at=next;
            continue;
        }
        /* A line of only blanks and '>' markers ("> >  ") is blank: it ends the paragraph too. */
        bool blank=CodeLineContent(body,at,end)>=end;
        if(blank){
            CodeSpanFlush(runs,ranges);
        }else{
            int i=at;
            while(i<end){
                if(body[i]!='`'){i++;continue;}
                int e=i;
                while(e<end&&body[e]=='`')e++;
                int slashes=0;
                for(int k=i-1;k>=at&&body[k]=='\\';k--)slashes++;
                /* An escaped first backtick is literal: the run starts one later and one shorter (gone if it was alone). */
                if((slashes&1)!=0){
                    if(e-i>1){runs.Add(i+1);runs.Add(e-i-1);}
                }else{runs.Add(i);runs.Add(e-i);}
                i=e;
            }
        }
        at=next;
    }
    if(fenceChar!='\0'){
        ranges.Add(fenceStart);
        ranges.Add(n);
    }else CodeSpanFlush(runs,ranges);
    return ranges;
}
/* Every image link of the body whose target names a file directly inside PastedImageTempFolder().
   A document restored from a session (or a crash draft) has no per-tab list, so its first save
   reads the document itself; this runs once per save, never on a keystroke. The token read is the
   one the paste writes, "![label](target)": the label holds no ']' (ImageAlt drops brackets) and
   the target is plain or wrapped in '<...>'. A token that cannot be read is left alone, and a
   target outside the temp folder is never a pasted picture. A token inside a fenced code block or
   an inline code span is code, literal text: it is neither read nor moved nor copied. */
static void PastedImageLinks(string body,List<PastedImageLink> links,string oldDir,string oldFolder){
    if(String.IsNullOrEmpty(body))return;
    /* No "![" at all (a plain Ctrl+S): nothing to read, so the code ranges are not even built. */
    if(body.IndexOf("![",StringComparison.Ordinal)<0)return;
    List<int> code=MarkdownCodeRanges(body);
    int codeAt=0,at=0;
    while(at<body.Length){
        int open=body.IndexOf("![",at,StringComparison.Ordinal);
        if(open<0)return;
        at=open+2;
        /* Code stays literal text: an "![" inside a fenced block or an inline code span is no link.
           open only grows, so one moving index over the sorted ranges keeps this linear. */
        while(codeAt<code.Count&&code[codeAt+1]<=open)codeAt+=2;
        if(codeAt<code.Count&&code[codeAt]<=open){at=code[codeAt+1];continue;}
        if(open>0&&body[open-1]=='\\')continue;   /* "\![x](y)" is literal text, not a picture */
        int label=body.IndexOf(']',open+2);
        if(label<0)return;
        if(label+1>=body.Length||body[label+1]!='(')continue;
        int start=label+2,end=-1;
        bool angle=false;
        if(start<body.Length&&body[start]=='<'){
            int close=body.IndexOf('>',start+1);
            if(close>0&&close+1<body.Length&&body[close+1]==')'){end=close+1;angle=true;}
        }else{
            int close=body.IndexOf(')',start);
            if(close>=0)end=close;
        }
        if(end<0){at=start;continue;}
        string raw=body.Substring(start,end-start);
        string file=PastedImageTempFile(raw);
        bool copy=false;
        if(file==null&&oldFolder!=null){file=OldFolderImage(raw,oldDir,oldFolder);copy=file!=null;}
        if(file!=null)links.Add(new PastedImageLink(start,end-start,angle,file,copy));
        at=end+1;
    }
}
/* The full path when the reference names a file directly inside the shared temp folder, else null.
   The paste writes an absolute path with '/' and escapes '%', '#', '?', '(', ')' and ' '
   (ImageReference with no document path); that form and a '<...>' wrapper are read here, and the
   comparison is on the full, normalized path, so a differently spelled folder still matches. A
   relative target, another folder, a missing file or a directory is not a pasted picture. */
static string PastedImageTempFile(string reference){
    string value=reference??String.Empty;
    if(value.Length>=2&&value[0]=='<'&&value[value.Length-1]=='>')value=value.Substring(1,value.Length-2);
    if(value.IndexOf('<')>=0||value.IndexOf('>')>=0)return null;
    value=ImageReferenceText(value).Replace('/','\\');
    string full,folder,parent;
    try{
        /* Fully qualified only (drive absolute or UNC): a drive-relative "C:name" would resolve
           against the process's own current folder, which a pasted link never means. */
        bool qualified=(value.Length>=3&&value[1]==':'&&value[2]=='\\')||(value.Length>=2&&value[0]=='\\'&&value[1]=='\\');
        if(!qualified)return null;
        full=Path.GetFullPath(value);
        folder=Path.GetFullPath(PastedImageTempFolder());
        parent=Path.GetDirectoryName(full);
    }catch(Exception){return null;}
    if(String.IsNullOrEmpty(parent)||!String.Equals(parent,folder,StringComparison.OrdinalIgnoreCase))return null;
    if(!File.Exists(full))return null;
    return full;
}
// The paste's escape rule (MarkdownCommands.ImageReference): '%' first, then '#', '?', '(' ,')'
// and ' '. A '%' that is not followed by two hex digits stays literal.
static string ImageReferenceText(string value){
    if(value.IndexOf('%')<0)return value;
    var result=new StringBuilder(value.Length);
    for(int i=0;i<value.Length;i++){
        char character=value[i];
        if(character=='%'&&i+2<value.Length){
            int high=ImageHexDigit(value[i+1]),low=ImageHexDigit(value[i+2]);
            if(high>=0&&low>=0){result.Append((char)(high*16+low));i+=2;continue;}
        }
        result.Append(character);
    }
    return result.ToString();
}
static int ImageHexDigit(char value){
    if(value>='0'&&value<='9')return value-'0';
    if(value>='a'&&value<='f')return value-'a'+10;
    if(value>='A'&&value<='F')return value-'A'+10;
    return -1;
}
// One source picture, once: the per-tab list and the text can name the same file (and the same
// file can be linked twice), so the full path is compared case-insensitively before it is added.
static void ImageMoveSource(List<string> sources,string file){
    if(String.IsNullOrEmpty(file))return;
    string full;
    try{full=Path.GetFullPath(file);}catch(Exception){return;}
    for(int i=0;i<sources.Count;i++)if(String.Equals(sources[i],full,StringComparison.OrdinalIgnoreCase))return;
    sources.Add(full);
}
/* The replacements applied as one command, sorted by offset. An image target span and the old
   absolute path inside it are the same rewrite twice; the longer form at one offset wins (so a
   '<...>' target keeps its wrapper) and every later edit touching a kept range is dropped, since
   the host applies non-overlapping edits. */
static List<SourceEdit> ImageEditsInOrder(List<SourceEdit> edits){
    edits.Sort(ImageCompareEdits);
    var kept=new List<SourceEdit>();
    int end=-1;
    for(int i=0;i<edits.Count;i++){
        SourceEdit edit=edits[i];
        if(edit.Offset<end)continue;
        kept.Add(edit);
        end=edit.Offset+edit.Length;
    }
    return kept;
}
static int ImageCompareEdits(SourceEdit a,SourceEdit b){
    int order=a.Offset.CompareTo(b.Offset);
    if(order!=0)return order;
    return b.Length.CompareTo(a.Length);
}
/* What one first save moved, so a save that did not write can put it back (SaveAsync): the files
   that left the shared temp folder, and each rewritten link span as the text holds it now. */
sealed class PastedImageMoveState {
    internal DocumentTab Tab;
    internal SourceEditor Editor;
    internal readonly List<string> From=new List<string>();
    internal readonly List<string> To=new List<string>();
    /* Files a Save As copy newly wrote into the new picture folder; only these are removed again
       when the save does not write (an identical file that was reused is not ours). */
    internal readonly List<string> Copied=new List<string>();
    internal readonly List<PastedImageUndo> Undo=new List<PastedImageUndo>();
}
/* One link span after a rewrite: Offset is the span in the rewritten text, Expected what the
   rewrite put there and Original the target text it replaced. */
sealed class PastedImageUndo {
    internal int Offset;
    internal string Expected,Original;
    internal PastedImageUndo(int offset,string expected,string original){Offset=offset;Expected=expected;Original=original;}
}
/* Every save (user, 2026-11-03): the pictures the text links from the shared temp folder move
   into <docname>\ beside the save target, and their absolute links become the relative form the
   drop writes; a Save As of a saved document also COPIES the pictures it links from the old
   <olddir>\<oldname>\ into the new folder (the old file and folder stay as they are). A plain
   Ctrl+S with no staged picture costs one parse of the body. Only links the text still holds move: a picture that was pasted and then deleted or
   undone has no link and stays in the temp folder. Only parsed image links are rewritten, so a
   path that merely appears in prose is left as text, and an image link inside a fenced code block
   or an inline code span is skipped (MarkdownCodeRanges): its picture is not moved or copied. Every replacement is one
   command; a staged picture that any OTHER open document (any tab of any window, saved or not)
   also links is COPIED instead of moved, so that document's link keeps working (user, 2026-11-04,
   TempImagesLinkedElsewhere); the state that can undo it goes back to SaveAsync, which uses it when the save itself
   does not write. A picture that cannot be moved keeps its absolute link and the paste still
   reaches the file. A document restored from a session or a crash draft has no per-tab list; its
   own text links are read the same way, and a link to any file outside that folder is never
   touched. */
PastedImageMoveState MovePastedImagesToDocument(string target){
    if(source==null||source.Document==null)return null;
    /* A clipboard picture has no place in a TXT document: the save that converts the document to
       .txt writes plain text, so the links are not rewritten there. */
    if(PlainText||String.Equals(Path.GetExtension(target),".txt",StringComparison.OrdinalIgnoreCase))return null;
    string folder=PastedImageFolder(target);
    if(String.IsNullOrEmpty(folder))return null;
    FlushDocumentChanges();
    /* Save As of a saved document into another name or folder (user, 2026-11-03): the pictures the
       text links from the OLD <olddir>\<oldname>\ are copied (never moved) beside the new file. The
       old folder is only looked at when the target really differs and has its own picture folder. */
    string oldDir=null,oldFolder=null;
    if(path.Length>0&&!SamePicturePath(path,target)){
        oldFolder=PastedImageFolder(path);
        try{oldDir=Path.GetDirectoryName(Path.GetFullPath(path));}catch(Exception){oldDir=null;}
        if(String.IsNullOrEmpty(oldFolder)||String.IsNullOrEmpty(oldDir)||String.Equals(oldFolder.TrimEnd('\\'),folder.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)){oldFolder=null;oldDir=null;}
    }
    string body=source.Document.Text;
    var links=new List<PastedImageLink>();
    PastedImageLinks(body,links,oldDir,oldFolder);
    if(links.Count==0)return null;
    /* The text is the truth: a link that is still there names a picture that still belongs to this
       document. The per-tab list described the same pastes, so it never decides a move. */
    var sources=new List<string>();
    var copySources=new List<string>();
    for(int i=0;i<links.Count;i++)ImageMoveSource(links[i].Copy?copySources:sources,links[i].File);
    var result=new CommandResult();
    var edits=new List<SourceEdit>();
    var move=new PastedImageMoveState {Tab=activeDocument,Editor=source};
    for(int i=0;i<copySources.Count;i++){
        string original=copySources[i];
        string destination;
        bool created;
        try{
            Directory.CreateDirectory(folder);
            destination=CopiedImageFile(original,folder,out created);
        }catch(IOException){continue;}
        catch(UnauthorizedAccessException){continue;}
        catch(ArgumentException){continue;}
        if(destination==null)continue;
        if(created)move.Copied.Add(destination);
        string copyReference=MarkdownCommands.ImageReference(target,destination);
        for(int k=0;k<links.Count;k++){
            var link=links[k];
            if(!link.Copy||!String.Equals(link.File,original,StringComparison.OrdinalIgnoreCase))continue;
            string text=link.Angle?"<"+copyReference+">":copyReference;
            /* Same text already (the new folder has the old folder's name): nothing to rewrite. */
            if(String.Equals(body.Substring(link.Offset,link.Length),text,StringComparison.Ordinal))continue;
            edits.Add(new SourceEdit(link.Offset,link.Length,text));
        }
    }
    /* Staged pictures another open document also links stay in the temp folder (user, 2026-11-04):
       they are copied, and only the ones nobody else links are moved. */
    HashSet<string> shared=sources.Count==0?null:TempImagesLinkedElsewhere(sources);
    for(int i=0;i<sources.Count;i++){
        string old=sources[i];
        string oldReference=MarkdownCommands.ImageReference("",old);
        string name=Path.GetFileName(old);
        string destination=Path.Combine(folder,name);
        bool keepStaged=shared!=null&&shared.Contains(old);
        try{
            Directory.CreateDirectory(folder);
            int number=1;
            while(File.Exists(destination)){
                number++;
                destination=Path.Combine(folder,Path.GetFileNameWithoutExtension(name)+"-"+number.ToString(CultureInfo.InvariantCulture)+Path.GetExtension(name));
            }
            if(keepStaged)File.Copy(old,destination,false);
            else File.Move(old,destination);
        }catch(IOException){continue;}
        catch(UnauthorizedAccessException){continue;}
        catch(ArgumentException){continue;}
        /* A copy leaves nothing to put back in the temp folder; a failed save only removes it. */
        if(keepStaged)move.Copied.Add(destination);
        else{
            move.From.Add(old);
            move.To.Add(destination);
        }
        string newReference=MarkdownCommands.ImageReference(target,destination);
        if(String.Equals(oldReference,newReference,StringComparison.Ordinal))continue;
        /* The target span as the text holds it (the '<...>' wrapper included when the paste wrote
           one): a path that only appears in prose or a code block is not an image link. */
        for(int k=0;k<links.Count;k++){
            var link=links[k];
            if(link.Copy||!String.Equals(link.File,old,StringComparison.OrdinalIgnoreCase))continue;
            edits.Add(new SourceEdit(link.Offset,link.Length,link.Angle?"<"+newReference+">":newReference));
        }
    }
    if(edits.Count>0){
        var kept=ImageEditsInOrder(edits);
        int moved=0;
        for(int i=0;i<kept.Count;i++){
            SourceEdit edit=kept[i];
            /* The positions the command applies are the ones the undo has to find afterwards. */
            move.Undo.Add(new PastedImageUndo(edit.Offset+moved,edit.Text,body.Substring(edit.Offset,edit.Length)));
            moved+=edit.Text.Length-edit.Length;
            result.Add(edit.Offset,edit.Length,edit.Text);
        }
        result.SortEdits();
        int caret=Math.Max(0,Math.Min(source.CaretIndex,body.Length));
        /* The caret keeps its place in the text: every replacement shorter or longer than the
           absolute path shifts it by the difference, counted only before the caret. */
        int shift=0;
        foreach(var edit in result.Edits)if(edit.Offset<caret)shift+=edit.Text.Length-edit.Length;
        result.SelectionStart=Math.Max(0,Math.Min(caret+shift,source.Document.TextLength));
        result.SelectionLength=0;
        ApplyCommandResult(result);
    }
    return move.From.Count==0&&move.Copied.Count==0&&move.Undo.Count==0?null:move;
}
/* The staged pictures (full paths, from candidates) that another open document also links (user,
   2026-11-04): every tab of every window of this process except the tab being saved, saved or
   not. Each tab is read from its live editor buffer when it has one, else from the text it kept. The
   same parser as the save (PastedImageLinks) reads it, so a link inside code is no link here
   either. A tab that cannot be read counts as linking everything (a copy is the safe answer).
   Runs on a save that has pictures to move, never on a keystroke. */
HashSet<string> TempImagesLinkedElsewhere(List<string> candidates){
    var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var wanted=new HashSet<string>(candidates,StringComparer.OrdinalIgnoreCase);
    var windows=new List<MainWindow>();
    windows.Add(this);
    if(Application.Current!=null){
        foreach(Window candidate in Application.Current.Windows){
            MainWindow editor=candidate as MainWindow;
            if(editor!=null&&!Object.ReferenceEquals(editor,this))windows.Add(editor);
        }
    }
    for(int w=0;w<windows.Count&&found.Count<wanted.Count;w++){
        MainWindow window=windows[w];
        var tabs=new List<DocumentTab>(window.documents);
        for(int t=0;t<tabs.Count&&found.Count<wanted.Count;t++){
            DocumentTab tab=tabs[t];
            if(Object.ReferenceEquals(tab,activeDocument))continue;
            try{
                /* The live editor first: its buffer is newer than the text a tab last stored. */
                string body;
                if(Object.ReferenceEquals(tab,window.activeDocument))body=window.source!=null&&window.source.Document!=null?window.source.Document.Text:window.text;
                else body=tab.Source!=null&&tab.Source.Document!=null?tab.Source.Document.Text:tab.Text;
                var links=new List<PastedImageLink>();
                PastedImageLinks(body,links,null,null);
                for(int k=0;k<links.Count;k++)if(wanted.Contains(links[k].File))found.Add(links[k].File);
            }catch(Exception){return wanted;}
        }
    }
    return found;
}
// True when both names are the same file (full paths, case-insensitive).
static bool SamePicturePath(string left,string right){
    try{return String.Equals(Path.GetFullPath(left),Path.GetFullPath(right),StringComparison.OrdinalIgnoreCase);}
    catch(Exception){return String.Equals(left,right,StringComparison.OrdinalIgnoreCase);}
}
/* A first save that did not write leaves the tab untitled with its links relative to a file that
   was never made: the pictures go back to the shared temp folder and every span the rewrite
   changed returns to its original text, so a later save starts from the same document. Best
   effort: a picture that cannot move back keeps its new place and its relative link. */
void RestorePastedImagesAfterFailedSave(PastedImageMoveState move){
    if(move==null)return;
    for(int i=move.From.Count-1;i>=0;i--){
        try{if(File.Exists(move.To[i])&&!File.Exists(move.From[i]))File.Move(move.To[i],move.From[i]);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    /* A Save As copy that did not write: only the copies this save just made are removed; the old
       document's folder was never touched. */
    for(int i=move.Copied.Count-1;i>=0;i--){
        try{if(File.Exists(move.Copied[i]))File.Delete(move.Copied[i]);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
    if(move.Undo.Count==0)return;
    if(!Object.ReferenceEquals(move.Tab,activeDocument)||!Object.ReferenceEquals(move.Editor,source))return;
    if(source==null||source.Document==null)return;
    string body=source.Document.Text;
    var result=new CommandResult();
    var undo=new List<PastedImageUndo>();
    for(int i=0;i<move.Undo.Count;i++){
        PastedImageUndo edit=move.Undo[i];
        if(edit.Offset<0||edit.Offset+edit.Expected.Length>body.Length)continue;
        /* Another owner changed the span since the rewrite: leave it rather than replace text that
           is no longer the link the move wrote. */
        if(!String.Equals(body.Substring(edit.Offset,edit.Expected.Length),edit.Expected,StringComparison.Ordinal))continue;
        result.Add(edit.Offset,edit.Expected.Length,edit.Original);
        undo.Add(edit);
    }
    if(undo.Count==0)return;
    result.SortEdits();
    int caret=Math.Max(0,Math.Min(source.CaretIndex,body.Length));
    int shift=0;
    for(int i=0;i<undo.Count;i++)if(undo[i].Offset<caret)shift+=undo[i].Original.Length-undo[i].Expected.Length;
    result.SelectionStart=Math.Max(0,Math.Min(caret+shift,source.Document.TextLength));
    result.SelectionLength=0;
    ApplyCommandResult(result);
}
// The clipboard read at the key press can be refused: another program holding the clipboard open
// answers CLIPBRD_E_CANT_OPEN for a few milliseconds. The read waits it out (ClipboardRetry), and
// a refusal that outlasts the retries is named in the status line instead of the key seeming to
// do nothing. False means nothing was pasted.
bool PasteClipboard(bool plain){
    if(!editing)return true;
    IDataObject data;
    if(!ClipboardRetry.TryGetDataObject(out data))return ShowClipboardBusy();
    /* The insertion path may still raise a clipboard exception from the captured object; as
       before, it must not escape the key handler. */
    try{
        /* An image with no text is a picture, unless the user asked for the text-only paste. */
        if(!plain&&PasteClipboardImage(data))return true;
        return PasteData(data,plain);
    }
    catch(System.Runtime.InteropServices.ExternalException){return false;}
    catch(IOException){return false;}
    catch(UnauthorizedAccessException){return false;}
    catch(ArgumentException){return false;}
}
// The answer to a clipboard that stayed locked: nothing is inserted, as before, but the status
// line says why the key did nothing.
bool ShowClipboardBusy(){
    ShowStatusNotice(T("클립보드를 열 수 없습니다. 잠시 후 다시 붙여넣으세요."));
    return false;
}
// A paste is the one command whose input lives outside the document: the formats are read once,
// at the key press, into a snapshot, and only the insertion is queued through RunRichCommand
// (StructuralInput.cs). Re-reading the OLE object after the composition release could read a
// clipboard the user has already replaced, and inserting under the composition is the document
// write that kills the process. The snapshot is also what makes the paste show the content that
// was on the clipboard when the key was pressed.
sealed class PasteSnapshot {internal string Value,Markdown,Html,Rtf;internal bool HasHtml,HasRtf;}
// Every format is read in its own ClipboardRetry step: the clipboard lock is taken per call, and
// one refusal used to abort the rest of the read through a shared try/catch. A refused plain-text
// read leaves nothing to paste, so it answers null and the caller names the failure; a refused
// richer format only loses that format, which is what the swallowed exception did before.
PasteSnapshot CapturePaste(IDataObject data,bool plain){
    var snapshot=new PasteSnapshot();
    string value=null;
    if(!ClipboardRetry.Try(delegate{value=data.GetDataPresent(DataFormats.UnicodeText)?data.GetData(DataFormats.UnicodeText) as string:null;}))return null;
    snapshot.Value=value;
    /* 11-01: a plain paste needs MDNote's own source too, to write its visible text instead of the
       Markdown the copy put in the text format (LivePaste). */
    string markdown=null;
    if(ClipboardRetry.Try(delegate{markdown=MarkdownClipboard.Read(data);}))snapshot.Markdown=MarkdownClipboard.CanonicalNewlines(markdown);
    if(!plain){
        bool hasHtml=false;string html=null;
        if(ClipboardRetry.Try(delegate{hasHtml=data.GetDataPresent(DataFormats.Html);if(hasHtml)html=data.GetData(DataFormats.Html) as string;})&&hasHtml){
            if(!String.IsNullOrEmpty(html)&&html.Length<8*1024*1024){
                snapshot.Html=html;snapshot.HasHtml=true;
                /* A spreadsheet range is also on the clipboard as tab-separated text. When its
                   HTML converts to nothing (Excel's <col>-wrapped range used to), the same range
                   is written as a Markdown table here instead of one long line of text; the
                   HTML conversion itself now answers null, so every other failure falls through
                   to the RTF and then the plain text rather than inserting nothing. A code line
                   keeps the copied text verbatim (P6). */
                if(snapshot.Markdown==null&&PasteSpreadsheetRange(data)&&!PasteOnCodeLine()&&!PasteHtmlConvertible(html))
                    snapshot.Markdown=MarkdownClipboard.CanonicalNewlines(PasteTextTable(snapshot.Value));
            }
        }
        bool hasRtf=false;string rtf=null;
        if(ClipboardRetry.Try(delegate{hasRtf=data.GetDataPresent(DataFormats.Rtf);if(hasRtf)rtf=data.GetData(DataFormats.Rtf) as string;})&&hasRtf){
            if(!String.IsNullOrEmpty(rtf)&&rtf.Length<8*1024*1024){snapshot.Rtf=rtf;snapshot.HasRtf=true;}
        }
    }
    return snapshot;
}
// LivePaste reads the formats itself, so the captured content is mirrored into a fresh DataObject:
// the queued run must never depend on the OLE object the clipboard handed out at key time.
static IDataObject CapturedPasteData(PasteSnapshot snapshot,bool plain){
    var data=new DataObject();
    if(snapshot.Value!=null)data.SetData(DataFormats.UnicodeText,snapshot.Value);
    if(snapshot.Markdown!=null)MarkdownClipboard.Set(data,snapshot.Markdown);
    if(!plain&&snapshot.HasHtml)data.SetData(DataFormats.Html,snapshot.Html);
    if(!plain&&snapshot.HasRtf)data.SetData(DataFormats.Rtf,snapshot.Rtf);
    return data;
}
// A spreadsheet copy carries these beside the HTML; a Word document or a web page does not, so the
// extra conversion the table fallback needs stays off the ordinary paste path.
static bool PasteSpreadsheetRange(IDataObject data){
    bool spreadsheet=false;
    bool read=ClipboardRetry.Try(delegate{spreadsheet=data.GetDataPresent("Csv")||data.GetDataPresent("Biff8")||data.GetDataPresent("Biff12")||data.GetDataPresent("Biff5")
        ||data.GetDataPresent("XML Spreadsheet")||data.GetDataPresent("DataInterchangeFormat");});
    return read&&spreadsheet;
}
// The HTML path's own converter decides whether the HTML is usable at all: when it cannot turn the
// copy into Markdown, the spreadsheet text answers instead of a silent empty paste.
static bool PasteHtmlConvertible(string html){
    try{return !String.IsNullOrEmpty(ClipboardMarkdown(html));}
    catch(Exception){return false;}
}
// A fenced block and an inline code span take only the copied text (the P6 rule), so the table a
// capture would add for a spreadsheet range must not replace it there.
bool PasteOnCodeLine(){
    try{
        if(!LivePreviewAttached||livePreview==null||source==null||source.Document==null)return false;
        int offset=source.SelectionStart;
        return livePreview.InCode(offset)||LiveInlineCodeAt(offset);
    }catch(Exception){return false;}   /* a paste must never fail on this probe */
}
// A spreadsheet range is also on the clipboard as tab-separated text. When its HTML converts to
// nothing, a clean rectangle of tab-separated fields becomes the same Markdown table instead of
// one long line of text. Excel quotes a field that holds a tab, a quote or a line break, so the
// quoted form is read the way Excel writes it (one cell holding a line break becomes "a<br>b").
static string PasteTextTable(string value){
    if(String.IsNullOrEmpty(value))return null;
    var rows=new List<List<string>>();
    var row=new List<string>();var field=new StringBuilder();bool quoted=false;
    for(int i=0;i<value.Length;i++){
        char c=value[i];
        if(quoted){
            if(c!='"'){field.Append(c);continue;}
            if(i+1<value.Length&&value[i+1]=='"'){field.Append('"');i++;continue;}
            quoted=false;continue;
        }
        if(c=='"'){if(field.Length==0){quoted=true;continue;}field.Append(c);continue;}
        if(c=='\t'){row.Add(field.ToString());field.Length=0;continue;}
        if(c=='\r'||c=='\n'){
            if(c=='\r'&&i+1<value.Length&&value[i+1]=='\n')i++;
            row.Add(field.ToString());field.Length=0;rows.Add(row);row=new List<string>();continue;
        }
        field.Append(c);
    }
    row.Add(field.ToString());rows.Add(row);
    while(rows.Count>0&&rows[rows.Count-1].Count==1&&rows[rows.Count-1][0].Length==0)rows.RemoveAt(rows.Count-1);
    if(rows.Count<2||rows.Count>1000)return null;
    int columns=rows[0].Count;
    if(columns<2||columns>64)return null;
    foreach(var line in rows)if(line.Count!=columns)return null;
    var lines=new List<string>();
    for(int r=0;r<rows.Count;r++){
        var cells=new List<string>();
        for(int c=0;c<columns;c++)cells.Add(PasteTablePipes(rows[r][c].Replace("\r\n","\n").Replace('\r','\n').Replace("\n","<br>")));
        lines.Add("| "+String.Join(" | ",cells.ToArray())+" |");
        if(r==0){var separators=new string[columns];for(int c=0;c<columns;c++)separators[c]="---";lines.Add("| "+String.Join(" | ",separators)+" |");}
    }
    return String.Join("\n",lines.ToArray());
}
bool PasteData(IDataObject data,bool plain){
    if(data==null||!editing||documentOperationBusy)return true;
    PasteSnapshot snapshot=CapturePaste(data,plain);
    if(snapshot==null)return ShowClipboardBusy();
    PasteCaptured(snapshot,plain);
    return true;
}
void PasteCaptured(PasteSnapshot snapshot,bool plain){if(snapshot==null||!editing||documentOperationBusy)return;if(source!=null&&source.ImeBusy){source.RunAfterComposition(delegate{PasteCaptured(snapshot,plain);});return;}/* #7: paste after the pending syllable lands */PasteCapturedNow(snapshot,plain);}
// Y2: a rich paste that lands inside an emphasis run must not repeat that run's own delimiter.
// The text is already bold/italic/struck/highlighted, and the nested pair closes the outer one early
// ("**a**bold** b**" leaves the markers as literal text). The caret's line is read with the live
// parser's pairing rules (token order, escapes, code spans, link labels and span/font tags
// skipped), once per paste, and only for a caret without a selection. At the inner edge of a run
// a different kind that would start (or end) with the same marker character is dropped too,
// because the two runs would merge into "***" and pair differently.
[ThreadStatic] static bool pasteEmphasisContext,pasteInsideBold,pasteInsideItalic,pasteInsideStrike,pasteInsideHighlight,pasteAtOpenEdge,pasteAtCloseEdge;
[ThreadStatic] static char pasteEdgeMarker;
internal static bool PasteBoldSuppressed{get{return pasteEmphasisContext&&(pasteInsideBold||((pasteAtOpenEdge||pasteAtCloseEdge)&&pasteEdgeMarker=='*'));}}
internal static bool PasteItalicSuppressed{get{return pasteEmphasisContext&&(pasteInsideItalic||((pasteAtOpenEdge||pasteAtCloseEdge)&&pasteEdgeMarker=='*'));}}
internal static bool PasteStrikeSuppressed{get{return pasteEmphasisContext&&(pasteInsideStrike||((pasteAtOpenEdge||pasteAtCloseEdge)&&pasteEdgeMarker=='~'));}}
internal static bool PasteHighlightSuppressed{get{return pasteEmphasisContext&&(pasteInsideHighlight||((pasteAtOpenEdge||pasteAtCloseEdge)&&pasteEdgeMarker=='='));}}
static void ArmPasteEmphasis(SourceEditor editor){
    pasteEmphasisContext=false;pasteInsideBold=false;pasteInsideItalic=false;pasteInsideStrike=false;pasteInsideHighlight=false;pasteAtOpenEdge=false;pasteAtCloseEdge=false;pasteEdgeMarker='\0';
    if(editor==null||editor.Document==null||editor.SelectionLength!=0)return;
    int caret=editor.SelectionStart;
    if(caret<0||caret>editor.Document.TextLength)return;
    var line=editor.Document.GetLineByOffset(caret);
    string text=editor.Document.GetText(line.Offset,line.Length);
    pasteEmphasisContext=true;
    PasteScanEmphasis(text,caret-line.Offset);
}
// One pass over the caret's line with the live parser's token order and opener/closer rules.
// Escapes, code spans, link targets and span/font tags are skipped; the walk is bounded by the
// parser's own budget so a pathological line cannot make a paste quadratic.
static void PasteScanEmphasis(string text,int column){
    int[] opens={-1,-1,-1,-1,-1,-1};
    string[] tokens={"**","__","*","_","~~","=="};
    int budget=text.Length*4+64;
    // A link label owns its own inline markup while the "](url)" / "][id]" tail is hidden, so a
    // marker inside a URL never pairs with the line (MarkdownLivePreview.ParseInline). The walk
    // saves the opening state, walks the label and jumps the tail whole.
    int linkResume=-1,linkSkip=-1;int[] linkOpens=null;
    for(int i=0;i<text.Length;){
        if(budget--<=0)return;
        if(linkResume>=0&&i>=linkResume){
            if(linkOpens!=null){Array.Copy(linkOpens,opens,opens.Length);linkOpens=null;}
            i=linkSkip;linkResume=-1;linkSkip=-1;continue;
        }
        char c=text[i];
        if(c=='\\'){i+=2;continue;}
        if(c=='`'){
            int fence=1;while(i+fence<text.Length&&text[i+fence]=='`')fence++;
            int close=PasteCodeClose(text,i+fence,fence,ref budget);
            if(close<0){if(budget<=0)return;i+=fence;continue;}
            // A span that closes past the label's "]" belongs to the hidden tail: the walk keeps
            // the backtick literal and lets the label end restore the opening state (ParseInline).
            if(linkResume>=0&&close>=linkResume){i+=fence;continue;}
            i=close+fence;continue;
        }
        if(c=='!'&&linkResume<0&&i+2<text.Length&&text[i+1]=='['){
            // An image is one object: its alt text and target are never markers to the parser.
            int labelEnd=PasteFind(text,"]",i+2,ref budget);
            if(labelEnd>=0&&labelEnd+1<text.Length&&text[labelEnd+1]=='('){
                int imageClose=PasteParenClose(text,labelEnd+2,ref budget);
                if(imageClose>=0){i=imageClose+1;continue;}
            }
        }
        if(c=='['&&linkResume<0){
            int note=-1;
            if(i+2<text.Length&&text[i+1]=='^')note=PasteFind(text,"]",i+2,ref budget);
            if(note>i+2&&(note+1>=text.Length||text[note+1]!=':')){i=note+1;continue;}
            if(i==0||text[i-1]!='!'){
                int labelEnd=PasteLabelEnd(text,i,ref budget);
                if(labelEnd>i+1){
                    if(labelEnd+1<text.Length&&text[labelEnd+1]=='['){
                        int idEnd=PasteFind(text,"]",labelEnd+2,ref budget);
                        if(idEnd>=0){linkOpens=(int[])opens.Clone();linkResume=labelEnd;linkSkip=idEnd+1;i++;continue;}
                    }
                    if(labelEnd+1<text.Length&&text[labelEnd+1]=='('){
                        int close=PasteFind(text,")",labelEnd+2,ref budget);
                        if(close>=0){linkOpens=(int[])opens.Clone();linkResume=labelEnd;linkSkip=close+1;i++;continue;}
                    }
                }
            }
        }
        if(c=='<'&&(PasteTagAt(text,i,"span")||PasteTagAt(text,i,"font"))){
            // An inline tag's attributes are hidden markup, not emphasis ("font-family:'A_B'").
            int from=i+5;int limit=Math.Min(512,text.Length-from);
            int gt=limit>0?text.IndexOf('>',from,limit):-1;
            if(gt>=0){i=gt+1;continue;}
        }
        int kind=-1;for(int t=0;t<tokens.Length;t++)if(PasteToken(text,i,tokens[t])){kind=t;break;}
        if(kind<0){i++;continue;}
        int length=tokens[kind].Length;
        // The same intraword "_" guard ParseInline uses (char.IsLetterOrDigit, not .NET \w).
        if(c=='_'&&i>0&&i+length<text.Length&&char.IsLetterOrDigit(text[i-1])&&char.IsLetterOrDigit(text[i+length])){i+=length;continue;}
        if(opens[kind]<0){if(i+length<text.Length&&!char.IsWhiteSpace(text[i+length]))opens[kind]=i;}
        else if(i>opens[kind]+length&&!char.IsWhiteSpace(text[i-1])){
            int open=opens[kind];
            if(open+length<=column&&column<=i){
                if(kind<2)pasteInsideBold=true;else if(kind<4)pasteInsideItalic=true;else if(kind==4)pasteInsideStrike=true;else pasteInsideHighlight=true;
                if(column==open+length){pasteAtOpenEdge=true;pasteEdgeMarker=c;}
                if(column==i){pasteAtCloseEdge=true;pasteEdgeMarker=c;}
            }
            opens[kind]=-1;
        }
        i+=length;
    }
}
// MarkdownLivePreview.Find: the token at or after start, with "\x" walked as one unit.
static int PasteFind(string text,string token,int start,ref int budget){
    if(token.Length==0)return -1;
    for(int p=start;p+token.Length<=text.Length;){
        if(--budget<=0)return -1;
        if(text[p]=='\\'){p+=2;continue;}
        if(String.CompareOrdinal(text,p,token,0,token.Length)==0)return p;
        p++;
    }
    return -1;
}
// MarkdownLivePreview.LinkLabelEnd: the "]" closing the label at open, counted by bracket depth
// with escapes as one unit (a nested "[" still counts; a nested image balances its own brackets).
static int PasteLabelEnd(string text,int open,ref int budget){
    int depth=1;int p=open+1;
    while(p<text.Length&&budget>0){
        budget--;
        char c=text[p];
        if(c=='\\'){p+=Math.Min(2,text.Length-p);continue;}
        if(c=='['){depth++;p++;continue;}
        if(c==']'){depth--;if(depth==0)return p;}
        p++;
    }
    return -1;
}
// MarkdownLivePreview.ImageEnd: the ")" ending a target, skipping escapes, a quoted title and a
// nested "(...)" pair.
static int PasteParenClose(string text,int start,ref int budget){
    int depth=0;bool angle=false;char quote='\0';
    for(int p=start;p<text.Length&&budget>0;){
        budget--;
        char c=text[p];
        if(c=='\\'){p+=2;continue;}
        if(quote!='\0'){if(c==quote)quote='\0';p++;continue;}
        if(c=='<')angle=true;
        else if(c=='>')angle=false;
        else if(!angle&&(c=='"'||c=='\'')&&p>start&&char.IsWhiteSpace(text[p-1]))quote=c;
        else if(!angle&&c=='(')depth++;
        else if(!angle&&c==')'){if(depth==0)return p;depth--;}
        p++;
    }
    return -1;
}
// MarkdownLivePreview.MatchesTag: "<span " or "<font " (the tag name then a space or tab).
static bool PasteTagAt(string text,int at,string name){
    if(at<0||at+name.Length+1>=text.Length)return false;
    for(int k=0;k<name.Length;k++)if(char.ToLowerInvariant(text[at+1+k])!=name[k])return false;
    char next=text[at+1+name.Length];
    return next==' '||next=='\t';
}
static bool PasteToken(string text,int at,string token){if(at<0||at+token.Length>text.Length)return false;for(int k=0;k<token.Length;k++)if(text[at+k]!=token[k])return false;return true;}
static int PasteCodeClose(string text,int start,int fence,ref int budget){
    for(int p=start;p<text.Length;){
        if(--budget<=0)return -1;
        if(text[p]!='`'){p++;continue;}
        int run=1;while(p+run<text.Length&&text[p+run]=='`')run++;
        if(run==fence)return p;
        p+=run;
    }
    return -1;
}
void PasteCapturedNow(PasteSnapshot snapshot,bool plain){
    if(!editing||documentOperationBusy)return;
    bool hadContext=pasteEmphasisContext,hadBold=pasteInsideBold,hadItalic=pasteInsideItalic,hadStrike=pasteInsideStrike,hadHighlight=pasteInsideHighlight,hadOpen=pasteAtOpenEdge,hadClose=pasteAtCloseEdge;
    char hadMarker=pasteEdgeMarker;
    if(!plain&&!hadContext&&(snapshot.HasHtml||snapshot.HasRtf))ArmPasteEmphasis(source);
    try{
        if(LivePaste(CapturedPasteData(snapshot,plain),plain,snapshot.Value))return;
    }finally{
        pasteEmphasisContext=hadContext;pasteInsideBold=hadBold;pasteInsideItalic=hadItalic;pasteInsideStrike=hadStrike;pasteInsideHighlight=hadHighlight;pasteAtOpenEdge=hadOpen;pasteAtCloseEdge=hadClose;pasteEdgeMarker=hadMarker;
    }
    /* This is the TXT (plain) path: LivePaste refuses a plain document, so the clipboard string is
       inserted here as the whole paste. It lands on the document's own newline like every LivePaste
       path (#18): a Ctrl+V of a CRLF copy used to write CRLF into an LF .txt buffer and mix endings. */
    if(snapshot.Value!=null){string value=NormalizeLineEndings(snapshot.Value,documentNewline);int at=source.SelectionStart;source.SelectedText=value;source.Select(at+value.Length,0);}
}
static bool NeutralPasteColor(System.Windows.Media.Color color){return Math.Max(color.R,Math.Max(color.G,color.B))-Math.Min(color.R,Math.Min(color.G,color.B))<=12||color==System.Windows.Media.Color.FromRgb(38,50,66);}
static void PasteRuns(InlineCollection inlines,System.Collections.Generic.List<Run> result){foreach(Inline inline in inlines){if(inline is Run)result.Add((Run)inline);else if(inline is Span&&!(inline is Hyperlink))PasteRuns(((Span)inline).Inlines,result);}}
void NormalizePasteDocument(FlowDocument imported){var runs=new System.Collections.Generic.List<Run>();foreach(var paragraph in EnumerateParagraphs(imported.Blocks))PasteRuns(paragraph.Inlines,runs);var originalSizes=new System.Collections.Generic.Dictionary<Run,double>();var originalColors=new System.Collections.Generic.Dictionary<Run,System.Windows.Media.Brush>();foreach(var run in runs){originalSizes[run]=run.FontSize;originalColors[run]=run.Foreground;}var counts=new System.Collections.Generic.Dictionary<System.Windows.Media.Color,int>();var sizes=new System.Collections.Generic.Dictionary<double,int>();foreach(var run in runs){var brush=run.Foreground as System.Windows.Media.SolidColorBrush;if(brush!=null&&NeutralPasteColor(brush.Color)){if(!counts.ContainsKey(brush.Color))counts[brush.Color]=0;counts[brush.Color]+=run.Text.Length;}if(!sizes.ContainsKey(run.FontSize))sizes[run.FontSize]=0;sizes[run.FontSize]+=run.Text.Length;}System.Windows.Media.Color? baseColor=null;int most=0;foreach(var item in counts)if(item.Value>most){baseColor=item.Key;most=item.Value;}double baseline=fontSize;most=0;foreach(var item in sizes)if(item.Value>most){baseline=item.Key;most=item.Value;}foreach(var paragraph in EnumerateParagraphs(imported.Blocks)){paragraph.FontSize=paragraph.FontSize/baseline*fontSize;paragraph.Foreground=Foreground;paragraph.FontFamily=new System.Windows.Media.FontFamily(family);}foreach(var run in runs){var brush=originalColors[run] as System.Windows.Media.SolidColorBrush;if(brush!=null&&baseColor.HasValue&&brush.Color==baseColor.Value)run.Foreground=Foreground;else run.Foreground=originalColors[run];run.FontSize=originalSizes[run]/baseline*fontSize;run.FontFamily=new System.Windows.Media.FontFamily(family);}imported.FontSize=fontSize;imported.Foreground=Foreground;}


}}
