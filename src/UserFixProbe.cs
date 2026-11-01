using System;
using System.IO;
using System.Text;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;

namespace MDNote {
// --userfix-probe <outDir>: regressions for the 2026-10-21 user test (generated text only).
// Numbers in check names refer to work/validation/user-test-20261021/테스트 결과 정리.md.
public partial class MainWindow {
    internal static int VerifyUserFixes(string outDir) {
        outDir = Path.GetFullPath(outDir); Directory.CreateDirectory(outDir);
        if (System.Windows.Application.Current == null) new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        // A deferred turn that throws would otherwise kill the process with no report.
        System.Windows.Threading.DispatcherUnhandledExceptionEventHandler dispatcherFault = delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args) {
            try { File.AppendAllText(Path.Combine(outDir, "userfix-fault.txt"), args.Exception.ToString()); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        System.Windows.Threading.Dispatcher.CurrentDispatcher.UnhandledException += dispatcherFault;
        AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs args) {
            try { File.AppendAllText(Path.Combine(outDir, "userfix-fault.txt"), "domain: " + args.ExceptionObject); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        // The source-surface search (TXT, live, and the large read body) is async void and returns to
        // its continuation through the captured context, the same way every other probe and bench
        // that touches it installs this one.
        var previousContext = System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        var report = new StringBuilder("# UserFixProbe\n\n"); bool all = true;
        UserFixWatch.Start(outDir, report); UserFixWatch.Mark("start");
        Action<string, bool, string> check = delegate(string name, bool ok, string detail) { all &= ok; report.Append(ok ? "PASS " : "FAIL ").Append(name).Append(": ").Append(detail).Append('\n'); try { File.WriteAllText(Path.Combine(outDir, "userfix-probe.txt"), report.ToString(), new UTF8Encoding(false)); } catch (IOException) { } catch (UnauthorizedAccessException) { } UserFixWatch.Mark("check " + name); };
        string file = Path.Combine(outDir, "userfix.md");
        const string body = "첫 줄\n\n셋째 줄\n## 제목\n**굵게** 시작\n가나다라마가나다라마\n";
        File.WriteAllText(file, body, new UTF8Encoding(false));
        bool old = ProbeDefaults.Live; ProbeDefaults.Live = true; MainWindow window = null;
        try {
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 }; window.Show(); window.Left = -20000;
            window.LoadFileCore(file); LongLineBench.Idle();
            window.SetMode(true); LongLineBench.Idle();
            var doc = window.source.Document; var area = window.source.TextArea;
            Action<Key> key = delegate(Key k) {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, System.Windows.PresentationSource.FromVisual(window.source), 0, k) { RoutedEvent = Keyboard.KeyDownEvent };
                area.RaiseEvent(args); LongLineBench.Idle();
            };
            Func<double> caretX = delegate { area.TextView.EnsureVisualLines(); return area.Caret.CalculateCaretRectangle().X; };
            string original = window.text;

            // #4: caret on an empty line sits at the left edge.
            window.source.CaretIndex = doc.GetLineByNumber(2).Offset; LongLineBench.Idle();
            double emptyX = caretX();
            check("4 empty line caret at left", emptyX < 40, "x=" + emptyX.ToString("0.#"));
            // #5: one Backspace removes the empty line.
            int lines = doc.LineCount;
            key(Key.Back);
            check("5 one Backspace deletes empty line", doc.LineCount == lines - 1 && window.text.StartsWith("첫 줄\n셋째 줄", StringComparison.Ordinal), "lines " + lines + "->" + doc.LineCount);
            window.Undo(); LongLineBench.Idle();
            // #4: a new line made by Enter also starts at the left edge.
            window.source.CaretIndex = doc.GetLineByNumber(1).EndOffset; LongLineBench.Idle();
            area.PerformTextInput("\n"); LongLineBench.Idle();
            double newX = caretX();
            check("4 caret after Enter at left", newX < 40 && area.Caret.Line == 2, "x=" + newX.ToString("0.#") + ", line=" + area.Caret.Line);
            window.Undo(); LongLineBench.Idle();
            check("4/5 undo restores text", window.text == original, "same=" + (window.text == original));
            // Line-end and line-start stops on non-empty lines still behave.
            DocumentLine third = doc.GetLineByNumber(3);
            window.source.CaretIndex = third.Offset; LongLineBench.Idle();
            key(Key.End);
            check("End reaches the line end", window.source.CaretIndex == third.EndOffset, "caret=" + window.source.CaretIndex + ", end=" + third.EndOffset);
            window.source.CaretIndex = third.Offset; LongLineBench.Idle();
            key(Key.Left);
            check("Left at line start moves to previous line", window.source.CaretIndex == doc.GetLineByNumber(2).EndOffset, "caret=" + window.source.CaretIndex);
            window.source.CaretIndex = third.Offset; LongLineBench.Idle();
            key(Key.Right);
            check("Right at line start moves one character", window.source.CaretIndex == third.Offset + 1, "caret=" + window.source.CaretIndex + ", start=" + third.Offset);
            window.source.CaretIndex = third.Offset + 2; LongLineBench.Idle();
            key(Key.Home);
            check("Home reaches the line start", window.source.CaretIndex == third.Offset, "caret=" + window.source.CaretIndex);
            window.source.CaretIndex = third.Offset; LongLineBench.Idle();
            key(Key.Back);
            bool joined = doc.GetText(doc.GetLineByNumber(2)) == "셋째 줄";
            check("Backspace at line start joins in one press", joined, "line2='" + doc.GetText(doc.GetLineByNumber(2)) + "'");
            window.Undo(); LongLineBench.Idle();

            // #7: a format command waits for the pending IME commit instead of letting the
            // late syllable overwrite the word it selected.
            DocumentLine word = doc.GetLineByNumber(6);
            int at = word.Offset + 5;
            window.source.Select(at, 0); LongLineBench.Idle();
            string beforeBold = window.text;
            window.source.SimulateImeCommit(1);
            window.ToggleFormat("bold"); LongLineBench.Idle();
            bool parked = window.text == beforeBold;
            area.PerformTextInput("가"); LongLineBench.Idle();
            string line6 = doc.GetText(doc.GetLineByNumber(6));
            check("7 bold waits for IME commit", parked && line6 == "**가나다라마가가나다라마**", "parked=" + parked + ", line='" + line6 + "'");
            window.Undo(); LongLineBench.Idle(); window.Undo(); LongLineBench.Idle();
            check("7 undo restores text", window.text == original, "same=" + (window.text == original));
            window.ToggleFormat("bold"); LongLineBench.Idle();
            check("7 bold runs at once without IME", window.text != original, "changed=" + (window.text != original));
            window.Undo(); LongLineBench.Idle();

            // #21: Ctrl+Shift+C copies readable text in edit and read mode.
            DocumentLine bold = doc.GetLineByNumber(5);
            window.source.Select(bold.Offset, bold.Length); LongLineBench.Idle();
            bool editCopy = window.CopyReadable(); string editText = ClipboardText();
            window.SetMode(false); LongLineBench.Idle();
            window.source.Select(bold.Offset, bold.Length); LongLineBench.Idle();
            bool readCopy = window.CopyReadable(); string readText = ClipboardText();
            window.SetMode(true); LongLineBench.Idle();
            check("21 readable copy in both modes", editCopy && readCopy && editText == "굵게 시작" && readText == "굵게 시작", "edit='" + editText + "', read='" + readText + "'");

            // #22: an endpoint at the left edge of a formatted heading includes its hidden `## `.
            window.source.CaretIndex = doc.GetLineByNumber(1).Offset; LongLineBench.Idle();
            DocumentLine heading = doc.GetLineByNumber(4), after = doc.GetLineByNumber(5);
            var session = window.livePreview;
            session.BeginPress();
            area.Caret.Offset = heading.Offset + 3;
            area.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(area, after.EndOffset, heading.Offset + 3);
            session.SnapHiddenPrefixes();
            int upStart = window.source.SelectionStart;
            session.EndPress(); LongLineBench.Idle();
            check("22 upward drag includes hidden heading prefix", upStart == heading.Offset, "start=" + upStart + ", line=" + heading.Offset);
            window.source.CaretIndex = doc.GetLineByNumber(1).Offset; LongLineBench.Idle();
            session.BeginPress();
            area.Caret.Offset = heading.Offset + 3;
            area.Selection = ICSharpCode.AvalonEdit.Editing.Selection.Create(area, doc.GetLineByNumber(3).Offset, heading.Offset + 3);
            session.SnapHiddenPrefixes();
            int downEnd = window.source.SelectionStart + window.source.SelectionLength;
            session.EndPress(); LongLineBench.Idle();
            check("22 downward drag stops before hidden heading prefix", downEnd == heading.Offset, "end=" + downEnd + ", line=" + heading.Offset);
            // #20: live paste escapes softly; the preview shows entities and backslash escapes decoded.
            softPasteLiteral = true;
            string pasted;
            try { pasted = ClipboardMarkdown("<html><body><!--StartFragment--><p>OECD Data Explorer - Historical <a href=\"https://data-explorer.oecd.org/\">https://data-explorer.oecd.org/</a> <b>굵게</b> a*b</p><!--EndFragment--></body></html>"); }
            finally { softPasteLiteral = false; }
            check("20 live paste keeps hyphens readable", pasted.Contains("Explorer - Historical") && pasted.Contains("](https://data-explorer.oecd.org/)") && pasted.Contains("**굵게**") && pasted.Contains("a\\*b") && !pasted.Contains("&#"), "pasted='" + pasted.Replace("\n", "/") + "'");
            doc.Insert(doc.TextLength, "\nA &#45; B \\* c &amp; d\n"); LongLineBench.Idle();
            DocumentLine entityLine = doc.GetLineByNumber(doc.LineCount - 1);
            window.source.CaretIndex = doc.GetLineByNumber(1).Offset; LongLineBench.Idle();
            var entityInfo = window.livePreview.Info(entityLine);
            int glyphs = 0, hidden = 0;
            foreach (var replacement in entityInfo.Replacements) { if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Glyph) glyphs++; if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Hidden) hidden++; }
            check("20 preview decodes entities and escapes", glyphs == 2 && hidden == 1, "glyphs=" + glyphs + ", hidden=" + hidden);
            // #18/#17: live table bar appears in a table and edits the source; alignment rewrites the delimiter.
            doc.Insert(doc.TextLength, "\n| a | b |\n| --- | --- |\n| 1 | 2 |\n"); LongLineBench.Idle();
            int tableEnd = doc.LineCount - 1;
            window.source.CaretIndex = doc.GetLineByNumber(tableEnd).Offset + 2; LongLineBench.Idle(); window.UpdateTools(); LongLineBench.Idle();
            bool barShown = window.tableToolsHost.Visibility == System.Windows.Visibility.Visible;
            window.EditTable(0); LongLineBench.Idle();
            bool rowAdded = doc.GetText(doc.GetLineByNumber(tableEnd + 1)).StartsWith("|", StringComparison.Ordinal);
            window.source.CaretIndex = doc.GetLineByNumber(tableEnd).Offset + 2; LongLineBench.Idle();
            window.EditTable(7); LongLineBench.Idle();
            string delimiter = doc.GetText(doc.GetLineByNumber(tableEnd - 1));
            window.source.CaretIndex = doc.GetLineByNumber(1).Offset; LongLineBench.Idle(); window.UpdateTools(); LongLineBench.Idle();
            bool barHidden = window.tableToolsHost.Visibility != System.Windows.Visibility.Visible;
            check("18 live table bar edits the source", barShown && rowAdded && delimiter.Contains(":---:") && barHidden, "shown=" + barShown + ", row=" + rowAdded + ", delimiter='" + delimiter + "', hidden=" + barHidden);
            window.Undo(); LongLineBench.Idle(); window.Undo(); LongLineBench.Idle();
            // #14: reading mode ticks checkboxes, also after edit/read round trips.
            doc.Insert(doc.TextLength, "- [ ] 할 일\n"); LongLineBench.Idle();
            DocumentLine taskLine = doc.GetLineByNumber(doc.LineCount - 1);
            for (int round = 0; round < 2; round++) { window.SetMode(false); LongLineBench.Idle(); window.SetMode(true); LongLineBench.Idle(); }
            window.SetMode(false); LongLineBench.Idle();
            window.livePreview.ToggleTask(taskLine, 2); LongLineBench.Idle();
            string ticked = doc.GetText(taskLine);
            window.livePreview.ToggleTask(taskLine, 2); LongLineBench.Idle();
            string unticked = doc.GetText(taskLine);
            window.SetMode(true); LongLineBench.Idle();
            check("14 reading mode toggles checkbox", ticked == "- [x] 할 일" && unticked == "- [ ] 할 일", "ticked='" + ticked + "', unticked='" + unticked + "'");
            UserFixWatch.Mark("snapshot userfix"); Program.Snapshot(window, Path.Combine(outDir, "userfix.png")); UserFixWatch.Mark("snapshot userfix done");
            // Screens for the E (UI) mockup: dark mode with every block kind, and a narrow window.
            window.source.Document.Text = "# 다크 모드 확인\n\n본문 **굵게** `인라인 코드` 와 [링크](https://example.com).\n\n- [x] 끝난 일\n- [ ] 할 일\n1. 하나\n2. 둘\n\n> 인용문입니다.\n\n| 이름 | 설명 | 값 |\n|:---|:---:|---:|\n| 사과 | 빨간 과일 | 1,200 |\n\n---\n\n```\n코드 블록 안입니다.\n```\n";
            LongLineBench.Idle(); window.source.CaretIndex = window.source.Document.TextLength; LongLineBench.Idle();
            window.SetDark(true); LongLineBench.Idle();
            for (int i = 0; i < 6; i++) { System.Threading.Thread.Sleep(50); LongLineBench.Idle(); }
            UserFixWatch.Mark("snapshot dark"); Program.Snapshot(window, Path.Combine(outDir, "e-dark.png")); UserFixWatch.Mark("snapshot dark done");
            window.SetDark(false); LongLineBench.Idle();
            UserFixWatch.Mark("snapshot light"); Program.Snapshot(window, Path.Combine(outDir, "e-light.png")); UserFixWatch.Mark("snapshot light done");
            window.Width = 380; LongLineBench.Idle();
            for (int i = 0; i < 6; i++) { System.Threading.Thread.Sleep(50); LongLineBench.Idle(); }
            UserFixWatch.Mark("snapshot narrow"); Program.Snapshot(window, Path.Combine(outDir, "e-narrow.png")); UserFixWatch.Mark("snapshot narrow done");
            window.Width = 900; LongLineBench.Idle();
        } catch (Exception ex) { all = false; report.Append("EXCEPTION ").Append(ex).Append('\n'); }
        finally { if (window != null) { window.saved = window.text; window.Close(); } ProbeDefaults.Live = old; try { File.Delete(file); } catch (IOException) { } }
        UserFixWatch.Mark("retest-controls"); VerifyRetestControls(outDir, check);
        UserFixWatch.Mark("selection-undo"); VerifySelectionUndo(outDir, check);
        UserFixWatch.Mark("save-close"); VerifySaveCloseDialogs(check);
        UserFixWatch.Mark("composition-close"); VerifyCompositionClose(outDir, check);
        UserFixWatch.Mark("close-open-cancel"); VerifyCloseOpenCancel(check);
        UserFixWatch.Mark("composition-settle"); VerifyCompositionSettle(check);
        UserFixWatch.Mark("unrelated-prompt"); VerifyUnrelatedPromptClose(check);
        UserFixWatch.Mark("search-live"); VerifySearchLiveUpdate(check);
        UserFixWatch.Mark("standin"); VerifyStandInBar(check);
        UserFixWatch.Mark("toolbar-narrow"); VerifyToolbarNarrowOverflow(check);
        UserFixWatch.Mark("heading-size"); VerifyLiveHeadingSizeBox(check);
        UserFixWatch.Mark("image-drop"); VerifyImageDrop(outDir, check);
        UserFixWatch.Mark("edit-exit"); VerifyEditExitAndViewSwitch(check);
        report.Append(all ? "\nResult: PASS\n" : "\nResult: FAIL\n");
        File.WriteAllText(Path.Combine(outDir, "userfix-probe.txt"), report.ToString(), new UTF8Encoding(false));
        if (all) File.WriteAllText(Path.Combine(outDir, "userfix-probe-PASS.txt"), "PASS\n");
        UserFixWatch.Stop();
        System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        return all ? 0 : 1;
    }
    static System.Windows.Controls.Button RetestHistoryButton(System.Windows.DependencyObject node,bool redo) {
        var button=node as System.Windows.Controls.Button;
        if(button!=null&&Object.Equals(button.ToolTip,redo?"다시 실행 (Ctrl+Y)":"실행 취소 (Ctrl+Z)"))return button;
        foreach(object child in System.Windows.LogicalTreeHelper.GetChildren(node)){var element=child as System.Windows.DependencyObject;if(element==null)continue;var found=RetestHistoryButton(element,redo);if(found!=null)return found;}
        return null;
    }
    // A split button's chevron half is 14px wide by user request (2026-10-24): it deliberately
    // misses the 24px target, but it is not a standalone hit target. Its sibling is the main half.
    static bool RetestSplitChevron(System.Windows.Controls.Button button){if(!(button.Width<=Ui.SplitArrowWidth||button.ActualWidth<=Ui.SplitArrowWidth))return false;var panel=button.Parent as System.Windows.Controls.Panel;if(panel==null)return false;foreach(object child in panel.Children){var other=child as System.Windows.Controls.Button;if(other!=null&&!Object.ReferenceEquals(other,button)&&(other.Width>14||other.ActualWidth>14))return true;}return false;}
    // 2026-10-28 (user): the 글자색 main half is 17px so its A sits close to the arrow; the split pair as a whole
    // (main + chevron, one hover surface) is the hit target and must still be >= 24px wide.
    static bool RetestSplitMain(System.Windows.Controls.Button button){if(button.ActualHeight<24-.1)return false;var panel=button.Parent as System.Windows.Controls.Panel;if(panel==null)return false;double total=0;bool chevron=false;foreach(object child in panel.Children){var other=child as System.Windows.Controls.Button;if(other==null)continue;total+=other.ActualWidth;if(!Object.ReferenceEquals(other,button)&&other.ActualWidth<=Ui.SplitArrowWidth+.1)chevron=true;}return chevron&&total>=24-.1;}
    static void RetestRibbonTargets(System.Windows.DependencyObject node,ref int count,ref int small,ref int clipped){var button=node as System.Windows.Controls.Button;if(button!=null){count++;if((button.ActualWidth<24-.1||button.ActualHeight<24-.1)&&!RetestSplitChevron(button)&&!RetestSplitMain(button))small++;var content=button.Content as System.Windows.FrameworkElement;if(content!=null&&content.DesiredSize.Width>button.ActualWidth-button.Padding.Left-button.Padding.Right-button.BorderThickness.Left-button.BorderThickness.Right+.5)clipped++;}foreach(object child in System.Windows.LogicalTreeHelper.GetChildren(node)){var element=child as System.Windows.DependencyObject;if(element!=null)RetestRibbonTargets(element,ref count,ref small,ref clipped);}}
    static void RetestWait(int milliseconds){var frame=new System.Windows.Threading.DispatcherFrame();var timer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};timer.Tick+=delegate{timer.Stop();frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);LongLineBench.Idle();}
    static void VerifyRetestControls(string outDir,Action<string,bool,string> check){
        bool old=ProbeDefaults.Live;string file=Path.Combine(outDir,"retest-controls.md");File.WriteAllText(file,"loaded document\n",new UTF8Encoding(false));
        try{foreach(bool live in new[]{true}){
            ProbeDefaults.Live=live;MainWindow w=null;
            try{
                w=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=1200,Height=700};w.Show();w.textMode=false;w.SetText("");w.SetMode(true);LongLineBench.Idle();
                var undo=RetestHistoryButton(w.ribbonHost,false);var redo=RetestHistoryButton(w.ribbonHost,true);string mode=live?"live":"basic";
                Action<string,bool,bool> state=delegate(string step,bool u,bool r){check("T4 "+mode+" "+step,undo!=null&&redo!=null&&undo.IsEnabled==u&&redo.IsEnabled==r,"undo="+(undo==null?"missing":undo.IsEnabled.ToString())+", redo="+(redo==null?"missing":redo.IsEnabled.ToString()));};
                state("new document",false,false);
                int targets=0,small=0,clipped=0;RetestRibbonTargets(w.formatBar,ref targets,ref small,ref clipped);
                check("UI "+mode+" compact ribbon hit targets and content",targets>=18&&small==0&&clipped==0,"buttons="+targets+", small="+small+", clipped="+clipped+", width="+RibbonContentWidth(w).ToString("0.##"));
                w.source.TextArea.PerformTextInput("edit");LongLineBench.Idle();state("after edit",true,false);
                w.Undo();LongLineBench.Idle();state("undo to start",false,true);
                w.Redo();LongLineBench.Idle();state("redo to end",true,false);
                var edited=w.activeDocument;w.CreateTab();LongLineBench.Idle();state("new tab",false,false);
                w.SwitchTab(edited);LongLineBench.Idle();state("switch back",true,false);
                w.LoadFileCore(file);w.SetMode(true);LongLineBench.Idle();state("load",false,false);
                w.OpenTablePicker();LongLineBench.Idle();var menu=w.tablePickerMenu;
                check("F1 "+mode+" click opens grid",menu!=null&&menu.IsOpen,"open="+(menu!=null&&menu.IsOpen));
                // A synthetic MouseLeave cannot move the captured popup's native pointer.
                // Exercise the production polling path with an injected screen position instead.
                w.tablePickerHoverOpened=true;System.Windows.Point pickerPoint=new System.Windows.Point(-100000,-100000);w.tablePickerPointerPosition=delegate{return pickerPoint;};w.QueueTablePickerClose();w.PollTablePicker(DateTime.UtcNow);
                if(menu!=null)menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=System.Windows.UIElement.MouseLeaveEvent});
                w.tableButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=System.Windows.UIElement.MouseLeaveEvent});RetestWait(450);
                check("F1 "+mode+" leave closes grid",menu!=null&&!menu.IsOpen,"open="+(menu!=null&&menu.IsOpen));
                if(menu!=null)menu.IsOpen=false;
                w.OpenTablePicker();LongLineBench.Idle();menu=w.tablePickerMenu;w.tablePickerHoverOpened=true;pickerPoint=menu.PointToScreen(new System.Windows.Point(5,5));w.QueueTablePickerClose();w.PollTablePicker(DateTime.UtcNow);
                w.tableButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=System.Windows.UIElement.MouseLeaveEvent});
                menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=System.Windows.UIElement.MouseEnterEvent});RetestWait(260);
                check("F1 "+mode+" enter grid cancels close",menu.IsOpen,"open="+menu.IsOpen);
                pickerPoint=new System.Windows.Point(-100000,-100000);w.PollTablePicker(DateTime.UtcNow);menu.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=System.Windows.UIElement.MouseLeaveEvent});RetestWait(450);
                check("F1 "+mode+" leave grid closes",!menu.IsOpen,"open="+menu.IsOpen);menu.IsOpen=false;
                w.tableButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));LongLineBench.Idle();
                check("F1 "+mode+" keyboard click opens",w.tablePickerMenu!=null&&w.tablePickerMenu.IsOpen,"open="+(w.tablePickerMenu!=null&&w.tablePickerMenu.IsOpen));if(w.tablePickerMenu!=null)w.tablePickerMenu.IsOpen=false;
                w.WindowState=System.Windows.WindowState.Minimized;LongLineBench.Idle();w.queuedSaveChoices.Enqueue(SaveChoice.Cancel);w.AskSave();
                check("F6 "+mode+" minimized save owner restored before choice",w.WindowState==System.Windows.WindowState.Normal,"state="+w.WindowState);
            }catch(Exception ex){check("retest controls exception",false,ex.ToString());}
            finally{if(w!=null){if(w.tablePickerMenu!=null)w.tablePickerMenu.IsOpen=false;w.saved=w.text;w.Close();}}
        }}finally{ProbeDefaults.Live=old;File.Delete(file);}
    }
    // One editing action = one undo step, and Ctrl+Z puts the replaced text and the previous
    // selection back (2026-10-21 Argon follow-up ③): typing over a selection (one character, a
    // whole run, an IME commit), pasting over one, and a drag-move. Both live Markdown editing and
    // TXT mode use the same source surface, so both are run.
    static void VerifySelectionUndo(string outDir, Action<string, bool, string> check) {
        bool old = ProbeDefaults.Live; ProbeDefaults.Live = true; MainWindow window = null;
        string liveFile = Path.Combine(outDir, "undo-live.md"), plainFile = Path.Combine(outDir, "undo-plain.txt");
        File.WriteAllText(liveFile, "live start\n", new UTF8Encoding(false));
        File.WriteAllText(plainFile, "plain start\n", new UTF8Encoding(false));
        try {
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 }; window.Show(); window.Left = -20000;
            window.LoadFileCore(liveFile); LongLineBench.Idle(); window.SetMode(true); LongLineBench.Idle();
            SelectionUndoCases(window, check, "live");
            window.LoadFileCore(plainFile); LongLineBench.Idle(); window.SetMode(true); LongLineBench.Idle();
            check("undo cases run on the TXT surface", window.PlainText && window.CodeMode && window.livePreview == null, "plain=" + window.PlainText + ", code=" + window.CodeMode);
            SelectionUndoCases(window, check, "txt");
        } catch (Exception ex) { check("selection undo exception", false, ex.ToString()); }
        finally { if (window != null) { window.saved = window.text; window.Close(); } ProbeDefaults.Live = old; try { File.Delete(liveFile); } catch (IOException) { } try { File.Delete(plainFile); } catch (IOException) { } }
    }
    static void SelectionUndoCases(MainWindow window, Action<string, bool, string> check, string mode) {
        var area = window.source.TextArea;
        // (a) one typed character replaces a selection.
        window.SetText("abcdef"); LongLineBench.Idle();
        window.source.Select(1, 3); LongLineBench.Idle();
        area.PerformTextInput("X"); LongLineBench.Idle();
        string typed = window.source.Document.Text;
        window.Undo(); LongLineBench.Idle();
        string typedUndo = window.source.Document.Text; int typedStart = window.source.SelectionStart, typedLength = window.source.SelectionLength;
        window.Redo(); LongLineBench.Idle();
        check(mode + " undo: typing one character over a selection is one step",
            typed == "aXef" && typedUndo == "abcdef" && typedStart == 1 && typedLength == 3 && window.source.Document.Text == typed,
            "typed='" + typed + "', undo='" + typedUndo + "' sel=" + typedStart + "/" + typedLength + ", redo='" + window.source.Document.Text + "'");
        // (a) a whole typed run stays one step, replacement included (the merge rule continues).
        window.SetText("abcdef"); LongLineBench.Idle();
        window.source.Select(1, 3); LongLineBench.Idle();
        foreach (string ch in new[] { "X", "y", "z" }) { area.PerformTextInput(ch); LongLineBench.Idle(); }
        string run = window.source.Document.Text;
        window.Undo(); LongLineBench.Idle();
        string runUndo = window.source.Document.Text; int runStart = window.source.SelectionStart, runLength = window.source.SelectionLength;
        window.Redo(); LongLineBench.Idle();
        check(mode + " undo: typing three characters over a selection is one step",
            run == "aXyzef" && runUndo == "abcdef" && runStart == 1 && runLength == 3 && window.source.Document.Text == run,
            "typed='" + run + "', undo='" + runUndo + "' sel=" + runStart + "/" + runLength + ", redo='" + window.source.Document.Text + "'");
        // (a) an IME-committed syllable over a selection.
        window.SetText("abcdef"); LongLineBench.Idle();
        window.source.Select(1, 3); LongLineBench.Idle();
        window.source.SimulateImeCommit(1);
        area.PerformTextInput("가"); LongLineBench.Idle();
        string ime = window.source.Document.Text;
        window.Undo(); LongLineBench.Idle();
        string imeUndo = window.source.Document.Text; int imeStart = window.source.SelectionStart, imeLength = window.source.SelectionLength;
        window.Redo(); LongLineBench.Idle();
        check(mode + " undo: an IME commit over a selection is one step",
            ime == "a가ef" && imeUndo == "abcdef" && imeStart == 1 && imeLength == 3 && window.source.Document.Text == ime,
            "typed='" + ime + "', undo='" + imeUndo + "' sel=" + imeStart + "/" + imeLength + ", redo='" + window.source.Document.Text + "'");
        // (c) pasting over a selection.
        window.SetText("abcdef"); LongLineBench.Idle();
        window.source.Select(1, 3); LongLineBench.Idle();
        window.PasteData(new System.Windows.DataObject(System.Windows.DataFormats.UnicodeText, "PQ"), true); LongLineBench.Idle();
        string pasted = window.source.Document.Text;
        window.Undo(); LongLineBench.Idle();
        string pasteUndo = window.source.Document.Text; int pasteStart = window.source.SelectionStart, pasteLength = window.source.SelectionLength;
        window.Redo(); LongLineBench.Idle();
        check(mode + " undo: pasting over a selection is one step",
            pasted == "aPQef" && pasteUndo == "abcdef" && pasteStart == 1 && pasteLength == 3 && window.source.Document.Text == pasted,
            "pasted='" + pasted + "', undo='" + pasteUndo + "' sel=" + pasteStart + "/" + pasteLength + ", redo='" + window.source.Document.Text + "'");
        // (b) dragging the selection forward and backward is a move: one step, source selection back.
        window.SetText("abcdef"); LongLineBench.Idle();
        window.source.Select(1, 3); LongLineBench.Idle();
        window.source.NoteDragSource();
        bool dragMoved = window.MoveSelectionTo(5); LongLineBench.Idle();
        string moved = window.source.Document.Text; int movedStart = window.source.SelectionStart, movedLength = window.source.SelectionLength;
        window.Undo(); LongLineBench.Idle();
        string moveUndo = window.source.Document.Text; int moveStart = window.source.SelectionStart, moveLength = window.source.SelectionLength;
        window.Redo(); LongLineBench.Idle();
        check(mode + " undo: dragging the selection forward is one step",
            dragMoved && moved == "aebcdf" && movedStart == 2 && movedLength == 3 && moveUndo == "abcdef" && moveStart == 1 && moveLength == 3 && window.source.Document.Text == moved,
            "moved=" + dragMoved + " '" + moved + "' sel=" + movedStart + "/" + movedLength + ", undo='" + moveUndo + "' sel=" + moveStart + "/" + moveLength + ", redo='" + window.source.Document.Text + "'");
        window.SetText("abcdef"); LongLineBench.Idle();
        window.source.Select(3, 2); LongLineBench.Idle();
        window.source.NoteDragSource();
        bool dragBack = window.MoveSelectionTo(0); LongLineBench.Idle();
        string movedBack = window.source.Document.Text; int backMoveStart = window.source.SelectionStart, backMoveLength = window.source.SelectionLength;
        window.Undo(); LongLineBench.Idle();
        string backMoveUndo = window.source.Document.Text; int backStart = window.source.SelectionStart, backLength = window.source.SelectionLength;
        window.Redo(); LongLineBench.Idle();
        check(mode + " undo: dragging the selection backward is one step",
            dragBack && movedBack == "deabcf" && backMoveStart == 0 && backMoveLength == 2 && backMoveUndo == "abcdef" && backStart == 3 && backLength == 2 && window.source.Document.Text == movedBack,
            "moved=" + dragBack + " '" + movedBack + "' sel=" + backMoveStart + "/" + backMoveLength + ", undo='" + backMoveUndo + "' sel=" + backStart + "/" + backLength + ", redo='" + window.source.Document.Text + "'");
        // A drop inside the selection moves nothing and records nothing.
        window.SetText("abcdef"); LongLineBench.Idle();
        window.source.Select(1, 3); LongLineBench.Idle();
        string beforeInside = window.source.Document.Text; bool undoBeforeInside = window.History.CanUndo;
        window.source.NoteDragSource();
        bool refused = !window.MoveSelectionTo(2);
        check(mode + " drag: dropping inside the selection is refused",
            refused && window.source.Document.Text == beforeInside && window.History.CanUndo == undoBeforeInside && window.source.SelectionStart == 1 && window.source.SelectionLength == 3,
            "refused=" + refused + ", text='" + window.source.Document.Text + "', undoBefore=" + undoBeforeInside);
        // A drop exactly at the end of the selection keeps the text too: no edit and no history entry.
        window.SetText("abcdef"); LongLineBench.Idle();
        window.source.Select(1, 3); LongLineBench.Idle();
        string beforeEnd = window.source.Document.Text; bool undoBeforeEnd = window.History.CanUndo;
        window.source.NoteDragSource();
        bool refusedEnd = !window.MoveSelectionTo(4);
        check(mode + " drag: dropping at the end of the selection moves nothing",
            refusedEnd && window.source.Document.Text == beforeEnd && window.History.CanUndo == undoBeforeEnd && window.source.SelectionStart == 1 && window.source.SelectionLength == 3,
            "refused=" + refusedEnd + ", text='" + window.source.Document.Text + "', undoBefore=" + undoBeforeEnd + ", canUndo=" + window.History.CanUndo);
        window.source.ClearDragSource();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr RetestSendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    static System.Windows.Window FindSaveConfirmDialog(string title){
        if(System.Windows.Application.Current==null)return null;
        foreach(System.Windows.Window candidate in System.Windows.Application.Current.Windows)if(candidate.IsVisible&&candidate.Title==title)return candidate;
        return null;
    }
    static int CountSaveConfirmDialogs(string title){
        int count=0;if(System.Windows.Application.Current==null)return 0;
        foreach(System.Windows.Window candidate in System.Windows.Application.Current.Windows)if(candidate.IsVisible&&candidate.Title==title)count++;
        return count;
    }
    // The prompt that is open now: the field ShowSaveConfirmation/AskSaveCore set, else by title.
    static System.Windows.Window VisibleSaveDialog(string title){
        System.Windows.Window dialog=saveConfirmationDialog;
        if(dialog!=null&&dialog.IsVisible)return dialog;
        return FindSaveConfirmDialog(title);
    }
    // b50: a close from a minimized window shows the save prompt without restoring the window.
    // The prompt is unowned and topmost while it activates; the owner stays minimized until it is answered.
    static bool VerifyMinimizedSaveClose(MainWindow owner,Action<string,bool,string> check){
        string title=owner.T("변경 내용 저장");System.Windows.Threading.DispatcherTimer poll=null;System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();
        System.Windows.Window dialog=null;bool found=false,minimized=true,unowned=false,raised=false,done=false;long appearMs=-1,closeAt=-1;
        try{
            /* Input priority: a Background timer ticks only after the dialog's own layout and render work. */
            poll=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) {Interval=TimeSpan.FromMilliseconds(30)};
            poll.Tick+=delegate{
                System.Windows.Window candidate=FindSaveConfirmDialog(title);
                if(candidate==null){
                    if(clock.ElapsedMilliseconds>3000&&!done){done=true;poll.Stop();System.Windows.Window stuck=saveConfirmationDialog;if(stuck!=null&&stuck.IsVisible)stuck.Close();}
                    return;
                }
                if(!found){found=true;dialog=candidate;appearMs=clock.ElapsedMilliseconds;closeAt=appearMs+120;unowned=candidate.Owner==null;}
                minimized=minimized&&owner.WindowState==System.Windows.WindowState.Minimized;
                raised=raised||candidate.Topmost||candidate.IsActive;
                if(!done&&clock.ElapsedMilliseconds>=closeAt){done=true;poll.Stop();candidate.Close();}
            };
            clock.Start();poll.Start();
            RetestSendMessage(new System.Windows.Interop.WindowInteropHelper(owner).Handle,0x112,new IntPtr(0xF060),IntPtr.Zero);
            for(int waited=0;!done&&waited<3200;waited+=50)RetestWait(50);
            if(!found){poll.Stop();System.Windows.Window stuck=saveConfirmationDialog;if(stuck!=null&&stuck.IsVisible)stuck.Close();check("F6 minimized close opens save dialog",false,"timeout after 3000ms");return false;}
            double tick=1000.0/System.Diagnostics.Stopwatch.Frequency;
            string trace=" (hook→process "+((closeTraceProcess-closeTraceHook)*tick).ToString("0")+"ms, →show "+((closeTraceShow-closeTraceHook)*tick).ToString("0")+"ms, →rendered "+((closeTraceRendered-closeTraceHook)*tick).ToString("0")+"ms)";
            // Our part (close message -> ShowDialog) is held to 50ms. The rest is WPF creating and painting a new
            // window, 210-340ms on the hidden test desktop (bimodal across runs), so the visible bound is 400ms.
            double ours=(closeTraceShow-closeTraceHook)*tick;
            check("F6 minimized close reaches ShowDialog within 50ms",closeTraceHook!=0&&ours<=50,"ms="+ours.ToString("0"));
            check("F6 minimized close opens save dialog within 400ms",appearMs>=0&&appearMs<=400,"ms="+appearMs+trace);
            check("F6 minimized close keeps owner minimized",minimized,"state="+owner.WindowState);
            check("F6 minimized save dialog is unowned",unowned,"owner null="+unowned);
            check("F6 minimized save dialog is active or was topmost",raised,"raised="+raised);
            bool open=owner.IsLoaded&&!owner.placementClosed&&owner.WindowState==System.Windows.WindowState.Minimized;
            check("F6 minimized cancel leaves owner minimized and open",open,"open="+open+", state="+owner.WindowState);
            return open;
        }finally{
            if(poll!=null)poll.Stop();
            if(dialog!=null&&dialog.IsVisible)dialog.Close();
        }
    }
    static bool VerifyNormalSaveClose(MainWindow owner,MainWindow other,Action<string,bool,string> check){
        string title=owner.T("변경 내용 저장");System.Windows.Threading.DispatcherTimer poll=null;System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();
        System.Windows.Window dialog=null;bool found=false,owned=false,visible=false,active=false,raised=false,raisedVisible=false,raisedActive=false,done=false,queuedOther=false;long closeAt=-1;
        try{
            /* Input priority: a Background timer ticks only after the dialog's own layout and render work. */
            poll=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) {Interval=TimeSpan.FromMilliseconds(30)};
            poll.Tick+=delegate{
                System.Windows.Window candidate=FindSaveConfirmDialog(title);
                if(candidate==null){
                    if(clock.ElapsedMilliseconds>3000&&!done){done=true;poll.Stop();System.Windows.Window stuck=saveConfirmationDialog;if(stuck!=null&&stuck.IsVisible)stuck.Close();}
                    return;
                }
                if(!found){found=true;dialog=candidate;owned=candidate.Owner==owner;visible=candidate.IsVisible;active=candidate.IsActive;closeAt=clock.ElapsedMilliseconds+150;}
                if(!raised&&clock.ElapsedMilliseconds>=closeAt-60&&other!=null&&!other.placementClosed){
                    raised=true;RetestSendMessage(new System.Windows.Interop.WindowInteropHelper(other).Handle,0x112,new IntPtr(0xF060),IntPtr.Zero);
                    // The deferred close is only raised here to prove SaveCloseMessage re-raises the open
                    // prompt. The queued close of `other` must survive this prompt's Cancel and run as the
                    // next batch (K3), so it is left in closeRequests.
                    queuedOther=closeRequests.Contains(other);raisedVisible=candidate.IsVisible;raisedActive=candidate.IsActive;
                }
                if(!done&&clock.ElapsedMilliseconds>=closeAt){done=true;poll.Stop();candidate.Close();}
            };
            clock.Start();poll.Start();
            RetestSendMessage(new System.Windows.Interop.WindowInteropHelper(owner).Handle,0x112,new IntPtr(0xF060),IntPtr.Zero);
            for(int waited=0;!done&&waited<3200;waited+=50)RetestWait(50);
            if(!found){poll.Stop();System.Windows.Window stuck=saveConfirmationDialog;if(stuck!=null&&stuck.IsVisible)stuck.Close();check("F6 save dialog opens after close",false,"timeout after 3000ms");return false;}
            check("F6 save dialog is owned visible and active",owned&&visible&&active,"owner="+owned+", visible="+visible+", active="+active);
            check("F6 another window close raises the existing dialog owner",raised&&raisedVisible&&raisedActive,"raised="+raised+", visible="+raisedVisible+", active="+raisedActive);
            // The cancelled batch ends after the dialog closes; the queued close of `other` (clean) then runs.
            for(int waited=0;other!=null&&!other.placementClosed&&waited<600;waited+=50)RetestWait(50);
            bool ownerOpen=owner.IsLoaded&&!owner.placementClosed,otherOpen=other!=null&&other.IsLoaded&&!other.placementClosed;
            check("F6 cancel keeps the close queued during the prompt",queuedOther&&!otherOpen,"queued="+queuedOther+", otherOpen="+otherOpen);
            check("F6 cancel leaves the owner window open",ownerOpen,"owner="+ownerOpen);
            return ownerOpen;
        }finally{
            if(poll!=null)poll.Stop();
            if(dialog!=null&&dialog.IsVisible)dialog.Close();
        }
    }
    static void VerifySaveCloseDialogs(Action<string,bool,string> check){
        MainWindow owner=null,other=null;bool oldDeferred=testDeferredClose;
        try{
            owner=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=800,Height=600};owner.Show();owner.SetText("unsaved generated close test");owner.saved="";
            other=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=800,Height=600};other.Show();LongLineBench.Idle();
            testDeferredClose=true;
            owner.WindowState=System.Windows.WindowState.Minimized;LongLineBench.Idle();
            VerifyMinimizedSaveClose(owner,check);
            if(owner.IsLoaded&&!owner.placementClosed){owner.WindowState=System.Windows.WindowState.Normal;LongLineBench.Idle();VerifyNormalSaveClose(owner,other,check);}
        }catch(Exception ex){check("F6 dialog exception",false,ex.ToString());}
        finally{testDeferredClose=oldDeferred;closeRequests.Clear();foreach(var w in new[]{other,owner})if(w!=null&&!w.placementClosed){w.saved=w.text;w.Close();}}
    }
    // K1: a close that lands while an IME syllable is still in flight must wait for the commit.
    // Before the fix FinishComposition+Flush saw a clean tab and the window closed, losing the syllable.
    static void VerifyCompositionClose(string outDir,Action<string,bool,string> check){
        MainWindow window=null;bool oldDeferred=testDeferredClose,oldLive=ProbeDefaults.Live;ProbeDefaults.Live=true;
        string file=Path.Combine(Path.GetFullPath(outDir),"close-ime.md");
        try{
            File.WriteAllText(file,"abc",new UTF8Encoding(false));
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=800,Height=600};window.Show();window.Left=-20000;
            window.LoadFileCore(file);LongLineBench.Idle();window.SetMode(true);LongLineBench.Idle();
            check("K1 close composition probe starts clean",!window.HasUnsavedChanges&&window.source.Document.Text=="abc","dirty="+window.HasUnsavedChanges+", text='"+window.source.Document.Text+"'");
            testDeferredClose=true;
            // The syllable is not in the document yet: AvalonEdit composes in the IMM32 window.
            window.source.SimulateImeComposition();window.source.SimulateImeCommit(1);
            window.queuedSaveChoices.Enqueue(SaveChoice.Save);
            window.Close();
            RetestWait(300);
            bool waited=!window.placementClosed;
            if(waited){window.source.TextArea.PerformTextInput("가");LongLineBench.Idle();}
            RetestWait(200);
            string saved=File.Exists(file)?File.ReadAllText(file):"";
            check("K1 close waits for the pending IME commit",waited&&window.placementClosed&&saved.IndexOf("가")>=0,"waited="+waited+", closed="+window.placementClosed+", saved='"+saved+"'");
        }catch(Exception ex){check("K1 close composition exception",false,ex.ToString());}
        finally{testDeferredClose=oldDeferred;if(window!=null&&!window.placementClosed){window.saved=window.text;window.Close();}ProbeDefaults.Live=oldLive;try{File.Delete(file);}catch(IOException){}}
    }
    // K2: cancelling an in-flight open inside the close batch must not pump the dispatcher. A callback
    // queued before the batch used to run from CancelOpenBeforeClose's DispatcherFrame while closingBatch
    // was still true, so key shortcuts and timers ran half way through the close.
    static void VerifyCloseOpenCancel(Action<string,bool,string> check){
        MainWindow window=null;bool oldDeferred=testDeferredClose,oldLive=ProbeDefaults.Live;ProbeDefaults.Live=true;
        try{
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=800,Height=600};window.Show();window.Left=-20000;window.SetText("");window.saved="";
            testDeferredClose=true;
            window.fileOpenRunning=true;window.documentOperationBusy=true;
            bool ranInsideBatch=false;
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{ranInsideBatch=closingBatch;window.fileOpenRunning=false;}));
            QueueWindowClose(window);ProcessWindowCloses();
            RetestWait(200);
            check("K2 close does not pump the dispatcher while an open cancels",!ranInsideBatch&&window.placementClosed,"inside="+ranInsideBatch+", closed="+window.placementClosed);
        }catch(Exception ex){check("K2 close open cancel exception",false,ex.ToString());}
        finally{testDeferredClose=oldDeferred;if(window!=null&&!window.placementClosed){window.saved=window.text;window.Close();}ProbeDefaults.Live=oldLive;}
    }
    // W2: after an IME composition ends the close must wait one more retry turn. The settle used to
    // be measured from when composing was first seen, so the 25 ms retry itself already looked
    // settled (waited>=25) and the wait never happened. Called directly: no batch, no timer.
    static void VerifyCompositionSettle(Action<string,bool,string> check){
        MainWindow window=null;
        try{
            window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=800,Height=600};window.Show();window.Left=-20000;
            window.ResetCloseDeferral();
            window.source.SimulateImeComposition();
            bool first=window.PrepareCloseDeferral();
            window.source.ResetCompositionSimulation();
            System.Threading.Thread.Sleep(30);
            bool settle=window.PrepareCloseDeferral();
            System.Threading.Thread.Sleep(30);
            bool ready=window.PrepareCloseDeferral();
            check("W2 close waits one retry turn after the composition ends",first&&settle&&!ready,"first="+first+", settle="+settle+", ready="+ready);
        }catch(Exception ex){check("W2 composition settle exception",false,ex.ToString());}
        finally{if(window!=null){window.ResetCloseDeferral();window.saved=window.text;window.Close();}}
    }
    // W1: a close that lands while a save prompt opened outside a close batch (Ctrl+W asks the same
    // way) is up must wait for that prompt. Running the batch underneath it stacked a second prompt
    // on the first, or closed the window whose prompt the user was answering.
    static void VerifyUnrelatedPromptClose(Action<string,bool,string> check){
        string title=null;MainWindow owner=null,other=null;bool oldDeferred=testDeferredClose;
        System.Windows.Threading.DispatcherTimer poll=null;System.Diagnostics.Stopwatch clock=new System.Diagnostics.Stopwatch();
        System.Windows.Window dialog=null;int phase=0;long phaseAt=0;
        bool prompted=false,found=false,queuedOther=false,queuedOwner=false,otherClosedDuring=true,ownerClosedDuring=true;
        int promptsOpen=-1;bool secondSeen=false,secondClosed=false,timedOut=false;
        try{
            owner=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=800,Height=600};owner.Show();owner.Left=-20000;
            other=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=800,Height=600};other.Show();other.Left=-20000;
            owner.SetText("unrelated prompt text");owner.saved="";
            testDeferredClose=true;title=owner.T("변경 내용 저장");
            // The prompt Ctrl+W opens: AskSave outside any close batch. ShowDialog blocks, so run it
            // on the dispatcher and drive everything else from an Input-priority poll (F6 pattern).
            owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{prompted=true;owner.AskSave();}));
            poll=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Input) {Interval=TimeSpan.FromMilliseconds(25)};
            poll.Tick+=delegate{
                if(clock.ElapsedMilliseconds>4000&&phase<4){
                    timedOut=true;System.Windows.Window stuck=saveConfirmationDialog;if(stuck!=null&&stuck.IsVisible)stuck.Close();
                    stuck=FindSaveConfirmDialog(title);if(stuck!=null&&stuck.IsVisible)stuck.Close();
                    poll.Stop();phase=4;return;
                }
                if(phase==0){
                    System.Windows.Window candidate=VisibleSaveDialog(title);
                    if(candidate==null)return;
                    found=true;dialog=candidate;
                    RetestSendMessage(new System.Windows.Interop.WindowInteropHelper(other).Handle,0x112,new IntPtr(0xF060),IntPtr.Zero);
                    RetestSendMessage(new System.Windows.Interop.WindowInteropHelper(owner).Handle,0x112,new IntPtr(0xF060),IntPtr.Zero);
                    queuedOther=closeRequests.Contains(other);queuedOwner=closeRequests.Contains(owner);
                    phase=1;phaseAt=clock.ElapsedMilliseconds;
                    return;
                }
                if(phase==1&&clock.ElapsedMilliseconds>=phaseAt+400){
                    otherClosedDuring=other.placementClosed;ownerClosedDuring=owner.placementClosed;
                    promptsOpen=CountSaveConfirmDialogs(title);
                    if(dialog!=null&&dialog.IsVisible)dialog.Close();
                    phase=2;phaseAt=clock.ElapsedMilliseconds;
                    return;
                }
                if(phase==2){
                    // Once the first prompt is answered the queued close of the owner runs as its
                    // own batch and asks again; this second prompt is the one answered here.
                    System.Windows.Window second=VisibleSaveDialog(title);
                    if(second==null||Object.ReferenceEquals(second,dialog))return;
                    secondSeen=true;second.Close();secondClosed=true;phase=3;phaseAt=clock.ElapsedMilliseconds;
                    return;
                }
                if(phase==3&&clock.ElapsedMilliseconds>=phaseAt+400){phase=4;poll.Stop();}
            };
            clock.Start();poll.Start();
            for(int waited=0;phase<4&&waited<4000;waited+=50)RetestWait(50);
            if(poll!=null)poll.Stop();
            bool otherClosedAfter=other.placementClosed,ownerOpenAfter=owner.IsLoaded&&!owner.placementClosed;
            check("W1 prompt opened outside a close batch",prompted&&found&&!timedOut,"prompted="+prompted+", found="+found+", timeout="+timedOut);
            check("W1 closes stay queued while the prompt is open",queuedOther&&queuedOwner,"other="+queuedOther+", owner="+queuedOwner);
            check("W1 nothing closes or stacks under the open prompt",!otherClosedDuring&&!ownerClosedDuring&&promptsOpen==1,"otherClosed="+otherClosedDuring+", ownerClosed="+ownerClosedDuring+", prompts="+promptsOpen);
            check("W1 queued closes run after the prompt is answered",otherClosedAfter&&secondSeen&&secondClosed&&ownerOpenAfter,"other="+otherClosedAfter+", second="+secondSeen+", ownerOpen="+ownerOpenAfter);
        }catch(Exception ex){check("W1 unrelated prompt exception",false,ex.ToString());}
        finally{
            if(poll!=null)poll.Stop();
            testDeferredClose=oldDeferred;closeRequests.Clear();
            System.Windows.Window stuck=saveConfirmationDialog;if(stuck!=null&&stuck.IsVisible)stuck.Close();
            stuck=FindSaveConfirmDialog(title);if(stuck!=null&&stuck.IsVisible)stuck.Close();
            foreach(MainWindow window in new MainWindow[]{other,owner})if(window!=null&&!window.placementClosed){window.saved=window.text;window.Close();}
        }
    }
    // ---------------------------------------------------------------- b52 #12 / #13
    // #12: while the find bar stays open, the count must follow edits to the document. The old invalidation
    // hook was attached once to the surface the window was constructed with, so an edit that went through
    // another surface never invalidated anything and the count only recovered after closing and reopening
    // the bar. Checked on every surface (basic view setting, live and TXT): add an occurrence, remove one,
    // and the count follows after the typing pause.
    static void VerifySearchLiveUpdate(Action<string, bool, string> check) {
        bool oldLive = ProbeDefaults.Live; MainWindow window = null;
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mdnote-search-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try {
            foreach (string surface in new[] { "basic", "live", "txt" }) {
                ProbeDefaults.Live = surface != "basic";
                string file = System.IO.Path.Combine(dir, "search-" + surface + (surface == "txt" ? ".txt" : ".md"));
                // "테스트" appears once; the body is a blank line apart so a paragraph insert cannot
                // merge with a neighbour.
                System.IO.File.WriteAllText(file, "머리말\n\n테스트 문장입니다.\n\n꼬리말\n", new UTF8Encoding(false));
                try {
                    window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 }; window.Show(); window.Left = -20000;
                    window.LoadFileCore(file); SearchPump(window);
                    window.SetMode(true); SearchPump(window);
                    window.ShowSearch(); window.query.Text = "테스트"; SearchPump(window);
                    // Nothing is selected yet, so the counter reads "0 / n".
                    string opened = window.matchCount.Text;
                    // The user types another occurrence at the end of the document.
                    SearchInsert(window, "테스트");
                    bool added = SearchWaitCount(window, "0 / 2");
                    string afterAdd = window.matchCount.Text;
                    SearchInsert(window, "테스트");
                    bool addedAgain = SearchWaitCount(window, "0 / 3");
                    string afterAdd2 = window.matchCount.Text;
                    // Deleting an occurrence brings the count back down.
                    SearchDeleteLast(window);
                    bool removed = SearchWaitCount(window, "0 / 2");
                    string afterDelete = window.matchCount.Text;
                    check("#12 " + surface + " find count follows document edits",
                        opened == "0 / 1" && added && addedAgain && removed,
                        "open='" + opened + "', +1='" + afterAdd + "', +2='" + afterAdd2 + "', -1='" + afterDelete + "'");
                } catch (Exception ex) { check("#12 " + surface + " search update exception", false, ex.ToString()); }
                finally { if (window != null) { try { if (window.searchHost.Visibility == System.Windows.Visibility.Visible) window.CloseSearch(); window.saved = window.text; window.Close(); } catch (Exception) { } window = null; } try { if (System.IO.File.Exists(file)) System.IO.File.Delete(file); } catch (IOException) { } }
            }
            ProbeDefaults.Live = false;
            VerifySearchCountAcrossTabs(check, dir);
        } finally { ProbeDefaults.Live = oldLive; try { foreach (string left in System.IO.Directory.GetFiles(dir)) System.IO.File.Delete(left); System.IO.Directory.Delete(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    // The reported case: the find bar stays open while the user edits a tab that opened after the window.
    // The old invalidation hook was attached once in BuildSearchBar to the surface the window was built
    // with, so edits reached through a tab's own surface raised TextChanged on something nothing listened
    // to, and the count froze until the bar was closed and reopened.
    static void VerifySearchCountAcrossTabs(Action<string, bool, string> check, string dir) {
        MainWindow window = null;
        string first = System.IO.Path.Combine(dir, "search-tab1.md"), second = System.IO.Path.Combine(dir, "search-tab2.md");
        try {
            System.IO.File.WriteAllText(first, "첫 탭\n\n테스트 하나.\n", new UTF8Encoding(false));
            System.IO.File.WriteAllText(second, "둘째 탭\n\n테스트 문장입니다.\n", new UTF8Encoding(false));
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 }; window.Show(); window.Left = -20000;
            window.LoadFileCore(first); SearchPump(window);
            // A tab that did not exist when the window was built: this is the surface that used to be unhooked.
            window.CreateTab(); SearchPump(window);
            window.LoadFileCore(second); SearchPump(window);
            window.SetMode(true); SearchPump(window);
            int documents = window.documents.Count;
            window.ShowSearch(); window.query.Text = "테스트"; SearchPump(window);
            string opened = window.matchCount.Text;
            SearchInsert(window, "테스트");
            bool added = SearchWaitCount(window, "0 / 2");
            string afterAdd = window.matchCount.Text;
            // Going back to the first tab must count that tab's text, not the one just left.
            var other = window.documents[0];
            window.SwitchTab(other); SearchPump(window);
            bool switched = SearchWaitCount(window, "0 / 1");
            string afterSwitch = window.matchCount.Text;
            check("#12 find count follows edits in a tab opened after the window",
                documents == 2 && opened == "0 / 1" && added && switched,
                "tabs=" + documents + ", open='" + opened + "', +1='" + afterAdd + "', otherTab='" + afterSwitch + "'");
        } catch (Exception ex) { check("#12 tab search update exception", false, ex.ToString()); }
        finally { if (window != null) { try { UserFixWatch.Mark("tab-cleanup close-search"); if (window.searchHost.Visibility == System.Windows.Visibility.Visible) window.CloseSearch(); UserFixWatch.Mark("tab-cleanup mark-saved"); foreach (var state in window.documents) { state.Saved = state.Text; } window.saved = window.text; UserFixWatch.Mark("tab-cleanup close"); window.Close(); UserFixWatch.Mark("tab-cleanup closed"); } catch (Exception) { } } try { if (System.IO.File.Exists(first)) System.IO.File.Delete(first); } catch (IOException) { } try { if (System.IO.File.Exists(second)) System.IO.File.Delete(second); } catch (IOException) { } }
    }
    // Wrap off on the source surface, one long paragraph, matches far to the right of the viewport.
    // Find-next must bring the match inside the viewport on both axes. A line wider than the viewport
    // but below LongLineWindow's 16K fold threshold is the case that had no reveal at all; 12K keeps it
    // there. (The basic rich surface this case also covered is gone; the TXT half is the remaining one.)
    static void VerifySearchRevealHorizontal(Action<string, bool, string> check, string dir) {
        MainWindow window = null;
        try {
            // Two matches, at roughly columns 2,000 and 10,000 of one paragraph (generated text only).
            var narrow = new StringBuilder();
            for (int i = 0; i < 2000; i++) narrow.Append('a');
            narrow.Append("테스트");
            for (int i = 0; i < 8000; i++) narrow.Append('b');
            narrow.Append("테스트");
            for (int i = 0; i < 300; i++) narrow.Append('c');
            string txtFile = System.IO.Path.Combine(dir, "search-wide.txt");
            System.IO.File.WriteAllText(txtFile, narrow.ToString() + "\n", new UTF8Encoding(false));
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 }; window.Show(); window.Left = -20000;
            window.LoadFileCore(txtFile); SearchPump(window); window.SetMode(true); SearchPump(window);
            window.SetWrap(false); SearchPump(window);
            window.ShowSearch(); window.query.Text = "테스트"; SearchPump(window);
            window.source.Select(0, 0); SearchPump(window);
            window.FindNext(false); SearchPump(window);
            int txtFirst = window.source.SelectionStart; bool txtFirstVisible = SearchMatchVisible(window);
            double txtFirstOffset = window.source.HorizontalOffset;
            window.FindNext(false); SearchPump(window);
            int txtSecond = window.source.SelectionStart; bool txtSecondVisible = SearchMatchVisible(window);
            double txtSecondOffset = window.source.HorizontalOffset;
            check("#13 txt wrap-off find reveals the match sideways",
                window.CodeMode && !window.source.WordWrap && txtFirst == 2000 && txtSecond == 10003 && txtFirstVisible && txtSecondVisible
                    && txtFirstOffset > 50 && txtSecondOffset > 50 && Math.Abs(txtSecondOffset - txtFirstOffset) > 1000,
                "code=" + window.CodeMode + ", wrap=" + window.source.WordWrap + ", first=" + txtFirst + "/" + txtFirstVisible + " @" + txtFirstOffset.ToString("0")
                + ", second=" + txtSecond + "/" + txtSecondVisible + " @" + txtSecondOffset.ToString("0") + ", viewport=" + window.source.ViewportWidth.ToString("0"));
            try { System.IO.File.Delete(txtFile); } catch (IOException) { }
        } catch (Exception ex) { check("#13 reveal exception", false, ex.ToString()); }
        finally { if (window != null) { try { if (window.searchHost.Visibility == System.Windows.Visibility.Visible) window.CloseSearch(); window.saved = window.text; window.Close(); } catch (Exception) { } } }
    }
    // The selection's own geometry, in the editor's client space: both ends must sit inside the viewport.
    static bool SearchMatchVisible(MainWindow window) {
        SourceEditor source = window.source;
        source.TextArea.TextView.EnsureVisualLines();
        int start = source.SelectionStart, length = source.SelectionLength;
        if (start < 0 || length <= 0) return false;
        System.Windows.Rect left = source.GetRectFromCharacterIndex(start, false);
        System.Windows.Rect right = source.GetRectFromCharacterIndex(start + length - 1, true);
        if (left.IsEmpty || right.IsEmpty) return false;
        double width = source.ViewportWidth, height = source.ViewportHeight;
        bool horizontal = left.Left >= -1 && right.Left <= width + 1;
        bool vertical = left.Top >= -1 && left.Bottom <= height + 1;
        return horizontal && vertical;
    }
    // Type at the end of the document, the way a user adding an occurrence does.
    static void SearchInsert(MainWindow window, string text) {
        int at = window.source.Document.TextLength;
        window.source.Select(at, 0);
        window.source.TextArea.PerformTextInput(text);
        SearchPump(window);
    }
    static void SearchDeleteLast(MainWindow window) {
        var document = window.source.Document;
        int at = document.Text.LastIndexOf("테스트", StringComparison.Ordinal);
        if (at >= 0) document.Remove(at, 3);
        SearchPump(window);
    }
    // Wait for the debounced rebuild (250 ms) to land: pump frames until the counter reads `expected`.
    static bool SearchWaitCount(MainWindow window, string expected) {
        for (int waited = 0; waited < 4000; waited += 25) {
            window.UpdateLayout();
            LongLineBench.Idle();
            if (window.matchCount.Text == expected) return true;
            System.Threading.Thread.Sleep(25);
        }
        window.UpdateLayout();
        LongLineBench.Idle();
        return window.matchCount.Text == expected;
    }
    static void SearchPump(MainWindow window) { window.UpdateLayout(); LongLineBench.Idle(); window.UpdateLayout(); LongLineBench.Idle(); }
    static void NoWrapPump(MainWindow window) { window.UpdateLayout(); LongLineBench.Idle(); window.source.UpdateLayout(); LongLineBench.Idle(); }
    // NoWrapPump with a bound: the ApplicationIdle callback ends the frame as usual, and a
    // Background-priority timer (above ApplicationIdle) ends it too and reports that the dispatcher
    // never went idle. LongLineBench.Idle() alone waits forever when something above ApplicationIdle
    // keeps posting, which is how the probe used to stop with no report at all.
    static bool NoWrapPumpBounded(MainWindow window) {
        window.UpdateLayout();
        bool first = UserFixPump(window, 3000);
        window.source.UpdateLayout();
        bool second = UserFixPump(window, 3000);
        return first && second;
    }
    static bool UserFixPump(MainWindow window, int milliseconds) {
        bool idled = false;
        var frame = new System.Windows.Threading.DispatcherFrame();
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(delegate { idled = true; frame.Continue = false; }));
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        System.EventHandler tick = delegate { timer.Stop(); if (!idled) frame.Continue = false; };
        timer.Tick += tick;
        timer.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); timer.Tick -= tick; }
        return idled;
    }
    // ------------------------------------------------ stand-in progress bar (Sonnet review 1/2/3/10)
    // The stand-in is skipped in self-test because benches measure this window's own frame timing, so the
    // probe turns FileOpenBarStandInSelfTest on for its own two opens. What it can check here is the
    // handshake that the review named: a "false" value stops the stand-in even when the template was
    // already applied (ApplyTemplate returned false), the start/stop pair survives a rapid restart, the
    // window is click-through/non-activating and is placed before it is shown, and the thread shuts its
    // dispatcher down. A real bar over a moving window was checked by hand on the built exe.
    static void VerifyStandInBar(Action<string, bool, string> check) {
        MainWindow window = null;
        bool old = FileOpenBarStandInSelfTest;
        try {
            FileOpenBarStandInSelfTest = true;
            UserFixWatch.Mark("standin construct"); window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 };
            UserFixWatch.Mark("standin show"); window.Show(); window.Left = -20000; UserFixWatch.Mark("standin pump-before-overlay");
            // The idle pump must end with a failure, not wait forever, when work above ApplicationIdle
            // keeps arriving (the 2026-10-31 stop). A self-posting ContextIdle loop reproduces that.
            bool starve = true; Action repost = null;
            repost = delegate { if (starve) window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, repost); };
            window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, repost);
            bool timedOut = !UserFixPump(window, 700);
            starve = false;
            check("idle pump reports a starved dispatcher instead of waiting", timedOut, "timedOut=" + timedOut);
            if (!NoWrapPumpBounded(window)) { check("stand-in bar window reaches dispatcher idle after Show", false, "applicationIdle did not run within 3000 ms"); return; }
            // The bar is only created when an overlay exists; make sure it is built, visible and templated
            // first (the overlay itself is shown by a 400 ms delay timer, which a probe pump never runs).
            UserFixWatch.Mark("standin ensure-overlay"); window.EnsureFileOpenOverlay(); UserFixWatch.Mark("standin overlay-created");
            // EnsureFileOpenOverlay marks the window as "an open is in progress" (documentOperationBusy).
            // A live session then re-posts its table-caret guard at ContextIdle on every turn while that
            // flag is set (MarkdownLivePreview.QueueCaretGuard), and that stream starves ApplicationIdle,
            // so the probe's Idle() pump waits forever. There is no open to finish here, so leave the
            // busy state through the product's own cancel path before showing the overlay.
            window.CancelFileOpen(); UserFixWatch.Mark("standin overlay-cancelled");
            window.fileOpenOverlay.Visibility = System.Windows.Visibility.Visible; UserFixWatch.Mark("standin overlay-visible");
            window.UpdateLayout(); UserFixWatch.Mark("standin update-layout");
            if (!NoWrapPumpBounded(window)) { check("stand-in bar overlay reaches dispatcher idle", false, "applicationIdle did not run within 3000 ms after the overlay was shown"); return; }
            window.fileOpenProgress.ApplyTemplate(); window.UpdateLayout(); NoWrapPump(window); UserFixWatch.Mark("standin template");
            // A first start from the normal path (indeterminate on -> segment), then the value the review
            // said returned early: "false" with the template already applied.
            window.SetFileOpenIndeterminate(true); NoWrapPump(window);
            // Self-test windows are never the foreground window, so the stand-in hides itself on purpose;
            // the probe asks for the shown state explicitly to exercise the placement.
            window.UpdateFileOpenBarStandForeground(true);
            bool started = WaitForStandInWindow(window);
            window.SetFileOpenIndeterminate(true);   // template already applied -> ApplyTemplate() returns false
            window.SetFileOpenIndeterminate(false);  // must still stop the animation and the stand-in
            bool stopped = WaitForStandInStop(window);
            check("stand-in bar starts and the second, template-applied state change stops it", started && stopped,
                "started=" + started + ", stopped=" + stopped);
            // Click-through, non-activating, no taskbar entry, and placed before Show.
            int style = FileOpenBarStandLastStyle;
            int wanted = 0x20 | 0x80 | 0x08000000;
            check("stand-in bar is click-through, non-activating and out of the taskbar", (style & wanted) == wanted,
                "style=0x" + style.ToString("X") + ", wanted=0x" + wanted.ToString("X"));
            check("stand-in bar is positioned before it is shown", FileOpenBarStandPlacedBeforeShow,
                "placed=" + FileOpenBarStandPlacedBeforeShow + ", shows=" + FileOpenBarStandShowCount);
            // Rapid restart: stop then start again immediately must leave a live stand-in with a window.
            window.SetFileOpenIndeterminate(true); NoWrapPump(window);
            window.SetFileOpenIndeterminate(false); NoWrapPump(window);
            window.SetFileOpenIndeterminate(true); NoWrapPump(window);
            window.UpdateFileOpenBarStandForeground(true);
            bool restarted = WaitForStandInWindow(window);
            window.SetFileOpenIndeterminate(false);
            bool stoppedAgain = WaitForStandInStop(window);
            check("stand-in bar survives a stop/start/stop cycle", restarted && stoppedAgain,
                "restarted=" + restarted + ", stopped=" + stoppedAgain);
            // The thread's dispatcher is shut down, so the field it owns is cleared and no thread is left.
            bool quiet = true;
            for (int waited = 0; waited < 3000 && window.HasFileOpenBarStandThread(); waited += 25) { System.Threading.Thread.Sleep(25); NoWrapPump(window); }
            quiet = !window.HasFileOpenBarStandThread();
            check("stand-in bar thread exits and clears itself", quiet, "threadLeft=" + !quiet);
        } catch (Exception ex) { check("stand-in bar exception", false, ex.ToString()); }
        finally {
            FileOpenBarStandInSelfTest = old;
            if (window != null) { try { window.SetFileOpenIndeterminate(false); window.saved = window.text; window.Close(); } catch (Exception) { } }
        }
    }
    static bool WaitForStandInStop(MainWindow window) {
        for (int waited = 0; waited < 3000; waited += 20) {
            window.UpdateLayout(); LongLineBench.Idle();
            if (!window.HasFileOpenBarStandWindow()) return true;
            System.Threading.Thread.Sleep(20);
        }
        return !window.HasFileOpenBarStandWindow();
    }
    // The window is created on the stand-in thread, so a start returns before it exists: wait for it.
    // A deactivation can arrive between the probe's own call and the stand-in's loop turn (self-test
    // windows are never foreground), and the stand-in then hides on purpose, so the wish is re-asserted
    // each turn - the same thing the real window does while it stays foreground.
    static bool WaitForStandInWindow(MainWindow window) {
        for (int waited = 0; waited < 3000; waited += 20) {
            window.UpdateFileOpenBarStandForeground(true);
            if (window.HasFileOpenBarStandWindow() && FileOpenBarStandShowCount > 0) { window.UpdateLayout(); LongLineBench.Idle(); return true; }
            System.Threading.Thread.Sleep(20);
            window.UpdateLayout(); LongLineBench.Idle();
        }
        return window.HasFileOpenBarStandWindow() && FileOpenBarStandShowCount > 0;
    }
    // ------------------------------------------------ narrow toolbar reachability (Sonnet review 4, b56)
    // Sonnet review 4 fixed the old hover strip: entering the strip left the scrolling row, hid the strip
    // and re-entered the row, a flicker that also lost the click. b56 moved the ribbon commands that no
    // longer fit into the row's "»" menu, so a narrow window no longer overflows sideways and the strip
    // legitimately stays at rest; the probe's old "overflow must be true" premise is gone with that
    // behaviour. The check now drives the contract that replaced it on a real 380 px window (the app
    // minimum): "»" is on screen and a click opens this row's menu, and every ribbon command is either
    // painted fully inside the row or listed in that menu. The hover test still runs when the fallback
    // strip is genuinely needed (the row really scrolls), so that regression stays covered.
    static void VerifyToolbarNarrowOverflow(Action<string, bool, string> check) {
        MainWindow window = null;
        try {
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 380, Height = 700 };
            window.Show(); window.Left = -20000; NoWrapPump(window);
            // SlimHorizontal puts a ScrollViewer (the row) and a ScrollBar (the strip) side by side in a Grid;
            // the ribbon's WrapPanel is the viewer's content, so walk up the visual tree to the viewer and
            // take its Grid parent.
            var scroll = VisualAncestor<System.Windows.Controls.ScrollViewer>(window.formatBar);
            var host = scroll == null ? null : System.Windows.Media.VisualTreeHelper.GetParent(scroll) as System.Windows.Controls.Panel;
            var strip = host == null ? null : FindSlimBar(host);
            if (host == null || scroll == null || strip == null) { check("toolbar strip present", false, "host=" + (host != null) + ", scroll=" + (scroll != null) + ", strip=" + (strip != null)); return; }
            window.UpdateLayout(); NoWrapPump(window);
            // SizeChanged queues the measurement at Background priority; the pump above already ran it.
            if (scroll.ScrollableWidth > 0.5) {
                // The fallback scroll is really needed here, so the old contract still applies.
                MouseEvent(window, host, System.Windows.Input.Mouse.MouseEnterEvent);
                NoWrapPump(window);
                bool shown = strip.Visibility == System.Windows.Visibility.Visible && strip.Opacity >= 0.99;
                bool takesClicks = strip.IsHitTestVisible;
                MouseEvent(window, host, System.Windows.Input.Mouse.MouseLeaveEvent);
                NoWrapPump(window);
                bool hidden = strip.Opacity <= 0.01 && !strip.IsHitTestVisible;
                check("toolbar overflow fallback strip reveals on host hover and hides on leave", shown && takesClicks && hidden,
                    "overflow=True, shown=" + shown + ", clickable=" + takesClicks + ", hidden=" + hidden + ", »=" + window.ToolbarOverflowShown(true));
                return;
            }
            var button = window.ribbonOverflow;
            bool buttonShown = window.ToolbarOverflowShown(true);
            System.Windows.Rect buttonBox = button == null ? System.Windows.Rect.Empty : button.TransformToAncestor(window.ribbonHost).TransformBounds(new System.Windows.Rect(0, 0, button.ActualWidth, button.ActualHeight));
            bool buttonInside = !buttonBox.IsEmpty && buttonBox.Left >= -.5 && buttonBox.Right <= window.ribbonHost.ActualWidth + .5;
            bool clickable = button != null && button.IsVisible && button.IsEnabled && button.IsHitTestVisible && button.ActualWidth > 0 && buttonInside;
            // A click must open this row's menu with the commands that did not fit (not an empty popup).
            bool opens = false;
            string menuDetail = "not clicked";
            if (clickable) {
                button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                NoWrapPump(window);
                var menu = window.ribbonOverflowMenu;
                opens = menu != null && menu.IsOpen && menu.Items.Count > 0;
                menuDetail = menu == null ? "missing" : "open=" + menu.IsOpen + ", items=" + menu.Items.Count;
                if (menu != null) menu.IsOpen = false;
                NoWrapPump(window);
            }
            int visible = 0, listed = 0;
            string lost = null, clipped = null;
            foreach (var source in window.ToolbarOverflowSources(true)) {
                bool painted = source.Visibility == System.Windows.Visibility.Visible && source.ActualWidth > 0 && source.ActualHeight > 0;
                if (painted) {
                    visible++;
                    System.Windows.Rect box = source.TransformToAncestor(host).TransformBounds(new System.Windows.Rect(0, 0, source.ActualWidth, source.ActualHeight));
                    if (clipped == null && (box.Left < -.5 || box.Right > host.ActualWidth + .5))
                        clipped = source.GetType().Name + " " + box.Left.ToString("0.#") + ".." + box.Right.ToString("0.#") + "/" + host.ActualWidth.ToString("0.#");
                } else {
                    if (!window.ToolbarOverflowMenuHas(true, source)) { if (lost == null) lost = source.GetType().Name; }
                    else listed++;
                }
            }
            // With no sideways scroll left, the old overlay must stay invisible and out of the hit test.
            bool stripRest = strip.Opacity <= 0.01 && !strip.IsHitTestVisible;
            check("toolbar overflow at 380 px keeps every ribbon command reachable",
                buttonShown && clickable && opens && visible > 0 && listed > 0 && lost == null && clipped == null && stripRest,
                "overflow=False, »=" + buttonShown + ", box=" + buttonBox.Left.ToString("0.#") + ".." + buttonBox.Right.ToString("0.#") + "/" + window.ribbonHost.ActualWidth.ToString("0.#")
                + ", clickable=" + clickable + ", menu=" + menuDetail + ", visible=" + visible + ", inMenu=" + listed
                + ", lost=" + (lost == null ? "none" : lost) + ", clipped=" + (clipped == null ? "none" : clipped) + ", stripRest=" + stripRest);
        } catch (Exception ex) { check("toolbar overflow exception", false, ex.ToString()); }
        finally { if (window != null) { try { if (window.ribbonOverflowMenu != null) window.ribbonOverflowMenu.IsOpen = false; window.saved = window.text; window.Close(); } catch (Exception) { } } }
    }
    static System.Windows.Controls.ScrollViewer FindScrollViewer(System.Windows.DependencyObject node) {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            var viewer = child as System.Windows.Controls.ScrollViewer; if (viewer != null) return viewer;
            var nested = FindScrollViewer(child); if (nested != null) return nested;
        }
        return null;
    }
    static System.Windows.Controls.Primitives.ScrollBar FindSlimBar(System.Windows.Controls.Panel host) {
        foreach (System.Windows.UIElement child in host.Children) { var bar = child as System.Windows.Controls.Primitives.ScrollBar; if (bar != null) return bar; }
        return null;
    }
    static T VisualAncestor<T>(System.Windows.DependencyObject node) where T : System.Windows.DependencyObject {
        for (System.Windows.DependencyObject at = System.Windows.Media.VisualTreeHelper.GetParent(node); at != null; at = System.Windows.Media.VisualTreeHelper.GetParent(at)) {
            var hit = at as T; if (hit != null) return hit;
        }
        return null;
    }
    static void MouseEvent(MainWindow window, System.Windows.UIElement target, System.Windows.RoutedEvent routed) {
        target.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = routed });
    }
    static string ClipboardText() {
        for (int i = 0; i < 3; i++) { try { return System.Windows.Clipboard.GetText(); } catch (System.Runtime.InteropServices.ExternalException) { System.Threading.Thread.Sleep(30); } }
        return null;
    }
    // ---------------------------------------- feedback 5: live size box shows the drawn heading size
    // The live surface draws a heading line at its own scale, but the size box read the base size, so
    // a caret on "# 긴 문단" showed the paragraph style as "제목1" and the number as the body size. The
    // box must report what is drawn there.
    static void VerifyLiveHeadingSizeBox(Action<string, bool, string> check) {
        bool oldDefault = ProbeDefaults.Live; ProbeDefaults.Live = true; MainWindow window = null;
        try {
            string body = "# 제목 하나\n\n본문 문단입니다.\n\n## 제목 둘\n\n## 두 번째 제목 둘\n\n### 제목 셋\n\n본문 둘.\n";
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 };
            window.Show(); window.Left = -20000;
            window.SetText(body); window.SetMode(true); LongLineBench.Idle();
            if (!window.LiveFormat) { check("5 heading size box runs on the live surface", false, "code=" + window.CodeMode + ", live=" + window.LiveFormat + ", preview=" + window.LivePreviewAttached); return; }
            var document = window.source.Document;
            // H1 and H3, the second H2 line a separate line so one level cannot hide behind another, and
            // one body line as the control.
            int[][] cases = new int[][] { new int[] { 1, 1 }, new int[] { 5, 2 }, new int[] { 7, 2 }, new int[] { 9, 3 }, new int[] { 3, 0 } };
            double baseSize = window.fontSize;
            string detail = "";
            bool all = true;
            foreach (int[] item in cases) {
                int lineNumber = item[0], level = item[1];
                var line = document.GetLineByNumber(lineNumber);
                // Land on real text, past the "#" run and the space after it, so the case cannot be
                // satisfied by a marker character.
                string lineText = document.GetText(line);
                int at = 0;
                while (at < lineText.Length && (lineText[at] == '#' || lineText[at] == ' ')) at++;
                window.source.CaretIndex = line.Offset + Math.Min(at, Math.Max(0, line.Length - 1));
                LongLineBench.Idle();
                window.UpdateTools(); LongLineBench.Idle();
                window.RefreshSizeBox();
                string shown = window.sizes.Text;
                double expected = level > 0 ? baseSize * Markdown.HeadingScale(level) : baseSize;
                bool ok = shown == expected.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " px";
                all &= ok;
                detail += " [line " + lineNumber + " H" + level + "='" + shown + "' want=" + expected.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "]";
            }
            check("5 live size box shows the heading's drawn size", all, detail);
        } catch (Exception ex) { check("5 heading size box exception", false, ex.ToString()); }
        finally { if (window != null) { try { window.saved = window.text; window.Close(); } catch (Exception) { } } ProbeDefaults.Live = oldDefault; }
    }
    // ---------------------------------------- image files dropped from Explorer (2026-10-29)
    // One path rule for the image button and both views, and an all-image drop on a Markdown body
    // inserts at the drop point as one undo step, several images one per line in file order.
    static void VerifyImageDrop(string outDir, Action<string, bool, string> check) {
        string dir = Path.Combine(outDir, "imagedrop");
        Directory.CreateDirectory(dir);
        string docPath = Path.Combine(dir, "drop.md");
        string beside = Path.Combine(dir, "beside.png");
        string spaced = Path.Combine(dir, "two words (1).png");
        string subDir = Path.Combine(dir, "sub");
        Directory.CreateDirectory(subDir);
        string inside = Path.Combine(subDir, "inside.png");
        // A sibling of the document folder, so the reference has to climb with "../".
        string otherDir = Path.Combine(outDir, "imagedrop-other");
        Directory.CreateDirectory(otherDir);
        string up = Path.Combine(otherDir, "up.png");
        string txt = Path.Combine(dir, "notes.txt");
        WriteDropImage(beside); WriteDropImage(spaced); WriteDropImage(inside); WriteDropImage(up);
        const string original = "first\nsecond\nthird\n";
        File.WriteAllText(docPath, original, new UTF8Encoding(false));
        File.WriteAllText(txt, "txt body\n", new UTF8Encoding(false));
        bool oldDefault = ProbeDefaults.Live;
        MainWindow window = null;
        try {
            // (1) The path rule: saved document -> relative with '/' on the same drive, otherwise
            // absolute; spaces and parentheses stay percent-encoded for both renderers.
            string same = MarkdownCommands.ImageReference(docPath, beside);
            string sub = MarkdownCommands.ImageReference(docPath, inside);
            string parent = MarkdownCommands.ImageReference(docPath, up);
            string unsaved = MarkdownCommands.ImageReference("", beside);
            string otherDrive = MarkdownCommands.ImageReference(docPath, "D:\\pics\\tail.png");
            string escaped = MarkdownCommands.ImageReference(docPath, spaced);
            string fullBeside = Path.GetFullPath(beside).Replace('\\', '/').Replace("%", "%25").Replace("(", "%28").Replace(")", "%29").Replace(" ", "%20");
            check("drop path same folder", same == "beside.png", "ref='" + same + "'");
            check("drop path sub folder", sub == "sub/inside.png", "ref='" + sub + "'");
            check("drop path parent folder", parent == "../imagedrop-other/up.png", "ref='" + parent + "'");
            check("drop path unsaved is absolute", unsaved == fullBeside, "ref='" + unsaved + "', want='" + fullBeside + "'");
            check("drop path other drive is absolute", otherDrive == "D:/pics/tail.png", "ref='" + otherDrive + "'");
            check("drop path spaces and parentheses", escaped == "two%20words%20%281%29.png", "ref='" + escaped + "'");
            // The basic renderer decodes the shared form to the file on disk.
            bool sameLoad = DropImageParses(MarkdownCommands.ImageMarkdown(same, "beside"), docPath);
            bool parentLoad = DropImageParses(MarkdownCommands.ImageMarkdown(parent, "up"), docPath);
            bool spaceLoad = DropImageParses(MarkdownCommands.ImageMarkdown(escaped, "two words (1)"), docPath);
            bool absoluteLoad = DropImageParses(MarkdownCommands.ImageMarkdown(unsaved, "beside"), "");
            check("drop path loads in the basic renderer", sameLoad && parentLoad && spaceLoad && absoluteLoad,
                "same=" + sameLoad + ", parent=" + parentLoad + ", spaces=" + spaceLoad + ", absolute=" + absoluteLoad);

            // (3) Live view: the same drop lands on the same line, renders, and undoes in one step.
            ProbeDefaults.Live = true;
            window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 };
            window.Show(); window.Left = -20000;
            window.LoadFileCore(docPath); LongLineBench.Idle();
            window.SetMode(true); LongLineBench.Idle();
            check("drop live surface active", window.LiveFormat && window.livePreview != null, "live=" + window.LiveFormat);
            System.Windows.Point livePoint = DropPointInSource(window.source, 3);
            bool liveAccepted = window.AcceptSourceImageDrop(new string[] { spaced, up }, livePoint);
            window.FlushDocumentChanges(); LongLineBench.Idle();
            string[] liveLines = window.text.Split('\n');
            check("drop live inserts at the drop line",
                liveAccepted && liveLines.Length >= 4 && liveLines[2] == "![two words (1)](two%20words%20%281%29.png)" && liveLines[3].StartsWith("![up](../imagedrop-other/up.png)", StringComparison.Ordinal),
                "accepted=" + liveAccepted + ", text='" + window.text.Replace("\n", "\\n") + "'");
            MarkdownLivePreview.Replacement? liveImage = null;
            var info = window.livePreview.Info(window.source.Document.GetLineByNumber(3));
            if (info != null) foreach (MarkdownLivePreview.Replacement replacement in info.Replacements) if (replacement.Kind == MarkdownLivePreview.ReplacementKind.Image) liveImage = replacement;
            check("drop live line is an image for the renderer", liveImage.HasValue && liveImage.Value.Source == "two%20words%20%281%29.png",
                "found=" + liveImage.HasValue + ", source='" + (liveImage.HasValue ? liveImage.Value.Source : "-") + "'");
            System.Windows.Controls.Image decoded = null;
            for (int wait = 0; wait < 100 && decoded == null && liveImage.HasValue; wait++) {
                decoded = window.livePreview.ImageObject(liveImage.Value) as System.Windows.Controls.Image;
                if (decoded == null) { LongLineBench.Idle(); System.Threading.Thread.Sleep(20); }
            }
            check("drop live image decodes from the shared reference", decoded != null && decoded.Source != null, "decoded=" + (decoded != null));
            window.Undo(); LongLineBench.Idle(); window.FlushDocumentChanges(); LongLineBench.Idle();
            check("drop live is one undo step", window.text == original, "text='" + window.text.Replace("\n", "\\n") + "'");
            // The live image button writes the same reference as the drop.
            window.InsertLiveImageFile(spaced); window.FlushDocumentChanges(); LongLineBench.Idle();
            check("drop live image button uses the shared reference",
                window.text.Contains("![two words (1)](two%20words%20%281%29.png)") && !window.text.Contains("file:"),
                "text='" + window.text.Replace("\n", "\\n") + "'");
            window.Undo(); LongLineBench.Idle(); window.FlushDocumentChanges(); LongLineBench.Idle();
            check("drop live image button undo", window.text == original, "text='" + window.text.Replace("\n", "\\n") + "'");

            // (4) A TXT document cannot hold an image: warn and change nothing.
            int notices = 0; string notice = null;
            MainWindow.ImageDropNotice = delegate(string message) { notices++; notice = message; };
            window.LoadFileCore(txt); LongLineBench.Idle(); window.SetMode(true); LongLineBench.Idle();
            bool txtAccepted = window.AcceptSourceImageDrop(new string[] { beside }, DropPointInSource(window.source, 1));
            window.FlushDocumentChanges(); LongLineBench.Idle();
            check("drop txt shows the notice and inserts nothing",
                txtAccepted && notices == 1 && notice == "이미지는 MD 문서에서만 넣을 수 있습니다." && window.text == "txt body\n",
                "accepted=" + txtAccepted + ", notices=" + notices + ", notice='" + notice + "', text='" + window.text.Replace("\n", "\\n") + "'");
            bool txtWindowAccepted = window.AcceptImageDrop(new string[] { beside }, null);
            check("drop txt answers at the window level too", txtWindowAccepted && notices == 2 && window.text == "txt body\n",
                "accepted=" + txtWindowAccepted + ", notices=" + notices + ", text='" + window.text.Replace("\n", "\\n") + "'");

            // (5) Any non-image file in the drop keeps today's open-as-document answer for all.
            bool mixedLive = window.AcceptSourceImageDrop(new string[] { beside, txt }, DropPointInSource(window.source, 1));
            bool mixedWindow = window.AcceptImageDrop(new string[] { beside, txt }, null);
            check("drop mixed stays open-as-document", !mixedLive && !mixedWindow && notices == 2 && window.text == "txt body\n",
                "live=" + mixedLive + ", window=" + mixedWindow + ", notices=" + notices);
            // (6) The handlers never throw on odd input.
            bool threw = false;
            try {
                window.AcceptImageDrop(null, null);
                window.AcceptImageDrop(new string[0], null);
                window.AcceptImageDrop(new string[] { null }, null);
                window.AcceptSourceImageDrop(null, new System.Windows.Point(1, 1));
                window.AcceptSourceImageDrop(new string[0], new System.Windows.Point(1, 1));
            } catch (Exception) { threw = true; }
            check("drop handlers never throw", !threw, "threw=" + threw);
            MainWindow.ImageDropNotice = null;
        } catch (Exception ex) {
            check("image drop exception", false, ex.ToString());
        } finally {
            MainWindow.ImageDropNotice = null;
            if (window != null) { try { window.saved = window.text; window.Close(); } catch (Exception) { } }
            ProbeDefaults.Live = oldDefault;
        }
    }
    // A small real PNG, decoded by the WPF image pipeline like any dropped file.
    static void WriteDropImage(string file) {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(8, 8, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawRectangle(System.Windows.Media.Brushes.SteelBlue, null, new System.Windows.Rect(0, 0, 8, 8));
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = File.Create(file)) encoder.Save(stream);
    }
    // True when the basic parser turns the generated line into an image element, i.e. the
    // reference decodes to a file it can open.
    static bool DropImageParses(string raw, string documentPath) {
        var document = Markdown.Parse(raw, 16, "Malgun Gothic", System.Windows.Media.Brushes.Black, false, documentPath);
        var paragraph = document.Blocks.FirstBlock as System.Windows.Documents.Paragraph;
        return paragraph != null && paragraph.Inlines.FirstInline is System.Windows.Documents.InlineUIContainer;
    }
    // A point on the middle of the given source line, converted to the viewport coordinates the
    // drop event carries (the text view expects document coordinates).
    static System.Windows.Point DropPointInSource(SourceEditor source, int line) {
        var view = source.TextArea.TextView;
        source.ScrollToLine(line); LongLineBench.Idle(); view.EnsureVisualLines();
        System.Windows.Point document = view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(line, 1), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle);
        return new System.Windows.Point(document.X - view.ScrollOffset.X, document.Y - view.ScrollOffset.Y);
    }
    // 2026-10-31 regressions: 버리기 on the edit-exit prompt must restore the buffer itself (not
    // only the dirty mirror), and the raw toggle / edit-mode switch must keep a caret line that
    // was on screen on screen (window-abuse items 20-22, mode-switch S1/S2). Both view modes
    // (basic = hideMarkers on, live = off) and TXT; saved and never-saved documents.
    static void VerifyEditExitAndViewSwitch(Action<string, bool, string> check) {
        bool oldHide = hideMarkers;
        MainWindow window = null;
        try {
            foreach (bool live in new bool[] { false, true }) {
                string view = live ? "live" : "basic";
                foreach (bool plain in new bool[] { false, true }) {
                    string kind = plain ? "txt" : "md";
                    string baseline = plain ? "keep this line\n" : "Keep this heading\n\nbody text\n";
                    // (1) Saved document: discard clears the dirty mark and puts the typed text back
                    // out of the source buffer, the mirror and the live view.
                    window = UserFixOpenViewWindow(live, plain);
                    window.SetText(baseline); window.saved = window.text; window.SetMode(true); LongLineBench.Idle();
                    window.source.CaretIndex = window.source.Document.TextLength;
                    window.source.TextArea.PerformTextInput("XYZ"); LongLineBench.Idle();
                    bool dirty = window.HasUnsavedChanges;
                    bool marked = window.Title.StartsWith("*", StringComparison.Ordinal) && UserFixTabMarked(window);
                    window.queuedSaveChoices.Enqueue(SaveChoice.Discard);
                    window.Toggle(); LongLineBench.Idle(); window.FlushDocumentChanges(); LongLineBench.Idle();
                    string buffer = window.source.Document.Text;
                    bool restored = buffer == baseline && window.text == baseline && !window.HasUnsavedChanges && !window.editing;
                    bool marks = !window.Title.StartsWith("*", StringComparison.Ordinal) && !UserFixTabMarked(window);
                    bool history = !window.History.CanUndo && !window.History.CanRedo;
                    bool caret = window.source.CaretIndex >= 0 && window.source.CaretIndex <= buffer.Length;
                    check("edit-exit discard " + view + " " + kind + " saved", dirty && marked && restored && marks && history && caret,
                        "dirty=" + dirty + ", marked=" + marked + ", editing=" + window.editing + ", unsaved=" + window.HasUnsavedChanges +
                        ", buffer='" + buffer.Replace("\n", "\\n") + "', title='" + window.Title + "', undo=" + window.History.CanUndo + ", caret=" + window.source.CaretIndex);
                    if (!plain) check("edit-exit discard " + view + " live view", window.LivePreviewAttached, "attached=" + window.LivePreviewAttached);
                    window.saved = window.text; window.Close(); window = null;

                    // (2) Never-saved document: discard returns to the empty initial text.
                    window = UserFixOpenViewWindow(live, plain);
                    window.SetText(""); window.saved = ""; window.SetMode(true); LongLineBench.Idle();
                    window.source.CaretIndex = 0; window.source.TextArea.PerformTextInput("XYZ"); LongLineBench.Idle();
                    bool newDirty = window.HasUnsavedChanges;
                    window.queuedSaveChoices.Enqueue(SaveChoice.Discard);
                    window.Toggle(); LongLineBench.Idle(); window.FlushDocumentChanges(); LongLineBench.Idle();
                    string newBuffer = window.source.Document.Text;
                    check("edit-exit discard " + view + " " + kind + " new",
                        newDirty && !window.HasUnsavedChanges && !window.editing && newBuffer.Length == 0 && window.text.Length == 0 &&
                        !window.Title.StartsWith("*", StringComparison.Ordinal) && !UserFixTabMarked(window) && !window.History.CanUndo,
                        "dirty=" + newDirty + ", editing=" + window.editing + ", buffer='" + newBuffer.Replace("\n", "\\n") + "', title='" + window.Title + "'");
                    window.saved = window.text; window.Close(); window = null;
                }
                // (3) Cancel keeps the edits and stays in edit mode.
                window = UserFixOpenViewWindow(live, false);
                window.SetText("cancel baseline\n"); window.saved = window.text; window.SetMode(true); LongLineBench.Idle();
                window.source.CaretIndex = window.source.Document.TextLength;
                window.source.TextArea.PerformTextInput("XYZ"); LongLineBench.Idle();
                window.queuedSaveChoices.Enqueue(SaveChoice.Cancel);
                window.Toggle(); LongLineBench.Idle(); window.FlushDocumentChanges(); LongLineBench.Idle();
                check("edit-exit cancel " + view + " keeps the edits",
                    window.editing && window.HasUnsavedChanges && window.source.Document.Text == "cancel baseline\nXYZ",
                    "editing=" + window.editing + ", unsaved=" + window.HasUnsavedChanges + ", buffer='" + window.source.Document.Text.Replace("\n", "\\n") + "'");
                window.saved = window.text; window.Close(); window = null;

                // (4) The raw toggle and the edit-mode switch keep a visible caret line visible.
                string tall = UserFixTallDocument();
                window = UserFixOpenViewWindow(live, false);
                window.SetText(tall); window.saved = window.text; window.SetMode(true); LongLineBench.Idle();
                window.source.ScrollToEnd(); LongLineBench.Idle(); window.UpdateLayout(); LongLineBench.Idle();
                // At the scroll limit the very last line can sit below the viewport edge; pick the
                // lowest line that is really on screen so the baseline is a caret the user could see.
                bool before = false; string beforeDetail = "";
                int lowest = window.source.Document.LineCount;
                for (int lineNumber = lowest; lineNumber > 1 && lineNumber > lowest - 60 && !before; lineNumber--) {
                    window.source.CaretIndex = window.source.Document.GetLineByNumber(lineNumber).Offset;
                    window.UpdateLayout(); LongLineBench.Idle();
                    before = UserFixCaretLineVisible(window, out beforeDetail);
                }
                string rawDetail, backDetail, readDetail, editDetail;
                window.preview.IsChecked = true; LongLineBench.Idle();
                bool raw = UserFixCaretLineVisible(window, out rawDetail);
                window.preview.IsChecked = false; LongLineBench.Idle();
                bool back = UserFixCaretLineVisible(window, out backDetail);
                check("raw view keeps the caret line " + view, before && raw && back,
                    "before=" + before + " [" + beforeDetail + "], raw=" + raw + " [" + rawDetail + "], back=" + back + " [" + backDetail + "]");
                window.SetMode(false); LongLineBench.Idle();
                bool read = UserFixCaretLineVisible(window, out readDetail);
                window.SetMode(true); LongLineBench.Idle();
                bool edit = UserFixCaretLineVisible(window, out editDetail);
                check("edit mode keeps the caret line " + view, before && read && edit,
                    "before=" + before + " [" + beforeDetail + "], read=" + read + " [" + readDetail + "], edit=" + edit + " [" + editDetail + "]");
                window.saved = window.text; window.Close(); window = null;
            }
        } catch (Exception ex) {
            check("edit-exit/view-switch exception", false, ex.ToString());
        } finally {
            hideMarkers = oldHide;
            if (window != null) { try { window.saved = window.text; window.Close(); } catch (Exception) { } }
        }
    }
    static MainWindow UserFixOpenViewWindow(bool live, bool plain) {
        hideMarkers = !live;
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false, Width = 900, Height = 700 };
        window.Show(); window.Left = -20000; LongLineBench.Idle();
        window.textMode = plain ? true : (bool?)null;
        return window;
    }
    // A tab caption carries the dirty mark as a leading bullet (RefreshTabCaption).
    static bool UserFixTabMarked(MainWindow window) {
        if (window.activeDocument == null || window.activeDocument.Item == null) return false;
        var header = window.activeDocument.Item.Header as System.Windows.Controls.StackPanel;
        if (header == null || header.Children.Count == 0) return false;
        var label = header.Children[0] as System.Windows.Controls.TextBlock;
        return label != null && label.Text.StartsWith("•", StringComparison.Ordinal);
    }
    // The same visibility rule the mode-switch probe applies: the whole caret line's pixel span in
    // the source viewport, with the visual position as the fallback when every glyph is folded.
    static bool UserFixCaretLineVisible(MainWindow window, out string detail) {
        detail = "";
        SourceEditor source = window.source;
        if (source == null || source.Document == null) { detail = "no source"; return false; }
        var view = source.TextArea.TextView;
        try { view.EnsureVisualLines(); } catch (Exception) { detail = "visual lines invalid"; return false; }
        int caret = Math.Max(0, Math.Min(source.CaretOffset, source.Document.TextLength));
        DocumentLine line = source.Document.GetLineByOffset(caret);
        System.Windows.Rect union = System.Windows.Rect.Empty;
        int from = line.Offset, to = line.EndOffset;
        if (to - from > 800) { from = Math.Max(from, caret - 60); to = Math.Min(to, caret + 60); }
        for (int i = from; i <= to; i++) {
            System.Windows.Rect rect = source.GetRectFromCharacterIndex(i);
            if (!rect.IsEmpty) union = union.IsEmpty ? rect : System.Windows.Rect.Union(union, rect);
        }
        double top, bottom;
        if (!union.IsEmpty) { top = union.Top; bottom = union.Bottom; }
        else {
            double y = view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(line.LineNumber, 1), ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop).Y - source.VerticalOffset;
            top = y; bottom = y + Math.Max(1, source.FontSize * 1.3);
        }
        detail = "line " + line.LineNumber + ", top=" + top.ToString("0.#") + ", bottom=" + bottom.ToString("0.#") +
            ", viewport=" + source.ViewportHeight.ToString("0.#") + ", offset=" + source.VerticalOffset.ToString("0.#");
        return bottom > 0 && top < source.ViewportHeight;
    }
    // Headings and paragraphs make the raw and live layouts differ enough that the caret line
    // really leaves the viewport when the offset alone is kept.
    static string UserFixTallDocument() {
        var text = new StringBuilder();
        for (int i = 1; i <= 60; i++) {
            text.Append("## Section ").Append(i).Append('\n').Append('\n');
            text.Append("Body line ").Append(i).Append(" with enough words to form a paragraph line in the source view.\n").Append('\n');
        }
        return text.ToString();
    }
    // 2026-10-31: the probe stopped with no PASS/FAIL file because it waited, unbounded, for the
    // dispatcher to reach ApplicationIdle. LongLineBench.Idle() is such a wait, and a window that
    // keeps work queued above ApplicationIdle (the live preview re-posts its table-caret guard at
    // ContextIdle on every turn while documentOperationBusy is set) starves it forever. The watch
    // below turns any stop that long into a FAIL line, so a hang is reported instead of a silent cut.
    static class UserFixWatch {
        const int StuckMilliseconds = 300000;
        static StringBuilder report; static string outDir; static volatile string last = "start";
        static long at; static volatile bool stopped;
        internal static void Start(string dir, StringBuilder target) {
            report = target; outDir = dir; at = System.Diagnostics.Stopwatch.GetTimestamp();
            var watch = new System.Threading.Thread(Loop);
            watch.IsBackground = true;
            watch.Name = "userfix-watch";
            watch.Start();
        }
        internal static void Stop() { stopped = true; }
        internal static void Mark(string text) {
            last = text;
            System.Threading.Interlocked.Exchange(ref at, System.Diagnostics.Stopwatch.GetTimestamp());
        }
        static void Loop() {
            while (!stopped) {
                System.Threading.Thread.Sleep(1000);
                if (stopped) return;
                long when = System.Threading.Interlocked.Read(ref at);
                long elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - when) * 1000 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsed < StuckMilliseconds) continue;
                try {
                    report.Append("FAIL watchdog: no step finished for ").Append(StuckMilliseconds / 1000).Append(" s, last step '").Append(last).Append("'\n\nResult: FAIL\n");
                    File.WriteAllText(Path.Combine(outDir, "userfix-probe.txt"), report.ToString(), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(outDir, "userfix-probe-FAIL.txt"), "FAIL\n");
                } catch (IOException) { } catch (UnauthorizedAccessException) { }
                Environment.Exit(1);
            }
        }
    }
}
}
