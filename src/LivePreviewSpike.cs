using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.TextFormatting;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Utils;

namespace MDNote {
// Research window and bench adapter for MarkdownLivePreview. Unlike the product host,
// it applies the font/wrap/colors to the editor itself and restores them on Dispose.
internal static class LivePreviewSpike {
    internal sealed class Options {
        internal double FontSize=16;
        internal string Family="Malgun Gothic";
        internal double LinePercent=150;
        internal bool Dark;
        internal bool WordWrap=true;
        internal string BaseDirectory;
    }

    internal static IDisposable Attach(SourceEditor editor, Options options) {
        if (editor == null) throw new ArgumentNullException("editor");
        if (options == null) throw new ArgumentNullException("options");
        return new Applied(editor, options);
    }

    sealed class Applied : IDisposable {
        readonly SourceEditor editor;
        readonly double oldSize;
        readonly FontFamily oldFamily;
        readonly bool oldWrap;
        readonly Brush oldBackground, oldForeground, oldCaret, oldSelection;
        readonly MarkdownLivePreview.Session session;
        bool disposed;
        internal Applied(SourceEditor editor, Options options) {
            this.editor = editor;
            oldSize = editor.FontSize;
            oldFamily = editor.FontFamily;
            oldWrap = editor.WordWrap;
            oldBackground = editor.Background;
            oldForeground = editor.Foreground;
            oldCaret = editor.CaretBrush;
            oldSelection = editor.TextArea.SelectionBrush;
            Brush foreground = Frozen(options.Dark ? "#E8EAED" : "#202124");
            editor.FontSize = options.FontSize;
            editor.FontFamily = new FontFamily(options.Family);
            editor.WordWrap = options.WordWrap;
            editor.Background = Frozen(options.Dark ? "#202124" : "#FFFFFF");
            editor.Foreground = foreground;
            editor.CaretBrush = foreground;
            editor.TextArea.SelectionBrush = Frozen(options.Dark ? "#80577CBA" : "#806CA7E8");
            session = MarkdownLivePreview.Attach(editor, new MarkdownLivePreview.Settings {
                FontSize=options.FontSize, Family=options.Family, LinePercent=options.LinePercent,
                Dark=options.Dark, WordWrap=options.WordWrap, BaseDirectory=options.BaseDirectory
            });
        }
        public void Dispose() {
            if (disposed) return;
            disposed = true;
            session.Dispose();
            editor.FontSize = oldSize;
            editor.FontFamily = oldFamily;
            editor.WordWrap = oldWrap;
            editor.Background = oldBackground;
            editor.Foreground = oldForeground;
            editor.CaretBrush = oldCaret;
            editor.TextArea.SelectionBrush = oldSelection;
        }
    }
    static Brush Frozen(string text) {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromInvariantString(text);
        brush.Freeze();
        return brush;
    }

    internal static int RunWindow(string file) {
        var app = new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
        var options = new Options { BaseDirectory=Path.GetDirectoryName(Path.GetFullPath(file)) };
        // Generated text only; this comparison precedes installation on the real editor.
        string heights = ProbeHeights(options);
        var editor = new SourceEditor {
            FontFamily=new FontFamily(options.Family), FontSize=options.FontSize,
            WordWrap=options.WordWrap
        };
        // Configure default metrics while the document is still empty: this fork
        // traverses its height tree when DefaultLineHeight changes.
        editor.TextArea.TextView.LineHeight = double.NaN;
        editor.Text = File.ReadAllText(file, Encoding.UTF8);
        editor.Document.UndoStack.SizeLimit = int.MaxValue;
        IDisposable handle = Attach(editor, options);
        var window = new Window {
            Title="MDNote live preview spike — " + Path.GetFileName(file) + " | " + heights,
            Width=1000, Height=800, Content=editor
        };
        window.PreviewKeyDown += delegate(object sender, KeyEventArgs e) {
            if (e.Key != Key.F2) return;
            handle.Dispose();
            options.Dark = !options.Dark;
            handle = Attach(editor, options);
            e.Handled = true;
        };
        window.Closed += delegate { handle.Dispose(); };
        return app.Run(window);
    }

