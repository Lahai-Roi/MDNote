using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
namespace MDNote {
public static partial class Markdown {
    static readonly HashSet<string> codeLanguages=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"javascript","js","typescript","ts","python","py","csharp","cs","c","cpp","java","json","css","go","rust","rs","sql","bash","sh","powershell","ps1","html","xml"};
    static readonly HashSet<string> codeKeywords=new HashSet<string>("if else elif for foreach while do switch case break continue return function def class public private protected static void new const let var using import from as try catch except finally throw raise async await yield true false null None True False int string bool double float char namespace interface enum struct package func fn impl use pub mut match select insert update delete where and or not in is with pass lambda print export default this self extends implements typeof instanceof".Split(' '));
    static readonly Regex codeTokens=new Regex(@"(?<comment>//[^\r\n]*|/\*[\s\S]*?\*/|\#[^\r\n]*)|(?<str>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`(?:\\.|[^`\\])*`)|(?<num>\b\d+(?:\.\d+)?\b)|(?<word>\b[A-Za-z_][A-Za-z_0-9]*\b)",RegexOptions.Compiled);
    static void DecorateCode(Paragraph paragraph,string language,bool dark){
        var menu=new ContextMenu();var copy=new MenuItem {Header="코드 복사"};copy.Click+=delegate{ClipboardRetry.TrySetText(new TextRange(paragraph.ContentStart,paragraph.ContentEnd).Text.TrimEnd('\r','\n'));};menu.Items.Add(copy);menu.Opened+=delegate{var owner=Window.GetWindow(paragraph) as MainWindow;if(owner!=null)copy.Header=owner.TranslateLabel("코드 복사");};paragraph.ContextMenu=menu;
        if(!codeLanguages.Contains(language.Split(' ')[0]))return;string code=new TextRange(paragraph.ContentStart,paragraph.ContentEnd).Text;paragraph.Inlines.Clear();int at=0;
        foreach(Match match in codeTokens.Matches(code)){if(match.Index>at)paragraph.Inlines.Add(new Run(code.Substring(at,match.Index-at)));string color=null;if(match.Groups["comment"].Success)color=dark?"#91A18C":"#5A7150";else if(match.Groups["str"].Success)color=dark?"#DAB48B":"#92512D";else if(match.Groups["num"].Success)color=dark?"#A6C7B7":"#246D58";else if(codeKeywords.Contains(match.Value))color=dark?"#BAA8E0":"#7541A0";var run=new Run(match.Value);if(color!=null)run.Foreground=Ui.Brush(color);paragraph.Inlines.Add(run);at=match.Index+match.Length;}if(at<code.Length)paragraph.Inlines.Add(new Run(code.Substring(at)));
    }
}
}
