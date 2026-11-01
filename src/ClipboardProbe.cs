using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
namespace MDNote {public partial class MainWindow {
internal static int VerifyClipboard(string outDir){return ClipboardProbe.Run(outDir);}
// #27 probe: copy -> paste through the real DataObject handlers. Only the live surface is
// left: the basic view's clipboards were removed with the rich renderer.
// Samples are generated text; nothing is read from the system clipboard or the disk.
static class ClipboardProbe {
    static StringBuilder report;
    static bool all;
    static MainWindow liveSource,liveTarget;
    internal static int Run(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        report=new StringBuilder("# clipboard-probe\n");all=true;
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        bool old=ProbeDefaults.Live;
        try{
            liveSource=Create();liveTarget=Create();
            RunCases();RunWholeDocument();RunBlankLine();RunWord();RunRichEmphasis();RunPrecedence();RunParagraphGaps();RunReadableCopy();RunLiveTableCells();RunMergedTable();RunWordList();RunPasteEscapes();RunRawCopy();
        }catch(Exception ex){all=false;report.Append("EXCEPTION: ").Append(ex.ToString()).Append('\n');}
        finally{
            CloseWindow(liveSource);CloseWindow(liveTarget);
            ProbeDefaults.Live=old;
        }
        File.WriteAllText(Path.Combine(outDir,"clipboard-probe.txt"),report.ToString(),new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir,all?"clipboard-probe-PASS.txt":"clipboard-probe-FAIL.txt"),report.ToString(),new UTF8Encoding(false));
        return all?0:1;
    }
    static void Check(string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail).Append('\n');}
    static string Show(string value){if(value==null)return "<null>";return "\""+value.Replace("\r","\\r").Replace("\n","\\n")+"\"";}
    static string Canonical(string value){return (value??"").Replace("\r\n","\n").Replace('\r','\n').Trim('\n');}
    static void Use(bool live,Action action){bool old=ProbeDefaults.Live;ProbeDefaults.Live=live;try{action();}finally{ProbeDefaults.Live=old;}}
    static MainWindow Create(){
        bool old=ProbeDefaults.Live;ProbeDefaults.Live=true;
        try{
            var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;window.SetMode(true);LongLineBench.Idle();
            return window;
        }finally{ProbeDefaults.Live=old;}
    }
    static void CloseWindow(MainWindow window){
        if(window==null)return;
        try{window.saved=window.text;window.Close();}catch(Exception){}
    }
    static void SetLive(MainWindow window,string text){Use(true,delegate{window.SetText(text);LongLineBench.Idle();});}
    static DataObject LiveCopy(MainWindow window,int start,int length){
        DataObject data=null;
        Use(true,delegate{
            window.source.Select(start,length);
            data=new DataObject();
            window.SourceCopying(window.source.TextArea,new DataObjectCopyingEventArgs(data,false));
        });
        return data;
    }
    static string LivePasteAtEnd(MainWindow window,IDataObject data){return LivePasteAtEnd(window,data,false);}
    static string LivePasteAtEnd(MainWindow window,IDataObject data,bool plain){
        string result="";
        Use(true,delegate{
            window.SetText("");
            window.source.Select(window.source.Document.TextLength,0);
            window.PasteData(data,plain);window.FlushDocumentChanges();LongLineBench.Idle();
            result=Canonical(window.source.Text);
        });
        return result;
    }
    static void Case(string name,string sample,int liveStart,int liveLength,string expected){
        SetLive(liveSource,sample);
        DataObject liveData=LiveCopy(liveSource,liveStart,liveLength);
        string payload=MarkdownClipboard.Get(liveData);
        Check(name+".live-copy",payload==expected,"payload="+Show(payload)+" expected="+Show(expected));
        string got=LivePasteAtEnd(liveTarget,liveData);
        Check(name+".live-to-live",got==expected,"got="+Show(got));
    }
    static void RunCases(){
        string footnote="각주 예시[^1]와 참조 링크 [공식 문서][docs].";
        Case("footnote",footnote+"\n\n[^1]: 각주 본문\n[docs]: https://example.invalid/docs",0,footnote.Length,footnote);
        string link="[예제](https://example.invalid) 링크";
        Case("inline-link",link,0,link.Length,link);
        string styles="**굵게** *기울임* ~~취소~~ <span style=\"color:#D03030\">빨강</span>";
        Case("inline-styles",styles,0,styles.Length,styles);
        string item="- 항목";
        Case("list-item",item,2,item.Length-2,item);
        string nested="- 첫째 항목\n  - 들여쓴 항목\n- 둘째 항목";
        Case("list-nested",nested,2,nested.Length-2,nested);
        string open="- [ ] 할 일";
        Case("task-open",open,6,open.Length-6,open);
        string done="- [x] 끝난 일";
        Case("task-done",done,6,done.Length-6,done);
        string quote="> 인용문입니다.";
        Case("quote",quote,2,quote.Length-2,quote);
        string quoteTwo="> 첫째 인용\n> 둘째 인용";
        Case("quote-two",quoteTwo,2,quoteTwo.Length-2,quoteTwo);
        string heading="## 목록";
        Case("heading",heading,3,heading.Length-3,heading);
        string table="| a | b |\n| --- | --- |\n| 1 | 2 |";
        Case("table",table,0,table.Length,table);
    }
    static void RunWholeDocument(){
        string expected="# 제목\n\n본문 **굵게** 문단.";
        SetLive(liveSource,expected);
        DataObject liveData=LiveCopy(liveSource,0,expected.Length);
        Check("whole.live-copy",MarkdownClipboard.Get(liveData)==expected,"payload="+Show(MarkdownClipboard.Get(liveData)));
        string got=LivePasteAtEnd(liveTarget,liveData);
        Check("whole.live-to-live",got==expected,"got="+Show(got));
    }
    // A range that crosses a blank line keeps it; the writer joins blocks with one newline per
    // boundary and the blank paragraph contributes the second one.
    static void RunBlankLine(){
        string expected="첫 문단.\n\n둘째 **문단**.";
        SetLive(liveSource,expected+"\n\n꼬리말");
        DataObject liveData=LiveCopy(liveSource,0,expected.Length);
        Check("blank-line.live-copy",MarkdownClipboard.Get(liveData)==expected,"payload="+Show(MarkdownClipboard.Get(liveData)));
        string got=LivePasteAtEnd(liveTarget,liveData);
        Check("blank-line.live-to-live",got==expected,"got="+Show(got));
    }
    static void RunWord(){
        string html="<html><body><!--StartFragment--><p><b>굵게&nbsp;</b>본문</p><!--EndFragment--></body></html>";
        /* the NBSP after the bold text becomes a space and moves outside the pair (Y1) */
        string expected="**굵게** 본문";
        var data=new DataObject();
        data.SetData(DataFormats.Html,html);data.SetData(DataFormats.UnicodeText,"굵게 본문");
        string got=LivePasteAtEnd(liveTarget,data);
        Check("word.live-paste",got==expected,"got="+Show(got));
        DataObject liveRecopy=LiveCopy(liveTarget,0,expected.Length);
        Check("word.live-recopy",MarkdownClipboard.Get(liveRecopy)==expected,"payload="+Show(MarkdownClipboard.Get(liveRecopy)));
        string repaste=LivePasteAtEnd(liveTarget,liveRecopy);
        Check("word.live-repaste",repaste==expected,"got="+Show(repaste));
    }
    static DataObject RichHtml(string fragment,string plain){
        var data=new DataObject();
        data.SetData(DataFormats.Html,"<html><body><!--StartFragment-->"+fragment+"<!--EndFragment--></body></html>");
        data.SetData(DataFormats.UnicodeText,plain);
        return data;
    }
    static DataObject RichRtf(string rtf,string plain){
        var data=new DataObject();
        data.SetData(DataFormats.Rtf,rtf);data.SetData(DataFormats.UnicodeText,plain);
        return data;
    }
    // Pastes a rich payload at a caret inside the given text (no selection).
    static string LivePasteInto(MainWindow window,string text,int caret,IDataObject data){
        string result="";
        Use(true,delegate{
            window.SetText(text);LongLineBench.Idle();
            window.source.Select(caret,0);LongLineBench.Idle();
            window.PasteData(data,false);window.FlushDocumentChanges();LongLineBench.Idle();
            result=Canonical(window.source.Text);
        });
        return result;
    }
    // Y1: emphasis must not be emitted with whitespace inside its delimiters; Y2: a rich paste
    // inside an existing emphasis run of the same kind must not nest (and close) that pair.
    static void RunRichEmphasis(){
        string got=LivePasteAtEnd(liveTarget,RichHtml("<p><b>Word </b><span style=\"color:#C00000\">문서</span> 본문</p>","Word 문서 본문"));
        Check("rich.trailing-space",got=="**Word** <span style=\"color:#C00000\">문서</span> 본문","got="+Show(got));
        got=LivePasteAtEnd(liveTarget,RichHtml("<p>x<b> y </b>z</p>","x y z"));
        Check("rich.leading-trailing-space",got=="x **y** z","got="+Show(got));
        got=LivePasteAtEnd(liveTarget,RichHtml("<p>a<b> </b>b</p>","a b"));
        Check("rich.empty-run",got=="a b","got="+Show(got));
        got=LivePasteAtEnd(liveTarget,RichRtf("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Malgun Gothic;}}\\f0\\fs24 \\b word \\b0 tail}","word tail"));
        Check("rich.rtf-trailing-space",got=="**word** tail","got="+Show(got));
        got=LivePasteInto(liveTarget,"**굵은글자** 중간",6,RichHtml("<p><b>plain</b> text</p>","plain text"));
        Check("rich.bold-inside-bold",got=="**굵은글자plain text** 중간","got="+Show(got));
        got=LivePasteInto(liveTarget,"**굵은글자**",4,RichHtml("<p><i>it</i></p>","it"));
        Check("rich.italic-inside-bold",got=="**굵은*it*글자**","got="+Show(got));
        got=LivePasteInto(liveTarget,"**굵은글자**",6,RichHtml("<p><i>it</i></p>","it"));
        Check("rich.italic-at-bold-close-edge",got=="**굵은글자it**","got="+Show(got));
        got=LivePasteInto(liveTarget,"*기울임글자* 끝",6,RichHtml("<p><i>it</i> x</p>","it x"));
        Check("rich.italic-inside-italic",got=="*기울임글자it x* 끝","got="+Show(got));
        got=LivePasteInto(liveTarget,"~~취소글자~~",5,RichHtml("<p><s>gone</s></p>","gone"));
        Check("rich.strike-inside-strike",got=="~~취소글gone자~~","got="+Show(got));
        got=LivePasteInto(liveTarget,"**굵은글자** 중간",6,RichRtf("{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Malgun Gothic;}}\\f0\\fs24 plain \\b bold\\b0{} text}","plain bold text"));
        Check("rich.rtf-inside-bold",got=="**굵은글자plain bold text** 중간","got="+Show(got));
    }
    // #18: every paste path separates source paragraphs with exactly one blank line, while a
    // line break inside a paragraph stays a single line break.
    static void RunParagraphGaps(){
        string html="<html><body><!--StartFragment--><h1>제목</h1><p>부제</p><p>본문</p><!--EndFragment--></body></html>";
        var rich=new DataObject();
        rich.SetData(DataFormats.Html,html);rich.SetData(DataFormats.UnicodeText,"제목\r\n부제\r\n본문");
        string expected="# 제목\n\n부제\n\n본문";
        string got=LivePasteAtEnd(liveTarget,rich);
        Check("gaps.word-to-live",got==expected,"got="+Show(got));
        string text="제목\r\n\r\n부제\r\n\r\n본문";
        var plain=new DataObject();plain.SetData(DataFormats.UnicodeText,text);
        string plainExpected="제목\n\n부제\n\n본문";
        got=LivePasteAtEnd(liveTarget,plain,true);
        Check("gaps.text-plain-to-live",got==plainExpected,"got="+Show(got));
        // A single line break inside a paragraph stays inside it.
        string lines="첫 줄\r\n둘째 줄\r\n\r\n새 문단",linesExpected="첫 줄\n둘째 줄\n\n새 문단";
        var soft=new DataObject();soft.SetData(DataFormats.UnicodeText,lines);
        got=LivePasteAtEnd(liveTarget,soft,true);
        Check("gaps.text-linebreak-to-live",got==linesExpected,"got="+Show(got));
        // <br> stays inside the paragraph; the sibling paragraph gets the blank line.
        var br=new DataObject();
        br.SetData(DataFormats.Html,"<html><body><!--StartFragment--><p>제목<br>둘째 줄</p><p>본문</p><!--EndFragment--></body></html>");
        string brExpected="제목\n둘째 줄\n\n본문";
        got=LivePasteAtEnd(liveTarget,br);
        Check("gaps.br-html-to-live",got==brExpected,"got="+Show(got));
    }
    // #22: copy without formatting keeps nested list indentation and the task state.
    static void RunReadableCopy(){
        string sample="## 제목\n\n- 첫째 항목\n  1. 하위 항목\n- [ ] 할 일\n- [x] 끝난 일\n\n> 인용문입니다.\n\n본문입니다.";
        SetLive(liveSource,sample);
        string live=null;
        Use(true,delegate{
            liveSource.source.Select(0,liveSource.source.Document.TextLength);
            live=Canonical(MarkdownPlainText.Convert(liveSource.source.SelectedText));
        });
        string liveExpected="제목\n\n- 첫째 항목\n  1. 하위 항목\n☐ 할 일\n☑ 끝난 일\n\n인용문입니다.\n\n본문입니다.";
        Check("readable.live-nested-task",live==liveExpected,"payload="+Show(live)+" expected="+Show(liveExpected));
    }
    static void RunPrecedence(){
        string expected="각주 예시[^1]와 참조 링크 [공식 문서][docs].";
        SetLive(liveSource,expected+"\n\n[^1]: 각주 본문\n[docs]: https://example.invalid/docs");
        DataObject liveData=LiveCopy(liveSource,0,expected.Length);
        string liveUnicode=liveData.GetDataPresent(DataFormats.UnicodeText)?liveData.GetData(DataFormats.UnicodeText) as string:null;
        Check("precedence.live-unicode-text",liveUnicode==expected.Replace("\n","\r\n"),"unicode="+Show(liveUnicode));
        // The Markdown payload wins over the HTML fragment on the live surface too.
        var data=new DataObject();
        MarkdownClipboard.Set(data,expected);
        data.SetData(DataFormats.Html,"<html><body><!--StartFragment--><span style='color:#2563D2'>1</span><!--EndFragment--></body></html>");
        string got=LivePasteAtEnd(liveTarget,data);
        Check("precedence.live-beats-html",got==expected,"got="+Show(got));
    }
    // Stage 4: a selection through live table cells copies the basic view's projection (one cell
    // keeps its clipped text, several cells become a fragment with only those columns and the
    // table's own delimiter row), and a paste/cut inside a cell keeps the "|" delimiters while a
    // pasted line becomes a cell break. Nothing reads or writes the real clipboard: the cut payload
    // is captured through LiveTableCutHook.
    static void RunLiveTableCells(){
        string table="| a | b |\n| --- | --- |\n| c | d |";
        string expected="| c | d |\n| --- | --- |";
        Use(true,delegate{
            liveSource.SetText(table+"\n\ntail");LongLineBench.Idle();
            var document=liveSource.source.Document;var row=document.GetLineByNumber(3);var info=liveSource.livePreview.Info(row);
            int c0=row.Offset+info.Cells[0].Start,e1=row.Offset+info.Cells[1].End;
            DataObject cells=LiveCopy(liveSource,c0,e1-c0);
            Check("table-cells.live-copy",MarkdownClipboard.Get(cells)==expected,"payload="+Show(MarkdownClipboard.Get(cells))+" expected="+Show(expected));
            string unicode=cells.GetDataPresent(DataFormats.UnicodeText)?cells.GetData(DataFormats.UnicodeText) as string:null;
            Check("table-cells.live-unicode",unicode==expected.Replace("\n","\r\n"),"unicode="+Show(unicode));
            DataObject one=LiveCopy(liveSource,c0,1);
            Check("table-cells.live-copy-one-cell",MarkdownClipboard.Get(one)=="c","payload="+Show(MarkdownClipboard.Get(one)));
            // A whole-line selection (triple click, or CutCopyWholeLine's TotalLength) ends exactly
            // where the next row starts: that row holds no selected character and must not become
            // an extra empty row; a selection that ends inside it still keeps its touched cell.
            var headerLine=document.GetLineByNumber(1);var bodyLine=document.GetLineByNumber(3);
            DataObject lineEnd=LiveCopy(liveSource,headerLine.Offset,bodyLine.Offset-headerLine.Offset);
            Check("table-cells.live-copy-row-end",MarkdownClipboard.Get(lineEnd)=="| a | b |\n| --- | --- |","payload="+Show(MarkdownClipboard.Get(lineEnd)));
            DataObject partialRow=LiveCopy(liveSource,headerLine.Offset,bodyLine.Offset+3-headerLine.Offset);
            Check("table-cells.live-copy-row-partial",MarkdownClipboard.Get(partialRow)=="| a | b |\n| --- | --- |\n| c |  |","payload="+Show(MarkdownClipboard.Get(partialRow)));
            // The same boundary at the selection start: the header row ends where this selection
            // begins, so none of its cells are selected and the fragment starts at the body row.
            int rowStart=headerLine.Offset+headerLine.Length;
            DataObject fromRowEnd=LiveCopy(liveSource,rowStart,bodyLine.Offset+bodyLine.Length-rowStart);
            Check("table-cells.live-copy-row-start",MarkdownClipboard.Get(fromRowEnd)=="| c | d |\n| --- | --- |","payload="+Show(MarkdownClipboard.Get(fromRowEnd)));
            liveSource.SetText("| a | b |\n| ---: | --- |\n| c | d |");LongLineBench.Idle();
            document=liveSource.source.Document;row=document.GetLineByNumber(3);info=liveSource.livePreview.Info(row);
            DataObject aligned=LiveCopy(liveSource,row.Offset+info.Cells[0].Start,info.Cells[1].End-info.Cells[0].Start);
            Check("table-cells.live-copy-alignment",MarkdownClipboard.Get(aligned)=="| c | d |\n| ---: | --- |","payload="+Show(MarkdownClipboard.Get(aligned)));
        });
        Use(true,delegate{
            liveSource.SetText(table);LongLineBench.Idle();
            var document=liveSource.source.Document;var row=document.GetLineByNumber(3);var info=liveSource.livePreview.Info(row);
            liveSource.source.Select(row.Offset+info.Cells[0].End,0);LongLineBench.Idle();
            var data=new DataObject();data.SetData(DataFormats.UnicodeText,"x\ny");
            liveSource.PasteData(data,false);liveSource.FlushDocumentChanges();LongLineBench.Idle();
            string got=Canonical(liveSource.text);
            Check("table-cells.live-paste-newline",got=="| a | b |\n| --- | --- |\n| cx<br>y | d |","text="+Show(got));
        });
        Use(true,delegate{
            liveSource.SetText(table);LongLineBench.Idle();
            var document=liveSource.source.Document;var row=document.GetLineByNumber(3);var info=liveSource.livePreview.Info(row);
            liveSource.source.Select(row.Offset+info.Cells[0].End,0);LongLineBench.Idle();
            var data=new DataObject();data.SetData(DataFormats.UnicodeText,"a\tb");
            liveSource.PasteData(data,false);liveSource.FlushDocumentChanges();LongLineBench.Idle();
            string got=Canonical(liveSource.text);
            Check("table-cells.live-paste-tab",got=="| a | b |\n| --- | --- |\n| ca\tb | d |","text="+Show(got));
        });
        Use(true,delegate{
            liveSource.SetText(table);LongLineBench.Idle();
            var document=liveSource.source.Document;var row=document.GetLineByNumber(3);var info=liveSource.livePreview.Info(row);
            liveSource.source.Select(row.Offset+info.Cells[0].Start,info.Cells[1].End-info.Cells[0].Start);LongLineBench.Idle();
            var data=new DataObject();data.SetData(DataFormats.UnicodeText,"Q");
            liveSource.PasteData(data,false);liveSource.FlushDocumentChanges();LongLineBench.Idle();
            string got=Canonical(liveSource.text);
            Check("table-cells.live-paste-over-cells",got=="| a | b |\n| --- | --- |\n| Q |  |","text="+Show(got));
        });
        Use(true,delegate{
            liveSource.SetText(table);LongLineBench.Idle();
            var document=liveSource.source.Document;var row=document.GetLineByNumber(3);var info=liveSource.livePreview.Info(row);
            liveSource.source.Select(row.Offset+info.Cells[0].Start,info.Cells[1].End-info.Cells[0].Start);LongLineBench.Idle();
            DataObject captured=null;
            Action<DataObject> previous=LiveTableCutHook;
            LiveTableCutHook=delegate(DataObject data){captured=data;};
            try{liveSource.source.Cut();}finally{LiveTableCutHook=previous;}
            liveSource.FlushDocumentChanges();LongLineBench.Idle();
            Check("table-cells.live-cut-copy",captured!=null&&MarkdownClipboard.Get(captured)==expected,"payload="+Show(captured==null?null:MarkdownClipboard.Get(captured)));
            string got=Canonical(liveSource.text);
            Check("table-cells.live-cut-keeps-pipes",got=="| a | b |\n| --- | --- |\n|  |  |","text="+Show(got));
            liveSource.Undo();liveSource.FlushDocumentChanges();LongLineBench.Idle();
            Check("table-cells.live-cut-undo",Canonical(liveSource.text)==table,"text="+Show(Canonical(liveSource.text)));
        });
    }
    // #5: a Word/Excel table with merged cells must still become a table: colspan fills its extra
    // columns with empty cells and a rowspan keeps its column empty on the rows it continues into.
    // Print and TXT cut or pad a data row to the header's cell count so the columns never shift.
    static void RunMergedTable(){
        string fragment="<table><tr><th colspan=\"2\">제목</th></tr><tr><td rowspan=\"2\">a</td><td>b</td></tr><tr><td>c</td></tr></table>";
        string expected="| 제목 |  |\n| --- | --- |\n| a | b |\n|  | c |";
        string converted=ClipboardMarkdown("<html><body><!--StartFragment-->"+fragment+"<!--EndFragment--></body></html>");
        Check("merged-table.convert",converted==expected,"got="+Show(converted));
        string pasted=LivePasteAtEnd(liveTarget,RichHtml(fragment,"제목 a b c"));
        Check("merged-table.live-paste",pasted==expected,"got="+Show(pasted));
        string uneven="| a | b |\n| --- | --- |\n| 1 | 2 | 3 |\n| 4 |";
        string shown="a\tb\n1\t2\n4\t";
        string printed=ReadableText(Markdown.Parse(uneven,16,"Malgun Gothic",System.Windows.Media.Brushes.Black,false,""));
        Check("merged-table.print-fits",printed==shown,"printed="+Show(printed));
        string text=MarkdownPlainText.Convert(uneven);
        Check("merged-table.txt-fits",text==shown,"txt="+Show(text));
    }
    // #15: Word list paragraphs (mso-list) become Markdown list items, numbered from the hidden
    // label, nested by the mso-list level, with the "·" marker span dropped.
    static void RunWordList(){
        string fragment="<p class=MsoListParagraphCxSpFirst style='margin-left:36.0pt;mso-list:l0 level1 lfo1'><![if !supportLists]><span style='mso-list:Ignore'>&#183;<span>&nbsp;&nbsp;</span></span><![endif]>첫째 항목</p>\r\n"
            +"<p class=MsoListParagraphCxSpMiddle style='margin-left:72.0pt;mso-list:l0 level2 lfo1'><![if !supportLists]><span style='mso-list:Ignore'>o<span>&nbsp;</span></span><![endif]>둘째 단계</p>\r\n"
            +"<p class=MsoListParagraphCxSpLast style='mso-list:l1 level1 lfo2'><![if !supportLists]><span style='mso-list:Ignore'>1.<span>&nbsp;</span></span><![endif]>번호 항목</p>";
        string expected="- 첫째 항목\n  - 둘째 단계\n1. 번호 항목";
        string converted=ClipboardMarkdown("<html><body><!--StartFragment-->"+fragment+"<!--EndFragment--></body></html>");
        Check("word-list.convert",converted==expected,"got="+Show(converted));
        string pasted=LivePasteAtEnd(liveTarget,RichHtml(fragment,"첫째 항목 둘째 단계 번호 항목"));
        Check("word-list.live-paste",pasted==expected,"got="+Show(pasted));
    }
    // #4: the escapes the rich paste writes ("\[", "\_", "\*", "\\") show the literal character in
    // print and in TXT, and an RTF paste writes a literal backslash as "\\" so it round-trips.
    static void RunPasteEscapes(){
        string fragment="<p>see [1] and a_b *c* C:\\path</p>";
        string expected="see \\[1\\] and a\\_b \\*c\\* C:\\\\path";
        string pasted=LivePasteAtEnd(liveTarget,RichHtml(fragment,"see [1] and a_b *c* C:\\path"));
        Check("escapes.source",pasted==expected,"got="+Show(pasted));
        string shown="see [1] and a_b *c* C:\\path";
        string printed=ReadableText(Markdown.Parse(expected,16,"Malgun Gothic",System.Windows.Media.Brushes.Black,false,""));
        Check("escapes.print",printed==shown,"printed="+Show(printed));
        string text=MarkdownPlainText.Convert(expected);
        Check("escapes.txt",text==shown,"txt="+Show(text));
        string rtf="{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Malgun Gothic;}}\\f0\\fs24 C:\\\\path \\\\[x\\\\]}";
        string expectedRtf="C:\\\\path \\\\&#91;x\\\\&#93;";
        string rtfPasted=LivePasteAtEnd(liveTarget,RichRtf(rtf,"C:\\path \\[x\\]"));
        Check("escapes.rtf-source",rtfPasted==expectedRtf,"got="+Show(rtfPasted));
        string rtfShown="C:\\path \\[x\\]";
        Check("escapes.rtf-print",ReadableText(Markdown.Parse(rtfPasted,16,"Malgun Gothic",System.Windows.Media.Brushes.Black,false,""))==rtfShown,"printed="+Show(ReadableText(Markdown.Parse(rtfPasted,16,"Malgun Gothic",System.Windows.Media.Brushes.Black,false,""))));
        Check("escapes.rtf-txt",MarkdownPlainText.Convert(rtfPasted)==rtfShown,"txt="+Show(MarkdownPlainText.Convert(rtfPasted)));
    }
    // Raw source mode ("원문 편집": the preview detached) copies the literal source, so a selection
    // that starts at a line's first character keeps its list/quote/heading marker without any
    // preview data.
    static void RunRawCopy(){
        string sample="- 항목\n> 인용\n## 제목";
        SetLive(liveSource,sample);
        liveSource.loading=true;liveSource.preview.IsChecked=true;liveSource.loading=false;liveSource.SwitchSurface();LongLineBench.Idle();
        Check("raw-copy.raw-mode",liveSource.CodeMode&&liveSource.editing&&!liveSource.LivePreviewAttached,"code="+liveSource.CodeMode+", editing="+liveSource.editing+", attached="+liveSource.LivePreviewAttached);
        string[] expected={"- 항목","> 인용","## 제목"};
        for(int i=0;i<expected.Length;i++){
            var line=liveSource.source.Document.GetLineByNumber(i+1);
            DataObject data=LiveCopy(liveSource,line.Offset,line.Length);
            Check("raw-copy.line-"+(i+1),MarkdownClipboard.Get(data)==expected[i],"payload="+Show(MarkdownClipboard.Get(data))+" expected="+Show(expected[i]));
        }
    }
}
}}
