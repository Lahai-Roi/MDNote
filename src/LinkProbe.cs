using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
public partial class MainWindow {
    const string LinkProbeText = "# 제목\n\n본문 [인라인](https://example.invalid/inline) 링크와 각주[^1] 및 [문서][docs] 참조.\n\n[^1]: 각주 정의 텍스트\n\n[docs]: https://example.invalid/docs\n";
    const string LinkProbeInline = "https://example.invalid/inline";
    const string LinkProbeDocs = "https://example.invalid/docs";
    // #anchor fixture: Korean and spaced headings, a duplicate pair (the first must win), one
    // fenced block for the copy menu, and a link to a heading that does not exist.
    const string LinkProbeAnchorText =
        "# 맨 위 제목\n"+
        "\n"+
        "본문 문단.\n"+
        "\n"+
        "## 한글 제목\n"+
        "\n"+
        "## Hello World\n"+
        "\n"+
        "## 같은 이름\n"+
        "\n"+
        "## 같은 이름\n"+
        "\n"+
        "[위로](#맨-위-제목) [한글](#한글-제목) [대소문자](#HELLO-WORLD) [중복](#같은-이름) [없음](#없는-제목)\n"+
        "\n"+
        "```text\n"+
        "alpha\n"+
        "beta\n"+
        "```\n"+
        "\n"+
        "펜스 밖 문단.\n";

