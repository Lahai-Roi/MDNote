using System;using System.IO;using System.Text;using System.Collections.Generic;using System.Threading;using System.Threading.Tasks;using ICSharpCode.AvalonEdit.Document;
namespace MDNote {
internal sealed class RecoveryRecord {internal string File,Name,OriginalPath;internal bool PlainText;internal int CodePage,Characters;internal bool Bom;internal string Newline;internal DateTime Updated;internal string Text;/* format 1 has neither: a session draft (이전 세션 계속하기) keeps the caret and the file version the document had when it closed, so the restored tab reopens at the same place and a save still meets an external edit instead of overwriting it in silence. */internal int Caret;internal string Fingerprint;/* v3 session drafts: Clean is a tab that had no unsaved text (only its path is stored), Active the tab that was on screen. Group/Order/Index are decoded from the draft id and never written; LoadedText and the Load* fields carry one record's text from the background read to the UI apply. */internal bool Clean,Active;internal string SessionGroup;internal int SessionOrder,SessionIndex;internal string LoadedText;internal Encoding LoadEncoding;internal string LoadFingerprint;internal byte[] LoadBytes;internal bool LoadFailed;}
internal static class RecoveryStore {
const int Magic=0x39444D52;static readonly object gate=new object();static readonly Dictionary<string,int> versions=new Dictionary<string,int>();static readonly SemaphoreSlim writer=new SemaphoreSlim(1,1);static readonly Dictionary<string,CancellationTokenSource> pendingWriters=new Dictionary<string,CancellationTokenSource>();static readonly string owner=Guid.NewGuid().ToString("N");static FileStream ownerLock;static string lockRoot;internal static string TestRoot=null;internal static Exception LastError;
internal static bool Enabled {get{return !Program.IsSelfTest||TestRoot!=null;}}
internal static SemaphoreSlim TestWriter {get{if(!Program.IsSelfTest)throw new InvalidOperationException();return writer;}}
internal static string Root {get{return TestRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MDNote","Recovery");}}
static string Target(string id){return Path.Combine(Root,owner+"-"+id+".draft");}
static void OwnerLock(string root){lock(gate){if(ownerLock!=null&&lockRoot==root)return;if(ownerLock!=null)ownerLock.Dispose();Directory.CreateDirectory(root);ownerLock=new FileStream(Path.Combine(root,owner+".lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);lockRoot=root;}}
static void Put(BinaryWriter output,string value){byte[] bytes=Encoding.UTF8.GetBytes(value??"");if(bytes.Length>128*1024)throw new InvalidDataException("Recovery metadata is too long");output.Write(bytes.Length);output.Write(bytes);}
static string Get(BinaryReader input){int count=input.ReadInt32();if(count<0||count>128*1024)throw new InvalidDataException("Invalid recovery metadata");byte[] bytes=input.ReadBytes(count);if(bytes.Length!=count)throw new EndOfStreamException();return new UTF8Encoding(false,true).GetString(bytes);}
static void CancelPending(CancellationTokenSource pending){if(pending!=null)try{pending.Cancel();}catch(ObjectDisposedException){}}
internal static Task WriteAsync(string id,RecoveryRecord record,ITextSource snapshot,string text){
    if(!Enabled)return Task.FromResult(0);int revision;CancellationTokenSource previous;var pending=new CancellationTokenSource();
    lock(gate){int old;versions.TryGetValue(id,out old);versions[id]=revision=old+1;pendingWriters.TryGetValue(id,out previous);pendingWriters[id]=pending;}
    CancelPending(previous);string root=Root,target=Target(id);return SaveAsync(id,revision,root,target,record,snapshot,text,pending);
}
// Session end ends the process before a queued async write could run, so the draft is written on the
// calling (UI) thread. The revision bump makes any older in-flight writer skip its replace.
internal static bool WriteSync(string id,RecoveryRecord record,ITextSource snapshot,string text){
    if(!Enabled)return false;int revision;lock(gate){int old;versions.TryGetValue(id,out old);versions[id]=revision=old+1;}
    string root=Root,target=Target(id),temp=null;
    try{
        OwnerLock(root);
        temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        int count=snapshot!=null?snapshot.TextLength:text.Length;
        using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var output=new BinaryWriter(stream,Encoding.UTF8)){
            output.Write(Magic);output.Write(3);Put(output,record.Name);Put(output,record.OriginalPath);output.Write(record.PlainText);output.Write(record.CodePage);output.Write(record.Bom);Put(output,record.Newline);output.Write(record.Caret);Put(output,record.Fingerprint);output.Write((record.Clean?1:0)|(record.Active?2:0));output.Write(DateTime.UtcNow.Ticks);output.Write(count);
            using(var reader=snapshot!=null?snapshot.CreateReader():new StringReader(text)){
                char[] chars=new char[16384];byte[] bytes=new byte[chars.Length*2];int read;
                while((read=reader.Read(chars,0,chars.Length))>0){Buffer.BlockCopy(chars,0,bytes,0,read*2);output.Write(bytes,0,read*2);}
            }
            output.Flush();stream.Flush(true);
        }
        lock(gate){if(versions[id]!=revision)return false;if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);temp=null;}
        LastError=null;return true;
    }catch(Exception error){LastError=error;}finally{if(temp!=null)try{File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
    return false;
}
static async Task SaveAsync(string id,int revision,string root,string target,RecoveryRecord record,ITextSource snapshot,string text,CancellationTokenSource pending){bool acquired=false;try{await writer.WaitAsync(pending.Token).ConfigureAwait(false);acquired=true;await Task.Run(delegate{string temp=null;try{lock(gate){if(versions[id]!=revision)return;}OwnerLock(root);temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";int count=snapshot!=null?snapshot.TextLength:text.Length;using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var output=new BinaryWriter(stream,Encoding.UTF8)){output.Write(Magic);output.Write(3);Put(output,record.Name);Put(output,record.OriginalPath);output.Write(record.PlainText);output.Write(record.CodePage);output.Write(record.Bom);Put(output,record.Newline);output.Write(record.Caret);Put(output,record.Fingerprint);output.Write((record.Clean?1:0)|(record.Active?2:0));output.Write(DateTime.UtcNow.Ticks);output.Write(count);using(var reader=snapshot!=null?snapshot.CreateReader():new StringReader(text)){char[] chars=new char[16384];byte[] bytes=new byte[chars.Length*2];int read;while((read=reader.Read(chars,0,chars.Length))>0){lock(gate){if(versions[id]!=revision)return;}Buffer.BlockCopy(chars,0,bytes,0,read*2);output.Write(bytes,0,read*2);}}output.Flush();stream.Flush(true);}lock(gate){if(versions[id]!=revision)return;if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);temp=null;}LastError=null;}catch(Exception error){LastError=error;}finally{if(temp!=null)try{File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}}).ConfigureAwait(false);}catch(OperationCanceledException){}finally{if(acquired)writer.Release();lock(gate){CancellationTokenSource current;if(pendingWriters.TryGetValue(id,out current)&&Object.ReferenceEquals(current,pending))pendingWriters.Remove(id);}pending.Dispose();}}
internal static void Remove(string id){if(!Enabled)return;string target=Target(id);CancellationTokenSource pending;lock(gate){int old;versions.TryGetValue(id,out old);versions[id]=old+1;pendingWriters.TryGetValue(id,out pending);try{File.Delete(target);}catch(IOException){}catch(UnauthorizedAccessException){}}CancelPending(pending);}
static bool OwnerIsLive(string file){string name=Path.GetFileName(file);if(name.Length<33)return true;string ownerFile=Path.Combine(Path.GetDirectoryName(file),name.Substring(0,32)+".lock");if(!File.Exists(ownerFile))return false;try{using(var stream=new FileStream(ownerFile,FileMode.Open,FileAccess.ReadWrite,FileShare.None))return false;}catch(IOException){return true;}catch(UnauthorizedAccessException){return true;}}
static RecoveryRecord Header(BinaryReader input,string path){if(input.ReadInt32()!=Magic)throw new InvalidDataException("Unknown recovery format");int version=input.ReadInt32();if(version!=1&&version!=2&&version!=3)throw new InvalidDataException("Unknown recovery format");var record=new RecoveryRecord {File=path,Name=Get(input),OriginalPath=Get(input),PlainText=input.ReadBoolean(),CodePage=input.ReadInt32(),Bom=input.ReadBoolean(),Newline=Get(input)};/* format 1 (every build before 이전 세션 계속하기) has no caret or fingerprint: those drafts are exactly the crash drafts, which never carry a caret. */if(version>=2){record.Caret=input.ReadInt32();record.Fingerprint=Get(input);}/* v3 adds the session flags: whether the tab had unsaved text and whether it was the one on screen. */if(version>=3){int flags=input.ReadInt32();record.Clean=(flags&1)!=0;record.Active=(flags&2)!=0;}long ticks=input.ReadInt64();record.Updated=new DateTime(ticks,DateTimeKind.Utc);record.Characters=input.ReadInt32();if(record.Characters<0||record.Characters>256*1024*1024||input.BaseStream.Length-input.BaseStream.Position!=record.Characters*2L)throw new InvalidDataException("Incomplete recovery document");return record;}
static string[] DraftFiles(string pattern){try{return Directory.GetFiles(Root,pattern);}catch(IOException error){LastError=error;}catch(UnauthorizedAccessException error){LastError=error;}return new string[0];}
internal static List<RecoveryRecord> List(){var result=new List<RecoveryRecord>();if(!Enabled||!Directory.Exists(Root))return result;foreach(string path in DraftFiles("*.draft")){try{ValidatePath(path);/* A session draft is offered through 이전 세션 계속하기, never by the crash prompt: restoring it there as well would restore the same text twice. */if(IsSessionId(RecordId(path)))continue;if(OwnerIsLive(path))continue;using(var stream=File.OpenRead(path))using(var input=new BinaryReader(stream,Encoding.UTF8))result.Add(Header(input,path));}catch(InvalidDataException){}catch(IOException){}catch(ArgumentException){}catch(UnauthorizedAccessException){}}result.Sort(delegate(RecoveryRecord a,RecoveryRecord b){return b.Updated.CompareTo(a.Updated);});var seen=new HashSet<string>();result.RemoveAll(delegate(RecoveryRecord record){return !seen.Add(RecordId(record.File));});return result;}
// A draft the user postpones ("나중에") is offered again at every start until the user restores it
// or deletes it in the recovery window; unsaved text is never removed on a timer. Only a draft
// that is provably empty can be expired by age: no characters and no original file, i.e. a new
// document in which nothing was ever typed. An empty draft of an existing file is kept too - it
// may be the user's unsaved "cleared the document" state. A draft whose owner editor is still
// running is never touched, whatever its age.
internal static int PurgeOlderThan(TimeSpan age){if(age<=TimeSpan.Zero||!Enabled||!Directory.Exists(Root))return 0;DateTime cutoff=DateTime.UtcNow-age;int removed=0;foreach(string path in DraftFiles("*.draft")){try{ValidatePath(path);if(IsSessionId(RecordId(path)))continue;if(OwnerIsLive(path))continue;if(File.GetLastWriteTimeUtc(path)>=cutoff)continue;bool empty;using(var stream=File.OpenRead(path))using(var input=new BinaryReader(stream,Encoding.UTF8)){var header=Header(input,path);empty=header.Characters==0&&String.IsNullOrEmpty(header.OriginalPath);}if(!empty)continue;File.Delete(path);if(!File.Exists(path))removed++;}catch(InvalidDataException){}catch(IOException){}catch(ArgumentException){}catch(UnauthorizedAccessException){}}return removed;}
static void ValidatePath(string file){string root=Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;string full=Path.GetFullPath(file);if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(full),@"^[a-f0-9]{32}-[a-f0-9]{32}\.draft$"))throw new InvalidDataException("Invalid recovery path");}
internal static RecoveryRecord Read(string file){ValidatePath(file);using(var stream=File.OpenRead(file))using(var input=new BinaryReader(stream,Encoding.UTF8)){var record=Header(input,file);char[] chars=new char[record.Characters];byte[] buffer=new byte[32768];int offset=0;while(offset<chars.Length){int wanted=Math.Min(buffer.Length,(chars.Length-offset)*2),read=0;while(read<wanted){int got=input.Read(buffer,read,wanted-read);if(got==0)throw new EndOfStreamException();read+=got;}Buffer.BlockCopy(buffer,0,chars,offset*2,read);offset+=read/2;}record.Text=new string(chars);return record;}}
internal static string RecordId(string file){string name=Path.GetFileNameWithoutExtension(file);if(name.Length!=65)throw new InvalidDataException("Invalid recovery ID");return name.Substring(33);}
/* 이전 세션 계속하기 (user, 2026-11-03): the drafts written when a window closes, so the next
   start can reopen its tabs instead of asking. Every window owns one group, and the group, the
   window's creation order and the tab's position are carried by the draft id:
     prefix(6) + group(12 hex) + order(6 hex) + tab index(8 hex).
   The fixed prefix keeps these ids apart from the crash drafts, so the crash prompt and its purge
   never touch them. A draft written by 2.1 (prefix + zeros + index) decodes as one group with
   order 0, which is exactly how that build left it. */
const string sessionPrefix="5e5510";
const int sessionGroupChars=12,sessionOrderChars=6,sessionIndexChars=8;
internal static string SessionId(string group,int order,int index){return sessionPrefix+(group??new string('0',sessionGroupChars))+order.ToString("x6",System.Globalization.CultureInfo.InvariantCulture).PadLeft(sessionOrderChars,'0')+index.ToString("x8",System.Globalization.CultureInfo.InvariantCulture).PadLeft(sessionIndexChars,'0');}
internal static bool IsSessionId(string id){if(id==null||id.Length!=32||!id.StartsWith(sessionPrefix,StringComparison.Ordinal))return false;for(int i=sessionPrefix.Length;i<id.Length;i++){char c=id[i];if(!(c>='0'&&c<='9')&&!(c>='a'&&c<='f'))return false;}return true;}
/* The group, window order and tab index an id carries; false for a crash-draft id. */
static bool SessionParts(string id,out string group,out int order,out int index){group=null;order=0;index=0;if(!IsSessionId(id))return false;group=id.Substring(sessionPrefix.Length,sessionGroupChars);if(!Int32.TryParse(id.Substring(sessionPrefix.Length+sessionGroupChars,sessionOrderChars),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out order))return false;if(!Int32.TryParse(id.Substring(sessionPrefix.Length+sessionGroupChars+sessionOrderChars,sessionIndexChars),System.Globalization.NumberStyles.HexNumber,System.Globalization.CultureInfo.InvariantCulture,out index))return false;return true;}
/* The session drafts every owner left behind. Only a draft whose editor is gone is read; the
   caller deletes each one after it has restored the document. */
internal static List<RecoveryRecord> ReadSessions(){
    var result=new List<RecoveryRecord>();if(!Enabled||!Directory.Exists(Root))return result;
    foreach(string path in DraftFiles("*.draft")){
        try{ValidatePath(path);string id=RecordId(path);if(!IsSessionId(id))continue;/* A draft another editor still holds is that editor's own session and is left alone. */if(OwnerIsLive(path))continue;using(var stream=File.OpenRead(path))using(var input=new BinaryReader(stream,Encoding.UTF8)){var record=Header(input,path);string group;int order,index;if(SessionParts(id,out group,out order,out index)){record.SessionGroup=group;record.SessionOrder=order;record.SessionIndex=index;}result.Add(record);}}catch(InvalidDataException){}catch(IOException){}catch(ArgumentException){}catch(UnauthorizedAccessException){}
    }
    result.Sort(delegate(RecoveryRecord a,RecoveryRecord b){return String.CompareOrdinal(RecordId(a.File),RecordId(b.File));});
    return result;
}
/* Every session draft no other running editor still owns. Each window writes and restores its own
   group now, so a close never calls this; it stays as the wholesale cleanup for a caller that has
   to drop everything. */
internal static void ClearSessions(){
    if(!Enabled||!Directory.Exists(Root))return;
    foreach(string path in DraftFiles("*.draft")){
        try{ValidatePath(path);if(!IsSessionId(RecordId(path)))continue;if(OwnerIsOtherLive(path))continue;File.Delete(path);}catch(InvalidDataException){}catch(IOException){}catch(ArgumentException){}catch(UnauthorizedAccessException){}
    }
}
static bool OwnerIsOtherLive(string file){string name=Path.GetFileName(file);if(name.StartsWith(owner+"-",StringComparison.Ordinal))return false;return OwnerIsLive(file);}
internal static bool WriteSession(string group,int order,int index,RecoveryRecord record,ITextSource snapshot,string text){return WriteSync(SessionId(group,order,index),record,snapshot,text);}
/* One session draft, once it is restored. It is deleted by path without the live-owner test Discard
   makes: the editor that wrote it is gone by definition, and the restoring process must be able to
   remove it even when its own lock file happens to sit under the same name. */
internal static void DiscardSession(string file){try{ValidatePath(file);File.Delete(file);}catch(InvalidDataException){}catch(IOException){}catch(UnauthorizedAccessException){}}
/* The drafts of one window group this process wrote. A capture drops its group first, and a
   cancelled shutdown drops only the group it flushed - a window closed earlier in the same
   process keeps its own group. Only this owner's files are touched. */
internal static void DropOwnGroup(string group){
    if(!Enabled||String.IsNullOrEmpty(group)||!Directory.Exists(Root))return;
    foreach(string path in DraftFiles(owner+"-*.draft")){
        try{ValidatePath(path);string draftGroup;int order,index;if(!SessionParts(RecordId(path),out draftGroup,out order,out index)||!String.Equals(draftGroup,group,StringComparison.Ordinal))continue;File.Delete(path);}catch(InvalidDataException){}catch(IOException){}catch(ArgumentException){}catch(UnauthorizedAccessException){}
    }
}
/* The drafts this process wrote before a shutdown that was cancelled: the close never happened, so
   they must not be restored as if it had. Only this owner's files are touched. */
internal static void DropOwnSessions(){
    if(!Enabled||!Directory.Exists(Root))return;
    foreach(string path in DraftFiles(owner+"-*.draft")){
        try{ValidatePath(path);if(!IsSessionId(RecordId(path)))continue;File.Delete(path);}catch(InvalidDataException){}catch(IOException){}catch(ArgumentException){}catch(UnauthorizedAccessException){}
    }
}
internal static void DiscardGroup(string id){if(!Enabled||!Directory.Exists(Root)||!System.Text.RegularExpressions.Regex.IsMatch(id,@"^[a-f0-9]{32}$"))return;foreach(string file in DraftFiles("*-"+id+".draft")){try{ValidatePath(file);if(!OwnerIsLive(file))File.Delete(file);}catch(InvalidDataException){}catch(IOException){}catch(UnauthorizedAccessException){}}}
internal static void Discard(string file){ValidatePath(file);if(OwnerIsLive(file))throw new IOException("This document belongs to a running editor");File.Delete(file);}
internal static void ReleaseTestOwner(){if(!Program.IsSelfTest)throw new InvalidOperationException();lock(gate){if(ownerLock!=null)ownerLock.Dispose();ownerLock=null;lockRoot=null;}}
}
}
