using System;using System.IO;using System.Security.Cryptography;using System.Threading;using System.Text;
namespace MDNote {
internal class SaveConflictException:IOException {internal SaveConflictException():base(MainWindow.TranslatedText(MainWindow.SavedLanguage(),"파일을 연 뒤 다른 창이나 프로그램에서 내용이 변경되었습니다. 다른 이름으로 저장하여 두 버전을 보존해 주세요.")) {}internal SaveConflictException(string message):base(message) {}}
/* A Save As onto a file another tab or window already holds would leave two editors on one path
   (UI bug hunt 4 item 2). It derives from SaveConflictException so a caller that already treats a
   refused same-file write as a save conflict keeps working; SaveAsync answers it with its own
   "already open" message instead of the "save as another name?" prompt. */
internal sealed class SaveTargetInUseException:SaveConflictException {internal SaveTargetInUseException():base(MainWindow.TranslatedText(MainWindow.SavedLanguage(),"이 파일은 다른 탭이나 창에서 이미 열려 있습니다. 그 탭이나 창에서 저장해 주세요.")) {}}
internal static class FileVersion {
internal static string Of(byte[] bytes){using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(bytes));}
internal static string Read(string path){if(!File.Exists(path))return "missing";using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(stream));}
}
internal sealed class SaveLease:IDisposable {readonly Mutex gate;bool owned;internal SaveLease(string path){string id=FileVersion.Of(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())).Replace('/','_').Replace('+','-');gate=new Mutex(false,"Local\\MDNote-save-"+id);try{owned=gate.WaitOne(5000);}catch(AbandonedMutexException){owned=true;}if(!owned){gate.Dispose();throw new IOException(MainWindow.TranslatedText(MainWindow.SavedLanguage(),"다른 창에서 이 파일을 저장하고 있습니다. 잠시 후 다시 저장해 주세요."));}}public void Dispose(){if(owned){owned=false;gate.ReleaseMutex();}gate.Dispose();}}
public partial class MainWindow {
string documentFingerprint,documentFingerprintPath;
/* one prompt per save (UI bug hunt 4 item 3): the conflict retry the user approved carries that
   path as its own parameter, and a write to it replaces the version the prompt named. Nothing is
   stored on the window, so a save started meanwhile still checks the file it is about to replace. */
void CheckSaveConflict(string target,string approvedPath){
    string full=System.IO.Path.GetFullPath(target);
    if(approvedPath!=null&&String.Equals(full,approvedPath,StringComparison.OrdinalIgnoreCase))return;
    if(documentFingerprint!=null&&documentFingerprintPath!=null&&String.Equals(full,documentFingerprintPath,StringComparison.OrdinalIgnoreCase)&&FileVersion.Read(target)!=documentFingerprint)throw new SaveConflictException();
}
}
}
