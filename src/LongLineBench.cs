using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace MDNote {
// Research only: separates the single-long-line input cost of plain AvalonEdit
// from the live-preview layer. Generated text only; no user file is read.
internal static class LongLineBench {
    static readonly string[] Words = {
        "기억이", "바람은", "노트를", "새벽에", "골목의", "편지가", "창문을", "지도는", "문장이", "커피가",
        "quiet", "morning", "river", "station", "notebook", "draft", "window", "light", "street", "page"
    };

    sealed class Config {
        internal string Name;
        internal bool Wrap = true;
        internal double LineHeight = 24;
        internal bool Live;
        internal int Chunk;
    }

    // Splits one long VisualLineText into bounded text runs without consuming text.
    sealed class ChunkGenerator : VisualLineElementGenerator {
        readonly int size;
        internal ChunkGenerator(int size) { this.size = size; }
        public override int GetFirstInterestedOffset(int startOffset) {
            DocumentLine line = CurrentContext.VisualLine.FirstDocumentLine;
            int relative = startOffset - line.Offset;
            int next = line.Offset + (relative / size + 1) * size;
            return next < line.EndOffset ? next : -1;
        }
        public override VisualLineElement ConstructElement(int offset) { return null; }
    }

    internal static int Run(string outputDirectory) {
        string outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        bool quick = outDir.TrimEnd('\\', '/').EndsWith("-quick", StringComparison.OrdinalIgnoreCase);
        var app = Application.Current ?? new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };
        var report = new StringBuilder();
        report.Append("# LongLineBench\n\n- Timestamp: ").Append(DateTime.Now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        report.Append("- Middle-of-line typing, 5 warmup + 20 samples, render frame + ContextIdle. Window 1000x800 offscreen.\n\n");
        report.Append("| Config | Line chars | Open ms | Type p50 | Type p95 | Type max | Rows | Status |\n|---|---:|---:|---:|---:|---:|---:|---|\n");
        int[] lengths = quick ? new[] { 2000, 10000 } : new[] { 10000, 25000, 50000, 100000, 200000 };
        var configs = new List<Config> {
            new Config { Name="plain-wrap-fixed" },
            new Config { Name="plain-wrap-nan", LineHeight=double.NaN },
            new Config { Name="plain-nowrap", Wrap=false },
            new Config { Name="plain-wrap-chunk1000", Chunk=1000 },
            new Config { Name="live-wrap", Live=true },
        };
        int exit = 0;
        foreach (Config config in configs) {
            foreach (int length in lengths) {
                if (!config.Wrap && length > 100000 && !quick) { report.Append("| ").Append(config.Name).Append(" | ").Append(length).Append(" | - | - | - | - | - | SKIP |\n"); continue; }
                try { report.Append(Measure(config, length)); }
                catch (Exception ex) { exit = 1; report.Append("| ").Append(config.Name).Append(" | ").Append(length).Append(" | - | - | - | - | - | FAIL ").Append(ex.GetType().Name).Append(": ").Append(ex.Message.Replace("|", "/")).Append(" |\n"); }
            }
        }
        report.Append('\n').Append(MeasureAttach(quick));
        File.WriteAllText(Path.Combine(outDir, "longline.md"), report.ToString(), new UTF8Encoding(false));
        Console.WriteLine(report.ToString());
        return exit;
    }

    static string Generate(int length, int seed) {
        var random = new Random(seed);
        var sb = new StringBuilder(length + 32);
        while (sb.Length < length) {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Words[random.Next(Words.Length)]);
            if (random.Next(12) == 0) sb.Append('.');
        }
        sb.Length = length;
        return sb.ToString();
    }

    static string Measure(Config config, int length) {
        string text = Generate(length, 4001);
        var editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=config.Wrap };
        editor.TextArea.TextView.LineHeight = config.LineHeight;
        IDisposable live = null;
        if (config.Live) live = LivePreviewSpike.Attach(editor, new LivePreviewSpike.Options { WordWrap=config.Wrap });
        if (config.Chunk > 0) editor.TextArea.TextView.ElementGenerators.Add(new ChunkGenerator(config.Chunk));
        var window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false, ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
        try {
            window.Show();
            Idle();
            double open = Time(delegate { editor.Text = text; }, editor.TextArea.TextView, DispatcherPriority.ApplicationIdle);
            editor.Select(length / 2, 0);
            editor.TextArea.Caret.BringCaretToView();
            Time(delegate { }, editor.TextArea.TextView, DispatcherPriority.ContextIdle);
            for (int i = 0; i < 5; i++) Time(delegate { editor.TextArea.PerformTextInput("가"); }, editor.TextArea.TextView, DispatcherPriority.ContextIdle);
            var samples = new List<double>();
            for (int i = 0; i < 20; i++) samples.Add(Time(delegate { editor.TextArea.PerformTextInput("가"); }, editor.TextArea.TextView, DispatcherPriority.ContextIdle));
            int rows = 0;
            VisualLine line = editor.TextArea.TextView.GetVisualLine(1);
            if (line != null) rows = line.TextLines.Count;
            bool ok = editor.Document.TextLength == length + 25;
            samples.Sort();
            return string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2:0.0} | {3:0.0} | {4:0.0} | {5:0.0} | {6} | {7} |\n",
                config.Name, length, open, Percentile(samples, 0.5), Percentile(samples, 0.95), samples[samples.Count - 1], rows, ok ? "PASS" : "FAIL length");
        } finally {
            if (live != null) live.Dispose();
            window.Close();
            Idle();
        }
    }

    // Attach cost after a large document is already loaded (HeightTree default-height traversal).
    static string MeasureAttach(bool quick) {
        int lines = quick ? 3000 : 300000;
        var sb = new StringBuilder(lines * 40);
        var random = new Random(4002);
        for (int i = 0; i < lines; i++) {
            if (i % 50 == 0) sb.Append("## 제목 ").Append(i).Append('\n');
            else sb.Append(Words[random.Next(Words.Length)]).Append(' ').Append(Words[random.Next(Words.Length)]).Append(" **굵게** 문장 ").Append(i).Append('\n');
        }
        var result = new StringBuilder("| Attach case | Lines | Attach ms | Detach ms |\n|---|---:|---:|---:|\n");
        foreach (double before in new[] { 24.0, double.NaN }) {
            var editor = new SourceEditor { FontFamily=new FontFamily("Malgun Gothic"), FontSize=16, WordWrap=true };
            editor.TextArea.TextView.LineHeight = before;
            var window = new Window { Left=-20000, Top=0, Width=1000, Height=800, ShowInTaskbar=false, ShowActivated=false, WindowStartupLocation=WindowStartupLocation.Manual, Content=editor };
            try {
                window.Show();
                Idle();
                Time(delegate { editor.Text = sb.ToString(); }, editor.TextArea.TextView, DispatcherPriority.ApplicationIdle);
                IDisposable live = null;
                double attach = Time(delegate { live = LivePreviewSpike.Attach(editor, new LivePreviewSpike.Options()); }, editor.TextArea.TextView, DispatcherPriority.ContextIdle);
                double detach = Time(delegate { live.Dispose(); }, editor.TextArea.TextView, DispatcherPriority.ContextIdle);
                result.Append(string.Format(CultureInfo.InvariantCulture, "| LineHeight before={0} | {1} | {2:0.0} | {3:0.0} |\n",
                    double.IsNaN(before) ? "NaN" : before.ToString(CultureInfo.InvariantCulture), editor.Document.LineCount, attach, detach));
            } finally {
                window.Close();
                Idle();
            }
        }
        return result.ToString();
    }

    internal static double Percentile(List<double> sorted, double p) {
        int index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
    }

    internal static double Time(Action action, UIElement view, DispatcherPriority idle) {
        view.InvalidateVisual();
        var watch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        bool seen = false;
        EventHandler rendering = null;
        rendering = delegate {
            if (seen) return;
            seen = true;
            CompositionTarget.Rendering -= rendering;
            Dispatcher.CurrentDispatcher.BeginInvoke(idle, new Action(delegate { frame.Continue = false; }));
        };
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval=TimeSpan.FromSeconds(60) };
        timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
        CompositionTarget.Rendering += rendering;
        timer.Start();
        try {
            action();
            Dispatcher.PushFrame(frame);
        } finally {
            timer.Stop();
            CompositionTarget.Rendering -= rendering;
        }
        return watch.Elapsed.TotalMilliseconds;
    }

    internal static void Idle() {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(delegate { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
    }
}
}
