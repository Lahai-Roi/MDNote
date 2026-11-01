using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;
namespace MDNote {
public static partial class Markdown {
    sealed class ReferenceContext {internal string Family;internal readonly Dictionary<string,string> Links=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);internal readonly Dictionary<string,int> Notes=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);}
    [ThreadStatic] static ReferenceContext referenceContext;
    /* imageLabel is the "[이미지: " prefix a missing image falls back to. A caller in a MainWindow passes
       T("[이미지: "); the default keeps the Korean text the ReadableText oracle writes. */
    public static FlowDocument Parse(string text,double size,string family,Brush foreground,bool dark,string file,string imageLabel=null){bool root=referenceContext==null;if(root){referenceContext=new ReferenceContext {Family=family};ScanReferences(text.Replace("\r\n","\n").Replace('\r','\n').Split('\n'),referenceContext,System.Threading.CancellationToken.None);}try{return ParseCore(text,size,family,foreground,dark,file,imageLabel);}finally{if(root)referenceContext=null;}}
    static void ScanReferences(IEnumerable<string> lines,ReferenceContext context,System.Threading.CancellationToken cancellation){char marker='\0';int length=0;foreach(string line in lines){cancellation.ThrowIfCancellationRequested();var fence=Regex.Match(line,@"^\s{0,3}(`{3,}|~{3,})(.*)$");if(marker!='\0'){if(fence.Success&&fence.Groups[1].Value[0]==marker&&fence.Groups[1].Value.Length>=length&&String.IsNullOrWhiteSpace(fence.Groups[2].Value))marker='\0';continue;}if(fence.Success&&!(fence.Groups[1].Value[0]=='`'&&fence.Groups[2].Value.Contains("`"))){marker=fence.Groups[1].Value[0];length=fence.Groups[1].Value.Length;continue;}string id,body;bool note;if(TryDefinition(line,out id,out body,out note)){if(note){if(!context.Notes.ContainsKey(id))context.Notes[id]=context.Notes.Count+1;}else context.Links[id]=body;}}}
    static bool TryDefinition(string line,out string id,out string body,out bool note){var match=Regex.Match(line,@"^\s{0,3}\[(\^?[^\]]+)\]:\s*(.*)$");id=match.Groups[1].Value;body=match.Groups[2].Value;note=id.StartsWith("^");if(!match.Success)return false;if(note)id=id.Substring(1);else{var address=Regex.Match(body,@"^(?:<([^>]+)>|(\S+))(?:\s+.*)?$");if(!address.Success)return false;body=address.Groups[1].Success?address.Groups[1].Value:address.Groups[2].Value;}return id.Length>0;}
    static int NoteNumber(string id){int number;return referenceContext!=null&&referenceContext.Notes.TryGetValue(id,out number)?number:1;}
    static string ExpandReferences(string value){if(referenceContext==null)return value;return Regex.Replace(value,@"`+[^`]*`+|!?\[([^\]]+)\]\[([^\]]*)\]|\[([^\]]+)\](?!\()",delegate(Match match){if(match.Value.StartsWith("`"))return match.Value;string label=match.Groups[1].Success?match.Groups[1].Value:match.Groups[3].Value;string id=match.Groups[1].Success?(match.Groups[2].Value==""?label:match.Groups[2].Value):label;if(id.StartsWith("^")&&referenceContext.Notes.ContainsKey(id.Substring(1)))return "["+NoteNumber(id.Substring(1))+"](#mdnote-footnote-"+Uri.EscapeDataString(id.Substring(1))+")";string address;return referenceContext.Links.TryGetValue(id,out address)?(match.Value.StartsWith("!")?"!":"")+"["+label+"]("+address+")":match.Value;});}
}
}
