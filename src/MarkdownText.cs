using System;using System.Text;using ICSharpCode.AvalonEdit.Document;
namespace MDNote {public static partial class Markdown {
// Immutable normalized text. Initial parsing can borrow a string; an updated
// LF source can borrow its rope snapshot without joining the entire document.
sealed class MarkdownText {
    readonly string value;readonly ITextSource snapshot;
    internal MarkdownText(string text){if(text==null)throw new ArgumentNullException("text");value=text;}
    internal MarkdownText(ITextSource text){if(text==null)throw new ArgumentNullException("text");snapshot=text.CreateSnapshot();}
    internal int Length {get{return value!=null?value.Length:snapshot.TextLength;}}
    internal char this[int offset] {get{return value!=null?value[offset]:snapshot.GetCharAt(offset);}}
    internal string Substring(int offset,int length){return value!=null?value.Substring(offset,length):snapshot.GetText(offset,length);}
    internal int IndexOf(char c,int start){return value!=null?value.IndexOf(c,start):snapshot.IndexOf(c,start,Length-start);}
    internal bool EqualsRange(int start,string other,int otherStart,int length){
        if(start<0||otherStart<0||length<0||start>Length-length||otherStart>other.Length-length)return false;
        if(value!=null)return String.CompareOrdinal(value,start,other,otherStart,length)==0;
        for(int at=0;at<length;at+=1024){int count=Math.Min(1024,length-at);string part=snapshot.GetText(start+at,count);if(String.CompareOrdinal(part,0,other,otherStart+at,count)!=0)return false;}return true;
    }
    internal void AppendTo(StringBuilder result,int start,int length){if(value!=null)result.Append(value,start,length);else result.Append(snapshot.GetText(start,length));}
    internal ICSharpCode.AvalonEdit.Utils.Rope<char> CloneRope(){var rope=snapshot as RopeTextSource;return rope!=null?rope.GetRope():ICSharpCode.AvalonEdit.Utils.CharRope.Create(value??snapshot.Text);}
    public override string ToString(){return value??snapshot.Text;}
}
}}
