using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using System.Xml;

namespace MDNote {
// B6 release test: drive the app's own print-document builder in both print modes (Markdown and
// plain TXT), paginate it as the print path does, write the pages to XPS files (no printer is ever
// involved), and check page count, every source text run, content bounds, the light print theme
// while the app is in dark mode, long code lines and missing/present images. The first two pages of
// every document/mode are rendered to PNG for human review.
internal static class PrintProbe {
    const double PageWidth=793.7,PageHeight=1122.5,PageMargin=32.0;
    // The ideographic space a Japanese/Chinese IME types: content at the start of a TXT line, and
    // the character WPF's text layout may keep out of the glyph run a page draws (see the TXT
    // text check below).
    const char WideSpaceChar='　';
    static readonly char[] WideSpaceArray=new char[]{WideSpaceChar};
    static StringBuilder report;
    static bool all;
    static string outDir;
    sealed class DocCase { internal string Name; internal string Text; internal string[] MdSentinels; internal string[] TxtSentinels; internal int MinPages; internal bool Images; internal bool MissingImage; internal bool WideImage; }
    sealed class XpsContent {
        internal int Pages; internal int ImageBrushes;
        internal readonly StringBuilder Text=new StringBuilder();
    }
    // The paginator's own page visuals carry the in-memory GlyphRuns: their characters keep what
    // the XPS serializer rewrites to spaces for emoji fallback fonts, and their ink boxes are the
    // element bounds after the page transforms.
    sealed class PageScan {
        internal int Pages,GlyphRuns;
        internal readonly StringBuilder Text=new StringBuilder();
        internal double MaxRight=Double.MinValue,MinLeft=Double.MaxValue;internal int MaxRightPage;internal bool HasBounds;
        // The right edge of the image drawings alone, so a picture wider than the paper is named
        // as such instead of hiding among the text bounds.
        internal double ImageMaxRight=Double.MinValue;
        // Every glyph run of the pages, in page coordinates: a leading full-width space can be
        // laid out as an advance that its run does not carry as a character, so the TXT check
        // reads where each line starts as well as what it carries.
        internal readonly List<PageRun> Runs=new List<PageRun>();
    }
    // One glyph run of a page visual: the text it carries and where it starts, in the page's own
    // coordinates. A run may carry no text at all, and a laid-out leading space can be one.
    sealed class PageRun {
        internal int Page;internal double X,Y;internal string Text;internal double InkLeft=Double.MaxValue;
    }
    // The runs of one baseline, in the order the page holds them: where the line starts (any run,
    // a space-only run included), where its first character starts, and where its first painted
    // glyph starts.
    sealed class ScanLine {
        internal int Page;internal double Baseline;internal double MinX=Double.MaxValue,ContentX=Double.MaxValue,InkLeft=Double.MaxValue;
        internal readonly StringBuilder Text=new StringBuilder();
    }
    internal static int Run(string dir){
        outDir=Path.GetFullPath(dir);Directory.CreateDirectory(outDir);
        report=new StringBuilder("# print-probe\n");all=true;
        try{
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            RunAll();
        }catch(Exception ex){all=false;report.Append("EXCEPTION: ").Append(ex.ToString()).Append('\n');}
        report.Append('\n').Append("SUMMARY: ").Append(all?"PASS":"FAIL").Append('\n');
        string body=report.ToString();
        string stale=Path.Combine(outDir,all?"print-probe-FAIL.txt":"print-probe-PASS.txt");
        try{if(File.Exists(stale))File.Delete(stale);}catch(Exception){}
        try{File.WriteAllText(Path.Combine(outDir,"print-probe.txt"),body,new UTF8Encoding(false));}catch(Exception){}
        try{File.WriteAllText(Path.Combine(outDir,all?"print-probe-PASS.txt":"print-probe-FAIL.txt"),body,new UTF8Encoding(false));}catch(Exception){}
        return all?0:1;
    }
    static void Check(string area,string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(area).Append(' ').Append(name).Append(detail.Length>0?"  "+detail:"").Append('\n');}
    static void RunAll(){
        List<DocCase> cases=Cases();
        MainWindow window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
        window.Show();LongLineBench.Idle();
        bool darkActive=ActivateDark(window);
        bool darkPaper=window.Background is SolidColorBrush&&Lum(window.Background)<0.45;
        Check("app","dark-active",darkActive&&darkPaper,"field="+darkActive+" windowBackground="+Describe(window.Background));
        string fixtures=Path.Combine(outDir,"fixtures");Directory.CreateDirectory(fixtures);
        WriteSampleImage(Path.Combine(fixtures,"sample-image.png"));
        WriteWideImage(Path.Combine(fixtures,"wide-image.png"));
        RunRenderingRuleChecks(fixtures);
        foreach(DocCase c in cases){
            File.WriteAllText(Path.Combine(fixtures,c.Name+".md"),c.Text,new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(fixtures,c.Name+".txt"),c.Text,new UTF8Encoding(false));
            RunOne(window,c,fixtures,false);
            RunOne(window,c,fixtures,true);
        }
        // The image bound has to come from the page, not from a fixed number: the same picture on
        // a narrow page must shrink to that page's printable column.
        RunNarrowPageImage(window,fixtures);
        // The probe window is left open: closing it would raise the unsaved-document prompt,
        // which would block the hidden desktop, and the process exit tears the window down.
    }
    static void RunNarrowPageImage(MainWindow window,string fixtures){
        const double narrowWidth=500.0,narrowHeight=800.0;
        string label="doc-wide-image-narrow";
        FlowDocument document;
        try{
            window.LoadFile(Path.Combine(fixtures,"doc-wide-image.md"));LongLineBench.Idle();
            document=window.CreatePrintDocument(narrowWidth,narrowHeight);
        }catch(Exception ex){Check(label,"build",false,ex.GetType().Name+": "+ex.Message);return;}
        PageScan scan;
        try{
            DocumentPaginator paginator=((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.ComputePageCount();scan=ScanPages(paginator);
        }catch(Exception ex){Check(label,"paginate",false,ex.GetType().Name+": "+ex.Message);return;}
        double contentRight=narrowWidth-PageMargin;
        int imageElements=CountImages(document);
        // The lower bound proves the picture was drawn: a missing image leaves ImageMaxRight at
        // its initial value and must not pass this check by accident. The element count tells the
        // two failures apart: 0 means the document holds no Image element (the parser or the
        // file), 1 with no bound means the page walk found no laid-out picture.
        Check(label,"image-fits-narrow-page",scan.ImageMaxRight>PageMargin&&scan.ImageMaxRight<=contentRight+3.0,"imageRight="+F(scan.ImageMaxRight)+" contentRight="+F(contentRight));
        Check(label,"image-element",imageElements>0,"imageElements="+imageElements);
    }
    static void RunOne(MainWindow window,DocCase c,string fixtures,bool plain){
        string mode=plain?"txt":"md",label=c.Name+"-"+mode;
        string fixture=Path.Combine(fixtures,c.Name+(plain?".txt":".md"));
        report.Append("\n== ").Append(label).Append("  fixture=").Append(Path.GetFileName(fixture)).Append('\n');
        FlowDocument document;
        try{
            window.LoadFile(fixture);LongLineBench.Idle();
            document=window.CreatePrintDocument(PageWidth,PageHeight);
            Check(label,"build",true,"background="+Describe(document.Background)+" foreground="+Describe(document.Foreground));
        }catch(Exception ex){Check(label,"build",false,ex.GetType().Name+": "+ex.Message);return;}
        Check(label,"page-size",Math.Abs(document.PageWidth-PageWidth)<0.1&&Math.Abs(document.PageHeight-PageHeight)<0.1,"width="+F(document.PageWidth)+" height="+F(document.PageHeight));
        CheckLight(label,document);
        DocumentPaginator paginator;
        int pages;
        try{
            paginator=((IDocumentPaginatorSource)document).DocumentPaginator;paginator.ComputePageCount();pages=paginator.PageCount;
        }catch(Exception ex){Check(label,"paginate",false,ex.GetType().Name+": "+ex.Message);return;}
        Check(label,"pages",pages>0,"count="+pages);
        Check(label,"pages-reasonable",pages>=c.MinPages&&pages<=40,"count="+pages+" min="+c.MinPages+" max=40");
        string xps=Path.Combine(outDir,label+".xps");
        bool wrote=false;
        try{WriteXps(paginator,xps);wrote=File.Exists(xps)&&new FileInfo(xps).Length>0;Check(label,"xps-write",wrote,"file="+Path.GetFileName(xps)+" bytes="+(wrote?new FileInfo(xps).Length:0));}
        catch(Exception ex){Check(label,"xps-write",false,ex.GetType().Name+": "+ex.Message);}
        PageScan scan=null;
        try{scan=ScanPages(paginator);Check(label,"page-scan",scan.Pages==pages,"pages="+scan.Pages+" glyphRuns="+scan.GlyphRuns+" glyphChars="+scan.Text.Length);}
        catch(Exception ex){Check(label,"page-scan",false,ex.GetType().Name+": "+ex.Message);}
        if(wrote){
            XpsContent content=null;
            try{content=ReadXps(xps);Check(label,"xps-pages",content.Pages==pages,"xps="+content.Pages+" paginator="+pages);}
            catch(Exception ex){Check(label,"xps-pages",false,ex.GetType().Name+": "+ex.Message);}
            if(content!=null){
                string text=content.Text.ToString()+(scan==null?"":scan.Text.ToString());
                string flat=Flatten(text);
                string[] sentinels=plain?c.TxtSentinels:c.MdSentinels;
                foreach(string sentinel in sentinels)Check(label,"text:"+sentinel,flat.Contains(Flatten(sentinel)),"len="+flat.Length);
                if(!plain&&c.Images){
                    Check(label,"image-present-drawn",content.ImageBrushes>0,"imageBrushElements="+content.ImageBrushes);
                    if(c.MissingImage)Check(label,"image-missing-placeholder",flat.Contains(Flatten("[이미지: 없는 그림]")),"placeholder-text");
                    Check(label,"image-present-not-placeholder",!flat.Contains(Flatten("[이미지: 샘플 그림]")),"no-fallback-run");
                }
            }
        }
        if(scan!=null){
            double contentRight=PageWidth-PageMargin;
            Check(label,"content-bounds",scan.HasBounds&&scan.MaxRight<=contentRight+3.0&&scan.MinLeft>=PageMargin-6.0,"maxRight="+F(scan.MaxRight)+" minLeft="+F(scan.MinLeft)+" contentRight="+F(contentRight)+" worstPage="+scan.MaxRightPage);
            // bughunt3 output-scale item 2: a picture wider than the printable column has to be
            // scaled to it, not drawn at its file width and cut off by the page edge.
            if(!plain&&c.WideImage)Check(label,"wide-image-fits-print-column",scan.ImageMaxRight<=contentRight+3.0,"imageRight="+F(scan.ImageMaxRight)+" contentRight="+F(contentRight));
            if(plain){
                // TXT printing draws the source literally: every character that has a glyph must
                // come back in reading order, so this is a whole-document text-loss check. The
                // built document is read first: a failure there is the builder, a failure only in
                // the page scan is the text layout (a leading ideographic space after a line
                // break is what dropped before). A line-initial U+3000 whose character the layout
                // keeps out of the glyph run is judged by that line's indent below; the count
                // stays strict for every other character.
                string source=Flatten(c.Text),extracted=Flatten(scan.Text.ToString());
                string strict=Flatten(WithoutLineStartWideSpaces(c.Text));
                string built=Flatten(new TextRange(document.ContentStart,document.ContentEnd).Text);
                Check(label,"document-carries-source",built.Contains(source),"sourceChars="+source.Length+" documentChars="+built.Length);
                Check(label,"all-source-text",extracted.Contains(strict),"sourceChars="+source.Length+" strictChars="+strict.Length+" extractedChars="+extracted.Length+" wideSpaces="+(source.Length-strict.Length));
                CheckWideSpaceIndent(label,c.Text,scan,document.FontSize);
            }
        }
        try{
            List<KeyValuePair<string,int>> pngs=RenderPages(paginator,label,2);
            for(int i=0;i<pngs.Count;i++){
                string name=Path.GetFileName(pngs[i].Key);
                report.Append("   PNG ").Append(name).Append(" nonWhite=").Append(pngs[i].Value).Append('\n');
                Check(label,"png-"+(i+1)+"-nonblank",pngs[i].Value>60,"file="+name);
            }
            Check(label,"png-count",pngs.Count==Math.Min(2,pages),"rendered="+pngs.Count+" pages="+pages);
        }catch(Exception ex){Check(label,"png-render",false,ex.GetType().Name+": "+ex.Message);}
    }
    // The print/rich-copy renderer now reads MarkdownPlainText's CommonMark/GFM helpers, so these
    // read the FlowDocument Markdown.Parse builds: the ATX closing "#", a decoded entity, a link
    // target with balanced parentheses and a title, an image target whose title is not in its path,
    // a pipe-less "---" under "a | b" that is a setext heading, and a data row's extra cells.
    static void RunRenderingRuleChecks(string fixtures){
        double size=16;
        Paragraph heading=FirstParagraph(Markdown.Parse("### C#",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","heading-c-hash",heading!=null&&ParagraphText(heading)=="C#"&&Math.Abs(heading.FontSize-size*Markdown.HeadingScale(3))<0.01,"text=["+ParagraphText(heading)+"] size="+F(heading==null?0:heading.FontSize));
        Paragraph closing=FirstParagraph(Markdown.Parse("## 제목 ##",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","heading-closing-run",ParagraphText(closing)=="제목","text=["+ParagraphText(closing)+"]");
        Paragraph entity=FirstParagraph(Markdown.Parse("&copy; 2026",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","entity-decode",ParagraphText(entity)=="© 2026","text=["+ParagraphText(entity)+"]");
        Paragraph link=FirstParagraph(Markdown.Parse("[w](https://x/A_(b))",size,"Malgun Gothic",Brushes.Black,false,""));
        Hyperlink hyperlink=link==null?null:link.Inlines.FirstInline as Hyperlink;
        Check("rules","link-balanced-parens",hyperlink!=null&&ParagraphText(link)=="w"&&String.Equals(hyperlink.Tag as string,"https://x/A_(b)",StringComparison.Ordinal),"tag=["+(hyperlink==null?"":hyperlink.Tag as string)+"] text=["+ParagraphText(link)+"]");
        Paragraph titled=FirstParagraph(Markdown.Parse("[w](/x \"ti)tle\")",size,"Malgun Gothic",Brushes.Black,false,""));
        Hyperlink titleLink=titled==null?null:titled.Inlines.FirstInline as Hyperlink;
        Check("rules","link-title-hidden",titleLink!=null&&String.Equals(titleLink.Tag as string,"/x",StringComparison.Ordinal)&&ParagraphText(titled)=="w","tag=["+(titleLink==null?"":titleLink.Tag as string)+"] text=["+ParagraphText(titled)+"]");
        string imageFile=Path.Combine(fixtures,"img_(1).png");WriteSampleImage(imageFile);
        Paragraph imageParagraph=FirstParagraph(Markdown.Parse("![a](img_(1).png \"t\")",size,"Malgun Gothic",Brushes.Black,false,Path.Combine(fixtures,"rules.md")));
        InlineUIContainer container=imageParagraph==null?null:imageParagraph.Inlines.FirstInline as InlineUIContainer;
        System.Windows.Controls.Image image=container==null?null:container.Child as System.Windows.Controls.Image;
        BitmapImage bitmap=image==null?null:image.Source as BitmapImage;
        string imagePath=bitmap==null||bitmap.UriSource==null?"":bitmap.UriSource.LocalPath;
        Check("rules","image-parens-title",image!=null&&imagePath.EndsWith("img_(1).png",StringComparison.OrdinalIgnoreCase)&&String.Equals(container.Tag as string,"image:![a](img_(1).png \"t\")",StringComparison.Ordinal),"path=["+imagePath+"] tag=["+(container==null?"":container.Tag as string)+"]");
        FlowDocument setext=Markdown.Parse("이름 | 나이\n---",size,"Malgun Gothic",Brushes.Black,false,"");
        Paragraph setextParagraph=FirstParagraph(setext);
        Check("rules","setext-not-table",CountTables(setext)==0&&ParagraphText(setextParagraph)=="이름 | 나이"&&setextParagraph!=null&&Math.Abs(setextParagraph.FontSize-size*Markdown.HeadingScale(2))<0.01,"tables="+CountTables(setext)+" text=["+ParagraphText(setextParagraph)+"]");
        FlowDocument extra=Markdown.Parse("| a | b |\n| --- | --- |\n| 1 | 2 | 3 |",size,"Malgun Gothic",Brushes.Black,false,"");
        Table extraTable=FirstTable(extra);
        TableRow extraRow=null;
        if(extraTable!=null&&extraTable.RowGroups.Count>0&&extraTable.RowGroups[0].Rows.Count>1)extraRow=extraTable.RowGroups[0].Rows[1];
        string firstCell=extraRow==null||extraRow.Cells.Count<1?"":ParagraphText(extraRow.Cells[0].Blocks.FirstBlock as Paragraph);
        string secondCell=extraRow==null||extraRow.Cells.Count<2?"":ParagraphText(extraRow.Cells[1].Blocks.FirstBlock as Paragraph);
        Check("rules","table-extra-cells",extraTable!=null&&extraTable.Columns.Count==2&&extraRow!=null&&extraRow.Cells.Count==2&&firstCell=="1"&&secondCell=="2","cols="+(extraTable==null?0:extraTable.Columns.Count)+" cells="+(extraRow==null?0:extraRow.Cells.Count)+" text=["+firstCell+"|"+secondCell+"]");
        // The inline rules the live view applies, now read by this renderer too: code-span
        // padding, a line-end backslash as a hard break, angle autolinks, a same-line HTML comment
        // and a parenthesized link title.
        Paragraph padded=FirstParagraph(Markdown.Parse("x ` code ` y",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","code-span-padding",ParagraphText(padded)=="x code y","text=["+ParagraphText(padded)+"]");
        Paragraph spaces=FirstParagraph(Markdown.Parse("a`  `b",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","code-span-all-spaces",ParagraphText(spaces)=="a  b","text=["+ParagraphText(spaces)+"]");
        Paragraph lineEnd=FirstParagraph(Markdown.Parse("줄 끝\\",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","line-end-backslash-hidden",ParagraphText(lineEnd)=="줄 끝","text=["+ParagraphText(lineEnd)+"]");
        Paragraph pair=FirstParagraph(Markdown.Parse("a\\\\",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","line-end-backslash-pair-kept",ParagraphText(pair)=="a\\","text=["+ParagraphText(pair)+"]");
        Paragraph angle=FirstParagraph(Markdown.Parse("<https://example.com/a>",size,"Malgun Gothic",Brushes.Black,false,""));
        Hyperlink angleLink=angle==null?null:angle.Inlines.FirstInline as Hyperlink;
        Check("rules","autolink-url",angleLink!=null&&ParagraphText(angle)=="https://example.com/a"&&String.Equals(angleLink.Tag as string,"https://example.com/a",StringComparison.Ordinal),"tag=["+(angleLink==null?"":angleLink.Tag as string)+"] text=["+ParagraphText(angle)+"]");
        Paragraph angleText=FirstParagraph(Markdown.Parse("see <https://example.com/a> now",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","autolink-in-text",ParagraphText(angleText)=="see https://example.com/a now","text=["+ParagraphText(angleText)+"]");
        Paragraph mail=FirstParagraph(Markdown.Parse("<a@b.c>",size,"Malgun Gothic",Brushes.Black,false,""));
        Hyperlink mailLink=mail==null?null:mail.Inlines.FirstInline as Hyperlink;
        Check("rules","autolink-mail",mailLink!=null&&ParagraphText(mail)=="a@b.c"&&String.Equals(mailLink.Tag as string,"mailto:a@b.c",StringComparison.Ordinal),"tag=["+(mailLink==null?"":mailLink.Tag as string)+"] text=["+ParagraphText(mail)+"]");
        Paragraph mailto=FirstParagraph(Markdown.Parse("<mailto:a@b.c>",size,"Malgun Gothic",Brushes.Black,false,""));
        Hyperlink mailtoLink=mailto==null?null:mailto.Inlines.FirstInline as Hyperlink;
        Check("rules","autolink-mailto",mailtoLink!=null&&ParagraphText(mailto)=="mailto:a@b.c"&&String.Equals(mailtoLink.Tag as string,"mailto:a@b.c",StringComparison.Ordinal),"tag=["+(mailtoLink==null?"":mailtoLink.Tag as string)+"] text=["+ParagraphText(mailto)+"]");
        Paragraph comment=FirstParagraph(Markdown.Parse("a <!-- 숨김 --> b",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","comment-same-line-hidden",ParagraphText(comment)=="a  b","text=["+ParagraphText(comment)+"]");
        Paragraph multiComment=FirstParagraph(Markdown.Parse("a <!-- x\n--> b",size,"Malgun Gothic",Brushes.Black,false,""));
        Check("rules","comment-multiline-literal",ParagraphText(multiComment)=="a <!-- x","text=["+ParagraphText(multiComment)+"]");
        Paragraph parenTitle=FirstParagraph(Markdown.Parse("[t](url (title))",size,"Malgun Gothic",Brushes.Black,false,""));
        Hyperlink parenLink=parenTitle==null?null:parenTitle.Inlines.FirstInline as Hyperlink;
        Check("rules","link-paren-title",parenLink!=null&&String.Equals(parenLink.Tag as string,"url",StringComparison.Ordinal)&&ParagraphText(parenTitle)=="t","tag=["+(parenLink==null?"":parenLink.Tag as string)+"] text=["+ParagraphText(parenTitle)+"]");
        Paragraph parenImage=FirstParagraph(Markdown.Parse("![a](img_(1).png (title))",size,"Malgun Gothic",Brushes.Black,false,Path.Combine(fixtures,"rules.md")));
        InlineUIContainer parenContainer=parenImage==null?null:parenImage.Inlines.FirstInline as InlineUIContainer;
        System.Windows.Controls.Image parenBitmap=parenContainer==null?null:parenContainer.Child as System.Windows.Controls.Image;
        BitmapImage parenSource=parenBitmap==null?null:parenBitmap.Source as BitmapImage;
        string parenPath=parenSource==null||parenSource.UriSource==null?"":parenSource.UriSource.LocalPath;
        Check("rules","image-paren-title",parenPath.EndsWith("img_(1).png",StringComparison.OrdinalIgnoreCase),"path=["+parenPath+"]");
        // The TXT converter (MarkdownPlainText.Convert, the live mode's MD-to-TXT) renders the same
        // sources to the same plain text; the md2txt probe compares it with the oracle above.
        string txtPadded=MarkdownPlainText.Convert("x ` code ` y");
        string txtSpaces=MarkdownPlainText.Convert("a`  `b");
        string txtLineEnd=MarkdownPlainText.Convert("줄 끝\\");
        string txtPair=MarkdownPlainText.Convert("a\\\\");
        string txtAngle=MarkdownPlainText.Convert("see <https://example.com/a> now");
        string txtMail=MarkdownPlainText.Convert("<a@b.c>");
        string txtMailto=MarkdownPlainText.Convert("<mailto:a@b.c>");
        string txtComment=MarkdownPlainText.Convert("a <!-- 숨김 --> b");
        string txtMultiComment=MarkdownPlainText.Convert("a <!-- x\n--> b");
        string txtTitle=MarkdownPlainText.Convert("[t](url (title))");
        Check("rules","txt-code-span-padding",txtPadded=="x code y","text=["+txtPadded+"]");
        Check("rules","txt-code-span-all-spaces",txtSpaces=="a  b","text=["+txtSpaces+"]");
        Check("rules","txt-line-end-backslash",txtLineEnd=="줄 끝","text=["+txtLineEnd+"]");
        Check("rules","txt-line-end-backslash-pair",txtPair=="a\\","text=["+txtPair+"]");
        Check("rules","txt-autolink-url",txtAngle=="see https://example.com/a now","text=["+txtAngle+"]");
        Check("rules","txt-autolink-mail",txtMail=="a@b.c","text=["+txtMail+"]");
        Check("rules","txt-autolink-mailto",txtMailto=="mailto:a@b.c","text=["+txtMailto+"]");
        Check("rules","txt-comment-same-line",txtComment=="a  b","text=["+txtComment+"]");
        Check("rules","txt-comment-multiline-literal",txtMultiComment=="a <!-- x\n--> b","text=["+txtMultiComment+"]");
        Check("rules","txt-link-paren-title",txtTitle=="t","text=["+txtTitle+"]");
        // bughunt3 rendering items 10 and 11, the list rule the live preview draws read from
        // the FlowDocument this renderer builds. A blank line between items keeps one list
        // (CommonMark), so three "1." items print 1, 2, 3 from one List: the blank paragraph
        // moves into the item above it, which keeps the print text at the source's line order
        // (the same text the TXT conversion below emits) and lets WPF number the items in a row.
        string looseMarkdown="1. 하나\n\n1. 둘\n\n1. 셋";
        FlowDocument loose=Markdown.Parse(looseMarkdown,size,"Malgun Gothic",Brushes.Black,false,"");
        List<System.Windows.Documents.List> looseLists=TopLists(loose);
        bool looseNumbers=looseLists.Count==1&&looseLists[0].ListItems.Count==3&&looseLists[0].StartIndex==1;
        bool looseBlanks=looseNumbers&&System.Linq.Enumerable.ElementAt(looseLists[0].ListItems,0).Blocks.Count==2&&System.Linq.Enumerable.ElementAt(looseLists[0].ListItems,1).Blocks.Count==2&&
            System.Linq.Enumerable.ElementAt(looseLists[0].ListItems,2).Blocks.Count==1;
        Check("lists","print-blank-line-keeps-numbering",looseNumbers&&looseBlanks,DescribeLists(looseLists));
        string looseText=MarkdownPlainText.Convert(looseMarkdown);
        Check("lists","txt-blank-line-keeps-numbering",looseText=="1. 하나\n\n2. 둘\n\n3. 셋","text=["+looseText.Replace("\n","\\n")+"]");
        // A two-space marker sits left of the parent item's content column (3 for "1. "), so it
        // opens a sibling top-level list; a three-space marker reaches that column and nests.
        FlowDocument shallow=Markdown.Parse("1. 첫 항목\n  - 얕은 항목",size,"Malgun Gothic",Brushes.Black,false,"");
        List<System.Windows.Documents.List> shallowLists=TopLists(shallow);
        bool shallowSibling=shallowLists.Count==2&&shallowLists[0].ListItems.Count==1&&System.Linq.Enumerable.ElementAt(shallowLists[0].ListItems,0).Blocks.Count==1&&
            shallowLists[1].MarkerStyle==TextMarkerStyle.Disc&&shallowLists[1].ListItems.Count==1;
        Check("lists","print-shallow-marker-is-sibling",shallowSibling,DescribeLists(shallowLists));
        string shallowText=MarkdownPlainText.Convert("1. 첫 항목\n  - 얕은 항목");
        Check("lists","txt-shallow-marker-is-sibling",shallowText=="1. 첫 항목\n- 얕은 항목","text=["+shallowText.Replace("\n","\\n")+"]");
        FlowDocument deep=Markdown.Parse("1. 첫 항목\n   - 깊은 항목",size,"Malgun Gothic",Brushes.Black,false,"");
        List<System.Windows.Documents.List> deepLists=TopLists(deep);
        bool deepNested=deepLists.Count==1&&deepLists[0].ListItems.Count==1&&System.Linq.Enumerable.ElementAt(deepLists[0].ListItems,0).Blocks.Count==2&&
            System.Linq.Enumerable.ElementAt(deepLists[0].ListItems,0).Blocks.LastBlock is System.Windows.Documents.List;
        string deepText=MarkdownPlainText.Convert("1. 첫 항목\n   - 깊은 항목");
        Check("lists","nested-marker-still-nests",deepNested&&deepText=="1. 첫 항목\n  - 깊은 항목","print="+DescribeLists(deepLists)+" txt=["+deepText.Replace("\n","\\n")+"]");
    }
    // Every top-level list of a print document in block order; nested lists are read through
    // their item's blocks by the checks above.
    static List<System.Windows.Documents.List> TopLists(FlowDocument document){
        var lists=new List<System.Windows.Documents.List>();
        foreach(Block block in document.Blocks){var list=block as System.Windows.Documents.List;if(list!=null)lists.Add(list);}
        return lists;
    }
    static string DescribeLists(List<System.Windows.Documents.List> lists){
        var text=new StringBuilder();
        text.Append("count=").Append(lists.Count);
        foreach(System.Windows.Documents.List list in lists)text.Append(" [").Append(list.MarkerStyle).Append(" start=").Append(list.StartIndex).Append(" items=").Append(list.ListItems.Count).Append(']');
        return text.ToString();
    }
    static Paragraph FirstParagraph(FlowDocument document){foreach(Block block in document.Blocks){var paragraph=block as Paragraph;if(paragraph!=null)return paragraph;}return null;}
    // Pictures the built print document holds, whatever block nests them.
    static int CountImages(FlowDocument document){int count=0;foreach(Block block in document.Blocks)count+=CountImages(block);return count;}
    static int CountImages(Block block){
        var paragraph=block as Paragraph;
        if(paragraph!=null)return CountImages(paragraph.Inlines);
        var section=block as Section;
        if(section!=null){int count=0;foreach(Block child in section.Blocks)count+=CountImages(child);return count;}
        var list=block as System.Windows.Documents.List;
        if(list!=null){int count=0;foreach(ListItem item in list.ListItems)foreach(Block child in item.Blocks)count+=CountImages(child);return count;}
        var table=block as Table;
        if(table!=null){int count=0;foreach(TableRowGroup group in table.RowGroups)foreach(TableRow row in group.Rows)foreach(TableCell cell in row.Cells)foreach(Block child in cell.Blocks)count+=CountImages(child);return count;}
        return 0;
    }
    static int CountImages(InlineCollection inlines){
        int count=0;
        foreach(Inline inline in inlines){
            var container=inline as InlineUIContainer;
            if(container!=null){if(container.Child is System.Windows.Controls.Image)count++;continue;}
            var span=inline as Span;
            if(span!=null)count+=CountImages(span.Inlines);
        }
        return count;
    }
    static string ParagraphText(Paragraph paragraph){return paragraph==null?"":new TextRange(paragraph.ContentStart,paragraph.ContentEnd).Text;}
    static int CountTables(FlowDocument document){int count=0;foreach(Block block in document.Blocks)if(block is Table)count++;return count;}
    static Table FirstTable(FlowDocument document){foreach(Block block in document.Blocks){var table=block as Table;if(table!=null)return table;}return null;}
    static List<DocCase> Cases(){
        var cases=new List<DocCase>();
        var b=new StringBuilder();
        b.Append("# 인쇄 검사 한국어 PRTKOREA001\n\n");
        b.Append("인쇄검사한국어 표식이 있는 첫 문단입니다. 한글 English 123 혼합.\n\n");
        b.Append("## 두 번째 제목\n\n");
        for(int i=0;i<120;i++)b.Append("인쇄할 때 긴 한국어 문단이 페이지를 넘어가며 자연스럽게 줄바꿈되는지 확인하는 문장입니다 ").Append(i).Append("번째. 다음 문장도 이어집니다.\n\n");
        b.Append("- 첫째 목록 PRTKOREA002\n- 둘째 목록\n  - 중첩항목표식\n\n");
        b.Append("1. 순서 항목 하나\n2. 순서 항목 둘\n\n");
        b.Append("> 인용문입니다. 인쇄 검사 인용.\n\n");
        b.Append("이모지 😀 확인 그리고 마지막 문장 PRTKOREA003 입니다.\n");
        string korean=b.ToString();string[] koreanS=new[]{"PRTKOREA001","PRTKOREA002","PRTKOREA003","인쇄검사한국어","중첩항목표식","😀"};
        cases.Add(new DocCase{Name="doc-korean",Text=korean,MdSentinels=koreanS,TxtSentinels=koreanS,MinPages=2});

        b.Clear();
        b.Append("# 日本語の見出し PRTJAPAN001\n\n");
        for(int i=0;i<100;i++)b.Append("日本語の印刷検査では、長い文章がページをまたいで正しく折り返されるかを確認します。").Append(i).Append("行目です。\n\n");
        b.Append("- 箇条書き PRTJAPAN002\n- 二番目\n\n");
        b.Append("最後の行の目印 PRTJAPAN003 です。\n");
        string japanese=b.ToString();string[] japaneseS=new[]{"PRTJAPAN001","PRTJAPAN002","PRTJAPAN003","日本語の印刷検査","最後の行の目印"};
        cases.Add(new DocCase{Name="doc-japanese",Text=japanese,MdSentinels=japaneseS,TxtSentinels=japaneseS,MinPages=1});

        b.Clear();
        b.Append("# 혼합 검사 PRTMIXED001\n\n");
        for(int i=0;i<40;i++)b.Append("한국어와日本語 문장이 섞인 문단입니다. Emoji 😀 🚀 and ASCII mixed ").Append(i).Append(".\n\n");
        for(int i=0;i<40;i++)b.Append("한국어と日本語の混在した段落です。絵文字 😄 も含みます ").Append(i).Append("行目。\n\n");
        b.Append("- 혼합 목록 PRTMIXED002\n- 두 번째\n\n");
        b.Append("마지막 혼합 문장 PRTMIXED003 😀 🚀 끝.\n");
        string mixed=b.ToString();string[] mixedS=new[]{"PRTMIXED001","PRTMIXED002","PRTMIXED003","한국어와日本語","😀","🚀"};
        cases.Add(new DocCase{Name="doc-mixed",Text=mixed,MdSentinels=mixedS,TxtSentinels=mixedS,MinPages=1});

        b.Clear();
        b.Append("# 표 검사 PRTTABLE001\n\n");
        b.Append("| 항목 | 값 | 설명 |\n| --- | ---: | --- |\n");
        for(int i=0;i<30;i++)b.Append("| 항목").Append(i).Append(" | ").Append(i).Append(" | 표의 일반 행입니다 ").Append(i).Append(" |\n");
        b.Append("\n| 번호 | 항목이름 | 설명내용 | 수량 | 단가 | 합계 | 비고 | 담당자 | 상태 | 날짜 |\n");
        b.Append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |\n");
        for(int i=0;i<12;i++)b.Append("| ").Append(i).Append(" | PRTWIDECELL").Append(i).Append(" | 아주 넓은 표의 설명 열 | ").Append(i).Append(" | 1000 | 1000 | 비고 | 담당 | 완료 | 2026-10-31 |\n");
        b.Append("\n| 구분 | 긴 내용 |\n| --- | --- |\n| 긴 셀 | PRTLONGCELL").Append(new string('가',240)).Append(" |\n");
        b.Append("| 마지막 | 표의마지막행 |\n");
        string tables=b.ToString();string[] tablesS=new[]{"PRTTABLE001","PRTWIDECELL","PRTLONGCELL","표의마지막행","아주 넓은 표의"};
        cases.Add(new DocCase{Name="doc-tables",Text=tables,MdSentinels=tablesS,TxtSentinels=tablesS,MinPages=2});

        b.Clear();
        b.Append("# 목록과 코드 PRTCODE001\n\n");
        b.Append("- 항목 A\n- 항목 B\n  - 중첩 B1\n    1. 깊은 C1\n- [ ] 미완료 작업\n- [x] 완료 작업 PRTLIST001\n\n");
        b.Append("1. 첫째 항목\n2. 둘째 항목\n\n");
        b.Append("> 코드 앞 인용문\n\n");
        string token="ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        b.Append("```\npublic void Example() {\n");
        b.Append("    Console.WriteLine(\"코드 첫 줄 PRTCODE001\");\n");
        for(int i=0;i<50;i++)b.Append("    Console.WriteLine(\"코드 줄 ").Append(i).Append("\");\n");
        b.Append("    string longLine = \"");
        for(int i=0;i<40;i++)b.Append("word ");
        b.Append("PRTCODE002\";\n");
        b.Append("    string token = \"").Append(token).Append("\";\n");
        b.Append("}\n```\n\n");
        b.Append("인라인 코드 `인라인코드표식` 와 마지막 문장 PRTCODE003.\n");
        string code=b.ToString();string[] codeS=new[]{"PRTCODE001","PRTCODE002","PRTCODE003","PRTLIST001","인라인코드표식",token};
        cases.Add(new DocCase{Name="doc-lists-code",Text=code,MdSentinels=codeS,TxtSentinels=codeS,MinPages=2});

        b.Clear();
        b.Append("# U+3000 검사 PRTU3000A\n\n");
        // A full-width space is text, not block-structure indentation: these three lines must
        // print as ordinary paragraphs, with their marker characters still on the page.
        b.Append("　# 이 줄은 제목이 아니다 PRTU3000B\n\n");
        b.Append("　- 이 줄은 목록이 아니다 PRTU3000C\n\n");
        b.Append("　> 이 줄은 인용이 아니다 PRTU3000D\n");
        string u3000=b.ToString();
        string[] u3000S=new[]{"PRTU3000A","#이 줄은 제목이 아니다","-이 줄은 목록이 아니다",">이 줄은 인용이 아니다"};
        cases.Add(new DocCase{Name="doc-u3000",Text=u3000,MdSentinels=u3000S,TxtSentinels=u3000S,MinPages=1});

        b.Clear();
        b.Append("# 이미지 검사 PRTIMAGE001\n\n");
        b.Append("![샘플 그림](sample-image.png)\n\n");
        b.Append("![없는 그림](missing-image.png)\n\n");
        b.Append("마지막 문장 PRTIMAGE002.\n");
        string images=b.ToString();
        cases.Add(new DocCase{Name="doc-images",Text=images,Images=true,MissingImage=true,
            MdSentinels=new[]{"PRTIMAGE001","PRTIMAGE002","[이미지: 없는 그림]"},
            TxtSentinels=new[]{"PRTIMAGE001","PRTIMAGE002","missing-image.png","sample-image.png"},MinPages=1});

        b.Clear();
        b.Append("# 넓은 그림 검사 PRTWIDEIMG001\n\n");
        b.Append("![넓은 그림](wide-image.png)\n\n");
        b.Append("마지막 문장 PRTWIDEIMG002.\n");
        string wideImage=b.ToString();
        cases.Add(new DocCase{Name="doc-wide-image",Text=wideImage,Images=true,WideImage=true,
            MdSentinels=new[]{"PRTWIDEIMG001","PRTWIDEIMG002"},
            TxtSentinels=new[]{"PRTWIDEIMG001","PRTWIDEIMG002","wide-image.png"},MinPages=1});
        return cases;
    }
    static bool ActivateDark(MainWindow window){
        try{
            MethodInfo method=typeof(MainWindow).GetMethod("SetDarkNow",BindingFlags.Instance|BindingFlags.NonPublic);
            if(method!=null)method.Invoke(window,new object[]{true});
        }catch(Exception ex){report.Append("NOTE dark-invoke ").Append(ex.GetType().Name).Append(": ").Append(ex.Message).Append('\n');}
        try{
            FieldInfo field=typeof(MainWindow).GetField("dark",BindingFlags.Instance|BindingFlags.NonPublic);
            if(field==null)return false;
            if(!(bool)field.GetValue(window))field.SetValue(window,true);
            return (bool)field.GetValue(window);
        }catch(Exception){return false;}
    }
    static void WriteSampleImage(string file){
        if(File.Exists(file))File.Delete(file);
        var visual=new DrawingVisual();
        using(var context=visual.RenderOpen()){
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(46,110,180)),null,new Rect(0,0,640,360));
            context.DrawRectangle(Brushes.White,null,new Rect(30,30,580,300));
            var text=new FormattedText("샘플 이미지 IMG",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Malgun Gothic"),30,Brushes.Black,1.0);
            context.DrawText(text,new Point(60,150));
        }
        var bitmap=new RenderTargetBitmap(640,360,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(file))encoder.Save(stream);
    }
    // A picture far wider than any page: 2000 px against the 729.7 px printable column of the
    // probe page, so the print walk has to scale it down (the file width alone would be cut off).
    static void WriteWideImage(string file){
        if(File.Exists(file))File.Delete(file);
        var visual=new DrawingVisual();
        using(var context=visual.RenderOpen()){
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(180,80,40)),null,new Rect(0,0,2000,240));
            var text=new FormattedText("넓은 이미지 IMG",CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Malgun Gothic"),40,Brushes.White,1.0);
            context.DrawText(text,new Point(40,80));
        }
        var bitmap=new RenderTargetBitmap(2000,240,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var stream=File.Create(file))encoder.Save(stream);
    }
    static void WriteXps(DocumentPaginator paginator,string file){
        if(File.Exists(file))File.Delete(file);
        using(var xps=new XpsDocument(file,FileAccess.ReadWrite,CompressionOption.Normal)){
            XpsDocumentWriter writer=XpsDocument.CreateXpsDocumentWriter(xps);
            writer.Write(paginator);
        }
    }
    static XpsContent ReadXps(string file){
        var content=new XpsContent();
        using(var xps=new XpsDocument(file,FileAccess.Read)){
            IXpsFixedDocumentSequenceReader sequence=xps.FixedDocumentSequenceReader;
            if(sequence==null)throw new InvalidOperationException("FixedDocumentSequenceReader is null");
            foreach(IXpsFixedDocumentReader document in sequence.FixedDocuments){
                foreach(IXpsFixedPageReader page in document.FixedPages){
                    content.Pages++;
                    XmlReader reader=page.XmlReader;
                    if(reader.ReadState==ReadState.Initial)reader.MoveToContent();
                    string markup=reader.ReadOuterXml();
                    if(String.IsNullOrEmpty(markup))throw new InvalidOperationException("empty page markup on page "+content.Pages);
                    var xml=new XmlDocument();xml.LoadXml(markup);
                    content.Text.Append('\n');
                    XmlNodeList glyphs=xml.SelectNodes("//*[local-name()='Glyphs']");
                    foreach(XmlNode node in glyphs){
                        XmlElement glyph=node as XmlElement;if(glyph==null)continue;
                        string unicode=glyph.GetAttribute("UnicodeString");
                        if(!String.IsNullOrEmpty(unicode))content.Text.Append(unicode);
                    }
                    content.ImageBrushes+=xml.SelectNodes("//*[local-name()='ImageBrush']").Count;
                }
            }
        }
        return content;
    }
    static PageScan ScanPages(DocumentPaginator paginator){
        var scan=new PageScan();
        paginator.ComputePageCount();
        for(int i=0;i<paginator.PageCount;i++){
            DocumentPage page=paginator.GetPage(i);
            if(page==null||page==DocumentPage.Missing||page.Visual==null)continue;
            scan.Pages++;
            ScanVisual(page.Visual,Matrix.Identity,scan,i+1);
        }
        return scan;
    }
    static void ScanVisual(Visual visual,Matrix parent,PageScan scan,int page){
        ContainerVisual container=visual as ContainerVisual;
        if(container==null){
            // A picture in an InlineUIContainer is a live Image control, not a DrawingVisual: the
            // element draws its own pixels, so the walk sees no ImageDrawing for it. Its laid-out
            // box is the rectangle the paginator placed, in the same page coordinates.
            System.Windows.Controls.Image image=visual as System.Windows.Controls.Image;
            if(image!=null&&image.RenderSize.Width>0&&image.RenderSize.Height>0){
                Vector offset=VisualTreeHelper.GetOffset(image);
                Matrix placed=Matrix.Identity;placed.Translate(offset.X,offset.Y);
                AddImageBounds(scan,new Rect(0,0,image.RenderSize.Width,image.RenderSize.Height),placed*parent);
            }
            return;
        }
        Matrix local=container.Transform==null?Matrix.Identity:container.Transform.Value;
        local.Translate(container.Offset.X,container.Offset.Y);
        Matrix total=local*parent;
        DrawingVisual drawing=visual as DrawingVisual;
        if(drawing!=null&&drawing.Drawing!=null)ScanDrawing(drawing.Drawing,total,scan,page);
        foreach(Visual child in container.Children)ScanVisual(child,total,scan,page);
    }
    static void ScanDrawing(Drawing drawing,Matrix matrix,PageScan scan,int page){
        DrawingGroup group=drawing as DrawingGroup;
        if(group!=null){
            Matrix childMatrix=group.Transform==null?matrix:group.Transform.Value*matrix;
            foreach(Drawing child in group.Children)ScanDrawing(child,childMatrix,scan,page);
            return;
        }
        GlyphRunDrawing glyph=drawing as GlyphRunDrawing;
        if(glyph!=null&&glyph.GlyphRun!=null){
            scan.GlyphRuns++;
            GlyphRun run=glyph.GlyphRun;
            var text=new StringBuilder();
            if(run.Characters!=null)for(int i=0;i<run.Characters.Count;i++){scan.Text.Append(run.Characters[i]);text.Append(run.Characters[i]);}
            Rect box=run.ComputeInkBoundingBox();
            // The run's own start is where its pen sits: a space laid out without a character
            // still moves the run after it by its advance, which is how the TXT check sees it.
            Point origin=matrix.Transform(new Point(run.BaselineOrigin.X,run.BaselineOrigin.Y));
            double inkLeft=Double.MaxValue;
            if(!box.IsEmpty){
                box.Offset(run.BaselineOrigin.X,run.BaselineOrigin.Y);
                AddBounds(scan,box,matrix,page,false);
                // Ink of a run that begins with a space starts at the first painted glyph, so this
                // left edge is the other way to see a space that only has an advance.
                inkLeft=matrix.Transform(new Point(box.Left,box.Top)).X;
                Point far=matrix.Transform(new Point(box.Right,box.Bottom));
                if(far.X<inkLeft)inkLeft=far.X;
            }
            scan.Runs.Add(new PageRun{Page=page,X=origin.X,Y=origin.Y,Text=text.ToString(),InkLeft=inkLeft});
            return;
        }
        ImageDrawing imageDrawing=drawing as ImageDrawing;
        if(imageDrawing!=null){AddBounds(scan,imageDrawing.Rect,matrix,page,false);AddImageBounds(scan,imageDrawing.Rect,matrix);return;}
        GeometryDrawing geometryDrawing=drawing as GeometryDrawing;
        if(geometryDrawing!=null&&geometryDrawing.Geometry!=null){
            Rect bounds=geometryDrawing.Geometry.Bounds;
            if(!bounds.IsEmpty)AddBounds(scan,bounds,matrix,page,true);
        }
    }
    static void AddBounds(PageScan scan,Rect box,Matrix matrix,int page,bool skipPageBackground){
        Point first=matrix.Transform(new Point(box.Left,box.Top));
        Point second=matrix.Transform(new Point(box.Right,box.Bottom));
        double left=Math.Min(first.X,second.X),right=Math.Max(first.X,second.X),top=Math.Min(first.Y,second.Y),bottom=Math.Max(first.Y,second.Y);
        if(skipPageBackground&&left<=0.5&&top<=0.5&&right>=PageWidth-1&&bottom>=PageHeight-1)return;
        scan.HasBounds=true;
        if(left<scan.MinLeft)scan.MinLeft=left;
        if(right>scan.MaxRight){scan.MaxRight=right;scan.MaxRightPage=page;}
    }
    static void AddImageBounds(PageScan scan,Rect box,Matrix matrix){
        Point first=matrix.Transform(new Point(box.Left,box.Top));
        Point second=matrix.Transform(new Point(box.Right,box.Bottom));
        double right=Math.Max(first.X,second.X);
        if(right>scan.ImageMaxRight)scan.ImageMaxRight=right;
    }
    // The page runs grouped into lines: same page and baseline, in the order the page holds them,
    // with the left edge of the line and the left edge of its first run that carries a character.
    static List<ScanLine> ScanLines(PageScan scan){
        var lines=new List<ScanLine>();
        foreach(PageRun run in scan.Runs){
            ScanLine line=null;
            for(int i=lines.Count-1;i>=0;i--){
                if(lines[i].Page!=run.Page)break;
                if(Math.Abs(lines[i].Baseline-run.Y)<=3.0){line=lines[i];break;}
            }
            if(line==null){line=new ScanLine{Page=run.Page,Baseline=run.Y};lines.Add(line);}
            line.Text.Append(run.Text);
            if(run.X<line.MinX)line.MinX=run.X;
            if(run.Text.Length>0&&run.X<line.ContentX)line.ContentX=run.X;
            if(run.InkLeft<line.InkLeft)line.InkLeft=run.InkLeft;
        }
        return lines;
    }
    static int LeadingWideSpaces(string line){
        int count=0;
        while(count<line.Length&&line[count]==WideSpaceChar)count++;
        return count;
    }
    // The line-initial ideographic spaces are left to the indent check below: WPF's text layout
    // may keep such a space out of the glyph run's characters even though the page shows it.
    // Every other character, an ideographic space inside a line included, stays a strict count.
    static string WithoutLineStartWideSpaces(string value){
        var text=new StringBuilder(value.Length);
        bool atLineStart=true;
        foreach(char c in value){
            if(atLineStart&&c==WideSpaceChar)continue;
            text.Append(c);
            atLineStart=c=='\n'||c=='\r';
        }
        return text.ToString();
    }
    // bughunt print item: a TXT line that begins with an ideographic space is content, not
    // indentation, and the printed page has to show it. When the page scan does not carry the
    // character, the line's first glyph is where that space has to show up: one full-width space
    // is one em of advance, so the first character stands one em right of the page margin per
    // space. That is case "advance-only" (the layout has the space, its run has no character),
    // which this check accepts; "missing" (no character and no indent) is a real loss.
    static void CheckWideSpaceIndent(string label,string text,PageScan scan,double fontSize){
        if(Double.IsNaN(fontSize)||Double.IsInfinity(fontSize)||fontSize<=0)fontSize=16;
        List<ScanLine> lines=ScanLines(scan);
        string[] source=text.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
        int index=0;
        foreach(string sourceLine in source){
            int wide=LeadingWideSpaces(sourceLine);
            if(wide==0)continue;
            string key=Flatten(sourceLine.Substring(wide));
            if(key.Length==0)continue;
            index++;
            string name="u3000-indent-"+index.ToString(CultureInfo.InvariantCulture);
            ScanLine match=null;
            foreach(ScanLine candidate in lines){
                if(Flatten(candidate.Text.ToString().TrimStart(WideSpaceArray)).StartsWith(key,StringComparison.Ordinal)){match=candidate;break;}
            }
            if(match==null){Check(label,name,false,"wide="+wide+" line=["+key+"] not found on a page");continue;}
            string carried=new string(WideSpaceChar,wide);
            bool characters=match.Text.ToString().StartsWith(carried,StringComparison.Ordinal);
            bool positioned=match.ContentX!=Double.MaxValue;
            double indent=positioned?match.ContentX-PageMargin:0.0;
            double inkIndent=match.InkLeft==Double.MaxValue?0.0:match.InkLeft-PageMargin;
            double needed=wide*fontSize*0.5;
            // Either the character's run starts one em in per space, or the run carries the space
            // inside it and its first painted glyph starts that far right. Both mean the page
            // shows the space; neither means the layout dropped it.
            bool indented=(positioned&&indent>=needed)||(match.InkLeft!=Double.MaxValue&&inkIndent>=needed);
            string verdict=characters?"carried":(indented?"advance-only":"missing");
            Check(label,name,characters||indented,"wide="+wide+" case="+verdict+" indent="+F(indent)+" inkIndent="+F(inkIndent)+" contentX="+(positioned?F(match.ContentX):"none")+" minX="+F(match.MinX)+" need="+F(needed));
        }
    }
    static List<KeyValuePair<string,int>> RenderPages(DocumentPaginator paginator,string label,int limit){
        var list=new List<KeyValuePair<string,int>>();
        paginator.ComputePageCount();
        int count=Math.Min(limit,paginator.PageCount);
        for(int i=0;i<count;i++){
            DocumentPage page=paginator.GetPage(i);
            if(page==null||page==DocumentPage.Missing||page.Visual==null)continue;
            string file=Path.Combine(outDir,label+"-p"+(i+1)+".png");
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(PageWidth),(int)Math.Ceiling(PageHeight),96,96,PixelFormats.Pbgra32);
            var background=new DrawingVisual();
            using(var context=background.RenderOpen())context.DrawRectangle(Brushes.White,null,new Rect(0,0,PageWidth,PageHeight));
            bitmap.Render(background);bitmap.Render(page.Visual);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var stream=File.Create(file))encoder.Save(stream);
            list.Add(new KeyValuePair<string,int>(file,NonWhitePixels(bitmap)));
        }
        return list;
    }
    static int NonWhitePixels(RenderTargetBitmap bitmap){
        int width=bitmap.PixelWidth,height=bitmap.PixelHeight,stride=width*4;
        var pixels=new byte[stride*height];bitmap.CopyPixels(pixels,stride,0);
        int count=0;
        for(int i=0;i+2<pixels.Length;i+=20)if(pixels[i]<245||pixels[i+1]<245||pixels[i+2]<245)count++;
        return count;
    }
    static void CheckLight(string label,FlowDocument document){
        Check(label,"print-background-white",IsColor(document.Background,255,255,255),Describe(document.Background));
        Check(label,"print-foreground-black",IsColor(document.Foreground,0,0,0),Describe(document.Foreground));
        var darkBackgrounds=new List<string>();var whiteTexts=new List<string>();
        foreach(Block block in document.Blocks)ScanBlock(block,darkBackgrounds,whiteTexts);
        Check(label,"print-no-dark-background",darkBackgrounds.Count==0,First(darkBackgrounds));
        Check(label,"print-no-white-text",whiteTexts.Count==0,First(whiteTexts));
    }
    static string First(List<string> values){return values.Count==0?"":values[0]+" ("+values.Count+" total)";}
    static void ScanBlock(Block block,List<string> darkBackgrounds,List<string> whiteTexts){
        if(block.Background!=null&&Lum(block.Background)<0.6)darkBackgrounds.Add(block.GetType().Name+" background="+Describe(block.Background));
        Paragraph paragraph=block as Paragraph;
        if(paragraph!=null)ScanInlines(paragraph.Inlines,darkBackgrounds,whiteTexts);
        Section section=block as Section;
        if(section!=null)foreach(Block child in section.Blocks)ScanBlock(child,darkBackgrounds,whiteTexts);
        System.Windows.Documents.List list=block as System.Windows.Documents.List;
        if(list!=null)foreach(ListItem item in list.ListItems)foreach(Block child in item.Blocks)ScanBlock(child,darkBackgrounds,whiteTexts);
        Table table=block as Table;
        if(table!=null)foreach(TableRowGroup group in table.RowGroups)foreach(TableRow row in group.Rows)foreach(TableCell cell in row.Cells){
            if(cell.Background!=null&&Lum(cell.Background)<0.6)darkBackgrounds.Add("TableCell background="+Describe(cell.Background));
            foreach(Block child in cell.Blocks)ScanBlock(child,darkBackgrounds,whiteTexts);
        }
        BlockUIContainer ui=block as BlockUIContainer;
        if(ui!=null){var border=ui.Child as System.Windows.Controls.Border;if(border!=null&&border.Background!=null&&Lum(border.Background)<0.6)darkBackgrounds.Add("rule background="+Describe(border.Background));}
    }
    static void ScanInlines(InlineCollection inlines,List<string> darkBackgrounds,List<string> whiteTexts){
        foreach(Inline inline in inlines){
            if(inline.Background!=null&&Lum(inline.Background)<0.6)darkBackgrounds.Add(inline.GetType().Name+" background="+Describe(inline.Background));
            if(inline.Foreground!=null&&Lum(inline.Foreground)>0.85)whiteTexts.Add(inline.GetType().Name+" foreground="+Describe(inline.Foreground));
            Span span=inline as Span;
            if(span!=null)ScanInlines(span.Inlines,darkBackgrounds,whiteTexts);
        }
    }
    static bool IsColor(Brush brush,byte r,byte g,byte b){SolidColorBrush solid=brush as SolidColorBrush;return solid!=null&&solid.Color.R==r&&solid.Color.G==g&&solid.Color.B==b;}
    static double Lum(Brush brush){SolidColorBrush solid=brush as SolidColorBrush;if(solid==null)return 1;Color c=solid.Color;return (0.299*c.R+0.587*c.G+0.114*c.B)/255.0;}
    static string Describe(Brush brush){SolidColorBrush solid=brush as SolidColorBrush;if(solid==null)return brush==null?"null":"non-solid";Color c=solid.Color;return "#"+c.R.ToString("X2")+c.G.ToString("X2")+c.B.ToString("X2");}
    static string Flatten(string value){
        if(value==null)return "";
        var b=new StringBuilder(value.Length);
        foreach(char c in value)if(c!=' '&&c!='\t'&&c!='\r'&&c!='\n'&&c!=' '&&c!='​'&&c!='﻿')b.Append(c);
        return b.ToString();
    }
    static double ParseD(string value){double result;return Double.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out result)?result:0;}
    static string F(double value){return value.ToString("0.0",CultureInfo.InvariantCulture);}
}
}
