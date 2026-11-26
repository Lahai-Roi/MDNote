using System;using System.IO;
namespace MDNote {
/* Issue #6 (11-26): where settings.xml, Recovery\ and crash-log.txt live. The installer and the existing
   portable zip keep %LocalAppData%\MDNote; a third zip ships portable-data.ini next to the exe and keeps
   everything in <exe folder>\data instead, so the whole folder moves (and is removed) as one. A folder that
   cannot be written (Program Files, a read-only stick) falls back to %LocalAppData%. Resolved once. */
internal static class AppDataPaths {
    internal const string MarkerName="portable-data.ini";
    static string root;static readonly object gate=new object();
    internal static string LocalRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MDNote");}}
    internal static string Root {get{lock(gate){if(root==null)root=Resolve();return root;}}}
    internal static bool Portable {get{return !String.Equals(Root,LocalRoot,StringComparison.OrdinalIgnoreCase);}}
    static string Resolve(){
        try{
            string exeDir=AppDomain.CurrentDomain.BaseDirectory;
            if(String.IsNullOrEmpty(exeDir)||!File.Exists(Path.Combine(exeDir,MarkerName)))return LocalRoot;
            string data=Path.Combine(exeDir,"data");
            Directory.CreateDirectory(data);
            /* Creating the folder can succeed where writing a file does not; prove a file write. */
            using(new FileStream(Path.Combine(data,"write-test-"+Guid.NewGuid().ToString("N")+".tmp"),FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose)){}
            CopyOldSettings(data);
            return data;
        }catch(Exception){return LocalRoot;}
    }
    /* First run of the folder build on a PC that used the installer or the old zip: start from that
       settings file instead of defaults. A copy, so the %LocalAppData% original stays as it was. */
    static void CopyOldSettings(string data){
        string temp=null;
        try{string target=Path.Combine(data,"settings.xml");string source=Path.Combine(LocalRoot,"settings.xml");
            if(File.Exists(target)||!File.Exists(source))return;
            /* Copy under a private name and move it into place, so a second instance starting at the same
               moment never reads a half-written settings.xml (#6 review). */
            temp=target+"."+Guid.NewGuid().ToString("N")+".tmp";
            File.Copy(source,temp,false);File.Move(temp,target);temp=null;}
        catch(Exception){/* defaults are a safe start; never block start-up on the copy */}
        finally{if(temp!=null)try{File.Delete(temp);}catch(Exception){}}
    }
}
}
