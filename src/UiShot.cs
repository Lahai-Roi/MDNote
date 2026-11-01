using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MDNote {
// --ui-shot <outDir>: real-window screenshots for the UI mockup (generated text only).
public partial class MainWindow {
    internal static int CaptureUiShots(string outDir) {
        outDir = Path.GetFullPath(outDir); Directory.CreateDirectory(outDir);
        if (System.Windows.Application.Current == null) new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var md = new StringBuilder();
        md.Append("# 다크 모드 확인\n\n본문 **굵게** `인라인 코드` 와 [링크](https://example.com).\n\n- [x] 끝난 일\n- [ ] 할 일\n\n");
        for (int i = 1; i <= 10; i++) md.Append(i).Append(". 번호 목록 ").Append(i).Append('\n');
        md.Append("\n> 인용문입니다.\n\n| 이름 | 설명 | 값 |\n|:---|:---:|---:|\n| 사과 | 빨간 과일 | 1,200 |\n\n---\n\n```\n코드 블록 안입니다.\n```\n\n");
        for (int i = 1; i <= 40; i++) md.Append("스크롤 확인용 문단 ").Append(i).Append(".\n\n");
        string mdFile = Path.Combine(outDir, "ui-shot.md");
        File.WriteAllText(mdFile, md.ToString(), new UTF8Encoding(false));
        string txtFile = Path.Combine(outDir, "ui-shot.txt");
        File.WriteAllText(txtFile, "첫째 줄입니다.\n둘째 줄입니다.\n\n넷째 줄입니다.\n다섯째 줄입니다.\n", new UTF8Encoding(false));
        bool old = ProbeDefaults.Live;
        try {
            Shot(outDir, "1-dark-live", mdFile, true, true, true, 900, 640, 0);
            Shot(outDir, "1-dark-live-scrolled", mdFile, true, true, false, 900, 640, 430);
            Shot(outDir, "2-txt-edit", txtFile, false, false, true, 900, 380, 0);
            Shot(outDir, "2-txt-read", txtFile, false, false, false, 900, 380, 0);
            Shot(outDir, "2-md-live-edit", mdFile, true, false, true, 900, 380, 0);
            Shot(outDir, "2-md-live-read", mdFile, true, false, false, 900, 380, 0);
            string bigFile = Path.Combine(outDir, "ui-shot-7digit.txt");
            var big = new StringBuilder(); for (int i = 1; i <= 1000005; i++) big.Append("줄 ").Append(i).Append('\n');
            File.WriteAllText(bigFile, big.ToString(), new UTF8Encoding(false));
            Shot(outDir, "2-txt-7digit-edit", bigFile, false, false, true, 900, 300, 1e9);
            Shot(outDir, "2-txt-7digit-read", bigFile, false, false, false, 900, 300, 1e9);
            Shot(outDir, "3-narrow-live", mdFile, true, false, true, 380, 640, 0);
            Shot(outDir, "4-live-scrolled", mdFile, true, false, true, 900, 520, 400);
            Shot(outDir, "4-live-end", mdFile, true, false, true, 900, 520, 1000000);
            ShotPart(outDir, "5-top-icons-light", mdFile, false, false, false, 900, 520, TopIconTarget, 2, 0, "Ribbon");
            ShotPart(outDir, "5-top-icons-dark", mdFile, false, true, false, 900, 520, TopIconTarget, 2, 0, "Ribbon");
            ShotPart(outDir, "5-format-ribbon", mdFile, true, false, true, 900, 520, RibbonTarget, 2, 0, "Ribbon");
            ShotPart(outDir, "toolbar-live", mdFile, true, false, true, 1600, 520, RibbonTarget, 1, 0, "Ribbon");
            ShotPart(outDir, "table-live", mdFile, true, false, true, 1600, 520, TableToolsTarget, 1, 0, "Ribbon");
            ShotPart(outDir, "5-table-tools-light", mdFile, true, false, true, 900, 520, TableToolsTarget, 2, 0, "Ribbon");
            ShotPart(outDir, "5-table-tools-dark", mdFile, true, true, true, 900, 520, TableToolsTarget, 2, 0, "Ribbon");
            ShotPart(outDir, "5-scrollbar-normal", mdFile, true, false, true, 900, 640, ScrollBarTarget, 4, 140, "Paper");
        } finally { ProbeDefaults.Live = old; }
        File.WriteAllText(Path.Combine(outDir, "ui-shot-PASS.txt"), "PASS\n");
        return 0;
    }
    static void Shot(string outDir, string name, string file, bool live, bool darkTheme, bool edit, double width, double height, double scroll) {
        ProbeDefaults.Live = live;
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
        try {
            window.Show(); window.Width = width; window.Height = height; window.Left = -20000;
            window.LoadFileCore(file); LongLineBench.Idle();
            window.SetDark(darkTheme); LongLineBench.Idle();
            if (window.editing != edit) window.SetMode(edit);
            LongLineBench.Idle();
            if (scroll > 0) {
                for (int pass = 0; pass < 2; pass++) {
                    window.source.ScrollToVerticalOffset(scroll);
                    LongLineBench.Idle();
                }
            }
            Program.Snapshot(window, Path.Combine(outDir, name + ".png"));
            var view = window.source.TextArea.TextView;
            File.AppendAllText(Path.Combine(outDir, "ui-shot-metrics.txt"), name + ": margin=" + view.Margin + " extent=" + window.source.ExtentHeight + " viewport=" + window.source.ViewportHeight + " offset=" + window.source.VerticalOffset + " lines=" + window.source.Document.LineCount + "\n");
        } finally { window.SetDark(false); window.Close(); }
    }
    static void ShotPart(string outDir, string name, string file, bool live, bool darkTheme, bool edit, double width, double height, Func<MainWindow, FrameworkElement> target, double scale, double maxHeight, string backdropKey) {
        ProbeDefaults.Live = live;
        var window = new MainWindow { ShowInTaskbar = false, ShowActivated = false };
        try {
            window.Show(); window.Width = width; window.Height = height; window.Left = -20000;
            window.LoadFileCore(file); LongLineBench.Idle();
            window.SetDark(darkTheme); LongLineBench.Idle();
            if (window.editing != edit) window.SetMode(edit);
            LongLineBench.Idle();
            var element = target(window);
            window.UpdateLayout(); LongLineBench.Idle();
            SavePart(window, outDir, name, element, scale, maxHeight, backdropKey);
            File.AppendAllText(Path.Combine(outDir,"toolbar-metrics.txt"),name+": formatWidth="+RibbonContentWidth(window).ToString("0.##")+", tableWidth="+window.tableToolsHost.ActualWidth.ToString("0.##")+", dpi="+VisualTreeHelper.GetDpi(window).PixelsPerDip+"\n");
        } finally { window.SetDark(false); window.Close(); }
    }
    static double RibbonContentWidth(MainWindow window){double width=0;foreach(FrameworkElement group in window.formatBar.Children)width+=group.ActualWidth+group.Margin.Left+group.Margin.Right;return width;}
    static FrameworkElement TopIconTarget(MainWindow window) { return window.quickFind.Parent as FrameworkElement; }
    static FrameworkElement RibbonTarget(MainWindow window) { window.updatingTools = true; try { window.sizes.Text = "16 px"; } finally { window.updatingTools = false; } window.UpdateLayout(); return window.ribbonHost; }
    static FrameworkElement TableToolsTarget(MainWindow window) {
        if (!window.LivePreviewAttached) window.UpdateLivePreview();
        int at = window.source.Text.IndexOf("| 사과");
        if (at >= 0) window.source.CaretIndex = at + 2;
        window.UpdateTableTools(); window.UpdateLayout();
        return window.tableToolsHost;
    }
    static FrameworkElement ScrollBarTarget(MainWindow window) { return FindVerticalBar(window.source); }
    static ScrollBar FindVerticalBar(DependencyObject root) {
        var bar = root as ScrollBar;
        if (bar != null && bar.Orientation == Orientation.Vertical && bar.IsVisible) return bar;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++) {
            var found = FindVerticalBar(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
    static void SavePart(MainWindow window, string outDir, string name, FrameworkElement element, double scale, double maxHeight, string backdropKey) {
        if (element == null) return;
        // Full layout pass first: part bounds are only final after the pending layout/render work drains.
        window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(delegate { }));
        window.UpdateLayout();
        element.UpdateLayout();
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var rootVisual = window.Content as Visual;
        if (rootVisual == null) return;
        var rootElement = (FrameworkElement)rootVisual;
        // Crop in the captured window's own coordinate space; rendering the element on its own
        // shifts or loses its content (offsets are relative to the parent visual).
        Rect bounds = element.TransformToAncestor(rootVisual).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        if (maxHeight > 0 && bounds.Height > maxHeight) bounds.Height = maxHeight;
        bounds.Intersect(new Rect(0, 0, rootElement.ActualWidth, rootElement.ActualHeight));
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) return;
        // Render the window at its own DPI (times the requested zoom), then crop the element rect.
        DpiScale dpi = VisualTreeHelper.GetDpi(rootVisual);
        double scaleX = dpi.DpiScaleX * scale, scaleY = dpi.DpiScaleY * scale;
        int fullWidth = Math.Max(1, (int)Math.Ceiling(rootElement.ActualWidth * scaleX));
        int fullHeight = Math.Max(1, (int)Math.Ceiling(rootElement.ActualHeight * scaleY));
        var bitmap = new RenderTargetBitmap(fullWidth, fullHeight, 96 * scaleX, 96 * scaleY, PixelFormats.Pbgra32);
        Brush backdrop = (window.Resources[backdropKey] as Brush) ?? window.Background ?? Brushes.White;
        var background = new DrawingVisual();
        using (var context = background.RenderOpen()) context.DrawRectangle(backdrop, null, new Rect(0, 0, rootElement.ActualWidth, rootElement.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(rootVisual);
        int x = (int)Math.Round(bounds.X * scaleX), y = (int)Math.Round(bounds.Y * scaleY);
        int w = (int)Math.Ceiling(bounds.Width * scaleX), h = (int)Math.Ceiling(bounds.Height * scaleY);
        if (x < 0) { w += x; x = 0; }
        if (y < 0) { h += y; y = 0; }
        w = Math.Min(w, fullWidth - x); h = Math.Min(h, fullHeight - y);
        if (w < 1 || h < 1) return;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, new Int32Rect(x, y, w, h))));
        using (var stream = File.Create(Path.Combine(outDir, name + ".png"))) encoder.Save(stream);
    }
}
}
