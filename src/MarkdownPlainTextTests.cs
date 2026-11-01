using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
namespace MDNote {public partial class MainWindow {
    // --md2txt-probe <outDir>: compares MarkdownPlainText.Convert with the FlowDocument oracle
    // ReadableText(Markdown.Parse(...)). Generated text only; no external files are read.
    internal static int VerifyMarkdownPlainText(string outDir){
        string reportPath=null;var report=new StringBuilder();
        try{
            outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
            reportPath=Path.Combine(outDir,"md2txt-probe.txt");
            if(System.Windows.Application.Current==null)new System.Windows.Application {ShutdownMode=System.Windows.ShutdownMode.OnExplicitShutdown};
            report.Append("# md2txt-probe\n\n");
            int passed=0,failed=0;
            Action<string,string> check=delegate(string name,string markdown){
                string expected,actual;
                try{expected=ReadableText(Markdown.Parse(markdown,16,"Malgun Gothic",Brushes.Black,false,""));}
                catch(Exception ex){failed++;report.Append("FAIL ").Append(name).Append(" [oracle threw ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("]\n");return;}
                try{actual=MarkdownPlainText.Convert(markdown);}
                catch(Exception ex){failed++;report.Append("FAIL ").Append(name).Append(" [converter threw ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("]\n");return;}
                if(String.Equals(expected,actual,StringComparison.Ordinal)){passed++;report.Append("PASS ").Append(name).Append('\n');return;}
                failed++;int at=0;
                while(at<expected.Length&&at<actual.Length&&expected[at]==actual[at])at++;
                report.Append("FAIL ").Append(name).Append(" [first difference at ").Append(at).Append(", lengths ").Append(expected.Length).Append('/').Append(actual.Length).Append("]\n");
                report.Append("  oracle: ").Append(Md2TxtEscape(Md2TxtSnippet(expected,at))).Append('\n');
                report.Append("  actual: ").Append(Md2TxtEscape(Md2TxtSnippet(actual,at))).Append('\n');
            };
            // Headings.
            check("atx-h1","# 제목");
            check("atx-h6","###### 여섯");
            check("atx-trailing-hashes","### 제목 ###");
            check("atx-seven-hashes","####### seven");
            check("atx-no-space","#nospace");
            check("atx-tab","##\t탭 제목");
            check("atx-empty","## ");
            check("setext-h1","제목\n===");
            check("setext-h2","제목\n---");
            check("setext-guard-list","- 항목\n---");
            check("setext-vs-rule","본문\n---\n본문");
            check("heading-entity","# A &amp; B");
            // Emphasis and strike.
            check("bold","**굵게**");
            check("bold-underscore","__굵게__");
            check("italic","*기울임*");
            check("italic-underscore","_기울임_");
            check("strike","~~취소~~");
            check("snake-case","snake_case_name");
            check("star-in-word","foo*bar*baz");
            check("unclosed-bold","**닫히지 않음");
            check("unclosed-italic","a *b");
            check("triple-star","***둘 다***");
            check("four-stars","****");
            check("nested-bold-italic","**굵게 *기울임* 끝**");
            check("emphasis-entity","*&amp;*");
            check("bold-across-lines-footnote","[^1]: **강조\n    계속**");
            // Inline code.
            check("code","`코드`");
            check("code-double","`` a ` b ``");
            check("code-space","` a `");
            check("code-tick-only","`` ` ``");
            check("code-entity","`&amp;`");
            check("code-unclosed","`코드");
            // Links and images.
            check("link","[텍스트](https://example.com/a)");
            check("link-empty-url","[a]()");
            check("link-in-heading","## [링크](https://example.com)");
            check("image-relative","![대체](img.png)");
            check("image-empty-alt","![](img.png)");
            check("image-marked-alt","![*a* _b_](x.png)");
            // Inline HTML.
            check("span","<span style=\"color:red\">빨강</span>");
            check("span-nested","<span a><span b>x</span></span>");
            check("span-unclosed","<span a>닫히지 않음");
            check("font","<font color=red>글자</font>");
            check("br","위<br>아래");
            check("br-space","위<br />아래");
            check("entities","&amp; &lt; &gt; &quot; &#65; &nbsp;");
            check("unknown-entity","&bogus; &copy;");
            check("lone-angle","a < b");
            // Backslashes stay literal.
            check("escaped-emphasis","\\*x\\*");
            check("backslash","a\\b");
            // Lists.
            check("list-bullets","- a\n- b");
            check("list-markers","- a\n+ b\n* c");
            check("list-ordered","1. a\n2. b");
            check("list-ordered-restart","3. a\n7. b");
            check("list-paren","1) a\n2) b");
            check("list-nested","- a\n  - b\n    1. c\n- d");
            check("list-task","- [ ] 할 일\n- [x] 끝난 일");
            check("list-task-ordered","1. [X] 확인");
            check("list-tab","- a\n\t- b");
            check("list-continuation","- a\n계속 줄\n- b");
            check("list-indented-continuation","- a\n   계속 줄");
            check("list-after-blank","- a\n\n- b");
            check("list-empty-item","- \n- b");
            // Quotes.
            check("quote","> 인용");
            check("quote-nested",">> 중첩");
            check("quote-list","> - a\n> - b");
            check("quote-blank","> a\n>\n> b");
            check("quote-code","> ```\n> a\n> ```");
            // Tables.
            check("table-basic","| a | b |\n|---|---|\n| 1 | 2 |");
            check("table-pipe-code","| a | `x|y` | c |\n|---|---|---|\n| 1 | 2 | 3 |");
            check("table-escaped-pipe","| a\\|b | c |\n|---|---|\n| 1 | 2 |");
            check("table-br","| a<br>b | c |\n|---|---|\n| 1 | 2 |");
            check("table-no-edges","a | b\n---|---\n1 | 2");
            check("table-entity","| &amp; | b |\n|---|---|\n| 1 | 2 |");
            check("table-align","| a | b |\n|:---|---:|\n| 1 | 2 |");
            // Fenced code.
            check("fence-plain","```\na **b** &amp;\n```");
            check("fence-js","```js\nlet a = 1;\n```");
            check("fence-js-trailing","```js\na\n\n```");
            check("fence-text-trailing","```text\na\n\n```");
            check("fence-unclosed","```js\nlet a = 1;");
            check("fence-tilde","~~~\nx\n~~~");
            check("fence-bad-info","```a`b\ncode\n```");
            // Rules.
            check("hr","***");
            check("hr-spaced"," - - - ");
            check("hr-two","- -");
            // Blank lines and line endings.
            check("empty","");
            check("newlines","\n\n");
            check("spaces","   ");
            check("tab-blank","a\n\t\nb");
            check("crlf","a\r\nb\r\n\r\nc");
            check("cr-only","a\rb");
            check("trailing-blank","a\n\n\n");
            // Reference links and footnotes.
            check("reference-link","문장 [텍스트][a]\n\n[a]: https://example.com");
            check("reference-shortcut","[a]\n\n[a]: x");
            check("reference-undefined","[없음]");
            check("reference-empty-id","[텍스트][]\n\n[텍스트]: x");
            check("footnote","본문[^1]\n\n[^1]: 각주");
            check("footnote-multiline","[^1]: 첫 줄\n    둘째 줄");
            // Unicode.
            check("emoji","😀 이모지 *😀*");
            check("mixed-korean","한국어 문장입니다. **굵게** 그리고 `코드`.");
            var longLine=new StringBuilder();
            while(longLine.Length<20000)longLine.Append("가나다 ABC 123 ");
            check("generated-line-20000",longLine.ToString());
            // ~200 KB document, compared and timed once each.
            string large=Md2TxtLargeDocument();
            var watch=Stopwatch.StartNew();
            string largeExpected=ReadableText(Markdown.Parse(large,16,"Malgun Gothic",Brushes.Black,false,""));
            watch.Stop();long oracleMs=watch.ElapsedMilliseconds;
            watch=Stopwatch.StartNew();
            string largeActual=MarkdownPlainText.Convert(large);
            watch.Stop();long directMs=watch.ElapsedMilliseconds;
            if(String.Equals(largeExpected,largeActual,StringComparison.Ordinal)){passed++;report.Append("PASS large-document-200kb\n");}
            else{
                failed++;int at=0;
                while(at<largeExpected.Length&&at<largeActual.Length&&largeExpected[at]==largeActual[at])at++;
                report.Append("FAIL large-document-200kb [first difference at ").Append(at).Append(", lengths ").Append(largeExpected.Length).Append('/').Append(largeActual.Length).Append("]\n");
                report.Append("  oracle: ").Append(Md2TxtEscape(Md2TxtSnippet(largeExpected,at))).Append('\n');
                report.Append("  actual: ").Append(Md2TxtEscape(Md2TxtSnippet(largeActual,at))).Append('\n');
            }
            report.Append("200KB oracle-ms: ").Append(oracleMs).Append('\n');
            report.Append("200KB direct-ms: ").Append(directMs).Append('\n');
            report.Append("\ncases: ").Append(passed+failed).Append(", passed: ").Append(passed).Append(", failed: ").Append(failed).Append('\n');
            report.Append(failed==0?"Result: PASS\n":"Result: FAIL\n");
            File.WriteAllText(reportPath,report.ToString(),new UTF8Encoding(false));
            if(failed==0)File.WriteAllText(Path.Combine(outDir,"md2txt-probe-PASS.txt"),"PASS\n",new UTF8Encoding(false));
            return failed==0?0:1;
        }catch(Exception ex){
            try{if(!String.IsNullOrEmpty(reportPath))File.WriteAllText(reportPath,report.ToString()+"EXCEPTION\n"+ex+"\n",new UTF8Encoding(false));}catch{}
            return 2;
        }
    }
    static string Md2TxtSnippet(string value,int at){
        int start=at-40;if(start<0)start=0;
        int end=at+40;if(end>value.Length)end=value.Length;
        return value.Substring(start,end-start);
    }
    static string Md2TxtEscape(string value){
        return value.Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t");
    }
    static string Md2TxtLargeDocument(){
        var text=new StringBuilder(210000);
        int section=0;
        while(text.Length<200000){
            section++;
            text.Append("# 제목 ").Append(section).Append("\n\n");
            text.Append("본문 **굵게** *기울임* ~~취소~~ `코드` [링크](https://example.com/").Append(section).Append(") 그리고 &amp; 엔티티.\n\n");
            text.Append("- 첫 항목\n  - 둘째 항목\n    1. 셋째 항목\n\n");
            text.Append("> 인용문 **강조**\n> 둘째 줄\n\n");
            text.Append("| a | b | c |\n|---|---|---|\n| 1 | 2 | 3 |\n| x | y | z |\n\n");
            text.Append("```\n코드 ").Append(section).Append("\n```\n\n");
            text.Append("---\n\n");
            text.Append("1. 번호\n2. 번호\n\n");
            text.Append("- [ ] 할 일\n- [x] 끝난 일\n\n");
        }
        return text.ToString();
    }
}}