    // --link-probe <outDir>: generated text only. Markdown.LinkOpener captures a simulated
    // click, so neither the edit nor the read surface can launch a browser.
    internal static int VerifyLinks(string outDir) {
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var report=new StringBuilder("# LinkProbe\n\n");bool all=true;int exit=0;
        Action<string,bool,string> check=delegate(string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name);if(detail!=null)report.Append(": ").Append(detail);report.Append('\n');};
        string file=Path.Combine(outDir,"link-probe.md");
        File.WriteAllText(file,LinkProbeText,new UTF8Encoding(false));
        bool oldDefault=ProbeDefaults.Live;string opened=null;
        Markdown.LinkOpener=delegate(string url,string document){opened=url;};
        MainWindow live=null;
        try {
            // The live view is the only surface left: read mode is the live read surface at any
            // size, and edit mode is the AvalonEdit source with the same transformer runs.
            ProbeDefaults.Live=true;
            live=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            live.Show();live.Left=-20000;live.LoadFileCore(file);LinkProbePump(live);
            var session=live.livePreview;
            check("live view attaches",live.CodeMode&&session!=null&&live.LivePreviewAttached,"code="+live.CodeMode+", attached="+live.LivePreviewAttached);
            live.SetMode(true);live.source.TextArea.Caret.Line=1;LinkProbePump(live);
            DocumentLine bodyLine=live.source.Document.GetLineByNumber(3);
            DocumentLine noteLine=live.source.Document.GetLineByNumber(5);
            DocumentLine refLine=live.source.Document.GetLineByNumber(7);
            string bodyText=live.source.Document.GetText(bodyLine),noteText=live.source.Document.GetText(noteLine),refText=live.source.Document.GetText(refLine);
            int noteStart=bodyText.IndexOf("[^1]",StringComparison.Ordinal),inlineAt=bodyText.IndexOf("[인라인]",StringComparison.Ordinal),referenceAt=bodyText.IndexOf("[문서]",StringComparison.Ordinal);
            int noteIdAt=noteText.IndexOf('^')+1,noteBodyAt=noteText.IndexOf(':')+2,refBodyAt=0;
            MarkdownLivePreview.LineInfo bodyInfo=session.Info(bodyLine),noteInfo=session.Info(noteLine),refInfo=session.Info(refLine);
            bool refStyle=noteStart>=0&&LinkProbeHasStyle(bodyInfo,noteStart+2,MarkdownLivePreview.Style.Link|MarkdownLivePreview.Style.Superscript);
            bool markerStyle=noteIdAt>0&&LinkProbeHasStyle(noteInfo,noteIdAt,MarkdownLivePreview.Style.Link|MarkdownLivePreview.Style.Superscript);
            bool noteTextStyle=noteBodyAt<noteText.Length&&LinkProbeHasStyle(noteInfo,noteBodyAt,MarkdownLivePreview.Style.Muted|MarkdownLivePreview.Style.Small);
            bool refDefinitionStyle=refText.Length>0&&LinkProbeHasStyle(refInfo,refBodyAt,MarkdownLivePreview.Style.Muted|MarkdownLivePreview.Style.Small);
            VisualLine bodyVisual=live.source.TextArea.TextView.GetVisualLine(3),noteVisual=live.source.TextArea.TextView.GetVisualLine(5),refVisual=live.source.TextArea.TextView.GetVisualLine(7);
            VisualLineElement refElement=LinkProbeElement(bodyVisual,noteStart+2);
            bool refVisualOk=refElement!=null&&refElement.TextRunProperties.BaselineAlignment==BaselineAlignment.Superscript&&LinkProbeNear(refElement.TextRunProperties.FontRenderingEmSize,live.fontSize*0.75)&&LinkProbeColor(refElement.TextRunProperties.ForegroundBrush)==LinkProbeColor(session.Colors.Link);
            VisualLineElement markerElement=LinkProbeElement(noteVisual,noteIdAt);
            bool markerVisualOk=markerElement!=null&&markerElement.TextRunProperties.BaselineAlignment==BaselineAlignment.Superscript&&LinkProbeNear(markerElement.TextRunProperties.FontRenderingEmSize,live.fontSize*0.75);
            VisualLineElement noteBodyElement=LinkProbeElement(noteVisual,noteBodyAt);
            VisualLineElement refBodyElement=LinkProbeElement(refVisual,refBodyAt);
            bool mutedVisualOk=noteBodyElement!=null&&LinkProbeColor(noteBodyElement.TextRunProperties.ForegroundBrush)==LinkProbeColor(session.Colors.Muted)&&LinkProbeNear(noteBodyElement.TextRunProperties.FontRenderingEmSize,live.fontSize*0.9)&&refBodyElement!=null&&LinkProbeColor(refBodyElement.TextRunProperties.ForegroundBrush)==LinkProbeColor(session.Colors.Muted);
            check("live footnote reference is a superscript link",refStyle&&refVisualOk,"style="+refStyle+", visual="+refVisualOk);
            check("live definition marker is a superscript link",markerStyle&&markerVisualOk,"style="+markerStyle+", visual="+markerVisualOk);
            check("live definition text is muted",noteTextStyle&&refDefinitionStyle&&mutedVisualOk,"note="+noteTextStyle+", reference="+refDefinitionStyle+", visual="+mutedVisualOk);
            var urls=new List<string>();
            Action<string> capture=delegate(string url){urls.Add(url);};
            session.LinkActivated+=capture;
            urls.Clear();bool plainLive=session.ActivateLinkAt(bodyLine.Offset+noteStart+1,false);
            check("live edit plain click does not navigate",!plainLive&&urls.Count==0,"returned="+plainLive+", urls="+urls.Count);
            urls.Clear();bool ctrlLive=session.ActivateLinkAt(bodyLine.Offset+noteStart+1,true);
            check("live Ctrl+click calls the host with the footnote url",ctrlLive&&urls.Count==1&&urls[0]=="#^1","urls="+LinkProbeJoin(urls));
            live.OpenLiveLink("#^1");LinkProbePump(live);
            var atCaret=live.source.Document.GetLineByOffset(Math.Min(live.source.CaretIndex,live.source.Document.TextLength));
            check("live footnote target moves the caret to the definition",live.source.Document.GetText(atCaret).StartsWith("[^1]:",StringComparison.Ordinal),"line="+atCaret.LineNumber);
            urls.Clear();bool ctrlInlineLive=session.ActivateLinkAt(bodyLine.Offset+inlineAt+1,true);
            check("live Ctrl+click opens an inline link",ctrlInlineLive&&urls.Count==1&&urls[0]==LinkProbeInline,"urls="+LinkProbeJoin(urls));
            urls.Clear();bool ctrlReferenceLive=session.ActivateLinkAt(bodyLine.Offset+referenceAt+1,true);
            opened=null;if(urls.Count==1)live.OpenLiveLink(urls[0]);
            check("live reference link resolves its definition",ctrlReferenceLive&&urls.Count==1&&urls[0]=="ref:docs"&&opened==LinkProbeDocs,"urls="+LinkProbeJoin(urls)+", opened="+(opened==null?"-":opened));
            urls.Clear();bool markerLive=session.ActivateLinkAt(noteLine.Offset+1,true);
            int selectedLine=live.source.Document.GetLineByOffset(Math.Min(live.source.SelectionStart,live.source.Document.TextLength)).LineNumber;
            check("live definition marker selects the first reference",markerLive&&urls.Count==0&&live.source.SelectedText=="[^1]"&&selectedLine==3,"urls="+urls.Count+", selected='"+live.source.SelectedText+"', line="+selectedLine);
            // Link hover tooltip: the definition check above left the caret on the body line, and
            // a revealed line draws Markdown source, so park the caret on the blank line 2 first.
            live.source.Select(live.source.Document.GetLineByNumber(2).Offset,0);LinkProbePump(live);
            var view=live.source.TextArea.TextView;
            bool editTip=session.UpdateLinkHover(bodyLine.Offset+inlineAt+1,false,false);
            check("hover tooltip shows Ctrl+click and the url in edit mode",editTip&&session.LinkTipText=="Ctrl+클릭하여 열기 · "+LinkProbeInline,"tip="+LinkProbeShow(session.LinkTipText));
            bool plainCursor=view.Cursor!=System.Windows.Input.Cursors.Hand;
            session.UpdateLinkHover(bodyLine.Offset+inlineAt+1,true,false);
            bool handCursor=view.Cursor==System.Windows.Input.Cursors.Hand;
            check("hover cursor is a hand only while Ctrl is held",plainCursor&&handCursor,"plain="+plainCursor+", ctrl="+handCursor);
            int hoverChecks=session.LinkHoverChecks;
            session.UpdateLinkHover(bodyLine.Offset+inlineAt+1,true,false);
            check("hover on the same offset does no hit-test work",session.LinkHoverChecks==hoverChecks,"checks="+session.LinkHoverChecks+"/"+hoverChecks);
            session.UpdateLinkHover(bodyLine.Offset+noteStart+1,true,false);
            string noteTip=session.LinkTipText;
            check("footnote reference hover shows its definition text",noteTip!=null&&noteTip.Contains("각주 정의 텍스트"),"tip="+LinkProbeShow(noteTip));
            session.UpdateLinkHover(noteLine.Offset+1,true,false);
            string markerTip=session.LinkTipText;
            check("definition marker hover shows its reference",markerTip!=null&&markerTip.EndsWith(" · [^1]",StringComparison.Ordinal),"tip="+LinkProbeShow(markerTip));
            session.UpdateLinkHover(bodyLine.Offset+referenceAt+1,true,false);
            string referenceTip=session.LinkTipText;
            check("reference link hover shows its definition url",referenceTip!=null&&referenceTip.Contains(LinkProbeDocs),"tip="+LinkProbeShow(referenceTip));
            session.UpdateLinkHover(bodyLine.Offset,true,false);
            check("leaving the link closes the tooltip",!session.LinkTipShown&&view.Cursor!=System.Windows.Input.Cursors.Hand,"shown="+session.LinkTipShown);
            live.source.Select(bodyLine.Offset,0);LinkProbePump(live);
            VisualLine rawVisual=live.source.TextArea.TextView.GetVisualLine(bodyLine.LineNumber);
            VisualLineElement rawElement=LinkProbeElement(rawVisual,0);
            string rawElementFace=rawElement==null?"(no element)":rawElement.TextRunProperties.Typeface.FontFamily.Source;
            check("revealed source line keeps the document face",String.Equals(rawElementFace,live.family,StringComparison.Ordinal),
                "face="+rawElementFace+"/"+live.family);
            bool rawHover=session.UpdateLinkHover(bodyLine.Offset+inlineAt+1,true,false);
            check("revealed source line shows no link tooltip",!rawHover&&!session.LinkTipShown,"shown="+session.LinkTipShown);
            live.source.Select(live.source.Document.GetLineByNumber(2).Offset,0);LinkProbePump(live);
            live.SetMode(false);LinkProbePump(live);
            check("live read mode is read-only",!live.editing&&session.Options.ReadOnly,"readOnly="+session.Options.ReadOnly);
            urls.Clear();bool readLive=session.ActivateLinkAt(bodyLine.Offset+inlineAt+1,false);
            check("live read plain click opens an inline link",readLive&&urls.Count==1&&urls[0]==LinkProbeInline,"urls="+LinkProbeJoin(urls));
            urls.Clear();bool readNote=session.ActivateLinkAt(bodyLine.Offset+noteStart+1,false);
            check("live read plain click follows a footnote",readNote&&urls.Count==1&&urls[0]=="#^1","urls="+LinkProbeJoin(urls));
            // Read mode: plain click wording and the hand cursor with no modifier.
            bool readTip=session.UpdateLinkHover(bodyLine.Offset+inlineAt+1,false,true);
            check("hover tooltip shows click-to-open and the url in read mode",readTip&&session.LinkTipText=="클릭하여 열기 · "+LinkProbeInline,"tip="+LinkProbeShow(session.LinkTipText));
            check("read mode shows the hand cursor over a link",live.source.TextArea.TextView.Cursor==System.Windows.Input.Cursors.Hand,"cursor="+live.source.TextArea.TextView.Cursor);
            session.UpdateLinkHover(noteLine.Offset+1,false,true);
            string readMarkerTip=session.LinkTipText;
            check("read mode hover keeps the footnote target",readMarkerTip!=null&&readMarkerTip.Contains("[^1]"),"tip="+LinkProbeShow(readMarkerTip));
            session.UpdateLinkHover(-1,false,true);
            check("pointer off the text closes the tooltip",!session.LinkTipShown,"shown="+session.LinkTipShown);
            session.LinkActivated-=capture;
            // The 원문 toggle detaches the preview; the whole editor then uses the Latin-first
            // source face, and switching back restores the document face.
            live.SetMode(true);LinkProbePump(live);
            live.loading=true;live.preview.IsChecked=true;live.loading=false;live.SwitchSurface();LinkProbePump(live);
            string rawFace=live.source.FontFamily.Source;bool rawDetached=live.livePreview==null;
            string rawAsciiFace=LinkProbeGlyphFamily(live.source.TextArea.TextView.GetVisualLine(1),'#');
            live.loading=true;live.preview.IsChecked=false;live.loading=false;live.SwitchSurface();LinkProbePump(live);
            string liveFace=live.source.FontFamily.Source;
            check("raw view uses the Latin-first source face",rawDetached&&rawFace=="Consolas, Malgun Gothic","detached="+rawDetached+", face="+rawFace);
            check("raw view draws ASCII with the code face",rawDetached&&rawAsciiFace=="Consolas","detached="+rawDetached+", ascii="+rawAsciiFace);
            check("live view restores the document face",liveFace==live.family,"face="+liveFace+"/"+live.family);

            // #anchor and fenced-code copy use their own fixture, so the footnote line numbers above
            // stay valid. It holds Korean and spaced headings, a duplicate pair, one fence and a
            // link to a heading that does not exist.
            string anchorFile=Path.Combine(outDir,"link-probe-anchor.md");
            File.WriteAllText(anchorFile,LinkProbeAnchorText,new UTF8Encoding(false));
            live.LoadFileCore(anchorFile);LinkProbePump(live);
            var anchorSession=live.livePreview;
            if(anchorSession==null)throw new InvalidOperationException("the anchor fixture did not attach");
            anchorSession.LinkActivated+=capture;
            live.SetMode(true);LinkProbePump(live);
            TextDocument anchorDocument=live.source.Document;
            DocumentLine topLine=LinkProbeLineWith(anchorDocument,"# 맨 위 제목");
            DocumentLine koreanLine=LinkProbeLineWith(anchorDocument,"## 한글 제목");
            DocumentLine caseLine=LinkProbeLineWith(anchorDocument,"## Hello World");
            DocumentLine duplicateLine=LinkProbeLineWith(anchorDocument,"## 같은 이름");
            DocumentLine anchorBody=LinkProbeLineWith(anchorDocument,"본문 문단.");
            DocumentLine alphaLine=LinkProbeLineWith(anchorDocument,"alpha");
            DocumentLine anchorAfter=LinkProbeLineWith(anchorDocument,"펜스 밖 문단.");
            int topAt=LinkProbeOffsetOf(anchorDocument,"[위로](#맨-위-제목)");
            int koreanAt=LinkProbeOffsetOf(anchorDocument,"[한글](#한글-제목)");
            int caseAt=LinkProbeOffsetOf(anchorDocument,"[대소문자](#HELLO-WORLD)");
            int duplicateAt=LinkProbeOffsetOf(anchorDocument,"[중복](#같은-이름)");
            int missingAt=LinkProbeOffsetOf(anchorDocument,"[없음](#없는-제목)");
            bool anchorFixture=topLine!=null&&koreanLine!=null&&caseLine!=null&&duplicateLine!=null&&anchorBody!=null&&alphaLine!=null&&anchorAfter!=null&&
                topAt>=0&&koreanAt>=0&&caseAt>=0&&duplicateAt>=0&&missingAt>=0;
            check("anchor fixture keeps its headings, links and fence",anchorFixture,"korean line="+(koreanLine==null?0:koreanLine.LineNumber)+", offsets="+koreanAt+"/"+missingAt);
            if(!anchorFixture)throw new InvalidOperationException("the anchor fixture lost a heading or a link");

            live.source.Select(anchorAfter.Offset,0);urls.Clear();opened=null;
            bool koreanJump=anchorSession.ActivateLinkAt(koreanAt+1,true);
            int koreanCaret=anchorDocument.GetLineByOffset(Math.Min(live.source.CaretIndex,anchorDocument.TextLength)).LineNumber;
            check("live Ctrl+click on an #anchor link jumps to the Korean heading",
                koreanJump&&koreanCaret==koreanLine.LineNumber&&urls.Count==1&&urls[0]=="#한글-제목"&&opened==null,
                "line="+koreanCaret+"/"+koreanLine.LineNumber+", urls="+LinkProbeJoin(urls)+", opened="+(opened==null?"-":opened));
            live.source.Select(anchorAfter.Offset,0);urls.Clear();
            bool spaceJump=anchorSession.ActivateLinkAt(topAt+1,true);
            int spaceCaret=anchorDocument.GetLineByOffset(Math.Min(live.source.CaretIndex,anchorDocument.TextLength)).LineNumber;
            check("live #anchor maps heading spaces to hyphens",spaceJump&&spaceCaret==topLine.LineNumber,"line="+spaceCaret+"/"+topLine.LineNumber);
            live.source.Select(anchorAfter.Offset,0);urls.Clear();
            bool caseJump=anchorSession.ActivateLinkAt(caseAt+1,true);
            int caseCaret=anchorDocument.GetLineByOffset(Math.Min(live.source.CaretIndex,anchorDocument.TextLength)).LineNumber;
            check("live #anchor matches case-insensitively",caseJump&&caseCaret==caseLine.LineNumber,"line="+caseCaret+"/"+caseLine.LineNumber);
            live.source.Select(anchorAfter.Offset,0);urls.Clear();
            bool duplicateJump=anchorSession.ActivateLinkAt(duplicateAt+1,true);
            check("live #anchor takes the first duplicate heading",duplicateJump&&live.source.CaretIndex==duplicateLine.Offset,
                "caret="+live.source.CaretIndex+"/"+duplicateLine.Offset);
            live.source.Select(anchorAfter.Offset,0);int beforeMissing=live.source.CaretIndex;urls.Clear();opened=null;
            bool missingJump=anchorSession.ActivateLinkAt(missingAt+1,true);
            check("live #anchor with no matching heading moves nothing",
                missingJump&&live.source.CaretIndex==beforeMissing&&opened==null&&urls.Count==1&&urls[0]=="#없는-제목",
                "caret="+live.source.CaretIndex+"/"+beforeMissing+", urls="+LinkProbeJoin(urls)+", opened="+(opened==null?"-":opened));

            // The fence copy menu uses the editor's own code-paragraph copy item (CodeHighlight.cs:14).
            var codeMenu=live.LiveCodeMenuAt(alphaLine.Offset);
            string codeHeader=codeMenu==null||codeMenu.Items.Count!=1?null:((System.Windows.Controls.MenuItem)codeMenu.Items[0]).Header as string;
            check("live fence menu carries the editor's copy item",codeMenu!=null&&codeMenu.Items.Count==1&&codeHeader==live.T("코드 복사"),
                "items="+(codeMenu==null?0:codeMenu.Items.Count)+", header="+(codeHeader==null?"-":codeHeader));
            string codeBody=live.LiveCodeBodyAt(alphaLine.Offset);
            string copiedBody=null;
            try{
                LiveCodeCopyHook=delegate(string text){copiedBody=text;};
                if(codeMenu!=null&&codeMenu.Items.Count>0)((System.Windows.Controls.MenuItem)codeMenu.Items[0]).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            }finally{LiveCodeCopyHook=null;}
            check("live code copy copies the body without the fences",
                codeBody!=null&&LinkProbeNewlines(codeBody)=="alpha\nbeta"&&copiedBody==codeBody,
                "body="+LinkProbeShow(codeBody)+", copied="+LinkProbeShow(copiedBody));
            bool outsideCode=live.LiveCodeMenuAt(anchorBody.Offset)==null&&live.LiveCodeMenuAt(anchorAfter.Offset)==null&&
                live.LiveCodeMenuAt(topLine.Offset)==null&&live.LiveCodeBodyAt(anchorBody.Offset)==null;
            check("live code menu exists only inside a fence",outsideCode,
                "body="+(live.LiveCodeMenuAt(anchorBody.Offset)==null)+", after="+(live.LiveCodeMenuAt(anchorAfter.Offset)==null)+", heading="+(live.LiveCodeMenuAt(topLine.Offset)==null));
            live.PrepareLiveCodeMenu(alphaLine.Offset);
            bool menuInstalled=live.source.TextArea.ContextMenu!=null&&live.source.TextArea.ContextMenu.Items.Count==1;
            live.PrepareLiveCodeMenu(anchorBody.Offset);
            check("live right-click installs the copy menu inside a fence only",menuInstalled&&live.source.TextArea.ContextMenu==null,
                "installed="+menuInstalled+", cleared="+(live.source.TextArea.ContextMenu==null));

            live.SetMode(false);LinkProbePump(live);
            live.source.Select(anchorAfter.Offset,0);urls.Clear();
            bool readAnchor=anchorSession.ActivateLinkAt(koreanAt+1,false);
            int readCaret=anchorDocument.GetLineByOffset(Math.Min(live.source.CaretIndex,anchorDocument.TextLength)).LineNumber;
            check("live read plain click follows #anchor",readAnchor&&readCaret==koreanLine.LineNumber&&urls.Count==1&&urls[0]=="#한글-제목",
                "line="+readCaret+"/"+koreanLine.LineNumber+", urls="+LinkProbeJoin(urls));
            check("live read mode keeps the fence copy menu",live.LiveCodeMenuAt(alphaLine.Offset)!=null,"readOnly="+anchorSession.Options.ReadOnly);
            anchorSession.LinkActivated-=capture;
        }catch(Exception ex){all=false;exit=2;report.Append("EXCEPTION ").Append(ex).Append('\n');}
        finally {
            Markdown.LinkOpener=null;ProbeDefaults.Live=oldDefault;
            if(live!=null){live.saved=live.text;live.Close();}
        }
        if(all)exit=0;else if(exit==0)exit=1;
        report.Append(all?"\nResult: PASS\n":"\nResult: FAIL\n");
        try {
            File.WriteAllText(Path.Combine(outDir,"link-probe.txt"),report.ToString(),new UTF8Encoding(false));
            string pass=Path.Combine(outDir,"link-probe-PASS.txt"),fail=Path.Combine(outDir,"link-probe-FAIL.txt");
            if(all){File.WriteAllText(pass,"PASS\n",new UTF8Encoding(false));if(File.Exists(fail))File.Delete(fail);}
            else{File.WriteAllText(fail,"FAIL\n",new UTF8Encoding(false));if(File.Exists(pass))File.Delete(pass);}
        }catch(IOException){exit=2;}
        return exit;
    }

    static void LinkProbePump(Window window) {
        window.UpdateLayout();
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{frame.Continue=false;}));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
        var main=window as MainWindow;
        if(main!=null&&main.source!=null)main.source.TextArea.TextView.EnsureVisualLines();
    }
    static bool LinkProbeNear(double value,double expected){return Math.Abs(value-expected)<0.01;}
    static Color LinkProbeColor(Brush brush){var solid=brush as SolidColorBrush;return solid==null?Colors.Transparent:solid.Color;}
    static bool LinkProbeHasStyle(MarkdownLivePreview.LineInfo info,int relative,MarkdownLivePreview.Style style){foreach(MarkdownLivePreview.StyledPart part in info.Styles)if(relative>=part.Start&&relative<part.Start+part.Length&&(part.Style&style)==style)return true;return false;}
    static VisualLineElement LinkProbeElement(VisualLine line,int relative){
        if(line==null)return null;
        foreach(VisualLineElement element in line.Elements){
            int start=element.RelativeTextOffset;
            if(relative>=start&&relative<start+element.DocumentLength&&element.VisualLength>0)return element;
        }
        return null;
    }
    static string LinkProbeGlyphFamily(VisualLine visual,char target){
        if(visual==null)return "(no visual line)";
        foreach(System.Windows.Media.TextFormatting.TextLine text in visual.TextLines)
            foreach(System.Windows.Media.TextFormatting.IndexedGlyphRun run in text.GetIndexedGlyphRuns()){
                GlyphRun glyphs=run.GlyphRun;
                if(glyphs.Characters==null||glyphs.GlyphTypeface==null)continue;
                foreach(char c in glyphs.Characters)if(c==target)return LinkProbePrimaryFamily(glyphs.GlyphTypeface);
            }
        return "(glyph not drawn)";
    }
    static string LinkProbePrimaryFamily(GlyphTypeface face){
        string name;
        if(face.FamilyNames.TryGetValue(CultureInfo.GetCultureInfo("en-US"),out name))return name;
        foreach(KeyValuePair<CultureInfo,string> pair in face.FamilyNames)return pair.Value;
        return "(unnamed)";
    }
    static string LinkProbeJoin(List<string> values){var text=new StringBuilder();for(int i=0;i<values.Count;i++){if(i>0)text.Append(',');text.Append(values[i]);}return text.ToString();}
    static DocumentLine LinkProbeLineWith(TextDocument document,string needle){
        foreach(DocumentLine line in document.Lines)if(document.GetText(line).Contains(needle))return line;
        return null;
    }
    static int LinkProbeOffsetOf(TextDocument document,string needle){
        DocumentLine line=LinkProbeLineWith(document,needle);
        return line==null?-1:line.Offset+document.GetText(line).IndexOf(needle,StringComparison.Ordinal);
    }
    static string LinkProbeNewlines(string value){return value==null?null:value.Replace("\r\n","\n").Replace('\r','\n');}
    static string LinkProbeShow(string value){
        if(value==null)return "null";
        string single=LinkProbeNewlines(value).Replace("\n","\\n");
        return single.Length>40?single.Substring(0,40)+"...":single;
    }
}
}