    static string ProbeHeights(Options options) {
        Window host = null;
        SourceEditor probe = null;
        HeightProbeTransformer transformer = null;
        Application app = Application.Current;
        Window oldMain = app == null ? null : app.MainWindow;
        ShutdownMode oldShutdown = app == null ? ShutdownMode.OnExplicitShutdown : app.ShutdownMode;
        try {
            // Showing the first temporary window must not shut down RunWindow's Application.
            if (app != null) app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            probe = new SourceEditor {
                FontFamily=new FontFamily(options.Family), FontSize=options.FontSize,
                WordWrap=false, Text="# Heading Ag 한글 😀\nNormal Ag 한글 😀\n"
            };
            host = new Window {
                Left=-20000, Top=0, Width=900, Height=400, Content=probe,
                ShowInTaskbar=false, ShowActivated=false, WindowStyle=WindowStyle.None
            };
            transformer = new HeightProbeTransformer(options.FontSize);
            TextView view = probe.TextArea.TextView;
            view.LineTransformers.Add(transformer);
            view.LineHeight = options.FontSize * options.LinePercent / 100;
            host.Show();
            LayoutProbe(host, probe);
            VisualLine heading = view.GetVisualLine(1), body = view.GetVisualLine(2);
            double fixedHeading = heading == null ? double.NaN : heading.Height;
            double fixedBody = body == null ? double.NaN : body.Height;
            double ink = heading == null || heading.TextLines.Count == 0 ? double.NaN : heading.TextLines[0].Extent;
            view.LineHeight = double.NaN;
            LayoutProbe(host, probe);
            heading = view.GetVisualLine(1);
            body = view.GetVisualLine(2);
            double naturalHeading = heading == null ? double.NaN : heading.Height;
            double naturalBody = body == null ? double.NaN : body.Height;
            string comparison = double.IsNaN(fixedHeading) || double.IsNaN(naturalHeading) ? "n/a" :
                fixedHeading + 0.1 < naturalHeading ? "fixed compresses heading" : "fixed fits heading";
            string result = string.Format(CultureInfo.InvariantCulture,
                "height probe fixed H/body={0}/{1}, H ink={2}; natural={3}/{4} ({5})",
                ProbeNumber(fixedHeading), ProbeNumber(fixedBody), ProbeNumber(ink),
                ProbeNumber(naturalHeading), ProbeNumber(naturalBody), comparison);
            System.Diagnostics.Trace.WriteLine(result);
            return result;
        } catch (Exception ex) {
            return "height probe unavailable: " + ex.GetType().Name;
        } finally {
            if (probe != null && transformer != null) probe.TextArea.TextView.LineTransformers.Remove(transformer);
            if (host != null) host.Close();
            if (app != null) { app.MainWindow = oldMain; app.ShutdownMode = oldShutdown; }
        }
    }

    static string ProbeNumber(double value) {
        return double.IsNaN(value) ? "n/a" : value.ToString("0.##", CultureInfo.InvariantCulture);
    }
    static void LayoutProbe(Window host, SourceEditor probe) {
        host.ApplyTemplate();
        probe.ApplyTemplate();
        probe.TextArea.ApplyTemplate();
        host.UpdateLayout();
        probe.UpdateLayout();
        probe.TextArea.TextView.EnsureVisualLines();
    }

    sealed class HeightProbeTransformer : DocumentColorizingTransformer {
        readonly double size;
        internal HeightProbeTransformer(double size) { this.size = size; }
        protected override void ColorizeLine(DocumentLine line) {
            if (line.LineNumber != 1) return;
            ChangeLinePart(line.Offset, line.EndOffset, delegate(VisualLineElement e) {
                e.TextRunProperties.SetFontRenderingEmSize(size * 2);
                Typeface old = e.TextRunProperties.Typeface;
                e.TextRunProperties.SetTypeface(new Typeface(old.FontFamily, old.Style, FontWeights.Bold, old.Stretch));
            });
        }
    }
}
}
