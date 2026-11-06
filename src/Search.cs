using System;
using System.Text;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Rx=System.Text.RegularExpressions;
namespace MDNote {
public partial class MainWindow {
    Menu topMenu; MenuItem darkToggle; Button quickFind,quickReplace,quickDark;
    readonly Border searchHost=new Border {Visibility=Visibility.Collapsed};
    /* b57: a vertical host around a WrapPanel. A horizontal StackPanel measures its children with an
       infinite width, so at the 360 px minimum window the 260 px replace field pushed both buttons past
       the right edge and "모두 바꾸기" could not be shown or clicked (UI bug hunt item 10). A vertical
       StackPanel hands its children the window width, and the wrap panel under it keeps every button on
       screen - on a second line when the three do not fit. The field stays a StackPanel because probes
       read it by name and cast it to that type. */
    readonly StackPanel replaceRow=new StackPanel {Orientation=Orientation.Vertical,Visibility=Visibility.Collapsed};
    readonly TextBox replacement=new TextBox {Width=200,Padding=new Thickness(5,3,5,3),Margin=new Thickness(6,3,6,3)};
    readonly CheckBox matchCase=new CheckBox {Content="대소문자 구분",VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,5,0)};
    /* The regular-expression option (user decision 2026-11-05): off by default and not saved. Beside the
       case option, same look and size. */
    readonly CheckBox matchRegex=new CheckBox {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(1,0,1,0)};
    readonly TextBlock matchCount=new TextBlock {Text="0 / 0",MinWidth=60,TextAlignment=TextAlignment.Right,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,6,0)};
    Button replaceOneButton,replaceAllButton;
    bool searchBusy;
    /* A Korean composition is not in TextBox.Text until it is committed, so a search built from
       query.Text alone lagged one syllable behind: the count and the highlight only caught up on
       commit. These hold the syllable the IME is still composing and the index it began at, and
       QueryText() answers with it inserted. Nothing here searches: the composition handlers only
       record the text and arm the existing debounce, so no document walk ever runs inside a
       composition callback. */
    string searchComposing="";int searchComposingStart=-1;
    /* The replace field composes in the same IMM context and its text lags the composition the
       same way, but nothing read the syllable: the replace buttons are Focusable=false
       (Editor.cs SetupButton), so clicking 모두 바꾸기 left the composition uncommitted and the
       document was written with the last syllable cut off (UI bug hunt 2026-10-31 item 1).
       These record it exactly like searchComposing, and ReplacementText() answers with it in
       place; Replace takes its value once per run, so every match of one run sees the same text. */
    string replaceComposing="";int replaceComposingStart=-1;
    TextBlock searchHint;
    string QueryText(){
        string text=query.Text;
        string composing=searchComposing;
        if(composing==null||composing.Length==0)return text;
        int at=searchComposingStart;
        if(at<0)at=0;
        if(at>text.Length)at=text.Length;
        /* The update event can arrive after the commit already put the same characters into Text:
           finding them at the start index means Text holds them and they must not be added twice. */
        if(at+composing.Length<=text.Length&&String.CompareOrdinal(text,at,composing,0,composing.Length)==0)return text;
        return text.Substring(0,at)+composing+text.Substring(at);
    }
    // The replace field's value with the syllable the IME is still composing, read the way
    // QueryText reads the find field's (including its "already committed" guard).
    string ReplacementText(){
        string text=replacement.Text;
        string composing=replaceComposing;
        if(composing==null||composing.Length==0)return text;
        int at=replaceComposingStart;
        if(at<0)at=0;
        if(at>text.Length)at=text.Length;
        if(at+composing.Length<=text.Length&&String.CompareOrdinal(text,at,composing,0,composing.Length)==0)return text;
        return text.Substring(0,at)+composing+text.Substring(at);
    }
    StringComparison Comparison {get{return matchCase.IsChecked==true?StringComparison.Ordinal:StringComparison.OrdinalIgnoreCase;}}
    Button SearchButton(Panel panel,string caption,Action action){var button=ActionButton(panel,caption,action);button.SetResourceReference(Control.BorderBrushProperty,"Line");button.Margin=new Thickness(6,0,0,0);CompactSearchButton(button);string key=OriginalKey(caption);button.ToolTip=T(key=="이전"?"이전 검색 결과":key=="다음"?"다음 검색 결과":key=="바꾸기"?"현재 항목 바꾸기":key=="모두 바꾸기"?"모든 일치 항목 바꾸기":key=="닫기"?"찾기/바꾸기 닫기 (Esc)":key);return button;}
    void CompactSearchButton(Button button){button.MinHeight=Ui.SquareSize;button.Height=Ui.SquareSize;button.VerticalAlignment=VerticalAlignment.Center;button.Padding=new Thickness(10,0,10,0);}
    void ToggleSearch(bool replace){if(searchHost.Visibility==Visibility.Visible&&(replaceRow.Visibility==Visibility.Visible)==replace)CloseSearch();else if(replace)ShowReplace();else ShowSearch();}
    void UpdateSearchToggles(){bool open=searchHost.Visibility==Visibility.Visible;quickFind.Tag=open&&replaceRow.Visibility!=Visibility.Visible?"Active":null;quickReplace.Tag=open&&replaceRow.Visibility==Visibility.Visible?"Active":null;}
    Button FindBarIcon(string icon,string tip,Action action){var button=new Button();SetupButton(button,"",action);button.Content=Ui.GridIcon(icon);Ui.Square(button);button.ToolTip=T(tip);System.Windows.Automation.AutomationProperties.SetName(button,T(tip));return button;}
    Button SearchIcon(Panel panel,string icon,string tip,Action action){var button=FindBarIcon(icon,tip,action);panel.Children.Add(button);return button;}
    // Docs / VS Code style field: grey placeholder instead of a left label, optional muted trailing text (the match count) inside the field at its right.
    Grid SearchField(TextBox field,string placeholder,TextBlock trailing){
        Ui.ToolbarText(field);field.SetResourceReference(Control.BackgroundProperty,"Paper");field.SetResourceReference(Control.ForegroundProperty,"SearchInk");field.SetResourceReference(Control.BorderBrushProperty,"Line");field.Width=Double.NaN;field.Height=28;field.Margin=new Thickness(0);field.Padding=new Thickness(6,0,trailing!=null?66:8,0);/* TextBoxView adds 2 px inside the 1 px frame: 6+2+1 = the placeholder's 9 px. */field.VerticalContentAlignment=VerticalAlignment.Center;
        var grid=new Grid {Width=260,Height=28,Margin=new Thickness(0,4,0,4)};grid.Children.Add(field);
        var hint=new TextBlock {Text=placeholder,Margin=new Thickness(9,0,0,0),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left,IsHitTestVisible=false};hint.SetResourceReference(TextBlock.ForegroundProperty,"SearchMuted");grid.Children.Add(hint);
        if(trailing!=null){trailing.MinWidth=0;trailing.Margin=new Thickness(0,0,9,0);trailing.HorizontalAlignment=HorizontalAlignment.Right;trailing.VerticalAlignment=VerticalAlignment.Center;trailing.IsHitTestVisible=false;trailing.FontSize=Ui.LabelSize;trailing.SetResourceReference(TextBlock.ForegroundProperty,"SearchMuted");grid.Children.Add(trailing);}
        if(field==query)searchHint=hint;field.TextChanged+=delegate{hint.Visibility=(field==query?QueryText():field.Text).Length==0?Visibility.Visible:Visibility.Collapsed;};return grid;
    }
    void BuildSearchBar(){
        /* find / replace as in Docs: no left labels, placeholders, the n / m count inside the find field, arrow buttons, an Aa toggle, the close icon on the right keyline (same line as the top bar menu icon) */
        search.Orientation=Orientation.Vertical;searchHost.Child=search;searchHost.Padding=new Thickness(8,0,0,0);/* b41: text boxes start on the left edge of the first top-row icon box */searchHost.BorderThickness=new Thickness(0,0,0,1);searchHost.SetResourceReference(Border.BackgroundProperty,"Ribbon");searchHost.SetResourceReference(Border.BorderBrushProperty,"Line");DockPanel.SetDock(searchHost,Dock.Top);root.Children.Add(searchHost);
        var row=new DockPanel {LastChildFill=true};search.Children.Add(row);var close=FindBarIcon("close","찾기/바꾸기 닫기 (Esc)",CloseSearch);/* b38: the x box, the outline box and the 원문 toggle border all end Ui.RightEdge px from the window edge (the ink sits centred in the boxes). b49: the x now uses the same 28 px box and the same 0.85 icon scale as the up/down buttons, not the 22 px top-row command icon */close.Margin=new Thickness(1,0,Ui.RightEdge,0);DockPanel.SetDock(close,Dock.Right);row.Children.Add(close);
        var controls=new WrapPanel {Orientation=Orientation.Horizontal};row.Children.Add(controls);controls.Children.Add(SearchField(query,"찾기",matchCount));
        var previousButton=SearchIcon(controls,"up","이전 검색 결과",delegate{FindNext(true);});previousButton.Margin=new Thickness(6,0,1,0);SearchIcon(controls,"down","다음 검색 결과",Find);
        /* b57: every other find-bar button is Focusable=false (SetupButton), so clicking Aa left the
           keyboard in the check box and the next typed letter never reached the query (UI bug hunt
           item 16). The mouse still toggles it; the field the user was typing in keeps the focus. */
        matchCase.Focusable=false;
        matchCase.Style=(Style)Resources["SquareToggle"];matchCase.Content=new TextBlock {Text="Aa",FontFamily=Ui.LabelFamily,FontSize=Ui.LabelSize+4,FontWeight=FontWeights.Normal,Tag=Ui.KeepFontTag,VerticalAlignment=VerticalAlignment.Center};matchCase.ToolTip=T("대소문자 구분");matchCase.Margin=new Thickness(1,0,1,0);matchCase.VerticalAlignment=VerticalAlignment.Center;controls.Children.Add(matchCase);
        matchRegex.Focusable=false;matchRegex.Style=(Style)Resources["SquareToggle"];matchRegex.Content=Ui.GridIcon("regex");matchRegex.ToolTip=T("정규식");System.Windows.Automation.AutomationProperties.SetName(matchRegex,T("정규식"));controls.Children.Add(matchRegex);
        search.Children.Add(replaceRow);var replaceWrap=new WrapPanel {Orientation=Orientation.Horizontal};replaceRow.Children.Add(replaceWrap);replaceWrap.Children.Add(SearchField(replacement,"바꿀 내용",null));replaceOneButton=SearchButton(replaceWrap,"바꾸기",ReplaceOne);replaceAllButton=SearchButton(replaceWrap,"모두 바꾸기",ReplaceAll);
        /* The two buttons cannot take the keyboard (SetupButton keeps them Focusable=false so the
           fields keep it), so their shortcuts are the way in and the tooltips have to name them
           (UI bug hunt 4 item 4). */
        replaceOneButton.ToolTip=T("현재 항목 바꾸기 (Enter, Alt+R)");replaceAllButton.ToolTip=T("모든 일치 항목 바꾸기 (Alt+A)");
        // A query edit is one field change, not a typing burst, so it refreshes at once (the count and the
        // highlight must follow the letters as before). A document edit is the burst path (SearchDocumentChanged).
        query.TextChanged+=delegate{QueryCompositionEnded();RefreshSearchNow();};System.Windows.Input.TextCompositionManager.AddPreviewTextInputStartHandler(query,QueryCompositionStarted);System.Windows.Input.TextCompositionManager.AddPreviewTextInputUpdateHandler(query,QueryCompositionUpdated);query.PreviewTextInput+=delegate{QueryCompositionEnded();};query.LostKeyboardFocus+=delegate{QueryCompositionEnded();};matchCase.Checked+=delegate{RefreshSearchNow();};matchCase.Unchecked+=delegate{RefreshSearchNow();};matchRegex.Checked+=delegate{RegexOptionChanged();};matchRegex.Unchecked+=delegate{RegexOptionChanged();};query.KeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.Enter){FindNext((System.Windows.Input.Keyboard.Modifiers&System.Windows.Input.ModifierKeys.Shift)!=0);e.Handled=true;}};
        // The replace field records its own composition so a replace run cannot write a cut-off syllable
        // (item 1); a commit, a caret move out of the field or a closed bar ends it, like the query's.
        replacement.TextChanged+=delegate{ReplaceCompositionEnded();};System.Windows.Input.TextCompositionManager.AddPreviewTextInputStartHandler(replacement,ReplaceCompositionStarted);System.Windows.Input.TextCompositionManager.AddPreviewTextInputUpdateHandler(replacement,ReplaceCompositionUpdated);replacement.PreviewTextInput+=delegate{ReplaceCompositionEnded();};replacement.LostKeyboardFocus+=delegate{ReplaceCompositionEnded();};
        /* A keyboard-only user could not run either replace command at all: the buttons never take
           their Tab stop (UI bug hunt 4 item 4). The keys live in the replace field, like Notepad's
           replace row, and an IME key or a syllable still composing is left to the composition. */
        replacement.KeyDown+=ReplaceFieldKey;
        // The cached map/matches live exactly as long as the document text does: a change only marks
        // them stale (b53 #12), the rebuild happens on the next query, find or count refresh.
        SyncSearchInvalidation();
        /* Esc closes the bar from either field, whichever spelling the window sees: the Korean IME
           reports its own keys as Key.ImeProcessed with the real key in ImeProcessedKey, so a query
           typed in Korean left the bar open (the window's Escape test only knew the plain key).
           The IME cancels a live composition with that same press, and the bar the user asked to
           close goes either way. */
        PreviewKeyDown+=SearchEscapeKey;
        /* The selection a closed find leaves behind answers the plain arrows like Notepad/Word do. */
        PreviewKeyDown+=SearchSelectionArrowKey;
        /* The debounce timer and the surface hooks hold this window alive; a closed window must drop them
           (InteractionLayout's Closed handler cannot know about them, Search.cs owns this state). */
        Closed+=delegate{StopSearchRefreshTimer();if(searchRefreshTimer!=null){searchRefreshTimer.Tick-=SearchRefreshTick;searchRefreshTimer=null;}if(searchHookSource!=null){searchHookSource.TextChanged-=SearchSourceChanged;searchHookSource=null;}};
    }
    void SearchEscapeKey(object sender,System.Windows.Input.KeyEventArgs e){
        if(e.Handled||search.Visibility!=Visibility.Visible)return;
        if(SearchKey(e.Key,e.ImeProcessedKey)!=System.Windows.Input.Key.Escape)return;
        /* b57: a popup opened over the bar takes this Escape first (UI bug hunt item 22). The size grid
           and the colour palette normally hold no keyboard focus of their own, and this is the first
           window Escape handler (BuildSearchBar runs before BuildColorControl, BuildTableControl and
           the window's own Esc branch), so without this test the bar closed underneath them and the
           popup stayed on screen with no key left to dismiss it. The event cannot simply be left
           unhandled for the popup's own handler: the window's Esc branch in Editor.cs closes the find
           bar for every Escape that arrives unhandled. So the popup is closed here and the bar keeps
           the next Escape. (A grid that itself took the keyboard answers Escape inside its popup.) */
        if(SearchPopupOwnsEscape()){e.Handled=true;return;}
        CloseSearch();e.Handled=true;
    }
    /* Closes the popup Escape belongs to and reports whether it did. The table grid is closed without the
       caret move its own Escape performs: the find field may still own the keyboard, and a jump into the
       body would send the next letters into the document (and end a live composition in the field). The
       Esc-closed flag is set by hand for the same reason the grid's own Escape sets it - the pointer still
       rests on the 표 button and a 2 px move must not reopen the grid. */
    bool SearchPopupOwnsEscape(){
        if(tablePickerMenu!=null&&tablePickerMenu.IsOpen){CloseTablePicker(false);tablePickerEscClosed=true;return true;}
        if(colorPopup.IsOpen){colorPopup.IsOpen=false;return true;}
        return false;
    }
    // The key behind the event: an IME-handled key reports Key.ImeProcessed and keeps the real key
    // in ImeProcessedKey, and every other spelling already is that key. Internal so the window
    // probe can hold the mapping itself, since a synthesized event cannot carry an IME one.
    internal static System.Windows.Input.Key SearchKey(System.Windows.Input.Key key,System.Windows.Input.Key imeKey){
        return key==System.Windows.Input.Key.ImeProcessed?imeKey:key;
    }
    /* Composition callbacks only record the syllable and arm the debounce; the search itself runs
       from SearchRefreshTick, never on this turn (a basic-view walk here would rebuild the body
       between two keystrokes). */
    void QueryCompositionStarted(object sender,System.Windows.Input.TextCompositionEventArgs args){
        if(args.Text==null||args.Text.Length==0)return;
        searchComposing=args.Text;searchComposingStart=Math.Max(0,query.SelectionStart);UpdateQueryHint();ScheduleSearchRefresh();
    }
    void QueryCompositionUpdated(object sender,System.Windows.Input.TextCompositionEventArgs args){
        if(args.Text==null||args.Text.Length==0)return;
        if(searchComposing.Length==0||searchComposingStart<0)searchComposingStart=Math.Max(0,query.SelectionStart);
        searchComposing=args.Text;UpdateQueryHint();ScheduleSearchRefresh();
    }
    /* A commit, a caret move out of the field or a closed bar ends the composition: the committed
       characters are in Text by then (or never will be), and holding the syllable would count it twice. */
    void QueryCompositionEnded(){
        if(searchComposing.Length==0)return;
        searchComposing="";searchComposingStart=-1;UpdateQueryHint();
    }
    // The placeholder hides while the field holds text, and a composing first syllable is text to the reader.
    void UpdateQueryHint(){if(searchHint!=null)searchHint.Visibility=QueryText().Length==0?Visibility.Visible:Visibility.Collapsed;}
    /* The replace field's composition, recorded the way the query's is recorded above. Nothing here
       searches or replaces: a replace run reads the composed value once, on its own turn. */
    void ReplaceCompositionStarted(object sender,System.Windows.Input.TextCompositionEventArgs args){
        if(args.Text==null||args.Text.Length==0)return;
        replaceComposing=args.Text;replaceComposingStart=Math.Max(0,replacement.SelectionStart);
    }
    void ReplaceCompositionUpdated(object sender,System.Windows.Input.TextCompositionEventArgs args){
        if(args.Text==null||args.Text.Length==0)return;
        if(replaceComposing.Length==0||replaceComposingStart<0)replaceComposingStart=Math.Max(0,replacement.SelectionStart);
        replaceComposing=args.Text;
    }
    void ReplaceCompositionEnded(){
        if(replaceComposing.Length==0)return;
        replaceComposing="";replaceComposingStart=-1;
    }
    /* Notepad's replace row runs from the keyboard (UI bug hunt 4 item 4): Enter replaces the
       current match and steps to the next, Alt+R repeats that, Alt+A (or Ctrl+Alt+Enter) replaces
       every match. A key the IME still owns (VK_PROCESSKEY) or one arriving while the field holds a
       composing syllable belongs to the composition - Enter commits the syllable and never runs a
       replace. Shift+Enter and other modified Enters keep the field's own behaviour. */
    void ReplaceFieldKey(object sender,System.Windows.Input.KeyEventArgs e){
        if(e.Handled||replaceComposing.Length>0)return;
        if(e.Key==System.Windows.Input.Key.ImeProcessed)return;
        System.Windows.Input.Key key=e.Key==System.Windows.Input.Key.System?e.SystemKey:e.Key;
        System.Windows.Input.ModifierKeys modifiers=ShortcutModifiersProbe.HasValue?ShortcutModifiersProbe.Value:System.Windows.Input.Keyboard.Modifiers;
        bool control=(modifiers&System.Windows.Input.ModifierKeys.Control)!=0;
        bool shift=(modifiers&System.Windows.Input.ModifierKeys.Shift)!=0;
        if((modifiers&System.Windows.Input.ModifierKeys.Alt)!=0){
            if(key==System.Windows.Input.Key.A||(key==System.Windows.Input.Key.Enter&&control)){e.Handled=true;ReplaceAll();}
            else if(key==System.Windows.Input.Key.R){e.Handled=true;ReplaceOne();}
            return;
        }
        if(key!=System.Windows.Input.Key.Enter||control||shift)return;
        e.Handled=true;ReplaceOne();
    }
    /* A run that was started without a preview event (the field composes in the window's IMM context)
       reads the context once, only while the replace field owns the keyboard. */
    void SyncReplaceCompositionFromIme(){
        if(replaceComposing.Length>0)return;
        if(replacement==null||!replacement.IsKeyboardFocusWithin)return;
        string text=QueryCompositionString(replacement);
        if(String.IsNullOrEmpty(text))return;
        replaceComposing=text;replaceComposingStart=Math.Max(0,replacement.SelectionStart);
    }
    /* WPF raises the composition preview events for a TextBox, but the editor surface composes in the
       window's IMM context instead (SourceEditor). If a run ever gets no preview event for a focused
       find field, the debounce tick reads the window composition string here. Only the tick calls
       this: never a keystroke path, and only while the find field owns the keyboard. */
    void SyncQueryCompositionFromIme(){
        if(searchComposing.Length>0)return;
        if(query==null||!query.IsKeyboardFocusWithin)return;
        string text=QueryCompositionString(query);
        if(String.IsNullOrEmpty(text))return;
        searchComposing=text;searchComposingStart=Math.Max(0,query.SelectionStart);UpdateQueryHint();
    }
    static string QueryCompositionString(UIElement owner){
        var window=PresentationSource.FromVisual(owner) as System.Windows.Interop.HwndSource;
        if(window==null)return null;
        IntPtr context=SearchImmGetContext(window.Handle);
        if(context==IntPtr.Zero)return null;
        try{
            int bytes=SearchImmGetCompositionStringW(context,0x0008,IntPtr.Zero,0);
            if(bytes<=0)return null;
            IntPtr buffer=System.Runtime.InteropServices.Marshal.AllocHGlobal(bytes);
            try{
                int read=SearchImmGetCompositionStringW(context,0x0008,buffer,bytes);
                if(read<=0)return null;
                string value=System.Runtime.InteropServices.Marshal.PtrToStringUni(buffer,read/2);
                if(value!=null&&value.Length>0&&value[value.Length-1]=='\0')value=value.Substring(0,value.Length-1);
                return value;
            }finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);}
        }finally{SearchImmReleaseContext(window.Handle,context);}
    }
    [System.Runtime.InteropServices.DllImport("imm32.dll",EntryPoint="ImmGetContext")]static extern IntPtr SearchImmGetContext(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("imm32.dll",EntryPoint="ImmReleaseContext")]static extern bool SearchImmReleaseContext(IntPtr hWnd,IntPtr hIMC);
    [System.Runtime.InteropServices.DllImport("imm32.dll",EntryPoint="ImmGetCompositionStringW",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]static extern int SearchImmGetCompositionStringW(IntPtr hIMC,int index,IntPtr buffer,int length);
    void ShowSearch(){OpenSearch(false);}
    void ShowReplace(){if(!editing){SetMode(true);/* The large-document question can answer "cancel"; do not open the replace row over a read-mode surface. */if(!editing)return;}OpenSearch(true);}/* b44: no pre-fill from the selection (user request) */
    void OpenSearch(bool replace){DisarmSearchSelectionCollapse();search.Visibility=Visibility.Visible;searchHost.Visibility=Visibility.Visible;replaceRow.Visibility=replace?Visibility.Visible:Visibility.Collapsed;UpdateSearchToggles();RefreshSearchNow();query.Focus();query.SelectAll();}
    void CloseSearch(){
        /* The overlay and the count are dropped below, so the match they describe is read here,
           while the bar still owns that state. */
        int keepStart,keepLength;bool keep=SearchMatchOnClose(out keepStart,out keepLength);
        QueryCompositionEnded();ReplaceCompositionEnded();StopSearchRefreshTimer();CancelParallelSearch();SearchCacheReset();
        search.Visibility=Visibility.Collapsed;searchHost.Visibility=Visibility.Collapsed;UpdateSearchToggles();UpdateSearchHighlight(null,-1);
        source.Focus();
        /* Closing does not drop the found text: it stays the editor's real selection, so typing
           replaces it and a plain arrow acts on it as in Notepad (SearchSelectionArrowKey). Enter
           already selected it; a match only the overlay drew is selected here. */
        if(keep&&source!=null&&source.Document!=null&&keepStart>=0&&keepStart+keepLength<=source.Document.TextLength){
            if(source.SelectionStart!=keepStart||source.SelectionLength!=keepLength)source.Select(keepStart,keepLength);
            ArmSearchSelectionCollapse(keepStart,keepLength);
        }else DisarmSearchSelectionCollapse();
        /* The editor's own window-level IME hook (SourceEditor.ImeProc) sees every WM_IME_* of this
           HwndSource, the find field's compositions included, and counts their result text. That
           text never reaches the TextArea, so the latch stayed set and the store guard skipped
           every Ctrl+Z after Esc (real input 2026-10-31: the z typed into the body after closing
           the bar could not be undone, so it was saved). The focus is back in the body and the
           field's composition was cancelled or committed when it lost focus, so a latch with no
           composition string left in the context is not the editor's own: drop it. A composition
           the context still holds is left alone, exactly as the command guard expects. */
        if(source!=null&&source.ImeBusy&&!source.ImeCompositionActive())source.AbandonComposition("find closed");
    }
    /* The current match as a source range while the bar is still open. Enter already selected it
       (FindNext -> SelectMatch); a match only the overlay drew is found from the cached map, or
       from the highlight's own source range, which the visible-text path maps back with the
       hidden markers outside the match excluded. False when no match is current (no find was
       pressed, or the caret was moved off it): a bar being closed must not move the caret. */
    bool SearchMatchOnClose(out int start,out int length){
        start=-1;length=0;
        if(searchHost==null||searchHost.Visibility!=Visibility.Visible)return false;
        if(source==null||source.Document==null||QueryText().Length==0)return false;
        /* A cache the document has outrun cannot name the match the overlay shows. */
        bool described=false;
        if(searchCacheMap!=null&&searchCacheMatches!=null&&!searchTextStale&&Object.ReferenceEquals(searchCacheSource,source.Document)&&Object.ReferenceEquals(searchCacheVersion,source.Document.Version)&&searchCacheQuery==QueryText()&&searchCacheComparison==Comparison){
            described=true;
            int current=Current(searchCacheMap,searchCacheMatches);
            if(current>=0)SourceVisibleRange(searchCacheMap,searchCacheMatches[current],out start,out length);
        }
        if(start<0&&described&&sourceHighlight!=null&&sourceHighlight.SourceStart>=0&&sourceHighlight.SourceLength>0){
            start=sourceHighlight.SourceStart;length=sourceHighlight.SourceLength;
        }
        return start>=0&&length>0&&start+length<=source.Document.TextLength;
    }
    /* Notepad/Word rule for the selection a closed find leaves behind: plain Left collapses it
       to its start and plain Right to its end. AvalonEdit's own rule (CaretNavigationCommandHandler
       OnMoveCaret) clears the selection and then steps once from the caret; after a find the caret
       sits at the match end, so the first Left walked into the found word and the next typed
       characters landed after it (real input 2026-10-31). Only that exact selection is taken, only
       while the editor owns the keyboard, and only with no modifier: Shift/Ctrl/Alt arrows keep
       their own meaning, and a table keeps its own cell-edge rule for every other selection. */
    int searchCollapseStart=-1,searchCollapseLength;ICSharpCode.AvalonEdit.Document.TextDocument searchCollapseDocument;
    void ArmSearchSelectionCollapse(int start,int length){searchCollapseStart=start;searchCollapseLength=length;searchCollapseDocument=source==null?null:source.Document;}
    void DisarmSearchSelectionCollapse(){searchCollapseStart=-1;searchCollapseLength=0;searchCollapseDocument=null;}
    void SearchSelectionArrowKey(object sender,System.Windows.Input.KeyEventArgs e){
        if(e.Handled||searchCollapseStart<0||source==null)return;
        if(source.Document==null||!Object.ReferenceEquals(searchCollapseDocument,source.Document)){DisarmSearchSelectionCollapse();return;}
        if(!source.TextArea.IsKeyboardFocusWithin)return;
        System.Windows.Input.Key key=SearchKey(e.Key,e.ImeProcessedKey);
        if(key!=System.Windows.Input.Key.Left&&key!=System.Windows.Input.Key.Right)return;
        if((System.Windows.Input.Keyboard.Modifiers&(System.Windows.Input.ModifierKeys.Shift|System.Windows.Input.ModifierKeys.Control|System.Windows.Input.ModifierKeys.Alt))!=0)return;
        if(source.ImeBusy||source.ImeCompositionActive())return;
        int start=searchCollapseStart,length=searchCollapseLength;
        DisarmSearchSelectionCollapse();
        if(source.SelectionStart!=start||source.SelectionLength!=length)return;
        e.Handled=true;
        source.Select(key==System.Windows.Input.Key.Left?start:start+length,0);
        /* A match spanning more lines may end off screen; the caret that moved to the selection's
           start (or end) has to stay visible, as AvalonEdit's own arrow movement would. */
        MarkdownLivePreview.Session.SafeBringCaretToView(source.TextArea);
    }
    sealed class SearchMap {
        string value;internal ICSharpCode.AvalonEdit.Document.ITextSource Snapshot;internal string Text {get{if(value==null&&Snapshot!=null)value=Snapshot.Text;return value??"";}set{this.value=value;}}
        // A live surface that draws no markup searches its visible text: the map holds the
        // characters the reader sees and SourceOffsets turns a visible index back into the source
        // offset the highlight, the selection and Replace use. Null everywhere else, where the map
        // text is the document text and an index already is a source offset.
        internal int[] SourceOffsets;
        // A regular-expression search keeps the length of every match beside its start (Starts is the
        // match list itself, sorted and strictly increasing); a plain search leaves both null and every
        // match is as long as the query.
        internal List<int> Starts,Lengths;
        internal int VisibleIndex(int sourceOffset){
            if(SourceOffsets==null)return sourceOffset;
            int low=0,high=SourceOffsets.Length,mid;
            while(low<high){mid=low+high>>1;if(SourceOffsets[mid]<sourceOffset)low=mid+1;else high=mid;}
            return low;
        }
    }
    // The live surface can draw the text without its marker runs (Settings.HideMarkers, or read
    // mode, which never reveals markup). Find/Replace then works on the visible text and maps a
    // match back to its source range; every other surface keeps the raw-source search.
    bool LiveVisibleSearch {get{return CodeMode&&LivePreviewAttached&&livePreview.SearchHiddenMarkup;}}
    SearchMap Map(){var map=new SearchMap();if(LargeSearchCurrent())return parallelSearchMap;if(LiveVisibleSearch){int[] offsets;map.Text=livePreview.VisibleText(out offsets);map.SourceOffsets=offsets;}else map.Text=source.Text;return map;}
    List<int> Matches(SearchMap map){if(Object.ReferenceEquals(map,parallelSearchMap)&&LargeSearchCurrent())return parallelSearchMatches;var matches=new List<int>();string needle=QueryText();if(needle.Length==0)return matches;if(RegexOn)return RegexMatches(map,needle);int at=0;while(at<=map.Text.Length-needle.Length){int found=map.Text.IndexOf(needle,at,Comparison);if(found<0)break;matches.Add(found);at=found+needle.Length;}return matches;}
    int SelectedStart(SearchMap map){return map.SourceOffsets==null?source.SelectionStart:map.VisibleIndex(source.SelectionStart);}
    int SelectedEnd(SearchMap map){int end=source.SelectionStart+source.SelectionLength;return map.SourceOffsets==null?end:map.VisibleIndex(end);}
    int Current(SearchMap map,List<int> matches){int start=SelectedStart(map),end=SelectedEnd(map);if(map.Lengths==null){if(end-start!=QueryText().Length)return -1;int plain=matches.BinarySearch(start);return plain>=0?plain:-1;}
        /* A regex match has its own length, and a zero-length one (^, \b) is "selected" by the bare caret. */
        int found=matches.BinarySearch(start);if(found<0||found>=map.Lengths.Count)return -1;return map.Lengths[found]==end-start?found:-1;}
    int MatchLength(SearchMap map,int start){
        if(map==null||map.Lengths==null||map.Starts==null)return QueryText().Length;
        int at=map.Starts.BinarySearch(start);
        return at>=0&&at<map.Lengths.Count?map.Lengths[at]:0;
    }
    bool RegexOn {get{return matchRegex.IsChecked==true;}}
    /* The compiled regex survives until the pattern or the options change: a document edit, a find press
       or a refresh reads it from here and never builds one (an invalid pattern is remembered the same way,
       so a refresh does not parse it again). Built and read on the UI thread only; a background scan gets
       the Regex object itself, which is safe to share. */
    Rx.Regex searchRegexCache;string searchRegexPattern;Rx.RegexOptions searchRegexOptions;bool searchRegexInvalid,searchRegexTimedOut;
    Rx.RegexOptions SearchRegexOptionsNow(){return Rx.RegexOptions.Multiline|Rx.RegexOptions.CultureInvariant|(matchCase.IsChecked==true?Rx.RegexOptions.None:Rx.RegexOptions.IgnoreCase);}
    internal Rx.Regex SearchRegex(string pattern,Rx.RegexOptions options){
        if(searchRegexPattern!=pattern||searchRegexOptions!=options||(searchRegexCache==null&&!searchRegexInvalid)){
            searchRegexPattern=pattern;searchRegexOptions=options;searchRegexCache=null;searchRegexInvalid=false;
            try{searchRegexCache=new Rx.Regex(AdaptPatternForCrLf(pattern),options,TimeSpan.FromSeconds(2));}
            catch(ArgumentException){searchRegexInvalid=true;}
        }
        return searchRegexCache;
    }
    /* A CRLF document keeps its CR in the editor text, and .NET's multiline $ stops only before LF and its
       . takes the CR: "abc$" found nothing and ".*" swallowed the CR (a replace then ate the line break).
       Outside a character class and an escape, $ becomes (?=\r?$) and . becomes [^\r\n], so a pattern
       reads the line the way the reader sees it. Everything else is passed through untouched. */
    internal static string AdaptPatternForCrLf(string pattern){
        if(pattern.IndexOf('$')<0&&pattern.IndexOf('.')<0)return pattern;
        var adapted=new StringBuilder(pattern.Length+16);bool inClass=false;
        for(int i=0;i<pattern.Length;i++){
            char c=pattern[i];
            if(c=='\\'){adapted.Append(c);if(i+1<pattern.Length){i++;adapted.Append(pattern[i]);}continue;}
            if(inClass){if(c==']')inClass=false;adapted.Append(c);continue;}
            if(c=='['){
                inClass=true;adapted.Append(c);int j=i+1;
                if(j<pattern.Length&&pattern[j]=='^'){adapted.Append('^');j++;}
                if(j<pattern.Length&&pattern[j]==']'){adapted.Append(']');j++;}
                i=j-1;continue;
            }
            if(c=='$'){adapted.Append("(?=\\r?$)");continue;}
            if(c=='.'){adapted.Append("[^\\r\\n]");continue;}
            adapted.Append(c);
        }
        return adapted.ToString();
    }
    // Every match of a scan as a start and a length. A zero-length match (^, \b) is a match too: the
    // enumeration steps past it by itself, so neither a find nor a replace run can loop on it.
    static void ScanRegex(Rx.Regex regex,string text,List<int> starts,List<int> lengths,System.Threading.CancellationToken token){
        for(Rx.Match found=regex.Match(text);found.Success;found=found.NextMatch()){token.ThrowIfCancellationRequested();starts.Add(found.Index);lengths.Add(found.Length);}
    }
    // The match list of a regex search. An invalid pattern or a timeout is an empty list, never an exception.
    List<int> RegexMatches(SearchMap map,string pattern){
        var starts=new List<int>();var lengths=new List<int>();
        Rx.Regex regex=SearchRegex(pattern,SearchRegexOptionsNow());searchRegexTimedOut=false;
        if(regex!=null){
            try{ScanRegex(regex,map.Text,starts,lengths,System.Threading.CancellationToken.None);}
            catch(Rx.RegexMatchTimeoutException){starts.Clear();lengths.Clear();searchRegexTimedOut=true;}
        }
        map.Starts=starts;map.Lengths=lengths;return starts;
    }
    /* The option changed: nothing built for the other kind of search may answer this one. */
    void RegexOptionChanged(){CancelParallelSearch();SearchCacheReset();RefreshSearchNow();}
    // The find field says why a regex finds nothing: only an invalid pattern is named.
    void UpdateRegexTip(){
        bool invalid=false;
        if(RegexOn){string pattern=QueryText();if(pattern.Length>0){SearchRegex(pattern,SearchRegexOptionsNow());invalid=searchRegexInvalid;}}
        object tip=invalid?T("정규식이 올바르지 않습니다"):null;
        if(!Object.Equals(query.ToolTip,tip))query.ToolTip=tip;
    }
    // The length of the i-th match of the list that was built with this map (the query's own for a plain search).
    static int LenAt(SearchMap map,int index,int plainLength){return map.Lengths==null?plainLength:map.Lengths[index];}
    /* Replace One: the replacement for the match that starts at `start`, with $1, ${name}, $$ and $0
       read against that match. Null when the match cannot be reproduced (a timeout, or a pattern that
       no longer matches there): nothing is written then. A plain search, and a replacement with no
       "$", is the text as typed. */
    string RegexValueAt(SearchMap map,int start,string replaceWith){
        if(map==null||map.Lengths==null||replaceWith.IndexOf('$')<0)return replaceWith;
        Rx.Regex regex=SearchRegex(QueryText(),SearchRegexOptionsNow());if(regex==null)return null;
        try{
            Rx.Match found=regex.Match(map.Text,start);
            if(!found.Success||found.Index!=start||found.Length!=MatchLength(map,start))return null;
            return found.Result(replaceWith);
        }catch(Rx.RegexMatchTimeoutException){return null;}
    }
    /* Replace All: the substitution of every match, taken in one more scan of the same text (the scan is
       deterministic, so it meets the same matches in the same order). Null when every match simply gets
       the text as typed (a plain search, or no "$" in the replacement); failed when the scan cannot be
       reproduced, and then nothing is written. */
    string[] RegexValues(SearchMap map,List<int> matches,string replaceWith,out bool failed){
        failed=false;
        if(map.Lengths==null||replaceWith.IndexOf('$')<0)return null;
        Rx.Regex regex=SearchRegex(QueryText(),SearchRegexOptionsNow());
        if(regex==null){failed=true;return null;}
        var values=new string[matches.Count];int index=0;
        try{
            for(Rx.Match found=regex.Match(map.Text);found.Success&&index<values.Length;found=found.NextMatch()){
                if(found.Index!=matches[index]||found.Length!=map.Lengths[index]){failed=true;return null;}
                values[index++]=found.Result(replaceWith);
            }
        }catch(Rx.RegexMatchTimeoutException){failed=true;return null;}
        if(index!=values.Length){failed=true;return null;}
        return values;
    }
    /* A document past 32K chars is searched off the UI thread from a snapshot, as the plain search does
       (SourceSearch.cs); the regex scan is one pass over the snapshot text because a match may span any
       two lines. Same state, same 100 ms start delay, same "searching" count and the same pending
       find-next direction; only the scan and the per-match lengths differ. */
    bool RefreshLargeRegexSearch(){
        if(!LargeSourceSearch){if(parallelSearchCancellation!=null)CancelParallelSearch();return false;}
        if(QueryText().Length==0){CancelParallelSearch();matchCount.Text="0 / 0";UpdateSearchHighlight(null,-1);replaceOneButton.IsEnabled=replaceAllButton.IsEnabled=false;return true;}
        if(LargeSearchCurrent()){DisplayLargeRegex();return true;}
        if(parallelSearchCancellation!=null&&parallelSearchDocument==source.Document&&Object.ReferenceEquals(parallelSearchVersion,source.Document.Version)&&parallelSearchQuery==QueryText()&&parallelSearchComparison==Comparison)return true;
        int direction=pendingSearchDirection;CancelParallelSearch();pendingSearchDirection=direction;
        parallelSearchDocument=source.Document;parallelSearchVersion=source.Document.Version;parallelSearchQuery=QueryText();parallelSearchComparison=Comparison;
        parallelSearchCancellation=new System.Threading.CancellationTokenSource();
        Rx.Regex regex=SearchRegex(parallelSearchQuery,SearchRegexOptionsNow());
        ICSharpCode.AvalonEdit.Document.ITextSource snapshot=source.Document.CreateSnapshot();
        if(regex==null){
            /* An invalid pattern has nothing to scan: answer at once with an empty list. */
            var none=new List<int>();parallelSearchMap=new SearchMap {Snapshot=snapshot,Starts=none,Lengths=new List<int>()};parallelSearchMatches=none;
            DisplayLargeRegex();return true;
        }
        StartLargeRegexSearch(parallelSearchCancellation,snapshot,regex);
        return true;
    }
    async void StartLargeRegexSearch(System.Threading.CancellationTokenSource cancellation,ICSharpCode.AvalonEdit.Document.ITextSource snapshot,Rx.Regex regex){
        System.Threading.CancellationToken token=cancellation.Token;matchCount.Text=T("검색 중…");replaceOneButton.IsEnabled=replaceAllButton.IsEnabled=false;UpdateSearchHighlight(null,-1);
        try{
            await System.Threading.Tasks.Task.Delay(100,token);
            var result=await System.Threading.Tasks.Task.Run(delegate{
                token.ThrowIfCancellationRequested();
                var starts=new List<int>();var lengths=new List<int>();
                try{ScanRegex(regex,snapshot.Text,starts,lengths,token);}
                catch(Rx.RegexMatchTimeoutException){starts.Clear();lengths.Clear();}
                return new Tuple<SearchMap,List<int>>(new SearchMap {Snapshot=snapshot,Starts=starts,Lengths=lengths},starts);
            },token);
            if(token.IsCancellationRequested||searchWindowClosed||parallelSearchCancellation!=cancellation||searchHost.Visibility!=Visibility.Visible||!CodeMode||parallelSearchDocument!=source.Document||!Object.ReferenceEquals(parallelSearchVersion,source.Document.Version))return;
            parallelSearchMap=result.Item1;parallelSearchMatches=result.Item2;DisplayLargeRegex();
            int direction=pendingSearchDirection;pendingSearchDirection=0;if(direction!=0)FindNext(direction<0);
        }catch(OperationCanceledException){}
        catch(Exception){if(!token.IsCancellationRequested&&!searchWindowClosed&&parallelSearchCancellation==cancellation)matchCount.Text=T("검색 실패");}
    }
    void DisplayLargeRegex(){
        int current=Current(parallelSearchMap,parallelSearchMatches);matchCount.Text=(current+1)+" / "+parallelSearchMatches.Count;
        if(current<0)UpdateSearchHighlightAll(parallelSearchMap,parallelSearchMatches);else ShowSearchHighlight(parallelSearchMap,parallelSearchMatches[current]);
        replaceOneButton.IsEnabled=replaceAllButton.IsEnabled=editing&&parallelSearchMatches.Count>0;
    }
    bool DeferLargeRegexNavigation(bool previous){if(!LargeSourceSearch||LargeSearchCurrent())return false;pendingSearchDirection=previous?-1:1;RefreshLargeRegexSearch();return true;}
    // Find-next used to rebuild the whole mapping (Map()) and rescan the whole text (Matches()) on every press, and
    // again from the RefreshSearch() after the selection: three O(document) TextPointer walks per Enter, 170 ms-1.6 s
    // in the basic view (--longline-surfaces-bench). The map and its match list now survive until the query, the case
    // rule, the surface version or the document changes; the selected match is tracked by comparing TextPointers, so
    // repeated presses do no offset mapping at all.
    SearchMap searchCacheMap;List<int> searchCacheMatches;string searchCacheQuery;StringComparison searchCacheComparison;
    ICSharpCode.AvalonEdit.Document.TextDocument searchCacheSource;object searchCacheVersion;
    /* b53 #12: the map/matches describe the text they were built from, so any edit makes them wrong. The
       invalidation hook was bound once, in BuildSearchBar, to the source this window was constructed with;
       Workspace.SwitchTab replaces source when a tab is created or switched to, and a TextDocument keeps
       its identity across edits, so the cache's "same document" test still said current. The hook now
       follows the live surface, and a change marks the cache stale instead of relying on document identity.
       Clearing is O(1); the rebuild is a whole-document walk, so it happens after a typing pause. */
    bool searchTextStale,searchTextPending;
    System.Windows.Threading.DispatcherTimer searchRefreshTimer;
    const int SearchRefreshDelay=250;
    int searchRefreshRetries;
    SourceEditor searchHookSource;
    void SearchCacheReset(){searchCacheMap=null;searchCacheMatches=null;searchTextStale=true;}
    // A tab switch swaps source (Workspace.cs calls this after the swap), so the listener has to move
    // with it; every search entry point also re-syncs, which covers a swap this missed.
    void SyncSearchInvalidation(){
        if(!Object.ReferenceEquals(searchHookSource,source)){if(searchHookSource!=null)searchHookSource.TextChanged-=SearchSourceChanged;searchHookSource=source;if(searchHookSource!=null)searchHookSource.TextChanged+=SearchSourceChanged;}
    }
    void SearchSourceChanged(object sender,EventArgs args){SearchDocumentChanged();}
    void SearchDocumentChanged(){SearchCacheReset();if(loading||applyingLoadedFile||searchWindowClosed)return;searchTextPending=true;ScheduleSearchRefresh();}
    // While the bar is open the count follows the typing with a short pause: a rebuild per keystroke would walk
    // the whole document, which is exactly what the cache exists to avoid. A query edit does not come here: it
    // is already one field change, and the existing synchronous refresh answers it.
    void ScheduleSearchRefresh(){
        if(loading||applyingLoadedFile||searchWindowClosed||searchHost.Visibility!=Visibility.Visible)return;
        if(searchRefreshTimer==null){searchRefreshTimer=new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background){Interval=TimeSpan.FromMilliseconds(SearchRefreshDelay)};searchRefreshTimer.Tick+=SearchRefreshTick;}
        searchRefreshTimer.Stop();searchRefreshRetries=0;searchRefreshTimer.Start();
    }
    void SearchRefreshTick(object sender,EventArgs args){
        searchRefreshTimer.Stop();
        SyncQueryCompositionFromIme();
        /* "pause", not "now": this is the refresh a typing pause owes, and for a large basic-view body it
           must not walk the whole document on this turn (see StartRichSearch). */
        if(RefreshSearchCore("pause"))return;
        // A load still in flight (or a bar being closed) held the rebuild off: retry briefly, then leave the
        // change owed for the next search entry instead of polling forever.
        if(searchTextPending&&searchRefreshRetries++<8&&!searchWindowClosed&&searchHost.Visibility==Visibility.Visible){searchRefreshTimer.Start();return;}
        searchTextPending=false;
    }
    void StopSearchRefreshTimer(){if(searchRefreshTimer!=null&&searchRefreshTimer.IsEnabled)searchRefreshTimer.Stop();searchTextPending=false;}
    bool SearchCacheCurrent(){
        SyncSearchInvalidation();
        if(searchTextStale||searchCacheMap==null||searchCacheMatches==null)return false;
        if(searchCacheQuery!=QueryText()||searchCacheComparison!=Comparison)return false;
        /* The map's kind follows the surface: toggling the no-reveal setting (or entering read
           mode) keeps the document version but changes which text the user is looking at. */
        if((searchCacheMap.SourceOffsets!=null)!=LiveVisibleSearch)return false;
        if(!Object.ReferenceEquals(searchCacheSource,source.Document)||!Object.ReferenceEquals(searchCacheVersion,source.Document.Version))return false;
        return true;
    }
    SearchMap SearchMapCached(){
        if(SearchCacheCurrent())return searchCacheMap;
        var map=Map();searchCacheMap=map;searchCacheMatches=Matches(map);searchCacheQuery=QueryText();searchCacheComparison=Comparison;
        searchCacheSource=source.Document;searchCacheVersion=source.Document.Version;
        searchTextStale=false;searchTextPending=false;
        if(searchRefreshTimer!=null&&searchRefreshTimer.IsEnabled)searchRefreshTimer.Stop();
        return map;
    }
    List<int> SearchMatchesCached(SearchMap map){return Object.ReferenceEquals(map,searchCacheMap)&&searchCacheMatches!=null?searchCacheMatches:Matches(map);}
    int SearchCurrentCached(SearchMap map,List<int> matches){return Current(map,matches);}
    bool SelectMatch(SearchMap map,int start){
        int at,length;SourceVisibleRange(map,start,out at,out length);
        /* A range the clamped map cannot place in this document is not a match: selecting it would
           throw out of the find press (TextEditor.Select checks both ends). */
        if(length<0||(length==0&&map.Lengths==null)||at<0||at>source.Document.TextLength||at+length>source.Document.TextLength)return false;
        source.Select(at,length);source.ScrollToLine(source.GetLineIndexFromCharacterIndex(at));
        /* ScrollToLine goes to the logical line start, which on a >16K line is the top of the folded prefix; this puts a match that sits inside a fold back into real text, on screen. */LongLineWindow.Reveal(source,at,length);
        // The reveal above runs before the frame's own layout. LongLineWindow re-windows a >16K line
        // from its queued Loaded pass and anchors that rebuild on the caret's screen Y from before the
        // press; the match then stays outside the viewport (300K basic find next on the 100K line:
        // caret 438 px below the bottom edge after everything settled). Confirm once after the layout
        // pass and let the editor reveal the caret itself when the match is still off screen. One post
        // per find press; nothing here reacts to a scroll or viewport change, so it cannot re-post.
        QueueSearchCaretReveal();
        // b53 #13: ScrollToLine is called with no column, so it never scrolls sideways, and LongLineWindow
        // only covers lines past its fold threshold. A wrap-off line that is merely wider than the
        // viewport is below that threshold, so reveal it here.
        if(!source.WordWrap)RevealSourceHorizontally(at,length);
        // Typewriter mode: the found line goes to the middle, as a "#heading" jump does (FocusMode.cs).
        var focusSurface=FocusSurfaceOf(source);if(focusSurface!=null)focusSurface.CentreAfterJump();
        return true;
    }
    void QueueSearchCaretReveal(){
        SourceEditor editor=source;
        if(editor==null)return;
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(delegate{
            if(!Object.ReferenceEquals(editor,source)||editor.Document==null)return;
            if(!CaretRowOnScreen(editor))MarkdownLivePreview.Session.SafeBringCaretToView(editor.TextArea);
        }));
    }
    // The same geometry the caret is drawn with: the visual column of the caret inside its realized
    // visual line. A long line's folds make the document column wrong, so the mapping goes through
    // GetVisualColumn (the long-line window and the caret drawing use it too). True also when the
    // line is not realized: an unjudgeable state must not scroll the view.
    static bool CaretRowOnScreen(SourceEditor editor){
        var view=editor.TextArea.TextView;
        if(view==null||editor.Document==null)return true;
        try{view.EnsureVisualLines();}
        catch(InvalidOperationException){return true;}
        catch(ICSharpCode.AvalonEdit.Rendering.VisualLinesInvalidException){return true;}
        int caret=Math.Max(0,Math.Min(editor.CaretOffset,editor.Document.TextLength));
        ICSharpCode.AvalonEdit.Document.DocumentLine line=editor.Document.GetLineByOffset(caret);
        ICSharpCode.AvalonEdit.Rendering.VisualLine visual=view.GetVisualLine(line.LineNumber);
        if(visual==null||visual.IsDisposed)return true;
        int relative=caret-line.Offset;
        int column=Math.Max(0,Math.Min(visual.GetVisualColumn(Math.Min(relative,line.Length)),visual.VisualLength));
        Point position=visual.GetVisualPosition(column,ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineMiddle);
        double top=view.VerticalOffset,bottom=top+((System.Windows.Controls.Primitives.IScrollInfo)view).ViewportHeight;
        return position.Y>=top-1&&position.Y<=bottom+1;
    }
    // Wrap-off source surface: put the match inside the viewport on both axes. LongLineWindow keeps a long
    // line's real text on screen when it has a session; this covers the wider line that has none, and the
    // match that is already real text but scrolled out sideways.
    void RevealSourceHorizontally(int start,int length){
        source.UpdateLayout();
        Rect first=source.GetRectFromCharacterIndex(start,false);
        if(first.IsEmpty||Double.IsInfinity(first.Left))return;
        Rect last=source.GetRectFromCharacterIndex(Math.Max(start,start+length-1),true);
        if(last.IsEmpty||Double.IsInfinity(last.Right))last=first;
        double viewportWidth=source.ViewportWidth,viewportHeight=source.ViewportHeight;
        if(viewportWidth<=0||viewportHeight<=0)return;
        double left=first.Left,right=Math.Max(last.Right,first.Right),top=first.Top,bottom=Math.Max(last.Bottom,first.Bottom),margin=24;
        if(left<margin||right>viewportWidth-margin){double centered=(left+right)/2-viewportWidth/2;if(Math.Abs(centered)>=.5)source.ScrollToHorizontalOffset(Math.Max(0,source.HorizontalOffset+centered));}
        if(top<margin||bottom>viewportHeight-margin){double centered=(top+bottom)/2-viewportHeight/2;if(Math.Abs(centered)>=.5)source.ScrollToVerticalOffset(Math.Max(0,source.VerticalOffset+centered));}
    }
    // mode: "debounce" (the typing pause fired), "now" (a query edit, Find, open), "pause" (the debounce
    // tick: this turn owns the owed work) or "cheap" (a selection change, UpdateTitle). Returns false when
    // it did not run, so the debounce can tell "still owed" from "done". Only "now" rebuilds while a change
    // is owed; the others leave it for the pause.
    void RefreshSearch(){RefreshSearchCore("cheap");}
    void RefreshSearchNow(){RefreshSearchCore("now");}
    bool RefreshSearchCore(string mode){
        if(loading||applyingLoadedFile||searchBusy||searchHost.Visibility!=Visibility.Visible)return false;
        SyncSearchInvalidation();UpdateRegexTip();
        if(QueryText().Length==0){StopSearchRefreshTimer();searchTextStale=true;CancelParallelSearch();matchCount.Text="0 / 0";UpdateSearchHighlight(null,-1);replaceOneButton.IsEnabled=replaceAllButton.IsEnabled=false;return true;}
        // Owed work waits for the pause; touching the map here would rebuild it on every keystroke. The pause
        // itself and an explicit "now" carry it out.
        if(mode!="now"&&mode!="pause"&&searchTextPending)return false;
        /* Both large paths run the scan from a snapshot and apply the result only while the text version
           still matches, so they are started only on the turn that owns the work (the old order started a
           large source search from the cheap path too, so every keystroke cancelled and restarted one). */
        if(RegexOn?RefreshLargeRegexSearch():RefreshLargeSourceSearch()){searchTextPending=false;return true;}
        searchBusy=true;try{var map=SearchMapCached();var matches=SearchMatchesCached(map);int current=SearchCurrentCached(map,matches);ApplySearchDisplay(map,matches,current);}finally{searchBusy=false;}
        return true;
    }
    void ApplySearchDisplay(SearchMap map,List<int> matches,int current){
        matchCount.Text=(current+1)+" / "+matches.Count;
        if(current>=0)ShowSearchHighlight(map,matches[current]);else UpdateSearchHighlightAll(map,matches);
        replaceOneButton.IsEnabled=replaceAllButton.IsEnabled=editing&&!documentOperationBusy&&matches.Count>0;
    }
    /* The replace commands are gated by the edit mode and by a running document operation, not only
       by the match list: a view switch that ends the edit session with the bar open left both buttons
       lit over a read-only surface, and a file open or format conversion owns the document the same
       way (finding 4). The cached match list already answers the new view, so this is O(1) and runs
       on the cheap path too. */
    void RefreshReplaceButtonState(){
        if(replaceOneButton==null||replaceAllButton==null)return;
        bool enabled=editing&&!documentOperationBusy&&searchCacheMatches!=null&&searchCacheMatches.Count>0;
        replaceOneButton.IsEnabled=enabled;replaceAllButton.IsEnabled=enabled;
    }
    // SearchHighlight.cs takes a source offset and the query's own length. A visible map's index is
    // not a source offset, and its source range is longer than the visible one when hidden markers
    // sit inside the match, so the overlay's range is set from the map here.
    void ShowSearchHighlight(SearchMap map,int index){
        if(index<0||map==null||(map.SourceOffsets==null&&map.Lengths==null)){UpdateSearchHighlight(map,index);return;}
        int start,length;SourceVisibleRange(map,index,out start,out length);
        UpdateSearchHighlight(null,-1);
        if(searchHost.Visibility==Visibility.Visible&&sourceHighlight!=null){sourceHighlight.SourceStart=start;sourceHighlight.SourceLength=length;sourceHighlight.Reposition();}
    }
    // The source range a visible match covers: from the first matched character's source offset to
    // one past the last one. Hidden runs between them stay inside the range (that is the highlight
    // and the selection the user sees); Replace asks the live session before it writes there.
    void SourceVisibleRange(SearchMap map,int index,out int start,out int length){
        start=index;length=MatchLength(map,index);
        int[] offsets=map==null?null:map.SourceOffsets;
        if(offsets!=null&&length==0&&map.Lengths!=null&&index>=0){
            /* A zero-length regex match is a place, not a range: the source offset of the character it
               stands before, or the end of the document. */
            int endOfDocument=source==null||source.Document==null?0:source.Document.TextLength;
            start=index<offsets.Length?offsets[index]:endOfDocument;
            if(start>endOfDocument)start=endOfDocument;
            return;
        }
        if(offsets==null||index<0||index>=offsets.Length)return;
        int last=Math.Min(offsets.Length-1,index+Math.Max(1,length)-1);
        start=offsets[index];length=offsets[last]+1-start;
        /* A map that no longer describes the document - one built before a whole-text replace, or
           a bar left open across a view switch - maps a hit to offsets past the end, and
           TextEditor.Select (and the live replace) throw for those. Clamp once here, so a stale
           map can at worst draw a wrong range and never break the press. */
        int documentLength=source==null||source.Document==null?0:source.Document.TextLength;
        if(start<0)start=0;
        if(start>documentLength)start=documentLength;
        int end=start+length;
        if(end>documentLength)end=documentLength;
        length=end-start;
    }
    /* W1: a replacement that lands inside a table row's cell must not add a cell. The row parser
       splits on unescaped pipes, so a "|" the user typed is written as "\|"; a pipe that already
       carries an odd run of backslashes is escaped already and stays as it is. A pipe inside a
       closed `code` span stays raw, the same rule MarkdownTableCommands.CellInsertText writes (the
       renderer skips the span whole); the span is read in the cell text the replacement leaves
       behind, prefix + value + suffix. Anywhere else the text is written exactly as typed. */
    string ReplacementForMatch(int sourceOffset,int sourceEnd,string value){
        if(value.Length==0||value.IndexOf('|')<0)return value;
        if(PlainText||!SourceInTableRow(sourceOffset))return value;
        ICSharpCode.AvalonEdit.Document.TextDocument document=source.Document;
        if(sourceEnd<sourceOffset)sourceEnd=sourceOffset;
        string prefix="",suffix="";
        if(sourceEnd<=document.TextLength){
            ICSharpCode.AvalonEdit.Document.DocumentLine line=document.GetLineByOffset(sourceOffset);
            int start=sourceOffset-line.Offset;
            int end=sourceEnd-line.Offset;
            if(end<=line.Length){
                string lineText=document.GetText(line.Offset,line.Length);
                List<MarkdownLivePreview.Cell> cells=MarkdownLivePreview.SplitCells(lineText);
                for(int c=0;c<cells.Count;c++){
                    if(start>=cells[c].Start&&end<=cells[c].End){
                        prefix=lineText.Substring(cells[c].Start,start-cells[c].Start);
                        suffix=lineText.Substring(end,cells[c].End-end);
                        break;
                    }
                }
            }
        }
        string cellText=prefix+value+suffix;
        var opens=new List<int>();
        var closes=new List<int>();
        int budget=cellText.Length*3;
        for(int p=0;p<cellText.Length;){
            char c=cellText[p];
            if(c=='\\'){p+=2;continue;}
            if(c=='`'){
                int run=1;
                while(p+run<cellText.Length&&cellText[p+run]=='`')run++;
                int close=MarkdownPlainText.CodeClose(cellText,p+run,run,ref budget);
                if(close>=0){opens.Add(p+run);closes.Add(close);p=close+run;continue;}
                p+=run;
                continue;
            }
            p++;
        }
        var escaped=new StringBuilder(value.Length+4);
        int backslashes=0;
        for(int p=Math.Min(sourceOffset,document.TextLength)-1;p>=0;p--){
            if(document.GetCharAt(p)!='\\')break;
            backslashes++;
        }
        int span=0;
        int offset=prefix.Length;
        for(int i=0;i<value.Length;i++){
            char c=value[i];
            int at=offset+i;
            while(span<opens.Count&&closes[span]<at)span++;
            bool code=span<opens.Count&&at>=opens[span];
            if(c=='|'){
                if(!code&&(backslashes&1)==0)escaped.Append('\\');
                escaped.Append(c);
                backslashes=0;
            }else{
                escaped.Append(c);
                if(c=='\\')backslashes++;else backslashes=0;
            }
        }
        return escaped.ToString();
    }
    // The live parse knows which lines are table rows (fences, list indentation and the delimiter
    // rule included). The detached 원문 view has no session, so the source walk answers instead.
    bool SourceInTableRow(int offset){
        ICSharpCode.AvalonEdit.Document.TextDocument document=source.Document;
        if(document==null||offset<0||offset>document.TextLength)return false;
        ICSharpCode.AvalonEdit.Document.DocumentLine line=document.GetLineByOffset(offset);
        if(livePreview!=null&&LivePreviewAttached)return livePreview.Info(line).Table;
        int first,last;
        return MarkdownTableCommands.FindTable(document,offset,out first,out last);
    }
    /* W2: a view switch changes which text the bar searches (visible text vs source) without
       touching the query. The cached map notices, but nothing asked for the new numbers, so the
       bar kept the old view's count and index until the next find. Every switch ends in
       UpdateLivePreview, which calls this: same path a query edit uses.
       The cached map already describes the searched text when the document version, the query and
       the visible/source kind all match, so an update that changed none of them (a zoom, a line
       spacing pass, a re-attach of the same view) must not walk the document again. Only a cache
       that no longer answers this view is rebuilt here. */
    void RefreshSearchForView(){
        if(searchHost==null)return;
        if(searchHost.Visibility!=Visibility.Visible){
            /* A bar closed in another tab only cleared that tab's overlay; this tab's adorner was
               restored from its captured state and kept drawing the old matches over a bar that is
               gone (UI bug hunt item 3). A hidden bar never keeps a highlight on the tab it returns
               to, and a null highlight is not created for a tab that had none. */
            if(sourceHighlight!=null)UpdateSearchHighlight(null,-1);
            return;
        }
        if(SearchCacheCurrent()){
            /* Same matches, new edit mode: the two replace buttons follow `editing` (above). */
            RefreshReplaceButtonState();
            return;
        }
        RefreshSearchNow();
    }
    void Find(){FindNext(false);}
    /* b53 #12: a find press works from the newest text. An edit since the last build left the cache cleared
       and the count owed; the press has to rebuild now, or it would search the pre-edit match list. */
    void FindNext(bool previous){if(RegexOn?DeferLargeRegexNavigation(previous):DeferLargeSearchNavigation(previous))return;if(QueryText().Length==0){ShowSearch();return;}SyncSearchInvalidation();var map=SearchMapCached();var matches=SearchMatchesCached(map);if(matches.Count==0){matchCount.Text="0 / 0";UpdateSearchHighlight(null,-1);return;}int current=SearchCurrentCached(map,matches),chosen=-1;if(current>=0)chosen=(current+(previous?matches.Count-1:1))%matches.Count;else{int anchor=previous?SelectedStart(map):SelectedEnd(map);int insertion=matches.BinarySearch(anchor);if(previous){chosen=insertion>=0?insertion-1:~insertion-1;if(chosen<0)chosen=matches.Count-1;}else{chosen=insertion>=0?insertion:~insertion;if(chosen>=matches.Count)chosen=0;}}searchBusy=true;try{SelectMatch(map,matches[chosen]);}finally{searchBusy=false;}RefreshSearchNow();}
    /* Finding 4: a file open or a save's mode conversion owns the document (documentOperationBusy).
       Writing a replacement into it then would race that operation, so both runs do nothing and the
       buttons read as disabled for the same window (RefreshReplaceButtonState, ApplySearchDisplay);
       the field's shortcuts call these same entries, so they are refused too. */
    void ReplaceOne(){if(documentOperationBusy)return;FlushDocumentChanges();if(!editing||QueryText().Length==0)return;SyncReplaceCompositionFromIme();string replaceWith=ReplacementText();var map=Map();var matches=Matches(map);if(matches.Count==0)return;int current=Current(map,matches);if(current<0){Find();map=Map();matches=Matches(map);current=Current(map,matches);if(current<0)return;}
        /* Regex: $1, ${name}, $$ and $0 are read against this very match; the table-cell rule below then
           treats the result exactly as typed text. */
        replaceWith=RegexValueAt(map,matches[current],replaceWith);if(replaceWith==null)return;
        if(map.SourceOffsets!=null){
            /* The live session rewrites the visible match in the source, keeping the formatting of
               its first character (hidden markers the match covered are folded away, not left half
               written). A match that holds structure it cannot rewrite safely is left alone. */
            int at,length;SourceVisibleRange(map,matches[current],out at,out length);
            bool done=false;searchBusy=true;try{done=livePreview!=null&&LivePreviewAttached&&livePreview.ReplaceVisible(at,at+length,ReplacementForMatch(at,at+length,replaceWith));}finally{searchBusy=false;}
            if(!done){ShowStatusNotice(T("이 항목은 구조에 걸려 바꿀 수 없습니다"));return;}
        }else{searchBusy=true;try{source.SelectedText=ReplacementForMatch(matches[current],matches[current]+MatchLength(map,matches[current]),replaceWith);}finally{searchBusy=false;}}
        FlushDocumentChanges();Find();RefreshSearchNow();}
    void ReplaceAll(){if(documentOperationBusy)return;FlushDocumentChanges();int queryLength=QueryText().Length;if(!editing||queryLength==0)return;SyncReplaceCompositionFromIme();string replaceWith=ReplacementText();var map=Map();var matches=Matches(map);if(matches.Count==0)return;bool valuesFailed;string[] values=RegexValues(map,matches,replaceWith,out valuesFailed);if(valuesFailed)return;int replaced=matches.Count;string skipped=null;/* Notepad keeps the reader's place: the run itself never scrolls or selects. The place is the first visible line, not a pixel number: the run rewrites every line, and the layout pass after it measured the old text (uifix s4: 721 px -> 769 px while the caret stayed put), so the line is put back below, at once and again after that pass. */int caretAtStart=source.CaretIndex;double scrollAtStart=source.VerticalOffset;ReplaceViewAnchor keepView=CaptureReplaceView(caretAtStart);searchBusy=true;try{
        if(map.SourceOffsets!=null){
            /* Visible text: every match is rewritten from the back, so each keeps the offsets of
               the ones before it, and one edit group makes the whole run a single undo step. A
               match that holds structure the rewrite cannot handle is left and counted. */
            int left=0;
            source.BeginChange();try{
                for(int i=matches.Count-1;i>=0;i--){int at2,length2;SourceVisibleRange(map,matches[i],out at2,out length2);string value=ReplacementForMatch(at2,at2+length2,values==null?replaceWith:values[i]);if(livePreview==null||!LivePreviewAttached||!livePreview.ReplaceVisible(at2,at2+length2,value))left++;}
            }finally{source.EndChange();}
            replaced=matches.Count-left;if(left>0)skipped=left+T("개는 구조에 걸려 건너뛰었습니다.");
        }else{
            /* The SelectedText setter keeps everything it inserted selected, and the next keystroke
               then replaced the whole document with itself (P1, UI bug hunt 4 item 1), so the text is
               written through Document.Replace, which creates no selection; the caret is put back
               below. One match at a time and from the back, so every earlier match keeps its offset:
               a single whole-document Replace rebuilt every line at the estimated default line
               height and threw away the heights already measured above the view, and the restore
               that followed then put the view at that estimate (uifix s4: 721 px -> 653 px with the
               same first visible line). A within-line replace keeps the line tree and those heights,
               which is also what the visible-text branch above already does through the session, and
               BeginChange/EndChange still makes the whole run one undo step. The find and replace
               fields are single-line, so a match never spans lines; a pasted newline that does put
               one in the replacement only moves the anchored line down by the lines it added above
               it, which the shift below answers. */
            int anchorStart=keepView.Line>0?source.Document.GetLineByNumber(keepView.Line).Offset:-1,shift=0,queryLines=LineBreakCount(QueryText());
            /* One Replace over the span from the first match to the end of the last one: the lines
               above the first match keep their measured heights, and the history sees one edit, as
               it did for the whole-document Replace. Replacing each match as its own edit made the
               run several small within-line edits, and the history-stress probe then saw a later
               Backspace take an entry away (11-01, A/B build). */
            int firstAt=matches[0],lastEnd=matches[matches.Count-1]+LenAt(map,matches.Count-1,queryLength);
            string span=source.Document.GetText(firstAt,lastEnd-firstAt);
            var rewritten=new StringBuilder(span.Length+matches.Count*Math.Max(0,replaceWith.Length-queryLength));
            int copied=firstAt;
            for(int i=0;i<matches.Count;i++){int at=matches[i],len=LenAt(map,i,queryLength);rewritten.Append(span,copied-firstAt,at-copied);string value=ReplacementForMatch(at,at+len,values==null?replaceWith:values[i]);rewritten.Append(value);copied=at+len;/* a regex match may span lines itself */if(anchorStart>=0&&at+len<=anchorStart)shift+=LineBreakCount(value)-(map.Lengths==null?queryLines:LineBreakCount(span.Substring(at-firstAt,len)));}
            source.BeginChange();try{source.Document.Replace(firstAt,lastEnd-firstAt,rewritten.ToString());}finally{source.EndChange();}
            /* The run ends as a small within-line edit; when the caret comes back to where that edit
               ended, the next Backspace or typed character would fold into the replacement's undo
               step (history-stress: one Backspace took an entry away). A Replace All is never part of
               a typing run. */
            source.BreakTypingRun();
            if(shift!=0)keepView.Line=Math.Max(1,keepView.Line+shift);}}finally{searchBusy=false;}
        /* Notepad leaves no selection after Replace All and keeps the reader's place: the caret goes
           back where it was, clamped to the new length, and the first visible line is put back at the
           same height in the view (the run's own relayout and the caret's return can both move it).
           Each run above is one update group, so one Ctrl+Z takes the whole replacement back
           (UI bug hunt 4 item 1). */
        int caret=caretAtStart;if(caret<0)caret=0;if(caret>source.Document.TextLength)caret=source.Document.TextLength;source.Select(caret,0);{var focusKeep=FocusSurfaceOf(source);if(focusKeep!=null)focusKeep.KeepViewAfterBulkEdit();}keepView.Caret=caret;keepView.Version=source.Document.Version;/* The line is the place; the pixel offset only answers a run whose line could not be measured. */if(!RestoreReplaceView(keepView))source.ScrollToVerticalOffset(scrollAtStart);
        FlushDocumentChanges();RefreshSearchNow();ShowStatusNotice(replaced+T("개를 바꿨습니다.")+(skipped==null?"":" "+skipped));}
    /* The reader's place across a Replace All. A single synchronous ScrollToVerticalOffset put one
       pixel number back and the layout pass that followed moved the view on its own (uifix s4:
       721 px -> 769 px with the caret unchanged), so Notepad's rule is used instead: keep the same
       line at the top, not the same pixel. The first visible line and its place on screen are
       captured before the run and put back after it, at once, after this frame's layout (Loaded)
       and once more at ContextIdle, the deferred order the reading-anchor restore already uses.
       The target is recomputed from the measured frame in every pass: a whole-document replace
       used to leave every line at the estimated default height, and the passes kept re-applying
       the pixel target that estimate produced (uifix s4: 721 px -> 653 px). A pass whose document,
       version, caret or selection changed is skipped, so typing, undo or a selection made in
       between is never scrolled. */
    // The line breaks the document's own line tree counts: a CR LF pair, a lone CR, LF and the two
    // Unicode separators. Replace All needs it to move its anchored line by the lines a replacement
    // above the view added.
    static int LineBreakCount(string text){
        if(String.IsNullOrEmpty(text))return 0;
        int count=0;for(int i=0;i<text.Length;i++){char c=text[i];if(c=='\r'){count++;if(i+1<text.Length&&text[i+1]=='\n')i++;}else if(c=='\n'||c==(char)0x2028||c==(char)0x2029)count++;}
        return count;
    }
    sealed class ReplaceViewAnchor {internal int Line;internal double Top;internal int Caret;internal ICSharpCode.AvalonEdit.Document.TextDocument Document;internal ICSharpCode.AvalonEdit.Document.ITextSourceVersion Version;}
    ReplaceViewAnchor CaptureReplaceView(int caret){
        var anchor=new ReplaceViewAnchor {Caret=caret,Document=source==null?null:source.Document,Top=Double.NaN};
        if(source==null||source.Document==null||source.Document.LineCount==0)return anchor;
        var view=source.TextArea.TextView;if(view==null)return anchor;
        try{
            view.EnsureVisualLines();
            int line=source.GetFirstVisibleLineIndex();
            anchor.Line=Math.Max(1,Math.Min(line<0?1:line+1,source.Document.LineCount));
            /* The line top's place on screen, as the reading anchor captures it: the rect already
               carries the 24 px page inset and the scroll, so a line that was scrolled off starts
               negative and the restore needs no arithmetic of its own. */
            /* The line's top, measured the way RestoreReplaceView measures it (LineTop plus the inset).
               The character rect's top is the text's top, a few pixels below the line's in the live
               view, so a restore that mixed the two ended that much off (uifix s4: -5 px -> -1 px). */
            double lineTop=view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(anchor.Line,1),ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop).Y;
            if(!Double.IsNaN(lineTop)&&!Double.IsInfinity(lineTop))anchor.Top=lineTop+source.Padding.Top-source.VerticalOffset;
        }catch(InvalidOperationException){anchor.Line=0;}
        catch(ICSharpCode.AvalonEdit.Rendering.VisualLinesInvalidException){anchor.Line=0;}
        return anchor;
    }
    // True when the anchor answered - the caller then keeps no pixel offset of its own.
    bool RestoreReplaceView(ReplaceViewAnchor anchor){
        if(anchor==null||anchor.Line<=0||Double.IsNaN(anchor.Top)||Double.IsInfinity(anchor.Top))return false;
        SourceEditor editor=source;if(editor==null||editor.Document==null)return false;
        var document=editor.Document;var version=anchor.Version;int caret=anchor.Caret,line=anchor.Line;double wasTop=anchor.Top;
        Action<bool> apply=delegate(bool deferred){
            if(editor!=source||editor.Document==null||!Object.ReferenceEquals(document,editor.Document))return;
            if(version!=null&&!Object.ReferenceEquals(version,editor.Document.Version))return;
            if(editor.CaretIndex!=caret||editor.SelectionLength!=0)return;
            var view=editor.TextArea.TextView;if(view==null)return;
            int number=Math.Max(1,Math.Min(line,editor.Document.LineCount));
            double top=0;int first=0;
            try{
                /* This frame's lines are measured (or built now) before the target is read; a target
                   taken from the frame before the run is exactly the stale pixel the later passes
                   used to re-apply. */
                view.EnsureVisualLines();
                first=editor.GetFirstVisibleLineIndex()+1;
                top=view.GetVisualPosition(new ICSharpCode.AvalonEdit.TextViewPosition(number,1),ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop).Y+editor.Padding.Top;
            }
            catch(InvalidOperationException){return;}
            catch(ICSharpCode.AvalonEdit.Rendering.VisualLinesInvalidException){return;}
            if(Double.IsNaN(top)||Double.IsInfinity(top))return;
            /* A later pass only answers a view that drifted: while the anchor line is still the
               first visible line the run is done, and scrolling would move a view nothing else did
               (the whole-document replace's estimated target pulled 721 px to 653 px here). A view
               that now shows a far different line was scrolled by something else (a view switch, the
               reader's wheel) in the idle gap, and pulling it back would be the same complaint this
               restore fixes. The first pass after the run is not judged this way: the replace itself
               can leave the view at the top until the line is put back. */
            /* "Still the first line" is not "still at the same height": the pass before it can leave
               the line a few pixels off (uifix s4: top -5 px -> -1 px, same line), because the lines
               it scrolled past were measured only after it. The line has to be back where it was
               seen, within a pixel. */
            if(deferred&&first==number&&Math.Abs((top-editor.VerticalOffset)-wasTop)<=1.0)return;
            if(deferred&&(first<1||first<number-6||first>number+6))return;
            double target=top-wasTop;
            /* The 24 px page inset shrinks while the view scrolls through it (ApplySourceInset), so a
               target inside that band is solved against the settled geometry, as the reading anchor
               restore does. */
            if(target<24){double inset=(top+24-wasTop)/2;target=inset<0?0:inset;}
            if(Math.Abs(target-editor.VerticalOffset)<=0.5)return;
            editor.ScrollToVerticalOffset(Math.Max(0,target));
        };
        apply(false);
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(delegate{apply(true);}));
        editor.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle,new Action(delegate{apply(true);}));
        return true;
    }
}
}
