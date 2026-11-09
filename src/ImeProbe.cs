using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// --ime-probe <outDir>: generated-text check of the IME composition font. AvalonEdit re-adds its
// own IME hook on every keyboard focus, so this also proves that the editor's hook is registered
// once (before the first focus) and therefore runs after AvalonEdit's. IME messages are sent
// straight to the window handle; a missing IME context or keyboard focus is a SKIP, not a FAIL.
internal static class ImeProbeRunner {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int WM_IME_STARTCOMPOSITION=0x010D,WM_IME_COMPOSITION=0x010F,WM_IME_ENDCOMPOSITION=0x010E,GCS_COMPSTR=0x0008,GCS_RESULTSTR=0x0800;
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    struct ProbeLogFont {
        public int lfHeight,lfWidth,lfEscapement,lfOrientation,lfWeight;
        public byte lfItalic,lfUnderline,lfStrikeOut,lfCharSet,lfOutPrecision,lfClipPrecision,lfQuality,lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string lfFaceName;
    }
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd,int msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd,IntPtr hIMC);
    [DllImport("imm32.dll",CharSet=CharSet.Unicode,EntryPoint="ImmGetCompositionFontW")] static extern bool ImmGetCompositionFont(IntPtr hIMC,out ProbeLogFont font);
    [StructLayout(LayoutKind.Sequential)]
    struct ProbeForm { public int Style,X,Y,Left,Top,Right,Bottom; }
    [DllImport("imm32.dll")] static extern bool ImmGetCompositionWindow(IntPtr hIMC,out ProbeForm form);

    internal static int Run(string outDir){
        outDir=Path.GetFullPath(outDir);Directory.CreateDirectory(outDir);
        string passFile=Path.Combine(outDir,"ime-probe-PASS.txt"),failFile=Path.Combine(outDir,"ime-probe-FAIL.txt");
        if(File.Exists(passFile))File.Delete(passFile);if(File.Exists(failFile))File.Delete(failFile);
        var report=new StringBuilder();bool all=true;
        Action<string,int,string> check=delegate(string name,int state,string detail){
            if(state<0)all=false;
            report.Append(state<0?"FAIL ":state>0?"PASS ":"SKIP ").Append(name).Append(": ").Append(detail).Append('\n');
        };
        bool old=ProbeDefaults.Live,oldSelfTest=Program.IsSelfTest;
        MainWindow window=null;Application application=null;ShutdownMode oldShutdown=ShutdownMode.OnExplicitShutdown;
        string file=Path.Combine(outDir,"ime-generated.md");
        try{
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            application=Application.Current;oldShutdown=application.ShutdownMode;application.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            ProbeDefaults.Live=true;
            File.WriteAllText(file,"# 제목 줄\n\n본문 줄입니다.\n\n<span style=\"font-size:32px\">크기글</span> 보통\n\n| 머리 | 둘째 |\n|---|---|\n| 칸글 | 끝 |\n",new UTF8Encoding(false));
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;window.Top=-20000;
            Invoke(window,"LoadFileCore",file);window.SetMode(true);Pump();
            var editor=ReadField<SourceEditor>(window,"source");
            // Every Korean case below asks for the Korean rule explicitly: the machine's own
            // thread layout must not decide what a probe tests.
            editor.NonKoreanImeLayout=delegate{return false;};
            bool code=ReadProperty<bool>(window,"CodeMode"),editing=ReadField<bool>(window,"editing"),attached=ReadProperty<bool>(window,"LivePreviewAttached");
            bool opened=code&&editing&&attached&&editor.Document.Text.Replace("\r\n","\n")=="# 제목 줄\n\n본문 줄입니다.\n\n<span style=\"font-size:32px\">크기글</span> 보통\n\n| 머리 | 둘째 |\n|---|---|\n| 칸글 | 끝 |\n";
            check("live source document",opened?1:-1,"code="+code+", editing="+editing+", preview="+attached+", lines="+editor.Document.LineCount);

            VerifyBoundaryFonts(editor,check);
            VerifyHiddenCompositionCaret(check);
            VerifyCompositionCaretParity(check);
            bool focused=FocusSource(window,editor);
            check("keyboard focus on source",focused?1:0,focused?"text area owns keyboard focus":"keyboard focus unavailable in this run");
            IntPtr hwnd=focused?WindowHandle(editor):IntPtr.Zero;
            int body=0,heading=0;
            if(hwnd==IntPtr.Zero){
                check("body composition font",0,"no window handle");
                check("heading composition font",0,"no window handle");
                check("composition font after refocus",0,"no window handle");
            }else{
                body=FontHeightAt(editor,hwnd,"본문",check,"body composition font");
                heading=FontHeightAt(editor,hwnd,"제목",check,"heading composition font");
                int span=FontHeightAt(editor,hwnd,"크기글",check,"sized span composition font");
                check("sized span em matches 32px",span!=0?(Math.Abs(span)==(int)Math.Round(32*System.Windows.Media.VisualTreeHelper.GetDpi(editor).DpiScaleY)?1:-1):0,"height="+span);
                check("heading em size is larger",body!=0&&heading!=0?(Math.Abs(heading)>Math.Abs(body)?1:-1):0,"body="+body+", heading="+heading);
                if(!RefocusCycle(editor))check("composition font after refocus",0,"keyboard focus could not leave and return");
                else{
                    hwnd=WindowHandle(editor);
                    int again=FontHeightAt(editor,hwnd,"본문",check,"composition font after refocus");
                    check("refocus keeps the body height",body!=0&&again!=0?(body==again?1:-1):0,"before="+body+", after="+again);
                }
            }
            // N8: the window's tunnelling PreviewMouseDown must ask for the composition commit
            // while the caret is still at the pre-click offset; AvalonEdit moves the caret from
            // the bubbling MouseDown, which runs after every preview handler.
            int preClick=CaretFor(editor,"본문");
            if(preClick<0)check("window preview commits the syllable",0,"text not found: 본문");
            else{
                editor.CaretIndex=preClick;editor.SimulateImeComposition();
                window.mouseDownCompositionCommitted=false;window.mouseDownCommitCaret=-1;editor.CompositionRequests=0;editor.CompositionRequestCaret=-1;
                var down=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left);
                down.RoutedEvent=UIElement.PreviewMouseDownEvent;down.Source=editor.TextArea.TextView;
                editor.TextArea.TextView.RaiseEvent(down);
                int committedAt=window.mouseDownCommitCaret;
                editor.CaretIndex=editor.Document.TextLength;
                bool ok=window.mouseDownCompositionCommitted&&committedAt==preClick&&editor.CompositionRequests>0;
                check("window preview commits the syllable before the caret moves",ok?1:-1,
                    "window commit="+window.mouseDownCompositionCommitted+", commit caret="+committedAt+", pre-click caret="+preClick+", requests="+editor.CompositionRequests);
                // Finding 1: the commit result arrives after the click moved the caret. It must land
                // at the remembered pre-click offset and carry the clicked caret along by its length.
                int length=editor.Document.TextLength;
                editor.SimulateImeCommit(1);
                editor.TextArea.PerformTextInput("각");
                bool atTarget=editor.Document.TextLength==length+1&&editor.Document.GetText(preClick,1)=="각";
                bool keptCaret=editor.CaretIndex==length+1;
                check("late commit result lands at the pre-click caret",atTarget&&keptCaret?1:-1,
                    "pre-click caret="+preClick+", length "+length+" -> "+editor.Document.TextLength+", caret="+editor.CaretIndex);
                editor.ResetCompositionSimulation();
            }
            VerifyCompositionWindowTarget(editor,check);
            VerifyCommittedHistory(window,editor,check);
            VerifyInternationalComposition(window,editor,check);
            // Last: it replaces the generated fixture with its own paragraph text.
            VerifyImeKeyOrdering(window,editor,check);
        }catch(Exception ex){check("exception",-1,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            if(window!=null){
                try{WriteField(window,"saved",ReadProperty<string>(window,"text"));window.Close();}
                catch(Exception ex){check("close",-1,ex.Message.Replace('\r',' ').Replace('\n',' '));}
            }
            ProbeDefaults.Live=old;Program.IsSelfTest=oldSelfTest;
            if(application!=null)application.ShutdownMode=oldShutdown;
            try{if(File.Exists(file))File.Delete(file);}catch(IOException ex){check("cleanup",-1,ex.Message.Replace('\r',' ').Replace('\n',' '));}
        }
        File.WriteAllText(all?passFile:failFile,report.ToString(),new UTF8Encoding(false));
        return all?0:1;
    }
    // IMM may send END before the final RESULTSTR (Korean), or after it. Unlike
    // SimulateImeCommit, this uses the native hook's state transition and never clears its flags
    // between result delivery and undo. Only the native result text and composition-string length
    // are supplied by the test. Each order must end with every composition flag released and the
    // real Ctrl+Z branch (Keys -> RestoreEditorHistory(false)) changing the document.
    static void VerifyCommittedHistory(MainWindow window,SourceEditor editor,Action<string,int,string> check){
        MethodInfo message=typeof(SourceEditor).GetMethod("ImeProc",Members);
        MethodInfo composition=typeof(SourceEditor).GetMethod("NoteImeComposition",Members);
        if(message==null||composition==null){check("ime message seam",-1,"SourceEditor.ImeProc/NoteImeComposition not found");return;}
        IntPtr hwnd=WindowHandle(editor);
        foreach(bool endFirst in new[]{false,true}){
            string name=endFirst?"Korean END then RESULTSTR":"RESULTSTR then END";
            ResetHistoryCase(window,editor);
            foreach(char value in "abc")editor.TextArea.PerformTextInput(value.ToString());
            message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
            string before=editor.Document.Text;
            Invoke(window,"RestoreEditorHistory",false);
            check(name+" active composition still guards undo",editor.Document.Text==before&&editor.ImeBusy?1:-1,"busy="+editor.ImeBusy+", unchanged="+(editor.Document.Text==before));
            if(endFirst)message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
            composition.Invoke(editor,new object[]{(long)0x0800,"가",0});
            check(name+" pending result guards store",editor.ImeBusy&&(bool)Invoke(window,"StoreGuardLive")?1:-1,"busy="+editor.ImeBusy+", "+editor.CompositionState());
            editor.TextArea.PerformTextInput("가");
            if(!endFirst)message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
            check(name+" committed result releases store immediately",SourceIdle(window,editor)?1:-1,editor.CompositionState());
            before=editor.Document.Text;
            Invoke(window,"RestoreEditorHistory",false); // Same target as Keys' Ctrl+Z branch.
            bool undone=before=="abc가"&&editor.Document.Text!=before;
            check(name+" Korean undo changes text",undone?1:-1,"before="+before+", after="+editor.Document.Text);
            check(name+" Korean undo leaves no composition flags",undone&&SourceIdle(window,editor)?1:-1,editor.CompositionState());
            Invoke(window,"RestoreEditorHistory",true);
            check(name+" Korean redo restores text",undone&&editor.Document.Text==before?1:-1,"text="+editor.Document.Text);
            foreach(char value in "def")editor.TextArea.PerformTextInput(value.ToString());
            before=editor.Document.Text;
            Invoke(window,"RestoreEditorHistory",false);
            undone=before=="abc가def"&&editor.Document.Text!=before;
            check(name+" later English Ctrl+Z changes text",undone?1:-1,"before="+before+", after="+editor.Document.Text);
            Invoke(window,"Redo");
            check(name+" later English toolbar redo restores text",undone&&editor.Document.Text==before?1:-1,"text="+editor.Document.Text);
            Invoke(window,"Undo");
            check(name+" toolbar undo changes text",editor.Document.Text!=before&&SourceIdle(window,editor)?1:-1,editor.CompositionState());
        }
        VerifyResultOnlyMessage(window,editor,check,message,hwnd);
        VerifyUnknownResultSafety(window,editor,check,message,composition,hwnd);
        VerifyPendingStyleCommitUndo(window,editor,check,message,composition,hwnd);
        VerifySpanCompositionBackground(window,editor,check);
        VerifyResultTerminatorUndo(window,editor,check,message,composition,hwnd);
        VerifyResultWithoutEndUndo(window,editor,check,message,composition,hwnd);
        VerifyClickCommitCaret(window,editor,check,message,composition,hwnd);
        VerifyClauseCommitUndo(window,editor,check,message,composition,hwnd);
        editor.ResetCompositionSimulation();
    }
    // Real Japanese MS-IME (2026-10-30): with a pending style armed (Ctrl+B on a collapsed caret)
    // the first committed character is written by the pending-style command, which sets Handled in
    // TextEntering and therefore raises no TextEntered. The counted result must be taken when the
    // text enters; when it was taken only on TextEntered, one character stayed counted after the
    // commit, ImeBusy stayed true, and every Ctrl+Z and toolbar undo press was skipped by
    // RestoreEditorHistory's store guard until one more character was typed. Undo must work as
    // soon as the commit is done.
    static void VerifyPendingStyleCommitUndo(MainWindow window,SourceEditor editor,Action<string,int,string> check,MethodInfo message,MethodInfo composition,IntPtr hwnd){
        ResetHistoryCase(window,editor);
        Invoke(window,"ToggleFormat","bold");
        var armField=typeof(MainWindow).GetField("typingOpen",Members);
        bool armed=armField!=null&&"**".Equals(armField.GetValue(window));
        message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        composition.Invoke(editor,new object[]{(long)GCS_RESULTSTR,"日本語",0});
        foreach(char value in "日本語")editor.TextArea.PerformTextInput(value.ToString());
        message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        Pump();
        string typed=editor.Document.Text;
        check("pending style wraps the first committed character",armed&&typed=="**日本語**"?1:-1,"armed="+armed+", typed=["+typed+"]");
        bool idle=SourceIdle(window,editor);
        check("Japanese commit under a pending style releases the store",idle?1:-1,editor.CompositionState());
        string before=editor.Document.Text;
        Invoke(window,"RestoreEditorHistory",false);
        check("undo works right after a Japanese commit under a pending style",idle&&editor.Document.Text!=before?1:-1,
            "before=["+before+"], after=["+editor.Document.Text+"], "+editor.CompositionState());
    }
    // 11-09 user: the first syllable typed after the highlight / inline-code button kept the span's box while composing (the armed style),
    // every later composing syllable was drawn without it. The box lives on the line element (MarkdownTransformer's BackgroundBrush) and the
    // run properties the composition copies carry only typeface and colour, so the composition element itself must be formatted with the box
    // (and the inline-code face) of the span it starts in. SetImeInline draws the string without a real IME context - the same seam a
    // composition uses - and the drawn element answers the background and face it was formatted with.
    static void VerifySpanCompositionBackground(MainWindow window,SourceEditor editor,Action<string,int,string> check){
        MethodInfo set=null;
        foreach(MethodInfo candidate in typeof(SourceEditor).GetMethods(Members))
            if(candidate.Name=="SetImeInline"&&candidate.GetParameters().Length==1&&candidate.GetParameters()[0].ParameterType==typeof(string)){set=candidate;break;}
        if(set==null){check("span composition background seam",-1,"SetImeInline(string) not found");return;}
        window.SetText("문단 ==가== 끝\n\n문단 `가` 끝\n");window.SetMode(true);
        if(!PumpBounded(2000)){check("span composition background setup",-1,"dispatcher did not become idle within 2 s");return;}
        /* The palette of the attached session: the session is created by the mode switch above, so it is read after it. */
        FieldInfo previewField=typeof(MainWindow).GetField("livePreview",Members);
        var live=previewField==null?null:previewField.GetValue(window) as MarkdownLivePreview.Session;
        if(live==null){check("span composition background seam",-1,"livePreview not found");return;}
        try{
            var document=editor.Document;
            var highlightLine=document.GetLineByNumber(1);
            string highlightText=document.GetText(highlightLine.Offset,highlightLine.Length);
            int highlightAt=highlightLine.Offset+highlightText.IndexOf("==")+3;
            editor.Select(highlightAt,0);editor.TextArea.Caret.BringCaretToView();PumpBounded(2000);
            set.Invoke(editor,new object[]{"나"});
            PumpBounded(2000);
            ImeInlineElement drawn=FindImeElement(editor);
            check("a later composing syllable inside ==x== keeps the highlight box",
                drawn!=null&&Object.ReferenceEquals(drawn.DrawnBackground,live.Colors.Highlight)?1:-1,
                "drawn="+DescribeBrush(drawn==null?null:drawn.DrawnBackground)+", expected="+DescribeBrush(live.Colors.Highlight)+", caret="+highlightAt);
            set.Invoke(editor,new object[]{null});
            var codeLine=document.GetLineByNumber(3);
            string codeText=document.GetText(codeLine.Offset,codeLine.Length);
            int codeAt=codeLine.Offset+codeText.IndexOf('`')+2;
            editor.Select(codeAt,0);editor.TextArea.Caret.BringCaretToView();PumpBounded(2000);
            set.Invoke(editor,new object[]{"나"});
            PumpBounded(2000);
            drawn=FindImeElement(editor);
            bool box=drawn!=null&&Object.ReferenceEquals(drawn.DrawnBackground,live.Colors.InlineCode);
            bool face=drawn!=null&&drawn.DrawnFace!=null&&drawn.DrawnFace.FontFamily.Source==MarkdownLivePreview.InlineCodeFamily.Source;
            check("a later composing syllable inside `x` keeps the code box and face",box&&face?1:-1,
                "drawn="+DescribeBrush(drawn==null?null:drawn.DrawnBackground)+", expected="+DescribeBrush(live.Colors.InlineCode)+
                ", face="+(drawn==null||drawn.DrawnFace==null?"none":drawn.DrawnFace.FontFamily.Source));
            // 11-09 user (screen recording): with a style active, Korean "테스트", a SPACE and then the first syllable after it: that syllable
            // composed plain (no box, no italic/code face) although its commit lands inside the pair. The closing-edge space rule keeps the typed
            // space outside the emphasis/code closers and pulls the next character back inside on commit, so the composition must read the pair's
            // style at the space's inside edge, the spot the toolbar and the commit use, not the plain space it is drawn after.
            set.Invoke(editor,new object[]{null});
            string spaceText;
            ImeInlineElement composed=ComposeAfterCloseSpace(window,editor,set,"문단 ==가==","==",out spaceText);
            bool spaceBox=composed!=null&&Object.ReferenceEquals(composed.DrawnBackground,live.Colors.Highlight);
            check("a composing syllable after the == pair's space keeps the highlight box",
                spaceBox&&spaceText.Replace("\r\n","\n")=="문단 ==가==  끝\n"?1:-1,
                "text=["+spaceText.Replace('\r',' ').Replace('\n','|')+"], drawn="+DescribeBrush(composed==null?null:composed.DrawnBackground)+
                ", expected="+DescribeBrush(live.Colors.Highlight));
            // The box must be one piece: the moved space before the composing string carries the same span brush, so
            // TextView.OnRender merges it with the span and the string (otherwise [box 가] [plain space] [box 나]).
            VisualLineElement spaceElement=FindVisualElementAt(editor,editor.CaretOffset-1);
            bool spaceElementBoxed=spaceElement!=null&&Object.ReferenceEquals(spaceElement.BackgroundBrush,live.Colors.Highlight);
            check("the pending space after the == pair takes the highlight box while composing",
                spaceElementBoxed?1:-1,
                "space="+DescribeBrush(spaceElement==null?null:spaceElement.BackgroundBrush)+", expected="+DescribeBrush(live.Colors.Highlight)+
                ", element="+(spaceElement==null?"none":spaceElement.GetType().Name)+", caret="+editor.CaretOffset+CloseSpaceWhy(live,editor));
            set.Invoke(editor,new object[]{null});
            composed=ComposeAfterCloseSpace(window,editor,set,"문단 *가*","*",out spaceText);
            bool spaceItalic=composed!=null&&composed.DrawnFace!=null&&composed.DrawnFace.Style==FontStyles.Italic;
            check("a composing syllable after the * pair's space keeps the italic run",
                spaceItalic&&spaceText.Replace("\r\n","\n")=="문단 *가*  끝\n"?1:-1,
                "text=["+spaceText.Replace('\r',' ').Replace('\n','|')+"], face="+(composed==null||composed.DrawnFace==null?"none":composed.DrawnFace.Style.ToString()));
            set.Invoke(editor,new object[]{null});
            composed=ComposeAfterCloseSpace(window,editor,set,"문단 `가`","`",out spaceText);
            bool spaceCodeBox=composed!=null&&Object.ReferenceEquals(composed.DrawnBackground,live.Colors.InlineCode);
            bool spaceCodeFace=composed!=null&&composed.DrawnFace!=null&&composed.DrawnFace.FontFamily.Source==MarkdownLivePreview.InlineCodeFamily.Source;
            check("a composing syllable after the ` pair's space keeps the code box and face",
                spaceCodeBox&&spaceCodeFace&&spaceText.Replace("\r\n","\n")=="문단 `가`  끝\n"?1:-1,
                "text=["+spaceText.Replace('\r',' ').Replace('\n','|')+"], drawn="+DescribeBrush(composed==null?null:composed.DrawnBackground)+
                ", face="+(composed==null||composed.DrawnFace==null?"none":composed.DrawnFace.FontFamily.Source));
            spaceElement=FindVisualElementAt(editor,editor.CaretOffset-1);
            bool spaceCodeElementBoxed=spaceElement!=null&&Object.ReferenceEquals(spaceElement.BackgroundBrush,live.Colors.InlineCode);
            check("the pending space after the ` pair takes the code box while composing",
                spaceCodeElementBoxed?1:-1,
                "space="+DescribeBrush(spaceElement==null?null:spaceElement.BackgroundBrush)+", expected="+DescribeBrush(live.Colors.InlineCode)+
                ", element="+(spaceElement==null?"none":spaceElement.GetType().Name)+", caret="+editor.CaretOffset+CloseSpaceWhy(live,editor));
        }catch(Exception ex){check("span composition background",-1,ex.GetType().Name+": "+ex.Message);}
        finally{
            try{set.Invoke(editor,new object[]{null});}catch(Exception){}
            editor.ResetCompositionSimulation();
            PumpBounded(2000);
        }
    }
    // Builds the closing-edge space state the user recorded: one formatted character in a pair, the caret on its inside edge, a typed SPACE
    // (the rule keeps it outside the pair and arms the pull-back), then a composition string started after that space. `text` is the document
    // text after the space, so the caller can prove the space really moved outside. Returns the drawn composition element, null when none.
    static ImeInlineElement ComposeAfterCloseSpace(MainWindow window,SourceEditor editor,MethodInfo set,string fixture,string marker,out string text){
        text="";
        window.SetText(fixture+" 끝\n");window.SetMode(true);
        if(!PumpBounded(2000))return null;
        var document=editor.Document;var line=document.GetLineByNumber(1);
        string lineText=document.GetText(line.Offset,line.Length);
        int open=lineText.IndexOf(marker);
        if(open<0)return null;
        // Inside edge of the closing marker: the opener, the one content character, then the closer.
        int caret=line.Offset+open+marker.Length+1;
        editor.Select(caret,0);editor.TextArea.Caret.BringCaretToView();PumpBounded(2000);
        editor.TextArea.PerformTextInput(" ");
        PumpBounded(2000);
        text=document.Text;
        set.Invoke(editor,new object[]{"나"});
        PumpBounded(2000);
        return FindImeElement(editor);
    }
    // The composition element drawn at the caret (ImeInlineGenerator built it for that offset), null when none is drawn yet.
    static ImeInlineElement FindImeElement(SourceEditor editor){
        var view=editor.TextArea.TextView;
        view.EnsureVisualLines();
        if(!view.VisualLinesValid)return null;
        int offset=editor.CaretOffset;
        foreach(var visual in view.VisualLines){
            if(visual==null||visual.IsDisposed||visual.Elements==null)continue;
            foreach(var element in visual.Elements){var ime=element as ImeInlineElement;if(ime!=null&&ime.BuiltOffset==offset)return ime;}
        }
        return null;
    }
    // The visual element that covers a document offset (the pending closing-edge space), null when no built line owns it.
    // Why CloseSpaceBoxRange answered no: the pending-space fields and each condition it reads (diagnostic text for a FAIL line).
    static string CloseSpaceWhy(MarkdownLivePreview.Session live,SourceEditor editor){
        try{
            var t=typeof(MarkdownLivePreview.Session);var f=BindingFlags.Instance|BindingFlags.NonPublic;
            Func<string,object> get=delegate(string name){var fi=t.GetField(name,f);return fi==null?(object)"?":fi.GetValue(live);};
            int caret=editor.CaretOffset;int start=(int)get("closeSpaceStart");int spaceCaret=(int)get("closeSpaceCaret");
            var line=editor.Document.GetLineByOffset(caret);int s0,s1,s2;System.Windows.Media.Brush b;
            return " why[pending="+get("closeSpacePending")+" caretLine="+get("caretLine")+"/"+line.LineNumber+" start="+start+" end="+get("closeSpaceEnd")+
                " spaceCaret="+spaceCaret+" caret="+caret+" drawn="+editor.ImeDrawnAt(spaceCaret)+
                " styleAt="+live.ImeStyleOffsetAt(spaceCaret)+" bg="+DescribeBrush(live.ImeSpanBackgroundAt(start))+
                " range="+live.CloseSpaceBoxRange(line,out s0,out s1,out s2,out b)+"]";
        }catch(Exception ex){return " why["+ex.GetType().Name+": "+ex.Message+"]";}
    }
    static VisualLineElement FindVisualElementAt(SourceEditor editor,int offset){
        var view=editor.TextArea.TextView;
        view.EnsureVisualLines();
        if(!view.VisualLinesValid)return null;
        foreach(var visual in view.VisualLines){
            if(visual==null||visual.IsDisposed||visual.Elements==null)continue;
            int first=visual.FirstDocumentLine.Offset;
            foreach(var element in visual.Elements){
                if(element.DocumentLength<=0)continue;
                int start=first+element.RelativeTextOffset;
                if(offset>=start&&offset<start+element.DocumentLength)return element;
            }
        }
        return null;
    }
    static string DescribeBrush(System.Windows.Media.Brush brush){return brush==null?"null":brush.ToString();}
    // An IME may report GCS_RESULTSTR with the terminating NUL counted in its byte length. The
    // counted result must be the characters, not the reported bytes: one extra unit left ImeBusy
    // true and parked undo after an ordinary commit. The probe passes the raw string with the NUL.
    static void VerifyResultTerminatorUndo(MainWindow window,SourceEditor editor,Action<string,int,string> check,MethodInfo message,MethodInfo composition,IntPtr hwnd){
        ResetHistoryCase(window,editor);
        message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        composition.Invoke(editor,new object[]{(long)GCS_RESULTSTR,"日本語\0",0});
        foreach(char value in "日本語")editor.TextArea.PerformTextInput(value.ToString());
        message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        Pump();
        check("a NUL-terminated result counts only its characters",SourceIdle(window,editor)&&IntlPending(editor)==0?1:-1,
            "pending="+IntlPending(editor)+", "+editor.CompositionState());
        string before=editor.Document.Text;
        Invoke(window,"RestoreEditorHistory",false);
        check("undo works right after a NUL-terminated result",editor.Document.Text!=before?1:-1,
            "before=["+before+"], after=["+editor.Document.Text+"]");
    }
    // Some IMEs deliver the result and never send WM_IME_ENDCOMPOSITION. The composition latch
    // must still be released before undo once every counted character is in and the context holds
    // no composition string: an END that never comes must not park undo.
    static void VerifyResultWithoutEndUndo(MainWindow window,SourceEditor editor,Action<string,int,string> check,MethodInfo message,MethodInfo composition,IntPtr hwnd){
        ResetHistoryCase(window,editor);
        message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        composition.Invoke(editor,new object[]{(long)(GCS_RESULTSTR|GCS_COMPSTR),"가",1});
        editor.TextArea.PerformTextInput("가");
        string before=editor.Document.Text;
        Invoke(window,"RestoreEditorHistory",false);
        bool undone=before=="가"&&editor.Document.Text!=before;
        check("a result without END still lets undo run",undone?1:-1,"before=["+before+"], after=["+editor.Document.Text+"], "+editor.CompositionState());
        check("a result without END leaves no composition flags after undo",undone&&SourceIdle(window,editor)?1:-1,editor.CompositionState());
    }
    // A press while composing asks for the commit and writes the text at the composition point.
    // Caret placement belongs to the stock handler (bubbling press) or the button-state path; the
    // probe has no pointer, so it only checks that the synthetic preview press leaves the caret.
    static void VerifyClickCommitCaret(MainWindow window,SourceEditor editor,Action<string,int,string> check,MethodInfo message,MethodInfo composition,IntPtr hwnd){
        ResetHistoryCase(window,editor);
        window.SetText("첫 줄\n\n둘째 줄입니다\n");window.SetMode(true);
        if(!PumpBounded(2000)){check("a click commit probe setup",-1,"dispatcher did not become idle within 2 s");return;}
        var document=editor.Document;
        int compositionAt=document.GetLineByNumber(3).EndOffset;
        editor.Select(compositionAt,0);editor.TextArea.Caret.BringCaretToView();PumpBounded(2000);
        editor.SimulateImeComposition();
        message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        composition.Invoke(editor,new object[]{(long)GCS_COMPSTR,null,2});
        // The press itself no longer places the caret: a real press that WPF receives is moved by the
        // stock bubbling handler (the probe has no pointer), and one the IME consumes is handled by
        // the button-state path (needs the real button). The synthetic preview press only asks for
        // the commit and must leave the caret alone.
        RaiseIntlMouseDown(editor);
        int placed=editor.CaretIndex;
        check("a click commit press does not move the caret by itself",placed==compositionAt?1:-1,"caret="+placed+", composition="+compositionAt);
        composition.Invoke(editor,new object[]{(long)GCS_RESULTSTR,"漢字",0});
        editor.TextArea.PerformTextInput("漢字");
        message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        bool idle=PumpBounded(2000);
        string text=editor.Document.Text;
        bool atComposition=compositionAt+2<=text.Length&&text.Substring(compositionAt,2)=="漢字";
        check("a click commit writes the text at the composition point",atComposition?1:-1,"text=["+text.Replace('\r',' ').Replace('\n','|')+"], at="+compositionAt);
        check("a click commit leaves no composition flags",idle&&SourceIdle(window,editor)?1:-1,"idle="+idle+", "+editor.CompositionState());
        // The former placement checks (right of a line end, under the last line) tested the removed
        // PreviewMouseDown placement (ClickProbePoint); the product no longer has that path.
        editor.ResetCompositionSimulation();
    }
    // A live composition owns the insertion point until its counted result arrives. A window key
    // handler that asked for a commit on a key still belonging to the composition made the capture
    // fall back to CPS_COMPLETE: the letter then reached TextInput first, landed in front of the
    // pending syllable, and the result followed it (real Japanese MS-IME, basic view: に + h +
    // Enter was saved as hにおん, and ほ became お because h never joined the reading). The key must
    // stay the IME's, every composition update must leave the source alone, and the commit must
    // append the whole word after the existing text.
    static void VerifyImeKeyOrdering(MainWindow window,SourceEditor editor,Action<string,int,string> check){
        MethodInfo message=typeof(SourceEditor).GetMethod("ImeProc",Members);
        MethodInfo composition=typeof(SourceEditor).GetMethod("NoteImeComposition",Members);
        if(message==null||composition==null){check("ime key ordering seam",-1,"SourceEditor.ImeProc/NoteImeComposition not found");return;}
        IntPtr hwnd=WindowHandle(editor);
        ResetHistoryCase(window,editor);
        window.SetText("first paragraph\n\nsecond paragraph\n\nthird paragraph\n");window.SetMode(true);
        // The reported input was the basic (no-reveal) view; the source/caret path is the same.
        Invoke(window,"SetLiveMode",false);Pump();
        if(!PumpBounded(2000)){check("ime key ordering setup",-1,"dispatcher did not become idle within 2 s");return;}
        var document=editor.Document;
        editor.Select(document.TextLength,0);editor.TextArea.Caret.BringCaretToView();PumpBounded(2000);
        string before=document.Text;
        // A counted result keeps ImeBusy true across the key event: RefreshCompositionState would
        // clear a simulated composition that has no native IMM context behind it.
        editor.CompositionRequests=0;
        editor.SimulateImeCommit(3);
        editor.SimulateImeComposition();
        Func<bool> oldFocus=MainWindow.KeyboardCommandFocus;
        System.Windows.Input.ModifierKeys? oldModifiers=MainWindow.ShortcutModifiersProbe;
        try{
            MainWindow.KeyboardCommandFocus=delegate{return true;};
            // Even a polluted modifier state must not turn an IME-owned key into a shortcut.
            MainWindow.ShortcutModifiersProbe=System.Windows.Input.ModifierKeys.Control;
            var args=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(editor),0,System.Windows.Input.Key.ImeProcessed);
            args.RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent;
            editor.TextArea.RaiseEvent(args);
        }finally{
            MainWindow.KeyboardCommandFocus=oldFocus;
            MainWindow.ShortcutModifiersProbe=oldModifiers;
        }
        check("an IME-processed key asks for no commit",editor.CompositionRequests==0?1:-1,
            "requests="+editor.CompositionRequests+", "+editor.CompositionState());
        check("an IME-processed key leaves the source alone",document.Text==before?1:-1,
            "length "+before.Length+" -> "+document.TextLength);
        editor.ResetCompositionSimulation();
        try{
            editor.SimulateImeComposition();
            message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
            // The reading the IME draws: に, にh, にほ, にほn, にほん. Only lengths reach the product.
            int[] reading=new int[]{1,2,2,3,3};
            for(int i=0;i<reading.Length;i++){
                composition.Invoke(editor,new object[]{(long)GCS_COMPSTR,null,reading[i]});
                check("composition update "+(i+1)+" inserts nothing",document.Text==before?1:-1,
                    "length "+before.Length+" -> "+document.TextLength);
            }
            composition.Invoke(editor,new object[]{(long)GCS_RESULTSTR,"にほん",0});
            editor.TextArea.PerformTextInput("にほん");
            message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
            bool idle=PumpBounded(2000);
            string after=document.Text;
            string expected=before+"にほん";
            // The fixture's own words contain 'h' ("paragraph", "third"), so the raw letter the
            // IME regression put in front of the syllable can only be detected as an *added* 'h',
            // never as a document without one. Compare counts with the pre-commit text; the exact
            // equality below still pins the committed word to the end.
            int strayH=IntlCount(after,"h")-IntlCount(before,"h");
            bool appended=String.Equals(after,expected,StringComparison.Ordinal)&&strayH==0;
            check("the committed word lands after the existing text",appended?1:-1,
                "appended="+appended+", "+IntlDiff(after,expected)+", stray h="+strayH+", idle="+idle+", "+editor.CompositionState());
            check("the composition commit leaves no busy state",idle&&SourceIdle(window,editor)?1:-1,editor.CompositionState());
        }finally{editor.ResetCompositionSimulation();}
    }
    // Pump with a cap: false when the dispatcher did not reach ApplicationIdle within the time.
    static bool PumpBounded(int milliseconds){
        var frame=new DispatcherFrame();
        bool idle=false;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{idle=true;frame.Continue=false;}));
        var timer=new DispatcherTimer(DispatcherPriority.Normal);
        timer.Interval=TimeSpan.FromMilliseconds(milliseconds);
        timer.Tick+=delegate{timer.Stop();frame.Continue=false;};
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        return idle;
    }
    // A non-Korean IME commits clause by clause, and the typing rule keeps the continuous run as
    // one undo step: two commits typed back to back share it even with no whitespace between them
    // (real Japanese MS-IME: 日本語 then てすと). The rule no longer differs by language, so the
    // Korean sequence below ends in the same single step.
    static void VerifyClauseCommitUndo(MainWindow window,SourceEditor editor,Action<string,int,string> check,MethodInfo message,MethodInfo composition,IntPtr hwnd){
        string[] clauses={"日本語","てすと"};
        try{
            editor.NonKoreanImeLayout=delegate{return true;};
            ResetHistoryCase(window,editor);
            CommitClauses(editor,message,composition,hwnd,clauses);
            string typed=editor.Document.Text;
            Invoke(window,"RestoreEditorHistory",false);
            string one=editor.Document.Text;
            Invoke(window,"RestoreEditorHistory",false);
            string two=editor.Document.Text;
            check("consecutive Japanese clause commits are one undo step",typed==clauses[0]+clauses[1]&&one==""&&two==""?1:-1,
                "typed=["+typed+"], once=["+one+"], twice=["+two+"]");
            Invoke(window,"RestoreEditorHistory",true);Invoke(window,"RestoreEditorHistory",true);
            check("redo restores both Japanese clauses",editor.Document.Text==typed?1:-1,"text=["+editor.Document.Text+"]");
            // The same sequence under the Korean layout merges the same way: one step.
            editor.NonKoreanImeLayout=delegate{return false;};
            ResetHistoryCase(window,editor);
            CommitClauses(editor,message,composition,hwnd,clauses);
            typed=editor.Document.Text;
            Invoke(window,"RestoreEditorHistory",false);
            check("Korean commits merge into the same single step",typed==clauses[0]+clauses[1]&&editor.Document.Text==""?1:-1,
                "typed=["+typed+"], after=["+editor.Document.Text+"]");
        }finally{editor.NonKoreanImeLayout=delegate{return false;};}
    }
    static void CommitClauses(SourceEditor editor,MethodInfo message,MethodInfo composition,IntPtr hwnd,string[] clauses){
        foreach(string clause in clauses){
            message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
            composition.Invoke(editor,new object[]{(long)GCS_RESULTSTR,clause,0});
            foreach(char value in clause)editor.TextArea.PerformTextInput(value.ToString());
            message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
            Pump();
        }
    }
    static Point ClickPoint(SourceEditor editor,int offset){
        var view=editor.TextArea.TextView;
        view.EnsureVisualLines();
        var position=new ICSharpCode.AvalonEdit.TextViewPosition(editor.Document.GetLocation(offset));
        Point point=view.GetVisualPosition(position,ICSharpCode.AvalonEdit.Rendering.VisualYPosition.TextTop)-view.ScrollOffset;
        return new Point(point.X+2,point.Y+2);
    }
    // The real WM_IME_COMPOSITION branch of the hook: a GCS_RESULTSTR message whose window
    // context no longer holds a composition string (the state the Korean END first leaves) must
    // not arm the latch. No result text is delivered here, so only the state decision is tested.
    static void VerifyResultOnlyMessage(MainWindow window,SourceEditor editor,Action<string,int,string> check,MethodInfo message,IntPtr hwnd){
        if(hwnd==IntPtr.Zero){check("result-only message leaves no composition latch",0,"no window handle");return;}
        ResetHistoryCase(window,editor);
        foreach(char value in "abc")editor.TextArea.PerformTextInput(value.ToString());
        message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        message.Invoke(editor,new object[]{hwnd,WM_IME_COMPOSITION,IntPtr.Zero,new IntPtr(0x0800),false});
        check("result-only message leaves no composition latch",SourceIdle(window,editor)?1:-1,editor.CompositionState());
        string before=editor.Document.Text;
        Invoke(window,"RestoreEditorHistory",false);
        check("result-only message keeps undo working",editor.Document.Text!=before&&SourceIdle(window,editor)?1:-1,"before="+before+", after="+editor.Document.Text);
    }
    // ImmGetContext can fail while a result message is in flight. The latch then stays conservative
    // (the old behaviour), but the pre-undo safety net must release it once END was seen and the
    // context holds no composition string; one lost context must not block every later undo.
    static void VerifyUnknownResultSafety(MainWindow window,SourceEditor editor,Action<string,int,string> check,MethodInfo message,MethodInfo composition,IntPtr hwnd){
        ResetHistoryCase(window,editor);
        foreach(char value in "abc")editor.TextArea.PerformTextInput(value.ToString());
        message.Invoke(editor,new object[]{hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        message.Invoke(editor,new object[]{hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});
        composition.Invoke(editor,new object[]{(long)0x0800,"가",-1});
        editor.TextArea.PerformTextInput("가");
        check("unknown native length keeps the result guarded",editor.ImeBusy&&(bool)Invoke(window,"StoreGuardLive")?1:-1,editor.CompositionState());
        string before=editor.Document.Text;
        Invoke(window,"RestoreEditorHistory",false);
        bool undone=before=="abc가"&&editor.Document.Text!=before;
        check("ended composition releases the unknown result before undo",undone&&SourceIdle(window,editor)?1:-1,"before="+before+", after="+editor.Document.Text+", "+editor.CompositionState());
    }
    // ------------------------------------------------------------ Japanese, Pinyin, Bopomofo, ChangJie
    // Synthetic IMM sequences for IMEs the Korean cases never covered. The native hook decides the
    // composition state from the result text and composition string the IME would report for that
    // message (NoteImeComposition is the state half of WM_IME_COMPOSITION); the committed text then
    // arrives through AvalonEdit's own TextInput path (PerformTextInput), which is what a real
    // WM_CHAR does. This cannot prove
    // the physical candidate window, the glyph IMM draws, or the reading->candidate conversion
    // itself. It proves the state machine, the insertion offset for multi-character results, the
    // Basic view's marker hiding, the table-cell caret and the history for every sequence.
    const int IntlVariants=16; // 0..13 and 15 run everywhere, 14 (pending space) only for the bold pair
    sealed class IntlCase {
        internal MainWindow Window;internal SourceEditor Editor;internal MarkdownLivePreview.Session Session;
        internal MethodInfo Message,Composition;internal IntPtr Handle;internal Action<string,int,string> Check;
        internal string Name,Fixture;internal int At;internal int Surface;internal bool Basic;
        internal string VisibleBefore;internal int[] OffsetsBefore;internal string Once;
    }
    static void VerifyInternationalComposition(MainWindow window,SourceEditor editor,Action<string,int,string> check){
        MethodInfo message=typeof(SourceEditor).GetMethod("ImeProc",Members);
        MethodInfo composition=typeof(SourceEditor).GetMethod("NoteImeComposition",Members);
        if(message==null||composition==null){check("international ime seam",-1,"SourceEditor.ImeProc/NoteImeComposition not found");return;}
        IntPtr hwnd=WindowHandle(editor);
        string fixture=InternationalFixture();
        bool oldHide=ReadStatic<bool>("hideMarkers");
        // Every international case is a non-Korean IME (Japanese, Pinyin, Bopomofo, ChangJie):
        // its committed clauses are word-unit undo steps. The machine's own thread layout must not
        // decide that for a probe.
        editor.NonKoreanImeLayout=delegate{return true;};
        try{
            for(int mode=0;mode<2;mode++){
                bool basic=mode==0;
                // The product's own switch: Basic keeps every line formatted, Live shows the caret line raw.
                Invoke(window,"SetLiveMode",!basic);Pump();
                for(int surface=0;surface<4;surface++){
                    for(int variant=0;variant<IntlVariants;variant++){
                        if(variant==14&&surface!=1)continue;
                        IntlCase c=PrepareIntlCase(window,editor,check,message,composition,hwnd,fixture,surface,basic,variant);
                        if(c==null)continue;
                        try{RunIntlCase(c,variant);}
                        catch(Exception ex){IntlCheck(c,-1,"exception "+ex.GetType().Name+": "+ex.Message.Replace('\r',' ').Replace('\n',' '));}
                        finally{editor.ResetCompositionSimulation();}
                    }
                }
            }
        }finally{
            try{SetHideMarkers(window,oldHide);}catch(Exception){}
            editor.NonKoreanImeLayout=delegate{return false;};
            editor.ResetCompositionSimulation();
        }
    }
    static string InternationalFixture(){
        return "# 제목\n\n문단 앞 문장\n\n**굵게 문장** 뒤\n\n<span style=\"font-size:20px\">크기 글</span> 보통\n\n| 머리 | 둘째 |\n|---|---|\n| 칸글 | 끝 |\n\n마지막 줄\n";
    }
    static string InternationalSurfaceName(int surface){
        switch(surface){case 0:return "paragraph";case 1:return "bold pair";case 2:return "20px span";default:return "table cell";}
    }
    static string InternationalCaseName(int variant){
        switch(variant){
            case 0:return "J1 conversion then commit";
            case 1:return "J2 partial then final commit";
            case 2:return "J3 escape cancel";
            case 3:return "J4a click commit";
            case 4:return "J4b menu late commit";
            case 5:return "J4c enter commit";
            case 6:return "J5 backspace to empty";
            case 7:return "C1 pinyin result";
            case 8:return "C1b punctuation result";
            case 9:return "C2 long result";
            case 10:return "T1a bopomofo in-place conversion";
            case 11:return "T1b bopomofo overflow commit";
            case 12:return "T2a changjie result";
            case 13:return "T2b space commit and punctuation";
            case 14:return "S1 pending space then commit";
            default:return "J4d click before the composition";
        }
    }
    // The caret the surface fixture gives each case: a paragraph end, the inner edge of the bold
    // pair's closers, the inner edge of the 20 px span's closing tag, and the first cell's end.
    static int InternationalAnchor(SourceEditor editor,MarkdownLivePreview.Session session,int surface,out string problem){
        problem=null;
        var document=editor.Document;
        for(int number=1;number<=document.LineCount;number++){
            var line=document.GetLineByNumber(number);string text=document.GetText(line);
            if(surface==0&&text=="문단 앞 문장")return line.EndOffset;
            if(surface==1){
                int pair=text.IndexOf("**굵게 문장**",StringComparison.Ordinal);
                if(pair>=0)return line.Offset+pair+"**굵게 문장".Length;
            }
            if(surface==2){
                int tag=text.IndexOf("</span>",StringComparison.Ordinal);
                if(tag>=0&&text.IndexOf("<span",StringComparison.Ordinal)>=0)return line.Offset+tag;
            }
            if(surface==3&&text.IndexOf("| 칸글 |",StringComparison.Ordinal)>=0){
                var info=session.Info(line);
                if(info.Table&&!info.Delimiter&&info.Cells!=null&&info.Cells.Count>=1)return line.Offset+info.Cells[0].End;
                problem="the table row was not parsed into cells";
            }
        }
        if(problem==null)problem="fixture line for the surface was not found";
        return -1;
    }
    static IntlCase PrepareIntlCase(MainWindow window,SourceEditor editor,Action<string,int,string> check,MethodInfo message,MethodInfo composition,IntPtr hwnd,string fixture,int surface,bool basic,int variant){
        editor.ResetCompositionSimulation();
        window.SetText(fixture);window.SetMode(true);Pump();
        string label=(basic?"basic ":"live ")+InternationalSurfaceName(surface)+" "+InternationalCaseName(variant);
        var session=ReadField<MarkdownLivePreview.Session>(window,"livePreview");
        if(session==null||session.IsDisposed){check(label,-1,"live preview session missing");return null;}
        string problem;int at=InternationalAnchor(editor,session,surface,out problem);
        if(at<0){check(label,-1,problem);return null;}
        editor.Select(at,0);editor.TextArea.Caret.BringCaretToView();Pump();
        if(editor.CaretIndex!=at){check(label,-1,"caret moved from the requested offset "+at+" to "+editor.CaretIndex);return null;}
        int[] offsets;string visible=session.VisibleText(out offsets);
        var c=new IntlCase();
        c.Window=window;c.Editor=editor;c.Session=session;c.Message=message;c.Composition=composition;c.Handle=hwnd;
        c.Check=check;c.Name=label;c.Fixture=fixture;c.At=at;c.Surface=surface;c.Basic=basic;c.VisibleBefore=visible;c.OffsetsBefore=offsets;
        return c;
    }
    static void IntlCheck(IntlCase c,int state,string detail){c.Check(c.Name,state,detail);}
    static void IntlStart(IntlCase c){c.Message.Invoke(c.Editor,new object[]{c.Handle,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});}
    static void IntlNote(IntlCase c,long flags,string resultText,int compositionLength){c.Composition.Invoke(c.Editor,new object[]{flags,resultText,compositionLength});}
    static void IntlDeliver(IntlCase c,string text){c.Editor.TextArea.PerformTextInput(text);}
    static void IntlEnd(IntlCase c){c.Message.Invoke(c.Editor,new object[]{c.Handle,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero,false});}
    static int IntlPending(SourceEditor editor){
        var field=typeof(SourceEditor).GetField("imeCommitPending",Members);
        return field==null?-1:(int)field.GetValue(editor);
    }
    static T ReadStatic<T>(string name){
        var field=typeof(MainWindow).GetField(name,Members);
        if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");
        return (T)field.GetValue(null);
    }
    static void SetHideMarkers(MainWindow window,bool hidden){
        var field=typeof(MainWindow).GetField("hideMarkers",Members);
        if(field==null)throw new MissingMemberException("probe: MainWindow has no field 'hideMarkers'");
        field.SetValue(null,hidden);Invoke(window,"UpdateLivePreview");Pump();
    }
    static void RaiseIntlMouseDown(SourceEditor editor){
        var down=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left);
        down.RoutedEvent=UIElement.PreviewMouseDownEvent;down.Source=editor.TextArea.TextView;
        editor.TextArea.TextView.RaiseEvent(down);
    }
    static int IntlInsertionIndex(int[] offsets,int at){
        if(offsets==null)return 0;
        for(int i=0;i<offsets.Length;i++)if(offsets[i]>=at)return i;
        return offsets.Length;
    }
    static int IntlCount(string text,string value){
        if(String.IsNullOrEmpty(value))return 0;
        int count=0,at=0;
        while(true){at=text.IndexOf(value,at,StringComparison.Ordinal);if(at<0)return count;count++;at+=value.Length;}
    }
    static string IntlDiff(string actual,string expected){
        int limit=Math.Min(actual.Length,expected.Length),p=0;
        while(p<limit&&actual[p]==expected[p])p++;
        if(p==limit&&actual.Length==expected.Length)return "equal";
        int from=Math.Max(0,p-8);
        string around=actual.Length>from?actual.Substring(from,Math.Min(20,actual.Length-from)):"";
        return "first difference at "+p+", expected length="+expected.Length+", actual length="+actual.Length+", actual around=["+around.Replace('\r',' ').Replace('\n','|')+"]";
    }
    static void RunIntlCase(IntlCase c,int variant){
        switch(variant){
            case 0:IntlJ1(c);break;
            case 1:IntlJ2(c);break;
            case 2:IntlJ3(c);break;
            case 3:IntlJ4a(c);break;
            case 4:IntlJ4b(c);break;
            case 5:IntlJ4c(c);break;
            case 6:IntlJ5(c);break;
            case 7:IntlC1(c);break;
            case 8:IntlC1b(c);break;
            case 9:IntlC2(c);break;
            case 10:IntlT1a(c);break;
            case 11:IntlT1b(c);break;
            case 12:IntlT2a(c);break;
            case 13:IntlT2b(c);break;
            case 14:IntlS1(c);break;
            default:IntlJ4d(c);break;
        }
    }
    // J1: Microsoft IME romaji. The reading grows, the conversion only changes the composition
    // string IMM draws, and one multi-character RESULTSTR reaches the document.
    static void IntlJ1(IntlCase c){
        const string committed="日本語を勉強";
        IntlStart(c);
        string[] reading={"に","にほ","にほん","にほんご","にほんごを","にほんごをべ","にほんごをべん","にほんごをべんきょ","にほんごをべんきょう"};
        for(int i=0;i<reading.Length;i++)IntlNote(c,GCS_COMPSTR,null,reading[i].Length);
        IntlCheck(c,c.Editor.Document.Text==c.Fixture&&c.Editor.ImeBusy?1:-1,"composition updates keep the source and guard commands (busy="+c.Editor.ImeBusy+")");
        string[] converted={"日本語","日本語を","日本語を勉","日本語を勉強"};
        for(int i=0;i<converted.Length;i++)IntlNote(c,GCS_COMPSTR,null,converted[i].Length);
        IntlCheck(c,c.Editor.Document.Text==c.Fixture?1:-1,"conversion replacement stays out of the document");
        IntlNote(c,GCS_RESULTSTR,committed,0);
        IntlDeliver(c,committed);
        IntlEnd(c);
        IntlFinish(c,committed,c.At+committed.Length,1);
    }
    // J2: a result and the next composition arrive in one message. Only the committed clause is
    // written, the counted result is consumed, and the following clause still owns the store.
    static void IntlJ2(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,9);
        IntlNote(c,GCS_RESULTSTR|GCS_COMPSTR,"日本語を",2);
        IntlDeliver(c,"日本語を");Pump();
        IntlCheck(c,c.Editor.Document.Text==c.Fixture.Insert(c.At,"日本語を")&&c.Editor.ImeBusy?1:-1,"partial commit writes its clause and keeps composing (busy="+c.Editor.ImeBusy+")");
        IntlCheck(c,IntlPending(c.Editor)==0?1:-1,"partial commit consumed its counted result (pending="+IntlPending(c.Editor)+")");
        IntlNote(c,GCS_COMPSTR,null,2);
        IntlNote(c,GCS_RESULTSTR,"勉強",0);
        IntlDeliver(c,"勉強");
        IntlEnd(c);
        IntlFinish(c,"日本語を勉強",c.At+6,2);
    }
    // J3: Esc empties the composition and nothing is written.
    static void IntlJ3(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,3);
        IntlNote(c,GCS_COMPSTR,null,0);
        IntlEnd(c);Pump();
        IntlCheck(c,c.Editor.Document.Text==c.Fixture&&c.Editor.CaretIndex==c.At?1:-1,"escape keeps the source and caret");
        IntlFinish(c,"",c.At,0);
    }
    // J4a: the window's tunnelling mouse-down asks for the commit at the pre-click caret; the
    // click moves the caret; the counted result may only land at the composition point.
    static void IntlJ4a(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,5);
        int preClick=c.Editor.CaretIndex;
        c.Window.mouseDownCompositionCommitted=false;c.Window.mouseDownCommitCaret=-1;
        RaiseIntlMouseDown(c.Editor);
        IntlCheck(c,c.Window.mouseDownCompositionCommitted&&c.Window.mouseDownCommitCaret==preClick?1:-1,"window mouse down asks for the commit at the pre-click caret (commit caret="+c.Window.mouseDownCommitCaret+", pre-click="+preClick+")");
        int clicked=c.Editor.Document.TextLength;
        c.Editor.Select(clicked,0);Pump();
        IntlNote(c,GCS_RESULTSTR,"日本語を",0);
        IntlDeliver(c,"日本語を");
        IntlEnd(c);
        IntlFinish(c,"日本語を",clicked>=preClick?clicked+4:clicked,1);
    }
    // J4b: the menu path. CommitCompositionForMenu calls BeginCompositionCommit while the text area
    // still owns focus; the popup then takes it and the counted result is adopted late at the
    // remembered caret instead of following the menu item that received the TextInput.
    static void IntlJ4b(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,3);
        c.Editor.BeginCompositionCommit();
        System.Windows.Input.Keyboard.ClearFocus();
        bool away=!c.Editor.TextArea.IsKeyboardFocusWithin;
        IntlNote(c,GCS_RESULTSTR,"日本語",0);
        bool adopted=false;
        if(away)adopted=c.Editor.AcceptLateImeCommit("日本語");
        else{c.Editor.TextArea.PerformTextInput("日本語");adopted=false;}
        IntlEnd(c);Pump();
        IntlCheck(c,away?(adopted?1:-1):0,"menu result adopted at the remembered caret (focus away="+away+", adopted="+adopted+")");
        try{c.Editor.TextArea.Focus();Pump();}catch(Exception){}
        IntlFinish(c,"日本語",c.At+3,1);
    }
    // J4d: the same click commit, but the click lands before the composition point. The result
    // still goes to the composition point; the clicked caret (in front of it) must not shift.
    static void IntlJ4d(IntlCase c){
        const string committed="日本語を";
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,5);
        int preClick=c.Editor.CaretIndex;
        c.Window.mouseDownCompositionCommitted=false;c.Window.mouseDownCommitCaret=-1;
        RaiseIntlMouseDown(c.Editor);
        IntlCheck(c,c.Window.mouseDownCompositionCommitted&&c.Window.mouseDownCommitCaret==preClick?1:-1,"window mouse down asks for the commit at the pre-click caret (commit caret="+c.Window.mouseDownCommitCaret+", pre-click="+preClick+")");
        // The paragraph line's start is a real caret stop in both views (offset 0 on the heading
        // line is not: the hidden heading prefix snaps the caret to its first visible character).
        int clicked=InternationalParagraphStart(c.Editor);
        c.Editor.Select(clicked,0);Pump();
        IntlCheck(c,c.Editor.CaretIndex==clicked?1:-1,"the click lands before the composition (caret="+c.Editor.CaretIndex+", requested="+clicked+")");
        IntlNote(c,GCS_RESULTSTR,committed,0);
        IntlDeliver(c,committed);
        IntlEnd(c);
        IntlFinish(c,committed,clicked,1);
    }
    static int InternationalParagraphStart(SourceEditor editor){
        var document=editor.Document;
        for(int number=1;number<=document.LineCount;number++){
            var line=document.GetLineByNumber(number);
            if(document.GetText(line)=="문단 앞 문장")return line.Offset;
        }
        return 0;
    }
    // J4c: Enter commits the composition (the IME consumes the key). The next Enter must reach the
    // editor and land after the committed text.
    static void IntlJ4c(IntlCase c){
        const string committed="日本語";
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,5);
        IntlNote(c,GCS_RESULTSTR,committed,0);
        IntlDeliver(c,committed);
        IntlEnd(c);Pump();
        IntlCheck(c,c.Editor.Document.Text==c.Fixture.Insert(c.At,committed)?1:-1,"commit by Enter writes the text once, with no newline of its own");
        string newline=c.Editor.DocumentNewline();
        c.Editor.TextArea.PerformTextInput(newline);Pump();
        // Enter inside a cell is the product's paragraph break, saved as "<br>". Elsewhere both
        // view modes first settle the caret on the outer edge of the closing markers
        // (LiveMarkdownCommands.InlineEnter, the shared Stage 6 rule) and write the newline there.
        // AvalonEdit's indentation strategy then replaces the continuation line's leading
        // whitespace with the previous line's indentation (empty here), which is the same rule
        // every Enter on the source surface follows.
        int index=IntlInsertionIndex(c.OffsetsBefore,c.At);
        string expectedVisible=c.VisibleBefore.Insert(index,committed+"\n");
        string expected;int caret;
        if(c.Surface==3){
            expected=c.Fixture.Insert(c.At,committed+"<br>");caret=c.At+committed.Length+4;
        }else{
            string tail=c.Fixture.Substring(c.At);int settle=0;
            if(tail.StartsWith("**",StringComparison.Ordinal))settle=2;
            else if(tail.StartsWith("</span>",StringComparison.Ordinal))settle="</span>".Length;
            string inserted=c.Fixture.Insert(c.At,committed);
            int at=c.At+committed.Length+settle,strip=0;
            while(at+strip<inserted.Length&&(inserted[at+strip]==' '||inserted[at+strip]=='\t'))strip++;
            expected=inserted.Substring(0,at)+newline+inserted.Substring(at+strip);
            caret=at+newline.Length;
            if(strip>0)expectedVisible=expectedVisible.Remove(index+committed.Length+1,strip);
        }
        c.Once=committed;
        IntlFinishText(c,expected,expectedVisible,caret,2,c.Fixture);
    }
    // J5: Backspace inside the composition shrinks it to nothing; the IME then ends without a
    // result and the document must not have lost a character.
    static void IntlJ5(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,5);
        IntlNote(c,GCS_COMPSTR,null,3);
        IntlNote(c,GCS_COMPSTR,null,1);
        IntlEnd(c);Pump();
        IntlCheck(c,c.Editor.Document.Text==c.Fixture&&c.Editor.CaretIndex==c.At?1:-1,"empty composition at END leaves the source and caret");
        IntlFinish(c,"",c.At,0);
    }
    // C1: Microsoft Pinyin keeps Latin letters in the composition and returns Han characters.
    static void IntlC1(IntlCase c){
        IntlStart(c);
        string[] stages={"z","zh","zho","zhon","zhong","zhongw","zhongwe","zhongwen"};
        for(int i=0;i<stages.Length;i++)IntlNote(c,GCS_COMPSTR,null,stages[i].Length);
        IntlCheck(c,c.Editor.Document.Text==c.Fixture&&c.Editor.ImeBusy?1:-1,"latin composition keeps the source and guards commands (busy="+c.Editor.ImeBusy+")");
        IntlNote(c,GCS_RESULTSTR,"中文",0);
        IntlDeliver(c,"中文");
        IntlEnd(c);
        IntlFinish(c,"中文",c.At+2,1);
    }
    // C1b: an IME returns full-width punctuation as a result of its own.
    static void IntlC1b(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,2);
        IntlNote(c,GCS_RESULTSTR,"，",0);
        IntlDeliver(c,"，");
        IntlEnd(c);
        IntlFinish(c,"，",c.At+1,1);
    }
    // C2: a whole phrase in one RESULTSTR. WM_IME_CHAR normally reaches the editor one character
    // at a time, so the same counted result is delivered as separate TextInput events here.
    static void IntlC2(IntlCase c){
        const string phrase="これは二十文字を超える長い日本語の文章です";
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,9);
        IntlNote(c,GCS_RESULTSTR,phrase,0);
        for(int i=0;i<phrase.Length;i++)IntlDeliver(c,phrase.Substring(i,1));
        IntlEnd(c);
        IntlFinish(c,phrase,c.At+phrase.Length,1);
    }
    // T1a: Bopomofo. The composition string itself changes from symbols to a Han character with no
    // RESULTSTR; more symbols are appended after it and only the final conversion is committed.
    static void IntlT1a(IntlCase c){
        IntlStart(c);
        string[] symbols={"ㄓ","ㄓㄨ","ㄓㄨㄥ","ㄓㄨㄥˉ"};
        for(int i=0;i<symbols.Length;i++)IntlNote(c,GCS_COMPSTR,null,symbols[i].Length);
        IntlNote(c,GCS_COMPSTR,null,1);
        IntlCheck(c,c.Editor.Document.Text==c.Fixture&&c.Editor.ImeBusy?1:-1,"in-place composition replacement keeps the document (busy="+c.Editor.ImeBusy+")");
        IntlNote(c,GCS_COMPSTR,null,4);
        IntlNote(c,GCS_COMPSTR,null,5);
        IntlNote(c,GCS_COMPSTR,null,2);
        IntlNote(c,GCS_RESULTSTR,"中文",0);
        IntlDeliver(c,"中文");
        IntlEnd(c);
        IntlFinish(c,"中文",c.At+2,1);
    }
    // T1b: the Bopomofo buffer overflows: the first part is committed while the remaining symbols
    // keep composing, which is the same "result and composition in one message" shape as J2.
    static void IntlT1b(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,8);
        IntlNote(c,GCS_RESULTSTR|GCS_COMPSTR,"中",3);
        IntlDeliver(c,"中");Pump();
        IntlCheck(c,c.Editor.Document.Text==c.Fixture.Insert(c.At,"中")&&c.Editor.ImeBusy?1:-1,"overflow commit writes the first part and keeps composing (busy="+c.Editor.ImeBusy+")");
        IntlCheck(c,IntlPending(c.Editor)==0?1:-1,"overflow commit consumed its counted result (pending="+IntlPending(c.Editor)+")");
        IntlNote(c,GCS_COMPSTR,null,1);
        IntlNote(c,GCS_RESULTSTR,"文",0);
        IntlDeliver(c,"文");
        IntlEnd(c);
        IntlFinish(c,"中文",c.At+2,2);
    }
    // T2a: ChangJie composes radical letters and returns a single Han character.
    static void IntlT2a(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,2);
        IntlNote(c,GCS_RESULTSTR,"明",0);
        IntlDeliver(c,"明");
        IntlEnd(c);
        IntlFinish(c,"明",c.At+1,1);
    }
    // T2b: Space commits the selected candidate, then punctuation results follow in the same session.
    static void IntlT2b(IntlCase c){
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,2);
        IntlNote(c,GCS_RESULTSTR,"明",0);
        IntlDeliver(c,"明");
        IntlNote(c,GCS_COMPSTR,null,1);
        IntlNote(c,GCS_RESULTSTR,"，",0);
        IntlDeliver(c,"，");
        IntlNote(c,GCS_COMPSTR,null,1);
        IntlNote(c,GCS_RESULTSTR,"。",0);
        IntlDeliver(c,"。");
        IntlEnd(c);
        // Three results of their own: under the non-Korean rule one commit is one undo step, so
        // the punctuation takes its own step instead of merging into 明.
        IntlFinish(c,"明，。",c.At+3,3);
    }
    // S1: a space typed on the bold pair's closing edge is taken back inside by the next IME
    // commit, together with the committed text, in the Basic view. The Live view keeps the space
    // where the caret was and writes the commit after it; both lines end up with the same source.
    static void IntlS1(IntlCase c){
        c.Editor.TextArea.PerformTextInput(" ");Pump();
        string before=c.Editor.Document.Text;
        IntlStart(c);
        IntlNote(c,GCS_COMPSTR,null,5);
        IntlNote(c,GCS_RESULTSTR,"日本語",0);
        IntlDeliver(c,"日本語");
        IntlEnd(c);Pump();
        int caret=c.Basic?c.At+6:c.At+4;
        IntlFinish(c," 日本語",caret,1,before);
    }
    // Everything that must hold once the sequence ended: the source text and caret, no composition
    // state left, the Basic view's drawn text (markers gone, the commit at its own spot), undo and
    // redo through the real Ctrl+Z handler, and ordinary English typing afterwards.
    static void IntlFinish(IntlCase c,string insert,int caret,int clauses){IntlFinish(c,insert,caret,clauses,c.Fixture);}
    static void IntlFinish(IntlCase c,string insert,int caret,int clauses,string beforeText){
        int index=IntlInsertionIndex(c.OffsetsBefore,c.At);
        c.Once=insert;
        IntlFinishText(c,c.Fixture.Insert(c.At,insert),c.VisibleBefore.Insert(index,insert),caret,clauses,beforeText);
    }
    static void IntlFinishText(IntlCase c,string expected,string expectedVisible,int caret,int clauses,string beforeText){
        Pump();
        IntlCheck(c,c.Editor.Document.Text==expected?1:-1,"source equals the expected text ("+IntlDiff(c.Editor.Document.Text,expected)+")");
        IntlCheck(c,c.Editor.CaretIndex==caret?1:-1,"caret is "+caret+" (actual="+c.Editor.CaretIndex+")");
        if(!String.IsNullOrEmpty(c.Once))IntlCheck(c,IntlCount(c.Editor.Document.Text,c.Once)==1?1:-1,"committed text appears exactly once");
        bool idle=SourceIdle(c.Window,c.Editor);
        IntlCheck(c,idle?1:-1,"composition flags released ("+c.Editor.CompositionState()+")");
        if(c.Session!=null&&!c.Session.IsDisposed){
            int[] offsets;string visible=c.Session.VisibleText(out offsets);
            if(c.Basic){
                bool noMarkers=visible.IndexOf("**",StringComparison.Ordinal)<0&&visible.IndexOf("<span",StringComparison.OrdinalIgnoreCase)<0&&visible.IndexOf("</span>",StringComparison.OrdinalIgnoreCase)<0&&visible.IndexOf('|')<0;
                IntlCheck(c,noMarkers&&visible==expectedVisible?1:-1,"basic view hides every marker and shows the commit in place (no markers="+noMarkers+", text match="+(visible==expectedVisible)+" "+IntlDiff(visible,expectedVisible)+")");
            }else{
                IntlCheck(c,String.IsNullOrEmpty(c.Once)||IntlCount(visible,c.Once)==1?1:-1,"live view shows the committed text once");
            }
        }
        int steps=0;string firstUndo=null;
        if(clauses>0){
            // A committed clause is its own history entry; a short clause may merge into the run,
            // so N clauses may need at most N presses.
            while(steps<clauses+1&&c.Editor.Document.Text!=beforeText){Invoke(c.Window,"RestoreEditorHistory",false);steps++;if(steps==1)firstUndo=c.Editor.Document.Text;}
            bool undone=c.Editor.Document.Text==beforeText;
            IntlCheck(c,undone&&steps<=clauses?1:-1,"undo restores the pre-commit text in "+steps+" step(s), allowed "+clauses+" (restored="+undone+", first=["+(firstUndo==null?"-":firstUndo.Replace('\r',' ').Replace('\n','|'))+"])");
            for(int i=0;i<steps;i++)Invoke(c.Window,"RestoreEditorHistory",true);
            IntlCheck(c,c.Editor.Document.Text==expected?1:-1,"redo restores the committed text ("+IntlDiff(c.Editor.Document.Text,expected)+")");
            IntlCheck(c,SourceIdle(c.Window,c.Editor)?1:-1,"redo leaves no composition state ("+c.Editor.CompositionState()+")");
        }else{
            string before=c.Editor.Document.Text;
            Invoke(c.Window,"RestoreEditorHistory",false);
            IntlCheck(c,c.Editor.Document.Text==before?1:-1,"undo leaves an unchanged document");
        }
        // Ordinary English typing after the commit. The caret may be pulled to the inner edge of a
        // hidden pair on the first character (the Basic view keeps typing inside the pair), so the
        // check is that three characters landed once at the caret, not a fixed source offset.
        int beforeLength=expected.Length;
        foreach(char value in "abc")c.Editor.TextArea.PerformTextInput(value.ToString());
        Pump();
        string typedText=c.Editor.Document.Text;
        string tail=c.Editor.CaretIndex>=3?typedText.Substring(c.Editor.CaretIndex-3,3):"";
        bool typed=typedText.Length==beforeLength+3&&IntlCount(typedText,"abc")==1&&tail=="abc";
        IntlCheck(c,typed?1:-1,"English typing after the commit inserts three characters at the caret (length "+beforeLength+" -> "+typedText.Length+", caret="+c.Editor.CaretIndex+", tail=["+tail+"])");
        Invoke(c.Window,"RestoreEditorHistory",false);
        IntlCheck(c,c.Editor.Document.Text!=typedText?1:-1,"undo after English typing changes the text");
        Invoke(c.Window,"RestoreEditorHistory",true);
        IntlCheck(c,c.Editor.Document.Text==typedText?1:-1,"redo after English typing restores it");
    }
    static void ResetHistoryCase(MainWindow window,SourceEditor editor){
        editor.ResetCompositionSimulation();window.SetText("");window.SetMode(true);Pump();
    }
    // Every flag a command consults before it may touch the store.
    static bool SourceIdle(MainWindow window,SourceEditor editor){
        return !editor.ImeBusy&&!(bool)Invoke(window,"CompositionOwnsStore")&&!(bool)Invoke(window,"StoreGuardLive");
    }
    // The existing IMM seam delivers only committed text to the document. Start/update events
    // must not be mistaken for an inline composing range: the real syllable is drawn by IMM.
    // These checks cover hidden-marker/table insertion geometry, not that native window's caret.
    static void VerifyHiddenCompositionCaret(Action<string,int,string> check){
        var editor=new SourceEditor {FontFamily=new System.Windows.Media.FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
        var window=new Window {Content=editor,Width=700,Height=450,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=-20000};
        MarkdownLivePreview.Session session=null;
        try{
            window.Show();Pump();
            session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=true});
            string fixture="앞\n\n본문 **굵게** 글자\n\n| 머리 | 둘째 |\n|---|---|\n| 본문 **굵게** 글자 | 끝 글자 |\n\n뒤\n";
            string[] names={"hidden body after bold","hidden table first cell after bold","hidden table last cell"};
            for(int test=0;test<names.Length;test++){
                editor.ResetCompositionSimulation();editor.Text=fixture;editor.CaretIndex=0;Pump();
                var line=editor.Document.GetLineByNumber(test==0?3:7);
                int cell=test==2?1:0;
                int at=test==0?line.EndOffset:line.Offset+session.Info(line).Cells[cell].End;
                editor.Select(at,0);Pump();
                bool raw=session.Raw(line);
                check(names[test]+" stays rendered",!raw?1:-1,"HideMarkers=true, raw="+raw);
                string expected=fixture;
                foreach(string syllable in new[]{"한","글"}){
                    int insertion=editor.CaretIndex;string before=editor.Document.Text;
                    Rect beforeCaret=editor.TextArea.Caret.CalculateCaretRectangle();
                    editor.SimulateImeComposition();
                    RaiseComposition(editor,System.Windows.Input.TextCompositionManager.PreviewTextInputStartEvent,syllable=="한"?"ㅎ":"ㄱ");
                    RaiseComposition(editor,System.Windows.Input.TextCompositionManager.PreviewTextInputUpdateEvent,syllable=="한"?"하":"그");
                    RaiseComposition(editor,System.Windows.Input.TextCompositionManager.PreviewTextInputUpdateEvent,syllable);Pump();
                    Rect during=editor.TextArea.Caret.CalculateCaretRectangle();
                    check(names[test]+" "+syllable+" start/update preserves insertion point",
                        editor.ImeBusy&&editor.Document.Text==before&&editor.CaretIndex==insertion&&Math.Abs(during.X-beforeCaret.X)<.01?1:-1,
                        "busy="+editor.ImeBusy+", offset="+editor.CaretIndex+"/"+insertion+", caretX="+during.X+"/"+beforeCaret.X);
                    // Do not invent glyph bounds from a string the TextView never drew. Such an
                    // assertion used to pass while real Korean composition was visibly wrong.
                    check(names[test]+" "+syllable+" native composing caret after glyph",0,
                        "not observable: simulated start/update creates no IMM composition glyph or native caret");
                    editor.SimulateImeCommit(syllable.Length);editor.TextArea.PerformTextInput(syllable);Pump();
                    expected=expected.Insert(insertion,syllable);
                    bool textOK=editor.Document.Text==expected&&editor.CaretIndex==insertion+syllable.Length;
                    check(names[test]+" "+syllable+" commit stays at expected source offset",textOK?1:-1,
                        "caret="+editor.CaretIndex+", expected="+(insertion+syllable.Length));
                    double right=Double.NaN,top=Double.NaN,bottom=Double.NaN;
                    bool measured=CommittedGlyphEdge(editor,session,line, test==0?-1:cell,insertion,out right,out top,out bottom);
                    Rect caret=editor.TextArea.Caret.CalculateCaretRectangle();var view=editor.TextArea.TextView;
                    double x=caret.X-view.HorizontalOffset,y=caret.Y-view.VerticalOffset;
                    check(names[test]+" "+syllable+" committed caret follows drawn glyph",
                        measured&&x>=right-.75&&Math.Abs(x-right)<=1&&y>=top-1&&y<bottom?1:-1,
                        "caretX="+x+", glyphRight="+right+", caretY="+y+", row="+top+".."+bottom);
                    editor.ResetCompositionSimulation();
                }
            }
        }finally{
            editor.ResetCompositionSimulation();if(session!=null)session.Dispose();window.Close();
        }
    }
    // The caret on both surfaces is AvalonEdit's own CaretLayer; the live table projection
    // replaces only the selection layer (LiveTableSelectionLayer), so a table row must not draw
    // a caret of its own. A body line and table cells are compared while a composition is
    // simulated: the same single layer must stay shown, and the point handed to the native IME
    // window (the composition window origin) must be the caret the layer draws.
    static void VerifyCompositionCaretParity(Action<string,int,string> check){
        var editor=new SourceEditor {FontFamily=new System.Windows.Media.FontFamily("Malgun Gothic"),FontSize=16,WordWrap=true};
        var window=new Window {Content=editor,Width=700,Height=450,ShowInTaskbar=false,ShowActivated=false,Left=-20000,Top=-20000};
        MarkdownLivePreview.Session session=null;
        try{
            window.Show();Pump();
            session=MarkdownLivePreview.Attach(editor,new MarkdownLivePreview.Settings {FontSize=16,Family="Malgun Gothic",LinePercent=150,WordWrap=true,HideMarkers=true});
            string fixture="앞\n\n본문 **굵게** 글자\n\n| 머리 | 둘째 |\n|---|---|\n| 본문 **굵게** 글자 | 끝 글자 |\n\n뒤\n";
            string[] names={"body","table first cell","table last cell"};
            int[] shown=new int[names.Length];
            double[] offsetX=new double[names.Length],offsetY=new double[names.Length],stockX=new double[names.Length],stockY=new double[names.Length];
            for(int test=0;test<names.Length;test++){
                editor.ResetCompositionSimulation();editor.Text=fixture;editor.CaretIndex=0;Pump();
                var line=editor.Document.GetLineByNumber(test==0?3:7);
                int cell=test==2?1:0;
                int at=test==0?line.EndOffset:line.Offset+session.Info(line).Cells[cell].End;
                editor.Select(at,0);Pump();
                // The offscreen window owns no keyboard focus, so the caret is shown exactly as
                // TextArea.OnGotKeyboardFocus does (vendor Editing/TextArea.cs:815).
                editor.TextArea.Caret.Show();Pump();
                bool beforeVisible;Rect beforeRect;
                bool readBefore=ReadCaretLayer(editor,out beforeVisible,out beforeRect);
                int layersBefore=CaretLayerCount(editor);
                editor.SimulateImeComposition();
                RaiseComposition(editor,System.Windows.Input.TextCompositionManager.PreviewTextInputStartEvent,"ㅇ");
                RaiseComposition(editor,System.Windows.Input.TextCompositionManager.PreviewTextInputUpdateEvent,"아");Pump();
                bool visible;Rect layerRect;
                bool read=ReadCaretLayer(editor,out visible,out layerRect);
                int layers=CaretLayerCount(editor);
                Rect caret=editor.TextArea.Caret.CalculateCaretRectangle();
                var view=editor.TextArea.TextView;
                double size;System.Windows.Media.Typeface face;
                Point composition=LiveCompositionGeometry.Position(editor,out size,out face);
                double caretX=caret.X-view.HorizontalOffset,caretY=caret.Y-view.VerticalOffset;
                double pointX,pointY;
                bool stock=StockCharacterBounds(editor,out pointX,out pointY);
                shown[test]=read&&visible?1:0;
                offsetX[test]=caret.IsEmpty||Double.IsNaN(composition.X)?Double.NaN:composition.X-caretX;
                offsetY[test]=caret.IsEmpty||Double.IsNaN(composition.Y)?Double.NaN:composition.Y-caretY;
                stockX[test]=stock?pointX-caretX:Double.NaN;stockY[test]=stock?pointY-caretY:Double.NaN;
                check(names[test]+" keeps one caret layer while composing",layersBefore==1&&layers==1&&readBefore&&read?1:-1,
                    "layers="+layersBefore+"->"+layers+", layer read="+read+", offset="+editor.CaretIndex);
                check(names[test]+" caret layer stays visible while composing",shown[test]>0&&beforeVisible?1:-1,
                    "visible before="+beforeVisible+", during="+visible+", layer rect x="+N(beforeRect.X)+"/"+N(layerRect.X)+", caret x="+N(caretX));
                check(names[test]+" composition point is the drawn caret",Math.Abs(offsetX[test])<=1&&Math.Abs(offsetY[test])<=1.5?1:-1,
                    "dx="+N(offsetX[test])+", dy="+N(offsetY[test])+", caret top="+N(caretY)+", height="+N(caret.Height)+", comp y="+N(composition.Y)+", em="+N(size)+", face="+(face==null?"-":face.FontFamily.Source));
                // AvalonEdit's own ImeSupport also writes the composition window from the
                // character bounds at the caret's visual column (vendor ImeNativeWrapper.cs:213-238).
                // A table row where those bounds are not the caret would move the window away
                // from the caret on every WM_IME_COMPOSITION (informational, not asserted).
                check(names[test]+" avalonedit character bounds match the caret",0,
                    "stock dx="+N(stockX[test])+", dy="+N(stockY[test]));
                editor.ResetCompositionSimulation();
            }
            bool parity=shown[0]==shown[1]&&shown[0]==shown[2]&&Math.Abs(offsetX[0]-offsetX[1])<=1&&Math.Abs(offsetX[0]-offsetX[2])<=1;
            check("composition caret parity body vs table",parity&&shown[0]>0?1:-1,
                "body dx="+N(offsetX[0])+"/dy="+N(offsetY[0])+", first cell dx="+N(offsetX[1])+"/dy="+N(offsetY[1])+", last cell dx="+N(offsetX[2])+"/dy="+N(offsetY[2]));
        }catch(Exception ex){check("composition caret parity exception",-1,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            editor.ResetCompositionSimulation();
            try{editor.TextArea.Caret.Hide();}catch(Exception){}
            if(session!=null)session.Dispose();window.Close();
        }
    }
    static int CaretLayerCount(SourceEditor editor){
        int count=0;
        foreach(object item in editor.TextArea.TextView.Layers){var layer=item as UIElement;if(layer!=null&&layer.GetType().Name=="CaretLayer")count++;}
        return count;
    }
    // The caret layer's own state (vendor Editing/CaretLayer.cs:53-68), so a probe can compare
    // what is actually drawn without a screen.
    static bool ReadCaretLayer(SourceEditor editor,out bool visible,out Rect rectangle){
        visible=false;rectangle=Rect.Empty;
        var field=typeof(ICSharpCode.AvalonEdit.Editing.Caret).GetField("caretAdorner",Members);
        if(field==null)return false;
        object layer=field.GetValue(editor.TextArea.Caret);
        if(layer==null)return false;
        var type=layer.GetType();
        var shown=type.GetField("isVisible",Members);var rect=type.GetField("caretRectangle",Members);
        if(shown==null||rect==null)return false;
        visible=(bool)shown.GetValue(layer);rectangle=(Rect)rect.GetValue(layer);
        return true;
    }
    // What AvalonEdit's ImeSupport.SetCompositionWindow would use (vendor ImeNativeWrapper.cs:213-238).
    static bool StockCharacterBounds(SourceEditor editor,out double x,out double y){
        x=Double.NaN;y=Double.NaN;
        var view=editor.TextArea.TextView;var caret=editor.TextArea.Caret;var position=caret.Position;
        var visual=view.GetVisualLine(position.Line);
        if(visual==null||position.VisualColumn<0||position.VisualColumn>=visual.VisualLengthWithEndOfLineMarker)return false;
        var line=visual.GetTextLine(position.VisualColumn,position.IsAtEndOfLine);
        if(line==null)return false;
        var bounds=line.GetTextBounds(position.VisualColumn,1);
        if(bounds.Count==0)return false;
        Rect rectangle=bounds[0].Rectangle;
        rectangle.Offset(0,visual.GetTextLineVisualYPosition(line,ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop));
        x=rectangle.Left-view.HorizontalOffset;y=rectangle.Top-view.VerticalOffset;return true;
    }
    static string N(double value){return Double.IsNaN(value)?"NaN":value.ToString("0.##",System.Globalization.CultureInfo.InvariantCulture);}
    // What the native IME window is actually told by the time the IME paints the syllable.
    // AvalonEdit's own ImeSupport rewrites the composition window from character bounds on every
    // WM_IME_COMPOSITION (vendor Editing/ImeSupport.cs:139-141, ImeNativeWrapper.cs:213-238) and
    // runs before the live session's hook, which only corrects a start synchronously. The form is
    // read straight after each message, before any dispatcher callback can rewrite it.
    static void VerifyCompositionWindowTarget(SourceEditor editor,Action<string,int,string> check){
        if(!editor.TextArea.IsKeyboardFocused){check("composition window target",0,"no keyboard focus");return;}
        IntPtr hwnd=WindowHandle(editor);
        var source=PresentationSource.FromVisual(editor.TextArea) as System.Windows.Interop.HwndSource;
        if(hwnd==IntPtr.Zero||source==null){check("composition window target",0,"no window handle");return;}
        string[] names={"body","table cell"};
        for(int test=0;test<names.Length;test++){
            int caret=test==0?CaretFor(editor,"본문"):CaretFor(editor,"칸글");
            if(caret<0){check(names[test]+" composition window target",0,"fixture text not found");continue;}
            if(test==1)caret+=2;
            editor.CaretIndex=caret;editor.TextArea.Caret.BringCaretToView();Pump();
            double size;System.Windows.Media.Typeface face;
            Point intended=LiveCompositionGeometry.Position(editor,out size,out face);
            if(Double.IsNaN(intended.X)||Double.IsNaN(intended.Y)){check(names[test]+" composition window target",0,"no composition point");continue;}
            double stockX,stockY;bool stock=StockCharacterBounds(editor,out stockX,out stockY);
            Point ours=LiveCompositionGeometry.ToDevice(editor,source,intended);
            Point theirs=stock?LiveCompositionGeometry.ToDevice(editor,source,new Point(stockX,stockY)):new Point(Double.NaN,Double.NaN);
            SendMessage(hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero);
            ProbeForm startForm;bool formRead=CompositionForm(hwnd,out startForm);
            Point start=formRead?new Point(startForm.X,startForm.Y):new Point(Double.NaN,Double.NaN);
            SendMessage(hwnd,WM_IME_COMPOSITION,IntPtr.Zero,new IntPtr(GCS_COMPSTR));
            Point update;bool updateRead=CompositionFormPoint(hwnd,out update);
            SendMessage(hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero);Pump();
            if(!formRead||!updateRead){check(names[test]+" composition window target",0,"no IME context to read the form");continue;}
            /* 11-07: the Korean form is no longer parked off screen (the Hanja list opened from the parked form at the
               screen corner); the IME's own box is hidden by alpha instead, so every layout gets the caret checks below.
               A parked form is still accepted here for builds that park. */
            bool koreanLayout=System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage!=null&&System.Windows.Input.InputLanguageManager.Current.CurrentInputLanguage.TwoLetterISOLanguageName=="ko";
            bool parked=koreanLayout&&start.X<-10000&&update.X<-10000;
            if(parked)check(names[test]+" composition form parked under the Korean layout",1,"form="+FormatPoint(start)+", update="+FormatPoint(update));
            if(!parked)check(names[test]+" composition window starts at the caret",Near(start,ours,1)?1:-1,
                "form="+FormatPoint(start)+", caret point="+FormatPoint(ours)+", character bounds="+FormatPoint(theirs));
            if(!parked)check(names[test]+" composition window stays at the caret on updates",Near(update,ours,1)?1:-1,
                "form="+FormatPoint(update)+", caret point="+FormatPoint(ours)+", character bounds="+FormatPoint(theirs));
            // A wrapped composition must continue inside the text body, not at the composition
            // window's own left edge (the window's far left, over the line-number gutter): the IME
            // only bounds/wraps by rcArea when the form carries CFS_RECT (real MS-IME, 2026-10-30).
            // CFS_RECT is imm.h's 0x0001; the old form carried 0x0020 (CFS_FORCE_POSITION), which
            // only names the caret point.
            const int CfsRect=0x0001;
            Rect area=LiveCompositionGeometry.BodyArea(editor,source);
            bool rect=(startForm.Style&CfsRect)!=0&&FormArea(startForm,area);
            if(!parked)check(names[test]+" composition form wraps inside the text body",rect?1:-1,
                "style=0x"+startForm.Style.ToString("X")+", form=("+startForm.Left+","+startForm.Top+","+startForm.Right+","+startForm.Bottom+"), body=("+N(area.Left)+","+N(area.Top)+","+N(area.Right)+","+N(area.Bottom)+")");
            bool body=area.Left>4&&area.Right>area.Left&&area.Bottom>area.Top&&(int)ours.X>=(int)area.Left&&(int)ours.X<=(int)area.Right&&(int)ours.Y>=(int)area.Top&&(int)ours.Y<=(int)area.Bottom;
            check(names[test]+" composition body area excludes the gutter and holds the caret",body?1:-1,
                "body=("+N(area.Left)+","+N(area.Top)+","+N(area.Right)+","+N(area.Bottom)+"), caret="+FormatPoint(ours));
        }
    }
    static bool CompositionForm(IntPtr hwnd,out ProbeForm form){
        form=new ProbeForm();
        IntPtr context=ImmGetContext(hwnd);
        if(context==IntPtr.Zero)return false;
        try{return ImmGetCompositionWindow(context,out form);}
        finally{ImmReleaseContext(hwnd,context);}
    }
    static bool CompositionFormPoint(IntPtr hwnd,out Point point){
        point=new Point(Double.NaN,Double.NaN);
        ProbeForm form;if(!CompositionForm(hwnd,out form))return false;
        point=new Point(form.X,form.Y);return true;
    }
    static bool FormArea(ProbeForm form,Rect area){
        return !area.IsEmpty&&Math.Abs(form.Left-area.Left)<=1&&Math.Abs(form.Top-area.Top)<=1&&
            Math.Abs(form.Right-area.Right)<=1&&Math.Abs(form.Bottom-area.Bottom)<=1;
    }
    static bool Near(Point a,Point b,double tolerance){return !Double.IsNaN(a.X)&&!Double.IsNaN(b.X)&&Math.Abs(a.X-b.X)<=tolerance&&Math.Abs(a.Y-b.Y)<=tolerance;}
    static string FormatPoint(Point value){return "("+N(value.X)+","+N(value.Y)+")";}
    static void RaiseComposition(SourceEditor editor,RoutedEvent routedEvent,string text){
        var composition=new System.Windows.Input.TextComposition(System.Windows.Input.InputManager.Current,editor.TextArea,text);
        editor.TextArea.RaiseEvent(new System.Windows.Input.TextCompositionEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,composition) {RoutedEvent=routedEvent});
    }
    // Expected source offset is supplied by the fixture, never taken from the caret being tested.
    static bool CommittedGlyphEdge(SourceEditor editor,MarkdownLivePreview.Session session,ICSharpCode.AvalonEdit.Document.DocumentLine line,int cell,int source,out double right,out double top,out double bottom){
        right=top=bottom=Double.NaN;
        var view=editor.TextArea.TextView;var visual=view.GetVisualLine(line.LineNumber);
        if(visual==null)return false;
        if(cell>=0){
            var info=session.Info(line);if(info.Row==null||info.Row.Texts[cell]==null)return false;
            var text=info.Row.Texts[cell];int index=-1;
            for(int p=0;p<text.Text.Length;p++)if(text.Map[p]==source-line.Offset){index=p;break;}
            if(index<0)return false;
            return session.CellGeometry(line,cell,index+1,true,out right,out top,out bottom);
        }
        int column=visual.GetVisualColumn(source-visual.FirstDocumentLine.Offset);
        var own=visual.GetTextLine(column);
        var bounds=own.GetTextBounds(column,1);if(bounds.Count==0)return false;
        right=bounds[0].Rectangle.Right-view.HorizontalOffset;
        top=visual.GetTextLineVisualYPosition(own,ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop)-view.VerticalOffset;
        bottom=visual.GetTextLineVisualYPosition(own,ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineBottom)-view.VerticalOffset;
        return true;
    }
    static void VerifyBoundaryFonts(SourceEditor editor,Action<string,int,string> check){
        var document=editor.Document;string original=document.Text;
        try{
            foreach(bool empty in new[]{false,true}){
                editor.Text=empty?"# \n\nbody":"# 제목\n\nbody";editor.CaretIndex=2;Pump();var view=editor.TextArea.TextView;view.EnsureVisualLines();
                double expected=editor.FontSize*2;
                CheckComputedFont(editor,expected,check,"D "+(empty?"empty heading end":"heading text start"));
                document.Insert(2,"각");bool invalid=!view.VisualLinesValid;
                CheckComputedFont(editor,expected,check,"D heading after edit before render (invalid="+invalid+")");
                check("D edit invalidates visual lines "+(empty?"empty":"nonempty"),invalid?1:-1,"VisualLinesValid="+view.VisualLinesValid);Pump();
            }
        }finally{editor.Text=original;editor.CaretIndex=0;Pump();}
    }
    static void CheckComputedFont(SourceEditor editor,double expected,Action<string,int,string> check,string name){
        var wrapper=typeof(ICSharpCode.AvalonEdit.TextEditor).Assembly.GetType("ICSharpCode.AvalonEdit.Editing.ImeNativeWrapper");
        var compute=wrapper.GetMethod("GetCompositionFont",Members);double size=0;string face="";
        if(compute!=null){object[] args={editor.TextArea,0.0,null};compute.Invoke(null,args);size=(double)args[1];var typeface=args[2] as System.Windows.Media.Typeface;face=typeface==null?"":typeface.FontFamily.Source;}
        else{
            // Baseline DLL has only the native setter. Measure exactly that implementation;
            // the fixed DLL exposes the same calculation without requiring an IME context.
            IntPtr hwnd=WindowHandle(editor),context=hwnd==IntPtr.Zero?IntPtr.Zero:ImmGetContext(hwnd);
            if(context==IntPtr.Zero){check(name,0,"baseline native setter has no IME context");return;}
            try{wrapper.GetMethod("SetCompositionFont",Members).Invoke(null,new object[]{PresentationSource.FromVisual(editor.TextArea) as HwndSource,context,editor.TextArea});ProbeLogFont font;bool got=ImmGetCompositionFont(context,out font);if(!got){check(name,0,"ImmGetCompositionFontW failed");return;}size=Math.Abs(font.lfHeight)/System.Windows.Media.VisualTreeHelper.GetDpi(editor).DpiScaleY;face=font.lfFaceName;}
            finally{ImmReleaseContext(hwnd,context);}
        }
        check(name,Math.Abs(size-expected)<.6&&!String.IsNullOrEmpty(face)?1:-1,"em="+size+", expected="+expected+", face="+face+", pure helper="+(compute!=null));
    }
    // The window is activated (offscreen) so the editor can create AvalonEdit's IME context.
    // A hidden/non-interactive session can refuse the foreground; the caller then reports SKIP.
    static bool FocusSource(MainWindow window,SourceEditor editor){
        for(int attempt=0;attempt<12&&!editor.TextArea.IsKeyboardFocused;attempt++){
            window.Activate();
            if(attempt==4)SetForegroundWindow(new WindowInteropHelper(window).Handle);
            editor.TextArea.Focus();
            System.Windows.Input.Keyboard.Focus(editor.TextArea);
            Pump();
        }
        return editor.TextArea.IsKeyboardFocused;
    }
    static IntPtr WindowHandle(SourceEditor editor){
        var source=PresentationSource.FromVisual(editor.TextArea) as HwndSource;
        return source==null?IntPtr.Zero:source.Handle;
    }
    // Lose and regain keyboard focus so AvalonEdit removes and re-adds its own hook after ours.
    static bool RefocusCycle(SourceEditor editor){
        if(!editor.TextArea.IsKeyboardFocused)return false;
        System.Windows.Input.Keyboard.ClearFocus();Pump();
        bool away=!editor.TextArea.IsKeyboardFocused&&!editor.TextArea.IsKeyboardFocusWithin;
        editor.TextArea.Focus();System.Windows.Input.Keyboard.Focus(editor.TextArea);Pump();
        return away&&editor.TextArea.IsKeyboardFocused;
    }
    // The font is read before Pump(): the hook order is what is measured here, and the queued
    // Send-priority re-apply would otherwise hide a wrong order.
    static int FontHeightAt(SourceEditor editor,IntPtr hwnd,string token,Action<string,int,string> check,string name){
        int caret=CaretFor(editor,token);
        if(caret<0){check(name,0,"text not found: "+token);return 0;}
        editor.CaretIndex=caret+1;editor.TextArea.Caret.BringCaretToView();Pump();
        editor.TextArea.TextView.EnsureVisualLines();Pump();
        int expected=ExpectedHeight(editor);
        SendMessage(hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero);
        SendMessage(hwnd,WM_IME_COMPOSITION,IntPtr.Zero,new IntPtr(GCS_COMPSTR));
        string skip;int actual=ReadHeight(hwnd,out skip);
        SendMessage(hwnd,WM_IME_ENDCOMPOSITION,IntPtr.Zero,IntPtr.Zero);
        Pump();
        if(skip!=null){check(name,0,skip);return 0;}
        bool ok=actual<0&&actual==expected;
        check(name,ok?1:-1,"lfHeight="+actual+", rendered-run expected="+expected);
        // Caret.ShowInternal invokes this native setter at Normal priority, outside the
        // window message hooks. Measure its immediate result before app-side corrections.
        var native=typeof(ICSharpCode.AvalonEdit.TextEditor).Assembly.GetType("ICSharpCode.AvalonEdit.Editing.ImeNativeWrapper");
        var hwndSource=PresentationSource.FromVisual(editor.TextArea) as HwndSource;
        IntPtr context=ImmGetContext(hwnd);
        if(context==IntPtr.Zero)check(name+" native caret setter",0,"no IME context");
        else try{native.GetMethod("SetCompositionFont",Members).Invoke(null,new object[]{hwndSource,context,editor.TextArea});int direct=ReadHeight(hwnd,out skip);check(name+" native caret setter",skip!=null?0:direct==expected?1:-1,"lfHeight="+direct+", rendered-run expected="+expected);}
        finally{ImmReleaseContext(hwnd,context);}
        return actual;
    }
    static int CaretFor(SourceEditor editor,string token){
        var document=editor.Document;
        for(int i=1;i<=document.LineCount;i++){
            var line=document.GetLineByNumber(i);
            int at=document.GetText(line).IndexOf(token,StringComparison.Ordinal);
            if(at>=0)return line.Offset+at;
        }
        return -1;
    }
    // Oracle: the TextFormatter output used to draw the glyph, not CompositionFont().
    static int ExpectedHeight(SourceEditor editor){
        var view=editor.TextArea.TextView;view.EnsureVisualLines();
        var visual=view.GetVisualLine(editor.TextArea.Caret.Line);
        int column=visual.GetVisualColumn(editor.CaretIndex-visual.FirstDocumentLine.Offset);
        var line=visual.GetTextLine(column,editor.TextArea.Caret.Position.IsAtEndOfLine);
        int start=0;foreach(var previous in visual.TextLines){if(Object.ReferenceEquals(previous,line))break;start+=previous.Length;}
        double em=editor.FontSize;int relative=Math.Max(0,column-start-1),at=0;
        foreach(var span in line.GetTextRunSpans()){if(span.Value.Properties!=null)em=span.Value.Properties.FontRenderingEmSize;if(relative<at+span.Length)break;at+=span.Length;}
        return -(int)Math.Round(em*System.Windows.Media.VisualTreeHelper.GetDpi(editor).DpiScaleY);
    }
    static int ReadHeight(IntPtr hwnd,out string skip){
        skip=null;
        IntPtr context=ImmGetContext(hwnd);
        if(context==IntPtr.Zero){skip="ImmGetContext returned zero";return 0;}
        try{
            ProbeLogFont font=new ProbeLogFont();
            if(!ImmGetCompositionFont(context,out font))skip="ImmGetCompositionFontW failed";
            return font.lfHeight;
        }finally{ImmReleaseContext(hwnd,context);}
    }
    static void Pump(){LongLineBench.Idle();Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));LongLineBench.Idle();}
    static FieldInfo Required(string name){var field=typeof(MainWindow).GetField(name,Members);if(field==null)throw new MissingMemberException("probe: MainWindow has no field '"+name+"'");return field;}
    static T ReadField<T>(MainWindow window,string name){return (T)Required(name).GetValue(window);}
    static void WriteField(MainWindow window,string name,object value){Required(name).SetValue(window,value);}
    static T ReadProperty<T>(MainWindow window,string name){var property=typeof(MainWindow).GetProperty(name,Members);if(property==null)throw new MissingMemberException("probe: MainWindow has no property '"+name+"'");return (T)property.GetValue(window,null);}
    static object Invoke(MainWindow window,string name,params object[] args){var method=typeof(MainWindow).GetMethod(name,Members);if(method==null)throw new MissingMemberException("probe: MainWindow has no method '"+name+"'");return method.Invoke(window,args);}
}
public partial class MainWindow {
    internal static int VerifyIme(string outDir){return ImeProbeRunner.Run(outDir);}
}
// --ime-click-hang-probe <outDir>: a click while a Korean composition is live must never keep the UI thread inside a
// self re-posting callback cycle (user report, 2.1.0: freeze with the caret on a paragraph above a table). Every case
// presses the mouse in the middle of a composition (one lone consonant, or a whole syllable), moves the caret to the
// start of the paragraph line and then waits for an Input-priority marker (1000 ms) and ApplicationIdle (2000 ms).
// A runaway cycle is cut by the dispatcher hooks (aborts every newly posted operation of priority Loaded or higher once
// the operation count passes the limit) and, for a cycle inside one operation, by a watchdog thread that exits.
internal static class ImeClickHangProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    const int WM_IME_STARTCOMPOSITION=0x010D,WM_IME_COMPOSITION=0x010F,WM_IME_NOTIFY=0x0282,GCS_COMPSTR=0x0008,LoopLimit=4000;
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd,int msg,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetContext(IntPtr hWnd);
    [DllImport("imm32.dll")] static extern bool ImmReleaseContext(IntPtr hWnd,IntPtr hIMC);
    static readonly object gate=new object();
    static readonly StringBuilder report=new StringBuilder();
    static bool all=true,counting,aborting,adjacentFixture;
    static string outFolder,currentCase;
    static int opsSinceClick,notifies,caretChanges,visualChanges,abortedOps;
    static volatile int caseTick;
    static bool simNotify;static IntPtr simWindow;
    static readonly System.Collections.Generic.Dictionary<string,int> histogram=new System.Collections.Generic.Dictionary<string,int>();
    static FieldInfo methodField;
    static void Check(string name,int state,string detail){
        lock(gate){
            if(state<0)all=false;
            report.Append(state<0?"FAIL ":state>0?"PASS ":"SKIP ").Append(name).Append(": ").Append(detail).Append('\n');
        }
    }
    static void Finish(){
        lock(gate){
            string pass=Path.Combine(outFolder,"ime-click-hang-probe-PASS.txt"),fail=Path.Combine(outFolder,"ime-click-hang-probe-FAIL.txt");
            try{if(File.Exists(pass))File.Delete(pass);if(File.Exists(fail))File.Delete(fail);}catch(IOException){}
            File.WriteAllText(all?pass:fail,report.ToString(),new UTF8Encoding(false));
        }
    }
    internal static int Run(string outDir){
        outFolder=Path.GetFullPath(outDir);Directory.CreateDirectory(outFolder);
        bool oldLive=ProbeDefaults.Live,oldSelfTest=Program.IsSelfTest;
        // A cycle inside a single operation cannot be cut from the dispatcher: this thread ends the process after 45 s without a case change.
        var watchdog=new System.Threading.Timer(delegate(object state){
            if(unchecked(Environment.TickCount-caseTick)>45000){
                lock(gate){all=false;report.Append("FAIL watchdog: the UI thread did not finish case [").Append(currentCase).Append("] in 45 s, parks=").Append(SourceEditor.ProbeParkCalls).Append(", ops=").Append(opsSinceClick).Append('\n');}
                Finish();Environment.Exit(3);
            }
        },null,1000,1000);
        try{
            Program.IsSelfTest=true;
            if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
            Application.Current.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            lock(gate){report.Append("layout=0x").Append(GetKeyboardLayout(0).ToInt64().ToString("X")).Append('\n');}
            string[] names={"lone consonant","whole syllable"};
            string[] texts={"ㅈ","자"};
            for(int mode=0;mode<2;mode++)
                for(int comp=0;comp<2;comp++)
                    for(int variant=0;variant<3;variant++)
                        RunCase((mode==0?"live":"basic")+" / "+names[comp]+" / "+VariantName(variant),mode==0,texts[comp],variant);
            adjacentFixture=true;
            for(int variant=0;variant<3;variant++)RunCase("live / table directly below / lone consonant / "+VariantName(variant),true,"ㅈ",variant);
            adjacentFixture=false;
        }catch(Exception ex){Check("exception",-1,ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            watchdog.Dispose();
            ProbeDefaults.Live=oldLive;Program.IsSelfTest=oldSelfTest;SourceEditor.KoreanLayoutOverride=null;
        }
        Finish();
        return all?0:1;
    }
    static string VariantName(int variant){return variant==0?"click":variant==1?"click + late composition update":"click + IME notifies on every form write";}
    static string Fixture(out int paragraphLine){
        var text=new StringBuilder();
        text.Append("# 제목 줄\n\n");
        paragraphLine=3;
        text.Append("가나다라마바사아자차카타파하 오늘은 바람이 시원하게 불어서 산책하기 좋은 날입니다 그래서 천천히 걸으며 생각을 정리하고 있었는데 문득 어제 읽은 책의 한 구절이 떠올라 잠시 걸음을 멈추고 하늘을 올려다보았습니다 구름이 천천히 흘러가고 있었습니다\n\n");
        text.Append("| 열1 | 열2 | 열3 |\n| --- | --- | --- |\n| 가 | 나 | 다 |\n| 라 | 마 | 바 |\n| 사 | 아 | 자 |\n\n");
        for(int i=1;i<=100;i++){
            text.Append("문단 ").Append(i).Append("번 내용입니다 가나다라마바사 아자차카타파하\n\n");
            if(i%5==0)text.Append("| 이름 | 값 | 설명 |\n| --- | --- | --- |\n| 항목").Append(i).Append(" | ").Append(i).Append(" | 설명 줄 |\n| 다음 | ").Append(i+1).Append(" | 끝 |\n\n");
        }
        string result=text.ToString();
        return adjacentFixture?result.Replace("있었습니다\n\n| 열1","있었습니다\n| 열1"):result;
    }
    static void OnStarted(object sender,DispatcherHookEventArgs e){
        if(!counting)return;
        opsSinceClick++;
        string key=Describe(e.Operation);
        int n;histogram.TryGetValue(key,out n);histogram[key]=n+1;
        if(opsSinceClick>LoopLimit&&!aborting)aborting=true;
    }
    static void OnPosted(object sender,DispatcherHookEventArgs e){
        if(!aborting||!counting)return;
        if(e.Operation.Priority>=DispatcherPriority.Loaded){if(e.Operation.Abort())abortedOps++;}
    }
    static void OnCompleted(object sender,DispatcherHookEventArgs e){
        if(!counting||!simNotify||aborting)return;
        // A real IME answers every form write with IMN_SETCOMPOSITIONWINDOW from inside the call; Caret.ShowInternal is what writes it.
        if(Describe(e.Operation).EndsWith("ShowInternal",StringComparison.Ordinal)&&simWindow!=IntPtr.Zero){notifies++;SendMessage(simWindow,WM_IME_NOTIFY,new IntPtr(0x000B),IntPtr.Zero);}
    }
    // Diagnostics of the first caret moves of a case: unvalidated caret position and the managed call chain that moved it.
    static readonly StringBuilder samples=new StringBuilder();
    static void Sample(SourceEditor editor){
        try{
            var caret=editor.TextArea.Caret;
            var property=typeof(ICSharpCode.AvalonEdit.Editing.Caret).GetProperty("NonValidatedPosition",Members);
            var position=(ICSharpCode.AvalonEdit.TextViewPosition)property.GetValue(caret,null);
            var trace=new System.Diagnostics.StackTrace(1,false);var chain=new StringBuilder();
            for(int i=0;i<trace.FrameCount&&i<16;i++){var method=trace.GetFrame(i).GetMethod();if(i>0)chain.Append(" < ");chain.Append(method.DeclaringType==null?"":method.DeclaringType.Name).Append('.').Append(method.Name);}
            samples.Append("  caret#").Append(caretChanges).Append(" line=").Append(position.Line).Append(" col=").Append(position.Column).Append(" vc=").Append(position.VisualColumn).Append(" :: ").Append(chain).Append('\n');
        }catch(Exception ex){samples.Append("  sample failed ").Append(ex.Message).Append('\n');}
    }
    static string Describe(DispatcherOperation operation){
        try{
            if(methodField==null)methodField=typeof(DispatcherOperation).GetField("_method",Members);
            Delegate method=methodField==null?null:methodField.GetValue(operation) as Delegate;
            if(method==null)return (int)operation.Priority+":?";
            var info=method.Method;
            return (int)operation.Priority+":"+(info.DeclaringType==null?"":info.DeclaringType.FullName)+"."+info.Name;
        }catch(Exception){return (int)operation.Priority+":??";}
    }
    static void Pump(){Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle,new Action(delegate{}));}
    static bool PumpBounded(int milliseconds){
        var frame=new DispatcherFrame();bool idle=false;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{idle=true;frame.Continue=false;}));
        var timer=new DispatcherTimer(DispatcherPriority.Normal);timer.Interval=TimeSpan.FromMilliseconds(milliseconds);
        timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();
        Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Normal,new Action(delegate{}));
        Dispatcher.PushFrame(frame);timer.Stop();return idle;
    }
    static object Invoke(object target,string name,params object[] args){
        var method=target.GetType().GetMethod(name,Members);if(method==null)throw new MissingMemberException("probe: no method "+name);
        return method.Invoke(target,args);
    }
    static T Field<T>(object target,string name){var field=target.GetType().GetField(name,Members);if(field==null)throw new MissingMemberException("probe: no field "+name);return (T)field.GetValue(target);}
    static void RunCase(string name,bool live,string composition,int variant){
        MainWindow window=null;string file=Path.Combine(outFolder,"ime-click-hang.md");
        currentCase=name;caseTick=Environment.TickCount;counting=false;aborting=false;abortedOps=0;simNotify=false;simWindow=IntPtr.Zero;
        var dispatcher=Dispatcher.CurrentDispatcher;
        DispatcherHooks hooks=dispatcher.Hooks;
        bool hooked=false;
        try{
            ProbeDefaults.Live=true;
            int paragraphLine;
            File.WriteAllText(file,Fixture(out paragraphLine),new UTF8Encoding(false));
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=900,Height=700};
            window.Show();window.Left=-20000;window.Top=-20000;
            Invoke(window,"LoadFileCore",file);window.SetMode(true);Pump();
            if(!live){var preview=Field<System.Windows.Controls.CheckBox>(window,"preview");preview.IsChecked=true;Pump();}
            var editor=Field<SourceEditor>(window,"source");
            bool attached=(bool)window.GetType().GetProperty("LivePreviewAttached",Members).GetValue(window,null);
            if(attached!=live){Check(name,-1,"setup: live preview attached="+attached+", wanted="+live);return;}
            for(int attempt=0;attempt<12&&!editor.TextArea.IsKeyboardFocused;attempt++){
                window.Activate();if(attempt==4)SetForegroundWindow(new WindowInteropHelper(window).Handle);
                editor.TextArea.Focus();System.Windows.Input.Keyboard.Focus(editor.TextArea);Pump();
            }
            var source=PresentationSource.FromVisual(editor.TextArea) as HwndSource;
            if(!editor.TextArea.IsKeyboardFocused||source==null){Check(name,0,"keyboard focus unavailable in this run");return;}
            IntPtr hwnd=source.Handle;
            SourceEditor.KoreanLayoutOverride=delegate{return true;};
            var document=editor.Document;
            var line=document.GetLineByNumber(paragraphLine);
            string paragraph=document.GetText(line.Offset,line.Length);
            if(paragraph.IndexOf("가나다라마바사아자차카타파하",StringComparison.Ordinal)!=0){Check(name,-1,"setup: paragraph line "+paragraphLine+" is ["+paragraph+"]");return;}
            editor.Select(line.EndOffset,0);editor.TextArea.Caret.BringCaretToView();PumpBounded(2000);
            MethodInfo note=typeof(SourceEditor).GetMethod("NoteImeComposition",Members);
            MethodInfo inline=typeof(SourceEditor).GetMethod("SetImeInline",Members,null,new Type[]{typeof(string)},null);
            if(note==null||inline==null){Check(name,-1,"seam missing: NoteImeComposition/SetImeInline");return;}
            // The composition: the real START message through the window hooks, the interim string through the inline seam (no real IME string exists here).
            SendMessage(hwnd,WM_IME_STARTCOMPOSITION,IntPtr.Zero,IntPtr.Zero);
            note.Invoke(editor,new object[]{(long)GCS_COMPSTR,null,composition.Length});
            inline.Invoke(editor,new object[]{composition});
            PumpBounded(1000);
            lock(histogram){histogram.Clear();}
            samples.Clear();opsSinceClick=0;notifies=0;caretChanges=0;visualChanges=0;SourceEditor.ProbeParkCalls=0;
            EventHandler caretHandler=delegate{caretChanges++;if(caretChanges<=12)Sample(editor);};EventHandler visualHandler=delegate{visualChanges++;};
            editor.TextArea.Caret.PositionChanged+=caretHandler;editor.TextArea.TextView.VisualLinesChanged+=visualHandler;
            hooks.OperationStarted+=OnStarted;hooks.OperationPosted+=OnPosted;hooks.OperationCompleted+=OnCompleted;hooked=true;
            simNotify=variant==2;simWindow=hwnd;
            counting=true;
            var watch=System.Diagnostics.Stopwatch.StartNew();
            long inputAt=-1,idleAt=-1;
            // The click: the window's preview press asks for the commit (the composition stays live: no END arrives in this case, as in the dump), then AvalonEdit moves the caret.
            var down=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,Environment.TickCount,System.Windows.Input.MouseButton.Left);
            down.RoutedEvent=UIElement.PreviewMouseDownEvent;down.Source=editor.TextArea.TextView;
            editor.TextArea.TextView.RaiseEvent(down);
            editor.Select(line.Offset,0);
            if(variant==1){
                SendMessage(hwnd,WM_IME_COMPOSITION,IntPtr.Zero,new IntPtr(GCS_COMPSTR));
                note.Invoke(editor,new object[]{(long)GCS_COMPSTR,null,composition.Length});
                inline.Invoke(editor,new object[]{composition});
            }
            if(variant==2)SendMessage(hwnd,WM_IME_NOTIFY,new IntPtr(0x000B),IntPtr.Zero);
            dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(delegate{inputAt=watch.ElapsedMilliseconds;}));
            var frame=new DispatcherFrame();
            dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{idleAt=watch.ElapsedMilliseconds;frame.Continue=false;}));
            var timer=new DispatcherTimer(DispatcherPriority.Normal);timer.Interval=TimeSpan.FromMilliseconds(6000);
            timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();
            Dispatcher.PushFrame(frame);timer.Stop();
            counting=false;
            string top;
            lock(histogram){
                var items=new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string,int>>(histogram);
                items.Sort(delegate(System.Collections.Generic.KeyValuePair<string,int> a,System.Collections.Generic.KeyValuePair<string,int> b){return b.Value.CompareTo(a.Value);});
                var text=new StringBuilder();
                for(int i=0;i<items.Count&&i<8;i++){if(i>0)text.Append("; ");text.Append(items[i].Key).Append(" x").Append(items[i].Value);}
                top=text.ToString();
            }
            bool ok=inputAt>=0&&inputAt<=1000&&idleAt>=0&&idleAt<=2000&&!aborting;
            Check(name,ok?1:-1,"input marker="+inputAt+" ms, idle marker="+idleAt+" ms, ops="+opsSinceClick+(aborting?" (RUNAWAY: aborted "+abortedOps+" posted operations)":"")
                +", ParkImeForm calls="+SourceEditor.ProbeParkCalls+", caret changes="+caretChanges+", visual line rebuilds="+visualChanges+", simulated notifies="+notifies+", composing="+editor.CompositionState()+", top: "+top);
            if(!ok&&samples.Length>0)Check(name+" caret samples",0,"\n"+samples.ToString());
            editor.TextArea.Caret.PositionChanged-=caretHandler;editor.TextArea.TextView.VisualLinesChanged-=visualHandler;
        }catch(Exception ex){Check(name,-1,"exception "+ex.ToString().Replace('\r',' ').Replace('\n',' '));}
        finally{
            counting=false;aborting=false;simNotify=false;
            if(hooked){hooks.OperationStarted-=OnStarted;hooks.OperationPosted-=OnPosted;hooks.OperationCompleted-=OnCompleted;}
            SourceEditor.KoreanLayoutOverride=null;
            if(window!=null){
                try{window.GetType().GetField("saved",Members).SetValue(window,window.GetType().GetProperty("text",Members).GetValue(window,null));window.Close();}
                catch(Exception ex){Check(name+" close",-1,ex.Message.Replace('\r',' ').Replace('\n',' '));}
            }
            try{if(File.Exists(file))File.Delete(file);}catch(IOException){}
        }
    }
}
}
