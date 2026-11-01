using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MDNote {
public partial class MainWindow {
    internal FlowDocument CreatePrintDocument(double width,double height){
        if(Double.IsNaN(width)||Double.IsInfinity(width)||width<160)width=793.7;
        if(Double.IsNaN(height)||Double.IsInfinity(height)||height<160)height=1122.5;
        // Build a separate light document: printing never resizes the active editor.
        var document=PlainText?PlainPrintDocument(text):Markdown.Parse(text,16,family,Brushes.Black,false,DocumentBasePath(),T("[이미지: "));
        InlineCodeBackground.RestorePrintBackground(document.Blocks);document.Background=Brushes.White;document.PagePadding=new Thickness(32);
        document.PageWidth=width;document.PageHeight=height;document.ColumnWidth=width;document.ColumnGap=0;
        ((IDocumentPaginatorSource)document).DocumentPaginator.PageSize=new Size(width,height);
        return document;
    }
    /* TXT printing draws the source literally. A line that begins with a Unicode space separator
       (U+3000, the Japanese/Chinese IME's space, is the one users type) is content, not
       indentation - every renderer reads it that way (MarkdownPlainText.Space) - but WPF's own
       line breaking counts such a character as the whitespace of the break and drops it there:
       the print probe's three U+3000 lines printed without their spaces (2026-10-31). A line
       that starts its own paragraph keeps its first character at a content position, so the
       space survives. A document with no such line keeps the one-Run shape, which keeps a large
       TXT one light paragraph. */
    FlowDocument PlainPrintDocument(string value){
        var document=new FlowDocument {FontFamily=new FontFamily(family),FontSize=16,Foreground=Brushes.Black};
        string normalized=value.Replace("\r\n","\n").Replace('\r','\n');
        string[] lines=normalized.Split('\n');
        bool split=false;
        foreach(string line in lines)if(line.Length>0&&line[0]!=' '&&Char.GetUnicodeCategory(line[0])==System.Globalization.UnicodeCategory.SpaceSeparator){split=true;break;}
        if(!split){document.Blocks.Add(new Paragraph(new Run(value)));return document;}
        foreach(string line in lines)document.Blocks.Add(new Paragraph(new Run(line)){Margin=new Thickness(0),LineHeight=Double.NaN});
        return document;
    }
    /* The preview is the same print document (CreatePrintDocument: white page, black text, the
       printer's printable area), paged by a FlowDocumentPageViewer that keeps the document's
       PageWidth/PageHeight, so what it shows is what the print path below lays out. The default
       printer's area is read without showing a dialog; with no printer it is A4 at 96 dpi. */
    bool ShowPrintPreview(){
        double width=793.7,height=1122.5;
        try{var probe=new PrintDialog();if(probe.PrintableAreaWidth>=160&&probe.PrintableAreaHeight>=160){width=probe.PrintableAreaWidth;height=probe.PrintableAreaHeight;}}catch(Exception){}
        FlowDocument document;
        var previousCursor=System.Windows.Input.Mouse.OverrideCursor;
        System.Windows.Input.Mouse.OverrideCursor=System.Windows.Input.Cursors.Wait;
        try{document=CreatePrintDocument(width,height);}finally{System.Windows.Input.Mouse.OverrideCursor=previousCursor;}
        var viewer=new FlowDocumentPageViewer {Document=document,Background=new SolidColorBrush(Color.FromRgb(0xDD,0xDD,0xDD))};
        var preview=new Window {Title=T("인쇄 미리 보기"),Owner=this,Width=Math.Max(640,ActualWidth*0.8),Height=Math.Max(520,ActualHeight*0.85),MinWidth=420,MinHeight=360,WindowStartupLocation=WindowStartupLocation.CenterOwner,ShowInTaskbar=false,Background=Background,Foreground=Foreground};
        preview.Resources.MergedDictionaries.Add(Resources);
        bool print=false;
        var panel=new DockPanel();
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(12,8,12,12)};
        DockPanel.SetDock(buttons,Dock.Bottom);
        var printButton=new Button {Content=T("인쇄…"),MinWidth=90,IsDefault=true,Margin=new Thickness(0,0,8,0)};
        printButton.Click+=delegate{print=true;preview.DialogResult=true;};
        var close=new Button {Content=T("닫기"),MinWidth=90,IsCancel=true};
        buttons.Children.Add(printButton);buttons.Children.Add(close);
        panel.Children.Add(buttons);panel.Children.Add(viewer);
        preview.Content=panel;
        // Ctrl+P in the preview is the same as 인쇄…; Esc is 닫기 (IsCancel).
        preview.PreviewKeyDown+=delegate(object sender,System.Windows.Input.KeyEventArgs e){if(e.Key==System.Windows.Input.Key.P&&(System.Windows.Input.Keyboard.Modifiers&System.Windows.Input.ModifierKeys.Control)!=0){print=true;e.Handled=true;preview.DialogResult=true;}};
        preview.ContentRendered+=delegate{viewer.Focus();};
        preview.ShowDialog();
        viewer.Document=null;
        return print;
    }
    /* A file open/format conversion holds the window with documentOperationBusy; printing then would
       read the previous document. Refuse with the same notice a failed print shows. */
    bool PrintReady(){
        if(!documentOperationBusy)return true;
        MessageBox.Show(this,T("인쇄할 수 없습니다.\n"),"MDNote",MessageBoxButton.OK,MessageBoxImage.Information);
        return false;
    }
    /* A body this large can hold the window for many seconds while the print document is built and
       paginated: Markdown.Parse, every block signature (MarkdownWriter.Track) and every image decode
       run on this thread. The file-open overlay is reused for that wait; the label names the stage,
       the bar moves once between the stages, and 취소 stops the print before the next stage. The
       document the print path builds is not changed. */
    const int PrintProgressTextLength=64*1024;
    bool PrintLarge(){return source!=null&&source.Document!=null&&source.Document.TextLength>=PrintProgressTextLength;}
    async void Print(){
        try {
            /* A live composition has not reached source.Text yet: finish it and let the pipeline
               mirror the document before the print snapshot is built. */
            if(!PrintReady())return;
            FinishComposition();FlushDocumentChanges();
            if(!PrintReady())return;
            /* The Windows 11 print dialog shows "this app does not support print preview" for a
               WPF PrintDialog, so the preview comes first (user decision 2026-11-01). 인쇄… there
               continues into the unchanged path below; closing it prints nothing. */
            if(!ShowPrintPreview())return;
            if(!PrintReady())return;
            var dialog=new PrintDialog {UserPageRangeEnabled=false,SelectedPagesEnabled=false,CurrentPageEnabled=false};
            if(dialog.ShowDialog()!=true)return;
            /* A queued open/save may have started while the modal dialog pumped the dispatcher. */
            if(!PrintReady())return;
            string title=path==""?"MDNote 문서":System.IO.Path.GetFileName(path);
            bool large=PrintLarge();
            System.Threading.CancellationTokenSource printCancel=null;
            System.Threading.CancellationToken printToken=System.Threading.CancellationToken.None;
            try {
                if(large){
                    /* The overlay's 취소 button runs CancelFileOpen, which cancels the source it
                       finds here, so canceling stops the print at the next stage boundary instead
                       of only hiding the overlay. */
                    printCancel=new System.Threading.CancellationTokenSource();
                    printToken=printCancel.Token;
                    fileOpenCancellation=printCancel;
                    EnsureFileOpenOverlay();
                    fileOpenLabel.Text=T("인쇄 준비 중…");
                    SetFileOpenProgress(5);
                    if(fileOpenOverlay!=null)fileOpenOverlay.Visibility=Visibility.Visible;
                }
                if(large)await Dispatcher.InvokeAsync(new Action(delegate{}),System.Windows.Threading.DispatcherPriority.Background);
                if(printToken.IsCancellationRequested)return;
                var document=CreatePrintDocument(dialog.PrintableAreaWidth,dialog.PrintableAreaHeight);
                if(large){
                    fileOpenLabel.Text=T("페이지 계산 중…");
                    SetFileOpenProgress(70);
                    await Dispatcher.InvokeAsync(new Action(delegate{}),System.Windows.Threading.DispatcherPriority.Background);
                }
                if(printToken.IsCancellationRequested)return;
                dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator,title);
            }finally{
                if(printCancel!=null){
                    /* A file open that started while this awaited owns the overlay and the busy
                       guard now (it replaced fileOpenCancellation); only the turn that still owns
                       them may release them. */
                    bool owns=Object.ReferenceEquals(fileOpenCancellation,printCancel);
                    if(owns)fileOpenCancellation=null;
                    printCancel.Dispose();
                    if(owns){
                        documentOperationBusy=false;
                        LayoutMode();
                        UpdateTools();
                        UpdateLayout();
                        HideFileOpenOverlay();
                    }
                }
            }
        }catch(Exception ex){MessageBox.Show(this,T("인쇄할 수 없습니다.\n")+ex.Message,"MDNote",MessageBoxButton.OK,MessageBoxImage.Information);}
    }
}
}
