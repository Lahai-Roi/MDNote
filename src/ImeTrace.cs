using System;
using System.Globalization;
using System.IO;
using System.Text;
namespace MDNote {
// MDNOTE_IME_TRACE=<file>: live-view IME diagnostics for the syllable a menu focus change eats.
// The path is read once; with the variable unset every Write call is one field check.
internal static class ImeTrace {
    static readonly string tracePath=Environment.GetEnvironmentVariable("MDNOTE_IME_TRACE");
    static readonly object gate=new object();
    internal static bool Enabled {get{return tracePath!=null&&tracePath.Length>0;}}
    internal static void Write(string message){
        if(tracePath==null||tracePath.Length==0)return;
        lock(gate){
            try{File.AppendAllText(tracePath,DateTime.Now.ToString("HH:mm:ss.fff",CultureInfo.InvariantCulture)+" "+message+Environment.NewLine,new UTF8Encoding(false));}
            catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}catch(NotSupportedException){}
        }
    }
}
}
