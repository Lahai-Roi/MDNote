using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace MDNote {
// --htmlexport-probe <dir>: File > Save As > HTML 파일 (*.html). Runs HtmlExporter.Convert (Save As's own converter) on a
// sample that holds every Markdown form the app draws and checks the key fragments, the title rule, the image
// path rewrite for every folder relation, the file encoding and a 4,000,000 character timing. No window, no
// dialog, no clipboard. The sample page is saved as <dir>\sample.html for a later look in a browser. The last
// section drives Save As in a hidden window (SaveTargetPicker stands in for the file type list).
static class HtmlExportProbe {
    static readonly StringBuilder Report=new StringBuilder();
    static bool All=true;static int Passes,Fails;

    static void Check(string name,bool ok,string detail){
        if(ok)Passes++;else Fails++;
        All&=ok;
        Report.Append(ok?"PASS ":"FAIL ").Append(name);
        if(!ok&&detail!=null&&detail.Length>0)Report.Append(" | ").Append(detail.Replace("\r","").Replace("\n","\\n"));
        Report.Append('\n');
    }
    static string Norm(string html){return html.Replace("\r\n","\n");}
    // One check per fragment; the first missing one names itself in the report.
    static void Has(string name,string html,params string[] fragments){
        string page=Norm(html);
        foreach(string fragment in fragments){
            if(page.IndexOf(fragment,StringComparison.Ordinal)<0){Check(name,false,"missing: "+fragment);return;}
        }
        Check(name,true,null);
    }
    static void Lacks(string name,string html,params string[] fragments){
        string page=Norm(html);
        foreach(string fragment in fragments){
            if(page.IndexOf(fragment,StringComparison.Ordinal)>=0){Check(name,false,"present: "+fragment);return;}
        }
        Check(name,true,null);
    }

    const string Sample=
        "# 제목 *강조* 첫 제목\n"+
        "## 둘째 <제목>\n"+
        "Setext 제목\n"+
        "===\n"+
        "\n"+
        "일반 **굵게** *기울임* ~~취소~~ `인라인 코드` `&amp;` <span style=\"color:#ff0000;font-size:24px\">빨강</span> <font color=\"blue\">파랑</font>\n"+
        "같은 문단 둘째 줄<br>줄바꿈 &copy; &amp; \\*이스케이프\\* a<b <u>밑줄</u> <!-- 숨김 -->\n"+
        "[링크](https://example.com/a_(b) \"제목\") <https://auto.example.com> <me@x.org> [참조][r1] [x](javascript:alert(1))\n"+
        "![그림](img/a%20b.png) ![svg](data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHdpZHRoPSI0MCIgaGVpZ2h0PSI0MCI+PHJlY3Qgd2lkdGg9IjQwIiBoZWlnaHQ9IjQwIiBmaWxsPSIjMjU2M2QyIi8+PC9zdmc+) ![원격](https://example.com/p.png)\n"+
        "수식은 그대로 $a<b$ 와 $$x^2$$ 와 $x_1 + y_2$ 입니다. <script>alert(1)</script>\n"+
        "\n"+
        "[r1]: https://ref.example.com\n"+
        "\n"+
        "---\n"+
        "\n"+
        "- 항목 1\n"+
        "  - 중첩 a\n"+
        "  - 중첩 b\n"+
        "- 항목 2\n"+
        "\n"+
        "5. 다섯째\n"+
        "6. 여섯째\n"+
        "   - 순서 안 불릿\n"+
        "\n"+
        "- [x] 완료\n"+
        "- [ ] 미완\n"+
        "\n"+
        "> 인용 첫 줄\n"+
        "> > 중첩 인용\n"+
        "> - 인용 안 목록\n"+
        "\n"+
        "```python\n"+
        "print('x < y')\n"+
        "```\n"+
        "~~~\n"+
        "틸데 `코드` & <tag>\n"+
        "~~~\n"+
        "\n"+
        "| 이름 | 값 | 가운데 |\n"+
        "|:---|---:|:---:|\n"+
        "| a | 1 | x |\n"+
        "| b \\| c | 2 |\n"+
        "\n"+
        "각주 문장[^1] 끝.\n"+
        "\n"+
        "[^1]: 각주 본문 **굵게**\n";

    internal static int Run(string outDir){
        string dir=Path.GetFullPath(outDir);Directory.CreateDirectory(dir);
        Report.Append("# htmlexport-probe\n");
        try{RunAll(dir);}
        catch(Exception ex){All=false;Report.Append("EXCEPTION: ").Append(ex.ToString()).Append('\n');}
        Report.Append('\n').Append("SUMMARY: ").Append(All?"PASS":"FAIL").Append(" passes=").Append(Passes).Append(" fails=").Append(Fails).Append('\n');
        string body=Report.ToString();
        string stale=Path.Combine(dir,All?"htmlexport-probe-FAIL.txt":"htmlexport-probe-PASS.txt");
        try{if(File.Exists(stale))File.Delete(stale);}catch(Exception){}
        try{File.WriteAllText(Path.Combine(dir,"htmlexport-probe.txt"),body,new UTF8Encoding(false));}catch(Exception){}
        try{File.WriteAllText(Path.Combine(dir,All?"htmlexport-probe-PASS.txt":"htmlexport-probe-FAIL.txt"),body,new UTF8Encoding(false));}catch(Exception){}
        return All?0:1;
    }

    static void RunAll(string dir){
        string docDir=Path.Combine(dir,"doc"),imgDir=Path.Combine(docDir,"img");
        Directory.CreateDirectory(imgDir);
        File.WriteAllText(Path.Combine(imgDir,"a b.png"),"x");   // the sample page links this name; the file only has to exist for a browser
        string documentPath=Path.Combine(docDir,"sample.md");
        string samplePath=Path.Combine(dir,"sample.html");
        string html=HtmlExporter.Convert(Sample,documentPath,samplePath,"ko");
        HtmlExporter.WriteFile(samplePath,html);
        string page=Norm(html);

        // ---- page frame ----
        Has("frame",html,"<!DOCTYPE html>\n","<html lang=\"ko\">","<meta charset=\"utf-8\">","<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">","<style>","</style>","</html>\n");
        Lacks("frame-no-script",html,"<script","javascript:","<link ","http-equiv");
        Has("lang-en",HtmlExporter.Convert("x","","","en"),"<html lang=\"en\">");
        Has("lang-zh",HtmlExporter.Convert("x","","","zh-TW"),"<html lang=\"zh-TW\">");

        // ---- inline ----
        Has("heading-1",html,"<h1 id=\"제목-강조-첫-제목\">제목 <em>강조</em> 첫 제목</h1>");
        Has("heading-2-escaped",html,"<h2 id=\"둘째-제목\">둘째 &lt;제목&gt;</h2>");
        Has("heading-setext",html,"<h1 id=\"setext-제목\">Setext 제목</h1>");
        Has("bold-italic-strike",html,"<strong>굵게</strong>","<em>기울임</em>","<del>취소</del>");
        Has("inline-code",html,"<code>인라인 코드</code>","<code>&amp;amp;</code>");
        Has("span-style",html,"<span style=\"color:#ff0000;font-size:24px;\">빨강</span>");
        Has("font-color",html,"<span style=\"color:blue;\">파랑</span>");
        Has("entities-escapes",html,"©"," &amp; ","*이스케이프*");
        Lacks("escapes-not-italic",html,"<em>이스케이프</em>");
        Has("br-token",html,"둘째 줄<br>\n줄바꿈");
        Has("raw-html-escaped",html,"a&lt;b","&lt;u&gt;밑줄&lt;/u&gt;","&lt;script&gt;alert(1)&lt;/script&gt;");
        Lacks("comment-hidden",html,"숨김","<!--");
        Has("link-title",html,"<a href=\"https://example.com/a_(b)\" title=\"제목\">링크</a>");
        Has("autolink",html,"<a href=\"https://auto.example.com\">https://auto.example.com</a>","<a href=\"mailto:me@x.org\">me@x.org</a>");
        Has("reference-link",html,"<a href=\"https://ref.example.com\">참조</a>");
        Has("javascript-link-dropped",html,"</a> x<br>\n<img");   // the label stays as plain text
        Lacks("javascript-link-no-href",html,"href=\"javascript");
        Has("math-plain",html,"$a&lt;b$","$$x^2$$","$x_1 + y_2$");
        Has("two-lines-one-paragraph",HtmlExporter.Convert("첫 줄\n둘째 줄","","",null),"<p>첫 줄<br>\n둘째 줄</p>");

        // ---- blocks ----
        Has("hr",html,"<hr>");
        Has("ul-nested",html,"<ul>\n<li>항목 1\n<ul>\n<li>중첩 a</li>\n<li>중첩 b</li>\n</ul></li>\n<li>항목 2</li>\n</ul>");
        Has("ol-nested-start",html,"<ol start=\"5\">\n<li>다섯째</li>\n<li>여섯째\n<ul>\n<li>순서 안 불릿</li>\n</ul></li>\n</ol>");
        Has("task-list",html,"<ul class=\"task\">\n<li class=\"task\"><input type=\"checkbox\" disabled checked> 완료</li>\n<li class=\"task\"><input type=\"checkbox\" disabled> 미완</li>\n</ul>");
        Has("quote-nested-list",html,"<blockquote>\n<p>인용 첫 줄</p>\n<blockquote>\n<p>중첩 인용</p>\n</blockquote>\n<ul>\n<li>인용 안 목록</li>\n</ul>\n</blockquote>");
        // Lazy continuation: a line without ">" right after a quote's paragraph line joins that paragraph
        // (the live editor draws it inside the quote); a blank line, a block start, or a blank/heading/list
        // line inside the quote ends it.
        string lazyHtml=Norm(HtmlExporter.Convert("> 인용 하나\n> 인용 둘\n게으른 줄","","",null));
        Has("quote-lazy-continuation",lazyHtml,"<blockquote>\n<p>인용 하나<br>\n인용 둘<br>\n게으른 줄</p>\n</blockquote>");
        Check("quote-lazy-one-blockquote",Count(lazyHtml,"<blockquote>")==1&&Count(lazyHtml,"<p>")==1,"blockquote="+Count(lazyHtml,"<blockquote>")+" p="+Count(lazyHtml,"<p>"));
        Has("quote-lazy-nested",Norm(HtmlExporter.Convert("> > 안쪽\n게으른 줄","","",null)),"<blockquote>\n<blockquote>\n<p>안쪽<br>\n게으른 줄</p>\n</blockquote>\n</blockquote>");
        Has("quote-lazy-ends-at-blank",Norm(HtmlExporter.Convert("> 인용\n\n바깥 문단","","",null)),"<blockquote>\n<p>인용</p>\n</blockquote>\n<p>바깥 문단</p>");
        Has("quote-lazy-not-after-blank-quote-line",Norm(HtmlExporter.Convert("> 인용\n>\n바깥 문단","","",null)),"<blockquote>\n<p>인용</p>\n</blockquote>\n<p>바깥 문단</p>");
        Has("quote-lazy-not-heading",Norm(HtmlExporter.Convert("> a\n# h","","",null)),"<blockquote>\n<p>a</p>\n</blockquote>\n<h1");
        Has("quote-lazy-not-list",Norm(HtmlExporter.Convert("> a\n- b","","",null)),"<blockquote>\n<p>a</p>\n</blockquote>\n<ul>");
        Has("quote-lazy-not-rule",Norm(HtmlExporter.Convert("> a\n***","","",null)),"<blockquote>\n<p>a</p>\n</blockquote>\n<hr>");
        Has("quote-lazy-not-fence",Norm(HtmlExporter.Convert("> a\n```\nx\n```","","",null)),"<blockquote>\n<p>a</p>\n</blockquote>\n<pre><code>x</code></pre>");
        Has("quote-lazy-not-after-heading-in-quote",Norm(HtmlExporter.Convert("> # h\n바깥 문단","","",null)),"</blockquote>\n<p>바깥 문단</p>");
        Has("quote-lazy-not-table",Norm(HtmlExporter.Convert("> a\n| x | y |\n|---|---|\n| 1 | 2 |","","",null)),"<blockquote>\n<p>a</p>\n</blockquote>\n<div class=\"table-wrap\">");
        Has("fenced-code-language",html,"<pre><code class=\"language-python\">print('x &lt; y')</code></pre>");
        Has("fenced-code-tilde",html,"<pre><code>틸데 `코드` &amp; &lt;tag&gt;</code></pre>");
        Has("table",html,"<table>","<thead>","<th style=\"text-align:left\">이름</th>","<th style=\"text-align:right\">값</th>","<th style=\"text-align:center\">가운데</th>","<td style=\"text-align:left\">a</td>","<td style=\"text-align:right\">1</td>","<td style=\"text-align:center\">x</td>");
        Has("table-escaped-pipe-and-padding",html,"<td style=\"text-align:left\">b | c</td>","<td style=\"text-align:right\">2</td><td style=\"text-align:center\"></td>");
        Has("footnote",html,"<sup><a href=\"#fn-1\" id=\"fnref-1\">1</a></sup>","<p class=\"footnote\" id=\"fn-1\"><a href=\"#fnref-1\">1.</a> 각주 본문 <strong>굵게</strong></p>");
        Lacks("definition-silent",html,"ref.example.com</p>","[r1]:");
        Has("text-mode",HtmlExporter.Convert("a<b\r\n  c & d","","","ko",null,true),"<pre class=\"plain\">a&lt;b\n  c &amp; d</pre>");
        Has("empty-document",HtmlExporter.Convert("","","",null),"<article>\n</article>");

        // ---- heading ids: the slug rule of the app's own "#heading" jump (AnchorRule.Slug on the heading's
        // visible text), first of equal headings keeps the plain slug, later ones get -1, -2, ... ----
        string idMd=
            "# Hello World, Again!\n"+
            "## C# & .NET: 100% (v2.0)\n"+
            "### 한글 제목\n"+
            "## 반복\n"+
            "## 반복\n"+
            "## 반복\n"+
            "## 반복 1\n"+
            "## `코드` **굵은** [링크](https://x.test) 끝\n"+
            "## !!!\n"+
            "Dash Title\n"+
            "---\n"+
            "- # 목록 제목\n"+
            "> ## 인용 제목\n"+
            "\n"+
            "[한글](#한글-제목) [대문자](#Hello-World-Again) [인코딩](#%ED%95%9C%EA%B8%80-%EC%A0%9C%EB%AA%A9) [코드](#코드-굵은-링크-끝) [없음](#top) [점](#a.b)\n";
        string idHtml=Norm(HtmlExporter.Convert(idMd,"","",null));
        Has("id-english",idHtml,"<h1 id=\"hello-world-again\">Hello World, Again!</h1>");
        Has("id-punctuation",idHtml,"<h2 id=\"c--net-100-v20\">C# &amp; .NET: 100% (v2.0)</h2>");
        Has("id-korean",idHtml,"<h3 id=\"한글-제목\">한글 제목</h3>");
        Has("id-duplicates",idHtml,"<h2 id=\"반복\">반복</h2>","<h2 id=\"반복-1\">반복</h2>","<h2 id=\"반복-2\">반복</h2>");
        Has("id-duplicate-never-reused",idHtml,"<h2 id=\"반복-1-1\">반복 1</h2>");
        Has("id-inline-formatting-plain-text",idHtml,"<h2 id=\"코드-굵은-링크-끝\"><code>코드</code> <strong>굵은</strong> <a href=\"https://x.test\">링크</a> 끝</h2>");
        Has("id-empty-slug-has-none",idHtml,"<h2>!!!</h2>");
        Has("id-setext-dash",idHtml,"<h2 id=\"dash-title\">Dash Title</h2>");
        Has("id-list-and-quote",idHtml,"<h1 id=\"목록-제목\">목록 제목</h1>","<h2 id=\"인용-제목\">인용 제목</h2>");
        var idSet=new HashSet<string>();bool idUnique=true;
        foreach(System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(idHtml,"<h[1-6] id=\"([^\"]*)\"")){if(!idSet.Add(m.Groups[1].Value))idUnique=false;}
        Check("id-all-unique",idUnique&&idSet.Count==11,"ids "+idSet.Count);
        Has("id-link-equals-id",idHtml,"<a href=\"#한글-제목\">한글</a>");
        Has("id-link-lowercased-like-app",idHtml,"<a href=\"#hello-world-again\">대문자</a>");
        Has("id-link-percent-decoded-like-app",idHtml,"<a href=\"#한글-제목\">인코딩</a>");
        Has("id-link-to-formatted-heading",idHtml,"<a href=\"#코드-굵은-링크-끝\">코드</a>");
        Has("id-link-other-kept",idHtml,"<a href=\"#top\">없음</a>","<a href=\"#a.b\">점</a>");
        bool linksResolve=true;string unresolved="";
        foreach(string wanted in new[]{"#한글-제목","#hello-world-again","#코드-굵은-링크-끝"}){
            if(idHtml.IndexOf("id=\""+wanted.Substring(1)+"\"",StringComparison.Ordinal)<0){linksResolve=false;unresolved+=wanted+" ";}
        }
        Check("id-every-slug-link-has-a-heading",linksResolve,unresolved);

        // ---- title rule ----
        Has("title-heading",html,"<title>제목 강조 첫 제목</title>");
        Has("title-escaped",HtmlExporter.Convert("## A & B <c>\n","","",null),"<title>A &amp; B &lt;c&gt;</title>");
        Has("title-file-name",HtmlExporter.Convert("본문만 있다","C:\\docs\\메모 노트.md","C:\\docs\\x.html"),"<title>메모 노트</title>");
        Has("title-fallback",HtmlExporter.Convert("본문만 있다","","C:\\docs\\x.html"),"<title>MDNote</title>");
        Has("title-skips-empty-heading",HtmlExporter.Convert("# \n## 진짜\n","C:\\docs\\메모.md",""),"<title>진짜</title>");
        Has("title-not-from-code",HtmlExporter.Convert("```\n# 코드 안\n```\n","C:\\docs\\메모.md",""),"<title>메모</title>");

        // ---- image paths ----
        string md="![x](pics/a.png)";
        Has("img-same-folder",HtmlExporter.Convert(md,"C:\\docs\\notes\\a.md","C:\\docs\\notes\\a.html"),"<img src=\"pics/a.png\" alt=\"x\">");
        Has("img-same-folder-no-html-path",HtmlExporter.Convert(md,"C:\\docs\\notes\\a.md",""),"<img src=\"pics/a.png\" alt=\"x\">");
        Has("img-same-folder-keeps-dotdot",HtmlExporter.Convert("![x](../up/a.png)","C:\\docs\\notes\\a.md","C:\\docs\\notes\\b.html"),"src=\"../up/a.png\"");
        Has("img-html-in-subfolder",HtmlExporter.Convert(md,"C:\\docs\\notes\\a.md","C:\\docs\\notes\\out\\a.html"),"src=\"../pics/a.png\"");
        Has("img-html-in-sibling-folder",HtmlExporter.Convert(md,"C:\\docs\\notes\\a.md","C:\\docs\\site\\a.html"),"src=\"../notes/pics/a.png\"");
        Has("img-html-in-parent-folder",HtmlExporter.Convert(md,"C:\\docs\\notes\\a.md","C:\\docs\\a.html"),"src=\"notes/pics/a.png\"");
        Has("img-other-drive",HtmlExporter.Convert(md,"C:\\docs\\notes\\a.md","Z:\\web\\a.html"),"src=\"file:///C:/docs/notes/pics/a.png\"");
        Has("img-absolute-same-drive",HtmlExporter.Convert("![x](C:\\docs\\pics\\z.png)","C:\\docs\\notes\\a.md","C:\\docs\\notes\\out\\a.html"),"src=\"../../pics/z.png\"");
        Has("img-absolute-other-drive",HtmlExporter.Convert("![x](Z:\\pics\\z%20q.png)","C:\\docs\\notes\\a.md","C:\\docs\\notes\\a.html"),"src=\"file:///Z:/pics/z%20q.png\"");
        Has("img-file-uri-input",HtmlExporter.Convert("![x](file:///C:/docs/pics/y.png)","C:\\docs\\notes\\a.md","C:\\docs\\notes\\a.html"),"src=\"../pics/y.png\"");
        Has("img-staged-untitled",HtmlExporter.Convert("![x](C:\\Users\\u\\Documents\\MDNote\\images\\i.png)","","C:\\Users\\u\\Desktop\\p.html"),"src=\"../Documents/MDNote/images/i.png\"");
        Has("img-untitled-relative-unchanged",HtmlExporter.Convert(md,"","C:\\Users\\u\\Desktop\\p.html"),"src=\"pics/a.png\"");
        Has("img-http-unchanged",HtmlExporter.Convert("![x](<https://example.com/a b.png>)","C:\\d\\a.md","Z:\\w\\a.html"),"src=\"https://example.com/a b.png\"");
        Has("img-http-query-escaped",HtmlExporter.Convert("![x](https://example.com/a.png?x=1&y=2)","C:\\d\\a.md","Z:\\w\\a.html"),"src=\"https://example.com/a.png?x=1&amp;y=2\"");
        Has("img-data-unchanged",HtmlExporter.Convert("![x](data:image/png;base64,iVBORw0KGgo=)","C:\\d\\a.md","Z:\\w\\a.html"),"src=\"data:image/png;base64,iVBORw0KGgo=\"");
        Has("img-korean-name",HtmlExporter.Convert("![그림](사진/그림.png)","C:\\d\\a.md","C:\\d\\a.html"),"src=\"%EC%82%AC%EC%A7%84/%EA%B7%B8%EB%A6%BC.png\" alt=\"그림\"");
        Has("img-percent-name",HtmlExporter.Convert("![x](a%2350%25.png)","C:\\d\\a.md","C:\\d\\a.html"),"src=\"a%2350%25.png\"");
        Has("img-title-attribute",HtmlExporter.Convert("![x](a.png \"설명\")","C:\\d\\a.md","C:\\d\\a.html"),"src=\"a.png\" alt=\"x\" title=\"설명\"");
        Has("img-unsupported-scheme",HtmlExporter.Convert("![x](ftp://h/a.png)","C:\\d\\a.md","C:\\d\\a.html","ko","[이미지: "),"<span class=\"missing-image\">[이미지: x]</span>");
        Has("img-sample-page",html,"src=\"doc/img/a%20b.png\" alt=\"그림\"","<img src=\"data:image/svg+xml;base64,","src=\"https://example.com/p.png\" alt=\"원격\"");
        Has("link-relative-kept",HtmlExporter.Convert("[a](other.md#h) [b](#top)","C:\\d\\a.md","Z:\\w\\a.html"),"href=\"other.md#h\"","href=\"#top\"");

        // ---- file encoding ----
        byte[] bytes=File.ReadAllBytes(samplePath);
        Check("file-no-bom",!(bytes.Length>=3&&bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF),"first bytes "+(bytes.Length>=3?bytes[0].ToString("X2")+bytes[1].ToString("X2")+bytes[2].ToString("X2"):"?"));
        string decoded=new UTF8Encoding(false,true).GetString(bytes);
        Check("file-utf8-roundtrip",decoded==html&&decoded.Contains("제목"),"decoded length "+decoded.Length+" html length "+html.Length);
        int lf=0,crlf=0;
        for(int i=0;i<decoded.Length;i++){if(decoded[i]=='\n'){lf++;if(i>0&&decoded[i-1]=='\r')crlf++;}}
        Check("file-crlf-only",lf>0&&lf==crlf,"lf="+lf+" crlf="+crlf);

        // ---- guards ----
        var deep=new StringBuilder();
        for(int i=0;i<5000;i++)deep.Append('>');
        deep.Append(" 깊다");
        string deepHtml=null;Exception deepError=null;
        try{deepHtml=HtmlExporter.Convert(deep.ToString(),"","");}catch(Exception ex){deepError=ex;}
        Check("deep-quote-bounded",deepError==null&&deepHtml!=null&&deepHtml.Contains("깊다"),deepError==null?"":deepError.GetType().Name);
        var nested=new StringBuilder();
        for(int i=0;i<200;i++)nested.Append(' ',i*2).Append("- 단계 ").Append(i).Append('\n');
        string nestedHtml=null;deepError=null;
        try{nestedHtml=HtmlExporter.Convert(nested.ToString(),"","");}catch(Exception ex){deepError=ex;}
        Check("deep-list-closes",deepError==null&&nestedHtml!=null&&Count(Norm(nestedHtml),"<ul>")==Count(Norm(nestedHtml),"</ul>")&&Count(Norm(nestedHtml),"<li>")==Count(Norm(nestedHtml),"</li>"),deepError==null?"":deepError.GetType().Name);
        Check("tag-balance-sample",Count(page,"<ul>")+Count(page,"<ul class=\"task\">")==Count(page,"</ul>")&&Count(page,"<ol")==Count(page,"</ol>")&&Count(page,"<li")==Count(page,"</li>")&&Count(page,"<blockquote>")==Count(page,"</blockquote>")&&Count(page,"<table>")==Count(page,"</table>")&&Count(page,"<p>")+Count(page,"<p class")==Count(page,"</p>"),"ul "+Count(page,"<ul")+"/"+Count(page,"</ul>")+" li "+Count(page,"<li")+"/"+Count(page,"</li>")+" p "+Count(page,"<p")+"/"+Count(page,"</p>"));

        // ---- timing: a generated 4,000,000 character document (three runs, milliseconds only) ----
        var big=new StringBuilder(4100000);
        int block=0;
        while(big.Length<4000000){
            block++;
            big.Append("## 제목 ").Append(block).Append("\n\n");
            big.Append("본문 **굵게** *기울임* `코드` [링크](https://example.com/").Append(block).Append(") 와 ![그림](img/p").Append(block).Append(".png) 그리고 <span style=\"color:#2563d2\">색</span> 문장입니다 & < >.\n");
            big.Append("둘째 줄 ~~취소~~ 각주[^").Append(block).Append("] 끝.\n\n");
            big.Append("- 항목 가\n  - 하위 나\n- [x] 완료\n\n> 인용 문장\n\n");
            big.Append("| 이름 | 값 |\n|:--|--:|\n| a | ").Append(block).Append(" |\n\n");
            big.Append("```cs\nint x = ").Append(block).Append(" < 3;\n```\n\n");
            big.Append("[^").Append(block).Append("]: 각주 ").Append(block).Append("\n\n");
        }
        string bigText=big.ToString();
        long[] times=new long[3];int bigLength=0;bool bigOk=true;
        for(int run=0;run<3;run++){
            var clock=Stopwatch.StartNew();
            string bigHtml=HtmlExporter.Convert(bigText,Path.Combine(docDir,"big.md"),Path.Combine(dir,"big.html"),"ko");
            byte[] bigBytes=HtmlExporter.Encode(bigHtml);
            clock.Stop();
            times[run]=clock.ElapsedMilliseconds;bigLength=bigHtml.Length;
            bigOk&=bigHtml.EndsWith("</html>\r\n",StringComparison.Ordinal)&&bigBytes.Length>=bigHtml.Length&&bigHtml.IndexOf("<h2 id=\"제목-"+block+"\">제목 "+block+"</h2>",StringComparison.Ordinal)>0;
        }
        Check("timing-4m-output",bigOk,"html length "+bigLength);
        Report.Append("INFO timing-4m-chars input="+bigText.Length+" blocks="+block+" html="+bigLength+" ms(run1,run2,run3)="+times[0]+","+times[1]+","+times[2]+" (convert + UTF-8 encode, no file write, 3 runs)\n");

        try{SaveAsChecks(dir);}
        catch(Exception ex){Exception inner=ex is TargetInvocationException&&ex.InnerException!=null?ex.InnerException:ex;Check("saveas-no-exception",false,inner.GetType().Name+": "+inner.Message+" "+inner.StackTrace);}
    }
    static int Count(string value,string needle){
        int count=0,at=0;
        while((at=value.IndexOf(needle,at,StringComparison.Ordinal))>=0){count++;at+=needle.Length;}
        return count;
    }

    // ---- Save As > "HTML 파일 (*.html)" (window checks; the file type list is stood in for by SaveTargetPicker: a .html name = the HTML type) ----
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    static void Pump(){LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
    static object Peek(MainWindow window,string name){
        FieldInfo field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");
        return field.GetValue(window);
    }
    static object Call(MainWindow window,string name,params object[] args){
        MethodInfo method=typeof(MainWindow).GetMethod(name,Members);
        if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");
        return method.Invoke(window,args);
    }
    static bool Dirty(MainWindow window){
        Call(window,"FlushDocumentChanges");
        return (bool)typeof(MainWindow).GetProperty("HasUnsavedChanges",Members).GetValue(window,null);
    }
    static MainWindow NewWindow(){
        var window=new MainWindow {ShowInTaskbar=false,Width=900,Height=700};
        window.Show();window.Left=-20000;Pump();
        DispatcherTimer timer=Peek(window,"recoveryTimer") as DispatcherTimer;if(timer!=null)timer.Stop();
        return window;
    }
    static MainWindow OpenDocument(string file){
        MainWindow window=NewWindow();
        Call(window,"LoadFileCore",file);Call(window,"CaptureTab");Pump();
        window.SetMode(true);Pump();
        return window;
    }
    static void TypeAtEnd(MainWindow window,string text){
        SourceEditor editor=(SourceEditor)Peek(window,"source");editor.Select(editor.Document.TextLength,0);editor.TextArea.PerformTextInput(text);Pump();
    }
    static void CloseDocument(MainWindow window){
        if(window==null)return;
        try{
            Call(window,"FlushDocumentChanges");
            string current=((SourceEditor)Peek(window,"source")).Document.Text;
            typeof(MainWindow).GetField("text",Members).SetValue(window,current);
            typeof(MainWindow).GetField("saved",Members).SetValue(window,current);
            typeof(MainWindow).GetField("recoveredDocument",Members).SetValue(window,false);
            typeof(MainWindow).GetField("sourceSnapshotDirty",Members).SetValue(window,false);
            var tabs=(System.Collections.IList)typeof(MainWindow).GetField("documents",Members).GetValue(window);
            foreach(object tab in tabs){tab.GetType().GetField("Saved",Members).SetValue(tab,tab.GetType().GetField("Text",Members).GetValue(tab));tab.GetType().GetField("Recovered",Members).SetValue(tab,false);}
            window.Close();
        }catch(Exception){}
    }
    // Everything the export must leave alone, as one comparable line.
    static string DocumentState(MainWindow window){
        var recent=(List<string>)Peek(window,"recentFiles");
        var tabs=(System.Collections.IList)Peek(window,"documents");
        var encoding=(Encoding)Peek(window,"documentEncoding");
        return "path="+Peek(window,"path")+"|title="+window.Title+"|plain="+Peek(window,"textMode")+"|encoding="+encoding.CodePage+"/"+encoding.GetPreamble().Length
            +"|dirty="+Dirty(window)+"|recent="+String.Join(";",recent.ToArray())+"|tabs="+tabs.Count
            +"|stamp="+Peek(window,"documentFingerprint")+"@"+Peek(window,"documentFingerprintPath")+"|recovered="+Peek(window,"recoveredDocument");
    }
    static bool MenuHasHtml(Menu menu){
        var pending=new Queue<MenuItem>();
        foreach(object entry in menu.Items){var top=entry as MenuItem;if(top!=null)pending.Enqueue(top);}
        while(pending.Count>0){
            MenuItem item=pending.Dequeue();
            string header=Convert.ToString(item.Header)??"";
            if(header.IndexOf("HTML",StringComparison.OrdinalIgnoreCase)>=0||header.IndexOf("내보내기",StringComparison.Ordinal)>=0)return true;
            foreach(object child in item.Items){var sub=child as MenuItem;if(sub!=null)pending.Enqueue(sub);}
        }
        return false;
    }
    static string Read(string file){return File.ReadAllText(file,new UTF8Encoding(false));}
    static void SaveAsChecks(string dir){
        string root=Path.Combine(dir,"saveas");Directory.CreateDirectory(root);
        string exportDir=Path.Combine(root,"exp");Directory.CreateDirectory(exportDir);
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        string md=Path.Combine(root,"doc.md"),html=Path.Combine(exportDir,"out.html"),copy=Path.Combine(root,"copy.md");
        File.WriteAllText(md,"# 제목\n\n본문 **굵게** ![그림](img/p.png)\n",new UTF8Encoding(false));
        MainWindow window=null;
        try{
            window=OpenDocument(md);
            Check("saveas-file-menu-no-html",!MenuHasHtml((Menu)Peek(window,"topMenu")),"a menu item mentions HTML/내보내기");
            TypeAtEnd(window," 추가");
            string before=DocumentState(window);
            string typed=((SourceEditor)Peek(window,"source")).Document.Text;
            MainWindow.SaveTargetPicker=delegate(string name){return html;};
            bool result;
            try{result=(bool)Call(window,"Save",true);}finally{MainWindow.SaveTargetPicker=null;}
            Pump();
            Check("saveas-html-not-a-save",!result&&(bool)Peek(window,"LastSaveCancelled")&&!(bool)Peek(window,"LastSaveReported"),"Save returned "+result);
            string page=File.Exists(html)?Read(html):"";
            byte[] bytes=File.Exists(html)?File.ReadAllBytes(html):new byte[0];
            Has("saveas-html-written",page,"<!DOCTYPE html>","<h1 id=\"제목\">제목</h1>","<strong>굵게</strong>","추가","<img src=\"../img/p.png\"");
            Check("saveas-html-encoding",bytes.Length>3&&!(bytes[0]==0xEF&&bytes[1]==0xBB&&bytes[2]==0xBF)&&page.IndexOf("\r\n",StringComparison.Ordinal)>0&&page.Replace("\r\n","").IndexOf('\n')<0,"bytes="+bytes.Length);
            Check("saveas-html-doc-unchanged",DocumentState(window)==before,"before="+before+" after="+DocumentState(window));
            Check("saveas-html-text-unchanged",((SourceEditor)Peek(window,"source")).Document.Text==typed&&Read(md).IndexOf("추가",StringComparison.Ordinal)<0,"the .md was rewritten or the text changed");
            Check("saveas-html-no-picture-folder",!Directory.Exists(Path.Combine(exportDir,"out")),"a picture folder was made next to the html");

            // The same dialog still saves Markdown: path moves to the copy, the document is clean.
            MainWindow.SaveTargetPicker=delegate(string name){return copy;};
            try{result=(bool)Call(window,"Save",true);}finally{MainWindow.SaveTargetPicker=null;}
            Pump();
            Check("saveas-md-still-saves",result&&File.Exists(copy)&&Read(copy).Replace("\r\n","\n")==typed.Replace("\r\n","\n"),"saved="+result);
            Check("saveas-md-state",String.Equals(Convert.ToString(Peek(window,"path")),Path.GetFullPath(copy),StringComparison.OrdinalIgnoreCase)&&!Dirty(window),"path="+Peek(window,"path"));
        }finally{MainWindow.SaveTargetPicker=null;CloseDocument(window);}

        // A TXT document: HTML is allowed and the page is the plain-text one.
        string txt=Path.Combine(root,"note.txt"),txtHtml=Path.Combine(root,"note.html");
        File.WriteAllText(txt,"첫 줄 <b>\n  둘째 줄\n",new UTF8Encoding(false));
        window=null;
        try{
            window=OpenDocument(txt);
            string before=DocumentState(window);
            MainWindow.SaveTargetPicker=delegate(string name){return txtHtml;};
            bool result;
            try{result=(bool)Call(window,"Save",true);}finally{MainWindow.SaveTargetPicker=null;}
            Pump();
            string page=File.Exists(txtHtml)?Read(txtHtml):"";
            Has("saveas-txt-html",page,"<pre class=\"plain\">첫 줄 &lt;b&gt;\n  둘째 줄\n</pre>");
            Check("saveas-txt-unchanged",!result&&DocumentState(window)==before&&Read(txt).Replace("\r\n","\n")=="첫 줄 <b>\n  둘째 줄\n","before="+before);
        }finally{MainWindow.SaveTargetPicker=null;CloseDocument(window);}

        // The document's own file is never replaced by its page.
        string own=Path.Combine(root,"own.html");
        File.WriteAllText(own,"<p>원본</p>\n",new UTF8Encoding(false));
        window=null;string notice=null;
        try{
            window=OpenDocument(own);
            MainWindow.SaveTargetPicker=delegate(string name){return own;};
            MainWindow.SaveTargetInUseNotice=delegate(string message){notice=message;};
            bool result;
            try{result=(bool)Call(window,"Save",true);}finally{MainWindow.SaveTargetPicker=null;MainWindow.SaveTargetInUseNotice=null;}
            Pump();
            Check("saveas-html-own-file-refused",!result&&notice!=null&&Read(own)=="<p>원본</p>\n","result="+result+" notice="+notice);
        }finally{MainWindow.SaveTargetPicker=null;MainWindow.SaveTargetInUseNotice=null;CloseDocument(window);}
    }
}
}
