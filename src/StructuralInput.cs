using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
namespace MDNote {public partial class MainWindow {
    internal string TaskTooltip(){return T("체크/해제 | 저장 필요");}
    void UpdateCodeAvailability(){bool enabled=LiveFormat;foreach(Control c in new Control[]{boldButton,italicButton,strikeButton,codeButton,headingButton,listButton,taskButton,sizes,colorMain,colorArrow,blockButton,quoteButton,ruleButton,tableButton})c.IsEnabled=enabled;Active(blockButton,false);}
    // The toolbar, shortcut and probe entry points: the live surface runs the Markdown command.
    void ToggleCodeBlock(){LiveCodeBlock();}
    void ToggleTaskList(){LiveList(ListKind.Task);}
    void ToggleCurrentCheck(){LiveCheck();}
    void ShowCustomTable(){/* b58: an F-key can reach this dialog; put a live IME syllable into the document before the modal takes the keyboard (a mouse open has already committed). */CommitCompositionForKeyboardCommand();if(!LiveFormat)return;var window=new Window {Title=T("표 삽입"),Owner=this,Width=290,Height=255,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Background,Foreground=Foreground,ShowInTaskbar=false};/* b56: Esc and 취소 close the custom table prompt like the shortcut window (user, 2026-10-30) and leave the caret in the body. */window.PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){window.Close();e.Handled=true;}};var panel=new StackPanel {Margin=new Thickness(18)};window.Resources.MergedDictionaries.Add(Resources);window.Content=panel;panel.Children.Add(new TextBlock {Text=T("열")+" (1–50)"});var columns=new TextBox {Text="3",Margin=new Thickness(0,4,0,10)};InputMethod.SetIsInputMethodEnabled(columns,false);panel.Children.Add(columns);panel.Children.Add(new TextBlock {Text=T("행")+" (1–200)"});var rows=new TextBox {Text="3",Margin=new Thickness(0,4,0,12)};InputMethod.SetIsInputMethodEnabled(rows,false);panel.Children.Add(rows);var actions=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};var cancel=new Button {Content=T("취소"),IsCancel=true,Padding=new Thickness(10,4,10,4),Margin=new Thickness(4,0,4,0)};cancel.Click+=delegate{window.Close();};actions.Children.Add(cancel);var button=new Button {Content=T("삽입"),IsDefault=true,Padding=new Thickness(10,4,10,4),Margin=new Thickness(4,0,4,0)};actions.Children.Add(button);panel.Children.Add(actions);int c=0,r=0;button.Click+=delegate{if(!Int32.TryParse(columns.Text,out c)||!Int32.TryParse(rows.Text,out r)||c<1||c>50||r<1||r>200){MessageBox.Show(window,T("열은 1~50, 행은 1~200 사이로 입력해 주세요."),"MDNote");return;}window.DialogResult=true;};window.Loaded+=delegate{columns.Focus();columns.SelectAll();};if(window.ShowDialog()==true)InsertTable(c,r);else source.Focus();}
    // Ctrl+] / Ctrl+[ move a table one list level in / out. Tab inside a table has to stay cell
    // navigation, so the table gets its own pair plus the two buttons on the table tool row.
    // Every live surface gets its own hook: the constructor's pair from BuildTableTools and each
    // tab's new source from Workspace (SetupTableLevelKeys).
    readonly HashSet<SourceEditor> tableKeySources=new HashSet<SourceEditor>();
    // The hook added with each surface, kept by editor so a closed tab's editor can be unhooked
    // again (ForgetTableKeySource); the set alone left every editor this window ever showed in it.
    readonly Dictionary<SourceEditor,KeyEventHandler> tableKeyHandlers=new Dictionary<SourceEditor,KeyEventHandler>();
    void SetupTableLevelKeys(){SetupTableKeys(source);}
    void SetupTableKeys(SourceEditor live){
        if(live==null||!tableKeySources.Add(live))return;
        KeyEventHandler handler=delegate(object sender,KeyEventArgs e){if(Object.ReferenceEquals(live,source))TableLevelKey(e,true);};
        tableKeyHandlers[live]=handler;live.TextArea.PreviewKeyDown+=handler;
    }
    // Releasing a tab drops its editor from both the key set and the TextArea's hook list.
    internal void ForgetTableKeySource(SourceEditor editor){
        if(editor==null)return;
        KeyEventHandler handler;
        if(tableKeyHandlers.TryGetValue(editor,out handler)){tableKeyHandlers.Remove(editor);editor.TextArea.PreviewKeyDown-=handler;}
        tableKeySources.Remove(editor);
    }
    // Test seam: Keyboard.Modifiers cannot be set from a probe, so the key path reads this
    // override when it is present. Null (the product path) means the real keyboard state.
    internal ModifierKeys? TableKeyModifiersProbe;
    // The Ctrl+[ / Ctrl+] decision. The caret's table is looked up only after the key and the
    // modifier matched, so an ordinary keystroke never scans the table lines.
    void TableLevelKey(KeyEventArgs e,bool live){
        if(e.Handled)return;
        ModifierKeys modifiers=TableKeyModifiersProbe.HasValue?TableKeyModifiersProbe.Value:Keyboard.Modifiers;
        if(modifiers!=ModifierKeys.Control)return;
        bool? indent=null;
        if(e.Key==Key.OemCloseBrackets)indent=true;
        else if(e.Key==Key.OemOpenBrackets)indent=false;
        if(!indent.HasValue)return;
        if(!live||!LiveFormat||source==null||!MarkdownStructuralInput.TableAtCaret(source.Document,source.CaretIndex))return;
        e.Handled=true;TableLevel(indent.Value);source.Focus();
    }
    // The same pair without the keyboard, so a probe can drive the key path (the static Keyboard
    // state cannot be set from a test).
    internal void TableLevelKeyProbe(bool indent){
        if(LiveFormat)TableLevel(indent);
    }
    // True when the surface on screen carries the Ctrl+[ / Ctrl+] hook. A tab's own source is
    // hooked from Workspace, so a probe can tell a missing hook from a failing command.
    internal bool TableKeysHooked {
        get { return source!=null&&tableKeySources.Contains(source); }
    }
    // The live surface's Tab path without a keyboard (the probe cannot set ModifierKeys).
    internal bool SourceTabKeyProbe(bool shift){
        if(!LiveFormat||source==null)return false;
        ICSharpCode.AvalonEdit.Document.TextDocument document=source.Document;
        CommandResult result=MarkdownStructuralInput.Tab(document,source.SelectionStart,source.SelectionLength,shift);
        if(result==null)return false;
        if(result.Edits.Count>0)ApplyCommandResult(result);else source.Select(result.SelectionStart,result.SelectionLength);
        source.Focus();
        return true;
    }
    // The Ctrl+[ / Ctrl+] key path without a keyboard: it builds the KeyEventArgs the attached
    // PreviewKeyDown hook sees and raises it on the live surface, so a probe exercises the
    // per-surface registration instead of calling TableLevel directly.
    internal bool RaiseTableLevelKeyProbe(bool indent){
        if(!TableKeysHooked)return false;
        Key key=indent?Key.OemCloseBrackets:Key.OemOpenBrackets;
        ModifierKeys? previous=TableKeyModifiersProbe;
        try{
            TableKeyModifiersProbe=ModifierKeys.Control;
            KeyEventArgs args=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(this),0,key);
            args.RoutedEvent=Keyboard.PreviewKeyDownEvent;
            source.TextArea.RaiseEvent(args);
            return args.Handled;
        }finally{TableKeyModifiersProbe=previous;}
    }
    // A toolbar/menu command that edits the document waits for the composition store to be
    // released: it runs from SourceEditor.RunAfterComposition instead of under TSF.
    bool StoreGuardLive(){return RichCompositionLive||CompositionOwnsStore();}
    // Visible text of a paragraph, the same text ReadableText collects for ReadableText
    // (TextMode) and the clipboard readers.
    static string ParagraphBody(InlineCollection inlines){var value=new System.Text.StringBuilder();foreach(Inline inline in inlines){var run=inline as Run;if(run!=null)value.Append(run.Text);else if(inline is Span)value.Append(ParagraphBody(((Span)inline).Inlines));else if(inline is LineBreak)value.Append("\n");}return value.ToString();}
}}
