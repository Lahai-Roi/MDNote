using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
namespace MDNote {
// Generated text only. Drive routed input and the actual combo/button handlers, then read
// the saved Markdown from disk (not a pending-style flag or a command's own result).
internal static class InputFormatProbeRunner {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    internal static int Run(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        var report=new StringBuilder();bool all=true;int serial=0;
        Action<string,bool,string> check=delegate(string name,bool ok,string detail){all&=ok;report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail.Replace('\r',' ').Replace('\n',' ')).Append('\n');};
        bool old=ProbeDefaults.Live;MainWindow window=null;
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        try{
            ProbeDefaults.Live=true;
            string fixture=Path.Combine(outDir,"inputformat-live.md");File.WriteAllText(fixture,"seed",new UTF8Encoding(false));
            window=new MainWindow {ShowInTaskbar=false,Width=900,Height=700};window.Show();window.Left=-20000;window.Activate();
            Call(window,"LoadFileCore",fixture);window.SetMode(true);Pump();
            var editor=Field<SourceEditor>(window,"source");var combo=Field<ComboBox>(window,"sizes");
            string prefix="live ";
            foreach(string format in new[]{"bold","italic","color","size"}){
                Reset(window,"seed ");
                if(format=="bold"||format=="italic")Field<Button>(window,format=="bold"?"boldButton":"italicButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                else if(format=="color")Call(window,"SetTextColor",Colors.Red);
                else ChooseSize(window,combo,"28 px");
                Type(window,"한글");
                string markdown=Save(window,outDir,ref serial);
                string expected=format=="bold"?"**한글**":format=="italic"?"*한글*":format=="color"?"color:#FF0000":"font-size:28px";
                bool ok=markdown.Contains(expected)&&markdown.Contains("한글")&&MarkdownPlainText.Convert(markdown).Contains("seed 한글");
                check(prefix+"empty selection "+format+" survives input and save",ok,markdown);
            }
            // Typing style: a toggle with no selection arms the style and writes nothing. The
            // pair appears only with the first input, so an abandoned toggle leaves no "****"
            // (or empty span) in the source and no undo step of its own.
            Reset(window,"seed ");Click(window,"boldButton");
            bool armedQuiet=editor.Document.Text=="seed "&&editor.CaretIndex==5;
            bool armedTag=Object.Equals(Field<Button>(window,"boldButton").Tag,"Active");
            editor.Select(0,0);Pump();
            bool movedQuiet=editor.Document.Text=="seed "&&!Object.Equals(Field<Button>(window,"boldButton").Tag,"Active");
            editor.Select(editor.Document.TextLength,0);Pump();Type(window,"x");
            check(prefix+"an armed style writes nothing and a caret move cancels it",
                armedQuiet&&armedTag&&movedQuiet&&editor.Document.Text=="seed x",
                "quiet="+armedQuiet+", active="+armedTag+", moved="+movedQuiet+", text=["+editor.Document.Text+"]");
            Reset(window,"seed ");Click(window,"boldButton");Type(window,"abc");
            check(prefix+"the first input is wrapped by the armed pair",
                editor.Document.Text=="seed **abc**"&&editor.CaretIndex==10,
                "text=["+editor.Document.Text+"] caret="+editor.CaretIndex);
            // The pair is written with the caret inside it, so the undo entry records that spot:
            // redo must put the caret back in front of the closing markers, not after them.
            Reset(window,"seed ");Click(window,"boldButton");Type(window,"a");
            Call(window,"Undo");Pump();Call(window,"Redo");Pump();
            check(prefix+"redo of a pending-style pair restores the caret inside the pair",
                editor.Document.Text=="seed **a**"&&editor.CaretIndex==8,
                "text=["+editor.Document.Text+"] caret="+editor.CaretIndex);
            Reset(window,"seed ");Click(window,"boldButton");Click(window,"boldButton");Type(window,"abc");
            check(prefix+"a second toggle cancels the armed style",
                editor.Document.Text=="seed abc",
                "text=["+editor.Document.Text+"]");
            // An edit that keeps the caret where it was (removing the character after it) also ends
            // the arm; the toolbar must drop the lit button without waiting for a caret move.
            Reset(window,"seed END");editor.Select(5,0);Pump();
            Click(window,"boldButton");
            bool armedLit=Object.Equals(Field<Button>(window,"boldButton").Tag,"Active");
            editor.Document.Remove(5,1);Pump();
            bool armCleared=!Object.Equals(Field<Button>(window,"boldButton").Tag,"Active");
            Type(window,"X");
            check(prefix+"an armed style ends on an edit that keeps the caret",
                armedLit&&armCleared&&editor.Document.Text=="seed XND",
                "lit="+armedLit+", cleared="+armCleared+", text=["+editor.Document.Text+"]");
            Reset(window,"seed ");Click(window,"boldButton");Type(window,"abc");Call(window,"Undo");Pump();
            check(prefix+"undo after armed typing leaves no markers",
                editor.Document.Text=="seed "&&editor.CaretIndex==5,
                "text=["+editor.Document.Text+"] caret="+editor.CaretIndex);
            // One character per input, the way a keyboard delivers them: the pair written by the
            // first character belongs to the same typing run, so one Ctrl+Z takes all of `**abc**`.
            Reset(window,"seed ");Click(window,"boldButton");
            Type(window,"a");Type(window,"b");Type(window,"c");
            string perChar=editor.Document.Text;
            Call(window,"Undo");Pump();
            string afterOneUndo=editor.Document.Text;
            Call(window,"Redo");Pump();
            string afterRedo=editor.Document.Text;
            check(prefix+"one undo takes a pending-style run typed one character at a time",
                perChar=="seed **abc**"&&afterOneUndo=="seed "&&afterRedo==perChar,
                "typed=["+perChar+"] undo=["+afterOneUndo+"] redo=["+afterRedo+"] caret="+editor.CaretIndex);
            Reset(window,"seed ");Call(window,"SetTextColor",Colors.Red);
            editor.Select(editor.Document.TextLength,0);Pump();
            check(prefix+"an abandoned colour arm leaves no span",editor.Document.Text=="seed ",
                "text=["+editor.Document.Text+"]");
            Reset(window,"seed ");ChooseSize(window,combo,"28 px");
            editor.Select(editor.Document.TextLength,0);Pump();
            check(prefix+"an abandoned size arm leaves no span",editor.Document.Text=="seed ",
                "text=["+editor.Document.Text+"]");
            Reset(window,"seed ");ChooseSize(window,combo,"28 px");Type(window,"재선택");
            string sameSize=Save(window,outDir,ref serial);check(prefix+"same dropdown item applies at a new caret",sameSize.Contains("font-size:28px"),sameSize);
            // A size chosen at a caret with no selection arms the style for the text typed next,
            // exactly like the bold toggle: the box shows the pending size until the first input,
            // and a caret move before typing drops it.
            Reset(window,"seed ");ChooseSize(window,combo,"20 px");
            string pendingCaption=combo.Text;
            Type(window,"가");
            string sizedTyping=Save(window,outDir,ref serial);
            check(prefix+"a chosen size is a pending style shown in the box",
                pendingCaption=="20 px"&&sizedTyping.Contains("font-size:20px")&&sizedTyping.Contains("가"),
                "box=["+pendingCaption+"] md=["+sizedTyping.Replace('\r',' ').Replace('\n','|')+"]");
            Reset(window,"seed ");ChooseSize(window,combo,"20 px");
            editor.Select(0,0);Pump();
            Type(window,"가");
            string cancelledSize=Save(window,outDir,ref serial);
            check(prefix+"a caret move cancels the pending size",cancelledSize=="가seed "&&combo.Text!="20 px",
                "md=["+cancelledSize.Replace('\r',' ').Replace('\n','|')+"] box=["+combo.Text+"]");
            foreach(Key key in new[]{Key.Enter,Key.Escape}){
                Reset(window,"seed ");FocusSize(window,combo);combo.Text="32";
                var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(combo),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};combo.RaiseEvent(args);Pump();
                editor.Focus();Pump();Type(window,"키");
                string markdown=Save(window,outDir,ref serial);
                bool size=markdown.Contains("font-size:32px");
                check(prefix+"size box "+key+(key==Key.Enter?" applies":" cancels"),args.Handled&&(key==Key.Enter?size:markdown=="seed 키"),markdown+", handled="+args.Handled);
            }
            // Matching a dropdown label must not auto-apply while the editable box owns
            // focus: WPF also raises SelectionChanged for this typed text.
            foreach(Key key in new[]{Key.Enter,Key.Escape}){
                Reset(window,"seed ");FocusSize(window,combo);combo.Text="48 px";Pump();
                var args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(combo),Environment.TickCount,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};combo.RaiseEvent(args);Pump();Type(window,"라벨");
                string markdown=Save(window,outDir,ref serial);bool size=markdown.Contains("font-size:48px");check(prefix+"exact size label "+key,args.Handled&&(key==Key.Enter?size:markdown=="seed 라벨"),markdown);
            }
            // An open popup stages its selection; Escape must discard that choice too.
            Reset(window,"seed ");FocusSize(window,combo);combo.IsDropDownOpen=true;Pump();combo.SelectedItem="40 px";
            combo.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(combo),Environment.TickCount,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});Pump();Type(window,"취소");
            string cancelled=Save(window,outDir,ref serial);check(prefix+"dropdown Escape cancels",!cancelled.Contains("font-size:40px"),cancelled);
            VerifyUndoRuns(window,check);
            VerifyLinkFormats(window,check);
            VerifyWholeSelectionFormats(window,check);
            VerifyReleaseFixes(window,check);
            VerifyOverlapFormatting(window,check);
            // The picker polling and save-dialog ownership checks do not use the removed rich
            // surface; the source-only window still runs their product paths.
            VerifyPickerTimer(window,check);VerifySaveDialog(window,check);
            FieldSet(window,"saved",Property<string>(window,"text"));window.Close();window=null;
        }catch(Exception ex){check("exception",false,ex.ToString());}
        finally{if(window!=null){FieldSet(window,"imeComposing",false);FieldSet(window,"saved",Property<string>(window,"text"));window.Close();}ProbeDefaults.Live=old;}
        foreach(string state in new[]{"PASS","FAIL"}){string file=Path.Combine(outDir,"inputformat-probe-"+state+".txt");if(File.Exists(file))File.Delete(file);}
        File.WriteAllText(Path.Combine(outDir,"inputformat-probe-"+(all?"PASS":"FAIL")+".txt"),report.ToString(),new UTF8Encoding(false));return all?0:1;
    }
    // Real-use regressions on the basic (markers hidden) surface, built through the toolbar the
    // way a user does it, not from a loaded literal:
    //  1. Enter between "b" and "c" of a nested run closes every pair on the first line and opens
    //     the same pairs on the second, so no marker is drawn as text; one undo restores the line.
    //  2. Typing at the drawn end of the run lands inside every pair, bold included (11-05 user decision:
    //     the toolbar shows the style lit there, so the typed text takes it; it replaced the 11-01 rule).
    static void VerifyOverlapFormatting(MainWindow window,Action<string,bool,string> check){
        Call(window,"SetLiveMode",false);Pump();
        string[] names={"bold","italic","strike","code","size","color","bold+size","bold+color","size+color","bold+size+color"};
        string[][] sets={new[]{"bold"},new[]{"italic"},new[]{"strike"},new[]{"code"},new[]{"size"},new[]{"color"},new[]{"bold","size"},new[]{"bold","color"},new[]{"size","color"},new[]{"bold","size","color"}};
        for(int i=0;i<names.Length;i++){
            string built=BuildOverlapRun(window,sets[i]);
            int abc=built.IndexOf("abc",StringComparison.Ordinal);
            if(abc<0){check("basic overlap build "+names[i],false,"text=["+Flat(built)+"]");continue;}
            VerifyOverlapEnter(window,check,names[i],built,abc);
            VerifyOverlapAppend(window,check,names[i],built,abc);
        }
        // The user's document is a normal CRLF file with lines above the edited line.
        Reset(window,"first\r\n");Type(window,"abc");
        string onLaterLine=ApplyOverlapStyles(window,new[]{"bold","size","color"});
        int later=onLaterLine.IndexOf("abc",StringComparison.Ordinal);
        if(later>=0){
            VerifyOverlapEnter(window,check,"CRLF line after text",onLaterLine,later);
            Reset(window,"first\r\n");Type(window,"abc");
            string again=ApplyOverlapStyles(window,new[]{"bold","size","color"});
            VerifyOverlapAppend(window,check,"CRLF line after text",again,again.IndexOf("abc",StringComparison.Ordinal));
        }
        // The same rules on nestings a command-built run does not produce: two separate spans
        // outside the emphasis, a span inside the emphasis, and mixed emphasis pairs.
        string[] literals={
            "<span style=\"color:#FF0000\"><span style=\"font-size:20px\">**abc**</span></span>",
            "**<span style=\"color:#FF0000;font-size:20px\">abc</span>**",
            "<span style=\"font-size:20px\">***abc***</span>",
            "<span style=\"font-size:20px\">~~abc~~</span>",
            "**~~abc~~**"
        };
        for(int i=0;i<literals.Length;i++){
            string source=literals[i];int abc=source.IndexOf("abc",StringComparison.Ordinal);
            Reset(window,source);Pump();
            VerifyOverlapEnter(window,check,"literal "+i,source,abc);
            // VerifyOverlapEnter undoes back to the built text; the literal survives that.
            Reset(window,source);Pump();
            VerifyOverlapAppend(window,check,"literal "+i,source,abc);
        }
        Call(window,"SetLiveMode",true);Pump();
    }
    // Applies the requested formats to a fresh `abc` through the real buttons, dropdown and colour
    // command, re-selecting the visible word before each step like the user's selection.
    static string BuildOverlapRun(MainWindow window,string[] styles){
        Reset(window,"");Type(window,"abc");
        return ApplyOverlapStyles(window,styles);
    }
    static string ApplyOverlapStyles(MainWindow window,string[] styles){
        var editor=Field<SourceEditor>(window,"source");var combo=Field<ComboBox>(window,"sizes");
        foreach(string style in styles){
            int at=editor.Document.Text.IndexOf("abc",StringComparison.Ordinal);
            if(at<0)break;
            editor.Select(at,3);Pump();
            if(style=="bold")Click(window,"boldButton");
            else if(style=="italic")Click(window,"italicButton");
            else if(style=="strike")Click(window,"strikeButton");
            else if(style=="code")Click(window,"codeButton");
            else if(style=="size")ChooseSize(window,combo,"20 px");
            else if(style=="color")Call(window,"SetTextColor",Colors.Red);
        }
        return editor.Document.Text;
    }
    static void VerifyOverlapEnter(MainWindow window,Action<string,bool,string> check,string name,string built,int abc){
        var editor=Field<SourceEditor>(window,"source");
        editor.Select(abc+2,0);Pump();
        bool handled=PressEnter(window);
        string actual=editor.Document.Text;
        int lineStart=Math.Max(built.LastIndexOf('\n',Math.Max(0,abc-1))+1,0);
        string openers=built.Substring(lineStart,abc-lineStart),closers=built.Substring(abc+3);
        // InlineEnter inherits the nearest earlier line delimiter ("\n" in a one-line buffer).
        string newline=built.IndexOf('\r')>=0?"\r\n":"\n";
        string expected=built.Substring(0,abc+2)+closers+newline+openers+built.Substring(abc+2,1)+closers;
        check("basic overlap Enter "+name,handled&&actual==expected,
            "handled="+handled+", text=["+Flat(actual)+"], built=["+Flat(built)+"], expected=["+Flat(expected)+"]");
        Call(window,"Undo");Pump();
        check("basic overlap Enter undo "+name,editor.Document.Text==built,"text=["+Flat(editor.Document.Text)+"]");
    }
    static void VerifyOverlapAppend(MainWindow window,Action<string,bool,string> check,string name,string built,int abc){
        var editor=Field<SourceEditor>(window,"source");
        // The drawn end draws the closers as nothing: the caret may sit at their outer edge.
        editor.Select(built.Length,0);Pump();
        Type(window,"d");
        string outer=editor.Document.Text;
        string expected=built.Insert(abc+3,"d");
        check("basic overlap typing at the drawn end "+name,outer==expected,
            "text=["+Flat(outer)+"], expected=["+Flat(expected)+"]");
        Call(window,"Undo");Pump();
        bool undone=editor.Document.Text==built;
        // And from the inner edge, directly in front of the hidden closers.
        editor.Select(abc+3,0);Pump();
        Type(window,"e");
        string inner=editor.Document.Text;
        string innerExpected=built.Insert(abc+3,"e");
        check("basic overlap typing at the inner edge "+name,undone&&inner==innerExpected,
            "undo=["+undone+"] text=["+Flat(inner)+"], expected=["+Flat(innerExpected)+"]");
        Call(window,"Undo");Pump();
        // The caret a command leaves between the emphasis closer and the tag closer draws on the
        // same spot as the text end; input there belongs inside every pair as well. Only a
        // leading emphasis closer gives a valid boundary there (a tag closer has no caret stop
        // between its characters).
        int closerStart=abc+3,emphasis=0;
        if(closerStart+2<=built.Length&&(built.Substring(closerStart,2)=="**"||built.Substring(closerStart,2)=="~~"))emphasis=2;
        else if(closerStart<built.Length&&(built[closerStart]=='*'||built[closerStart]=='`'))emphasis=1;
        if(emphasis>0){
            editor.Select(closerStart+emphasis,0);Pump();
            Type(window,"f");
            string between=editor.Document.Text;
            // A caret between the emphasis closer and a tag closer draws on the same spot: inside every pair.
            string betweenExpected=built.Insert(abc+3,"f");
            check("basic overlap typing just past the emphasis closer "+name,between==betweenExpected,
                "text=["+Flat(between)+"], expected=["+Flat(betweenExpected)+"]");
            Call(window,"Undo");Pump();
        }
    }
    // The keyboard path SourceStructuralKeys takes: PreviewKeyDown, then KeyDown when the
    // structural handler declined. No real key is sent.
    static bool PressEnter(MainWindow window){
        var editor=Field<SourceEditor>(window,"source");
        var preview=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor.TextArea),Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        editor.TextArea.RaiseEvent(preview);Pump();
        bool handled=preview.Handled;
        if(!handled){
            var down=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor.TextArea),Environment.TickCount,Key.Enter){RoutedEvent=Keyboard.KeyDownEvent};
            editor.TextArea.RaiseEvent(down);Pump();
        }
        return handled;
    }
    static string Flat(string text){return text==null?"null":text.Replace("\r","\\r").Replace("\n","\\n");}
    // Undo step boundaries: a caret the user moved into an older typing run is its own step, a
    // pending-style pair typed on one character at a time stays one step, and a caret moved
    // inside that pair splits the run. Run in both views (basic hides the markers, live shows
    // the caret line as source); the source/typing path is shared, the document edits differ.
    static void VerifyUndoRuns(MainWindow window,Action<string,bool,string> check){
        var editor=Field<SourceEditor>(window,"source");var combo=Field<ComboBox>(window,"sizes");
        foreach(bool live in new[]{false,true}){
            Call(window,"SetLiveMode",live);Pump();
            string prefix=live?"live run ":"basic run ";
            // (a) hello, two presses of ←, then X: X alone must be one step and the run the next.
            Reset(window,"seed ");Type(window,"hello");editor.Select(editor.Document.TextLength-2,0);Pump();Type(window,"X");
            string typed=editor.Document.Text;Call(window,"Undo");Pump();string once=editor.Document.Text;Call(window,"Undo");Pump();
            check(prefix+"a caret moved into a typed run keeps its own undo step",
                typed=="seed helXlo"&&once=="seed hello"&&editor.Document.Text=="seed ",
                "typed=["+typed+"] once=["+once+"] twice=["+editor.Document.Text+"]");
            // (b) one continuous typing run is one step: the space and the word typed after it
            // join the run (the old word-boundary split is gone).
            Reset(window,"seed ");Type(window,"a");Type(window,"b");Type(window," ");Type(window,"c");
            string run=editor.Document.Text;Call(window,"Undo");Pump();string runOnce=editor.Document.Text;Call(window,"Undo");Pump();
            check(prefix+"one undo takes the space and the word typed after it",
                run=="seed ab c"&&runOnce=="seed "&&editor.Document.Text=="seed ",
                "typed=["+run+"] once=["+runOnce+"] twice=["+editor.Document.Text+"]");
            // (c) the armed pair typed one character at a time is one undo step, in every style.
            Reset(window,"seed ");Click(window,"boldButton");Type(window,"a");Type(window,"b");Type(window,"c");
            string pair=editor.Document.Text;Call(window,"Undo");Pump();string pairOnce=editor.Document.Text;
            check(prefix+"one undo takes a pending-style run typed one character at a time",
                pair=="seed **abc**"&&pairOnce=="seed ","typed=["+pair+"] undo=["+pairOnce+"]");
            Reset(window,"seed ");ChooseSize(window,combo,"20 px");Type(window,"a");Type(window,"b");
            string sized=editor.Document.Text;Call(window,"Undo");Pump();
            check(prefix+"one undo takes a size-pair run typed one character at a time",
                sized.StartsWith("seed <span style=\"font-size:20px\">ab</span>")&&editor.Document.Text=="seed ",
                "typed=["+sized+"] undo=["+editor.Document.Text+"]");
            Reset(window,"seed ");Call(window,"SetTextColor",Colors.Red);Type(window,"a");Type(window,"b");
            string coloured=editor.Document.Text;Call(window,"Undo");Pump();
            check(prefix+"one undo takes a colour-pair run typed one character at a time",
                coloured.StartsWith("seed <span style=\"color:#FF0000\">ab</span>")&&editor.Document.Text=="seed ",
                "typed=["+coloured+"] undo=["+editor.Document.Text+"]");
            // (d) a caret moved inside the pair starts a new step, before and after the run's
            // second character.
            Reset(window,"seed ");Click(window,"boldButton");Type(window,"a");editor.Select(editor.CaretIndex-1,0);Pump();Type(window,"b");
            string back=editor.Document.Text;Call(window,"Undo");Pump();
            check(prefix+"a caret moved inside the pair starts its own step",
                back=="seed **ba**"&&editor.Document.Text=="seed **a**","typed=["+back+"] undo=["+editor.Document.Text+"]");
            Reset(window,"seed ");Click(window,"boldButton");Type(window,"a");Type(window,"b");editor.Select(editor.CaretIndex-1,0);Pump();Type(window,"c");
            string moved=editor.Document.Text;Call(window,"Undo");Pump();
            check(prefix+"a caret moved into a typed pair run starts its own step",
                moved=="seed **acb**"&&editor.Document.Text=="seed **ab**","typed=["+moved+"] undo=["+editor.Document.Text+"]");
            // (e) The same offset reached again after a caret left the run is still a new step: the
            // user's report moved away with the arrows and typed on at the run's end, and one undo
            // took both runs.
            Reset(window,"seed ");Type(window,"a");Type(window,"b");
            int runEnd=editor.CaretIndex;
            editor.Select(runEnd-1,0);Pump();editor.Select(runEnd,0);Pump();Type(window,"c");
            string returned=editor.Document.Text;Call(window,"Undo");Pump();string returnedOnce=editor.Document.Text;
            check(prefix+"a caret that left and came back still starts a new step",
                returned=="seed abc"&&returnedOnce=="seed ab",
                "typed=["+returned+"] once=["+returnedOnce+"]");
            // (f) A space typed while the closing-edge move is waiting only extends that gap. It
            // must stay a typing entry: an empty update opened for the refused move used to take
            // the typing-run flag, and the space that landed after it was recorded as a plain edit.
            Reset(window,"");Type(window,"ab");editor.Select(0,2);Pump();Click(window,"boldButton");
            string pairText=editor.Document.Text;
            editor.Select(pairText.Length-2,0);Pump();Type(window," ");Type(window," ");
            // Both views keep the space outside the pair: the closing-edge move puts it past the
            // closers on the live raw caret line too (MarkdownLivePreview.HiddenCloseTypingEntering),
            // and only the next non-space character takes the gap back inside the pair.
            string gapTypedText=pairText+"  ";
            bool gapTyped=LastEntryTyped(window);
            check(prefix+"a space that only extends a closing-edge gap stays a typing entry",
                pairText=="**ab**"&&editor.Document.Text==gapTypedText&&gapTyped,
                "pair=["+pairText+"] text=["+editor.Document.Text+"] typed="+gapTyped);
        }
        Call(window,"SetLiveMode",true);Pump();
    }
    // A toolbar style on a selection that covers a link's visible text wraps the whole link from
    // outside its markup (user, 2026-10-31, b56: the pen button wrote its pair inside "[...]",
    // which the live parse never reads, so the link showed raw source). The selection is the
    // label's first character, the buttons are the real ones, and the second press uses the
    // selection the command left, exactly the toggle a user gets.
    static void VerifyLinkFormats(MainWindow window,Action<string,bool,string> check){
        var editor=Field<SourceEditor>(window,"source");
        foreach(bool live in new[]{false,true}){
            Call(window,"SetLiveMode",live);Pump();
            string prefix=live?"live link ":"basic link ";
            LinkCase(window,check,editor,prefix+"bold wraps the whole link","[t](u)","boldButton","**[t](u)**","[t](u)");
            // Backticks around "[t](u)" would draw the link's markup as code: the pen styles the
            // whole label inside the brackets, so the link stays a link.
            LinkCase(window,check,editor,prefix+"pen styles the whole label inside the link","[t](u)","codeButton","[`t`](u)","[t](u)");
            LinkCase(window,check,editor,prefix+"bold toggles a bold link off","**[t](u)**","boldButton","[t](u)","**[t](u)**");
            LinkCase(window,check,editor,prefix+"pen keeps a bold link a link","**[t](u)**","codeButton","**[`t`](u)**","**[t](u)**");
            LinkCase(window,check,editor,prefix+"a partial label selection takes the whole link","[가나다](https://example.com)","boldButton",
                "**[가나다](https://example.com)**","[가나다](https://example.com)");
        }
        Call(window,"SetLiveMode",true);Pump();
        // One undo takes the wrap back, and the text around the link keeps its own place.
        Reset(window,"앞 [t](u) 뒤");
        editor.Select(3,1);Pump();
        Click(window,"codeButton");
        string wrapped=editor.Document.Text;
        Call(window,"Undo");Pump();
        check("link one undo restores the line",wrapped=="앞 [`t`](u) 뒤"&&editor.Document.Text=="앞 [t](u) 뒤",
            "wrapped=["+wrapped+"] undo=["+editor.Document.Text+"]");
    }
    static void LinkCase(MainWindow window,Action<string,bool,string> check,SourceEditor editor,string name,string source,string button,string first,string second){
        Reset(window,source);
        editor.Select(source.IndexOf('[')+1,1);Pump();
        Click(window,button);
        string after=editor.Document.Text;
        Click(window,button);
        string back=editor.Document.Text;
        check(name,after==first&&back==second,"press=["+after+"] toggle=["+back+"] expected=["+first+"] / ["+second+"]");
    }
    // Word/Google Docs behavior the user asked for (release blockers, videos 14:37/14:38): a
    // selection that mixes styled and plain text takes the style as a whole, a selection already
    // in the style loses it as a whole (also from the middle of a longer run), and no result may
    // leave a pair the parser cannot draw ("****", a closer after a space, a marker inside
    // backticks). Read back from the drawn text: the line must still render, not show the source.
    static void VerifyWholeSelectionFormats(MainWindow window,Action<string,bool,string> check){
        var editor=Field<SourceEditor>(window,"source");
        Call(window,"SetLiveMode",false);Pump();
        string plain="빌드 결과가 「지금 번역」과 같을 때만 바뀝니다.";
        // Rule A: half bold -> one pair around the whole selection -> no bold at all.
        Reset(window,"**가나다** 라마");
        editor.Select(2,8);Pump();
        Click(window,"boldButton");
        string applied=editor.Document.Text;
        Click(window,"boldButton");
        string removed=editor.Document.Text;
        check("rule A half-bold selection takes the whole style",
            applied=="**가나다 라마**"&&removed=="가나다 라마"&&applied.IndexOf("****",StringComparison.Ordinal)<0,
            "applied=["+applied+"] removed=["+removed+"]");
        // Rule B: the user's line, a word selected inside the longer run. The closer moves to the
        // last spot the parser accepts (before the space), so the rest stays bold and nothing is
        // drawn as raw source.
        string line="**빌드 결과가 「지금 번역」과 같을 때만** 바뀝니다.";
        Reset(window,line);
        int at=line.IndexOf('「');
        editor.Select(at,"「지금 번역」".Length);Pump();
        Click(window,"boldButton");
        string split=editor.Document.Text;
        string expected="**빌드 결과가** 「지금 번역」**과 같을 때만** 바뀝니다.";
        check("rule B unbold inside a run splits it",split==expected,"text=["+split+"] expected=["+expected+"]");
        check("rule B split renders without markers",NoVisibleMarker(window,plain),"visible=["+Visible(window)+"]");
        Click(window,"boldButton");
        string rebold=editor.Document.Text;
        check("rule B toggle back re-bolds only the word",
            rebold=="**빌드 결과가** **「지금 번역」과 같을 때만** 바뀝니다."&&NoVisibleMarker(window,plain),
            "text=["+rebold+"] visible=["+Visible(window)+"]");
        // Bug C: italic and colour on the same word, then the B and I toggles: every pair stays
        // balanced and the drawn line keeps its text and shows no marker.
        Reset(window,line);
        editor.Select(at,"「지금 번역」".Length);Pump();
        Click(window,"italicButton");
        Call(window,"SetTextColor",Colors.Red);Pump();
        bool colored=NoVisibleMarker(window,plain);
        Click(window,"boldButton");
        bool afterBold=NoVisibleMarker(window,plain);
        Click(window,"italicButton");
        bool afterItalic=NoVisibleMarker(window,plain);
        string bugC=editor.Document.Text;
        check("bug C toggles keep the line balanced and rendered",
            colored&&afterBold&&afterItalic&&bugC.IndexOf("****",StringComparison.Ordinal)<0&&EvenStars(bugC)&&EvenTildes(bugC),
            "colored="+colored+", after B="+afterBold+", after I="+afterItalic+", visible=["+Visible(window)+"], text=["+bugC+"]");
        // A selection inside inline code never takes a marker between the backticks: the emphasis
        // takes the whole run, and the pen toggles the run itself.
        Reset(window,"`{행, 원문, ...}`");
        editor.Select(2,1);Pump();
        Click(window,"boldButton");
        string aroundCode=editor.Document.Text;
        Click(window,"strikeButton");
        string strikeCode=editor.Document.Text;
        bool insideWhole=aroundCode=="**`{행, 원문, ...}`**"
            &&strikeCode=="**~~`{행, 원문, ...}`~~**";
        Reset(window,"`{행, 원문, ...}`");
        editor.Select(2,1);Pump();
        Click(window,"codeButton");
        string penInCode=editor.Document.Text;
        check("a selection inside inline code keeps its backticks whole",
            insideWhole&&penInCode=="{행, 원문, ...}"&&penInCode.IndexOf('`')<0,
            "bold=["+aroundCode+"] strike=["+strikeCode+"] pen=["+penInCode+"]");
        // The user's 14:26 video: a drag from the line start takes the hidden "**[" too, and the
        // pen turned the whole bold link into raw code. The label takes the code inside the link,
        // the line still draws as text, and a second press restores the source.
        string video="**[다운로드 페이지](https://example.com/r)**에서 `x.exe`를 받으면 돼.";
        Reset(window,video);
        editor.Select(0,"**[다운로드 페이지".Length);Pump();
        Click(window,"codeButton");
        string penLink=editor.Document.Text;
        bool penLinkDrawn=Visible(window)=="다운로드 페이지에서 x.exe를 받으면 돼.";
        Click(window,"codeButton");
        check("pen on a dragged bold link keeps the link",
            penLink=="**[`다운로드 페이지`](https://example.com/r)**에서 `x.exe`를 받으면 돼."&&penLinkDrawn&&editor.Document.Text==video,
            "pen=["+penLink+"] drawn="+penLinkDrawn+" visible=["+Visible(window)+"] toggle=["+editor.Document.Text+"]");
        // A selection that cuts a bold run: the "**" stays outside the backticks on both presses.
        Reset(window,"**굵은 글자** 보통");
        editor.Select(5,"글자** 보통".Length);Pump();
        Click(window,"codeButton");
        string penCut=editor.Document.Text;
        bool penCutDrawn=NoVisibleMarker(window,"굵은 글자 보통");
        Click(window,"codeButton");
        check("pen across a bold edge keeps the pair closed",
            penCut=="**굵은 `글자`** `보통`"&&penCutDrawn&&editor.Document.Text=="**굵은 글자** 보통",
            "pen=["+penCut+"] drawn="+penCutDrawn+" toggle=["+editor.Document.Text+"]");
        Call(window,"SetLiveMode",true);Pump();
    }
    // Release-blocker videos (11-01 16:45, 16:46 and the list dropdown, basic view): the code
    // button takes a fenced block back to plain lines and fences it again, one syllable inside a
    // word takes every inline style, and converting/removing a list marker leaves no spaces
    // behind (the view hides a marker's separator run, so a stray one showed only after removal).
    static void VerifyReleaseFixes(MainWindow window,Action<string,bool,string> check){
        var editor=Field<SourceEditor>(window,"source");
        Call(window,"SetLiveMode",false);Pump();
        Reset(window,"a\nb");Pump();
        string nl=editor.Document.Text.IndexOf('\r')>=0?"\r\n":"\n";
        string block="```"+nl+"원화 = 엔화 × 8.5"+nl+"예) 1,000엔 × 8.5 = 약 8,500원"+nl+"```";
        string inside="원화 = 엔화 × 8.5"+nl+"예) 1,000엔 × 8.5 = 약 8,500원";
        Reset(window,block);
        editor.Select(block.IndexOf("원화"),0);Pump();
        Click(window,"blockButton");
        string opened=editor.Document.Text;
        Click(window,"blockButton");
        string refenced=editor.Document.Text;
        check("code button inside a block removes its fences and keeps the code",opened==inside&&refenced==block,
            "opened=["+Flat(opened)+"] refenced=["+Flat(refenced)+"]");
        // The user's drag takes both fence lines with the code; the same press still toggles off.
        Reset(window,block);
        editor.Select(0,block.Length);Pump();
        Click(window,"blockButton");
        check("code button over a whole dragged block removes its fences",editor.Document.Text==inside,
            "text=["+Flat(editor.Document.Text)+"]");
        // One syllable inside a word: the pair lands around that syllable for every style, and the
        // second press (on the selection the command left) takes it back.
        string line="표에는 굵직한 동선만. **자주 바뀌는** 세부 일정은 아래에 따로 적기.";
        string[] buttons={"strikeButton","boldButton","italicButton"};
        string[] marks={"~~","**","*"};
        string[] names={"strike","bold","italic"};
        int syllable=line.IndexOf("세부",StringComparison.Ordinal)+1;
        for(int i=0;i<buttons.Length;i++){
            string expected=line.Substring(0,syllable)+marks[i]+"부"+marks[i]+line.Substring(syllable+1);
            Reset(window,line);
            editor.Select(syllable,1);Pump();
            Click(window,buttons[i]);
            string applied=editor.Document.Text;
            Click(window,buttons[i]);
            check("one syllable inside a word takes "+names[i],applied==expected&&editor.Document.Text==line,
                "applied=["+Flat(applied)+"] toggle=["+Flat(editor.Document.Text)+"] expected=["+Flat(expected)+"]");
        }
        // A bullet converts in place and the removal leaves the text at column 0. The second row
        // types the marker's separator as two spaces: the screen draws the item the same, and the
        // removal must not leave a leading space (user's list/task video).
        string item="표 도구 줄의 들여쓰기 버튼을 누르면 표가 바로 위 목록 항목 안으로 들어갑니다.";
        string[] sources={"- "+item,"-  "+item};
        string[] labels={""," with a two-space marker"};
        for(int i=0;i<sources.Length;i++){
            Reset(window,sources[i]);
            editor.Select(0,sources[i].Length);Pump();
            Call(window,"ToggleListStyle",true);
            string numbered=editor.Document.Text;
            Call(window,"ToggleListStyle",true);
            string removed=editor.Document.Text;
            check("numbered list conversion keeps the column"+labels[i],numbered=="1. "+item&&removed==item,
                "numbered=["+Flat(numbered)+"] removed=["+Flat(removed)+"]");
        }
        for(int i=0;i<sources.Length;i++){
            Reset(window,sources[i]);
            editor.Select(0,sources[i].Length);Pump();
            Click(window,"taskButton");
            string tasked=editor.Document.Text;
            Click(window,"taskButton");
            string removed=editor.Document.Text;
            check("task conversion keeps the column"+labels[i],tasked=="- [ ] "+item&&removed==item,
                "tasked=["+Flat(tasked)+"] removed=["+Flat(removed)+"]");
        }
        Call(window,"SetLiveMode",true);Pump();
    }
    static bool EvenStars(string text){int n=0;for(int i=0;i<text.Length;i++)if(text[i]=='*')n++;return n%2==0;}
    static bool EvenTildes(string text){int n=0;for(int i=0;i<text.Length;i++)if(text[i]=='~')n++;return n%2==0;}
    // The drawn text of the whole document (basic view draws every line formatted).
    static string Visible(MainWindow window){
        var session=Field<MarkdownLivePreview.Session>(window,"livePreview");
        if(session==null||session.IsDisposed)return "<no session>";
        int[] offsets;
        return session.VisibleText(out offsets);
    }
    static bool NoVisibleMarker(MainWindow window,string expected){
        string visible=Visible(window);
        return visible==expected&&visible.IndexOf('*')<0&&visible.IndexOf('~')<0;
    }
    // The newest source entry a typing run wrote (DocumentHistory.Edit.Typed): true when the last
    // edit came from a text input, not from a command or a paste.
    static bool LastEntryTyped(MainWindow window){
        object history=typeof(MainWindow).GetProperty("History",Members).GetValue(window,null);
        if(history==null)return false;
        var list=(System.Collections.IList)history.GetType().GetField("undo",Members).GetValue(history);
        if(list==null||list.Count==0)return false;
        object edit=list[list.Count-1];
        return (bool)edit.GetType().GetField("Typed",Members).GetValue(edit);
    }
    static void VerifyPickerTimer(MainWindow window,Action<string,bool,string> check){
        Reset(window,"picker test");Call(window,"OpenTablePicker");Pump();var menu=Field<ContextMenu>(window,"tablePickerMenu");var button=Field<Button>(window,"tableButton");DateTime now=DateTime.UtcNow;
        Point pointer=button.PointToScreen(new Point(2,2));FieldSet(window,"tablePickerPointerPosition",new Func<Point>(delegate{return pointer;}));FieldSet(window,"tablePickerHoverOpened",true);Call(window,"QueueTablePickerClose");
        Call(window,"PollTablePicker",now);Call(window,"PollTablePicker",now.AddMilliseconds(400));bool buttonKept=menu.IsOpen;
        pointer=menu.PointToScreen(new Point(5,5));Call(window,"PollTablePicker",now.AddMilliseconds(500));bool menuKept=menu.IsOpen;
        // The 2026-10-24 pointer policy keeps the grid open only over the button, the popup and the
        // gap between them; the old 200ms re-entry grace is gone, so leaving closes on the same poll.
        Point a=button.PointToScreen(new Point()),b=button.PointToScreen(new Point(button.ActualWidth,button.ActualHeight)),c=menu.PointToScreen(new Point()),d=menu.PointToScreen(new Point(menu.ActualWidth,menu.ActualHeight));
        double top=Math.Min(b.Y,d.Y),bottom=Math.Max(a.Y,c.Y);if(bottom<top){double swap=top;top=bottom;bottom=swap;}
        pointer=new Point((a.X+b.X)/2,(top+bottom)/2);Call(window,"PollTablePicker",now.AddMilliseconds(550));bool gap=menu.IsOpen;
        pointer=new Point(-100000,-100000);Call(window,"PollTablePicker",now.AddMilliseconds(600));bool closed=!menu.IsOpen;
        check("picker polling keeps button popup and gap and closes immediately outside",buttonKept&&menuKept&&gap&&closed,"button="+buttonKept+", popup="+menuKept+", gap="+gap+", closed="+closed);
        Call(window,"OpenTablePicker");menu=Field<ContextMenu>(window,"tablePickerMenu");Call(window,"PollTablePicker",now.AddSeconds(5));bool keyboard=menu.IsOpen;menu.IsOpen=false;FieldSet(window,"tablePickerPointerPosition",null);
        var timer=Field<DispatcherTimer>(window,"tablePickerCloseTimer");check("keyboard picker ignores pointer and closed picker stops timer",keyboard&&timer!=null&&!timer.IsEnabled,"keyboard open="+keyboard+", timer stopped="+(timer!=null&&!timer.IsEnabled));
    }
    static void VerifySaveDialog(MainWindow window,Action<string,bool,string> check){
        var dialog=new Window {Owner=window,ShowInTaskbar=false,Width=200,Height=100,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000};
        Call(window,"ForegroundSaveWindow",dialog);bool premature=dialog.IsVisible;
        check("E foreground does not show an unopened dialog",!premature,"visible before ShowDialog="+premature);dialog.Close();
        dialog=new Window {Owner=window,ShowInTaskbar=false,Width=200,Height=100,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000};
        var target=dialog;bool injected=false,shownInGap=false,loaded=false;Exception failure=null;
        dialog.Loaded+=delegate{loaded=true;dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{target.Close();}));};
        window.WindowState=WindowState.Minimized;
        EventHandler restoring=delegate{
            if(injected||window.WindowState==WindowState.Minimized)return;injected=true;
            object[] args={IntPtr.Zero,0x10,IntPtr.Zero,IntPtr.Zero,false};Call(window,"SaveCloseMessage",args);shownInGap=target.IsVisible;
        };
        window.StateChanged+=restoring;
        try{Call(window,"ShowSaveConfirmation",dialog);}catch(TargetInvocationException ex){failure=ex.InnerException;}finally{window.StateChanged-=restoring;target.Close();}
        check("E close message before ShowDialog stays modal",injected&&!shownInGap&&loaded&&failure==null,"injected="+injected+", shown in gap="+shownInGap+", Loaded="+loaded+", exception="+(failure==null?"none":failure.GetType().Name));Pump();
    }
    static void Reset(MainWindow window,string text){
        window.SetText(text);Pump();window.Activate();
        var editor=Field<SourceEditor>(window,"source");editor.Select(editor.Document.TextLength,0);editor.Focus();Keyboard.Focus(editor.TextArea);
        Pump();
    }
    static void Type(MainWindow window,string value){
        Field<SourceEditor>(window,"source").TextArea.PerformTextInput(value);
        Pump();Call(window,"FlushDocumentChanges");
    }
    static void Click(MainWindow window,string name){
        Field<Button>(window,name).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump();
    }
    static void FocusSize(MainWindow window,ComboBox combo){window.Activate();combo.ApplyTemplate();var text=combo.Template.FindName("PART_EditableTextBox",combo) as TextBox;if(text!=null){text.Focus();Keyboard.Focus(text);}else combo.Focus();Pump();}
    static void ChooseSize(MainWindow window,ComboBox combo,string size){FocusSize(window,combo);combo.IsDropDownOpen=true;Pump();var item=combo.ItemContainerGenerator.ContainerFromItem(size) as ComboBoxItem;if(item!=null)item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseDownEvent,Source=item});combo.SelectedItem=size;combo.IsDropDownOpen=false;Pump();}
    static string Save(MainWindow window,string outDir,ref int serial){Call(window,"FinishComposition");string path=Path.Combine(outDir,"saved-"+(++serial)+".md");Call(window,"SaveToPath",path,new UTF8Encoding(false),false);return File.ReadAllText(path);}
    static void Pump(){LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
    static FieldInfo RequireField(string name){var field=typeof(MainWindow).GetField(name,Members);if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");return field;}
    static T Field<T>(MainWindow window,string name){return (T)RequireField(name).GetValue(window);}
    static void FieldSet(MainWindow window,string name,object value){RequireField(name).SetValue(window,value);}
    static T Property<T>(MainWindow window,string name){var property=typeof(MainWindow).GetProperty(name,Members);if(property==null)throw new MissingMemberException("probe: MainWindow has no property '"+name+"'");return (T)property.GetValue(window,null);}
    static object Call(MainWindow window,string name,params object[] args){var method=typeof(MainWindow).GetMethod(name,Members);if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");return method.Invoke(window,args);}
}
public partial class MainWindow {
    internal static int VerifyInputFormat(string outDir){return InputFormatProbeRunner.Run(outDir);}
}
}
