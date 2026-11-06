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
            // An indented body line is the item's continuation: the list stays open, so the next deeper
            // marker nests under it (the print path, the live view and this converter read one rule).
            // The document with no heading marks and the one with them ("- ## ...") are both compared with the
            // oracle, and their output is pinned: an item's heading marks drop like a top-level heading's.
            string continuationPlain="본문 첫 줄입니다.\n\n- 목록 첫째 항목\n- 목록 안 2단계 제목\n  목록 안 본문 한 줄.\n  - 한 단계 들어간 목록 안 3단계 제목\n    더 들어간 목록 본문.\n    - 두 단계 들어간 4단계 제목\n      가장 깊은 목록 본문.\n1. 번호 목록 안 3단계 제목\n   번호 목록 본문.\n- [ ] 할 일 안 2단계 제목";
            check("list-continuation-nested",continuationPlain);
            string continuationShown=continuationPlain.Replace("- [ ] ","☐ ");
            if(String.Equals(MarkdownPlainText.Convert(continuationPlain),continuationShown,StringComparison.Ordinal)){passed++;report.Append("PASS list-continuation-nested-output\n");}
            else{failed++;report.Append("FAIL list-continuation-nested-output [").Append(Md2TxtEscape(MarkdownPlainText.Convert(continuationPlain))).Append("]\n");}
            string continuationHeadings="본문 첫 줄입니다.\n\n- 목록 첫째 항목\n- ## 목록 안 2단계 제목\n  목록 안 본문 한 줄.\n  - ### 한 단계 들어간 목록 안 3단계 제목\n    더 들어간 목록 본문.\n    - #### 두 단계 들어간 4단계 제목\n      가장 깊은 목록 본문.\n1. ### 번호 목록 안 3단계 제목\n   번호 목록 본문.\n- [ ] ## 할 일 안 2단계 제목";
            check("list-continuation-nested-headings",continuationHeadings);
            if(String.Equals(MarkdownPlainText.Convert(continuationHeadings),continuationShown,StringComparison.Ordinal)){passed++;report.Append("PASS list-continuation-nested-headings-output\n");}
            else{failed++;report.Append("FAIL list-continuation-nested-headings-output [").Append(Md2TxtEscape(MarkdownPlainText.Convert(continuationHeadings))).Append("]\n");}
            // A heading inside a list item or quote, each form on its own: oracle comparison plus the pinned text.
            string[,] headingForms={
                {"heading-in-list","- ## 목록 안 2단계 제목","- 목록 안 2단계 제목"},
                {"heading-in-list-closing-hashes","- ## 제목 ##","- 제목"},
                {"heading-in-list-keeps-text-hash","- ### C#","- C#"},
                {"heading-in-list-star-marker","* # 제목\n+ ###### 여섯","- 제목\n- 여섯"},
                {"heading-in-ordered","1. ### x","1. x"},
                {"heading-in-ordered-paren","1) ## a\n2) ## b","1. a\n2. b"},
                {"heading-in-task","- [ ] ## x\n- [x] ### y","☐ x\n☑ y"},
                {"heading-in-quote","> ## 인용 안 제목","인용 안 제목"},
                {"heading-in-quote-closing-hashes","> ### 제목 ###","제목"},
                {"heading-in-item-quote","- > ### x","- x"},
                {"heading-in-nested-list","- a\n  - #### x","- a\n  - x"},
                {"heading-in-nested-list-deeper","- a\n  - b\n    - ## x\n    - y","- a\n  - b\n    - x\n    - y"},
                {"heading-in-quote-list","> - ## x\n> - y","- x\n- y"},
                {"heading-in-list-with-body","- ## x\n  본문\n- y","- x\n  본문\n- y"},
                {"heading-in-list-not-heading-no-space","- ##x","- ##x"},
                {"heading-in-list-not-heading-seven","- ####### x","- ####### x"},
                {"heading-in-list-not-heading-bare","- ##","- ##"}
            };
            for(int f=0;f<headingForms.GetLength(0);f++){
                check(headingForms[f,0],headingForms[f,1]);
                string headingForm=MarkdownPlainText.Convert(headingForms[f,1]);
                if(String.Equals(headingForm,headingForms[f,2],StringComparison.Ordinal)){passed++;report.Append("PASS ").Append(headingForms[f,0]).Append("-output\n");}
                else{failed++;report.Append("FAIL ").Append(headingForms[f,0]).Append("-output [").Append(Md2TxtEscape(headingForm)).Append("]\n");}
            }
            check("heading-in-list-empty","- ## \n- b");
            check("list-continuation-deeper-marker","- a\n  b\n  - c\n    d\n  e\n  - f");
            check("list-continuation-ordered","1. a\n   b\n   - c\n2. d");
            check("list-continuation-after-blank","- a\n\n  b\n  - c");
            check("list-continuation-lazy-ends","- a\n b\n- c");
            // The writer's round trip keeps every line, indentation included, of the document with headings.
            string continuationDocument="본문 첫 줄입니다.\n\n- 목록 첫째 항목\n- ## 목록 안 2단계 제목\n  목록 안 본문 한 줄.\n  - ### 한 단계 들어간 목록 안 3단계 제목\n    더 들어간 목록 본문.\n    - #### 두 단계 들어간 4단계 제목\n      가장 깊은 목록 본문.\n1. ### 번호 목록 안 3단계 제목\n   번호 목록 본문.\n- [ ] ## 할 일 안 2단계 제목";
            try{
                string written=MarkdownWriter.Write(Markdown.Parse(continuationDocument,16,"Malgun Gothic",Brushes.Black,false,""));
                if(String.Equals(written,continuationDocument,StringComparison.Ordinal)){passed++;report.Append("PASS list-continuation-roundtrip\n");}
                else{failed++;report.Append("FAIL list-continuation-roundtrip [").Append(Md2TxtEscape(written)).Append("]\n");}
            }catch(Exception ex){failed++;report.Append("FAIL list-continuation-roundtrip [threw ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("]\n");}
            // A quote line indented to an open item's content column is a quote inside that item: one quote
            // with the item's own "- > text" line, and the list stays open (the print path, the live view, the
            // HTML export and this converter read one rule). The reported document with its headings is compared
            // with the oracle below, and its print model, pinned text and the writer's round trip are checked too.
            check("list-quote-continuation","- > 목록 안 인용 3단계 제목\n  > 목록 안 인용 본문.\n\n> - 인용 안 목록 5단계 제목\n>   인용 안 목록 본문.");
            check("list-quote-continuation-ordered","1. > a\n   > b\n1. c");
            check("list-quote-continuation-plain-item","- a\n  > b\n- c");
            check("list-quote-continuation-nested","- x\n  - > a\n    > b\n  - c");
            check("list-quote-continuation-after-blank","- > a\n\n  > b");
            check("list-quote-column-zero-ends-list","- a\n> b\n- c");
            check("list-quote-under-content-column","1. a\n  > b");
            check("list-quote-holding-list-stays-quote","- a\n  > - b");
            // A setext heading ("b" over "===") indented to an open item's content column is a heading inside that item and the
            // list stays open: the numbers go on past it. Its underline at column 0 ends the list, as it always did.
            string[,] setextForms={
                {"list-setext-continuation-keeps-numbers","1. a\n   b\n   ===\n1. c","1. a\n   b\n2. c"},
                {"list-setext-h2-keeps-list","- a\n  b\n  ---\n- c","- a\n  b\n- c"},
                {"list-setext-underline-column-zero-ends-list","1. a\n   b\n===\n1. c","1. a\n   b\n1. c"},
                {"list-setext-unindented-text-ends-list","1. a\nb\n===\n1. c","1. a\nb\n1. c"},
                // A nested item's underline sits at column 5, past the three columns a setext underline may be indented, so it is no
                // underline in any view: "===" stays text, the nested list goes on and the outer numbers go on past it (the oracle agrees).
                {"list-setext-nested-keeps-outer-numbers","1. a\n   - b\n     c\n     ===\n   - d\n1. e","1. a\n  - b\n     c\n     ===\n  - d\n2. e"},
                {"list-setext-after-blank","1. a\n\n   b\n   ===\n1. c","1. a\n\n   b\n2. c"}
            };
            for(int f=0;f<setextForms.GetLength(0);f++){
                check(setextForms[f,0],setextForms[f,1]);
                string setextForm=MarkdownPlainText.Convert(setextForms[f,1]);
                if(String.Equals(setextForm,setextForms[f,2],StringComparison.Ordinal)){passed++;report.Append("PASS ").Append(setextForms[f,0]).Append("-output\n");}
                else{failed++;report.Append("FAIL ").Append(setextForms[f,0]).Append("-output [").Append(Md2TxtEscape(setextForm)).Append("]\n");}
            }
            // A GitHub alert as the first quote line inside an item ("- > [!NOTE]" and "  > [!NOTE]" under a plain item) is an alert in the
            // item; the same marker on a line joined to an item's open quote is plain text of that quote.
            check("list-quote-alert-first-line","- > [!NOTE]\n  > 본문\n- c");
            check("list-quote-alert-continuation","- a\n  > [!NOTE]\n  > 본문\n- c");
            check("list-quote-alert-task","- [ ] > [!TIP]\n      > 본문\n- c");
            string alertJoined=MarkdownPlainText.Convert("- > x\n  > [!NOTE]\n  > y");
            check("list-quote-alert-joined-is-text","- > x\n  > [!NOTE]\n  > y");
            if(String.Equals(alertJoined,"- x\n[!NOTE]\ny",StringComparison.Ordinal)){passed++;report.Append("PASS list-quote-alert-joined-is-text-output\n");}
            else{failed++;report.Append("FAIL list-quote-alert-joined-is-text-output [").Append(Md2TxtEscape(alertJoined)).Append("]\n");}
            // A task item whose text is a quote: the second quote line joins the first.
            check("list-quote-task-second-line","- [ ] > a\n      > b\n- [x] c");
            // A backslash that ends a table cell is a literal backslash (the live view draws it); only a backslash at the end of the
            // source line is a hard break that drops, so the last cell of a row with no closing pipe loses it.
            check("table-cell-trailing-backslash","| 경로 | 설명 |\n| --- | --- |\n| C:\\Users\\ | 폴더 |");
            check("table-cell-trailing-backslash-open-row","| 경로 | 설명 |\n| --- | --- |\n| C:\\Users\\ | 폴더\\");
            string cellBackslash=MarkdownPlainText.Convert("| 경로 | 설명 |\n| --- | --- |\n| C:\\Users\\ | 폴더 |");
            if(cellBackslash.EndsWith("C:\\Users\\\t폴더",StringComparison.Ordinal)){passed++;report.Append("PASS table-cell-trailing-backslash-output\n");}
            else{failed++;report.Append("FAIL table-cell-trailing-backslash-output [").Append(Md2TxtEscape(cellBackslash)).Append("]\n");}
            string openRowBackslash=MarkdownPlainText.Convert("| 경로 | 설명 |\n| --- | --- |\n| C:\\Users\\ | 폴더\\");
            if(openRowBackslash.EndsWith("C:\\Users\\\t폴더",StringComparison.Ordinal)){passed++;report.Append("PASS table-cell-trailing-backslash-open-row-output\n");}
            else{failed++;report.Append("FAIL table-cell-trailing-backslash-open-row-output [").Append(Md2TxtEscape(openRowBackslash)).Append("]\n");}
            // The paragraph rule the cells follow: a backslash at the end of a line is a hard break and drops in every view.
            check("backslash-line-end-paragraph","끝 역슬래시 C:\\path\\\n다음 줄");
            string lineEndBackslash=MarkdownPlainText.Convert("끝 역슬래시 C:\\path\\\n다음 줄");
            if(String.Equals(lineEndBackslash,"끝 역슬래시 C:\\path\n다음 줄",StringComparison.Ordinal)){passed++;report.Append("PASS backslash-line-end-paragraph-output\n");}
            else{failed++;report.Append("FAIL backslash-line-end-paragraph-output [").Append(Md2TxtEscape(lineEndBackslash)).Append("]\n");}
            string listQuoteDocument="- > ### 목록 안 인용 3단계 제목\n  > 목록 안 인용 본문.\n\n> - ##### 인용 안 목록 5단계 제목\n>   인용 안 목록 본문.";
            check("list-quote-continuation-headings",listQuoteDocument);
            try{
                System.Windows.Documents.FlowDocument listQuoteParsed=Markdown.Parse(listQuoteDocument,16,"Malgun Gothic",Brushes.Black,false,"");
                System.Windows.Documents.List listQuoteList=listQuoteParsed.Blocks.FirstBlock as System.Windows.Documents.List;
                System.Windows.Documents.ListItem listQuoteItem=null;
                System.Windows.Documents.Section listQuoteSection=null;
                if(listQuoteList!=null&&listQuoteList.ListItems.Count==1)listQuoteItem=listQuoteList.ListItems.FirstListItem;
                if(listQuoteItem!=null&&listQuoteItem.Blocks.Count==1)listQuoteSection=listQuoteItem.Blocks.FirstBlock as System.Windows.Documents.Section;
                // One list, one item, one quote Section holding both lines (the heading and the body): a single bar.
                if(listQuoteSection!=null&&listQuoteSection.Blocks.Count==2){passed++;report.Append("PASS list-quote-continuation-one-quote\n");}
                else{failed++;report.Append("FAIL list-quote-continuation-one-quote [list=").Append(listQuoteList!=null).Append(" item=").Append(listQuoteItem!=null).Append(" section blocks=").Append(listQuoteSection==null?-1:listQuoteSection.Blocks.Count).Append("]\n");}
                string listQuotePinned="- 목록 안 인용 3단계 제목\n목록 안 인용 본문.\n\n- 인용 안 목록 5단계 제목\n  인용 안 목록 본문.";
                string listQuotePrinted=ReadableText(listQuoteParsed);
                if(String.Equals(listQuotePrinted,listQuotePinned,StringComparison.Ordinal)){passed++;report.Append("PASS list-quote-continuation-print-text\n");}
                else{failed++;report.Append("FAIL list-quote-continuation-print-text [").Append(Md2TxtEscape(listQuotePrinted)).Append("]\n");}
            }catch(Exception ex){failed++;report.Append("FAIL list-quote-continuation-print [threw ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("]\n");}
            // MD -> rich -> MD: an unedited list is written back byte for byte, the quote line's own indentation included.
            string[] listQuoteRoundTrips={listQuoteDocument,"1. > a\n   > b\n1. c","- a\n  > b\n- c","- x\n  - > a\n    > b\n  - c","- > a\n\n  > b","- > a\n    > b"};
            for(int r=0;r<listQuoteRoundTrips.Length;r++){
                try{
                    string written=MarkdownWriter.Write(Markdown.Parse(listQuoteRoundTrips[r],16,"Malgun Gothic",Brushes.Black,false,""));
                    if(String.Equals(written,listQuoteRoundTrips[r],StringComparison.Ordinal)){passed++;report.Append("PASS list-quote-continuation-roundtrip-").Append(r).Append('\n');}
                    else{failed++;report.Append("FAIL list-quote-continuation-roundtrip-").Append(r).Append(" [").Append(Md2TxtEscape(written)).Append("]\n");}
                }catch(Exception ex){failed++;report.Append("FAIL list-quote-continuation-roundtrip-").Append(r).Append(" [threw ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append("]\n");}
            }
            check("list-empty-item","- \n- b");
            // Quotes.
            check("quote","> 인용");
            check("quote-nested",">> 중첩");
            check("quote-list","> - a\n> - b");
            check("quote-blank","> a\n>\n> b");
            check("quote-code","> ```\n> a\n> ```");
            // GitHub alerts / Obsidian callouts: a title line (the text after the marker, else the default
            // English title), then the body.
            check("alert-note","> [!NOTE]\n> 본문");
            check("alert-tip","> [!TIP]\n> 본문");
            check("alert-important","> [!IMPORTANT]\n> 본문");
            check("alert-warning","> [!WARNING]\n> 본문");
            check("alert-caution","> [!CAUTION]\n> 본문");
            check("alert-lowercase","> [!tip]\n> 본문");
            check("alert-custom-title","> [!tip] good **job**\n> 본문");
            check("alert-alias","> [!info]\n> 본문");
            check("alert-unknown","> [!foo]\n> 본문");
            check("alert-fold-sign","> [!tip]-\n> 본문");
            check("alert-fold-sign-title","> [!warning]+ 주의\n> 본문");
            check("alert-title-only","> [!NOTE]");
            check("alert-nested","> 바깥\n> > [!NOTE]\n> > 안쪽");
            check("alert-lazy","> [!NOTE]\n> 본문\n게으른 줄");
            check("alert-list-body","> [!NOTE]\n> - a\n> - b");
            check("alert-not-first-line","> 가\n> [!NOTE]");
            check("alert-glued-text","> [!NOTE]글자");
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
