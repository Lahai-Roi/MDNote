// UI fix probe: help-document tab bar and find-box IME composition highlight (offscreen, no SendInput).
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MDNote;

// --uifix-probe <outDir>
// a) The help document is opened through the same call the Help menu makes (OpenAppUsage). It must
//    never land in a tab strip the user cannot see: either a new window, or a strip that is Visible,
//    laid out (>= 24 px) and holding one item per document. Checked with "새 탭으로 보기" on and off,
//    and with a second document forced into a window whose option is off.
// b) The find field during a Korean IME composition. The composing syllable is not in TextBox.Text
//    until it is committed, so the count and the highlight have to read it before the commit, and
//    the commit itself must not count the syllable twice.
internal static class UiFixProbe {
    const BindingFlags Members=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
    internal static int Run(string outDir){
        string dir;
        try{dir=Path.GetFullPath(outDir);}catch(Exception){dir=outDir;}
        Directory.CreateDirectory(dir);
        string passPath=Path.Combine(dir,"uifix-PASS.txt"),failPath=Path.Combine(dir,"uifix-FAIL.txt");
        RemoveFile(passPath);RemoveFile(failPath);
        progressPath=Path.Combine(dir,"uifix-progress.txt");try{if(File.Exists(progressPath))File.Delete(progressPath);}catch(Exception){}
        Progress("start");
        var report=new StringBuilder();
        report.Append("# UiFixProbe\n\n- Timestamp: ").Append(DateTime.Now.ToString("o",System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        bool all=true;
        if(Application.Current==null)new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        /* The app always runs its async UI work inside a dispatcher operation; this probe is called
           straight from Main, so without a dispatcher context an await (the large outline's
           background scan) would resume on a pool thread and fail silently (RC9Tests, FindAbuseProbe
           and LargeDocAbuseProbe install the same context). */
        var previousContext=System.Threading.SynchronizationContext.Current;
        System.Threading.SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
        try{
            string markdown=Path.Combine(dir,"uifix-document.md");
            File.WriteAllText(markdown,"# 첫 문서\n\n본문 내용입니다.\n",new UTF8Encoding(false));
            string plain=Path.Combine(dir,"uifix-find.txt");
            File.WriteAllText(plain,"목록 하나\n목 둘\n",new UTF8Encoding(false));
            /* The Help menu opens the documents through MainWindow.HelpFile: the help\ folder next to
               the exe first, the exe folder only for a copy shipped without it. Looking in the exe
               folder alone failed a0 on every build whose documents sit in help\. */
            string help=HelpDocument("MDNote 사용법.md");
            bool helpExists=File.Exists(help);
            Check(ref all,report,"a0 help document resolved the way the Help menu does",helpExists,help);

            if(helpExists){
                // a1: the option is on, so the help document has to become a second tab in this window.
                Progress("a1-window");
                MainWindow tabsOn=NewWindow();
                Progress("a1-window-made");
                Call(tabsOn,"SetOpenInTabs",true);
                tabsOn.LoadFile(markdown);
                Pump(800);
                Progress("a1-loaded");
                Check(ref all,report,"a1 one document before the help open",tabsOn.OpenDocumentCount==1,"documents="+tabsOn.OpenDocumentCount);
                Call(tabsOn,"OpenAppUsage");
                Pump(1600);
                Progress("a1-opened");
                int count=tabsOn.OpenDocumentCount;
                Check(ref all,report,"a1 help opened as a second tab in the same window",count>=2&&tabsOn.ContainsFile(help),"documents="+count+" helpInWindow="+tabsOn.ContainsFile(help));
                CheckStrip(ref all,report,"a1",tabsOn);

                // a2: the option is off, so the help document belongs to its own window.
                Progress("a2-window");
                MainWindow tabsOff=NewWindow();
                Call(tabsOff,"SetOpenInTabs",false);
                tabsOff.LoadFile(markdown);
                Pump(800);
                Progress("a2-loaded");
                Check(ref all,report,"a2 one document with the option off",tabsOff.OpenDocumentCount==1&&TabControlOf(tabsOff).Visibility==Visibility.Collapsed,"documents="+tabsOff.OpenDocumentCount+" visibility="+TabControlOf(tabsOff).Visibility);
                Call(tabsOff,"OpenAppUsage");
                Pump(1800);
                MainWindow helpWindow=null;
                foreach(Window candidate in Application.Current.Windows){
                    var editor=candidate as MainWindow;
                    if(editor!=null&&!Object.ReferenceEquals(editor,tabsOff)&&editor.ContainsFile(help))helpWindow=editor;
                }
                Check(ref all,report,"a2 option off opens the help in its own window",helpWindow!=null&&tabsOff.OpenDocumentCount==1,"helpWindow="+(helpWindow!=null)+" documentsInFirst="+tabsOff.OpenDocumentCount);
                CheckStrip(ref all,report,"a2 first",tabsOff);
                if(helpWindow!=null)CheckStrip(ref all,report,"a2 help",helpWindow);

                // a3: the invariant behind the async open - a second document in an option-off window shows the strip.
                Call(tabsOff,"CreateTab");
                Pump(400);
                Check(ref all,report,"a3 second document in an option-off window",tabsOff.OpenDocumentCount==2,"documents="+tabsOff.OpenDocumentCount);
                Check(ref all,report,"a3 strip is visible with the option off",TabControlOf(tabsOff).Visibility==Visibility.Visible,"visibility="+TabControlOf(tabsOff).Visibility);
                CheckStrip(ref all,report,"a3",tabsOff);

                // a4: closing back to one document while the option is off puts the strip away again.
                var documents=(IList)FieldOf(tabsOff,"documents");
                object last=documents[documents.Count-1];
                Call(tabsOff,"CloseTab",last);
                Pump(500);
                Check(ref all,report,"a4 one document after closing the second tab",tabsOff.OpenDocumentCount==1,"documents="+tabsOff.OpenDocumentCount);
                Check(ref all,report,"a4 strip collapses for a single option-off document",TabControlOf(tabsOff).Visibility==Visibility.Collapsed,"visibility="+TabControlOf(tabsOff).Visibility);
            }
            Progress("a-done");

            // b: the find field during a Korean composition.
            MainWindow findWindow=NewWindow();
            findWindow.LoadFile(plain);
            Pump(800);
            Call(findWindow,"ShowSearch");
            Pump(500);
            TextBox box=(TextBox)FieldOf(findWindow,"query");
            Check(ref all,report,"b0 find field exists",box!=null,"box="+(box!=null));
            if(box!=null){
                box.Text="목";
                box.CaretIndex=box.Text.Length;
                Pump(600);
                string before=CountOf(findWindow);
                Check(ref all,report,"b1 count for the committed first syllable",Total(before)=="2","count="+before+" text="+box.Text);
                SearchHighlight typingHighlight=FieldValue<SearchHighlight>(findWindow,"sourceHighlight");
                int typingExtras=typingHighlight==null?-1:typingHighlight.ExtraCount;
                int typingRects=typingHighlight==null?-1:typingHighlight.GeometryCount;
                int typingStart=typingHighlight==null?-2:typingHighlight.SourceStart;
                Check(ref all,report,"b1 every visible match is highlighted while typing",typingExtras==2&&typingRects>=2&&typingStart<0,"extras="+typingExtras+" rectangles="+typingRects+" currentStart="+typingStart);
                Progress("b1-checked");
                Raise(box,TextCompositionManager.PreviewTextInputStartEvent,"록");
                Pump(700);
                Check(ref all,report,"b2 composing syllable is not in the field text",box.Text=="목","text="+box.Text);
                string composingQuery=(string)Call(findWindow,"QueryText");
                Check(ref all,report,"b2 query text holds the composing syllable",String.Equals(composingQuery,"목록"),"query="+composingQuery);
                string composing=CountOf(findWindow);
                Check(ref all,report,"b2 count follows the composing text before commit",Total(composing)=="1","count="+composing);
                Raise(box,TextCompositionManager.PreviewTextInputUpdateEvent,"록");
                Pump(700);
                string updated=CountOf(findWindow);
                Check(ref all,report,"b2 update keeps the count on the composed word",Total(updated)=="1"&&box.Text=="목","count="+updated+" text="+box.Text);
                SearchHighlight composingHighlight=FieldValue<SearchHighlight>(findWindow,"sourceHighlight");
                int composingExtras=composingHighlight==null?-1:composingHighlight.ExtraCount;
                int composingRects=composingHighlight==null?-1:composingHighlight.GeometryCount;
                int composingStart=composingHighlight==null?-2:composingHighlight.SourceStart;
                Check(ref all,report,"b2 composing matches are highlighted before commit",composingExtras==1&&composingRects>=1&&composingStart<0,"extras="+composingExtras+" rectangles="+composingRects+" currentStart="+composingStart);
                Call(findWindow,"Find");
                Pump(600);
                string found=CountOf(findWindow);
                SearchHighlight highlight=FieldValue<SearchHighlight>(findWindow,"sourceHighlight");
                if(highlight==null)highlight=FieldValue<SearchHighlight>(findWindow,"bodyHighlight");
                int length=highlight==null?-1:highlight.SourceLength;
                int rectangles=highlight==null?-1:highlight.GeometryCount;
                Check(ref all,report,"b3 find selects the composed match",found.StartsWith("1 / ",StringComparison.Ordinal)&&Total(found)=="1","count="+found);
                Check(ref all,report,"b3 highlight covers the composed match",length==2&&rectangles>=1,"sourceLength="+length+" rectangles="+rectangles);
                box.Text="목록";
                Pump(600);
                string committed=(string)Call(findWindow,"QueryText");
                Check(ref all,report,"b4 commit keeps one copy of the word",String.Equals(committed,"목록"),"query="+committed);
                Check(ref all,report,"b4 committed count is unchanged",Total(CountOf(findWindow))=="1","count="+CountOf(findWindow));
                FieldSet(findWindow,"searchComposing","록");FieldSet(findWindow,"searchComposingStart",1);
                string guarded=(string)Call(findWindow,"QueryText");
                FieldSet(findWindow,"searchComposing","");FieldSet(findWindow,"searchComposingStart",-1);
                Check(ref all,report,"b5 committed characters are not added twice",String.Equals(guarded,"목록"),"query="+guarded);
                box.Text="목";box.CaretIndex=box.Text.Length;
                Pump(500);
                FieldSet(findWindow,"searchComposing","록");FieldSet(findWindow,"searchComposingStart",1);
                string inserted=(string)Call(findWindow,"QueryText");
                FieldSet(findWindow,"searchComposing","");FieldSet(findWindow,"searchComposingStart",-1);
                Check(ref all,report,"b5 composing syllable is inserted at its start",String.Equals(inserted,"목록"),"query="+inserted);
                VerifyCompositionCommands(findWindow,box,ref all,report);
            }
            Progress("b-done");
            VerifyFindReplaceRegressions(ref all,report);
            Progress("f1-find-replace-done");
            VerifySaveAndReplaceFixes(dir,ref all,report);
            Progress("s4-save-replace-done");
            VerifyFindBarLayoutAndEscape(ref all,report);
            Progress("bh-find-bar-done");
            VerifyInlineCodeButton(ref all,report);
            Progress("c-code-button-done");
            VerifyUndoRedoToolbar(ref all,report);
            Progress("d-undo-toolbar-done");
            VerifyFunctionKeys(ref all,report);
            Progress("c-fkeys-done");
            VerifyKeyboardCompositionCommit(ref all,report);
            Progress("kbd-ime-done");
            VerifyStatusEncodingName(ref all,report);
            Progress("encoding-name-done");
            VerifyCloseSpaceAndTableCommands(ref all,report);
            Progress("g-close-space-done");
            VerifySizeCaptionFit(ref all,report);
            Progress("d-size-done");
            VerifyUndoCaretScroll(ref all,report);
            Progress("e-undo-done");
            VerifyCaretRowClear(ref all,report);
            Progress("bottom-row-done");
            VerifyOverflowAndDialogs(ref all,report);
            Progress("f-overflow-done");
            VerifyMenuSkinAndOverflow(ref all,report);
            Progress("x29-diag-done");
            VerifyFixRound(ref all,report);
            Progress("fix-round-done");
            VerifyViewBughuntRound(ref all,report);
            Progress("view-bughunt-done");
            VerifyOverflowStability(ref all,report);
            Progress("overflow-stability-done");
            VerifyOutlineAndStatusSurfaces(ref all,report);
            Progress("p2-surfaces-done");
        }catch(Exception error){
            all=false;
            report.Append("FAIL exception: ").Append(error.ToString().Replace('\r',' ').Replace('\n',' ')).Append('\n');
        }finally{
            var open=new List<Window>();
            if(Application.Current!=null)foreach(Window candidate in Application.Current.Windows)open.Add(candidate);
            foreach(Window candidate in open){
                try{
                    var editor=candidate as MainWindow;
                    if(editor!=null)Discard(editor);
                    candidate.Close();
                }catch(Exception){}
            }
            System.Threading.SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        File.WriteAllText(Path.Combine(dir,"uifix-report.txt"),report.ToString(),new UTF8Encoding(false));
        if(all){
            File.WriteAllText(passPath,"uifix-probe PASS\n"+DateTime.Now.ToString("o",System.Globalization.CultureInfo.InvariantCulture)+"\n",new UTF8Encoding(false));
            return 0;
        }
        File.WriteAllText(failPath,"uifix-probe FAIL\n",new UTF8Encoding(false));
        return 1;
    }
    static void VerifyCompositionCommands(MainWindow window,TextBox box,ref bool all,StringBuilder report){
        Call(window,"SetText","목록 목");Call(window,"ShowReplace");Pump(300);
        var source=(SourceEditor)FieldOf(window,"source");
        box.Text="";box.CaretIndex=0;
        FieldSet(window,"searchComposing","목");FieldSet(window,"searchComposingStart",0);
        source.Select(0,0);
        Call(window,"FindNext",false);Pump();
        int first=source.SelectionStart;
        bool found=box.Text.Length==0&&source.SelectionLength==1&&source.SelectedText=="목";
        Call(window,"FindNext",false);Pump();
        int next=source.SelectionStart;
        Call(window,"FindNext",true);Pump();
        Check(ref all,report,"review5 composing-only Next/Prev navigate",found&&next>first&&source.SelectionStart==first&&source.SelectedText=="목",
            "first="+first+", next="+next+", previous="+source.SelectionStart+", text="+box.Text);
        ((TextBox)FieldOf(window,"replacement")).Text="끝";
        Call(window,"ReplaceOne");Pump();
        Check(ref all,report,"review5 ReplaceOne uses the composing match",source.Text=="끝록 목","text="+source.Text);

        Call(window,"SetText","목록 목록X");
        box.Text="목";box.CaretIndex=1;
        FieldSet(window,"searchComposing","록");FieldSet(window,"searchComposingStart",1);
        source.Select(0,0);Call(window,"Find");Pump();
        bool length=source.SelectionLength==2&&source.SelectedText=="목록";
        Call(window,"ReplaceAll");Pump();
        Check(ref all,report,"review5 ReplaceAll uses composed length",length&&source.Text=="끝 끝X","text="+source.Text+", highlightedTwo="+length);
        Call(window,"SetText","목 목");
        box.Text="";
        FieldSet(window,"searchComposing","목");FieldSet(window,"searchComposingStart",0);
        Call(window,"ReplaceAll");Pump();
        Check(ref all,report,"review5 composing-only ReplaceAll",source.Text=="끝 끝","text="+source.Text);
        FieldSet(window,"searchComposing","");FieldSet(window,"searchComposingStart",-1);
        bool oldHide=(bool)typeof(MainWindow).GetField("hideMarkers",Members).GetValue(null);
        try{
            typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,true);
            foreach(bool plain in new[]{true,false})foreach(bool one in new[]{true,false}){
                FieldSet(window,"textMode",plain);
                string seed=plain?"목록 목록X":"**목록** <span style=\"color:red\">목록</span>X";
                Call(window,"SetText",seed);Call(window,"ShowReplace");Pump();
                box.Text="목";box.CaretIndex=1;
                FieldSet(window,"searchComposing","록");FieldSet(window,"searchComposingStart",1);
                ((TextBox)FieldOf(window,"replacement")).Text="완료됨";
                source.Select(0,0);
                Call(window,one?"ReplaceOne":"ReplaceAll");Pump();
                var session=FieldValue<MarkdownLivePreview.Session>(window,"livePreview");
                int[] offsets;
                string actual=plain?source.Text:session.VisibleText(out offsets);
                string expected=one?"완료됨 목록X":"완료됨 완료됨X";
                Check(ref all,report,"x8 composing Replace"+(one?"One":"All")+(plain?" TXT":" visible MD"),actual==expected&&(string)Call(window,"QueryText")=="목록"&&box.Text=="목",
                    "visible="+actual+", query="+Call(window,"QueryText")+", field="+box.Text);
                Call(window,"Undo");Pump();
                Check(ref all,report,"x8 composed replacement one-step undo",source.Text==seed,"restored="+(source.Text==seed));
            }
        }finally{typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,oldHide);}
        FieldSet(window,"searchComposing","");FieldSet(window,"searchComposingStart",-1);
        // Closing the bar must also drop an IME latch the find field left in the editor. The
        // window-level hook in SourceEditor sees every WM_IME_* of this HwndSource, so a Korean
        // clause typed in the field is booked for the body too, while its result text never
        // reaches the TextArea. The stale latch made RestoreEditorHistory skip every Ctrl+Z after
        // Esc (real input 2026-10-31: the z typed into the body could not be undone). CloseSearch
        // now abandons it when no composition string is left in the context.
        Call(window,"SetText","가나다");
        source.Select(0,0);
        Call(window,"ShowSearch");Pump(300);
        source.SimulateImeComposition();
        Call(window,"CloseSearch");Pump(300);
        bool free=!source.ImeBusy&&!(bool)Call(window,"CompositionOwnsStore");
        source.Select(0,0);
        source.SelectedText="z";Pump(200);
        string dirty=source.Text;
        Call(window,"Undo");Pump(300);
        Check(ref all,report,"review5 find close drops the field's IME latch and undo runs",
            free&&dirty=="z가나다"&&source.Text=="가나다",
            "free="+free+", typed=["+dirty+"], after undo=["+source.Text+"]");
    }
    /* UI bug hunt items 10, 16 and 22 (2026-10-31): the replace row at the window's 360 px minimum, the
       Aa toggle taking the keyboard out of the find field, and Escape while the table size grid or the
       colour palette is open over the bar. Bounds come from the layout, not a screenshot, so the window
       stays off the desktop; the focus check activates it on the hidden desktop, as the menu checks do. */
    static void VerifyFindBarLayoutAndEscape(ref bool all,StringBuilder report){
        Progress("bh-start");
        MainWindow window=NewWindow();
        try{
            window.SetText("alpha beta\nalpha\n");window.SetMode(true);Pump(800);
            Call(window,"ShowReplace");Pump(600);
            var host=FieldValue<Border>(window,"searchHost");
            var queryBox=FieldValue<TextBox>(window,"query");
            var entry=FieldValue<TextBox>(window,"replacement");
            var one=FieldValue<Button>(window,"replaceOneButton");
            var replaceAll=FieldValue<Button>(window,"replaceAllButton");
            var matchCaseBox=FieldValue<CheckBox>(window,"matchCase");
            if(queryBox!=null){queryBox.Text="alpha";Pump(600);}
            /* h1: Aa must not be a stop for the keyboard. Focus() is the claim a mouse click makes on a
               focusable button; it has to fail while the field keeps the keyboard, and the IsChecked
               handler must not move the focus either. */
            if(matchCaseBox!=null&&queryBox!=null){
                window.Activate();Pump(300);
                queryBox.Focus();Keyboard.Focus(queryBox);Pump(300);
                bool inQuery=queryBox.IsKeyboardFocusWithin;
                bool claimed=matchCaseBox.Focus();
                Pump(200);
                bool afterClaim=queryBox.IsKeyboardFocusWithin;
                matchCaseBox.IsChecked=true;Pump(400);
                bool afterToggle=queryBox.IsKeyboardFocusWithin;
                Check(ref all,report,"bh16 Aa is not a focus target and the query keeps the keyboard",
                    !matchCaseBox.Focusable&&inQuery&&!claimed&&afterClaim&&afterToggle&&!matchCaseBox.IsKeyboardFocusWithin,
                    "focusable="+matchCaseBox.Focusable+" inQuery="+inQuery+" claimed="+claimed+" afterClaim="+afterClaim+" afterToggle="+afterToggle+" aaFocus="+matchCaseBox.IsKeyboardFocusWithin);
                matchCaseBox.IsChecked=false;Pump(300);
            }
            /* h2: Escape with the table size grid open over the bar - the grid goes, the bar stays. */
            Call(window,"OpenTablePicker");Pump(800);
            var picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            bool pickerOpened=picker!=null&&picker.IsOpen;
            RaiseKey(window,window,Key.Escape);Pump(600);
            Check(ref all,report,"bh22 Escape closes the table grid and keeps the find bar",
                pickerOpened&&picker!=null&&!picker.IsOpen&&host!=null&&host.Visibility==Visibility.Visible,
                "opened="+pickerOpened+" grid="+(picker!=null&&picker.IsOpen)+" bar="+(host==null?"missing":host.Visibility.ToString()));
            /* h3: the same key with the colour palette open. */
            var palette=FieldValue<Popup>(window,"colorPopup");
            bool paletteOpened=false;
            if(palette!=null){
                try{palette.IsOpen=true;Pump(400);paletteOpened=palette.IsOpen;}
                catch(Exception failure){paletteOpened=false;report.Append("palette open failed: ").Append(OneLine(failure.Message)).Append('\n');}
            }
            RaiseKey(window,window,Key.Escape);Pump(500);
            Check(ref all,report,"bh22 Escape closes the colour palette and keeps the find bar",
                paletteOpened&&palette!=null&&!palette.IsOpen&&host!=null&&host.Visibility==Visibility.Visible,
                "opened="+paletteOpened+" palette="+(palette!=null&&palette.IsOpen)+" bar="+(host==null?"missing":host.Visibility.ToString()));
            /* h4: with nothing open the same key still closes the bar. */
            RaiseKey(window,window,Key.Escape);Pump(600);
            Check(ref all,report,"bh22 Escape with no popup still closes the find bar",host!=null&&host.Visibility==Visibility.Collapsed,"bar="+(host==null?"missing":host.Visibility.ToString()));
            /* h5: the 360 px minimum window. The row has to wrap rather than run past the right edge:
               both buttons fully inside the content, enabled for the "alpha" matches, and not sitting on
               top of the replace field. */
            Call(window,"ShowReplace");Pump(500);
            window.Width=360;window.UpdateLayout();Pump(900);
            window.UpdateLayout();Pump(300);
            var root=window.Content as FrameworkElement;
            var row=FieldValue<StackPanel>(window,"replaceRow");
            bool barOpen=host!=null&&host.Visibility==Visibility.Visible&&row!=null&&row.Visibility==Visibility.Visible;
            Check(ref all,report,"bh10 the replace row opens in a 360 px window",barOpen,
                "bar="+(host==null?"missing":host.Visibility.ToString())+" row="+(row==null?"missing":row.Visibility.ToString())+" width="+window.ActualWidth.ToString("0")+" content="+(root==null?-1:root.ActualWidth).ToString("0"));
            if(barOpen&&root!=null){
                Check(ref all,report,"bh10 모두 바꾸기 is fully inside and enabled at 360 px",
                    FindBarInside(replaceAll,root)&&replaceAll.IsEnabled,
                    FindBarBoundsText(replaceAll,root)+" root="+root.ActualWidth.ToString("0")+"x"+root.ActualHeight.ToString("0")+" rowWidth="+row.ActualWidth.ToString("0")+" enabled="+replaceAll.IsEnabled);
                Check(ref all,report,"bh10 바꾸기 is fully inside the window at 360 px",FindBarInside(one,root),FindBarBoundsText(one,root)+" root="+root.ActualWidth.ToString("0"));
                Check(ref all,report,"bh10 the row keeps the field and the buttons apart",!FindBarOverlap(root,entry,one,replaceAll),
                    FindBarBoundsText(entry,root)+" | "+FindBarBoundsText(one,root)+" | "+FindBarBoundsText(replaceAll,root));
            }
        }catch(Exception error){all=false;report.Append("FAIL h find-bar layout/escape exception: ").Append(OneLine(error.ToString())).Append('\n');}
        finally{Discard(window);window.Close();}
    }
    static Rect FindBarBounds(FrameworkElement element,FrameworkElement root){
        if(element==null||root==null||element.ActualWidth<=0||element.ActualHeight<=0)return Rect.Empty;
        try{return new Rect(element.TransformToAncestor(root).Transform(new Point(0,0)),new Size(element.ActualWidth,element.ActualHeight));}
        catch(InvalidOperationException){return Rect.Empty;}
    }
    static bool FindBarInside(FrameworkElement element,FrameworkElement root){
        Rect bounds=FindBarBounds(element,root);
        return !bounds.IsEmpty&&element.IsVisible&&bounds.Left>=-0.5&&bounds.Top>=-0.5&&bounds.Right<=root.ActualWidth+0.5&&bounds.Bottom<=root.ActualHeight+0.5;
    }
    static string FindBarBoundsText(FrameworkElement element,FrameworkElement root){
        Rect bounds=FindBarBounds(element,root);
        if(element==null)return "missing";
        if(bounds.IsEmpty)return "no bounds";
        return "bounds="+bounds.Left.ToString("0")+","+bounds.Top.ToString("0")+" "+bounds.Width.ToString("0")+"x"+bounds.Height.ToString("0");
    }
    static bool FindBarOverlap(FrameworkElement root,FrameworkElement first,FrameworkElement second,FrameworkElement third){
        Rect a=FindBarBounds(first,root),b=FindBarBounds(second,root),c=FindBarBounds(third,root);
        if(a.IsEmpty||b.IsEmpty||c.IsEmpty)return true;
        return a.IntersectsWith(b)||a.IntersectsWith(c)||b.IntersectsWith(c);
    }
    // Two find/replace defects: a "|" typed into a replacement that lands inside a table cell used
    // to add a cell (replace one and replace all, both search paths), and a view switch with the bar
    // open used to leave the old view's count and current index until the next find action
    // (basic/live, 원문 toggle, TXT conversion).
    static void VerifyFindReplaceRegressions(ref bool all,StringBuilder report){
        Progress("f1-find-replace-start");
        MainWindow window=NewWindow();
        bool oldHide=(bool)typeof(MainWindow).GetField("hideMarkers",Members).GetValue(null);
        try{
            var source=(SourceEditor)FieldOf(window,"source");
            var box=(TextBox)FieldOf(window,"query");
            var field=(TextBox)FieldOf(window,"replacement");
            var matchCaseBox=(CheckBox)FieldOf(window,"matchCase");
            matchCaseBox.IsChecked=false;
            string table="| a | b |\n| --- | --- |\n| alpha | beta |\n";
            string tableEscaped="| a | b |\n| --- | --- |\n| A\\|B | beta |\n";
            string tableTwo="| alpha | b |\n| --- | --- |\n| alpha | beta |\n";
            string tableTwoEscaped="| A\\|B | b |\n| --- | --- |\n| A\\|B | beta |\n";
            foreach(bool hide in new[]{true,false}){
                string mode=hide?" (basic)":" (live)";
                typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,hide);
                // W1: replace one writes "\|" into the cell.
                window.SetText(table);window.SetMode(true);Pump(400);
                Call(window,"ShowReplace");Pump(300);
                box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(400);
                field.Text="A|B";
                source.Select(0,0);Call(window,"Find");Pump(300);
                Call(window,"ReplaceOne");Pump(400);
                Check(ref all,report,"f1 table cell replace-one escapes the pipe"+mode,source.Text==tableEscaped,"text=["+OneLine(source.Text)+"]");
                Call(window,"Undo");Pump(300);
                Check(ref all,report,"f1 table replace-one is one undo step"+mode,source.Text==table,"text=["+OneLine(source.Text)+"]");
                // W1: replace all escapes every cell and stays one undo step.
                window.SetText(tableTwo);window.SetMode(true);Pump(400);
                box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(400);
                field.Text="A|B";
                Call(window,"ReplaceAll");Pump(500);
                Check(ref all,report,"f1 table cells replace-all escapes every pipe"+mode,source.Text==tableTwoEscaped,"text=["+OneLine(source.Text)+"]");
                Call(window,"Undo");Pump(300);
                Check(ref all,report,"f1 table replace-all is one undo step"+mode,source.Text==tableTwo,"text=["+OneLine(source.Text)+"]");
                // W1: a pipe that already carries a backslash is not escaped twice.
                window.SetText(table);window.SetMode(true);Pump(400);
                box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(400);
                field.Text="A\\|B";
                source.Select(0,0);Call(window,"Find");Pump(300);
                Call(window,"ReplaceOne");Pump(400);
                Check(ref all,report,"f1 an escaped pipe is not escaped twice"+mode,source.Text==tableEscaped,"text=["+OneLine(source.Text)+"]");
                // W1: outside a table the pipe is written as typed.
                window.SetText("alpha beta\n");window.SetMode(true);Pump(400);
                box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(400);
                field.Text="A|B";
                source.Select(0,0);Call(window,"Find");Pump(300);
                Call(window,"ReplaceOne");Pump(400);
                Check(ref all,report,"f1 a pipe outside a table is literal"+mode,source.Text=="A|B beta\n","text=["+OneLine(source.Text)+"]");
            }
            // W2: basic (visible text: one "alpha") to live (source: two) and back, bar open.
            // SetLiveMode(false) is the basic view (markers hidden), SetLiveMode(true) the live view.
            Call(window,"SetLiveMode",false);Pump(400);
            window.SetText("[링크](https://x.invalid/alpha) alpha\n");window.SetMode(true);Pump(500);
            Call(window,"ShowSearch");Pump(300);
            box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(500);
            Check(ref all,report,"f1 basic visible count before the switch",Total(CountOf(window))=="1","count="+CountOf(window));
            source.Select(0,0);Call(window,"Find");Pump(300);
            Check(ref all,report,"f1 basic current index after find",CountOf(window)=="1 / 1","count="+CountOf(window));
            Call(window,"SetLiveMode",true);Pump(500);
            Check(ref all,report,"f1 basic to live updates count and index",CountOf(window)=="2 / 2","count="+CountOf(window));
            Call(window,"SetLiveMode",false);Pump(500);
            Check(ref all,report,"f1 live to basic updates count and index",CountOf(window)=="1 / 1","count="+CountOf(window));
            // W2: the 원문 toggle detaches the preview, the bar must follow to the source text.
            var preview=(CheckBox)FieldOf(window,"preview");
            FieldSet(window,"loading",true);preview.IsChecked=true;FieldSet(window,"loading",false);
            Call(window,"SwitchSurface");Pump(500);
            Check(ref all,report,"f1 원문 toggle updates the count",CountOf(window)=="2 / 2","count="+CountOf(window));
            FieldSet(window,"loading",true);preview.IsChecked=false;FieldSet(window,"loading",false);
            Call(window,"SwitchSurface");Pump(500);
            Check(ref all,report,"f1 원문 toggle back updates the count",CountOf(window)=="1 / 1","count="+CountOf(window));
            // W2: TXT conversion rewrites the document; the count must be the converted text's.
            Call(window,"ToggleTextModeNow");Pump(700);
            int textTotal=Occurrences(source.Text,"alpha");
            Check(ref all,report,"f1 TXT conversion updates the count",Total(CountOf(window))==textTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "count="+CountOf(window)+", expected="+textTotal+", text=["+OneLine(source.Text)+"]");
            Call(window,"ToggleTextModeNow");Pump(700);
            var session=FieldValue<MarkdownLivePreview.Session>(window,"livePreview");
            int[] offsets=null;
            bool attached=(bool)typeof(MainWindow).GetProperty("LivePreviewAttached",Members).GetValue(window,null);
            string shown=session==null||!attached||!session.SearchHiddenMarkup?source.Text:session.VisibleText(out offsets);
            int markdownTotal=Occurrences(shown,"alpha");
            Check(ref all,report,"f1 MD conversion back updates the count",Total(CountOf(window))==markdownTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "count="+CountOf(window)+", expected="+markdownTotal+", text=["+OneLine(shown)+"]");
        }catch(Exception error){
            all=false;report.Append("FAIL f1 find/replace regression exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,oldHide);
            FieldSet(window,"loading",false);
            Discard(window);window.Close();
        }
    }
    static int Occurrences(string text,string needle){
        if(String.IsNullOrEmpty(text)||String.IsNullOrEmpty(needle))return 0;
        int count=0,at=0;
        while(at<=text.Length-needle.Length){
            int found=text.IndexOf(needle,at,StringComparison.OrdinalIgnoreCase);
            if(found<0)break;
            count++;at=found+needle.Length;
        }
        return count;
    }
    /* UI bug hunt 4, save/find round (2026-10-31). Four user-visible defects: (1) 모두 바꾸기 left
       the whole document selected in TXT, live and 원문 view, so the next keystroke wiped it; (2) a
       Save As onto a file another tab or window held made a second editor for one path; (3) a
       conflict the user approved by keeping the same file name asked again; (4) the replace row had
       no keyboard command and its shortcuts were not named. The review findings of that round add
       two more: the conflict approval is a parameter of its own retry save (finding 1) and both
       replace runs refuse to write while a file open or conversion owns the document (finding 4).
       Everything runs offscreen and every fixture lives under the probe's own output folder; no
       clipboard, no native file dialog. */
    static void VerifySaveAndReplaceFixes(string dir,ref bool all,StringBuilder report){
        Progress("s4-start");
        MainWindow window=NewWindow();
        bool oldHide=(bool)typeof(MainWindow).GetField("hideMarkers",Members).GetValue(null);
        var preview=(CheckBox)FieldOf(window,"preview");
        try{
            // 1: basic view (visible text), live view (source), 원문 (detached source), TXT.
            Call(window,"SetLiveMode",false);Pump(300);
            CheckReplaceAllSurface(window,"basic view",false,ref all,report);
            Call(window,"SetLiveMode",true);Pump(300);
            CheckReplaceAllSurface(window,"live view",false,ref all,report);
            FieldSet(window,"loading",true);preview.IsChecked=true;FieldSet(window,"loading",false);Call(window,"SwitchSurface");Pump(400);
            CheckReplaceAllSurface(window,"원문 view",true,ref all,report);
            FieldSet(window,"loading",true);preview.IsChecked=false;FieldSet(window,"loading",false);Call(window,"SwitchSurface");Pump(400);
            Call(window,"CloseSearch");Pump(200);
            Call(window,"ToggleTextModeNow");Pump(500);
            CheckReplaceAllSurface(window,"TXT",false,ref all,report);
            Call(window,"CloseSearch");Pump(200);
            Call(window,"ToggleTextModeNow");Pump(500);
            Call(window,"SetLiveMode",true);Pump(300);
            CheckReplaceAllKeepsViewport(window,ref all,report);
        }catch(Exception error){
            all=false;report.Append("FAIL s4 replace-all exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,oldHide);
            FieldSet(window,"loading",false);
            Discard(window);window.Close();
        }
        CheckSaveAsOntoOpenTab(dir,ref all,report);
        CheckConflictRetryOnePrompt(dir,ref all,report);
        CheckConflictApprovalNotShared(dir,ref all,report);
        CheckOpenWithEncodingSwitchesTab(dir,ref all,report);
        CheckReplaceFieldKeys(ref all,report);
        CheckReplaceRefusedWhileBusy(ref all,report);
    }
    // Item 1: one surface at a time. The caret starts inside "beta" (offset 1), so a document that
    // stays selected, a caret that moved to the end and a selection left over from a match all fail.
    static void CheckReplaceAllSurface(MainWindow window,string label,bool sourceView,ref bool all,StringBuilder report){
        var source=(SourceEditor)FieldOf(window,"source");
        var preview=(CheckBox)FieldOf(window,"preview");
        const string seed="alpha beta alpha\n";
        const string expected="X beta X\n";
        Call(window,"SetText",seed);window.SetMode(true);Pump(300);
        if(sourceView){FieldSet(window,"loading",true);preview.IsChecked=true;FieldSet(window,"loading",false);Call(window,"SwitchSurface");Pump(400);}
        Call(window,"ShowReplace");Pump(400);
        var box=(TextBox)FieldOf(window,"query");var field=(TextBox)FieldOf(window,"replacement");
        box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(500);
        field.Text="X";Pump(100);
        source.Select(1,0);Pump(150);
        Call(window,"ReplaceAll");Pump(500);
        bool replaced=String.Equals(source.Document.Text,expected,StringComparison.Ordinal);
        bool caret=source.SelectionLength==0&&source.CaretIndex==1;
        string shown=OneLine(source.Document.Text);
        Call(window,"Undo");Pump(400);
        bool undone=String.Equals(source.Document.Text,seed,StringComparison.Ordinal);
        Check(ref all,report,"s4 replace-all "+label+" leaves no selection and keeps the caret",replaced&&caret,
            "text=["+shown+"] selection="+source.SelectionLength+" caret="+source.CaretIndex);
        Check(ref all,report,"s4 replace-all "+label+" is one undo step",undone,"after undo text=["+OneLine(source.Document.Text)+"]");
        if(sourceView){FieldSet(window,"loading",true);preview.IsChecked=false;FieldSet(window,"loading",false);Call(window,"SwitchSurface");Pump(300);}
    }
    static void FirstVisibleLine(SourceEditor source,out int line,out double top){
        line=0;top=Double.NaN;
        ICSharpCode.AvalonEdit.Rendering.TextView view=source.TextArea.TextView;
        view.EnsureVisualLines();
        if(view.VisualLines.Count==0)return;
        ICSharpCode.AvalonEdit.Rendering.VisualLine first=view.VisualLines[0];
        line=first.FirstDocumentLine.LineNumber;
        top=first.VisualTop-view.VerticalOffset;
    }
    // Item 1: the same run must not scroll the reader away (source path: whole-document replace).
    static void CheckReplaceAllKeepsViewport(MainWindow window,ref bool all,StringBuilder report){
        var source=(SourceEditor)FieldOf(window,"source");
        var builder=new StringBuilder();
        for(int i=0;i<60;i++)builder.Append("alpha line ").Append(i).Append('\n');
        string seed=builder.ToString();
        Call(window,"SetText",seed);window.SetMode(true);Pump(400);
        Call(window,"ShowReplace");Pump(400);
        var box=(TextBox)FieldOf(window,"query");var field=(TextBox)FieldOf(window,"replacement");
        box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(500);
        field.Text="X";Pump(100);
        int caret=source.Document.GetLineByNumber(40).Offset;
        source.Select(caret,0);Pump(150);
        source.ScrollToLine(40);window.UpdateLayout();Pump(500);
        double before=source.VerticalOffset;
        int lineBefore;double topBefore;FirstVisibleLine(source,out lineBefore,out topBefore);
        Call(window,"ReplaceAll");Pump(600);window.UpdateLayout();Pump(200);
        double after=source.VerticalOffset;
        int lineAfter;double topAfter;FirstVisibleLine(source,out lineAfter,out topAfter);
        /* The reader's view is the first visible line and where it sits on screen. A replacement
           that rewrites the lines above the view drops their measured heights, so the scroll
           offset in pixels can change while the same text stays in the same place (11-01). */
        Check(ref all,report,"s4 replace-all keeps the view and the caret in place",
            lineAfter==lineBefore&&Math.Abs(topAfter-topBefore)<=1.0&&source.SelectionLength==0&&source.CaretIndex==caret,
            "first line="+lineBefore+"->"+lineAfter+", top="+topBefore.ToString("0.##")+"->"+topAfter.ToString("0.##")+", offset="+before.ToString("0.##")+"->"+after.ToString("0.##")+", caret="+caret+"->"+source.CaretIndex+", selection="+source.SelectionLength);
    }
    // Item 2: the Save As target is another tab's file. It must be refused with the localized
    // message before anything is written, and both tabs must survive.
    static void CheckSaveAsOntoOpenTab(string dir,ref bool all,StringBuilder report){
        string first=Path.Combine(dir,"s4-open-first.md"),second=Path.Combine(dir,"s4-open-second.md");
        File.WriteAllText(first,"first file\n",new UTF8Encoding(false));
        File.WriteAllText(second,"second file\n",new UTF8Encoding(false));
        MainWindow window=NewWindow();
        string notice=null;
        try{
            /* The field, not SetOpenInTabs: the check needs two tabs, not the user's saved option. */
            FieldSet(window,"openInTabs",true);Pump(100);
            window.LoadFile(first);Pump(300);
            window.LoadFile(second);Pump(300);
            int tabs=window.OpenDocumentCount;
            string expected=MainWindow.TranslatedText((string)FieldOf(window,"language"),"이 파일은 다른 탭이나 창에서 이미 열려 있습니다. 그 탭이나 창에서 저장해 주세요.");
            MainWindow.SaveTargetInUseNotice=delegate(string text){notice=text;};
            MainWindow.SaveTargetPicker=delegate(string name){return first;};
            bool saved=(bool)Call(window,"Save",true);
            Pump(200);
            bool messaged=notice!=null&&String.Equals(notice,expected,StringComparison.Ordinal);
            bool untouched=String.Equals(File.ReadAllText(first,new UTF8Encoding(false)),"first file\n",StringComparison.Ordinal);
            Check(ref all,report,"s4 save-as onto a file open in another tab is refused",
                !saved&&messaged&&untouched&&window.OpenDocumentCount==tabs,
                "saved="+saved+" notice=["+OneLine(notice)+"] expected=["+OneLine(expected)+"] first=["+OneLine(File.ReadAllText(first,new UTF8Encoding(false)))+"] tabs="+window.OpenDocumentCount+"/"+tabs);
        }catch(Exception error){
            all=false;report.Append("FAIL s4 save-as duplicate exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            MainWindow.SaveTargetPicker=null;MainWindow.SaveTargetInUseNotice=null;
            Discard(window);window.Close();
        }
    }
    // Item 3: approve the conflict and keep the same name; the write goes through once, and the
    // stored fingerprint follows it, so the next save of the same file does not ask again.
    static void CheckConflictRetryOnePrompt(string dir,ref bool all,StringBuilder report){
        string file=Path.Combine(dir,"s4-conflict.md");
        File.WriteAllText(file,"origin line\r\n",new UTF8Encoding(false));
        MainWindow window=NewWindow();
        try{
            window.LoadFile(file);Pump(300);
            window.SetMode(true);Pump(200);
            var source=(SourceEditor)FieldOf(window,"source");
            string newline=(string)FieldOf(window,"documentNewline");
            source.Select(source.Document.TextLength,0);Pump(100);
            TypeText(source," LOCAL");Pump(250);
            Call(window,"FlushDocumentChanges");
            string expected=NormalizeNewlines(source.Document.Text,newline);
            File.WriteAllText(file,"external version\r\nsecond\r\n",new UTF8Encoding(false));
            int promptsBefore=MainWindow.ConflictPrompts;
            MainWindow.ConflictAnswer=MessageBoxResult.Yes;
            MainWindow.SaveTargetPicker=delegate(string name){return file;};
            bool saved=(bool)Call(window,"Save",false);
            int prompts=MainWindow.ConflictPrompts-promptsBefore;
            string disk=File.Exists(file)?File.ReadAllText(file,new UTF8Encoding(false)):null;
            Check(ref all,report,"s4 an approved conflict retry overwrites the same name once",
                saved&&prompts==1&&String.Equals(disk,expected,StringComparison.Ordinal),
                "saved="+saved+" prompts="+prompts+" disk=["+OneLine(disk)+"] expected=["+OneLine(expected)+"]");
            MainWindow.ConflictAnswer=null;MainWindow.SaveTargetPicker=null;
            source.Select(source.Document.TextLength,0);Pump(100);
            TypeText(source," AGAIN");Pump(250);
            Call(window,"FlushDocumentChanges");
            string expectedAgain=NormalizeNewlines(source.Document.Text,newline);
            int promptsBeforeAgain=MainWindow.ConflictPrompts;
            Exception error=null;bool savedAgain=false;
            try{savedAgain=(bool)Call(window,"Save",false);}catch(Exception saveError){error=saveError;}
            string diskAgain=File.Exists(file)?File.ReadAllText(file,new UTF8Encoding(false)):null;
            Check(ref all,report,"s4 the overwrite updates the fingerprint so the conflict does not return",
                error==null&&savedAgain&&MainWindow.ConflictPrompts==promptsBeforeAgain&&String.Equals(diskAgain,expectedAgain,StringComparison.Ordinal),
                "error="+(error==null?"none":OneLine(error.Message))+" saved="+savedAgain+" prompts="+(MainWindow.ConflictPrompts-promptsBeforeAgain)+" disk=["+OneLine(diskAgain)+"]");
        }catch(Exception error){
            all=false;report.Append("FAIL s4 conflict retry exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            MainWindow.ConflictAnswer=null;MainWindow.SaveTargetPicker=null;
            Discard(window);window.Close();
        }
    }
    /* Finding 1: the conflict prompt's "yes" belongs to the save that asked for it. It travels as the
       retry save's parameter now, not window state, so a second save started while that retry waits
       for a counted IME result must still meet the external edit and ask again instead of inheriting
       the approval and overwriting it. The approved save is started through the parameter seam. */
    static void CheckConflictApprovalNotShared(string dir,ref bool all,StringBuilder report){
        string file=Path.Combine(dir,"s4-approval.md");
        File.WriteAllText(file,"origin line\r\n",new UTF8Encoding(false));
        MainWindow window=NewWindow();
        try{
            window.LoadFile(file);Pump(300);
            window.SetMode(true);Pump(200);
            var source=(SourceEditor)FieldOf(window,"source");
            string newline=(string)FieldOf(window,"documentNewline");
            source.Select(source.Document.TextLength,0);Pump(100);
            TypeText(source," LOCAL");Pump(250);
            Call(window,"FlushDocumentChanges");
            string expected=NormalizeNewlines(source.Document.Text,newline);
            File.WriteAllText(file,"external version\r\nsecond\r\n",new UTF8Encoding(false));
            string approved=Path.GetFullPath(file);
            int promptsBefore=MainWindow.ConflictPrompts;
            MainWindow.ConflictAnswer=MessageBoxResult.No;
            MainWindow.SaveTargetPicker=delegate(string name){return file;};
            /* A retry save holding its Save As dialog until the counted IME result lands. */
            source.SimulateImeComposition();source.SimulateImeCommit(1);
            var retry=(System.Threading.Tasks.Task<bool>)Call(window,"RetrySaveOnDispatcher",approved);
            bool waiting=retry!=null&&!retry.IsCompleted;
            /* Same turn, before that wait ends: an ordinary save carries no approval and must ask. */
            var second=(System.Threading.Tasks.Task<bool>)Call(window,"SaveTaskOnDispatcher",false);
            bool secondSaved=second!=null&&second.GetAwaiter().GetResult();
            int prompts=MainWindow.ConflictPrompts-promptsBefore;
            Pump(1200);
            source.ResetCompositionSimulation();
            bool retried=retry!=null&&retry.IsCompleted&&retry.GetAwaiter().GetResult();
            string disk=File.Exists(file)?File.ReadAllText(file,new UTF8Encoding(false)):null;
            bool stored=typeof(MainWindow).GetField("conflictApprovedPath",Members)!=null;
            Check(ref all,report,"s4 the conflict approval is not reused by a second save",
                waiting&&!secondSaved&&prompts==1&&retried&&String.Equals(disk,expected,StringComparison.Ordinal)&&!stored,
                "waiting="+waiting+" second="+secondSaved+" prompts="+prompts+" retried="+retried+" storedField="+stored+" disk=["+OneLine(disk)+"] expected=["+OneLine(expected)+"]");
        }catch(Exception error){
            all=false;report.Append("FAIL s4 conflict approval exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            MainWindow.ConflictAnswer=null;MainWindow.SaveTargetPicker=null;
            try{((SourceEditor)FieldOf(window,"source")).ResetCompositionSimulation();}catch(Exception){}
            Discard(window);window.Close();
        }
    }
    // Item 2, open-with-encoding half: the queued explicit-encoding open of a file this window
    // already holds switches to that tab (user rule A26) instead of reading a second copy.
    static void CheckOpenWithEncodingSwitchesTab(string dir,ref bool all,StringBuilder report){
        string first=Path.Combine(dir,"s4-encoding-first.md"),second=Path.Combine(dir,"s4-encoding-second.md");
        File.WriteAllText(first,"encoding first\n",new UTF8Encoding(false));
        File.WriteAllText(second,"encoding second\n",new UTF8Encoding(false));
        MainWindow window=NewWindow();
        try{
            /* The field, not SetOpenInTabs: the check needs two tabs, not the user's saved option. */
            FieldSet(window,"openInTabs",true);Pump(100);
            window.LoadFile(first);Pump(300);
            window.LoadFile(second);Pump(300);
            int tabs=window.OpenDocumentCount;
            EnqueueEncodingOpen(window,first,65001);
            bool finished=WaitForOpenQueue(window,10000);
            Pump(400);
            string activePath=(string)FieldOf(window,"path");
            Exception error=(Exception)FieldOf(window,"LastFileOpenError");
            Check(ref all,report,"s4 open-with-encoding switches to the tab that holds the file",
                finished&&error==null&&window.OpenDocumentCount==tabs&&String.Equals(activePath,Path.GetFullPath(first),StringComparison.OrdinalIgnoreCase),
                "finished="+finished+" error="+(error==null?"none":OneLine(error.Message))+" tabs="+window.OpenDocumentCount+"/"+tabs+" active=["+activePath+"]");
        }catch(Exception error){
            all=false;report.Append("FAIL s4 encoding switch exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            Discard(window);window.Close();
        }
    }
    // Item 4: Enter / Alt+R / Alt+A in the replace field, the IME guard, and the tooltips that name
    // the shortcuts. A key the IME owns or a composing syllable must never run a replace.
    static void CheckReplaceFieldKeys(ref bool all,StringBuilder report){
        MainWindow window=NewWindow();
        try{
            window.SetText("alpha alpha alpha\n");window.SetMode(true);Pump(400);
            Call(window,"ShowReplace");Pump(400);
            var source=(SourceEditor)FieldOf(window,"source");
            var box=(TextBox)FieldOf(window,"query");var field=(TextBox)FieldOf(window,"replacement");
            var one=FieldValue<Button>(window,"replaceOneButton");var allButton=FieldValue<Button>(window,"replaceAllButton");
            box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(500);
            field.Text="X";Pump(100);
            source.Select(0,0);Pump(100);
            RaiseTextKey(field,window,Key.Enter);Pump(500);
            bool enter=String.Equals(source.Document.Text,"X alpha alpha\n",StringComparison.Ordinal)&&String.Equals(source.SelectedText,"alpha",StringComparison.Ordinal);
            FieldSet(window,"replaceComposing","록");FieldSet(window,"replaceComposingStart",0);
            RaiseTextKey(field,window,Key.Enter);RaiseTextKey(field,window,Key.ImeProcessed);Pump(300);
            bool composing=String.Equals(source.Document.Text,"X alpha alpha\n",StringComparison.Ordinal);
            FieldSet(window,"replaceComposing","");FieldSet(window,"replaceComposingStart",-1);
            Call(window,"SetText","alpha alpha\n");window.SetMode(true);Pump(300);
            box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(500);field.Text="X";
            ModifierKeys? oldModifiers=MainWindow.ShortcutModifiersProbe;
            MainWindow.ShortcutModifiersProbe=ModifierKeys.Alt;
            RaiseTextKey(field,window,Key.A);
            MainWindow.ShortcutModifiersProbe=oldModifiers;
            Pump(500);
            bool altAll=String.Equals(source.Document.Text,"X X\n",StringComparison.Ordinal);
            Call(window,"SetText","alpha alpha\n");window.SetMode(true);Pump(300);
            box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(500);field.Text="X";
            oldModifiers=MainWindow.ShortcutModifiersProbe;
            MainWindow.ShortcutModifiersProbe=ModifierKeys.Alt;
            RaiseTextKey(field,window,Key.R);
            MainWindow.ShortcutModifiersProbe=oldModifiers;
            Pump(500);
            bool altOne=String.Equals(source.Document.Text,"X alpha\n",StringComparison.Ordinal);
            string oneTip=one==null?"":Convert.ToString(one.ToolTip);
            string allTip=allButton==null?"":Convert.ToString(allButton.ToolTip);
            bool tips=oneTip.IndexOf("Enter",StringComparison.Ordinal)>=0&&oneTip.IndexOf("Alt+R",StringComparison.Ordinal)>=0&&allTip.IndexOf("Alt+A",StringComparison.Ordinal)>=0;
            Check(ref all,report,"s4 Enter in the replace field replaces and steps to the next match",enter,
                "text=["+OneLine(source.Document.Text)+"] selection=["+source.SelectedText+"]");
            Check(ref all,report,"s4 Enter does not run a replace while the field composes",composing,
                "text=["+OneLine(source.Document.Text)+"]");
            Check(ref all,report,"s4 Alt+A replaces every match",altAll,"text=["+OneLine(source.Document.Text)+"]");
            Check(ref all,report,"s4 Alt+R replaces the current match",altOne,"text=["+OneLine(source.Document.Text)+"]");
            Check(ref all,report,"s4 replace buttons name their shortcuts",tips,"one=["+oneTip+"] all=["+allTip+"]");
        }catch(Exception error){
            all=false;report.Append("FAIL s4 replace keys exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            MainWindow.ShortcutModifiersProbe=null;
            Discard(window);window.Close();
        }
    }
    /* Finding 4: a file open or a save's mode conversion owns the document (documentOperationBusy).
       Both replace runs must do nothing then and the two buttons must read as disabled; the field's
       Enter/Alt+A/Alt+R go through the same entries, so they are covered by the same guard. */
    static void CheckReplaceRefusedWhileBusy(ref bool all,StringBuilder report){
        MainWindow window=NewWindow();
        try{
            window.SetText("alpha alpha\n");window.SetMode(true);Pump(400);
            Call(window,"ShowReplace");Pump(400);
            var source=(SourceEditor)FieldOf(window,"source");
            var box=(TextBox)FieldOf(window,"query");var field=(TextBox)FieldOf(window,"replacement");
            var one=FieldValue<Button>(window,"replaceOneButton");var allButton=FieldValue<Button>(window,"replaceAllButton");
            box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(500);
            field.Text="X";Pump(100);
            source.Select(0,0);Pump(100);
            bool lit=one!=null&&allButton!=null&&one.IsEnabled&&allButton.IsEnabled;
            FieldSet(window,"documentOperationBusy",true);
            Call(window,"LayoutMode");Pump(200);
            bool off=one!=null&&allButton!=null&&!one.IsEnabled&&!allButton.IsEnabled;
            Call(window,"ReplaceAll");Pump(300);
            bool allRefused=String.Equals(source.Document.Text,"alpha alpha\n",StringComparison.Ordinal);
            ModifierKeys? oldModifiers=MainWindow.ShortcutModifiersProbe;
            MainWindow.ShortcutModifiersProbe=ModifierKeys.Alt;
            RaiseTextKey(field,window,Key.A);
            MainWindow.ShortcutModifiersProbe=oldModifiers;
            Pump(300);
            bool keyRefused=String.Equals(source.Document.Text,"alpha alpha\n",StringComparison.Ordinal);
            RaiseTextKey(field,window,Key.Enter);Pump(300);
            bool enterRefused=String.Equals(source.Document.Text,"alpha alpha\n",StringComparison.Ordinal);
            FieldSet(window,"documentOperationBusy",false);
            Call(window,"LayoutMode");Pump(200);
            bool back=one!=null&&allButton!=null&&one.IsEnabled&&allButton.IsEnabled;
            Check(ref all,report,"s4 replace is refused and disabled while a document operation runs",
                lit&&off&&allRefused&&keyRefused&&enterRefused&&back,
                "lit="+lit+" off="+off+" allRefused="+allRefused+" keyRefused="+keyRefused+" enterRefused="+enterRefused+" back="+back);
        }catch(Exception error){
            all=false;report.Append("FAIL s4 replace busy exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            MainWindow.ShortcutModifiersProbe=null;
            FieldSet(window,"documentOperationBusy",false);
            Discard(window);window.Close();
        }
    }
    // The key event the field's own handler sees (bubbling KeyDown, not the window's PreviewKeyDown).
    static void RaiseTextKey(UIElement target,Visual visual,Key key){
        var presentation=PresentationSource.FromVisual(visual);
        if(presentation==null)return;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,key){RoutedEvent=Keyboard.KeyDownEvent});
    }
    static string NormalizeNewlines(string value,string newline){
        if(value==null)return null;
        if(String.IsNullOrEmpty(newline)||newline=="\n")return value.Replace("\r\n","\n").Replace('\r','\n');
        if(newline=="\r\n")return value.Replace("\r\n","\n").Replace('\r','\n').Replace("\n","\r\n");
        return value.Replace("\r\n","\r").Replace('\n','\r');
    }
    // The app's own queued open path, shaped like the "인코딩을 지정하여 열기" menu entry.
    static void EnqueueEncodingOpen(MainWindow window,string path,int? codePage){
        Type operation=typeof(MainWindow).GetNestedType("FileOperation",Members);
        if(operation==null)throw new MissingMemberException("probe: MainWindow has no nested FileOperation");
        object item=Activator.CreateInstance(operation,true);
        operation.GetField("Name",Members).SetValue(item,path);
        operation.GetField("CodePage",Members).SetValue(item,codePage);
        MethodInfo enqueue=typeof(MainWindow).GetMethod("EnqueueFileOperation",Members);
        if(enqueue==null)throw new MissingMemberException("probe: MainWindow has no EnqueueFileOperation");
        enqueue.Invoke(window,new object[]{item});
    }
    static bool WaitForOpenQueue(MainWindow window,int timeoutMilliseconds){
        var watch=System.Diagnostics.Stopwatch.StartNew();
        FieldInfo running=typeof(MainWindow).GetField("fileOpenRunning",Members);
        while(watch.ElapsedMilliseconds<timeoutMilliseconds){
            Pump();
            if(running!=null&&!Object.Equals(running.GetValue(window),true))return true;
            System.Threading.Thread.Sleep(10);
        }
        return false;
    }
    // The inline-code toolbar button must be lit exactly where the caret is inside an inline code
    // run. A caret one step past the closing backtick is outside it (the hidden closing run draws
    // on the pair's inside, which the old rule reported as code), and F7 on a selected word must
    // leave the button on only while the caret stays in the new run.
    static void VerifyInlineCodeButton(ref bool all,StringBuilder report){
        Progress("c1-code-start");
        MainWindow window=NewWindow();
        bool oldHide=(bool)typeof(MainWindow).GetField("hideMarkers",Members).GetValue(null);
        try{
            var source=(SourceEditor)FieldOf(window,"source");
            var button=(Button)FieldOf(window,"codeButton");
            const string body="seed `code` tail\n";
            foreach(bool hide in new[]{true,false}){
                typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,hide);
                window.SetText(body);window.SetMode(true);Pump(400);
                int open=body.IndexOf('`'),close=body.IndexOf('`',open+1);
                var boxes=new List<string>();
                source.Select(open+3,0);Pump(200);bool inside=Object.Equals(button.Tag,"Active");boxes.Add("inside="+inside);
                source.Select(close+1,0);Pump(200);bool after=Object.Equals(button.Tag,"Active");boxes.Add("afterClose="+after);
                source.Select(open,0);Pump(200);bool before=Object.Equals(button.Tag,"Active");boxes.Add("beforeOpen="+before);
                source.Select(body.Length-1,0);Pump(200);bool lineEnd=Object.Equals(button.Tag,"Active");boxes.Add("lineEnd="+lineEnd);
                Check(ref all,report,"c1 inline code button follows the caret"+(hide?" (markers hidden)":" (markers shown)"),
                    inside&&!after&&!before&&!lineEnd,String.Join(", ",boxes.ToArray()));
            }
            typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,true);
            // F7 on a selected word: the caret lands inside the new run, then leaves it.
            window.SetText("seed word tail\n");window.SetMode(true);Pump(400);
            source.Select(5,4);Pump(200);
            RaiseFunctionKey(window,Key.F7);Pump(350);
            string applied=source.Text;
            bool lit=Object.Equals(button.Tag,"Active");
            source.Select(source.Document.TextLength-1,0);Pump(250);
            bool cleared=!Object.Equals(button.Tag,"Active");
            Check(ref all,report,"c1 F7 inline code lights the button only inside the run",applied.Contains("`word`")&&lit&&cleared,
                "text=["+OneLine(applied)+"] lit="+lit+" cleared="+cleared+" tag="+button.Tag);
        }catch(Exception error){
            all=false;report.Append("FAIL c inline code button exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,oldHide);Discard(window);window.Close();}
    }
    // The undo/redo toolbar buttons follow the document history, not keyboard focus: clicking a
    // menu or the find field must not gray them, and pressing one while focus is elsewhere still
    // undoes the document and hands the keyboard back to the editor.
    static void VerifyUndoRedoToolbar(ref bool all,StringBuilder report){
        Progress("d1-undo-start");
        MainWindow window=NewWindow();
        try{
            window.SetText("seed");window.SetMode(true);Pump(400);
            var source=(SourceEditor)FieldOf(window,"source");
            var undo=(Button)FieldOf(window,"undoButton");var redo=(Button)FieldOf(window,"redoButton");
            source.Select(source.Document.TextLength,0);source.Focus();Pump(150);
            source.TextArea.PerformTextInput("abc");Pump(350);
            bool typed=source.Text=="seedabc"&&undo.IsEnabled;
            Call(window,"ShowSearch");Pump(300);
            var box=(TextBox)FieldOf(window,"query");
            box.Focus();Keyboard.Focus(box);Pump(250);
            bool inField=undo.IsEnabled&&!redo.IsEnabled;
            MenuItem first=null;
            var menu=window.ToolbarMenu();
            if(menu!=null&&menu.Items.Count>0)first=menu.Items[0] as MenuItem;
            if(first!=null){first.IsSubmenuOpen=true;Pump(250);}
            bool inMenu=undo.IsEnabled&&!redo.IsEnabled;
            if(first!=null){first.IsSubmenuOpen=false;Pump(150);}
            undo.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump(350);
            bool undone=source.Text=="seed";
            bool enabledAfter=redo.IsEnabled&&!undo.IsEnabled;
            bool focused=source.TextArea.IsKeyboardFocusWithin;
            Call(window,"CloseSearch");
            Pump(150);
            redo.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump(300);
            bool redone=source.Text=="seedabc";
            Check(ref all,report,"d1 undo/redo buttons follow the history without focus",typed&&inField&&inMenu&&undone&&enabledAfter&&focused&&redone,
                "typed="+typed+" inField="+inField+" inMenu="+inMenu+" undone="+undone+" flags="+enabledAfter+" focusBack="+focused+" redone="+redone
                +" text=["+OneLine(source.Text)+"]");
        }catch(Exception error){
            all=false;report.Append("FAIL d undo/redo toolbar exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{Discard(window);window.Close();}
    }
    // The strip the user can see: 2+ documents require Visible, laid out at its real height, one item each.
    static void CheckStrip(ref bool all,StringBuilder report,string name,MainWindow window){
        var tabs=TabControlOf(window);
        int documents=window.OpenDocumentCount;
        bool visible=tabs!=null&&tabs.Visibility==Visibility.Visible;
        double height=tabs==null?0:tabs.ActualHeight;
        int items=tabs==null?-1:tabs.Items.Count;
        bool ok=documents<2||(visible&&height>=24&&items==documents);
        Check(ref all,report,name+" tab strip shows every document",ok,"documents="+documents+" visible="+visible+" height="+height.ToString("0.0")+" items="+items);
    }
    static string OneLine(string value){return value==null?"":value.Replace("\r"," ").Replace("\n","|");}
    static void ResetDocument(MainWindow window,string body){
        window.SetText(body);window.SetMode(true);Pump(300);
        var source=(SourceEditor)FieldOf(window,"source");
        string clean=(string)typeof(MainWindow).GetProperty("text",Members).GetValue(window,null);
        FieldSet(window,"saved",clean);FieldSet(window,"recoveredDocument",false);
        // Strictly inside the word: a caret at a word edge is the "style for the text typed
        // next" case (ToggleInlineAtEdge writes nothing until the first input), so the F-key
        // and toolbar outputs would both equal the body and prove nothing.
        source.Select(source.Document.GetLineByNumber(3).Offset+1,0);
    }
    static void RaiseFunctionKey(MainWindow window,Key key){
        var source=(SourceEditor)FieldOf(window,"source");
        KeyEventArgs args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(source),0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};
        source.TextArea.RaiseEvent(args);
    }
    static string RunFunctionKey(MainWindow window,string body,Key key){
        ResetDocument(window,body);
        var source=(SourceEditor)FieldOf(window,"source");
        RaiseFunctionKey(window,key);Pump(350);
        return source.Text;
    }
    static string RunToolbarControl(MainWindow window,string body,string field){
        ResetDocument(window,body);
        var source=(SourceEditor)FieldOf(window,"source");
        var control=(FrameworkElement)FieldOf(window,field);
        var button=control as Button;var item=control as MenuItem;
        if(button!=null)button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        else if(item!=null)item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump(350);
        return source.Text;
    }
    // B3: every default F-key runs the control the toolbar runs, not a second copy of the command.
    static void VerifyFunctionKeys(ref bool all,StringBuilder report){
        Progress("c1-start");
        MainWindow window=NewWindow();
        try{
            const string body="# 첫 제목\n\n본문 한 줄\n\n본문 둘째 줄\n";
            string[] labels={"F3 heading","F4 list","F5 task","F6 quote","F7 inline code","F8 code block"};
            Key[] keys={Key.F3,Key.F4,Key.F5,Key.F6,Key.F7,Key.F8};
            string[] controls={"headingButton","listButton","taskButton","quoteButton","codeButton","blockButton"};
            for(int i=0;i<keys.Length;i++){
                Progress("c1-"+labels[i]);
                string keyText=RunFunctionKey(window,body,keys[i]);
                string toolText=RunToolbarControl(window,body,controls[i]);
                Check(ref all,report,"c1 "+labels[i]+" runs the toolbar command",keyText==toolText&&!String.Equals(keyText,body,StringComparison.Ordinal),"key='"+OneLine(keyText)+"' toolbar='"+OneLine(toolText)+"'");
            }
            ResetDocument(window,body);
            var outline=(TreeView)FieldOf(window,"outline");
            bool beforeOutline=outline.Visibility==Visibility.Visible;
            RaiseFunctionKey(window,Key.F10);Pump(350);
            bool afterOutline=outline.Visibility==Visibility.Visible;
            Check(ref all,report,"c1 F10 toggles the outline like its toolbar button",afterOutline!=beforeOutline,"before="+beforeOutline+" after="+afterOutline);
            // Ctrl+B / Ctrl+I: the shortcut path needs a real modifier state, so the same command
            // it calls is driven here (the key handler is the one tested above).
            ResetDocument(window,body);
            var source=(SourceEditor)FieldOf(window,"source");
            source.Select(source.Document.GetLineByNumber(3).Offset,2);
            Call(window,"ToggleFormat","bold");Pump(300);
            bool bold=source.Text.Contains("**본문**");
            ResetDocument(window,body);
            source=(SourceEditor)FieldOf(window,"source");
            source.Select(source.Document.GetLineByNumber(3).Offset,2);
            Call(window,"ToggleFormat","italic");Pump(300);
            Check(ref all,report,"c2 bold and italic commands still wrap the selection",bold&&source.Text.Contains("*본문*"),"bold="+bold+" text="+OneLine(source.Text));
        }catch(Exception error){all=false;report.Append("FAIL c function keys exception: ").Append(OneLine(error.ToString())).Append('\n');}
        finally{Discard(window);window.Close();}
    }
    /* 2026-10-31 bug hunt 1: a keyboard command that opens the find bar or moves the focus used to
       lose a live IME composition (AvalonEdit cancels it from LostKeyboardFocus). Ctrl+F and an F-key
       (F5 = 글자 크기 선택, which focuses the size box) are raised through the window's own
       PreviewKeyDown handler while a counted IME result is pending. The commit request must name the
       body caret, and the result that arrives after the focus left must land in the document instead
       of following the new focus. Keyboard.Modifiers cannot be set from a probe, so the handler's
       ShortcutModifiersProbe seam supplies Ctrl; KeyboardCommandFocus stands in for the body's
       keyboard focus (the same seam pattern as FunctionKeyFocus). */
    static void VerifyKeyboardCompositionCommit(ref bool all,StringBuilder report){
        Progress("kbd-ime-start");
        MainWindow window=NewWindow();
        string[] oldKeys=null;
        Func<bool> oldFocus=null;
        ModifierKeys? oldModifiers=null;
        try{
            window.SetText("가나");window.SetMode(true);Pump(300);
            var source=(SourceEditor)FieldOf(window,"source");
            source.Select(source.Document.TextLength,0);Pump(120);
            oldFocus=MainWindow.KeyboardCommandFocus;MainWindow.KeyboardCommandFocus=delegate{return true;};
            /* k1: Ctrl+F through the window's key handler; the find field takes the keyboard next. */
            source.SimulateImeCommit(1);
            int requests=source.CompositionRequests,caret=source.CaretIndex;
            oldModifiers=MainWindow.ShortcutModifiersProbe;MainWindow.ShortcutModifiersProbe=ModifierKeys.Control;
            RaiseKey(window,window,Key.F);MainWindow.ShortcutModifiersProbe=oldModifiers;
            bool asked=source.CompositionRequests>requests&&source.CompositionRequestCaret==caret;
            var host=FieldValue<Border>(window,"searchHost");
            bool barOpen=host!=null&&host.Visibility==Visibility.Visible;
            Keyboard.ClearFocus();
            bool adopted=source.AcceptLateImeCommit("다");Pump(150);
            Check(ref all,report,"k1 Ctrl+F commits the composing syllable before the find bar takes focus",
                asked&&adopted&&barOpen&&String.Equals(source.Text,"가나다",StringComparison.Ordinal),
                "asked="+asked+" adopted="+adopted+" bar="+barOpen+" text=["+OneLine(source.Text)+"]");
            Call(window,"CloseSearch");Pump(150);
            /* k2: an F-key that moves the focus (F5 = 글자 크기 선택) through the same handler. */
            oldKeys=(string[])typeof(MainWindow).GetField("functionKeys",Members).GetValue(null);
            string[] keys=new string[12];for(int i=0;i<keys.Length;i++)keys[i]="none";keys[4]="size";
            typeof(MainWindow).GetField("functionKeys",Members).SetValue(null,keys);
            Keyboard.ClearFocus();
            source.SimulateImeCommit(1);
            requests=source.CompositionRequests;caret=source.CaretIndex;
            RaiseFunctionKey(window,Key.F5);
            bool fAsked=source.CompositionRequests>requests&&source.CompositionRequestCaret==caret;
            Keyboard.ClearFocus();
            bool fAdopted=source.AcceptLateImeCommit("라");Pump(150);
            Check(ref all,report,"k2 an F-key that moves the focus commits the composing syllable first",
                fAsked&&fAdopted&&String.Equals(source.Text,"가나다라",StringComparison.Ordinal),
                "asked="+fAsked+" adopted="+fAdopted+" text=["+OneLine(source.Text)+"]");
        }catch(Exception error){
            all=false;report.Append("FAIL k keyboard composition exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            MainWindow.KeyboardCommandFocus=oldFocus;
            MainWindow.ShortcutModifiersProbe=oldModifiers;
            if(oldKeys!=null){try{typeof(MainWindow).GetField("functionKeys",Members).SetValue(null,oldKeys);}catch(Exception){}}
            Discard(window);window.Close();
        }
    }
    /* 2026-10-31 bug hunt 4: the status bar showed .NET's web name for an ANSI document
       (KS_C_5601-1987) while the save dialog names the same encoding ANSI (CP949). The bar now uses
       EncodingCaption - the name the save dialog's keep-current entry and its fixed list carry. */
    static void VerifyStatusEncodingName(ref bool all,StringBuilder report){
        Progress("encoding-name-start");
        MainWindow window=NewWindow();
        object oldEncoding=null;
        try{
            oldEncoding=FieldOf(window,"documentEncoding");
            // The typed overload keeps the lookup exact: a same-named helper added later must not
            // turn the call into an ambiguous match.
            var caption=typeof(MainWindow).GetMethod("EncodingCaption",Members,null,new Type[]{typeof(System.Text.Encoding)},null);
            var status=FieldValue<TextBlock>(window,"status");
            int[] codePages=new int[]{949,932,936};
            for(int i=0;i<codePages.Length;i++){
                var encoding=Encoding.GetEncoding(codePages[i]);
                FieldSet(window,"documentEncoding",encoding);
                var updateTitle=typeof(MainWindow).GetMethod("UpdateTitle",Members|BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null);if(updateTitle!=null)updateTitle.Invoke(window,null);
                string text=status==null?"":status.Text;
                string expected=caption==null?"":(string)caption.Invoke(null,new object[]{encoding});
                bool named=!String.IsNullOrEmpty(expected)&&text.IndexOf(expected,StringComparison.Ordinal)>=0;
                bool webName=text.IndexOf("KS_C_5601",StringComparison.OrdinalIgnoreCase)>=0||text.IndexOf("SHIFT_JIS",StringComparison.OrdinalIgnoreCase)>=0||text.IndexOf("GB2312",StringComparison.OrdinalIgnoreCase)>=0;
                Check(ref all,report,"k3 status bar names CP"+codePages[i]+" like the save dialog",named&&!webName,
                    "expected=["+expected+"] webName="+webName+" status=["+OneLine(text)+"]");
            }
        }catch(Exception error){
            all=false;report.Append("FAIL k encoding name exception: ").Append(OneLine(error.ToString())).Append('\n');
        }finally{
            if(oldEncoding!=null)FieldSet(window,"documentEncoding",oldEncoding);
            Discard(window);window.Close();
        }
    }
    // B1/B2/B3 (2026-10-31): a space typed on a bold run's closing edge leaves the pair outside in
    // the Basic view and on the Live view's raw caret line; the style toggle at that spot ends the
    // run without a second pair or an undo step; and every block command refuses a table row while
    // inline commands keep working inside a cell.
    static void VerifyCloseSpaceAndTableCommands(ref bool all,StringBuilder report){
        Progress("g-close-space-start");
        MainWindow window=NewWindow();
        bool oldHide=(bool)typeof(MainWindow).GetField("hideMarkers",Members).GetValue(null);
        try{
            var source=(SourceEditor)FieldOf(window,"source");
            var bold=(Button)FieldOf(window,"boldButton");
            const string table="| h1 | h2 |\n| --- | --- |\n| a | b |\n";
            // (a) Basic: x, B on, a, Space, b, Space, B off, c  =>  x**a b** c
            ApplyHideMarkers(window,true);
            window.SetText("x");window.SetMode(true);Pump(300);
            ClearTyping(window);
            source.Select(1,0);Pump(100);
            Click(bold);Pump(120);
            TypeText(source,"a");TypeText(source," ");TypeText(source,"b");TypeText(source," ");
            object version=source.Document.Version;int caret=source.CaretIndex;
            Click(bold);Pump(250);
            bool off=!Object.Equals(bold.Tag,"Active");
            bool untouched=Object.ReferenceEquals(version,source.Document.Version)&&source.CaretIndex==caret;
            TypeText(source,"c");
            Check(ref all,report,"g1 basic closing-edge space ends bold at the toggle",source.Text=="x**a b** c"&&off&&untouched,
                "text=["+OneLine(source.Text)+"] off="+off+" untouched="+untouched);
            // (b) Basic, no second toggle: the next character takes the space back inside.
            ApplyHideMarkers(window,true);
            window.SetText("x");window.SetMode(true);Pump(250);
            ClearTyping(window);source.Select(1,0);Pump(100);
            Click(bold);Pump(120);
            TypeText(source,"a");TypeText(source," ");TypeText(source,"b");TypeText(source," ");TypeText(source,"c");
            Check(ref all,report,"g2 basic closing-edge space is taken back by the next character",source.Text=="x**a b c**","text=["+OneLine(source.Text)+"]");
            // (b2) Same with text after the caret, and with the italic toggle.
            ApplyHideMarkers(window,true);
            window.SetText("x tail");window.SetMode(true);Pump(250);
            ClearTyping(window);source.Select(1,0);Pump(100);
            Click(bold);Pump(120);
            TypeText(source,"a");TypeText(source," ");TypeText(source,"b");TypeText(source," ");TypeText(source,"c");
            Check(ref all,report,"g2b basic closing-edge space with text after the caret",source.Text=="x**a b c** tail","text=["+OneLine(source.Text)+"]");
            ApplyHideMarkers(window,true);
            window.SetText("x");window.SetMode(true);Pump(250);
            ClearTyping(window);source.Select(1,0);Pump(100);
            Call(window,"ToggleFormat","italic");Pump(120);
            TypeText(source,"a");TypeText(source," ");TypeText(source,"b");TypeText(source," ");TypeText(source,"c");
            Check(ref all,report,"g2c basic italic closing-edge space is taken back by the next character",source.Text=="x*a b c*","text=["+OneLine(source.Text)+"]");
            // (c) Live: B on, a, Space, b
            ApplyHideMarkers(window,false);
            window.SetText("");window.SetMode(true);Pump(250);
            ClearTyping(window);source.Select(0,0);Pump(100);
            Click(bold);Pump(120);
            TypeText(source,"a");TypeText(source," ");TypeText(source,"b");
            Check(ref all,report,"g3 live closing-edge space is taken back on the raw line",source.Text=="**a b**","text=["+OneLine(source.Text)+"]");
            // (d) Live: B on, a, Space, B off, c
            ApplyHideMarkers(window,false);
            window.SetText("");window.SetMode(true);Pump(250);
            ClearTyping(window);source.Select(0,0);Pump(100);
            Click(bold);Pump(120);
            TypeText(source,"a");TypeText(source," ");
            object liveVersion=source.Document.Version;
            Click(bold);Pump(250);
            bool liveOff=!Object.Equals(bold.Tag,"Active");
            bool liveUntouched=Object.ReferenceEquals(liveVersion,source.Document.Version)&&source.Text=="**a** ";
            TypeText(source,"c");
            Check(ref all,report,"g4 live toggle at the closing edge closes bold without an edit",source.Text=="**a** c"&&liveOff&&liveUntouched,
                "text=["+OneLine(source.Text)+"] off="+liveOff+" untouched="+liveUntouched);
            // (e) Every block command is a no-op with the caret in a header, delimiter or body cell.
            ApplyHideMarkers(window,true);
            string[] commands={"h0","h1","h2","h3","h4","h5","h6","bullet","number","task","quote","rule"};
            string[] places={"header","delimiter","body"};
            for(int place=0;place<places.Length;place++){
                window.SetText(table);window.SetMode(true);Pump(250);
                var placeLine=source.Document.GetLineByNumber(place==0?1:place==1?2:3);
                int at=placeLine.Offset+(place==0?3:2);
                bool placeOk=true;string placeDetail="";
                for(int c=0;c<commands.Length;c++){
                    source.Select(at,0);Call(window,"UpdateTools");Pump(30);
                    string before=source.Document.Text;
                    RunBlockCommand(window,commands[c]);Pump(120);
                    if(source.Document.Text!=before){
                        placeOk=false;
                        placeDetail+=" "+commands[c]+"=["+OneLine(source.Document.Text)+"]";
                        window.SetText(table);window.SetMode(true);Pump(200);
                        source.Select(at,0);Call(window,"UpdateTools");Pump(30);
                    }
                }
                Check(ref all,report,"g5 block commands are no-ops in a "+places[place]+" cell",placeOk,placeOk?"":placeDetail.Trim());
            }
            // The default F-key table reaches the same command method; F4 (list) must change nothing.
            window.SetText(table);window.SetMode(true);Pump(250);
            source.Select(source.Document.GetLineByNumber(1).Offset+3,0);Call(window,"UpdateTools");Pump(60);
            string beforeKey=source.Document.Text;
            RaiseFunctionKey(window,Key.F4);Pump(250);
            Check(ref all,report,"g5b the F-key list command is a no-op inside a table row",source.Document.Text==beforeKey,
                "text=["+OneLine(source.Document.Text)+"]");
            // The same row must also show the block buttons disabled (the code button stays live).
            window.SetText(table);window.SetMode(true);Pump(250);
            source.Select(source.Document.GetLineByNumber(1).Offset+3,0);Call(window,"UpdateTools");Pump(120);
            var listTool=(Button)FieldOf(window,"listButton");var taskTool=(Button)FieldOf(window,"taskButton");
            var quoteTool=(Button)FieldOf(window,"quoteButton");var headingTool=(Button)FieldOf(window,"headingButton");
            var ruleTool=(Button)FieldOf(window,"ruleButton");var blockTool=(Button)FieldOf(window,"blockButton");
            // 11-01 user (HANDOFF): the code block button is off inside a table too, like the other five.
            Check(ref all,report,"g6 block buttons read disabled inside a table cell",
                !listTool.IsEnabled&&!taskTool.IsEnabled&&!quoteTool.IsEnabled&&!headingTool.IsEnabled&&!ruleTool.IsEnabled&&!blockTool.IsEnabled,
                "list="+listTool.IsEnabled+" task="+taskTool.IsEnabled+" quote="+quoteTool.IsEnabled+" heading="+headingTool.IsEnabled+" rule="+ruleTool.IsEnabled+" code="+blockTool.IsEnabled);
            source.Select(source.Document.TextLength,0);Call(window,"UpdateTools");Pump(120);
            Check(ref all,report,"g6 block buttons return outside the table",listTool.IsEnabled&&taskTool.IsEnabled&&quoteTool.IsEnabled&&headingTool.IsEnabled&&ruleTool.IsEnabled&&blockTool.IsEnabled,
                "list="+listTool.IsEnabled+" quote="+quoteTool.IsEnabled+" code="+blockTool.IsEnabled);
            // (f) 코드 with no selection in a cell arms inline code instead of writing an empty
            // pair; the first typed character writes "`x`" inside the cell, never a fence.
            foreach(bool header in new[]{true,false}){
                window.SetText(table);window.SetMode(true);Pump(250);
                var cellLine=source.Document.GetLineByNumber(header?1:3);
                int at=cellLine.Offset+(header?3:2);
                source.Select(at,0);ClearTyping(window);Pump(80);
                Click(blockTool);Pump(250);
                bool armed=source.Document.Text==table&&source.CaretIndex==at&&source.Document.LineCount==4;
                TypeText(source,"x");
                string expected=table.Substring(0,at)+"`x`"+table.Substring(at);
                bool pair=source.Document.Text==expected&&source.CaretIndex==at+2&&source.Document.LineCount==4;
                Check(ref all,report,"g7 코드 in a "+(header?"header":"body")+" cell arms inline code and the first input writes the pair",armed&&pair,
                    "armed="+armed+" text=["+OneLine(source.Document.Text)+"] caret="+source.CaretIndex+"/"+(at+2));
            }
            // g7b (11-08 VM, real clicks): in an empty cell ("|  |") the inline code / bold button with no selection,
            // then the first typed character, must write the pair there ("| `x` |"); it wrote the bare character.
            foreach(string kind in new[]{"code","bold"})foreach(int click in new[]{7,6}){
                const string emptyRow="| h1 | h2 |\n| --- | --- |\n| a |  |\n";
                window.SetText(emptyRow);window.SetMode(true);Pump(250);
                var rowLine=source.Document.GetLineByNumber(3);
                source.Select(rowLine.Offset+click,0);ClearTyping(window);Pump(80);
                int caret0=source.CaretIndex;
                Call(window,"ToggleFormat",kind);Pump(250);
                int caret1=source.CaretIndex;
                TypeText(source,"x");Pump(80);
                string mark=kind=="code"?"`":"**";
                string want="| h1 | h2 |\n| --- | --- |\n| a | "+mark+"x"+mark+" |\n";
                Check(ref all,report,"g7b "+kind+" with no selection in an empty cell (caret "+click+") writes the pair with the first input",source.Document.Text==want,
                    "caret="+caret0+"->"+caret1+" text=["+OneLine(source.Document.Text)+"]");
            }
            // Inline bold still wraps a cell's own text and keeps the row shape.
            window.SetText(table);window.SetMode(true);Pump(250);
            var boldLine=source.Document.GetLineByNumber(1);
            source.Select(boldLine.Offset+2,2);Pump(80);
            Call(window,"ToggleFormat","bold");Pump(250);
            Check(ref all,report,"g8 inline bold still wraps text inside a table cell",source.Document.Text=="| **h1** | h2 |\n| --- | --- |\n| a | b |\n","text=["+OneLine(source.Document.Text)+"]");
        }catch(Exception error){all=false;report.Append("FAIL g close-space/table exception: ").Append(OneLine(error.ToString())).Append('\n');}
        finally{typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,oldHide);Discard(window);window.Close();}
    }
    static void Click(Button button){if(button!=null)button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));}
    static void TypeText(SourceEditor source,string text){foreach(char ch in text)source.TextArea.PerformTextInput(ch.ToString());Pump(60);}
    static void ClearTyping(MainWindow window){Call(window,"ClearTypingStyle");Call(window,"UpdateTools");Pump(60);}
    static void ApplyHideMarkers(MainWindow window,bool hide){
        typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,hide);
        Call(window,"UpdateLivePreview");Pump(200);
    }
    // The same commands the toolbar buttons and the F-key table run, called directly so a disabled
    // button cannot make the no-op check vacuous.
    static void RunBlockCommand(MainWindow window,string id){
        if(id=="bullet"){Call(window,"ToggleListStyle",false);return;}
        if(id=="number"){Call(window,"ToggleListStyle",true);return;}
        if(id=="task"){Call(window,"ToggleTaskList");return;}
        if(id=="quote"){Call(window,"Quote");return;}
        if(id=="rule"){Call(window,"InsertMarkdownBlock","---");return;}
        if(id.Length==2&&id[0]=='h'){Call(window,"SetHeading",id[1]-'0');return;}
    }
    static double MeasureSizeText(TextBox field,string text,double size){
        var face=new Typeface(field.FontFamily,field.FontStyle,field.FontWeight,field.FontStretch);
        var formatted=new FormattedText(text,System.Globalization.CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,size,Brushes.Black,1.0);
        return formatted.WidthIncludingTrailingWhitespace;
    }
    // D1 (size box): a heading's scaled size such as "20.8 px" used to be cut to "20.8 p" by the
    // box's caption slot. The value stays exact; the caption type shrinks only when it must.
    static void VerifySizeCaptionFit(ref bool all,StringBuilder report){
        Progress("d1-start");
        MainWindow window=NewWindow();
        try{
            window.SetText("# 제목 하나\n\n## 제목 둘\n\n본문 문단입니다.\n");
            window.SetMode(true);Pump(400);
            var source=(SourceEditor)FieldOf(window,"source");
            source.Select(source.Document.GetLineByNumber(3).Offset+3,0);
            Call(window,"RefreshSizeBox");Pump(300);
            var sizes=(ComboBox)FieldOf(window,"sizes");
            sizes.ApplyTemplate();
            var field=sizes.Template.FindName("PART_EditableTextBox",sizes) as TextBox;
            string shown=sizes.Text;
            double available=field==null?0:field.ActualWidth-field.Padding.Left-field.Padding.Right;
            double full=field==null?0:MeasureSizeText(field,shown,13);
            double fitted=field==null?0:MeasureSizeText(field,shown,field.FontSize);
            bool exact=shown!=null&&shown.EndsWith(" px",StringComparison.Ordinal)&&field!=null&&field.Text==shown;
            Check(ref all,report,"d1 scaled size caption keeps its value and fits the box",field!=null&&exact&&full>available&&fitted<=available+0.51,"text="+shown+" font="+(field==null?0:field.FontSize)+" width="+fitted.ToString("0.00")+" full="+full.ToString("0.00")+" available="+available.ToString("0.0"));
            Call(window,"ToolbarSizeCaption","48 px");Call(window,"FitSizeCaption");Pump(200);
            bool whole=field!=null&&Math.Abs(field.FontSize-13)<.01&&MeasureSizeText(field,"48 px",field.FontSize)<=available+0.51;
            Check(ref all,report,"d1 whole pixel caption keeps the label size",whole,"font="+(field==null?0:field.FontSize)+" text="+sizes.Text);
        }finally{Discard(window);window.Close();}
    }
    // E1: an undo/redo restores a caret that can be a screenful away from the scrolled view.
    static void VerifyUndoCaretScroll(ref bool all,StringBuilder report){
        Progress("e1-start");
        MainWindow window=NewWindow();
        try{
            var builder=new StringBuilder();
            for(int i=1;i<=240;i++)builder.Append("line ").Append(i).Append('\n');
            string body=builder.ToString();
            window.SetText(body);window.SetMode(true);Pump(400);
            var source=(SourceEditor)FieldOf(window,"source");
            var line=source.Document.GetLineByNumber(180);
            source.Select(line.Offset+2,0);
            source.TextArea.PerformTextInput("X");Pump(300);
            string typed=source.Text;
            source.ScrollToVerticalOffset(0);Pump(300);window.UpdateLayout();Pump(100);
            bool atTop=source.VerticalOffset<1;
            Call(window,"RestoreEditorHistory",false);Pump(400);window.UpdateLayout();Pump(100);
            Rect caret=source.TextArea.Caret.CalculateCaretRectangle();
            bool visible=!caret.IsEmpty&&caret.Top>=source.VerticalOffset-1&&caret.Bottom<=source.VerticalOffset+source.ViewportHeight+1;
            Check(ref all,report,"e1 undo scrolls the restored caret into view",atTop&&source.Text!=typed&&visible,"atTop="+atTop+" caretLine="+source.TextArea.Caret.Line+" caret="+caret+" offset="+source.VerticalOffset.ToString("0.0")+" viewport="+source.ViewportHeight.ToString("0.0"));
            source.ScrollToVerticalOffset(0);Pump(300);window.UpdateLayout();Pump(100);
            Call(window,"RestoreEditorHistory",true);Pump(400);window.UpdateLayout();Pump(100);
            Rect redoCaret=source.TextArea.Caret.CalculateCaretRectangle();
            bool redoVisible=!redoCaret.IsEmpty&&redoCaret.Top>=source.VerticalOffset-1&&redoCaret.Bottom<=source.VerticalOffset+source.ViewportHeight+1;
            Check(ref all,report,"e1 redo scrolls the restored caret into view",source.Text==typed&&redoVisible,"caretLine="+source.TextArea.Caret.Line+" caret="+redoCaret+" offset="+source.VerticalOffset.ToString("0.0")+" viewport="+source.ViewportHeight.ToString("0.0"));
        }finally{Discard(window);window.Close();}
    }
    /* 11-06 (user video 193550): typing at the end of a window-sized document left the last row cut
       off under the sliding 24 px end margin (ApplySourceInset), and the view shook on fast input /
       arrow keys. The fix verifies the caret row after the frame's layout and scrolls the missing
       pixels (EnsureCaretRowClear, Editor.cs:361-406), queued from the caret's PositionChanged
       (Editor.cs:139). This types at the document end the way the user did and reads the caret's own
       rectangle against the box TextView actually shows (its own two numbers).
       Depends on: EnsureCaretRowClear and the QueueCaretClearance post from Caret.PositionChanged.
       A revert leaves the row below the visible bottom by the margin step (> 1 px) and this check
       fails; removing either method fails it through the missing member. The clip is produced by the
       product's own layout order, so it is the end state that is asserted, not a forced scroll. */
    static void VerifyCaretRowClear(ref bool all,StringBuilder report){
        Progress("bottom-row-start");
        MainWindow window=NewWindow();
        try{
            var builder=new StringBuilder();
            for(int i=1;i<=60;i++)builder.Append("줄 ").Append(i).Append('\n');
            window.SetText(builder.ToString());window.SetMode(true);Pump(500);
            var source=(SourceEditor)FieldOf(window,"source");
            IntPtr handle=IntPtr.Zero;try{handle=new WindowInteropHelper(window).Handle;}catch(Exception){}
            bool focused=source.TextArea.IsKeyboardFocusWithin;
            for(int attempt=0;attempt<12&&!focused;attempt++){
                window.Activate();if(attempt==4&&handle!=IntPtr.Zero)Win32.SetForegroundWindow(handle);
                source.Focus();Keyboard.Focus(source.TextArea);Pump(250);
                focused=source.TextArea.IsKeyboardFocusWithin;
            }
            source.Select(source.Document.TextLength,0);Pump(400);window.UpdateLayout();Pump(200);
            /* Type at the end the way the user does: the extent grows and the end margin slides in
               while the frame's own follow-up scroll may land before the new margin is laid out. */
            source.TextArea.PerformTextInput("\n");Pump(700);window.UpdateLayout();Pump(400);
            source.TextArea.PerformTextInput("끝");Pump(700);window.UpdateLayout();Pump(400);
            var view=source.TextArea.TextView;
            Rect caret;
            try{caret=source.TextArea.Caret.CalculateCaretRectangle();}catch(Exception){caret=Rect.Empty;}
            double offset=view.ScrollOffset.Y,visible=view.ActualHeight,extent=source.ExtentHeight;
            bool measured=!caret.IsEmpty&&!Double.IsNaN(caret.Bottom)&&!Double.IsInfinity(caret.Bottom);
            bool inside=measured&&caret.Bottom<=offset+visible+1&&caret.Top>=offset-1;
            bool helper=typeof(MainWindow).GetMethod("EnsureCaretRowClear",BindingFlags.Static|BindingFlags.NonPublic)!=null
                &&typeof(MainWindow).GetMethod("QueueCaretClearance",BindingFlags.Instance|BindingFlags.NonPublic)!=null;
            bool atEnd=source.TextArea.Caret.Line==source.Document.LineCount&&extent>visible+1&&offset>0;
            Check(ref all,report,"11-06 typing at the document end leaves the last row inside the text view",
                focused&&helper&&atEnd&&inside,
                "focus="+focused+" helper="+helper+" end="+atEnd
                    +" caret="+caret.Top.ToString("0.0")+".."+caret.Bottom.ToString("0.0")
                    +" view="+offset.ToString("0.0")+".."+(offset+visible).ToString("0.0")
                    +" extent="+extent.ToString("0.0")+" line="+source.TextArea.Caret.Line+"/"+source.Document.LineCount);
        }catch(Exception error){all=false;report.Append("FAIL bottom row clear exception: ").Append(OneLine(error.ToString())).Append('\n');}
        finally{Discard(window);window.Close();}
    }
    /* X25 (user real-input run 2026-10-30): dark + 450 px » crash, Esc on the table grid and the link prompt,
       the dark table-grid surface, and submenus of the » list staying inside the window. The » submenu check
       needs the popups placed against real screen coordinates, so this window sits on the hidden desktop
       instead of the usual -20000 offset (nothing is painted on the user's desktop). */
    static void VerifyOverflowAndDialogs(ref bool all,StringBuilder report){
        Progress("f-start");
        MainWindow window=NewWindow();
        window.WindowStartupLocation=WindowStartupLocation.Manual;
        window.Left=120;window.Top=120;window.Width=450;window.Height=620;
        window.UpdateLayout();Pump(400);
        try{
            window.SetText("# 첫 제목\n\n본문 한 줄\n");
            window.SetMode(true);Pump(700);
            Call(window,"SetDark",true);Pump(700);
            window.UpdateLayout();Pump(300);
            window.ToolbarRefreshOverflow();Pump(500);
            window.UpdateLayout();Pump(200);
            var source=(SourceEditor)FieldOf(window,"source");
            var top=FieldOf(window,"topOverflow") as Button;
            var menu=FieldValue<ContextMenu>(window,"topOverflowMenu");
            bool shown=(bool)Call(window,"ToolbarOverflowShown",false);
            string error="none",opened="";
            try{
                Call(window,"OpenOverflowMenu",top,menu);
                /* The click that opened the menu also runs a layout pass of the main tree, and the » list has
                   realised its items by then; that is the pass the divider used to throw in. Rebuild it without
                   a resize (a resize would restore the menu items from the » list first). */
                var menuBar=FieldValue<Menu>(window,"topMenu");
                var topPanel=menuBar==null?null:menuBar.Parent as FrameworkElement;
                if(topPanel!=null){topPanel.InvalidateMeasure();window.UpdateLayout();}
                Pump(300);
                if(topPanel!=null){topPanel.InvalidateMeasure();window.UpdateLayout();}
                Pump(200);
                opened="open="+(menu!=null&&menu.IsOpen);
            }
            catch(Exception failure){error=failure.GetType().Name+": "+OneLine(failure.Message);}
            Check(ref all,report,"f1 dark + 450 px » menu opens without an exception",shown&&top!=null&&menu!=null&&menu.IsOpen&&error=="none",
                "shown="+shown+" "+opened+" items="+(menu==null?-1:menu.Items.Count)+" error="+error);
            if(menu!=null&&menu.IsOpen){
                MenuItem first=null;
                foreach(object entry in menu.Items){var item=entry as MenuItem;if(item!=null&&item.Items.Count>0){first=item;break;}}
                string bounds="no submenu item in the » list";
                if(first!=null){
                    first.IsSubmenuOpen=true;Pump(900);window.UpdateLayout();Pump(300);
                    bounds=SubmenuBounds(window,first);
                }
                Check(ref all,report,"f5 » submenu unfolds to the right of its entry, to the other side only at the screen edge",bounds.Contains(" ok=True"),bounds);
                if(first!=null)first.IsSubmenuOpen=false;
                /* f1b: the invariant behind the crash. A width exists where the menu divider is still painted
                   while the first menu item already lives in the » list; this layout at 450 px keeps the file
                   menu in the bar one unit longer, so the move is made here exactly as ApplyTopOverflowVisibility
                   makes it, and the next main-tree layout pass must not throw. */
                var menuBar=FieldValue<Menu>(window,"topMenu");
                var topPanel=menuBar==null?null:menuBar.Parent as FrameworkElement;
                var fileItem=menuBar==null||menuBar.Items.Count==0?null:menuBar.Items[0] as MenuItem;
                string moved="no file menu";
                if(fileItem==null&&topPanel!=null&&menuBar!=null&&menuBar.Items.Count==0){
                    /* The dark menu skin widens the bar entries, so at 450 px the file menu can already live in
                       the » list; that is the state this check builds, so only the layout passes remain. */
                    try{
                        topPanel.InvalidateMeasure();window.UpdateLayout();Pump(250);
                        topPanel.InvalidateMeasure();window.UpdateLayout();
                        MenuItem listed=menu.Items.Count==0?null:menu.Items[0] as MenuItem;
                        if(listed!=null)moved="moved=True parent="+(listed.Parent==null?"null":listed.Parent.GetType().Name)+" bar=0 already-in-list size="+listed.ActualWidth.ToString("0")+"x"+listed.ActualHeight.ToString("0");
                    }catch(Exception failure){moved="moved-throw="+failure.GetType().Name+": "+OneLine(failure.Message);}
                    fileItem=menu.Items.Count==0?null:menu.Items[0] as MenuItem;
                }
                else if(fileItem!=null&&topPanel!=null){
                    try{
                        menuBar.Items.Remove(fileItem);
                        window.UpdateLayout();
                        menu.Items.Insert(0,fileItem);
                        window.UpdateLayout();
                        topPanel.InvalidateMeasure();window.UpdateLayout();
                        Pump(250);
                        topPanel.InvalidateMeasure();window.UpdateLayout();
                        moved="moved=True parent="+(fileItem.Parent==null?"null":fileItem.Parent.GetType().Name)+" bar="+menuBar.Items.Count+" size="+fileItem.ActualWidth.ToString("0")+"x"+fileItem.ActualHeight.ToString("0");
                    }catch(Exception failure){moved="moved-throw="+failure.GetType().Name+": "+OneLine(failure.Message);}
                    try{menu.Items.Remove(fileItem);if(menuBar.Items.IndexOf(fileItem)<0)menuBar.Items.Insert(0,fileItem);window.UpdateLayout();}catch(Exception){}
                }
                Check(ref all,report,"f1b a menu item moved into the » list does not break the divider pass",fileItem!=null&&moved.StartsWith("moved=",StringComparison.Ordinal)&&!moved.Contains("throw"),moved);
                menu.IsOpen=false;Pump(300);
            }
            Call(window,"OpenTablePicker");Pump(600);
            var picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            Check(ref all,report,"f2 table size grid opens",picker!=null&&picker.IsOpen,"open="+(picker!=null&&picker.IsOpen));
            if(picker!=null&&picker.IsOpen){
                RaiseKey(picker,window,Key.Escape);Pump(400);
                Check(ref all,report,"f2 Escape raised inside the grid closes it",!picker.IsOpen,"open="+picker.IsOpen);
            }
            Call(window,"OpenTablePicker");Pump(500);
            picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            bool reopened=picker!=null&&picker.IsOpen;
            RaiseKey(window,window,Key.Escape);Pump(400);
            Check(ref all,report,"f2 Escape in the body closes the grid and returns the caret",
                reopened&&picker!=null&&!picker.IsOpen&&source.TextArea.IsKeyboardFocusWithin,
                "reopened="+reopened+" open="+(picker!=null&&picker.IsOpen)+" focus="+source.TextArea.IsKeyboardFocusWithin);
            Call(window,"OpenTablePicker");Pump(500);
            picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            Color paper=((SolidColorBrush)window.Resources["Paper"]).Color;
            string surface=PickerSurface(picker);
            bool darkSurface=surface.Contains("bg="+ColorText(paper))&&surface.Contains("root="+ColorText(paper));
            Check(ref all,report,"f4 dark table grid uses the dark popup surface",reopened&&darkSurface,"paper="+ColorText(paper)+" "+surface);
            if(picker!=null)picker.IsOpen=false;Pump(200);
            string untouched=source.Text;
            bool linkSaw,linkRaised,linkForced;
            bool linkEsc=RunModalWithEscape(window,delegate{Call(window,"LiveLink");},out linkSaw,out linkRaised,out linkForced);
            Check(ref all,report,"f3 link prompt closes on Escape and the caret returns",
                linkEsc&&linkSaw&&linkRaised&&!linkForced&&source.Text==untouched&&source.TextArea.IsKeyboardFocusWithin,
                "esc="+linkEsc+" dialog="+linkSaw+" key="+linkRaised+" forced="+linkForced+" textChanged="+(source.Text!=untouched)+" focus="+source.TextArea.IsKeyboardFocusWithin);
            bool tableSaw,tableRaised,tableForced;
            bool tableEsc=RunModalWithEscape(window,delegate{Call(window,"ShowCustomTable");},out tableSaw,out tableRaised,out tableForced);
            Check(ref all,report,"f3 custom table prompt closes on Escape and inserts nothing",
                tableEsc&&tableSaw&&tableRaised&&!tableForced&&source.Text==untouched,
                "esc="+tableEsc+" dialog="+tableSaw+" key="+tableRaised+" forced="+tableForced+" textChanged="+(source.Text!=untouched));
            bool cancelSaw,cancelClicked,cancelForced;
            bool cancelWorks=RunModalWithCancel(window,delegate{Call(window,"ShowCustomTable");},out cancelSaw,out cancelClicked,out cancelForced);
            Check(ref all,report,"f3 custom table prompt has a cancel button that closes it",
                cancelWorks&&cancelSaw&&cancelClicked&&!cancelForced&&source.Text==untouched,
                "cancel="+cancelWorks+" dialog="+cancelSaw+" click="+cancelClicked+" forced="+cancelForced+" textChanged="+(source.Text!=untouched));
        }catch(Exception error){all=false;report.Append("FAIL f overflow/dialog exception: ").Append(OneLine(error.ToString())).Append('\n');}
        finally{Discard(window);window.Close();}
    }
    static void RaiseKey(UIElement target,Visual source,Key key){
        var presentation=PresentationSource.FromVisual(source);
        if(presentation==null)return;
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent});
    }
    /* Menu skin and » checks: one real Escape must close the table grid and keep it closed while the pointer still rests on
       the button; every menu takes the app's dark surfaces; a » submenu clicked at a narrow width must
       never run the entry under the pointer. */
    static void VerifyMenuSkinAndOverflow(ref bool all,StringBuilder report){
        MainWindow window=NewWindow();
        try{
            /* A popup's screen rectangle is meaningless while its window sits off the desktop, so this
               window lives on the hidden desktop at 20,20 (hidden-run never shows it to the user). */
            window.Left=20;window.Top=20;window.UpdateLayout();Pump(400);
            window.SetText("# 제목\n\n본문\n");window.SetMode(true);Pump(700);
            var source=(SourceEditor)FieldOf(window,"source");
            window.Activate();source.Focus();Keyboard.Focus(source.TextArea);Pump(500);
            var tableButton=FieldOf(window,"tableButton") as Button;
            IntPtr windowHandle=IntPtr.Zero;
            try{windowHandle=new WindowInteropHelper(window).Handle;}catch(Exception){}
            /* g1: the real key path is WM_KEYDOWN on the focused top-level window, not a synthesized event. */
            Call(window,"OpenTablePicker");Pump(900);
            var picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            bool pickerOpened=picker!=null&&picker.IsOpen;
            SendEscape(windowHandle);Pump(800);
            bool pickerClosed=pickerOpened&&picker!=null&&!picker.IsOpen;
            bool caretBack=source.TextArea.IsKeyboardFocusWithin;
            bool hoverReopened=false;
            if(tableButton!=null){
                // Closing the popup hands the mouse back; WPF raises MouseEnter on whatever is under it.
                tableButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseEnterEvent});
                Pump(700);
                picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
                hoverReopened=picker!=null&&picker.IsOpen;
                if(hoverReopened)picker.IsOpen=false;
                Pump(300);
            }
            Check(ref all,report,"g1 one real Escape closes the table grid, returns the caret and survives the hover",
                pickerOpened&&pickerClosed&&caretBack&&!hoverReopened,
                "opened="+pickerOpened+" closed="+pickerClosed+" caret="+caretBack+" hoverReopened="+hoverReopened);
            if(tableButton!=null){try{Keyboard.Focus(tableButton);}catch(Exception){}Pump(300);}
            Call(window,"OpenTablePicker");Pump(900);
            picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            bool buttonOpened=picker!=null&&picker.IsOpen;
            SendEscape(windowHandle);Pump(800);
            Check(ref all,report,"g1 one real Escape also closes the grid when the table button holds focus",
                buttonOpened&&picker!=null&&!picker.IsOpen&&source.TextArea.IsKeyboardFocusWithin,
                "opened="+buttonOpened+" open="+(picker!=null&&picker.IsOpen)+" caret="+source.TextArea.IsKeyboardFocusWithin);
            if(picker!=null&&picker.IsOpen)picker.IsOpen=false;Pump(400);
            /* g2: dark menus. The system menu templates paint their popup, entry, highlight, check and
               separator from static theme brushes, so the dark skin supplies its own (Ui.MenuSurface ...);
               light mode must stay the system chrome. */
            Call(window,"SetDark",true);Pump(600);
            var bar=FieldValue<Menu>(window,"topMenu");
            MenuItem barFirst=bar==null||bar.Items.Count==0?null:bar.Items[0] as MenuItem;
            bool barDark=false;string barState="no menu bar item";
            if(barFirst!=null&&barFirst.IsVisible&&barFirst.ActualWidth>0&&PresentationSource.FromVisual(barFirst)!=null){
                /* The theme switch alone must restyle the bar (Theme() ends in UpdateHeadingCaption); a
                   click then must keep it. Raising PreviewMouseDown runs the handlers a real click runs
                   (Window -> Menu -> item); mouse messages sent with SendMessage never reach the menu bar
                   on the hidden desktop (measured submenu=False with the window active). */
                Popup barPopup=null;
                try{barPopup=barFirst.Template==null?null:barFirst.Template.FindName("PART_Popup",barFirst) as Popup;}catch(Exception){}
                Border barBorder=barPopup==null?null:barPopup.Child as Border;
                MenuItem barEntry=barFirst.Items.Count==0?null:barFirst.Items[0] as MenuItem;
                bool themeDark=Object.ReferenceEquals(barFirst.Template,Ui.DarkTopMenuItemTemplate())
                    &&barBorder!=null&&SameBrush(barBorder.Background,Ui.MenuSurface)&&SameBrush(barBorder.BorderBrush,Ui.MenuLine)
                    &&barEntry!=null&&Object.ReferenceEquals(barEntry.Style,Ui.DarkSubMenuItemStyle())&&SameBrush(barEntry.Foreground,Ui.MenuInk);
                try{barFirst.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseDownEvent});}catch(Exception){}
                Pump(500);window.UpdateLayout();Pump(300);
                MenuItem barEntryAfter=barFirst.Items.Count==0?null:barFirst.Items[0] as MenuItem;
                bool clickDark=Object.ReferenceEquals(barFirst.Template,Ui.DarkTopMenuItemTemplate())
                    &&barEntryAfter!=null&&Object.ReferenceEquals(barEntryAfter.Style,Ui.DarkSubMenuItemStyle());
                barDark=themeDark&&clickDark;
                barState="theme="+themeDark+" click="+clickDark+" surface="+(barBorder==null?"none":BrushText(barBorder.Background)+"/"+BrushText(barBorder.BorderBrush))+" entryInk="+(barEntry==null?"none":BrushText(barEntry.Foreground));
                if(barFirst.IsSubmenuOpen)barFirst.IsSubmenuOpen=false;
                Pump(300);SendEscape(windowHandle);Pump(400);
            }
            Check(ref all,report,"g2 the menu bar opens the dark submenu surface with dark entries",barDark,barState);
            /* The narrow-row checks need real screen geometry: an off-screen window makes a popup's
               screen rect meaningless, so this window sits on the hidden desktop at 20,20. */
            window.Width=360;window.Height=620;window.UpdateLayout();Pump(400);
            window.ToolbarRefreshOverflow();Pump(700);window.UpdateLayout();Pump(300);
            var top=FieldOf(window,"topOverflow") as Button;
            var menu=FieldValue<ContextMenu>(window,"topOverflowMenu");
            bool overflowShown=(bool)Call(window,"ToolbarOverflowShown",false);
            bool ribbonShown=(bool)Call(window,"ToolbarOverflowShown",true);
            MenuItem first=null;
            bool topDark=false;string topState="the » menu did not open";
            if(menu!=null&&overflowShown){
                Call(window,"OpenOverflowMenu",top,menu);Pump(900);window.UpdateLayout();Pump(300);
                foreach(object entry in menu.Items){var item=entry as MenuItem;if(item!=null&&item.Items.Count>0){first=item;break;}}
                bool skin=Object.ReferenceEquals(menu.Template,Ui.DarkMenuTemplate());
                bool surface=SameBrush(MenuSurfaceBrush(menu),Ui.MenuSurface);
                bool entryDark=first!=null&&Object.ReferenceEquals(first.Style,Ui.DarkSubMenuItemStyle())&&SameBrush(first.Foreground,Ui.MenuInk);
                bool separator=false;
                foreach(object line in menu.Items){var sep=line as Separator;if(sep!=null){separator=Object.ReferenceEquals(sep.Style,Ui.DarkSeparatorStyle());break;}}
                topDark=menu.IsOpen&&skin&&surface&&entryDark&&separator;
                topState="open="+menu.IsOpen+" skin="+skin+" surface="+BrushText(MenuSurfaceBrush(menu))+" entryInk="+(first==null?"none":BrushText(first.Foreground))+" separator="+separator;
                menu.IsOpen=false;Pump(300);
            }
            Check(ref all,report,"g2 the » menu paints the app's dark surface, entries and separators",overflowShown&&topDark,topState);
            bool ribbonDark=false;string ribbonState="the ribbon » button is not shown";
            var ribbonMenu=FieldValue<ContextMenu>(window,"ribbonOverflowMenu");
            var ribbon=FieldOf(window,"ribbonOverflow") as Button;
            if(ribbonShown&&ribbonMenu!=null&&ribbon!=null){
                try{Call(window,"OpenOverflowMenu",ribbon,ribbonMenu);}catch(Exception){}
                Pump(900);window.UpdateLayout();Pump(300);
                bool skin=Object.ReferenceEquals(ribbonMenu.Template,Ui.DarkMenuTemplate());
                bool surface=SameBrush(MenuSurfaceBrush(ribbonMenu),Ui.MenuSurface);
                bool anyEntry=false;
                foreach(object entry in ribbonMenu.Items){var item=entry as MenuItem;if(item!=null&&Object.ReferenceEquals(item.Style,Ui.DarkSubMenuItemStyle())){anyEntry=true;break;}}
                ribbonDark=ribbonMenu.IsOpen&&skin&&surface&&anyEntry;
                ribbonState="open="+ribbonMenu.IsOpen+" skin="+skin+" surface="+BrushText(MenuSurfaceBrush(ribbonMenu))+" entry="+anyEntry;
                ribbonMenu.IsOpen=false;Pump(300);
            }
            Check(ref all,report,"g2 the ribbon » menu uses the same dark chrome",ribbonDark,ribbonState);
            /* Light keeps the system menu chrome the user knows (same popup, same entry colours). */
            Call(window,"SetDark",false);Pump(700);
            bool lightSystem=false;string lightState="the » menu did not open";
            if(menu!=null&&overflowShown){
                Call(window,"OpenOverflowMenu",top,menu);Pump(900);window.UpdateLayout();Pump(300);
                bool system=!Object.ReferenceEquals(menu.Template,Ui.DarkMenuTemplate());
                bool entryRestored=first==null||!Object.ReferenceEquals(first.Style,Ui.DarkSubMenuItemStyle());
                bool surfaceLight=!SameBrush(MenuSurfaceBrush(menu),Ui.MenuSurface);
                lightSystem=menu.IsOpen&&system&&entryRestored&&surfaceLight;
                lightState="open="+menu.IsOpen+" system="+system+" surface="+BrushText(MenuSurfaceBrush(menu))+" entryRestored="+entryRestored;
                menu.IsOpen=false;Pump(300);
            }
            if(barFirst!=null){
                // The bar item must also go back to the system chrome: same routed event, light theme.
                try{barFirst.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left){RoutedEvent=UIElement.PreviewMouseDownEvent});}catch(Exception){}
                Pump(400);
                bool barRestored=!Object.ReferenceEquals(barFirst.Template,Ui.DarkTopMenuItemTemplate());
                lightSystem=lightSystem&&barRestored;
                lightState+=" barRestored="+barRestored;
            }
            Check(ref all,report,"g2 light keeps the system menu chrome",lightSystem,lightState);
            /* g4: a theme only changes colours. The bar items, the submenu rows and the » hand-over point must
               measure the same in light (system Aero2 templates) and dark (our templates). */
            bool metricsOk=false;string metricsState="no menu bar";
            window.Width=900;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
            if(bar!=null&&bar.Items.Count>0){
                var lightWidths=new List<double>();var darkWidths=new List<double>();var lightSub=new List<double>();var darkSub=new List<double>();
                window.Width=900;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
                Call(window,"ApplyMenuBarSkin",bar);Pump(300);window.UpdateLayout();
                string lightBar=BarMetrics(window,bar,lightWidths,lightSub);
                window.Width=450;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
                int lightCount=bar.Items.Count;
                Call(window,"SetDark",true);Pump(600);
                window.Width=900;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
                Call(window,"ApplyMenuBarSkin",bar);Pump(300);window.UpdateLayout();
                string darkBar=BarMetrics(window,bar,darkWidths,darkSub);
                window.Width=450;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
                int darkCount=bar.Items.Count;
                bool same=lightWidths.Count>0&&lightWidths.Count==darkWidths.Count&&lightCount==darkCount&&lightSub.Count==darkSub.Count;
                if(same){for(int i=0;i<lightWidths.Count;i++)if(Math.Abs(lightWidths[i]-darkWidths[i])>0.5)same=false;for(int i=0;i<lightSub.Count;i++)if(Math.Abs(lightSub[i]-darkSub[i])>0.5)same=false;}
                metricsOk=same;
                metricsState="light "+lightBar+" at450="+lightCount+" | dark "+darkBar+" at450="+darkCount;
                Call(window,"SetDark",false);Pump(600);
                window.Width=360;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
            }
            Check(ref all,report,"g4 dark menu bar and submenu rows measure the same as light",metricsOk,metricsState);
            Call(window,"SetDark",true);Pump(600);
            /* g3: the click that opens a moved » parent must only unfold its submenu - the submenu used to be
               pushed back inside the window over the parent, so the same click's release ran 새로 만들기. */
            bool clickOk=false;string clickState="the » menu did not open";
            if(menu!=null&&overflowShown){
                Call(window,"OpenOverflowMenu",top,menu);Pump(900);window.UpdateLayout();Pump(300);
                MenuItem header=null;
                foreach(object entry in menu.Items){var item=entry as MenuItem;if(item!=null&&item.Items.Count>0){header=item;break;}}
                if(header!=null&&menu.IsOpen){
                    var popupSource=PresentationSource.FromVisual(menu) as HwndSource;
                    int windowsBefore=Application.Current.Windows.Count;int docsBefore=window.OpenDocumentCount;
                    string mouse="no hwnd";bool covers=false;
                    if(popupSource!=null){
                        Point screen=header.PointToScreen(new Point(header.ActualWidth/2,header.ActualHeight/2));
                        Point client=ToClient(popupSource.Handle,screen);
                        Win32.SendMessage(popupSource.Handle,0x0200,IntPtr.Zero,LParam(client));Pump(300);
                        Win32.SendMessage(popupSource.Handle,0x0201,(IntPtr)1,LParam(client));Pump(900);
                        Popup clickPopup=null;
                        try{clickPopup=header.Template==null?null:header.Template.FindName("PART_Popup",header) as Popup;}catch(Exception){}
                        var clickChild=clickPopup==null?null:clickPopup.Child as FrameworkElement;
                        if(clickChild!=null&&clickChild.ActualWidth>0&&PresentationSource.FromVisual(clickChild)!=null){
                            Point corner=clickChild.PointToScreen(new Point(0,0));
                            covers=new Rect(corner,clickChild.PointToScreen(new Point(clickChild.ActualWidth,clickChild.ActualHeight))).Contains(screen);
                        }
                        IntPtr capture=Win32.GetCapture();
                        Point up=ToClient(capture,screen);
                        Win32.SendMessage(capture,0x0202,IntPtr.Zero,LParam(up));Pump(1200);
                        mouse="click="+client+" capture="+capture.ToInt64();
                    }
                    bool sameDocs=window.OpenDocumentCount==docsBefore;
                    bool sameWindows=Application.Current.Windows.Count==windowsBefore;
                    bool submenuOpened=header.IsSubmenuOpen;
                    bool inkOk=SameBrush(header.Foreground,Ui.MenuInk);
                    clickOk=sameDocs&&sameWindows&&submenuOpened&&!covers&&inkOk;
                    clickState=mouse+" docs="+sameDocs+" windows="+sameWindows+" submenu="+submenuOpened+" coversClick="+covers+" ink="+BrushText(header.Foreground);
                    if(menu.IsOpen)menu.IsOpen=false;
                    header.IsSubmenuOpen=false;Pump(300);
                }
            }
            Check(ref all,report,"g3 a click on a moved » parent opens only its submenu",clickOk,clickState);
        }catch(Exception error){all=false;report.Append("FAIL g menu skin exception: ").Append(OneLine(error.ToString())).Append((char)10);}
        finally{Discard(window);window.Close();}
    }
    /* b56/b57 UI fixes that need no synthetic input: a view command keeps the selection, black is a
       colour, Escape closes the colour palette before it reaches the find bar, the table tool row stays
       inside a 360 px window, line spacing keeps the reading place when the caret line is off screen,
       Alt+Down reaches the », a bar item returning from the » list takes the bar skin again, and the
       table grid waits for real pointer movement before it opens under a still pointer. */
    static void VerifyFixRound(ref bool all,StringBuilder report){
        MainWindow window=NewWindow();
        try{
            window.Left=20;window.Top=20;window.Width=900;window.Height=620;window.UpdateLayout();Pump(300);
            var source=(SourceEditor)FieldOf(window,"source");
            /* h1: zoom / base font / colour re-render the text, which used to keep only the caret. */
            window.SetText("# 제목\n\n선택 영역 보존 검사 문단입니다.\n");window.SetMode(true);Pump(700);
            source=(SourceEditor)FieldOf(window,"source");
            int selectionAt=source.Text.IndexOf("보존",StringComparison.Ordinal);
            source.Select(selectionAt,2);Pump(250);
            int selectionBefore=source.SelectionLength;
            Call(window,"Zoom",new object[]{2.0});Pump(700);
            Check(ref all,report,"h1 zoom keeps the selection",selectionBefore==2&&source.SelectionLength==2&&source.SelectionStart==selectionAt,
                "before="+selectionBefore+" after="+source.SelectionLength+" start="+source.SelectionStart+" expected="+selectionAt);
            /* h2: #000000 is a real colour, not the "no colour" value. */
            window.SetText("# 제목\n\n검정 글자 검사\n");Pump(400);
            source=(SourceEditor)FieldOf(window,"source");
            int wordAt=source.Text.IndexOf("검정",StringComparison.Ordinal);
            source.Select(wordAt,2);Pump(250);
            Call(window,"ChooseTextColor",new object[]{Colors.Black});Pump(600);
            bool colorNone=Object.Equals(FieldOf(window,"lastColorNone"),true);
            string colored=source.Text;
            bool blackSpan=colored.IndexOf("<span style=\"color:#000000\">",StringComparison.Ordinal)>=0;
            Check(ref all,report,"h2 the black swatch writes a black span",!colorNone&&blackSpan,"lastColorNone="+colorNone+" span="+blackSpan+" text="+OneLine(colored));
            /* h3: Escape closes the palette; the find bar keeps its own Escape. */
            window.SetText("팔레트 검사\n");Pump(300);
            var arrow=FieldOf(window,"colorArrow") as Button;
            var popup=FieldOf(window,"colorPopup") as Popup;
            bool popupOpened=false,popupClosed=false,barOpen=false,popupWithBar=false,closedWithBar=false,barKept=false,barClosed=false;
            if(arrow!=null&&popup!=null){
                arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(400);
                popupOpened=popup.IsOpen;
                RaiseKey(window,window,Key.Escape);Pump(400);
                popupClosed=!popup.IsOpen;
            }
            Call(window,"ShowSearch");Pump(400);
            var search=(StackPanel)FieldOf(window,"search");
            barOpen=search!=null&&search.Visibility==Visibility.Visible;
            if(barOpen&&arrow!=null&&popup!=null){
                arrow.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(400);
                popupWithBar=popup.IsOpen;
                RaiseKey(window,window,Key.Escape);Pump(400);
                closedWithBar=!popup.IsOpen;
                barKept=search.Visibility==Visibility.Visible;
                RaiseKey(window,window,Key.Escape);Pump(400);
                barClosed=search.Visibility!=Visibility.Visible;
            }
            Check(ref all,report,"h3 Escape closes the palette and the find bar keeps its own Escape",
                popupOpened&&popupClosed&&barOpen&&popupWithBar&&closedWithBar&&barKept&&barClosed,
                "open="+popupOpened+" closed="+popupClosed+" bar="+barOpen+" popupWithBar="+popupWithBar+" closedWithBar="+closedWithBar+" barKept="+barKept+" barClosed="+barClosed);
            /* h4: every table tool button has to stay inside a 360 px window (the row was clipped). */
            window.Width=360;window.Height=620;window.UpdateLayout();Pump(400);
            window.SetText("| a | b |\n| --- | --- |\n| 1 | 2 |\n");Pump(500);
            source=(SourceEditor)FieldOf(window,"source");
            source.Select(source.Text.IndexOf("| 1",StringComparison.Ordinal)+2,0);Pump(700);
            Call(window,"UpdateTableTools");Pump(600);window.UpdateLayout();Pump(200);
            var toolsHost=FieldOf(window,"tableToolsHost") as Border;
            var actions=FieldOf(window,"tableActions") as IList;
            var rootPanel=FieldOf(window,"root") as Panel;
            bool rowShown=toolsHost!=null&&toolsHost.Visibility==Visibility.Visible;
            double limit=rootPanel==null?window.ActualWidth:rootPanel.ActualWidth;
            int inside=0,outside=0;
            if(actions!=null&&rootPanel!=null)foreach(object entry in actions){
                var button=entry as Button;if(button==null||!button.IsVisible)continue;
                Rect box=button.TransformToAncestor(rootPanel).TransformBounds(new Rect(0,0,button.ActualWidth,button.ActualHeight));
                if(box.Right<=limit+0.5&&box.Left>=-0.5)inside++;else outside++;
            }
            Check(ref all,report,"h4 all 11 table tool buttons stay inside a 360 px window",rowShown&&inside>=11&&outside==0,
                "shown="+rowShown+" inside="+inside+" outside="+outside+" width="+limit.ToString("0.#"));
            /* h5: line spacing keeps the reading place when the caret line is off screen. */
            window.Width=700;window.Height=520;window.UpdateLayout();Pump(300);
            var lines=new StringBuilder();
            for(int i=0;i<500;i++)lines.Append("본문 줄 ").Append(i).Append('\n');
            window.SetText(lines.ToString());Pump(500);
            source=(SourceEditor)FieldOf(window,"source");
            source.CaretIndex=0;source.ScrollToVerticalOffset(1500);window.UpdateLayout();Pump(400);
            int firstBefore=source.GetFirstVisibleLineIndex();
            Call(window,"SetLinePercent",new object[]{200.0});Pump(900);window.UpdateLayout();
            int firstAfter=source.GetFirstVisibleLineIndex();
            Check(ref all,report,"h5 line spacing keeps the reading place with the caret off screen",
                firstBefore>0&&Math.Abs(firstAfter-firstBefore)<=2,"before="+firstBefore+" after="+firstAfter+" viewport="+source.ViewportHeight.ToString("0.#"));
            Call(window,"SetLinePercent",new object[]{150.0});Pump(400);
            /* h6: Alt+Down reaches the » that the narrow window paints. */
            window.Width=360;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(800);window.UpdateLayout();Pump(200);
            var topButton=FieldOf(window,"topOverflow") as Button;
            var topMenu=FieldOf(window,"topOverflowMenu") as ContextMenu;
            bool overflowShown=(bool)Call(window,"ToolbarOverflowShown",false);
            bool keyOpened=false,systemKeyWritable=false;
            var presentation=PresentationSource.FromVisual(window);
            if(presentation!=null&&topButton!=null&&topMenu!=null){
                var args=new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,Key.System){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                /* Alt+Down arrives as Key.System with the real key in SystemKey; set it through reflection so
                   this probe does not depend on the property setter being public. */
                var systemKey=typeof(KeyEventArgs).GetProperty("SystemKey",Members);
                if(systemKey!=null&&systemKey.CanWrite){systemKey.SetValue(args,Key.Down,null);systemKeyWritable=true;}
                window.RaiseEvent(args);
                Pump(600);window.UpdateLayout();
                keyOpened=topMenu.IsOpen;
                if(topMenu.IsOpen)topMenu.IsOpen=false;
                Pump(200);
            }
            bool buttonFocusable=topButton!=null&&topButton.Focusable;
            Check(ref all,report,"h6 Alt+Down opens the » menu without a focusable button",
                (systemKeyWritable?overflowShown&&keyOpened:true)&&!buttonFocusable,
                "shown="+overflowShown+" open="+keyOpened+" systemKey="+systemKeyWritable+" focusable="+buttonFocusable);
            /* h7: a bar item that was parked in the » list takes the bar skin again when it comes back. */
            window.Width=450;window.UpdateLayout();Pump(300);
            Call(window,"SetDark",true);Pump(700);
            window.Width=450;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(800);window.UpdateLayout();Pump(200);
            var bar=FieldValue<Menu>(window,"topMenu");
            MenuItem parked=null;bool parkedDark=false,backOnBar=false,barSkin=false;
            if(topMenu!=null&&bar!=null&&topButton!=null){
                /* the parked bar menus are the ones that are in the bar now but reappear inside the » list */
                var onBar=new List<MenuItem>();
                foreach(object entry in bar.Items){var item=entry as MenuItem;if(item!=null)onBar.Add(item);}
                Call(window,"OpenOverflowMenu",topButton,topMenu);Pump(800);window.UpdateLayout();Pump(200);
                foreach(object entry in topMenu.Items){
                    var item=entry as MenuItem;
                    /* bar menus carry no Tag; a ribbon command that moved into the list is tagged with its
                       OverflowCommand, so only a real bar item answers this check */
                    if(item!=null&&item.Tag==null&&item.Items.Count>0&&!onBar.Contains(item)){parked=item;break;}
                }
                parkedDark=parked!=null&&Object.ReferenceEquals(parked.Style,Ui.DarkSubMenuItemStyle());
                if(topMenu.IsOpen)topMenu.IsOpen=false;
                Pump(300);
                Call(window,"SetDark",false);Pump(700);
                window.Width=1400;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(800);window.UpdateLayout();Pump(200);
                backOnBar=parked!=null&&bar.Items.IndexOf(parked)>=0;
                barSkin=parked!=null&&!Object.ReferenceEquals(parked.Style,Ui.DarkSubMenuItemStyle())&&!Object.ReferenceEquals(parked.Template,Ui.DarkTopMenuItemTemplate());
            }
            /* the other direction: parked in light, themed while parked, then returned in dark. Only the
               move itself can restore the bar shape here - RefreshMenuBarSkin sees the same theme flag. */
            bool darkReturn=false;
            if(parked!=null&&topButton!=null){
                window.Width=450;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(600);window.UpdateLayout();
                Call(window,"SetDark",true);Pump(700);
                window.Width=1400;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(700);window.UpdateLayout();Pump(200);
                darkReturn=bar.Items.IndexOf(parked)>=0&&Object.ReferenceEquals(parked.Style,Ui.DarkTopMenuItemStyle());
                Call(window,"SetDark",false);Pump(500);
            }
            Check(ref all,report,"h7 a bar item back from the » list takes the bar skin",parkedDark&&backOnBar&&barSkin&&darkReturn,
                "parkedDark="+parkedDark+" backOnBar="+backOnBar+" barSkin="+barSkin+" darkReturn="+darkReturn);
            /* h8: the table grid must not open just because the ribbon appeared under a still pointer. */
            window.Width=1000;window.Height=620;window.UpdateLayout();Pump(300);
            Call(window,"SetMode",new object[]{false});Pump(700);
            var tableButton=FieldOf(window,"tableButton") as Button;
            Call(window,"SetMode",new object[]{true});Pump(700);
            /* the layout change alone raises MouseEnter on the button that appeared under the pointer */
            if(tableButton!=null)tableButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0){RoutedEvent=Mouse.MouseEnterEvent});
            Pump(300);
            var picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            bool stillClosed=picker==null||!picker.IsOpen;
            object anchorValue=FieldOf(window,"tableHoverScreen");
            Point anchor=(anchorValue is Point)?(Point)anchorValue:new Point(0,0);
            Call(window,"TableHoverMoved",new object[]{anchor});
            picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            bool samePointClosed=picker==null||!picker.IsOpen;
            bool openedOnMove=(bool)Call(window,"TableHoverMoved",new object[]{new Point(anchor.X+40,anchor.Y)});
            picker=FieldValue<ContextMenu>(window,"tablePickerMenu");
            bool hoverOpened=openedOnMove&&picker!=null&&picker.IsOpen;
            if(picker!=null)picker.IsOpen=false;
            Pump(200);
            Check(ref all,report,"h8 the table grid waits for real pointer movement before it opens",
                stillClosed&&samePointClosed&&hoverOpened,
                "still="+stillClosed+" samePoint="+samePointClosed+" moved="+hoverOpened);
        }catch(Exception error){all=false;report.Append("FAIL h fix-round exception: ").Append(OneLine(error.ToString())).Append((char)10);}
        finally{Discard(window);window.Close();}
    }
    /* b58 view items (view bughunt, 2026-10-31): the table outdent/indent tooltips were rewritten in
       Korean on every toolbar refresh, word wrap dropped the reading place and left a far-right caret
       off screen, and reading mode left the size dropdown and the colour palette floating over the
       folded ribbon. */
    static void VerifyViewBughuntRound(ref bool all,StringBuilder report){
        Progress("v-start");
        MainWindow window=NewWindow();
        bool oldHide=(bool)typeof(MainWindow).GetField("hideMarkers",Members).GetValue(null);
        string oldLanguage=null;
        try{
            window.Left=20;window.Top=20;window.Width=900;window.Height=620;window.UpdateLayout();Pump(300);
            var source=(SourceEditor)FieldOf(window,"source");
            /* v1: every update rewrites both tooltips, so the disabled reason and the plain caption must
               follow the UI language without any document or caret change in between. */
            window.SetText("- item\n  | a |\n  | --- |\n  | b |\n");window.SetMode(true);ApplyHideMarkers(window,false);Pump(600);
            source=(SourceEditor)FieldOf(window,"source");
            source.Select(source.Text.IndexOf("| b",StringComparison.Ordinal)+2,0);Pump(200);
            oldLanguage=(string)FieldOf(window,"language");
            FieldSet(window,"language","en");Call(window,"UpdateTools");Pump(250);
            var actions=FieldOf(window,"tableActions") as IList;
            var outdentButton=actions==null?null:actions[9] as Button;
            var indentButton=actions==null?null:actions[10] as Button;
            string enOutdent=outdentButton==null?null:(outdentButton.ToolTip as string);
            string enIndent=indentButton==null?null:(indentButton.ToolTip as string);
            bool enOk=String.Equals(enOutdent,"Outdent table",StringComparison.Ordinal)
                &&String.Equals(enIndent,"Cannot indent further because the table is already inside a list item",StringComparison.Ordinal);
            FieldSet(window,"language","ko");Call(window,"UpdateTools");Pump(250);
            string koOutdent=outdentButton==null?null:(outdentButton.ToolTip as string);
            string koIndent=indentButton==null?null:(indentButton.ToolTip as string);
            bool koOk=String.Equals(koOutdent,"표 내어쓰기",StringComparison.Ordinal)
                &&String.Equals(koIndent,"표가 이미 목록 항목 안에 있어 더 들여쓸 수 없습니다.",StringComparison.Ordinal);
            Check(ref all,report,"v1 table level tooltips follow the UI language on every refresh",enOk&&koOk,
                "en=["+enIndent+"]/["+enOutdent+"] ko=["+koIndent+"]/["+koOutdent+"]");
            /* v2: word wrap off re-lays every line; a caret the reader scrolled away from must keep the
               same line at the top instead of the same pixel offset. */
            window.Width=700;window.Height=520;window.UpdateLayout();Pump(300);
            var wrapped=new StringBuilder();
            for(int i=0;i<300;i++){
                wrapped.Append("문단 ").Append(i).Append(' ');
                for(int j=0;j<40;j++)wrapped.Append("가나다라마바사 ");
                wrapped.Append('\n');
            }
            window.SetText(wrapped.ToString());Call(window,"SetWrap",new object[]{true});Pump(700);
            source=(SourceEditor)FieldOf(window,"source");
            source.CaretIndex=0;source.ScrollToVerticalOffset(4000);window.UpdateLayout();Pump(500);
            int wrapFirst=source.GetFirstVisibleLineIndex();
            Call(window,"SetWrap",new object[]{false});Pump(900);window.UpdateLayout();Pump(200);
            int noWrapFirst=source.GetFirstVisibleLineIndex();
            Check(ref all,report,"v2 word wrap off keeps the reading line with the caret off screen",
                wrapFirst>0&&Math.Abs(noWrapFirst-wrapFirst)<=2,
                "before="+wrapFirst+" after="+noWrapFirst+" offset="+source.VerticalOffset.ToString("0.#")+" viewport="+source.ViewportHeight.ToString("0.#"));
            /* v3: a caret at the end of a long wrapped line is at the viewport's right edge only after
               wrap leaves; turning wrap off must scroll horizontally to it. */
            var longLine=new StringBuilder();
            for(int i=0;i<120;i++)longLine.Append("가나다라마바사 ");
            window.SetText(longLine.ToString());Call(window,"SetWrap",new object[]{true});Pump(600);
            source=(SourceEditor)FieldOf(window,"source");
            source.CaretIndex=source.Document.TextLength;source.TextArea.Caret.BringCaretToView();window.UpdateLayout();Pump(400);
            bool caretLineOnScreen=(bool)Call(window,"CaretLineOnScreen");
            Call(window,"SetWrap",new object[]{false});Pump(900);window.UpdateLayout();Pump(200);
            Rect caretRect=source.TextArea.Caret.CalculateCaretRectangle();
            bool caretRight=!caretRect.IsEmpty&&caretRect.Right>=source.HorizontalOffset-1&&caretRect.Right<=source.HorizontalOffset+source.ViewportWidth+1;
            Check(ref all,report,"v3 word wrap off pulls a far-right caret back on screen",
                caretLineOnScreen&&caretRight,
                "onScreen="+caretLineOnScreen+" caret="+caretRect.Left.ToString("0.#")+".."+caretRect.Right.ToString("0.#")+" offset="+source.HorizontalOffset.ToString("0.#")+" viewport="+source.ViewportWidth.ToString("0.#"));
            /* v4: reading mode folds the ribbon; the size dropdown and the colour palette must fold with it. */
            var sizesBox=FieldValue<ComboBox>(window,"sizes");
            var palette=FieldValue<Popup>(window,"colorPopup");
            bool sizeOpened=false,sizeClosed=false,paletteOpened=false,paletteClosed=false;
            if(sizesBox!=null){
                sizesBox.IsDropDownOpen=true;Pump(400);
                sizeOpened=sizesBox.IsDropDownOpen;
                Call(window,"SetMode",new object[]{false});Pump(700);
                sizeClosed=!sizesBox.IsDropDownOpen;
                Call(window,"SetMode",new object[]{true});Pump(700);
            }
            if(palette!=null){
                palette.IsOpen=true;Pump(400);
                paletteOpened=palette.IsOpen;
                Call(window,"SetMode",new object[]{false});Pump(700);
                paletteClosed=!palette.IsOpen;
            }
            Check(ref all,report,"v4 reading mode closes the size dropdown and the colour palette",
                sizeOpened&&sizeClosed&&paletteOpened&&paletteClosed,
                "size="+sizeOpened+"->"+sizeClosed+" palette="+paletteOpened+"->"+paletteClosed);
            /* v5: the 원문 toggle attaches or detaches the preview, which changes whether the
               caret line is a table row. The table tool row and the five block buttons its gate
               owns must follow immediately, without a caret move or a keystroke. */
            window.SetText("before\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\nafter\n");
            Call(window,"SetMode",new object[]{true});Pump(500);
            source=(SourceEditor)FieldOf(window,"source");
            var toolsHost=FieldOf(window,"tableToolsHost") as Border;
            var headingButton=FieldOf(window,"headingButton") as Button;
            var listButton=FieldOf(window,"listButton") as Button;
            var taskButton=FieldOf(window,"taskButton") as Button;
            var quoteButton=FieldOf(window,"quoteButton") as Button;
            var ruleButton=FieldOf(window,"ruleButton") as Button;
            source.Select(source.Text.IndexOf("| 1",StringComparison.Ordinal)+2,0);Call(window,"UpdateTools");Pump(600);
            bool rowLive=toolsHost!=null&&toolsHost.Visibility==Visibility.Visible;
            bool gatedLive=headingButton!=null&&listButton!=null&&taskButton!=null&&quoteButton!=null&&ruleButton!=null
                &&!headingButton.IsEnabled&&!listButton.IsEnabled&&!taskButton.IsEnabled&&!quoteButton.IsEnabled&&!ruleButton.IsEnabled;
            var previewBox=(CheckBox)FieldOf(window,"preview");
            FieldSet(window,"loading",true);previewBox.IsChecked=true;FieldSet(window,"loading",false);
            Call(window,"SwitchSurface");Pump(800);
            bool rowHiddenRaw=toolsHost!=null&&toolsHost.Visibility==Visibility.Collapsed;
            bool openRaw=headingButton!=null&&listButton!=null&&taskButton!=null&&quoteButton!=null&&ruleButton!=null
                &&headingButton.IsEnabled&&listButton.IsEnabled&&taskButton.IsEnabled&&quoteButton.IsEnabled&&ruleButton.IsEnabled;
            FieldSet(window,"loading",true);previewBox.IsChecked=false;FieldSet(window,"loading",false);
            Call(window,"SwitchSurface");Pump(800);
            bool rowBack=toolsHost!=null&&toolsHost.Visibility==Visibility.Visible;
            bool gatedBack=headingButton!=null&&listButton!=null&&taskButton!=null&&quoteButton!=null&&ruleButton!=null
                &&!headingButton.IsEnabled&&!listButton.IsEnabled&&!taskButton.IsEnabled&&!quoteButton.IsEnabled&&!ruleButton.IsEnabled;
            Check(ref all,report,"v5 원문 toggle updates the table tool row and the in-table button gate at once",
                rowLive&&gatedLive&&rowHiddenRaw&&openRaw&&rowBack&&gatedBack,
                "liveRow="+rowLive+" liveGate="+gatedLive+" rawRow="+rowHiddenRaw+" rawOpen="+openRaw+" backRow="+rowBack+" backGate="+gatedBack);
        }catch(Exception error){all=false;report.Append("FAIL v view bughunt exception: ").Append(OneLine(error.ToString())).Append((char)10);}
        finally{
            typeof(MainWindow).GetField("hideMarkers",Members).SetValue(null,oldHide);
            if(oldLanguage!=null)FieldSet(window,"language",oldLanguage);
            Discard(window);window.Close();
        }
    }
    /* b57: the two measured toolbar rows must settle in one pass per width, and a bar menu that spent time
       in the » list must open its dropdown where it did before the move. The resize walk goes through the
       real path: setting Width raises SizeChanged at the forced layout, and the posted Background pass then
       runs inside Pump. */
    static void VerifyOverflowStability(ref bool all,StringBuilder report){
        MainWindow window=NewWindow();
        try{
            window.Left=20;window.Top=20;window.Width=1100;window.Height=620;
            window.SetMode(true);Call(window,"SetDark",false);
            window.UpdateLayout();Pump(800);
            var undo=FieldOf(window,"undoButton") as Button;
            bool firstKept=undo!=null;
            int firstHiddenAt=-1,worstPasses=0,selfPasses=0;
            int previous=window.overflowPassCount;
            var walk=new StringBuilder();
            for(int width=1100;width>=450;width-=25){
                window.Width=width;window.UpdateLayout();Pump(240);window.UpdateLayout();Pump(60);
                int passes=window.overflowPassCount-previous;previous=window.overflowPassCount;
                if(passes>worstPasses)worstPasses=passes;
                if(undo==null||!undo.IsVisible){firstKept=false;if(firstHiddenAt<0)firstHiddenAt=width;}
                /* The » gap and the visibility changes this pass made are laid out next; neither may post
                   another full pass (that would feed the pass its own result again). */
                int settled=window.overflowPassCount;
                window.UpdateLayout();Pump(140);window.UpdateLayout();
                int extra=window.overflowPassCount-settled;
                if(extra>selfPasses)selfPasses=extra;
                if(width==1100||width==950||width==750||width==550||width==450)
                    walk.Append(' ').Append(width).Append(':').Append(passes).Append('p')
                        .Append(passes>3?"!":"").Append(undo!=null&&undo.IsVisible?"":"/hidden");
            }
            Check(ref all,report,"i1 the toolbar overflow settles in one pass per width and keeps the first unit",
                undo!=null&&firstKept&&selfPasses==0&&worstPasses<=3&&(bool)Call(window,"ToolbarOverflowShown",true),
                "firstUnitHiddenAt="+firstHiddenAt+" worstPasses="+worstPasses+" selfScheduled="+selfPasses
                    +" narrowOverflow="+(bool)Call(window,"ToolbarOverflowShown",true)+" walk="+walk);
            /* i2: the same bar MenuItem is put back on the bar when the window widens; its dropdown must
               open at the position it had before it moved into the » list. A hidden desktop can refuse to
               keep a bar submenu popup open, so the placements the hook records on the popup are read as
               well - the real rectangle is compared whenever both opens really stayed open. */
            bool menuOk=false;string menuState="no menu bar";
            var bar=FieldValue<Menu>(window,"topMenu");
            var topButton=FieldOf(window,"topOverflow") as Button;
            var overflowMenu=FieldValue<ContextMenu>(window,"topOverflowMenu");
            MenuItem item=null;
            int parkedWidth=0;
            if(bar!=null&&topButton!=null&&overflowMenu!=null&&bar.Items.Count>0){
                window.Width=1000;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
                var topItems=new List<MenuItem>();
                for(int i=0;i<bar.Items.Count;i++){var candidate=bar.Items[i] as MenuItem;if(candidate!=null)topItems.Add(candidate);}
                /* The » list is filled from the front of the top row's units and the 원문 toggle is one of
                   them (InsertControls.BuildTopOverflowUnits), so a narrow width can park only that button
                   while every bar menu stays on the row. The item under test is the one that really moves
                   into the list at a width where » is shown; 450 is the probe's usual width, narrower
                   windows (down to MinWidth 360) are tried when 450 parks no menu item. */
                for(int width=450;width>=360&&item==null;width-=30){
                    window.Width=width;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(240);window.UpdateLayout();
                    if(!(bool)Call(window,"ToolbarOverflowShown",false))continue;
                    for(int i=overflowMenu.Items.Count-1;i>=0;i--){
                        var candidate=overflowMenu.Items[i] as MenuItem;
                        if(candidate==null||topItems.IndexOf(candidate)<0)continue;
                        if(!(bool)Call(window,"ToolbarOverflowMenuHas",false,candidate))continue;
                        item=candidate;parkedWidth=width;break;
                    }
                }
                if(item==null)menuState="no bar menu moved into the » list at 450..360";
            }
            if(item!=null&&topButton!=null&&overflowMenu!=null){
                window.Width=1000;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
                Rect wideItem=ScreenRect(item);
                bool wideOpen=false;
                Rect widePopup=OpenSubmenuRect(window,item,2,out wideOpen);
                Popup templatePopup=ItemPopup(item);
                double baseH=templatePopup==null?0:templatePopup.HorizontalOffset;
                double baseV=templatePopup==null?0:templatePopup.VerticalOffset;
                double gapX=wideOpen&&!wideItem.IsEmpty?widePopup.Left-wideItem.Left:0;
                double gapY=wideOpen&&!wideItem.IsEmpty?widePopup.Top-wideItem.Top:0;
                double popupWidth=wideOpen?widePopup.Width:0;
                /* the narrowest width where the item still sits on the bar and its normal dropdown would
                   run past the window edge: that is where the » placement clamp visibly moves a hook */
                int probeWidth=0;bool crossed=false;string scan="";
                for(int width=800;width>=480;width-=10){
                    window.Width=width;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(120);window.UpdateLayout();
                    if(bar.Items.IndexOf(item)<0){scan+=" parked@"+width;break;}
                    Rect box=ScreenRect(item),client=WindowScreenRect(window);
                    probeWidth=width;
                    if(popupWidth>0&&!box.IsEmpty&&!client.IsEmpty&&box.Left+gapX+popupWidth>client.Right-6){crossed=true;scan+=" cross@"+width;break;}
                }
                Rect beforeItem=Rect.Empty,beforePopup=Rect.Empty;bool beforeOpen=false;
                if(probeWidth>0){
                    window.Width=probeWidth;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(400);window.UpdateLayout();
                    beforeItem=ScreenRect(item);
                    beforePopup=OpenSubmenuRect(window,item,2,out beforeOpen);
                }
                bool parked=false;double parkedH=baseH,parkedV=baseV;
                window.Width=parkedWidth;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(500);window.UpdateLayout();
                if((bool)Call(window,"ToolbarOverflowShown",false)){
                    Call(window,"OpenOverflowMenu",topButton,overflowMenu);Pump(700);window.UpdateLayout();Pump(200);
                    parked=overflowMenu.IsOpen&&overflowMenu.Items.IndexOf(item)>=0;
                    /* two opens: the popup hook is installed on the first SubmenuOpened, so the second one
                       is where the » clamp really runs */
                    if(parked){
                        bool parkedOpen=false;
                        OpenSubmenuRect(window,item,2,out parkedOpen);
                        Popup parkedPopup=ItemPopup(item);
                        parkedH=parkedPopup==null?baseH:parkedPopup.HorizontalOffset;
                        parkedV=parkedPopup==null?baseV:parkedPopup.VerticalOffset;
                        window.UpdateLayout();
                    }
                    overflowMenu.IsOpen=false;Pump(400);
                }
                bool back=false;Rect afterItem=Rect.Empty,afterPopup=Rect.Empty;bool afterOpen=false;
                if(probeWidth>0){
                    window.Width=probeWidth;window.UpdateLayout();window.ToolbarRefreshOverflow();Pump(600);window.UpdateLayout();Pump(200);
                    back=bar.Items.IndexOf(item)>=0;
                    if(back){
                        afterItem=ScreenRect(item);
                        afterPopup=OpenSubmenuRect(window,item,2,out afterOpen);
                    }
                }
                Popup afterTemplate=ItemPopup(item);
                double afterH=afterTemplate==null?baseH:afterTemplate.HorizontalOffset;
                double afterV=afterTemplate==null?baseV:afterTemplate.VerticalOffset;
                bool offsetsOk=Math.Abs(afterH-baseH)<=0.5&&Math.Abs(afterV-baseV)<=0.5;
                bool rectMeasured=beforeOpen&&afterOpen&&!beforePopup.IsEmpty&&!afterPopup.IsEmpty;
                bool rectOk=true;
                if(rectMeasured){
                    rectOk=Math.Abs(afterItem.Left-beforeItem.Left)<=1.5&&Math.Abs(afterItem.Top-beforeItem.Top)<=1.5
                        &&Math.Abs((afterPopup.Left-afterItem.Left)-(beforePopup.Left-beforeItem.Left))<=2
                        &&Math.Abs((afterPopup.Top-afterItem.Top)-(beforePopup.Top-beforeItem.Top))<=2;
                }
                bool clamped=Math.Abs(parkedH-baseH)>2||Math.Abs(parkedV-baseV)>2;
                menuOk=parked&&back&&offsetsOk&&rectOk;
                menuState="parked="+parked+" back="+back+" width="+probeWidth+" parkedAt="+parkedWidth+" crossed="+crossed+" clamped="+clamped
                    +" offsets="+baseH.ToString("0.#")+","+baseV.ToString("0.#")+" -> "+afterH.ToString("0.#")+","+afterV.ToString("0.#")
                    +" rect="+(rectMeasured?rectOk.ToString():"not-measured")
                    +" item="+beforeItem.Left.ToString("0.#")+"/"+afterItem.Left.ToString("0.#")+" scan="+scan;
                if(overflowMenu.IsOpen)overflowMenu.IsOpen=false;
            }
            Check(ref all,report,"i2 a bar menu back from the » list keeps its dropdown position",menuOk,menuState);
        }catch(Exception error){all=false;report.Append("FAIL i overflow stability exception: ").Append(OneLine(error.ToString())).Append((char)10);}
        finally{Discard(window);window.Close();}
    }
    /* UI bug hunt round 4's P2 items (2026-10-31), all offscreen on the product surface:
       P2-1 the heading panel leaves with TXT mode and comes back open;
       P2-3 a transient status answer sits next to the normal line and clears;
       P2-4 an outline rebuild keeps the reader's fold, selection and scroll (small and large trees);
       P2-5 the heading-change test reads headings behind list/quote prefixes and leaves ordinary
            typing, an Enter in a plain paragraph and a bullet line below alone;
       P3-1 the outline grammar matches the line the view draws. */
    static void VerifyOutlineAndStatusSurfaces(ref bool all,StringBuilder report){
        Progress("p2-start");
        MainWindow window=NewWindow();
        try{
            var source=FieldOf(window,"source") as SourceEditor;
            var outline=FieldValue<TreeView>(window,"outline");
            var outlineButton=FieldOf(window,"outlineButton") as Button;
            var status=FieldValue<TextBlock>(window,"status");
            if(source==null||outline==null||outlineButton==null||status==null){
                Check(ref all,report,"p2-0 outline and status surfaces exist",false,
                    "source="+(source!=null)+" outline="+(outline!=null)+" button="+(outlineButton!=null)+" status="+(status!=null));
                return;
            }
            /* P2-1: the panel belongs to the mode that can fill it. Two H1 roots so the tree's item
               count is the root count. */
            string headingDoc="# 첫 제목\n\n# 둘째 제목\n\n본문 문단입니다.\n";
            window.SetText(headingDoc);window.SetMode(true);Pump(400);
            outline.Visibility=Visibility.Visible;Call(window,"RefreshOutline");Pump(300);
            bool opened=outline.Items.Count==2;
            Call(window,"ToggleTextModeNow");Pump(700);
            bool hiddenInTxt=outline.Visibility==Visibility.Collapsed&&!outlineButton.IsEnabled;
            bool emptied=outline.Items.Count==0;
            Call(window,"ToggleTextModeNow");Pump(700);
            bool backInMarkdown=outline.Visibility==Visibility.Visible&&outlineButton.IsEnabled;
            window.SetText(headingDoc);Pump(400);
            bool backItems=outline.Items.Count==2;
            Check(ref all,report,"p2-1 the outline leaves with TXT mode and comes back open",
                opened&&hiddenInTxt&&emptied&&backInMarkdown&&backItems,
                "opened="+opened+" hiddenInTxt="+hiddenInTxt+" emptied="+emptied+" back="+backInMarkdown+" items="+outline.Items.Count);

            /* P2-3: a replace run's count and a shared notice sit next to the normal status and are
               gone on the next caret move; the notice slot also expires on its own timer. */
            window.SetText("alpha beta alpha\n");window.SetMode(true);Pump(300);
            Call(window,"ShowReplace");Pump(400);
            var box=FieldValue<TextBox>(window,"query");
            var field=FieldValue<TextBox>(window,"replacement");
            bool statusOk=false;string statusDetail="replacement field missing";
            if(box!=null&&field!=null){
                box.Text="alpha";box.CaretIndex=box.Text.Length;Pump(500);
                field.Text="X";Pump(150);
                source.Select(0,0);Pump(150);
                Call(window,"ReplaceAll");Pump(600);
                string noticed=status.Text;
                bool appended=noticed.IndexOf("개를 바꿨습니다",StringComparison.Ordinal)>=0
                    &&noticed.IndexOf("[",StringComparison.Ordinal)>=0&&noticed.IndexOf("%",StringComparison.Ordinal)>=0;
                source.Select(1,0);Pump(300);
                bool cleared=status.Text.IndexOf("개를 바꿨습니다",StringComparison.Ordinal)<0
                    &&status.Text.IndexOf("[",StringComparison.Ordinal)>=0;
                Call(window,"ShowStatusNotice","잠깐 알림");Pump(200);
                bool helperShown=status.Text.IndexOf("잠깐 알림",StringComparison.Ordinal)>=0&&status.Text.IndexOf("[",StringComparison.Ordinal)>=0;
                Pump(4700);
                bool expired=status.Text.IndexOf("잠깐 알림",StringComparison.Ordinal)<0&&status.Text.IndexOf("[",StringComparison.Ordinal)>=0;
                statusOk=appended&&cleared&&helperShown&&expired;
                statusDetail="appended="+appended+" clearedOnCaret="+cleared+" helper="+helperShown+" expired="+expired+" line=["+OneLine(status.Text)+"]";
                Call(window,"CloseSearch");Pump(200);
            }
            Check(ref all,report,"p2-3 a transient status answer keeps the normal line",statusOk,statusDetail);

            /* P2-4, small tree: fold the first root, select the second, then type a marker that
               rebuilds the list; both have to come back. The scroll is checked on a plain rebuild
               with nothing selected, so a selection's BringIntoView cannot mask it. */
            var tree=new StringBuilder("# 머리 하나\n## 아래 하나\n## 아래 둘\n# 머리 둘\n");
            for(int i=0;i<40;i++)tree.Append("## 가지 ").Append(i).Append('\n');
            tree.Append("\n중간 본문 줄\n마지막 본문 줄\n");
            window.SetText(tree.ToString());window.SetMode(true);Pump(400);
            outline.Visibility=Visibility.Visible;Call(window,"RefreshOutline");Pump(400);
            bool smallListed=outline.Items.Count==2;
            var firstRoot=outline.Items.Count>0?outline.Items[0] as TreeViewItem:null;
            var secondRoot=outline.Items.Count>1?outline.Items[1] as TreeViewItem:null;
            if(firstRoot!=null)firstRoot.IsExpanded=false;
            if(secondRoot!=null)secondRoot.IsSelected=true;
            Pump(300);
            string selectedBefore=secondRoot==null?"":System.Windows.Automation.AutomationProperties.GetName(secondRoot);
            object itemBefore=outline.Items.Count>0?outline.Items[0]:null;
            int middle=LineNumberWithText(source,"중간 본문 줄");
            if(middle>0){
                source.Select(source.Document.GetLineByNumber(middle).Offset,0);
                source.TextArea.PerformTextInput("#");source.TextArea.PerformTextInput(" ");
                Pump(600);
            }
            var newFirst=outline.Items.Count>0?outline.Items[0] as TreeViewItem:null;
            bool rebuilt=middle>0&&itemBefore!=null&&!Object.ReferenceEquals(itemBefore,outline.Items.Count>0?outline.Items[0]:null);
            bool stillFolded=newFirst!=null&&!newFirst.IsExpanded;
            var selectedAfterItem=outline.SelectedItem as TreeViewItem;
            string selectedAfter=selectedAfterItem==null?"":System.Windows.Automation.AutomationProperties.GetName(selectedAfterItem);
            Check(ref all,report,"p2-4 an outline rebuild keeps the fold and the selection",
                smallListed&&rebuilt&&stillFolded&&selectedBefore.Length>0&&selectedAfter==selectedBefore,
                "listed="+smallListed+" rebuilt="+rebuilt+" folded="+stillFolded+" selection=["+selectedBefore+"]->["+selectedAfter+"]");
            var selectedNow=outline.SelectedItem as TreeViewItem;
            if(selectedNow!=null)selectedNow.IsSelected=false;
            var scrollHost=ScrollChild(outline);
            if(scrollHost!=null)scrollHost.ScrollToVerticalOffset(90);
            Pump(400);
            double scrollBefore=scrollHost==null?0:scrollHost.VerticalOffset;
            Call(window,"RefreshOutline");Pump(500);
            double scrollAfter=scrollHost==null?0:scrollHost.VerticalOffset;
            bool scrollKept=scrollBefore>0&&Math.Abs(scrollAfter-scrollBefore)<=2;
            Check(ref all,report,"p2-4 an outline rebuild keeps the scroll",scrollKept,
                "scroll="+scrollBefore.ToString("0.#")+"->"+scrollAfter.ToString("0.#"));

            /* P2-5, Enter in a plain paragraph: no rebuild (the same item object stays in place). */
            object stable=outline.Items.Count>0?outline.Items[0]:null;
            int stableCount=outline.Items.Count;
            int lastLineNumber=LineNumberWithText(source,"마지막 본문 줄");
            if(lastLineNumber>0){
                var line=source.Document.GetLineByNumber(lastLineNumber);
                source.Select(line.EndOffset,0);source.TextArea.PerformTextInput("\n");Pump(500);
            }
            bool enterKept=lastLineNumber>0&&stable!=null&&outline.Items.Count==stableCount
                &&Object.ReferenceEquals(stable,outline.Items.Count>0?outline.Items[0]:null);
            Check(ref all,report,"p2-5 an Enter in a plain paragraph does not rebuild the outline",enterKept,
                "items="+stableCount+"->"+outline.Items.Count+" same="+Object.ReferenceEquals(stable,outline.Items.Count>0?outline.Items[0]:null));

            /* P2-4, large tree: the background scan publishes through ItemsSource and binds
               IsExpanded to the node, so a fold made on the container has to reach the rebuilt one. */
            var big=new StringBuilder("# 큰 하나\n## 큰 둘\n");
            for(int i=0;i<2050;i++)big.Append("본문 ").Append(i).Append('\n');
            window.SetText(big.ToString());window.SetMode(true);Pump(500);
            outline.Visibility=Visibility.Visible;Call(window,"RefreshOutline");Pump(900);
            outline.UpdateLayout();Pump(100);
            /* A scroll the previous document left is not this tree's; start at the top so the first
               row's container exists to be folded. */
            var largeScroll=ScrollChild(outline);
            if(largeScroll!=null)largeScroll.ScrollToVerticalOffset(0);
            outline.UpdateLayout();Pump(200);
            bool largeUsed=outline.ItemsSource!=null&&outline.Items.Count==1;
            var largeRoot=outline.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
            bool largeReady=largeRoot!=null;
            if(largeRoot!=null){largeRoot.IsExpanded=false;largeRoot.IsSelected=true;}
            Pump(300);
            var tailLine=source.Document.GetLineByNumber(Math.Max(1,source.Document.LineCount-1));
            source.Select(tailLine.Offset,0);
            source.TextArea.PerformTextInput("#");source.TextArea.PerformTextInput(" ");Pump(400);
            Call(window,"RefreshOutline");Pump(1200);
            outline.UpdateLayout();Pump(200);
            var rebuiltRoot=outline.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
            bool largeFolded=rebuiltRoot!=null&&!rebuiltRoot.IsExpanded;
            bool largeSelected=rebuiltRoot!=null&&rebuiltRoot.IsSelected;
            Check(ref all,report,"p2-4 the large outline keeps its fold and selection",largeUsed&&largeReady&&largeFolded&&largeSelected,
                "large="+largeUsed+" ready="+largeReady+" folded="+largeFolded+" selected="+largeSelected);

            /* P2-5: the decision itself, on a bare document and the change the editor would raise. */
            bool bodyTyping=!OutlineChangeDecision(window,"alpha beta\n- 항목\n",5,"x");
            bool tagTyping=!OutlineChangeDecision(window,"#태그 줄\n",3,"x");
            bool listHeading=OutlineChangeDecision(window,"- ## 목록 제목\n",6,"x");
            bool quoteHeading=OutlineChangeDecision(window,"> # 인용 제목\n",6,"x");
            bool plainEnter=!OutlineChangeDecision(window,"alpha\nbeta\n",5,"\r\n");
            bool setextEnter=OutlineChangeDecision(window,"제목\n====\n본문\n",2,"\r\n");
            bool setextJoin=OutlineChangeDecision(window,"제목\n====\n본문\n",2,null);
            Check(ref all,report,"p2-5 heading changes are judged by the edited lines",
                bodyTyping&&tagTyping&&listHeading&&quoteHeading&&plainEnter&&setextEnter&&setextJoin,
                "body="+bodyTyping+" tag="+tagTyping+" listHeading="+listHeading+" quoteHeading="+quoteHeading
                    +" plainEnter="+plainEnter+" setextEnter="+setextEnter+" setextJoin="+setextJoin);

            /* P3-1: the outline's grammar against the view's. A quoted heading counts the spaces
               after its marker (four is text, three is a heading) and an indented paragraph line
               over an underline is a heading the view draws. */
            var spacedQuote=OutlineNodes(">     # 글자\n");
            var fourSpaceQuote=OutlineNodes(">    # 글자\n");
            var indentedSetext=OutlineNodes("    들여쓴 줄\n---\n");
            /* A line of hashes only is an empty heading in the view (AtxStart takes the line's
               end), so the "---" under it is a rule: the scan must not promote the pair to a
               setext H2. */
            var emptyAtx=OutlineNodes("###\n---\n");
            bool quoteLimit=spacedQuote.Count==0&&fourSpaceQuote.Count==1&&OutlineNodeLevel(fourSpaceQuote[0])==1;
            bool setextIndent=indentedSetext.Count==1&&OutlineNodeLevel(indentedSetext[0])==2;
            bool hashOnly=emptyAtx.Count==0;
            Check(ref all,report,"p3-1 the outline grammar matches the view",quoteLimit&&setextIndent&&hashOnly,
                "fiveSpaceQuote="+spacedQuote.Count+" fourSpaceQuote="+fourSpaceQuote.Count
                    +" indentedSetext="+indentedSetext.Count+" level="+(indentedSetext.Count==0?"-":OutlineNodeLevel(indentedSetext[0]).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    +" hashOnlyAtx="+emptyAtx.Count);

            /* 11-01 #8 (user video 16:48): a press on a nested outline row jumped to the document's
               first H1. The old press path asked ItemsControl.ContainerFromElement for the row, which
               answers with the top-level heading a nested row hangs under (SourceOutline.cs:254-257);
               the fix walks up from the pressed element instead (OutlineRowUnder, :309) and jumps from
               the row it finds (JumpOutlineItem, :332). This drives the same pair the press handler
               runs (OutlineRowUnder(args.OriginalSource) -> JumpOutlineItem). Depends on: OutlineRowUnder
               returning the pressed nested row (not its root) and JumpOutlineItem carrying that row's
               offset to the caret. Reverting to ContainerFromElement sends the caret to line 1
               ("# 머리 하나") instead of line 2 and this check fails; removing either method fails it
               through the missing member. */
            string outlineJumpDoc="# 머리 하나\n## 아래 제목\n본문 줄\n# 머리 둘\n";
            window.SetText(outlineJumpDoc);window.SetMode(true);Pump(400);
            outline.Visibility=Visibility.Visible;Call(window,"RefreshOutline");Pump(500);
            window.UpdateLayout();outline.UpdateLayout();Pump(200);
            var jumpRoot=outline.Items.Count>0?outline.Items[0] as TreeViewItem:null;
            if(jumpRoot!=null)jumpRoot.IsExpanded=true;
            Pump(400);outline.UpdateLayout();Pump(200);
            TreeViewItem jumpRow=jumpRoot==null?null:jumpRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
            if(jumpRow==null&&jumpRoot!=null){jumpRoot.UpdateLayout();Pump(150);jumpRow=jumpRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;}
            var pressTarget=jumpRow==null?null:FirstTextBlock(jumpRow);
            var rowUnder=typeof(MainWindow).GetMethod("OutlineRowUnder",BindingFlags.Static|BindingFlags.NonPublic);
            var jumpItem=typeof(MainWindow).GetMethod("JumpOutlineItem",BindingFlags.Instance|BindingFlags.NonPublic);
            bool rowResolved=false,clickMoved=false;
            string jumpState="no nested row realised";
            int headingOffset=source.Document.GetLineByNumber(2).Offset;
            if(pressTarget!=null&&rowUnder!=null&&jumpItem!=null){
                TreeViewItem resolved=null;
                try{resolved=rowUnder.Invoke(null,new object[]{pressTarget}) as TreeViewItem;}catch(TargetInvocationException){}
                rowResolved=jumpRow!=null&&Object.ReferenceEquals(resolved,jumpRow);
                if(resolved!=null){
                    try{jumpItem.Invoke(window,new object[]{resolved});}catch(TargetInvocationException){}
                    Pump(300);
                }
                clickMoved=source.CaretIndex==headingOffset&&source.TextArea.Caret.Line==2;
                jumpState="resolved="+(resolved==null?"null":(rowResolved?"row":"other"))+" caret="+source.CaretIndex+"/line"+source.TextArea.Caret.Line+" want="+headingOffset;
            }
            Check(ref all,report,"11-01 #8 a press on a nested outline row jumps to that heading",rowResolved&&clickMoved,jumpState);
        }catch(Exception error){all=false;report.Append("FAIL p2 outline/status exception: ").Append(OneLine(error.ToString())).Append('\n');}
        finally{Discard(window);window.Close();}
    }
    // The first TextBlock inside a realised row, the element a press inside the row names.
    static TextBlock FirstTextBlock(DependencyObject root){
        var text=root as TextBlock;
        if(text!=null)return text;
        int count=root==null?-1:VisualTreeHelper.GetChildrenCount(root);
        for(int i=0;i<count;i++){var found=FirstTextBlock(VisualTreeHelper.GetChild(root,i));if(found!=null)return found;}
        return null;
    }
    // The scroll host of a templated control, found by walking the visual tree (the tree's own
    // ScrollViewer is the first one on the way down).
    static ScrollViewer ScrollChild(DependencyObject root){
        var scroll=root as ScrollViewer;
        if(scroll!=null)return scroll;
        int count=root==null?-1:VisualTreeHelper.GetChildrenCount(root);
        for(int i=0;i<count;i++){var found=ScrollChild(VisualTreeHelper.GetChild(root,i));if(found!=null)return found;}
        return null;
    }
    static int LineNumberWithText(SourceEditor source,string text){
        for(int i=1;i<=source.Document.LineCount;i++)if(String.Equals(source.Document.GetText(source.Document.GetLineByNumber(i)),text,StringComparison.Ordinal))return i;
        return -1;
    }
    // One edit on a bare document, answered by the window's own OutlineMayChange (the same call the
    // document-change handler makes). inserted==null removes one character at the offset.
    static bool OutlineChangeDecision(MainWindow window,string seed,int offset,string inserted){
        var document=new ICSharpCode.AvalonEdit.Document.TextDocument(seed);
        ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs captured=null;
        EventHandler<ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs> handler=delegate(object sender,ICSharpCode.AvalonEdit.Document.DocumentChangeEventArgs change){if(captured==null)captured=change;};
        document.Changed+=handler;
        try{if(inserted==null)document.Remove(offset,1);else document.Insert(offset,inserted);}
        finally{document.Changed-=handler;}
        return captured!=null&&(bool)Call(window,"OutlineMayChange",document,captured);
    }
    static IList OutlineNodes(string markdown){
        var scan=typeof(MainWindow).GetMethod("ScanSourceOutline",BindingFlags.Static|BindingFlags.NonPublic);
        if(scan==null)return new ArrayList();
        var nodes=scan.Invoke(null,new object[]{new ICSharpCode.AvalonEdit.Document.StringTextSource(markdown),System.Threading.CancellationToken.None});
        return nodes as IList??new ArrayList();
    }
    static int OutlineNodeLevel(object node){
        var property=node==null?null:node.GetType().GetProperty("Level",Members);
        return property==null?0:(int)property.GetValue(node,null);
    }
    static void SendEscape(IntPtr hwnd){
        if(hwnd==IntPtr.Zero)return;
        Win32.SendMessage(hwnd,0x0100,(IntPtr)0x1B,(IntPtr)0x00010001);
        Win32.SendMessage(hwnd,0x0101,(IntPtr)0x1B,unchecked((IntPtr)(int)0xC0010001u));
    }
    /* The popup surface a ContextMenu actually paints: its template root Border (absent on the system
       template when no brush is set), so a theme pass is read from the pixels' brush, not a screenshot. */
    static Brush MenuSurfaceBrush(ContextMenu menu){
        try{
            if(menu==null||VisualTreeHelper.GetChildrenCount(menu)==0)return null;
            var border=VisualTreeHelper.GetChild(menu,0) as Border;
            return border==null?null:border.Background;
        }catch(Exception){return null;}
    }
    static bool SameBrush(Brush first,Brush second){
        var a=first as SolidColorBrush;var b=second as SolidColorBrush;
        if(a==null||b==null)return false;
        return a.Color==b.Color;
    }
    static IntPtr LParam(Point point){return (IntPtr)(((int)Math.Round(point.Y)&0xFFFF)<<16|((int)Math.Round(point.X)&0xFFFF));}
    static Point ToClient(IntPtr hwnd,Point screen){
        if(hwnd==IntPtr.Zero)return screen;
        var point=new Win32.POINT{X=(int)Math.Round(screen.X),Y=(int)Math.Round(screen.Y)};
        Win32.ScreenToClient(hwnd,ref point);
        return new Point(point.X,point.Y);
    }
    static class Win32{
        [StructLayout(LayoutKind.Sequential)] internal struct POINT{internal int X;internal int Y;}
        [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] internal static extern IntPtr GetCapture();
        [DllImport("user32.dll")] internal static extern bool ScreenToClient(IntPtr hwnd,ref POINT point);
        [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hwnd,ref POINT point);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    }
    static string SubmenuBounds(MainWindow window,MenuItem item){
        Popup popup=null;
        try{popup=item.Template==null?null:item.Template.FindName("PART_Popup",item) as Popup;}catch(Exception){}
        if(popup==null||!popup.IsOpen)return "submenu popup is not open";
        var child=popup.Child as FrameworkElement;
        if(child==null)return "submenu has no child";
        try{
            Point left=child.PointToScreen(new Point(0,0)),right=child.PointToScreen(new Point(child.ActualWidth,0));
            Point windowLeft=window.PointToScreen(new Point(0,0)),windowRight=window.PointToScreen(new Point(window.ActualWidth,0));
            /* User rule (HANDOFF, 11-01): a submenu of the » list unfolds to the right of its entry, even past the
               window's edge; only the screen edge sends it to the other side. So the check is against the entry
               and the screen, not the window. The template overlaps the entry by 4 px, 8 px of slack covers it. */
            Rect entry=ScreenRect(item);
            if(entry.IsEmpty)return "submenu entry is not realised";
            Rect screen=new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight);
            bool onRightSide=left.X>=entry.Right-8;
            bool flippedLeft=right.X<=entry.Left+8;
            bool onScreen=left.X>=screen.Left-1&&right.X<=screen.Right+1;
            bool roomOnRight=entry.Right-8+(right.X-left.X)<=screen.Right+1;
            bool ok=onScreen&&(onRightSide||(flippedLeft&&!roomOnRight));
            return "submenu="+left.X.ToString("0")+".."+right.X.ToString("0")+" entry="+entry.Left.ToString("0")+".."+entry.Right.ToString("0")+" screen="+screen.Left.ToString("0")+".."+screen.Right.ToString("0")+" window="+windowLeft.X.ToString("0")+".."+windowRight.X.ToString("0")+" right="+onRightSide+" flipped="+flippedLeft+" room="+roomOnRight+" ok="+ok;
        }catch(Exception failure){return "submenu measure error "+failure.GetType().Name;}
    }
    /* Screen rectangles for the overflow placement checks: the window, an element on it, and the popup a
       MenuItem's template opens. Empty means the visual is not realised, so a caller can report it. */
    static Rect ScreenRect(FrameworkElement element){
        if(element==null||!element.IsVisible||element.ActualWidth<=0||element.ActualHeight<=0)return Rect.Empty;
        try{return new Rect(element.PointToScreen(new Point(0,0)),element.PointToScreen(new Point(element.ActualWidth,element.ActualHeight)));}
        catch(Exception){return Rect.Empty;}
    }
    static Rect WindowScreenRect(MainWindow window){
        if(window==null)return Rect.Empty;
        try{return new Rect(window.PointToScreen(new Point(0,0)),window.PointToScreen(new Point(window.ActualWidth,window.ActualHeight)));}
        catch(Exception){return Rect.Empty;}
    }
    static Rect PopupScreenRect(MenuItem item){
        Popup popup=null;
        try{popup=item==null||item.Template==null?null:item.Template.FindName("PART_Popup",item) as Popup;}catch(Exception){}
        var child=popup==null?null:popup.Child as FrameworkElement;
        if(popup==null||!popup.IsOpen||child==null||child.ActualWidth<=0||child.ActualHeight<=0)return Rect.Empty;
        return ScreenRect(child);
    }
    /* Opens a MenuItem submenu the given number of times and returns the last open popup's screen rect
       (Empty when the popup did not stay open). Two opens let the popup's Opened hook, installed on the
       first SubmenuOpened, run on a later open. */
    static Rect OpenSubmenuRect(MainWindow window,MenuItem item,int opens,out bool stayedOpen){
        Rect rect=Rect.Empty;stayedOpen=false;
        for(int i=0;i<opens;i++){
            try{item.IsSubmenuOpen=true;}catch(Exception){return rect;}
            Pump(400);window.UpdateLayout();Pump(100);
            if(i==opens-1){rect=PopupScreenRect(item);stayedOpen=!rect.IsEmpty;}
            try{item.IsSubmenuOpen=false;}catch(Exception){}
            Pump(150);
        }
        return rect;
    }
    /* The popup part of a MenuItem's template; the overflow hook writes its placement offsets here. */
    static Popup ItemPopup(MenuItem item){
        if(item==null)return null;
        try{item.ApplyTemplate();}catch(Exception){}
        try{return item.Template==null?null:item.Template.FindName("PART_Popup",item) as Popup;}catch(Exception){return null;}
    }
    static string PickerSurface(ContextMenu picker){
        if(picker==null)return "picker missing";
        string root="";
        if(VisualTreeHelper.GetChildrenCount(picker)>0){var border=VisualTreeHelper.GetChild(picker,0) as Border;if(border!=null)root=BrushText(border.Background);}
        return "bg="+BrushText(picker.Background)+" border="+BrushText(picker.BorderBrush)+" ink="+BrushText(picker.Foreground)+" root="+root;
    }
    static string BrushText(Brush brush){var solid=brush as SolidColorBrush;return solid==null?(brush==null?"null":brush.ToString()):ColorText(solid.Color);}
    static string ColorText(Color color){return "#"+color.R.ToString("X2")+color.G.ToString("X2")+color.B.ToString("X2");}
    static Window OwnedDialog(MainWindow window){
        if(Application.Current==null)return null;
        foreach(Window candidate in Application.Current.Windows){
            if(Object.ReferenceEquals(candidate,window))continue;
            if(Object.ReferenceEquals(candidate.Owner,window)&&candidate.IsVisible)return candidate;
        }
        return null;
    }
    // Runs one modal prompt, sends Escape once the dialog is up, and force-closes it if it ignored the key.
    static bool RunModalWithEscape(MainWindow window,Action action,out bool saw,out bool raised,out bool forced){
        bool found=false,keyed=false,closed=false,stop=false;
        var guard=new DispatcherTimer(DispatcherPriority.Normal){Interval=TimeSpan.FromMilliseconds(4000)};
        Action attempt=null;
        attempt=delegate{
            if(stop)return;
            Window dialog=OwnedDialog(window);
            var presentation=dialog==null?null:PresentationSource.FromVisual(dialog);
            if(dialog==null||presentation==null){Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,attempt);return;}
            found=true;
            try{dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,presentation,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});keyed=true;}catch(Exception){}
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,attempt);
        };
        guard.Tick+=delegate{guard.Stop();if(stop)return;var dialog=OwnedDialog(window);if(dialog!=null){try{dialog.Close();}catch(Exception){}}closed=true;};
        guard.Start();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,attempt);
        try{action();}finally{stop=true;guard.Stop();}
        saw=found;raised=keyed;forced=closed;
        return found&&keyed&&!closed;
    }

    // Same shape for the visible cancel button (the custom table prompt): the button must exist and close it.
    static bool RunModalWithCancel(MainWindow window,Action action,out bool saw,out bool clicked,out bool forced){
        bool found=false,hit=false,closed=false,stop=false;
        var guard=new DispatcherTimer(DispatcherPriority.Normal){Interval=TimeSpan.FromMilliseconds(4000)};
        Action attempt=null;
        attempt=delegate{
            if(stop)return;
            Window dialog=OwnedDialog(window);
            if(dialog==null||PresentationSource.FromVisual(dialog)==null){Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,attempt);return;}
            var button=FindCancelButton(dialog);
            if(button==null){Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,attempt);return;}
            found=true;
            try{button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));hit=true;}catch(Exception){}
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,attempt);
        };
        guard.Tick+=delegate{guard.Stop();if(stop)return;var dialog=OwnedDialog(window);if(dialog!=null){try{dialog.Close();}catch(Exception){}}closed=true;};
        guard.Start();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,attempt);
        try{action();}finally{stop=true;guard.Stop();}
        saw=found;clicked=hit;forced=closed;
        return found&&hit&&!closed;
    }
    static Button FindCancelButton(DependencyObject node){
        int count=VisualTreeHelper.GetChildrenCount(node);
        for(int i=0;i<count;i++){
            var child=VisualTreeHelper.GetChild(node,i);
            var button=child as Button;
            if(button!=null&&button.IsCancel)return button;
            var nested=FindCancelButton(child);
            if(nested!=null)return nested;
        }
        return null;
    }
    /* Bar item widths/heights plus the first submenu's row heights and widths (opened once, then closed). */
    static string BarMetrics(MainWindow window,Menu bar,List<double> widths,List<double> sub){
        var text=new StringBuilder();text.Append("bar[");
        MenuItem opener=null;
        foreach(object entry in bar.Items){
            var item=entry as MenuItem;if(item==null)continue;
            widths.Add(item.ActualWidth);widths.Add(item.ActualHeight);
            text.Append(item.ActualWidth.ToString("0.#")).Append('x').Append(item.ActualHeight.ToString("0.#")).Append(' ');
            if(opener==null&&item.Items.Count>0&&item.IsVisible)opener=item;
        }
        text.Append("] fs=").Append(bar.FontSize.ToString("0.#"));
        if(opener!=null){
            /* Rows are measured on their own (natural size, no popup needed): the template is applied and the row arranged at its desired size. */
            text.Append(" sub[");
            int taken=0;
            foreach(object entry in opener.Items){
                var row=entry as MenuItem;if(row==null)continue;
                row.ApplyTemplate();row.Measure(new Size(Double.PositiveInfinity,Double.PositiveInfinity));row.Arrange(new Rect(new Point(0,0),row.DesiredSize));row.UpdateLayout();
                sub.Add(row.DesiredSize.Width);sub.Add(row.DesiredSize.Height);
                text.Append(row.DesiredSize.Width.ToString("0.#")).Append('x').Append(row.DesiredSize.Height.ToString("0.#")).Append(' ');
                if(++taken>=6)break;
            }
            text.Append(']');
        }
        return text.ToString();
    }
    static void Check(ref bool all,StringBuilder report,string name,bool ok,string detail){
        all&=ok;
        report.Append(ok?"PASS ":"FAIL ").Append(name).Append(": ").Append(detail==null?"":detail.Replace('\r',' ').Replace('\n',' ')).Append('\n');
    }
    static string progressPath;
    static void Progress(string step){try{if(!String.IsNullOrEmpty(progressPath))File.AppendAllText(progressPath,step+" "+DateTime.Now.ToString("HH:mm:ss.fff")+"\r\n");}catch(Exception){}}
    static void RemoveFile(string path){try{if(File.Exists(path))File.Delete(path);}catch(Exception){}}
    static MainWindow NewWindow(){
        var window=new MainWindow {ShowInTaskbar=false,ShowActivated=false,Width=1000,Height=720};
        window.Show();
        window.Left=-20000;window.Top=-20000;
        window.UpdateLayout();
        Pump();
        return window;
    }
    // Every window this probe creates is thrown away: the tabs are marked saved first so Close cannot prompt.
    static void Discard(MainWindow window){
        object documents=typeof(MainWindow).GetField("documents",Members).GetValue(window);
        if(documents!=null)foreach(object tab in (IEnumerable)documents){
            var type=tab.GetType();
            var textField=type.GetField("Text",Members);
            var savedField=type.GetField("Saved",Members);
            var recoveredField=type.GetField("Recovered",Members);
            if(textField!=null&&savedField!=null)savedField.SetValue(tab,textField.GetValue(tab));
            if(recoveredField!=null)recoveredField.SetValue(tab,false);
        }
        /* text is a property (EditPipeline.cs), not a field: reading it as a field gave null, so the
           window stayed dirty once the replace checks had edited the body, and Close raised the save
           prompt and waited on a hidden desktop forever (the run that stopped after uifix-find.txt).
           The property getter is also what clears sourceSnapshotDirty, so it must be the one read. */
        var text=typeof(MainWindow).GetProperty("text",Members);
        var saved=typeof(MainWindow).GetField("saved",Members);
        if(text!=null&&saved!=null)saved.SetValue(window,text.GetValue(window,null));
        var recovered=typeof(MainWindow).GetField("recoveredDocument",Members);
        if(recovered!=null)recovered.SetValue(window,false);
    }
    static TabControl TabControlOf(MainWindow window){return (TabControl)typeof(MainWindow).GetField("documentTabs",Members).GetValue(window);}
    static object FieldOf(MainWindow window,string name){
        var field=typeof(MainWindow).GetField(name,Members);
        return field==null?null:field.GetValue(window);
    }
    static T FieldValue<T>(MainWindow window,string name) where T:class{return FieldOf(window,name) as T;}
    static void FieldSet(MainWindow window,string name,object value){
        var field=typeof(MainWindow).GetField(name,Members);
        if(field!=null)field.SetValue(window,value);
    }
    static object Call(MainWindow window,string name,params object[] args){
        var method=typeof(MainWindow).GetMethod(name,Members);
        return method==null?null:method.Invoke(window,args);
    }
    /* The path a help document really opens from: MainWindow.HelpFile itself, so the probe cannot
       drift from the menu again. The mirror below only runs if that method is ever renamed. */
    static string HelpDocument(string name){
        var method=typeof(MainWindow).GetMethod("HelpFile",Members);
        if(method!=null){
            try{
                string found=method.Invoke(null,new object[]{name}) as string;
                if(!String.IsNullOrEmpty(found))return found;
            }catch(TargetInvocationException){}
        }
        string folder=AppDomain.CurrentDomain.BaseDirectory;
        string inHelp=Path.Combine(Path.Combine(folder,"help"),name);
        return File.Exists(inHelp)?inHelp:Path.Combine(folder,name);
    }
    static string CountOf(MainWindow window){
        var label=FieldOf(window,"matchCount") as TextBlock;
        return label==null?"":label.Text;
    }
    static string Total(string count){
        if(count==null)return "";
        int slash=count.LastIndexOf('/');
        return slash<0?"":count.Substring(slash+1).Trim();
    }
    // A composition update as WPF raises it for a TextBox: the text is in the event, not in Text.
    static void Raise(TextBox box,RoutedEvent routedEvent,string text){
        var composition=new TextComposition(InputManager.Current,box,text);
        box.RaiseEvent(new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice,composition) {RoutedEvent=routedEvent});
    }
    static void Pump(){
        var frame=new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(delegate{frame.Continue=false;}));
        Dispatcher.PushFrame(frame);
    }
    // Waits real time at idle priority: Background-priority work (the search debounce) runs first.
    static void Pump(int milliseconds){
        var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.ApplicationIdle){Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=delegate{timer.Stop();frame.Continue=false;};
        timer.Start();
        Dispatcher.PushFrame(frame);
        Pump();
    }
}
